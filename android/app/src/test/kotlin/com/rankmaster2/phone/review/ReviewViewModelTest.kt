package com.rankmaster2.phone.review

import com.rankmaster2.phone.net.Rm2Result
import com.rankmaster2.phone.review.ReviewFixtures.discarded
import com.rankmaster2.phone.review.ReviewFixtures.dropped
import com.rankmaster2.phone.review.ReviewFixtures.items
import com.rankmaster2.phone.review.ReviewFixtures.refused
import com.rankmaster2.phone.review.ReviewFixtures.undone
import com.rankmaster2.phone.review.ReviewFixtures.unreachable
import com.rankmaster2.phone.ui.review.ReviewState.Phase
import com.rankmaster2.phone.ui.review.ReviewViewModel
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.UnconfinedTestDispatcher
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.advanceUntilIdle
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The review screen's logic: keep (a tap), discard (a swipe left; optimistic, retried with one id,
 * put back on failure), cancel (one level on the server, as many as you like for keeps), the ends of
 * the list, where it starts and where it saves its position, and that nothing but a fatal problem is
 * ever said out loud.
 *
 * What these protect is the owner's files, so most of them assert what was *not* sent, and in what
 * order: a discard resent with a new id, a cancel that overtakes the discard it undoes, an undo of
 * something the server can no longer undo.
 */
@OptIn(ExperimentalCoroutinesApi::class)
class ReviewViewModelTest {

    private fun TestScope.viewModel(client: FakeReviewClient): ReviewViewModel {
        var n = 0
        val vm = ReviewViewModel(
            client = client,
            opened = ReviewFixtures.snapshot(),
            newRequestId = { "req-${++n}" },
            scope = TestScope(UnconfinedTestDispatcher(testScheduler)),
            retryDelayMs = 10,
        )
        vm.resume(ReviewFixtures.snapshot())
        return vm
    }

    private fun client(vararg ids: String, reviewPosition: String? = null) =
        FakeReviewClient().apply { listing = items(*ids, reviewPosition = reviewPosition) }

    private val ReviewViewModel.ids get() = state.value.items.map { it.id }
    private val ReviewViewModel.currentId get() = state.value.current?.id

    /** Let the debounce pass and everything it started finish. */
    private fun TestScope.settle() {
        advanceTimeBy(ReviewViewModel.POSITION_DEBOUNCE_MS + 1)
        runCurrent()
    }

    // -- loading and where to start ------------------------------------------------------------------

    @Test
    fun `it opens on the first item when the PC has no position`() = runTest {
        val vm = viewModel(client("a.jpg", "b.jpg", "c.jpg"))

        assertEquals(Phase.Reviewing, vm.state.value.phase)
        assertEquals("a.jpg", vm.currentId)
        assertFalse(vm.state.value.canCancel)
    }

    @Test
    fun `it opens on the stored item itself, not after it`() = runTest {
        val vm = viewModel(client("a.jpg", "b.jpg", "c.jpg", "d.jpg", reviewPosition = "c.jpg"))

        assertEquals("c.jpg", vm.currentId)
    }

    @Test
    fun `a stored item that has left the folder opens on the next one after it in natural order`() = runTest {
        // DSC_10 sorts after DSC_9 - not before it, as plain text order would have it.
        val vm = viewModel(client("DSC_8.jpg", "DSC_9.jpg", "DSC_11.jpg", "DSC_12.jpg", reviewPosition = "DSC_10.jpg"))

        assertEquals("DSC_11.jpg", vm.currentId)
    }

    @Test
    fun `a stored item beyond the end of the folder opens at the beginning`() = runTest {
        val vm = viewModel(client("a.jpg", "b.jpg", reviewPosition = "zzz.jpg"))

        assertEquals("a.jpg", vm.currentId)
    }

    @Test
    fun `startIndex covers every rule`() {
        val items = listOf("a", "c", "e").map { ReviewFixtures.ref(it) }

        assertEquals(0, ReviewViewModel.startIndex(items, null))
        assertEquals(0, ReviewViewModel.startIndex(items, "a"))
        assertEquals(1, ReviewViewModel.startIndex(items, "c"))
        assertEquals(2, ReviewViewModel.startIndex(items, "e"))
        assertEquals(1, ReviewViewModel.startIndex(items, "b"))
        assertEquals(2, ReviewViewModel.startIndex(items, "d"))
        assertEquals(0, ReviewViewModel.startIndex(items, "f"))
        assertEquals(0, ReviewViewModel.startIndex(emptyList(), "a"))
    }

    @Test
    fun `an empty folder says so and offers retry rather than a blank screen`() = runTest {
        val vm = viewModel(client())

        assertEquals(Phase.Failed, vm.state.value.phase)
        assertEquals("Nothing to review", vm.state.value.problem!!.title)
        assertFalse(vm.state.value.problem!!.fatal)
    }

    @Test
    fun `a session that has gone is a fatal message and not a retry`() = runTest {
        val c = FakeReviewClient().apply { listing = refused(404, "no_session", "No session") }

        val vm = viewModel(c)

        assertEquals(Phase.Failed, vm.state.value.phase)
        assertTrue(vm.state.value.problem!!.fatal)
    }

    @Test
    fun `an unreachable PC on load can be retried`() = runTest {
        val c = FakeReviewClient().apply {
            itemsResults += unreachable()
            listing = items("a.jpg", "b.jpg")
        }
        val vm = viewModel(c)
        assertEquals(Phase.Failed, vm.state.value.phase)
        assertFalse(vm.state.value.problem!!.fatal)

        vm.retry()

        assertEquals(Phase.Reviewing, vm.state.value.phase)
        assertEquals("a.jpg", vm.currentId)
        assertNull(vm.state.value.problem)
    }

    // -- tap: keep ------------------------------------------------------------------------------------

    @Test
    fun `keep advances without a call to the PC`() = runTest {
        val c = client("a.jpg", "b.jpg", "c.jpg")
        val vm = viewModel(c)
        val readsBefore = c.itemsReads

        vm.keep()

        assertEquals("b.jpg", vm.currentId)
        assertTrue(c.discards.isEmpty())
        assertEquals(readsBefore, c.itemsReads)
        assertTrue(vm.state.value.canCancel)
    }

    @Test
    fun `taps and swipes are ignored while a cancel is in flight`() = runTest {
        val gate = CompletableDeferred<Unit>()
        val c = client("a.jpg", "b.jpg", "c.jpg").apply {
            discardGate = gate
            discardResults += discarded("a.jpg", "req-1")
        }
        val vm = viewModel(c)
        vm.discard()
        vm.cancel()
        assertTrue(vm.state.value.busy)

        vm.keep()
        vm.discard()

        assertEquals("b.jpg", vm.currentId)
        assertEquals(1, c.discards.size)
    }

    // -- swipe left: discard ----------------------------------------------------------------------------

    @Test
    fun `discard shows the next item at once, before the PC has answered`() = runTest {
        val c = client("a.jpg", "b.jpg", "c.jpg").apply { discardGate = CompletableDeferred() }
        val vm = viewModel(c)

        vm.discard()

        // The call is out and unanswered, and the screen has already moved on.
        assertEquals(1, c.discards.size)
        assertEquals(listOf("b.jpg", "c.jpg"), vm.ids)
        assertEquals("b.jpg", vm.currentId)
        // Cancel is on while the discard is still waiting: it will wait for it.
        assertTrue(vm.state.value.canCancel)
    }

    @Test
    fun `a successful discard sends the id and one request id, and adopts the snapshot`() = runTest {
        val c = client("a.jpg", "b.jpg").apply { discardResults += discarded("a.jpg", "req-1") }
        val vm = viewModel(c)

        vm.discard()

        assertEquals(listOf(SentDiscard("a.jpg", "req-1")), c.discards)
        assertEquals("discard", vm.state.value.snapshot.lastAction!!.type)
        assertEquals(listOf("b.jpg"), vm.ids)
        assertTrue(vm.state.value.canCancel)
    }

    @Test
    fun `a discard that times out is resent with the same id and the same request id`() = runTest {
        val c = client("a.jpg", "b.jpg").apply {
            discardResults += unreachable()
            discardResults += unreachable()
            discardResults += discarded("a.jpg", "req-1")
        }
        val vm = viewModel(c)

        vm.discard()
        advanceUntilIdle()

        assertEquals(3, c.discards.size)
        assertEquals(setOf(SentDiscard("a.jpg", "req-1")), c.discards.toSet())
        assertEquals(listOf("b.jpg"), vm.ids)
        assertNull(vm.state.value.problem)
    }

    @Test
    fun `session_busy is waited out and resent with the same request id`() = runTest {
        val c = client("a.jpg", "b.jpg").apply {
            discardResults += refused(503, "session_busy")
            discardResults += discarded("a.jpg", "req-1")
        }
        val vm = viewModel(c)

        vm.discard()
        advanceUntilIdle()

        assertEquals(listOf(SentDiscard("a.jpg", "req-1"), SentDiscard("a.jpg", "req-1")), c.discards)
        assertNull(vm.state.value.problem)
    }

    @Test
    fun `calls reach the PC one at a time and in the order they were made`() = runTest {
        val gate = CompletableDeferred<Unit>()
        val c = client("a.jpg", "b.jpg", "c.jpg").apply {
            discardGate = gate
            discardResults += discarded("a.jpg", "req-1")
            discardResults += discarded("b.jpg", "req-2")
        }
        val vm = viewModel(c)

        vm.discard()
        vm.discard()

        // The second is made but must not overtake the first, which is still waiting.
        assertEquals(listOf(SentDiscard("a.jpg", "req-1")), c.discards)
        assertEquals(listOf("c.jpg"), vm.ids)

        gate.complete(Unit)
        assertEquals(listOf("a.jpg", "b.jpg"), c.discards.map { it.id })
    }

    @Test
    fun `a file that had already gone is dropped and cannot be cancelled`() = runTest {
        val c = client("a.jpg", "b.jpg").apply { discardResults += dropped("a.jpg", "req-1") }
        val vm = viewModel(c)

        vm.discard()

        assertEquals(listOf("b.jpg"), vm.ids)
        assertFalse(vm.state.value.canCancel)
        assertNull(vm.state.value.problem)
    }

    @Test
    fun `a file the session does not know is not put back as a ghost`() = runTest {
        val c = client("a.jpg", "b.jpg").apply { discardResults += refused(404, "unknown_media_id") }
        val vm = viewModel(c)

        vm.discard()

        assertEquals(listOf("b.jpg"), vm.ids)
        assertNull(vm.state.value.problem)
        assertFalse(vm.state.value.canCancel)
    }

    // -- silence ------------------------------------------------------------------------------------------

    @Test
    fun `a discard that fails for good puts the item back where it was and says nothing`() = runTest {
        val c = client("a.jpg", "b.jpg", "c.jpg").apply {
            discardResults += refused(500, "move_failed", "disk is read-only")
        }
        val vm = viewModel(c)
        vm.keep()           // now on b.jpg
        vm.discard()        // b.jpg goes; the fake answers at once with the failure

        assertEquals(listOf("a.jpg", "b.jpg", "c.jpg"), vm.ids)
        assertEquals("b.jpg", vm.currentId)
        assertNull(vm.state.value.problem)
        // The failed discard is not on the history any more, so Cancel is back to the keep before it.
        assertTrue(vm.state.value.canCancel)
        vm.cancel()
        assertEquals("a.jpg", vm.currentId)
    }

    @Test
    fun `an unreachable PC after every retry puts it back, reads the list again, and says nothing`() = runTest {
        val c = client("a.jpg", "b.jpg").apply {
            repeat(ReviewViewModel.MAX_ATTEMPTS) { discardResults += unreachable() }
        }
        val vm = viewModel(c)
        val readsBefore = c.itemsReads

        vm.discard()
        advanceUntilIdle()

        assertEquals(ReviewViewModel.MAX_ATTEMPTS, c.discards.size)
        assertEquals(setOf(SentDiscard("a.jpg", "req-1")), c.discards.toSet())
        assertEquals(listOf("a.jpg", "b.jpg"), vm.ids)
        assertEquals("a.jpg", vm.currentId)
        assertNull(vm.state.value.problem)
        assertEquals(readsBefore + 1, c.itemsReads)
    }

    @Test
    fun `a save that failed after the file moved keeps it discarded, cancellable, and silent`() = runTest {
        val moved = (discarded("a.jpg", "req-1") as Rm2Result.Ok).value
        val c = client("a.jpg", "b.jpg").apply {
            discardResults += Rm2Result.Refused(
                status = 500,
                code = "save_failed",
                message = "disk full",
                session = moved,
                details = kotlinx.serialization.json.buildJsonObject {
                    put("recordsChanged", kotlinx.serialization.json.JsonPrimitive(true))
                    put("fileMoved", kotlinx.serialization.json.JsonPrimitive(true))
                },
            )
        }
        val vm = viewModel(c)

        vm.discard()

        assertEquals(listOf("b.jpg"), vm.ids)
        assertNull(vm.state.value.problem)
        assertTrue(vm.state.value.canCancel)
    }

    @Test
    fun `a discard refused because the session is gone puts it back and is fatal`() = runTest {
        val c = client("a.jpg", "b.jpg").apply { discardResults += refused(404, "no_session") }
        val vm = viewModel(c)

        vm.discard()

        assertEquals("a.jpg", vm.currentId)
        assertTrue(vm.state.value.problem!!.fatal)
        assertFalse(vm.state.value.actionable)
    }

    @Test
    fun `a certificate mismatch is fatal and nothing more is sent`() = runTest {
        val c = client("a.jpg", "b.jpg").apply {
            discardResults += Rm2Result.Unreachable(null, "pin", pinMismatch = true)
        }
        val vm = viewModel(c)
        val readsBefore = c.itemsReads

        vm.discard()

        assertTrue(vm.state.value.problem!!.fatal)
        assertEquals(1, c.discards.size)
        assertEquals(readsBefore, c.itemsReads)
    }

    @Test
    fun `a cancel the PC refuses is silent and the list is read again`() = runTest {
        val c = client("a.jpg", "b.jpg").apply {
            discardResults += discarded("a.jpg", "req-1")
            cancelResults += refused(409, "nothing_to_undo")
        }
        val vm = viewModel(c)
        vm.discard()
        val readsBefore = c.itemsReads
        c.itemsResults += items("b.jpg")

        vm.cancel()

        assertNull(vm.state.value.problem)
        assertEquals(readsBefore + 1, c.itemsReads)
        assertEquals(listOf("b.jpg"), vm.ids)
        assertFalse(vm.state.value.busy)
    }

    @Test
    fun `a cancel that cannot reach the PC is silent and the list is read again`() = runTest {
        val c = client("a.jpg", "b.jpg").apply {
            discardResults += discarded("a.jpg", "req-1")
            repeat(ReviewViewModel.MAX_ATTEMPTS) { cancelResults += unreachable() }
        }
        val vm = viewModel(c)
        vm.discard()
        val readsBefore = c.itemsReads

        vm.cancel()
        advanceUntilIdle()

        assertNull(vm.state.value.problem)
        assertEquals(readsBefore + 1, c.itemsReads)
        assertFalse(vm.state.value.busy)
    }

    @Test
    fun `a cancel refused because the session is gone is fatal`() = runTest {
        val c = client("a.jpg", "b.jpg").apply {
            discardResults += discarded("a.jpg", "req-1")
            cancelResults += refused(404, "no_session")
        }
        val vm = viewModel(c)
        vm.discard()

        vm.cancel()

        assertTrue(vm.state.value.problem!!.fatal)
    }

    // -- cancel ---------------------------------------------------------------------------------------

    @Test
    fun `cancel of a keep goes back to that item and sends nothing`() = runTest {
        val c = client("a.jpg", "b.jpg", "c.jpg")
        val vm = viewModel(c)
        vm.keep()
        vm.keep()
        assertEquals("c.jpg", vm.currentId)

        vm.cancel()

        assertEquals("b.jpg", vm.currentId)
        assertTrue(c.cancels.isEmpty())

        vm.cancel()
        assertEquals("a.jpg", vm.currentId)
        assertFalse(vm.state.value.canCancel)
    }

    @Test
    fun `cancel of a discard asks the PC to undo and reads the list for the restored file`() = runTest {
        val c = client("a.jpg", "b.jpg").apply {
            discardResults += discarded("a.jpg", "req-1")
            cancelResults += undone("a.jpg")
        }
        val vm = viewModel(c)
        vm.discard()
        c.itemsResults += items("a.jpg", "b.jpg", session = ReviewFixtures.snapshot())

        vm.cancel()

        assertEquals(1, c.cancels.size)
        assertEquals(listOf("a.jpg", "b.jpg"), vm.ids)
        assertEquals("a.jpg", vm.currentId)
        assertFalse(vm.state.value.busy)
        assertFalse(vm.state.value.canCancel)
    }

    @Test
    fun `cancel makes the restored id current when it came back under another name`() = runTest {
        val c = client("a.jpg", "b.jpg", "c.jpg").apply {
            discardResults += discarded("a.jpg", "req-1")
            cancelResults += undone("a.jpg", restoredId = "a (2).jpg")
        }
        val vm = viewModel(c)
        vm.discard()
        c.itemsResults += items("a (2).jpg", "b.jpg", "c.jpg")

        vm.cancel()

        assertEquals("a (2).jpg", vm.currentId)
        assertEquals(listOf("a (2).jpg", "b.jpg", "c.jpg"), vm.ids)
    }

    @Test
    fun `cancel waits for the discard it undoes instead of overtaking it`() = runTest {
        val gate = CompletableDeferred<Unit>()
        val c = client("a.jpg", "b.jpg").apply {
            discardGate = gate
            discardResults += discarded("a.jpg", "req-1")
            cancelResults += undone("a.jpg")
        }
        val vm = viewModel(c)
        vm.discard()
        assertTrue(vm.state.value.canCancel)

        vm.cancel()

        assertTrue(vm.state.value.busy)
        assertTrue(c.cancels.isEmpty())

        c.itemsResults += items("a.jpg", "b.jpg")
        gate.complete(Unit)

        assertEquals(1, c.cancels.size)
        val order = c.log.filter { it == "discard:a.jpg" || it == "cancel" }
        assertEquals(listOf("discard:a.jpg", "cancel"), order)
        assertEquals("a.jpg", vm.currentId)
        assertFalse(vm.state.value.busy)
    }

    @Test
    fun `cancel is only on while the server can still undo the top of the history`() = runTest {
        // discard A, keep B, discard C: cancel undoes C, cancel goes back to B, then it is off -
        // A is no longer the server's last action, so it can no longer be undone.
        val c = client("a.jpg", "b.jpg", "c.jpg", "d.jpg").apply {
            discardResults += discarded("a.jpg", "req-1")
            discardResults += discarded("c.jpg", "req-2")
            cancelResults += undone("c.jpg")
        }
        val vm = viewModel(c)

        vm.discard()                 // A
        vm.keep()                    // B
        vm.discard()                 // C
        assertEquals("d.jpg", vm.currentId)
        assertTrue(vm.state.value.canCancel)

        c.itemsResults += items("b.jpg", "c.jpg", "d.jpg")
        vm.cancel()                  // undoes C on the server
        assertEquals("c.jpg", vm.currentId)
        assertEquals(1, c.cancels.size)
        assertTrue(vm.state.value.canCancel)   // the keep of B is next

        vm.cancel()                  // back to B, locally
        assertEquals("b.jpg", vm.currentId)
        assertEquals(1, c.cancels.size)
        assertFalse(vm.state.value.canCancel)  // A is out of the server's reach

        vm.cancel()                  // does nothing
        assertEquals("b.jpg", vm.currentId)
        assertEquals(1, c.cancels.size)
    }

    // -- the end --------------------------------------------------------------------------------------

    @Test
    fun `keeping the last item ends the list`() = runTest {
        val vm = viewModel(client("a.jpg", "b.jpg"))

        vm.keep()
        vm.keep()

        assertEquals(Phase.Done, vm.state.value.phase)
        assertNull(vm.state.value.current)
    }

    @Test
    fun `discarding the last item ends the list`() = runTest {
        val c = client("a.jpg", "b.jpg").apply {
            discardResults += discarded("a.jpg", "req-1")
            discardResults += discarded("b.jpg", "req-2")
        }
        val vm = viewModel(c)

        vm.discard()
        vm.discard()

        assertEquals(Phase.Done, vm.state.value.phase)
    }

    @Test
    fun `cancel still works at the end`() = runTest {
        val vm = viewModel(client("a.jpg", "b.jpg"))
        vm.keep()
        vm.keep()
        assertEquals(Phase.Done, vm.state.value.phase)
        assertTrue(vm.state.value.canCancel)

        vm.cancel()

        assertEquals(Phase.Reviewing, vm.state.value.phase)
        assertEquals("b.jpg", vm.currentId)
    }

    @Test
    fun `start from the beginning goes to the first item and clears the history`() = runTest {
        val vm = viewModel(client("a.jpg", "b.jpg"))
        vm.keep()
        vm.keep()

        vm.restart()

        assertEquals(Phase.Reviewing, vm.state.value.phase)
        assertEquals("a.jpg", vm.currentId)
        assertFalse(vm.state.value.canCancel)
    }

    // -- the menu ---------------------------------------------------------------------------------------

    @Test
    fun `the menu is about the item it was opened on and closes when that item goes`() = runTest {
        val vm = viewModel(client("a.jpg", "b.jpg"))

        vm.openMenu()
        assertEquals("a.jpg", vm.state.value.menuFor)

        vm.keep()
        assertNull(vm.state.value.menuFor)
    }

    @Test
    fun `discard from the menu is a discard of that item`() = runTest {
        val c = client("a.jpg", "b.jpg").apply { discardResults += discarded("a.jpg", "req-1") }
        val vm = viewModel(c)

        vm.openMenu()
        vm.discardFromMenu()

        assertEquals(listOf(SentDiscard("a.jpg", "req-1")), c.discards)
        assertEquals("b.jpg", vm.currentId)
        assertNull(vm.state.value.menuFor)
    }

    @Test
    fun `restart from the menu closes it`() = runTest {
        val vm = viewModel(client("a.jpg", "b.jpg"))
        vm.keep()
        vm.openMenu()

        vm.restart()

        assertEquals("a.jpg", vm.currentId)
        assertNull(vm.state.value.menuFor)
    }

    @Test
    fun `the menu will not open with nothing on screen or a fatal problem up`() = runTest {
        val gone = viewModel(FakeReviewClient().apply { listing = refused(404, "no_session") })
        gone.openMenu()
        assertNull(gone.state.value.menuFor)

        val vm = viewModel(client("a.jpg"))
        vm.keep()
        assertEquals(Phase.Done, vm.state.value.phase)
        vm.openMenu()
        assertNull(vm.state.value.menuFor)
    }

    // -- the position, saved on the PC ----------------------------------------------------------------------

    @Test
    fun `opening where the PC already says writes nothing`() = runTest {
        val c = client("a.jpg", "b.jpg", "c.jpg", reviewPosition = "b.jpg")
        viewModel(c)

        advanceUntilIdle()

        assertTrue(c.positions.isEmpty())
    }

    @Test
    fun `opening somewhere else than the stored id writes where it opened`() = runTest {
        val c = client("a.jpg", "c.jpg", "d.jpg", reviewPosition = "b.jpg")
        viewModel(c)

        settle()

        assertEquals(listOf<String?>("c.jpg"), c.positions)
    }

    @Test
    fun `every change of the current item writes the new id, once, after the debounce, latest wins`() = runTest {
        val c = client("a.jpg", "b.jpg", "c.jpg", "d.jpg", reviewPosition = "a.jpg")
        val vm = viewModel(c)

        vm.keep()
        vm.keep()
        vm.keep()
        advanceTimeBy(ReviewViewModel.POSITION_DEBOUNCE_MS - 1)
        runCurrent()
        assertTrue("nothing is written inside the debounce", c.positions.isEmpty())

        advanceTimeBy(2)
        runCurrent()
        assertEquals(listOf<String?>("d.jpg"), c.positions)
    }

    @Test
    fun `a discard writes the item that is on screen afterwards`() = runTest {
        val c = client("a.jpg", "b.jpg", "c.jpg", reviewPosition = "a.jpg").apply {
            discardResults += discarded("a.jpg", "req-1")
        }
        val vm = viewModel(c)

        vm.discard()
        settle()

        assertEquals(listOf<String?>("b.jpg"), c.positions)
    }

    @Test
    fun `cancel writes the item it went back to`() = runTest {
        val c = client("a.jpg", "b.jpg", "c.jpg", reviewPosition = "a.jpg")
        val vm = viewModel(c)
        vm.keep()
        vm.keep()
        settle()

        vm.cancel()
        settle()

        assertEquals(listOf<String?>("c.jpg", "b.jpg"), c.positions)
    }

    @Test
    fun `a discard that is put back writes the item that came back`() = runTest {
        val c = client("a.jpg", "b.jpg", reviewPosition = "a.jpg").apply {
            discardResults += refused(500, "move_failed")
        }
        val vm = viewModel(c)

        vm.discard()
        settle()

        // Back on the item the PC already holds: nothing to write.
        assertEquals("a.jpg", vm.currentId)
        assertTrue(c.positions.isEmpty())
    }

    @Test
    fun `the end of the list writes null, and starting over writes the first item`() = runTest {
        val c = client("a.jpg", "b.jpg", reviewPosition = "a.jpg")
        val vm = viewModel(c)

        vm.keep()
        vm.keep()
        settle()
        assertEquals(Phase.Done, vm.state.value.phase)
        assertEquals(listOf<String?>(null), c.positions)

        vm.restart()
        settle()
        assertEquals(listOf<String?>(null, "a.jpg"), c.positions)
    }

    @Test
    fun `leaving sends the pending position before it closes the session`() = runTest {
        val c = client("a.jpg", "b.jpg", reviewPosition = "a.jpg")
        val vm = viewModel(c)
        vm.keep()   // inside the debounce: not written yet
        assertTrue(c.positions.isEmpty())

        var left = false
        vm.leave { left = true }

        assertTrue(left)
        assertEquals(listOf("items", "position:b.jpg", "close"), c.log)
        // The cancelled debounce does not write it a second time.
        advanceUntilIdle()
        assertEquals(listOf<String?>("b.jpg"), c.positions)
    }

    @Test
    fun `leaving with nothing pending only closes`() = runTest {
        val c = client("a.jpg", "b.jpg", reviewPosition = "a.jpg")
        val vm = viewModel(c)

        vm.leave { }

        assertEquals(listOf("items", "close"), c.log)
    }

    @Test
    fun `leaving waits for a discard still going out, then sends the position, then closes`() = runTest {
        val gate = CompletableDeferred<Unit>()
        val c = client("a.jpg", "b.jpg", reviewPosition = "a.jpg").apply {
            discardGate = gate
            discardResults += discarded("a.jpg", "req-1")
        }
        val vm = viewModel(c)
        vm.discard()

        var left = false
        vm.leave { left = true }

        assertTrue(left)
        assertFalse(c.closed)

        gate.complete(Unit)

        assertTrue(c.closed)
        assertEquals(listOf("discard:a.jpg", "position:b.jpg", "close"), c.log.filter { it != "items" })
    }

    @Test
    fun `a position write that fails is quiet and the next change sends again`() = runTest {
        val c = client("a.jpg", "b.jpg", "c.jpg", reviewPosition = "a.jpg").apply {
            positionResults += unreachable()
        }
        val vm = viewModel(c)

        vm.keep()
        settle()
        assertNull(vm.state.value.problem)
        assertEquals(listOf<String?>("b.jpg"), c.positions)

        vm.keep()
        settle()
        assertNull(vm.state.value.problem)
        assertEquals(listOf<String?>("b.jpg", "c.jpg"), c.positions)
    }

    @Test
    fun `leaving tries again for a position that did not land`() = runTest {
        val c = client("a.jpg", "b.jpg", reviewPosition = "a.jpg").apply {
            positionResults += unreachable()
        }
        val vm = viewModel(c)
        vm.keep()
        settle()

        vm.leave { }

        assertEquals(listOf<String?>("b.jpg", "b.jpg"), c.positions)
        assertTrue(c.closed)
    }

    @Test
    fun `a position write that never answers does not hold up a discard`() = runTest {
        val c = client("a.jpg", "b.jpg", "c.jpg", reviewPosition = "a.jpg").apply {
            discardResults += discarded("b.jpg", "req-1")
        }
        val vm = viewModel(c)
        vm.keep()
        c.positionGate = CompletableDeferred()   // the PC takes the write and never answers
        settle()
        assertEquals(listOf("items", "position:b.jpg"), c.log)

        // The discard changes the position, which drops the stale write and frees the line at once.
        vm.discard()

        assertEquals(listOf(SentDiscard("b.jpg", "req-1")), c.discards)
    }

    @Test
    fun `a position write that never answers holds up leaving for its ceiling and no longer`() = runTest {
        val c = client("a.jpg", "b.jpg", reviewPosition = "a.jpg")
        val vm = viewModel(c)
        vm.keep()
        c.positionGate = CompletableDeferred()
        settle()

        vm.leave { }
        assertFalse(c.closed)

        advanceTimeBy(ReviewViewModel.POSITION_TIMEOUT_MS + 1)
        runCurrent()

        assertTrue(c.closed)
    }

    // -- coming back to the front -------------------------------------------------------------------------

    @Test
    fun `returning to the front re-reads the list and keeps the current item`() = runTest {
        val c = client("a.jpg", "b.jpg", "c.jpg")
        val vm = viewModel(c)
        vm.keep()
        assertEquals("b.jpg", vm.currentId)

        // While the phone was elsewhere a file appeared before the current one.
        c.itemsResults += items("a.jpg", "a2.jpg", "b.jpg", "c.jpg")
        vm.onForeground(false)
        vm.onForeground(true)

        assertEquals("b.jpg", vm.currentId)
        assertEquals(listOf("a.jpg", "a2.jpg", "b.jpg", "c.jpg"), vm.ids)
    }

    @Test
    fun `when the current item has gone the same place in the list is shown`() = runTest {
        val c = client("a.jpg", "b.jpg", "c.jpg")
        val vm = viewModel(c)
        vm.keep()

        c.itemsResults += items("a.jpg", "c.jpg")
        vm.onForeground(true)

        assertEquals("c.jpg", vm.currentId)
    }

    @Test
    fun `an unreachable PC on the way back changes nothing`() = runTest {
        val c = client("a.jpg", "b.jpg")
        val vm = viewModel(c)
        c.itemsResults += unreachable()

        vm.onForeground(true)

        assertEquals("a.jpg", vm.currentId)
        assertNull(vm.state.value.problem)
    }

    @Test
    fun `a fatal refusal on the way back is shown`() = runTest {
        val c = client("a.jpg", "b.jpg")
        val vm = viewModel(c)
        c.itemsResults += refused(404, "no_session")

        vm.onForeground(true)

        assertNotNull(vm.state.value.problem)
        assertTrue(vm.state.value.problem!!.fatal)
    }
}
