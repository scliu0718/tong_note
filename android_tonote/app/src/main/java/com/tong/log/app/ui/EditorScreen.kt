package com.tong.log.app.ui

import android.annotation.SuppressLint
import android.webkit.WebView
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ArrowBack
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import com.tong.log.app.data.codec.MarkdownConverter
import com.tong.log.app.data.codec.RichTextTableCodec
import com.tong.log.app.data.model.Note
import com.tong.log.app.data.model.NoteContentFormat
import com.tong.log.app.data.store.AppStorage
import com.tong.log.app.data.store.AttachmentStore
import com.tong.log.app.data.store.NoteStore
import com.tong.log.app.data.sync.SyncEngine
import com.tong.log.app.ui.theme.*
import kotlinx.coroutines.launch
import org.json.JSONObject

private val BASE_URL get() = "file://${AppStorage.rootDir.absolutePath}/"

/** 富文本编辑器：WebView + contenteditable，正文即受控 HTML，与 Mac 端格式互通。 */
@SuppressLint("SetJavaScriptEnabled")
@Composable
fun EditorScreen(noteId: String, onBack: () -> Unit) {
    val note = NoteStore.note(noteId)
    if (note == null) {
        LaunchedEffect(Unit) { onBack() }
        return
    }
    var title by remember { mutableStateOf(note.title) }
    var showStyleMenu by remember { mutableStateOf(false) }
    var showColorMenu by remember { mutableStateOf(false) }
    var showHighlightMenu by remember { mutableStateOf(false) }
    var showLinkDialog by remember { mutableStateOf(false) }
    var showTableDialog by remember { mutableStateOf(false) }
    var showMore by remember { mutableStateOf(false) }
    var webView by remember { mutableStateOf<WebView?>(null) }
    val scope = rememberCoroutineScope()

    fun saveBody() {
        val wv = webView ?: return
        wv.evaluateJavascript("document.body.innerHTML") { v ->
            val html = jsString(v).replace(BASE_URL, "")
            NoteStore.updateContent(noteId, html, NoteContentFormat.RICH_TEXT)
        }
    }

    fun exec(cmd: String, value: String? = null) {
        val js = if (value == null) "document.execCommand('$cmd');"
        else "document.execCommand('$cmd', false, '$value');"
        webView?.evaluateJavascript(js, null)
        saveBody()
    }

    val pickImage = rememberLauncherForActivityResult(ActivityResultContracts.GetContent()) { uri ->
        uri?.let {
            val rel = AttachmentStore.importImage(it, noteId)
            rel?.let { r ->
                webView?.evaluateJavascript("document.execCommand('insertImage', false, '$r');", null)
                saveBody()
            }
        }
    }

    Column(Modifier.fillMaxSize().background(Color.White)) {
        // 顶部导航
        Row(
            Modifier.fillMaxWidth().padding(horizontal = 8.dp, vertical = 6.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            IconButton(onClick = { saveBody(); onBack() }) {
                Icon(Icons.Default.ArrowBack, contentDescription = "返回", tint = TextPrimary)
            }
            Text("编辑笔记", fontSize = 17.sp, fontWeight = FontWeight.Bold)
            Spacer(Modifier.weight(1f))
            IconButton(onClick = { showMore = true }) {
                Icon(Icons.Default.MoreVert, contentDescription = "更多", tint = TextPrimary)
            }
        }

        // 标题
        androidx.compose.material3.TextField(
            value = title,
            onValueChange = { title = it; NoteStore.updateTitle(noteId, it) },
            modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp),
            placeholder = { Text("笔记标题", fontSize = 21.sp) },
            textStyle = androidx.compose.ui.text.TextStyle(fontSize = 21.sp, fontWeight = FontWeight.Bold),
            colors = TextFieldDefaults.colors(
                focusedContainerColor = Color.Transparent,
                unfocusedContainerColor = Color.Transparent,
                focusedIndicatorColor = Color.Transparent,
                unfocusedIndicatorColor = Color.Transparent
            )
        )

        // 元信息
        Row(Modifier.padding(horizontal = 16.dp, vertical = 2.dp), verticalAlignment = Alignment.CenterVertically) {
            Text(note.notebook, Modifier.clip(RoundedCornerShape(6.dp)).background(GreenTint).padding(horizontal = 8.dp, vertical = 2.dp), color = GreenHover, fontSize = 11.sp)
            note.tags.take(2).forEach {
                Spacer(Modifier.width(6.dp))
                Text(it, Modifier.clip(RoundedCornerShape(6.dp)).background(Color(0xFFF2F2F4)).padding(horizontal = 8.dp, vertical = 2.dp), color = TextTertiary, fontSize = 11.sp)
            }
        }
        HorizontalDivider(color = LineSoft, modifier = Modifier.padding(vertical = 6.dp))

        // WebView 编辑器
        AndroidView(
            modifier = Modifier.weight(1f),
            factory = { ctx ->
                WebView(ctx).apply {
                    settings.javaScriptEnabled = true
                    settings.domStorageEnabled = true
                    webView = this
                    val initialHtml = if (note.contentFormat == NoteContentFormat.RICH_TEXT) note.body
                    else MarkdownConverter.toHtml(note.body)
                    loadDataWithBaseURL(BASE_URL, buildEditorHtml(initialHtml), "text/html", "utf-8", null)
                }
            }
        )

        // 底部格式工具条
        Row(
            Modifier
                .fillMaxWidth()
                .background(Color(0xFFFAFAFB))
                .horizontalScroll(rememberScrollState())
                .padding(horizontal = 8.dp, vertical = 9.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            FmtButton("T") { showStyleMenu = true }
            FmtButton("B", bold = true) { exec("bold") }
            FmtButton("I", italic = true) { exec("italic") }
            FmtButton("U", underline = true) { exec("underline") }
            FmtButton("S", strike = true) { exec("strikeThrough") }
            FmtSep()
            FmtButton("A", color = true) { showColorMenu = true }
            FmtButton("背景") { showHighlightMenu = true }
            FmtSep()
            FmtButton("•列表") { exec("insertUnorderedList") }
            FmtButton("1.列表") { exec("insertOrderedList") }
            FmtButton("引用") { exec("formatBlock", "blockquote") }
            FmtButton("代码") { exec("formatBlock", "pre") }
            FmtSep()
            FmtButton("链接") { showLinkDialog = true }
            FmtButton("图片") { pickImage.launch("image/*") }
            FmtButton("表格") { showTableDialog = true }
        }
    }

    // 样式菜单
    if (showStyleMenu) {
        OptionDialog("文本样式", onDismiss = { showStyleMenu = false }) {
            listOf(
                "正文" to "p",
                "一级标题" to "h1",
                "二级标题" to "h2",
                "三级标题" to "h3",
                "引用" to "blockquote",
                "代码块" to "pre"
            ).forEach { (label, tag) ->
                OptionItem(label) { exec("formatBlock", tag); showStyleMenu = false }
            }
        }
    }

    // 文字颜色
    if (showColorMenu) {
        OptionDialog("文字颜色", onDismiss = { showColorMenu = false }) {
            listOf("#1D1D1F", "#E5484D", "#F59E42", "#F7CE46", "#35A45A", "#2F7BEE", "#C86DD7", "#86868B", "#1A9E5C").forEach { c ->
                ColorItem(c) { exec("foreColor", c); showColorMenu = false }
            }
        }
    }

    // 背景高亮
    if (showHighlightMenu) {
        OptionDialog("背景颜色", onDismiss = { showHighlightMenu = false }) {
            listOf("#FCEFB4", "#DCEFD8", "#D6EFF0", "#D9E6F8", "#FBE4D6", "#F2F2F4", "#F7CE46", "#35A45A", "#FFFFFF").forEach { c ->
                ColorItem(c) { exec("hiliteColor", c); showHighlightMenu = false }
            }
        }
    }

    // 链接
    if (showLinkDialog) {
        var url by remember { mutableStateOf("https://") }
        AlertDialog(
            onDismissRequest = { showLinkDialog = false },
            title = { Text("插入链接") },
            text = {
                OutlinedTextField(value = url, onValueChange = { url = it }, singleLine = true)
            },
            confirmButton = {
                TextButton(onClick = { exec("createLink", url); showLinkDialog = false }) { Text("确定") }
            },
            dismissButton = {
                TextButton(onClick = { showLinkDialog = false }) { Text("取消") }
            }
        )
    }

    // 表格
    if (showTableDialog) {
        var rows by remember { mutableStateOf(3) }
        var cols by remember { mutableStateOf(3) }
        AlertDialog(
            onDismissRequest = { showTableDialog = false },
            title = { Text("插入表格") },
            text = {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text("行", fontSize = 13.sp)
                    Spacer(Modifier.width(8.dp))
                    TablePicker(rows) { rows = it }
                    Spacer(Modifier.width(16.dp))
                    Text("列", fontSize = 13.sp)
                    Spacer(Modifier.width(8.dp))
                    TablePicker(cols) { cols = it }
                }
            },
            confirmButton = {
                TextButton(onClick = {
                    val html = RichTextTableCodec.makeTableHtml(rows, cols)
                    exec("insertHTML", html)
                    showTableDialog = false
                }) { Text("插入") }
            },
            dismissButton = {
                TextButton(onClick = { showTableDialog = false }) { Text("取消") }
            }
        )
    }

    // 更多菜单
    if (showMore) {
        OptionDialog("更多操作", onDismiss = { showMore = false }) {
            OptionItem("同步本篇") {
                scope.launch { SyncEngine.syncSingleNote(noteId) }
                showMore = false
            }
            OptionItem("删除笔记（移入回收站）", danger = true) {
                NoteStore.moveToTrash(noteId); showMore = false; onBack()
            }
        }
    }
}

@Composable
private fun TablePicker(value: Int, onChange: (Int) -> Unit) {
    Row(horizontalArrangement = Arrangement.spacedBy(4.dp)) {
        (1..6).forEach { n ->
            Box(
                Modifier
                    .size(26.dp)
                    .clip(RoundedCornerShape(6.dp))
                    .background(if (n == value) Green else Color(0xFFF0F0F2))
                    .clickable { onChange(n) },
                contentAlignment = Alignment.Center
            ) {
                Text("$n", color = if (n == value) Color.White else TextSecondary, fontSize = 12.sp)
            }
        }
    }
}

@Composable
private fun FmtButton(label: String, bold: Boolean = false, italic: Boolean = false, underline: Boolean = false, strike: Boolean = false, color: Boolean = false, onClick: () -> Unit) {
    Text(
        label,
        Modifier
            .clip(RoundedCornerShape(8.dp))
            .clickable(onClick = onClick)
            .padding(horizontal = 9.dp, vertical = 8.dp),
        color = if (color) Green else TextSecondary,
        fontSize = 14.sp,
        fontWeight = if (bold) FontWeight.Bold else FontWeight.Normal,
        fontStyle = if (italic) androidx.compose.ui.text.font.FontStyle.Italic else androidx.compose.ui.text.font.FontStyle.Normal,
        textDecoration = if (underline) androidx.compose.ui.text.style.TextDecoration.Underline
        else if (strike) androidx.compose.ui.text.style.TextDecoration.LineThrough
        else androidx.compose.ui.text.style.TextDecoration.None
    )
}

@Composable
private fun FmtSep() {
    Box(Modifier.width(1.dp).height(18.dp).padding(horizontal = 4.dp).background(Line))
}

@Composable
private fun OptionDialog(title: String, onDismiss: () -> Unit, content: @Composable ColumnScope.() -> Unit) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(title, fontSize = 15.sp) },
        text = { Column(content = content) },
        confirmButton = {}
    )
}

@Composable
private fun OptionItem(label: String, danger: Boolean = false, onClick: () -> Unit) {
    Text(
        label,
        Modifier.fillMaxWidth().clickable(onClick = onClick).padding(vertical = 12.dp),
        color = if (danger) Red else TextPrimary,
        fontSize = 14.sp
    )
}

@Composable
private fun ColumnScope.ColorItem(hex: String, onClick: () -> Unit) {
    Row(
        Modifier.fillMaxWidth().clickable(onClick = onClick).padding(vertical = 6.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Box(Modifier.size(26.dp).clip(RoundedCornerShape(7.dp)).background(Color(android.graphics.Color.parseColor(hex))))
        Spacer(Modifier.width(10.dp))
        Text(hex, fontSize = 13.sp, color = TextSecondary)
    }
}

private fun jsString(value: String?): String {
    if (value == null || value == "null") return ""
    return try {
        JSONObject("{\"v\":$value}").optString("v")
    } catch (e: Exception) {
        value.removeSurrounding("\"").replace("\\n", "\n").replace("\\\"", "\"").replace("\\\\", "\\")
    }
}

private fun buildEditorHtml(body: String): String {
    val css = """
        *{box-sizing:border-box}
        body{font-family:-apple-system,"PingFang SC",sans-serif;font-size:16px;line-height:1.75;color:#1d1d1f;padding:12px 4px;margin:0;word-wrap:break-word;}
        h1{font-size:24px;font-weight:700;margin:12px 0 6px}
        h2{font-size:20px;font-weight:700;margin:10px 0 5px}
        h3{font-size:18px;font-weight:600;margin:8px 0 4px}
        p{margin:0 0 8px}
        blockquote{border-left:3px solid #1a9e5c;padding:4px 10px;margin:8px 0;color:#86868b;background:#f7faf8;}
        pre{background:#f5f5f7;padding:10px;border-radius:6px;font-family:monospace;white-space:pre-wrap;}
        img{max-width:100%;border-radius:6px;}
        table{border-collapse:collapse;margin:8px 0;width:100%;}
        td,th{border:1px solid #d9dadf;padding:6px 10px;min-width:40px;vertical-align:top;}
        td[data-align="center"]{text-align:center}
        td[data-align="right"]{text-align:right}
        td[data-align="justify"]{text-align:justify}
    """.trimIndent()
    return "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1,maximum-scale=1\"><style>$css</style></head><body contenteditable=\"true\">${body}</body></html>"
}
