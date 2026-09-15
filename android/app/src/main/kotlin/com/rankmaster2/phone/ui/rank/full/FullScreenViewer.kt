package com.rankmaster2.phone.ui.rank.full

import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectHorizontalDragGestures
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawing
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.rankmaster2.phone.media.MediaPane
import com.rankmaster2.phone.media.Rm2Media
import com.rankmaster2.phone.net.MediaRef
import com.rankmaster2.phone.ui.rank.Side

/**
 * One item of the pair, filling the screen, with no vote attached.
 *
 * Looking is not voting. This is where a photograph is actually examined, or a video watched, and
 * a tap here leaves rather than choosing. **Swipe sideways to see the other one** — comparing two
 * pictures properly means going back and forth between them at full size, and having to leave and
 * long-press the other one breaks exactly the comparison being made.
 *
 * Only one item plays at a time, and the panes behind this are stopped while it is open: two
 * players on one video is two hardware decoders on one file.
 */
@Composable
fun FullScreenViewer(
    left: MediaRef,
    right: MediaRef,
    showing: Side,
    media: Rm2Media,
    onShow: (Side) -> Unit,
    onDismiss: () -> Unit,
) {
    val ref = if (showing == Side.LEFT) left else right
    var dragged by remember { mutableFloatStateOf(0f) }

    Box(
        Modifier
            .fillMaxSize()
            .background(Color.Black)
            .pointerInput(showing) {
                detectTapGestures(onTap = { onDismiss() })
            }
            .pointerInput(showing) {
                detectHorizontalDragGestures(
                    onDragEnd = {
                        // A deliberate swipe, not a stray finger: a quarter of the screen.
                        val threshold = size.width / 4f
                        if (dragged <= -threshold) onShow(Side.RIGHT)
                        if (dragged >= threshold) onShow(Side.LEFT)
                        dragged = 0f
                    },
                    onDragCancel = { dragged = 0f },
                    onHorizontalDrag = { _, amount -> dragged += amount },
                )
            },
        contentAlignment = Alignment.Center,
    ) {
        MediaPane(ref = ref, media = media, playing = true)

        // Which of the two is on screen, and that there is another. Two dots, no words.
        Row(
            Modifier
                .align(Alignment.BottomCenter)
                .windowInsetsPadding(WindowInsets.safeDrawing)
                .padding(bottom = 28.dp),
        ) {
            Dot(filled = showing == Side.LEFT)
            Box(Modifier.size(width = 8.dp, height = 1.dp))
            Dot(filled = showing == Side.RIGHT)
        }

        Text(
            text = ref.id,
            color = Color(0x80ECEEF2),
            fontSize = 11.sp,
            modifier = Modifier
                .align(Alignment.BottomCenter)
                .windowInsetsPadding(WindowInsets.safeDrawing)
                .padding(bottom = 10.dp),
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
