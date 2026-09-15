package com.rankmaster2.phone.rank

import com.rankmaster2.phone.net.Rm2Result
import com.rankmaster2.phone.net.Snapshot
import com.rankmaster2.phone.ui.rank.RankViewModel
import com.rankmaster2.phone.ui.rank.Side
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
 * The ranking screen's logic, and above all the clause that costs the owner data if it is wrong:
 * `SERVER_SPEC.md` § 13.3. One logical vote must reach the server at most once, however badly the
 * network behaves.
 *
 * Most of these tests assert a *negative* — what was not sent, and what was not sent twice —
 * because that is the failure that leaves no trace. A double vote does not crash; it quietly moves
 * a rating that nobody asked to move.
 */
@OptIn(ExperimentalCoroutinesApi::class)
class RankViewModelTest {

    private fun viewModel(
        client: FakeRankClient,
        opened: Snapshot = RankFixtures.ranking(),
        scope: TestScope,
    ) = RankViewModel(
        client = client,
        opened = opened,
        newRequestId = { "req-fixed" },
        scope = TestScope(UnconfinedTestDispatcher(scope.testScheduler)),
    )

    // -- the retry rule ---------------------------------------------------------------------------

    @Test
    fun `a vote that times out is retried with the same token and the same request id`() = runTest {
        val client = FakeRankClient().apply {
            voteResults += Rm2Result.Unreachable(null, "timeout")
            voteResults += Rm2Result.Ok(RankFixtures.ranking(pairSeq = 1, token = "token-2"))
        }
        val vm = viewModel(client, scope = this)

        vm.vote(Side.LEFT)

        assertEquals(2, client.votes.size)
        assertEquals(client.votes[0], client.votes[1])
        assertEquals("token-1", client.votes[0].token)
        assertEquals("req-fixed", client.votes[0].requestId)
    }

    @Test
    fun `a retry that is refused as stale adopts the state it carries and sends nothing more`() = runTest {
        val landed = RankFixtures.ranking(pairSeq = 1, token = "token-2")
        val client = FakeRankClient().apply {
            voteResults += Rm2Result.Unreachable(null, "the answer was lost")
            // The first attempt had in fact applied; the retry meets a token that is no longer current.
            voteResults += Rm2Result.Refused(409, "stale_pair_token", "not the current pair", session = landed)
        }
        val vm = viewModel(client, scope = this)

        vm.vote(Side.LEFT)

        assertEquals(2, client.votes.size)
        assertEquals(landed.pairToken, vm.state.value.snapshot?.pairToken)
        assertEquals(1L, vm.state.value.snapshot?.pairSeq)
        assertNull(vm.state.value.problem)
        assertFalse(vm.state.value.busy)
    }

    @Test
    fun `a vote is never sent a third time`() = runTest {
        val client = FakeRankClient().apply {
            voteResults += Rm2Result.Unreachable(null, "down")
            voteResults += Rm2Result.Unreachable(null, "still down")
        }
        val vm = viewModel(client, scope = this)

        vm.vote(Side.LEFT)

        assertEquals(2, client.votes.size)
        assertTrue(vm.state.value.problem != null)
        assertFalse(vm.state.value.busy)
    }

    @Test
    fun `a certificate mismatch is not retried at all`() = runTest {
        val client = FakeRankClient().apply {
            voteResults += Rm2Result.Unreachable(null, "wrong certificate", pinMismatch = true)
        }
        val vm = viewModel(client, scope = this)

        vm.vote(Side.LEFT)

        assertEquals(1, client.votes.size)
        assertTrue(vm.state.value.problem?.fatal == true)
    }

    @Test
    fun `a second tap while an action is in flight is ignored`() = runTest {
        val client = FakeRankClient().apply { holdVote = true }
        val vm = viewModel(client, scope = this)

        vm.vote(Side.LEFT)
        vm.vote(Side.RIGHT)
        vm.skip()

        assertEquals(1, client.votes.size)
        assertEquals(0, client.skips.size)
        assertTrue(vm.state.value.busy)
    }

    @Test
    fun `a failed action never leaves a way to re-send it with a fresh token`() = runTest {
        val client = FakeRankClient().apply {
            voteResults += Rm2Result.Unreachable(null, "down")
            voteResults += Rm2Result.Unreachable(null, "down")
        }
        val vm = viewModel(client, scope = this)
        vm.vote(Side.LEFT)

        // Dismissing the problem is the only thing the screen offers, and it sends nothing.
        vm.dismissProblem()
        assertEquals(2, client.votes.size)

        // The token has not moved, so a fresh tap is the same logical vote, not a second one.
        assertEquals("token-1", vm.state.value.snapshot?.pairToken)
    }

    // -- what the server says goes ----------------------------------------------------------------

    @Test
    fun `a stale token on the first attempt is adopted, not reported`() = runTest {
        val current = RankFixtures.ranking(pairSeq = 4, token = "token-current")
        val client = FakeRankClient().apply {
            voteResults += Rm2Result.Refused(409, "stale_pair_token", "no", session = current)
        }
        val vm = viewModel(client, scope = this)

        vm.vote(Side.LEFT)

        assertEquals("token-current", vm.state.value.snapshot?.pairToken)
        assertNull(vm.state.value.problem)
    }

    @Test
    fun `a refusal with no snapshot becomes something the owner can read`() = runTest {
        val client = FakeRankClient().apply {
            voteResults += Rm2Result.Refused(404, "no_session", "No session is open.")
        }
        val vm = viewModel(client, scope = this)

        vm.vote(Side.LEFT)

        val problem = vm.state.value.problem
        assertTrue(problem != null && problem.fatal)
        assertTrue(problem!!.title.contains("no longer open"))
    }

    @Test
    fun `cancel sends no token and says what it took back`() = runTest {
        val cancelled = RankFixtures.ranking(
            pairSeq = 2,
            token = "token-3",
            lastAction = RankFixtures.undoOf("vote"),
        )
        val client = FakeRankClient().apply { cancelResults += Rm2Result.Ok(cancelled) }
        val vm = viewModel(client, scope = this)

        vm.cancel()

        assertEquals(1, client.cancels.size)
        assertEquals("Vote taken back", vm.state.value.notice)
    }

    @Test
    fun `a cancelled discard names the file it came back as`() = runTest {
        val cancelled = RankFixtures.ranking(
            lastAction = RankFixtures.undoOf("discard", id = "a.jpg", restoredId = "a (2).jpg"),
        )
        val client = FakeRankClient().apply { cancelResults += Rm2Result.Ok(cancelled) }
        val vm = viewModel(client, scope = this)

        vm.cancel()

        assertEquals("Discard taken back as a (2).jpg", vm.state.value.notice)
    }

    @Test
    fun `the notch is offered only when the server says there is something to take back`() = runTest {
        val client = FakeRankClient()
        val vm = viewModel(client, opened = RankFixtures.ranking(undoAvailable = false), scope = this)
        assertFalse(vm.state.value.canCancel)

        val after = RankFixtures.ranking(undoAvailable = true)
        client.voteResults += Rm2Result.Ok(after)
        vm.vote(Side.LEFT)

        assertTrue(vm.state.value.canCancel)
    }

    @Test
    fun `an exhausted folder offers no actions`() = runTest {
        val client = FakeRankClient()
        val vm = viewModel(client, opened = RankFixtures.exhausted(), scope = this)

        vm.vote(Side.LEFT)
        vm.skip()

        assertEquals(0, client.votes.size)
        assertEquals(0, client.skips.size)
        assertTrue(vm.state.value.exhausted)
        assertFalse(vm.state.value.actionable)
    }

    @Test
    fun `save reports itself without pretending an action happened`() = runTest {
        val client = FakeRankClient().apply { saveResults += Rm2Result.Ok(RankFixtures.ranking()) }
        val vm = viewModel(client, scope = this)

        vm.save()

        assertEquals("Saved", vm.state.value.notice)
    }

    @Test
    fun `coming back to the front re-reads the state rather than assuming it`() = runTest {
        val moved = RankFixtures.ranking(pairSeq = 9, token = "token-9")
        val client = FakeRankClient().apply { sessionResults += Rm2Result.Ok(moved) }
        val vm = viewModel(client, scope = this)

        vm.onForeground(false)
        assertFalse(vm.state.value.foreground)

        vm.onForeground(true)
        assertEquals(1, client.sessionReads)
        assertEquals("token-9", vm.state.value.snapshot?.pairToken)
    }

    // -- leaving, and the folder the PC was left holding ------------------------------------------

    @Test
    fun `leaving closes the session on the PC`() = runTest {
        val client = FakeRankClient()
        val vm = viewModel(client, scope = this)
        var left = false

        vm.leave { left = true }

        assertTrue("the session must be closed before the screen goes", client.sessionClosed)
        assertTrue(left)
    }

    @Test
    fun `opening the viewer stops the panes underneath`() = runTest {
        val vm = viewModel(FakeRankClient(), scope = this)
        assertTrue(vm.state.value.panesPlaying)

        vm.view(Side.LEFT)
        assertFalse(
            "two players on one video is two decoders on one file",
            vm.state.value.panesPlaying,
        )

        vm.view(null)
        assertTrue(vm.state.value.panesPlaying)
    }

    @Test
    fun `backgrounding stops the panes whatever else is open`() = runTest {
        val vm = viewModel(FakeRankClient(), scope = this)

        vm.onForeground(false)
        assertFalse(vm.state.value.panesPlaying)
    }

    // -- coming back to a folder the PC never let go of -------------------------------------------

    @Test
    fun `re-opening a session that resumed adopts the snapshot it answered with`() = runTest {
        // SERVER_SPEC.md § 10.1: opening the folder that is already open is a pure read and
        // returns the *same* sessionId. These view models are keyed by that id and outlive the
        // screen, so the one handed back is the one from before, still holding the old pair.
        val vm = viewModel(FakeRankClient(), opened = RankFixtures.ranking(pairSeq = 3), scope = this)

        val live = RankFixtures.ranking(pairSeq = 9, token = "token-live")
        vm.resume(live)

        assertEquals("token-live", vm.state.value.snapshot?.pairToken)
        assertEquals(9L, vm.state.value.snapshot?.pairSeq)
    }

    @Test
    fun `resuming sends nothing - it is a read of what the open already answered`() = runTest {
        val client = FakeRankClient()
        val vm = viewModel(client, scope = this)

        vm.resume(RankFixtures.ranking(pairSeq = 7, token = "token-live"))

        assertEquals(0, client.votes.size)
        assertEquals(0, client.sessionReads)
    }

    @Test
    fun `resuming does not drag the screen back to an older pair`() = runTest {
        // The screen has been voting since it opened; the snapshot it was *opened* with is by now
        // several pairs stale. Recomposition must not undo that.
        val client = FakeRankClient().apply {
            voteResults += Rm2Result.Ok(RankFixtures.ranking(pairSeq = 5, token = "token-5"))
        }
        val opened = RankFixtures.ranking(pairSeq = 0)
        val vm = viewModel(client, opened = opened, scope = this)
        vm.vote(Side.LEFT)

        vm.resume(opened)

        assertEquals("token-5", vm.state.value.snapshot?.pairToken)
    }

    @Test
    fun `resuming clears a busy flag left behind by the last visit`() = runTest {
        val client = FakeRankClient().apply { holdVote = true }
        val vm = viewModel(client, scope = this)
        vm.vote(Side.LEFT)
        assertTrue(vm.state.value.busy)

        vm.resume(RankFixtures.ranking(pairSeq = 4, token = "token-4"))

        assertFalse("a screen that comes back deaf is a screen that looks broken", vm.state.value.busy)
    }
}
