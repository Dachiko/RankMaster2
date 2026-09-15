package com.rankmaster2.phone.browse

import com.rankmaster2.phone.net.Root
import com.rankmaster2.phone.net.Roots
import com.rankmaster2.phone.net.Rm2Result
import com.rankmaster2.phone.ui.browse.BrowseViewModel
import com.rankmaster2.phone.ui.browse.CountsPhase
import com.rankmaster2.phone.ui.browse.MediaTally
import com.rankmaster2.phone.ui.browse.Place
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
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

/**
 * Walking the tree: down, back up, and knowing when there is no "up" left.
 *
 * These are plain JVM tests. The screen is Compose but every decision in it is made in the
 * [BrowseViewModel], which is the point of keeping it there.
 */
@OptIn(ExperimentalCoroutinesApi::class)
class BrowseNavigationTest {

    private val dispatcher = StandardTestDispatcher()
    private val client = FakeRm2Client()

    @Before fun setUp() = Dispatchers.setMain(dispatcher)
    @After fun tearDown() = Dispatchers.resetMain()

    private fun vm() = BrowseViewModel(client)

    @Test
    fun `starts at the drive roots`() = runTest(dispatcher) {
        client.rootsResult = Rm2Result.Ok(
            Roots(
                listOf(
                    Root("C:\\", "System (C:)", "fixed", available = true, totalBytes = 512L * 1024 * 1024 * 1024, freeBytes = 64L * 1024 * 1024 * 1024),
                    Root("E:\\", "DVD drive (E:)", "removable", available = false, totalBytes = null, freeBytes = null),
                ),
            ),
        )

        val vm = vm()
        advanceUntilIdle()

        assertEquals(Place.Roots, vm.state.value.place)
        // § 10.14: an unavailable root "MUST still appear". Dropping the empty optical drive is how
        // an owner ends up convinced the app cannot see their disconnected NAS either.
        assertEquals(listOf("C:\\", "E:\\"), vm.state.value.roots.map { it.path })
        assertFalse(vm.state.value.roots[1].available)
    }

    @Test
    fun `walking into a folder lists its children`() = runTest(dispatcher) {
        client.listings[rootD(false)] = listing(
            "D:\\", null,
            uncounted("Photos", "D:\\Photos"),
            uncounted("Video", "D:\\Video"),
        )
        client.listings[rootD(true)] = listing(
            "D:\\", null,
            counted("Photos", "D:\\Photos", stills = 40, videos = 0),
            counted("Video", "D:\\Video", stills = 0, videos = 9),
        )

        val vm = vm()
        advanceUntilIdle()
        vm.enter("D:\\")
        advanceUntilIdle()

        val state = vm.state.value
        assertEquals(Place.Folder("D:\\", null), state.place)
        assertEquals(listOf("Photos", "Video"), state.rows.map { it.name })
    }

    @Test
    fun `parent null is how we know we are at a root, and up from there is the drive list`() =
        runTest(dispatcher) {
            client.rootsResult = Rm2Result.Ok(Roots(listOf(Root("D:\\", "Data (D:)", "fixed", true, null, null))))
            // A drive root: deep-looking path, `parent` null. Nothing here may be inferred from the
            // string - `D:\` and `\\nas\share` and `\\?\Volume{...}\` are all roots and look nothing
            // like each other.
            client.listings["\\\\nas\\share" to false] = listing("\\\\nas\\share", null, uncounted("Trip", "\\\\nas\\share\\Trip"))
            client.listings["\\\\nas\\share" to true] = listing("\\\\nas\\share", null, counted("Trip", "\\\\nas\\share\\Trip", 5, 0))

            val vm = vm()
            advanceUntilIdle()
            vm.enter("\\\\nas\\share")
            advanceUntilIdle()

            assertTrue("a null parent means a root", vm.state.value.atRoot)
            assertTrue(vm.state.value.canGoUp)

            vm.up()
            advanceUntilIdle()

            assertEquals(Place.Roots, vm.state.value.place)
            assertEquals(listOf("D:\\"), vm.state.value.roots.map { it.path })
            // "Up" from a root is the drive list, so it must not have browsed anything.
            assertEquals(listOf("\\\\nas\\share" to false, "\\\\nas\\share" to true), client.browseCalls)
        }

    @Test
    fun `up from a sub-folder goes to the parent the server named, not to a chopped-off path`() =
        runTest(dispatcher) {
            // Deliberately a parent that no amount of string surgery on the child would produce.
            // If this test passes, the app is navigating by the server's `parent` and nothing else.
            val child = "D:\\Photos\\2024"
            val parentTheServerSays = "Z:\\somewhere\\else"

            client.listings[child to false] = listing(child, parentTheServerSays, uncounted("Iceland", "$child\\Iceland"))
            client.listings[child to true] = listing(child, parentTheServerSays, counted("Iceland", "$child\\Iceland", 30, 0))
            client.listings[parentTheServerSays to false] = listing(parentTheServerSays, "Z:\\somewhere", uncounted("else-child", "$parentTheServerSays\\c"))
            client.listings[parentTheServerSays to true] = listing(parentTheServerSays, "Z:\\somewhere", counted("else-child", "$parentTheServerSays\\c", 2, 0))

            val vm = vm()
            advanceUntilIdle()
            vm.enter(child)
            advanceUntilIdle()
            assertFalse(vm.state.value.atRoot)

            vm.up()
            advanceUntilIdle()

            assertEquals(Place.Folder(parentTheServerSays, "Z:\\somewhere"), vm.state.value.place)
            assertEquals(listOf("else-child"), vm.state.value.rows.map { it.name })
        }

    @Test
    fun `a listing that fails leaves a message and a way back`() = runTest(dispatcher) {
        client.rootsResult = Rm2Result.Ok(Roots(listOf(Root("D:\\", "Data (D:)", "fixed", true, null, null))))
        client.listings["D:\\Locked" to false] =
            Rm2Result.Refused(403, "folder_access_denied", "Access to the path is denied.")

        val vm = vm()
        advanceUntilIdle()
        vm.enter("D:\\Locked")
        advanceUntilIdle()

        assertEquals("Access to the path is denied.", vm.state.value.listFailure)
        assertFalse(vm.state.value.loadingList)
        // The failed listing never became a Place, so the browser is still standing at the drive
        // list and "Try again" puts it back there rather than stranding the owner on an error.
        assertEquals(Place.Roots, vm.state.value.place)

        vm.refresh()
        advanceUntilIdle()

        assertNull(vm.state.value.listFailure)
        assertEquals(listOf("D:\\"), vm.state.value.roots.map { it.path })
    }

    @Test
    fun `counts that arrive after the owner has walked away are dropped`() = runTest(dispatcher) {
        val gateA = client.gate("D:\\A")
        val gateB = client.gate("D:\\B")

        // The trap: A's counting pass claims 99 photos for a path that is actually one of B's
        // children, and it is released *after* B's. Without the generation guard the stale merge
        // lands last and the owner sees A's numbers against B's folder.
        client.listings["D:\\A" to false] = listing("D:\\A", "D:\\", uncounted("shared", "D:\\shared"))
        client.listings["D:\\A" to true] = listing("D:\\A", "D:\\", counted("shared", "D:\\shared", 99, 0))
        client.listings["D:\\B" to false] = listing("D:\\B", "D:\\", uncounted("shared", "D:\\shared"))
        client.listings["D:\\B" to true] = listing("D:\\B", "D:\\", counted("shared", "D:\\shared", 7, 0))

        val vm = vm()
        advanceUntilIdle()

        vm.enter("D:\\A")
        advanceUntilIdle()
        vm.enter("D:\\B")
        advanceUntilIdle()

        gateB.complete(Unit)
        advanceUntilIdle()
        gateA.complete(Unit)
        advanceUntilIdle()

        val state = vm.state.value
        assertEquals(Place.Folder("D:\\B", "D:\\"), state.place)
        assertEquals(MediaTally.Counted(7, 0), state.rows.single().tally)
        assertEquals(CountsPhase.Loaded, state.counts)
    }

    private fun rootD(counts: Boolean) = "D:\\" to counts
}
