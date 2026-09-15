# Video memory on the phone — proposals, not changes

Two 4K AV1 clips, side by side, on a phone with no hardware AV1 decoder. The app has died twice
with `OutOfMemoryError`. This document is the options, ranked, with the arithmetic behind each and
a plain statement of what is measured, what is inferred and what is a guess.

**Nothing here has been implemented.** No code was changed, no branch made. Every number that could
be checked against a jar on this machine was; every number that could not is marked.

Already done and deliberately not re-proposed: the load-control cap
(`Rm2VideoPlayers.TARGET_BUFFER_BYTES` = 32 MB, 4–20 s, size over duration), `largeHeap`, the Coil
memory cache at 15%, releasing a covered pane rather than pausing it, and the audit that found no
ordinary player leak.

---

## 1. Where the memory actually goes

### 1.1 The two ledgers are not the same ledger

Android has kept bitmap pixels in **native** memory since API 26, and a `MediaCodec`'s decoded
frames were never on the Java heap at all. The 256 MB ceiling that killed this app bounds only the
Java heap. So the app has two budgets, and almost everything that *sounds* expensive is on the
wrong one for these crashes:

| What | Where it lives | Counted against the 256 MB? |
|---|---|---|
| ExoPlayer source buffers (`DefaultAllocator`, 64 KB `byte[]` each) | **Java heap** | **yes** |
| OkHttp / okio buffers (8 KB `Segment` each) | **Java heap** | **yes** |
| Coil's decoded bitmaps | native (API 26+) | no — but see § 1.5 |
| AV1 decoded frames and reference frames | native, and on this device in **another process** | no |
| The `PlayerView` surface's graphic buffers | graphics / dmabuf | no |

Both crashes are Java-heap crashes. Therefore **neither crash was caused by the decoder's frame
buffers**, and no amount of reasoning about 4K frame sizes explains them. That matters, because the
obvious story ("4K video, of course it ran out of memory") points at the wrong half of the problem.

### 1.2 The Java-heap ledger, with the real constants

Verified by `javap` against the exact artifacts in this build's Gradle cache:

| Constant | Value | Source |
|---|---|---|
| `DefaultLoadControl.DEFAULT_MUXED_BUFFER_SIZE` | 144,310,272 B = **137.6 MiB** | media3-exoplayer 1.5.0 |
| `DefaultLoadControl.DEFAULT_VIDEO_BUFFER_SIZE` | 131,072,000 B = 125 MB | same |
| `C.DEFAULT_BUFFER_SEGMENT_SIZE` (allocation granule) | 65,536 B | media3-common 1.5.0 |
| `Http2Connection.OKHTTP_CLIENT_WINDOW_SIZE` | 16,777,216 B = **16 MiB** | okhttp 4.12.0 |
| `okio.Segment.SIZE` | **8,192 B** | okio-jvm 3.9.0 |

Two players at the Media3 default were allowed 275 MiB of heap against a 256 MB ceiling. That is
the number § 3.4.1 already records, and it is correct.

What § 3.4.1 does **not** record is the second line of the ledger. OkHttp raises both its
connection window and its per-stream initial window to 16 MiB. The HTTP/2 reader thread reads
frames off the socket and parks them in the stream's `readBuffer` — a chain of 8 KiB okio segments
— and only issues a `WINDOW_UPDATE` when the *consumer* reads. ExoPlayer's loader thread stops
consuming the moment the load control says "enough". The reader thread does not stop; it fills
until the window closes.

So the ceiling on that buffer is **16 MiB of 8 KiB segments — about 2,048 objects — per HTTP/2
connection**, entirely outside ExoPlayer's accounting. `targetBufferBytes` does not know it exists.

Why HTTP/2 at all: `SecurityEndpoints.ConfigureTransport` calls `kestrel.Listen(..., UseHttps(...))`
and never sets `HttpProtocols`, so Kestrel's default `Http1AndHttp2` applies and ALPN gives `h2`.
Nothing in `SERVER_SPEC.md` mentions an HTTP version, so this is an accident of defaults on both
sides, not a contract.

### 1.3 The two crashes, attributed

| Crash | What ran out | What had the memory |
|---|---|---|
| `ExoPlayerImpl.handlePlaybackInfo`, 16-byte allocation | Java heap | The `DefaultAllocator` pools. The failing allocation is 16 bytes because by then *everything* fails; the player thread is simply the busiest thread. |
| okio 8 KB `Segment` on the HTTP/2 reader thread | Java heap | Same pools, plus that reader's own 16 MiB window. The reader is the thread that asks for memory most often, so it is the one that reports the wall. |

They are one crash seen from two threads. The comment in `Rm2VideoPlayers` already says this and it
is right.

### 1.4 What the 32 MB cap fixes, and what it does not

**Fixes:** the largest single line. 2 × 137.6 MiB becomes 2 × 32 MB. That alone is very likely
enough to stop these two specific stack traces recurring.

**Does not fix, and is worth knowing:**

1. **Two pools, not one.** `ExoPlayer.Builder` is given a fresh `lanLoadControl()` per player, and
   each `DefaultLoadControl.Builder` that is not handed an allocator constructs its own
   `DefaultAllocator`. So the budget is 32 MB *each*, 64 MB together, in two pools that are trimmed
   independently. `DefaultLoadControl` in 1.5.0 is built for sharing — it keeps a
   `HashMap<PlayerId, PlayerLoadingState>` and `calculateTotalTargetBufferBytes()` **sums** the
   per-player targets onto one allocator (confirmed in the bytecode). Sharing one instance turns
   two pools into one with a single, enforced total.
2. **The 16 MiB of okio segments are untouched.** The cap is a ceiling on what ExoPlayer *keeps*;
   it is not a ceiling on what OkHttp has *already read*. Crash number two is still reachable.
3. **The clips are small enough that the cap is always hit.** A 10 s 4K AV1 clip at 20–40 Mbps is
   25–50 MB (estimate — nothing in this repo measures bitrate, and the server is forbidden to probe
   it). With a 32 MB target and a file of that size, the player will try to hold essentially the
   whole file, hit the ceiling, and stay there for as long as the pair is on screen. The cap is
   therefore not a "usually unused headroom"; it is the steady state.
4. **The loop re-downloads.** `REPEAT_MODE_ONE` on a progressive HTTP source seeks back to 0, and
   with a 32 MB window over a 40 MB file the head has been discarded — so each loop re-requests
   from the server. Every 5–15 s, per pane, forever, for as long as the owner looks at that pair.
   The HTTP/2 streams never go idle, which is exactly the condition crash two needs.
5. **32 MB was a choice, and it costs what it looks like.** Over a LAN with a Range-capable server
   in the same room, 6–8 MB is ample; the difference between 6 and 32 buys nothing but risk. The
   comment says the owner asked for headroom, which is a fair reason — but if § 2's file-backed
   proposal lands, the number stops mattering at all, and that is the better way to give him his
   headroom.

### 1.5 `largeHeap` made the image cache *bigger* — an actual regression

Coil 2.7's `calculateMemoryCacheSize` (decompiled here, `coil.util.-Utils`) does this: read
`ApplicationInfo.flags & FLAG_LARGE_HEAP` (0x100000), and if set use
`ActivityManager.getLargeMemoryClass()`, otherwise `getMemoryClass()`. Then multiply by the percent.

So, with the crash report's 268,435,456 as the small heap and the usual doubling for large:

| | basis | percent | cache |
|---|---|---|---|
| Before both changes | 256 MB | 25% | **64 MB** |
| After both changes | 512 MB (largeHeap) | 15% | **76.8 MB** |

The two edits fight each other and the image cache came out 20% **larger**. It is native memory
either way, so it did not cause the crashes — but it is not the reduction the comment in
`Rm2ImageLoader` claims, and a percentage of a heap that another edit just doubled is a number that
will keep drifting. **Set it as an absolute byte figure** (`MemoryCache.Builder.maxSizeBytes`), so
it means one thing regardless of what the manifest says.

The exact large-heap figure is a **guess**: 512 MB is typical, the device could report something
else. `ActivityManager.getLargeMemoryClass()` settles it in one line — see § 5.

### 1.6 One more unbounded `byte[]` on the still path

`FullyReadBodyInterceptor.MAX_BUFFERED` is 32 MB, and it buffers whole response bodies into a heap
`byte[]` with no limit on how many are in flight. Two panes plus a prefetch is three, so the
theoretical worst case is 96 MB of heap for photographs. In practice a 2160 px WebP is 0.5–3 MB and
this has never fired — but it is precisely the shape of thing that turns a tight moment into a
crash. 8 MB is well above any still this server can produce.

### 1.7 The native ledger, for completeness

The decoder's memory is not what died, but the question asks for the numbers and they decide § 2.

- 3840 × 2160 = 8,294,400 pixels.
- 8-bit 4:2:0: **11.9 MiB per frame**. 10-bit (AV1 Main 10, common in AV1 libraries): **23.7 MiB**.
- AV1's bitstream mandates **8 reference frame slots** (`NUM_REF_FRAMES = 8`), all of which the
  decoder must hold.
- A frame-parallel software decoder (dav1d, libgav1) holds those 8, plus one in-flight frame per
  frame thread, plus an output queue. With 4 frame threads that is roughly **8 + 4 + 4 ≈ 16 frames**.
- 16 × 11.9 MiB ≈ **190 MB per decoder** at 8-bit; ≈ 380 MB at 10-bit.
- Plus the output `BufferQueue` to the `PlayerView`'s surface: typically 8–12 gralloc buffers,
  another **95–145 MB** of graphics memory, and *this* part is charged to the app's graphics PSS.

Two panes: **roughly 400–800 MB of native and graphics memory**, depending on bit depth. The phone
has 8 or 16 GB, so this does not by itself kill anything — but on this device the software codec
runs in the separate `media.swcodec` process, so if it does go wrong it arrives as a
`MediaCodec` error (`ERROR_CODE_DECODING_FAILED`) or a low-memory kill of that process, **never**
as an `OutOfMemoryError` in this app. If the owner ever reports "the video just stopped" rather
than "the app closed", that is this, and it is a different bug with a different fix.

The 16-frame figure is an **estimate** from how dav1d and libgav1 size their buffer pools; the exact
count is the decoder's business and is not queryable. What *is* queryable is whether the decoder
will accept the format at all — see § 5.

---

## 2. Are two simultaneous 4K AV1 decoders viable on this SoC?

### 2.1 The honest answer: probably, but only just, and not measured

Snapdragon 8+ Gen 1 has no AV1 hardware decoder — the task states it, and it matches the Adreno 730
generation. So every AV1 frame goes through the CPU.

Android 14 ships two software AV1 decoders: `c2.android.av1.decoder` (libgav1) and, since 14,
`c2.android.av1-dav1d.decoder`. dav1d is several times faster than libgav1 on ARM. **Which of the
two this phone picks by default is not known and is the single cheapest thing to find out** (§ 5).

Rough judgement, and it is a judgement: dav1d on a Cortex-X2 at ~3 GHz with 4–6 threads can hold
4K30 8-bit. **One** clip, alone, should play. **Two** at once split the same big cores, and the two
decoders are also competing with Compose, the loader threads and the TLS reader. My expectation is
that two 4K AV1 streams will run at somewhere between half and three-quarters of real time, which
shows up as stutter and dropped frames rather than failure — and the owner has said playback
performance is fine, which is *evidence against this worry* and is the most interesting fact in the
whole brief.

**So: do not design around an assumption the owner's own experience contradicts.** Measure first
(§ 5.2 gives the two numbers that settle it), and only then choose between § 2.2's options.

If it turns out the owner's "fine" was on 1080p files and the 4K ones are the ones that crash, that
changes everything — and nothing in the app currently knows a clip's resolution, because the server
is forbidden to say and the client only learns it after the decoder starts.

### 2.2 If two at once is not viable — what to do instead

The product is *comparing two things*. Judged against that:

| Option | What it costs the comparison | Verdict |
|---|---|---|
| **A. Both play, always** (today) | nothing | The goal. Keep if it measures clean. |
| **B. Alternate: left plays one loop, then right plays one loop**, the idle pane showing its last frame | The owner sees both moving, never simultaneously. On a half-a-phone-screen pane the eye saccades between panes anyway — it cannot actually watch two 4K clips at once. Costs a second or two of waiting. | **Best fallback.** Never two decoders; both sides still move; no gesture to learn. |
| **C. Only the last-touched pane plays** | Requires a deliberate act to see the other move, and a tap is a vote — so the "which one" gesture has to be something else (hover is not available; long press is the menu). Gesture budget is already full (§ 3.6). | Rejected: it spends the app's scarcest resource, an unused gesture. |
| **D. Still frame both panes, playback only in full screen** | Full screen already exists and already plays one at a time, so this is nearly free. But a video folder ranked from two still frames is ranking the wrong thing when motion is what distinguishes the clips. | Acceptable only as a panic setting, not a default. |
| **E. Both play at reduced resolution** | Nothing — it is the ideal answer | **Impossible client-side.** There is no way to ask an AV1 decoder for a 1080p decode of a 4K stream; `KEY_MAX_WIDTH/HEIGHT` is about adaptive switching, and a smaller surface only scales at composite time after a full-size decode. This needs the server (§ 3). |
| **F. Play only the first 2–3 s of each, then hold** | Halves nothing — both decoders still start together. | Rejected: pays a product cost for no memory gain. |

**Option B in detail**, because it is the one I would build. The idle pane needs a frame to show.
That frame can be had for nothing: render the `PlayerView` with a **`TextureView`** surface type and
call `textureView.getBitmap(paneWidth, paneHeight)` at the moment the player is released. That is a
poster frame, taken from the decode that already happened, at pane resolution (~1080 × 1200, about
5 MB ARGB in native memory), with no second decode, no second request and no server change.

Its costs, stated plainly: `TextureView` is measurably heavier than `SurfaceView` for playback
(an extra copy per frame through the GPU), it cannot be read back if the surface has already been
torn down, and the very first appearance of a pane has no frame yet — it must show black until the
first frame lands, which it will within a fraction of a second on a LAN. If the readback proves
unreliable on this device, the fallback is a server poster frame (§ 3.2), and that is the strongest
argument for breaking a server rule anywhere in this document.

---

## 3. What the server could do

`SERVER_PLAN.md` § 7 is explicit: *Originals only. No FFmpeg, no poster frames, no video probing.
Server never decodes video.* `SERVER_SPEC.md` § 12.1 turns it into contract: "There are no poster
frames and no still for a video", a still request on a video is `409 wrong_media_kind`, and
`MediaMeta` **MUST NOT** contain `durationMs`, `codec`, `frameRate` or `bitrate`. `VideoStreamer`'s
class comment says the same in as many words. The decision removed roughly a third of the original
scope.

### 3.1 The one thing the server can do that breaks no rule

Nothing in `SERVER_SPEC.md` names an HTTP version. `kestrel.Listen(...UseHttps(...))` without
`HttpProtocols` gives `Http1AndHttp2`, so ALPN produces `h2` and the client gets 16 MiB receive
windows it never asked for. Pinning the listener to `HttpProtocols.Http1` would make the whole of
§ 1.2's second ledger line disappear — backpressure would become TCP's job, bounded by the socket
buffer rather than by a 16 MiB window.

It is a one-line change to `SecurityEndpoints.ConfigureTransport`, breaks no clause, and would need
the security and conformance suites re-run rather than rewritten. **But it is a server change made
for a client's benefit, and the client can get the same effect alone** (§ 4.2), so I would do it
client-side and leave the server untouched. Listing it because it is the only free server lever
there is.

### 3.2 Poster frames — the cheapest rule to break, if one must be

**What it would be:** `GET /media/{id}/still?w=` answering for a video with a single decoded frame
(say at 0.5 s, or the first keyframe), cached exactly like a still.

**What it costs:**

- `SERVER_PLAN.md` § 7's "no poster frames" and "server never decodes video" — both, directly.
- `SERVER_SPEC.md` § 12.1's "There are no poster frames and no still for a video", and the
  `409 wrong_media_kind` behaviour for `still` on a video, which is currently tested.
- A new `variant` for § 12.2's ETag (`p{w}j` / `p{w}w`). Mechanical.
- An FFmpeg dependency on a server that currently has none, on a Windows PC, which means shipping
  or locating a binary and dealing with its absence. This is the real cost, not the code.
- § 12.5's "two responses with the same ETag MUST be byte-identical, forever". A single-frame
  decode re-encoded to JPEG/WebP by the server's own encoder is reproducible **only if the FFmpeg
  build is pinned**. The variant string would have to encode a decoder generation, or the guarantee
  becomes a lie the first time the binary is updated.
- 554 tests, an audited server, and a `MediaEndpoints` shape that currently makes "a video has no
  still" an invariant rather than a branch.

**What it buys:** a reliable idle-pane image for option B, independent of `TextureView` readback,
available *before* the clip is ever played — which also means a video pane could appear instantly
instead of black. It would let `MediaPrefetcher` warm video pairs too, which it currently skips.

**My recommendation:** do not do it yet. Build option B on `TextureView` readback first. If the
readback is flaky on this device, come back and make this case properly — it is a good case, and
the fact that it needs no *ongoing* decoding (one frame, cached forever under a stable ETag) makes
it far more defensible than transcoding.

### 3.3 Transcoding to a 1080p hardware-decodable proxy — the only complete answer

**What it would be:** a `GET /media/{id}/proxy?h=1080` returning H.264 or HEVC at 1080p, produced
once per file and cached. On this phone those go straight to the hardware decoder; two of them at
once is unremarkable, the way it is for every other video app on the device.

**What it costs, beyond everything in § 3.2:**

- Encoding time. A 10 s 4K AV1 clip decoded and re-encoded on a desktop CPU is a matter of seconds
  to tens of seconds. It cannot be synchronous on first view without a visible stall. It would have
  to be warmed ahead — and the API already has exactly the right hook: `warmPairs` (§ 9.5) is the
  server telling the client what is coming next, so the server equally knows what to pre-encode.
  That is a genuinely elegant fit and it is the strongest thing about this proposal.
- Cache size. `MediaOptions.CacheMaxBytes` is 512 MB for stills; 1080p proxies of a video library
  are a different order and would need their own budget and their own eviction.
- The byte-identity guarantee, harder than in § 3.2: video encoders are not bit-reproducible across
  versions or even across thread counts. The variant would have to encode the encoder identity, or
  § 12.5.1 has to be weakened for this endpoint alone.
- It makes the server a media subsystem again — precisely the third of the scope § 7 deleted.

**What it buys:** the product as designed. Two clips, side by side, playing, smoothly, at any source
resolution, with the phone's decoders barely warm, and it retires the AV1 problem permanently
rather than working around it.

**My recommendation:** hold it in reserve, and gate it on one measurement. If § 5.2 shows that even
**one** 4K AV1 clip cannot sustain its frame rate alone on this phone, then no client-side
arrangement helps — option B would be two stuttering clips instead of one — and this becomes the
right answer rather than an expensive one. If one clip plays clean, option B is enough and this
should stay unbuilt.

### 3.4 Video probing — small, and not worth it alone

Knowing `width`, `height` and `codec` for a video would let the client apply expensive handling only
to 4K AV1 and leave a 1080p H.264 clip alone. That is real value. But `MediaMeta` **MUST NOT**
carry those fields (§ 12.1), and the client can learn the same thing from the decoder within a
fraction of a second of starting playback — `Player.Listener.onVideoSizeChanged` and the selected
`Format` give resolution and codec for free, client-side, no contract touched. Adapting *after* the
first frame is slightly late but entirely sufficient for "should this pane keep playing".

**Recommendation: refuse.** This one is genuinely unnecessary.

---

## 4. Client-side levers that need no server

Ranked by value per unit of risk.

### 4.1 Play from a local file, not from the network — the big one

Fetch each clip to disk **once**, then hand the player a local file.

Media3 has the pieces: a `SimpleCache` over its own directory with its own `LeastRecentlyUsedCacheEvictor`,
a `CacheDataSource.Factory` with the existing `OkHttpDataSource.Factory` upstream, and — for the
version where the player never touches the network at all — a `CacheWriter` that pulls the clip
down before the player is created.

What it fixes, in order of importance:

1. **Crash two stops being reachable for video.** A `FileDataSource` has no HTTP/2 reader thread and
   no 16 MiB window. During the initial download the consumer is a disk sink that never falls
   behind the network, so the reader is never outrun either.
2. **The loop becomes free.** No re-request every 5–15 s; the second and every subsequent loop is a
   local read. This also removes a continuous Wi-Fi load that is competing with the *other* pane.
3. **The buffer ceiling stops mattering.** From a local file, refilling is instant and 6 MB is as
   good as 32; the 32 MB can stay for the owner's peace of mind and cost nothing.
4. **Revisiting a pair is instant.** The ranking algorithm shows files more than once.

What it costs: disk. 5–15 s clips at 4K are 25–50 MB each (estimate); a 2 GB cache holds 40–80 of
them, which is a session's worth. That is a *separate* cache from the 512 MB OkHttp one, which is
the point — `Rm2VideoPlayers` currently sets `no-store` precisely so video does not evict
photographs, and giving video its own bounded store honours that reasoning instead of undoing it.

The one thing to be careful of: a `CacheWriter` pre-download adds latency before the first frame.
On a LAN a 40 MB file is under a second at a few hundred Mbit/s, but the pane must show something
during it, and that something is what § 2.2 option B already needs.

**Contract check:** none. The URLs are still `links.video` used verbatim, still Range-capable, still
`v=`-keyed and immutable per § 12.5, which is exactly what makes them safe to cache on disk forever.

### 4.2 HTTP/1.1 for the video path

`client.newBuilder().protocols(listOf(Protocol.HTTP_1_1)).build()` for the video data source only.
No reader thread, no receive window, backpressure via TCP. It opens a second connection to the same
host — connection pooling keys on the protocol list — which on a LAN is free.

With § 4.1 this is belt and braces, and I would still do it: the download in § 4.1 is the one moment
the window can still fill, and this closes it. Two lines.

### 4.3 One shared `DefaultLoadControl` and one shared `DefaultAllocator`

Build the load control once in `Rm2VideoPlayers` and hand the same instance to every player, with
`DefaultLoadControl.Builder().setAllocator(sharedAllocator)`.

- `calculateTotalTargetBufferBytes()` sums per-player targets onto that one allocator (verified in
  bytecode), so the *total* is enforced rather than N × 32 MB.
- One pool means bytes freed by one player are reused by the next instead of each holding its own
  high-water mark.
- Set 16 MB per player so the total stays 32 MB, not 64.

**The honest trade:** with a shared budget, `shouldContinueLoading` compares
`allocator.getTotalBytesAllocated()` against the *total*, so a player that fills the pool first can
stall the other's loading. With two panes at 16 MB each and § 4.1's local files, that is a non-issue;
without § 4.1 it could produce a pane that buffers slowly because its neighbour got there first.
Worth knowing before it is mistaken for a bug.

### 4.4 Video-only renderers

Every clip is muted and there are no subtitles. `DefaultRenderersFactory` still builds audio, text,
metadata and camera-motion renderers, each with a slot in the load control's sizing and, for audio,
a real codec instance and an `AudioTrack`.

Either supply a `RenderersFactory` that returns only the video renderer, or disable audio in the
track selector (`setTrackTypeDisabled(C.TRACK_TYPE_AUDIO, true)`). A few MB and one codec instance
per pane, at no risk, and it makes "this screen is silent" structural rather than a `volume = 0f`
that a future edit can undo.

### 4.5 Prefer dav1d over libgav1

`DefaultRenderersFactory.setMediaCodecSelector(...)` can reorder the decoders Media3 would try, so
that `c2.android.av1-dav1d.decoder` is preferred over `c2.android.av1.decoder` where both exist.
`setEnableDecoderFallback(true)` is already on, so a wrong guess degrades rather than fails.

This is a **speed** lever, not a memory one — but the memory problem is downstream of the speed
problem (the buffers fill because the decoder cannot keep up with the LAN), so it is on the causal
path. Whether it does anything depends entirely on which decoders this phone reports, which § 5.1
answers in one screen.

### 4.6 Cap the still-path body buffer

`FullyReadBodyInterceptor.MAX_BUFFERED`: 32 MB → 8 MB. See § 1.6.

### 4.7 The image cache as bytes, not a percentage

See § 1.5. `maxSizeBytes(48L * 1024 * 1024)` or whatever figure is chosen, so it stops tracking
`largeHeap`.

### 4.8 What `largeHeap` actually bought

It roughly doubles the Java-heap ceiling — the crash report's 268,435,456 becomes whatever
`getLargeMemoryClass()` says, typically 536,870,912. It does **nothing** for the decoder, nothing for
graphics memory, and nothing for bitmaps, because none of those are on the Java heap.

So it is real headroom for exactly the two things that crashed: ExoPlayer's `byte[]` pools and
okio's segments. § 3.4.1 says "largeHeap is not the fix", and that remains true — with the caps in
place it is now headroom over a bounded budget rather than a bigger room to fill, which is the only
form in which it is defensible. It also silently enlarged the image cache (§ 1.5), which is the kind
of side effect that argues for keeping it and making every other budget absolute.

`largeHeap` is also a hint, not a promise; a device under memory pressure can refuse it. Nothing
should be sized as a fraction of it.

### 4.9 Native-memory pressure — can it be measured?

Yes, from inside the app, without a profiler:

| Number | How |
|---|---|
| Java heap in use / ceiling | `Runtime.getRuntime().totalMemory()`, `freeMemory()`, `maxMemory()` |
| The heap ceiling the system granted | `ActivityManager.getMemoryClass()` / `getLargeMemoryClass()` |
| Native heap | `Debug.getNativeHeapAllocatedSize()` |
| **Graphics memory — where decoded frames land** | `ActivityManager.getProcessMemoryInfo(...)` then `Debug.MemoryInfo.getMemoryStat("summary.graphics")` |
| Java/native/code/stack split | the same `getMemoryStat` with `summary.java-heap`, `summary.native-heap`, … |
| Source buffers actually held | `((DefaultAllocator) loadControl.getAllocator()).getTotalBytesAllocated()` — both are public API |
| Pressure signals from the OS | `ComponentCallbacks2.onTrimMemory`, `ActivityManager.getMyMemoryState` |

`summary.graphics` is the one that answers "is the decoder the problem". None of it needs a cable.

### 4.10 What I would *not* do client-side

- **The in-process libgav1/dav1d extension renderer** (`media3-decoder-av1`). It would give explicit
  control of input and output buffer counts, which is attractive — but it moves several hundred
  megabytes of frame buffers *into this app's own address space* from the `media.swcodec` process
  where they safely live now. That is a memory regression sold as a memory fix. It also needs an
  NDK build of a library with, as far as I know, no published AAR — **that last point is a guess**;
  it can be settled by looking for `androidx.media3:media3-decoder-av1` on Maven Central.
- **Calling `System.gc()` or recycling bitmaps by hand.** Neither has fixed an Android OOM in a
  decade and both hide the real ceiling.
- **Raising `largeHeap` further or chasing the heap number.** There is nothing above `largeHeap`,
  and the gigabyte that was asked for cannot exist — the `Rm2VideoPlayers` comment is right about
  that and should stay.

---

## 5. How to know it is fixed

The phone is the owner's and there is no profiler. Everything below is either in-app or one
pairing code away.

### 5.1 A one-screen decoder probe — do this before anything else

The cheapest and most informative thing in this document. On a hidden diagnostics screen (long-press
the version line, or a corner of Connect), enumerate `MediaCodecList(REGULAR_CODECS)` and for every
AV1 decoder print:

- `name`, `isHardwareAccelerated()`, `isSoftwareOnly()`, `isVendor()`
- `getMaxSupportedInstances()`
- `VideoCapabilities.areSizeAndRateSupported(3840, 2160, 30)`
- `VideoCapabilities.getSupportedPerformancePoints()`
- `getSupportedWidths()` / `getSupportedHeights()`

Plus, on the same screen, `getMemoryClass()`, `getLargeMemoryClass()` and `Runtime.maxMemory()`.

The owner sends one screenshot, and the following stop being guesses: whether dav1d is present,
whether the phone claims 4K30 AV1 at all, and what `largeHeap` is actually worth here. § 1.5, § 2.1
and § 4.5 all turn on this.

### 5.2 The two numbers that decide § 2 and § 3.3

While a video pair is on screen, collect from an `AnalyticsListener`:

- `onDroppedVideoFrames(eventTime, droppedFrames, elapsedMs)` — per player.
- `DecoderCounters.renderedOutputBufferCount` — per player.

Dropped ÷ (dropped + rendered) is the frame-drop rate, and it is the whole answer:

| Measurement | Conclusion |
|---|---|
| Both panes < ~2% dropped | Two decoders are viable. Do § 4 and stop. |
| One pane alone < 2%, two panes much worse | Option B (§ 2.2). No server change. |
| One pane alone already bad | No client arrangement helps. § 3.3 (transcode) is the real answer. |

Run it once with both panes and once with `playing` forced to one side, on the same folder.

### 5.3 A high-water-mark sampler

A 1 Hz coroutine while the rank screen is up, keeping **peaks** of: Java heap used, `maxMemory`,
native heap, `summary.graphics`, allocator bytes per player, Coil `memoryCache.size`. Displayed on
the diagnostics screen and appended to a small ring-buffer file.

Peaks, not instantaneous values — an OOM is a peak, and a sampler that only shows "now" will never
catch it.

### 5.4 A crash handler that writes the ledger

`Thread.setDefaultUncaughtExceptionHandler`: on `OutOfMemoryError`, write the § 5.3 snapshot plus
the stack trace to a file the owner can send, next to the archived R8 mapping (§ 3.4.1 is already
right that the mapping is what made the last two traces readable).

Do **not** try to `Debug.dumpHprofData()` from inside an OOM — it needs memory. Offer it as a manual
button on the diagnostics screen instead; a zipped hprof from a healthy moment, taken while a video
pair is on screen, answers "what is holding the heap" definitively and is the closest thing to a
profiler that fits down a Telegram file upload.

### 5.5 The cable-free profiler that already exists

Android 11+ has wireless debugging. The owner enables it once, reads a pairing code, and then
`adb shell dumpsys meminfo com.rankmaster2.phone` gives the full PSS breakdown — Java heap, native
heap, graphics, everything in § 4.9 and more — with no app changes at all. If he is willing to do it
once, it is worth more than §§ 5.3–5.4 combined. Offer it; do not depend on it.

### 5.6 The soak test, as an acceptance gate

Rank a 4K AV1 folder for 200 pairs without leaving the app, then:

| Check | Pass |
|---|---|
| Crashes | none |
| Peak Java heap | < 60% of `maxMemory()` |
| Allocator bytes | never above the configured total |
| `summary.graphics` | flat across the run, not climbing |
| Dropped frames | per § 5.2 |
| Codec errors | none (a climbing graphics figure or a codec error is the § 1.7 failure mode, not the § 1.2 one) |

### 5.7 What can be tested off the phone

`LoadControlBudgetTest` is the right idea and should be extended to hold the *whole* ledger, not
just one line of it: assert that allocator total + HTTP window + image cache + still buffer, as the
app actually configures them, stays under the **small** (non-`largeHeap`) figure. Then raising any
one of them fails a test on this machine rather than the owner's phone in three weeks.

---

## 6. Ranked

### Do first — no server change, no product change, low risk

1. **§ 5.1, the decoder probe.** One screen, and it turns four guesses in this document into facts.
2. **§ 4.1, play from a local file.** The single biggest structural fix: it removes the HTTP/2
   reader from the video path, removes the per-loop re-download, and makes the buffer ceiling
   irrelevant. Everything else is tuning by comparison.
3. **§ 4.2, HTTP/1.1 for video.** Two lines; closes the remaining window.
4. **§ 4.3, one shared load control and allocator**, 16 MB each. Makes the budget a real total.
5. **§§ 4.4, 4.6, 4.7** — video-only renderers, the 8 MB body cap, the image cache in bytes. Small,
   safe, and they stop the ledger drifting.
6. **§§ 5.3–5.4, the sampler and the crash handler.** Without these the next crash is as blind as
   the last two were before the R8 mapping.

### If that is not enough

7. **§ 4.5**, prefer dav1d — only if § 5.1 says there is a choice to make.
8. **§ 2.2 option B**, alternating playback with a `TextureView`-captured idle frame — only if § 5.2
   says two decoders cannot hold their frame rate. It is a product change and should be made on
   evidence, not on caution.
9. **§ 3.2, server poster frames** — only if the `TextureView` readback proves unreliable on this
   device. The case is decent and the cost is mostly the FFmpeg dependency, not the code.

### If the measurement is bad enough

10. **§ 3.3, a 1080p proxy transcode.** The only thing that makes two 4K clips genuinely play side
    by side on a phone with no AV1 hardware. Expensive, breaks the § 7 decision squarely, and should
    be built only if § 5.2 shows a *single* clip cannot hold its frame rate — because at that point
    every cheaper option is rearranging a stutter.

### Refuse

- **§ 3.4, video probing** for resolution and codec. The decoder tells the client the same thing for
  free, and it would break § 12.1's explicit `MUST NOT` for nothing.
- **§ 4.10's in-process AV1 renderer** as a memory fix. It is the opposite of one.
- **`System.gc()`, manual bitmap recycling, a larger `largeHeap`.** None of these is a fix.
- **Any budget expressed as a percentage of the heap.** § 1.5 is what that costs.
- **A "video quality" setting for the owner to configure.** A control nobody uses is a control in
  the way (§ 3.6.1), and this one would be a control that asks him to debug the app.
- **Anything that moves ranking logic into the client.** Not at issue in any proposal here, and it
  stays that way.

---

## 7. Open questions, and the evidence that settles each

| Question | Settled by | Cost |
|---|---|---|
| Which AV1 decoders does this phone have, and does either claim 4K30? | § 5.1 probe, one screenshot | minutes |
| What is `getLargeMemoryClass()` here? Is the image cache 76.8 MB? | same screen | free |
| Can **one** 4K AV1 clip hold its frame rate? | § 5.2, `onDroppedVideoFrames` with one pane playing | an hour |
| Can **two**? | § 5.2 with both | same run |
| Were the crashing clips actually 4K, or were they 1080p and something else is wrong? | `onVideoSizeChanged`, logged per pane | free, and important — the owner says playback is fine, which does not fit the 4K story |
| What bitrate and file size is a typical clip? | `MediaRef.sizeBytes` is already in the snapshot; log it per video pane | free — and it sizes § 4.1's disk cache |
| Is `TextureView.getBitmap` reliable at release time on this device? | try it; if it returns null or a black frame, § 3.2 | a day |
| Does `media3-decoder-av1` publish an AAR? | look on Maven Central | minutes |
| Is the graphics figure climbing across a session? | § 5.3 sampler, or one `dumpsys meminfo` | free |

The single most valuable measurement is § 5.2 with one pane versus two. It decides between "tune the
client" (§ 4), "change the interaction" (§ 2.2 B) and "change the server" (§ 3.3), and until it
exists every one of those is an argument rather than a plan.

---

## 8. A note on § 3.4.1

`CLIENT_PLAN.md` § 3.4.1 as it stands describes **one** crash — the full-screen one, with the third
player — and its remedy. The second crash, the okio segment on the HTTP/2 reader thread, is recorded
only in a comment in `Rm2VideoPlayers` ("one thrown by the player, one by the HTTP reader, the same
wall from either side"). The verbatim reports the brief refers to are not in the file.

If they exist, they should go into § 3.4.1, because the second one is the one that points at the
16 MiB receive window — the whole of § 1.2 — and that half of the problem is currently written down
nowhere except as a single clause in a Kotlin comment.
