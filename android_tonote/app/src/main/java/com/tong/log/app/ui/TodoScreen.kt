package com.tong.log.app.ui

import android.app.DatePickerDialog
import android.content.DialogInterface
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
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
import androidx.compose.material.icons.outlined.DeleteOutline
import androidx.compose.material.icons.filled.EditNote
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import com.tong.log.app.data.model.Todo
import com.tong.log.app.data.model.TodoStatus
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

    val active = todos.filter { !it.purged }
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
            Modifier.fillMaxWidth().padding(horizontal = 14.dp, vertical = 10.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Text("任务", fontSize = 17.sp, fontWeight = FontWeight.Bold)
            Spacer(Modifier.weight(1f))
            IconButton(onClick = onSync) {
                Icon(Icons.Default.Refresh, contentDescription = "同步", tint = TextPrimary)
            }
        }
        // 标题栏分割线
        TopBarDivider()
        Row(Modifier.padding(horizontal = 16.dp, vertical = 4.dp), verticalAlignment = Alignment.CenterVertically) {
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

    // 编辑已有任务：标题 / 备注 / 计划日期
    editingTodo?.let { t ->
        TodoEditDialog(
            title = "编辑任务",
            initialTitle = t.title,
            initialNotes = t.notes,
            initialDate = isoToDate(t.plannedAt),
            confirmLabel = "保存",
            onDismiss = { editingTodo = null },
            onConfirm = { title, notes, date ->
                TodoStore.updateTitle(t.id, title)
                TodoStore.setNotes(t.id, notes)
                TodoStore.setPlannedAt(t.id, date)
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
            Box(Modifier.size(8.dp).background(color, CircleShape))
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
                .then(if (t.isOverdue) Modifier.border(1.5.dp, Red, RoundedCornerShape(11.dp)) else Modifier)
                .clickable { onEdit(t) }
                .padding(start = 12.dp, top = 11.dp, end = 9.dp, bottom = 11.dp),
            verticalAlignment = Alignment.Top
        ) {
            // 状态圈用各组颜色区分：待完成=红空心、进行中=琥珀空心、已完成=绿实心带勾、已终止=灰实心
            val ringColor = color
            val filled = t.status == TodoStatus.DONE || t.status == TodoStatus.TERMINATED
            Box(
                Modifier
                    .size(21.dp)
                    .clickable { TodoStore.toggleDone(t.id) }
                    .background(if (filled) ringColor else Color.Transparent, CircleShape)
                    .border(2.dp, ringColor, CircleShape)
                    .padding(3.dp)
            ) {
                if (t.status == TodoStatus.DONE) {
                    Text("✓", color = Color.White, fontSize = 10.sp, modifier = Modifier.align(Alignment.Center))
                }
            }
            Spacer(Modifier.width(11.dp))
            Column(Modifier.weight(1f)) {
                Text(
                    t.title,
                    fontSize = 13.sp,
                    color = if (isDoneGroup) TextQuaternary else TextPrimary,
                    textDecoration = if (isDoneGroup) TextDecoration.LineThrough else TextDecoration.None
                )
                if (!t.notes.isBlank()) {
                    Spacer(Modifier.height(3.dp))
                    Text(t.notes, fontSize = 11.sp, color = TextTertiary, maxLines = 2)
                }
                if (t.plannedAt != null) {
                    Spacer(Modifier.height(3.dp))
                    Text(
                        displayTime(t.plannedAt) + if (t.isOverdue) " · 逾期" else "",
                        Modifier
                            .clip(RoundedCornerShape(999.dp))
                            .background(if (t.isOverdue) RedTint else Color(0xFFF0F0F2))
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

/** 自绘紧凑对话框：标题 / 备注 / 计划日期，避免 M3 AlertDialog 的固定大 padding。 */
@Composable
private fun TodoEditDialog(
    title: String,
    initialTitle: String,
    initialNotes: String,
    initialDate: LocalDate?,
    confirmLabel: String,
    onDismiss: () -> Unit,
    onConfirm: (String, String, LocalDate?) -> Unit
) {
    val context = LocalContext.current
    var taskTitle by remember { mutableStateOf(initialTitle) }
    var notes by remember { mutableStateOf(initialNotes) }
    var date by remember { mutableStateOf(initialDate) }

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
                    OutlinedTextField(
                        value = taskTitle,
                        onValueChange = { taskTitle = it },
                        label = { Text("标题", fontSize = 12.sp) },
                        singleLine = true,
                        modifier = Modifier.fillMaxWidth()
                    )
                    Spacer(Modifier.height(8.dp))
                    OutlinedTextField(
                        value = notes,
                        onValueChange = { notes = it },
                        label = { Text("备注", fontSize = 12.sp) },
                        minLines = 2,
                        maxLines = 4,
                        modifier = Modifier.fillMaxWidth()
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
                                .clickable {
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
                        if (date != null) {
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
                    TextButton(onClick = { onConfirm(taskTitle.trim(), notes.trim(), date) }) {
                        Text(confirmLabel, color = GreenHover, fontWeight = FontWeight.SemiBold)
                    }
                    TextButton(onClick = onDismiss) { Text("取消", color = TextSecondary) }
                }
            }
        }
    }
}
