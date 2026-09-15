package com.rankmaster2.phone.ui.browse

/**
 * The words on the screen, as pure functions of the state.
 *
 * They live outside the composables so that "a folder that cannot be read says *cannot be read*,
 * not *empty*" is a unit test rather than something you have to squint at on a phone. They are
 * plain Kotlin strings and not resources for the same reason: this app ships in one language, and a
 * `Context` in the middle of a formatting function would drag Robolectric into every assertion.
 */

/** "3 photos", "1 video", "12 photos and 4 videos" - for a tally we actually have. */
private fun countPhrase(tally: MediaTally.Counted): String = when {
    tally.isEmpty -> "empty"
    tally.videos == 0 -> plural(tally.stills, "photo", "photos")
    tally.stills == 0 -> plural(tally.videos, "video", "videos")
    else -> plural(tally.stills, "photo", "photos") + " and " + plural(tally.videos, "video", "videos")
}

private fun plural(n: Int, one: String, many: String): String = "$n " + if (n == 1) one else many

/**
 * The line under a folder's name.
 *
 * The three cases § 10.15 keeps apart stay apart here:
 *  - [MediaTally.Unreadable] - "cannot be read". **Never** "empty": the counts are null because the
 *    enumeration failed, and an owner told "empty" about a folder full of photos will believe the
 *    photos are gone.
 *  - [MediaTally.NotCounted] - no claim at all while `counts=false`, or "counting…" while the
 *    second pass is in flight. Saying "0" here would be inventing a fact.
 *  - [MediaTally.Counted] - the number, including a real zero.
 */
fun folderDetail(row: FolderRow, counting: Boolean = false): String {
    val base = when (val t = row.tally) {
        MediaTally.Unreadable -> "cannot be read"
        MediaTally.NotCounted -> if (counting) "counting…" else ""
        is MediaTally.Counted -> countPhrase(t)
    }
    if (!row.hasDatabase) return base
    // § 10.15: hasDatabase says the file is there, not that it parses. Phrase it as a fact, not a
    // promise, so a corrupt file arriving later as `library_json_unreadable` is not a contradiction.
    return if (base.isEmpty()) "has ranking history" else "$base · has ranking history"
}

/**
 * Why a greyed folder is greyed, in a form the owner can act on.
 *
 * Only ever called for [Rankability.No]: the server has already decided this folder would be
 * refused, and repeating its arithmetic here would be a second implementation of the rule that
 * could disagree with the first. What the counts are used for is *explaining* the refusal, which is
 * exactly the job § 10.15 says they exist for ("so a client can grey out folders that would return
 * `409 folder_not_rankable`").
 */
fun notRankableReason(row: FolderRow): String = when (val t = row.tally) {
    MediaTally.Unreadable -> "cannot be read"
    MediaTally.NotCounted -> "the PC will not rank this folder"
    is MediaTally.Counted -> when {
        t.isEmpty -> "nothing to rank in here"
        // SPEC.md § Media policy: both kinds present means stills only, so the videos do not count
        // towards the two. This is the reason that looks like a bug if you do not explain it.
        t.isMixed -> "mixed folder - only the ${plural(t.stills, "photo", "photos")} can be ranked, and ranking needs 2"
        else -> "only ${countPhrase(t)} - ranking needs 2"
    }
}

/** The headline of the failure banner: what happened, in the owner's terms. */
fun openFailureHeadline(failure: OpenFailure): String = when (failure) {
    is OpenFailure.NotRankable -> "Nothing to rank in that folder"
    is OpenFailure.NotFound -> "That folder is not there any more"
    is OpenFailure.AlreadyOpen -> "A folder is still open on the PC"
    is OpenFailure.Locked -> "That folder is in use"
    is OpenFailure.Refused -> "The PC would not open that folder"
    is OpenFailure.Unreachable ->
        if (failure.pinMismatch) "That is not your PC" else "Cannot reach the PC"
}

/** What to do about it. One sentence, and every one of them names a next move. */
fun openFailureAdvice(failure: OpenFailure): String = when (failure) {
    is OpenFailure.NotRankable ->
        "Ranking needs at least two photos, or at least two videos, in the folder itself - " +
            "files in sub-folders do not count. Pick a different folder."

    is OpenFailure.NotFound ->
        "It has been moved, renamed, or its drive was unplugged since this list was made. " +
            "Go back and refresh."

    is OpenFailure.AlreadyOpen -> buildString {
        // Almost always this phone's own doing: an earlier run left the folder open, and the PC
        // holds it until someone says otherwise. Saying "the PC is ranking something" sends the
        // owner looking for a window that is not there.
        append("The PC still has ")
        append(failure.openFolder?.let { "\"" + it.substringAfterLast('\\') + "\"" } ?: "a folder")
        append(" open - probably left behind by this phone. Nothing is being ranked there and ")
        append("nothing is lost; close it and this folder opens.")
    }

    is OpenFailure.Locked ->
        "Something else is holding this folder (the Rank Master window on the PC, or another " +
            "phone). Close it there, then try again."

    is OpenFailure.Refused ->
        failure.serverMessage ?: "The PC answered ${failure.status} ${failure.code}."

    is OpenFailure.Unreachable ->
        if (failure.pinMismatch) {
            "The PC answered with a different certificate than the one this phone was paired " +
                "with. Do not continue - pair again from the PC."
        } else {
            "${failure.detail}. Check the PC is awake and on the same network, then try again."
        }
}

/** True when the banner should offer "close it and open this one" (§ 10.1: the client must ask). */
fun offersCloseAndRetry(failure: OpenFailure): Boolean = failure is OpenFailure.AlreadyOpen

/**
 * A folder's name, for a heading. **Display only.**
 *
 * Everywhere this app *navigates*, it uses the `path` and `parent` values the server handed back and
 * never takes a Windows path apart - section 10.15, and the reason `\\server\share`, `D:\` and
 * `\\?\D:\x` are all roots that look nothing like each other. This is the one exception and it is
 * safe because nothing is done with the result except draw it: if the slicing is wrong the owner
 * sees an odd word at the top of a list, not a request for the wrong folder.
 *
 * It earns its place because the heading it replaces said "12 folders", which tells the owner
 * how many things are below him and nothing at all about where he is standing.
 */
fun folderLabel(path: String): String {
    val trimmed = path.trimEnd('\\', '/')
    // A drive root or a bare share trims away to nothing useful; show it as it is.
    if (trimmed.isEmpty()) return path
    val cut = maxOf(trimmed.lastIndexOf('\\'), trimmed.lastIndexOf('/'))
    val tail = if (cut < 0) trimmed else trimmed.substring(cut + 1)
    return tail.ifBlank { path }
}

/** "12 folders", or nothing at all when there are none to count. */
fun folderCount(rows: Int): String = when (rows) {
    0 -> ""
    1 -> "1 folder"
    else -> "$rows folders"
}

/** The drive-list subtitle: "234 GB free of 1.8 TB", or why the drive is no use right now. */
fun rootDetail(available: Boolean, totalBytes: Long?, freeBytes: Long?): String {
    // § 10.14: a listed-but-unavailable root must still appear. Say why rather than dim it silently.
    if (!available) return "not connected"
    if (freeBytes == null && totalBytes == null) return ""
    if (totalBytes == null) return "${bytes(freeBytes!!)} free"
    if (freeBytes == null) return bytes(totalBytes)
    return "${bytes(freeBytes)} free of ${bytes(totalBytes)}"
}

private fun bytes(n: Long): String {
    val units = listOf("bytes", "KB", "MB", "GB", "TB", "PB")
    var value = n.toDouble()
    var unit = 0
    while (value >= 1024 && unit < units.lastIndex) {
        value /= 1024
        unit++
    }
    return if (unit == 0) "$n bytes"
    else if (value >= 100) "${value.toLong()} ${units[unit]}"
    else String.format(java.util.Locale.US, "%.1f %s", value, units[unit])
}
