package com.rankmaster2.phone.media

import androidx.annotation.OptIn
import androidx.compose.runtime.MutableIntState
import androidx.compose.runtime.mutableIntStateOf
import androidx.media3.common.C
import androidx.media3.common.Format
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.common.Tracks
import androidx.media3.common.util.UnstableApi
import java.util.Locale

/**
 * What one video's path, from URL to pixels, has been seen to do. Owned by an [Rm2Video], filled in
 * by that video's player listener and by its surface, and read by the review screen's debug overlay.
 *
 * Why it exists: on the owner's phone a review video downloads completely and never shows a
 * picture, and there is no device here to look at. So the path records itself, always on and
 * cheaply (a counter or a short string per event), and the owner copies the text out.
 *
 * Everything discrete bumps [revision], a Compose state, so the overlay updates live; the numbers
 * that only make sense *now* (position, buffered, the surface's size) are sampled when the report
 * is read, through [sampler] and [surfaceSampler].
 *
 * Main thread only, like the player it describes. Nothing here changes what the video does.
 */
@OptIn(UnstableApi::class)
class VideoDiagnostics(
    val url: String?,
    private val clock: () -> Long = { System.nanoTime() / 1_000_000L },
) {

    /** Bumped on every recorded event; reading it in composition is what makes the overlay live. */
    val revision: MutableIntState = mutableIntStateOf(0)

    private val createdAt = clock()

    // A plain counter, written to [revision] without ever reading it: `showOn` runs inside
    // AndroidView.update, which re-runs on any state it reads, and a read-modify-write of a state
    // there would loop forever.
    private var revisionCounter = 0
    private val events = EventRing(EVENT_CAPACITY)

    /** Reads the live player and pane at report time. Set by [Rm2Video]. */
    var sampler: (() -> PlayerSample)? = null

    /** Reads the attached SurfaceView at report time. Set by [Rm2Video]. */
    var surfaceSampler: (() -> SurfaceSample?)? = null

    private var prepareAt: Long? = null
    private var firstFrameAt: Long? = null
    private var lastVideoSize: String = "none yet"
    private var lastFormat: String = "none yet"
    private var decoder: String = "none yet"
    private var codecError: String = "none"
    private var videoDisabledCount = 0
    private var lastError: String = "none"
    private var droppedFrames = 0L
    private var droppedEvents = 0
    private var surfaceSizeSeenByPlayer: String = "none yet"
    private var loadError: String = "none"

    private var showOnCalls = 0
    private var hideFromCalls = 0
    private var surfaceCreatedCount = 0
    private var surfaceChangedCount = 0
    private var surfaceDestroyedCount = 0
    private var lastSurfaceChanged: String = "never"

    private var pane: String = "none yet"

    /** The attached video box's place in the window, kept as text; updated without a bump. */
    @Volatile
    var box: String = "not laid out yet"

    private var tracks: List<String> = emptyList()

    fun now(): Long = clock() - createdAt

    /** Records an event line, stamped relative to creation. */
    fun log(text: String) {
        events.add(now(), text)
        revision.intValue = ++revisionCounter
    }

    // -- the player's side -------------------------------------------------------------------------

    fun onPrepare() {
        prepareAt = clock()
        log("prepare()")
    }

    fun onPlaybackState(state: Int) = log("playbackState=${stateName(state)}")

    fun onPlayWhenReady(playWhenReady: Boolean) = log("playWhenReady=$playWhenReady")

    fun onIsPlaying(isPlaying: Boolean) = log("isPlaying=$isPlaying")

    fun onVideoSize(width: Int, height: Int, pixelWidthHeightRatio: Float, aspectRatio: Float?) {
        lastVideoSize = "${width}x$height par=$pixelWidthHeightRatio -> aspectRatio=${fmt(aspectRatio)}"
        log("videoSize $lastVideoSize")
    }

    fun onFirstFrame() {
        firstFrameAt = clock()
        log("renderedFirstFrame (${sincePrepare(firstFrameAt!!)} since prepare)")
    }

    fun onVideoFormat(format: Format) {
        lastFormat = describeFormat(format)
        log("videoInputFormat $lastFormat")
    }

    fun onDecoderInitialized(name: String, initMs: Long) {
        decoder = "$name (init ${initMs} ms)"
        log("decoderInitialized $decoder")
    }

    fun onDecoderReleased(name: String) = log("decoderReleased $name")

    fun onCodecError(error: Exception) {
        codecError = chain(error)
        log("videoCodecError $codecError")
    }

    fun onVideoDisabled() {
        videoDisabledCount++
        log("videoDisabled (x$videoDisabledCount)")
    }

    fun onVideoEnabled() = log("videoEnabled")

    fun onPlayerError(error: PlaybackException) {
        lastError = "${error.errorCodeName} (${error.errorCode}): ${chain(error)}"
        log("PLAYER ERROR $lastError")
    }

    fun onLoadError(error: Throwable, wasCanceled: Boolean) {
        loadError = "${chain(error)} canceled=$wasCanceled"
        log("loadError $loadError")
    }

    fun onDropped(frames: Int, elapsedMs: Long) {
        droppedFrames += frames
        droppedEvents++
        log("dropped $frames frames in $elapsedMs ms (total $droppedFrames)")
    }

    fun onSurfaceSizeSeenByPlayer(width: Int, height: Int) {
        surfaceSizeSeenByPlayer = "${width}x$height"
        log("player surfaceSize $surfaceSizeSeenByPlayer")
    }

    fun onTracks(groups: Tracks) {
        tracks = describeVideoTracks(groups)
        log("tracksChanged ${tracks.firstOrNull() ?: "no video"}")
    }

    // -- the surface's side ------------------------------------------------------------------------

    fun onShowOn(view: String) {
        showOnCalls++
        log("showOn #$showOnCalls $view")
    }

    fun onShowOnIgnored() = log("showOn ignored (released)")

    fun onHideFrom(view: String) {
        hideFromCalls++
        log("hideFrom #$hideFromCalls $view")
    }

    fun onSurfaceCreated() {
        surfaceCreatedCount++
        log("surfaceCreated #$surfaceCreatedCount")
    }

    fun onSurfaceChanged(format: Int, width: Int, height: Int) {
        surfaceChangedCount++
        lastSurfaceChanged = "${width}x$height format=$format"
        log("surfaceChanged #$surfaceChangedCount $lastSurfaceChanged")
    }

    fun onSurfaceDestroyed() {
        surfaceDestroyedCount++
        log("surfaceDestroyed #$surfaceDestroyedCount")
    }

    fun onReleased() = log("released")

    // -- the pane's side ---------------------------------------------------------------------------

    /** The pane's own view of itself; logged only when it changes. */
    fun onPane(info: PaneInfo) {
        val text = info.describe()
        if (text == pane) return
        pane = text
        log("pane $text")
    }

    // -- reading it ---------------------------------------------------------------------------------

    /** The state lines, with the live numbers sampled now. */
    fun stateLines(): List<String> {
        val p = sampler?.invoke()
        val s = surfaceSampler?.invoke()
        val firstFrame = firstFrameAt
        val prepared = prepareAt
        return buildList {
            add("url: ${url ?: "none"}")
            add(
                "player: " + if (p?.playbackState != null) {
                    "state=${stateName(p.playbackState)} playWhenReady=${p.playWhenReady} isPlaying=${p.isPlaying}"
                } else {
                    "none (released or absent)"
                },
            )
            add(
                "position: " + if (p?.playbackState != null) {
                    "${ms(p.positionMs)} / ${ms(p.durationMs)} buffered=${ms(p.bufferedMs)}"
                } else {
                    "n/a"
                },
            )
            add("videoSize: $lastVideoSize")
            add("aspectRatio now: ${fmt(p?.aspectRatio)}")
            add(
                "firstFrame: " + when {
                    firstFrame != null -> "yes, ${sincePrepare(firstFrame)} since prepare"
                    prepared != null -> "NO, ${clock() - prepared} ms since prepare"
                    else -> "NO (not prepared)"
                },
            )
            add("format: $lastFormat")
            p?.currentFormat?.let { add("player.videoFormat: $it") }
            add("decoder: $decoder")
            add("codecError: $codecError")
            add("videoDisabled: ${videoDisabledCount}x")
            add("error: $lastError")
            add("loadError: $loadError")
            add("dropped: $droppedFrames frames in $droppedEvents reports")
            val liveTracks = p?.tracks ?: tracks
            if (liveTracks.isEmpty()) add("tracks: no video track reported") else liveTracks.forEach { add("tracks: $it") }
            add("surface calls: showOn=$showOnCalls hideFrom=$hideFromCalls")
            add(
                "surface callbacks: created=$surfaceCreatedCount changed=$surfaceChangedCount " +
                    "destroyed=$surfaceDestroyedCount last=$lastSurfaceChanged",
            )
            add("player sees surface: $surfaceSizeSeenByPlayer")
            add(
                "view: " + if (s == null) "none attached" else
                    "${s.width}x${s.height} attached=${s.attached} visibility=${s.visibility} " +
                        "surfaceValid=${s.surfaceValid} onScreen=(${s.screenX},${s.screenY})",
            )
            add("box in window: $box")
            add("pane: $pane")
        }
    }

    /** The recorded events, oldest first. */
    fun eventLines(): List<String> = events.lines()

    private fun sincePrepare(at: Long): String = "${at - (prepareAt ?: createdAt)} ms"

    companion object {
        const val EVENT_CAPACITY = 40

        fun stateName(state: Int?): String = when (state) {
            null -> "n/a"
            Player.STATE_IDLE -> "IDLE"
            Player.STATE_BUFFERING -> "BUFFERING"
            Player.STATE_READY -> "READY"
            Player.STATE_ENDED -> "ENDED"
            else -> "UNKNOWN($state)"
        }

        private fun fmt(value: Float?): String = if (value == null) "null" else String.format(Locale.US, "%.4f", value)

        private fun ms(value: Long): String = if (value == C.TIME_UNSET || value < 0) "?" else "${value} ms"

        fun describeFormat(format: Format): String = buildString {
            append(format.sampleMimeType ?: "?")
            append(" codecs=").append(format.codecs ?: "?")
            append(' ').append(format.width).append('x').append(format.height)
            append(" fps=").append(if (format.frameRate == Format.NO_VALUE.toFloat()) "?" else format.frameRate.toString())
            append(" rot=").append(format.rotationDegrees)
            format.containerMimeType?.let { append(" container=").append(it) }
            if (format.bitrate != Format.NO_VALUE) append(" bitrate=").append(format.bitrate)
            format.colorInfo?.let { append(" color=").append(it) }
        }

        /** The video groups of [tracks]: a count line, then one line per group. */
        fun describeVideoTracks(tracks: Tracks): List<String> {
            val video = tracks.groups.filter { it.type == C.TRACK_TYPE_VIDEO }
            if (video.isEmpty()) return emptyList()
            return buildList {
                add("${video.size} video group(s) of ${tracks.groups.size} total")
                video.forEachIndexed { g, group ->
                    for (i in 0 until group.length) {
                        add(
                            "group$g track$i selected=${group.isTrackSelected(i)} " +
                                "supported=${group.isTrackSupported(i)} " +
                                "support=${C.getFormatSupportString(group.getTrackSupport(i))} " +
                                describeFormat(group.getTrackFormat(i)),
                        )
                    }
                }
            }
        }

        /** "class: message" for the throwable and each cause under it, up to eight deep. */
        fun chain(error: Throwable): String =
            generateSequence(error) { it.cause }.take(8)
                .joinToString(" <- ") { "${it.javaClass.name}: ${it.message}" }
    }
}

/** What the live player says at the moment the report is read. */
data class PlayerSample(
    val playbackState: Int?,
    val playWhenReady: Boolean,
    val isPlaying: Boolean,
    val positionMs: Long,
    val durationMs: Long,
    val bufferedMs: Long,
    val aspectRatio: Float?,
    val currentFormat: String?,
    val tracks: List<String>,
)

/** What the attached `SurfaceView` looks like at the moment the report is read. */
data class SurfaceSample(
    val width: Int,
    val height: Int,
    val attached: Boolean,
    val visibility: String,
    val surfaceValid: Boolean,
    val screenX: Int,
    val screenY: Int,
)

/** The pane's own view of itself, as a one-line description. */
data class PaneInfo(
    val constraints: String,
    val state: String,
    val coverShowing: Boolean,
    val ratio: Float?,
    val playing: Boolean,
) {
    fun describe(): String =
        "constraints=$constraints state=$state cover=$coverShowing " +
            "ratio=${ratio?.let { String.format(Locale.US, "%.4f", it) } ?: "null"} playing=$playing"
}

/** A fixed-size ring of timestamped event lines. Oldest drops first. */
class EventRing(private val capacity: Int) {
    private val items = ArrayDeque<String>()

    @Synchronized
    fun add(atMs: Long, text: String) {
        if (items.size == capacity) items.removeFirst()
        items.addLast(String.format(Locale.US, "+%6d ms  %s", atMs, text))
    }

    @Synchronized
    fun lines(): List<String> = items.toList()
}
