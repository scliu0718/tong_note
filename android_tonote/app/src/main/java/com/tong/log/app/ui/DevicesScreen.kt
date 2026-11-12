package com.tong.log.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ArrowBack
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tong.log.app.data.model.DeviceRecord
import com.tong.log.app.data.net.BaiduPanClient
import com.tong.log.app.data.sync.DeviceRegistry
import com.tong.log.app.ui.theme.*
import kotlinx.coroutines.launch

@Composable
fun DevicesScreen(onBack: () -> Unit) {
    var devices by remember { mutableStateOf<List<DeviceRecord>>(emptyList()) }
    var loading by remember { mutableStateOf(true) }
    var error by remember { mutableStateOf<String?>(null) }
    var confirmKick by remember { mutableStateOf<DeviceRecord?>(null) }
    val scope = rememberCoroutineScope()

    fun load() {
        scope.launch {
            loading = true
            error = null
            try {
                val token = BaiduPanClient.validToken()
                devices = DeviceRegistry.listDevices(token)
            } catch (e: Exception) {
                error = e.message
            }
            loading = false
        }
    }

    LaunchedEffect(Unit) { load() }

    // 二级页面没有 Scaffold 自动处理系统栏，标题栏必须避开状态栏
    Column(Modifier.fillMaxSize().background(Bg).statusBarsPadding()) {
        Row(
            Modifier.fillMaxWidth().padding(horizontal = 8.dp, vertical = 6.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            IconButton(onClick = onBack) {
                Icon(Icons.Default.ArrowBack, contentDescription = "返回", tint = TextPrimary)
            }
            Text("登录设备", fontSize = 17.sp, fontWeight = FontWeight.Bold)
            Spacer(Modifier.weight(1f))
            IconButton(onClick = { load() }) {
                Icon(Icons.Default.Refresh, contentDescription = "刷新", tint = TextPrimary)
            }
        }

        if (loading) {
            Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                CircularProgressIndicator(color = Green)
            }
        } else if (error != null) {
            Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                Text(error ?: "加载失败", color = Red, fontSize = 13.sp)
            }
        } else {
            LazyColumn(Modifier.fillMaxSize().padding(horizontal = 16.dp)) {
                items(devices, key = { it.deviceId }) { d ->
                    val isSelf = d.deviceId == DeviceRegistry.identity.deviceId
                    Row(
                        Modifier
                            .fillMaxWidth()
                            .padding(vertical = 6.dp)
                            .clip(RoundedCornerShape(12.dp))
                            .background(Color.White)
                            .padding(14.dp),
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        Column(Modifier.weight(1f)) {
                            Text(d.deviceName.ifBlank { "未命名设备" }, fontSize = 14.sp, fontWeight = FontWeight.SemiBold)
                            Spacer(Modifier.height(3.dp))
                            Text(d.deviceId.take(8) + "…", fontSize = 11.sp, color = TextQuaternary)
                        }
                        when {
                            isSelf -> Text("本机", color = Green, fontSize = 12.sp, fontWeight = FontWeight.SemiBold)
                            d.kicked -> Text("已下线", color = TextQuaternary, fontSize = 12.sp)
                            else -> TextButton(onClick = { confirmKick = d }) {
                                Text("下线", color = Red, fontSize = 12.sp)
                            }
                        }
                    }
                }
            }
        }
    }

    confirmKick?.let { d ->
        AlertDialog(
            onDismissRequest = { confirmKick = null },
            title = { Text("让这台设备下线？") },
            text = { Text("「${d.deviceName}」下次打开或同步时会退出登录。已经写在那台设备上、还没同步的笔记会留在本地。") },
            confirmButton = {
                TextButton(onClick = {
                    scope.launch {
                        try {
                            val token = BaiduPanClient.validToken()
                            DeviceRegistry.kick(d.deviceId, token)
                        } catch (e: Exception) {
                        }
                        load()
                    }
                    confirmKick = null
                }) { Text("确认下线", color = Red) }
            },
            dismissButton = {
                TextButton(onClick = { confirmKick = null }) { Text("取消") }
            }
        )
    }
}
