using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RankMaster2.Server.Contracts;
using RankMaster2.Server.Security;

namespace RankMaster2.Server.Media;

/// <summary>
/// The four media endpoints of SERVER_SPEC.md § 12.1, and the only thing the media layer asks of
/// the rest of the server: one <see cref="MapMediaEndpoints"/> call in <c>Program.cs</c>.
/// <para/>
/// The layer builds its own collaborators at map time rather than requiring registrations in
/// <c>Program.cs</c>, so nothing outside this folder has to know it exists. The one thing it does
/// look for in the container is an <see cref="IMediaSessionAccessor"/>; without one it serves
/// <c>404 no_session</c>, which is the correct answer when no session layer is present.
/// </summary>
public static class MediaEndpoints
{
    /// <summary>§ 2: every path in the contract is relative to this.</summary>
    public const string BasePath = "/api/v1";

    private static readonly string[] GetAndHead = ["GET", "HEAD"];

    private static readonly JsonSerializerOptionsHolder Json = new();

    private sealed class JsonSerializerOptionsHolder
    {
        public System.Text.Json.JsonSerializerOptions Value { get; } = new(System.Text.Json.JsonSerializerDefaults.Web);
    }

    /// <summary>
    /// Everything the handlers share, built once. Kept out of the container so the media layer
    /// cannot collide with, or depend on the ordering of, registrations another layer makes.
    /// </summary>
    private sealed class MediaLayer(MediaOptions options, StillRenderer renderer, StillCache cache, IMediaSessionAccessor fallbackSessions, ILogger logger)
    {
        public MediaOptions Options { get; } = options;
        public StillRenderer Renderer { get; } = renderer;
        public StillCache Cache { get; } = cache;
        public ILogger Logger { get; } = logger;

        /// <summary>
        /// Prefers the request scope's accessor so a scoped session layer works, and falls back to
        /// whatever was in the root container at map time.
        /// </summary>
        public IMediaSessionAccessor SessionsFor(HttpContext context) =>
            context.RequestServices.GetService<IMediaSessionAccessor>() ?? fallbackSessions;
    }

    /// <summary>
    /// Optional. Registering <see cref="MediaOptions"/> and the media singletons in the container
    /// lets a host bind them from configuration in the usual way; <see cref="MapMediaEndpoints"/>
    /// will then reuse whatever is registered instead of building its own.
    /// </summary>
    public static IServiceCollection AddMediaServices(this IServiceCollection services)
    {
        services.AddOptions<MediaOptions>().BindConfiguration(MediaOptions.SectionName);
        return services;
    }

    /// <summary>
    /// Second audit § 3.5: left to itself, <see cref="MediaOptions.ResolvedCacheDirectory"/> falls
    /// back to a hard-coded platform default that never consults
    /// <c>RankMaster2:DataDirectory</c> / <c>RM2_DATA_DIR</c> — the exact setting SERVER_RUNNING.md
    /// tells an operator to move when the default drive is full, and which lists <c>cache\</c> as
    /// one of the files that live under it. So an operator who moves the data directory for
    /// exactly that reason keeps filling the old drive with up to <see cref="MediaOptions.CacheMaxBytes"/>
    /// of cached stills anyway.
    /// <para/>
    /// Only applied when <see cref="MediaOptions.CacheDirectory"/> was left unset — an operator who
    /// names <c>Media:CacheDirectory</c> explicitly is always obeyed, unchanged from before. The
    /// resolution itself is <see cref="Rm2SecurityOptions.ResolveDataDirectory"/> — the one place
    /// the security layer already answers "where is the data directory" — reused rather than
    /// re-implemented so the two layers can never disagree about it.
    /// </summary>
    public static void ApplyConfiguredCacheDirectoryDefault(MediaOptions options, IConfiguration? configuration)
    {
        if (!string.IsNullOrWhiteSpace(options.CacheDirectory) || configuration is null)
            return;

        var security = new Rm2SecurityOptions();
        configuration.GetSection(Rm2SecurityOptions.SectionName).Bind(security);
        var dataDirectory = security.ResolveDataDirectory();
        if (!string.IsNullOrWhiteSpace(dataDirectory))
            options.CacheDirectory = Path.Combine(dataDirectory, "cache");
    }

    /// <summary>
    /// Maps <c>GET</c> and <c>HEAD</c> for <c>meta</c>, <c>still</c>, <c>thumb</c> and
    /// <c>video</c>. § 2: "<c>HEAD</c> MUST be supported on all four <c>/media</c> endpoints and
    /// MUST return the identical status line and headers as <c>GET</c> with no body."
    /// </summary>
    public static IEndpointRouteBuilder MapMediaEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var services = endpoints.ServiceProvider;
        var loggerFactory = services.GetService<ILoggerFactory>() ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;

        var configuration = services.GetService<IConfiguration>();
        var options =
            services.GetService<IOptions<MediaOptions>>()?.Value
            ?? configuration?.GetSection(MediaOptions.SectionName).Get<MediaOptions>()
            ?? new MediaOptions();

        // § 3.5: only fills in a default when CacheDirectory was left unset; an explicit
        // Media:CacheDirectory is always obeyed as before.
        ApplyConfiguredCacheDirectoryDefault(options, configuration);

        var renderer = services.GetService<StillRenderer>() ?? new StillRenderer(options);
        var cache = services.GetService<StillCache>() ?? new StillCache(options, loggerFactory.CreateLogger<StillCache>());
        var sessions = services.GetService<IMediaSessionAccessor>() ?? new NoOpenSessionAccessor();
        var layer = new MediaLayer(options, renderer, cache, sessions, loggerFactory.CreateLogger("RankMaster2.Server.Media"));

        services.GetService<IHostApplicationLifetime>()?.ApplicationStopping.Register(() =>
        {
            renderer.Dispose();
            cache.Dispose();
        });

        endpoints.MapMethods(BasePath + "/media/{id}/meta", GetAndHead,
            (HttpContext ctx) => HandleAsync(ctx, layer, MediaEndpointKind.Meta));

        endpoints.MapMethods(BasePath + "/media/{id}/still", GetAndHead,
            (HttpContext ctx) => HandleAsync(ctx, layer, MediaEndpointKind.Still));

        endpoints.MapMethods(BasePath + "/media/{id}/thumb", GetAndHead,
            (HttpContext ctx) => HandleAsync(ctx, layer, MediaEndpointKind.Thumb));

        endpoints.MapMethods(BasePath + "/media/{id}/video", GetAndHead,
            (HttpContext ctx) => HandleAsync(ctx, layer, MediaEndpointKind.Video));

        return endpoints;
    }

    private enum MediaEndpointKind
    {
        Meta,
        Still,
        Thumb,
        Video,
    }

    private static async Task HandleAsync(HttpContext context, MediaLayer layer, MediaEndpointKind endpoint)
    {
        MediaHttp.ApplyStandardHeaders(context);

        var resolver = new MediaResolver(layer.SessionsFor(context));
        var resolved = resolver.Resolve(RawIdSegment(context));

        if (!resolved.Ok)
        {
            await WriteResolutionFailureAsync(context, resolved).ConfigureAwait(false);
            return;
        }

        var media = resolved.Media!;

        try
        {
            switch (endpoint)
            {
                case MediaEndpointKind.Meta:
                    await MetaAsync(context, layer, media).ConfigureAwait(false);
                    return;

                case MediaEndpointKind.Still:
                case MediaEndpointKind.Thumb:
                    if (media.Kind != MediaKind.Still)
                    {
                        await WrongKindAsync(context, media, endpoint).ConfigureAwait(false);
                        return;
                    }

                    await StillAsync(context, layer, media, isThumb: endpoint == MediaEndpointKind.Thumb).ConfigureAwait(false);
                    return;

                default:
                    if (media.Kind != MediaKind.Video)
                    {
                        await WrongKindAsync(context, media, endpoint).ConfigureAwait(false);
                        return;
                    }

                    await VideoAsync(context, media).ConfigureAwait(false);
                    return;
            }
        }
        catch (StillDecodeException ex)
        {
            // § 5.5: the file exists but is not a decodable image. § 11.3 is the reason this does
            // not also drop the record: "The server MUST NOT mutate session state from a GET."
            //
            // AUDIT2.md § 2.1: a StillDecodeException raised because DecodeRoom/DecodeBudget had no
            // room for this decode's peak — never because the bytes on disk were bad — carries
            // IsCapacityRefusal. The wire contract does not change for it: still 422
            // media_decode_failed, still details: { id } (SERVER_SPEC.md § 5.2's row for this code).
            // Only error.message differs, and SERVER_SPEC.md § 4 says that field is free to: "Human
            // English. Unstable. Never parsed." So this needs no contract change — the file itself
            // was never the problem, and the owner must not be told it was damaged.
            layer.Logger.LogInformation(ex, "Could not decode {Id}.", media.Id);
            var message = ex.IsCapacityRefusal
                ? "The file is present and is not damaged, but the server did not have enough free decode memory for it right now — try again in a moment."
                : "The file is present but could not be decoded as an image.";
            await MediaHttp.WriteErrorAsync(
                context,
                ErrorCodes.MediaDecodeFailed,
                message,
                new { id = media.Id },
                cancellationToken: context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Lost between the stat in the resolver and the read here.
            await MediaHttp.WriteErrorAsync(
                context,
                ErrorCodes.MediaFileMissing,
                "The file for this id has gone from disk.",
                new { id = media.Id },
                cancellationToken: context.RequestAborted).ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------- meta

    private static async Task MetaAsync(HttpContext context, MediaLayer layer, ResolvedMedia media)
    {
        int? width = null;
        int? height = null;

        if (media.Kind == MediaKind.Still)
        {
            // § 12.1: a header read, not a full decode, and the dimensions are the ones after
            // EXIF orientation. A failure here is 422, not 500.
            var dimensions = layer.Renderer.Identify(media.Path);
            width = dimensions.Width;
            height = dimensions.Height;
        }

        var record = media.Record;
        var body = new
        {
            id = media.Id,
            kind = media.Kind == MediaKind.Still ? "still" : "video",
            sizeBytes = media.SizeBytes,
            modifiedAt = media.ModifiedUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            mediaVersion = media.MediaVersion,
            width,
            height,
            rating = new
            {
                mu = record.Rating.Mu,
                sigma = record.Rating.Sigma,
                conservative = record.Rating.ConservativeScore,
            },
            matches = record.Matches,
            impressions = record.Impressions,
            lastPlayed = record.LastPlayed,
            rankable = record.Kind == media.Session.Policy,
        };

        // § 12.5.5: meta is JSON, so no-store, and it is never cached.
        context.Response.Headers.CacheControl = MediaHttp.CacheControlNoStore;
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";

        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(body, Json.Value);
        context.Response.ContentLength = json.Length;
        if (HttpMethods.IsHead(context.Request.Method))
            return;

        await context.Response.Body.WriteAsync(json, context.RequestAborted).ConfigureAwait(false);
    }

    // --------------------------------------------------------------- still

    private static async Task StillAsync(HttpContext context, MediaLayer layer, ResolvedMedia media, bool isThumb)
    {
        var parsed = StillVariantParser.Parse(context.Request.Query, context.Request.Headers.Accept, isThumb);
        if (!parsed.Ok)
        {
            if (parsed.Fault == VariantFault.UnsupportedWidth)
            {
                await MediaHttp.WriteErrorAsync(
                    context,
                    ErrorCodes.UnsupportedWidth,
                    "w must be one of the allowed widths.",
                    new { requested = parsed.RequestedRawWidth, allowed = StillWidths.Allowed },
                    cancellationToken: context.RequestAborted).ConfigureAwait(false);
            }
            else
            {
                await MediaHttp.WriteErrorAsync(
                    context,
                    ErrorCodes.UnsupportedFormat,
                    "format must be jpeg or webp.",
                    new { allowed = new[] { "jpeg", "webp" } },
                    cancellationToken: context.RequestAborted).ConfigureAwait(false);
            }

            return;
        }

        var variant = parsed.Variant;
        var varyOnAccept = variant.Source == FormatSource.FromAccept;
        var etag = media.Fingerprint.ETag(variant.Token);

        if (MediaHttp.IfNoneMatchHits(context, etag))
        {
            await MediaHttp.WriteNotModifiedAsync(context, etag, varyOnAccept).ConfigureAwait(false);
            return;
        }

        var quality = variant.Format == StillFormat.Jpeg ? layer.Options.JpegQuality : layer.Options.WebpQuality;
        var key = media.Fingerprint.EntityHex + "-" + variant.Token + "-q" + quality.ToString(CultureInfo.InvariantCulture);

        var cached = await layer.Cache.GetOrAddAsync(
            key,
            variant.FileExtension,
            media.Session.Folder,
            stream => layer.Renderer.RenderAsync(media.Path, variant, stream, context.RequestAborted),
            context.RequestAborted).ConfigureAwait(false);

        MediaHttp.ApplyByteCacheHeaders(context, etag, varyOnAccept);
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = variant.ContentType;

        if (cached is null)
        {
            // The cache declined to write, because its root sits inside the media folder. Still
            // the right bytes, just slower and without a Content-Length.
            if (HttpMethods.IsHead(context.Request.Method))
                return;

            AllowSynchronousEncodeWrites(context);
            await layer.Renderer.RenderAsync(media.Path, variant, context.Response.Body, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        try
        {
            var length = new FileInfo(cached).Length;
            context.Response.ContentLength = length;
            if (HttpMethods.IsHead(context.Request.Method))
                return;

            var bodyFeature = context.Features.Get<IHttpResponseBodyFeature>();
            if (bodyFeature is not null)
                await bodyFeature.SendFileAsync(cached, 0, length, context.RequestAborted).ConfigureAwait(false);
            else
                await context.Response.SendFileAsync(cached, context.RequestAborted).ConfigureAwait(false);
            return;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // § 3.12 (second audit): StillCache.GetOrAddAsync writes the entry, runs its own
            // eviction, and only then returns the path — so its own eviction (racing against a
            // concurrent write elsewhere, or, deterministically, a CacheMaxBytes bound smaller
            // than the entry it just wrote) can take the file back off disk before we get to stat
            // or send it. Nothing is wrong with the photograph; only the cached copy of it is
            // gone. Answering 404 media_file_missing here would name a file that is sitting right
            // there on disk — so this is the cache-declined case above, not a missing-file case:
            // fall back to rendering it directly, the same way a cache root inside the media
            // folder already does. Nothing has been written to the response body yet — the two
            // points that can throw here (the stat, and SendFileAsync's own open) both happen
            // before the first byte would go out — so it is still safe to change course.
            layer.Logger.LogDebug(
                ex,
                "Still cache entry for {Id} disappeared between hand-off and send; re-rendering instead of answering media_file_missing.",
                media.Id);

            context.Response.ContentLength = null;
            if (HttpMethods.IsHead(context.Request.Method))
                return;

            AllowSynchronousEncodeWrites(context);
            await layer.Renderer.RenderAsync(media.Path, variant, context.Response.Body, context.RequestAborted).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Found while wiring up § 3.12's fallback, not one of the numbered findings itself: rendering
    /// straight to <c>context.Response.Body</c> — both here and in the pre-existing "cache declined
    /// to write" branch above — has SkiaSharp encode synchronously into whatever
    /// <see cref="StillRenderer.RenderAsync"/> is handed, and ASP.NET Core refuses a synchronous
    /// <c>Stream.Write</c> on the response body by default (Kestrel and <c>TestServer</c> alike),
    /// throwing <see cref="InvalidOperationException"/> instead of a picture. Both direct-render
    /// fallbacks are for exactly the cases where the disk cache could not help, so crashing there
    /// is strictly worse than the thing being worked around. This is the documented escape hatch,
    /// scoped to the one response it is called on.
    /// </summary>
    private static void AllowSynchronousEncodeWrites(HttpContext context)
    {
        var feature = context.Features.Get<IHttpBodyControlFeature>();
        if (feature is not null)
            feature.AllowSynchronousIO = true;
    }

    // --------------------------------------------------------------- video

    private static async Task VideoAsync(HttpContext context, ResolvedMedia media)
    {
        var etag = media.Fingerprint.ETag("orig");

        if (MediaHttp.IfNoneMatchHits(context, etag))
        {
            context.Response.Headers.AcceptRanges = "bytes";
            await MediaHttp.WriteNotModifiedAsync(context, etag, varyOnAccept: false).ConfigureAwait(false);
            return;
        }

        MediaHttp.ApplyByteCacheHeaders(context, etag, varyOnAccept: false);
        await VideoStreamer.WriteAsync(context, media.Path, media.SizeBytes, etag, context.RequestAborted).ConfigureAwait(false);
    }

    // ---------------------------------------------------------- diagnostics

    private static Task WrongKindAsync(HttpContext context, ResolvedMedia media, MediaEndpointKind endpoint)
    {
        // § 5.5 fixes details as { id, kind, endpoint }. "kind" here is the media's own kind — the
        // fact the client got wrong — not the kind the endpoint wanted.
        var name = endpoint switch
        {
            MediaEndpointKind.Still => "still",
            MediaEndpointKind.Thumb => "thumb",
            MediaEndpointKind.Video => "video",
            _ => "meta",
        };

        var message = media.Kind == MediaKind.Video
            ? "This id is a video; there is no still and no poster frame for a video."
            : "This id is a still, not a video.";

        return MediaHttp.WriteErrorAsync(
            context,
            ErrorCodes.WrongMediaKind,
            message,
            new { id = media.Id, kind = media.Kind == MediaKind.Still ? "still" : "video", endpoint = name },
            cancellationToken: context.RequestAborted);
    }

    private static Task WriteResolutionFailureAsync(HttpContext context, MediaResolveResult result) =>
        result.Resolution switch
        {
            MediaResolution.NoSession => MediaHttp.WriteErrorAsync(
                context,
                ErrorCodes.NoSession,
                "No session is open. There is no way to fetch media bytes without one.",
                cancellationToken: context.RequestAborted),

            MediaResolution.InvalidId => MediaHttp.WriteErrorAsync(
                context,
                ErrorCodes.InvalidMediaId,
                "The id segment is not a legal media id.",
                new { reason = result.Reason ?? "invalid" },
                cancellationToken: context.RequestAborted),

            MediaResolution.OutsideSession => MediaHttp.WriteErrorAsync(
                context,
                ErrorCodes.MediaOutsideSession,
                "That id would leave the session folder.",
                new { id = result.RequestedId },
                cancellationToken: context.RequestAborted),

            MediaResolution.ExtensionNotAllowed => MediaHttp.WriteErrorAsync(
                context,
                ErrorCodes.MediaExtensionNotAllowed,
                "That extension is in neither the still nor the video list.",
                new { id = result.RequestedId, extension = result.Reason ?? "" },
                cancellationToken: context.RequestAborted),

            MediaResolution.UnknownId => MediaHttp.WriteErrorAsync(
                context,
                ErrorCodes.UnknownMediaId,
                "That id is not a record in the open session.",
                new { id = result.RequestedId },
                cancellationToken: context.RequestAborted),

            MediaResolution.FileMissing => MediaHttp.WriteErrorAsync(
                context,
                ErrorCodes.MediaFileMissing,
                "That id is a record, but the file has gone from disk.",
                new { id = result.RequestedId },
                cancellationToken: context.RequestAborted),

            _ => MediaHttp.WriteErrorAsync(
                context,
                ErrorCodes.InternalError,
                "The request could not be completed.",
                cancellationToken: context.RequestAborted),
        };

    /// <summary>
    /// The <c>{id}</c> segment exactly as it arrived on the wire.
    /// <para/>
    /// Routing hands back a segment the framework has already percent-decoded, and § 11.1.2 says
    /// the server decodes <b>exactly once</b> — decoding that string again would be twice, and
    /// taking it as-is loses the one distinction § 11.1.5 turns on, between a literal separator
    /// and a <c>%2F</c>. So the raw target is the source of truth, and the route value is only the
    /// fallback for a host that does not expose one.
    /// </summary>
    internal static string RawIdSegment(HttpContext context)
    {
        var raw = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
        if (!string.IsNullOrEmpty(raw))
        {
            var path = raw.AsSpan();
            var query = path.IndexOf('?');
            if (query >= 0)
                path = path[..query];

            // § 3.11 (second audit): routing tolerates a trailing slash and still matches
            // /media/{id}/{verb} with the intended id, but this hand-rolled split did not — the
            // path split on that slash left a trailing empty segment, so segments[^2] named the
            // verb ("still") instead of the id. Routing's own tolerance is the contract here, so
            // trim the same trailing slash(es) before splitting, the same way routing effectively
            // does.
            path = path.TrimEnd('/');

            // Expect /api/v1/media/<id>/<verb>: the id is the second-to-last segment.
            var segments = path.TrimStart('/').ToString().Split('/');
            if (segments.Length >= 2 && segments[^2].Length > 0)
                return segments[^2];
        }

        return context.Request.RouteValues.TryGetValue("id", out var value) && value is string s ? s : "";
    }
}
