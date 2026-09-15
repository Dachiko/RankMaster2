package com.rankmaster2.phone.net

import com.rankmaster2.phone.net.impl.OkHttpRm2Client
import com.rankmaster2.phone.store.ServerIdentity
import java.security.cert.X509Certificate
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import okhttp3.OkHttpClient
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import okhttp3.mockwebserver.RecordedRequest

/**
 * A MockWebServer speaking TLS with a certificate it made up on the spot, and a client pinned to
 * exactly that certificate.
 *
 * Every test here goes over real TLS through a real [Rm2Http] client rather than over plain HTTP
 * through a hand-built one, for two reasons. The pin and the bearer token are added by the client
 * this app is required to use, so a test that supplies its own would prove the endpoints work while
 * saying nothing about whether they work *as shipped*. And the pinning tests need this setup
 * anyway, so there is no second arrangement to keep honest.
 */
class Rm2TestServer(val token: String = "rm2_test_token_0123456789") : AutoCloseable {

    private val held = SelfSignedCertificate()

    val certificate: X509Certificate get() = held.certificate

    val server: MockWebServer = MockWebServer().apply {
        useHttps(held.sslSocketFactory(), false)
        start()
    }

    val identity: ServerIdentity = ServerIdentity(
        host = server.hostName,
        port = server.port,
        certificateFingerprint = Rm2Http.fingerprintOf(held.certificate),
        token = token,
        deviceId = "dev_test",
    )

    /** The very client the app ships: pinned to [certificate], carrying [token] on every request. */
    val http: OkHttpClient = Rm2Http.client(identity)

    val client: OkHttpRm2Client = OkHttpRm2Client(identity)

    fun enqueue(status: Int, body: String, requestId: String = "01J8Z5K0QF3V8A0M6R9Q2B7T4C") {
        server.enqueue(
            MockResponse()
                .setResponseCode(status)
                .setHeader("Content-Type", "application/json; charset=utf-8")
                .setHeader("X-Request-Id", requestId)
                .setBody(body),
        )
    }

    fun enqueueNoContent(requestId: String = "01J8Z5K0QF3V8A0M6R9Q2B7T4C") {
        server.enqueue(MockResponse().setResponseCode(204).setHeader("X-Request-Id", requestId))
    }

    fun take(): RecordedRequest = server.takeRequest()

    override fun close() {
        server.shutdown()
    }
}

/** The body of a request, as JSON. Fails loudly rather than silently if it is not an object. */
fun RecordedRequest.jsonBody(): JsonObject =
    Json.parseToJsonElement(body.readUtf8()) as JsonObject

fun JsonObject.str(field: String): String? =
    (this[field] as? kotlinx.serialization.json.JsonPrimitive)?.takeIf { it.isString }?.content

// -------------------------------------------------------------------------------------------
// Real bodies, from openapi.yaml `components.examples` and SERVER_SPEC.md § 9. Invented ones are
// the ones that agree with whatever the client happens to do.
// -------------------------------------------------------------------------------------------

/** openapi.yaml `SnapshotRanking`, verbatim. Note `beach day #2.jpg`: an id with a space and a `#`. */
const val SNAPSHOT_RANKING: String = """
{
  "sessionId": "Qv8kZ2r5tN0pXbA1cD3eFg",
  "state": "ranking",
  "folder": "D:\\photos\\trip",
  "folderName": "trip",
  "policy": "still",
  "openedAt": "2026-09-12T17:40:02.000Z",
  "prefetchPairs": 2,
  "sessionVotes": 13,
  "counts": { "total": 415, "rankable": 412, "unranked": 380, "stills": 412, "videos": 3 },
  "progress": 0.114213,
  "progressPercent": 11,
  "cues": ["confirmation", "upset", "confirmation", "confirmation"],
  "pair": {
    "left": {
      "id": "IMG_0042.jpg",
      "kind": "still",
      "sizeBytes": 3128844,
      "mediaVersion": "1a0b9c8d7e6f5041",
      "links": {
        "meta": "/api/v1/media/IMG_0042.jpg/meta",
        "still": "/api/v1/media/IMG_0042.jpg/still?v=1a0b9c8d7e6f5041",
        "thumb": "/api/v1/media/IMG_0042.jpg/thumb?v=1a0b9c8d7e6f5041",
        "video": null
      }
    },
    "right": {
      "id": "beach day #2.jpg",
      "kind": "still",
      "sizeBytes": 5012993,
      "mediaVersion": "44cc21a0be7f1d93",
      "links": {
        "meta": "/api/v1/media/beach%20day%20%232.jpg/meta",
        "still": "/api/v1/media/beach%20day%20%232.jpg/still?v=44cc21a0be7f1d93",
        "thumb": "/api/v1/media/beach%20day%20%232.jpg/thumb?v=44cc21a0be7f1d93",
        "video": null
      }
    }
  },
  "pairToken": "M0xQrT8vB3nJ7yE2sW9uHk",
  "pairSeq": 14,
  "warmPairs": [
    {
      "left": {
        "id": "IMG_0107.jpg",
        "kind": "still",
        "sizeBytes": 2884101,
        "mediaVersion": "c3d4e5f607182930",
        "links": {
          "meta": "/api/v1/media/IMG_0107.jpg/meta",
          "still": "/api/v1/media/IMG_0107.jpg/still?v=c3d4e5f607182930",
          "thumb": "/api/v1/media/IMG_0107.jpg/thumb?v=c3d4e5f607182930",
          "video": null
        }
      },
      "right": {
        "id": "IMG_0311.jpg",
        "kind": "still",
        "sizeBytes": 4102338,
        "mediaVersion": "7788aabbccddeeff",
        "links": {
          "meta": "/api/v1/media/IMG_0311.jpg/meta",
          "still": "/api/v1/media/IMG_0311.jpg/still?v=7788aabbccddeeff",
          "thumb": "/api/v1/media/IMG_0311.jpg/thumb?v=7788aabbccddeeff",
          "video": null
        }
      }
    }
  ],
  "undoAvailable": true,
  "lastAction": {
    "seq": 14,
    "type": "vote",
    "pairToken": "7tRbQ0xW9mKa2ZpL4nVdCe",
    "clientRequestId": "1f0c2a7e-6b41-4f0a-9f6a-2b3c4d5e6f70",
    "winner": "left",
    "side": null,
    "id": null,
    "restoredId": null,
    "undoneType": null,
    "at": "2026-09-12T18:04:11.400Z"
  },
  "lastSavedAt": "2026-09-12T18:04:11.398Z"
}
"""

/** openapi.yaml `SnapshotExhausted`, verbatim. `pair` and `pairToken` are both null (§ 8.2). */
const val SNAPSHOT_EXHAUSTED: String = """
{
  "sessionId": "Qv8kZ2r5tN0pXbA1cD3eFg",
  "state": "exhausted",
  "folder": "D:\\photos\\tiny",
  "folderName": "tiny",
  "policy": "still",
  "openedAt": "2026-09-12T17:40:02.000Z",
  "prefetchPairs": 2,
  "sessionVotes": 2,
  "counts": { "total": 1, "rankable": 1, "unranked": 0, "stills": 1, "videos": 0 },
  "progress": 0.061,
  "progressPercent": 6,
  "cues": ["confirmation", "upset"],
  "pair": null,
  "pairToken": null,
  "pairSeq": 5,
  "warmPairs": [],
  "undoAvailable": true,
  "lastAction": {
    "seq": 5,
    "type": "discard",
    "pairToken": "M0xQrT8vB3nJ7yE2sW9uHk",
    "clientRequestId": null,
    "winner": null,
    "side": "right",
    "id": "IMG_0311.jpg",
    "restoredId": null,
    "undoneType": null,
    "at": "2026-09-12T18:09:50.120Z"
  },
  "lastSavedAt": "2026-09-12T18:09:50.118Z"
}
"""

/** § 8.5: the whole point of the 409 - the complete current state, in the refusal. */
fun stalePairToken(supplied: String, snapshot: String = SNAPSHOT_RANKING): String = """
{
  "error": {
    "code": "stale_pair_token",
    "message": "The supplied pairToken is not the current pair.",
    "requestId": "01J8Z5K0QF3V8A0M6R9Q2B7T4C",
    "details": { "suppliedToken": "$supplied", "currentToken": "M0xQrT8vB3nJ7yE2sW9uHk" },
    "session": $snapshot
  }
}
"""

fun errorEnvelope(code: String, message: String, details: String? = null): String = """
{
  "error": {
    "code": "$code",
    "message": "$message",
    "requestId": "01J8Z5K0QF3V8A0M6R9Q2B7T4C"${if (details == null) "" else ",\n    \"details\": $details"}
  }
}
"""

/** § 14, the authenticated body. `features` and `limits` are fields [Ping] does not declare. */
const val PING_BODY: String = """
{
  "product": "Rank Master 2 server",
  "apiVersion": "v1",
  "version": "1.1.3",
  "ready": true,
  "authenticated": true,
  "certificateFingerprint": "sha256:3b1f00000000000000000000000000000000000000000000000000000000beef",
  "serverTime": "2026-09-12T18:04:11.412Z",
  "features": {
    "rename": false,
    "videoTranscoding": false,
    "posterFrames": false,
    "videoProbe": false,
    "browse": "full-filesystem",
    "maxConcurrentSessions": 1
  },
  "limits": {
    "stillWidths": [360, 540, 720, 1080, 1440, 2160],
    "thumbWidth": 320,
    "maxJsonBodyBytes": 65536,
    "sessionLockTimeoutSeconds": 5
  },
  "session": { "open": true, "sessionId": "Qv8kZ2r5tN0pXbA1cD3eFg", "folder": "D:\\photos\\trip", "state": "ranking" }
}
"""
