package com.tong.log.app.data.store

import com.tong.log.app.data.model.AvatarPresets
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

object AvatarStore {
    private const val KEY_AVATAR_ID = "avatar.id"

    private val _currentId = MutableStateFlow("tong")
    val currentId: StateFlow<String> = _currentId.asStateFlow()

    fun load() {
        _currentId.value = AppStorage.prefs.getString(KEY_AVATAR_ID, "tong") ?: "tong"
    }

    fun select(id: String) {
        _currentId.value = id
        AppStorage.prefs.edit().putString(KEY_AVATAR_ID, id).apply()
    }

    fun current() = AvatarPresets.byId(_currentId.value)
}
