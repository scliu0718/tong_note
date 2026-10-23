import XCTest
import AppKit
@testable import WorkLogCore
@testable import WorkLogRichText

/// AppKit 层真实交互验证：直接驱动 NSTextView + 编辑控制器，
/// 覆盖输入、加粗、字号、颜色、段落、列表、图片与跨笔记隔离。
@MainActor
final class RichTextEditorInteractionTests: XCTestCase {

    private var tempDir: URL!
    private var store: NoteStore!

    override func setUp() async throws {
        tempDir = FileManager.default.temporaryDirectory
            .appendingPathComponent("WorkLogEditorTests-\(UUID().uuidString)", isDirectory: true)
        store = NoteStore(directory: tempDir)
    }

    override func tearDown() async throws {
        store = nil
        try? FileManager.default.removeItem(at: tempDir)
    }

    // MARK: - 测试脚手架

    /// 造一个真实的 NSTextView，并挂上控制器
    private func makeEditor(noteID: UUID) -> (NSTextView, RichTextEditorController) {
        let scrollView = NSTextView.scrollableTextView()
        let textView = scrollView.documentView as! NSTextView
        textView.isRichText = true
        textView.allowsUndo = true
        textView.frame = NSRect(x: 0, y: 0, width: 600, height: 400)
        // 与生产环境（RichTextEditor.load）保持一致
        textView.typingAttributes = [
            .font: NSFont.systemFont(ofSize: RichTextBridge.defaultFontSize),
            .foregroundColor: RichTextBridge.defaultTextColor
        ]

        let controller = RichTextEditorController()
        controller.textView = textView
        controller.attachments = store.attachments
        controller.noteID = noteID
        return (textView, controller)
    }

    /// 模拟用户键入文本
    private func type(_ text: String, into textView: NSTextView) {
        textView.insertText(text, replacementRange: textView.selectedRange())
    }

    /// 选中全文
    private func selectAll(_ textView: NSTextView) {
        textView.setSelectedRange(NSRange(location: 0, length: textView.string.count))
    }

    // MARK: - 基础输入

    func testTypingProducesHTMLParagraph() {
        let (textView, controller) = makeEditor(noteID: UUID())
        type("你好世界", into: textView)

        let document = RichTextBridge.document(from: textView.textStorage!)
        XCTAssertEqual(document.blocks.count, 1)
        XCTAssertEqual(document.plainText, "你好世界")

        let html = RichTextHTMLCodec.encode(document)
        XCTAssertTrue(html.contains("<p>你好世界</p>"))
    }

    func testNewLineCreatesSeparateParagraphs() {
        let (textView, _) = makeEditor(noteID: UUID())
        type("第一段", into: textView)
        type("\n第二段", into: textView)

        let document = RichTextBridge.document(from: textView.textStorage!)
        XCTAssertEqual(document.blocks.count, 2)
        XCTAssertEqual(document.blocks[0].plainText, "第一段")
        XCTAssertEqual(document.blocks[1].plainText, "第二段")
    }

    // MARK: - 格式

    func testBoldAppliedToSelection() {
        let (textView, controller) = makeEditor(noteID: UUID())
        type("重要结论", into: textView)
        selectAll(textView)
        controller.toggleBold()

        let document = RichTextBridge.document(from: textView.textStorage!)
        XCTAssertTrue(document.blocks[0].runs.contains { $0.isBold }, "选区应被加粗")
        XCTAssertEqual(document.plainText, "重要结论", "加粗不应改变文本内容")
    }

    func testBoldToggleTwiceReturnsToNormal() {
        let (textView, controller) = makeEditor(noteID: UUID())
        type("测试", into: textView)
        selectAll(textView)
        controller.toggleBold()
        controller.toggleBold()

        let document = RichTextBridge.document(from: textView.textStorage!)
        XCTAssertFalse(document.blocks[0].runs.contains { $0.isBold })
    }

    func testFontSizeAppliedToSelection() {
        let (textView, controller) = makeEditor(noteID: UUID())
        type("大标题文字", into: textView)
        selectAll(textView)
        controller.setFontSize(24)

        let document = RichTextBridge.document(from: textView.textStorage!)
        XCTAssertTrue(document.blocks[0].runs.contains { $0.fontSize == 24 })
    }

    func testTextColorAppliedToSelection() {
        let (textView, controller) = makeEditor(noteID: UUID())
        type("红色文字", into: textView)
        selectAll(textView)
        controller.setTextColor(NSColor(srgbRed: 0xE5 / 255, green: 0x48 / 255, blue: 0x4D / 255, alpha: 1))

        let document = RichTextBridge.document(from: textView.textStorage!)
        XCTAssertTrue(document.blocks[0].runs.contains { $0.colorHex == "#E5484D" })
    }

    func testUnderlineAndStrikeApplied() {
        let (textView, controller) = makeEditor(noteID: UUID())
        type("格式测试", into: textView)
        selectAll(textView)
        controller.toggleUnderline()
        controller.toggleStrike()

        let document = RichTextBridge.document(from: textView.textStorage!)
        XCTAssertTrue(document.blocks[0].runs.contains { $0.isUnderline })
        XCTAssertTrue(document.blocks[0].runs.contains { $0.isStrike })
    }

    // MARK: - 段落与列表

    func testHeadingStyleApplied() {
        let (textView, controller) = makeEditor(noteID: UUID())
        type("章节标题", into: textView)
        selectAll(textView)
        controller.applyBlockStyle(.heading1)

        let document = RichTextBridge.document(from: textView.textStorage!)
        XCTAssertEqual(document.blocks[0].style, RichTextBlockStyle.heading1)

        let html = RichTextHTMLCodec.encode(document)
        XCTAssertTrue(html.contains("<h1>章节标题</h1>"))
    }

    func testQuoteAndCodeStylesApplied() {
        let (textView, controller) = makeEditor(noteID: UUID())
        type("引用内容", into: textView)
        selectAll(textView)
        controller.applyBlockStyle(.quote)
        var document = RichTextBridge.document(from: textView.textStorage!)
        XCTAssertEqual(document.blocks[0].style, RichTextBlockStyle.quote)

        controller.applyBlockStyle(.code)
        document = RichTextBridge.document(from: textView.textStorage!)
        XCTAssertEqual(document.blocks[0].style, RichTextBlockStyle.code)
    }

    /// 有序列表：多个段落都应变成序号项，并合并成一个 <ol>
    func testOrderedListCreatesSequentialNumberedItems() {
        let (textView, controller) = makeEditor(noteID: UUID())
        type("第一项\n第二项\n第三项", into: textView)
        selectAll(textView)
        controller.toggleList(.orderedItem)

        let document = RichTextBridge.document(from: textView.textStorage!)
        XCTAssertEqual(document.blocks.count, 3)
        XCTAssertTrue(document.blocks.allSatisfy { $0.style == RichTextBlockStyle.orderedItem })

        let html = RichTextHTMLCodec.encode(document)
        XCTAssertTrue(html.contains("<ol>"))
        XCTAssertTrue(html.contains("<li>第一项</li>"))
        XCTAssertTrue(html.contains("<li>第三项</li>"))
        XCTAssertEqual(html.components(separatedBy: "<ol>").count - 1, 1, "应合并成单个有序列表")
    }

    /// 无序列表
    func testBulletListCreatesItems() {
        let (textView, controller) = makeEditor(noteID: UUID())
        type("要点一\n要点二", into: textView)
        selectAll(textView)
        controller.toggleList(.bulletItem)

        let document = RichTextBridge.document(from: textView.textStorage!)
        XCTAssertTrue(document.blocks.allSatisfy { $0.style == RichTextBlockStyle.bulletItem })
        XCTAssertTrue(RichTextHTMLCodec.encode(document).contains("<ul>"))
    }

    /// 再点一次序号按钮应回到正文
    func testTogglingListTwiceReturnsToParagraph() {
        let (textView, controller) = makeEditor(noteID: UUID())
        type("条目", into: textView)
        selectAll(textView)
        controller.toggleList(.orderedItem)
        controller.toggleList(.orderedItem)

        let document = RichTextBridge.document(from: textView.textStorage!)
        XCTAssertEqual(document.blocks[0].style, RichTextBlockStyle.paragraph)
    }

    /// 有序列表段落必须带 NSTextList，才能真正渲染出序号
    func testOrderedListParagraphCarriesTextList() {
        let (textView, controller) = makeEditor(noteID: UUID())
        type("编号内容", into: textView)
        selectAll(textView)
        controller.toggleList(.orderedItem)

        let paragraph = textView.textStorage!.attribute(.paragraphStyle, at: 0, effectiveRange: nil) as? NSParagraphStyle
        XCTAssertNotNil(paragraph?.textLists.first, "有序列表段落应带 NSTextList")
        XCTAssertEqual(paragraph?.textLists.first?.markerFormat, .decimal)
    }

    // MARK: - 图片

    func testInsertImageCreatesAttachmentAndInlineRun() throws {
        let noteID = UUID()
        let (textView, controller) = makeEditor(noteID: noteID)
        let source = tempDir.appendingPathComponent("shot.png")
        try makeTestImage(at: source)

        type("图片前", into: textView)
        try controller.insertImage(from: source)
        type("图片后", into: textView)

        // 附件已落盘
        let attachmentDir = store.attachments.directory(for: noteID)
        let files = try FileManager.default.contentsOfDirectory(atPath: attachmentDir.path)
        XCTAssertEqual(files.count, 1)
        XCTAssertTrue(files[0].hasSuffix(".png"))

        // 正文里是图片 run，且带相对路径
        let document = RichTextBridge.document(from: textView.textStorage!)
        let imageRun = document.blocks.flatMap { $0.runs }.first { $0.image != nil }
        XCTAssertNotNil(imageRun, "正文应包含图片 run")
        XCTAssertTrue(imageRun?.image?.path.hasPrefix("attachments/\(noteID.uuidString)/") == true)
        XCTAssertFalse((imageRun?.image?.path ?? "").contains("file://"))

        // 文本与图片混排：前后文字都在
        XCTAssertTrue(document.plainText.contains("图片前"))
        XCTAssertTrue(document.plainText.contains("图片后"))

        // HTML 只保存相对路径
        let html = RichTextHTMLCodec.encode(document)
        XCTAssertTrue(html.contains("<img src=\"attachments/"))
        XCTAssertFalse(html.contains("file://"))
    }

    /// 插入的图片应能在重新载入时还原（真实图片而非占位）
    func testInsertedImageReloadsFromAttachmentStore() throws {
        let noteID = UUID()
        let (textView, controller) = makeEditor(noteID: noteID)
        let source = tempDir.appendingPathComponent("reload.png")
        try makeTestImage(at: source)
        try controller.insertImage(from: source)

        let document = RichTextBridge.document(from: textView.textStorage!)
        let html = RichTextHTMLCodec.encode(document)

        // 模拟重新打开笔记：从 HTML 解析并重建 attributed string
        let reloaded = RichTextHTMLCodec.decode(html)
        let attributed = RichTextBridge.attributedString(from: reloaded, attachments: store.attachments)
        let attachment = attributed.attribute(.attachment, at: 0, effectiveRange: nil) as? NSTextAttachment
        XCTAssertNotNil(attachment, "重新载入后应是真实附件")
        XCTAssertNotNil(attachment?.image)
    }

    // MARK: - 粘贴图片

    /// 粘贴图片文件时，必须变成内联图片，而不是一段路径文本
    func testPastingImageFileCreatesInlineImageNotPathText() throws {
        let noteID = UUID()
        let (textView, controller) = makeEditor(noteID: noteID)
        let source = tempDir.appendingPathComponent("pasted.png")
        try makeTestImage(at: source)

        // 模拟系统粘贴：把图片文件放进剪贴板
        let pasteboard = NSPasteboard.general
        pasteboard.clearContents()
        pasteboard.writeObjects([source as NSURL])

        type("粘贴前", into: textView)
        let richTextView = RichTextNSTextView(frame: .zero)
        var imported: URL?
        richTextView.onPasteImage = { url in
            imported = url
            try? controller.insertImage(from: url)
        }
        richTextView.paste(nil)

        XCTAssertNotNil(imported, "应识别出剪贴板中的图片文件")

        let document = RichTextBridge.document(from: textView.textStorage!)
        let imageRun = document.blocks.flatMap { $0.runs }.first { $0.image != nil }
        XCTAssertNotNil(imageRun, "粘贴后正文应包含图片 run")
        XCTAssertTrue(imageRun?.image?.path.hasPrefix("attachments/") == true)
        XCTAssertFalse(document.plainText.contains(".png"), "不应把图片路径当作文本插入")
    }

    /// 剪贴板里的位图数据也应被识别
    func testPastingBitmapDataCreatesInlineImage() throws {
        let noteID = UUID()
        let (textView, controller) = makeEditor(noteID: noteID)

        let image = NSImage(size: NSSize(width: 30, height: 20))
        image.lockFocus()
        NSColor.systemGreen.setFill()
        NSRect(x: 0, y: 0, width: 30, height: 20).fill()
        image.unlockFocus()
        guard let tiff = image.tiffRepresentation,
              let bitmap = NSBitmapImageRep(data: tiff),
              let png = bitmap.representation(using: .png, properties: [:]) else {
            return XCTFail("无法构造测试位图")
        }

        let pasteboard = NSPasteboard.general
        pasteboard.clearContents()
        pasteboard.setData(png, forType: .png)

        let richTextView = RichTextNSTextView(frame: .zero)
        var imported: URL?
        richTextView.onPasteImage = { url in
            imported = url
            try? controller.insertImage(from: url)
        }
        richTextView.paste(nil)

        XCTAssertNotNil(imported, "位图数据应被识别为图片")

        let document = RichTextBridge.document(from: textView.textStorage!)
        XCTAssertTrue(document.blocks.flatMap { $0.runs }.contains { $0.image != nil })
    }

    // MARK: - 粘贴路径文本

    /// 粘贴一段图片路径文本，必须变成内联图片，而不是路径字符串
    func testPastingImagePathTextCreatesInlineImage() throws {
        let noteID = UUID()
        let (textView, controller) = makeEditor(noteID: noteID)
        let source = tempDir.appendingPathComponent("clip.png")
        try makeTestImage(at: source)

        type("粘贴前", into: textView)
        let inserted = try controller.insertTextWithImagePaths(source.path)

        XCTAssertEqual(inserted, 1, "应插入 1 张图片")

        let document = RichTextBridge.document(from: textView.textStorage!)
        let imageRun = document.blocks.flatMap { $0.runs }.first { $0.image != nil }
        XCTAssertNotNil(imageRun, "正文应包含图片 run")
        XCTAssertTrue(imageRun?.image?.path.hasPrefix("attachments/\(noteID.uuidString)/") == true,
                      "图片应进入本笔记的附件目录")
        XCTAssertFalse(document.plainText.contains(".png"), "不应把路径当作文本插入")
        XCTAssertTrue(document.plainText.contains("粘贴前"), "周围文字应保留")

        // 附件确实落盘
        let files = try FileManager.default.contentsOfDirectory(atPath: store.attachments.directory(for: noteID).path)
        XCTAssertEqual(files.count, 1)
    }

    /// 路径文本混在文字中间时，路径变图片，文字保留
    func testPastingPathWithinTextKeepsSurroundingWords() throws {
        let noteID = UUID()
        let (textView, controller) = makeEditor(noteID: noteID)
        let source = tempDir.appendingPathComponent("mid.png")
        try makeTestImage(at: source)

        let payload = "看这个图 \(source.path) 很清楚"
        let inserted = try controller.insertTextWithImagePaths(payload)

        XCTAssertEqual(inserted, 1)
        let document = RichTextBridge.document(from: textView.textStorage!)
        XCTAssertTrue(document.plainText.contains("看这个图"))
        XCTAssertTrue(document.plainText.contains("很清楚"))
        XCTAssertFalse(document.plainText.contains(".png"))
        XCTAssertTrue(document.blocks.flatMap { $0.runs }.contains { $0.image != nil })
    }

    /// 图片显示尺寸必须取文件真实尺寸，而不是历史污染的占位值
    func testImageDisplaySizeUsesActualFileSize() throws {
        let noteID = UUID()
        let source = tempDir.appendingPathComponent("real.png")
        try makeTestImage(at: source, width: 300, height: 200)

        let relative = try store.attachments.importImage(at: source, noteID: noteID)

        // 故意带上错误的声明尺寸（模拟历史污染值）
        let document = RichTextDocument(blocks: [
            RichTextBlock(style: .paragraph, runs: [
                RichTextRun(image: RichTextImage(path: relative, width: 160, height: 90))
            ])
        ])

        let attributed = RichTextBridge.attributedString(from: document, attachments: store.attachments)
        let attachment = attributed.attribute(.attachment, at: 0, effectiveRange: nil) as? NSTextAttachment
        XCTAssertNotNil(attachment)
        XCTAssertEqual(attachment?.bounds.width ?? 0, 300, accuracy: 1, "应使用文件真实宽度")
        XCTAssertEqual(attachment?.bounds.height ?? 0, 200, accuracy: 1, "应使用文件真实高度")
    }

    /// 往返后正文里存的尺寸必须与真实文件一致
    func testEncodedImageSizeMatchesRealFile() throws {
        let noteID = UUID()
        let source = tempDir.appendingPathComponent("rt.png")
        try makeTestImage(at: source, width: 300, height: 200)
        let relative = try store.attachments.importImage(at: source, noteID: noteID)

        let document = RichTextDocument(blocks: [
            RichTextBlock(style: .paragraph, runs: [
                RichTextRun(image: RichTextImage(path: relative, width: 160, height: 90))
            ])
        ])

        let attributed = RichTextBridge.attributedString(from: document, attachments: store.attachments)
        let back = RichTextBridge.document(from: attributed)
        let html = RichTextHTMLCodec.encode(back)

        XCTAssertTrue(html.contains("width=\"300\""), "HTML 应记录真实宽度，实际：\(html)")
        XCTAssertTrue(html.contains("height=\"200\""), "HTML 应记录真实高度，实际：\(html)")
        XCTAssertFalse(html.contains("width=\"160\""), "不应保留污染的占位宽度")
    }

    /// 编辑过程中：先输入文字，再插图，再输入文字，三者应在同一段落
    func testTypingTextThenImageThenTextKeepsOneParagraph() throws {
        let noteID = UUID()
        let (textView, controller) = makeEditor(noteID: noteID)
        let source = tempDir.appendingPathComponent("seq.png")
        try makeTestImage(at: source, width: 120, height: 60)

        type("前面", into: textView)
        try controller.insertImage(from: source)
        type("后面", into: textView)

        let document = RichTextBridge.document(from: textView.textStorage!)
        XCTAssertEqual(document.blocks.count, 1,
                       "应只有一个段落，实际：\(document.blocks.count)  内容：\(document.blocks.map { $0.plainText })")
        XCTAssertTrue(document.plainText.contains("前面"))
        XCTAssertTrue(document.plainText.contains("后面"))
    }

    /// 载入真实笔记里那种「段首两张图 + 末尾文字」的 HTML，段落结构必须保持
    func testLoadingHTMLWithLeadingImagesKeepsParagraphStructure() {
        let html = "<p><img src=\"attachments/a/1.png\" width=\"224\" height=\"94\"><img src=\"attachments/a/2.png\" width=\"380\" height=\"178\">asfd</p>"
        let document = RichTextHTMLCodec.decode(html)
        XCTAssertEqual(document.blocks.count, 1, "HTML 层应解析为一个段落")
        XCTAssertEqual(document.blocks.first?.runs.count, 3, "两个图片 run + 一个文字 run")

        // 关键：桥接层往返后段落数不能变
        let attributed = RichTextBridge.attributedString(from: document, attachments: store.attachments)
        let back = RichTextBridge.document(from: attributed)
        XCTAssertEqual(back.blocks.count, 1,
                       "桥接往返后段落数不应变化，实际：\(back.blocks.count)")
        XCTAssertEqual(back.blocks.first?.plainText, "asfd")
    }

    // MARK: - 图文同段落

    /// 图片与后续文字必须留在同一段落（AppKit 桥接层往返后不能被拆开）
    func testImageFollowedByTextStaysInSameParagraphAfterBridge() throws {
        let noteID = UUID()
        let source = tempDir.appendingPathComponent("mix.png")
        try makeTestImage(at: source, width: 200, height: 100)
        let relative = try store.attachments.importImage(at: source, noteID: noteID)

        let document = RichTextDocument(blocks: [
            RichTextBlock(style: .paragraph, runs: [
                RichTextRun(image: RichTextImage(path: relative, width: 200, height: 100)),
                RichTextRun(text: "asfd")
            ])
        ])

        let attributed = RichTextBridge.attributedString(from: document, attachments: store.attachments)
        let back = RichTextBridge.document(from: attributed)

        XCTAssertEqual(back.blocks.count, 1, "图片与文字应同属一个段落，实际段落数：\(back.blocks.count)")
        XCTAssertEqual(back.blocks.first?.plainText, "asfd")

        let html = RichTextHTMLCodec.encode(back)
        XCTAssertFalse(html.contains("</p>\n<p>"), "不应把同一段落拆开，实际：\(html)")
    }

    // MARK: - 链接

    func testInsertLinkOnSelection() {
        let (textView, controller) = makeEditor(noteID: UUID())
        type("查看详情", into: textView)
        selectAll(textView)
        controller.insertLink(url: URL(string: "https://example.com")!)

        let document = RichTextBridge.document(from: textView.textStorage!)
        XCTAssertTrue(document.blocks[0].runs.contains { $0.link == "https://example.com" })
        XCTAssertEqual(document.plainText, "查看详情")
    }

    // MARK: - 跨笔记隔离

    /// 编辑器 A 的操作绝不能影响编辑器 B（不同笔记、不同附件目录）
    func testTwoEditorsRemainIsolated() throws {
        let noteA = UUID()
        let noteB = UUID()
        let (textViewA, controllerA) = makeEditor(noteID: noteA)
        let (textViewB, controllerB) = makeEditor(noteID: noteB)

        type("A 篇正文", into: textViewA)
        selectAll(textViewA)
        controllerA.toggleBold()
        controllerA.setFontSize(28)
        controllerA.applyBlockStyle(.heading1)

        type("B 篇正文", into: textViewB)
        selectAll(textViewB)
        controllerB.toggleList(.orderedItem)

        let docA = RichTextBridge.document(from: textViewA.textStorage!)
        let docB = RichTextBridge.document(from: textViewB.textStorage!)

        XCTAssertEqual(docA.plainText, "A 篇正文")
        XCTAssertEqual(docB.plainText, "B 篇正文")
        XCTAssertEqual(docA.blocks[0].style, RichTextBlockStyle.heading1)
        XCTAssertEqual(docB.blocks[0].style, RichTextBlockStyle.orderedItem)
    }

    /// 同一篇笔记里插入图片，附件必须落在自己的目录下
    func testImagesGoToCorrectNoteDirectory() throws {
        let noteA = UUID()
        let noteB = UUID()
        let (_, controllerA) = makeEditor(noteID: noteA)
        let (_, controllerB) = makeEditor(noteID: noteB)

        let source = tempDir.appendingPathComponent("multi.png")
        try makeTestImage(at: source)

        try controllerA.insertImage(from: source)
        try controllerB.insertImage(from: source)

        let filesA = try FileManager.default.contentsOfDirectory(atPath: store.attachments.directory(for: noteA).path)
        let filesB = try FileManager.default.contentsOfDirectory(atPath: store.attachments.directory(for: noteB).path)
        XCTAssertEqual(filesA.count, 1)
        XCTAssertEqual(filesB.count, 1)
        XCTAssertNotEqual(filesA[0], filesB[0], "不同笔记的附件文件名必须唯一")
    }

    // MARK: - 工具

    private func makeTestImage(at url: URL, width: CGFloat = 60, height: CGFloat = 40) throws {
        let image = NSImage(size: NSSize(width: width, height: height))
        image.lockFocus()
        NSColor.systemBlue.setFill()
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
