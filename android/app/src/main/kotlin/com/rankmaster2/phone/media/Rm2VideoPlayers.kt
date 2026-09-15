package com.rankmaster2.phone.media

import android.content.Context
import androidx.annotation.OptIn
import androidx.media3.common.MediaItem
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.common.util.UnstableApi
import androidx.media3.datasource.HttpDataSource
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
    private val dataSourceFactory = OkHttpDataSource.Factory(client)
        .setCacheControl(CacheControl.Builder().noStore().build())

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
        // The hard ceiling, and the number that actually matters: 6 MB a player, so two panes cost
        // about twelve between them rather than two hundred and seventy-five.
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

        val listener = object : Player.Listener {
            override fun onPlaybackStateChanged(state: Int) {
                when (state) {
                    Player.STATE_BUFFERING, Player.STATE_IDLE -> onState(MediaPaneState.Loading)
                    Player.STATE_READY -> onState(MediaPaneState.Loaded)
                    else -> Unit
                }
            }

            override fun onPlayerError(error: PlaybackException) = onState(stateOf(error))
        }
        player.addListener(listener)

        onState(MediaPaneState.Loading)
        player.setMediaItem(MediaItem.fromUri(url))
        player.prepare()
        return Rm2Video(player, listener)
    }

    internal companion object {

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
    /** For attaching to a `PlayerView`, and for nothing else. */
    val player: ExoPlayer,
    private val listener: Player.Listener,
) {

    var isReleased: Boolean = false
        private set

    /** Play. Called when the pane is on screen and settled. */
    fun start() {
        if (!isReleased) player.playWhenReady = true
    }

    /** Pause, keeping the buffer, for a pane that is still on screen but not the one in front. */
    fun stop() {
        if (!isReleased) player.playWhenReady = false
    }

    /**
     * Give back the decoder and the surface. After this the player is dead and [start] does
     * nothing. A pane that scrolls away calls this; a pane that forgets is the bug this class
     * exists to make obvious.
     */
    fun release() {
        if (isReleased) return
        isReleased = true
        player.removeListener(listener)
        player.release()
    }
}
