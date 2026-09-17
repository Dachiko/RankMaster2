package com.rankmaster2.phone.media

import android.content.Context
import androidx.annotation.OptIn
import androidx.media3.common.MediaItem
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.common.VideoSize
import androidx.compose.runtime.MutableState
import androidx.compose.runtime.mutableStateOf
import androidx.media3.common.util.UnstableApi
import androidx.media3.exoplayer.analytics.AnalyticsListener
import androidx.media3.datasource.HttpDataSource
import androidx.media3.datasource.cache.CacheDataSource
import androidx.media3.datasource.okhttp.OkHttpDataSource
import androidx.media3.exoplayer.DefaultLoadControl
import androidx.media3.exoplayer.DefaultRenderersFactory
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.LoadControl
import androidx.media3.exoplayer.source.DefaultMediaSourceFactory
import com.rankmaster2.phone.net.MediaRef
import okhttp3.CacheControl
import okhttp3.OkHttpClient

/**
 * Players for the video panes.
 *
 * `links.video` is the original file, served whole and Range-capable (SERVER_SPEC.md § 12.4), so
 * this is ordinary progressive HTTP playback: Media3's default extractors over an OkHttp data
 * source built on the app's one pinned, authenticated client.
 *
 * ## What these players deliberately do not have
 *
 * **Controls.** On the ranking screen a tap is a vote. A transport bar that swallows it, or a
 * play/pause button sitting where the user is about to tap, is a wrong vote - and a wrong vote is
 * permanent in the ratings. Controls belong to a full-screen view, which is somebody else's file.
 *
 * So: muted, looping, no UI. The pane is a moving picture to judge, not a video to watch.
 *
 * ## Releasing is not optional
 *
 * Two panes means two players, each holding a hardware decoder and a surface. A player left behind
 * by a pane that scrolled away is not a leak that shows up later; it is the second video failing to
 * start, or an OutOfMemory. [Rm2Video.release] must run when the pane leaves the screen; MediaPane
 * does it from a RememberObserver, which catches the abandoned-composition case too.
 */
@OptIn(UnstableApi::class)
class Rm2VideoPlayers(
    private val context: Context,
    client: OkHttpClient,
    private val baseUrl: String,
) {

    /**
     * The same client as everything else, with one instruction added: do not put this in the disk
     * cache.
     *
     * OkHttp would happily store a 200-response video under the server's `immutable` header, and a
     * handful of them would evict every photograph from a shared 512 MB cache. Video streams over
     * Range and is watched once in passing; photographs are fetched repeatedly across a session.
     * `no-store` keeps the cache for the ones that benefit from it.
     */
    private val upstream = OkHttpDataSource.Factory(client)
        .setCacheControl(CacheControl.Builder().noStore().build())

    /**
     * The player reads from disk, and only reaches the network for bytes it has never seen.
     *
     * `no-store` above keeps video out of the *photographs'* HTTP cache; this puts it in one of its
     * own (see [Rm2VideoCache]). The difference that matters is the loop: a clip that repeats for
     * as long as a pair is on screen used to be re-downloaded in full every few seconds.
     *
     * `FLAG_IGNORE_CACHE_ON_ERROR` so a cache that cannot be written - full disk, evicted
     * mid-read - degrades to plain streaming instead of failing the pane.
     */
    private val dataSourceFactory = CacheDataSource.Factory()
        .setCache(Rm2VideoCache.get(context))
        .setUpstreamDataSourceFactory(upstream)
        .setFlags(CacheDataSource.FLAG_IGNORE_CACHE_ON_ERROR)

    /**
     * How much of a file a player may hoard, for a file that is one Wi-Fi hop away.
     *
     * This exists because the defaults are written for a phone on mobile data and they killed this
     * app twice. Media3 allows a muxed stream **137 MB** of source buffer and reads 50 seconds
     * ahead; two panes is 275 MB of allowance against a 256 MB heap, before a single photograph.
     *
     * That allowance is only theoretical until something fills it - and a 4K AV1 clip on a phone
     * with no hardware AV1 decoder fills it perfectly. The network delivers far faster than a
     * software decoder consumes, so the buffer grows to its ceiling and stays there. Both crashes
     * were that: one thrown by the player, one by the HTTP reader, the same wall from either side.
     *
     * Seconds and megabytes instead. The bytes are on a PC in the same room, served Range-capable:
     * there is no outage to ride out, and a rebuffer costs a round trip on a LAN.
     */
    private fun lanLoadControl(): LoadControl = DefaultLoadControl.Builder()
        .setBufferDurationsMs(
            /* minBufferMs = */ MIN_BUFFER_MS,
            /* maxBufferMs = */ MAX_BUFFER_MS,
            /* bufferForPlaybackMs = */ 500,
            /* bufferForPlaybackAfterRebufferMs = */ 1_000,
        )
        // The hard ceiling, and the number that actually matters: 32 MB a player, so two panes
        // cost sixty-four between them rather than two hundred and seventy-five.
        .setTargetBufferBytes(TARGET_BUFFER_BYTES)
        // Size wins over duration: with a 4K file, 8 seconds is far more than 6 MB, and it is the
        // megabytes that have to be obeyed.
        .setPrioritizeTimeOverSizeThresholds(false)
        .build()

    /** The URL this ref would play, or null if it is not a video (or has already gone). */
    fun videoUrl(ref: MediaRef): String? {
        if (ref.isMissing) return null
        val link = ref.links.video ?: return null
        return MediaUrls.resolve(baseUrl, link)
    }

    /**
     * A player prepared on [ref], muted, looping and paused. Null if [ref] is not a playable video.
     *
     * [onState] is called with [MediaPaneState.Loading], [MediaPaneState.Loaded] and whatever the
     * failure turns out to be - including [MediaPaneState.Undecodable] for a file this phone's
     * decoders will not take, which is the honest answer for an exotic container and is the signal
     * that matters when deciding whether Media3 is enough for this library.
     */
    fun create(ref: MediaRef, onState: (MediaPaneState) -> Unit = {}): Rm2Video? {
        val url = videoUrl(ref) ?: return null
        val player = ExoPlayer.Builder(
            context,
            DefaultRenderersFactory(context)
                // If the first decoder that claims the format then fails to initialise, try the
                // next one instead of failing the pane. Phones ship several, of varying honesty.
                .setEnableDecoderFallback(true),
        )
            .setMediaSourceFactory(DefaultMediaSourceFactory(dataSourceFactory))
            .setLoadControl(lanLoadControl())
            .build()

        player.volume = 0f            // A ranking screen that makes noise is a ranking screen
        player.repeatMode = Player.REPEAT_MODE_ONE   // nobody uses twice.
        player.playWhenReady = false

        onState(MediaPaneState.Loading)
        // The listener belongs to the Rm2Video, which is what both of the things it reports -
        // the pane's state and the video's shape - are read off. Registering it here and handing
        // it over separately is how one of the two used to be forgotten on release.
        val video = Rm2Video(player, onState)

        // § the owner's ask, 2026-09-15: instead of a probe screen, the video that cannot keep up
        // says so on itself. Media3 counts frames the renderer had to throw away because they were
        // already late; sustained drops are precisely "this phone cannot decode this file in real
        // time", which is the only decoder question worth answering.
        player.addAnalyticsListener(object : AnalyticsListener {
            override fun onDroppedVideoFrames(
                eventTime: AnalyticsListener.EventTime,
                droppedFrames: Int,
                elapsedMs: Long,
            ) {
                video.reportDroppedFrames(droppedFrames, elapsedMs)
            }
        })

        player.setMediaItem(MediaItem.fromUri(url))
        player.prepare()
        return video
    }

    /**
     * One pane's video, released before its replacement is ever built.
     *
     * A pane keeps one `Slot` for as long as it is on screen and calls [acquire] every time the
     * pair changes. This is the fix for A12: Compose's own remembered-object cleanup composes the
     * replacement first and forgets the old value afterwards, so a `remember` keyed on the pair
     * alone had a moment - two of them, at every pair change - where the outgoing and incoming
     * player were both alive, decoder and surface each. `acquire` runs the two steps itself, in the
     * only safe order: release what this slot already holds, *then* build and `prepare()` the next
     * player. There is never a moment with two.
     */
    inner class Slot internal constructor(
        private val build: (MediaRef, (MediaPaneState) -> Unit) -> Rm2Video? =
            { ref, onState -> create(ref, onState) },
    ) {
        private var current: Rm2Video? = null
        private var currentRefId: String? = null

        // The shape this slot last had reported for whichever file it most recently held, kept
        // across a release so the *next* player for the *same* file does not have to rediscover it.
        //
        // This is the owner's second repro on this bug (BUGS.md § 1): full-screen a video, swipe the
        // pair a few times, press back. § 2.5 releases a covered pane's player outright, so the pane
        // that resumes gets a brand-new `Rm2Video` - and a fresh one starts at `aspectRatio == null`
        // (its own doc comment), even though the decoder already told this exact slot the shape a
        // moment earlier, before the viewer covered it. Until the new player's own
        // `onVideoSizeChanged` arrives, the pane fills its box - `RESIZE_MODE_FIT` keeps that from
        // ever being stretched, but it is still the wrong shape for however long that takes, right
        // after the owner was looking at the correct one full screen. Seeding the next player from
        // what this slot already knows about *that file* closes the gap; tagging it with the file's
        // id keeps it from ever being handed to a different file that happens to land in this slot
        // next (an ordinary pair change, not this bug, but the same field would be wrong for it).
        private var lastKnownRatioRefId: String? = null
        private var lastKnownRatio: Float? = null

        /** Releases whatever this slot holds, then builds and prepares [ref]'s player. */
        fun acquire(ref: MediaRef, onState: (MediaPaneState) -> Unit = {}): Rm2Video? {
            release()
            val next = build(ref, onState)
            if (next != null && ref.id == lastKnownRatioRefId) {
                next.aspectRatio.value = lastKnownRatio
            }
            current = next
            currentRefId = ref.id
            return current
        }

        /** Gives back this slot's player, if it has one, without taking a new one. */
        fun release() {
            current?.let { video ->
                val ratio = video.aspectRatio.value
                if (ratio != null) {
                    lastKnownRatioRefId = currentRefId
                    lastKnownRatio = ratio
                }
                video.release()
            }
            current = null
            currentRefId = null
        }

        /**
         * What a pane's slot should hold *right now*, given whether the pane is actually playing.
         *
         * § 2.5 (the second audit): a pane that stopped playing - because the full-screen viewer
         * opened over it, or the app went to the background - used to keep its player anyway,
         * paused rather than released, on the theory that "not in front means black, not gone".
         * That is fine for one covered pane; it stops being fine the moment something else needs a
         * decoder for the *same* file at the same time, which is exactly what the full-screen
         * viewer does - it builds its own player for whichever pane it is showing, on top of the
         * two the ranking screen never let go of. Four players at 32 MB each, against a heap sized
         * for two, is the number `MEMORY_PROPOSALS.md` already names as having killed the app
         * twice.
         *
         * So: a pane holds a player only while it is playing. Not playing releases it outright -
         * [acquire] runs again, from a cold decoder, the next time this pane's `playing` turns
         * true. That costs a restart of one file's playback on the rare transitions that flip it
         * (opening or closing the viewer, backgrounding), never per vote - voting does not touch
         * `playing`. Cheap resume was traded for a ceiling that actually holds.
         */
        fun sync(ref: MediaRef, playing: Boolean, onState: (MediaPaneState) -> Unit = {}): Rm2Video? =
            if (playing) acquire(ref, onState) else { release(); null }
    }

    /** A fresh, empty slot - one per pane, kept for as long as the pane is on screen. */
    fun newSlot(): Slot = Slot()

    internal companion object {

        /**
         * An [Rm2Video] with no player behind it, for testing the dropped-frame rule alone. The
         * rule is arithmetic and deserves a test; a real ExoPlayer on a build machine does not.
         */
        internal fun testVideo(): Rm2Video = Rm2Video(maybePlayer = null)

        /** Frames a second the renderer may throw away before a pane admits it is struggling. */
        const val STRUGGLING_FRAMES_PER_SECOND = 5.0

        /** How long it must run clean before the mark comes off again. */
        const val RECOVERED_AFTER_MS = 4_000L

        /**
         * The ceiling that matters, and the number both crashes came down to.
         *
         * 32 MB a player, 64 MB across two panes. Not the 6 MB a LAN strictly needs and not the
         * 137 MB Media3 offers: the owner asked for room to spare, and with `largeHeap` the app has
         * about 512 MB to spend rather than 256. A gigabyte was asked for and cannot exist - the
         * buffer is inside the heap, so a gigabyte is four times everything the app is allowed.
         */
        const val TARGET_BUFFER_BYTES = 32 * 1024 * 1024

        const val MIN_BUFFER_MS = 4_000
        const val MAX_BUFFER_MS = 20_000


        /**
         * The shape a video pane must be, from what the decoder reported. Null until it has.
         *
         * `onVideoSizeChanged` gives three numbers, not two: [pixelWidthHeightRatio] is 1 for
         * almost every file and is **not** 1 for an anamorphic one, where the stored pixels are
         * not square and the picture is wider than `width / height` says. Multiplying it in is the
         * difference between this fix and a subtler version of the bug it replaces.
         *
         * Null for anything that cannot describe a shape - a zero dimension, an audio-only track,
         * the state before the first frame is decoded - so the caller can leave the pane filling
         * its box until the truth arrives rather than guessing at 16:9.
         */
        internal fun aspectRatioOf(width: Int, height: Int, pixelWidthHeightRatio: Float): Float? {
            if (width <= 0 || height <= 0) return null
            if (!pixelWidthHeightRatio.isFinite() || pixelWidthHeightRatio <= 0f) return null
            val ratio = width * pixelWidthHeightRatio / height
            return if (ratio.isFinite() && ratio > 0f) ratio else null
        }

        /**
         * A playback failure, in the terms a pane can act on.
         *
         * The HTTP cases go through the same § 5.5 reading as a still: a `404` on a media URL means
         * the id is gone (§ 11.3), never "retry the URL". The decoder cases are the ones that only
         * a real phone can produce, and they are reported as [MediaPaneState.Undecodable] rather
         * than hidden behind a generic failure, because "this device cannot play this file" is
         * exactly the evidence the libVLC decision needs.
         */
        internal fun stateOf(error: PlaybackException): MediaPaneState {
            val http = error.findHttpCause()
            if (http != null) {
                return MediaFailures.stateOf(http.responseCode, null, error.errorCodeName)
            }
            return when (error.errorCode) {
                PlaybackException.ERROR_CODE_IO_FILE_NOT_FOUND -> MediaPaneState.Gone

                PlaybackException.ERROR_CODE_PARSING_CONTAINER_MALFORMED,
                PlaybackException.ERROR_CODE_PARSING_CONTAINER_UNSUPPORTED,
                PlaybackException.ERROR_CODE_DECODER_INIT_FAILED,
                PlaybackException.ERROR_CODE_DECODER_QUERY_FAILED,
                PlaybackException.ERROR_CODE_DECODING_FAILED,
                PlaybackException.ERROR_CODE_DECODING_FORMAT_UNSUPPORTED,
                PlaybackException.ERROR_CODE_DECODING_FORMAT_EXCEEDS_CAPABILITIES,
                -> MediaPaneState.Undecodable

                else -> MediaPaneState.Unavailable(
                    error.errorCodeName,
                    error.message ?: "The video did not play.",
                )
            }
        }

        fun PlaybackException.findHttpCause(): HttpDataSource.InvalidResponseCodeException? {
            var cause: Throwable? = this
            var hops = 0
            while (cause != null && hops++ < 8) {
                if (cause is HttpDataSource.InvalidResponseCodeException) return cause
                cause = cause.cause
            }
            return null
        }
    }
}

/**
 * One prepared player, and the three things a pane is allowed to do with it.
 *
 * Nothing here seeks, scrubs or changes the volume: that would be a control, and controls are the
 * one thing a ranking pane must not have.
 */
@OptIn(UnstableApi::class)
class Rm2Video internal constructor(
    /** For attaching to a `PlayerView`, and for nothing else. Null only in a test of the rule. */
    private val maybePlayer: ExoPlayer?,
    private val onState: (MediaPaneState) -> Unit = {},
) {

    val player: ExoPlayer get() = requireNotNull(maybePlayer) { "this Rm2Video has no player" }

    var isReleased: Boolean = false
        private set

    /**
     * The shape of the picture, once the decoder knows it, and null until then.
     *
     * This exists because the pane used to take its shape from the `PlayerView` inside it. The
     * video's size arrives after the first frame is decoded, which is long after Compose measured
     * the pane, and nothing told Compose to look again - so the picture kept whatever box it had
     * been given and drew stretched until a rotation forced a fresh layout pass. That is the whole
     * of "rotating twice fixes it".
     *
     * Reported as Compose state instead, so the *Compose* layout owns the shape: a new value is a
     * recomposition and a re-measure, which is the one thing the old arrangement could not produce.
     */
    val aspectRatio: MutableState<Float?> = mutableStateOf(null)

    private val listener = object : Player.Listener {
        override fun onPlaybackStateChanged(state: Int) {
            when (state) {
                Player.STATE_BUFFERING, Player.STATE_IDLE -> onState(MediaPaneState.Loading)
                Player.STATE_READY -> onState(MediaPaneState.Loaded)
                else -> Unit
            }
        }

        override fun onPlayerError(error: PlaybackException) = onState(Rm2VideoPlayers.stateOf(error))

        override fun onVideoSizeChanged(size: VideoSize) {
            aspectRatio.value = Rm2VideoPlayers.aspectRatioOf(
                width = size.width,
                height = size.height,
                pixelWidthHeightRatio = size.pixelWidthHeightRatio,
            )
        }
    }

    init {
        maybePlayer?.addListener(listener)
    }

    /**
     * True while this file is beyond this phone in real time — the mark a pane draws on itself.
     *
     * Asked for instead of a probe screen, and it is the better instrument: it reports on the
     * owner's own files, during real use, at the moment it matters. If the dot never appears, the
     * decoder question is answered. If it appears on 4K AV1 and nothing else, that is the answer
     * too, and it arrives without anyone running a test.
     */
    val struggling: MutableState<Boolean> = mutableStateOf(false)

    private var struggledAt = 0L

    /**
     * Dropped frames, turned into that one boolean.
     *
     * A frame is dropped when the renderer finds it already late, so a steady trickle is a decoder
     * that cannot hold real time. One late frame after a start is not — hence a rate over the
     * window Media3 reports, and a clean recovery when it stops.
     */
    internal fun reportDroppedFrames(dropped: Int, elapsedMs: Long) {
        if (elapsedMs <= 0) return

        val perSecond = dropped * 1000.0 / elapsedMs
        val now = System.currentTimeMillis()

        if (perSecond >= Rm2VideoPlayers.STRUGGLING_FRAMES_PER_SECOND) {
            struggledAt = now
            struggling.value = true
        } else if (struggling.value && now - struggledAt > Rm2VideoPlayers.RECOVERED_AFTER_MS) {
            struggling.value = false
        }
    }

    /**
     * Whether this plays, applied to the player already prepared - never by tearing it down and
     * building another. The pane calls this from a `LaunchedEffect` keyed on `playing`, so a vote
     * that stops the panes and a resume that starts them again cost one flag each, not a decoder.
     */
    fun setPlaying(playing: Boolean) {
        if (!isReleased) maybePlayer?.playWhenReady = playing
    }

    /**
     * Give back the decoder and the surface. After this the player is dead and [setPlaying] does
     * nothing. A pane that scrolls away calls this; a pane that forgets is the bug this class
     * exists to make obvious.
     */
    fun release() {
        if (isReleased) return
        isReleased = true
        maybePlayer?.removeListener(listener)
        maybePlayer?.release()
    }
}
