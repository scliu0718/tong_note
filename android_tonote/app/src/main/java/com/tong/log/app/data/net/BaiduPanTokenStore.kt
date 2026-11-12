package com.tong.log.app.data.net

import com.tong.log.app.data.store.AppStorage

object BaiduPanTokenStore {
    @Volatile
    private var cached: BaiduPanToken? = null

    fun load(): BaiduPanToken? {
        cached = AppStorage.readJson(AppStorage.tokenFile, null as BaiduPanToken?)
        return cached
    }

    fun save(token: BaiduPanToken) {
        cached = token
        AppStorage.writeJson(AppStorage.tokenFile, token)
    }

    fun clear() {
        cached = null
        if (AppStorage.tokenFile.exists()) AppStorage.tokenFile.delete()
    }

    fun current(): BaiduPanToken? = cached
}
