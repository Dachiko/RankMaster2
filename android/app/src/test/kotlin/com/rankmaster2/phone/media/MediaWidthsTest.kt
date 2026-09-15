package com.rankmaster2.phone.media

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The width rule, across the whole range. The server refuses anything outside its six values with
 * `400 unsupported_width` (SERVER_SPEC.md § 12.3), so every one of these cases is a request that
 * either works or is rejected outright - there is no "close enough".
 */
class MediaWidthsTest {

    @Test
    fun `the allowed set is exactly the server's`() {
        assertEquals(listOf(360, 540, 720, 1080, 1440, 2160), MediaWidths.ALLOWED)
    }

    @Test
    fun `every boundary asks for itself`() {
        for (w in MediaWidths.ALLOWED) {
            assertEquals("exactly $w", w, MediaWidths.forPane(w))
        }
    }

    @Test
    fun `one pixel over a boundary steps up`() {
        assertEquals(540, MediaWidths.forPane(361))
        assertEquals(720, MediaWidths.forPane(541))
        assertEquals(1080, MediaWidths.forPane(721))
        assertEquals(1440, MediaWidths.forPane(1081))
        assertEquals(2160, MediaWidths.forPane(1441))
    }

    @Test
    fun `one pixel under a boundary does not step up`() {
        assertEquals(360, MediaWidths.forPane(359))
        assertEquals(540, MediaWidths.forPane(539))
        assertEquals(720, MediaWidths.forPane(719))
        assertEquals(1080, MediaWidths.forPane(1079))
        assertEquals(1440, MediaWidths.forPane(1439))
        assertEquals(2160, MediaWidths.forPane(2159))
    }

    @Test
    fun `below the smallest allowed width, ask for the smallest`() {
        assertEquals(360, MediaWidths.forPane(1))
        assertEquals(360, MediaWidths.forPane(120))
        assertEquals(360, MediaWidths.forPane(359))
    }

    @Test
    fun `a zero or nonsense pane still asks for something legal`() {
        assertEquals(360, MediaWidths.forPane(0))
        assertEquals(360, MediaWidths.forPane(-1))
        assertEquals(360, MediaWidths.forPane(Int.MIN_VALUE))
    }

    @Test
    fun `above the largest allowed width, cap - never compute one`() {
        assertEquals(2160, MediaWidths.forPane(2161))
        assertEquals(2160, MediaWidths.forPane(2880))
        assertEquals(2160, MediaWidths.forPane(3840))
        assertEquals(2160, MediaWidths.forPane(Int.MAX_VALUE))
    }

    @Test
    fun `every answer is one the server accepts, for every pane size in the range`() {
        for (px in -10..4100) {
            assertTrue(px.toString(), MediaWidths.isAllowed(MediaWidths.forPane(px)))
        }
    }

    @Test
    fun `the answer is monotonic - a bigger pane never asks for fewer pixels`() {
        var previous = 0
        for (px in 0..4100) {
            val w = MediaWidths.forPane(px)
            assertTrue("$px went backwards", w >= previous)
            previous = w
        }
    }

    @Test
    fun `a pane is measured by its long edge, because w bounds the long edge`() {
        // StillRenderer.FitLongEdge fits max(width, height) to w. A 540 px wide, 2000 px tall pane
        // can display a picture 2000 px down its long edge, and asking for 540 would blur it.
        assertEquals(2160, MediaWidths.forPane(widthPx = 540, heightPx = 2000))
        assertEquals(1080, MediaWidths.forPane(widthPx = 1000, heightPx = 600))
        assertEquals(MediaWidths.forPane(1000), MediaWidths.forPane(1000, 1000))
    }

    @Test
    fun `a computed width is not an allowed width`() {
        assertFalse(MediaWidths.isAllowed(1000))
        assertFalse(MediaWidths.isAllowed(320))   // thumb's width is not a still width
        assertFalse(MediaWidths.isAllowed(2161))
    }
}
