package com.rankmaster2.phone.ui.rank

import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.requiredSize
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.systemGestureExclusion
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.draw.rotate
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.lerp
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.drawscope.DrawScope
import androidx.compose.ui.graphics.drawscope.Fill
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.DpSize
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

/**
 * The three things allowed on top of the pair, and the rules they all obey.
 *
 * Nothing here reserves space. The two photographs have the whole screen and these float over it,
 * which is why every one of them has to survive being drawn over a white photograph *and* a black
 * one. "Barely visible" still has to be findable, so each shape is drawn twice: a soft dark halo
 * under a light mark. That costs nothing and removes the only way these can disappear.
 */

/**
 * Emerald and amber, at the weight the owner asked for: faint.
 *
 * These are the desktop's hues, but the desktop draws them on a chrome strip and this draws them on
 * a photograph. At full strength they read as part of the picture; at this weight they are a mark
 * you can consult and otherwise ignore, which is what the strip is for. The halo underneath is what
 * lets them stay this faint and still be findable on a white photograph.
 */
private val Confirmation = Color(0x8C2ECC71)

private val Upset = Color(0x8CF0A020)

private val Halo = Color(0x66000000)

/**
 * The match strip: up to ten dots, oldest first, exactly `snapshot.cues` (§ 9.1).
 *
 * Hidden until the first vote, like the desktop's. That is what lets the screen open completely
 * clean - two photographs and nothing else - and makes the strip's appearance mean something.
 */
@Composable
fun MatchStrip(cues: List<String>, modifier: Modifier = Modifier) {
    if (cues.isEmpty()) return

    Row(
        modifier = modifier
            .clip(RoundedCornerShape(50))
            .background(Color(0x26000000))
            .padding(horizontal = 7.dp, vertical = 4.dp),
        horizontalArrangement = Arrangement.spacedBy(6.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        cues.forEach { cue ->
            val colour = if (cue == "upset") Upset else Confirmation
            Box(
                Modifier
                    .size(8.dp)
                    .drawBehind {
                        drawCircle(Halo, radius = size.minDimension / 2f + 1.5f, center = center)
                        drawCircle(colour, radius = size.minDimension / 2f, center = center)
                    }
            )
        }
    }
}

/**
 * The cancel notch: a tab that the screen edge grows, with the word inside it.
 *
 * The first two attempts at this were a filled rounded rectangle, which reads as a panel stuck onto
 * the edge and - being 30% black - is invisible over a dark photograph and nearly invisible over a
 * bright one. What was asked for, and what this draws, is the sketch: flat along the edge, up,
 * **sloping inward** to a short flat top, and back down.
 *
 * The outline is the control; the word only names it, so the word sits below the outline's weight.
 * Both are drawn twice - a dark halo under a light mark - which is what lets something this faint
 * stay findable on a white photograph and a black one alike.
 *
 * ## Why there is no `rotate` here any more
 *
 * The third attempt drew one horizontal tab and turned it with `rotate(90f)` for the right edge.
 * That was wrong twice over. `rotate` turns the *content* and not the layout box, so the tab kept a
 * 112 x 28 footprint while looking 28 x 112 - and because it turns about the box's centre, a tab
 * aligned to the right edge came to rest 42 dp clear of it. That gap is the "it floats" in the bug
 * report; the window insets it was blamed on are zero here, because the system bars are hidden.
 * The sign was wrong as well: turning content clockwise sends its open base to the **left**, so the
 * tab grew out of the wrong side of itself.
 *
 * So each orientation is drawn in the orientation it is used in, from one description of the shape
 * in [notchOutline] and one mapping onto the canvas in [NotchPoint.onCanvas]. Two short functions
 * with a test each, rather than one path plus a transform that has now been got wrong three times.
 */
@Composable
fun CancelNotch(
    onCancel: () -> Unit,
    enabled: Boolean,
    edge: NotchEdge,
    modifier: Modifier = Modifier,
) {
    val box = notchBoxSize(edge)

    Box(
        modifier = modifier
            .size(width = box.width, height = box.height)
            // The tab sits *on* the edge, and on a gesture-navigation phone the edge belongs to
            // the system. This hands the tab's own rectangle back, so a tap on it is a tap and not
            // the start of a back swipe.
            .systemGestureExclusion()
            .clickableWhen(enabled, onCancel)
            .drawBehind { drawNotch(edge, enabled) },
        contentAlignment = Alignment.Center,
    ) {
        // The word is laid out in the tab's own long-edge box and then turned, which is the one
        // place a rotation is right: a label has no footprint of its own to get wrong.
        Box(
            modifier = Modifier
                .requiredSize(width = NotchBase, height = NotchHeight)
                .rotate(if (edge == NotchEdge.RIGHT) 90f else 0f),
            contentAlignment = Alignment.Center,
        ) {
            Text(
                text = "cancel",
                color = if (enabled) Color(0x8CECEEF2) else Color(0x3DECEEF2),
                fontSize = 12.sp,
                fontWeight = FontWeight.Normal,
                maxLines = 1,
                modifier = Modifier.padding(bottom = 3.dp),
            )
        }
    }
}

/** Which edge the tab grows out of. Portrait puts it on the right, landscape along the bottom. */
enum class NotchEdge { BOTTOM, RIGHT }

/** ~110 dp along the edge, a top ~62% of that, ~26 dp tall. See CLIENT_PLAN.md section 3.6.2a. */
private val NotchBase = 112.dp
private val NotchHeight = 28.dp

/** How far the slopes travel inward, as a fraction of the base: a top of ~62% of the base. */
private const val NotchInsetFraction = 0.19f

/** The eased shoulder. The corners are rounded and the slopes stay straight between them. */
private val NotchShoulder = 9.dp

/**
 * The tab's footprint, which is long **along** the edge it grows from and short across it.
 *
 * This is the half the old `rotate` could not do: the layout box has to be turned as well as the
 * drawing, or the tab occupies a rectangle it is not in.
 */
internal fun notchBoxSize(edge: NotchEdge): DpSize = when (edge) {
    NotchEdge.BOTTOM -> DpSize(width = NotchBase, height = NotchHeight)
    NotchEdge.RIGHT -> DpSize(width = NotchHeight, height = NotchBase)
}

/**
 * A point on the tab in **edge space**: [along] runs down the edge the tab grows from, and [depth]
 * is how far it has risen off that edge. `depth == 0` is on the edge itself.
 *
 * Describing the shape this way is what makes "the base is open and lies on the edge" a property of
 * the description, rather than something a transform has to be trusted to preserve.
 */
internal data class NotchPoint(val along: Float, val depth: Float)

/** One step of the outline. A quadratic where a shoulder is eased, a line everywhere else. */
internal sealed interface NotchSegment {
    val to: NotchPoint

    data class Line(override val to: NotchPoint) : NotchSegment
    data class Curve(val control: NotchPoint, override val to: NotchPoint) : NotchSegment
}

/** The whole outline: where it starts on the edge, and the way back down to it. */
internal data class NotchOutline(val start: NotchPoint, val segments: List<NotchSegment>)

/**
 * The owner's sketch, in numbers: the edge runs flat, rises, slopes inward to a short flat top, and
 * slopes back down, shoulders eased rather than cut square.
 *
 * [alongExtent] is the tab's length on the edge and [depthExtent] how far it stands off it, both in
 * pixels. Nothing here leaves `0..alongExtent` by `0..depthExtent`, which is the box the caller
 * reserved - the property the rotated version quietly broke.
 */
internal fun notchOutline(alongExtent: Float, depthExtent: Float, shoulder: Float): NotchOutline {
    val inset = alongExtent * NotchInsetFraction
    val r = shoulder
    val far = alongExtent - inset

    return NotchOutline(
        start = NotchPoint(0f, 0f),
        segments = listOf(
            // Flat along the edge, out to the foot of the first slope.
            NotchSegment.Line(NotchPoint(inset - r, 0f)),
            // Up the slope and round the shoulder onto the flat top.
            NotchSegment.Curve(
                control = NotchPoint(inset, depthExtent * 0.45f),
                to = NotchPoint(inset + r * 0.4f, depthExtent - r),
            ),
            NotchSegment.Curve(
                control = NotchPoint(inset + r * 0.9f, depthExtent),
                to = NotchPoint(inset + r * 1.6f, depthExtent),
            ),
            // The short flat top.
            NotchSegment.Line(NotchPoint(far - r * 1.6f, depthExtent)),
            // Round the far shoulder and back down.
            NotchSegment.Curve(
                control = NotchPoint(far - r * 0.9f, depthExtent),
                to = NotchPoint(far - r * 0.4f, depthExtent - r),
            ),
            NotchSegment.Curve(
                control = NotchPoint(far, depthExtent * 0.45f),
                to = NotchPoint(far + r, 0f),
            ),
            // And flat along the edge again. The base stays open: the tab rises *out of* the edge.
            NotchSegment.Line(NotchPoint(alongExtent, 0f)),
        ),
    )
}

/**
 * Edge space onto the canvas, and the only place the two orientations differ.
 *
 * `depth` always runs *away* from the edge the tab grows out of - upwards from the bottom edge,
 * leftwards from the right one - so a point at `depth == 0` lands exactly on that edge, whichever
 * edge it is.
 */
internal fun NotchPoint.onCanvas(edge: NotchEdge, width: Float, height: Float): Offset =
    when (edge) {
        NotchEdge.BOTTOM -> Offset(x = along, y = height - depth)
        NotchEdge.RIGHT -> Offset(x = width - depth, y = along)
    }

private fun DrawScope.drawNotch(edge: NotchEdge, enabled: Boolean) {
    val alongExtent = if (edge == NotchEdge.BOTTOM) size.width else size.height
    val depthExtent = if (edge == NotchEdge.BOTTOM) size.height else size.width
    val outline = notchOutline(alongExtent, depthExtent, NotchShoulder.toPx())

    fun NotchPoint.canvas() = onCanvas(edge, size.width, size.height)

    val path = Path().apply {
        val from = outline.start.canvas()
        moveTo(from.x, from.y)
        outline.segments.forEach { segment ->
            val to = segment.to.canvas()
            when (segment) {
                is NotchSegment.Line -> lineTo(to.x, to.y)
                is NotchSegment.Curve -> {
                    val control = segment.control.canvas()
                    quadraticBezierTo(control.x, control.y, to.x, to.y)
                }
            }
        }
    }

    val alpha = if (enabled) 1f else 0.45f

    // A wash inside, barely there, so the word stays legible over a busy photograph.
    drawPath(path, Color(0x1F000000).copy(alpha = 0.12f * alpha), style = Fill)
    // Halo first, then the line: the edge survives either extreme behind it.
    drawPath(path, Color(0x73000000), style = Stroke(width = 3.dp.toPx()))
    drawPath(path, Color(0xFFECEEF2).copy(alpha = 0.40f * alpha), style = Stroke(width = 1.25.dp.toPx()))
}

/** A one-line word from the app, floating clear of both panes. Never covers a photograph's middle. */
@Composable
fun Notice(text: String, modifier: Modifier = Modifier) {
    Box(
        modifier = modifier
            .clip(RoundedCornerShape(50))
            .background(Color(0xB3000000))
            .padding(horizontal = 14.dp, vertical = 7.dp),
    ) {
        Text(text, color = Color(0xE6ECEEF2), fontSize = 13.sp)
    }
}

/**
 * Progress, as a hairline along the bottom that grows out of the centre towards both edges, running
 * pale red at nothing done to pale green at all of it.
 *
 * A percentage invites reading, and reading is not this screen's job — the only question here is
 * which of two pictures is better. A line that reaches the edges says the same thing from the corner
 * of the eye, and says nothing at all when it is not being looked at.
 *
 * It grows from the centre rather than the left because it is not a loading bar: it is how far a
 * folder has come, and the middle is where the eye already is.
 *
 * `progressPercent` is an overlay figure, not a measurement (SERVER_SPEC.md § 9.1) — it is a mean
 * over rankable records and it moves **down** after a discard. So it animates, and it has to look
 * unremarkable going backwards.
 */
@Composable
fun ProgressLine(percent: Int, modifier: Modifier = Modifier) {
    val fraction by animateFloatAsState(
        targetValue = (percent.coerceIn(0, 100)) / 100f,
        animationSpec = tween(durationMillis = 450),
        label = "progress",
    )

    if (fraction <= 0f) return

    // Pale red through to pale green, at the same weight throughout: the colour carries the
    // meaning, never the brightness.
    val colour = lerp(Color(0xFFD26B6B), Color(0xFF6BD28C), fraction).copy(alpha = 0.55f)

    Box(
        modifier = modifier
            .fillMaxWidth()
            .height(2.5.dp)
            .drawBehind {
                val half = size.width * fraction / 2f
                val centre = size.width / 2f
                drawRect(
                    color = colour,
                    topLeft = Offset(centre - half, 0f),
                    size = Size(half * 2f, size.height),
                )
            }
    )
}
