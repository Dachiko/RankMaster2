package com.rankmaster2.phone.review

import com.rankmaster2.phone.net.Browse
import com.rankmaster2.phone.net.Items
import com.rankmaster2.phone.net.LastAction
import com.rankmaster2.phone.net.Links
import com.rankmaster2.phone.net.MediaRef
import com.rankmaster2.phone.net.PairedDevice
import com.rankmaster2.phone.net.Ping
import com.rankmaster2.phone.net.Rm2Client
import com.rankmaster2.phone.net.Rm2Result
import com.rankmaster2.phone.net.Roots
import com.rankmaster2.phone.net.Snapshot
import com.rankmaster2.phone.rank.RankFixtures
import kotlinx.coroutines.CompletableDeferred

/** One discard as it went out: the file and the id that makes a resend recognisable. */
data class SentDiscard(val id: String, val requestId: String)

/**
 * A `Rm2Client` for the review screen: records what was asked, in order, and answers from queues.
 *
 * As with the ranking fake, an unqueued call is a test bug rather than a canned success - except
 * `items()`, which falls back to [listing] because a refresh after a failure is not something every
 * test wants to spell out.
 */
class FakeReviewClient : Rm2Client {

    override val baseUrl = "https://127.0.0.1:18611/api/v1"

    /** Every server call in the order it happened, for the "waits for what came before" tests. */
    val log = mutableListOf<String>()

    val discards = mutableListOf<SentDiscard>()
    val cancels = mutableListOf<String>()

    /** Every review position written, in order; null is the "finished" write. */
    val positions = mutableListOf<String?>()
    var itemsReads = 0
        private set
    var closed = false
        private set

    val itemsResults = ArrayDeque<Rm2Result<Items>>()
    val discardResults = ArrayDeque<Rm2Result<Snapshot>>()
    val cancelResults = ArrayDeque<Rm2Result<Snapshot>>()

    /** Answers for position writes; an empty queue answers Ok. */
    val positionResults = ArrayDeque<Rm2Result<Unit>>()

    /** When set, `setReviewPosition` parks until the test completes it - a PC that does not answer. */
    var positionGate: CompletableDeferred<Unit>? = null

    /** What `items()` answers when nothing is queued. */
    var listing: Rm2Result<Items>? = null

    /** When set, `discardItem` parks until the test completes it - a slow PC, on demand. */
    var discardGate: CompletableDeferred<Unit>? = null

    override suspend fun items(): Rm2Result<Items> {
        itemsReads++
        log += "items"
        return itemsResults.removeFirstOrNull() ?: listing ?: error("unqueued items")
    }

    override suspend fun discardItem(id: String, clientRequestId: String): Rm2Result<Snapshot> {
        discards += SentDiscard(id, clientRequestId)
        log += "discard:$id"
        discardGate?.await()
        return discardResults.removeFirstOrNull() ?: error("unqueued discardItem")
    }

    override suspend fun cancel(clientRequestId: String): Rm2Result<Snapshot> {
        cancels += clientRequestId
        log += "cancel"
        return cancelResults.removeFirstOrNull() ?: error("unqueued cancel")
    }

    override suspend fun setReviewPosition(id: String?): Rm2Result<Unit> {
        log += "position:$id"
        positionGate?.await()
        positions += id
        return positionResults.removeFirstOrNull() ?: Rm2Result.Ok(Unit)
    }

    override suspend fun closeSession(): Rm2Result<Unit> {
        closed = true
        log += "close"
        return Rm2Result.Ok(Unit)
    }

    // -- not used by the review screen -------------------------------------------------------------

    override suspend fun ping(): Rm2Result<Ping> = error("not used")
    override suspend fun pair(code: String, deviceName: String): Rm2Result<PairedDevice> = error("not used")
    override suspend fun revoke(deviceId: String): Rm2Result<Unit> = error("not used")
    override suspend fun roots(): Rm2Result<Roots> = error("not used")
    override suspend fun browse(path: String, counts: Boolean): Rm2Result<Browse> = error("not used")
    override suspend fun openSession(folder: String): Rm2Result<Snapshot> = error("not used")
    override suspend fun session(): Rm2Result<Snapshot> = error("not used")
    override suspend fun save(): Rm2Result<Snapshot> = error("not used")
    override suspend fun vote(pairToken: String, winner: String, clientRequestId: String): Rm2Result<Snapshot> = error("not used")
    override suspend fun skip(pairToken: String, clientRequestId: String): Rm2Result<Snapshot> = error("not used")
    override suspend fun discard(pairToken: String, side: String, clientRequestId: String): Rm2Result<Snapshot> = error("not used")
    override suspend fun special(pairToken: String, side: String, clientRequestId: String): Rm2Result<Snapshot> = error("not used")
    override fun url(link: String): String = baseUrl.removeSuffix("/api/v1") + link
}

/** Snapshots, items and answers shaped like the real thing. */
object ReviewFixtures {

    const val FOLDER = "D:\\Photos"

    fun ref(id: String, kind: String = "still") = MediaRef(
        id = id,
        kind = kind,
        sizeBytes = 1234,
        mediaVersion = "abcdef0123456789",
        links = Links(
            meta = "/api/v1/media/$id/meta",
            still = if (kind == "still") "/api/v1/media/$id/still?v=abcdef0123456789" else null,
            thumb = null,
            video = if (kind == "video") "/api/v1/media/$id/video?v=abcdef0123456789" else null,
        ),
    )

    fun snapshot(undoAvailable: Boolean = false, lastAction: LastAction? = null): Snapshot =
        RankFixtures.ranking(undoAvailable = undoAvailable, lastAction = lastAction)

    fun items(
        vararg ids: String,
        session: Snapshot = snapshot(),
        reviewPosition: String? = null,
    ): Rm2Result<Items> =
        Rm2Result.Ok(Items(session, ids.map { ref(it) }, reviewPosition))

    private fun action(type: String, id: String?, requestId: String?, restoredId: String? = null, undone: String? = null) =
        LastAction(
            seq = 1,
            type = type,
            pairToken = null,
            clientRequestId = requestId,
            winner = null,
            side = null,
            id = id,
            restoredId = restoredId,
            undoneType = undone,
            at = "2026-09-15T10:01:00.000Z",
        )

    /** The snapshot a successful `POST /session/items/discard` answers. */
    fun discarded(id: String, requestId: String): Rm2Result<Snapshot> =
        Rm2Result.Ok(snapshot(undoAvailable = true, lastAction = action("discard", id, requestId)))

    /** The same, for a file that had already left the folder: dropped, and nothing to undo. */
    fun dropped(id: String, requestId: String): Rm2Result<Snapshot> =
        Rm2Result.Ok(snapshot(undoAvailable = false, lastAction = action("drop_missing", id, requestId)))

    /** The snapshot a successful `POST /session/undo` answers after a discard. */
    fun undone(id: String, restoredId: String = id): Rm2Result<Snapshot> =
        Rm2Result.Ok(snapshot(undoAvailable = false, lastAction = action("undo", id, "undo-req", restoredId, "discard")))

    fun refused(status: Int, code: String, message: String = "no") = Rm2Result.Refused(status, code, message)

    fun unreachable() = Rm2Result.Unreachable(null, "timeout")
}
