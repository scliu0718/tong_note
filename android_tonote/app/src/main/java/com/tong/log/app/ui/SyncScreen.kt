package com.tong.log.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ArrowBack
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tong.log.app.data.sync.SyncEngine
import com.tong.log.app.ui.theme.*
import kotlinx.coroutines.launch

@Composable
fun SyncScreen(onBack: () -> Unit, onReauth: () -> Unit, onLogout: () -> Unit) {
    val status by SyncEngine.status.collectAsState()
    val account by AuthState.account.collectAsState()
    val scope = rememberCoroutineScope()

    // 二级页面没有 Scaffold 自动处理系统栏，必须自己避开状态栏
    Column(Modifier.fillMaxSize().background(Bg).statusBarsPadding()) {
        Row(
            Modifier.fillMaxWidth().padding(horizontal = 8.dp, vertical = 6.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            IconButton(onClick = onBack) {
                Icon(Icons.Default.ArrowBack, contentDescription = "返回", tint = TextPrimary)
            }
            Text("同步与存储", fontSize = 17.sp, fontWeight = FontWeight.Bold)
        }

        Column(
            Modifier
                .fillMaxSize()
                .verticalScroll(rememberScrollState())
                .padding(horizontal = 16.dp)
        ) {
            Text("笔记数据存放于你自己的百度网盘，同笔记仅做读写，不另建服务器。", fontSize = 12.sp, color = TextTertiary)
            Spacer(Modifier.height(12.dp))

            // 同步状态卡
            SyncCard(title = "同步状态") {
                Row(Modifier.fillMaxWidth().padding(vertical = 10.dp), verticalAlignment = Alignment.CenterVertically) {
                    Column(Modifier.weight(1f)) {
                        Text("自动同步", fontSize = 13.sp, color = TextSecondary)
                        Text("联网时自动增量同步", fontSize = 10.sp, color = TextQuaternary)
                    }
                    Switch(checked = true, onCheckedChange = {}, colors = SwitchDefaults.colors(checkedTrackColor = Green))
                }
                Row(Modifier.fillMaxWidth().padding(vertical = 10.dp), verticalAlignment = Alignment.CenterVertically) {
                    Column(Modifier.weight(1f)) {
                        Text("最近同步", fontSize = 13.sp, color = TextSecondary)
                        Text(status.lastSyncDate?.let { displayTime(it) } ?: "尚未同步", fontSize = 10.sp, color = TextQuaternary)
                    }
                    Text(
                        when (status.state) {
                            "success" -> "已同步"
                            "syncing" -> "同步中"
                            "failed" -> "同步失败"
                            else -> "待同步"
                        },
                        color = when (status.state) {
                            "success" -> Green
                            "failed" -> Red
                            "syncing" -> Amber
                            else -> TextQuaternary
                        },
                        fontSize = 12.sp, fontWeight = FontWeight.SemiBold
                    )
                }
                Row(Modifier.fillMaxWidth().padding(vertical = 10.dp), verticalAlignment = Alignment.CenterVertically) {
                    Text("立即同步", Modifier.weight(1f), fontSize = 13.sp, color = TextSecondary)
                    Button(
                        onClick = { scope.launch { SyncEngine.syncRound() } },
                        enabled = !status.isSyncing,
                        colors = ButtonDefaults.buttonColors(containerColor = Green),
                        contentPadding = PaddingValues(horizontal = 16.dp, vertical = 6.dp)
                    ) {
                        Icon(Icons.Default.Refresh, contentDescription = null, modifier = Modifier.size(14.dp))
                        Spacer(Modifier.width(4.dp))
                        Text(if (status.isSyncing) "同步中…" else "同步全部", fontSize = 12.sp)
                    }
                }
                if (status.state == "failed") {
                    Text(status.lastMessage, color = Red, fontSize = 11.sp)
                }
            }

            // 目录结构卡
            SyncCard(title = "云端目录结构") {
                Column(
                    Modifier
                        .fillMaxWidth()
                        .background(Color(0xFFFAFAFB), RoundedCornerShape(10.dp))
                        .padding(13.dp)
                ) {
                    Text("/apps/tong_log/", fontFamily = FontFamily.Monospace, fontSize = 11.sp, color = BaiduDeep, fontWeight = FontWeight.SemiBold)
                    Text("  ├─ notes/      每篇笔记一个 JSON", fontFamily = FontFamily.Monospace, fontSize = 11.sp, color = TextSecondary)
                    Text("  ├─ todos/      每条任务一个 JSON", fontFamily = FontFamily.Monospace, fontSize = 11.sp, color = TextSecondary)
                    Text("  ├─ attachments/  图片附件", fontFamily = FontFamily.Monospace, fontSize = 11.sp, color = TextSecondary)
                    Text("  ├─ notebooks.json", fontFamily = FontFamily.Monospace, fontSize = 11.sp, color = TextSecondary)
                    Text("  ├─ devices/     登录设备", fontFamily = FontFamily.Monospace, fontSize = 11.sp, color = TextSecondary)
                    Text("  └─ avatar.json", fontFamily = FontFamily.Monospace, fontSize = 11.sp, color = TextSecondary)
                }
            }

            // 账号安全卡
            SyncCard(title = "账号与安全") {
                Row(Modifier.fillMaxWidth().padding(vertical = 10.dp), verticalAlignment = Alignment.CenterVertically) {
                    Column(Modifier.weight(1f)) {
                        Text("当前百度账号", fontSize = 13.sp, color = TextSecondary)
                        Text(
                            "百度网盘 OAuth 2.0 · 已授权",
                            fontSize = 10.sp,
                            color = TextQuaternary
                        )
                    }
                    Text(
                        account?.displayName?.takeIf { it.isNotBlank() } ?: "获取中…",
                        fontSize = 13.sp,
                        color = Green,
                        fontWeight = FontWeight.SemiBold
                    )
                }
                Row(Modifier.fillMaxWidth().padding(vertical = 10.dp), verticalAlignment = Alignment.CenterVertically) {
                    Column(Modifier.weight(1f)) {
                        Text("授权方式", fontSize = 13.sp, color = TextSecondary)
                        Text("百度网盘开放平台 OAuth 2.0", fontSize = 10.sp, color = TextQuaternary)
                    }
                    OutlinedButton(onClick = onReauth, contentPadding = PaddingValues(horizontal = 14.dp, vertical = 4.dp)) {
                        Text("重新授权", fontSize = 12.sp)
                    }
                }
                Row(Modifier.fillMaxWidth().padding(vertical = 10.dp), verticalAlignment = Alignment.CenterVertically) {
                    Text("退出登录", Modifier.weight(1f), fontSize = 13.sp, color = TextSecondary)
                    OutlinedButton(
                        onClick = onLogout,
                        colors = ButtonDefaults.outlinedButtonColors(contentColor = Red),
                        contentPadding = PaddingValues(horizontal = 14.dp, vertical = 4.dp)
                    ) {
                        Text("退出", fontSize = 12.sp)
                    }
                }
            }
            Spacer(Modifier.height(20.dp))
        }
    }
}

@Composable
private fun SyncCard(title: String, content: @Composable ColumnScope.() -> Unit) {
    Column(
        Modifier
            .fillMaxWidth()
            .padding(bottom = 12.dp)
            .background(Color.White, RoundedCornerShape(14.dp))
            .padding(16.dp)
    ) {
        Text(title, fontSize = 13.sp, fontWeight = FontWeight.Bold)
        Spacer(Modifier.height(6.dp))
        content()
    }
}
