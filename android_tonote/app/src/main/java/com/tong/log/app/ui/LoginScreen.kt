package com.tong.log.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tong.log.app.ui.theme.*

@Composable
fun LoginScreen(onLogin: () -> Unit, error: String?) {
    Column(
        Modifier
            .fillMaxSize()
            .background(
                Brush.verticalGradient(
                    listOf(Color(0xFF17C26B), Color(0xFF0F9D52), Color(0xFF0C7A40))
                )
            )
    ) {
        Column(
            Modifier
                .fillMaxWidth()
                .weight(1f)
                .padding(28.dp)
        ) {
            Spacer(Modifier.height(24.dp))
            Row(verticalAlignment = Alignment.CenterVertically) {
                Box(
                    Modifier
                        .size(48.dp)
                        .background(Color.White.copy(alpha = 0.16f), RoundedCornerShape(13.dp)),
                    contentAlignment = Alignment.Center
                ) {
                    Text("同", color = Color.White, fontSize = 22.sp, fontWeight = FontWeight.Bold)
                }
                Spacer(Modifier.width(12.dp))
                Column {
                    Text("同笔记", color = Color.White, fontSize = 22.sp, fontWeight = FontWeight.Bold)
                    Text("工作日志 · 笔记", color = Color.White.copy(alpha = 0.8f), fontSize = 12.sp)
                }
            }
            Spacer(Modifier.height(48.dp))
            Text(
                "你的每一份记录，\n都交给你自己保管。",
                color = Color.White, fontSize = 30.sp, fontWeight = FontWeight.Bold, lineHeight = 38.sp
            )
            Spacer(Modifier.height(14.dp))
            Text(
                "不用订阅、不锁数据。笔记直接写入你自己的百度网盘，随时可导出、可迁移。",
                color = Color.White.copy(alpha = 0.92f), fontSize = 13.sp, lineHeight = 20.sp
            )
            Spacer(Modifier.height(26.dp))
            listOf(
                "数据存于百度网盘，Mac / 手机跨端同步",
                "富文本笔记 + 图片附件，格式开放可迁移",
                "离线编辑，联网自动增量同步"
            ).forEach {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Box(
                        Modifier
                            .size(20.dp)
                            .background(Color.White.copy(alpha = 0.18f), CircleShape),
                        contentAlignment = Alignment.Center
                    ) { Text("✓", color = Color.White, fontSize = 12.sp) }
                    Spacer(Modifier.width(10.dp))
                    Text(it, color = Color.White.copy(alpha = 0.94f), fontSize = 12.sp)
                }
                Spacer(Modifier.height(12.dp))
            }
        }
        // 底部登录卡片
        Column(
            Modifier
                .fillMaxWidth()
                .background(Color.White, RoundedCornerShape(topStart = 24.dp, topEnd = 24.dp))
                .padding(horizontal = 24.dp, vertical = 22.dp)
        ) {
            Text("欢迎使用同笔记", Modifier.align(Alignment.CenterHorizontally), fontSize = 16.sp, fontWeight = FontWeight.Bold)
            Spacer(Modifier.height(6.dp))
            Text(
                "使用百度网盘账号登录，笔记将存储于你的网盘",
                Modifier.align(Alignment.CenterHorizontally), fontSize = 12.sp, color = TextTertiary
            )
            Spacer(Modifier.height(18.dp))
            Button(
                onClick = onLogin,
                modifier = Modifier.fillMaxWidth().height(50.dp),
                colors = ButtonDefaults.buttonColors(containerColor = BaiduBlue),
                shape = RoundedCornerShape(12.dp)
            ) {
                Text("使用百度网盘账号登录", fontSize = 15.sp, fontWeight = FontWeight.SemiBold)
            }
            Spacer(Modifier.height(12.dp))
            OutlinedButton(
                onClick = onLogin,
                modifier = Modifier.fillMaxWidth().height(50.dp),
                colors = ButtonDefaults.outlinedButtonColors(contentColor = BaiduDeep),
                shape = RoundedCornerShape(12.dp)
            ) {
                Text("扫码授权登录", fontSize = 15.sp, fontWeight = FontWeight.SemiBold)
            }
            if (error != null) {
                Spacer(Modifier.height(12.dp))
                Text(error, Modifier.fillMaxWidth(), color = Red, fontSize = 12.sp)
            }
            Spacer(Modifier.height(14.dp))
            Text(
                "登录即授权同笔记通过百度网盘开放平台读写 /apps/tong_log 目录。授权信息仅保存在本机。",
                Modifier.align(Alignment.CenterHorizontally),
                color = TextQuaternary, fontSize = 10.sp, lineHeight = 15.sp
            )
        }
    }
}
