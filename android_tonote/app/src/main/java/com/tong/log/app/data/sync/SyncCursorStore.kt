package com.tong.log.app.data.sync

import com.tong.log.app.data.model.SyncCursor
import com.tong.log.app.data.store.AppStorage

/**
 * 同步基线对照表（只落本机，不上传）。换设备 / 重装后 baseRev=0，靠 afterFetch 的
 * neverSynced 分支按 updatedAt 兜底。
 */
object SyncCursorStore {
    private val noteCursors = mutableMapOf<String, SyncCursor>()
    private val todoCursors = mutableMapOf<String, SyncCursor>()

    fun load() {
        noteCursors.clear()
        noteCursors.putAll(AppStorage.readJson(AppStorage.noteSyncFile, emptyMap<String, SyncCursor>()))
        todoCursors.clear()
        todoCursors.putAll(AppStorage.readJson(AppStorage.todoSyncFile, emptyMap<String, SyncCursor>()))
    }

    fun save() {
        AppStorage.writeJson(AppStorage.noteSyncFile, noteCursors.toMap())
        AppStorage.writeJson(AppStorage.todoSyncFile, todoCursors.toMap())
    }

    fun cursor(id: String, isTodo: Boolean): SyncCursor? =
        (if (isTodo) todoCursors else noteCursors)[id]

    fun markDirty(id: String, isTodo: Boolean) {
        val map = if (isTodo) todoCursors else noteCursors
        val cur = map[id] ?: SyncCursor()
        map[id] = cur.copy(dirty = true)
        save()
    }

    fun set(cursor: SyncCursor, id: String, isTodo: Boolean) {
        (if (isTodo) todoCursors else noteCursors)[id] = cursor
        save()
    }

    fun clear(id: String, isTodo: Boolean) {
        (if (isTodo) todoCursors else noteCursors).remove(id)
        save()
    }
}
