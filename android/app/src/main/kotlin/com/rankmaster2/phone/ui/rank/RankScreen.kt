package com.rankmaster2.phone.ui.rank

import androidx.compose.foundation.background
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.safeDrawing
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.IntRect
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.ui.window.Popup
import androidx.compose.ui.window.PopupPositionProvider
import androidx.compose.ui.window.PopupProperties
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.rankmaster2.phone.media.MediaPane
import com.rankmaster2.phone.media.Rm2Media
import com.rankmaster2.phone.net.MediaRef
import kotlinx.coroutines.delay

/**
 * The ranking screen: two photographs and almost nothing else.
 *
 * The pair gets **all** of the screen. Everything the app wants to say floats on top of it, and the
 * layout reserves no space for any of it, because the only question on this screen is which of two
 * pictures is better and anything permanently occupying pixels is competing with the answer.
 *
 * ## Where things sit, and why
 *
 * There is no menu and no bar. Skip went because the owner does not skip; save went because every
 * action is already on disk before its response is sent, so there was never anything to save; close
 * went because back does it. What is left floating is the match strip on the seam, the cancel notch
 * on an edge, and a progress hairline along the bottom.
 *
 * ## The dead band
 *
 * A tap in a thin strip along the seam does nothing at all. A vote cannot be taken back by looking
 * at it — cancel exists, but it costs a round trip and the owner's attention — so a thumb landing on
 * the border between the two targets must not guess. Doing nothing is always recoverable.
 */
@Composable
fun RankScreen(
    state: RankState,
    media: Rm2Media,
    onVote: (Side) -> Unit,
    onPaneMenu: (Side) -> Unit,
    onClosePaneMenu: () -> Unit,
    onDiscard: (Side) -> Unit,
    onSpecial: (Side) -> Unit,
    onView: (Side) -> Unit,
    onCancel: () -> Unit,
    onLeave: () -> Unit,
    onDismissNotice: () -> Unit,
    onDismissProblem: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val snapshot = state.snapshot

    // Where the long press landed, so its menu opens under the thumb rather than in a corner of
    // the screen the owner was not looking at.
    var pressedAt by remember { mutableStateOf(Offset.Zero) }

    BoxWithConstraints(modifier = modifier.fillMaxSize().background(Color.Black)) {
        when {
            snapshot == null -> Centred("Opening the folder…")
            state.exhausted -> Exhausted(onLeave = onLeave)
            state.left != null && state.right != null ->
                Pair(state, media, onVote) { side, at ->
                    pressedAt = at
                    onPaneMenu(side)
                }
            // The server says it is still ranking but sent no pair. Not something the owner did,
            // and not something he can fix from here - going back and opening it again is.
            else -> Centred("This folder has stopped handing out pictures. Go back and open it again.")
        }

        // -- the overlays, in the order they may cover one another ---------------------------------

        state.paneMenu?.let { side ->
            PaneMenu(
                side = side,
                at = pressedAt,
                state = state,
                onDismiss = onClosePaneMenu,
                onView = { onView(side) },
                onDiscard = { onDiscard(side) },
                onSpecial = { onSpecial(side) },
            )
        }

        if (state.canCancel) {
            val portrait = maxHeight >= maxWidth
            CancelNotch(
                onCancel = onCancel,
                enabled = !state.busy,
                edge = if (portrait) NotchEdge.RIGHT else NotchEdge.BOTTOM,
                // No window inset, deliberately. `safeDrawing` is right for a control that must
                // stay *clear* of an edge and wrong for one whose whole design is to grow out of
                // one: the base has to touch the glass or the tab is a floating panel again. The
                // system bars are hidden on this screen, so the inset it would add is zero
                // anyway - the gap the owner saw came from the rotation, not from here, and is
                // fixed inside CancelNotch. The tab reserves its own rectangle from the gesture
                // navigator instead, which is the thing an edge control actually has to do.
                modifier = if (portrait) {
                    // The right edge, where the thumb already is and where nothing else lives.
                    Modifier.align(Alignment.CenterEnd)
                } else {
                    // Bottom, off to the right: dead centre belongs to Android's home gesture.
                    Modifier.align(Alignment.BottomEnd).padding(end = 40.dp)
                },
            )
        }

        state.notice?.let { text ->
            LaunchedEffect(text) {
                delay(2200)
                onDismissNotice()
            }
            Notice(
                text = text,
                modifier = Modifier
                    .align(Alignment.BottomCenter)
                    .windowInsetsPadding(WindowInsets.safeDrawing)
                    .padding(bottom = 18.dp),
            )
        }

        ProgressLine(
            percent = state.snapshot?.progressPercent ?: 0,
            modifier = Modifier.align(Alignment.BottomCenter),
        )

        state.problem?.let { problem ->
            ProblemPanel(problem = problem, onDismiss = onDismissProblem, onLeave = onLeave)
        }
    }
}

/**
 * The pair itself, and the only gesture surface in the app.
 *
 * One pointer handler over both panes rather than one each: the dead band is defined by where the
 * seam is, so the decision has to be made somewhere that knows about both.
 */
@Composable
private fun Pair(
    state: RankState,
    media: Rm2Media,
    onVote: (Side) -> Unit,
    onPaneMenu: (Side, Offset) -> Unit,
) {
    val left = state.left ?: return
    val right = state.right ?: return

    BoxWithConstraints(Modifier.fillMaxSize()) {
        val portrait = maxHeight >= maxWidth
        val deadBandPx = with(LocalDensity.current) { DeadBand.toPx() }
        val widthPx = with(LocalDensity.current) { maxWidth.toPx() }
        val heightPx = with(LocalDensity.current) { maxHeight.toPx() }

        // Videos run only while this screen is in front and nothing is in flight; two decoders
        // going while an action resolves is how a phone runs out of memory mid-session.
        val playing = state.panesPlaying

        if (portrait) {
            Column(Modifier.fillMaxSize()) {
                Pane(left, media, playing, Modifier.weight(1f).fillMaxWidth())
                Pane(right, media, playing, Modifier.weight(1f).fillMaxWidth())
            }
        } else {
            Row(Modifier.fillMaxSize()) {
                Pane(left, media, playing, Modifier.weight(1f))
                Pane(right, media, playing, Modifier.weight(1f))
            }
        }

        // The strip lives on the seam: across the middle in portrait, at the seam's top in landscape.
        MatchStrip(
            cues = state.cues,
            modifier = if (portrait) {
                Modifier.align(Alignment.Center)
            } else {
                Modifier.align(Alignment.TopCenter).padding(top = 6.dp)
            },
        )

        // The gesture detector is keyed on the *geometry* and nothing else, so it survives every
        // recomposition that only changed what a tap means. Keying it on `actionable` tore the
        // detector down and built a new one in the middle of every single vote, which loses
        // whatever gesture happened to be in progress under the finger at that moment.
        //
        // Everything the detector reads is therefore read through a holder rather than captured:
        // a lambda captured when the detector was built would still be the one from that
        // composition, several pairs ago.
        val actionable by rememberUpdatedState(state.actionable)
        val vote by rememberUpdatedState(onVote)
        val paneMenu by rememberUpdatedState(onPaneMenu)

        Box(
            Modifier
                .fillMaxSize()
                .pointerInput(portrait, widthPx, heightPx, deadBandPx) {
                    detectTapGestures(
                        // A tap votes, and only away from the seam and away from the outside edge.
                        onTap = { at ->
                            votableSideAt(at, portrait, widthPx, heightPx)
                                ?.let { if (actionable) vote(it) }
                        },
                        // A long press is not an accident, so it reaches every corner: the pane
                        // menu is the one thing somebody might deliberately go to an edge for.
                        onLongPress = { at ->
                            sideAt(at, portrait, widthPx, heightPx, deadBandPx)
                                ?.let { side -> paneMenu(side, at) }
                        },
                    )
                }
        )
    }
}

/**
 * The share of the screen on which a tap casts a vote: **half**.
 *
 * The owner kept voting by accident. A wrong vote is the one mistake this screen can make that
 * costs anything - it moves a rating nobody asked to move, and cancel costs a round trip and his
 * attention - so the target is deliberately much smaller than the picture it belongs to. The rest
 * of each pane still *shows* the photograph; it just does not answer a tap.
 *
 * Expressed as the share of the glass, because that is how it was asked for and how it will be
 * adjusted. One number, one place: if half is still too much, this line moves and nothing else.
 */
internal const val VotingShareOfScreen = 0.50f

/**
 * The live box sits inside each pane, so this is also the share of *each pane* that votes - two
 * panes, each half live, is half the screen. Equal on both axes, hence the square root.
 */
private fun liveFractionPerAxis(share: Float): Float =
    kotlin.math.sqrt(share.coerceIn(0.01f, 1f))

/**
 * Which pane a touch may **vote** for: inside that pane's live box, or nothing.
 *
 * Kept separate from [sideAt] rather than folded into it, because the two questions genuinely
 * differ. "Which pane is this?" is what a long press asks, and it has an answer everywhere on the
 * glass. "May this cast a vote?" is what a tap asks, and outside the live box the answer is no.
 *
 * This subsumes the old seam band and the old edge margin: the live box is centred in the pane, so
 * it is already clear of the seam on one side and of the outside on the other three. One rule
 * instead of two that had to be kept consistent with each other.
 */
internal fun votableSideAt(
    at: Offset,
    portrait: Boolean,
    width: Float,
    height: Float,
    share: Float = VotingShareOfScreen,
): Side? {
    val side = sideAt(at, portrait, width, height, deadBand = 0f) ?: return null

    // The pane this touch is in, in screen coordinates.
    val paneWidth = if (portrait) width else width / 2f
    val paneHeight = if (portrait) height / 2f else height
    val originX = if (portrait || side == Side.LEFT) 0f else width / 2f
    val originY = if (!portrait || side == Side.LEFT) 0f else height / 2f

    val live = liveFractionPerAxis(share)
    val marginX = paneWidth * (1f - live) / 2f
    val marginY = paneHeight * (1f - live) / 2f

    val x = at.x - originX
    val y = at.y - originY

    val inside = x >= marginX && x <= paneWidth - marginX &&
        y >= marginY && y <= paneHeight - marginY

    return if (inside) side else null
}

/**
 * Which pane a touch belongs to, or null inside the dead band.
 *
 * Kept as a plain function so it can be reasoned about, and tested, without a screen.
 */
internal fun sideAt(
    at: Offset,
    portrait: Boolean,
    width: Float,
    height: Float,
    deadBand: Float,
): Side? {
    val along = if (portrait) at.y else at.x
    val extent = if (portrait) height else width
    val seam = extent / 2f

    if (kotlin.math.abs(along - seam) <= deadBand / 2f) return null
    return if (along < seam) Side.LEFT else Side.RIGHT
}

@Composable
private fun Pane(ref: MediaRef, media: Rm2Media, playing: Boolean, modifier: Modifier) {
    Box(modifier.background(Color.Black), contentAlignment = Alignment.Center) {
        MediaPane(ref = ref, media = media, playing = playing)
    }
}

/**
 * One photograph's own actions. Anchored to the pane that was pressed, so "which one?" is never a
 * question the owner has to answer twice.
 */
@Composable
private fun PaneMenu(
    side: Side,
    at: Offset,
    state: RankState,
    onDismiss: () -> Unit,
    onView: () -> Unit,
    onDiscard: () -> Unit,
    onSpecial: () -> Unit,
) {
    val ref = if (side == Side.LEFT) state.left else state.right

    // Anchored where the thumb was, and then pushed back onto the screen if that would hang it
    // off the edge. A long press in the bottom corner of the lower pane - which the edge margin
    // now makes a *likely* place to press, because it is the one thing left that works there -
    // otherwise opens a menu the owner can only see half of.
    val press = IntOffset(at.x.toInt(), at.y.toInt())

    Popup(
        popupPositionProvider = remember(press) { ThumbPositionProvider(press) },
        onDismissRequest = onDismiss,
        properties = PopupProperties(focusable = true),
    ) {
        Surface(
            color = Color(0xF21A1D23),
            shape = RoundedCornerShape(12.dp),
            tonalElevation = 6.dp,
        ) {
            Column(Modifier.padding(vertical = 6.dp)) {
                Text(
                    text = ref?.id.orEmpty(),
                    color = Color(0xFF9AA3B2),
                    fontSize = 11.sp,
                    modifier = Modifier.padding(horizontal = 16.dp, vertical = 6.dp),
                )
                MenuLine("View full screen", enabled = true, onClick = onView)
                MenuLine("Discard", enabled = state.actionable, onClick = onDiscard)
                MenuLine("Move to special", enabled = state.actionable, onClick = onSpecial)
            }
        }
    }
}

@Composable
private fun MenuLine(text: String, enabled: Boolean, onClick: () -> Unit) {
    Text(
        text = text,
        color = if (enabled) Color(0xFFECEEF2) else Color(0xFF606878),
        fontSize = 15.sp,
        modifier = Modifier
            .fillMaxWidth()
            .clickableWhen(enabled, onClick)
            .padding(horizontal = 20.dp, vertical = 11.dp),
    )
}

@Composable
private fun Exhausted(onLeave: () -> Unit) {
    Column(
        Modifier
            .fillMaxSize()
            .windowInsetsPadding(WindowInsets.safeDrawing)
            .padding(32.dp),
        verticalArrangement = Arrangement.spacedBy(10.dp, Alignment.CenterVertically),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Text("Nothing left to rank here", color = Color(0xFFECEEF2), textAlign = TextAlign.Center)
        Text(
            "Every pair in this folder has been seen. Everything is saved.",
            color = Color(0xFF9AA3B2),
            fontSize = 13.sp,
            textAlign = TextAlign.Center,
        )
        TextButton(onClick = onLeave) { Text("Pick another folder", color = Color(0xFF2ECC71)) }
    }
}

@Composable
private fun ProblemPanel(
    problem: RankState.Problem,
    onDismiss: () -> Unit,
    onLeave: () -> Unit,
) {
    Box(
        Modifier.fillMaxSize().background(Color(0xE6000000)),
        contentAlignment = Alignment.Center,
    ) {
        Column(
            Modifier
                .windowInsetsPadding(WindowInsets.safeDrawing)
                .padding(32.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            Text(problem.title, color = Color(0xFFECEEF2), textAlign = TextAlign.Center)
            Text(
                problem.body,
                color = Color(0xFF9AA3B2),
                fontSize = 13.sp,
                textAlign = TextAlign.Center,
            )
            if (problem.fatal) {
                // No "try again" on a fatal one. There is nothing to try: the session is gone, or
                // the thing that answered was not the PC.
                TextButton(onClick = onLeave) { Text("Back to folders", color = Color(0xFF2ECC71)) }
            } else {
                TextButton(onClick = onDismiss) { Text("Close", color = Color(0xFF2ECC71)) }
            }
        }
    }
}

@Composable
private fun Centred(text: String) {
    Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
        Text(text, color = Color(0xFF9AA3B2))
    }
}

/**
 * Opens a popup at the point that was pressed, and never off the glass.
 *
 * Compose's own alignment-plus-offset provider does not clamp: asked for a position near the bottom
 * or the right of the window it hands back exactly that, and the part of the menu past the edge is
 * simply not on the screen. Every candidate here is clamped into the window instead, and when there
 * is no room below the press the menu opens above it - the way a context menu on any other Android
 * surface behaves.
 */
internal class ThumbPositionProvider(private val at: IntOffset) : PopupPositionProvider {

    override fun calculatePosition(
        anchorBounds: IntRect,
        windowSize: IntSize,
        layoutDirection: LayoutDirection,
        popupContentSize: IntSize,
    ): IntOffset = positionAt(
        pressX = anchorBounds.left + at.x,
        pressY = anchorBounds.top + at.y,
        windowSize = windowSize,
        popupContentSize = popupContentSize,
    )

    internal companion object {

        /** Kept apart from the Compose interface so the arithmetic can be tested on its own. */
        fun positionAt(
            pressX: Int,
            pressY: Int,
            windowSize: IntSize,
            popupContentSize: IntSize,
        ): IntOffset {
            // Below and to the right of the thumb where there is room; flipped above it where
            // there is not, so the press point stays visible rather than being covered.
            val preferredY = if (pressY + popupContentSize.height <= windowSize.height) {
                pressY
            } else {
                pressY - popupContentSize.height
            }
            return IntOffset(
                x = clamp(pressX, popupContentSize.width, windowSize.width),
                y = clamp(preferredY, popupContentSize.height, windowSize.height),
            )
        }

        /** Inside the window if it fits, and hard against the leading edge if it does not. */
        private fun clamp(start: Int, size: Int, extent: Int): Int =
            start.coerceIn(0, maxOf(0, extent - size))
    }
}

/** How wide the seam's dead band is. Thin enough to never be in the way, wide enough for a thumb. */
private val DeadBand = 44.dp

internal fun Modifier.clickableWhen(enabled: Boolean, onClick: () -> Unit): Modifier =
    this.then(if (enabled) Modifier.clickable(onClick = onClick) else Modifier)
