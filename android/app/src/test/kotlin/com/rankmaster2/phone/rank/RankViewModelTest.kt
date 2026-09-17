package com.rankmaster2.phone.rank

import com.rankmaster2.phone.net.Browse
import com.rankmaster2.phone.net.LastAction
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
import kotlinx.coroutines.test.advanceUntilIdle
import kotlinx.coroutines.test.runTest
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
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
        client: Rm2Client,
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

    // -- § 1.3: a gesture is aimed at the pair it started on, not whatever is current when it lands -
    //
    // `RankFixtures.ranking()` now gives every distinct (pairSeq, token) its own file names, so these
    // tests can actually tell a misdirected action apart from a correctly-targeted one - before the
    // second audit's fix to the fixtures, every pair in every test was named `alpha.jpg`/`bravo.jpg`
    // regardless of which pair it was, so no test here could have caught this even with the bug
    // present. This is the test the audit's § 1.3 (and § 5) asked for.

    @Test
    fun `a vote whose pair moved on between the finger and the send is dropped, not sent`() = runTest {
        val opened = RankFixtures.ranking(pairSeq = 0, token = "token-1")
        val client = FakeRankClient()
        val vm = viewModel(client, opened = opened, scope = this)
        val whatHeSaw = opened.pair?.left?.id

        // A refresh() lands between his finger going down and the tap being processed (§ 1.3: this
        // is deliberately not gated by `busy` - see RankViewModel.refresh's A10 note) and the pair
        // moves on to a different picture entirely.
        val moved = RankFixtures.ranking(pairSeq = 1, token = "token-2")
        client.sessionResults += Rm2Result.Ok(moved)
        vm.refresh()
        assertNotEquals("the fixture must actually be a different picture now", whatHeSaw, vm.state.value.pair?.left?.id)

        // The tap still carries the token of the pair his finger actually landed on.
        vm.vote(Side.LEFT, aimedAtToken = opened.pairToken)

        assertEquals("a vote for a picture no longer on screen must never reach the PC", 0, client.votes.size)
        assertEquals("token-2", vm.state.value.snapshot?.pairToken)
        assertFalse(vm.state.value.busy)
    }

    @Test
    fun `a vote whose token still matches what is current is sent exactly as before`() = runTest {
        val opened = RankFixtures.ranking()
        val client = FakeRankClient().apply {
            voteResults += Rm2Result.Ok(RankFixtures.ranking(pairSeq = 1, token = "token-2"))
        }
        val vm = viewModel(client, opened = opened, scope = this)

        vm.vote(Side.LEFT, aimedAtToken = opened.pairToken)

        assertEquals(1, client.votes.size)
        assertEquals("token-2", vm.state.value.snapshot?.pairToken)
    }

    @Test
    fun `the pane menu is opened for one photograph, and Discard on it after a resync moves nothing`() = runTest {
        val opened = RankFixtures.ranking(pairSeq = 0, token = "token-1")
        val client = FakeRankClient()
        val vm = viewModel(client, opened = opened, scope = this)

        // He long-presses the left photograph and the menu opens on it.
        vm.openPaneMenu(Side.LEFT, opened.pairToken)
        val heWasLookingAt = opened.pair?.left?.id
        assertEquals(opened.pairToken, vm.state.value.paneMenuToken)

        // He reads the menu. While he does, the app comes back to the front and resyncs onto a
        // different pair entirely - the scenario the audit proved end to end with PROBE1/finish().
        val moved = RankFixtures.ranking(pairSeq = 1, token = "token-2")
        client.sessionResults += Rm2Result.Ok(moved)
        vm.refresh()
        assertNotEquals(heWasLookingAt, vm.state.value.pair?.left?.id)

        // He taps Discard, believing it is still about the photograph he read the menu for.
        // RankScreen would pass RankState.paneMenuToken here - the token from when the menu opened.
        vm.discard(Side.LEFT, aimedAtToken = opened.pairToken)

        assertEquals(
            "the photograph he read the menu for must not be moved off disk for a pair he never saw",
            0,
            client.discards.size,
        )
        assertNull("a dropped gesture closes the menu rather than leaving it open on a stale pair", vm.state.value.paneMenu)
        assertNull(vm.state.value.paneMenuToken)
    }

    @Test
    fun `opening the pane menu records the pair it was aimed at, and closing it forgets`() = runTest {
        val vm = viewModel(FakeRankClient(), scope = this)

        vm.openPaneMenu(Side.RIGHT, "token-1")
        assertEquals("token-1", vm.state.value.paneMenuToken)
        assertEquals(Side.RIGHT, vm.state.value.paneMenu)

        vm.closePaneMenu()
        assertNull(vm.state.value.paneMenuToken)
        assertNull(vm.state.value.paneMenu)
    }

    @Test
    fun `a discard whose token still matches what is current reaches the PC exactly as before`() = runTest {
        val opened = RankFixtures.ranking()
        val client = FakeRankClient().apply {
            moveResults += Rm2Result.Ok(RankFixtures.ranking(pairSeq = 1, token = "token-2"))
        }
        val vm = viewModel(client, opened = opened, scope = this)

        vm.openPaneMenu(Side.LEFT, opened.pairToken)
        vm.discard(Side.LEFT, aimedAtToken = vm.state.value.paneMenuToken)

        assertEquals(1, client.discards.size)
        assertEquals("token-1", client.discards[0].token)
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
    fun `an unrecognised refusal shows the server's own message, never the bare code`() = runTest {
        // K5: the old fallback read "Error code: folder_not_rankable" - a code is not a sentence a
        // person reads. The server's own message is written for one; use only that.
        val client = FakeRankClient().apply {
            voteResults += Rm2Result.Refused(400, "folder_not_rankable", "This folder has fewer than two rankable files.")
        }
        val vm = viewModel(client, scope = this)

        vm.vote(Side.LEFT)

        val problem = vm.state.value.problem
        assertEquals("This folder has fewer than two rankable files.", problem?.body)
        assertFalse("must never name the bare code", problem?.body.orEmpty().contains("folder_not_rankable"))
    }

    // -- H6: shown-versus-silent is decided by the code, never by whether a snapshot came with it --
    // These four used to build a `Refused` without a session, a shape the real server never sends
    // (it attaches one to almost every refusal while a session is open). With the real shape, the
    // view model used to treat *any* attached snapshot as a silent resync; these are the codes
    // where that hid something the owner could have acted on. Flipped: each now proves the refusal
    // is shown, and the pair the server sent along with it is still adopted underneath it.

    @Test
    fun `a save_failed refusal is shown, and its text fits the durability latch`() = runTest {
        val same = RankFixtures.ranking() // pairSeq 0, token-1: the vote was rolled back
        val client = FakeRankClient().apply {
            voteResults += Rm2Result.Refused(
                500, "save_failed",
                "The ranking file could not be written; the vote was rolled back.",
                session = same,
            )
        }
        val vm = viewModel(client, scope = this)

        vm.vote(Side.LEFT)

        val problem = vm.state.value.problem
        assertEquals("The PC could not write the ranking file", problem?.title)
        assertTrue(problem!!.body.contains("did not count"))
        assertFalse(vm.state.value.busy)
        assertEquals("token-1", vm.state.value.snapshot?.pairToken)
    }

    @Test
    fun `a move_failed refusal is shown`() = runTest {
        val same = RankFixtures.ranking()
        val client = FakeRankClient().apply {
            moveResults += Rm2Result.Refused(500, "move_failed", "The file could not be moved.", session = same)
        }
        val vm = viewModel(client, scope = this)

        vm.discard(Side.LEFT)

        assertEquals("The file could not be moved", vm.state.value.problem?.title)
    }

    @Test
    fun `a rename_in_progress refusal is shown, so a tap never looks dead`() = runTest {
        val same = RankFixtures.ranking()
        val client = FakeRankClient().apply {
            voteResults += Rm2Result.Refused(409, "rename_in_progress", "A rename is already running.", session = same)
        }
        val vm = viewModel(client, scope = this)

        vm.vote(Side.LEFT)

        assertEquals("The PC is renaming this folder", vm.state.value.problem?.title)
    }

    @Test
    fun `a nothing_to_undo refusal is shown`() = runTest {
        val same = RankFixtures.ranking(undoAvailable = false)
        val client = FakeRankClient().apply {
            cancelResults += Rm2Result.Refused(409, "nothing_to_undo", "There is nothing to undo.", session = same)
        }
        val vm = viewModel(client, scope = this)

        vm.cancel()

        assertEquals("Nothing to take back", vm.state.value.problem?.title)
    }

    // -- § 2.6: a silent code with no readable snapshot must not go dead --------------------------

    @Test
    fun `a silent refusal with no readable snapshot refreshes instead of going dead`() = runTest {
        // The server always attaches a snapshot to stale_pair_token / no_current_pair - the
        // correction *is* the snapshot. If it could not be decoded (a schema drift between phone
        // and server), staying silent as these codes normally deserve would mean nothing on screen
        // ever changes and every further tap meets the same stale token, forever - the screen the
        // audit found completely dead. A plain read is the way out.
        val client = FakeRankClient().apply {
            voteResults += Rm2Result.Refused(409, "stale_pair_token", "not current", session = null)
            sessionResults += Rm2Result.Ok(RankFixtures.ranking(pairSeq = 3, token = "token-3"))
        }
        val vm = viewModel(client, scope = this)

        vm.vote(Side.LEFT)

        assertEquals("the unreadable refusal must trigger a plain read", 1, client.sessionReads)
        assertEquals("token-3", vm.state.value.snapshot?.pairToken)
        assertNull(vm.state.value.problem)
        assertFalse(vm.state.value.busy)
    }

    @Test
    fun `a silent refusal that does carry a readable snapshot needs no extra read`() = runTest {
        // Regression guard for the fix above: the common case - the server's snapshot decodes fine
        // - must not start reading the session an extra time on every stale token.
        val landed = RankFixtures.ranking(pairSeq = 1, token = "token-2")
        val client = FakeRankClient().apply {
            voteResults += Rm2Result.Refused(409, "stale_pair_token", "not current", session = landed)
        }
        val vm = viewModel(client, scope = this)

        vm.vote(Side.LEFT)

        assertEquals(0, client.sessionReads)
        assertEquals("token-2", vm.state.value.snapshot?.pairToken)
    }

    // -- A15: 503 session_busy is retried once, transparently, before it ever reaches the owner ----

    @Test
    fun `a session_busy refusal waits the server's own delay and resends the same vote once`() = runTest {
        val busy = Rm2Result.Refused(
            503, "session_busy", "The PC is busy.",
            details = buildJsonObject { put("retryAfterSeconds", 1) },
        )
        val landed = RankFixtures.ranking(pairSeq = 1, token = "token-2")
        val client = FakeRankClient().apply {
            voteResults += busy
            voteResults += Rm2Result.Ok(landed)
        }
        val vm = viewModel(client, scope = this)

        vm.vote(Side.LEFT)
        advanceUntilIdle() // let the virtual clock reach the server's retryAfterSeconds

        assertEquals(2, client.votes.size)
        assertEquals(client.votes[0], client.votes[1]) // same token, same request id
        assertEquals("token-2", vm.state.value.snapshot?.pairToken)
        assertNull(vm.state.value.problem)
    }

    @Test
    fun `a session_busy refusal that persists past the one retry shows the modal`() = runTest {
        val busy = Rm2Result.Refused(
            503, "session_busy", "The PC is busy.",
            details = buildJsonObject { put("retryAfterSeconds", 1) },
        )
        val client = FakeRankClient().apply {
            voteResults += busy
            voteResults += Rm2Result.Refused(503, "session_busy", "The PC is busy.")
        }
        val vm = viewModel(client, scope = this)

        vm.vote(Side.LEFT)
        advanceUntilIdle()

        assertEquals(2, client.votes.size)
        assertEquals("The PC is busy", vm.state.value.problem?.title)
        assertFalse(vm.state.value.busy)
    }

    @Test
    fun `a huge retryAfterSeconds from the server cannot freeze the screen for as long as it names`() = runTest {
        // § 2.7: retryAfterSeconds is a number off the wire with no ceiling on this side. The
        // server sends 1 today, but nothing stopped it (a bug, or something on the LAN answering
        // that is not the PC he thinks it is) from naming a day and freezing `busy` for one.
        val busy = Rm2Result.Refused(
            503, "session_busy", "The PC is busy.",
            details = buildJsonObject { put("retryAfterSeconds", 86_400) },
        )
        val landed = RankFixtures.ranking(pairSeq = 1, token = "token-2")
        val client = FakeRankClient().apply {
            voteResults += busy
            voteResults += Rm2Result.Ok(landed)
        }
        val vm = viewModel(client, scope = this)

        val startedAt = testScheduler.currentTime
        vm.vote(Side.LEFT)
        advanceUntilIdle()
        val waitedMs = testScheduler.currentTime - startedAt

        assertEquals(2, client.votes.size)
        assertTrue(
            "waited ${waitedMs}ms - a wait this long must be capped, not obey the server's own number",
            waitedMs <= RankViewModel.MAX_RETRY_AFTER_SECONDS * 1000L,
        )
    }

    // -- A16: a cancel that is still lost after its retry cannot self-heal the way a token can - it
    // says so honestly, rather than repeating "tap the same picture again" advice that fits a vote
    // and not a cancel, and folds into a refresh so the screen catches up to whatever is true now --

    @Test
    fun `a cancel lost twice says the cancel may not have landed, then refreshes`() = runTest {
        val client = FakeRankClient().apply {
            cancelResults += Rm2Result.Unreachable(null, "timeout")
            cancelResults += Rm2Result.Unreachable(null, "still down")
            sessionResults += Rm2Result.Ok(RankFixtures.ranking(pairSeq = 2, token = "token-2"))
        }
        val vm = viewModel(client, scope = this)

        vm.cancel()

        assertEquals(2, client.cancels.size)
        assertEquals(1, client.sessionReads)
        // The refresh landed and is what the screen now shows - not a guess about the cancel.
        assertEquals("token-2", vm.state.value.snapshot?.pairToken)
        assertFalse(vm.state.value.busy)
    }

    // -- K5: the notice after a special move names the file, the way a discard's notice already did

    @Test
    fun `a special move names the file it moved, matching what a discard says`() = runTest {
        val moved = RankFixtures.ranking(
            lastAction = LastAction(
                seq = 2,
                type = "special",
                clientRequestId = "req-fixed",
                side = "left",
                id = "alpha.jpg",
                at = "2026-09-15T10:02:00.000Z",
            ),
        )
        val client = FakeRankClient().apply { moveResults += Rm2Result.Ok(moved) }
        val vm = viewModel(client, scope = this)

        vm.special(Side.LEFT)

        assertEquals("Moved alpha.jpg to special", vm.state.value.notice)
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
        // T2e: leave() navigates first, by design (RankViewModel's own doc comment says so) - the
        // close is housekeeping that happens after. This asserts the close happens at all, not that
        // it happens before the screen goes, which the code never promised.
        val client = FakeRankClient()
        val vm = viewModel(client, scope = this)
        var left = false

        vm.leave { left = true }

        assertTrue("the session is closed", client.sessionClosed)
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

    @Test
    fun `resuming clears busy even when the opened pairSeq equals the held one (A11)`() = runTest {
        // The early-return branch of resume() - held and opened already agree, so there is nothing
        // to adopt - used to return before touching `busy`, leaving a screen whose in-flight action
        // will never be answered (the DELETE on leave was lost, the same folder was reopened, the
        // server answers with the same pairSeq under the same sessionId) permanently deaf.
        val client = FakeRankClient().apply { holdVote = true }
        val vm = viewModel(client, scope = this)
        vm.vote(Side.LEFT)
        assertTrue(vm.state.value.busy)

        vm.resume(RankFixtures.ranking(pairSeq = 0, token = "token-1"))

        assertFalse("resume must clear busy on every branch, not only the ones that adopt a new pair", vm.state.value.busy)
    }

    // -- A10: a plain read must never reopen the gate underneath an action it raced with -----------

    @Test
    fun `a slow foreground refresh does not reopen the gate while a vote is in flight`() = runTest {
        val client = HoldingClient()
        val vm = viewModel(client, scope = this)

        vm.onForeground(true)              // GET /session in flight (held)
        vm.vote(Side.LEFT)                 // vote in flight (held)
        assertTrue(vm.state.value.busy)
        assertEquals(1, client.votes.size)

        client.session.complete(Rm2Result.Ok(RankFixtures.ranking(pairSeq = 0, token = "token-1")))

        // The read landed after the vote started - its answer is discarded rather than reopening
        // the gate underneath an action that has not been answered yet.
        assertTrue("a slow read must not reopen the gate while an action is in flight", vm.state.value.busy)

        vm.vote(Side.RIGHT) // still gated: the first vote has not resolved
        assertEquals(1, client.votes.size)
    }

    @Test
    fun `a foreground refresh that lands behind the held pair is discarded, not adopted`() = runTest {
        // Two refreshes can land out of order. The second to start is not necessarily the second to
        // answer, and an older pair generation must never drag a newer screen backwards.
        val client = FakeRankClient().apply {
            sessionResults += Rm2Result.Ok(RankFixtures.ranking(pairSeq = 1, token = "token-older"))
        }
        val vm = viewModel(client, opened = RankFixtures.ranking(pairSeq = 5, token = "token-5"), scope = this)

        vm.refresh()

        assertEquals("token-5", vm.state.value.snapshot?.pairToken)
        assertFalse(vm.state.value.busy)
    }

    /** A `Rm2Client` whose `vote` and `session` calls are held open until the test completes them. */
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

    // -- § 3.4: an action's own late answer must not drag a newer screen backwards -----------------

    @Test
    fun `an action's late answer for an older pair does not drag a newer screen backwards`() = runTest {
        // resume() clears `busy` the moment the owner comes back to a folder his view model was
        // already holding one for (A11) - it cannot tell a truly-lost action from one still
        // genuinely in flight, and chooses never to leave the screen deaf. So the first vote's
        // coroutine keeps running after he backs out, resumes, and casts a second vote that lands
        // first and moves the screen on. When the first vote's own answer finally arrives, it must
        // not drag the pair he is looking at back to the one it was actually about.
        val client = TwoVotesClient()
        val opened = RankFixtures.ranking(pairSeq = 0, token = "token-1")
        val vm = viewModel(client, opened = opened, scope = this)

        vm.vote(Side.LEFT) // the first vote - hangs, as if the PC were slow to answer
        assertTrue(vm.state.value.busy)

        // He backs out and re-opens the same folder; the server has not processed the first vote
        // yet, so the open answers with the same pair he left on - the early-return branch of
        // resume() (A11), which clears `busy` regardless.
        vm.resume(opened)
        assertFalse(vm.state.value.busy)

        // A second, later vote - on the pair actually on screen now - lands and moves things on.
        client.immediate += Rm2Result.Ok(RankFixtures.ranking(pairSeq = 2, token = "token-2"))
        vm.vote(Side.RIGHT)
        assertEquals("token-2", vm.state.value.snapshot?.pairToken)

        // The first vote's answer finally arrives: an older pair generation than what is on screen.
        client.firstCall.complete(Rm2Result.Ok(RankFixtures.ranking(pairSeq = 1, token = "token-old")))

        assertEquals(
            "a late answer for an older pair must not drag the screen back past what is already shown",
            "token-2",
            vm.state.value.snapshot?.pairToken,
        )
        assertFalse(vm.state.value.busy)
    }

    /**
     * A client whose first `vote()` call hangs until the test completes it, and whose later calls
     * answer immediately from a queue - for reproducing two actions genuinely in flight at once
     * (§ 3.4), which [HoldingClient] above cannot (every call it makes hangs forever).
     */
    private class TwoVotesClient : Rm2Client {
        override val baseUrl = "https://127.0.0.1:18611/api/v1"
        val firstCall = CompletableDeferred<Rm2Result<Snapshot>>()
        val immediate = ArrayDeque<Rm2Result<Snapshot>>()
        private var calls = 0

        override suspend fun vote(pairToken: String, winner: String, clientRequestId: String): Rm2Result<Snapshot> {
            calls++
            return if (calls == 1) firstCall.await() else immediate.removeFirstOrNull() ?: error("unqueued vote")
        }

        override suspend fun skip(pairToken: String, clientRequestId: String) = error("n/a")
        override suspend fun discard(pairToken: String, side: String, clientRequestId: String) = error("n/a")
        override suspend fun special(pairToken: String, side: String, clientRequestId: String) = error("n/a")
        override suspend fun cancel(clientRequestId: String) = error("n/a")
        override suspend fun save() = error("n/a")
        override suspend fun session(): Rm2Result<Snapshot> = error("n/a")
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
