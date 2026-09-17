package com.rankmaster2.phone.ui.pairing

import com.rankmaster2.phone.net.ErrorCodes
import com.rankmaster2.phone.net.PairedDevice
import com.rankmaster2.phone.net.Rm2Client
import com.rankmaster2.phone.net.Rm2Result
import com.rankmaster2.phone.store.Credentials
import com.rankmaster2.phone.store.ServerIdentity

/**
 * Builds the client this phone talks to while pairing: pinned to the fingerprint the QR carried,
 * carrying no token, because none exists yet.
 *
 * It is an interface and not a direct call so that the pairing sequence can be driven by a fake in
 * a unit test - the one place where "the ping fingerprint did not match, so `pair` was never
 * called" can actually be asserted rather than asserted about.
 *
 * Integration wires the real one in one line: `Rm2Http.pairingClient(fingerprint)` is the pinning,
 * token-less OkHttp client (`net/Rm2Http.kt`), and the `Rm2Client` implementation in `net/impl/`
 * wraps it for `https://host:port/api/v1`:
 *
 * ```
 * PairingClientFactory { host, port, fingerprint ->
 *     OkHttpRm2Client(
 *         baseUrl = "https://$host:$port/api/v1",
 *         http = Rm2Http.pairingClient(fingerprint),
 *     )
 * }
 * ```
 *
 * This file deliberately does not name that implementation. `net/impl/` belongs to someone else,
 * and the pairing screen only ever needs the interface (SERVER_SPEC.md § 10.1.1 makes pinning-first
 * normative; which class does the pinning is not this screen's business).
 */
fun interface PairingClientFactory {

    /** A pinned, unauthenticated client pointed at `https://host:port/api/v1`. */
    fun create(host: String, port: Int, fingerprint: String): Rm2Client
}

/** How the pairing attempt ended. Every case is a screen the user can act on. */
sealed interface PairingOutcome {

    /** Paired, and the credentials are already stored. */
    data class Paired(val identity: ServerIdentity, val device: PairedDevice) : PairingOutcome

    /**
     * `401 invalid_pairing_code` - wrong, already used, or expired; the server refuses to say which,
     * on purpose. [attemptsRemaining] is `details.attemptsRemaining` when it reached us: the window
     * carries a budget of five attempts in total, and on zero it is destroyed (§ 10.11).
     */
    data class InvalidCode(val attemptsRemaining: Int?) : PairingOutcome

    /** `403 pairing_not_open` - nothing to pair with. A window is opened from the PC, not from here. */
    data object NotOpen : PairingOutcome

    /** `429 too_many_requests` - five attempts per minute per address (§ 15). */
    data class RateLimited(val retryAfterSeconds: Int?) : PairingOutcome

    /**
     * The server presented a certificate that is not the pinned one - either the TLS handshake was
     * refused by the pin, or `/ping` reported a different fingerprint than the QR promised.
     *
     * This is a dead end by design. On a home network there is no innocent reason for it, and the
     * one guilty reason is precisely the attack the pin exists to stop. Nothing in this app offers
     * a way past it, and the pairing code was never sent.
     */
    data class CertificateMismatch(val expected: String, val found: String?) : PairingOutcome

    /** Nothing answered: wrong address, PC asleep, different network. */
    data class Unreachable(val detail: String) : PairingOutcome

    /** The server answered with a code this screen has nothing specific to say about. */
    data class Refused(val code: String, val message: String) : PairingOutcome

    /**
     * A29. The payload's `host` is a loopback address (`127.0.0.1`, `::1`, `localhost`) - the tray
     * binds there by default, and the QR it shows then carries that address whether or not the PC
     * is reachable on the LAN at all. Reported before the ping is even attempted: on a phone,
     * `127.0.0.1` names the phone itself, so the connection would either fail (nothing answers, and
     * the phone's own failure text would blame Wi-Fi for a problem that is on the PC) or - if the
     * phone happens to run something on that port - answer for a completely different machine.
     */
    data object LoopbackHost : PairingOutcome
}

/**
 * The pairing sequence, in the order SERVER_SPEC.md § 10.1.1 makes normative:
 *
 *  1. pin the fingerprint the payload carried,
 *  2. `GET /ping`, and check the `certificateFingerprint` it reports is that same one,
 *  3. **only then** `POST /pair` with the code,
 *  4. store host, port, fingerprint, token and deviceId.
 *
 * Step 3 never runs unless step 2 succeeded. The pairing code is a bearer secret: handing it to an
 * unverified peer hands it to whoever answered, so a mismatch has to fail *before* the code leaves
 * the phone, not after the request that carried it comes back.
 *
 * Step 2 is not redundant next to the pin. The pin makes a wrong certificate fail the handshake;
 * the `/ping` comparison catches the subtler case where the pinned string and the QR's string have
 * drifted apart in the app's own hands - a normalisation bug, a stale field, a payload parsed into
 * the wrong slot - and it costs one cheap unauthenticated request.
 */
class PairingFlow(
    private val clients: PairingClientFactory,
    private val credentials: Credentials,
) {

    suspend fun pair(payload: PairingPayload, deviceName: String): PairingOutcome {
        // A29: caught before anything is sent, not after a ping that was never going to answer.
        // `host=127.0.0.1` on the QR means the tray is bound to loopback, which is on the PC and
        // never reachable from the phone that just scanned it.
        if (isLoopback(payload.host)) return PairingOutcome.LoopbackHost

        // 1. Pinned before anything is sent. No token: there is none yet.
        val client = clients.create(payload.host, payload.port, payload.fingerprint)

        // 2. Does the machine that answered present the certificate the QR promised?
        when (val ping = client.ping()) {
            is Rm2Result.Unreachable ->
                return if (ping.pinMismatch) {
                    PairingOutcome.CertificateMismatch(payload.fingerprint, null)
                } else {
                    PairingOutcome.Unreachable(ping.detail)
                }

            is Rm2Result.Refused -> return refusal(ping)

            is Rm2Result.Ok -> {
                val reported = normalise(ping.value.certificateFingerprint)
                if (reported != payload.fingerprint) {
                    // Stop here. The code stays on the phone.
                    return PairingOutcome.CertificateMismatch(payload.fingerprint, reported)
                }
                if (!ping.value.ready) {
                    return PairingOutcome.Unreachable(
                        "The PC answered but is still starting up. Try again in a moment."
                    )
                }
            }
        }

        // 3. The peer is the right machine. Only now does the code leave the phone.
        return when (val paired = client.pair(payload.code, deviceName)) {
            is Rm2Result.Unreachable ->
                if (paired.pinMismatch) {
                    PairingOutcome.CertificateMismatch(payload.fingerprint, null)
                } else {
                    PairingOutcome.Unreachable(paired.detail)
                }

            is Rm2Result.Refused -> refusal(paired)

            is Rm2Result.Ok -> {
                // 4. Store exactly what came back, against the address and pin we verified.
                val identity = ServerIdentity(
                    host = payload.host,
                    port = payload.port,
                    certificateFingerprint = payload.pinnedFingerprint,
                    token = paired.value.token,
                    deviceId = paired.value.deviceId,
                )
                credentials.save(identity)
                PairingOutcome.Paired(identity, paired.value)
            }
        }
    }

    /**
     * A19. `attemptsRemaining` and `retryAfterSeconds` are structured payload (§ 5, § 5.1, § 15),
     * not text folded into [Rm2Result.Refused.message] - the server never puts them there, so a
     * regex over the message read nothing on the real wire and this screen never showed "N attempts
     * left". `Rm2Result.Refused.detailInt` reads `details` by name, which is where they actually are.
     */
    private fun refusal(refused: Rm2Result.Refused): PairingOutcome = when (refused.code) {
        ErrorCodes.INVALID_PAIRING_CODE ->
            PairingOutcome.InvalidCode(refused.detailInt("attemptsRemaining"))

        ErrorCodes.PAIRING_NOT_OPEN -> PairingOutcome.NotOpen

        ErrorCodes.TOO_MANY_REQUESTS ->
            PairingOutcome.RateLimited(refused.detailInt("retryAfterSeconds"))

        else -> PairingOutcome.Refused(refused.code, refused.message)
    }

    /** `/ping` reports `sha256:<hex>`; the QR carries bare `<hex>`. Compare like with like. */
    private fun normalise(fingerprint: String): String =
        fingerprint.trim().lowercase().removePrefix("sha256:")

    /**
     * A29. `127.0.0.1`, `::1` (and its zero-compressed and bracketed forms) and `localhost` all
     * name "this machine" - which, read from a QR code on a phone, means the phone, never the PC
     * that showed it. [PairingPayloads] already trims and lower-cases `host`, but this is cheap
     * enough not to depend on that.
     */
    private fun isLoopback(host: String): Boolean {
        val h = host.trim().trim('[', ']').lowercase()
        return h == "127.0.0.1" || h == "localhost" || h == "::1" || h == "0:0:0:0:0:0:0:1" ||
            h.startsWith("127.")
    }
}
