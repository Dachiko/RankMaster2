@file:OptIn(androidx.compose.ui.ExperimentalComposeUiApi::class, androidx.compose.ui.InternalComposeUiApi::class, kotlinx.coroutines.ExperimentalCoroutinesApi::class)

package com.rankmaster2.phone.media

import android.content.Context
import android.os.Looper
import android.view.SurfaceView
import android.view.View
import android.view.ViewGroup
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.requiredSize
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.BroadcastFrameClock
import androidx.compose.runtime.Recomposer
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshots.Snapshot
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.layout.boundsInRoot
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.ui.node.RootForTest
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.WindowRecomposerFactory
import androidx.compose.ui.platform.WindowRecomposerPolicy
import androidx.compose.ui.semantics.SemanticsNode
import androidx.compose.ui.semantics.SemanticsProperties
import androidx.compose.ui.semantics.getOrNull
import androidx.compose.ui.unit.Density
import androidx.compose.ui.unit.dp
import androidx.media3.exoplayer.ExoPlayer
import com.rankmaster2.phone.net.MediaRef
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.StandardTestDispatcher
import kotlinx.coroutines.test.TestCoroutineScheduler
import okhttp3.OkHttpClient
import org.junit.After
import org.junit.Before
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNotSame
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.Robolectric
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config
import kotlin.math.abs

/**
 * The shape of a video pane, *executed* rather than reasoned about: the real [MediaPane], inside
 * the real arrangement `RankScreen.Pair` puts it in (a `Column` of two weighted panes in portrait,
 * a `Row` in landscape), measured by Compose and laid out by the View system under Robolectric,
 * with the decoder's one contribution - the shape - handed in by the test.
 *
 * ## What this can and cannot prove
 *
 * Robolectric runs Compose's measure and placement and Android's own `View.measure`/`layout` for
 * real, so everything down to the `SurfaceView`'s laid-out rectangle is genuinely exercised here.
 * What it cannot run is the compositor: whether the *surface* behind that rectangle is the same
 * size as the rectangle is decided by SurfaceFlinger on a phone and is invisible from a JVM. That
 * boundary is the reason for the rule the last two tests pin - a surface is created at its final
 * size and never resized in place - because a fresh surface is exactly what a rotation gives a
 * pane, and a rotation is what the owner reports as the cure.
 *
 * ## The owner's report, 2026-09-17
 *
 * "Lower video is incorrect; rotate back and forth, lower is fixed but now the upper one is
 * broken; one more rotation, both are fine." The activity keeps its composition across a rotation
 * (`configChanges` in the manifest), so a rotation is the `Column`/`Row` branch switching: both
 * panes are disposed and rebuilt with fresh players and fresh surfaces. `a rotation gives each
 * pane a fresh player` pins that no state - no remembered shape, nothing - crosses that boundary,
 * which is why the fault could land on either pane at random and never followed a file.
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [33], qualifiers = "w1080dp-h2340dp-mdpi")
class VideoPaneLayoutTest {

    private val context: Context get() = RuntimeEnvironment.getApplication()

    /** One player the pane asked for, and the two things the decoder would tell it about. */
    private class Built(val video: Rm2Video, val onState: (MediaPaneState) -> Unit) {
        fun report(shape: Float, loaded: Boolean = true) {
            video.aspectRatio.value = shape
            if (loaded) onState(MediaPaneState.Loaded)
        }
    }

    private val built = mutableListOf<Built>()
    private val players = mutableListOf<ExoPlayer>()

    // Compose is driven from here rather than from Android's Choreographer. The window's default
    // recomposer runs on `AndroidUiDispatcher.Main`, a process-wide singleton that binds itself to
    // the Choreographer of the *first* test in a Robolectric sandbox and is stranded once
    // Robolectric replaces it for the next one - so recompositions silently stop after the first
    // test in the class. A recomposer per test, on a test scheduler, with a frame clock that fires
    // as soon as it is asked, is what the Compose test rule does for the same reason.
    private val scheduler = TestCoroutineScheduler()
    private val dispatcher = StandardTestDispatcher(scheduler)
    // Frames are sent from `frame()`, never on their own: the pane's progress spinner is an
    // infinite animation, and a clock that answered every request at once would never go idle.
    private val frameClock = BroadcastFrameClock()
    private var frameTimeNanos = 0L
    private val recomposerScope = CoroutineScope(dispatcher + frameClock)
    private val recomposers = mutableListOf<Recomposer>()

    @Before
    fun driveComposeFromTheTest() {
        WindowRecomposerPolicy.setFactory(
            WindowRecomposerFactory {
                Recomposer(dispatcher + frameClock).also { recomposer ->
                    recomposers += recomposer
                    recomposerScope.launch { recomposer.runRecomposeAndApplyChanges() }
                }
            },
        )
    }

    @After
    fun tearDown() {
        players.forEach { runCatching { it.release() } }
        recomposers.forEach { it.cancel() }
        recomposerScope.cancel()
        WindowRecomposerPolicy.setFactory(WindowRecomposerFactory.LifecycleAware)
    }

    /** A media layer whose slots build real, unprepared players the test can speak for. */
    private fun media(): Rm2Media {
        val client = OkHttpClient()
        val baseUrl = "https://192.168.1.42:18611/api/v1"
        val videoPlayers = Rm2VideoPlayers(context, client, baseUrl) { _, onState ->
            val player = ExoPlayer.Builder(context).build().also { players += it }
            Rm2Video(player, onState).also { built += Built(it, onState) }
        }
        return Rm2Media(
            baseUrl = baseUrl,
            imageLoader = Rm2ImageLoader.create(context, client),
            prefetcher = MediaPrefetcher(client, baseUrl, StillFormat.WEBP),
            players = videoPlayers,
            format = StillFormat.WEBP,
        )
    }

    private inner class Stage(val activity: ComponentActivity) {
        var portrait by mutableStateOf(true)
        var playing by mutableStateOf(true)
        val paneBounds = mutableMapOf<String, Rect>()

        /** Lets Compose recompose, measure and place, and the View tree lay out, until quiet. */
        fun frame() {
            repeat(5) {
                Snapshot.sendApplyNotifications()
                scheduler.advanceUntilIdle()
                frameTimeNanos += 16_000_000L
                frameClock.sendFrame(frameTimeNanos)
                scheduler.advanceUntilIdle()
                shadowOf(Looper.getMainLooper()).idle()
                roots().forEach { it.measureAndLayoutForTest() }
            }
        }

        private fun roots(): List<RootForTest> = views().filterIsInstance<RootForTest>()

        fun views(): List<View> {
            val out = mutableListOf<View>()
            fun walk(v: View) {
                out += v
                if (v is ViewGroup) for (i in 0 until v.childCount) walk(v.getChildAt(i))
            }
            walk(activity.window.decorView)
            return out
        }

        fun bounds(v: View): Rect {
            val loc = IntArray(2).also { v.getLocationInWindow(it) }
            return Rect(loc[0].toFloat(), loc[1].toFloat(), (loc[0] + v.width).toFloat(), (loc[1] + v.height).toFloat())
        }

        /** The one surface a pane is showing, or null if it shows none. */
        fun surface(pane: String): SurfaceView? {
            val within = paneBounds.getValue(pane)
            val found = views().filterIsInstance<SurfaceView>().filter { it.isAttachedToWindow && within.contains(bounds(it).center) }
            check(found.size <= 1) { "$pane shows ${found.size} surfaces at once" }
            return found.singleOrNull()
        }

        private fun semantics(tag: String): List<SemanticsNode> {
            val out = mutableListOf<SemanticsNode>()
            fun walk(n: SemanticsNode) {
                if (n.config.getOrNull(SemanticsProperties.TestTag) == tag) out += n
                n.children.forEach(::walk)
            }
            roots().forEach { walk(it.semanticsOwner.unmergedRootSemanticsNode) }
            return out
        }

        fun cover(pane: String): Rect? =
            semantics(MediaPaneTags.COVER).map { it.boundsInRoot }.singleOrNull { paneBounds.getValue(pane).contains(it.center) }

        fun videoBox(pane: String): Rect? =
            semantics(MediaPaneTags.VIDEO).map { it.boundsInRoot }.singleOrNull { paneBounds.getValue(pane).contains(it.center) }
    }

    /** The ranking screen's pair, laid out exactly as `RankScreen.Pair` lays it out. */
    private fun stage(left: MediaRef, right: MediaRef, media: Rm2Media, portrait: Boolean = true): Stage {
        val activity = Robolectric.buildActivity(ComponentActivity::class.java).setup().get()
        val stage = Stage(activity).also { it.portrait = portrait }
        activity.setContent {
            CompositionLocalProvider(LocalDensity provides Density(1f)) {
                // The phone's window, in pixels, either way up. Rotating is changing this box.
                val (w, h) = if (stage.portrait) 1080 to 2340 else 2340 to 1080
                Box(Modifier.requiredSize(w.dp, h.dp)) {
                    BoxWithConstraints(Modifier.fillMaxSize()) {
                        val isPortrait = maxHeight >= maxWidth
                        if (isPortrait) {
                            Column(Modifier.fillMaxSize()) {
                                Pane("upper", left, media, stage, Modifier.weight(1f).fillMaxWidth())
                                Pane("lower", right, media, stage, Modifier.weight(1f).fillMaxWidth())
                            }
                        } else {
                            Row(Modifier.fillMaxSize()) {
                                Pane("upper", left, media, stage, Modifier.weight(1f))
                                Pane("lower", right, media, stage, Modifier.weight(1f))
                            }
                        }
                    }
                }
            }
        }
        stage.frame()
        return stage
    }

    /** `RankScreen.Pane`, to the letter: a black, centring box around the pane. */
    @Composable
    private fun Pane(name: String, ref: MediaRef, media: Rm2Media, stage: Stage, modifier: Modifier) {
        Box(
            modifier.background(Color.Black).onGloballyPositioned { stage.paneBounds[name] = it.boundsInRoot() },
            contentAlignment = Alignment.Center,
        ) {
            MediaPane(ref = ref, media = media, playing = stage.playing)
        }
    }

    private val tall = MediaFixtures.video("phone-clip.mp4")   // 9:16, most of the owner's footage
    private val wide = MediaFixtures.video("camera-clip.mp4")  // 16:9

    private companion object {
        const val TALL = 9f / 16f
        const val WIDE = 16f / 9f
    }

    /** The picture is [shape], fits inside [pane], and sits in its middle - to the pixel. */
    private fun assertPictureFits(what: String, picture: Rect, pane: Rect, shape: Float) {
        assertEquals("$what: shape", shape, picture.width / picture.height, 0.002f)
        assertTrue(
            "$what: inside its pane ($picture in $pane)",
            picture.left >= pane.left - 1f && picture.top >= pane.top - 1f &&
                picture.right <= pane.right + 1f && picture.bottom <= pane.bottom + 1f,
        )
        assertTrue("$what: as large as the pane allows", abs(picture.width - pane.width) <= 1f || abs(picture.height - pane.height) <= 1f)
        assertTrue("$what: centred ($picture in $pane)", abs(picture.center.x - pane.center.x) <= 1f && abs(picture.center.y - pane.center.y) <= 1f)
    }

    // -- until the decoder speaks, nothing is shown ---------------------------------------------

    @Test
    fun `until the decoder has reported a shape, the pane is covered and shows no picture`() {
        // A surface has to exist before the decoder can produce a frame, and the decoder scales its
        // frames to whatever surface it has - so before the shape is known, the only honest thing to
        // show is nothing. The cover is opaque and the pane's full size, over both panes.
        val stage = stage(tall, wide, media())

        for (pane in listOf("upper", "lower")) {
            assertEquals("$pane is covered edge to edge", stage.paneBounds.getValue(pane), stage.cover(pane))
        }

        // The upper pane's decoder reports; the lower one's has not yet. Only the upper uncovers.
        built[0].report(TALL)
        stage.frame()

        assertNull("upper, told its shape and loaded, is uncovered", stage.cover("upper"))
        assertEquals("lower, still waiting, stays covered", stage.paneBounds.getValue("lower"), stage.cover("lower"))
    }

    @Test
    fun `a shape alone does not uncover the pane - it also has to be playing`() {
        val stage = stage(tall, wide, media())

        built[0].report(TALL, loaded = false)
        stage.frame()

        assertNotNull("shape known but not loaded: still covered", stage.cover("upper"))

        built[0].onState(MediaPaneState.Loaded)
        stage.frame()

        assertNull(stage.cover("upper"))
    }

    // -- the picture is the right shape, in both orientations -----------------------------------

    @Test
    fun `in portrait, a phone clip is bounded by the pane's height and a camera clip by its width`() {
        val stage = stage(tall, wide, media())
        built[0].report(TALL)
        built[1].report(WIDE)
        stage.frame()

        assertPictureFits("upper 9:16", stage.bounds(stage.surface("upper")!!), stage.paneBounds.getValue("upper"), TALL)
        assertPictureFits("lower 16:9", stage.bounds(stage.surface("lower")!!), stage.paneBounds.getValue("lower"), WIDE)

        // And the surface is exactly the box Compose measured - there is one owner of the shape.
        assertEquals(stage.videoBox("upper"), stage.bounds(stage.surface("upper")!!))
        assertEquals(stage.videoBox("lower"), stage.bounds(stage.surface("lower")!!))
    }

    @Test
    fun `in landscape, the same two clips fit their side-by-side panes`() {
        val stage = stage(tall, wide, media(), portrait = false)
        built[0].report(TALL)
        built[1].report(WIDE)
        stage.frame()

        assertPictureFits("left 9:16", stage.bounds(stage.surface("upper")!!), stage.paneBounds.getValue("upper"), TALL)
        assertPictureFits("right 16:9", stage.bounds(stage.surface("lower")!!), stage.paneBounds.getValue("lower"), WIDE)
    }

    // -- rotation: fresh players, fresh surfaces, nothing carried over --------------------------

    @Test
    fun `a rotation gives each pane a fresh player with no shape, covered until its own decoder reports`() {
        val stage = stage(tall, wide, media())
        built[0].report(TALL)
        built[1].report(WIDE)
        stage.frame()
        assertEquals(2, built.size)

        // The phone turns. The composition survives (configChanges), the Column becomes a Row.
        stage.portrait = false
        stage.frame()

        assertEquals("both panes rebuilt their players", 4, built.size)
        assertTrue("the players from before the turn are gone", built[0].video.isReleased && built[1].video.isReleased)
        assertNull("nothing of the old shape reaches the new player", built[2].video.aspectRatio.value)
        assertNull(built[3].video.aspectRatio.value)
        assertNotNull("covered again until told", stage.cover("upper"))
        assertNotNull(stage.cover("lower"))

        built[2].report(TALL)
        built[3].report(WIDE)
        stage.frame()

        assertPictureFits("left after turning", stage.bounds(stage.surface("upper")!!), stage.paneBounds.getValue("upper"), TALL)
        assertPictureFits("right after turning", stage.bounds(stage.surface("lower")!!), stage.paneBounds.getValue("lower"), WIDE)

        // And back.
        stage.portrait = true
        stage.frame()
        assertEquals(6, built.size)
        built[4].report(TALL)
        built[5].report(WIDE)
        stage.frame()

        assertPictureFits("upper after turning back", stage.bounds(stage.surface("upper")!!), stage.paneBounds.getValue("upper"), TALL)
        assertPictureFits("lower after turning back", stage.bounds(stage.surface("lower")!!), stage.paneBounds.getValue("lower"), WIDE)
    }

    // -- the rule: a surface is created at its final size, never resized in place ---------------

    @Test
    fun `the surface that shows the picture is created at the picture's size, not resized into it`() {
        // Before the shape is known there is a full-pane surface (under the cover) for the decoder
        // to work into. When the shape arrives, the pane does not resize that surface: it lets it go
        // and creates a new one that is the right size from its first layout - which is what a
        // rotation does for a pane, and is the one thing the owner reports as putting a pane right.
        val stage = stage(tall, wide, media())
        val provisional = stage.surface("upper")
        assertNotNull("a surface exists for the decoder before the shape is known", provisional)
        assertEquals("and it is the pane, edge to edge", stage.paneBounds.getValue("upper"), stage.bounds(provisional!!))

        built[0].report(TALL)
        stage.frame()

        val shown = stage.surface("upper")
        assertNotNull(shown)
        assertNotSame("the surface that shows the picture is not the provisional one, resized", provisional, shown)
        assertFalse("the provisional surface is gone from the window", provisional.isAttachedToWindow)
        assertPictureFits("the new surface", stage.bounds(shown!!), stage.paneBounds.getValue("upper"), TALL)
    }

    @Test
    fun `a pane that stops playing and resumes starts over - no shape, covered, until its decoder reports`() {
        // The full-screen viewer opening over the pair, or the app going to the background. On
        // resume the pane builds a fresh player, and the honest state of a fresh player is "shape
        // unknown": it is covered until its own decoder reports, rather than seeded with what the
        // previous player knew.
        val stage = stage(tall, wide, media())
        built[0].report(TALL)
        built[1].report(WIDE)
        stage.frame()

        stage.playing = false
        stage.frame()
        assertNull("a pane that is not playing shows no surface", stage.surface("upper"))
        assertTrue(built[0].video.isReleased)

        stage.playing = true
        stage.frame()
        assertEquals(4, built.size)
        assertNull(built[2].video.aspectRatio.value)
        assertNotNull(stage.cover("upper"))

        built[2].report(TALL)
        stage.frame()
        assertNull(stage.cover("upper"))
        assertPictureFits("resumed", stage.bounds(stage.surface("upper")!!), stage.paneBounds.getValue("upper"), TALL)
    }
}
