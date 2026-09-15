package com.rankmaster2.phone.net

import com.rankmaster2.phone.net.impl.OkHttpRm2Client
import com.rankmaster2.phone.store.ServerIdentity
import kotlinx.coroutines.test.runTest
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

/**
 * The pin, end to end, against a server presenting a certificate this device did not pair with.
 *
 * This is the app's entire security story. The server signs its own certificate, so there is no
 * authority to appeal to and nothing else to check: if the fingerprint does not match, the machine
 * answering is not the owner's PC. The two things that must be true are that the call fails, and
 * that **nothing was sent** - a pin that is consulted after the bearer token has gone out is not a
 * pin, it is a log entry.
 */
class OkHttpRm2ClientPinningTest {

    private lateinit var server: MockWebServer
    private lateinit var certificate: SelfSignedCertificate

    @Before
    fun setUp() {
        certificate = SelfSignedCertificate()
        server = MockWebServer()
        server.useHttps(certificate.sslSocketFactory(), false)
        server.start()
    }

    @After
    fun tearDown() {
        server.shutdown()
    }

    private fun clientPinnedTo(fingerprint: String): OkHttpRm2Client = OkHttpRm2Client(
        ServerIdentity(
            host = server.hostName,
            port = server.port,
            certificateFingerprint = fingerprint,
            token = "rm2_secret_that_must_not_leave_this_phone",
        )
    )

    @Test
    fun `the wrong fingerprint is Unreachable with pinMismatch, and nothing reached the server`() =
        runTest {
            server.enqueue(MockResponse().setResponseCode(200).setBody(PING_BODY))
            val wrong = "sha256:" + "0".repeat(64)

            val result = clientPinnedTo(wrong).ping()

            val unreachable = result as Rm2Result.Unreachable
            assertTrue(
                "a certificate mismatch must be reported as a pin mismatch, not as a flaky network",
                unreachable.pinMismatch,
            )
            // The handshake failed, so the request line - and the bearer token on it - never went.
            assertEquals(0, server.requestCount)
        }

    @Test
    fun `the right fingerprint connects and the call succeeds`() = runTest {
        server.enqueue(
            MockResponse()
                .setResponseCode(200)
                .setHeader("Content-Type", "application/json; charset=utf-8")
                .setBody(PING_BODY),
        )

        val result = clientPinnedTo(Rm2Http.fingerprintOf(certificate.certificate)).ping()

        assertEquals("v1", (result as Rm2Result.Ok).value.apiVersion)
        assertEquals(1, server.requestCount)
    }

    @Test
    fun `an ordinary connection failure is Unreachable but not a pin mismatch`() = runTest {
        val good = Rm2Http.fingerprintOf(certificate.certificate)
        val client = clientPinnedTo(good)
        server.shutdown()

        val result = client.ping()

        val unreachable = result as Rm2Result.Unreachable
        assertFalse(
            "a server that is merely off must not be presented to the user as an attack",
            unreachable.pinMismatch,
        )
    }

    @Test
    fun `a mismatch on an action is a mismatch too, not a refusal to be retried`() = runTest {
        server.enqueue(MockResponse().setResponseCode(200).setBody(SNAPSHOT_RANKING))
        val wrong = "sha256:" + "f".repeat(64)

        val result = clientPinnedTo(wrong).vote("M0xQrT8vB3nJ7yE2sW9uHk", Sides.LEFT, "req-1")

        assertTrue((result as Rm2Result.Unreachable).pinMismatch)
        assertEquals(0, server.requestCount)
    }

    @Test
    fun `fingerprintOf produces the sha256 form the server publishes and the pin compares`() {
        val fingerprint = Rm2Http.fingerprintOf(certificate.certificate)

        assertTrue(fingerprint.startsWith("sha256:"))
        assertEquals(7 + 64, fingerprint.length)
        assertTrue(fingerprint.removePrefix("sha256:").all { it in "0123456789abcdef" })
    }
}
