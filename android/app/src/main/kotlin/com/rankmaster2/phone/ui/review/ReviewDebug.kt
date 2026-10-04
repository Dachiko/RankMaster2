package com.rankmaster2.phone.ui.review

import android.content.Context
import android.os.Build
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawing
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.rankmaster2.phone.media.MediaDebugProbe
import com.rankmaster2.phone.net.MediaRef
import kotlinx.coroutines.delay

/**
 * The review screen's debug overlay: a panel of text that says what the video path is doing, for
 * a phone nobody here can hold. Opened from the menu's "Debug info" row, which also copies the
 * text; the Copy button copies it again.
 *
 * It is observation only. It reads [MediaDebugProbe] and the screen's own state and changes
 * nothing about what is drawn or played.
 */

/** Where and on what the app is running. */
internal data class DebugEnv(
    val versionName: String,
    val versionCode: Long,
    val device: String,
    val androidRelease: String,
    val sdkInt: Int,
)

internal fun debugEnv(context: Context): DebugEnv {
    val info = runCatching { context.packageManager.getPackageInfo(context.packageName, 0) }.getOrNull()
    return DebugEnv(
        versionName = info?.versionName ?: "?",
        versionCode = info?.longVersionCode ?: -1L,
        device = "${Build.MANUFACTURER} ${Build.MODEL}",
        androidRelease = Build.VERSION.RELEASE ?: "?",
        sdkInt = Build.VERSION.SDK_INT,
    )
}

/** What the screen itself knows, apart from the media pane. */
internal data class DebugScreen(
    val item: MediaRef?,
    val phase: String,
    val playing: Boolean,
    val foreground: Boolean,
    val index: Int,
    val count: Int,
)

/**
 * The whole text, as shown and as copied. Pure: every line of it comes in through the arguments, so
 * the format can be tested without a screen. [mediaLines] are the video's (or still's) state lines
 * and [events] its event log, oldest first.
 */
internal fun reviewDebugText(
    env: DebugEnv,
    screen: DebugScreen,
    mediaTitle: String,
    mediaLines: List<String>,
    events: List<String>,
): String = buildString {
    appendLine("Rank Master review debug")
    appendLine("app: ${env.versionName} (code ${env.versionCode})")
    appendLine("device: ${env.device}, Android ${env.androidRelease} (SDK ${env.sdkInt})")
    val item = screen.item
    if (item == null) {
        appendLine("item: none")
    } else {
        appendLine("item: id=${item.id} kind=${item.kind} sizeBytes=${item.sizeBytes} mediaVersion=${item.mediaVersion}")
    }
    appendLine(
        "review: phase=${screen.phase} playing=${screen.playing} foreground=${screen.foreground} " +
            "index=${screen.index}/${screen.count}",
    )
    appendLine("--- $mediaTitle ---")
    mediaLines.forEach { appendLine(it) }
    appendLine("--- events (last ${events.size}) ---")
    if (events.isEmpty()) appendLine("(none)") else events.forEach { appendLine(it) }
}.trimEnd()

/** Reads the probe for the current item and builds the text. Call it where states may be read. */
internal fun reviewDebugText(env: DebugEnv, screen: DebugScreen, probe: MediaDebugProbe): String {
    val item = screen.item
    return when {
        item == null -> reviewDebugText(env, screen, "nothing on screen", emptyList(), emptyList())
        item.isVideo -> {
            val video = probe.video
            if (video == null) {
                reviewDebugText(
                    env, screen, "video",
                    listOf("no video diagnostics: no player is attached (pane not playing, or no URL)"),
                    emptyList(),
                )
            } else {
                reviewDebugText(env, screen, "video", video.stateLines(), video.eventLines())
            }
        }
        else -> reviewDebugText(
            env, screen, "still",
            listOf("url: ${probe.stillUrl ?: "none"}", "coil/pane state: ${probe.stillState ?: "unknown"}"),
            emptyList(),
        )
    }
}

/**
 * The panel itself: small monospace text on a dark, translucent card, with a Copy button.
 *
 * Placed so that it does not take taps from the live area (the centred rectangle ranking and
 * review both use, `ui.insideLiveArea`): in portrait it is the strip above that rectangle, in
 * landscape the strip to its left. Only the panel's own scroll and its Copy button take touches;
 * every other pixel of the screen is untouched.
 */
@Composable
internal fun ReviewDebugOverlay(
    screen: DebugScreen,
    probe: MediaDebugProbe,
    onCopy: (String) -> Unit,
    modifier: Modifier = Modifier,
) {
    val context = LocalContext.current
    val env = remember(context) { debugEnv(context) }

    // The numbers that only exist "now" (position, buffered) need a clock; events bump their own
    // state and show at once.
    var tick by remember { mutableIntStateOf(0) }
    LaunchedEffect(Unit) {
        while (true) {
            delay(500)
            tick++
        }
    }
    // Read both, so that either one changing recomposes the text.
    val liveness = tick + (probe.video?.revision?.intValue ?: 0)
    val text = remember(liveness, screen, probe.video, probe.stillUrl, probe.stillState) {
        reviewDebugText(env, screen, probe)
    }

    var copied by remember { mutableStateOf(false) }
    LaunchedEffect(copied) {
        if (copied) {
            delay(1_200)
            copied = false
        }
    }

    BoxWithConstraints(modifier) {
        val portrait = maxHeight >= maxWidth
        // The dead margin around the live rectangle: 22.6% of each axis.
        val bandHeight = maxHeight * 0.226f
        val bandWidth = maxWidth * 0.226f
        val panel = if (portrait) {
            // Leave the menu dots' corner free.
            Modifier.fillMaxWidth().height(bandHeight).padding(end = 56.dp)
        } else {
            Modifier.width(bandWidth).fillMaxHeight()
        }
        Column(
            panel
                .align(Alignment.TopStart)
                .background(Color(0xDD0B0D10))
                .windowInsetsPadding(WindowInsets.safeDrawing)
                .padding(horizontal = 6.dp, vertical = 4.dp),
        ) {
            Row(
                Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text("DEBUG", color = Color(0xFF9CA3AF), fontSize = 10.sp, fontFamily = FontFamily.Monospace)
                Text(
                    text = if (copied) "Copied" else "Copy",
                    color = Color(0xFF0B0D10),
                    fontSize = 12.sp,
                    fontFamily = FontFamily.Monospace,
                    modifier = Modifier
                        .clip(RoundedCornerShape(6.dp))
                        .background(if (copied) Color(0xFF86EFAC) else Color(0xFFE5E7EB))
                        .clickable {
                            onCopy(text)
                            copied = true
                        }
                        .padding(horizontal = 12.dp, vertical = 5.dp),
                )
            }
            Box(Modifier.weight(1f).verticalScroll(rememberScrollState())) {
                Text(
                    text = text,
                    color = Color(0xFFE5E7EB),
                    fontSize = 9.sp,
                    lineHeight = 11.sp,
                    fontFamily = FontFamily.Monospace,
                )
            }
        }
    }
}
