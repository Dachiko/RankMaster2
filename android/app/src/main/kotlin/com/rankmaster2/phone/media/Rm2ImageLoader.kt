package com.rankmaster2.phone.media

import android.content.Context
import coil.ImageLoader
import coil.memory.MemoryCache
import coil.request.CachePolicy
import okhttp3.Interceptor
import okhttp3.OkHttpClient
import okhttp3.Response
import okhttp3.ResponseBody.Companion.toResponseBody

/**
 * The app's one image loader.
 *
 * ## It has no cache of its own, and that is the design
 *
 * Every byte response from the server carries a strong `ETag` and
 * `Cache-Control: private, max-age=31536000, immutable`, and the `links` it hands out already carry
 * `v=<mediaVersion>`, so a link's bytes genuinely never change (SERVER_SPEC.md § 12.5). OkHttp's
 * own disk cache - the 512 MB one `Rm2Http` configures, shared by the API, the pictures and the
 * video - honours exactly those headers. That is already the whole caching story: each photo
 * crosses the network once, ever.
 *
 * So Coil's own disk cache is switched **off**. Not to be frugal: a second disk cache in front of a
 * correct one is a second copy of every photograph on a phone, a second eviction policy nobody
 * tuned, and a second place for the answer to be stale. `diskCache = null` makes Coil's
 * `HttpUriFetcher` fall through to a plain OkHttp call, which is what we want it to do.
 *
 * The memory cache stays on. That is a cache of *decoded bitmaps*, not of bytes - it is not a
 * second copy of the network layer, it is what stops a recomposition re-decoding a 2160 px photo.
 *
 * `networkCachePolicy` and `diskCachePolicy` are left **enabled** and `respectCacheHeaders` is left
 * **true**, which is Coil's default and the only correct setting here: turning them off to "be
 * safe" would send `Cache-Control: no-cache` on every request and throw away the one guarantee the
 * server went to the trouble of making.
 */
object Rm2ImageLoader {

    /**
     * Build the loader on [client] - which must be `Rm2Http.client(identity)`. Nothing in this
     * layer ever constructs an `OkHttpClient`: a second one is a second place to forget the
     * certificate pin and the bearer token.
     *
     * The two interceptors are added with `newBuilder()`, which shares the dispatcher, the
     * connection pool and - the point - the same `Cache` instance. It is the same HTTP stack, with
     * two behaviours the image path needs and the API path does not.
     */
    fun create(context: Context, client: OkHttpClient): ImageLoader =
        ImageLoader.Builder(context)
            .callFactory(
                client.newBuilder()
                    .addInterceptor(MediaErrorInterceptor())
                    .addInterceptor(FullyReadBodyInterceptor())
                    .build()
            )
            // Coil keeps no bytes. OkHttp's cache does, and it is the one the server's headers
            // were written for. See the class comment.
            .diskCache(null)
            .diskCachePolicy(CachePolicy.ENABLED)
            .networkCachePolicy(CachePolicy.ENABLED)
            .respectCacheHeaders(true)
            .memoryCache {
                MemoryCache.Builder(context)
                    // Two panes of a full-screen photograph, plus the pair behind them.
                    .maxSizePercent(0.25)
                    .build()
            }
            .build()
}

/**
 * Reads a successful body to the end before handing it on.
 *
 * OkHttp only commits a cache entry when the response body is read to completion; a decoder that
 * stops at the end of the image data and leaves a few trailing bytes in the stream leaves the entry
 * aborted, and the "crosses the network once" promise quietly becomes "crosses the network every
 * time". A still is at most a couple of megabytes, so buffering it is cheap insurance on the
 * guarantee this whole layer is built on.
 *
 * Anything without a known length, or larger than [MAX_BUFFERED], is passed through untouched -
 * video never goes through this client, but nothing here assumes that.
 */
internal class FullyReadBodyInterceptor : Interceptor {

    override fun intercept(chain: Interceptor.Chain): Response {
        val response = chain.proceed(chain.request())
        val body = response.body
        if (!response.isSuccessful || body == null) return response
        val length = body.contentLength()
        if (length < 0 || length > MAX_BUFFERED) return response

        val bytes = body.bytes()
        return response.newBuilder()
            .body(bytes.toResponseBody(body.contentType()))
            .build()
    }

    private companion object {
        const val MAX_BUFFERED = 32L * 1024 * 1024
    }
}
