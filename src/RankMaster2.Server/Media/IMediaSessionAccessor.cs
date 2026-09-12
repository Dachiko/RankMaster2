using System.Diagnostics.CodeAnalysis;

namespace RankMaster2.Server.Media;

/// <summary>
/// The one seam between the media layer and the session layer.
/// <para/>
/// The media endpoints need three facts and nothing else: whether a session is open, which folder
/// it is rooted at, and whether a given filename is one of its records. They never mutate session
/// state — SERVER_SPEC.md § 11.3: "The server MUST NOT mutate session state from a <c>GET</c>."
/// <para/>
/// The session layer registers its own implementation; without one, every media request answers
/// <c>404 no_session</c>, which is the honest answer when nothing can tell the media layer what is
/// open. Implementations MUST be safe to call from several request threads at once.
/// <para/>
/// <b>Wiring this from the session layer.</b> The media layer needs a folder, a policy and a
/// record lookup, and takes them without holding the session gate — media is a <c>GET</c> and
/// § 11.3 forbids it from mutating session state, so it must not queue behind a vote either. One
/// registration before <c>builder.Build()</c> connects the two:
/// <code>
/// builder.Services.AddSingleton&lt;IMediaSessionAccessor&gt;(_ =&gt;
///     new DelegatingMediaSessionAccessor(() =&gt;
///     {
///         var open = SessionRegistry.Shared.CurrentForMedia;   // null when no session is open
///         return open is null
///             ? null
///             : new MediaSessionView(open.Folder, open.Policy, open.Session.Records);
///     }));
/// </code>
/// <c>Records</c> is the right set and the only right set: § 11.2 step 4 makes membership
/// "against <c>RankingSession.Records</c>, which includes videos in a mixed folder even though
/// they are not rankable. It excludes anything already discarded." Re-scanning the folder here
/// would resurrect discarded files; using <c>Rankable</c> would hide a mixed folder's videos,
/// which § 16.8 says are served.
/// </summary>
public interface IMediaSessionAccessor
{
    /// <summary>The open session, or <see langword="null"/> when none is open (§ 11.2 step 1).</summary>
    IMediaSessionView? Current { get; }
}

/// <summary>A read-only window onto the open session, as the media layer needs it.</summary>
public interface IMediaSessionView
{
    /// <summary>The absolute folder the session was opened on.</summary>
    string Folder { get; }

    /// <summary>
    /// The session's rank policy (<c>MediaExtensions.RankPolicy</c>). Only used to fill
    /// <c>rankable</c> in <c>MediaMeta</c>; it never gates access to bytes, because a mixed
    /// folder's videos are still served (§ 16.8).
    /// </summary>
    MediaKind Policy { get; }

    /// <summary>
    /// Looks a filename up among the session's records. Membership is against
    /// <c>RankingSession.Records</c>, so it includes videos in a mixed folder and excludes
    /// anything already discarded (§ 11.2 step 4). The record returned carries the <b>on-disk</b>
    /// spelling, which is what the server echoes back (§ 11.1.6).
    /// </summary>
    bool TryFindRecord(string id, [MaybeNullWhen(false)] out MediaRecord record);
}

/// <summary>
/// The default: no session, so every media request answers <c>404 no_session</c>. Present so the
/// media layer builds and serves correctly on its own; the session layer replaces it.
/// </summary>
public sealed class NoOpenSessionAccessor : IMediaSessionAccessor
{
    public IMediaSessionView? Current => null;
}

/// <summary>
/// Adapts any "give me the current session" callback to <see cref="IMediaSessionAccessor"/>, so
/// the session layer does not have to write a class to plug in.
/// </summary>
public sealed class DelegatingMediaSessionAccessor(Func<IMediaSessionView?> current) : IMediaSessionAccessor
{
    public IMediaSessionView? Current => current();
}

/// <summary>
/// A ready-made <see cref="IMediaSessionView"/> over a snapshot of records.
/// <para/>
/// The comparer is the platform's: <c>OrdinalIgnoreCase</c> on Windows, <c>Ordinal</c> elsewhere.
/// SERVER_SPEC.md § 11.1.3 asks for exactly that ("case-insensitive on Windows, case-sensitive
/// elsewhere ... and the platform's filesystem") in the same breath as calling it "matching
/// <c>JsonCatalog</c>'s <c>OrdinalIgnoreCase</c> dictionary", which those two cannot both be on
/// Linux. The filesystem rule is the one implemented here.
/// </summary>
public sealed class MediaSessionView : IMediaSessionView
{
    public static readonly StringComparer IdComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly Dictionary<string, MediaRecord> _byId;

    public MediaSessionView(string folder, MediaKind policy, IEnumerable<MediaRecord> records)
    {
        Folder = folder;
        Policy = policy;
        _byId = new Dictionary<string, MediaRecord>(IdComparer);
        foreach (var record in records)
            _byId[record.Id.Filename] = record;
    }

    public string Folder { get; }

    public MediaKind Policy { get; }

    public bool TryFindRecord(string id, [MaybeNullWhen(false)] out MediaRecord record) =>
        _byId.TryGetValue(id, out record);
}
