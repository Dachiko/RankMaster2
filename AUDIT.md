# Rank Master 3 — independent audit

Date: 2026-09-17. Tree audited: this worktree, unmodified except for `AUDIT.md` and the throwaway tests
listed in Appendix A. No production file was changed. No git command was run.

**How to read this.** Findings are grouped for triage, not filtered: **would hurt** (loses or corrupts
judgement, applies something he did not ask for, or leaves him unable to continue), **would annoy**,
**would confuse the next person**, **cosmetic**. Within a group they are roughly in order of cost.
Every item says which kind of claim it is:

- **[proven]** — a throwaway test I (or a fork of me) wrote fails/passes exactly as the item says, or a
  real server answered as quoted.
- **[read]** — from reading the code; file and line cited; not executed.
- **[guess]** — an inference or a Windows/device-only claim that nothing here could run. Each says what
  would settle it.

Baseline before any of my tests: server-side `dotnet test RankMaster2.Server.slnf` → **596 passed, 0
failed**; PC `dotnet test pc/RankMaster2.Pc.sln` → **417 passed, 2 failed** (pre-existing — see T3);
phone `./gradlew :app:testDebugUnitTest` → **333 passed**. `rm2ctl cycle` against a real TLS server:
**110/110 checks pass**.

The work was split five ways: I read the ranking engine, catalog, rename engine, session registry, lock,
tray and docs myself; four forks of me took security, the phone, the PC client, and the server media layer
plus contract drift. Their evidence is folded in below with the same labels; where two of us reached the
same conclusion independently it is said once and marked so.

---

## The short version

Six things I would want fixed before the first real use, in order:

1. **H1 — the second rename of a folder can permute the ratings if it is interrupted.** The journal
   design that exists precisely to make rename crash-safe misidentifies files when the folder's names are
   already `000001.jpg …`. Cancel is a button. Proven by a test; the spec's own recovery table calls one of
   the failing cases "harmless".
2. **H2 — after a rename whose final save fails, every vote answers 200 and writes nothing.** Proven.
3. **H5 / H6 — the phone hides the one error he can fix, and undo can restore a file he discarded on
   purpose.** Both proven.
4. **H8 / H9 — the Windows client will stop showing pictures after ~124 votes on a small folder, and can
   wedge at the start screen on first launch.** Both proven by test; neither has ever run on Windows.
5. **H3 / H4 — two ways the server wedges itself and reports a false cause** ("a rename is already
   running", "in use by another Rank Master process"). Proven.
6. **H13 — nothing stops Rank Master 2 and the server writing the same `rankmaster_db.json`.** Known,
   still true, and the README says otherwise.

Where I went looking and found it sound is in § "What holds up" — the never-vote-twice protocol on both
clients and on the server, the atomic save, the media path containment, the auth gate, and the still
renderer's memory discipline are the best-built parts of the product and I could not break them.

---

## Would hurt

### Server and engine

**H1. A second rename of a folder, interrupted at any point in phase 1, assigns ratings to the wrong
files. [proven]**
`src/RankMaster2.Catalog/RenameEngine.cs` `Reunite` (≈ line 290) identifies each on-disk file by looking
its name up as *new* first, then *temp*, then *old*. After a folder has been renamed once every file is
already `000001.jpg …`, so on the second rename most names are simultaneously the `old` of one plan entry
and the `new` of another. A file that has **not moved yet** is then matched as if it had. `FinalizeBestEffort`
(≈ line 245) has the same blind spot: `if (File.Exists(newPath)) continue;` treats an unmoved neighbour as a
finished move.
Sequence: rank → rename → rank more (order changes) → rename again → the owner presses **cancel** during
phase 1, or the process dies there. `ReuniteInPlace`/`RecoverIfPresent` write a database in which the
ratings are rotated between files. Nothing reports it. Even the row the spec table (`SERVER_SPEC.md`
§ 10.16, "after the journal fsync, before any move … harmless") calls harmless permutes every rating on a
renamed folder.
Evidence: `tests/RankMaster2.Catalog.Tests/Audit_RenameSecondRunTests.cs` — three of five tests fail
(`Cancel_during_phase1…`, `Crash_during_phase1…`, `Crash_after_journal_before_any_move…`); the happy path
passes. The existing `RenameEngineTests` only ever use fresh names (`alpha/bravo/charlie`), which is why
they pass.
Likelihood: needs an interruption inside phase 1; a small folder is over before a finger reaches cancel,
a large one on a USB drive is not. Cost: silently wrong ratings, permanent. Confidence: high.

**H2. After a rename whose commit save throws, the session keeps the old names and every subsequent
vote returns 200 while writing nothing. [proven — media fork]**
`SERVER_SPEC.md` § 7.2 promises "rename cancelled **or failed** → resynced from disk". The cancel path
does (`SessionRegistry.CancelInPlaceAsync` → `Session.Start()`, `SessionRegistry.cs:947`); both failure
branches (`RunRenameAsync`, `:898-911` and `:928-938`) only clear the flag. The ratings on disk are correct
(recovery worked), but the in-memory records still carry `charlie.jpg`, `delta.jpg` …; `JsonCatalog.Save`
merges by filename against disk, finds none of them, and writes the on-disk rows back unchanged.
Observed: files `000001.jpg … 000006.jpg`, snapshot pair `charlie.jpg | delta.jpg`, vote → **200**,
`rankmaster_db.json` byte-identical before and after. `sessionVotes`, cues and `pairSeq` all advance on the
phone. Test: `tests/RankMaster2.Server.Tests/Audit_RenameFailureLosesVotes.cs`. Reachable by anything
that makes one save throw at that moment (disk full, an Explorer/antivirus handle on the file on Windows, a
USB pull). Confidence: high.

**H3. A journal write that throws at rename start wedges the session behind a false message. [proven]**
`SessionRegistry.StartRenameAsync` sets `open.RenameInProgress = true` and publishes `_rename` *before*
`_journalWriter.Write(...)` (`SessionRegistry.cs:≈700-720`), inside `try { … } finally { release }` with
no catch. If the write throws (disk full, folder read-only), the exception leaves as `500 internal_error`,
and the flag stays set: every vote, skip, discard, special and undo now answers `409 rename_in_progress`
"A rename is already running" until the session is closed and reopened. Nothing is running.
Test: `tests/RankMaster2.Server.Tests/Audit_RegistryWedgeTests.cs`
`A_journal_write_that_throws_leaves_the_session_refusing_every_vote` (fails as described).
Related **[read/guess]**: `FinishAsync` (`:963`) gives up after 5 s if the gate is busy and then never
applies — `RenameInProgress` stays true and the operation stays `saving` forever. I could not construct a
5-second gate hold; a `Start()` scan of a huge network folder is the only candidate. Confidence: high on
the proven part, low on the guess.

**H4. A corrupt or unreadable rename journal on open leaks the folder lock. [proven]**
`OpenAsync` takes `FolderLock` (`:≈228`) and then calls `RenameEngine.RecoverIfPresent` (`:≈255`)
outside the `try` that guards `Start()`. `ReadJournal` deserialises with no catch, so a truncated or
hand-edited `.rankmaster-rename.json` throws `JsonException` → `500 internal_error`, and the lock is
never disposed. Every later `POST /session` on that folder answers `423 folder_locked` "That folder is in
use by another Rank Master process" until the server restarts. A journal with `"plan": null` would NRE
the same way. Test: `Audit_RegistryWedgeTests.A_corrupt_journal_on_open_leaks_the_folder_lock` (fails as
described). Likelihood: low (the journal is written atomically); consequence: the owner is told a lie and
has to restart the server. Confidence: high.

**H5. Undo restores a file he discarded two actions earlier. [proven, twice independently]**
`RankingSession.Drop` clears the engine's vote snapshot (`RankingSession.cs:129`) but nothing clears
`LibraryActions.LastMove`. So: discard A → vote → a file vanishes externally and he discards that side
(`drop_missing`) → `undoAvailable: true` → undo → **A comes back from `discarded/`** and the pair is
replaced. `SERVER_SPEC.md` § 10.10 lists `drop_missing` as not cancellable and says undo is one level.
Tests: `tests/RankMaster2.Server.Tests/Audit_UndoReachTests.cs` (mine) and
`Audit_UndoReachesBack.cs` (media fork, two cases). Bounded to one wrong restore; it is still a file move
and a pair change nobody asked for. Confidence: high.

**H13. Rank Master 2 and the server can still both write one `rankmaster_db.json`. [read]**
`src/RankMaster2.App` contains no reference to `.rankmaster.lock` (grep), and `Directory.Build.props`
describes it as "the WPF desktop app the owner runs today". `SERVER_SPEC.md` § 16.6 records the gap;
`README.md` "What is in 3.0.0" says the server is "the only writer, so two programs can no longer corrupt
`rankmaster_db.json` between them" — true only once RM2 is retired. Until then the transition period is
exactly the two-writer case. Confidence: high that the gap exists; the cost depends on his habits.

**H14. `DELETE /session` is accepted while a rename is running. [read, chain is a guess]**
`CloseAsync` (`SessionRegistry.cs:364-373`) does not check `RenameInProgress`; it releases the folder lock
while the rename task keeps moving files. The phone does exactly this without asking (H7). A `POST
/session` on the same folder then runs journal recovery **concurrently** with the live rename, and
`Start()` scans a folder mid-move — the new session can hold `__rm2_000003.jpg` as a media id. From
`JsonCatalog.Save`'s merge rule I believe the on-disk ratings survive (the commit writes the reunited rows;
the new session's stale names are simply not on disk) and only the votes cast in between are lost, as in
H2 — but I did not build the race, and two writers on `rankmaster_db.json.tmp` at the same instant is a
sharing-violation path I did not exercise. Settle it with a blocking `ICatalog` in `RenameGateServer`.

### The phone

**H6. The phone hides every refusal that carries a snapshot — including the ones he can fix. [proven —
Android fork]**
`RankViewModel.finish()` (`android/…/ui/rank/RankViewModel.kt:227-236`) treats *any* `Refused` with
`error.session` as a silent resync. The server attaches a snapshot to every `500 save_failed`, `500
move_failed`, `409 nothing_to_undo`, `409 rename_in_progress` while a session is open. So the `problemFor(…)`
branches for those codes (`:249-262`) are unreachable on the real wire. Sequence: USB unplugged → tap →
server rolls back and says `save_failed` → the phone shows the same pair and **nothing else** → he taps
again, again. Tests: `android/app/src/test/kotlin/com/rankmaster2/phone/rank/AuditRankViewModelTest.kt`
(`AUDIT vote save_failed…`, `AUDIT discard move_failed…`, `AUDIT rename_in_progress…`,
`AUDIT nothing_to_undo…`, all pass by documenting the defect). The existing `RankViewModelTest` builds
these refusals only *without* a session — a shape the server never sends. The PC client reports them
(`pc/…/Link/SessionLink.cs:511-529` says so in a comment). **The two clients disagree.** Confidence: high.

**H7. Opening a folder on the phone silently closes whatever the PC client is doing. [proven by code +
existing tests]**
`BrowseViewModel.attemptOpen` (`:311-319`): on `session_already_open` it calls `DELETE /session` and retries,
unconditionally; the comment justifying it ("he is either holding the phone or sitting at the PC, never
both") predates the PC client. The PC, on its next action, gets `404 no_session` and silently reopens its
folder (`SessionLink.ReopenAfterNoSession` → `Notice.Silent`) with cues and the session counter reset. If
the PC was mid-rename, see H14. The phone's own ask-first flow (`OpenFailure.AlreadyOpen`,
`offersCloseAndRetry`, the "Close it and open this" button, `BrowseState.kt:158-166`) is dead code as a
result; `BrowseOpenTest.kt:153,175,266` assert the auto-close. Confidence: high on behaviour.

### The Windows client (never run by anyone)

**H8. Every reused pane leaks a still frame; on a small folder the client stops showing pictures after
~124 votes. [proven — PC fork]**
`StillSource.Show()` raises `Changed` for both ids with a fresh lease each (`StillSource.cs:61-62`);
`RankCoordinator.OnStillChanged` only consumes it when the pane is `Waiting`/`Refining`
(`RankCoordinator.cs:478-495`). A pane reused for the same id (`PaneState.ReusableFor`) drops the lease
undisposed, so the `DecodeBudget` (512 MB) is never refunded. Under ~32 files the recent-set is ignored
(`PairSelector.cs:17-19`) so ids repeat on nearly every pair; at ~4.1 MB per pane that is ≈124 votes before
every decode fails with "… is too large to show inside the memory budget". Restart is the only cure. The
`StillLease` finalizer that would catch it is `#if DEBUG` (`IStillSource.cs:368-380`). Test:
`pc/tests/RankMaster2.Pc.Tests/AuditLeaseLeakTests.cs`. Confidence: high.

**H9. Pressing Resume/Open while the startup connect is still running wedges the start screen. [proven —
PC fork]**
`MainWindow.OnOpened` fires `ConnectAsync` in the background (`MainWindow.axaml.cs:39`); with the tray not
up it holds the link's busy gate for up to ~18 s (offer poll 8 s + server start 10 s). Open/Resume are
enabled meanwhile. `OpenFolderAsync` → `SessionLink.EnterBusy` throws `InvalidOperationException`
(`SessionLink.cs:46-48`); `OpenFolderAsync` has no catch (`RankCoordinator.cs:104-128`), its caller is
fire-and-forget (`StartView.axaml.cs:45,49`) → `Opening` stays true, both buttons disabled, no message.
This is the first launch on a machine where the tray is not already running. `TryUndoFromStartAsync`
(`:133`) has the same gap. Tests: `AuditOpenWhileBusyTests.cs`, `AuditBusyGateTests.cs`. Confidence: high.

**H10. Pane size is never given to the decoders. [proven by grep — PC fork]**
`IStillSource.SetPaneSize` / `IVideoSurface.SetPaneSize` have no caller in `pc/src`. Stills decode at the
constructor default 960×1080 (`StillSource.cs:19-20`), video fits to 1920×1080 (`VideoSurface.cs:57-58`).
On a 4K monitor each pane is 1920×2160 and he is shown a 960-wide decode stretched — soft photographs on the
one screen the product exists for. On a 1080p monitor the default is coincidentally right. Plan E § 3.6
and `RankCoordinator.cs:16-21` both say Views wires it; Views does not. Confidence: high on the missing
call; cost depends on his display.

### Security (one user, one LAN — stated for the record)

**H11. Any paired token can open, discard into, or rename every media file in any folder on the machine.
[read — security fork]** `POST /session` accepts any absolute path (`SessionRegistry.ResolveFolder`); `POST
/session/rename` is open to any token by decision (§ 10.16). Browsing being unrestricted is a documented
choice; that the destructive operations inherit it is worth saying plainly. **[guess, Windows-only]**:
`POST /session {"folder":"\\\\evil\\share"}` bypasses `PathGuard` (which only guards `/libraries/browse` —
proven on Linux with a `..` path: browse 400, session 201) and on Windows `Directory.Exists` would make the
server authenticate to that host as the owner's account. Post-compromise only. Settle by running it on
Windows and watching for the SMB connect.

**H12. A stranger on the LAN can kill the owner's pairing window silently; a damaged `devices.json`
logs every phone out and opens a window by itself. [proven — security fork]**
Five wrong guesses from any address destroy the window and delete `pairing.json`; the correct code on the
tray's screen is then refused with the same `invalid_pairing_code` while the form still counts down
(`Audit_PairingAndGate.Five_wrong_guesses_…`). `TokenStore.Load` swallows `JsonException` by clearing both
maps (`TokenStore.cs:≈239`); `ActiveDeviceCount == 0` then auto-opens pairing at startup
(`SecurityEndpoints.cs:146`). Reproduced end to end. The owner learns of neither except from the log.

---

## Would annoy

### Server and engine

**A1. A cancelled rename resets the session counters and undo. [read; also media fork]**
`CancelInPlaceAsync` calls `Session.Start()`, which zeroes `SessionVotes`, clears cues, the recent set and
the undo point — the exact loss § 10.1 goes out of its way to prevent on a resuming `POST /session`.
`Start()`'s `bool` is discarded, so a folder the cancel left unrankable becomes `exhausted` with no signal.

**A2. One vote on a 20 000-file library costs ~130 ms on a local disk, more on USB. [proven]**
`JsonCatalog.Save` re-reads and re-parses the whole JSON, re-lists the folder, re-serialises and fsyncs on
every choice. Measured: 2 000 files → 10 ms, 392 KiB; 20 000 → 131 ms, 3.9 MiB per save (tmpfs; USB 2 with
fsync will be several times that). `tests/RankMaster2.Catalog.Tests/Audit_SaveCostTests.cs`. SPEC.md says
20k is fine; it is fine, and it is not free.

**A3. The lock file is left in the photo folder after a clean exit. [read]**
Nothing calls `SessionRegistry.Shared.Dispose()` or `CloseAsync` on shutdown (`grep ApplicationStopping`:
only `Security` and `Media` register hooks). The OS releases the handle, so the next open works, but
`.rankmaster.lock` stays in the user's folder — visible in Explorer, not hidden — and the tray's own comment
("the folder lock is released on the way out") describes the handle, not the file.

**A4. `GET /ping`'s `session` block never says `sessionId` or `state`. [proven live — media + security
forks]** `{'open': True, 'sessionId': None, 'folder': …, 'state': None}` with a session open. Only
`ReflectiveSessionStatusProvider` exists in production; it reads `OpenFolder` by reflection
(`Security/SessionStatus.cs`). § 14 and `openapi.yaml:132-136` promise both. The test that should catch it
guards its assertion with `if (session.ValueKind == JsonValueKind.Object)` and asserts no field.

**A5. `GET /libraries/browse` on a root with 11 171 children returns 1.8 MB, and `counts=false` returns
more (1.83 MB). [proven live — media fork]** `null` serialises longer than a small integer. The documented
mitigation (§ 16.5) saves CPU and costs wire bytes and phone memory; nothing caps the child count.

**A6. First still after a restart waits for a synchronous scan of the whole cache directory. [read]**
`StillCache.EnsureScanned` (`:196`) enumerates `SearchOption.AllDirectories` on the request thread. With a
full 512 MB cache of small JPEGs that is tens of thousands of `FileInfo`s before the first pair paints.

**A7. `undoAvailable` is computed from two sources that can disagree with `lastAction`. [read]**
`Materialise` (`SessionRegistry.cs:≈1180`) ORs the engine's snapshot with `LastMove`. After
`drop_missing` the client sees `undoAvailable: true` and `lastAction.type: drop_missing` — which § 10.10
says cannot be cancelled. H5 is the consequence; this is the symptom a client could at least detect.

**A8. `CurrentForMedia`'s optimistic read can build a view from a half-mutated list. [guess]**
`SessionRegistry.CurrentForMedia` copies `Session.Records` without the gate and catches
`InvalidOperationException` — but `List<T>.ToArray()` throws nothing on concurrent mutation; during
`RestoreSnapshot`'s `Clear()+AddRange()` (undo, or a rolled-back save) a copy can contain nulls (→ 500 on a
media request) or miss records (→ transient `404 unknown_media_id`, cached until the next `pairSeq`).
Rare, transient, read-only paths only. A stress test racing undo against `/media` GETs would settle it.

**A9. A stale `rankmaster_db.json.tmp` is never cleaned up. [read]** Left by a crash between write and
replace; skipped by the scan (`.tmp`), overwritten by the next save. Harmless, visible.

### The phone

**A10. A slow foreground `GET /session` reopens the gate under an in-flight vote. [proven — Android fork]**
`refresh()` is not gated by `busy` when it starts; `finish(quiet=true)` sets `busy=false` and can adopt an
older snapshot after `act()` began. A second vote leaves with the old token — the server's token check makes
it a 409, never a double vote — but he sees the old pair reappear after voting, then jump. Test
`AUDIT a slow foreground refresh…`.

**A11. `resume()` leaves the screen deaf when `pairSeq` is unchanged. [proven — Android fork]**
`RankViewModel.kt:107-111` returns early keeping `busy = true`; resolves only when the stuck coroutine times
out (5 s + up to 30 s, ×2). The comment says it fixes exactly this case. Test `AUDIT resume keeps the screen
deaf…`.

**A12. Four live video players for a frame at every video-pair change. [guess — Android fork]**
`VideoPane` uses `remember(ref.id, url, playing)` (`MediaPane.kt:137-146`): the new `ExoPlayer` is built and
`prepare()`d during composition, the old slot's `onForgotten()` runs later. `TARGET_BUFFER_BYTES = 32 MB`
each. The comment "releases the old one in the same breath" is not what the code does. On the phone that
has died twice, a `dumpsys meminfo` trace across twenty video pair changes would settle it.

**A13. The full-screen viewer fetches and decodes a second copy of each image at 2160. [read — Android
fork]** `FullScreenViewer` → `MediaWidths.forPane` = 2160 on a 2400-px screen, a different URL from the
pane's 1440 (`MediaPane.kt:66-71`, `MediaWidths.kt:41`); the pager keeps neighbours; the two pane bitmaps
stay in Coil's memory cache underneath. Bounded by the 15 % cache, so pressure, not a leak.

**A14. `CrashLog.install` chains a new handler on every `onCreate`. [read — Android fork]**
`MainActivity.kt:26`, `CrashLog.kt:22-38`: each recreate wraps the previous wrapper; on a crash the file is
written N times.

**A15. `503 session_busy` is a modal "The PC is busy… tap again"; the PC client sleeps and resends.**
Contract allows both; clients disagree. **A16.** A failed cancel and a failed foreground refresh both show
"Cannot reach the PC … tap the same picture again" (`RankViewModel.kt:280-291`) — wrong advice for a lost
cancel. **A17.** The `exhausted` screen says "Every pair in this folder has been seen" (`RankScreen.kt:263`)
— it means fewer than two rankable files remain, and the copy points at "Pick another folder" although undo
is the documented way out. **A18.** "Forget this PC" never calls `revoke()` (`Rm2App.kt:114`; the method
exists, `OkHttpRm2Client:83`, no caller) — every re-pair leaves a never-expiring token in `devices.json`;
the PC does revoke. **A19.** The phone never shows "N attempts left": `PairingFlow.refusal` regex-parses
`error.message` for a number the server puts in `error.details`; `PairingFlowTest:160` fakes a message the
server never produces, so the test proves nothing.

### The Windows client

**A20. The toast never disappears and the "late action" line can never appear. [proven by grep — PC
fork]** There is no repaint tick; `UiRoot.Render` runs only on `RankCoordinator.Changed`. `Toast.Render`
hides at `ExpiresAt` but nothing re-renders; `RankModel.ClearToastIfExpired` (`:120`) has no caller;
`LateActionLine` needs a render ≥ 300 ms into an action and the only renders are at 0 ms and at the end.

**A21. `Esc` during an in-flight action can freeze the UI for up to 4 s and never sends `DELETE
/session`. [read; timing Windows-only — PC fork]** `Quit()` calls `ReleaseVideoSurfaces()` synchronously;
`VideoSurface.Dispose` blocks on `WaitReleasedAsync` (40 × 50 ms per surface, `VideoOptions.cs:16-19`).
Then `AppLifetime.PrepareQuitAsync` → `link.CloseAsync` → `EnterBusy` throws → `catch { }`
(`AppLifetime.cs:36`). SPEC says Esc quits immediately. Harmless to data by design; the blanket catch hides
a genuinely failing close too.

**A22. `panes_painted` is never marked — the startup kit cannot answer the owner's own question. [proven
by grep — PC fork]** Plan A § 6.5 names five marks; none of `link_connecting`, `link_ready`, `tray_started`,
`panes_painted`, `first_video_frame` is emitted. `PC_CLIENT_PLAN.md` § 13 asks him which startup annoys him;
the block `kit.ps1` pastes stops at `snapshot`.

**A23. The PC client cannot rename, though the docs say it is the client that offers it.** `grep rename
pc/src` → nothing; `ISessionLink` has no member for it. Today only `rm2ctl` can start one.

**A24. The session is silently replaced under his feet.** On `404 no_session` mid-action the PC reopens
the same folder → `Notice.Silent`; cues empty, `SESSION` counter to 0, fresh pair, no line. Judgement.

**A25. Crash files are destroyed by the next install.** `CrashLog.Write` puts `crash-*.txt` in
`AppContext.BaseDirectory`; `install.ps1` step 5 renames that directory to `.old` and deletes it. Two
crashes in one second overwrite each other.

**A26. Suspected native leak: one LibVLC media reference per surface per second. [half-proven — PC
fork]** `LibVlcPlayer.Statistics()` reads `_player.Media` once a second; IL inspection of LibVLCSharp 3.10.1
shows the getter constructs a new `Media` wrapper each call, with no finalizer, and nothing disposes them
(`pc/tests/RankMaster2.Pc.Tests/Video/AuditLibVlcSharpMediaGetterTests.cs` passes). The native retain is
inferred from the libvlc C contract, not measured. `rm2vidprobe --loop` on his PC for ten minutes watching
native memory would settle it.

**A27. Windows-only guesses the PC fork could not test:** true fullscreen over the taskbar; per-monitor
DPI; whether `Esc` inside the native folder picker reaches `UiRoot` (if it does, `Quit()` runs without the
`Start.DialogOpen` check that `OpenFolder` has — the app would exit from inside the picker); LibVLC's
`plugins.dat` index build; the 2 s release poll.

### Tray and pairing

**A28. The PC client cannot find `pairing.json` if the server's `DataDirectory` is set in
`appsettings.json`. [read — security fork]** `pc/…/Enrolment/ServerPaths.cs` honours only `RM2_DATA_DIR`
and the platform default; the server and tray honour the config key first. Enrolment fails with
`NoServerFound` naming the wrong directory on the very machine the server is on.

**A29. Tray shows "Listening on 127.0.0.1:18611" and nothing says a phone cannot reach that.** Default
bind is loopback by design; the QR then carries `host=127.0.0.1` and the phone's failure text blames Wi-Fi.
**A30.** When the window's guess budget is exhausted the tray form keeps counting down (H12).
**A31.** `rm2ctl pair --take` sends the code with trust-on-first-use unless `--pin` is given, though the
offer it just read carries the fingerprint (`Program.cs`, `Rm2Api.cs:117-123`) — contradicts § 10.1.1.
**A32.** `PairingService.Redeem`: if persisting `devices.json` throws after the window is marked consumed,
the phone gets `internal_error` and the window is spent. **A33.** PC `OfferChannel.Read` catches
IO/Json/Format but not `InvalidOperationException` from `GetString()` — a malformed `pairing.json` crashes
enrolment. **A34.** `TrayApp.RefreshSession` polls `CurrentForMedia` on the UI thread every second; its
fallback path can block up to 5 s and throw `TimeoutException` into the timer tick. [read]

---

## Would confuse the next person

### Contract vs server (the drift table)

| # | Document says | Server does | Kind |
|---|---|---|---|
| C1 | § 7.2 rename "cancelled or failed → resynced" | failure does not resync (H2) | proven |
| C2 | § 4 `error.session` present iff session open + authenticated | always `null` on every `/media/*` error; `MediaHttp.WriteErrorAsync`'s comment declares this policy | proven |
| C3 | § 4 "no second error shape anywhere" | security writer omits `details`/`session`; session and media writers emit explicit `null`s — three envelope writers (`ApiResults`, `SessionResults`, `MediaHttp`) | proven live |
| C4 | § 14 `session: {open, sessionId, folder, state}` | `sessionId`, `state` always null (A4) | proven live |
| C5 | § 6 has no 405 row | routing 405 → `404 not_found`, `Allow` header dropped (`SecurityMiddleware.UseTransport`) | proven live |
| C6 | § 5.3.1 "Do not implement a best-effort read" of the lock holder | `FolderLock.ReadHolder` implements exactly that and `SessionRegistry` builds a populated `details.holder` from it (`:232-246`); output happens to be null today | read |
| C7 | § 10.10 `drop_missing` not cancellable, one level | H5 | proven |
| C8 | openapi server variable defaults to mDNS `rankmaster.local` / `_rankmaster._tcp` | no mDNS code exists (`grep -rn mdns` → 0); `CertificateStore` even adds the SAN | read |
| C9 | openapi: `POST /session/rename` → 400 on a bad body | body never read; `202` | proven |
| C10 | § 14 / openapi examples `version: "1.1.3"` | `3.0.0` | proven live |
| C11 | § 10.16 silent on cancel's `sessionVotes` | cancel zeroes it (A1) | read |
| C12 | § 2.4 data dir `RankMaster2\Server` | code uses `server`/`rankmaster2` lowercase; `MediaOptions.DefaultCacheDirectory` uses `Server` capital — two directories on Linux, one on Windows | read |
| C13 | § 10.8 "release any server-side decode of the id" | `SessionRegistry.ReleaseMedia` is never assigned; `NoOpMediaPipeline`'s comment says the media layer hooks in through it — it does not. Harmless because media streams open with `FileShare.Delete` | read |
| C14 | § 15 `clientRequestId` ≤ 64 → `invalid_request` | as documented (fine) — but `65536` is written three times (`SecurityMiddleware.MaxJsonBodyBytes`, `SessionBody.MaxBytes`, a literal in `/ping`), and body size/Content-Type are validated twice per request; the second pass is the one that produces the contract's 413/415 | read |

### Where the phone and the PC do the same thing differently

| Situation | Phone | PC | Contract |
|---|---|---|---|
| `500 save_failed` / `move_failed` with snapshot | silent resync (H6) | reported | either allowed; disagreement is the finding |
| `409 stale_pair_token` | adopt snapshot, ignore `lastAction` | classify via `lastAction.clientRequestId` / `pairToken` (§ 8.5 row for row) | § 8.5 recommends the PC's way |
| `503 session_busy` | modal, owner retaps | sleep `retryAfterSeconds`, resend once | both allowed |
| `401` mid-action | fatal "no longer paired" | re-enrol in place, resend | both allowed |
| undo lost (timeout) | no retry, wrong advice (A16) | resend once | § 13.3 says safe to retry |
| `session_already_open` | auto `DELETE` + reopen, no question (H7) | same, silent | § 10.1 intends a deliberate step |
| certificate mismatch | fatal dead end, re-pair by hand | silently re-reads local `pairing.json`, re-enrols | justified for the PC (same disk), written down nowhere |
| "Forget this PC" | clears credentials only (A18) | revokes best-effort | — |
| media 404 on a pair id | pane says "press and hold to drop it", user decides | user decides | § 11.3 |
| in-flight token on timeout | held, resent once verbatim | held, resent once verbatim (`FrozenRequest`) | § 13.3 ✓ both |

### Structure that will hurt to change

**C15.** `ReflectiveSessionStatusProvider` reaches `SessionRegistry` **by reflection on a class in the same
assembly** to avoid a `using` ("this layer must not fail to start because a neighbouring folder was
refactored"). The four-implementer split leaking into the binary; the first rename of `OpenFolder` turns
`/ping`'s session block into permanent `Closed`. `ISessionStatusProvider` is never registered by `Rm2Host`.

**C16.** Two `MediaFingerprint` classes (`Media/` public struct, `Sessions/` internal static) implement
§ 12.2 independently; they agree today (live: `v=1fcc50855f17fc30` == first 16 hex of the ETag) and nothing
enforces it. The session copy exists to avoid a project reference.

**C17.** Seven copies of the RFC 3339 format string (`SessionRegistry`, `RenameTypes`, `RenameEngine`,
`SecurityEndpoints`, `PairingService`, `MediaEndpoints`, `FolderLock` — the last without
`InvariantCulture`). Four copies of "where is the server's data directory" (`Rm2SecurityOptions`, rm2ctl
`Pairing`, PC `ServerPaths`, `MediaOptions`) and four readers of `pairing.json` (Tray `PairingChannel`, PC
`OfferChannel`, rm2ctl `Pairing.TryRead`, test `TestAuth`), each with its own "is this offer stale" rule
(ExpiresAt-newer / Code-differs / first-found). Four "fit a picture in a box" routines (`Video/PaneFit`,
`Stills/DecodeGeometry`, server `StillRenderer.FitLongEdge`, frozen `VlcFramePlayer.Fit`), each subtly
different, each with its own tests. `DecodeBudget` is a verbatim copy of the server's with a duplicated
suite. On the phone: `paneLongEdgePx` twice with different inputs (guarded by a test that they agree),
`originOf`/`MediaUrls.origin`, `stillUrl` twice, the "pairing lost" code trio twice, three depth-limited
cause walkers.

**C18.** The desktop app's abstractions survive on the server as stubs: `IMediaPipeline` → `NoOpMediaPipeline`;
`LibraryActions` still takes a pipeline, a `_releaseUi` callback and carries `RenameByRank` (the backup-copy
rename — dead on the server); `FileOps.RenameByConservativeScore`, `BackupLibrary`, `RestoreLibrary`,
`WaitUntilUnlocked` have no caller outside the frozen app and their own tests; `ICatalog.RemapIds` is a pure
function on an I/O interface; `JsonCatalog.Classify` is used only by tests.

**C19.** `RankingSession` is `List`-based with linear `Find`/`Replace`/`FindIndex` and re-materialises
`Eligible()` on every call; `Materialise` calls it four times per snapshot plus a `TryFind` per pair id.
Fine at 2 000, visible at 20 000 next to A2.

**C20.** `OpenSession.Actions` is `null!` and assigned after construction. `PairTokens.NewSessionId()` is
reused as the rename `operationId`. `SessionRegistry.DefaultPrefetchPairs` duplicates the SPEC knob that
lives in `MediaPipeline.DefaultPrefetchPairs`.

**C21.** On the phone `RankViewModel.finish()` decides silent-vs-shown by *presence of a snapshot*, not by
code — any future server code that carries a snapshot is silent by default (H6 is the consequence). View
models live in the activity store keyed by `sessionId`/`"browse-$round"` with three places (`RankGate`,
`resume`, `SessionSaver`) each reasoning about "which snapshot is newer". `PaneMenu.kt` is 796 lines, ~600
of them animation and popup placement for three rows.

**C22.** On the PC a whole second `MediaProbe` (`Video/MediaProbe.cs`, `IMediaProbe`, `FolderPolicy`) is
tested and used by nothing; `Composition` wires `Stills.MediaProbe` fully-qualified precisely because both
exist, and the two disagree (one skips `rankmaster_db.json`, `*.tmp`, Hidden/System; the other skips
nothing). `PC_CLIENT_PARTS.md` records the split; one half was never connected.

**C23.** `RankModel.TakeQuitting()` is a plain getter; its name and doc comment ("Consumes the quitting
flag…") describe behaviour that is not there (`RankModel.cs:100-106`). No live consequence today.

### Dead code and stale comments

**C24 (server).** `SecurityState.TlsConfigured` (written, never read, with a comment promising loudness),
`CertificateStore.WasGenerated`, `TokenStore.Devices`, `PairingService.IsWindowOpen`,
`ClosedSessionStatusProvider`, `MediaIdCodec.Encode` (zero call sites; its comment says the media layer
hands out URLs spelled like `links` — `SessionRegistry.MediaRefOf` uses `Uri.EscapeDataString` instead),
`Placeholder.cs`/`AuditScope` in three test projects, rm2ctl's alternative offer filenames
`pair.offer`/`pairing.offer.json` (the spec fixes the name). `CurrentForMedia`'s
`catch (InvalidOperationException)` guards a call that cannot throw it (A8).
**C25 (phone).** `Rm2Result.onOk`, `ErrorEnvelope`/`ErrorBody` (declared, never decoded),
`StillFormat.JPEG`/`NEGOTIATE`, `MediaWidths.THUMB`, `Rm2Video.stop()` ("pause keeping the buffer" — never
called), `Rm2Media.shutdown()`, `MediaPane.onState` (the whole `MediaPaneState` reporting path in the
file's header is unused), `Refused.requestId`, `ErrorCodes.NO_CURRENT_PAIR/UNDO_FOLDER_CHANGED/
STALE_PAIR_TOKEN/MEDIA_*` (declared, not branched on), `ErrorCodes` duplicating `MediaErrorCodes`.
Stale: `RankScreen.kt:171-173` ("and nothing is in flight" — `busy` is not a factor),
`Rm2VideoPlayers.kt:112-113` "6 MB a player" vs the 32 MB constant, `Rm2ImageLoader.kt:52-55` "256 MB"
vs `largeHeap` 512 MB, `PairingFlow.kt:24` names `Rm2HttpClient` which does not exist,
`Credentials.clear()` doc ("after the token is revoked") describes a flow that does not exist.
**C26 (PC).** `MainWindow.axaml.cs:7` crefs `Seams.PlaceholderRoot`, which is gone;
`pc/Directory.Build.props` says "The root is 2.0.0 now" (root is 3.0.0); `ApplySnapshotSync` guards
`_stills.Show` with `IsStill && IsStill` — a pair can never contain a video under the mixed-folder rule;
`ReleaseHandlesBeforeMove` disposes a lease `PaneControl.Render` already disposed; `PaneKind.Refining` is
documented as never entered.
**C27 (docs).** `PC_CLIENT_PLAN.md` § 6.6 and its decision table still say rename is "kept, local, under
the lock" — the design `PC_CLIENT_PARTS.md` reversed as "a second writer wearing a disguise" — with nothing
in the plan marking it superseded; § 13's six "decisions for the owner" read as open. `pc/plans/F-server-
rename.md` is a server plan in the PC client's plan folder (its header owns `src/RankMaster2.Server/…`).
`SPEC.md` is still "Rank Master 2 — specification", "App version … now 1.1.4", "Git repo:
`C:\Utils\rank-master-2\project`", a "Project layout" without Server/Tray/Actions/pc/android, and its
Rename section describes the backup-copy rename with a note pointing at the server. `README.md` is titled
"Rank Master 2", its "Run" section says `dotnet run --project src/RankMaster2.App` (the frozen app), its
Keys table says "Ctrl+Z Undo last move" (3.0.0 says any action), and `publish.ps1` still publishes RM2.
`SERVER_RUNNING.md` says `%LOCALAPPDATA%\RankMaster2\server`, the spec says `\Server`.
`CLIENT_PLAN.md` is still headed "Rank Master 2 — Android client".

### Tests that pass without proving anything

**T1 (server).** `PingContractTests.Authenticated_ping_summarises_the_session_without_a_404` guards its
one structural assertion with an `if` and asserts no field (A4). `SessionLifecycleTests.A_folder_whose_
lock_is_held_elsewhere_is_refused` opens the lock from the test process, which *is* the server process
(`WebApplicationFactory`), so the 423 comes from .NET's in-process share bookkeeping, never the OS; its
"advisory locks" escape hatch cannot fire; cross-process locking is untested on every platform.
`StillRendererCorpusTests` (8 sites) `return` with "skipped: corpus not built" — `tests/corpus/media` is
git-ignored and absent in a clean checkout, so the EXIF-orientation, wide-gamut, CMYK and 40 MP-bomb tests
are green while exercising nothing. `RenameEngineTests` never use a previously-renamed folder (H1).
`ContractShape.RequireErrorBody` accepts both envelope shapes so no test can notice C3.
**T2 (phone).** `RankViewModelTest` builds every `Refused` branch only without a session (H6).
`FakeRankClient` answers every unqueued call with a canned success, so a test that forgets to queue
"passes". `LoadControlBudgetTest`, `PaneMenuDesignTest:131-141`, `CancelNotchTest:118-132` assert design
constants against thresholds (an edit guard, not evidence of behaviour). `PairingFlowTest:160` fakes a
server message the server never produces (A19). `RankViewModelTest:236` asserts `sessionClosed` "before the
screen goes" which the code does not guarantee — it passes because the fake is synchronous.
**T3 (PC).** 56 early-return-instead-of-assert sites across 9 files (`LibVlcTests`, four `StillDecoder*`/
`StillSource*` corpus files, `MediaProbeTests`, `TimingReportTests`, `ShellTests`, `CompositionTests`);
neither `tests/corpus/media` nor `pc/tests/fixtures` exists in a clean checkout, so all nine real-LibVLC
tests skip, and two of them do not skip cleanly but fail outright (`LibVlcTests.Corpus_video_reaches_
Playing_with_frames`, `Broken_fixture_fails_with_the_expected_kind` — "No data found"), which is why the
PC solution is red from a clean checkout. `Video/MediaProbeTests` prove a class nothing uses (C22).
**T4 (both clients).** Nothing has run on Windows or on a device. The best-tested seam in the product is
`pc/tests/RankMaster2.Pc.Link.Tests` (63 tests, real Kestrel, real TLS, fault injection at the
`HttpMessageHandler`, assertions against `rankmaster_db.json` on disk).

---

## Cosmetic

- **K1.** The Windows client's start screen says **"Rank Master 2"** in 48 px (`StartView.axaml:15`);
  the window title says "Rank Master 3". First thing he reads.
- **K2.** Version strings: root `3.0.0`; `SPEC.md` `1.1.4`; `SERVER_SPEC.md` § 14 and both `openapi.yaml`
  `/ping` examples `1.1.3`; `pc/Directory.Build.props` comment `2.0.0`.
- **K3.** `rm2ctl pair` prints a truncated copy of the token *above* the full one (journal summary line
  trimmed to 110 chars); the media fork lost a run to it.
- **K4.** The pairing code is written to the tray's daily log at Information while the same method's
  comment says the payload is kept out of the log "so the credential does not end up in a log pipeline".
- **K5.** Error text that reads like a program: "Could not open the data folder: " + `e.Message` (tray);
  phone `Refused` shows "(${outcome.code})"; phone notice after `special` says "Moved to special" with no
  filename while `discard` names the file (`RankState.kt:74-75`).
- **K6.** Phone: overlay items SPEC lists (folder name, session vote count, unranked count) are never shown;
  `Counts`/`sessionVotes` parsed and unused. Deliberate per `RankScreen`'s header; recorded as a deviation.
- **K7.** `.rankmaster.lock` and `.rankmaster-rename.json` are not hidden on Windows; they sit next to
  the photos.
- **K8.** `JsonCatalog` accepts any `version` value silently and ignores the `filename` field entirely
  (the key wins). Fine for RM1 files; undocumented.
- **K9.** `StillCache.Dispose` disposes per-key semaphores under waiting renders (shutdown only).
- **K10.** Phone `PairingPayloads.decode` percent-decodes to Latin-1, not UTF-8 — irrelevant for IPs.
- **K11.** `FullScreenViewer` has three page notions (`pages[currentPage]`, `currentPage`, `settledPage`).
- **K12.** Windows-only guess: `CertificateStore.Load` uses `UserKeySet | PersistKeySet`; each start may
  leave a key container under the user's CNG store. Check `%APPDATA%\Microsoft\Crypto\Keys` after a few
  restarts.
- **K13.** `PairingRateLimiter.KeyFor(null)` puts every in-process test request in bucket "unknown".
- **K14.** `LibraryActions.UndoLastMove` does `Directory.CreateDirectory(destDir)` on the *media folder* —
  the one thing 1.1.4 taught `Save` never to do. Consequence is only an empty stray folder (the move then
  throws), but it contradicts the lesson in the file next to it.

---

## What holds up

I went looking here and found it sound. This list matters as much as the one above.

- **Never twice, three times over.** Server: token derived from `(sessionId, pairSeq, left, right)` so a
  rolled-back save yields the token the client already holds; validated in the § 8.4 order inside the lock;
  stale token is a pure read with a full snapshot; `rm2ctl cycle` 110/110. Phone: token and `clientRequestId`
  captured before the first send, resent verbatim once, never a third time, never after a resync;
  `retryOnConnectionFailure(false)`. PC: `FrozenRequest` — the request bytes are built once and every retry
  path re-sends that instance; `ISessionLink` exposes no member that takes a token, and
  `N5_NoPublicMemberOfLinkCarriesATokenOrRequestId` / `N14_PairSeqNeverOnTheWire` assert it by reflection
  over real traffic.
- **The atomic save and its refusals.** Write tmp → `Flush(true)` → `File.Replace`; never creates the
  folder; never writes `{}` over records; corrupt JSON refuses to start and is never overwritten; vote/skip
  roll back in full when the save throws. The § 8.3 asymmetry table is implemented exactly and covered by
  `RollbackAsymmetryTests` (13 cases against a jammed save).
- **One press, one action.** Phone: `busy` gate in `act()`/`cancel()`/`save()` plus `actionable` in the
  gesture, scrim eats taps while the menu is up; portrait side mapping tested at seam and edges. PC:
  `ActionGate` is pure and total (busy / panes ready / key released / 200 ms arrival guard / target
  `pairSeq`); ten presses during one in-flight call make one call; panes are keyed by `(pairSeq, side)` and
  late deliveries for a superseded generation are dropped; `Esc` during the select cue does not vote.
- **Media containment.** `MediaResolver` implements § 11.2 step for step including 5b (final symlink target
  vs the folder's own final target — a link to `/tmp/private-key.pem` named `holiday.mp4` is 403);
  `%2F`/`%5C`/`..`/rooted/`C:name` rejected; `PathGuard` rejects UNC, `\\?\`, device names, trailing dots and
  re-checks after canonicalisation; `/libraries/*` returns no bytes.
- **Auth.** Fail-closed allow-list before routing; query tokens ignored; `Bearer a, Bearer b` refused;
  invalid token on `/ping` is 401 not the public subset; `error.session` absent on 401/403/503; token store
  with 256-bit secrets salted-hashed at rest, decoy hash for unknown ids, `FixedTimeEquals`; pairing
  constant-time, whitespace-insensitive, single-use inside the lock, 5-attempt window budget, per-address
  5/min (observed `401,401,401,429…`). Pinning on both clients is the trust decision (leaf DER SHA-256,
  hostname verifier bypassed on purpose, pin-before-code, mismatch fatal and never retried); phone token in
  `EncryptedSharedPreferences` with `allowBackup=false`.
- **The still renderer.** `DecodeBudget` reserves before allocating and counts native bytes; decode buffer
  freed before the orientation buffer is taken; never below target, never upscaled; a 48 MP source is 422
  rather than an OOM; the cache never writes inside the media folder; ETag/`mediaVersion` derivation matches
  § 12.2 exactly (variant carries the *requested* width); `RangeParser` is right on every case that is easy to
  get backwards (16 unit tests).
- **Rename recovery itself, on a first rename.** Every interruption point in `RenameEngineTests` reunites
  correctly, and in the H2 failure the journal still did its job on disk. H1 is a lookup-order bug on top of a
  mechanism that is otherwise sound.
- **Bounded caches on the phone.** OkHttp 512 MB disk, Media3 1 GB LRU, Coil memory 15 %, sequential
  cancellable prefetcher that skips videos and missing refs and requests byte-identical URLs; players released
  via `RememberObserver`, viewer stops the panes, background stops everything, trim-memory drops bitmaps.
- **PC frame plumbing.** `FrameStore` copies outside the lock so a concurrent `Free()` cannot pull memory
  from under the UI thread; `StillFrame` reference counting is correct in itself (H8 is one missing
  `Dispose`); `StillDecoder` peak is two buffers, every failure path disposes both; the lazy video engine
  really does not load `LibVLCSharp.dll` for a stills folder.
- **Tray.** Single-instance mutex; pairing goes through the `pair.request` sentinel even in-process; QR is
  rendered from the same payload string the server wrote.

---

## Improvements (not problems)

1. Make the rename plan unambiguous on disk: either refuse to reuse a name that is also an `old` in the same
   plan (e.g. rename to a fresh prefix first), or record per-file progress in the journal so recovery knows
   which names have moved. Add a `RenameEngineTests` case on a previously-renamed folder (H1).
2. On rename failure, resync the session the way cancel does; on cancel, keep `sessionVotes`/cues (H2, A1).
3. Set `RenameInProgress` after the journal is written, and wrap recovery on open so a bad journal answers
   `rename_failed` and releases the lock (H3, H4).
4. Clear `LastMove` in `Drop`/`drop_missing`, or compute `undoAvailable` from `lastAction` alone (H5).
5. Phone: decide silent-vs-shown by code, not by snapshot presence; ask before closing another client's
   session; read `attemptsRemaining` from `details`; call `revoke()` on forget (H6, H7, A18, A19).
6. PC: dispose the unconsumed lease in `OnStillChanged` (or have `Show()` not raise for reused ids); wrap
   every fire-and-forget entry in a catch that resets `Opening`; call `SetPaneSize` from the view; one 250 ms
   repaint tick; mark `panes_painted`; move crash files out of the install directory; put "Rank Master 3" on
   the start screen (H8, H9, H10, A20, A22, A25, K1).
7. Close the session on server shutdown so the lock file is removed (A3); register a real
   `ISessionStatusProvider` and delete the reflection bridge (A4, C15); one `MediaFingerprint`, one envelope
   writer, one data-directory resolver, one offer reader, one `Fit` (C16, C17); delete the dead members in
   C24–C26; delete or wire the second `MediaProbe` (C22).
8. Run `PathGuard.Check` on `POST /session`'s `folder` (H11).
9. Make the folder-lock test cross-process; make the corpus tests fail (or `Skip`) rather than return when
   the corpus is absent, and build `pc/tests/fixtures` in CI so the PC solution is green from a clean
   checkout (T1, T3).
10. Retire or lock-aware RM2 before the transition, or at least say so in the README (H13).
11. Docs: bring `SPEC.md`, `README.md`, `PC_CLIENT_PLAN.md` § 6.6/§ 13, `openapi.yaml`'s server block and
    version examples up to 3.0.0; move `pc/plans/F-server-rename.md` next to the server plans (C27, C8, K2).

---

## Coverage — an honest map

**Read line by line (me):** `SPEC.md`, `SERVER_SPEC.md` (all 1475 lines), `README.md`, `Directory.Build.props`,
the three publish scripts, `.gitignore`, `SERVER_RUNNING.md` § 1–3; `src/RankMaster2.Core/*`;
`src/RankMaster2.Ranking/*` (all six files); `src/RankMaster2.Catalog/*` (all four, `RenameEngine` and
`JsonCatalog` twice); `src/RankMaster2.Actions/LibraryActions.cs`; `src/RankMaster2.Server/Sessions/
SessionRegistry.cs` (all 1289 lines), `PairTokens`, `FolderLock`, `RenameTypes`, `SessionSnapshot`,
`SessionOutcome`, `SessionEndpoints`, `RenameEndpoints`, `NoOpMediaPipeline`; `Rm2Host.cs`, `Program.cs`,
`Security/SessionStatus.cs`; `src/RankMaster2.Tray/Program.cs`, `TrayApp.cs`, `PairingChannel.cs`; the
test-name inventories of every test project and the bodies of `RankingSessionTests` (undo section),
`RenameEngineTests` (first half), `RenameTests` (first 140 lines), `SmokeTests`, `Placeholder.cs`.
**Read line by line (forks):** all of `Security/`, all of `Media/`, `Contracts/`, all of `rm2ctl` except
`Cycle.cs` (structure only); `openapi.yaml` in full; on the phone every file under `ui/rank`, `net`, `media`,
`Rm2App`, `MainActivity`, `CrashLog`, `LastFolderStore`, `BrowseViewModel/State/Labels`, `RankViewModelTest`,
`FakeRankClient`; on the PC every file under `Link/`, `Ui/Surface`, `Ui/Views/*.axaml.cs`, `App/`,
`Video/` (incl. `Backend/`), `Stills/`; `PC_CLIENT_PARTS.md`, `PC_CLIENT_PLAN.md` (rulings and tables),
`pc/plans/A,B,E,F` (the parts bearing on what was examined).
**Skimmed:** `SERVER_PLAN.md`, `CLIENT_PLAN.md` (headings), `SERVER_RUNNING.md` § 4+, `rm2ctl/Cycle.cs`,
`Tray/PairingForm.cs`, `FileLogger.cs`, `TrayArt.cs`; phone `PaneMenu.kt` (constants and rows, not the
placement maths), `BrowseScreen.kt` (strings), `PairingScreen/ViewModel/Flow` (strings and the refusal
path), `QrScanner.kt` (lifecycle), `CredentialsStore.kt`; PC `.axaml` files (text and geometry, not the
palette), `pc/tools/*` entry points, `pc/kit/*`, `publish.sh`, `install.ps1`, `prune-check.sh`; the frozen
`src/RankMaster2.App` only as evidence (lock awareness, undo, version).
**Not looked at:** `src/RankMaster2.App`'s pipeline and playback code; `tests/RankMaster2.Audit.Security`
bodies (the security fork read only names) and `tests/RankMaster2.Audit.Compatibility` with its golden
corpus — the Rank Master 1 round-trip claim is unverified by any of us; the 400+ bodies under
`tests/RankMaster2.Server.Tests/Media`; phone `PairingPayload` internals, `BrowseScreen` layout, the
~20 browse/pairing/media unit-test bodies, gradle config beyond versions, ProGuard/R8 rules, `strings.xml`;
PC `Theme.axaml`/`Icons.axaml` against RM1's colours, `HelpRows` wording, `pc/kit/Empty*`,
`libvlc/plugins.keep.txt` per-plugin justification, `rm2vidprobe`'s compare mode; Kestrel TLS
cipher/protocol configuration; `HEAD`/`OPTIONS` on `/pair`.
**Not runnable here, and not assumed to work:** everything Avalonia/Win32 (fullscreen over the taskbar,
DPI, the folder picker, key routing), LibVLC on Windows (`plugins.dat`, the 2 s release poll, A26's native
leak), the tray as a process, Schannel and the certificate's key-usage bits, `File.Replace` semantics and
case-insensitive id comparison on NTFS, SMB/NTLM on a UNC `folder` (H11), CNG key persistence (K12), and on
the phone every memory claim (A12, A13) plus PlayerView/Coil painter behaviour and gesture timing, which are
reasoned from library defaults (Coil 2.7, Media3 1.5, Compose BOM 2024.12). What would settle each is
written next to it.
**Not exercised even where it could have been:** concurrency on the server — two requests racing the
session lock, a media GET racing an undo (A8), `session_busy`; ETag byte-identity across two server
processes (encoder determinism is assumed, not measured); a hand-made decompression bomb (relied on
`StillRendererMemoryTests`' existence); client-disconnect mid-stream handle release; the H14 race.

---

## Appendix A — throwaway tests left in this worktree

All under the existing test projects; none touches production code. Run with
`export PATH="$HOME/.dotnet:$PATH"` and `dotnet test <project> --filter FullyQualifiedName~Audit`.

| File | What it shows | Result |
|---|---|---|
| `tests/RankMaster2.Catalog.Tests/Audit_RenameSecondRunTests.cs` | H1 — second rename, cancel/crash in phase 1 and crash before any move permute ratings | 3 fail, 2 pass |
| `tests/RankMaster2.Catalog.Tests/Audit_SaveCostTests.cs` | A2 — ms per save at 2k / 20k files | passes, prints |
| `tests/RankMaster2.Server.Tests/Audit_RegistryWedgeTests.cs` | H3, H4 — journal-write throw wedges the session; corrupt journal leaks the lock | 2 fail |
| `tests/RankMaster2.Server.Tests/Audit_UndoReachTests.cs` | H5 — undo after drop_missing restores an older discard | fails: "undoAvailable was True; undo answered 200 undo of discard restoring a.jpg — the file discarded three actions ago is back in the folder: True" |
| `tests/RankMaster2.Server.Tests/Audit_UndoReachesBack.cs` (media fork) | H5, two cases | fail |
| `tests/RankMaster2.Server.Tests/Audit_RenameFailureLosesVotes.cs` (media fork) | H2 | fails |
| `tests/RankMaster2.Server.Tests/Audit_ContractDrift.cs` (media fork) | C3, C5, C9 and others | mixed, as documented |
| `tests/RankMaster2.Audit.Security/Audit_PairingAndGate.cs`, `Audit_RateLimitProbe.cs` (security fork) | H12, H11 (`..` path), A4, rate-limit sequence | pass (document behaviour) |
| `android/app/src/test/kotlin/com/rankmaster2/phone/rank/AuditRankViewModelTest.kt` (Android fork) | H6, A10, A11 | 6 pass (document behaviour) |
| `pc/tests/RankMaster2.Pc.Tests/AuditLeaseLeakTests.cs`, `AuditOpenWhileBusyTests.cs`, `Video/AuditLibVlcSharpMediaGetterTests.cs`, `pc/tests/RankMaster2.Pc.Link.Tests/AuditBusyGateTests.cs` (PC fork) | H8, H9, A26 | 4 pass (document behaviour) |

H5 was reached three times independently (my `Audit_UndoReachTests`, the media fork's two cases in
`Audit_UndoReachesBack.cs`); all three fail the same way. A4 was likewise confirmed by the security fork's
`Authenticated_ping_session_block_has_null_sessionId_and_state` (passes, documenting the nulls).

**Environment note for whoever runs this next:** a process not started by this audit is listening on
`127.0.0.1:18777`; it was left alone.
