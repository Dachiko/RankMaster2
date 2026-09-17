package com.rankmaster2.phone.net.impl

import com.rankmaster2.phone.net.Browse
import com.rankmaster2.phone.net.PairedDevice
import com.rankmaster2.phone.net.Ping
import com.rankmaster2.phone.net.PinMismatchException
import com.rankmaster2.phone.net.Rm2Client
import com.rankmaster2.phone.net.Rm2Http
import com.rankmaster2.phone.net.Rm2Result
import com.rankmaster2.phone.net.Roots
import com.rankmaster2.phone.net.Snapshot
import com.rankmaster2.phone.store.ServerIdentity
import java.io.IOException
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.serialization.decodeFromString
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import okhttp3.Call
import okhttp3.Callback
import okhttp3.HttpUrl.Companion.toHttpUrl
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response

/**
 * [Rm2Client] over OkHttp, against SERVER_SPEC.md § 10.
 *
 * ## The client it is given
 *
 * It never builds an [OkHttpClient]. The one it is handed comes from [Rm2Http], which is where the
 * certificate pin and the bearer token live, and which the image loader and the video player share.
 * A second HTTP stack in this app would be a second place both can be forgotten - so this class
 * takes one and does not know how to make one.
 *
 * ## Three answers, not an exception
 *
 * Every call ends as one of [Rm2Result.Ok], [Rm2Result.Refused] or [Rm2Result.Unreachable], and
 * nothing thrown escapes except cancellation. A body that will not parse is a [Rm2Result.Refused]
 * with one of the [Rm2SyntheticCodes]: a malformed server answer is the server saying no in a way
 * this client did not understand, and it must not be able to crash a ranking session.
 *
 * ## Why the refusal carries a snapshot
 *
 * `409 stale_pair_token` embeds the entire current [Snapshot] in `error.session` (§ 8.5), which is
 * the difference between resynchronising in one round trip and polling. Parsing it is not an
 * optimisation; a client that drops it is required to go and ask again, and every extra round trip
 * there is a window in which the user can vote twice.
 */
class OkHttpRm2Client(
    override val baseUrl: String,
    private val http: OkHttpClient,
) : Rm2Client {

    /**
     * The ordinary case: a paired server, and the pinned, token-carrying client [Rm2Http] keeps for
     * it. Pairing is the exception - it has a fingerprint but no token yet, and passes
     * [Rm2Http.pairingClient] to the primary constructor instead.
     */
    constructor(identity: ServerIdentity) : this(identity.baseUrl, Rm2Http.client(identity))

    /** `https://host:port`, with no path. What a `links.*` value is appended to, verbatim (§ 9.3). */
    private val origin: String = originOf(baseUrl)

    /** `https://host:port/api/v1`, with no trailing slash. */
    private val api: String = baseUrl.trimEnd('/')

    // -- connection ----------------------------------------------------------------------------

    override suspend fun ping(): Rm2Result<Ping> =
        call(get("/ping")) { decode<Ping>(it) }

    override suspend fun pair(code: String, deviceName: String): Rm2Result<PairedDevice> =
        call(post("/pair", PairRequest(code, deviceName))) { decode<PairedDevice>(it) }

    override suspend fun revoke(deviceId: String): Rm2Result<Unit> =
        call(
            Request.Builder()
                .url(api + "/pair/" + segment(deviceId))
                .delete()
                .build()
        ) { }

    // -- picking a folder ----------------------------------------------------------------------

    override suspend fun roots(): Rm2Result<Roots> =
        call(get("/libraries/roots")) { decode<Roots>(it) }

    override suspend fun browse(path: String, counts: Boolean): Rm2Result<Browse> {
        val url = (api + "/libraries/browse").toHttpUrl().newBuilder()
            .addQueryParameter("path", path)
            .addQueryParameter("counts", counts.toString())
            .build()
        return call(Request.Builder().url(url).get().build()) { decode<Browse>(it) }
    }

    // -- the session ---------------------------------------------------------------------------

    override suspend fun openSession(folder: String): Rm2Result<Snapshot> =
        call(post("/session", OpenSessionRequest(folder))) { decode<Snapshot>(it) }

    override suspend fun session(): Rm2Result<Snapshot> =
        call(get("/session")) { decode<Snapshot>(it) }

    override suspend fun closeSession(): Rm2Result<Unit> =
        call(Request.Builder().url(api + "/session").delete().build()) { }

    /**
     * § 10.5. Takes no body of its own, but still sends `{}` as JSON: the server answers
     * `415 unsupported_content_type` to a request whose `Content-Type` is not `application/json`
     * (§ 2), and OkHttp will not put a content type on a POST with no body at all.
     */
    override suspend fun save(): Rm2Result<Snapshot> =
        call(postJson("/session/save", "{}")) { decode<Snapshot>(it) }

    // -- the actions ---------------------------------------------------------------------------

    override suspend fun vote(pairToken: String, winner: String, clientRequestId: String): Rm2Result<Snapshot> =
        call(post("/session/vote", VoteRequest(pairToken, winner, clientRequestId))) { decode<Snapshot>(it) }

    override suspend fun skip(pairToken: String, clientRequestId: String): Rm2Result<Snapshot> =
        call(post("/session/skip", SkipRequest(pairToken, clientRequestId))) { decode<Snapshot>(it) }

    override suspend fun discard(pairToken: String, side: String, clientRequestId: String): Rm2Result<Snapshot> =
        call(post("/session/discard", SideActionRequest(pairToken, side, clientRequestId))) { decode<Snapshot>(it) }

    override suspend fun special(pairToken: String, side: String, clientRequestId: String): Rm2Result<Snapshot> =
        call(post("/session/special", SideActionRequest(pairToken, side, clientRequestId))) { decode<Snapshot>(it) }

    override suspend fun cancel(clientRequestId: String): Rm2Result<Snapshot> =
        call(post("/session/undo", CancelRequest(clientRequestId))) { decode<Snapshot>(it) }

    // -- bytes ---------------------------------------------------------------------------------

    /**
     * § 9.3. The server built this string, percent-encoded it and put `v=` on it. It is concatenated
     * and never re-parsed: running it back through a URL builder is how `beach day %232.jpg`
     * becomes `beach day %25232.jpg` in one client and `beach+day+#2.jpg` in the next.
     */
    override fun url(link: String): String = when {
        link.startsWith("https://") || link.startsWith("http://") -> link
        link.startsWith("/") -> origin + link
        else -> "$api/$link"
    }

    // -- the plumbing --------------------------------------------------------------------------

    private fun get(path: String): Request =
        Request.Builder().url(api + path).get().build()

    private inline fun <reified T> post(path: String, body: T): Request =
        postJson(path, json.encodeToString(body))

    private fun postJson(path: String, body: String): Request =
        Request.Builder().url(api + path).post(body.toRequestBody(JSON)).build()

    /**
     * Runs [request] and turns whatever happens into one of the three results.
     *
     * [decode] is only reached for a 2xx. It is allowed to throw: a 2xx this client cannot read is
     * reported as [Rm2SyntheticCodes.MALFORMED_RESPONSE], not as a crash and not as [Rm2Result.Ok]
     * of something half-built.
     */
    private suspend fun <T> call(request: Request, decode: (String) -> T): Rm2Result<T> {
        val response = try {
            http.newCall(request).await()
        } catch (cancelled: CancellationException) {
            throw cancelled
        } catch (failure: Throwable) {
            return unreachable(failure)
        }

        return response.use { r ->
            val requestId = r.header("X-Request-Id")
            val body = try {
                r.body?.string().orEmpty()
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (failure: Throwable) {
                // The status arrived and the body did not. Nothing was learned, so this is not an
                // answer - and an action must be able to retry it with the same token (§ 13.3).
                return unreachable(failure)
            }

            if (!r.isSuccessful) return refuse(r.code, requestId, body)

            try {
                Rm2Result.Ok(decode(body))
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (failure: Throwable) {
                Rm2Result.Refused(
                    status = r.code,
                    code = Rm2SyntheticCodes.MALFORMED_RESPONSE,
                    message = "The server answered ${r.code} with a body this app could not read: " +
                        (failure.message ?: failure.javaClass.simpleName),
                    session = null,
                    requestId = requestId,
                )
            }
        }
    }

    /**
     * The § 4 envelope, read field by field rather than decoded as one strict-typed object.
     *
     * The reason is `error.session`: a strict decode of the whole envelope loses `code` and
     * `message` too if the embedded snapshot has anything wrong with it, and then the app cannot
     * even tell the user which refusal it was. Read this way, an unreadable snapshot costs the
     * snapshot alone.
     */
    private fun refuse(status: Int, headerRequestId: String?, body: String): Rm2Result.Refused {
        val error = try {
            (json.parseToJsonElement(body) as? JsonObject)?.get("error") as? JsonObject
        } catch (cancelled: CancellationException) {
            throw cancelled
        } catch (_: Throwable) {
            null
        } ?: return Rm2Result.Refused(
            status = status,
            code = Rm2SyntheticCodes.MALFORMED_ERROR,
            message = "The server answered $status without the error envelope this app expects.",
            session = null,
            requestId = headerRequestId,
        )

        val code = error.text("code")
        val session = error["session"]
            ?.takeUnless { it is JsonNull }
            ?.let { runCatching { json.decodeFromJsonElement(Snapshot.serializer(), it) }.getOrNull() }

        return Rm2Result.Refused(
            status = status,
            code = code ?: Rm2SyntheticCodes.MALFORMED_ERROR,
            message = error.text("message")
                ?: "The server answered $status with no message.",
            session = session,
            requestId = error.text("requestId") ?: headerRequestId,
            // § 5's per-code payload, passed through as it arrived. The pairing screen reads
            // attemptsRemaining out of it; nothing here has to know which codes carry what.
            details = error["details"] as? JsonObject,
        )
    }

    /**
     * No answer. [PinMismatchException] is the one cause that is never a network problem: the app
     * must stop on it rather than offer to try again, so it is lifted out of the cause chain here
     * and put on the result where a caller cannot miss it.
     */
    private fun unreachable(failure: Throwable): Rm2Result.Unreachable = Rm2Result.Unreachable(
        cause = failure,
        detail = failure.message ?: failure.javaClass.simpleName,
        pinMismatch = isPinMismatch(failure),
    )

    private inline fun <reified T> decode(body: String): T = json.decodeFromString(body)

    private companion object {
        val JSON = "application/json; charset=utf-8".toMediaType()

        /**
         * § 2: the server ignores unknown request fields and reserves the right to add response
         * ones. A client that refuses a snapshot because a later server version added a field to it
         * is a client that breaks on an upgrade it did not need to notice.
         */
        val json = Json {
            ignoreUnknownKeys = true
            isLenient = false
        }

        fun JsonObject.text(field: String): String? =
            (this[field] as? JsonPrimitive)?.takeIf { it.isString }?.content

        /** `https://host:port` - everything before the path. Sliced, never rebuilt (§ 9.3). */
        fun originOf(baseUrl: String): String {
            val scheme = baseUrl.indexOf("://")
            if (scheme < 0) return baseUrl.trimEnd('/')
            val path = baseUrl.indexOf('/', scheme + 3)
            return if (path < 0) baseUrl else baseUrl.substring(0, path)
        }

        /** A device id is opaque and may contain anything; it is encoded before it becomes a path. */
        fun segment(value: String): String =
            java.net.URLEncoder.encode(value, "UTF-8").replace("+", "%20")

        /**
         * Walks causes *and* suppressed exceptions. The pin fails inside the TLS handshake, so it
         * arrives wrapped in an `SSLHandshakeException`; OkHttp's async path can instead attach a
         * non-IO throwable as suppressed rather than as a cause, and a pin mismatch that was only
         * *nearly* detected is the same as one that was not detected at all.
         */
        fun isPinMismatch(failure: Throwable?): Boolean {
            var depth = 0
            var current = failure
            val seen = HashSet<Throwable>()
            while (current != null && depth++ < 16 && seen.add(current)) {
                if (current is PinMismatchException) return true
                if (current.suppressed.any { isPinMismatch(it) }) return true
                current = current.cause
            }
            return false
        }
    }
}

/**
 * Codes this client invents when the server's answer was not one. They are prefixed so that they
 * can never be mistaken for, or collide with, a SERVER_SPEC.md § 5 code - including one added
 * later.
 */
object Rm2SyntheticCodes {

    /** A 2xx whose body is not the shape this app expects. */
    const val MALFORMED_RESPONSE = "client_malformed_response"

    /** A non-2xx that did not carry a readable § 4 envelope - or carried no body at all. */
    const val MALFORMED_ERROR = "client_malformed_error"
}

/**
 * OkHttp's async call as a suspending one, so that cancelling the coroutine cancels the request
 * instead of leaking it.
 */
private suspend fun Call.await(): Response = suspendCancellableCoroutine { continuation ->
    continuation.invokeOnCancellation { cancel() }
    enqueue(object : Callback {
        override fun onFailure(call: Call, e: IOException) {
            if (continuation.isActive) continuation.resumeWithException(e)
        }

        override fun onResponse(call: Call, response: Response) {
            if (continuation.isActive) continuation.resume(response) else response.close()
        }
    })
}
