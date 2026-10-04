# Rank Master 3 — where the work stands

Updated 2026-10-04. How to work on it is in `AGENTS.md`; this file is only the current state.

## State

- **Version 3.1.0** (2026-10-04, tag `v3.1.0`) is the head of `master` and `origin/master`:
  **Review mode** on the phone (see `CHANGELOG.md`; server `GET /session/items` +
  `POST /session/items/discard`, SERVER_SPEC.md § 10.17–10.18; phone `android/.../ui/review/`).
  Pushed and deployed on this PC 2026-10-04 (`deploy.ps1 -SkipPull`; `/ping` answers 3.1.0).
- **Debug APK 3.1.0 sent to Mike on Telegram 2026-10-04** (arm64 only, see `AGENTS.md`); waiting for
  him to install, pair and try it.
- **Review mode is untested on a phone.** Unit tests only (server 18 new, phone 51 new). A debug
  APK (`com.rankmaster2.phone.debug`, installs beside the release app, needs its own pairing) builds
  here; to check on the phone: swipe feel (30 % / fling threshold), the edge back-gesture conflict,
  video while swiping 4K files, landscape side-column layout. Videos slide but don't tilt
  (SurfaceView ignores the parent's rotation).
- **Installed on this PC:** 3.1.0 in `C:\Utils\RankMaster v3` (`78f802a`), tray running.
- **Phone:** the tree's APK is now `versionName 3.1.0` / `versionCode 4`. Which build is on Mike's
  phone is unknown — ask him.
- **Local Android toolchain** since 2026-10-04 (see `AGENTS.md` § Build): phone tests 443 pass.
- **Open phone bugs** (`android/BUGS.md`): 1 video wrong proportions, 2 cancel notch drawn wrong,
  3 Back leaves the app while browsing folders, 4 accidental votes near the screen edge. All four
  have a fix in the tree; none is confirmed on the phone.
- **Phone video memory** (`android/MEMORY_PROPOSALS.md`): two 4K AV1 clips side by side crashed the
  app with OutOfMemoryError twice. Options are written up and ranked; nothing implemented.
- The 3.0.1–3.0.6 changes were PC client fixes and polish from Mike's first real use (see
  `CHANGELOG.md`).

## Tests on this PC (Windows), run 2026-10-04 at 3.0.6

Baseline to compare against; none of these failures was investigated.

| Suite | Result | Failures |
|---|---|---|
| Ranking, Catalog, Audit.Compatibility, Audit.StateMachine | all pass | — |
| Server.Tests | 446 pass, 2 fail, 20 skip | `The_openapi_enum_lists_the_same_codes` ("could not find the ErrorCode enum in openapi.yaml", maybe CRLF line endings); `RenameAccessDeniedWedgeTests.Cancel_clears_the_wedge…` (the folder-permission trick likely needs Unix) |
| Audit.Security | 102 pass, 1 fail | `A_failed_persist_during_redeem…`: "Unix file modes are not supported on this platform" |
| Pc.Tests | 283 pass, 2 fail | `StartupInitializeTests` ×2: "LibVLCSharp was loaded before first_frame" (another test in the same run loads it first) |
| Pc.Stills.Tests | 75 pass, 1 fail | `Twenty_thousand_names_probe_under_half_a_second`: 514 ms against a 500 ms limit (timing) |
| Pc.Link.Tests | 15 pass, 58 fail | Almost all: the test client cannot complete the TLS handshake with the test server ("unexpected EOF" during SSL). The installed app connects fine, so suspect how the tests make their certificate on Windows (compare commit `c56bd64`, the Windows TLS fix for the real server). |

## Loose ends

- **Uncommitted edits that are not from the 2026-10-04 docs session** — leave them alone unless
  Mike says otherwise:
  - `deploy.ps1` (modified): replaces `Stop-Process` with `taskkill /F` and a wait loop, and stops
    everything a second time before swapping the PC client folder. Looks like a real fix for a
    file lock. While it is uncommitted, `deploy.ps1` without `-SkipPull` refuses to run.
  - `open-grok.cmd` (untracked): launches Grok Build in this folder.
- Tags: only `v3.0.0` exists; 3.0.1–3.0.6 are untagged.
- `README.md` and `pc/install.ps1` name the frozen app's folder `C:\Utils\rank-master-2`; it is
  really `C:\utils\rank master 2`.

## Waiting for Mike (asked 2026-10-04, not answered)

- Commit the `deploy.ps1` fix, or is it someone else's work in progress?
- Which APK version is on his phone?

## Next step

1. Review mode on the phone: get a 3.1.0 APK on his phone (release from the build box, which now has
   it on GitHub, or the local debug APK), and tune from his feedback.
2. Commit (or drop) the `deploy.ps1` fix.
3. Confirm the four phone bugs on his phone and delete the ones that are fixed.
4. Choose an option from `MEMORY_PROPOSALS.md` if the phone still crashes on 4K video.
