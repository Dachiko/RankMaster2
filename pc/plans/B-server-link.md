# Part B — the server link

**Status: plan.** Owns `pc/src/RankMaster2.Pc/Link/`. Writes no pixels, opens no windows, computes no
rating. Everything the PC client says to the server passes through here, and this is the only place in
the PC client that ever sees a `pairToken`.

Read in this order before touching a file: `SERVER_SPEC.md` § 4, § 5, § 8, § 9, § 10, § 13.3, § 13.4;
`PC_CLIENT_PLAN.md` § 1, § 6.1, § 6.4, § 6.8; `CLIENT_PLAN.md` § 3.2 and § 3.6.3;
`android/…/net/Rm2Client.kt` and `…/ui/rank/RankViewModel.kt` (the function `act`); `src/rm2ctl/Rm2Api.cs`
(the certificate callback) and `src/RankMaster2.Tray/PairingChannel.cs` (the offer reader).

Where this plan and `SERVER_SPEC.md` disagree, the spec wins and this plan is wrong. Nothing in this
plan changes the server; § 9 lists what the contract could say better, as observations only.

---

## 1. Decisions, settled before any code

| Question | Answer | Why |
|---|---|---|
| Who holds the `pairToken` | **the link, privately.** No public member of `Link/` accepts or returns a token | the double vote needs a caller that can hand a fresh token to an old action; a caller that never sees a token cannot (§ 5.1) |
| Retry | **exactly one, inside the call, of the identical body bytes**, only on a transport failure | `SERVER_SPEC.md` § 13.3; `RankViewModel.act` is the shape. The body is frozen as `byte[]` before the first send, so no code path can rebuild it with a different token |
| Two actions at once | **impossible**: the second call throws `InvalidOperationException` | one token, one action in flight, exactly as `RankState.busy` gates the phone. Overlap is a bug in the caller, not a state to handle |
| Acting on the pair you saw | every pair action takes the `pairSeq` the caller rendered; a mismatch is `NotSent`, nothing is transmitted | closes the gap between the link adopting a snapshot and the surface repainting. `pairSeq` is compared locally and **never put in a body** (§ 9.1) |
| Background work | **none.** No heartbeat, no timer, no reconnect loop, no event | every change to the link's state is the return value of a call the surface made. Nothing can send an action the owner did not just ask for |
| Address | **the one the server publishes** in `<data>/pairing.json` (`host`, `port`), stored at enrolment | the server binds exactly one address (`SERVER_SPEC.md` § 2) and writes it into the offer; guessing between `127.0.0.1` and a LAN address is unnecessary. On Windows, traffic to the machine's own LAN address never leaves the machine |
| Loopback listener (`PC_CLIENT_PLAN.md` § 8) | **not needed by this plan; not requested** | § 5.2.5. The server is complete and audited; this plan connects to whatever it binds. If the owner answers "yes" to `PC_CLIENT_PLAN.md` § 13 question 4, the server change is a separate task and this plan needs no code change |
| Trust | pin the SHA-256 of the certificate's DER encoding, learned from `pairing.json` in the server's owner-only data directory; never trust-on-first-use over the network | the file is `0600` in a directory only the owner's account can write: it is the trust root. `rm2ctl`'s TOFU mode exists for a diagnostic tool and is deliberately not copied |
| Pairing | silent: `pair.request` → `pairing.json` → pin → `POST /pair` → store. Same code path for first run and for an address change | `SERVER_SPEC.md` § 10.1.1; `PC_CLIENT_PLAN.md` § 6.1. No QR, no typing, no dialog |
| Server not running | ping fails and the offer channel is silent → start `RankMaster2.Tray.exe`, wait for `ready: true`, up to 10 s; then report | `PC_CLIENT_PLAN.md` § 13 question 6 defaults to "start it". Off by one option if the owner says no |
| Open | `DELETE /session` (404 is fine) then `POST /session` | `PC_CLIENT_PLAN.md` § 6.8: Resume never restores a pair, and this dissolves `409 session_already_open` silently as the phone does (`CLIENT_PLAN.md` § 3.6.3) |
| `409 stale_pair_token` | adopt `error.session`, classify by `lastAction`, **never re-send** | `SERVER_SPEC.md` § 8.5 |
| `5xx` on an action | `GET /session` once (a pure read), then report the failure with that snapshot | a `save_failed` after a moved file bumps `pairSeq` (§ 8.3) and the 500 carries no snapshot; the read is safe and puts the surface back in step |
| `401` mid-session | re-enrol through the data directory once, then re-send the identical body once | a 401 is refused before the token is looked at (§ 8.4), so nothing was applied; the same token is still the right one |
| `503 session_busy` | wait `Retry-After` (1 s), re-send the identical body once | nothing was applied (§ 7.3); the phone may be mid-save in the same folder |
| HTTP version | **HTTP/1.1, exact** | the server speaks h2 by Kestrel default (seen in its log on this box). The link makes small JSON calls only; 1.1 makes connection-drop fault injection deterministic and removes a flow-control budget the phone was bitten by |
| Proxy | `UseProxy = false` | a Windows system proxy would otherwise be offered a request to the PC's own LAN address |
| JSON | `System.Text.Json`, source-generated context, `PropertyNameCaseInsensitive = false`, unknown members ignored | startup: no reflection warm-up; forward compatibility: `SERVER_SPEC.md` § 2 lets the server add fields |
| Dependencies of `Link/` | **the BCL only.** No `RankMaster2.Core`, no Avalonia, no server assembly | so the test project can compile the folder directly (§ 6.1) and so nothing in the client can construct a ranking type by accident (`PC_CLIENT_PLAN.md` § 6.9) |
| Credential file | `%LOCALAPPDATA%\RankMaster2\pc\link.json` (Linux: `$XDG_DATA_HOME/rankmaster2/pc/link.json`), owner-only, plaintext | the server's own data directory next door holds the TLS private key with the same protection. DPAPI would protect one secret while its neighbour stays plain, and cannot be verified here |
| Words shown to the owner | the link supplies `Title` and `Detail` in English for every `Failure` it returns; part E lays them out | the link is the only part that knows which refusal it just received and what was and was not applied |

### 1.1 Deliberately absent

None of these is an oversight:

- **No `/media/*` calls.** The PC reads pixels from disk (`PC_CLIENT_PLAN.md` § 1.2). The `links` in a
  snapshot are carried through untouched and unused.
- **No `/libraries/roots` or `/libraries/browse`.** The PC has a native folder dialog
  (`PC_CLIENT_PLAN.md` § 2.1). Adding them later is two methods and no new concept.
- **No token, `pairSeq` or `clientRequestId` in the public API.** Not as a parameter, not as a property,
  not in a result. `Snapshot.PairToken` exists because the wire type mirrors § 9 exactly; § 4 explains
  why it is harmless there and § 6.4 test N5 asserts it is the only place.
- **No "retry" operation.** There is nothing for a retry button to call. After `Unknown`, the owner's
  next key press is a new intention that happens to carry the old token, which is safe (§ 5.1.4).
- **No events, no `INotifyPropertyChanged`, no dispatcher.** Results come back from awaited calls.
- **No last-folder store.** Whose it is (A or E) is not this part's decision; the link exposes
  `Snapshot.Folder`, the server's canonical spelling, for whoever keeps it.
- **No offline mode, no local engine, no rename.** `PC_CLIENT_PLAN.md` § 1.1 and § 6.6.
- **No token expiry handling.** `expiresAt` is `null` on this server (`SERVER_RUNNING.md` § 3); a 401 is
  handled the same way whatever caused it.
- **No IPv6 special cases beyond `UriBuilder`.** The server binds one parsed `IPAddress`; `UriBuilder`
  brackets it correctly.

---

## 2. What is shared, lifted, or deliberately not

There are already three C# clients of this server: `src/rm2ctl/Rm2Api.cs`,
`tests/RankMaster2.Server.Tests/Harness/Rm2Client.cs`, and the server's own DTOs in
`src/RankMaster2.Server/Sessions/SessionSnapshot.cs`. A fourth is justified only if it is deliberately
different, so:

| Existing code | Decision | Reason |
|---|---|---|
| `Rm2Api` certificate callback (`ServerCertificateCustomValidationCallback`, fingerprint = `"sha256:" + lowercase hex of SHA-256(RawData)`, `Normalise`) | **lift** into `Link/Transport/PinnedHandler.cs`, minus the TOFU and `--insecure` branches | it is correct and matches what `/ping` and `pairing.json` publish. The two fallbacks are for a diagnostic tool; the PC has a trust root on disk and needs neither |
| `Rm2Api` as a whole | **not referenced** | it is an `Exe` project, takes a `Journal` in its constructor, returns `Reply` (raw `JsonElement`) and resolves media links — none of which the PC wants. Referencing it would drag `QRCoder` into a startup-sensitive client |
| `Pairing.DefaultDataDirectory()` (rm2ctl) and `Rm2SecurityOptions.ResolveDataDirectory()` (server) | **lift** the resolution rules into `Link/Enrolment/ServerPaths.cs`: `RM2_DATA_DIR`, else `%LOCALAPPDATA%\RankMaster2\server`, else `$XDG_DATA_HOME/rankmaster2/server`, else `~/.local/share/rankmaster2/server` | both agree; the PC must look where the server actually writes. A test pins this against the server's own resolution by starting the server with `RM2_DATA_DIR` |
| `PairingChannel.Request` (Tray) — write `pair.request`, wait for an offer whose `expiresAt` is strictly newer than the one already there | **lift** the algorithm into `Link/Enrolment/OfferChannel.cs`; read the full offer (`host`, `port`, `certificateFingerprint`, `code`, `expiresAt`), not just the display fields | it already solves the stale-offer trap `rm2ctl` also documents. It is `internal` to a WinForms exe, so it is copied, not referenced |
| Server DTOs (`SessionSnapshot` and friends) | **not referenced; mirrored** in `Link/Wire/` | referencing `RankMaster2.Server` pulls the ASP.NET Core shared framework into the client and inverts the dependency. The test harness refuses the same reference for the same reason. A test parses live server output into the mirror instead (§ 6.3) |
| Test harness `Rm2Client` / `Snapshot` | **not shared** | test-only, `JsonElement`-based, throws xunit exceptions |
| `rm2ctl/ScratchLibrary.EncodePng` (self-contained PNG writer, no imaging library) | **lift** into the test project as `Fixtures/ScratchFolder.cs` | the link's tests need a folder that opens (two or more media files) and nothing more; `rm2ctl cycle` proves the server accepts these PNGs |
| `Rm2Client.kt` / `Rm2Result` / `RankViewModel.act` (Kotlin) | **the shape to follow**, translated: three-valued result, refusal carrying the snapshot, retry inside the call | it is in daily use and passed the phone's adversarial review |
| `CLIENT_PLAN.md` § 3.6.3 conflict rule, `BrowseViewModel.attemptOpen` | **followed** as the open sequence, with the PC's stronger form (always `DELETE` first, `PC_CLIENT_PLAN.md` § 6.8) | one user, never both at once |

Each lifted block carries a one-line comment naming its source file so a fix there can be mirrored here.

---

## 3. Shape

```
pc/src/RankMaster2.Pc/Link/
  ISessionLink.cs            the frozen seam (§ 4.1) — interface, result types, Failure, Side
  SessionLink.cs             the implementation: state, the action protocol (§ 5.1), open/close (§ 5.3)
  LinkOptions.cs             everything the link is told by its host (§ 4.3)
  Paths.cs                   DefaultCredentialDirectory(), DefaultTrayExecutable() — the two host-platform defaults
  Wire/
    Snapshot.cs              § 9 mirror: Snapshot, Counts, Pair, MediaRef, Links, LastAction (§ 4.2)
    Messages.cs              Ping, PingSession, PairedDevice, ErrorEnvelope, ErrorBody; request bodies (internal)
    WireJson.cs              JsonSerializerContext + the one JsonSerializerOptions
    Codes.cs                 the § 5 code strings the link branches on; synthetic client_* codes
  Transport/
    PinnedHandler.cs         SocketsHttpHandler factory: pin, HTTP/1.1, no proxy, connect timeout
    Rm2Http.cs               one HttpClient; Send(request) → Reply {Ok | Refused | Unreachable} (§ 5.5)
    FrozenRequest.cs         method, path, byte[] body, token, requestId — built once, sent up to twice
  Enrolment/
    Credential.cs            {baseUrl, fingerprint, token, deviceId, pairedAt}; load/save owner-only
    ServerPaths.cs           ServerPaths.DataDirectory(): where the server keeps pairing.json (lifted, § 2)
    OfferChannel.cs          pair.request → pairing.json (lifted, § 2)
    Enroller.cs              the sequence of § 5.2.3
    ServerProcess.cs         start the tray/console host, never stop it (§ 5.2.4)

pc/tests/RankMaster2.Pc.Link.Tests/
  RankMaster2.Pc.Link.Tests.csproj   compiles ../../src/RankMaster2.Pc/Link/**/*.cs directly (§ 6.1)
  Harness/RealServer.cs              the real Kestrel server as a child process
  Harness/TapHandler.cs              records every request; scripts faults per request (§ 6.2)
  Fixtures/ScratchFolder.cs          real PNGs, lifted from rm2ctl
  …tests (§ 6.3, § 6.4)
```

Namespace `RankMaster2.Pc.Link`; wire types in `RankMaster2.Pc.Link.Wire`. Everything outside
`ISessionLink.cs`, `Wire/Snapshot.cs`, `Wire/Messages.cs` and `LinkOptions.cs` is `internal`.
`[assembly: InternalsVisibleTo("RankMaster2.Pc.Link.Tests")]` is the only exception.

---

## 4. The seam, exactly

Four parts compile against this. It is frozen once execution begins; change it by changing this file
first.

### 4.1 `ISessionLink`

```csharp
namespace RankMaster2.Pc.Link;

using RankMaster2.Pc.Link.Wire;

/// <summary>
/// The PC client's only way to talk to the server. One instance per process.
///
/// Call it from one thread, one call at a time: a call made while another is in flight throws
/// InvalidOperationException. Nothing here runs in the background — every change to State or
/// Snapshot is the return value of a call the caller made.
///
/// No member of this interface accepts or returns a pairToken. The link holds the token of the
/// pair in Snapshot and sends it itself; a lost response is retried inside the call with the same
/// token (SERVER_SPEC.md § 13.3). This is what makes a double vote impossible to express.
/// </summary>
public interface ISessionLink : IAsyncDisposable
{
    /// <summary>Where the link stands. Changes only as the result of a call.</summary>
    LinkState State { get; }

    /// <summary>The last snapshot adopted, or null before a session is open. The pair on screen.</summary>
    Snapshot? Snapshot { get; }

    /// <summary>The failure the last call ended with, or null. For a status line; results are the primary channel.</summary>
    Failure? LastFailure { get; }

    /// <summary>True while a call is in flight. Callers ignore keys while it is true.</summary>
    bool IsBusy { get; }

    /// <summary>
    /// Find, trust and authenticate with the server: stored credential, else enrol through the
    /// server's data directory, starting the server first if it is not running (§ 5.2). Idempotent.
    /// Never opens a session.
    /// </summary>
    Task<ConnectResult> ConnectAsync(CancellationToken ct = default);

    /// <summary>
    /// Open a folder: connects if needed, then DELETE /session (404 is fine) and POST /session, so
    /// the server runs Start() and picks a fresh pair. Resolves a session another client left open
    /// without asking (§ 5.3).
    /// </summary>
    Task<OpenResult> OpenAsync(string folder, CancellationToken ct = default);

    /// <summary>
    /// Vote on the pair the caller is looking at. <paramref name="onPairSeq"/> is Snapshot.PairSeq
    /// as rendered; if it no longer matches, nothing is sent and the result is NotSent(PairMoved).
    /// It is compared locally and never transmitted (SERVER_SPEC.md § 9.1).
    /// </summary>
    Task<ActionResult> VoteAsync(Side winner, long onPairSeq, CancellationToken ct = default);
    Task<ActionResult> SkipAsync(long onPairSeq, CancellationToken ct = default);
    Task<ActionResult> DiscardAsync(Side side, long onPairSeq, CancellationToken ct = default);
    Task<ActionResult> SpecialAsync(Side side, long onPairSeq, CancellationToken ct = default);

    /// <summary>POST /session/undo. Not pair-scoped, takes no pairSeq. NotSent(UndoNotAvailable) when Snapshot.UndoAvailable is false.</summary>
    Task<ActionResult> UndoAsync(CancellationToken ct = default);

    /// <summary>POST /session/save. Never changes the pair.</summary>
    Task<ActionResult> SaveAsync(CancellationToken ct = default);

    /// <summary>
    /// GET /session — a pure read. Reconnects (and may start the server) if the server is
    /// unreachable; re-opens the same folder if the server answers no_session. Never sends an action.
    /// This is what "Try again" calls after Unknown.
    /// </summary>
    Task<ActionResult> RefreshAsync(CancellationToken ct = default);

    /// <summary>
    /// DELETE /session. 404 no_session is success. Never throws; a failure is recorded in
    /// LastFailure and otherwise ignored — the next OpenAsync closes whatever is left. Pass a short
    /// token on Esc (PC_CLIENT_PLAN.md § 6.8 says 500 ms) and do not wait for more.
    /// </summary>
    Task CloseAsync(CancellationToken ct = default);
}

public enum LinkState
{
    /// <summary>Nothing has been tried, or the last connect failed. LastFailure says why.</summary>
    Disconnected,
    /// <summary>Authenticated with the server; no session open.</summary>
    Connected,
    /// <summary>A session is open and Snapshot is current as of the last call.</summary>
    InSession,
    /// <summary>A session was open and the last call got no answer. Snapshot is the last known state and still holds its token.</summary>
    InSessionUnreachable,
}

public enum Side { Left, Right }

/// <summary>What ConnectAsync ends with.</summary>
public abstract record ConnectResult
{
    /// <summary>Authenticated. <paramref name="Enrolled"/> is true when a new pairing was made on this call.</summary>
    public sealed record Connected(string BaseUrl, bool Enrolled, bool StartedServer) : ConnectResult;
    public sealed record Failed(Failure Failure) : ConnectResult;
}

/// <summary>What OpenAsync ends with.</summary>
public abstract record OpenResult
{
    /// <summary><paramref name="ReplacedOther"/>: a session on another folder was closed first, silently.</summary>
    public sealed record Opened(Snapshot Snapshot, bool ReplacedOther) : OpenResult;
    public sealed record Failed(Failure Failure) : OpenResult;
}

/// <summary>What every action, save and refresh ends with. Always look at Snapshot first: three of
/// the five cases carry the truth and need nothing said.</summary>
public abstract record ActionResult
{
    /// <summary>The server applied this request and answered 2xx. Snapshot is the new state.</summary>
    public sealed record Applied(Snapshot Snapshot) : ActionResult;

    /// <summary>
    /// The server refused with the current state attached (409 with error.session, or a reopen
    /// after no_session). Nothing this request asked for happened now; Snapshot is the truth.
    /// Usually nothing to say to the owner; <paramref name="Why"/> lets the surface choose.
    /// </summary>
    public sealed record Resynchronised(Snapshot Snapshot, ResyncReason Why) : ActionResult;

    /// <summary>
    /// The server answered and said no, and the owner can do something about it. Snapshot is the
    /// state after a read (may be null if that read also failed). Show Failure.
    /// </summary>
    public sealed record Refused(Failure Failure, Snapshot? Snapshot) : ActionResult;

    /// <summary>
    /// No definite answer after the retry. The link kept the token: the next action the owner asks
    /// for is sent with it, which the server applies once or refuses as stale (§ 13.3). Show Failure
    /// (it says to press again or try again). State is InSessionUnreachable.
    /// </summary>
    public sealed record Unknown(Failure Failure) : ActionResult;

    /// <summary>Nothing was transmitted. Not an error; the surface repaints from Snapshot.</summary>
    public sealed record NotSent(NotSentReason Why) : ActionResult;
}

public enum ResyncReason
{
    /// <summary>lastAction.clientRequestId equals the id this call sent: this exact request landed on an earlier attempt.</summary>
    LandedEarlier,
    /// <summary>lastAction.pairToken equals the token sent but the id differs: an earlier request of ours spent it.</summary>
    TokenSpentByOwnEarlierRequest,
    /// <summary>Neither matched: the pair moved for another reason (the phone, an undo).</summary>
    PairMovedElsewhere,
    /// <summary>lastAction was null: the session was reopened or the server restarted (§ 13.4). The fate of this request is unknown.</summary>
    OutcomeUnknownAfterRestart,
    /// <summary>The server answered no_session; the link reopened the same folder. Cues and session votes restarted.</summary>
    SessionReplaced,
    /// <summary>409 nothing_to_undo: the undo had already been done.</summary>
    UndoAlreadyDone,
    /// <summary>409 no_current_pair: the session is exhausted.</summary>
    NoCurrentPair,
}

public enum NotSentReason { NoSession, PairMoved, Exhausted, UndoNotAvailable }

/// <summary>
/// Something the owner can act on, in words the surface shows as they are. Everything the owner
/// cannot act on is absorbed by the link and never becomes a Failure (§ 5.4).
/// </summary>
/// <param name="Code">The § 5 code, or a client_* synthetic code, for the log. Never shown as the message.</param>
/// <param name="RequestId">The server's X-Request-Id when there was an answer, for the log.</param>
/// <param name="Fatal">True when nothing further can work until the owner acts (wrong server, pairing lost): the surface returns to the start screen.</param>
public sealed record Failure(
    FailureKind Kind,
    string Title,
    string Detail,
    string Code,
    string? RequestId,
    bool Fatal);

public enum FailureKind
{
    ServerNotRunning, NotYourServer, PairingFailed, PairingLost,
    FolderNotFound, FolderNotADirectory, FolderAccessDenied, FolderNotRankable, LibraryJsonUnreadable, FolderLocked,
    SaveFailed, MoveFailed, ServerBusy, ServerShuttingDown, Unreachable, Unexpected,
}
```

Rules the implementer keeps and the tests assert:

- `Snapshot` is replaced, never mutated. Every `Applied`/`Resynchronised`/`Opened` also sets the
  `Snapshot` property to the same instance before returning.
- `IsBusy` is true from entry to return of every method, including `CloseAsync`.
- `ct` cancellation aborts the *wait*, not the protocol: a cancelled action returns `Unknown` with
  `Code = "client_cancelled"` and keeps the token, exactly like a timeout. The retry is not attempted
  after cancellation.
- `DisposeAsync` does **not** close the session (the host decides that with `CloseAsync`); it disposes
  the `HttpClient`.

### 4.2 The wire types (`Wire/Snapshot.cs`)

Field for field with `SERVER_SPEC.md` § 9, names as on the wire via `JsonPropertyName`, nullability as
the spec's "Null?" column. Records, `init`-only, read-only collections. No behaviour beyond the four
convenience properties shown.

```csharp
namespace RankMaster2.Pc.Link.Wire;

public sealed record Snapshot(
    string SessionId, string State, string Folder, string FolderName, string Policy, string OpenedAt,
    int PrefetchPairs, int SessionVotes, Counts Counts, double Progress, int ProgressPercent,
    IReadOnlyList<string> Cues, Pair? Pair, string? PairToken, long PairSeq,
    IReadOnlyList<Pair> WarmPairs, bool UndoAvailable, LastAction? LastAction, string? LastSavedAt)
{
    public bool IsRanking => State == "ranking";
    public bool IsExhausted => State == "exhausted";
}

public sealed record Counts(int Total, int Rankable, int Unranked, int Stills, int Videos);

public sealed record Pair(MediaRef Left, MediaRef Right);

public sealed record MediaRef(string Id, string Kind, long? SizeBytes, string? MediaVersion, Links Links)
{
    public bool IsStill => Kind == "still";
    public bool IsVideo => Kind == "video";
    /// <summary>§ 11.3: the file has gone from the folder under the session. The only action left is discard, which the server turns into drop_missing.</summary>
    public bool IsMissing => SizeBytes is null;
}

/// <summary>§ 9.3. Carried through unused: the PC reads pixels from disk. Never rebuilt, never parsed.</summary>
public sealed record Links(string Meta, string? Still, string? Thumb, string? Video);

public sealed record LastAction(
    long Seq, string Type, string? PairToken, string? ClientRequestId, string? Winner, string? Side,
    string? Id, string? RestoredId, string? UndoneType, string At);

public static class ActionTypes
{
    public const string Vote = "vote", Skip = "skip", Discard = "discard", Special = "special",
                        Undo = "undo", DropMissing = "drop_missing";
}
```

`pairSeq` is `ulong` on the server and `long` here: it starts at 0 and increments by one per action;
the difference is unreachable and `long` is what the rest of the client's arithmetic wants.

`Snapshot.PairToken` and `LastAction.PairToken` are present because the type is a faithful mirror and
because `Resynchronised.Why` is computed from them. They are strings the surface has no use for, and
there is no member anywhere in the public API that accepts one — which is the property that matters.
Test N5 (§ 6.4) asserts it.

`Wire/Messages.cs` (public): `Ping(string Product, string ApiVersion, string Version, bool Ready,
bool Authenticated, string CertificateFingerprint, string ServerTime, PingSession? Session)`,
`PingSession(bool Open, string? SessionId, string? Folder, string? State)`,
`PairedDevice(string DeviceId, string? DeviceName, string Token, string IssuedAt, string? ExpiresAt)`,
`ErrorEnvelope(ErrorBody Error)`, `ErrorBody(string Code, string Message, string? RequestId,
JsonElement? Details, Snapshot? Session)`. Request bodies (`internal`): `OpenSessionRequest(string
Folder)`, `VoteRequest(string PairToken, string Winner, string ClientRequestId)`,
`SkipRequest(string PairToken, string ClientRequestId)`, `SideActionRequest(string PairToken,
string Side, string ClientRequestId)`, `CancelRequest(string ClientRequestId)`, `PairRequest(string
Code, string DeviceName)`. Declared as types, not anonymous objects, so `pairToken` and
`clientRequestId` cannot be forgotten on an action — the same reasoning as `Rm2Wire.kt`.

`WireJson.cs`: one `[JsonSerializable]` context listing every type above, `JsonSerializerDefaults.Web`
naming, `DefaultIgnoreCondition = Never` (the server writes nulls and so do we), unknown members
ignored (the default). `ErrorBody` is decoded **field by field**, as `OkHttpRm2Client.refuse` does:
if `error.session` fails to parse, `code` and `message` must still come through.

### 4.3 `LinkOptions`

```csharp
public sealed record LinkOptions
{
    /// <summary>Where link.json lives. Default: %LOCALAPPDATA%\RankMaster2\pc (Linux: $XDG_DATA_HOME/rankmaster2/pc).</summary>
    public string CredentialDirectory { get; init; } = Paths.DefaultCredentialDirectory();
    /// <summary>The running server's data directory (pairing.json). Default: the server's own resolution (§ 2).</summary>
    public string ServerDataDirectory { get; init; } = ServerPaths.DataDirectory();   // class and property differ on purpose: a same-named string property would shadow the type
    /// <summary>Start the server when it is not running. PC_CLIENT_PLAN.md § 13 q.6 default.</summary>
    public bool StartServerIfNotRunning { get; init; } = true;
    /// <summary>The command that starts it. Default on Windows: {app dir}\..\tray\RankMaster2.Tray.exe, no arguments.</summary>
    public string? ServerExecutable { get; init; } = Paths.DefaultTrayExecutable();
    public IReadOnlyList<string> ServerArguments { get; init; } = [];
    /// <summary>Recorded against the token by the server; shown in its device list.</summary>
    public string DeviceName { get; init; } = "PC (" + Environment.MachineName + ")";
    /// <summary>One line per request/response for the host's log file. Never carries a token or a code.</summary>
    public Action<string>? Log { get; init; }
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan ActionTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan OpenTimeout { get; init; } = TimeSpan.FromSeconds(120);
    public TimeSpan ServerStartTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan OfferTimeout { get; init; } = TimeSpan.FromSeconds(8);
}

public static class SessionLinkFactory
{
    public static ISessionLink Create(LinkOptions options);
}
```

The `internal` constructor `SessionLink(LinkOptions, Func<HttpMessageHandler, HttpMessageHandler>?
wrap)` exists for the tests: `wrap` receives the pinned production handler and returns the handler to
use, which is how `TapHandler` (§ 6.2) sits between the link and the real server.

---

## 5. The hard problems, and how they are solved

### 5.1 Never twice — the action protocol

This is the part that protects the owner's data. The server has already made it *possible* to be safe
(`SERVER_SPEC.md` § 8, § 13.3); the link's job is to make it *impossible* to be unsafe.

#### 5.1.1 The sequence

Every pair action (`vote`, `skip`, `discard`, `special`) runs this, and nothing else runs a POST to
those routes:

```
1. gate       IsBusy? → throw InvalidOperationException. Set IsBusy.
2. check      State not InSession/InSessionUnreachable → NotSent(NoSession)
              Snapshot.Pair is null → NotSent(Exhausted)
              onPairSeq != Snapshot.PairSeq → NotSent(PairMoved)
3. freeze     token = Snapshot.PairToken; id = "pc-" + 22 base64url chars of 16 random bytes
              body = UTF-8 bytes of the typed request record   ← built ONCE, a byte[]
              request = FrozenRequest(POST, path, body, token, id)
4. send       reply = Rm2Http.Send(request, ActionTimeout)
5. once more  if reply is Unreachable and not PinMismatch and not Cancelled:
                  reply = Rm2Http.Send(request, ActionTimeout)          ← the same byte[]
              if reply is Refused 503 session_busy:
                  wait Retry-After (default 1 s); reply = Send(request)  ← once
              if reply is Refused 401 (unauthenticated | invalid_token | token_revoked):
                  Enrol (§ 5.2.3); if it succeeded, reply = Send(request) ← once, same bytes, new bearer
6. finish     Ok(snapshot)                       → adopt; Applied
              Refused with error.session         → adopt; Resynchronised(classify(lastAction, token, id))
              Refused 5xx without session        → snapshot = GET /session (ReadTimeout) if it answers
                                                   → Refused(failureFor(code), snapshot)
              Refused 404 no_session             → the server restarted or the phone closed the folder:
                                                   POST /session {Snapshot.Folder} (OpenTimeout) → adopt
                                                   → Resynchronised(SessionReplaced). This request is NOT re-sent:
                                                   the pair it named no longer exists (§ 5.1.5)
              Refused other 4xx without session  → Refused(failureFor(code), Snapshot)   (a 400 here is a bug → Unexpected)
              Unreachable                        → State = InSessionUnreachable; Unknown(failureFor(unreachable))
7. release    clear IsBusy. The token variable dies with the call.
```

`classify` is `SERVER_SPEC.md` § 8.5's table, row for row: `lastAction?.ClientRequestId == id` →
`LandedEarlier`; else `lastAction?.PairToken == token` → `TokenSpentByOwnEarlierRequest`; else
`lastAction is null` → `OutcomeUnknownAfterRestart`; else `PairMovedElsewhere`. For
`nothing_to_undo` → `UndoAlreadyDone`; for `no_current_pair` → `NoCurrentPair`.

Step 5 sends **the same `byte[]`**. There is no code that serialises an action body anywhere except
step 3, and step 3 runs exactly once per call. A reviewer looking for a double vote has one function
to read.

`undo` runs the same sequence without step 2's pair checks (it checks `UndoAvailable` instead) and
without a token in the body. `save` runs it with `{}` as the body. `refresh` is a GET and is
described in § 5.3.4.

#### 5.1.2 Why the retry is exactly one

Zero retries makes every timeout an `Unknown` the owner has to resolve by pressing again. Two or more
buys nothing: if the server is down, the second attempt already told us; if it is up, the first retry
either applied or was refused as stale. One is what the phone does and what `SERVER_SPEC.md` § 13.3
describes. The 503 and 401 resends are not retries of a lost response — both are definite refusals
that applied nothing — and each is bounded to once.

#### 5.1.3 Why a `pin mismatch` is never retried

A certificate that does not match the pin means the thing that answered is not the server we paired
with. Retrying would send the bearer token again to whatever it is. `Rm2Http` recognises the
`PinMismatchException` its own callback threw (walking `InnerException`s, as `isPinMismatch` does in
Kotlin) and returns `Unreachable(PinMismatch: true)`; the protocol stops and the result is
`Failure(NotYourServer, Fatal: true)`.

#### 5.1.4 After `Unknown`

The link keeps `Snapshot` — with its token — and goes to `InSessionUnreachable`. Two things can happen
next, both safe:

- The owner presses a key. The action is sent with the *retained* token (and a new request id). If the
  lost request had landed, the server answers `409 stale_pair_token` with the truth and the link adopts
  it: one vote. If it had not, the server applies this one: one vote.
- The surface calls `RefreshAsync`. That is a GET; it adopts whatever the server says. If the pair has
  moved, the owner sees the new pair and votes on *that* — a new intention, not a replay.

The one thing that would be wrong — the link resyncing and then sending the *old* action against the
*new* token — cannot happen because the link does not remember the old action once step 7 has run.
There is no queue of pending intentions anywhere in `Link/`. Test N3 and N4 assert the observable
consequence.

#### 5.1.5 The § 13.4 case

If the server dies with a request in flight, the retry gets connection refused → `Unknown`. When the
server is back, `RefreshAsync` gets `404 no_session`, reopens the folder (§ 5.3.4), and returns
`Resynchronised(SessionReplaced)`; a subsequent action's `lastAction` is null, which is
`OutcomeUnknownAfterRestart`. The link never replays. The surface may say one line ("the server
restarted; the last vote may not have counted") — this is information the owner can act on by looking
at the pair, so it is allowed under his rule, and it is the surface's choice. The data is safe either
way (§ 13.1).

### 5.2 Finding, trusting and pairing with the server, silently

#### 5.2.1 Where the server is

The address is whatever the server bound, and the server tells us: `pairing.json` carries `host` and
`port` (confirmed on this box: `{"host":"127.0.0.1","port":18799,"certificateFingerprint":"sha256:…"}`
appears within a second of an unenrolled server starting). The link stores `baseUrl =
https://{host}:{port}/api/v1` at enrolment and uses it until it stops working.

#### 5.2.2 `ConnectAsync`

```
A. credential file present?
     yes → GET /ping with bearer, pinned (ReadTimeout)
             200, authenticated:true         → Connected(Enrolled:false)
             401 (any code)                  → the token is dead → go to C (enrol), then Connected(Enrolled:true)
             PinMismatch                     → go to C: if the offer channel answers with a DIFFERENT
                                                fingerprint, the server re-minted its certificate — adopt it
                                                (the data directory is the trust root, § 1). If it answers
                                                with the SAME fingerprint, or does not answer, → Failed(NotYourServer, Fatal)
             Unreachable                     → go to B
     no  → go to B
B. is a server alive at all?  OfferChannel.Request(ServerDataDirectory, OfferTimeout)
     offer arrives → the server is running (it consumed pair.request within a second);
                     its host/port/fingerprint are in the offer → go to C with that offer
     nothing       → StartServerIfNotRunning? no → Failed(ServerNotRunning)
                     yes → ServerProcess.Start(); wait up to ServerStartTimeout for EITHER
                           (i) GET /ping at the stored baseUrl to answer 200 ready:true (credential present, address unchanged)
                               → Connected(Enrolled:false, StartedServer:true)
                           (ii) pairing.json to appear with a fresh expiresAt (pair.request from step B is
                               still on disk; the server consumes it once ready; a fresh install also
                               auto-opens a window) → go to C
                           neither → Failed(ServerNotRunning, Detail names the exe path and the data directory)
C. Enrol (§ 5.2.3) with the offer in hand → Connected(Enrolled:true)
```

A `ready:false` from `/ping` (server starting or stopping) is treated as "not yet": poll at 250 ms
within the same timeout budget.

#### 5.2.3 Enrol

The order is normative (`SERVER_SPEC.md` § 10.1.1: pin before sending the code):

```
1. offer  = OfferChannel.Request(...) if not already in hand     (writes pair.request; waits for a
            pairing.json whose expiresAt is strictly later than the one already there — lifted from the Tray)
2. http   = Rm2Http.Create(pin: offer.certificateFingerprint, baseUrl: https://{offer.host}:{offer.port}/api/v1)
3. reply  = POST /pair { code: offer.code, deviceName }   — unauthenticated, NEVER retried (§ 13.3: the code is single-use)
            201 → credential = {baseUrl, fingerprint, token, deviceId, pairedAt}; save owner-only (0600 on Unix)
            401 invalid_pairing_code / 403 pairing_not_open / 429 → Failed(PairingFailed, Detail includes the code's
                                                                      attemptsRemaining or retryAfterSeconds when present)
4. if a previous credential existed with a deviceId: DELETE /pair/{oldDeviceId} with the NEW token, best effort,
   result ignored — so re-enrolment does not grow the server's device list. (A device MAY revoke itself, § 10.12;
   revoking another device with a valid token is allowed by the same route.)
```

An expired-window race (the server's five-minute window closed between reading the offer and posting)
is a `401 invalid_pairing_code`; the link asks for one fresh window and tries once more, because the
offer it read may have been the tail of an earlier window. Two failures → `PairingFailed`.

If `ServerDataDirectory` is not writable (the server runs as a different user, or the directory is not
where this program looks), `OfferChannel` reports it and `ConnectAsync` returns
`Failed(ServerNotRunning)` whose `Detail` names the directory it tried. That is the one setup
failure the owner has to fix by hand and the text says exactly what to check.

When `OfferChannel` gives up waiting, it deletes the `pair.request` it wrote, so a server started
later by hand does not open a pairing window nobody asked for.

#### 5.2.4 Starting the server

`ServerProcess.Start` runs `ServerExecutable` with `ServerArguments`, `UseShellExecute = false`,
`CreateNoWindow = true`, no redirected streams, and forgets the `Process` object. The link never stops,
kills or waits on the server: the Tray's single-instance mutex makes a duplicate start harmless (it
shows one message box and exits), and the server must outlive the client so the phone can use it. On
this Linux box the tests point `ServerExecutable` at `dotnet` with the console host's dll as the
argument, which is the same code path with a different command line. Whether the default path
`..\tray\RankMaster2.Tray.exe` is right relative to the published client is part A's packaging
decision; the option exists so A can set it.

#### 5.2.5 Loopback: not needed

`PC_CLIENT_PLAN.md` § 8 proposes an optional second Kestrel listener on `127.0.0.1` so the PC client's
address never changes. This plan does not need it, for three reasons:

1. The link connects to the address the server publishes, so it never has to guess. If the address
   changes and the server is reconfigured, `ConnectAsync` step A fails, step B reads the new address
   from a fresh offer, and step C re-enrols — silently, in about two seconds.
2. The failure the listener would remove — the PC has no LAN address, so the server cannot bind — is
   a *server* failure and is already visible where it happens: `RankMaster2.Tray.exe` shows "could not
   start" (its `Program.cs`). The link's `ServerNotRunning` text points at it.
3. The server is complete and audited; a transport change re-runs the conformance and security suites
   for a case the owner has not said he has.

If the owner answers "yes" to `PC_CLIENT_PLAN.md` § 13 question 4, the change is the server's, and the
only thing this plan would add is a second candidate address (`127.0.0.1`) tried in step A before
step B. It is not built now.

### 5.3 The session lifecycle

#### 5.3.1 Open

`OpenAsync(folder)`:

```
1. State is Disconnected → ConnectAsync; Failed → OpenResult.Failed(same failure)
2. DELETE /session      (ReadTimeout; 204 or 404 both fine; other → continue anyway: POST will tell us)
                        replacedOther = a 204 came back AND ping.session.folder (from Connect) or the
                        previous Snapshot.Folder differs from `folder`
3. POST /session {folder}   (OpenTimeout: Start() scans the folder and parses the JSON — 20k files on a
                            slow drive is seconds, on a share more)
     201 or 200          → adopt; State = InSession; Opened(snapshot, replacedOther)
     409 session_already_open → cannot happen after step 2 unless the phone opened a folder in the gap.
                            Do step 2 and 3 once more; a second 409 → Failed(Unexpected) rather than looping
     404 folder_not_found / 400 folder_not_a_directory / 403 folder_access_denied
     409 folder_not_rankable (details: stills, videos, rankable) / 409 library_json_unreadable
     423 folder_locked   → Failed(kind for the code, § 5.4)
     Unreachable         → one retry (POST /session is safe to retry blindly, § 13.3), then Failed(Unreachable)
```

Why always DELETE first, even when the server has the same folder open: `SPEC.md` § Screens says Resume
does not restore a pair, and `PC_CLIENT_PLAN.md` § 6.8 settled it. A resume with `200` would keep the
phone's cues and session votes on the PC's screen; a fresh `Start()` is what the owner expects when he
sits down.

Why no question is asked on `session_already_open`: `CLIENT_PLAN.md` § 3.6.3 — one user, and a folder
still open is always his own doing (a crash, or walking away from the phone). The one conflict worth a
message is `423 folder_locked`, which says something *other* than this server holds the folder.

#### 5.3.2 Close

`CloseAsync(ct)`: `DELETE /session` with the caller's token or `ReadTimeout`, whichever is shorter.
`204` and `404` are success. Anything else is written to `LastFailure` and to the log, and the method
returns normally: the server writes nothing on close (§ 10.4), so there is nothing to lose, and the next
`OpenAsync` closes whatever is left. `State` becomes `Connected`; `Snapshot` becomes null.

#### 5.3.3 The process dies without closing

Nothing to do, and the plan says so explicitly: the server keeps the session and the lock; the folder's
`rankmaster_db.json` is already complete (§ 13.1); the next `OpenAsync` from this PC does `DELETE` then
`POST`; the phone opening another folder gets `409 session_already_open` and closes ours itself
(`BrowseViewModel.attemptOpen`). The only cost is cosmetic — the tray shows a folder as open until
then. A crash *handler* that tries to `DELETE` on the way down is not built: it can only run when the
process is healthy enough not to need it.

#### 5.3.4 Refresh

`RefreshAsync`:

```
GET /session (ReadTimeout)
  200                → adopt; State = InSession; Applied(snapshot)          (a read, but the result type is shared)
  404 no_session     → the server restarted or the phone closed it → POST /session {Snapshot.Folder}
                       (no DELETE needed: there is nothing open) → adopt; Resynchronised(SessionReplaced)
  401                → Enrol once, GET again
  Unreachable        → ConnectAsync (may start the server) → GET again once → still Unreachable → Unknown
```

This is the only reconnect path, and it sends no action. The surface calls it from "Try again" and
when the window regains focus after a long absence, never from a failure of an action (that is
§ 5.1.4's job).

### 5.4 What the owner is told

His rule: **say something only when he is the one who can fix it.** The link applies it by construction:
a situation the owner cannot act on never becomes a `Failure`; it is absorbed and reported through the
result type's shape (`Resynchronised`, `NotSent`, `Opened(ReplacedOther: true)`), which the surface may
ignore.

Absorbed, silently:

| Situation | What the link does |
|---|---|
| `409 stale_pair_token` | adopts the attached snapshot |
| `409 session_already_open` | closes the other session, opens ours |
| `404 no_session` on close | success |
| `404 no_session` on refresh/action | reopens the same folder |
| `409 nothing_to_undo`, `409 no_current_pair` | adopts the attached snapshot |
| `503 session_busy` | waits 1 s, resends once; only a second 503 is reported |
| `401` with the data directory reachable | re-enrols, resends once |
| a lost response whose retry answers | `Applied` or `Resynchronised` — the owner never knows |

Reported, with the words the surface shows (English; `Detail` may append the server's `message`
where marked *+msg*, because § 4 lets it be shown and forbids it being parsed):

| `FailureKind` | When | `Title` / `Detail` | Fatal |
|---|---|---|---|
| `ServerNotRunning` | § 5.2.2 exhausted | "The Rank Master server is not running" / "It could not be started from `{exe}`. Start RankMaster2.Tray.exe yourself, then try again. If it is running, it keeps its files somewhere other than `{dataDir}`; set the server data directory in settings." | no |
| `NotYourServer` | pin mismatch | "That is not your server" / "Something at {host}:{port} answered with a certificate this PC has not paired with. Nothing was sent to it." | yes |
| `PairingFailed` | § 5.2.3 | "Pairing with the server failed" / "The server refused the pairing code ({code}). Try again; if it keeps failing, restart the server." | no |
| `PairingLost` | 401 and re-enrolment failed | "This PC is no longer paired with the server" / "The server did not accept this PC's credential and a new one could not be made. Restart the server, then try again." | yes |
| `FolderNotFound` | open | "That folder does not exist" / "{folder}" | no |
| `FolderNotADirectory` | open | "That is a file, not a folder" / "{folder}" | no |
| `FolderAccessDenied` | open | "That folder cannot be read" / "Windows refused access to {folder}." | no |
| `FolderNotRankable` | open | "Nothing to rank here" / "{folder} has {stills} pictures and {videos} videos; at least two of one kind are needed." | no |
| `LibraryJsonUnreadable` | open | "The ranking file is damaged" / "rankmaster_db.json in {folder} does not parse. It was not touched. Restore it from a backup or move it aside to start over." | no |
| `FolderLocked` | open, 423 | "Another program is using this folder" / "The old Rank Master, or a second server, holds {folder}. Close it and try again." | no |
| `SaveFailed` | action, 500 | "The server could not write the ranking file" / "Nothing was lost — the ratings are still in the server's memory. Check the drive is connected and the folder is not read-only, then press the key again." *+msg* | no |
| `MoveFailed` | discard/special/undo, 500 | "The file could not be moved" / "It is probably open in another program. Nothing changed; close that program and press the key again." *+msg* | no |
| `ServerBusy` | second 503 | "The server is busy" / "It is finishing something in this folder — probably from the phone. Press the key again in a moment." | no |
| `ServerShuttingDown` | 503 server_shutting_down | "The server is shutting down" / "Wait for it to finish, then try again; it will be started if needed." | no |
| `Unreachable` | `Unknown` | "The server did not answer" / "Everything ranked so far is already on disk. This last one may or may not have counted; press the key again and the server will sort it out — it never counts a vote twice." | no |
| `Unexpected` | 500 internal_error, 400s from a bug, `client_malformed_*` | "The server answered in a way this program did not expect" / "Code {code}, request {requestId}. The details are in the log." | no |

The `Unreachable` text is deliberately honest where the phone's is not ("this last one did not count"
is not something the client can know); the promise it makes — the server never counts a vote twice — is
the one the server actually keeps.

`Detail` never contains a token, a stack trace, or an exception type name. `Code` and `RequestId` are
for the log line.

### 5.5 Transport

`Rm2Http` is the only class that owns an `HttpClient`, and there is one per link:

- `SocketsHttpHandler`: `ConnectTimeout = options.ConnectTimeout`, `UseProxy = false`,
  `AllowAutoRedirect = false`, `AutomaticDecompression = None`, `PooledConnectionLifetime = 10 min`,
  `SslOptions.RemoteCertificateValidationCallback` = the pin (lifted from `Rm2Api`, minus TOFU):
  compute `"sha256:" + hex(SHA256(certificate.RawData))`, compare to the pinned value with
  `CryptographicOperations.FixedTimeEquals`, ignore `SslPolicyErrors` entirely (the certificate names an
  IP address the server was configured with; the fingerprint identifies the machine more tightly than
  any name could), throw `PinMismatchException(expected, actual)` on mismatch so it is recognisable
  in the failure chain.
- `HttpClient.Timeout = Timeout.InfiniteTimeSpan`; every call is bounded by a linked
  `CancellationTokenSource` for the per-call timeout, so a timeout is distinguishable from the caller's
  cancellation (`client_timeout` vs `client_cancelled` in the log and in `Unknown`).
- `DefaultRequestVersion = HttpVersion.Version11`, `DefaultVersionPolicy = RequestVersionExact`.
- `Authorization: Bearer {token}` on every request except `POST /pair`; `Accept: application/json`;
  bodies `application/json; charset=utf-8`. The bearer is set per request from the credential, so
  re-enrolment mid-session takes effect on the very next send without rebuilding the client.
- `Send(FrozenRequest, timeout)` → `Reply`: `Ok(status, bodyBytes, requestId)` for 2xx;
  `Refused(status, code, message, requestId, details, session)` for anything else with a parsable § 4
  envelope; `Refused(status, "client_malformed_error", …)` when there is a status but no envelope;
  `Unreachable(kind: Refused | Timeout | Cancelled | PinMismatch | Other, detail)` when there is no
  status. A 2xx whose body does not parse as the expected type is
  `Refused(status, "client_malformed_response", …)` — never an exception out of the link, never a
  half-built snapshot. Synthetic codes carry the `client_` prefix so they can never collide with a
  § 5 code added later (`Rm2SyntheticCodes` in Kotlin).
- A body that arrives after the status but fails mid-read is `Unreachable`, not `Refused`: nothing was
  learned, and the action protocol must be allowed to retry it with the same token.
- The log line: `{method} {path} → {status|unreachable} {code?} {ms}ms {requestId?}`. No body, no
  token, no folder path.

---

## 6. Tests

Development is on Linux, the server runs here, and this part has no Windows-only code path except the
default paths in `Paths` and the tray executable name. So the whole of `Link/` is proven here, against
the real server, and the plan's acceptance gate is the test run.

### 6.1 The test project

`pc/tests/RankMaster2.Pc.Link.Tests/RankMaster2.Pc.Link.Tests.csproj` — `net8.0`, xunit (the versions
`tests/RankMaster2.Server.Tests` uses), and:

```xml
<ItemGroup>
  <Compile Include="../../src/RankMaster2.Pc/Link/**/*.cs" LinkBase="Link" />
</ItemGroup>
```

It compiles the link's sources directly. Part A owns `RankMaster2.Pc.csproj` and may not exist yet
when this part is built; this makes B buildable and testable alone, and it is also the proof that
`Link/` depends on nothing but the BCL — the moment someone adds a `using Avalonia` to `Link/`, this
project stops compiling. Part A adds the same folder to the client project without a project reference
in either direction.

### 6.2 The harness

**`RealServer`** (collection fixture, one per test run, tests in the collection run serially — the
server holds one session):

- Locates the repository root (walk up from the test assembly until `RankMaster2.sln` is found —
  `tests/RankMaster2.Server.Tests/Harness/Repo.cs` does this) and the built
  `src/RankMaster2.Server/bin/{Configuration}/net8.0/RankMaster2.Server.dll`; if absent, runs
  `dotnet build src/RankMaster2.Server` once (the Compatibility audit's `CrashHarness` builds a child
  the same way).
- Picks a free port (bind a `TcpListener` on 0, read the port, close it; the server cannot be given
  port 0 — its offer would publish `0`).
- Starts `dotnet RankMaster2.Server.dll` with environment `RankMaster2__ListenAddress=127.0.0.1`,
  `RankMaster2__Port={port}`, `RM2_DATA_DIR={temp}/rm2-link-tests/{guid}`, `Logging__LogLevel__Default=Warning`,
  stdout/stderr redirected to a file the test output names on failure.
- Waits for `{data}/pairing.json` (the server auto-opens a window when no device is enrolled — seen
  on this box within one second of start) and reads `host`, `port`, `certificateFingerprint`, `code`.
- Exposes `DataDirectory`, `BaseUrl`, `Fingerprint`, `Kill()` (SIGKILL, `entireProcessTree: true`),
  `Restart()` (same port, same data directory, so the certificate and tokens survive, as they would on
  the owner's PC), and `PairSecondDevice()` — writes `pair.request`, reads the fresh offer, posts the
  code with a plain `HttpClient` pinned to `Fingerprint`, returns a token. The second device plays
  "the phone" in lifecycle tests.
- `Disposes` by killing the process and deleting the data directory.

**`LinkUnderTest`**: builds a `SessionLink` through the internal constructor with `LinkOptions`
pointing `CredentialDirectory` at a fresh temp directory, `ServerDataDirectory` at
`RealServer.DataDirectory`, `ServerExecutable = "dotnet"`, `ServerArguments = [dllPath]`, `Log` into
the xunit output, and `wrap = h => new TapHandler(h)`.

**`TapHandler : DelegatingHandler`**: records every request as `Sent(method, path, bodyBytes,
bearerPresent, at)` in order, and runs a per-request script the test sets up in advance, matched by
path and occurrence:

| Script | Behaviour |
|---|---|
| `Forward` (default) | pass through to the real server |
| `DropBeforeForward` | do not send; throw `HttpRequestException("injected: connection reset")` |
| `ForwardThenDropResponse` | send to the real server, await the response, dispose it, throw `TaskCanceledException` (what a timeout looks like) — **the request landed; the client did not learn it** |
| `ForwardThenKillServer` | send, await the response, `RealServer.Kill()`, then throw as above |
| `Fabricate(status, json)` | do not send; return a synthetic response (used for 503 `session_busy` and 401 with a body the real server would produce, so those branches can be exercised without timing the phone) |
| `ThrowPinMismatch` | do not send; throw `HttpRequestException` with a `PinMismatchException` inner — what `PinnedHandler` produces when the peer's certificate is wrong (N11) |
| `Delay(ms)` | sleep, then forward (for cancellation tests) |

Every fault is injected at the one seam the link owns — its `HttpMessageHandler` — so the server on the
other side is always the real one, and every assertion about "what the server did" is made by reading
the server's own state: `GET /session` through a separate pinned `HttpClient` with the second device's
token, and `rankmaster_db.json` on disk.

**`ScratchFolder`**: a temp folder of `n` real PNGs (lifted from `rm2ctl/ScratchLibrary.cs`), with
`Db()` returning the parsed `rankmaster_db.json` — `matches`, `impressions` per file — and
`Exists(name)`, `InDiscarded(name)`, `InSpecial(name)`.

### 6.3 What must work (positive)

Wire:
- W1 `Snapshot` parses the live `POST /session` body; every § 9.1 field present; `PairSeq == 0`,
  `LastAction == null`, `LastSavedAt == null`, `Cues` empty, `WarmPairs.Count <= PrefetchPairs`.
- W2 A snapshot with an unknown extra field (fabricated by `TapHandler` wrapping the real body) parses.
- W3 The § 4 envelope parses field by field: a fabricated 409 whose `error.session` is garbage still
  yields `code` and `message`; a 404 with an empty body yields `client_malformed_error`.
- W4 `Ping` parses public and authenticated bodies, `Session` null and non-null.
- W5 Every request body type serialises to exactly the field set § 10 lists (compared to `openapi.yaml`
  by name, so a typo in `JsonPropertyName` fails here and not on the owner's PC).

Connect and enrol:
- C1 Cold: no credential → `ConnectAsync` writes `pair.request`, reads the offer, pins, pairs →
  `Connected(Enrolled: true)`; `link.json` exists with mode `0600`; the token works (`/ping`
  `authenticated: true`).
- C2 Warm: credential present → `Connected(Enrolled: false)`; exactly one request sent (`GET /ping`).
- C3 Address changed: edit `link.json` to a wrong port → `ConnectAsync` → offer channel → re-enrol →
  `Connected(Enrolled: true)`; `link.json` now has the right port; the *old* `deviceId` was revoked
  (a `/ping` with the old token is 401).
- C4 Server not running, start allowed: `RealServer.Kill()`, options pointing `ServerExecutable` at
  `dotnet` + the dll → `ConnectAsync` starts it → `Connected(StartedServer: true)` within
  `ServerStartTimeout`. (`RealServer` adopts the started process so it can clean up.)
- C5 Server not running, start refused: `StartServerIfNotRunning = false` → `Failed(ServerNotRunning)`
  whose `Detail` names the data directory; no process started.
- C6 Wrong server: a stub TLS listener with its own certificate on another port, `link.json` pointed
  at it → `Failed(NotYourServer, Fatal: true)`; the stub received a TLS handshake and **zero HTTP
  bytes with a bearer** (assert the stub saw no request line).
- C7 Re-minted certificate: `RealServer.Restart()` with `certificate.pfx` deleted first → stored pin
  fails → the offer channel publishes the new fingerprint → `Connected(Enrolled: true)`.
- C8 Pairing code spent: `Fabricate(401 invalid_pairing_code, attemptsRemaining: 4)` on the first
  `POST /pair`, forward the second → `Connected` (one fresh window asked for); fabricate both →
  `Failed(PairingFailed)`; exactly two `POST /pair` sent, never a third.

Lifecycle:
- L1 Open → `DELETE` then `POST`, in that order, `Opened(ReplacedOther: false)`, `State == InSession`.
- L2 The phone (second device) has folder X open; `OpenAsync(Y)` → `Opened(ReplacedOther: true)`, no
  `Failure`; the phone's next `GET /session` shows Y.
- L3 Each of `folder_not_found`, `folder_not_a_directory`, `folder_access_denied` (a folder with mode
  `000`), `folder_not_rankable` (one PNG), `library_json_unreadable` (`rankmaster_db.json` containing
  `{`) → the matching `FailureKind`; the JSON file is byte-identical afterwards.
- L4 `folder_locked`: the test holds `{folder}/.rankmaster.lock` with `FileShare.None` →
  `Failed(FolderLocked)`; released → `Opened`.
- L5 `CloseAsync` → 204; again → 404 → still returns normally, `LastFailure == null`.
- L6 Open on the same folder twice in a row (simulating a client crash and restart): second open is
  `DELETE` + `POST 201` (not a `200` resume): `SessionVotes == 0` after votes had been cast.
- L7 Kill the server with a session open; `RefreshAsync` → `ConnectAsync` restarts it → `no_session`
  → reopen → `Resynchronised(SessionReplaced)`; `Snapshot.SessionId` changed.
- L8 Esc path: `CloseAsync` with a 500 ms token against a `Delay(2000)` script returns within ~500 ms,
  does not throw.

Actions:
- A1 Vote left → `Applied`; `PairSeq` 0→1; on disk both ids `matches == 1`; `LastAction.Type == vote`,
  `.ClientRequestId` starts with `pc-` and is ≤ 64 chars.
- A2 Skip → `Applied`; impressions +1, matches unchanged.
- A3 Discard left → `Applied`; the file is in `discarded/`; `LastAction.Id` is the id.
- A4 Special right → file in `special 1/`.
- A5 Undo after vote → `Applied`; `LastAction.UndoneType == "vote"`; `SessionVotes` back by one; the
  pair on screen is the pair the vote consumed (ids equal, order may differ).
- A6 Undo after discard → `Applied`; file back; `RestoredId` non-null.
- A7 Undo when `UndoAvailable == false` → `NotSent(UndoNotAvailable)`, zero requests.
- A8 `VoteAsync(onPairSeq: Snapshot.PairSeq - 1)` → `NotSent(PairMoved)`, zero requests.
- A9 Exhausted: two-file folder, discard one → `IsExhausted`; `VoteAsync` → `NotSent(Exhausted)`.
- A10 Save → `Applied`; `PairToken`, `PairSeq` unchanged; `LastSavedAt` set. Again with
  `ForwardThenDropResponse` → `Applied` (idempotent, § 10.5); two identical `{}` bodies.
- A11 Two-file folder: `WarmPairs` empty and everything still works (`SERVER_SPEC.md` § 16 gap 7).
- A12 Missing file: delete a file from disk under the session → next snapshot has `IsMissing`; discard
  that side → `Applied`, `LastAction.Type == drop_missing`, `UndoAvailable` unchanged.
- A13 Overlap: start `VoteAsync` against `Delay(500)`, call `SkipAsync` before it returns →
  `InvalidOperationException`; exactly one request in the tap.
- A14 Cancellation: `VoteAsync` with a token cancelled during `Delay(500)` → `Unknown` with
  `Code == "client_cancelled"`; exactly one request; `Snapshot.PairToken` unchanged.
- A15 `save_failed` surface: make `rankmaster_db.json` read-only and the folder non-writable
  (Linux: `chmod 555`) → vote → `Refused(SaveFailed, snapshot)`; the link did a `GET /session` after
  the 500 (tap shows it); token unchanged; restore permissions → vote with the same `onPairSeq` →
  `Applied`.
- A16 `session_busy` absorbed: `Fabricate(503 session_busy, Retry-After: 1)` then `Forward` →
  `Applied`; two bodies, byte-identical; ~1 s elapsed.
- A17 `server_shutting_down`: `Fabricate(503 server_shutting_down)` → `Refused(ServerShuttingDown)`;
  no resend.

### 6.4 What must never happen (negative)

Each test below ends with the same three assertions, written once as `AssertNoDoubleVote(tap, folder,
expectedVotes)`: (i) the number of *distinct* `clientRequestId`s sent to any action route equals the
number of actions the test asked for; (ii) every request that carried a `pairToken` carried the token
the link held when the action was *asked for* — never a token learned later in the same call; (iii) the
sum of `matches` on disk equals `2 × expectedVotes`.

- **N1 Lost response, landed.** Vote with `ForwardThenDropResponse` on the first `/session/vote` →
  `Resynchronised(LandedEarlier)`; the tap shows exactly two `/session/vote` requests with
  **byte-identical bodies**; server `SessionVotes == 1`; `PairSeq == 1`.
- **N2 Lost request, not landed.** `DropBeforeForward` then `Forward` → `Applied`; two identical bodies;
  one vote.
- **N3 Both attempts lost, then the owner presses again.** `DropBeforeForward` ×2 → `Unknown`;
  `State == InSessionUnreachable`; `Snapshot.PairToken == T0`; the tap shows two identical bodies and
  **nothing else** for the next 2 s (no background retry). Then `VoteAsync(Left, onPairSeq: 0)` with
  `Forward` → `Applied`; that third body carries **`T0`** and a *different* `clientRequestId`; one vote
  on disk.
- **N4 Landed, both replies lost, then refresh, then the owner votes on the new pair.**
  `ForwardThenDropResponse` then `DropBeforeForward` → `Unknown`, token still `T0`. `RefreshAsync` →
  `Applied(snapshot)` with `PairSeq == 1` and `LastAction.ClientRequestId == R0` (the surface can see
  it landed). `VoteAsync(Right, onPairSeq: 1)` → `Applied`, `PairSeq == 2`. Two votes asked, two on
  disk — **and no request after the refresh carried `T0` or `R0`.**
- **N5 The forbidden call cannot be written.** Reflection over every public type in
  `RankMaster2.Pc.Link` (not `.Wire`): no method or constructor has a parameter whose name contains
  `token` or `requestId`, and no public method returns `string` — the seam has no channel for a
  token to come back through. (Wire types are exempt and are records; the test asserts they have no
  methods beyond the generated ones.)
- **N6 Kill mid-vote, restart, reopen.** `ForwardThenKillServer` on `/session/vote` → retry gets
  connection refused → `Unknown`. `RealServer.Restart()`. `RefreshAsync` → `Resynchronised(SessionReplaced)`,
  new `SessionId`, `PairSeq == 0`, `LastAction == null`. Then five more votes → `Applied` each. Disk:
  the killed vote landed (the server answered before it was killed), so total `matches == 2 × 6`; and
  no request after the restart carried `T0` or `R0`.
- **N7 Discard replayed.** `ForwardThenDropResponse` on `/session/discard` → `Resynchronised(LandedEarlier)`;
  exactly one file in `discarded/`; the folder has `n − 1` files; two identical bodies sent.
- **N8 Undo replayed.** Vote, then undo with `ForwardThenDropResponse` → the retry gets
  `409 nothing_to_undo` with a snapshot → `Resynchronised(UndoAlreadyDone)`; `SessionVotes == 0`, not −1;
  `PairSeq == 2`.
- **N9 Token revoked mid-session.** Revoke the link's device through the second device's token
  (`DELETE /pair/{deviceId}`) → `VoteAsync` → 401 → re-enrol → resend → `Applied`; the tap shows two
  `/session/vote` bodies, byte-identical, the second with a different bearer; `link.json` has the new
  token; one vote on disk.
- **N10 The phone moved the pair.** Second device votes on the current pair; the link's `VoteAsync`
  (still on `PairSeq 0`) → `Resynchronised(PairMovedElsewhere)`; one vote on disk (the phone's); the
  link's snapshot now shows `PairSeq == 1`. Then `VoteAsync(onPairSeq: 0)` → `NotSent(PairMoved)`, zero
  requests.
- **N11 A pin mismatch is never retried.** With a session open, script `ThrowPinMismatch` on the next
  `/session/vote`: the tap throws `HttpRequestException` whose `InnerException` is a
  `PinMismatchException` — exactly what `PinnedHandler` throws when the peer's certificate is wrong.
  `VoteAsync` → `Unknown(Failure.Kind == NotYourServer, Fatal)`; the tap recorded exactly **one** attempt;
  `State == Disconnected`. (C6 covers the real handshake; this covers the protocol's refusal to retry.)
- **N12 `POST /pair` is never retried.** Cold link, `ForwardThenDropResponse` on `POST /pair` →
  `ConnectAsync` returns `Failed(PairingFailed)`; the tap recorded exactly one `POST /pair`. The next
  `ConnectAsync` writes a new `pair.request`, reads a *fresh* offer (a later `expiresAt`) and succeeds;
  the first, lost token is simply never used.
- **N13 No background activity.** Open a session, then wait 15 s doing nothing → the tap recorded zero
  requests.
- **N14 `pairSeq` is never on the wire.** Every recorded action body, parsed as JSON, has exactly the
  key set § 10 lists for that route and no `pairSeq` key.

### 6.5 Running them

`cd pc/tests/RankMaster2.Pc.Link.Tests && dotnet test` — no environment needed; the harness builds the
server if it must. One run is expected to take under two minutes; the kill/restart tests dominate.
The suite must also pass when the server is already built in `Release`.

---

## 7. Phases

Each phase ends with its tests green against the real server. Nothing from a later phase starts until
the earlier one is green — this part is small enough that overlap only hides which step broke.

**Phase 0 — wire types and the test scaffold** (`Wire/`, the test csproj compiling `Link/**`,
`RealServer`, `ScratchFolder`). Exit: W1–W5 pass; `RealServer` starts, pairs a second device, kills,
restarts. Nothing in `Link/` beyond `Wire/` yet.

**Phase 1 — transport** (`Transport/`: `PinnedHandler`, `Rm2Http`, `FrozenRequest`, `Reply`). Exit: a
test sends every § 10 request once through `Rm2Http` and gets the expected `Reply` shape; C6 (wrong
server) passes at the transport level; the malformed-body cases return synthetic codes, never throw.

**Phase 2 — enrolment and the server process** (`Enrolment/`, `ConnectAsync`). Exit: C1–C8.

**Phase 3 — the session and the action protocol** (`SessionLink`: open, close, refresh, the § 5.1.1
sequence, the § 5.4 failure catalogue). Exit: L1–L8, A1–A17. `ISessionLink.cs` is now frozen and
announced to parts A and E.

**Phase 4 — the negative suite** (`TapHandler` scripts, N1–N14). Exit: all pass, and one deliberate
sabotage is tried and caught: change step 5 of § 5.1.1 to rebuild the body from `Snapshot.PairToken`
after a stale reply, run the suite, watch N1/N4 fail, revert. The commit message records that the
sabotage was caught.

**Phase 5 — adversarial read-through.** One agent, fresh, given `SERVER_SPEC.md` § 8 and § 13.3 and
`Link/`, asked to find a path to a double vote or a token leaving the link. Findings become tests
before fixes. Same shape as the server's phase 4 and the phone's, for the same reason.

---

## 8. Acceptance gate

Part B is done when, on this Linux box, against the real server started by the harness:

1. `dotnet test` in `pc/tests/RankMaster2.Pc.Link.Tests` is green, and the run includes every test
   named in § 6.3 and § 6.4 — a test may be renamed, not dropped.
2. Every row of `SERVER_SPEC.md` § 13.3's table has a test that exercises the retry it describes
   (GET: L7/refresh; `POST /session`: § 5.3.1 retry in L7; save: A10 with a drop; DELETE: L5; vote/skip:
   N1–N4; discard/special: N7; undo: N8; `POST /pair`: N12).
3. The § 5.1.1 sequence is one function; `FrozenRequest` is the only type that turns a request record
   into bytes, and it does so once per instance. A `grep` for `JsonSerializer.Serialize` and
   `SerializeToUtf8Bytes` in `Link/` finds them in `FrozenRequest.cs`, `Enroller.cs` (`POST /pair`) and
   `Credential.cs` (`link.json`) and nowhere else.
4. `Link/` compiles with no `PackageReference` and no `ProjectReference` (the test csproj proves it).
5. N5 passes: no public parameter or return in `RankMaster2.Pc.Link` carries a token or a request id.
6. The phase 4 sabotage was performed and caught.
7. A `Failure` exists for every situation in the § 5.4 "Reported" table and for none in the "Absorbed"
   table; each has a test that produces it against the real server (or, for `ServerShuttingDown`, a
   fabricated 503 — the server's own window for it is milliseconds long).
8. `ISessionLink.cs` matches § 4.1 of this document, or this document has been changed first.
9. What could not be verified here is listed in the hand-off, not implied: the default tray path and
   `%LOCALAPPDATA%` resolution; that `Process.Start` of the Tray exe with `CreateNoWindow` behaves on
   Windows 11; that a Windows system proxy is really bypassed with `UseProxy = false`; owner-only file
   mode is not applied on Windows (the profile directory's ACL is relied on).

---

## 9. What the contract could say better

Observations, not requests. Nothing here blocks this part and nothing changes the server.

1. **`error.session` on `500 save_failed` / `move_failed`.** § 4 requires the snapshot on every 409 on
   `/session/*` and says "present iff a session is open and the caller is authenticated" otherwise —
   but the server does not attach it to 500s, and § 8.3 says a `save_failed` after a moved file bumps
   `pairSeq`. The client therefore has to `GET /session` after every 5xx to learn its own token is
   stale. One sentence — "a 500 on `/session/*` SHOULD carry `error.session` when the lock is held" —
   would save the round trip. The link does the read; it costs ~5 ms on loopback.
2. **§ 13.4 gap 1 is the only unknown this client ever reports to the owner.** A server-side journal of
   the last `clientRequestId` in the data directory would let `RefreshAsync` after a restart answer
   "landed" or "not landed" instead of `OutcomeUnknownAfterRestart`. Already in § 16; noted because
   the PC is the client most likely to see a restart (the tray's Exit is one click away).
3. **`Port = 0` is not usable for tests** — the offer publishes the configured port, not the bound
   one. The harness picks a free port itself. A harness inconvenience only.
4. **`details.holder` on `423`** is documented as always null (§ 5.3.1) while `FolderLock.TryAcquire`
   fills it best-effort. The link ignores it either way.
5. **`pairing.json`'s `host` is the listen address as configured.** If the owner ever configures the
   server with an address the PC cannot route to itself (it cannot: binding requires the address to
   be local), the offer would mislead the PC. Not a real case; recorded so nobody adds a fallback for
   it.

---

## 10. Owner decisions that touch this part

From `PC_CLIENT_PLAN.md` § 13; this plan's defaults are stated so nothing waits on an answer:

- **Q4, loopback listener:** not needed for this plan (§ 5.2.5). If "yes", the server changes; this
  part gains one candidate address in step A.
- **Q6, start the server from the client:** default **yes** (`StartServerIfNotRunning = true`). If
  "no", one option flips and `ServerNotRunning`'s `Detail` drops the sentence about starting.
- **Settled 2026-09-16, `Ctrl+Z` = server undo:** this part exposes it as `UndoAsync`; the surface reads
  `Snapshot.LastAction.UndoneType` from the `Applied` result to say what was taken back, as
  `PC_CLIENT_PARTS.md` requires. The link guarantees that field is typed and preserved (test A5).
