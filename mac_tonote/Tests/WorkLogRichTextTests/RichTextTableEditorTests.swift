import XCTest
import AppKit
@testable import WorkLogCore
@testable import WorkLogRichText

@MainActor
final class RichTextTableEditorTests: XCTestCase {

    private func makeView() -> (RichTextNSTextView, RichTextEditorController, AttachmentStore) {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("WorkLogTable-\(UUID().uuidString)", isDirectory: true)
        let store = AttachmentStore(rootDirectory: directory)
        let textView = RichTextNSTextView.makeTextKit1()
        textView.frame = NSRect(x: 0, y: 0, width: 640, height: 480)
        textView.textContainer?.widthTracksTextView = false
        textView.textContainer?.containerSize = NSSize(width: 600, height: CGFloat.greatestFiniteMagnitude)
        textView.isRichText = true
        textView.isEditable = true
        let controller = RichTextEditorController()
        controller.textView = textView
        controller.attachments = store
        return (textView, controller, store)
    }

    private func tableBlock(in document: RichTextDocument) -> RichTextTable? {
        document.blocks.first { $0.style == .table }?.table
    }

    func testTextKit1IsUsedForTables() {
        let textView = RichTextNSTextView.makeTextKit1()
        XCTAssertNil(textView.textLayoutManager)
        XCTAssertNotNil(textView.layoutManager)
    }

    func testRoundTripThroughTextViewKeepsCellText() {
        let (textView, _, store) = makeView()
        var model = RichTextTable.make(rows: 2, columns: 2)
        model.rows[0][0] = RichTextTableCell(blocks: [
            RichTextBlock(style: .paragraph, runs: [RichTextRun(text: "姓名", isBold: true)])
        ])
        model.rows[0][1] = RichTextTableCell(blocks: [
            RichTextBlock(style: .paragraph, runs: [RichTextRun(text: "刘松")]),
            RichTextBlock(style: .paragraph, runs: [RichTextRun(text: "第二段")])
        ])
        let document = RichTextDocument(blocks: [
            RichTextBlock(style: .table, runs: [], table: model)
        ])
        textView.textStorage?.setAttributedString(
            RichTextBridge.attributedString(from: document, attachments: store)
        )

        let back = RichTextBridge.document(from: textView.textStorage!)
        let table = tableBlock(in: back)
        XCTAssertEqual(back.blocks.filter { $0.style == .table }.count, 1)
        XCTAssertEqual(table?.rowCount, 2)
        XCTAssertEqual(table?.columnCount, 2)
        XCTAssertEqual(table?.rows[0][0].plainText, "姓名")
        XCTAssertEqual(table?.rows[0][0].blocks.first?.runs.first?.isBold, true)
        XCTAssertEqual(table?.rows[0][1].blocks.map(\.plainText), ["刘松", "第二段"])
        XCTAssertEqual(table?.rows[1][0].plainText, "")
    }

    func testRowDotsLineUpWithBodyText() throws {
        let (textView, _, store) = makeView()
        textView.textContainerInset = NSSize(width: 24, height: 24)
        let document = RichTextDocument(blocks: [
            RichTextBlock(style: .paragraph, runs: [RichTextRun(text: "一一一一一一")]),
            RichTextBlock(style: .table, runs: [], table: RichTextTable.make(rows: 2, columns: 2))
        ])
        textView.textStorage?.setAttributedString(
            RichTextBridge.attributedString(from: document, attachments: store)
        )
        let layout = try XCTUnwrap(textView.layoutManager)
        let container = try XCTUnwrap(textView.textContainer)
        layout.ensureLayout(for: container)

        let ns = textView.string as NSString
        let character = ns.range(of: "一")
        let glyphRange = layout.glyphRange(forCharacterRange: NSRange(location: character.location, length: 1),
                                           actualCharacterRange: nil)
        let glyphRect = layout.boundingRect(forGlyphRange: glyphRange, in: container)
        let textX = glyphRect.minX + textView.textContainerOrigin.x
        let gutter = RichTextTableGutterGeometry.layout(in: textView)
        let rowDot = try XCTUnwrap(gutter.dots.first { $0.kind == .row })
        let columnDot = try XCTUnwrap(gutter.dots.first { $0.kind == .column })
        XCTAssertEqual(rowDot.point.x, textX, accuracy: 1,
                       "加行圆 \(rowDot.point.x) 应与正文 \(textX) 对齐，表格左缘 \(columnDot.point.x)")
        XCTAssertGreaterThan(columnDot.point.x, textX + 8,
                             "表格左缘 \(columnDot.point.x) 应在正文 \(textX) 右侧")
    }

    func testTableKeepsVerticalGapAroundBodyText() throws {
        let (textView, _, store) = makeView()
        let document = RichTextDocument(blocks: [
            RichTextBlock(style: .paragraph, runs: [RichTextRun(text: "上文")]),
            RichTextBlock(style: .table, runs: [], table: RichTextTable.make(rows: 2, columns: 2)),
            RichTextBlock(style: .paragraph, runs: [RichTextRun(text: "下文")])
        ])
        textView.textStorage?.setAttributedString(
            RichTextBridge.attributedString(from: document, attachments: store)
        )
        let layout = try XCTUnwrap(textView.layoutManager)
        let container = try XCTUnwrap(textView.textContainer)
        layout.ensureLayout(for: container)

        let ns = textView.string as NSString
        let aboveLocation = ns.range(of: "上").location
        let belowLocation = ns.range(of: "下").location
        let aboveGlyph = layout.glyphRange(forCharacterRange: NSRange(location: aboveLocation, length: 1),
                                           actualCharacterRange: nil)
        let belowGlyph = layout.glyphRange(forCharacterRange: NSRange(location: belowLocation, length: 1),
                                           actualCharacterRange: nil)
        let aboveInk = layout.boundingRect(forGlyphRange: aboveGlyph, in: container)
        let belowInk = layout.boundingRect(forGlyphRange: belowGlyph, in: container)
        let gutter = RichTextTableGutterGeometry.layout(in: textView)
        let top = try XCTUnwrap(gutter.cells.map(\.rect.minY).min())
        let bottom = try XCTUnwrap(gutter.cells.map(\.rect.maxY).max())
        let originY = textView.textContainerOrigin.y
        XCTAssertGreaterThanOrEqual(top - (aboveInk.maxY + originY), 20,
                                    "表格上沿应离上文至少 20pt")
        XCTAssertGreaterThanOrEqual((belowInk.minY + originY) - bottom, 10,
                                    "下文应离表格下沿至少 10pt")
    }

    func testAdjacentTablesDoNotOverlap() throws {
        let (textView, _, store) = makeView()
        var upper = RichTextTable.make(rows: 2, columns: 2)
        upper.rows[0][0] = RichTextTableCell(blocks: [
            RichTextBlock(style: .paragraph, runs: [RichTextRun(text: "123")])
        ])
        let document = RichTextDocument(blocks: [
            RichTextBlock(style: .table, runs: [], table: upper),
            RichTextBlock(style: .table, runs: [], table: RichTextTable.make(rows: 1, columns: 2))
        ])
        textView.textStorage?.setAttributedString(
            RichTextBridge.attributedString(from: document, attachments: store)
        )
        let layout = try XCTUnwrap(textView.layoutManager)
        layout.ensureLayout(for: try XCTUnwrap(textView.textContainer))
        let gutter = RichTextTableGutterGeometry.layout(in: textView)
        let tables = Dictionary(grouping: gutter.cells, by: \.tableProbe).values.map { cells in
            (top: cells.map(\.rect.minY).min() ?? 0,
             bottom: cells.map(\.rect.maxY).max() ?? 0)
        }.sorted { $0.top < $1.top }
        XCTAssertEqual(tables.count, 2)
        let gap = tables[1].top - tables[0].bottom
        XCTAssertGreaterThanOrEqual(gap, 20, "两张表之间应留出 20pt，实际 \(gap)")
    }

    func testCellsLayOutOnTheSameRow() throws {
        let (textView, _, store) = makeView()
        var model = RichTextTable.make(rows: 1, columns: 2)
        model.rows[0][0] = RichTextTableCell(blocks: [
            RichTextBlock(style: .paragraph, runs: [RichTextRun(text: "左")])
        ])
        model.rows[0][1] = RichTextTableCell(blocks: [
            RichTextBlock(style: .paragraph, runs: [RichTextRun(text: "右")])
        ])
        let document = RichTextDocument(blocks: [
            RichTextBlock(style: .table, runs: [], table: model)
        ])
        textView.textStorage?.setAttributedString(
            RichTextBridge.attributedString(from: document, attachments: store)
        )
        let layout = try XCTUnwrap(textView.layoutManager)
        let container = try XCTUnwrap(textView.textContainer)
        layout.ensureLayout(for: container)

        let text = textView.string as NSString
        let leftIndex = text.range(of: "左").location
        let rightIndex = text.range(of: "右").location
        XCTAssertNotEqual(leftIndex, NSNotFound)
        XCTAssertNotEqual(rightIndex, NSNotFound)
        let left = layout.boundingRect(
            forGlyphRange: layout.glyphRange(forCharacterRange: NSRange(location: leftIndex, length: 1),
                                             actualCharacterRange: nil),
            in: container
        )
        let right = layout.boundingRect(
            forGlyphRange: layout.glyphRange(forCharacterRange: NSRange(location: rightIndex, length: 1),
                                             actualCharacterRange: nil),
            in: container
        )
        XCTAssertGreaterThan(right.minX, left.maxX - 1, "两列应该左右并排，而不是上下堆叠")
        XCTAssertLessThan(abs(right.midY - left.midY), 12)
    }

    func testTypingStaysInsideTheCell() {
        let (textView, controller, _) = makeView()
        controller.insertTable(rows: 2, columns: 2)
        textView.insertText("姓名", replacementRange: textView.selectedRange())
        if let storage = textView.textStorage {
            RichTextTableLayout.repairIdentity(in: storage)
        }

        let document = RichTextBridge.document(from: textView.textStorage!)
        let table = tableBlock(in: document)
        XCTAssertEqual(table?.rowCount, 2)
        XCTAssertEqual(table?.columnCount, 2)
        XCTAssertEqual(table?.rows[0][0].plainText, "姓名")
        XCTAssertEqual(table?.rows[0][1].plainText, "")
        XCTAssertEqual(document.blocks.filter { $0.style != .table && !$0.plainText.isEmpty }.count, 0)
    }

    func testTabAddsRowFromTheLastCell() {
        let (textView, controller, _) = makeView()
        controller.insertTable(rows: 1, columns: 2)
        XCTAssertTrue(controller.handleTabInTable(shift: false))
        XCTAssertEqual(controller.tableCaret?.column, 1)
        XCTAssertTrue(controller.handleTabInTable(shift: false))
        XCTAssertEqual(controller.tableCaret?.row, 1)
        XCTAssertEqual(controller.tableCaret?.column, 0)
        let table = tableBlock(in: RichTextBridge.document(from: textView.textStorage!))
        XCTAssertEqual(table?.rowCount, 2)
        XCTAssertEqual(table?.columnCount, 2)
    }

    func testDeletingTheBoundaryBetweenCellsIsRejected() {
        let (textView, controller, _) = makeView()
        controller.insertTable(rows: 1, columns: 2)
        textView.insertText("左", replacementRange: textView.selectedRange())
        let storage = textView.textStorage!
        let newline = (storage.string as NSString).range(of: "\n")
        XCTAssertNotEqual(newline.location, NSNotFound)
        XCTAssertFalse(RichTextTableLayout.shouldAllowChange(in: textView, range: newline, replacement: ""))
        let whole = NSRange(location: 0, length: storage.length)
        XCTAssertTrue(RichTextTableLayout.shouldAllowChange(in: textView, range: whole, replacement: ""))
    }

    func testAddColumnAndDeleteTable() {
        let (textView, controller, _) = makeView()
        controller.insertTable(rows: 2, columns: 2)
        controller.insertTableColumnAfter()
        var table = tableBlock(in: RichTextBridge.document(from: textView.textStorage!))
        XCTAssertEqual(table?.columnCount, 3)
        XCTAssertEqual(controller.tableCaret?.column, 1)

        controller.deleteTable()
        table = tableBlock(in: RichTextBridge.document(from: textView.textStorage!))
        XCTAssertNil(table)
        XCTAssertNil(controller.tableCaret)
    }

    func testBackgroundAppliesToEverySelectedCell() throws {
        let (textView, _, store) = makeView()
        var model = RichTextTable.make(rows: 2, columns: 2)
        model.rows[0][0] = RichTextTableCell(blocks: [
            RichTextBlock(style: .paragraph, runs: [RichTextRun(text: "甲")])
        ])
        model.rows[0][1] = RichTextTableCell(blocks: [
            RichTextBlock(style: .paragraph, runs: [RichTextRun(text: "乙")])
        ])
        model.rows[1][0] = RichTextTableCell(blocks: [
            RichTextBlock(style: .paragraph, runs: [RichTextRun(text: "丙")])
        ])
        model.rows[1][1] = RichTextTableCell(blocks: [
            RichTextBlock(style: .paragraph, runs: [RichTextRun(text: "丁")])
        ])
        textView.textStorage?.setAttributedString(
            RichTextBridge.attributedString(from: RichTextDocument(blocks: [
                RichTextBlock(style: .table, runs: [], table: model)
            ]), attachments: store)
        )
        let storage = try XCTUnwrap(textView.textStorage)
        let text = storage.string as NSString
        let start = text.range(of: "甲").location
        let end = text.range(of: "丙").location
        let selection = NSRange(location: start, length: end - start + 1)
        guard let read = RichTextTableLayout.read(from: storage, at: start) else {
            XCTFail("没有读到表格")
            return
        }
        let targets = RichTextTableLayout.cells(in: storage, selection: selection, tableRange: read.range)
        XCTAssertEqual(targets.count, 3)
        RichTextTableEditor.setCellBackground("#D9E6F8", cells: targets, probe: start, in: textView)
        let table = tableBlock(in: RichTextBridge.document(from: storage))
        XCTAssertEqual(table?.rows[0][0].backgroundHex, "#D9E6F8")
        XCTAssertEqual(table?.rows[0][1].backgroundHex, "#D9E6F8")
        XCTAssertEqual(table?.rows[1][0].backgroundHex, "#D9E6F8")
        XCTAssertNil(table?.rows[1][1].backgroundHex)
    }

    /// 不主动排整张表。补排应在下一拍把第一行的每一列都排完，横线才有完整行框。
    func testRowFillLaysOutEntireFirstRow() throws {
        let (textView, _, store) = makeView()
        let document = RichTextDocument(blocks: [
            RichTextBlock(style: .table, runs: [], table: RichTextTable.make(rows: 4, columns: 6))
        ])
        textView.textStorage?.setAttributedString(
            RichTextBridge.attributedString(from: document, attachments: store)
        )
        let deadline = Date().addingTimeInterval(1)
        while Date() < deadline {
            RunLoop.main.run(until: Date().addingTimeInterval(0.05))
        }

        let layout = try XCTUnwrap(textView.layoutManager)
        let container = try XCTUnwrap(textView.textContainer)
        let storage = try XCTUnwrap(textView.textStorage)
        let ns = storage.string as NSString
        let laid = layout.glyphRange(
            forBoundingRectWithoutAdditionalLayout: NSRect(x: -100, y: -100, width: 8_000, height: 8_000),
            in: container)
        var columns = Set<Int>()
        var glyph = laid.location
        let end = NSMaxRange(laid)
        while glyph < end {
            var fragmentGlyphs = NSRange(location: glyph, length: 0)
            let fragment = layout.lineFragmentRect(forGlyphAt: glyph,
                                                  effectiveRange: &fragmentGlyphs,
                                                  withoutAdditionalLayout: true)
            let step = max(NSMaxRange(fragmentGlyphs), glyph + 1)
            if fragment.height > 0.5 {
                let chars = layout.characterRange(forGlyphRange: NSRange(location: glyph, length: 1),
                                                  actualGlyphRange: nil)
                if chars.location < ns.length,
                   let cell = RichTextTableLayout.tableBlock(in: storage, at: chars.location),
                   cell.startingRow == 0 {
                    columns.insert(cell.startingColumn)
                }
            }
            if step <= glyph { break }
            glyph = step
        }
        XCTAssertEqual(columns, Set(0..<6), "第一行的 6 列都应已排完，实际 \(columns.sorted())")
    }
}
