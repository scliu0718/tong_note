import XCTest
@testable import WorkLogCore

final class RichTextBlockImageTests: XCTestCase {
    func testBareImgAfterParagraphIsKept() {
        let html = "<p>mac</p><img src=\"attachments/A/B.jpg\">"
        let doc = RichTextHTMLCodec.decode(html)
        let paths = doc.blocks.flatMap(\.runs).compactMap { $0.image?.path }
        XCTAssertEqual(paths, ["attachments/A/B.jpg"])
    }

    func testWrappedImgIsKept() {
        let html = "<p>mac</p><p><img src=\"attachments/A/B.jpg\"></p>"
        let doc = RichTextHTMLCodec.decode(html)
        let paths = doc.blocks.flatMap(\.runs).compactMap { $0.image?.path }
        XCTAssertEqual(paths, ["attachments/A/B.jpg"])
    }
}
