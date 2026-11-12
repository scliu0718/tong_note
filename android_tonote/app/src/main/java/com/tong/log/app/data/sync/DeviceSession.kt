package com.tong.log.app.data.sync

import android.os.Build
import com.tong.log.app.data.model.DeviceRecord
import com.tong.log.app.data.model.LocalDeviceIdentity
import com.tong.log.app.data.model.newUuid
import com.tong.log.app.data.net.BaiduPanClient
import com.tong.log.app.data.net.BaiduPanConfig
import com.tong.log.app.data.store.AppStorage
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

class DeviceKickedException(message: String) : Exception(message)

/**
 * 设备会话：手动下线设备 + 被标记设备下次校验时自我登出。
 * 没有自动互踢；被下线设备重新登录即可自行解绑。
 */
object DeviceRegistry {
    private val devicesDir = "${BaiduPanConfig.REMOTE_ROOT}/devices"

    @Volatile
    var identity: LocalDeviceIdentity = LocalDeviceIdentity()
        private set

    fun loadIdentity() {
        identity = AppStorage.readJson(AppStorage.deviceFile, LocalDeviceIdentity())
        if (identity.deviceName.isBlank()) {
            identity = identity.copy(deviceName = Build.MODEL ?: "Android")
        }
        saveIdentity()
    }

    fun saveIdentity() {
        AppStorage.writeJson(AppStorage.deviceFile, identity)
    }

    /** 登录后登记：换新 sessionId 并覆盖写自己的设备文件。 */
    suspend fun beginLogin(token: String) {
        identity = identity.copy(sessionId = newUuid())
        saveIdentity()
        val record = DeviceRecord(
            deviceId = identity.deviceId,
            deviceName = identity.deviceName,
            sessionId = identity.sessionId,
            kicked = false
        )
        runCatching {
            BaiduPanClient.uploadData(
                AppStorage.json.encodeToString(DeviceRecord.serializer(), record).toByteArray(),
                "$devicesDir/${identity.deviceId}.json",
                token
            )
        }
    }

    /** 校验本机是否被下线；被下线则抛 DeviceKickedException。 */
    suspend fun ensureNotKicked(token: String) {
        val remote = try {
            val bytes = BaiduPanClient.downloadData("$devicesDir/${identity.deviceId}.json", token)
            AppStorage.json.decodeFromString(DeviceRecord.serializer(), String(bytes))
        } catch (e: Exception) {
            return
        }
        if (remote.kicked && remote.sessionId == identity.sessionId) {
            throw DeviceKickedException("本机已在其他设备被下线")
        }
    }

    suspend fun listDevices(token: String): List<DeviceRecord> = withContext(Dispatchers.IO) {
        val files = BaiduPanClient.listJsonFiles(devicesDir, token)
        val records = mutableListOf<DeviceRecord>()
        for (name in files.keys) {
            val rec = try {
                val bytes = BaiduPanClient.downloadData("$devicesDir/$name.json", token)
                AppStorage.json.decodeFromString(DeviceRecord.serializer(), String(bytes))
            } catch (e: Exception) {
                DeviceRecord(deviceId = name, deviceName = "未命名设备")
            }
            records.add(rec)
        }
        if (records.none { it.deviceId == identity.deviceId }) {
            records.add(
                DeviceRecord(
                    deviceId = identity.deviceId,
                    deviceName = identity.deviceName,
                    sessionId = identity.sessionId,
                    kicked = false
                )
            )
        }
        records.sortedWith(compareBy({ it.deviceId != identity.deviceId }, { it.deviceName }))
    }

    suspend fun kick(deviceId: String, token: String) {
        if (deviceId == identity.deviceId) return
        val bytes = try {
            BaiduPanClient.downloadData("$devicesDir/$deviceId.json", token)
        } catch (e: Exception) {
            return
        }
        val rec = AppStorage.json.decodeFromString(DeviceRecord.serializer(), String(bytes))
        val kicked = rec.copy(kicked = true)
        BaiduPanClient.uploadData(
            AppStorage.json.encodeToString(DeviceRecord.serializer(), kicked).toByteArray(),
            "$devicesDir/$deviceId.json",
            token
        )
    }
}
