using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RankMaster2.Server.Media;

namespace RankMaster2.Server.Tests.Media;

/// <summary>
/// A host with the media endpoints and nothing else.
/// <para/>
/// Deliberately not <c>WebApplicationFactory&lt;Program&gt;</c>: the real <c>Program.cs</c> also
/// installs auth, sessions and TLS, and a media test that fails because a token was missing tells
/// you nothing about the media layer. Booting <see cref="MediaEndpoints.MapMediaEndpoints"/> alone
/// also proves the claim the layer makes about itself — that one call in <c>Program.cs</c> is all
/// it needs, with no registrations anywhere else.
/// </summary>
public sealed class MediaHost : IAsyncDisposable
{
    private readonly IHost _host;

    /// <param name="sessions">The session accessor the media layer will call.</param>
    /// <param name="options">
    /// Left null for a throwaway cache directory under the temp folder, exactly as before. Pass an
    /// instance with <see cref="MediaOptions.CacheDirectory"/> left unset to exercise the § 3.5
    /// default-resolution path instead.
    /// </param>
    /// <param name="configuration">
    /// Config keys to seed <c>builder.Configuration</c> with before the host starts — e.g.
    /// <c>RankMaster2:DataDirectory</c>, for § 3.5. <c>TestServer</c> never populates
    /// <see cref="IHttpRequestFeature.RawTarget"/> on its own (which is exactly why § 3.11 was
    /// "untested by construction" per the second audit), so this host fills it in from the request
    /// path and query string the same way a real Kestrel server would, letting
    /// <c>MediaEndpoints.RawIdSegment</c> be exercised for real over HTTP.
    /// </param>
    /// <param name="renderer">
    /// Registered ahead of <c>MapMediaEndpoints</c> so it, rather than a throwaway
    /// <c>new StillRenderer(options)</c>, is what every request through <see cref="Client"/> shares —
    /// the point being that the caller keeps its own reference and can read
    /// <see cref="StillRenderer.Budget"/> / <see cref="StillRenderer.Room"/> after the requests it
    /// made are done (AUDIT2.md § 2.1's concurrent-pair tests need exactly this).
    /// </param>
    public MediaHost(
        IMediaSessionAccessor sessions,
        MediaOptions? options = null,
        IEnumerable<KeyValuePair<string, string?>>? configuration = null,
        StillRenderer? renderer = null)
    {
        options ??= new MediaOptions
        {
            CacheDirectory = Path.Combine(Path.GetTempPath(), "rm2-host-cache-" + Guid.NewGuid().ToString("N")[..8]),
        };

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(sessions);

        if (renderer is not null)
            builder.Services.AddSingleton(renderer);

        if (configuration is not null)
            builder.Configuration.AddInMemoryCollection(configuration);

        // Resolve the default cache directory the same way MapMediaEndpoints will (§ 3.5), before
        // StillCache is built below, so CacheDirectory and Cache both reflect where the layer
        // actually ends up writing rather than the pre-resolution guess.
        MediaEndpoints.ApplyConfiguredCacheDirectoryDefault(options, builder.Configuration);

        builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(options));

        Cache = new StillCache(options, NullLogger<StillCache>.Instance);
        builder.Services.AddSingleton(Cache);

        var app = builder.Build();

        app.Use(async (context, next) =>
        {
            var feature = context.Features.Get<IHttpRequestFeature>();
            if (feature is not null && string.IsNullOrEmpty(feature.RawTarget))
                feature.RawTarget = context.Request.Path + context.Request.QueryString;
            await next().ConfigureAwait(false);
        });

        app.MapMediaEndpoints();

        _host = app;
        app.StartAsync().GetAwaiter().GetResult();
        Client = app.GetTestClient();

        CacheDirectory = options.ResolvedCacheDirectory();
    }

    public HttpClient Client { get; }

    public string CacheDirectory { get; }

    /// <summary>
    /// The same <see cref="StillCache"/> instance <see cref="MediaEndpoints.MapMediaEndpoints"/>
    /// picked up from the container, exposed so a test can wait on <see cref="StillCache.WhenScanned"/>
    /// before relying on cache behaviour, or inspect it directly.
    /// </summary>
    public StillCache Cache { get; }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _host.StopAsync();
        _host.Dispose();

        try
        {
            if (Directory.Exists(CacheDirectory))
                Directory.Delete(CacheDirectory, recursive: true);
        }
        catch
        {
            // A leftover temp directory is not a test failure.
        }
    }
}

/// <summary>A session that is simply not open — the default the media layer ships with.</summary>
public sealed class NoSession : IMediaSessionAccessor
{
    public IMediaSessionView? Current => null;
}

/// <summary>
/// A real folder on disk with real files in it, standing in for an open session. The media layer
/// only ever asks three things of a session, so the stub is three things wide.
/// </summary>
public sealed class StubSession : IMediaSessionAccessor, IMediaSessionView, IDisposable
{
    private readonly Dictionary<string, MediaRecord> _records;

    public StubSession(MediaKind policy = MediaKind.Still)
    {
        Folder = Path.Combine(Path.GetTempPath(), "rm2-session-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Folder);
        Policy = policy;
        _records = new Dictionary<string, MediaRecord>(MediaSessionView.IdComparer);
    }

    public string Folder { get; }

    public MediaKind Policy { get; }

    public IMediaSessionView? Current => this;

    /// <summary>Writes a file into the folder and adds it to the record set.</summary>
    public string Add(string id, byte[] bytes, int matches = 0, int impressions = 0, long lastPlayed = 0)
    {
        var path = Path.Combine(Folder, id);
        File.WriteAllBytes(path, bytes);
        Track(id, matches, impressions, lastPlayed);
        return path;
    }

    /// <summary>Adds a record without writing the file — the "deleted under the session" case.</summary>
    public void Track(string id, int matches = 0, int impressions = 0, long lastPlayed = 0)
    {
        var kind = MediaExtensions.KindOf(id) ?? MediaKind.Still;
        _records[id] = new MediaRecord(new MediaId(id), kind, new Rating(25.0, 8.333), matches, impressions, lastPlayed);
    }

    /// <summary>Writes a file into a subfolder, which is never a record and never reachable.</summary>
    public string AddUnreachable(string relative, byte[] bytes)
    {
        var path = Path.Combine(Folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public void Forget(string id) => _records.Remove(id);

    public bool TryFindRecord(string id, [MaybeNullWhen(false)] out MediaRecord record) =>
        _records.TryGetValue(id, out record);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Folder))
                Directory.Delete(Folder, recursive: true);
        }
        catch
        {
            // A leftover temp directory is not a test failure.
        }
    }
}

/// <summary>
/// An <see cref="IMediaSessionAccessor"/> whose <see cref="Current"/> can be swapped at will —
/// standing in for a server whose one open session moves from one folder to another, without
/// tearing down and rebuilding the <see cref="MediaHost"/> (and, with it, its <see cref="StillCache"/>)
/// in between. Second audit § 1.2: proving the disk cache does not collide across folders needs the
/// <em>same</em> cache serving both.
/// </summary>
public sealed class SwitchableSession : IMediaSessionAccessor
{
    public IMediaSessionView? Current { get; set; }
}
