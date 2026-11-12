package com.tong.log.app.data.store

import com.tong.log.app.data.model.Notebooks

object NotebookStore {
    private const val KEY_CUSTOM = "customNotebooks"
    private const val KEY_REMOVED = "removedNotebooks"

    val customNotebooks: List<String>
        get() = readList(KEY_CUSTOM)

    val removedNotebooks: List<String>
        get() = readList(KEY_REMOVED)

    val all: List<String>
        get() = listOf(Notebooks.ALL) + Notebooks.builtIn + customNotebooks

    fun add(name: String): Result<Unit> {
        val n = name.trim()
        return when {
            n.isEmpty() -> Result.failure(IllegalArgumentException("笔记本名称不能为空"))
            n == Notebooks.ALL -> Result.failure(IllegalArgumentException("「全部笔记」是内置项，不能用作笔记本名称"))
            n.length > 12 -> Result.failure(IllegalArgumentException("笔记本名称最多 12 个字"))
            all.contains(n) -> Result.failure(IllegalArgumentException("该笔记本已存在"))
            else -> {
                writeList(KEY_CUSTOM, customNotebooks + n)
                Result.success(Unit)
            }
        }
    }

    fun remove(name: String) {
        if (!Notebooks.isBuiltIn(name)) {
            writeList(KEY_CUSTOM, customNotebooks - name)
            writeList(KEY_REMOVED, removedNotebooks + name)
        }
    }

    private fun readList(key: String): List<String> =
        AppStorage.prefs.getString(key, "")?.split("\n")?.filter { it.isNotBlank() } ?: emptyList()

    private fun writeList(key: String, list: List<String>) {
        AppStorage.prefs.edit().putString(key, list.joinToString("\n")).apply()
    }
}
