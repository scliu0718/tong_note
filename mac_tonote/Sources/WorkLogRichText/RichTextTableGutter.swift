import AppKit
import WorkLogCore

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

    var label: String {
        switch kind {
        case .column: return "增加列"
        case .row: return "增加行"
        }
    }

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
            updateLabel()
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

    private let labelField: NSTextField = {
        let field = NSTextField(labelWithString: "")
        field.font = RichTextTableGutterGeometry.labelFont
        field.textColor = NSColor(srgbRed: 0.29, green: 0.31, blue: 0.35, alpha: 1)
        field.isEditable = false
        field.isSelectable = false
        field.isBezeled = false
        field.drawsBackground = false
        field.lineBreakMode = .byClipping
        field.isHidden = true
        return field
    }()

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        addSubview(labelField)
    }

    required init?(coder: NSCoder) {
        super.init(coder: coder)
        addSubview(labelField)
    }

    private func updateLabel() {
        guard let dot = activeDot else {
            labelField.isHidden = true
            return
        }
        labelField.stringValue = dot.label
        labelField.frame = RichTextTableGutterGeometry.labelRect(for: dot)
        labelField.isHidden = false
    }

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

        // 3. hover 命中的灰圆：引导线 → 文字底 → 蓝色圆加号
        guard let dot = activeDot else { return }
        Self.accent.withAlphaComponent(0.45).setFill()
        NSBezierPath(rect: dot.guideRect).fill()

        let labelRect = RichTextTableGutterGeometry.labelRect(for: dot)
        let labelBackground = NSBezierPath(roundedRect: labelRect.insetBy(dx: -3, dy: -1),
                                          xRadius: 5,
                                          yRadius: 5)
        NSColor.white.withAlphaComponent(0.94).setFill()
        labelBackground.fill()
        Self.softBorder.setStroke()
        labelBackground.lineWidth = 0.8
        labelBackground.stroke()

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
        // 文字由 NSTextField 子视图绘制，避免 flipped 上下文里手写字符串被倒置
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

    static let labelFont = NSFont.systemFont(ofSize: 10.5, weight: .medium)

    /// 单元格左右 padding 10pt + 边框 1pt
    private static let horizontalChrome: CGFloat = 11
    /// 单元格上下 padding 6pt + 边框 1pt
    private static let verticalChrome: CGFloat = 7
    /// 灰圆离表格边沿的距离
    private static let dotInset: CGFloat = 7
    private static let dotHitRadius: CGFloat = 14

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
    }

    private struct Frame {
        var probe: Int
        var bounds: NSRect
        var columnEdges: [CGFloat]
        var rowEdges: [CGFloat]
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
            // 左边距只有 4pt，灰圆放外面会被 NSScrollView 裁掉，改贴左边界内侧。
            for (index, edge) in frame.rowEdges.enumerated() {
                dots.append(RichTextTableGutterDot(
                    kind: .row,
                    index: index,
                    probe: frame.probe,
                    point: NSPoint(x: frame.bounds.minX + 5, y: edge),
                    guideRect: NSRect(x: frame.bounds.minX,
                                      y: edge - 0.75,
                                      width: frame.bounds.width,
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
        var distance = dotHitRadius
        for dot in layout.dots {
            let dx = dot.point.x - point.x
            let dy = dot.point.y - point.y
            let delta = (dx * dx + dy * dy).squareRoot()
            if delta <= distance {
                distance = delta
                best = dot
            }
        }
        return best
    }

    static func cell(at characterIndex: Int,
                     in layout: RichTextTableGutterLayout) -> RichTextTableCellFrame? {
        layout.cells.first { cell in
            cell.characterRange.location <= characterIndex
                && characterIndex < NSMaxRange(cell.characterRange)
        }
    }

    static func labelRect(for dot: RichTextTableGutterDot) -> NSRect {
        let measured = (dot.label as NSString).size(withAttributes: [.font: labelFont])
        return NSRect(x: dot.point.x + 14,
                      y: dot.point.y - 9,
                      width: ceil(measured.width) + 6,
                      height: 18)
    }

    // MARK: - 表格几何

    private static func tableFrames(in textView: NSTextView,
                                    storage: NSTextStorage,
                                    layoutManager: NSLayoutManager,
                                    textContainer: NSTextContainer) -> [Frame] {
        let ns = storage.string as NSString
        let origin = textView.textContainerOrigin
        var groups: [String: Group] = [:]

        var location = 0
        while location < ns.length {
            let paragraph = ns.paragraphRange(for: NSRange(location: location, length: 0))
            let probe = min(paragraph.location, ns.length - 1)
            if let key = RichTextTableLayout.tableKey(in: storage, at: probe),
               let cell = RichTextTableLayout.tableBlock(in: storage, at: probe) {
                let glyph = layoutManager.glyphIndexForCharacter(at: probe)
                if glyph < layoutManager.numberOfGlyphs {
                    let fragment = layoutManager.lineFragmentRect(forGlyphAt: glyph, effectiveRange: nil)
                    let rect = fragment.offsetBy(dx: origin.x, dy: origin.y)
                    var group = groups[key] ?? Group(probe: paragraph.location,
                                                     cells: [],
                                                     weightsRaw: storage.attribute(
                                                        RichTextTableLayout.weightsKey,
                                                        at: probe,
                                                        effectiveRange: nil) as? String)
                    if let last = group.cells.indices.last,
                       group.cells[last].row == cell.startingRow,
                       group.cells[last].column == cell.startingColumn {
                        // 同一格里换段：矩形取并集，字符范围向后延
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
            let next = NSMaxRange(paragraph)
            if next <= location { break }
            location = next
        }

        return groups.values.compactMap { frame(from: $0) }
            .sorted { $0.bounds.minY < $1.bounds.minY }
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
                columnEdges.append(minimum - horizontalChrome)
            } else {
                return nil
            }
        }
        let lastColumnMaxX = group.cells.filter { $0.column == columnCount - 1 }
            .map(\.rect.maxX).max()
        let rightMost = lastColumnMaxX ?? (columnEdges.last ?? 0)
        columnEdges.append(rightMost + horizontalChrome)

        var rowEdges: [CGFloat] = []
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
        if rowEdges.count > 1,
           zip(rowEdges, rowEdges.dropFirst()).contains(where: { $0.1 - $0.0 < 4 }) {
            let top = rowEdges.first ?? 0
            let bottom = rowEdges.last ?? top
            rowEdges = (0...rowCount).map {
                top + (bottom - top) * CGFloat($0) / CGFloat(rowCount)
            }
        }

        let left = columnEdges.first ?? 0
        let right = columnEdges.last ?? 0
        let top = rowEdges.first ?? 0
        let bottom = rowEdges.last ?? 0
        let bounds = NSRect(x: left, y: top, width: right - left, height: bottom - top)
        guard bounds.width > 8, bounds.height > 4 else { return nil }
        return Frame(probe: group.probe,
                     bounds: bounds,
                     columnEdges: columnEdges,
                     rowEdges: rowEdges,
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
