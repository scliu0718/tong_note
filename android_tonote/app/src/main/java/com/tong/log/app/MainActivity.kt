package com.tong.log.app

import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.Description
import androidx.compose.material.icons.filled.Person
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
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
import com.tong.log.app.ui.theme.TongNoteTheme
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

    // 启动即同步 + 前台每 45 秒同步
    LaunchedEffect(Unit) {
        if (BaiduPanConfig.isConfigured) SyncEngine.syncRound()
        while (true) {
            delay(45_000)
            if (BaiduPanConfig.isConfigured) SyncEngine.syncRound()
        }
    }
}

@Composable
private fun MainScaffold(navController: NavController) {
    var tab by remember { mutableStateOf(0) }
    var showAvatar by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()

    Scaffold(
        bottomBar = {
            NavigationBar(containerColor = Color(0xFFFAFAFB)) {
                NavigationBarItem(
                    selected = tab == 0, onClick = { tab = 0 },
                    icon = { Icon(Icons.Default.Description, contentDescription = null) },
                    label = { Text("笔记", fontSize = 10.sp) }
                )
                NavigationBarItem(
                    selected = tab == 1, onClick = { tab = 1 },
                    icon = { Icon(Icons.Default.CheckCircle, contentDescription = null) },
                    label = { Text("任务", fontSize = 10.sp) }
                )
                NavigationBarItem(
                    selected = tab == 2, onClick = { tab = 2 },
                    icon = { Icon(Icons.Default.Person, contentDescription = null) },
                    label = { Text("我的", fontSize = 10.sp) }
                )
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
