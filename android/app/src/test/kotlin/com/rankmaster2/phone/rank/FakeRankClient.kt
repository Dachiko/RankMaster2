package com.rankmaster2.phone.rank

import com.rankmaster2.phone.net.Browse
import com.rankmaster2.phone.net.Counts
import com.rankmaster2.phone.net.LastAction
import com.rankmaster2.phone.net.Links
import com.rankmaster2.phone.net.MediaRef
import com.rankmaster2.phone.net.Pair as Rm2Pair
import com.rankmaster2.phone.net.PairedDevice
import com.rankmaster2.phone.net.Ping
import com.rankmaster2.phone.net.Rm2Client
import com.rankmaster2.phone.net.Rm2Result
import com.rankmaster2.phone.net.Roots
import com.rankmaster2.phone.net.Snapshot
import kotlinx.coroutines.CompletableDeferred

/** One call, recorded exactly as it went out. */
data class SentAction(val token: String, val requestId: String, val extra: String? = null)

/**
 * A `Rm2Client` that records what it was asked to do and answers from a queue.
 *
 * Recording the token and the request id is the whole point: the test that matters asserts that a
 * retry carried the *same* pair of values, which is what makes it one logical action rather than
 * two.
 *
 * T2b: an unqueued call is a test bug, not a green test. The old default - a canned `Ok` when the
 * queue ran dry - let a test pass while asserting nothing about what the server actually said for
 * whichever call it forgot to queue; the fix that mattered upstream (H6, A15) would have shown up as
 * the same silent green either way. So every queue-backed call now throws when it is asked for an
 * answer it was not given one for, and every test that relied on the old default queues what it
 * means instead.
 */
class FakeRankClient : Rm2Client {

    override val baseUrl = "https://127.0.0.1:18611/api/v1"

    val votes = mutableListOf<SentAction>()
    val skips = mutableListOf<SentAction>()
    val discards = mutableListOf<SentAction>()
    val specials = mutableListOf<SentAction>()
    val cancels = mutableListOf<String>()
    var sessionReads = 0
        private set

    val voteResults = ArrayDeque<Rm2Result<Snapshot>>()
    val skipResults = ArrayDeque<Rm2Result<Snapshot>>()
    val moveResults = ArrayDeque<Rm2Result<Snapshot>>()
    val cancelResults = ArrayDeque<Rm2Result<Snapshot>>()
    val saveResults = ArrayDeque<Rm2Result<Snapshot>>()
    val sessionResults = ArrayDeque<Rm2Result<Snapshot>>()

    /** When set, a vote never answers — the state stays busy, which is what a slow PC looks like. */
    var holdVote = false

    private val forever = CompletableDeferred<Rm2Result<Snapshot>>()

    override suspend fun vote(pairToken: String, winner: String, clientRequestId: String): Rm2Result<Snapshot> {
        votes += SentAction(pairToken, clientRequestId, winner)
        if (holdVote) return forever.await()
        return voteResults.removeFirstOrNull() ?: unqueued("vote")
    }

    override suspend fun skip(pairToken: String, clientRequestId: String): Rm2Result<Snapshot> {
        skips += SentAction(pairToken, clientRequestId)
        return skipResults.removeFirstOrNull() ?: unqueued("skip")
    }

    override suspend fun discard(pairToken: String, side: String, clientRequestId: String): Rm2Result<Snapshot> {
        discards += SentAction(pairToken, clientRequestId, side)
        return moveResults.removeFirstOrNull() ?: unqueued("discard")
    }

    override suspend fun special(pairToken: String, side: String, clientRequestId: String): Rm2Result<Snapshot> {
        specials += SentAction(pairToken, clientRequestId, side)
        return moveResults.removeFirstOrNull() ?: unqueued("special")
    }

    override suspend fun cancel(clientRequestId: String): Rm2Result<Snapshot> {
        cancels += clientRequestId
        return cancelResults.removeFirstOrNull() ?: unqueued("cancel")
    }

    override suspend fun save(): Rm2Result<Snapshot> =
        saveResults.removeFirstOrNull() ?: unqueued("save")

    override suspend fun session(): Rm2Result<Snapshot> {
        sessionReads++
        return sessionResults.removeFirstOrNull() ?: unqueued("session")
    }

    private fun unqueued(call: String): Nothing = throw IllegalStateException("unqueued $call")

    // -- not used by the ranking screen ------------------------------------------------------------

    override suspend fun ping(): Rm2Result<Ping> = error("not used")
    override suspend fun pair(code: String, deviceName: String): Rm2Result<PairedDevice> = error("not used")
    override suspend fun revoke(deviceId: String): Rm2Result<Unit> = error("not used")
    override suspend fun roots(): Rm2Result<Roots> = error("not used")
    override suspend fun browse(path: String, counts: Boolean): Rm2Result<Browse> = error("not used")
    override suspend fun openSession(folder: String): Rm2Result<Snapshot> = error("not used")
    var sessionClosed = false
        private set

    override suspend fun closeSession(): Rm2Result<Unit> {
        sessionClosed = true
        return Rm2Result.Ok(Unit)
    }
    override fun url(link: String): String = baseUrl.removeSuffix("/api/v1") + link
}

/** Snapshots shaped like the real thing, in the shapes the tests need. */
object RankFixtures {

    fun ranking(
        pairSeq: Long = 0,
        token: String? = "token-1",
        undoAvailable: Boolean = true,
        lastAction: LastAction? = null,
        cues: List<String> = emptyList(),
    ) = Snapshot(
        sessionId = "session-1",
        state = "ranking",
        folder = "D:\\Photos",
        folderName = "Photos",
        policy = "still",
        openedAt = "2026-09-15T10:00:00.000Z",
        prefetchPairs = 2,
        sessionVotes = 0,
        counts = Counts(total = 6, rankable = 6, unranked = 6, stills = 6, videos = 0),
        progress = 0.0,
        progressPercent = 0,
        cues = cues,
        pair = Rm2Pair(pairRef("alpha", pairSeq, token), pairRef("bravo", pairSeq, token)),
        pairToken = token,
        pairSeq = pairSeq,
        warmPairs = emptyList(),
        undoAvailable = undoAvailable,
        lastAction = lastAction,
        lastSavedAt = null,
    )

    fun exhausted() = ranking(token = null).copy(state = "exhausted", pair = null, pairToken = null)

    fun undoOf(what: String, id: String? = null, restoredId: String? = null) = LastAction(
        seq = 1,
        type = "undo",
        pairToken = null,
        clientRequestId = "req-fixed",
        winner = null,
        side = null,
        id = id,
        restoredId = restoredId,
        undoneType = what,
        at = "2026-09-15T10:01:00.000Z",
    )

    /**
     * A pair's two files, named so that two different `(pairSeq, token)` fixtures are two different
     * pictures rather than the same two filenames every time.
     *
     * The second audit's § 5: every ranking test compared `alpha.jpg` against `bravo.jpg` regardless
     * of which pair it was, so no test in the repository could observe a vote — or, far worse, a
     * discard (§ 1.3) — landing on a pair other than the one the test built. The default fixture
     * (`pairSeq = 0`, `token = "token-1"`) keeps the plain names, because plenty of existing tests
     * read "alpha.jpg" to mean "whatever the default pair is called" rather than testing identity;
     * every other `(pairSeq, token)` combination gets a name nothing else in the suite can produce,
     * so a test that captures a file name at one pair and finds it attached to a *different* pair
     * later has actually proven something.
     */
    private fun pairRef(stem: String, pairSeq: Long, token: String?): MediaRef {
        val id = if (pairSeq == 0L && token == "token-1") "$stem.jpg" else "$stem-$pairSeq-${token ?: "none"}.jpg"
        return ref(id)
    }

    private fun ref(id: String) = MediaRef(
        id = id,
        kind = "still",
        sizeBytes = 1234,
        mediaVersion = "abcdef0123456789",
        links = Links(
            meta = "/api/v1/media/$id/meta",
            still = "/api/v1/media/$id/still?v=abcdef0123456789",
            thumb = "/api/v1/media/$id/thumb?v=abcdef0123456789",
            video = null,
        ),
    )
}
