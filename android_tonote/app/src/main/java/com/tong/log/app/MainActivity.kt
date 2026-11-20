package com.tong.log.app

import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.foundation.background
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
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalDensity
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
import com.tong.log.app.data.store.TodoStore
import com.tong.log.app.data.store.isOverdue
import com.tong.log.app.data.sync.SyncEngine
import com.tong.log.app.data.sync.SyncScope
import com.tong.log.app.data.net.BaiduPanTokenStore
import com.tong.log.app.ui.*
import com.tong.log.app.ui.theme.*
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        // 显式开启边到边（decorFitsSystemWindows=false）。
        // 只有这样才能拿到 WindowInsets.ime：编辑器弹键盘时靠它把整页上推，
        // 否则 IME inset 会被 decor view 吃掉、Compose 侧恒为 0，键盘直接盖住正文。
        enableEdgeToEdge()
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
                } else {
                    // 授权成功立即拉一轮云端数据，不要让用户等 45 秒轮询；
                    // 首次登录与「重新授权」都走这里。syncRound 自带并发排队。
                    SyncEngine.syncRound()
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

    // 退到后台：把没传的笔记和任务都传上去
    val lifecycleOwner = androidx.compose.ui.platform.LocalLifecycleOwner.current
    DisposableEffect(lifecycleOwner) {
        val observer = androidx.lifecycle.LifecycleEventObserver { _, event ->
            if (event == androidx.lifecycle.Lifecycle.Event.ON_STOP && BaiduPanTokenStore.current() != null) {
                scope.launch {
                    SyncEngine.flushNotes()
                    SyncEngine.request(SyncScope.DirtyTodos, priority = false)
                }
            }
        }
        lifecycleOwner.lifecycle.addObserver(observer)
        onDispose { lifecycleOwner.lifecycle.removeObserver(observer) }
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
        composable("guide") { GuideScreen(onBack = { navController.popBackStack() }) }
    }

    // 启动即同步 + 前台每 45 秒同步；每轮都顺带刷新账号，
    // 避免首次网络未就绪时「当前百度账号」一直停在「获取中…」
    LaunchedEffect(Unit) {
        while (true) {
            if (BaiduPanConfig.isConfigured) {
                runCatching { AuthState.refreshAccount() }
                SyncEngine.syncRound()
            }
            // 同步失败（含部分篇目失败）时 10 秒后重试，避免「登录空数据」要干等 45 秒
            val failed = SyncEngine.status.value.state == "failed"
            delay(if (failed) 10_000L else 45_000L)
        }
    }
}

private data class NavTab(val label: String, val icon: ImageVector)

@Composable
private fun MainScaffold(navController: NavController) {
    // rememberSaveable：从二级页（使用说明 / 回收站 / 同步与存储…）返回时停留在原来的底栏页，而不是回到「笔记」
    var tab by rememberSaveable { mutableStateOf(0) }
    var showAvatar by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()
    // 切页：点开「笔记」/「任务」先拉远端（插队）；离开「任务」时把没传的任务同步到远端
    var prevTab by remember { mutableStateOf(-1) }
    LaunchedEffect(tab) {
        val from = prevTab
        prevTab = tab
        if (from == -1) return@LaunchedEffect // 刚进主页，启动全量同步已覆盖
        if (from == 1 && tab != 1) SyncEngine.request(SyncScope.DirtyTodos, priority = false)
        if (tab == 0) SyncEngine.request(SyncScope.Notes)
        if (tab == 1) SyncEngine.request(SyncScope.Todos)
    }
    // 底栏「任务」角标：逾期任务数；collect todos 触发重组，否则 overdueCount 不刷新
    val todos by TodoStore.todos.collectAsState()
    val overdueCount = todos.count { !it.purged && it.isOverdue }

    // 键盘弹起时隐藏底栏：否则 Scaffold 仍会给 content 留出底栏高度，
    // 与页面自身的 imePadding 叠加，输入框被顶得过高且底栏挡在键盘上方。
    val imeBottom = WindowInsets.ime.getBottom(LocalDensity.current)
    Scaffold(
        bottomBar = {
            if (imeBottom == 0) {
            // 自绘底栏：M3 NavigationBar 自带 80dp 最小高度 + 内部留白，会在选中行上下多出白边。
            // 这里直接画 64dp 内容行 + 系统导航栏 inset（边到边后必须消费，否则内容顶到手势条下），背景同色不留白
            val tabs = listOf(
                NavTab("笔记", Icons.Outlined.Description),
                NavTab("任务", Icons.Outlined.CheckCircle),
                NavTab("我的", Icons.Outlined.Person)
            )
            Column(
                Modifier
                    .fillMaxWidth()
                    .background(Color(0xFFFAFAFB))
                    .navigationBarsPadding()
            ) {
                HorizontalDivider(color = LineSoft)
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
                                Box {
                                    // 不要图标外的方框，线框图标直接放大显示
                                    Icon(
                                        item.icon,
                                        contentDescription = item.label,
                                        tint = line,
                                        modifier = Modifier.size(28.dp)
                                    )
                                    // 「任务」tab 右上角逾期数量角标
                                    if (index == 1) {
                                        OverdueCountBadge(
                                            overdueCount,
                                            Modifier.align(Alignment.TopEnd).offset(x = 7.dp, y = (-5).dp)
                                        )
                                    }
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
                1 -> TodoScreen(onSync = { scope.launch { SyncEngine.request(SyncScope.Todos) } })
                2 -> MeScreen(
                    onOpenSync = { navController.navigate("sync") },
                    onOpenDevices = { navController.navigate("devices") },
                    onOpenTrash = { navController.navigate("trash") },
                    onOpenAvatar = { showAvatar = true },
                    onOpenGuide = { navController.navigate("guide") },
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
                                // 不要按压涟漪：默认 indication 会按 Box 的方形 bounds 画阴影，很突兀
                                .clickable(interactionSource = null, indication = null) { AvatarStore.select(preset.id) },
                            contentAlignment = Alignment.Center
                        ) {
                            AvatarGlyph(preset, 52.dp)
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
