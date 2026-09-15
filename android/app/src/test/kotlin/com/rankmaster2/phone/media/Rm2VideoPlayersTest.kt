package com.rankmaster2.phone.media

import android.content.Context
import androidx.media3.common.PlaybackException
import okhttp3.OkHttpClient
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config

/**
 * The video side, as far as a JVM can see it.
 *
 * What is provable here is the URL and the failure vocabulary. Whether an AV1/MP4, a VP9/WebM or an
 * MPEG-4-in-AVI actually decodes is a question about a phone's codecs and cannot be answered on a
 * build machine at all - which is why a codec refusal is mapped to a state the pane can say out
 * loud rather than swallowed as a generic failure.
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [33])
class Rm2VideoPlayersTest {

    private val context: Context get() = RuntimeEnvironment.getApplication()

    private fun players() =
        Rm2VideoPlayers(context, OkHttpClient(), "https://192.168.1.42:18611/api/v1")

    @Test
    fun `the video link is used verbatim, encoding and all`() {
        val ref = MediaFixtures.video("my clip+1.mp4", encoded = "my%20clip%2B1.mp4")

        assertEquals(
            "https://192.168.1.42:18611/api/v1/media/my%20clip%2B1.mp4/video?v=1122334455667788",
            players().videoUrl(ref),
        )
    }

    @Test
    fun `a still has no video to play, and a vanished file has nothing at all`() {
        assertNull(players().videoUrl(MediaFixtures.still("a.jpg")))
        assertNull(players().videoUrl(MediaFixtures.gone("b.jpg")))
        assertNull(players().create(MediaFixtures.still("a.jpg")))
    }

    @Test
    fun `a codec this phone will not take is reported as undecodable, not as a network failure`() {
        // This is the .avi / exotic-codec case. It has to reach the pane as "this file will not
        // open" so that the owner - and whoever decides about libVLC - can see it happening.
        val unsupported = listOf(
            PlaybackException.ERROR_CODE_DECODING_FORMAT_UNSUPPORTED,
            PlaybackException.ERROR_CODE_DECODER_INIT_FAILED,
            PlaybackException.ERROR_CODE_DECODING_FAILED,
            PlaybackException.ERROR_CODE_PARSING_CONTAINER_UNSUPPORTED,
            PlaybackException.ERROR_CODE_PARSING_CONTAINER_MALFORMED,
        )
        for (code in unsupported) {
            val error = PlaybackException("no", null, code)
            assertEquals(
                PlaybackException.getErrorCodeName(code),
                MediaPaneState.Undecodable,
                Rm2VideoPlayers.stateOf(error),
            )
        }
    }

    @Test
    fun `a network failure is not a reason to discard a video`() {
        val error = PlaybackException("no", null, PlaybackException.ERROR_CODE_IO_NETWORK_CONNECTION_FAILED)
        assertTrue(Rm2VideoPlayers.stateOf(error) is MediaPaneState.Unavailable)
    }
}
