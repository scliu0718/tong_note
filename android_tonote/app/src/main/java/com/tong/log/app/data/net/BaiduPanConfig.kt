package com.tong.log.app.data.net

import com.tong.log.app.data.store.AppStorage
import kotlinx.serialization.Serializable

/**
 * 百度网盘开放平台配置：凭证从 assets/baidupan.secret.json 读取（与 Mac 端
 * baidupan.secret.json 同结构，二者都可复用同一份凭证）。
 */
object BaiduPanConfig {
    const val AUTHORIZE_URL = "https://openapi.baidu.com/oauth/2.0/authorize"
    const val TOKEN_URL = "https://openapi.baidu.com/oauth/2.0/token"
    const val XPAN_API = "https://pan.baidu.com/rest/2.0/xpan/nas"
    const val FILE_API = "https://pan.baidu.com/rest/2.0/xpan/file"
    const val SUPERFILE2 = "https://c.pcs.baidu.com/rest/2.0/pcs/superfile2"

    const val REDIRECT_URI = "tongnote://oauth/callback"
    const val SCOPE = "basic,netdisk"
    const val REMOTE_ROOT = "/apps/tong_log"

    @Serializable
    data class Secret(val appKey: String, val appSecret: String, val appID: String)

    @Volatile
    var secret: Secret? = null
        private set

    fun loadSecret() {
        secret = try {
            val text = AppStorage.context.assets.open("baidupan.secret.json")
                .bufferedReader().use { it.readText() }
            AppStorage.json.decodeFromString<Secret>(text)
        } catch (e: Exception) {
            null
        }
    }

    val appKey: String get() = secret?.appKey ?: ""
    val appSecret: String get() = secret?.appSecret ?: ""
    val appID: String get() = secret?.appID ?: ""
    val isConfigured: Boolean get() = appKey.isNotEmpty() && appSecret.isNotEmpty()
}
