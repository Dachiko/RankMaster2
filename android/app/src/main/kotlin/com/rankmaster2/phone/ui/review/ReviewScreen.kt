package com.rankmaster2.phone.ui.review

import androidx.compose.animation.core.Spring
import androidx.compose.animation.core.animate
import androidx.compose.animation.core.spring
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawing
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.systemGestureExclusion
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.Stable
import androidx.compose.runtime.State
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clipToBounds
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.PointerInputChange
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.input.pointer.util.VelocityTracker
import androidx.compose.ui.layout.LayoutCoordinates
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import com.rankmaster2.phone.media.MediaPane
import com.rankmaster2.phone.media.Rm2Media
import com.rankmaster2.phone.ui.rank.CancelNotch
import com.rankmaster2.phone.ui.rank.Centred
import com.rankmaster2.phone.ui.rank.NotchEdge
import com.rankmaster2.phone.ui.rank.ProblemPanelShell
import com.rankmaster2.phone.ui.rank.QuietButton
import com.rankmaster2.phone.ui.rank.QuietScreen
import kotlin.math.abs
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.launch

/**
 * The review screen: one photograph or video, the whole screen's worth of it, and almost nothing
 * else - the cancel notch on an edge and three dots in a corner.
 *
 * Black, like the ranking screen, and for the same reason: the picture is being judged and the
 * chrome should be the quietest thing on the glass. A vertical picture is shown vertically and a
 * horizontal one horizontally because the activity follows the sensor: turn the phone and the same
 * item simply gets a wider box. It is always *fit* - never cropped, never stretched.
 *
 * ## Touch
 *
 * A **tap** keeps the item and shows the next. A **swipe left** discards it. A **long press** opens
 * the menu at the finger, as it does on the ranking screen. Nothing else does anything.
 *
 * A tap or a swipe counts only if the finger went down inside the same centred rectangle the ranking
 * screen uses ([com.rankmaster2.phone.ui.insideLiveArea]); a long press reaches every corner,
 * because it is never an accident.
 *
 * While being swiped the item follows the finger leftwards, a still tilting a little, with no colour
 * and no label; past [COMMIT_FRACTION] of the width, or on a flick, it leaves the screen and the
 * discard commits, otherwise it springs back. Only one item is ever composed - no "next card" under
 * the dragged one - because a second video decoder on a 4K file is what crashed this app before
 * (MEMORY_PROPOSALS.md). A video is moved but not tilted: its surface is a separate layer that
 * follows a parent's position but not its rotation.
 */
@Composable
fun ReviewScreen(
    state: ReviewState,
    media: Rm2Media,
    onKeep: () -> Unit,
    onDiscard: () -> Unit,
    onCancel: () -> Unit,
    onRestart: () -> Unit,
    onRetry: () -> Unit,
    onLeave: () -> Unit,
    onOpenMenu: () -> Unit,
    onCloseMenu: () -> Unit,
    onDiscardFromMenu: () -> Unit,
    modifier: Modifier = Modifier,
) {
    // Where the menu grows from: the finger of a long press, or the three dots.
    var menuAt by remember { mutableStateOf(Offset.Zero) }
    var container by remember { mutableStateOf<LayoutCoordinates?>(null) }
    var dots by remember { mutableStateOf<LayoutCoordinates?>(null) }

    BoxWithConstraints(
        modifier
            .fillMaxSize()
            .background(Color.Black)
            .onGloballyPositioned { container = it },
    ) {
        val portrait = maxHeight >= maxWidth
        val menuItem = state.menuFor

        when (state.phase) {
            ReviewState.Phase.Loading -> Centred("Opening the folder…")
            ReviewState.Phase.Failed -> Unit // the problem panel below says why
            ReviewState.Phase.Done -> End(onRestart, onLeave)
            ReviewState.Phase.Reviewing -> Reviewer(
                state = state,
                media = media,
                onKeep = onKeep,
                onDiscard = onDiscard,
                onLongPress = { at ->
                    menuAt = at
                    onOpenMenu()
                },
            )
        }

        // The cancel notch, where and when the ranking screen shows it: the right edge in
        // portrait, bottom-right in landscape, and only when there is something to take back.
        if (state.canCancel) {
            CancelNotch(
                onCancel = onCancel,
                enabled = !state.busy,
                edge = if (portrait) NotchEdge.RIGHT else NotchEdge.BOTTOM,
                // No window inset, deliberately - see the same call in RankScreen.
                modifier = if (portrait) {
                    Modifier.align(Alignment.CenterEnd)
                } else {
                    Modifier.align(Alignment.BottomEnd).padding(end = 40.dp)
                },
            )
        }

        if (state.phase == ReviewState.Phase.Reviewing) {
            MenuDots(
                onClick = {
                    // The menu grows out of the dots: their centre, in this screen's own frame.
                    val frame = container
                    val mark = dots
                    menuAt = if (frame != null && frame.isAttached && mark != null && mark.isAttached) {
                        frame.localPositionOf(mark, Offset(mark.size.width / 2f, mark.size.height / 2f))
                    } else {
                        Offset(constraints.maxWidth.toFloat(), 0f)
                    }
                    onOpenMenu()
                },
                modifier = Modifier
                    .align(Alignment.TopEnd)
                    .windowInsetsPadding(WindowInsets.safeDrawing)
                    .padding(top = 6.dp, end = 6.dp)
                    .onGloballyPositioned { dots = it },
            )
        }

        // The menu goes on after the chrome, so its scrim covers all of it.
        if (menuItem != null && state.phase == ReviewState.Phase.Reviewing) {
            ReviewMenu(
                itemId = menuItem,
                at = menuAt,
                windowSize = IntSize(constraints.maxWidth, constraints.maxHeight),
                canDiscard = state.actionable,
                onDismiss = onCloseMenu,
                onRestart = onRestart,
                onLeave = onLeave,
                onDiscard = onDiscardFromMenu,
            )
        }

        state.problem?.let { problem ->
            ProblemPanelShell(problem.title, problem.body) {
                // No retry on a fatal one: the session is gone, or what answered was not the PC.
                if (!problem.fatal) QuietButton("Retry", onRetry)
                QuietButton("Back to folders", onLeave)
            }
        }
    }
}

// -- an item on screen -----------------------------------------------------------------------------

@Composable
private fun Reviewer(
    state: ReviewState,
    media: Rm2Media,
    onKeep: () -> Unit,
    onDiscard: () -> Unit,
    onLongPress: (Offset) -> Unit,
) {
    val ref = state.current ?: return
    val swipe = remember { SwipeState() }
    val scope = rememberCoroutineScope()
    val flingPx = with(LocalDensity.current) { FLING_DP.dp.toPx() }

    // Everything the detector reads goes through a holder rather than being captured: the detector
    // is built once, and a lambda captured when it was built would still be the one from that
    // composition, several items ago.
    val actionable = rememberUpdatedState(state.actionable)
    val keep = rememberUpdatedState(onKeep)
    val discard = rememberUpdatedState(onDiscard)
    val longPress = rememberUpdatedState(onLongPress)

    Box(Modifier.fillMaxSize().clipToBounds()) {
        val tilts = !ref.isVideo
        Box(
            Modifier
                .fillMaxSize()
                .graphicsLayer {
                    translationX = swipe.dragX
                    val width = swipe.widthPx
                    rotationZ = if (tilts && width > 0f) swipe.dragX / width * MAX_TILT_DEGREES else 0f
                },
        ) {
            MediaPane(ref, media, Modifier.fillMaxSize(), playing = state.playing)
        }

        // The gesture surface sits over the picture and does not move with it. One handler for the
        // whole screen: what a touch means depends on where it started, which only something that
        // sees the whole glass can say.
        Box(
            Modifier
                .fillMaxSize()
                // The edges are where the system's own back swipe starts. Android caps what an app
                // may claim, so this protects the middle of each edge and no more.
                .systemGestureExclusion()
                .reviewGestures(swipe, flingPx, scope, actionable, keep, discard, longPress),
        )
    }
}

private fun Modifier.reviewGestures(
    swipe: SwipeState,
    flingPx: Float,
    scope: CoroutineScope,
    actionable: State<Boolean>,
    keep: State<() -> Unit>,
    discard: State<() -> Unit>,
    longPress: State<(Offset) -> Unit>,
): Modifier = pointerInput(Unit) {
    awaitEachGesture {
        val down = awaitFirstDown(requireUnconsumed = false)
        if (swipe.flying) return@awaitEachGesture

        val width = size.width.toFloat()
        val height = size.height.toFloat()
        swipe.widthPx = width
        val slop = viewConfiguration.touchSlop
        val tracker = VelocityTracker()
        tracker.addPosition(down.uptimeMillis, down.position)

        // First: is this a tap, a long press, or a finger on the move?
        val first: Verdict? = withTimeoutOrNull(viewConfiguration.longPressTimeoutMillis) {
            var verdict: Verdict? = null
            while (verdict == null) {
                val event = awaitPointerEvent()
                val change = event.changes.firstOrNull { it.id == down.id }
                if (change == null || change.isConsumed) {
                    verdict = Verdict.Cancelled
                } else if (!change.pressed) {
                    verdict = Verdict.Up(change)
                } else if ((change.position - down.position).getDistance() > slop) {
                    verdict = Verdict.Moved(change)
                }
            }
            verdict
        }

        when (first) {
            // Held still long enough: the menu, at the finger, from anywhere on the glass.
            null -> longPress.value(down.position)

            Verdict.Cancelled -> Unit

            is Verdict.Up -> {
                first.change.consume()
                if (actionable.value && tapAction(down.position, width, height) == ReviewAction.KEEP) {
                    keep.value()
                }
            }

            is Verdict.Moved -> {
                val dx = first.change.position.x - down.position.x
                val dy = first.change.position.y - down.position.y
                // A vertical drag is not a swipe, and a drag that began on the edge is not ours.
                if (abs(dy) > abs(dx) || tapAction(down.position, width, height) == ReviewAction.NONE) {
                    return@awaitEachGesture
                }

                var released = false
                while (true) {
                    val event = awaitPointerEvent()
                    val change = event.changes.firstOrNull { it.id == down.id } ?: break
                    tracker.addPosition(change.uptimeMillis, change.position)
                    if (!change.pressed) {
                        released = true
                        break
                    }
                    swipe.dragX = dragOffset(change.position.x - down.position.x, slop)
                    change.consume()
                }
                val velocity = if (released) tracker.calculateVelocity().x else 0f
                val commits = actionable.value &&
                    swipeAction(down.position, swipe.dragX, velocity, width, height, flingPx) == ReviewAction.DISCARD
                scope.launch { swipe.settle(commits, discard.value) }
            }
        }
    }
}

/** How a touch's first moments ended. */
private sealed interface Verdict {
    data object Cancelled : Verdict
    data class Up(val change: PointerInputChange) : Verdict
    data class Moved(val change: PointerInputChange) : Verdict
}

// -- the swipe itself --------------------------------------------------------------------------------

/**
 * How far the item is dragged, and what happens when the finger lets go.
 *
 * Plain state rather than an `Animatable`: during a drag the value is set straight from the finger,
 * and only the settle (spring back, fly off) is animated, which `animate` does by writing back here.
 */
@Stable
internal class SwipeState {
    /** Zero or negative: the item only ever goes left. */
    var dragX by mutableFloatStateOf(0f)
    var flying by mutableStateOf(false)
    var widthPx: Float = 0f

    /** Off the screen to the left and then the discard - or back to rest. */
    suspend fun settle(commits: Boolean, onDiscard: () -> Unit) {
        if (!commits) {
            if (dragX != 0f) {
                animate(
                    dragX,
                    0f,
                    animationSpec = spring(dampingRatio = Spring.DampingRatioMediumBouncy, stiffness = Spring.StiffnessMedium),
                ) { v, _ -> dragX = v }
            }
            return
        }
        if (flying) return
        flying = true
        try {
            animate(dragX, -maxOf(widthPx, 1f) * 1.25f, animationSpec = tween(durationMillis = 170)) { v, _ ->
                dragX = v
            }
            onDiscard()
        } finally {
            dragX = 0f
            flying = false
        }
    }
}

// -- the end ---------------------------------------------------------------------------------------

/** Quiet, in the style of ranking's "exhausted": one line and two ways on. */
@Composable
private fun End(onRestart: () -> Unit, onLeave: () -> Unit) {
    QuietScreen {
        Text("End of the folder", color = Color(0xFFECEEF2), textAlign = TextAlign.Center)
        QuietButton("Start from the beginning", onRestart)
        QuietButton("Back to folders", onLeave)
    }
}
