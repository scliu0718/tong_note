package com.tong.log.app.data.store

import android.content.Context
import kotlinx.serialization.decodeFromString
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import java.io.File

/**
 * 本地存储根：与 Mac 端 `~/Library/Application Support/tonote/` 对应，
 * Android 使用 app 私有目录 `filesDir/tonote/`。
 */
object AppStorage {
    lateinit var context: Context
        private set

    val json = Json {
        prettyPrint = true
        encodeDefaults = true
        ignoreUnknownKeys = true
    }

    fun init(context: Context) {
        this.context = context.applicationContext
    }

    val rootDir: File get() = File(context.filesDir, "tonote").apply { mkdirs() }
    val notesFile: File get() = File(rootDir, "notes.json")
    val todosFile: File get() = File(rootDir, "todos.json")
    val noteSyncFile: File get() = File(rootDir, "note-sync.json")
    val todoSyncFile: File get() = File(rootDir, "todo-sync.json")
    val deviceFile: File get() = File(rootDir, "device.json")
    val tokenFile: File get() = File(rootDir, "baidupan-token.json")
    val attachmentsDir: File get() = File(rootDir, "attachments").apply { mkdirs() }

    val prefs: android.content.SharedPreferences
        get() = context.getSharedPreferences("tonote_prefs", Context.MODE_PRIVATE)

    inline fun <reified T> readJson(file: File, default: T): T = try {
        if (file.exists() && file.length() > 0) {
            json.decodeFromString<T>(file.readText())
        } else default
    } catch (e: Exception) {
        default
    }

    inline fun <reified T> writeJson(file: File, value: T) {
        try {
            file.parentFile?.mkdirs()
            file.writeText(json.encodeToString(value))
        } catch (_: Exception) {
        }
    }
}
