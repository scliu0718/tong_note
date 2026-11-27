package com.tong.log.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.window.Dialog
import kotlinx.coroutines.delay
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.PushPin
import androidx.compose.material.icons.filled.Search
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tong.log.app.data.model.Note
import com.tong.log.app.data.model.Notebooks
import com.tong.log.app.data.model.AvatarPresets
import com.tong.log.app.data.model.QUICK_SYNC_NOTE_ID
import com.tong.log.app.data.store.AvatarStore
import com.tong.log.app.data.store.NoteStore
import com.tong.log.app.data.store.NotebookStore
import com.tong.log.app.data.store.TodoStore
import com.tong.log.app.data.store.isOverdue
import com.tong.log.app.ui.theme.*
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.time.format.DateTimeFormatter

internal fun displayTime(iso: String?): String {
    if (iso == null) return ""
    return try {
        val instant = Instant.parse(iso)
        val zoned = instant.atZone(ZoneId.systemDefault())
        val date = zoned.toLocalDate()
        val today = LocalDate.now()
        when (date) {
            today -> "今天 " + zoned.format(DateTimeFormatter.ofPattern("HH:mm"))
            today.minusDays(1) -> "昨天 " + zoned.format(DateTimeFormatter.ofPattern("HH:mm"))
            else -> zoned.format(DateTimeFormatter.ofPattern("M月d日"))
        }
    } catch (e: Exception) {
        ""
    }
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
fun NotesScreen(
    onOpenNote: (String) -> Unit,
    onOpenQuickSync: () -> Unit,
    onOpenTodo: () -> Unit,
    onOpenMe: () -> Unit,
    onNewNote: (String) -> Unit
) {
    val notes by NoteStore.notes.collectAsState()
    val account by AuthState.account.collectAsState()
    // 「快任务」卡片右上角逾期数量角标
    val todos by TodoStore.todos.collectAsState()
    val overdueCount = todos.count { !it.purged && it.isOverdue }
    // 左上角头像跟随「我的」里选中的头像（AvatarStore 是本地 prefs，与 Mac 端 preset id 同源）
    val avatarId by AvatarStore.currentId.collectAsState()
    val avatar = AvatarPresets.byId(avatarId)
    val accountName = account?.displayName?.takeIf { it.isNotBlank() } ?: "获取中…"
    // 笔记本列表读自本地偏好（非 Flow）：新建后用 notebookVersion 触发刷新；
    // 同步拉到别的设备建的笔记本时 notes 会变，也顺带刷新
    var notebookVersion by remember { mutableIntStateOf(0) }
    val notebooks = remember(notes, notebookVersion) { NotebookStore.all }
    val customNotebooks = remember(notes, notebookVersion) { NotebookStore.customNotebooks }
    var showNewNotebook by remember { mutableStateOf(false) }
    // 长按弹出「删除」菜单的笔记本 / 等待二次确认删除的笔记本
    var menuNotebook by remember { mutableStateOf<String?>(null) }
    var pendingDeleteNotebook by remember { mutableStateOf<String?>(null) }
    val notebookScroll = rememberScrollState()
    var scrollNotebooksToEnd by remember { mutableStateOf(false) }
    LaunchedEffect(scrollNotebooksToEnd, notebooks.size) {
        if (scrollNotebooksToEnd) {
            delay(60) // 等新 chip 排版完，maxValue 才是最新的
            notebookScroll.animateScrollTo(notebookScroll.maxValue)
            scrollNotebooksToEnd = false
        }
    }
    var selectedNotebook by remember { mutableStateOf(Notebooks.ALL) }
    // 笔记本与标签互斥（与 Mac 侧栏一致）：选标签 → 笔记本回到「全部笔记」；选笔记本 → 清掉标签
    var selectedTag by remember { mutableStateOf<String?>(null) }
    var query by remember { mutableStateOf("") }
    var sort by remember { mutableStateOf("updated") }
    val searchFocus = remember { FocusRequester() }

    // 标签 → 笔记数：只统计未删除、非「快同步」的笔记，避免出现点进去却是空列表的标签
    val tagCounts = remember(notes) {
        notes.asSequence()
            .filter { !it.purged && !it.isTrashed && it.id != QUICK_SYNC_NOTE_ID }
            .flatMap { it.tags.asSequence() }
            .groupingBy { it }
            .eachCount()
            .toSortedMap()
    }
    // 选中的标签被删光（本机或其它设备同步下来）时自动取消筛选，避免列表一直为空
    LaunchedEffect(tagCounts) {
        val tag = selectedTag
        if (tag != null && tag !in tagCounts) selectedTag = null
    }

    val filtered = remember(notes, selectedNotebook, selectedTag, query, sort) {
        notes.asSequence()
            .filter { !it.purged && !it.isTrashed && it.id != QUICK_SYNC_NOTE_ID }
            .filter { selectedNotebook == Notebooks.ALL || it.notebook == selectedNotebook }
            .filter { selectedTag == null || selectedTag in it.tags }
            .filter {
                query.isBlank() || it.title.contains(query, true) ||
                    it.plainBody.contains(query, true)
            }
            .sortedWith(
                // 置顶笔记永远在最上面，同组内再按所选方式排序
                compareByDescending<Note> { it.pinned }.then(
                    when (sort) {
                        "created" -> compareByDescending<Note> { it.createdAt }
                        "title" -> compareBy<Note> { it.title }
                        else -> compareByDescending<Note> { it.updatedAt }
                    }
                )
            )
            .toList()
    }

    Column(Modifier.fillMaxSize().background(Bg)) {
        // 顶部导航
        Row(
            Modifier.fillMaxWidth().padding(horizontal = 14.dp, vertical = 7.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Box(
                Modifier
                    .size(30.dp)
                    .clip(CircleShape)
                    .background(Brush.linearGradient(listOf(Color(avatar.color1), Color(avatar.color2))))
                    .clickable(onClick = onOpenMe),
                contentAlignment = Alignment.Center
            ) {
                AvatarGlyph(avatar, 30.dp)
            }
            Spacer(Modifier.width(10.dp))
            Column {
                Text("笔记", fontSize = 17.sp, fontWeight = FontWeight.Bold)
                Text("当前百度账号：$accountName", fontSize = 10.sp, color = TextTertiary)
            }
            Spacer(Modifier.weight(1f))
            // 顶栏放大镜：聚焦下方搜索框
            IconButton(onClick = { searchFocus.requestFocus() }) {
                Icon(Icons.Default.Search, contentDescription = "搜索", tint = TextPrimary)
            }
        }
        // 标题栏分割线
        TopBarDivider()
        Spacer(Modifier.height(4.dp))

        // 笔记本 chips
        Row(
            Modifier.fillMaxWidth()
                .horizontalScroll(notebookScroll)
                .padding(horizontal = 16.dp),
            horizontalArrangement = Arrangement.spacedBy(8.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            notebooks.forEach { nb ->
                // 选了标签时笔记本一律不高亮（此时 selectedNotebook 已重置为「全部笔记」）
                val on = selectedTag == null && selectedNotebook == nb
                // 只有自建笔记本能删：「全部笔记」和内置四个不响应长按（与 Mac 一致）
                val deletable = nb in customNotebooks
                Box {
                    Text(
                        nb,
                        Modifier
                            .clip(RoundedCornerShape(999.dp))
                            .background(if (on) Green else Color(0xFFF0F0F2))
                            .combinedClickable(
                                onClick = {
                                    selectedNotebook = nb
                                    selectedTag = null
                                },
                                onLongClick = if (deletable) ({ menuNotebook = nb }) else null
                            )
                            .padding(horizontal = 13.dp, vertical = 6.dp),
                        color = if (on) Color.White else TextSecondary,
                        fontSize = 12.sp,
                        maxLines = 1,
                        softWrap = false,
                        fontWeight = if (on) FontWeight.SemiBold else FontWeight.Medium
                    )
                    DropdownMenu(
                        expanded = menuNotebook == nb,
                        onDismissRequest = { menuNotebook = null },
                        containerColor = Color.White
                    ) {
                        DropdownMenuItem(
                            text = { Text("删除笔记本", fontSize = 13.sp, color = Red) },
                            onClick = {
                                menuNotebook = null
                                pendingDeleteNotebook = nb
                            }
                        )
                    }
                }
            }
            // 行尾「＋」：新建笔记本
            Box(
                Modifier
                    .size(30.dp)
                    .clip(CircleShape)
                    .background(Color(0xFFF0F0F2))
                    .clickable { showNewNotebook = true },
                contentAlignment = Alignment.Center
            ) {
                Icon(Icons.Default.Add, contentDescription = "新建笔记本", tint = TextSecondary, modifier = Modifier.size(16.dp))
            }
        }

        // 标签横向 chips：紧跟在笔记本下面，与笔记本互斥；蓝色系，和绿色的笔记本区分开
        if (tagCounts.isNotEmpty()) {
            Row(
                Modifier.fillMaxWidth()
                    .horizontalScroll(rememberScrollState())
                    .padding(horizontal = 16.dp)
                    .padding(top = 8.dp),
                horizontalArrangement = Arrangement.spacedBy(8.dp)
            ) {
                tagCounts.keys.forEach { tag ->
                    val on = selectedTag == tag
                    Text(
                        "# $tag",
                        Modifier
                            .clip(RoundedCornerShape(999.dp))
                            .background(if (on) BaiduBlue else BaiduTint)
                            .clickable {
                                // 再点一次已选中的标签 = 取消筛选
                                if (on) {
                                    selectedTag = null
                                } else {
                                    selectedTag = tag
                                    selectedNotebook = Notebooks.ALL
                                }
                            }
                            .padding(horizontal = 12.dp, vertical = 5.dp),
                        color = if (on) Color.White else BaiduDeep,
                        fontSize = 12.sp,
                        maxLines = 1,
                        softWrap = false,
                        fontWeight = if (on) FontWeight.SemiBold else FontWeight.Medium
                    )
                }
            }
        }

        // 搜索框：紧凑胶囊（M3 OutlinedTextField 固定 56dp 高，太占地方，改用 BasicTextField 自绘，
        // 整体高度约 34dp；占位文字叠在输入框下层实现 placeholder 效果）
        Row(
            Modifier
                .fillMaxWidth()
                .padding(horizontal = 16.dp, vertical = 6.dp)
                .clip(RoundedCornerShape(9.dp))
                .background(Color(0xFFF0F0F2))
                .padding(horizontal = 10.dp, vertical = 7.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Icon(Icons.Default.Search, contentDescription = null, tint = TextQuaternary, modifier = Modifier.size(15.dp))
            Spacer(Modifier.width(6.dp))
            Box(Modifier.weight(1f)) {
                if (query.isBlank()) {
                    Text("搜索笔记、标签…", fontSize = 13.sp, color = TextQuaternary)
                }
                BasicTextField(
                    value = query,
                    onValueChange = { query = it },
                    singleLine = true,
                    textStyle = TextStyle(fontSize = 13.sp, color = TextPrimary),
                    cursorBrush = SolidColor(Green),
                    modifier = Modifier.fillMaxWidth().focusRequester(searchFocus)
                )
            }
            if (query.isNotEmpty()) {
                Icon(
                    Icons.Default.Close, contentDescription = "清空搜索",
                    tint = TextQuaternary,
                    modifier = Modifier.size(15.dp).clickable { query = "" }
                )
            }
        }

        // 排序
        Row(Modifier.padding(horizontal = 16.dp, vertical = 2.dp), horizontalArrangement = Arrangement.spacedBy(14.dp)) {
            listOf("updated" to "最近更新", "created" to "按创建", "title" to "按标题").forEach { (key, label) ->
                val on = sort == key
                Text(
                    label,
                    Modifier
                        .clickable { sort = key }
                        .padding(bottom = 6.dp),
                    color = if (on) Green else TextTertiary,
                    fontSize = 12.sp,
                    fontWeight = if (on) FontWeight.SemiBold else FontWeight.Normal
                )
            }
        }
        HorizontalDivider(color = LineSoft)

        // 列表与 FAB 放在同一个 weight(1f) 的 Box 内：
        // 若 Box(fillMaxSize) 作为非 weight 子项排在加权 LazyColumn 之后，
        // 它会先于 weight 子项测量并吃掉全部剩余高度，导致列表高度为 0（永不显示）
        Box(Modifier.weight(1f)) {
            LazyColumn(Modifier.fillMaxSize()) {
                item {
                    // 固定卡片：快同步 / 快任务
                    Row(Modifier.padding(horizontal = 16.dp, vertical = 12.dp), horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                        QuickCard(
                            modifier = Modifier.weight(1f),
                            icon = "⇄", tint = Green, bg = GreenTint,
                            title = "快同步", sub = "多设备共享",
                            onClick = onOpenQuickSync
                        )
                        QuickCard(
                            modifier = Modifier.weight(1f),
                            icon = "✓", tint = Amber, bg = AmberTint,
                            title = "快任务", sub = "待完成 · 进行中",
                            badge = overdueCount,
                            onClick = onOpenTodo
                        )
                    }
                }
                items(filtered, key = { it.id }) { note ->
                    NoteCard(note, onClick = { onOpenNote(note.id) })
                }
            }

            FloatingActionButton(
                onClick = { onNewNote(selectedNotebook) },
                modifier = Modifier.align(Alignment.BottomEnd).padding(20.dp).size(50.dp),
                shape = CircleShape,
                containerColor = Green,
                contentColor = Color.White
            ) {
                // + 号加一点立体感：底层深色描边阴影轻微下移，上层白色盖住，形成浮雕
                Box(contentAlignment = Alignment.Center) {
                    Icon(
                        Icons.Default.Add, contentDescription = null,
                        modifier = Modifier.size(24.dp).offset(y = 1.dp),
                        tint = Color(0x66000000)
                    )
                    Icon(
                        Icons.Default.Add, contentDescription = "新建笔记",
                        modifier = Modifier.size(24.dp),
                        tint = Color.White
                    )
                }
            }
        }
    }

    pendingDeleteNotebook?.let { name ->
        val count = notes.count { !it.purged && !it.isTrashed && it.id != QUICK_SYNC_NOTE_ID && it.notebook == name }
        DeleteNotebookDialog(
            name = name,
            noteCount = count,
            onDismiss = { pendingDeleteNotebook = null },
            onConfirm = {
                // 与 Mac 一致：先把笔记转到「全部笔记」，再移除笔记本；笔记本身不会丢
                NoteStore.moveNotes(fromNotebook = name, toNotebook = Notebooks.ALL)
                NotebookStore.remove(name)
                if (selectedNotebook == name) selectedNotebook = Notebooks.ALL
                notebookVersion++
                pendingDeleteNotebook = null
            }
        )
    }

    if (showNewNotebook) {
        NewNotebookDialog(
            onDismiss = { showNewNotebook = false },
            onCreate = { input ->
                val result = NotebookStore.add(input)
                if (result.isSuccess) {
                    // 成功：关窗、刷新列表、选中新笔记本，并把 chips 滚到最右让它可见
                    val created = input.trim()
                    notebookVersion++
                    selectedNotebook = created
                    selectedTag = null
                    showNewNotebook = false
                    scrollNotebooksToEnd = true
                    null
                } else {
                    result.exceptionOrNull()?.message ?: "创建失败"
                }
            }
        )
    }
}

/** 删除笔记本二次确认：文案与 Mac 一致，红色「删除」强调危险操作。 */
@Composable
private fun DeleteNotebookDialog(name: String, noteCount: Int, onDismiss: () -> Unit, onConfirm: () -> Unit) {
    Dialog(onDismissRequest = onDismiss) {
        Surface(
            shape = RoundedCornerShape(14.dp),
            color = Color.White,
            shadowElevation = 6.dp,
            modifier = Modifier.fillMaxWidth()
        ) {
            Column {
                Text(
                    "删除笔记本「$name」？",
                    Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 11.dp),
                    fontSize = 14.sp,
                    fontWeight = FontWeight.Bold,
                    color = TextPrimary
                )
                HorizontalDivider(thickness = 0.75.dp, color = LineHair)
                Text(
                    if (noteCount > 0) "该笔记本下有 $noteCount 篇笔记，删除后会自动转移到「全部笔记」，笔记本身不会丢失。"
                    else "该笔记本下暂无笔记，删除后会从笔记本列表中移除。",
                    Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 18.dp),
                    fontSize = 13.sp,
                    color = TextSecondary,
                    lineHeight = 20.sp
                )
                HorizontalDivider(thickness = 0.5.dp, color = LineHair)
                Row(
                    Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 6.dp),
                    horizontalArrangement = Arrangement.End
                ) {
                    TextButton(onClick = onDismiss) { Text("取消", color = TextSecondary) }
                    TextButton(onClick = onConfirm) {
                        Text("删除", color = Red, fontWeight = FontWeight.SemiBold)
                    }
                }
            }
        }
    }
}

/** 新建笔记本：输入名称；校验（空 / 重名 / 超长 / 内置名）复用 [NotebookStore.add]，错误就地提示。 */
@Composable
private fun NewNotebookDialog(onDismiss: () -> Unit, onCreate: (String) -> String?) {
    var name by remember { mutableStateOf("") }
    var error by remember { mutableStateOf<String?>(null) }
    val focus = remember { FocusRequester() }
    LaunchedEffect(Unit) { focus.requestFocus() }

    fun submit() {
        error = onCreate(name)
    }

    Dialog(onDismissRequest = onDismiss) {
        Surface(
            shape = RoundedCornerShape(14.dp),
            color = Color.White,
            shadowElevation = 6.dp,
            modifier = Modifier.fillMaxWidth()
        ) {
            Column {
                Text(
                    "新建笔记本",
                    Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 11.dp),
                    fontSize = 14.sp,
                    fontWeight = FontWeight.Bold,
                    color = TextPrimary
                )
                HorizontalDivider(thickness = 0.75.dp, color = LineHair)
                Column(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 14.dp)) {
                    Box(
                        Modifier
                            .fillMaxWidth()
                            .clip(RoundedCornerShape(9.dp))
                            .background(Color(0xFFF0F0F2))
                            .padding(horizontal = 12.dp, vertical = 10.dp)
                    ) {
                        if (name.isEmpty()) {
                            Text("笔记本名称（最多 12 个字）", fontSize = 13.sp, color = TextQuaternary)
                        }
                        BasicTextField(
                            value = name,
                            onValueChange = { name = it; error = null },
                            singleLine = true,
                            textStyle = TextStyle(fontSize = 13.sp, color = TextPrimary),
                            cursorBrush = SolidColor(Green),
                            keyboardOptions = KeyboardOptions(imeAction = ImeAction.Done),
                            keyboardActions = KeyboardActions(onDone = { submit() }),
                            modifier = Modifier.fillMaxWidth().focusRequester(focus)
                        )
                    }
                    Spacer(Modifier.height(8.dp))
                    Text(
                        error ?: "新笔记本会出现在笔记本列表中，编辑笔记时也能把笔记切换过去。",
                        fontSize = 11.sp,
                        lineHeight = 16.sp,
                        color = if (error != null) Red else TextTertiary
                    )
                }
                HorizontalDivider(thickness = 0.5.dp, color = LineHair)
                Row(
                    Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 6.dp),
                    horizontalArrangement = Arrangement.End
                ) {
                    TextButton(onClick = onDismiss) { Text("取消", color = TextSecondary) }
                    TextButton(onClick = { submit() }) {
                        Text("创建", color = Green, fontWeight = FontWeight.SemiBold)
                    }
                }
            }
        }
    }
}

@Composable
private fun QuickCard(
    modifier: Modifier = Modifier,
    icon: String, tint: Color, bg: Color, title: String, sub: String, badge: Int? = null,
    onClick: () -> Unit
) {
    Box(modifier) {
        Row(
            Modifier
                .fillMaxWidth()
                .clip(RoundedCornerShape(12.dp))
                .background(Color.White)
                .clickable(onClick = onClick)
                .padding(11.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Box(
                Modifier.size(34.dp).background(bg, RoundedCornerShape(9.dp)),
                contentAlignment = Alignment.Center
            ) { Text(icon, color = tint, fontSize = 18.sp, fontWeight = FontWeight.Bold) }
            Spacer(Modifier.width(9.dp))
            Column {
                Text(title, fontSize = 12.sp, fontWeight = FontWeight.SemiBold, color = TextPrimary)
                Text(sub, fontSize = 10.sp, color = TextQuaternary)
            }
        }
        // 逾期数量角标放在卡片内部右上角（badge 为 null 或 0 时不渲染）
        OverdueCountBadge(badge ?: 0, Modifier.align(Alignment.TopEnd).padding(top = 7.dp, end = 9.dp))
    }
}

// 摘要缓存：snippet 每次都要整篇扫描 HTML；切 tab 会重建整个列表，进程级缓存避免重复解析。
// key 含 updatedAt 与正文长度，笔记一改就自然失效。
private val snippetCache = object : LinkedHashMap<String, String>(256, 0.75f, true) {
    override fun removeEldestEntry(eldest: MutableMap.MutableEntry<String, String>?) = size > 500
}

private fun cachedSnippet(note: Note): String {
    val key = "${note.id}|${note.updatedAt}|${note.body.length}|${note.contentFormat}"
    synchronized(snippetCache) { snippetCache[key]?.let { return it } }
    val s = note.snippet
    synchronized(snippetCache) { snippetCache[key] = s }
    return s
}

@Composable
private fun NoteCard(note: Note, onClick: () -> Unit) {
    Column(
        Modifier
            .fillMaxWidth()
            .clickable(onClick = onClick)
            .padding(horizontal = 16.dp, vertical = 13.dp)
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            if (note.pinned) {
                Icon(Icons.Default.PushPin, contentDescription = "置顶", tint = Amber, modifier = Modifier.size(12.dp))
                Spacer(Modifier.width(4.dp))
            }
            Text(
                note.title.ifBlank { "无标题" },
                fontSize = 14.sp, fontWeight = FontWeight.SemiBold, color = TextPrimary,
                maxLines = 1, overflow = TextOverflow.Ellipsis
            )
        }
        Spacer(Modifier.height(4.dp))
        Text(
            cachedSnippet(note), fontSize = 12.sp, color = TextTertiary,
            maxLines = 2, overflow = TextOverflow.Ellipsis, lineHeight = 17.sp
        )
        Spacer(Modifier.height(8.dp))
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(displayTime(note.updatedAt), fontSize = 11.sp, color = TextQuaternary)
            note.tags.take(2).forEach {
                Spacer(Modifier.width(6.dp))
                Text(
                    it,
                    Modifier.clip(RoundedCornerShape(999.dp)).background(Color(0xFFEFEFF1)).padding(horizontal = 8.dp, vertical = 2.dp),
                    color = TextTertiary, fontSize = 10.sp
                )
            }
        }
        Spacer(Modifier.height(13.dp))
        HorizontalDivider(color = LineSoft)
    }
}
