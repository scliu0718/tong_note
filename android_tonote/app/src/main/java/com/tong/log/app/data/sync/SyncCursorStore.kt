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

    /** 云端文件名统一大写，本地 id 可能是小写；所有读写都归到同一个 key，避免游标丢失。 */
    private fun MutableMap<String, SyncCursor>.keyFor(id: String): String =
        if (containsKey(id)) id else keys.firstOrNull { it.equals(id, ignoreCase = true) } ?: id

    fun cursor(id: String, isTodo: Boolean): SyncCursor? {
        val map = if (isTodo) todoCursors else noteCursors
        return map[map.keyFor(id)]
    }

    fun markDirty(id: String, isTodo: Boolean) {
        val map = if (isTodo) todoCursors else noteCursors
        val key = map.keyFor(id)
        val cur = map[key] ?: SyncCursor()
        map[key] = cur.copy(dirty = true)
        save()
    }

    fun set(cursor: SyncCursor, id: String, isTodo: Boolean) {
        val map = if (isTodo) todoCursors else noteCursors
        map[map.keyFor(id)] = cursor
        save()
    }

    fun clear(id: String, isTodo: Boolean) {
        val map = if (isTodo) todoCursors else noteCursors
        map.remove(map.keyFor(id))
        save()
    }
}
