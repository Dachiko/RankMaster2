using System.Text.Json;
using RankMaster2.Cli;

// rm2ctl — the headless client for the Rank Master 2 server.
//
// It exists so the server can be exercised, and its contract checked, without a phone: `cycle` drives
// the whole ranking cycle end to end and exits non-zero if anything disagrees with SERVER_SPEC.md.
// `pair` is the other half — it shows the QR code a phone scans, which is the one part of the flow
// that has to happen on the server's own machine.

return await Entry.RunAsync(args);

internal static class Entry
{
    public static async Task<int> RunAsync(string[] args)
    {
        var options = Options.Parse(args);

        if (options.WantsHelp || options.Command is null)
        {
            Options.PrintUsage();
            return options.Command is null && !options.WantsHelp ? 2 : 0;
        }

        var journal = new Journal(options.Verbose);

        try
        {
            return options.Command switch
            {
                "ping" => await PingAsync(options, journal),
                "pair" => await PairAsync(options, journal),
                "session" => await SessionAsync(options, journal),
                "browse" => await BrowseAsync(options, journal),
                "cycle" => await CycleAsync(options, journal),
                _ => Unknown(options.Command)
            };
        }
        catch (Rm2Unreachable e)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("  Could not talk to the server.");
            Console.Error.WriteLine();
            foreach (var line in e.Message.Split('\n')) Console.Error.WriteLine("    " + line);
            Console.Error.WriteLine();
            return 2;
        }
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"rm2ctl: no such command '{command}'.");
        Options.PrintUsage();
        return 2;
    }

    private static Rm2Api Connect(Options options, Journal journal) =>
        new(options.BaseUrl, journal, options.Pin, options.Insecure, options.Timeout) { Token = options.Token };

    // ---- ping -------------------------------------------------------------------------------

    private static async Task<int> PingAsync(Options options, Journal journal)
    {
        using var api = Connect(options, journal);

        journal.Title($"Ping {api.BaseUrl}");
        journal.Step("read the server's own account of itself");

        var reply = await api.PingAsync();
        if (reply.Status != 200)
        {
            journal.Failure("GET /ping answers 200 (SERVER_SPEC.md § 14)", reply.Summarise());
            return journal.Summary();
        }

        if (reply.Json is not { } body)
        {
            journal.Failure("GET /ping returns JSON");
            return journal.Summary();
        }

        journal.Detail(
            $"{Text(body, "product")} {Text(body, "version")}, api {Text(body, "apiVersion")}\n" +
            $"ready {Bool(body, "ready")}, authenticated {Bool(body, "authenticated")}\n" +
            $"server time {Text(body, "serverTime")}\n" +
            $"certificate  {Text(body, "certificateFingerprint")}");

        if (api.ObservedFingerprint is { } observed)
        {
            var advertised = Text(body, "certificateFingerprint");
            journal.Detail($"presented   {observed}");
            journal.Check(string.Equals(observed, advertised, StringComparison.OrdinalIgnoreCase),
                "the fingerprint in /ping is the certificate actually presented — this is what the client pins, " +
                "and it is the whole reason a self-signed certificate is security rather than decoration " +
                "(SERVER_SPEC.md § 14)",
                $"presented {observed}\nadvertised {advertised}");
        }

        if (body.TryGetProperty("features", out var features))
            journal.Detail("features:  " + string.Join("  ", features.EnumerateObject()
                .Select(p => $"{p.Name}={p.Value}")));

        if (body.TryGetProperty("limits", out var limits))
            journal.Detail("limits:    " + string.Join("  ", limits.EnumerateObject()
                .Select(p => $"{p.Name}={p.Value}")));

        if (body.TryGetProperty("session", out var session))
            journal.Detail("session:   " + (session.ValueKind == JsonValueKind.Null
                ? "null (the public subset — no token, or an unauthenticated read)"
                : session.ToString()));

        journal.Ok("the server is reachable and answered /ping");
        return journal.Summary();
    }

    // ---- pair -------------------------------------------------------------------------------

    private static async Task<int> PairAsync(Options options, Journal journal)
    {
        journal.Title("Pair a device");

        var code = options.Code;

        if (code is null)
        {
            // SERVER_SPEC.md § 10.11: the window is opened out of band. The channel is the server's
            // own data directory, which only the user it runs as can write — deliberately not an
            // HTTP route, or anyone on the LAN could open a window and start guessing.
            var directory = options.DataDirectory ?? Pairing.DefaultDataDirectory();
            journal.Step($"read the pairing window published in {directory}");

            var offer = await Pairing.ReadOfferAsync(directory, TimeSpan.FromSeconds(20), request: true, journal);
            if (offer is null)
            {
                journal.Failure(
                    "the server publishes an open pairing window in its data directory",
                    $"Looked for {Pairing.OfferFileName} in '{directory}' for 20 s and asked " +
                    $"for one with {Pairing.RequestFileName}.\n" +
                    "Point --data-dir at the server's data directory, or pass the code with --code.");
                return journal.Summary();
            }

            Pairing.Show(offer, journal);

            if (!options.TakeCode)
            {
                Console.WriteLine("  Pass --take to redeem this code here instead of on the phone.");
                Console.WriteLine();
                return 0;
            }

            // The offer names the host and port to come back on. Using the built-in default
            // instead is how this ended up talking TLS at whatever else held the default port.
            if (!options.BaseUrlGiven && offer.Host is not null && offer.Port is not null)
            {
                options.AdoptBase(offer.Host, offer.Port.Value);
                journal.Note($"using the address from the pairing offer: {options.BaseUrl}");
            }

            // SERVER_SPEC.md § 10.1.1: "The client MUST pin the fingerprint before sending the
            // code. The code is a bearer secret: handing it to an unverified TLS peer hands it to
            // whoever answered." The offer just read carries that fingerprint and came from a
            // directory only the owner's account can write, so there is nothing to trust on first
            // use — and trusting on first use here is precisely the window an attacker needs.
            if (options.Pin is null && !options.Insecure)
            {
                if (offer.Fingerprint is not { Length: > 0 })
                {
                    journal.Failure(
                        "the pairing offer carries the certificate fingerprint to pin before the code is sent " +
                        "(SERVER_SPEC.md § 10.1.1)",
                        "This offer has no `certificateFingerprint`, so the code cannot be sent to a verified\n" +
                        "peer. Pass --pin sha256:… with the fingerprint you trust, or --insecure if you accept\n" +
                        "handing the code to whoever answers.");
                    return journal.Summary();
                }

                options.AdoptPin(offer.Fingerprint);
                journal.Note("pinned the fingerprint from the pairing offer before sending the code");
            }

            code = offer.Code;
        }

        // A31, the other half. The --take path above pins the fingerprint the offer published, so
        // it can never arrive here unpinned. A code typed by hand (--code) reaches here with no
        // offer to take a fingerprint from, and sending it to an unverified peer is the same
        // mistake § 10.1.1 forbids: the code is a bearer secret, and trusting on first use is
        // exactly the window an attacker needs. Refuse, and say the two ways forward.
        if (options.Pin is null && !options.Insecure)
        {
            journal.Failure(
                "the certificate is pinned before a pairing code is sent (SERVER_SPEC.md § 10.1.1)",
                "No fingerprint to pin: --code was given without --pin, and no pairing offer was read.\n" +
                "Pass --pin sha256:… with the fingerprint you trust (the tray shows it, and so does\n" +
                "`rm2ctl ping --insecure`), or use --take to pin the one the server published, or\n" +
                "--insecure if you accept handing the code to whoever answers.");
            return journal.Summary();
        }

        // Connect only now: the offer above may have just told us which address to use and which
        // fingerprint to pin, and the client has to be built against the final ones.
        using var api = Connect(options, journal);

        journal.Step("exchange the code for a device token");
        var reply = await api.PairAsync(code, options.DeviceName ?? $"rm2ctl on {Environment.MachineName}");

        if (reply.Status != 201)
        {
            journal.Failure("POST /pair exchanges a valid code for a token (SERVER_SPEC.md § 10.11)",
                reply.Summarise() + "\n" +
                "Never retry this blindly: the code is single-use, and a retry of a pairing that already " +
                "succeeded loses the token. Start pairing again (SERVER_SPEC.md § 13.3).");
            return journal.Summary();
        }

        var body = reply.Json!.Value;
        Console.WriteLine();
        Console.WriteLine("  Paired.");
        Console.WriteLine("    device   " + Text(body, "deviceId") + "  (" + Text(body, "deviceName") + ")");
        Console.WriteLine("    issued   " + Text(body, "issuedAt"));
        Console.WriteLine("    expires  " + (body.GetProperty("expiresAt").ValueKind == JsonValueKind.Null
            ? "never" : Text(body, "expiresAt")));
        Console.WriteLine();
        Console.WriteLine("    token    " + Text(body, "token"));
        Console.WriteLine();
        Console.WriteLine("  The token is returned once and is never readable again. Send it only in the");
        Console.WriteLine("  Authorization header — a token in a query string is ignored (SERVER_SPEC.md § 3).");
        Console.WriteLine();
        Console.WriteLine("    export RM2_TOKEN=" + Text(body, "token"));
        Console.WriteLine();
        return 0;
    }

    // ---- session ----------------------------------------------------------------------------

    private static async Task<int> SessionAsync(Options options, Journal journal)
    {
        using var api = Connect(options, journal);
        journal.Title("Session");

        if (options.Close)
        {
            journal.Step("close");
            var closed = await api.CloseSessionAsync();
            journal.Check(closed.Status is 204 or 404,
                "DELETE /session is 204, or 404 no_session when none is open (SERVER_SPEC.md § 10.4)",
                closed.Summarise());
            return journal.Summary();
        }

        if (options.Folder is not null)
        {
            journal.Step($"open {options.Folder}");
            var opened = await api.OpenSessionAsync(options.Folder);
            if (opened.Status is 200 or 201 && Snapshot.From(opened) is { } fresh)
            {
                journal.Detail(fresh.Describe());
                journal.Ok(opened.Status == 201 ? "opened a new session" : "resumed the session already open");
            }
            else
            {
                journal.Failure("POST /session (SERVER_SPEC.md § 10.1)", opened.Summarise());
            }

            return journal.Summary();
        }

        journal.Step("read the snapshot");
        var reply = await api.GetSessionAsync();

        if (reply.Status == 404 && reply.ErrorCode == "no_session")
        {
            journal.Note("no session is open");
            return 0;
        }

        if (Snapshot.From(reply) is { } snapshot)
        {
            journal.Detail(snapshot.Describe());
            journal.Detail($"folder {snapshot.Folder}");
            journal.Ok("read the session");
            return journal.Summary();
        }

        journal.Failure("GET /session returns a SessionSnapshot (SERVER_SPEC.md § 10.2)", reply.Summarise());
        return journal.Summary();
    }

    // ---- browse -----------------------------------------------------------------------------

    private static async Task<int> BrowseAsync(Options options, Journal journal)
    {
        using var api = Connect(options, journal);
        journal.Title("Browse");

        if (options.Folder is null)
        {
            journal.Step("list the roots");
            var roots = await api.RootsAsync();
            if (roots.Status != 200 || roots.Json is not { } rootBody)
            {
                journal.Failure("GET /libraries/roots (SERVER_SPEC.md § 10.14)", roots.Summarise());
                return journal.Summary();
            }

            foreach (var root in rootBody.GetProperty("roots").EnumerateArray())
                journal.Detail($"{Text(root, "path"),-28} {Text(root, "kind"),-10} " +
                               $"{(root.GetProperty("available").GetBoolean() ? "available" : "not available")}");

            journal.Ok("listed the roots");
            return journal.Summary();
        }

        journal.Step($"list {options.Folder}");
        var reply = await api.BrowseAsync(options.Folder, options.Counts);

        if (reply.Status != 200 || reply.Json is not { } body)
        {
            journal.Failure("GET /libraries/browse (SERVER_SPEC.md § 10.15)", reply.Summarise());
            return journal.Summary();
        }

        foreach (var entry in body.GetProperty("entries").EnumerateArray())
        {
            var stills = entry.GetProperty("stillCount");
            var videos = entry.GetProperty("videoCount");
            var rankable = entry.GetProperty("rankable");

            journal.Detail(
                $"{Text(entry, "name"),-32} " +
                $"stills {Show(stills),-6} videos {Show(videos),-6} " +
                $"{(rankable.ValueKind == JsonValueKind.Null ? "rankable ?" : rankable.GetBoolean() ? "rankable" : "not rankable"),-14}" +
                (entry.GetProperty("hasDatabase").GetBoolean() ? " has db" : "") +
                (entry.GetProperty("accessible").GetBoolean() ? "" : "  (unreadable)"));
        }

        journal.Ok($"listed {body.GetProperty("entries").GetArrayLength()} child folders");
        return journal.Summary();

        static string Show(JsonElement value) => value.ValueKind == JsonValueKind.Null ? "?" : value.ToString();
    }

    // ---- cycle ------------------------------------------------------------------------------

    private static async Task<int> CycleAsync(Options options, Journal journal)
    {
        using var api = Connect(options, journal);

        if (api.Token is null)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("  rm2ctl cycle needs a device token.");
            Console.Error.WriteLine("    Pass --token, or set RM2_TOKEN, or run 'rm2ctl pair --take' first.");
            Console.Error.WriteLine();
            return 2;
        }

        var folder = options.Folder;
        string? scratch = null;

        if (folder is null)
        {
            scratch = ScratchLibrary.Create();
            folder = scratch;
            Console.WriteLine();
            Console.WriteLine($"  No --folder given, so rm2ctl made one: {scratch}");
            Console.WriteLine("  Six generated PNGs. It is deleted when the run finishes.");
        }

        try
        {
            await new Cycle(api, journal).RunAsync(folder);
        }
        finally
        {
            if (scratch is not null)
            {
                // Leave the folder behind on a failure: whatever went wrong is easier to look at
                // with the files still there.
                if (journal.AnythingFailed)
                    Console.WriteLine($"\n  Keeping {scratch} so the failure can be inspected.");
                else
                    ScratchLibrary.Remove(scratch);
            }
        }

        return journal.Summary();
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.ToString() : "";

    private static string Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.ToString() : "?";
}
