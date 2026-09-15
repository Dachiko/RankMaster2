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
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalLifecycleOwner
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import com.rankmaster2.phone.media.Rm2Media
import com.rankmaster2.phone.ui.rank.full.FullScreenViewer
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

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
            state.overflowOpen -> viewModel.closeOverflow()
            else -> viewModel.leave(onLeave)
        }
    }

    BoxWithConstraints(modifier.fillMaxSize()) {
        val panePx = with(LocalDensity.current) {
            // A pane is half the screen along the seam, and the server sizes by the long edge.
            maxOf(maxWidth.toPx(), maxHeight.toPx() / 2f).toInt()
        }

        // Warm the next pairs once the current one is on screen (§ 9.5). This is why the second
        // pair appears instantly and the first does not: without it every pair is a fresh download.
        LaunchedEffect(state.snapshot?.pairSeq, state.foreground, panePx) {
            val snapshot = state.snapshot ?: return@LaunchedEffect
            if (!state.foreground || snapshot.warmPairs.isEmpty()) return@LaunchedEffect
            withContext(Dispatchers.IO) { media.prefetcher.warm(snapshot, panePx) }
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
            onSkip = viewModel::skip,
            onSave = viewModel::save,
            onCancel = viewModel::cancel,
            onOpenOverflow = viewModel::openOverflow,
            onCloseOverflow = viewModel::closeOverflow,
            onLeave = { viewModel.leave(onLeave) },
            onDismissNotice = viewModel::dismissNotice,
            onDismissProblem = viewModel::dismissProblem,
        )

        state.viewing?.let { side ->
            val pair = state.pair
            if (pair == null) {
                viewModel.view(null)
            } else {
                FullScreenViewer(
                    left = pair.left,
                    right = pair.right,
                    showing = side,
                    media = media,
                    onShow = { viewModel.view(it) },
                    onDismiss = { viewModel.view(null) },
                )
            }
        }
    }
}
