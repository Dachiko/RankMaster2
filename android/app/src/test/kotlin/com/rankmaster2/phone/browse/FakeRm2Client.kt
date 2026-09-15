package com.rankmaster2.phone.browse

import com.rankmaster2.phone.net.Browse
import com.rankmaster2.phone.net.BrowseEntry
import com.rankmaster2.phone.net.Counts
import com.rankmaster2.phone.net.PairedDevice
import com.rankmaster2.phone.net.Ping
import com.rankmaster2.phone.net.Rm2Client
import com.rankmaster2.phone.net.Rm2Result
import com.rankmaster2.phone.net.Roots
import com.rankmaster2.phone.net.Snapshot
import kotlinx.coroutines.CompletableDeferred

/**
 * A stand-in server.
 *
 * It answers from tables the test fills in, and it records what it was asked - which is how the
 * "names first, counts second" rule gets asserted: the test can see both calls, in order, with
 * their `counts` flag.
 *
 * Nothing here is a mocking framework on purpose. The interesting part of these tests is *which
 * call happened when*, and a hand-written fake makes that an ordinary list you can assert on.
 */
class FakeRm2Client : Rm2Client {

    override val baseUrl: String = "https://pc.test:18611/api/v1"

    /** Every `browse` that was made, in order: the path and the `counts` flag it carried. */
    val browseCalls = mutableListOf<Pair<String, Boolean>>()
    val openCalls = mutableListOf<String>()
    var closeCalls = 0
        private set

    var rootsResult: Rm2Result<Roots> = Rm2Result.Ok(Roots(emptyList()))

    /** Keyed by path, then by the `counts` flag. */
    val listings = mutableMapOf<Pair<String, Boolean>, Rm2Result<Browse>>()

    var openResult: (String) -> Rm2Result<Snapshot> = { Rm2Result.Ok(snapshot(it)) }
    var closeResult: Rm2Result<Unit> = Rm2Result.Ok(Unit)

    /**
     * A `counts=true` call for a path with a gate here parks until the test completes it. That is
     * the whole mechanism behind the "counts arrive after the names" tests: it makes the slow
     * second request of § 10.15 slow on demand, and per path, instead of hoping for a race.
     */
    val countsGates = mutableMapOf<String, CompletableDeferred<Unit>>()

    fun gate(path: String): CompletableDeferred<Unit> =
        countsGates.getOrPut(path) { CompletableDeferred() }

    override suspend fun ping(): Rm2Result<Ping> = notUsed()
    override suspend fun pair(code: String, deviceName: String): Rm2Result<PairedDevice> = notUsed()
    override suspend fun revoke(deviceId: String): Rm2Result<Unit> = notUsed()

    override suspend fun roots(): Rm2Result<Roots> = rootsResult

    override suspend fun browse(path: String, counts: Boolean): Rm2Result<Browse> {
        browseCalls += path to counts
        if (counts) countsGates[path]?.await()
        return listings[path to counts]
            ?: Rm2Result.Refused(404, "folder_not_found", "no listing stubbed for $path (counts=$counts)")
    }

    override suspend fun openSession(folder: String): Rm2Result<Snapshot> {
        openCalls += folder
        return openResult(folder)
    }

    override suspend fun session(): Rm2Result<Snapshot> = notUsed()

    override suspend fun closeSession(): Rm2Result<Unit> {
        closeCalls++
        return closeResult
    }

    override suspend fun save(): Rm2Result<Snapshot> = notUsed()
    override suspend fun vote(pairToken: String, winner: String, clientRequestId: String): Rm2Result<Snapshot> = notUsed()
    override suspend fun skip(pairToken: String, clientRequestId: String): Rm2Result<Snapshot> = notUsed()
    override suspend fun discard(pairToken: String, side: String, clientRequestId: String): Rm2Result<Snapshot> = notUsed()
    override suspend fun special(pairToken: String, side: String, clientRequestId: String): Rm2Result<Snapshot> = notUsed()
    override suspend fun cancel(clientRequestId: String): Rm2Result<Snapshot> = notUsed()

    override fun url(link: String): String = baseUrl + link

    private fun <T> notUsed(): Rm2Result<T> =
        error("the folder browser must not call this endpoint")
}

// -- builders ------------------------------------------------------------------------------------

/** A child folder whose counts were never asked for: § 10.15's `counts=false` shape. */
fun uncounted(name: String, path: String, hasDatabase: Boolean = false) = BrowseEntry(
    name = name,
    path = path,
    stillCount = null,
    videoCount = null,
    rankable = null,
    hasDatabase = hasDatabase,
    accessible = true,
)

/** A child that was counted. `stills = 0, videos = 0` is "counted and empty", not "unknown". */
fun counted(
    name: String,
    path: String,
    stills: Int,
    videos: Int,
    rankable: Boolean = stills >= 2 || (stills == 0 && videos >= 2),
    hasDatabase: Boolean = false,
) = BrowseEntry(
    name = name,
    path = path,
    stillCount = stills,
    videoCount = videos,
    rankable = rankable,
    hasDatabase = hasDatabase,
    accessible = true,
)

/** § 10.15: a child that could not be enumerated - `accessible: false` and **null** counts. */
fun unreadable(name: String, path: String) = BrowseEntry(
    name = name,
    path = path,
    stillCount = null,
    videoCount = null,
    rankable = null,
    hasDatabase = false,
    accessible = false,
)

fun listing(path: String, parent: String?, vararg entries: BrowseEntry) =
    Rm2Result.Ok(Browse(path = path, parent = parent, entries = entries.toList()))

fun snapshot(folder: String, folderName: String = folder) = Snapshot(
    sessionId = "s1",
    state = "ranking",
    folder = folder,
    folderName = folderName,
    policy = "stills",
    openedAt = "2026-01-01T00:00:00Z",
    prefetchPairs = 2,
    sessionVotes = 0,
    counts = Counts(total = 4, rankable = 4, unranked = 4, stills = 4, videos = 0),
    progress = 0.0,
    progressPercent = 0,
    cues = emptyList(),
    pair = null,
    pairToken = null,
    pairSeq = 0,
    warmPairs = emptyList(),
    undoAvailable = false,
    lastAction = null,
    lastSavedAt = null,
)
