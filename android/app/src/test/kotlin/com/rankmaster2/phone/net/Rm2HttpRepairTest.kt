package com.rankmaster2.phone.net

import okhttp3.Request
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

/**
 * § 3.7 (the second audit), end to end: re-pairing must reach the PC with the *new* token, through
 * the *one* client `Rm2Http` keeps for that certificate - not a second client built alongside the
 * first, which is what used to open a second disk cache on the directory the first one still had
 * open. `Rm2HttpCacheTest` proves the client and its `Cache` are reused; this proves the reused
 * client actually carries the token re-pairing gave it, against a real server.
 */
class Rm2HttpRepairTest {

    private lateinit var rm2: Rm2TestServer

    @Before fun setUp() { rm2 = Rm2TestServer(token = "token-before-repair") }

    @After fun tearDown() { rm2.close() }

    @Test
    fun `after re-pairing, the shared client sends the new token on the wire`() {
        rm2.enqueue(200, PING_BODY)
        rm2.enqueue(200, PING_BODY)

        // The ordinary session, before he re-pairs.
        val before = Rm2Http.client(rm2.identity)
        before.newCall(Request.Builder().url(rm2.identity.baseUrl + "/ping").get().build()).execute().close()
        val firstAuth = rm2.take().getHeader("Authorization")

        // Re-pairing: same PC, same certificate, a token this client was never built with.
        val repaired = rm2.identity.copy(token = "token-after-repair")
        val after = Rm2Http.client(repaired)
        after.newCall(Request.Builder().url(repaired.baseUrl + "/ping").get().build()).execute().close()
        val secondAuth = rm2.take().getHeader("Authorization")

        assertTrue("re-pairing must reuse the one client for this certificate", before === after)
        assertEquals("Bearer token-before-repair", firstAuth)
        assertEquals(
            "the reused client must send the token re-pairing gave it, not the one it started with",
            "Bearer token-after-repair",
            secondAuth,
        )
    }
}
