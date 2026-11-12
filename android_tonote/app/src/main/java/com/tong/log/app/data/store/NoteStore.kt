package com.tong.log.app.data.store

import com.tong.log.app.data.model.Iso8601
import com.tong.log.app.data.model.Note
import com.tong.log.app.data.model.NoteContentFormat
import com.tong.log.app.data.model.QUICK_SYNC_NOTE_ID
import com.tong.log.app.data.model.newUuid
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

    fun note(id: String): Note? = _notes.value.firstOrNull { it.id == id }

    fun load() {
        _notes.value = AppStorage.readJson(AppStorage.notesFile, emptyList<Note>())
        ensureQuickSyncNote()
        save()
    }

    fun save() {
        AppStorage.writeJson(AppStorage.notesFile, _notes.value)
    }

    fun addNote(notebook: String): Note {
        val n = Note(id = newUuid(), notebook = notebook)
        _notes.value = listOf(n) + _notes.value
        save()
        return n
    }

    fun update(note: Note) {
        _notes.value = _notes.value.map { if (it.id == note.id) note else it }
        save()
    }

    /** 存在则替换，不存在则追加（用于同步收下云端新笔记 / 冲突副本）。 */
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
    }

    fun updateContent(id: String, body: String, format: String) {
        _notes.value = _notes.value.map {
            if (it.id == id) it.copy(body = body, contentFormat = format, updatedAt = Iso8601.now()) else it
        }
        save()
    }

    fun setNotebook(id: String, notebook: String) {
        _notes.value = _notes.value.map {
            if (it.id == id) it.copy(notebook = notebook, updatedAt = Iso8601.now()) else it
        }
        save()
    }

    fun addTag(id: String, tag: String) {
        _notes.value = _notes.value.map {
            if (it.id == id && !it.tags.contains(tag)) it.copy(tags = it.tags + tag) else it
        }
        save()
    }

    fun removeTag(id: String, tag: String) {
        _notes.value = _notes.value.map {
            if (it.id == id) it.copy(tags = it.tags - tag) else it
        }
        save()
    }

    fun removeTagEverywhere(tag: String) {
        _notes.value = _notes.value.map { it.copy(tags = it.tags - tag) }
        save()
    }

    fun togglePin(id: String) {
        _notes.value = _notes.value.map {
            if (it.id == id) it.copy(pinned = !it.pinned, updatedAt = Iso8601.now()) else it
        }
        save()
    }

    fun moveToTrash(id: String) {
        _notes.value = _notes.value.map {
            if (it.id == id) it.copy(deletedAt = Iso8601.now(), updatedAt = Iso8601.now(), pinned = false) else it
        }
        save()
    }

    fun restore(id: String) {
        _notes.value = _notes.value.map {
            if (it.id == id) it.copy(deletedAt = null, updatedAt = Iso8601.now()) else it
        }
        save()
    }

    fun permanentlyDelete(id: String) {
        _notes.value = _notes.value.map {
            if (it.id == id) it.copy(purged = true, updatedAt = Iso8601.now()) else it
        }
        save()
        AttachmentStore.removeAttachmentsIfUnreferenced(id)
    }

    fun moveNotes(fromNotebook: String, toNotebook: String) {
        _notes.value = _notes.value.map {
            if (it.notebook == fromNotebook) it.copy(notebook = toNotebook) else it
        }
        save()
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
