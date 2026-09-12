using System.Reflection;

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
/// is mid-save. The session layer owns the real answer; registering an implementation of this
/// interface in DI is the one line that lets it supply one. Until then
/// <see cref="ReflectiveSessionStatusProvider"/> reads the lock-free "is a folder open" field if it
/// can find it, and everything falls back to <see cref="SessionStatus.Closed"/> — a wrong "no
/// session" is a client that asks again and gets a 404, which is recoverable; a ping that blocks is
/// not.
/// </para>
/// </summary>
public interface ISessionStatusProvider
{
    SessionStatus Current { get; }
}

internal sealed class ClosedSessionStatusProvider : ISessionStatusProvider
{
    public SessionStatus Current => SessionStatus.Closed;
}

/// <summary>
/// A deliberately loose bridge to the session layer: everything is looked up by name once and any
/// failure degrades to "no session". This layer must not fail to start, or fail to answer
/// <c>/ping</c>, because a neighbouring folder was refactored.
/// </summary>
internal sealed class ReflectiveSessionStatusProvider : ISessionStatusProvider
{
    private readonly object? _registry;
    private readonly PropertyInfo? _openFolder;

    public ReflectiveSessionStatusProvider()
    {
        try
        {
            var type = typeof(ReflectiveSessionStatusProvider).Assembly
                .GetType("RankMaster2.Server.Sessions.SessionRegistry", throwOnError: false);

            _registry = type?.GetProperty("Shared", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            _openFolder = type?.GetProperty("OpenFolder", BindingFlags.Public | BindingFlags.Instance);
        }
        catch (Exception)
        {
            _registry = null;
            _openFolder = null;
        }
    }

    public SessionStatus Current
    {
        get
        {
            if (_registry is null || _openFolder is null) return SessionStatus.Closed;

            try
            {
                return _openFolder.GetValue(_registry) is string folder && folder.Length > 0
                    ? new SessionStatus(true, null, folder, null)
                    : SessionStatus.Closed;
            }
            catch (Exception)
            {
                return SessionStatus.Closed;
            }
        }
    }
}
