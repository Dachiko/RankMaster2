package com.rankmaster2.phone.ui.rank

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.draw.rotate
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
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
 * The cancel notch, on the bottom edge: a tab with the word in it, rising out of the screen.
 *
 * Pushed off-centre because the middle of the bottom edge belongs to Android's home gesture, and a
 * tab underneath it is a tab that cannot be pressed.
 */
@Composable
fun CancelNotchBottom(onCancel: () -> Unit, enabled: Boolean, modifier: Modifier = Modifier) {
    Box(
        modifier = modifier
            .clip(RoundedCornerShape(topStart = 14.dp, topEnd = 14.dp))
            .background(Color(0x4D000000))
            .clickableWhen(enabled, onCancel)
            .padding(horizontal = 20.dp, vertical = 7.dp),
        contentAlignment = Alignment.Center,
    ) {
        CancelWord(enabled)
    }
}

/**
 * The same tab on the right edge, for portrait, where the thumb already is and where nothing else
 * lives. The word turns with the edge.
 */
@Composable
fun CancelNotchRight(onCancel: () -> Unit, enabled: Boolean, modifier: Modifier = Modifier) {
    Box(
        modifier = modifier
            .clip(RoundedCornerShape(topStart = 14.dp, bottomStart = 14.dp))
            .background(Color(0x4D000000))
            .clickableWhen(enabled, onCancel)
            .padding(horizontal = 7.dp, vertical = 20.dp),
        contentAlignment = Alignment.Center,
    ) {
        Box(Modifier.rotate(90f)) { CancelWord(enabled) }
    }
}

@Composable
private fun CancelWord(enabled: Boolean) {
    Text(
        text = "cancel",
        color = if (enabled) Color(0xB3ECEEF2) else Color(0x4DECEEF2),
        fontSize = 13.sp,
        fontWeight = FontWeight.Medium,
        maxLines = 1,
    )
}

/**
 * The overflow: three dots, barely there, in a corner. Everything that is about the session rather
 * than about one photograph lives behind it.
 */
@Composable
fun OverflowDots(onOpen: () -> Unit, modifier: Modifier = Modifier) {
    Box(
        modifier = modifier
            .clip(CircleShape)
            .clickableWhen(true, onOpen)
            .padding(horizontal = 12.dp, vertical = 8.dp),
        contentAlignment = Alignment.Center,
    ) {
        Box(
            Modifier.size(width = 4.dp, height = 18.dp).drawBehind {
                val x = size.width / 2f
                val gap = size.height / 3f
                listOf(gap * 0.5f, gap * 1.5f, gap * 2.5f).forEach { y ->
                    drawCircle(Halo, radius = 2.8f, center = Offset(x, y))
                    drawCircle(Color(0xB3ECEEF2), radius = 1.9f, center = Offset(x, y))
                }
            }
        )
    }
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
