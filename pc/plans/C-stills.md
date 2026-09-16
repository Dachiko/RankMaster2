# Part C — Stills

**Status: plan.** Owns `pc/src/RankMaster2.Pc/Stills/` and its tests in `pc/tests/RankMaster2.Pc.Stills.Tests/`.
Nothing here has been built. Every number below was measured on the build box (Linux, SkiaSharp 3.119.0,
the real corpus in `tests/corpus/media/`) and says so, or is arithmetic and says so.

Goal: a photograph on the screen at the size the pane will draw it, correctly oriented, in the right
colours, in one decode, holding no more memory than the pairs in flight — and, when the file is not a
photograph after all, a plain statement of that to part E instead of a crash or a silent drop.

`SPEC.md` § Pipeline and § Media policy are the behaviour and do not change. `PC_CLIENT_PLAN.md` § 1.2 and
§ 6.5 are the architecture this part lands. `src/RankMaster2.Server/Media/StillRenderer.cs` has already
solved the decode once, with the same library; this plan copies its technique and its helper functions
rather than inventing a second set. Where this plan and any of those disagree, they win.

The reader of this plan executes it without re-deciding anything. Where a choice was open, it is closed
below and the reason is given. Where the plan says "copy", copy the named function verbatim and change
only the namespace.

---

## 1. Decisions, settled before any code

| Question | Answer | Why |
|---|---|---|
| Library | **SkiaSharp 3.119.0**, the version the server already pins, with `SkiaSharp.NativeAssets.Win32` and `SkiaSharp.NativeAssets.Linux.NoDependencies` | the server's `StillRenderer` is a working solution to this exact job; the Linux native runs the whole test suite here (§ 7) |
| Decode size | **decode at the codec's nearest native size at or above the pane fit, then one downscale** — never decode full and shrink | § 3.1. Measured: `bomb_40mp.jpg` at a 1080 px pane is 9.5 MB and 26 ms this way, 152 MB and 198–304 ms the other way |
| Upscaling | **never**, in the decoder or the downscale. A source smaller than the pane is delivered at source size; the pane draws it centred | `SPEC.md` § Pipeline "never upscale in the decoder"; the pane's own scaling is E's business |
| Long-edge cap | **4096** on the delivered frame | `SPEC.md` § Pipeline. Only an 8K monitor reaches it |
| Orientation | **applied to the pixels** by this part, from `SKCodec.EncodedOrigin`, using the server's `OrientationMatrix` | Skia reports orientation and never applies it (server comment on `Orient`); E receives an upright frame and knows nothing of EXIF |
| Colour | **decode into an sRGB destination colour space**, so Skia applies the embedded profile during decode; no profile, or a profile Skia cannot parse → the numbers are taken as sRGB | § 3.2; what 1.1.3 fixed. Measured on `wide_gamut.jpg`: Skia parses the Adobe RGB profile and the four bands come out different from `wide_gamut_untagged.jpg` |
| Pixel format handed to E | **BGRA8888, premultiplied alpha, sRGB, top-down rows, `RowBytes == Width * 4`**, in a native buffer | it is exactly `PixelFormats.Bgra8888` + `AlphaFormat.Premul`, which Avalonia's `WriteableBitmap(PixelFormat, AlphaFormat, IntPtr, PixelSize, Vector, int)` constructor copies from in one pass; it is also WPF's `Pbgra32` if the § 4 fallback is ever taken. One format, no branches in E |
| Two-pass 720 px first paint | **dropped** | § 3.4. Measured on `photo_large.jpg` (13 MP, 5 MB): the 720-class pass costs 109 ms and the pane-size pass costs 109 ms — the preview does not arrive sooner, it makes the real picture arrive twice as late |
| Animated GIF | **first frame only** | `gif` is a still by `SPEC.md` § Media policy; measured: `animated.gif` reports `FrameCount = 3` and `GetPixels` with `SKCodecOptions(frameIndex: 0)` succeeds |
| Concurrency | **the two visible panes decode concurrently (2 workers); warm pairs decode one at a time, and only while no visible decode is running or queued** | § 3.5. A deliberate reading of `SPEC.md` "one sequential reader", argued there; flagged for the integrator in § 9 |
| Prefetch depth | `PrefetchPairs = 2`, taken from `snapshot.prefetchPairs` when E passes it, default 2 | `SPEC.md` § Pipeline; `SERVER_SPEC.md` § 9.5 |
| What is cached | **exactly the wanted set**: the visible pair plus the warm pairs. Nothing else, ever. No LRU, no size-based cache | § 3.3. The folder's size never enters this part; 20 photographs and 20,000 cost the same |
| Memory ceiling | a **`DecodeBudget`** (copied from the server) of **512 MB** over every pixel buffer this part allocates; a decode that cannot fit alone is refused as `TooLarge`; one that cannot fit beside the other in-flight decode waits for it | § 3.3. The arithmetic says real files peak far below it; the ceiling exists so that a 200 MP PNG becomes a message on one pane, not an `OutOfMemoryException` |
| Frame lifetime | **reference-counted native buffers**: the cache holds one reference, E holds a lease while it copies | § 3.6. Eviction while E is mid-copy must be impossible by construction, not by timing |
| Failure | a **`StillFailure` enum** with four values and a one-line detail; never an exception across the seam, never a silent drop, never a state change | § 3.7; `PC_CLIENT_PLAN.md` § 6.5; `SERVER_SPEC.md` § 11.3 "the client is the one that decides" |
| Truncated files | **shown**, with `IsPartial = true` on the frame | Skia fills the rows it read (`SKCodecResult.IncompleteInput`); the server serves those and so does this part. E may badge it; the owner can discard it |
| Stale bytes | the cache key is **id + file length + last-write time**; `Show` re-stats and re-decodes if either moved | `SERVER_SPEC.md` § 11.3 "file replaced with different bytes, same name". One `stat` per id per `Show`, microseconds |
| Dependencies | `RankMaster2.Core` (for `MediaExtensions`, `MediaKind`) and SkiaSharp. **No Avalonia, no `RankMaster2.Catalog`, no server project** | the whole part must build and run on Linux under xunit with no UI; the fallback to WPF must not touch it |
| File handles | opened `FileShare.ReadWrite | FileShare.Delete`, `SequentialScan`, and closed the moment the codec is done — a handle lives only as long as one decode | the server's `OpenRead`, copied. On Windows a plain read handle blocks the server's `File.Move` behind a discard (`PC_CLIENT_PLAN.md` § 6.3) |

### 1.1 Deliberately absent

None of these is an oversight:

- **No thumbnails, no filmstrip, no EXIF-embedded preview.** `SPEC.md` non-goals; and the EXIF thumbnail is
  160 px of the wrong colour.
- **No disk cache of decoded frames.** The disk already holds the file, and decode-at-size is 20–110 ms
  (measured, § 3.4). A cache would be a second copy of the library in a second format.
- **No `/media` requests, no `mediaVersion` bookkeeping** (`PC_CLIENT_PLAN.md` § 2.1).
- **No HEIC, no RAW, no TIFF, no SVG.** `MediaExtensions.Still` is `jpg jpeg png gif bmp webp` and this part
  decodes what the catalog admits, nothing more.
- **No re-decode on resize.** `SPEC.md` § Pipeline: "do not re-decode during drag; the next fill uses the
  new panel size." `SetPaneSize` stores a number and starts nothing.
- **No decode of a warm pair before both visible panes have pixels** (§ 3.5).
- **No impression, no vote, no session call of any kind.** This part has no reference to part B and cannot
  reach the server; a warm pair is bytes on a disk and nothing else (`SERVER_SPEC.md` § 9.5).
- **No GPU upload.** The frame is CPU memory; how E turns it into a texture is E's.
- **No auto-skip and no auto-drop on a failed decode.** The old app called `Drop` from the UI thread; under
  the server the client cannot drop without moving, and it must not decide for the owner
  (`PC_CLIENT_PLAN.md` § 6.5).

---

## 2. The seams

The integrator writes the seam types as real files before execution begins (`PC_CLIENT_PARTS.md`). If
`IStillSource` or `IMediaProbe` already exists when this part starts, **use the existing file**; if it
differs from the shapes below in a way that breaks a rule in § 3, report the difference to the integrator
and stop — do not fork the type. The shapes below are what this part argues for.

Namespace for everything in this part: `RankMaster2.Pc.Stills`.

### 2.1 `IStillSource` — C → E

```csharp
namespace RankMaster2.Pc.Stills;

/// <summary>
/// Pixels for the ranking surface. One instance per process. Every method is safe to call from
/// any thread; <see cref="Changed"/> is raised on a worker thread (or synchronously inside
/// <see cref="Show"/>), never on a UI thread — E marshals.
/// </summary>
public interface IStillSource : IAsyncDisposable
{
    /// <summary>
    /// The size of one pane in PHYSICAL pixels (E multiplies by its render scaling). Stored; starts
    /// nothing. The next Show/Warm decodes to it. Before the first call the size is 960 × 1080.
    /// Values below 16 are ignored.
    /// </summary>
    void SetPaneSize(int widthPx, int heightPx);

    /// <summary>
    /// Make these two ids the visible pair. <paramref name="folder"/> is snapshot.folder verbatim;
    /// ids are pair.left.id / pair.right.id verbatim (on-disk spelling). Rebuilds the wanted set
    /// as {left, right} ∪ warm, evicts everything outside it, queues decodes for whichever of the
    /// two has no usable frame, and raises Changed for BOTH ids synchronously before returning
    /// (Ready if a frame is already cached, otherwise Pending).
    /// </summary>
    void Show(string folder, string leftId, string rightId);

    /// <summary>
    /// The server's warmPairs, oldest first, as (leftId, rightId). Replaces the previous warm list
    /// entirely; at most <paramref name="prefetchPairs"/> pairs are kept (extra ones ignored).
    /// Never raises Changed. Never counts as anything. Call after Show, with the same folder.
    /// </summary>
    void Warm(string folder, IReadOnlyList<(string LeftId, string RightId)> pairs, int prefetchPairs = 2);

    /// <summary>Current state of an id. Unknown ids are Pending.</summary>
    StillState StateOf(string id);

    /// <summary>State transitions for ids in the wanted set. Not raised for warm-only ids.</summary>
    event Action<string, StillState>? Changed;

    /// <summary>
    /// Forget everything about <paramref name="id"/> and return only when this part holds no file
    /// handle on it: the frame is dropped from the cache, any queued decode is removed, and an
    /// in-flight decode is waited for (a Skia decode cannot be interrupted; it is short). Then
    /// the file is probed for an exclusive open; returns false if something else still holds it
    /// after 2 s. E calls this before discard / special and before undo of a move. A file the
    /// server restores under a new name (lastAction.restoredId) is simply a new id: nothing to do.
    /// </summary>
    Task<bool> ReleaseAsync(string folder, string id, CancellationToken ct);

    /// <summary>Drop every frame, empty every queue, wait for in-flight decodes. Called on folder close.</summary>
    Task ReleaseAllAsync(CancellationToken ct);
}

public abstract record StillState
{
    /// <summary>Queued or decoding. E shows the filename and a spinner.</summary>
    public sealed record Pending : StillState;

    /// <summary>
    /// Pixels exist. The lease MUST be disposed by E once its bitmap copy is made (a `using` inside
    /// the handler is the expected shape). The buffer stays valid until the last lease is gone even
    /// if the cache has evicted the frame meanwhile.
    /// </summary>
    public sealed record Ready(StillLease Lease) : StillState;

    /// <summary>The file could not be shown. Detail is one line for a toast; never a stack trace.</summary>
    public sealed record Failed(StillFailure Reason, string Detail) : StillState;
}

public enum StillFailure
{
    /// <summary>File or folder not found. E: "file is gone"; only that side's discard is offered (→ drop_missing).</summary>
    Missing,
    /// <summary>Exists but cannot be read: sharing violation, permissions, I/O error. E: "cannot be read"; offer retry (re-Show) and discard.</summary>
    Unreadable,
    /// <summary>Exists, readable, not a decodable image. E: "not a valid image"; offer discard (ordinary, with undo) or a vote for the other side.</summary>
    NotAnImage,
    /// <summary>A real image whose decode would exceed the pixel budget on its own. E: same offers as NotAnImage, different sentence.</summary>
    TooLarge,
}

/// <summary>A reference to a frame. Dispose exactly once. Thread-agnostic.</summary>
public sealed class StillLease : IDisposable
{
    public StillFrame Frame { get; }
    public void Dispose();   // decrements the frame's count; frees the buffer at zero
}

/// <summary>An upright, sRGB, BGRA8888 premultiplied image at (or below) pane size.</summary>
public sealed class StillFrame
{
    public string Id { get; }
    public int Width { get; }            // displayed width, after orientation, ≤ pane width, ≤ 4096 long edge
    public int Height { get; }
    public int RowBytes { get; }         // always Width * 4
    public IntPtr Pixels { get; }        // valid while any lease is alive; throws ObjectDisposedException after
    public int SourceWidth { get; }      // the file's displayed size, after orientation (what MediaMeta.width would say)
    public int SourceHeight { get; }
    public bool IsPartial { get; }       // decode ended with IncompleteInput; rows past the cut are whatever Skia left (black)
    public long ByteSize => (long)RowBytes * Height;
}
```

What E does with a `Ready`: inside the `Changed` handler, `using var lease = ready.Lease;` then construct
its bitmap from `Frame.Pixels`, `Frame.Width`, `Frame.Height`, `Frame.RowBytes` (one copy, ≤ 16.6 MB at
4K, about a millisecond), then let the `using` end. If E instead stores the lease and disposes it when the
pane changes picture, that is also correct — the buffer simply lives longer. What E must not do is read
`Pixels` after disposing.

### 2.2 `IMediaProbe` — C, D → A

```csharp
namespace RankMaster2.Pc.Stills;

/// <summary>
/// "Does this folder contain video?" — answered from filenames alone. Opens no file, decodes
/// nothing. Uses exactly the eligibility rules of JsonCatalog.ListTopLevelMedia so its answer
/// agrees with the server's policy: top level only; skip rankmaster_db.json, *.tmp, Hidden and
/// System attributes; MediaExtensions.KindOf decides the kind.
/// </summary>
public interface IMediaProbe
{
    Task<FolderMedia> ProbeAsync(string folder, CancellationToken ct);
}

public readonly record struct FolderMedia(int Stills, int Videos)
{
    public int Total => Stills + Videos;
    public bool HasVideo => Videos > 0;
    /// <summary>What the server will call `policy` for this folder: still unless there are videos and no stills.</summary>
    public MediaKind Policy => Videos > 0 && Stills == 0 ? MediaKind.Video : MediaKind.Still;
    /// <summary>The question A actually asks: will a session here ever play a frame?</summary>
    public bool NeedsVideoEngine => Policy == MediaKind.Video && Videos >= 2;
}
```

This part implements the whole of it; nothing in it needs part D. If D wants to add "can the engine play
this particular file", that is a second member D defines and D implements — it is not this probe's
question. A folder that does not exist returns `(0, 0)`; an unreadable folder throws `IOException` /
`UnauthorizedAccessException` unchanged, because A needs to tell those apart from "empty".

Cost on a 20,000-file folder: one `Directory.EnumerateFiles` and one `File.GetAttributes` per file.
`PC_CLIENT_PLAN.md` § 3.2 measured the catalog's identical loop at 104–118 ms here on ext4. NTFS is slower
per attribute call; this is A's to measure, and the probe runs off the UI thread for that reason.

---

## 3. The hard problems, and how they are solved

### 3.1 Choosing the decode size

The whole part exists for this rule, so it is stated exactly. Inputs: the pane `(paneW, paneH)` in physical
pixels; the codec's `Info.Width × Info.Height` (stored orientation) and `EncodedOrigin`.

1. **Displayed source size.** If `SwapsAxes(origin)` (orientations 5–8), `srcW, srcH = Info.Height,
   Info.Width`; else `Info.Width, Info.Height`. This is what `MediaMeta.width/height` would report.
2. **Fit, never upscale, cap at 4096.** `scale = min(paneW / srcW, paneH / srcH, 1.0)`; then if
   `max(srcW, srcH) * scale > 4096`, `scale *= 4096 / (max(srcW, srcH) * scale)`. `fitW = max(1,
   round(srcW * scale))`, `fitH = max(1, round(srcH * scale))`. This is the old `StillDecoder.Fit`, kept
   verbatim. The frame E receives is exactly `fitW × fitH`.
3. **Target long edge.** `target = max(fitW, fitH)`. A long edge is the same number whichever way the
   picture is turned, which is why the codec can be asked in stored orientation without further
   thought (the server's "square box" argument).
4. **Ask the codec.** `scaled = ChooseScaledDimensions(codec, target)` — **copy the server's function**.
   It calls `SKCodec.GetScaledDimensions(target / longEdge)` and walks the scale upward until the
   candidate's long edge is `≥ target`, so the step that follows is always a downscale. Measured
   behaviour of the codecs:
   - **JPEG**: produces N/8 of the source, N = 1…8 (`0.75 → 6000×3750` on the 8000×5000 bomb; `0.3 →
     2000×1250`, i.e. it rounds down, which is why the walk-up loop exists). Worst case the decode
     buffer is just under 2× the target on each axis, 4× its area — when the source is between 8 and 16
     times the target. For a 4K pane (target 2160) that is a source over 17,000 px wide, which does
     not exist in a camera library.
   - **WebP**: any size (`0.3 → 480×360` on 1600×1200). Time does not fall with size (45–55 ms at every
     scale for `lossless.webp`) — WebP decodes in full internally — but memory does, which is what
     matters here.
   - **PNG, BMP**: source size only, at every scale. Asking `GetPixels` for anything else returns
     `SKCodecResult.InvalidScale` (measured). So for these the decode buffer is the full image and the
     downscale does all the work.
   - **GIF**: reports scaled sizes (`0.3 → 72×54` on 240×180). Take the codec's word for it and fall
     back as below if `GetPixels` disagrees.
5. **Decode.** `SKImageInfo(scaled.Width, scaled.Height, SKColorType.Bgra8888, SKAlphaType.Premul,
   SKColorSpace.CreateSrgb())` into a `DecodeBudget` buffer via `SKBitmap.InstallPixels`, then
   `codec.GetPixels(info, pointer)` (for a multi-frame codec, with `new SKCodecOptions(0)`). Results:
   `Success` → continue; `IncompleteInput` → continue with `IsPartial = true`; `InvalidScale` → free the
   buffer, retry once at `Info.Width × Info.Height` (this is the PNG path if the codec lied); anything
   else → `NotAnImage`.
6. **Downscale.** If `scaled != (fitW', fitH')` where `(fitW', fitH')` is the fit in **stored**
   orientation (swap `fitW, fitH` back if `SwapsAxes`), allocate `fitW' × fitH'` from the budget and
   `decoded.ScalePixels(destination, new SKSamplingOptions(SKCubicResampler.Mitchell))`. Free the decode
   buffer **before** the next step. Mitchell, not Catmull-Rom or bilinear, for the reason in the server's
   comment; measured 20–34 ms for a 2000-class source to a 960-px pane.
7. **Orient.** If origin is `TopLeft`/`Default`, done. Else allocate `fitW × fitH` (displayed), create an
   `SKSurface` on it, `SetMatrix(OrientationMatrix(origin, fitW', fitH'))`, draw the fitted bitmap with
   nearest sampling, free the fitted buffer. **Copy the server's `Orient`, `OrientationMatrix` and
   `SwapsAxes`** — the eight matrices are already tested there (`OrientationMatricesPlaceTheStoredOrigin`).

Peak memory during one decode is therefore `decodeBuffer + fitBuffer` (step 6), then `fitBuffer +
orientedBuffer` (step 7), never all three. A 48 MP JPEG at a 4K pane: 3000×1875×4 = 22.5 MB plus
1920×1200×4 = 9.2 MB. A 48 MP PNG at the same pane: 192 MB plus 9.2 MB. Both inside the ceiling.

**Reusing a frame after the pane changed size.** A cached frame is usable for a new pane if its long edge
`≥ target` computed for the new pane, or if it is at source size (`Width == SourceWidth`). A frame that is
too small for a bigger pane is re-decoded at the next `Show`, not before (`SPEC.md`: next fill).

### 3.2 Colour and orientation, which is what 1.1.3 was

Both are silent failures — the picture is valid either way — so both are pixel-asserted in tests (§ 6),
not status-asserted.

**Colour.** Skia converts from `codec.Info.ColorSpace` to the destination colour space during
`GetPixels`. Asking for `SKColorSpace.CreateSrgb()` as the destination is the entire mechanism; there is
no second step. Three cases, all measured on this box:

| File | `Info.ColorSpace` | What happens |
|---|---|---|
| `wide_gamut.jpg` (synthesised Adobe RGB matrix/TRC profile) | non-sRGB, parsed | converted; bands measured `#50b653 #358dca #c77935 #a75aae` |
| `wide_gamut_untagged.jpg` (same pixels, no profile) | sRGB | passed through; bands `#78b45a #5a8cc7 #b4783c #955aaa` |
| a LUT-based (v4 `mAB`/`mBA`) profile Skia cannot parse | `null` | Skia treats the numbers as sRGB — the same as untagged. The server's corpus test skips in this case with a note; this part does the same and does **not** attempt its own colour management |

`cmyk.jpg` (4-component JPEG) and `grayscale.jpg` (`Gray8` codec colour type) both decode straight into
BGRA8888 with `Success` and a centre pixel matching the RGB encodings of the same picture (`#774211`
against `#784211`) — no special path.

**Orientation.** `SKCodec.EncodedOrigin` mirrors TIFF `Orientation` 1–8 exactly, and Skia applies none
of it. Step 7 of § 3.1 applies it. The sixteen `exif_*` files are the ground truth: all eight of one shape
must come out the same displayed size (`landscape` 1800×1200-shaped, `portrait` 1200×1800-shaped) and
the same way up. Files 5–8 are stored transposed (`exif_landscape_6.jpg` is 1200×1800 on disk with
`RightTop`) — that is what step 1 and the swap in step 6 are for.

### 3.3 Memory: what is held, what evicts, and 20,000 photographs

The phone died twice of memory because two budgets nobody had summed were each allowed to fill
(`android/MEMORY_PROPOSALS.md` § 1). This part has one budget and can state it as a table.

**Resident.** Frames for the wanted set only: 2 visible + 2 × `PrefetchPairs` warm = **6 frames at most**,
each at most the pane fit:

| Monitor | Pane (physical) | One frame | Six frames |
|---|---|---|---|
| 1920 × 1080 | 960 × 1080 | 4.1 MB | 25 MB |
| 2560 × 1440 | 1280 × 1440 | 7.4 MB | 44 MB |
| 3840 × 2160 | 1920 × 2160 | 16.6 MB | 100 MB |

A frame is never larger than that because step 2 of § 3.1 never upscales and step 6 always lands on the
fit. Smaller sources give smaller frames.

**Transient.** Per decode, § 3.1's peak: JPEG ≤ 4 × frame + frame; PNG/BMP = full source + frame. Two
visible decodes may run at once, one warm decode otherwise. Worst realistic case: two 48 MP PNGs at a 4K
pane, 2 × 201 MB = 402 MB, inside the ceiling; the second waits for the first if it would not fit beside
it.

**The ceiling.** `DecodeBudget` — **copy `src/RankMaster2.Server/Media/DecodeBudget.cs` verbatim**
(`DecodeBudget`, `BudgetedBuffer`, `DecodeBudgetExceededException`), namespace changed — with
`CeilingBytes = 512 MB`. Every pixel buffer this part allocates comes from it, so `LiveBytes` is the
true native footprint of stills and `PeakBytes` is what the tests assert against. The rule for a request
of `n` bytes: if `n > Ceiling` → `TooLarge` immediately; if `live + n > Ceiling` and `live > 0` → wait
(the other decode will finish in well under a second) and retry; else allocate. Skia's own internal
buffers (Huffman tables, one row of coefficients) are not counted, for the reason the server's comment
gives: they scale with width, not megapixels.

**Eviction.** `Show` and `Warm` each rebuild `wanted = {left, right} ∪ warm ids`; every cached frame whose
id is not in `wanted` has its cache reference released **immediately, synchronously, in that call** — the
old `MediaPipeline.EvictUnwanted`, unchanged in spirit. A decode that finishes for an id no longer wanted
frees its frame on the spot. No timer, no LRU, no memory pressure callback: the wanted set *is* the
policy, and it has six members.

**20,000 photographs.** This part never enumerates the folder except in the probe (names only, no
handles, no state kept). It holds nothing per file that is not in the wanted set — no header cache, no
dimensions table, no per-id state beyond the six ids. The only thing that grows with the library is the
server's JSON, which is not this part's problem. A test proves it: 2,000 `Show`/`Warm` cycles over random
ids from a 20,000-name synthetic folder end with `Budget.LiveBytes` equal to the byte size of the six
resident frames and nothing else (§ 6, `StillSourceTests.ResidentMemoryIsBoundedByTheWantedSet`).

### 3.4 Time, and why the two-pass first paint is gone

Measured here, `photo_large.jpg` (4390 × 2948, 5.0 MB, a real camera JPEG), decode into BGRA sRGB:

| Requested | Codec produced | Buffer | Decode | Mitchell to a 960 × 1080 pane |
|---|---|---|---|---|
| 1/8 | 549 × 369 | 0.8 MB | 79 ms | 3 ms |
| 1/4 | 1098 × 737 | 3.1 MB | 109 ms | 30 ms |
| 3/8 | 1647 × 1106 | 6.9 MB | 106 ms | 24 ms |
| 1/2 | 2195 × 1474 | 12.3 MB | 148 ms | 22 ms |
| 1 | 4390 × 2948 | 49.4 MB | 219 ms | 21 ms |

Entropy decoding dominates a real JPEG, and it is paid in full at every scale: 1/8 costs 79 ms, 1/4 costs
109 ms. `SPEC.md`'s optional 720 px first paint would be the 1/4 row (549 px does not reach 720), which is
the **same row the pane needs**. Two passes would put the final picture on screen at ~220 ms instead of
~110 ms and show nothing sooner. So: one pass. `SPEC.md` says "optional" and this part takes the option.

For the 8000 × 5000 bomb the story is the opposite (18 / 44 / 80 / 85 / 304 ms) because it has almost no
entropy data — which is exactly the file decode-at-size was invented for, and it is the one real
photographs do not resemble.

A 48 MP camera JPEG at 20–25 MB is estimated (not measured — none in the corpus) at 4–5 × the 13 MP file:
400–550 ms at pane size. Two of them sequentially would be ~1 s, the whole of the T2 budget
(`PC_CLIENT_PLAN.md` § 10 point 3). That estimate is why § 3.5 decodes the visible pair concurrently.

### 3.5 Concurrency: two visible, one warm, in that order

`SPEC.md` § Pipeline: "One sequential reader. Never two huge files at once. Never prefetch the next file
until the current read has finished." The rule's stated reason is I/O — 100–200 MB files over USB 2 —
and its purpose is that prefetch must never slow the pair on screen. Stills are 2–25 MB and the measured
cost is CPU (§ 3.4), which a desktop has cores for. So this part reads the rule as:

- **Visible ids: up to 2 decodes at once**, one per pane. Both panes are "the current read".
- **Warm ids: one at a time, and only when no visible decode is queued or running.** This is the clause
  that protects the owner's picture, kept exactly.
- **A `Show` pre-empts**: its two ids go to the head of the queue; a warm decode already in `GetPixels`
  is allowed to finish (it cannot be interrupted and is short) and its frame is kept only if still wanted.

Mechanics: one `PriorityQueue<DecodeJob, (int Rank, long Seq)>` with rank 0 for visible and 1 for warm,
two worker threads named `RankMaster2.Stills.0/1`. A worker takes a rank-1 job only if the queue holds no
rank-0 job **and** the other worker is not running a rank-0 job. Duplicate jobs for one id are collapsed
(a `HashSet<string>` of queued ids, as the old `_queued`).

This is a deliberate deviation from a literal reading of `SPEC.md`, argued here and listed in § 9 for the
integrator. If it is refused, `VisibleWorkers` is one constant and the plan is otherwise unchanged.

### 3.6 Lifetimes: native buffers and a UI that copies

`StillFrame.Pixels` is native memory. The cache may evict the frame at any `Show`; E may be mid-copy on
another thread. The only construction that makes the race impossible is a count:

- `StillFrame` has an `int _refs`. The cache's reference is one. Each `StillLease` is one.
- `Ready` events carry a lease already acquired **for E**; E disposes it. `StateOf` returning `Ready` also
  acquires a fresh lease each call, for the same reason.
- When `_refs` reaches zero the `BudgetedBuffer` is disposed (which refunds the budget). `Pixels` throws
  `ObjectDisposedException` afterwards.
- A `StillLease` disposed twice is a no-op; a `StillLease` never disposed is a leak that a `DEBUG`-only
  finalizer reports to `Debug.WriteLine` with the id — the one thing this part can do about E's mistake.

Nothing is copied to a managed array on the way: the native buffer Skia decoded into is the buffer E copies
from.

### 3.7 Failure: what E is told and what the owner can do

`SPEC.md` § Media policy: "Unreadable / corrupt files are skipped for that pair. The session does not
crash." Under the server the client cannot skip on its own, so "skipped" becomes "stated, and the owner
chooses" (`PC_CLIENT_PLAN.md` § 6.5). The mapping, decided here so E does not have to:

| What happened | Detection | `StillFailure` | What E shows | Keys E accepts for that side |
|---|---|---|---|---|
| File gone (deleted, renamed, moved) | `FileNotFoundException` / `DirectoryNotFoundException` on open, or `stat` fails at `Show` | `Missing` | "file is gone" | only discard → server `drop_missing`, no undo (`SERVER_SPEC.md` § 10.8) |
| Locked by another program, permissions, disk error | `UnauthorizedAccessException`, `IOException` other than not-found | `Unreadable` | "cannot be read: <one line>" | retry (E calls `Show` again), discard |
| Not an image: empty, 2 bytes, noise, text, header cut off | `SKCodec.Create` returns `null` (`Unimplemented`, `IncompleteInput`, …) or `GetPixels` returns anything but `Success`/`IncompleteInput`/`InvalidScale`; `Info.Width` or `Height` ≤ 0; any other exception from Skia | `NotAnImage` | "not a valid image (<result>)" | discard (ordinary, with undo), or vote for the other side |
| Real image, absurd size | `n > CeilingBytes` for the decode buffer | `TooLarge` | "too large to show (W × H)" | same as `NotAnImage` |
| Scan data cut short | `GetPixels` = `IncompleteInput` | **not a failure** — `Ready`, `IsPartial = true` | the picture, optionally badged | everything |

Measured on `tests/corpus/media/broken/`: `empty.jpg`, `header_only.jpg`, `noise.jpg`,
`text_pretending.jpg` → codec `null`, `Unimplemented`; `truncated.jpg` (cut at 3000 bytes, inside the EXIF
segment) → codec `null`, `IncompleteInput`. All five are `NotAnImage` today. A file truncated *after* its
headers would be the `IsPartial` row; the test accepts either outcome for `truncated.jpg`, as the server's
does.

The exception filter is the server's `IsDecodeFailure`, copied: everything is `NotAnImage` except
`OperationCanceledException`, the not-found pair, `UnauthorizedAccessException` (→ `Unreadable`),
`OutOfMemoryException` and `StackOverflowException` (rethrown — those are not the file's fault).

Failures are cached like frames — as the id's state in the wanted set — so a `Show` of the same broken
file does not decode it again; `ReleaseAsync` or eviction clears it; a retry is E calling `Show` after
`ReleaseAsync`.

### 3.8 Release before the server moves the file

`PC_CLIENT_PLAN.md` § 6.3: stop the player, drop the bitmap, `WaitUntilUnlocked`, then `POST
/session/discard`. This part's share is `ReleaseAsync(folder, id)`:

1. Remove `id` from `wanted`; drop its queued job; drop the cache's frame reference and its cached
   failure.
2. If a decode of `id` is in flight, await its completion (a `TaskCompletionSource` per job).
3. Probe: try `new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)` up to 40 times
   at 50 ms (the loop of `RankMaster2.Catalog.FileOps.WaitUntilUnlocked`, **rewritten here in 15 lines
   rather than referenced**, so this part does not depend on `Catalog`). Success → `true`. A file that is
   already gone → `true` (nothing to release). Still locked after 2 s → `false`, and E says "file is in
   use by another program" instead of letting the server answer `500 move_failed` after its own retries.

Step 3 is only meaningful on Windows (Linux does not enforce sharing); the test here proves steps 1–2 and
that step 3 returns `true` when nothing holds the file. The Windows behaviour is inherited from the old app,
which is the evidence `PC_CLIENT_PLAN.md` § 12 cites.

Frames E is still leasing are **not** a handle: the file is closed after decode. So a lease outstanding
does not block a move, and `ReleaseAsync` does not wait for E.

---

## 4. Files

```
pc/src/RankMaster2.Pc/Stills/
  IStillSource.cs        the § 2.1 seam: IStillSource, StillState, StillFailure, StillLease, StillFrame  (or the integrator's frozen file)
  IMediaProbe.cs         the § 2.2 seam: IMediaProbe, FolderMedia                                          (or the integrator's frozen file)
  DecodeBudget.cs        COPIED from src/RankMaster2.Server/Media/DecodeBudget.cs, namespace only changed
  DecodeGeometry.cs      pure functions, no I/O: Fit (old StillDecoder.Fit), FitLongEdge, ChooseScaledDimensions,
                         OrientationMatrix, SwapsAxes (the last four COPIED from StillRenderer), FrameCovers(frame, pane)
  StillDecoder.cs        path + pane + budget → StillFrame or StillFailure; § 3.1 steps 1–7, § 3.7 mapping; OpenRead copied
  StillFrame.cs          the ref-counted frame and StillLease (§ 3.6)
  FrameCache.cs          wanted set, frames, failures, the (length, mtime) stat key, EvictUnwanted
  DecodeQueue.cs         PriorityQueue + 2 workers + the rank rule of § 3.5 + per-job completion sources
  StillSource.cs         IStillSource over the three above; the only public entry point besides the probe
  MediaProbe.cs          IMediaProbe (§ 2.2); the catalog's eligibility loop, rewritten, names only
  HandleCheck.cs         the exclusive-open probe of § 3.8 step 3

pc/tests/RankMaster2.Pc.Stills.Tests/
  RankMaster2.Pc.Stills.Tests.csproj   net8.0, xunit 2.9, references pc/src/RankMaster2.Pc/RankMaster2.Pc.csproj
  Corpus.cs                            COPIED from the bottom of tests/RankMaster2.Server.Tests/Media/StillRendererCorpusTests.cs
  Fixtures/TinyBmp.cs                  a 2×2 24-bit BMP written by hand (the corpus has no .bmp)
  Fixtures/FakeDecoder.cs              an IStillDecoder that returns a frame of a given size after a controllable delay
  DecodeGeometryTests.cs
  StillDecoderCorpusTests.cs
  StillDecoderColourTests.cs
  StillDecoderBrokenTests.cs
  DecodeBudgetTests.cs
  StillFrameLeaseTests.cs
  StillSourceTests.cs                  ordering, eviction, release, memory — against FakeDecoder
  StillSourceCorpusTests.cs            the same class against the real decoder and the real corpus
  MediaProbeTests.cs
  TimingReportTests.cs                 prints the § 3.4 table for this box; asserts loose upper bounds
```

`StillDecoder` is behind a small internal interface `IStillDecoder { DecodeResult Decode(string path, int
paneW, int paneH); }` so `StillSource` can be tested for ordering and memory with `FakeDecoder` and no
disk. `DecodeResult` is either a `StillFrame` or a `(StillFailure, string)`.

**Prerequisites from part A** (project files are A's): `pc/src/RankMaster2.Pc/RankMaster2.Pc.csproj` must
reference `SkiaSharp 3.119.0`, `SkiaSharp.NativeAssets.Win32 3.119.0`,
`SkiaSharp.NativeAssets.Linux.NoDependencies 3.119.0` and the `RankMaster2.Core` project. No `unsafe`
code is needed — everything is `IntPtr` through Skia. If the project file is missing any of these when
this part starts, report to the integrator; do not edit it.

---

## 5. Phases

Each phase ends with `dotnet test pc/tests/RankMaster2.Pc.Stills.Tests` green on this box. Run no `git`
command; the integrator commits.

**Phase 0 — read, and check the ground.** Read `StillRenderer.cs`, `DecodeBudget.cs`,
`src/RankMaster2.App/Pipeline/*.cs`, `SPEC.md` § Pipeline and § Media policy, `SERVER_SPEC.md` § 9.5 and
§ 11.3. Confirm `tests/corpus/media/stills` has 30 files and `broken` has 7 (it does today;
`manifest.json` lists them). Confirm the § 4 prerequisites. Create the test project. Exit: an empty test
passes.

**Phase 1 — geometry and budget, no files.** `DecodeGeometry.cs` (copies plus `Fit` and `FrameCovers`),
`DecodeBudget.cs` (copy), `StillFrame.cs`. Tests: `DecodeGeometryTests`, `DecodeBudgetTests`,
`StillFrameLeaseTests`. Exit: every § 3.1 arithmetic example in this document is a passing test case.

**Phase 2 — the decoder against the corpus.** `StillDecoder.cs`. Tests: `StillDecoderCorpusTests`,
`StillDecoderColourTests`, `StillDecoderBrokenTests`, `TimingReportTests`. Exit: all 30 stills and 7
broken files behave as § 3.7 and § 6 say; the timing table prints.

**Phase 3 — the source.** `FrameCache.cs`, `DecodeQueue.cs`, `StillSource.cs`, `HandleCheck.cs`. Tests:
`StillSourceTests` (fake decoder), `StillSourceCorpusTests` (real). Exit: the ordering, eviction, memory
and release tests of § 6 pass; a warm-then-shown pair is `Ready` synchronously inside `Show`.

**Phase 4 — the probe.** `MediaProbe.cs`, `MediaProbeTests`. Exit: agrees with `JsonCatalog` on every
rule in § 2.2, measured under 500 ms on a synthetic 20,000-name folder here.

**Phase 5 — hand-off.** A note to the integrator listing: the timing table from this box; which seam
shapes differed from § 2 (if any); the § 9 items. Nothing else — no README, no doc file.

---

## 6. Tests, by name, with the corpus file each uses

Every corpus test begins with the server's skip idiom: if `Corpus.Directory("stills")` is `null`, write
"skipped: corpus not built" to the output and return. The corpus is present on this box; the idiom is for
a clone without it.

### `DecodeGeometryTests` (no files)

- `Fit_never_upscales`: `Fit(600, 400, 960, 1080)` → `(600, 400)`.
- `Fit_lands_on_the_binding_axis`: `Fit(4390, 2948, 960, 1080)` → `(960, 645)`; `Fit(1200, 1800, 960, 1080)`
  → `(720, 1080)`.
- `Fit_caps_the_long_edge_at_4096`: `Fit(10000, 5000, 8000, 8000)` → `(4096, 2048)`.
- `FitLongEdge_matches_the_server` — the server's own cases, copied.
- `OrientationMatrices_place_the_stored_origin` — copy the server's theory
  (`OrientationMatricesPlaceTheStoredOrigin`) unchanged.
- `SwapsAxes_is_true_for_5_to_8_only`.
- `FrameCovers`: a 960×645 frame covers a 960×1080 pane; does not cover 1920×2160; a frame at source size
  covers any pane.

### `StillDecoderCorpusTests`

- `The_bomb_is_decoded_inside_a_small_budget` — `bomb_40mp.jpg`, pane 960×1080, budget 32 MB: `Ready`,
  frame `960×600`, `Budget.PeakBytes < 32 MB`, `LiveBytes == 0` after the frame is released. (Measured:
  target 960 → the codec's 1/8 step, 1000×625 = 2.5 MB, plus the 2.3 MB fit. At a 4K pane the target is
  1920 and the 2/8 step, 2000×1250 = 9.5 MB, is taken.)
- `All_eight_orientations_of_one_shape_give_one_displayed_size` — `exif_landscape_1..8.jpg` and
  `exif_portrait_1..8.jpg`, pane 960×1080: within a shape, all eight frames have identical `(Width,
  Height)` and identical `(SourceWidth, SourceHeight)` (landscape `1800×1200`, portrait `1200×1800`).
- `Orientation_is_applied_to_the_pixels_not_just_the_size` — `exif_landscape_1.jpg` against
  each of `exif_landscape_2..8.jpg`, and the same for `portrait`, pane 960×1080: mean channel difference
  between the upright frames, sampled on a 9×9 grid (the server's `MeanDifference`), is `< 6/255` — the
  same picture, so the pixels must agree once upright. Measured here: 0.1–3.4/255 upright; 38–84/255 if
  the orientation step is skipped (and for 5–8 the frame is the wrong shape besides). The digit painted
  into each corpus image is small enough not to move the mean. A decoder that orients the size but not
  the pixels fails this and passes the previous test.
- `Every_real_encoding_gives_a_frame_of_the_expected_size` — theory over `progressive.jpg`,
  `grayscale.jpg`, `cmyk.jpg`, `interlaced.png`, `lossy.webp`, `lossless.webp`, `animated.gif`, `tiny.png`,
  `photo_cat.jpg`, `photo_large.jpg`, `wide_gamut.jpg`, plus `Fixtures/TinyBmp`; pane 960×1080: `Ready`,
  `IsPartial == false`, `(Width, Height) == Fit(SourceWidth, SourceHeight, 960, 1080)`, `RowBytes ==
  Width * 4`, `LiveBytes == frame.ByteSize` while leased and `0` after.
- `A_small_image_is_delivered_at_source_size` — `tiny.png` (320×240) and `wide_gamut.jpg` (600×400),
  pane 960×1080: frame equals source size.
- `A_png_decodes_at_full_size_then_downscales` — `interlaced.png`, pane 480×360, budget 16 MB: `Ready`,
  `PeakBytes` between 7.3 MB and 9 MB (full 1600×1200 buffer + fit), proving the `InvalidScale` path is
  never hit and the fit is taken.
- `The_first_frame_of_an_animated_gif_is_used` — `animated.gif`: `Ready`, 240×180, and the frame's centre
  pixel equals `#753d13` ± 4 per channel (measured frame 0; frame 1 is rotated 90° and differs).
- `Alpha_is_premultiplied` — `lossless.webp` (declares an alpha channel, though every pixel is opaque, so
  this proves the format contract, not the maths): decode succeeds with `Premul`; for every sampled
  pixel `B, G, R ≤ A` and `A == 255`.
- `A_pane_change_is_honoured_at_the_next_decode` — `photo_large.jpg` at 960×1080 → 960×645; then at
  1920×2160 → 1920×1289.

### `StillDecoderColourTests`

- `The_wide_gamut_file_is_converted_and_the_untagged_twin_is_not` — `wide_gamut.jpg` and
  `wide_gamut_untagged.jpg`, pane 960×1080 (both come out 600×400). Sample the centre of each of the four
  100-px bands. Mean channel difference between the two files `> 8/255` (measured 11.6/255; band 0
  alone is `(0x50 vs 0x78, 0xb6 vs 0xb4, 0x53 vs 0x5a)`). Then decode `wide_gamut.jpg` a second time with a `null`
  destination colour space (test-only path through `SKCodec` directly, as the server's test does) and
  assert those raw numbers match `wide_gamut_untagged.jpg`'s frame within `2/255` — proving the
  difference *is* the transform and nothing else. If `codec.Info.ColorSpace` is `null` on some future
  Skia, print "skipped: profile not parsed" and return, exactly as the server's test does.
- `An_untagged_file_is_left_alone` — `wide_gamut_untagged.jpg`: frame band colours within `3/255` of the
  values Pillow wrote (`(120,180,90) (90,140,200) (180,120,60) (150,90,170)`, JPEG q98).
- `Cmyk_and_grayscale_agree_with_the_rgb_encodings` — `cmyk.jpg`, `grayscale.jpg`, `progressive.jpg`,
  `lossy.webp` are one picture (built from the same 1600×1200 resize by `build-corpus.sh`): `cmyk.jpg`
  vs `progressive.jpg` mean difference `< 8/255` (measured 1.1/255; `lossy.webp` vs `progressive.jpg` is
  2.2/255); `grayscale.jpg`'s frame has `R == G == B` on every sampled pixel (measured: all 81 samples
  neutral).

### `StillDecoderBrokenTests`

Theory over `broken/empty.jpg`, `header_only.jpg`, `noise.jpg`, `text_pretending.jpg`,
`truncated.jpg`, and `broken/truncated.mp4` renamed to `.jpg` in a temp folder (a video wearing the
wrong extension — the catalog admits by extension, so this part will be handed it):

- result is `Failed(NotAnImage, detail)` with a non-empty one-line detail containing no newline, **or**
  `Ready` with `IsPartial == true` (allowed for `truncated.jpg` only);
- `Budget.LiveBytes == 0` afterwards;
- the decoder instance decodes `photo_cat.jpg` successfully immediately after — nothing is poisoned.

Plus:

- `A_missing_file_is_Missing` — a path in the corpus folder that does not exist → `Failed(Missing)`;
  a path in a folder that does not exist → `Failed(Missing)`.
- `An_unreadable_file_is_Unreadable` — Linux only (`chmod 000` a copy of `photo_cat.jpg` in a temp dir,
  skip if running as root) → `Failed(Unreadable)`.
- `Too_large_for_the_ceiling_is_TooLarge` — `bomb_40mp.jpg` with a 4 MB budget at a 4K pane (the 1/4
  decode buffer is 9.5 MB) → `Failed(TooLarge)`; detail contains `8000 × 5000`.

### `DecodeBudgetTests` — the budget half of `tests/RankMaster2.Server.Tests/Media/StillRendererMemoryTests.cs`
if it can be lifted; otherwise: allocate to the ceiling, the next allocation throws, dispose refunds,
`PeakBytes` records the high-water mark, double dispose is a no-op.

### `StillFrameLeaseTests` (fake buffers)

- cache reference dropped while a lease is alive → `Pixels` still valid; lease disposed → buffer freed,
  `Budget.LiveBytes` falls by `ByteSize`, `Pixels` throws `ObjectDisposedException`.
- two leases, disposed in either order → freed exactly once.
- a lease disposed twice → no-op.

### `StillSourceTests` (FakeDecoder, no corpus)

- `Show_raises_Pending_then_Ready_for_both_ids`, in that order per id, `Changed` never raised on the
  calling thread except the synchronous Pending/Ready inside `Show`.
- `Show_of_a_warm_pair_is_Ready_synchronously` — `Warm([(c,d)])`, wait for both frames, `Show(c,d)`:
  both `Changed` events fire inside `Show` with `Ready`, and `FakeDecoder` is not called again.
- `Warm_never_runs_while_a_visible_decode_is_pending` — FakeDecoder with a 200 ms delay; `Show(a,b)`,
  `Warm([(c,d),(e,f)])`: the decoder's call log shows `a` and `b` started before any of `c d e f`, `c d
  e f` strictly one at a time, in order.
- `Show_preempts_warm` — while `c` is decoding, `Show(g,h)`: `g` and `h` start before `d`.
- `Visible_ids_decode_two_at_once` — `Show(a,b)`: both start within 20 ms of each other.
- `Duplicate_requests_collapse` — `Show(a,b)` twice quickly: FakeDecoder called once per id.
- `Eviction_is_exactly_the_wanted_set` — `Show(a,b)`, `Warm([(c,d),(e,f)])`, all Ready; `Show(c,d)`,
  `Warm([(e,f),(g,h)])`: `a`, `b` are freed (their `Pixels` throw once E's leases are disposed), `c d e f`
  kept, `g h` queued.
- `Extra_warm_pairs_beyond_prefetchPairs_are_ignored` — `Warm` with 3 pairs and `prefetchPairs = 2`:
  the third pair is never decoded.
- `Warm_raises_no_Changed` — subscribe, `Warm` only: zero events.
- `ResidentMemoryIsBoundedByTheWantedSet` — 20,000 synthetic ids; 2,000 random `Show` + `Warm` cycles with
  FakeDecoder producing 4.1 MB frames, E's leases disposed promptly: at the end `Budget.LiveBytes ==
  6 × 4.1 MB` at most, and `PeakBytes ≤ 8 × 4.1 MB` (six resident + two in flight). No growth.
- `A_stale_file_is_redecoded` — real files in a temp folder: `Show(a,b)`, Ready; overwrite `a` with
  different bytes and a new mtime; `Show(a,b)` again → `a` Pending then Ready, `b` Ready synchronously.
- `ReleaseAsync_waits_for_the_inflight_decode` — FakeDecoder with a 300 ms delay; `Show(a,b)`; at 50 ms
  `ReleaseAsync(a)`: returns after the decode completes, returns `true`, `StateOf(a)` is `Pending`
  (unknown), no frame for `a` retained, `Budget.LiveBytes` reflects `b` only.
- `ReleaseAsync_on_a_locked_file_returns_false` — **Windows-only fact; on Linux this test asserts `true`
  and prints "sharing not enforced on this OS".**
- `ReleaseAllAsync_then_Show_works` — the source is reusable after a folder close.
- `Failed_state_is_cached_and_cleared_by_Release` — FakeDecoder fails `a` once: `Show(a,b)` twice calls
  the decoder for `a` once; `ReleaseAsync(a)` then `Show(a,b)` calls it again.
- `DisposeAsync_stops_the_workers_and_frees_everything` — `LiveBytes == 0`, worker threads exited.

### `StillSourceCorpusTests` (real decoder; the corpus `stills/` folder is the "library")

- `A_real_pair_becomes_Ready_within_a_second` — `Show("photo_large.jpg", "exif_portrait_6.jpg")` at
  960×1080: both `Ready` within 1,000 ms on this box; frames `960×645` and `720×1080`.
- `Warm_pairs_make_the_next_Show_instant` — `Show(photo_cat, photo_large)`, `Warm([(exif_landscape_6,
  lossless.webp)])`, wait; `Show(exif_landscape_6, lossless.webp)` returns with both `Ready` and the
  call took `< 5 ms`.
- `A_broken_file_in_a_pair_does_not_stop_the_other` — `Show("broken/noise.jpg" copied into a temp
  library, "photo_cat.jpg")`: `Failed(NotAnImage)` for one, `Ready` for the other.
- `A_vanished_file_is_Missing_and_Release_still_succeeds` — copy `photo_cat.jpg` to a temp library,
  `Show`, delete it, `Show` again → `Failed(Missing)`; `ReleaseAsync` → `true`.

### `MediaProbeTests` (temp folders)

- stills only → `(n, 0)`, `Policy == Still`, `NeedsVideoEngine == false`.
- videos only, two files → `(0, 2)`, `Policy == Video`, `NeedsVideoEngine == true`; one file → `false`.
- mixed → `Policy == Still`, `HasVideo == true`, `NeedsVideoEngine == false`.
- `rankmaster_db.json`, `x.tmp`, a subfolder with media, a `.hidden` file (set the `Hidden` attribute where
  the OS allows; on Linux skip that case) are not counted.
- unknown extensions (`.txt`, `.heic`) not counted.
- a missing folder → `(0, 0)`; a folder with no permission (Linux, `chmod 000`, skip as root) throws.
- `Twenty_thousand_names_probe_under_half_a_second` — 20,000 empty files, alternating `.jpg`/`.mp4`, timed.
- `Agrees_with_the_catalog` — build a folder mixing all the above; compare with
  `RankMaster2.Catalog.JsonCatalog`'s listing **in the test project only** (the test project may
  reference `Catalog`; the production code must not).

### `TimingReportTests`

Prints, for `photo_large.jpg`, `bomb_40mp.jpg`, `lossless.webp`, `interlaced.png`, `exif_portrait_6.jpg`:
decode-to-frame wall time and `PeakBytes` at 960×1080 and 1920×2160. Asserts only loose bounds on this box
(`photo_large` at 960×1080 `< 400 ms`; `bomb` `PeakBytes < 40 MB` at 4K). The table is what phase 5 hands
over.

---

## 7. What can be verified here, and what cannot

**Everything in § 6 runs on this box.** Decoding a JPEG has nothing to do with Windows; the same
`libSkiaSharp` code path runs under the Linux native, and the probe measured the full corpus here. That is
the reason this part carries the most tests of the five.

Not provable here, stated so nobody mistakes it for proven:

- **Windows file sharing** — that a decode's closed handle really does let `File.Move` succeed, and that
  `HandleCheck` returns `false` on a file another program holds. Inherited from the old app's behaviour
  (`PC_CLIENT_PLAN.md` § 12).
- **`SkiaSharp.NativeAssets.Win32`** loading in the published `win-x64` build. The server already ships
  it on the owner's PC, which is the evidence.
- **Wall-clock numbers on the owner's CPU and disk.** The § 3.4 table is this box; his will differ.
- **E's copy into an Avalonia bitmap** and what it costs. The format decision in § 1 makes it a single
  `memcpy`-class copy; whether Avalonia does anything slower is E's to measure.

---

## 8. Acceptance gate

Part C is done when, on this box, `dotnet test pc/tests/RankMaster2.Pc.Stills.Tests` is green with the
corpus present and:

1. **The bomb costs its display size.** `bomb_40mp.jpg` at a 960×1080 pane: `PeakBytes < 32 MB`. (Full
   decode would be 152 MB.)
2. **Sixteen orientations, two shapes, two sizes.** Every `exif_*` file lands at the same displayed size
   as its siblings and its pixels agree with orientation 1 within `6/255`.
3. **Colour is converted only when a profile says so.** `wide_gamut.jpg` differs from
   `wide_gamut_untagged.jpg` by `> 10/255`; the raw decode of the tagged file matches the untagged frame
   within `2/255`.
4. **Nothing broken takes anything down.** All of `broken/` and a video renamed `.jpg` give
   `Failed(NotAnImage)` (or a partial frame) with `LiveBytes == 0` afterwards, and the decoder works
   immediately after each.
5. **Memory is the wanted set.** After 2,000 `Show`/`Warm` cycles over 20,000 ids, `LiveBytes ≤ 6 frames`
   and `PeakBytes ≤ 8 frames`.
6. **Warm means instant.** A `Show` of a warmed pair raises `Ready` for both ids synchronously, in under
   5 ms, with no decoder call.
7. **Prefetch never delays the picture.** No warm decode starts while a visible decode is queued or
   running (the call-log test).
8. **Release means released.** `ReleaseAsync` returns only after any in-flight decode of that id has
   closed its stream, and afterwards the id has no frame, no job and no failure cached.
9. **The probe agrees with the catalog** on every eligibility rule and answers a 20,000-name folder in
   under 500 ms here.
10. **No forbidden dependency.** The production assembly's references are `RankMaster2.Core`, `SkiaSharp`
    and the BCL — checked by a test that reflects over
    `typeof(StillSource).Assembly.GetReferencedAssemblies()` and fails on `Avalonia*`, `RankMaster2.Catalog`,
    `RankMaster2.Server`, `RankMaster2.Ranking`, `System.Net.Http`.

Points 1, 3 and 5 are the ones that matter: the first is the reason decode-at-size exists, the second is
1.1.3 not regressing, and the third is the phone's two deaths not happening a third time.

---

## 9. For the integrator — decisions this plan made that touch a shared document

1. **§ 3.5, two visible decodes at once.** A reading of `SPEC.md` § Pipeline "one sequential reader" that
   keeps its purpose (prefetch never slows the current pair) and relaxes its letter for the two visible
   panes only. One constant reverts it.
2. **§ 3.4, the 720 px first paint is not built.** `SPEC.md` calls it optional; the measurement says it
   makes the real picture later. If the owner remembers it fondly, it is one extra job per id and this
   plan's queue can carry it — but the numbers should be shown to him first.
3. **§ 2.1, `Changed` is raised off the UI thread.** E must marshal. The alternative — this part taking a
   `SynchronizationContext` — would put an Avalonia concept into a part that must stay free of Avalonia.
4. **§ 2.1, leases.** E has one obligation: dispose the lease. It is the price of not copying every frame
   twice, and it is one `using`.
5. **§ 3.7, a partial decode is shown, not failed.** Matches the server. If E would rather treat
   `IsPartial` as a failure it can, from the flag, without a change here.
6. **§ 4, the test project is this part's own** (`pc/tests/RankMaster2.Pc.Stills.Tests/`), so it can
   reference `Catalog` for the agreement test without the production project doing so. If A prefers one
   test project for the client, these files move into a `Stills/` folder there unchanged.

---

## 10. Risks

| Risk | Mitigation |
|---|---|
| A real camera JPEG at 48 MP takes longer than the § 3.4 estimate and T2 misses 1.0 s | the visible pair decodes in parallel (§ 3.5); the timing test prints the number so the integrator sees it before the owner does; if still short, `TieredPGO`/R2R is A's lever and the only remaining one is the EXIF preview, which § 1.1 refuses for colour reasons |
| A LUT-based ICC profile (some Display P3 exports) is not parsed by Skia and renders as sRGB | the same limitation the server has; the test prints "profile not parsed"; the owner's library is what decides whether it matters, and `IMediaProbe`-style header scan of his real folder is a one-line diagnostic if he reports a colour problem |
| `GetScaledDimensions` on some format returns a size `GetPixels` then rejects | the `InvalidScale` retry at full size (§ 3.1 step 5), budget-checked first |
| E forgets to dispose a lease | frames leak at pane size, ~4–17 MB each, visible in `Budget.LiveBytes`; the `DEBUG` finalizer names the id; the acceptance test for E should include a `LiveBytes` check after 100 pairs |
| Two visible decodes of two 48 MP PNGs exceed 512 MB together | the second waits for the first (§ 3.3); neither fails |
| `FileShare.Delete` on the read handle lets the server move a file *during* a decode | Skia then returns `IncompleteInput` or an I/O error; the result is `IsPartial` or `Unreadable` for an id that the next snapshot no longer contains, and `Show` of the new snapshot evicts it. No state is touched |
| The integrator's frozen seam differs from § 2 | § 2 says: use the frozen file; report conflicts with § 3; do not fork |
