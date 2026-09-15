package com.rankmaster2.phone.net.impl

import kotlinx.serialization.Serializable

/**
 * The request bodies of SERVER_SPEC.md § 10, and nothing else.
 *
 * They are declared rather than hand-assembled so that a field name can only be wrong in one place,
 * and so that `pairToken` and `clientRequestId` are structurally impossible to forget on an action
 * (§ 8.4, § 8.5) - the whole no-double-vote story rests on both of them arriving.
 */

/** § 10.1. */
@Serializable
internal data class OpenSessionRequest(val folder: String)

/** § 10.6. `winner` is `left` or `right`; the server answers `400 invalid_side` for anything else. */
@Serializable
internal data class VoteRequest(
    val pairToken: String,
    val winner: String,
    val clientRequestId: String,
)

/** § 10.7. */
@Serializable
internal data class SkipRequest(
    val pairToken: String,
    val clientRequestId: String,
)

/** § 10.8 and § 10.9 - identical bodies; only the path differs. */
@Serializable
internal data class SideActionRequest(
    val pairToken: String,
    val side: String,
    val clientRequestId: String,
)

/**
 * § 10.10. Deliberately carries no `pairToken`: the action being cancelled belongs to an earlier
 * pair generation, so any token the client holds for it is stale by construction. The server would
 * ignore one, but sending it would invite a reader to think undo is pair-scoped. It is not.
 */
@Serializable
internal data class CancelRequest(val clientRequestId: String)

/** § 10.11. */
@Serializable
internal data class PairRequest(
    val code: String,
    val deviceName: String,
)
