package com.rankmaster2.phone.ui.rank

import androidx.compose.animation.core.CubicBezierEasing
import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.material3.ripple
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.TransformOrigin
import androidx.compose.ui.graphics.drawscope.DrawScope
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.IntRect
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.util.lerp
import androidx.compose.ui.window.Popup
import androidx.compose.ui.window.PopupPositionProvider
import androidx.compose.ui.window.PopupProperties
import kotlinx.coroutines.delay
import kotlin.math.PI
import kotlin.math.cos
import kotlin.math.pow
import kotlin.math.sin

/**
 * One photograph's own actions, opened by a long press and anchored to the thumb that opened it.
 *
 * ## What this has to survive
 *
 * It is drawn **on a photograph** - any photograph, a white sky or a black shadow - on a screen
 * that is otherwise entirely picture. Everything else floating over the pair (the dots, the notch,
 * the progress hairline) solves that with a faint mark over a dark halo, because those are marks to
 * be glanced at. This is different: it is read, aimed at, and two of its three lines move a file on
 * the owner's PC. It has to be unambiguous, not faint.
 *
 * So the menu brings its own ground rather than borrowing the photograph's:
 *
 * - **A pool of dark around the thumb.** A radial scrim centred on the press, at its strongest
 *   under the menu and gone before the far edges of the screen. It is not a full-screen dim: the
 *   *other* photograph stays visible, which matters because the whole reason this menu never has to
 *   ask "which one?" is that the owner can see the answer.
 * - **A near-opaque panel** on top of that, with a hairline edge - so what carries the text is the
 *   panel, and the scrim only has to say "the screen is busy", not "make this legible".
 *
 * ## Why not a blur
 *
 * `RenderEffect` blur is the obvious modern answer and it is the wrong one here. It is API 31+ and
 * this app's floor is 29, so it is two designs to keep in step, one of which nobody would ever look
 * at. It also costs a readback of whatever is behind it every frame it changes - over a *playing
 * video*, on a mid-range phone, during the 170 ms it is scaling up. A radial gradient is one quad,
 * no readback, identical on every API this app runs on, and it buys the same thing: depth, and a
 * photograph that stops competing with the words on top of it.
 *
 * ## The one mistake that costs something
 *
 * A tap on the pair votes, and a vote is the only expensive mistake on this screen. Every surface
 * this composable puts up - the scrim included - eats the pointer events it receives, so there is
 * no path from a tap made while the menu is open to a vote.
 */
@Composable
internal fun PaneMenu(
    side: Side,
    at: Offset,
    windowSize: IntSize,
    state: RankState,
    onDismiss: () -> Unit,
    onView: () -> Unit,
    onDiscard: () -> Unit,
    onSpecial: () -> Unit,
) {
    val ref = if (side == Side.LEFT) state.left else state.right
    val press = remember(at) { IntOffset(at.x.toInt(), at.y.toInt()) }

    // Open, then closing, then gone. `shown` drives every animation; `closing` is what defers the
    // real dismissal until the exit has been seen. A dismissal the owner asked for is still a
    // dismissal 110 ms later, and during those 110 ms the popup is still the thing under his
    // finger - so a second tap lands here and not on a photograph.
    var shown by remember(side, press) { mutableStateOf(false) }
    var closing by remember(side, press) { mutableStateOf(false) }
    val dismiss: () -> Unit = { if (!closing) { closing = true; shown = false } }

    val haptics = LocalHapticFeedback.current
    LaunchedEffect(side, press) {
        // The long press has no feedback of its own in Compose: without this the menu simply
        // appears, and the owner cannot tell a long press that worked from one that is still
        // counting.
        haptics.performHapticFeedback(HapticFeedbackType.LongPress)
        shown = true
    }
    LaunchedEffect(closing) {
        if (closing) {
            delay(ExitMillis.toLong())
            onDismiss()
        }
    }

    val target = if (shown) 1f else 0f
    val exiting = !shown

    // Three curves out of one boolean, because they are three different jobs. The ground arrives
    // first and fastest so there is something to read *on*; the panel grows out of the press point
    // over the full duration; the words come last, by a hair, so they are not smeared across the
    // scale-up.
    val groundAlpha by animateFloatAsState(
        targetValue = target,
        animationSpec = tween(if (exiting) ExitMillis else GroundMillis, easing = LinearEasing),
        label = "pane-menu-ground",
    )
    val grow by animateFloatAsState(
        targetValue = target,
        animationSpec = tween(
            durationMillis = if (exiting) ExitMillis else OpenMillis,
            easing = if (exiting) LinearEasing else OpenEasing,
        ),
        label = "pane-menu-grow",
    )
    val contentAlpha by animateFloatAsState(
        targetValue = target,
        animationSpec = tween(
            durationMillis = if (exiting) ExitMillis else ContentMillis,
            delayMillis = if (exiting) 0 else ContentDelayMillis,
            easing = LinearEasing,
        ),
        label = "pane-menu-content",
    )

    // Growing from 0.86 and shrinking back to 0.96: opening is a thing arriving from the press
    // point, closing is a thing letting go. A menu that collapsed all the way back into the thumb
    // would be the opening animation run backwards, which reads as an undo rather than a dismissal.
    val scale = lerp(if (exiting) ExitScale else OpenScale, 1f, grow)

    val scrimRadius = remember(windowSize) { scrimRadiusPx(windowSize) }
    val scrim = remember(at, scrimRadius) { radialScrim(at, scrimRadius) }

    Box(
        Modifier
            .fillMaxSize()
            // Belt and braces. The focusable popup already swallows touches outside itself, so in
            // practice this never fires - but "in practice" is not the standard for the one gesture
            // on this screen that cannot be taken back for free.
            .pointerInput(side, press) { detectTapGestures { dismiss() } }
            .drawBehind { drawRect(brush = scrim, alpha = groundAlpha) }
    )

    Popup(
        popupPositionProvider = remember(press) { ThumbPositionProvider(press) },
        onDismissRequest = dismiss,
        properties = PopupProperties(focusable = true),
    ) {
        // The popup's content is the panel *plus* its margin, and the margin is why the panel never
        // ends up welded to the edge of the glass. `ThumbPositionProvider` clamps whatever it is
        // given into the window, hard against the edge - which is right, and which would put a
        // floating panel flush against the side of the screen on almost every press, because the
        // panel is most of the width of a phone. Padding the thing that gets clamped is how the
        // margin survives the clamp without the clamp having to know about it.
        val boxSize = with(LocalDensity.current) {
            IntSize(MenuBoxWidth.roundToPx(), MenuBoxHeight.roundToPx())
        }
        val origin = remember(press, windowSize, boxSize) {
            menuTransformOrigin(press, windowSize, boxSize)
        }

        Box(
            Modifier
                .width(MenuBoxWidth)
                // The margin is not dead: it is the nearest place to the menu that closes it.
                .pointerInput(side, press) { detectTapGestures { dismiss() } }
                // The whole box scales, margin included, because the origin above is a fraction of
                // the box. Putting the layer inside the padding would move the point it grows from.
                .graphicsLayer {
                    scaleX = scale
                    scaleY = scale
                    alpha = groundAlpha
                    transformOrigin = origin
                }
                .padding(EdgeGap)
        ) {
            Column(
                Modifier
                    .fillMaxWidth()
                    .shadow(MenuShadow, MenuShape, clip = false)
                    .background(PanelFill, MenuShape)
                    .border(HairlineWidth, PanelEdge, MenuShape)
                    // Anything that reaches the panel and was not a row stops here.
                    .pointerInput(Unit) { detectTapGestures { } }
                    .padding(PanelPad)
                    .graphicsLayer { alpha = contentAlpha }
            ) {
                FileName(ref?.id.orEmpty())

                MenuAction(
                    label = "View full screen",
                    glyph = MenuGlyph.VIEW,
                    accent = Ink,
                    // Always. It reads a file and moves nothing, so there is nothing for it to
                    // collide with - and it is the line that gets used most.
                    enabled = true,
                    onClick = onView,
                )

                // Below this line, a file moves on the PC. That is the whole hierarchy: one
                // hairline, and two rows that answer "where does it go?" in the same monospace the
                // file name is written in. Not red, not shouting - the two rows that talk in paths
                // are the two rows that touch the disk.
                Hairline()

                MenuAction(
                    label = "Discard",
                    detail = "discarded/",
                    glyph = MenuGlyph.DISCARD,
                    accent = Amber,
                    enabled = state.actionable,
                    onClick = onDiscard,
                )
                MenuAction(
                    label = "Move to special",
                    detail = "special 1/",
                    glyph = MenuGlyph.SPECIAL,
                    accent = Emerald,
                    enabled = state.actionable,
                    onClick = onSpecial,
                )
            }
        }
    }
}

/**
 * The file this menu is about. Context, not an action: it is never tappable and it never grows.
 *
 * Monospace, because it is an identifier rather than a sentence, and the same face the two rows
 * that move it use for their destinations. The extension is dropped a shade below the stem: it is
 * the least interesting eight characters on the screen and it is the part that is never truncated.
 */
@Composable
private fun FileName(id: String) {
    val (shown, extension) = remember(id) { fileNameParts(id) }

    Row(
        modifier = Modifier.fillMaxWidth().height(HeaderHeight).padding(horizontal = RowPad),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(
            text = shown,
            color = InkMuted,
            fontSize = FileNameSize,
            fontFamily = FontFamily.Monospace,
            letterSpacing = 0.sp,
            maxLines = 1,
            softWrap = false,
            overflow = TextOverflow.Clip,
        )
        if (extension.isNotEmpty()) {
            Text(
                text = extension,
                color = InkFaint,
                fontSize = FileNameSize,
                fontFamily = FontFamily.Monospace,
                letterSpacing = 0.sp,
                maxLines = 1,
                softWrap = false,
                overflow = TextOverflow.Clip,
            )
        }
    }
}

/**
 * One action: a drawn glyph, a label, and - for the two that move a file - where it goes.
 *
 * ## Disabled without flashing
 *
 * `actionable` goes false for the length of a round trip to the PC, which on a LAN is a fraction of
 * a second. Painting that grey the instant it happens is a blink, and a blink reads as a fault. So
 * the row stops *accepting* the tap immediately and only starts *looking* disabled if the state
 * lasts past [DisabledSettleMillis] - and then it fades there rather than switching. A menu opened
 * while an action is already in flight is dimmed from its first frame instead, because in that case
 * there is nothing transient about it and pretending otherwise would be the real lie.
 */
@Composable
private fun MenuAction(
    label: String,
    glyph: MenuGlyph,
    accent: Color,
    enabled: Boolean,
    onClick: () -> Unit,
    detail: String? = null,
) {
    var settled by remember { mutableStateOf(!enabled) }
    LaunchedEffect(enabled) {
        if (enabled) {
            settled = false
        } else {
            delay(DisabledSettleMillis)
            settled = true
        }
    }
    val dimmed = looksDisabled(enabled, settled)
    val fade by animateFloatAsState(
        targetValue = if (dimmed) DisabledAlpha else 1f,
        animationSpec = tween(DisabledFadeMillis, easing = LinearEasing),
        label = "pane-menu-row",
    )

    Row(
        modifier = Modifier
            .fillMaxWidth()
            .heightIn(min = if (detail == null) RowHeight else RowHeightWithDetail)
            .clip(RowShape)
            .clickable(
                interactionSource = remember { MutableInteractionSource() },
                indication = ripple(color = accent),
                enabled = enabled,
                onClick = onClick,
            )
            .padding(horizontal = RowPad)
            .graphicsLayer { alpha = fade },
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(
            Modifier
                .size(IconBox)
                .drawBehind { drawGlyph(glyph, accent, IconStroke.toPx()) }
        )
        Spacer(Modifier.width(IconGap))
        Column {
            Text(
                text = label,
                color = Ink,
                fontSize = LabelSize,
                lineHeight = LabelLineHeight,
                fontWeight = FontWeight.Medium,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            if (detail != null) {
                Text(
                    text = detail,
                    color = InkFaint,
                    fontSize = DetailSize,
                    lineHeight = DetailLineHeight,
                    fontFamily = FontFamily.Monospace,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }
        }
    }
}

/** The one line in the menu. Everything above it reads a file; everything below it moves one. */
@Composable
private fun Hairline() {
    Box(
        Modifier
            .padding(horizontal = RowPad, vertical = HairlineGap)
            .fillMaxWidth()
            .height(HairlineWidth)
            .background(PanelEdge)
    )
}

// -- the numbers ---------------------------------------------------------------------------------
//
// Every visual decision in this file is one of these, named, in one place. Spacing is a scale of
// 4 dp and not a set of paddings that happen to look alike.

/** Wide enough for a file name in monospace, narrow enough to leave the photograph around it. */
internal val MenuWidth = 264.dp

/** A floating surface, so a rounded rectangle - unlike the notch, which the screen edge grows. */
private val MenuShape = RoundedCornerShape(22.dp)

private val RowShape = RoundedCornerShape(14.dp)

/** Inside the panel, so the rows' press highlight is an inset shape and not a full-bleed bar. */
private val PanelPad = 6.dp

/** From the padded panel's edge to the icon column: 6 + 14 = 20 dp from the glass. */
private val RowPad = 14.dp

private val HeaderHeight = 34.dp
internal val RowHeight = 52.dp
internal val RowHeightWithDetail = 60.dp
private val HairlineWidth = 1.dp
private val HairlineGap = 5.dp
private val IconBox = 22.dp
private val IconGap = 14.dp
private val IconStroke = 1.6.dp
private val MenuShadow = 28.dp

/**
 * What the menu measures, by construction: the panel is a stack of fixed heights.
 *
 * It is written down because the opening animation needs the size *before* anything is measured -
 * see [menuTransformOrigin]. At a very large system font scale a row grows past its minimum and
 * this becomes an underestimate; the cost of that is a few dp of error in where the scale-up
 * appears to start, which is the right thing to be wrong about. Clipping the label would not be.
 */
internal val MenuHeight: Dp =
    PanelPad * 2 + HeaderHeight + RowHeight + (HairlineWidth + HairlineGap * 2) +
        RowHeightWithDetail * 2

/**
 * How close the panel may come to the edge of the glass, and to the thumb that opened it.
 *
 * Carried as padding *inside* the popup so that the positioning arithmetic, which clamps what it is
 * given hard against the window, clamps the margin rather than the panel.
 */
private val EdgeGap = 10.dp

/** What actually gets positioned, and therefore what has to fit on the screen. */
internal val MenuBoxWidth: Dp = MenuWidth + EdgeGap * 2
internal val MenuBoxHeight: Dp = MenuHeight + EdgeGap * 2

/**
 * How much width the file name actually has: the panel, less the padding that puts every row's
 * first mark 20 dp from the glass.
 */
internal val FileNameFieldWidth: Dp = MenuWidth - (PanelPad + RowPad) * 2

private val PanelFill = Color(0xF00B0D11)

/** The hairline round the panel and the one inside it: the same mark, doing the same job. */
private val PanelEdge = Color(0x1FFFFFFF)

private val Ink = Color(0xFFF3F5F8)
private val InkMuted = Color(0xFF99A1AF)
private val InkFaint = Color(0xFF6D7583)

/** The screen's own two hues (see [MatchStrip]), at full strength because these are read, not glanced at. */
private val Amber = Color(0xFFF0A020)
private val Emerald = Color(0xFF2ECC71)

private val LabelSize = 16.sp
private val LabelLineHeight = 20.sp
private val DetailSize = 12.sp
private val DetailLineHeight = 15.sp
internal val FileNameSize = 12.sp

/**
 * How many characters of the stem survive.
 *
 * Monospace at 12 sp advances about 0.6 em, so 30 characters is ~216 dp against the 224 dp the
 * header has to give. `PaneMenuDesignTest` asserts that rather than trusting this comment.
 */
internal const val FileNameMaxChars = 30

/** A monospace advance as a fraction of its size. An estimate, and the test that uses it says so. */
internal const val MonoAdvanceRatio = 0.60f

// -- motion --------------------------------------------------------------------------------------

/**
 * 170 ms, in the same family as the notch's 180 ms snap and for the same reason: long enough to see
 * where it came from, short enough that nobody ever waits for it.
 */
private const val OpenMillis = 170

/** The ground beats the panel so the words never land on a photograph. */
private const val GroundMillis = 120

private const val ContentMillis = 110
private const val ContentDelayMillis = 60

/** Leaving is not arriving backwards: shorter, linear, no ceremony. */
private const val ExitMillis = 110

private const val OpenScale = 0.86f
private const val ExitScale = 0.96f

/** Fast away from the press, then a long settle. The overshoot-free version of a spring. */
private val OpenEasing = CubicBezierEasing(0.16f, 1f, 0.3f, 1f)

/** How long `actionable` has to stay false before the rows admit it. */
internal const val DisabledSettleMillis = 240L

private const val DisabledFadeMillis = 140

/**
 * Dimmed, not greyed. The row keeps its colour and loses its weight, so a disabled line looks like
 * the same line waiting rather than a different line that is broken.
 */
private const val DisabledAlpha = 0.38f

/**
 * Whether a row should *look* disabled: only when it is disabled and has been for long enough to be
 * worth saying so.
 *
 * Split out from the composable because it is the rule, and the rule is the part worth a test.
 */
internal fun looksDisabled(enabled: Boolean, settled: Boolean): Boolean = !enabled && settled

// -- the ground ----------------------------------------------------------------------------------

/**
 * How dark the scrim is directly under the thumb.
 *
 * Not 1.0: at full black the pressed photograph disappears and the menu becomes a dialogue floating
 * in a void, which is a different screen. At 0.78 the picture is still there, underneath, dimmed.
 */
internal const val ScrimPeak = 0.78f

/**
 * How far the pool of dark reaches, as a fraction of the screen's long edge.
 *
 * Sized so that on a portrait phone the *other* photograph is only lightly touched: the menu never
 * has to say which picture it is about, because the press point is the answer and the scrim is what
 * points at it.
 */
private const val ScrimRadiusFraction = 0.85f

/**
 * The falloff, from 1 at the press to 0 at the edge of the pool.
 *
 * Slightly flatter than linear near the middle (exponent 1.25) so the menu sits in an even field
 * rather than on a bright spot with a dark ring around it, and still reaches zero rather than
 * ending on a visible circle.
 */
internal fun scrimFalloff(t: Float): Float {
    val clamped = t.coerceIn(0f, 1f)
    return (1f - clamped).toDouble().pow(1.25).toFloat()
}

/** Where the gradient is sampled. Six stops is enough for [scrimFalloff] to be what is drawn. */
internal val ScrimStops = listOf(0f, 0.2f, 0.4f, 0.6f, 0.8f, 1f)

internal fun scrimRadiusPx(windowSize: IntSize): Float =
    maxOf(windowSize.width, windowSize.height) * ScrimRadiusFraction

private fun radialScrim(centre: Offset, radius: Float): Brush = Brush.radialGradient(
    colorStops = ScrimStops
        .map { stop -> stop to Color.Black.copy(alpha = ScrimPeak * scrimFalloff(stop)) }
        .toTypedArray(),
    center = centre,
    radius = radius.coerceAtLeast(1f),
)

// -- the file name -------------------------------------------------------------------------------

/**
 * A media id split into the part worth reading and the part that is not.
 *
 * The id is a bare file name (`SERVER_SPEC.md` § 9.1) but ids that are not are exactly what the
 * server's `media_outside_session` exists to refuse, so anything folder-shaped is dropped here
 * rather than drawn: a menu is not the place to find out.
 */
internal fun splitFileName(id: String): Pair<String, String> {
    val name = id.substringAfterLast('/').substringAfterLast('\\')
    val dot = name.lastIndexOf('.')
    return if (dot <= 0 || dot == name.length - 1) name to "" else {
        name.substring(0, dot) to name.substring(dot)
    }
}

/**
 * The file name as it is drawn: a stem that has been made to fit, and the extension it keeps.
 *
 * The extension is never truncated and never counted out of the budget - it is the stem that gives
 * way, because five characters of `.jpeg` are worth less than five characters of the name and
 * because a name that has lost its extension looks like a different kind of file.
 */
internal fun fileNameParts(id: String, max: Int = FileNameMaxChars): Pair<String, String> {
    val (stem, extension) = splitFileName(id)
    return middleTruncate(stem, max - extension.length) to extension
}

/**
 * A long name shortened in the **middle**, because the ends are where the information is: a camera
 * writes `DSC_0123`, a phone writes `IMG_20240513_184212`, and in both the run of digits that tells
 * two files apart is at the end. Cutting the tail off is what makes every file in a folder look
 * like the same file.
 */
internal fun middleTruncate(text: String, max: Int): String {
    if (max <= 1) return if (text.length <= max) text else "…"
    if (text.length <= max) return text
    val keep = max - 1
    val head = (keep + 1) / 2
    val tail = keep - head
    return text.take(head) + "…" + text.takeLast(tail)
}

// -- where it opens, and where it grows from -----------------------------------------------------

/**
 * Opens a popup at the point that was pressed, and never off the glass.
 *
 * Compose's own alignment-plus-offset provider does not clamp: asked for a position near the bottom
 * or the right of the window it hands back exactly that, and the part of the menu past the edge is
 * simply not on the screen. Every candidate here is clamped into the window instead, and when there
 * is no room below the press the menu opens above it - the way a context menu on any other Android
 * surface behaves.
 */
internal class ThumbPositionProvider(private val at: IntOffset) : PopupPositionProvider {

    override fun calculatePosition(
        anchorBounds: IntRect,
        windowSize: IntSize,
        layoutDirection: LayoutDirection,
        popupContentSize: IntSize,
    ): IntOffset = positionAt(
        pressX = anchorBounds.left + at.x,
        pressY = anchorBounds.top + at.y,
        windowSize = windowSize,
        popupContentSize = popupContentSize,
    )

    internal companion object {

        /** Kept apart from the Compose interface so the arithmetic can be tested on its own. */
        fun positionAt(
            pressX: Int,
            pressY: Int,
            windowSize: IntSize,
            popupContentSize: IntSize,
        ): IntOffset {
            // Below and to the right of the thumb where there is room; flipped above it where
            // there is not, so the press point stays visible rather than being covered.
            val preferredY = if (pressY + popupContentSize.height <= windowSize.height) {
                pressY
            } else {
                pressY - popupContentSize.height
            }
            return IntOffset(
                x = clamp(pressX, popupContentSize.width, windowSize.width),
                y = clamp(preferredY, popupContentSize.height, windowSize.height),
            )
        }

        /** Inside the window if it fits, and hard against the leading edge if it does not. */
        private fun clamp(start: Int, size: Int, extent: Int): Int =
            start.coerceIn(0, maxOf(0, extent - size))
    }
}

/**
 * The corner of the menu the scale-up starts from: **the press point**, expressed as a fraction of
 * the menu's own box.
 *
 * A menu that grows from its own top-left is a menu that arrived from somewhere; a menu that grows
 * from the pixel the thumb was on is the thumb's menu. The difference is only visible for the 170 ms
 * it takes, which is exactly when the owner is deciding whether this thing belongs to what he did.
 *
 * It is computed from [ThumbPositionProvider.positionAt] rather than from a second guess about
 * where the popup went, so the two can never disagree: when a press near the right edge shoves the
 * menu back onto the glass, the origin slides along with it and the menu still grows out of the
 * finger. Where the popup was flipped above the press, the origin lands on the bottom edge.
 */
internal fun menuTransformOrigin(
    press: IntOffset,
    windowSize: IntSize,
    menuSize: IntSize,
): TransformOrigin {
    if (menuSize.width <= 0 || menuSize.height <= 0) return TransformOrigin.Center
    val placed = ThumbPositionProvider.positionAt(press.x, press.y, windowSize, menuSize)
    return TransformOrigin(
        pivotFractionX = ((press.x - placed.x).toFloat() / menuSize.width).coerceIn(0f, 1f),
        pivotFractionY = ((press.y - placed.y).toFloat() / menuSize.height).coerceIn(0f, 1f),
    )
}

// -- the glyphs ----------------------------------------------------------------------------------

/**
 * The three marks, drawn rather than imported.
 *
 * No icon dependency is allowed here and none is wanted: three glyphs at one stroke weight, in one
 * family, are better than three borrowed from a set drawn for a different screen. They are
 * described in **unit space** - 0..1 across the icon box - as polylines, and mapped onto the canvas
 * in one place, which is the same shape the cancel notch's geometry has and for the same reason:
 * the description can then be tested without a screen.
 *
 * - [VIEW] four corner brackets, the universal "bigger". It opens the picture and moves nothing.
 * - [DISCARD] an arrow falling into an open tray. Not a bin: this **moves** the file to
 *   `discarded/`, it does not delete it, and a bin would be a lie about what the button does.
 * - [SPECIAL] a star, which is what `special 1/` means.
 */
internal enum class MenuGlyph { VIEW, DISCARD, SPECIAL }

/** One stroke of a glyph: a polyline in unit space, optionally closed. */
internal data class GlyphStroke(val points: List<Offset>, val closed: Boolean = false)

internal fun glyphStrokes(glyph: MenuGlyph): List<GlyphStroke> = when (glyph) {
    MenuGlyph.VIEW -> {
        val near = 0.08f
        val far = 1f - near
        val arm = 0.30f
        listOf(
            GlyphStroke(listOf(Offset(near, near + arm), Offset(near, near), Offset(near + arm, near))),
            GlyphStroke(listOf(Offset(far - arm, near), Offset(far, near), Offset(far, near + arm))),
            GlyphStroke(listOf(Offset(far, far - arm), Offset(far, far), Offset(far - arm, far))),
            GlyphStroke(listOf(Offset(near + arm, far), Offset(near, far), Offset(near, far - arm))),
        )
    }
    MenuGlyph.DISCARD -> listOf(
        // The shaft, then the head that meets its tip, then the open tray it falls into.
        GlyphStroke(listOf(Offset(0.5f, 0.08f), Offset(0.5f, 0.52f))),
        GlyphStroke(listOf(Offset(0.29f, 0.32f), Offset(0.5f, 0.53f), Offset(0.71f, 0.32f))),
        GlyphStroke(
            listOf(
                Offset(0.10f, 0.64f),
                Offset(0.10f, 0.92f),
                Offset(0.90f, 0.92f),
                Offset(0.90f, 0.64f),
            )
        ),
    )
    MenuGlyph.SPECIAL -> listOf(
        GlyphStroke(starOutline(centre = Offset(0.5f, 0.54f), outer = 0.46f, inner = 0.21f), closed = true)
    )
}

/**
 * A five-pointed star as ten points, alternating outer and inner, starting at the top.
 *
 * Arithmetic rather than a copied path, so "it is centred", "it points up" and "it stays inside its
 * box" are properties that can be asserted instead of eyeballed.
 */
internal fun starOutline(
    centre: Offset,
    outer: Float,
    inner: Float,
    points: Int = 5,
): List<Offset> = (0 until points * 2).map { i ->
    val radius = if (i % 2 == 0) outer else inner
    val angle = -PI / 2 + i * PI / points
    Offset(
        x = centre.x + (radius * cos(angle)).toFloat(),
        y = centre.y + (radius * sin(angle)).toFloat(),
    )
}

/** Unit space onto the icon box, and the only place the drawing knows how big it is. */
private fun DrawScope.drawGlyph(glyph: MenuGlyph, colour: Color, strokeWidth: Float) {
    val extent = size.minDimension
    val path = Path()
    glyphStrokes(glyph).forEach { stroke ->
        stroke.points.forEachIndexed { index, point ->
            val x = point.x * extent
            val y = point.y * extent
            if (index == 0) path.moveTo(x, y) else path.lineTo(x, y)
        }
        if (stroke.closed) path.close()
    }
    drawPath(
        path = path,
        color = colour,
        style = Stroke(width = strokeWidth, cap = StrokeCap.Round, join = StrokeJoin.Round),
    )
}
