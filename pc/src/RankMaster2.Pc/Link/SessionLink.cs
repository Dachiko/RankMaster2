using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using RankMaster2.Pc.Link.Enrolment;
using RankMaster2.Pc.Link.Transport;
using RankMaster2.Pc.Link.Wire;
using WireSnapshot = RankMaster2.Pc.Link.Wire.Snapshot;

namespace RankMaster2.Pc.Link;

/// <summary>
/// The implementation: state, the action protocol (§ 5.1), open/close (§ 5.3). Everything here is
/// <c>internal</c> except through <see cref="ISessionLink"/> — the seam is frozen (§ 4), this is not.
/// </summary>
internal sealed class SessionLink : ISessionLink
{
    private readonly LinkOptions _options;
    private readonly Func<HttpMessageHandler, HttpMessageHandler>? _wrap;

    private Rm2Http? _http;
    private string? _httpBaseUrl;
    private string? _httpFingerprint;
    private Credential? _credential;

    private int _busyFlag;
    /// <summary>Non-null exactly while a <see cref="ConnectAsync"/> call holds the busy gate; every
    /// other caller that lands meanwhile awaits this instead of throwing (H9). Cleared, after the
    /// flag itself is released, by the same <see cref="ExitBusy"/> that let the connect go.</summary>
    private TaskCompletionSource<bool>? _connectInFlight;

    public LinkState State { get; private set; } = LinkState.Disconnected;
    public WireSnapshot? Snapshot { get; private set; }
    public Failure? LastFailure { get; private set; }
    public bool IsBusy => Volatile.Read(ref _busyFlag) == 1;

    /// <param name="wrap">Receives the pinned production handler and returns the handler to use.
    /// Null in production; the test harness's <see cref="object"/> — see the test project's
    /// <c>TapHandler</c> — sits between the link and the real server through this.</param>
    internal SessionLink(LinkOptions options, Func<HttpMessageHandler, HttpMessageHandler>? wrap)
    {
        _options = options;
        _wrap = wrap;
    }

    // ---- the busy gate (§ 4.1, § 5.1.1 step 1 and 7) ---------------------------------------------

    private void EnterBusy(bool isConnect)
    {
        if (Interlocked.CompareExchange(ref _busyFlag, 1, 0) != 0)
            throw new InvalidOperationException(
                "A call is already in flight on this ISessionLink. Call it from one thread, one call at a time.");
        if (isConnect)
            Volatile.Write(ref _connectInFlight, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    private void ExitBusy()
    {
        // Release the flag before waking anyone waiting on it, so a waiter's own EnterBusy (below)
        // never races its own wake-up.
        var connectDone = Interlocked.Exchange(ref _connectInFlight, null);
        Volatile.Write(ref _busyFlag, 0);
        connectDone?.TrySetResult(true);
    }

    /// <summary>Every call except <see cref="ConnectAsync"/> itself. A caller that lands while a
    /// <em>different</em> caller's <see cref="ConnectAsync"/> holds the gate waits for it instead of
    /// throwing — bounded by <c>ConnectAsync</c>'s own timeouts (offer + server-start, ~18 s), since
    /// this simply awaits that call's completion (H9: <c>MainWindow.OnOpened</c> fires
    /// <c>ConnectAsync</c> in the background at first frame while Open/Resume are already enabled).
    /// A caller that lands while anything <em>other</em> than a connect holds the gate still throws
    /// at once — that rule is unchanged.</summary>
    private async Task<T> Gated<T>(Func<Task<T>> body, CancellationToken ct = default)
    {
        if (Volatile.Read(ref _connectInFlight) is { } connecting)
            await connecting.Task.WaitAsync(ct).ConfigureAwait(false);

        EnterBusy(isConnect: false);
        try { return await body().ConfigureAwait(false); }
        finally { ExitBusy(); }
    }

    private async Task<T> GatedConnect<T>(Func<Task<T>> body)
    {
        EnterBusy(isConnect: true);
        try { return await body().ConfigureAwait(false); }
        finally { ExitBusy(); }
    }

    // ---- ConnectAsync (§ 5.2.2) -------------------------------------------------------------------

    public Task<ConnectResult> ConnectAsync(CancellationToken ct = default) => GatedConnect(() => ConnectCore(ct));

    private async Task<ConnectResult> ConnectCore(CancellationToken ct)
    {
        var credential = Credential.Load(_options.CredentialDirectory);

        if (credential is not null)
        {
            EnsureHttp(credential.BaseUrl, credential.Fingerprint);
            _http!.BearerToken = credential.Token;

            var reply = await _http.Send(FrozenRequest.Get("/ping"), _options.ReadTimeout, ct).ConfigureAwait(false);

            if (reply is Reply.Ok(200, var body, _))
            {
                var ping = TryParse(body, WireJsonContext.Default.Ping);
                if (ping is { Authenticated: true })
                {
                    AdoptConnected(credential);
                    return new ConnectResult.Connected(credential.BaseUrl, Enrolled: false, StartedServer: false);
                }
                // A 200 that does not parse, or is not authenticated (a stale credential the server
                // no longer honours the way /ping said it would): fall through to B like any other
                // failure to confirm the credential still works.
            }
            else if (reply is Reply.Refused(401, _, _, _, _, _))
            {
                return await EnrolFlow(credential, offerInHand: null, startedServer: false, ct).ConfigureAwait(false);
            }
            else if (reply is Reply.Unreachable(UnreachableKind.PinMismatch, _))
            {
                return await ReconcilePinMismatch(credential, ct).ConfigureAwait(false);
            }
            // Unreachable (other kinds): go to B.
        }

        return await FindServerAndEnrol(credential, ct).ConfigureAwait(false);
    }

    private async Task<ConnectResult> ReconcilePinMismatch(Credential credential, CancellationToken ct)
    {
        var offer = await Task.Run(
            () => OfferChannel.Request(_options.ServerDataDirectory, _options.OfferTimeout), ct).ConfigureAwait(false);

        if (offer is not null &&
            !string.Equals(PinnedHandler.Normalise(offer.CertificateFingerprint),
                           PinnedHandler.Normalise(credential.Fingerprint), StringComparison.Ordinal))
        {
            // The server re-minted its certificate. The data directory is the trust root; adopt it.
            return await EnrolFlow(credential, offerInHand: offer, startedServer: false, ct).ConfigureAwait(false);
        }

        var failure = NotYourServerFailure(credential.BaseUrl);
        State = LinkState.Disconnected;
        LastFailure = failure;
        return new ConnectResult.Failed(failure);
    }

    private async Task<ConnectResult> FindServerAndEnrol(Credential? credential, CancellationToken ct)
    {
        var dataDir = _options.ServerDataDirectory;
        var previousOffer = OfferChannel.ReadCurrent(dataDir);
        var wroteRequest = OfferChannel.RequestWindow(dataDir);

        if (!wroteRequest)
            return Disconnect(ServerNotRunningFailure());

        var offer = await Task.Run(
            () => OfferChannel.WaitForOffer(dataDir, previousOffer, _options.OfferTimeout), ct).ConfigureAwait(false);

        if (offer is not null)
            return await EnrolFlow(credential, offerInHand: offer, startedServer: false, ct).ConfigureAwait(false);

        if (!_options.StartServerIfNotRunning || _options.ServerExecutable is null)
        {
            OfferChannel.TryDeleteRequestFile(dataDir);
            return Disconnect(ServerNotRunningFailure());
        }

        ServerProcess.Start(_options.ServerExecutable, _options.ServerArguments);

        var deadline = DateTimeOffset.UtcNow + _options.ServerStartTimeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (credential is not null)
            {
                EnsureHttp(credential.BaseUrl, credential.Fingerprint);
                _http!.BearerToken = credential.Token;

                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining > TimeSpan.Zero)
                {
                    var pingTimeout = remaining < _options.ReadTimeout ? remaining : _options.ReadTimeout;
                    var reply = await _http.Send(FrozenRequest.Get("/ping"), pingTimeout, ct).ConfigureAwait(false);
                    if (reply is Reply.Ok(200, var body, _))
                    {
                        var ping = TryParse(body, WireJsonContext.Default.Ping);
                        if (ping is { Ready: true, Authenticated: true })
                        {
                            AdoptConnected(credential);
                            return new ConnectResult.Connected(credential.BaseUrl, Enrolled: false, StartedServer: true);
                        }
                    }
                }
            }

            var freshOffer = OfferChannel.WaitForOffer(dataDir, previousOffer, TimeSpan.FromMilliseconds(250));
            if (freshOffer is not null)
                return await EnrolFlow(credential, offerInHand: freshOffer, startedServer: true, ct).ConfigureAwait(false);
        }

        OfferChannel.TryDeleteRequestFile(dataDir);
        return Disconnect(ServerNotRunningFailure());
    }

    private async Task<ConnectResult> EnrolFlow(Credential? previous, Offer? offerInHand, bool startedServer, CancellationToken ct)
    {
        var outcome = await Enroller.Enrol(
            BuildHttp, _options.ServerDataDirectory, _options.OfferTimeout, _options.ConnectTimeout,
            _options.DeviceName, previous, offerInHand, ct).ConfigureAwait(false);

        switch (outcome)
        {
            case EnrolOutcome.Enrolled enrolled:
                enrolled.Credential.Save(_options.CredentialDirectory);
                AdoptConnected(enrolled.Credential);
                return new ConnectResult.Connected(enrolled.Credential.BaseUrl, Enrolled: true, StartedServer: startedServer);

            case EnrolOutcome.Failed failed:
                var failure = failed.Reason switch
                {
                    EnrolFailureReason.NoServerFound => ServerNotRunningFailure(),
                    // InvalidPairingCode / PairingNotOpen / RateLimited are definite refusals of the
                    // pairing attempt; Unreachable (§ 13.3, N12) is a lost POST /pair — never retried,
                    // reported the same way, since retrying it blindly could double-spend a code that
                    // actually landed. PairingLost (Fatal) is reserved for the mid-session 401 path
                    // (ReEnrolInPlace), where an existing credential is known to be dead.
                    _ => PairingFailedFailure(offerInHand?.Code, failed.Detail),
                };
                return Disconnect(failure);

            default:
                throw new InvalidOperationException("unreachable EnrolOutcome");
        }
    }

    /// <summary>Re-enrols in place without disturbing <see cref="State"/> or <see cref="Snapshot"/> —
    /// used mid-action (§ 5.1.1 step 5) and mid-refresh (§ 5.3.4), where a session may already be
    /// open and must stay that way across a re-enrolment.</summary>
    private async Task<bool> ReEnrolInPlace(CancellationToken ct)
    {
        var outcome = await Enroller.Enrol(
            BuildHttp, _options.ServerDataDirectory, _options.OfferTimeout, _options.ConnectTimeout,
            _options.DeviceName, _credential, offerInHand: null, ct).ConfigureAwait(false);

        if (outcome is EnrolOutcome.Enrolled enrolled)
        {
            enrolled.Credential.Save(_options.CredentialDirectory);
            _credential = enrolled.Credential;
            EnsureHttp(enrolled.Credential.BaseUrl, enrolled.Credential.Fingerprint);
            _http!.BearerToken = enrolled.Credential.Token;
            return true;
        }

        if (outcome is EnrolOutcome.Failed failed)
            LastFailure = failed.Reason == EnrolFailureReason.NoServerFound ? ServerNotRunningFailure() : PairingLostFailure();

        return false;
    }

    private ConnectResult.Failed Disconnect(Failure failure)
    {
        State = LinkState.Disconnected;
        LastFailure = failure;
        return new ConnectResult.Failed(failure);
    }

    private void AdoptConnected(Credential credential)
    {
        _credential = credential;
        EnsureHttp(credential.BaseUrl, credential.Fingerprint);
        _http!.BearerToken = credential.Token;
        State = LinkState.Connected;
        LastFailure = null;
    }

    private void AdoptSession(WireSnapshot snapshot)
    {
        Snapshot = snapshot;
        State = LinkState.InSession;
        LastFailure = null;
    }

    private void EnsureHttp(string baseUrl, string fingerprint)
    {
        if (_http is not null && _httpBaseUrl == baseUrl && _httpFingerprint == fingerprint) return;

        var old = _http;
        _http = BuildHttp(baseUrl, fingerprint);
        _httpBaseUrl = baseUrl;
        _httpFingerprint = fingerprint;
        old?.Dispose();
    }

    private Rm2Http BuildHttp(string baseUrl, string fingerprint)
    {
        var handler = PinnedHandler.Create(fingerprint, _options.ConnectTimeout);
        HttpMessageHandler final = _wrap is null ? handler : _wrap(handler);
        return new Rm2Http(baseUrl, final, _options.Log);
    }

    // ---- OpenAsync (§ 5.3.1) ----------------------------------------------------------------------

    public Task<OpenResult> OpenAsync(string folder, CancellationToken ct = default) => Gated(() => OpenCore(folder, ct), ct);

    private async Task<OpenResult> OpenCore(string folder, CancellationToken ct)
    {
        if (State == LinkState.Disconnected)
        {
            var connect = await ConnectCore(ct).ConfigureAwait(false);
            if (connect is ConnectResult.Failed failed) return new OpenResult.Failed(failed.Failure);
        }

        var previousFolder = Snapshot?.Folder ?? await LearnOpenSessionFolder(ct).ConfigureAwait(false);

        var deleteReply = await _http!.Send(FrozenRequest.Delete("/session"), _options.ReadTimeout, ct).ConfigureAwait(false);
        var deletedSomething = deleteReply is Reply.Ok(204, _, _);

        var reply = await _http.Send(FrozenRequest.OpenSession(folder), _options.OpenTimeout, ct).ConfigureAwait(false);

        if (reply is Reply.Refused(409, Codes.SessionAlreadyOpen, _, _, _, _))
        {
            // "Cannot happen after step 2 unless the phone opened a folder in the gap." Do step 2/3
            // once more; a second 409 is Unexpected rather than looping.
            await _http.Send(FrozenRequest.Delete("/session"), _options.ReadTimeout, ct).ConfigureAwait(false);
            reply = await _http.Send(FrozenRequest.OpenSession(folder), _options.OpenTimeout, ct).ConfigureAwait(false);

            if (reply is Reply.Refused(409, Codes.SessionAlreadyOpen, var msg, var rid, _, _))
                return new OpenResult.Failed(UnexpectedFailure(Codes.SessionAlreadyOpen, rid));
        }
        else if (reply is Reply.Unreachable)
        {
            // POST /session is safe to retry blindly (§ 13.3).
            reply = await _http.Send(FrozenRequest.OpenSession(folder), _options.OpenTimeout, ct).ConfigureAwait(false);
        }

        if (reply is Reply.Ok(var status, var body, _) && status is 200 or 201)
        {
            var snapshot = TryParse(body, WireJsonContext.Default.Snapshot);
            if (snapshot is null)
                return new OpenResult.Failed(UnexpectedFailure(Codes.ClientMalformedResponse, null));

            var replacedOther = deletedSomething && previousFolder is not null && !FolderEquals(previousFolder, snapshot.Folder);
            AdoptSession(snapshot);
            return new OpenResult.Opened(snapshot, replacedOther);
        }

        if (reply is Reply.Refused(404, Codes.FolderNotFound, _, _, _, _))
            return new OpenResult.Failed(FolderNotFoundFailure(folder));
        if (reply is Reply.Refused(400, Codes.FolderNotADirectory, _, _, _, _))
            return new OpenResult.Failed(FolderNotADirectoryFailure(folder));
        if (reply is Reply.Refused(403, Codes.FolderAccessDenied, _, _, _, _))
            return new OpenResult.Failed(FolderAccessDeniedFailure(folder));
        if (reply is Reply.Refused(409, Codes.FolderNotRankable, _, _, var rankableDetails, _))
            return new OpenResult.Failed(FolderNotRankableFailure(folder, rankableDetails));
        if (reply is Reply.Refused(409, Codes.LibraryJsonUnreadable, _, _, _, _))
            return new OpenResult.Failed(LibraryJsonUnreadableFailure(folder));
        if (reply is Reply.Refused(423, Codes.FolderLocked, _, _, _, _))
            return new OpenResult.Failed(FolderLockedFailure(folder));

        if (reply is Reply.Unreachable(var kind, _))
        {
            var f = UnreachableFailure(kind == UnreachableKind.Timeout ? Codes.ClientTimeout : Codes.ClientUnreachable);
            LastFailure = f;
            return new OpenResult.Failed(f);
        }

        if (reply is Reply.Refused(var status2, var code2, var message2, var rid2, _, _))
            return new OpenResult.Failed(UnexpectedFailure(code2, rid2));

        return new OpenResult.Failed(UnexpectedFailure(Codes.ClientMalformedResponse, null));
    }

    private async Task<string?> LearnOpenSessionFolder(CancellationToken ct)
    {
        var reply = await _http!.Send(FrozenRequest.Get("/ping"), _options.ReadTimeout, ct).ConfigureAwait(false);
        if (reply is Reply.Ok(200, var body, _))
        {
            var ping = TryParse(body, WireJsonContext.Default.Ping);
            if (ping?.Session is { Open: true } session) return session.Folder;
        }
        return null;
    }

    private static bool FolderEquals(string a, string b) =>
        string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // ---- CloseAsync (§ 5.3.2) ---------------------------------------------------------------------

    public Task CloseAsync(CancellationToken ct = default) => Gated(async () =>
    {
        if (_http is null) { State = LinkState.Disconnected; Snapshot = null; return true; }

        var reply = await _http.Send(FrozenRequest.Delete("/session"), _options.ReadTimeout, ct).ConfigureAwait(false);

        LastFailure = reply switch
        {
            Reply.Ok(204, _, _) => null,
            Reply.Refused(404, Codes.NoSession, _, _, _, _) => null,
            Reply.Refused(var status, var code, var message, var rid, _, _) => FailureForCode(code, message, rid),
            Reply.Unreachable(var kind, _) => UnreachableFailure(kind == UnreachableKind.Timeout ? Codes.ClientTimeout : Codes.ClientUnreachable),
            _ => null,
        };

        State = State == LinkState.Disconnected ? LinkState.Disconnected : LinkState.Connected;
        Snapshot = null;
        return true;
    }, ct);

    // ---- the action protocol (§ 5.1.1) -------------------------------------------------------------

    public Task<ActionResult> VoteAsync(Side winner, long onPairSeq, CancellationToken ct = default) =>
        Gated(() => RunPairAction(onPairSeq, (token, id) => FrozenRequest.Vote(token, WireOf(winner), id), ct), ct);

    public Task<ActionResult> SkipAsync(long onPairSeq, CancellationToken ct = default) =>
        Gated(() => RunPairAction(onPairSeq, (token, id) => FrozenRequest.Skip(token, id), ct), ct);

    public Task<ActionResult> DiscardAsync(Side side, long onPairSeq, CancellationToken ct = default) =>
        Gated(() => RunPairAction(onPairSeq, (token, id) => FrozenRequest.Discard(token, WireOf(side), id), ct), ct);

    public Task<ActionResult> SpecialAsync(Side side, long onPairSeq, CancellationToken ct = default) =>
        Gated(() => RunPairAction(onPairSeq, (token, id) => FrozenRequest.Special(token, WireOf(side), id), ct), ct);

    public Task<ActionResult> UndoAsync(CancellationToken ct = default) => Gated(() => UndoCore(ct), ct);

    public Task<ActionResult> SaveAsync(CancellationToken ct = default) => Gated(() => SaveCore(ct), ct);

    public Task<ActionResult> RefreshAsync(CancellationToken ct = default) => Gated(() => RefreshCore(ct), ct);

    private static string WireOf(Side side) => side == Side.Left ? "left" : "right";

    private bool InSessionOfAnyKind => State is LinkState.InSession or LinkState.InSessionUnreachable;

    private async Task<ActionResult> RunPairAction(long onPairSeq, Func<string, string, FrozenRequest> build, CancellationToken ct)
    {
        if (!InSessionOfAnyKind) return new ActionResult.NotSent(NotSentReason.NoSession);

        var snapshot = Snapshot;
        if (snapshot?.Pair is null || snapshot.PairToken is null)
            return new ActionResult.NotSent(NotSentReason.Exhausted);
        if (onPairSeq != snapshot.PairSeq)
            return new ActionResult.NotSent(NotSentReason.PairMoved);

        var token = snapshot.PairToken;
        var id = NewRequestId();
        var request = build(token, id);

        return await SendActionSequence(request, ct).ConfigureAwait(false);
    }

    private async Task<ActionResult> UndoCore(CancellationToken ct)
    {
        if (!InSessionOfAnyKind) return new ActionResult.NotSent(NotSentReason.NoSession);
        if (Snapshot is not { UndoAvailable: true }) return new ActionResult.NotSent(NotSentReason.UndoNotAvailable);

        var request = FrozenRequest.Undo(NewRequestId());
        return await SendActionSequence(request, ct).ConfigureAwait(false);
    }

    private async Task<ActionResult> SaveCore(CancellationToken ct)
    {
        if (!InSessionOfAnyKind) return new ActionResult.NotSent(NotSentReason.NoSession);

        var request = FrozenRequest.Save();
        return await SendActionSequence(request, ct).ConfigureAwait(false);
    }

    // ---- rename by rank (§ 10.16) -------------------------------------------------------------

    public Task<RenameOperationResult> StartRenameAsync(CancellationToken ct = default) => Gated(async () =>
    {
        var reply = await SendRenameSequence(
            FrozenRequest.StartRename(NewRequestId()), _options.ActionTimeout, retryOnUnreachable: true, ct).ConfigureAwait(false);
        return ClassifyRenameReply(reply);
    }, ct);

    public Task<RenameOperationResult> GetRenameAsync(CancellationToken ct = default) => Gated(async () =>
    {
        var reply = await SendRenameSequence(
            FrozenRequest.GetRename(), _options.ReadTimeout, retryOnUnreachable: false, ct).ConfigureAwait(false);
        return ClassifyRenameReply(reply);
    }, ct);

    public Task<RenameOperationResult> CancelRenameAsync(CancellationToken ct = default) => Gated(async () =>
    {
        var reply = await SendRenameSequence(
            FrozenRequest.CancelRename(), _options.ActionTimeout, retryOnUnreachable: true, ct).ConfigureAwait(false);
        return ClassifyRenameReply(reply);
    }, ct);

    /// <summary>The retry shape every rename call shares: a lost response to a mutating call
    /// (start/cancel) is safe to resend once — a start that actually landed answers the resend with
    /// rename_in_progress and its own operationId, never a second run (§ 10.16's ordering makes that
    /// safe the same way POST /session is); a read (GetRenameAsync) is not retried here because the
    /// caller's own next poll, 250 ms later, is the retry. A stale bearer token is re-enrolled and
    /// the same request sent once more, exactly as § 5.1.1 step 5 does for a pair action.</summary>
    private async Task<Reply> SendRenameSequence(FrozenRequest request, TimeSpan timeout, bool retryOnUnreachable, CancellationToken ct)
    {
        var reply = await _http!.Send(request, timeout, ct).ConfigureAwait(false);

        if (retryOnUnreachable && reply is Reply.Unreachable(var initialKind, _) &&
            initialKind is not (UnreachableKind.PinMismatch or UnreachableKind.Cancelled))
        {
            reply = await _http.Send(request, timeout, ct).ConfigureAwait(false);
        }

        if (reply is Reply.Refused(401, _, _, _, _, _) && !ct.IsCancellationRequested)
        {
            var reEnrolled = await ReEnrolInPlace(ct).ConfigureAwait(false);
            if (reEnrolled)
                reply = await _http.Send(request, timeout, ct).ConfigureAwait(false);
        }

        return reply;
    }

    /// <summary>The body of every one of the three routes is the same shape (§ 10.16) whatever its
    /// state, including a terminal one — that is passed through as Observed and the caller (the
    /// rename surface, § 3.13 item 2) decides what a succeeded/cancelled/failed operation means on
    /// screen. Only an HTTP-level refusal — the operation could not even be named — is Refused.</summary>
    private RenameOperationResult ClassifyRenameReply(Reply reply)
    {
        if (reply is Reply.Ok(var status, var body, _) && status is 200 or 202)
        {
            var operation = TryParse(body, WireJsonContext.Default.RenameOperation);
            if (operation is null)
            {
                var malformed = UnexpectedFailure(Codes.ClientMalformedResponse, null);
                LastFailure = malformed;
                return new RenameOperationResult.Refused(malformed);
            }
            return new RenameOperationResult.Observed(operation);
        }

        if (reply is Reply.Refused(404, Codes.NoRenameOperation, _, _, _, var noOpSession))
        {
            if (noOpSession is not null) AdoptSession(noOpSession);
            return new RenameOperationResult.NoOperation();
        }

        if (reply is Reply.Refused(409, Codes.RenameInProgress, var busyMessage, var busyRequestId, var busyDetails, var busySession))
        {
            if (busySession is not null) AdoptSession(busySession);
            var operationId = DetailString(busyDetails, "operationId");
            var busyFailure = new Failure(FailureKind.Unexpected, "A rename is already running",
                operationId is null
                    ? busyMessage
                    : $"Operation {operationId} is already in progress in this folder. {busyMessage}",
                Codes.RenameInProgress, busyRequestId, Fatal: false);
            LastFailure = busyFailure;
            return new RenameOperationResult.Refused(busyFailure);
        }

        if (reply is Reply.Refused(500, Codes.RenameFailed, var failMessage, var failRequestId, var failDetails, var failSession))
        {
            // Only POST /session/rename answers rename_failed as an HTTP-level 500 — the flag is set
            // after the journal write succeeds, never before (§ 10.16), so a session was open and is
            // left exactly as it was. A rename that fails after it started is observed as a
            // terminal "failed" RenameOperation through GetRenameAsync instead, not this branch.
            if (failSession is not null) AdoptSession(failSession);
            var reunited = DetailBool(failDetails, "reunited") ?? true;
            var journal = DetailString(failDetails, "journal");
            var detail = reunited
                ? "Nothing moved and nothing was renamed; every rating is exactly where it was. " + failMessage
                : $"The rename journal at {journal ?? "an unknown path"} could not be written. Nothing was renamed. " + failMessage;
            var failFailure = new Failure(FailureKind.Unexpected, "The rename could not start", detail,
                Codes.RenameFailed, failRequestId, Fatal: false);
            LastFailure = failFailure;
            return new RenameOperationResult.Refused(failFailure);
        }

        if (reply is Reply.Refused(404, Codes.NoSession, var noSessionMessage, var noSessionRequestId, _, _))
        {
            State = State == LinkState.Disconnected ? LinkState.Disconnected : LinkState.Connected;
            Snapshot = null;
            var noSessionFailure = new Failure(FailureKind.Unexpected, "The folder is not open",
                "The session closed before the rename could be checked. " + noSessionMessage,
                Codes.NoSession, noSessionRequestId, Fatal: false);
            LastFailure = noSessionFailure;
            return new RenameOperationResult.Refused(noSessionFailure);
        }

        if (reply is Reply.Refused(var otherStatus, var otherCode, var otherMessage, var otherRequestId, _, var otherSession))
        {
            if (otherSession is not null) AdoptSession(otherSession);
            var otherFailure = FailureForCode(otherCode, otherMessage, otherRequestId);
            LastFailure = otherFailure;
            return new RenameOperationResult.Refused(otherFailure);
        }

        if (reply is Reply.Unreachable(UnreachableKind.PinMismatch, _))
        {
            State = LinkState.Disconnected;
            var pinFailure = NotYourServerFailure(_credential?.BaseUrl);
            LastFailure = pinFailure;
            return new RenameOperationResult.Refused(pinFailure);
        }

        if (reply is Reply.Unreachable(var unreachableKind, _))
        {
            State = InSessionOfAnyKind ? LinkState.InSessionUnreachable : State;
            var code = unreachableKind switch
            {
                UnreachableKind.Timeout => Codes.ClientTimeout,
                UnreachableKind.Cancelled => Codes.ClientCancelled,
                _ => Codes.ClientUnreachable,
            };
            var unreachableFailure = UnreachableFailure(code);
            LastFailure = unreachableFailure;
            return new RenameOperationResult.Refused(unreachableFailure);
        }

        var unexpected = UnexpectedFailure(Codes.ClientMalformedResponse, null);
        LastFailure = unexpected;
        return new RenameOperationResult.Refused(unexpected);
    }

    private static bool? DetailBool(JsonElement? details, string name) =>
        details is { } d && d.ValueKind == JsonValueKind.Object &&
        d.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean() : null;

    private static string? DetailString(JsonElement? details, string name) =>
        details is { } d && d.ValueKind == JsonValueKind.Object &&
        d.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static string NewRequestId()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        var base64 = Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return "pc-" + base64;
    }

    /// <summary>§ 5.1.1 steps 4-7: send, retry once on a lost response, resend once on 503, re-enrol
    /// and resend once on 401 — all of the <b>same</b> already-frozen <paramref name="request"/> —
    /// then classify the answer.</summary>
    private async Task<ActionResult> SendActionSequence(FrozenRequest request, CancellationToken ct)
    {
        var reply = await _http!.Send(request, _options.ActionTimeout, ct).ConfigureAwait(false);

        if (reply is Reply.Unreachable(var initialKind, _) && initialKind is not (UnreachableKind.PinMismatch or UnreachableKind.Cancelled))
        {
            reply = await _http.Send(request, _options.ActionTimeout, ct).ConfigureAwait(false);
        }

        if (reply is Reply.Refused(503, Codes.SessionBusy, _, _, var busyDetails, _) && !ct.IsCancellationRequested)
        {
            var retryAfterSeconds = DetailInt(busyDetails, "retryAfterSeconds") ?? 1;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(retryAfterSeconds), ct).ConfigureAwait(false);
                reply = await _http.Send(request, _options.ActionTimeout, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                reply = new Reply.Unreachable(UnreachableKind.Cancelled, "The caller cancelled the request.");
            }
        }

        if (reply is Reply.Refused(401, _, _, _, _, _) && !ct.IsCancellationRequested)
        {
            var reEnrolled = await ReEnrolInPlace(ct).ConfigureAwait(false);
            if (reEnrolled)
            {
                reply = await _http.Send(request, _options.ActionTimeout, ct).ConfigureAwait(false);
            }
            else
            {
                // Re-enrolment itself failed: nothing was applied (a 401 is refused before the token
                // is looked at, § 8.4), but the credential is dead and could not be replaced. Report
                // that directly rather than falling through to a generic mapping of the stale 401.
                var pairingLost = LastFailure ?? PairingLostFailure();
                LastFailure = pairingLost;
                return new ActionResult.Refused(pairingLost, Snapshot);
            }
        }

        return await Finish(request, reply, ct).ConfigureAwait(false);
    }

    private async Task<ActionResult> Finish(FrozenRequest request, Reply reply, CancellationToken ct)
    {
        if (reply is Reply.Ok(var status, var body, var okRequestId) && status is >= 200 and < 300)
        {
            var snapshot = TryParse(body, WireJsonContext.Default.Snapshot);
            if (snapshot is null)
            {
                var malformed = UnexpectedFailure(Codes.ClientMalformedResponse, okRequestId);
                LastFailure = malformed;
                return new ActionResult.Refused(malformed, Snapshot);
            }

            AdoptSession(snapshot);
            return new ActionResult.Applied(snapshot);
        }

        // save_failed / move_failed are always reported, never silently absorbed (§ 5.4) — even
        // though the running server attaches error.session to these 500s (§ 9 observation 1 assumed
        // it would not; it does, so the extra GET below is unreachable in practice but kept as a
        // fallback for a 500 that genuinely carries no session). Checked before the generic
        // session-attached branch so these two codes are never mistaken for a silent resync.
        if (reply is Reply.Refused(var failStatus, var failCode, var failMessage, var failRequestId, _, var failSession)
            && failStatus is >= 500 and < 600
            && (failCode == Codes.SaveFailed || failCode == Codes.MoveFailed))
        {
            if (failSession is not null) AdoptSession(failSession);
            else
            {
                var readSnapshot = await TryReadSessionSnapshot(ct).ConfigureAwait(false);
                if (readSnapshot is not null) Snapshot = readSnapshot;
            }
            var failFailure = FailureForCode(failCode, failMessage, failRequestId);
            LastFailure = failFailure;
            return new ActionResult.Refused(failFailure, Snapshot);
        }

        if (reply is Reply.Refused(var refusedStatus, var refusedCode, var refusedMessage, var refusedRequestId, _, { } session))
        {
            AdoptSession(session);
            var why = Classify(session.LastAction, request.PairToken, request.ClientRequestId, refusedCode);
            return new ActionResult.Resynchronised(session, why);
        }

        if (reply is Reply.Refused(404, Codes.NoSession, _, _, _, null))
        {
            return await ReopenAfterNoSession(ct).ConfigureAwait(false);
        }

        if (reply is Reply.Refused(var status5, var code5, var message5, var requestId5, _, null) && status5 is >= 500 and < 600)
        {
            var readSnapshot = await TryReadSessionSnapshot(ct).ConfigureAwait(false);
            if (readSnapshot is not null) Snapshot = readSnapshot;
            var failure5 = FailureForCode(code5, message5, requestId5);
            LastFailure = failure5;
            return new ActionResult.Refused(failure5, readSnapshot ?? Snapshot);
        }

        if (reply is Reply.Refused(var status4, var code4, var message4, var requestId4, _, null))
        {
            var failure4 = FailureForCode(code4, message4, requestId4);
            LastFailure = failure4;
            return new ActionResult.Refused(failure4, Snapshot);
        }

        if (reply is Reply.Unreachable(UnreachableKind.PinMismatch, _))
        {
            State = LinkState.Disconnected;
            var pinFailure = NotYourServerFailure(_credential?.BaseUrl);
            LastFailure = pinFailure;
            return new ActionResult.Unknown(pinFailure);
        }

        if (reply is Reply.Unreachable(var unreachableKind, _))
        {
            State = LinkState.InSessionUnreachable;
            var code = unreachableKind switch
            {
                UnreachableKind.Timeout => Codes.ClientTimeout,
                UnreachableKind.Cancelled => Codes.ClientCancelled,
                _ => Codes.ClientUnreachable,
            };
            var unreachableFailure = UnreachableFailure(code);
            LastFailure = unreachableFailure;
            return new ActionResult.Unknown(unreachableFailure);
        }

        var unexpected = UnexpectedFailure(Codes.ClientMalformedResponse, null);
        LastFailure = unexpected;
        return new ActionResult.Refused(unexpected, Snapshot);
    }

    /// <summary>§ 8.5's table, row for row, plus the two undo-specific codes it does not cover in
    /// generic terms (§ 10.10).</summary>
    private static ResyncReason Classify(LastAction? lastAction, string? sentPairToken, string? sentClientRequestId, string code)
    {
        if (code == Codes.NothingToUndo) return ResyncReason.UndoAlreadyDone;
        if (code == Codes.NoCurrentPair) return ResyncReason.NoCurrentPair;

        if (sentClientRequestId is not null && lastAction?.ClientRequestId == sentClientRequestId)
            return ResyncReason.LandedEarlier;
        if (sentPairToken is not null && lastAction?.PairToken == sentPairToken)
            return ResyncReason.TokenSpentByOwnEarlierRequest;
        if (lastAction is null)
            return ResyncReason.OutcomeUnknownAfterRestart;
        return ResyncReason.PairMovedElsewhere;
    }

    private async Task<WireSnapshot?> TryReadSessionSnapshot(CancellationToken ct)
    {
        var reply = await _http!.Send(FrozenRequest.Get("/session"), _options.ReadTimeout, ct).ConfigureAwait(false);
        return reply is Reply.Ok(200, var body, _) ? TryParse(body, WireJsonContext.Default.Snapshot) : null;
    }

    private async Task<ActionResult> ReopenAfterNoSession(CancellationToken ct)
    {
        var folder = Snapshot?.Folder;
        if (folder is null)
        {
            var f = UnexpectedFailure(Codes.NoSession, null);
            LastFailure = f;
            return new ActionResult.Refused(f, null);
        }

        var reply = await _http!.Send(FrozenRequest.OpenSession(folder), _options.OpenTimeout, ct).ConfigureAwait(false);
        if (reply is Reply.Ok(var status, var body, _) && status is 200 or 201)
        {
            var snapshot = TryParse(body, WireJsonContext.Default.Snapshot);
            if (snapshot is not null)
            {
                AdoptSession(snapshot);
                return new ActionResult.Resynchronised(snapshot, ResyncReason.SessionReplaced);
            }
        }

        State = LinkState.InSessionUnreachable;
        var failure = UnreachableFailure(Codes.ClientUnreachable);
        LastFailure = failure;
        return new ActionResult.Unknown(failure);
    }

    // ---- RefreshAsync (§ 5.3.4) --------------------------------------------------------------------

    private async Task<ActionResult> RefreshCore(CancellationToken ct)
    {
        if (State == LinkState.Disconnected)
        {
            var connect = await ConnectCore(ct).ConfigureAwait(false);
            if (connect is ConnectResult.Failed failed) return new ActionResult.Unknown(failed.Failure);
        }

        var reply = await _http!.Send(FrozenRequest.Get("/session"), _options.ReadTimeout, ct).ConfigureAwait(false);

        if (reply is Reply.Ok(200, var body, _))
        {
            var snapshot = TryParse(body, WireJsonContext.Default.Snapshot);
            if (snapshot is not null)
            {
                AdoptSession(snapshot);
                return new ActionResult.Applied(snapshot);
            }
        }

        if (reply is Reply.Refused(404, Codes.NoSession, _, _, _, _))
        {
            return await ReopenAfterNoSession(ct).ConfigureAwait(false);
        }

        if (reply is Reply.Refused(401, _, _, _, _, _))
        {
            var reEnrolled = await ReEnrolInPlace(ct).ConfigureAwait(false);
            if (reEnrolled)
            {
                var retry = await _http.Send(FrozenRequest.Get("/session"), _options.ReadTimeout, ct).ConfigureAwait(false);
                if (retry is Reply.Ok(200, var body2, _))
                {
                    var snap2 = TryParse(body2, WireJsonContext.Default.Snapshot);
                    if (snap2 is not null) { AdoptSession(snap2); return new ActionResult.Applied(snap2); }
                }
                if (retry is Reply.Refused(404, Codes.NoSession, _, _, _, _))
                    return await ReopenAfterNoSession(ct).ConfigureAwait(false);
            }
            else
            {
                var failure = LastFailure ?? PairingLostFailure();
                LastFailure = failure;
                return new ActionResult.Refused(failure, Snapshot);
            }
        }

        if (reply is Reply.Unreachable)
        {
            var connect = await ConnectCore(ct).ConfigureAwait(false);
            if (connect is ConnectResult.Connected)
            {
                var retry = await _http!.Send(FrozenRequest.Get("/session"), _options.ReadTimeout, ct).ConfigureAwait(false);
                if (retry is Reply.Ok(200, var body3, _))
                {
                    var snap3 = TryParse(body3, WireJsonContext.Default.Snapshot);
                    if (snap3 is not null) { AdoptSession(snap3); return new ActionResult.Applied(snap3); }
                }
                if (retry is Reply.Refused(404, Codes.NoSession, _, _, _, _))
                    return await ReopenAfterNoSession(ct).ConfigureAwait(false);
            }

            State = LinkState.InSessionUnreachable;
            var f = UnreachableFailure(Codes.ClientUnreachable);
            LastFailure = f;
            return new ActionResult.Unknown(f);
        }

        var unexpected = UnexpectedFailure(Codes.ClientMalformedResponse, null);
        LastFailure = unexpected;
        return new ActionResult.Refused(unexpected, Snapshot);
    }

    // ---- § 5.4: the failure catalogue --------------------------------------------------------------

    private static int? DetailInt(JsonElement? details, string name) =>
        details is { } d && d.ValueKind == JsonValueKind.Object &&
        d.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : null;

    private Failure FailureForCode(string code, string message, string? requestId) => code switch
    {
        Codes.SaveFailed => new Failure(FailureKind.SaveFailed, "The server could not write the ranking file",
            "Nothing was lost — the ratings are still in the server's memory. Check the drive is connected and " +
            "the folder is not read-only, then press the key again. " + message,
            code, requestId, Fatal: false),

        Codes.MoveFailed => new Failure(FailureKind.MoveFailed, "The file could not be moved",
            "It is probably open in another program. Nothing changed; close that program and press the key again. " + message,
            code, requestId, Fatal: false),

        Codes.SessionBusy => new Failure(FailureKind.ServerBusy, "The server is busy",
            "It is finishing something in this folder — probably from the phone. Press the key again in a moment.",
            code, requestId, Fatal: false),

        Codes.ServerShuttingDown => new Failure(FailureKind.ServerShuttingDown, "The server is shutting down",
            "Wait for it to finish, then try again; it will be started if needed.",
            code, requestId, Fatal: false),

        _ => UnexpectedFailure(code, requestId),
    };

    private static Failure UnexpectedFailure(string code, string? requestId) => new(
        FailureKind.Unexpected, "The server answered in a way this program did not expect",
        $"Code {code}, request {requestId}. The details are in the log.", code, requestId, Fatal: false);

    private static Failure UnreachableFailure(string code) => new(
        FailureKind.Unreachable, "The server did not answer",
        "Everything ranked so far is already on disk. This last one may or may not have counted; press the key " +
        "again and the server will sort it out — it never counts a vote twice.",
        code, null, Fatal: false);

    private Failure NotYourServerFailure(string? baseUrl) => new(
        FailureKind.NotYourServer, "That is not your server",
        $"Something at {baseUrl ?? "the paired address"} answered with a certificate this PC has not paired with. " +
        "Nothing was sent to it.",
        Codes.ClientPinMismatch, null, Fatal: true);

    private Failure ServerNotRunningFailure() => new(
        FailureKind.ServerNotRunning, "The Rank Master server is not running",
        $"It could not be started from `{_options.ServerExecutable ?? "(no server executable configured)"}`. " +
        "Start RankMaster2.Tray.exe yourself, then try again. If it is running, it keeps its files somewhere " +
        $"other than `{_options.ServerDataDirectory}`; set the server data directory in settings.",
        Codes.ClientUnreachable, null, Fatal: false);

    private Failure PairingFailedFailure(string? pairingCode, string? detail) => new(
        FailureKind.PairingFailed, "Pairing with the server failed",
        $"The server refused the pairing code ({pairingCode ?? "?"}). Try again; if it keeps failing, restart the server."
        + (string.IsNullOrEmpty(detail) ? "" : " " + detail),
        Codes.InvalidPairingCode, null, Fatal: false);

    private Failure PairingLostFailure() => new(
        FailureKind.PairingLost, "This PC is no longer paired with the server",
        "The server did not accept this PC's credential and a new one could not be made. Restart the server, then try again.",
        Codes.TokenRevoked, null, Fatal: true);

    private static Failure FolderNotFoundFailure(string folder) => new(
        FailureKind.FolderNotFound, "That folder does not exist", folder, Codes.FolderNotFound, null, Fatal: false);

    private static Failure FolderNotADirectoryFailure(string folder) => new(
        FailureKind.FolderNotADirectory, "That is a file, not a folder", folder, Codes.FolderNotADirectory, null, Fatal: false);

    private static Failure FolderAccessDeniedFailure(string folder) => new(
        FailureKind.FolderAccessDenied, "That folder cannot be read", $"Windows refused access to {folder}.",
        Codes.FolderAccessDenied, null, Fatal: false);

    private static Failure FolderNotRankableFailure(string folder, JsonElement? details)
    {
        var stills = DetailInt(details, "stills") ?? 0;
        var videos = DetailInt(details, "videos") ?? 0;
        return new Failure(FailureKind.FolderNotRankable, "Nothing to rank here",
            $"{folder} has {stills} pictures and {videos} videos; at least two of one kind are needed.",
            Codes.FolderNotRankable, null, Fatal: false);
    }

    private static Failure LibraryJsonUnreadableFailure(string folder) => new(
        FailureKind.LibraryJsonUnreadable, "The ranking file is damaged",
        $"rankmaster_db.json in {folder} does not parse. It was not touched. Restore it from a backup or move it aside to start over.",
        Codes.LibraryJsonUnreadable, null, Fatal: false);

    private static Failure FolderLockedFailure(string folder) => new(
        FailureKind.FolderLocked, "Another program is using this folder",
        $"The old Rank Master, or a second server, holds {folder}. Close it and try again.",
        Codes.FolderLocked, null, Fatal: false);

    // ---- plumbing -----------------------------------------------------------------------------------

    private static T? TryParse<T>(byte[] body, JsonTypeInfo<T> typeInfo)
    {
        try { return JsonSerializer.Deserialize(body, typeInfo); }
        catch (JsonException) { return default; }
    }

    public ValueTask DisposeAsync()
    {
        _http?.Dispose();
        _http = null;
        return ValueTask.CompletedTask;
    }
}
