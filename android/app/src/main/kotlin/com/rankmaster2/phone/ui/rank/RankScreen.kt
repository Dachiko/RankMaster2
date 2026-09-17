package com.rankmaster2.phone.ui.rank

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectTapGestures
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
import androidx.compose.ui.unit.IntSize
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
    onVote: (Side, String?) -> Unit,
    onPaneMenu: (Side, String?) -> Unit,
    onClosePaneMenu: () -> Unit,
    onDiscard: (Side, String?) -> Unit,
    onSpecial: (Side, String?) -> Unit,
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
            state.exhausted -> Exhausted(
                canCancel = state.canCancel,
                busy = state.busy,
                onCancel = onCancel,
                onLeave = onLeave,
            )
            state.left != null && state.right != null ->
                Pair(state, media, onVote) { side, at, aimedAtToken ->
                    pressedAt = at
                    onPaneMenu(side, aimedAtToken)
                }
            // The server says it is still ranking but sent no pair. Not something the owner did,
            // and not something he can fix from here - going back and opening it again is.
            else -> Centred("This folder has stopped handing out pictures. Go back and open it again.")
        }

        // -- the overlays, in the order they may cover one another ---------------------------------

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

        // The menu goes on *after* the chrome, not before it. Its scrim is the modal state made
        // visible, and a cancel tab still glowing at full strength over a dimmed screen would be
        // both a lie about what is live and a second thing to aim at.
        state.paneMenu?.let { side ->
            PaneMenu(
                side = side,
                at = pressedAt,
                // The window the menu is positioned in, so it can work out which corner of itself
                // the press point is - and grow from there. See `menuTransformOrigin`.
                windowSize = with(LocalDensity.current) {
                    IntSize(maxWidth.roundToPx(), maxHeight.roundToPx())
                },
                state = state,
                onDismiss = onClosePaneMenu,
                onView = { onView(side) },
                // The token this menu was opened with (§ 1.3), not whatever is current now — the
                // menu can sit open for seconds while it is read.
                onDiscard = { onDiscard(side, state.paneMenuToken) },
                onSpecial = { onSpecial(side, state.paneMenuToken) },
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
    onVote: (Side, String?) -> Unit,
    onPaneMenu: (Side, Offset, String?) -> Unit,
) {
    val left = state.left ?: return
    val right = state.right ?: return

    BoxWithConstraints(Modifier.fillMaxSize()) {
        val portrait = maxHeight >= maxWidth
        val deadBandPx = with(LocalDensity.current) { DeadBand.toPx() }
        val widthPx = with(LocalDensity.current) { maxWidth.toPx() }
        val heightPx = with(LocalDensity.current) { maxHeight.toPx() }

        // Videos run only while this screen is in front and the full-screen viewer is not open
        // over it - RankState.panesPlaying is exactly `foreground && viewing == null`. Busy (an
        // action resolving) is deliberately not part of this: it is brief, and a pane that stopped
        // and restarted playback on every vote would be a worse annoyance than the memory an
        // in-flight action's own two live players cost, which § 2.5's budget already accounts for.
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
        // § 1.3: read fresh on every recomposition, but only ever *sampled* at the finger - inside
        // onPress, below - never read again once a gesture is under way. That sample is the pair
        // identity the gesture is aimed at; RankViewModel checks it against whatever is current by
        // the time the call actually goes out.
        val currentPairToken by rememberUpdatedState(state.snapshot?.pairToken)

        Box(
            Modifier
                .fillMaxSize()
                .pointerInput(portrait, widthPx, heightPx, deadBandPx) {
                    // Scoped to this gesture-detector instance, reassigned on every pointer down.
                    // Not `remember`ed - it has no reason to survive a recomposition, only a whole
                    // touch sequence, and this coroutine already loops for the detector's lifetime.
                    var aimedAtToken: String? = null
                    detectTapGestures(
                        // The moment a finger lands is the moment the gesture picks its target -
                        // before Compose has had a chance to recompose underneath it.
                        onPress = { aimedAtToken = currentPairToken },
                        // A tap votes, and only inside the rectangle inset from the outside edges.
                        onTap = { at ->
                            votableSideAt(at, portrait, widthPx, heightPx)
                                ?.let { if (actionable) vote(it, aimedAtToken) }
                        },
                        // A long press is not an accident, so it reaches every corner: the pane
                        // menu is the one thing somebody might deliberately go to an edge for.
                        onLongPress = { at ->
                            sideAt(at, portrait, widthPx, heightPx, deadBandPx)
                                ?.let { side -> paneMenu(side, at, aimedAtToken) }
                        },
                    )
                }
        )
    }
}

/**
 * The share of the screen on which a tap casts a vote: **thirty per cent**.
 *
 * The owner kept voting by accident. A wrong vote is the one mistake this screen can make that
 * costs anything - it moves a rating nobody asked to move, and cancel costs a round trip and his
 * attention - so the target is deliberately much smaller than the picture it belongs to. The rest
 * of each pane still *shows* the photograph; it just does not answer a tap.
 *
 * Expressed as the share of the glass, because that is how it was asked for and how it will be
 * adjusted. One number, one place: if thirty per cent is still too much, this line moves and
 * nothing else. It was half before, and half was still catching his thumb.
 */
internal const val VotingShareOfScreen = 0.30f

/**
 * The live box is **one rectangle centred on the screen**, inset from the four outside edges and
 * crossing the seam - not one box per pane.
 *
 * His words, 2026-09-17: *"the active area is a rectangle inside the screen, with paddings from the
 * edges. It's ok it covers the seam between a pair, the false voting is produced on the edges, not
 * in the center of the screen."* That is a measurement, not a preference: the accidental taps come
 * from the thumb meeting the edge of the glass, and nothing was ever mis-hit in the middle. Insetting
 * from the seam as well - which is what a per-pane box did - shrank the target where it was never
 * wrong, and pushed the live area further from where he actually aims.
 *
 * Equal inset on both axes, hence the square root.
 */
private fun liveFractionPerAxis(share: Float): Float =
    kotlin.math.sqrt(share.coerceIn(0.01f, 1f))

/**
 * Which pane a touch may **vote** for: inside the screen's live rectangle, or nothing.
 *
 * Kept separate from [sideAt] rather than folded into it, because the two questions genuinely
 * differ. "Which pane is this?" is what a long press asks, and it has an answer everywhere on the
 * glass. "May this cast a vote?" is what a tap asks, and outside the live rectangle the answer is no.
 *
 * This subsumes the old edge margin, and deliberately drops the old seam band: one rectangle inset
 * from the four outside edges, crossing the middle, instead of two boxes that each had to be kept
 * clear of the seam as well.
 */
internal fun votableSideAt(
    at: Offset,
    portrait: Boolean,
    width: Float,
    height: Float,
    share: Float = VotingShareOfScreen,
): Side? {
    val live = liveFractionPerAxis(share)
    val marginX = width * (1f - live) / 2f
    val marginY = height * (1f - live) / 2f

    val inside = at.x >= marginX && at.x <= width - marginX &&
        at.y >= marginY && at.y <= height - marginY

    if (!inside) return null

    // Inside the rectangle, which pane the finger is over decides the vote. No dead band at the
    // seam: he said plainly that the seam is not where the accidents happen, and a band there would
    // cut a hole in the middle of the one area he does aim at.
    return sideAt(at, portrait, width, height, deadBand = 0f)
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
 * A17: "exhausted" does not mean the owner has seen everything — it means the engine cannot build a
 * pair from what is left, which on a small or heavily-discarded folder is often just one file. The
 * old copy ("every pair has been seen") told him a story that was not true and pointed him only at
 * "pick another folder", when the documented way back into *this* one is undo. So: the honest
 * sentence, and undo offered first when there is one to take back — leaving is still here, second,
 * for when there genuinely is nowhere further to go.
 */
@Composable
private fun Exhausted(canCancel: Boolean, busy: Boolean, onCancel: () -> Unit, onLeave: () -> Unit) {
    Column(
        Modifier
            .fillMaxSize()
            .windowInsetsPadding(WindowInsets.safeDrawing)
            .padding(32.dp),
        verticalArrangement = Arrangement.spacedBy(10.dp, Alignment.CenterVertically),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Text("Fewer than two files left to compare", color = Color(0xFFECEEF2), textAlign = TextAlign.Center)
        Text(
            "Everything ranked so far is saved.",
            color = Color(0xFF9AA3B2),
            fontSize = 13.sp,
            textAlign = TextAlign.Center,
        )
        if (canCancel) {
            TextButton(onClick = onCancel, enabled = !busy) {
                Text("Take back the last choice", color = Color(0xFF2ECC71))
            }
        }
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

/** How wide the seam's dead band is. Thin enough to never be in the way, wide enough for a thumb. */
private val DeadBand = 44.dp

internal fun Modifier.clickableWhen(enabled: Boolean, onClick: () -> Unit): Modifier =
    this.then(if (enabled) Modifier.clickable(onClick = onClick) else Modifier)
