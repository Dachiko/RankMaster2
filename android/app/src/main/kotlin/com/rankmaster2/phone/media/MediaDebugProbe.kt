package com.rankmaster2.phone.media

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue

/**
 * A window onto the [MediaPane] a screen hosts, for that screen's debug overlay.
 *
 * The pane writes what it is showing into this and the overlay reads it; the pane never reads it
 * back, so passing one (or none) changes nothing about what is drawn. The screen owns it, because
 * the overlay is a sibling of the pane and has no other way to reach into it.
 */
class MediaDebugProbe {
    /** The video being shown, or null for a still / nothing. */
    var video by mutableStateOf<VideoDiagnostics?>(null)
        internal set

    /** The URL the still pane is loading, when the current item is a still. */
    var stillUrl by mutableStateOf<String?>(null)
        internal set

    /** Coil's outcome for [stillUrl], as the pane saw it. */
    var stillState by mutableStateOf<String?>(null)
        internal set

    internal fun showingVideo(diagnostics: VideoDiagnostics?) {
        video = diagnostics
        stillUrl = null
        stillState = null
    }

    internal fun showingStill(url: String?, state: String?) {
        video = null
        stillUrl = url
        stillState = state
    }

    internal fun showingNothing() {
        video = null
        stillUrl = null
        stillState = null
    }
}
