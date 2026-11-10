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

/// 颜色值统一成 `#RRGGBB`，无效值视为「无颜色」。
public enum RichTextCellColor {
    public static func normalized(_ raw: String?) -> String? {
        guard let raw else { return nil }
        var text = raw.trimmingCharacters(in: .whitespacesAndNewlines).uppercased()
        if text.hasPrefix("#") { text.removeFirst() }
        guard text.count == 6, text.allSatisfy({ $0.isHexDigit }) else { return nil }
        return "#" + text
    }
}

/// 表格的一个单元格。里面是若干普通段落（加粗、颜色、换段），不再嵌套表格。
public struct RichTextTableCell: Equatable, Sendable {
    public var blocks: [RichTextBlock]
    public var alignment: RichTextCellAlignment
    /// 单元格背景色 `#RRGGBB`；nil = 默认白色。
    public var backgroundHex: String?

    public init(blocks: [RichTextBlock] = [RichTextBlock()],
                fillIfEmpty: Bool = true,
                alignment: RichTextCellAlignment = .left,
                backgroundHex: String? = nil) {
        let kept = blocks.filter { $0.style != .table }
        if kept.isEmpty && fillIfEmpty {
            self.blocks = [RichTextBlock()]
        } else {
            self.blocks = kept
        }
        self.alignment = alignment
        self.backgroundHex = RichTextCellColor.normalized(backgroundHex)
    }

    public var plainText: String {
        blocks.map { block in
            block.runs.map { $0.image != nil ? "" : $0.text }.joined()
        }.joined(separator: "\n")
    }
}

/// 一张矩形表格。列宽权重和为 1；nil 表示各列等宽。
public struct RichTextTable: Equatable, Sendable {
    // 只是防御性上限，不能等于工具栏选择器的最大列数：
    // 用户从选择器建了 8 列表格后再点边沿「加列」，若上限也是 8 就会被静默拒绝。
    public static let maxRows = 100
    public static let maxColumns = 16
    /// 默认每列固定 100pt，表格不铺满笔记宽度。
    public static let fixedColumnWidth: CGFloat = 100

    public var rows: [[RichTextTableCell]]
    public var columnWeights: [Double]?
    /// 「匹配笔记宽度」：true = 表格占满编辑区，false = 缩到 80% 便于在旁边写正文。
    public var fullWidth: Bool

    public init(rows: [[RichTextTableCell]],
                columnWeights: [Double]? = nil,
                fullWidth: Bool = true) {
        let columnCount = max(rows.map(\.count).max() ?? 1, 1)
        let source = rows.isEmpty
            ? [Array(repeating: RichTextTableCell(), count: columnCount)]
            : rows
        self.rows = source.map { row in
            var copy = row.map {
                RichTextTableCell(blocks: $0.blocks,
                                  alignment: $0.alignment,
                                  backgroundHex: $0.backgroundHex)
            }
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
        self.fullWidth = fullWidth
    }

    public var rowCount: Int { rows.count }
    public var columnCount: Int { rows.first?.count ?? 0 }

    /// 新建空表：每列固定 100pt，不铺满笔记。用户可在单元格菜单里勾选「匹配笔记宽度」。
    public static func make(rows: Int, columns: Int) -> RichTextTable {
        let rowCount = min(max(rows, 1), maxRows)
        let columnCount = min(max(columns, 1), maxColumns)
        let row = Array(repeating: RichTextTableCell(), count: columnCount)
        return RichTextTable(rows: Array(repeating: row, count: rowCount),
                             columnWeights: nil,
                             fullWidth: false)
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
        let fresh = (0..<columnCount).map { column -> RichTextTableCell in
            guard reference.indices.contains(column) else { return RichTextTableCell() }
            let source = reference[column]
            return RichTextTableCell(alignment: source.alignment,
                                     backgroundHex: source.backgroundHex)
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
            guard rows[row].indices.contains(referenceColumn) else {
                rows[row].insert(RichTextTableCell(), at: insertion)
                continue
            }
            let source = rows[row][referenceColumn]
            rows[row].insert(RichTextTableCell(alignment: source.alignment,
                                               backgroundHex: source.backgroundHex),
                             at: insertion)
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
