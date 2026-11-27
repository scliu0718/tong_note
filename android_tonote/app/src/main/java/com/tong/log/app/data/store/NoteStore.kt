package com.tong.log.app.data.store

import com.tong.log.app.data.model.Iso8601
import com.tong.log.app.data.model.Note
import com.tong.log.app.data.model.NoteContentFormat
import com.tong.log.app.data.model.QUICK_SYNC_NOTE_ID
import com.tong.log.app.data.model.newUuid
import com.tong.log.app.data.sync.SyncCursorStore
import com.tong.log.app.data.sync.SyncEngine
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

object NoteStore {
    private val _notes = MutableStateFlow<List<Note>>(emptyList())
    val notes: StateFlow<List<Note>> = _notes.asStateFlow()

    val activeNotes: List<Note>
        get() = _notes.value.asSequence()
            .filter { !it.purged && !it.isTrashed }
            .sortedByDescending { it.updatedAt }
            .toList()

    val trashNotes: List<Note>
        get() = _notes.value.asSequence()
            .filter { it.isTrashed && !it.purged }
            .sortedByDescending { it.deletedAt ?: "" }
            .toList()

    val allTags: List<String>
        get() = _notes.value.asSequence()
            .filter { !it.purged && it.id != QUICK_SYNC_NOTE_ID }
            .flatMap { it.tags }
            .distinct()
            .sorted()
            .toList()

    fun note(id: String): Note? = _notes.value.firstOrNull { it.id.equals(id, ignoreCase = true) }

    /**
     * 用户改动必须标脏并触发 1.5 秒防抖同步。
     * 不标脏时三方合并的 dirty 恒为 false，已同步笔记的后续编辑会被判成「干净」，
     * 既不上云、又可能在云端变化时被 UseRemote 覆盖 —— 表现为「上传不成功 / 数据丢失」。
     * 注意：同步引擎写回走 upsert，绝不能标脏，否则会形成同步风暴。
     */
    private fun markChanged(ids: List<String>) {
        if (ids.isEmpty()) return
        ids.forEach { SyncCursorStore.markDirty(it, false) }
        runCatching { SyncEngine.scheduleNotesUpload(quick = QUICK_SYNC_NOTE_ID in ids) }
    }

    fun load() {
        _notes.value = AppStorage.readJson(AppStorage.notesFile, emptyList<Note>())
        // 只有补建了「快同步」笔记才需要落盘；无条件 save() 会在启动时把整个 notes.json 再序列化一遍，
        // 数据大时峰值内存翻倍（曾因此启动即 OOM 崩溃）。
        val before = _notes.value.size
        ensureQuickSyncNote()
        if (_notes.value.size != before) save()
        purgeExpiredTrash()
    }

    fun save() {
        AppStorage.writeJson(AppStorage.notesFile, _notes.value)
    }

    fun addNote(notebook: String): Note {
        // 新建即富文本：编辑器只对 richText 笔记回写正文，默认 markdown 会变成只读、输入不保存
        val n = Note(id = newUuid(), notebook = notebook, contentFormat = NoteContentFormat.RICH_TEXT)
        _notes.value = listOf(n) + _notes.value
        save()
        markChanged(listOf(n.id))
        return n
    }

    fun update(note: Note) {
        _notes.value = _notes.value.map { if (it.id == note.id) note else it }
        save()
        markChanged(listOf(note.id))
    }

    /** 存在则替换，不存在则追加（用于同步收下云端新笔记 / 冲突副本）。不标脏。 */
    fun upsert(note: Note) {
        _notes.value = if (_notes.value.any { it.id == note.id }) {
            _notes.value.map { if (it.id == note.id) note else it }
        } else {
            _notes.value + note
        }
        save()
    }

    fun updateTitle(id: String, title: String) {
        _notes.value = _notes.value.map {
            if (it.id == id) it.copy(title = title, updatedAt = Iso8601.now()) else it
        }
        save()
        markChanged(listOf(id))
    }

    fun updateContent(id: String, body: String, format: String) {
        _notes.value = _notes.value.map {
            if (it.id == id) it.copy(body = body, contentFormat = format, updatedAt = Iso8601.now()) else it
        }
        save()
        markChanged(listOf(id))
    }

    fun setNotebook(id: String, notebook: String) {
        _notes.value = _notes.value.map {
            if (it.id == id) it.copy(notebook = notebook, updatedAt = Iso8601.now()) else it
        }
        save()
        markChanged(listOf(id))
    }

    fun addTag(id: String, tag: String) {
        _notes.value = _notes.value.map {
            if (it.id == id && !it.tags.contains(tag)) it.copy(tags = it.tags + tag) else it
        }
        save()
        markChanged(listOf(id))
    }

    fun removeTag(id: String, tag: String) {
        _notes.value = _notes.value.map {
            if (it.id == id) it.copy(tags = it.tags - tag) else it
        }
        save()
        markChanged(listOf(id))
    }

    fun removeTagEverywhere(tag: String) {
        val ids = _notes.value.filter { it.tags.contains(tag) }.map { it.id }
        _notes.value = _notes.value.map { it.copy(tags = it.tags - tag) }
        save()
        markChanged(ids)
    }

    fun togglePin(id: String) {
        _notes.value = _notes.value.map {
            if (it.id == id) it.copy(pinned = !it.pinned, updatedAt = Iso8601.now()) else it
        }
        save()
        markChanged(listOf(id))
    }

    fun moveToTrash(id: String) {
        _notes.value = _notes.value.map {
            if (it.id == id) it.copy(deletedAt = Iso8601.now(), updatedAt = Iso8601.now(), pinned = false) else it
        }
        save()
        markChanged(listOf(id))
    }

    fun restore(id: String) {
        _notes.value = _notes.value.map {
            if (it.id == id) it.copy(deletedAt = null, updatedAt = Iso8601.now()) else it
        }
        save()
        markChanged(listOf(id))
    }

    fun permanentlyDelete(id: String) {
        _notes.value = _notes.value.map {
            if (it.id == id) it.copy(purged = true, updatedAt = Iso8601.now()) else it
        }
        save()
        AttachmentStore.removeAttachmentsIfUnreferenced(id)
        markChanged(listOf(id))
    }

    /** 回收站笔记保留天数，超过后自动彻底删除（三端统一为 30 天）。 */
    const val TRASH_RETENTION_DAYS = 30

    /**
     * 彻底删除回收站里超过 [TRASH_RETENTION_DAYS] 天的笔记，返回删除篇数。
     * 以 deletedAt（进回收站的时间）为准——updatedAt 会被同步合并刷新，会让 30 天一直往后推。
     * 「快同步」不处理；没有 deletedAt（不在回收站）或时间解析失败的笔记不会被碰。
     * 走与手动彻底删除相同的墓碑流程（purged + 标脏），所以会同步到其它设备。
     */
    @Synchronized
    fun purgeExpiredTrash(now: java.time.Instant = java.time.Instant.now()): Int {
        val cutoff = now.minus(java.time.Duration.ofDays(TRASH_RETENTION_DAYS.toLong()))
        val expired = _notes.value.filter { n ->
            if (!n.isTrashed || n.purged || n.id == QUICK_SYNC_NOTE_ID) return@filter false
            val deleted = runCatching { Iso8601.parse(n.deletedAt!!) }.getOrNull() ?: return@filter false
            !deleted.isAfter(cutoff)
        }.map { it.id }
        if (expired.isEmpty()) return 0
        val ids = expired.toSet()
        val stamp = Iso8601.now()
        _notes.value = _notes.value.map {
            if (it.id in ids) it.copy(purged = true, updatedAt = stamp) else it
        }
        save()
        // 附件只在没有别的笔记引用时才删（与手动彻底删除同一规则）
        expired.forEach { AttachmentStore.removeAttachmentsIfUnreferenced(it) }
        markChanged(expired)
        return expired.size
    }

    /**
     * 把某个笔记本下的笔记整体转移到另一个笔记本（删除笔记本时转到「全部笔记」，与 Mac `moveNotes` 一致）。
     * 必须刷新 updatedAt：它是冲突时的胜负依据，不刷新的话这次转移会被云端旧版本覆盖。
     * 回收站里的笔记也一并转移，恢复后才不会落在已不存在的笔记本里；删除墓碑（purged）不动。
     */
    fun moveNotes(fromNotebook: String, toNotebook: String) {
        val ids = _notes.value.filter { !it.purged && it.notebook == fromNotebook }.map { it.id }
        if (ids.isEmpty()) return
        val now = Iso8601.now()
        _notes.value = _notes.value.map {
            if (!it.purged && it.notebook == fromNotebook) it.copy(notebook = toNotebook, updatedAt = now) else it
        }
        save()
        markChanged(ids)
    }

    private fun ensureQuickSyncNote() {
        if (_notes.value.none { it.id == QUICK_SYNC_NOTE_ID }) {
            val qs = Note(
                id = QUICK_SYNC_NOTE_ID,
                title = "快同步",
                notebook = "全部笔记",
                body = "",
                contentFormat = NoteContentFormat.RICH_TEXT
            )
            _notes.value = _notes.value + qs
        }
    }
}
