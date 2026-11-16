package com.tong.log.app.data.sync

import com.tong.log.app.data.model.SyncCursor
import com.tong.log.app.data.model.Todo
import com.tong.log.app.data.model.TodoStatus
import java.time.Instant

/**
 * 三方合并规则：用 rev 判断云端是否变化、updatedAt 定冲突胜负、cursor 记基线。
 * 与 Mac 端 SyncRules 语义一致，非简单 last-write-wins。
 */
object SyncRules {
    private const val TOLERANCE_SECONDS = 1L

    fun sameTime(a: String?, b: String?): Boolean {
        if (a == null || b == null) return false
        return runCatching {
            kotlin.math.abs(Instant.parse(a).epochSecond - Instant.parse(b).epochSecond) <= TOLERANCE_SECONDS
        }.getOrDefault(false)
    }

    fun shouldSkipFetch(serverMtime: Long?, cursor: SyncCursor?): Boolean {
        if (cursor == null) return false
        return !cursor.dirty && serverMtime != null && cursor.serverMtime != null &&
            serverMtime == cursor.serverMtime
    }

    sealed interface SyncStep {
        data object Skip : SyncStep
        data object RememberMtime : SyncStep
        data object UseRemote : SyncStep
        data class Upload(val rev: Long) : SyncStep
        data object Conflict : SyncStep
    }

    fun remoteWins(localUpdatedAt: String?, remoteUpdatedAt: String?): Boolean {
        if (remoteUpdatedAt == null) return false
        if (localUpdatedAt == null) return true
        return runCatching {
            val remote = Instant.parse(remoteUpdatedAt)
            val local = Instant.parse(localUpdatedAt)
            !remote.isBefore(local)
        }.getOrDefault(true)
    }

    fun afterFetch(
        dirty: Boolean,
        remoteRev: Long?,
        remoteUpdatedAt: String?,
        localUpdatedAt: String?,
        cursor: SyncCursor?
    ): SyncStep {
        if (remoteRev == null) {
            val baseRev = cursor?.baseRev ?: 0
            var rev = if (baseRev == 0L) 1L else baseRev + (if (dirty) 1 else 0)
            if (rev < 1) rev = 1
            return SyncStep.Upload(rev)
        }
        val baseRev = cursor?.baseRev
        val baseUpdatedAt = cursor?.baseUpdatedAt
        val neverSynced = (baseRev == null || baseRev == 0L) && baseUpdatedAt == null
        if (neverSynced) {
            if (!dirty) return SyncStep.UseRemote
            return if (sameTime(remoteUpdatedAt, localUpdatedAt)) SyncStep.UseRemote else SyncStep.Conflict
        }
        val changed = remoteRev != baseRev || baseUpdatedAt == null || !sameTime(remoteUpdatedAt, baseUpdatedAt)
        if (!dirty) return if (changed) SyncStep.UseRemote else SyncStep.RememberMtime
        if (!changed) {
            val r = (baseRev ?: 0) + 1
            return SyncStep.Upload(if (r < 1) 1 else r)
        }
        return SyncStep.Conflict
    }

    // ---------- 任务字段三方合并（与 Mac 端 SyncRules.mergeTodos 语义一致） ----------

    sealed interface TodoMergeResult {
        data class Merged(val todo: Todo) : TodoMergeResult
        data object Unresolved : TodoMergeResult
    }

    fun mergeTodos(base: Todo?, local: Todo, remote: Todo): TodoMergeResult {
        if (base == null || !local.id.equals(remote.id, ignoreCase = true)) return TodoMergeResult.Unresolved
        if (local.notes != base.notes && remote.notes != base.notes && local.notes != remote.notes) {
            return TodoMergeResult.Unresolved
        }
        val remoteNewer = remoteWins(local.updatedAt, remote.updatedAt)
        val merged = base.copy(
            id = local.id,
            createdAt = base.createdAt,
            title = threeWay(local.title, remote.title, base.title) {
                if (remoteNewer) remote.title else local.title
            },
            notes = threeWay(local.notes, remote.notes, base.notes) { local.notes },
            status = threeWay(local.status, remote.status, base.status) {
                mergeStatus(local.status, remote.status)
            },
            plannedAt = threeWay(local.plannedAt, remote.plannedAt, base.plannedAt) {
                pickDate(local.plannedAt, remote.plannedAt, !remoteNewer)
            },
            startedAt = threeWay(local.startedAt, remote.startedAt, base.startedAt) {
                pickDate(local.startedAt, remote.startedAt, !remoteNewer)
            },
            completedAt = threeWay(local.completedAt, remote.completedAt, base.completedAt) {
                pickDate(local.completedAt, remote.completedAt, !remoteNewer)
            },
            order = threeWay(local.order, remote.order, base.order) {
                if (remoteNewer) remote.order else local.order
            },
            purged = local.purged || remote.purged,
            rev = maxOf(local.rev, remote.rev),
            updatedAt = laterOf(local.updatedAt, remote.updatedAt)
        )
        return TodoMergeResult.Merged(merged)
    }

    private fun <T> threeWay(local: T, remote: T, base: T, bothChanged: () -> T): T = when {
        local == remote -> local
        local == base -> remote
        remote == base -> local
        else -> bothChanged()
    }

    private fun mergeStatus(local: String, remote: String): String {
        if (local == TodoStatus.TERMINATED || remote == TodoStatus.TERMINATED) return TodoStatus.TERMINATED
        fun rank(s: String) = when (s) {
            TodoStatus.DOING -> 1
            TodoStatus.DONE -> 2
            else -> 0
        }
        return if (rank(local) >= rank(remote)) local else remote
    }

    private fun pickDate(local: String?, remote: String?, localNewer: Boolean): String? = when {
        local == null -> remote
        remote == null -> local
        else -> if (localNewer) local else remote
    }

    private fun laterOf(a: String, b: String): String = runCatching {
        if (Instant.parse(a).isBefore(Instant.parse(b))) b else a
    }.getOrDefault(maxOf(a, b))
}
