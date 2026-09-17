package com.rankmaster2.phone.media

import android.content.Context
import android.util.Log
import com.rankmaster2.phone.net.Rm2Http
import java.io.File
import java.util.concurrent.atomic.AtomicBoolean

/**
 * "I don't want to keep anything on device if the app is not running." - the owner, 2026-09-17.
 *
 * Two caches hold his pictures at rest, and neither one expires anything on its own:
 * `cacheDir/rm2-media` is the OkHttp disk cache [Rm2Http] keeps for photographs (up to 512 MB),
 * and `cacheDir/rm2-video` is the Media3 `SimpleCache` [Rm2VideoCache] keeps for video (up to
 * 1 GB, [Rm2VideoCache.MAX_BYTES]) - together up to about 1.5 GB. The server sends
 * `Cache-Control: private, max-age=31536000, immutable`, so nothing here goes stale on its own; it
 * sits until a cache fills, Android reclaims storage, or he clears app data by hand.
 *
 * Both caches are exactly what makes voting feel instant *within* a sitting, and what fixed the
 * OutOfMemory crashes video used to cause (§ 2.5) - they stay, and keep working normally, for as
 * long as the app is open. What does not survive the app not being open is their content.
 *
 * ## Two calls, because one is not the guarantee
 *
 * [wipeBeforeUse] deletes both directories outright, before anything has opened either cache -
 * plain file deletion, against an unopened directory, which is as certain as a wipe gets. This is
 * the call that actually delivers the guarantee: Android can kill this process at any moment
 * without running any shutdown callback at all, so a clean-exit-only wipe leaves the gap between
 * "something went wrong" and the next launch completely unguarded - which is precisely the gap
 * the owner is asking to have closed. Running the wipe at the *start* of the next run, before
 * either cache is touched, closes it regardless of how the previous run ended.
 *
 * [wipeWhileRunning] is the adjunct for the ordinary case, a clean stop: it empties both caches'
 * *live* instances in place, without closing or releasing either one, so nothing mid-fetch is
 * disturbed and both keep working the moment the app is back. It shrinks the window in the common
 * case; it is not what the guarantee rests on.
 */
object MediaCacheJanitor {

    private const val TAG = "MediaCacheJanitor"

    private val wipedThisProcess = AtomicBoolean(false)

    /**
     * Call **first** in `onCreate` - before `Rm2Http.configure`, before anything can reach
     * `Rm2VideoCache.get`. Guarded to run once per process: this app's activity can be recreated
     * without the process restarting (a manual recreate, though nothing here currently triggers
     * one - see `CrashLog.install`'s own A14 note for why this app treats "once per process" as
     * the rule rather than "once per `onCreate`" regardless), and re-wiping a cache that has spent
     * this session's own network traffic filling would throw that traffic away for nothing - the
     * guarantee is about what survives *between* runs, not about emptying a cache mid-session.
     */
    fun wipeBeforeUse(context: Context) {
        if (!wipedThisProcess.compareAndSet(false, true)) return
        val cacheDir = context.applicationContext.cacheDir
        deleteDirectory(File(cacheDir, "rm2-media"))
        deleteDirectory(File(cacheDir, "rm2-video"))
    }

    /**
     * Call from a clean-stop path (`MainActivity.onStop`). Best-effort, by design: Android does
     * not promise this runs at all, which is exactly why [wipeBeforeUse] is the call the guarantee
     * rests on and this is only the improvement on top of it. Never lets a failure in one cache
     * stop the other from being tried, and never lets either one crash the app or disturb a video
     * that is mid-read - see `Rm2VideoCache.evictAll`'s own doc for how that is kept safe.
     */
    fun wipeWhileRunning(context: Context) {
        runCatching { Rm2Http.evictMediaCache() }
            .onFailure { Log.w(TAG, "could not empty the photograph cache", it) }
        runCatching { Rm2VideoCache.evictAll() }
            .onFailure { Log.w(TAG, "could not empty the video cache", it) }
    }

    private fun deleteDirectory(dir: File) {
        runCatching {
            if (dir.exists()) dir.deleteRecursively()
        }.onFailure { Log.w(TAG, "could not clear ${dir.name} before use", it) }
    }

    /** Test-only: [wipeBeforeUse] is a real once-per-process latch, so a test proving it needs a way back to "unlatched". */
    internal fun resetForTest() {
        wipedThisProcess.set(false)
    }
}
