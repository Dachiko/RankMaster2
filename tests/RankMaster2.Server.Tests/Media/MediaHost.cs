using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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

    public MediaHost(IMediaSessionAccessor sessions, MediaOptions? options = null)
    {
        options ??= new MediaOptions
        {
            CacheDirectory = Path.Combine(Path.GetTempPath(), "rm2-host-cache-" + Guid.NewGuid().ToString("N")[..8]),
        };

        CacheDirectory = options.ResolvedCacheDirectory();

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(sessions);
        builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(options));

        var app = builder.Build();
        app.MapMediaEndpoints();

        _host = app;
        app.StartAsync().GetAwaiter().GetResult();
        Client = app.GetTestClient();
    }

    public HttpClient Client { get; }

    public string CacheDirectory { get; }

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
