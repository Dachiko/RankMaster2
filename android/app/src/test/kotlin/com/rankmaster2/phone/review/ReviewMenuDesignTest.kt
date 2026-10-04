package com.rankmaster2.phone.review

import androidx.compose.ui.unit.dp
import com.rankmaster2.phone.ui.rank.MenuBoxHeight
import com.rankmaster2.phone.ui.rank.MenuGlyph
import com.rankmaster2.phone.ui.rank.RankMenuBodyHeight
import com.rankmaster2.phone.ui.rank.glyphStrokes
import com.rankmaster2.phone.ui.rank.menuBoxHeightFor
import com.rankmaster2.phone.ui.review.ReviewMenuBodyHeight
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The review menu is the ranking menu's own panel with other rows, so what is tested here is only
 * what review adds: two more glyphs and a different stack height. The panel's shape, scrim and
 * animation are tested where they are defined (`PaneMenuDesignTest`).
 */
class ReviewMenuDesignTest {

    @Test
    fun `the ranking menu is exactly as tall as before the shell was shared`() {
        // 6 + 6 padding, a 34 header, 52 + (1 + 10) + 60 + 60 of rows, and 10 + 10 of margin.
        assertEquals(249.dp, MenuBoxHeight)
        assertEquals(MenuBoxHeight, menuBoxHeightFor(RankMenuBodyHeight))
    }

    @Test
    fun `the review menu is three plain rows, a hairline and one row that names a folder`() {
        // Start over, back to folders and the debug toggle; then the hairline; then Discard.
        assertEquals(52.dp * 3 + 11.dp + 60.dp, ReviewMenuBodyHeight)
        // Taller than the ranking menu now the debug row is in; still a compact panel.
        assertTrue(menuBoxHeightFor(ReviewMenuBodyHeight) < 320.dp)
    }

    @Test
    fun `back points left, along one shaft`() {
        val (shaft, head) = glyphStrokes(MenuGlyph.BACK)
        assertEquals(2, shaft.points.size)
        assertTrue("the shaft runs right to left", shaft.points.first().x > shaft.points.last().x)
        assertEquals("the head ends where the shaft does", shaft.points.last(), head.points[1])
        assertFalse(head.closed)
    }

    @Test
    fun `restart is an open arc with a two-barbed head on its end`() {
        val (arc, head) = glyphStrokes(MenuGlyph.RESTART)
        assertFalse("an arc that closed would be a circle, which says nothing", arc.closed)
        assertTrue(arc.points.size > 8)
        assertEquals("the head is on the end of the arc", arc.points.last(), head.points[1])
        assertEquals(3, head.points.size)
    }
}
