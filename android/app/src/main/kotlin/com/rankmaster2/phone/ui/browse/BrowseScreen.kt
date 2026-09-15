package com.rankmaster2.phone.ui.browse

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.safeDrawing
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.runtime.collectAsState
import com.rankmaster2.phone.net.Root

/**
 * The folder browser, drawn.
 *
 * Every composable in this file takes state and callbacks and owns nothing, so the screen can be
 * rendered from a test, a preview or a different host without a `Rm2Client` anywhere near it. The
 * decisions - what is greyed, what a null count says, where "up" goes - were all made in
 * [BrowseViewModel]; this file only has opinions about pixels.
 */

// A greyed row must still be *legible*. Dimming to the point of unreadable turns "this folder can
// not be ranked, here is why" back into "this folder is missing", which is the thing § 10.15's
// grey-don't-hide rule exists to prevent.
private val Dimmed = Color(0xFF7C838F)
private val Faint = Color(0xFF9AA2AE)
private val Line = Color(0xFF23262B)
private val Warn = Color(0xFFE8B63D)
private val Bad = Color(0xFFE2635B)

@Composable
fun BrowseRoute(
    viewModel: BrowseViewModel,
    /** Forget this PC and go back to pairing. The only way out of a PC that stopped trusting us. */
    onPairAgain: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val state by viewModel.state.collectAsState()

    // Back comes up a level, exactly as "Up" does, and only means "leave the app" at the drive
    // list. Without this, walking four folders down and pressing back closed the app - Android's
    // default, because nothing here had ever claimed the gesture.
    //
    // `canGoUp` is the same fact the Up button is drawn from, and it comes from section 10.15's
    // `parent`: no path arithmetic decides where up is, or whether there is an up.
    // Not while the "no longer paired" panel is up: walking up a level is one more request that
    // is certain to be refused, and it would replace the one screen that has a way out on it.
    BackHandler(enabled = state.canGoUp && !state.pairingLost) { viewModel.up() }

    BrowseScreen(
        state = state,
        onEnter = viewModel::enter,
        onOpen = viewModel::open,
        onOpenCurrent = viewModel::openCurrent,
        onOpenRemembered = viewModel::openRemembered,
        onUp = viewModel::up,
        onRefresh = viewModel::refresh,
        onRetryCounts = viewModel::retryCounts,
        onDismissFailure = viewModel::dismissOpenFailure,
        onCloseOtherSession = viewModel::closeOtherSessionAndRetry,
        onPairAgain = onPairAgain,
        modifier = modifier,
    )
}

@Composable
fun BrowseScreen(
    state: BrowseUiState,
    onEnter: (String) -> Unit,
    onOpen: (String) -> Unit,
    onOpenCurrent: () -> Unit,
    onOpenRemembered: () -> Unit,
    onUp: () -> Unit,
    onRefresh: () -> Unit,
    onRetryCounts: () -> Unit,
    onDismissFailure: () -> Unit,
    onCloseOtherSession: () -> Unit,
    onPairAgain: () -> Unit = {},
    modifier: Modifier = Modifier,
) {
    Surface(modifier = modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
        // This app draws edge to edge with the system bars hidden, so nothing puts a screen clear
        // of the camera cutout or the gesture bar unless it asks. Without this the header runs
        // under the punch-hole in portrait and under the cutout down one side in landscape, and
        // the "Open this folder" button at the bottom sits exactly on the home swipe.
        //
        // The ranking screen is the deliberate exception - a photograph should have the whole
        // glass - but a list of folders with a button on it is an ordinary screen and behaves
        // like one.
        //
        // Note what this does and does not cover: the bars are hidden, so their share of
        // `safeDrawing` is zero and what is left is the display cutout (and the keyboard). That is
        // the part that was actually eating the header. The home *gesture* at the bottom has no
        // inset to give while the bar is hidden, so the bottom bar buys its own room - see
        // OpenThisFolderBar.
        Column(Modifier.fillMaxSize().windowInsetsPadding(WindowInsets.safeDrawing)) {
            Header(state = state, onUp = onUp, onRefresh = onRefresh)

            if (state.loadingList) {
                LinearProgressIndicator(Modifier.fillMaxWidth())
            } else {
                Spacer(Modifier.height(4.dp))
            }

            state.openFailure?.let {
                FailureBanner(
                    failure = it,
                    onDismiss = onDismissFailure,
                    onCloseOtherSession = onCloseOtherSession,
                )
            }

            Box(Modifier.weight(1f)) {
                when {
                    // First, because it is the only one of these the owner cannot try again out of.
                    state.pairingLost -> PairingLost(onPairAgain)
                    state.listFailure != null -> ListFailure(state.listFailure, onRefresh)
                    state.place is Place.Roots -> RootList(state.roots, state.lastFolder, onEnter, onOpenRemembered)
                    else -> EntryList(state, onEnter, onOpen, onRetryCounts)
                }
            }

            if (state.place is Place.Folder && !state.pairingLost) {
                OpenThisFolderBar(state, onOpenCurrent)
            }
        }
    }
}

// -- chrome ------------------------------------------------------------------------------------

@Composable
private fun Header(state: BrowseUiState, onUp: () -> Unit, onRefresh: () -> Unit) {
    Column(Modifier.fillMaxWidth().padding(start = 16.dp, end = 8.dp, top = 12.dp, bottom = 8.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            if (state.canGoUp) {
                TextButton(onClick = onUp, modifier = Modifier.padding(end = 4.dp)) { Text("‹ Up") }
            }
            Column(Modifier.weight(1f)) {
                // Where he is, not how many things are under him. The count is worth having and
                // is not worth the heading.
                Text(
                    text = when (val place = state.place) {
                        Place.Roots -> "Choose a folder"
                        is Place.Folder -> folderLabel(place.path)
                    },
                    style = MaterialTheme.typography.titleMedium,
                    fontWeight = FontWeight.SemiBold,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
                if (state.place is Place.Folder && !state.loadingList) {
                    val count = folderCount(state.rows.size)
                    Text(
                        text = if (count.isEmpty()) "no sub-folders" else count,
                        style = MaterialTheme.typography.labelSmall,
                        color = Dimmed,
                    )
                }
            }
            // Refresh is hidden rather than disabled once the pairing is gone: there is exactly
            // one thing to do on that screen and it is on the panel below.
            if (!state.pairingLost) {
                TextButton(onClick = onRefresh) { Text("Refresh") }
            }
        }

        val path = state.currentPath
        if (path != null) {
            // Windows paths get long and the useful half is the tail, so it scrolls instead of
            // being ellipsised from the end.
            Text(
                text = path,
                style = MaterialTheme.typography.bodySmall,
                fontFamily = FontFamily.Monospace,
                color = Faint,
                maxLines = 1,
                modifier = Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()),
            )
            if (state.atRoot) {
                // § 10.15: parent == null. Worth saying out loud, because "Up" going to the drive
                // list rather than to a folder is otherwise a surprise.
                Text("top of this drive", style = MaterialTheme.typography.labelSmall, color = Dimmed)
            }
        }
    }
}

@Composable
private fun ListFailure(message: String, onRefresh: () -> Unit) {
    Column(
        modifier = Modifier.fillMaxSize().padding(24.dp),
        verticalArrangement = Arrangement.Center,
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Text("Could not open that folder's list", style = MaterialTheme.typography.titleMedium)
        Spacer(Modifier.height(6.dp))
        Text(message, style = MaterialTheme.typography.bodyMedium, color = Faint)
        Spacer(Modifier.height(16.dp))
        Button(onClick = onRefresh) { Text("Try again") }
    }
}

/**
 * The end of this phone's relationship with that PC, and the one way out of it.
 *
 * Reached when the PC presents a certificate this phone never pinned, or when the token stops being
 * accepted. Both are permanent, and the old build said "pair again" while offering no way to do it:
 * the pairing screen only appears when no credentials are stored, so the app simply stopped working
 * and the only remaining move was to uninstall it.
 *
 * Nothing here says "try again", because trying again is the one thing that cannot help.
 */
@Composable
private fun PairingLost(onPairAgain: () -> Unit) {
    Column(
        modifier = Modifier.fillMaxSize().padding(24.dp),
        verticalArrangement = Arrangement.Center,
    ) {
        Text(
            "This phone is not paired with the PC any more",
            style = MaterialTheme.typography.titleMedium,
            fontWeight = FontWeight.SemiBold,
            color = Bad,
        )
        Spacer(Modifier.height(8.dp))
        Text(
            "Either Rank Master on the PC was given a new security certificate, or this phone was " +
                "removed from its list. Nothing is lost - every ranking lives on the PC.",
            style = MaterialTheme.typography.bodyMedium,
            color = Faint,
        )
        Spacer(Modifier.height(20.dp))
        Button(onClick = onPairAgain, modifier = Modifier.fillMaxWidth()) {
            Text("Pair this phone again")
        }
        Spacer(Modifier.height(8.dp))
        Text(
            "Open the Rank Master icon in the PC's system tray and choose “Pair a phone”, then " +
                "scan the code it shows.",
            style = MaterialTheme.typography.bodySmall,
            color = Dimmed,
        )
    }
}

@Composable
private fun FailureBanner(
    failure: OpenFailure,
    onDismiss: () -> Unit,
    onCloseOtherSession: () -> Unit,
) {
    val accent = if (failure is OpenFailure.Unreachable && failure.pinMismatch) Bad else Warn
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .padding(horizontal = 12.dp, vertical = 6.dp)
            .border(1.dp, accent.copy(alpha = 0.5f), RoundedCornerShape(10.dp))
            .background(accent.copy(alpha = 0.08f), RoundedCornerShape(10.dp))
            .padding(14.dp),
    ) {
        Text(
            openFailureHeadline(failure),
            style = MaterialTheme.typography.titleSmall,
            fontWeight = FontWeight.SemiBold,
            color = accent,
        )
        Spacer(Modifier.height(4.dp))
        Text(failure.folder, style = MaterialTheme.typography.bodySmall, fontFamily = FontFamily.Monospace, color = Faint)
        Spacer(Modifier.height(6.dp))
        Text(openFailureAdvice(failure), style = MaterialTheme.typography.bodyMedium)
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
            if (offersCloseAndRetry(failure)) {
                TextButton(onClick = onCloseOtherSession) { Text("Close it and open this") }
            }
            TextButton(onClick = onDismiss) { Text("Dismiss") }
        }
    }
}

// -- the drive list ------------------------------------------------------------------------------

@Composable
private fun RootList(
    roots: List<Root>,
    lastFolder: RememberedFolder?,
    onEnter: (String) -> Unit,
    onOpenRemembered: () -> Unit,
) {
    LazyColumn(Modifier.fillMaxSize()) {
        if (lastFolder != null) {
            item { LastFolderCard(lastFolder, onOpenRemembered) }
        }
        items(roots, key = { it.path }) { root ->
            RootRow(root, onEnter)
            HorizontalDivider(color = Line)
        }
        if (roots.isEmpty()) {
            item {
                Text(
                    "The PC listed no drives. That is the PC's answer, not a connection problem - " +
                        "“Refresh” asks it again.",
                    color = Faint,
                    modifier = Modifier.padding(24.dp),
                )
            }
        }
    }
}

@Composable
private fun LastFolderCard(folder: RememberedFolder, onOpen: () -> Unit) {
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .padding(12.dp)
            .border(1.dp, Line, RoundedCornerShape(12.dp))
            .padding(16.dp),
    ) {
        Text("Last ranked", style = MaterialTheme.typography.labelMedium, color = Faint)
        Spacer(Modifier.height(4.dp))
        Text(folder.name, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
        Text(
            folder.path,
            style = MaterialTheme.typography.bodySmall,
            fontFamily = FontFamily.Monospace,
            color = Dimmed,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
        Spacer(Modifier.height(10.dp))
        Button(onClick = onOpen) { Text("Open it again") }
    }
}

@Composable
private fun RootRow(root: Root, onEnter: (String) -> Unit) {
    // § 10.14: an unavailable root is listed, not dropped. Tapping it would only fail, so it is
    // inert - but it is visible, and it says why.
    val enabled = root.available
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .then(if (enabled) Modifier.clickable { onEnter(root.path) } else Modifier)
            .padding(horizontal = 16.dp, vertical = 14.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Marker(enabled)
        Spacer(Modifier.width(14.dp))
        Column(Modifier.weight(1f)) {
            Text(
                root.label.ifBlank { root.path },
                style = MaterialTheme.typography.bodyLarge,
                color = if (enabled) MaterialTheme.colorScheme.onSurface else Dimmed,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            val detail = rootDetail(root.available, root.totalBytes, root.freeBytes)
            val kind = root.kind.takeIf { it.isNotBlank() && it != "unknown" }
            val line = listOfNotNull(kind, detail.takeIf { it.isNotEmpty() }).joinToString(" · ")
            if (line.isNotEmpty()) {
                Text(line, style = MaterialTheme.typography.bodySmall, color = if (enabled) Faint else Dimmed)
            }
        }
        if (enabled) Text("›", color = Faint)
    }
}

// -- the folder list -----------------------------------------------------------------------------

@Composable
private fun EntryList(
    state: BrowseUiState,
    onEnter: (String) -> Unit,
    onOpen: (String) -> Unit,
    onRetryCounts: () -> Unit,
) {
    LazyColumn(Modifier.fillMaxSize()) {
        if (state.counts == CountsPhase.Failed) {
            item { CountsFailedNote(onRetryCounts) }
        }
        items(state.rows, key = { it.path }) { row ->
            EntryRow(
                row = row,
                counting = state.counts == CountsPhase.Loading,
                opening = state.openingPath == row.path,
                openEnabled = !state.isOpening,
                onEnter = onEnter,
                onOpen = onOpen,
            )
            HorizontalDivider(color = Line)
        }
        if (state.rows.isEmpty() && !state.loadingList) {
            item {
                Text(
                    "Nothing below this one. If the photographs are in this folder itself, " +
                        "“Open this folder” at the bottom is what ranks them.",
                    color = Faint,
                    modifier = Modifier.padding(24.dp),
                )
            }
        }
    }
}

@Composable
private fun CountsFailedNote(onRetry: () -> Unit) {
    Row(
        modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        // Not an error dialog: the names below work, and on a network share this is routine.
        Text(
            "Could not count the files in these folders.",
            style = MaterialTheme.typography.bodySmall,
            color = Faint,
            modifier = Modifier.weight(1f),
        )
        TextButton(onClick = onRetry) { Text("Count again") }
    }
}

@Composable
private fun EntryRow(
    row: FolderRow,
    counting: Boolean,
    opening: Boolean,
    openEnabled: Boolean,
    onEnter: (String) -> Unit,
    onOpen: (String) -> Unit,
) {
    val dim = !row.openable
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .then(if (row.enterable) Modifier.clickable { onEnter(row.path) } else Modifier)
            .padding(start = 16.dp, end = 8.dp, top = 12.dp, bottom = 12.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Marker(!dim)
        Spacer(Modifier.width(14.dp))
        Column(Modifier.weight(1f)) {
            Text(
                row.name,
                style = MaterialTheme.typography.bodyLarge,
                color = if (dim) Dimmed else MaterialTheme.colorScheme.onSurface,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            val detail = folderDetail(row, counting)
            if (detail.isNotEmpty()) {
                Text(detail, style = MaterialTheme.typography.bodySmall, color = if (dim) Dimmed else Faint)
            }
            // The whole point of rule 1: the folder is here, and this line says why it is grey.
            if (row.rankability == Rankability.No || !row.accessible) {
                Text(notRankableReason(row), style = MaterialTheme.typography.labelSmall, color = Warn)
            }
        }
        when {
            opening -> CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp)
            row.openable -> TextButton(onClick = { onOpen(row.path) }, enabled = openEnabled) { Text("Rank") }
            else -> Spacer(Modifier.width(8.dp))
        }
        if (row.enterable) Text("›", color = if (dim) Dimmed else Faint)
    }
}

@Composable
private fun Marker(bright: Boolean) {
    Box(
        Modifier
            .size(width = 18.dp, height = 14.dp)
            .background(
                if (bright) MaterialTheme.colorScheme.onSurface.copy(alpha = 0.22f) else Line,
                RoundedCornerShape(topStart = 2.dp, topEnd = 4.dp, bottomEnd = 3.dp, bottomStart = 3.dp),
            ),
    )
}

// -- the bottom bar ------------------------------------------------------------------------------

@Composable
private fun OpenThisFolderBar(state: BrowseUiState, onOpenCurrent: () -> Unit) {
    val path = state.currentPath ?: return
    val opening = state.openingPath == path
    HorizontalDivider(color = Line)
    Row(
        // Extra room underneath, because `safeDrawing` gives none here. This app hides the system
        // bars, so their insets come back as zero - but the *gesture* is still there, and the
        // bottom strip of the glass is the home swipe whether or not a bar is drawn on it. A
        // button sitting on it is a button that sometimes sends you to the launcher instead.
        modifier = Modifier.fillMaxWidth().padding(16.dp).padding(bottom = 12.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column(Modifier.weight(1f)) {
            Text("Rank the folder you are in", style = MaterialTheme.typography.bodyMedium)
            // § 10.15 lists a folder's *children*; it never says whether the folder you are
            // standing in is rankable. So this button is always live and the server decides.
            Text(
                path,
                style = MaterialTheme.typography.bodySmall,
                fontFamily = FontFamily.Monospace,
                color = Dimmed,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
        }
        Spacer(Modifier.width(12.dp))
        Button(onClick = onOpenCurrent, enabled = !state.isOpening) {
            if (opening) {
                CircularProgressIndicator(Modifier.size(16.dp), strokeWidth = 2.dp)
                Spacer(Modifier.width(8.dp))
            }
            Text("Open this folder")
        }
    }
}
