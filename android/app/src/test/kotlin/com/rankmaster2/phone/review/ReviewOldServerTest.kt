package com.rankmaster2.phone.review

import com.rankmaster2.phone.review.ReviewFixtures.refused
import com.rankmaster2.phone.review.ReviewFixtures.items
import com.rankmaster2.phone.ui.review.ReviewState.Phase
import com.rankmaster2.phone.ui.review.ReviewViewModel
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.UnconfinedTestDispatcher
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class ReviewOldServerTest {

    private fun TestScope.vm(client: FakeReviewClient) = ReviewViewModel(
        client, ReviewFixtures.snapshot(),
        newRequestId = { "req-1" },
        scope = TestScope(UnconfinedTestDispatcher(testScheduler)),
    ).also { it.resume(ReviewFixtures.snapshot()) }

    @Test
    fun `a server without the review routes says to update it, with no retry`() = runTest {
        val vm = vm(FakeReviewClient().apply { listing = refused(404, "not_found", "no such route") })

        assertEquals(Phase.Failed, vm.state.value.phase)
        assertEquals("Update the Rank Master server on the PC", vm.state.value.problem!!.title)
        assertTrue(vm.state.value.problem!!.fatal)
    }

    @Test
    fun `the list is shown in natural order whatever order the server sent`() = runTest {
        val vm = vm(FakeReviewClient().apply { listing = items("DSC_10.jpg", "DSC_9.jpg", "dsc_100.jpg") })

        assertEquals(listOf("DSC_9.jpg", "DSC_10.jpg", "dsc_100.jpg"), vm.state.value.items.map { it.id })
        assertEquals("DSC_9.jpg", vm.state.value.current!!.id)
    }
}
