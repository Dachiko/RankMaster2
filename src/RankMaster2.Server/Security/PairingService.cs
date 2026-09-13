using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RankMaster2.Server.Security;

public enum PairOutcome
{
    Paired,
    NotOpen,
    InvalidCode,
}

public sealed record PairAttemptResult(
    PairOutcome Outcome,
    IssuedToken? Issued = null,
    int AttemptsRemaining = 0);

/// <summary>
/// What <c>rm2ctl pair</c> renders: the human code and the QR payload.
/// </summary>
public sealed record PairingOffer(
    string Code,
    string CodeDisplay,
    string Payload,
    DateTimeOffset ExpiresAt);

/// <summary>
/// The pairing window.
///
/// <para><b>Opening a window is deliberately not an HTTP operation.</b> If it were, anyone on the
/// LAN could ask the server to start accepting pairings and then brute force the code at their
/// leisure. It is opened out of band by something already running as the owner: automatically on a
/// fresh install with no enrolled device, or by dropping a <c>pair.request</c> file into the data
/// directory — a directory only the owner's account can write.</para>
///
/// <para><b>The pairing payload format</b> (defined here; this is the string <c>rm2ctl</c> renders
/// as a QR code):</para>
/// <code>
/// rm2://pair?v=1&amp;host=192.168.1.42&amp;port=18611&amp;fp=&lt;64 lowercase hex&gt;&amp;code=418250&amp;exp=1789000000
/// </code>
/// <list type="bullet">
/// <item><c>v</c> — payload version, currently <c>1</c>. A client MUST refuse a version it does not know.</item>
/// <item><c>host</c>, <c>port</c> — where to connect; the base URL is <c>https://host:port/api/v1</c>.</item>
/// <item><c>fp</c> — SHA-256 of the certificate's DER encoding, 64 lowercase hex, no <c>sha256:</c>
///   prefix (the prefix is present on the wire in <c>/ping</c>, and omitted here only to keep the QR
///   small). <b>The client MUST pin this before sending the code.</b> The code is a bearer secret;
///   handing it to an unverified TLS peer hands it to whoever answered.</item>
/// <item><c>code</c> — the six digits, unspaced.</item>
/// <item><c>exp</c> — unix seconds at which the window closes.</item>
/// </list>
/// <para>The payload carries a live credential, so it is short-lived by construction and is written
/// to <c>&lt;data&gt;/pairing.json</c> with owner-only permissions, never logged in full.</para>
/// </summary>
public sealed class PairingService
{
    private const string OfferFileName = "pairing.json";
    private const string RequestFileName = "pair.request";

    /// <summary>
    /// How long a spent or expired window is remembered. Inside this grace period a stale code
    /// answers <c>401 invalid_pairing_code</c> as SERVER_SPEC.md § 10.11 requires; outside it, the
    /// honest answer is <c>403 pairing_not_open</c>.
    /// </summary>
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(10);

    private readonly object _gate = new();
    private readonly Rm2SecurityOptions _options;
    private readonly TokenStore _tokens;
    private readonly PairingRateLimiter _limiter;
    private readonly string _dataDirectory;
    private readonly string _fingerprint;
    private readonly string _host;
    private readonly int _port;

    private Window? _window;

    public PairingService(
        Rm2SecurityOptions options,
        TokenStore tokens,
        PairingRateLimiter limiter,
        string dataDirectory,
        string fingerprint,
        string host,
        int port)
    {
        _options = options;
        _tokens = tokens;
        _limiter = limiter;
        _dataDirectory = dataDirectory;
        _fingerprint = fingerprint;
        _host = host;
        _port = port;
    }

    public string RequestFilePath => Path.Combine(_dataDirectory, RequestFileName);
    public string OfferFilePath => Path.Combine(_dataDirectory, OfferFileName);

    public bool IsWindowOpen(DateTimeOffset now)
    {
        lock (_gate) return _window is { } w && w.IsOpen(now);
    }

    /// <summary>Opens a window, replacing any window already open, and publishes the offer.</summary>
    public PairingOffer OpenWindow(DateTimeOffset now)
    {
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        var expiresAt = now.Add(_options.PairingWindow);
        var window = new Window(
            CodeHash: HashCode(code),
            OpenedAt: now,
            ExpiresAt: expiresAt,
            AttemptsRemaining: Math.Max(1, _options.PairingWindowAttempts));

        lock (_gate) _window = window;

        var offer = new PairingOffer(
            code,
            code[..3] + " " + code[3..],
            BuildPayload(code, expiresAt),
            expiresAt);

        PublishOffer(offer);
        return offer;
    }

    public void CloseWindow()
    {
        lock (_gate) _window = null;
        TryDelete(OfferFilePath);
    }

    /// <summary>
    /// Redeems a code. <paramref name="rateLimitKey"/> is the source address bucket; the limit is
    /// charged before anything about the window is revealed, so the limit applies to attackers who
    /// are only probing for whether pairing is open.
    /// </summary>
    public PairAttemptResult Redeem(string? suppliedCode, string? deviceName, DateTimeOffset now)
    {
        lock (_gate)
        {
            var window = _window;

            if (window is null || now > window.ExpiresAt.Add(Grace))
                return new PairAttemptResult(PairOutcome.NotOpen);

            if (!window.IsOpen(now))
            {
                // Spent, exhausted or expired but still remembered: the caller learns the code is
                // no good, not whether it was ever right.
                return new PairAttemptResult(PairOutcome.InvalidCode, AttemptsRemaining: 0);
            }

            var normalised = Normalise(suppliedCode);
            var matches = CryptographicOperations.FixedTimeEquals(HashCode(normalised), window.CodeHash);

            if (!matches)
            {
                var remaining = window.AttemptsRemaining - 1;
                _window = window with { AttemptsRemaining = remaining };
                if (remaining <= 0)
                {
                    // The budget is what makes six digits safe. Once it is gone the window is dead;
                    // the owner opens a new one.
                    _window = window with { AttemptsRemaining = 0, Consumed = true };
                    TryDelete(OfferFilePath);
                }

                return new PairAttemptResult(PairOutcome.InvalidCode, AttemptsRemaining: Math.Max(0, remaining));
            }

            // Single use: the window dies here, inside the same lock that matched it, so two
            // concurrent requests with the right code cannot both be issued a token.
            _window = window with { Consumed = true, AttemptsRemaining = 0 };

            var expiresAt = _options.TokenLifetimeDays is { } days && days > 0
                ? now.AddDays(days)
                : (DateTimeOffset?)null;

            var issued = _tokens.Issue(deviceName, now, expiresAt);
            TryDelete(OfferFilePath);
            return new PairAttemptResult(PairOutcome.Paired, issued);
        }
    }

    public int? ChargeRateLimit(string key, DateTimeOffset now) => _limiter.TryAttempt(key, now);

    /// <summary>
    /// Polls for the out-of-band open request. A file, not a socket: the only principal that can
    /// create it is one already running as the owner's user account.
    /// </summary>
    public bool ConsumePairRequestFile()
    {
        try
        {
            if (!File.Exists(RequestFilePath)) return false;
            File.Delete(RequestFilePath);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal string BuildPayload(string code, DateTimeOffset expiresAt)
    {
        var builder = new StringBuilder("rm2://pair?v=1");
        builder.Append("&host=").Append(Uri.EscapeDataString(_host));
        builder.Append("&port=").Append(_port.ToString(CultureInfo.InvariantCulture));
        builder.Append("&fp=").Append(_fingerprint);
        builder.Append("&code=").Append(code);
        builder.Append("&exp=").Append(expiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        return builder.ToString();
    }

    private void PublishOffer(PairingOffer offer)
    {
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = 1,
                code = offer.Code,
                codeDisplay = offer.CodeDisplay,
                payload = offer.Payload,
                host = _host,
                port = _port,
                certificateFingerprint = "sha256:" + _fingerprint,
                expiresAt = offer.ExpiresAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            }, new JsonSerializerOptions { WriteIndented = true });

            DataDirectory.WriteAllBytesAtomic(OfferFilePath, json);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The console still carries the code; a missing offer file is an inconvenience, not a
            // reason to refuse to pair.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// SERVER_SPEC.md § 10.11: whitespace is ignored. Hashing before comparing keeps the comparison
    /// constant-time <i>and</i> constant-length — <see cref="CryptographicOperations.FixedTimeEquals"/>
    /// on the raw strings would return early on a length mismatch and leak the code's length.
    /// </summary>
    private static string Normalise(string? code)
    {
        if (string.IsNullOrEmpty(code)) return "";
        var builder = new StringBuilder(code.Length);
        foreach (var c in code)
        {
            if (!char.IsWhiteSpace(c)) builder.Append(c);
        }

        return builder.ToString();
    }

    private static byte[] HashCode(string code) => SHA256.HashData(Encoding.UTF8.GetBytes(code));

    private sealed record Window(
        byte[] CodeHash,
        DateTimeOffset OpenedAt,
        DateTimeOffset ExpiresAt,
        int AttemptsRemaining,
        bool Consumed = false)
    {
        public bool IsOpen(DateTimeOffset now) =>
            !Consumed && AttemptsRemaining > 0 && now <= ExpiresAt;
    }
}
