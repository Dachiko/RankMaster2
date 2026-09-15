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
        return voteResults.removeFirstOrNull() ?: Rm2Result.Ok(RankFixtures.ranking())
    }

    override suspend fun skip(pairToken: String, clientRequestId: String): Rm2Result<Snapshot> {
        skips += SentAction(pairToken, clientRequestId)
        return skipResults.removeFirstOrNull() ?: Rm2Result.Ok(RankFixtures.ranking())
    }

    override suspend fun discard(pairToken: String, side: String, clientRequestId: String): Rm2Result<Snapshot> {
        discards += SentAction(pairToken, clientRequestId, side)
        return moveResults.removeFirstOrNull() ?: Rm2Result.Ok(RankFixtures.ranking())
    }

    override suspend fun special(pairToken: String, side: String, clientRequestId: String): Rm2Result<Snapshot> {
        specials += SentAction(pairToken, clientRequestId, side)
        return moveResults.removeFirstOrNull() ?: Rm2Result.Ok(RankFixtures.ranking())
    }

    override suspend fun cancel(clientRequestId: String): Rm2Result<Snapshot> {
        cancels += clientRequestId
        return cancelResults.removeFirstOrNull() ?: Rm2Result.Ok(RankFixtures.ranking())
    }

    override suspend fun save(): Rm2Result<Snapshot> =
        saveResults.removeFirstOrNull() ?: Rm2Result.Ok(RankFixtures.ranking())

    override suspend fun session(): Rm2Result<Snapshot> {
        sessionReads++
        return sessionResults.removeFirstOrNull() ?: Rm2Result.Ok(RankFixtures.ranking())
    }

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
        pair = Rm2Pair(ref("alpha.jpg"), ref("bravo.jpg")),
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
