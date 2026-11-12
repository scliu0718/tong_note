package com.tong.log.app.data.net

import com.tong.log.app.data.model.Iso8601
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonPrimitive
import java.time.Instant

@Serializable
data class BaiduTokenResponse(
    @SerialName("access_token") val accessToken: String? = null,
    @SerialName("refresh_token") val refreshToken: String? = null,
    @SerialName("expires_in") val expiresIn: Long? = null,
    val scope: String? = null,
    val error: String? = null,
    @SerialName("error_description") val errorDescription: String? = null
)

/** 本地保存的 token（与 Mac 端 baidupan-token.json 字段一致）。 */
@Serializable
data class BaiduPanToken(
    val accessToken: String,
    val refreshToken: String,
    val expiresIn: Long = 0,
    val scope: String = "",
    val createdAt: String = Iso8601.now()
) {
    val isExpired: Boolean
        get() = try {
            val created = Iso8601.parse(createdAt)
            Instant.now().isAfter(created.plusSeconds(expiresIn - 5 * 60))
        } catch (e: Exception) {
            true
        }
}

@Serializable
data class UserInfo(
    val errno: Int? = null,
    @SerialName("baidu_name") val baiduName: String? = null,
    @SerialName("netdisk_name") val netdiskName: String? = null,
    @SerialName("uk") val uk: Long? = null,
    @SerialName("avatar_url") val avatarUrl: String? = null
) {
    /** 界面上展示的账号名：百度账号 > 网盘昵称 > uk。 */
    val displayName: String
        get() = baiduName?.takeIf { it.isNotBlank() }
            ?: netdiskName?.takeIf { it.isNotBlank() }
            ?: uk?.toString()
            ?: ""
}

@Serializable
data class Quota(val total: Long = 0, val used: Long = 0)

@Serializable
data class FileMeta(
    @SerialName("fs_id") val fsId: Long = 0,
    val path: String = "",
    @SerialName("isdir") val isDir: Int = 0,
    val size: Long = 0,
    @SerialName("server_mtime") val serverMtime: Long = 0
)

@Serializable
data class ListResponse(
    val errno: Int? = null,
    @SerialName("show_msg") val showMsg: String? = null,
    val list: List<FileMeta> = emptyList()
)

@Serializable
data class PrecreateResponse(
    val errno: Int? = null,
    val uploadid: JsonElement? = null,
    @SerialName("return_type") val returnType: Int? = null
) {
    val uploadIdValue: String?
        get() = (uploadid as? JsonPrimitive)?.content
}

@Serializable
data class CreateResponse(
    val errno: Int? = null,
    @SerialName("fs_id") val fsId: Long? = null,
    val path: String? = null
)

@Serializable
data class DownloadMeta(
    val errno: Int? = null,
    val dlink: String? = null
)

@Serializable
data class DownloadResponse(
    val errno: Int? = null,
    @SerialName("show_msg") val showMsg: String? = null,
    val list: List<DownloadMeta> = emptyList()
)
