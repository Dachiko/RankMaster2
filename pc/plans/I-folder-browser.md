# I — In-app folder browser (replaces the Windows folder picker)

Status: **built and deployed as 3.4.0 (2026-10-04)**; visual check skipped by Mike; pushed 2026-10-04. See the S2 notes in § 6.

Mike's request (2026-10-04): navigate folders inside the app instead of the Windows folder dialog —
"simple, I don't need loads of information. I need to see a clean path": the full path on top
(`C:\images\garden`) with the folder he is in highlighted, and the folders inside it under it.
This reverses `PC_CLIENT_PLAN.md` § 2.1 ("No folder browser over `/libraries/*`") and the matching
line in `pc/plans/E-ranking-surface.md` § non-goals; both get a pointer here when this ships.

Visual spec: `C:\ai\projects\rankmaster\mockups\house-style\index.html`, tab **5 · Folders**
(frames 01–05). Style rules: plan H (version C "Quiet") and `C:\ai\workflows\app-design\DESIGN.md`.

## 1. Vocabulary

| Name | Means |
|---|---|
| **browser** | the new screen, `AppScreen.Browse`, `BrowseView` + `BrowseModel` |
| **mode** | why the browser is open: **Rank** (from `O`, start or compare screen) or **Rename** (from `R`) |
| **path** | the line on top: the full path of the folder being shown, in Doto uppercase |
| **here** | the last segment of the path = the folder being shown; ink, with the 36×3 red bar under it |
| **rows** | the names of the folders inside *here* — nothing else on a row |
| **roots** | `GET /libraries/roots` (SERVER_SPEC § 10.14); shown as the path `THIS PC` with drive rows |
| **listing** | `GET /libraries/browse?path=…` (§ 10.15) |
| **openable** | a folder the mode can act on: Rank → `rankable`; Rename → `rankable && hasDatabase` |

## 2. Decisions

### 2.1 Settled by Mike (2026-10-04)

1. **Simple.** Path on top, folder names under it. No counts, no second lines, no markers, no detail
   pane, no search box.

### 2.2 Proposed defaults (from the mockup; Mike may overrule)

1. One browser for both modes; the Windows picker goes entirely (start screen `O`, `R`, compare
   screen `O`).
2. **Path:** earlier segments and the `\` separators in `faint`, *here* in ink with the red bar.
   A path too long for the column drops its earliest segments behind `…\` (here always shows).
3. **Rows:** Inter Medium 16, natural sort order (port the phone's `ui/review/NaturalOrder.kt`).
   A folder that isn't openable is dimmed (≈ 35 %) — no text says why. Hidden and system folders
   are left out by name (`.` or `$` prefix, `System Volume Information`, `$RECYCLE.BIN`). An empty
   folder shows one faint caps line, `NO FOLDERS INSIDE`.
4. **Ranking the folder you are in:** ↑ from the first row moves the selection onto *here* in the
   path (it turns ink); Enter acts on it. With no rows, *here* is selected from the start.
5. **Keys:** ↑/↓ move (onto *here* above the first row) · PageUp/PageDown/Home/End · **→ goes into**
   the selected folder · **← / Backspace goes up** (from a drive to `THIS PC`) · **Enter:** on an
   openable folder ranks it (Rank) or asks the rename question (Rename); on a folder that isn't
   openable it goes into it · **typing finds:** the typed letters show in red after the path,
   matching folders glide to the top, the rest stay below dimmed, Enter/↓ take the best match,
   Backspace deletes a letter, Esc clears · **Esc leaves** (with nothing typed): back to the start
   screen, or back to the pair if opened from the compare screen (the session stays open) · F1 keys.
6. **Where it starts:** in the last folder's parent with the last folder selected; with no last
   folder, at `THIS PC`. From the compare screen, the open folder's parent with it selected.
7. **Mouse:** click selects; double-click = Enter; a click on a path segment goes there; wheel
   scrolls. Rows are whole rows; no scrollbar; the list scrolls to keep the selection in view.
8. **Bottom line:** faint caps key hints, `ENTER RANK · → INTO · ← UP · ESC BACK` (`ENTER RENAME`
   in Rename mode); the corner says `RANK MASTER 3 / OPEN` or `/ RENAME` (accent).
9. **Loading:** one listing call per folder. The server's counts are needed only for *openable*, so
   ask with `counts=true`; while it is under way a busy line runs under the path. A response for a
   folder already left is dropped (sequence number). Rows never move when it lands.
10. **Failures** (folder gone, no access, server down): one accent status line at the bottom; the
    browser stays where it was. Never an OS dialog.

## 3. What stays

The compare screen, `RankCoordinator`'s open/rename transitions after a folder is chosen
(`OpenFolderAsync`, `BeginRenameConfirm`), the server and its API (both routes exist; the phone uses
them), the rename ink card, the database.

## 4. Stages

| Stage | Who | Branch | Depends on | Status |
|---|---|---|---|---|
| S0 contract: link signatures, wire records, fakes, `AppScreen.Browse` | orchestrator | `master` | Mike's go | done |
| S1 link: roots + browse over the pinned client | worker **L** | `browse/link` | S0 | done 105c79f (reviewed) |
| S2 browser: model, view, keys, coordinator wiring | worker **U** | `browse/ui` | S0 | done 554786b (reviewed) |
| S3 merge, docs, 3.4.0, deploy | orchestrator | `master` | S1, S2 | done, pushed 2026-10-04 |
| S4 visual check (targeted) | orchestrator | — | S3 + Mike's yes | skipped — Mike: "no, looks good" (2026-10-04) |

S1 and S2 run in parallel in their own worktrees (`..\wt-browse-link`, `..\wt-browse-ui`).

### S0 — orchestrator (so S1 and S2 never touch the same file)

- Wire records (new `Link/Wire/Libraries.cs`): `LibraryRoot(Path, Label, Kind, Available)`,
  `FolderListing(Path, Parent?, Entries)`, `FolderEntry(Name, Path, Rankable?, HasDatabase,
  Accessible)`; results `RootsResult` / `ListingResult` = `Ok(...)` | `Failed(Failure)`, the same
  shape as the link's other results. (Counts and byte sizes are not carried: § 2.1.)
- `ISessionLink`: `Task<RootsResult> GetRootsAsync(CancellationToken)`,
  `Task<ListingResult> BrowseAsync(string path, CancellationToken)` (always `counts=true`).
- `SessionLink` (+ any wrapper such as `WakingSessionLink`): stubs that throw
  `NotImplementedException`; every test fake of `ISessionLink` gets working in-memory versions.
- `AppScreen.Browse` added; nothing routes to it yet.
- Gate: both solutions build; tests at baseline.

### S1 — worker L (link)

- **May change:** `pc/src/RankMaster2.Pc/Link/**` (implementations only; S0's signatures and records
  are fixed), `pc/src/RankMaster2.Pc/App/WakingSessionLink.cs` if it forwards calls, tests in
  `pc/tests/RankMaster2.Pc.Link.Tests/`.
- **Must not touch:** `Ui/**`, `src/**` (server), `android/**`, docs, versions.
- Behaviour: same transport, pinning, credential and retry rules as the link's other GETs; JSON per
  SERVER_SPEC § 10.14–10.15 (an absent `rankable` = `null`); errors mapped to the link's `Failure`.
- **Gate:** `dotnet test pc\RankMaster2.Pc.sln` at baseline; new wire-parsing tests (pure JSON, no
  TLS — the Link.Tests TLS failures on Windows are baseline) pass; diff within allowed paths.

### S2 — worker U (browser UI)

- **May change:** `Ui/Surface/` (new `BrowseModel`, `NaturalOrder`, `KeyMap.MapBrowse`, `HelpRows`,
  `RankCoordinator`: O/R route to the browser, Enter/Esc from it, Esc back to the pair),
  `Ui/Views/` (new `BrowseView`; `UiRoot` routing; remove the picker code from `StartView`),
  `pc/tests/RankMaster2.Pc.Tests/Ui/**`.
- **Must not touch:** `Link/**` (use the S0 interface and fakes), compare-screen views, `src/**`,
  `android/**`, docs, versions.
- **Gate:** tests at baseline plus new ones: `MapBrowse`; `BrowseModel` (start location; up from a
  drive to `THIS PC`; ↑ from the first row selects *here*; Enter on openable vs not; typed filter
  reorders and Esc clears; stale-response drop; hidden-name filter; natural order; long-path
  elision keeps *here*); headless `UiRoot`: O opens the browser in Rank mode, R in Rename mode, Esc
  returns to the right screen; the palette test covers `BrowseView`. No window opened.

### S3 — orchestrator

Merge, re-run both suites, docs (`PC_CLIENT_PLAN.md` § 2.1 and `E` non-goals pointers,
`SERVER_RUNNING.md` if it mentions the picker, `NOTES.md`), version **3.4.0** (minor: new screen),
`CHANGELOG.md`, `deploy.ps1 -SkipPull`, tag. Push only with Mike's OK.

## 5. Control state

This file's § 4 table is the source of truth (orchestrator writes it). Work in progress lives on the
`browse/*` branches; resume by reading this file and `git log master..browse/*`.

## 6. As built (S1/S2 reports, 2026-10-04)

- The link is single-flight (`Gated`): the browser keeps one listing in flight and fires only the
  latest target when it returns; a superseded call is cancelled and its `client_cancelled` failure is
  silent. Enter cancels a pending listing and waits for it before `OpenAsync`.
- Enter in Rank mode stays in the browser (busy line) until the compare screen opens; a refused
  folder shows the accent status line and the browser stays.
- Typing: letters arrive as text input; the best match (name starting with the letters first) is
  selected; with no match nothing is selected and Enter does nothing; Esc clears the letters and
  keeps the selection; Backspace with nothing typed goes up.
- Enter on *here* acts unless the listing it came from said it isn't openable, or at THIS PC. An
  unknown `rankable` counts as not openable (dimmed, still enterable if accessible).
- If the last folder's parent can't be read, the browser falls back to THIS PC with the failure as
  the status line.
- Rows are one custom control (`BrowseList`): whole rows, no scrollbar, virtual, glide ~180 ms on a
  filter reorder; a new listing lands still. Hover uses `paper`.
- F1 shows the browser's own keys (MOVE / FOLDERS / FIND, `HelpRows.BrowsePage`).
- The picker machinery is gone (`OpenFolderRequested`, `RenameRequested`, `BeginDialog`/`EndDialog`,
  `DialogOpen`, three picker-only tests).
- To look at in the visual check: column centring, path line (faint segments, red bar, *here*
  inverted when selected), deep-path `…\` elision, row alignment and dimming, typing, busy line,
  THIS PC, `NO FOLDERS INSIDE`, status line, F1, mouse, Esc back to the same pair.
