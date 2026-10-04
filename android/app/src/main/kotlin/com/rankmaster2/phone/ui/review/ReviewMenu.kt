package com.rankmaster2.phone.ui.review

import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.systemGestureExclusion
import androidx.compose.material3.ripple
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import com.rankmaster2.phone.ui.rank.Amber
import com.rankmaster2.phone.ui.rank.HairlineBlockHeight
import com.rankmaster2.phone.ui.rank.Hairline
import com.rankmaster2.phone.ui.rank.Ink
import com.rankmaster2.phone.ui.rank.MenuAction
import com.rankmaster2.phone.ui.rank.MenuGlyph
import com.rankmaster2.phone.ui.rank.PaneMenuShell
import com.rankmaster2.phone.ui.rank.RowHeight
import com.rankmaster2.phone.ui.rank.RowHeightWithDetail

/**
 * The review menu: the ranking screen's long-press menu - same panel, rows, glyphs, scrim and
 * growth from the press - with review's own rows.
 *
 * The rows that only move around the folder come first and the one that moves a file comes last,
 * under the hairline, as in ranking: the panel opens with its first row under the thumb, and the
 * first row is the one a stray release must not be able to hurt.
 */
@Composable
internal fun ReviewMenu(
    itemId: String,
    at: Offset,
    windowSize: IntSize,
    canDiscard: Boolean,
    onDismiss: () -> Unit,
    onRestart: () -> Unit,
    onLeave: () -> Unit,
    onDiscard: () -> Unit,
    onDebug: () -> Unit = {},
) {
    PaneMenuShell(
        key = itemId,
        header = itemId,
        at = at,
        windowSize = windowSize,
        bodyHeight = ReviewMenuBodyHeight,
        onDismiss = onDismiss,
    ) {
        MenuAction(
            label = "Start from the beginning",
            glyph = MenuGlyph.RESTART,
            accent = Ink,
            enabled = true,
            onClick = onRestart,
        )
        MenuAction(
            label = "Back to folders",
            glyph = MenuGlyph.BACK,
            accent = Ink,
            enabled = true,
            onClick = onLeave,
        )

        MenuAction(
            label = "Debug info",
            glyph = MenuGlyph.DEBUG,
            accent = Ink,
            enabled = true,
            onClick = {
                onDebug()
                onDismiss()
            },
        )

        Hairline()

        MenuAction(
            label = "Discard",
            detail = "discarded/",
            glyph = MenuGlyph.DISCARD,
            accent = Amber,
            enabled = canDiscard,
            onClick = onDiscard,
        )
    }
}

/** The rows the review menu stacks: three plain ones (the last is the debug toggle), the hairline, one that names a folder. */
internal val ReviewMenuBodyHeight: Dp = RowHeight * 3 + HairlineBlockHeight + RowHeightWithDetail

/**
 * The three dots that open the menu: a small, quiet mark in a corner, drawn the way the cancel
 * notch is - a dark halo under a light mark, so it survives a white photograph and a black one.
 *
 * The touch target is much larger than the mark. It sits clear of the screen edge but, being near
 * one, hands its rectangle back to the system's gesture navigation, as the notch does.
 */
@Composable
internal fun MenuDots(onClick: () -> Unit, modifier: Modifier = Modifier) {
    Box(
        modifier
            .size(DotsTarget)
            .systemGestureExclusion()
            .clickable(
                interactionSource = remember { MutableInteractionSource() },
                indication = ripple(bounded = false, radius = DotsTarget / 2, color = Color.White),
                onClick = onClick,
            )
            .drawBehind {
                val radius = DotRadius.toPx()
                val gap = DotGap.toPx()
                val x = size.width / 2f
                for (i in -1..1) {
                    val c = Offset(x, size.height / 2f + i * gap)
                    drawCircle(Color(0x73000000), radius = radius + 1.5.dp.toPx(), center = c)
                    drawCircle(Color(0xFFECEEF2).copy(alpha = 0.55f), radius = radius, center = c)
                }
            },
    )
}

private val DotsTarget = 48.dp
private val DotRadius = 2.dp
private val DotGap = 7.dp
