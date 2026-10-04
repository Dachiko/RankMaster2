package com.rankmaster2.phone.review

import com.rankmaster2.phone.net.Rm2Result
import com.rankmaster2.phone.review.ReviewFixtures.dropped
import com.rankmaster2.phone.review.ReviewFixtures.discarded
import com.rankmaster2.phone.review.ReviewFixtures.items
import com.rankmaster2.phone.review.ReviewFixtures.refused
import com.rankmaster2.phone.review.ReviewFixtures.undone
import com.rankmaster2.phone.review.ReviewFixtures.unreachable
import com.rankmaster2.phone.ui.review.InMemoryReviewPositionStore
import com.rankmaster2.phone.ui.review.ReviewState
import com.rankmaster2.phone.ui.review.ReviewState.Phase
import com.rankmaster2.phone.ui.review.ReviewViewModel
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.UnconfinedTestDispatcher
import kotlinx.coroutines.test.advanceUntilIdle
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The review screen's logic: keep, discard (optimistic, retried with one id, rolled back on
 * failure), cancel (one level on the server, as many as you like for keeps), the ends of the list.
 *
 * What these protect is the owner's files, so most of them assert what was *not* sent, and in what
 * order: a discard resent with a new id, a cancel that overtakes the discard it undoes, an undo of
 * something the server can no longer undo.
 */
@OptIn(ExperimentalCoroutinesApi::class)
class ReviewViewModelTest {

    private val folder = ReviewFixtures.FOLDER

    private fun TestScope.viewModel(
        client: FakeReviewClient,
        store: InMemoryReviewPositionStore = InMemoryReviewPositionStore(),
    ): ReviewViewModel {
        var n = 0
        val vm = ReviewViewModel(
            client = client,
            opened = ReviewFixtures.snapshot(),
            positions = store,
            newRequestId = { "req-${++n}" },
            scope = TestScope(UnconfinedTestDispatcher(testScheduler)),
            retryDelayMs = 10,
        )
        vm.resume(ReviewFixtures.snapshot())
        return vm
    }

    private fun client(vararg ids: String) = FakeReviewClient().apply { listing = items(*ids) }

    private val ReviewViewModel.ids get() = state.value.items.map { it.id }
    private val ReviewViewModel.currentId get() = state.value.current?.id

    // -- loading and where to start ------------------------------------------------------------------

    @Test
    fun `it opens on the first item when nothing is remembered`() = runTest {
        val vm = viewModel(client("a.jpg", "b.jpg", "c.jpg"))

        assertEquals(Phase.Reviewing, vm.state.value.phase)
        assertEquals("a.jpg", vm.currentId)
        assertEquals(0, vm.state.value.percent)
        assertFalse(vm.state.value.canCancel)
    }

    @Test
    fun `it starts at the first item after the one he last moved past`() = runTest {
        val store = InMemoryReviewPositionStore().apply { remember(folder, "b.jpg") }

        val vm = viewModel(client("a.jpg", "b.jpg", "c.jpg", "d.jpg"), store)

        assertEquals("c.jpg", vm.currentId)
        assertEquals(50, vm.state.value.percent)
    }

    @Test
    fun `a remembered item that is gone or was the last starts from the beginning`() = runTest {
        val gone = InMemoryReviewPositionStore().apply { remember(folder, "zzz.jpg") }
        assertEquals("a.jpg", viewModel(client("a.jpg", "b.jpg"), gone).currentId)

        val last = InMemoryReviewPositionStore().apply { remember(folder, "b.jpg") }
        assertEquals("a.jpg", viewModel(client("a.jpg", "b.jpg"), last).currentId)
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
    }

    // -- keep -----------------------------------------------------------------------------------------

    @Test
    fun `keep advances without a call to the PC and remembers the position`() = runTest {
        val c = client("a.jpg", "b.jpg", "c.jpg")
        val store = InMemoryReviewPositionStore()
        val vm = viewModel(c, store)
        val readsBefore = c.itemsReads

        vm.keep()

        assertEquals("b.jpg", vm.currentId)
        assertEquals(33, vm.state.value.percent)
        assertTrue(c.discards.isEmpty())
        assertEquals(readsBefore, c.itemsReads)
        assertEquals("a.jpg", store.lastPassed(folder))
        assertTrue(vm.state.value.canCancel)
    }

    // -- discard --------------------------------------------------------------------------------------

    @Test
    fun `discard shows the next item at once, before the PC has answered`() = runTest {
        val c = client("a.jpg", "b.jpg", "c.jpg").apply { discardGate = CompletableDeferred() }
        val vm = viewModel(c)

        vm.discard()

        // The call is out and unanswered, and the screen has already moved on.
        assertEquals(1, c.discards.size)
        assertEquals(listOf("b.jpg", "c.jpg"), vm.ids)
        assertEquals("b.jpg", vm.currentId)
        assertEquals(1, vm.state.value.discardedThisVisit)
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
    fun `a discard that fails for good puts the item back where it was and says so`() = runTest {
        val c = client("a.jpg", "b.jpg", "c.jpg").apply {
            discardResults += refused(500, "move_failed", "disk is read-only")
        }
        val vm = viewModel(c)
        vm.keep()           // now on b.jpg
        vm.discard()        // b.jpg goes, c.jpg is current

        assertEquals("a.jpg", vm.ids.first())

        // (the failure was already delivered: the fake answers at once)
        assertEquals(listOf("a.jpg", "b.jpg", "c.jpg"), vm.ids)
        assertEquals("b.jpg", vm.currentId)
        assertEquals(0, vm.state.value.discardedThisVisit)
        val problem = vm.state.value.problem!!
        assertTrue(problem.title.contains("b.jpg"))
        assertTrue(problem.body.contains("disk is read-only"))
        assertFalse(problem.fatal)
        // The failed discard is not on the history any more, so Cancel is back to the keep before it.
        assertTrue(vm.state.value.canCancel)
        vm.cancel()
        assertEquals("a.jpg", vm.currentId)
    }

    @Test
    fun `a save that failed after the file moved keeps it discarded and still cancellable`() = runTest {
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
        assertEquals(1, vm.state.value.discardedThisVisit)
        assertTrue(vm.state.value.problem!!.title.startsWith("Discarded"))
        assertTrue(vm.state.value.canCancel)
    }

    @Test
    fun `an unreachable PC after every retry rolls back, reads the list again, and keeps the position`() = runTest {
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
        assertNotNull(vm.state.value.problem)
        assertEquals(readsBefore + 1, c.itemsReads)
    }

    @Test
    fun `dismissing the problem clears it unless it is fatal`() = runTest {
        val c = client("a.jpg", "b.jpg").apply { discardResults += refused(500, "move_failed") }
        val vm = viewModel(c)
        vm.discard()
        assertNotNull(vm.state.value.problem)

        vm.dismissProblem()

        assertNull(vm.state.value.problem)
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

    @Test
    fun `calls reach the PC one at a time and in the order they were swiped`() = runTest {
        val gate = CompletableDeferred<Unit>()
        val c = client("a.jpg", "b.jpg", "c.jpg").apply {
            discardGate = gate
            discardResults += discarded("a.jpg", "req-1")
            discardResults += discarded("b.jpg", "req-2")
        }
        val vm = viewModel(c)

        vm.discard()
        vm.discard()

        // The second is swiped but must not overtake the first, which is still waiting.
        assertEquals(listOf(SentDiscard("a.jpg", "req-1")), c.discards)
        assertEquals(listOf("c.jpg"), vm.ids)

        gate.complete(Unit)
        assertEquals(listOf("a.jpg", "b.jpg"), c.discards.map { it.id })
    }

    // -- cancel ---------------------------------------------------------------------------------------

    @Test
    fun `cancel of a keep goes back to that item and sends nothing`() = runTest {
        val c = client("a.jpg", "b.jpg", "c.jpg")
        val store = InMemoryReviewPositionStore()
        val vm = viewModel(c, store)
        vm.keep()
        vm.keep()
        assertEquals("c.jpg", vm.currentId)

        vm.cancel()

        assertEquals("b.jpg", vm.currentId)
        assertTrue(c.cancels.isEmpty())
        assertEquals("a.jpg", store.lastPassed(folder))

        vm.cancel()
        assertEquals("a.jpg", vm.currentId)
        assertFalse(vm.state.value.canCancel)
        assertNull(store.lastPassed(folder))
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
        assertEquals(0, vm.state.value.discardedThisVisit)
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
        val order = c.log.filter { it != "items" }
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

    @Test
    fun `a cancel the PC refuses says so and changes nothing`() = runTest {
        val c = client("a.jpg", "b.jpg").apply {
            discardResults += discarded("a.jpg", "req-1")
            cancelResults += refused(409, "nothing_to_undo")
        }
        val vm = viewModel(c)
        vm.discard()

        vm.cancel()

        assertEquals("Nothing to take back", vm.state.value.problem!!.title)
        assertEquals(listOf("b.jpg"), vm.ids)
        assertFalse(vm.state.value.busy)
    }

    // -- the end --------------------------------------------------------------------------------------

    @Test
    fun `keeping the last item ends the list at one hundred per cent`() = runTest {
        val vm = viewModel(client("a.jpg", "b.jpg"))

        vm.keep()
        vm.keep()

        assertEquals(Phase.Done, vm.state.value.phase)
        assertNull(vm.state.value.current)
        assertEquals(100, vm.state.value.percent)
    }

    @Test
    fun `discarding the last item ends the list and counts the discards of this visit`() = runTest {
        val c = client("a.jpg", "b.jpg").apply {
            discardResults += discarded("a.jpg", "req-1")
            discardResults += discarded("b.jpg", "req-2")
        }
        val vm = viewModel(c)

        vm.discard()
        vm.discard()

        assertEquals(Phase.Done, vm.state.value.phase)
        assertEquals(2, vm.state.value.discardedThisVisit)
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
    fun `start again goes to the first item and forgets the position`() = runTest {
        val store = InMemoryReviewPositionStore()
        val vm = viewModel(client("a.jpg", "b.jpg"), store)
        vm.keep()
        vm.keep()
        assertEquals("b.jpg", store.lastPassed(folder))

        vm.restart()

        assertEquals(Phase.Reviewing, vm.state.value.phase)
        assertEquals("a.jpg", vm.currentId)
        assertNull(store.lastPassed(folder))
        assertFalse(vm.state.value.canCancel)
    }

    // -- coming back to the front and leaving -------------------------------------------------------

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
    fun `leaving navigates at once and closes the session after any discard still waiting`() = runTest {
        val gate = CompletableDeferred<Unit>()
        val c = client("a.jpg", "b.jpg").apply {
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
        assertEquals(listOf("discard:a.jpg", "close"), c.log.filter { it != "items" })
    }

    // -- the numbers ------------------------------------------------------------------------------------

    @Test
    fun `progress is always a whole percentage between zero and one hundred`() {
        val items = listOf("a", "b", "c").map { ReviewFixtures.ref(it) }
        val base = ReviewState(snapshot = ReviewFixtures.snapshot(), phase = Phase.Reviewing, items = items)

        assertEquals(0, base.copy(index = 0).percent)
        assertEquals(33, base.copy(index = 1).percent)
        assertEquals(66, base.copy(index = 2).percent)
        assertEquals(100, base.copy(index = 3, phase = Phase.Done).percent)
    }

    @Test
    fun `startIndex is the first item after the remembered one`() {
        val items = listOf("a", "b", "c").map { ReviewFixtures.ref(it) }

        assertEquals(0, ReviewViewModel.startIndex(items, null))
        assertEquals(1, ReviewViewModel.startIndex(items, "a"))
        assertEquals(2, ReviewViewModel.startIndex(items, "b"))
        assertEquals(0, ReviewViewModel.startIndex(items, "c"))
        assertEquals(0, ReviewViewModel.startIndex(items, "zzz"))
    }
}
