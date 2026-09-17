package com.rankmaster2.phone.media

import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

/**
 * A failed media request has to arrive at the pane as a *reason*, not as "it failed".
 *
 * SERVER_SPEC.md § 4 requires branching on `error.code`, and § 11.3 requires treating a media `404`
 * as "this id is gone" rather than as a transport hiccup. The consequence on screen is concrete:
 * `media_file_missing` is the one case where the client offers to discard a file the user still
 * believes they own, so getting it confused with anything else is destructive.
 */
class MediaErrorsTest {

    private lateinit var server: MockWebServer
    private lateinit var client: OkHttpClient

    @Before
    fun setUp() {
        server = MockWebServer().apply { start() }
        client = OkHttpClient.Builder().addInterceptor(MediaErrorInterceptor()).build()
    }

    @After
    fun tearDown() {
        server.shutdown()
    }

    private fun get(path: String = "/api/v1/media/x.jpg/still"): okhttp3.Response =
        client.newCall(Request.Builder().url(server.url(path)).build()).execute()

    @Test
    fun `a 422 arrives as media_decode_failed, and the pane says so instead of crashing`() {
        server.enqueue(MediaFixtures.errorResponse(422, "media_decode_failed"))

        val thrown = assertThrows(MediaHttpException::class.java) { get() }

        assertEquals(422, thrown.status)
        assertEquals("media_decode_failed", thrown.code)
        assertEquals(MediaPaneState.Undecodable, MediaFailures.stateOf(thrown))
    }

    @Test
    fun `a 404 media_file_missing is the cue to offer discarding that side`() {
        server.enqueue(MediaFixtures.errorResponse(404, "media_file_missing"))

        val thrown = assertThrows(MediaHttpException::class.java) { get() }

        assertEquals("media_file_missing", thrown.code)
        assertEquals(MediaPaneState.Gone, MediaFailures.stateOf(thrown))
    }

    @Test
    fun `a 404 unknown_media_id is also gone - the id left the session`() {
        server.enqueue(MediaFixtures.errorResponse(404, "unknown_media_id"))
        assertEquals(MediaPaneState.Gone, MediaFailures.stateOf(assertThrows(MediaHttpException::class.java) { get() }))
    }

    @Test
    fun `a 404 no_session is NOT gone - discarding on it would throw away a file that is still there`() {
        // Same status, opposite meaning. This is why the code is read and not just the status.
        server.enqueue(MediaFixtures.errorResponse(404, "no_session"))

        val state = MediaFailures.stateOf(assertThrows(MediaHttpException::class.java) { get() })

        assertTrue("no_session must not read as a missing file", state is MediaPaneState.Unavailable)
        assertEquals("no_session", (state as MediaPaneState.Unavailable).code)
    }

    @Test
    fun `a 409 wrong_media_kind is a client bug, not a missing file`() {
        server.enqueue(MediaFixtures.errorResponse(409, "wrong_media_kind"))
        val state = MediaFailures.stateOf(assertThrows(MediaHttpException::class.java) { get() })
        assertTrue(state is MediaPaneState.Unavailable)
        assertEquals("wrong_media_kind", (state as MediaPaneState.Unavailable).code)
    }

    @Test
    fun `a 400 unsupported_width is a client bug too - and is why the width list is enforced locally`() {
        server.enqueue(MediaFixtures.errorResponse(400, "unsupported_width"))
        val state = MediaFailures.stateOf(assertThrows(MediaHttpException::class.java) { get() })
        assertEquals("unsupported_width", (state as MediaPaneState.Unavailable).code)
    }

    @Test
    fun `a 404 with no envelope at all still means gone, per section 11_3`() {
        server.enqueue(MockResponse().setResponseCode(404).setBody("<html>nginx</html>"))

        val thrown = assertThrows(MediaHttpException::class.java) { get() }

        assertEquals(null, thrown.code)
        assertEquals(MediaPaneState.Gone, MediaFailures.stateOf(thrown))
    }

    @Test
    fun `a 401 is never a reason to discard a photograph`() {
        server.enqueue(MediaFixtures.errorResponse(401, "invalid_token"))
        val state = MediaFailures.stateOf(assertThrows(MediaHttpException::class.java) { get() })
        assertTrue(state is MediaPaneState.Unavailable)
    }

    @Test
    fun `a network failure is not gone either`() {
        val state = MediaFailures.stateOf(java.net.SocketTimeoutException("timeout"))
        assertTrue(state is MediaPaneState.Unavailable)
        assertEquals(null, (state as MediaPaneState.Unavailable).code)
    }

    @Test
    fun `a reason wrapped in another exception is still found`() {
        // Coil and Media3 both wrap what the data source threw.
        val wrapped = RuntimeException("load failed", MediaHttpException(404, "media_file_missing", "u", "m"))
        assertEquals(MediaPaneState.Gone, MediaFailures.stateOf(wrapped))
    }

    @Test
    fun `no room on the PC is not the same answer as an unreadable file`() {
        // SERVER_SPEC.md § 5.2: media_decode_failed carries details.reason. The two cases share one
        // code, so before this field the phone had to pick one sentence for both — and picked the
        // one that blamed the owner's photograph and his phone for the PC being briefly busy.
        assertEquals(
            MediaPaneState.NoRoomOnThePc,
            MediaFailures.stateOf(422, "media_decode_failed", "busy", reason = "no_room"),
        )
        assertEquals(
            MediaPaneState.Undecodable,
            MediaFailures.stateOf(422, "media_decode_failed", "bad bytes", reason = "unreadable"),
        )
        // An older server sends no reason at all. Falling back to Undecodable keeps today's
        // behaviour rather than inventing an optimistic one from a field that is not there.
        assertEquals(
            MediaPaneState.Undecodable,
            MediaFailures.stateOf(422, "media_decode_failed", "no details"),
        )
    }

    @Test
    fun `the reason is read off the wire, not just accepted as an argument`() {
        server.enqueue(MediaFixtures.errorResponse(422, "media_decode_failed", reason = "no_room"))

        val thrown = runCatching { get().close() }.exceptionOrNull()
        val http = generateSequence(thrown) { it.cause }.filterIsInstance<MediaHttpException>().first()

        assertEquals("no_room", http.reason)
        assertEquals(MediaPaneState.NoRoomOnThePc, MediaFailures.stateOf(http))
    }

    @Test
    fun `a success passes straight through, untouched`() {
        server.enqueue(MediaFixtures.stillResponse())

        get().use { response ->
            assertEquals(200, response.code)
            assertEquals(MediaFixtures.PNG.size, response.body!!.bytes().size)
        }
    }
}
