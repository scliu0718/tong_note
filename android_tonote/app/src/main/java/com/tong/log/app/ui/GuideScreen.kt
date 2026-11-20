package com.tong.log.app.ui

import android.annotation.SuppressLint
import android.webkit.WebView
import android.webkit.WebViewClient
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import com.tong.log.app.ui.theme.*

/** 使用说明 H5：随包放在 assets/guide/index.html，离线可看，不依赖线上服务。 */
private const val GUIDE_URL = "file:///android_asset/guide/index.html"

@SuppressLint("SetJavaScriptEnabled")
@Composable
fun GuideScreen(onBack: () -> Unit) {
    BackHandler(onBack = onBack)

    // 二级页面没有 Scaffold 自动处理系统栏，标题栏必须避开状态栏
    Column(Modifier.fillMaxSize().background(Bg).statusBarsPadding()) {
        Row(
            Modifier.fillMaxWidth().padding(horizontal = 8.dp, vertical = 6.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            IconButton(onClick = onBack) {
                Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "返回", tint = TextPrimary)
            }
            Text("使用说明", fontSize = 17.sp, fontWeight = FontWeight.Bold)
        }
        TopBarDivider()
        AndroidView(
            modifier = Modifier.fillMaxSize().navigationBarsPadding(),
            factory = { ctx ->
                WebView(ctx).apply {
                    setBackgroundColor(android.graphics.Color.TRANSPARENT)
                    // 页内只有锚点跳转，不需要脚本；android_asset 不受 allowFileAccess 影响，
                    // 所以文件访问全部关掉，页面只能读本包 assets
                    settings.javaScriptEnabled = false
                    settings.allowFileAccess = false
                    settings.allowContentAccess = false
                    webViewClient = WebViewClient()
                    loadUrl(GUIDE_URL)
                }
            },
            onRelease = { it.destroy() }
        )
    }
}
