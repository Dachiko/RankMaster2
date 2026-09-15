package com.rankmaster2.phone.rank

import androidx.compose.runtime.saveable.SaverScope
import com.rankmaster2.phone.SessionSaver
import com.rankmaster2.phone.net.Pair as Rm2Pair
import com.rankmaster2.phone.net.Snapshot
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The open folder, across an activity that was thrown away and rebuilt.
 *
 * Two things do that and the owner did neither: changing the system font or display size, and
 * Android reclaiming the app while it was in the background - which during a long session in a
 * video folder is ordinary. Before this, either one dropped him back at the folder list with the PC
 * still holding the folder open, and he had to go and find it again.
 *
 * This is the one piece of that which can be tested without a phone: that a snapshot survives the
 * round trip through a saved-instance bundle at all, and that it does not take the prefetch hints
 * with it. What cannot be tested here is the process death itself - see the report.
 */
class SessionSurvivalTest {

    private val scope = SaverScope { true }

    private fun save(snapshot: Snapshot?): String? = with(SessionSaver) { scope.save(snapshot) }

    @Test
    fun `the open session survives the round trip intact`() {
        val open = RankFixtures.ranking(pairSeq = 12, token = "token-12")

        val stored = save(open)
        assertNotNull("nothing saved means the folder is lost on every recreation", stored)
        val restored = SessionSaver.restore(stored!!)

        assertEquals(open.sessionId, restored?.sessionId)
        assertEquals(open.folder, restored?.folder)
        assertEquals(open.pairToken, restored?.pairToken)
        assertEquals(open.pairSeq, restored?.pairSeq)
        assertEquals(open.pair?.left?.id, restored?.pair?.left?.id)
        assertEquals(open.pair?.right?.links?.still, restored?.pair?.right?.links?.still)
    }

    @Test
    fun `the prefetch hints are not carried through the bundle`() {
        // § 9.5: warm pairs are hints, they may be stale, and the next response brings a fresh set.
        // A saved-instance bundle has a hard size limit and this is the one field that grows.
        val warm = RankFixtures.ranking().let { it.copy(warmPairs = List(2) { _ -> it.pair!! }) }

        val restored = SessionSaver.restore(save(warm)!!)

        assertTrue(restored!!.warmPairs.isEmpty())
        // And the current pair - the one thing that must survive - is untouched.
        assertEquals(warm.pair?.left?.id, restored.pair?.left?.id)
    }

    @Test
    fun `nothing open saves nothing`() {
        assertNull(save(null))
    }

    @Test
    fun `an unreadable bundle is no session rather than a crash`() {
        // An older build's format, or a truncated bundle. Landing on the folder list is the
        // outcome this whole thing exists to avoid, and it is still far better than not starting.
        assertNull(SessionSaver.restore("not json"))
        assertNull(SessionSaver.restore("{}"))
    }

    @Test
    fun `an exhausted folder round-trips too, so the end of one is not mistaken for a crash`() {
        val restored = SessionSaver.restore(save(RankFixtures.exhausted())!!)

        assertEquals("exhausted", restored?.state)
        assertNull(restored?.pair)
        assertNull(restored?.pairToken)
    }

    @Test
    fun `a pair with two sides is exactly what comes back`() {
        val open = RankFixtures.ranking()
        val restored = SessionSaver.restore(save(open)!!)
        val pair: Rm2Pair = restored!!.pair!!

        assertEquals("alpha.jpg", pair.left.id)
        assertEquals("bravo.jpg", pair.right.id)
    }
}
