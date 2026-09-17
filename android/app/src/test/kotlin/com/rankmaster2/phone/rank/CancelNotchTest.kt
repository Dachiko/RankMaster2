package com.rankmaster2.phone.rank

import com.rankmaster2.phone.ui.rank.NotchEdge
import com.rankmaster2.phone.ui.rank.NotchPoint
import com.rankmaster2.phone.ui.rank.NotchSegment
import com.rankmaster2.phone.ui.rank.notchBoxSize
import com.rankmaster2.phone.ui.rank.notchOutline
import com.rankmaster2.phone.ui.rank.onCanvas
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The cancel tab, which has now been got wrong three times and is therefore worth pinning down.
 *
 * Two things were wrong on the owner's phone and both are geometry, not taste: the tab floated
 * clear of the right edge instead of growing out of it, and it faced the wrong way. Both came from
 * drawing one horizontal tab and turning it with `rotate(90f)` - which turns the drawing and not
 * the layout box, and turns the open base to the left rather than the right.
 *
 * These are the two facts a transform could not be trusted to keep, asserted directly.
 */
class CancelNotchTest {

    // A tab of 112 x 28 dp. The numbers here are pixels; nothing in the shape cares which.
    private val along = 112f
    private val depth = 28f
    private val shoulder = 9f

    // -- the footprint ------------------------------------------------------------------------

    @Test
    fun `the box is long along the edge it grows from, in both orientations`() {
        val bottom = notchBoxSize(NotchEdge.BOTTOM)
        val right = notchBoxSize(NotchEdge.RIGHT)

        // Landscape: the tab runs along the bottom, so it is wide and short.
        assertTrue("${bottom.width} should be the long side", bottom.width > bottom.height)
        // Portrait: it runs down the right edge, so it is narrow and tall. This is the half the
        // old `rotate` could not do - it left a 112 x 28 footprint under a 28 x 112 drawing, so
        // the tab was never where it looked.
        assertTrue("${right.height} should be the long side", right.height > right.width)

        assertEquals(bottom.width, right.height)
        assertEquals(bottom.height, right.width)
    }

    // -- which way it faces -------------------------------------------------------------------

    @Test
    fun `the base lies on the bottom edge when it grows from the bottom`() {
        val outline = notchOutline(along, depth, shoulder)
        val width = along
        val height = depth

        val start = outline.start.onCanvas(NotchEdge.BOTTOM, width, height)
        val end = outline.segments.last().to.onCanvas(NotchEdge.BOTTOM, width, height)

        assertEquals(0f, start.x, 0.001f)
        assertEquals(height, start.y, 0.001f)
        assertEquals(width, end.x, 0.001f)
        assertEquals(height, end.y, 0.001f)
    }

    @Test
    fun `the base lies on the right edge when it grows from the right`() {
        val outline = notchOutline(along, depth, shoulder)
        // Swapped, because the box is swapped.
        val width = depth
        val height = along

        val start = outline.start.onCanvas(NotchEdge.RIGHT, width, height)
        val end = outline.segments.last().to.onCanvas(NotchEdge.RIGHT, width, height)

        // x == width is the right edge. The old version put the base on the left of the tab while
        // the tab sat on the right of the screen, which is exactly "facing the wrong way".
        assertEquals(width, start.x, 0.001f)
        assertEquals(0f, start.y, 0.001f)
        assertEquals(width, end.x, 0.001f)
        assertEquals(height, end.y, 0.001f)
    }

    @Test
    fun `the tab rises inward from the edge it sits on`() {
        val outline = notchOutline(along, depth, shoulder)
        val top = outline.segments.mapNotNull { it.to.takeIf { p -> p.depth == depth } }
        assertTrue("the flat top should exist", top.isNotEmpty())

        // From the bottom edge, "away" is upwards: a smaller y.
        val fromBottom = top.first().onCanvas(NotchEdge.BOTTOM, along, depth)
        assertEquals(0f, fromBottom.y, 0.001f)

        // From the right edge, "away" is leftwards: a smaller x. This is the sign that was wrong.
        val fromRight = top.first().onCanvas(NotchEdge.RIGHT, depth, along)
        assertEquals(0f, fromRight.x, 0.001f)
    }

    // -- the shape itself ---------------------------------------------------------------------

    @Test
    fun `nothing leaves the box the layout reserved`() {
        val outline = notchOutline(along, depth, shoulder)
        val points = buildList {
            add(outline.start)
            outline.segments.forEach { segment ->
                if (segment is NotchSegment.Curve) add(segment.control)
                add(segment.to)
            }
        }

        points.forEach { point ->
            assertTrue("along ${point.along} is outside 0..$along", point.along in 0f..along)
            assertTrue("depth ${point.depth} is outside 0..$depth", point.depth in 0f..depth)
        }
    }

    @Test
    fun `the top is about 62 percent of the base, so the slope reads as a slope (design guard)`() {
        val outline = notchOutline(along, depth, shoulder)
        val controls = outline.segments.filterIsInstance<NotchSegment.Curve>().map(NotchSegment.Curve::control)

        // The first and last curves are the two slopes, and their control points sit on them. The
        // span between is the trapezoid's top before the shoulders are eased into it - which is
        // the figure CLIENT_PLAN.md section 3.6.2a names: obvious, rather than a hint.
        val top = controls.last().along - controls.first().along
        assertEquals(0.62f, top / along, 0.01f)

        // And the flat between the eased shoulders is a real flat rather than a point, which is
        // what keeps this a notch instead of a triangle.
        val flat = outline.segments.map { it.to }.filter { it.depth == depth }
        val flatWidth = flat.maxOf(NotchPoint::along) - flat.minOf(NotchPoint::along)
        assertTrue("the flat top is ${flatWidth / along} of the base", flatWidth / along > 0.25f)
    }

    @Test
    fun `the flat runs on the edge are what the shape starts and ends with`() {
        val outline = notchOutline(along, depth, shoulder)
        assertTrue(outline.segments.first() is NotchSegment.Line)
        assertTrue(outline.segments.last() is NotchSegment.Line)
        assertEquals(0f, outline.segments.first().to.depth, 0.001f)
        assertEquals(0f, outline.segments.last().to.depth, 0.001f)
    }
}
