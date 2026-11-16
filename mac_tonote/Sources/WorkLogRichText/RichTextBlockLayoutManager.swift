import AppKit
import WorkLogCore

/// 在 TextKit 1 里补画「引用 / 代码块」的块级装饰。
///
/// `NSParagraphStyle` 只能改缩进，左侧竖线、整块底色、圆角必须自己画；
/// 之前 Mac 端只设了 `headIndent`，所以引用 / 代码块看起来「只有 tab」。
/// 规格逐项对齐 Android WebView `buildEditorHtml` 里的 CSS：
///
///   blockquote { border-left:3px solid #1a9e5c; padding:4px 10px;
///                margin:8px 0; color:#86868b; background:#f7faf8; }
///   pre        { background:#f5f5f7; padding:10px; border-radius:6px;
///                font-family:monospace; white-space:pre-wrap; }
///
/// 只读取 `RichTextKeys.blockStyle`，**不修改 NSTextStorage**，
/// 因此这些装饰不会被序列化进 HTML，也不会污染 Android / Windows 端。
final class RichTextBlockLayoutManager: NSLayoutManager {

    // Android CSS 的三个色值
    private static let quoteBarColor = NSColor(srgbRed: 26.0 / 255.0,
                                               green: 158.0 / 255.0,
                                               blue: 92.0 / 255.0, alpha: 1)
    private static let quoteFillColor = NSColor(srgbRed: 247.0 / 255.0,
                                                green: 250.0 / 255.0,
                                                blue: 248.0 / 255.0, alpha: 1)
    private static let codeFillColor = NSColor(srgbRed: 245.0 / 255.0,
                                               green: 245.0 / 255.0,
                                               blue: 247.0 / 255.0, alpha: 1)

    private static let barWidth: CGFloat = 3
    private static let quotePaddingY: CGFloat = 4
    private static let codePaddingY: CGFloat = 10
    private static let cornerRadius: CGFloat = 6
    /// 底色块比行碎片区左右各多出一点，视觉上更接近块级元素
    private static let horizontalBleed: CGFloat = 4

    override func drawBackground(forGlyphRange glyphsToShow: NSRange, at origin: NSPoint) {
        // 先画块级底色，再让 super 画用户手动设的文字高亮、选区与表格单元格底色，
        // 顺序反了会把用户高亮盖掉。
        drawBlockDecorations(forGlyphRange: glyphsToShow, origin: origin)
        super.drawBackground(forGlyphRange: glyphsToShow, at: origin)
    }

    private func drawBlockDecorations(forGlyphRange glyphsToShow: NSRange, origin: NSPoint) {
        guard glyphsToShow.length > 0,
              let storage = textStorage,
              let container = textContainers.first else { return }
        let charRange = characterRange(forGlyphRange: glyphsToShow, actualGlyphRange: nil)
        guard charRange.length > 0, NSMaxRange(charRange) <= storage.length else { return }

        let whole = NSRange(location: 0, length: storage.length)
        let string = storage.string as NSString
        var painted: [NSRange] = []
        var cursor = charRange.location
        let limit = NSMaxRange(charRange)

        NSGraphicsContext.saveGraphicsState()
        defer { NSGraphicsContext.restoreGraphicsState() }

        // 用 longestEffectiveRange 拿到整块的完整范围：即使本轮只重绘可见的几行，
        // 盒子也按整块几何画，滚动时不会在视口边缘出现半个圆角。AppKit 会按 dirtyRect 裁剪。
        while cursor < limit {
            var effective = NSRange(location: NSNotFound, length: 0)
            let value = storage.attribute(RichTextKeys.blockStyle,
                                          at: cursor,
                                          longestEffectiveRange: &effective,
                                          in: whole)
            guard effective.location != NSNotFound else { break }
            let next = max(NSMaxRange(effective), cursor + 1)

            if let raw = value as? String,
               let style = RichTextBlockStyle(rawValue: raw),
               style == .quote || style == .code,
               !painted.contains(effective),
               !RichTextTableLayout.containsTable(storage, at: effective.location) {
                painted.append(effective)
                drawDecoration(style: style,
                               charRange: effective,
                               string: string,
                               container: container,
                               origin: origin)
            }
            cursor = next
        }
    }

    private func drawDecoration(style: RichTextBlockStyle,
                                charRange: NSRange,
                                string: NSString,
                                container: NSTextContainer,
                                origin: NSPoint) {
        // 段尾换行会额外产生一条空白行碎片，先裁掉，避免盒子底部多出一行
        var range = charRange
        while range.length > 0 {
            let last = NSMaxRange(range) - 1
            guard last >= 0, last < string.length else { break }
            let ch = string.character(at: last)
            if ch == 10 || ch == 13 || ch == 9 { range.length -= 1 } else { break }
        }
        guard range.length > 0 else { return }
        let glyphs = glyphRange(forCharacterRange: range, actualCharacterRange: nil)
        guard glyphs.length > 0 else { return }

        // 一次拿到整块所有行碎片的上下边界（容器坐标系）
        let used = boundingRect(forGlyphRange: glyphs, in: container)
        guard !used.isEmpty, used.height > 0 else { return }

        let pad = container.lineFragmentPadding
        let bleed = Self.horizontalBleed
        let x = origin.x + pad - bleed
        let width = max(20, container.size.width - 2 * pad + 2 * bleed)
        let paddingY = style == .quote ? Self.quotePaddingY : Self.codePaddingY
        let rect = NSRect(x: x,
                          y: origin.y + used.minY - paddingY,
                          width: width,
                          height: used.height + 2 * paddingY)

        if style == .quote {
            Self.quoteFillColor.setFill()
            NSBezierPath(rect: rect).fill()
            Self.quoteBarColor.setFill()
            NSBezierPath(rect: NSRect(x: rect.minX, y: rect.minY,
                                      width: Self.barWidth, height: rect.height)).fill()
        } else {
            Self.codeFillColor.setFill()
            NSBezierPath(roundedRect: rect,
                         xRadius: Self.cornerRadius,
                         yRadius: Self.cornerRadius).fill()
        }
    }
}
