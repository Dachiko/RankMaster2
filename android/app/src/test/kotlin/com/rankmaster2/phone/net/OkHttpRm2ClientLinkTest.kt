package com.rankmaster2.phone.net

import com.rankmaster2.phone.net.impl.OkHttpRm2Client
import kotlinx.coroutines.test.runTest
import okhttp3.Request
import okhttp3.mockwebserver.MockResponse
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

/**
 * § 9.3: a `links.*` value is server-built, already percent-encoded and already carrying `v=`. The
 * client appends it to the origin and does nothing else to it.
 *
 * The failures this guards against are not hypothetical. Re-encoding turns `%20` into `%2520` and
 * the picture 404s; form-decoding turns a `+` in a filename into a space and a *different* file
 * 404s; and because the id is a filename (§ 11.1), both are silent until someone photographs a file
 * whose name has a space in it.
 */
class OkHttpRm2ClientLinkTest {

    private lateinit var rm2: Rm2TestServer

    @Before fun setUp() { rm2 = Rm2TestServer() }

    @After fun tearDown() { rm2.close() }

    private val base = "https://192.168.1.42:18611/api/v1"
    /** Lazy, because the fixture only exists once @Before has run. */
    private val offline by lazy { OkHttpRm2Client(base, rm2.http) }

    @Test
    fun `a link is appended to the origin and nothing else happens to it`() {
        assertEquals(
            "https://192.168.1.42:18611/api/v1/media/IMG_0042.jpg/still?v=1a0b9c8d7e6f5041",
            offline.url("/api/v1/media/IMG_0042.jpg/still?v=1a0b9c8d7e6f5041"),
        )
    }

    @Test
    fun `an encoded space stays one escape and is never doubled`() {
        val link = "/api/v1/media/beach%20day%20%232.jpg/still?v=44cc21a0be7f1d93"

        val resolved = offline.url(link)

        assertEquals("https://192.168.1.42:18611$link", resolved)
        assertTrue("%2520 means the link was encoded twice", !resolved.contains("%2520"))
        assertTrue("%2523 means the link was encoded twice", !resolved.contains("%2523"))
        // Resolving what was already resolved must be a no-op, not another round of encoding.
        assertEquals(resolved, offline.url(resolved))
    }

    @Test
    fun `a plus in a filename survives as a plus`() {
        val link = "/api/v1/media/sunset+moon.jpg/thumb?v=deadbeefdeadbeef"

        val resolved = offline.url(link)

        assertEquals("https://192.168.1.42:18611$link", resolved)
        // Neither escaped into %2B nor form-decoded into a space - it is part of the filename.
        assertTrue(resolved.contains("sunset+moon.jpg"))
    }

    @Test
    fun `the links the snapshot actually carried resolve verbatim`() = runTest {
        rm2.enqueue(200, SNAPSHOT_RANKING)

        val snapshot = (rm2.client.session() as Rm2Result.Ok).value
        val right = snapshot.pair!!.right

        assertEquals("beach day #2.jpg", right.id)
        assertEquals(
            "https://${rm2.identity.host}:${rm2.identity.port}" +
                "/api/v1/media/beach%20day%20%232.jpg/still?v=44cc21a0be7f1d93",
            rm2.client.url(right.links.still!!),
        )
    }

    /**
     * The end of the story: a resolved link, fetched through the very client the app uses, arrives
     * at the server as the same bytes the server sent. If OkHttp were re-canonicalising anything on
     * the way out, this is where it would show.
     */
    @Test
    fun `a resolved link reaches the server as the exact path and query it started as`() = runTest {
        val link = "/api/v1/media/beach%20day%20%232.jpg/still?v=44cc21a0be7f1d93&w=1080"
        rm2.server.enqueue(MockResponse().setResponseCode(200).setBody("bytes"))

        rm2.http.newCall(Request.Builder().url(rm2.client.url(link)).get().build())
            .execute()
            .use { it.body?.string() }

        assertEquals(link, rm2.take().path)
    }

    @Test
    fun `a plus in a filename reaches the server as a plus`() = runTest {
        val link = "/api/v1/media/sunset+moon.jpg/thumb?v=deadbeefdeadbeef"
        rm2.server.enqueue(MockResponse().setResponseCode(200).setBody("bytes"))

        rm2.http.newCall(Request.Builder().url(rm2.client.url(link)).get().build())
            .execute()
            .use { it.body?.string() }

        assertEquals(link, rm2.take().path)
    }

    /**
     * The one that is not merely cosmetic. A media id is a filename (§ 11.1), so an id containing
     * `..` is a legal id the server encodes as `%2e%2e` - and a client that resolves the link
     * through a URL builder gets the escapes decoded and the dot segments collapsed, turning
     * `/api/v1/media/%2e%2e/x.jpg/still` into `/api/v1/x.jpg/still`. That is a different resource,
     * requested silently, and it is the shape `media_outside_session` (§ 5.5) exists to refuse.
     */
    @Test
    fun `encoded dot segments are not decoded and not collapsed`() {
        val link = "/api/v1/media/%2e%2e/x.jpg/still?v=0011223344556677"

        val resolved = offline.url(link)

        assertEquals("https://192.168.1.42:18611$link", resolved)
        assertTrue("the link was re-parsed and its dot segments resolved away", resolved.contains("%2e%2e"))
    }

    @Test
    fun `an absolute link is handed back untouched`() {
        val absolute = "https://elsewhere.example:443/api/v1/media/x.jpg/still?v=0011223344556677"

        assertEquals(absolute, offline.url(absolute))
    }
}
