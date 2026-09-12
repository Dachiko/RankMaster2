# Rank Master 2 — specification

This file is the source of truth. If code and this document disagree, the document wins until we change it on purpose. When behavior changes, update this file in the same change.

## What it is

A Windows 11 desktop app that ranks the photos **or** videos in one folder by pairwise comparison. You see two items, pick the better one (or skip). Ratings live in `rankmaster_db.json` in that folder so the library is portable.

Working title / assembly name: **RankMaster2**  
App version: `Directory.Build.props` (now **1.1.4**). Shown on the start screen next to the title (small label set high on its top-right corner) and in the help footer.  
Git repo: `C:\Utils\rank-master-2\project`. Runnable exe: `C:\Utils\rank-master-2\RankMaster2.exe` with `libvlc\` next to it (not in git).

## Stack

- .NET 8 (Windows), WPF
- Self-contained `RankMaster2.exe` with the native `libvlc\` folder shipped next to it. No installer, no Store, no browser shell
- No SQLite. No Chromium. No Python

## Non-goals

- Thumbnail grid or filmstrip
- Recursive scan
- HEIC / HEVC / RAW / TIFF / AVIF
- Tie vote, vote-undo, persisted match log
- In-app leaderboard
- Folder watcher
- Play / pause / seek / volume
- Images-only / videos-only filter UI
- Browser or dual-runtime mode
- Settings window
- “Professional Grade” confidence tiers

---

## Product rules

### Folder

- User picks one directory. Scan **top-level files only**.
- Ignore the JSON file itself, hidden/system files, and anything in a subfolder (`discarded/`, `special 1/`, `rankmaster_backup_*`, etc. stay out automatically).
- Need at least 2 eligible media files or stay on the start screen with an error.

### Media policy

Extensions:

- Stills: `jpg jpeg png gif bmp webp`
- Video: `mp4 webm mkv avi mov`

A folder is classified once at scan:

| Contents | Rank |
|---|---|
| Only stills | Stills |
| Only videos | Videos |
| Both | **Stills only** (mixed folder is an edge case) |

Unreadable / corrupt files are skipped for that pair. The session does not crash.

Honor EXIF orientation on stills.

### Screens

1. **Start** — Open folder (`O` or button). App version under the title. If a last-folder path exists and is still valid, show **Resume** and **Rename**. Resume does **not** auto-start; the user clicks it. Last folder path lives in app local data, not in the media folder. Resume does not restore a pair (pick a new one). Scan/merge JSON happens here with no separate loading screen.
2. **Ranking** — **True fullscreen** two-pane compare (covers the Windows taskbar / Start menu). No title bar.
3. **Renaming** — Progress for backup + two-phase rename.

There is no “Saved” screen. `Esc` **quits the process immediately**.

### Compare UI

- The window is borderless and fills the monitor, **including over the taskbar**.
- Two panes, each exactly half the screen. Each still or video is scaled with **uniform** aspect (`Stretch.Uniform`) so it is as large as possible on its half without cropping or stretching. Black letterbox if the aspect does not match the pane.
- Visual chrome matches RankMaster 1: zinc→black start screen, blue→emerald title, left info card (256px, top/left 16), help circle top-right, discard/star buttons top-center with `[1][4]` / `[5][2]`. No VS badge. No “Professional Grade” tier names — the confidence bar is a quiet percent only.
- **Filename** dim, top-left of the left pane (after the info card) and top-right of the right pane.
- Click a pane to vote for it. Keys also work.
- **Select cue (~100 ms):** winner gets a short white flash, emerald ring, and scale punch. The other pane is not dimmed (that left a leftover tint). Storyboard is stopped/`FillBehavior.Stop` so nothing sticks. Esc during the cue must not vote.
- Vote applies immediately after the cue. The new pair’s IDs come on screen at once even if pixels are not ready.
- **Still:** paint the first decodable image as soon as one exists; refine in place if a better decode arrives. No spinner if any pixels can be shown. Baseline JPEG/PNG on a slow disk may have no pixels until the read finishes — that is acceptable.
- **Video:** spinner until a frame can play, then autoplay, loop, muted, no controls. Two videos may play at once.
- Overlay (start + ranking): folder name, session vote count, unranked count (`matches == 0`). No tier names.
- **Match strip:** after each **vote**, a bottom-center pill of up to **10** 12px balls (session only, not in JSON). **Confirmation** (emerald): winner already had `μ ≥` loser. **Upset** (amber): winner had lower `μ`. Skip / discard / special add no ball. New folder starts empty. Hidden until the first vote.
- Help: `?` top-right on the window (start and ranking). Hover to show the cheat sheet; move away to hide it (unless F1 opened it). `F1` toggles it. `Esc` still quits the process. Toasts sit above the match strip so they do not cover it.

### Keys

| Key | Action |
|---|---|
| `←` / `→` | Vote left / right |
| `↓` or `S` | Skip (no rating change) |
| `1` / `2` | Move left / right to `discarded/` |
| `4` / `5` | Move left / right to `special 1/` |
| `O` | Open folder |
| `F1` | Toggle the help sheet (does not quit) |
| `Ctrl+S` | Save JSON now (redundant if the last action already saved) |
| `Ctrl+Z` | Undo **last move** only |
| `Esc` | **Quit immediately.** Cancel any in-flight select cue (do not vote). The pair on screen is unseen. Prior choices are already on disk. |

Ignore keys while a move is in flight.

### Persistence

File: `<folder>/rankmaster_db.json`

**Must load existing v1 files from Rank Master 1.** Do not invent a new schema. Write pretty-printed JSON (`Indent = true`).

```json
{
  "version": 1,
  "lastUpdated": 0,
  "images": {
    "DSC_0123.jpg": {
      "filename": "DSC_0123.jpg",
      "rating": { "mu": 25.0, "sigma": 8.333 },
      "matches": 0,
      "impressions": 0,
      "lastPlayed": 0
    }
  }
}
```

- Identity is the **filename** (the object key and the `filename` field must match).
- Scan loads **every** on-disk media file (stills and videos). Ranking eligibility is a filter (mixed folder → stills only). `Save` merges: keep rows for files still on disk even if this session did not rank them; drop only files that vanished.
- If `rankmaster_db.json` exists and does not parse, **refuse to start** and never overwrite it.
- Atomic save: write tmp, `Flush(true)`, `File.Replace` (or `Move` if the file is new). If save throws, roll back the in-memory vote.
- **Save on every choice** (vote or skip), atomically, before the next pair is requested. Not batched every N pairs. `Ctrl+S` is a manual extra save. `Esc` does not write (nothing new to write). Save also before rename.
- **Never write an empty database.** If the folder is missing, or lists no media while the session still holds records, `Save` throws instead of writing. Saving must not create the folder: recreating a vanished one turns a recoverable error into silent, total rating loss.
- `μ − 3σ` is computed, never stored.
- Keep writing `impressions` and `lastPlayed` for compatibility. **Do not use `impressions` to pick pairs.**

### File actions

- `discarded/` and `special 1/` created on demand under the chosen folder.
- Move with a unique name on collision (`name (2).ext`, …).
- Before move: `Pipeline.Release` the id, wait until the OS lock is gone, then `File.Move`.
- After move: drop the id from Ranking/Catalog, if the current or any queued pair contains it, throw that pair away and refill, then save.
- **Undo** restores only the most recent successful move (file back + catalog row). No undo stack. Vote undo does not exist.
- v1 only *creates* `special 1`. Do not create `special 2` until we ask.

### Rename by rank

From the start screen, when a folder is loaded/remembered:

1. Confirm.
2. Copy the whole top-level library + JSON into `rankmaster_backup_<yyyyMMdd_HHmmss>/`.
3. Phase 1: rename each media file to a unique temp name.
4. Phase 2: rename to `000001.ext`, `000002.ext`, … sorted by **`μ − 3σ` descending** (then filename for ties). Extension unchanged.
5. Rewrite JSON: every key and `filename` becomes the new name. Ratings stay with the same bytes.
6. Save. On failure, restore from the backup folder and report the error.

---

## Modules

Five modules. **Disk and cache are one module (Pipeline).** Ranking never sees a path. Pipeline never sees `μ`. Shell never opens a `FileStream` except through Pipeline.

```
Shell  →  Ranking.selectPair()           →  (id, id)
Shell  →  Pipeline.Show(id, id)          →  frames on screen
Shell  →  Pipeline.Warm(id, id, …)       →  queue
Shell  →  Ranking.Vote / Skip
Shell  →  Catalog.Save()
Actions.Move → Pipeline.Release → disk move → Catalog → Ranking.Drop
```

### Shared types (`RankMaster2.Core`)

```
MediaId        // filename, e.g. "DSC_0123.jpg"
MediaKind      // Still | Video
Rating         // Mu, Sigma
MediaRecord    // Id, Kind, Rating, Matches, Impressions, LastPlayed
Pair           // Left: MediaId, Right: MediaId
MatchCue       // Confirmation | Upset (session strip only)
```

`MediaId` is the filename. No extra GUID.

### Ranking (`RankMaster2.Ranking`) — no WPF, no media bytes

Xbox TrueSkill 1v1 defaults:

- `InitialMu = 25`
- `InitialSigma = 8.333`
- `Beta = 4.167`
- `Tau = 0.083`
- `TargetSigma = 1.8` (progress math only; not a picker input)

**Update (win/loss only):**

```
c² = 2β² + σw² + σl²
c  = sqrt(c²)
t  = (μw − μl) / c
v  = pdf(t) / cdf(t)          // clamp cdf to a small epsilon so v/w stay finite
w  = v * (v + t)
μw' = μw + (σw² / c) * v
μl' = μl − (σl² / c) * v
σw'² = σw² * (1 − (σw² / c²) * w)
σl'² = σl² * (1 − (σl² / c²) * w)
then σ² += τ²
```

Skip: no rating change, no `matches++`.

`impressions++` on both ids only when the user **makes a choice** (vote or skip). Merely displaying a pair, prefetching it, or quitting with `Esc` does **not** increment impressions. The pair on screen at `Esc` is unseen.

`matches++` and `lastPlayed = now` only on vote.

**Conservative score** (rename): `μ − 3σ`.

**Match quality** (picker only):

```
c² = 2β² + σ1² + σ2²
q  = sqrt(2β² / c²) * exp(−Δμ² / (2c²))
```

**`SelectNextPair(records, recentShownIds, reservedIds)`**

`recentShownIds`: last ~30 ids that received a **choice** (vote or skip), not merely displayed or queued.  
`reservedIds`: current pair + warm/queued pairs — never pick these.

If applying recent would leave fewer than 2 candidates, ignore **recent** only. Still honor `reservedIds`.

1. `unplaced` = eligible, `matches < 3`, not recent.
2. `placed` = eligible, `matches >= 3`, not recent.
3. If `unplaced` is non-empty:
   - `p1` = unplaced with highest `σ` (filename tie-break).
   - If `placed` is non-empty: `p2` = placed that **maximizes `q(p1, p2)`**.
   - Else: `p2` = next-highest-`σ` unplaced (only while nobody is placed).
4. Else:
   - `p1` = eligible (not recent) with highest `σ`.
   - `p2` = other eligible that maximizes `q(p1, p2) * p2.σ` (close match that is still uncertain).
5. Never return the same id twice. If `< 2` eligible, return null.

Do **not** sample 50 random files. Sorting 20k records is fine.

**Match strip (session only):** `RecentCues`, cap `MatchCueLimit = 10`. On vote, **before** the TrueSkill update, if winner `μ ≥` loser `μ` append Confirmation, else Upset. Skip / drop do not append. `Start()` clears cues, session vote count, and the recent-shown set. Not written to JSON.

`RankingSession` keeps the folder string so it can `Save`. Pair picking still uses ids only.

Prefetch: the Shell may ask for the next pair before the current vote. That pair is allowed to be one vote stale. Do not increment `impressions` until the user votes or skips that pair. After a vote/skip, clear `Current` before `Pick()` so a 2–3 file library can pair again (the just-finished pair must not stay reserved).

### Catalog (`RankMaster2.Catalog`) — disk names and JSON only

- `Scan(folder) → IReadOnlyList<MediaRecord>` (merge with JSON, apply media policy)
- `Save(folder, records)` atomic
- `LoadRaw` / schema tolerant of missing `impressions` (treat as 0)
- Rewrite ids after rename (Actions calls this)

### Pipeline (`RankMaster2.Pipeline`) — the only module that reads media bytes

Single configuration knob:

```
PrefetchPairs = 2
```

That is **pairs ahead of the current pair**. Current + 2 warm pairs = up to 6 items in the pipeline. Changing this one value changes queue length. Default 2.

**One sequential reader.** Never two huge files at once. Never prefetch the next file until the current read has finished.

Most files are a few MB; some are 100–200 MB on USB 2 (~30–40 MB/s). Depth is for the small ones. Large files still queue in single file.

**Stills:** decode from a disposed `FileStream` (no `Uri` cache). Honor color profile. Fit to **panel physical pixels**, never upscale in the decoder, long edge capped at **4096**. Optional 720px first paint for files > 4 MB, then replace with the full-size frame. Both passes decode at the target size (`DecodePixelWidth`), never at full resolution. Evict previous bitmaps on swap.

**Video:** at most **two live decoders** (the visible pair). Warm video pairs may hold a path or a first-frame still, not four running players. Playback is **LibVLC** (software, including AV1 in `.mp4`). Frames are painted onto the same WPF `Image` as stills so overlays stay clickable. Do **not** use WPF `MediaElement` / Media Foundation — it cannot play AV1 and was dropping those files as if they were corrupt. On `Release`, stop the player and do not return until the file can be moved.

**Window resize:** do not re-decode during drag. The next fill uses the new panel size.

API shape (names can match this closely):

```
Show(left, right)           // make these the visible pair
Enqueue(pair)               // add a warm pair, up to PrefetchPairs
Release(id)                 // drop decode + file lock
CancelWarmContaining(id)    // discard any queued pair that includes id
```

When `Show` targets a pair that is already warm, **remove it from `_warm`** so `Enqueue` can accept the next pair. When it is cold, show the ids immediately and let the reader fill the panes (still: first paint; video: percent loader). Votes/skip/discard are ignored until both panes are ready (still has pixels; video has painted a frame). Each waiting pane shows buffer percent. A video play error drops that id only when the failing path is still the one this pane was asked to open. Teardown errors during `Stop` must not `Drop`.

### Actions (`RankMaster2.Actions`)

Discard, special 1, undo last move, rename-by-conservative-score. Orchestrates Pipeline + Catalog + file system. No pair-picking.

### Shell (`RankMaster2.App`)

Borderless fullscreen WPF window, keys, two surfaces, start/resume, overlay, match strip, F1/`?` help. Version on the start screen and help footer. Talks to the four modules. Dark compare surface. `Esc` calls `Shutdown` with no extra catalog write.

---

## Project layout

```
C:\Utils\rank-master-2\
  RankMaster2.exe            # self-contained publish; run this
  libvlc\                    # native VLC, must stay next to the exe
  project\                   # source, tests, this spec
    Directory.Build.props    # Version (bump on every shipped change)
    README.md
    SPEC.md
    RankMaster2.sln
    publish.ps1
    src\
      RankMaster2.Core\        # types + interfaces
      RankMaster2.Ranking\
      RankMaster2.Catalog\
      RankMaster2.App\         # WPF Shell + Pipeline + Actions
    tests\
      RankMaster2.Ranking.Tests\
      RankMaster2.Catalog.Tests\
```

App version is `Version` in `Directory.Build.props`. It appears on the start screen. Patch / minor / major as in the README.

Pipeline and Actions live in the App project in v1 because they touch WPF media types and `File.Move`. Their interfaces still sit in Core so tests and later splits stay possible.

Target framework: `net8.0-windows`.

## Build / run (once the SDK is installed)

From `project\`:

```
dotnet test
dotnet run --project src/RankMaster2.App
```

Ship the exe to the parent folder:

```
powershell -File publish.ps1
```

Shipped. This file stays the brief. The prefetch depth constant in code is `MediaPipeline.DefaultPrefetchPairs` (currently `2`).
