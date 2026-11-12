package com.tong.log.app.data.sync

import com.tong.log.app.data.model.SyncCursor
import com.tong.log.app.data.sync.SyncRules.SyncStep
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * SyncRules 三方合并规则的单测。
 *
 * 这组用例的作用不是「提高覆盖率」，而是**给跨端同步语义上锁**：
 * Mac / Windows / Android 三端共享同一套 rev + updatedAt + cursor 约定，
 * 任何一端改了这里的判定逻辑，都会先在这里红掉，而不是等用户丢数据。
 *
 * 修改 SyncRules 前请先读 CLAUDE.md 第 5 节（同步引擎红线）。
 */
class SyncRulesTest {

    private companion object {
        const val T0 = "2026-11-12T09:55:00Z"
        const val T1 = "2026-11-12T10:00:00Z"
        const val T1_PLUS_1S = "2026-11-12T10:00:01Z"
        const val T1_PLUS_2S = "2026-11-12T10:00:02Z"
        const val T2 = "2026-11-12T10:05:00Z"

        /** 无同步历史的游标（等价于游标缺失）。 */
        val EMPTY_CURSOR = SyncCursor()
    }

    // ───────────────────────── sameTime ─────────────────────────

    @Test
    fun `时间字符串完全相同时判定为同一时刻`() {
        assertTrue(SyncRules.sameTime(T1, T1))
    }

    @Test
    fun `一秒内的差异视为同一时刻_容忍文件系统时间精度`() {
        // 这是最关键的回归点：FAT/网盘的 mtime 精度只到秒，
        // 跨端写入后回读可能差 1 秒，不能因此判定为冲突。
        assertTrue(SyncRules.sameTime(T1, T1_PLUS_1S))
        assertTrue(SyncRules.sameTime(T1_PLUS_1S, T1))
    }

    @Test
    fun `超过一秒的差异判定为不同时刻`() {
        assertFalse(SyncRules.sameTime(T1, T1_PLUS_2S))
        assertFalse(SyncRules.sameTime(T1, T2))
    }

    @Test
    fun `不同时区表示同一瞬间时判定为同一时刻`() {
        assertTrue(SyncRules.sameTime("2026-11-12T10:00:00Z", "2026-11-12T18:00:00+08:00"))
    }

    @Test
    fun `任一为空时判定为不同时刻`() {
        assertFalse(SyncRules.sameTime(null, T1))
        assertFalse(SyncRules.sameTime(T1, null))
        assertFalse(SyncRules.sameTime(null, null))
    }

    @Test
    fun `无法解析的时间不抛异常且判定为不同时刻`() {
        // 云端可能返回本地时区偏移的超集格式，解析失败必须降级为「不同」，
        // 而不是让同步线程崩掉。
        assertFalse(SyncRules.sameTime("2026-11-12 10:00:00", T1))
        assertFalse(SyncRules.sameTime("", T1))
    }

    // ───────────────────────── remoteWins ─────────────────────────

    @Test
    fun `远端时间为空时本地优先`() {
        assertFalse(SyncRules.remoteWins(localUpdatedAt = T1, remoteUpdatedAt = null))
    }

    @Test
    fun `本地时间为空且远端有值时远端优先`() {
        assertTrue(SyncRules.remoteWins(localUpdatedAt = null, remoteUpdatedAt = T1))
    }

    @Test
    fun `远端更新时远端优先`() {
        assertTrue(SyncRules.remoteWins(localUpdatedAt = T0, remoteUpdatedAt = T1))
    }

    @Test
    fun `远端更旧时本地优先`() {
        assertFalse(SyncRules.remoteWins(localUpdatedAt = T2, remoteUpdatedAt = T1))
    }

    @Test
    fun `时间完全相同时归远端_双向幂等_避免两端互相覆盖`() {
        assertTrue(SyncRules.remoteWins(localUpdatedAt = T1, remoteUpdatedAt = T1))
    }

    @Test
    fun `远端时间无法解析时兜底为远端优先`() {
        assertTrue(SyncRules.remoteWins(localUpdatedAt = T1, remoteUpdatedAt = "not-a-date"))
    }

    // ───────────────────────── shouldSkipFetch ─────────────────────────

    @Test
    fun `无游标时不跳过拉取`() {
        assertFalse(SyncRules.shouldSkipFetch(serverMtime = 1000L, cursor = null))
    }

    @Test
    fun `本地有未提交改动时必须拉取_即使 mtime 未变`() {
        val cursor = EMPTY_CURSOR.copy(serverMtime = 1000L, dirty = true)
        assertFalse(SyncRules.shouldSkipFetch(serverMtime = 1000L, cursor = cursor))
    }

    @Test
    fun `mtime 与游标一致时跳过拉取`() {
        val cursor = EMPTY_CURSOR.copy(serverMtime = 1000L, dirty = false)
        assertTrue(SyncRules.shouldSkipFetch(serverMtime = 1000L, cursor = cursor))
    }

    @Test
    fun `mtime 变化时重新拉取`() {
        val cursor = EMPTY_CURSOR.copy(serverMtime = 1000L, dirty = false)
        assertFalse(SyncRules.shouldSkipFetch(serverMtime = 2000L, cursor = cursor))
    }

    @Test
    fun `任一侧 mtime 缺失时都不跳过拉取`() {
        val cursorWithoutMtime = EMPTY_CURSOR.copy(serverMtime = null, dirty = false)
        assertFalse(SyncRules.shouldSkipFetch(serverMtime = 1000L, cursor = cursorWithoutMtime))
        assertFalse(SyncRules.shouldSkipFetch(serverMtime = null, cursor = EMPTY_CURSOR.copy(serverMtime = 1000L)))
        assertFalse(SyncRules.shouldSkipFetch(serverMtime = null, cursor = null))
    }

    // ────────────── afterFetch：云端不存在该笔记（remoteRev == null） ──────────────

    @Test
    fun `云端不存在且无游标时按 rev 1 上传`() {
        val step = SyncRules.afterFetch(
            dirty = true,
            remoteRev = null,
            remoteUpdatedAt = null,
            localUpdatedAt = T1,
            cursor = null
        )
        assertEquals(SyncStep.Upload(1L), step)
    }

    @Test
    fun `云端不存在时即使本地干净也要上传_首次建文件`() {
        // 反面直觉但有意为之：本地有内容、云端没有，就是一次首次上传。
        val step = SyncRules.afterFetch(
            dirty = false,
            remoteRev = null,
            remoteUpdatedAt = null,
            localUpdatedAt = T1,
            cursor = null
        )
        assertEquals(SyncStep.Upload(1L), step)
    }

    @Test
    fun `云端不存在时沿用游标 rev 不递增_内容未变只是文件丢了`() {
        val cursor = SyncCursor(baseRev = 5L, baseUpdatedAt = T1)
        val step = SyncRules.afterFetch(
            dirty = false,
            remoteRev = null,
            remoteUpdatedAt = null,
            localUpdatedAt = T1,
            cursor = cursor
        )
        assertEquals(SyncStep.Upload(5L), step)
    }

    @Test
    fun `云端不存在且本地有改动时在游标 rev 上递增`() {
        val cursor = SyncCursor(baseRev = 5L, baseUpdatedAt = T1)
        val step = SyncRules.afterFetch(
            dirty = true,
            remoteRev = null,
            remoteUpdatedAt = null,
            localUpdatedAt = T2,
            cursor = cursor
        )
        assertEquals(SyncStep.Upload(6L), step)
    }

    // ────────────── afterFetch：从未同步过但云端已有内容 ──────────────

    @Test
    fun `首次遇到云端文件且本地干净时直接采用远端`() {
        val step = SyncRules.afterFetch(
            dirty = false,
            remoteRev = 3L,
            remoteUpdatedAt = T1,
            localUpdatedAt = T0,
            cursor = null
        )
        assertEquals(SyncStep.UseRemote, step)
    }

    @Test
    fun `首次遇到云端文件且时间一致时采用远端_避免凭空造冲突`() {
        val step = SyncRules.afterFetch(
            dirty = true,
            remoteRev = 3L,
            remoteUpdatedAt = T1,
            localUpdatedAt = T1,
            cursor = EMPTY_CURSOR
        )
        assertEquals(SyncStep.UseRemote, step)
    }

    @Test
    fun `首次遇到云端文件且本地也改过时间不同时报冲突`() {
        val step = SyncRules.afterFetch(
            dirty = true,
            remoteRev = 3L,
            remoteUpdatedAt = T2,
            localUpdatedAt = T0,
            cursor = EMPTY_CURSOR
        )
        assertEquals(SyncStep.Conflict, step)
    }

    // ────────────── afterFetch：已同步过（有基线） ──────────────

    @Test
    fun `本地干净且云端未变时只记住 mtime 不做写入`() {
        val cursor = SyncCursor(baseRev = 2L, baseUpdatedAt = T1)
        val step = SyncRules.afterFetch(
            dirty = false,
            remoteRev = 2L,
            remoteUpdatedAt = T1_PLUS_1S, // 1 秒抖动不算变化
            localUpdatedAt = T1,
            cursor = cursor
        )
        assertEquals(SyncStep.RememberMtime, step)
    }

    @Test
    fun `本地干净且云端 rev 递增时采用远端`() {
        val cursor = SyncCursor(baseRev = 2L, baseUpdatedAt = T1)
        val step = SyncRules.afterFetch(
            dirty = false,
            remoteRev = 3L,
            remoteUpdatedAt = T2,
            localUpdatedAt = T1,
            cursor = cursor
        )
        assertEquals(SyncStep.UseRemote, step)
    }

    @Test
    fun `本地干净且云端 rev 未变但时间变了时仍采用远端`() {
        // 其他端改了内容但没推进 rev（例如 Mac 端手工落盘），也要能拉下来。
        val cursor = SyncCursor(baseRev = 2L, baseUpdatedAt = T1)
        val step = SyncRules.afterFetch(
            dirty = false,
            remoteRev = 2L,
            remoteUpdatedAt = T2,
            localUpdatedAt = T1,
            cursor = cursor
        )
        assertEquals(SyncStep.UseRemote, step)
    }

    @Test
    fun `本地有改动且云端未变时 rev 加一上传`() {
        val cursor = SyncCursor(baseRev = 2L, baseUpdatedAt = T1)
        val step = SyncRules.afterFetch(
            dirty = true,
            remoteRev = 2L,
            remoteUpdatedAt = T1,
            localUpdatedAt = T2,
            cursor = cursor
        )
        assertEquals(SyncStep.Upload(3L), step)
    }

    @Test
    fun `本地有改动且云端也变了时报冲突_绝不静默覆盖`() {
        val cursor = SyncCursor(baseRev = 2L, baseUpdatedAt = T1)
        val step = SyncRules.afterFetch(
            dirty = true,
            remoteRev = 3L,
            remoteUpdatedAt = T2,
            localUpdatedAt = T2,
            cursor = cursor
        )
        assertEquals(SyncStep.Conflict, step)
    }

    @Test
    fun `有 rev 但没有 baseUpdatedAt 时视为云端已变更`() {
        // 老版本游标缺 updatedAt 字段，只能靠 rev 判断，保守起见当作已变更。
        val cursor = SyncCursor(baseRev = 2L, baseUpdatedAt = null)
        val step = SyncRules.afterFetch(
            dirty = false,
            remoteRev = 2L,
            remoteUpdatedAt = T1,
            localUpdatedAt = T2,
            cursor = cursor
        )
        assertEquals(SyncStep.UseRemote, step)
    }

    // ────────────── 端到端场景：一轮完整同步生命周期 ──────────────

    @Test
    fun `场景_首次上传后第二轮应识别为无变化`() {
        // 第 1 轮：云端没有文件 → 上传 rev 1
        val first = SyncRules.afterFetch(
            dirty = true,
            remoteRev = null,
            remoteUpdatedAt = null,
            localUpdatedAt = T1,
            cursor = null
        )
        assertEquals(SyncStep.Upload(1L), first)

        // 第 2 轮：游标已记录基线，云端 rev 1、时间一致 → 什么都不用做
        val cursor = SyncCursor(baseRev = 1L, baseUpdatedAt = T1)
        val second = SyncRules.afterFetch(
            dirty = false,
            remoteRev = 1L,
            remoteUpdatedAt = T1,
            localUpdatedAt = T1,
            cursor = cursor
        )
        assertEquals(SyncStep.RememberMtime, second)
    }

    @Test
    fun `场景_本地连续编辑时 rev 单调递增`() {
        var cursor = SyncCursor(baseRev = 1L, baseUpdatedAt = T1)
        var rev = 1L

        repeat(3) { round ->
            val step = SyncRules.afterFetch(
                dirty = true,
                remoteRev = rev,
                remoteUpdatedAt = cursor.baseUpdatedAt,
                localUpdatedAt = T2,
                cursor = cursor
            )
            assertTrue("第 ${round + 1} 轮应上传", step is SyncStep.Upload)
            rev = (step as SyncStep.Upload).rev
            cursor = cursor.copy(baseRev = rev)
        }

        assertEquals(4L, rev) // 1 → 2 → 3 → 4，每轮只加一
    }

    @Test
    fun `场景_另一端推送新版本时应采用远端而不是冲突`() {
        val cursor = SyncCursor(baseRev = 4L, baseUpdatedAt = T1)
        val step = SyncRules.afterFetch(
            dirty = false,
            remoteRev = 5L,
            remoteUpdatedAt = T2,
            localUpdatedAt = T1,
            cursor = cursor
        )
        assertEquals(SyncStep.UseRemote, step)
        assertTrue(SyncRules.remoteWins(localUpdatedAt = T1, remoteUpdatedAt = T2))
    }

    @Test
    fun `场景_双向同时编辑必须落到冲突分支`() {
        val cursor = SyncCursor(baseRev = 4L, baseUpdatedAt = T1)
        val step = SyncRules.afterFetch(
            dirty = true,
            remoteRev = 5L,
            remoteUpdatedAt = T2,
            localUpdatedAt = T2,
            cursor = cursor
        )
        assertEquals(SyncStep.Conflict, step)
    }
}
