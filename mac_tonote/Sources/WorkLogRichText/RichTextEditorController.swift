import AppKit
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

    public init() {}

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
    }

    // MARK: - 段落与列表

    /// 应用段落样式（正文/标题/引用/代码）
    public func applyBlockStyle(_ style: RichTextBlockStyle) {
        guard let tv = textView, let storage = tv.textStorage else { return }
        applyBlockStyle(style, to: storage, in: tv)
    }

    /// 切换无序列表 / 有序列表
    public func toggleList(_ style: RichTextBlockStyle) {
        guard let tv = textView, let storage = tv.textStorage else { return }
        let ranges = paragraphRanges(in: storage, range: tv.selectedRange())
        guard !ranges.isEmpty else { return }
        let allMatch = ranges.allSatisfy { resolveStyle(storage: storage, range: $0) == style }
        applyBlockStyle(allMatch ? .paragraph : style, to: storage, in: tv)
    }

    private func applyBlockStyle(_ style: RichTextBlockStyle,
                                 to storage: NSTextStorage,
                                 in tv: NSTextView) {
        let ranges = paragraphRanges(in: storage, range: tv.selectedRange())
        guard !ranges.isEmpty else { return }
        // 同一批段落共享同一个 NSTextList，序号才能连续
        let textList = RichTextBridge.makeTextList(for: style)
        let baseSize = RichTextBridge.baseFontSize(for: style)

        storage.beginEditing()
        for range in ranges {
            storage.addAttribute(RichTextKeys.blockStyle, value: style.rawValue, range: range)
            storage.addAttribute(RichTextKeys.baseFontSize, value: NSNumber(value: Double(baseSize)), range: range)
            storage.addAttribute(.paragraphStyle,
                                 value: RichTextBridge.paragraphStyle(for: style, textList: textList),
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
    }

    private func runRanges(in storage: NSTextStorage, range: NSRange) -> [NSRange] {
        var ranges: [NSRange] = []
        storage.enumerateAttribute(.font,
                                  in: range,
                                  options: [],
                                  using: { _, runRange, _ in
            ranges.append(runRange)
        })
        return ranges.isEmpty ? [range] : ranges
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
