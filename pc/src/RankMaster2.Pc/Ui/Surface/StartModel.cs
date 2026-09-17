namespace RankMaster2.Pc.Ui.Surface;

using RankMaster2.Pc.Link;

/// <summary>
/// The start screen as data (plan § 2.1, § 4.1). No Avalonia type; <c>Views/StartView</c> binds to
/// this and decides nothing.
/// </summary>
public sealed class StartModel
{
    /// <summary>From <see cref="LastFolderStore"/>. Non-null only when the path still exists.
    /// Resume is shown, and focused, exactly when this is non-null and <see cref="Opening"/> is false.</summary>
    public string? LastFolder { get; set; }

    public bool Opening { get; private set; }
    public string? OpeningFolder { get; private set; }

    /// <summary>The red box: an open refusal, or the exhausted sentence. Null when there is nothing
    /// to say (plan's rule: say something only when he can fix it, or when it is the loudest signal
    /// of what just happened).</summary>
    public string? BoxText { get; private set; }

    /// <summary>True only while the session that just became exhausted is still open behind this
    /// screen, so <c>Ctrl+Z</c> here can call the link (plan § 4.1, § 3.4).</summary>
    public bool ExhaustedSessionOpen { get; private set; }

    public bool DialogOpen { get; set; }

    /// <summary>The link's status line (plan § 4.1's table), derived from the real
    /// <see cref="ISessionLink"/>'s coarser <see cref="LinkState"/> plus its last <see cref="Failure"/>.
    /// The plan asked for a six-way observable (Connecting/StartingServer/Ready/Unreachable/
    /// NotPaired/PinMismatch); the real seam exposes four <see cref="LinkState"/> values and a
    /// <see cref="Failure"/> that already carries which of those six things happened in its own
    /// wording (<see cref="FailureKind"/>), so this reads the failure's text rather than
    /// re-deriving English from a status enum that does not exist. While a connect or an open is
    /// in flight, <see cref="OpeningFolder"/>/<see cref="Opening"/> is what is shown instead — the
    /// old app's "Opening *folder*…" line — because no sub-progress ("connecting" vs "starting the
    /// server") is observable from outside a single in-flight call.</summary>
    public static string? StatusLineFor(LinkState state, Failure? lastFailure)
    {
        if (state is LinkState.Connected or LinkState.InSession) return null; // "Ready" → nothing.

        if (lastFailure is not null)
            return string.IsNullOrEmpty(lastFailure.Detail) ? lastFailure.Title : $"{lastFailure.Title}. {lastFailure.Detail}";

        return state switch
        {
            LinkState.Disconnected => "Not connected to the ranking server yet.",
            LinkState.InSessionUnreachable => "The server did not answer.",
            _ => null,
        };
    }

    public void BeginOpening(string folder)
    {
        Opening = true;
        OpeningFolder = folder;
        BoxText = null;
        ExhaustedSessionOpen = false;
    }

    public void EndOpening()
    {
        Opening = false;
        OpeningFolder = null;
    }

    /// <summary>The red box's text: an open refusal, a mid-session fatal failure that sent the
    /// screen back here, or the "server closed the folder" sentence. Same box, same rules
    /// (plan § 3.5): shown only when it is the owner's to fix or the loudest signal of what happened.</summary>
    public void ShowMessage(string text)
    {
        Opening = false;
        OpeningFolder = null;
        BoxText = text;
        ExhaustedSessionOpen = false;
    }

    /// <summary>Plan § 4.1's "Exhausted returns here": the box names the folder, and — only when
    /// the snapshot says so — that <c>Ctrl+Z</c> can bring the last pair back.</summary>
    public void ShowExhausted(string folder, bool undoAvailable)
    {
        Opening = false;
        OpeningFolder = null;
        BoxText = undoAvailable
            ? $"No pair left to compare in {folder}. `Ctrl+Z` takes back the last one."
            : $"No pair left to compare in {folder}.";
        ExhaustedSessionOpen = true;
    }

    public void ClearBox()
    {
        BoxText = null;
        ExhaustedSessionOpen = false;
    }
}
