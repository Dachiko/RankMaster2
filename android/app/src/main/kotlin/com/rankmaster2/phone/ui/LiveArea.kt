package com.rankmaster2.phone.ui

import androidx.compose.ui.geometry.Offset

/**
 * The share of the screen on which a tap or a swipe counts: **thirty per cent**.
 *
 * The owner kept voting by accident. A wrong vote is the one mistake the ranking screen can make
 * that costs anything - it moves a rating nobody asked to move, and cancel costs a round trip and
 * his attention - so the target is deliberately much smaller than the picture it belongs to. The
 * rest of the screen still *shows* the photograph; it just does not answer a touch. Review mode
 * has the same thumb and the same edges, so it uses the same number.
 *
 * Expressed as the share of the glass, because that is how it was asked for and how it will be
 * adjusted. One number, one place, for both screens: if thirty per cent is still too much, this
 * line moves and nothing else. It was half before, and half was still catching his thumb.
 */
internal const val VotingShareOfScreen = 0.30f

/**
 * The live box is **one rectangle centred on the screen**, inset from the four outside edges - not
 * one box per pane.
 *
 * His words, 2026-09-17: *"the active area is a rectangle inside the screen, with paddings from the
 * edges. It's ok it covers the seam between a pair, the false voting is produced on the edges, not
 * in the center of the screen."* That is a measurement, not a preference: the accidental taps come
 * from the thumb meeting the edge of the glass, and nothing was ever mis-hit in the middle.
 *
 * Equal inset on both axes, hence the square root.
 */
internal fun liveFractionPerAxis(share: Float): Float =
    kotlin.math.sqrt(share.coerceIn(0.01f, 1f))

/**
 * Whether a finger that went down at [at] on a screen [width] x [height] is inside the centred live
 * rectangle - the only place a tap or a swipe may start.
 *
 * Plain arithmetic, no screen needed, so the rule can be tested and both screens ask the same thing.
 */
internal fun insideLiveArea(
    at: Offset,
    width: Float,
    height: Float,
    share: Float = VotingShareOfScreen,
): Boolean {
    val live = liveFractionPerAxis(share)
    val marginX = width * (1f - live) / 2f
    val marginY = height * (1f - live) / 2f
    return at.x >= marginX && at.x <= width - marginX &&
        at.y >= marginY && at.y <= height - marginY
}
