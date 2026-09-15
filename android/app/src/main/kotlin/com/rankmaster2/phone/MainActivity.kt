package com.rankmaster2.phone

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color

/// The one activity. Every screen is a composable inside it; there is no navigation library and no
/// second activity, because the app is a single flow: connect, pick a folder, rank.
class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        setContent { Rm2Theme { Skeleton() } }
    }
}

@Composable
fun Rm2Theme(content: @Composable () -> Unit) {
    // Fixed dark. A photograph judged against a light surround is a photograph judged wrong.
    MaterialTheme(
        colorScheme = darkColorScheme(
            background = Color.Black,
            surface = Color.Black,
            onBackground = Color(0xFFECEEF2),
            onSurface = Color(0xFFECEEF2),
        ),
        content = content,
    )
}

@Composable
private fun Skeleton() {
    Box(
        modifier = Modifier.fillMaxSize().background(Color.Black),
        contentAlignment = Alignment.Center,
    ) {
        Text("Rank Master 2", color = Color(0xFFECEEF2))
    }
}
