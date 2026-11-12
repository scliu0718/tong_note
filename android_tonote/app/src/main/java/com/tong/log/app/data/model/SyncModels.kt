package com.tong.log.app.data.model

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

/** 与 Mac 端保持完全一致，作为「快同步」虚拟笔记的固定 ID。 */
const val QUICK_SYNC_NOTE_ID = "E1C2D3A4-B5F6-47C8-9D0E-1F2A3B4C5D6E"

@Serializable
data class SyncCursor(
    val baseRev: Long = 0,
    val baseUpdatedAt: String? = null,
    val serverMtime: Long? = null,
    val dirty: Boolean = false,
    val uploadedAttachments: List<String> = emptyList()
)

/** 云端设备记录，字段名与 Mac 端一致（snake_case）。 */
@Serializable
data class DeviceRecord(
    @SerialName("device_id") val deviceId: String,
    @SerialName("device_name") val deviceName: String = "",
    @SerialName("session_id") val sessionId: String = "",
    @SerialName("kicked") val kicked: Boolean = false
)

/** 本机设备身份，存 device.json。 */
@Serializable
data class LocalDeviceIdentity(
    val deviceId: String = newUuid(),
    val sessionId: String = "",
    val deviceName: String = ""
)

object Notebooks {
    const val ALL = "全部笔记"
    const val WORK = "工作日志"
    const val READING = "读书笔记"
    const val INVEST = "投资研究"
    const val IDEA = "灵感速记"

    val builtIn: List<String> = listOf(WORK, READING, INVEST, IDEA)
    fun isBuiltIn(name: String) = builtIn.contains(name)
}

/** 头像预设（存 id 到 preferences）；glyph 与 Mac 端 AvatarStore.presets 保持一致。 */
data class AvatarPreset(
    val id: String,
    val glyph: String,
    val color1: Long,
    val color2: Long,
    val isSymbol: Boolean = false
)

object AvatarPresets {
    // 顺序、id、渐变色逐项对齐 mac_tonote/Sources/WorkLogCore/AvatarStore.swift
    val list = listOf(
        AvatarPreset("tong", "同", 0xFF19B46AL, 0xFF0E8A48L),
        AvatarPreset("leaf", "leaf.fill", 0xFF34C77BL, 0xFF0E8A48L, true),
        AvatarPreset("note", "note.text", 0xFF3AA0FFL, 0xFF0B68D6L, true),
        AvatarPreset("book", "book.closed.fill", 0xFF8B7BF0L, 0xFF5B45D6L, true),
        AvatarPreset("pen", "pencil", 0xFFF5A623L, 0xFFD97A06L, true),
        AvatarPreset("star", "star.fill", 0xFFF2C14EL, 0xFFD9930BL, true),
        AvatarPreset("moon", "moon.stars.fill", 0xFF5B7FB5L, 0xFF2F4C7EL, true),
        AvatarPreset("heart", "heart.fill", 0xFFEF7A85L, 0xFFD0465CL, true)
    )
    fun byId(id: String) = list.firstOrNull { it.id == id } ?: list.first()
}
