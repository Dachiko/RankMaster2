package com.rankmaster2.phone.rank

import androidx.compose.ui.geometry.Offset
import com.rankmaster2.phone.ui.rank.Side
import com.rankmaster2.phone.ui.rank.VotingShareOfScreen
import com.rankmaster2.phone.ui.rank.sideAt
import com.rankmaster2.phone.ui.rank.votableSideAt
import kotlin.math.abs
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Where a tap votes, and where it deliberately does not.
 *
 * The owner kept voting by accident, so the target is one rectangle centred on the glass, covering
 * thirty per cent of it and inset from the four outside edges. It crosses the seam on purpose: the
 * accidental taps come from the edges, never the middle. The rest of the screen still shows the
 * photographs and still answers a long press — it just does not answer a tap.
 *
 * A wrong vote is the only mistake this screen can make that costs anything: it moves a rating
 * nobody asked to move, and taking it back costs a round trip and the owner's attention. Doing
 * nothing is always recoverable, so where the two rules disagree, nothing wins.
 */
class EdgeMarginTest {

    private val w = 1080f
    private val h = 2400f

    // -- the share is what it says it is -----------------------------------------------------------

    @Test
    fun `half the screen votes, within a rounding error`() {
        val step = 4f
        var live = 0
        var total = 0

        var x = 0f
        while (x < w) {
            var y = 0f
            while (y < h) {
                total++
                if (votableSideAt(Offset(x, y), portrait = true, w, h) != null) live++
                y += step
            }
            x += step
        }

        val share = live.toFloat() / total
        assertTrue(
            "the live area is ${"%.3f".format(share)} of the screen, and should be " +
                "${VotingShareOfScreen}",
            abs(share - VotingShareOfScreen) < 0.03f,
        )
    }

    @Test
    fun `and half of it in landscape too, without a second number`() {
        val step = 4f
        var live = 0
        var total = 0

        var x = 0f
        while (x < h) {
            var y = 0f
            while (y < w) {
                total++
                if (votableSideAt(Offset(x, y), portrait = false, h, w) != null) live++
                y += step
            }
            x += step
        }

        assertTrue(abs(live.toFloat() / total - VotingShareOfScreen) < 0.03f)
    }

    // -- where the votes are, and are not ----------------------------------------------------------

    @Test
    fun `the middle of each pane votes for that pane`() {
        assertEquals(Side.LEFT, votableSideAt(Offset(w / 2f, h * 0.25f), portrait = true, w, h))
        assertEquals(Side.RIGHT, votableSideAt(Offset(w / 2f, h * 0.75f), portrait = true, w, h))
    }

    @Test
    fun `the seam votes, and picks the pane the finger is actually over`() {
        // Reversed deliberately, 2026-09-17. The seam used to be dead, on the theory that a tap
        // near it was ambiguous. The owner measured his own thumb against that theory: "It's ok it
        // covers the seam between a pair, the false voting is produced on the edges, not in the
        // center of the screen." A hole in the middle of the one place he aims at was costing him
        // real taps to prevent accidents that were happening somewhere else entirely.
        assertEquals(Side.LEFT, votableSideAt(Offset(w / 2f, h / 2f - 2f), portrait = true, w, h))
        assertEquals(Side.RIGHT, votableSideAt(Offset(w / 2f, h / 2f + 2f), portrait = true, w, h))

        // Landscape is the same rule on the other axis.
        assertEquals(Side.LEFT, votableSideAt(Offset(w / 2f - 2f, h / 2f), portrait = false, w, h))
        assertEquals(Side.RIGHT, votableSideAt(Offset(w / 2f + 2f, h / 2f), portrait = false, w, h))
    }

    @Test
    fun `the live area is one rectangle, not two boxes with a gap between them`() {
        // The distinguishing case: a point that is inside the screen-centred rectangle but would
        // have been in the old per-pane box's seam margin. If this votes, the rectangle is whole.
        val justInsideTheSeam = Offset(w / 2f, h / 2f - h * 0.05f)
        assertEquals(Side.LEFT, votableSideAt(justInsideTheSeam, portrait = true, w, h))
    }

    @Test
    fun `no edge of the glass votes`() {
        for (at in listOf(
            Offset(2f, h * 0.25f),          // left, where a hand wraps round
            Offset(w - 2f, h * 0.25f),      // right, where the cancel notch lives
            Offset(w / 2f, 2f),             // top
            Offset(w / 2f, h - 2f),         // bottom, by the home gesture
            Offset(2f, 2f),                 // and the corners
            Offset(w - 2f, h - 2f),
        )) {
            assertNull("$at must not vote", votableSideAt(at, portrait = true, w, h))
        }
    }

    // -- the long press is a different question ----------------------------------------------------

    @Test
    fun `a long press still answers everywhere a tap refuses to`() {
        val corner = Offset(4f, 4f)

        assertNull("a tap in the corner does nothing", votableSideAt(corner, true, w, h))
        assertEquals(
            "but a long press there still knows which pane it is - nobody long-presses by accident",
            Side.LEFT,
            sideAt(corner, portrait = true, w, h, deadBand = 0f),
        )
    }

    @Test
    fun `everything that votes agrees with which pane it is in`() {
        var x = 0f
        while (x < w) {
            var y = 0f
            while (y < h) {
                val vote = votableSideAt(Offset(x, y), portrait = true, w, h)
                if (vote != null) {
                    assertEquals(
                        "a vote must never be cast for the pane the finger is not in",
                        sideAt(Offset(x, y), portrait = true, w, h, deadBand = 0f),
                        vote,
                    )
                }
                y += 12f
            }
            x += 12f
        }
    }
}
