package com.rankmaster2.phone.net

import com.rankmaster2.phone.store.ServerIdentity
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

/**
 * That the app actually has the disk cache everything else is written as though it had.
 *
 * `Rm2Http.configure` existed, was documented as "call once at startup", and was called from
 * nowhere at all. Every photograph therefore crossed the network every single time it came round -
 * and the warm-pair prefetch, whose entire job is to put bytes in this cache before a pane asks for
 * them, was reading files to the end and dropping them on the floor. Nothing failed; it was just
 * all slower than it looks in the code, and the acceptance gate's "instant on second sight" could
 * not have passed.
 *
 * SERVER_SPEC.md section 12.5 is what makes the cache safe to have: byte responses carry a strong
 * ETag and `immutable` and `links` carry `v=`, while every JSON endpoint is `no-store` at the
 * server, so nothing that has to be fresh can go stale in here.
 */
class Rm2HttpCacheTest {

    @get:Rule val folder = TemporaryFolder()

    private fun identity(token: String) = ServerIdentity(
        host = "192.168.1.42",
        port = 18611,
        certificateFingerprint = "sha256:" + "ab".repeat(32),
        token = token,
    )

    @Test
    fun `a configured client caches bytes on disk`() {
        Rm2Http.configure(folder.newFolder("configured"))

        val cache = Rm2Http.client(identity("token-a")).cache

        assertNotNull("without this the media layer's whole caching story is untrue", cache)
        assertEquals(512L * 1024 * 1024, cache!!.maxSize())
    }

    @Test
    fun `the cache lands under the directory it was given, not in it`() {
        val root = folder.newFolder("app-cache")
        Rm2Http.configure(root)

        assertEquals(java.io.File(root, "rm2-media"), Rm2Http.cacheDirectory())
    }

    @Test
    fun `clients built before configuration are not kept`() {
        // The failure this guards against is subtle: configure late and the cacheless client built
        // first stays in the map for the life of the process, so the cache exists and nothing uses
        // it. Re-configuring drops them instead.
        val early = Rm2Http.client(identity("token-b"))
        Rm2Http.configure(folder.newFolder("later"))
        val late = Rm2Http.client(identity("token-b"))

        assert(early !== late) { "a client built before configure() must not survive it" }
        assertNotNull(late.cache)
    }
}
