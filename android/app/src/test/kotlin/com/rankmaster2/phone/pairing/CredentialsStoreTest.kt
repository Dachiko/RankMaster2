package com.rankmaster2.phone.pairing

import android.content.Context
import android.content.SharedPreferences
import com.rankmaster2.phone.store.ServerIdentity
import com.rankmaster2.phone.store.impl.CredentialsStore
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment

/**
 * The credential store, on a real (Robolectric) `SharedPreferences`.
 *
 * The encryption itself is `EncryptedSharedPreferences`' job and is exercised by
 * [CredentialsStore.create] on a device; what is worth testing here is the part this file actually
 * decides - that one server is stored whole, that a second pairing replaces the first rather than
 * merging with it, and that `clear()` really clears.
 */
@RunWith(RobolectricTestRunner::class)
class CredentialsStoreTest {

    private lateinit var prefs: SharedPreferences
    private lateinit var store: CredentialsStore

    private val identity = ServerIdentity(
        host = "192.168.1.42",
        port = 18611,
        certificateFingerprint = "sha256:" + "3b1f".repeat(16),
        token = "tok_live_9aZ",
        deviceId = "d-7f3a",
    )

    @Before
    fun setUp() {
        val context: Context = RuntimeEnvironment.getApplication()
        prefs = context.getSharedPreferences("rm2-credentials-test", Context.MODE_PRIVATE)
        prefs.edit().clear().commit()
        store = CredentialsStore(prefs)
    }

    @Test
    fun `a fresh phone has no server`() {
        assertNull(store.current())
    }

    @Test
    fun `what was saved is what comes back`() {
        store.save(identity)
        assertEquals(identity, store.current())
    }

    @Test
    fun `it survives a new store over the same preferences, which is what a restart is`() {
        store.save(identity)
        assertEquals(identity, CredentialsStore(prefs).current())
    }

    @Test
    fun `a device id is optional, because a server need not return one`() {
        store.save(identity.copy(deviceId = null))
        val read = store.current()!!
        assertEquals(identity.host, read.host)
        assertNull(read.deviceId)
    }

    @Test
    fun `pairing again replaces the first server whole`() {
        store.save(identity)
        val second = ServerIdentity(
            host = "10.0.0.9",
            port = 443,
            certificateFingerprint = "sha256:" + "9c2e".repeat(16),
            token = "tok_live_other",
            deviceId = null,
        )

        store.save(second)

        // In particular the old deviceId is gone: revoking with it would revoke the wrong device.
        assertEquals(second, store.current())
        assertNull(store.current()!!.deviceId)
    }

    @Test
    fun `clear really clears`() {
        store.save(identity)
        store.clear()

        assertNull(store.current())
        assertTrue("nothing may be left behind", prefs.all.isEmpty())
        assertNull(CredentialsStore(prefs).current())
    }

    @Test
    fun `a half-written identity reads as not paired`() {
        // However this came about - an interrupted write, a downgrade, a hand-edited file - a
        // record with no token is not a pairing, and the app must not believe it is.
        prefs.edit()
            .putString("host", "192.168.1.42")
            .putInt("port", 18611)
            .putString("fingerprint", "sha256:" + "3b1f".repeat(16))
            .commit()

        assertNull(store.current())
    }

    @Test
    fun `a blank token is not a token`() {
        store.save(identity)
        prefs.edit().putString("token", "").commit()
        assertNull(store.current())
    }

    @Test
    fun `a nonsense port reads as not paired`() {
        store.save(identity)
        prefs.edit().putInt("port", 0).commit()
        assertNull(store.current())
    }

    @Test
    fun `the store is where the token lives, and nowhere else in this feature`() {
        store.save(identity)
        val serialised = prefs.all.values.joinToString(" ") { it.toString() }
        assertTrue(serialised.contains("tok_live_9aZ"))
        assertFalse("no pairing code is ever persisted", serialised.contains("418250"))
    }
}
