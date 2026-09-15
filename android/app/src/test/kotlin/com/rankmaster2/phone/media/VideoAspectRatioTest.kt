package com.rankmaster2.phone.media

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/**
 * The shape of a video pane, which the owner saw stretched until he turned the phone twice.
 *
 * The bug was never arithmetic - it was *when*. The pane took its shape from a `PlayerView` that
 * learns the video's size after the first frame is decoded, long after Compose measured the box
 * around it, and nothing asked Compose to look again. Rotating forced a fresh layout pass, which is
 * the whole of "rotating twice fixes it".
 *
 * So the shape moved into Compose state, and what is left to get wrong is this function. It has one
 * trap in it, and it is the one that would produce a quieter version of the same bug: an anamorphic
 * file stores pixels that are not square, and `width / height` is then not the shape of the picture.
 */
class VideoAspectRatioTest {

    private fun ratio(width: Int, height: Int, pixels: Float = 1f) =
        Rm2VideoPlayers.aspectRatioOf(width, height, pixels)

    @Test
    fun `an ordinary landscape clip is as wide as it is`() {
        assertEquals(16f / 9f, ratio(1920, 1080)!!, 0.0001f)
    }

    @Test
    fun `a phone video held upright is taller than it is wide`() {
        // The case that looks most wrong when it is wrong: a 9:16 clip stretched to fill a
        // landscape-ish pane is unmistakable, and it is most of the owner's own footage.
        assertEquals(9f / 16f, ratio(1080, 1920)!!, 0.0001f)
    }

    @Test
    fun `anamorphic pixels widen the picture, not the pixel count`() {
        // 720x576 PAL stored at the 64:45 pixel aspect is a 16:9 picture. Ignoring the third
        // number draws it at 1.25:1 - squashed, and squashed subtly enough to be argued about
        // rather than reported, which is the worst kind of wrong for this pane to be.
        val anamorphic = ratio(720, 576, pixels = 64f / 45f)!!
        assertEquals(16f / 9f, anamorphic, 0.001f)

        // And it is genuinely different from the naive answer, which is what makes it worth a test.
        assertEquals(1.25f, ratio(720, 576)!!, 0.0001f)
    }

    @Test
    fun `nothing to measure yet is null, not a guess`() {
        // Before the first frame, and for an audio-only or broken track. A pane told "16:9" here
        // would be confidently the wrong shape, which is worse than filling its box until it knows.
        assertNull(ratio(0, 0))
        assertNull(ratio(1920, 0))
        assertNull(ratio(0, 1080))
    }

    @Test
    fun `a nonsense pixel ratio is refused rather than propagated`() {
        assertNull(ratio(1920, 1080, pixels = 0f))
        assertNull(ratio(1920, 1080, pixels = -1f))
        assertNull(ratio(1920, 1080, pixels = Float.NaN))
        assertNull(ratio(1920, 1080, pixels = Float.POSITIVE_INFINITY))
    }
}
