package com.rankmaster2.phone.pairing

import com.rankmaster2.phone.net.ErrorCodes
import com.rankmaster2.phone.net.PairedDevice
import com.rankmaster2.phone.net.Ping
import com.rankmaster2.phone.net.Rm2Result
import com.rankmaster2.phone.ui.pairing.PairingClientFactory
import com.rankmaster2.phone.ui.pairing.PairingFlow
import com.rankmaster2.phone.ui.pairing.PairingOutcome
import com.rankmaster2.phone.ui.pairing.PairingPayload
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.IOException

/**
 * The sequence, which SERVER_SPEC.md § 10.1.1 makes normative: pin, then `/ping`, then and only
 * then the code. The test that matters most is the one proving `pair` is never reached.
 */
class PairingFlowTest {

    private val fp = "3b1f".repeat(16)
    private val other = "9c2e".repeat(16)

    private val payload = PairingPayload(
        host = "192.168.1.42",
        port = 18611,
        fingerprint = fp,
        code = "418250",
        expiresAtEpochSeconds = 1_789_000_240L,
    )

    private val device = PairedDevice(
        deviceId = "d-7f3a",
        deviceName = "Pixel 8",
        token = "tok_live_9aZ…",
        issuedAt = "2026-09-15T18:04:11.412Z",
        expiresAt = null,
    )

    private fun flow(
        pingResult: Rm2Result<Ping>,
        pairResult: Rm2Result<PairedDevice> = Rm2Result.Ok(device),
        credentials: FakeCredentials = FakeCredentials(),
    ): kotlin.Pair<PairingFlow, FakeRm2Client> {
        val client = FakeRm2Client("https://192.168.1.42:18611/api/v1", pingResult, pairResult)
        val factory = PairingClientFactory { _, _, _ -> client }
        return PairingFlow(factory, credentials) to client
    }

    // -- the order -------------------------------------------------------------------------------

    @Test
    fun `the ping comes before the code, always`() = runTest {
        val (flow, client) = flow(Rm2Result.Ok(ping("sha256:$fp")))

        flow.pair(payload, "Pixel 8")

        assertEquals(listOf("ping", "pair"), client.calls)
    }

    @Test
    fun `a fingerprint mismatch at the ping step means the code is never sent`() = runTest {
        val (flow, client) = flow(Rm2Result.Ok(ping("sha256:$other")))

        val outcome = flow.pair(payload, "Pixel 8")

        assertEquals(listOf("ping"), client.calls)
        assertNull("the bearer secret must not leave the phone", client.codeSent)
        assertTrue(outcome is PairingOutcome.CertificateMismatch)
        assertEquals(fp, (outcome as PairingOutcome.CertificateMismatch).expected)
        assertEquals(other, outcome.found)
    }

    @Test
    fun `nothing is stored when the certificate does not match`() = runTest {
        val credentials = FakeCredentials()
        val (flow, _) = flow(Rm2Result.Ok(ping("sha256:$other")), credentials = credentials)

        flow.pair(payload, "Pixel 8")

        assertNull(credentials.current())
        assertEquals(0, credentials.saves)
    }

    @Test
    fun `a pin mismatch in the TLS handshake also stops before the code`() = runTest {
        val (flow, client) = flow(
            Rm2Result.Unreachable(
                cause = IOException("pin"),
                detail = "certificate does not match",
                pinMismatch = true,
            )
        )

        val outcome = flow.pair(payload, "Pixel 8")

        assertEquals(listOf("ping"), client.calls)
        assertTrue(outcome is PairingOutcome.CertificateMismatch)
        assertNull((outcome as PairingOutcome.CertificateMismatch).found)
    }

    @Test
    fun `the fingerprint comparison survives the sha256 prefix and casing`() = runTest {
        val (flow, client) = flow(Rm2Result.Ok(ping("SHA256:${fp.uppercase()} ")))

        val outcome = flow.pair(payload, "Pixel 8")

        assertEquals(listOf("ping", "pair"), client.calls)
        assertTrue(outcome is PairingOutcome.Paired)
    }

    // -- success ---------------------------------------------------------------------------------

    @Test
    fun `a successful pairing stores exactly what came back`() = runTest {
        val credentials = FakeCredentials()
        val (flow, client) = flow(Rm2Result.Ok(ping("sha256:$fp")), credentials = credentials)

        val outcome = flow.pair(payload, "Pixel 8")

        assertTrue(outcome is PairingOutcome.Paired)
        val stored = credentials.current()!!
        assertEquals("192.168.1.42", stored.host)
        assertEquals(18611, stored.port)
        assertEquals("sha256:$fp", stored.certificateFingerprint)
        assertEquals("tok_live_9aZ…", stored.token)
        assertEquals("d-7f3a", stored.deviceId)
        assertEquals("https://192.168.1.42:18611/api/v1", stored.baseUrl)
        assertEquals(1, credentials.saves)

        // And exactly what was asked for went out.
        assertEquals("418250", client.codeSent)
        assertEquals("Pixel 8", client.deviceNameSent)
    }

    @Test
    fun `the stored identity is the one the outcome reports`() = runTest {
        val credentials = FakeCredentials()
        val (flow, _) = flow(Rm2Result.Ok(ping("sha256:$fp")), credentials = credentials)

        val outcome = flow.pair(payload, "Pixel 8") as PairingOutcome.Paired

        assertEquals(credentials.current(), outcome.identity)
        assertEquals(device, outcome.device)
    }

    // -- the refusals ----------------------------------------------------------------------------

    @Test
    fun `a wrong code comes back as invalid, with the attempts left when the server said`() = runTest {
        val (flow, _) = flow(
            Rm2Result.Ok(ping("sha256:$fp")),
            pairResult = Rm2Result.Refused(
                status = 401,
                code = ErrorCodes.INVALID_PAIRING_CODE,
                message = """That pairing code is not valid. {"attemptsRemaining":3}""",
            ),
        )

        val outcome = flow.pair(payload, "Pixel 8")

        assertEquals(PairingOutcome.InvalidCode(3), outcome)
    }

    @Test
    fun `a wrong code with no attempt count says so rather than inventing one`() = runTest {
        val (flow, _) = flow(
            Rm2Result.Ok(ping("sha256:$fp")),
            pairResult = Rm2Result.Refused(401, ErrorCodes.INVALID_PAIRING_CODE, "That pairing code is not valid."),
        )

        assertEquals(PairingOutcome.InvalidCode(null), flow.pair(payload, "Pixel 8"))
    }

    @Test
    fun `no pairing window open is its own outcome`() = runTest {
        val (flow, _) = flow(
            Rm2Result.Ok(ping("sha256:$fp")),
            pairResult = Rm2Result.Refused(403, ErrorCodes.PAIRING_NOT_OPEN, "No pairing window is open."),
        )

        assertEquals(PairingOutcome.NotOpen, flow.pair(payload, "Pixel 8"))
    }

    @Test
    fun `the rate limit is its own outcome`() = runTest {
        val (flow, _) = flow(
            Rm2Result.Ok(ping("sha256:$fp")),
            pairResult = Rm2Result.Refused(
                429,
                ErrorCodes.TOO_MANY_REQUESTS,
                """Too many attempts. {"retryAfterSeconds":41}""",
            ),
        )

        assertEquals(PairingOutcome.RateLimited(41), flow.pair(payload, "Pixel 8"))
    }

    @Test
    fun `nothing is stored when the server refuses the code`() = runTest {
        val credentials = FakeCredentials()
        val (flow, _) = flow(
            Rm2Result.Ok(ping("sha256:$fp")),
            pairResult = Rm2Result.Refused(401, ErrorCodes.INVALID_PAIRING_CODE, "no"),
            credentials = credentials,
        )

        flow.pair(payload, "Pixel 8")

        assertNull(credentials.current())
    }

    @Test
    fun `an unreachable PC never reaches the pair call`() = runTest {
        val (flow, client) = flow(
            Rm2Result.Unreachable(IOException("timeout"), "connect timed out")
        )

        val outcome = flow.pair(payload, "Pixel 8")

        assertEquals(listOf("ping"), client.calls)
        assertTrue(outcome is PairingOutcome.Unreachable)
        assertFalse((outcome as PairingOutcome.Unreachable).detail.isEmpty())
    }

    @Test
    fun `a server that is still starting up is not asked to pair`() = runTest {
        val (flow, client) = flow(Rm2Result.Ok(ping("sha256:$fp", ready = false)))

        val outcome = flow.pair(payload, "Pixel 8")

        assertEquals(listOf("ping"), client.calls)
        assertTrue(outcome is PairingOutcome.Unreachable)
    }

    @Test
    fun `an unexpected refusal keeps its code rather than being flattened`() = runTest {
        val (flow, _) = flow(
            Rm2Result.Ok(ping("sha256:$fp")),
            pairResult = Rm2Result.Refused(503, "server_shutting_down", "Shutting down."),
        )

        val outcome = flow.pair(payload, "Pixel 8")

        assertEquals("server_shutting_down", (outcome as PairingOutcome.Refused).code)
    }

    @Test
    fun `the client is built for the address and pin the payload named`() = runTest {
        var seen: Triple<String, Int, String>? = null
        val client = FakeRm2Client(
            "https://192.168.1.42:18611/api/v1",
            Rm2Result.Ok(ping("sha256:$fp")),
            Rm2Result.Ok(device),
        )
        val flow = PairingFlow(
            { host, port, fingerprint ->
                seen = Triple(host, port, fingerprint)
                client
            },
            FakeCredentials(),
        )

        flow.pair(payload, "Pixel 8")

        assertEquals(Triple("192.168.1.42", 18611, fp), seen)
    }
}
