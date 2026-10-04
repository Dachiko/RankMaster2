# Rank Master 3 — agent guide

Read this first, then `NOTES.md` (where the work stands). This file changes rarely; `NOTES.md`
changes every session. `CLAUDE.md` only imports this file.

## What it is

Ranks a folder of photos or videos by pairwise comparison: two items on screen, pick the better one,
TrueSkill ratings saved in `rankmaster_db.json` inside that folder. Rename by rank writes the order
into the file names. Mike ranks his own libraries with it, on the PC and on his Android phone.

**Rank Master 3 is the product being worked on.** Rank Master 2 (`src/RankMaster2.App`, WPF, 1.1.4)
is frozen: it stays in the tree as the reference for engine behaviour and is never changed.

## Where everything is

| What | Where |
|---|---|
| Repo (this folder) | `C:\utils\rank master 2\project` — GitHub `Dachiko/RankMaster2`, branch `master` |
| Installed v3 | `C:\Utils\RankMaster v3\` — `tray\` (server + tray icon), `pc\` (Windows client, `VERSION.txt`) |
| Server settings | `C:\Utils\RankMaster v3\tray\appsettings.json` (`ListenAddress` = PC's LAN IP, port 18611) |
| Server data | `%LOCALAPPDATA%\RankMaster2\Server\` — certificate, paired devices, cache, `logs\` |
| Frozen Rank Master 2 | `C:\utils\rank master 2\RankMaster2.exe` (the docs' `C:\Utils\rank-master-2` is wrong) |
| Phone app | `android/` — app id `com.rankmaster2.phone`, built on the build box (see below) |

## The three programs (one engine, one database format)

| Program | Code | Role |
|---|---|---|
| Server | `src/RankMaster2.Server`, hosted by `src/RankMaster2.Tray` | The only writer of `rankmaster_db.json`. Serves stills and range-streamed video over TLS on the LAN. Normally started by the PC client and left running for the phone. |
| PC client | `pc/` (Avalonia + LibVLC, own `pc/RankMaster2.Pc.sln`) | Fullscreen two-pane compare; a client of the server. Builds an exe also called `RankMaster2.exe`. |
| Phone | `android/` (Kotlin, Compose, Media3) | Ranks from the phone with the PC screen off. |

Shared engine: `src/RankMaster2.Core`, `.Ranking` (TrueSkill, pair picking), `.Catalog` (JSON db,
rename engine), `.Actions`. `src/rm2ctl` is a headless command-line client (also does rename).

## Docs — which file answers what

| File | Role |
|---|---|
| `SPEC.md` | Engine rules (TrueSkill, pairs, cues, undo, media, db format) + the frozen desktop app. **If code and spec disagree, the spec wins until it is changed on purpose.** Update it in the same change as behaviour. |
| `SERVER_SPEC.md`, `openapi.yaml` | The server's API contract; every client codes against it. |
| `SERVER_RUNNING.md` | Operating manual: build, configure, firewall, pairing, troubleshooting. |
| `SERVER_PLAN.md`, `PC_CLIENT_PLAN.md`, `pc/plans/A–G`, `CLIENT_PLAN.md` | Why each part is built the way it is; settled decisions and deliberately absent features. Read the relevant one before changing that part. |
| `android/BUGS.md` | Open phone bugs. An item is deleted only once the fix works on Mike's phone. |
| `android/MEMORY_PROPOSALS.md` | Options for the phone's video out-of-memory; none implemented. |
| `AUDIT.md`, `AUDIT2.md` | Two independent audits and their findings (closed). |
| `CHANGELOG.md` | One entry per version. |

## Build, test, deploy (this PC)

.NET 8 SDK is installed here. No Android SDK here.

```powershell
dotnet test RankMaster2.sln            # engine, server, audits (no windows)
dotnet test pc\RankMaster2.Pc.sln      # PC client logic
powershell -File deploy.ps1            # pull origin/master, publish tray + PC client into C:\Utils\RankMaster v3, start the tray
powershell -File deploy.ps1 -SkipPull  # same, from the local tree
```

- The suites were written and kept green on the Linux build box. On Windows some tests fail for
  reasons of the platform, not of the app: see `NOTES.md` for the current list, and compare with
  it before blaming your change.
- `deploy.ps1` kills the running tray/server/PC client first, keeps the tray's `appsettings.json`,
  writes `pc\VERSION.txt`, and does not launch the PC client (it is fullscreen). Without `-SkipPull`
  it refuses to run if tracked files have local edits.
- `pc/install.ps1` is the other route: downloads the latest PC client from the build box
  (`https://bormin.fintebtc.de/rm2`).
- `publish.ps1` publishes the **frozen** Rank Master 2 — don't use it for v3.

## Who works where

Most v3 commits come from `bormin-agent`, an agent on a separate Linux build box that also builds
the APKs (delivered to Mike over Telegram, see `CLIENT_PLAN.md` §8) and hosts the PC client
download. Mike's PC pulls from GitHub and deploys. Before starting work, `git fetch` and check
`git status`: the build box may have pushed, and uncommitted edits here may not be yours.

## Versioning

- One number for server, tray and PC client: `<Version>` in `Directory.Build.props` (shown on the
  start screen). Bump it in the same commit as every shipped change: patch = fix/tweak, minor = new
  capability, major = big feature or engine rework. Add a `CHANGELOG.md` entry; tag `vX.Y.Z`.
- The phone has its own `versionName` / `versionCode` in `android/app/build.gradle.kts`.
  `versionCode` only ever climbs, or Android refuses the update.
- Code names stay `RankMaster2` (namespaces, assemblies, repo, phone app id). That is code identity,
  not the product name; don't rename.

## Rules and traps

- **One program per folder at a time.** The server locks a folder (`.rankmaster.lock`); the frozen
  Rank Master 2 ignores that lock. Both on one folder can lose ratings silently.
- **The database format is frozen** (Rank Master 1 `version: 1`), so the old app keeps working.
- **Two exes named `RankMaster2.exe`** (frozen app and v3 PC client); only the path tells them apart.
- **No `appsettings.json` beside the tray** → the server binds 127.0.0.1 and the phone can't connect.
- **Phone fixes are guesses until seen on the phone.** Say "fixed in the tree, unconfirmed", never "fixed".
- **Testing:** normal tier is the two `dotnet test` runs. Launching the PC client (fullscreen) or
  taking screenshots is the visual tier: propose it, run it only on Mike's yes, close it after.
