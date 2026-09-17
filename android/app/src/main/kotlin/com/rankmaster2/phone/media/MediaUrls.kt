package com.rankmaster2.phone.media

/**
 * Which encoding of a still to ask for. SERVER_SPEC.md § 12.3.
 *
 * `format=webp`, always: WebP is materially smaller than JPEG at the same visual quality for
 * photographs, and a URL that names its own format is a cache key that cannot be got wrong - the
 * response carries no `Vary: Accept`, so every cache on the way, ours included, keys on the URL and
 * nothing else. The server also accepts an explicit `format=jpeg` and format negotiation with no
 * `format=` at all (§ 12.3, rule 2); this app has never had a reason to ask for either.
 */
enum class StillFormat(internal val query: String?) {
    WEBP("webp"),
}

/**
 * Turning a `links.*` value into the URL that is actually fetched.
 *
 * The rule from SERVER_SPEC.md § 9.3 and § 11.1 is one line long: **use the link verbatim.** The
 * server percent-encoded the filename for us, and it is the filename - `MediaId` is nothing but the
 * name on disk, so ids routinely contain spaces, `+`, `&`, `#`, and every byte above 0x7F. Any
 * client that re-parses and re-emits that URL is one canonicalisation rule away from asking for a
 * file that does not exist.
 *
 * So this file does string concatenation and no parsing. `w=` and `format=` are appended to
 * whatever the server wrote, after the `v=` it already carries. Nothing in the link is touched,
 * decoded, re-encoded or normalised.
 */
object MediaUrls {

    /**
     * [link] (a `links.still` value) with `w=` and `format=` appended.
     *
     * @throws IllegalArgumentException if [width] is not one of the six the server accepts - which
     *   would be a `400 unsupported_width` at the other end, and is a bug here, not a server
     *   condition to handle.
     */
    fun still(link: String, width: Int, format: StillFormat = StillFormat.WEBP): String {
        require(MediaWidths.isAllowed(width)) {
            "w=$width is not one of ${MediaWidths.ALLOWED}; the server refuses anything else (§ 12.3)."
        }
        var url = append(link, "w", width.toString())
        format.query?.let { url = append(url, "format", it) }
        return url
    }

    /** An absolute URL for a link that may be server-relative (`/api/v1/media/...`), as they are. */
    fun resolve(baseUrl: String, link: String): String {
        if (link.startsWith("http://") || link.startsWith("https://")) return link
        if (!link.startsWith("/")) return baseUrl.trimEnd('/') + "/" + link
        return origin(baseUrl) + link
    }

    /** `https://host:port` of [baseUrl], which is `https://host:port/api/v1`. */
    internal fun origin(baseUrl: String): String {
        val scheme = baseUrl.indexOf("://")
        if (scheme < 0) return baseUrl.trimEnd('/')
        val path = baseUrl.indexOf('/', startIndex = scheme + 3)
        return if (path < 0) baseUrl else baseUrl.substring(0, path)
    }

    /**
     * Append one query parameter. [name] and [value] here are always ours - `w`, `format` and their
     * values - and contain nothing that needs encoding, which is why this can be concatenation.
     */
    private fun append(url: String, name: String, value: String): String {
        val separator = if (url.contains('?')) '&' else '?'
        return "$url$separator$name=$value"
    }
}
