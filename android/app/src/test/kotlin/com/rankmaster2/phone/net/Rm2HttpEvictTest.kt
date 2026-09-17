package com.rankmaster2.phone.net

import okhttp3.Request
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

/**
 * § the owner's ask, 2026-09-17 ("I don't want to keep anything on device if the app is not
 * running"): [Rm2Http.evictMediaCache] is the live-instance half of that for photographs -
 * `MediaCacheJanitor.wipeWhileRunning`'s call for a clean stop, backing up `wipeBeforeUse`'s
 * directory delete for the case that actually holds against a killed process.
 *
 * This proves it end to end against a real cached response: a photograph fetched once is served
 * from disk on the second ask (the ordinary case, and the whole reason the cache exists); after
 * `evictMediaCache`, the same client asks the network again rather than finding stale bytes still
 * sitting there - and can go on caching normally afterward, because nothing about the client itself
 * was closed.
 */
class Rm2HttpEvictTest {

    @get:Rule val folder = TemporaryFolder()

    private lateinit var rm2: Rm2TestServer

    @Before fun setUp() { rm2 = Rm2TestServer() }

    @After fun tearDown() { rm2.close() }

    @Test
    fun `evictMediaCache clears a cached response, and the client keeps caching afterward`() {
        Rm2Http.configure(folder.newFolder("evict"))
        val client = Rm2Http.client(rm2.identity)
        val url = rm2.identity.baseUrl + "/media/a.jpg/still"
        val body = "fake-photograph-bytes"

        rm2.server.enqueue(
            okhttp3.mockwebserver.MockResponse()
                .setResponseCode(200)
                .setHeader("Content-Type", "image/jpeg")
                .setHeader("ETag", "\"v1\"")
                .setHeader("Cache-Control", "private, max-age=31536000, immutable")
                .setBody(body),
        )

        // First fetch: from the network, and cached.
        client.newCall(Request.Builder().url(url).build()).execute().use { it.body!!.bytes() }
        assertEquals(1, rm2.server.requestCount)

        // Second fetch, unchanged: served from disk, no second request - the cache doing its job.
        client.newCall(Request.Builder().url(url).build()).execute().use { r ->
            assertTrue("must be a cache hit before evicting means anything", r.cacheResponse != null)
        }
        assertEquals(1, rm2.server.requestCount)

        Rm2Http.evictMediaCache()

        // A third fetch must go back to the network - the cached copy is gone.
        rm2.server.enqueue(
            okhttp3.mockwebserver.MockResponse()
                .setResponseCode(200)
                .setHeader("Content-Type", "image/jpeg")
                .setHeader("ETag", "\"v1\"")
                .setHeader("Cache-Control", "private, max-age=31536000, immutable")
                .setBody(body),
        )
        client.newCall(Request.Builder().url(url).build()).execute().use { r ->
            assertEquals("the evicted entry must not still answer from disk", null, r.cacheResponse)
        }
        assertEquals("evicting must not stop the client asking the network again", 2, rm2.server.requestCount)

        // And the client goes on caching normally - nothing about it was closed.
        client.newCall(Request.Builder().url(url).build()).execute().use { r ->
            assertTrue("caching must still work after an eviction", r.cacheResponse != null)
        }
        assertEquals(2, rm2.server.requestCount)
    }

    @Test
    fun `evictMediaCache never throws, whatever state the client map is in`() {
        // Reaching the end of this test at all is the proof - including the very first launch of a
        // process, before anything has built a client.
        Rm2Http.evictMediaCache()
    }
}
