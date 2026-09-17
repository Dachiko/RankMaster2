namespace RankMaster2.Cli;

/// <summary>Command line parsing, and the usage text that doubles as the tool's documentation.</summary>
public sealed class Options
{
    public string? Command { get; private set; }
    public bool WantsHelp { get; private set; }
    public bool Verbose { get; private set; }

    public string BaseUrl { get; private set; } = "https://127.0.0.1:18611/api/v1";

    /// <summary>True once --base was actually given, so a published pairing offer can fill it in.</summary>
    public bool BaseUrlGiven { get; private set; }

    /// <summary>Adopt the host and port the server published in its pairing offer.</summary>
    public void AdoptBase(string host, int port) => BaseUrl = Normalise($"{host}:{port}");

    /// <summary>
    /// Pin the fingerprint the server published in its own pairing offer (A31). The offer is read
    /// from the server's data directory, which only the owner's OS account can write, so it is a
    /// better source for the pin than the certificate the peer presents — and § 10.1.1 requires the
    /// pin to be in place <b>before</b> the code is sent, because the code is a bearer secret.
    /// An explicit <c>--pin</c> wins; <c>--insecure</c> is the only way to send a code unpinned.
    /// </summary>
    public void AdoptPin(string fingerprint) => Pin = fingerprint;

    public string? Token { get; private set; } = Environment.GetEnvironmentVariable("RM2_TOKEN");
    public string? Pin { get; private set; } = Environment.GetEnvironmentVariable("RM2_PIN");
    public bool Insecure { get; private set; }
    public TimeSpan Timeout { get; private set; } = TimeSpan.FromSeconds(60);

    public string? Folder { get; private set; }
    public string? Code { get; private set; }
    public string? DeviceName { get; private set; }
    public string? DataDirectory { get; private set; }
    public bool TakeCode { get; private set; }
    public bool Close { get; private set; }
    public bool? Counts { get; private set; }

    public static Options Parse(string[] args)
    {
        var options = new Options();

        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];

            string Next(string name) =>
                i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{name} needs a value.");

            switch (argument)
            {
                case "-h" or "--help" or "help":
                    options.WantsHelp = true;
                    break;
                case "-v" or "--verbose":
                    options.Verbose = true;
                    break;
                case "--base":
                    options.BaseUrl = Normalise(Next("--base"));
                    options.BaseUrlGiven = true;
                    break;
                case "--token":
                    options.Token = Next("--token");
                    break;
                case "--pin":
                    options.Pin = Next("--pin");
                    break;
                case "--insecure":
                    options.Insecure = true;
                    break;
                case "--timeout":
                    options.Timeout = TimeSpan.FromSeconds(double.Parse(Next("--timeout")));
                    break;
                case "--folder" or "--path":
                    options.Folder = Path.GetFullPath(Next(argument));
                    break;
                case "--code":
                    options.Code = Next("--code");
                    break;
                case "--name" or "--device-name":
                    options.DeviceName = Next(argument);
                    break;
                case "--data-dir":
                    options.DataDirectory = Path.GetFullPath(Next("--data-dir"));
                    break;
                case "--take":
                    options.TakeCode = true;
                    break;
                case "--close":
                    options.Close = true;
                    break;
                case "--counts":
                    options.Counts = true;
                    break;
                case "--no-counts":
                    options.Counts = false;
                    break;
                default:
                    if (argument.StartsWith('-'))
                        throw new ArgumentException($"Unknown option '{argument}'.");
                    options.Command ??= argument;
                    break;
            }
        }

        return options;
    }

    /// <summary>
    /// Accept a bare host, a scheme and host, or a full base path — and always end up at
    /// <c>/api/v1</c>, which is where every path in SERVER_SPEC.md is relative to (§ 2).
    /// </summary>
    private static string Normalise(string value)
    {
        var url = value.Trim().TrimEnd('/');
        if (!url.Contains("://", StringComparison.Ordinal)) url = "https://" + url;
        if (!url.EndsWith("/api/v1", StringComparison.Ordinal)) url += "/api/v1";
        return url;
    }

    public static void PrintUsage()
    {
        Console.WriteLine("""

            rm2ctl — drive the Rank Master 2 server from the command line.

            USAGE
              rm2ctl <command> [options]

            COMMANDS
              ping                 Read /ping: version, capabilities, limits, and the certificate
                                   fingerprint the client is meant to pin.

              pair                 Show the open pairing window as a QR code and a short numeric
                                   code, for a phone to scan. Opening a window is deliberately not
                                   an HTTP operation, so this reads the server's data directory —
                                   run it on the server's own machine.
                --take             Redeem the code here and print a device token instead. The
                                   fingerprint in the offer is pinned before the code is sent;
                                   --pin overrides it and --insecure is the only way to skip it.
                --code "418 250"   Redeem this code without reading the data directory.
                --data-dir PATH    Where the server keeps its data (default: the per-user location).
                --name NAME        The device name recorded against the token.

              session              Show the current snapshot.
                --folder PATH      Open a session on PATH (or resume the one already open on it).
                --close            Close the session and release the folder lock.

              browse               List drive roots.
                --path PATH        List the direct child folders of PATH instead.
                --no-counts        Skip the per-child media counts; every count comes back null.

              cycle                Drive the whole ranking cycle — open, read the pair, fetch the
                                   stills, vote, skip, discard, special, undo, save, close — and
                                   then the refusals the contract specifies: a stale token, no
                                   session, an unknown id, a forbidden width, no credential.
                                   Checks each answer against SERVER_SPEC.md and exits non-zero if
                                   any of them disagree. This is what makes the server testable
                                   without a phone.
                --folder PATH      Rank this folder. Without it, rm2ctl generates a scratch folder
                                   of six images and deletes it afterwards.

            OPTIONS
              --base URL           The server (default https://127.0.0.1:18611/api/v1). A bare host
                                   or host:port is fine; /api/v1 is appended if missing.
              --token TOKEN        The device token. Defaults to $RM2_TOKEN.
              --pin sha256:HEX     Require this certificate fingerprint. Defaults to $RM2_PIN.
                                   Without it, any self-signed certificate is accepted and its
                                   fingerprint is printed for you to pin next time.
              --insecure           Accept any certificate at all.
              --timeout SECONDS    Per-request timeout (default 60).
              -v, --verbose        Print request bodies and every check that passed, not just the
                                   failures.

            EXIT CODES
              0  everything checked matched the contract
              1  the server answered, and something disagreed with SERVER_SPEC.md
              2  the server could not be reached, or the command line was wrong

            EXAMPLES
              rm2ctl pair --take --base 192.168.1.20:18611
              RM2_TOKEN=... rm2ctl cycle --base 192.168.1.20:18611 --folder /photos/trip
              rm2ctl cycle --insecure -v

            """);
    }
}
