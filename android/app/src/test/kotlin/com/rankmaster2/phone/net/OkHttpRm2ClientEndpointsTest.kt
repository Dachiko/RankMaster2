package com.rankmaster2.phone.net

import kotlinx.coroutines.test.runTest
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

/**
 * Every endpoint of SERVER_SPEC.md § 10: the method, the path, the query string and the body.
 *
 * These are the tests that break when someone "tidies" a field name. That matters most for the four
 * actions, where the body is the entire no-double-vote mechanism: a `pairToken` that does not arrive
 * is a vote the server cannot refuse as a duplicate, and a `clientRequestId` that does not arrive is
 * a retry the client can never afterwards tell apart from a second vote (§ 8.5).
 */
class OkHttpRm2ClientEndpointsTest {

    private lateinit var rm2: Rm2TestServer

    @Before fun setUp() { rm2 = Rm2TestServer() }

    @After fun tearDown() { rm2.close() }

    // -- connection ----------------------------------------------------------------------------

    @Test
    fun `ping reads the fingerprint and ignores features and limits`() = runTest {
        rm2.enqueue(200, PING_BODY)

        val result = rm2.client.ping()

        val ping = (result as Rm2Result.Ok).value
        assertEquals("v1", ping.apiVersion)
        assertEquals("1.1.3", ping.version)
        assertTrue(ping.ready)
        assertTrue(ping.authenticated)
        assertEquals(
            "sha256:3b1f00000000000000000000000000000000000000000000000000000000beef",
            ping.certificateFingerprint,
        )

        val request = rm2.take()
        assertEquals("GET", request.method)
        assertEquals("/api/v1/ping", request.path)
    }

    @Test
    fun `every request carries the bearer token the pinned client was built with`() = runTest {
        rm2.enqueue(200, SNAPSHOT_RANKING)

        rm2.client.session()

        assertEquals("Bearer ${rm2.token}", rm2.take().getHeader("Authorization"))
    }

    @Test
    fun `pair posts the code and the device name`() = runTest {
        rm2.enqueue(
            201,
            """{"deviceId":"dev_7h2k9qp4","deviceName":"Pixel 8","token":"rm2_9Qk3","""+
                """"issuedAt":"2026-09-12T18:04:11.412Z","expiresAt":null}""",
        )

        val paired = (rm2.client.pair("418 250", "Pixel 8") as Rm2Result.Ok).value
        assertEquals("dev_7h2k9qp4", paired.deviceId)
        assertEquals("rm2_9Qk3", paired.token)
        assertNull(paired.expiresAt)

        val request = rm2.take()
        assertEquals("POST", request.method)
        assertEquals("/api/v1/pair", request.path)
        assertTrue(request.getHeader("Content-Type")!!.startsWith("application/json"))
        val body = request.jsonBody()
        assertEquals("418 250", body.str("code"))
        assertEquals("Pixel 8", body.str("deviceName"))
    }

    @Test
    fun `revoke deletes the device and accepts 204 with no body`() = runTest {
        rm2.enqueueNoContent()

        assertTrue(rm2.client.revoke("dev_7h2k9qp4") is Rm2Result.Ok)

        val request = rm2.take()
        assertEquals("DELETE", request.method)
        assertEquals("/api/v1/pair/dev_7h2k9qp4", request.path)
    }

    // -- picking a folder ----------------------------------------------------------------------

    @Test
    fun `roots keeps an unavailable root instead of dropping it`() = runTest {
        rm2.enqueue(
            200,
            """
            {"roots":[
              {"path":"C:\\","label":"System","kind":"fixed","available":true,
               "totalBytes":511000000000,"freeBytes":120000000000},
              {"path":"E:\\","label":"DVD","kind":"removable","available":false,
               "totalBytes":null,"freeBytes":null}
            ]}
            """,
        )

        val roots = (rm2.client.roots() as Rm2Result.Ok).value.roots
        assertEquals(2, roots.size)
        assertEquals(false, roots[1].available)
        assertNull(roots[1].totalBytes)
        assertEquals("/api/v1/libraries/roots", rm2.take().path)
    }

    @Test
    fun `browse sends the path and counts as query parameters`() = runTest {
        rm2.enqueue(
            200,
            """
            {"path":"D:\\photos","parent":"D:\\","entries":[
              {"name":"trip","path":"D:\\photos\\trip","stillCount":412,"videoCount":3,
               "rankable":true,"hasDatabase":true,"accessible":true},
              {"name":"System Volume Information","path":"D:\\photos\\System Volume Information",
               "stillCount":null,"videoCount":null,"rankable":null,
               "hasDatabase":false,"accessible":false}
            ]}
            """,
        )

        val browse = (rm2.client.browse("D:\\photos") as Rm2Result.Ok).value
        assertEquals("D:\\", browse.parent)
        assertEquals(true, browse.entries[0].rankable)
        // § 10.15: null is "not counted", and is not the same answer as 0.
        assertNull(browse.entries[1].stillCount)
        assertNull(browse.entries[1].rankable)
        assertEquals(false, browse.entries[1].accessible)

        val request = rm2.take()
        assertEquals("GET", request.method)
        assertTrue(request.path!!.startsWith("/api/v1/libraries/browse?"))
        assertEquals("D:\\photos", request.requestUrl!!.queryParameter("path"))
        assertEquals("true", request.requestUrl!!.queryParameter("counts"))
    }

    @Test
    fun `browse passes counts false through rather than dropping it`() = runTest {
        rm2.enqueue(200, """{"path":"D:\\","parent":null,"entries":[]}""")

        rm2.client.browse("D:\\", counts = false)

        assertEquals("false", rm2.take().requestUrl!!.queryParameter("counts"))
    }

    // -- the session ---------------------------------------------------------------------------

    @Test
    fun `openSession posts the folder and parses the 201 snapshot`() = runTest {
        rm2.enqueue(201, SNAPSHOT_RANKING)

        val snapshot = (rm2.client.openSession("D:\\photos\\trip") as Rm2Result.Ok).value
        assertEquals("Qv8kZ2r5tN0pXbA1cD3eFg", snapshot.sessionId)
        assertTrue(snapshot.isRanking)
        assertEquals(412, snapshot.counts.rankable)
        assertEquals(listOf("confirmation", "upset", "confirmation", "confirmation"), snapshot.cues)
        assertEquals("beach day #2.jpg", snapshot.pair!!.right.id)
        assertEquals(1, snapshot.warmPairs.size)

        val request = rm2.take()
        assertEquals("POST", request.method)
        assertEquals("/api/v1/session", request.path)
        assertEquals("D:\\photos\\trip", request.jsonBody().str("folder"))
    }

    @Test
    fun `session reads the snapshot and exhausted has no pair and no token`() = runTest {
        rm2.enqueue(200, SNAPSHOT_EXHAUSTED)

        val snapshot = (rm2.client.session() as Rm2Result.Ok).value
        assertTrue(snapshot.isExhausted)
        assertNull(snapshot.pair)
        assertNull(snapshot.pairToken)
        assertEquals(5L, snapshot.pairSeq)

        val request = rm2.take()
        assertEquals("GET", request.method)
        assertEquals("/api/v1/session", request.path)
    }

    @Test
    fun `closeSession deletes the session`() = runTest {
        rm2.enqueueNoContent()

        assertTrue(rm2.client.closeSession() is Rm2Result.Ok)

        val request = rm2.take()
        assertEquals("DELETE", request.method)
        assertEquals("/api/v1/session", request.path)
    }

    @Test
    fun `save posts JSON so the server cannot answer 415`() = runTest {
        rm2.enqueue(200, SNAPSHOT_RANKING)

        val snapshot = (rm2.client.save() as Rm2Result.Ok).value
        assertEquals("2026-09-12T18:04:11.398Z", snapshot.lastSavedAt)

        val request = rm2.take()
        assertEquals("POST", request.method)
        assertEquals("/api/v1/session/save", request.path)
        // § 2: anything but application/json is 415, and OkHttp puts no type on an empty POST.
        assertTrue(request.getHeader("Content-Type")!!.startsWith("application/json"))
    }

    // -- the actions ---------------------------------------------------------------------------

    @Test
    fun `vote sends pairToken winner and clientRequestId and parses the new snapshot`() = runTest {
        rm2.enqueue(200, SNAPSHOT_RANKING)

        val snapshot = (rm2.client.vote("7tRbQ0xW9mKa2ZpL4nVdCe", Sides.LEFT, "req-vote-1")
            as Rm2Result.Ok).value
        assertEquals("M0xQrT8vB3nJ7yE2sW9uHk", snapshot.pairToken)
        assertEquals(14L, snapshot.pairSeq)
        assertEquals("vote", snapshot.lastAction!!.type)

        val request = rm2.take()
        assertEquals("POST", request.method)
        assertEquals("/api/v1/session/vote", request.path)
        val body = request.jsonBody()
        assertEquals("7tRbQ0xW9mKa2ZpL4nVdCe", body.str("pairToken"))
        assertEquals("left", body.str("winner"))
        assertEquals("req-vote-1", body.str("clientRequestId"))
        // § 9.1: pairSeq is not a substitute for the token and MUST NOT be sent in an action body.
        assertNull(body["pairSeq"])
    }

    @Test
    fun `skip sends pairToken and clientRequestId and nothing else`() = runTest {
        rm2.enqueue(200, SNAPSHOT_RANKING)

        rm2.client.skip("7tRbQ0xW9mKa2ZpL4nVdCe", "req-skip-1")

        val request = rm2.take()
        assertEquals("/api/v1/session/skip", request.path)
        val body = request.jsonBody()
        assertEquals(setOf("pairToken", "clientRequestId"), body.keys)
        assertEquals("7tRbQ0xW9mKa2ZpL4nVdCe", body.str("pairToken"))
        assertEquals("req-skip-1", body.str("clientRequestId"))
    }

    @Test
    fun `discard sends pairToken side and clientRequestId, and never an id`() = runTest {
        rm2.enqueue(200, SNAPSHOT_EXHAUSTED)

        val snapshot = (rm2.client.discard("M0xQrT8vB3nJ7yE2sW9uHk", Sides.RIGHT, "req-discard-1")
            as Rm2Result.Ok).value
        assertEquals("discard", snapshot.lastAction!!.type)
        assertEquals("IMG_0311.jpg", snapshot.lastAction!!.id)

        val request = rm2.take()
        assertEquals("/api/v1/session/discard", request.path)
        val body = request.jsonBody()
        assertEquals(setOf("pairToken", "side", "clientRequestId"), body.keys)
        assertEquals("M0xQrT8vB3nJ7yE2sW9uHk", body.str("pairToken"))
        assertEquals("right", body.str("side"))
        assertEquals("req-discard-1", body.str("clientRequestId"))
    }

    @Test
    fun `special hits its own path with the discard body`() = runTest {
        rm2.enqueue(200, SNAPSHOT_RANKING)

        rm2.client.special("M0xQrT8vB3nJ7yE2sW9uHk", Sides.LEFT, "req-special-1")

        val request = rm2.take()
        assertEquals("/api/v1/session/special", request.path)
        val body = request.jsonBody()
        assertEquals(setOf("pairToken", "side", "clientRequestId"), body.keys)
        assertEquals("left", body.str("side"))
    }

    @Test
    fun `cancel posts to undo with a clientRequestId and deliberately no pairToken`() = runTest {
        rm2.enqueue(200, SNAPSHOT_RANKING)

        rm2.client.cancel("req-cancel-1")

        val request = rm2.take()
        assertEquals("POST", request.method)
        assertEquals("/api/v1/session/undo", request.path)
        val body = request.jsonBody()
        assertEquals("req-cancel-1", body.str("clientRequestId"))
        // § 10.10: undo is not pair-scoped. Any token the client holds for it is stale by
        // construction, so sending one would only suggest otherwise.
        assertNull(body["pairToken"])
    }

    @Test
    fun `a retried action repeats the same token, which is what makes one vote stay one vote`() =
        runTest {
            rm2.enqueue(200, SNAPSHOT_RANKING)
            rm2.enqueue(409, stalePairToken("7tRbQ0xW9mKa2ZpL4nVdCe"))

            rm2.client.vote("7tRbQ0xW9mKa2ZpL4nVdCe", Sides.LEFT, "req-vote-1")
            rm2.client.vote("7tRbQ0xW9mKa2ZpL4nVdCe", Sides.LEFT, "req-vote-1")

            val first = rm2.take().jsonBody()
            val second = rm2.take().jsonBody()
            assertEquals(first.str("pairToken"), second.str("pairToken"))
            assertEquals(first.str("clientRequestId"), second.str("clientRequestId"))
        }
}
