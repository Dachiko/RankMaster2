package com.rankmaster2.phone.ui.rank

import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalLifecycleOwner
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import com.rankmaster2.phone.media.Rm2Media
import com.rankmaster2.phone.ui.rank.full.FullScreenViewer

/**
 * The ranking screen wired to its view model, plus the two things only a screen can know: whether
 * the app is in front, and which pane the owner asked to see full screen.
 */
@Composable
fun RankRoute(
    viewModel: RankViewModel,
    media: Rm2Media,
    onLeave: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val state by viewModel.state.collectAsState()
    var viewing by remember { mutableStateOf<Side?>(null) }

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

    RankScreen(
        state = state,
        media = media,
        onVote = viewModel::vote,
        onPaneMenu = viewModel::openPaneMenu,
        onClosePaneMenu = viewModel::closePaneMenu,
        onDiscard = viewModel::discard,
        onSpecial = viewModel::special,
        onView = { side -> viewModel.closePaneMenu(); viewing = side },
        onSkip = viewModel::skip,
        onSave = viewModel::save,
        onCancel = viewModel::cancel,
        onOpenOverflow = viewModel::openOverflow,
        onCloseOverflow = viewModel::closeOverflow,
        onLeave = onLeave,
        onDismissNotice = viewModel::dismissNotice,
        onDismissProblem = viewModel::dismissProblem,
        modifier = modifier,
    )

    viewing?.let { side ->
        val ref = if (side == Side.LEFT) state.left else state.right
        if (ref == null) {
            viewing = null
        } else {
            FullScreenViewer(ref = ref, media = media, onDismiss = { viewing = null })
        }
    }
}
