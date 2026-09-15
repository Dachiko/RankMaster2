package com.rankmaster2.phone.media

import android.content.Context
import coil.request.CachePolicy
import coil.request.ErrorResult
import coil.request.ImageRequest
import coil.request.SuccessResult
import java.nio.file.Files
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.test.UnconfinedTestDispatcher
import kotlinx.coroutines.test.resetMain
import kotlinx.coroutines.test.setMain
import okhttp3.Cache
import okhttp3.OkHttpClient
import okhttp3.mockwebserver.MockWebServer
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config

/**
 * What the image loader's cache policy actually is, proved rather than asserted in a comment.
 *
 * The claim being tested is the one the whole layer rests on: **a photograph crosses the network
 * once.** It is OkHttp's disk cache that makes that true, honouring the server's `ETag` and
 * `Cache-Control: private, max-age=31536000, immutable` (SERVER_SPEC.md § 12.5), and Coil is
 * configured to stay out of its way - no second disk cache, no `no-cache` header added "to be
 * safe".
 */
@OptIn(ExperimentalCoroutinesApi::class)
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [33])
class Rm2ImageLoaderTest {

    private lateinit var server: MockWebServer
    private lateinit var client: OkHttpClient
    private lateinit var cacheDir: java.io.File
    private val context: Context get() = RuntimeEnvironment.getApplication()

    @Before
    fun setUp() {
        // Coil runs a request's lifecycle on Dispatchers.Main.immediate. Under Robolectric that is
        // the main looper, and this test blocks it, so without a test dispatcher in its place the
        // loader would simply never get to run. Nothing about the loader itself is faked here.
        Dispatchers.setMain(UnconfinedTestDispatcher())
        server = MockWebServer().apply { start() }
        cacheDir = Files.createTempDirectory("rm2-loader-cache").toFile()
        client = OkHttpClient.Builder().cache(Cache(cacheDir, 32L * 1024 * 1024)).build()
    }

    @After
    fun tearDown() {
        server.shutdown()
        cacheDir.deleteRecursively()
        Dispatchers.resetMain()
    }

    private fun loader() = Rm2ImageLoader.create(context, client)

    private fun request(url: String) = ImageRequest.Builder(context)
        .data(url)
        // Robolectric has no GPU, and the point of the request is the bytes, not the bitmap.
        .allowHardware(false)
        // So the second load goes to the fetcher rather than stopping at the decoded bitmap: it is
        // the *network* layer under test here.
        .memoryCachePolicy(CachePolicy.DISABLED)
        .build()

    @Test
    fun `there is no second disk cache`() {
        val loader = loader()

        // Coil's own disk cache is off: OkHttp's is the one the server's headers were written for,
        // and two byte caches on a phone is two copies of every photograph.
        assertNull(loader.diskCache)
        // The memory cache stays - that is decoded bitmaps, not bytes.
        assertNotNull(loader.memoryCache)
    }

    @Test
    fun `a photograph crosses the network once, and the second load is a cache hit`() = runBlocking {
        server.enqueue(MediaFixtures.stillResponse())
        val url = server.url("/api/v1/media/holiday%20photo.jpg/still?v=abc&w=1080&format=webp").toString()
        val loader = loader()

        val first = loader.execute(request(url))
        assertTrue(first.toString(), first is SuccessResult)
        assertEquals(1, server.requestCount)

        // Nothing more is enqueued. If this load touched the network at all, MockWebServer would
        // hang or fail rather than answer - which is exactly the assertion wanted.
        val second = loader.execute(request(url))
        assertTrue(second.toString(), second is SuccessResult)
        assertEquals("a second download of an immutable URL", 1, server.requestCount)
    }

    @Test
    fun `a different width is a different URL and is fetched separately`() = runBlocking {
        server.enqueue(MediaFixtures.stillResponse(etag = "\"s1080w-aaaa\""))
        server.enqueue(MediaFixtures.stillResponse(etag = "\"s2160w-bbbb\""))
        val loader = loader()
        val link = "/api/v1/media/x.jpg/still?v=abc"
        val base = server.url("/api/v1").toString()

        loader.execute(request(MediaUrls.resolve(base, MediaUrls.still(link, 1080))))
        loader.execute(request(MediaUrls.resolve(base, MediaUrls.still(link, 2160))))

        assertEquals(2, server.requestCount)
    }

    @Test
    fun `a 422 arrives at the caller as undecodable, not as a crash`() = runBlocking {
        server.enqueue(MediaFixtures.errorResponse(422, "media_decode_failed"))

        val result = loader().execute(request(server.url("/api/v1/media/broken.jpg/still?w=1080").toString()))

        assertTrue(result is ErrorResult)
        assertEquals(MediaPaneState.Undecodable, MediaFailures.stateOf((result as ErrorResult).throwable))
    }

    @Test
    fun `a 404 arrives as gone, which is the cue to offer discarding that side`() = runBlocking {
        server.enqueue(MediaFixtures.errorResponse(404, "media_file_missing"))

        val result = loader().execute(request(server.url("/api/v1/media/vanished.jpg/still?w=1080").toString()))

        assertTrue(result is ErrorResult)
        assertEquals(MediaPaneState.Gone, MediaFailures.stateOf((result as ErrorResult).throwable))
    }
}
