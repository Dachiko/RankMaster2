# Rank Master 3 — where the work stands

Updated 2026-10-04. How to work on it is in `AGENTS.md`; this file is only the current state.

## State

- **PC 3.4.0** (tag `v3.4.0`, deployed 2026-10-04): **in-app folder browser** replaces the Windows
  picker for `O` and `R` (Mike's simple layout: full path on top, current folder highlighted, folder
  names under it). Plan, decisions and as-built notes: `pc/plans/I-folder-browser.md`. Not yet seen
  on screen (visual check waits for Mike's yes); not pushed (waits for Mike's OK).
- **Server/tray/PC 3.3.0** (tag `v3.3.0`, deployed 2026-10-04): **house-style redesign** of every
  PC-side surface except the compare screen — start screen, rename card, start-screen keys page (R =
  rename), tray two-pane icon, tray menu (Show QR + Exit only), QR-only pairing card. Plan,
  decisions and stage record: `pc/plans/H-house-style.md`. Mockup: `C:\ai\projects\rankmaster\mockups\house-style\index.html`
  (tab C). **Not yet seen by anyone on screen**: the targeted visual
  check (H § 5 S5) waits for Mike's yes; things to look at are listed there and in the S1/S2 reports
  (Doto font resolving, pill focus, decode, menu corner pixels, card dot grid strength, percent width
  9→10→100). `master` not pushed yet (waits for Mike's OK). The phone app is the next round.
- Before that: server/tray/PC 3.2.0 (tag `v3.2.0`). **Phone 3.2.4** (tag
  `phone-v3.2.4`, versionCode 9) is the head of `master` and `origin/master`: **Review mode**, done
  and confirmed on Mike's phone 2026-10-04 (see `CHANGELOG.md`). Server `GET /session/items`,
  `POST /session/items/discard`, `PUT /session/review-position` (SERVER_SPEC.md § 10.17–10.19);
  phone `android/.../ui/review/`.
- **Review mode, Mike's design:** only the item, ranking's cancel notch and a ⋮ menu (ranking's
  menu look; long press opens it too). Tap = keep, swipe left = discard, swipe right = nothing; only
  touches starting inside ranking's live area (`ui/LiveArea.kt`) count, and the screen claims no
  system-gesture exclusion, so the edge back swipe works. No button strip, colours, status text or
  progress line (he said no). Non-fatal failures silent. Position stored on the PC in
  `<folder>/.rankmaster_review.json`; resumes on the same item.
- **Review videos use a TextureView** (`MediaPane(textureVideo = true)`). With a SurfaceView the
  picture stayed black until something was drawn over it (it appeared when the menu opened);
  ranking always has chrome over its panes and keeps its SurfaceView. The 3.2.2 debug panel that
  found this is removed in 3.2.4 (in git history at `770b7d9` if ever needed again).
- **Release APK:** signed only on the build box (keystore not here). Waiting for the build box to
  build `phone-v3.2.4` as a release and send it to Mike (he was given the line to send it). Until
  then he uses the local debug app (`com.rankmaster2.phone.debug`), **3.2.4 sent on Telegram
  2026-10-04**, beside the release app; he can uninstall it once the release has Review.
- **Local Android toolchain** since 2026-10-04 (see `AGENTS.md` § Build): phone tests 491 pass.
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

1. Review mode: get the build box's signed release of `phone-v3.2.4` onto his phone; then tune
   from his use (swipe threshold, menu glyphs, resume).
2. Commit (or drop) the `deploy.ps1` fix.
3. Confirm the four phone bugs on his phone and delete the ones that are fixed.
4. Choose an option from `MEMORY_PROPOSALS.md` if the phone still crashes on 4K video.
