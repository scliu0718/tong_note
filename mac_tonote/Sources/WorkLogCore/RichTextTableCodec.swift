import Foundation

/// 表格 ↔ 受控 HTML。
///
/// 单元格复用正文那一套段落标签，保证加粗、颜色、换段和普通笔记同一条编解码路径。
///
/// ```html
/// <table data-widths="0.28,0.72">
/// <tr><td><p>姓名</p></td><td><p>刘松</p></td></tr>
/// </table>
/// ```
enum RichTextTableCodec {

    static func encode(_ table: RichTextTable) -> String {
        var html = "<table"
        if let weights = table.encodedWeights {
            html += " data-widths=\"\(RichTextHTMLCodec.escapeAttribute(weights))\""
        }
        // 满宽是默认值，不写属性，保证老笔记编码结果与之前完全一致。
        if !table.fullWidth { html += " data-fullwidth=\"0\"" }
        html += ">"
        for row in table.rows {
            html += "<tr>"
            for cell in row {
                let inner = RichTextHTMLCodec.encode(RichTextDocument(blocks: cell.blocks))
                // 左对齐、无背景色是默认值，不写属性。
                let align = cell.alignment == .left
                    ? "" : " data-align=\"\(cell.alignment.rawValue)\""
                let background = cell.backgroundHex.map { " data-bg=\"\($0)\"" } ?? ""
                html += "<td\(align)\(background)>\(inner)</td>"
            }
            html += "</tr>"
        }
        html += "</table>"
        return html
    }

    static func decode(attributes: [String: String], children: [HTMLNode]) -> RichTextTable {
        var rows: [[RichTextTableCell]] = []
        for child in children {
            guard case .element(let tag, _, let rowChildren) = child, tag == "tr" else { continue }
            var cells: [RichTextTableCell] = []
            for cellNode in rowChildren {
                guard case .element(let cellTag, let cellAttributes, let cellChildren) = cellNode else { continue }
                guard cellTag == "td" || cellTag == "th" else { continue }
                let blocks = RichTextHTMLCodec.blocks(from: cellChildren).filter { $0.style != .table }
                cells.append(RichTextTableCell(blocks: blocks,
                                               alignment: RichTextCellAlignment.fromHTMLAttributes(cellAttributes),
                                               backgroundHex: backgroundHex(from: cellAttributes)))
            }
            if !cells.isEmpty {
                rows.append(cells)
            }
        }
        let columnCount = max(rows.map(\.count).max() ?? 1, 1)
        let weights = RichTextTable.weights(from: attributes["data-widths"], columnCount: columnCount)
        let fullWidth = attributes["data-fullwidth"] != "0"
        return RichTextTable(rows: rows, columnWeights: weights, fullWidth: fullWidth)
    }

    /// 兼容外部粘贴进来的 `style="background-color:#RRGGBB"` 或 `rgb(r,g,b)`。
    private static func backgroundHex(from attributes: [String: String]) -> String? {
        if let raw = attributes["data-bg"] {
            return RichTextCellColor.normalized(raw)
        }
        let style = (attributes["style"] ?? "").lowercased()
        guard let marker = style.range(of: "background-color:") else { return nil }
        var value = String(style[marker.upperBound...])
        if let end = value.firstIndex(where: { $0 == ";" || $0 == "\"" || $0 == "'" }) {
            value = String(value[..<end])
        }
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed.hasPrefix("#") {
            return RichTextCellColor.normalized(trimmed)
        }
        if trimmed.hasPrefix("rgb") {
            let numbers = trimmed.split(whereSeparator: { !$0.isNumber && $0 != "." })
                .compactMap { Double($0) }
            if numbers.count >= 3 {
                let parts = numbers.prefix(3).map { Int(min(max($0, 0), 255)) }
                return String(format: "#%02X%02X%02X", parts[0], parts[1], parts[2])
            }
        }
        return nil
    }
}
