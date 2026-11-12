package com.tong.log.app.ui

import android.net.Uri
import com.tong.log.app.data.model.newUuid
import com.tong.log.app.data.net.BaiduPanClient
import com.tong.log.app.data.net.BaiduPanTokenStore
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

    @Volatile
    var pendingState: String? = null
        private set

    fun init() {
        _signedIn.value = BaiduPanTokenStore.current() != null
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
            null
        } catch (e: Exception) {
            e.message ?: "登录失败"
        }
    }

    suspend fun signOut() {
        BaiduPanTokenStore.clear()
        _signedIn.value = false
    }
}
