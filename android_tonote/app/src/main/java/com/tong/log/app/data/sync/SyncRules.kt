package com.tong.log.app.data.sync

import com.tong.log.app.data.model.SyncCursor
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
}
