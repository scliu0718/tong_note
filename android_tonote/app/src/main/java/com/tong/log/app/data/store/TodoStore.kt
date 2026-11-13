package com.tong.log.app.data.store

import com.tong.log.app.data.model.Iso8601
import com.tong.log.app.data.model.Todo
import com.tong.log.app.data.model.TodoStatus
import com.tong.log.app.data.model.newUuid
import com.tong.log.app.data.sync.SyncCursorStore
import com.tong.log.app.data.sync.SyncEngine
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import java.time.LocalDate

val Todo.isOverdue: Boolean
    get() {
        if (status == TodoStatus.DONE || status == TodoStatus.TERMINATED) return false
        val p = plannedAt ?: return false
        return try {
            Iso8601.parse(p).let { it.isBefore(java.time.Instant.now()) }
        } catch (e: Exception) {
            false
        }
    }

object TodoStore {
    private val _todos = MutableStateFlow<List<Todo>>(emptyList())
    val todos: StateFlow<List<Todo>> = _todos.asStateFlow()

    val active: List<Todo>
        get() = _todos.value.asSequence()
            .filter { !it.purged }
            .sortedWith(
                compareByDescending<Todo> { it.isOverdue }
                    .thenByDescending { it.updatedAt }
            )
            .toList()

    fun todo(id: String): Todo? = _todos.value.firstOrNull { it.id.equals(id, ignoreCase = true) }

    /** 用户改动必须标脏并防抖同步；同步引擎写回一律走 upsert，避免同步风暴。 */
    private fun markChanged(ids: List<String>) {
        if (ids.isEmpty()) return
        ids.forEach { SyncCursorStore.markDirty(it, true) }
        runCatching { SyncEngine.schedule() }
    }

    fun load() {
        _todos.value = AppStorage.readJson(AppStorage.todosFile, emptyList<Todo>())
        save()
    }

    fun save() {
        AppStorage.writeJson(AppStorage.todosFile, _todos.value)
    }

    fun add(title: String): Todo {
        val t = Todo(id = newUuid(), title = title.trim())
        _todos.value = listOf(t) + _todos.value
        save()
        markChanged(listOf(t.id))
        return t
    }

    fun update(todo: Todo) {
        _todos.value = _todos.value.map { if (it.id == todo.id) todo else it }
        save()
        markChanged(listOf(todo.id))
    }

    /** 同步引擎写回用，不标脏。 */
    fun upsert(todo: Todo) {
        _todos.value = if (_todos.value.any { it.id == todo.id }) {
            _todos.value.map { if (it.id == todo.id) todo else it }
        } else {
            _todos.value + todo
        }
        save()
    }

    fun updateTitle(id: String, title: String) {
        _todos.value = _todos.value.map {
            if (it.id == id) it.copy(title = title, updatedAt = Iso8601.now()) else it
        }
        save()
        markChanged(listOf(id))
    }

    fun setNotes(id: String, notes: String) {
        _todos.value = _todos.value.map {
            if (it.id == id) it.copy(notes = notes, updatedAt = Iso8601.now()) else it
        }
        save()
        markChanged(listOf(id))
    }

    fun setPlannedAt(id: String, date: LocalDate?) {
        val iso = date?.let {
            Iso8601.fromEpochMillis(it.atStartOfDay(java.time.ZoneId.systemDefault()).toInstant().toEpochMilli())
        }
        _todos.value = _todos.value.map {
            if (it.id == id) it.copy(plannedAt = iso, updatedAt = Iso8601.now()) else it
        }
        save()
        markChanged(listOf(id))
    }

    fun setStatus(id: String, status: String) {
        val now = Iso8601.now()
        _todos.value = _todos.value.map {
            if (it.id == id) {
                when (status) {
                    TodoStatus.DONE -> it.copy(status = status, completedAt = now, updatedAt = now)
                    TodoStatus.DOING -> it.copy(status = status, startedAt = now, updatedAt = now)
                    else -> it.copy(status = status, updatedAt = now)
                }
            } else it
        }
        save()
        markChanged(listOf(id))
    }

    fun toggleDone(id: String) {
        val t = todo(id) ?: return
        setStatus(id, if (t.status == TodoStatus.DONE) TodoStatus.TODO else TodoStatus.DONE)
    }

    fun remove(id: String) {
        _todos.value = _todos.value.map {
            if (it.id == id) it.copy(purged = true, updatedAt = Iso8601.now()) else it
        }
        save()
        markChanged(listOf(id))
    }

    fun clearCompleted() {
        val ids = _todos.value.filter {
            it.status == TodoStatus.DONE || it.status == TodoStatus.TERMINATED
        }.map { it.id }
        _todos.value = _todos.value.map {
            if (it.status == TodoStatus.DONE || it.status == TodoStatus.TERMINATED) {
                it.copy(purged = true, updatedAt = Iso8601.now())
            } else it
        }
        save()
        markChanged(ids)
    }
}
