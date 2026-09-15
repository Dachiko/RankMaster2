package com.rankmaster2.phone.media

import android.content.Context
import coil.ImageLoader
import com.rankmaster2.phone.net.MediaRef
import com.rankmaster2.phone.net.Rm2Http
import com.rankmaster2.phone.store.ServerIdentity
import okhttp3.OkHttpClient

/**
 * Everything the ranking screen needs to put a photograph or a video on the glass, and nothing it
 * needs to decide what a tap means.
 *
 * One of these per paired server, built once and kept for the life of the screen: it owns an
 * `ImageLoader` with a memory cache in it, and building a second one throws that cache away.
 *
 * The seam is deliberately narrow. This layer knows about widths, formats, `links`, ETags and
 * decoders. It does not know what a vote is, and the ranking screen does not know what a `w=` is.
 */
class Rm2Media internal constructor(
    val baseUrl: String,
    val imageLoader: ImageLoader,
    val prefetcher: MediaPrefetcher,
    val players: Rm2VideoPlayers,
    val format: StillFormat,
) {

    /**
     * The URL for [ref]'s still at the width a pane whose long edge is [panePx] should ask for, or
     * null if there is nothing to fetch - a video, or a file that has already gone from the folder.
     *
     * The link is used verbatim (§ 9.3); only `w=` and `format=` are appended.
     */
    fun stillUrl(ref: MediaRef, panePx: Int): String? {
        if (ref.isMissing) return null
        val link = ref.links.still ?: return null
        return MediaUrls.resolve(baseUrl, MediaUrls.still(link, MediaWidths.forPane(panePx), format))
    }

    /** The URL for [ref]'s video, or null if it is not one. */
    fun videoUrl(ref: MediaRef): String? = players.videoUrl(ref)

    /** Drops the decoded bitmaps. The bytes stay in OkHttp's cache, where they belong. */
    fun shutdown() {
        imageLoader.shutdown()
    }

    companion object {

        /**
         * The normal way in: one paired server, one pinned and authenticated OkHttp client, which
         * `Rm2Http` has already built and cached.
         */
        fun create(
            context: Context,
            identity: ServerIdentity,
            format: StillFormat = StillFormat.WEBP,
        ): Rm2Media = create(context, identity.baseUrl, Rm2Http.client(identity), format)

        /**
         * For tests, and for anything that already holds the client. [client] must be the one from
         * `Rm2Http` - a second HTTP stack is a second place to forget the certificate pin.
         */
        fun create(
            context: Context,
            baseUrl: String,
            client: OkHttpClient,
            format: StillFormat = StillFormat.WEBP,
        ): Rm2Media {
            val application = context.applicationContext
            return Rm2Media(
                baseUrl = baseUrl,
                imageLoader = Rm2ImageLoader.create(application, client),
                prefetcher = MediaPrefetcher(client, baseUrl, format),
                players = Rm2VideoPlayers(application, client, baseUrl),
                format = format,
            )
        }
    }
}
