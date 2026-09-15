package com.rankmaster2.phone.rank

import androidx.compose.ui.unit.IntSize
import com.rankmaster2.phone.ui.rank.ThumbPositionProvider
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Where the long-press menu opens, and why it is not simply "where the thumb was".
 *
 * Compose's alignment-plus-offset popup does not clamp: asked for a position near the bottom of the
 * window it returns exactly that, and the part of the menu past the edge is not drawn anywhere the
 * owner can see or reach. A press in the bottom corner of the lower pane put most of the menu off
 * the glass - and the edge margin makes that corner a *more* likely place to press, because a long
 * press is now the only thing that does anything there.
 */
class PaneMenuPositionTest {

    private val window = IntSize(width = 1080, height = 2400)
    private val menu = IntSize(width = 420, height = 260)

    private fun at(x: Int, y: Int) =
        ThumbPositionProvider.positionAt(x, y, window, menu)

    private fun fitsOnScreen(x: Int, y: Int): Boolean {
        val p = at(x, y)
        return p.x >= 0 && p.y >= 0 &&
            p.x + menu.width <= window.width &&
            p.y + menu.height <= window.height
    }

    @Test
    fun `in open space it opens exactly where the thumb landed`() {
        val p = at(300, 800)
        assertEquals(300, p.x)
        assertEquals(800, p.y)
    }

    @Test
    fun `a press near the right edge is pushed back onto the screen`() {
        val p = at(window.width - 20, 800)
        assertEquals(window.width - menu.width, p.x)
        assertTrue(fitsOnScreen(window.width - 20, 800))
    }

    @Test
    fun `a press near the bottom opens upwards rather than off the bottom`() {
        val p = at(300, window.height - 20)
        assertTrue("the menu should sit above the press", p.y < window.height - 20)
        assertTrue(fitsOnScreen(300, window.height - 20))
    }

    @Test
    fun `every corner of the screen produces a menu that is entirely on it`() {
        listOf(
            0 to 0,
            window.width to 0,
            0 to window.height,
            window.width to window.height,
        ).forEach { (x, y) ->
            assertTrue("a press at $x,$y hangs off the screen", fitsOnScreen(x, y))
        }
    }

    @Test
    fun `a menu taller than the window is pinned to the top rather than pushed off it`() {
        // Degenerate, but the alternative is a negative offset and a menu whose first line - the
        // file name it is about - is the part that is not on screen.
        val tall = IntSize(width = 420, height = 3000)
        val p = ThumbPositionProvider.positionAt(300, 2000, window, tall)
        assertEquals(0, p.y)
    }
}
