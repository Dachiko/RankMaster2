package com.rankmaster2.phone.net

import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.math.BigInteger
import java.nio.charset.StandardCharsets
import java.security.KeyPair
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.SecureRandom
import java.security.Signature
import java.security.cert.CertificateFactory
import java.security.cert.X509Certificate
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.TimeZone
import javax.net.ssl.KeyManagerFactory
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLSocketFactory

/**
 * A throwaway self-signed certificate, and the [SSLSocketFactory] that serves it.
 *
 * It is written out here in DER by hand because the test classpath has `mockwebserver` but not
 * `okhttp-tls`, where [okhttp3.tls.HeldCertificate] lives, and `app/build.gradle.kts` is not this
 * agent's to change. That is the only reason; nothing about the pin needs it.
 *
 * What it produces is precisely what the real server produces and what these tests must be able to
 * pin against: a certificate signed by nobody, identified by nothing but the SHA-256 of its own DER
 * encoding. It is a **test fixture** - it has no extensions, no key usage and no SAN, and is not a
 * model for how the server should make its own.
 */
class SelfSignedCertificate(commonName: String = "rank-master-2-test") {

    private val keyPair: KeyPair = KeyPairGenerator.getInstance("RSA")
        .apply { initialize(2048, SecureRandom()) }
        .generateKeyPair()

    val certificate: X509Certificate = build(commonName)

    /** What MockWebServer serves. The private key never leaves this object. */
    fun sslSocketFactory(): SSLSocketFactory {
        val password = "rankmaster".toCharArray()
        val store = KeyStore.getInstance("PKCS12").apply {
            load(null, password)
            setKeyEntry("rm2", keyPair.private, password, arrayOf<java.security.cert.Certificate>(certificate))
        }
        val managers = KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm())
            .apply { init(store, password) }
        return SSLContext.getInstance("TLS")
            .apply { init(managers.keyManagers, null, SecureRandom()) }
            .socketFactory
    }

    private fun build(commonName: String): X509Certificate {
        val name = distinguishedName(commonName)
        val now = System.currentTimeMillis()

        val tbs = sequence(
            integer(BigInteger(64, SecureRandom()).add(BigInteger.ONE)),
            SHA256_WITH_RSA,
            name,
            sequence(
                utcTime(Date(now - ONE_DAY)),
                utcTime(Date(now + 365 * ONE_DAY)),
            ),
            name,
            // The X.509 encoding of a public key is already a SubjectPublicKeyInfo.
            keyPair.public.encoded,
        )

        val signature = Signature.getInstance("SHA256withRSA")
            .apply { initSign(keyPair.private); update(tbs) }
            .sign()

        val der = sequence(tbs, SHA256_WITH_RSA, bitString(signature))
        return CertificateFactory.getInstance("X.509")
            .generateCertificate(ByteArrayInputStream(der)) as X509Certificate
    }

    private fun distinguishedName(commonName: String): ByteArray = sequence(
        set(
            sequence(
                OID_COMMON_NAME,
                tlv(0x13, commonName.toByteArray(StandardCharsets.US_ASCII)),
            ),
        ),
    )

    private companion object {
        const val ONE_DAY = 24L * 60 * 60 * 1000

        /** OID 2.5.4.3, `commonName`. */
        val OID_COMMON_NAME = byteArrayOf(0x06, 0x03, 0x55, 0x04, 0x03)

        /** `AlgorithmIdentifier { 1.2.840.113549.1.1.11, NULL }` - sha256WithRSAEncryption. */
        val SHA256_WITH_RSA = byteArrayOf(
            0x30, 0x0D,
            0x06, 0x09, 0x2A, 0x86.toByte(), 0x48, 0x86.toByte(), 0xF7.toByte(), 0x0D, 0x01, 0x01, 0x0B,
            0x05, 0x00,
        )

        fun tlv(tag: Int, content: ByteArray): ByteArray {
            val out = ByteArrayOutputStream()
            out.write(tag)
            if (content.size < 0x80) {
                out.write(content.size)
            } else {
                val length = BigInteger.valueOf(content.size.toLong()).toByteArray()
                    .let { if (it[0] == 0.toByte()) it.copyOfRange(1, it.size) else it }
                out.write(0x80 or length.size)
                out.write(length)
            }
            out.write(content)
            return out.toByteArray()
        }

        fun concat(vararg parts: ByteArray): ByteArray {
            val out = ByteArrayOutputStream()
            parts.forEach { out.write(it) }
            return out.toByteArray()
        }

        fun sequence(vararg parts: ByteArray): ByteArray = tlv(0x30, concat(*parts))

        fun set(vararg parts: ByteArray): ByteArray = tlv(0x31, concat(*parts))

        fun integer(value: BigInteger): ByteArray = tlv(0x02, value.toByteArray())

        fun bitString(value: ByteArray): ByteArray = tlv(0x03, concat(byteArrayOf(0), value))

        fun utcTime(date: Date): ByteArray {
            val format = SimpleDateFormat("yyMMddHHmmss'Z'", Locale.ROOT)
                .apply { timeZone = TimeZone.getTimeZone("UTC") }
            return tlv(0x17, format.format(date).toByteArray(StandardCharsets.US_ASCII))
        }
    }
}
