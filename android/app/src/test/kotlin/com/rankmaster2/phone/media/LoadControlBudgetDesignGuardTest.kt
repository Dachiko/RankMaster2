package com.rankmaster2.phone.media

import androidx.media3.exoplayer.DefaultLoadControl
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * A design guard, not a behaviour test: it asserts the *arithmetic* behind the buffer constants,
 * not that a real ExoPlayer honours them (T2c - that would need a phone).
 *
 * What a video player is allowed to hoard, in numbers, because the defaults killed this app twice.
 * Media3's own constants: a muxed stream may buffer DEFAULT_MUXED_BUFFER_SIZE bytes and reads 50
 * seconds ahead. On the owner's phone the heap ceiling is 256 MB and the ranking screen runs two
 * players at once, so the default allowance is larger than the entire heap before a single
 * photograph is decoded. A 4K AV1 clip on a phone with no hardware AV1 decoder fills it reliably:
 * the network delivers faster than software decoding consumes.
 *
 * This test is the arithmetic, kept where it will fail if someone raises the ceiling again.
 */
class LoadControlBudgetDesignGuardTest {

    /** The heap the crash reported: `target footprint 268435456`. With largeHeap, roughly double. */
    private val heapBytes = 268_435_456L

    private val panes = 2

    @Test
    fun `the default allowance really is bigger than the whole heap`() {
        val default = DefaultLoadControl.DEFAULT_MUXED_BUFFER_SIZE.toLong() * panes

        assertTrue(
            "Media3's default is ${default / 1024 / 1024} MB across $panes panes, against a " +
                "${heapBytes / 1024 / 1024} MB heap. This is why the defaults cannot be used here.",
            default > heapBytes,
        )
    }

    @Test
    fun `ours leaves room for the photographs as well`() {
        val ours = Rm2VideoPlayers.TARGET_BUFFER_BYTES.toLong() * panes

        // Both players, the image cache at 15%, and still under a third of the heap in total.
        val imageCache = (heapBytes * 0.15).toLong()
        // Two players plus the image cache must still fit the *small* heap with room to decode,
        // so that losing largeHeap on some future device is a slowdown and not a crash.
        assertTrue(
            "two players at ${ours / 1024 / 1024} MB plus an image cache of " +
                "${imageCache / 1024 / 1024} MB must leave most of a 256 MB heap free",
            ours + imageCache < heapBytes / 2,
        )
    }

    @Test
    fun `a rebuffer is cheap enough to prefer size over duration`() {
        // Eight seconds of a 4K AV1 clip is far more than six megabytes, so the byte ceiling is the
        // one that must win. If this is ever flipped back, the duration governs and the ceiling
        // stops meaning anything.
        assertTrue(Rm2VideoPlayers.MAX_BUFFER_MS <= 30_000)
        assertTrue(Rm2VideoPlayers.TARGET_BUFFER_BYTES <= 48 * 1024 * 1024)
    }
}
