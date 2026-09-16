using System.Net.Http;
using System.Text.Json;
using RankMaster2.Pc.Link.Wire;

namespace RankMaster2.Pc.Link.Transport;

/// <summary>
/// A request built exactly once and sent up to twice (§ 5.1.1 steps 3-5). Every factory below calls
/// <c>JsonSerializer.SerializeToUtf8Bytes</c> exactly once and freezes
/// the result as <see cref="Body"/> before the first send. <c>FrozenRequest.cs</c> is the only type
/// in <c>Link/</c> (besides <c>Enroller.cs</c>'s <c>POST /pair</c> and <c>Credential.cs</c>'s
/// <c>link.json</c>) that turns a request record into bytes, and it does so once per instance
/// (acceptance gate item 3): a reviewer looking for a double vote has one function to read.
/// <para/>
/// <see cref="PairToken"/> and <see cref="ClientRequestId"/> are carried alongside the already-frozen
/// body only so <c>classify()</c> (§ 8.5) can compare against <c>lastAction</c> without re-parsing the
/// body. They are null for requests that carry neither (undo, save, GET, DELETE, POST /pair).
/// </summary>
internal sealed record FrozenRequest(
    HttpMethod Method,
    string Path,
    byte[] Body,
    string? PairToken,
    string? ClientRequestId,
    bool Authenticate = true)
{
    public static FrozenRequest Get(string path) =>
        new(HttpMethod.Get, path, [], null, null);

    public static FrozenRequest Delete(string path) =>
        new(HttpMethod.Delete, path, [], null, null);

    public static FrozenRequest OpenSession(string folder)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(
            new OpenSessionRequest(folder), WireJsonContext.Default.OpenSessionRequest);
        return new FrozenRequest(HttpMethod.Post, "/session", body, null, null);
    }

    public static FrozenRequest Vote(string pairToken, string winner, string clientRequestId)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(
            new VoteRequest(pairToken, winner, clientRequestId), WireJsonContext.Default.VoteRequest);
        return new FrozenRequest(HttpMethod.Post, "/session/vote", body, pairToken, clientRequestId);
    }

    public static FrozenRequest Skip(string pairToken, string clientRequestId)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(
            new SkipRequest(pairToken, clientRequestId), WireJsonContext.Default.SkipRequest);
        return new FrozenRequest(HttpMethod.Post, "/session/skip", body, pairToken, clientRequestId);
    }

    public static FrozenRequest Discard(string pairToken, string side, string clientRequestId)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(
            new SideActionRequest(pairToken, side, clientRequestId), WireJsonContext.Default.SideActionRequest);
        return new FrozenRequest(HttpMethod.Post, "/session/discard", body, pairToken, clientRequestId);
    }

    public static FrozenRequest Special(string pairToken, string side, string clientRequestId)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(
            new SideActionRequest(pairToken, side, clientRequestId), WireJsonContext.Default.SideActionRequest);
        return new FrozenRequest(HttpMethod.Post, "/session/special", body, pairToken, clientRequestId);
    }

    public static FrozenRequest Undo(string clientRequestId)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(
            new CancelRequest(clientRequestId), WireJsonContext.Default.CancelRequest);
        return new FrozenRequest(HttpMethod.Post, "/session/undo", body, null, clientRequestId);
    }

    public static FrozenRequest Save()
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new SaveRequest(), WireJsonContext.Default.SaveRequest);
        return new FrozenRequest(HttpMethod.Post, "/session/save", body, null, null);
    }

    /// <summary>Unauthenticated (§ 3): the bearer token does not exist yet. Never retried (§ 13.3):
    /// the code is single-use, so the caller must set <c>Authenticate = false</c> and never resend
    /// this instance.</summary>
    public static FrozenRequest Pair(string code, string deviceName)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(
            new PairRequest(code, deviceName), WireJsonContext.Default.PairRequest);
        return new FrozenRequest(HttpMethod.Post, "/pair", body, null, null, Authenticate: false);
    }
}
