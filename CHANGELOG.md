# Changelog — Rank Master 3

One entry per version of the server, tray and PC client (`Directory.Build.props`). The phone app
versions separately (`android/app/build.gradle.kts`). The frozen Rank Master 2 (1.1.x) history is
in `README.md`.

## 3.3.0 — 2026-10-04 (PC client + tray; phone unchanged)
- House style (`pc/plans/H-house-style.md`, version C "Quiet"; the compare screen is untouched).
- PC start screen: paper, no gradient; the last folder fills the screen in dot-matrix with
  `ENTER RESUME · O OPEN · R RENAME` under it; server lamp and `F1 KEYS` in the corner; one short
  status line. New key **R** = rename by rank; ←/→ move between the captions.
- Rename: the question and the progress are a dark card over the start screen; progress is a
  percent with a row of red dots, never "done / total". Shorter result and error sentences.
- F1 on the start screen opens a keys page (the compare screen keeps its own sheet).
- Motion: cards fade, the folder name and status line decode in at the owner's action.
- Tray: two-pane icon (left pane red while a folder is open), menu = Show QR and Exit only, a single
  click shows the QR. The pairing window is a borderless card with only the QR, renewed before it
  expires, closing itself once a phone pairs. No six-digit code, no warnings, no balloons.
- App and tray executables use the two-pane icon.

## Phone 3.2.4 — 2026-10-04 (phone only) — the release
- Mike confirmed Review on his phone with 3.2.3 (videos show). The debug panel and the video
  diagnostics from 3.2.2–3.2.3 are removed; the TextureView fix stays. Release build (R8) checked
  locally; the signed release APK comes from the build box.

## Phone 3.2.3 — 2026-10-04 (phone only)
- Review: videos draw on a TextureView instead of a SurfaceView. Mike saw a black video turn into a
  picture the moment the menu opened over it, the signature of a SurfaceView hole never shown.
  Ranking keeps its SurfaceView. Unconfirmed until seen on the phone.
- Review debug text no longer names files or URLs (`<file>.mp4`, `<url>`), at Mike's request.

## Phone 3.2.2 — 2026-10-04 (phone only)
- Review: "Debug info" in the menu shows a live diagnostics panel for the current item (player
  state, format, decoder, first frame, errors, surface, last 40 events) and copies it to the
  clipboard, to find why videos stay black in Review (3.2.1's fix did not help).

## Phone 3.2.1 — 2026-10-04 (phone only; server stays 3.2.0)
- Review: videos were a black screen. The video is now hosted like ranking's (no graphics layer or
  clip above its SurfaceView; it slides by layout offset). Unconfirmed until seen on the phone.
- Review: the system back swipe from the screen edge works again (the screen no longer claims a
  system-gesture exclusion; taps and swipes only count from the central live area anyway).

## 3.2.0 — 2026-10-04
- Phone (3.2.0, versionCode 5): Review mode reworked after Mike's first look. Tap keeps, swipe left
  discards, swipe right does nothing; only touches that start inside ranking's centred live area
  count. On screen only the item, ranking's cancel notch and a ⋮ menu (ranking's menu look; long
  press opens it too): Start from the beginning, Back to folders, Discard. No button strip, no
  colours, no status text; non-fatal failures are silent. Next 3 stills prefetched.
- Review position now lives on the PC: `<folder>/.rankmaster_review.json` (hidden), read through
  `GET /session/items` (`reviewPosition`) and written with `PUT /session/review-position`
  (SERVER_SPEC.md § 10.19). Review resumes on the same item; a finished folder starts over.

## 3.1.0 — 2026-10-04
- New: **Review mode** on the phone (phone 3.1.0, versionCode 4). From the folder list, "Review"
  beside "Rank" shows every photo and video of the folder one at a time, full screen, true aspect.
  Swipe left (or Discard) moves it to `discarded/`, swipe right (or Next) keeps it, Cancel takes back
  the last swipe (a discard only while it is the PC's last action). Progress in %, position
  remembered per folder.
- Server: `GET /session/items` (every file of the session plus the snapshot) and
  `POST /session/items/discard` (discard one file by id, same move/undo as the pair discard).
  SERVER_SPEC.md § 10.17, § 10.18. The PC client is unchanged.

## 3.0.6 — 2026-09-19
- PC client: the info card (folder, confidence, unranked, session) is two lines again, larger.
- Filenames moved to the bottom corners and made fainter, out of the card's way.

## 3.0.5 — 2026-09-19
- Tooltips moved to the compare screen: the [1] [4] [5] [2] buttons say what they do.
- Fix: the last-10 match strip stopped updating after the tenth vote.
- Lighter info card.

## 3.0.4 — 2026-09-18
- More transparent info card over the photos.
- Tooltips on the start screen's three buttons.

## 3.0.3 — 2026-09-18
- Fix: videos sat frozen until clicked; the pane now repaints on every decoded frame.

## 3.0.2 — 2026-09-18
- Rename by rank: success shows a green message, not the red error box.
- Fix: Open folder did nothing after a rename.

## 3.0.1 — 2026-09-18
- Fixes from the first real use: a crash from a resource looked up in the wrong tree, and the first
  pair staying blurry.
- Installs into `C:\Utils\RankMaster v3`, not the frozen Rank Master 2 folder; `deploy.ps1` added.

## 3.0.0 — 2026-09-17
- Rank Master 3: server (sole writer of the database, LAN TLS, tray icon), Windows client (Avalonia)
  and Android client. Undo takes back any action; rename by rank journals instead of copying and
  tags new names (`000001-7f3a.jpg`); votes are saved within two seconds. See `README.md`.
