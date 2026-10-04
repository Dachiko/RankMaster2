package com.rankmaster2.phone.review

import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.unit.Constraints
import com.rankmaster2.phone.media.MediaPrefetcher
import com.rankmaster2.phone.media.MediaUrls
import com.rankmaster2.phone.media.MediaWidths
import com.rankmaster2.phone.media.StillFormat
import com.rankmaster2.phone.media.paneLongEdgePx
import com.rankmaster2.phone.net.MediaRef
import com.rankmaster2.phone.ui.VotingShareOfScreen
import com.rankmaster2.phone.ui.insideLiveArea
import com.rankmaster2.phone.ui.rank.votableSideAt
import com.rankmaster2.phone.ui.review.COMMIT_FRACTION
import com.rankmaster2.phone.ui.review.PREFETCH_AHEAD
import com.rankmaster2.phone.ui.review.ReviewAction
import com.rankmaster2.phone.ui.review.decidesDiscard
import com.rankmaster2.phone.ui.review.dragOffset
import com.rankmaster2.phone.ui.review.reviewPrefetchPx
import com.rankmaster2.phone.ui.review.swipeAction
import com.rankmaster2.phone.ui.review.tapAction
import okhttp3.OkHttpClient
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * What a touch means on the review screen, and that the screen's cache lines up with its pane.
 *
 * No screen is needed for any of it: the rules are plain arithmetic on where a finger went down and
 * how far and how fast it went.
 */
class ReviewGesturesTest {

    // A 1080 x 2400 phone, in pixels.
    private val w = 1080f
    private val h = 2400f
    private val flingPx = 900f * 3f
    private val centre = Offset(w / 2f, h / 2f)

    // -- the shared live rectangle ------------------------------------------------------------------

    @Test
    fun `the live rectangle is the share of the screen the ranking screen uses`() {
        var live = 0
        var total = 0
        var y = 0.5f
        while (y < h) {
            var x = 0.5f
            while (x < w) {
                total++
                if (insideLiveArea(Offset(x, y), w, h)) live++
                x += 6f
            }
            y += 6f
        }
        assertEquals(VotingShareOfScreen.toDouble(), live.toDouble() / total, 0.01)
    }

    @Test
    fun `the helper and the ranking screen's vote test agree everywhere`() {
        for (x in listOf(0f, 100f, 300f, 540f, 800f, 1079f)) {
            for (y in listOf(0f, 200f, 800f, 1210f, 1900f, 2399f)) {
                val at = Offset(x, y)
                assertEquals(
                    "at $at",
                    insideLiveArea(at, w, h),
                    votableSideAt(at, portrait = true, w, h) != null,
                )
            }
        }
    }

    @Test
    fun `the middle is live and every edge is not`() {
        assertTrue(insideLiveArea(centre, w, h))
        assertFalse(insideLiveArea(Offset(2f, h / 2f), w, h))
        assertFalse(insideLiveArea(Offset(w - 2f, h / 2f), w, h))
        assertFalse(insideLiveArea(Offset(w / 2f, 2f), w, h))
        assertFalse(insideLiveArea(Offset(w / 2f, h - 2f), w, h))
        assertFalse(insideLiveArea(Offset(0f, 0f), w, h))
    }

    @Test
    fun `a wider share is a bigger rectangle`() {
        val at = Offset(w * 0.2f, h * 0.2f)
        assertFalse(insideLiveArea(at, w, h, share = 0.30f))
        assertTrue(insideLiveArea(at, w, h, share = 0.90f))
    }

    @Test
    fun `the margins are the same fraction of each side`() {
        var left = 0f
        while (!insideLiveArea(Offset(left, h / 2f), w, h)) left += 1f
        var top = 0f
        while (!insideLiveArea(Offset(w / 2f, top), w, h)) top += 1f
        assertEquals(left / w, top / h, 0.002f)
    }

    // -- tap: keep -------------------------------------------------------------------------------------

    @Test
    fun `a tap in the middle keeps`() {
        assertEquals(ReviewAction.KEEP, tapAction(centre, w, h))
    }

    @Test
    fun `a tap that lands near an edge does nothing`() {
        listOf(Offset(10f, h / 2f), Offset(w - 10f, h / 2f), Offset(w / 2f, 10f), Offset(w / 2f, h - 10f)).forEach {
            assertEquals("at $it", ReviewAction.NONE, tapAction(it, w, h))
        }
    }

    // -- swipe left: discard ----------------------------------------------------------------------------

    @Test
    fun `a swipe left past the threshold discards`() {
        val dragged = -w * COMMIT_FRACTION - 1f
        assertEquals(ReviewAction.DISCARD, swipeAction(centre, dragged, 0f, w, h, flingPx))
    }

    @Test
    fun `a short swipe left springs back`() {
        assertEquals(ReviewAction.NONE, swipeAction(centre, -w * 0.1f, -100f, w, h, flingPx))
    }

    @Test
    fun `a flick to the left discards whatever the distance`() {
        assertEquals(ReviewAction.DISCARD, swipeAction(centre, -40f, -flingPx - 1f, w, h, flingPx))
    }

    @Test
    fun `a swipe right does nothing, however far or fast`() {
        assertEquals(ReviewAction.NONE, swipeAction(centre, w * 0.6f, 0f, w, h, flingPx))
        assertEquals(ReviewAction.NONE, swipeAction(centre, w * 0.1f, flingPx * 3, w, h, flingPx))
        assertFalse(decidesDiscard(w, flingPx * 3, w, flingPx))
    }

    @Test
    fun `a flick left from the right of where it started does nothing`() {
        assertEquals(ReviewAction.NONE, swipeAction(centre, 120f, -flingPx - 1f, w, h, flingPx))
    }

    @Test
    fun `a swipe that began near an edge does nothing`() {
        assertEquals(ReviewAction.NONE, swipeAction(Offset(8f, h / 2f), -w, -flingPx * 2, w, h, flingPx))
        assertEquals(ReviewAction.NONE, swipeAction(Offset(w - 8f, h / 2f), -w, -flingPx * 2, w, h, flingPx))
    }

    @Test
    fun `the item only follows the finger to the left, and starts from rest`() {
        val slop = 24f
        assertEquals(0f, dragOffset(-slop, slop), 0.001f)
        assertEquals(-76f, dragOffset(-100f, slop), 0.001f)
        assertEquals(0f, dragOffset(300f, slop), 0.001f)
        assertEquals(0f, dragOffset(0f, slop), 0.001f)
    }

    // -- the cache -------------------------------------------------------------------------------------------

    private val base = "https://192.168.1.5:18611/api/v1"
    private val prefetcher = MediaPrefetcher(OkHttpClient(), base)

    /** The URL the item's own pane asks for: the width for its measured long edge, on the server's link. */
    private fun paneUrl(ref: MediaRef, c: Constraints): String =
        MediaUrls.resolve(
            base,
            MediaUrls.still(ref.links.still!!, MediaWidths.forPane(paneLongEdgePx(c)), StillFormat.WEBP),
        )

    @Test
    fun `the prefetch asks for exactly the URL the full-screen pane will ask for`() {
        val refs = (1..PREFETCH_AHEAD).map { ReviewFixtures.ref("IMG_000$it.jpg") }
        // Screens on, just under and just over the steps of the server's width list, both ways up.
        val screens = listOf(
            1080 to 2400, 1079 to 2339, 1081 to 2400, 1440 to 3120, 720 to 1280, 2400 to 1080,
            1080 to 1440, 1080 to 1441, 360 to 640, 3000 to 1440,
        )
        for ((sw, sh) in screens) {
            val c = Constraints.fixed(sw, sh)
            val warmed = prefetcher.urlsForItems(refs, reviewPrefetchPx(c))
            assertEquals("${sw}x$sh", refs.map { paneUrl(it, c) }, warmed)
        }
    }

    @Test
    fun `the prefetch skips videos and vanished files, which a pane does not fetch as stills`() {
        val refs = listOf(
            ReviewFixtures.ref("a.jpg"),
            ReviewFixtures.ref("b.mp4", kind = "video"),
            ReviewFixtures.ref("c.jpg").copy(sizeBytes = null),
        )

        val warmed = prefetcher.urlsForItems(refs, reviewPrefetchPx(Constraints.fixed(1080, 2400)))

        assertEquals(1, warmed.size)
        assertTrue(warmed.single().contains("/a.jpg/"))
    }

    @Test
    fun `three items are warmed ahead`() {
        assertEquals(3, PREFETCH_AHEAD)
    }

    @Test
    fun `the prefetch width is the screen's long edge, whichever way up`() {
        assertEquals(2400, reviewPrefetchPx(Constraints.fixed(1080, 2400)))
        assertEquals(2400, reviewPrefetchPx(Constraints.fixed(2400, 1080)))
    }
}
