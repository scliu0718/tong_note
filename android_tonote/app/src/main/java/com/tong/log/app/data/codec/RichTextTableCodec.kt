package com.tong.log.app.data.codec

/**
 * 表格受控 HTML：与 Mac 端 RichTextTableCodec 保持一致。
 *   - `<table data-fullwidth="0">` 表示固定列宽（不铺满）
 *   - `<td data-align="center|right">` / `<td data-bg="#RRGGBB">`
 *   - 左对齐 / 无背景不写属性；满宽是默认值、不写 data-fullwidth
 */
object RichTextTableCodec {

    /** 单元格背景 20 色（首个 null 表示无颜色）。 */
    val cellColors: List<String?> = listOf(
        null, "#F2F2F4", "#D8D9DE", "#B9BCC5", "#8E929B",
        "#FBE4D6", "#FCEFB4", "#DCEFD8", "#D6EFF0", "#D9E6F8",
        "#5B5B60", "#8B8F98", "#7A5AF8", "#C86DD7", "#E5484D",
        "#F59E42", "#F7CE46", "#35A45A", "#2EB3AB", "#2F7BEE"
    )

    fun makeTableHtml(rows: Int, columns: Int, fullWidth: Boolean = false): String {
        val sb = StringBuilder()
        sb.append("<table")
        if (!fullWidth) sb.append(" data-fullwidth=\"0\"")
        sb.append(">")
        repeat(rows.coerceIn(1, 100)) {
            sb.append("<tr>")
            repeat(columns.coerceIn(1, 32)) { sb.append("<td><p></p></td>") }
            sb.append("</tr>")
        }
        sb.append("</table>")
        return sb.toString()
    }

    /** 插入表格 / 增删行列后，给编辑器 HTML 模板使用的表格基础样式。 */
    fun tableCss(): String = """
        table{border-collapse:collapse;width:100%;margin:8px 0;}
        table[data-fullwidth="0"]{width:auto;}
        td,th{border:1px solid #d9dadf;padding:6px 10px;min-width:60px;vertical-align:top;}
        td[data-align="center"]{text-align:center;}
        td[data-align="right"]{text-align:right;}
        td[data-align="justify"]{text-align:justify;}
    """.trimIndent()
}
