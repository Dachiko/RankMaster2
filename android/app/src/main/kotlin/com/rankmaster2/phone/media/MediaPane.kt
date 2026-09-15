package com.rankmaster2.phone.media

import android.view.ViewGroup
import androidx.annotation.OptIn
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.RememberObserver
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.Constraints
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.media3.common.util.UnstableApi
import androidx.media3.ui.AspectRatioFrameLayout
import androidx.media3.ui.PlayerView
import coil.compose.AsyncImage
import coil.request.ImageRequest
import com.rankmaster2.phone.net.MediaRef

/**
 * One pane of the ranking screen: whatever [ref] is, drawn to fit, at the right number of pixels.
 *
 * This is the whole public surface of the media layer as far as a screen is concerned. It takes a
 * `MediaRef` and a size and shows the right thing for its kind; it reports what it is showing
 * through [onState] so the screen can offer "discard this side" when a file has gone.
 *
 * What a tap means is not here, on purpose. This composable installs no gesture of any kind, so
 * whatever the ranking screen wraps it in is the only thing that can consume one.
 */
@Composable
fun MediaPane(
    ref: MediaRef,
    media: Rm2Media,
    modifier: Modifier = Modifier,
    /**
     * Whether a video pane should be running. The screen owns this: it is what stops a pane playing
     * in the background, or both panes playing when only one is in front.
     */
    playing: Boolean = true,
    onState: (MediaPaneState) -> Unit = {},
) {
    BoxWithConstraints(modifier.background(Color.Black), contentAlignment = Alignment.Center) {
        val panePx = paneLongEdgePx(constraints)
        when {
            // § 11.3: the snapshot already told us the file has left the folder. No request to
            // make, and nothing to wait for.
            ref.isMissing -> StatusPane(MediaPaneState.Gone, onState)
            ref.isVideo -> VideoPane(ref, media, playing, onState)
            else -> StillPane(ref, media, panePx, onState)
        }
    }
}

/**
 * The long edge of a pane, in pixels, which is what `w=` is measured against - see [MediaWidths].
 *
 * An unbounded constraint (a pane inside a scrolling column, say) has no pixel count to work from;
 * the server's own default is the honest answer there.
 */
internal fun paneLongEdgePx(constraints: Constraints): Int {
    val width = if (constraints.hasBoundedWidth) constraints.maxWidth else 0
    val height = if (constraints.hasBoundedHeight) constraints.maxHeight else 0
    val longEdge = maxOf(width, height)
    return if (longEdge > 0) longEdge else MediaWidths.DEFAULT
}

@Composable
private fun StillPane(
    ref: MediaRef,
    media: Rm2Media,
    panePx: Int,
    onState: (MediaPaneState) -> Unit,
) {
    val context = LocalContext.current
    val url = media.stillUrl(ref, panePx)
    var state by remember(ref.id, url) { mutableStateOf<MediaPaneState>(MediaPaneState.Loading) }

    fun report(next: MediaPaneState) {
        state = next
        onState(next)
    }

    if (url == null) {
        StatusPane(MediaPaneState.Gone, onState)
        return
    }

    AsyncImage(
        model = remember(url) { ImageRequest.Builder(context).data(url).build() },
        contentDescription = ref.id,
        imageLoader = media.imageLoader,
        modifier = Modifier.fillMaxSize(),
        // Fit, never crop: the picture is being judged, and a crop would be judging a different
        // picture. The decoded bitmap is what is laid out - never the width that was requested,
        // which the server is free to have undershot (§ 12.3).
        contentScale = ContentScale.Fit,
        onLoading = { report(MediaPaneState.Loading) },
        onSuccess = { report(MediaPaneState.Loaded) },
        onError = { report(MediaFailures.stateOf(it.result.throwable)) },
    )

    if (state !is MediaPaneState.Loaded) StatusPane(state, onState = {})
}

@OptIn(UnstableApi::class)
@Composable
private fun VideoPane(
    ref: MediaRef,
    media: Rm2Media,
    playing: Boolean,
    onState: (MediaPaneState) -> Unit,
) {
    val url = media.videoUrl(ref)
    var state by remember(ref.id, url) { mutableStateOf<MediaPaneState>(MediaPaneState.Loading) }

    if (url == null) {
        StatusPane(MediaPaneState.Gone, onState)
        return
    }

    // Keyed on the ref: a new pair builds a new player and releases the old one in the same breath.
    // This is the release that stops two videos on screen becoming an OutOfMemory - and it is a
    // RememberObserver rather than a DisposableEffect so that a composition which is started and
    // then abandoned releases its decoder too, which is the case a DisposableEffect never sees.
    val slot = remember(ref.id, url) {
        VideoSlot(media.players.create(ref) { next ->
            state = next
            onState(next)
        })
    }
    val video = slot.video

    LaunchedEffect(video, playing) {
        if (playing) video?.start() else video?.stop()
    }

    if (video == null) {
        StatusPane(MediaPaneState.Gone, onState)
        return
    }

    AndroidView(
        modifier = Modifier.fillMaxSize(),
        factory = { context ->
            PlayerView(context).apply {
                // No controller, no transport bar, nothing that can swallow a tap that was meant
                // to be a vote. See Rm2VideoPlayers.
                useController = false
                setShutterBackgroundColor(android.graphics.Color.BLACK)
                resizeMode = AspectRatioFrameLayout.RESIZE_MODE_FIT
                layoutParams = ViewGroup.LayoutParams(
                    ViewGroup.LayoutParams.MATCH_PARENT,
                    ViewGroup.LayoutParams.MATCH_PARENT,
                )
            }
        },
        update = { view -> view.player = video.player },
        onReset = { view -> view.player = null },
        // onReset fires when the view is recycled; onRelease when it is thrown away. A PlayerView
        // that leaves the screen still holding a player keeps its surface, and the next video finds
        // the decoder occupied.
        onRelease = { view -> view.player = null },
    )

    if (state !is MediaPaneState.Loaded) StatusPane(state, onState = {})
}

/** Holds a player for exactly as long as the composition that asked for it. */
private class VideoSlot(val video: Rm2Video?) : RememberObserver {
    override fun onRemembered() = Unit
    override fun onForgotten() { video?.release() }
    override fun onAbandoned() { video?.release() }
}

/**
 * What the pane says when it is not showing a picture.
 *
 * Plain words, no icon, no retry button: the screen decides what is on offer, and for a file that
 * has gone the only sane offer - discarding that side - belongs on the screen's own controls, not
 * buried in a pane the user is about to tap.
 */
@Composable
private fun StatusPane(state: MediaPaneState, onState: (MediaPaneState) -> Unit) {
    LaunchedEffect(state) { onState(state) }

    Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
        when (state) {
            is MediaPaneState.Loading -> CircularProgressIndicator(color = Color(0xFF6B7280))
            is MediaPaneState.Loaded -> Unit
            is MediaPaneState.Gone -> Message("This file is no longer in the folder.")
            is MediaPaneState.Undecodable -> Message("This file will not open.")
            is MediaPaneState.Unavailable -> Message("Could not load this one.")
        }
    }
}

@Composable
private fun Message(text: String) {
    Text(
        text = text,
        color = Color(0xFF9CA3AF),
        textAlign = TextAlign.Center,
        modifier = Modifier.padding(24.dp),
    )
}
