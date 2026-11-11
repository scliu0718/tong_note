import AppKit
import WorkLogCore

/// 光标所在的单元格。工具栏据此决定要不要显示增删行列。
public struct RichTextTableCaret: Equatable {
    public var row: Int
    public var column: Int
    public var rowCount: Int
    public var columnCount: Int
}

/// 表格在文本视图里的排版与读回。
///
/// 每个单元格是挂了 `NSTextTableBlock` 的普通段落，加粗、颜色、换段仍走现有文字命令。
/// 同一张表共用一个 `NSTextTable`，并用自定义属性把各段串起来。
/// 回车若复制出新的表对象，`repairIdentity` 会把它扳回原来那张。
enum RichTextTableLayout {

    static let idKey = NSAttributedString.Key("WorkLogTableID")
    static let weightsKey = NSAttributedString.Key("WorkLogTableColumnWeights")
    static let cellBackgroundKey = NSAttributedString.Key("WorkLogTableCellBackground")
    static let fullWidthKey = NSAttributedString.Key("WorkLogTableFullWidth")
    /// 插入表格、增删行列会改掉单元格之间的换行，这些编辑需要绕过「不许拆开表格」的检查。
    static var allowsStructuralEdits = false

    private static let borderColor = NSColor(srgbRed: 0.85, green: 0.86, blue: 0.88, alpha: 1)

    struct ReadResult {
        var table: RichTextTable
        var range: NSRange
        var next: Int
    }

    // MARK: - 模型 → 属性串

    static func attributedString(for table: RichTextTable, attachments: AttachmentStore) -> NSAttributedString {
        let result = NSMutableAttributedString()
        guard table.rowCount > 0, table.columnCount > 0 else { return result }

        let textTable = NSTextTable()
        textTable.numberOfColumns = table.columnCount
        textTable.collapsesBorders = true
        textTable.hidesEmptyCells = false
        applyTableInset(textTable)
        // 默认每列固定 100pt；勾选「匹配笔记宽度」后按比例铺满编辑区。
        let fixedWidth = !table.fullWidth
        if fixedWidth {
            textTable.setContentWidth(CGFloat(table.columnCount) * RichTextTable.fixedColumnWidth,
                                      type: .absoluteValueType)
        } else {
            textTable.setContentWidth(100, type: .percentageValueType)
        }

        let tableID = UUID().uuidString
        let weights = resolvedWeights(for: table)
        let weightsAttribute = table.encodedWeights
        let fullWidthValue = table.fullWidth ? "1" : "0"

        for (rowIndex, row) in table.rows.enumerated() {
            for (columnIndex, cell) in row.enumerated() {
                let cellBlock = makeCellBlock(table: textTable,
                                              row: rowIndex,
                                              column: columnIndex,
                                              weight: weights[columnIndex],
                                              fixedWidth: fixedWidth,
                                              backgroundHex: cell.backgroundHex)
                let paragraphStyle = cellParagraphStyle(block: cellBlock, alignment: cell.alignment)
                let cellBlocks = cell.blocks.isEmpty ? [RichTextBlock()] : cell.blocks
                let plan = RichTextBridge.markerPlan(for: cellBlocks.map { ($0.style, $0.listLevel) })
                for (index, cellBlockModel) in cellBlocks.enumerated() {
                    let rendered = RichTextBridge.renderBlock(cellBlockModel,
                                                             marker: plan[index],
                                                             attachments: attachments,
                                                             paragraphStyle: paragraphStyle)
                    let piece = NSMutableAttributedString()
                    piece.append(rendered.content)
                    piece.append(NSAttributedString(string: "\n", attributes: rendered.attributes))
                    let full = NSRange(location: 0, length: piece.length)
                    guard full.length > 0 else { continue }
                    piece.addAttribute(.paragraphStyle, value: paragraphStyle, range: full)
                    piece.addAttribute(idKey, value: tableID, range: full)
                    piece.addAttribute(fullWidthKey, value: fullWidthValue, range: full)
                    if let weightsAttribute {
                        piece.addAttribute(weightsKey, value: weightsAttribute, range: full)
                    }
                    if let background = cell.backgroundHex {
                        piece.addAttribute(cellBackgroundKey, value: background, range: full)
                    }
                    result.append(piece)
                }
            }
        }
        return result
    }

    /// 表格若是正文最后一块，后面补一个空段落，光标才能点到表格下面。
    static func trailingParagraph() -> NSAttributedString {
        let style = RichTextBridge.paragraphStyle(for: .paragraph)
        let attributes: [NSAttributedString.Key: Any] = [
            .font: RichTextBridge.font(size: RichTextBridge.defaultFontSize, bold: false, italic: false),
            .foregroundColor: RichTextBridge.defaultTextColor,
            .paragraphStyle: style,
            RichTextKeys.blockStyle: RichTextBlockStyle.paragraph.rawValue,
            RichTextKeys.baseFontSize: NSNumber(value: Double(RichTextBridge.defaultFontSize)),
            RichTextKeys.listLevel: NSNumber(value: 0)
        ]
        return NSAttributedString(string: "\n", attributes: attributes)
    }

    // MARK: - 属性串 → 模型

    static func read(from attributed: NSAttributedString, at location: Int) -> ReadResult? {
        let ns = attributed.string as NSString
        guard location < ns.length else { return nil }
        let origin = tableOrigin(in: attributed, containing: location)
        guard let key = tableKey(in: attributed, at: origin) else { return nil }

        var entries: [(row: Int, column: Int, block: RichTextBlock,
                       alignment: RichTextCellAlignment, backgroundHex: String?)] = []
        var weightsRaw: String?
        var columnCountHint = 0
        // 属性串没声明宽度模式时先留空，最后从排版对象本身推断，
        // 否则固定 100pt 的表格会被误读成「铺满笔记」，设置单元格后宽度突然变大。
        var declaredFullWidth: Bool?
        var loc = origin
        while loc < ns.length {
            let paragraph = ns.paragraphRange(for: NSRange(location: loc, length: 0))
            let probe = min(paragraph.location, ns.length - 1)
            guard tableKey(in: attributed, at: probe) == key,
                  let cell = tableBlock(in: attributed, at: probe) else { break }
            if weightsRaw == nil {
                weightsRaw = attributed.attribute(weightsKey, at: probe, effectiveRange: nil) as? String
            }
            if declaredFullWidth == nil,
               let raw = attributed.attribute(fullWidthKey, at: probe, effectiveRange: nil) as? String {
                declaredFullWidth = raw != "0"
            }
            columnCountHint = max(columnCountHint, cell.table.numberOfColumns)
            let separator = RichTextBridge.separatorLength(in: ns, range: paragraph)
            let content = NSRange(location: paragraph.location, length: max(0, paragraph.length - separator))
            let paragraphStyle = attributed.attribute(.paragraphStyle,
                                                      at: probe,
                                                      effectiveRange: nil) as? NSParagraphStyle
            let rawBackground = attributed.attribute(cellBackgroundKey,
                                                     at: probe,
                                                     effectiveRange: nil) as? String
            entries.append((cell.startingRow,
                            cell.startingColumn,
                            RichTextBridge.block(from: attributed, range: content),
                            cellAlignment(from: paragraphStyle),
                            RichTextCellColor.normalized(rawBackground)))
            let next = NSMaxRange(paragraph)
            if next <= loc { break }
            loc = next
        }
        guard !entries.isEmpty, loc > origin else { return nil }

        let rowCount = (entries.map(\.row).max() ?? 0) + 1
        let columnCount = max(columnCountHint, (entries.map(\.column).max() ?? 0) + 1)
        var rows = Array(
            repeating: Array(repeating: RichTextTableCell(blocks: [], fillIfEmpty: false), count: columnCount),
            count: rowCount
        )
        for entry in entries where entry.row >= 0 && entry.row < rowCount && entry.column >= 0 && entry.column < columnCount {
            if rows[entry.row][entry.column].blocks.isEmpty {
                // 同一格子里多个段落共用一种对齐与背景色，取第一段为准。
                rows[entry.row][entry.column].alignment = entry.alignment
                rows[entry.row][entry.column].backgroundHex = entry.backgroundHex
            }
            rows[entry.row][entry.column].blocks.append(sanitized(entry.block))
        }
        for row in 0..<rowCount {
            for column in 0..<columnCount where rows[row][column].blocks.isEmpty {
                rows[row][column] = RichTextTableCell()
            }
        }
        // absolute = 每列固定 100pt，percentage = 铺满编辑区；老笔记没有该属性也能正确读回。
        let fixedWidth = isFixedWidth(attributed, at: origin)
        let fullWidth = declaredFullWidth ?? !fixedWidth
        let table = RichTextTable(rows: rows,
                                  columnWeights: RichTextTable.weights(from: weightsRaw, columnCount: columnCount),
                                  fullWidth: fullWidth)
        return ReadResult(table: table,
                          range: NSRange(location: origin, length: loc - origin),
                          next: loc)
    }

    // MARK: - 查询与光标

    static func containsTable(_ storage: NSAttributedString, at location: Int) -> Bool {
        tableKey(in: storage, at: location) != nil
    }

    static func caret(in textView: NSTextView) -> RichTextTableCaret? {
        guard let storage = textView.textStorage, storage.length > 0 else { return nil }
        let probe = min(textView.selectedRange().location, storage.length - 1)
        guard let cell = tableBlock(in: storage, at: probe),
              let read = read(from: storage, at: probe) else { return nil }
        return RichTextTableCaret(row: cell.startingRow,
                                  column: cell.startingColumn,
                                  rowCount: read.table.rowCount,
                                  columnCount: read.table.columnCount)
    }

    /// 单元格坐标（同一张表内）。
    struct CellAddress: Hashable {
        var row: Int
        var column: Int
    }

    /// 选区覆盖到的所有格子。无选区时就是光标所在那一格。
    static func cells(in storage: NSAttributedString,
                      selection: NSRange,
                      tableRange: NSRange) -> Set<CellAddress> {
        let start = max(selection.location, tableRange.location)
        let end = min(NSMaxRange(selection), NSMaxRange(tableRange))
        guard start < end, storage.length > 0 else { return [] }
        let ns = storage.string as NSString
        var result: Set<CellAddress> = []
        var location = start
        while location < end {
            let paragraph = ns.paragraphRange(for: NSRange(location: location, length: 0))
            let probe = min(paragraph.location, ns.length - 1)
            if let cell = tableBlock(in: storage, at: probe) {
                result.insert(CellAddress(row: cell.startingRow, column: cell.startingColumn))
            }
            let next = min(NSMaxRange(paragraph), end)
            if next <= location { break }
            location = next
        }
        return result
    }

    /// 当前光标所在格子的对齐；不在表格里返回 nil。
    static func alignmentAtCaret(in textView: NSTextView) -> RichTextCellAlignment? {
        guard let storage = textView.textStorage, storage.length > 0 else { return nil }
        let probe = min(textView.selectedRange().location, storage.length - 1)
        guard tableKey(in: storage, at: probe) != nil else { return nil }
        let style = storage.attribute(.paragraphStyle,
                                      at: probe,
                                      effectiveRange: nil) as? NSParagraphStyle
        return cellAlignment(from: style)
    }

    /// 属性串没写 `fullWidthKey` 时，从排版对象本身判断宽度模式：
    /// absolute = 每列固定 100pt，percentage = 铺满笔记。
    static func isFixedWidth(_ attributed: NSAttributedString, at location: Int) -> Bool {
        guard let block = tableBlock(in: attributed, at: location) else { return false }
        return block.contentWidthValueType == .absoluteValueType
    }

    /// 只改段落样式的对齐，不重建整张表，因此表格 ID、undo 栈和正在输入的文字都不受影响。
    @discardableResult
    static func setAlignment(_ value: RichTextCellAlignment,
                             for cells: Set<CellAddress>,
                             tableRange: NSRange,
                             in storage: NSTextStorage) -> Bool {
        guard !cells.isEmpty else { return false }
        let wanted = textAlignment(for: value)
        let ns = storage.string as NSString
        let end = min(NSMaxRange(tableRange), ns.length)
        var changed = false
        storage.beginEditing()
        var location = tableRange.location
        while location < end {
            let paragraph = ns.paragraphRange(for: NSRange(location: location, length: 0))
            let probe = min(paragraph.location, ns.length - 1)
            if let cell = tableBlock(in: storage, at: probe),
               cells.contains(CellAddress(row: cell.startingRow, column: cell.startingColumn)),
               let existing = storage.attribute(.paragraphStyle,
                                                at: probe,
                                                effectiveRange: nil) as? NSParagraphStyle,
               existing.alignment != wanted,
               let mutable = existing.mutableCopy() as? NSMutableParagraphStyle {
                mutable.alignment = wanted
                storage.addAttribute(.paragraphStyle, value: mutable, range: paragraph)
                changed = true
            }
            let next = NSMaxRange(paragraph)
            if next <= location { break }
            location = next
        }
        storage.endEditing()
        return changed
    }

    /// 只改目标格子的背景色：不换表格对象、不动列宽和边框、不重建整张表。
    /// 选中多个格子时一次改全部；宽度、光标、undo 栈和正在输入的文字都保持原样。
    @discardableResult
    static func setCellBackground(_ color: NSColor,
                                  hex: String?,
                                  cells: Set<CellAddress>,
                                  tableRange: NSRange,
                                  in storage: NSTextStorage) -> Bool {
        guard !cells.isEmpty else { return false }
        let ns = storage.string as NSString
        let end = min(NSMaxRange(tableRange), ns.length)
        var changed = false
        storage.beginEditing()
        var location = tableRange.location
        while location < end {
            let paragraph = ns.paragraphRange(for: NSRange(location: location, length: 0))
            let probe = min(paragraph.location, ns.length - 1)
            if let cell = tableBlock(in: storage, at: probe),
               cells.contains(CellAddress(row: cell.startingRow, column: cell.startingColumn)),
               let existing = storage.attribute(.paragraphStyle,
                                                at: probe,
                                                effectiveRange: nil) as? NSParagraphStyle {
                if let block = existing.textBlocks.compactMap({ $0 as? NSTextTableBlock }).first {
                    block.backgroundColor = color
                }
                // 换一个新的 paragraphStyle 对象触发 TextKit 重排；textBlocks 仍是同一个 block，
                // 因此 contentWidth、padding、border 全部不变。
                let replacement = (existing.mutableCopy() as? NSMutableParagraphStyle) ?? existing
                storage.addAttribute(.paragraphStyle, value: replacement, range: paragraph)
                if let hex {
                    storage.addAttribute(cellBackgroundKey, value: hex, range: paragraph)
                } else {
                    storage.removeAttribute(cellBackgroundKey, range: paragraph)
                }
                changed = true
            }
            let next = NSMaxRange(paragraph)
            if next <= location { break }
            location = next
        }
        storage.endEditing()
        return changed
    }

    /// 「匹配笔记宽度」只改排版对象的宽度值，不重建表格，避免顺带把单元格属性冲掉。
    @discardableResult
    static func setWidthMode(fullWidth: Bool,
                             tableRange: NSRange,
                             in storage: NSTextStorage) -> Bool {
        let ns = storage.string as NSString
        let end = min(NSMaxRange(tableRange), ns.length)
        var items: [(paragraph: NSRange, block: NSTextTableBlock, style: NSParagraphStyle)] = []
        var location = tableRange.location
        while location < end {
            let paragraph = ns.paragraphRange(for: NSRange(location: location, length: 0))
            let probe = min(paragraph.location, ns.length - 1)
            if let block = tableBlock(in: storage, at: probe),
               let style = storage.attribute(.paragraphStyle,
                                             at: probe,
                                             effectiveRange: nil) as? NSParagraphStyle {
                items.append((paragraph, block, style))
            }
            let next = NSMaxRange(paragraph)
            if next <= location { break }
            location = next
        }
        guard let first = items.first else { return false }

        let table = first.block.table
        let columns = max(table.numberOfColumns, 1)
        let rawWeights = storage.attribute(weightsKey,
                                           at: tableRange.location,
                                           effectiveRange: nil) as? String
        let weights = RichTextTable.weights(from: rawWeights, columnCount: columns)
        let total = weights?.reduce(0, +) ?? 0
        if fullWidth {
            table.setContentWidth(100, type: .percentageValueType)
        } else {
            table.setContentWidth(CGFloat(columns) * RichTextTable.fixedColumnWidth,
                                  type: .absoluteValueType)
        }
        applyTableInset(table)

        storage.beginEditing()
        for item in items {
            if fullWidth {
                var share = 100 / Double(columns)
                if let weights, total > 0, weights.indices.contains(item.block.startingColumn) {
                    share = weights[item.block.startingColumn] / total * 100
                }
                item.block.setContentWidth(CGFloat(share), type: .percentageValueType)
            } else {
                item.block.setContentWidth(RichTextTable.fixedColumnWidth, type: .absoluteValueType)
            }
            let replacement = (item.style.mutableCopy() as? NSMutableParagraphStyle) ?? item.style
            storage.addAttribute(.paragraphStyle, value: replacement, range: item.paragraph)
            storage.addAttribute(fullWidthKey, value: fullWidth ? "1" : "0", range: item.paragraph)
        }
        storage.endEditing()
        return true
    }

    static func moveCaret(in textView: NSTextView, near location: Int, row: Int, column: Int) {
        guard let storage = textView.textStorage, storage.length > 0 else { return }
        let probe = min(max(location, 0), storage.length - 1)
        guard let read = read(from: storage, at: probe) else { return }
        let ns = storage.string as NSString
        var loc = read.range.location
        let end = NSMaxRange(read.range)
        while loc < end && loc < ns.length {
            let paragraph = ns.paragraphRange(for: NSRange(location: loc, length: 0))
            if let cell = tableBlock(in: storage, at: paragraph.location),
               cell.startingRow == row, cell.startingColumn == column {
                textView.setSelectedRange(NSRange(location: paragraph.location, length: 0))
                adoptTypingAttributes(in: textView)
                return
            }
            let next = NSMaxRange(paragraph)
            if next <= loc { break }
            loc = next
        }
    }

    /// 光标在格子里时，把该格的段落样式写进后续输入，避免下一个字掉出表格。
    static func adoptTypingAttributes(in textView: NSTextView) {
        guard let storage = textView.textStorage, storage.length > 0 else { return }
        guard textView.selectedRange().length == 0 else { return }
        let probe = min(textView.selectedRange().location, storage.length - 1)
        guard tableKey(in: storage, at: probe) != nil else { return }

        var typing = textView.typingAttributes
        let attributes = storage.attributes(at: probe, effectiveRange: nil)
        let cellTable = (attributes[.paragraphStyle] as? NSParagraphStyle)?
            .textBlocks.compactMap { $0 as? NSTextTableBlock }.first?.table
        let typingTable = (typing[.paragraphStyle] as? NSParagraphStyle)?
            .textBlocks.compactMap { $0 as? NSTextTableBlock }.first?.table
        // 已经在这张表里打字时，保留用户刚设的加粗 / 颜色。
        // 从表格外进来时，才把字号和颜色对齐到单元格。
        if typingTable !== cellTable {
            if let font = attributes[.font] { typing[.font] = font }
            if let color = attributes[.foregroundColor] { typing[.foregroundColor] = color }
        }
        if let style = attributes[.paragraphStyle] { typing[.paragraphStyle] = style }
        for key in [idKey, weightsKey, cellBackgroundKey, fullWidthKey,
                    RichTextKeys.blockStyle, RichTextKeys.baseFontSize, RichTextKeys.listLevel] {
            if let value = attributes[key] { typing[key] = value }
        }
        typing.removeValue(forKey: RichTextKeys.listMarker)
        typing.removeValue(forKey: .strokeWidth)
        typing.removeValue(forKey: .strokeColor)
        textView.typingAttributes = typing
    }

    /// 改段落样式时保住单元格上的表格块，否则一设标题表格就散。
    /// 同时保住居左 / 居中 / 居右：新段落样式是默认左对齐，不复制就会把对齐冲掉。
    static func keepingTable(_ style: NSParagraphStyle, from existing: NSParagraphStyle?) -> NSParagraphStyle {
        guard let block = existing?.textBlocks.compactMap({ $0 as? NSTextTableBlock }).first,
              let mutable = style.mutableCopy() as? NSMutableParagraphStyle else {
            return style
        }
        mutable.textBlocks = [block]
        mutable.alignment = existing?.alignment ?? .natural
        return mutable
    }

    // MARK: - 不许删掉单元格之间的边界

    static func shouldAllowChange(in textView: NSTextView, range: NSRange, replacement: String?) -> Bool {
        if allowsStructuralEdits { return true }
        guard range.length > 0, let storage = textView.textStorage, storage.length > 0 else { return true }
        let ns = storage.string as NSString
        let end = min(NSMaxRange(range), ns.length)
        guard range.location < end else { return true }

        var keys: [String] = []
        var index = range.location
        while index < end {
            let character = ns.character(at: index)
            if character == 10 || character == 13, isCellBoundary(in: storage, newlineAt: index) {
                if let key = tableKey(in: storage, at: index) ?? (index + 1 < ns.length ? tableKey(in: storage, at: index + 1) : nil) {
                    keys.append(key)
                } else {
                    return false
                }
            }
            index += 1
        }
        guard !keys.isEmpty else { return true }

        for key in Set(keys) {
            guard let span = span(ofTableKey: key, in: storage) else { return false }
            let covers = range.location <= span.location && NSMaxRange(range) >= NSMaxRange(span)
            if !covers { return false }
        }
        return true
    }

    // MARK: - 把复制出来的表对象扳回同一张表

    @discardableResult
    static func repairIdentity(in storage: NSMutableAttributedString) -> Bool {
        let ns = storage.string as NSString
        guard ns.length > 0 else { return false }
        var changed = false
        var canonical: [String: NSTextTable] = [:]
        var location = 0
        while location < ns.length {
            let paragraph = ns.paragraphRange(for: NSRange(location: location, length: 0))
            if let fixed = unified(in: storage, paragraph: paragraph, canonical: &canonical) {
                storage.beginEditing()
                let range = NSRange(location: paragraph.location, length: paragraph.length)
                storage.addAttribute(idKey, value: fixed.key, range: range)
                storage.addAttribute(.paragraphStyle, value: fixed.paragraphStyle, range: range)
                if let weights = fixed.weights {
                    storage.addAttribute(weightsKey, value: weights, range: range)
                }
                if let background = fixed.background {
                    storage.addAttribute(cellBackgroundKey, value: background, range: range)
                }
                if let fullWidth = fixed.fullWidth {
                    storage.addAttribute(fullWidthKey, value: fullWidth, range: range)
                }
                storage.endEditing()
                changed = true
            }
            let next = NSMaxRange(paragraph)
            if next <= location { break }
            location = next
        }
        if separateFromBodyText(in: storage) { changed = true }
        return changed
    }

    /// 表格下沿会伸进下一段大约 1.5pt，所以段前距多留 2pt。上沿改由表格自己的 margin 留出。
    @discardableResult
    static func separateFromBodyText(in storage: NSMutableAttributedString) -> Bool {
        let ns = storage.string as NSString
        guard ns.length > 0 else { return false }
        var ranges: [NSRange] = []
        var location = 0
        while location < ns.length {
            let paragraph = ns.paragraphRange(for: NSRange(location: location, length: 0))
            ranges.append(paragraph)
            let next = NSMaxRange(paragraph)
            if next <= location { break }
            location = next
        }

        func isTable(_ range: NSRange) -> Bool {
            let probe = min(range.location, ns.length - 1)
            return tableBlock(in: storage, at: probe) != nil
        }

        var changed = false
        storage.beginEditing()
        for (index, paragraph) in ranges.enumerated() {
            if isTable(paragraph) { continue }
            let probe = min(paragraph.location, ns.length - 1)
            guard let style = storage.attribute(.paragraphStyle, at: probe, effectiveRange: nil) as? NSParagraphStyle,
                  let mutable = style.mutableCopy() as? NSMutableParagraphStyle else { continue }
            let natural = naturalParagraphSpacing(at: probe, in: storage)
            let followsTable = index > 0 && isTable(ranges[index - 1])
            let wantedBefore = followsTable ? max(natural.before, tableVerticalMargin + 2) : natural.before
            let wantedAfter = natural.after
            if abs(mutable.paragraphSpacingBefore - wantedBefore) < 0.5,
               abs(mutable.paragraphSpacing - wantedAfter) < 0.5 {
                continue
            }
            mutable.paragraphSpacingBefore = wantedBefore
            mutable.paragraphSpacing = wantedAfter
            storage.addAttribute(.paragraphStyle, value: mutable, range: paragraph)
            changed = true
        }
        storage.endEditing()
        return changed
    }

    private static func naturalParagraphSpacing(at location: Int,
                                                in storage: NSAttributedString) -> (before: CGFloat, after: CGFloat) {
        let raw = storage.attribute(RichTextKeys.blockStyle, at: location, effectiveRange: nil) as? String
        let style = raw.flatMap(RichTextBlockStyle.init(rawValue:)) ?? .paragraph
        let level = (storage.attribute(RichTextKeys.listLevel, at: location, effectiveRange: nil) as? NSNumber)?.intValue ?? 0
        let canonical = RichTextBridge.paragraphStyle(for: style, level: level)
        return (canonical.paragraphSpacingBefore, canonical.paragraphSpacing)
    }

    // MARK: - 私有

    private struct CellIdentity: Equatable {
        var key: String
        var row: Int
        var column: Int
    }

    private struct FixedParagraph {
        var key: String
        var paragraphStyle: NSParagraphStyle
        var weights: String?
        var background: String?
        var fullWidth: String?
    }

    private static func sanitized(_ block: RichTextBlock) -> RichTextBlock {
        guard block.style == .table else { return block }
        return RichTextBlock(style: .paragraph, runs: [RichTextRun(text: block.plainText)])
    }

    private static func resolvedWeights(for table: RichTextTable) -> [Double] {
        if let weights = table.columnWeights, weights.count == table.columnCount {
            let sum = weights.reduce(0, +)
            if sum > 0 { return weights.map { $0 / sum } }
        }
        let share = 1 / Double(max(table.columnCount, 1))
        return Array(repeating: share, count: table.columnCount)
    }

    static func makeCellBlock(table: NSTextTable,
                              row: Int,
                              column: Int,
                              weight: Double,
                              fixedWidth: Bool = false,
                              backgroundHex: String? = nil) -> NSTextTableBlock {
        let block = NSTextTableBlock(table: table,
                                     startingRow: row,
                                     rowSpan: 1,
                                     startingColumn: column,
                                     columnSpan: 1)
        applyChrome(block, weight: weight, fixedWidth: fixedWidth, backgroundHex: backgroundHex)
        return block
    }

    /// 表格上沿到上方内容的距离。TextKit 会吃掉大约 2pt，所以实际写入时加 2。
    static let tableTopMargin: CGFloat = 20
    /// 表格下沿到下方文字的距离。
    static let tableVerticalMargin: CGFloat = 10

    /// 整张表右移，给左侧「增加行」灰圆留出位置。
    /// 上下空隙不靠这里：NSTextTable 的纵向 margin 推不开相邻正文，见 `separateFromBodyText`。
    static func applyTableInset(_ table: NSTextTable) {
        table.setWidth(0, type: .absoluteValueType, for: .margin)
        table.setWidth(RichTextTableGutterGeometry.tableLeadingMargin,
                       type: .absoluteValueType,
                       for: .margin,
                       edge: .minX)
        // 上边距放在表格上。正文前的段后距再加一份会叠成两倍，两张表之间又只有这里生效。
        table.setWidth(tableTopMargin + 2, type: .absoluteValueType, for: .margin, edge: .minY)
    }

    static func applyChrome(_ block: NSTextTableBlock,
                            weight: Double,
                            fixedWidth: Bool = false,
                            backgroundHex: String? = nil) {
        if fixedWidth {
            block.setContentWidth(RichTextTable.fixedColumnWidth, type: .absoluteValueType)
        } else {
            block.setContentWidth(CGFloat(weight * 100), type: .percentageValueType)
        }
        block.setWidth(10, type: .absoluteValueType, for: .padding, edge: .minX)
        block.setWidth(10, type: .absoluteValueType, for: .padding, edge: .maxX)
        block.setWidth(6, type: .absoluteValueType, for: .padding, edge: .minY)
        block.setWidth(6, type: .absoluteValueType, for: .padding, edge: .maxY)
        block.setWidth(1, type: .absoluteValueType, for: .border)
        block.setWidth(0, type: .absoluteValueType, for: .margin)
        block.setBorderColor(borderColor)
        let fill = backgroundHex.flatMap { RichTextBridge.color(fromHex: $0) } ?? NSColor.white
        block.backgroundColor = fill
        block.verticalAlignment = .topAlignment
    }

    private static func cellParagraphStyle(block: NSTextTableBlock,
                                           alignment: RichTextCellAlignment) -> NSParagraphStyle {
        let paragraph = NSMutableParagraphStyle()
        paragraph.lineSpacing = 2
        paragraph.paragraphSpacing = 1
        paragraph.paragraphSpacingBefore = 0
        paragraph.alignment = textAlignment(for: alignment)
        paragraph.textBlocks = [block]
        return paragraph
    }

    static func textAlignment(for value: RichTextCellAlignment) -> NSTextAlignment {
        switch value {
        case .left: return .left
        case .center: return .center
        case .right: return .right
        case .justify: return .justified
        }
    }

    static func cellAlignment(from style: NSParagraphStyle?) -> RichTextCellAlignment {
        switch style?.alignment ?? .natural {
        case .center: return .center
        case .right: return .right
        case .justified: return .justify
        default: return .left
        }
    }

    static func tableKey(in attributed: NSAttributedString, at location: Int) -> String? {
        guard location >= 0, location < attributed.length else { return nil }
        if let id = attributed.attribute(idKey, at: location, effectiveRange: nil) as? String, !id.isEmpty {
            return id
        }
        if let block = tableBlock(in: attributed, at: location) {
            return "table-\(ObjectIdentifier(block.table))"
        }
        return nil
    }

    static func tableBlock(in attributed: NSAttributedString, at location: Int) -> NSTextTableBlock? {
        guard location >= 0, location < attributed.length,
              let style = attributed.attribute(.paragraphStyle, at: location, effectiveRange: nil) as? NSParagraphStyle else {
            return nil
        }
        return style.textBlocks.compactMap { $0 as? NSTextTableBlock }.first
    }

    private static func tableOrigin(in attributed: NSAttributedString, containing location: Int) -> Int {
        let ns = attributed.string as NSString
        guard ns.length > 0 else { return 0 }
        let probe = min(max(location, 0), ns.length - 1)
        guard let key = tableKey(in: attributed, at: probe) else {
            return ns.paragraphRange(for: NSRange(location: probe, length: 0)).location
        }
        var paragraph = ns.paragraphRange(for: NSRange(location: probe, length: 0))
        while paragraph.location > 0 {
            let previous = ns.paragraphRange(for: NSRange(location: paragraph.location - 1, length: 0))
            guard tableKey(in: attributed, at: previous.location) == key else { break }
            paragraph = previous
        }
        return paragraph.location
    }

    private static func cellIdentity(in attributed: NSAttributedString, at location: Int) -> CellIdentity? {
        guard let key = tableKey(in: attributed, at: location),
              let block = tableBlock(in: attributed, at: location) else { return nil }
        return CellIdentity(key: key, row: block.startingRow, column: block.startingColumn)
    }

    private static func isCellBoundary(in attributed: NSAttributedString, newlineAt index: Int) -> Bool {
        guard index + 1 < attributed.length else { return false }
        return cellIdentity(in: attributed, at: index) != cellIdentity(in: attributed, at: index + 1)
    }

    private static func span(ofTableKey key: String, in attributed: NSAttributedString) -> NSRange? {
        let ns = attributed.string as NSString
        var location = 0
        while location < ns.length {
            let paragraph = ns.paragraphRange(for: NSRange(location: location, length: 0))
            if tableKey(in: attributed, at: paragraph.location) == key,
               let read = read(from: attributed, at: paragraph.location) {
                return read.range
            }
            let next = NSMaxRange(paragraph)
            if next <= location { break }
            location = next
        }
        return nil
    }

    private static func weightForColumn(_ column: Int, weights raw: String?, columns: Int) -> Double {
        if let weights = RichTextTable.weights(from: raw, columnCount: columns),
           weights.indices.contains(column) {
            let sum = weights.reduce(0, +)
            if sum > 0 { return weights[column] / sum }
        }
        return 1 / Double(max(columns, 1))
    }

    private static func unified(in storage: NSAttributedString,
                                paragraph: NSRange,
                                canonical: inout [String: NSTextTable]) -> FixedParagraph? {
        guard paragraph.length > 0 else { return nil }
        var foundKey: String?
        var foundBlock: NSTextTableBlock?
        var foundStyle: NSParagraphStyle?
        var foundWeights: String?
        var foundBackground: String?
        var foundFullWidth: String?
        var inconsistent = false

        storage.enumerateAttributes(in: paragraph, options: []) { attributes, _, _ in
            if let key = attributes[idKey] as? String, !key.isEmpty {
                if let foundKey, foundKey != key { inconsistent = true }
                else { foundKey = key }
            } else {
                inconsistent = true
            }
            if foundWeights == nil, let weights = attributes[weightsKey] as? String {
                foundWeights = weights
            }
            if foundBackground == nil, let background = attributes[cellBackgroundKey] as? String {
                foundBackground = background
            }
            if foundFullWidth == nil, let fullWidth = attributes[fullWidthKey] as? String {
                foundFullWidth = fullWidth
            }
            guard let style = attributes[.paragraphStyle] as? NSParagraphStyle else {
                inconsistent = true
                return
            }
            if foundStyle == nil { foundStyle = style }
            guard let block = style.textBlocks.compactMap({ $0 as? NSTextTableBlock }).first else {
                inconsistent = true
                return
            }
            if let foundBlock,
               foundBlock.table !== block.table
                || foundBlock.startingRow != block.startingRow
                || foundBlock.startingColumn != block.startingColumn {
                inconsistent = true
            } else if foundBlock == nil {
                foundBlock = block
            }
        }

        guard let foundBlock else { return nil }
        let key = foundKey ?? "table-\(ObjectIdentifier(foundBlock.table))"
        let table = canonical[key] ?? foundBlock.table
        applyTableInset(table)
        canonical[key] = table
        let needsRetarget = foundBlock.table !== table
        guard inconsistent || needsRetarget || foundKey == nil else { return nil }

        let columns = max(table.numberOfColumns, foundBlock.startingColumn + 1)
        let weight = weightForColumn(foundBlock.startingColumn, weights: foundWeights, columns: columns)
        // 宽度模式优先用属性；属性缺失时从被替换的 block 反推，
        // 否则固定 100pt 的表格会在回车、粘贴后被重建成铺满笔记。
        let declaredFixed = foundFullWidth.map { $0 == "0" }
        let inferredFixed = foundBlock.contentWidthValueType == .absoluteValueType
        let fixedWidth = declaredFixed ?? inferredFixed
        let block = makeCellBlock(table: table,
                                  row: foundBlock.startingRow,
                                  column: foundBlock.startingColumn,
                                  weight: weight,
                                  fixedWidth: fixedWidth,
                                  backgroundHex: foundBackground)
        let style = (foundStyle?.mutableCopy() as? NSMutableParagraphStyle) ?? NSMutableParagraphStyle()
        style.textBlocks = [block]
        return FixedParagraph(key: key,
                              paragraphStyle: style,
                              weights: foundWeights,
                              background: foundBackground,
                              fullWidth: fixedWidth ? "0" : "1")
    }
}
