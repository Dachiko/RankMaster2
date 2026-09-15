package com.rankmaster2.phone.ui.rank.full

import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawing
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.pager.HorizontalPager
import androidx.compose.foundation.pager.PagerDefaults
import androidx.compose.foundation.pager.VerticalPager
import androidx.compose.foundation.pager.rememberPagerState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.snapshotFlow
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.rankmaster2.phone.media.MediaPane
import com.rankmaster2.phone.media.Rm2Media
import com.rankmaster2.phone.net.MediaRef
import com.rankmaster2.phone.ui.rank.Side

/**
 * One item of the pair, filling the screen, with no vote attached.
 *
 * Looking is not voting: a tap here leaves rather than choosing. Swiping moves to the other one,
 * **along the axis the panes are laid out on** — portrait stacks them, so the top picture's partner
 * is below it and the gesture is downward; landscape puts them side by side, so it is sideways. A
 * gesture that disagrees with where the thing actually sits is a second thing to learn.
 *
 * Built on a `Pager` rather than a drag detector. The first attempt was hand-rolled with a
 * quarter-screen threshold and no animation, so nothing followed the finger and nothing moved until
 * the drag was already over — which is what "slow" meant. A pager follows the finger, takes a flick
 * rather than a journey, and snaps.
 *
 * It fills the window rather than whatever box contained it, which is what left the pair showing
 * through after a rotation.
 */
@Composable
fun FullScreenViewer(
    left: MediaRef,
    right: MediaRef,
    showing: Side,
    portrait: Boolean,
    media: Rm2Media,
    onShow: (Side) -> Unit,
    onDismiss: () -> Unit,
) {
    val pages = listOf(left, right)
    val state = rememberPagerState(initialPage = if (showing == Side.LEFT) 0 else 1) { pages.size }

    // Tell the screen behind which one is showing, so it knows what "back" and the panes should do.
    LaunchedEffect(state) {
        snapshotFlow { state.settledPage }.collect { page ->
            onShow(if (page == 0) Side.LEFT else Side.RIGHT)
        }
    }

    val fling = PagerDefaults.flingBehavior(
        state = state,
        // Short enough not to be waited on, long enough to see which way it went.
        snapAnimationSpec = tween(durationMillis = 180),
    )

    Box(
        Modifier
            .fillMaxSize()
            .background(Color.Black)
            .pointerInput(Unit) { detectTapGestures(onTap = { onDismiss() }) },
    ) {
        val content: @Composable (Int) -> Unit = { page ->
            Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                // Only the settled page plays: two decoders on two 4K files is how this app died.
                MediaPane(
                    ref = pages[page],
                    media = media,
                    playing = state.settledPage == page,
                )
            }
        }

        if (portrait) {
            VerticalPager(state = state, flingBehavior = fling, modifier = Modifier.fillMaxSize()) {
                content(it)
            }
        } else {
            HorizontalPager(state = state, flingBehavior = fling, modifier = Modifier.fillMaxSize()) {
                content(it)
            }
        }

        Row(
            Modifier
                .align(Alignment.BottomCenter)
                .windowInsetsPadding(WindowInsets.safeDrawing)
                .padding(bottom = 26.dp),
        ) {
            Dot(filled = state.currentPage == 0)
            Box(Modifier.size(width = 8.dp, height = 1.dp))
            Dot(filled = state.currentPage == 1)
        }

        // A media id is a filename (SERVER_SPEC.md section 11.1), and filenames from a real
        // library are long. Unbounded, this wrapped into a paragraph of grey text across the
        // bottom of the photograph it is naming.
        Text(
            text = pages[state.currentPage].id,
            color = Color(0x80ECEEF2),
            fontSize = 11.sp,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            textAlign = TextAlign.Center,
            modifier = Modifier
                .align(Alignment.BottomCenter)
                .windowInsetsPadding(WindowInsets.safeDrawing)
                .padding(horizontal = 24.dp)
                .padding(bottom = 8.dp),
        )
    }
}

@Composable
private fun Dot(filled: Boolean) {
    Box(
        Modifier
            .size(6.dp)
            .clip(CircleShape)
            .background(if (filled) Color(0xCCECEEF2) else Color(0x40ECEEF2))
    )
}
