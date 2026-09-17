package com.rankmaster2.phone.rank

import com.rankmaster2.phone.net.Browse
import com.rankmaster2.phone.net.PairedDevice
import com.rankmaster2.phone.net.Ping
import com.rankmaster2.phone.net.Rm2Client
import com.rankmaster2.phone.net.Rm2Result
import com.rankmaster2.phone.net.Roots
import com.rankmaster2.phone.net.Snapshot
import com.rankmaster2.phone.ui.rank.RankViewModel
import com.rankmaster2.phone.ui.rank.Side
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.UnconfinedTestDispatcher
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * AUDIT throwaway tests. They document what the view model actually does on the server's real
 * error shapes; several assert behaviour the auditor believes is a defect. Not production tests.
 */
@OptIn(ExperimentalCoroutinesApi::class)
class AuditRankViewModelTest {

    private fun viewModel(client: Rm2Client, opened: Snapshot = RankFixtures.ranking(), scope: TestScope) =
        RankViewModel(
            client = client,
            opened = opened,
            newRequestId = { "req-fixed" },
            scope = TestScope(UnconfinedTestDispatcher(scope.testScheduler)),
        )

    // The server attaches `error.session` to every 409/500 on a /session endpoint while a session
    // is open (SessionRegistry.Materialise(open)). RankViewModel.finish() treats ANY refusal that
    // carries a snapshot as a silent resync. So the owner never sees these.

    @Test
    fun `AUDIT vote save_failed with the server's real shape is swallowed silently`() = runTest {
        val same = RankFixtures.ranking() // pairSeq 0, token-1: the vote was rolled back
        val client = FakeRankClient().apply {
            voteResults += Rm2Result.Refused(500, "save_failed", "The ranking file could not be written; the vote was rolled back.", session = same)
        }
        val vm = viewModel(client, scope = this)

        vm.vote(Side.LEFT)

        // Defect: the drive is unplugged / folder read-only, the vote did not count, and the phone
        // says nothing at all. problemFor(SAVE_FAILED) is unreachable on the real wire shape.
        assertNull(vm.state.value.problem)
        assertNull(vm.state.value.notice)
        assertFalse(vm.state.value.busy)
        assertEquals("token-1", vm.state.value.snapshot?.pairToken)
    }

    @Test
    fun `AUDIT discard move_failed with the server's real shape is swallowed silently`() = runTest {
        val same = RankFixtures.ranking()
        val client = FakeRankClient().apply {
            moveResults += Rm2Result.Refused(500, "move_failed", "The file could not be moved.", session = same)
        }
        val vm = viewModel(client, scope = this)

        vm.discard(Side.LEFT)

        assertNull(vm.state.value.problem)
        assertNull(vm.state.value.notice)
    }

    @Test
    fun `AUDIT rename_in_progress is swallowed silently so every tap looks dead`() = runTest {
        val same = RankFixtures.ranking()
        val client = FakeRankClient().apply {
            voteResults += Rm2Result.Refused(409, "rename_in_progress", "A rename is already running.", session = same)
        }
        val vm = viewModel(client, scope = this)

        vm.vote(Side.LEFT)

        assertNull(vm.state.value.problem)
        assertNull(vm.state.value.notice)
    }

    @Test
    fun `AUDIT nothing_to_undo with the server's real shape is swallowed silently`() = runTest {
        val same = RankFixtures.ranking(undoAvailable = false)
        val client = FakeRankClient().apply {
            cancelResults += Rm2Result.Refused(409, "nothing_to_undo", "There is nothing to undo.", session = same)
        }
        val vm = viewModel(client, scope = this)

        vm.cancel()

        assertNull(vm.state.value.problem)
    }

    // resume() keeps `busy` when the held snapshot is at least as new as the one just opened.

    @Test
    fun `AUDIT resume keeps the screen deaf when the opened pairSeq equals the held one`() = runTest {
        val client = FakeRankClient().apply { holdVote = true }
        val vm = viewModel(client, scope = this)
        vm.vote(Side.LEFT)
        assertTrue(vm.state.value.busy)

        // The vote never landed; the DELETE on leave was lost; the same folder is re-opened and the
        // server answers with the same pairSeq (0) under the same sessionId.
        vm.resume(RankFixtures.ranking(pairSeq = 0, token = "token-1"))

        // Stays busy until the in-flight coroutine finishes (up to 2 x OkHttp timeouts).
        assertTrue(vm.state.value.busy)
    }

    // refresh() is not part of the busy gate: a session read that is in flight when the owner taps
    // can land after the vote was sent, reopen the gate with an older snapshot, and let a second
    // request out. The server's token check is what stops that being a double vote.

    @Test
    fun `AUDIT a slow foreground refresh reopens the gate while a vote is in flight`() = runTest {
        val client = HoldingClient()
        val vm = viewModel(client, scope = this)

        vm.onForeground(true)              // GET /session in flight (held)
        vm.vote(Side.LEFT)                 // vote in flight (held)
        assertTrue(vm.state.value.busy)

        client.session.complete(Rm2Result.Ok(RankFixtures.ranking(pairSeq = 0, token = "token-1")))
        assertFalse("the read landed and flipped busy off with the vote still unanswered", vm.state.value.busy)

        vm.vote(Side.RIGHT)                // a second tap now gets through
        assertEquals(2, client.votes.size)
        assertEquals("token-1", client.votes[1].token) // same token, so the server will refuse it as stale
    }

    private class HoldingClient : Rm2Client {
        override val baseUrl = "https://127.0.0.1:18611/api/v1"
        val votes = mutableListOf<SentAction>()
        val session = CompletableDeferred<Rm2Result<Snapshot>>()
        private val forever = CompletableDeferred<Rm2Result<Snapshot>>()

        override suspend fun vote(pairToken: String, winner: String, clientRequestId: String): Rm2Result<Snapshot> {
            votes += SentAction(pairToken, clientRequestId, winner)
            return forever.await()
        }
        override suspend fun session(): Rm2Result<Snapshot> = session.await()

        override suspend fun skip(pairToken: String, clientRequestId: String) = error("n/a")
        override suspend fun discard(pairToken: String, side: String, clientRequestId: String) = error("n/a")
        override suspend fun special(pairToken: String, side: String, clientRequestId: String) = error("n/a")
        override suspend fun cancel(clientRequestId: String) = error("n/a")
        override suspend fun save() = error("n/a")
        override suspend fun ping(): Rm2Result<Ping> = error("n/a")
        override suspend fun pair(code: String, deviceName: String): Rm2Result<PairedDevice> = error("n/a")
        override suspend fun revoke(deviceId: String): Rm2Result<Unit> = error("n/a")
        override suspend fun roots(): Rm2Result<Roots> = error("n/a")
        override suspend fun browse(path: String, counts: Boolean): Rm2Result<Browse> = error("n/a")
        override suspend fun openSession(folder: String): Rm2Result<Snapshot> = error("n/a")
        override suspend fun closeSession(): Rm2Result<Unit> = Rm2Result.Ok(Unit)
        override fun url(link: String): String = link
    }
}
