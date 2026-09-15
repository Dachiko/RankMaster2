package com.rankmaster2.phone.ui.rank

import com.rankmaster2.phone.net.LastAction
import com.rankmaster2.phone.net.MediaRef
import com.rankmaster2.phone.net.Snapshot

/**
 * Everything the ranking screen draws, and nothing about how.
 *
 * The screen is a pure function of the last [Snapshot] the server sent. It never patches its own
 * copy: every action returns the whole new state, so there is no local model to drift out of step
 * with the PC. That is why there is no "optimistic" vote here — a vote that has not been answered
 * has not happened.
 */
data class RankState(
    val snapshot: Snapshot? = null,
    /** An action is in flight. The vote gesture is deaf until it resolves — see [RankViewModel]. */
    val busy: Boolean = false,
    /** Shown briefly after something happened, then gone. Never covers a pane. */
    val notice: String? = null,
    /** A failure that needs a person: unreachable, a refusal we cannot act on, a lost session. */
    val problem: Problem? = null,
    /** The pane whose long-press menu is open, if any. */
    val paneMenu: Side? = null,
    val overflowOpen: Boolean = false,
    /** Set while the app is not in front, so videos stop and nothing is prefetched. */
    val foreground: Boolean = true,
    /**
     * Which pane is open full screen, if any. While something is, the panes underneath must stop:
     * a second player on the same file means two hardware decoders on one video, which is how the
     * app died the first time it was asked to watch one.
     */
    val viewing: Side? = null,
) {
    val pair get() = snapshot?.pair
    val left: MediaRef? get() = pair?.left
    val right: MediaRef? get() = pair?.right

    /** § 9.1: `cues` is the match strip — up to ten, oldest first, empty until the first vote. */
    val cues: List<String> get() = snapshot?.cues.orEmpty()

    /** § 10.10: there is an action to take back. The notch appears only when this is true. */
    val canCancel: Boolean get() = snapshot?.undoAvailable == true

    val exhausted: Boolean get() = snapshot?.isExhausted == true

    /** A pane may be acted on only while a token exists and nothing else is in flight. */
    val actionable: Boolean get() = !busy && snapshot?.pairToken != null

    /** Whether the panes behind should be running. */
    val panesPlaying: Boolean get() = foreground && viewing == null

    data class Problem(val title: String, val body: String, val fatal: Boolean = false)
}

enum class Side { LEFT, RIGHT;

    val wire: String get() = if (this == LEFT) "left" else "right"
}

/**
 * What the last action was, in words, for the one-line notice after a cancel.
 *
 * § 9.4's `undoneType` exists exactly so a client can say "vote taken back" rather than a bare
 * "undone", which would leave the owner wondering which of four things just happened.
 */
fun noticeFor(action: LastAction?): String? = when {
    action == null -> null
    action.type == "undo" -> when (action.undoneType) {
        "vote" -> "Vote taken back"
        "skip" -> "Skip taken back"
        "discard" -> "Discard taken back" + restored(action)
        "special" -> "Moved back out of special" + restored(action)
        else -> "Taken back"
    }
    action.type == "discard" -> "Discarded ${action.id.orEmpty()}"
    action.type == "special" -> "Moved to special"
    action.type == "drop_missing" -> "${action.id.orEmpty()} is gone from the folder"
    else -> null
}

/**
 * § 10.10: the file can come back under a different name when its own was taken in the meantime.
 * Saying so is the difference between the owner finding it again and not.
 */
private fun restored(action: LastAction): String {
    val back = action.restoredId ?: return ""
    val was = action.id ?: return ""
    return if (back == was) "" else " as $back"
}
