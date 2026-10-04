# H — House-style redesign of the PC side (all screens except compare)

Status: **approved 2026-10-04, building** (see § 5 for each stage).

The PC client's start screen, rename flow and F1 keys page, and the tray's menu, icon and pairing
window, move to Mike's house style (`C:\ai\workflows\app-design\DESIGN.md`, skill
`app-design-style`) in its **version C "Quiet"** form. The compare screen and everything that floats
on it are not touched. The phone app is a later round.

The visual spec is the mockup: `C:\ai\projects\rankmaster\mockups\house-style\index.html`, tab
**3 · C Quiet** (open it in a browser; keys 0–4 switch tabs). Where this file and the mockup
disagree, this file wins (§ 2 records the decisions made after the mockup).

## 1. Vocabulary (use these names everywhere: code, commits, reports)

| Name | Means |
|---|---|
| **start screen** | `StartView`: what shows when no folder is being ranked |
| **compare screen** | `RankView` and its overlays (`PaneControl`, `InfoCard`, `ActionBar`, `MatchStrip`, `Toast`, `LateActionLine`, `HelpSheet`). **Out of scope, not one line changes** |
| **hero** | the big Doto line in the middle of the start screen: the last folder's name, or `RANK MASTER` when there is none |
| **pills** | the key captions under the hero (`ENTER RESUME`, `O OPEN`, `R RENAME`); they are the start screen's buttons |
| **ink card** | the dark card (ink `#141414`, faint 7 px dot grid, radius 14) that shows the rename question and the rename progress over the faded start screen |
| **keys page** | the start screen's F1 page (three columns of keys) |
| **QR card** | the tray's pairing window, rebuilt: a borderless ink card holding only the QR code |
| **two-pane icon** | mockup B's mark: two upright panes, the left one filled; on the tray the left pane is red while a folder is open |
| **palette** | the house tokens: `bg #FCFCFA`, `paper #F1F2EF`, `ink #141414`, `mid #555555`, `dim #737373`, `faint #ABABA7`, `line #DEDEDE`, `rule #8C8C88`, `accent #E31B23`, `on-ink #FCFCFA`. No other colour in new or changed files |
| **baseline failures** | the Windows-only test failures listed in `NOTES.md` § Tests; they do not count against a stage |

## 2. Decisions

### 2.1 Settled by Mike (2026-10-04)

1. Version **C "Quiet"** for every screen in scope.
2. The app and tray icon is mockup **B's two-pane icon**.
3. Tray menu: **Show QR** (bold, the default; a single left click on the icon does the same) and
   **Exit**. Nothing else: no address line, no folder line, no Copy fingerprint, no Open data folder.
4. The QR card shows **only the QR code**: no six-digit code, no countdown, no hint text, no buttons.
5. The phone app waits for a later round.

### 2.2 Settled by Mike after the plan (2026-10-04)

1. **No six-digit code anywhere on the PC.** His phone's camera works; the phone's "type the code"
   path stays in the phone app, unused.
2. **No warnings at all.** He is the only user. The "certificate changed" balloon, the "device list
   unreadable" menu line and every other tray balloon go. The server's log still records them.
3. **The QR never shows as expired.** The server's 5-minute codes stay as they are (no change to the
   server or its security rules); the QR card asks for a fresh code shortly before the current one
   runs out (~10 s), so the QR on screen is always valid. When a phone pairs, the card closes by
   itself. Esc closes it; right-click → Close (no ✕). While a code is fetched, a red busy line runs
   where the QR goes. If no code can be had, the QR's place shows one accent caps line (e.g.
   `NO CODE · SERVER NOT ANSWERING`) and the card stays until Esc; no OS message box, no balloon.
4. **The compare screen's F1 sheet stays as it is.** Only the start screen gets the new keys page.
5. Build it: S0–S2 start now.

Plan default (not asked): **opening a folder has no cancel** (as today): during opening the pills
dim and a busy line runs under the hero; Esc still quits.

## 3. What each screen becomes (from mockup C)

All sizes are logical px of the fullscreen window. Fonts: **Doto Rounded Black** (the modified copy
in `C:\ai\workflows\app-design\fonts\Doto_Rounded-Black.ttf`) for the hero, big numbers and the
keys page title; **Inter** for everything else (the client already ships `Avalonia.Fonts.Inter`).
Caps labels: Inter Medium 10.5, uppercase, letter-spacing 0.16 em.

### 3.1 Start screen (`StartView`)

- Background `bg`. No card, no gradient.
- Top-left corner: caps `RANK MASTER 3` in `dim`. Top-right: a 7 px status dot + caps `SERVER`
  (dot ink = connected; dot accent + caps `NO SERVER` in accent = not), then ` · F1 KEYS` in `faint`
  (a click opens the keys page). 26 px from the top, 34 px from the sides.
- Centre column, centred both ways:
  - caps label `RESUME` (`dim`), 14 px above the hero;
  - **hero**: last folder's name, Doto 64, uppercase; with no last folder: `RANK MASTER`;
  - the folder's full path, Inter 12.5 `faint`, 10 px under the hero (hidden with no last folder);
  - **pills**, 34 px under: `ENTER RESUME`, `O OPEN`, `R RENAME` (no last folder: `O OPEN`,
    `R RENAME`). Pill = caps on a 1.5 px ink outline, radius 13, padding 7×14; the focused pill is
    filled ink with on-ink text. Resume is focused when shown (as today); otherwise Open.
- Bottom centre, 34 px up: the **status line** (Inter 12, `mid`; errors in `accent`).
- States:
  - **opening** — pills at 35 % opacity, a red busy line (2 px; red dash sweeping ~0.9 s along a
    1 px `line` hairline) as wide as the hero, 12 px under it. No "Opening…" text.
  - **exhausted** — the label above the hero becomes accent caps `● NO PAIR LEFT`; the first pill
    becomes `CTRL+Z TAKE BACK` (focused) when undo is available.
  - **open refused / any start-screen error** — status line in accent, short: `Nothing to rank here ·
    D:\Downloads has no photos or videos` (title · detail, no trailing full stop).
  - **no server** — the corner dot and caps turn accent (`NO SERVER`) and the status line says
    `Server not answering` (+ the failure's detail if it has one). Nothing else moves.
  - **rename finished** — status line in `mid` with an ink dot: `Renamed 1 284 files · Iceland 2026`;
    cancelled / failed: the same line in accent with the shortened sentence (§ 3.2).

### 3.2 Rename (`RenameView` → ink card over the start screen)

`AppScreen.Rename` stays; what it shows changes: the start screen stays drawn underneath behind a
`bg` veil at 78 %, and the **ink card** (560 wide, padding 22×30, radius 14, shadow, faint 7 px dot
grid of `rgba(255,255,255,.07)`) sits in the centre. The Windows folder picker that comes first is
unchanged.

- **Confirming:** caps `RENAME BY RANK` in accent; the folder name in Doto 56, uppercase (the
  mockup's "1 284 files" is not possible: `RenameModel` has no total before `POST /session/rename`,
  and no server call is added for it); one line Inter 13.5 at 75 % on-ink:
  `000001-….jpg · ratings stay`; dotted rule; footer caps `ENTER RENAME · ESC KEEP` (on-ink).
  Enter = rename, Esc = keep.
- **Running:** caps `● RENAMING · <folder>` (pulsing red dot); Doto 56 percent (`37` + Inter
  SemiBold 22 `%`), **never "done / total"**; a row of 60 dots (6 px, gap 4) that fill red from the
  left with the percent (unfilled `#3A3A3A`); dotted rule; footer `ESC CANCEL` and, on the right,
  `PREPARE · RENAME · SAVE` with the current phase in accent (`reuniting` shows as `SAVE`).
  Cancel requested: the footer's left caption becomes `CANCELLING` and a busy line replaces the dots.
- Terminal states return to the start screen with the status line from § 3.1. New sentences
  (`Notices.ForRenameTerminal`): succeeded `Renamed {n} files · {folder name}`; cancelled `Rename
  stopped · what was done stays, ratings stay with their files`; failed and reunited `Rename failed ·
  nothing changed`; failed, not reunited `Rename failed · open the folder again to retry ·
  {journal}`.

### 3.3 Keys page (start screen F1)

Replaces the start screen's use of `HelpSheet` (the compare screen keeps `HelpSheet`, § 2.2.4).
Covers the start screen: corner caps `RANK MASTER 3 / KEYS` left, version (`v3.3.0`) right in
`faint`. Centre: 820 px wide, three columns, each headed by a red number + ink caps (`01 RANK`,
`02 START`, `03 EVERYWHERE`), each key an ink-outlined chip (Inter SemiBold 11.5, 1 px ink, radius 4)
+ action text in `mid` 13.5. Bottom: caps `ANY KEY CLOSES` in `faint`. F1 or any key closes.
Rows come from `HelpRows` (Compare rows as today + new `Start` rows), so the existing
`HelpRowsTests` pattern keeps keys and map in step.

### 3.4 Keys on the start screen

| Key | Does | New? |
|---|---|---|
| Enter / Space | the focused pill (Avalonia's own button behaviour, as today) | — |
| ← / → / Tab | move focus between pills | ← → new |
| O | open a folder (picker) | — |
| **R** | rename by rank (picker → ink card) | **new** |
| Ctrl+Z | undo, only while an exhausted session is open | — |
| F1 | keys page | changed look |
| Esc | quit (keys page open: close it) | — |

### 3.5 Tray (`src/RankMaster2.Tray`)

- **Icon** (drawn in code in `TrayArt`, two states): two upright rounded panes, 1.4 px `on-ink`
  outline on the dark taskbar; left pane filled `on-ink` when idle, filled `accent` while a folder is
  open (the 1 s `RefreshSession` poll already knows). Executable icons: `src/RankMaster2.Tray` and
  `pc/src/RankMaster2.Pc/App/icon.ico` get the two-pane mark on a `bg` tile with ink panes
  (16–256 px, generated once by the orchestrator, § 5 stage S0).
- **Menu:** `ContextMenuStrip` with a custom renderer in the house look (bg, 1.5 px ink outline,
  radius 10, Inter 13; hover row ink with on-ink text): **Show QR** (bold) and **Exit**, a dotted
  rule between. Left click on the icon = Show QR (today it is double-click). Tooltip:
  `Rank Master 3`.
- **QR card** (`PairingForm` rewritten): borderless, top-most, centred, no taskbar button;
  ink card (radius 14, dot grid as § 3.2), padding 24; the QR on a `bg` chip (radius 8, padding 10),
  ~300 px; nothing else on it. Drag anywhere moves it; Esc closes; right-click → Close. Lifecycle: § 2.2.3. The `PairingChannel` protocol and file layout are unchanged.
- No warnings, no balloons (§ 2.2.2).

## 4. What stays exactly as it is

- Every file of the compare screen (list in § 1) and `Theme.axaml`'s existing keys (new keys may be
  added; none renamed or recoloured).
- `RankCoordinator`'s transitions, the `ISessionLink` calls, `AppScreen`, the server and its API,
  `PairingChannel`, the database, the phone app.
- `SPEC.md` (it describes the frozen Rank Master 2 app).

## 5. Stages

Each stage is one commit (or a few) on its own branch, reviewable alone. Workers run in their own
git worktree so builds never collide. The orchestrator (the main session) merges into `master`.

| Stage | Who | Branch | Depends on | Status |
|---|---|---|---|---|
| S0 icons + fonts + plan commit | orchestrator | `master` | Mike's OK | done |
| S1 tray: icon, menu, QR card | worker **T** | `house/tray` | S0 | todo |
| S2 PC: start screen, ink card, keys page, R key (static) | worker **P** | `house/pc` | S0 | todo |
| S3 PC: motion | worker **P** | `house/pc` | S2 | todo |
| S4 docs, version 3.3.0, changelog, merge, deploy | orchestrator | `master` | S1, S3 | todo |
| S5 visual check (targeted) | orchestrator | — | S4 + Mike's yes | todo |

S1 and S2 run **in parallel** (disjoint files). S3 follows S2 in the same worktree.

### S0 — orchestrator

- Generate the two-pane `.ico` files (Python + Pillow, script kept at `pc/tools/make-pane-icon.py`).
- Copy `Doto_Rounded-Black.ttf` + `Doto-OFL.txt` into `pc/src/RankMaster2.Pc/Ui/Fonts/`.
- Commit this plan. Create the worktrees:
  `git worktree add ../wt-house-tray -b house/tray` and `git worktree add ../wt-house-pc -b house/pc`
  (siblings of `project`, i.e. `C:\utils\rank master 2\wt-house-*`).

### S1 — worker T (tray)

- **Inputs:** this file § 1–4 (esp. § 3.5, § 2.2.2–2.2.3); mockup tab C frames 08–09 (QR card and tray; ignore the
  countdown, code and dots there: § 2.1.4 overrides); worktree `wt-house-tray`.
- **May change:** `src/RankMaster2.Tray/TrayApp.cs`, `TrayArt.cs`, `PairingForm.cs`, new files in
  `src/RankMaster2.Tray/` (e.g. a menu renderer), `RankMaster2.Tray.csproj` (icon, font files).
- **Must not touch:** `pc/**`, `src/RankMaster2.Server/**` (incl. `PairingChannel` semantics — the
  tray's `PairingChannel.cs` may be read, not changed), `android/**`, `Directory.Build.props`,
  `CHANGELOG.md`, any doc.
- **Output:** commits on `house/tray`; a final report: files changed, gate results, anything left
  open.
- **Gate G1:** `dotnet build src\RankMaster2.Tray -c Release` has no new warnings; `dotnet test
  RankMaster2.sln` shows only baseline failures; `git diff --name-only master...house/tray` lists
  only allowed paths; a search of the changed files finds no colour outside the palette (plus
  pure white for the QR's quiet zone if needed). No window is opened to check the look.

### S2 — worker P (PC, static)

- **Inputs:** this file § 1–4 (esp. § 3.1–3.4, § 2.2.4 and the plan default under § 2.2); mockup tab C frames 01–07;
  `C:\ai\workflows\app-design\DESIGN.md` § 2–3 for tokens and components; worktree `wt-house-pc`.
- **May change:** `pc/src/RankMaster2.Pc/Ui/Views/StartView.*`, `RenameView.*`, new view files
  (keys page, pill style, busy line, house resource dictionary), `Theme.axaml` (add keys only),
  `UiRoot.axaml(.cs)` (R key routing, overlay hosting), `Ui/Surface/KeyMap.cs`, `Intent.cs` (add
  `RenameFolder`), `HelpRows.cs` (add `Start`), `StartModel.cs` (message kind + short text),
  `RenameModel.cs` (drop `ConfirmText` or shorten), `Notices.cs` (rename terminal sentences,
  start-screen failure text only), `RankCoordinator.cs` (only: start-key handling for R/arrows,
  `RenameRequested` event, start message kinds), `RankMaster2.Pc.csproj` (font resource), the
  matching tests under `pc/tests/RankMaster2.Pc.Tests/`.
- **Must not touch:** the compare screen files (§ 1), `Link/**`, `Stills/**`, `Video/**`, `App/**`
  except `App/icon.ico` already done in S0, `src/**`, `android/**`, `Directory.Build.props`,
  `CHANGELOG.md`, docs.
- **Output:** commits on `house/pc`; report as T.
- **Gate G2:** `dotnet test pc\RankMaster2.Pc.sln` shows only baseline failures, plus new passing
  tests for: `MapStart(R) == RenameFolder`; `HelpRows.Start` rows map through `MapStart`; `StartModel`
  exhausted/error/success kinds; `ForRenameTerminal` new sentences; a headless `UiRoot` test that R on
  the start screen raises the rename request and that F1 opens/closes the keys page; a **palette
  test** that scans the new/changed `.axaml` files for hex colours outside the palette. Diff limited
  to allowed paths. No window opened.

### S3 — worker P (motion)

Short, quiet, ~150 ms ease-out, every user-caused change visible (DESIGN.md § 4 "Motion"):
ink card fades in/out (150 ms); keys page fades; the hero **decodes in** when it changes at the
user's action (DESIGN.md "Autofill decodes in", ~0.4 s, red write head; port of `Decode.In` from
`C:\ai\workflows\app-design\wpf\Inline.cs` to an Avalonia control); the status line decodes in;
the percent rolls digit by digit; the red dots fill; any key ends a running animation. Nothing moves
by itself; layout never shifts. Same file limits and gate as S2 (tests for the decode timing logic
where it is pure code).

### S4 — orchestrator

1. Review each branch's diff against its contract; run G1/G2 again on `master` after merging
   (`house/tray`, then `house/pc`; rebase if needed).
2. Docs: `SERVER_RUNNING.md` (tray menu, left click, QR card, no code, no warnings);
   `pc/plans/E-ranking-surface.md` (one pointer line at the start screen rows: "superseded by H");
   `PC_CLIENT_PLAN.md` if it describes the start screen; `NOTES.md`.
3. Version **3.3.0** in `Directory.Build.props` (minor: new key, changed tray behaviour); one
   `CHANGELOG.md` entry; tag `v3.3.0`.
4. `powershell -File deploy.ps1 -SkipPull` (the uncommitted `deploy.ps1` edit is not ours; leave it).
5. Push `master` and the tag only after Mike's OK. Remove the worktrees.

### S5 — visual check (only on Mike's yes)

Targeted: start screen (with and without a last folder, no server), rename ink card (confirming,
running on a scratch copy of a small folder), keys page, tray menu, QR card. Close every window
after. Without a yes: skip and say so.

## 6. Control state (what survives a crash or a new session)

- **This file's § 5 table** is the source of truth for stage status; only the orchestrator edits it
  (todo → in progress → done + commit hash).
- Branches `house/tray`, `house/pc` and their worktrees hold work in progress; a worker's last commit
  is its checkpoint. A new session resumes by reading this file, then `git log master..house/*`.
- `NOTES.md` gets one line under State while H is in progress, and the full result at S4.
