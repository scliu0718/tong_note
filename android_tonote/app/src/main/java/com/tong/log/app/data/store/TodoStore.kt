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
import java.time.ZoneId

val Todo.isOverdue: Boolean
    get() {
        if (status == TodoStatus.DONE || status == TodoStatus.TERMINATED) return false
        val p = plannedAt ?: return false
        return try {
            // 与 Mac/Windows 统一：plannedAt 存的是当天零点，过了当天才算逾期。
            // 原来比 Instant.now() 会把「今天到期」的任务零点后即判逾期，三端角标数字对不上。
            Iso8601.parse(p).atZone(ZoneId.systemDefault()).toLocalDate().isBefore(LocalDate.now())
        } catch (e: Exception) {
            false
        }
    }

object TodoStore {
    private val _todos = MutableStateFlow<List<Todo>>(emptyList())
    val todos: StateFlow<List<Todo>> = _todos.asStateFlow()

    /** 逾期数量：底栏「任务」角标与首页「快任务」角标共用；读之前需 collect todos 触发重组。 */
    val overdueCount: Int
        get() = _todos.value.count { !it.purged && it.isOverdue }

    val active: List<Todo>
        get() = _todos.value.asSequence()
            .filter { !it.purged }
            // 与 Mac / Windows 一致：先按状态分组（界面按状态过滤，这里保证比较器是全序），
            // 组内：逾期在前 → 截止时间越近越靠前（没有截止时间的排最后）→
            // 已完成按完成时间新到旧 → 最后按创建时间旧到新。
            .sortedWith(
                compareBy<Todo> { statusRank(it.status) }
                    .thenByDescending { it.isOverdue }
                    .thenBy { epochMillis(it.plannedAt) ?: Long.MAX_VALUE }
                    .thenByDescending { if (it.status == TodoStatus.DONE) epochMillis(it.completedAt) ?: Long.MIN_VALUE else 0L }
                    .thenBy { epochMillis(it.createdAt) ?: Long.MAX_VALUE }
            )
            .toList()

    private fun statusRank(status: String): Int = when (status) {
        TodoStatus.TODO -> 0
        TodoStatus.DOING -> 1
        TodoStatus.DONE -> 2
        else -> 3
    }

    private fun epochMillis(iso: String?): Long? =
        if (iso == null) null else runCatching { Iso8601.parse(iso).toEpochMilli() }.getOrNull()

    fun todo(id: String): Todo? = _todos.value.firstOrNull { it.id.equals(id, ignoreCase = true) }

    /** 用户改动必须标脏并防抖同步；同步引擎写回一律走 upsert，避免同步风暴。 */
    private fun markChanged(ids: List<String>) {
        if (ids.isEmpty()) return
        ids.forEach { SyncCursorStore.markDirty(it, true) }
        runCatching { SyncEngine.scheduleTodoUpload(ids) }
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
