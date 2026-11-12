package com.tong.log.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ChevronRight
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Devices
import androidx.compose.material.icons.filled.Info
import androidx.compose.material.icons.filled.Sync
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
import com.tong.log.app.data.model.AvatarPresets
import com.tong.log.app.data.store.AvatarStore
import com.tong.log.app.data.store.NoteStore
import com.tong.log.app.data.sync.SyncEngine
import com.tong.log.app.ui.theme.*

@Composable
fun MeScreen(
    onOpenSync: () -> Unit,
    onOpenDevices: () -> Unit,
    onOpenTrash: () -> Unit,
    onOpenAvatar: () -> Unit,
    onLogout: () -> Unit
) {
    val avatarId by AvatarStore.currentId.collectAsState()
    val avatar = AvatarPresets.byId(avatarId)
    val syncStatus by SyncEngine.status.collectAsState()
    val account by AuthState.account.collectAsState()
    val accountName = account?.displayName?.takeIf { it.isNotBlank() } ?: "正在获取百度账号…"
    val trashCount = NoteStore.trashNotes.size

    Column(Modifier.fillMaxSize().background(Bg)) {
        Row(
            Modifier.fillMaxWidth().padding(horizontal = 14.dp, vertical = 10.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Text("我的", fontSize = 17.sp, fontWeight = FontWeight.Bold)
            Spacer(Modifier.weight(1f))
        }

        LazyColumn(Modifier.fillMaxSize().padding(horizontal = 16.dp)) {
            item {
                // 账号卡
                Row(
                    Modifier
                        .fillMaxWidth()
                        .clip(RoundedCornerShape(16.dp))
                        .background(Color.White)
                        .clickable(onClick = onOpenAvatar)
                        .padding(16.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Box(
                        Modifier
                            .size(56.dp)
                            .background(
                                Brush.linearGradient(
                                    listOf(Color(avatar.color1), Color(avatar.color2))
                                ),
                                CircleShape
                            ),
                        contentAlignment = Alignment.Center
                    ) {
                        AvatarGlyph(avatar, 56.dp)
                    }
                    Spacer(Modifier.width(14.dp))
                    Column {
                        Text("当前百度账号", fontSize = 11.sp, color = TextQuaternary)
                        Spacer(Modifier.height(2.dp))
                        Text(accountName, fontSize = 15.sp, fontWeight = FontWeight.Bold, color = TextPrimary)
                        Spacer(Modifier.height(3.dp))
                        Text(
                            if (account == null) "百度网盘账号已授权" else "百度网盘 · 已授权 · uk ${account?.uk ?: "—"}",
                            fontSize = 11.sp,
                            color = TextTertiary
                        )
                    }
                    Spacer(Modifier.weight(1f))
                    Text(
                        if (syncStatus.state == "success") "已同步" else "待同步",
                        fontSize = 11.sp, color = if (syncStatus.state == "success") Green else TextQuaternary
                    )
                }
            }
            item { Spacer(Modifier.height(14.dp)) }
            item {
                // 存储卡
                Column(
                    Modifier
                        .fillMaxWidth()
                        .clip(RoundedCornerShape(16.dp))
                        .background(Color.White)
                        .padding(16.dp)
                ) {
                    Text("2.4 GB", fontSize = 22.sp, fontWeight = FontWeight.Bold)
                    Text(" / 2 TB", fontSize = 11.sp, color = TextQuaternary)
                    Spacer(Modifier.height(11.dp))
                    Box(Modifier.fillMaxWidth().height(6.dp).background(Color(0xFFE5E5E8), RoundedCornerShape(3.dp))) {
                        Box(Modifier.fillMaxWidth(0.12f).fillMaxHeight().background(BaiduBlue, RoundedCornerShape(3.dp)))
                    }
                    Spacer(Modifier.height(8.dp))
                    Text("百度网盘 · 以网盘用量为准", fontSize = 11.sp, color = TextQuaternary)
                }
            }
            item { Spacer(Modifier.height(14.dp)) }
            item {
                Column(
                    Modifier
                        .fillMaxWidth()
                        .clip(RoundedCornerShape(16.dp))
                        .background(Color.White)
                ) {
                    MeItem(Icons.Default.Sync, "同步与存储", GreenTint, Green, "刚刚") { onOpenSync() }
                    HorizontalDivider(color = LineSoft)
                    MeItem(Icons.Default.Devices, "登录设备", BaiduTint, BaiduDeep, "") { onOpenDevices() }
                    HorizontalDivider(color = LineSoft)
                    MeItem(Icons.Default.Delete, "回收站", Color(0xFFF0F0F2), TextTertiary, if (trashCount > 0) "$trashCount" else "") { onOpenTrash() }
                    HorizontalDivider(color = LineSoft)
                    MeItem(Icons.Default.Info, "关于与帮助", Color(0xFFF0F0F2), TextTertiary, "") {}
                }
            }
            item { Spacer(Modifier.height(14.dp)) }
            item {
                Column(
                    Modifier
                        .fillMaxWidth()
                        .clip(RoundedCornerShape(16.dp))
                        .background(Color.White)
                        .clickable(onClick = onLogout)
                        .padding(14.dp)
                ) {
                    Text("退出登录", Modifier.fillMaxWidth(), color = Red, fontSize = 14.sp, fontWeight = FontWeight.SemiBold, textAlign = androidx.compose.ui.text.style.TextAlign.Center)
                }
            }
            item { Spacer(Modifier.height(14.dp)) }
            item {
                Text(
                    "同笔记 · 数据属于你自己\n无需订阅 · 无服务器存储",
                    Modifier.fillMaxWidth(),
                    color = TextQuaternary, fontSize = 10.sp, textAlign = androidx.compose.ui.text.style.TextAlign.Center,
                    lineHeight = 15.sp
                )
                Spacer(Modifier.height(16.dp))
            }
        }
    }
}

@Composable
private fun MeItem(
    icon: ImageVector, label: String, iconBg: Color, iconTint: Color, trailing: String,
    onClick: () -> Unit
) {
    Row(
        Modifier
            .fillMaxWidth()
            .clickable(onClick = onClick)
            .padding(horizontal = 16.dp, vertical = 14.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Box(
            Modifier.size(30.dp).background(iconBg, RoundedCornerShape(8.dp)),
            contentAlignment = Alignment.Center
        ) {
            Icon(icon, contentDescription = label, tint = iconTint, modifier = Modifier.size(17.dp))
        }
        Spacer(Modifier.width(12.dp))
        Text(label, Modifier.weight(1f), fontSize = 13.sp, color = TextPrimary)
        if (trailing.isNotEmpty()) {
            Text(
                trailing,
                Modifier.clip(RoundedCornerShape(999.dp)).background(GreenTint).padding(horizontal = 8.dp, vertical = 1.dp),
                color = GreenHover, fontSize = 10.sp, fontWeight = FontWeight.SemiBold
            )
            Spacer(Modifier.width(4.dp))
        }
        Icon(Icons.Default.ChevronRight, contentDescription = null, tint = TextQuaternary, modifier = Modifier.size(15.dp))
    }
}
