package com.tong.log.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ArrowBack
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tong.log.app.data.model.Note
import com.tong.log.app.data.store.NoteStore
import com.tong.log.app.ui.theme.*

@Composable
fun TrashScreen(onBack: () -> Unit) {
    val notes by NoteStore.notes.collectAsState()
    val trash = notes.filter { it.isTrashed && !it.purged }
    var confirmDelete by remember { mutableStateOf<Note?>(null) }

    Column(Modifier.fillMaxSize().background(Bg)) {
        Row(
            Modifier.fillMaxWidth().padding(horizontal = 8.dp, vertical = 6.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            IconButton(onClick = onBack) {
                Icon(Icons.Default.ArrowBack, contentDescription = "返回", tint = TextPrimary)
            }
            Text("回收站", fontSize = 17.sp, fontWeight = FontWeight.Bold)
        }
        if (trash.isEmpty()) {
            Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                Text("回收站是空的", color = TextQuaternary, fontSize = 13.sp)
            }
        } else {
            LazyColumn(Modifier.fillMaxSize().padding(horizontal = 16.dp)) {
                items(trash, key = { it.id }) { note ->
                    Row(
                        Modifier
                            .fillMaxWidth()
                            .padding(vertical = 6.dp)
                            .clip(RoundedCornerShape(12.dp))
                            .background(Color.White)
                            .padding(14.dp),
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        Column(Modifier.weight(1f)) {
                            Text(note.title.ifBlank { "无标题" }, fontSize = 14.sp, fontWeight = FontWeight.SemiBold)
                            Spacer(Modifier.height(3.dp))
                            Text(displayTime(note.deletedAt), fontSize = 11.sp, color = TextQuaternary)
                        }
                        TextButton(onClick = { NoteStore.restore(note.id) }) {
                            Text("恢复", color = Green, fontSize = 12.sp)
                        }
                        TextButton(onClick = { confirmDelete = note }) {
                            Text("彻底删除", color = Red, fontSize = 12.sp)
                        }
                    }
                }
            }
        }
    }

    confirmDelete?.let { note ->
        AlertDialog(
            onDismissRequest = { confirmDelete = null },
            title = { Text("彻底删除笔记？") },
            text = { Text("此操作不可恢复，笔记将被永久移除。") },
            confirmButton = {
                TextButton(onClick = {
                    NoteStore.permanentlyDelete(note.id)
                    confirmDelete = null
                }) { Text("删除", color = Red) }
            },
            dismissButton = {
                TextButton(onClick = { confirmDelete = null }) { Text("取消") }
            }
        )
    }
}
