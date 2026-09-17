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

/// <summary>What polling <c>&lt;data&gt;/pair.request</c> found.</summary>
public enum PairRequestPickup
{
    /// <summary>No sentinel file was there.</summary>
    None,

    /// <summary>A sentinel young enough to be a live request. Open a window for it.</summary>
    Fresh,

    /// <summary>
    /// A sentinel older than any asker still waiting on it — already answered by an earlier poll
    /// and left behind, or dropped by a process that crashed before it could clean up after
    /// itself. Consumed (deleted) the same as a fresh one, but it must not open a window: that
    /// would be a pairing window nobody is watching (AUDIT2.md § 3.13).
    /// </summary>
    Stale,
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

    /// <summary>
    /// How old <c>pair.request</c> may be and still be treated as a live ask. The tray waits up to
    /// 8 s for its own request to be answered (<c>PairingChannel.Timeout</c>) and this server polls
    /// once a second, so anything left after several times that margin was not written by an asker
    /// still watching for the answer — it was already served by an earlier poll (the ordinary case;
    /// the file should already be gone by then) or left behind by a process that never got to clean
    /// up after itself, and opening a window for it now would be a window nobody is at the screen
    /// for (AUDIT2.md § 3.13).
    /// </summary>
    public static readonly TimeSpan MaxPairRequestAge = TimeSpan.FromSeconds(30);

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

    /// <summary>Opens a window, replacing any window already open, and publishes the offer.</summary>
    public PairingOffer OpenWindow(DateTimeOffset now)
    {
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        var expiresAt = now.Add(_options.PairingWindow);
        var window = new Window(HashCode(code), now, expiresAt);

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
    /// Redeems a code. <paramref name="sourceAddress"/> is the caller's own address key (the same
    /// key <see cref="PairingRateLimiter.KeyFor"/> produces): SERVER_SPEC.md § 10.11 gives every
    /// address its own five-guess budget against the one open window, so a stranger who spends their
    /// own five cannot touch anyone else's count, and the owner's own five tries are never at risk of
    /// being burned by somebody else's guessing. An address that exhausts its own budget is refused
    /// forever after — for that address only; the window and every other address's budget stand. The
    /// per-minute rate limit is charged by the caller, before this runs, so it applies even to a
    /// caller who is only probing for whether pairing is open.
    /// </summary>
    public PairAttemptResult Redeem(string? suppliedCode, string? deviceName, string sourceAddress, DateTimeOffset now)
    {
        var addressKey = string.IsNullOrEmpty(sourceAddress) ? "unknown" : sourceAddress;

        lock (_gate)
        {
            var window = _window;

            if (window is null || now > window.ExpiresAt.Add(Grace))
                return new PairAttemptResult(PairOutcome.NotOpen);

            var remainingForAddress = window.AttemptsRemaining(addressKey, DefaultAttempts);

            if (window.Consumed || now > window.ExpiresAt || remainingForAddress <= 0)
            {
                // Spent (by anyone), expired, or this address's own budget is already gone: the
                // caller learns the code is no good, not whether it was ever right.
                return new PairAttemptResult(PairOutcome.InvalidCode, AttemptsRemaining: Math.Max(0, remainingForAddress));
            }

            var normalised = Normalise(suppliedCode);
            var matches = CryptographicOperations.FixedTimeEquals(HashCode(normalised), window.CodeHash);

            if (!matches)
            {
                var remaining = remainingForAddress - 1;
                window.SetAttemptsRemaining(addressKey, remaining);

                // AUDIT2.md § 3.3 / § 3.2: this address's own budget is what makes six digits safe
                // against it. Once it is gone the window is dead for this address only — every
                // other address (in particular the owner's own devices) keeps its own five and the
                // code itself still redeems (SERVER_SPEC.md § 10.11's per-address fix). The offer
                // file MUST NOT come down here: it used to, which let a single stranger repeat this
                // forever and delete the owner's own QR/code out from under him every time, and it
                // is also what the tray was reading "gone" as "attacked" from on the one occasion
                // the file legitimately disappears for a happy reason (§ 3.2) — a successful pair,
                // below. Only a successful redemption or an explicit close ever removes the file now.

                return new PairAttemptResult(PairOutcome.InvalidCode, AttemptsRemaining: Math.Max(0, remaining));
            }

            var expiresAt = _options.TokenLifetimeDays is { } days && days > 0
                ? now.AddDays(days)
                : (DateTimeOffset?)null;

            // A32: persist before marking the window consumed, not after. TokenStore.Issue writes
            // devices.json itself; if that throws (a full disk, a permissions problem), the window
            // must stay valid — the caller gets 500 internal_error from the transport's generic
            // handler and can simply try the same code again, rather than finding the window spent
            // for a token that was never actually issued.
            var issued = _tokens.Issue(deviceName, now, expiresAt);

            // Single use: only now does the window die, still inside the same lock that matched it,
            // so two concurrent requests with the right code cannot both be issued a token.
            _window = window with { Consumed = true };

            TryDelete(OfferFilePath);
            return new PairAttemptResult(PairOutcome.Paired, issued);
        }
    }

    private int DefaultAttempts => Math.Max(1, _options.PairingWindowAttempts);

    public int? ChargeRateLimit(string key, DateTimeOffset now) => _limiter.TryAttempt(key, now);

    /// <summary>
    /// Polls for the out-of-band open request. A file, not a socket: the only principal that can
    /// create it is one already running as the owner's user account.
    /// <para/>
    /// Consumed (deleted) whenever it is found, fresh or stale alike — a spent or abandoned request
    /// must never survive to be picked up again by a later poll, possibly long after this one, with
    /// nobody watching (AUDIT2.md § 3.13). Only a fresh one tells the caller to actually open a
    /// window; see <see cref="PairRequestPickup"/>.
    /// </summary>
    public PairRequestPickup ConsumePairRequestFile(DateTimeOffset now)
    {
        try
        {
            if (!File.Exists(RequestFilePath)) return PairRequestPickup.None;

            // Age first, delete second: whichever of "read the age" and "the writer's own 8 s
            // give-up" loses the race just means both sides agree the request is spent, which is
            // the safe direction either way.
            var age = now - new DateTimeOffset(File.GetLastWriteTimeUtc(RequestFilePath), TimeSpan.Zero);
            File.Delete(RequestFilePath);

            return age <= MaxPairRequestAge ? PairRequestPickup.Fresh : PairRequestPickup.Stale;
        }
        catch (IOException)
        {
            return PairRequestPickup.None;
        }
        catch (UnauthorizedAccessException)
        {
            return PairRequestPickup.None;
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

    /// <summary>
    /// One open window. <see cref="_attemptsBySource"/> tracks each caller's own remaining guesses
    /// separately (SERVER_SPEC.md § 10.11); it is a mutable dictionary on an otherwise-immutable
    /// record because every access to it happens inside <see cref="PairingService._gate"/> already —
    /// there is no concurrency to protect it from, only bookkeeping to keep off the record's own
    /// identity, since replacing the window (<c>with</c>) must not reset the counts.
    /// </summary>
    private sealed record Window(byte[] CodeHash, DateTimeOffset OpenedAt, DateTimeOffset ExpiresAt)
    {
        private readonly Dictionary<string, int> _attemptsBySource = new(StringComparer.Ordinal);

        public bool Consumed { get; init; }

        public int AttemptsRemaining(string address, int defaultAttempts) =>
            _attemptsBySource.TryGetValue(address, out var remaining) ? remaining : defaultAttempts;

        public void SetAttemptsRemaining(string address, int remaining) =>
            _attemptsBySource[address] = Math.Max(0, remaining);
    }
}
