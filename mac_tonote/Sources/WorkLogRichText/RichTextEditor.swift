import SwiftUI
import AppKit
import WorkLogCore

/// 富文本正文编辑器（NSTextView 包装）。
///
/// 与旧 Markdown 编辑器的关键差异：
/// 1. 富文本模式，图片以 `NSTextAttachment` 真实显示，可与文字混排；
/// 2. 正文序列化为受控 HTML（跨平台可解析），图片只记录相对路径；
/// 3. 保留按笔记 UUID 隔离的全部保护，避免跨笔记串写。
public struct RichTextEditor: NSViewRepresentable {
    public let noteID: UUID
    @Binding public var content: NoteContent
    public var controller: RichTextEditorController
    public var attachments: AttachmentStore
    /// 编辑器错误回调（由上层展示）
    public var onEditorError: ((String) -> Void)?

    public init(noteID: UUID,
                content: Binding<NoteContent>,
                controller: RichTextEditorController,
                attachments: AttachmentStore,
                onEditorError: ((String) -> Void)? = nil) {
        self.noteID = noteID
        self._content = content
        self.controller = controller
        self.attachments = attachments
        self.onEditorError = onEditorError
    }

    public func makeCoordinator() -> Coordinator { Coordinator(self) }

    public func makeNSView(context: Context) -> NSScrollView {
        let scrollView = NSScrollView()
        let tv = RichTextNSTextView(frame: .zero)
        tv.isVerticallyResizable = true
        tv.isHorizontallyResizable = false
        tv.autoresizingMask = [.width]
        tv.textContainer?.widthTracksTextView = true
        scrollView.documentView = tv
        scrollView.hasVerticalScroller = true
        scrollView.hasHorizontalScroller = false

        tv.delegate = context.coordinator
        tv.isRichText = true
        tv.allowsUndo = true
        tv.isAutomaticQuoteSubstitutionEnabled = false
        tv.isAutomaticDashSubstitutionEnabled = false
        tv.isAutomaticTextReplacementEnabled = false
        tv.isAutomaticSpellingCorrectionEnabled = false
        tv.textContainerInset = NSSize(width: 4, height: 14)
        tv.backgroundColor = .white
        tv.drawsBackground = true
        tv.isVerticallyResizable = true
        tv.isHorizontallyResizable = false
        tv.autoresizingMask = [.width]
        tv.textContainer?.widthTracksTextView = true
        tv.typingAttributes = [
            .font: NSFont.systemFont(ofSize: RichTextBridge.defaultFontSize),
            .foregroundColor: RichTextBridge.defaultTextColor
        ]

        context.coordinator.update(parent: self, textView: tv)
        controller.textView = tv
        controller.attachments = attachments
        controller.noteID = noteID

        context.coordinator.installPasteHandler(on: tv)
        context.coordinator.load(content: content, into: tv, attachments: attachments)
        return scrollView
    }

    public func updateNSView(_ scrollView: NSScrollView, context: Context) {
        guard let tv = scrollView.documentView as? NSTextView else { return }

        // NSViewRepresentable 可能复用 Coordinator；每次更新都必须刷新到当前笔记。
        context.coordinator.update(parent: self, textView: tv)
        controller.textView = tv
        controller.attachments = attachments
        controller.noteID = noteID

        // 粘贴回调要随笔记切换重新绑定，避免指向旧笔记的附件目录
        if let richTextView = tv as? RichTextNSTextView {
            context.coordinator.installPasteHandler(on: richTextView)
        }

        // 只有当外部内容确实变化时才重载，避免打断正在输入的光标与撤销栈。
        if context.coordinator.loadedBody != content.body {
            context.coordinator.load(content: content, into: tv, attachments: attachments)
        }
    }

    public final class Coordinator: NSObject, NSTextViewDelegate {
        var parent: RichTextEditor
        var isUpdatingProgrammatically = false
        private weak var textView: NSTextView?
        private var representedNoteID: UUID
        /// 最近一次载入或写回正文的原始字符串，用于判断外部是否真的变了
        private(set) var loadedBody: String = ""

        init(_ parent: RichTextEditor) {
            self.parent = parent
            representedNoteID = parent.noteID
            loadedBody = parent.content.body
        }

        func update(parent: RichTextEditor, textView: NSTextView) {
            let switchedNote = parent.noteID != representedNoteID
            self.parent = parent
            self.textView = textView
            representedNoteID = parent.noteID
            if switchedNote {
                loadedBody = parent.content.body
            }
        }

        /// 绑定粘贴回调（始终指向当前笔记）
        func installPasteHandler(on tv: RichTextNSTextView) {
            tv.onPasteImage = { [weak tv] url in
                guard tv != nil else { return }
                do {
                    try self.parent.controller.insertImage(from: url)
                } catch {
                    self.parent.onEditorError?(error.localizedDescription)
                }
            }
            // 剪贴板里是「图片路径文本」时，把路径换成内联图片，其余文字原样保留
            tv.onPasteText = { [weak tv] text in
                guard tv != nil else { return }
                do {
                    try self.parent.controller.insertTextWithImagePaths(text)
                } catch {
                    self.parent.onEditorError?(error.localizedDescription)
                }
            }
        }

        /// 载入正文到编辑器
        func load(content: NoteContent, into tv: NSTextView, attachments: AttachmentStore) {
            let document: RichTextDocument = content.format == .richText
                ? RichTextHTMLCodec.decode(content.body)
                : MarkdownConverter.toDocument(content.body)
            let attributed = RichTextBridge.attributedString(from: document, attachments: attachments)

            isUpdatingProgrammatically = true
            tv.textStorage?.setAttributedString(attributed)
            tv.setSelectedRange(NSRange(location: 0, length: 0))
            tv.typingAttributes = [
                .font: NSFont.systemFont(ofSize: RichTextBridge.defaultFontSize),
                .foregroundColor: RichTextBridge.defaultTextColor
            ]
            isUpdatingProgrammatically = false

            loadedBody = content.body
        }

        public func textDidChange(_ notification: Notification) {
            guard !isUpdatingProgrammatically,
                  let tv = notification.object as? NSTextView,
                  tv === textView,
                  parent.noteID == representedNoteID else {
                return
            }

            let document = RichTextBridge.document(from: tv.textStorage ?? NSAttributedString())
            let html = RichTextHTMLCodec.encode(document)
            loadedBody = html

            // 正文与格式一起写入，避免出现「HTML 正文 + markdown 格式」的中间态
            parent.content = NoteContent(body: html, format: .richText)
        }
    }
}
