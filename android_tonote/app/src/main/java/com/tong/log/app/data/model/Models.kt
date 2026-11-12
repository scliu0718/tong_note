package com.tong.log.app.data.model

import com.tong.log.app.data.codec.RichTextHTMLCodec
import kotlinx.serialization.Serializable
import java.time.Instant
import java.time.temporal.ChronoUnit
import java.util.UUID

/**
 * 时间工具：与 Mac 端 Swift 的 `.iso8601` 保持一致（UTC、秒精度、无毫秒）。
 * 例：2026-11-12T05:30:41Z
 */
object Iso8601 {
    fun now(): String = Instant.now().truncatedTo(ChronoUnit.SECONDS).toString()
    fun parse(s: String): Instant = Instant.parse(s)
    fun fromEpochMillis(millis: Long): String =
        Instant.ofEpochMilli(millis).truncatedTo(ChronoUnit.SECONDS).toString()
}

fun newUuid(): String = UUID.randomUUID().toString().uppercase()

object NoteContentFormat {
    const val MARKDOWN = "markdown"
    const val RICH_TEXT = "richText"
}

@Serializable
data class Note(
    val id: String = newUuid(),
    val title: String = "",
    val body: String = "",
    val createdAt: String = Iso8601.now(),
    val updatedAt: String = Iso8601.now(),
    val deletedAt: String? = null,
    val tags: List<String> = emptyList(),
    val notebook: String = "工作日志",
    val pinned: Boolean = false,
    val contentFormat: String = NoteContentFormat.MARKDOWN,
    val rev: Long = 0,
    val purged: Boolean = false
) {
    val isTrashed: Boolean get() = deletedAt != null

    /** 富文本正文剥标签后取纯文本摘要（用于列表卡片 / 字数 / 搜索）。 */
    val plainBody: String
        get() = if (contentFormat == NoteContentFormat.RICH_TEXT) {
            RichTextHTMLCodec.visibleText(body)
        } else body

    val snippet: String
        get() {
            val t = plainBody.trim()
            return if (t.isEmpty()) "空笔记" else t.take(60)
        }
}

@Serializable
data class Todo(
    val id: String = newUuid(),
    val title: String = "",
    val notes: String = "",
    val status: String = TodoStatus.TODO,
    val plannedAt: String? = null,
    val completedAt: String? = null,
    val startedAt: String? = null,
    val createdAt: String = Iso8601.now(),
    val updatedAt: String = Iso8601.now(),
    val order: Long = 0,
    val rev: Long = 0,
    val purged: Boolean = false
)

object TodoStatus {
    const val TODO = "todo"
    const val DOING = "doing"
    const val DONE = "done"
    const val TERMINATED = "terminated"
}
