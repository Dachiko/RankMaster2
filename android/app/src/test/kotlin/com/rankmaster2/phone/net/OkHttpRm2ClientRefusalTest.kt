package com.rankmaster2.phone.net

import com.rankmaster2.phone.net.impl.Rm2SyntheticCodes
import kotlinx.coroutines.test.runTest
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

/**
 * The § 4 envelope and the § 5 codes.
 *
 * The one that earns its keep is the 409: `stale_pair_token` carries the **whole** current snapshot
 * (§ 8.5), and a client that parses only `code` out of it has to go and fetch the state it was just
 * handed. Every extra round trip there is a window in which a user can vote into a pair that is
 * already gone, so `session` being parsed is not a nicety.
 */
class OkHttpRm2ClientRefusalTest {

    private lateinit var rm2: Rm2TestServer

    @Before fun setUp() { rm2 = Rm2TestServer() }

    @After fun tearDown() { rm2.close() }

    @Test
    fun `409 stale_pair_token arrives with the complete current state parsed`() = runTest {
        rm2.enqueue(409, stalePairToken("7tRbQ0xW9mKa2ZpL4nVdCe"))

        val result = rm2.client.vote("7tRbQ0xW9mKa2ZpL4nVdCe", Sides.LEFT, "req-1")

        val refused = result as Rm2Result.Refused
        assertEquals(409, refused.status)
        assertEquals(ErrorCodes.STALE_PAIR_TOKEN, refused.code)
        assertEquals("01J8Z5K0QF3V8A0M6R9Q2B7T4C", refused.requestId)

        // The point of the whole exercise: the client is resynchronised by this one response.
        assertNotNull(refused.session)
        val session = refused.session!!
        assertEquals("M0xQrT8vB3nJ7yE2sW9uHk", session.pairToken)
        assertEquals(14L, session.pairSeq)
        assertEquals("Qv8kZ2r5tN0pXbA1cD3eFg", session.sessionId)
        assertEquals("IMG_0042.jpg", session.pair!!.left.id)
        assertEquals(13, session.sessionVotes)
        assertEquals(1, session.warmPairs.size)

        // § 8.5: lastAction is how the client decides whether its lost request landed.
        assertEquals("1f0c2a7e-6b41-4f0a-9f6a-2b3c4d5e6f70", session.lastAction!!.clientRequestId)
        assertEquals("7tRbQ0xW9mKa2ZpL4nVdCe", session.lastAction!!.pairToken)

        // And that is enough: no second request went out.
        assertEquals(1, rm2.server.requestCount)
    }

    @Test
    fun `snapshotOrNull reaches the snapshot inside a refusal`() = runTest {
        rm2.enqueue(409, stalePairToken("7tRbQ0xW9mKa2ZpL4nVdCe"))

        val snapshot = rm2.client.skip("7tRbQ0xW9mKa2ZpL4nVdCe", "req-1").snapshotOrNull()

        assertEquals("M0xQrT8vB3nJ7yE2sW9uHk", snapshot!!.pairToken)
    }

    @Test
    fun `409 stale_pair_token on an exhausted session carries a snapshot with a null token`() =
        runTest {
            rm2.enqueue(409, stalePairToken("M0xQrT8vB3nJ7yE2sW9uHk", SNAPSHOT_EXHAUSTED))

            val refused = rm2.client.discard("M0xQrT8vB3nJ7yE2sW9uHk", Sides.LEFT, "req-1")
                as Rm2Result.Refused

            assertEquals(ErrorCodes.STALE_PAIR_TOKEN, refused.code)
            assertTrue(refused.session!!.isExhausted)
            assertNull(refused.session!!.pairToken)
        }

    @Test
    fun `404 no_session is a refusal with the code, not an exception`() = runTest {
        rm2.enqueue(404, errorEnvelope("no_session", "No session is open."))

        val refused = rm2.client.session() as Rm2Result.Refused

        assertEquals(404, refused.status)
        assertEquals(ErrorCodes.NO_SESSION, refused.code)
        assertEquals("No session is open.", refused.message)
        // § 4: no session, so nothing to attach.
        assertNull(refused.session)
    }

    @Test
    fun `500 save_failed keeps its code even though details is a shape the client does not model`() =
        runTest {
            rm2.enqueue(
                500,
                errorEnvelope(
                    "save_failed",
                    "The library could not be written.",
                    details = """{ "recordsChanged": false, "fileMoved": false }""",
                ),
            )

            val refused = rm2.client.save() as Rm2Result.Refused

            assertEquals(500, refused.status)
            assertEquals(ErrorCodes.SAVE_FAILED, refused.code)
            assertEquals("The library could not be written.", refused.message)
        }

    @Test
    fun `423 folder_locked and 503 session_busy are ordinary refusals`() = runTest {
        rm2.enqueue(
            423,
            errorEnvelope("folder_locked", "That folder is open elsewhere.", """{ "holder": null }"""),
        )
        rm2.enqueue(
            503,
            errorEnvelope("session_busy", "The session is busy.", """{ "retryAfterSeconds": 1 }"""),
        )

        val locked = rm2.client.openSession("D:\\photos\\trip") as Rm2Result.Refused
        assertEquals(423, locked.status)
        assertEquals("folder_locked", locked.code)

        val busy = rm2.client.session() as Rm2Result.Refused
        assertEquals(503, busy.status)
        assertEquals(ErrorCodes.SESSION_BUSY, busy.code)
        // § 4: a 503 never carries a snapshot - producing one needs the lock that is the failure.
        assertNull(busy.session)
    }

    @Test
    fun `401 unauthenticated is a refusal and carries no session`() = runTest {
        rm2.enqueue(401, errorEnvelope("unauthenticated", "Missing bearer token."))

        val refused = rm2.client.roots() as Rm2Result.Refused

        assertEquals(401, refused.status)
        assertEquals(ErrorCodes.UNAUTHENTICATED, refused.code)
        assertNull(refused.session)
    }

    @Test
    fun `409 session_already_open refuses without a snapshot the server did not send`() = runTest {
        rm2.enqueue(
            409,
            errorEnvelope(
                "session_already_open",
                "A session is open on another folder.",
                """{ "openFolder": "D:\\photos\\other" }""",
            ),
        )

        val refused = rm2.client.openSession("D:\\photos\\trip") as Rm2Result.Refused

        assertEquals(ErrorCodes.SESSION_ALREADY_OPEN, refused.code)
        assertNull(refused.session)
    }

    // -- when the answer is not the shape it should be -----------------------------------------

    @Test
    fun `a non-2xx with no envelope is refused with a synthetic code, never a crash`() = runTest {
        rm2.server.enqueue(
            okhttp3.mockwebserver.MockResponse()
                .setResponseCode(502)
                .setHeader("X-Request-Id", "rq-502")
                .setBody("<html>Bad Gateway</html>"),
        )

        val refused = rm2.client.session() as Rm2Result.Refused

        assertEquals(502, refused.status)
        assertEquals(Rm2SyntheticCodes.MALFORMED_ERROR, refused.code)
        assertEquals("rq-502", refused.requestId)
    }

    @Test
    fun `a non-2xx with an empty body is refused, not reported as unreachable`() = runTest {
        rm2.server.enqueue(okhttp3.mockwebserver.MockResponse().setResponseCode(401))

        val refused = rm2.client.session() as Rm2Result.Refused

        assertEquals(401, refused.status)
        assertEquals(Rm2SyntheticCodes.MALFORMED_ERROR, refused.code)
    }

    @Test
    fun `a 2xx whose body is not a snapshot is refused with a synthetic code`() = runTest {
        rm2.enqueue(200, """{"state":"ranking"}""")

        val refused = rm2.client.session() as Rm2Result.Refused

        assertEquals(200, refused.status)
        assertEquals(Rm2SyntheticCodes.MALFORMED_RESPONSE, refused.code)
        assertEquals("01J8Z5K0QF3V8A0M6R9Q2B7T4C", refused.requestId)
    }

    @Test
    fun `an unreadable embedded snapshot costs the snapshot and not the code`() = runTest {
        // The refusal itself is still actionable; only the free resync is lost.
        rm2.enqueue(
            409,
            """
            {"error":{"code":"stale_pair_token","message":"Stale.","requestId":"rq-1",
              "session":{"state":"ranking"}}}
            """,
        )

        val refused = rm2.client.vote("t", Sides.LEFT, "req-1") as Rm2Result.Refused

        assertEquals(ErrorCodes.STALE_PAIR_TOKEN, refused.code)
        assertEquals("rq-1", refused.requestId)
        assertNull(refused.session)
    }

    @Test
    fun `the requestId falls back to the header when the envelope omits it`() = runTest {
        rm2.enqueue(
            404,
            """{"error":{"code":"no_session","message":"None."}}""",
            requestId = "rq-from-header",
        )

        val refused = rm2.client.session() as Rm2Result.Refused

        assertEquals(ErrorCodes.NO_SESSION, refused.code)
        assertEquals("rq-from-header", refused.requestId)
    }
}
