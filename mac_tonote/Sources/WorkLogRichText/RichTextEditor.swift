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
    /// 只读模式（回收站笔记）：仍可选中复制文字，但不能修改正文
    public var readOnly: Bool = false
    /// 正文距边框的留白。笔记要给表格圆点留位置，任务备注可以更紧。
    public var textInset: CGSize = CGSize(width: 24, height: 24)
    /// 选区变化回调。
    /// - 传入 nil：当前无选区（或只读/窗口失焦），上层应收起浮动格式窗口；
    /// - 传入 rect：选区首字所在的屏幕坐标矩形，供上层定位浮动格式窗口。
    public var onSelectionChange: ((NSRect?) -> Void)?

    public init(noteID: UUID,
                content: Binding<NoteContent>,
                controller: RichTextEditorController,
                attachments: AttachmentStore,
                onEditorError: ((String) -> Void)? = nil,
                readOnly: Bool = false,
                textInset: CGSize = CGSize(width: 24, height: 24),
                onSelectionChange: ((NSRect?) -> Void)? = nil) {
        self.noteID = noteID
        self._content = content
        self.controller = controller
        self.attachments = attachments
        self.onEditorError = onEditorError
        self.readOnly = readOnly
        self.textInset = textInset
        self.onSelectionChange = onSelectionChange
    }

    public func makeCoordinator() -> Coordinator { Coordinator(self) }

    public func makeNSView(context: Context) -> NSScrollView {
        let scrollView = NSScrollView()
        // 表格用 NSTextTable，只有 TextKit 1 会画边框并让单元格横排。
        let tv = RichTextNSTextView.makeTextKit1()
        tv.isVerticallyResizable = true
        tv.isHorizontallyResizable = false
        tv.autoresizingMask = [.width]
        tv.textContainer?.widthTracksTextView = true
        scrollView.documentView = tv
        scrollView.hasVerticalScroller = true
        scrollView.hasHorizontalScroller = false

        tv.delegate = context.coordinator
        tv.isRichText = true
        tv.isEditable = !readOnly
        tv.isSelectable = true
        tv.allowsUndo = true
        tv.isAutomaticQuoteSubstitutionEnabled = false
        tv.isAutomaticDashSubstitutionEnabled = false
        tv.isAutomaticTextReplacementEnabled = false
        tv.isAutomaticSpellingCorrectionEnabled = false
        // 左右各留 24pt：表格左边界外侧要放「增加行」的小灰圆，放 4pt 会被裁掉；
        // 上方同样留 24pt，给顶部「增加列」的灰圆和蓝色圆留位置。
        tv.textContainerInset = NSSize(width: textInset.width, height: textInset.height)
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

        // 滚动时让浮动格式窗口跟着选区移动
        scrollView.contentView.postsBoundsChangedNotifications = true
        NotificationCenter.default.addObserver(
            context.coordinator,
            selector: #selector(Coordinator.scrollViewBoundsChanged(_:)),
            name: NSView.boundsDidChangeNotification,
            object: scrollView.contentView
        )

        // 属性类命令（颜色 / 字号 / 段落样式）不一定触发 textDidChange，
        // 这里把「命令执行完 → 序列化回正文」的回调接到 Coordinator。
        let coordinator = context.coordinator
        controller.onContentChanged = { [weak coordinator, weak tv] in
            guard let coordinator, let tv else { return }
            coordinator.persist(from: tv)
        }

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
        // 视图复用时 readOnly 可能随「进入/退出回收站」变化，每次更新都同步
        tv.isEditable = !readOnly
        tv.isSelectable = true
        tv.textContainerInset = NSSize(width: textInset.width, height: textInset.height)

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
        /// 选区变化防抖：拖动选择过程中不要反复弹出浮窗
        private var selectionWork: DispatchWorkItem?

        init(_ parent: RichTextEditor) {
            self.parent = parent
            representedNoteID = parent.noteID
            loadedBody = parent.content.body
        }

        deinit {
            selectionWork?.cancel()
            NotificationCenter.default.removeObserver(self)
        }

        func update(parent: RichTextEditor, textView: NSTextView) {
            let switchedNote = parent.noteID != representedNoteID
            let readOnlyChanged = self.parent.readOnly != parent.readOnly
            self.parent = parent
            self.textView = textView
            representedNoteID = parent.noteID
            if switchedNote {
                selectionWork?.cancel()
                parent.onSelectionChange?(nil)
                loadedBody = parent.content.body
            } else if readOnlyChanged {
                // 进入 / 退出回收站时立刻收起或恢复浮窗
                refreshSelection()
            }
        }

        /// 绑定粘贴回调（始终指向当前笔记）
        func installPasteHandler(on tv: RichTextNSTextView) {
            tv.tableController = parent.controller
            tv.onPasteImage = { [weak tv] url in
                guard tv != nil else {
                    NSLog("%@", "[TNPaste] onPasteImage 触发但 textView 已释放")
                    return
                }
                NSLog("%@", "[TNPaste] onPasteImage 触发：\(url.lastPathComponent)")
                do {
                    try self.parent.controller.insertImage(from: url)
                } catch {
                    NSLog("%@", "[TNPaste] insertImage 抛错：\(error.localizedDescription)")
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
            tv.onTabInList = { [weak self] delta in
                guard let self else { return false }
                if self.parent.controller.handleTabInTable(shift: delta < 0) { return true }
                return self.parent.controller.changeListLevel(by: delta)
            }
            tv.onPasteFailure = { [weak self] message in
                NSLog("%@", "[TNPaste] onPasteFailure → \(message)")
                self?.parent.onEditorError?(message)
            }
            // 表格边沿「+」：把插入位置与表格内字符位置交给命令层，
            // 不依赖当前选区，鼠标停在表外也能正确定位目标表格。
            tv.onTableGutterInsertRow = { [weak self] index, probe in
                self?.parent.controller.insertTableRow(at: index, probe: probe)
            }
            tv.onTableGutterInsertColumn = { [weak self] index, probe in
                self?.parent.controller.insertTableColumn(at: index, probe: probe)
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
            // 载入会清空选区，浮窗也随之收起
            parent.onSelectionChange?(nil)
            parent.controller.refreshCurrentStyle()
            // 编程式 setAttributedString 不触发 didChangeText，手动重算表格灰圆与格子矩形。
            // 宽表格会把横向滚动停在中间，打开笔记时强制从最左侧开始。
            if let rich = tv as? RichTextNSTextView {
                rich.refreshTableGutter()
                rich.showFromDocumentStart()
            }
        }

        public func textDidChange(_ notification: Notification) {
            guard let tv = notification.object as? NSTextView else { return }
            persist(from: tv)
        }

        /// 列表段落里的键盘命令：
        /// - Tab / Shift+Tab：切换编号体系（1. ↔ a. ↔ i.）
        /// - 内容最前面按退格：退出列表变正文（飞书行为）
        public func textView(_ textView: NSTextView, doCommandBy commandSelector: Selector) -> Bool {
            if commandSelector == NSSelectorFromString("insertTab:") {
                if parent.controller.handleTabInTable(shift: false) { return true }
                return parent.controller.changeListLevel(by: 1)
            }
            if commandSelector == NSSelectorFromString("insertBacktab:") {
                if parent.controller.handleTabInTable(shift: true) { return true }
                return parent.controller.changeListLevel(by: -1)
            }
            if commandSelector == NSSelectorFromString("deleteBackward:") {
                return parent.controller.cancelListAtCaret()
            }
            return false
        }

        /// 输入前的最后一道保险：不让标记前缀属性被后续输入的字继承。
        public func textView(_ textView: NSTextView,
                             shouldChangeTextIn affectedCharRange: NSRange,
                             replacementString: String?) -> Bool {
            if !RichTextTableLayout.shouldAllowChange(in: textView,
                                                     range: affectedCharRange,
                                                     replacement: replacementString) {
                return false
            }
            clearMarkerTypingAttribute(in: textView)
            return true
        }

        private func clearMarkerTypingAttribute(in tv: NSTextView) {
            guard tv.typingAttributes[RichTextKeys.listMarker] != nil else { return }
            var typing = tv.typingAttributes
            typing.removeValue(forKey: RichTextKeys.listMarker)
            // 空心圆的纯描边属性也不能被正文继承
            typing.removeValue(forKey: .strokeWidth)
            typing.removeValue(forKey: .strokeColor)
            // 属性确实是从蓝色标记派生来的，才把字重与颜色恢复成正文；
            // 用户手动设过颜色时不动，保留可手动修改的能力。
            let inherited = (typing[.foregroundColor] as? NSColor)?
                .isEqual(RichTextBridge.listMarkerColor) ?? false
            if inherited {
                typing[.font] = RichTextBridge.font(size: RichTextBridge.defaultFontSize,
                                                    bold: false, italic: false)
                typing[.foregroundColor] = RichTextBridge.defaultTextColor
            }
            tv.typingAttributes = typing
        }

        /// 把当前 NSTextStorage 序列化回正文（输入与格式命令共用）。
        func persist(from tv: NSTextView) {
            guard !isUpdatingProgrammatically,
                  tv === textView,
                  parent.noteID == representedNoteID else {
                return
            }

            // 先同步列表的可见标记（编号连续性、层级、蓝色样式）。
            // 这些字符只是视图层前缀，编辑期间屏蔽递归通知与 undo 注册。
            if let storage = tv.textStorage {
                let undo = tv.undoManager
                isUpdatingProgrammatically = true
                undo?.disableUndoRegistration()
                RichTextTableLayout.repairIdentity(in: storage)
                RichTextBridge.normalizeListMarkers(in: storage)
                undo?.enableUndoRegistration()
                isUpdatingProgrammatically = false
            }

            // 光标紧贴标记后面时，NSTextView 会把标记属性派生进 typingAttributes，
            // 接下来输入的字就会被当成前缀而丢失；每次写回后清一次。
            clearMarkerTypingAttribute(in: tv)

            let document = RichTextBridge.document(from: tv.textStorage ?? NSAttributedString())
            let html = RichTextHTMLCodec.encode(document)
            loadedBody = html

            // 正文与格式一起写入，避免出现「HTML 正文 + markdown 格式」的中间态
            parent.content = NoteContent(body: html, format: .richText)
        }

        // MARK: - 选区 → 浮动格式窗口

        public func textViewDidChangeSelection(_ notification: Notification) {
            guard let tv = notification.object as? NSTextView, tv === textView else { return }
            parent.controller.refreshCurrentStyle()
            evaluateSelection(in: tv, debounce: true)
        }

        /// 滚动时重新定位浮窗，避免它停在原处与选区脱节。
        @objc func scrollViewBoundsChanged(_ notification: Notification) {
            guard let tv = textView else { return }
            evaluateSelection(in: tv, debounce: false)
        }

        func refreshSelection() {
            guard let tv = textView else {
                parent.onSelectionChange?(nil)
                return
            }
            evaluateSelection(in: tv, debounce: false)
        }

        private func evaluateSelection(in tv: NSTextView, debounce: Bool) {
            selectionWork?.cancel()

            let range = tv.selectedRange()
            guard !parent.readOnly,
                  range.length > 0,
                  let window = tv.window,
                  window.isKeyWindow,
                  parent.onSelectionChange != nil else {
                parent.onSelectionChange?(nil)
                return
            }

            let report = { [weak self, weak tv] in
                guard let self, let tv, tv.window != nil else { return }
                let current = tv.selectedRange()
                guard !self.parent.readOnly,
                      current.length > 0,
                      let rect = self.selectionScreenRect(in: tv, range: current) else {
                    self.parent.onSelectionChange?(nil)
                    return
                }
                self.parent.onSelectionChange?(rect)
            }

            if debounce {
                let work = DispatchWorkItem(block: report)
                selectionWork = work
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.25, execute: work)
            } else {
                report()
            }
        }

        /// 取选区首字的矩形，并换算到屏幕坐标（AppKit 全局坐标，y 轴向上）。
        private func selectionScreenRect(in tv: NSTextView, range: NSRange) -> NSRect? {
            guard let layoutManager = tv.layoutManager,
                  let textContainer = tv.textContainer,
                  let window = tv.window else { return nil }

            let firstCharacter = NSRange(location: range.location, length: 1)
            let glyphRange = layoutManager.glyphRange(forCharacterRange: firstCharacter,
                                                      actualCharacterRange: nil)
            var rect = layoutManager.boundingRect(forGlyphRange: glyphRange, in: textContainer)
            rect.origin.x += tv.textContainerOrigin.x
            rect.origin.y += tv.textContainerOrigin.y
            let windowRect = tv.convert(rect, to: nil)
            return window.convertToScreen(windowRect)
        }
    }
}
