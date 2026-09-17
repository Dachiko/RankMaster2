package com.rankmaster2.phone.pairing

import com.rankmaster2.phone.ui.pairing.PairingPayload
import com.rankmaster2.phone.ui.pairing.PairingPayloads
import com.rankmaster2.phone.ui.pairing.PayloadRejection
import com.rankmaster2.phone.ui.pairing.PayloadResult
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The payload parser is the one place in this feature where a string a stranger could have printed
 * on a sticker becomes a machine this phone will trust for good, so it is tested harder than
 * anything else here.
 *
 * Format per `RankMaster2.Server/Security/PairingService.cs`:
 * `rm2://pair?v=1&host=…&port=…&fp=<64 lowercase hex>&code=418250&exp=<unix seconds>`
 */
class PairingPayloadTest {

    private val fp = "3b1f".repeat(16) // 64 hex characters
    private val now = 1_789_000_000L
    private val soon = now + 240

    private fun payload(
        v: String = "1",
        host: String = "192.168.1.42",
        port: String = "18611",
        fingerprint: String = fp,
        code: String = "418250",
        exp: String = soon.toString(),
        extra: String = "",
    ) = "rm2://pair?v=$v&host=$host&port=$port&fp=$fingerprint&code=$code&exp=$exp$extra"

    private fun parse(raw: String, at: Long = now) = PairingPayloads.parse(raw, at)

    private fun ok(raw: String, at: Long = now): PairingPayload {
        val result = parse(raw, at)
        assertTrue("expected Ok, got $result", result is PayloadResult.Ok)
        return (result as PayloadResult.Ok).payload
    }

    private fun rejection(raw: String, at: Long = now): PayloadRejection {
        val result = parse(raw, at)
        assertTrue("expected Rejected, got $result", result is PayloadResult.Rejected)
        return (result as PayloadResult.Rejected).reason
    }

    // -- the happy path --------------------------------------------------------------------------

    @Test
    fun `a valid payload parses into every field`() {
        val parsed = ok(payload())

        assertEquals("192.168.1.42", parsed.host)
        assertEquals(18611, parsed.port)
        assertEquals(fp, parsed.fingerprint)
        assertEquals("418250", parsed.code)
        assertEquals(soon, parsed.expiresAtEpochSeconds)
        assertEquals("https://192.168.1.42:18611/api/v1", parsed.baseUrl)
    }

    @Test
    fun `the QR carries bare hex and the store wants the sha256 prefix`() {
        val parsed = ok(payload())
        assertEquals("sha256:$fp", parsed.pinnedFingerprint)
    }

    @Test
    fun `the code is displayed the way the PC shows it`() {
        assertEquals("418 250", ok(payload()).codeDisplay)
    }

    @Test
    fun `toString never carries the code`() {
        val text = ok(payload(code = "418250")).toString()
        assertFalse("the bearer secret must not be loggable: $text", text.contains("418250"))
    }

    @Test
    fun `a host name is as good as an address`() {
        assertEquals("rankpc.local", ok(payload(host = "rankpc.local")).host)
    }

    // -- version ---------------------------------------------------------------------------------

    @Test
    fun `a version this app does not know is refused`() {
        val reason = rejection(payload(v = "2"))
        assertTrue(reason is PayloadRejection.UnsupportedVersion)
        assertEquals("2", (reason as PayloadRejection.UnsupportedVersion).found)
    }

    @Test
    fun `a non-numeric version is refused as a version, not parsed around`() {
        assertTrue(rejection(payload(v = "next")) is PayloadRejection.UnsupportedVersion)
    }

    @Test
    fun `a missing version is refused`() {
        val raw = "rm2://pair?host=10.0.0.5&port=18611&fp=$fp&code=418250&exp=$soon"
        assertEquals(PayloadRejection.MissingField("v"), rejection(raw))
    }

    @Test
    fun `the version is checked before anything else, so a v2 payload with rubbish in it still reads as v2`() {
        val raw = "rm2://pair?v=2&host=&port=zero&fp=nope&code=x&exp=x"
        assertTrue(rejection(raw) is PayloadRejection.UnsupportedVersion)
    }

    // -- fingerprint -----------------------------------------------------------------------------

    @Test
    fun `a missing fingerprint is refused`() {
        val raw = "rm2://pair?v=1&host=10.0.0.5&port=18611&code=418250&exp=$soon"
        assertEquals(PayloadRejection.MissingField("fp"), rejection(raw))
    }

    @Test
    fun `an empty fingerprint is refused`() {
        assertEquals(PayloadRejection.MissingField("fp"), rejection(payload(fingerprint = "")))
    }

    @Test
    fun `a fingerprint one character short is refused`() {
        val reason = rejection(payload(fingerprint = fp.dropLast(1)))
        assertTrue(reason is PayloadRejection.MalformedField)
        assertEquals("fp", (reason as PayloadRejection.MalformedField).field)
    }

    @Test
    fun `a fingerprint one character long is refused`() {
        val reason = rejection(payload(fingerprint = fp + "a"))
        assertEquals("fp", (reason as PayloadRejection.MalformedField).field)
    }

    @Test
    fun `a fingerprint of the right length but not hex is refused`() {
        val reason = rejection(payload(fingerprint = "z".repeat(64)))
        assertEquals("fp", (reason as PayloadRejection.MalformedField).field)
    }

    @Test
    fun `an upper-case fingerprint is normalised, not refused`() {
        assertEquals(fp, ok(payload(fingerprint = fp.uppercase())).fingerprint)
    }

    @Test
    fun `a fingerprint copied with its sha256 prefix still names the same machine`() {
        assertEquals(fp, ok(payload(fingerprint = "sha256:$fp")).fingerprint)
    }

    // -- port ------------------------------------------------------------------------------------

    @Test
    fun `a missing port is refused`() {
        val raw = "rm2://pair?v=1&host=10.0.0.5&fp=$fp&code=418250&exp=$soon"
        assertEquals(PayloadRejection.MissingField("port"), rejection(raw))
    }

    @Test
    fun `port zero is not a port`() {
        assertEquals("port", (rejection(payload(port = "0")) as PayloadRejection.MalformedField).field)
    }

    @Test
    fun `a port above 65535 is not a port`() {
        assertEquals("port", (rejection(payload(port = "70000")) as PayloadRejection.MalformedField).field)
    }

    @Test
    fun `a negative port is not a port`() {
        assertEquals("port", (rejection(payload(port = "-1")) as PayloadRejection.MalformedField).field)
    }

    @Test
    fun `a port that is not a number at all is not a port`() {
        assertEquals("port", (rejection(payload(port = "18611x")) as PayloadRejection.MalformedField).field)
    }

    @Test
    fun `the edges of the port range are accepted`() {
        assertEquals(1, ok(payload(port = "1")).port)
        assertEquals(65535, ok(payload(port = "65535")).port)
    }

    // -- expiry ----------------------------------------------------------------------------------

    @Test
    fun `a window that closed a second ago is expired`() {
        val reason = rejection(payload(exp = (now - 1).toString()))
        assertTrue(reason is PayloadRejection.Expired)
        assertEquals(now - 1, (reason as PayloadRejection.Expired).expiredAtEpochSeconds)
    }

    @Test
    fun `a window that closes exactly now is expired`() {
        assertTrue(rejection(payload(exp = now.toString())) is PayloadRejection.Expired)
    }

    @Test
    fun `a window with a second left is not expired`() {
        assertEquals(now + 1, ok(payload(exp = (now + 1).toString())).expiresAtEpochSeconds)
    }

    @Test
    fun `an expiry that is not a number is refused`() {
        assertEquals("exp", (rejection(payload(exp = "soon")) as PayloadRejection.MalformedField).field)
    }

    @Test
    fun `a missing expiry is refused`() {
        val raw = "rm2://pair?v=1&host=10.0.0.5&port=18611&fp=$fp&code=418250"
        assertEquals(PayloadRejection.MissingField("exp"), rejection(raw))
    }

    // -- the code --------------------------------------------------------------------------------

    @Test
    fun `whitespace in the code is ignored, per section 10 11`() {
        assertEquals("418250", ok(payload(code = "418 250")).code)
        assertEquals("418250", ok(payload(code = "418%20250")).code)
        assertEquals("418250", ok(payload(code = "418+250")).code)
    }

    @Test
    fun `a five-digit code is refused`() {
        assertEquals("code", (rejection(payload(code = "41825")) as PayloadRejection.MalformedField).field)
    }

    @Test
    fun `a seven-digit code is refused`() {
        assertEquals("code", (rejection(payload(code = "4182500")) as PayloadRejection.MalformedField).field)
    }

    @Test
    fun `a code with a letter in it is refused`() {
        assertEquals("code", (rejection(payload(code = "41a250")) as PayloadRejection.MalformedField).field)
    }

    @Test
    fun `an empty code is refused`() {
        assertEquals(PayloadRejection.MissingField("code"), rejection(payload(code = "")))
    }

    // -- the shape of the thing ------------------------------------------------------------------

    @Test
    fun `unknown query parameters are ignored so a later server can add one`() {
        val parsed = ok(payload(extra = "&name=Study%20PC&flavour=stable&v2hint=7"))
        assertEquals("418250", parsed.code)
        assertEquals(fp, parsed.fingerprint)
    }

    @Test
    fun `a repeated parameter is refused rather than resolved`() {
        val reason = rejection(payload(extra = "&fp=" + "aa".repeat(32)))
        assertEquals("query", (reason as PayloadRejection.MalformedField).field)
    }

    @Test
    fun `some other QR code is not a pairing payload`() {
        assertEquals(PayloadRejection.NotAPairingPayload, rejection("https://example.com/?v=1"))
        assertEquals(PayloadRejection.NotAPairingPayload, rejection("WIFI:S:home;T:WPA;P:hunter2;;"))
        assertEquals(PayloadRejection.NotAPairingPayload, rejection(""))
    }

    @Test
    fun `an rm2 URL that is not the pair URL is not a pairing payload`() {
        assertEquals(
            PayloadRejection.NotAPairingPayload,
            rejection("rm2://session?v=1&host=a&port=1&fp=$fp&code=418250&exp=$soon"),
        )
    }

    @Test
    fun `a payload with no query at all is not a pairing payload`() {
        assertEquals(PayloadRejection.NotAPairingPayload, rejection("rm2://pair"))
    }

    @Test
    fun `an upper-cased payload still pairs`() {
        val parsed = ok("RM2://PAIR?v=1&host=10.0.0.5&port=18611&fp=$fp&code=418250&exp=$soon")
        assertEquals("10.0.0.5", parsed.host)
    }

    @Test
    fun `surrounding whitespace from a scan is trimmed`() {
        assertEquals("192.168.1.42", ok("  ${payload()}\n").host)
    }

    // -- the manual fallback ---------------------------------------------------------------------

    @Test
    fun `typed-in details pair, with the fingerprint spaced out as a human would copy it`() {
        val result = PairingPayloads.fromManualEntry(
            host = " 192.168.1.42 ",
            port = " 18611 ",
            fingerprint = fp.chunked(8).joinToString(" ").uppercase(),
            code = "418 250",
        )
        val parsed = (result as PayloadResult.Ok).payload

        assertEquals("192.168.1.42", parsed.host)
        assertEquals(18611, parsed.port)
        assertEquals(fp, parsed.fingerprint)
        assertEquals("418250", parsed.code)
    }

    @Test
    fun `typed-in details carry no expiry, because the user has no way to know it`() {
        val parsed = (
            PairingPayloads.fromManualEntry("10.0.0.5", "18611", fp, "418250") as PayloadResult.Ok
            ).payload
        assertNull(parsed.expiresAtEpochSeconds)
    }

    @Test
    fun `a blank typed-in host is refused`() {
        val result = PairingPayloads.fromManualEntry("   ", "18611", fp, "418250")
        assertEquals(
            PayloadRejection.MissingField("host"),
            (result as PayloadResult.Rejected).reason,
        )
    }

    @Test
    fun `a typed-in host with a slash in it is refused`() {
        val result = PairingPayloads.fromManualEntry("https://10.0.0.5", "18611", fp, "418250")
        val reason = (result as PayloadResult.Rejected).reason
        assertEquals("host", (reason as PayloadRejection.MalformedField).field)
    }

    @Test
    fun `a typed-in fingerprint of the wrong length is refused`() {
        val result = PairingPayloads.fromManualEntry("10.0.0.5", "18611", "3b1f", "418250")
        val reason = (result as PayloadResult.Rejected).reason
        assertEquals("fp", (reason as PayloadRejection.MalformedField).field)
    }

    @Test
    fun `every rejection has something to say to the user`() {
        val reasons = listOf(
            rejection("not a pairing code"),
            rejection(payload(v = "9")),
            rejection(payload(fingerprint = "")),
            rejection(payload(port = "x")),
            rejection(payload(exp = (now - 1).toString())),
        )
        reasons.forEach { assertTrue(it.message.length > 20) }
    }

    @Test
    fun `a host carrying anything but an address is refused`() {
        // Each of these would be spliced into "https://<host>:<port>/api/v1", where it means
        // something other than part of the address - and the best case is a URL the HTTP client
        // refuses outright, which reaches the screen as a crash instead of as an answer.
        listOf("10.0.0.5:18611", "10.0.0.5/api", "10.0.0.5?x=1", "10.0.0.5#f", "user@10.0.0.5")
            .forEach { host ->
                val result = PairingPayloads.fromManualEntry(
                    host = host,
                    port = "18611",
                    fingerprint = "ab".repeat(32),
                    code = "418250",
                )
                val rejected = result as? PayloadResult.Rejected
                    ?: error("“$host” should not be accepted as an address")
                assertEquals("host", (rejected.reason as PayloadRejection.MalformedField).field)
            }
    }

    @Test
    fun `an ordinary name or address is still fine`() {
        listOf("192.168.1.42", "rankpc.local", "rank-pc", "RankPC").forEach { host ->
            val result = PairingPayloads.fromManualEntry(
                host = host,
                port = "18611",
                fingerprint = "ab".repeat(32),
                code = "418250",
            )
            assertTrue("“$host” should be accepted", result is PayloadResult.Ok)
        }
    }

    @Test
    fun `percent-escaped fields decode, and escapes are read as bytes not characters`() {
        // K10. The decoder gathers escapes as bytes and converts once as UTF-8. The fields this
        // payload carries are all ASCII today - an address, a port, a fingerprint, digits - so
        // nothing here can currently arrive non-ASCII; what this pins is that the ordinary escaped
        // case still decodes, and that the rewrite from char-at-a-time did not break it.
        val now = 1_789_000_000L
        val raw = "rm2://pair?v=1&host=rank%2Dpc&port=18611&fp=${"ab".repeat(32)}&code=418250&exp=${now + 60}"

        val result = PairingPayloads.parse(raw, now)

        val ok = result as? PayloadResult.Ok ?: error("expected the escaped host to be accepted, got $result")
        assertEquals("rank-pc", ok.payload.host)
    }

    @Test
    fun `a multi-byte escape becomes one character, not two`() {
        // The defect K10 names: %C3%A9 is one character, e-acute, encoded as two bytes. Reading
        // each byte as a character would yield "A(c)" - two characters of mojibake - and the host
        // check would then be judging a different string from the one the PC encoded. Whichever
        // way the host rule falls, it must fall on the correctly decoded name.
        val now = 1_789_000_000L
        val raw = "rm2://pair?v=1&host=caf%C3%A9&port=18611&fp=${"ab".repeat(32)}&code=418250&exp=${now + 60}"

        when (val result = PairingPayloads.parse(raw, now)) {
            is PayloadResult.Ok -> assertEquals("café", result.payload.host)
            is PayloadResult.Rejected -> assertEquals(
                "a non-ASCII host must be judged as the name it really is",
                "host",
                (result.reason as PayloadRejection.MalformedField).field,
            )
        }
    }
}
