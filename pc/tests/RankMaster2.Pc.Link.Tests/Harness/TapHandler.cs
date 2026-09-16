using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using System.Text;
using RankMaster2.Pc.Link.Transport;

namespace RankMaster2.Pc.Link.Tests.Harness;

/// <summary>One request the link sent, as observed at the seam it owns — its
/// <see cref="System.Net.Http.HttpMessageHandler"/>.</summary>
public sealed record SentRequest(string Method, string Path, byte[] Body, bool BearerPresent, string? Bearer, DateTimeOffset At)
{
    public string BodyText => Encoding.UTF8.GetString(Body);
}

public enum FaultKind { Forward, DropBeforeForward, ForwardThenDropResponse, ForwardThenKillServer, Fabricate, ThrowPinMismatch, Delay }

public sealed record ScriptedFault
{
    public FaultKind Kind { get; }
    public int Status { get; init; }
    public string Json { get; init; } = "";
    public int DelayMs { get; init; }

    private ScriptedFault(FaultKind kind) => Kind = kind;

    public static readonly ScriptedFault Forward = new(FaultKind.Forward);
    public static readonly ScriptedFault DropBeforeForward = new(FaultKind.DropBeforeForward);
    public static readonly ScriptedFault ForwardThenDropResponse = new(FaultKind.ForwardThenDropResponse);
    public static readonly ScriptedFault ForwardThenKillServer = new(FaultKind.ForwardThenKillServer);
    public static readonly ScriptedFault ThrowPinMismatch = new(FaultKind.ThrowPinMismatch);

    public static ScriptedFault Fabricate(int status, string json) => new(FaultKind.Fabricate) { Status = status, Json = json };
    public static ScriptedFault Delay(int ms) => new(FaultKind.Delay) { DelayMs = ms };
}

/// <summary>
/// Records every request the link makes, in order, and runs a per-route fault script the test sets
/// up in advance (§ 6.2). One <see cref="TapHandler"/> is created per test and its <see cref="Wrap"/>
/// is passed as the link's <c>wrap</c> hook (§ 4.3): every <c>Rm2Http</c> the link ever builds —
/// across reconnects and re-enrolments — is wrapped through the same recorder, so <see cref="Sent"/>
/// is the whole test's history regardless of how many handshakes happened.
/// <para/>
/// Every fault is injected here, at the one seam the link owns; the server on the other side is
/// always the real one, so "what the server did" is always checked against its own state.
/// </summary>
public sealed class TapHandler
{
    private readonly RealServer _server;
    private readonly object _gate = new();
    private readonly List<SentRequest> _sent = [];
    private readonly Dictionary<string, Queue<ScriptedFault>> _scripts = new();

    public TapHandler(RealServer server) => _server = server;

    public IReadOnlyList<SentRequest> Sent { get { lock (_gate) return _sent.ToList(); } }

    public void ClearSent() { lock (_gate) _sent.Clear(); }

    /// <summary>Requests to <paramref name="path"/> (short form, e.g. <c>/session/vote</c>) follow
    /// this ordered script, one entry per occurrence; once exhausted, further requests forward.</summary>
    public void Script(HttpMethod method, string path, params ScriptedFault[] faults)
    {
        lock (_gate) _scripts[Key(method, path)] = new Queue<ScriptedFault>(faults);
    }

    public void ClearScripts() { lock (_gate) _scripts.Clear(); }

    /// <summary>The link's <c>wrap</c> hook (§ 4.3): receives the pinned production handler, returns
    /// the handler to actually use.</summary>
    public HttpMessageHandler Wrap(HttpMessageHandler inner) => new Relay(this, inner);

    private static string Key(HttpMethod method, string shortPath) => method.Method + " " + shortPath;

    private async Task<HttpResponseMessage> Handle(Relay relay, HttpRequestMessage request, CancellationToken ct)
    {
        var absolutePath = request.RequestUri!.AbsolutePath;
        const string prefix = "/api/v1";
        var shortPath = absolutePath.StartsWith(prefix, StringComparison.Ordinal) ? absolutePath[prefix.Length..] : absolutePath;

        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var bearerToken = request.Headers.Authorization?.Parameter;
        var bearerPresent = request.Headers.Authorization is not null;

        lock (_gate) _sent.Add(new SentRequest(request.Method.Method, shortPath, body, bearerPresent, bearerToken, DateTimeOffset.UtcNow));

        ScriptedFault fault = ScriptedFault.Forward;
        lock (_gate)
        {
            var key = Key(request.Method, shortPath);
            if (_scripts.TryGetValue(key, out var queue) && queue.Count > 0)
                fault = queue.Dequeue();
        }

        switch (fault.Kind)
        {
            case FaultKind.Forward:
                return await relay.ForwardAsync(request, ct).ConfigureAwait(false);

            case FaultKind.DropBeforeForward:
                throw new HttpRequestException("injected: connection reset before the request reached the server");

            case FaultKind.ForwardThenDropResponse:
            {
                using var response = await relay.ForwardAsync(request, ct).ConfigureAwait(false);
                // The request landed; the client did not learn it. This is what a timeout looks like
                // from the caller's side.
                throw new TaskCanceledException("injected: response dropped after the server answered");
            }

            case FaultKind.ForwardThenKillServer:
            {
                using var response = await relay.ForwardAsync(request, ct).ConfigureAwait(false);
                _server.Kill();
                throw new TaskCanceledException("injected: server killed after it answered");
            }

            case FaultKind.Fabricate:
            {
                var message = new HttpResponseMessage((HttpStatusCode)fault.Status)
                {
                    Content = new StringContent(fault.Json, Encoding.UTF8, "application/json"),
                };
                message.Headers.TryAddWithoutValidation("X-Request-Id", "injected-" + Guid.NewGuid().ToString("N")[..12]);
                return message;
            }

            case FaultKind.ThrowPinMismatch:
                // Exactly what PinnedHandler's RemoteCertificateValidationCallback throws on a
                // mismatch, wrapped the way a real TLS handshake failure would arrive.
                throw new HttpRequestException("injected: certificate pin mismatch",
                    new AuthenticationException("The remote certificate is invalid.",
                        new PinMismatchException("sha256:" + new string('0', 64), "sha256:" + new string('f', 64))));

            case FaultKind.Delay:
                await Task.Delay(fault.DelayMs, ct).ConfigureAwait(false);
                return await relay.ForwardAsync(request, ct).ConfigureAwait(false);

            default:
                throw new InvalidOperationException("unhandled fault kind");
        }
    }

    private sealed class Relay(TapHandler owner, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            owner.Handle(this, request, ct);

        public Task<HttpResponseMessage> ForwardAsync(HttpRequestMessage request, CancellationToken ct) =>
            base.SendAsync(request, ct);
    }
}
