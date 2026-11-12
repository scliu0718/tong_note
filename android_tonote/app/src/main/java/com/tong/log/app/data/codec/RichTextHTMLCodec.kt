package com.tong.log.app.data.codec

/**
 * 受控 HTML 的轻量工具：与 Mac 端 WorkLogCore.RichTextHTMLCodec 保持同样的
 * 实体转义表与「逐字符剥标签」的 visibleText 语义。
 */
object RichTextHTMLCodec {

    fun escapeText(s: String): String =
        s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")

    fun escapeAttribute(s: String): String =
        escapeText(s).replace("\"", "&quot;").replace("'", "&#39;")

    fun unescapeEntities(s: String): String {
        var r = s
        val map = linkedMapOf(
            "&amp;" to "&",
            "&lt;" to "<",
            "&gt;" to ">",
            "&quot;" to "\"",
            "&#39;" to "'",
            "&apos;" to "'",
            "&nbsp;" to " "
        )
        for ((k, v) in map) r = r.replace(k, v)
        return r
    }

    /** 剥掉所有标签，取纯文本（用于列表摘要 / 搜索 / 字数统计）。 */
    fun visibleText(html: String): String {
        val sb = StringBuilder(html.length.coerceAtMost(8000))
        var inTag = false
        for (ch in html) {
            when (ch) {
                '<' -> inTag = true
                '>' -> inTag = false
                else -> if (!inTag) sb.append(ch)
            }
        }
        return unescapeEntities(sb.toString()).replace('\u00a0', ' ')
    }

    /** 取正文纯文本长度（去空白），用于字数统计。 */
    fun textLength(html: String): Int =
        visibleText(html).filterNot { it.isWhitespace() }.length
}
