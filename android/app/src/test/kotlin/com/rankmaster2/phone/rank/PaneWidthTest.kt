package com.rankmaster2.phone.rank

import androidx.compose.ui.unit.Constraints
import com.rankmaster2.phone.media.MediaWidths
import com.rankmaster2.phone.media.paneLongEdgePx
import com.rankmaster2.phone.ui.rank.paneLongEdgePx as screenPaneLongEdgePx
import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * The prefetch and the pane must ask for the same `w`, or the prefetch is a download nobody uses.
 *
 * Warm pairs are fetched from the screen, which works the pane size out from the whole screen, and
 * then drawn by the pane, which works it out from the box Compose measured it into. Those are two
 * calculations of one number, and while they disagreed the warm bytes were cached under one URL and
 * the pane asked for another - so every "warm" pair was downloaded twice and appeared no faster.
 *
 * Portrait agreed by luck. Landscape did not: `max(width, height / 2)` takes the whole screen width
 * for a pane that is half of it.
 */
class PaneWidthTest {

    // A 1080 x 2400 phone.
    private val shortEdge = 1080f
    private val longEdge = 2400f

    @Test
    fun `portrait - the screen and the pane agree`() {
        val fromScreen = screenPaneLongEdgePx(portrait = true, shortEdge, longEdge)
        // Stacked: each pane is the full width and half the height.
        val fromPane = paneLongEdgePx(Constraints.fixed(width = 1080, height = 1200))

        assertEquals(fromPane, fromScreen)
        assertEquals(MediaWidths.forPane(fromPane), MediaWidths.forPane(fromScreen))
    }

    @Test
    fun `landscape - the screen and the pane agree`() {
        val fromScreen = screenPaneLongEdgePx(portrait = false, longEdge, shortEdge)
        // Side by side: each pane is half the width and the full height.
        val fromPane = paneLongEdgePx(Constraints.fixed(width = 1200, height = 1080))

        assertEquals(fromPane, fromScreen)
        assertEquals(MediaWidths.forPane(fromPane), MediaWidths.forPane(fromScreen))
    }

    @Test
    fun `landscape asks for half the screen, not all of it`() {
        // The old rule returned 2400 here: the full width of a landscape screen, for a pane that
        // is 1200 wide. One bucket too large, every time, and never the pane's own URL.
        assertEquals(1200, screenPaneLongEdgePx(portrait = false, longEdge, shortEdge))
    }

    @Test
    fun `whichever way up, the answer is one of the six the server accepts`() {
        listOf(
            screenPaneLongEdgePx(portrait = true, shortEdge, longEdge),
            screenPaneLongEdgePx(portrait = false, longEdge, shortEdge),
            screenPaneLongEdgePx(portrait = true, 720f, 1280f),
            screenPaneLongEdgePx(portrait = false, 3840f, 2160f),
        ).forEach { pane ->
            val w = MediaWidths.forPane(pane)
            assert(MediaWidths.isAllowed(w)) { "w=$w is not one of ${MediaWidths.ALLOWED}" }
        }
    }
}
