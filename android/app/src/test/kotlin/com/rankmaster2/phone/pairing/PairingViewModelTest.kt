package com.rankmaster2.phone.pairing

import com.rankmaster2.phone.net.ErrorCodes
import com.rankmaster2.phone.net.Rm2Result
import com.rankmaster2.phone.ui.pairing.ManualEntry
import com.rankmaster2.phone.ui.pairing.PairingClientFactory
import com.rankmaster2.phone.ui.pairing.PairingFlow
import com.rankmaster2.phone.ui.pairing.PairingMode
import com.rankmaster2.phone.ui.pairing.PairingViewModel
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.StandardTestDispatcher
import kotlinx.coroutines.test.advanceUntilIdle
import kotlinx.coroutines.test.resetMain
import kotlinx.coroutines.test.runTest
import kotlinx.coroutines.test.setMain
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import java.io.IOException

/**
 * The screen's behaviour, without a screen. Everything the composables do is a function of this
 * state, so this is where "a certificate mismatch is a dead end" is actually enforced.
 */
@OptIn(ExperimentalCoroutinesApi::class)
class PairingViewModelTest {

    private val dispatcher = StandardTestDispatcher()
    private val fp = "3b1f".repeat(16)
    private val other = "9c2e".repeat(16)
    private val now = 1_789_000_000L
    private val credentials = FakeCredentials()

    @Before fun setUp() = Dispatchers.setMain(dispatcher)

    @After fun tearDown() = Dispatchers.resetMain()

    private fun payload(code: String = "418250", exp: Long = now + 240) =
        "rm2://pair?v=1&host=192.168.1.42&port=18611&fp=$fp&code=$code&exp=$exp"

    private fun viewModel(client: FakeRm2Client): PairingViewModel {
        val factory = PairingClientFactory { _, _, _ -> client }
        return PairingViewModel(PairingFlow(factory, credentials), "Pixel 8") { now }
    }

    private fun client(
        pingFingerprint: String = "sha256:$fp",
        pairResult: Rm2Result<com.rankmaster2.phone.net.PairedDevice> = Rm2Result.Ok(
            com.rankmaster2.phone.net.PairedDevice(
                deviceId = "d-7f3a",
                deviceName = "Pixel 8",
                token = "tok_live_9aZ",
                issuedAt = "2026-09-15T18:04:11.412Z",
            )
        ),
    ) = FakeRm2Client(
        "https://192.168.1.42:18611/api/v1",
        Rm2Result.Ok(ping(pingFingerprint)),
        pairResult,
    )

    @Test
    fun `a scanned payload pairs and the screen ends up paired`() = runTest(dispatcher) {
        val vm = viewModel(client())

        vm.onPayloadScanned(payload())
        assertTrue("the screen should say it is working", vm.state.value.busy)
        advanceUntilIdle()

        val state = vm.state.value
        assertFalse(state.busy)
        assertNull(state.failure)
        assertEquals("192.168.1.42", state.paired?.host)
        assertNotNull(credentials.current())
    }

    @Test
    fun `an expired payload never reaches the network`() = runTest(dispatcher) {
        val client = client()
        val vm = viewModel(client)

        vm.onPayloadScanned(payload(exp = now - 1))
        advanceUntilIdle()

        assertTrue(client.calls.isEmpty())
        assertEquals("That pairing code has expired", vm.state.value.failure?.title)
    }

    @Test
    fun `a QR code from something else is explained, not swallowed`() = runTest(dispatcher) {
        val vm = viewModel(client())

        vm.onPayloadScanned("WIFI:S:home;T:WPA;P:hunter2;;")
        advanceUntilIdle()

        assertEquals("That is not a pairing code", vm.state.value.failure?.title)
        assertFalse(vm.state.value.failure!!.fatal)
    }

    @Test
    fun `a certificate mismatch is fatal and cannot be dismissed`() = runTest(dispatcher) {
        val client = client(pingFingerprint = "sha256:$other")
        val vm = viewModel(client)

        vm.onPayloadScanned(payload())
        advanceUntilIdle()

        val failure = vm.state.value.failure!!
        assertTrue("a mismatch must be a dead end", failure.fatal)
        assertNull(client.codeSent)

        // The user taps everything available. The screen does not move.
        vm.dismissFailure()
        assertEquals(failure, vm.state.value.failure)
        assertFalse(vm.state.value.scannerActive)
        assertNull(vm.state.value.paired)
        assertNull(credentials.current())
    }

    @Test
    fun `a wrong code can be tried again`() = runTest(dispatcher) {
        val vm = viewModel(
            client(
                pairResult = Rm2Result.Refused(
                    401,
                    ErrorCodes.INVALID_PAIRING_CODE,
                    """no. {"attemptsRemaining":2}""",
                )
            )
        )

        vm.onPayloadScanned(payload())
        advanceUntilIdle()

        val failure = vm.state.value.failure!!
        assertFalse(failure.fatal)
        assertTrue("the attempts left should be on screen", failure.body.contains("2 attempts left"))

        vm.dismissFailure()
        assertNull(vm.state.value.failure)
        assertTrue(vm.state.value.scannerActive)
    }

    @Test
    fun `no pairing window says where to open one`() = runTest(dispatcher) {
        val vm = viewModel(
            client(pairResult = Rm2Result.Refused(403, ErrorCodes.PAIRING_NOT_OPEN, "No window."))
        )

        vm.onPayloadScanned(payload())
        advanceUntilIdle()

        val failure = vm.state.value.failure!!
        assertEquals("No pairing window is open", failure.title)
        assertTrue(failure.body.contains("system tray"))
    }

    @Test
    fun `an unreachable PC is a network story, not a pairing one`() = runTest(dispatcher) {
        val client = FakeRm2Client(
            "https://192.168.1.42:18611/api/v1",
            Rm2Result.Unreachable(IOException("timeout"), "connect timed out"),
            Rm2Result.Unreachable(null, "unused"),
        )
        val vm = viewModel(client)

        vm.onPayloadScanned(payload())
        advanceUntilIdle()

        assertEquals("Cannot reach the PC", vm.state.value.failure?.title)
        assertEquals(listOf("ping"), client.calls)
    }

    @Test
    fun `the manual fallback pairs the same way the camera does`() = runTest(dispatcher) {
        val client = client()
        val vm = viewModel(client)

        vm.showManualEntry()
        assertEquals(PairingMode.Manual, vm.state.value.mode)
        vm.onManualChanged(
            ManualEntry(
                host = "192.168.1.42",
                port = "18611",
                fingerprint = fp.uppercase(),
                code = "418 250",
            )
        )
        vm.onManualSubmit()
        advanceUntilIdle()

        assertEquals("418250", client.codeSent)
        assertNotNull(vm.state.value.paired)
    }

    @Test
    fun `a half-typed manual entry is refused before anything is sent`() = runTest(dispatcher) {
        val client = client()
        val vm = viewModel(client)

        vm.showManualEntry()
        vm.onManualChanged(ManualEntry(host = "192.168.1.42", port = "18611", fingerprint = "3b1f", code = "418250"))
        vm.onManualSubmit()
        advanceUntilIdle()

        assertTrue(client.calls.isEmpty())
        assertNotNull(vm.state.value.failure)
    }

    @Test
    fun `the scanner stops feeding payloads while one is in flight`() = runTest(dispatcher) {
        val client = client()
        val vm = viewModel(client)

        vm.onPayloadScanned(payload())
        vm.onPayloadScanned(payload())
        vm.onPayloadScanned(payload())
        advanceUntilIdle()

        assertEquals(listOf("ping", "pair"), client.calls)
    }
}
