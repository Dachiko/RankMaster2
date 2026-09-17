package com.rankmaster2.phone.media

import android.content.Context
import com.rankmaster2.phone.net.Rm2Http
import com.rankmaster2.phone.store.ServerIdentity
import java.io.File
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config

/**
 * § the owner's ask, 2026-09-17: "I don't want to keep anything on device if the app is not
 * running." [MediaCacheJanitor.wipeBeforeUse] is the half of that this suite can actually prove -
 * real files, deleted, before anything reopens the directory they were in. Whether Android really
 * runs [MediaCacheJanitor.wipeWhileRunning] before killing the process is not something a JVM can
 * observe; see the report for what that leaves resting on reasoning rather than a test.
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [33])
class MediaCacheJanitorTest {

    private val context: Context get() = RuntimeEnvironment.getApplication()

    @Before
    fun setUp() {
        // Robolectric gives each test its own sandboxed classloader, which already resets
        // MediaCacheJanitor's once-per-process latch between methods - this is here so the test
        // that exercises the latch itself does not depend on that being true forever.
        MediaCacheJanitor.resetForTest()
    }

    @Test
    fun `wipeBeforeUse deletes whatever the previous run left behind`() {
        val media = File(context.cacheDir, "rm2-media").apply { mkdirs() }
        File(media, "leftover.jpg").writeText("stale bytes from a run that ended badly")
        val video = File(context.cacheDir, "rm2-video").apply { mkdirs() }
        File(video, "leftover.mp4").writeText("stale bytes")

        MediaCacheJanitor.wipeBeforeUse(context)

        assertFalse("the photograph cache directory must be gone", media.exists())
        assertFalse("the video cache directory must be gone", video.exists())
    }

    @Test
    fun `wipeBeforeUse does nothing, and does not throw, when there is nothing to wipe`() {
        assertFalse(File(context.cacheDir, "rm2-media").exists())
        MediaCacheJanitor.wipeBeforeUse(context) // must not throw against directories that do not exist
    }

    @Test
    fun `wipeBeforeUse only runs once per process, so a live session is never wiped out from under it`() {
        val media = File(context.cacheDir, "rm2-media").apply { mkdirs() }
        File(media, "first.jpg").writeText("x")
        MediaCacheJanitor.wipeBeforeUse(context)
        assertFalse(media.exists())

        // A second call - as a recreated activity in the same process would make - must not throw
        // away what this session has since fetched.
        media.mkdirs()
        val writtenThisSession = File(media, "written-this-session.jpg").apply { writeText("real bytes") }

        MediaCacheJanitor.wipeBeforeUse(context)

        assertTrue("a second call in the same process must be a no-op", writtenThisSession.exists())
    }

    @Test
    fun `wipeWhileRunning empties both live caches without crashing, and both keep working`() {
        // The photograph cache is proved end to end, with a real cached response, in
        // Rm2HttpEvictTest. What is proved here is the two caches together, through the same
        // entry point MainActivity.onStop calls: nothing crashes, the video cache's directory
        // lock is not released, and the photograph client is still the same, usable client.
        // (Driving a real write through Media3's SimpleCache failed under Robolectric with an
        // unrelated NullPointerException before evictAll was even reached - see Rm2VideoCacheTest.)
        Rm2Http.configure(context.cacheDir)
        val identity = ServerIdentity(
            host = "192.168.1.42",
            port = 18611,
            certificateFingerprint = "sha256:" + "ab".repeat(32),
            token = "token-janitor",
        )
        val client = Rm2Http.client(identity)
        assertTrue("a configured client must carry a cache to empty", client.cache != null)

        val videoCache = Rm2VideoCache.get(context)

        MediaCacheJanitor.wipeWhileRunning(context)

        assertTrue(
            "the video cache's directory lock must not have been released",
            Rm2VideoCache.get(context) === videoCache,
        )
        assertTrue(
            "the photograph cache's client must still be the same, working, client",
            Rm2Http.client(identity) === client,
        )
    }

    @Test
    fun `wipeWhileRunning does not throw even if neither cache has ever been opened`() {
        MediaCacheJanitor.wipeWhileRunning(context)
    }
}
