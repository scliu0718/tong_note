package com.tong.log.app.ui

import android.annotation.SuppressLint
import android.webkit.WebView
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Code
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.FormatBold
import androidx.compose.material.icons.filled.FormatColorFill
import androidx.compose.material.icons.filled.FormatColorText
import androidx.compose.material.icons.filled.FormatItalic
import androidx.compose.material.icons.filled.FormatListBulleted
import androidx.compose.material.icons.filled.FormatListNumbered
import androidx.compose.material.icons.filled.FormatQuote
import androidx.compose.material.icons.filled.StrikethroughS
import androidx.compose.material.icons.filled.FormatUnderlined
import androidx.compose.material.icons.filled.Image
import androidx.compose.material.icons.filled.Link
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.TableChart
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.compose.ui.window.Dialog
import com.tong.log.app.data.codec.MarkdownConverter
import com.tong.log.app.data.codec.RichTextTableCodec
import com.tong.log.app.data.model.Note
import com.tong.log.app.data.model.NoteContentFormat
import com.tong.log.app.data.store.AppStorage
import com.tong.log.app.data.store.AttachmentStore
import com.tong.log.app.data.store.NoteStore
import com.tong.log.app.data.sync.SyncEngine
import com.tong.log.app.ui.theme.*
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import org.json.JSONObject

private val BASE_URL get() = "file://${AppStorage.rootDir.absolutePath}/"

/** 富文本编辑器：WebView + contenteditable，正文即受控 HTML，与 Mac 端格式互通。 */
@OptIn(ExperimentalLayoutApi::class)
@SuppressLint("SetJavaScriptEnabled")
@Composable
fun EditorScreen(noteId: String, onBack: () -> Unit) {
    val context = androidx.compose.ui.platform.LocalContext.current
    val note = NoteStore.note(noteId)
    if (note == null) {
        LaunchedEffect(Unit) { onBack() }
        return
    }
    var title by remember { mutableStateOf(note.title) }
    var showColorMenu by remember { mutableStateOf(false) }
    var showHighlightMenu by remember { mutableStateOf(false) }
    var showLinkDialog by remember { mutableStateOf(false) }
    var showTableDialog by remember { mutableStateOf(false) }
    var showMore by remember { mutableStateOf(false) }
    var showTitleDialog by remember { mutableStateOf(false) }
    var webView by remember { mutableStateOf<WebView?>(null) }
    val scope = rememberCoroutineScope()

    // 键盘开合会把 WebView 压小，Chromium 不会主动把光标滚进新的可视区，
    // 正文脚本里的 resize 兜底偶发不触发，所以再从原生侧按 IME 高度变化补两次校正。
    val imeBottom = WindowInsets.ime.asPaddingValues().calculateBottomPadding()
    LaunchedEffect(imeBottom) {
        val wv = webView ?: return@LaunchedEffect
        delay(180)
        wv.evaluateJavascript("window.__caretIntoView && window.__caretIntoView();", null)
        delay(220)
        wv.evaluateJavascript("window.__caretIntoView && window.__caretIntoView();", null)
    }

    /**
     * 取回 WebView 正文。命令与取 HTML 放进同一次 evaluateJavascript，
     * 避免 execCommand 还没落地就先存了旧 DOM（工具栏操作偶发不生效）。
     *
     * 旧 Markdown 笔记在 Android 只做有损 HTML 渲染，若把 innerHTML 写回，会把 Mac 的
     * Markdown 原稿永久改写并强制改成 richText —— 标脏同步接通后会真的写坏云端数据。
     * 因此这类笔记在手机端只阅读、不回写正文。
     */
    fun captureBody(command: String? = null) {
        if (note.contentFormat != NoteContentFormat.RICH_TEXT) return
        val wv = webView ?: return
        val script = if (command == null) "document.body.innerHTML"
        else "(function(){ $command return document.body.innerHTML; })()"
        wv.evaluateJavascript(script) { v ->
            val html = jsString(v).replace(BASE_URL, "")
            if (html.isBlank()) return@evaluateJavascript
            NoteStore.updateContent(noteId, html, NoteContentFormat.RICH_TEXT)
        }
    }

    fun saveBody() = captureBody()

    fun exec(cmd: String, value: String? = null) {
        val js = if (value == null) "document.execCommand('$cmd');"
        else "document.execCommand('$cmd', false, '$value');"
        captureBody(js)
    }

    val pickImage = rememberLauncherForActivityResult(ActivityResultContracts.GetContent()) { uri ->
        uri?.let {
            val rel = AttachmentStore.importImage(it, noteId)
            if (rel == null) {
                android.widget.Toast.makeText(context, "图片导入失败，请换一张重试", android.widget.Toast.LENGTH_SHORT).show()
            } else {
                captureBody("document.execCommand('insertImage', false, '$rel');")
            }
        }
    }

    // 编辑器是二级页面，标题栏必须避开状态栏。
    // 底部取「导航栏 / 输入法」中较大者：Android 15（targetSdk 35）边到边后，
    // adjustResize 不再替应用缩小窗口，必须显式消费 IME inset，否则弹键盘会盖住正在编辑的正文。
    // 注意：IME inset 只有在 decorFitsSystemWindows=false 时才会下发到 Compose，
    // 所以 MainActivity 里必须调用 enableEdgeToEdge()（见 MainActivity.onCreate）。
    Column(
        Modifier
            .fillMaxSize()
            .background(Color.White)
            .windowInsetsPadding(WindowInsets.systemBars.only(WindowInsetsSides.Top))
            .windowInsetsPadding(WindowInsets.ime.union(WindowInsets.systemBars.only(WindowInsetsSides.Bottom)))
    ) {
        // 顶部导航：返回 + 笔记标题（单行省略，改标题走右上角菜单）
        Row(
            Modifier.fillMaxWidth().padding(horizontal = 4.dp, vertical = 4.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            IconButton(onClick = { saveBody(); onBack() }) {
                Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "返回", tint = TextPrimary)
            }
            Text(
                text = title.ifBlank { "无标题" },
                modifier = Modifier.weight(1f).padding(horizontal = 4.dp),
                color = if (title.isBlank()) TextQuaternary else TextPrimary,
                fontSize = 17.sp,
                fontWeight = FontWeight.Bold,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis
            )
            IconButton(onClick = { showMore = true }) {
                Icon(Icons.Default.MoreVert, contentDescription = "更多", tint = TextPrimary)
            }
        }
        // 标题栏分割线
        TopBarDivider()

        // 第一行：笔记本 + 标签（数量多时自动换行）
        FlowRow(
            Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 6.dp),
            horizontalArrangement = Arrangement.spacedBy(6.dp),
            verticalArrangement = Arrangement.spacedBy(4.dp)
        ) {
            Text(
                note.notebook,
                Modifier.clip(RoundedCornerShape(6.dp)).background(GreenTint).padding(horizontal = 8.dp, vertical = 2.dp),
                color = GreenHover, fontSize = 11.sp
            )
            note.tags.forEach { tag ->
                Text(
                    tag,
                    Modifier.clip(RoundedCornerShape(6.dp)).background(Color(0xFFF2F2F4)).padding(horizontal = 8.dp, vertical = 2.dp),
                    color = TextTertiary, fontSize = 11.sp
                )
            }
            if (note.contentFormat != NoteContentFormat.RICH_TEXT) {
                Spacer(Modifier.width(8.dp))
                Text("Markdown 笔记 · 手机端只读", color = TextTertiary, fontSize = 10.sp)
            }
        }

        // 第二行：格式操作条（自底部上移，自动换行）。
        // 图标 chip 样式与 Mac 端 SelectionFormatBar 一致：浅灰圆角底、分隔线分组、
        // 顺序 = 段落样式 → 引用/代码/列表 → B/I/U/S → 颜色 → 链接/图片/表格
        FlowRow(
            Modifier
                .fillMaxWidth()
                .background(Color(0xFFFAFAFB))
                .padding(horizontal = 8.dp, vertical = 4.dp),
            horizontalArrangement = Arrangement.spacedBy(3.dp),
            verticalArrangement = Arrangement.spacedBy(3.dp)
        ) {
            FmtTextChip("正文") { exec("formatBlock", "p") }
            FmtTextChip("H1") { exec("formatBlock", "h1") }
            FmtTextChip("H2") { exec("formatBlock", "h2") }
            FmtTextChip("H3") { exec("formatBlock", "h3") }
            FmtSep()
            FmtIconButton(Icons.Default.FormatQuote, "引用") { exec("formatBlock", "blockquote") }
            FmtIconButton(Icons.Default.Code, "代码块") { exec("formatBlock", "pre") }
            FmtIconButton(Icons.Default.FormatListBulleted, "无序列表") { exec("insertUnorderedList") }
            FmtIconButton(Icons.Default.FormatListNumbered, "有序列表") { exec("insertOrderedList") }
            FmtSep()
            FmtIconButton(Icons.Default.FormatBold, "加粗") { exec("bold") }
            FmtIconButton(Icons.Default.FormatItalic, "斜体") { exec("italic") }
            FmtIconButton(Icons.Default.FormatUnderlined, "下划线") { exec("underline") }
            FmtIconButton(Icons.Default.StrikethroughS, "删除线") { exec("strikeThrough") }
            FmtSep()
            FmtIconButton(Icons.Default.FormatColorText, "文字颜色") { showColorMenu = true }
            FmtIconButton(Icons.Default.FormatColorFill, "背景颜色") { showHighlightMenu = true }
            FmtSep()
            FmtIconButton(Icons.Default.Link, "插入链接") { showLinkDialog = true }
            FmtIconButton(Icons.Default.Image, "插入图片") { pickImage.launch("image/*") }
            FmtIconButton(Icons.Default.TableChart, "插入表格") { showTableDialog = true }
        }
        HorizontalDivider(color = LineSoft)

        // WebView 编辑器
        AndroidView(
            modifier = Modifier.weight(1f),
            factory = { ctx ->
                WebView(ctx).apply {
                    settings.javaScriptEnabled = true
                    settings.domStorageEnabled = true
                    // 正文图片是相对路径 attachments/<noteId>/<uuid>.<ext>，靠 BASE_URL 解析成 file:// 子资源。
                    // Android 11+ allowFileAccess 默认 false；loadDataWithBaseURL 的 data: 页面
                    // 访问 file:// 子资源还需要 allowFileAccessFromFileURLs，缺一则图片全部加载失败
                    settings.allowFileAccess = true
                    settings.allowFileAccessFromFileURLs = true
                    webView = this
                    val initialHtml = if (note.contentFormat == NoteContentFormat.RICH_TEXT) note.body
                    else MarkdownConverter.toHtml(note.body)
                    loadDataWithBaseURL(BASE_URL, buildEditorHtml(initialHtml), "text/html", "utf-8", null)
                }
            }
        )
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
        EditorDialog(
            title = "插入链接",
            onDismiss = { showLinkDialog = false },
            confirmLabel = "确定",
            onConfirm = {
                exec("createLink", url)
                showLinkDialog = false
            }
        ) {
            OutlinedTextField(value = url, onValueChange = { url = it }, singleLine = true, modifier = Modifier.fillMaxWidth())
        }
    }

    // 表格
    if (showTableDialog) {
        var rows by remember { mutableStateOf(3) }
        var cols by remember { mutableStateOf(3) }
        EditorDialog(
            title = "插入表格",
            onDismiss = { showTableDialog = false },
            confirmLabel = "插入",
            onConfirm = {
                val html = RichTextTableCodec.makeTableHtml(rows, cols)
                exec("insertHTML", html)
                showTableDialog = false
            }
        ) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text("行", fontSize = 13.sp)
                Spacer(Modifier.width(8.dp))
                TablePicker(rows) { rows = it }
                Spacer(Modifier.width(16.dp))
                Text("列", fontSize = 13.sp)
                Spacer(Modifier.width(8.dp))
                TablePicker(cols) { cols = it }
            }
        }
    }

    // 编辑标题
    if (showTitleDialog) {
        var draft by remember(title) { mutableStateOf(title) }
        EditorDialog(
            title = "编辑标题",
            onDismiss = { showTitleDialog = false },
            confirmLabel = "保存",
            onConfirm = {
                val t = draft.trim()
                title = t
                NoteStore.updateTitle(noteId, t)
                showTitleDialog = false
            }
        ) {
            OutlinedTextField(
                value = draft,
                onValueChange = { draft = it },
                singleLine = true,
                placeholder = { Text("笔记标题", fontSize = 14.sp) },
                modifier = Modifier.fillMaxWidth()
            )
        }
    }

    // 更多菜单
    if (showMore) {
        OptionDialog("更多操作", onDismiss = { showMore = false }) {
            OptionItem("编辑标题") {
                showMore = false
                showTitleDialog = true
            }
            OptionDivider()
            OptionItem("同步本篇") {
                scope.launch { SyncEngine.syncSingleNote(noteId) }
                showMore = false
            }
            OptionDivider()
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

/** 图标 chip：样式对齐 Mac 端 SelectionFormatBar 的 actionIcon（浅灰圆角底 + 图标）。 */
@Composable
private fun FmtIconButton(icon: androidx.compose.ui.graphics.vector.ImageVector, desc: String, onClick: () -> Unit) {
    Box(
        Modifier
            .size(width = 30.dp, height = 26.dp)
            .clip(RoundedCornerShape(5.dp))
            .background(Color(0xFFF4F4F6))
            .clickable(onClick = onClick),
        contentAlignment = Alignment.Center
    ) {
        Icon(icon, contentDescription = desc, tint = TextSecondary, modifier = Modifier.size(16.dp))
    }
}

/** 文字 chip：对齐 Mac 端的 actionText（正文 / H1 / H2 / H3）。 */
@Composable
private fun FmtTextChip(label: String, onClick: () -> Unit) {
    Box(
        Modifier
            .height(26.dp)
            .clip(RoundedCornerShape(5.dp))
            .background(Color(0xFFF4F4F6))
            .clickable(onClick = onClick)
            .padding(horizontal = 7.dp),
        contentAlignment = Alignment.Center
    ) {
        Text(label, fontSize = 11.sp, fontWeight = FontWeight.Medium, color = TextSecondary)
    }
}

@Composable
private fun FmtSep() {
    Box(Modifier.width(1.dp).height(18.dp).padding(horizontal = 4.dp).background(Line))
}

/** 底部抽屉：从屏幕底部上滑、贴底全宽（下边留 6dp），用于「更多操作」等选项列表。 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun OptionDialog(title: String, onDismiss: () -> Unit, content: @Composable ColumnScope.() -> Unit) {
    ModalBottomSheet(
        onDismissRequest = onDismiss,
        containerColor = Color.White,
        shape = RoundedCornerShape(topStart = 16.dp, topEnd = 16.dp),
        dragHandle = null,
        modifier = Modifier.padding(bottom = 6.dp)
    ) {
        Column {
            Text(
                title,
                Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 15.dp),
                color = TextPrimary,
                fontSize = 16.sp,
                fontWeight = FontWeight.Bold
            )
            HorizontalDivider(thickness = 0.5.dp, color = LineHair)
            Column(content = content)
        }
    }
}

/**
 * 统一样式的弹窗：标题加粗 + 标题下分割线 + 紧凑下边距；
 * 传 confirmLabel/onConfirm 时底部带「取消 / 确认」按钮行。
 * 不用 M3 AlertDialog，因为它的固定 padding 会把标题和底部撑得很空。
 */
@Composable
private fun EditorDialog(
    title: String,
    onDismiss: () -> Unit,
    confirmLabel: String? = null,
    onConfirm: (() -> Unit)? = null,
    content: @Composable ColumnScope.() -> Unit
) {
    Dialog(onDismissRequest = onDismiss) {
        Surface(
            shape = RoundedCornerShape(14.dp),
            color = Color.White,
            shadowElevation = 6.dp,
            modifier = Modifier.fillMaxWidth()
        ) {
            Column {
                Text(
                    title,
                    Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 11.dp),
                    color = TextPrimary,
                    fontSize = 14.sp,
                    fontWeight = FontWeight.Bold
                )
                HorizontalDivider(thickness = 0.5.dp, color = LineHair)
                Column(Modifier.padding(horizontal = 16.dp, vertical = 4.dp), content = content)
                if (confirmLabel != null && onConfirm != null) {
                    HorizontalDivider(thickness = 0.5.dp, color = LineHair)
                    Row(
                        Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 6.dp),
                        horizontalArrangement = Arrangement.End,
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        TextButton(onClick = onDismiss) { Text("取消", color = TextSecondary) }
                        TextButton(onClick = { onConfirm() }) {
                            Text(confirmLabel, color = GreenHover, fontWeight = FontWeight.SemiBold)
                        }
                    }
                }
            }
        }
    }
}

/** 选项之间的分割线（0.5dp 发丝线，从文字起始位置留 16dp 边距）。 */
@Composable
private fun OptionDivider() {
    HorizontalDivider(thickness = 0.5.dp, color = LineHair, modifier = Modifier.padding(horizontal = 16.dp))
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
        td,th{border:1px solid #d9dadf;padding:6px 10px;min-width:40px;height:calc(2.5em - 2px);vertical-align:top;}
        td[data-align="center"]{text-align:center}
        td[data-align="right"]{text-align:right}
        td[data-align="justify"]{text-align:justify}
        /* 键盘弹起时（.kb）给正文尾部留出空间，否则最后几行滚不到键盘上方 */
        body.kb{padding-bottom:45vh}
    """.trimIndent()
    // 光标跟随脚本。必须放 <head>：保存正文取的是 document.body.innerHTML，
    // 脚本若混在 body 里会被当成正文持久化，还会同步到 Mac / Windows 端。
    val caretJs = """
        (function(){
          var MARGIN = 72;      // 光标与可视区底部保持的距离
          var pending = false;
          function caretRect(){
            var sel = window.getSelection();
            if (!sel || sel.rangeCount === 0) return null;
            var range = sel.getRangeAt(0);
            var rects = range.getClientRects();
            if (rects && rects.length) return rects[rects.length - 1];
            var r = range.getBoundingClientRect();
            return (r && (r.height || r.width || r.top)) ? r : null;
          }
          function ensureCaretVisible(){
            pending = false;
            var r = caretRect();
            if (!r) return;
            var vh = window.innerHeight || document.documentElement.clientHeight;
            if (r.bottom > vh - MARGIN) window.scrollBy(0, r.bottom - (vh - MARGIN));
            else if (r.top < 12) window.scrollBy(0, r.top - 12);
          }
          window.__caretIntoView = ensureCaretVisible;
          function schedule(){
            if (pending) return;
            pending = true;
            setTimeout(ensureCaretVisible, 40);
          }
          document.addEventListener('selectionchange', schedule);
          document.addEventListener('input', schedule);
          document.addEventListener('keyup', schedule);
          document.addEventListener('focusin', schedule);
          document.addEventListener('click', schedule);
          // WebView 被键盘压小后 Chromium 不会自己把光标滚进来，靠 resize 兜一次；
          // 同时按高度突变判断键盘开合，切换尾部留白
          var lastH = window.innerHeight;
          window.addEventListener('resize', function(){
            var h = window.innerHeight;
            if (document.body){
              if (h < lastH - 60) document.body.classList.add('kb');
              else if (h > lastH + 60) document.body.classList.remove('kb');
            }
            lastH = h;
            setTimeout(ensureCaretVisible, 120);
            setTimeout(ensureCaretVisible, 320);
          });
        })();
    """.trimIndent()
    return "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1,maximum-scale=1\"><style>$css</style><script>$caretJs</script></head><body contenteditable=\"true\">${body}</body></html>"
}
