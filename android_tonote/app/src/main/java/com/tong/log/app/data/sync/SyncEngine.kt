package com.tong.log.app.data.sync

import com.tong.log.app.data.codec.ImagePathScanner
import com.tong.log.app.data.model.Iso8601
import com.tong.log.app.data.model.Note
import com.tong.log.app.data.model.SyncCursor
import com.tong.log.app.data.model.Todo
import com.tong.log.app.data.model.newUuid
import com.tong.log.app.data.net.BaiduPanClient
import com.tong.log.app.data.net.BaiduPanConfig
import com.tong.log.app.data.net.BaiduPanTokenStore
import com.tong.log.app.data.store.AppStorage
import com.tong.log.app.data.store.AttachmentStore
import com.tong.log.app.data.store.NoteStore
import com.tong.log.app.data.store.NotebookStore
import com.tong.log.app.data.store.TodoStore
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import java.io.File

/**
 * 按篇同步引擎：notes/<UUID>.json / todos/<UUID>.json，三方合并（rev + updatedAt + cursor）。
 */
object SyncEngine {
    data class Status(
        val state: String = "idle", // idle / syncing / success / failed
        val lastMessage: String = "",
        val lastSyncDate: String? = null
    ) {
        val isSyncing: Boolean get() = state == "syncing"
    }

    private val _status = MutableStateFlow(Status())
    val status: StateFlow<Status> = _status.asStateFlow()

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private var scheduleJob: Job? = null
    private var needsAnotherRound = false

    private val notesDir = "${BaiduPanConfig.REMOTE_ROOT}/notes"
    private val todosDir = "${BaiduPanConfig.REMOTE_ROOT}/todos"
    private val notebooksPath = "${BaiduPanConfig.REMOTE_ROOT}/notebooks.json"
    private val manifestPath = "${BaiduPanConfig.REMOTE_ROOT}/manifest.json"

    private val attachmentRegex =
        Regex("""attachments/[0-9A-Fa-f-]{36}/[0-9A-Fa-f-]{36}\.[A-Za-z0-9]+""")

    // ---------- 触发 ----------

    fun schedule() {
        scheduleJob?.cancel()
        scheduleJob = scope.launch {
            delay(1500)
            syncRound()
        }
    }

    suspend fun syncRound() {
        if (_status.value.isSyncing) {
            needsAnotherRound = true
            return
        }
        _status.value = Status(state = "syncing", lastMessage = "", lastSyncDate = _status.value.lastSyncDate)
        var kicked = false
        debugLog("同步开始")
        try {
            val token = BaiduPanClient.validToken()
            DeviceRegistry.ensureNotKicked(token)
            migrateNotesIfNeeded(token)
            val noteCount = syncNotes(token)
            val localNotes = NoteStore.notes.value.size
            syncNotebooks(token)
            val todoCount = syncTodos(token)
            _status.value = Status(
                state = "success",
                lastMessage = "笔记 $noteCount 篇 · 任务 $todoCount 条",
                lastSyncDate = Iso8601.now()
            )
            debugLog("同步成功 云端笔记 $noteCount 篇 / 本地 $localNotes 篇")
        } catch (e: DeviceKickedException) {
            kicked = true
            _status.value = Status(state = "failed", lastMessage = e.message ?: "本机已被下线", lastSyncDate = _status.value.lastSyncDate)
            debugLog("同步失败（被下线）：${e.message}")
        } catch (e: Exception) {
            _status.value = Status(state = "failed", lastMessage = e.message ?: "同步失败", lastSyncDate = _status.value.lastSyncDate)
            debugLog("同步失败：${e.javaClass.simpleName} ${e.message}")
        }
        if (needsAnotherRound && !kicked) {
            needsAnotherRound = false
            syncRound()
        }
    }

    suspend fun syncSingleNote(noteId: String) {
        if (_status.value.isSyncing) return
        _status.value = Status(state = "syncing", lastMessage = "", lastSyncDate = _status.value.lastSyncDate)
        try {
            val token = BaiduPanClient.validToken()
            DeviceRegistry.ensureNotKicked(token)
            val remoteMtimes = BaiduPanClient.listJsonFiles(notesDir, token)
            syncOneNote(noteId, remoteMtimes[noteId.uppercase()], token)
            _status.value = Status(state = "success", lastMessage = "已同步本篇", lastSyncDate = Iso8601.now())
        } catch (e: Exception) {
            _status.value = Status(state = "failed", lastMessage = e.message ?: "同步失败", lastSyncDate = _status.value.lastSyncDate)
        }
    }

    // ---------- 笔记同步 ----------

    private suspend fun syncNotes(token: String): Int {
        val remoteMtimes = BaiduPanClient.listJsonFiles(notesDir, token)
        val ids = (NoteStore.notes.value.map { it.id } + remoteMtimes.keys).distinct()
        for (id in ids) {
            syncOneNote(id, remoteMtimes[id.uppercase()], token)
        }
        return ids.size
    }

    private suspend fun syncOneNote(id: String, serverMtime: Long?, token: String) {
        val cursor = SyncCursorStore.cursor(id, false)
        if (SyncRules.shouldSkipFetch(serverMtime, cursor)) return

        val remoteNote = try {
            val bytes = BaiduPanClient.downloadData("$notesDir/$id.json", token)
            AppStorage.json.decodeFromString(Note.serializer(), String(bytes))
        } catch (e: Exception) {
            debugLog("下载笔记失败 $id：${e.message}")
            null
        }

        val local = NoteStore.note(id)
        val remoteRev = remoteNote?.rev
        val remoteUpdatedAt = remoteNote?.updatedAt
        val localUpdatedAt = local?.updatedAt
        val dirty = cursor?.dirty == true

        when (val step = SyncRules.afterFetch(dirty, remoteRev, remoteUpdatedAt, localUpdatedAt, cursor)) {
            is SyncRules.SyncStep.Skip -> Unit
            is SyncRules.SyncStep.RememberMtime -> {
                if (serverMtime != null) {
                    SyncCursorStore.set(
                        (cursor ?: SyncCursor()).copy(serverMtime = serverMtime), id, false
                    )
                }
            }
            is SyncRules.SyncStep.UseRemote -> {
                if (remoteNote != null) applyRemoteNote(remoteNote, remoteRev ?: 0, serverMtime, token)
            }
            is SyncRules.SyncStep.Upload -> {
                val l = local ?: return
                val published = publish(l, step.rev, token)
                if (published != null) {
                    applyLocalPublished(published, serverMtime)
                }
            }
            is SyncRules.SyncStep.Conflict -> {
                if (local != null && remoteNote != null) {
                    resolveConflict(local, remoteNote, remoteRev ?: 0, serverMtime, token)
                } else if (remoteNote != null) {
                    applyRemoteNote(remoteNote, remoteRev ?: 0, serverMtime, token)
                }
            }
        }
    }

    private suspend fun publish(note: Note, baseRev: Long, token: String): Note? {
        var rev = baseRev
        var attempt = 0
        while (attempt < 3) {
            val toPublish = note.copy(rev = rev)
            uploadNote(toPublish, token)
            val back = try {
                val bytes = BaiduPanClient.downloadData("$notesDir/${note.id}.json", token)
                AppStorage.json.decodeFromString(Note.serializer(), String(bytes))
            } catch (e: Exception) {
                null
            }
            if (back != null && back.rev == rev && SyncRules.sameTime(back.updatedAt, toPublish.updatedAt)) {
                return toPublish
            }
            val step = SyncRules.afterFetch(
                dirty = true,
                remoteRev = back?.rev,
                remoteUpdatedAt = back?.updatedAt,
                localUpdatedAt = toPublish.updatedAt,
                cursor = SyncCursor(baseRev = rev, baseUpdatedAt = toPublish.updatedAt)
            )
            when (step) {
                is SyncRules.SyncStep.Upload -> rev = step.rev
                else -> return null
            }
            attempt++
        }
        throw Exception("笔记「${note.title}」连续三次没能写上云端")
    }

    private suspend fun uploadNote(note: Note, token: String) {
        uploadAttachmentsFor(note.body, note.id, token, false)
        val json = AppStorage.json.encodeToString(Note.serializer(), note).toByteArray()
        BaiduPanClient.uploadData(json, "$notesDir/${note.id}.json", token)
    }

    private suspend fun applyLocalPublished(note: Note, serverMtime: Long?) {
        NoteStore.upsert(note)
        SyncCursorStore.set(
            SyncCursor(
                baseRev = note.rev,
                baseUpdatedAt = note.updatedAt,
                serverMtime = serverMtime,
                dirty = false,
                uploadedAttachments = attachmentPaths(note.body)
            ),
            note.id, false
        )
    }

    private suspend fun applyRemoteNote(remote: Note, remoteRev: Long, serverMtime: Long?, token: String) {
        downloadAttachmentsFor(remote.body, remote.id, token)
        NoteStore.upsert(remote)
        SyncCursorStore.set(
            SyncCursor(
                baseRev = remoteRev,
                baseUpdatedAt = remote.updatedAt,
                serverMtime = serverMtime,
                dirty = false,
                uploadedAttachments = attachmentPaths(remote.body)
            ),
            remote.id, false
        )
        if (remote.purged) AttachmentStore.removeAttachmentsIfUnreferenced(remote.id)
    }

    private suspend fun resolveConflict(
        local: Note, remote: Note, remoteRev: Long, serverMtime: Long?, token: String
    ) {
        if (SyncRules.remoteWins(local.updatedAt, remote.updatedAt)) {
            // 云端胜：本地另存副本并上传，原 id 收下云端
            val copy = conflictCopy(local)
            NoteStore.upsert(copy)
            uploadNote(copy, token)
            applyRemoteNote(remote, remoteRev, serverMtime, token)
        } else {
            // 本地胜：云端另存副本，本地作为赢家
            val cloudCopy = conflictCopy(remote)
            uploadNote(cloudCopy, token)
            val winnerRev = maxOf(local.rev, remoteRev) + 1
            val published = publish(local, winnerRev, token)
            if (published != null) applyLocalPublished(published, serverMtime)
        }
    }

    private fun conflictCopy(note: Note): Note {
        val title = if (note.title.endsWith("（冲突副本）")) note.title else note.title + "（冲突副本）"
        return note.copy(id = newUuid(), title = title, rev = 0, purged = false)
    }

    // ---------- 任务同步 ----------

    private suspend fun syncTodos(token: String): Int {
        val remoteMtimes = BaiduPanClient.listJsonFiles(todosDir, token)
        val ids = (TodoStore.todos.value.map { it.id } + remoteMtimes.keys).distinct()
        for (id in ids) {
            syncOneTodo(id, remoteMtimes[id.uppercase()], token)
        }
        return ids.size
    }

    private suspend fun syncOneTodo(id: String, serverMtime: Long?, token: String) {
        val cursor = SyncCursorStore.cursor(id, true)
        if (SyncRules.shouldSkipFetch(serverMtime, cursor)) return
        val remoteTodo = try {
            val bytes = BaiduPanClient.downloadData("$todosDir/$id.json", token)
            AppStorage.json.decodeFromString(Todo.serializer(), String(bytes))
        } catch (e: Exception) {
            null
        }
        val local = TodoStore.todo(id)
        val dirty = cursor?.dirty == true
        when (val step = SyncRules.afterFetch(
            dirty, remoteTodo?.rev, remoteTodo?.updatedAt, local?.updatedAt, cursor
        )) {
            is SyncRules.SyncStep.RememberMtime -> {
                if (serverMtime != null) {
                    SyncCursorStore.set((cursor ?: SyncCursor()).copy(serverMtime = serverMtime), id, true)
                }
            }
            is SyncRules.SyncStep.UseRemote -> if (remoteTodo != null) {
                TodoStore.upsert(remoteTodo)
                SyncCursorStore.set(
                    SyncCursor(remoteTodo.rev, remoteTodo.updatedAt, serverMtime, false), id, true
                )
            }
            is SyncRules.SyncStep.Upload -> {
                val l = local ?: return
                val published = publishTodo(l, step.rev, token)
                if (published != null) {
                    TodoStore.update(published)
                    SyncCursorStore.set(
                        SyncCursor(published.rev, published.updatedAt, serverMtime, false), id, true
                    )
                }
            }
            is SyncRules.SyncStep.Conflict -> {
                if (local != null && remoteTodo != null) {
                    if (SyncRules.remoteWins(local.updatedAt, remoteTodo.updatedAt)) {
                        TodoStore.upsert(remoteTodo)
                        SyncCursorStore.set(
                            SyncCursor(remoteTodo.rev, remoteTodo.updatedAt, serverMtime, false), id, true
                        )
                    } else {
                        val published = publishTodo(local, maxOf(local.rev, remoteTodo.rev) + 1, token)
                        if (published != null) TodoStore.upsert(published)
                    }
                } else if (remoteTodo != null) {
                    TodoStore.upsert(remoteTodo)
                }
            }
            is SyncRules.SyncStep.Skip -> Unit
        }
    }

    private suspend fun publishTodo(todo: Todo, baseRev: Long, token: String): Todo? {
        var rev = baseRev
        var attempt = 0
        while (attempt < 3) {
            val toPublish = todo.copy(rev = rev)
            val json = AppStorage.json.encodeToString(Todo.serializer(), toPublish).toByteArray()
            BaiduPanClient.uploadData(json, "$todosDir/${todo.id}.json", token)
            val back = try {
                val bytes = BaiduPanClient.downloadData("$todosDir/${todo.id}.json", token)
                AppStorage.json.decodeFromString(Todo.serializer(), String(bytes))
            } catch (e: Exception) {
                null
            }
            if (back != null && back.rev == rev && SyncRules.sameTime(back.updatedAt, toPublish.updatedAt)) {
                return toPublish
            }
            val step = SyncRules.afterFetch(true, back?.rev, back?.updatedAt, toPublish.updatedAt,
                SyncCursor(baseRev = rev, baseUpdatedAt = toPublish.updatedAt))
            if (step is SyncRules.SyncStep.Upload) rev = step.rev else return null
            attempt++
        }
        throw Exception("任务「${todo.title}」连续三次没能写上云端")
    }

    // ---------- 笔记本同步 ----------

    private suspend fun syncNotebooks(token: String) {
        val local = NotebookStore.customNotebooks
        val localRemoved = NotebookStore.removedNotebooks
        val remote = try {
            val bytes = BaiduPanClient.downloadData(notebooksPath, token)
            AppStorage.json.decodeFromString(NotebooksPayload.serializer(), String(bytes))
        } catch (e: Exception) {
            null
        }
        val remoteNames = remote?.names ?: emptyList()
        val remoteRemoved = remote?.removed ?: emptyList()
        val names = ((local + remoteNames) - remoteRemoved).distinct().sorted()
        val removed = (localRemoved + remoteRemoved).distinct()
        val changed = names != remoteNames || removed != remoteRemoved
        if (changed) {
            val payload = NotebooksPayload(names, removed)
            BaiduPanClient.uploadData(
                AppStorage.json.encodeToString(NotebooksPayload.serializer(), payload).toByteArray(),
                notebooksPath, token
            )
        }
        // 合并回本地偏好
        val prefs = AppStorage.prefs.edit()
        prefs.putString("customNotebooks", names.joinToString("\n"))
        prefs.putString("removedNotebooks", removed.joinToString("\n"))
        prefs.apply()
    }

    @kotlinx.serialization.Serializable
    data class NotebooksPayload(val names: List<String> = emptyList(), val removed: List<String> = emptyList())

    // ---------- 附件 ----------

    private fun attachmentPaths(body: String): List<String> =
        attachmentRegex.findAll(body).map { it.value }.toList()

    private suspend fun uploadAttachmentsFor(body: String, ownerId: String, token: String, isTodo: Boolean) {
        val cursor = SyncCursorStore.cursor(ownerId, isTodo)
        val uploaded = cursor?.uploadedAttachments ?: emptyList()
        for (rel in attachmentPaths(body)) {
            if (rel in uploaded) continue
            val file = AttachmentStore.absoluteFile(rel)
            if (!file.exists()) continue
            BaiduPanClient.uploadData(file.readBytes(), "${BaiduPanConfig.REMOTE_ROOT}/$rel", token)
        }
    }

    private suspend fun downloadAttachmentsFor(body: String, ownerId: String, token: String) {
        for (rel in attachmentPaths(body)) {
            val file = AttachmentStore.absoluteFile(rel)
            if (file.exists()) continue
            try {
                val bytes = BaiduPanClient.downloadData("${BaiduPanConfig.REMOTE_ROOT}/$rel", token)
                file.parentFile?.mkdirs()
                file.writeBytes(bytes)
            } catch (e: Exception) {
                // 下载失败留待下一轮重试
            }
        }
    }

    // ---------- 旧格式迁移 ----------

    private suspend fun migrateNotesIfNeeded(token: String) {
        val notes = try {
            BaiduPanClient.listJsonFiles(notesDir, token)
        } catch (e: Exception) {
            emptyMap()
        }
        if (notes.isNotEmpty()) return
        val manifest = try {
            val bytes = BaiduPanClient.downloadData(manifestPath, token)
            AppStorage.json.decodeFromString<LegacyManifest>(String(bytes))
        } catch (e: Exception) {
            null
        } ?: return
        for (n in manifest.notes) {
            BaiduPanClient.uploadData(
                AppStorage.json.encodeToString(Note.serializer(), n).toByteArray(),
                "$notesDir/${n.id}.json", token
            )
        }
    }

    @kotlinx.serialization.Serializable
    data class LegacyManifest(val notes: List<Note> = emptyList())

    /**
     * 同步诊断日志落在 App 私有目录 files/tonote/sync-debug.log，
     * 可用 adb shell run-as com.tong.log.app cat files/tonote/sync-debug.log 查看。
     */
    private fun debugLog(message: String) {
        try {
            val f = File(AppStorage.rootDir, "sync-debug.log")
            f.appendText("${Iso8601.now()} $message\n")
        } catch (_: Exception) {
        }
    }

    fun isLoggedIn(): Boolean = BaiduPanTokenStore.current() != null
}
