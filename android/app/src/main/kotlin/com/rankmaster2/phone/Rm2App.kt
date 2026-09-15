package com.rankmaster2.phone

import android.content.ComponentCallbacks2
import android.content.Context
import android.content.Intent
import android.content.res.Configuration
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawing
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.Saver
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalClipboardManager
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.unit.dp
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
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
import com.rankmaster2.phone.ui.pairing.PairingRoute
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
    var opened by rememberSaveable(stateSaver = SessionSaver) { mutableStateOf<Snapshot?>(null) }
    var crash by remember { mutableStateOf(CrashLog.lastCrash(context)) }

    /**
     * How many times this phone has been paired during this run of the app.
     *
     * View models here live in the activity's store and are looked up by key, so without something
     * that changes, "pair again" would hand back the *previous* pairing's view models: a pairing
     * screen already holding the old identity (which would announce "Paired" and walk straight back
     * into the broken state), and a folder browser still holding a client with the revoked token.
     * Bumping this is what makes starting over actually start over.
     */
    var pairings by rememberSaveable { mutableStateOf(0) }

    crash?.let { text ->
        CrashReport(
            context = context,
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
            round = pairings,
            onPaired = { identity = it },
        )

        opened == null -> BrowseGate(
            context = context,
            identity = paired,
            round = pairings,
            onOpened = { opened = it },
            // The only way back to the pairing screen. It exists because the PC can stop trusting
            // this phone - a regenerated certificate, a revoked token - and until now that left
            // the app permanently telling the owner to pair again with nothing that could.
            onPairAgain = {
                credentials.clear()
                opened = null
                identity = null
                pairings += 1
            },
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
    round: Int,
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

    val viewModel: PairingViewModel = viewModel(key = "pairing-$round") {
        // PairingFlow stores the identity itself, the moment the server hands the token over:
        // the token is returned exactly once and is never readable again, so it must be on disk
        // before anything else can go wrong (SERVER_SPEC.md § 10.11).
        PairingViewModel(
            flow = PairingFlow(factory, credentials),
            deviceName = deviceName,
        )
    }
    // The screen's own route rather than a second copy of the same wiring here: two places that
    // hand the same nine callbacks over is one place for them to drift apart.
    PairingRoute(viewModel = viewModel, onPaired = onPaired)
}

@Composable
private fun BrowseGate(
    context: Context,
    identity: ServerIdentity,
    round: Int,
    onOpened: (Snapshot) -> Unit,
    onPairAgain: () -> Unit,
) {
    val client: Rm2Client = remember(identity) { OkHttpRm2Client(identity) }
    val viewModel: BrowseViewModel = viewModel(
        // Keyed by the pairing, so a browser built around a token that has since been revoked is
        // not handed back to the phone that has just been paired again.
        key = "browse-$round",
        factory = BrowseViewModel.factory(
            client = client,
            lastFolders = PrefsLastFolderStore(context),
            onOpened = onOpened,
        ),
    )
    BrowseRoute(viewModel = viewModel, onPairAgain = onPairAgain)
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

    // These live in the activity's store and outlive the screen, and a re-opened folder resumes
    // under its old `sessionId` (SERVER_SPEC.md section 10.1) - so the key can match a view model
    // that is still holding the pair from last time. The snapshot just handed back is the live one.
    LaunchedEffect(opened) { viewModel.resume(opened) }

    RankRoute(viewModel = viewModel, media = media, onLeave = onLeave)
}

/**
 * The open session, across an activity that is thrown away and rebuilt.
 *
 * Two things do that and the owner did neither: changing the system font or display size, and
 * Android reclaiming the app while it was in the background - which during a long session in a
 * video folder is a normal Tuesday. Without this he comes back to the folder list, with the PC
 * still holding the folder, and has to find it and open it again.
 *
 * Saving the snapshot is not the same as trusting it. It is only where the screen starts; the very
 * next thing that happens is `ON_START`, which re-reads `GET /session` and replaces it with what is
 * actually true. If the session went away in the meantime that read says so, and the screen offers
 * the way back to the folder list. So the worst case is one wasted round trip, and the best case is
 * that a session survives being killed.
 *
 * `warmPairs` is dropped on the way in: they are prefetch hints (SERVER_SPEC.md section 9.5), the
 * next response brings a fresh set, and there is no reason to put kilobytes of them through a
 * saved-instance bundle that has a hard size limit.
 */
internal val SessionSaver: Saver<Snapshot?, String> = Saver(
    save = { snapshot ->
        snapshot?.let { runCatching { SessionJson.encodeToString(it.copy(warmPairs = emptyList())) }.getOrNull() }
    },
    restore = { text -> runCatching { SessionJson.decodeFromString<Snapshot>(text) }.getOrNull() },
)

private val SessionJson = Json { ignoreUnknownKeys = true }

/**
 * What the app died of last time, shown once and then forgotten.
 *
 * The owner is not going to fetch a log with developer tools, and "it just closed" is not something
 * anyone can fix. This is the whole of the diagnosis, on the screen of the phone it happened on.
 *
 * What he needs from this screen, in order: that nothing was lost, that he can carry on, and a way
 * to send the details to whoever can act on them. The stack trace is the last of those three and is
 * folded away, because it is addressed to somebody else - showing a wall of Java class names to the
 * one person who cannot read it is how a useful screen becomes a frightening one.
 */
@Composable
private fun CrashReport(context: Context, text: String, onDismiss: () -> Unit) {
    val clipboard = LocalClipboardManager.current
    var showDetails by remember { mutableStateOf(false) }
    var copied by remember { mutableStateOf(false) }

    Column(
        Modifier
            .fillMaxSize()
            .background(Color.Black)
            .windowInsetsPadding(WindowInsets.safeDrawing)
            .padding(24.dp),
        verticalArrangement = Arrangement.spacedBy(14.dp),
    ) {
        Text(
            "Rank Master closed itself last time",
            color = Color(0xFFECEEF2),
            fontSize = 20.sp,
        )
        Text(
            "Nothing was lost. Every vote is written on the PC before the phone is told about it, " +
                "so the folder is exactly where you left it. Carry on and it will pick up there.",
            color = Color(0xFFB4B9C2),
            fontSize = 14.sp,
        )
        Text(
            "The phone wrote down why. Sending it on is the only thing that gets this fixed.",
            color = Color(0xFF9AA3B2),
            fontSize = 13.sp,
        )

        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            Button(onClick = { context.share(text) }) { Text("Send it") }
            TextButton(
                onClick = {
                    clipboard.setText(AnnotatedString(text))
                    copied = true
                }
            ) {
                Text(if (copied) "Copied" else "Copy", color = Color(0xFF2ECC71))
            }
        }

        TextButton(onClick = { showDetails = !showDetails }) {
            Text(
                if (showDetails) "Hide the details" else "Show the details",
                color = Color(0xFF9AA3B2),
            )
        }

        if (showDetails) {
            Text(
                text = text,
                color = Color(0xFF7C838F),
                fontSize = 11.sp,
                modifier = Modifier.weight(1f).verticalScroll(rememberScrollState()),
            )
        } else {
            Spacer(Modifier.weight(1f))
        }

        Button(onClick = onDismiss, modifier = Modifier.fillMaxWidth()) { Text("Carry on") }
    }
}

/**
 * Hands the report to whatever the owner sends things with. No address is built in: he knows who
 * is building this and the phone already knows how he talks to them.
 */
private fun Context.share(text: String) {
    val intent = Intent(Intent.ACTION_SEND).apply {
        type = "text/plain"
        putExtra(Intent.EXTRA_SUBJECT, "Rank Master crash")
        putExtra(Intent.EXTRA_TEXT, text)
    }
    runCatching { startActivity(Intent.createChooser(intent, "Send the crash report")) }
}

/**
 * One handle per identity. Coil's loader and the video players are expensive to build and cheap to
 * share, and building them per screen is how a second HTTP client sneaks into an app that has one.
 *
 * It also gives the decoded bitmaps back when nobody is looking at them, which CLIENT_PLAN.md
 * section 3.4.1 asked for and nothing was doing. The memory cache is a convenience - the disk cache
 * is what actually makes a photograph instant on second sight - and holding tens of megabytes of
 * decoded 2160 px images while the app is in the background is holding them at exactly the moment
 * Android is deciding what to reclaim. `TRIM_MEMORY_UI_HIDDEN` is that moment.
 */
@Composable
fun rememberMedia(context: Context, identity: ServerIdentity): Rm2Media {
    val media = remember(identity) { Rm2Media.create(context, identity) }

    val application = context.applicationContext
    DisposableEffect(media) {
        val callbacks = object : ComponentCallbacks2 {
            override fun onTrimMemory(level: Int) {
                if (level >= ComponentCallbacks2.TRIM_MEMORY_UI_HIDDEN) media.dropDecodedImages()
            }

            override fun onConfigurationChanged(configuration: Configuration) = Unit

            @Deprecated("Called on older releases; nothing here needs it.")
            override fun onLowMemory() = media.dropDecodedImages()
        }
        application.registerComponentCallbacks(callbacks)
        onDispose { application.unregisterComponentCallbacks(callbacks) }
    }

    return media
}
