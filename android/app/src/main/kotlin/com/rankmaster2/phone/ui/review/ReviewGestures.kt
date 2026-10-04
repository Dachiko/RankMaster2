package com.rankmaster2.phone.ui.review

import androidx.compose.ui.geometry.Offset
import com.rankmaster2.phone.ui.insideLiveArea
import kotlin.math.min

/**
 * What a touch on the review screen amounts to, decided with plain arithmetic so that it can be
 * tested without a screen.
 *
 * - **Tap** keeps the item (moves on to the next).
 * - **Swipe left** discards it.
 * - Everything else - a swipe right, a vertical drag, anything that did not start inside the live
 *   rectangle - does nothing.
 *
 * "Inside the live rectangle" is the one the ranking screen uses, from the same function and the same
 * number ([com.rankmaster2.phone.ui.VotingShareOfScreen]): the accidental touches come from the thumb
 * meeting the edge of the glass, and a keep or a discard is not something to guess at there.
 */
internal enum class ReviewAction { KEEP, DISCARD, NONE }

/** A tap that landed at [down] on a screen [width] x [height]. */
internal fun tapAction(down: Offset, width: Float, height: Float): ReviewAction =
    if (insideLiveArea(down, width, height)) ReviewAction.KEEP else ReviewAction.NONE

/**
 * A drag that began at [down] and was let go with the item [dragX] to the side and moving at
 * [velocityX] (both negative to the left).
 */
internal fun swipeAction(
    down: Offset,
    dragX: Float,
    velocityX: Float,
    width: Float,
    height: Float,
    flingPx: Float,
): ReviewAction = when {
    !insideLiveArea(down, width, height) -> ReviewAction.NONE
    decidesDiscard(dragX, velocityX, width, flingPx) -> ReviewAction.DISCARD
    else -> ReviewAction.NONE
}

/**
 * Whether a drag let go at [dragX] / [velocityX] commits: to the left, and either past
 * [COMMIT_FRACTION] of the [width] or a flick faster than [flingPx] that is not heading back.
 * Nothing to the right commits - it springs back.
 */
internal fun decidesDiscard(dragX: Float, velocityX: Float, width: Float, flingPx: Float): Boolean = when {
    width > 0f && dragX <= -width * COMMIT_FRACTION -> true
    velocityX <= -flingPx && dragX <= 0f -> true
    else -> false
}

/**
 * Where the item sits for a finger that is [totalDx] from where it went down. Only leftwards: the
 * item does not follow the finger to the right. The touch slop is taken off, so the item starts
 * moving from rest rather than jumping by the slop when the drag is recognised.
 */
internal fun dragOffset(totalDx: Float, slop: Float): Float = min(0f, totalDx + slop)

/** Let go past this share of the width and the discard commits. */
internal const val COMMIT_FRACTION = 0.30f

/** A flick faster than this, in dp per second, commits regardless of distance. */
internal const val FLING_DP = 900

/** The tilt at a full width of drag, for a still. */
internal const val MAX_TILT_DEGREES = 8f
