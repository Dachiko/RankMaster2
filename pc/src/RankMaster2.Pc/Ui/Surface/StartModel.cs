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

    /// <summary>What the start screen has to say, if anything (plan H § 3.1's states): an exhausted
    /// session (label above the hero), an error (accent status line), or something that finished as
    /// asked (mid status line with an ink dot). Replaces the single red/green box and its
    /// <c>BoxIsError</c> flag, which could only say "good" or "bad" -- not "no pair left", which is
    /// shown somewhere else entirely.</summary>
    public StartMessageKind MessageKind { get; private set; } = StartMessageKind.None;

    /// <summary>The message's short text ("title · detail", no trailing full stop; plan H § 3.1).
    /// Null exactly when <see cref="MessageKind"/> is <see cref="StartMessageKind.None"/>. For
    /// <see cref="StartMessageKind.Exhausted"/> it is the sentence for tests and the like; the view
    /// shows the fixed label <c>NO PAIR LEFT</c> instead.</summary>
    public string? MessageText { get; private set; }

    /// <summary>For <see cref="StartMessageKind.Exhausted"/> only: whether <c>Ctrl+Z</c> can take the
    /// last pair back, i.e. whether the first pill is "CTRL+Z TAKE BACK".</summary>
    public bool UndoAvailable { get; private set; }

    /// <summary>True only while the session that just became exhausted is still open behind this
    /// screen, so <c>Ctrl+Z</c> here can call the link (plan § 4.1, § 3.4).</summary>
    public bool ExhaustedSessionOpen { get; private set; }


    /// <summary>The link's status line (plan § 4.1's table), derived from the real
    /// <see cref="ISessionLink"/>'s coarser <see cref="LinkState"/> plus its last <see cref="Failure"/>.
    /// The plan asked for a six-way observable (Connecting/StartingServer/Ready/Unreachable/
    /// NotPaired/PinMismatch); the real seam exposes four <see cref="LinkState"/> values and a
    /// <see cref="Failure"/> that already carries which of those six things happened in its own
    /// wording (<see cref="FailureKind"/>), so this reads the failure's text rather than
    /// re-deriving English from a status enum that does not exist. While a connect or an open is
    /// in flight, <see cref="OpeningFolder"/>/<see cref="Opening"/> is what is shown instead (the
    /// busy line) because no sub-progress ("connecting" vs "starting the server") is observable
    /// from outside a single in-flight call.
    /// <para/>
    /// Plan H § 3.1 shortened it: a server that is not there at all reads <c>Server not answering</c>
    /// (plus the failure's detail when it has one); any other failure (a lost pairing, the wrong
    /// server) keeps its own title, joined to the detail with " · " and without a trailing full stop.</summary>
    public static string? StatusLineFor(LinkState state, Failure? lastFailure)
    {
        if (state is LinkState.Connected or LinkState.InSession) return null; // "Ready" → nothing.

        if (lastFailure is not null)
        {
            var title = lastFailure.Kind is FailureKind.ServerNotRunning or FailureKind.Unreachable
                ? ServerNotAnswering
                : lastFailure.Title;
            return Notices.TitleAndDetail(title, lastFailure.Detail);
        }

        return state switch
        {
            LinkState.Disconnected => "Connecting to the server",
            LinkState.InSessionUnreachable => ServerNotAnswering,
            _ => null,
        };
    }

    public const string ServerNotAnswering = "Server not answering";

    /// <summary>The corner lamp (plan H § 3.1): ink dot + <c>SERVER</c> when connected, accent dot +
    /// <c>NO SERVER</c> when not. Before the first connect attempt has said anything (state
    /// Disconnected, no failure yet) it is <see cref="ServerLamp.Waiting"/> -- a faint dot, neither
    /// good nor bad news -- so the corner does not flash red for the half second the first connect takes.</summary>
    public static ServerLamp LampFor(LinkState state, Failure? lastFailure) => state switch
    {
        LinkState.Connected or LinkState.InSession => ServerLamp.Ok,
        LinkState.Disconnected when lastFailure is null => ServerLamp.Waiting,
        _ => ServerLamp.Down,
    };

    public void BeginOpening(string folder)
    {
        Opening = true;
        OpeningFolder = folder;
        ClearMessage();
    }

    public void EndOpening()
    {
        Opening = false;
        OpeningFolder = null;
    }

    /// <summary>An open refusal, a mid-session fatal failure that sent the screen back here, the
    /// "server closed the folder" sentence, or a rename's terminal sentence. Shown only when it is
    /// the owner's to fix or the loudest signal of what happened (plan § 3.5).
    /// <paramref name="isError"/> defaults to true because that is what every caller except a
    /// successful rename means; false is <see cref="StartMessageKind.Done"/>.</summary>
    public void ShowMessage(string text, bool isError = true)
    {
        Opening = false;
        OpeningFolder = null;
        MessageKind = isError ? StartMessageKind.Error : StartMessageKind.Done;
        MessageText = text;
        UndoAvailable = false;
        ExhaustedSessionOpen = false;
    }

    /// <summary>Plan § 4.1's "Exhausted returns here", in plan H § 3.1's form: the label above the
    /// hero becomes <c>NO PAIR LEFT</c>, and -- only when the snapshot says so -- the first pill
    /// becomes <c>CTRL+Z TAKE BACK</c>.</summary>
    public void ShowExhausted(string folder, bool undoAvailable)
    {
        Opening = false;
        OpeningFolder = null;
        MessageKind = StartMessageKind.Exhausted;
        MessageText = $"No pair left · {folder}";
        UndoAvailable = undoAvailable;
        ExhaustedSessionOpen = true;
    }

    public void ClearBox()
    {
        ClearMessage();
        ExhaustedSessionOpen = false;
    }

    private void ClearMessage()
    {
        MessageKind = StartMessageKind.None;
        MessageText = null;
        UndoAvailable = false;
        ExhaustedSessionOpen = false;
    }
}

/// <summary>Plan H § 3.1: which of the start screen's three kinds of message is showing.</summary>
public enum StartMessageKind { None, Exhausted, Error, Done }

/// <summary>Plan H § 3.1's corner lamp.</summary>
public enum ServerLamp { Ok, Waiting, Down }
