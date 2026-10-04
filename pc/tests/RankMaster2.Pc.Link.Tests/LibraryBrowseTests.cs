using System.Net;
using System.Net.Http;
using System.Text;
using System.Web;
using RankMaster2.Pc.Link;
using RankMaster2.Pc.Link.Enrolment;
using RankMaster2.Pc.Link.Transport;
using RankMaster2.Pc.Link.Wire;
using Xunit;

namespace RankMaster2.Pc.Link.Tests;

/// <summary>
/// Plan I stage S1: <c>GetRootsAsync</c> / <c>BrowseAsync</c> (SERVER_SPEC.md § 10.14, § 10.15).
/// Everything here is pure JSON, string building or a link talking to a scripted in-memory
/// <see cref="HttpMessageHandler"/> through the link's <c>wrap</c> hook — no TLS, no server, so these
/// pass on Windows where the real-server tests cannot complete their handshake.
/// </summary>
public sealed class LibraryBrowseTests
{
    private static byte[] Bytes(string json) => Encoding.UTF8.GetBytes(json);

    // ---- request building --------------------------------------------------------------------------

    [Theory]
    [InlineData(@"C:\images\garden")]
    [InlineData(@"C:\My Pictures\Summer 2026")]
    [InlineData(@"D:\Fotos\Österreich – Ürümqi\日本語 フォルダ")]
    [InlineData(@"E:\a&b=c#d%20e+f\x")]
    [InlineData("C:\\")]
    public void Browse_puts_the_path_in_one_encoded_query_value_and_always_asks_for_counts(string path)
    {
        var request = FrozenRequest.Browse(path);

        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Empty(request.Body);
        Assert.True(request.Authenticate);
        Assert.StartsWith("/libraries/browse?path=", request.Path);
        Assert.EndsWith("&counts=true", request.Path);

        // What the server's query parser sees: exactly two keys, and the path survives byte for byte.
        var query = new Uri("https://server" + request.Path).Query;
        var parsed = HttpUtility.ParseQueryString(query, Encoding.UTF8);
        Assert.Equal(["path", "counts"], parsed.AllKeys);
        Assert.Equal(path, parsed["path"]);
        Assert.Equal("true", parsed["counts"]);

        // Nothing unescaped that would end the value early or be read as another parameter.
        var encoded = request.Path["/libraries/browse?path=".Length..^"&counts=true".Length];
        Assert.DoesNotContain(' ', encoded);
        Assert.DoesNotContain('&', encoded);
        Assert.DoesNotContain('#', encoded);
        Assert.DoesNotContain('\\', encoded);
        Assert.All(encoded, c => Assert.True(c < 128));
    }

    [Fact]
    public void Roots_is_a_plain_authenticated_get()
    {
        var request = FrozenRequest.GetRoots();
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/libraries/roots", request.Path);
        Assert.Empty(request.Body);
        Assert.True(request.Authenticate);
    }

    // ---- JSON --------------------------------------------------------------------------------------

    [Fact]
    public void A_roots_body_parses_and_ignores_byte_counts_and_unknown_members()
    {
        var roots = SessionLink.ParseRoots(Bytes("""
            { "roots": [
              { "path": "C:\\", "label": "System", "kind": "fixed", "available": true,
                "totalBytes": 1000204886016, "freeBytes": 271234400256, "futureField": 1 },
              { "path": "D:\\", "label": null, "kind": "removable", "available": false,
                "totalBytes": null, "freeBytes": null },
              { "path": "Z:\\", "label": "nas-photos", "kind": "network", "available": false }
            ] }
            """));

        Assert.NotNull(roots);
        Assert.Equal(3, roots!.Count);
        Assert.Equal(new LibraryRoot(@"C:\", "System", "fixed", true), roots[0]);
        Assert.Equal(new LibraryRoot(@"D:\", null, "removable", false), roots[1]);
        Assert.False(roots[2].Available);
        Assert.Equal("network", roots[2].Kind);
    }

    [Fact]
    public void An_empty_roots_list_is_a_list_and_a_body_without_roots_is_not()
    {
        Assert.Empty(SessionLink.ParseRoots(Bytes("""{ "roots": [] }""")) ?? throw new Xunit.Sdk.XunitException("null"));
        Assert.Null(SessionLink.ParseRoots(Bytes("{}")));
        Assert.Null(SessionLink.ParseRoots(Bytes("[1,2]")));
        Assert.Null(SessionLink.ParseRoots(Bytes("not json")));
    }

    [Fact]
    public void A_browse_body_parses_with_rankable_present_absent_and_an_inaccessible_child()
    {
        var listing = SessionLink.ParseListing(Bytes("""
            { "path": "C:\\images", "parent": "C:\\",
              "entries": [
                { "name": "garden", "path": "C:\\images\\garden", "stillCount": 12, "videoCount": 0,
                  "rankable": true, "hasDatabase": true, "accessible": true },
                { "name": "empty", "path": "C:\\images\\empty", "stillCount": 0, "videoCount": 0,
                  "rankable": false, "hasDatabase": false, "accessible": true },
                { "name": "locked", "path": "C:\\images\\locked", "stillCount": null, "videoCount": null,
                  "rankable": null, "hasDatabase": false, "accessible": false },
                { "name": "uncounted", "path": "C:\\images\\uncounted", "hasDatabase": true, "accessible": true,
                  "somethingNew": "x" }
              ] }
            """));

        Assert.NotNull(listing);
        Assert.Equal(@"C:\images", listing!.Path);
        Assert.Equal(@"C:\", listing.Parent);
        Assert.Equal(4, listing.Entries.Count);

        Assert.Equal(new FolderEntry("garden", @"C:\images\garden", true, true, true), listing.Entries[0]);
        Assert.Equal(false, listing.Entries[1].Rankable);        // counted and not rankable: false, not unknown
        Assert.Null(listing.Entries[2].Rankable);                // explicit null: unknown
        Assert.False(listing.Entries[2].Accessible);
        Assert.Null(listing.Entries[3].Rankable);                // key absent (counts=false wire): unknown
        Assert.True(listing.Entries[3].HasDatabase);
        Assert.True(listing.Entries[3].Accessible);
    }

    [Fact]
    public void A_listing_at_a_root_has_a_null_parent_and_may_be_empty_but_must_have_entries()
    {
        var root = SessionLink.ParseListing(Bytes("""{ "path": "C:\\", "parent": null, "entries": [] }"""));
        Assert.NotNull(root);
        Assert.Null(root!.Parent);
        Assert.Empty(root.Entries);

        Assert.Null(SessionLink.ParseListing(Bytes("""{ "path": "C:\\", "parent": null }""")));
        Assert.Null(SessionLink.ParseListing(Bytes("garbage")));
    }

    // ---- the link against a scripted handler (no TLS) ------------------------------------------------

    private const string PingBody =
        """{"product":"rankmaster2","apiVersion":"1","version":"3.3.0","ready":true,"authenticated":true,"certificateFingerprint":"aa","serverTime":"2026-10-04T10:00:00.000Z","session":null}""";

    private static string Error(string code, string message = "m") =>
        "{\"error\":{\"code\":\"" + code + "\",\"message\":\"" + message + "\",\"requestId\":\"req-1\"}}";

    private sealed record Seen(string Method, string PathAndQuery, string? Bearer);

    private sealed class Script : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, int, HttpResponseMessage> _answer;
        private readonly Dictionary<string, int> _counts = [];
        public List<Seen> Sent { get; } = [];

        public Script(Func<HttpRequestMessage, int, HttpResponseMessage> answer) => _answer = answer;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Sent.Add(new Seen(request.Method.Method, request.RequestUri!.PathAndQuery, request.Headers.Authorization?.Parameter));
            var key = request.RequestUri.AbsolutePath;
            _counts[key] = _counts.GetValueOrDefault(key) + 1;
            return Task.FromResult(_answer(request, _counts[key]));
        }

        public int Count(string absolutePath) => _counts.GetValueOrDefault(absolutePath);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    /// <summary>A link with a stored credential, whose only transport is <paramref name="script"/>.</summary>
    private static (SessionLink Link, string Dir) NewLink(Script script, bool withCredential = true)
    {
        var dir = Path.Combine(Path.GetTempPath(), "rm2-browse-tests", Guid.NewGuid().ToString("N"));
        if (withCredential)
            new Credential("https://127.0.0.1:1", "aa", "tok-1", "dev-1", "2026-10-04T10:00:00Z").Save(dir);

        var options = new LinkOptions
        {
            CredentialDirectory = dir,
            ServerDataDirectory = Path.Combine(dir, "server-data"),
            StartServerIfNotRunning = false,
            ServerExecutable = null,
            DeviceName = "browse-test",
            ReadTimeout = TimeSpan.FromSeconds(2),
            OfferTimeout = TimeSpan.FromMilliseconds(150),
            ServerStartTimeout = TimeSpan.FromMilliseconds(300),
        };
        return (new SessionLink(options, wrap: _ => script), dir);
    }

    private static HttpResponseMessage Route(HttpRequestMessage request, string rootsBody, string browseBody, HttpStatusCode browseStatus = HttpStatusCode.OK) =>
        request.RequestUri!.AbsolutePath switch
        {
            "/ping" => Json(HttpStatusCode.OK, PingBody),
            "/libraries/roots" => Json(HttpStatusCode.OK, rootsBody),
            "/libraries/browse" => Json(browseStatus, browseBody),
            _ => Json(HttpStatusCode.NotFound, Error("not_found")),
        };

    [Fact]
    public async Task Roots_on_a_link_that_is_not_connected_connects_first_then_reads_with_the_stored_token()
    {
        var script = new Script((r, _) => Route(r, """{"roots":[{"path":"C:\\","label":"System","kind":"fixed","available":true}]}""", ""));
        var (link, _) = NewLink(script);
        await using var scope = link;

        Assert.Equal(LinkState.Disconnected, link.State);
        var result = await link.GetRootsAsync();

        var ok = Assert.IsType<RootsResult.Ok>(result);
        Assert.Equal(@"C:\", Assert.Single(ok.Roots).Path);
        Assert.Equal(["/ping", "/libraries/roots"], script.Sent.Select(s => s.PathAndQuery));
        Assert.All(script.Sent, s => Assert.Equal("tok-1", s.Bearer));
        Assert.Equal(LinkState.Connected, link.State);   // no session was opened or implied
        Assert.Null(link.Snapshot);
        Assert.Null(link.LastFailure);
        Assert.False(link.IsBusy);
    }

    [Fact]
    public async Task Browse_sends_the_encoded_path_with_counts_and_returns_the_listing()
    {
        const string path = @"D:\Fotos\Österreich 2026\日本語";
        var script = new Script((r, _) => Route(r, "", """
            {"path":"D:\\Fotos\\Österreich 2026\\日本語","parent":"D:\\Fotos\\Österreich 2026",
             "entries":[{"name":"a b","path":"D:\\x\\a b","rankable":true,"hasDatabase":false,"accessible":true},
                        {"name":"c","path":"D:\\x\\c","hasDatabase":false,"accessible":false}]}
            """));
        var (link, _) = NewLink(script);
        await using var scope = link;

        var result = await link.BrowseAsync(path);

        var listing = Assert.IsType<ListingResult.Ok>(result).Listing;
        Assert.Equal(2, listing.Entries.Count);
        Assert.True(listing.Entries[0].Rankable);
        Assert.Null(listing.Entries[1].Rankable);

        var browse = Assert.Single(script.Sent, s => s.PathAndQuery.StartsWith("/libraries/browse"));
        Assert.Equal("GET", browse.Method);
        var parsed = HttpUtility.ParseQueryString(new Uri("https://h" + browse.PathAndQuery).Query, Encoding.UTF8);
        Assert.Equal(path, parsed["path"]);
        Assert.Equal("true", parsed["counts"]);
    }

    [Fact]
    public async Task Browse_when_already_connected_does_not_ping_again()
    {
        var script = new Script((r, _) => Route(r, "", """{"path":"C:\\","parent":null,"entries":[]}"""));
        var (link, _) = NewLink(script);
        await using var scope = link;

        Assert.IsType<ConnectResult.Connected>(await link.ConnectAsync());
        Assert.IsType<ListingResult.Ok>(await link.BrowseAsync(@"C:\"));
        Assert.IsType<ListingResult.Ok>(await link.BrowseAsync(@"C:\"));

        Assert.Equal(1, script.Count("/ping"));
        Assert.Equal(2, script.Count("/libraries/browse"));
    }

    [Theory]
    [InlineData(404, "folder_not_found", FailureKind.FolderNotFound, "Folder not found")]
    [InlineData(400, "folder_not_a_directory", FailureKind.FolderNotADirectory, "Not a folder")]
    [InlineData(403, "folder_access_denied", FailureKind.FolderAccessDenied, "Access denied")]
    [InlineData(400, "invalid_path", FailureKind.FolderNotFound, "Folder not available")]
    public async Task A_refused_folder_becomes_a_non_fatal_failure_that_names_the_path(
        int status, string code, FailureKind kind, string title)
    {
        const string path = @"C:\images\gone";
        var script = new Script((r, _) => Route(r, "", Error(code), (HttpStatusCode)status));
        var (link, _) = NewLink(script);
        await using var scope = link;

        var result = await link.BrowseAsync(path);

        var failure = Assert.IsType<ListingResult.Failed>(result).Failure;
        Assert.Equal(kind, failure.Kind);
        Assert.Equal(title, failure.Title);
        Assert.Equal(path, failure.Detail);
        Assert.Equal(code, failure.Code);
        Assert.Equal("req-1", failure.RequestId);
        Assert.False(failure.Fatal);
        Assert.Equal(failure, link.LastFailure);
        Assert.Equal(1, script.Count("/libraries/browse"));   // a definite answer is not retried
        Assert.Equal(LinkState.Connected, link.State);        // the link is still usable
    }

    [Fact]
    public async Task An_unknown_refusal_uses_the_links_general_wording()
    {
        var script = new Script((r, _) => Route(r, "", Error("internal_error"), HttpStatusCode.InternalServerError));
        var (link, _) = NewLink(script);
        await using var scope = link;

        var failure = Assert.IsType<ListingResult.Failed>(await link.BrowseAsync(@"C:\")).Failure;
        Assert.Equal(FailureKind.Unexpected, failure.Kind);
        Assert.Equal("internal_error", failure.Code);
        Assert.False(failure.Fatal);
    }

    [Fact]
    public async Task A_2xx_body_of_the_wrong_shape_is_a_malformed_response_failure()
    {
        var script = new Script((r, _) => Route(r, """{"nope":1}""", """{"path":"C:\\"}"""));
        var (link, _) = NewLink(script);
        await using var scope = link;

        var roots = Assert.IsType<RootsResult.Failed>(await link.GetRootsAsync()).Failure;
        var browse = Assert.IsType<ListingResult.Failed>(await link.BrowseAsync(@"C:\")).Failure;
        Assert.Equal(Codes.ClientMalformedResponse, roots.Code);
        Assert.Equal(Codes.ClientMalformedResponse, browse.Code);
        Assert.False(roots.Fatal);
        Assert.False(browse.Fatal);
    }

    [Fact]
    public async Task A_lost_answer_to_a_read_is_sent_again_once()
    {
        var script = new Script((r, n) =>
            r.RequestUri!.AbsolutePath == "/libraries/browse" && n == 1
                ? throw new HttpRequestException("connection reset")
                : Route(r, "", """{"path":"C:\\","parent":null,"entries":[]}"""));
        var (link, _) = NewLink(script);
        await using var scope = link;

        Assert.IsType<ListingResult.Ok>(await link.BrowseAsync(@"C:\"));
        Assert.Equal(2, script.Count("/libraries/browse"));
    }

    [Fact]
    public async Task A_server_that_is_gone_ends_as_ServerNotRunning_when_it_cannot_be_started()
    {
        var script = new Script((r, _) => throw new HttpRequestException("connection refused"));
        var (link, _) = NewLink(script);
        await using var scope = link;

        var failure = Assert.IsType<ListingResult.Failed>(await link.BrowseAsync(@"C:\")).Failure;
        Assert.Equal(FailureKind.ServerNotRunning, failure.Kind);
        Assert.Equal(LinkState.Disconnected, link.State);
        Assert.Equal(failure, link.LastFailure);

        var rootsFailure = Assert.IsType<RootsResult.Failed>(await link.GetRootsAsync()).Failure;
        Assert.Equal(FailureKind.ServerNotRunning, rootsFailure.Kind);
    }

    [Fact]
    public async Task A_cancelled_read_returns_a_cancelled_failure_and_frees_the_link()
    {
        using var cts = new CancellationTokenSource();
        var script = new Script((r, _) =>
        {
            if (r.RequestUri!.AbsolutePath == "/libraries/browse") cts.Cancel();
            return Route(r, "", """{"path":"C:\\","parent":null,"entries":[]}""");
        });
        var (link, _) = NewLink(script);
        await using var scope = link;

        var failure = Assert.IsType<ListingResult.Failed>(await link.BrowseAsync(@"C:\", cts.Token)).Failure;
        Assert.Equal(Codes.ClientCancelled, failure.Code);
        Assert.Equal(FailureKind.Unreachable, failure.Kind);
        Assert.False(failure.Fatal);
        Assert.False(link.IsBusy);
        Assert.Equal(1, script.Count("/libraries/browse"));   // the caller's own cancel is not retried

        // And the next call works.
        Assert.IsType<ListingResult.Ok>(await link.BrowseAsync(@"C:\"));
    }

    [Fact]
    public async Task A_certificate_that_does_not_match_the_pin_is_fatal_NotYourServer()
    {
        var script = new Script((r, _) =>
            r.RequestUri!.AbsolutePath == "/ping"
                ? Json(HttpStatusCode.OK, PingBody)
                : throw new HttpRequestException(
                    "The SSL connection could not be established.",
                    new System.Security.Authentication.AuthenticationException("The remote certificate is invalid.",
                        new PinMismatchException("sha256:" + new string('0', 64), "sha256:" + new string('f', 64)))));
        var (link, _) = NewLink(script);
        await using var scope = link;

        var failure = Assert.IsType<ListingResult.Failed>(await link.BrowseAsync(@"C:\")).Failure;
        Assert.Equal(FailureKind.NotYourServer, failure.Kind);
        Assert.True(failure.Fatal);
        Assert.Equal(1, script.Count("/libraries/browse"));   // never retried toward an impostor
    }
}
