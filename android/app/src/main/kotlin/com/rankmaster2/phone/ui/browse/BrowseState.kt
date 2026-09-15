package com.rankmaster2.phone.ui.browse

import com.rankmaster2.phone.net.BrowseEntry
import com.rankmaster2.phone.net.Root

/**
 * The state of the folder browser, and the small vocabulary it needs.
 *
 * Everything here is data: no `Context`, no coroutines, no Compose. The screen renders it and the
 * [BrowseViewModel] produces it, which is what makes both testable without a device.
 *
 * The one rule this file exists to enforce is SERVER_SPEC.md § 10.15's: **`null` is not `0`**. The
 * wire type [BrowseEntry] carries three nullable fields that a careless `?: 0` would flatten into
 * "empty folder", and "empty" is the one thing they do not mean. [MediaTally] and [Rankability]
 * keep the three cases apart all the way to the pixels.
 */

/** Where the browser is standing. */
sealed interface Place {

    /** The drive list (§ 10.14). Above every path; not a path itself. */
    data object Roots : Place

    /**
     * One folder's children (§ 10.15). [parent] is the server's, verbatim.
     *
     * § 10.15: "`parent` is `null` at a root". That is the *only* way this app is allowed to know
     * it is at the top. These are Windows paths (`D:\Photos`) reached from an Android phone, and
     * any attempt to find the parent by chopping at a separator gets `\\server\share` wrong, gets
     * `D:\` wrong, and gets extended-length `\\?\D:\x` wrong. So: no path arithmetic, ever.
     */
    data class Folder(val path: String, val parent: String?) : Place {
        val atRoot: Boolean get() = parent == null
    }
}

/**
 * What is inside a child folder, as far as we know.
 *
 * Three cases, because the wire has three: counts absent because nobody counted, counts present and
 * zero, and counts absent because the folder could not be read.
 */
sealed interface MediaTally {

    /**
     * § 10.15: "A child that cannot be enumerated MUST be returned with `accessible: false` and
     * **`null`** counts - not zero." This is that child. It is not empty; we do not know what is in
     * it, and neither does the server.
     */
    data object Unreadable : MediaTally

    /**
     * Nobody has counted yet - either the listing was fetched with `counts=false`, or the counting
     * pass is still in flight. Also not empty.
     */
    data object NotCounted : MediaTally

    /** Counted. [stills] and [videos] may be `0`, and *that* is "counted and empty". */
    data class Counted(val stills: Int, val videos: Int) : MediaTally {
        val total: Int get() = stills + videos
        val isEmpty: Boolean get() = total == 0

        /** SPEC.md § Media policy: a folder holding both ranks its stills only. */
        val isMixed: Boolean get() = stills > 0 && videos > 0
    }
}

/** § 10.15's `rankable`, with its null spelled out instead of implied. */
enum class Rankability {
    /** `rankable: null` - not computed. Says nothing about whether the folder would open. */
    Unknown,

    /** `rankable: true` - `POST /session` would take it. */
    Yes,

    /** `rankable: false` - `POST /session` would answer `409 folder_not_rankable`. */
    No,
}

/** One child folder, ready to draw. */
data class FolderRow(
    val name: String,
    val path: String,
    val tally: MediaTally,
    val rankability: Rankability,
    val hasDatabase: Boolean,
    val accessible: Boolean,
) {

    /**
     * § 10.15 rule 1: a folder that would be refused is **shown greyed, never hidden**. An owner who
     * can see `D:\Photos\Scans` in Explorer and cannot see it here concludes the app is broken and
     * goes hunting; an owner who sees it greyed with "only 1 photo - needs at least 2" knows
     * exactly what to do. So this flag dims a row, it never filters one.
     *
     * [Rankability.Unknown] is deliberately *not* dimmed: with `counts=false` every row is unknown,
     * and greying the whole listing while the counts are in flight would be a lie.
     */
    val openable: Boolean get() = accessible && rankability != Rankability.No

    /** You may walk into any folder the server can read, even one with nothing to rank in it. */
    val enterable: Boolean get() = accessible

    companion object {
        fun of(entry: BrowseEntry): FolderRow = FolderRow(
            name = entry.name,
            path = entry.path,
            tally = tallyOf(entry),
            rankability = when (entry.rankable) {
                null -> Rankability.Unknown
                true -> Rankability.Yes
                false -> Rankability.No
            },
            hasDatabase = entry.hasDatabase,
            accessible = entry.accessible,
        )

        private fun tallyOf(entry: BrowseEntry): MediaTally {
            if (!entry.accessible) return MediaTally.Unreadable
            val stills = entry.stillCount
            val videos = entry.videoCount
            // Either half missing means the pair was never counted. Half a count is not a count.
            if (stills == null || videos == null) return MediaTally.NotCounted
            return MediaTally.Counted(stills, videos)
        }
    }
}

/** How the second, counting request is getting on. The names do not wait for it. */
enum class CountsPhase {
    /** No counting pass is wanted or running (the drive list, or a listing not yet fetched). */
    Idle,

    /** The `counts=true` call is in flight. Names are already on screen. */
    Loading,

    /** Counts merged in. */
    Loaded,

    /**
     * The counting pass failed or timed out. § 10.15 warns this call is one directory walk per
     * child and slow on a network share, so this is an expected outcome, not a crash: the names
     * stay, the counts stay [MediaTally.NotCounted], and the owner can still navigate and open.
     */
    Failed,
}

/**
 * The folder the owner opened last (rule 6: it is the one they want nine times out of ten).
 *
 * Both fields come from the [com.rankmaster2.phone.net.Snapshot] the server returned - `folder` and
 * `folderName` - so the display name is the server's spelling of it and this app still never takes
 * a Windows path apart.
 */
data class RememberedFolder(val path: String, val name: String)

/** Why `POST /session` said no, in cases the owner can act on differently (§ 10.1). */
sealed interface OpenFailure {

    /** The path that was being opened, so a banner can name it. */
    val folder: String

    /** The server's own words, kept for the detail line. Null when nothing answered. */
    val serverMessage: String?

    /** `409 folder_not_rankable` - fewer than two eligible files under the mixed-folder rule. */
    data class NotRankable(override val folder: String, override val serverMessage: String?) : OpenFailure

    /** `404 folder_not_found` - gone since it was listed, or the drive was unplugged. */
    data class NotFound(override val folder: String, override val serverMessage: String?) : OpenFailure

    /**
     * `409 session_already_open` - the PC is ranking something else. § 10.1: "The client MUST
     * `DELETE /session` first. The server MUST NOT close the open session implicitly." So this is
     * the one failure that comes with an action attached: close that one, then open this one.
     */
    data class AlreadyOpen(
        override val folder: String,
        override val serverMessage: String?,
        /** § 5.3's `details.openFolder`: the folder the PC actually has open. */
        val openFolder: String? = null,
    ) : OpenFailure

    /** `423 folder_locked` - `<folder>/.rankmaster.lock` is held (§ 10.1 step 4, § 10.4). */
    data class Locked(override val folder: String, override val serverMessage: String?) : OpenFailure

    /** Any other refusal - shown with the server's code so a bug report can carry it. */
    data class Refused(
        override val folder: String,
        val status: Int,
        val code: String,
        override val serverMessage: String?,
    ) : OpenFailure

    /**
     * Nothing answered. [pinMismatch] is not a network problem: the PC presented a certificate this
     * phone did not pair with, and the only correct move is to stop.
     */
    data class Unreachable(
        override val folder: String,
        val detail: String,
        val pinMismatch: Boolean,
    ) : OpenFailure {
        override val serverMessage: String? get() = null
    }
}

/** Everything the screen draws. */
data class BrowseUiState(
    val place: Place = Place.Roots,

    /** The first request - roots, or a listing's names. A spinner belongs to this, not to counts. */
    val loadingList: Boolean = false,

    /** The names could not be fetched at all. Distinct from [counts] failing. */
    val listFailure: String? = null,

    val roots: List<Root> = emptyList(),
    val rows: List<FolderRow> = emptyList(),
    val counts: CountsPhase = CountsPhase.Idle,

    val lastFolder: RememberedFolder? = null,

    /** The folder whose `POST /session` is in flight, if any. */
    val openingPath: String? = null,
    val openFailure: OpenFailure? = null,
) {

    /** § 10.15: `parent == null` means a root. Not "the path is three characters long". */
    val atRoot: Boolean get() = place is Place.Folder && place.atRoot

    /** There is somewhere to go back to unless we are already looking at the drive list. */
    val canGoUp: Boolean get() = place is Place.Folder

    val currentPath: String? get() = (place as? Place.Folder)?.path

    val isOpening: Boolean get() = openingPath != null
}
