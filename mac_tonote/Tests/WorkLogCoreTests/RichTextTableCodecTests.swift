import XCTest
@testable import WorkLogCore

final class RichTextTableCodecTests: XCTestCase {

    func testHTMLRoundTripKeepsGridAndFormatting() {
        var table = RichTextTable.make(rows: 2, columns: 2)
        table.rows[0][0] = RichTextTableCell(blocks: [
            RichTextBlock(style: .paragraph, runs: [RichTextRun(text: "姓名", isBold: true)])
        ])
        table.rows[0][1] = RichTextTableCell(blocks: [
            RichTextBlock(style: .paragraph, runs: [RichTextRun(text: "刘松", colorHex: "#E5484D")])
        ])
        table.rows[1][0] = RichTextTableCell(blocks: [
            RichTextBlock(style: .paragraph, runs: [RichTextRun(text: "当前职责")])
        ])
        table.rows[1][1] = RichTextTableCell(blocks: [
            RichTextBlock(style: .paragraph, runs: [RichTextRun(text: "需求评审")]),
            RichTextBlock(style: .paragraph, runs: [RichTextRun(text: "a < b & c")])
        ])
        let document = RichTextDocument(blocks: [
            RichTextBlock(style: .paragraph, runs: [RichTextRun(text: "前面")]),
            RichTextBlock(style: .table, runs: [], table: table),
            RichTextBlock(style: .paragraph, runs: [RichTextRun(text: "后面")])
        ])

        let html = RichTextHTMLCodec.encode(document)
        XCTAssertTrue(html.contains("<table"))
        XCTAssertTrue(html.contains("data-widths=\"0.28,0.72\""))
        XCTAssertFalse(html.contains("<table><table"))

        let decoded = RichTextHTMLCodec.decode(html)
        XCTAssertEqual(decoded.blocks.count, 3)
        XCTAssertEqual(decoded.blocks[0].plainText, "前面")
        XCTAssertEqual(decoded.blocks[2].plainText, "后面")
        let back = decoded.blocks[1].table
        XCTAssertEqual(back?.rowCount, 2)
        XCTAssertEqual(back?.columnCount, 2)
        XCTAssertEqual(back?.rows[0][0].plainText, "姓名")
        XCTAssertEqual(back?.rows[0][0].blocks.first?.runs.first?.isBold, true)
        XCTAssertEqual(back?.rows[0][1].blocks.first?.runs.first?.colorHex, "#E5484D")
        XCTAssertEqual(back?.rows[1][1].blocks.map(\.plainText), ["需求评审", "a < b & c"])
        let weights = back?.columnWeights ?? []
        XCTAssertEqual(weights.count, 2)
        if weights.count == 2 {
            XCTAssertEqual(weights[0], 0.28, accuracy: 0.0001)
            XCTAssertEqual(weights[1], 0.72, accuracy: 0.0001)
        }

        XCTAssertEqual(RichTextHTMLCodec.encode(decoded), html)
        XCTAssertTrue(decoded.plainText.contains("刘松"))
        XCTAssertTrue(decoded.plainText.contains("a < b & c"))
    }

    func testShortRowIsPaddedToRectangle() {
        let html = "<table><tr><td><p>仅左</p></td></tr><tr><td><p>左</p></td><td><p>右</p></td></tr></table>"
        let table = RichTextHTMLCodec.decode(html).blocks.first?.table
        XCTAssertEqual(table?.columnCount, 2)
        XCTAssertEqual(table?.rows[0][1].plainText, "")
        XCTAssertEqual(table?.rows[1][1].plainText, "右")
    }

    func testInsertAndRemoveStayRectangular() {
        var table = RichTextTable.make(rows: 1, columns: 2)
        XCTAssertTrue(table.insertRow(at: 1))
        XCTAssertEqual(table.rowCount, 2)
        XCTAssertEqual(table.rows[1].count, 2)
        XCTAssertTrue(table.insertColumn(at: 2))
        XCTAssertEqual(table.columnCount, 3)
        XCTAssertNil(table.columnWeights)
        XCTAssertTrue(table.removeColumn(at: 0))
        XCTAssertEqual(table.columnCount, 2)
        XCTAssertFalse(table.removeRow(at: 0) && table.rowCount == 0)
        XCTAssertEqual(table.rowCount, 1)
    }
}
