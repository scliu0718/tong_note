package com.tong.log.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyListScope
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tong.log.app.data.model.Todo
import com.tong.log.app.data.model.TodoStatus
import com.tong.log.app.data.store.TodoStore
import com.tong.log.app.data.store.isOverdue
import com.tong.log.app.ui.theme.*
import kotlinx.coroutines.launch

@Composable
fun TodoScreen(onSync: () -> Unit) {
    val todos by TodoStore.todos.collectAsState()
    val scope = rememberCoroutineScope()
    var filter by remember { mutableStateOf("all") }
    var newTitle by remember { mutableStateOf("") }

    val active = todos.filter { !it.purged }
    val todo = active.filter { it.status == TodoStatus.TODO }
    val doing = active.filter { it.status == TodoStatus.DOING }
    val done = active.filter { it.status == TodoStatus.DONE }
    val terminated = active.filter { it.status == TodoStatus.TERMINATED }
    val overdueCount = active.count { it.isOverdue }

    Column(Modifier.fillMaxSize().background(Bg)) {
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
            todoGroup("待完成", todo, Color(0xFFE5484D), show = true, isDoneGroup = false)
            todoGroup("进行中", doing, Amber, show = true, isDoneGroup = false)
            if (filter == "all") {
                todoGroup("已完成", done, Green, show = filter == "all", isDoneGroup = true)
                todoGroup("已终止", terminated, TextQuaternary, show = filter == "all", isDoneGroup = true)
            }
            item { Spacer(Modifier.height(8.dp)) }
        }

        // 快捷添加
        Row(
            Modifier.fillMaxWidth().background(Bg).padding(horizontal = 16.dp, vertical = 8.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Icon(Icons.Default.Add, contentDescription = null, tint = Green, modifier = Modifier.size(20.dp))
            Spacer(Modifier.width(10.dp))
            OutlinedTextField(
                value = newTitle,
                onValueChange = { newTitle = it },
                modifier = Modifier.weight(1f),
                placeholder = { Text("添加任务，回车确认", fontSize = 13.sp) },
                shape = RoundedCornerShape(12.dp),
                singleLine = true
            )
            Spacer(Modifier.width(8.dp))
            Button(
                onClick = {
                    if (newTitle.isNotBlank()) {
                        scope.launch { TodoStore.add(newTitle); newTitle = "" }
                    }
                },
                colors = ButtonDefaults.buttonColors(containerColor = Green),
                contentPadding = PaddingValues(horizontal = 16.dp, vertical = 8.dp)
            ) { Text("添加", fontSize = 12.sp) }
        }
    }
}

private fun LazyListScope.todoGroup(
    title: String, list: List<Todo>, color: Color, show: Boolean, isDoneGroup: Boolean
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
                .background(Color.White)
                .padding(horizontal = 12.dp, vertical = 11.dp),
            verticalAlignment = Alignment.Top
        ) {
            Box(
                Modifier
                    .size(21.dp)
                    .clickable { TodoStore.toggleDone(t.id) }
                    .background(
                        if (t.status == TodoStatus.DONE) Green else Color.Transparent,
                        CircleShape
                    )
                    .border(2.dp, if (t.status == TodoStatus.DONE) Green else Line, CircleShape)
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
            when (t.status) {
                TodoStatus.TODO -> ActionChip("开始", GreenTint, GreenHover) { TodoStore.setStatus(t.id, TodoStatus.DOING) }
                TodoStatus.DOING -> ActionChip("完成", AmberTint, Color(0xFFB9770A)) { TodoStore.setStatus(t.id, TodoStatus.DONE) }
                TodoStatus.TERMINATED -> ActionChip("恢复", Color(0xFFF0F0F2), TextTertiary) { TodoStore.setStatus(t.id, TodoStatus.TODO) }
            }
            if (t.status == TodoStatus.TODO || t.status == TodoStatus.DOING) {
                Spacer(Modifier.width(6.dp))
                ActionChip("终止", Color.Transparent, TextQuaternary) { TodoStore.setStatus(t.id, TodoStatus.TERMINATED) }
            }
            Spacer(Modifier.width(6.dp))
            Text(
                "删", Modifier.clickable { TodoStore.remove(t.id) }.padding(4.dp),
                color = TextQuaternary, fontSize = 12.sp
            )
        }
    }
}

@Composable
private fun ActionChip(label: String, bg: Color, fg: Color, onClick: () -> Unit) {
    Text(
        label,
        Modifier
            .clip(RoundedCornerShape(7.dp))
            .background(bg)
            .clickable(onClick = onClick)
            .padding(horizontal = 10.dp, vertical = 4.dp),
        color = fg, fontSize = 11.sp, fontWeight = FontWeight.SemiBold
    )
}
