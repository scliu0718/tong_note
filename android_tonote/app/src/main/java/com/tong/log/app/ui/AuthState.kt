package com.tong.log.app.ui

import android.net.Uri
import com.tong.log.app.data.model.newUuid
import com.tong.log.app.data.net.BaiduPanClient
import com.tong.log.app.data.net.BaiduPanTokenStore
import com.tong.log.app.data.net.UserInfo
import com.tong.log.app.data.store.AppStorage
import com.tong.log.app.data.sync.DeviceRegistry
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/**
 * 登录态管理：登录发起 OAuth、回调换取 token、退出登录。
 * Android 端授权改为「唤起系统浏览器完成授权 + tongnote://oauth/callback 回调」。
 */
object AuthState {
    private val _signedIn = MutableStateFlow(false)
    val signedIn: StateFlow<Boolean> = _signedIn.asStateFlow()

    /** 当前登录的百度网盘账号（账号名 / uk），登录后从 uinfo 拉取并缓存。 */
    private val _account = MutableStateFlow<UserInfo?>(null)
    val account: StateFlow<UserInfo?> = _account.asStateFlow()

    @Volatile
    var pendingState: String? = null
        private set

    fun init() {
        _signedIn.value = BaiduPanTokenStore.current() != null
        val prefs = AppStorage.prefs
        val name = prefs.getString("baiduAccountName", null)
        val uk = prefs.getLong("baiduAccountUk", 0L)
        _account.value = if (name.isNullOrBlank()) null else
            UserInfo(baiduName = name, uk = uk.takeIf { it != 0L })
    }

    /** 拉取当前百度账号信息；失败时保留上次缓存，不影响同步主链路。 */
    suspend fun refreshAccount() {
        val info = BaiduPanClient.userInfo(BaiduPanClient.validToken()) ?: return
        if ((info.errno ?: 0) != 0 || info.displayName.isBlank()) return
        _account.value = info
        AppStorage.prefs.edit()
            .putString("baiduAccountName", info.displayName)
            .putLong("baiduAccountUk", info.uk ?: 0L)
            .apply()
    }

    /** 发起浏览器授权，返回要打开的授权 URL。 */
    fun beginBrowserLogin(): String {
        val state = newUuid()
        pendingState = state
        return BaiduPanClient.buildAuthorizeUrl(state, "page")
    }

    /** 处理 tongnote://oauth/callback 回调，返回错误信息（成功则 null）。 */
    suspend fun handleCallback(uri: Uri): String? {
        val code = uri.getQueryParameter("code")
        if (code == null) return "授权被拒绝"
        val state = uri.getQueryParameter("state")
        val pending = pendingState
        pendingState = null
        if (pending != null && state != null && state != pending) {
            return "登录回调校验失败"
        }
        return try {
            val token = BaiduPanClient.exchangeCode(code)
            BaiduPanTokenStore.save(token)
            _signedIn.value = true
            runCatching { DeviceRegistry.beginLogin(token.accessToken) }
            runCatching { refreshAccount() }
            null
        } catch (e: Exception) {
            e.message ?: "登录失败"
        }
    }

    suspend fun signOut() {
        BaiduPanTokenStore.clear()
        AppStorage.prefs.edit()
            .remove("baiduAccountName")
            .remove("baiduAccountUk")
            .apply()
        _account.value = null
        _signedIn.value = false
    }
}
