# Rank Master 2 — specification

This file is the source of truth. If code and this document disagree, the document wins until we change it on purpose. When behavior changes, update this file in the same change.

## What it is

A Windows 11 desktop app that ranks the photos **or** videos in one folder by pairwise comparison. You see two items, pick the better one (or skip). Ratings live in `rankmaster_db.json` in that folder so the library is portable.

Working title / assembly name: **RankMaster2**  
Repo: `C:\Utils\rank master 2`

## Stack

- .NET 8 (Windows), WPF
- Portable folder publish later (`dotnet publish -r win-x64 --self-contained`). No installer, no Store, no browser shell
- No SQLite. No Chromium. No Python

## Non-goals (v1)

- Thumbnail grid or filmstrip
- Recursive scan
- HEIC / HEVC / RAW / TIFF / AVIF
- Tie vote, vote-undo, match log
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

1. **Start** — Open folder (`O` or button). If a last-folder path exists and is still valid, show **Resume**. Resume does **not** auto-start; the user clicks it. Last folder path lives in app local data, not in the media folder. Resume does not restore a pair (pick a new one).
2. **Loading** — Scanning / merging JSON.
3. **Ranking** — Full-window two-pane compare, dark background.
4. **Renaming** — Progress for backup + two-phase rename.
5. **Saved** — After `Esc` (saved). User closes the window.

### Compare UI

- Two panes, `Uniform` / contain, black letterbox, a neutral VS mark in the center.
- **Filename** visible on each pane.
- Click a pane to vote for it. Keys also work.
- Vote applies immediately. The new pair’s IDs come on screen at once even if pixels are not ready.
- **Still:** paint the first decodable image as soon as one exists; refine in place if a better decode arrives. No spinner if any pixels can be shown. Baseline JPEG/PNG on a slow disk may have no pixels until the read finishes — that is acceptable.
- **Video:** spinner until a frame can play, then autoplay, loop, muted, no controls. Two videos may play at once.
- Overlay (start + ranking): folder name, session vote count, unranked count (`matches == 0`). No tier names, no match-history dots.
- Help: a small hover/click cheat sheet of keys is fine.

### Keys

| Key | Action |
|---|---|
| `←` / `→` | Vote left / right |
| `↓` or `S` | Skip (no rating change) |
| `1` / `2` | Move left / right to `discarded/` |
| `4` / `5` | Move left / right to `special 1/` |
| `O` | Open folder |
| `Ctrl+S` | Save JSON now |
| `Ctrl+Z` | Undo **last move** only |
| `Esc` | Save, go to Saved screen |

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
- Scan disk, then merge: existing rows kept, missing files dropped from the in-memory session (leave orphan keys out of the next save), new files get default rating.
- Atomic save: write `rankmaster_db.json.tmp`, flush, replace `rankmaster_db.json`.
- Autosave every **20** votes, plus manual save, plus Esc, plus before rename.
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
```

`MediaId` is the filename. No extra GUID.

### Ranking (`RankMaster2.Ranking`) — pure CLR, no `System.IO`, no WPF

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

After a shown pair (vote **or** skip): `impressions++` on both ids (compatibility only).

`matches++` and `lastPlayed = now` only on vote.

**Conservative score** (rename): `μ − 3σ`.

**Match quality** (picker only):

```
c² = 2β² + σ1² + σ2²
q  = sqrt(2β² / c²) * exp(−Δμ² / (2c²))
```

**`SelectNextPair(records, recentShownIds)`**

`recentShownIds`: last ~30 **shown** ids (not merely queued). If applying it would leave fewer than 2 candidates, ignore it.

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

Prefetch: the Shell may ask for the next pair before the current vote. That pair is allowed to be one vote stale. Do not increment `impressions` until that pair is **shown**.

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

**Stills:** decode to **panel pixel size** (include DPI), long edge capped at 2560. Prefer shrink-on-load (WIC scaled decode) over full-res then scale. Honor EXIF. Paint first available bitmap; optional refine to the cap. Evict the previous pair’s bitmaps on swap (do not wait for GC).

**Video:** at most **two live decoders** (the visible pair). Warm video pairs may hold a path or a first-frame still, not four running players. On `Release`, clear `Source` / close the player and do not return until the file can be moved.

**Window resize:** do not re-decode during drag. The next fill uses the new panel size.

API shape (names can match this closely):

```
Show(left, right)           // make these the visible pair
Enqueue(pair)               // add a warm pair, up to PrefetchPairs
Release(id)                 // drop decode + file lock
CancelWarmContaining(id)    // discard any queued pair that includes id
```

When `Show` targets a pair that is already warm, swap it up. When it is cold, show the ids immediately and let the reader fill the panes (still: first paint; video: spinner).

### Actions (`RankMaster2.Actions`)

Discard, special 1, undo last move, rename-by-conservative-score. Orchestrates Pipeline + Catalog + file system. No pair-picking.

### Shell (`RankMaster2.App`)

WPF window, keys, two surfaces, start/resume, overlay. Talks to the four modules. Standard title bar, dark compare surface.

---

## Project layout

```
C:\Utils\rank master 2\
  README.md
  SPEC.md
  RankMaster2.sln
  src\
    RankMaster2.Core\        # types + interfaces
    RankMaster2.Ranking\
    RankMaster2.Catalog\
    RankMaster2.App\         # WPF Shell + Pipeline + Actions
  tests\
    RankMaster2.Ranking.Tests\
    RankMaster2.Catalog.Tests\
```

Pipeline and Actions live in the App project in v1 because they touch WPF media types and `File.Move`. Their interfaces still sit in Core so tests and later splits stay possible.

Target framework: `net8.0-windows`.

## Build / run (once the SDK is installed)

```
dotnet test
dotnet run --project src/RankMaster2.App
```

Later:

```
dotnet publish src/RankMaster2.App -c Release -r win-x64 --self-contained
```

## Implementation order

1. This spec + repo — **done**
2. Solution + contracts that compile — **done**
3. Ranking + tests, Catalog + tests — **done**
4. Pipeline (sequential reader, `PrefetchPairs`, still first-paint, video spinner) — next
5. Shell + Actions wired to the real Pipeline

Do not start a later phase by opening any other ranking app. This file is the brief.

The prefetch depth constant in code is `MediaPipeline.DefaultPrefetchPairs` (currently `2`).
