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
import androidx.compose.foundation.layout.size
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
 * the edge and — being 30% black — is invisible over a dark photograph and nearly invisible over a
 * bright one. What was asked for, and what this draws, is the sketch: flat along the edge, up,
 * **sloping inward** to a short flat top, and back down.
 *
 * The outline is the control; the word only names it, so the word sits below the outline's weight.
 * Both are drawn twice — a dark halo under a light mark — which is what lets something this faint
 * stay findable on a white photograph and a black one alike.
 */
@Composable
fun CancelNotch(
    onCancel: () -> Unit,
    enabled: Boolean,
    edge: NotchEdge,
    modifier: Modifier = Modifier,
) {
    val rotation = if (edge == NotchEdge.RIGHT) 90f else 0f

    Box(
        modifier = modifier
            .rotate(rotation)
            .size(width = NotchBase, height = NotchHeight)
            .clickableWhen(enabled, onCancel)
            .drawBehind { drawNotch(enabled) },
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

enum class NotchEdge { BOTTOM, RIGHT }

/** ~110 dp along the edge, a top ~62% of that, ~26 dp tall. See CLIENT_PLAN.md § 3.6.2a. */
private val NotchBase = 112.dp
private val NotchHeight = 28.dp

private fun DrawScope.drawNotch(enabled: Boolean) {
    val w = size.width
    val h = size.height
    val inset = w * 0.19f          // how far each slope travels inward: top ≈ 62% of the base
    val radius = 9.dp.toPx()

    val path = Path().apply {
        // Left foot, on the edge the tab grows out of.
        moveTo(0f, h)
        lineTo(inset - radius, h - (h - radius) * 0f)
        // Up the left slope, shoulders eased rather than cut square.
        quadraticBezierTo(inset, h * 0.55f, inset + radius * 0.4f, radius)
        quadraticBezierTo(inset + radius * 0.9f, 0f, inset + radius * 1.6f, 0f)
        lineTo(w - inset - radius * 1.6f, 0f)
        quadraticBezierTo(w - inset - radius * 0.9f, 0f, w - inset - radius * 0.4f, radius)
        quadraticBezierTo(w - inset, h * 0.55f, w - inset + radius, h)
        lineTo(w, h)
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
