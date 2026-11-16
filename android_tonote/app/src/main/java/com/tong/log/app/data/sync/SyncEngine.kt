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
    private var notesUploadJob: Job? = null
    private var todoUploadJob: Job? = null
    private val pendingTodoIds = linkedSetOf<String>()
    /** 同步进行中又来的请求：插队的放队首，普通的放队尾；`All` 覆盖其余范围。仅主线程访问。 */
    private val pending = ArrayDeque<SyncScope>()

    /** 笔记：改完最多等 30 秒上传一次（节流，不是防抖）；「快同步」更勤，15 秒。 */
    const val NOTES_UPLOAD_INTERVAL_MS = 30_000L
    const val QUICK_NOTE_UPLOAD_INTERVAL_MS = 15_000L
    private var notesUploadDeadline = 0L

    private val notesDir = "${BaiduPanConfig.REMOTE_ROOT}/notes"
    private val todosDir = "${BaiduPanConfig.REMOTE_ROOT}/todos"
    private val notebooksPath = "${BaiduPanConfig.REMOTE_ROOT}/notebooks.json"
    private val manifestPath = "${BaiduPanConfig.REMOTE_ROOT}/manifest.json"

    private val attachmentRegex =
        Regex("""attachments/[0-9A-Fa-f-]{36}/[0-9A-Fa-f-]{36}\.[A-Za-z0-9]+""")

    // ---------- 触发 ----------

    /**
     * 笔记改动后调用：普通笔记 30 秒内最多传一次，「快同步」15 秒；离开笔记时用 [flushNotes] 立刻传。
     * 已有计时时只在新的截止时间更早时才改期（先改了普通笔记、又去改快同步，要按 15 秒来）。
     */
    fun scheduleNotesUpload(quick: Boolean = false) {
        val wait = if (quick) QUICK_NOTE_UPLOAD_INTERVAL_MS else NOTES_UPLOAD_INTERVAL_MS
        val deadline = System.currentTimeMillis() + wait
        if (notesUploadJob?.isActive == true && notesUploadDeadline <= deadline) return
        notesUploadJob?.cancel()
        notesUploadDeadline = deadline
        notesUploadJob = scope.launch {
            delay(wait)
            request(SyncScope.DirtyNotes, priority = false)
        }
    }

    /** 离开笔记 / 退到后台：取消 30 秒计时，立刻上传改过的笔记。 */
    suspend fun flushNotes() {
        notesUploadJob?.cancel()
        notesUploadJob = null
        request(SyncScope.DirtyNotes, priority = false)
    }

    /** 离开编辑器时用：页面协程会随返回被取消，所以放到引擎自己的作用域里等落盘后再传。 */
    fun flushNotesSoon(delayMs: Long = 400) {
        scope.launch {
            delay(delayMs)
            flushNotes()
        }
    }

    /**
     * 任务保存后立刻只上传这几条。
     * 300ms 只用来合并同一次保存里连续的几个字段写入（标题 / 备注 / 日期各写一次），不是防抖等待。
     */
    fun scheduleTodoUpload(ids: List<String>) {
        pendingTodoIds.addAll(ids)
        todoUploadJob?.cancel()
        todoUploadJob = scope.launch {
            delay(300)
            val batch = pendingTodoIds.toList()
            pendingTodoIds.clear()
            // 另起协程：之后再来的保存只取消上面的 300ms 等待，不会中断正在跑的上传
            scope.launch { batch.forEach { request(SyncScope.OneTodo(it), priority = false) } }
        }
    }

    suspend fun syncRound() = request(SyncScope.All, priority = false)

    suspend fun syncSingleNote(noteId: String) = request(SyncScope.OneNote(noteId))

    /**
     * 请求同步某个范围。空闲时立刻跑；正在同步时排进队列（`priority` 为 true 则插到队首，
     * 比定时轮、普通请求都先跑）。队列里已有 `All` 时其余范围被它覆盖，新来 `All` 会清掉其余范围。
     */
    suspend fun request(target: SyncScope, priority: Boolean = true) {
        // 实际同步放在引擎自己的作用域里跑：调用方多是 LaunchedEffect / 页面协程，
        // 切 tab、返回都会取消它，同步若跟着被取消就会半途失败（日志里的 LeftCompositionCancellationException）。
        // 这里只 join 等结果；调用方被取消时 join 抛出，但同步照常跑完。
        scope.launch { dispatch(target, priority) }.join()
    }

    private suspend fun dispatch(target: SyncScope, priority: Boolean) {
        if (_status.value.isSyncing) {
            enqueue(target, priority)
            return
        }
        run(target)
        while (pending.isNotEmpty()) run(pending.removeFirst())
    }

    private fun enqueue(target: SyncScope, front: Boolean) {
        if (SyncScope.All in pending) return
        if (target == SyncScope.All) {
            pending.clear(); pending.addLast(target); return
        }
        pending.remove(target)
        if (front) pending.addFirst(target) else pending.addLast(target)
    }

    private fun hasDirtyNotes(): Boolean =
        NoteStore.notes.value.any { SyncCursorStore.cursor(it.id, false)?.dirty == true }

    private fun hasDirtyTodos(): Boolean =
        TodoStore.todos.value.any { SyncCursorStore.cursor(it.id, true)?.dirty == true }

    private suspend fun run(target: SyncScope) {
        // 只传「改过的」且当前没有可传内容时，不动状态、不发请求
        if (target == SyncScope.DirtyNotes && !hasDirtyNotes()) return
        if (target == SyncScope.DirtyTodos && !hasDirtyTodos()) return
        if (target is SyncScope.OneTodo && SyncCursorStore.cursor(target.id, true)?.dirty != true) return

        _status.value = Status(state = "syncing", lastMessage = "", lastSyncDate = _status.value.lastSyncDate)
        var kicked = false
        debugLog("同步开始 $target")
        try {
            val token = BaiduPanClient.validToken()
            DeviceRegistry.ensureNotKicked(token)
            var noteCount = 0
            var noteFailed = 0
            var todoCount = 0
            var todoFailed = 0
            var message: String? = null
            fun notes(r: Pair<Int, Int>) { noteCount = r.first; noteFailed = r.second }
            fun todos(r: Pair<Int, Int>) { todoCount = r.first; todoFailed = r.second }
            when (target) {
                SyncScope.All -> {
                    migrateNotesIfNeeded(token)
                    notes(syncNotes(token))
                    runCatching { syncNotebooks(token) }.onFailure { debugLog("笔记本同步失败：${it.message}") }
                    todos(syncTodos(token))
                    message = "笔记 $noteCount 篇 · 任务 $todoCount 条"
                }
                SyncScope.Notes -> {
                    migrateNotesIfNeeded(token)
                    notes(syncNotes(token))
                    runCatching { syncNotebooks(token) }.onFailure { debugLog("笔记本同步失败：${it.message}") }
                    message = "已同步笔记 $noteCount 篇"
                }
                is SyncScope.OneNote -> {
                    notes(syncNotes(token, onlyNoteId = target.id))
                    message = "已同步本篇"
                }
                SyncScope.DirtyNotes -> {
                    notes(syncNotes(token, dirtyOnly = true))
                    message = "已上传笔记"
                }
                SyncScope.Todos -> {
                    todos(syncTodos(token))
                    message = "已同步任务"
                }
                SyncScope.DirtyTodos -> {
                    todos(syncTodos(token, dirtyOnly = true))
                    message = "已上传任务"
                }
                is SyncScope.OneTodo -> {
                    todos(syncTodos(token, onlyTodoId = target.id))
                    message = "已上传任务"
                }
            }
            val failed = noteFailed + todoFailed
            _status.value = Status(
                state = if (failed > 0) "failed" else "success",
                lastMessage = if (failed > 0) "$failed 篇未同步，下一轮自动重试" else message ?: "",
                lastSyncDate = Iso8601.now()
            )
            debugLog(
                if (failed > 0) "同步部分失败 $target 笔记失败 $noteFailed · 任务失败 $todoFailed"
                else "同步成功 $target"
            )
        } catch (e: DeviceKickedException) {
            kicked = true
            _status.value = Status(state = "failed", lastMessage = e.message ?: "本机已被下线", lastSyncDate = _status.value.lastSyncDate)
            debugLog("同步失败（被下线）：${e.message}")
        } catch (e: Exception) {
            _status.value = Status(state = "failed", lastMessage = e.message ?: "同步失败", lastSyncDate = _status.value.lastSyncDate)
            debugLog("同步失败：${e.javaClass.simpleName} ${e.message}")
        }
        if (kicked) pending.clear()
    }

    /**
     * 「同步」按钮语义：强制用云端本篇覆盖本地（远程 → 本地）。
     * 与 syncSingleNote 的三方合并不同：不做胜负判断、不受 shouldSkipFetch 短路影响，
     * 走 applyRemoteNote 统一处理附件下载与基线游标更新。
     * UI 只在本地无未同步变更时展示该入口，因此覆盖是安全的。
     * 返回远端笔记供调用方刷新编辑器；云端无此篇或失败返回 null。
     */
    suspend fun pullSingleNote(noteId: String): Note? {
        if (_status.value.isSyncing) return null
        _status.value = Status(state = "syncing", lastMessage = "", lastSyncDate = _status.value.lastSyncDate)
        return try {
            val token = BaiduPanClient.validToken()
            val remoteMtimes = BaiduPanClient.listJsonFiles(notesDir, token)
            val cloudKey = remoteMtimes.keys.firstOrNull { it.equals(noteId, ignoreCase = true) }
            if (cloudKey == null) {
                _status.value = Status(state = "failed", lastMessage = "云端暂无本篇", lastSyncDate = _status.value.lastSyncDate)
                debugLog("同步本篇：云端列表里没有 $noteId")
                return null
            }
            val bytes = BaiduPanClient.downloadData("$notesDir/$cloudKey.json", token)
            val remote = AppStorage.json.decodeFromString(Note.serializer(), String(bytes))
            applyRemoteNote(remote, remote.rev, remoteMtimes[cloudKey], token)
            _status.value = Status(state = "success", lastMessage = "已从云端更新本篇", lastSyncDate = Iso8601.now())
            debugLog("同步本篇成功：${remote.title} rev=${remote.rev}")
            remote
        } catch (e: Exception) {
            _status.value = Status(state = "failed", lastMessage = e.message ?: "同步失败", lastSyncDate = _status.value.lastSyncDate)
            debugLog("同步本篇失败 ${e.javaClass.simpleName} ${e.message}")
            null
        }
    }

    // ---------- 笔记同步 ----------

    /** 返回 Pair(成功篇数, 失败篇数)。失败篇会留到下一轮重试，绝不静默算成功。 */
    private suspend fun syncNotes(
        token: String, onlyNoteId: String? = null, dirtyOnly: Boolean = false
    ): Pair<Int, Int> {
        val remoteMtimes = BaiduPanClient.listJsonFiles(notesDir, token)
        var ids = (NoteStore.notes.value.map { it.id } + remoteMtimes.keys).distinct()
        if (onlyNoteId != null) ids = ids.filter { it.equals(onlyNoteId, ignoreCase = true) }
        if (dirtyOnly) {
            // 只处理本机改过、还没上传的笔记；别端变化留给打开笔记时的拉取和定时全量轮
            ids = ids.filter { NoteStore.note(it) != null && SyncCursorStore.cursor(it, false)?.dirty == true }
        }
        var done = 0
        var failed = 0
        for (id in ids) {
            // 单篇失败（上传冲突、附件缺失等）绝不能中断整轮，否则云端其余笔记全拉不下来
            runCatching { syncOneNote(id, remoteMtimes[id.uppercase()], token) }
                .onSuccess { done++ }
                .onFailure {
                    failed++
                    debugLog("笔记同步失败 $id：${it.javaClass.simpleName} ${it.message}")
                }
        }
        return done to failed
    }

    private suspend fun syncOneNote(id: String, serverMtime: Long?, token: String) {
        val cursor = SyncCursorStore.cursor(id, false)
        if (SyncRules.shouldSkipFetch(serverMtime, cursor)) return

        val remoteNote: Note? = if (serverMtime == null) {
            // 目录里没有这个文件。若本篇曾经同步过，说明是列表抖动或刚被别端删除，
            // 此时绝不能当成「云端无此文件」拿本地旧版覆盖，跳过等下一轮列表恢复。
            if (cursor != null && cursor.baseRev != 0L) {
                debugLog("云端列表暂缺 $id（本篇曾同步），跳过本轮避免误覆盖")
                return
            }
            null
        } else {
            // 目录里既然有这个 id，任何下载/解析失败都只是临时错误：一律上抛，
            // 交给 syncNotes 记失败、下一轮重试，绝不退化成用本地内容覆盖云端笔记。
            val bytes = BaiduPanClient.downloadData("$notesDir/$id.json", token)
            AppStorage.json.decodeFromString(Note.serializer(), String(bytes))
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
            // create 返回 errno=0 即代表文件已写入。回读只用于确认，不能因为直链抖动
            // 就 rev+1 反复重传（旧实现最后会误报「连续三次没能写上云端」）。
            uploadNote(toPublish, token)

            var back: Note? = null
            for (retry in 0 until 3) {
                if (retry > 0) delay(400L * retry)
                back = try {
                    val bytes = BaiduPanClient.downloadData("$notesDir/${note.id}.json", token)
                    AppStorage.json.decodeFromString(Note.serializer(), String(bytes))
                } catch (e: Exception) {
                    null
                }
                if (back != null) break
            }
            val confirmed = back
            if (confirmed == null) {
                debugLog("上传已成功但回读未确认 ${note.id}，留待下轮校验")
                return toPublish
            }
            if (confirmed.rev == rev && SyncRules.sameTime(confirmed.updatedAt, toPublish.updatedAt)) {
                return toPublish
            }
            val step = SyncRules.afterFetch(
                dirty = true,
                remoteRev = confirmed.rev,
                remoteUpdatedAt = confirmed.updatedAt,
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

    private suspend fun applyRemoteTodo(remote: Todo, serverMtime: Long?, token: String) {
        downloadAttachmentsFor(remote.notes, remote.id, token)
        TodoStore.upsert(remote)
        SyncCursorStore.set(
            SyncCursor(remote.rev, remote.updatedAt, serverMtime, false,
                attachmentPaths(remote.notes), baseTodo = remote),
            remote.id, true
        )
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

    private suspend fun syncTodos(
        token: String, dirtyOnly: Boolean = false, onlyTodoId: String? = null
    ): Pair<Int, Int> {
        val remoteMtimes = BaiduPanClient.listJsonFiles(todosDir, token)
        var ids = (TodoStore.todos.value.map { it.id } + remoteMtimes.keys).distinct()
        if (dirtyOnly) {
            // 只处理本机改过、还没上传的任务；别端的变化留给全量轮拉取
            ids = ids.filter { TodoStore.todo(it) != null && SyncCursorStore.cursor(it, true)?.dirty == true }
        }
        if (onlyTodoId != null) ids = ids.filter { it.equals(onlyTodoId, ignoreCase = true) }
        var done = 0
        var failed = 0
        for (id in ids) {
            runCatching { syncOneTodo(id, remoteMtimes[id.uppercase()], token) }
                .onSuccess { done++ }
                .onFailure {
                    failed++
                    debugLog("任务同步失败 $id：${it.javaClass.simpleName} ${it.message}")
                }
        }
        return done to failed
    }

    private suspend fun syncOneTodo(id: String, serverMtime: Long?, token: String) {
        val cursor = SyncCursorStore.cursor(id, true)
        // 补齐缺失的备注图片：老版本只下载笔记图片、漏掉任务图片，
        // 这里对已同步任务也兜底补齐（downloadAttachmentsFor 会跳过已存在的文件）。
        val localTodo = TodoStore.todo(id)
        if (localTodo != null && localTodo.notes.isNotBlank()) {
            downloadAttachmentsFor(localTodo.notes, id, token)
        }
        if (SyncRules.shouldSkipFetch(serverMtime, cursor)) return
        val remoteTodo: Todo? = if (serverMtime == null) {
            if (cursor != null && cursor.baseRev != 0L) {
                debugLog("云端列表暂缺任务 $id（曾同步），跳过本轮避免误覆盖")
                return
            }
            null
        } else {
            val bytes = BaiduPanClient.downloadData("$todosDir/$id.json", token)
            AppStorage.json.decodeFromString(Todo.serializer(), String(bytes))
        }
        val local = TodoStore.todo(id)
        val dirty = cursor?.dirty == true
        when (val step = SyncRules.afterFetch(
            dirty, remoteTodo?.rev, remoteTodo?.updatedAt, local?.updatedAt, cursor
        )) {
            is SyncRules.SyncStep.RememberMtime -> {
                if (serverMtime != null) {
                    SyncCursorStore.set(
                        (cursor ?: SyncCursor()).copy(serverMtime = serverMtime, baseTodo = local ?: cursor?.baseTodo),
                        id, true
                    )
                }
            }
            is SyncRules.SyncStep.UseRemote -> if (remoteTodo != null) {
                applyRemoteTodo(remoteTodo, serverMtime, token)
            }
            is SyncRules.SyncStep.Upload -> {
                val l = local ?: return
                val published = publishTodo(l, step.rev, token)
                if (published != null) {
                    // 写回必须走 upsert（不标脏），否则会再触发一轮同步形成风暴
                    TodoStore.upsert(published)
                    SyncCursorStore.set(
                        SyncCursor(published.rev, published.updatedAt, serverMtime, false,
                            attachmentPaths(published.notes), baseTodo = published),
                        id, true
                    )
                }
            }
            is SyncRules.SyncStep.Conflict -> {
                if (local != null && remoteTodo != null) {
                    val merge = SyncRules.mergeTodos(cursor?.baseTodo, local, remoteTodo)
                    if (merge is SyncRules.TodoMergeResult.Merged) {
                        // 字段合并成功：在原 id 上以 max(rev)+1 写回，不产生冲突副本
                        downloadAttachmentsFor(merge.todo.notes, merge.todo.id, token)
                        val published = publishTodo(merge.todo, maxOf(local.rev, remoteTodo.rev) + 1, token)
                        if (published != null) {
                            TodoStore.upsert(published)
                            SyncCursorStore.set(
                                SyncCursor(published.rev, published.updatedAt, null, false,
                                    attachmentPaths(published.notes), baseTodo = published),
                                id, true
                            )
                        }
                    } else if (SyncRules.remoteWins(local.updatedAt, remoteTodo.updatedAt)) {
                        applyRemoteTodo(remoteTodo, serverMtime, token)
                    } else {
                        val published = publishTodo(local, maxOf(local.rev, remoteTodo.rev) + 1, token)
                        if (published != null) {
                            TodoStore.upsert(published)
                            SyncCursorStore.set(
                                SyncCursor(published.rev, published.updatedAt, null, false,
                                    attachmentPaths(published.notes), baseTodo = published),
                                id, true
                            )
                        }
                    }
                } else if (remoteTodo != null) {
                    applyRemoteTodo(remoteTodo, serverMtime, token)
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
            // 先传备注图片再传任务 JSON：别端拉到任务时图片已经在云端（已传过的按 cursor 清单跳过）
            if (!todo.purged) uploadAttachmentsFor(todo.notes, todo.id, token, true)
            val json = AppStorage.json.encodeToString(Todo.serializer(), toPublish).toByteArray()
            BaiduPanClient.uploadData(json, "$todosDir/${todo.id}.json", token)
            var back: Todo? = null
            for (retry in 0 until 3) {
                if (retry > 0) delay(400L * retry)
                back = try {
                    val bytes = BaiduPanClient.downloadData("$todosDir/${todo.id}.json", token)
                    AppStorage.json.decodeFromString(Todo.serializer(), String(bytes))
                } catch (e: Exception) {
                    null
                }
                if (back != null) break
            }
            val confirmed = back
            if (confirmed == null) {
                debugLog("任务上传已成功但回读未确认 ${todo.id}，留待下轮校验")
                return toPublish
            }
            if (confirmed.rev == rev && SyncRules.sameTime(confirmed.updatedAt, toPublish.updatedAt)) {
                return toPublish
            }
            val step = SyncRules.afterFetch(true, confirmed.rev, confirmed.updatedAt, toPublish.updatedAt,
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
                debugLog("附件下载成功 $rel (${bytes.size}B)")
            } catch (e: Exception) {
                debugLog("附件下载失败 $rel：${e.javaClass.simpleName} ${e.message}")
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
