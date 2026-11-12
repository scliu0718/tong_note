package com.tong.log.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.PushPin
import androidx.compose.material.icons.filled.Search
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tong.log.app.data.model.Note
import com.tong.log.app.data.model.Notebooks
import com.tong.log.app.data.model.QUICK_SYNC_NOTE_ID
import com.tong.log.app.data.store.NoteStore
import com.tong.log.app.data.store.NotebookStore
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

@Composable
fun NotesScreen(
    onOpenNote: (String) -> Unit,
    onOpenQuickSync: () -> Unit,
    onOpenTodo: () -> Unit,
    onOpenMe: () -> Unit,
    onNewNote: (String) -> Unit
) {
    val notes by NoteStore.notes.collectAsState()
    val notebooks = remember { NotebookStore.all }
    var selectedNotebook by remember { mutableStateOf(Notebooks.ALL) }
    var query by remember { mutableStateOf("") }
    var sort by remember { mutableStateOf("updated") }

    val filtered = remember(notes, selectedNotebook, query, sort) {
        notes.asSequence()
            .filter { !it.purged && !it.isTrashed && it.id != QUICK_SYNC_NOTE_ID }
            .filter { selectedNotebook == Notebooks.ALL || it.notebook == selectedNotebook }
            .filter {
                query.isBlank() || it.title.contains(query, true) ||
                    it.plainBody.contains(query, true)
            }
            .sortedWith(
                when (sort) {
                    "created" -> compareByDescending<Note> { it.createdAt }
                    "title" -> compareBy { it.title }
                    else -> compareByDescending<Note> { it.updatedAt }
                }
            )
            .toList()
    }

    Column(Modifier.fillMaxSize().background(Bg)) {
        // 顶部导航
        Row(
            Modifier.fillMaxWidth().padding(horizontal = 14.dp, vertical = 10.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Box(
                Modifier.size(30.dp).background(Brush.linearGradient(listOf(Color(0xFF19B46A), Color(0xFF0E8A48))), CircleShape),
                contentAlignment = Alignment.Center
            ) {
                Text("同", color = Color.White, fontSize = 12.sp, fontWeight = FontWeight.Bold)
            }
            Spacer(Modifier.width(10.dp))
            Text("笔记", fontSize = 17.sp, fontWeight = FontWeight.Bold)
            Spacer(Modifier.weight(1f))
            IconButton(onClick = { /* 搜索焦点 */ }) {
                Icon(Icons.Default.Search, contentDescription = "搜索", tint = TextPrimary)
            }
        }

        // 笔记本 chips
        Row(
            Modifier.fillMaxWidth().padding(horizontal = 16.dp),
            horizontalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            notebooks.take(6).forEach { nb ->
                val on = selectedNotebook == nb
                Text(
                    nb,
                    Modifier
                        .clip(RoundedCornerShape(999.dp))
                        .background(if (on) Green else Color(0xFFF0F0F2))
                        .clickable { selectedNotebook = nb }
                        .padding(horizontal = 13.dp, vertical = 6.dp),
                    color = if (on) Color.White else TextSecondary,
                    fontSize = 12.sp,
                    fontWeight = if (on) FontWeight.SemiBold else FontWeight.Medium
                )
            }
        }

        // 搜索框
        OutlinedTextField(
            value = query,
            onValueChange = { query = it },
            modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp),
            placeholder = { Text("搜索笔记、标签…", fontSize = 13.sp) },
            shape = RoundedCornerShape(10.dp),
            singleLine = true
        )

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

        LazyColumn(Modifier.weight(1f)) {
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
                        badge = null,
                        onClick = onOpenTodo
                    )
                }
            }
            items(filtered, key = { it.id }) { note ->
                NoteCard(note, onClick = { onOpenNote(note.id) })
            }
        }

        Box(Modifier.fillMaxSize()) {
            FloatingActionButton(
                onClick = { onNewNote(selectedNotebook) },
                modifier = Modifier.align(Alignment.BottomEnd).padding(20.dp),
                containerColor = Green,
                contentColor = Color.White
            ) {
                Icon(Icons.Default.Add, contentDescription = "新建笔记")
            }
        }
    }
}

@Composable
private fun QuickCard(
    modifier: Modifier = Modifier,
    icon: String, tint: Color, bg: Color, title: String, sub: String, badge: String? = null,
    onClick: () -> Unit
) {
    Row(
        modifier
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
            note.snippet, fontSize = 12.sp, color = TextTertiary,
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
