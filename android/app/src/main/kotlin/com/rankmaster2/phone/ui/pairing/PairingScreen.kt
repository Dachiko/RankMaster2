package com.rankmaster2.phone.ui.pairing

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import com.rankmaster2.phone.store.ServerIdentity

/**
 * The pairing screen: everything between "this phone has never seen a PC" and "this phone holds a
 * token for one".
 *
 * Every composable here takes state and callbacks and holds nothing of its own beyond scroll
 * position. The decisions live in [PairingViewModel]; this file only renders them, which is why the
 * order-of-operations that matters (`SERVER_SPEC.md` § 10.1.1) can be proven in a JVM test.
 */
@Composable
fun PairingScreen(
    state: PairingUiState,
    onPayloadScanned: (String) -> Unit,
    onManualChanged: (ManualEntry) -> Unit,
    onManualSubmit: () -> Unit,
    onShowScanner: () -> Unit,
    onShowManual: () -> Unit,
    onDismissFailure: () -> Unit,
    onPaired: (ServerIdentity) -> Unit,
    modifier: Modifier = Modifier,
) {
    Surface(modifier = modifier.fillMaxSize(), color = Color.Black) {
        val paired = state.paired
        when {
            paired != null -> PairedPanel(paired, onContinue = { onPaired(paired) })

            state.failure != null -> FailurePanel(
                failure = state.failure,
                onTryAgain = onDismissFailure,
                onEnterByHand = onShowManual,
            )

            state.busy -> BusyPanel()

            state.mode == PairingMode.Manual -> ManualPanel(
                entry = state.manual,
                onChanged = onManualChanged,
                onSubmit = onManualSubmit,
                onUseCamera = onShowScanner,
            )

            else -> ScanPanel(
                active = state.scannerActive,
                onPayloadScanned = onPayloadScanned,
                onEnterByHand = onShowManual,
            )
        }
    }
}

// -- the panels --------------------------------------------------------------------------------

@Composable
private fun ScanPanel(
    active: Boolean,
    onPayloadScanned: (String) -> Unit,
    onEnterByHand: () -> Unit,
) {
    Column(modifier = Modifier.fillMaxSize()) {
        Column(modifier = Modifier.padding(24.dp)) {
            Heading("Point the phone at the PC")
            Spacer(Modifier.height(8.dp))
            Body(
                "On the PC, open the Rank Master icon in the system tray and choose “Pair a " +
                    "phone”. A QR code appears; hold it in the frame below."
            )
        }
        Box(
            modifier = Modifier
                .fillMaxWidth()
                .weight(1f)
                .padding(horizontal = 24.dp)
                .clip(RoundedCornerShape(16.dp))
                .background(Color(0xFF101010)),
        ) {
            QrScanner(
                active = active,
                onPayload = onPayloadScanned,
                onEnterByHand = onEnterByHand,
                modifier = Modifier.fillMaxSize(),
            )
        }
        Column(modifier = Modifier.padding(24.dp)) {
            TextButton(onClick = onEnterByHand) {
                Text("The camera will not cooperate — type it instead")
            }
        }
    }
}

@Composable
private fun BusyPanel() {
    Column(
        modifier = Modifier.fillMaxSize().padding(32.dp),
        verticalArrangement = Arrangement.Center,
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        CircularProgressIndicator(modifier = Modifier.size(40.dp))
        Spacer(Modifier.height(24.dp))
        Heading("Checking the PC")
        Spacer(Modifier.height(8.dp))
        Body(
            "Making sure the machine answering is the one the code came from, before the code is " +
                "sent anywhere."
        )
    }
}

@Composable
private fun FailurePanel(
    failure: PairingFailure,
    onTryAgain: () -> Unit,
    onEnterByHand: () -> Unit,
) {
    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(32.dp),
        verticalArrangement = Arrangement.Center,
    ) {
        Text(
            text = failure.title,
            color = if (failure.fatal) Color(0xFFFF6B6B) else Color(0xFFECEEF2),
            style = MaterialTheme.typography.headlineSmall,
            fontWeight = FontWeight.SemiBold,
        )
        Spacer(Modifier.height(12.dp))
        Body(failure.body)
        Spacer(Modifier.height(32.dp))

        // A certificate mismatch offers nothing. No "continue anyway", no "trust once": on a home
        // network there is no innocent reason for it, and an escape hatch here would be the only
        // thing standing between the user and the attack the pin exists to stop.
        if (!failure.fatal) {
            Button(onClick = onTryAgain, modifier = Modifier.fillMaxWidth()) {
                Text("Try again")
            }
            Spacer(Modifier.height(8.dp))
            TextButton(onClick = onEnterByHand, modifier = Modifier.fillMaxWidth()) {
                Text("Type the details by hand")
            }
        }
    }
}

@Composable
private fun ManualPanel(
    entry: ManualEntry,
    onChanged: (ManualEntry) -> Unit,
    onSubmit: () -> Unit,
    onUseCamera: () -> Unit,
) {
    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(24.dp),
    ) {
        Heading("Type it in")
        Spacer(Modifier.height(8.dp))
        Body(
            "The PC shows all four of these next to the QR code. The fingerprint is the long one; " +
                "spaces in it do not matter."
        )
        Spacer(Modifier.height(24.dp))

        Field(
            label = "Address of the PC",
            value = entry.host,
            onValueChange = { onChanged(entry.copy(host = it)) },
            keyboard = KeyboardType.Uri,
        )
        Spacer(Modifier.height(12.dp))
        Field(
            label = "Port",
            value = entry.port,
            onValueChange = { onChanged(entry.copy(port = it)) },
            keyboard = KeyboardType.Number,
        )
        Spacer(Modifier.height(12.dp))
        Field(
            label = "Certificate fingerprint",
            value = entry.fingerprint,
            onValueChange = { onChanged(entry.copy(fingerprint = it)) },
            keyboard = KeyboardType.Ascii,
            monospace = true,
            supporting = "64 characters, digits and a–f",
        )
        Spacer(Modifier.height(12.dp))
        Field(
            label = "Pairing code",
            value = entry.code,
            onValueChange = { onChanged(entry.copy(code = it)) },
            keyboard = KeyboardType.Number,
            monospace = true,
            supporting = "Six digits, e.g. 418 250",
        )

        Spacer(Modifier.height(24.dp))
        Button(onClick = onSubmit, modifier = Modifier.fillMaxWidth()) {
            Text("Pair")
        }
        Spacer(Modifier.height(8.dp))
        TextButton(onClick = onUseCamera, modifier = Modifier.fillMaxWidth()) {
            Text("Use the camera instead")
        }
    }
}

@Composable
private fun PairedPanel(identity: ServerIdentity, onContinue: () -> Unit) {
    Column(
        modifier = Modifier.fillMaxSize().padding(32.dp),
        verticalArrangement = Arrangement.Center,
    ) {
        Heading("Paired")
        Spacer(Modifier.height(12.dp))
        Body("This phone is now paired with ${identity.host}:${identity.port}.")
        Spacer(Modifier.height(4.dp))
        Text(
            text = identity.certificateFingerprint,
            color = Color(0xFF8A8F98),
            fontFamily = FontFamily.Monospace,
            style = MaterialTheme.typography.bodySmall,
        )
        Spacer(Modifier.height(32.dp))
        Button(onClick = onContinue, modifier = Modifier.fillMaxWidth()) {
            Text("Pick a folder")
        }
    }
}

// -- small pieces ------------------------------------------------------------------------------

@Composable
private fun Field(
    label: String,
    value: String,
    onValueChange: (String) -> Unit,
    keyboard: KeyboardType,
    monospace: Boolean = false,
    supporting: String? = null,
) {
    OutlinedTextField(
        value = value,
        onValueChange = onValueChange,
        label = { Text(label) },
        singleLine = true,
        keyboardOptions = KeyboardOptions(keyboardType = keyboard),
        textStyle = if (monospace) {
            MaterialTheme.typography.bodyLarge.copy(fontFamily = FontFamily.Monospace)
        } else {
            MaterialTheme.typography.bodyLarge
        },
        supportingText = supporting?.let { { Text(it) } },
        modifier = Modifier.fillMaxWidth(),
    )
}

@Composable
internal fun Heading(text: String) {
    Text(
        text = text,
        color = Color(0xFFECEEF2),
        style = MaterialTheme.typography.headlineSmall,
        fontWeight = FontWeight.SemiBold,
    )
}

@Composable
internal fun Body(text: String) {
    Text(
        text = text,
        color = Color(0xFFB4B9C2),
        style = MaterialTheme.typography.bodyMedium,
    )
}

/**
 * The screen, bound to its [PairingViewModel]. Integration builds the view model - it needs the
 * pinning client factory and the credential store, neither of which the UI knows how to make - and
 * calls this.
 */
@Composable
fun PairingRoute(
    viewModel: PairingViewModel,
    onPaired: (ServerIdentity) -> Unit,
    modifier: Modifier = Modifier,
) {
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
        modifier = modifier,
    )
}
