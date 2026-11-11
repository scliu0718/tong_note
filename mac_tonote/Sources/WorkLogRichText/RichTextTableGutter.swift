import AppKit
import WorkLogCore
import Foundation

/// 表格边沿交互的诊断日志。用户手工点一下加行/加列后，
/// 直接读 `~/Library/Application Support/tonote/table-debug.log` 就能定位链路断在哪。
enum RichTextTableDebug {
    static func log(_ message: String) {
        let line = "[TNTable] \(message)"
        NSLog("%@", line)
        let fm = FileManager.default
        guard let base = fm.urls(for: .applicationSupportDirectory, in: .userDomainMask).first else { return }
        let directory = base.appendingPathComponent("tonote", isDirectory: true)
        try? fm.createDirectory(at: directory, withIntermediateDirectories: true)
        let file = directory.appendingPathComponent("table-debug.log")
        let entry = "\(Date())\t\(line)\n"
        guard let data = entry.data(using: .utf8) else { return }
        if let handle = try? FileHandle(forWritingTo: file) {
            handle.seekToEndOfFile()
            handle.write(data)
            try? handle.close()
        } else {
            try? data.write(to: file)
        }
    }
}

/// 表格边沿的一个常驻小灰圆（插入点）。
/// 顶部横线与列分隔线的交点 → 加列；左侧竖线与行分隔线的交点 → 加行。
struct RichTextTableGutterDot: Equatable {
    enum Kind: Equatable {
        case column
        case row
    }

    var kind: Kind
    /// 插入位置：第 index 列 / 行之前；等于总数时表示在末尾追加。
    var index: Int
    /// 目标表格内任意字符位置，点击时用它定位是哪张表。
    var probe: Int
    /// 灰圆圆心（NSTextView 坐标）
    var point: NSPoint
    /// 插入位置的引导线
    var guideRect: NSRect

    static func == (lhs: Self, rhs: Self) -> Bool {
        lhs.kind == rhs.kind
            && lhs.index == rhs.index
            && lhs.probe == rhs.probe
            && abs(lhs.point.x - rhs.point.x) < 0.5
            && abs(lhs.point.y - rhs.point.y) < 0.5
    }
}

/// 一个单元格的可见矩形，用来放右上角的「下箭头」。
struct RichTextTableCellFrame: Equatable {
    var row: Int
    var column: Int
    var tableProbe: Int
    var rect: NSRect
    /// 该格所有段落覆盖的字符范围，用于判断光标是否在格子里。
    var characterRange: NSRange

    var menuRect: NSRect {
        NSRect(x: rect.maxX - 19, y: rect.minY + 3, width: 16, height: 16)
    }

    static func == (lhs: Self, rhs: Self) -> Bool {
        lhs.row == rhs.row
            && lhs.column == rhs.column
            && lhs.tableProbe == rhs.tableProbe
            && abs(lhs.rect.minX - rhs.rect.minX) < 0.5
            && abs(lhs.rect.minY - rhs.rect.minY) < 0.5
            && lhs.characterRange.location == rhs.characterRange.location
    }
}

struct RichTextTableGutterLayout {
    var dots: [RichTextTableGutterDot]
    var cells: [RichTextTableCellFrame]
}

/// 只负责画灰圆 / 蓝圆 / 单元格下箭头，不接收鼠标事件。
/// `hitTest` 恒返回 nil，事件仍由底下的 NSTextView 处理，不影响选字与光标。
final class RichTextTableGutterView: NSView {

    var layout: RichTextTableGutterLayout? {
        didSet { needsDisplay = true }
    }
    /// 鼠标正指着的小灰圆：它会变成蓝色「+」。
    var activeDot: RichTextTableGutterDot? {
        didSet {
            guard activeDot != oldValue else { return }
            needsDisplay = true
        }
    }
    /// 光标所在格子：右上角显示下箭头。
    var activeCell: RichTextTableCellFrame? {
        didSet {
            guard activeCell != oldValue else { return }
            needsDisplay = true
        }
    }
    var menuHover = false {
        didSet {
            guard menuHover != oldValue else { return }
            needsDisplay = true
        }
    }

    private static let accent = NSColor(srgbRed: 0.20, green: 0.48, blue: 0.92, alpha: 1)
    private static let dotGray = NSColor(srgbRed: 0.79, green: 0.80, blue: 0.83, alpha: 1)
    private static let softBorder = NSColor(srgbRed: 0.87, green: 0.88, blue: 0.90, alpha: 1)

    /// NSTextView 是 flipped 视图，遮罩层必须跟着翻转，否则用排版坐标画出来的按钮会上下颠倒。
    override var isFlipped: Bool { true }
    override var isOpaque: Bool { false }

    override func hitTest(_ point: NSPoint) -> NSView? { nil }

    override func draw(_ dirtyRect: NSRect) {
        guard let layout else { return }

        // 1. 所有常驻小灰圆
        Self.dotGray.setFill()
        for dot in layout.dots where dot != activeDot {
            let r: CGFloat = 3.2
            NSBezierPath(ovalIn: NSRect(x: dot.point.x - r,
                                        y: dot.point.y - r,
                                        width: r * 2,
                                        height: r * 2)).fill()
        }

        // 2. 当前格右上角的「下箭头」
        if let cell = activeCell {
            drawCellMenuButton(cell.menuRect, hovered: menuHover)
        }

        // 3. hover 命中的灰圆：引导线 → 蓝色圆加号（按需求不再显示文字浮窗）
        guard let dot = activeDot else { return }
        Self.accent.withAlphaComponent(0.45).setFill()
        NSBezierPath(rect: dot.guideRect).fill()

        let radius: CGFloat = 10
        let circle = NSRect(x: dot.point.x - radius,
                            y: dot.point.y - radius,
                            width: radius * 2,
                            height: radius * 2)
        Self.accent.setFill()
        NSBezierPath(ovalIn: circle).fill()

        let arm: CGFloat = 4.2
        let plus = NSBezierPath()
        plus.lineWidth = 1.6
        plus.lineCapStyle = .round
        plus.move(to: NSPoint(x: dot.point.x - arm, y: dot.point.y))
        plus.line(to: NSPoint(x: dot.point.x + arm, y: dot.point.y))
        plus.move(to: NSPoint(x: dot.point.x, y: dot.point.y - arm))
        plus.line(to: NSPoint(x: dot.point.x, y: dot.point.y + arm))
        NSColor.white.setStroke()
        plus.stroke()
    }

    private func drawCellMenuButton(_ rect: NSRect, hovered: Bool) {
        let path = NSBezierPath(roundedRect: rect, xRadius: 3, yRadius: 3)
        (hovered ? Self.accent.withAlphaComponent(0.12) : NSColor.white).setFill()
        path.fill()
        Self.softBorder.setStroke()
        path.lineWidth = 1
        path.stroke()

        let center = NSPoint(x: rect.midX, y: rect.midY)
        let chevron = NSBezierPath()
        chevron.lineWidth = 1.2
        chevron.lineCapStyle = .round
        chevron.lineJoinStyle = .round
        chevron.move(to: NSPoint(x: center.x - 3, y: center.y - 1.5))
        chevron.line(to: NSPoint(x: center.x, y: center.y + 1.5))
        chevron.line(to: NSPoint(x: center.x + 3, y: center.y - 1.5))
        (hovered ? Self.accent : NSColor.secondaryLabelColor).setStroke()
        chevron.stroke()
    }
}

/// 从 TextKit 1 的排版结果反推表格的列分隔 x / 行分隔 y。
///
/// NSTextTableBlock 会把同一行的格子横排，每个单元格段落各有一条 line fragment。
/// 以它的矩形为准算出边沿；万一某些版本把整行合成一条 fragment，
/// 就退回按 `data-widths`（或等宽）均分，保证灰圆仍然点得到。
enum RichTextTableGutterGeometry {

    // applyChrome 设 minX/maxX padding 10 + border 1 = 11pt，minY/maxY = 6 + 1 = 7pt。
    // 列：collapsesBorders 让相邻两格共用边框，可见竖线正好落在两格 chrome 的中点 11/2 = 5.5。
    // 行：TextKit 对上下边框不做同样合并，可见横线仍落在完整 chrome 处 = 7（截图实测，
    //     写成 3.5 会让加行圆比横线低 3.5pt，用户反馈「增加行没对齐」）。
    // 灰圆、蓝色加号、引导线共用同一 edge，才能与可见边线重叠。
    private static let columnLeadingInset: CGFloat = 5.5
    private static let columnTrailingInset: CGFloat = 5.5
    private static let verticalChrome: CGFloat = 7
    /// 灰圆离表格边沿的距离（上下左右一致）
    private static let dotInset: CGFloat = 12
    /// 整张表相对正文起点右移的距离。12pt 让灰圆落在文字上，再加 4pt 让表格离文字更远。
    static let tableLeadingMargin: CGFloat = 16
    /// 蓝色加号半径 10。实际点击常常落在圆心偏表格一侧约 15–20pt，
    /// 13pt 的命中框会把按钮本身判成没点中。
    private static let dotHitRadius: CGFloat = 22

    private struct CellRect {
        var row: Int
        var column: Int
        var rect: NSRect
        var characterRange: NSRange
    }

    private struct Group {
        var probe: Int
        var cells: [CellRect]
        var weightsRaw: String?
        var columnCount: Int
    }

    private struct Frame {
        var probe: Int
        var bounds: NSRect
        var columnEdges: [CGFloat]
        var rowEdges: [CGFloat]
        /// 要画灰圆的行边界下标。nil 表示每一道 rowEdges 都画。
        var rowDotIndexes: [Int]?
        var cells: [CellRect]
    }

    static func layout(in textView: NSTextView) -> RichTextTableGutterLayout {
        guard let storage = textView.textStorage,
              let layoutManager = textView.layoutManager,
              let textContainer = textView.textContainer,
              storage.length > 0,
              layoutManager.numberOfGlyphs > 0 else {
            return RichTextTableGutterLayout(dots: [], cells: [])
        }

        let frames = tableFrames(in: textView,
                                 storage: storage,
                                 layoutManager: layoutManager,
                                 textContainer: textContainer)
        let rowDotX = bodyTextMinX(in: textView,
                                   storage: storage,
                                   layoutManager: layoutManager,
                                   textContainer: textContainer)
        var dots: [RichTextTableGutterDot] = []
        var cells: [RichTextTableCellFrame] = []
        for frame in frames {
            for (index, edge) in frame.columnEdges.enumerated() {
                dots.append(RichTextTableGutterDot(
                    kind: .column,
                    index: index,
                    probe: frame.probe,
                    point: NSPoint(x: edge, y: frame.bounds.minY - dotInset),
                    guideRect: NSRect(x: edge - 0.75,
                                      y: frame.bounds.minY - dotInset,
                                      width: 1.5,
                                      height: frame.bounds.height + dotInset * 2)
                ))
            }
            // 加行圆的横坐标锁在正文起点，表格再往右挪时灰圆仍与上面的文字对齐。
            // 只排了屏幕里的若干行时，下标仍是表格里的真实行号，不能按数组序号从 0 重数。
            let rowDotIndexes = frame.rowDotIndexes ?? Array(frame.rowEdges.indices)
            for index in rowDotIndexes where frame.rowEdges.indices.contains(index) {
                let edge = frame.rowEdges[index]
                dots.append(RichTextTableGutterDot(
                    kind: .row,
                    index: index,
                    probe: frame.probe,
                    point: NSPoint(x: rowDotX, y: edge),
                    guideRect: NSRect(x: rowDotX,
                                      y: edge - 0.75,
                                      width: max(frame.bounds.maxX - rowDotX, 1),
                                      height: 1.5)
                ))
            }
            for cell in frame.cells {
                let column = min(max(cell.column, 0), frame.columnEdges.count - 2)
                let row = min(max(cell.row, 0), frame.rowEdges.count - 2)
                let left = frame.columnEdges[column]
                let right = frame.columnEdges[column + 1]
                let top = frame.rowEdges[row]
                let bottom = frame.rowEdges[row + 1]
                let rect = NSRect(x: left, y: top, width: right - left, height: bottom - top)
                cells.append(RichTextTableCellFrame(row: cell.row,
                                                    column: cell.column,
                                                    tableProbe: frame.probe,
                                                    rect: rect,
                                                    characterRange: cell.characterRange))
            }
        }
        return RichTextTableGutterLayout(dots: dots, cells: cells)
    }

    static func dot(at point: NSPoint, in layout: RichTextTableGutterLayout) -> RichTextTableGutterDot? {
        var best: RichTextTableGutterDot?
        var bestScore = CGFloat.greatestFiniteMagnitude
        for dot in layout.dots {
            let dx = abs(dot.point.x - point.x)
            let dy = abs(dot.point.y - point.y)
            // 蓝色圆半径 10。点击常常落在圆心靠表格的一侧，命中框要盖住整个按钮。
            // 主轴（列=横向、行=纵向）权重加倍，避免左上角两个圆互相抢。
            let primary = dot.kind == .column ? dx : dy
            let secondary = dot.kind == .column ? dy : dx
            let primaryLimit = dotHitRadius
            let secondaryLimit = dotHitRadius
            guard primary <= primaryLimit, secondary <= secondaryLimit else { continue }
            let score = primary * 2 + secondary
            if score < bestScore {
                bestScore = score
                best = dot
            }
        }
        return best
    }

    /// 鼠标落在单元格竖边或横边上时，返回对应的拖动光标。
    /// 竖边是左右拖（两道竖线），横边是上下拖。
    static func borderCursor(at point: NSPoint, in layout: RichTextTableGutterLayout) -> NSCursor? {
        let slop: CGFloat = 4
        guard !layout.cells.isEmpty else { return nil }
        var bestVertical = CGFloat.greatestFiniteMagnitude
        var bestHorizontal = CGFloat.greatestFiniteMagnitude
        var inside = false
        for cell in layout.cells {
            let body = cell.rect.insetBy(dx: -slop, dy: -slop)
            if body.contains(point) { inside = true }
            bestVertical = min(bestVertical, abs(point.x - cell.rect.minX), abs(point.x - cell.rect.maxX))
            bestHorizontal = min(bestHorizontal, abs(point.y - cell.rect.minY), abs(point.y - cell.rect.maxY))
        }
        guard inside else { return nil }
        let nearVertical = bestVertical <= slop
        let nearHorizontal = bestHorizontal <= slop
        if nearVertical && (!nearHorizontal || bestVertical <= bestHorizontal) {
            return .resizeLeftRight
        }
        if nearHorizontal {
            return .resizeUpDown
        }
        return nil
    }

    static func cell(at characterIndex: Int,
                     in layout: RichTextTableGutterLayout) -> RichTextTableCellFrame? {
        layout.cells.first { cell in
            cell.characterRange.location <= characterIndex
                && characterIndex < NSMaxRange(cell.characterRange)
        }
    }

    /// 正文第一个字的左缘。没有正文时用编辑区文字起点，保证灰圆仍对齐文字开始的位置。
    private static func bodyTextMinX(in textView: NSTextView,
                                    storage: NSTextStorage,
                                    layoutManager: NSLayoutManager,
                                    textContainer: NSTextContainer) -> CGFloat {
        let origin = textView.textContainerOrigin
        let fallback = origin.x + textContainer.lineFragmentPadding
        let ns = storage.string as NSString
        var location = 0
        while location < ns.length {
            let paragraph = ns.paragraphRange(for: NSRange(location: location, length: 0))
            let probe = min(paragraph.location, ns.length - 1)
            if paragraph.length > 1,
               RichTextTableLayout.tableKey(in: storage, at: probe) == nil {
                let glyphs = layoutManager.glyphRange(
                    forCharacterRange: NSRange(location: probe, length: 1),
                    actualCharacterRange: nil)
                if glyphs.length > 0, glyphs.location < layoutManager.numberOfGlyphs {
                    let rect = layoutManager.boundingRect(forGlyphRange: glyphs, in: textContainer)
                    return rect.minX + origin.x
                }
            }
            let next = NSMaxRange(paragraph)
            if next <= location { break }
            location = next
        }
        return fallback
    }

    // MARK: - 表格几何

    private static func tableFrames(in textView: NSTextView,
                                    storage: NSTextStorage,
                                    layoutManager: NSLayoutManager,
                                    textContainer: NSTextContainer) -> [Frame] {
        let ns = storage.string as NSString
        let origin = textView.textContainerOrigin
        var groups: [String: Group] = [:]
        // 只读已经排完的行框。主动 glyphIndex / 按矩形要行框会把没排完的格子排一半，边框就会断。
        let laid = layoutManager.glyphRange(
            forBoundingRectWithoutAdditionalLayout: NSRect(x: -20_000, y: -20_000,
                                                           width: 80_000, height: 80_000),
            in: textContainer)
        var glyph = laid.location
        let laidEnd = NSMaxRange(laid)
        while glyph < laidEnd {
            var fragmentGlyphs = NSRange(location: glyph, length: 0)
            let fragment = layoutManager.lineFragmentRect(forGlyphAt: glyph,
                                                          effectiveRange: &fragmentGlyphs,
                                                          withoutAdditionalLayout: true)
            let step = max(NSMaxRange(fragmentGlyphs), glyph + 1)
            if fragment.width > 0.5, fragment.height > 0.5 {
                let chars = layoutManager.characterRange(forGlyphRange: NSRange(location: glyph, length: 1),
                                                        actualGlyphRange: nil)
                if chars.location < ns.length {
                    let paragraph = ns.paragraphRange(for: NSRange(location: chars.location, length: 0))
                    let probe = min(paragraph.location, ns.length - 1)
                    if let key = RichTextTableLayout.tableKey(in: storage, at: probe),
                       let cell = RichTextTableLayout.tableBlock(in: storage, at: probe) {
                        let rect = fragment.offsetBy(dx: origin.x, dy: origin.y)
                        var group = groups[key] ?? Group(
                            probe: paragraph.location,
                            cells: [],
                            weightsRaw: storage.attribute(
                                RichTextTableLayout.weightsKey,
                                at: probe,
                                effectiveRange: nil) as? String,
                            columnCount: cell.table.numberOfColumns)
                        if let last = group.cells.indices.last,
                           group.cells[last].row == cell.startingRow,
                           group.cells[last].column == cell.startingColumn {
                            group.cells[last].rect = group.cells[last].rect.union(rect)
                            let start = group.cells[last].characterRange.location
                            group.cells[last].characterRange = NSRange(location: start,
                                                                       length: NSMaxRange(paragraph) - start)
                        } else {
                            group.cells.append(CellRect(row: cell.startingRow,
                                                        column: cell.startingColumn,
                                                        rect: rect,
                                                        characterRange: paragraph))
                        }
                        groups[key] = group
                    }
                }
            }
            if step <= glyph { break }
            glyph = step
        }

        return groups.values.compactMap { group in
            let cells = completeRows(in: group.cells, columnCount: group.columnCount)
            guard !cells.isEmpty else { return nil }
            var trimmed = group
            trimmed.cells = cells
            return frame(from: trimmed)
        }
        .sorted { $0.bounds.minY < $1.bounds.minY }
    }

    /// 一行里每一列都有行框才算排完。缺列的行画出来横线是断的，先不拿去算灰圆。
    private static func completeRows(in cells: [CellRect], columnCount: Int) -> [CellRect] {
        guard columnCount > 0 else { return [] }
        let expected = Set(0..<columnCount)
        let byRow = Dictionary(grouping: cells, by: \.row)
        let ready = Set(byRow.compactMap { row, items -> Int? in
            Set(items.map(\.column)) == expected ? row : nil
        })
        return cells.filter { ready.contains($0.row) }
    }

    private static func frame(from group: Group) -> Frame? {
        guard !group.cells.isEmpty else { return nil }
        let rowCount = (group.cells.map(\.row).max() ?? 0) + 1
        let columnCount = (group.cells.map(\.column).max() ?? 0) + 1
        guard rowCount > 0, columnCount > 0 else { return nil }

        var columnEdges: [CGFloat] = []
        for column in 0..<columnCount {
            let cells = group.cells.filter { $0.column == column }
            if let minimum = cells.map(\.rect.minX).min() {
                columnEdges.append(minimum - columnLeadingInset)
            } else {
                return nil
            }
        }
        let lastColumnMaxX = group.cells.filter { $0.column == columnCount - 1 }
            .map(\.rect.maxX).max()
        let rightMost = lastColumnMaxX ?? (columnEdges.last ?? 0)
        columnEdges.append(rightMost + columnTrailingInset)

        let presentRows = Set(group.cells.map(\.row))
        let contiguousRows = (0..<rowCount).allSatisfy { presentRows.contains($0) }
        var rowDotIndexes: [Int]?
        var rowEdges: [CGFloat] = []
        if contiguousRows {
            for row in 0..<rowCount {
                let cells = group.cells.filter { $0.row == row }
                if let minimum = cells.map(\.rect.minY).min() {
                    rowEdges.append(minimum - verticalChrome)
                } else {
                    return nil
                }
            }
            let lastRowMaxY = group.cells.filter { $0.row == rowCount - 1 }
                .map(\.rect.maxY).max()
            let bottomMost = lastRowMaxY ?? (rowEdges.last ?? 0)
            rowEdges.append(bottomMost + verticalChrome)
        } else {
            // 屏幕上只排了中间若干行。边界按下标留出空位，灰圆仍用真实行号。
            let maxRow = rowCount - 1
            rowEdges = Array(repeating: 0, count: maxRow + 2)
            var dots: [Int] = []
            var seen = Set<Int>()
            for row in presentRows.sorted() {
                let cells = group.cells.filter { $0.row == row }
                guard let minimum = cells.map(\.rect.minY).min(),
                      let maximum = cells.map(\.rect.maxY).max() else { continue }
                rowEdges[row] = minimum - verticalChrome
                rowEdges[row + 1] = maximum + verticalChrome
                if seen.insert(row).inserted { dots.append(row) }
                if seen.insert(row + 1).inserted { dots.append(row + 1) }
            }
            rowDotIndexes = dots.sorted()
            guard let first = rowDotIndexes?.first, let last = rowDotIndexes?.last, last > first else {
                return nil
            }
        }

        // TextKit 把整行合成一条 fragment 时，各列 minX 会挤在一起，
        // 按列宽权重（或等宽）重新切一遍列边界。
        if columnCount > 1 {
            let firstEdge = columnEdges.first ?? 0
            let lastEdge = columnEdges.last ?? 0
            let distinct = Set(columnEdges.dropLast().map { Int($0.rounded()) })
            if distinct.count < columnCount {
                columnEdges = splitColumns(left: firstEdge,
                                           right: lastEdge,
                                           count: columnCount,
                                           weightsRaw: group.weightsRaw)
            }
        }
        // 行边界必须递增；异常排版时退回按行高均分，避免灰圆叠在一起。
        // 缺行时数组里有占位 0，不能拿来均分。
        if contiguousRows,
           rowEdges.count > 1,
           zip(rowEdges, rowEdges.dropFirst()).contains(where: { $0.1 - $0.0 < 4 }) {
            let top = rowEdges.first ?? 0
            let bottom = rowEdges.last ?? top
            rowEdges = (0...rowCount).map {
                top + (bottom - top) * CGFloat($0) / CGFloat(rowCount)
            }
        }

        let left = columnEdges.first ?? 0
        let right = columnEdges.last ?? 0
        let top = rowDotIndexes.flatMap { indexes in indexes.first.map { rowEdges[$0] } } ?? rowEdges.first ?? 0
        let bottom = rowDotIndexes.flatMap { indexes in indexes.last.map { rowEdges[$0] } } ?? rowEdges.last ?? 0
        let bounds = NSRect(x: left, y: top, width: right - left, height: bottom - top)
        guard bounds.width > 8, bounds.height > 4 else { return nil }
        return Frame(probe: group.probe,
                     bounds: bounds,
                     columnEdges: columnEdges,
                     rowEdges: rowEdges,
                     rowDotIndexes: rowDotIndexes,
                     cells: group.cells)
    }

    private static func splitColumns(left: CGFloat,
                                     right: CGFloat,
                                     count: Int,
                                     weightsRaw: String?) -> [CGFloat] {
        let weights = RichTextTable.weights(from: weightsRaw, columnCount: count)
            ?? Array(repeating: 1.0, count: count)
        let sum = weights.reduce(0, +)
        let width = right - left
        var edges: [CGFloat] = [left]
        var accumulated = 0.0
        for weight in weights.dropLast() {
            accumulated += sum > 0 ? weight / sum : 1 / Double(count)
            edges.append(left + width * CGFloat(accumulated))
        }
        edges.append(right)
        return edges
    }
}
