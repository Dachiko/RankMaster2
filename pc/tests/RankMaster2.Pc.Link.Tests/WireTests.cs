using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RankMaster2.Pc.Link;
using RankMaster2.Pc.Link.Tests.Fixtures;
using RankMaster2.Pc.Link.Tests.Harness;
using RankMaster2.Pc.Link.Transport;
using RankMaster2.Pc.Link.Wire;
using Xunit;

namespace RankMaster2.Pc.Link.Tests;

[Collection("RealServer")]
public sealed class WireTests(RealServer server)
{
    // W1: Snapshot parses the live POST /session body; every field present with sane defaults.
    [Fact]
    public async Task W1_SnapshotParsesTheLiveOpenSessionBody()
    {
        using var scratch = new ScratchFolder(4);
        var (link, _) = LinkFactory.Build(server);
        await using var linkScope = link;

        var connect = await link.ConnectAsync();
        Assert.IsType<ConnectResult.Connected>(connect);

        var opened = await link.OpenAsync(scratch.Path);
        var snapshot = Assert.IsType<OpenResult.Opened>(opened).Snapshot;

        Assert.NotEmpty(snapshot.SessionId);
        Assert.Equal("ranking", snapshot.State);
        Assert.NotEmpty(snapshot.Folder);
        Assert.NotEmpty(snapshot.FolderName);
        Assert.NotEmpty(snapshot.Policy);
        Assert.NotEmpty(snapshot.OpenedAt);
        Assert.True(snapshot.PrefetchPairs > 0);
        Assert.Equal(0, snapshot.SessionVotes);
        Assert.Equal(0L, snapshot.PairSeq);
        Assert.Null(snapshot.LastAction);
        Assert.Null(snapshot.LastSavedAt);
        Assert.Empty(snapshot.Cues);
        Assert.NotNull(snapshot.Pair);
        Assert.NotNull(snapshot.PairToken);
        Assert.True(snapshot.WarmPairs.Count <= snapshot.PrefetchPairs);
        Assert.False(snapshot.UndoAvailable);

        await link.CloseAsync();
    }

    // W2: an unknown extra field does not stop the snapshot parsing.
    [Fact]
    public async Task W2_UnknownExtraFieldStillParses()
    {
        using var scratch = new ScratchFolder(4);
        var device = await server.PairSecondDeviceAsync("w2-device");
        using var http = device.CreateClient();

        await http.DeleteAsync(server.BaseUrl + "/session"); // clean slate: another test may have left a session open

        var openBody = new StringContent("{\"folder\":" + JsonSerializer.Serialize(scratch.Path) + "}", Encoding.UTF8, "application/json");
        var response = await http.PostAsync(server.BaseUrl + "/session", openBody);
        var raw = await response.Content.ReadAsByteArrayAsync();

        var node = JsonNode.Parse(raw)!.AsObject();
        node["somethingFromTheFuture"] = "a field this client has never heard of";
        var mutated = Encoding.UTF8.GetBytes(node.ToJsonString());

        var snapshot = JsonSerializer.Deserialize(mutated, WireJsonContext.Default.Snapshot);
        Assert.NotNull(snapshot);
        Assert.Equal("ranking", snapshot!.State);

        await http.DeleteAsync(server.BaseUrl + "/session"); // leave a clean slate for the next test
    }

    // W3: the § 4 envelope parses field by field.
    [Fact]
    public async Task W3_ErrorEnvelopeParsesFieldByFieldDespiteGarbage()
    {
        var tap = new TapHandler(server);
        var handler = tap.Wrap(new SocketFallbackHandler());
        using var http = new Rm2Http(server.BaseUrl, handler, log: null);

        tap.Script(HttpMethod.Get, "/session",
            ScriptedFault.Fabricate(409,
                """{"error":{"code":"stale_pair_token","message":"nope","requestId":"abc","session":"garbage-not-an-object"}}"""));

        var reply = await http.Send(FrozenRequest.Get("/session"), TimeSpan.FromSeconds(5), CancellationToken.None);
        var refused = Assert.IsType<Reply.Refused>(reply);
        Assert.Equal("stale_pair_token", refused.Code);
        Assert.Equal("nope", refused.Message);
        Assert.Null(refused.Session);

        tap.Script(HttpMethod.Get, "/session", ScriptedFault.Fabricate(404, ""));
        var reply2 = await http.Send(FrozenRequest.Get("/session"), TimeSpan.FromSeconds(5), CancellationToken.None);
        var refused2 = Assert.IsType<Reply.Refused>(reply2);
        Assert.Equal(Codes.ClientMalformedError, refused2.Code);
    }

    // W4: Ping parses public and authenticated bodies, Session null and non-null.
    [Fact]
    public async Task W4_PingParsesPublicAndAuthenticatedBodies()
    {
        var tap = new TapHandler(server);
        var handler = tap.Wrap(new SocketFallbackHandler());
        using var http = new Rm2Http(server.BaseUrl, handler, log: null);

        tap.Script(HttpMethod.Get, "/ping", ScriptedFault.Fabricate(200,
            """{"product":"x","apiVersion":"v1","version":"1","ready":true,"authenticated":false,"certificateFingerprint":"sha256:aa","serverTime":"2026-01-01T00:00:00Z","session":null}"""));
        var reply = await http.Send(FrozenRequest.Get("/ping"), TimeSpan.FromSeconds(5), CancellationToken.None);
        var ok = Assert.IsType<Reply.Ok>(reply);
        var ping = JsonSerializer.Deserialize(ok.Body, WireJsonContext.Default.Ping);
        Assert.NotNull(ping);
        Assert.False(ping!.Authenticated);
        Assert.Null(ping.Session);

        tap.Script(HttpMethod.Get, "/ping", ScriptedFault.Fabricate(200,
            """{"product":"x","apiVersion":"v1","version":"1","ready":true,"authenticated":true,"certificateFingerprint":"sha256:aa","serverTime":"2026-01-01T00:00:00Z","session":{"open":true,"sessionId":"s1","folder":"/tmp/x","state":"ranking"}}"""));
        var reply2 = await http.Send(FrozenRequest.Get("/ping"), TimeSpan.FromSeconds(5), CancellationToken.None);
        var ok2 = Assert.IsType<Reply.Ok>(reply2);
        var ping2 = JsonSerializer.Deserialize(ok2.Body, WireJsonContext.Default.Ping);
        Assert.NotNull(ping2!.Session);
        Assert.True(ping2.Session!.Open);
        Assert.Equal("ranking", ping2.Session.State);
    }

    // W5: every request body type serialises to exactly the field set § 10 / openapi.yaml lists.
    [Theory]
    [InlineData("OpenSessionRequest")]
    [InlineData("VoteRequest")]
    [InlineData("SkipRequest")]
    [InlineData("SideRequest")]
    [InlineData("UndoRequest")]
    [InlineData("PairRequest")]
    public void W5_RequestBodyFieldSetsMatchOpenApi(string schemaName)
    {
        var expected = OpenApiSchema.PropertyNames(schemaName);

        var actual = schemaName switch
        {
            "OpenSessionRequest" => FieldsOf(new OpenSessionRequest("/tmp/x"), WireJsonContext.Default.OpenSessionRequest),
            "VoteRequest" => FieldsOf(new VoteRequest("tok", "left", "id"), WireJsonContext.Default.VoteRequest),
            "SkipRequest" => FieldsOf(new SkipRequest("tok", "id"), WireJsonContext.Default.SkipRequest),
            "SideRequest" => FieldsOf(new SideActionRequest("tok", "left", "id"), WireJsonContext.Default.SideActionRequest),
            "UndoRequest" => FieldsOf(new CancelRequest("id"), WireJsonContext.Default.CancelRequest),
            "PairRequest" => FieldsOf(new PairRequest("123456", "device"), WireJsonContext.Default.PairRequest),
            _ => throw new ArgumentOutOfRangeException(nameof(schemaName)),
        };

        Assert.Equal(expected.OrderBy(x => x), actual.OrderBy(x => x));
    }

    private static HashSet<string> FieldsOf<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        using var document = JsonDocument.Parse(bytes);
        return document.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();
    }

    /// <summary>A handler that only ever runs if a test forgets to script a route — fails loudly
    /// rather than silently hitting the network.</summary>
    private sealed class SocketFallbackHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException($"Unscripted request reached the network: {request.Method} {request.RequestUri}");
    }
}
