package com.rankmaster2.phone.pairing

import com.rankmaster2.phone.net.Browse
import com.rankmaster2.phone.net.PairedDevice
import com.rankmaster2.phone.net.Ping
import com.rankmaster2.phone.net.Rm2Client
import com.rankmaster2.phone.net.Rm2Result
import com.rankmaster2.phone.net.Roots
import com.rankmaster2.phone.net.Snapshot
import com.rankmaster2.phone.store.Credentials
import com.rankmaster2.phone.store.ServerIdentity

/**
 * A stand-in for the real client, recording what the pairing sequence did and in what order.
 *
 * It implements the frozen `Rm2Client` interface and nothing else - the pairing screens are not
 * allowed to know that an implementation exists, and neither is this fake. Everything past pairing
 * throws: a pairing flow that calls `vote` has a bigger problem than a failing assertion.
 */
class FakeRm2Client(
    override val baseUrl: String,
    private val pingResult: Rm2Result<Ping>,
    private val pairResult: Rm2Result<PairedDevice>,
) : Rm2Client {

    /** Every call made, in order. This is how "the code was never sent" is actually proven. */
    val calls = mutableListOf<String>()

    var codeSent: String? = null
        private set
    var deviceNameSent: String? = null
        private set

    override suspend fun ping(): Rm2Result<Ping> {
        calls += "ping"
        return pingResult
    }

    override suspend fun pair(code: String, deviceName: String): Rm2Result<PairedDevice> {
        calls += "pair"
        codeSent = code
        deviceNameSent = deviceName
        return pairResult
    }

    override suspend fun revoke(deviceId: String): Rm2Result<Unit> = unexpected("revoke")
    override suspend fun roots(): Rm2Result<Roots> = unexpected("roots")
    override suspend fun browse(path: String, counts: Boolean): Rm2Result<Browse> = unexpected("browse")
    override suspend fun openSession(folder: String): Rm2Result<Snapshot> = unexpected("openSession")
    override suspend fun session(): Rm2Result<Snapshot> = unexpected("session")
    override suspend fun closeSession(): Rm2Result<Unit> = unexpected("closeSession")
    override suspend fun save(): Rm2Result<Snapshot> = unexpected("save")

    override suspend fun vote(pairToken: String, winner: String, clientRequestId: String) =
        unexpected<Snapshot>("vote")

    override suspend fun skip(pairToken: String, clientRequestId: String) = unexpected<Snapshot>("skip")

    override suspend fun discard(pairToken: String, side: String, clientRequestId: String) =
        unexpected<Snapshot>("discard")

    override suspend fun special(pairToken: String, side: String, clientRequestId: String) =
        unexpected<Snapshot>("special")

    override suspend fun cancel(clientRequestId: String) = unexpected<Snapshot>("cancel")

    override fun url(link: String): String = baseUrl + link

    private fun <T> unexpected(name: String): Rm2Result<T> =
        throw AssertionError("pairing must not call $name")
}

/** The credential store, in memory, remembering how often it was written. */
class FakeCredentials : Credentials {

    var stored: ServerIdentity? = null
        private set

    var saves = 0
        private set

    var clears = 0
        private set

    override fun current(): ServerIdentity? = stored

    override fun save(identity: ServerIdentity) {
        stored = identity
        saves++
    }

    override fun clear() {
        stored = null
        clears++
    }
}

/** A `/ping` body with the fingerprint under test and everything else plausible. */
fun ping(fingerprint: String, ready: Boolean = true) = Ping(
    product = "Rank Master 2 server",
    apiVersion = "v1",
    version = "1.1.3",
    ready = ready,
    authenticated = false,
    certificateFingerprint = fingerprint,
    serverTime = "2026-09-15T18:04:11.412Z",
)
