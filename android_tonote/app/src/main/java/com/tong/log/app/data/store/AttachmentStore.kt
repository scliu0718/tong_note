package com.tong.log.app.data.store

import android.net.Uri
import com.tong.log.app.data.model.newUuid
import java.io.File

/**
 * 图片附件：正文只存相对路径 attachments/<noteId>/<uuid>.<ext>，
 * 与 Mac 端同一套路径规则，保证跨端同步后图片可解析。
 */
object AttachmentStore {
    private val imageExtensions = listOf("png", "jpg", "jpeg", "gif", "webp", "heic", "heif", "tiff", "tif", "bmp")

    fun importImage(uri: Uri, noteId: String): String? {
        val ext = inferExtension(uri) ?: return null
        val dir = File(AppStorage.attachmentsDir, noteId).apply { mkdirs() }
        val target = File(dir, "${newUuid()}.$ext")
        return try {
            AppStorage.context.contentResolver.openInputStream(uri)?.use { input ->
                target.outputStream().use { output -> input.copyTo(output) }
            }
            relativePath(target)
        } catch (e: Exception) {
            null
        }
    }

    private fun inferExtension(uri: Uri): String? {
        val mime = AppStorage.context.contentResolver.getType(uri)
        val fromMime = when (mime) {
            "image/png" -> "png"
            "image/jpeg" -> "jpg"
            "image/gif" -> "gif"
            "image/webp" -> "webp"
            "image/heic" -> "heic"
            "image/heif" -> "heif"
            "image/tiff" -> "tiff"
            "image/bmp" -> "bmp"
            else -> null
        }
        if (fromMime != null) return fromMime
        val name = uri.lastPathSegment ?: return null
        val dot = name.lastIndexOf('.')
        if (dot < 0) return null
        val ext = name.substring(dot + 1).lowercase()
        return if (imageExtensions.contains(ext)) ext else null
    }

    fun relativePath(absFile: File): String {
        val root = AppStorage.rootDir.absolutePath
        val abs = absFile.absolutePath
        return if (abs.startsWith(root)) {
            abs.substring(root.length).trimStart('/')
        } else absFile.name
    }

    fun absoluteFile(relativePath: String): File = File(AppStorage.rootDir, relativePath)

    fun removeAttachments(noteId: String) {
        File(AppStorage.attachmentsDir, noteId).deleteRecursively()
    }

    fun removeAttachmentsIfUnreferenced(noteId: String) {
        // 仅当没有其它未 purged 笔记正文引用该附件目录时才删除
        val referenced = NoteStore.notes.value.any { n ->
            !n.purged && n.body.contains("attachments/$noteId/")
        }
        if (!referenced) removeAttachments(noteId)
    }
}
