package com.rankmaster2.phone.ui.review

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
import androidx.compose.ui.platform.LocalView
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import com.rankmaster2.phone.media.Rm2Media
import com.rankmaster2.phone.net.Pair as MediaPair

/**
 * The review screen wired to its view model, plus the three things only a screen can know: whether
 * the app is in front, what back means here, and how big a picture is (for prefetching).
 */
@Composable
fun ReviewRoute(
    viewModel: ReviewViewModel,
    media: Rm2Media,
    onLeave: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val state by viewModel.state.collectAsState()

    // Leaving the app stops the video and re-reads the list on the way back, because anything could
    // have happened on the PC in between - including the folder being renamed or the session closed.
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

    // Back leaves: it closes the session so the PC does not sit holding a folder nobody is using,
    // and returns to the folder list. Everything swiped is already on its way to the PC and is sent
    // before the close.
    BackHandler(enabled = true) { viewModel.leave(onLeave) }

    // A video loops for as long as it is on screen and nothing is being touched, so the display
    // would otherwise dim and lock in the middle of watching one.
    val view = LocalView.current
    val watchingVideo = state.foreground && state.current?.isVideo == true
    DisposableEffect(view, watchingVideo) {
        view.keepScreenOn = watchingVideo
        onDispose { view.keepScreenOn = false }
    }

    BoxWithConstraints(modifier.fillMaxSize()) {
        // The long edge of the screen. The picture's own box is a little smaller (the controls take
        // some), but the server's widths come in steps (360, 540, 720, 1080, 1440, 2160), so this
        // lands on the same step nearly always, and then the prefetch is a cache hit.
        val panePx = with(LocalDensity.current) { maxOf(maxWidth.toPx(), maxHeight.toPx()).toInt() }

        // Warm the next one or two stills once the current item is up - the cheapest work in the
        // app, and it competes for the same Wi-Fi, so it starts only after the picture is showing.
        // Videos are skipped by the prefetcher itself; they stream.
        val upcoming = state.upcoming(PREFETCH_AHEAD)
        LaunchedEffect(state.current?.id, upcoming.map { it.id }, state.foreground, panePx) {
            if (!state.foreground || upcoming.isEmpty()) return@LaunchedEffect
            val pair = MediaPair(upcoming.first(), upcoming.last())
            media.prefetcher.warm(listOf(pair), panePx)
        }

        ReviewScreen(
            state = state,
            media = media,
            onKeep = viewModel::keep,
            onDiscard = viewModel::discard,
            onCancel = viewModel::cancel,
            onRestart = viewModel::restart,
            onRetry = viewModel::retry,
            onLeave = { viewModel.leave(onLeave) },
            onDismissProblem = viewModel::dismissProblem,
        )
    }
}

private const val PREFETCH_AHEAD = 2
