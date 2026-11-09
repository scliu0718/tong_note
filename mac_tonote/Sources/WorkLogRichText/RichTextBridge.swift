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
    /// 列表项的可见标记字符（NSNumber(true)）。带此属性的字符是视图层前缀，
    /// 序列化回文档时必须跳过，避免把「1.」「•」写进正文 HTML。
    static let listMarker = NSAttributedString.Key("WorkLogListMarker")
    /// 列表缩进层级（NSNumber，Int），0 为第一级
    static let listLevel = NSAttributedString.Key("WorkLogListLevel")
}

/// RichTextDocument（跨平台模型）↔ NSAttributedString（AppKit）双向转换。
///
/// 关键约束：正文里只允许出现 `attachments/...` 相对路径，
/// 绝不允许写入 Cocoa HTML Writer 生成的 `file:///Attachment.tiff` 临时引用。
enum RichTextBridge {

    /// 正文字号 14pt；标题按 18 / 24 / 32 / 40 递进
    static let defaultFontSize: CGFloat = 14
    static let defaultTextColor = NSColor(srgbRed: 0.28, green: 0.28, blue: 0.29, alpha: 1)
    static let maxImageWidth: CGFloat = 480
    /// 列表编号 / 圆点的蓝色，与工具栏样式菜单的强调色一致
    static let listMarkerColor = NSColor(srgbRed: 0.18, green: 0.486, blue: 0.965, alpha: 1)

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

    /// 一级 40 / 二级 32 / 三级 24 / 四级 18 / 正文 12。
    /// H5、H6 仅为兼容旧笔记保留，字号并入四级以下，菜单不再提供入口。
    static func baseFontSize(for style: RichTextBlockStyle) -> CGFloat {
        switch style {
        case .heading1: return 40
        case .heading2: return 32
        case .heading3: return 24
        case .heading4: return 18
        case .heading5: return 16
        case .heading6: return 14
        case .code: return defaultFontSize
        default: return defaultFontSize
        }
    }

    static func isHeading(_ style: RichTextBlockStyle) -> Bool {
        switch style {
        case .heading1, .heading2, .heading3, .heading4, .heading5, .heading6:
            return true
        default:
            return false
        }
    }

    static func paragraphStyle(for style: RichTextBlockStyle,
                               textList: NSTextList? = nil,
                               level: Int = 0) -> NSParagraphStyle {
        let paragraph = NSMutableParagraphStyle()
        switch style {
        case .heading1:
            paragraph.paragraphSpacingBefore = 18
            paragraph.paragraphSpacing = 8
            paragraph.lineSpacing = 3
        case .heading2:
            paragraph.paragraphSpacingBefore = 15
            paragraph.paragraphSpacing = 6
            paragraph.lineSpacing = 2
        case .heading3:
            paragraph.paragraphSpacingBefore = 12
            paragraph.paragraphSpacing = 5
            paragraph.lineSpacing = 2
        case .heading4, .heading5, .heading6:
            paragraph.paragraphSpacingBefore = 10
            paragraph.paragraphSpacing = 4
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
            // 标记写在段落开头（marker + \t）；第一行从 markerIndent 起，
            // \t 跳到 textIndent，换行后的续行也对齐到 textIndent。
            // 这样层级切换时编号 / 圆点本身也会右移，而不是只有正文动。
            let markerIndent = listIndent(for: level)
            let textIndent = markerIndent + listTextOffset
            paragraph.firstLineHeadIndent = markerIndent
            paragraph.headIndent = textIndent
            paragraph.paragraphSpacingBefore = 1
            paragraph.paragraphSpacing = 1
            // 主制表位在标记后 15pt；长编号（iii. 等）超出时落到备用位，
            // 免得跳到系统默认的 28pt 整数倍、间距反而更大
            paragraph.tabStops = [
                NSTextTab(textAlignment: .left, location: textIndent),
                NSTextTab(textAlignment: .left, location: textIndent + 14)
            ]
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

    // MARK: - 列表标记（视图层前缀，不写入正文）

    /// 列表标记自身的起点：每深一层右移 32pt，编号 / 圆点与正文一起缩进
    static func listIndent(for level: Int) -> CGFloat {
        4 + CGFloat(min(max(level, 0), 7)) * 32
    }

    /// 标记到正文的距离；制表位与续行缩进都用它（按要求由 30 缩小一半）
    static let listTextOffset: CGFloat = 15

    /// 无序列表层级符号：父级实心、子级空心，再深继续交替。
    /// 两者都是 bullet 系列、用同一字号，视觉尺寸一致；
    /// 不用全角大圆 ○ —— 那会比实心点大一圈，看着不协调。
    static func bulletSymbol(for level: Int) -> String {
        let clamped = min(max(level, 0), 7)
        return clamped % 2 == 0 ? "•" : "◦"
    }

    /// 第一级 1...n；存在上一层级时切到 a...z；再深一级用 i...，之后循环。
    static func orderedLabel(_ index: Int, level: Int) -> String {
        let n = max(1, index)
        switch ((level % 3) + 3) % 3 {
        case 1:
            var value = n
            var letters = ""
            while value > 0 {
                value -= 1
                if let scalar = UnicodeScalar(UInt32(97 + value % 26)) {
                    letters = String(Character(scalar)) + letters
                }
                value /= 26
            }
            return letters.isEmpty ? "a" : letters
        case 2:
            return lowerRoman(n)
        default:
            return "\(n)"
        }
    }

    private static func lowerRoman(_ value: Int) -> String {
        let table: [(Int, String)] = [
            (1000, "m"), (900, "cm"), (500, "d"), (400, "cd"),
            (100, "c"), (90, "xc"), (50, "l"), (40, "xl"),
            (10, "x"), (9, "ix"), (5, "v"), (4, "iv"), (1, "i")
        ]
        var remaining = value
        var result = ""
        for (amount, symbol) in table {
            while remaining >= amount {
                result += symbol
                remaining -= amount
            }
        }
        return result.isEmpty ? "i" : result
    }

    /// marker 文本尾部的 \t 用于把正文推到制表位；非列表返回 nil。
    static func markerString(for style: RichTextBlockStyle, level: Int, index: Int) -> String? {
        switch style {
        case .orderedItem: return orderedLabel(index, level: level) + ".\t"
        case .bulletItem: return bulletSymbol(for: level) + "\t"
        default: return nil
        }
    }

    /// 按 (段落样式, 层级) 序列为每个段落算出应有的 marker。
    ///
    /// 编号规则（飞书式）：
    /// - 连续有序项共用一个计数，按层级分别编号；
    /// - 中间只隔 1 段正文（典型的「退格退出列表 → 手动再设回来」）时编号顺延上方；
    /// - 隔了多段正文、标题、引用、代码块或无序列表时视为新列表，重新从 1 开始。
    static func markerPlan(for styles: [(style: RichTextBlockStyle, level: Int)]) -> [String?] {
        var counters = Array(repeating: 0, count: 8)
        var plan: [String?] = []
        var paragraphGap = 0

        for entry in styles {
            let level = min(max(entry.level, 0), 7)
            switch entry.style {
            case .orderedItem:
                if paragraphGap > 1 {
                    counters = Array(repeating: 0, count: counters.count)
                }
                paragraphGap = 0
                if level + 1 < counters.count {
                    for i in (level + 1)..<counters.count { counters[i] = 0 }
                }
                counters[level] += 1
                plan.append(markerString(for: .orderedItem, level: level, index: counters[level]))
            case .bulletItem:
                counters = Array(repeating: 0, count: counters.count)
                paragraphGap = 0
                plan.append(markerString(for: .bulletItem, level: level, index: 0))
            case .paragraph:
                paragraphGap += 1
                plan.append(nil)
            default:
                counters = Array(repeating: 0, count: counters.count)
                paragraphGap = 0
                plan.append(nil)
            }
        }
        return plan
    }

    static func markerAttributes(for style: RichTextBlockStyle,
                                 level: Int,
                                 baseSize: CGFloat) -> [NSAttributedString.Key: Any] {
        let size: CGFloat
        switch style {
        case .orderedItem: size = baseSize + 1
        case .bulletItem:
            // 实心与空心用同一个字号（比正文放大 1.5 倍），大小一致；
            // 放大后空心点中间的孔也更容易辨认
            size = max(9, baseSize - 2) * 1.5
        default: size = baseSize
        }
        return [
            .font: font(size: size, bold: true, italic: false),
            .foregroundColor: listMarkerColor,
            .paragraphStyle: paragraphStyle(for: style, level: level),
            RichTextKeys.listMarker: NSNumber(value: true),
            RichTextKeys.listLevel: NSNumber(value: level),
            RichTextKeys.blockStyle: style.rawValue,
            RichTextKeys.baseFontSize: NSNumber(value: Double(baseSize))
        ]
    }

    /// 生成标记前缀。
    ///
    /// 标签字符用蓝色粗体，但**末尾的 \t 必须是正文属性**：
    /// 光标紧贴 \t 后面输入时，NSTextView 会从 \t 派生 typingAttributes，
    /// 若 \t 还是蓝色粗体，列表后面的内容就会跟着变蓝加粗。
    static func markerAttributedString(_ text: String,
                                       style: RichTextBlockStyle,
                                       level: Int,
                                       baseSize: CGFloat) -> NSMutableAttributedString {
        let result = NSMutableAttributedString(string: text)
        guard result.length > 0 else { return result }
        result.setAttributes(markerAttributes(for: style, level: level, baseSize: baseSize),
                             range: NSRange(location: 0, length: result.length))
        let tabRange = NSRange(location: result.length - 1, length: 1)
        result.addAttribute(.font,
                            value: font(size: baseSize, bold: false, italic: false),
                            range: tabRange)
        result.addAttribute(.foregroundColor, value: defaultTextColor, range: tabRange)
        return result
    }

    private static func markerNeedsRefresh(_ storage: NSMutableAttributedString,
                                           range: NSRange,
                                           expected: [NSAttributedString.Key: Any]) -> Bool {
        let current = storage.attributes(at: range.location, effectiveRange: nil)
        guard let color = current[.foregroundColor] as? NSColor,
              let wantedColor = expected[.foregroundColor] as? NSColor,
              color.isEqual(wantedColor) else { return true }
        guard let font = current[.font] as? NSFont,
              let wantedFont = expected[.font] as? NSFont,
              abs(font.pointSize - wantedFont.pointSize) < 0.01 else { return true }
        guard let level = current[RichTextKeys.listLevel] as? NSNumber,
              let wantedLevel = expected[RichTextKeys.listLevel] as? NSNumber,
              level.intValue == wantedLevel.intValue else { return true }
        guard let style = current[.paragraphStyle] as? NSParagraphStyle,
              let wantedStyle = expected[.paragraphStyle] as? NSParagraphStyle,
              abs(style.headIndent - wantedStyle.headIndent) < 0.01 else { return true }
        // 末尾 \t 若是旧版的蓝色粗体，也要重建，否则后续输入仍会继承错误样式
        let baseSize = (expected[RichTextKeys.baseFontSize] as? NSNumber)
            .map { CGFloat(truncating: $0) } ?? defaultFontSize
        let tabIndex = NSMaxRange(range) - 1
        if tabIndex >= range.location, tabIndex < storage.length {
            guard let tabColor = storage.attribute(.foregroundColor, at: tabIndex, effectiveRange: nil) as? NSColor,
                  tabColor.isEqual(defaultTextColor),
                  let tabFont = storage.attribute(.font, at: tabIndex, effectiveRange: nil) as? NSFont,
                  abs(tabFont.pointSize - baseSize) < 0.01 else { return true }
        }
        return false
    }

    /// 段落开头属于标记前缀的范围。
    ///
    /// 不能用 `attribute(_:at:effectiveRange:)`：一旦新输入的字误继承了
    /// listMarker 属性，effectiveRange 会一路延伸到整段，normalize 会把正文
    /// 一起当成前缀替换掉 —— 正是子级 a. / b. 后面「输不进内容」的原因。
    /// 这里按结构扫描：连续带标记属性、遇到第一个 \t 结束，且最多 8 个字符。
    static func markerRange(in storage: NSAttributedString, paragraph: NSRange) -> NSRange? {
        let text = storage.string as NSString
        guard paragraph.length > 0, text.length > 0 else { return nil }
        let start = min(max(paragraph.location, 0), text.length - 1)
        let end = min(NSMaxRange(paragraph), text.length, start + 8)
        guard storage.attribute(RichTextKeys.listMarker, at: start, effectiveRange: nil) != nil else {
            return nil
        }
        var cursor = start
        while cursor < end {
            guard storage.attribute(RichTextKeys.listMarker, at: cursor, effectiveRange: nil) != nil else {
                break
            }
            let ch = text.character(at: cursor)
            if ch == 9 { // \t 是标记的最后一个字符
                cursor += 1
                break
            }
            // 正文（中文等）就算误继承了标记属性，也不算前缀的一部分
            guard isMarkerCharacter(ch) else { break }
            cursor += 1
        }
        return cursor > start ? NSRange(location: start, length: cursor - start) : nil
    }

    /// 合法标记字符：1. / a. / iii. 与 • / ◦（兼容 ○），其余一律视为正文
    private static func isMarkerCharacter(_ ch: unichar) -> Bool {
        switch ch {
        case 0x2022, 0x25CB, 0x25E6, 0x2E: return true  // • ○ ◦ .
        case 48...57: return true                        // 0-9
        case 65...90: return true                        // A-Z
        case 97...122: return true                       // a-z
        default: return false
        }
    }

    /// 让每个列表段落开头的可见标记与样式、层级、序号保持一致。
    /// 标记字符带 `RichTextKeys.listMarker`，`document(from:)` 会跳过，不会写进 HTML。
    /// - Returns: 是否改动了 textStorage（调用方需自行屏蔽递归的 textDidChange）。
    @discardableResult
    static func normalizeListMarkers(in storage: NSMutableAttributedString) -> Bool {
        let text = storage.string as NSString
        guard text.length > 0 else { return false }

        struct Segment {
            let contentRange: NSRange
            let style: RichTextBlockStyle
            let level: Int
            let markerRange: NSRange?
            let desired: String?
        }

        var styles: [(style: RichTextBlockStyle, level: Int)] = []
        var segments: [Segment] = []
        var location = 0

        while location < text.length {
            let paragraph = text.paragraphRange(for: NSRange(location: location, length: 0))
            let probe = min(paragraph.location, max(0, text.length - 1))
            let separator = separatorLength(in: text, range: paragraph)
            let content = NSRange(location: paragraph.location,
                                  length: max(0, paragraph.length - separator))
            let style: RichTextBlockStyle
            if let raw = storage.attribute(RichTextKeys.blockStyle, at: probe, effectiveRange: nil) as? String,
               let resolved = RichTextBlockStyle(rawValue: raw) {
                style = resolved
            } else if let paragraphStyle = storage.attribute(.paragraphStyle, at: probe, effectiveRange: nil) as? NSParagraphStyle,
                      let list = paragraphStyle.textLists.first {
                style = list.markerFormat == .decimal ? .orderedItem : .bulletItem
            } else {
                style = .paragraph
            }
            let level = (storage.attribute(RichTextKeys.listLevel, at: probe, effectiveRange: nil) as? NSNumber)?.intValue ?? 0
            let clamped = min(max(level, 0), 7)

            let markerRange = RichTextBridge.markerRange(in: storage, paragraph: content)

            styles.append((style, clamped))
            segments.append(Segment(contentRange: content, style: style, level: clamped,
                                    markerRange: markerRange, desired: nil))

            let next = NSMaxRange(paragraph)
            if next <= location { break }
            location = next
        }

        let plan = markerPlan(for: styles)
        var changed = false

        // 逆序改动：前面的插入/删除不会让后续 NSRange 失效
        for (index, segment) in segments.enumerated().reversed() {
            let desired = plan[index]
            let desiredText = desired ?? ""
            let existingText = segment.markerRange.map { text.substring(with: $0) } ?? ""

            // 空列表项回车后，用户新输入的字可能误继承 listMarker 属性 → 清属性但保留文字
            let bodyStart = segment.contentRange.location + (segment.markerRange?.length ?? 0)
            let bodyEnd = NSMaxRange(segment.contentRange)
            if bodyStart < bodyEnd {
                let bodyRange = NSRange(location: bodyStart, length: bodyEnd - bodyStart)
                var stray: [NSRange] = []
                storage.enumerateAttribute(RichTextKeys.listMarker, in: bodyRange, options: []) { value, range, _ in
                    if value != nil { stray.append(range) }
                }
                if !stray.isEmpty {
                    storage.beginEditing()
                    for range in stray { storage.removeAttribute(RichTextKeys.listMarker, range: range) }
                    storage.endEditing()
                    changed = true
                }
            }

            let attributes = desiredText.isEmpty
                ? nil
                : markerAttributes(for: segment.style,
                                   level: segment.level,
                                   baseSize: baseFontSize(for: segment.style))

            let markerContent = desiredText.isEmpty
                ? NSMutableAttributedString()
                : markerAttributedString(desiredText,
                                         style: segment.style,
                                         level: segment.level,
                                         baseSize: baseFontSize(for: segment.style))

            if existingText != desiredText {
                let target = segment.markerRange
                    ?? NSRange(location: segment.contentRange.location, length: 0)
                storage.beginEditing()
                storage.replaceCharacters(in: target, with: markerContent)
                storage.endEditing()
                changed = true
            } else if let range = segment.markerRange, !desiredText.isEmpty, let attributes,
                      markerNeedsRefresh(storage, range: range, expected: attributes) {
                // 文本一致但属性过期：整体重建，保证末尾 \t 也是正文属性
                storage.beginEditing()
                storage.replaceCharacters(in: range, with: markerContent)
                storage.endEditing()
                changed = true
            }
        }

        return changed
    }

    // MARK: - Document → AttributedString

    static func attributedString(from document: RichTextDocument,
                                 attachments: AttachmentStore) -> NSAttributedString {
        let result = NSMutableAttributedString()
        let blocks = document.blocks

        let plan = markerPlan(for: blocks.map { (style: $0.style, level: $0.listLevel) })

        for (index, block) in blocks.enumerated() {
            let level = min(max(block.listLevel, 0), 7)
            let baseSize = baseFontSize(for: block.style)
            let paragraph = NSMutableAttributedString()
            let paragraphAttributes: [NSAttributedString.Key: Any] = [
                .font: block.style == .code
                    ? monoFont(size: baseSize, bold: false)
                    : font(size: baseSize, bold: isHeading(block.style), italic: false),
                .foregroundColor: defaultTextColor,
                .paragraphStyle: paragraphStyle(for: block.style, level: level),
                RichTextKeys.blockStyle: block.style.rawValue,
                RichTextKeys.baseFontSize: NSNumber(value: Double(baseSize)),
                RichTextKeys.listLevel: NSNumber(value: level)
            ]

            // 视图层标记前缀：蓝色序号 / 小圆点，序列化回文档时会被跳过
            if let marker = plan[index] {
                paragraph.append(markerAttributedString(marker,
                                                        style: block.style,
                                                        level: level,
                                                        baseSize: baseSize))
            }

            // 列表正文曾误继承标记的蓝色粗体（历史 bug 产物），载入时恢复为正文样式
            let markerHex = hexString(from: listMarkerColor).uppercased()
            let isListBlock = block.style == .bulletItem || block.style == .orderedItem

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
                let polluted = isListBlock && run.isBold
                    && run.colorHex?.uppercased() == markerHex
                let bold = polluted ? false : (run.isBold || isHeading(block.style))
                let italic = run.isItalic
                attributes[.font] = block.style == .code
                    ? monoFont(size: runSize, bold: bold)
                    : font(size: runSize, bold: bold, italic: italic)
                if let hex = run.colorHex, !polluted, let color = color(fromHex: hex) {
                    attributes[.foregroundColor] = color
                }
                if let hex = run.backgroundHex, let color = color(fromHex: hex) {
                    attributes[.backgroundColor] = color
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
        let listLevel = (attributed.attribute(RichTextKeys.listLevel, at: safeLocation, effectiveRange: nil) as? NSNumber)?
            .intValue ?? 0

        var runs: [RichTextRun] = []
        let nsString = attributed.string as NSString

        if range.length > 0 {
            attributed.enumerateAttributes(in: range, options: []) { attributes, runRange, _ in
                guard NSMaxRange(runRange) <= attributed.length else { return }
                // 列表编号 / 圆点只是视图层前缀，绝不写进正文
                guard attributes[RichTextKeys.listMarker] == nil else { return }

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

                var backgroundHex: String?
                if let color = attributes[.backgroundColor] as? NSColor,
                   color.alphaComponent > 0.01 {
                    backgroundHex = hexString(from: color)
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
                                        backgroundHex: backgroundHex,
                                        link: link))
            }
        }

        return RichTextBlock(style: style, runs: runs, listLevel: listLevel)
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

        // 兜底：粗体大字号视为标题（阈值与 40/32/24/18 的字号阶梯对齐）
        if let font = attributed.attribute(.font, at: location, effectiveRange: nil) as? NSFont, isBold(font) {
            let size = font.pointSize
            if size >= 34 { return .heading1 }
            if size >= 27 { return .heading2 }
            if size >= 21 { return .heading3 }
            if size >= 17 { return .heading4 }
        }
        return .paragraph
    }
}
