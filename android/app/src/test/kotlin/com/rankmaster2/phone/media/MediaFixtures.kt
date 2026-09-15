package com.rankmaster2.phone.media

import com.rankmaster2.phone.net.Counts
import com.rankmaster2.phone.net.Links
import com.rankmaster2.phone.net.MediaRef
import com.rankmaster2.phone.net.Snapshot
import java.util.Base64
import okhttp3.mockwebserver.MockResponse
import com.rankmaster2.phone.net.Pair as MediaPair

/** Snapshots and refs shaped like the server's, for tests that need something to point at. */
object MediaFixtures {

    /** A real 16x16 PNG, so a decoder has something it can actually decode. */
    val PNG: ByteArray = Base64.getDecoder().decode(
        "iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAIAAACQkWg2AAABlklEQVR42g3LQQEAIQgAQRvQwAY0sIEN" +
            "aEADnvuzAQ1sYAMb0MAmd/Of1hrS6A1tjMZsWMMb0ViNbOzGadxGNV6jNUGELqgwhCmY4EIIS0hh" +
            "C0e4QglP/tCRTu9oZ3Rmxzreic7qZGd3Tud2qvP6HxRRuqLKUKZiiiuhLCWVrRzlKqU8/cNABn2g" +
            "gzGYAxv4IAZrkIM9OIM7qMEbf5jIpE90MiZzYhOfxGRNcrInZ3InNXnzD4YY3VBjGNMww40wlpHG" +
            "No5xjTKe/cERpzvqDGc65rgTznLS2c5xrlPO8z8EEvRAgxHMwAIPIlhBBjs4wQ0qePGHhSz6Qhdj" +
            "MRe28EUs1iIXe3EWd1GLt/6QSNITTUYyE0s8iWQlmezkJDep5OUfNrLpG92MzdzYxjexWZvc7M3Z" +
            "3E1t3v7DQQ79oIdxmAc7+CEO65CHfTiHe6jDO3+4yKVf9DIu82IXv8RlXfKyL+dyL3V59w+FFL3Q" +
            "YhSzsMKLKFaRxS5OcYsqXv3hIY/+0Md4zIc9/BGP9cjHfpzHfdTjPT7YZWEQt5heBwAAAABJRU5E" +
            "rkJggg=="
    )

    /** What § 12.5 says a byte response carries. The whole caching story is in these two headers. */
    fun stillResponse(etag: String = "\"s1080w-9f2a1c77b0e4d3105ab8c1d2e3f40506\""): MockResponse =
        MockResponse()
            .setResponseCode(200)
            .setHeader("Content-Type", "image/webp")
            .setHeader("ETag", etag)
            .setHeader("Cache-Control", "private, max-age=31536000, immutable")
            .setBody(okio.Buffer().write(PNG))

    /** The § 4 envelope, which is what a client is required to branch on. */
    fun errorResponse(status: Int, code: String, message: String = "no"): MockResponse =
        MockResponse()
            .setResponseCode(status)
            .setHeader("Content-Type", "application/json")
            .setBody("""{"error":{"code":"$code","message":"$message","requestId":"r1"}}""")

    fun still(id: String, encoded: String = id, version: String = "9f2a1c77b0e4d310"): MediaRef =
        MediaRef(
            id = id,
            kind = "still",
            sizeBytes = 4210332,
            mediaVersion = version,
            links = Links(
                meta = "/api/v1/media/$encoded/meta",
                still = "/api/v1/media/$encoded/still?v=$version",
                thumb = "/api/v1/media/$encoded/thumb?v=$version",
                video = null,
            ),
        )

    fun video(id: String, encoded: String = id, version: String = "1122334455667788"): MediaRef =
        MediaRef(
            id = id,
            kind = "video",
            sizeBytes = 99000000,
            mediaVersion = version,
            links = Links(
                meta = "/api/v1/media/$encoded/meta",
                still = null,
                thumb = null,
                video = "/api/v1/media/$encoded/video?v=$version",
            ),
        )

    /** § 9.3: a ref whose file has gone from the folder under the session. */
    fun gone(id: String, encoded: String = id): MediaRef =
        still(id, encoded).copy(sizeBytes = null, mediaVersion = null)

    fun pair(left: MediaRef, right: MediaRef) = MediaPair(left, right)

    fun snapshot(
        pair: MediaPair? = null,
        warmPairs: List<MediaPair> = emptyList(),
    ): Snapshot = Snapshot(
        sessionId = "AAAAAAAAAAAAAAAAAAAAAA",
        state = if (pair == null) "exhausted" else "ranking",
        folder = "/home/misha/photos",
        folderName = "photos",
        policy = "still",
        openedAt = "2026-09-15T10:00:00Z",
        prefetchPairs = 2,
        sessionVotes = 3,
        counts = Counts(total = 40, rankable = 40, unranked = 12, stills = 40, videos = 0),
        progress = 0.25,
        progressPercent = 25,
        cues = emptyList(),
        pair = pair,
        // A warm pair carries no token, and there is deliberately no way to reach one from here.
        pairToken = if (pair == null) null else "tok",
        pairSeq = 7,
        warmPairs = warmPairs,
        undoAvailable = true,
        lastAction = null,
        lastSavedAt = null,
    )
}
