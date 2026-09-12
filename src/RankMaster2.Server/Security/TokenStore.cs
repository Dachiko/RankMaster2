using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RankMaster2.Server.Security;

public enum TokenStatus
{
    Valid,
    Malformed,
    Unknown,
    Expired,
    Revoked,
}

public sealed record TokenCheck(TokenStatus Status, DeviceRecord? Device)
{
    public static readonly TokenCheck Malformed = new(TokenStatus.Malformed, null);
    public static readonly TokenCheck Unknown = new(TokenStatus.Unknown, null);
}

public sealed record DeviceRecord(
    string DeviceId,
    string? DeviceName,
    DateTimeOffset IssuedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt)
{
    public bool IsRevoked => RevokedAt is not null;
}

public sealed record IssuedToken(DeviceRecord Device, string Token);

/// <summary>
/// The device token store.
///
/// <para>
/// A token is <c>rm2_&lt;tokenId&gt;.&lt;secret&gt;</c>: 128 bits of public lookup id and 256 bits of
/// secret, both base64url. Only the id is stored in the clear. The secret is stored as
/// <c>SHA-256(salt ‖ secret)</c>, so the file on disk cannot be replayed as a credential even by
/// someone who can read it — which matters because the same file survives backups and sync clients.
/// A plain hash with a per-device salt is the right primitive here rather than a password KDF: the
/// secret is 256 bits of CSPRNG output, so there is no dictionary to run and no work factor worth
/// paying on every single request.
/// </para>
/// <para>
/// Verification is constant-time in the secret (SERVER_SPEC.md § 3): the id lookup is a plain
/// dictionary hit because the id is not a secret, and an id that is not present still pays for a
/// full comparison against a decoy so that "no such device" and "wrong secret" take the same time.
/// </para>
/// </summary>
public sealed class TokenStore
{
    private const string FileName = "devices.json";
    private const string Prefix = "rm2_";
    private const int TokenIdBytes = 16;
    private const int SecretBytes = 32;
    private const int SaltBytes = 16;

    private readonly object _gate = new();
    private readonly string _path;
    private readonly Dictionary<string, Entry> _byDeviceId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entry> _byTokenId = new(StringComparer.Ordinal);
    private readonly byte[] _decoySalt = RandomNumberGenerator.GetBytes(SaltBytes);
    private readonly byte[] _decoyHash = RandomNumberGenerator.GetBytes(32);

    private TokenStore(string path) => _path = path;

    public static TokenStore Open(string dataDirectory)
    {
        DataDirectory.Ensure(dataDirectory);
        var store = new TokenStore(Path.Combine(dataDirectory, FileName));
        store.Load();
        return store;
    }

    /// <summary>Devices that could authenticate right now. Drives first-run pairing.</summary>
    public int ActiveDeviceCount
    {
        get
        {
            lock (_gate)
            {
                var now = DateTimeOffset.UtcNow;
                return _byDeviceId.Values.Count(e =>
                    e.Device.RevokedAt is null && (e.Device.ExpiresAt is null || e.Device.ExpiresAt > now));
            }
        }
    }

    public IReadOnlyList<DeviceRecord> Devices
    {
        get
        {
            lock (_gate) return _byDeviceId.Values.Select(e => e.Device).ToList();
        }
    }

    public IssuedToken Issue(string? deviceName, DateTimeOffset issuedAt, DateTimeOffset? expiresAt)
    {
        var tokenIdBytes = RandomNumberGenerator.GetBytes(TokenIdBytes);
        var secretBytes = RandomNumberGenerator.GetBytes(SecretBytes);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);

        var tokenId = Base64Url.Encode(tokenIdBytes);
        var secret = Base64Url.Encode(secretBytes);
        var hash = HashSecret(salt, secretBytes);

        var deviceId = "dev_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var device = new DeviceRecord(deviceId, Trim(deviceName), issuedAt, expiresAt, null);
        var entry = new Entry(device, tokenId, salt, hash);

        lock (_gate)
        {
            _byDeviceId[deviceId] = entry;
            _byTokenId[tokenId] = entry;
            Persist();
        }

        CryptographicOperations.ZeroMemory(secretBytes);
        return new IssuedToken(device, Prefix + tokenId + "." + secret);
    }

    /// <summary>
    /// Checks a bearer token. Never throws on hostile input and never allocates unboundedly: a
    /// token longer than any token this server issues is rejected before anything is decoded.
    /// </summary>
    public TokenCheck Check(string? presented, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(presented) || presented.Length > 256 || !presented.StartsWith(Prefix, StringComparison.Ordinal))
            return TokenCheck.Malformed;

        var body = presented.AsSpan(Prefix.Length);
        var separator = body.IndexOf('.');
        if (separator <= 0 || separator == body.Length - 1)
            return TokenCheck.Malformed;

        var tokenId = body[..separator].ToString();
        var secretText = body[(separator + 1)..].ToString();

        if (!Base64Url.TryDecode(tokenId, TokenIdBytes, out _) ||
            !Base64Url.TryDecode(secretText, SecretBytes, out var secretBytes))
        {
            return TokenCheck.Malformed;
        }

        try
        {
            Entry? entry;
            lock (_gate) _byTokenId.TryGetValue(tokenId, out entry);

            // Unknown id still pays for a full hash and comparison, so timing cannot be used to
            // decide whether an id exists before guessing its secret.
            var salt = entry?.Salt ?? _decoySalt;
            var expected = entry?.SecretHash ?? _decoyHash;
            var actual = HashSecret(salt, secretBytes);
            var matches = CryptographicOperations.FixedTimeEquals(actual, expected);

            if (entry is null || !matches)
                return TokenCheck.Unknown;

            // Order matters: the secret is proven before anything about the device is disclosed, so
            // "revoked" can never be learned by guessing device ids.
            if (entry.Device.RevokedAt is not null)
                return new TokenCheck(TokenStatus.Revoked, entry.Device);

            if (entry.Device.ExpiresAt is { } expiry && expiry <= now)
                return new TokenCheck(TokenStatus.Expired, entry.Device);

            return new TokenCheck(TokenStatus.Valid, entry.Device);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secretBytes);
        }
    }

    /// <summary>True when the device existed; false is the caller's <c>404 not_found</c>.</summary>
    public bool Revoke(string deviceId, DateTimeOffset at)
    {
        if (string.IsNullOrEmpty(deviceId)) return false;

        lock (_gate)
        {
            if (!_byDeviceId.TryGetValue(deviceId, out var entry))
                return false;

            if (entry.Device.RevokedAt is null)
            {
                var revoked = entry with { Device = entry.Device with { RevokedAt = at } };
                _byDeviceId[deviceId] = revoked;
                _byTokenId[revoked.TokenId] = revoked;
                Persist();
            }

            return true;
        }
    }

    private static byte[] HashSecret(byte[] salt, byte[] secret)
    {
        using var sha = SHA256.Create();
        sha.TransformBlock(salt, 0, salt.Length, null, 0);
        sha.TransformFinalBlock(secret, 0, secret.Length);
        return sha.Hash!;
    }

    private static string? Trim(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var trimmed = name.Trim();
        return trimmed.Length > 64 ? trimmed[..64] : trimmed;
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;

        try
        {
            var json = File.ReadAllBytes(_path);
            var dto = JsonSerializer.Deserialize<StoreDto>(json, SerializerOptions);
            if (dto?.Devices is null) return;

            foreach (var d in dto.Devices)
            {
                if (string.IsNullOrEmpty(d.DeviceId) || string.IsNullOrEmpty(d.TokenId)) continue;
                if (!Base64Url.TryDecode(d.Salt ?? "", SaltBytes, out var salt)) continue;
                if (!Base64Url.TryDecode(d.SecretHash ?? "", 32, out var hash)) continue;

                var device = new DeviceRecord(d.DeviceId, d.DeviceName, d.IssuedAt, d.ExpiresAt, d.RevokedAt);
                var entry = new Entry(device, d.TokenId, salt, hash);
                _byDeviceId[d.DeviceId] = entry;
                _byTokenId[d.TokenId] = entry;
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            // A device file that will not parse must not brick the server, and must not silently
            // become an empty allow-list either: nothing is trusted, so every device re-pairs.
            _byDeviceId.Clear();
            _byTokenId.Clear();
        }
    }

    private void Persist()
    {
        var dto = new StoreDto
        {
            Version = 1,
            Devices = _byDeviceId.Values.Select(e => new DeviceDto
            {
                DeviceId = e.Device.DeviceId,
                DeviceName = e.Device.DeviceName,
                TokenId = e.TokenId,
                Salt = Base64Url.Encode(e.Salt),
                SecretHash = Base64Url.Encode(e.SecretHash),
                IssuedAt = e.Device.IssuedAt,
                ExpiresAt = e.Device.ExpiresAt,
                RevokedAt = e.Device.RevokedAt,
            }).ToList(),
        };

        DataDirectory.WriteAllBytesAtomic(_path, JsonSerializer.SerializeToUtf8Bytes(dto, SerializerOptions));
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record Entry(DeviceRecord Device, string TokenId, byte[] Salt, byte[] SecretHash);

    private sealed class StoreDto
    {
        public int Version { get; set; }
        public List<DeviceDto>? Devices { get; set; }
    }

    private sealed class DeviceDto
    {
        public string DeviceId { get; set; } = "";
        public string? DeviceName { get; set; }
        public string TokenId { get; set; } = "";
        public string? Salt { get; set; }
        public string? SecretHash { get; set; }
        public DateTimeOffset IssuedAt { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
        public DateTimeOffset? RevokedAt { get; set; }
    }
}

/// <summary>Base64url with no padding — URL-safe, and no '+' to be mangled by a form decoder.</summary>
internal static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool TryDecode(string text, int expectedBytes, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrEmpty(text) || text.Length > 512) return false;

        foreach (var c in text)
        {
            var ok = c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_';
            if (!ok) return false;
        }

        var builder = new StringBuilder(text.Length + 3);
        builder.Append(text.Replace('-', '+').Replace('_', '/'));
        while (builder.Length % 4 != 0) builder.Append('=');

        try
        {
            var decoded = Convert.FromBase64String(builder.ToString());
            if (decoded.Length != expectedBytes) return false;
            bytes = decoded;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
