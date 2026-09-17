using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using RankMaster2.Server.Contracts;

namespace RankMaster2.Server.Security;

public sealed record PingFeatures(
    bool Rename,
    bool VideoTranscoding,
    bool PosterFrames,
    bool VideoProbe,
    string Browse,
    int MaxConcurrentSessions);

public sealed record PingLimits(
    IReadOnlyList<int> StillWidths,
    int ThumbWidth,
    int MaxJsonBodyBytes,
    int SessionLockTimeoutSeconds);

public sealed record PingResponse(
    string Product,
    string ApiVersion,
    string Version,
    bool Ready,
    bool Authenticated,
    string CertificateFingerprint,
    string ServerTime,
    PingFeatures Features,
    PingLimits Limits,
    SessionStatus? Session);

public sealed record PairResponse(
    string DeviceId,
    string? DeviceName,
    string Token,
    string IssuedAt,
    string? ExpiresAt);

/// <summary>
/// Route registration for the security and transport layer: <c>/ping</c>, <c>/pair</c>,
/// <c>/pair/{deviceId}</c>, <c>/libraries/roots</c> and <c>/libraries/browse</c>, plus the pipeline
/// that protects every other route in the server.
///
/// <para>One call from <c>Program.cs</c>; everything else lives in this folder.</para>
/// </summary>
public static class SecurityEndpoints
{
    private const string ApiBase = "/api/v1";

    /// <summary>
    /// Wires the whole layer. Call this immediately after <c>builder.Build()</c> and before the other
    /// route groups: the middleware it installs applies to every endpoint in the server regardless of
    /// mapping order, and installing it first is what makes that obvious to anyone reading
    /// <c>Program.cs</c>.
    /// </summary>
    public static WebApplication UseRankMaster2Security(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("RankMaster2.Security");

        var options = new Rm2SecurityOptions();
        app.Configuration.GetSection(Rm2SecurityOptions.SectionName).Bind(options);

        var dataDirectory = options.ResolveDataDirectory();
        var listenAddress = options.ResolveListenAddress();

        DataDirectory.Ensure(dataDirectory);
        var certificates = CertificateStore.LoadOrCreate(dataDirectory, listenAddress);
        var tokens = TokenStore.Open(dataDirectory, logger);
        var limiter = new PairingRateLimiter(options.PairingAttemptsPerMinute);
        var pairing = new PairingService(
            options, tokens, limiter, dataDirectory, certificates.Fingerprint,
            listenAddress.ToString(), options.Port);

        var sessions = app.Services.GetService<ISessionStatusProvider>()
                       ?? new ReflectiveSessionStatusProvider();

        var version = typeof(SecurityEndpoints).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        var state = new SecurityState(
            options, dataDirectory, listenAddress, certificates, tokens, pairing,
            new LibraryBrowser(), sessions, version);

        ConfigureTransport(app, state, logger);
        WireLifetime(app, state, logger);

        app.UseTransport(state, logger);
        app.UseGate(state);

        MapPing(app, state);
        MapPairing(app, state);
        MapLibraries(app, state);

        logger.LogInformation(
            "Security layer ready. Data directory {DataDirectory}, certificate {Fingerprint}.",
            dataDirectory, certificates.FingerprintHeaderValue);

        return app;
    }

    // ---------------------------------------------------------------- transport

    private static void ConfigureTransport(WebApplication app, SecurityState state, ILogger logger)
    {
        try
        {
            var kestrel = app.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value;

            // One explicit endpoint. SERVER_SPEC.md § 2: https only, an explicit LAN address, never
            // a wildcard bind. Calling Listen also takes the binding decision away from
            // ASPNETCORE_URLS, so an environment variable cannot quietly move the server onto
            // http or onto every interface.
            kestrel.Listen(state.ListenAddress, state.Options.Port,
                listen => listen.UseHttps(state.Certificates.Certificate));

            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = 64L * 1024 * 1024;   // media POSTs do not exist; this is a backstop
            kestrel.Limits.MaxRequestHeadersTotalSize = 32 * 1024;
            kestrel.Limits.MaxRequestLineSize = 16 * 1024;

            logger.LogInformation("Listening on https://{Address}:{Port}{Base}",
                state.ListenAddress, state.Options.Port, ApiBase);
        }
        catch (Exception e)
        {
            // The in-memory test server has no Kestrel to configure. A real deployment that cannot
            // configure TLS must be loud about it rather than silently serving plaintext.
            logger.LogWarning(e,
                "Kestrel was not configured by the security layer; TLS is not being served by it.");
        }
    }

    private static void WireLifetime(WebApplication app, SecurityState state, ILogger logger)
    {
        var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
        var stopping = new CancellationTokenSource();

        lifetime.ApplicationStarted.Register(() =>
        {
            state.Ready = true;

            // H12: auto-open only on a genuinely fresh install (no devices.json at all), never
            // because the store turned out to be unreadable — a damaged store must not double as a
            // way to force a new pairing window open.
            if (state.Options.AutoOpenPairingWhenUnenrolled && state.Tokens.WasAbsentAtLoad)
                OpenPairingWindow(state, logger, "no device is enrolled");

            if (state.Options.WatchPairRequestFile)
                _ = WatchPairRequestsAsync(state, logger, stopping.Token);
        });

        lifetime.ApplicationStopping.Register(() =>
        {
            state.Ready = false;
            stopping.Cancel();
            state.Pairing.CloseWindow();
        });
    }

    /// <summary>
    /// The out-of-band channel that opens a pairing window: a file in the data directory, which only
    /// a process running as the owner can create. Deliberately not an HTTP route — an HTTP route
    /// would let anyone on the LAN start a pairing window and then spend the rest of the day
    /// guessing at it.
    /// </summary>
    private static async Task WatchPairRequestsAsync(SecurityState state, ILogger logger, CancellationToken cancellation)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellation))
            {
                if (state.Pairing.ConsumePairRequestFile())
                    OpenPairingWindow(state, logger, "requested via " + Path.GetFileName(state.Pairing.RequestFilePath));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "The pairing request watcher stopped.");
        }
    }

    private static void OpenPairingWindow(SecurityState state, ILogger logger, string reason)
    {
        var offer = state.Pairing.OpenWindow(DateTimeOffset.UtcNow);

        // K4: the code itself is never written to the log, at any level — a log file is not the
        // "out of band" channel SERVER_SPEC.md § 10.11 means. It goes only to the offer file, with
        // owner-only permissions; rm2ctl and the tray read the code from there.
        logger.LogInformation(
            "Pairing window open ({Reason}), valid until {ExpiresAt:O}. QR payload written to {OfferFile}.",
            reason, offer.ExpiresAt, state.Pairing.OfferFilePath);
    }

    // ---------------------------------------------------------------- /ping

    private static void MapPing(WebApplication app, SecurityState state)
    {
        app.MapMethods($"{ApiBase}/ping", ["GET", "HEAD"], async (HttpContext context) =>
        {
            var device = SecurityMiddleware.AuthenticatedDevice(context);
            var authenticated = device is not null;

            var body = new PingResponse(
                Product: "Rank Master 3 server",
                ApiVersion: "v1",
                Version: state.Version,
                Ready: state.Ready,
                Authenticated: authenticated,
                CertificateFingerprint: state.Certificates.FingerprintHeaderValue,
                ServerTime: Rfc3339(DateTimeOffset.UtcNow),
                Features: new PingFeatures(
                    Rename: true,
                    VideoTranscoding: false,
                    PosterFrames: false,
                    VideoProbe: false,
                    Browse: "full-filesystem",
                    MaxConcurrentSessions: 1),
                Limits: new PingLimits(
                    StillWidths: [360, 540, 720, 1080, 1440, 2160],
                    ThumbWidth: 320,
                    MaxJsonBodyBytes: Limits.MaxJsonBodyBytes,
                    SessionLockTimeoutSeconds: 5),

                // Null in the public subset. A client that has not paired learns the fingerprint it
                // needs to pin and nothing at all about what is on the disk.
                Session: authenticated ? state.Sessions.Current : null);

            await ApiResults.WriteJsonAsync(context, body);
        });
    }

    // ---------------------------------------------------------------- /pair

    private static void MapPairing(WebApplication app, SecurityState state)
    {
        app.MapPost($"{ApiBase}/pair", async (HttpContext context) =>
        {
            string? code;
            string? deviceName;

            try
            {
                using var document = await JsonDocument.ParseAsync(context.Request.Body, default, context.RequestAborted);
                var root = document.RootElement;

                if (root.ValueKind != JsonValueKind.Object)
                {
                    await ApiResults.WriteErrorAsync(context, ErrorCodes.InvalidRequest,
                        "The request body must be a JSON object.", new { field = "body" });
                    return;
                }

                if (!root.TryGetProperty("code", out var codeElement) || codeElement.ValueKind == JsonValueKind.Null)
                {
                    await ApiResults.WriteErrorAsync(context, ErrorCodes.MissingField,
                        "A pairing code is required.", new { field = "code" });
                    return;
                }

                if (codeElement.ValueKind != JsonValueKind.String)
                {
                    await ApiResults.WriteErrorAsync(context, ErrorCodes.InvalidRequest,
                        "The pairing code must be a string.", new { field = "code" });
                    return;
                }

                code = codeElement.GetString();

                deviceName = null;
                if (root.TryGetProperty("deviceName", out var nameElement) &&
                    nameElement.ValueKind != JsonValueKind.Null)
                {
                    if (nameElement.ValueKind != JsonValueKind.String)
                    {
                        await ApiResults.WriteErrorAsync(context, ErrorCodes.InvalidRequest,
                            "deviceName must be a string.", new { field = "deviceName" });
                        return;
                    }

                    deviceName = nameElement.GetString();
                }
            }
            catch (JsonException)
            {
                await ApiResults.WriteErrorAsync(context, ErrorCodes.InvalidRequest,
                    "The request body is not valid JSON.", new { field = "body" });
                return;
            }

            var sourceAddress = PairingRateLimiter.KeyFor(context.Connection.RemoteIpAddress);
            var result = state.Pairing.Redeem(code, deviceName, sourceAddress, DateTimeOffset.UtcNow);

            switch (result.Outcome)
            {
                case PairOutcome.NotOpen:
                    await ApiResults.WriteErrorAsync(context, ErrorCodes.PairingNotOpen,
                        "No pairing window is open.");
                    return;

                case PairOutcome.InvalidCode:
                    // One message for wrong, used and expired alike. Which of the three it was is
                    // exactly what a guesser would like to know.
                    await ApiResults.WriteErrorAsync(context, ErrorCodes.InvalidPairingCode,
                        "That pairing code is not valid.",
                        new { attemptsRemaining = result.AttemptsRemaining });
                    return;

                default:
                    var issued = result.Issued!;
                    await ApiResults.WriteJsonAsync(context, new PairResponse(
                        issued.Device.DeviceId,
                        issued.Device.DeviceName,
                        issued.Token,
                        Rfc3339(issued.Device.IssuedAt),
                        issued.Device.ExpiresAt is { } expiry ? Rfc3339(expiry) : null),
                        StatusCodes.Status201Created);
                    return;
            }
        });

        app.MapDelete($"{ApiBase}/pair/{{deviceId}}", async (HttpContext context, string deviceId) =>
        {
            // A device may revoke itself (§ 10.12); revocation does not close an open session.
            if (string.IsNullOrWhiteSpace(deviceId) || deviceId.Length > 64 ||
                !state.Tokens.Revoke(deviceId, DateTimeOffset.UtcNow))
            {
                await ApiResults.WriteErrorAsync(context, ErrorCodes.NotFound, "No such device.");
                return;
            }

            await ApiResults.WriteNoContentAsync(context);
        });
    }

    // ---------------------------------------------------------------- /libraries

    private static void MapLibraries(WebApplication app, SecurityState state)
    {
        app.MapGet($"{ApiBase}/libraries/roots", async (HttpContext context) =>
            await ApiResults.WriteJsonAsync(context, state.Browser.Roots()));

        app.MapGet($"{ApiBase}/libraries/browse", async (HttpContext context) =>
        {
            var query = context.Request.Query;

            // One value per parameter. Two `path=` values mean one layer's answer and another
            // layer's answer can differ, which is the whole mechanism behind parameter pollution.
            if (query["path"].Count > 1 || query["counts"].Count > 1)
            {
                await ApiResults.WriteErrorAsync(context, ErrorCodes.InvalidRequest,
                    "Each query parameter may appear at most once.", new { field = "path" });
                return;
            }

            var counts = true;
            var rawCounts = query["counts"].ToString();
            if (!string.IsNullOrEmpty(rawCounts))
            {
                switch (rawCounts.ToLowerInvariant())
                {
                    case "true" or "1": counts = true; break;
                    case "false" or "0": counts = false; break;
                    default:
                        await ApiResults.WriteErrorAsync(context, ErrorCodes.InvalidRequest,
                            "counts must be true or false.", new { field = "counts" });
                        return;
                }
            }

            var check = PathGuard.Check(query["path"].ToString());
            if (!check.Ok)
            {
                await ApiResults.WriteErrorAsync(context, ErrorCodes.InvalidPath,
                    DescribeRejection(check.Reason), new { field = "path" });
                return;
            }

            var result = state.Browser.Browse(check.Canonical, counts);
            switch (result.Failure)
            {
                case BrowseFailure.NotFound:
                    await ApiResults.WriteErrorAsync(context, ErrorCodes.FolderNotFound,
                        "That folder does not exist.", new { folder = check.Canonical });
                    return;

                case BrowseFailure.NotADirectory:
                    await ApiResults.WriteErrorAsync(context, ErrorCodes.FolderNotADirectory,
                        "That path is a file, not a folder.", new { folder = check.Canonical });
                    return;

                case BrowseFailure.AccessDenied:
                    await ApiResults.WriteErrorAsync(context, ErrorCodes.FolderAccessDenied,
                        "The operating system refused to list that folder.", new { folder = check.Canonical });
                    return;

                default:
                    await ApiResults.WriteJsonAsync(context, result.Response!);
                    return;
            }
        });
    }

    /// <summary>
    /// Human text only. It names nothing the caller did not already send, and it never reports which
    /// of several checks a path failed in a machine-readable field — <c>details</c> stays exactly the
    /// <c>{ field }</c> shape SERVER_SPEC.md § 5.2 fixes for <c>invalid_path</c>.
    /// </summary>
    private static string DescribeRejection(PathRejection reason) => reason switch
    {
        PathRejection.Empty => "A path is required.",
        PathRejection.TooLong => "That path is longer than 4096 characters.",
        PathRejection.Nul => "That path contains a NUL character.",
        PathRejection.ControlCharacter => "That path contains a control character.",
        PathRejection.NotAbsolute => "That path is not absolute.",
        PathRejection.Traversal => "That path contains a relative segment.",
        PathRejection.Unc => "UNC and device paths are not browsable.",
        PathRejection.DeviceName => "That path names a reserved device.",
        PathRejection.TrailingDotOrSpace => "A path segment ends with a dot or a space.",
        _ => "That path is not usable.",
    };

    private static string Rfc3339(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
