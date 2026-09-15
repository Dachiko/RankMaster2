package com.rankmaster2.phone.media

import java.nio.file.Files
import kotlinx.coroutines.runBlocking
import okhttp3.Cache
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.mockwebserver.Dispatcher
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import okhttp3.mockwebserver.RecordedRequest
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

/**
 * Prefetching warm pairs, and the two promises it has to keep.
 *
 * 1. It asks for **exactly** what the pane will ask for - same URL, same width, same format - or it
 *    is not a prefetch, it is a second download.
 * 2. It does nothing else. A warm pair has no token and MUST NOT be acted on, and a prefetch MUST
 *    NOT count as an impression (SERVER_SPEC.md § 9.5). What is proved here is that the only
 *    traffic a prefetch generates is a plain `GET` on the still, with no extra request of any kind.
 */
class MediaPrefetcherTest {

    private lateinit var server: MockWebServer
    private lateinit var client: OkHttpClient
    private lateinit var cacheDir: java.io.File
    private lateinit var baseUrl: String

    @Before
    fun setUp() {
        server = MockWebServer().apply {
            dispatcher = object : Dispatcher() {
                override fun dispatch(request: RecordedRequest): MockResponse =
                    // The ETag is per-URL, as the server's per-variant one is.
                    MediaFixtures.stillResponse(etag = "\"s1080w-" + request.path.orEmpty().hashCode().toUInt().toString(16) + "\"")
            }
            start()
        }
        cacheDir = Files.createTempDirectory("rm2-cache").toFile()
        client = OkHttpClient.Builder().cache(Cache(cacheDir, 32L * 1024 * 1024)).build()
        baseUrl = server.url("/api/v1").toString()
    }

    @After
    fun tearDown() {
        server.shutdown()
        cacheDir.deleteRecursively()
    }

    private fun prefetcher() = MediaPrefetcher(client, baseUrl)

    @Test
    fun `warm pairs are fetched at the pane's width, links verbatim`() = runBlocking {
        val snapshot = MediaFixtures.snapshot(
            pair = MediaFixtures.pair(MediaFixtures.still("now-left.jpg"), MediaFixtures.still("now-right.jpg")),
            warmPairs = listOf(
                MediaFixtures.pair(
                    MediaFixtures.still("holiday photo.jpg", encoded = "holiday%20photo.jpg"),
                    MediaFixtures.still("a+b.jpg"),
                ),
                MediaFixtures.pair(MediaFixtures.still("c.jpg"), MediaFixtures.still("d.jpg")),
            ),
        )

        val fetched = prefetcher().warm(snapshot, panePx = 1000)

        assertEquals(4, fetched)
        assertEquals(4, server.requestCount)

        val paths = (1..4).map { server.takeRequest().path }
        assertEquals(
            listOf(
                "/api/v1/media/holiday%20photo.jpg/still?v=9f2a1c77b0e4d310&w=1080&format=webp",
                "/api/v1/media/a+b.jpg/still?v=9f2a1c77b0e4d310&w=1080&format=webp",
                "/api/v1/media/c.jpg/still?v=9f2a1c77b0e4d310&w=1080&format=webp",
                "/api/v1/media/d.jpg/still?v=9f2a1c77b0e4d310&w=1080&format=webp",
            ),
            paths,
        )
        // The current pair is the screen's job, not the prefetcher's.
        assertTrue(paths.none { "now-" in it.orEmpty() })
    }

    @Test
    fun `an encoded filename reaches the wire exactly as the server wrote it`() = runBlocking {
        val warm = MediaFixtures.pair(
            MediaFixtures.still("a b&c#d.jpg", encoded = "a%20b%26c%23d.jpg"),
            MediaFixtures.still("100%.jpg", encoded = "100%25.jpg"),
        )

        prefetcher().warm(listOf(warm), panePx = 360)

        val paths = listOf(server.takeRequest().path, server.takeRequest().path)
        assertEquals("/api/v1/media/a%20b%26c%23d.jpg/still?v=9f2a1c77b0e4d310&w=360&format=webp", paths[0])
        assertEquals("/api/v1/media/100%25.jpg/still?v=9f2a1c77b0e4d310&w=360&format=webp", paths[1])
    }

    @Test
    fun `the prefetch and the pane ask for the same URL, so the picture crosses the network once`() =
        runBlocking {
            val ref = MediaFixtures.still("holiday photo.jpg", encoded = "holiday%20photo.jpg")
            val warm = MediaFixtures.pair(ref, MediaFixtures.still("other.jpg"))

            prefetcher().warm(listOf(warm), panePx = 1000)
            assertEquals(2, server.requestCount)

            // What the pane will do when this warm pair becomes current: the same GET.
            val paneUrl = MediaUrls.resolve(
                baseUrl,
                MediaUrls.still(ref.links.still!!, MediaWidths.forPane(1000), StillFormat.WEBP),
            )
            val response = client.newCall(Request.Builder().url(paneUrl).build()).execute()
            response.use { assertEquals(MediaFixtures.PNG.size, it.body!!.bytes().size) }

            // Served from OkHttp's cache under `Cache-Control: immutable`. No second download, and
            // not even a revalidation: § 12.5's guarantee, doing the job it was written for.
            assertEquals(2, server.requestCount)
            assertNotNull("must come from the cache", response.cacheResponse)
            assertNull("and must not have touched the network", response.networkResponse)
        }

    @Test
    fun `videos and vanished files are not prefetched`() {
        val pairs = listOf(
            MediaFixtures.pair(MediaFixtures.video("clip.mp4"), MediaFixtures.gone("deleted.jpg")),
            MediaFixtures.pair(MediaFixtures.still("real.jpg"), MediaFixtures.video("clip2.mkv")),
        )

        val urls = prefetcher().urlsFor(pairs, panePx = 720)

        assertEquals(1, urls.size)
        assertTrue(urls.single().endsWith("/api/v1/media/real.jpg/still?v=9f2a1c77b0e4d310&w=720&format=webp"))
        assertFalse(urls.any { "clip" in it || "deleted" in it })
    }

    @Test
    fun `an empty warm list is normal and does nothing`() = runBlocking {
        // § 9.5: with exactly two eligible files, warmPairs is always empty. Not an error.
        assertEquals(0, prefetcher().warm(MediaFixtures.snapshot(), panePx = 1080))
        assertEquals(0, server.requestCount)
    }

    @Test
    fun `the same file in two warm pairs is fetched once`() = runBlocking {
        val ref = MediaFixtures.still("shared.jpg")
        val pairs = listOf(
            MediaFixtures.pair(ref, MediaFixtures.still("x.jpg")),
            MediaFixtures.pair(ref, MediaFixtures.still("y.jpg")),
        )

        assertEquals(3, prefetcher().warm(pairs, panePx = 540))
        assertEquals(3, server.requestCount)
    }

    @Test
    fun `a warm pair whose file has just gone does not break the warm-up`() = runBlocking {
        server.shutdown()
        server = MockWebServer().apply {
            dispatcher = object : Dispatcher() {
                override fun dispatch(request: RecordedRequest): MockResponse =
                    if ("missing" in request.path!!) {
                        MediaFixtures.errorResponse(404, "media_file_missing")
                    } else {
                        MediaFixtures.stillResponse()
                    }
            }
            start()
        }
        baseUrl = server.url("/api/v1").toString()

        val pairs = listOf(
            MediaFixtures.pair(MediaFixtures.still("missing.jpg"), MediaFixtures.still("fine.jpg")),
        )

        assertEquals("the second one still warms", 1, prefetcher().warm(pairs, panePx = 1080))
        assertEquals(2, server.requestCount)
    }
}
