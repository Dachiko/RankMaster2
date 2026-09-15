package com.rankmaster2.phone.media

import androidx.compose.ui.unit.Constraints
import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * What a pane asks the server for, given the box Compose measured it into.
 *
 * The pane is measured in pixels already - Compose constraints are pixels - so there is no density
 * arithmetic here, and deliberately so: a `w` computed from dp and a scale factor is a `w` that can
 * land between the allowed values and earn a `400 unsupported_width`.
 */
class MediaPaneGeometryTest {

    @Test
    fun `a pane is measured by its long edge`() {
        // Half of a 1080x2400 phone, in portrait: one pane above the other.
        assertEquals(1200, paneLongEdgePx(Constraints.fixed(width = 1080, height = 1200)))
        assertEquals(1440, MediaWidths.forPane(paneLongEdgePx(Constraints.fixed(1080, 1200))))

        // The same phone on its side: two panes side by side.
        assertEquals(1200, paneLongEdgePx(Constraints.fixed(width = 1200, height = 1080)))
    }

    @Test
    fun `an unbounded pane falls back to the server's own default`() {
        // A pane inside a scrolling column has no height to work from; 1080 is what the server uses
        // when `w` is omitted, so it is the least surprising answer.
        assertEquals(
            MediaWidths.DEFAULT,
            paneLongEdgePx(Constraints(minWidth = 0, maxWidth = Constraints.Infinity, minHeight = 0, maxHeight = Constraints.Infinity)),
        )
    }

    @Test
    fun `a pane unbounded in one direction uses the direction it does know`() {
        val constraints = Constraints(minWidth = 0, maxWidth = 900, minHeight = 0, maxHeight = Constraints.Infinity)
        assertEquals(900, paneLongEdgePx(constraints))
        assertEquals(1080, MediaWidths.forPane(paneLongEdgePx(constraints)))
    }

    @Test
    fun `a zero-sized pane still produces a width the server accepts`() {
        val w = MediaWidths.forPane(paneLongEdgePx(Constraints.fixed(0, 0)))
        assertEquals(MediaWidths.DEFAULT, w)
    }
}
