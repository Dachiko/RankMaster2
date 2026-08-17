# Rank Master 2

Windows app for ranking a folder of photos **or** videos by pairwise comparison. Pick the better of two; a TrueSkill rating is stored in `rankmaster_db.json` next to the files.

This is a remake. Behavior is defined in [SPEC.md](SPEC.md). If the code disagrees with the spec, the spec is right until we change the spec.

## Status

Spec is the source of truth. Compare loop, prefetch, and file actions are wired. Update this file and SPEC.md when behavior changes.

| Piece | State |
|---|---|
| SPEC.md / this README | Living docs — update when behavior changes |
| Ranking (TrueSkill + pair picker) | Implemented + tests |
| Catalog (scan, v1 JSON, media policy) | Implemented + tests |
| Pipeline / compare UI | Fullscreen compare (RankMaster chrome), sequential prefetch (`PrefetchPairs = 2`), save on each vote/skip, ~220 ms select flash |
| Actions | Discard → `discarded/`, special → `special 1/`, `Ctrl+Z` undoes last move, start-screen rename by `μ − 3σ` |

`PrefetchPairs` defaults to **2**. Change `MediaPipeline.DefaultPrefetchPairs` (the one knob).

## Requirements

- Windows 11
- .NET 8 SDK (Windows)

The machine this repo was created on had a .NET **runtime** but no SDK. Install [SDK 8](https://dotnet.microsoft.com/download/dotnet/8.0) before building.

## Run

```
dotnet test
dotnet run --project src/RankMaster2.App
```

No installer. Later we publish a self-contained folder.

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
| `Ctrl+S` | Save |
| `Ctrl+Z` | Undo last move |
| `Esc` | Quit immediately (current pair is not saved as seen) |

## Docs

| File | Role |
|---|---|
| [SPEC.md](SPEC.md) | Product, ranking, JSON, modules, prefetch. Update it when behavior changes. |
| This README | How to build and what the app is. |
