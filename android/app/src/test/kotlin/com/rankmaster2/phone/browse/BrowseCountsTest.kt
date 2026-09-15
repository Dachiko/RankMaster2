package com.rankmaster2.phone.browse

import com.rankmaster2.phone.net.Rm2Result
import com.rankmaster2.phone.ui.browse.BrowseViewModel
import com.rankmaster2.phone.ui.browse.CountsPhase
import com.rankmaster2.phone.ui.browse.FolderRow
import com.rankmaster2.phone.ui.browse.MediaTally
import com.rankmaster2.phone.ui.browse.Rankability
import com.rankmaster2.phone.ui.browse.folderDetail
import com.rankmaster2.phone.ui.browse.notRankableReason
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
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

/**
 * The counting half of § 10.15: two requests, three meanings of "no number", and what happens when
 * the expensive one does not come back.
 */
@OptIn(ExperimentalCoroutinesApi::class)
class BrowseCountsTest {

    private val dispatcher = StandardTestDispatcher()
    private val client = FakeRm2Client()

    @Before fun setUp() = Dispatchers.setMain(dispatcher)
    @After fun tearDown() = Dispatchers.resetMain()

    private fun vm() = BrowseViewModel(client)

    /** The listing used by most of the tests below: one of every case § 10.15 can produce. */
    private fun stubMixedFolder() {
        client.listings[PATH to false] = listing(
            PATH, "D:\\",
            uncounted("Iceland", "$PATH\\Iceland"),
            uncounted("Empty", "$PATH\\Empty"),
            unreadable("System Volume Information", "$PATH\\System Volume Information"),
            uncounted("OneVideo", "$PATH\\OneVideo"),
        )
        client.listings[PATH to true] = listing(
            PATH, "D:\\",
            counted("Iceland", "$PATH\\Iceland", stills = 431, videos = 0),
            counted("Empty", "$PATH\\Empty", stills = 0, videos = 0),
            unreadable("System Volume Information", "$PATH\\System Volume Information"),
            counted("OneVideo", "$PATH\\OneVideo", stills = 0, videos = 1),
        )
    }

    @Test
    fun `the names are fetched without counts first, then the counts follow`() = runTest(dispatcher) {
        stubMixedFolder()
        val gate = client.gate(PATH)

        val vm = vm()
        advanceUntilIdle()
        vm.enter(PATH)
        advanceUntilIdle()

        // The counting call is parked, so this is exactly what the owner sees while a network
        // share grinds through one directory enumeration per child.
        val whileCounting = vm.state.value
        assertEquals(listOf(PATH to false, PATH to true), client.browseCalls)
        assertEquals(
            listOf("Iceland", "Empty", "System Volume Information", "OneVideo"),
            whileCounting.rows.map { it.name },
        )
        assertFalse("the names must not be behind a spinner", whileCounting.loadingList)
        assertEquals(CountsPhase.Loading, whileCounting.counts)
        // Not counted yet. Not zero.
        assertEquals(MediaTally.NotCounted, whileCounting.rows[0].tally)
        assertEquals(Rankability.Unknown, whileCounting.rows[0].rankability)
        // And nothing is greyed on an unknown: greying the whole listing while the counts are in
        // flight would tell the owner every folder is unrankable.
        assertTrue(whileCounting.rows.filter { it.accessible }.all { it.openable })

        gate.complete(Unit)
        advanceUntilIdle()

        val counted = vm.state.value
        assertEquals(CountsPhase.Loaded, counted.counts)
        assertEquals(MediaTally.Counted(431, 0), counted.rows[0].tally)
        assertEquals(
            listOf("Iceland", "Empty", "System Volume Information", "OneVideo"),
            counted.rows.map { it.name },
        )
    }

    @Test
    fun `null and zero and counted are three different things on screen`() = runTest(dispatcher) {
        stubMixedFolder()
        val vm = vm()
        advanceUntilIdle()
        vm.enter(PATH)
        advanceUntilIdle()

        val rows = vm.state.value.rows.associateBy { it.name }

        // Counted, non-zero.
        assertEquals(MediaTally.Counted(431, 0), rows.getValue("Iceland").tally)
        assertEquals("431 photos", folderDetail(rows.getValue("Iceland")))

        // Counted, and genuinely zero.
        assertEquals(MediaTally.Counted(0, 0), rows.getValue("Empty").tally)
        assertEquals("empty", folderDetail(rows.getValue("Empty")))

        // Not counted at all - and that says nothing, rather than saying "0".
        val notCounted = FolderRow.of(uncounted("Unknown", "$PATH\\Unknown"))
        assertEquals(MediaTally.NotCounted, notCounted.tally)
        assertEquals("", folderDetail(notCounted))
        assertEquals("counting…", folderDetail(notCounted, counting = true))

        // Null counts because it could not be read. The three are never the same string.
        assertEquals(MediaTally.Unreadable, rows.getValue("System Volume Information").tally)
        assertEquals("cannot be read", folderDetail(rows.getValue("System Volume Information")))
    }

    @Test
    fun `an inaccessible child does not break the listing and never says empty`() = runTest(dispatcher) {
        stubMixedFolder()
        val vm = vm()
        advanceUntilIdle()
        vm.enter(PATH)
        advanceUntilIdle()

        val row = vm.state.value.rows.single { !it.accessible }

        // § 10.15: "The whole listing MUST NOT fail because one child is unreadable."
        assertEquals(4, vm.state.value.rows.size)
        assertEquals(CountsPhase.Loaded, vm.state.value.counts)

        assertEquals(MediaTally.Unreadable, row.tally)
        assertFalse(row.openable)
        assertFalse(row.enterable)
        assertEquals("cannot be read", folderDetail(row))
        assertEquals("cannot be read", notRankableReason(row))
        // The bug this guards: `stillCount ?: 0` anywhere upstream turns a locked folder into an
        // empty one, and the owner concludes their photos are gone.
        assertFalse(folderDetail(row).contains("empty"))
    }

    @Test
    fun `a folder that cannot be opened is greyed with a reason, not hidden`() = runTest(dispatcher) {
        stubMixedFolder()
        val vm = vm()
        advanceUntilIdle()
        vm.enter(PATH)
        advanceUntilIdle()

        val oneVideo = vm.state.value.rows.single { it.name == "OneVideo" }

        assertTrue("it is still in the list", vm.state.value.rows.any { it.name == "OneVideo" })
        assertEquals(Rankability.No, oneVideo.rankability)
        assertFalse(oneVideo.openable)
        // Greyed, but still walkable: there may be something rankable one level further down.
        assertTrue(oneVideo.enterable)
        assertEquals("only 1 video - ranking needs 2", notRankableReason(oneVideo))
    }

    @Test
    fun `a mixed folder explains that only its photos count`() {
        // SPEC.md § Media policy: both kinds present means stills only, so one photo beside twenty
        // videos is not rankable - the single least obvious refusal in the whole app.
        val row = FolderRow.of(counted("Holiday", "D:\\Holiday", stills = 1, videos = 20, rankable = false))
        assertEquals(Rankability.No, row.rankability)
        assertEquals(
            "mixed folder - only the 1 photo can be ranked, and ranking needs 2",
            notRankableReason(row),
        )
        assertEquals("1 photo and 20 videos", folderDetail(row))
    }

    @Test
    fun `when the counting call fails the names still work`() = runTest(dispatcher) {
        client.listings[PATH to false] = listing(PATH, "D:\\", uncounted("Iceland", "$PATH\\Iceland"))
        client.listings[PATH to true] = Rm2Result.Unreachable(null, "timeout after 20s")

        val vm = vm()
        advanceUntilIdle()
        vm.enter(PATH)
        advanceUntilIdle()

        val state = vm.state.value
        assertEquals(CountsPhase.Failed, state.counts)
        assertEquals(listOf("Iceland"), state.rows.map { it.name })
        // Degraded, not broken: the row is still enterable and still offerable to open, because
        // "we could not count" is not "we know there is nothing here".
        assertTrue(state.rows.single().enterable)
        assertTrue(state.rows.single().openable)
        assertEquals(MediaTally.NotCounted, state.rows.single().tally)
        // And the whole-listing error channel stays clear - this is not a failed listing.
        assertEquals(null, state.listFailure)

        // Retrying the counts alone must not refetch the names.
        client.listings[PATH to true] = listing(PATH, "D:\\", counted("Iceland", "$PATH\\Iceland", 12, 0))
        vm.retryCounts()
        advanceUntilIdle()

        assertEquals(CountsPhase.Loaded, vm.state.value.counts)
        assertEquals(MediaTally.Counted(12, 0), vm.state.value.rows.single().tally)
        assertEquals(listOf(PATH to false, PATH to true, PATH to true), client.browseCalls)
    }

    @Test
    fun `counts merge onto the rows already drawn, keeping their order`() = runTest(dispatcher) {
        client.listings[PATH to false] = listing(
            PATH, "D:\\",
            uncounted("a", "$PATH\\a"),
            uncounted("b", "$PATH\\b"),
            uncounted("c", "$PATH\\c"),
        )
        // The counting pass comes back in a different order and has lost "b" - it was deleted on
        // the PC between the two calls.
        client.listings[PATH to true] = listing(
            PATH, "D:\\",
            counted("c", "$PATH\\c", 3, 0),
            counted("a", "$PATH\\a", 5, 0),
        )

        val vm = vm()
        advanceUntilIdle()
        vm.enter(PATH)
        advanceUntilIdle()

        val rows = vm.state.value.rows
        // Order is the one the owner is already looking at. A listing that reshuffles under a
        // thumb mid-scroll opens the wrong folder.
        assertEquals(listOf("a", "b", "c"), rows.map { it.name })
        assertEquals(MediaTally.Counted(5, 0), rows[0].tally)
        // "b" keeps its null: it was never counted, and we are not going to claim it is empty.
        assertEquals(MediaTally.NotCounted, rows[1].tally)
        assertEquals(MediaTally.Counted(3, 0), rows[2].tally)
    }

    private companion object {
        const val PATH = "D:\\Photos"
    }
}
