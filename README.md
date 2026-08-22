# Rank Master 2

Windows app for ranking a folder of photos **or** videos by pairwise comparison. Pick the better of two; a TrueSkill rating is stored in `rankmaster_db.json` next to the files.

This is a remake of Rank Master 1. Behavior is defined in [SPEC.md](SPEC.md). If the code disagrees with the spec, the spec is right until we change the spec.

**Current version: 1.1.2.** The number lives in [Directory.Build.props](Directory.Build.props) and on the start screen next to the title (small label on its top-right corner). Bump it in the same change as the update, then republish:

| Bump | When |
|---|---|
| Patch (`1.0.x`) | Bug fix, small UI tweak, packaging-only |
| Minor (`1.x.0`) | New capability that is not a rewrite |
| Major (`x.0.0`) | Large new feature, or an extensive engine rework |

This tree is `project\` (including `.git`). The runnable app is the single file one level up: `..\RankMaster2.exe`.

## What is in 1.1.0

Fullscreen two-pane compare, LibVLC video (including AV1), prefetch of 2 pairs, v1-compatible JSON, discard / `special 1` / rename-by-`μ − 3σ`, F1/`?` help, last-10 confirmation (emerald) / upset (amber) strip.

**What is in 1.1.2**

Ship layout is now `RankMaster2.exe` + `libvlc\` folder instead of one self-extracting file. This removes the ~12 s first-run extraction after every published update; startup time is now the same on every launch. No app-behavior change.

**What is in 1.1.1**

Version label moved onto the title (small top-right corner of the title text). Undo (`Ctrl+Z`) can no longer leave a small library with the old pair reserved and nothing on screen; dead `AppScreen` type removed.

`PrefetchPairs` defaults to **2**. Change `MediaPipeline.DefaultPrefetchPairs` (the one knob).

## Requirements

- **Run the exe:** Windows 11. No extra .NET install.
- **Build from this folder:** .NET 8 SDK (Windows).

## Run

From this `project` folder:

```
dotnet test
dotnet run --project src/RankMaster2.App
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
| `↓` / `S` | Skip |
| `1` `2` | Discard (to `discarded/`) |
| `4` `5` | Move to `special 1/` |
| `O` | Open folder |
| `F1` | Toggle help (hover `?` also works) |
| `Ctrl+S` | Save |
| `Ctrl+Z` | Undo last move |
| `Esc` | Quit immediately (current pair is not saved as seen) |

## Docs

| File | Role |
|---|---|
| [SPEC.md](SPEC.md) | Product, ranking, JSON, modules, prefetch. Update it when behavior changes. |
| [Directory.Build.props](Directory.Build.props) | App version. Bump on every shipped change. |
| This README | How to build and what the app is. |
