import AppKit
import WorkLogCore

/// 表格边沿的一次「+」命中。
///
/// - 横向边沿（表格顶边，正好落在列分隔线的 T 形交点）：插入列
/// - 纵向边沿（表格左边，正好落在行分隔线的 T 形交点）：插入行
struct RichTextTableGutterHit: Equatable {
    enum Kind: Equatable {
        case column
        case row
    }

    var kind: Kind
    /// 插入位置：第 index 列 / 第 index 行之前；等于总数时表示在末尾追加。
    var index: Int
    /// 目标表格内任意字符位置，点击时用它定位是哪张表（鼠标不一定在正文选区里）。
    var probe: Int
    /// 「+」按钮中心（NSTextView 坐标）
    var point: NSPoint
    var buttonRect: NSRect
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

/// 只负责画「+」按钮和引导线，不接收鼠标事件。
/// `hitTest` 恒返回 nil，事件仍由底下的 NSTextView 处理，不影响选字与光标。
final class RichTextTableGutterView: NSView {

    var hit: RichTextTableGutterHit? {
        didSet {
            guard hit != oldValue else { return }
            needsDisplay = true
        }
    }

    private static let accent = NSColor(srgbRed: 0.20, green: 0.48, blue: 0.92, alpha: 1)

    override var isOpaque: Bool { false }

    override func hitTest(_ point: NSPoint) -> NSView? { nil }

    override func draw(_ dirtyRect: NSRect) {
        guard let hit else { return }

        // 引导线：贴在插入位置上，明确「要插在哪两列 / 两行之间」
        Self.accent.withAlphaComponent(0.45).setFill()
        NSBezierPath(rect: hit.guideRect).fill()

        // 圆形白底 + 蓝色细边 + 蓝色加号，和表格浅灰边框区分开
        let circle = hit.buttonRect.insetBy(dx: 1, dy: 1)
        NSColor.white.setFill()
        NSBezierPath(ovalIn: circle).fill()
        Self.accent.setStroke()
        let border = NSBezierPath(ovalIn: circle)
        border.lineWidth = 1
        border.stroke()

        let center = hit.point
        let arm: CGFloat = 4
        let plus = NSBezierPath()
        plus.lineWidth = 1.4
        plus.lineCapStyle = .round
        plus.move(to: NSPoint(x: center.x - arm, y: center.y))
        plus.line(to: NSPoint(x: center.x + arm, y: center.y))
        plus.move(to: NSPoint(x: center.x, y: center.y - arm))
        plus.line(to: NSPoint(x: center.x, y: center.y + arm))
        plus.stroke()
    }
}

/// 从 TextKit 1 的排版结果反推表格的列分隔 x / 行分隔 y。
///
/// NSTextTableBlock 会把同一行的格子横排，每个单元格段落各有一条 line fragment。
/// 以它的矩形为准算出边沿；万一某些版本把整行合成一条 fragment，
/// 就退回按 `data-widths`（或等宽）均分，保证加号仍然点得到。
enum RichTextTableGutterGeometry {

    private struct CellRect {
        var row: Int
        var column: Int
        var rect: NSRect
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
    }

    /// 单元格左右 padding 10pt + 边框 1pt
    private static let horizontalChrome: CGFloat = 11
    /// 单元格上下 padding 6pt + 边框 1pt
    private static let verticalChrome: CGFloat = 7
    private static let buttonRadius: CGFloat = 9
    private static let edgeTolerance: CGFloat = 16
    private static let snapTolerance: CGFloat = 18

    static func hit(at point: NSPoint, in textView: NSTextView) -> RichTextTableGutterHit? {
        guard textView.isEditable,
              let textContainer = textView.textContainer,
              let layoutManager = textView.layoutManager,
              let storage = textView.textStorage,
              storage.length > 0 else { return nil }

        let frames = tableFrames(in: textView,
                                 storage: storage,
                                 layoutManager: layoutManager,
                                 textContainer: textContainer)
        for frame in frames {
            // 顶部横向边沿：优先，正好是列分隔线与顶边的 T 形交点
            if abs(point.y - frame.bounds.minY) <= edgeTolerance,
               point.x >= frame.bounds.minX - edgeTolerance,
               point.x <= frame.bounds.maxX + edgeTolerance,
               let edge = nearestEdge(point.x, in: frame.columnEdges, tolerance: snapTolerance) {
                let center = NSPoint(x: edge.value, y: frame.bounds.minY)
                return RichTextTableGutterHit(
                    kind: .column,
                    index: edge.index,
                    probe: frame.probe,
                    point: center,
                    buttonRect: buttonRect(around: center),
                    guideRect: NSRect(x: edge.value - 0.75,
                                      y: frame.bounds.minY - 6,
                                      width: 1.5,
                                      height: frame.bounds.height + 12)
                )
            }

            // 左侧纵向边沿：行分隔线与左边的 T 形交点
            if abs(point.x - frame.bounds.minX) <= edgeTolerance,
               point.y >= frame.bounds.minY - edgeTolerance,
               point.y <= frame.bounds.maxY + edgeTolerance,
               let edge = nearestEdge(point.y, in: frame.rowEdges, tolerance: snapTolerance) {
                // 往表格内侧挪 8pt：左边界紧贴编辑区左边距，按钮完全放外面会被裁掉。
                let center = NSPoint(x: frame.bounds.minX + 8, y: edge.value)
                return RichTextTableGutterHit(
                    kind: .row,
                    index: edge.index,
                    probe: frame.probe,
                    point: center,
                    buttonRect: buttonRect(around: center),
                    guideRect: NSRect(x: frame.bounds.minX - 6,
                                      y: edge.value - 0.75,
                                      width: frame.bounds.width + 12,
                                      height: 1.5)
                )
            }
        }
        return nil
    }

    private static func buttonRect(around center: NSPoint) -> NSRect {
        NSRect(x: center.x - buttonRadius,
               y: center.y - buttonRadius,
               width: buttonRadius * 2,
               height: buttonRadius * 2)
    }

    private static func nearestEdge(_ value: CGFloat,
                                    in edges: [CGFloat],
                                    tolerance: CGFloat) -> (index: Int, value: CGFloat)? {
        var best: (index: Int, value: CGFloat)?
        var distance = tolerance
        for (index, edge) in edges.enumerated() {
            let delta = abs(edge - value)
            if delta <= distance {
                distance = delta
                best = (index, edge)
            }
        }
        return best
    }

    // MARK: - 表格几何

    private static func tableFrames(in textView: NSTextView,
                                    storage: NSTextStorage,
                                    layoutManager: NSLayoutManager,
                                    textContainer: NSTextContainer) -> [Frame] {
        guard layoutManager.numberOfGlyphs > 0 else { return [] }
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
                    group.cells.append(CellRect(row: cell.startingRow,
                                                column: cell.startingColumn,
                                                rect: rect))
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
        // 行边界必须递增；异常排版时退回按行高均分，避免加号叠在一起。
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
                     rowEdges: rowEdges)
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
