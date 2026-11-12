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

/** 头像预设（存 id 到 preferences）。 */
data class AvatarPreset(
    val id: String,
    val glyph: String,
    val color1: Long,
    val color2: Long
)

object AvatarPresets {
    val list = listOf(
        AvatarPreset("tong", "同", 0xFF19B46AL, 0xFF0E8A48L),
        AvatarPreset("leaf", "叶", 0xFF35A45AL, 0xFF147A46L),
        AvatarPreset("note", "记", 0xFF2F7BEEL, 0xFF1A5BD6L),
        AvatarPreset("book", "书", 0xFFC86DD7L, 0xFF8E3FB0L),
        AvatarPreset("pen", "笔", 0xFFF59E42L, 0xFFC9790AL),
        AvatarPreset("star", "星", 0xFFF7CE46L, 0xFFC79A00L),
        AvatarPreset("moon", "月", 0xFF5B5B60L, 0xFF2C2C2EL),
        AvatarPreset("heart", "心", 0xFFE5484DL, 0xFFB02A2FL)
    )
    fun byId(id: String) = list.firstOrNull { it.id == id } ?: list.first()
}
