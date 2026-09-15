package com.rankmaster2.phone.rank

import androidx.compose.ui.geometry.Offset
import com.rankmaster2.phone.ui.rank.EdgeMarginFraction
import com.rankmaster2.phone.ui.rank.Side
import com.rankmaster2.phone.ui.rank.sideAt
import com.rankmaster2.phone.ui.rank.votableSideAt
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The margin around the outside where a tap does not vote.
 *
 * Asked for because a wrong vote is the one mistake on this screen that costs something, and the
 * edge of the screen is where a hand rests while holding a phone. It is the seam's dead band turned
 * outward, and it obeys the same rule: doing nothing is always recoverable, and a wrong vote is not.
 *
 * The distinction these tests exist to keep is that the margin kills **taps only**. Nobody long-
 * presses by accident, and the pane menu is the one thing somebody might deliberately reach for in
 * a corner - so [sideAt] still answers everywhere and only [votableSideAt] goes deaf.
 */
class EdgeMarginTest {

    private val w = 1080f
    private val h = 2400f
    private val band = 120f

    private fun vote(x: Float, y: Float, portrait: Boolean = true) =
        votableSideAt(Offset(x, y), portrait, w, h, band)

    // -- what the margin kills ------------------------------------------------------------------

    @Test
    fun `a tap in any of the four margins does nothing`() {
        // The two the thumbs sit on in portrait.
        assertNull("left edge", vote(10f, 600f))
        assertNull("right edge", vote(w - 10f, 600f))
        // And the two at the ends, which are where a phone rests in the other hand.
        assertNull("top edge", vote(540f, 10f))
        assertNull("bottom edge", vote(540f, h - 10f))
    }

    @Test
    fun `the corners - where a hand actually rests - are dead`() {
        assertNull(vote(0f, 0f))
        assertNull(vote(w, 0f))
        assertNull(vote(0f, h))
        assertNull(vote(w, h))
    }

    @Test
    fun `it is the same fraction in landscape, so it stays proportional`() {
        // Landscape puts the thumbs on the left and right rather than the bottom; the margin is a
        // share of each dimension, so it follows without anything being re-tuned.
        assertNull(vote(10f, 1200f, portrait = false))
        assertNull(vote(w - 10f, 1200f, portrait = false))
        assertEquals(Side.LEFT, vote(200f, 1200f, portrait = false))
        assertEquals(Side.RIGHT, vote(900f, 1200f, portrait = false))
    }

    // -- what it leaves alone -------------------------------------------------------------------

    @Test
    fun `the middle of each pane still votes`() {
        assertEquals(Side.LEFT, vote(540f, h * 0.25f))
        assertEquals(Side.RIGHT, vote(540f, h * 0.75f))
    }

    @Test
    fun `the seam band is still dead as well, and the two stack`() {
        assertNull(vote(540f, h / 2f))
        // Bounded by the outside on three sides and the seam on the fourth.
        assertEquals(Side.LEFT, vote(540f, h / 2f - band))
        assertEquals(Side.RIGHT, vote(540f, h / 2f + band))
    }

    @Test
    fun `a long press still reaches the corner, because nobody does that by accident`() {
        // The pane menu - view, discard, special - is the one thing worth reaching for at an edge.
        assertNotNull(sideAt(Offset(0f, 0f), portrait = true, w, h, band))
        assertNotNull(sideAt(Offset(w, h), portrait = true, w, h, band))
        assertEquals(Side.LEFT, sideAt(Offset(5f, 5f), portrait = true, w, h, band))
        assertEquals(Side.RIGHT, sideAt(Offset(w - 5f, h - 5f), portrait = true, w, h, band))
    }

    // -- the number itself ----------------------------------------------------------------------

    @Test
    fun `most of each pane is still live`() {
        // 10% off each side leaves the middle 80% by 80%, which is 64% of a pane - and it is the
        // 64% the photograph is actually in. A margin that took more than this would be a margin
        // that had to be aimed around.
        val live = (1f - 2 * EdgeMarginFraction) * (1f - 2 * EdgeMarginFraction)
        assertTrue("only $live of a pane would be live", live > 0.6f)
    }

    @Test
    fun `the edge is exactly where the fraction says it is`() {
        val side = w * EdgeMarginFraction
        val cap = h * EdgeMarginFraction

        assertNull(vote(side - 1f, h * 0.25f))
        assertEquals(Side.LEFT, vote(side + 1f, h * 0.25f))
        assertNull(vote(540f, cap - 1f))
        assertEquals(Side.LEFT, vote(540f, cap + 1f))
    }
}
