package com.rankmaster2.phone.net

import kotlinx.serialization.Serializable

/**
 * The wire types of SERVER_SPEC.md § 9, and nothing else.
 *
 * Every 2xx from every session endpoint is the same [Snapshot] - there is no small variant and no
 * partial update - so the whole app is "send an action, get the new state back". A screen that
 * tries to patch its own copy of this is a screen that will disagree with the server.
 *
 * These names are the contract. They are frozen: only integration changes this file, because four
 * other pieces of the app compile against it.
 */
@Serializable
data class Snapshot(
    val sessionId: String,
    val state: String,
    val folder: String,
    val folderName: String,
    val policy: String,
    val openedAt: String,
    val prefetchPairs: Int,
    val sessionVotes: Int,
    val counts: Counts,
    val progress: Double,
    val progressPercent: Int,
    val cues: List<String>,
    val pair: Pair? = null,
    val pairToken: String? = null,
    val pairSeq: Long,
    val warmPairs: List<Pair> = emptyList(),
    val undoAvailable: Boolean,
    val lastAction: LastAction? = null,
    val lastSavedAt: String? = null,
) {
    val isRanking: Boolean get() = state == "ranking"
    val isExhausted: Boolean get() = state == "exhausted"
}

@Serializable
data class Counts(
    val total: Int,
    val rankable: Int,
    val unranked: Int,
    val stills: Int,
    val videos: Int,
)

@Serializable
data class Pair(val left: MediaRef, val right: MediaRef)

@Serializable
data class MediaRef(
    val id: String,
    val kind: String,
    val sizeBytes: Long? = null,
    val mediaVersion: String? = null,
    val links: Links,
) {
    val isStill: Boolean get() = kind == "still"
    val isVideo: Boolean get() = kind == "video"

    /** § 11.3: a null size means the file has gone from the folder under the session. */
    val isMissing: Boolean get() = sizeBytes == null
}

/**
 * § 9.3: server-built, already encoded, already carrying `v=`. Clients use them verbatim. Building
 * these by hand is how four separate clients each invent their own id-encoding bug.
 */
@Serializable
data class Links(
    val meta: String,
    val still: String? = null,
    val thumb: String? = null,
    val video: String? = null,
)

@Serializable
data class LastAction(
    val seq: Long,
    val type: String,
    val pairToken: String? = null,
    val clientRequestId: String? = null,
    val winner: String? = null,
    val side: String? = null,
    val id: String? = null,
    val restoredId: String? = null,
    val undoneType: String? = null,
    val at: String,
)

// ---------------------------------------------------------------------------------------------
// § 10.13 - § 10.15

@Serializable
data class Ping(
    val product: String,
    val apiVersion: String,
    val version: String,
    val ready: Boolean,
    val authenticated: Boolean,
    val certificateFingerprint: String,
    val serverTime: String,
)

@Serializable
data class PairedDevice(
    val deviceId: String,
    val deviceName: String? = null,
    val token: String,
    val issuedAt: String,
    val expiresAt: String? = null,
)

@Serializable
data class Root(
    val path: String,
    val label: String,
    val kind: String,
    val available: Boolean,
    val totalBytes: Long? = null,
    val freeBytes: Long? = null,
)

@Serializable
data class Roots(val roots: List<Root>)

@Serializable
data class Browse(
    val path: String,
    val parent: String? = null,
    val entries: List<BrowseEntry>,
)

@Serializable
data class BrowseEntry(
    val name: String,
    val path: String,
    val stillCount: Int? = null,
    val videoCount: Int? = null,
    /** Null means "not counted". False means counted, and this folder would refuse to open. */
    val rankable: Boolean? = null,
    val hasDatabase: Boolean = false,
    val accessible: Boolean = true,
)

// ---------------------------------------------------------------------------------------------
// § 4, the error envelope

/**
 * The § 5 codes this app branches on. § 4: "Clients MUST branch on this" - on the code, never on
 * the message and never on the status alone.
 */
object ErrorCodes {
    const val NO_SESSION = "no_session"
    const val STALE_PAIR_TOKEN = "stale_pair_token"
    const val NO_CURRENT_PAIR = "no_current_pair"
    const val NOTHING_TO_UNDO = "nothing_to_undo"
    const val UNDO_FOLDER_CHANGED = "undo_folder_changed"
    const val SESSION_ALREADY_OPEN = "session_already_open"
    const val FOLDER_NOT_RANKABLE = "folder_not_rankable"
    const val FOLDER_NOT_FOUND = "folder_not_found"
    const val SESSION_BUSY = "session_busy"
    const val SAVE_FAILED = "save_failed"
    const val MOVE_FAILED = "move_failed"
    const val UNAUTHENTICATED = "unauthenticated"
    const val INVALID_TOKEN = "invalid_token"
    const val TOKEN_REVOKED = "token_revoked"
    const val INVALID_PAIRING_CODE = "invalid_pairing_code"
    const val PAIRING_NOT_OPEN = "pairing_not_open"
    const val TOO_MANY_REQUESTS = "too_many_requests"
    const val MEDIA_FILE_MISSING = "media_file_missing"
    const val UNKNOWN_MEDIA_ID = "unknown_media_id"
    const val WRONG_MEDIA_KIND = "wrong_media_kind"
    const val FOLDER_LOCKED = "folder_locked"
    const val RENAME_IN_PROGRESS = "rename_in_progress"
}

object Sides {
    const val LEFT = "left"
    const val RIGHT = "right"
}
