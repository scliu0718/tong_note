import XCTest
import AppKit
@testable import WorkLogCore
@testable import WorkLogRichText

/// 富文本核心链路回归：受控 HTML 编解码、附件、格式持久化、跨笔记隔离。
@MainActor
final class RichTextTests: XCTestCase {

    private var tempDir: URL!
    private var store: NoteStore!

    override func setUp() async throws {
        tempDir = FileManager.default.temporaryDirectory
            .appendingPathComponent("WorkLogRichTests-\(UUID().uuidString)", isDirectory: true)
        store = NoteStore(directory: tempDir)
    }

    override func tearDown() async throws {
        store = nil
        try? FileManager.default.removeItem(at: tempDir)
    }

    // MARK: - 受控 HTML 编解码

    func testHTMLRoundTripPreservesFormatting() {
        var document = RichTextDocument()
        document.blocks = [
            RichTextBlock(style: .heading1, runs: [RichTextRun(text: "标题")]),
            RichTextBlock(style: .paragraph, runs: [
                RichTextRun(text: "粗体", isBold: true),
                RichTextRun(text: "与"),
                RichTextRun(text: "斜体", isItalic: true)
            ]),
            RichTextBlock(style: .quote, runs: [RichTextRun(text: "引用")]),
            RichTextBlock(style: .bulletItem, runs: [RichTextRun(text: "要点一")]),
            RichTextBlock(style: .orderedItem, runs: [RichTextRun(text: "序号项")]),
            RichTextBlock(style: .paragraph, runs: [
                RichTextRun(text: "红色", fontSize: 20, colorHex: "#E5484D"),
                RichTextRun(text: "链接", link: "https://example.com")
            ])
        ]

        let html = RichTextHTMLCodec.encode(document)
        let decoded = RichTextHTMLCodec.decode(html)

        XCTAssertEqual(decoded.blocks.count, 6, "块数量应保持一致")
        XCTAssertEqual(decoded.blocks[0].style, RichTextBlockStyle.heading1)
        XCTAssertEqual(decoded.blocks[0].plainText, "标题")
        XCTAssertTrue(decoded.blocks[1].runs.contains { $0.isBold })
        XCTAssertTrue(decoded.blocks[1].runs.contains { $0.isItalic })
        XCTAssertEqual(decoded.blocks[2].style, RichTextBlockStyle.quote)
        XCTAssertEqual(decoded.blocks[3].style, RichTextBlockStyle.bulletItem)
        XCTAssertEqual(decoded.blocks[4].style, RichTextBlockStyle.orderedItem)
        XCTAssertEqual(decoded.blocks[5].runs.first?.colorHex, "#E5484D")
        XCTAssertEqual(decoded.blocks[5].runs.first?.fontSize, 20)
        XCTAssertEqual(decoded.blocks[5].runs.last?.link, "https://example.com")
    }

    /// 正文里不允许出现 Cocoa HTML Writer 生成的临时 file:// 引用
    func testHTMLNeverContainsFileURLReferences() {
        let document = RichTextDocument(blocks: [
            RichTextBlock(style: .paragraph, runs: [
                RichTextRun(image: RichTextImage(path: "attachments/abc/1.png", width: 100, height: 80))
            ])
        ])
        let html = RichTextHTMLCodec.encode(document)
        XCTAssertFalse(html.contains("file://"), "正文不应包含 file:// 引用")
        XCTAssertTrue(html.contains("attachments/abc/1.png"))
        XCTAssertFalse(html.contains("Attachment.tiff"))
    }

    /// 特殊字符必须转义，否则会破坏 HTML 结构
    func testHTMLEscapesSpecialCharacters() {
        let document = RichTextDocument(blocks: [
            RichTextBlock(style: .paragraph, runs: [RichTextRun(text: "a < b & c > d")])
        ])
        let html = RichTextHTMLCodec.encode(document)
        XCTAssertTrue(html.contains("&lt;"))
        XCTAssertTrue(html.contains("&amp;"))
        XCTAssertTrue(html.contains("&gt;"))

        let decoded = RichTextHTMLCodec.decode(html)
        XCTAssertEqual(decoded.blocks.first?.plainText, "a < b & c > d")
    }

    /// 无序/有序列表应合并为 <ul> / <ol>
    func testListBlocksMergeIntoListElements() {
        let document = RichTextDocument(blocks: [
            RichTextBlock(style: .bulletItem, runs: [RichTextRun(text: "A")]),
            RichTextBlock(style: .bulletItem, runs: [RichTextRun(text: "B")]),
            RichTextBlock(style: .orderedItem, runs: [RichTextRun(text: "1")]),
            RichTextBlock(style: .orderedItem, runs: [RichTextRun(text: "2")])
        ])
        let html = RichTextHTMLCodec.encode(document)
        XCTAssertTrue(html.contains("<ul><li>A</li><li>B</li></ul>"))
        XCTAssertTrue(html.contains("<ol><li>1</li><li>2</li></ol>"))
    }

    /// img 标签的 src 不能被 width/height 属性污染
    func testImageTagAttributesAreParsedCorrectly() {
        let html = "<p>前<img src=\"attachments/note-1/img-1.png\" width=\"160\" height=\"90\">后</p>"
        let document = RichTextHTMLCodec.decode(html)
        let imageRun = document.blocks.flatMap { $0.runs }.first { $0.image != nil }
        XCTAssertNotNil(imageRun)
        XCTAssertEqual(imageRun?.image?.path, "attachments/note-1/img-1.png")
        XCTAssertEqual(imageRun?.image?.width, 160)
        XCTAssertEqual(imageRun?.image?.height, 90)
    }

    /// 多个属性连续出现时不能串味
    func testMultipleAttributesDoNotBleed() {
        let html = "<p><span style=\"font-size:20px\" style=\"color:#FF0000\">文本</span></p>"
        let document = RichTextHTMLCodec.decode(html)
        let run = document.blocks.first?.runs.first
        XCTAssertEqual(run?.fontSize, 20)
        XCTAssertEqual(run?.colorHex, "#FF0000")
    }

    /// 段落内的图片与文字必须保持在同一段落（图文混排不能被拆开）
    func testImageAndTextStayInSameParagraph() {
        let html = "<p><img src=\"attachments/x/y.png\" width=\"224\" height=\"94\">asfd</p>"
        let document = RichTextHTMLCodec.decode(html)

        XCTAssertEqual(document.blocks.count, 1, "图片与文字应同属一个段落")
        XCTAssertEqual(document.blocks[0].plainText, "asfd")
        XCTAssertEqual(document.blocks[0].runs.count, 2, "应包含图片 run 与文字 run")

        // 重新编码后仍应在同一段落内
        let reencoded = RichTextHTMLCodec.encode(document)
        XCTAssertTrue(reencoded.contains("<img src=\"attachments/x/y.png\""), "实际：\(reencoded)")
        XCTAssertTrue(reencoded.contains("asfd"))
        XCTAssertFalse(reencoded.contains("</p>\n<p>"), "不应把同一段落拆开，实际：\(reencoded)")
    }

    // MARK: - 旧 Markdown 兼容

    /// 旧 Markdown 笔记必须能正常解码，且不丢失正文
    func testLegacyMarkdownStillDecodes() {
        let markdown = """
        # 标题

        这是**加粗**和*斜体*。

        - 要点一
        - 要点二

        1. 第一
        2. 第二
        """
        let document = MarkdownConverter.toDocument(markdown)
        XCTAssertEqual(document.blocks.first?.style, RichTextBlockStyle.heading1)
        XCTAssertTrue(document.blocks.contains { $0.runs.contains { $0.isBold } })
        XCTAssertTrue(document.blocks.contains { $0.style == .bulletItem })
        XCTAssertTrue(document.blocks.contains { $0.style == .orderedItem })
        XCTAssertTrue(document.plainText.contains("标题"))
    }

    /// 旧 Markdown 里的图片占位符不是真实附件，必须保留为文本而不是生成附件
    func testLegacyImagePlaceholderStaysText() {
        let document = MarkdownConverter.toDocument("1234134![](图片地址)")
        XCTAssertEqual(document.blocks.count, 1)
        XCTAssertNil(document.blocks[0].runs.first?.image)
        XCTAssertTrue(document.plainText.contains("图片地址"))
    }

    // MARK: - 附件仓库

    func testImageImportCreatesRelativePath() throws {
        let noteID = UUID()
        let source = tempDir.appendingPathComponent("source.png")
        try makeTestImage(at: source)

        let relative = try store.attachments.importImage(at: source, noteID: noteID)

        XCTAssertTrue(relative.hasPrefix("attachments/\(noteID.uuidString)/"), "应为跨平台相对路径，实际：\(relative)")
        XCTAssertTrue(relative.hasSuffix(".png"))
        XCTAssertTrue(store.attachments.fileExists(relativePath: relative))

        // 绝对路径能正确还原
        let absolute = store.attachments.absoluteURL(for: relative)
        XCTAssertTrue(FileManager.default.fileExists(atPath: absolute.path))
    }

    func testUnsupportedImageTypeRejected() {
        let source = tempDir.appendingPathComponent("note.txt")
        try? "hello".write(to: source, atomically: true, encoding: .utf8)
        XCTAssertThrowsError(try store.attachments.importImage(at: source, noteID: UUID()))
    }

    /// 彻底删除笔记时应一并清理附件
    func testPermanentlyDeleteRemovesAttachments() throws {
        let note = store.addNote()
        let source = tempDir.appendingPathComponent("pic.png")
        try makeTestImage(at: source)
        let relative = try store.attachments.importImage(at: source, noteID: note.id)

        XCTAssertTrue(store.attachments.fileExists(relativePath: relative))
        store.permanentlyDelete(note.id)
        XCTAssertFalse(store.attachments.fileExists(relativePath: relative))
    }

    // MARK: - 持久化与格式升级

    /// 富文本正文与格式必须一起写入并能正确重载
    func testRichTextContentPersistsWithFormat() {
        let note = store.addNote()
        let html = "<p>富文本正文</p>"
        store.updateContent(note.id, body: html, format: .richText)

        let reloaded = NoteStore(directory: tempDir)
        let restored = reloaded.note(with: note.id)
        XCTAssertEqual(restored?.body, html)
        XCTAssertEqual(restored?.contentFormat, .richText)
    }

    /// 旧 notes.json（没有 contentFormat 字段）必须能正常解码
    func testLegacyJSONWithoutContentFormatDecodes() throws {
        let legacy = """
        [
          {
            "id": "11111111-1111-1111-1111-111111111111",
            "title": "旧笔记",
            "body": "**旧正文**",
            "createdAt": "2026-10-01T08:00:00Z",
            "updatedAt": "2026-10-02T08:00:00Z",
            "tags": ["历史"],
            "notebook": "工作日志",
            "pinned": false
          }
        ]
        """
        let url = tempDir.appendingPathComponent("notes.json")
        try legacy.write(to: url, atomically: true, encoding: .utf8)

        let reloaded = NoteStore(directory: tempDir)
        XCTAssertEqual(reloaded.notes.count, 1)
        XCTAssertEqual(reloaded.notes.first?.title, "旧笔记")
        XCTAssertEqual(reloaded.notes.first?.body, "**旧正文**")
        XCTAssertEqual(reloaded.notes.first?.contentFormat, .markdown, "缺省格式应为 markdown")
    }

    /// 摘要与搜索不应命中 HTML 标签或附件路径
    func testSnippetIgnoresMarkupAndAttachmentPaths() {
        let note = Note(title: "带图笔记",
                        body: "<p>正文<strong>加粗</strong></p><img src=\"attachments/x/y.png\">",
                        contentFormat: .richText)
        XCTAssertFalse(note.snippet.contains("<p>"))
        XCTAssertFalse(note.snippet.contains("attachments/"))
        XCTAssertTrue(note.snippet.contains("正文"))
    }

    /// 跨笔记隔离：编辑第二篇笔记不能写进第一篇
    func testRichTextContentStaysIsolatedAcrossNotes() {
        let first = store.addNote()
        let second = store.addNote()
        let third = store.addNote()

        store.updateContent(first.id, body: "<p>第一篇</p>", format: .richText)
        store.updateContent(second.id, body: "<p>第二篇</p>", format: .richText)
        store.updateContent(third.id, body: "<p>第三篇</p>", format: .richText)

        store.updateContent(second.id, body: "<p>第二篇-再次修改</p>", format: .richText)

        XCTAssertEqual(store.note(with: first.id)?.body, "<p>第一篇</p>")
        XCTAssertEqual(store.note(with: second.id)?.body, "<p>第二篇-再次修改</p>")
        XCTAssertEqual(store.note(with: third.id)?.body, "<p>第三篇</p>")

        let reloaded = NoteStore(directory: tempDir)
        XCTAssertEqual(reloaded.note(with: first.id)?.body, "<p>第一篇</p>")
        XCTAssertEqual(reloaded.note(with: second.id)?.body, "<p>第二篇-再次修改</p>")
        XCTAssertEqual(reloaded.note(with: third.id)?.body, "<p>第三篇</p>")
    }

    // MARK: - AppKit 桥接

    /// NSAttributedString → 文档 → NSAttributedString 的往返
    func testAttributedStringRoundTripKeepsFormatting() {
        let document = RichTextDocument(blocks: [
            RichTextBlock(style: .heading2, runs: [RichTextRun(text: "标题二")]),
            RichTextBlock(style: .paragraph, runs: [
                RichTextRun(text: "普通", isBold: true),
                RichTextRun(text: "彩色", fontSize: 20, colorHex: "#E5484D")
            ])
        ])

        let attributed = RichTextBridge.attributedString(from: document, attachments: store.attachments)
        let back = RichTextBridge.document(from: attributed)

        XCTAssertEqual(back.blocks.count, 2)
        XCTAssertEqual(back.blocks[0].style, RichTextBlockStyle.heading2)
        XCTAssertEqual(back.blocks[0].plainText, "标题二")
        XCTAssertTrue(back.blocks[1].runs.contains { $0.isBold })
        XCTAssertTrue(back.blocks[1].runs.contains { $0.colorHex == "#E5484D" })
        XCTAssertTrue(back.blocks[1].runs.contains { $0.fontSize == 20 })
    }

    /// 附件图片必须能还原成带相对路径的图片 run
    func testAttributedStringKeepsImageRelativePath() throws {
        let noteID = UUID()
        let source = tempDir.appendingPathComponent("in.png")
        try makeTestImage(at: source)
        let relative = try store.attachments.importImage(at: source, noteID: noteID)

        let document = RichTextDocument(blocks: [
            RichTextBlock(style: .paragraph, runs: [
                RichTextRun(text: "图片前"),
                RichTextRun(image: RichTextImage(path: relative, width: 120, height: 80))
            ])
        ])

        let attributed = RichTextBridge.attributedString(from: document, attachments: store.attachments)
        let back = RichTextBridge.document(from: attributed)

        let imageRun = back.blocks.first?.runs.first { $0.image != nil }
        XCTAssertNotNil(imageRun, "应还原出图片 run")
        XCTAssertEqual(imageRun?.image?.path, relative)
        XCTAssertFalse((imageRun?.image?.path ?? "").hasPrefix("file://"))
    }

    // MARK: - 工具

    private func makeTestImage(at url: URL) throws {
        let image = NSImage(size: NSSize(width: 40, height: 30))
        image.lockFocus()
        NSColor.red.setFill()
        NSRect(x: 0, y: 0, width: 40, height: 30).fill()
        image.unlockFocus()
        guard let tiff = image.tiffRepresentation,
              let rep = NSBitmapImageRep(data: tiff),
              let png = rep.representation(using: .png, properties: [:]) else {
            throw NSError(domain: "TestImage", code: 1)
        }
        try png.write(to: url)
    }
}
