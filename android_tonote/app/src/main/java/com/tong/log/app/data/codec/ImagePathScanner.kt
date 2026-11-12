package com.tong.log.app.data.codec

import java.io.File

/**
 * 识别文本里混排的本地图片路径（粘贴纯文本时）。
 * 支持 file:// 前缀、绝对路径、路径含空格；跨平台与 Mac 端规则对齐。
 */
object ImagePathScanner {
    private val imageExtensions = listOf("png", "jpg", "jpeg", "gif", "webp", "heic", "heif", "tiff", "tif", "bmp")

    data class Segment(val isImage: Boolean, val text: String)

    /** 从一段文本里拆分出图片路径片段与普通文字片段（保序）。 */
    fun segments(text: String): List<Segment> {
        val result = mutableListOf<Segment>()
        val pattern = Regex("""(?:file://)?/[^\s"'<>()\[\]]+\.(?:png|jpe?g|gif|webp|heic|heif|tiff?|bmp)(?=[\s"',，。；;!?！？]|$)""", RegexOption.IGNORE_CASE)
        var last = 0
        for (m in pattern.findAll(text)) {
            if (m.range.first > last) {
                result.add(Segment(false, text.substring(last, m.range.first)))
            }
            result.add(Segment(true, m.value))
            last = m.range.last + 1
        }
        if (last < text.length) result.add(Segment(false, text.substring(last)))
        return result
    }

    fun containsImagePath(text: String): Boolean = segments(text).any { it.isImage }

    fun extensionOf(path: String): String? {
        val name = File(path).name
        val dot = name.lastIndexOf('.')
        if (dot < 0) return null
        val ext = name.substring(dot + 1).lowercase()
        return if (imageExtensions.contains(ext)) ext else null
    }
}
