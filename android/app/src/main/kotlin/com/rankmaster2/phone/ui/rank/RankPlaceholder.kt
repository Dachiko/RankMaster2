package com.rankmaster2.phone.ui.rank

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import com.rankmaster2.phone.net.Snapshot
import com.rankmaster2.phone.store.ServerIdentity

/**
 * A holding screen, not the ranking screen.
 *
 * It exists so the flow can be walked end to end - pair, browse, open a folder - before the screen
 * that matters is built. It shows what the server said about the folder, which is enough to prove
 * the session really opened, and gets out of the way.
 */
@Composable
fun RankPlaceholder(
    snapshot: Snapshot,
    identity: ServerIdentity,
    onLeave: () -> Unit,
    modifier: Modifier = Modifier,
) {
    Column(
        modifier = modifier.fillMaxSize().background(Color.Black).padding(24.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp, Alignment.CenterVertically),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Text(snapshot.folderName, color = Color(0xFFECEEF2), textAlign = TextAlign.Center)
        Text(
            "${snapshot.counts.total} files — ${snapshot.counts.rankable} rankable, " +
                "${snapshot.counts.unranked} never ranked",
            color = Color(0xFF9AA3B2),
            textAlign = TextAlign.Center,
        )
        snapshot.pair?.let {
            Text("${it.left.id}   vs   ${it.right.id}", color = Color(0xFF9AA3B2), textAlign = TextAlign.Center)
        }
        Text(
            "The ranking screen is not built yet.",
            color = Color(0xFF606878),
            textAlign = TextAlign.Center,
        )
        TextButton(onClick = onLeave) { Text("Pick another folder", color = Color(0xFF2ECC71)) }
    }
}
