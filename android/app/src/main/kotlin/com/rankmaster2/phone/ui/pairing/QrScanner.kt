package com.rankmaster2.phone.ui.pairing

import android.Manifest
import android.app.Activity
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.provider.Settings
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.camera.core.CameraSelector
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.ImageProxy
import androidx.camera.core.Preview
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Button
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalLifecycleOwner
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.content.ContextCompat
import com.google.mlkit.vision.barcode.BarcodeScanner
import com.google.mlkit.vision.barcode.BarcodeScannerOptions
import com.google.mlkit.vision.barcode.BarcodeScanning
import com.google.mlkit.vision.barcode.common.Barcode
import com.google.mlkit.vision.common.InputImage
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicReference

/**
 * The camera half of pairing: CameraX for the frames, ML Kit for the barcode, both on-device.
 *
 * Nothing here decides anything. It hands raw payload strings to [onPayload] and lets
 * [PairingViewModel] decide whether they mean anything - which is what keeps the pairing rules out
 * of a file that can only be exercised on a phone.
 *
 * **The permission path explains itself.** A bare system dialog for the camera, on an app the user
 * has just installed to control a photo ranker, is a dialog that gets denied - and a denied camera
 * permission on Android 11+ is permanent after the second refusal. So the reason comes first, in
 * this app's own words, and the system prompt only follows a deliberate tap.
 */
@Composable
fun QrScanner(
    active: Boolean,
    onPayload: (String) -> Unit,
    onEnterByHand: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val context = LocalContext.current
    var granted by remember { mutableStateOf(context.hasCameraPermission()) }
    var asked by remember { mutableStateOf(false) }

    val launcher = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.RequestPermission(),
    ) { result ->
        granted = result
        asked = true
    }

    when {
        granted -> CameraPreview(active = active, onPayload = onPayload, modifier = modifier)

        asked -> PermissionPanel(
            modifier = modifier,
            title = "The camera stayed off",
            body = "Android will not ask again from inside the app. Either turn the camera " +
                "permission on in Settings, or pair by typing the four values the PC shows " +
                "next to the QR code.",
            primaryLabel = "Open Settings",
            onPrimary = { context.openAppSettings() },
            secondaryLabel = "Type it instead",
            onSecondary = onEnterByHand,
        )

        else -> PermissionPanel(
            modifier = modifier,
            title = "The camera reads the pairing code",
            body = "The PC shows a QR code holding its address and the fingerprint of its " +
                "security certificate. The camera is used for that and nothing else: no photo " +
                "is saved, and nothing leaves the phone.",
            primaryLabel = "Use the camera",
            onPrimary = { launcher.launch(Manifest.permission.CAMERA) },
            secondaryLabel = "Type it instead",
            onSecondary = onEnterByHand,
        )
    }
}

@Composable
private fun PermissionPanel(
    title: String,
    body: String,
    primaryLabel: String,
    onPrimary: () -> Unit,
    secondaryLabel: String,
    onSecondary: () -> Unit,
    modifier: Modifier = Modifier,
) {
    Column(
        modifier = modifier.padding(24.dp),
        verticalArrangement = Arrangement.Center,
    ) {
        Heading(title)
        Spacer(Modifier.height(8.dp))
        Body(body)
        Spacer(Modifier.height(24.dp))
        Button(onClick = onPrimary, modifier = Modifier.fillMaxWidth()) { Text(primaryLabel) }
        Spacer(Modifier.height(4.dp))
        TextButton(onClick = onSecondary, modifier = Modifier.fillMaxWidth()) {
            Text(secondaryLabel)
        }
    }
}

@Composable
private fun CameraPreview(
    active: Boolean,
    onPayload: (String) -> Unit,
    modifier: Modifier = Modifier,
) {
    val context = LocalContext.current
    val lifecycleOwner = LocalLifecycleOwner.current
    val deliver by rememberUpdatedState(onPayload)

    val analysisExecutor = remember { Executors.newSingleThreadExecutor() }
    val scanner: BarcodeScanner = remember {
        BarcodeScanning.getClient(
            BarcodeScannerOptions.Builder()
                .setBarcodeFormats(Barcode.FORMAT_QR_CODE)
                .build()
        )
    }
    // One payload per scan session. Without this the analyser fires the same string thirty times a
    // second, and the second one arrives while the first is still pairing.
    val seen = remember { AtomicReference<String?>(null) }
    val isActive = rememberUpdatedState(active)

    LaunchedEffect(active) {
        if (active) seen.set(null)
    }

    val previewView = remember {
        PreviewView(context).apply {
            scaleType = PreviewView.ScaleType.FILL_CENTER
            implementationMode = PreviewView.ImplementationMode.COMPATIBLE
        }
    }

    DisposableEffect(lifecycleOwner) {
        val future = ProcessCameraProvider.getInstance(context)
        var provider: ProcessCameraProvider? = null
        // The provider arrives asynchronously and the screen can be gone before it does - pairing
        // succeeds and the folder list replaces this in well under the time the camera takes to
        // start on a cold app. Without this flag that late callback binds a camera nothing is
        // watching, and `onDispose` has already run with nothing to unbind: the camera stays on,
        // with its indicator lit, until the process ends.
        var disposed = false

        future.addListener({
            // `get()` throws on a device with no camera service, or one that is already claimed.
            // That is a screen that says "type it instead", not a crash on a background thread.
            val cameraProvider = runCatching { future.get() }.getOrNull() ?: return@addListener
            provider = cameraProvider
            if (disposed) {
                cameraProvider.unbindAll()
                return@addListener
            }

            val preview = Preview.Builder().build()
            preview.setSurfaceProvider(previewView.surfaceProvider)

            val analysis = ImageAnalysis.Builder()
                .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST)
                .build()
            analysis.setAnalyzer(analysisExecutor) { image ->
                scan(image, scanner) { payload ->
                    if (isActive.value && seen.getAndSet(payload) != payload) {
                        ContextCompat.getMainExecutor(context).execute { deliver(payload) }
                    }
                }
            }

            runCatching {
                cameraProvider.unbindAll()
                cameraProvider.bindToLifecycle(
                    lifecycleOwner,
                    CameraSelector.DEFAULT_BACK_CAMERA,
                    preview,
                    analysis,
                )
            }
        }, ContextCompat.getMainExecutor(context))

        onDispose {
            disposed = true
            provider?.unbindAll()
            scanner.close()
            analysisExecutor.shutdown()
        }
    }

    Box(modifier = modifier) {
        AndroidView(factory = { previewView }, modifier = Modifier.fillMaxSize())
    }
}

@androidx.annotation.OptIn(androidx.camera.core.ExperimentalGetImage::class)
private fun scan(image: ImageProxy, scanner: BarcodeScanner, onFound: (String) -> Unit) {
    val media = image.image
    if (media == null) {
        image.close()
        return
    }
    val input = InputImage.fromMediaImage(media, image.imageInfo.rotationDegrees)
    // The scanner is closed when the screen goes, and a frame already in flight then meets a
    // detector that is gone. That throws on the analysis thread, where nothing is catching it.
    val scanning = runCatching { scanner.process(input) }.getOrElse {
        image.close()
        return
    }
    scanning
        .addOnSuccessListener { barcodes ->
            barcodes.firstNotNullOfOrNull { it.rawValue }?.let(onFound)
        }
        .addOnCompleteListener { image.close() }
}

private fun Context.hasCameraPermission(): Boolean =
    ContextCompat.checkSelfPermission(this, Manifest.permission.CAMERA) ==
        PackageManager.PERMISSION_GRANTED

private fun Context.openAppSettings() {
    val intent = Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS).apply {
        data = Uri.fromParts("package", packageName, null)
        if (this@openAppSettings !is Activity) addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
    }
    runCatching { startActivity(intent) }
}
