package com.rankmaster2.phone

import android.content.Context
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawing
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalClipboardManager
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.viewmodel.compose.viewModel
import com.rankmaster2.phone.media.Rm2Media
import com.rankmaster2.phone.net.Rm2Client
import com.rankmaster2.phone.net.Rm2Http
import com.rankmaster2.phone.net.Snapshot
import com.rankmaster2.phone.net.impl.OkHttpRm2Client
import com.rankmaster2.phone.store.Credentials
import com.rankmaster2.phone.store.ServerIdentity
import com.rankmaster2.phone.store.impl.CredentialsStore
import com.rankmaster2.phone.ui.browse.BrowseRoute
import com.rankmaster2.phone.ui.browse.BrowseViewModel
import com.rankmaster2.phone.ui.browse.PrefsLastFolderStore
import com.rankmaster2.phone.ui.pairing.PairingClientFactory
import com.rankmaster2.phone.ui.pairing.PairingFlow
import com.rankmaster2.phone.ui.pairing.PairingScreen
import com.rankmaster2.phone.ui.pairing.PairingViewModel
import com.rankmaster2.phone.ui.rank.RankRoute
import com.rankmaster2.phone.ui.rank.RankViewModel

/**
 * The whole app's flow, which is short on purpose: pair once, pick a folder, rank.
 *
 * There is no navigation library. Three states and two edges between them is not a graph, and the
 * back stack a library would give us is one the app does not want - backing out of a ranking
 * session should close it, not leave it open behind a screen.
 */
@Composable
fun Rm2App(context: Context) {
    val credentials: Credentials = remember { CredentialsStore.create(context) }
    var identity by remember { mutableStateOf(credentials.current()) }
    var opened by remember { mutableStateOf<Snapshot?>(null) }
    var crash by remember { mutableStateOf(CrashLog.lastCrash(context)) }

    crash?.let { text ->
        CrashReport(
            text = text,
            onDismiss = {
                CrashLog.clear(context)
                crash = null
            },
        )
        return
    }

    val paired = identity
    when {
        paired == null -> PairingGate(
            credentials = credentials,
            deviceName = android.os.Build.MODEL ?: "Android phone",
            onPaired = { identity = it },
        )

        opened == null -> BrowseGate(
            context = context,
            identity = paired,
            onOpened = { opened = it },
        )

        else -> RankGate(
            context = context,
            identity = paired,
            opened = opened!!,
            onLeave = { opened = null },
        )
    }
}

@Composable
private fun PairingGate(
    credentials: Credentials,
    deviceName: String,
    onPaired: (ServerIdentity) -> Unit,
) {
    // Every call in the pairing exchange goes through a client pinned to the fingerprint the QR
    // carried - built here, before the code is sent anywhere (SERVER_SPEC.md § 10.1.1).
    val factory = remember {
        PairingClientFactory { host, port, fingerprint ->
            OkHttpRm2Client(
                baseUrl = "https://$host:$port/api/v1",
                http = Rm2Http.pairingClient(fingerprint),
            )
        }
    }

    val viewModel: PairingViewModel = viewModel {
        // PairingFlow stores the identity itself, the moment the server hands the token over:
        // the token is returned exactly once and is never readable again, so it must be on disk
        // before anything else can go wrong (SERVER_SPEC.md § 10.11).
        PairingViewModel(
            flow = PairingFlow(factory, credentials),
            deviceName = deviceName,
        )
    }
    val state by viewModel.state.collectAsState()

    PairingScreen(
        state = state,
        onPayloadScanned = viewModel::onPayloadScanned,
        onManualChanged = viewModel::onManualChanged,
        onManualSubmit = viewModel::onManualSubmit,
        onShowScanner = viewModel::showScanner,
        onShowManual = viewModel::showManualEntry,
        onDismissFailure = viewModel::dismissFailure,
        onPaired = onPaired,
    )
}

@Composable
private fun BrowseGate(context: Context, identity: ServerIdentity, onOpened: (Snapshot) -> Unit) {
    val client: Rm2Client = remember(identity) { OkHttpRm2Client(identity) }
    val viewModel: BrowseViewModel = viewModel(
        factory = BrowseViewModel.factory(
            client = client,
            lastFolders = PrefsLastFolderStore(context),
            onOpened = onOpened,
        )
    )
    BrowseRoute(viewModel)
}

@Composable
private fun RankGate(
    context: Context,
    identity: ServerIdentity,
    opened: Snapshot,
    onLeave: () -> Unit,
) {
    val client: Rm2Client = remember(identity) { OkHttpRm2Client(identity) }
    val media = rememberMedia(context, identity)

    // Keyed by the session, so opening a different folder is a different screen with a different
    // view model rather than the old one holding a token from a session that has gone.
    val viewModel: RankViewModel = viewModel(
        key = opened.sessionId,
        factory = RankViewModel.factory(client, opened),
    )

    RankRoute(viewModel = viewModel, media = media, onLeave = onLeave)
}

/**
 * What the app died of last time, shown once and then forgotten.
 *
 * The owner is not going to fetch a log with developer tools, and "it just closed" is not something
 * anyone can fix. This is the whole of the diagnosis, on the screen of the phone it happened on.
 */
@Composable
private fun CrashReport(text: String, onDismiss: () -> Unit) {
    val clipboard = LocalClipboardManager.current

    Column(
        Modifier
            .fillMaxSize()
            .background(Color.Black)
            .windowInsetsPadding(WindowInsets.safeDrawing)
            .padding(20.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Text("Rank Master closed itself last time", color = Color(0xFFECEEF2))
        Text(
            "This is why. Send it to whoever is building this.",
            color = Color(0xFF9AA3B2),
            fontSize = 13.sp,
        )
        Text(
            text = text,
            color = Color(0xFF9AA3B2),
            fontSize = 11.sp,
            modifier = Modifier.weight(1f).verticalScroll(rememberScrollState()),
        )
        Row(horizontalArrangement = Arrangement.spacedBy(16.dp)) {
            TextButton(onClick = { clipboard.setText(AnnotatedString(text)) }) {
                Text("Copy", color = Color(0xFF2ECC71))
            }
            TextButton(onClick = onDismiss) { Text("Carry on", color = Color(0xFF2ECC71)) }
        }
    }
}

/**
 * One handle per identity. Coil's loader and the video players are expensive to build and cheap to
 * share, and building them per screen is how a second HTTP client sneaks into an app that has one.
 */
@Composable
fun rememberMedia(context: Context, identity: ServerIdentity): Rm2Media =
    remember(identity) { Rm2Media.create(context, identity) }
