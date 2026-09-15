package com.rankmaster2.phone.ui.rank

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalLifecycleOwner
import androidx.compose.ui.platform.LocalView
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import com.rankmaster2.phone.media.Rm2Media
import com.rankmaster2.phone.ui.rank.full.FullScreenViewer

/**
 * The ranking screen wired to its view model, plus the three things only a screen can know: how big
 * a pane is, whether the app is in front, and what the back gesture should mean here.
 */
@Composable
fun RankRoute(
    viewModel: RankViewModel,
    media: Rm2Media,
    onLeave: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val state by viewModel.state.collectAsState()

    // Leaving the app stops the videos and re-reads the state on the way back, because anything
    // could have happened on the PC in between - including the session being closed.
    val lifecycleOwner = LocalLifecycleOwner.current
    DisposableEffect(lifecycleOwner) {
        val observer = LifecycleEventObserver { _, event ->
            when (event) {
                Lifecycle.Event.ON_START -> viewModel.onForeground(true)
                Lifecycle.Event.ON_STOP -> viewModel.onForeground(false)
                else -> Unit
            }
        }
        lifecycleOwner.lifecycle.addObserver(observer)
        onDispose { lifecycleOwner.lifecycle.removeObserver(observer) }
    }

    // Back goes back: out of the viewer if it is open, otherwise out of the folder - never out of
    // the app. Leaving closes the session, so the PC does not sit holding a folder nobody is using.
    BackHandler(enabled = true) {
        when {
            state.viewing != null -> viewModel.view(null)
            state.paneMenu != null -> viewModel.closePaneMenu()
            else -> viewModel.leave(onLeave)
        }
    }

    val configuration = LocalConfiguration.current
    val portrait = configuration.screenHeightDp >= configuration.screenWidthDp

    // The pane menu is anchored to where a thumb landed, and after a rotation that point describes
    // somewhere else entirely. Close it rather than leave it pointing at nothing.
    LaunchedEffect(portrait) { viewModel.closePaneMenu() }

    // A video loops for as long as its pair is on screen and nothing is being touched, so the
    // display would otherwise dim and lock in the middle of watching one.
    //
    // The condition is "this pair contains a video", not "a video is playing right now": judging a
    // pair means going back and forth between the two, including full screen and including the
    // still side of a mixed pair, and a screen that locks while he is looking at one half is the
    // same annoyance either way. One owner for the flag, here, rather than one per pane fighting
    // over the same boolean on the same window.
    val view = LocalView.current
    val watchingVideo = state.foreground &&
        (state.left?.isVideo == true || state.right?.isVideo == true)
    DisposableEffect(view, watchingVideo) {
        view.keepScreenOn = watchingVideo
        onDispose { view.keepScreenOn = false }
    }

    BoxWithConstraints(modifier.fillMaxSize()) {
        val panePx = with(LocalDensity.current) {
            paneLongEdgePx(portrait, maxWidth.toPx(), maxHeight.toPx())
        }

        // Warm the next pairs once the current one is on screen (§ 9.5). This is why the second
        // pair appears instantly and the first does not: without it every pair is a fresh download.
        LaunchedEffect(state.snapshot?.pairSeq, state.foreground, panePx) {
            val snapshot = state.snapshot ?: return@LaunchedEffect
            if (!state.foreground || snapshot.warmPairs.isEmpty()) return@LaunchedEffect
            media.prefetcher.warm(snapshot, panePx)
        }

        RankScreen(
            state = state,
            media = media,
            onVote = viewModel::vote,
            onPaneMenu = viewModel::openPaneMenu,
            onClosePaneMenu = viewModel::closePaneMenu,
            onDiscard = viewModel::discard,
            onSpecial = viewModel::special,
            onView = { side -> viewModel.view(side) },
            onCancel = viewModel::cancel,
            onLeave = { viewModel.leave(onLeave) },
            onDismissNotice = viewModel::dismissNotice,
            onDismissProblem = viewModel::dismissProblem,
        )

    }

    // The pair can go while the viewer is open - a resync on the way back to the front, a session
    // the PC closed. Closing the viewer is a state change and belongs in an effect, not in the
    // middle of a composition that is supposed to be describing what is on screen.
    val viewerOrphaned = state.viewing != null && state.pair == null
    LaunchedEffect(viewerOrphaned) {
        if (viewerOrphaned) viewModel.view(null)
    }

    val pair = state.pair
    val viewing = state.viewing
    if (pair != null && viewing != null) {
        FullScreenViewer(
            left = pair.left,
            right = pair.right,
            showing = viewing,
            portrait = portrait,
            media = media,
            onShow = { viewModel.view(it) },
            onDismiss = { viewModel.view(null) },
        )
    }
}

/**
 * The long edge of one pane, in pixels, on a screen [screenWidth] x [screenHeight].
 *
 * The panes split the screen along the seam - stacked in portrait, side by side in landscape - so
 * which half is halved depends on the orientation. This used to be `max(width, height / 2)` in both,
 * which is right in portrait by luck and wrong in landscape: it takes the *whole* screen width for
 * a pane that is only half of it.
 *
 * What that cost was not a soft picture but a prefetch that never hit. The warm-up fetches a URL
 * built from this number and the pane fetches one built from its own measured box; in landscape the
 * two disagreed, so every warm pair was downloaded at one width, cached under that URL, and then
 * downloaded again at another the moment it became the current pair.
 */
internal fun paneLongEdgePx(portrait: Boolean, screenWidth: Float, screenHeight: Float): Int {
    val paneWidth = if (portrait) screenWidth else screenWidth / 2f
    val paneHeight = if (portrait) screenHeight / 2f else screenHeight
    return maxOf(paneWidth, paneHeight).toInt()
}
