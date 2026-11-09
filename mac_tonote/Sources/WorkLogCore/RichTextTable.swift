import Foundation

/// 表格的一个单元格。里面是若干普通段落（加粗、颜色、换段），不再嵌套表格。
public struct RichTextTableCell: Equatable, Sendable {
    public var blocks: [RichTextBlock]

    public init(blocks: [RichTextBlock] = [RichTextBlock()], fillIfEmpty: Bool = true) {
        let kept = blocks.filter { $0.style != .table }
        if kept.isEmpty && fillIfEmpty {
            self.blocks = [RichTextBlock()]
        } else {
            self.blocks = kept
        }
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
            var copy = row.map { RichTextTableCell(blocks: $0.blocks) }
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
        rows.insert(Array(repeating: RichTextTableCell(), count: columnCount), at: insertion)
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
        for row in rows.indices {
            rows[row].insert(RichTextTableCell(), at: insertion)
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
