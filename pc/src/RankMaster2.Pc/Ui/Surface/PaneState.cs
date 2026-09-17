namespace RankMaster2.Pc.Ui.Surface;

using RankMaster2.Pc.Link;
using RankMaster2.Pc.Stills;

/// <summary>One side of the screen (plan § 3.2). Immutable; <see cref="RankCoordinator"/> replaces
/// it wholesale on every transition and is responsible for disposing the outgoing <see cref="Lease"/>
/// when it is not being carried forward (see "reuse" below).</summary>
public enum PaneKind
{
    /// <summary>The pair landed; the request to C or D has been made (or, for a missing file, never
    /// will be). Accepts nothing.</summary>
    Waiting,

    /// <summary>Pixels (still or video) are on screen. Accepts everything.</summary>
    Ready,

    /// <summary>
    /// A still preview is shown and the full decode is pending (SPEC.md: "paint the first decodable
    /// image as soon as one exists; refine in place"). Accepts everything, same as Ready.
    /// <para/>
    /// The real <see cref="IStillSource"/> this plan was built against (pc/src/RankMaster2.Pc/Stills)
    /// delivers one decode per id — <see cref="StillState.Ready"/> or <see cref="StillState.Failed"/>,
    /// never a preview followed by a refinement — so this state is never entered in practice today.
    /// It is kept because the plan names it as part of the pane's contract and a future two-pass
    /// decoder would only need to raise <c>Changed</c> twice for the same generation to make it real;
    /// nothing else in <see cref="PaneState"/> or <see cref="RankCoordinator"/> would need to change.
    /// </summary>
    Refining,

    /// <summary>§ 11.3: <c>sizeBytes == null</c>, or C/D reported the file missing. Accepts only
    /// this side's discard (→ <c>drop_missing</c>). Not special, not a vote for either side.</summary>
    Gone,

    /// <summary>The file is there but will not decode/play. Accepts this side's discard or special,
    /// skip, and a vote for the *other* side. Not a vote for this side.</summary>
    Undecodable,

    /// <summary>D's engine is <c>Unavailable</c>/<c>Failed</c>. Same acceptance as Undecodable.</summary>
    NoVideoEngine,
}

/// <summary>One pane, keyed by the <c>(pairSeq, side)</c> generation it was created for (plan § 3.2).
/// A delivery from C or D that does not match <see cref="Generation"/> and <see cref="MySide"/> is
/// stale and must be dropped by the caller before it ever reaches this type.</summary>
public sealed record PaneState
{
    public required Side MySide { get; init; }
    public required long Generation { get; init; }
    public required string Id { get; init; }
    public required string? MediaVersion { get; init; }
    public required bool IsVideo { get; init; }
    public required PaneKind Kind { get; init; }

    /// <summary>Centred, dim, on-black sentence for Gone/Undecodable/NoVideoEngine. Null otherwise.</summary>
    public string? Sentence { get; init; }

    /// <summary>Owned by this record while Kind is Ready/Refining and the pane is a still. The
    /// caller (<see cref="RankCoordinator"/>) disposes it when replacing the pane, unless the
    /// replacement is a "reuse" of the same still (see <see cref="ReusableFor"/>), in which case the
    /// lease is carried forward unchanged and must not be disposed.</summary>
    public StillLease? Lease { get; init; }

    /// <summary>When this pane last entered <see cref="Waiting"/>. Drives the still ring's 300 ms
    /// grace and the "Starting video…" line's 1 s delay -- both computed by the caller against
    /// <see cref="IClock"/>, never by a timer inside this type.</summary>
    public required DateTimeOffset WaitingSince { get; init; }

    // ---- factories -------------------------------------------------------------------------------

    public static PaneState Waiting(Side side, long generation, string id, string? mediaVersion, bool isVideo, DateTimeOffset now) => new()
    {
        MySide = side,
        Generation = generation,
        Id = id,
        MediaVersion = mediaVersion,
        IsVideo = isVideo,
        Kind = PaneKind.Waiting,
        Sentence = null,
        Lease = null,
        WaitingSince = now,
    };

    public PaneState WithReady(StillLease lease) => this with { Kind = PaneKind.Ready, Lease = lease, Sentence = null };

    public PaneState WithVideoReady() => this with { Kind = PaneKind.Ready, Lease = null, Sentence = null };

    public PaneState WithGone() => this with
    {
        Kind = PaneKind.Gone,
        Lease = null,
        Sentence = $"{Id} is no longer in the folder — `{DiscardKeyLabel(MySide)}` removes it from the ranking",
    };

    public PaneState WithUndecodable(string reason) => this with
    {
        Kind = PaneKind.Undecodable,
        Lease = null,
        Sentence = $"{Id} cannot be shown — `{DiscardKeyLabel(MySide)}` discards it, `{VoteOtherKeyLabel(MySide)}` votes for the other",
    };

    public PaneState WithNoVideoEngine(string reason) => this with
    {
        Kind = PaneKind.NoVideoEngine,
        Lease = null,
        Sentence = $"Video cannot play on this PC — {reason}",
    };

    private static string DiscardKeyLabel(Side side) => side == Side.Left ? "1" : "2";
    private static string VoteOtherKeyLabel(Side side) => side == Side.Left ? "→" : "←";

    // ---- acceptance (plan § 3.2's table) ----------------------------------------------------------

    /// <summary>Whether this pane, on its own, permits <paramref name="intent"/> to proceed. The
    /// gate's rule 2 is "ready" only when both panes agree.</summary>
    public bool Accepts(Intent intent)
    {
        var mine = Intents.Discard(MySide);
        var mineSpecial = Intents.Special(MySide);
        var mineVote = Intents.Vote(MySide);
        var otherVote = Intents.Vote(MySide == Side.Left ? Side.Right : Side.Left);

        return Kind switch
        {
            PaneKind.Waiting => false,
            PaneKind.Ready or PaneKind.Refining => true,
            PaneKind.Gone => intent == mine,
            PaneKind.Undecodable => intent == mine || intent == mineSpecial || intent == Intent.Skip || intent == otherVote,
            PaneKind.NoVideoEngine => intent == mine || intent == mineSpecial || intent == Intent.Skip,
            _ => false,
        };
    }

    // ---- reuse (plan § 3.2 "Pane reuse") -----------------------------------------------------------

    /// <summary>
    /// True when a new pair's id and mediaVersion for this side match this pane's, and this pane is
    /// currently showing pixels. When true, the caller keeps this exact <see cref="PaneState"/> (its
    /// Lease, its Kind) and only advances <see cref="Generation"/> -- no re-decode, no flash, and a
    /// video pane keeps playing rather than restarting.
    /// </summary>
    public bool ReusableFor(string id, string? mediaVersion) =>
        (Kind == PaneKind.Ready || Kind == PaneKind.Refining) && Id == id && MediaVersion == mediaVersion;

    public PaneState WithGeneration(long generation) => this with { Generation = generation };
}
