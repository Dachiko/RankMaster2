# Rank Master 2 — PC client, part D: video

**Status: proposed.** Plan only; no application code exists for this part yet. The reader is the
agent who will build it, and the plan is written so that nothing below needs re-deciding.

Owns `pc/src/RankMaster2.Pc/Video/`. Owns the `IVideoSurface` seam (D → E) and shares `IMediaProbe`
(C, D → A). Touches no other folder; where this plan needs something from parts A or E it says so in
§ 9 and § 10 and does not build it.

What to have read before starting, in this order: `PC_CLIENT_PARTS.md` (the division of labour and the
frozen seams), `PC_CLIENT_PLAN.md` § 2, § 3.2 candidate 1, § 6.2, § 6.3, § 6.5 (the architecture and
the video decision), `src/RankMaster2.App/Playback/VlcFramePlayer.cs` and `VlcRuntime.cs` (the working
prior art — this plan is a port of it, not a replacement), `android/MEMORY_PROPOSALS.md` § 1–2 (what
two simultaneous decoders and a default buffer allowance did on the phone), `SPEC.md` § Media policy
and § Compare UI (which files are video, and "spinner until a frame can play, then autoplay, loop,
muted, no controls").

Every number in this document is either **measured on the build box** (an i5-13500, 20 threads,
Linux, ffmpeg's dav1d and svt-av1 — and says so) or an **estimate** (and says so). Nothing in this
document has run on Windows.

---

## 1. Decisions, settled before any code

| Question | Answer | Why |
|---|---|---|
| Decoder | **LibVLC 3.0.21 via LibVLCSharp 3.10.1**, software decode | the only stack proven to play this owner's AV1 files on this PC; the same NuGet packages the old app ships (`VideoLAN.LibVLC.Windows 3.0.21`) |
| AV1 decoder inside LibVLC | **dav1d** (`libdav1d_plugin`), never libaom | measured here: dav1d 86 fps on a 4K30 clip, libaom 20.6 fps on the same clip (§ 4.3). libaom is not shipped |
| Frames reach the screen by | **frame callbacks into a `WriteableBitmap`** (`vmem`), the old `VlcFramePlayer` design — not a native child window | § 4.1: overlays must draw over the pane, and the last decoded frame is then ours for free, which § 4.3's fallback needs |
| Output pixel format | `RV32` from VLC, pane-fitted by VLC, into an Avalonia `Bgra32` / `AlphaFormat.Opaque` bitmap | the old app's `RV32` → `Bgra32` pairing is proven; VLC does the 4K → pane scaling so the process never holds a 4K RGB frame |
| Hardware decoding | **off** (`--avcodec-hw=none`, `EnableHardwareDecoding = false`) | as the old app; a GPU frame would need a readback to reach a bitmap, and software is measured to be enough (§ 4.3) |
| When LibVLC initialises | **lazily, on a worker thread, the first time a surface is asked to play — and never before** | § 4.2. The engine's constructor does no native work; a stills folder never loads `libvlc.dll` and a test proves it |
| How many players may exist | **two**, the factory throws on a third | `MEMORY_PROPOSALS.md` § 3.4.1: "three players is one too many" is the phone's most expensive lesson |
| Two 4K AV1 decoders at once | **yes, by default**; a measured fallback (§ 4.3) puts one pane on hold when the lost-frame rate says the CPU cannot keep up | two decoders cost ~2× one, and on this box that is 70 fps each with pane scaling; on a 4-logical-CPU stand-in it is 40 fps each — viable, but with no margin, so the fallback exists |
| Fallback when it cannot | **alternate**: one pane plays, the other holds its last frame; swap every clip length | the phone's option B, for the phone's reasons; no gesture to learn, both panes still move |
| Who owns a player | **the pane's `IVideoSurface` owns exactly one `MediaPlayer`**; part E owns the surface and disposes it when the pane goes | § 4.4. A player that outlives its pane killed the phone twice |
| Releasing a file before the server moves it | `StopAsync()` completes only when the file can be opened `FileShare.None` | `PC_CLIENT_PLAN.md` § 6.3; the old `Pipeline.Release` → `FileOps.WaitUntilUnlocked` loop, carried over |
| Looping | `:input-repeat=65535` **and** an `EndReached` → restart posted to the UI thread | the old app's belt and braces; both kept because both are proven |
| Sound | `:no-audio`, `Mute`, `--aout=dummy` | `SPEC.md`: muted, no controls |
| What "will not play" means | a closed set of six failure kinds (§ 4.5), each mapped from a specific LibVLC signal or timeout, with the last LibVLC log lines attached | part E shows a sentence and offers discard; nothing is ever auto-skipped and nothing is ever moved by this part |
| LibVLC calls run on | **one dedicated worker thread per engine**; callbacks post to the UI thread; nothing calls LibVLC from a callback or from the UI thread | § 4.6: `Stop()` blocks for as long as the decoder takes to wind down, and LibVLC 3 deadlocks when a callback calls back in |
| The seam's frame type | Avalonia `Bitmap` (a `WriteableBitmap` underneath) | E binds it to an `Image` directly. If `PC_CLIENT_PLAN.md` § 4's WPF gate fires, this one property changes type and nothing else in the seam does |
| Where the LibVLCSharp reference lives | **one file**, `Backend/LibVlcBackend.cs`, behind `IPlayerBackend` | everything else — state machine, lifetime rules, pane fit, failure mapping, the probe — is plain C# and runs under xunit on Linux |
| Test environments | pure tests here always; real-LibVLC tests in a Debian container here; Windows only on the owner's PC via `rm2vidprobe` | § 6. This is the least verifiable part and the plan separates what can be proven where |

### 1.1 Deliberately absent

None of these are oversights:

- **No native `VideoView` / HWND child.** `LibVLCSharp.Avalonia`'s `VideoView` is exactly the control
  the old app rejected in its WPF form: VLC paints an HWND that nothing Avalonia draws can sit on top of.
  It is the named fallback of `PC_CLIENT_PLAN.md` § 4 for the *panes only* if Phase 3 measures stutter,
  and it is not built until that measurement exists.
- **No hardware decoding**, no D3D11VA, no DXVA2 — measured unnecessary, and the plugins are not shipped.
- **No poster frames, thumbnails, seeking, scrubbing, playback rate, audio, controls, or on-screen text
  from VLC.** `SPEC.md` § Compare UI and `SERVER_PLAN.md` § 7.
- **No prefetch of video.** Warm pairs hold paths only (`SPEC.md` § Pipeline). Opening a local file and
  decoding its first frame is a fraction of a second (§ 4.2); a third decoder is the thing that must not
  exist.
- **No probing of file contents to answer "does this folder contain video".** `SPEC.md` § Media policy
  classifies by extension; so does `IMediaProbe`. It never opens a file.
- **No HDR handling.** A 10-bit AV1 file is converted to 8-bit by VLC's swscale with no tone-mapping; PQ or
  HLG content will look flat. Recorded, not solved.
- **No LibVLC teardown on Esc.** `SPEC.md`: Esc quits the process immediately. `LibVLC.Dispose()` can
  take hundreds of milliseconds to join decoder threads; the process exits instead.
- **No third `MediaPlayer`, ever, for any reason** — not for prefetch, not for a poster, not for a probe.

---

## 2. Architecture

```
pc/src/RankMaster2.Pc/Video/
  IVideoSurface.cs          the D → E seam, exactly as § 3.1 (interfaces, enums, records; no logic)
  MediaProbe.cs             IMediaProbe over RankMaster2.Core.MediaExtensions — extension only, pure
  VideoEngine.cs            IVideoSurfaceFactory: lazy LibVLC, the worker thread, the two-surface cap,
                            the LoadWatch timer, the alternate-mode coordinator
  VideoSurface.cs           IVideoSurface: the state machine, generation counter, events on the UI thread
  PaneFit.cs                pure: fit a source size into a pane, even dimensions, 32-byte pitches
                            (the old Fit / Align32, unchanged)
  FrameStore.cs             pure with respect to VLC: the native buffer + front/back byte[] + the
                            one-pending-present flag (the old buffer code, plus § 4.1's coalescing)
  FailureMapper.cs          pure: (parse status, event, elapsed) → VideoFailure
  LoadWatch.cs              pure: lost-frame samples → "hold one pane" / "keep both"
  VideoLog.cs               ring of the last 100 LibVLC log lines, attached to every failure
  VideoOptions.cs           every tunable in one record with the defaults from this document
  Backend/
    IPlayerBackend.cs       the thin seam over LibVLCSharp: Initialize, CreatePlayer, Parse, Play,
                            Stop, SetPause, Statistics, Length, the four callbacks, the three events
    LibVlcBackend.cs        the ONLY file in Video/ with `using LibVLCSharp.Shared`
    LibVlcOptions.cs        the engine and per-media option strings (§ 5.2), nowhere else
    FakeBackend.cs          lives in the test project, not here — listed so nobody puts a fake in src

pc/tests/RankMaster2.Pc.Tests/Video/     D's tests (the .csproj is part A's; § 9 lists what it needs)
pc/tests/fixtures/video/                 generated, git-ignored 4K AV1 clips (§ 6.2's script)
pc/tools/rm2vidprobe/                    the Windows-side proof, a net8.0 console (§ 6.4)
```

Layering rule, enforced by a review grep in Phase 4: `LibVLCSharp` appears in `Backend/LibVlcBackend.cs`
and `Backend/LibVlcOptions.cs` and in no other file under `Video/`. Avalonia types appear in
`IVideoSurface.cs` (the `Bitmap` property), `VideoSurface.cs` (creating and locking the
`WriteableBitmap`) and nowhere else — the UI-thread dependency is an interface, `IUiThread { void
Post(Action); bool IsOnThread }`, so tests run the state machine synchronously.

Ownership at run time:

```
VideoEngine (one per process, created by A's composition root, does nothing until asked)
  └─ LibVLC instance          created on first WarmUpAsync / Create, on the worker thread, never disposed
  └─ worker thread            every LibVLC call goes through its queue
  └─ live surfaces (≤ 2)
       VideoSurface "left"    owned by E's left pane
         └─ MediaPlayer       exactly one, for the surface's lifetime
         └─ FrameStore        one native buffer + two managed copies, allocated at OnFormat, freed at Stop/Dispose
         └─ WriteableBitmap   allocated at OnFormat, replaced only when the size changes
       VideoSurface "right"   the same
```

---

## 3. The seams

### 3.1 `IVideoSurface` — D → E

This is the exact text to be written into `IVideoSurface.cs`. The coordinator freezes the seams; if the
frozen file differs, the frozen file wins and this plan's § 3.1 is corrected to match, not the reverse.

```csharp
namespace RankMaster2.Pc.Video;

using Avalonia;                       // PixelSize
using Avalonia.Media.Imaging;         // Bitmap

/// <summary>One per process. Its constructor touches nothing native.</summary>
public interface IVideoSurfaceFactory
{
    /// <summary>
    /// Begins LibVLC initialisation if it has not begun; returns the same task on every later call.
    /// Never throws: a failed engine is reported as <see cref="VideoEngineStatus.Failed"/>.
    /// </summary>
    Task<VideoEngineStatus> WarmUpAsync(CancellationToken ct = default);

    VideoEngineStatus EngineStatus { get; }
    /// <summary>Non-null iff <see cref="EngineStatus"/> is Failed. One sentence, for the screen.</summary>
    string? EngineFailure { get; }

    /// <summary>
    /// One per pane. Implicitly calls <see cref="WarmUpAsync"/>. Throws InvalidOperationException
    /// when two surfaces are already alive — there is never a third player.
    /// </summary>
    IVideoSurface Create(string paneName);

    int LiveSurfaces { get; }

    /// <summary>Raised on the UI thread when EngineStatus changes.</summary>
    event Action<VideoEngineStatus>? EngineStatusChanged;

    /// <summary>The last LibVLC log lines and the live counters, for the crash file and the probe.</summary>
    string DiagnosticsDump();
}

public interface IVideoSurface : IDisposable
{
    string PaneName { get; }
    VideoSurfaceState State { get; }

    /// <summary>The path given to the last Play, until StopAsync or the next Play.</summary>
    string? Path { get; }

    /// <summary>
    /// The pane-fitted frame. Null until the first frame arrives and again after StopAsync.
    /// The same object is written in place for every later frame; E redraws on FrameChanged.
    /// </summary>
    Bitmap? Frame { get; }
    PixelSize FrameSize { get; }

    /// <summary>Non-null iff State is Failed.</summary>
    VideoFailure? Failure { get; }
    VideoStats Stats { get; }

    /// <summary>
    /// Device pixels available to this pane. Applied at the next Play; a change while playing
    /// takes effect only when the next file opens (the window is fullscreen and does not resize).
    /// </summary>
    void SetPaneSize(PixelSize pixels);

    /// <summary>
    /// Opens and plays <paramref name="path"/> muted and looping. Returns at once; progress arrives
    /// through StateChanged. Calling it with the path already playing is a no-op. Calling it with a
    /// different path stops the current one first.
    /// </summary>
    void Play(string path);

    /// <summary>
    /// Stops playback, detaches the media and releases the file. The task completes when the file
    /// can be opened with FileShare.None (or after 2 s, with a logged warning). State becomes Idle
    /// and Frame becomes null. Safe to call in any state, any number of times.
    /// </summary>
    Task StopAsync();

    /// <summary>UI thread. Fires on every transition, including into Failed.</summary>
    event Action<IVideoSurface>? StateChanged;

    /// <summary>UI thread. At most once per UI-thread turn; E calls InvalidateVisual on its Image.</summary>
    event Action<IVideoSurface>? FrameChanged;
}

public enum VideoEngineStatus { Asleep, Starting, Ready, Failed }

public enum VideoSurfaceState
{
    Idle,      // nothing requested, or stopped
    Opening,   // Play called, no frame yet — E shows the spinner
    Playing,   // frames arriving
    Holding,   // paused on its last frame by the engine's alternate mode — E shows the frame, optionally a marker
    Failed     // see Failure
}

public enum VideoFailureKind
{
    EngineUnavailable,  // LibVLC could not initialise; every surface reports this
    Missing,            // the file is not on disk (E: "file is gone"; discard → drop_missing)
    Unreadable,         // LibVLC could not parse the container
    NoVideoTrack,       // parsed, but nothing to show (audio-only, data-only)
    DecodeFailed,       // VLC raised EncounteredError, or reached the end before any frame
    NoFrameInTime       // opened, no error, no frame within VideoOptions.FirstFrameTimeout
}

/// <param name="Message">One sentence for the screen, no VLC jargon.</param>
/// <param name="Detail">The last LibVLC log lines, for the crash file and the owner's report.</param>
public sealed record VideoFailure(VideoFailureKind Kind, string Message, string? Detail);

public readonly record struct VideoStats(
    int SourceWidth, int SourceHeight,   // from the parsed track, 0 until parsed
    string Codec,                        // fourcc as text, "" until parsed
    long DecodedFrames, long LostFrames, // from LibVLC statistics, cumulative for this Play
    double LostFrameRate,                // lost / (lost + displayed) over the LoadWatch window, 0..1
    bool Alternating,                    // the engine has put the pair into alternate mode
    TimeSpan TimeToFirstFrame);          // zero until Playing
```

What part E does with it, so the surface is built for that and nothing else:

| E's moment | E's call | What E reads back |
|---|---|---|
| compare view created | `Create("left")`, `Create("right")`, `SetPaneSize(...)` on each | — |
| a video pair arrives | `left.Play(folder + id)`, `right.Play(...)` | `State == Opening` → spinner; filenames go up at once (`SPEC.md`) |
| `StateChanged` → `Playing` or `Holding` | — | bind `Frame` to the pane's `Image` (`Stretch.Uniform`); the pane is "ready" for votes/clicks |
| `FrameChanged` | `image.InvalidateVisual()` | — |
| `StateChanged` → `Failed` | — | `Failure.Message`; accept discard, or a vote for the other side; never auto-skip (`PC_CLIENT_PLAN.md` § 6.5) |
| discard / special / undo-of-move on this side | `await surface.StopAsync()` **then** the server call | — |
| leaving the compare view | `Dispose()` both | — |
| `EngineStatusChanged` → `Failed` | — | `EngineFailure` on both panes |

### 3.2 `IMediaProbe` — C, D → A

Shared with part C. The question it answers is the one that keeps the engine asleep. It is a thin
wrapper over `RankMaster2.Core.MediaExtensions` (`KindOf`, `RankPolicy`), so C and D cannot disagree
with the server about what is video.

```csharp
namespace RankMaster2.Pc.Video;   // or wherever the coordinator places shared seams

public enum FolderPolicy { Empty, Stills, Videos }

public interface IMediaProbe
{
    /// <summary>
    /// SPEC.md § Media policy by extension only, over the top-level files of <paramref name="folder"/>:
    /// only stills → Stills; only videos → Videos; both → Stills; nothing recognised → Empty.
    /// Never opens a file. Never throws for a missing or unreadable folder: returns Empty.
    /// </summary>
    FolderPolicy Classify(string folder);

    /// <summary>True iff the snapshot's policy is "video". The cheap answer when a snapshot exists.</summary>
    bool NeedsVideoEngine(string snapshotPolicy);
}
```

`Classify` exists for the start screen (part A may want to know before a session opens);
`NeedsVideoEngine` is what E uses once a snapshot is in hand. Neither is on any startup path unless A
puts it there, and `Classify` on a 20,000-file folder is one directory enumeration (~10–20 ms measured for
the same enumeration in `PC_CLIENT_PLAN.md` § 3.2, candidate 4).

---

## 4. The hard problems, and how they are solved

### 4.1 Frame callbacks into a bitmap, or a native surface?

**Callbacks. The old app was right, and it is still right in Avalonia.**

Why the old app chose it: WPF's only fast way to host VLC's own renderer is an HWND child, and an HWND
child is opaque to everything WPF draws — the *airspace* problem. The info card, filenames, the
`[1][4]`/`[5][2]` buttons, the select cue's ring and flash all sit over the panes. Avalonia on Windows
draws through its own compositor into one HWND; a `NativeControlHost` (which is what
`LibVLCSharp.Avalonia.VideoView` is) creates a child HWND with the same limitation. Nothing has changed.

What callbacks buy beyond that, and this plan uses both:

1. **VLC does the scaling.** `OnFormat` asks for pane-fitted `RV32`, so VLC's swscale converts and
   scales 4K I420 → ~1920 × 1080 BGRA in one pass and the process never holds a 4K RGB frame (33 MB); it
   holds a pane-sized one (~8 MB for 1920 × 1080; 16.6 MB if the pane were 1920 × 2160 and the clip filled
   it, which a 16:9 clip in a 8:9 pane does not).
2. **The last frame is ours.** § 4.3's alternate mode needs a poster for the held pane. The phone had to
   invent a `TextureView` readback for that; here it is the bitmap that is already on screen.

What it costs at 4K, per pane, per frame — the honest ledger:

| Step | Where | Cost (estimate unless stated) |
|---|---|---|
| dav1d decode | VLC decoder threads | measured: 86 fps alone, 81 fps each with two, on this box (§ 4.3) |
| swscale 4K I420 → pane BGRA | VLC vout thread | measured as ffmpeg's `scale+format=bgra`: 124 fps multithreaded, 77 fps single-threaded; VLC's swscale filter is single-threaded → **~13 ms per frame** on this box |
| `OnDisplay`: `Marshal.Copy` native → back buffer, swap | VLC vout thread | 8–16 MB memcpy, ~1–3 ms |
| `Present`: copy front → `WriteableBitmap.Lock()` | **UI thread** | 8–16 MB, ~1–3 ms, plus Avalonia's texture upload of the same bytes on the next render |

At two panes × 30 fps the UI thread spends roughly 60–180 ms of every second copying — 6–18%. Real, not
prohibitive. Two rules keep it from growing:

- **One pending present per surface.** `OnDisplay` posts `Present` only if no `Present` is already
  queued (an `Interlocked` flag in `FrameStore`, cleared at the top of `Present`). The old app posted
  every frame; under load that queues stale frames on the dispatcher and the pane falls behind. With the
  flag, a slow UI drops frames instead of buffering them, and it always shows the newest one.
- **Do not upscale.** `PaneFit` only shrinks (the old `Fit`). A 720p clip stays 720p; Avalonia's
  `Stretch.Uniform` does the enlargement on the GPU.

The named optimisation, **not built unless Phase 3 measures the UI thread's `Present` time above 25% of a
frame budget** (that is, above ~8 ms per pane at 30 fps): rotate three native buffers so `OnLock` hands
VLC a buffer that is neither the latest nor the one being presented, and `OnDisplay` only flips an index
— removing the vout-thread memcpy. It is a well-understood change and it is also a new place to get a
race wrong, which is why it waits for a number.

Pixel format detail, because getting it wrong shows as blue faces: VLC's `RV32` on x86 is memory order
B, G, R, X. Avalonia's `PixelFormat.Bgra32` is the same order; the X byte is undefined, so the bitmap is
created with `AlphaFormat.Opaque` and the alpha channel is never read. `Present` copies row by row when
`ILockedFramebuffer.RowBytes` differs from VLC's 32-aligned pitch, and in one `Buffer.MemoryCopy` when
they are equal.

One Avalonia fact the port must not miss: an `Image` does **not** observe writes into a `WriteableBitmap`.
After every `Present` the surface raises `FrameChanged` and E calls `InvalidateVisual()` on its `Image`.
Without that, the pane shows the first frame forever.

### 4.2 Lazy initialisation

**Exactly when.** `VideoEngine`'s constructor stores options and creates nothing. LibVLC initialises on
the first of: `WarmUpAsync()`, or `Create(...)` (which calls it). Part E calls `WarmUpAsync()` when it
handles a snapshot whose `policy` is `"video"` and calls `Create` when the compare view for that session
is built. Part A calls **nothing** in `Video/` at startup; its only job is to register `VideoEngine` as a
singleton whose construction is free. The test in § 6.1 (row 9) is the proof that the constructor and the
probe load no native library.

**What it does the first time**, on the worker thread, in order: resolve `libvlc\win-x64` next to the
executable exactly as `VlcRuntime.cs` does (with the same `BaseDirectory` fallback), `Core.Initialize(dir)`,
`new LibVLC(args)` (§ 5.2), subscribe `LibVLC.Log` into `VideoLog`, set `EngineStatus = Ready`. Player
creation happens per surface (`new MediaPlayer(lib)` + `SetVideoFormatCallbacks` + `SetVideoCallbacks` +
the three event subscriptions — the old constructor verbatim).

**What it costs the first time** (estimates; `rm2vidprobe` replaces them with numbers on the owner's PC):

| Step | With the old 320-plugin set, no `plugins.dat` | With § 5.1's 27 files and a `plugins.dat` |
|---|---|---|
| `Core.Initialize` (two `LoadLibrary`) | 10–30 ms | same |
| `new LibVLC` (module bank scan) | **1–4 s** cold — 320 `LoadLibrary` calls plus Defender on a fresh publish; this is `PC_CLIENT_PLAN.md` § 3.2's primary startup suspect | 20–80 ms |
| two `new MediaPlayer` | ~1 ms | same |
| first `Play` → first frame (demux open, dav1d init, one 4K keyframe) | 100–400 ms | same |

So the owner sees, on the first video pair of a session: both filenames at once, both panes' spinners,
and the first frames roughly 0.2–0.5 s later on a warm machine with the pruned set — the same spinner
`SPEC.md` § Compare UI already specifies for video, with no "starting video engine" text of its own. The
surface exposes `EngineStatus` and `Stats.TimeToFirstFrame`, so E may add a line if `Starting` lasts
longer than 2 s; that is E's decision.

If initialisation fails (`libvlc.dll` missing, a plugin folder gone, `libvlc_new` returning null):
`EngineStatus = Failed`, `EngineFailure` is one sentence ("The video engine could not start: <reason>"),
`EngineStatusChanged` fires, and every surface's `Play` goes straight to `Failed(EngineUnavailable)`.
Nothing is written, nothing is moved; the owner can press Esc or open another folder. The engine does not
retry on its own; the next process start will.

A stills folder never reaches any of this. A mixed folder has `policy: "still"` and never reaches it. The
start screen never reaches it.

### 4.3 Two 4K AV1 decoders at once

**Measured on the build box** (i5-13500: 6 P-cores with HT + 8 E-cores = 20 logical CPUs; Linux; ffmpeg
7 with dav1d; an 8-second 3840 × 2160 30 fps clip encoded here with SVT-AV1 at ~33 Mbps, which is in the
25–50 MB-per-10-s range `MEMORY_PROPOSALS.md` § 1.4 estimates for the owner's clips; synthetic `testsrc2`
content, which is *easier* than camera footage with grain — real files will be somewhat slower):

| Configuration | Result |
|---|---|
| one dav1d decoder, threads auto | 86 fps |
| two decoders at once, threads auto | 81 fps each |
| two at once, each with the pane scale + BGRA conversion (the real two-pane load) | **70 fps each** |
| one decoder, 10-bit (Main 10) | 70 fps |
| pinned to **4 logical CPUs** (a small laptop stand-in): one decoder + pane scale | 58 fps |
| pinned to 4 logical CPUs: **two** decoders + pane scale | **40 fps each** |
| pinned to 4 logical CPUs: two 10-bit decoders + pane scale | 33 fps each |
| pinned to 8 logical CPUs: two decoders + pane scale | 59 fps each |
| libaom instead of dav1d, one decoder, threads auto | 20.6 fps |
| peak process memory, one 8-bit decoder (whole ffmpeg process, an upper bound) | 453 MB |
| peak process memory, one 10-bit decoder | 752 MB |

**What it means.** On any desktop of the last five years two 4K30 AV1 streams decode comfortably in
software — the two-pane load runs at more than twice real time on this box and at almost exactly
double real time on 8 logical CPUs. On a 4-logical-CPU machine it is still real time but with the UI,
Avalonia's renderer and the copies of § 4.1 competing for the same cores, so stutter is plausible there,
and 10-bit content leaves ~10% margin. **Viable; the fallback is insurance, not the plan.** The owner's
CPU model is § 12's first question and settles whether the insurance is theoretical.

Memory: two decoders plus this part's own buffers is roughly **1.0–1.6 GB** of process memory at the
worst (two 10-bit 4K streams). That is a lot for a ranking tool and fine for a PC with 8 GB or more, and
unlike the phone it is not near any ceiling: a 64-bit process and no Java heap. What *would* be a problem
is a third decoder or a decoder that is never released — § 4.4.

**When it is not viable — the fallback ladder**, decided here so it is not decided under pressure:

1. **Both play** (default). `LoadWatch` samples each Playing surface's `LostPictures` and
   `DisplayedPictures` (LibVLC `Media.Statistics`, fetched on the worker once a second) and exposes
   `Stats.LostFrameRate` over a 3-sample window. E may show the phone's red dot from this; `SPEC.md`
   says "no controls", and a dot is not a control.
2. **Alternate.** If both surfaces are Playing and *either* `LostFrameRate` exceeds
   `VideoOptions.AlternateThreshold` (**0.25**) for three consecutive samples, the engine puts the
   right surface into `Holding` (`SetPause(true)` — its last frame stays on screen because it is our
   bitmap) and lets the left play alone. Every `max(Length, 3 s)` — `Length` being the clip duration
   LibVLC reports after the first frame — the roles swap. Both panes move, in turn; no gesture is
   spent. The mode is **sticky for the session**: it is reset only when `LiveSurfaces` drops to zero
   (leaving the compare view). Flapping between modes would look broken; one drop into alternate mode
   looks deliberate. `Stats.Alternating` tells E, so E can show a small pause marker on the held pane
   if it chooses.
3. There is no step 3. Reduced-resolution decode is impossible client-side (an AV1 decoder decodes
   what it is given), the server refuses to transcode (`SERVER_PLAN.md` § 7), and hardware decode is
   § 1.1's named non-goal until a measurement says otherwise.

Why pause and not stop for the held pane: a paused decoder keeps its ~0.5 GB, a stopped one frees it but
costs a first-frame delay and a black pane on every swap. The PC has the memory; the owner's eye does not
have the patience. Pause.

Why the phone's option B and not "the pane under the mouse plays": the owner ranks with keys (`SPEC.md`
§ Keys); hover would work for a mouse user and do nothing for him. B needs no input at all.

Decoder threads: dav1d's default is `0 = auto`, one thread per logical CPU, so two players ask for 2× the
machine. That oversubscription is harmless while threads sleep between frames (measured: the two-decoder
case lost 6% against one), and capping it (`:dav1d-thread-frames`) helped nothing in the pinned runs. Left
at auto; `VideoOptions.Dav1dThreads` exists so `rm2vidprobe` can try a cap on the owner's PC without a
rebuild.

### 4.4 Memory, ownership, release

The phone died twice from a player that outlived its pane. The rules here are few and each has a proof.

**Ownership.**

| Thing | Owned by | Created | Released |
|---|---|---|---|
| `LibVLC` instance | `VideoEngine` | first `WarmUpAsync` | never during the process; `Dispose` exists for tests |
| worker thread | `VideoEngine` | with the `LibVLC` instance | with `Dispose` (tests) |
| `VideoSurface` | **part E's pane** | `Create`, at compare-view construction, two of them | E's `Dispose` when the compare view goes; `LiveSurfaces` decrements |
| `MediaPlayer` | its `VideoSurface` | with the surface | with the surface |
| `Media` | its `VideoSurface`, one at a time | `Play` | `StopAsync`, the next `Play`, `Dispose` — `player.Media = null` then `media.Dispose()`, the old `Stop()` order |
| native frame buffer + two `byte[]` | `FrameStore` | VLC's `OnFormat` | VLC's `OnCleanup`, `StopAsync`, `Dispose` |
| `WriteableBitmap` | its `VideoSurface` | first `OnFormat` of a given size | replaced when the next file's fitted size differs; dropped at `Dispose` |

**Rules.**

1. `Create` throws on a third surface. Not a warning, not a queue — an exception, so the bug that killed
   the phone cannot be written here without a stack trace in a test.
2. Every callback and event carries the surface's generation number captured when it was armed; a stale
   generation is ignored on arrival (the old `_generation` rule, unchanged). `Play`, `StopAsync` and
   `Dispose` each increment it first.
3. `Play(newPath)` while playing is `StopAsync` then open; `Play(samePath)` while Playing or Opening is
   a no-op (the old `PathsMatch` rule — `OrdinalIgnoreCase` on the full path).
4. `StopAsync` completes when the file opens with `FileShare.None`, polling 40 × 50 ms (the old
   `FileOps.WaitUntilUnlocked`, copied into `Video/` — this project depends on `Core` only). After 2 s it
   completes anyway and logs; the server's own move retry then decides (`PC_CLIENT_PLAN.md` § 6.3).
5. `Dispose` is `StopAsync` with a 2 s cap, then `MediaPlayer.Dispose()`, then `FrameStore.Free()`, then
   `Frame = null`, then `LiveSurfaces--`. Idempotent. After it, `Play` and `SetPaneSize` throw
   `ObjectDisposedException`; `StopAsync` and `Dispose` return.
6. Nothing in `Video/` ever moves, renames, deletes or writes a media file, or writes anything at all
   except log lines.

**Proofs** (§ 6 says where each runs):

- Pure: after `Dispose`, the fake backend reports the player disposed, the native buffer freed, and a
  late `OnDisplay`/`EncounteredError`/`EndReached` from the old generation changes nothing.
- Pure: a third `Create` throws; `Dispose` of one then `Create` succeeds.
- Container (real LibVLC): after `StopAsync`, `/proc/self/fd` holds no link to the file. Linux does
  not lock files, so this is the honest release proof available here; Windows proves it as a
  first-time `File.Move` on the owner's PC (§ 8, row 4).
- Container: 100 × (`Play` → first frame → `StopAsync`) on one surface, then 20 × (`Create` → `Play` →
  `Dispose`); `VmRSS` after, minus `VmRSS` after the tenth iteration, is under 64 MB.
- Owner's PC: 30 minutes of ranking a video folder; private bytes at 30 min minus private bytes at 5 min
  under 200 MB (§ 8, row 5).

### 4.5 Files that will not play

LibVLC's error reporting is thin — one `EncounteredError` event with no reason, and for many corrupt
files no event at all, just silence or a premature `EndReached`. The surface therefore decides failure
from a fixed sequence with timeouts, and attaches VLC's own log lines so the owner's report says *why*.

`Play(path)` runs, on the worker thread, in this order:

| Step | Signal | Outcome |
|---|---|---|
| 1 | `File.Exists` false | `Failed(Missing)` — no LibVLC call |
| 2 | `new Media(lib, path, FromPath)` + options; `Parse(ParseLocal, timeout 3000 ms)` returns `Failed` or `Timeout` | `Failed(Unreadable)` |
| 3 | parsed, `Tracks` has no `TrackType.Video` | `Failed(NoVideoTrack)` |
| 4 | parsed with a video track → record `SourceWidth/Height`, `Codec`; `player.Play(media)` returns false | `Failed(DecodeFailed)` |
| 5 | `EncounteredError` before the first frame | `Failed(DecodeFailed)` |
| 6 | `EndReached` before the first frame | `Failed(DecodeFailed)`, message "the file ends before its first frame" |
| 7 | no frame within `FirstFrameTimeout` (**5 s**) | `Failed(NoFrameInTime)` |
| 8 | first `OnDisplay` → `Present` | `Playing`; the timer is cancelled |
| 9 | `EncounteredError` while Playing | `Failed(DecodeFailed)`; `Frame` is **kept** so the pane does not go black; E decides |
| 10 | `EndReached` while Playing | post `Position = 0; Play()` to the worker — looping; state unchanged |

Why 5 s: a 4K AV1 keyframe decodes in ~12 ms here and the whole open is under half a second; 5 s is ten
times that and still short enough that a hung demuxer does not look like a frozen app. It is in
`VideoOptions` for the probe to adjust.

Why `Parse` first: it runs only the demuxer, costs milliseconds on a local file, and turns two of the
worst cases — a 0-byte file and an audio-only file — into a definite answer in step 2 or 3 instead of a
5-second wait in step 7. It also yields the resolution and codec for `Stats` before a decoder exists.
With the pruned plugin set (§ 5.1) a failed `mp4` demux cannot fall through to the twenty other demuxers
VLC would otherwise try, so failure is *faster* with fewer plugins.

Every `Failed` carries `Detail` = the last 20 lines from `VideoLog` at or above warning level — the lines
where LibVLC says "no suitable decoder module for fourcc `xxxx`" or "moov atom not found". That is what
goes into the crash file and what the owner pastes when a file of his does not play.

The corpus, mapped:

| Fixture | Expected | Kind |
|---|---|---|
| `video/av1.mp4`, `h264.mp4`, `h264.mov`, `h264.mkv`, `vp9.webm`, `mpeg4.avi` | `Playing` within 3 s; at least two `FrameChanged` in the next 2 s | — |
| `broken/empty.mp4` (0 bytes) | `Failed` within 3 s | `Unreadable` |
| `broken/truncated.mp4` (1,500 bytes, no `moov`) | `Failed` within 5 s | `Unreadable` or `DecodeFailed` — the test accepts either; both are correct |
| `broken/text_pretending.jpg` copied to `text_pretending.mp4` in scratch | `Failed` within 3 s | `Unreadable` |
| an audio-only `.mp4` generated in scratch (`ffmpeg -f lavfi -i sine -t 2`) | `Failed` within 3 s | `NoVideoTrack` |
| a path that does not exist | `Failed` at once | `Missing` |
| a codec LibVLC lacks (e.g. an `.mkv` with a video track whose fourcc no shipped plugin decodes — made in scratch by remuxing a raw stream with a bogus codec id, or skipped if ffmpeg cannot produce one) | `Failed` within 5 s | `DecodeFailed` or `NoFrameInTime`, and `Detail` names the fourcc |

**What part E shows, and what the owner loses:** for `Missing`, "file is gone" and only that side's
discard, which the server turns into `drop_missing` (`PC_CLIENT_PLAN.md` § 6.5). For everything else,
"this file cannot be played: <Message>" with discard (ordinary, with undo) or a vote for the other side
by filename. Never an automatic skip — that would record an impression nobody chose. The owner loses
nothing: this part never moves or writes a file, the rating row stays, and the log tells him which file
and why. This is a visible change from the old app, which silently dropped the id and moved on
(`OnFrameFailed` → `_session.Drop`), and it is the smaller sin for the reason `PC_CLIENT_PLAN.md` § 6.5
gives.

### 4.6 Threads, and LibVLC 3's two deadlocks

Facts the port must honour, all learned the hard way in libvlc 3 and visible in the old code's shape:

1. **Calling into a `MediaPlayer` from one of its own callbacks or events deadlocks.** The old
   `OnEndReached` posts `Play()` to the dispatcher rather than calling it; the old `OnEncounteredError`
   posts too. Keep that shape: callbacks touch `FrameStore` and post; nothing else.
2. **`Stop()` blocks until the decoder threads join** — 50–300 ms for a 4K decoder (estimate). Two panes
   changing on every vote would put up to half a second of blocking on the UI thread, right through the
   select cue. So every LibVLC call runs on the engine's **single worker thread** through a queue:
   `Initialize`, `CreatePlayer`, `Parse`, `Play`, `Stop`, `SetPause`, `Statistics`, `Dispose`. One queue
   gives per-player ordering for free (a `Play` can never overtake the `Stop` before it) and the UI thread
   never waits on VLC.
3. **State lives on the UI thread.** `VideoSurface`'s state machine runs on the UI thread through
   `IUiThread.Post`; worker and VLC threads only post results with their generation. Tests substitute a
   synchronous `IUiThread` and a `FakeBackend` and drive every transition of § 4.5 without LibVLC.
4. `OnFormat` runs on a VLC thread and needs the pane size: `SetPaneSize` writes two `volatile int`s that
   `OnFormat` reads; the bitmap is created by a post to the UI thread with the generation attached (the
   old code's exact shape).

### 4.7 The frame path, end to end

For the executor, the whole per-frame path in one place, so it is ported and not re-derived:

```
VLC thread     OnFormat(chroma, w, h, pitches, lines)
                 chroma = "RV32"; (w, h) = PaneFit.Fit(w, h, paneW, paneH); w, h even and ≥ 2
                 pitches = Align32(w * 4); lines = Align32(h)
                 FrameStore.Allocate(pitches * lines)   -- AllocHGlobal + two byte[]
                 post(gen) → UI: create WriteableBitmap(w × h, 96 dpi, Bgra32, Opaque) if size changed
               OnLock(planes)  → write FrameStore.NativePtr into planes[0]
               OnDisplay       → lock; Marshal.Copy(native → back); swap(front, back); unlock
                                 if Interlocked.Exchange(ref presentPending, 1) == 0: post(gen) → Present
               OnCleanup       → FrameStore.Free()

UI thread      Present(gen)
                 presentPending = 0
                 if gen stale or bitmap null: return
                 using fb = bitmap.Lock(): copy front → fb.Address (row-wise if RowBytes ≠ pitch)
                 if State == Opening: State = Playing; TimeToFirstFrame = now - playStarted; StateChanged
                 FrameChanged
```

---

## 5. What part A must ship for this design

### 5.1 The plugin list

The `VideoLAN.LibVLC.Windows 3.0.21` package restores **320 plugin DLLs, 98 MB**. This design loads a
local file, demuxes one of five containers, packetizes, decodes video, converts and scales to RV32, and
hands frames to `vmem`. It needs **27 files, 31.2 MB** (sizes from the package restored here):

| Folder | File | Bytes | Why |
|---|---|---|---|
| `libvlc\win-x64\` | `libvlc.dll` | 194,440 | the API |
| | `libvlccore.dll` | 2,810,760 | the core |
| `plugins\access\` | `libfilesystem_plugin.dll` | 74,120 | local files — the only access this client uses |
| | `libidummy_plugin.dll` | 44,936 | `--intf=dummy` names it |
| `plugins\demux\` | `libmp4_plugin.dll` | 331,144 | `.mp4`, `.mov` |
| | `libmkv_plugin.dll` | 1,749,896 | `.mkv`, `.webm` |
| | `libavi_plugin.dll` | 139,144 | `.avi` |
| `plugins\stream_filter\` | `libprefetch_plugin.dll` | 48,520 | VLC's read-ahead for file access |
| | `libcache_read_plugin.dll` | 47,496 | |
| | `libcache_block_plugin.dll` | 46,984 | |
| `plugins\packetizer\` | `libpacketizer_av1_plugin.dll` | 70,024 | AV1 |
| | `libpacketizer_h264_plugin.dll` | 176,520 | H.264 in the corpus and in the wild |
| | `libpacketizer_hevc_plugin.dll` | 158,600 | HEVC `.mov`/`.mp4` from phones and cameras |
| | `libpacketizer_mpeg4video_plugin.dll` | 59,272 | MPEG-4 Part 2 `.avi` (corpus) |
| | `libpacketizer_copy_plugin.dll` | 45,448 | the fallback packetizer VP8/VP9 and others use |
| `plugins\codec\` | `libdav1d_plugin.dll` | 1,891,208 | **AV1 — the reason LibVLC is here** |
| | `libavcodec_plugin.dll` | 17,273,224 | H.264, HEVC, MPEG-4, and everything else the five containers may carry |
| | `libvpx_plugin.dll` | 4,510,600 | VP8/VP9 in `.webm` (see the note below) |
| `plugins\video_chroma\` | `libswscale_plugin.dll` | 1,017,224 | the scaler and the catch-all converter, including 10-bit → RV32 |
| | `libi420_rgb_plugin.dll` | 62,344 | fast I420 → RGB when no scaling is needed |
| | `libi420_rgb_sse2_plugin.dll` | 150,408 | |
| | `libchain_plugin.dll` | 72,584 | chains two converters when no single one fits |
| `plugins\video_filter\` | `libscale_plugin.dll` | 45,448 | the vout's resize filter hook |
| `plugins\video_output\` | `libvmem_plugin.dll` | 46,984 | **the callbacks** — `SetVideoCallbacks` selects it |
| | `libvdummy_plugin.dll` | 45,960 | harmless fallback if a vout is requested with no callbacks |
| `plugins\audio_output\` | `libadummy_plugin.dll` | 42,888 | `--aout=dummy` names it |
| `plugins\text_renderer\` | `libtdummy_plugin.dll` | 42,888 | the vout asks for a text renderer lazily; the dummy one answers |

Ship **no** `lua\` folder, **no** `hrtfs\` folder, **no** `.lib` files, and none of the other 293
plugins. In particular, and each for a reason:

- `libaom_plugin.dll` — a second AV1 decoder, measured 4× slower than dav1d (§ 4.3); dav1d decodes
  every AV1 profile. Two decoders for one codec means VLC picks by priority and the loser is dead weight.
- `d3d11\`, `d3d9\`, `libd3d11va_plugin`, `libdxva2_plugin`, `libmft_plugin`, `libqsv_plugin` — hardware
  decoding is off.
- every `demux` beyond the three — with them absent, a broken `.mp4` fails in milliseconds instead of
  being tried by twenty demuxers (§ 4.5).
- every `codec` for subtitles and audio — audio is disabled per media; an `.mkv` with a subtitle track
  logs "no suitable decoder" and plays.
- `meta_engine\` (`libfolder_plugin` scans the folder for cover art on every parse), `services_discovery\`,
  `stream_out\`, `mux\`, `access_output\`, `visualization\`, `video_splitter\`, `spu\`, `misc\`, `keystore\`,
  `logger\`, `stream_extractor\`, every network `access` — none can be reached by this code path.

**`libvpx_plugin.dll` note.** VLC's `libavcodec_plugin` very likely contains ffmpeg's native VP9 decoder
too (the string appears in the binary), which would make `libvpx` redundant. Ship it in Phase 2; if
`rm2vidprobe` plays `vp9.webm` on the owner's PC with `libvpx` removed, part A drops it (−4.5 MB, −1
`LoadLibrary`). Not decided here because it cannot be checked here.

**If the owner has a file the pruned set cannot play and the full set can**, `rm2vidprobe` (§ 6.4) run
over his folder with both plugin directories reports the difference and `Detail` names the missing
module. The add-back candidates, in order of likelihood: `libpacketizer_mpegvideo_plugin` and
`libpacketizer_vc1_plugin` (old `.avi`/`.mkv`), `libdeinterlace_plugin` (interlaced camera footage — VLC
inserts it automatically and logs when it cannot), `libaom_plugin` (only if a dav1d failure is ever
seen — it should not be).

For part A's `plugins.dat`: the cache is keyed on each plugin file's size and mtime, so it must be
generated **after** the final prune and regenerated whenever this list changes. This list is the
contract; a change to it is a change to this plan.

### 5.2 The options

Engine (`new LibVLC(...)`), the old `VlcRuntime` list with **one deletion**:

```
--intf=dummy
--aout=dummy
--no-video-title-show
--no-osd
--quiet
--avcodec-hw=none
--drop-late-frames
--skip-frames
--plugin-path=<libvlc\win-x64\plugins>
```

Deleted: `--no-stats`. It switches off the input statistics that `Media.Statistics` reads, and § 4.3's
`LoadWatch` needs `LostPictures` and `DisplayedPictures`. The cost of statistics is a few counters per
frame.

Per media (`Media.AddOption`), the old `VlcFramePlayer.Play` list verbatim:

```
:no-audio
:input-repeat=65535
:avcodec-hw=none
```

Player: `Mute = true`, `Volume = 0`, `EnableHardwareDecoding = false` — verbatim.

`VideoOptions` (the tunables, with the defaults this document sets): `FirstFrameTimeout = 5 s`,
`ParseTimeout = 3 s`, `ReleaseWait = 40 × 50 ms`, `LoadWatchInterval = 1 s`, `AlternateThreshold = 0.25`
over 3 samples, `MinSwapInterval = 3 s`, `Dav1dThreads = 0` (auto; when non-zero adds
`:dav1d-thread-frames=N` per media), `ForceAlternate = false` (for tests and the probe),
`LogRingSize = 100`.

One open point about `--quiet`: it sets LibVLC's verbosity low, and whether the `LibVLC.Log` event still
delivers warnings under it is not certain from here. The container test in § 6.1 row 4 asserts that a
forced failure leaves `Detail` non-empty; if it is empty, replace `--quiet` with `--verbose=1` (warnings
and errors) and re-run. That is the only permitted change to the option list without a measurement.

---

## 6. Verification — what is proven where

This is the least verifiable part of the client. The design puts the state machine, lifetime rules,
pane maths, failure mapping and the probe behind `IPlayerBackend` and `IUiThread` precisely so that they
are ordinary C# with ordinary tests. What remains — LibVLC's actual behaviour — is proven twice: once on
Linux against the same libvlc 3.0.x in a container, once on the owner's Windows PC with a console tool.

### 6.1 Pure tests — always run, here, `dotnet test`

`pc/tests/RankMaster2.Pc.Tests/Video/`, xunit, with `FakeBackend` (scriptable: returns a chosen parse
status and track list, raises events on demand, records every call) and a synchronous `IUiThread`.

| # | Test | Proves |
|---|---|---|
| 1 | `PaneFit`: 3840 × 2160 into 1920 × 2160 → 1920 × 1080; 1280 × 720 into 1920 × 2160 → unchanged; odd sizes → even; pitches 32-aligned; the ≥ 2 floor | the old `Fit`/`Align32` semantics, exactly |
| 2 | `FrameStore`: `OnDisplay` twice before one `Present` → one post, newest frame shown; free while a late `OnDisplay` arrives → no write | § 4.1 coalescing; no use-after-free |
| 3 | state machine: every row of § 4.5's table, driven by the fake | the failure mapping |
| 4 | generation: an event armed under generation N arriving after `Play`/`StopAsync`/`Dispose` bumped to N+1 changes nothing | § 4.4 rule 2 |
| 5 | `Play(same)` is a no-op; `Play(other)` stops then opens; `StopAsync` twice is fine; `Dispose` twice is fine; `Play` after `Dispose` throws | § 4.4 rules 3, 5 |
| 6 | `Create` × 3 throws; `Dispose` one then `Create` succeeds; `LiveSurfaces` tracks | rule 1 |
| 7 | `LoadWatch`: rates below threshold → keep; three samples above → hold right; swap after `Length`; sticky until `LiveSurfaces` = 0; never with one surface | § 4.3 ladder |
| 8 | `MediaProbe`: stills-only → Stills; videos-only → Videos; both → Stills; empty and missing folder → Empty; extension case-insensitive; `NeedsVideoEngine("video")` true, `"still"` false | `SPEC.md` § Media policy |
| 9 | laziness: construct `VideoEngine`, call `Classify` on a stills folder, run a fake stills "session" → `FakeBackend.InitializeCalls == 0`; **and** on Linux `/proc/self/maps` contains no `libvlc` — the second half runs for real in § 6.2 too | § 4.2 |
| 10 | `FailureMapper` messages contain no VLC jargon and `Detail` carries the ring's last 20 lines | § 4.5 |

### 6.2 Real LibVLC on Linux — the container

LibVLC is not installed on the build box and installing it is a system change. It is not needed on the
host: the tests run in a container.

```
pc/tests/run-video-linux.sh           (D-owned helper)
  docker run --rm -v "$REPO":/src -w /src mcr.microsoft.com/dotnet/sdk:8.0 bash -c '
    apt-get update -qq && apt-get install -y -qq libvlc5 vlc-plugin-base >/dev/null
    dpkg -L vlc-plugin-base | grep -E "vmem|dav1d"          # both must be present; abort if not
    dotnet test pc/tests/RankMaster2.Pc.Tests --filter Category=LibVlc'
```

The SDK image is Debian 12, whose VLC is 3.0.2x — the same major/minor as the Windows payload; the exact
patch level is printed by the test run and recorded in the phase report. LibVLCSharp on Linux finds the
system `libvlc.so.5` with a bare `Core.Initialize()`; `LibVlcBackend` calls the path-taking overload only
when a `libvlc\win-x64` folder exists next to the executable. The tests are `[Trait("Category",
"LibVlc")]` and skip cleanly (not fail) when `Core.Initialize` throws, so `dotnet test` on the host stays
green.

Fixtures: `tests/corpus/media/video/*` and `broken/*` as they are (read-only — shared files are not
edited), plus generated, git-ignored clips in `pc/tests/fixtures/video/` from `pc/tests/make-video-
fixtures.sh` (host ffmpeg, which exists): the 8-second 4K AV1 8-bit and 10-bit clips exactly as measured
in § 4.3 (`-f lavfi -i testsrc2=size=3840x2160:rate=30 -t 8 -c:v libsvtav1 -preset 10 -crf 32 -g 60`,
with `-pix_fmt yuv420p` and `yuv420p10le`), a 2-second audio-only `.mp4`, and `text_pretending.mp4`.

| # | Test | Proves |
|---|---|---|
| 1 | each corpus video → `Playing` ≤ 3 s, ≥ 2 `FrameChanged` in 2 s, `FrameSize` ≤ pane, `Stats.Codec` non-empty | the plugin path, all five containers, AV1 |
| 2 | each broken/generated failure fixture → the § 4.5 kind, within its bound, `Frame == null` | failure mapping against real VLC |
| 3 | `Missing` without touching the backend (`InitializeCalls` may be 1 from the engine, `PlayCalls == 0`) | step 1 |
| 4 | a forced failure's `Detail` is non-empty | the log ring works under `--quiet` (else § 5.2's switch) |
| 5 | `Play` → `Playing` → `StopAsync` → `/proc/self/fd` has no entry for the path; `Frame == null`; `State == Idle` | release |
| 6 | 100 × (`Play` → first frame → `StopAsync`), then 20 × (`Create` → `Play` → `Dispose`); RSS growth from iteration 10 to the end < 64 MB | no leak |
| 7 | two surfaces, the 4K AV1 clip on both → both `Playing`; run 6 s; print `LostFrameRate`, `TimeToFirstFrame`, and `Present` durations — **informational**, asserted only that both reach `Playing` | the two-pane path exists and its numbers are seen here first |
| 8 | `ForceAlternate = true` with two surfaces → one `Holding`, the other `Playing`; after `Length` they swap; `Dispose` both → engine mode resets | the ladder with a real paused player |
| 9 | construct the engine and probe a stills folder → `/proc/self/maps` has no `libvlc` | laziness, for real |

### 6.3 What only the owner's PC can prove

Stated so nobody mistakes the container for Windows:

- that `libvlc.dll` and the 27 plugins load and `libvlc_new` succeeds with the pruned set and part A's
  `plugins.dat`;
- the first-time cost of § 4.2 in milliseconds;
- that `RV32` lands as `Bgra32` on Windows exactly as it did in WPF (it should; same VLC, same bytes);
- `WriteableBitmap.Lock()` and Avalonia's texture upload throughput on his GPU — the `Present` numbers;
- that a `File.Move` succeeds first time after `StopAsync` — Windows file locking, which Linux cannot
  imitate;
- the decode rate of *his* files on *his* CPU, and therefore whether alternate mode ever engages;
- the `DisplayedPictures`/`LostPictures` counters' behaviour with `vmem` output on Windows.

### 6.4 `rm2vidprobe` — the Windows-side proof, built here

A net8.0 console in `pc/tools/rm2vidprobe/` (~250 lines), published `win-x64` from Linux like every other
binary in this project, using the **same** `Video/` classes with a null `IUiThread` (runs the state
machine inline) and no Avalonia bitmap (a `FrameStore`-only sink). It takes a plugin directory and one or
more paths or a folder and prints one text block for Telegram:

```
libvlc 3.0.21  plugins: <dir>  plugins.dat: present  Core.Initialize 14 ms  new LibVLC 41 ms
<file>   parse 6 ms  first frame 212 ms  3840x2160 av01  6.0 s: decoded 180 lost 0 (0.0%)  ws 612 MB
<file>   FAILED DecodeFailed after 4.9 s: no suitable decoder module for fourcc 'xxxx'
PAIR <a> + <b>   6.0 s: left lost 0.0%  right lost 0.0%   process ws 1180 MB
--plugins-compare <full> <pruned>   per-file: plays/fails under each; the diff
--loop N <file>   N play/stop cycles; ws before/after
```

It is the tool that replaces every "estimate" in this document with a number and that tells part A
whether the plugin list is right. It is sent to the owner with part A's Phase 0 kit or on its own; it
needs no UI and no server.

---

## 7. Phases

**Phase 0 — seams and the pure core.** `IVideoSurface.cs` as § 3.1 (or the coordinator's frozen version),
`IMediaProbe` as § 3.2, `IPlayerBackend`, `IUiThread`, `VideoOptions`, `PaneFit`, `FrameStore`,
`FailureMapper`, `LoadWatch`, `MediaProbe`, `VideoSurface`, `VideoEngine` — with **no** `LibVlcBackend`
yet — and every § 6.1 test green on the host. Exit: `dotnet test` green; the `LibVLCSharp` grep finds
nothing outside `Backend/`.

**Phase 1 — the backend, in the container.** `LibVlcBackend.cs` and `LibVlcOptions.cs`, ported from
`VlcRuntime.cs` and `VlcFramePlayer.cs` with the changes this plan names and no others: the worker
thread, the present-coalescing flag, the `Parse` step, `--no-stats` removed, statistics read. The fixture
script. Every § 6.2 test green in the container; row 7's numbers written into the phase report. Exit: the
corpus plays and fails as § 4.5 says; release and leak proofs pass.

**Phase 2 — `rm2vidprobe` to the owner.** Built here with part A's pruned plugin folder (§ 5.1). The owner
runs it on one AV1 pair and on his real folder with `--plugins-compare`. Exit: § 4.2's cost table and
§ 4.3's viability become numbers; the plugin list is confirmed or amended (the `libvpx` decision is made);
the owner's CPU and bit depth are known.

**Phase 3 — integration with part E, on his PC.** Two panes of 4K AV1; `Present` time and
`LostFrameRate` logged for 60 s; the § 4.1 rotating-buffer optimisation built **only** if `Present` is
over 8 ms per pane; alternate mode observed or confirmed not to engage; discard-while-playing ten times.
This is also where `PC_CLIENT_PLAN.md` § 4's video gate ("two LibVLC panes at pane size without visible
stutter") is evaluated. Exit: § 8 rows 2–4 and 8.

**Phase 4 — adversarial pass.** One reviewer, two questions: can any path leave a `MediaPlayer` alive
without a live surface, or create a third (grep every `new MediaPlayer`, every `Create`, every early
return in `Dispose`); and can anything under `Video/` be reached before a video snapshot (grep every
reference to `VideoEngine` and `IVideoSurfaceFactory` outside `Video/`, and every `LibVLCSharp` usage
outside `Backend/`). Findings fixed before § 8 is claimed.

Phase 0 needs only the frozen seams. Phase 1 needs docker on the build box (present). Phase 2 needs
part A's plugin folder and the owner's time — one run, one paste. Phase 3 needs part E's compare view.

---

## 8. Acceptance gate

Part D is done when, on the owner's PC, with part A's pruned `libvlc\` folder:

1. **A stills folder never loads LibVLC.** After ranking 20 pairs of stills, `libvlc.dll` is not in the
   process's module list (part A's diagnostics or `tasklist /m libvlc.dll` print it). Here, § 6.2 row 9
   is the same proof on Linux.
2. **First frames arrive.** On the first video pair of a session, both panes show a frame within 1.0 s
   of the pair appearing (`Stats.TimeToFirstFrame`, printed by the crash-file diagnostics), and within
   0.5 s on every later pair.
3. **Two 4K AV1 clips play at once**, looping, muted, with `LostFrameRate` under 5% on both panes for
   60 s — **or** alternate mode has engaged and both panes visibly take turns, with no black frame at a
   swap.
4. **Discarding a playing video moves the file the first time**, ten times out of ten — no `move_failed`
   toast.
5. **No growth.** 30 minutes of ranking a video folder; the process's private bytes at 30 minutes are
   within 200 MB of the value at 5 minutes.
6. **Every failure fixture** (`broken/*.mp4`, the audio-only file, a file deleted under the session)
   shows E's sentence with a reason, offers discard, moves nothing by itself, and the session continues.
7. **The pruned plugin set plays everything the full set plays** in the owner's real folder
   (`rm2vidprobe --plugins-compare` reports zero regressions).
8. **Esc while two 4K clips play**: the process is gone within a second.

Rows 1 and 3 are the ones that matter: the first is this part's share of the reason the client exists,
the second is the thing that killed two designs on the phone.

---

## 9. What this plan needs from the other parts

Stated as requests, not designs:

- **Part A**: register `VideoEngine` as a singleton with a free constructor and call nothing on it; ship
  § 5.1's 27 files under `libvlc\win-x64\` in the old layout and generate `plugins.dat` after the prune;
  add to the client project `LibVLCSharp 3.10.1` and `VideoLAN.LibVLC.Windows 3.0.21` (both already in
  the local NuGet cache) with the `win-x86` drop the old `.csproj` has; add `pc/tools/rm2vidprobe/` to the
  solution; include `Video/`'s `DiagnosticsDump()` in the crash text file.
- **Part E**: the table under § 3.1 — create two surfaces, `Play` per pair, redraw on `FrameChanged`,
  show a sentence on `Failed`, `await StopAsync()` before any move on that side, `Dispose` on leaving the
  compare view, and `WarmUpAsync()` on the first video snapshot.
- **Part C**: shares `IMediaProbe`; both implement nothing of it — the one implementation is `MediaProbe`
  in `Video/`, and if the coordinator prefers it in a neutral folder, it moves without change.
- **Part B**: nothing. This part never sees a token.
- **The coordinator**: freeze § 3.1 and § 3.2 (or amend them and tell this part); decide where shared
  seam files live; confirm the `--no-stats` deletion and the test-folder ownership in § 2.

---

## 10. Risks

| Risk | Mitigation |
|---|---|
| The pruned plugin set is missing something the owner's real files need | `rm2vidprobe --plugins-compare` in Phase 2, before any UI depends on it; the add-back list in § 5.1 |
| `WriteableBitmap` throughput in Avalonia on Windows is worse than WPF's `WritePixels` | measured in Phase 3; the rotating-buffer step removes one copy; `PC_CLIENT_PLAN.md` § 4's HWND fallback for the panes remains the last resort |
| `LibVLC.Log` delivers nothing under `--quiet`, so failures arrive without a reason | § 6.2 row 4 catches it; the one sanctioned option change |
| `Media.Statistics` counters are zero or misleading with `vmem` output | `LoadWatch` then never triggers and both panes simply play; the probe prints the counters so this is seen in Phase 2, not discovered later. Fallback signal: measured `Present` interval vs the clip's frame rate |
| A callback fires after `Dispose` on a VLC thread | generation check plus `FrameStore`'s lock and null checks; tested with the fake in § 6.1 row 2 and 4 |
| `Stop()` takes seconds on some file, stalling the worker queue | the queue is per engine, so the other pane's `Play` waits — visible as a late second pane, not a hang; `StopAsync`'s 2 s cap bounds the wait E sees |
| The owner's files are 10-bit and his PC has 8 GB | ~1.5 GB peak (§ 4.3) fits; alternate mode halves the *active* decode but not the memory (paused keeps its pool) — if this combination shows swapping, switch the held pane from pause to stop (one flag in `VideoOptions`) |
| Debian's libvlc differs from 3.0.21 in a way that matters | the same major/minor; the container run is a proof of *this code's* behaviour, and Windows is proven separately by `rm2vidprobe` |

---

## 11. What could not be determined without Windows

Everything in § 6.3, plus: whether `libvlc 3.0.21` on Windows honours `dav1d-thread-frames` as a per-media
option or only as an engine option (the plugin's strings show the option exists; the probe tries both if a
cap is ever wanted); the exact byte the `X` channel of `RV32` carries (irrelevant with `AlphaFormat.Opaque`,
noted only so nobody switches to `Premul`).

---

## 12. Decisions for the owner

1. **Which CPU is in the PC** (model, core count) — decides whether § 4.3's alternate mode is insurance
   or a real path, and what `rm2vidprobe` should expect.
2. **Are the AV1 files 8-bit or 10-bit?** `MediaInfo` or `ffprobe` shows `yuv420p` or `yuv420p10le`. Memory
   is 450 MB vs 750 MB per pane.
3. **When alternate mode holds a pane, do you want a small pause marker on it**, or nothing at all? (Part E
   draws it; this part only reports `Holding`.)
4. **Do any of your folders mix AV1 with older files** (H.264, VP9, HEVC)? If every video is AV1,
   `libavcodec_plugin.dll` (17 MB) and `libvpx_plugin.dll` (4.5 MB) can go too and the engine starts faster
   still. Default: keep them, because the spec says five containers and the corpus has H.264 in four of them.
