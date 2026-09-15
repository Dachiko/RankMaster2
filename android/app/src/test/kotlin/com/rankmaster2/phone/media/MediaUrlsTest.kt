package com.rankmaster2.phone.media

import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The link goes through untouched.
 *
 * A media id is a filename (SERVER_SPEC.md § 11.1), so it contains whatever the owner's camera and
 * the owner's fingers put there: spaces, `+`, `&`, `#`, accents. The server encoded it once,
 * correctly, and § 9.3 asks clients to use the result verbatim. Every case here is a filename that
 * a client which re-parsed and re-emitted the URL would get wrong.
 */
class MediaUrlsTest {

    private val v = "?v=9f2a1c77b0e4d310"

    @Test
    fun `a space stays percent-encoded and never becomes a plus`() {
        val link = "/api/v1/media/holiday%20photo.jpg/still$v"
        val url = MediaUrls.still(link, 1080)

        assertTrue(url.startsWith("/api/v1/media/holiday%20photo.jpg/still"))
        assertTrue("a space in a path segment must be %20, never + (§ 11.1)", "+" !in url.substringBefore('?'))
        assertEquals("/api/v1/media/holiday%20photo.jpg/still?v=9f2a1c77b0e4d310&w=1080&format=webp", url)
    }

    @Test
    fun `a literal plus in the filename survives`() {
        // "a+b.jpg" on disk. The server left the + alone - it is legal in a path segment and means
        // itself there. Re-encoding it to %2B, or decoding it to a space, both ask for a file that
        // is not there.
        val link = "/api/v1/media/a+b.jpg/still$v"
        val url = MediaUrls.still(link, 720)

        assertEquals("/api/v1/media/a+b.jpg/still?v=9f2a1c77b0e4d310&w=720&format=webp", url)
    }

    @Test
    fun `an already-encoded plus stays encoded`() {
        val link = "/api/v1/media/a%2Bb.jpg/still$v"
        assertTrue(MediaUrls.still(link, 720).startsWith("/api/v1/media/a%2Bb.jpg/still?"))
    }

    @Test
    fun `everything else a filename can contain survives too`() {
        val cases = listOf(
            "IMG%20%231.jpg",              // a # in the name
            "Ma%CC%88dchen.jpg",           // decomposed umlaut - not to be normalised, § 11.1 rule 3
            "M%C3%A4dchen.jpg",            // composed umlaut - a different id, deliberately
            "a%26b%3Dc.jpg",               // & and =
            "100%25.jpg",                  // a literal percent sign
            "%D0%BA%D0%BE%D1%82.jpg",      // Cyrillic
            "photo%20(2).jpg",
        )
        for (id in cases) {
            val link = "/api/v1/media/$id/still$v"
            val url = MediaUrls.still(link, 360)
            assertEquals("/api/v1/media/$id/still?v=9f2a1c77b0e4d310&w=360&format=webp", url)
        }
    }

    @Test
    fun `w and format are appended after the v the server already put there`() {
        val url = MediaUrls.still("/api/v1/media/x.jpg/still?v=abc", 1440, StillFormat.JPEG)
        assertEquals("/api/v1/media/x.jpg/still?v=abc&w=1440&format=jpeg", url)
    }

    @Test
    fun `a link with no query of its own gets a question mark`() {
        assertEquals(
            "/api/v1/media/x.jpg/still?w=1080&format=webp",
            MediaUrls.still("/api/v1/media/x.jpg/still", 1080),
        )
    }

    @Test
    fun `negotiating the format sends no format parameter`() {
        val url = MediaUrls.still("/api/v1/media/x.jpg/still?v=abc", 1080, StillFormat.NEGOTIATE)
        assertEquals("/api/v1/media/x.jpg/still?v=abc&w=1080", url)
    }

    @Test
    fun `a width the server would refuse is a bug here, not a request`() {
        // Better to fail on this phone than to spend a round trip discovering 400 unsupported_width.
        assertThrows(IllegalArgumentException::class.java) {
            MediaUrls.still("/api/v1/media/x.jpg/still", 1000)
        }
        assertThrows(IllegalArgumentException::class.java) {
            MediaUrls.still("/api/v1/media/x.jpg/still", 320)
        }
    }

    @Test
    fun `resolving against the base url keeps the encoded segment intact`() {
        val base = "https://192.168.1.42:18611/api/v1"
        val link = "/api/v1/media/holiday%20photo.jpg/still?v=abc&w=1080"

        assertEquals(
            "https://192.168.1.42:18611/api/v1/media/holiday%20photo.jpg/still?v=abc&w=1080",
            MediaUrls.resolve(base, link),
        )
    }

    @Test
    fun `an absolute link is left alone`() {
        val absolute = "https://elsewhere:18611/api/v1/media/x%20y.jpg/still?v=abc"
        assertEquals(absolute, MediaUrls.resolve("https://192.168.1.42:18611/api/v1", absolute))
    }

    @Test
    fun `a relative link hangs off the base path`() {
        assertEquals(
            "https://h:1/api/v1/media/x.jpg/still",
            MediaUrls.resolve("https://h:1/api/v1", "media/x.jpg/still"),
        )
        assertEquals(
            "https://h:1/api/v1/media/x.jpg/still",
            MediaUrls.resolve("https://h:1/api/v1/", "media/x.jpg/still"),
        )
    }

    @Test
    fun `the origin is the host and port, never the api path`() {
        assertEquals("https://192.168.1.42:18611", MediaUrls.origin("https://192.168.1.42:18611/api/v1"))
        assertEquals("https://192.168.1.42:18611", MediaUrls.origin("https://192.168.1.42:18611"))
    }
}
