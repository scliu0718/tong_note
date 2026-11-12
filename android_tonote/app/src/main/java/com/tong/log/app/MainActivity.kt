package com.tong.log.app

import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.CheckCircle
import androidx.compose.material.icons.outlined.Description
import androidx.compose.material.icons.outlined.Person
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.lifecycleScope
import androidx.navigation.NavController
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.rememberNavController
import com.tong.log.app.data.model.AvatarPresets
import com.tong.log.app.data.model.QUICK_SYNC_NOTE_ID
import com.tong.log.app.data.net.BaiduPanConfig
import com.tong.log.app.data.store.AvatarStore
import com.tong.log.app.data.store.NoteStore
import com.tong.log.app.data.sync.SyncEngine
import com.tong.log.app.ui.*
import com.tong.log.app.ui.theme.*
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContent {
            TongNoteTheme { AppRoot() }
        }
        handleIntent(intent)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        handleIntent(intent)
    }

    private fun handleIntent(intent: Intent?) {
        val uri = intent?.data ?: return
        if (uri.scheme == "tongnote") {
            lifecycleScope.launch {
                val err = AuthState.handleCallback(uri)
                if (err != null) {
                    Toast.makeText(this@MainActivity, err, Toast.LENGTH_SHORT).show()
                }
            }
        }
    }
}

@Composable
private fun AppRoot() {
    val signedIn by AuthState.signedIn.collectAsState()
    val context = androidx.compose.ui.platform.LocalContext.current
    val scope = rememberCoroutineScope()
    var loginError by remember { mutableStateOf<String?>(null) }

    if (!signedIn) {
        LoginScreen(
            onLogin = {
                if (!BaiduPanConfig.isConfigured) {
                    loginError = "尚未配置百度网盘凭证（assets/baidupan.secret.json）"
                } else {
                    val url = AuthState.beginBrowserLogin()
                    try {
                        context.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(url)))
                    } catch (e: Exception) {
                        loginError = "无法打开浏览器"
                    }
                }
            },
            error = loginError
        )
        return
    }

    val navController = rememberNavController()
    NavHost(navController, startDestination = "main") {
        composable("main") { MainScaffold(navController) }
        composable("editor/{noteId}") { entry ->
            val noteId = entry.arguments?.getString("noteId") ?: ""
            EditorScreen(noteId) { navController.popBackStack() }
        }
        composable("sync") {
            SyncScreen(
                onBack = { navController.popBackStack() },
                onReauth = {
                    val url = AuthState.beginBrowserLogin()
                    context.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(url)))
                },
                onLogout = { scope.launch { AuthState.signOut() } }
            )
        }
        composable("trash") { TrashScreen(onBack = { navController.popBackStack() }) }
        composable("devices") { DevicesScreen(onBack = { navController.popBackStack() }) }
    }

    // 启动即同步 + 前台每 45 秒同步；顺带刷新当前百度账号，供「我的 / 同步与存储」展示
    LaunchedEffect(Unit) {
        if (BaiduPanConfig.isConfigured) {
            runCatching { AuthState.refreshAccount() }
            SyncEngine.syncRound()
        }
        while (true) {
            delay(45_000)
            if (BaiduPanConfig.isConfigured) SyncEngine.syncRound()
        }
    }
}

private data class NavTab(val label: String, val icon: ImageVector)

@Composable
private fun MainScaffold(navController: NavController) {
    var tab by remember { mutableStateOf(0) }
    var showAvatar by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()

    Scaffold(
        bottomBar = {
            // 选中态给整个选项（图标 + 文字）加浅绿底色，而不是只包住图标。
            // 图标统一为白底线框：未选中黑色线框，选中绿色线框。
            val tabs = listOf(
                NavTab("笔记", Icons.Outlined.Description),
                NavTab("任务", Icons.Outlined.CheckCircle),
                NavTab("我的", Icons.Outlined.Person)
            )
            NavigationBar(containerColor = Color(0xFFFAFAFB), tonalElevation = 0.dp) {
                Row(
                    Modifier
                        .fillMaxWidth()
                        .height(64.dp)
                        .padding(horizontal = 10.dp, vertical = 6.dp),
                    horizontalArrangement = Arrangement.spacedBy(6.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    tabs.forEachIndexed { index, item ->
                        val selected = tab == index
                        val line = if (selected) Green else TextPrimary
                        Box(
                            Modifier
                                .weight(1f)
                                .fillMaxHeight()
                                .clip(RoundedCornerShape(14.dp))
                                .background(if (selected) GreenTint else Color.Transparent)
                                .clickable { tab = index },
                            contentAlignment = Alignment.Center
                        ) {
                            Column(horizontalAlignment = Alignment.CenterHorizontally) {
                                Box(
                                    Modifier
                                        .size(27.dp)
                                        .background(Color.White, RoundedCornerShape(8.dp))
                                        .border(1.dp, line, RoundedCornerShape(8.dp)),
                                    contentAlignment = Alignment.Center
                                ) {
                                    Icon(
                                        item.icon,
                                        contentDescription = item.label,
                                        tint = line,
                                        modifier = Modifier.size(16.dp)
                                    )
                                }
                                Spacer(Modifier.height(3.dp))
                                Text(
                                    item.label,
                                    fontSize = 10.sp,
                                    color = if (selected) GreenHover else TextSecondary,
                                    fontWeight = if (selected) FontWeight.SemiBold else FontWeight.Normal
                                )
                            }
                        }
                    }
                }
            }
        }
    ) { padding ->
        Box(Modifier.fillMaxSize().padding(padding)) {
            when (tab) {
                0 -> NotesScreen(
                    onOpenNote = { navController.navigate("editor/$it") },
                    onOpenQuickSync = { navController.navigate("editor/$QUICK_SYNC_NOTE_ID") },
                    onOpenTodo = { tab = 1 },
                    onOpenMe = { tab = 2 },
                    onNewNote = { nb ->
                        val n = NoteStore.addNote(nb)
                        navController.navigate("editor/${n.id}")
                    }
                )
                1 -> TodoScreen(onSync = { scope.launch { SyncEngine.syncRound() } })
                2 -> MeScreen(
                    onOpenSync = { navController.navigate("sync") },
                    onOpenDevices = { navController.navigate("devices") },
                    onOpenTrash = { navController.navigate("trash") },
                    onOpenAvatar = { showAvatar = true },
                    onLogout = { scope.launch { AuthState.signOut() } }
                )
            }
        }
    }

    if (showAvatar) {
        AvatarPicker(onDismiss = { showAvatar = false })
    }
}

@Composable
private fun AvatarPicker(onDismiss: () -> Unit) {
    val currentId by AvatarStore.currentId.collectAsState()
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("选择头像", fontSize = 15.sp) },
        text = {
            LazyVerticalGrid(
                columns = GridCells.Fixed(4),
                modifier = Modifier.fillMaxWidth(),
                verticalArrangement = Arrangement.spacedBy(12.dp)
            ) {
                items(AvatarPresets.list) { preset ->
                    Column(horizontalAlignment = Alignment.CenterHorizontally) {
                        Box(
                            Modifier
                                .size(52.dp)
                                .background(
                                    Brush.linearGradient(listOf(Color(preset.color1), Color(preset.color2))),
                                    CircleShape
                                )
                                .clickable { AvatarStore.select(preset.id) },
                            contentAlignment = Alignment.Center
                        ) {
                            Text(preset.glyph, color = Color.White, fontSize = 20.sp, fontWeight = FontWeight.Bold)
                            if (currentId == preset.id) {
                                Text("✓", Modifier.align(Alignment.BottomEnd).padding(2.dp), color = Color.White, fontSize = 12.sp)
                            }
                        }
                    }
                }
            }
        },
        confirmButton = {
            TextButton(onClick = onDismiss) { Text("完成") }
        }
    )
}
