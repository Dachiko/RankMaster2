package com.rankmaster2.phone.ui.pairing

/**
 * What the pairing QR code carries, once it has been believed.
 *
 * The format is defined by `RankMaster2.Server/Security/PairingService.cs`, which is the only place
 * it is defined:
 *
 * ```
 * rm2://pair?v=1&host=192.168.1.42&port=18611&fp=<64 lowercase hex>&code=418250&exp=1789000000
 * ```
 *
 * This type only exists for a payload that has already passed every check in [PairingPayloads].
 * There is no half-valid pairing payload: it carries a live credential and points at a machine this
 * phone is about to trust for good, so either all five fields are sound or there is nothing here.
 *
 * [fingerprint] is the bare 64 hex characters the QR carries. [pinnedFingerprint] is the same value
 * in the `sha256:` form `GET /ping` reports and [com.rankmaster2.phone.store.ServerIdentity] stores;
 * the prefix is dropped from the QR only to keep it small (PairingService.cs), and the two forms
 * must never be compared without normalising first.
 */
data class PairingPayload(
    val host: String,
    val port: Int,
    /** 64 lowercase hex characters, no prefix. */
    val fingerprint: String,
    /** Six digits, whitespace already removed (SERVER_SPEC.md § 10.11). */
    val code: String,
    /**
     * Unix seconds at which the pairing window closes, or `null` when the payload was typed by hand
     * and carries no window information. `null` is not "never expires" - it is "this phone does not
     * know", and the server is still the thing that decides (`403 pairing_not_open`).
     */
    val expiresAtEpochSeconds: Long?,
) {
    /** The form `/ping` reports and the credential store keeps. */
    val pinnedFingerprint: String get() = "sha256:$fingerprint"

    val baseUrl: String get() = "https://$host:$port/api/v1"

    /** The code as a human reads it off the PC screen: `418 250`. */
    val codeDisplay: String get() =
        if (code.length == 6) code.substring(0, 3) + " " + code.substring(3) else code

    /**
     * Deliberately not the default [toString]. This object holds a bearer secret, and the one thing
     * that must never reach a log is the six digits.
     */
    override fun toString(): String =
        "PairingPayload(host=$host, port=$port, fp=${fingerprint.take(8)}…, code=******, " +
            "exp=$expiresAtEpochSeconds)"
}

/** Why a payload was not believed. Every case is something the screen can say out loud. */
sealed interface PayloadRejection {

    /** What to show the user. */
    val message: String

    /** Not an `rm2://pair` URL at all - most likely a QR code from something else entirely. */
    data object NotAPairingPayload : PayloadRejection {
        override val message: String =
            "That is not a Rank Master pairing code. Scan the QR code shown by the PC."
    }

    /**
     * § 10.1.1 / PairingService.cs: "a client MUST refuse a version it does not know". A newer
     * server may mean any field here, including `fp`, means something else.
     */
    data class UnsupportedVersion(val found: String) : PayloadRejection {
        override val message: String =
            "This pairing code is version $found and this app only understands version 1. " +
                "Update the phone app to match the PC."
    }

    data class MissingField(val field: String) : PayloadRejection {
        override val message: String = "The pairing code is incomplete: no “$field”."
    }

    data class MalformedField(val field: String, val why: String) : PayloadRejection {
        override val message: String = "The pairing code's “$field” is not usable: $why"
    }

    /** The window has already closed. Open a new one from the PC's tray icon. */
    data class Expired(val expiredAtEpochSeconds: Long) : PayloadRejection {
        override val message: String =
            "That pairing code has expired. A pairing window lasts five minutes - " +
                "open a new one from the Rank Master icon in the PC's system tray."
    }
}

sealed interface PayloadResult {
    data class Ok(val payload: PairingPayload) : PayloadResult
    data class Rejected(val reason: PayloadRejection) : PayloadResult
}

/**
 * The parser. Pure, synchronous and the most heavily tested thing in this feature, because it is
 * the one place where a hostile string becomes a machine this phone will trust.
 */
object PairingPayloads {

    const val SUPPORTED_VERSION = "1"

    private const val PREFIX = "rm2://pair"

    /** Characters that mean something other than "part of the address" in a URL. */
    private const val HOST_FORBIDDEN = "/\\?#@:[]\"'<>"

    private val HEX = Regex("^[0-9a-f]{64}$")
    private val SIX_DIGITS = Regex("^[0-9]{6}$")

    /**
     * Parse a scanned or pasted payload.
     *
     * @param nowEpochSeconds the clock, injected so expiry is testable without waiting five minutes.
     */
    fun parse(raw: String, nowEpochSeconds: Long): PayloadResult {
        val text = raw.trim()
        val separator = text.indexOf('?')
        if (separator < 0) return reject(PayloadRejection.NotAPairingPayload)

        val head = text.substring(0, separator)
        // Scheme and authority are compared case-insensitively; a QR encoder that upper-cases the
        // whole payload (some do, it packs denser) must still pair.
        if (!head.equals(PREFIX, ignoreCase = true)) return reject(PayloadRejection.NotAPairingPayload)

        val query = parseQuery(text.substring(separator + 1))
            ?: return reject(PayloadRejection.MalformedField("query", "it repeats a parameter."))

        // Version first, and before anything else is even looked at: if this is a payload shape
        // this app does not know, every other field in it is a guess.
        val version = query["v"] ?: return reject(PayloadRejection.MissingField("v"))
        if (version != SUPPORTED_VERSION) return reject(PayloadRejection.UnsupportedVersion(version))

        val host = query["host"] ?: return reject(PayloadRejection.MissingField("host"))
        val port = query["port"] ?: return reject(PayloadRejection.MissingField("port"))
        val fingerprint = query["fp"] ?: return reject(PayloadRejection.MissingField("fp"))
        val code = query["code"] ?: return reject(PayloadRejection.MissingField("code"))
        val expiry = query["exp"] ?: return reject(PayloadRejection.MissingField("exp"))

        val expiresAt = expiry.trim().toLongOrNull()
            ?: return reject(PayloadRejection.MalformedField("exp", "it is not a number."))
        if (expiresAt <= nowEpochSeconds) return reject(PayloadRejection.Expired(expiresAt))

        // Unknown parameters are ignored on purpose: a later server may add one, and a phone that
        // refuses to pair because of a field it did not need is a phone that cannot be updated.
        return build(host, port, fingerprint, code, expiresAt)
    }

    /**
     * The manual fallback, for when the camera will not cooperate. Same validation as [parse] minus
     * the expiry, which the user has no way to type and no reason to know: if the window has closed,
     * the server says `pairing_not_open` and the screen says so.
     */
    fun fromManualEntry(
        host: String,
        port: String,
        fingerprint: String,
        code: String,
    ): PayloadResult = build(host, port, fingerprint, code, expiresAt = null)

    private fun build(
        host: String,
        port: String,
        fingerprint: String,
        code: String,
        expiresAt: Long?,
    ): PayloadResult {
        val cleanHost = host.trim()
        if (cleanHost.isEmpty()) return reject(PayloadRejection.MissingField("host"))
        // A bare host and nothing else. Everything rejected here would go on to be spliced into
        // `https://<host>:<port>/api/v1`, where a `/`, `?`, `#`, `@` or `:` does not mean what it
        // looks like it means - and where the least bad outcome is a URL the HTTP client refuses
        // to parse at all, which reaches the screen as a crash rather than as an answer.
        if (cleanHost.any { it.isWhitespace() || it in HOST_FORBIDDEN }) {
            return reject(
                PayloadRejection.MalformedField(
                    "host",
                    "“$cleanHost” is not an address. It should be just the name or the " +
                        "numbers, like 192.168.1.42 - no slashes, no port, nothing else."
                )
            )
        }

        val cleanPort = port.trim().toIntOrNull()
        if (cleanPort == null || cleanPort !in 1..65535) {
            return reject(
                PayloadRejection.MalformedField("port", "“${port.trim()}” is not a port number.")
            )
        }

        // Liberal in what it accepts: the QR carries bare hex, but a fingerprint copied out of
        // /ping or off the tray icon carries the sha256: prefix, and both name the same machine.
        val cleanFingerprint = fingerprint
            .filterNot { it.isWhitespace() }
            .lowercase()
            .removePrefix("sha256:")
        if (cleanFingerprint.isEmpty()) return reject(PayloadRejection.MissingField("fp"))
        if (!HEX.matches(cleanFingerprint)) {
            return reject(
                PayloadRejection.MalformedField(
                    "fp",
                    "a certificate fingerprint is 64 hex characters; this one is " +
                        "${cleanFingerprint.length}."
                )
            )
        }

        // § 10.11: whitespace in the code is ignored when comparing, so "418 250" is the same code
        // as "418250" - which matters, because that is how the PC displays it.
        val cleanCode = code.filterNot { it.isWhitespace() }
        if (cleanCode.isEmpty()) return reject(PayloadRejection.MissingField("code"))
        if (!SIX_DIGITS.matches(cleanCode)) {
            return reject(PayloadRejection.MalformedField("code", "a pairing code is six digits."))
        }

        return PayloadResult.Ok(
            PairingPayload(
                host = cleanHost,
                port = cleanPort,
                fingerprint = cleanFingerprint,
                code = cleanCode,
                expiresAtEpochSeconds = expiresAt,
            )
        )
    }

    private fun reject(reason: PayloadRejection): PayloadResult = PayloadResult.Rejected(reason)

    /**
     * Returns null when a parameter appears twice. In a payload that carries a credential, an
     * ambiguous field is not something to resolve by a first-wins or last-wins rule; the two
     * choices disagree about which machine to trust, and that is the whole attack.
     */
    private fun parseQuery(query: String): Map<String, String>? {
        val out = HashMap<String, String>()
        for (part in query.split('&')) {
            if (part.isEmpty()) continue
            val eq = part.indexOf('=')
            val key = if (eq < 0) part else part.substring(0, eq)
            val value = if (eq < 0) "" else part.substring(eq + 1)
            val decodedKey = decode(key)
            if (out.put(decodedKey, decode(value)) != null) return null
        }
        return out
    }

    /** Percent-decoding, by hand: `java.net.URLDecoder` is not on the phone's critical path. */
    private fun decode(value: String): String {
        if (!value.contains('%') && !value.contains('+')) return value
        val out = StringBuilder(value.length)
        var i = 0
        while (i < value.length) {
            val c = value[i]
            when {
                c == '+' -> { out.append(' '); i++ }
                c == '%' && i + 2 < value.length -> {
                    val hex = value.substring(i + 1, i + 3).toIntOrNull(16)
                    if (hex == null) { out.append(c); i++ } else { out.append(hex.toChar()); i += 3 }
                }
                else -> { out.append(c); i++ }
            }
        }
        return out.toString()
    }
}
