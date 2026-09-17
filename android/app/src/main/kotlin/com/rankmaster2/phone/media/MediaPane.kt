package com.rankmaster2.phone.media

import android.view.SurfaceView
import android.view.ViewGroup
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
import androidx.compose.runtime.key
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.Constraints
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import coil.compose.AsyncImage
import coil.request.ImageRequest
import com.rankmaster2.phone.net.MediaRef

/**
 * One pane of the ranking screen: whatever [ref] is, drawn to fit, at the right number of pixels.
 *
 * This is the whole public surface of the media layer as far as a screen is concerned. It takes a
 * `MediaRef` and a size and shows the right thing for its kind.
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
) {
    BoxWithConstraints(modifier.background(Color.Black), contentAlignment = Alignment.Center) {
        val panePx = paneLongEdgePx(constraints)
        when {
            // § 11.3: the snapshot already told us the file has left the folder. No request to
            // make, and nothing to wait for.
            ref.isMissing -> StatusPane(MediaPaneState.Gone)
            ref.isVideo -> VideoPane(ref, media, playing)
            else -> StillPane(ref, media, panePx)
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
) {
    val context = LocalContext.current
    val url = media.stillUrl(ref, panePx)
    var state by remember(ref.id, url) { mutableStateOf<MediaPaneState>(MediaPaneState.Loading) }

    if (url == null) {
        StatusPane(MediaPaneState.Gone)
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
        onLoading = { state = MediaPaneState.Loading },
        onSuccess = { state = MediaPaneState.Loaded },
        onError = { state = MediaFailures.stateOf(it.result.throwable) },
    )

    if (state !is MediaPaneState.Loaded) StatusPane(state)
}

@Composable
private fun VideoPane(
    ref: MediaRef,
    media: Rm2Media,
    playing: Boolean,
) {
    val url = media.videoUrl(ref)
    var state by remember(ref.id, url) { mutableStateOf<MediaPaneState>(MediaPaneState.Loading) }

    if (url == null) {
        StatusPane(MediaPaneState.Gone)
        return
    }

    // One slot for as long as this pane is in the composition - one per side, since the ranking
    // screen calls this composable once for the left pane and once for the right, and Compose's
    // own per-call-site memory keeps each pane's `remember` separate from the other's. A pair
    // change asks the *same* slot for the next player, and Rm2VideoPlayers.Slot.acquire releases
    // what it already holds before building the next one (A12). It is a RememberObserver rather
    // than a DisposableEffect so that a composition which is started and then abandoned releases
    // its decoder too, which is the case a DisposableEffect never sees.
    val holder = remember(media) { VideoSlotHolder(media.players.newSlot()) }

    // § 2.5 (second audit): keyed on `playing` too, and gone through `Slot.sync` rather than
    // `acquire` directly. A pane that is not playing releases its player outright instead of
    // holding it paused - see `sync`'s own doc for why "keep the buffer, just don't play" stopped
    // being safe once the full-screen viewer started building its own player for the same file on
    // top of the two this screen was already holding. `playing` only changes when the viewer opens
    // or closes, or the app is backgrounded - never per vote - so this is not a rebuild-per-vote.
    val video = remember(ref.id, url, playing) {
        holder.slot.sync(ref, playing) { next -> state = next }
    }

    LaunchedEffect(video, playing) { video?.setPlaying(playing) }

    if (!playing) {
        // Not in front means black - and, since § 2.5, the player is actually gone too: `sync`
        // released it above. The pane still draws as "loading" rather than anything more alarming,
        // because from here this looks exactly like a normal pair still warming up.
        StatusPane(MediaPaneState.Loading)
        return
    }

    if (video == null) {
        StatusPane(MediaPaneState.Gone)
        return
    }

    // The shape of the picture, owned by Compose and by nothing else.
    //
    // The decoder reports it with the first frame, which is well after this pane was first
    // measured; reading it as state here makes its arrival a recomposition and a re-measure. What
    // is drawn into the box is a bare `SurfaceView` that fills it: no `PlayerView`, no
    // `AspectRatioFrameLayout` with a second opinion about the shape, no resize mode. A decoder
    // scales its frames to whatever surface it is given, so the surface being the right shape is
    // the whole of the picture being the right shape, and one owner of that is one fewer thing to
    // disagree.
    val ratio = video.aspectRatio.value

    // One surface per shape, created at its final size and never resized in place.
    //
    // Before the shape is known there has to be a surface - the decoder cannot produce the first
    // frame, and so cannot report a shape, without one - so a provisional full-pane surface is
    // created under the cover below. When the shape arrives, this pane does not resize that
    // surface into the right box; `key(ratio)` throws it away and creates a new one whose very
    // first layout is the right size. That is exactly what a rotation does to a pane, and a
    // rotation is the one thing the owner has reported as putting a wrong pane right (BUGS.md § 1,
    // and his report of 2026-09-17 where turning the phone moved the fault from one pane to the
    // other and a second turn cleared it - each turn rebuilds both surfaces). Everything above the
    // surface - Compose's box, the View's rectangle - is proven right by VideoPaneLayoutTest in
    // every orientation; what a live SurfaceView's *surface* does when its View is resized under
    // it is decided by the compositor, which nothing on a build machine can see, and which this
    // rule simply never asks to do anything.
    key(ratio) {
        AndroidView(
            modifier = (if (ratio != null) Modifier.aspectRatio(ratio) else Modifier.fillMaxSize())
                .testTag(MediaPaneTags.VIDEO),
            factory = { context ->
                SurfaceView(context).apply {
                    layoutParams = ViewGroup.LayoutParams(
                        ViewGroup.LayoutParams.MATCH_PARENT,
                        ViewGroup.LayoutParams.MATCH_PARENT,
                    )
                }
            },
            update = { view -> video.showOn(view) },
            onReset = { view -> video.hideFrom(view) },
            // onReset fires when the view is recycled; onRelease when it is thrown away. A surface
            // that leaves the screen still attached to a player keeps the decoder's output, and
            // the next surface finds it occupied.
            onRelease = { view -> video.hideFrom(view) },
        )
    }

    // The cover: opaque, the pane's full size, and there until the picture underneath is right.
    //
    // Until the decoder has reported the shape, the surface is the provisional full-pane one and
    // the frames the decoder scales into it are the wrong shape by construction - `RESIZE_MODE_FIT`
    // never protected against that, because a surface that is the wrong shape is a picture that is
    // the wrong shape, whatever a frame layout around it intended. So nothing of the picture is
    // shown until its shape is known *and* the player has reached ready (which, for a player with
    // a real surface, it only does once it has rendered a frame). What shows instead is the same
    // spinner or message the pane would show anyway.
    if (ratio == null || state !is MediaPaneState.Loaded) {
        Box(Modifier.fillMaxSize().background(Color.Black).testTag(MediaPaneTags.COVER)) {
            StatusPane(if (state is MediaPaneState.Loaded) MediaPaneState.Loading else state)
        }
    }

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

/**
 * Test tags on the two things a video pane is made of, so a layout test can find them in the
 * semantics tree: the box the picture is drawn in, and the cover that hides it until it is right.
 */
internal object MediaPaneTags {
    const val VIDEO = "media-pane/video"
    const val COVER = "media-pane/cover"
}

/** Keeps one pane's video slot alive for exactly as long as the composition that asked for it. */
private class VideoSlotHolder(val slot: Rm2VideoPlayers.Slot) : RememberObserver {
    override fun onRemembered() = Unit
    override fun onForgotten() = slot.release()
    override fun onAbandoned() = slot.release()
}

/**
 * What the pane says when it is not showing a picture.
 *
 * Plain words, no icon, no retry button: the screen decides what is on offer, and for a file that
 * has gone the only sane offer - discarding that side - belongs on the screen's own controls, not
 * buried in a pane the user is about to tap.
 */
@Composable
private fun StatusPane(state: MediaPaneState, name: String? = null) {
    Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
        when (state) {
            is MediaPaneState.Loading -> CircularProgressIndicator(color = Color(0xFF6B7280))
            is MediaPaneState.Loaded -> Unit
            // Each of these names the one thing that can be done about it, and that one thing is
            // always the same gesture: press and hold this side. Saying so is what turns a pane
            // that has gone blank into a pane with an answer in it.
            is MediaPaneState.Gone ->
                Message(named(name, "is not in the folder any more", "This file is not in the folder any more") +
                    ".\n\nPress and hold here to drop it.")
            // Named, and blaming neither the phone nor - without evidence - the file. The old text
            // read "This phone cannot open this file", which was wrong twice over: the phone never
            // receives the file, and the usual cause was the PC running out of decode memory.
            is MediaPaneState.Undecodable ->
                Message(named(name, "could not be read as a picture", "This file could not be read as a picture") +
                    ".\n\nPress and hold here to drop it.")
            // Nothing is wrong with this picture and nothing is wrong with this phone, so it offers
            // no discard: throwing away a good photograph because the PC was briefly busy is the
            // one outcome this pane must not invite.
            is MediaPaneState.NoRoomOnThePc ->
                Message(named(name, "could not be prepared just now", "This one could not be prepared just now") +
                    " — the PC had no memory free for it.\n\nThe file is fine. It will load next time it comes up.")
            is MediaPaneState.Unavailable ->
                Message("This one did not arrive from the PC.\n\nIt will try again on the next pair.")
        }
    }
}

/** The filename when the pane knows it, because "which one?" is the owner's first question. */
private fun named(name: String?, tail: String, fallback: String): String =
    if (name.isNullOrBlank()) fallback else "$name $tail"

@Composable
private fun Message(text: String) {
    Text(
        text = text,
        color = Color(0xFF9CA3AF),
        textAlign = TextAlign.Center,
        modifier = Modifier.padding(24.dp),
    )
}
