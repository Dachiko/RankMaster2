package com.rankmaster2.phone.rank

import androidx.compose.ui.geometry.Offset
import com.rankmaster2.phone.ui.rank.Side
import com.rankmaster2.phone.ui.rank.sideAt
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/**
 * Where a touch lands, and where it deliberately lands nowhere.
 *
 * A vote cannot be undone by looking at it - cancel exists but costs a round trip and the owner's
 * attention - so the border between the two targets must not guess. A thumb on the seam does
 * nothing, which is the one outcome that is always recoverable.
 */
class SeamTest {

    private val w = 1080f
    private val h = 2400f
    private val band = 120f

    @Test
    fun `portrait splits at the middle, top is left`() {
        assertEquals(Side.LEFT, sideAt(Offset(540f, 100f), portrait = true, w, h, band))
        assertEquals(Side.RIGHT, sideAt(Offset(540f, 2300f), portrait = true, w, h, band))
    }

    @Test
    fun `landscape splits at the middle, the left half is left`() {
        assertEquals(Side.LEFT, sideAt(Offset(100f, 400f), portrait = false, w, h, band))
        assertEquals(Side.RIGHT, sideAt(Offset(1000f, 400f), portrait = false, w, h, band))
    }

    @Test
    fun `dead on the seam is nothing, in both orientations`() {
        assertNull(sideAt(Offset(540f, h / 2f), portrait = true, w, h, band))
        assertNull(sideAt(Offset(w / 2f, 400f), portrait = false, w, h, band))
    }

    @Test
    fun `the whole band is dead, and one pixel past it is not`() {
        val seam = h / 2f
        assertNull(sideAt(Offset(540f, seam - band / 2f + 1f), portrait = true, w, h, band))
        assertNull(sideAt(Offset(540f, seam + band / 2f - 1f), portrait = true, w, h, band))

        assertEquals(Side.LEFT, sideAt(Offset(540f, seam - band / 2f - 1f), portrait = true, w, h, band))
        assertEquals(Side.RIGHT, sideAt(Offset(540f, seam + band / 2f + 1f), portrait = true, w, h, band))
    }

    @Test
    fun `the far edges are always a side`() {
        assertEquals(Side.LEFT, sideAt(Offset(0f, 0f), portrait = true, w, h, band))
        assertEquals(Side.RIGHT, sideAt(Offset(w, h), portrait = true, w, h, band))
        assertEquals(Side.LEFT, sideAt(Offset(0f, 0f), portrait = false, w, h, band))
        assertEquals(Side.RIGHT, sideAt(Offset(w, h), portrait = false, w, h, band))
    }

    @Test
    fun `the band never swallows a meaningful part of the screen`() {
        // 120px of 2400 is 5% of the height - a thumb's width, not a third of a photograph.
        val dead = (band / h) * 100f
        org.junit.Assert.assertTrue("the dead band is $dead% of the screen", dead < 8f)
    }
}
