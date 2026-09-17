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
import kotlinx.coroutines.delay
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
 * So: the retry lives *inside* [act] (and [cancel], which shares it through [sendWithRetries]),
 * where the token cannot have changed between attempts. There is no retry button anywhere in the
 * UI, and no path from a failure back to "send it again" that could pick up a new token on the way.
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
     *
     * A16: unlike a vote, a lost cancel cannot be resolved by a `stale_pair_token` reply, because it
     * carries no token to begin with — the server has no way to tell "already applied" from "never
     * arrived" apart for it. So after the one retry every other action gets, a cancel that is still
     * unreachable says so honestly and folds into a [refresh] rather than guessing.
     */
    fun cancel() {
        if (_state.value.busy) return
        _state.update { it.copy(busy = true, paneMenu = null) }

        where.launch {
            val id = newRequestId()
            val result = sendWithRetries { client.cancel(id) }

            if (result is Rm2Result.Unreachable && !result.pinMismatch) {
                _state.update {
                    it.copy(
                        busy = false,
                        problem = RankState.Problem(
                            "Cannot reach the PC",
                            "The cancel may or may not have landed — the screen shows the current pair.",
                        ),
                    )
                }
                refresh()
            } else {
                finish(result)
            }
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
        // A11: whichever branch this takes, a screen coming back to front must not stay deaf. The
        // early branch below used to return before touching `busy`, so a coroutine that had already
        // given up waiting (an action nobody will ever hear back from) left the gestures dead until
        // the view model was thrown away. Resuming always means "the screen is live again."
        if (held != null && held.sessionId == opened.sessionId && held.pairSeq >= opened.pairSeq) {
            _state.update { it.copy(busy = false) }
            return
        }
        _state.update {
            it.copy(snapshot = opened, busy = false, problem = null, notice = null, paneMenu = null)
        }
    }

    /**
     * A plain read. Used when the screen comes back to the front, never to recover from a failure.
     *
     * A10: this is not gated by `busy` the way [act] is — it is a read, not an action, and holding
     * up "is the app in front again" behind an in-flight vote would make the screen feel stuck. So
     * instead the *answer* is discarded, at the moment it lands, if either: an action started after
     * this read began (`busy` is true again — that action's own [finish] owns the screen now), or
     * the read's own pair generation is older than the one already held (a second refresh landing
     * out of order must never drag a newer screen backwards).
     */
    fun refresh() {
        if (_state.value.busy) return
        where.launch {
            val result = client.session()
            val current = _state.value
            if (current.busy || isOlderThanHeld(result, current.snapshot)) return@launch
            finish(result, quiet = true)
        }
    }

    /** True if [result] carries a snapshot of the same session, but an earlier pair generation. */
    private fun isOlderThanHeld(result: Rm2Result<Snapshot>, held: Snapshot?): Boolean {
        val carried = when (result) {
            is Rm2Result.Ok -> result.value
            is Rm2Result.Refused -> result.session
            is Rm2Result.Unreachable -> null
        } ?: return false
        return held != null && held.sessionId == carried.sessionId && carried.pairSeq < held.pairSeq
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
     * The token and the request id are captured before the first attempt and reused, so every
     * retry is the same logical action rather than a new one. Whichever way it lands, the server
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
            finish(sendWithRetries { send(token, requestId) })
        }
    }

    /**
     * The one retry sequence every action (and cancel) goes through, always resending the exact
     * same call [send] closes over — same token where there is one, same `clientRequestId` always.
     *
     * 1. A lost response (§ 13.3): resend once. If the first attempt had in fact landed, this one
     *    comes back `409 stale_pair_token` carrying the truth, never a double vote.
     * 2. `503 session_busy` (A15): the PC never looked at the request, so resending it is not a
     *    second action — wait the `retryAfterSeconds` the server named (default 1 s) and resend
     *    once more before giving up and letting the caller show the modal.
     */
    private suspend fun sendWithRetries(
        send: suspend () -> Rm2Result<Snapshot>,
    ): Rm2Result<Snapshot> {
        var result = send()
        if (result is Rm2Result.Unreachable && !result.pinMismatch) {
            result = send()
        }
        if (result is Rm2Result.Refused && result.code == ErrorCodes.SESSION_BUSY) {
            delay((result.detailInt("retryAfterSeconds") ?: 1).coerceAtLeast(0) * 1000L)
            result = send()
        }
        return result
    }

    /**
     * Turns any answer into the next state.
     *
     * H6: whether a refusal is silent is decided **by its code**, never by whether a snapshot came
     * with it — the server attaches one to almost every refusal while a session is open (§ 4), so
     * presence proves nothing. Silent iff the code is `stale_pair_token` or `no_current_pair`: both
     * mean "the pair on your screen is not current any more; here is the current one", which is the
     * server correcting us for free (§ 8.5), not something the owner needs to read. Every other
     * refusal shows its text — and still adopts the snapshot it carried, when it carried one, so the
     * screen stays live under the problem panel rather than frozen on a pair that has moved on.
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
                val silent = result.code == ErrorCodes.STALE_PAIR_TOKEN ||
                    result.code == ErrorCodes.NO_CURRENT_PAIR
                _state.update {
                    it.copy(
                        snapshot = result.session ?: it.snapshot,
                        busy = false,
                        problem = if (silent) null else problemFor(result),
                    )
                }
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
            "The last choice did not count; nothing before it is lost. The usual cause is the " +
                "drive being unplugged or the folder being read-only. " + result.message,
        )

        ErrorCodes.MOVE_FAILED -> RankState.Problem(
            "The file could not be moved",
            "Nothing changed. " + result.message,
        )

        ErrorCodes.RENAME_IN_PROGRESS -> RankState.Problem(
            "The PC is renaming this folder",
            "Wait for it to finish, then tap the same picture again. Nothing was lost.",
        )

        ErrorCodes.SESSION_BUSY -> RankState.Problem(
            "The PC is busy",
            "It is part-way through something else in this folder. This was already retried once " +
                "on its own; close this and tap again in a moment, nothing was lost.",
        )

        ErrorCodes.TOKEN_REVOKED, ErrorCodes.INVALID_TOKEN, ErrorCodes.UNAUTHENTICATED ->
            RankState.Problem(
                "This phone is no longer paired with the PC",
                "Either the PC was given a new security certificate, or this phone was removed " +
                    "from its list. Nothing is lost. Go back to the folder list, where there is " +
                    "a button to pair this phone again.",
                fatal = true,
            )

        // K5: never "(code)" - the server's own message is written for a person, the code is not.
        else -> RankState.Problem("The PC refused that", result.message)
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
