package com.rankmaster2.phone.media

import android.content.Context
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config

/**
 * § the owner's ask, 2026-09-17 ("I don't want to keep anything on device if the app is not
 * running"): [Rm2VideoCache.evictAll] is the live-instance half of that for video. It has to empty
 * the cache without releasing the directory lock `SimpleCache` holds - a second `SimpleCache` on
 * the same directory throws (the class's own doc says so).
 *
 * What is provable here, on a JVM under Robolectric: that `evictAll` never throws, before or after
 * the cache has been opened, and that the instance [Rm2VideoCache.get] hands back afterward is the
 * *same* one - the lock was never released and reacquired. Driving a real write through Media3's
 * `SimpleCache` (`startFile`/`commitFile`) failed here with a `NullPointerException` inside
 * `SimpleCache` itself even before `evictAll` runs, which looks like a Robolectric/SQLite gap in
 * `StandaloneDatabaseProvider` rather than anything about this class - nothing in this codebase
 * exercised that path before. So "a resource actually gets removed" is reasoned from the
 * implementation (`removeResource` is called for every key in `cache.keys`, the same call ordinary
 * LRU eviction already makes) rather than proven end to end here; see the report.
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [33])
class Rm2VideoCacheTest {

    private val context: Context get() = RuntimeEnvironment.getApplication()

    @Test
    fun `evictAll on a cache that was never opened is a safe no-op`() {
        // Nothing has called get() yet in this process - proving the "nothing to empty" path never
        // throws, and never forces the cache open just to find it empty.
        Rm2VideoCache.evictAll()
    }

    @Test
    fun `evictAll on an opened, empty cache does not throw`() {
        Rm2VideoCache.get(context)

        Rm2VideoCache.evictAll()
    }

    @Test
    fun `evictAll does not release the directory lock - the same instance keeps being handed back`() {
        val cache = Rm2VideoCache.get(context)

        Rm2VideoCache.evictAll()

        assertTrue(
            "a second SimpleCache on the same directory throws, so this only holds if the lock " +
                "was never released",
            Rm2VideoCache.get(context) === cache,
        )
    }
}
