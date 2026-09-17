package com.rankmaster2.phone.net

import com.rankmaster2.phone.net.impl.OkHttpRm2Client
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.test.runTest
import okhttp3.mockwebserver.MockResponse
import org.junit.After
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

/**
 * § 2.7 (the second audit): a PC that accepts the connection and then stalls used to hold a call
 * open for the full 30 s `readTimeout` `Rm2Http` sets, and `RankViewModel.sendWithRetries` resends
 * once on top of that - so one tap could cost a full minute of a screen with nothing on it to say
 * why. `OkHttpRm2Client`'s `bounded` client puts a ceiling on the *whole* call instead of trusting
 * the per-phase timeouts alone.
 *
 * This proves the bound actually fires rather than trusting the wiring by inspection: a
 * one-second override against a server that accepts the connection and then never answers must come
 * back `Unreachable` in close to that one second, not the 5 s the server was told to sit on the
 * response for.
 */
class OkHttpRm2ClientCallTimeoutTest {

    private lateinit var rm2: Rm2TestServer

    @Before fun setUp() { rm2 = Rm2TestServer() }

    @After fun tearDown() { rm2.close() }

    @Test
    fun `a call that outruns its timeout comes back unreachable, not hung`() = runTest {
        rm2.server.enqueue(
            MockResponse()
                .setResponseCode(200)
                .setHeader("Content-Type", "application/json; charset=utf-8")
                .setBody(PING_BODY)
                .setBodyDelay(5, TimeUnit.SECONDS),
        )
        val bounded = OkHttpRm2Client(rm2.identity.baseUrl, rm2.http, callTimeoutSeconds = 1)

        val startedAt = System.nanoTime()
        val result = bounded.ping()
        val elapsedMs = (System.nanoTime() - startedAt) / 1_000_000

        assertTrue("a stalled server must be reported unreachable, not left pending", result is Rm2Result.Unreachable)
        assertTrue(
            "elapsed was ${elapsedMs}ms - the call must be cut off close to its one-second bound",
            elapsedMs in 0..4000,
        )
    }

    @Test
    fun `a call that answers well inside its timeout is unaffected by it`() = runTest {
        rm2.enqueue(200, PING_BODY)
        val bounded = OkHttpRm2Client(rm2.identity.baseUrl, rm2.http, callTimeoutSeconds = 1)

        val result = bounded.ping()

        assertTrue("an ordinary fast answer must not be caught by the same bound", result is Rm2Result.Ok)
    }
}
