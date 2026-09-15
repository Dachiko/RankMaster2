package com.rankmaster2.phone.media

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import org.robolectric.RobolectricTestRunner
import org.junit.runner.RunWith

/**
 * The red dot: when a video admits it cannot keep up, and when it takes the admission back.
 *
 * Asked for in place of a probe screen, and it is the better instrument - it reports on the owner's
 * own files, while he is ranking them, at the moment it matters. The rule it has to obey is that it
 * marks sustained stutter and ignores a hiccup, because a dot that appears after every start is a
 * dot nobody looks at.
 */
@RunWith(RobolectricTestRunner::class)
class StrugglingVideoTest {

    private fun video() = Rm2VideoPlayers.testVideo()

    @Test
    fun `a hiccup is not a struggle`() {
        val video = video()

        // Two frames lost across a second: a start, a seek, a garbage collection.
        video.reportDroppedFrames(dropped = 2, elapsedMs = 1_000)

        assertFalse(video.struggling.value)
    }

    @Test
    fun `sustained stutter raises the mark`() {
        val video = video()

        // Fifteen frames gone in a second is visible: this phone is not decoding this in real time.
        video.reportDroppedFrames(dropped = 15, elapsedMs = 1_000)

        assertTrue(video.struggling.value)
    }

    @Test
    fun `the mark does not flicker off at the first clean report`() {
        val video = video()
        video.reportDroppedFrames(dropped = 20, elapsedMs = 1_000)
        assertTrue(video.struggling.value)

        // Immediately clean - but too soon to call it recovered. A dot that blinks is noise.
        video.reportDroppedFrames(dropped = 0, elapsedMs = 1_000)

        assertTrue(video.struggling.value)
    }

    @Test
    fun `a batch of zero length is ignored rather than dividing by it`() {
        val video = video()

        video.reportDroppedFrames(dropped = 9, elapsedMs = 0)

        assertFalse(video.struggling.value)
    }
}
