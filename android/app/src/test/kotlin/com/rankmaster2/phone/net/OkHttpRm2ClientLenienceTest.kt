package com.rankmaster2.phone.net

import kotlinx.coroutines.test.runTest
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

/**
 * § 5: "New codes MAY be added." § 2: unknown JSON fields are ignored.
 *
 * The client is on the phone and the server is on the PC, and they are updated by hand, separately,
 * by one person. A client that refuses a snapshot because version 1.2 of the server put a field in
 * it that version 1.1 did not is a client that stops working on the day the PC is upgraded, for a
 * field it never wanted to read.
 */
class OkHttpRm2ClientLenienceTest {

    private lateinit var rm2: Rm2TestServer

    @Before fun setUp() { rm2 = Rm2TestServer() }

    @After fun tearDown() { rm2.close() }

    @Test
    fun `a snapshot with fields from a later server still parses`() = runTest {
        rm2.enqueue(
            200,
            """
            {
              "sessionId": "Qv8kZ2r5tN0pXbA1cD3eFg",
              "state": "ranking",
              "folder": "D:\\photos\\trip",
              "folderName": "trip",
              "policy": "still",
              "openedAt": "2026-09-12T17:40:02.000Z",
              "prefetchPairs": 2,
              "sessionVotes": 13,
              "counts": { "total": 2, "rankable": 2, "unranked": 0, "stills": 2, "videos": 0,
                          "archived": 7 },
              "progress": 0.5,
              "progressPercent": 50,
              "cues": ["confirmation"],
              "pair": {
                "left": { "id": "a.jpg", "kind": "still", "sizeBytes": 1, "mediaVersion": "0000000000000000",
                          "links": { "meta": "/api/v1/media/a.jpg/meta",
                                     "still": "/api/v1/media/a.jpg/still?v=0000000000000000",
                                     "thumb": "/api/v1/media/a.jpg/thumb?v=0000000000000000",
                                     "video": null, "preview": "/api/v1/media/a.jpg/preview" },
                          "capturedAt": "2026-01-01T00:00:00Z" },
                "right": { "id": "b.jpg", "kind": "still", "sizeBytes": 2, "mediaVersion": "1111111111111111",
                           "links": { "meta": "/api/v1/media/b.jpg/meta",
                                      "still": "/api/v1/media/b.jpg/still?v=1111111111111111",
                                      "thumb": "/api/v1/media/b.jpg/thumb?v=1111111111111111",
                                      "video": null } },
                "hint": "adjacent"
              },
              "pairToken": "M0xQrT8vB3nJ7yE2sW9uHk",
              "pairSeq": 3,
              "warmPairs": [],
              "undoAvailable": false,
              "lastAction": { "seq": 3, "type": "skip", "pairToken": "old", "clientRequestId": null,
                              "winner": null, "side": null, "id": null, "restoredId": null,
                              "undoneType": null, "at": "2026-09-12T18:04:11.400Z",
                              "durationMs": 412 },
              "lastSavedAt": null,
              "serverBuild": "1.2.0",
              "experimental": { "anything": [1, 2, 3] }
            }
            """,
        )

        val snapshot = (rm2.client.session() as Rm2Result.Ok).value

        assertEquals("Qv8kZ2r5tN0pXbA1cD3eFg", snapshot.sessionId)
        assertEquals("M0xQrT8vB3nJ7yE2sW9uHk", snapshot.pairToken)
        assertEquals(3L, snapshot.pairSeq)
        assertEquals(2, snapshot.counts.stills)
        assertEquals("a.jpg", snapshot.pair!!.left.id)
        assertEquals("skip", snapshot.lastAction!!.type)
        assertNull(snapshot.lastSavedAt)
    }

    @Test
    fun `an error code this client has never heard of still arrives as that code`() = runTest {
        rm2.enqueue(
            409,
            """
            {"error":{"code":"folder_went_read_only","message":"The folder became read-only.",
              "requestId":"rq-9","details":{"folder":"D:\\photos\\trip"},"hint":"remount"}}
            """,
        )

        val refused = rm2.client.vote("t", Sides.LEFT, "req-1") as Rm2Result.Refused

        // Passed through, not flattened to "unknown": § 4 says branch on the code, and a client
        // that erases codes it does not know makes that impossible to do later.
        assertEquals("folder_went_read_only", refused.code)
        assertEquals(409, refused.status)
        assertEquals("rq-9", refused.requestId)
    }

    @Test
    fun `a snapshot embedded in a refusal is just as lenient`() = runTest {
        rm2.enqueue(
            409,
            """
            {"error":{"code":"stale_pair_token","message":"Stale.","requestId":"rq-1",
              "details":{"suppliedToken":"old","currentToken":"M0xQrT8vB3nJ7yE2sW9uHk",
                         "newFieldHere":true},
              "session":$SNAPSHOT_WITH_EXTRAS}}
            """,
        )

        val refused = rm2.client.vote("old", Sides.LEFT, "req-1") as Rm2Result.Refused

        assertEquals(ErrorCodes.STALE_PAIR_TOKEN, refused.code)
        assertEquals("M0xQrT8vB3nJ7yE2sW9uHk", refused.session!!.pairToken)
        assertEquals(42L, refused.session!!.pairSeq)
    }

    @Test
    fun `a MediaRef whose file vanished under the session parses as missing`() = runTest {
        rm2.enqueue(200, SNAPSHOT_WITH_MISSING_FILE)

        val snapshot = (rm2.client.session() as Rm2Result.Ok).value

        // § 11.3: a null size is the file having gone, not a parse failure.
        assertTrue(snapshot.pair!!.right.isMissing)
        assertNull(snapshot.pair!!.right.mediaVersion)
        assertNull(snapshot.pair!!.right.links.still)
        assertTrue(!snapshot.pair!!.left.isMissing)
    }
}

private const val SNAPSHOT_WITH_EXTRAS: String = """
{
  "sessionId": "Qv8kZ2r5tN0pXbA1cD3eFg",
  "state": "ranking",
  "folder": "D:\\photos\\trip",
  "folderName": "trip",
  "policy": "still",
  "openedAt": "2026-09-12T17:40:02.000Z",
  "prefetchPairs": 2,
  "sessionVotes": 13,
  "counts": { "total": 2, "rankable": 2, "unranked": 0, "stills": 2, "videos": 0 },
  "progress": 0.5,
  "progressPercent": 50,
  "cues": [],
  "pair": {
    "left": { "id": "a.jpg", "kind": "still", "sizeBytes": 1, "mediaVersion": "0000000000000000",
              "links": { "meta": "/api/v1/media/a.jpg/meta" } },
    "right": { "id": "b.jpg", "kind": "still", "sizeBytes": 2, "mediaVersion": "1111111111111111",
               "links": { "meta": "/api/v1/media/b.jpg/meta" } }
  },
  "pairToken": "M0xQrT8vB3nJ7yE2sW9uHk",
  "pairSeq": 42,
  "warmPairs": [],
  "undoAvailable": false,
  "lastAction": null,
  "lastSavedAt": null,
  "aFieldFromTheFuture": { "nested": true }
}
"""

private const val SNAPSHOT_WITH_MISSING_FILE: String = """
{
  "sessionId": "Qv8kZ2r5tN0pXbA1cD3eFg",
  "state": "ranking",
  "folder": "D:\\photos\\trip",
  "folderName": "trip",
  "policy": "still",
  "openedAt": "2026-09-12T17:40:02.000Z",
  "prefetchPairs": 2,
  "sessionVotes": 0,
  "counts": { "total": 2, "rankable": 2, "unranked": 2, "stills": 2, "videos": 0 },
  "progress": 0.0,
  "progressPercent": 0,
  "cues": [],
  "pair": {
    "left": { "id": "a.jpg", "kind": "still", "sizeBytes": 1, "mediaVersion": "0000000000000000",
              "links": { "meta": "/api/v1/media/a.jpg/meta",
                         "still": "/api/v1/media/a.jpg/still?v=0000000000000000",
                         "thumb": "/api/v1/media/a.jpg/thumb?v=0000000000000000",
                         "video": null } },
    "right": { "id": "gone.jpg", "kind": "still", "sizeBytes": null, "mediaVersion": null,
               "links": { "meta": "/api/v1/media/gone.jpg/meta",
                          "still": null, "thumb": null, "video": null } }
  },
  "pairToken": "M0xQrT8vB3nJ7yE2sW9uHk",
  "pairSeq": 0,
  "warmPairs": [],
  "undoAvailable": false,
  "lastAction": null,
  "lastSavedAt": null
}
"""
