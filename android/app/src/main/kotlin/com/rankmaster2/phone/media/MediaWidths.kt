package com.rankmaster2.phone.media

/**
 * Which `w=` to ask for.
 *
 * The server accepts six widths and refuses everything else with `400 unsupported_width` - even a
 * smaller one that would be perfectly serviceable (SERVER_SPEC.md § 12.3). That is not fussiness:
 * the set is what keeps the server's on-disk cache of re-encoded stills bounded. So the client
 * never computes a width; it picks one from this list.
 *
 * Two things about `w` that are easy to get wrong, and that the tests pin down:
 *
 *  1. **`w` bounds the long edge, not the width.** `StillRenderer.FitLongEdge` fits
 *     `max(width, height)` to the requested value. A pane therefore has to ask by its *own* long
 *     edge: an image drawn to fit inside a pane can have a displayed long edge as large as the
 *     pane's long edge, whichever way round the picture is. Asking by pane width alone leaves a
 *     landscape photo in a tall pane soft.
 *  2. **The answer may be smaller than you asked for.** The server never upscales (§ 12.3), so a
 *     900 px original asked for at 1440 comes back 900 px wide and is still keyed by `s1440*`.
 *     Nothing may be laid out from the requested width - only from the decoded image.
 */
object MediaWidths {

    /** § 12.3, in ascending order. The server's own `StillWidths.Allowed`. */
    val ALLOWED = listOf(360, 540, 720, 1080, 1440, 2160)

    /** What the server uses when `w` is omitted. */
    const val DEFAULT = 1080

    /** `thumb` is fixed here and ignores `w` entirely. This layer does not use `thumb`. */
    const val THUMB = 320

    private val LARGEST = ALLOWED.last()

    /**
     * The smallest allowed width at or above [panePx], capped at 2160.
     *
     * Below 360 the answer is 360, because there is nothing smaller to ask for. Above 2160 the
     * answer is 2160 and the picture is drawn slightly soft on a 4K panel - which is the trade the
     * bounded width set buys, and is invisible on a phone.
     */
    fun forPane(panePx: Int): Int = ALLOWED.firstOrNull { it >= panePx } ?: LARGEST

    /**
     * The width for a pane [widthPx] x [heightPx], by its long edge - see the note on `w` above.
     */
    fun forPane(widthPx: Int, heightPx: Int): Int = forPane(maxOf(widthPx, heightPx))

    fun isAllowed(w: Int): Boolean = w in ALLOWED
}
