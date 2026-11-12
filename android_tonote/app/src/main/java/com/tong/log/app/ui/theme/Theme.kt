package com.tong.log.app.ui.theme

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Typography
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.sp

// 与 Mac 端 / 移动端设计稿一致的设计令牌（浅色）
val Green = Color(0xFF1A9E5C)
val GreenHover = Color(0xFF147A46)
val GreenTint = Color(0xFFE6F4EC)
val GreenTint2 = Color(0xFFCFE9D9)
val BaiduBlue = Color(0xFF06A7FF)
val BaiduDeep = Color(0xFF0084D6)
val BaiduTint = Color(0xFFE5F5FF)
val Bg = Color(0xFFF5F5F7)
val SidebarBg = Color(0xFFECECEF)
val Panel = Color(0xFFFFFFFF)
val TextPrimary = Color(0xFF1D1D1F)
val TextSecondary = Color(0xFF48484A)
val TextTertiary = Color(0xFF86868B)
val TextQuaternary = Color(0xFFA1A1A6)
val Line = Color(0xFFE0E0E4)
val LineSoft = Color(0xFFECECEF)
val Red = Color(0xFFE5484D)
val RedTint = Color(0xFFFCEBEB)
val Amber = Color(0xFFF5A623)
val AmberTint = Color(0xFFFDF1DC)
val CardSelected = Color(0xFFF4FAF6)

private val LightColors = lightColorScheme(
    primary = Green,
    onPrimary = Color.White,
    primaryContainer = GreenTint,
    onPrimaryContainer = GreenHover,
    secondary = BaiduBlue,
    onSecondary = Color.White,
    background = Bg,
    onBackground = TextPrimary,
    surface = Panel,
    onSurface = TextPrimary,
    surfaceVariant = SidebarBg,
    onSurfaceVariant = TextSecondary,
    error = Red,
    outline = Line,
    outlineVariant = LineSoft
)

private val AppTypography = Typography(
    titleLarge = TextStyle(fontSize = 20.sp, fontWeight = FontWeight.Bold),
    titleMedium = TextStyle(fontSize = 17.sp, fontWeight = FontWeight.Bold),
    bodyLarge = TextStyle(fontSize = 15.sp),
    bodyMedium = TextStyle(fontSize = 14.sp),
    bodySmall = TextStyle(fontSize = 12.sp),
    labelSmall = TextStyle(fontSize = 10.sp)
)

@Composable
fun TongNoteTheme(content: @Composable () -> Unit) {
    // 当前仅浅色主题，与设计稿保持一致
    MaterialTheme(
        colorScheme = LightColors,
        typography = AppTypography,
        content = content
    )
}
