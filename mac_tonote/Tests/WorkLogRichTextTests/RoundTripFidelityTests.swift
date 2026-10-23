import XCTest
import AppKit
@testable import WorkLogCore
@testable import WorkLogRichText

/// 端到端复现：真实笔记的正文 → 载入编辑器 → 回写正文，检查结构是否被破坏。
@MainActor
final class RoundTripFidelityTests: XCTestCase {

    private var tempDir: URL!
    private var store: NoteStore!

    override func setUp() async throws {
        tempDir = FileManager.default.temporaryDirectory
            .appendingPathComponent("WorkLogRoundTrip-\(UUID().uuidString)", isDirectory: true)
        store = NoteStore(directory: tempDir)
    }

    override func tearDown() async throws {
        store = nil
        try? FileManager.default.removeItem(at: tempDir)
    }

    /// 载入 → 重新编码，正文的段落结构必须完全一致
    func testLoadAndReserializePreservesParagraphStructure() throws {
        let note = store.addNote()
        let noteID = note.id
        let source = tempDir.appendingPathComponent("a.png")
        try makePNG(at: source, width: 224, height: 94)
        let source2 = tempDir.appendingPathComponent("b.png")
        try makePNG(at: source2, width: 380, height: 178)

        let rel1 = try store.attachments.importImage(at: source, noteID: noteID)
        let rel2 = try store.attachments.importImage(at: source2, noteID: noteID)

        // 用户真实数据里的形态：段首两张图 + 末尾文字
        let originalHTML = "<p><img src=\"\(rel1)\" width=\"224\" height=\"94\"><img src=\"\(rel2)\" width=\"380\" height=\"178\">asfd</p>"
        store.updateContent(noteID, body: originalHTML, format: .richText)

        // 模拟编辑器载入
        let loaded = store.note(with: noteID)!
        let document = RichTextHTMLCodec.decode(loaded.body)
        let attributed = RichTextBridge.attributedString(from: document, attachments: store.attachments)

        // 模拟编辑器回写
        let back = RichTextBridge.document(from: attributed)
        let rewritten = RichTextHTMLCodec.encode(back)

        XCTAssertEqual(back.blocks.count, 1, "段落数不应变化")
        XCTAssertEqual(rewritten, originalHTML,
                       "回写的 HTML 应与原 HTML 完全一致\n原：\(originalHTML)\n新：\(rewritten)")
    }

    /// 载入到 NSTextView 再读回，结构也不能变
    func testNSTextViewRoundTripPreservesStructure() throws {
        let note = store.addNote()
        let noteID = note.id
        let source = tempDir.appendingPathComponent("c.png")
        try makePNG(at: source, width: 224, height: 94)
        let rel = try store.attachments.importImage(at: source, noteID: noteID)

        let originalHTML = "<p><img src=\"\(rel)\" width=\"224\" height=\"94\">asfd</p>"

        let document = RichTextHTMLCodec.decode(originalHTML)
        let attributed = RichTextBridge.attributedString(from: document, attachments: store.attachments)

        let scroll = NSScrollView()
        let tv = RichTextNSTextView(frame: .zero)
        scroll.documentView = tv
        tv.isRichText = true
        tv.textStorage?.setAttributedString(attributed)

        let back = RichTextBridge.document(from: tv.textStorage!)
        let rewritten = RichTextHTMLCodec.encode(back)

        XCTAssertEqual(back.blocks.count, 1, "段落数不应变化")
        XCTAssertEqual(rewritten, originalHTML,
                       "回写的 HTML 应与原 HTML 完全一致\n原：\(originalHTML)\n新：\(rewritten)")
    }

    private func makePNG(at url: URL, width: CGFloat, height: CGFloat) throws {
        let image = NSImage(size: NSSize(width: width, height: height))
        image.lockFocus()
        NSColor.systemTeal.setFill()
        NSRect(x: 0, y: 0, width: width, height: height).fill()
        image.unlockFocus()
        guard let tiff = image.tiffRepresentation,
              let rep = NSBitmapImageRep(data: tiff),
              let png = rep.representation(using: .png, properties: [:]) else {
            throw NSError(domain: "TestImage", code: 1)
        }
        try png.write(to: url)
    }
}
