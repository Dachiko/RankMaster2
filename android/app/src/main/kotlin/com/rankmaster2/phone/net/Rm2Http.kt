package com.rankmaster2.phone.net

import com.rankmaster2.phone.store.ServerIdentity
import java.io.File
import java.security.MessageDigest
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import java.util.concurrent.TimeUnit
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLSession
import javax.net.ssl.X509TrustManager
import okhttp3.Cache
import okhttp3.OkHttpClient

/**
 * The one HTTP client in this app, and the reason there is only one.
 *
 * Images (Coil), video (Media3) and every API call go through the client this builds. A second HTTP
 * stack anywhere is a second place the certificate pin and the bearer token can be forgotten, which
 * is how an app that says it pins its certificate turns out not to.
 *
 * ## Why a trust manager and not OkHttp's CertificatePinner
 *
 * `CertificatePinner` runs *after* the chain has been validated against the device's trust store.
 * This server signs its own certificate, so that validation fails before the pin is ever consulted
 * and every request dies. The pin has to *be* the trust decision, not an extra check on top of one.
 *
 * ## What is trusted
 *
 * Exactly one thing: a leaf certificate whose SHA-256 over its DER encoding equals the fingerprint
 * this device stored when it paired - the same string `/ping` reports and the pairing QR carries.
 * Not a CA, not a name, not an expiry window. A mismatch is [PinMismatchException] and the app must
 * stop on it: on a home network there is no innocent reason for it, and the one guilty reason is
 * the entire attack this pin exists to prevent.
 *
 * Host name checking is deliberately not part of the decision. The certificate names the address
 * the server was first configured with, and the owner may have moved it since; the fingerprint
 * already identifies the machine far more tightly than a name in a self-signed certificate could.
 */
object Rm2Http {

    private const val CACHE_BYTES = 512L * 1024 * 1024
    private var cacheDirectory: File? = null
    private val clients = HashMap<String, OkHttpClient>()

    /**
     * The live bearer token for a paired fingerprint, read fresh by [client]'s interceptor on every
     * request rather than baked into the client at build time. This is what lets [client] key its
     * map on the fingerprint alone - see [client]'s own doc for why that matters (§ 3.7).
     */
    private val tokens = HashMap<String, String>()

    /**
     * Call once at startup, **before anything asks for a client**. The cache is what makes each
     * photo cross the network once, ever.
     *
     * Until this runs there is no disk cache at all, and without one every promise the media layer
     * is built on quietly stops being true: a photograph is re-fetched every time it comes back
     * round, and the warm-pair prefetch writes its bytes into nothing and helps nobody. The server
     * goes to the trouble of sending a strong ETag and `immutable` on every byte response
     * (SERVER_SPEC.md section 12.5) precisely so this is free; JSON endpoints are `no-store` at the
     * server, so nothing that must be fresh is at risk.
     *
     * Any client built before this call has no cache in it, so they are dropped rather than left
     * in the map: a cacheless client that outlives configuration is the same bug one restart later.
     */
    @Synchronized
    fun configure(cacheDir: File) {
        val directory = File(cacheDir, "rm2-media")
        if (directory == cacheDirectory) return
        cacheDirectory = directory
        clients.clear()
    }

    /** What [configure] set, or null if nothing has. Here so a test can see the difference. */
    @Synchronized
    fun cacheDirectory(): File? = cacheDirectory

    /**
     * Empties every cached client's disk cache in place - `Cache.evictAll()`, never a close or a
     * delete of the directory. The client keeps working exactly as before, just re-filling from
     * nothing rather than from what was there.
     *
     * The live-instance half of "nothing survives while the app is not running" (the owner's ask,
     * 2026-09-17): `MediaCacheJanitor.wipeBeforeUse` deletes the directory outright before anything
     * has opened it, which is the half that actually holds against a killed process; this is what
     * backs it up for the ordinary case, where something does get to run a shutdown path. One
     * client's cache failing to evict does not stop the others - `evictAll` can throw on a strange
     * disk state, and this is best-effort by design (see `MediaCacheJanitor`).
     */
    @Synchronized
    fun evictMediaCache() {
        clients.values.forEach { client -> runCatching { client.cache?.evictAll() } }
    }

    /**
     * The client for a paired server: pinned to its certificate, carrying its token on every
     * request. Cached per **fingerprint**, not per `(fingerprint, token)` pair - re-pairing the same
     * PC names a fresh token for a certificate this app already has a client for.
     *
     * § 3.7 (the second audit): keying on the token too, as this used to, made re-pairing build a
     * second client - and, because every client with a cache directory configured gets its own
     * `Cache` object, a second `Cache` pointed at the very directory the first one still had open.
     * OkHttp documents that as corrupting. The fix is not to evict and rebuild on a new token - that
     * only moves the same race earlier - but to never need to: the token lives in [tokens], read
     * fresh by the interceptor on every request, so re-pairing updates *what this one client sends*
     * rather than building another one.
     */
    @Synchronized
    fun client(identity: ServerIdentity): OkHttpClient {
        tokens[identity.certificateFingerprint] = identity.token
        return clients.getOrPut(identity.certificateFingerprint) {
            build(identity.certificateFingerprint, authenticated = true)
        }
    }

    /**
     * The client used while pairing: the fingerprint is known (the QR carried it) but no token
     * exists yet. The order matters and is normative - SERVER_SPEC.md § 10.1.1 requires the client
     * to pin *before* it sends the code, because the code is a bearer secret and handing it to an
     * unverified peer hands it to whoever answered.
     *
     * Built fresh every call, deliberately outside [clients] - pairing is a handful of requests and
     * never touches a photograph, so it has no business with the disk cache, and a client here never
     * risks becoming the second `Cache` on [cacheDirectory] that § 3.7 was about.
     */
    fun pairingClient(fingerprint: String): OkHttpClient = build(fingerprint, authenticated = false)

    private fun build(fingerprint: String, authenticated: Boolean): OkHttpClient {
        val trust = PinnedTrustManager(fingerprint)
        val context = SSLContext.getInstance("TLS").apply {
            init(null, arrayOf(trust), java.security.SecureRandom())
        }

        val builder = OkHttpClient.Builder()
            .sslSocketFactory(context.socketFactory, trust)
            // The pin is the identity. See the class comment.
            .hostnameVerifier { _: String, _: SSLSession -> true }
            .connectTimeout(5, TimeUnit.SECONDS)
            .readTimeout(30, TimeUnit.SECONDS)
            .writeTimeout(15, TimeUnit.SECONDS)
            // Off: this app decides when to repeat a request, because repeating an action is the
            // one thing that can double-count a vote (SERVER_SPEC.md § 13.3).
            .retryOnConnectionFailure(false)

        if (authenticated) {
            cacheDirectory?.let { builder.cache(Cache(it, CACHE_BYTES)) }
            builder.addInterceptor { chain ->
                val token = synchronized(this) { tokens[fingerprint] }
                val request = if (token != null) {
                    chain.request().newBuilder().header("Authorization", "Bearer $token").build()
                } else {
                    chain.request()
                }
                chain.proceed(request)
            }
        }

        return builder.build()
    }

    /** The fingerprint of a certificate, in the `sha256:<64 hex>` form the server publishes. */
    fun fingerprintOf(certificate: X509Certificate): String {
        val digest = MessageDigest.getInstance("SHA-256").digest(certificate.encoded)
        return "sha256:" + digest.joinToString("") { "%02x".format(it) }
    }

    private class PinnedTrustManager(pinned: String) : X509TrustManager {
        private val expected = pinned.trim().lowercase().removePrefix("sha256:")

        override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
            val leaf = chain?.firstOrNull()
                ?: throw CertificateException("The server presented no certificate at all.")

            val actual = fingerprintOf(leaf).removePrefix("sha256:")
            if (!MessageDigest.isEqual(actual.toByteArray(), expected.toByteArray())) {
                throw PinMismatchException(expected = expected, actual = actual)
            }
        }

        override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) {
            throw CertificateException("This app is never a TLS server.")
        }

        override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
    }
}

/**
 * The server presented a certificate this device has not pinned. Never retried, never excused, and
 * never offered to the user as something to continue past.
 */
class PinMismatchException(val expected: String, val actual: String) : CertificateException(
    "The server's certificate does not match the one this phone paired with. " +
        "Expected sha256:$expected, got sha256:$actual."
)
