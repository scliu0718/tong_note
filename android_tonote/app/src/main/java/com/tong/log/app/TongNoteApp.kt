package com.tong.log.app

import android.app.Application
import com.tong.log.app.data.net.BaiduPanConfig
import com.tong.log.app.data.net.BaiduPanTokenStore
import com.tong.log.app.data.store.AppStorage
import com.tong.log.app.data.store.AvatarStore
import com.tong.log.app.data.store.NoteStore
import com.tong.log.app.data.store.TodoStore
import com.tong.log.app.data.sync.DeviceRegistry
import com.tong.log.app.data.sync.SyncCursorStore
import com.tong.log.app.ui.AuthState

class TongNoteApp : Application() {
    override fun onCreate() {
        super.onCreate()
        AppStorage.init(this)
        BaiduPanConfig.loadSecret()
        BaiduPanTokenStore.load()
        NoteStore.load()
        TodoStore.load()
        SyncCursorStore.load()
        DeviceRegistry.loadIdentity()
        AvatarStore.load()
        AuthState.init()
    }
}
