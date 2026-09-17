package com.rankmaster2.phone

import android.os.Build
import android.os.Bundle
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import com.rankmaster2.phone.media.MediaCacheJanitor
import com.rankmaster2.phone.net.Rm2Http
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color

/// The one activity. Every screen is a composable inside it; there is no navigation library and no
/// second activity, because the app is a single flow: connect, pick a folder, rank.
class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        CrashLog.install(this)
        // The owner's ask, 2026-09-17: nothing of his survives on disk while the app is not
        // running. First, before anything opens either cache - see MediaCacheJanitor's own doc for
        // why this is the call the guarantee actually rests on, and why it has to run before the
        // next two lines rather than after them.
        MediaCacheJanitor.wipeBeforeUse(this)
        // Before anything can ask for an HTTP client, because a client built without the cache
        // keeps not having one. This one line is what makes "each photograph crosses the network
        // once, ever" true rather than aspirational.
        Rm2Http.configure(cacheDir)
        hideFromTheAppSwitcher()
        enableEdgeToEdge()
        goFullScreen()
        setContent { Rm2Theme { Rm2App(this) } }
    }

    override fun onStop() {
        super.onStop()
        // The ordinary-case half of the same guarantee: empties both caches in place while they
        // are still open, so the gap between leaving the app and the next launch is small rather
        // than however long it takes onCreate's own wipe, above, to run. Best-effort on purpose -
        // Android can kill this process without ever calling onStop - which is exactly why that
        // wipe, not this one, is what the guarantee is built on.
        MediaCacheJanitor.wipeWhileRunning(this)
    }

    /**
     * No status bar, no navigation bar: the two photographs get the glass.
     *
     * A clock and a battery icon above a picture being judged are two things competing with it, and
     * this screen exists to ask one question. The bars come back on a swipe from the edge and go
     * away again on their own - `BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE` - so nothing is lost, it is
     * just not permanently in the way.
     */
    private fun goFullScreen() {
        WindowCompat.setDecorFitsSystemWindows(window, false)
        WindowInsetsControllerCompat(window, window.decorView).apply {
            hide(WindowInsetsCompat.Type.systemBars())
            systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
        }
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        // Coming back from the app switcher, or from a permission dialog, restores the bars. Put
        // them away again.
        if (hasFocus) goFullScreen()
    }

    /**
     * The app switcher must show black, not the pair of photographs that happened to be on screen.
     *
     * Android keeps that thumbnail after the app is left, so without this a glance at someone's
     * recent apps is a glance at whatever they were ranking. Which is the whole library, eventually.
     *
     * Two ways to do it, and they are not equivalent:
     *
     *  - `setRecentsScreenshotEnabled(false)` (Android 13+) blanks the switcher and nothing else.
     *    Screenshots still work, which matters while this app is being built: a screenshot is how
     *    a layout problem gets reported.
     *  - `FLAG_SECURE` also blanks it, but blocks screenshots and screen recording entirely, and
     *    blocks mirroring to an external display.
     *
     * So: the narrow API where it exists, and the blunt one only below Android 13, where nothing
     * else will do it. The target device is Android 14 and takes the first path.
     */
    private fun hideFromTheAppSwitcher() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            setRecentsScreenshotEnabled(false)
        } else {
            window.setFlags(WindowManager.LayoutParams.FLAG_SECURE, WindowManager.LayoutParams.FLAG_SECURE)
        }
    }
}

@Composable
fun Rm2Theme(content: @Composable () -> Unit) {
    // Fixed dark. A photograph judged against a light surround is a photograph judged wrong.
    MaterialTheme(
        colorScheme = darkColorScheme(
            background = Color.Black,
            surface = Color.Black,
            onBackground = Color(0xFFECEEF2),
            onSurface = Color(0xFFECEEF2),
        ),
        content = content,
    )
}
