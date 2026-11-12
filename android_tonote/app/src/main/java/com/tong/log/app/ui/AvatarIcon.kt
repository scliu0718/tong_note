package com.tong.log.app.ui

import androidx.compose.foundation.layout.size
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.MenuBook
import androidx.compose.material.icons.filled.Description
import androidx.compose.material.icons.filled.Eco
import androidx.compose.material.icons.filled.Edit
import androidx.compose.material.icons.filled.Favorite
import androidx.compose.material.icons.filled.NightsStay
import androidx.compose.material.icons.filled.Star
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.sp
import com.tong.log.app.data.model.AvatarPreset

/**
 * 头像内容：与 Mac 端 AvatarStore.presets 的 SF Symbol 一一对应的矢量图标，
 * 只有默认「同」保留汉字。调用方负责把本组件放进圆形渐变底并居中。
 *
 * leaf.fill → Eco / note.text → Description / book.closed.fill → MenuBook /
 * pencil → Edit / star.fill → Star / moon.stars.fill → NightsStay / heart.fill → Favorite
 */
@Composable
fun AvatarGlyph(preset: AvatarPreset, boxSize: Dp) {
    val vector = when (preset.id) {
        "leaf" -> Icons.Filled.Eco
        "note" -> Icons.Filled.Description
        "book" -> Icons.AutoMirrored.Filled.MenuBook
        "pen" -> Icons.Filled.Edit
        "star" -> Icons.Filled.Star
        "moon" -> Icons.Filled.NightsStay
        "heart" -> Icons.Filled.Favorite
        else -> null
    }
    if (vector != null) {
        Icon(
            imageVector = vector,
            contentDescription = preset.glyph,
            tint = Color.White,
            modifier = Modifier.size(boxSize * 0.5f)
        )
    } else {
        Text(
            preset.glyph,
            color = Color.White,
            fontSize = (boxSize.value * 0.38f).sp,
            fontWeight = FontWeight.Bold
        )
    }
}
