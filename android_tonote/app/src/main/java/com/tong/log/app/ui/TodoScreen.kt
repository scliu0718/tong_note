package com.tong.log.app.ui

import android.app.DatePickerDialog
import android.content.DialogInterface
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.widget.Toast
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import java.io.File
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyListScope
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.outlined.DeleteOutline
import androidx.compose.material.icons.filled.EditNote
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import com.tong.log.app.data.sync.SyncEngine
import androidx.compose.ui.draw.rotate
import androidx.compose.animation.core.tween
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.LinearEasing
import androidx.compose.ui.draw.drawWithContent
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import com.tong.log.app.data.codec.ImagePathScanner
import com.tong.log.app.data.codec.RichTextHTMLCodec
import com.tong.log.app.data.model.Todo
import com.tong.log.app.data.model.TodoStatus
import com.tong.log.app.data.store.AttachmentStore
import com.tong.log.app.data.store.TodoStore
import com.tong.log.app.data.store.isOverdue
import com.tong.log.app.ui.theme.*
import kotlinx.coroutines.launch
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.time.format.DateTimeFormatter

private val DateFmt = DateTimeFormatter.ofPattern("yyyy-MM-dd")

/** ISO 字符串 ↔ 本地日期（TodoStore.setPlannedAt 收的是 LocalDate）。 */
private fun isoToDate(iso: String?): LocalDate? = iso?.let {
    runCatching { Instant.parse(it).atZone(ZoneId.systemDefault()).toLocalDate() }.getOrNull()
}

@Composable
fun TodoScreen(onSync: () -> Unit) {
    val todos by TodoStore.todos.collectAsState()
    val scope = rememberCoroutineScope()
    var filter by remember { mutableStateOf("all") }
    var newTitle by remember { mutableStateOf("") }

    // 编辑 / 详细新建共用的对话框状态：null = 关闭
    var editingTodo by remember { mutableStateOf<Todo?>(null) }
    var showCreateDetail by remember { mutableStateOf(false) }
    // 待删除的任务：非 null 时弹出二次确认框
    var pendingDelete by remember { mutableStateOf<Todo?>(null) }

    val active = TodoStore.active // todos 已 collect，变化时重组；排序规则在 TodoStore.active
    val todo = active.filter { it.status == TodoStatus.TODO }
    val doing = active.filter { it.status == TodoStatus.DOING }
    val done = active.filter { it.status == TodoStatus.DONE }
    val terminated = active.filter { it.status == TodoStatus.TERMINATED }
    val overdueCount = active.count { it.isOverdue }

    fun submitQuick() {
        val t = newTitle.trim()
        if (t.isEmpty()) return
        scope.launch { TodoStore.add(t) }
        newTitle = ""
    }

    // 键盘弹起时由 imePadding 把整页抬到键盘上方，底部输入行不再被遮住。
    // Android 15 边到边后 adjustResize 不缩窗口，必须显式消费 IME inset。
    Column(Modifier.fillMaxSize().background(Bg).imePadding()) {
        Row(
            Modifier.fillMaxWidth().padding(horizontal = 14.dp, vertical = 7.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Text("任务", fontSize = 17.sp, fontWeight = FontWeight.Bold)
            Spacer(Modifier.weight(1f))
            // 同步中（含切页自动拉取、保存后上传）图标持续旋转，停了就回正
            val syncing = SyncEngine.status.collectAsState().value.isSyncing
            val spin = rememberInfiniteTransition(label = "sync-spin")
            val angle by spin.animateFloat(
                initialValue = 0f,
                targetValue = 360f,
                animationSpec = infiniteRepeatable(tween(900, easing = LinearEasing)),
                label = "sync-angle"
            )
            IconButton(onClick = onSync) {
                Icon(
                    Icons.Default.Refresh,
                    contentDescription = "同步",
                    tint = if (syncing) Green else TextPrimary,
                    modifier = Modifier.rotate(if (syncing) angle else 0f)
                )
            }
        }
        // 标题栏分割线
        TopBarDivider()
        Spacer(Modifier.height(4.dp))
        Row(Modifier.padding(start = 16.dp, end = 16.dp, top = 0.dp, bottom = 4.dp), verticalAlignment = Alignment.CenterVertically) {
            Text(
                "待完成 ${todo.size} · 进行中 ${doing.size} · 已完成 ${done.size}" +
                    (if (terminated.isNotEmpty()) " · 终止 ${terminated.size}" else "") +
                    (if (overdueCount > 0) " · 逾期 $overdueCount" else ""),
                fontSize = 12.sp, color = TextTertiary
            )
            Spacer(Modifier.weight(1f))
            Text("上次同步", fontSize = 11.sp, color = TextQuaternary)
        }
        Row(Modifier.padding(horizontal = 16.dp, vertical = 8.dp), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            listOf("all" to "全部", "open" to "仅未完成").forEach { (key, label) ->
                val on = filter == key
                Text(
                    label,
                    Modifier
                        .clip(RoundedCornerShape(999.dp))
                        .background(if (on) Green else Color(0xFFF0F0F2))
                        .clickable { filter = key }
                        .padding(horizontal = 13.dp, vertical = 5.dp),
                    color = if (on) Color.White else TextSecondary,
                    fontSize = 12.sp
                )
            }
        }

        LazyColumn(Modifier.weight(1f).padding(horizontal = 16.dp)) {
            todoGroup(
                "待完成", todo, Color(0xFFE5484D), show = true, isDoneGroup = false,
                onEdit = { editingTodo = it }, onDelete = { pendingDelete = it }
            )
            todoGroup(
                "进行中", doing, Amber, show = true, isDoneGroup = false,
                onEdit = { editingTodo = it }, onDelete = { pendingDelete = it }
            )
            if (filter == "all") {
                todoGroup("已完成", done, Green, show = true, isDoneGroup = true, onEdit = { editingTodo = it }, onDelete = { pendingDelete = it })
                todoGroup("已终止", terminated, TextQuaternary, show = true, isDoneGroup = true, onEdit = { editingTodo = it }, onDelete = { pendingDelete = it })
            }
            item { Spacer(Modifier.height(8.dp)) }
        }

        // 快捷添加：38dp 胶囊输入行，比原来的 OutlinedTextField（56dp+）矮一截
        Row(
            Modifier
                .fillMaxWidth()
                .background(Bg)
                .padding(horizontal = 16.dp, vertical = 6.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Row(
                Modifier
                    .weight(1f)
                    .height(38.dp)
                    .clip(RoundedCornerShape(10.dp))
                    .background(Color.White)
                    .border(1.dp, Line, RoundedCornerShape(10.dp))
                    .padding(horizontal = 10.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                Icon(Icons.Default.Add, contentDescription = null, tint = Green, modifier = Modifier.size(16.dp))
                Spacer(Modifier.width(8.dp))
                BasicTextField(
                    value = newTitle,
                    onValueChange = { newTitle = it },
                    modifier = Modifier.weight(1f),
                    singleLine = true,
                    textStyle = LocalTextStyle.current.copy(fontSize = 13.sp, color = TextPrimary),
                    keyboardOptions = KeyboardOptions(imeAction = ImeAction.Done),
                    keyboardActions = KeyboardActions(onDone = { submitQuick() }),
                    decorationBox = { inner ->
                        if (newTitle.isEmpty()) {
                            Text("添加任务，回车确认", fontSize = 12.sp, color = TextQuaternary)
                        }
                        inner()
                    }
                )
                // 详细新建：可一并设置日期与备注
                Icon(
                    Icons.Default.EditNote,
                    contentDescription = "详细添加",
                    tint = TextTertiary,
                    modifier = Modifier
                        .size(24.dp)
                        .clip(RoundedCornerShape(6.dp))
                        .clickable { showCreateDetail = true }
                )
            }
            Spacer(Modifier.width(8.dp))
            Button(
                onClick = { submitQuick() },
                colors = ButtonDefaults.buttonColors(containerColor = Green),
                contentPadding = PaddingValues(horizontal = 14.dp, vertical = 6.dp),
                modifier = Modifier.height(34.dp)
            ) { Text("添加", fontSize = 12.sp) }
        }
    }

    // 编辑已有任务：标题 / 备注 / 计划日期。
    // 已完成 / 已终止是终态：仍可点开查看，但内容只读（与 Mac 端一致）。
    editingTodo?.let { t ->
        val readOnly = t.status == TodoStatus.DONE || t.status == TodoStatus.TERMINATED
        TodoEditDialog(
            title = if (readOnly) "查看任务" else "编辑任务",
            initialTitle = t.title,
            initialNotes = t.notes,
            initialDate = isoToDate(t.plannedAt),
            confirmLabel = "保存",
            todoId = t.id,
            readOnly = readOnly,
            onDismiss = { editingTodo = null },
            onConfirm = { title, notes, date ->
                // 只读态隐藏保存按钮；这里再兜一层，避免状态在弹窗打开期间突变后误写
                if (!readOnly) {
                    TodoStore.updateTitle(t.id, title)
                    TodoStore.setNotes(t.id, notes)
                    TodoStore.setPlannedAt(t.id, date)
                }
                editingTodo = null
            }
        )
    }

    // 删除二次确认：非 null 时弹出
    pendingDelete?.let { t ->
        TodoDeleteDialog(
            title = t.title,
            onDismiss = { pendingDelete = null },
            onConfirm = {
                TodoStore.remove(t.id)
                pendingDelete = null
            }
        )
    }

    // 详细新建：一次把标题、备注、日期都填好
    if (showCreateDetail) {
        TodoEditDialog(
            title = "新建任务",
            initialTitle = newTitle,
            initialNotes = "",
            initialDate = null,
            confirmLabel = "创建",
            todoId = null,
            onDismiss = { showCreateDetail = false },
            onConfirm = { title, notes, date ->
                if (title.isNotBlank()) {
                    val t = TodoStore.add(title)
                    if (notes.isNotBlank()) TodoStore.setNotes(t.id, notes)
                    if (date != null) TodoStore.setPlannedAt(t.id, date)
                    newTitle = ""
                }
                showCreateDetail = false
            }
        )
    }
}

private fun LazyListScope.todoGroup(
    title: String, list: List<Todo>, color: Color, show: Boolean, isDoneGroup: Boolean,
    onEdit: (Todo) -> Unit, onDelete: (Todo) -> Unit
) {
    if (!show || list.isEmpty()) return
    item {
        Row(Modifier.padding(vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
            // 分类图标与 Mac 对齐：13% 透明度的主题色底圆 + 8dp 实心点（浅粉底 + 红点）
            Box(
                Modifier
                    .size(20.dp)
                    .background(color.copy(alpha = 0.13f), CircleShape),
                contentAlignment = Alignment.Center
            ) {
                Box(Modifier.size(8.dp).background(color, CircleShape))
            }
            Spacer(Modifier.width(8.dp))
            Text(title, fontSize = 12.sp, fontWeight = FontWeight.Bold, color = TextSecondary)
            Spacer(Modifier.width(4.dp))
            Text("${list.size}", fontSize = 11.sp, color = TextQuaternary)
        }
    }
    items(list.size) { idx ->
        val t = list[idx]
        Row(
            Modifier
                .fillMaxWidth()
                .padding(vertical = 4.dp)
                .clip(RoundedCornerShape(11.dp))
                .background(if (t.isOverdue) RedTint else Color.White)
                .then(if (t.isOverdue) Modifier.border(0.75.dp, RedLine, RoundedCornerShape(11.dp)) else Modifier)
                .clickable { onEdit(t) }
                .padding(start = 12.dp, top = 11.dp, end = 9.dp, bottom = 11.dp),
            verticalAlignment = Alignment.Top
        ) {
            // 状态图标：待完成=红色播放三角、进行中=黄色指针、已完成=绿色对勾、已终止=灰色减号
            StatusRing(
                t.status,
                Modifier
                    .size(17.dp)
                    // 去掉点击涟漪：默认 indication 会按方形 bounds 画阴影，圆圈上很突兀
                    .clickable(interactionSource = null, indication = null) { TodoStore.toggleDone(t.id) }
            )
            Spacer(Modifier.width(11.dp))
            Column(Modifier.weight(1f)) {
                Text(
                    t.title,
                    fontSize = 13.sp,
                    color = if (isDoneGroup) TextQuaternary else TextPrimary,
                    textDecoration = if (isDoneGroup) TextDecoration.LineThrough else TextDecoration.None
                )
                val snippet = todoNotesSnippet(t.notes)
                if (snippet.isNotEmpty()) {
                    Spacer(Modifier.height(3.dp))
                    Text(snippet, fontSize = 11.sp, color = TextTertiary, maxLines = 2)
                }
                if (t.plannedAt != null) {
                    Spacer(Modifier.height(3.dp))
                    Text(
                        displayTime(t.plannedAt) + if (t.isOverdue) " · 逾期" else "",
                        Modifier
                            .clip(RoundedCornerShape(999.dp))
                            .background(if (t.isOverdue) Color.White else Color(0xFFF0F0F2))
                            .padding(horizontal = 8.dp, vertical = 2.dp),
                        color = if (t.isOverdue) Red else TextTertiary,
                        fontSize = 10.sp,
                        fontWeight = if (t.isOverdue) FontWeight.SemiBold else FontWeight.Normal
                    )
                }
            }
            // 第一个按钮距内容 +2dp
            Spacer(Modifier.width(2.dp))
            when (t.status) {
                TodoStatus.TODO -> ActionChip("开始", GreenTint, GreenHover) { TodoStore.setStatus(t.id, TodoStatus.DOING) }
                TodoStatus.DOING -> ActionChip("完成", AmberTint, Color(0xFFB9770A)) { TodoStore.setStatus(t.id, TodoStatus.DONE) }
                TodoStatus.TERMINATED -> ActionChip("恢复", Color(0xFFF0F0F2), TextTertiary) { TodoStore.setStatus(t.id, TodoStatus.TODO) }
            }
            if (t.status == TodoStatus.TODO || t.status == TodoStatus.DOING) {
                Spacer(Modifier.width(4.dp))
                // 终止：灰色底 + 细边框，明确标识为可点按钮
                ActionChip(
                    "终止", Color(0xFFF5F5F7), TextTertiary, bordered = true
                ) { TodoStore.setStatus(t.id, TodoStatus.TERMINATED) }
            }
            Spacer(Modifier.width(4.dp))
            Icon(
                Icons.Outlined.DeleteOutline,
                contentDescription = "删除任务",
                tint = TextQuaternary,
                modifier = Modifier
                    .size(20.dp)
                    .clip(RoundedCornerShape(5.dp))
                    .clickable { onDelete(t) }
            )
        }
    }
}

@Composable
private fun ActionChip(label: String, bg: Color, fg: Color, bordered: Boolean = false, onClick: () -> Unit) {
    Text(
        label,
        Modifier
            .clip(RoundedCornerShape(7.dp))
            .background(bg)
            .then(if (bordered) Modifier.border(1.dp, Line, RoundedCornerShape(7.dp)) else Modifier)
            .clickable(onClick = onClick)
            .padding(horizontal = 10.dp, vertical = 4.dp),
        color = fg, fontSize = 11.sp, fontWeight = FontWeight.SemiBold
    )
}

/** 删除二次确认框：与编辑弹窗同款紧凑样式，删除按钮用红色强调危险操作。 */
@Composable
private fun TodoDeleteDialog(title: String, onDismiss: () -> Unit, onConfirm: () -> Unit) {
    Dialog(onDismissRequest = onDismiss) {
        Surface(
            shape = RoundedCornerShape(14.dp),
            color = Color.White,
            shadowElevation = 6.dp,
            modifier = Modifier.fillMaxWidth()
        ) {
            Column {
                Text(
                    "删除任务",
                    Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 11.dp),
                    fontSize = 14.sp,
                    fontWeight = FontWeight.Bold,
                    color = TextPrimary
                )
                HorizontalDivider(thickness = 0.75.dp, color = LineHair)
                Text(
                    "确定删除「$title」吗？\n删除后不可恢复。",
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

/**
 * 限高 + 可滚动 + 右侧滚动条：内容超过 [maxHeight] 时才出现滚动条。
 * 输入框本身不限行数、随内容长高，由外层滚动，光标移动时外层会自动跟随。
 */
@Composable
private fun ScrollableField(maxHeight: Dp, content: @Composable () -> Unit) {
    val state = rememberScrollState()
    val thumbColor = TextQuaternary
    Box(
        Modifier
            .heightIn(max = maxHeight)
            .drawWithContent {
                drawContent()
                val max = state.maxValue
                if (max > 0) {
                    // 滚动条画在输入框边框内侧：上方让出浮动标签的半高，右侧让出边框和内边距
                    val inTop = 12.dp.toPx()
                    val inBottom = 6.dp.toPx()
                    val inRight = 4.dp.toPx()
                    val track = (size.height - inTop - inBottom).coerceAtLeast(1f)
                    val thumbH = (track * size.height / (size.height + max)).coerceAtLeast(24.dp.toPx()).coerceAtMost(track)
                    val top = inTop + state.value.toFloat() / max * (track - thumbH)
                    val w = 2.dp.toPx()
                    drawRoundRect(
                        color = thumbColor,
                        topLeft = Offset(size.width - w - inRight, top),
                        size = Size(w, thumbH),
                        cornerRadius = CornerRadius(w / 2, w / 2)
                    )
                }
            }
            .verticalScroll(state)
    ) { content() }
}

/** 自绘紧凑对话框：标题 / 备注 / 计划日期，避免 M3 AlertDialog 的固定大 padding。 */
@Composable
private fun TodoEditDialog(
    title: String,
    initialTitle: String,
    initialNotes: String,
    initialDate: LocalDate?,
    confirmLabel: String,
    todoId: String?,
    onDismiss: () -> Unit,
    onConfirm: (String, String, LocalDate?) -> Unit,
    readOnly: Boolean = false
) {
    val context = LocalContext.current
    var taskTitle by remember { mutableStateOf(initialTitle) }
    // 备注在 Mac 端是富文本 HTML（<ol><li>1</li></ol>、<img src="attachments/...">），
    // 这里拆成「纯文本 + 图片」两部分展示；保存时文本与图片均未改动则原样保留 HTML，
    // 避免有损转换写坏 Mac 原稿。
    val initialText = remember(initialNotes) { RichTextHTMLCodec.plainText(initialNotes) }
    val initialImages = remember(initialNotes) { ImagePathScanner.attachmentPaths(initialNotes) }
    var images by remember(initialNotes) { mutableStateOf(initialImages) }
    var notes by remember { mutableStateOf(initialText) }
    var date by remember { mutableStateOf(initialDate) }

    // 添加图片：导入到 attachments/<todoId>/ 下，并把相对路径加进图片列表
    val pickImage = rememberLauncherForActivityResult(ActivityResultContracts.GetContent()) { uri ->
        val id = todoId
        if (id != null && uri != null) {
            val rel = AttachmentStore.importImage(uri, id)
            if (rel == null) {
                Toast.makeText(context, "图片导入失败，请换一张重试", Toast.LENGTH_SHORT).show()
            } else {
                images = images + rel
            }
        }
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
                    title,
                    Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 11.dp),
                    fontSize = 14.sp,
                    fontWeight = FontWeight.Bold,
                    color = TextPrimary
                )
                HorizontalDivider(thickness = 0.75.dp, color = LineHair)
                Column(Modifier.padding(horizontal = 16.dp, vertical = 8.dp)) {
                    if (readOnly) {
                        Text(
                            "只读 · 已完成 / 已终止的任务不可编辑",
                            Modifier
                                .fillMaxWidth()
                                .clip(RoundedCornerShape(6.dp))
                                .background(Color(0xFFF2F2F4))
                                .padding(horizontal = 10.dp, vertical = 6.dp),
                            color = TextQuaternary,
                            fontSize = 11.sp
                        )
                        Spacer(Modifier.height(8.dp))
                    }
                    // 标题过长时自动换行，超过最大高度后可滚动并显示滚动条
                    ScrollableField(maxHeight = 96.dp) {
                        OutlinedTextField(
                            value = taskTitle,
                            onValueChange = { taskTitle = it.replace("\n", "") },
                            label = { Text("标题", fontSize = 12.sp) },
                            readOnly = readOnly,
                            modifier = Modifier.fillMaxWidth()
                        )
                    }
                    Spacer(Modifier.height(8.dp))
                    ScrollableField(maxHeight = 140.dp) {
                        OutlinedTextField(
                            value = notes,
                            onValueChange = { notes = it },
                            label = { Text("备注", fontSize = 12.sp) },
                            minLines = 2,
                            readOnly = readOnly,
                            modifier = Modifier.fillMaxWidth()
                        )
                    }
                    // 备注图片单独展示：缩略图 + 删除 + 上传，点击预览大图
                    NotesImageStrip(
                        images = images,
                        canEdit = !readOnly && todoId != null,
                        onRemove = { rel -> images = images - rel },
                        onAdd = { pickImage.launch("image/*") }
                    )
                    Spacer(Modifier.height(8.dp))
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Text("计划日期", fontSize = 12.sp, color = TextSecondary)
                        Spacer(Modifier.width(12.dp))
                        Text(
                            date?.format(DateFmt) ?: "未设置",
                            Modifier
                                .clip(RoundedCornerShape(6.dp))
                            .background(GreenTint)
                            .clickable(enabled = !readOnly) {
                                    val base = date ?: LocalDate.now()
                                    val picker = DatePickerDialog(
                                        context,
                                        { _, y, m, d -> date = LocalDate.of(y, m + 1, d) },
                                        base.year, base.monthValue - 1, base.dayOfMonth
                                    )
                                    // 原生默认「取消」(NEGATIVE)在左、「确定」(POSITIVE)在右；
                                    // 交换文字与回调：左侧显示「确定」执行提交，右侧显示「取消」不改动
                                    picker.setButton(DialogInterface.BUTTON_NEGATIVE, "确定") { _, _ ->
                                        val dp = picker.datePicker
                                        date = LocalDate.of(dp.year, dp.month + 1, dp.dayOfMonth)
                                    }
                                    picker.setButton(DialogInterface.BUTTON_POSITIVE, "取消") { _, _ -> }
                                    picker.show()
                                }
                                .padding(horizontal = 10.dp, vertical = 4.dp),
                            color = GreenHover,
                            fontSize = 12.sp
                        )
                        if (date != null && !readOnly) {
                            Spacer(Modifier.width(8.dp))
                            Text(
                                "清除",
                                Modifier.clickable { date = null }.padding(horizontal = 6.dp, vertical = 6.dp),
                                color = TextTertiary,
                                fontSize = 12.sp
                            )
                        }
                    }
                }
                HorizontalDivider(thickness = 0.5.dp, color = LineHair)
                Row(
                    Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 6.dp),
                    horizontalArrangement = Arrangement.End
                ) {
                    if (!readOnly) {
                        TextButton(onClick = {
                            // 文本、图片均未变则保留原始 HTML（不丢失 Mac 端格式）；有变化才用「纯文本 + 图片」重组
                            val finalNotes = if (notes == initialText && images == initialImages) initialNotes
                                else notesToHtml(notes, images)
                            onConfirm(taskTitle.trim(), finalNotes, date)
                        }) {
                            Text(confirmLabel, color = GreenHover, fontWeight = FontWeight.SemiBold)
                        }
                    }
                    TextButton(onClick = onDismiss) { Text(if (readOnly) "关闭" else "取消", color = TextSecondary) }
                }
            }
        }
    }
}

/** 列表里备注的摘要：剥标签取纯文本；有图片时在文字后加「[图片]」，与 Mac 端一致。 */
private fun todoNotesSnippet(notes: String): String {
    val text = RichTextHTMLCodec.visibleText(notes).replace('\n', ' ').trim()
    return if (ImagePathScanner.attachmentPaths(notes).isNotEmpty()) "$text[图片]" else text
}

/** 把「纯文本 + 图片」重组成 HTML：文本每行一段 <p>，图片逐个 <img>。 */
private fun notesToHtml(text: String, images: List<String>): String {
    val sb = StringBuilder()
    RichTextHTMLCodec.escapeText(text)
        .split('\n')
        .map { it.trim() }
        .filter { it.isNotEmpty() }
        .forEach { line -> sb.append("<p>").append(line).append("</p>") }
    images.forEach { rel ->
        // 图片必须包在 <p> 里：Mac / Windows 解码器不认块级位置的裸 <img>，会整张丢掉
        sb.append("<p><img src=\"").append(RichTextHTMLCodec.escapeAttribute(rel)).append("\"></p>")
    }
    return sb.toString()
}

/** 备注图片缩略图区：缩略图 + 删除 + 上传入口，点击缩略图预览大图。 */
@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun NotesImageStrip(
    images: List<String>,
    canEdit: Boolean,
    onRemove: (String) -> Unit,
    onAdd: () -> Unit
) {
    if (images.isEmpty() && !canEdit) return
    var preview by remember { mutableStateOf<String?>(null) }
    Column(Modifier.fillMaxWidth().padding(top = 10.dp)) {
        Text("图片", fontSize = 12.sp, color = TextSecondary)
        Spacer(Modifier.height(8.dp))
        FlowRow(
            Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.spacedBy(8.dp),
            verticalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            images.forEach { rel ->
                NotesThumbnail(
                    relPath = rel,
                    showRemove = canEdit,
                    onRemove = { onRemove(rel) },
                    onClick = { preview = rel }
                )
            }
            if (canEdit) {
                AddImageTile(onClick = onAdd)
            }
        }
    }
    preview?.let { rel ->
        ImageViewerDialog(rel, onDismiss = { preview = null })
    }
}

@Composable
private fun NotesThumbnail(
    relPath: String,
    showRemove: Boolean = true,
    onRemove: () -> Unit,
    onClick: () -> Unit
) {
    val bmp = rememberImageBitmap(relPath, maxDim = 360)
    Box(
        Modifier
            .size(52.dp)
            .clip(RoundedCornerShape(8.dp))
            .background(Color(0xFFF7F7F9))
            .clickable(onClick = onClick),
        contentAlignment = Alignment.Center
    ) {
        if (bmp != null) {
            Image(bmp, contentDescription = "备注图片", modifier = Modifier.fillMaxSize(), contentScale = ContentScale.Crop)
        } else {
            Text("无法加载", fontSize = 8.sp, color = TextQuaternary)
        }
        // 右上角删除按钮：往中间收一点、缩小，避免压住图片过多；只读态隐藏
        if (showRemove) {
            Box(
                Modifier
                    .align(Alignment.TopEnd)
                    .offset(x = (-2).dp, y = 2.dp)
                    .size(16.dp)
                    .clip(CircleShape)
                    .background(Color(0xCC000000))
                    .clickable(onClick = onRemove),
                contentAlignment = Alignment.Center
            ) {
                Icon(Icons.Default.Close, contentDescription = "删除图片", tint = Color.White, modifier = Modifier.size(10.dp))
            }
        }
    }
}

/** 添加图片的「+」入口，样式与缩略图一致。 */
@Composable
private fun AddImageTile(onClick: () -> Unit) {
    Box(
        Modifier
            .size(52.dp)
            .clip(RoundedCornerShape(8.dp))
            .background(Color(0xFFFBFBFB))
            .border(1.dp, Line, RoundedCornerShape(8.dp))
            .clickable(onClick = onClick),
        contentAlignment = Alignment.Center
    ) {
        Icon(Icons.Default.Add, contentDescription = "添加图片", tint = TextTertiary, modifier = Modifier.size(22.dp))
    }
}

/** 全屏预览大图，点击任意处关闭。 */
@Composable
private fun ImageViewerDialog(relPath: String, onDismiss: () -> Unit) {
    val bmp = rememberImageBitmap(relPath, maxDim = 2048)
    Dialog(
        onDismissRequest = onDismiss,
        properties = DialogProperties(usePlatformDefaultWidth = false)
    ) {
        Box(
            Modifier
                .fillMaxSize()
                .background(Color(0xE6000000))
                .clickable(onClick = onDismiss),
            contentAlignment = Alignment.Center
        ) {
            if (bmp != null) {
                Image(bmp, contentDescription = "图片预览", modifier = Modifier.fillMaxWidth(), contentScale = ContentScale.Fit)
            } else {
                Text("图片无法加载", color = Color.White, fontSize = 14.sp)
            }
        }
    }
}

/** 按需解码本地图片并缓存为 ImageBitmap，按 maxDim 降采样避免大图 OOM。 */
@Composable
private fun rememberImageBitmap(relPath: String, maxDim: Int): ImageBitmap? {
    return remember(relPath, maxDim) {
        decodeImage(AttachmentStore.absoluteFile(relPath).absolutePath, maxDim)?.asImageBitmap()
    }
}

private fun decodeImage(path: String, maxDim: Int): Bitmap? {
    val f = File(path)
    if (!f.exists()) return null
    return try {
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeFile(f.absolutePath, bounds)
        var sample = 1
        while (bounds.outWidth / sample > maxDim || bounds.outHeight / sample > maxDim) sample *= 2
        BitmapFactory.decodeFile(f.absolutePath, BitmapFactory.Options().apply { inSampleSize = sample })
    } catch (e: Exception) {
        null
    }
}
