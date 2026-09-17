# Rank Master 3

Ranks a folder of photos **or** videos by pairwise comparison. You are shown two; you pick the better one; a TrueSkill rating is stored in `rankmaster_db.json` next to the files.

This is a remake of Rank Master 1. Behavior is defined in [SPEC.md](SPEC.md). If the code disagrees with the spec, the spec is right until we change the spec.

**Current version: 3.0.0** (Rank Master 3).  The number lives in [Directory.Build.props](Directory.Build.props) and on the start screen next to the title (small label on its top-right corner). Bump it in the same change as the update, then republish:

| Bump | When |
|---|---|
| Patch (`1.0.x`) | Bug fix, small UI tweak, packaging-only |
| Minor (`1.x.0`) | New capability that is not a rewrite |
| Major (`x.0.0`) | Large new feature, or an extensive engine rework |

This tree is `project\` (including `.git`). The runnable app is the single file one level up: `..\RankMaster2.exe`.

## What is in 3.0.0 — Rank Master 3

**Rank Master 3 is three programs**, sharing one ranking engine and one database format. Rank Master
2 — the WPF desktop app in `src/RankMaster2.App` — is the program it replaces; it is frozen at
1.1.4, keeps its own name, and stays in the tree as the reference for the behaviour it defined.

- **The server** (`src/RankMaster2.Server`) owns a folder and every action taken on it — the only
  writer *among the three programs of Rank Master 3*, so the phone and the PC client can never
  corrupt `rankmaster_db.json` between them. **Rank Master 2 keeps reading and writing that same
  file**, which is why the database format is frozen and will stay frozen: you can go on using the
  old app for as long as you like. What the server cannot do is stop it — the old app knows nothing
  about the server's lock file. So: **one program per folder at a time.** Do not open a folder in
  Rank Master 2 while the server has it open, and do not open it in the server while Rank Master 2
  has it. Either one alone is safe; both at once can lose ratings, and no warning will appear.
  The server also serves resized stills and range-streamed video over TLS on the LAN, and it runs
  behind a notification-area icon (`RankMaster2.Tray.exe`). Its contract is
  [SERVER_SPEC.md](SERVER_SPEC.md).
- **The phone** (`android/`) ranks a folder from an Android device with the PC screen off.
- **The PC client** (`pc/`) replaces the old desktop app: same two-pane compare, same keys, but a
  client of the server rather than a second writer.

What changed for someone who just wants to rank photographs:

- **Undo takes back any action**, not only a file move. A mis-hit key is a real vote, and there is
  now a way back from one. `Ctrl+Z`, one level.
- **Rename by rank no longer copies the library first.** It journals the renames instead, so it does
  not need gigabytes of free space or minutes of copying, and a crash part-way through can no longer
  lose a rating. What it protects is the database; the photographs were never at risk.
- **Renamed files now carry a short tag after the rank number** — `000001-7f3a.jpg` rather than
  `000001.jpg`. Each run of rename picks its own four-character tag, so the names a run is creating
  can never collide with the names already in the folder. That is what makes a rename interrupted
  half-way recoverable without anything having to guess which files already moved. Sorting by name
  is still sorting by rank, which is the only thing the number is for.
- **Votes are written within two seconds rather than on every key press.** A vote counts the instant
  you press it; the file is written a moment later, or immediately if you discard, rename, save or
  close. A crash can cost the last few votes and nothing else. Set `SaveDelaySeconds` to `0` in
  `appsettings.json` to go back to writing on every press
  ([SERVER_RUNNING.md](SERVER_RUNNING.md)).
- **The Windows client's first-run faults are fixed** — pictures no longer stop appearing after a
  couple of hundred votes, the start screen no longer wedges if opening a folder fails, and a 4K
  monitor gets a picture decoded for a 4K monitor.
- **Skip is gone from the Windows client.** You said you do not use it; the phone never had it. The
  desktop app still has `↓` / `S`, and the server still accepts a skip from anything that sends one.
- **The desktop app starts far less work.** The video engine is no longer loaded for a folder that
  contains no video, the plugin set it does load is 26 files rather than 320, and the program is
  published precompiled.

**Rename by rank is started from the PC client's start screen** (or from `rm2ctl`, the headless
command-line client). It shows `done / total` and a Cancel button from the first instant — on a
local disk it is over before you can read it, but on a USB drive you can stop it the moment it looks
wrong. Cancelling stops where it is; it never rolls anything back and never loses a rating, it just
leaves the folder with some names changed and some not, which the next run tidies up.

The repository, the namespaces and the assembly names still say `RankMaster2`. They are code
identity, not product identity: renaming them would churn every file for no one's benefit, and the
phone's application id in particular cannot change without the owner uninstalling and re-pairing.

## What is in 1.1.0

Fullscreen two-pane compare, LibVLC video (including AV1), prefetch of 2 pairs, v1-compatible JSON, discard / `special 1` / rename-by-`μ − 3σ`, F1/`?` help, last-10 confirmation (emerald) / upset (amber) strip.

**What is in 1.1.4**

Saving can no longer destroy a library. If the folder disappears mid-session — USB pulled, network share dropped, renamed in Explorer while the app is open — `Save` used to recreate the empty folder, find no media in it, and atomically install an empty database over every rating. No exception, no warning. It now refuses to write and reports the error, which lets the vote roll back the way it always should have.

**What is in 1.1.3**

Stills now decode straight to the size the pane draws, instead of decoding at full resolution and scaling afterwards. A 48 MP photo costs its display size rather than ~192 MB of pixels, so large libraries open faster and the 720px first paint is finally cheap. Embedded colour profiles are honoured again (Adobe RGB / Display P3 photos were rendering with the wrong saturation).

**What is in 1.1.2**

Ship layout is now `RankMaster2.exe` + `libvlc\` folder instead of one self-extracting file. This removes the ~12 s first-run extraction after every published update; startup time is now the same on every launch. No app-behavior change.

**What is in 1.1.1**

Version label moved onto the title (small top-right corner of the title text). Undo (`Ctrl+Z`) can no longer leave a small library with the old pair reserved and nothing on screen; dead `AppScreen` type removed.

`PrefetchPairs` defaults to **2**. Change `MediaPipeline.DefaultPrefetchPairs` (the one knob).

## Requirements

- **Run the exe:** Windows 11. No extra .NET install.
- **Build from this folder:** .NET 8 SDK (Windows).

## Run

**The server** — start here, because both new clients are clients of it. Building, configuring,
running and troubleshooting it is [SERVER_RUNNING.md](SERVER_RUNNING.md). It runs behind the
notification-area icon; the icon's menu tells you which address it is listening on, which is the
address the phone needs.

**The PC client** — `pc/`. Built and published from `pc/publish.sh` / `pc/install.ps1`; see
[PC_CLIENT_PLAN.md](PC_CLIENT_PLAN.md). This is what replaces the old desktop app.

**The phone** — `android/`. See [CLIENT_PLAN.md](CLIENT_PLAN.md).

**Rank Master 2, the old desktop app** — `src/RankMaster2.App`. It is **frozen**: it is kept in the
tree, it still runs, and it is not being changed. Nothing in this round touched it.

```
dotnet test
dotnet run --project src/RankMaster2.App   # the frozen Rank Master 2
```

No installer. The window and `.exe` use the same icon as Rank Master 1 (`src/RankMaster2.App/icon.ico`).

Self-contained **single-file** publish (writes `..\RankMaster2.exe`):

```
powershell -File publish.ps1
```

Writes `..\RankMaster2.exe` and copies `..\libvlc\` next to it. The exe is self-contained but the native VLC files stay in the folder — embedding them made the first launch after every publish take ~12 s to unpack. Keep `libvlc\` next to the exe.

Or directly:

```
dotnet publish src/RankMaster2.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:DebugType=None -o ..
```

First launch of a fresh exe is no slower than any other launch.

## Existing rankings

Opens the same `rankmaster_db.json` as Rank Master 1 (`version: 1`). You do not migrate.

## Keys

| Key | Action |
|---|---|
| `←` `→` | Vote |
| `↓` / `S` | Skip — **the frozen desktop app only.** The PC client and the phone do not offer it |
| `1` `2` | Discard (to `discarded/`) |
| `4` `5` | Move to `special 1/` |
| `O` | Open folder |
| `F1` | Toggle help (hover `?` also works) |
| `Ctrl+S` | Save |
| `Ctrl+Z` | Take back the last action (vote, discard, special) — one level. The frozen desktop app's `Ctrl+Z` still takes back the last file move only |
| `Esc` | Quit immediately (current pair is not saved as seen) |

## Docs

| File | Role |
|---|---|
| [SPEC.md](SPEC.md) | Product, ranking, JSON, modules, prefetch. Update it when behavior changes. |
| [SERVER_RUNNING.md](SERVER_RUNNING.md) | **How to build, configure, run and troubleshoot the server.** Start here to deploy it. |
| [SERVER_SPEC.md](SERVER_SPEC.md) | The server's API contract. The four client surfaces code against this. |
| [SERVER_PLAN.md](SERVER_PLAN.md) | Why the server is built the way it is. |
| [CLIENT_PLAN.md](CLIENT_PLAN.md) | The Android client: stack, screens, phases, acceptance gate. |
| [PC_CLIENT_PLAN.md](PC_CLIENT_PLAN.md) | The Windows client that replaces the old desktop app. |
| [Directory.Build.props](Directory.Build.props) | App version. Bump on every shipped change. |
| This README | How to build and what the app is. |
