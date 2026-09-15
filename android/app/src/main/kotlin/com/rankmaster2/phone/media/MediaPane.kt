package com.rankmaster2.phone.media

import android.view.ViewGroup
import androidx.annotation.OptIn
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
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
import androidx.compose.ui.draw.drawBehind
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

    // Keyed on the ref *and on whether this pane should exist at all*: `playing = false` releases
    // the player rather than pausing it. Pausing keeps the buffer - up to megabytes of a 4K file -
    // and the whole reason a covered pane stops is to give that memory back.
    // Keyed on the ref: a new pair builds a new player and releases the old one in the same breath.
    // This is the release that stops two videos on screen becoming an OutOfMemory - and it is a
    // RememberObserver rather than a DisposableEffect so that a composition which is started and
    // then abandoned releases its decoder too, which is the case a DisposableEffect never sees.
    val slot = remember(ref.id, url, playing) {
        if (!playing) {
            VideoSlot(null)
        } else {
            VideoSlot(media.players.create(ref) { next ->
                state = next
                onState(next)
            })
        }
    }
    val video = slot.video

    LaunchedEffect(video) { video?.start() }

    if (!playing) {
        // Stopped means gone, not paused. The pane stays black until it is in front again.
        StatusPane(MediaPaneState.Loading, onState = {})
        return
    }

    if (video == null) {
        StatusPane(MediaPaneState.Gone, onState)
        return
    }

    // The shape of the picture, owned by Compose.
    //
    // The player reports it once the first frame is decoded, which is well after this pane was
    // first measured. Reading it as state here means the arrival of the true shape is a
    // recomposition and therefore a re-measure - so the box the video is drawn into is the right
    // shape from the moment anyone could know what the right shape is, and stays right across a
    // rotation without anything being asked to look again.
    //
    // Until then the pane fills its box, and `RESIZE_MODE_FIT` inside it means the worst this can
    // ever look is a picture drawn smaller than it could be. It is never stretched.
    val ratio = video.aspectRatio.value

    AndroidView(
        modifier = if (ratio != null) Modifier.aspectRatio(ratio) else Modifier.fillMaxSize(),
        factory = { context ->
            PlayerView(context).apply {
                // No controller, no transport bar, nothing that can swallow a tap that was meant
                // to be a vote. See Rm2VideoPlayers.
                useController = false
                setShutterBackgroundColor(android.graphics.Color.BLACK)
                // FIT, always, and never FILL.
                //
                // The box around this view is already the picture's shape, so FIT has nothing
                // left to letterbox and costs nothing. What it buys is that the one remaining way
                // to be wrong stays harmless: if the ratio Compose was given ever disagreed with
                // the picture, FIT draws it *small* and correctly proportioned, while FILL would
                // stretch it - which is the bug this whole change exists to remove, on a narrower
                // path. A safety net that costs nothing stays.
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

    // The mark. A red dot in the corner of a video that cannot hold its frame rate, so the limit
    // being hit is something the owner *sees*, on his own files, in the middle of ranking - rather
    // than something anyone has to go and measure.
    if (video.struggling.value) {
        Box(Modifier.fillMaxSize().padding(10.dp), contentAlignment = Alignment.TopEnd) {
            Box(
                Modifier.size(9.dp).drawBehind {
                    drawCircle(Color(0x99000000), radius = size.minDimension / 2f + 1.5f)
                    drawCircle(Color(0xCCE24A4A), radius = size.minDimension / 2f)
                }
            )
        }
    }
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
            // Each of these names the one thing that can be done about it, and that one thing is
            // always the same gesture: press and hold this side. Saying so is what turns a pane
            // that has gone blank into a pane with an answer in it.
            is MediaPaneState.Gone ->
                Message("This file is not in the folder any more.\n\nPress and hold here to drop it.")
            is MediaPaneState.Undecodable ->
                Message("This phone cannot open this file.\n\nPress and hold here to drop it.")
            is MediaPaneState.Unavailable ->
                Message("This one did not arrive from the PC.\n\nIt will try again on the next pair.")
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
