package com.tong.log.app.data.sync

/** 一次同步的范围。按页面划分：谁的按钮只同步谁。与 Mac 端 `SyncScope` 一一对应。 */
sealed interface SyncScope {
    /** 启动 / 间隔：笔记 + 笔记本 + 任务。 */
    data object All : SyncScope

    /** 笔记页：全部笔记 + 笔记本。 */
    data object Notes : SyncScope

    /** 单篇（含「快同步」）：拉远端、本机有改动再上传。 */
    data class OneNote(val id: String) : SyncScope

    /** 只上传本机改过的笔记。 */
    data object DirtyNotes : SyncScope

    /** 任务页：拉远端、本机有改动再上传。 */
    data object Todos : SyncScope

    /** 只上传本机改过的任务。 */
    data object DirtyTodos : SyncScope

    /** 只上传这一条任务。 */
    data class OneTodo(val id: String) : SyncScope
}
