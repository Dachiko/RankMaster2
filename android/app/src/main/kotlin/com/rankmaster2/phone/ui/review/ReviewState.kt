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
 *
 * What is *not* here is anything to say about how it went: there is no progress, no counts and no
 * banner. The screen is the item, the cancel notch and a menu; a failure that matters is
 * [problem], and everything else is dealt with without a word.
 */
data class ReviewState(
    /** The latest snapshot the server sent. Its `lastAction` / `undoAvailable` decide Cancel. */
    val snapshot: Snapshot,
    val phase: Phase = Phase.Loading,
    /** Every file of the session in natural filename order, minus the ones discarded this visit. */
    val items: List<MediaRef> = emptyList(),
    /** The item on screen is `items[index]`; `index == items.size` is the end. */
    val index: Int = 0,
    /** Whether the Cancel notch is live - see [ReviewViewModel.cancel] for the rules. */
    val canCancel: Boolean = false,
    /** A cancel is being taken back. Every control is deaf until it is. */
    val busy: Boolean = false,
    /** The id of the item the open menu is about, or null with no menu. */
    val menuFor: String? = null,
    /**
     * Only ever a *fatal* problem (session gone, pairing lost, old server, wrong certificate) or
     * the reason the first load failed. Nothing else is ever put on screen.
     */
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

    /** Taps and swipes act only on a shown item, with no cancel in flight and nothing fatal up. */
    val actionable: Boolean get() = phase == Phase.Reviewing && !busy && current != null && problem?.fatal != true

    /** Whether a video on screen should be running. */
    val playing: Boolean get() = foreground && phase == Phase.Reviewing

    /** The next [count] items after the current one, for prefetching. */
    fun upcoming(count: Int): List<MediaRef> =
        if (phase != Phase.Reviewing) emptyList() else items.drop(index + 1).take(count)

    data class Problem(val title: String, val body: String, val fatal: Boolean = false)
}
