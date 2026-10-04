package com.rankmaster2.phone.ui.review

import androidx.compose.animation.core.Spring
import androidx.compose.animation.core.animate
import androidx.compose.animation.core.spring
import androidx.compose.animation.core.tween
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.gestures.awaitHorizontalTouchSlopOrCancellation
import androidx.compose.foundation.gestures.horizontalDrag
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawing
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.systemGestureExclusion
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.SideEffect
import androidx.compose.runtime.Stable
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
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.input.pointer.positionChange
import androidx.compose.ui.input.pointer.util.VelocityTracker
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.rankmaster2.phone.media.MediaPane
import com.rankmaster2.phone.media.Rm2Media
import com.rankmaster2.phone.net.MediaRef
import kotlin.math.abs
import kotlin.math.sign
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.launch

/**
 * The review screen: one photograph or video, the whole screen's worth of it, and three buttons.
 *
 * Black, like the ranking screen, and for the same reason: the picture is being judged and the
 * chrome should be the quietest thing on the glass. A vertical picture is shown vertically and a
 * horizontal one horizontally because the activity follows the sensor: turn the phone and the
 * same item simply gets a wider box. It is always *fit* - never cropped, never stretched.
 *
 * ## Swiping
 *
 * Left discards, right keeps. The item follows the finger, tilted a little, with the word for what
 * will happen growing on it; let go past [COMMIT_FRACTION] of the width (or flick) and it leaves the
 * screen and the action commits, otherwise it springs back. Only one item is ever composed - no
 * "next card" under the dragged one - because a second video decoder on a 4K file is what crashed
 * this app before (MEMORY_PROPOSALS.md). A video is moved but not tilted: its surface is a separate
 * layer that follows a parent's position but not its rotation.
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
    onDismissProblem: () -> Unit,
    modifier: Modifier = Modifier,
) {
    Box(modifier.fillMaxSize().background(Color.Black)) {
        when (state.phase) {
            ReviewState.Phase.Loading -> Centred("Opening the folder…")
            ReviewState.Phase.Failed -> Failed(state.problem, onRetry, onLeave)
            ReviewState.Phase.Done -> Done(state, onCancel, onRestart, onLeave)
            ReviewState.Phase.Reviewing -> Reviewer(state, media, onKeep, onDiscard, onCancel)
        }

        state.problem?.takeIf { state.phase != ReviewState.Phase.Failed }?.let { problem ->
            if (problem.fatal) {
                FatalPanel(problem, onLeave)
            } else {
                ProblemBanner(
                    problem = problem,
                    onDismiss = onDismissProblem,
                    modifier = Modifier
                        .align(Alignment.TopCenter)
                        .windowInsetsPadding(WindowInsets.safeDrawing)
                        .padding(top = 34.dp, start = 16.dp, end = 16.dp),
                )
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
    onCancel: () -> Unit,
) {
    val ref = state.current ?: return
    val swipe = remember { SwipeState() }
    val scope = rememberCoroutineScope()
    val keep by rememberUpdatedState(onKeep)
    val discard by rememberUpdatedState(onDiscard)

    val flyKeep: () -> Unit = { scope.launch { swipe.flyOff(+1f, keep, discard) } }
    val flyDiscard: () -> Unit = { scope.launch { swipe.flyOff(-1f, keep, discard) } }
    val enabled = state.actionable

    BoxWithConstraints(Modifier.fillMaxSize()) {
        val portrait = maxHeight >= maxWidth
        if (portrait) {
            Column(Modifier.fillMaxSize().windowInsetsPadding(WindowInsets.safeDrawing)) {
                TopLine(state, Modifier.fillMaxWidth().padding(horizontal = 14.dp, vertical = 6.dp))
                MediaArea(state, ref, media, swipe, scope, keep, discard, Modifier.weight(1f).fillMaxWidth())
                FileName(ref, Modifier.fillMaxWidth().padding(horizontal = 14.dp, vertical = 4.dp))
                Row(
                    Modifier.fillMaxWidth().padding(start = 12.dp, end = 12.dp, top = 4.dp, bottom = 20.dp),
                    horizontalArrangement = Arrangement.spacedBy(10.dp),
                ) {
                    DiscardButton(enabled, flyDiscard, Modifier.weight(1f))
                    CancelButton(state.canCancel && !state.busy, onCancel, Modifier.weight(1f))
                    NextButton(enabled, flyKeep, Modifier.weight(1f))
                }
            }
        } else {
            Row(Modifier.fillMaxSize().windowInsetsPadding(WindowInsets.safeDrawing)) {
                MediaArea(state, ref, media, swipe, scope, keep, discard, Modifier.weight(1f).fillMaxHeight())
                Column(
                    Modifier.width(150.dp).fillMaxHeight().padding(horizontal = 10.dp, vertical = 6.dp),
                    verticalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    TopLine(state, Modifier.fillMaxWidth())
                    Box(Modifier.weight(1f).fillMaxWidth(), contentAlignment = Alignment.BottomStart) {
                        FileName(ref, Modifier.fillMaxWidth())
                    }
                    DiscardButton(enabled, flyDiscard, Modifier.fillMaxWidth())
                    CancelButton(state.canCancel && !state.busy, onCancel, Modifier.fillMaxWidth())
                    NextButton(enabled, flyKeep, Modifier.fillMaxWidth())
                }
            }
        }
    }
}

/** `37 %` and the folder, small and dim: there to be consulted, not read. */
@Composable
private fun TopLine(state: ReviewState, modifier: Modifier = Modifier) {
    Row(modifier, verticalAlignment = Alignment.CenterVertically) {
        Text("${state.percent} %", color = Dim, fontSize = 12.sp, fontWeight = FontWeight.Medium)
        Text(
            state.folderName,
            color = Faint,
            fontSize = 12.sp,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier.padding(start = 10.dp).weight(1f, fill = false),
        )
    }
}

@Composable
private fun FileName(ref: MediaRef, modifier: Modifier = Modifier) {
    Text(
        ref.id,
        color = Faint,
        fontSize = 11.sp,
        maxLines = 1,
        overflow = TextOverflow.Ellipsis,
        modifier = modifier,
    )
}

/**
 * The picture, draggable. The gesture lives here and only horizontal movement is taken: a vertical
 * drag is not consumed and does nothing.
 */
@Composable
private fun MediaArea(
    state: ReviewState,
    ref: MediaRef,
    media: Rm2Media,
    swipe: SwipeState,
    scope: CoroutineScope,
    onKeep: () -> Unit,
    onDiscard: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val flingPx = with(LocalDensity.current) { FLING_DP.dp.toPx() }
    val enabled = state.actionable

    BoxWithConstraints(
        modifier
            .clipToBounds()
            // The edges are where the system's own back swipe starts. Android caps what an app may
            // claim, so this protects the middle of each edge and no more.
            .systemGestureExclusion()
            .pointerInput(enabled) {
                if (!enabled) return@pointerInput
                awaitEachGesture {
                    val down = awaitFirstDown(requireUnconsumed = false)
                    if (swipe.flying) return@awaitEachGesture
                    val tracker = VelocityTracker()
                    tracker.addPosition(down.uptimeMillis, down.position)
                    val slop = awaitHorizontalTouchSlopOrCancellation(down.id) { change, over ->
                        change.consume()
                        swipe.dragX += over
                    }
                    if (slop != null) {
                        tracker.addPosition(slop.uptimeMillis, slop.position)
                        val completed = horizontalDrag(slop.id) { change ->
                            swipe.dragX += change.positionChange().x
                            tracker.addPosition(change.uptimeMillis, change.position)
                            change.consume()
                        }
                        val velocity = if (completed) tracker.calculateVelocity().x else 0f
                        scope.launch { swipe.release(velocity, flingPx, onKeep, onDiscard) }
                    }
                }
            },
    ) {
        val widthPx = constraints.maxWidth.toFloat()
        SideEffect { swipe.widthPx = widthPx }

        val tilts = !ref.isVideo
        Box(
            Modifier
                .fillMaxSize()
                .graphicsLayer {
                    translationX = swipe.dragX
                    rotationZ = if (tilts && widthPx > 0f) swipe.dragX / widthPx * MAX_TILT_DEGREES else 0f
                },
        ) {
            MediaPane(ref, media, Modifier.fillMaxSize(), playing = state.playing)
        }

        Stamp(swipe, widthPx)
    }
}

/** The word that grows on the item as it is dragged: DISCARD to the left, KEEP to the right. */

@Composable
private fun androidx.compose.foundation.layout.BoxScope.Stamp(swipe: SwipeState, widthPx: Float) {
    val x = swipe.dragX
    val progress = if (widthPx > 0f) (abs(x) / (widthPx * COMMIT_FRACTION)).coerceIn(0f, 1f) else 0f
    if (progress < 0.06f) return
    val discarding = x < 0f
    val colour = if (discarding) Red else Green
    Box(
        Modifier
            .align(Alignment.TopCenter)
            .padding(top = 28.dp)
            .graphicsLayer {
                alpha = progress
                scaleX = 0.8f + 0.25f * progress
                scaleY = 0.8f + 0.25f * progress
                rotationZ = if (discarding) -9f else 9f
            }
            .border(3.dp, colour, RoundedCornerShape(10.dp))
            .background(Color(0x66000000), RoundedCornerShape(10.dp))
            .padding(horizontal = 16.dp, vertical = 6.dp),
    ) {
        Text(
            if (discarding) "DISCARD" else "KEEP",
            color = colour,
            fontSize = 26.sp,
            fontWeight = FontWeight.Bold,
        )
    }
}

@Composable
private fun DiscardButton(enabled: Boolean, onClick: () -> Unit, modifier: Modifier) {
    Button(
        onClick = onClick,
        enabled = enabled,
        modifier = modifier.height(BUTTON_HEIGHT),
        shape = RoundedCornerShape(12.dp),
        colors = ButtonDefaults.buttonColors(
            containerColor = Color(0xFFB8433C),
            contentColor = Color.White,
            disabledContainerColor = Color(0xFF3A2523),
            disabledContentColor = Color(0xFF7C838F),
        ),
    ) { Text("Discard", fontSize = 16.sp) }
}

@Composable
private fun CancelButton(enabled: Boolean, onClick: () -> Unit, modifier: Modifier) {
    OutlinedButton(
        onClick = onClick,
        enabled = enabled,
        modifier = modifier.height(BUTTON_HEIGHT),
        shape = RoundedCornerShape(12.dp),
        border = BorderStroke(1.dp, if (enabled) Color(0xFF6B7280) else Color(0xFF2B2F36)),
        colors = ButtonDefaults.outlinedButtonColors(
            contentColor = Color(0xFFECEEF2),
            disabledContentColor = Color(0xFF555B66),
        ),
    ) { Text("Cancel", fontSize = 16.sp) }
}

@Composable
private fun NextButton(enabled: Boolean, onClick: () -> Unit, modifier: Modifier) {
    Button(
        onClick = onClick,
        enabled = enabled,
        modifier = modifier.height(BUTTON_HEIGHT),
        shape = RoundedCornerShape(12.dp),
        colors = ButtonDefaults.buttonColors(
            containerColor = Color(0xFF1F7A47),
            contentColor = Color.White,
            disabledContainerColor = Color(0xFF1E2E25),
            disabledContentColor = Color(0xFF7C838F),
        ),
    ) { Text("Next", fontSize = 16.sp) }
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
    var dragX by mutableFloatStateOf(0f)
    var flying by mutableStateOf(false)
    var widthPx: Float = 0f

    suspend fun release(velocityX: Float, flingPx: Float, onKeep: () -> Unit, onDiscard: () -> Unit) {
        val direction = decide(dragX, velocityX, widthPx, flingPx)
        if (direction == 0f) {
            animate(dragX, 0f, animationSpec = spring(dampingRatio = Spring.DampingRatioMediumBouncy, stiffness = Spring.StiffnessMedium)) { v, _ ->
                dragX = v
            }
        } else {
            flyOff(direction, onKeep, onDiscard)
        }
    }

    /** Off the screen on the [direction] side (-1 left, +1 right), then the action, then reset. */
    suspend fun flyOff(direction: Float, onKeep: () -> Unit, onDiscard: () -> Unit) {
        if (flying) return
        flying = true
        try {
            animate(dragX, direction * maxOf(widthPx, 1f) * 1.25f, animationSpec = tween(durationMillis = 170)) { v, _ ->
                dragX = v
            }
            if (direction < 0f) onDiscard() else onKeep()
        } finally {
            dragX = 0f
            flying = false
        }
    }

    companion object {
        /**
         * -1 (discard), +1 (keep) or 0 (spring back), from where the item was let go and how fast.
         * Past [COMMIT_FRACTION] of the width, or a fast flick the way it is already heading.
         */
        fun decide(dragX: Float, velocityX: Float, widthPx: Float, flingPx: Float): Float = when {
            widthPx > 0f && abs(dragX) >= widthPx * COMMIT_FRACTION -> sign(dragX)
            abs(velocityX) >= flingPx && (dragX == 0f || sign(dragX) == sign(velocityX)) -> sign(velocityX)
            else -> 0f
        }
    }
}

// -- the other views -----------------------------------------------------------------------------------

@Composable
private fun Done(state: ReviewState, onCancel: () -> Unit, onRestart: () -> Unit, onLeave: () -> Unit) {
    Column(
        Modifier.fillMaxSize().windowInsetsPadding(WindowInsets.safeDrawing).padding(32.dp),
        verticalArrangement = Arrangement.spacedBy(10.dp, Alignment.CenterVertically),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Text("All reviewed", color = Color(0xFFECEEF2), fontSize = 20.sp, textAlign = TextAlign.Center)
        Text(
            when (state.discardedThisVisit) {
                0 -> "Nothing was discarded this time."
                1 -> "1 file discarded this time."
                else -> "${state.discardedThisVisit} files discarded this time."
            },
            color = Dim,
            fontSize = 14.sp,
            textAlign = TextAlign.Center,
        )
        if (state.canCancel) {
            TextButton(onClick = onCancel, enabled = !state.busy) {
                Text("Cancel the last one", color = Green)
            }
        }
        if (state.items.isNotEmpty()) {
            Button(onClick = onRestart, shape = RoundedCornerShape(12.dp)) { Text("Start again") }
        }
        TextButton(onClick = onLeave) { Text("Back to folders", color = Green) }
    }
}

@Composable
private fun Failed(problem: ReviewState.Problem?, onRetry: () -> Unit, onLeave: () -> Unit) {
    Column(
        Modifier.fillMaxSize().windowInsetsPadding(WindowInsets.safeDrawing).padding(32.dp),
        verticalArrangement = Arrangement.spacedBy(10.dp, Alignment.CenterVertically),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Text(problem?.title ?: "Could not open the folder", color = Color(0xFFECEEF2), textAlign = TextAlign.Center)
        problem?.body?.let { Text(it, color = Dim, fontSize = 13.sp, textAlign = TextAlign.Center) }
        if (problem?.fatal != true) {
            Button(onClick = onRetry, shape = RoundedCornerShape(12.dp)) { Text("Retry") }
        }
        TextButton(onClick = onLeave) { Text("Back to folders", color = Green) }
    }
}

@Composable
private fun FatalPanel(problem: ReviewState.Problem, onLeave: () -> Unit) {
    Box(Modifier.fillMaxSize().background(Color(0xE6000000)), contentAlignment = Alignment.Center) {
        Column(
            Modifier.windowInsetsPadding(WindowInsets.safeDrawing).padding(32.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            Text(problem.title, color = Color(0xFFECEEF2), textAlign = TextAlign.Center)
            Text(problem.body, color = Dim, fontSize = 13.sp, textAlign = TextAlign.Center)
            TextButton(onClick = onLeave) { Text("Back to folders", color = Green) }
        }
    }
}

/** A failure that does not need a decision: said once, dismissible, never covering the picture's controls. */
@Composable
private fun ProblemBanner(problem: ReviewState.Problem, onDismiss: () -> Unit, modifier: Modifier = Modifier) {
    Row(
        modifier
            .background(Color(0xF0181A1F), RoundedCornerShape(12.dp))
            .border(1.dp, Red.copy(alpha = 0.5f), RoundedCornerShape(12.dp))
            .padding(start = 14.dp, top = 8.dp, bottom = 8.dp, end = 4.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column(Modifier.weight(1f)) {
            Text(problem.title, color = Color(0xFFECEEF2), fontSize = 13.sp, fontWeight = FontWeight.SemiBold, maxLines = 2, overflow = TextOverflow.Ellipsis)
            Text(problem.body, color = Dim, fontSize = 12.sp, maxLines = 3, overflow = TextOverflow.Ellipsis)
        }
        TextButton(onClick = onDismiss) { Text("Close", color = Green) }
    }
}

@Composable
private fun Centred(text: String) {
    Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
        Text(text, color = Color(0xFF9AA3B2))
    }
}

private val Dim = Color(0xFF9AA3B2)
private val Faint = Color(0xFF7C838F)
private val Red = Color(0xFFE2635B)
private val Green = Color(0xFF2ECC71)
private val BUTTON_HEIGHT = 56.dp

/** Let go past this share of the width and the action commits. */
internal const val COMMIT_FRACTION = 0.30f

/** A flick faster than this commits regardless of distance. */
private const val FLING_DP = 900

/** The tilt at a full width of drag. */
private const val MAX_TILT_DEGREES = 8f
