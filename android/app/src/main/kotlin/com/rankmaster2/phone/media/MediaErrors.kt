package com.rankmaster2.phone.media

import java.io.IOException
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import okhttp3.Interceptor
import okhttp3.Response

/** The § 5.5 codes a media URL can answer with, plus the two § 5.3 / § 5.1 ones it can also hit. */
object MediaErrorCodes {
    const val MEDIA_DECODE_FAILED = "media_decode_failed"
    const val MEDIA_FILE_MISSING = "media_file_missing"
    const val UNKNOWN_MEDIA_ID = "unknown_media_id"
    const val WRONG_MEDIA_KIND = "wrong_media_kind"
    const val MEDIA_OUTSIDE_SESSION = "media_outside_session"
    const val MEDIA_EXTENSION_NOT_ALLOWED = "media_extension_not_allowed"
    const val UNSUPPORTED_WIDTH = "unsupported_width"
    const val UNSUPPORTED_FORMAT = "unsupported_format"
    const val NO_SESSION = "no_session"
}

/**
 * A media URL answered, and said no.
 *
 * Carries the `error.code` from the envelope, not just the status, because the client is required
 * to branch on the code (SERVER_SPEC.md § 4) and because two `404`s on the same URL mean opposite
 * things: `media_file_missing` means "this file is gone, offer to discard that side", while
 * `no_session` means "the session went away, reopen it" - and discarding on the second would throw
 * away a picture the user still has.
 */
class MediaHttpException(
    val status: Int,
    val code: String?,
    val url: String,
    override val message: String,
) : IOException(message)

/**
 * Turns a non-2xx media response into a [MediaHttpException].
 *
 * It lives on the image loader's client rather than in the pane, because Coil hands a caller a
 * `Throwable` and nothing else: without this, a pane could see only "it failed" and would have to
 * guess between a file that has gone, a file that will not decode, and a Wi-Fi hiccup. Those need
 * three different offers on screen.
 *
 * The error body is a small JSON envelope; it is peeked with a hard cap and the response is closed
 * either way, so nothing leaks a connection.
 */
class MediaErrorInterceptor : Interceptor {

    override fun intercept(chain: Interceptor.Chain): Response {
        val response = chain.proceed(chain.request())
        if (response.isSuccessful) return response

        val code = try {
            codeOf(response.peekBody(MAX_ERROR_BODY).string())
        } catch (_: Exception) {
            null
        }
        val url = response.request.url.toString()
        response.close()

        throw MediaHttpException(
            status = response.code,
            code = code,
            url = url,
            message = "HTTP ${response.code}" + (code?.let { " $it" } ?: "") + " for $url",
        )
    }

    private companion object {
        const val MAX_ERROR_BODY = 8L * 1024

        val JSON = Json { ignoreUnknownKeys = true }

        /** `error.code`, or null if this was not an error envelope at all. */
        fun codeOf(body: String): String? =
            JSON.parseToJsonElement(body)
                .jsonObject["error"]
                ?.jsonObject
                ?.get("code")
                ?.jsonPrimitive
                ?.content
    }
}

/**
 * What a pane is showing. The whole point of this type is that the caller can tell [Gone] from
 * [Undecodable] from [Unavailable] without knowing anything about HTTP.
 *
 * What a tap *means* is deliberately not here: this layer never votes, never discards and never
 * decides. It says what the pane can show; the ranking screen decides what to offer.
 */
sealed interface MediaPaneState {

    /** Bytes are on the way, or being decoded. */
    data object Loading : MediaPaneState

    /** On screen. */
    data object Loaded : MediaPaneState

    /**
     * `422 media_decode_failed`, or a decoder on this phone that would not take the file. The file
     * is there and will not become a picture. A pane, not a crash - the desktop app drops such a
     * file from the UI thread, but here the client decides and the sane offer is still to discard.
     */
    data object Undecodable : MediaPaneState

    /**
     * `404 media_file_missing` / `unknown_media_id`, or a `MediaRef` that already arrived with
     * `sizeBytes: null`. The file has left the folder under the session (§ 11.3). The caller's one
     * sane offer is to discard that side, which the server turns into `drop_missing` (§ 10.8).
     */
    data object Gone : MediaPaneState

    /**
     * Anything else: the network, a `no_session`, a `401`. Retryable, or a cue to resynchronise -
     * never a reason to discard a file.
     */
    data class Unavailable(val code: String?, val detail: String) : MediaPaneState
}

/** How a failure from Coil, Media3 or a plain GET becomes one of the states above. */
object MediaFailures {

    fun stateOf(error: Throwable?): MediaPaneState {
        val http = error?.asMediaHttp()
            ?: return MediaPaneState.Unavailable(null, error?.message ?: "The picture did not load.")
        return stateOf(http.status, http.code, http.message)
    }

    fun stateOf(status: Int, code: String?, detail: String): MediaPaneState = when {
        code == MediaErrorCodes.MEDIA_DECODE_FAILED -> MediaPaneState.Undecodable
        code == MediaErrorCodes.MEDIA_FILE_MISSING -> MediaPaneState.Gone
        code == MediaErrorCodes.UNKNOWN_MEDIA_ID -> MediaPaneState.Gone
        // The envelope did not parse, so fall back to the status. § 11.3 is explicit that a 404 on
        // a media URL means "this id is gone", never "the network glitched, retry the URL".
        code == null && status == 404 -> MediaPaneState.Gone
        code == null && status == 422 -> MediaPaneState.Undecodable
        else -> MediaPaneState.Unavailable(code, detail)
    }

    private fun Throwable.asMediaHttp(): MediaHttpException? {
        var cause: Throwable? = this
        var hops = 0
        while (cause != null && hops++ < 8) {
            if (cause is MediaHttpException) return cause
            cause = cause.cause
        }
        return null
    }
}
