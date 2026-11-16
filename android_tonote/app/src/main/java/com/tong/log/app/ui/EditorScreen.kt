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
import androidx.compose.ui.text.style.TextAlign
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
import com.tong.log.app.data.sync.SyncCursorStore
import com.tong.log.app.data.sync.SyncEngine
import com.tong.log.app.data.sync.SyncScope
import com.tong.log.app.data.model.QUICK_SYNC_NOTE_ID
import androidx.activity.compose.BackHandler
import com.tong.log.app.ui.theme.*
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import org.json.JSONObject

private val BASE_URL get() = "file://${AppStorage.rootDir.absolutePath}/"

/** 富文本编辑器：WebView + contenteditable，正文即受控 HTML，与 Mac 端格式互通。 */
@OptIn(ExperimentalLayoutApi::class)
@SuppressLint("SetJavaScriptEnabled", "JavascriptInterface")
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

    // 右上角文字动作按钮的切换信号：
    // ① edited —— WebView 正文被用户输入改动（JS bridge 通知，原生侧不落盘，点「保存」时才写）；
    // ② noteDirty —— 本篇已有未同步变更（上次编辑遗留 / 工具栏操作触发 captureBody 标脏）。
    // dirtyCount 只作重组触发，本篇是否脏按 noteId 精确判断，避免别的笔记改动影响这里。
    val dirtyCount by SyncCursorStore.dirtyCount.collectAsState()
    var edited by remember { mutableStateOf(false) }
    val noteDirty = dirtyCount >= 0 && SyncCursorStore.cursor(noteId, false)?.dirty == true
    val needSave = edited || noteDirty

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

    // 离开编辑器（返回键 / 左上角返回）：正文先落盘，再由引擎在自己的作用域里立刻上传
    fun leave() {
        if (edited) saveBody()
        SyncEngine.flushNotesSoon()
        onBack()
    }
    BackHandler { leave() }

    // 点开笔记：先拉远端（插队，优先于定时轮）；本篇没被用户改过且拉到新内容时，刷新编辑器
    LaunchedEffect(noteId) {
        val before = NoteStore.note(noteId)?.body
        SyncEngine.request(SyncScope.OneNote(noteId))
        // 若请求被排进队列，request 立即返回；等同步空闲后再看有没有拉到新内容
        var waited = 0
        while (SyncEngine.status.value.isSyncing && waited < 60) { delay(300); waited++ }
        val after = NoteStore.note(noteId) ?: return@LaunchedEffect
        if (after.body != before && !edited) {
            title = after.title
            val html = if (after.contentFormat == NoteContentFormat.RICH_TEXT) after.body
            else MarkdownConverter.toHtml(after.body)
            webView?.loadDataWithBaseURL(BASE_URL, buildEditorHtml(html), "text/html", "utf-8", null)
        }
    }

    // 编辑中定时自动保存并上传：「快同步」15 秒，其它笔记 30 秒
    LaunchedEffect(noteId) {
        val interval = if (noteId.equals(QUICK_SYNC_NOTE_ID, ignoreCase = true))
            SyncEngine.QUICK_NOTE_UPLOAD_INTERVAL_MS else SyncEngine.NOTES_UPLOAD_INTERVAL_MS
        while (true) {
            delay(interval)
            if (edited || SyncCursorStore.cursor(noteId, false)?.dirty == true) {
                if (edited) saveBody()
                delay(350)
                SyncEngine.flushNotes()
                edited = false
            }
        }
    }

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
            Modifier.fillMaxWidth().padding(start = 4.dp, end = 0.dp, top = 4.dp, bottom = 4.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            IconButton(onClick = { leave() }) {
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
            // 右上角文字按钮：语义明确，避免图标被误读。
            // 「同步」= 用云端本篇覆盖本地（远程 → 本地）；
            // 「保存」= 本地有变更后，先落盘再推云端（本地 → 本地 + 远程）。
            // 两者互斥显示，不会出现「有改动却提供同步覆盖」的危险入口。
            val syncing = SyncEngine.status.collectAsState().value.isSyncing
            Text(
                text = when {
                    syncing -> "同步中"
                    needSave -> "保存"
                    else -> "同步"
                },
                modifier = Modifier
                    .clip(RoundedCornerShape(6.dp))
                    .clickable(enabled = !syncing) {
                        if (needSave) {
                            // 保存：正文写回本地，再推到云端。captureBody 是异步回写，
                            // 等落盘完成后再 syncSingleNote，否则会把旧内容推上去。
                            saveBody()
                            scope.launch {
                                delay(350)
                                SyncEngine.syncSingleNote(noteId)
                                edited = false
                            }
                        } else {
                            // 同步：强制拉云端本篇覆盖本地，并刷新编辑器内容
                            scope.launch {
                                val remote = SyncEngine.pullSingleNote(noteId)
                                if (remote != null) {
                                    title = remote.title
                                    val html = if (remote.contentFormat == NoteContentFormat.RICH_TEXT) remote.body
                                    else MarkdownConverter.toHtml(remote.body)
                                    webView?.loadDataWithBaseURL(BASE_URL, buildEditorHtml(html), "text/html", "utf-8", null)
                                    edited = false
                                }
                            }
                        }
                    }
                    .padding(start = 10.dp, end = 0.dp, top = 9.dp, bottom = 9.dp),
                color = if (needSave) Green else TextPrimary,
                fontSize = 14.sp,
                fontWeight = FontWeight.Medium
            )
            // 与文字按钮间距 0，整体更贴右边缘
            Box(
                Modifier
                    .size(38.dp)
                    .clip(RoundedCornerShape(19.dp))
                    .clickable { showMore = true },
                contentAlignment = Alignment.Center
            ) {
                Icon(Icons.Default.MoreVert, contentDescription = "更多", tint = TextPrimary, modifier = Modifier.size(20.dp))
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
                    // 正文输入 → 切「保存」。loadDataWithBaseURL 加载的是 data: 页面，
                    // addJavascriptInterface 注入的对象在部分机型上不可见，改用 console 通道最稳；
                    // 回调在 WebView 线程，post 回主线程再改 Compose state。
                    webChromeClient = object : android.webkit.WebChromeClient() {
                        override fun onConsoleMessage(msg: android.webkit.ConsoleMessage): Boolean {
                            if (msg.message() == "__TONOTE_EDITED__") {
                                this@apply.post { edited = true }
                                return true
                            }
                            return super.onConsoleMessage(msg)
                        }
                    }
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
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                NumberStepper("行", rows) { rows = it }
                NumberStepper("列", cols) { cols = it }
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

/**
 * 行 / 列步进器：[-] 数字 [+]，默认 3，范围 1..10。
 * 两行独立摆放（行一行、列一行），比原来 1..6 的数字胶囊更省横向空间。
 */
@Composable
private fun NumberStepper(label: String, value: Int, onChange: (Int) -> Unit) {
    Row(verticalAlignment = Alignment.CenterVertically) {
        Text(label, fontSize = 13.sp, color = TextSecondary, modifier = Modifier.width(28.dp))
        Spacer(Modifier.width(10.dp))
        Box(
            Modifier
                .size(30.dp)
                .clip(RoundedCornerShape(7.dp))
                .background(Color(0xFFF0F0F2))
                .clickable(enabled = value > 1) { onChange((value - 1).coerceAtLeast(1)) },
            contentAlignment = Alignment.Center
        ) {
            Text("−", fontSize = 16.sp, color = if (value > 1) TextPrimary else TextQuaternary)
        }
        Text(
            "$value",
            fontSize = 14.sp,
            fontWeight = FontWeight.SemiBold,
            color = TextPrimary,
            textAlign = TextAlign.Center,
            modifier = Modifier.width(52.dp)
        )
        Box(
            Modifier
                .size(30.dp)
                .clip(RoundedCornerShape(7.dp))
                .background(Color(0xFFF0F0F2))
                .clickable(enabled = value < 10) { onChange((value + 1).coerceAtMost(10)) },
            contentAlignment = Alignment.Center
        ) {
            Text("+", fontSize = 16.sp, color = if (value < 10) TextPrimary else TextQuaternary)
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
        Column(Modifier.navigationBarsPadding()) {
            Text(
                title,
                Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 15.dp),
                color = TextPrimary,
                fontSize = 16.sp,
                fontWeight = FontWeight.Bold
            )
            HorizontalDivider(thickness = 0.75.dp, color = LineHair)
            Column(content = content)
            Spacer(Modifier.height(10.dp))
            // 底部取消按钮：浅灰胶囊，左右边距与选项分割线一致（48dp），两者同宽
            Box(
                Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 48.dp)
                    .clip(RoundedCornerShape(24.dp))
                    .background(Color(0xFFF2F2F5))
                    .clickable(onClick = onDismiss)
                    .padding(vertical = 12.dp),
                contentAlignment = Alignment.Center
            ) {
                Text("取消", fontSize = 15.sp, fontWeight = FontWeight.Medium, color = TextPrimary)
            }
            Spacer(Modifier.height(10.dp))
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
                HorizontalDivider(thickness = 0.75.dp, color = LineHair)
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

/** 选项之间的分割线（0.5dp 发丝线，左右留 48dp 边距）。 */
@Composable
private fun OptionDivider() {
    HorizontalDivider(thickness = 0.5.dp, color = LineHair, modifier = Modifier.padding(horizontal = 48.dp))
}

@Composable
private fun OptionItem(label: String, danger: Boolean = false, onClick: () -> Unit) {
    Text(
        label,
        Modifier.fillMaxWidth().clickable(onClick = onClick).padding(vertical = 16.dp),
        color = if (danger) Red else TextPrimary,
        fontSize = 14.sp,
        textAlign = TextAlign.Center
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
          var editTimer = null;
          document.addEventListener('input', function(){
            schedule();
            // 只有真正的正文输入才通知原生切「保存」按钮；200ms 合并，避免每个字符都刷
            if (editTimer) clearTimeout(editTimer);
            editTimer = setTimeout(function(){
              try { console.log('__TONOTE_EDITED__'); } catch(e) {}
            }, 200);
          });
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
