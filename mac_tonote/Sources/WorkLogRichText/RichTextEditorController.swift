import AppKit
import Combine
import WorkLogCore

/// 富文本编辑命令控制器。
///
/// 所有命令作用于当前 `NSTextView` 的选区；
/// 命令执行后触发 `textDidChange` → 序列化回正文（受控 HTML）。
public final class RichTextEditorController: ObservableObject {

    public weak var textView: NSTextView?
    /// 附件仓库（导入图片用）
    public var attachments: AttachmentStore?
    /// 当前笔记 ID（决定附件子目录）
    public var noteID: UUID?

    /// 属性类命令（颜色 / 字号 / 段落样式）执行完后的回调。
    /// `NSTextStorage` 的属性变更不保证触发 `NSTextView.textDidChange`，
    /// 因此由编辑器宿主显式把正文重新序列化写回，避免格式改了却没保存。
    public var onContentChanged: (() -> Void)?

    /// 当前光标所在段落的样式，供工具栏「T」样式菜单显示勾选状态。
    @Published public private(set) var currentBlockStyle: RichTextBlockStyle = .paragraph
    /// 当前光标所在列表的层级，供「T」菜单显示 1. / a. / i. 的编号体系。
    @Published public private(set) var currentListLevel: Int = 0

    public init() {}

    private func commitChanges() {
        onContentChanged?()
    }

    // MARK: - 基础开关

    public func toggleBold() {
        applyToSelection { font in
            RichTextBridge.isBold(font)
                ? NSFontManager.shared.convert(font, toNotHaveTrait: .boldFontMask)
                : NSFontManager.shared.convert(font, toHaveTrait: .boldFontMask)
        }
    }

    public func toggleItalic() {
        applyToSelection { font in
            RichTextBridge.isItalic(font)
                ? NSFontManager.shared.convert(font, toNotHaveTrait: .italicFontMask)
                : NSFontManager.shared.convert(font, toHaveTrait: .italicFontMask)
        }
    }

    public func toggleUnderline() {
        toggleAttribute(.underlineStyle, value: NSUnderlineStyle.single.rawValue)
    }

    public func toggleStrike() {
        toggleAttribute(.strikethroughStyle, value: NSUnderlineStyle.single.rawValue)
    }

    /// 设置字号（无选区时作用于后续输入）
    public func setFontSize(_ size: CGFloat) {
        guard let tv = textView, let storage = tv.textStorage else { return }
        let selection = tv.selectedRange()

        if selection.length == 0 {
            var typing = tv.typingAttributes
            let font = typing[.font] as? NSFont ?? NSFont.systemFont(ofSize: RichTextBridge.defaultFontSize)
            typing[.font] = RichTextBridge.font(size: size,
                                                bold: RichTextBridge.isBold(font),
                                                italic: RichTextBridge.isItalic(font))
            tv.typingAttributes = typing
            return
        }

        storage.beginEditing()
        for range in runRanges(in: storage, range: selection) {
            let font = storage.attribute(.font, at: range.location, effectiveRange: nil) as? NSFont
                ?? NSFont.systemFont(ofSize: RichTextBridge.defaultFontSize)
            storage.addAttribute(.font,
                                 value: RichTextBridge.font(size: size,
                                                            bold: RichTextBridge.isBold(font),
                                                            italic: RichTextBridge.isItalic(font)),
                                 range: range)
        }
        storage.endEditing()
        commitChanges()
    }

    /// 设置文字颜色（无选区时作用于后续输入）
    public func setTextColor(_ color: NSColor) {
        guard let tv = textView, let storage = tv.textStorage else { return }
        let selection = tv.selectedRange()

        if selection.length == 0 {
            var typing = tv.typingAttributes
            typing[.foregroundColor] = color
            tv.typingAttributes = typing
            return
        }

        storage.beginEditing()
        storage.addAttribute(.foregroundColor, value: color, range: selection)
        storage.endEditing()
        commitChanges()
    }

    /// 设置文字背景色 / 高亮色（无选区时作用于后续输入）
    public func setBackgroundColor(_ color: NSColor) {
        guard let tv = textView, let storage = tv.textStorage else { return }
        let selection = tv.selectedRange()

        if selection.length == 0 {
            var typing = tv.typingAttributes
            typing[.backgroundColor] = color
            tv.typingAttributes = typing
            return
        }

        storage.beginEditing()
        storage.addAttribute(.backgroundColor, value: color, range: selection)
        storage.endEditing()
        commitChanges()
    }

    /// 清除选区（或后续输入）的文字背景色
    public func clearBackgroundColor() {
        guard let tv = textView, let storage = tv.textStorage else { return }
        let selection = tv.selectedRange()

        if selection.length == 0 {
            var typing = tv.typingAttributes
            typing.removeValue(forKey: .backgroundColor)
            tv.typingAttributes = typing
            return
        }

        storage.beginEditing()
        storage.removeAttribute(.backgroundColor, range: selection)
        storage.endEditing()
        commitChanges()
    }

    // MARK: - 段落与列表

    /// 应用段落样式（正文/标题/引用/代码）
    public func applyBlockStyle(_ style: RichTextBlockStyle) {
        guard let tv = textView, let storage = tv.textStorage else { return }
        applyBlockStyle(style, to: storage, in: tv, level: 0)
    }

    /// 由编辑器的选区变化回调驱动，刷新「T」菜单里的当前段落样式。
    public func refreshCurrentStyle() {
        guard let tv = textView, let storage = tv.textStorage, storage.length > 0 else {
            currentBlockStyle = .paragraph
            currentListLevel = 0
            return
        }
        let location = min(tv.selectedRange().location, storage.length - 1)
        let style = resolveStyle(storage: storage, range: NSRange(location: location, length: 0))
        if currentBlockStyle != style { currentBlockStyle = style }
        let level = (storage.attribute(RichTextKeys.listLevel, at: location, effectiveRange: nil) as? NSNumber)?.intValue ?? 0
        if currentListLevel != level { currentListLevel = level }
    }

    /// 切换无序列表 / 有序列表。
    /// 已是该列表时，再次点击提升层级（1. → a. → i.），到顶后再点才回到正文。
    public func toggleList(_ style: RichTextBlockStyle) {
        guard let tv = textView, let storage = tv.textStorage else { return }
        let ranges = paragraphRanges(in: storage, range: tv.selectedRange())
        guard !ranges.isEmpty else { return }
        let allMatch = ranges.allSatisfy { resolveStyle(storage: storage, range: $0) == style }
        guard allMatch else {
            applyBlockStyle(style, to: storage, in: tv, level: 0)
            return
        }
        let currentLevel = ranges.map {
            (storage.attribute(RichTextKeys.listLevel, at: $0.location, effectiveRange: nil) as? NSNumber)?.intValue ?? 0
        }.max() ?? 0
        if currentLevel < 2 {
            applyBlockStyle(style, to: storage, in: tv, level: currentLevel + 1)
        } else {
            applyBlockStyle(.paragraph, to: storage, in: tv, level: 0)
        }
    }

    /// Tab / Shift+Tab 调整列表缩进层级；当前段落不是列表时返回 false，交回系统默认处理。
    @discardableResult
    public func changeListLevel(by delta: Int) -> Bool {
        guard let tv = textView, let storage = tv.textStorage else { return false }
        let ranges = paragraphRanges(in: storage, range: tv.selectedRange())
        let listRanges = ranges.filter {
            let style = resolveStyle(storage: storage, range: $0)
            return style == .orderedItem || style == .bulletItem
        }
        guard !listRanges.isEmpty else { return false }

        storage.beginEditing()
        for range in listRanges {
            let style = resolveStyle(storage: storage, range: range)
            let current = (storage.attribute(RichTextKeys.listLevel, at: range.location, effectiveRange: nil) as? NSNumber)?.intValue ?? 0
            let next = min(max(current + delta, 0), 3)
            storage.addAttribute(RichTextKeys.listLevel, value: NSNumber(value: next), range: range)
            storage.addAttribute(.paragraphStyle,
                                 value: RichTextBridge.paragraphStyle(for: style, level: next),
                                 range: range)
        }
        storage.endEditing()
        commitChanges()
        refreshCurrentStyle()
        return true
    }

    private func applyBlockStyle(_ style: RichTextBlockStyle,
                                 to storage: NSTextStorage,
                                 in tv: NSTextView,
                                 level: Int) {
        let ranges = paragraphRanges(in: storage, range: tv.selectedRange())
        guard !ranges.isEmpty else { return }
        let baseSize = RichTextBridge.baseFontSize(for: style)

        storage.beginEditing()
        for range in ranges {
            storage.addAttribute(RichTextKeys.blockStyle, value: style.rawValue, range: range)
            storage.addAttribute(RichTextKeys.baseFontSize, value: NSNumber(value: Double(baseSize)), range: range)
            storage.addAttribute(RichTextKeys.listLevel, value: NSNumber(value: level), range: range)
            storage.addAttribute(.paragraphStyle,
                                 value: RichTextBridge.paragraphStyle(for: style, level: level),
                                 range: range)

            for runRange in runRanges(in: storage, range: range) {
                let font = storage.attribute(.font, at: runRange.location, effectiveRange: nil) as? NSFont
                    ?? NSFont.systemFont(ofSize: baseSize)
                let bold = RichTextBridge.isHeading(style) ? true : RichTextBridge.isBold(font)
                let italic = RichTextBridge.isItalic(font)
                let newFont = style == .code
                    ? RichTextBridge.monoFont(size: baseSize, bold: bold)
                    : RichTextBridge.font(size: baseSize, bold: bold, italic: italic)
                storage.addAttribute(.font, value: newFont, range: runRange)
            }
        }
        storage.endEditing()
        commitChanges()

        // 让后续输入继承新的段落样式
        if let last = ranges.last, last.length > 0 {
            let sample = storage.attribute(.font, at: last.location, effectiveRange: nil) as? NSFont
            if let sample {
                var typing = tv.typingAttributes
                typing[.font] = sample
                typing[.paragraphStyle] = storage.attribute(.paragraphStyle, at: last.location, effectiveRange: nil)
                tv.typingAttributes = typing
            }
        }

        refreshCurrentStyle()
    }

    // MARK: - 链接

    /// 插入链接：无选区时插入 URL 文本，有选区时套用到选中文本
    public func insertLink(url: URL, displayText: String? = nil) {
        guard let tv = textView else { return }
        let selection = tv.selectedRange()
        let linkColor = NSColor(calibratedRed: 0.10, green: 0.45, blue: 0.85, alpha: 1)

        if selection.length == 0 {
            let text = displayText ?? url.absoluteString
            let attributed = NSMutableAttributedString(string: text)
            let range = NSRange(location: 0, length: attributed.length)
            attributed.addAttribute(.link, value: url, range: range)
            attributed.addAttribute(.foregroundColor, value: linkColor, range: range)
            attributed.addAttribute(.underlineStyle, value: NSUnderlineStyle.single.rawValue, range: range)
            tv.insertText(attributed, replacementRange: selection)
            return
        }

        tv.textStorage?.addAttribute(.link, value: url, range: selection)
        tv.textStorage?.addAttribute(.foregroundColor, value: linkColor, range: selection)
        tv.textStorage?.addAttribute(.underlineStyle, value: NSUnderlineStyle.single.rawValue, range: selection)
        commitChanges()
    }

    // MARK: - 图片

    /// 导入本地图片并插入到当前选区
    public func insertImage(from sourceURL: URL) throws {
        guard let tv = textView else { return }
        let (attachment, relativePath) = try makeAttachment(from: sourceURL)

        let holder = NSMutableAttributedString(attachment: attachment)
        holder.addAttribute(RichTextKeys.imagePath, value: relativePath, range: NSRange(location: 0, length: holder.length))

        let selection = tv.selectedRange()
        tv.insertText(holder, replacementRange: selection)
    }

    /// 粘贴含图片路径的文本：路径位置替换成内联图片，其余文字原样保留。
    /// - Returns: 实际插入的图片数量。
    @discardableResult
    public func insertTextWithImagePaths(_ text: String) throws -> Int {
        guard let tv = textView else { return 0 }
        let segments = ImagePathScanner.segments(in: text)
        guard segments.contains(where: { $0.isImage }) else { return 0 }

        let result = NSMutableAttributedString()
        var inserted = 0

        for segment in segments {
            if let url = segment.imageURL {
                let (attachment, relativePath) = try makeAttachment(from: url)
                let holder = NSMutableAttributedString(attachment: attachment)
                holder.addAttribute(RichTextKeys.imagePath,
                                    value: relativePath,
                                    range: NSRange(location: 0, length: holder.length))
                result.append(holder)
                inserted += 1
            } else {
                result.append(NSAttributedString(string: segment.text))
            }
        }

        let selection = tv.selectedRange()
        tv.insertText(result, replacementRange: selection)
        return inserted
    }

    /// 把图片导入附件目录并生成附件，同时返回相对路径
    private func makeAttachment(from sourceURL: URL) throws -> (NSTextAttachment, String) {
        guard let attachments, let noteID else {
            throw AttachmentError.copyFailed("附件仓库未就绪")
        }
        let relativePath = try attachments.importImage(at: sourceURL, noteID: noteID)

        guard let image = NSImage(contentsOf: attachments.absoluteURL(for: relativePath)) else {
            throw AttachmentError.copyFailed("无法读取图片内容")
        }

        let attachment = NSTextAttachment()
        attachment.image = image
        var width = image.size.width
        var height = image.size.height
        if width > RichTextBridge.maxImageWidth {
            let ratio = RichTextBridge.maxImageWidth / width
            width = RichTextBridge.maxImageWidth
            height = height * ratio
        }
        attachment.bounds = NSRect(x: 0, y: 0, width: width, height: height)

        return (attachment, relativePath)
    }

    // MARK: - 工具

    private func applyToSelection(_ transform: @escaping (NSFont) -> NSFont?) {
        guard let tv = textView, let storage = tv.textStorage else { return }
        let selection = tv.selectedRange()

        if selection.length == 0 {
            var typing = tv.typingAttributes
            let font = typing[.font] as? NSFont ?? NSFont.systemFont(ofSize: RichTextBridge.defaultFontSize)
            typing[.font] = transform(font) ?? font
            tv.typingAttributes = typing
            return
        }

        storage.beginEditing()
        for range in runRanges(in: storage, range: selection) {
            let font = storage.attribute(.font, at: range.location, effectiveRange: nil) as? NSFont
                ?? NSFont.systemFont(ofSize: RichTextBridge.defaultFontSize)
            storage.addAttribute(.font, value: transform(font) ?? font, range: range)
        }
        storage.endEditing()
        commitChanges()
    }

    private func toggleAttribute(_ key: NSAttributedString.Key, value: Int) {
        guard let tv = textView, let storage = tv.textStorage else { return }
        let selection = tv.selectedRange()

        if selection.length == 0 {
            var typing = tv.typingAttributes
            let current = typing[key] as? Int ?? 0
            typing[key] = current == 0 ? value : 0
            tv.typingAttributes = typing
            return
        }

        let ranges = runRanges(in: storage, range: selection)
        let allOn = ranges.allSatisfy { (storage.attribute(key, at: $0.location, effectiveRange: nil) as? Int ?? 0) != 0 }
        storage.beginEditing()
        for range in ranges {
            storage.addAttribute(key, value: allOn ? 0 : value, range: range)
        }
        storage.endEditing()
        commitChanges()
    }

    private func runRanges(in storage: NSTextStorage, range: NSRange) -> [NSRange] {
        var ranges: [NSRange] = []
        storage.enumerateAttribute(.font,
                                  in: range,
                                  options: [],
                                  using: { _, runRange, _ in
            ranges.append(runRange)
        })
        // 列表编号 / 圆点是视图层前缀，不参与字体、颜色等行内格式命令
        let content = ranges.filter { runRange in
            guard runRange.location < storage.length else { return false }
            return storage.attribute(RichTextKeys.listMarker,
                                     at: runRange.location,
                                     effectiveRange: nil) == nil
        }
        return content.isEmpty ? (ranges.isEmpty ? [range] : []) : content
    }

    /// 选区覆盖到的所有段落
    private func paragraphRanges(in storage: NSTextStorage, range: NSRange) -> [NSRange] {
        let nsString = storage.string as NSString
        guard nsString.length > 0 else { return [] }

        var result: [NSRange] = []
        let limit = NSMaxRange(range)
        var location = min(range.location, nsString.length - 1)

        while true {
            let paragraph = nsString.paragraphRange(for: NSRange(location: location, length: 0))
            result.append(paragraph)
            let next = NSMaxRange(paragraph)
            if next >= limit || next >= nsString.length { break }
            location = next
        }
        return result
    }

    private func resolveStyle(storage: NSTextStorage, range: NSRange) -> RichTextBlockStyle {
        guard range.location < storage.length else { return .paragraph }
        if let raw = storage.attribute(RichTextKeys.blockStyle, at: range.location, effectiveRange: nil) as? String,
           let style = RichTextBlockStyle(rawValue: raw) {
            return style
        }
        if let paragraph = storage.attribute(.paragraphStyle, at: range.location, effectiveRange: nil) as? NSParagraphStyle,
           let list = paragraph.textLists.first {
            return list.markerFormat == .decimal ? .orderedItem : .bulletItem
        }
        return .paragraph
    }
}
