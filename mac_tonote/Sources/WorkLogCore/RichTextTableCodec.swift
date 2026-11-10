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
        html += ">"
        for row in table.rows {
            html += "<tr>"
            for cell in row {
                let inner = RichTextHTMLCodec.encode(RichTextDocument(blocks: cell.blocks))
                // 左对齐是默认值，不写属性，保证老笔记编码结果与之前完全一致。
                let align = cell.alignment == .left
                    ? "" : " data-align=\"\(cell.alignment.rawValue)\""
                html += "<td\(align)>\(inner)</td>"
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
                                               alignment: RichTextCellAlignment.fromHTMLAttributes(cellAttributes)))
            }
            if !cells.isEmpty {
                rows.append(cells)
            }
        }
        let columnCount = max(rows.map(\.count).max() ?? 1, 1)
        let weights = RichTextTable.weights(from: attributes["data-widths"], columnCount: columnCount)
        return RichTextTable(rows: rows, columnWeights: weights)
    }
}
