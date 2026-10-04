package com.rankmaster2.phone.ui.review

import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import com.rankmaster2.phone.net.ErrorCodes
import com.rankmaster2.phone.net.Items
import com.rankmaster2.phone.net.MediaRef
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
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

/**
 * The review screen's brain: one file at a time, keep it or discard it.
 *
 * ## Optimistic, and why that is safe here
 *
 * Ranking never guesses (a vote that has not been answered has not happened). Review does: a
 * discard takes the item off the list and shows the next one at once, and the PC is told in the
 * background. A discard is a *move* into `<folder>/discarded/`, undoable, and a failed one puts the
 * item back - so the worst case of guessing wrong is a picture reappearing with a message.
 *
 * ## One call at a time, in order
 *
 * Every server call goes through [lock], a fair mutex, so discards reach the PC in the order they
 * were swiped, a Cancel waits for every discard before it, and a list refresh never runs between a
 * discard being swiped and being sent.
 *
 * ## The retry rule (SERVER_SPEC.md section 13.3, the same one ranking keeps)
 *
 * A discard that times out is resent **with the same `clientRequestId`**: if the first attempt
 * landed, the server answers the resend `200` and does nothing twice; with a fresh id it would
 * answer `404 unknown_media_id`. The id is created once, when the item is swiped, and travels with
 * the history entry - nothing here can pick up a new one on the way.
 *
 * ## Cancel
 *
 * A step back per press. Past a *keep* it is local (no call). Past a *discard* it is the server's
 * undo, which is **one level**: discard A, keep B, discard C - Cancel undoes C, Cancel goes back to
 * B, and then Cancel is off, because A is no longer the server's last action and can no longer be
 * undone. [ReviewState.canCancel] encodes exactly that.
 *
 * @param scope tests pass their own; production uses the view model's.
 * @param retryDelayMs the pause before resending after an unreachable PC.
 */
class ReviewViewModel(
    private val client: Rm2Client,
    opened: Snapshot,
    private val positions: ReviewPositionStore,
    private val newRequestId: () -> String = { UUID.randomUUID().toString() },
    private val scope: CoroutineScope? = null,
    private val retryDelayMs: Long = 500L,
) : ViewModel() {

    private val _state = MutableStateFlow(ReviewState(snapshot = opened))
    val state: StateFlow<ReviewState> = _state.asStateFlow()

    private val where: CoroutineScope get() = scope ?: viewModelScope

    /** What the last swipes were, oldest first. Only the top is ever acted on. */
    private val history = ArrayList<Entry>()

    /** Discards swiped and not yet answered, by `clientRequestId`. A fetched list must not resurrect them. */
    private val inFlight = LinkedHashMap<String, MediaRef>()

    private val lock = Mutex()

    private sealed interface Entry {
        data class Keep(val id: String) : Entry
        data class Discard(val ref: MediaRef, val index: Int, val requestId: String) : Entry
    }

    // -- starting ---------------------------------------------------------------------------------

    /**
     * Start a visit: read the list and open at the remembered position.
     *
     * View models here outlive the screen and a re-opened folder resumes under its old `sessionId`,
     * so the one held for it may be left over from a visit that ended. A visit therefore always
     * starts clean - nothing from the last one (history, counts, a problem) carries over.
     */
    fun resume(opened: Snapshot) {
        history.clear()
        _state.update { ReviewState(snapshot = opened, foreground = it.foreground) }
        load()
    }

    /** Read the list again after it could not be read. */
    fun retry() {
        _state.update { it.copy(phase = ReviewState.Phase.Loading, problem = null) }
        load()
    }

    private fun load() {
        where.launch { lock.withLock { applyLoad(client.items()) } }
    }

    private fun applyLoad(result: Rm2Result<Items>) {
        when (result) {
            is Rm2Result.Ok -> {
                val list = withoutInFlight(result.value.items)
                val snapshot = result.value.session
                if (list.isEmpty()) {
                    _state.update {
                        it.copy(
                            snapshot = snapshot,
                            items = emptyList(),
                            index = 0,
                            phase = ReviewState.Phase.Failed,
                            problem = ReviewState.Problem(
                                "Nothing to review",
                                "This folder has no photos or videos in it.",
                            ),
                        )
                    }
                    publish()
                    return
                }
                val start = startIndex(list, positions.lastPassed(snapshot.folder))
                _state.update {
                    it.copy(
                        snapshot = snapshot,
                        items = list,
                        index = start,
                        phase = ReviewState.Phase.Reviewing,
                        problem = null,
                    )
                }
                publish()
            }

            is Rm2Result.Refused -> failLoad(problemFor(result), result.session)
            is Rm2Result.Unreachable -> failLoad(problemFor(result, null), null)
        }
    }

    private fun failLoad(problem: ReviewState.Problem, session: Snapshot?) {
        _state.update {
            it.copy(
                snapshot = session ?: it.snapshot,
                phase = ReviewState.Phase.Failed,
                problem = problem,
            )
        }
        publish()
    }

    // -- the two swipes ---------------------------------------------------------------------------

    /** Swipe right / Next: keep it, move on. No call to the PC. */
    fun keep() {
        val s = _state.value
        val ref = s.current ?: return
        if (!s.actionable) return
        addHistory(Entry.Keep(ref.id))
        val next = s.index + 1
        _state.update {
            it.copy(
                index = next,
                phase = if (next >= it.items.size) ReviewState.Phase.Done else ReviewState.Phase.Reviewing,
            )
        }
        publish()
        savePosition()
    }

    /** Swipe left / Discard: gone from the list at once, sent to the PC in the background. */
    fun discard() {
        val s = _state.value
        val ref = s.current ?: return
        if (!s.actionable) return
        val index = s.index
        val requestId = newRequestId()

        addHistory(Entry.Discard(ref, index, requestId))
        inFlight[requestId] = ref

        val remaining = s.items.toMutableList().also { it.removeAt(index) }
        _state.update {
            it.copy(
                items = remaining,
                phase = if (index >= remaining.size) ReviewState.Phase.Done else ReviewState.Phase.Reviewing,
                discardedThisVisit = it.discardedThisVisit + 1,
            )
        }
        publish()
        savePosition()

        where.launch { lock.withLock { sendDiscard(ref, index, requestId) } }
    }

    private suspend fun sendDiscard(ref: MediaRef, index: Int, requestId: String) {
        val result = withRetries { client.discardItem(ref.id, requestId) }
        inFlight.remove(requestId)

        when (result) {
            is Rm2Result.Ok -> {
                // `drop_missing`: the file had already left the folder. It is gone either way, but
                // there is nothing to restore, so it is not something Cancel could undo.
                if (result.value.lastAction?.type == DROP_MISSING) forget(requestId)
                _state.update { it.copy(snapshot = result.value) }
                publish()
            }

            is Rm2Result.Refused -> if (result.code == ErrorCodes.UNKNOWN_MEDIA_ID) {
                // Not in the session any more: someone else moved it. Putting it back would show a
                // ghost, so it stays gone, quietly.
                forget(requestId)
                _state.update { it.copy(snapshot = result.session ?: it.snapshot) }
                publish()
            } else if (result.code == ErrorCodes.SAVE_FAILED && result.detailText("fileMoved") == "true") {
                // § 10.8: the file is already in discarded/ and the PC armed undo; only its own
                // ranking file is behind. Putting the picture back would show a file that is not
                // there, so it stays discarded - and Cancel can still bring it back.
                _state.update {
                    it.copy(
                        snapshot = result.session ?: it.snapshot,
                        problem = ReviewState.Problem(
                            "Discarded, but the PC could not save",
                            "${ref.id} is in discarded/. The PC will try to save again with the next action. " +
                                result.message,
                        ),
                    )
                }
                publish()
            } else {
                rollBack(ref, index, requestId, problemFor(result, ref), result.session)
            }

            is Rm2Result.Unreachable -> {
                rollBack(ref, index, requestId, problemFor(result, ref), null)
                // The answer may have been lost after the move happened; the PC's list is the truth.
                if (!result.pinMismatch) refreshLocked()
            }
        }
    }

    /** The discard failed for good: the item comes back where it was, and is the one on screen. */
    private fun rollBack(
        ref: MediaRef,
        index: Int,
        requestId: String,
        problem: ReviewState.Problem,
        session: Snapshot?,
    ) {
        forget(requestId)
        _state.update { s ->
            val items = s.items.toMutableList()
            val at = index.coerceIn(0, items.size)
            items.add(at, ref)
            s.copy(
                snapshot = session ?: s.snapshot,
                items = items,
                index = at,
                phase = ReviewState.Phase.Reviewing,
                discardedThisVisit = (s.discardedThisVisit - 1).coerceAtLeast(0),
                problem = problem,
            )
        }
        publish()
        savePosition()
    }

    // -- cancel -----------------------------------------------------------------------------------

    /**
     * Take back the last swipe, one step per press.
     *
     * A *keep* goes back to that item with no call. A *discard* waits for every call before it, asks
     * the PC to undo, re-reads the list (the restored file's links, and the name it came back
     * under, which can differ - `name (2).jpg`), and makes it the current item.
     */
    fun cancel() {
        val s = _state.value
        if (s.busy || !s.canCancel) return
        when (val top = history.lastOrNull() ?: return) {
            is Entry.Keep -> {
                history.removeAt(history.lastIndex)
                _state.update { st ->
                    val i = st.items.indexOfFirst { it.id == top.id }
                    if (i < 0) st else st.copy(index = i, phase = ReviewState.Phase.Reviewing)
                }
                publish()
                savePosition()
            }

            is Entry.Discard -> {
                _state.update { it.copy(busy = true, problem = null) }
                where.launch {
                    lock.withLock { cancelDiscard(top) }
                    _state.update { it.copy(busy = false) }
                    publish()
                }
            }
        }
    }

    private suspend fun cancelDiscard(entry: Entry.Discard) {
        // While this waited for the lock, that discard may have failed and rolled itself back, or
        // been dropped. Then there is nothing of it left to undo.
        if (history.lastOrNull() !== entry) return

        val requestId = newRequestId()
        when (val result = withRetries { client.cancel(requestId) }) {
            is Rm2Result.Ok -> {
                val undone = result.value
                history.remove(entry)
                _state.update {
                    it.copy(
                        snapshot = undone,
                        discardedThisVisit = (it.discardedThisVisit - 1).coerceAtLeast(0),
                    )
                }
                val restoredId = undone.lastAction?.restoredId ?: entry.ref.id
                when (val fetched = client.items()) {
                    is Rm2Result.Ok -> {
                        val list = withoutInFlight(fetched.value.items)
                        _state.update { s ->
                            val i = list.indexOfFirst { it.id == restoredId }
                                .takeIf { it >= 0 } ?: list.indexOfFirst { it.id == entry.ref.id }
                            s.copy(
                                snapshot = fetched.value.session,
                                items = list,
                                index = if (i >= 0) i else entry.index.coerceIn(0, list.size),
                                phase = if (list.isEmpty()) ReviewState.Phase.Done else ReviewState.Phase.Reviewing,
                            )
                        }
                    }

                    // The undo landed but the list did not come: put the picture back by hand,
                    // under the name it had, and let the next foreground read correct it.
                    else -> _state.update { s ->
                        val items = s.items.toMutableList()
                        val at = entry.index.coerceIn(0, items.size)
                        items.add(at, entry.ref)
                        s.copy(items = items, index = at, phase = ReviewState.Phase.Reviewing)
                    }
                }
                publish()
                savePosition()
            }

            is Rm2Result.Refused -> {
                _state.update { it.copy(snapshot = result.session ?: it.snapshot, problem = cancelProblem(result)) }
                publish()
            }

            is Rm2Result.Unreachable -> {
                _state.update { it.copy(problem = problemFor(result, null, cancelling = true)) }
                publish()
                // The undo may have landed with its answer lost; the PC's list is the truth.
                if (!result.pinMismatch) refreshLocked()
            }
        }
    }

    // -- the end ----------------------------------------------------------------------------------

    /** "Start again": from the first item, with nothing remembered. */
    fun restart() {
        val s = _state.value
        if (s.items.isEmpty() || s.busy) return
        history.clear()
        positions.forget(s.snapshot.folder)
        _state.update { it.copy(index = 0, phase = ReviewState.Phase.Reviewing, discardedThisVisit = 0) }
        publish()
    }

    /**
     * Closes the session before leaving, after any discard still waiting to be sent (the PC would
     * otherwise refuse them with `no_session`). Navigates first: waiting for the PC makes back look
     * broken on a slow network, and the close is housekeeping - if it fails, the next open closes a
     * stale session anyway.
     */
    fun leave(then: () -> Unit) {
        then()
        where.launch { lock.withLock { client.closeSession() } }
    }

    fun dismissProblem() = _state.update { if (it.problem?.fatal == true) it else it.copy(problem = null) }

    // -- coming back to the front ------------------------------------------------------------------

    fun onForeground(inFront: Boolean) {
        _state.update { it.copy(foreground = inFront) }
        if (inFront) refresh()
    }

    /**
     * Re-read the list - anything could have happened on the PC while the phone was elsewhere - and
     * keep the current item if it is still there. Quiet on failure: a phone that cannot reach the PC
     * right now has not lost anything it was looking at.
     */
    fun refresh() {
        if (_state.value.phase == ReviewState.Phase.Loading) return
        where.launch { lock.withLock { refreshLocked() } }
    }

    private suspend fun refreshLocked() {
        when (val result = client.items()) {
            is Rm2Result.Ok -> {
                val list = withoutInFlight(result.value.items)
                _state.update { s ->
                    val keepId = s.current?.id
                    val found = keepId?.let { id -> list.indexOfFirst { it.id == id } } ?: -1
                    val wasDone = s.phase == ReviewState.Phase.Done
                    val index = when {
                        wasDone -> list.size
                        found >= 0 -> found
                        else -> s.index.coerceIn(0, list.size)
                    }
                    val phase = when {
                        s.phase == ReviewState.Phase.Failed || s.phase == ReviewState.Phase.Loading -> s.phase
                        index >= list.size -> ReviewState.Phase.Done
                        else -> ReviewState.Phase.Reviewing
                    }
                    s.copy(snapshot = result.value.session, items = list, index = index, phase = phase)
                }
                publish()
            }

            is Rm2Result.Refused -> if (result.code == ErrorCodes.NO_SESSION || result.isPairingLost()) {
                _state.update { it.copy(problem = problemFor(result)) }
            } else {
                _state.update { it.copy(snapshot = result.session ?: it.snapshot) }
                publish()
            }

            is Rm2Result.Unreachable -> if (result.pinMismatch) {
                _state.update { it.copy(problem = problemFor(result, null)) }
            }
        }
    }

    // -- plumbing ---------------------------------------------------------------------------------

    private fun withoutInFlight(list: List<MediaRef>): List<MediaRef> {
        val ordered = NaturalOrder.sorted(list)
        if (inFlight.isEmpty()) return ordered
        val ids = inFlight.values.mapTo(HashSet()) { it.id }
        return ordered.filterNot { it.id in ids }
    }

    private fun addHistory(entry: Entry) {
        history.add(entry)
        if (history.size > MAX_HISTORY) history.removeAt(0)
    }

    private fun forget(requestId: String) {
        history.removeAll { it is Entry.Discard && it.requestId == requestId }
    }

    /** Remember the item before the current one: the last one he moved past (or nothing, at the start). */
    private fun savePosition() {
        val s = _state.value
        val folder = s.snapshot.folder
        val previous = s.items.getOrNull(s.index - 1)
        if (previous == null) positions.forget(folder) else positions.remember(folder, previous.id)
    }

    /** Recompute what Cancel may do from the history and the latest snapshot. */
    private fun publish() {
        _state.update { it.copy(canCancel = cancellable(it.snapshot)) }
    }

    private fun cancellable(snapshot: Snapshot): Boolean {
        return when (val top = history.lastOrNull()) {
            null -> false
            is Entry.Keep -> true
            is Entry.Discard -> {
                // Still waiting to be sent: Cancel will wait for it and then undo it. Otherwise the
                // server's single undo level must still be that very discard.
                top.requestId in inFlight ||
                    (snapshot.undoAvailable &&
                        snapshot.lastAction?.type == DISCARD &&
                        snapshot.lastAction?.clientRequestId == top.requestId)
            }
        }
    }

    /**
     * Sends [send], and on a network failure or `503 session_busy` sends the identical call again -
     * the closure holds the same id every time. Never retries anything else.
     */
    private suspend fun <T> withRetries(send: suspend () -> Rm2Result<T>): Rm2Result<T> {
        var result = send()
        var attempts = 1
        while (attempts < MAX_ATTEMPTS) {
            val wait = when {
                result is Rm2Result.Unreachable && !result.pinMismatch -> retryDelayMs
                result is Rm2Result.Refused && result.code == ErrorCodes.SESSION_BUSY ->
                    (result.detailInt("retryAfterSeconds") ?: 1).coerceIn(0, MAX_RETRY_AFTER_SECONDS) * 1000L
                else -> return result
            }
            delay(wait)
            result = send()
            attempts++
        }
        return result
    }

    private fun Rm2Result.Refused.isPairingLost(): Boolean =
        code == ErrorCodes.TOKEN_REVOKED || code == ErrorCodes.INVALID_TOKEN || code == ErrorCodes.UNAUTHENTICATED

    private fun problemFor(result: Rm2Result.Refused, ref: MediaRef? = null): ReviewState.Problem = when {
        result.code == ErrorCodes.NO_SESSION -> ReviewState.Problem(
            "The folder is no longer open",
            "The PC restarted, or something else took the folder. Nothing is lost. Go back and " +
                "open the folder again.",
            fatal = true,
        )

        // A route the server does not have: it predates review. Nothing to retry.
        result.code == ErrorCodes.NOT_FOUND -> ReviewState.Problem(
            "Update the Rank Master server on the PC",
            "The server on the PC is too old for Review. Update it, then open the folder again.",
            fatal = true,
        )

        result.isPairingLost() -> ReviewState.Problem(
            "This phone is no longer paired with the PC",
            "Either the PC was given a new security certificate, or this phone was removed from its " +
                "list. Go back to the folder list, where there is a button to pair this phone again.",
            fatal = true,
        )

        else -> {
            val what = ref?.id?.let { "Could not discard $it" }
            val back = if (ref != null) "It is back in the list. " else ""
            when (result.code) {
                ErrorCodes.MOVE_FAILED -> ReviewState.Problem(
                    what ?: "The file could not be moved",
                    back + "Nothing changed. " + result.message,
                )

                ErrorCodes.SAVE_FAILED -> ReviewState.Problem(
                    what ?: "The PC could not write its file",
                    back + "Nothing was moved. " + result.message,
                )

                ErrorCodes.RENAME_IN_PROGRESS -> ReviewState.Problem(
                    "The PC is renaming this folder",
                    back + "Wait for it to finish, then try again.",
                )

                ErrorCodes.SESSION_BUSY -> ReviewState.Problem(
                    "The PC is busy",
                    back + "Try again in a moment.",
                )

                else -> ReviewState.Problem(what ?: "The PC refused that", back + result.message)
            }
        }
    }

    private fun cancelProblem(result: Rm2Result.Refused): ReviewState.Problem =
        if (result.code == ErrorCodes.NOTHING_TO_UNDO) {
            ReviewState.Problem(
                "Nothing to take back",
                "That has already been cancelled, or the folder was changed in the meantime.",
            )
        } else {
            problemFor(result)
        }

    private fun problemFor(
        result: Rm2Result.Unreachable,
        ref: MediaRef?,
        cancelling: Boolean = false,
    ): ReviewState.Problem = when {
        result.pinMismatch -> ReviewState.Problem(
            "That is not your PC",
            "Something answered with a certificate this phone has not paired with. Nothing was " +
                "sent to it. Do not continue on this network.",
            fatal = true,
        )

        cancelling -> ReviewState.Problem(
            "Cannot reach the PC",
            "The cancel may or may not have landed - the list is being read again.",
        )

        else -> ReviewState.Problem(
            ref?.id?.let { "Could not discard $it" } ?: "Cannot reach the PC",
            (if (ref != null) "It is back in the list. " else "") +
                "Check the PC is awake and on the same Wi-Fi.",
        )
    }

    companion object {
        fun factory(client: Rm2Client, opened: Snapshot, positions: ReviewPositionStore): ViewModelProvider.Factory =
            viewModelFactory { initializer { ReviewViewModel(client, opened, positions) } }

        /** The first attempt plus three resends. */
        const val MAX_ATTEMPTS = 4

        /** The ceiling on a server-named `retryAfterSeconds`, as in ranking. */
        const val MAX_RETRY_AFTER_SECONDS = 30

        private const val MAX_HISTORY = 500
        private const val DISCARD = "discard"
        private const val DROP_MISSING = "drop_missing"

        /**
         * Where to open: the first item after [lastPassed] in [items], or 0 when there is nothing
         * remembered, it is no longer in the list, or it was the last one (a finished folder starts
         * over rather than opening on an empty end).
         */
        fun startIndex(items: List<MediaRef>, lastPassed: String?): Int {
            if (lastPassed == null) return 0
            val at = items.indexOfFirst { it.id == lastPassed }
            return if (at < 0 || at + 1 >= items.size) 0 else at + 1
        }
    }
}
