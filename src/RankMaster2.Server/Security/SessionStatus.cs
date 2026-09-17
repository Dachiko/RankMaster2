namespace RankMaster2.Server.Security;

/// <summary>
/// The <c>session</c> block of <c>GET /ping</c> (SERVER_SPEC.md § 14). Null fields are written as
/// null, never omitted — openapi.yaml marks every member required.
/// </summary>
public sealed record SessionStatus(bool Open, string? SessionId, string? Folder, string? State)
{
    public static readonly SessionStatus Closed = new(false, null, null, null);
}

/// <summary>
/// How <c>/ping</c> learns whether a session is open without taking the session semaphore.
///
/// <para>
/// <c>/ping</c> is documented as cheap and safe to poll, so it must never queue behind a vote that
/// is mid-save. The session layer owns the real answer and implements this interface directly
/// (<c>Sessions.SessionRegistry</c>, registered by <c>Rm2Host</c>); a host that maps no session
/// routes at all gets <see cref="ClosedSessionStatusProvider"/>.
/// </para>
///
/// <para>
/// There used to be a third implementation here that found the registry by reflecting on a type
/// name in this same assembly, so that this folder would not have to reference the next one. It
/// could read only the open folder, which is why <c>sessionId</c> and <c>state</c> were <c>null</c>
/// on every authenticated ping while § 14 and <c>openapi.yaml</c> promised both (A4, C15) — and the
/// first rename of a property would have turned the whole block permanently closed, silently. A
/// <c>using</c> is cheaper than that.
/// </para>
/// </summary>
public interface ISessionStatusProvider
{
    SessionStatus Current { get; }
}

/// <summary>The answer when nothing in this application owns a session at all.</summary>
internal sealed class ClosedSessionStatusProvider : ISessionStatusProvider
{
    public SessionStatus Current => SessionStatus.Closed;
}
