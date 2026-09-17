# Rank Master 2 — PC client

**Status: proposed.** The server is complete and audited (`SERVER_PLAN.md` phase 4, 554 tests); the
phone client is in daily use (`CLIENT_PLAN.md`); the WPF desktop app (`src/RankMaster2.App`) is frozen
and is what this document replaces.

Goal: rank a folder at the PC, on the PC's own screen, with a program that is on screen in well under a
second of double-clicking it — and that cannot corrupt a ranking the phone is also allowed to touch.

`SPEC.md` is the ranking behaviour and does not change. `SERVER_SPEC.md` is the contract and this client
implements it; where this document and either of those disagree, they win. Everything below that is a
number was either measured on the build box (and says so) or is an estimate (and says so). Nothing in
this document has been run on Windows.

---

## 1. The question before the stack: server client, or standalone?

**Decision: the PC client is a client of the local server — for every ranking action — and reads pixels
straight from disk.** It never computes a rating, never picks a pair, never writes `rankmaster_db.json`
in a session. It also never fetches a still or a video over HTTP: the files are on the same machine, so
it opens them itself, exactly as the old app did.

That is a hybrid, and the two halves are argued separately below, because they are decided by different
facts.

### 1.1 Ranking through the server

The case *for* standalone is real and should be stated at full strength: the ranking library is C#,
`Core` / `Ranking` / `Catalog` / `Actions` are plain `net8.0`, and a standalone .NET client would reuse
the very same assemblies the server does. It would not be "a second implementation of the rules"; it
would be a second *host* of the one implementation — which is precisely what the server itself is. A
standalone client needs no pairing, no TLS, no token, no second process, and works with the network
cable pulled. The old app is this design, minus the folder lock.

Against it, three things, in order of weight:

1. **Where the old app's bugs actually lived.** Of the four shipped fixes in `README.md`, one was in
   `Catalog` (1.1.4, save could destroy a library) and one was in the app's own session glue (1.1.1, undo
   left a small library with nothing on screen). The glue — `MainWindow.xaml.cs`, 823 lines deciding when
   to `Drop`, when to `Restore`, what is reserved, what `_busy` means — is the part a standalone client
   would write again from scratch. The server has already written it once, as `SessionRegistry` +
   `SessionEndpoints`, and had it audited by four adversarial passes with a state-machine suite aimed at
   exactly that class of fault. A server client inherits that; a standalone client re-earns it.

2. **The lock.** `SERVER_SPEC.md` § 16 gap 6: two writers to one JSON. A standalone client *can* respect
   `<folder>/.rankmaster.lock` — it is twenty lines and the format is documented in § 10.4 — but it
   respects it by discipline. A server client cannot write the file at all, so the phone and the PC are
   serialised by construction: one session server-wide (§ 7), `423` if something else holds the folder,
   and every mutation behind one semaphore. That is the difference between "we remembered" and "it is
   impossible", and the file it protects is the whole product.

3. **One session model, two screens.** The phone and the PC then share the snapshot (§ 9): the same
   `cues`, the same `progressPercent`, the same undo, the same retry protocol. A folder the owner starts
   at the PC and continues on the sofa is one session, not a hand-off between two engines with two
   ideas of "recent".

The costs of the server route, honestly:

- **The server must be running.** On this PC it already is — `RankMaster2.Tray.exe` is the documented
  way to run it (`SERVER_RUNNING.md` § 9 puts it in the Startup folder). The client checks `GET /ping`
  and, if nothing answers, starts the tray itself and waits for `ready: true`. That is a one-off cost of
  a cold ASP.NET Core start (estimate: 1–2 s, unmeasured) paid only when the server was not resident.
- **The server binds one address — the LAN one — never `0.0.0.0`** (§ 2). So the PC client talks to the
  PC's own LAN address, not `127.0.0.1`. On Windows that traffic never leaves the machine; but if the
  PC has *no* LAN address (cable out, Wi-Fi off) the server cannot bind, and the PC client cannot rank.
  § 8 proposes the one optional server change that removes this.
- **Pairing ceremony for a same-machine client.** Solved without the owner seeing it: the sentinel-file
  channel (§ 10.1.1) exists so a process running as the owner can open a pairing window, and `rm2ctl
  pair --take` already does exactly this in C#. The PC client does the same on first run and stores its
  token. No QR, no typing.
- **Every action is an HTTPS round trip.** Loopback TLS, one small JSON body each way. The dominant
  cost inside that call is `JsonCatalog.Save` — fsync of the whole file — and that cost is identical
  in a standalone client, because it is the same function. Measured here: ~150 ms per vote at 20,000
  records (§ 3.4), ~15 ms at 2,000.

**Startup, specifically.** Neither arrangement is faster *by architecture*. The work between a click on
Resume and a pair on screen is: scan the folder, parse the JSON, pick three pairs, decode two images.
Standalone does it in-process; the server client asks a process that has already done its JIT and has
the code warm, then decodes locally. Measured here, the scan-and-pick half is ~120 ms at 20,000 files
(§ 3.4) — it is not where the seconds go in either design. What the server route *does* buy for startup
is structural: the client binary has nothing in it that must initialise before the first window —
no LibVLC, no catalog, no ranking engine — so it can be small and can start showing the start screen
while it pings.

### 1.2 Pixels from disk, not from `/media`

The phone fetches re-encoded stills because it must. The PC does not:

| | Via `/media/{id}/still` | Read from disk |
|---|---|---|
| Quality | JPEG q85 / WebP q80 re-encode, long edge ≤ 2160 (§ 12.3), sRGB | the file, decoded at pane size, ICC honoured — what the old app shows today |
| First-sight latency | server decode-at-size + encode + write cache + transfer + client decode | one decode-at-size |
| Failure modes | `422 media_decode_failed` when the server's 128 MB `DecodeBudget` refuses (a >32 MP PNG), server cache eviction | the same file the old app decodes |
| Video | `Range` stream of the original over loopback, player buffers over HTTP | the file, opened by the player |
| Server load while the PC ranks | decodes and caches every still | none |

Two rules come with reading the files directly, both inherited from the old app and both enforced in
the client's pipeline (§ 6.3): **release every handle on an id before asking the server to move it**,
and **never treat a failed local read as permission to change state** — the server owns state; the
client's only move on a bad file is to offer discard (§ 6.5).

Nothing in `SERVER_SPEC.md` is violated: the contract governs what crosses HTTP, and the client sends
no `/media` requests at all. The snapshot's `folder` and `pair.left.id` are the whole address.

---

## 2. Decisions, settled before any code

| Question | Answer | Why |
|---|---|---|
| Ranking logic in the client | **none** — every vote, skip, discard, special, undo, save goes to the server | § 1.1 |
| Media bytes | **from disk**, by `folder + id` from the snapshot | § 1.2 |
| Language / runtime | **.NET 8** | rename-by-rank is only available as `RankMaster2.Actions` (C#) and the server does not expose it (§ 6.6); the server, `rm2ctl` and the DTOs are C#; and everything .NET compiles for `win-x64` on the Linux build box — proven today for the WPF app (`EnableWindowsTargeting`), the Tray, and an Avalonia probe |
| UI framework | **Avalonia 11**, with WPF as the named fallback | § 4 |
| Video | **LibVLC 3.0.21 via LibVLCSharp**, software decode, frames into a `WriteableBitmap` — the old app's `VlcFramePlayer` design | the one decoder proven to play this owner's AV1/4K files on this PC; the frame-callback path is what keeps overlays clickable (§ 6.2) |
| When LibVLC initialises | **lazily**, on a background thread, only once a snapshot says `policy: "video"` | it is the leading startup suspect (§ 3) and a stills folder never needs it |
| Stills | **SkiaSharp** decode-at-target-size, EXIF orientation, ICC → sRGB | the same library and the same technique the server's `StillRenderer` already uses; runs here for tests |
| Fullscreen | borderless, covers the taskbar | `SPEC.md` § Screens |
| Keys, chrome, cues, overlay | as `SPEC.md` § Compare UI, § Keys | the product does not change because the plumbing did |
| Rename by rank | **kept, local, under the folder lock, with no session open** | § 6.6 |
| Skip | kept (`↓` / `S`) | `SPEC.md`; the phone dropped it for its own reasons (`CLIENT_PLAN.md` § 3.6.1), the PC has keys to spare |
| Startup target | start screen visible in **≤ 0.5 s** warm; Resume → both panes painted in **≤ 1.0 s** on a stills folder of 20k on SSD | targets, to be replaced by measured numbers in Phase 0 |
| Distribution | a folder: `RankMaster2.exe` + `libvlc\` + natives, next to `tray\` | no installer, no store (`SPEC.md`) |
| Phone client | **untouched** | constraint |
| Server | **untouched** in every required path; one optional change proposed | § 8 |

### 2.1 Deliberately absent

- No rating, score, μ or σ anywhere on the compare screen (`SERVER_SPEC.md` § 9.3).
- No `/media` requests, no still cache, no `mediaVersion` bookkeeping. The disk is the cache.
- No folder browser over `/libraries/*`. The PC has a native folder dialog; `/libraries` is for a phone
  that does not.
- No offline mode. Without the server there is no session; the client starts the server rather than
  ranking alone.
- No settings window, no thumbnails, no filmstrip, no recursive scan, no HEIC — every `SPEC.md` non-goal
  stands.
- No Chromium, no browser shell, no Python — the old `SPEC.md` § Stack exclusions are kept on purpose:
  each of them is a runtime that costs more to start than the program it hosts.
- No auto-update, no telemetry, no crash upload. A crash writes a text file next to the exe.

---

## 3. Where the old app's startup goes

The owner says the app is slow to start. Two fixes have shipped against that (1.1.2, 1.1.3) and nobody
has measured it. What follows is what the code shows, ranked by how much I believe each matters, with
what is *shown* separated from what is *guessed*, and the measurement that settles each.

### 3.1 The timeline

`App.xaml` has `StartupUri="MainWindow.xaml"`, so WPF constructs `MainWindow` before anything is on
screen. In that constructor (`MainWindow.xaml.cs` lines 35–63), **before the window is shown**:

```
InitializeComponent()                 XAML load, styles, ~40 named elements
CreatePipeline()                      one thread
new LibraryActions(...)
_vlc = new VlcRuntime()               Core.Initialize + new LibVLC(...)          ← line 47
CreatePlayer() x2                     two LibVLC MediaPlayers
ApplyExclusiveFullscreen()
RefreshResumeButton()                 one small file read
```

Then the start screen appears. The catalog scan (`RankingSession.Start`) does **not** run at startup; it
runs on the UI thread when Resume or Open is clicked (`BeginSession`, line 139). First decode runs after
that on the pipeline thread.

So "startup" is really two intervals, and the owner may mean either: **T1**, double-click → start screen;
**T2**, click Resume → both panes painted. They have different causes.

### 3.2 Candidates, ranked

**1. LibVLC initialisation on the critical path — shown in code, cost estimated.**
`VlcRuntime` runs `Core.Initialize(nativeDir)` and `new LibVLC(...)` synchronously in the window
constructor, before first paint, for every launch, including a stills-only folder that will never play a
frame. `new LibVLC` is `libvlc_new`, which loads the module bank. The shipped payload is
`VideoLAN.LibVLC.Windows 3.0.21`, restored here today: **320 plugin DLLs, 97 MB, and no `plugins.dat`
cache and no `vlc-cache-gen.exe` in the package**. Without a cache, libvlccore `LoadLibrary`s every one
of the 320 plugins to ask what it is. libvlc 3 writes `plugins.dat` into the plugins folder after a scan
if it can, so on a writable install the *second* launch may read the cache instead — but `publish.ps1`
refreshes the plugin files on every publish, and whether the cache survives that (it is validated by
each file's size and mtime) I cannot tell from here. Estimate: hundreds of milliseconds with a valid
cache, several seconds without; multiplied by Windows Defender scanning 320 freshly written DLLs on the
first launch after a publish. *This is my primary suspect for T1, and it is the shape of the 1.1.2
symptom ("first launch after every published update"), which was attributed entirely to single-file
extraction.*

**2. JIT of a self-contained WPF app with no ReadyToRun — shown in code, cost estimated.**
Neither `RankMaster2.App.csproj` nor `publish.ps1` sets `PublishReadyToRun`. Every method of WPF,
`System.Text.Json`, LibVLCSharp and the app is compiled at first call on every launch. A self-contained
WPF window with no R2R typically shows in the 1–2 s range cold; R2R commonly takes a third to a half off
that. Estimate only.

**3. WPF's first window — inherent, small.** D3D device and composition setup (`wpfgfx_cor3`,
`D3DCompiler_47_cor3` are in the ship list). Estimate 200–500 ms. Not fixable inside WPF; a reason to
weigh the UI stack (§ 4).

**4. The catalog scan — measured, not the culprit on a local SSD.** `JsonCatalog.Scan` is
`Directory.EnumerateFiles` + one `File.GetAttributes` per file + `File.ReadAllText` + one
`JsonSerializer.Deserialize` of the whole file; `Start()` then picks three pairs, each an O(n) pass with
an O(n log n) sort. Measured on this box (ext4, SSD, synthetic library, `rankmaster_db.json` written by
`JsonCatalog` itself):

| Files | JSON | `Scan` | `RankingSession.Start` | one vote (TrueSkill + save + fsync + pick) |
|---|---|---|---|---|
| 2,000 | 447 KB | 9–18 ms | 23 ms | 13–17 ms |
| 20,000 | 4.4 MB | 104–118 ms | 121 ms | 143–160 ms |

NTFS costs more per `GetAttributes` and a USB-2 or network folder costs far more (the plan's risk table
in `SERVER_PLAN.md` § 6 already flags this), so on the owner's real library this must be measured, not
assumed — but on an internal drive it is a tenth of a second, and it affects T2 only.

**5. First image decode — designed for, affects T2 only.** `StillDecoder` reads the header, decodes at
pane size, and takes a 720 px first pass for files over 4 MB. Two decodes on one sequential thread.
Estimate 50–300 ms per large JPEG. Fine.

**Belief:** T1 is dominated by (1) + (2), in that order, with (1) potentially much larger on a fresh
publish; T2 is dominated by first decode and is already reasonable. If the owner's complaint is T1 —
and "startup" usually means the time until *anything* appears — the fix is to take LibVLC off the
critical path and precompile, and neither of those needs a new app. That is why Phase 0 measures the
*old* app first (§ 9): if a repackaged old app satisfies him, this plan should be shortened, not
executed in full.

### 3.3 What settles it — the measurement kit (Phase 0)

All of it runs on the owner's PC, prints numbers, and needs no profiler. I build the tools here.

1. **`Measure-Startup.ps1`** — starts an exe, polls `MainWindowHandle` until non-zero, prints T1 in ms.
   Run three times warm, once after a reboot. Then `Get-ChildItem libvlc\win-x64\plugins\plugins.dat`:
   present or not is half the answer to candidate 1.
2. **`rm2probe.exe`** (net8.0 console, ~150 lines, built here): times `Core.Initialize` + `new LibVLC` in
   a fresh process (run twice: with and without `plugins.dat` present), reports plugin count; times
   `JsonCatalog.Scan` and `RankingSession.Start` on the owner's **real** folder; times a SkiaSharp
   decode-at-2160 of the largest still there; prints all of it in one block for Telegram.
3. **A ReadyToRun publish of the existing app** — packaging only, no code change, produced here today
   (`dotnet publish … -p:PublishReadyToRun=true` from Linux works; the single-file exe is 171 MB against
   the current build's smaller one). Time it with (1) next to the shipped exe. This isolates candidate 2
   from everything else with zero risk to the owner's install.
4. **Two empty windows**, WPF and Avalonia, both R2R, both `win-x64`, timed with (1). This is the number
   § 4 depends on.

Exit criterion for Phase 0: a table with T1 for shipped / R2R / empty-WPF / empty-Avalonia, the LibVLC
init time with and without cache, and the real-folder scan time. Every "estimate" above becomes a
number or is struck out.

---

## 4. The stack

Constraints that bind: runs on Windows 11; plays AV1 up to 4K in mp4/webm/mkv/mov/avi; starts fast;
compiles on Linux, where it is developed, or every change reaches the owner blind; keeps rename
available; honours the `SPEC.md` exclusions (no Chromium, no Python, no browser shell).

| Option | Startup (estimate) | Video | Rename | Build here | Run here | Verdict |
|---|---|---|---|---|---|---|
| **.NET + Avalonia** | JIT ~0.5–1 s; R2R less; NativeAOT ~0.2–0.4 s | LibVLC frames → `WriteableBitmap` (port of `VlcFramePlayer`) | `Actions` reused | **yes** (proven today: Avalonia 11.3.22 + LibVLCSharp 3.10.1 + SkiaSharp 3.119 → win-x64 R2R single-file, 106 MB) | **yes** — the same binary runs on Linux against the real server; headless UI tests possible | **chosen** |
| .NET + WPF (thin) | JIT ~1–2 s; R2R less; no AOT | `VlcFramePlayer` and `StillDecoder` reused verbatim | `Actions` reused | **yes** (proven today) | no | **fallback** |
| .NET + WinForms | lightest managed shell | LibVLC to an HWND is fast but overlays over video hit airspace; frames path same as WPF | `Actions` reused | yes | no | rejected: nothing over Avalonia except a slightly lighter shell, and it gives up running here |
| WinUI 3 | heavy, packaging | MediaPlayerElement lacks AV1 without a Store codec | `Actions` | partly | no | rejected |
| Native C++ / Rust + ffmpeg/dav1d | ~50 ms | own decoder stack | reimplement two-phase rename | cross-compile possible, ffmpeg for Windows is a project | no | rejected: months of unverifiable work to save a few hundred ms that R2R/AOT also saves |
| Go / Flutter / Electron / Tauri / Python-Qt | varies | mpv / libmpv / Chromium | reimplement | Flutter Windows needs Windows; Electron/Python excluded by `SPEC.md` | partly | rejected |

**Why Avalonia over WPF, when WPF would reuse ~1,500 lines of debugged pane, decoder and video code:**
because those lines are the part I can *only* verify on the owner's screen, and the compare surface is
where the old app's visible bugs were (leftover tints, sticking storyboards, help popup re-layout). With
Avalonia the identical binary runs here under X, against the real server, with real photographs and
real AV1 files, and I see the panes before he does. That is worth a rewrite of the surface. The client
layer underneath (§ 5) is UI-agnostic and moves to WPF unchanged if the fallback is taken.

**The fallback is a gate, not a mood.** WPF is taken if, in Phase 0, the empty Avalonia window is not
at least as fast to appear as the empty WPF window on the owner's PC; or if in Phase 3 two rounds
cannot make Avalonia go truly fullscreen over the taskbar, or cannot paint two LibVLC panes at pane size
without visible stutter on a 4K AV1 pair.

**Startup engineering, in order of expected return:**

1. Nothing native initialises before the first frame. LibVLC starts on a background thread when the
   first video snapshot arrives; SkiaSharp's native library loads on the first decode, after the start
   screen is up.
2. `PublishReadyToRun=true`, `TieredCompilation` on, `TieredPGO` on. Producible from Linux (proven).
3. NativeAOT — the largest single win, but **cross-OS AOT compilation is not supported**: a `win-x64`
   AOT binary must be compiled on Windows. The route is a GitHub Actions `windows-latest` job on the
   existing repository. Unproven for this repo, and LibVLCSharp's AOT/trim compatibility is unverified;
   listed as Phase 4, taken only if Phase 0's numbers say R2R is not enough.
4. Trim `libvlc\plugins` to what the library needs (demux, packetizer, codec, video_chroma,
   video_output, video_filter, access, misc) — fewer DLLs to scan and to Defender-scan — and ship a
   `plugins.dat` generated on the owner's machine once (`vlc-cache-gen.exe` from a VLC install, or let
   libvlc write it on first run and keep it across publishes by not overwriting the plugins folder when
   its version has not changed). Measured, not assumed, in Phase 0.

---

## 5. Architecture

```
src/RankMaster2.Pc/                      net8.0, Avalonia, win-x64 publish; runs on Linux for tests
  Net/                                   the server contract, one method per endpoint
    Rm2Client.cs                         HttpClient + pinned fingerprint + bearer token + § 4 envelope
    Snapshot.cs                          SessionSnapshot, Pair, MediaRef, Counts, LastAction (§ 9) — source-generated JSON
    Pairing.cs                           sentinel file → offer → POST /pair → stored credential (§ 10.1.1)
    ServerLauncher.cs                    /ping, else start RankMaster2.Tray.exe, wait for ready
  Session/                               snapshot in, intent out — no rules
    SessionDriver.cs                     open (DELETE then POST), vote/skip/discard/special/undo/save with the § 13.3 retry protocol
    LastFolderStore.cs                   the old app's %LOCALAPPDATA%\RankMaster2\last-folder.txt, reused so Resume carries over
  Media/                                 pixels; the only code that opens a media file
    MediaPipeline.cs                     sequential reader, PrefetchPairs = 2, evict on Show — the old class, without WPF types
    StillDecoder.cs                      SkiaSharp decode-at-size, EXIF, ICC → sRGB, 720 px first paint over 4 MB
    VlcRuntime.cs / VlcFramePlayer.cs    the old classes, dispatcher and bitmap types swapped for Avalonia's
    HandleRelease.cs                     stop players, drop bitmaps, FileOps.WaitUntilUnlocked — before every move
  Rename/                                RankMaster2.Actions.RenameByRank under an exclusive .rankmaster.lock, session closed
  Ui/                                    StartView, RankView (two panes, overlay, strip, help, toasts), RenameView
tests/RankMaster2.Pc.Tests/              net8.0; the driver against the real server on this box; pipeline and decoder against fixtures; Avalonia.Headless for the views
```

Project references: `RankMaster2.Core` (types), `RankMaster2.Catalog` + `RankMaster2.Actions` +
`RankMaster2.Ranking` **for the rename path only** — `Actions` needs `RankingSession`'s type to compile.
A review item in Phase 5 confirms that no type from `Ranking` is constructed anywhere but `Rename/`.

**One `HttpClient`, one pin, one token** — the same rule the phone lives by (`CLIENT_PLAN.md` § 2), for
the same reason.

---

## 6. The hard problems, and how they are solved

### 6.1 Finding, trusting and pairing with a server on the same machine, silently

- **Address.** The credential file stores `baseUrl`, `fingerprint`, `token`, `deviceId`. On first run
  there is none: the client writes `<data>/pair.request`, waits for `<data>/pairing.json`, and takes
  `host`, `port`, `fingerprint` and `code` from the offer — the server publishes its bound address there,
  so the client never guesses between `127.0.0.1` and a LAN address. `<data>` is
  `%LOCALAPPDATA%\RankMaster2\server`, the same resolution `rm2ctl` and the Tray use.
- **Trust.** `HttpClientHandler.ServerCertificateCustomValidationCallback` accepting exactly the pinned
  SHA-256 of the peer's DER — `rm2ctl`'s `Rm2Api.cs` already does this in C#; it is copied, not rewritten.
  Pin before sending the code (§ 10.1.1). A mismatch is a hard stop with a plain sentence, never a
  "continue" button.
- **Pair.** `POST /pair { code, deviceName: "PC" }` → token, stored once. The window is single-use with a
  5-attempt budget; a failed first run asks for a new window rather than retrying the code (§ 13.3).
- **Not running.** `/ping` unreachable → start `RankMaster2.Tray.exe` (default `..\tray\` beside the
  client, overridable in `%LOCALAPPDATA%\RankMaster2\pc\settings.json`) → poll `/ping` until `ready`.
  The Tray's single-instance mutex makes a race harmless. If it still does not answer in 10 s, the start
  screen says so in one line and offers to try again. There is no standalone mode to fall back to, on
  purpose (§ 1.1).
- **Address changed** (new DHCP lease, new adapter): the server fails to bind, the client fails to
  connect, and the fix is the server's configuration, not the client's. Same policy as the phone
  (`CLIENT_PLAN.md` § 1: "Recovering from a changed PC address — not built").

### 6.2 Video that plays this owner's files, with clickable chrome on top

Two designs have died on video already. This one changes nothing that is known to work:

- **LibVLC 3.0.21**, software decode (`--avcodec-hw=none`), the same options as `VlcRuntime.cs`. It is
  the decoder that plays the library today.
- **Frames into a bitmap, not a native child window.** `LibVLCSharp.Avalonia`'s `VideoView` embeds an
  HWND that VLC paints directly — fast, and the reason the old app rejected the equivalent WPF control:
  nothing Avalonia draws can sit on top of it (airspace), so the info card, filenames, buttons and the
  select cue would need a second, layered window. `VlcFramePlayer`'s RV32 callbacks → back buffer →
  `WriteableBitmap.Lock()` → `Image` keeps every overlay an ordinary control. At two panes of
  1920 × 2160 that is two ~16 MB copies per frame per pane; the old app does exactly this and the owner
  has not complained about playback. If Phase 3 measures stutter on a 4K pair, the HWND path is the
  fallback for the *panes only*, with overlays moved to a transparent top-level window.
- **At most two live players**, warm pairs hold paths only (`SPEC.md` § Pipeline). `Fit` asks VLC for
  pane-sized output, so it scales, not Avalonia.
- **Lazy, off-thread init.** `VlcRuntime` is created the first time a snapshot has `policy: "video"`, on
  a thread pool thread, with both panes showing the spinner the spec already allows for video. A stills
  folder never pays for it.
- **Release before move** — § 6.3.
- **Not built:** poster frames, probing, transcoding (`SERVER_PLAN.md` § 7 stands and this client needs
  none of them).

### 6.3 Handles, and the server's `File.Move`

The server moves the file on discard/special (§ 10.8) and on undo of a move (§ 10.10). A file the client
holds open cannot be moved on Windows, and the server answers `500 move_failed` after 20 × 50 ms of
retrying. The old app solved this with `Pipeline.Release` → `FileOps.WaitUntilUnlocked` → move, and so
does this one, in the same order, on the client side of the HTTP call:

```
stop the pane's player if it is playing this id   (VLC holds the file)
drop the pane's bitmap and any cached frame       (Skia streams are already closed after decode)
FileOps.WaitUntilUnlocked(path)                   (≤ 2 s; the old app's loop)
POST /session/discard { pairToken, side }
```

A `500 move_failed` after that is shown as a toast and the pair stays; the token is still valid (§ 8.3),
so the owner can simply press the key again.

### 6.4 Never voting twice

Solved by the server; the client's job is to not break it (`SERVER_SPEC.md` § 13.3, `CLIENT_PLAN.md`
§ 3.2). `SessionDriver` holds the in-flight `pairToken` and `clientRequestId` until a definite answer,
retries a timeout with the **same** token, and adopts the snapshot from a `409 stale_pair_token`
without ever re-sending. Keys are ignored while an action is in flight, exactly as the old app ignores
them while a move is in flight. Loopback makes timeouts rare; the protocol is kept anyway, because the
server can be killed from the tray mid-vote and § 13.4 says what the client may then know: nothing.

### 6.5 A file that will not decode, and a file that is gone

The old app called `Drop` from the UI thread when a decode failed. Under the server the client cannot
drop anything without moving it, and § 11.3 is explicit that the client decides. So:

- `sizeBytes: null` in the snapshot, or the local open fails with *not found*: the pane shows "file is
  gone" and the only key it accepts is that side's discard, which the server turns into `drop_missing`
  (§ 10.8). No undo entry, as specified.
- The file exists but Skia or VLC cannot decode it: the pane says so and accepts discard (ordinary, with
  undo) or a vote for the *other* side if the owner can judge from the filename. It does not auto-skip:
  that would count an impression nobody chose (`SPEC.md` § Ranking).

This is a visible change from the old app, which silently dropped the id and moved on. It is the
smaller sin: the old behaviour hid the existence of a corrupt file from the one person who could fix it.

### 6.6 Rename by rank without a rename endpoint

> **Superseded** — rename is the server's (`PC_CLIENT_PARTS.md`, 2026-09-16); the PC client calls
> `POST /session/rename` (part G, PC-RENAME). Everything below described a local rename under the
> folder lock, which `PC_CLIENT_PARTS.md` rejected as "a second writer wearing a disguise". It is kept
> for the reasoning, not as the design. The server's rename is `SERVER_SPEC.md` § 10.16: journalled,
> no backup copy, names `NNNNNN-ssss.ext`, one move per file, with a progress bar and a Cancel the
> client shows from the first instant — the owner's requirement for a slow USB drive.

The server refuses rename forever (§ 1.1). The PC keeps it, the way the old app has it — from the start
screen, with confirmation, backup first — as a **local, exclusive, offline** operation:

```
DELETE /session                       if this client holds the folder (404 is fine)
acquire <folder>/.rankmaster.lock     FileMode.OpenOrCreate, FileShare.None — the server's own convention (§ 10.4)
RankMaster2.Actions.RenameByRank      the shipped implementation, no-op pipeline, null session
release the lock
```

While the client holds the lock, a phone that tries the folder gets `423 folder_locked`, which
`CLIENT_PLAN.md` § 3.6.3 already treats as the one conflict worth a message. `RenameByRank` scans, saves,
backs up, renames in two phases and rewrites the JSON — the same code, so no second implementation of a
destructive operation. If the lock cannot be taken (the server has the folder open for the phone), the
client says so and does nothing.

**Owner decision (§ 13):** confirm rename is still used. If it is not, `Rename/` and the three
`Ranking`/`Catalog`/`Actions` references go, and the client depends on `Core` alone.

### 6.7 Undo

The old app's `Ctrl+Z` undoes the last *move* only, and can reach a move made several votes ago
(`LibraryActions.LastMove` is only cleared by undo). The server's `POST /session/undo` cancels the
**last action of any kind**, one level (§ 10.10). A server client cannot have the old behaviour: after
discard → vote, undo reverses the vote and the discard is out of reach.

**Decision: `Ctrl+Z` = `POST /session/undo`, server semantics** — the same cancel the phone has, gated
on `undoAvailable`. This is the one place the PC's behaviour differs from `SPEC.md` § Keys as written;
it is a key mapping, not a ranking rule, and § 13 asks the owner to confirm it. The strict alternative
(send undo only when `lastAction.type` is `discard` or `special`) is one condition and is offered there.

### 6.8 Esc, Resume, and one session for two screens

- **Esc quits immediately** (`SPEC.md`). The client fires `DELETE /session` with a 500 ms timeout and
  exits without waiting for more; the server writes nothing on close (§ 10.4). A crash that skips the
  DELETE is harmless: the next open resumes or replaces the session.
- **Resume does not restore a pair** (`SPEC.md` § Screens). Open is therefore `DELETE /session` (ignore
  404) then `POST /session`, so `Start()` runs, cues and session votes reset, and a fresh pair is
  picked — which also resolves `409 session_already_open` from a folder the phone left open, silently,
  the way the phone resolves it (`CLIENT_PLAN.md` § 3.6.3). One user, never both at once.
- **Both screens on one folder** is legal and self-healing: whichever acts second gets `409
  stale_pair_token` with the truth attached.

### 6.9 What the client must never do

1. Never compute a rating, a score, a pick, or an impression. Never construct `RankingSession` outside
   `Rename/`.
2. Never write `rankmaster_db.json` except through `RenameByRank`, under the lock, with no session open.
3. Never act on a warm pair; never send `pairSeq`; never retry with a fresh token.
4. Never open a media file for longer than a decode, except the two live players — and never hold one
   across a discard/special/undo request.
5. Never show μ or σ on the compare screen.
6. Never start ranking without a server. There is no local engine to fall back to, by design.

---

## 7. Screens and keys

| Screen | What it does | Contract |
|---|---|---|
| **Start** | title + version; Open folder (`O`, native dialog); Resume / Rename when `last-folder.txt` is valid; one-line status if the server is being started or cannot be reached; errors in the red box | `SPEC.md` § Screens; § 10.1 |
| **Ranking** | two panes, filenames, info card (folder, confidence %, unranked, session), `[1][4]` / `[5][2]` buttons, match strip from `cues`, help `?`/`F1`, toasts, select cue | `SPEC.md` § Compare UI; § 9 |
| **Renaming** | progress line while `RenameByRank` runs; result toast or error | `SPEC.md` § Rename |
| **Exhausted** | "No pair left to compare" back on the start screen | § 7.1 |

Keys are `SPEC.md` § Keys, mapped, **with one deliberate omission**: `←`/`→` → vote; `1`/`2` →
discard; `4`/`5` → special; `Ctrl+S` → `POST /session/save`; `Ctrl+Z` → § 6.7; `Esc` → § 6.8; `O`,
`F1` local.

**The PC client does not offer skip.** `↓` and `S` are mapped to nothing, and the help sheet does not
list them. This is the owner's decision (2026-09-17): he does not use it, and the phone dropped it
already. It is a change to this client's surface only — the server keeps `POST /session/skip`, its
error codes and every test of it (`SERVER_SPEC.md` § 10.7), the link layer keeps `SkipAsync` and the
link tests keep exercising the endpoint, and the frozen desktop app keeps `↓`/`S`. Nothing about the
contract moves.
Panes accept clicks only when both are ready (still has pixels; video has painted a frame), as now.

---

## 8. Server changes

**Required: none.** Every path above is served by the contract as it stands, and the phone needs no
change.

**Proposed, optional, one:** listen on loopback *in addition to* the configured LAN address.

- *What:* a second `kestrel.Listen(IPAddress.Loopback, port, UseHttps(cert))` in
  `SecurityEndpoints.ConfigureTransport`; `SERVER_SPEC.md` § 2 "Bind address" gains "and loopback";
  `SERVER_RUNNING.md` § 3 gains a sentence. Clients pin by fingerprint, not name, so the certificate is
  untouched.
- *Why:* today the PC client's connection depends on the PC holding a LAN address at the moment the
  server starts. Cable out, Wi-Fi off, or a DHCP change means no server and therefore no ranking at the
  PC — a dependency the standalone design would not have had. Loopback removes it, and lets the PC
  client's stored `baseUrl` be `https://127.0.0.1:18611` forever.
- *Cost:* it widens nothing — loopback is reachable only from the machine — but it is a change to an
  audited transport layer: the conformance and security suites must be re-run, and the
  `Rm2SecurityOptions` documentation ("the single IP address Kestrel binds") corrected.
- *Decision rule:* do it only if the owner says the PC is ever without a LAN address while he ranks. If
  it is always wired, skip it and store the LAN `baseUrl` from the pairing offer.

**Refused:** a rename endpoint (product decision, § 1.1); poster frames, probing, transcoding
(`SERVER_PLAN.md` § 7); any change to the snapshot or the error envelope.

**Paperwork, not behaviour:** `SPEC.md` § Stack and § Shell describe the WPF app and will need to
describe this one; the sentence "The desktop app does not call [`UndoLastAction`]" becomes false under
§ 6.7. Those edits are made in the same change as Phase 2 lands, after § 13 is answered.

---

## 9. Phases

**Phase 0 — measure, before anything is built.** The § 3.3 kit: `Measure-Startup.ps1`, `rm2probe.exe`,
an R2R publish of the existing app, and two empty windows (WPF, Avalonia). All built here, sent to the
owner as one zip with one paragraph of instructions, results back as one pasted block. Exit: the § 3.3
table. Decision points it feeds: is the old app fixable by repackaging alone (if so, ship that as 1.1.5
and stop here); WPF or Avalonia (§ 4 gate); is candidate 1 the culprit.

**Phase 1 — the client layer, alone, against the real server on this box.** `Net/` and `Session/`:
every endpoint, the envelope, pinning (copied from `rm2ctl`), sentinel pairing, the retry protocol,
`ServerLauncher`. Tested against the real server here — not mocks — including kill-mid-vote. Exit: a
console driver completes the `rm2ctl cycle` equivalent through `SessionDriver`, and every § 13.3 row
has a test.

**Phase 2 — the compare surface, stills only.** `Media/MediaPipeline`, `StillDecoder` (Skia), `Ui/`.
Runs here under X against a scratch folder of real photographs including EXIF-rotated and Adobe RGB
ones; Avalonia.Headless tests for key handling, ready-gating, the strip, the cue. Publish `win-x64` R2R
to the owner. Exit: he ranks 50 pairs of stills at the PC; T1 and T2 measured with the Phase 0 script.

**Phase 3 — video.** `VlcRuntime`/`VlcFramePlayer` ported, lazy init, release-before-move, two live
players. Runs here with LibVLC for Linux against AV1 files to prove the frame path; the Windows LibVLC
binaries can only be proven on his PC. Exit: a 4K AV1 pair plays side by side; discarding a playing
video moves the file first time; § 4 fallback gate evaluated.

**Phase 4 — startup engineering, to the measured number.** Whatever Phase 0 said: plugin trim and
`plugins.dat`; R2R tuning; NativeAOT via a Windows CI job if R2R is not enough. Exit: T1 ≤ 0.5 s warm on
his PC, or a written reason it cannot be.

**Phase 5 — rename, and the adversarial pass.** `Rename/` under the lock (if § 13 keeps it). Then two
reviews, as the server and the phone had: one on the retry/handle-release ordering (the double-vote and
`move_failed` surfaces), one confirming no ranking type is constructed outside `Rename/` and no media
file is held across a request.

Phases 1 and 2 can overlap; 3 waits for 2; 4 waits for the numbers; 5 is last.

---

## 10. Acceptance gate

The PC client is done when, on the owner's PC, with the phone client unchanged and installed, and the
old `RankMaster2.exe` never launched:

1. Double-click to start screen in **≤ 0.5 s** warm, measured by `Measure-Startup.ps1`, and no slower
   on the first launch after a publish than on the tenth.
2. With the tray **not** running, the client starts it and is ranking within 10 s, with no dialog.
3. Resume on his largest stills folder: both panes painted within **1.0 s** of the click (T2), and no
   spinner when pixels can be shown.
4. Rank 200 pairs: vote, skip, discard, special, undo, `Ctrl+S`; `rankmaster_db.json` correct after
   each, and opening the same folder on the phone afterwards shows the same counts and ratings.
5. A video folder: a 4K AV1 pair plays, looping, muted, both at once; discarding the left one while it
   plays moves the file on the first keypress.
6. Exit the tray mid-vote from its menu: the client shows what happened in one line, the PC's next open
   resumes, and the JSON shows no double-counted vote.
7. Open a folder the phone has open: the PC takes it silently; the phone's next action is answered with
   the current state and does not vote twice.
8. `Esc` at any moment: the process is gone within a second, the tray shows "No folder open", nothing
   was written.
9. Rename by rank on a copy of a real folder produces the backup, the `000001…` names and a JSON that
   both the phone and this client open with ratings intact — **or** § 13 has removed rename.
10. Everything in § 6.9 is enforced by a test or a review finding, not by intention.

Points 1 and 4 are the ones that matter: the first is why this client exists, the second is the
compatibility contract every piece of this project has had to meet.

---

## 11. Risks

| Risk | Mitigation |
|---|---|
| The owner's complaint is fixable by repackaging the old app (R2R + LibVLC off the critical path) and this plan is oversized | Phase 0 measures the old app first; the plan says stop if a 1.1.5 repackage satisfies him |
| Avalonia does something on his Windows 11 that it does not do here (fullscreen, DPI, fonts, D3D) | § 4 gate: WPF fallback with the client layer unchanged; icons as vector paths rather than `Segoe MDL2 Assets` glyphs to remove one font dependency |
| LibVLC frames into an Avalonia `WriteableBitmap` stutter at 2 × 4K | HWND `VideoView` for the panes only, overlays in a layered window; measured in Phase 3 with VLC's dropped-frame statistics |
| A held handle makes the server's move fail | release-before-move (§ 6.3), a test that discards a *playing* video, a Phase 5 review item |
| The server is on a LAN address the PC does not have today | § 8's optional loopback listener; otherwise the same policy as the phone |
| Pairing from the PC consumes the phone's open window or its attempt budget | the client opens its own window via the sentinel and pairs within seconds; one window is live at a time by the server's design, so the client waits if `pairing.json` predates its request |
| `Ranking.dll` in the client tempts a local shortcut | reference exists for `Rename/` only; a review greps for `new RankingSession` and `PairSelector` outside it; removed entirely if rename is dropped |
| NativeAOT impossible from Linux | R2R first (proven from Linux); AOT via a Windows CI runner only if measured necessary |
| Per-vote fsync of a 4.4 MB JSON at 20k records is ~150 ms here | not startup, not this client's to fix, identical in every arrangement; noted so nobody attributes it to HTTP |

---

## 12. What could not be determined without Windows

Stated so nobody mistakes an estimate for a measurement:

- **Every number in § 3.2 except the scan table.** LibVLC init time with and without `plugins.dat`;
  whether `plugins.dat` currently exists in `C:\Utils\rank-master-2\libvlc\win-x64\plugins\`; JIT and
  WPF first-window cost; Defender's share of a fresh publish's first launch. Phase 0 exists for these.
- **Whether libvlc 3.0.21 on Windows writes `plugins.dat` after a scan** into a user-writable folder, and
  whether `publish.ps1`'s `robocopy /MOV` invalidates it. Read from VLC's behaviour, not observed.
- **Startup of an empty Avalonia versus an empty WPF window on his hardware.** The whole § 4 gate.
- **Fullscreen over the taskbar, per-monitor DPI, and `WriteableBitmap` throughput in Avalonia on
  Windows 11.** Avalonia documents all three; none observed here.
- **LibVLCSharp under NativeAOT.** Unverified; only relevant to Phase 4's last step.
- **That the WPF app still runs after today's Linux compile.** It compiled with 0 errors and published
  as R2R; nothing here can execute it. The same is true of the Avalonia probe.
- **The real-folder scan time on his drive** (NTFS, and whether the library is on an internal SSD or a
  USB/network volume). Measured here on ext4 only.
- **Windows-specific file-handle behaviour** — that `WaitUntilUnlocked` succeeds after `Stop()` with this
  LibVLC build. It does in the old app, which is the evidence.

---

## 13. Decisions for the owner

**All six are answered. Nothing here is open.**

1. **Which startup annoys you** — the time until the start screen appears, or the time from clicking
   Resume until the pictures are up? (§ 3.1; it changes what Phase 0 looks at first.)
   **Answered: both** (2026-09-16, `PC_CLIENT_PARTS.md`). The measurement kit measures both and the
   five startup marks say which of the two is costing what, rather than the plan guessing.
2. **Rename by rank** — do you still use it? Keep (local, under the lock) or drop.
   **Answered: keep — but on the server, not locally** (2026-09-16, `PC_CLIENT_PARTS.md`). A local
   rename under the folder lock was rejected as a second writer wearing a disguise. The client calls
   `POST /session/rename` (§ 6.6, superseded note).
3. **`Ctrl+Z`** — cancel the last action of any kind, or strictly the last discard/special?
   **Answered: any action** (2026-09-16). One level, as on the phone — a mis-hit arrow key is a real
   vote and there has to be a way back from it.
4. **Is the PC ever ranking without a network connection?** **Answered: no** (2026-09-16). The § 8
   loopback change is not made.
5. **Skip on `↓`/`S`** — keep it on the PC, or drop it as the phone did?
   **Answered: drop it** (2026-09-17, the owner: he does not use it). Removed from this client's
   surface; the server's `POST /session/skip` and all of its tests stay exactly as they are (§ 7).
6. **Should the PC ever start the server itself**, or the tray in the Startup folder and a plain
   "server is not running" message? **Answered: start it** (2026-09-16).
