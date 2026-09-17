package com.rankmaster2.phone.ui.browse

import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import com.rankmaster2.phone.net.ErrorCodes
import com.rankmaster2.phone.net.Rm2Client
import com.rankmaster2.phone.net.Rm2Result
import com.rankmaster2.phone.net.Snapshot
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

/**
 * Picking a folder: the drive list, one level down at a time, and "open this one".
 *
 * All of the decisions live here and none of them live in a composable, so every rule below is
 * asserted by a JVM unit test rather than by looking at a phone.
 *
 * Three things shape this class:
 *
 *  1. **Two requests per listing.** § 10.15 is explicit that `counts=true` is "one directory
 *     enumeration per child, and a root with hundreds of children on a slow share is a slow
 *     request". So the names are fetched with `counts=false` and painted immediately, and a second
 *     `counts=true` call fills the numbers in afterwards. If that second call is slow, wrong or
 *     never answers, the owner has already been browsing for a while and loses nothing but the
 *     numbers.
 *  2. **Navigation is by `path` values the server handed back, never by editing a string.** The
 *     paths are Windows paths seen from an Android phone; the way up is `parent`, and `parent ==
 *     null` is the way to know there is no up (§ 10.15).
 *  3. **Opening is the server's decision.** This screen greys out what § 10.15 says would be
 *     refused, but it does not re-implement the rule, and it still handles every refusal § 10.1
 *     can produce - because the folder can change between the listing and the open.
 *
 * @param onOpened handed the [Snapshot] from a successful `POST /session`. The ranking screen
 *   belongs to someone else; this class's job ends the moment a session exists.
 */
class BrowseViewModel(
    private val client: Rm2Client,
    private val lastFolders: LastFolderStore = InMemoryLastFolderStore(),
    private val onOpened: (Snapshot) -> Unit = {},
) : ViewModel() {

    private val _state = MutableStateFlow(BrowseUiState(lastFolder = lastFolders.last()))
    val state: StateFlow<BrowseUiState> = _state.asStateFlow()

    /**
     * Bumped by every navigation. A listing or a counting pass that comes back carrying an old
     * generation is dropped on the floor: the owner has moved on, and merging stale counts into the
     * folder they are looking at now would put `Iceland`'s numbers next to `Norway`'s names.
     */
    private var generation = 0

    private val scope: CoroutineScope get() = viewModelScope

    init {
        loadRoots()
    }

    // -- navigation --------------------------------------------------------------------------

    /** § 10.14. The top of the world: drives and mount points, including the unavailable ones. */
    fun loadRoots() {
        val gen = ++generation
        _state.update {
            it.copy(
                place = Place.Roots,
                loadingList = true,
                listFailure = null,
                rows = emptyList(),
                counts = CountsPhase.Idle,
                openFailure = null,
            )
        }
        scope.launch {
            when (val result = client.roots()) {
                is Rm2Result.Ok -> {
                    if (gen != generation) return@launch
                    // § 10.14: "A root that is listed but not available MUST still appear." We do
                    // not filter them out either - a disconnected share the owner expects to see
                    // missing is a support call.
                    _state.update { it.copy(roots = result.value.roots, loadingList = false) }
                }

                is Rm2Result.Refused -> {
                    if (gen != generation) return@launch
                    _state.update {
                        it.copy(
                            loadingList = false,
                            listFailure = result.message,
                            pairingLost = it.pairingLost || result.isPairingLost,
                        )
                    }
                }

                is Rm2Result.Unreachable -> {
                    if (gen != generation) return@launch
                    _state.update {
                        it.copy(
                            loadingList = false,
                            listFailure = unreachableText(result),
                            pairingLost = it.pairingLost || result.pinMismatch,
                        )
                    }
                }
            }
        }
    }

    /**
     * Walk into [path]: names first, counts second.
     *
     * [path] must be a value the server produced - a `Root.path` or a `BrowseEntry.path`. Nothing
     * in this class ever constructs one.
     */
    fun enter(path: String) {
        val gen = ++generation
        _state.update {
            it.copy(
                loadingList = true,
                listFailure = null,
                rows = emptyList(),
                counts = CountsPhase.Idle,
                openFailure = null,
            )
        }
        scope.launch {
            when (val result = client.browse(path, counts = false)) {
                is Rm2Result.Ok -> {
                    if (gen != generation) return@launch
                    val listing = result.value
                    _state.update {
                        it.copy(
                            // The server's own `path` and `parent`, not the ones we asked with: it
                            // resolves and trims, and its spelling is the one to navigate by.
                            place = Place.Folder(listing.path, listing.parent),
                            rows = listing.entries.map(FolderRow::of),
                            loadingList = false,
                            counts = CountsPhase.Loading,
                        )
                    }
                    // Same coroutine, deliberately: the names are already in `state` above, so the
                    // counts physically cannot arrive first.
                    fillInCounts(listing.path, gen)
                }

                is Rm2Result.Refused -> {
                    if (gen != generation) return@launch
                    _state.update {
                        it.copy(
                            loadingList = false,
                            listFailure = result.message,
                            pairingLost = it.pairingLost || result.isPairingLost,
                        )
                    }
                }

                is Rm2Result.Unreachable -> {
                    if (gen != generation) return@launch
                    _state.update {
                        it.copy(
                            loadingList = false,
                            listFailure = unreachableText(result),
                            pairingLost = it.pairingLost || result.pinMismatch,
                        )
                    }
                }
            }
        }
    }

    /**
     * Back one level.
     *
     * From a folder with a `parent`, that is the parent. From a folder whose `parent` is null - a
     * drive root - it is the drive list. Which of the two it is, is read off `parent`, never off
     * the shape of the path.
     */
    fun up() {
        when (val place = _state.value.place) {
            Place.Roots -> Unit
            is Place.Folder -> place.parent?.let(::enter) ?: loadRoots()
        }
    }

    /** Re-run whatever we are looking at. Used by the "try again" on a failed listing. */
    fun refresh() {
        when (val place = _state.value.place) {
            Place.Roots -> loadRoots()
            is Place.Folder -> enter(place.path)
        }
    }

    // -- counts ------------------------------------------------------------------------------

    private suspend fun fillInCounts(path: String, gen: Int) {
        when (val result = client.browse(path, counts = true)) {
            is Rm2Result.Ok -> {
                if (gen != generation) return
                val counted = result.value.entries.associateBy { it.path }
                _state.update { current ->
                    current.copy(
                        // Merged onto the rows we already drew, keyed by path and keeping their
                        // order: the listing must not reshuffle under the owner's thumb, and a
                        // child that vanished between the two calls simply keeps its null counts
                        // rather than disappearing mid-scroll.
                        rows = current.rows.map { row ->
                            counted[row.path]?.let(FolderRow::of) ?: row
                        },
                        counts = CountsPhase.Loaded,
                    )
                }
            }

            // The names already work. § 10.15 warns this is the slow call; losing it is a
            // degraded listing, not a failed one, and it must never clear what is on screen.
            //
            // Except when it is not about the folder at all. A counting pass refused because this
            // phone is no longer paired says the same thing any other call would have, and a
            // browser that reports it as "could not count the files" is a browser that looks
            // merely slow while nothing will ever work again.
            is Rm2Result.Refused -> {
                if (gen != generation) return
                _state.update {
                    it.copy(
                        counts = CountsPhase.Failed,
                        pairingLost = it.pairingLost || result.isPairingLost,
                    )
                }
            }

            is Rm2Result.Unreachable -> {
                if (gen != generation) return
                _state.update {
                    it.copy(
                        counts = CountsPhase.Failed,
                        pairingLost = it.pairingLost || result.pinMismatch,
                    )
                }
            }
        }
    }

    /** Ask for the counts again after they failed, without re-fetching the names. */
    fun retryCounts() {
        val path = (_state.value.place as? Place.Folder)?.path ?: return
        if (_state.value.counts != CountsPhase.Failed) return
        val gen = generation
        _state.update { it.copy(counts = CountsPhase.Loading) }
        scope.launch { fillInCounts(path, gen) }
    }

    // -- opening -----------------------------------------------------------------------------

    /** Open the folder the browser is standing in. */
    fun openCurrent() {
        (_state.value.place as? Place.Folder)?.let { open(it.path) }
    }

    /** Rule 6: the one-tap path back to yesterday's folder. */
    fun openRemembered() {
        _state.value.lastFolder?.let { open(it.path) }
    }

    /**
     * § 10.1. The end of this screen's job.
     *
     * Every branch here is a refusal an owner can do something about, which is why they are
     * separate states and not one "could not open" string. Note that a greyed row cannot get here
     * (the button is disabled) but `folder_not_rankable` is handled anyway: the listing may be
     * minutes old, `counts=false` leaves every `rankable` null, and the owner can always open the
     * folder they are *standing in*, whose rankability the browse endpoint never reports.
     */
    fun open(path: String) {
        if (_state.value.isOpening) return
        _state.update { it.copy(openingPath = path, openFailure = null) }
        scope.launch { attemptOpen(path) }
    }

    /**
     * Opens a folder. § 10.1 is explicit that the server will not close a session left open
     * elsewhere on its own - "the client MUST `DELETE /session` first" - and neither does this:
     * `session_already_open` comes back through [refusalToFailure] as an ordinary
     * [OpenFailure.AlreadyOpen], the same as any other refusal. The only thing that ever calls
     * `DELETE /session` is the owner tapping "Close it and open this" ([closeOtherSessionAndRetry]).
     * A folder left open elsewhere may be a half-finished ranking run on the PC, and nothing here
     * is allowed to throw that away behind his back (H7).
     *
     * `423 folder_locked` is a different claim and a real one - something *other* than this server
     * holds the folder - and it keeps its message, because that is the one case where the owner has
     * to go and do something.
     */
    private suspend fun attemptOpen(path: String) {
        when (val result = client.openSession(path)) {
            is Rm2Result.Ok -> {
                val snapshot = result.value
                // The server's own spelling of both, from the snapshot - so the remembered path is
                // the resolved one it will accept next time, and the label is its `folderName`
                // rather than something this app sliced off the end of a Windows path.
                val remembered = RememberedFolder(snapshot.folder, snapshot.folderName)
                lastFolders.remember(remembered)
                _state.update { it.copy(openingPath = null, openFailure = null, lastFolder = remembered) }
                onOpened(snapshot)
            }

            is Rm2Result.Refused -> {
                val failure = refusalToFailure(path, result)
                if (result.isPairingLost) _state.update { it.copy(pairingLost = true) }
                // A remembered folder that is gone stops being offered, or rule 6 turns into a
                // shortcut to an error message every single launch.
                if (failure is OpenFailure.NotFound && lastFolders.last()?.path == path) {
                    lastFolders.forget()
                }
                _state.update {
                    it.copy(
                        openingPath = null,
                        openFailure = failure,
                        lastFolder = lastFolders.last(),
                    )
                }
            }

            is Rm2Result.Unreachable -> _state.update {
                it.copy(
                    openingPath = null,
                    openFailure = OpenFailure.Unreachable(path, result.detail, result.pinMismatch),
                    pairingLost = it.pairingLost || result.pinMismatch,
                )
            }
        }
    }

    private fun refusalToFailure(path: String, refused: Rm2Result.Refused): OpenFailure = when {
        refused.code == ErrorCodes.FOLDER_NOT_RANKABLE -> OpenFailure.NotRankable(path, refused.message)
        refused.code == ErrorCodes.FOLDER_NOT_FOUND -> OpenFailure.NotFound(path, refused.message)
        refused.code == ErrorCodes.SESSION_ALREADY_OPEN -> OpenFailure.AlreadyOpen(
            folder = path,
            serverMessage = refused.message,
            openFolder = refused.detailText("openFolder"),
        )
        // § 4 says branch on the code, and `folder_locked` is the code § 10.1 step 4 produces.
        // The status is kept as a second route to the same answer: 423 is Locked and nothing else
        // in this API returns it.
        refused.status == LOCKED || refused.code == ErrorCodes.FOLDER_LOCKED ->
            OpenFailure.Locked(path, refused.message)
        else -> OpenFailure.Refused(path, refused.status, refused.code, refused.message)
    }

    /**
     * The action attached to `session_already_open`: close the other session, then open this one.
     *
     * § 10.1 is explicit that the server will not do this implicitly, so it has to be a deliberate
     * tap and not a silent retry - the session being closed may be a half-finished ranking run on
     * the PC, and nothing else in this app is allowed to throw that away behind the owner's back.
     */
    fun closeOtherSessionAndRetry() {
        val failure = _state.value.openFailure as? OpenFailure.AlreadyOpen ?: return
        val path = failure.folder
        _state.update { it.copy(openingPath = path, openFailure = null) }
        scope.launch {
            when (val closed = client.closeSession()) {
                is Rm2Result.Ok -> attemptOpen(path)

                is Rm2Result.Refused -> _state.update {
                    it.copy(
                        openingPath = null,
                        openFailure = OpenFailure.Refused(
                            path,
                            closed.status,
                            closed.code,
                            // H14: the PC refuses `DELETE /session` while it is mid-rename with
                            // `409 rename_in_progress`. The server's own message is about the
                            // session endpoint; this says what the owner can actually do about it.
                            if (closed.code == ErrorCodes.RENAME_IN_PROGRESS) {
                                "The PC is renaming that folder; try again when it has finished"
                            } else {
                                closed.message
                            },
                        ),
                    )
                }

                is Rm2Result.Unreachable -> _state.update {
                    it.copy(
                        openingPath = null,
                        openFailure = OpenFailure.Unreachable(path, closed.detail, closed.pinMismatch),
                    )
                }
            }
        }
    }

    fun dismissOpenFailure() {
        _state.update { it.copy(openFailure = null) }
    }

    private fun unreachableText(result: Rm2Result.Unreachable): String =
        if (result.pinMismatch) {
            "The PC answered with a certificate this phone did not pair with."
        } else {
            result.detail
        }

    /**
     * § 5.1: the three codes that mean this phone's token will never be accepted again.
     *
     * Kept apart from every other refusal because the others are about a folder and this one is
     * about the phone: no amount of trying again, refreshing or picking somewhere else will move it.
     */
    private val Rm2Result.Refused.isPairingLost: Boolean
        get() = code == ErrorCodes.TOKEN_REVOKED ||
            code == ErrorCodes.INVALID_TOKEN ||
            code == ErrorCodes.UNAUTHENTICATED

    companion object {
        private const val LOCKED = 423

        fun factory(
            client: Rm2Client,
            lastFolders: LastFolderStore,
            onOpened: (Snapshot) -> Unit,
        ): ViewModelProvider.Factory = viewModelFactory {
            initializer { BrowseViewModel(client, lastFolders, onOpened) }
        }
    }
}
