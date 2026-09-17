package com.rankmaster2.phone.ui.pairing

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.rankmaster2.phone.store.ServerIdentity
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

/** Which way the user is trying to pair. */
enum class PairingMode { Scan, Manual }

/** The four fields of the manual fallback, exactly as typed. */
data class ManualEntry(
    val host: String = "",
    val port: String = "18611",
    val fingerprint: String = "",
    val code: String = "",
)

/**
 * A failure the user can read. [fatal] is the difference between "try again" and "stop":
 * a certificate mismatch is fatal, and there is deliberately no way past it.
 */
data class PairingFailure(
    val title: String,
    val body: String,
    val fatal: Boolean = false,
)

data class PairingUiState(
    val mode: PairingMode = PairingMode.Scan,
    val manual: ManualEntry = ManualEntry(),
    /** True from the moment a payload is accepted until the server has answered. */
    val busy: Boolean = false,
    val failure: PairingFailure? = null,
    val paired: ServerIdentity? = null,
) {
    /** The scanner stops feeding payloads while a pairing is in flight or has already failed hard. */
    val scannerActive: Boolean
        get() = mode == PairingMode.Scan && !busy && paired == null && failure?.fatal != true
}

/**
 * The pairing screen's brain. Holds no Android types and no Compose types, so the whole of the
 * screen's behaviour - including "a certificate mismatch never sends the code" - is testable on a
 * JVM without a device.
 *
 * @param deviceName what the PC will list this phone as; `Build.MODEL` at the call site.
 * @param now unix seconds, injected so an expired payload can be tested without waiting.
 */
class PairingViewModel(
    private val flow: PairingFlow,
    private val deviceName: String,
    private val now: () -> Long = { System.currentTimeMillis() / 1000 },
) : ViewModel() {

    private val _state = MutableStateFlow(PairingUiState())
    val state: StateFlow<PairingUiState> = _state.asStateFlow()

    fun showScanner() {
        _state.update { it.copy(mode = PairingMode.Scan, failure = null) }
    }

    fun showManualEntry() {
        _state.update { it.copy(mode = PairingMode.Manual, failure = null) }
    }

    fun onManualChanged(entry: ManualEntry) {
        _state.update { it.copy(manual = entry) }
    }

    fun dismissFailure() {
        // A fatal failure is not dismissable. There is nothing to go back to: the peer on the other
        // end of that address is not the machine this QR code described.
        _state.update { if (it.failure?.fatal == true) it else it.copy(failure = null) }
    }

    /** A QR code came out of the camera, or a payload was pasted in. */
    fun onPayloadScanned(raw: String) {
        if (!_state.value.scannerActive) return
        when (val parsed = PairingPayloads.parse(raw, now())) {
            is PayloadResult.Rejected -> fail(rejection(parsed.reason))
            is PayloadResult.Ok -> start(parsed.payload)
        }
    }

    /** The manual fallback's "Pair" button. */
    fun onManualSubmit() {
        val state = _state.value
        if (state.busy) return
        val entry = state.manual
        when (
            val parsed = PairingPayloads.fromManualEntry(
                host = entry.host,
                port = entry.port,
                fingerprint = entry.fingerprint,
                code = entry.code,
            )
        ) {
            is PayloadResult.Rejected -> fail(rejection(parsed.reason))
            is PayloadResult.Ok -> start(parsed.payload)
        }
    }

    private fun start(payload: PairingPayload) {
        _state.update { it.copy(busy = true, failure = null) }
        viewModelScope.launch {
            val outcome = flow.pair(payload, deviceName)
            _state.update { current ->
                when (outcome) {
                    is PairingOutcome.Paired ->
                        current.copy(busy = false, failure = null, paired = outcome.identity)
                    else ->
                        current.copy(busy = false, failure = describe(outcome))
                }
            }
        }
    }

    private fun fail(failure: PairingFailure) {
        _state.update { it.copy(busy = false, failure = failure) }
    }

    private fun rejection(reason: PayloadRejection) = PairingFailure(
        title = when (reason) {
            is PayloadRejection.Expired -> "That pairing code has expired"
            is PayloadRejection.UnsupportedVersion -> "This app is too old for that code"
            PayloadRejection.NotAPairingPayload -> "That is not a pairing code"
            else -> "That pairing code is not usable"
        },
        body = reason.message,
    )

    companion object {

        /** The user-facing half of [PairingOutcome]. Public so the screens can be previewed. */
        fun describe(outcome: PairingOutcome): PairingFailure? = when (outcome) {
            is PairingOutcome.Paired -> null

            is PairingOutcome.InvalidCode -> PairingFailure(
                title = "The PC did not accept that code",
                body = buildString {
                    append(
                        "It may be mistyped, already used, or from a window that has since closed. " +
                            "Open a new pairing window on the PC and try the code it shows."
                    )
                    val left = outcome.attemptsRemaining
                    if (left != null) {
                        append("\n\n")
                        append(
                            if (left <= 0) {
                                "That window has no attempts left; it has closed and a new one is needed."
                            } else {
                                "$left attempt${if (left == 1) "" else "s"} left before that window closes."
                            }
                        )
                    } else {
                        append("\n\nA pairing window allows five attempts in total before it closes.")
                    }
                },
            )

            PairingOutcome.NotOpen -> PairingFailure(
                title = "No pairing window is open",
                body = "The PC is not accepting pairings right now. Open one from the Rank Master " +
                    "icon in the PC's system tray, then scan the code it shows. A window lasts " +
                    "five minutes.",
            )

            is PairingOutcome.RateLimited -> PairingFailure(
                title = "Too many tries",
                body = outcome.retryAfterSeconds?.let {
                    "The PC is refusing further attempts for another $it second${if (it == 1) "" else "s"}. Wait, then try again."
                } ?: "The PC is refusing further attempts for a moment. Wait about a minute, then try again.",
            )

            is PairingOutcome.Unreachable -> PairingFailure(
                title = "Cannot reach the PC",
                body = "Nothing answered at that address. Check the PC is awake, that Rank Master " +
                    "is running on it, and that this phone is on the same Wi-Fi network.\n\n" +
                    outcome.detail,
            )

            is PairingOutcome.CertificateMismatch -> PairingFailure(
                title = "That is not your PC",
                body = "Something answered at that address, but it presented a different security " +
                    "certificate than the pairing code promised. On a home network there is no " +
                    "harmless reason for this.\n\nThe pairing code was NOT sent. Stop, check you " +
                    "are on your own network, and generate a fresh pairing code on the PC. If it " +
                    "happens again, someone else is answering for your PC." +
                    (outcome.found?.let { "\n\nExpected ${outcome.expected.take(16)}…, got ${it.take(16)}…" } ?: ""),
                fatal = true,
            )

            is PairingOutcome.Refused -> PairingFailure(
                title = "The PC refused",
                body = "${outcome.message}\n\n(${outcome.code})",
            )

            PairingOutcome.LoopbackHost -> PairingFailure(
                title = "This code won't work over Wi-Fi",
                body = "This code points at the PC itself (127.0.0.1). The server is not " +
                    "listening on the network — see SERVER_RUNNING.md on the PC.",
            )
        }
    }
}
