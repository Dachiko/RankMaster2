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
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalLifecycleOwner
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.unit.Constraints
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import com.rankmaster2.phone.media.Rm2Media
import com.rankmaster2.phone.media.paneLongEdgePx

/**
 * The review screen wired to its view model, plus the things only a screen can know: whether the
 * app is in front, what back means here, and how big a picture is (for prefetching).
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

    // Back closes the menu if it is open; otherwise it leaves - closing the session so the PC does
    // not sit holding a folder nobody is using, and returning to the folder list. Everything
    // discarded is already on its way to the PC and is sent before the close.
    BackHandler(enabled = true) {
        if (state.menuFor != null) viewModel.closeMenu() else viewModel.leave(onLeave)
    }

    // The menu is anchored to where a thumb or a button was, and after a rotation that point
    // describes somewhere else entirely. Close it rather than leave it pointing at nothing.
    val configuration = LocalConfiguration.current
    val portrait = configuration.screenHeightDp >= configuration.screenWidthDp
    LaunchedEffect(portrait) { viewModel.closeMenu() }

    // A video loops for as long as it is on screen and nothing is being touched, so the display
    // would otherwise dim and lock in the middle of watching one.
    val view = LocalView.current
    val watchingVideo = state.foreground && state.current?.isVideo == true
    DisposableEffect(view, watchingVideo) {
        view.keepScreenOn = watchingVideo
        onDispose { view.keepScreenOn = false }
    }

    BoxWithConstraints(modifier.fillMaxSize()) {
        // The item is drawn into this very box, edge to edge, so the pane's own width is worked out
        // from these same constraints: the same `w=`, the same URL, a cache hit.
        val panePx = reviewPrefetchPx(constraints)

        // Warm the next few stills once the current item is up - the cheapest work in the app, and
        // it competes for the same Wi-Fi, so it starts only after the picture is showing. Videos
        // are skipped by the prefetcher itself; they stream, as in ranking. Going back with Cancel
        // needs nothing extra: what was just on screen is already in the disk cache.
        val upcoming = state.upcoming(PREFETCH_AHEAD)
        LaunchedEffect(state.current?.id, upcoming.map { it.id }, state.foreground, panePx) {
            if (!state.foreground || upcoming.isEmpty()) return@LaunchedEffect
            media.prefetcher.warmItems(upcoming, panePx)
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
            onOpenMenu = viewModel::openMenu,
            onCloseMenu = viewModel::closeMenu,
            onDiscardFromMenu = viewModel::discardFromMenu,
        )
    }
}

/** How many items ahead are warmed. */
internal const val PREFETCH_AHEAD = 3

/**
 * The long edge to prefetch at for a review screen measured at [constraints]: exactly what the item's
 * own pane computes from the same box, because the item fills the screen. One function, so the two
 * cannot disagree.
 */
internal fun reviewPrefetchPx(constraints: Constraints): Int = paneLongEdgePx(constraints)
