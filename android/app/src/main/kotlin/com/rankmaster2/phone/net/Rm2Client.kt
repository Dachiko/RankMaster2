package com.rankmaster2.phone.net

/**
 * Every call this app makes to the server, and the only way it is allowed to make one.
 *
 * **Frozen.** Four pieces of the app compile against this file; only integration changes it.
 *
 * Two rules the implementation must keep, because nothing else in the app can:
 *
 *  1. **One OkHttp client, pinned and authenticated.** Images and video go through the same one, so
 *     there is no second HTTP stack that forgot the certificate pin or the token.
 *  2. **Never retry an action with a fresh token.** [vote], [skip], [discard] and [special] carry
 *     the token of the pair they acted on. If the call times out, the retry sends the *same* token:
 *     the server either applies it or answers `stale_pair_token` carrying the current state
 *     (SERVER_SPEC.md § 13.3). Resyncing first and re-sending is a double vote, and is the one
 *     thing this app must make impossible.
 */
interface Rm2Client {

    /** Where this client is pointed, e.g. `https://192.168.1.42:18611/api/v1`. */
    val baseUrl: String

    // -- connection ----------------------------------------------------------------------------

    suspend fun ping(): Rm2Result<Ping>

    /** § 10.11. Exchanges a one-time pairing code for a long-lived device token. */
    suspend fun pair(code: String, deviceName: String): Rm2Result<PairedDevice>

    /** § 10.12. Revokes this device's own token. */
    suspend fun revoke(deviceId: String): Rm2Result<Unit>

    // -- picking a folder ----------------------------------------------------------------------

    suspend fun roots(): Rm2Result<Roots>

    /** § 10.15. `counts = false` on a big or networked tree; counting is a directory walk per child. */
    suspend fun browse(path: String, counts: Boolean = true): Rm2Result<Browse>

    // -- the session ---------------------------------------------------------------------------

    suspend fun openSession(folder: String): Rm2Result<Snapshot>
    suspend fun session(): Rm2Result<Snapshot>
    suspend fun closeSession(): Rm2Result<Unit>
    suspend fun save(): Rm2Result<Snapshot>

    // -- the actions ---------------------------------------------------------------------------
    // Each carries the token of the pair it acts on, and a clientRequestId so a retry can be
    // recognised on the other side (§ 9.4).

    suspend fun vote(pairToken: String, winner: String, clientRequestId: String): Rm2Result<Snapshot>
    suspend fun skip(pairToken: String, clientRequestId: String): Rm2Result<Snapshot>
    suspend fun discard(pairToken: String, side: String, clientRequestId: String): Rm2Result<Snapshot>
    suspend fun special(pairToken: String, side: String, clientRequestId: String): Rm2Result<Snapshot>

    /**
     * § 10.10. Cancel: takes back the last action of any kind - vote, skip, discard or special -
     * one level. Takes no pairToken, deliberately: the action it reverses belongs to an earlier
     * pair generation, so any token the client holds for it is already stale.
     */
    suspend fun cancel(clientRequestId: String): Rm2Result<Snapshot>

    // -- bytes ---------------------------------------------------------------------------------

    /** Resolves a `links.*` value against [baseUrl]. Links are used verbatim (§ 9.3). */
    fun url(link: String): String
}

/**
 * What a call answers. Deliberately three cases and not an exception: a `409 stale_pair_token`
 * carries the full current state in the same envelope (§ 8.5), and that is the difference between
 * a client that resynchronises in one round trip and one that polls.
 */
sealed interface Rm2Result<out T> {

    data class Ok<T>(val value: T) : Rm2Result<T>

    /**
     * The server answered, and said no. [session] is the snapshot the envelope carried, when it
     * carried one - which is how a stale token self-heals without a second request.
     */
    data class Refused(
        val status: Int,
        val code: String,
        val message: String,
        val session: Snapshot? = null,
        val requestId: String? = null,
    ) : Rm2Result<Nothing>

    /**
     * No answer: the network, the TLS handshake, a timeout. [pinMismatch] is the one that is never
     * a network problem - it means the server presented a certificate this device has not pinned,
     * and the app must stop rather than offer to continue.
     */
    data class Unreachable(
        val cause: Throwable?,
        val detail: String,
        val pinMismatch: Boolean = false,
    ) : Rm2Result<Nothing>
}

inline fun <T> Rm2Result<T>.onOk(block: (T) -> Unit): Rm2Result<T> {
    if (this is Rm2Result.Ok) block(value)
    return this
}

/** The snapshot a call produced, or the one its refusal carried. Null only when nothing arrived. */
fun Rm2Result<Snapshot>.snapshotOrNull(): Snapshot? = when (this) {
    is Rm2Result.Ok -> value
    is Rm2Result.Refused -> session
    is Rm2Result.Unreachable -> null
}
