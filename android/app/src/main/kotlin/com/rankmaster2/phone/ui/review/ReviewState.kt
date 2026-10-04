package com.rankmaster2.phone.ui.review

import com.rankmaster2.phone.net.MediaRef
import com.rankmaster2.phone.net.Snapshot

/**
 * Everything the review screen draws, and nothing about how.
 *
 * Unlike the ranking screen, which is a pure function of the last server snapshot, this one holds a
 * list of its own and edits it *optimistically*: a discard removes the item at once and the PC is
 * told in the background. That is safe here, and not in ranking, because a discard is a move into
 * `<folder>/discarded/` that can be undone, and because a failed one puts the item back.
 */
data class ReviewState(
    /** The latest snapshot the server sent. Its `lastAction` / `undoAvailable` decide Cancel. */
    val snapshot: Snapshot,
    val phase: Phase = Phase.Loading,
    /** Every file of the session in filename order, minus the ones discarded this visit. */
    val items: List<MediaRef> = emptyList(),
    /** The item on screen is `items[index]`; `index == items.size` is the end. */
    val index: Int = 0,
    /** Net number discarded in this visit: a rolled-back or cancelled discard is not counted. */
    val discardedThisVisit: Int = 0,
    /** Whether the Cancel button is live - see [ReviewViewModel.cancel] for the rules. */
    val canCancel: Boolean = false,
    /** A cancel is being taken back. Every control is deaf until it is. */
    val busy: Boolean = false,
    /** Shown as a dismissible banner (or, if [Problem.fatal], a panel with a way out). */
    val problem: Problem? = null,
    /** False while the app is not in front: videos stop and nothing is prefetched. */
    val foreground: Boolean = true,
) {

    enum class Phase {
        /** The list is being read. */
        Loading,

        /** An item is on screen. */
        Reviewing,

        /** Past the last item. */
        Done,

        /** Nothing to show: the list could not be read, or it is empty. [problem] says which. */
        Failed,
    }

    val current: MediaRef? get() = if (phase == Phase.Reviewing) items.getOrNull(index) else null

    /** Position through the list as a whole percentage, 0-100 (never "x of y"). */
    val percent: Int
        get() = when (phase) {
            Phase.Done -> 100
            Phase.Reviewing -> if (items.isEmpty()) 0 else (index * 100 / items.size).coerceIn(0, 100)
            else -> 0
        }

    val folderName: String get() = snapshot.folderName

    /** Swipes and buttons act only on a shown item, with no cancel in flight. */
    val actionable: Boolean get() = phase == Phase.Reviewing && !busy && current != null

    /** Whether a video on screen should be running. */
    val playing: Boolean get() = foreground && phase == Phase.Reviewing

    /** The next [count] items after the current one, for prefetching. */
    fun upcoming(count: Int): List<MediaRef> =
        if (phase != Phase.Reviewing) emptyList() else items.drop(index + 1).take(count)

    data class Problem(val title: String, val body: String, val fatal: Boolean = false)
}
