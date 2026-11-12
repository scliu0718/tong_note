package com.tong.log.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
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
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.foundation.Canvas
import com.tong.log.app.ui.theme.*

/**
 * 登录 / 欢迎页。
 *
 * 文案与 Mac 端 LoginView.swift、Windows 端 LoginView.cs、UI 设计稿保持一致：
 * - 品牌副标题「记录 · 同步 · 属于你」
 * - 跨端说明列出 Mac / Windows / iOS / Android 四个平台
 * 品牌块按设计评审意见居中放置，作为首屏视觉焦点。
 */
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
        // 上半区：品牌 + 介绍，整体垂直居中
        Column(
            Modifier
                .fillMaxWidth()
                .weight(1f)
                .padding(horizontal = 28.dp, vertical = 24.dp),
            verticalArrangement = Arrangement.Center,
            horizontalAlignment = Alignment.CenterHorizontally
        ) {
            // 品牌标识（对应设计稿图 1）
            Row(verticalAlignment = Alignment.CenterVertically) {
                Box(
                    Modifier
                        .size(56.dp)
                        .background(Color.White.copy(alpha = 0.16f), RoundedCornerShape(15.dp))
                        .border(1.dp, Color.White.copy(alpha = 0.22f), RoundedCornerShape(15.dp)),
                    contentAlignment = Alignment.Center
                ) {
                    BrandPencilIcon()
                }
                Spacer(Modifier.width(14.dp))
                Column {
                    Text("同笔记", color = Color.White, fontSize = 24.sp, fontWeight = FontWeight.Bold)
                    Spacer(Modifier.height(3.dp))
                    Text("记录 · 同步 · 属于你", color = Color.White.copy(alpha = 0.82f), fontSize = 12.sp)
                }
            }

            Spacer(Modifier.height(38.dp))
            Text(
                "你的每一份记录，\n都交给你自己保管。",
                color = Color.White,
                fontSize = 27.sp,
                fontWeight = FontWeight.Bold,
                lineHeight = 36.sp,
                textAlign = TextAlign.Center
            )
            Spacer(Modifier.height(12.dp))
            Text(
                "不用订阅、不锁数据。笔记直接写入你自己的百度网盘，随时可导出、可迁移。",
                color = Color.White.copy(alpha = 0.92f),
                fontSize = 13.sp,
                lineHeight = 20.sp,
                textAlign = TextAlign.Center
            )
            Spacer(Modifier.height(26.dp))

            Column(Modifier.fillMaxWidth().widthIn(max = 300.dp)) {
                listOf(
                    "数据存储于百度网盘，跨 Mac / Windows / iOS / Android 同步",
                    "笔记、表格、任务和附件，格式开放可迁移",
                    "离线编辑，联网自动增量同步"
                ).forEach {
                    Row(verticalAlignment = Alignment.Top) {
                        Box(
                            Modifier
                                .padding(top = 1.dp)
                                .size(20.dp)
                                .background(Color.White.copy(alpha = 0.18f), CircleShape),
                            contentAlignment = Alignment.Center
                        ) { Text("✓", color = Color.White, fontSize = 12.sp) }
                        Spacer(Modifier.width(10.dp))
                        Text(
                            it,
                            color = Color.White.copy(alpha = 0.94f),
                            fontSize = 12.sp,
                            lineHeight = 19.sp
                        )
                    }
                    Spacer(Modifier.height(12.dp))
                }
            }
        }

        // 底部登录卡片
        Column(
            Modifier
                .fillMaxWidth()
                .background(Color.White, RoundedCornerShape(topStart = 24.dp, topEnd = 24.dp))
                .navigationBarsPadding()
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
                color = TextQuaternary, fontSize = 10.sp, lineHeight = 15.sp,
                textAlign = TextAlign.Center
            )
        }
    }
}

/**
 * 品牌图标：圆角方框 + 斜置的笔，对应 Mac 端 SF Symbol「square.and.pencil」
 * 与 UI 设计稿中的白色描边图标。用 Canvas 自绘，避免依赖图标库版本差异。
 */
@Composable
private fun BrandPencilIcon() {
    Canvas(Modifier.size(28.dp)) {
        val w = size.width
        val h = size.height
        val lineWidth = 1.9.dp.toPx()
        val stroke = Stroke(width = lineWidth, cap = StrokeCap.Round, join = StrokeJoin.Round)
        val white = Color.White

        // 方框：右上角留出笔的开口
        val box = Path().apply {
            moveTo(w * 0.60f, h * 0.20f)
            lineTo(w * 0.24f, h * 0.20f)
            lineTo(w * 0.16f, h * 0.28f)
            lineTo(w * 0.16f, h * 0.76f)
            lineTo(w * 0.24f, h * 0.84f)
            lineTo(w * 0.76f, h * 0.84f)
            lineTo(w * 0.84f, h * 0.76f)
            lineTo(w * 0.84f, h * 0.44f)
        }
        drawPath(box, white, style = stroke)

        // 笔身：从右上斜向左下
        val pen = Path().apply {
            moveTo(w * 0.98f, h * 0.04f)
            lineTo(w * 0.62f, h * 0.40f)
            lineTo(w * 0.52f, h * 0.50f)
            lineTo(w * 0.58f, h * 0.56f)
            lineTo(w * 0.68f, h * 0.46f)
            close()
        }
        drawPath(pen, white, style = stroke)
        drawLine(
            white,
            Offset(w * 0.62f, h * 0.40f),
            Offset(w * 0.74f, h * 0.28f),
            lineWidth * 0.85f,
            StrokeCap.Round
        )
    }
}
