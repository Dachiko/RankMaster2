package com.rankmaster2.phone.review

import androidx.media3.common.Format
import androidx.media3.common.MimeTypes
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import com.rankmaster2.phone.media.EventRing
import com.rankmaster2.phone.media.PaneInfo
import com.rankmaster2.phone.media.PlayerSample
import com.rankmaster2.phone.media.SurfaceSample
import com.rankmaster2.phone.media.VideoDiagnostics
import com.rankmaster2.phone.net.Links
import com.rankmaster2.phone.net.MediaRef
import com.rankmaster2.phone.ui.review.DebugEnv
import com.rankmaster2.phone.ui.review.DebugScreen
import com.rankmaster2.phone.ui.review.redactDebugText
import com.rankmaster2.phone.ui.review.reviewDebugText
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner

/**
 * The review debug overlay's plain logic: the event ring, the way a video state reads out, and
 * that the copied text carries the fields the owner is going to send back.
 */
@RunWith(RobolectricTestRunner::class)
class ReviewDebugTest {

    private val env = DebugEnv("3.2.1-debug", 6L, "Google Pixel 7", "14", 34)

    private val videoRef = MediaRef(
        id = "clips/a b.mp4",
        kind = "video",
        sizeBytes = 123_456_789L,
        mediaVersion = "v7",
        links = Links(meta = "/m", video = "/v"),
    )

    private fun screen(item: MediaRef?) = DebugScreen(
        item = item,
        phase = "Reviewing",
        playing = true,
        foreground = true,
        index = 3,
        count = 120,
    )

    @Test
    fun `the ring keeps the last N events, oldest first, with timestamps`() {
        val ring = EventRing(3)
        (1..5).forEach { ring.add(it * 10L, "event $it") }

        val lines = ring.lines()
        assertEquals(3, lines.size)
        assertTrue(lines.first().contains("event 3"))
        assertTrue(lines.last().contains("event 5"))
        assertTrue(lines.last().startsWith("+    50 ms"))
    }

    @Test
    fun `a diagnostics log never grows past forty events`() {
        var clock = 0L
        val diagnostics = VideoDiagnostics("https://pc/v", clock = { clock += 5; clock })
        repeat(100) { diagnostics.log("e$it") }

        val events = diagnostics.eventLines()
        assertEquals(VideoDiagnostics.EVENT_CAPACITY, events.size)
        assertTrue(events.last().contains("e99"))
        assertTrue(events.first().contains("e60"))
    }

    @Test
    fun `a video state reads out what the player, the decoder and the surface reported`() {
        var clock = 1_000L
        val diagnostics = VideoDiagnostics("https://pc/v?id=1", clock = { clock })
        diagnostics.sampler = {
            PlayerSample(
                playbackState = Player.STATE_READY,
                playWhenReady = true,
                isPlaying = true,
                positionMs = 1_500,
                durationMs = 9_000,
                bufferedMs = 9_000,
                aspectRatio = 1.7778f,
                currentFormat = null,
                tracks = listOf("1 video group(s) of 2 total"),
            )
        }
        diagnostics.surfaceSampler = { SurfaceSample(1080, 608, true, "VISIBLE", true, 0, 700) }

        diagnostics.onPrepare()
        clock += 250
        diagnostics.onVideoFormat(
            Format.Builder().setSampleMimeType(MimeTypes.VIDEO_H265).setCodecs("hvc1.2.4.L153")
                .setWidth(3840).setHeight(2160).setFrameRate(30f).build(),
        )
        diagnostics.onDecoderInitialized("c2.qti.hevc.decoder", 18)
        diagnostics.onVideoSize(3840, 2160, 1f, 1.7778f)
        clock += 100
        diagnostics.onFirstFrame()
        diagnostics.onSurfaceCreated()
        diagnostics.onSurfaceChanged(4, 1080, 608)
        diagnostics.onShowOn("view@1 0x0")
        diagnostics.onPane(
            PaneInfo("0x0..1080x2000", "Loaded", coverShowing = false, ratio = 1.7778f, playing = true),
        )
        diagnostics.onPlayerError(
            PlaybackException("boom", RuntimeException("inner cause"), PlaybackException.ERROR_CODE_DECODING_FAILED),
        )

        val text = diagnostics.stateLines().joinToString("\n")

        assertTrue(text, text.contains("url: https://pc/v?id=1"))
        assertTrue(text, text.contains("state=READY playWhenReady=true isPlaying=true"))
        assertTrue(text, text.contains("1500 ms / 9000 ms"))
        assertTrue(text, text.contains("3840x2160 par=1.0 -> aspectRatio=1.7778"))
        assertTrue(text, text.contains("firstFrame: yes, 350 ms since prepare"))
        assertTrue(text, text.contains("video/hevc codecs=hvc1.2.4.L153 3840x2160 fps=30.0"))
        assertTrue(text, text.contains("c2.qti.hevc.decoder"))
        assertTrue(text, text.contains("showOn=1 hideFrom=0"))
        assertTrue(text, text.contains("created=1 changed=1"))
        assertTrue(text, text.contains("1080x608 attached=true visibility=VISIBLE surfaceValid=true"))
        assertTrue(text, text.contains("cover=false"))
        assertTrue(text, text.contains("ERROR_CODE_DECODING_FAILED"))
        assertTrue(text, text.contains("inner cause"))
    }

    @Test
    fun `a first frame that never came says so`() {
        var clock = 0L
        val diagnostics = VideoDiagnostics("u", clock = { clock })
        diagnostics.onPrepare()
        clock += 4_000

        assertTrue(diagnostics.stateLines().any { it.startsWith("firstFrame: NO, 4000 ms since prepare") })
    }

    @Test
    fun `an unchanged pane description is not logged twice`() {
        val diagnostics = VideoDiagnostics("u")
        val info = PaneInfo("0x0..10x10", "Loading", coverShowing = true, ratio = null, playing = true)
        diagnostics.onPane(info)
        diagnostics.onPane(info)
        diagnostics.onPane(info.copy(coverShowing = false))

        assertEquals(2, diagnostics.eventLines().size)
    }

    @Test
    fun `the copied text carries version, device, item, review bits, the video state and the whole log`() {
        val diagnostics = VideoDiagnostics("https://pc/api/v1/video?id=1")
        diagnostics.onPrepare()
        diagnostics.log("something happened")

        val text = reviewDebugText(
            env, screen(videoRef), "video",
            diagnostics.stateLines(), diagnostics.eventLines(),
        )

        assertTrue(text, text.contains("app: 3.2.1-debug (code 6)"))
        assertTrue(text, text.contains("device: Google Pixel 7, Android 14 (SDK 34)"))
        assertTrue(text, text.contains("id=clips/a b.mp4 kind=video sizeBytes=123456789 mediaVersion=v7"))
        assertTrue(text, text.contains("phase=Reviewing playing=true foreground=true index=3/120"))
        assertTrue(text, text.contains("url: https://pc/api/v1/video?id=1"))
        assertTrue(text, text.contains("firstFrame: NO"))
        assertTrue(text, text.contains("--- events (last 2) ---"))
        assertTrue(text, text.contains("something happened"))
    }

    @Test
    fun `a still shows its url and state instead`() {
        val still = videoRef.copy(kind = "still", links = Links(meta = "/m", still = "/s"))
        val text = reviewDebugText(
            env, screen(still), "still",
            listOf("url: https://pc/s?w=1080", "coil/pane state: Loaded"), emptyList(),
        )

        assertTrue(text, text.contains("kind=still"))
        assertTrue(text, text.contains("url: https://pc/s?w=1080"))
        assertTrue(text, text.contains("(none)"))
    }

    @Test
    fun `the copied text names no file and no url`() {
        val raw = listOf(
            "item: id=Holiday clip.mp4 kind=video",
            "url: https://192.168.1.5:18611/api/v1/media/Holiday%20clip.mp4/video?v=ab",
            "error: Response code 404 for Holiday%20clip.mp4 and Holiday clip.mp4",
        ).joinToString("\n")

        val text = redactDebugText(raw, "Holiday clip.mp4")

        assertTrue(text, !text.contains("Holiday"))
        assertTrue(text, !text.contains("192.168"))
        assertTrue(text, text.contains("id=<file>.mp4 kind=video"))
        assertTrue(text, text.contains("url: <url>"))
    }
}
