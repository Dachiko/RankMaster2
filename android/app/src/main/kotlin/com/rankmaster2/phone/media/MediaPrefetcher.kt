package com.rankmaster2.phone.media

import com.rankmaster2.phone.net.MediaRef
import com.rankmaster2.phone.net.Snapshot
import java.io.IOException
import kotlin.coroutines.resume
import kotlinx.coroutines.CoroutineDispatcher
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withContext
import okhttp3.OkHttpClient
import okhttp3.Request
import okio.blackholeSink
import okio.buffer
import com.rankmaster2.phone.net.Pair as MediaPair

/**
 * Warming the cache for the pairs that are coming next.
 *
 * `warmPairs` is the server telling the client what `Advance()` will hand out (SERVER_SPEC.md
 * § 9.5), which is the same hint `MediaPipeline` uses on the desktop. Fetching those bytes now is
 * the difference between a pair that appears and a pair that fades in.
 *
 * ## What a prefetch must not be
 *
 * A warm pair carries **no token** and MUST NOT be acted on (§ 9.5), and prefetching one MUST NOT
 * count as an impression - by client or by server. The defence here is not a flag or a header: it
 * is that this class issues a plain `GET` on the same URL a pane would, and then does nothing else
 * at all. It touches no session state, votes on nothing, and reports nothing back. There is no
 * impression to accidentally record because there is no code here that could record one.
 *
 * ## Why this is not a Coil prefetch
 *
 * Coil would decode what it fetched and put a bitmap in the memory cache - four full-size bitmaps
 * for two warm pairs, most of which will be evicted before they are looked at, and one of which
 * (§ 7.4.3) may never become current at all. What is actually slow is the network, so this warms
 * the *bytes*, in OkHttp's shared disk cache. The decode then happens when the pane appears, from
 * local disk, at full speed.
 *
 * The request is byte-identical to the one the pane will make - same URL, same width, same format,
 * no extra headers - which is the only reason it is a cache hit rather than a second download.
 */
class MediaPrefetcher(
    private val client: OkHttpClient,
    private val baseUrl: String,
    private val format: StillFormat = StillFormat.WEBP,
    private val dispatcher: CoroutineDispatcher = Dispatchers.IO,
) {

    /**
     * Warm every still in [snapshot]'s warm pairs at the width [panePx] would ask for.
     *
     * Call it **after** the current pair is on screen: this is the cheapest work in the app and it
     * still competes for the same Wi-Fi.
     *
     * Suspends until done, and is cancellable - a new snapshot cancels the previous warm-up by
     * cancelling the job it was launched in. Failures are swallowed on purpose: a warm pair whose
     * file has just been deleted answers `404`, and that is not this screen's problem.
     */
    suspend fun warm(snapshot: Snapshot, panePx: Int): Int = warm(snapshot.warmPairs, panePx)

    suspend fun warm(pairs: List<MediaPair>, panePx: Int): Int {
        val urls = urlsFor(pairs, panePx)
        if (urls.isEmpty()) return 0
        var fetched = 0
        withContext(dispatcher) {
            // One at a time, deliberately. This is background work behind a pair the user is
            // already looking at; four parallel downloads would make the picture in front of them
            // slower to arrive, which is exactly backwards.
            for (url in urls) {
                ensureActive()
                if (fetch(url)) fetched++
            }
        }
        return fetched
    }

    /**
     * The URLs [warm] would fetch, in order. Public because it is the honest way to test that a
     * prefetch asks for exactly the same thing a pane will, and nothing more.
     *
     * Videos are skipped: they stream over Range and are not worth pulling whole into a cache that
     * holds photographs. A ref whose file has already gone (`sizeBytes: null`, § 11.3) is skipped
     * too - it would only answer `404`.
     */
    fun urlsFor(pairs: List<MediaPair>, panePx: Int): List<String> {
        val width = MediaWidths.forPane(panePx)
        return pairs
            .flatMap { listOf(it.left, it.right) }
            .mapNotNull { stillUrl(it, width) }
            .distinct()
    }

    private fun stillUrl(ref: MediaRef, width: Int): String? {
        if (ref.isMissing) return null
        val link = ref.links.still ?: return null
        return MediaUrls.resolve(baseUrl, MediaUrls.still(link, width, format))
    }

    /**
     * A plain GET, read to the end so OkHttp commits the cache entry, then thrown away.
     *
     * § 3.8 (the second audit): a warm-up this cancelled used to keep running regardless - `execute`
     * blocks the thread it is on, and nothing coroutine cancellation does can interrupt a blocking
     * Java call already in progress; `ensureActive` in [warm] only ever caught the gap *between*
     * URLs. So a vote that superseded this warm-up (a fresh `LaunchedEffect` key cancelling the old
     * job) left the download running to completion anyway, on the same Wi-Fi and through the same
     * connection pool as the picture now actually in front of him - the download he meant to cancel
     * was the one slowing down the one he was waiting for.
     *
     * The call is kept reachable for exactly as long as this suspends, so
     * [suspendCancellableCoroutine] can wire the coroutine's own cancellation straight to
     * `Call.cancel()` - which closes the connection out from under a blocking read on another
     * thread and is what actually stops the bytes, not just stops *asking* for more of them.
     */
    private suspend fun fetch(url: String): Boolean = suspendCancellableCoroutine { continuation ->
        val call = client.newCall(Request.Builder().url(url).build())
        continuation.invokeOnCancellation { call.cancel() }

        val result = try {
            call.execute().use { response ->
                if (response.isSuccessful) {
                    response.body?.source()?.use { source ->
                        blackholeSink().buffer().use { sink -> sink.writeAll(source) }
                    }
                    true
                } else {
                    false
                }
            }
        } catch (_: IOException) {
            // Reached both by an ordinary network failure and by the cancellation above - a
            // cancelled call's blocking read ends in an IOException, not a CancellationException,
            // because nothing here ever suspended in the coroutine sense for it to interrupt.
            false
        }

        if (continuation.isActive) continuation.resume(result)
    }
}
