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
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.window.Popup
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
 * The two panes meet along a seam: horizontal across the middle in portrait, vertical in landscape.
 * The match strip sits *on* that seam — in portrait at the middle of the screen, in landscape at the
 * seam's top end — and the cancel notch sits at the seam's other end, rising out of the bottom edge.
 * So both overlays live on the one line that belongs to neither photograph.
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
    onSkip: () -> Unit,
    onSave: () -> Unit,
    onCancel: () -> Unit,
    onOpenOverflow: () -> Unit,
    onCloseOverflow: () -> Unit,
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
            snapshot == null -> Centred("Opening…")
            state.exhausted -> Exhausted(onLeave = onLeave, onSave = onSave)
            state.left != null && state.right != null ->
                Pair(state, media, onVote) { side, at ->
                    pressedAt = at
                    onPaneMenu(side)
                }
            else -> Centred("Nothing to rank here")
        }

        // -- the overlays, in the order they may cover one another ---------------------------------

        OverflowDots(
            onOpen = onOpenOverflow,
            // Inside the safe area: a control under the status bar or a camera cutout is a control
            // that cannot be pressed.
            modifier = Modifier
                .align(Alignment.TopEnd)
                .windowInsetsPadding(WindowInsets.safeDrawing)
                .padding(4.dp),
        )

        OverflowMenu(
            open = state.overflowOpen,
            state = state,
            onDismiss = onCloseOverflow,
            onSkip = onSkip,
            onSave = onSave,
            onLeave = onLeave,
            modifier = Modifier
                .align(Alignment.TopEnd)
                .windowInsetsPadding(WindowInsets.safeDrawing)
                .padding(top = 36.dp, end = 8.dp),
        )

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
            if (portrait) {
                // The right edge, where the thumb already is and where nothing else lives.
                CancelNotchRight(
                    onCancel = onCancel,
                    enabled = !state.busy,
                    modifier = Modifier.align(Alignment.CenterEnd).windowInsetsPadding(WindowInsets.safeDrawing),
                )
            } else {
                // Bottom, but off to the right: dead centre belongs to Android's home gesture.
                CancelNotchBottom(
                    onCancel = onCancel,
                    enabled = !state.busy,
                    modifier = Modifier
                        .align(Alignment.BottomEnd)
                        .windowInsetsPadding(WindowInsets.safeDrawing)
                        .padding(end = 56.dp),
                )
            }
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

        Box(
            Modifier
                .fillMaxSize()
                .pointerInput(state.actionable, portrait, widthPx, heightPx) {
                    detectTapGestures(
                        onTap = { at ->
                            sideAt(at, portrait, widthPx, heightPx, deadBandPx)
                                ?.let { if (state.actionable) onVote(it) }
                        },
                        onLongPress = { at ->
                            sideAt(at, portrait, widthPx, heightPx, deadBandPx)
                                ?.let { side -> onPaneMenu(side, at) }
                        },
                    )
                }
        )
    }
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

@Composable
private fun OverflowMenu(
    open: Boolean,
    state: RankState,
    onDismiss: () -> Unit,
    onSkip: () -> Unit,
    onSave: () -> Unit,
    onLeave: () -> Unit,
    modifier: Modifier = Modifier,
) {
    Box(modifier) {
        DropdownMenu(expanded = open, onDismissRequest = onDismiss) {
            val counts = state.snapshot?.counts
            if (counts != null) {
                // Progress lives here rather than on the pair: the desktop shows a percentage, but
                // a number on the ranking surface is a number competing with the photographs.
                DropdownMenuItem(
                    enabled = false,
                    text = {
                        Text(
                            "${state.snapshot?.progressPercent ?: 0}% — ${counts.unranked} never ranked " +
                                "of ${counts.rankable}",
                            fontSize = 13.sp,
                        )
                    },
                    onClick = {},
                )
            }
            DropdownMenuItem(
                text = { Text("Skip this pair") },
                enabled = state.actionable,
                onClick = onSkip,
            )
            DropdownMenuItem(text = { Text("Save now") }, enabled = !state.busy, onClick = onSave)
            DropdownMenuItem(text = { Text("Close this folder") }, onClick = onLeave)
        }
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
    val density = LocalDensity.current

    // Anchored where the thumb was. A menu that opens across the screen from the press makes the
    // owner hunt for the thing they just asked for.
    val offset = with(density) { IntOffset(at.x.toInt(), at.y.toInt()) }

    Popup(
        offset = offset,
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
private fun Exhausted(onLeave: () -> Unit, onSave: () -> Unit) {
    Column(
        Modifier.fillMaxSize().padding(32.dp),
        verticalArrangement = Arrangement.spacedBy(10.dp, Alignment.CenterVertically),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Text("Nothing left to rank here", color = Color(0xFFECEEF2), textAlign = TextAlign.Center)
        Text(
            "Every pair in this folder has been seen.",
            color = Color(0xFF9AA3B2),
            fontSize = 13.sp,
            textAlign = TextAlign.Center,
        )
        TextButton(onClick = onSave) { Text("Save now", color = Color(0xFF2ECC71)) }
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
            Modifier.padding(32.dp),
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

/** How wide the seam's dead band is. Thin enough to never be in the way, wide enough for a thumb. */
private val DeadBand = 44.dp

internal fun Modifier.clickableWhen(enabled: Boolean, onClick: () -> Unit): Modifier =
    this.then(if (enabled) Modifier.clickable(onClick = onClick) else Modifier)
