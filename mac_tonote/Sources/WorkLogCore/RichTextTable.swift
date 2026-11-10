import Foundation

/// 单元格内文字的水平对齐。左对齐是默认值，不写进 HTML。
public enum RichTextCellAlignment: String, Equatable, Sendable, CaseIterable {
    case left
    case center
    case right

    /// 受控 HTML 的 `data-align` 值，同时兼容外部粘贴进来的 `style="text-align:…"`。
    public static func fromHTMLAttributes(_ attributes: [String: String]) -> Self {
        if let raw = attributes["data-align"]?.trimmingCharacters(in: .whitespacesAndNewlines).lowercased(),
           let value = Self(rawValue: raw) {
            return value
        }
        let style = (attributes["style"] ?? "").lowercased()
            .replacingOccurrences(of: " ", with: "")
            .replacingOccurrences(of: "\t", with: "")
        if style.contains("text-align:center") { return .center }
        if style.contains("text-align:right") { return .right }
        return .left
    }
}

/// 表格的一个单元格。里面是若干普通段落（加粗、颜色、换段），不再嵌套表格。
public struct RichTextTableCell: Equatable, Sendable {
    public var blocks: [RichTextBlock]
    public var alignment: RichTextCellAlignment

    public init(blocks: [RichTextBlock] = [RichTextBlock()],
                fillIfEmpty: Bool = true,
                alignment: RichTextCellAlignment = .left) {
        let kept = blocks.filter { $0.style != .table }
        if kept.isEmpty && fillIfEmpty {
            self.blocks = [RichTextBlock()]
        } else {
            self.blocks = kept
        }
        self.alignment = alignment
    }

    public var plainText: String {
        blocks.map { block in
            block.runs.map { $0.image != nil ? "" : $0.text }.joined()
        }.joined(separator: "\n")
    }
}

/// 一张矩形表格。列宽权重和为 1；nil 表示各列等宽。
public struct RichTextTable: Equatable, Sendable {
    public static let maxRows = 50
    public static let maxColumns = 8

    public var rows: [[RichTextTableCell]]
    public var columnWeights: [Double]?

    public init(rows: [[RichTextTableCell]], columnWeights: [Double]? = nil) {
        let columnCount = max(rows.map(\.count).max() ?? 1, 1)
        let source = rows.isEmpty
            ? [Array(repeating: RichTextTableCell(), count: columnCount)]
            : rows
        self.rows = source.map { row in
            var copy = row.map { RichTextTableCell(blocks: $0.blocks, alignment: $0.alignment) }
            if copy.count < columnCount {
                copy.append(contentsOf: Array(repeating: RichTextTableCell(), count: columnCount - copy.count))
            } else if copy.count > columnCount {
                copy = Array(copy.prefix(columnCount))
            }
            return copy
        }
        if let columnWeights, columnWeights.count == columnCount, columnWeights.allSatisfy({ $0 > 0 }) {
            self.columnWeights = columnWeights
        } else {
            self.columnWeights = nil
        }
    }

    public var rowCount: Int { rows.count }
    public var columnCount: Int { rows.first?.count ?? 0 }

    /// 新建空表。两列时左列稍窄，方便写「字段 / 内容」这种表。
    public static func make(rows: Int, columns: Int) -> RichTextTable {
        let rowCount = min(max(rows, 1), maxRows)
        let columnCount = min(max(columns, 1), maxColumns)
        let row = Array(repeating: RichTextTableCell(), count: columnCount)
        let weights: [Double]? = columnCount == 2 ? [0.28, 0.72] : nil
        return RichTextTable(rows: Array(repeating: row, count: rowCount), columnWeights: weights)
    }

    public var plainText: String {
        rows.map { row in
            row.map(\.plainText).joined(separator: "\t")
        }.joined(separator: "\n")
    }

    public var encodedWeights: String? {
        guard let columnWeights, columnWeights.count == columnCount else { return nil }
        return columnWeights.map(Self.formatWeight).joined(separator: ",")
    }

    public static func weights(from raw: String?, columnCount: Int) -> [Double]? {
        guard columnCount > 0, let raw, !raw.isEmpty else { return nil }
        let parts = raw.split(separator: ",").compactMap {
            Double($0.trimmingCharacters(in: .whitespaces))
        }
        guard parts.count == columnCount, parts.allSatisfy({ $0 > 0 }) else { return nil }
        return parts
    }

    @discardableResult
    public mutating func insertRow(at index: Int) -> Bool {
        guard rowCount < Self.maxRows, columnCount > 0 else { return false }
        let insertion = min(max(index, 0), rowCount)
        // 新行沿用相邻行的对齐，用户按「在下方插入行」时不会突然变回左对齐。
        let reference = rows.indices.contains(insertion) ? rows[insertion] : rows[rowCount - 1]
        let fresh = (0..<columnCount).map { column in
            RichTextTableCell(alignment: reference.indices.contains(column)
                ? reference[column].alignment : .left)
        }
        rows.insert(fresh, at: insertion)
        return true
    }

    @discardableResult
    public mutating func removeRow(at index: Int) -> Bool {
        guard rowCount > 1, rows.indices.contains(index) else { return false }
        rows.remove(at: index)
        return true
    }

    @discardableResult
    public mutating func insertColumn(at index: Int) -> Bool {
        guard columnCount < Self.maxColumns, rowCount > 0 else { return false }
        let insertion = min(max(index, 0), columnCount)
        // 新列沿用左邻列（在最左侧插入时沿用原第一列）的对齐。
        let referenceColumn = insertion > 0 ? insertion - 1 : min(insertion, columnCount - 1)
        for row in rows.indices {
            let alignment = rows[row].indices.contains(referenceColumn)
                ? rows[row][referenceColumn].alignment : .left
            rows[row].insert(RichTextTableCell(alignment: alignment), at: insertion)
        }
        columnWeights = nil
        return true
    }

    @discardableResult
    public mutating func removeColumn(at index: Int) -> Bool {
        guard columnCount > 1, index >= 0, index < columnCount else { return false }
        for row in rows.indices {
            rows[row].remove(at: index)
        }
        columnWeights = nil
        return true
    }

    private static func formatWeight(_ value: Double) -> String {
        var text = String(format: "%.4f", value)
        while text.contains("."), text.hasSuffix("0") || text.hasSuffix(".") {
            text.removeLast()
        }
        return text.isEmpty ? "0" : text
    }
}
