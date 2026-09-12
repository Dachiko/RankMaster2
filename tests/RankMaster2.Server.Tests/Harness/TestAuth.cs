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
/// Getting a bearer token for the tests, and the one place where the contract leaves a real hole.
///
/// SERVER_SPEC.md § 10.11 says the pairing window "is opened out of band" and never says by what.
/// There is no endpoint for it and no named channel, so a test cannot pair by following the
/// contract alone — which is a gap in the contract, not in any implementation.
///
/// The out-of-band channel this server actually uses is a file in its data directory: it publishes
/// the open window's code there, and watches for a request file that asks it to open one. That is a
/// sound choice — an HTTP route for opening a window would let anyone on the LAN start one — but it
/// is a wiring detail, so it lives here rather than in an assertion, and it is tried alongside the
/// plainer seams. If none of them yields a token, the failure says exactly what was tried.
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

    /// <summary>Files a server may publish an open pairing window through.</summary>
    public static readonly string[] OfferFileNames = { "pairing.json", "pair.offer", "pairing.offer.json" };

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
            "\n  SERVER_SPEC.md § 10.11 says the pairing window is 'opened out of band' but never says how, " +
            "so there is nothing in the contract a test can call to open one.\n" +
            $"  To run these tests: set {TokenEnvironmentVariable} to a device token, set " +
            $"{CodeEnvironmentVariable} to a live pairing code, or point the harness at the server's data " +
            $"directory so it can read the published offer file ({string.Join(", ", OfferFileNames)}).");
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
