package com.rankmaster2.phone.media

import android.content.Context
import androidx.media3.common.PlaybackException
import okhttp3.OkHttpClient
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config

/**
 * The video side, as far as a JVM can see it.
 *
 * What is provable here is the URL and the failure vocabulary. Whether an AV1/MP4, a VP9/WebM or an
 * MPEG-4-in-AVI actually decodes is a question about a phone's codecs and cannot be answered on a
 * build machine at all - which is why a codec refusal is mapped to a state the pane can say out
 * loud rather than swallowed as a generic failure.
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [33])
class Rm2VideoPlayersTest {

    private val context: Context get() = RuntimeEnvironment.getApplication()

    private fun players() =
        Rm2VideoPlayers(context, OkHttpClient(), "https://192.168.1.42:18611/api/v1")

    @Test
    fun `the video link is used verbatim, encoding and all`() {
        val ref = MediaFixtures.video("my clip+1.mp4", encoded = "my%20clip%2B1.mp4")

        assertEquals(
            "https://192.168.1.42:18611/api/v1/media/my%20clip%2B1.mp4/video?v=1122334455667788",
            players().videoUrl(ref),
        )
    }

    @Test
    fun `a still has no video to play, and a vanished file has nothing at all`() {
        assertNull(players().videoUrl(MediaFixtures.still("a.jpg")))
        assertNull(players().videoUrl(MediaFixtures.gone("b.jpg")))
        assertNull(players().create(MediaFixtures.still("a.jpg")))
    }

    @Test
    fun `a codec this phone will not take is reported as undecodable, not as a network failure`() {
        // This is the .avi / exotic-codec case. It has to reach the pane as "this file will not
        // open" so that the owner - and whoever decides about libVLC - can see it happening.
        val unsupported = listOf(
            PlaybackException.ERROR_CODE_DECODING_FORMAT_UNSUPPORTED,
            PlaybackException.ERROR_CODE_DECODER_INIT_FAILED,
            PlaybackException.ERROR_CODE_DECODING_FAILED,
            PlaybackException.ERROR_CODE_PARSING_CONTAINER_UNSUPPORTED,
            PlaybackException.ERROR_CODE_PARSING_CONTAINER_MALFORMED,
        )
        for (code in unsupported) {
            val error = PlaybackException("no", null, code)
            assertEquals(
                PlaybackException.getErrorCodeName(code),
                MediaPaneState.Undecodable,
                Rm2VideoPlayers.stateOf(error),
            )
        }
    }

    @Test
    fun `a network failure is not a reason to discard a video`() {
        val error = PlaybackException("no", null, PlaybackException.ERROR_CODE_IO_NETWORK_CONNECTION_FAILED)
        assertTrue(Rm2VideoPlayers.stateOf(error) is MediaPaneState.Unavailable)
    }

    // -- A12: a slot never holds two players at once ---------------------------------------

    @Test
    fun `acquiring twice for one pane releases the old player before the new one is built`() {
        // A12: the old code let Compose build the replacement player before it forgot the one it
        // replaced, so a single pane could hold two live players for a frame - four, across both
        // panes, at every pair change. Slot.acquire does the two steps itself, in the safe order:
        // release what it already holds, then build. This proves the order without a real player,
        // by having the build side of the *second* acquire look at whether the first one is
        // already released.
        lateinit var previous: Rm2Video
        var previousWasReleasedBeforeSecondBuild = false
        var calls = 0

        val ref = MediaFixtures.video("a.mp4")
        val slot = players().Slot { _, _ ->
            calls++
            if (calls == 2) previousWasReleasedBeforeSecondBuild = previous.isReleased
            Rm2VideoPlayers.testVideo().also { previous = it }
        }

        val first = slot.acquire(ref)
        requireNotNull(first)
        assertTrue(!first.isReleased)

        val second = slot.acquire(ref)
        requireNotNull(second)

        assertTrue(
            "the previous player must be released before the next one is built",
            previousWasReleasedBeforeSecondBuild,
        )
        assertTrue(first.isReleased)
        assertTrue(!second.isReleased)
    }

    // -- § 2.5 (second audit): a pane that is not playing must hold no player at all -------------

    @Test
    fun `a slot's sync releases the player outright when the pane stops playing, not merely pauses it`() {
        // Before this, a covered pane (the full-screen viewer open over it) or a backgrounded app
        // kept its player alive, paused - "not in front means black, not gone", per MediaPane's own
        // old comment. That was fine for one covered pane; it stopped being fine the moment the
        // full-screen viewer built a *second* player for the same file on top of it. Four players
        // at 32 MB each is the number MEMORY_PROPOSALS.md says already killed the app twice.
        var built = 0
        val slot = players().Slot { _, _ -> built++; Rm2VideoPlayers.testVideo() }
        val ref = MediaFixtures.video("a.mp4")

        val playing = slot.sync(ref, playing = true)
        requireNotNull(playing)
        assertTrue(!playing.isReleased)
        assertEquals(1, built)

        val covered = slot.sync(ref, playing = false)
        assertNull("a pane that is not playing must hold no player at all", covered)
        assertTrue("its player must actually be released, not merely paused", playing.isReleased)

        // Staying not-playing must not build another one behind the scenes.
        slot.sync(ref, playing = false)
        assertEquals(1, built)

        val resumed = slot.sync(ref, playing = true)
        requireNotNull(resumed)
        assertTrue(!resumed.isReleased)
        assertEquals("resuming builds a fresh player - it does not resurrect the released one", 2, built)
    }

    @Test
    fun `two panes each releasing on cover leaves the ceiling this app's memory budget assumes`() {
        // The scenario the audit measured: the ranking screen's two panes plus the full-screen
        // viewer's own player for whichever one it is showing. With `sync`, a covered ranking-screen
        // pane holds nothing, so the worst case is the viewer's one player for the pane it shows -
        // never the four that stacked up when covered panes kept theirs.
        val leftSlot = players().Slot { _, _ -> Rm2VideoPlayers.testVideo() }
        val rightSlot = players().Slot { _, _ -> Rm2VideoPlayers.testVideo() }
        val viewerSlot = players().Slot { _, _ -> Rm2VideoPlayers.testVideo() }
        val left = MediaFixtures.video("left.mp4")
        val right = MediaFixtures.video("right.mp4")

        // Ranking screen, both panes playing normally.
        val leftPlaying = leftSlot.sync(left, playing = true)
        val rightPlaying = rightSlot.sync(right, playing = true)
        requireNotNull(leftPlaying)
        requireNotNull(rightPlaying)

        // The owner long-presses and opens the full-screen viewer on the left photograph: the
        // ranking screen's panes are covered, and the viewer builds its own player for the one it
        // shows.
        val leftCovered = leftSlot.sync(left, playing = false)
        val rightCovered = rightSlot.sync(right, playing = false)
        val viewerPlaying = viewerSlot.sync(left, playing = true)

        assertNull(leftCovered)
        assertNull(rightCovered)
        requireNotNull(viewerPlaying)
        assertTrue("the ranking screen's left pane must have let its player go", leftPlaying.isReleased)
        assertTrue("the ranking screen's right pane must have let its player go", rightPlaying.isReleased)

        val liveAtOnce = listOf(leftPlaying, rightPlaying, viewerPlaying).count { !it.isReleased }
        assertEquals("only the viewer's one player may be live while it is open", 1, liveAtOnce)
    }

    @Test
    fun `a slot releases its player on release, and a second release is a no-op`() {
        val video = Rm2VideoPlayers.testVideo()
        val slot = players().Slot { _, _ -> video }

        val acquired = slot.acquire(MediaFixtures.video("a.mp4"))
        assertEquals(video, acquired)
        assertTrue(!video.isReleased)

        slot.release()
        assertTrue(video.isReleased)

        slot.release() // must not throw, and must not touch an already-released video again
    }

    // -- the owner's repro, 2026-09-17: full-screen a video, swipe a few times, press back --------

    @Test
    fun `a pane resuming after being covered keeps the shape it already knew, not null`() {
        // § 2.5 releases a covered pane's player outright rather than pausing it, which is right for
        // the memory ceiling but wrong for `aspectRatio`: the rebuilt player is a fresh `Rm2Video`,
        // and a fresh one starts at null (Rm2Video.aspectRatio's own doc). Until its own
        // `onVideoSizeChanged` arrives, the pane fills its box - not stretched, `RESIZE_MODE_FIT`
        // never does that - but the wrong shape, for however long that takes. This is exactly the
        // owner's repro: open a video full screen (covers the pane), swipe between the pair a few
        // times, press back (pane resumes) - and the pane he lands on flashes to the wrong shape for
        // a file whose shape it had already been told, half a second earlier.
        val ref = MediaFixtures.video("a.mp4")
        val slot = players().Slot { _, _ -> Rm2VideoPlayers.testVideo() }

        val first = slot.sync(ref, playing = true)
        requireNotNull(first)
        assertNull("nothing known yet, the decoder has not reported", first.aspectRatio.value)

        // The decoder reports the shape. Simulated directly - decoding is not available on a build
        // machine, and VideoAspectRatioTest already pins the arithmetic that produces this number.
        first.aspectRatio.value = 9f / 16f

        // The viewer opens over this pane: sync(playing = false) releases the player outright.
        val covered = slot.sync(ref, playing = false)
        assertNull(covered)

        // Back closes the viewer: the pane resumes, for the same file.
        val resumed = slot.sync(ref, playing = true)
        requireNotNull(resumed)

        assertEquals(
            "a file whose shape is already known must not flash back to an unknown-shape box",
            9f / 16f,
            resumed.aspectRatio.value!!,
            0.0001f,
        )
    }

    @Test
    fun `a slot does not carry one file's shape onto a different file`() {
        val a = MediaFixtures.video("a.mp4")
        val b = MediaFixtures.video("b.mp4")
        val slot = players().Slot { _, _ -> Rm2VideoPlayers.testVideo() }

        val first = slot.acquire(a)
        requireNotNull(first)
        first.aspectRatio.value = 9f / 16f

        val second = slot.acquire(b)
        requireNotNull(second)
        assertNull(
            "a different file must start with its shape unknown, not the previous file's",
            second.aspectRatio.value,
        )
    }
}
