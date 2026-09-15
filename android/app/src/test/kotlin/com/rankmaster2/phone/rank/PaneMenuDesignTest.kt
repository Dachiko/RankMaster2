package com.rankmaster2.phone.rank

import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import com.rankmaster2.phone.ui.rank.FileNameFieldWidth
import com.rankmaster2.phone.ui.rank.FileNameMaxChars
import com.rankmaster2.phone.ui.rank.FileNameSize
import com.rankmaster2.phone.ui.rank.MenuGlyph
import com.rankmaster2.phone.ui.rank.MenuBoxHeight
import com.rankmaster2.phone.ui.rank.MenuBoxWidth
import com.rankmaster2.phone.ui.rank.MenuHeight
import com.rankmaster2.phone.ui.rank.MenuWidth
import com.rankmaster2.phone.ui.rank.MonoAdvanceRatio
import com.rankmaster2.phone.ui.rank.RowHeight
import com.rankmaster2.phone.ui.rank.RowHeightWithDetail
import com.rankmaster2.phone.ui.rank.ScrimPeak
import com.rankmaster2.phone.ui.rank.ScrimStops
import com.rankmaster2.phone.ui.rank.ThumbPositionProvider
import com.rankmaster2.phone.ui.rank.fileNameParts
import com.rankmaster2.phone.ui.rank.glyphStrokes
import com.rankmaster2.phone.ui.rank.looksDisabled
import com.rankmaster2.phone.ui.rank.menuTransformOrigin
import com.rankmaster2.phone.ui.rank.middleTruncate
import com.rankmaster2.phone.ui.rank.scrimFalloff
import com.rankmaster2.phone.ui.rank.splitFileName
import com.rankmaster2.phone.ui.rank.starOutline
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import kotlin.math.hypot

/**
 * The long-press menu, in numbers.
 *
 * Nothing here can be looked at: there is no device, no emulator and no screenshot, so every
 * decision that could be checked by arithmetic is checked by arithmetic instead. That is the same
 * bargain `SeamTest`, `EdgeMarginTest` and `CancelNotchTest` make - the geometry is written down as
 * a function of named numbers, and the test asserts what those numbers add up to on a real phone.
 *
 * What this file deliberately does **not** claim is that the result is handsome.
 */
class PaneMenuDesignTest {

    // The glass, in pixels, on a fairly ordinary portrait phone at 3x.
    private val window = IntSize(width = 1080, height = 2400)
    private val menu = IntSize(width = 800, height = 700)

    // -- where it grows from --------------------------------------------------------------------

    @Test
    fun `in open space the menu grows out of its own top-left, which is the press`() {
        // The provider puts the corner on the press point, so the corner *is* the press point and
        // the scale-up starts there. "Open space" is narrower than it sounds: the panel is most of
        // the width of a phone, so only the left third of the glass leaves the menu unclamped.
        val free = window.width - menu.width
        assertTrue("there is no unclamped press point at all", free > 0)
        val origin = menuTransformOrigin(IntOffset(free / 2, 800), window, menu)
        assertEquals(0f, origin.pivotFractionX, 0.0001f)
        assertEquals(0f, origin.pivotFractionY, 0.0001f)
    }

    @Test
    fun `when a press near the right edge shoves the menu back, the origin slides with it`() {
        val press = IntOffset(window.width - 40, 800)
        val placed = ThumbPositionProvider.positionAt(press.x, press.y, window, menu)
        val origin = menuTransformOrigin(press, window, menu)

        // The menu has been pushed left to stay on the glass; the origin has to travel exactly as
        // far, or the animation starts somewhere the finger never was.
        assertTrue("the menu should have been clamped", placed.x < press.x)
        assertEquals(
            (press.x - placed.x).toFloat() / menu.width,
            origin.pivotFractionX,
            0.0001f,
        )
    }

    @Test
    fun `a press near the bottom flips the menu above it and the origin lands on its bottom edge`() {
        val origin = menuTransformOrigin(IntOffset(300, window.height - 10), window, menu)
        assertEquals(1f, origin.pivotFractionY, 0.0001f)
    }

    @Test
    fun `every point on the glass gives an origin inside the menu`() {
        for (x in 0..window.width step 60) {
            for (y in 0..window.height step 120) {
                val origin = menuTransformOrigin(IntOffset(x, y), window, menu)
                assertTrue(
                    "origin off the menu at $x,$y",
                    origin.pivotFractionX in 0f..1f && origin.pivotFractionY in 0f..1f,
                )
            }
        }
    }

    @Test
    fun `a menu with no size falls back to the middle rather than dividing by zero`() {
        val origin = menuTransformOrigin(IntOffset(300, 800), window, IntSize.Zero)
        assertEquals(0.5f, origin.pivotFractionX, 0.0001f)
        assertEquals(0.5f, origin.pivotFractionY, 0.0001f)
    }

    // -- the panel on a real phone ----------------------------------------------------------------

    @Test
    fun `the panel leaves the photograph visible around it on the narrowest phone`() {
        // 320 dp is the narrowest Android screen anyone still ships. The menu is a thing *on* a
        // picture, not a dialogue that takes the screen, so it has to leave a margin even there.
        val narrowest = 320.dp
        assertTrue(
            "the menu box is $MenuBoxWidth wide on a $narrowest screen",
            MenuBoxWidth <= narrowest - 16.dp,
        )
    }

    @Test
    fun `the panel never touches the edge of the glass, wherever the press pushed it`() {
        // The positioner clamps hard against the window, which is what keeps the menu on screen -
        // and, on a phone barely wider than the panel, is what almost every press does. The margin
        // has to be inside the thing being clamped, or it is not there when it is needed.
        val gap = (MenuBoxWidth - MenuWidth) / 2
        assertTrue("there is no margin at all", gap > 0.dp)
        assertEquals(MenuBoxHeight - MenuHeight, gap * 2)
    }

    @Test
    fun `the menu is short enough that there is always somewhere to put it`() {
        // It opens under the thumb and flips above the press when there is no room below, so it
        // only ever needs half the screen minus wherever the thumb was. Under half of a small
        // phone's 640 dp leaves that always true.
        assertTrue("the menu box is $MenuBoxHeight tall", MenuBoxHeight <= 288.dp)
    }

    @Test
    fun `every row is a real touch target`() {
        assertTrue("single-line row is $RowHeight", RowHeight >= 48.dp)
        assertTrue("two-line row is $RowHeightWithDetail", RowHeightWithDetail >= 48.dp)
    }

    @Test
    fun `a truncated file name fits the width it is given`() {
        // Monospace advances about 0.6 em, which is the one figure here that is an estimate rather
        // than arithmetic - hence the slack. At the default font scale 1 sp is 1 dp.
        val widest = FileNameMaxChars * MonoAdvanceRatio * FileNameSize.value
        assertTrue(
            "$FileNameMaxChars monospace characters is ${widest}dp in ${FileNameFieldWidth}",
            widest <= FileNameFieldWidth.value,
        )
    }

    // -- the file name ----------------------------------------------------------------------------

    @Test
    fun `a name that fits is left exactly alone`() {
        assertEquals("DSC_0123", middleTruncate("DSC_0123", FileNameMaxChars))
    }

    @Test
    fun `a long name loses its middle and keeps both ends`() {
        val long = "IMG_20240513_184212_portrait_backlit_v2"
        val shown = middleTruncate(long, FileNameMaxChars)

        assertEquals(FileNameMaxChars, shown.length)
        assertTrue(shown.contains("…"))
        assertTrue("the camera's prefix is gone", shown.startsWith("IMG_2024"))
        // The end is what tells two files in a folder apart, so the end is what survives.
        assertTrue("the tail is gone", long.endsWith(shown.substringAfter("…")))
    }

    @Test
    fun `truncating to nothing does not crash or lie about the length`() {
        assertEquals("…", middleTruncate("anything at all", 1))
        assertEquals("a", middleTruncate("a", 1))
    }

    @Test
    fun `what is drawn always fits the budget, extension included`() {
        // The header is one line of monospace and the extension sits at the end of it, so the
        // extension has to be paid for out of the same width - not added to it.
        listOf(
            "DSC_0123.jpg",
            "IMG_20240513_184212_portrait_backlit_version_two.jpeg",
            "a_name_with_no_extension_at_all_that_runs_and_runs",
            "",
        ).forEach { id ->
            val (stem, extension) = fileNameParts(id)
            assertTrue(
                "\"$id\" draws as ${stem.length + extension.length} characters",
                stem.length + extension.length <= FileNameMaxChars,
            )
        }
    }

    @Test
    fun `the extension survives whatever happens to the name`() {
        val long = "IMG_20240513_184212_portrait_backlit_version_two.jpeg"
        assertEquals(".jpeg", fileNameParts(long).second)
        assertTrue(fileNameParts(long).first.contains("…"))
    }

    @Test
    fun `the extension is split off, and it is the part that is never cut`() {
        assertEquals("DSC_0123" to ".jpg", splitFileName("DSC_0123.jpg"))
        assertEquals("clip" to ".mp4", splitFileName("clip.mp4"))
    }

    @Test
    fun `a name with no extension keeps all of itself`() {
        assertEquals("README" to "", splitFileName("README"))
        // A leading dot is the whole name, not an extension with an empty stem.
        assertEquals(".hidden" to "", splitFileName(".hidden"))
        assertEquals("trailing." to "", splitFileName("trailing."))
    }

    @Test
    fun `anything folder-shaped is dropped rather than drawn`() {
        // The server refuses ids with separators (media_outside_session); the menu is not where
        // the owner should be finding that out, and a path would not fit the header anyway.
        assertEquals("DSC_0123" to ".jpg", splitFileName("holiday/DSC_0123.jpg"))
        assertEquals("DSC_0123" to ".jpg", splitFileName("""C:\photos\DSC_0123.jpg"""))
    }

    // -- disabled ---------------------------------------------------------------------------------

    @Test
    fun `an enabled row never looks disabled, however long it has been enabled`() {
        assertFalse(looksDisabled(enabled = true, settled = false))
        assertFalse(looksDisabled(enabled = true, settled = true))
    }

    @Test
    fun `a row disabled for a moment does not say so`() {
        // `actionable` goes false for the length of one round trip to the PC. Painting that grey
        // and back is a blink, and a blink reads as a fault.
        assertFalse(looksDisabled(enabled = false, settled = false))
    }

    @Test
    fun `a row disabled for longer than a round trip admits it`() {
        assertTrue(looksDisabled(enabled = false, settled = true))
    }

    // -- the ground -------------------------------------------------------------------------------

    @Test
    fun `the scrim is darkest under the thumb and gone at the edge of its pool`() {
        assertEquals(1f, scrimFalloff(0f), 0.0001f)
        assertEquals(0f, scrimFalloff(1f), 0.0001f)
        assertEquals(0f, scrimFalloff(4f), 0.0001f)
        assertEquals(1f, scrimFalloff(-1f), 0.0001f)
    }

    @Test
    fun `it only ever gets lighter on the way out`() {
        val samples = ScrimStops.map(::scrimFalloff)
        samples.zipWithNext { nearer, further ->
            assertTrue("the scrim brightens then darkens again: $samples", nearer > further)
        }
    }

    @Test
    fun `the other photograph survives the scrim`() {
        // A long press in the middle of the top pane. The whole argument for anchoring this menu
        // to the press - rather than labelling it "top picture" - is that the owner can see which
        // picture he is acting on, so the far one must not go black.
        val press = Offset(window.width / 2f, window.height * 0.25f)
        val farCorner = Offset(window.width.toFloat(), window.height.toFloat())
        val radius = maxOf(window.width, window.height) * 0.85f

        val alpha = ScrimPeak * scrimFalloff(hypot(farCorner.x - press.x, farCorner.y - press.y) / radius)
        assertTrue("the far corner is ${alpha} dark", alpha < 0.15f)
    }

    @Test
    fun `and the menu's own ground is dark enough to sit on`() {
        // The panel's bottom edge is about 240 dp - roughly 700 px at 3x - from the press.
        val radius = maxOf(window.width, window.height) * 0.85f
        val alpha = ScrimPeak * scrimFalloff(700f / radius)
        assertTrue("only ${alpha} dark under the menu", alpha > 0.4f)
    }

    // -- the glyphs -------------------------------------------------------------------------------

    @Test
    fun `every glyph stays inside its icon box`() {
        MenuGlyph.entries.forEach { glyph ->
            glyphStrokes(glyph).flatMap { it.points }.forEach { point ->
                assertTrue(
                    "$glyph leaves the box at $point",
                    point.x in 0f..1f && point.y in 0f..1f,
                )
            }
        }
    }

    @Test
    fun `no glyph is a single point or an empty path`() {
        MenuGlyph.entries.forEach { glyph ->
            val strokes = glyphStrokes(glyph)
            assertTrue("$glyph has no strokes", strokes.isNotEmpty())
            strokes.forEach {
                assertTrue("$glyph has a stroke of ${it.points.size} point(s)", it.points.size >= 2)
            }
        }
    }

    @Test
    fun `view is four corner brackets and nothing else`() {
        val strokes = glyphStrokes(MenuGlyph.VIEW)
        assertEquals(4, strokes.size)
        strokes.forEach { assertEquals(3, it.points.size) }
        assertTrue("a corner bracket should not close", strokes.none { it.closed })
    }

    @Test
    fun `discard drops into an open tray rather than closing a lid on it`() {
        // The file is *moved* to discarded/, not deleted. A bin with a lid would be a lie about
        // what the row does, and undo would have nothing to open.
        val tray = glyphStrokes(MenuGlyph.DISCARD).last()
        assertFalse("the tray is open at the top", tray.closed)
        assertEquals(4, tray.points.size)
        // Both uprights end level, above the floor: an open-topped box.
        assertEquals(tray.points.first().y, tray.points.last().y, 0.0001f)
        assertTrue(tray.points.first().y < tray.points[1].y)
    }

    @Test
    fun `the star has ten points, alternating, and it points up`() {
        val star = glyphStrokes(MenuGlyph.SPECIAL).single()
        assertTrue("a star has to close", star.closed)
        assertEquals(10, star.points.size)

        val top = star.points.minByOrNull { it.y }!!
        assertEquals("the first point is the top point", star.points.first(), top)
        assertEquals("and it is on the centre line", 0.5f, top.x, 0.0001f)
    }

    @Test
    fun `the star's points alternate between the two radii`() {
        val centre = Offset(0.5f, 0.5f)
        val star = starOutline(centre = centre, outer = 0.4f, inner = 0.18f)

        star.forEachIndexed { index, point ->
            val radius = hypot(point.x - centre.x, point.y - centre.y)
            assertEquals(if (index % 2 == 0) 0.4f else 0.18f, radius, 0.0001f)
        }
    }

    @Test
    fun `a star is symmetric about its own centre line`() {
        val centre = Offset(0.5f, 0.5f)
        val star = starOutline(centre = centre, outer = 0.4f, inner = 0.18f)
        // Point 1 and point 9 are the two inner points either side of the top spike.
        assertEquals(star[1].y, star[9].y, 0.0001f)
        assertEquals(centre.x - (star[9].x - centre.x), star[1].x, 0.0001f)
    }
}
