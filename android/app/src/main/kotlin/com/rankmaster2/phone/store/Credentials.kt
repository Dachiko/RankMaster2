package com.rankmaster2.phone.store

/**
 * What this device knows about one server: where it is, which certificate is *the* certificate, and
 * the token that proves this phone was paired.
 *
 * **Frozen.** Only integration changes this file.
 *
 * The fingerprint is not a convenience - it is the whole of this app's security. The server signs
 * its own certificate, so there is no authority to appeal to: a certificate that does not match this
 * string is not the user's PC, and the only correct response is to stop.
 */
data class ServerIdentity(
    val host: String,
    val port: Int,
    /** `sha256:` followed by 64 lowercase hex characters, exactly as `/ping` reports it. */
    val certificateFingerprint: String,
    val token: String,
    val deviceId: String? = null,
) {
    val baseUrl: String get() = "https://$host:$port/api/v1"
}

interface Credentials {
    /** The paired server, or null if this phone has never been paired. */
    fun current(): ServerIdentity?

    fun save(identity: ServerIdentity)

    /** Forgets everything. Used by "forget this device" after the token is revoked. */
    fun clear()
}
