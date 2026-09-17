package com.rankmaster2.phone.media

import android.content.Context
import android.util.Log
import androidx.annotation.OptIn
import androidx.media3.common.util.UnstableApi
import androidx.media3.database.StandaloneDatabaseProvider
import androidx.media3.datasource.cache.LeastRecentlyUsedCacheEvictor
import androidx.media3.datasource.cache.SimpleCache
import java.io.File

/**
 * Video on disk, once.
 *
 * A clip loops for as long as its pair is on screen, and without this every loop fetched the whole
 * file again — 25–50 MB of 4K AV1 every five to fifteen seconds, per pane, for as long as the owner
 * looked at it. The bytes came down the same HTTP/2 connection whose 16 MB receive window was one
 * of the two things that ran the heap out.
 *
 * With a cache the file is fetched once and every loop after that is a disk read. The player's heap
 * buffer stops being fed by the network at all after the first pass, which is what makes the
 * buffer ceiling a comfort rather than a cliff.
 *
 * **One instance per directory, process-wide.** `SimpleCache` locks its folder and throws if a
 * second one opens the same place, so this is an object and not something a screen constructs.
 */
@OptIn(UnstableApi::class)
object Rm2VideoCache {

    /**
     * A gigabyte, on disk, evicted least-recently-used.
     *
     * Disk, not heap: this is the owner's 256 GB phone, and at 25–50 MB a clip it holds a few
     * dozen. It is deliberately separate from the photographs' 512 MB HTTP cache, so a session in a
     * video folder cannot evict every photograph the owner has been ranking.
     */
    const val MAX_BYTES = 1024L * 1024 * 1024

    @Volatile
    private var cache: SimpleCache? = null

    @Synchronized
    fun get(context: Context): SimpleCache =
        cache ?: SimpleCache(
            File(context.applicationContext.cacheDir, "rm2-video"),
            LeastRecentlyUsedCacheEvictor(MAX_BYTES),
            StandaloneDatabaseProvider(context.applicationContext),
        ).also { cache = it }

    /**
     * Empties the cache in place, one resource at a time, without releasing the directory lock
     * this instance holds. The live-instance half of "nothing survives while the app is not
     * running" (the owner's ask, 2026-09-17) - `MediaCacheJanitor.wipeBeforeUse` deletes the
     * directory outright before anything has opened it, which is what actually holds against a
     * killed process; this backs it up for the ordinary case, where something does get to run a
     * shutdown path, and does it without the release-then-delete-then-reacquire dance that would
     * risk a player mid-read finding its file gone out from under it.
     *
     * `removeResource` is the same call ordinary LRU eviction already makes when the cache fills,
     * so running it over every key the cache currently has is that same, already-safe operation,
     * exhaustively. A no-op if nothing has opened the cache yet - there is nothing to empty. One
     * key's failure is logged and does not stop the rest from being removed, and does not stop
     * this instance from going on serving the next video.
     */
    @Synchronized
    fun evictAll() {
        val live = cache ?: return
        live.keys.toList().forEach { key ->
            try {
                live.removeResource(key)
            } catch (e: Exception) {
                Log.w("Rm2VideoCache", "could not remove cached video resource: $key", e)
            }
        }
    }
}
