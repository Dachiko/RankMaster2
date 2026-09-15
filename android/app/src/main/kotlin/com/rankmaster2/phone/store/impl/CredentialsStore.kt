package com.rankmaster2.phone.store.impl

import android.content.Context
import android.content.SharedPreferences
import androidx.security.crypto.EncryptedSharedPreferences
import androidx.security.crypto.MasterKey
import com.rankmaster2.phone.store.Credentials
import com.rankmaster2.phone.store.ServerIdentity

/**
 * The one paired server, on disk, encrypted.
 *
 * Backed by `EncryptedSharedPreferences` with a Keystore-held master key, so the token and the
 * certificate fingerprint are not readable out of the app's data directory by a rooted shell or an
 * `adb backup`. (`android:allowBackup="false"` is already set in the manifest; this is the second
 * lock, not the first.)
 *
 * **One server, stored whole.** There is no partial state here. Either all five fields are present
 * and this phone is paired, or [current] is null and it is not - a half-written identity would be
 * an app that thinks it is paired and then cannot pin, which is exactly the state that tempts a
 * client into talking to an unverified peer.
 *
 * Writes use `commit()`, not `apply()`. The one moment this file is written is the moment the user
 * is told "paired", and a token that was only queued for writing is a token that is gone if the
 * process dies in between - and pairing codes are single-use, so it cannot simply be redone.
 */
class CredentialsStore internal constructor(
    private val prefs: SharedPreferences,
) : Credentials {

    override fun current(): ServerIdentity? {
        val host = prefs.getString(KEY_HOST, null)?.takeIf { it.isNotBlank() } ?: return null
        val port = prefs.getInt(KEY_PORT, 0).takeIf { it in 1..65535 } ?: return null
        val fingerprint = prefs.getString(KEY_FINGERPRINT, null)?.takeIf { it.isNotBlank() }
            ?: return null
        val token = prefs.getString(KEY_TOKEN, null)?.takeIf { it.isNotBlank() } ?: return null
        return ServerIdentity(
            host = host,
            port = port,
            certificateFingerprint = fingerprint,
            token = token,
            deviceId = prefs.getString(KEY_DEVICE_ID, null)?.takeIf { it.isNotBlank() },
        )
    }

    override fun save(identity: ServerIdentity) {
        val editor = prefs.edit()
        // Cleared first: "one paired server" means a second pairing replaces the first whole, never
        // merges with the leftovers of it (a stale deviceId revoking the wrong device).
        editor.clear()
        editor.putString(KEY_HOST, identity.host)
        editor.putInt(KEY_PORT, identity.port)
        editor.putString(KEY_FINGERPRINT, identity.certificateFingerprint)
        editor.putString(KEY_TOKEN, identity.token)
        identity.deviceId?.let { editor.putString(KEY_DEVICE_ID, it) }
        editor.commit()
    }

    override fun clear() {
        prefs.edit().clear().commit()
    }

    companion object {
        internal const val FILE_NAME = "rm2-credentials"

        private const val KEY_HOST = "host"
        private const val KEY_PORT = "port"
        private const val KEY_FINGERPRINT = "fingerprint"
        private const val KEY_TOKEN = "token"
        private const val KEY_DEVICE_ID = "deviceId"

        /** The real one. Keystore-backed; the master key never leaves the secure hardware. */
        fun create(context: Context): CredentialsStore =
            CredentialsStore(encryptedPreferences(context.applicationContext))

        private fun encryptedPreferences(context: Context): SharedPreferences {
            val masterKey = MasterKey.Builder(context)
                .setKeyScheme(MasterKey.KeyScheme.AES256_GCM)
                .build()
            return EncryptedSharedPreferences.create(
                context,
                FILE_NAME,
                masterKey,
                EncryptedSharedPreferences.PrefKeyEncryptionScheme.AES256_SIV,
                EncryptedSharedPreferences.PrefValueEncryptionScheme.AES256_GCM,
            )
        }
    }
}
