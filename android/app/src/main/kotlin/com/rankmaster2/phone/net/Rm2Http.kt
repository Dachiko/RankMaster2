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
     * The client for a paired server: pinned to its certificate, carrying its token on every
     * request. Cached per identity, because OkHttp clients are meant to be shared - a fresh one per
     * call throws away the connection pool and the disk cache along with it.
     */
    @Synchronized
    fun client(identity: ServerIdentity): OkHttpClient =
        clients.getOrPut(identity.certificateFingerprint + "|" + identity.token.take(12)) {
            build(identity.certificateFingerprint, identity.token)
        }

    /**
     * The client used while pairing: the fingerprint is known (the QR carried it) but no token
     * exists yet. The order matters and is normative - SERVER_SPEC.md § 10.1.1 requires the client
     * to pin *before* it sends the code, because the code is a bearer secret and handing it to an
     * unverified peer hands it to whoever answered.
     */
    fun pairingClient(fingerprint: String): OkHttpClient = build(fingerprint, token = null)

    private fun build(fingerprint: String, token: String?): OkHttpClient {
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

        cacheDirectory?.let { builder.cache(Cache(it, CACHE_BYTES)) }

        if (token != null) {
            builder.addInterceptor { chain ->
                chain.proceed(
                    chain.request().newBuilder()
                        .header("Authorization", "Bearer $token")
                        .build()
                )
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
