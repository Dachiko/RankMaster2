package com.rankmaster2.phone.ui.rank

import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import com.rankmaster2.phone.net.ErrorCodes
import com.rankmaster2.phone.net.Rm2Client
import com.rankmaster2.phone.net.Rm2Result
import com.rankmaster2.phone.net.Snapshot
import java.util.UUID
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

/**
 * The ranking screen's brain, and the one place in this app where a mistake costs the owner data.
 *
 * ## The rule everything here exists to keep
 *
 * `SERVER_SPEC.md` § 13.3: an action that times out is retried **with the same `pairToken` and the
 * same `clientRequestId`**. If the first attempt landed, the retry is answered `409
 * stale_pair_token` carrying the current state, and the client adopts it. Retrying after resyncing
 * — with a fresh token — applies the action a second time. That is a double vote, and it is silent.
 *
 * So: the retry lives *inside* [act], where the token cannot have changed between attempts. There
 * is no retry button anywhere in the UI, and no path from a failure back to "send it again" that
 * could pick up a new token on the way.
 *
 * One action at a time, for the same reason — [RankState.busy] gates the gestures, so two taps in
 * quick succession cannot become two different actions racing on one token.
 */
class RankViewModel(
    private val client: Rm2Client,
    opened: Snapshot,
    private val newRequestId: () -> String = { UUID.randomUUID().toString() },
    private val scope: CoroutineScope? = null,
) : ViewModel() {

    private val _state = MutableStateFlow(RankState(snapshot = opened))
    val state: StateFlow<RankState> = _state.asStateFlow()

    private val where: CoroutineScope get() = scope ?: viewModelScope

    // -- what the screen calls -------------------------------------------------------------------

    fun vote(side: Side) = act { token, id -> client.vote(token, side.wire, id) }

    fun skip() = act { token, id -> client.skip(token, id) }

    fun discard(side: Side) = act { token, id -> client.discard(token, side.wire, id) }

    fun special(side: Side) = act { token, id -> client.special(token, side.wire, id) }

    /**
     * § 10.10. Takes no token — the action it reverses belongs to an earlier pair generation, so any
     * token held for it is stale by construction.
     */
    fun cancel() {
        if (_state.value.busy) return
        _state.update { it.copy(busy = true, paneMenu = null) }

        where.launch {
            val id = newRequestId()
            finish(client.cancel(id))
        }
    }

    fun save() {
        if (_state.value.busy) return
        _state.update { it.copy(busy = true) }

        where.launch {
            when (val result = client.save()) {
                is Rm2Result.Ok -> _state.update {
                    it.copy(snapshot = result.value, busy = false, notice = "Saved")
                }
                else -> finish(result)
            }
        }
    }

    /**
     * Adopt the snapshot the folder was just opened with, if it is not the one already held.
     *
     * View models here outlive the screen - they live in the activity's store, keyed by session -
     * and SERVER_SPEC.md section 10.1 says re-opening the *same* folder resumes and returns the
     * same `sessionId` rather than a new one. So a phone whose `DELETE /session` was lost to a
     * network blip, opening that folder again, gets handed back the view model from before,
     * holding whatever the screen was showing when it left: a pair that has moved on, and possibly
     * a `busy` flag from an action nobody is waiting for any more.
     *
     * This is not a resync and never sends anything. It takes the snapshot `POST /session` just
     * answered with - which is the live state by definition - and starts from it.
     */
    fun resume(opened: Snapshot) {
        val held = _state.value.snapshot
        if (held != null && held.sessionId == opened.sessionId && held.pairSeq >= opened.pairSeq) {
            return
        }
        _state.update {
            it.copy(snapshot = opened, busy = false, problem = null, notice = null, paneMenu = null)
        }
    }

    /** A plain read. Used when the screen comes back to the front, never to recover from a failure. */
    fun refresh() {
        if (_state.value.busy) return
        where.launch { finish(client.session(), quiet = true) }
    }

    /**
     * Closes the session before leaving.
     *
     * Without this the PC keeps the folder open and locked after the phone walks away, and the next
     * folder the owner picks is refused with "already ranking another folder" - which is exactly
     * what happened the first time this app was used on a real library.
     */
    fun leave(then: () -> Unit) {
        // Navigate first. Waiting for the PC to answer makes back look broken on a slow network,
        // and the close is housekeeping - if it fails, the next open closes the stale session
        // anyway (§ 10.1 / browse).
        then()
        where.launch { client.closeSession() }
    }

    fun view(side: Side?) = _state.update { it.copy(viewing = side, paneMenu = null) }

    fun openPaneMenu(side: Side) = _state.update { it.copy(paneMenu = side) }
    fun closePaneMenu() = _state.update { it.copy(paneMenu = null) }
    fun dismissNotice() = _state.update { it.copy(notice = null) }
    fun dismissProblem() = _state.update { if (it.problem?.fatal == true) it else it.copy(problem = null) }

    fun onForeground(inFront: Boolean) {
        _state.update { it.copy(foreground = inFront) }
        if (inFront) refresh()
    }

    // -- the one path every action takes ----------------------------------------------------------

    /**
     * Sends an action, and on a network failure sends **the identical request** once more.
     *
     * The token and the request id are captured before the first attempt and reused, so the second
     * attempt is the same logical action rather than a new one. Whichever way it lands, the server
     * answers with the state — a success carries the new snapshot, a `409 stale_pair_token` carries
     * the current one — so the screen never has to ask a second question to find out what happened.
     */
    private fun act(send: suspend (token: String, requestId: String) -> Rm2Result<Snapshot>) {
        val current = _state.value
        if (current.busy) return
        val token = current.snapshot?.pairToken ?: return

        _state.update { it.copy(busy = true, paneMenu = null, notice = null) }

        where.launch {
            val requestId = newRequestId()

            val first = send(token, requestId)
            val result = if (first is Rm2Result.Unreachable && !first.pinMismatch) {
                // The same token, the same id, once. If the first attempt landed after all, this
                // one is refused as stale and brings the truth back with it.
                send(token, requestId)
            } else {
                first
            }

            finish(result)
        }
    }

    /**
     * Turns any answer into the next state.
     *
     * A refusal that carries a snapshot is not an error the owner needs to see — it is the server
     * correcting us, and the correction is free (§ 8.5). Only a refusal with nothing attached, or
     * silence, becomes something to read.
     */
    private fun finish(result: Rm2Result<Snapshot>, quiet: Boolean = false) {
        when (result) {
            is Rm2Result.Ok -> _state.update {
                it.copy(
                    snapshot = result.value,
                    busy = false,
                    notice = if (quiet) it.notice else noticeFor(result.value.lastAction),
                    problem = null,
                )
            }

            is Rm2Result.Refused -> {
                val carried = result.session
                if (carried != null) {
                    // Resynchronised in one round trip. A stale token means our action did not
                    // apply *this* time round, and the snapshot says what is true now.
                    _state.update { it.copy(snapshot = carried, busy = false, problem = null) }
                    return
                }

                _state.update { it.copy(busy = false, problem = problemFor(result)) }
            }

            is Rm2Result.Unreachable -> _state.update {
                it.copy(busy = false, problem = problemFor(result))
            }
        }
    }

    private fun problemFor(result: Rm2Result.Refused): RankState.Problem = when (result.code) {
        ErrorCodes.NO_SESSION -> RankState.Problem(
            "The folder is no longer open",
            "The PC restarted, or something else took the folder. Everything ranked so far is " +
                "already saved. Go back and open the folder again to carry on where you stopped.",
            fatal = true,
        )

        ErrorCodes.NOTHING_TO_UNDO -> RankState.Problem(
            "Nothing to take back",
            "The last action has already been cancelled, or there has not been one yet.",
        )

        ErrorCodes.SAVE_FAILED -> RankState.Problem(
            "The PC could not write the ranking file",
            "Nothing was lost - it is all still in the PC's memory. The usual cause is the drive " +
                "being unplugged or the folder being read-only. " + result.message,
        )

        ErrorCodes.MOVE_FAILED -> RankState.Problem(
            "The file could not be moved",
            "Nothing changed. " + result.message,
        )

        ErrorCodes.SESSION_BUSY -> RankState.Problem(
            "The PC is busy",
            "It is part-way through something else in this folder. Close this and tap again in a " +
                "moment; nothing was lost.",
        )

        ErrorCodes.TOKEN_REVOKED, ErrorCodes.INVALID_TOKEN, ErrorCodes.UNAUTHENTICATED ->
            RankState.Problem(
                "This phone is no longer paired with the PC",
                "Either the PC was given a new security certificate, or this phone was removed " +
                    "from its list. Nothing is lost. Go back to the folder list, where there is " +
                    "a button to pair this phone again.",
                fatal = true,
            )

        else -> RankState.Problem(
            "The PC refused that",
            result.message.ifBlank { "Error code: ${result.code}" },
        )
    }

    private fun problemFor(result: Rm2Result.Unreachable): RankState.Problem =
        if (result.pinMismatch) {
            RankState.Problem(
                "That is not your PC",
                "Something answered with a certificate this phone has not paired with. Nothing was " +
                    "sent to it. Do not continue on this network.",
                fatal = true,
            )
        } else {
            RankState.Problem(
                "Cannot reach the PC",
                "Nothing was lost - everything ranked so far is already on the PC's disk, and " +
                    "this last one did not count. Check the PC is awake and on the same Wi-Fi, " +
                    "then close this and tap the same picture again.",
            )
        }

    companion object {
        fun factory(client: Rm2Client, opened: Snapshot): ViewModelProvider.Factory =
            viewModelFactory { initializer { RankViewModel(client, opened) } }
    }
}
