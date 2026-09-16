# Rank Master 2 — PC client, part E: the ranking surface

**Status: plan, nothing built.** Owns `pc/src/RankMaster2.Pc/Ui/` and its tests in
`pc/tests/RankMaster2.Pc.Tests/Ui/`. Consumes the four seams of `PC_CLIENT_PARTS.md` and designs none
of them. `SPEC.md` § Compare UI, § Keys, § File actions and § Ranking fix the behaviour and are not
re-decided here; `PC_CLIENT_PLAN.md` § 2, § 6.4–6.8 and § 7 are the architecture's positions and this
plan implements them. Where this document departs from either, § 1.3 says so in one list.

Written for the agent who will build it: every decision below is taken, with its reason, so nothing
has to be re-decided at the keyboard. Where a seam's exact shape is not yet known, § 2.3 says what E
assumes of it, so the coordinator can confirm or correct before code exists.

This is the screen the owner spends every minute of the program on, at a desk, with a keyboard. The
phone's surface was shaped by thumbs and six inches of glass; this one is shaped by a key map he
already has in his fingers, a mouse that is mostly not worth reaching for, and a screen large enough
that chrome sits in letterbox black rather than on the photograph.

---

## 1. Decisions, settled before any code

| Question | Answer | Why |
|---|---|---|
| The key map | **`SPEC.md` § Keys verbatim**, including the numpad digits the old app accepted. One key changes meaning (`Ctrl+Z`, next row). One key is added by focus, not by mapping (Enter on the start screen, § 4.1) | It is the owner's muscle memory. No reason was found to move anything |
| `Ctrl+Z` | **The server's one-level cancel of the last action of any kind** — `POST /session/undo`. Settled by the owner, 2026-09-16 (`PC_CLIENT_PARTS.md` § Settled). Not argued here; carried out in § 3.4 | A mis-hit key is a real vote and now has a way back. The consequence E must carry: the key does more than it used to, so what it undid is **said out loud**, and a second press is a **quiet no-op** |
| What the surface says after an undo | a toast naming exactly what came back, from `lastAction.undoneType`: "Vote taken back", "Skip taken back", "Discard taken back", "Moved back out of special", with " as `name (2).jpg`" appended when `restoredId` differs from `id` | the server names it precisely so the client can; a bare "Undone" would leave him guessing which of four things happened |
| What happens on screen, and what does not | **the old app's chrome, kept**: info card, filenames, `[1][4]` / `[5][2]` buttons, `?` + `F1` help, match strip, toasts, per-pane wait ring. Three things added, each earning its place in § 1.2: a pane state for a file that is gone or will not decode, a late-action line, a "Starting video…" line | `SPEC.md` § Compare UI names this chrome and the owner likes the screen. The phone stripped its surface because on a phone every pixel of chrome sits on the picture; on a 16:9 desktop the card, the buttons and the strip mostly sit in the letterbox of a 3:2 photograph, and a keyboard user does not have to hit anything through them |
| Cancel on a keyboard | **`Ctrl+Z` is the cancel.** No notch, no button | the phone's notch exists because a phone has no key. The PC has one, and it is already in the help sheet |
| Progress figure | **the info card as before**: `CONFIDENCE` label, `progressPercent`, a 6 px bar, `UNRANKED`, `SESSION`. No hairline | `SPEC.md` § Compare UI ("a quiet percent only"). Two progress indicators would be one too many; the phone's hairline was a replacement for a number on a screen where a number invites reading — the desktop card sits in a corner the owner has to choose to look at |
| The mouse | click a pane to vote; click the four action buttons; hover `?`; start-screen buttons; the native folder dialog. **Nothing else** — no right-click menu, no scroll, no drag, no hover effects on panes. Cursor hides after 3 s idle over the compare screen and returns on movement | `SPEC.md` says "click a pane to vote for it"; the rest of the phone's touch surface (long press, swipe, full-screen viewer) exists because a phone has no keys. Hover effects on a photograph are a blemish, and an idle pointer on a photograph is one too |
| One press, one vote | **five rules in one gate** (§ 3.1): busy, both panes ready, key released since the last accepted action, 200 ms arrival guard after a new pair lands, action targets the token on screen. No key queue, ever | a keyboard repeats at 30 Hz and a double-click is two clicks 100 ms apart; the busy flag alone lets the second press fall on the *next* pair. The retry protocol underneath is part B's and is not touched |
| Cue, then send | the ~100 ms select cue plays first, then the vote is sent; `Esc` during the cue quits without sending | `SPEC.md` § Compare UI: "Vote applies immediately after the cue … Esc during the cue must not vote" |
| Video panes | one `IVideoSurface` per pane, created on first need and kept for the process; `Start`/`Stop` per file; **two live surfaces at most**, warm video pairs hold nothing; ring with buffer percent until the first frame; "Starting video…" under the ring once the engine has been waking for more than 1 s | `SPEC.md` § Pipeline (two decoders); the first video pair after launch is the one moment a wait is long and unexplained, and a bare `0%` for five seconds looks hung |
| A file that will not decode, or is gone | **shown, not dropped** (`PC_CLIENT_PLAN.md` § 6.5). The pane says so in one sentence and lists the keys it still accepts. Never auto-skipped | the old app hid corrupt files from the one person who could fix them; auto-skip would count an impression nobody chose |
| Failures | **only when he can fix it** (`CLIENT_PLAN.md` § 3.6.3), and in the words of the folder, not the protocol. A refusal that carries a snapshot is a correction, not a failure, and is silent | the phone's rule, and it survived a real library |
| Start screen | title + version, Open `[O]`, Resume `<folder>` (focused, so Enter takes it), one dim status line for the server link, one red box for folder errors. Resume never auto-starts | `SPEC.md` § Screens. The owner's complaint is time-to-ranking; reaching for the mouse to click Resume is part of that time |
| Exhausted | back to the start screen with "No pair left to compare in *folder*", plus "`Ctrl+Z` takes back the last one" when `undoAvailable` is true; the session stays open so `Ctrl+Z` there works and returns to the compare screen if it restores a pair | the old app's behaviour, and `SERVER_SPEC.md` § 7.1: exhausted is escapable only by undo or reopen |
| Help | overlay control in the same visual tree, **not** an Avalonia `Popup` | a `Popup` is a second top-level window on Windows and misbehaves over a borderless fullscreen one; the old app's help re-layout bug lived in exactly this area |
| Icons and fonts | inline vector paths for the five glyphs (folder, trash, star, help, rename); system UI font | `PC_CLIENT_PLAN.md` § 11: remove the `Segoe MDL2 Assets` dependency |
| Palette and geometry | the old app's, to the pixel where it had pixels (§ 4.3) | "Visual chrome matches RankMaster 1" — the product does not change because the plumbing did |
| Rename by rank | a **slot** on the start screen and a `RenameView`, built only when the owner keeps rename (`PC_CLIENT_PLAN.md` § 13.2) and the operation is assigned to a part — it is in no part today | not E's to build; E's to give a button and a progress line |
| Testability | everything that decides is plain C# with no Avalonia type: key map, gate, pane state machine, notice text, coordinator. Views bind to it. Headless Avalonia tests for the views; the owner's PC for everything that can only be true on Windows | nothing Windows runs on the build box |

### 1.1 The key map, exactly

The compare screen. `Ctrl` means either Control key; `Shift`/`Alt` states are ignored except where
they would make a different key.

| Key(s) | Intent | Gate (§ 3.1) | Notes |
|---|---|---|---|
| `←` | vote left | full | cue on the left pane, then send |
| `→` | vote right | full | cue on the right pane, then send |
| `↓`, `S` | skip | full | no cue |
| `1`, `NumPad1` | discard left | full | release handles first (§ 3.3) |
| `2`, `NumPad2` | discard right | full | " |
| `4`, `NumPad4` | special left | full | " |
| `5`, `NumPad5` | special right | full | " |
| `Ctrl+Z` | undo | busy only | not gated on pane readiness — the panes it will replace do not matter; not gated on `undoAvailable` at the key level (§ 3.4) |
| `Ctrl+S` | save | busy only | toast "Saved" on success |
| `O` | open folder | not while a dialog is open | native picker; the session stays open behind it; cancelling returns to the pair |
| `F1` | toggle help sheet (pinned) | none | |
| `Esc` | quit | none | stops the cue if one is playing; raises `QuitRequested`; sends nothing |
| anything else | nothing | — | swallowed, so Avalonia's own key handling (arrow-key focus navigation, Space/Enter on a focused button) never runs on this screen |

The start screen: `O` open, `F1` help, `Esc` quit, `Ctrl+Z` undo (only while a session is open and
exhausted), `Enter`/`Space` activate the focused button (Avalonia's own behaviour; Resume takes focus
when shown, otherwise Open does).

`S` is skip and `Ctrl+S` is save: the map must check modifiers **before** letters, and a `Ctrl+S`
must never fall through to skip. This is a unit test.

### 1.2 What is on the compare screen, and what earns its place

Everything below floats over the two panes; nothing reserves layout. Positions are the old app's
(§ 4.3).

| Element | Kept / new | Earns its place because |
|---|---|---|
| Two panes, each exactly half the width, full height, uniform fit, black letterbox, 1 px seam rule | kept | the product |
| Filename, dim, top-left of the left pane (after the card) and top-right of the right pane | kept | the discard toast and the "will not decode" state both refer to a file by name; without the name on screen those sentences point at nothing |
| Info card (256 px, at 16,16): folder, `CONFIDENCE` %, bar, `UNRANKED`, `SESSION` | kept | `SPEC.md` § Compare UI; the owner's sense of how far along the folder is. It sits on letterbox for landscape photographs and on the top-left 140 px of a portrait one, as it always has |
| `[1][4]` … `[5][2]` buttons, top centre | kept | a mouse route for four actions, and — more useful — the on-screen reminder of the four keys that are not intuitive. Non-focusable, so they can never take arrow keys |
| `?` top-right; hover shows the sheet, `F1` pins it; version in the footer | kept | `SPEC.md` § Compare UI. The sheet is the whole documentation of the program |
| Match strip: bottom centre, up to 10 balls of 12 px, emerald/amber, hidden until the first vote, last ball pops in over 300 ms | kept | `SPEC.md` § Match strip; the only rating signal allowed on this surface |
| Toast, above the strip | kept, narrowed | what a file action did ("Discarded *name*", "Moved *name* to special 1", the undo sentences), "Saved", and failures the owner can fix. Nothing else — no "vote recorded", no "skipped" |
| Per-pane wait ring, 80 px, centred | kept, refined | video: ring + buffer percent from the first moment (video always takes a moment). Still: ring **without** a number, and only after 300 ms without pixels — most decodes land before that and a ring that flashes for 40 ms is noise. `SPEC.md`: "No spinner if any pixels can be shown" |
| **Pane sentence** for a file that is gone or will not decode | new | `PC_CLIENT_PLAN.md` § 6.5. "*name* is no longer in the folder — `1` removes it from the ranking" / "*name* cannot be shown — `1` discards it, `→` votes for the other". Centred, dim, on black. The one case where text on a pane is the point |
| **Late-action line**: a 2 px indeterminate line along the top edge of the screen, appearing only when an action has been in flight for more than 300 ms | new | a vote that takes 15 ms needs no sign; one that takes 800 ms on a 20k folder over a slow disk does, or the second press "did nothing" and the owner presses harder. It occupies no layout and cannot be confused with the card's bar |
| **"Starting video…"** under the ring of a video pane once the engine has been waking for more than 1 s | new | § 3.6; the one unexplained multi-second wait in the program |
| Select cue: white flash to 0.22 over 35 ms and back over 65; emerald 5 px ring in over 40, out over 60; scale to 1.03 over 45, back over 55 — total 100 ms, winner only, loser untouched | kept | `SPEC.md` § Compare UI to the millisecond. Every animated value returns to rest explicitly when the cue ends or is stopped, so nothing sticks |

### 1.3 Where this plan departs from `SPEC.md` or the old app

Listed in one place so the owner and the reviewer can see every deviation:

1. `Ctrl+Z` cancels the last action of any kind, not the last move only — **owner's decision**, `PC_CLIENT_PARTS.md`.
2. A file that will not decode is shown and offered for discard; the old app dropped it silently — `PC_CLIENT_PLAN.md` § 6.5.
3. The Resume button is focused when shown, so `Enter` resumes. Resume still never auto-starts.
4. A still's wait ring waits 300 ms before appearing. The old app showed it at once.
5. Two new lines of text exist on the compare screen: the late-action line (no text, a line) and "Starting video…".
6. An action cannot be accepted within 200 ms of a new pair landing, and an action key must be released before it can act again (§ 3.1). The old app had only the busy flag.
7. Toasts no longer echo every action; they echo file actions, `Ctrl+S`, undo, and fixable failures.
8. The help sheet is an overlay, not a popup window.
9. The pointer hides after 3 s idle on the compare screen.
10. `Ctrl+S` shows "Saved". The old app was silent unless the save threw.

### 1.4 Deliberately absent

- No μ, σ, score, rank or tier name anywhere (`SERVER_SPEC.md` § 9.3). The strip is the only rating signal.
- No cancel notch, no progress hairline, no full-screen viewer, no swipe, no long-press menu. Each was a phone's substitute for a key.
- No key queue and no type-ahead. A keystroke that arrives while an action is in flight is dropped, not held: it would vote on a pair the owner has not seen.
- No optimistic UI. The pair on screen is the last snapshot the server sent; nothing is patched locally. A vote that has not been answered has not happened.
- No confirmation dialogs during ranking. Discard and special have undo; the only confirmation in the program is rename's, on the start screen.
- No whole-screen busy overlay, no modal error dialogs. Failures are a toast or a sentence on the start screen.
- No settings, thumbnails, filmstrip, leaderboard, play/pause/seek/volume, folder browser over `/libraries/*` (`SPEC.md` § Non-goals; `PC_CLIENT_PLAN.md` § 2.1).
- No local ranking logic, no `pairSeq` in a request, no retry of any kind in `Ui/`. If a retry is ever needed it is part B's, inside the call.
- No `Popup`, no second window, no `Segoe MDL2 Assets`.
- No "Saved" screen. `Esc` quits.

---

## 2. Architecture of `Ui/`

### 2.1 The split that makes it testable

Two layers, one rule: **everything that decides has no Avalonia type in it.**

```
pc/src/RankMaster2.Pc/Ui/
  Surface/                         plain C#, net8.0, no Avalonia reference — the part that is tested here
    Intent.cs                      the eleven intents of § 1.1 (VoteLeft, VoteRight, Skip, DiscardLeft, …, Quit, None)
    KeyMap.cs                      (key, modifiers, screen) → Intent. A pure function over an abstract key enum
    ActionGate.cs                  the five rules of § 3.1; answers Accept or Drop(reason) for an intent at a time
    PaneState.cs                   one pane's state machine (§ 3.2) and what it accepts
    RankModel.cs                   the compare screen as data: snapshot, two PaneStates, busy, in-flight since,
                                   cue in progress, held keys, arrival time, toast, help pinned, strip
    RankCoordinator.cs             intent → (release handles) → ISessionLink call → apply result → prefetch.
                                   The only class that talks to B, C and D
    Notices.cs                     lastAction → toast sentence; refusal/unreachable → toast, start-screen sentence,
                                   or silence — the § 3.5 table as code
    StartModel.cs                  the start screen as data: link status, last folder, opening, error, exhausted hint
    LastFolderStore.cs             %LOCALAPPDATA%\RankMaster2\last-folder.txt — the old app's file, so Resume carries over
    Timings.cs                     every constant in § 2.4, in one place
    IClock / IUiThread             TimeProvider and a "post to the UI thread" abstraction; tests run both synchronously
  Views/                           Avalonia 11 — bound to Surface/, decides nothing
    UiRoot.axaml(.cs)              what part A hosts as window content; switches StartView / RankView / RenameView;
                                   attaches key handlers to the TopLevel; exposes QuitRequested
    StartView.axaml(.cs)
    RankView.axaml(.cs)            the two PaneControls plus every overlay of § 1.2
    PaneControl.axaml(.cs)         Image + flash + ring + wait ring + filename + pane sentence
    InfoCard.axaml, ActionBar.axaml, HelpSheet.axaml, MatchStrip.axaml, Toast.axaml, LateActionLine.axaml
    SelectCue.cs                   the 100 ms animation, built from Avalonia Animation with explicit reset
    RenameView.axaml(.cs)          built in Phase E6 only if rename survives § 13.2 of PC_CLIENT_PLAN.md
    Icons.axaml                    five StreamGeometry resources
    Theme.axaml                    the palette of § 4.3
pc/tests/RankMaster2.Pc.Tests/Ui/
  Surface/                         xunit over Surface/ with fakes for ISessionLink, IStillSource, IVideoSurface
  Views/                           Avalonia.Headless: layout, key routing, help, strip, cue
```

`Surface/` references `RankMaster2.Pc.Link` (for `ISessionLink` and the snapshot types),
`RankMaster2.Pc.Stills` and `RankMaster2.Pc.Video` (for the two media seams) and nothing else. It
does **not** reference `RankMaster2.Core`, `Ranking`, `Catalog` or `Actions`: a review greps for it
(`PC_CLIENT_PLAN.md` § 6.9.1). If the seam types force an Avalonia bitmap type into `Surface/`, the
bitmap is carried as `object` there and cast in `Views/` — the model never inspects pixels.

### 2.2 What E needs from part A, and gives back

E is a control, not a window. Part A constructs the link, the still source and the video surface
factory, constructs `UiRoot` with them, makes it the window's content, and owns the process.

| Direction | What | Notes |
|---|---|---|
| A → E | `ISessionLink` | one instance, the process's link |
| A → E | `IStillSource` | one instance |
| A → E | a way to obtain `IVideoSurface` instances | a factory; E creates at most two, on first video pair, and releases them on leaving the compare screen for a stills folder or on quit |
| A → E | the link's status (§ 2.3, `ISessionLink`) | if the seam does not carry it, A passes an observable of the same shape |
| A → E | the app version string | or E reads the entry assembly's informational version, which is `Directory.Build.props`; either way `Directory.Build.props` is the one source |
| E → A | `QuitRequested` event | raised by `Esc`. A fires `DELETE /session` with a 500 ms timeout and exits (`PC_CLIENT_PLAN.md` § 6.8). E does not touch process lifetime |
| E → A | nothing else | E does not know whether the window is fullscreen, on which monitor, or at what DPI; it reads `TopLevel.RenderScaling` and its own bounds |

Keys: `UiRoot` attaches `KeyDown` and `KeyUp` handlers to its `TopLevel` with
`RoutingStrategies.Tunnel` in `OnAttachedToVisualTree`, marks handled every key `KeyMap` recognises
and every key in `SPEC.md` § Keys, and detaches in `OnDetachedFromVisualTree`. Tunnel, because
Avalonia's default handling would otherwise move focus with the arrow keys and click a focused button
with Space — both fatal on this screen.

### 2.3 What E assumes of each seam

The coordinator writes the seams as real types before execution. E has not seen them. What follows is
what E's design needs from each; every line marked **asks** is something E cannot do without and that
the one-line description in `PC_CLIENT_PARTS.md` does not obviously include. The executing agent reads
the real types first (Phase E0) and reconciles: where a type differs in shape but not in capability, E
adapts in `RankCoordinator`; where a capability is missing, the executing agent reports it, and does
not work around it inside `Ui/`.

**`ISessionLink` (B).**

- `Open(folder)` returns either a snapshot or a typed refusal (code, message, details). E assumes
  `Open` *replaces* whatever is open — `DELETE` then `POST`, as `PC_CLIENT_PLAN.md` § 6.8 decides — so
  `session_already_open` never reaches E. **Asks** that this is B's, not E's.
- `Vote(side)`, `Skip()`, `Discard(side)`, `Special(side)`, `Undo()`, `Save()`, each returning one of:
  **Applied**(snapshot), **Adopted**(snapshot) — the request was refused `409 stale_pair_token` and
  the carried snapshot is the truth — **Refused**(code, message, details, snapshot?) or
  **Unreachable**(reason, pinMismatch). **Asks** that Applied and Adopted are distinguishable: E shows
  the `lastAction` toast only for Applied, or for Adopted when `lastAction.clientRequestId` equals the
  id of the request E just made — which means either B exposes the request id it used, or B answers
  the question "did *my* request land" itself. Either is fine.
- The pair actions take the `pairToken` of the pair E is displaying. E passes it from the snapshot it
  holds, so a press always names the pair the owner saw; B may compare it against its own held
  snapshot and treat a mismatch as a programming error.
- Retries, request ids and the § 13.3 protocol are entirely inside these calls. E never sees them.
- A **status** observable: `Connecting`, `StartingServer`, `Ready`, `Unreachable(reason)`,
  `NotPaired(reason)`, `PinMismatch`. **Asks**; the start screen's one dim line is drawn from it.
  If A owns server launching, A may be the one to supply it — E does not care which.
- Thread affinity: E assumes callbacks and completions may arrive on any thread and marshals them
  itself through `IUiThread`.

**`Snapshot` and friends (B).** § 9 of `SERVER_SPEC.md`, read-only: `state`, `folder`,
`folderName`, `policy`, `sessionVotes`, `counts.unranked`, `progressPercent`, `cues` (oldest
first), `pair` (`left`/`right`: `id`, `kind`, `sizeBytes`, `mediaVersion`), `pairToken`, `pairSeq`, `warmPairs`,
`undoAvailable`, `lastAction` (`type`, `undoneType`, `id`, `restoredId`, `side`, `clientRequestId`).
E uses `sizeBytes == null` as "the file is gone" before it even asks C or D.

**`IStillSource` (C).**

- "Give me this id at this pane size": a request keyed by an id, a folder, a pane size in physical
  pixels, and a **request handle** E chooses (E uses `(pairSeq, side)`), with results delivered
  asynchronously and possibly **twice** — a preview and then the full decode (`SPEC.md` § Pipeline,
  720 px first paint over 4 MB). Each delivery carries the handle, so E can drop one that belongs to
  a pair no longer on screen. **Asks** the handle.
- A failure state that distinguishes **Missing** (the file is not there) from **Undecodable** (it is
  there and will not decode), because the pane says different sentences and offers different keys
  (§ 3.2). **Asks** the distinction.
- `Prefetch(pairs, paneSize)` for `warmPairs`; E calls it after the current pair is on screen, with
  stills only.
- `Release(id)` — a no-op for a Skia decode whose stream is closed, kept so the release-before-move
  order (§ 3.3) is the same code path for both kinds.
- The pixel type: an Avalonia `IImage`/`Bitmap`, or a pixel buffer E can wrap once in `Views/`.
  Either; `Surface/` never looks inside it.

**`IVideoSurface` (D).**

- Per instance: `Start(path, fitPixels)`, `Stop()`, `Release()` returning a task that completes only
  when the file handle is gone (the old app's `WaitUntilUnlocked`, ≤ 2 s), `SetFit(pixels)`,
  a frame bitmap E can draw and an event that fires when a new frame is in it (the first fire is
  "ready"), `BufferPercent` while no frame has arrived, and a failure event carrying the **path** it
  failed on — the old `VlcFramePlayer` bug (a stale error dropping the next file) is avoided by E
  ignoring any failure whose path is not the one this pane last asked to start.
- **Asks** an engine state readable by E: `Cold`, `Starting`, `Ready`, `Unavailable(reason)`. It
  drives "Starting video…" (§ 3.6) and the sentence for a PC that cannot play video at all. If D does
  not expose it, E infers `Starting` from "no frame and no buffer movement for 1 s on the first
  video pair after launch", which is worse.
- Frames land on any thread; E marshals the invalidate.

### 2.4 Every constant, in one file

| Name | Value | Where it acts |
|---|---|---|
| `CueMs` | 100 | § 1.2, the select cue's total length |
| `ArrivalGuardMs` | 200 | § 3.1, rule 4 |
| `LateActionLineAfterMs` | 300 | § 1.2, the top-edge line |
| `StillRingGraceMs` | 300 | a still pane's ring waits this long |
| `EngineWakeNoticeAfterMs` | 1000 | "Starting video…" |
| `ToastMs` | 2500 | toast lifetime; a new toast restarts it |
| `ReleaseWaitMaxMs` | 2000 | how long § 3.3 waits for D's `Release` before sending anyway |
| `CursorHideAfterMs` | 3000 | compare screen only |
| `StripBallPopMs` | 300 | the last ball's entrance |
| `ProgressBarAnimMs` | 200 | the card's bar, both directions |

All of them are read through `Timings` and the model takes an `IClock`, so a test can run the whole
gate with a fake clock and no `Task.Delay`.

---

## 3. The hard problems, and how they are solved

### 3.1 One press is one action — the gate

Part B guarantees that one *request* cannot become two votes. E's job is that one *press* cannot
become two requests, and that a press meant for one pair cannot land on the next. Both fail with the
old app's single busy flag: a key held down repeats every ~33 ms, the busy flag clears when the vote
resolves (~15 ms on a small folder), and the next repeat votes on a pair nobody has looked at. A
double-click does the same with a mouse.

`ActionGate.Try(intent, now)` answers **Accept** or **Drop(reason)**, in this order, and stops at the
first Drop:

1. **Busy.** An action is in flight (from the moment an intent is accepted until its result has been
   applied to the model, cue included). Drop. Nothing is queued.
2. **Ready.** For vote, skip, discard and special: both panes are in a state that accepts this intent
   (§ 3.2). A pane showing a wait ring accepts nothing; a pane showing "gone" accepts only its own
   discard. Drop otherwise. Undo and save skip this rule.
3. **Released.** The physical key that carries this intent has been released since the last accepted
   action. `KeyDown` adds to a held set, `KeyUp` removes; an accepted action clears nothing — the
   release does. So a held `→` votes once, however long it is held, and however fast the server is.
   Mouse clicks count as pressed-and-released.
4. **Arrival guard.** At least `ArrivalGuardMs` (200) have passed since the current pair landed in
   the model. A pair cannot be judged in 200 ms — two new photographs have to be looked at — so no
   legitimate press is lost, and the second half of a double-click, a key bounce, or a repeat that
   slipped past rule 3 on a different key cannot reach the new pair.
5. **Target.** The intent carries the `pairToken` the model displays. The coordinator sends that
   token, not "whatever is current when the call is made". If the model's token has changed between
   accept and send (it cannot, under rule 1, but the check is one line), Drop.

Rules 3 and 4 are the ones the old app did not have and the phone did not need. They cost nothing a
person can feel and they are the reason a fast keyboard cannot outrun the surface.

On Accept, the coordinator sets busy, plays the cue (vote only), and — unless `Esc` arrived during the
cue — calls the link once. On any result it applies the snapshot (if one came), clears busy, records
the arrival time, and asks C to prefetch. Drops are silent, always: a dropped press is a press that
would have been a mistake.

**Esc during the cue.** `Esc` sets a `quitting` flag and stops the cue; the coordinator checks the
flag when the cue task completes and does not call the link. `QuitRequested` is raised at once. `Esc`
during an in-flight request also raises `QuitRequested` at once — the request lands or not
(`SERVER_SPEC.md` § 13.4), and A's `DELETE /session` does not wait for it. The pair on screen is
unseen either way.

### 3.2 What a pane is, and what it accepts

`PaneState` is one side of the screen; two of them make the pair. It is keyed by the `(pairSeq, side)`
generation it was created for, and **every** delivery from C or D carries that key and is dropped if
it does not match the pane's current generation. Checking by id alone is not enough: with two or
three eligible files the same id is on screen pair after pair, sometimes on the other side
(`SERVER_SPEC.md` § 7.4.1), and a late decode for "left, pairSeq 41" must not land on "right,
pairSeq 42" just because the filename agrees.

| State | Entered when | Shows | Accepts |
|---|---|---|---|
| `Waiting` | the pair landed; the request to C or D has been made | filename at once; still: ring after 300 ms; video: ring + percent at once; "Starting video…" per § 3.6 | nothing |
| `Ready` | still: first pixels (preview or full); video: first frame | the picture | everything |
| `Refining` | a still preview is shown and the full decode is pending | the preview | everything — `SPEC.md`: "paint the first decodable image as soon as one exists; refine in place" |
| `Gone` | `sizeBytes == null` in the snapshot, or C/D report Missing | "*name* is no longer in the folder — `1` removes it from the ranking" (or `2`) | only this side's **discard**, which the server turns into `drop_missing` (§ 10.8). Not special, not a vote for either side |
| `Undecodable` | C reports Undecodable, or D's failure event names this pane's current path | "*name* cannot be shown — `1` discards it, `→` votes for the other" | this side's discard or special; a vote for the **other** side; skip. Not a vote for this side |
| `NoVideoEngine` | D's engine state is `Unavailable` | "Video cannot play on this PC — *reason*" | this side's discard or special; skip; `Esc` |

The pair is "ready" for rule 2 of the gate when the intent is acceptable to both panes. A vote-left
needs left in `Ready`/`Refining` and right in `Ready`/`Refining`/`Undecodable`.

**Pane reuse.** When a new snapshot lands and a side's id **and** `mediaVersion` are unchanged
**and** the pane is `Ready` or `Refining`, the pane keeps its pixels, its state and its video
surface, and only its generation key moves forward. No re-decode, no flash. A changed `mediaVersion`
under the same id means the bytes changed (`SERVER_SPEC.md` § 11.3) and the pane reloads like any
new id. This is what makes a cancelled vote — which puts the *identical*
pair back — look like nothing moved, and what keeps a two-file library from re-decoding the same two
files forever. A video pane whose id is unchanged keeps playing; it does not restart.

**Frames that arrive late.** The old app checked `pair.Left == id` in `OnFrameReady`; the generation
key replaces that. A preview that arrives after the full decode is dropped (the model records which
pass it has).

### 3.3 Handles, released before the server moves a file

Discard, special, and undo-of-a-move all make the server move a file, and a file this process holds
open cannot be moved on Windows (`PC_CLIENT_PLAN.md` § 6.3). The order, inside the coordinator, before
the link is called:

```
for the side being moved:
    if the pane has a video surface:  Stop(); await Release()   — ≤ ReleaseWaitMaxMs
    drop the pane's bitmap reference; call IStillSource.Release(id)
    the pane goes to Waiting with no ring (the request will replace it)
then: link.Discard(side) / link.Special(side)
```

For **undo** the file that comes back is not on screen, so nothing is held; but a cancelled vote or
skip replaces the pair, so both panes are re-keyed after the result like any other action. If
`Release()` has not completed within `ReleaseWaitMaxMs`, the request is sent anyway: the server
retries the move itself for a second (`SERVER_SPEC.md` § 10.8), and a `500 move_failed` is answered
with a toast the owner can act on ("*name* is in use by another program — press the key again"). The
token is still valid on that path (§ 8.3), so the key simply works the second time.

Nothing else in E ever holds a file: stills are closed after decode (C's promise), and only the two
live surfaces hold anything.

### 3.4 `Ctrl+Z` — decided; what the surface must carry

The owner has decided that `Ctrl+Z` cancels the last action of any kind (`PC_CLIENT_PARTS.md`
§ Settled by the owner). E's two obligations follow from the key now doing *more* than it did.

**Say what came back.** After an Applied undo the toast is built from `lastAction.undoneType` and
nothing else:

| `undoneType` | Toast |
|---|---|
| `vote` | "Vote taken back" |
| `skip` | "Skip taken back" |
| `discard` | "Discard taken back" — and, when `restoredId != id`, "Discard taken back as *restoredId*" |
| `special` | "Moved back out of special 1" — same `restoredId` suffix |

The toast sits where every toast sits (above the strip), for `ToastMs`. For a cancelled vote the
strip also loses its last ball in the same frame (the snapshot's `cues` is one shorter) and `SESSION`
on the card drops by one; for a cancelled discard the pair is freshly picked and both panes reload.
The pair returning is the loudest signal; the toast names which of four things caused it.

**And then nothing, quietly.** There is one level. A second `Ctrl+Z` — and a held `Ctrl+Z` produces
one within 33 ms — must be an unremarkable no-op, not an error. Three layers make it so:

1. **Rule 3 of the gate.** A held `Ctrl+Z` is one accepted action until `Z` is released. The
   repeats never reach the link.
2. **`undoAvailable` at the coordinator.** After an undo, the snapshot says `undoAvailable: false`.
   A fresh `Ctrl+Z` (released and pressed again) is accepted by the gate and then answered by the
   coordinator **without a request**: nothing is sent, no toast, no sound, busy is not even set. The
   same happens on a fresh session before the first action. The old app was silent here too
   (`UndoLastMove()` returned false, no toast).
3. **`409 nothing_to_undo` from the server**, if the client's `undoAvailable` was stale (the phone
   acted in between): silent. Busy clears, the model is unchanged, the owner sees nothing. It is not
   his to fix.

So the owner can lean on `Ctrl+Z` and get exactly one cancellation, then silence — the same thing a
text editor does at the bottom of its history. The help sheet's row reads "Undo last action" so the
single level and the wider meaning are both stated where he will read it.

Everything else about undo is the server's: the pair is always replaced (§ 7.4.4) — for a cancelled
vote by the identical pair, which § 3.2's pane reuse renders without a flicker; for a cancelled move
by a fresh pick. `Ctrl+Z` is not gated on pane readiness (the panes are about to be replaced) and is
accepted from the start screen when the session is exhausted (§ 4.1).

### 3.5 Failures — say something only when he can fix it

Every result the link can return, and what the surface does with it. "Silent" means the model
updates and nothing is written anywhere. "Toast" is a sentence above the strip. "Start screen" means
the compare screen closes and the sentence sits in the start screen's red box.

| Result | Surface | Why |
|---|---|---|
| Applied | apply snapshot; toast only for file actions, undo and save (§ 1.2) | |
| Adopted (`409 stale_pair_token` carried a snapshot) | apply snapshot, silent — unless `lastAction` is *this* request's, in which case treat as Applied | a correction is free (`SERVER_SPEC.md` § 8.5); the phone did the same |
| `409 no_current_pair` | apply the snapshot if carried, else refresh via the link's next open; go to exhausted (§ 4.1) | the pair moved under us to nothing; not his |
| `409 nothing_to_undo` | silent | § 3.4 |
| `409 undo_folder_changed` | silent | the server clears it; nothing on this screen is different |
| `500 save_failed`, `recordsChanged:false` | toast: "The ranking file could not be written — nothing was lost, and that press did not count. Check the drive is still there, then press the key again." | he can fix it, and the token is still valid so the key works |
| `500 save_failed`, `fileMoved:true` | toast: "*name* was moved, but the ranking file could not be written. `Ctrl+S` will retry it." | the file is where he asked; the save is his to retry |
| `500 move_failed` | toast: "*name* could not be moved — it is in use by another program. Press the key again." | he can close the other program |
| `503 session_busy` | toast: "The server is busy — try again in a moment." | one press, and only if B did not already absorb it |
| `404 no_session` (server restarted or the folder was closed elsewhere) | start screen: "The server closed *folder*. Everything up to the last answered action is saved. Resume to carry on." If a request was in flight when it happened, add: "The very last press may not have counted." | he can fix it (Resume); § 13.4 says the last vote is unknowable and honesty costs one sentence |
| Unreachable, not pin mismatch | toast: "Lost the server — nothing was lost; press the key again." The second consecutive Unreachable goes to the start screen with the link status line showing what A/B know | he can restart the tray; the token is still valid |
| Unreachable, **pin mismatch** | start screen, red, no button but Quit: "Something answered that is not your Rank Master server. Nothing was sent to it." | the one security stop; never a "continue" |
| `401` any (`token_revoked`, `invalid_token`) | start screen: "This PC is no longer paired with its server. *what A/B offer as the re-pair route*." | he can re-pair |
| `423 folder_locked` on open | start screen box: "Something else has *folder* open — another Rank Master, or a rename still running. Close it and try again." | the one open-conflict worth a message (`CLIENT_PLAN.md` § 3.6.3) |
| `409 folder_not_rankable` on open | box: "*folder* needs at least two photos or two videos to rank. Found *stills* photos and *videos* videos." | he can pick another folder |
| `409 library_json_unreadable` on open | box: "*folder*\rankmaster_db.json is damaged. It was not touched. Repair or move it, then open the folder again." | he must fix the file; the server refuses to write it |
| `404 folder_not_found`, `400 folder_not_a_directory`, `403 folder_access_denied` on open | box: the plain sentence with the path | he can see what is wrong |
| C: Undecodable / Missing | pane sentence (§ 3.2) | he can discard it |
| D: will not play (this pane's path) | pane `Undecodable` | " |
| D: engine `Unavailable` | pane `NoVideoEngine` with the reason | he can put `libvlc\` back next to the exe |

The link status line on the start screen (§ 4.1) is the only other failure surface, and it is one
line. Nothing in this table is a dialog.

### 3.6 Video panes, and the engine waking up

The two panes are the only place video exists in E. A pane's `IVideoSurface` is created the first
time that pane is asked to show a video and kept for the life of the compare screen; leaving for a
stills folder or quitting releases both. Two surfaces exist at most (`SPEC.md` § Pipeline); warm
video pairs are not prefetched at all — they hold a path in the snapshot and nothing in E.

On a new pair, per side: if the id is unchanged and the pane is `Ready`, nothing (§ 3.2); otherwise
`Stop()` the surface, `Start(path, fit)` with the pane's physical pixel size, state `Waiting`, ring
with `BufferPercent` at once. First frame → `Ready`, ring gone. Each subsequent frame → invalidate
the pane's `Image`. Loop, mute and no controls are D's defaults and E offers no control that could
change them.

**The engine's first wake.** LibVLC initialises lazily, off the UI thread, the first time a snapshot
says `policy: "video"` (`PC_CLIENT_PLAN.md` § 6.2). With a cold plugin cache that can take seconds,
and the first video pair of a session is exactly when the owner is looking. So:

- Both panes show the ring at once, as for any video, with the percent D reports (likely `0%`).
- If D's engine state is still `Starting` after `EngineWakeNoticeAfterMs` (1 s), each waiting pane
  adds one dim line under its ring: **"Starting video…"**. It goes when the pane's first frame lands.
- If the engine state becomes `Unavailable`, both panes go to `NoVideoEngine` with D's reason. The
  folder is unrankable in practice; `Esc` and the discard keys are what is left, and the sentence
  says what to check.
- Later pairs never see this line: the engine is up, and a plain ring with a moving percent is enough.

Pane pixel size: computed from `RankView`'s bounds × `TopLevel.RenderScaling` at layout, handed to
both surfaces via `SetFit` and to C as the decode size. Recomputed on a bounds change, not per frame;
the window is borderless fullscreen so it changes on monitor or DPI changes only.

### 3.7 Focus, keys, and Avalonia's own opinions

Avalonia gives arrow keys to focus navigation and Space/Enter to the focused button. Both are fatal
here: a `→` that moves focus instead of voting, or a Space that "clicks" whatever last had focus.
Three measures, all in `Views/`:

1. Keys are handled at the `TopLevel` in the **tunnel** phase and marked handled for every key in
   § 1.1, before any control sees them.
2. Every control on the compare screen is `Focusable="False"` — the buttons, the panes, the card, the
   help button. `RankView` itself takes focus on entering so the `TopLevel` has a focus target.
3. `KeyboardNavigation.TabNavigation="None"` on `RankView`.

On the start screen, the reverse: buttons are focusable, Resume takes focus when it is shown
(otherwise Open), and Enter/Space do what Avalonia does with a focused button. `O`, `F1`, `Esc` and
`Ctrl+Z` are still handled in the tunnel phase so they work whatever has focus.

The native folder dialog (`TopLevel.StorageProvider.OpenFolderPickerAsync`) is modal to the window;
keys go to it while it is open, including `Esc`, which closes the dialog and does not reach E. The
model records `dialogOpen` so `O` cannot open a second one.

### 3.8 The help sheet as an overlay

The old app's help lived in a `Popup` and had a re-layout bug the version history remembers. Here it
is a `Border` in `RankView`'s (and `StartView`'s) own overlay layer, anchored top-right below the `?`,
shown by pointer-over on the `?` or on the sheet itself, hidden when the pointer leaves both unless
`F1` has pinned it. `F1` toggles pinned. `Esc` still quits, sheet or no sheet. Its rows are exactly
§ 1.1's table in the old order — Select Left `[←]`, Select Right `[→]`, Skip `[↓]/[S]`, Discard
`[1] / [2]`, Special `[4] / [5]`, Open `[O]`, Undo last action `[Ctrl+Z]`, Save `[Ctrl+S]`, Help
`[F1]`, Exit `[Esc]` — with the version in the footer. A headless test checks the sheet's rows against
`KeyMap` so the two cannot drift.

---

## 4. Screens

### 4.1 Start

The zinc-to-black gradient, the blue-to-emerald title with the version set high to its right, a
512 px column of buttons, exactly as the old app draws it.

| Element | Behaviour |
|---|---|
| **Select Folder `[O]`** | native folder picker → `link.Open(path)`. While opening: both buttons disabled, a dim "Opening *folder*…" under them (a 20k-file scan on a USB disk is a visible wait) |
| **Resume *folder name*** | shown when `LastFolderStore` holds a path that is still a directory; **focused** when shown so Enter takes it; `link.Open(path)`. Never auto-starts |
| **Rename Files by Rank** | the slot; built in E6 only if rename is kept and assigned (§ 1) |
| Red box | the § 3.5 open-time sentences, and the exhausted sentence |
| Link status line, dim, below the buttons | from the link's status: `Ready` → nothing; `Connecting` → "Connecting to the ranking server…"; `StartingServer` → "Starting the ranking server…"; `Unreachable` → "The ranking server is not running and could not be started. Start *RankMaster2.Tray* and press Open or Resume again." plus a **Try again** button; `NotPaired` → "This PC is not paired with its server: *reason*." with whatever re-pair route A/B offer; `PinMismatch` → the red security sentence of § 3.5 |
| `?` top-right, `F1` | the same help sheet |

Open and Resume are never disabled by link status: pressing them while `Unreachable` is the retry
(A/B attempt the connection or the launch inside `Open`), and failure lands in the status line. There
is no separate "retry" key.

On success the compare screen opens and `LastFolderStore.Save(folder)` runs. A failed open saves
nothing.

**Exhausted** returns here: the compare screen closes, the box says "No pair left to compare in
*folder*." and, when the snapshot's `undoAvailable` is true, "`Ctrl+Z` takes back the last one." The
session stays open. `Ctrl+Z` here calls the link's undo; if the snapshot that comes back is `ranking`
the compare screen reopens on it; if it is still exhausted the sentence stays. Open or Resume from
here replaces the session as usual.

### 4.2 Ranking

Two `PaneControl`s in a two-column grid with equal star widths, a 1 px rule between them, and the
overlays of § 1.2 in this z-order, bottom to top: panes → filenames → info card → action bar → match
strip → toast → late-action line → help sheet. The pointer is a hand over the panes and hides after
`CursorHideAfterMs` idle.

On entering: focus to `RankView`, pane size computed and handed to C and D, the first snapshot
applied, both panes `Waiting`, prefetch of `warmPairs` (stills only) requested. On leaving (exhausted,
`O` to another folder, quit): stop and release both surfaces, drop both bitmaps, cancel any toast
timer, reset the cue.

### 4.3 Geometry and palette, from the old app

Kept to the value where the old app had one; `Theme.axaml` holds them.

| | Value |
|---|---|
| Compare background | `#000000`; pane rule `#111827` 1 px |
| Start gradient | `#18181B` → `#000000`, top-left to bottom-right |
| Title | 48 px black weight, gradient `#60A5FA` → `#34D399`; version `#71717A` 12 px, top-right of the title |
| Info card | 256 wide at (16,16); `#99000000` fill, `#1AFFFFFF` 1 px border, radius 8, padding 12; labels 10 px `#71717A`; folder 12 px bold white; confidence % Consolas-like monospace 12 px `#60A5FA`; bar 6 px, track `#0DFFFFFF`, fill `#60A5FA`, width 232 × progress; `UNRANKED` 14 px `#FB923C`; `SESSION` 14 px `#93C5FD` |
| Filenames | 11 px `#66FFFFFF`; left at margin (280,18), right at margin (…,18,64) — after the card and before the `?` |
| Action bar | top centre, margin-top 24; 48 px circles `#66000000` / `#1AFFFFFF`; captions `[1]` etc. 11 px bold monospace `#4DFFFFFF`; 64 px gap between the two groups, 12 px inside |
| `?` | 36 px circle top-right at (…,16,16); same fill and border as the card |
| Help sheet | 208 wide, `#CC000000`, radius 8, padding 16, rows 22 px, labels `#9CA3AF`, keys monospace |
| Match strip | bottom centre, margin-bottom 32, padding 12, `#66000000`, `#0DFFFFFF` border, radius 18; balls 12 px, margin 4, confirmation `#B310B981`, upset `#B3F59E0B`, soft shadow |
| Toast | bottom centre, margin-bottom 72 (above the strip), `#CC18181B`, `#33FFFFFF` border, radius 20, padding 20×10, 14 px white |
| Wait ring | 80 px, stroke `#4B5563` 4 px; percent 18 px `#9CA3AF`; "Starting video…" 12 px `#71717A` below |
| Pane sentence | 14 px `#9CA3AF`, centred, max width 70 % of the pane, wrapping |
| Late-action line | 2 px at the top edge, `#60A5FA` at 60 %, indeterminate sweep |
| Select cue | flash `#FFFFFF` to 0.22; ring `#34D399` 5 px; scale 1.03 about the pane centre; timings in § 1.2 |
| Start buttons | 512 wide column; base `#1AFFFFFF` / `#1AFFFFFF` border, radius 12, 18 px semibold; Resume `#332563EB` / `#4D3B82F6`; Rename `#1AF59E0B` / `#4DF59E0B`, text `#F59E0B`; error box `#1AEF4444` / `#80EF4444`, text `#F87171` 13 px |

---

## 5. Phases

Each phase ends with its tests green on the build box and a one-paragraph note of what is *not*
proven here. Phases E1–E3 can run before parts C and D exist, against fakes of their seams; E4 needs
D's real surface on Linux LibVLC to mean anything.

**E0 — Seam intake.** Read the frozen types in `Link/`, `Stills/`, `Video/`. Fill in a table:
each **asks** of § 2.3 → present / present under another name / absent. Absent ones go to the
coordinator as a list, not as workarounds. Write the fakes (`FakeSessionLink` scripted with a list of
results, `FakeStillSource` and `FakeVideoSurface` that deliver on command). Exit: the table, the
fakes compiling against the real seam types.

**E1 — The surface without pixels.** `Intent`, `KeyMap`, `ActionGate`, `PaneState`, `RankModel`,
`RankCoordinator`, `Notices`, `Timings`, `IClock`/`IUiThread`. Tests (§ 6.1) for every rule of § 3.1,
every transition of § 3.2, every row of § 3.5, and the § 3.4 undo behaviour. Exit: a scripted session
— open, 20 mixed actions, an undo, a stale adoption, a `no_session` — runs through the coordinator
against the fake link with exactly the expected calls and no others.

**E2 — The start screen.** `StartModel`, `LastFolderStore`, `StartView`, `UiRoot` with key routing
and the `TopLevel` tunnel handlers, the help sheet, `Theme.axaml`, `Icons.axaml`. Headless tests for
focus (Resume focused when shown), status line per link state, error box per open refusal, `O`
opening the picker exactly once. Exit: A can host `UiRoot` and reach a start screen that opens a
folder against the real server on this box and reports refusals in the words of § 3.5.

**E3 — The compare screen, stills.** `RankView`, `PaneControl`, `InfoCard`, `ActionBar`,
`MatchStrip`, `Toast`, `LateActionLine`, `SelectCue`, cursor hiding. Bound to C's real
`IStillSource` if it exists, the fake otherwise. Headless tests for layout (two panes of equal width,
full height), z-order, key routing (a `→` reaches `KeyMap` whatever has focus; Space does nothing),
the strip's ball count against `cues`, the cue's completion and reset, `Esc` during the cue sending
nothing, the late line appearing at 300 ms on a fake clock. If an X display is available on the build
box (Xvfb — a request to the server owner, not a requirement), run the real binary against the real
server on a scratch folder of real photographs including EXIF-rotated and portrait ones and look at
it. Exit: the owner receives a build with A+B+C+E and ranks 50 pairs of stills; § 7.1's first six
points reported.

**E4 — Video panes.** `IVideoSurface` binding, two-surface rule, `Waiting` with percent, the engine
wake line, `NoVideoEngine`, release-before-move with the real `Release()` task, path-checked
failures. Tested with the fake surface for state, and with D's Linux LibVLC build against AV1 files
for the frame path if D provides one. Exit: a test discards a *playing* fake video and asserts
`Stop`, `Release` awaited, then the link call, in that order; on the owner's PC, a 4K AV1 pair plays
and discarding the left one moves the file on the first key.

**E5 — Failure surfaces, end to end.** Against the real server on this box, through the coordinator:
kill the server mid-vote (start screen sentence, Resume works, one vote at most); rename the folder
under an open session (`no_session` path or `sizeBytes: null` → `Gone`); a truncated JPEG in the
folder (`Undecodable` → `1` discards, nothing auto-skips); a file deleted under the session (`Gone` →
`1` → `drop_missing`, no undo offered, `Ctrl+Z` silent); a read-only folder (`save_failed` toast,
key works again after `chmod`). Exit: each row of § 3.5 that can be provoked here has a test that
provokes it and asserts the surface.

**E6 — Rename slot, polish, review.** `RenameView` and the start-screen button, **only if**
`PC_CLIENT_PLAN.md` § 13.2 keeps rename and the coordinator has assigned the operation to a part;
otherwise the slot stays empty and this is recorded. Polish from the owner's first two sessions.
Then the review the phone and the server had: one pass over `Surface/` for "can one press become two
link calls" and "can a press reach the next pair", one grep for `RankMaster2.Ranking` /
`RankMaster2.Catalog` / `RankMaster2.Actions` / `Avalonia` inside `Surface/`.

---

## 6. Verification

### 6.1 What is proven on the build box

Unit tests (xunit, `net8.0`, no display):

- **KeyMap**: every row of § 1.1 including numpad; `Ctrl+S` is save and never skip; `S` alone is
  skip; `Shift+→` is still vote right; unknown keys → `None`; start-screen map differs from the
  compare map exactly as § 1.1 says.
- **ActionGate**: busy drops; not-ready drops per pane state (a `Gone` left pane accepts `1` and
  refuses `←`, `→`, `4`, `S`); a held key is one accept until `KeyUp`; a second key while the first is
  held is still subject to the arrival guard; nothing accepted within 200 ms of arrival on a fake
  clock; accepted at 201 ms; a token mismatch drops.
- **RankCoordinator** with the fake link: one accepted vote → exactly one `Vote` call with the
  displayed token; ten presses during one in-flight call → still one; cue then send, `Esc` during cue
  → zero calls and `QuitRequested`; Adopted with a foreign `lastAction` → no toast; Adopted with our
  request's id → the file-action toast; every § 3.5 row → the specified toast text, start-screen
  sentence, or nothing; discard → `Stop`, `Release` awaited, `Release(id)` on the still source, then
  the link call, in order; `Release` that never completes → link call at 2 s on the fake clock.
- **Undo**: Applied undo of each `undoneType` → the exact toast, with and without a differing
  `restoredId`; `undoAvailable: false` → no link call, no toast, busy never set; `nothing_to_undo` →
  silent; held `Ctrl+Z` → one call; from the start screen while exhausted → call, and the compare
  screen reopens iff the result is `ranking`.
- **PaneState**: every transition of § 3.2; late deliveries for a previous generation dropped; a
  preview after the full pass dropped; id-unchanged reuse keeps state and pixels across a snapshot; a
  D failure for another path ignored; `sizeBytes: null` → `Gone` before any request is made; still
  ring appears at 300 ms and not at 299 on the fake clock; "Starting video…" at 1 s while engine
  `Starting`, never while `Ready`.
- **Notices**: the whole § 3.4 and § 3.5 tables as data-driven tests.
- **StartModel / LastFolderStore**: a stored path to a missing directory → no Resume; Resume focused
  when shown; open in progress disables both; each open refusal → its sentence; status line per link
  state.

Headless Avalonia tests (`Avalonia.Headless.XUnit`): pane widths equal and full height at three window
sizes; `→` at the `TopLevel` reaches the model with a focused action button and with nothing focused;
Space and Enter do nothing on the compare screen; `F1` toggles the sheet and `Esc` still raises
`QuitRequested` with the sheet open; the sheet's rows equal `KeyMap`'s table; the strip renders
`cues.Count` balls in order; the cue animation completes and every animated property is at rest
afterwards; the toast disappears at `ToastMs`; the late line appears at 300 ms.

### 6.2 What only the owner's PC can show

Reported as unverified until he has seen it:

- True fullscreen over the taskbar, per-monitor DPI, and that the pane pixel size matches what is
  drawn — all part A's window, seen through E's panes.
- That the tunnel key handling wins over Avalonia's on Windows (it does in the headless platform;
  the Win32 platform has its own input path).
- The feel of the cue at 100 ms on his monitor, the ring's 300 ms grace, the 200 ms arrival guard
  being imperceptible.
- Video frames painting at pane size without stutter — D's, seen through E.
- The native folder picker, and `Esc` inside it closing the dialog and not the app.
- The cursor hiding and returning.
- Fonts: the system UI font on Windows 11 is Segoe UI; the monospace fallback for the key captions
  and numbers is whatever Avalonia resolves for `Consolas, monospace` there.

### 6.3 What E must never do — enforced by test or review

1. Never call the link twice for one accepted intent (test: fake link call count).
2. Never queue an intent (test: presses during busy → zero calls afterwards).
3. Never send a token other than the one displayed at accept time (test).
4. Never treat a C or D failure as permission to change server state — the only state change is the
   owner's discard key (review + test: `Undecodable` sends nothing).
5. Never hold a video surface across a discard/special of its side (test: order of calls).
6. Never reference `RankMaster2.Ranking`, `Catalog`, `Actions`, or `Avalonia` from `Surface/` (grep
   in E6).
7. Never show μ, σ, a score or a tier name (review: the snapshot has none, and E builds no other
   numbers than `progressPercent`, `unranked`, `sessionVotes`).
8. Never write to disk except `last-folder.txt` (review).

---

## 7. Acceptance gate

Part E is done when the owner, at his PC, with parts A–D installed and the old `RankMaster2.exe`
never launched, does the following on a real folder and each line is true:

1. **Sits down and starts.** Double-clicks the exe; the start screen shows Resume with his folder's
   name and the version beside the title. Presses Enter. Both panes are painted; the info card's
   folder, unranked and session numbers match what the phone shows for the same folder opened
   afterwards.
2. **Ranks 200 pairs without touching the mouse.** `←`, `→`, `↓`, `S`, `1`, `2`, `4`, `5`, `Ctrl+Z`,
   `Ctrl+S`, in whatever mix he likes. `SESSION` on the card equals the number of votes he cast minus
   the votes he took back. `rankmaster_db.json` afterwards agrees with the phone.
3. **Holds `→` down for two seconds.** Exactly one vote. The pair after it is untouched until he
   releases and presses again.
4. **Double-clicks a pane.** Exactly one vote.
5. **Presses `→` and then `←` as fast as he can.** One vote; the second press is dropped; the new pair
   waits for him.
6. **Discards a still, then presses `Ctrl+Z`.** The toast says "Discarded *name*", the file is in
   `discarded\`; then "Discard taken back" (or "… as *name (2).jpg*" if the name was taken), the file
   is back, and a fresh pair is on screen.
7. **Votes, then presses `Ctrl+Z`.** The same two pictures come back with no flicker, the toast says
   "Vote taken back", the strip is one ball shorter, `SESSION` is one lower. Presses `Ctrl+Z` again,
   and holds it: nothing happens, nothing appears, nothing needs dismissing.
8. **Opens his video folder.** The first pair shows rings with a percent; if the engine takes more
   than a second, "Starting video…" appears under them and goes when the frames come. Both play,
   looping, muted. Presses `1` on a playing video: the file moves on the first press; the toast names
   it.
9. **Exits the tray from its menu mid-session.** Within a second the start screen says the server
   closed the folder and that everything up to the last answered action is saved; Resume works; the
   JSON shows no double-counted vote.
10. **Puts a truncated JPEG in a folder and opens it.** When it comes up, the pane says it cannot be
    shown and which key discards it; nothing is skipped for him. `1` moves it.
11. **Deletes a file from the folder while it is on screen (Explorer).** The pane says it is gone;
    `1` removes it; `Ctrl+Z` afterwards is silent because there is nothing to put back.
12. **Presses `Esc` — during a cue, during a slow save, with the help open.** The program is gone
    within a second every time. Nothing was written by the exit.
13. **Hovers `?`, presses `F1`.** The sheet shows the § 1.1 keys, "Undo last action" among them, and
    the version in the footer.
14. **Starts the program with the tray not running and its exe moved away.** The start screen says the
    server is not running and what to start, in one line, with no dialog. Puts the exe back, presses
    Resume: it works.
15. **Discards a three-file folder down to one.** The start screen says no pair is left and that
    `Ctrl+Z` takes back the last one; `Ctrl+Z` puts the file back and the compare screen returns.

Points 2, 3, 4 and 7 are the ones that matter: 2 is the product, 3–4 are why the gate exists, and 7 is
the owner's own decision seen working.

---

## 8. Risks

| Risk | Mitigation |
|---|---|
| The frozen seams lack something § 2.3 asks for (result discriminator, request handle, Missing vs Undecodable, engine state, `Release` as a task) | E0 produces the table before any code; each gap is a one-line request to the coordinator, and E1–E3 proceed against fakes that have the capability so the surface is not shaped by the gap |
| Avalonia's Win32 input path lets a key through before the tunnel handler | belt and braces: `Focusable="False"` on every compare-screen control and `TabNavigation="None"`, so even an unhandled arrow key has nowhere to go; listed in § 6.2 as unverified until seen |
| The 200 ms arrival guard is felt by a very fast owner | it is one constant; the acceptance session will say. Below 150 ms it would still stop a double-click; below 80 ms it would not |
| The still ring's 300 ms grace hides a genuinely slow disk for a third of a second | the filename is on screen from the first millisecond, so the pane is never blank; the ring is late, not absent |
| Pane reuse keeps stale pixels for a file whose bytes changed under the same name | the snapshot's `mediaVersion` changes; E re-requests when `mediaVersion` differs even if `id` does not — one comparison in § 3.2's reuse rule |
| `Release()` never completes on Windows for this LibVLC build | 2 s cap, then send; `move_failed` becomes a toast that tells him to press again; the old app's `WaitUntilUnlocked` is the evidence it does complete |
| The help overlay is drawn under a video frame's invalidation | z-order is fixed in XAML; the frame invalidates the pane's `Image` only |
| Two toasts in quick succession (discard, then `move_failed` on the next) | one toast slot; a new toast replaces the text and restarts the timer; the strip is never covered |
| Rename is never assigned to a part | the slot stays empty; nothing in E depends on it |

---

## 9. What could not be determined without Windows, or without the seams

- Every item of § 6.2.
- Whether D will expose an engine state, or E must infer one from time (§ 2.3). The inference is
  designed but is the worse of the two.
- Whether C delivers an Avalonia bitmap or raw pixels; `Surface/` is written not to care.
- Whether A or B owns server launching and therefore the status line's source. E consumes one
  observable either way.
- Whether Resume-by-Enter, the cursor hiding, and the late-action line feel right at a real desk.
  Each is one constant or one control to remove.

---

## 10. Decisions for the owner

The `Ctrl+Z` question is closed. Three small ones remain, each with a default that this plan builds:

1. **The info card** — keep it as on the old screen (default), or thin it to a percent alone? The
   phone's reasoning for a hairline does not apply at a desk, but it is his corner of the screen.
2. **Enter resumes** on the start screen (default: yes). It is focus, not a new key, and Resume still
   waits to be asked.
3. **Skip on `↓`/`S`** — `PC_CLIENT_PLAN.md` § 13.5 already asks; E builds it (default: keep). If it
   goes, it is two rows in `KeyMap` and one in the help sheet.
