package com.rankmaster2.phone.browse

import com.rankmaster2.phone.net.Rm2Result
import com.rankmaster2.phone.ui.browse.BrowseViewModel
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.StandardTestDispatcher
import kotlinx.coroutines.test.advanceUntilIdle
import kotlinx.coroutines.test.resetMain
import kotlinx.coroutines.test.runTest
import kotlinx.coroutines.test.setMain
import org.junit.After
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

/**
 * The one failure the owner cannot try his way out of, and which the app used to leave him stuck in.
 *
 * If the PC's certificate is regenerated, or this phone's token is revoked, every call fails
 * permanently. The old build's answer was a message telling him to pair again - on a screen with no
 * route to pairing, because the pairing screen only appears when no credentials are stored. The app
 * was, at that point, only uninstallable.
 *
 * So these two causes are a *state* rather than one more error string, and the screen they produce
 * offers exactly one thing: forget this PC and pair again.
 */
@OptIn(ExperimentalCoroutinesApi::class)
class PairingLostTest {

    private val dispatcher = StandardTestDispatcher()
    private val client = FakeRm2Client()

    @Before fun setUp() = Dispatchers.setMain(dispatcher)
    @After fun tearDown() = Dispatchers.resetMain()

    private fun vm() = BrowseViewModel(client)

    @Test
    fun `a certificate this phone never pinned is the end of the pairing, not a network blip`() =
        runTest(dispatcher) {
            client.rootsResult = Rm2Result.Unreachable(null, "handshake failed", pinMismatch = true)

            val vm = vm()
            advanceUntilIdle()

            assertTrue(vm.state.value.pairingLost)
        }

    @Test
    fun `a revoked token is the same conclusion by a different route`() = runTest(dispatcher) {
        client.rootsResult =
            Rm2Result.Refused(401, "token_revoked", "This device's token has been revoked.")

        val vm = vm()
        advanceUntilIdle()

        assertTrue(vm.state.value.pairingLost)
    }

    @Test
    fun `an unauthenticated or invalid token counts too`() = runTest(dispatcher) {
        listOf("invalid_token", "unauthenticated").forEach { code ->
            val fake = FakeRm2Client().apply {
                rootsResult = Rm2Result.Refused(401, code, "no")
            }
            val vm = BrowseViewModel(fake)
            advanceUntilIdle()
            assertTrue("$code should end the pairing", vm.state.value.pairingLost)
        }
    }

    @Test
    fun `an ordinary unreachable PC is not the end of anything`() = runTest(dispatcher) {
        // The PC asleep, the phone on mobile data, the server not started. All recoverable, and
        // telling him to re-pair over any of them would be telling him to throw away a good token.
        client.rootsResult = Rm2Result.Unreachable(null, "connect timed out", pinMismatch = false)

        val vm = vm()
        advanceUntilIdle()

        assertFalse(vm.state.value.pairingLost)
        assertTrue(vm.state.value.listFailure != null)
    }

    @Test
    fun `an ordinary refusal about a folder is not the end of anything either`() =
        runTest(dispatcher) {
            client.rootsResult = Rm2Result.Refused(500, "internal_error", "something broke")

            val vm = vm()
            advanceUntilIdle()

            assertFalse(vm.state.value.pairingLost)
        }

    @Test
    fun `once lost it stays lost, even if a later call happens to answer`() = runTest(dispatcher) {
        client.rootsResult = Rm2Result.Unreachable(null, "handshake failed", pinMismatch = true)
        val vm = vm()
        advanceUntilIdle()
        assertTrue(vm.state.value.pairingLost)

        // Nothing short of pairing again clears this. A PC that answers one call and refuses the
        // next is exactly the shape of the problem, not evidence that it went away.
        client.rootsResult = Rm2Result.Ok(com.rankmaster2.phone.net.Roots(emptyList()))
        vm.refresh()
        advanceUntilIdle()

        assertTrue(vm.state.value.pairingLost)
    }

    @Test
    fun `the slow counting pass can be the one that finds out`() = runTest(dispatcher) {
        // Names first, counts second - so the counting pass can be the call that meets a token
        // that has just stopped being accepted. Reporting that as "could not count the files in
        // these folders" leaves the browser looking merely slow while nothing will work again.
        client.rootsResult = Rm2Result.Ok(
            com.rankmaster2.phone.net.Roots(
                listOf(com.rankmaster2.phone.net.Root("D:\\", "Photos (D:)", "fixed", true, null, null)),
            ),
        )
        client.listings["D:\\" to false] = listing("D:\\", null, uncounted("Iceland", "D:\\Iceland"))
        client.listings["D:\\" to true] =
            Rm2Result.Refused(401, "token_revoked", "This device's token has been revoked.")

        val vm = vm()
        advanceUntilIdle()
        vm.enter("D:\\")
        advanceUntilIdle()

        assertTrue(vm.state.value.pairingLost)
        // And the names it already drew are still there, because nothing about them was wrong.
        assertTrue(vm.state.value.rows.isNotEmpty())
    }
}
