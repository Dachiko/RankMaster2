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
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withTimeoutOrNull

/**
 * The review screen's brain: one file at a time, tap to keep it, swipe left to discard it.
 *
 * ## Optimistic, and why that is safe here
 *
 * Ranking never guesses (a vote that has not been answered has not happened). Review does: a
 * discard takes the item off the list and shows the next one at once, and the PC is told in the
 * background. A discard is a *move* into `<folder>/discarded/`, undoable, and a failed one puts the
 * item back where it was - so the worst case of guessing wrong is a picture quietly reappearing.
 *
 * ## One call at a time, in order
 *
 * Every server call goes through [lock], a fair mutex, so discards reach the PC in the order they
 * were made, a Cancel waits for every discard before it, and a list refresh never runs between a
 * discard being made and being sent.
 *
 * ## The retry rule (SERVER_SPEC.md section 13.3, the same one ranking keeps)
 *
 * A discard that times out is resent **with the same `clientRequestId`**: if the first attempt
 * landed, the server answers the resend `200` and does nothing twice; with a fresh id it would
 * answer `404 unknown_media_id`. The id is created once, when the item is discarded, and travels
 * with the history entry - nothing here can pick up a new one on the way.
 *
 * ## Cancel
 *
 * A step back per press. Past a *keep* it is local (no call). Past a *discard* it is the server's
 * undo, which is **one level**: discard A, keep B, discard C - Cancel undoes C, Cancel goes back to
 * B, and then Cancel is off, because A is no longer the server's last action and can no longer be
 * undone. [ReviewState.canCancel] encodes exactly that.
 *
 * ## The position lives on the PC
 *
 * Which item was on screen is stored by the server, with the folder, so the next visit - from any
 * phone - resumes there. Every change of the current item schedules a write, debounced
 * ([positionDebounceMs], latest wins) and quiet on failure: the next change sends it again. The end
 * of the list sends null, so a finished folder starts from the beginning. Leaving sends whatever is
 * pending *before* closing the session, which would otherwise take the folder's session with it.
 *
 * ## Silence
 *
 * Nothing non-fatal is ever shown. A failed discard puts the item back; a failed cancel re-reads
 * the list; a failed position write is forgotten until the next change. The only things that reach
 * the screen are the ones with no way forward: the session is gone, the phone is no longer paired,
 * the server is too old, or something else answered with the wrong certificate.
 *
 * @param scope tests pass their own; production uses the view model's.
 * @param retryDelayMs the pause before resending after an unreachable PC.
 */
class ReviewViewModel(
    private val client: Rm2Client,
    opened: Snapshot,
    private val newRequestId: () -> String = { UUID.randomUUID().toString() },
    private val scope: CoroutineScope? = null,
    private val retryDelayMs: Long = 500L,
    private val positionDebounceMs: Long = POSITION_DEBOUNCE_MS,
) : ViewModel() {

    private val _state = MutableStateFlow(ReviewState(snapshot = opened))
    val state: StateFlow<ReviewState> = _state.asStateFlow()

    private val where: CoroutineScope get() = scope ?: viewModelScope

    /** What the last moves were, oldest first. Only the top is ever acted on. */
    private val history = ArrayList<Entry>()

    /** Discards made and not yet answered, by `clientRequestId`. A fetched list must not resurrect them. */
    private val inFlight = LinkedHashMap<String, MediaRef>()

    private val lock = Mutex()

    private sealed interface Entry {
        data class Keep(val id: String) : Entry
        data class Discard(val ref: MediaRef, val index: Int, val requestId: String) : Entry
    }

    /** A review position as the PC holds it. A class, so that "none" (a null id) is a value and not an absence. */
    private data class Position(val id: String?)

    /** What the PC is known to hold; null until the list has been read. */
    private var confirmed: Position? = null

    /** Where the screen is now, as a position; a write is due when it differs from [confirmed]. */
    private var wanted: Position? = null

    private var positionJob: Job? = null

    // -- starting ---------------------------------------------------------------------------------

    /**
     * Start a visit: read the list and open at the PC's remembered position.
     *
     * View models here outlive the screen and a re-opened folder resumes under its old `sessionId`,
     * so the one held for it may be left over from a visit that ended. A visit therefore always
     * starts clean - nothing from the last one (history, a menu, a problem) carries over.
     */
    fun resume(opened: Snapshot) {
        history.clear()
        positionJob?.cancel()
        positionJob = null
        confirmed = null
        wanted = null
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
                // What the PC holds is what it just said; the first write is due only if where we
                // start differs from it (the stored item has left the folder, or there was none).
                confirmed = Position(result.value.reviewPosition)
                val start = startIndex(list, result.value.reviewPosition)
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

            is Rm2Result.Refused -> failLoad(loadFailure(result), result.session)
            is Rm2Result.Unreachable -> failLoad(loadFailure(result), null)
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

    // -- the two gestures -------------------------------------------------------------------------

    /** Tap: keep it, move on. No call to the PC. */
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
    }

    /** Swipe left / the menu's Discard: gone from the list at once, sent to the PC in the background. */
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
            )
        }
        publish()

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
                // there, so it stays discarded - and Cancel can still bring it back. The PC saves
                // again with its next action, so there is nothing to say.
                _state.update { it.copy(snapshot = result.session ?: it.snapshot) }
                publish()
            } else {
                rollBack(ref, index, requestId, fatalProblem(result), result.session)
            }

            is Rm2Result.Unreachable -> {
                rollBack(ref, index, requestId, fatalProblem(result), null)
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
        fatal: ReviewState.Problem?,
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
                problem = fatal ?: s.problem,
            )
        }
        publish()
    }

    // -- the menu ---------------------------------------------------------------------------------

    /** Open the menu for the item on screen. It closes by itself if that item stops being the one shown. */
    fun openMenu() {
        val s = _state.value
        val ref = s.current ?: return
        if (s.busy || s.problem?.fatal == true) return
        _state.update { it.copy(menuFor = ref.id) }
    }

    fun closeMenu() {
        if (_state.value.menuFor != null) _state.update { it.copy(menuFor = null) }
    }

    /** The menu's Discard: the same as a swipe left, and only for the item the menu was opened on. */
    fun discardFromMenu() {
        val s = _state.value
        val id = s.menuFor ?: return
        closeMenu()
        if (s.current?.id == id) discard()
    }

    // -- cancel -----------------------------------------------------------------------------------

    /**
     * Take back the last move, one step per press.
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
            }

            is Entry.Discard -> {
                _state.update { it.copy(busy = true, menuFor = null) }
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
                _state.update { it.copy(snapshot = undone) }
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
            }

            // Not taken back, and nothing to say about it: the list is read again, which also
            // corrects whatever made the PC refuse (an undo that was already used, a folder that
            // changed). Only a refusal with no way forward is shown.
            is Rm2Result.Refused -> {
                val fatal = fatalProblem(result)
                _state.update { it.copy(snapshot = result.session ?: it.snapshot, problem = fatal ?: it.problem) }
                publish()
                if (fatal == null) refreshLocked()
            }

            is Rm2Result.Unreachable -> {
                val fatal = fatalProblem(result)
                if (fatal != null) {
                    _state.update { it.copy(problem = fatal) }
                    publish()
                } else {
                    // The undo may have landed with its answer lost; the PC's list is the truth.
                    refreshLocked()
                }
            }
        }
    }

    // -- the end ----------------------------------------------------------------------------------

    /** "Start from the beginning": the first item, with nothing to cancel back to. */
    fun restart() {
        val s = _state.value
        if (s.items.isEmpty() || s.busy) return
        history.clear()
        _state.update { it.copy(index = 0, phase = ReviewState.Phase.Reviewing, menuFor = null) }
        publish()
    }

    /**
     * Sends the position still waiting, then closes the session, after any discard still waiting to
     * be sent (the PC would otherwise refuse them with `no_session`). Navigates first: waiting for
     * the PC makes back look broken on a slow network, and all of this is housekeeping - if the close
     * fails, the next open closes a stale session anyway.
     */
    fun leave(then: () -> Unit) {
        then()
        positionJob?.cancel()
        positionJob = null
        where.launch {
            lock.withLock {
                sendPosition()
                client.closeSession()
            }
        }
    }

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

            is Rm2Result.Refused -> {
                val fatal = fatalProblem(result)
                if (fatal != null) {
                    _state.update { it.copy(problem = fatal) }
                } else {
                    _state.update { it.copy(snapshot = result.session ?: it.snapshot) }
                    publish()
                }
            }

            is Rm2Result.Unreachable -> fatalProblem(result)?.let { fatal ->
                _state.update { it.copy(problem = fatal) }
            }
        }
    }

    // -- the position -----------------------------------------------------------------------------

    /**
     * Notes where the screen is now and, if the PC does not hold that, schedules the write.
     *
     * Called from [publish], so every change of the current item - keep, discard, cancel, restart,
     * a rolled-back discard, a refresh that moved things - is covered by one rule and none can be
     * forgotten. The end of the list is the position `null`.
     */
    private fun notePosition() {
        val s = _state.value
        val target = when (s.phase) {
            ReviewState.Phase.Reviewing -> Position(s.current?.id)
            ReviewState.Phase.Done -> Position(null)
            else -> return
        }
        if (target == wanted) return
        wanted = target
        positionJob?.cancel()
        positionJob = if (target == confirmed) {
            null // back on what the PC already holds: nothing to write
        } else {
            where.launch {
                delay(positionDebounceMs)
                lock.withLock { sendPosition() }
            }
        }
    }

    /**
     * One write of what is [wanted], inside [lock] so that it stays in order with everything else.
     * No retries and a short ceiling: it must never hold up a discard or a cancel waiting behind it,
     * and a write that does not land is simply made again by the next change.
     */
    private suspend fun sendPosition() {
        val target = wanted ?: return
        if (target == confirmed) return
        val result = withTimeoutOrNull(POSITION_TIMEOUT_MS) { client.setReviewPosition(target.id) }
        if (result is Rm2Result.Ok) confirmed = target
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

    /** Recompute what Cancel may do and what the menu is about, then note the position. */
    private fun publish() {
        _state.update {
            it.copy(
                canCancel = cancellable(it.snapshot),
                menuFor = it.menuFor?.takeIf { id -> id == it.current?.id },
            )
        }
        notePosition()
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

    /**
     * The refusals with no way forward - the only ones ever shown after the list has loaded. Null
     * for everything else, which is dealt with without a word.
     */
    private fun fatalProblem(result: Rm2Result.Refused): ReviewState.Problem? = when {
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

        else -> null
    }

    private fun fatalProblem(result: Rm2Result.Unreachable): ReviewState.Problem? =
        if (result.pinMismatch) {
            ReviewState.Problem(
                "That is not your PC",
                "Something answered with a certificate this phone has not paired with. Nothing was " +
                    "sent to it. Do not continue on this network.",
                fatal = true,
            )
        } else {
            null
        }

    /** Why the list could not be read: a fatal reason, or a plain one the screen answers with Retry. */
    private fun loadFailure(result: Rm2Result.Refused): ReviewState.Problem =
        fatalProblem(result) ?: ReviewState.Problem("Could not open the folder", result.message)

    private fun loadFailure(result: Rm2Result.Unreachable): ReviewState.Problem =
        fatalProblem(result) ?: ReviewState.Problem(
            "Cannot reach the PC",
            "Check the PC is awake and on the same Wi-Fi.",
        )

    companion object {
        fun factory(client: Rm2Client, opened: Snapshot): ViewModelProvider.Factory =
            viewModelFactory { initializer { ReviewViewModel(client, opened) } }

        /** The first attempt plus three resends. */
        const val MAX_ATTEMPTS = 4

        /** The ceiling on a server-named `retryAfterSeconds`, as in ranking. */
        const val MAX_RETRY_AFTER_SECONDS = 30

        /** A burst of taps writes the position once, after the last of them. */
        const val POSITION_DEBOUNCE_MS = 400L

        /** A position write that has not answered by now is dropped; the next change makes it again. */
        const val POSITION_TIMEOUT_MS = 3000L

        private const val MAX_HISTORY = 500
        private const val DISCARD = "discard"
        private const val DROP_MISSING = "drop_missing"

        /**
         * Where to open, given the position the PC stored ([reviewPosition], which may name a file
         * that has since left the folder):
         *
         * - on that item, if it is in [items];
         * - otherwise on the first item that comes after it in [NaturalOrder];
         * - at 0 when there is none after it, or nothing is stored.
         *
         * [items] must already be in natural order, as everything held in the state is.
         */
        fun startIndex(items: List<MediaRef>, reviewPosition: String?): Int {
            if (reviewPosition == null) return 0
            val at = items.indexOfFirst { it.id == reviewPosition }
            if (at >= 0) return at
            val after = items.indexOfFirst { NaturalOrder.compare(it.id, reviewPosition) > 0 }
            return if (after >= 0) after else 0
        }
    }
}
