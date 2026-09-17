using System.Text.Json;

namespace RankMaster2.Server.Tests.Harness;

public enum AuthMode
{
    /// <summary>A bearer token was obtained; authenticated calls carry it.</summary>
    Bearer,

    /// <summary>The server does not enforce auth yet, so the cycle can still be exercised.</summary>
    NotEnforced,

    /// <summary>Auth is enforced and no token could be obtained. Tests that need one fail, clearly.</summary>
    Unavailable
}

public sealed record TestCredentials(AuthMode Mode, string? Token, string Diagnostic);

/// <summary>
/// Getting a bearer token for the tests.
///
/// SERVER_SPEC.md § 10.1.1 settles how a pairing window opens, and it is deliberately not an HTTP
/// operation: an HTTP route would let anyone who can reach the port open a window and then spend the
/// day guessing at six digits. Opening one requires control of the owner's OS account — either the
/// server opens one itself at startup when no device is enrolled, or a sentinel file appears in its
/// data directory. The resulting offer, with the code, is written back to that directory.
///
/// So this points the server at a data directory of the suite's own, which starts with no device
/// enrolled, and reads the offer from it. The exact filenames are a wiring detail rather than
/// contract, so several are tried; if none of them yields a token, the failure says what was tried
/// rather than leaving a puzzling 401 behind.
/// </summary>
public static class TestAuth
{
    /// <summary>The code the harness seeds into configuration and then offers to POST /pair.</summary>
    public const string SeededPairingCode = "424242";

    /// <summary>
    /// Configuration keys the harness seeds with <see cref="SeededPairingCode"/> before the host
    /// starts. The contract names none of these; they are a wiring seam, not an assertion.
    /// </summary>
    public static readonly string[] PairingCodeConfigurationKeys =
    {
        "Rm2:Pairing:Code",
        "Rm2:Security:PairingCode",
        "RankMaster2:Pairing:Code",
        "Pairing:Code",
        "Security:PairingCode",
        "PairingCode",
        "RM2_PAIRING_CODE"
    };

    /// <summary>An explicit token, for running the suite against an already-paired live server.</summary>
    public const string TokenEnvironmentVariable = "RM2_TEST_TOKEN";

    /// <summary>An explicit pairing code, same purpose.</summary>
    public const string CodeEnvironmentVariable = "RM2_TEST_PAIRING_CODE";

    /// <summary>
    /// The file a server publishes an open pairing window through. `SERVER_SPEC.md` § 10.1.1 names
    /// exactly one, and it is an array only so the call site can keep iterating. The two guesses
    /// that used to sit beside it — `pair.offer`, `pairing.offer.json` — were never written by any
    /// server; C24 removed the same pair from `rm2ctl`, and this was the last copy.
    /// </summary>
    public static readonly string[] OfferFileNames = { "pairing.json" };

    /// <summary>Dropping this file in the data directory asks the server to open a window.</summary>
    public const string PairRequestFileName = "pair.request";

    public static async Task<TestCredentials> AcquireAsync(Rm2Client anonymous, string? dataDirectory = null)
    {
        var attempts = new List<string>();

        var supplied = Environment.GetEnvironmentVariable(TokenEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(supplied))
            return new TestCredentials(AuthMode.Bearer, supplied.Trim(),
                $"token supplied through {TokenEnvironmentVariable}");

        attempts.Add($"{TokenEnvironmentVariable} was not set");

        var codes = new List<string>();
        var suppliedCode = Environment.GetEnvironmentVariable(CodeEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(suppliedCode)) codes.Add(suppliedCode.Trim());

        if (dataDirectory is not null)
        {
            var offered = await ReadOfferedCodeAsync(dataDirectory, attempts);
            if (offered is not null) codes.Add(offered);
        }

        codes.Add(SeededPairingCode);

        foreach (var code in codes.Distinct())
        {
            Rm2Response response;
            try
            {
                response = await anonymous.PairAsync(code, "rm2 test harness");
            }
            catch (Exception ex)
            {
                attempts.Add($"POST /pair with '{code}' threw {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            if (response.StatusCode == 201 &&
                response.Json?.TryGetProperty("token", out var token) == true &&
                token.GetString() is { Length: > 0 } value)
            {
                return new TestCredentials(AuthMode.Bearer, value, $"paired with code '{code}'");
            }

            var code1 = response.Json?.TryGetProperty("error", out var error) == true &&
                        error.TryGetProperty("code", out var errorCode)
                ? errorCode.GetString()
                : null;

            attempts.Add($"POST /pair with '{code}' answered {response.StatusCode}" +
                         (code1 is null ? "" : $" ({code1})"));
        }

        // Nothing paired. Is auth even switched on yet? If a protected endpoint answers something
        // other than 401 without a token, the tests can still drive the cycle — and the dedicated
        // auth tests will report the missing enforcement on their own.
        var probe = await anonymous.GetSessionAsync();
        if (probe.StatusCode != 401)
        {
            return new TestCredentials(AuthMode.NotEnforced, null,
                $"no token could be obtained, and GET /session answered {probe.StatusCode} without one, " +
                "so the server is not enforcing authentication yet. Running the cycle unauthenticated.\n" +
                Indent(attempts));
        }

        return new TestCredentials(AuthMode.Unavailable, null,
            "No bearer token could be obtained, and the server does enforce authentication.\n" +
            Indent(attempts) +
            "\n  SERVER_SPEC.md § 10.1.1: opening a pairing window is deliberately not an HTTP operation, so " +
            "the harness has to reach the offer the server publishes in its data directory.\n" +
            $"  To run these tests: set {TokenEnvironmentVariable} to a device token, set " +
            $"{CodeEnvironmentVariable} to a live pairing code, or point the harness at the server's data " +
            $"directory so it can read the published offer file ({string.Join(", ", OfferFileNames)}).");
    }

    /// <summary>A device paired purely so a test can revoke it.</summary>
    public sealed record SpareDevice(string DeviceId, string Token);

    /// <summary>
    /// Pair a second device, for the revocation tests.
    ///
    /// Done up front alongside the primary token rather than inside the test that needs it: POST
    /// /pair is rate limited to five attempts a minute (§ 15) and the pairing tests deliberately
    /// spend that budget, so a test that paired lazily would succeed or fail by running order.
    /// Returns null when no second window could be had — the caller reports that itself.
    /// </summary>
    public static async Task<SpareDevice?> PairSpareDeviceAsync(Rm2Client anonymous, string dataDirectory)
    {
        var code = await RequestFreshWindowAsync(dataDirectory, TimeSpan.FromSeconds(15));
        if (code is null) return null;

        var response = await anonymous.PairAsync(code, "rm2 test harness (to be revoked)");
        if (response.StatusCode != 201 || response.Json is not { } body) return null;

        var deviceId = body.TryGetProperty("deviceId", out var id) ? id.GetString() : null;
        var token = body.TryGetProperty("token", out var value) ? value.GetString() : null;

        return deviceId is { Length: > 0 } && token is { Length: > 0 }
            ? new SpareDevice(deviceId, token)
            : null;
    }

    /// <summary>
    /// Ask for a brand-new pairing window and wait until the server publishes one whose code
    /// differs from the one already on offer.
    ///
    /// A test that wants to watch the per-window attempt budget fall (SERVER_SPEC.md § 10.11) needs
    /// a window with its budget intact, and the window is shared by the whole suite — so without
    /// this, whether the test could see anything would depend on which pairing test ran first.
    /// Returns null if no fresh window appears.
    /// </summary>
    public static async Task<string?> RequestFreshWindowAsync(string dataDirectory, TimeSpan timeout)
    {
        var previous = CurrentCode(dataDirectory);

        try
        {
            Directory.CreateDirectory(dataDirectory);
            await File.WriteAllTextAsync(Path.Combine(dataDirectory, PairRequestFileName), "");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var code = CurrentCode(dataDirectory);
            if (code is not null && code != previous) return code;
            await Task.Delay(150);
        }

        return null;
    }

    private static string? CurrentCode(string dataDirectory)
    {
        foreach (var name in OfferFileNames)
        {
            var code = TryReadCode(Path.Combine(dataDirectory, name));
            if (code is not null) return code;
        }

        return null;
    }

    /// <summary>
    /// Ask for a pairing window through the out-of-band file channel and read back the code the
    /// server publishes. Polls rather than waits on a signal: the server checks for the request
    /// file on a timer, so there is nothing to await.
    /// </summary>
    private static async Task<string?> ReadOfferedCodeAsync(string dataDirectory, List<string> attempts)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        var requested = false;

        while (DateTime.UtcNow < deadline)
        {
            foreach (var name in OfferFileNames)
            {
                var path = Path.Combine(dataDirectory, name);
                if (!File.Exists(path)) continue;

                var code = TryReadCode(path);
                if (code is not null)
                {
                    attempts.Add($"read a pairing code from {name}");
                    return code;
                }
            }

            if (!requested)
            {
                requested = true;
                try
                {
                    Directory.CreateDirectory(dataDirectory);
                    await File.WriteAllTextAsync(Path.Combine(dataDirectory, PairRequestFileName), "");
                }
                catch (IOException)
                {
                    // Not being able to ask is just another exhausted option.
                }
            }

            await Task.Delay(200);
        }

        attempts.Add($"no pairing offer file appeared in '{dataDirectory}' within 15 s " +
                     $"(looked for {string.Join(", ", OfferFileNames)}, and asked with {PairRequestFileName})");
        return null;
    }

    private static string? TryReadCode(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;

            if (root.TryGetProperty("code", out var code) && code.GetString() is { Length: > 0 } value)
                return value;

            // Otherwise dig the code out of the QR payload, which carries it as a query parameter.
            if (root.TryGetProperty("payload", out var payload) && payload.GetString() is { } uri)
            {
                var marker = uri.IndexOf("code=", StringComparison.Ordinal);
                if (marker >= 0)
                {
                    var rest = uri[(marker + 5)..];
                    var end = rest.IndexOf('&');
                    return Uri.UnescapeDataString(end < 0 ? rest : rest[..end]);
                }
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // A half-written file on the next poll is normal.
        }

        return null;
    }

    private static string Indent(IEnumerable<string> lines) =>
        string.Join("\n", lines.Select(line => "  tried: " + line));
}
