package com.tong.log.app.data.codec

/**
 * 旧 Markdown 笔记 → 受控 HTML（有损简化，与 Mac 端 MarkdownConverter 语义一致）：
 *   - 代码围栏整行丢弃；标题仅到 h3；连续普通行合并为单段
 *   - 图片 ![](路径) 仅当指向 attachments/ 时才转 <img>
 */
object MarkdownConverter {
    fun toHtml(markdown: String): String {
        val sb = StringBuilder()
        var inList = false
        var listIsOrdered = false
        var paragraph = StringBuilder()

        fun flushParagraph() {
            val t = paragraph.toString().trim()
            if (t.isNotEmpty()) sb.append("<p>").append(inline(t)).append("</p>")
            paragraph = StringBuilder()
        }

        fun closeList() {
            if (inList) {
                sb.append(if (listIsOrdered) "</ol>" else "</ul>")
                inList = false
            }
        }

        for (raw in markdown.lines()) {
            val line = raw.trimEnd()
            val trimmed = line.trim()
            when {
                trimmed.isEmpty() -> {
                    closeList(); flushParagraph()
                }
                trimmed.startsWith("```") -> { closeList(); flushParagraph() } // 围栏丢弃
                trimmed.startsWith("### ") -> { closeList(); flushParagraph(); sb.append("<h3>").append(inline(trimmed.removePrefix("### "))).append("</h3>") }
                trimmed.startsWith("## ") -> { closeList(); flushParagraph(); sb.append("<h2>").append(inline(trimmed.removePrefix("## "))).append("</h2>") }
                trimmed.startsWith("# ") -> { closeList(); flushParagraph(); sb.append("<h1>").append(inline(trimmed.removePrefix("# "))).append("</h1>") }
                trimmed.startsWith("> ") -> { closeList(); flushParagraph(); sb.append("<blockquote>").append(inline(trimmed.removePrefix("> "))).append("</blockquote>") }
                trimmed.startsWith("- ") || trimmed.startsWith("* ") -> {
                    flushParagraph()
                    if (!inList || listIsOrdered) { closeList(); sb.append("<ul>"); inList = true; listIsOrdered = false }
                    sb.append("<li>").append(inline(trimmed.removePrefix("- ").removePrefix("* "))).append("</li>")
                }
                trimmed.matches(Regex("""^\d+\.\s+.*""")) -> {
                    flushParagraph()
                    if (!inList || !listIsOrdered) { closeList(); sb.append("<ol>"); inList = true; listIsOrdered = true }
                    sb.append("<li>").append(inline(trimmed.substringAfter(". "))).append("</li>")
                }
                else -> {
                    closeList()
                    if (paragraph.isNotEmpty()) paragraph.append(' ')
                    paragraph.append(trimmed)
                }
            }
        }
        closeList(); flushParagraph()
        return sb.toString()
    }

    private fun inline(s: String): String {
        var r = RichTextHTMLCodec.escapeText(s)
        r = Regex("""!\[([^\]]*)]\((attachments/[^)]+)\)""").replace(r) {
            val alt = it.groupValues[1]
            val src = it.groupValues[2]
            "<img src=\"${RichTextHTMLCodec.escapeAttribute(src)}\" alt=\"${RichTextHTMLCodec.escapeAttribute(alt)}\">"
        }
        r = Regex("""\[([^\]]+)]\(([^)]+)\)""").replace(r) {
            "<a href=\"${RichTextHTMLCodec.escapeAttribute(it.groupValues[2])}\">${it.groupValues[1]}</a>"
        }
        r = Regex("""`([^`]+)`""").replace(r) { "<code>${it.groupValues[1]}</code>" }
        r = Regex("""\*\*([^*]+)\*\*""").replace(r) { "<strong>${it.groupValues[1]}</strong>" }
        r = Regex("""\*([^*]+)\*""").replace(r) { "<em>${it.groupValues[1]}</em>" }
        return r
    }
}
