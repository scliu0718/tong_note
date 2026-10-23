import AppKit
import WorkLogCore

/// 富文本编辑器使用的自定义属性键。
enum RichTextKeys {
    /// 段落样式（RichTextBlockStyle.rawValue）
    static let blockStyle = NSAttributedString.Key("WorkLogBlockStyle")
    /// 段落默认字号（NSNumber，pt）
    static let baseFontSize = NSAttributedString.Key("WorkLogBaseFontSize")
    /// 图片附件相对路径（String）
    static let imagePath = NSAttributedString.Key("WorkLogImagePath")
}

/// RichTextDocument（跨平台模型）↔ NSAttributedString（AppKit）双向转换。
///
/// 关键约束：正文里只允许出现 `attachments/...` 相对路径，
/// 绝不允许写入 Cocoa HTML Writer 生成的 `file:///Attachment.tiff` 临时引用。
enum RichTextBridge {

    static let defaultFontSize: CGFloat = 15
    static let defaultTextColor = NSColor(srgbRed: 0.28, green: 0.28, blue: 0.29, alpha: 1)
    static let maxImageWidth: CGFloat = 480

    // MARK: - 字体与颜色

    static func font(size: CGFloat, bold: Bool, italic: Bool) -> NSFont {
        var mask = NSFontTraitMask()
        if bold { mask.insert(.boldFontMask) }
        if italic { mask.insert(.italicFontMask) }
        let base = NSFont.systemFont(ofSize: size)
        guard !mask.isEmpty else { return base }
        return NSFontManager.shared.convert(base, toHaveTrait: mask)
    }

    static func monoFont(size: CGFloat, bold: Bool) -> NSFont {
        NSFont.monospacedSystemFont(ofSize: size, weight: bold ? .bold : .regular)
    }

    static func isBold(_ font: NSFont) -> Bool {
        font.fontDescriptor.symbolicTraits.contains(.bold)
    }

    static func isItalic(_ font: NSFont) -> Bool {
        font.fontDescriptor.symbolicTraits.contains(.italic)
    }

    static func hexString(from color: NSColor) -> String {
        guard let srgb = color.usingColorSpace(.sRGB) else { return "#1D1D1F" }
        let r = Int((srgb.redComponent * 255).rounded())
        let g = Int((srgb.greenComponent * 255).rounded())
        let b = Int((srgb.blueComponent * 255).rounded())
        return String(format: "#%02X%02X%02X", r, g, b)
    }

    static func color(fromHex hex: String) -> NSColor? {
        var value = hex.trimmingCharacters(in: .whitespaces)
        if value.hasPrefix("#") { value.removeFirst() }
        guard value.count == 6, let number = Int(value, radix: 16) else { return nil }
        // 必须用 sRGB 构造：calibrated 色彩空间转换后会偏移，十六进制无法精确往返
        return NSColor(
            srgbRed: CGFloat((number >> 16) & 0xFF) / 255,
            green: CGFloat((number >> 8) & 0xFF) / 255,
            blue: CGFloat(number & 0xFF) / 255,
            alpha: 1
        )
    }

    // MARK: - 段落样式

    static func baseFontSize(for style: RichTextBlockStyle) -> CGFloat {
        switch style {
        case .heading1: return 26
        case .heading2: return 20
        case .heading3: return 17
        case .code: return 13
        default: return defaultFontSize
        }
    }

    static func isHeading(_ style: RichTextBlockStyle) -> Bool {
        style == .heading1 || style == .heading2 || style == .heading3
    }

    static func paragraphStyle(for style: RichTextBlockStyle, textList: NSTextList?) -> NSParagraphStyle {
        let paragraph = NSMutableParagraphStyle()
        switch style {
        case .heading1:
            paragraph.paragraphSpacingBefore = 14
            paragraph.paragraphSpacing = 6
            paragraph.lineSpacing = 2
        case .heading2:
            paragraph.paragraphSpacingBefore = 12
            paragraph.paragraphSpacing = 4
            paragraph.lineSpacing = 2
        case .heading3:
            paragraph.paragraphSpacingBefore = 10
            paragraph.paragraphSpacing = 3
            paragraph.lineSpacing = 1
        case .quote:
            paragraph.headIndent = 20
            paragraph.firstLineHeadIndent = 20
            paragraph.paragraphSpacingBefore = 4
            paragraph.paragraphSpacing = 4
        case .code:
            paragraph.headIndent = 12
            paragraph.firstLineHeadIndent = 0
            paragraph.paragraphSpacingBefore = 2
            paragraph.paragraphSpacing = 2
        case .bulletItem, .orderedItem:
            paragraph.headIndent = 26
            paragraph.firstLineHeadIndent = 0
            paragraph.paragraphSpacingBefore = 1
            paragraph.paragraphSpacing = 1
            paragraph.tabStops = [NSTextTab(textAlignment: .left, location: 26)]
        case .paragraph:
            paragraph.lineSpacing = 4
            paragraph.paragraphSpacing = 2
        }
        if let textList {
            paragraph.textLists = [textList]
        }
        return paragraph
    }

    static func makeTextList(for style: RichTextBlockStyle) -> NSTextList? {
        switch style {
        case .bulletItem:
            return NSTextList(markerFormat: .disc, options: 0)
        case .orderedItem:
            return NSTextList(markerFormat: .decimal, options: 0)
        default:
            return nil
        }
    }

    // MARK: - Document → AttributedString

    static func attributedString(from document: RichTextDocument,
                                 attachments: AttachmentStore) -> NSAttributedString {
        let result = NSMutableAttributedString()
        let blocks = document.blocks

        var sharedBullet: NSTextList?
        var sharedOrdered: NSTextList?
        var previousStyle: RichTextBlockStyle?

        for (index, block) in blocks.enumerated() {
            // 连续的同类列表项共享同一个 NSTextList，编号才能连续
            switch block.style {
            case .bulletItem:
                if previousStyle != .bulletItem { sharedBullet = makeTextList(for: .bulletItem) }
                sharedOrdered = nil
            case .orderedItem:
                if previousStyle != .orderedItem { sharedOrdered = makeTextList(for: .orderedItem) }
                sharedBullet = nil
            default:
                sharedBullet = nil
                sharedOrdered = nil
            }
            let list = block.style == .bulletItem ? sharedBullet : (block.style == .orderedItem ? sharedOrdered : nil)

            let baseSize = baseFontSize(for: block.style)
            let paragraph = NSMutableAttributedString()
            let paragraphAttributes: [NSAttributedString.Key: Any] = [
                .font: block.style == .code
                    ? monoFont(size: baseSize, bold: false)
                    : font(size: baseSize, bold: isHeading(block.style), italic: false),
                .foregroundColor: defaultTextColor,
                .paragraphStyle: paragraphStyle(for: block.style, textList: list),
                RichTextKeys.blockStyle: block.style.rawValue,
                RichTextKeys.baseFontSize: NSNumber(value: Double(baseSize))
            ]

            for run in block.runs {
                if let image = run.image {
                    let attachment = NSTextAttachment()
                    let absolute = attachments.absoluteURL(for: image.path)
                    if let loaded = NSImage(contentsOf: absolute) {
                        attachment.image = loaded
                        attachment.bounds = fittedBounds(image: loaded, declared: image)
                    } else {
                        // 附件缺失：显示占位框，避免整篇正文被一个坏图带崩
                        let placeholder = NSImage(size: NSSize(width: 160, height: 90))
                        attachment.image = placeholder
                        attachment.bounds = NSRect(x: 0, y: 0, width: 160, height: 90)
                    }
                    let holder = NSMutableAttributedString(attachment: attachment)
                    holder.addAttributes(paragraphAttributes, range: NSRange(location: 0, length: holder.length))
                    holder.addAttribute(RichTextKeys.imagePath, value: image.path, range: NSRange(location: 0, length: holder.length))
                    paragraph.append(holder)
                    continue
                }

                let runSize = run.fontSize.map { CGFloat($0) } ?? baseSize
                var attributes = paragraphAttributes
                let bold = run.isBold || isHeading(block.style)
                let italic = run.isItalic
                attributes[.font] = block.style == .code
                    ? monoFont(size: runSize, bold: bold)
                    : font(size: runSize, bold: bold, italic: italic)
                if let hex = run.colorHex, let color = color(fromHex: hex) {
                    attributes[.foregroundColor] = color
                }
                if run.isUnderline {
                    attributes[.underlineStyle] = NSUnderlineStyle.single.rawValue
                }
                if run.isStrike {
                    attributes[.strikethroughStyle] = NSUnderlineStyle.single.rawValue
                }
                if let link = run.link, let url = URL(string: link) {
                    attributes[.link] = url
                }
                paragraph.append(NSAttributedString(string: run.text, attributes: attributes))
            }

            result.append(paragraph)
            if index < blocks.count - 1 {
                result.append(NSAttributedString(string: "\n", attributes: paragraphAttributes))
            }
            previousStyle = block.style
        }

        return result
    }

    private static func fittedBounds(image: NSImage, declared: RichTextImage) -> NSRect {
        let size = image.size
        guard size.width > 0, size.height > 0 else {
            return NSRect(x: 0, y: 0, width: 160, height: 90)
        }
        // 以文件真实尺寸为准：正文里记录的声明尺寸可能是历史污染值
        // （例如曾经读图失败退化成 160×90 占位图），优先采信会形成自我循环。
        var width = size.width
        var height = size.height
        if width > maxImageWidth {
            let ratio = maxImageWidth / width
            width = maxImageWidth
            height = height * ratio
        }
        return NSRect(x: 0, y: 0, width: width, height: height)
    }

    // MARK: - AttributedString → Document

    static func document(from attributed: NSAttributedString) -> RichTextDocument {
        let nsString = attributed.string as NSString
        var blocks: [RichTextBlock] = []

        var location = 0
        while location < nsString.length {
            let paragraph = nsString.paragraphRange(for: NSRange(location: location, length: 0))
            var content = paragraph
            content.length = max(0, paragraph.length - separatorLength(in: nsString, range: paragraph))
            blocks.append(block(from: attributed, range: content))
            location = NSMaxRange(paragraph)
        }

        // 末尾若只有一个空段落（结尾多敲了一次回车），不额外产生一个空块
        if let last = blocks.last, last.runs.isEmpty, blocks.count > 1 {
            blocks.removeLast()
        }

        if blocks.isEmpty {
            blocks = [RichTextBlock(style: .paragraph, runs: [])]
        }
        return RichTextDocument(blocks: blocks)
    }

    /// 段落尾部换行符长度（\r\n / \n / \r）
    static func separatorLength(in nsString: NSString, range: NSRange) -> Int {
        let end = NSMaxRange(range)
        if end >= 2, nsString.substring(with: NSRange(location: end - 2, length: 2)) == "\r\n" { return 2 }
        if end >= 1 {
            let last = nsString.substring(with: NSRange(location: end - 1, length: 1))
            if last == "\n" || last == "\r" { return 1 }
        }
        return 0
    }

    private static func block(from attributed: NSAttributedString, range: NSRange) -> RichTextBlock {
        let safeLocation = min(range.location, max(0, attributed.length - 1))
        let style = resolveStyle(attributed: attributed, location: safeLocation, hasContent: range.length > 0)
        let baseSize = (attributed.attribute(RichTextKeys.baseFontSize, at: safeLocation, effectiveRange: nil) as? NSNumber)
            .map { CGFloat(truncating: $0) } ?? defaultFontSize

        var runs: [RichTextRun] = []
        let nsString = attributed.string as NSString

        if range.length > 0 {
            attributed.enumerateAttributes(in: range, options: []) { attributes, runRange, _ in
                guard NSMaxRange(runRange) <= attributed.length else { return }

                if let attachment = attributes[.attachment] as? NSTextAttachment {
                    var image = RichTextImage(path: attributes[RichTextKeys.imagePath] as? String ?? "")
                    let bounds = attachment.bounds
                    if bounds.width > 0 { image.width = Double(bounds.width) }
                    if bounds.height > 0 { image.height = Double(bounds.height) }
                    runs.append(RichTextRun(image: image))
                    return
                }

                let text = nsString.substring(with: runRange)
                guard !text.isEmpty else { return }

                let font = attributes[.font] as? NSFont
                let size = font?.pointSize ?? baseSize
                let bold = font.map(isBold) ?? false
                let italic = font.map(isItalic) ?? false
                let underline = (attributes[.underlineStyle] as? Int ?? 0) != 0
                let strike = (attributes[.strikethroughStyle] as? Int ?? 0) != 0

                var colorHex: String?
                if let color = attributes[.foregroundColor] as? NSColor {
                    let hex = hexString(from: color)
                    if hex != hexString(from: defaultTextColor) { colorHex = hex }
                }

                var link: String?
                if let url = attributes[.link] as? URL {
                    link = url.absoluteString
                } else if let string = attributes[.link] as? String {
                    link = string
                }

                runs.append(RichTextRun(text: text,
                                        isBold: bold,
                                        isItalic: italic,
                                        isUnderline: underline,
                                        isStrike: strike,
                                        fontSize: abs(size - baseSize) > 0.01 ? Double(size) : nil,
                                        colorHex: colorHex,
                                        link: link))
            }
        }

        return RichTextBlock(style: style, runs: runs)
    }

    private static func resolveStyle(attributed: NSAttributedString,
                                     location: Int,
                                     hasContent: Bool) -> RichTextBlockStyle {
        if hasContent,
           let raw = attributed.attribute(RichTextKeys.blockStyle, at: location, effectiveRange: nil) as? String,
           let style = RichTextBlockStyle(rawValue: raw) {
            return style
        }

        // 兜底：段落样式里的列表标记
        if let paragraph = attributed.attribute(.paragraphStyle, at: location, effectiveRange: nil) as? NSParagraphStyle,
           let list = paragraph.textLists.first {
            return list.markerFormat == .decimal ? .orderedItem : .bulletItem
        }

        // 兜底：粗体大字号视为标题
        if let font = attributed.attribute(.font, at: location, effectiveRange: nil) as? NSFont, isBold(font) {
            let size = font.pointSize
            if size >= 23 { return .heading1 }
            if size >= 18 { return .heading2 }
            if size >= 16 { return .heading3 }
        }
        return .paragraph
    }
}
