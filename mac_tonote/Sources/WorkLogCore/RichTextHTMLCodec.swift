import Foundation

/// 受控 HTML 编解码。
///
/// 只允许一组固定标签与属性，保证：
/// 1. macOS / Android 解析结果一致；
/// 2. 正文里不会出现 `file:///Attachment.tiff` 这类临时引用；
/// 3. 图片只以相对路径 `attachments/<note-uuid>/<image-uuid>.ext` 落盘。
public enum RichTextHTMLCodec {

    // MARK: - 编码

    public static func encode(_ document: RichTextDocument) -> String {
        var html = ""
        let blocks = document.blocks
        var index = 0

        while index < blocks.count {
            let block = blocks[index]

            switch block.style {
            case .bulletItem, .orderedItem:
                // 连续的同类列表项合并成一个 <ul> / <ol>
                var items: [[RichTextRun]] = []
                var cursor = index
                while cursor < blocks.count, blocks[cursor].style == block.style {
                    items.append(blocks[cursor].runs)
                    cursor += 1
                }
                let tag = block.style == .bulletItem ? "ul" : "ol"
                html += "<\(tag)>"
                for item in items {
                    html += "<li>\(encodeInline(item))</li>"
                }
                html += "</\(tag)>"
                index = cursor
            default:
                html += encodeBlock(block)
                index += 1
            }

            // 块之间用换行分隔，最后一块不加尾随换行，保证「编码→解码→编码」稳定
            if index < blocks.count {
                html += "\n"
            }
        }
        return html
    }

    private static func encodeBlock(_ block: RichTextBlock) -> String {
        // 标题本身即为加粗，段落内再套 <strong> 属于语义冗余
        let suppressBold = block.style == .heading1 || block.style == .heading2 || block.style == .heading3
        let inner = encodeInline(block.runs, suppressBold: suppressBold)
        switch block.style {
        case .paragraph: return "<p>\(inner)</p>"
        case .heading1:  return "<h1>\(inner)</h1>"
        case .heading2:  return "<h2>\(inner)</h2>"
        case .heading3:  return "<h3>\(inner)</h3>"
        case .quote:     return "<blockquote>\(inner)</blockquote>"
        case .code:      return "<pre>\(inner)</pre>"
        case .bulletItem, .orderedItem: return "<p>\(inner)</p>"
        }
    }

    private static func encodeInline(_ runs: [RichTextRun], suppressBold: Bool = false) -> String {
        runs.map { encodeRun($0, suppressBold: suppressBold) }.joined()
    }

    private static func encodeRun(_ run: RichTextRun, suppressBold: Bool = false) -> String {
        if let image = run.image {
            var img = "<img src=\"\(escapeAttribute(image.path))\""
            if let width = image.width { img += " width=\"\(formatNumber(width))\"" }
            if let height = image.height { img += " height=\"\(formatNumber(height))\"" }
            img += ">"
            return img
        }

        var inner = escapeText(run.text)
        if let link = run.link, !link.isEmpty {
            inner = "<a href=\"\(escapeAttribute(link))\">\(inner)</a>"
        }
        if let size = run.fontSize {
            inner = "<span style=\"font-size:\(formatNumber(size))px\">\(inner)</span>"
        }
        if let color = run.colorHex {
            inner = "<span style=\"color:\(escapeAttribute(color))\">\(inner)</span>"
        }
        if run.isStrike { inner = "<s>\(inner)</s>" }
        if run.isUnderline { inner = "<u>\(inner)</u>" }
        if run.isItalic { inner = "<em>\(inner)</em>" }
        if run.isBold && !suppressBold { inner = "<strong>\(inner)</strong>" }
        return inner
    }

    private static func formatNumber(_ value: Double) -> String {
        if value == value.rounded() {
            return String(Int(value))
        }
        return String(format: "%.1f", value)
    }

    static func escapeText(_ text: String) -> String {
        text
            .replacingOccurrences(of: "&", with: "&amp;")
            .replacingOccurrences(of: "<", with: "&lt;")
            .replacingOccurrences(of: ">", with: "&gt;")
    }

    static func escapeAttribute(_ text: String) -> String {
        escapeText(text)
            .replacingOccurrences(of: "\"", with: "&quot;")
            .replacingOccurrences(of: "'", with: "&#39;")
    }

    // MARK: - 解码

    public static func decode(_ html: String) -> RichTextDocument {
        let nodes = HTMLTreeBuilder.parse(html)
        var blocks: [RichTextBlock] = []
        for node in nodes {
            blocks.append(contentsOf: blocksFrom(node: node))
        }
        if blocks.isEmpty {
            blocks = [RichTextBlock(style: .paragraph, runs: [])]
        }
        return RichTextDocument(blocks: blocks)
    }

    private static func blocksFrom(node: HTMLNode) -> [RichTextBlock] {
        switch node {
        case .text(let text):
            let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !trimmed.isEmpty else { return [] }
            return [RichTextBlock(style: .paragraph, runs: [RichTextRun(text: text)])]
        case .element(let tag, _, let children):
            switch tag {
            case "p":
                return paragraphBlocks(from: children)
            case "h1":
                return [RichTextBlock(style: .heading1, runs: inlineRuns(children, context: InlineContext()))]
            case "h2":
                return [RichTextBlock(style: .heading2, runs: inlineRuns(children, context: InlineContext()))]
            case "h3", "h4", "h5", "h6":
                return [RichTextBlock(style: .heading3, runs: inlineRuns(children, context: InlineContext()))]
            case "blockquote":
                return [RichTextBlock(style: .quote, runs: inlineRuns(children, context: InlineContext()))]
            case "pre":
                return [RichTextBlock(style: .code, runs: [RichTextRun(text: rawText(children))])]
            case "ul":
                return listBlocks(children, style: .bulletItem)
            case "ol":
                return listBlocks(children, style: .orderedItem)
            case "br":
                return []
            default:
                var result: [RichTextBlock] = []
                for child in children {
                    result.append(contentsOf: blocksFrom(node: child))
                }
                if result.isEmpty {
                    let trimmed = rawText(children).trimmingCharacters(in: .whitespacesAndNewlines)
                    if !trimmed.isEmpty {
                        result = [RichTextBlock(style: .paragraph, runs: [RichTextRun(text: trimmed)])]
                    }
                }
                return result
            }
        }
    }

    private static func listBlocks(_ children: [HTMLNode], style: RichTextBlockStyle) -> [RichTextBlock] {
        var result: [RichTextBlock] = []
        for child in children {
            guard case .element(let tag, _, let grandChildren) = child else { continue }
            guard tag == "li" else {
                result.append(contentsOf: blocksFrom(node: child))
                continue
            }
            result.append(RichTextBlock(style: style, runs: inlineRuns(grandChildren, context: InlineContext())))
        }
        return result
    }

    /// 段落内的 `<br>` 会拆成多个段落
    private static func paragraphBlocks(from nodes: [HTMLNode]) -> [RichTextBlock] {
        var segments: [[HTMLNode]] = [[]]
        for node in nodes {
            if case .element(let tag, _, _) = node, tag == "br" {
                segments.append([])
            } else {
                segments[segments.count - 1].append(node)
            }
        }
        return segments.map {
            RichTextBlock(style: .paragraph, runs: inlineRuns($0, context: InlineContext()))
        }
    }

    private struct InlineContext {
        var isBold = false
        var isItalic = false
        var isUnderline = false
        var isStrike = false
        var fontSize: Double?
        var colorHex: String?
        var link: String?
    }

    private static func inlineRuns(_ nodes: [HTMLNode], context: InlineContext) -> [RichTextRun] {
        var runs: [RichTextRun] = []

        for node in nodes {
            switch node {
            case .text(let text):
                guard !text.isEmpty else { continue }
                runs.append(RichTextRun(text: text,
                                       isBold: context.isBold,
                                       isItalic: context.isItalic,
                                       isUnderline: context.isUnderline,
                                       isStrike: context.isStrike,
                                       fontSize: context.fontSize,
                                       colorHex: context.colorHex,
                                       link: context.link))
            case .element(let tag, let attrs, let children):
                switch tag {
                case "strong", "b":
                    var ctx = context; ctx.isBold = true
                    runs.append(contentsOf: inlineRuns(children, context: ctx))
                case "em", "i":
                    var ctx = context; ctx.isItalic = true
                    runs.append(contentsOf: inlineRuns(children, context: ctx))
                case "u", "ins":
                    var ctx = context; ctx.isUnderline = true
                    runs.append(contentsOf: inlineRuns(children, context: ctx))
                case "s", "strike", "del":
                    var ctx = context; ctx.isStrike = true
                    runs.append(contentsOf: inlineRuns(children, context: ctx))
                case "span":
                    var ctx = context
                    applyStyle(attrs["style"], to: &ctx)
                    runs.append(contentsOf: inlineRuns(children, context: ctx))
                case "a":
                    var ctx = context
                    if let href = attrs["href"], !href.isEmpty { ctx.link = href }
                    runs.append(contentsOf: inlineRuns(children, context: ctx))
                case "img":
                    var image = RichTextImage(path: attrs["src"] ?? "")
                    if let width = attrs["width"], let value = Double(width) { image.width = value }
                    if let height = attrs["height"], let value = Double(height) { image.height = value }
                    runs.append(RichTextRun(image: image))
                case "br":
                    runs.append(RichTextRun(text: "\n"))
                default:
                    runs.append(contentsOf: inlineRuns(children, context: context))
                }
            }
        }
        return runs
    }

    private static func applyStyle(_ style: String?, to context: inout InlineContext) {
        guard let style, !style.isEmpty else { return }
        for piece in style.split(separator: ";") {
            let parts = piece.split(separator: ":", maxSplits: 1, omittingEmptySubsequences: true)
            guard parts.count == 2 else { continue }
            let key = parts[0].trimmingCharacters(in: .whitespaces).lowercased()
            let value = parts[1].trimmingCharacters(in: .whitespaces)
            switch key {
            case "font-size":
                let numeric = value.replacingOccurrences(of: "px", with: "")
                if let size = Double(numeric), size > 0 { context.fontSize = size }
            case "color":
                if !value.isEmpty { context.colorHex = value }
            default:
                break
            }
        }
    }

    private static func rawText(_ nodes: [HTMLNode]) -> String {
        var text = ""
        for node in nodes {
            switch node {
            case .text(let value): text += value
            case .element(_, _, let children): text += rawText(children)
            }
        }
        return text
    }
}

// MARK: - 极简 HTML 解析器

private indirect enum HTMLNode {
    case text(String)
    case element(tag: String, attrs: [String: String], children: [HTMLNode])
}

/// 只解析受控标签集的小型解析器：足够还原自己写出的 HTML，不追求通用 HTML5。
private enum HTMLTreeBuilder {

    private struct Frame {
        let tag: String
        let attrs: [String: String]
        var children: [HTMLNode]
    }

    static func parse(_ html: String) -> [HTMLNode] {
        var roots: [HTMLNode] = []
        var stack: [Frame] = []

        func append(_ node: HTMLNode) {
            if stack.isEmpty {
                roots.append(node)
            } else {
                stack[stack.count - 1].children.append(node)
            }
        }

        let characters = Array(html)
        var index = 0
        var textBuffer = ""

        func flushText() {
            if !textBuffer.isEmpty {
                append(.text(decodeEntities(textBuffer)))
                textBuffer = ""
            }
        }

        while index < characters.count {
            guard characters[index] == "<" else {
                textBuffer.append(characters[index])
                index += 1
                continue
            }

            // 注释
            if characters.count - index >= 4,
               characters[index + 1] == "!",
               characters[index + 2] == "-",
               characters[index + 3] == "-" {
                flushText()
                var cursor = index + 4
                while cursor + 2 < characters.count {
                    if characters[cursor] == "-" && characters[cursor + 1] == "-" && characters[cursor + 2] == ">" {
                        cursor += 3
                        break
                    }
                    cursor += 1
                }
                index = cursor
                continue
            }

            // 结束标签
            if characters.count - index >= 2, characters[index + 1] == "/" {
                var cursor = index + 2
                var tag = ""
                while cursor < characters.count, characters[cursor] != ">" {
                    tag.append(characters[cursor])
                    cursor += 1
                }
                guard cursor < characters.count else {
                    textBuffer.append(contentsOf: characters[index...])
                    break
                }
                flushText()
                let name = tag.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
                if let position = stack.lastIndex(where: { $0.tag == name }) {
                    while stack.count > position {
                        let frame = stack.removeLast()
                        append(.element(tag: frame.tag, attrs: frame.attrs, children: frame.children))
                    }
                }
                index = cursor + 1
                continue
            }

            // 开始标签
            var cursor = index + 1
            var tag = ""
            while cursor < characters.count,
                  !characters[cursor].isWhitespace,
                  characters[cursor] != ">",
                  characters[cursor] != "/" {
                tag.append(characters[cursor])
                cursor += 1
            }
            var attributes = ""
            while cursor < characters.count, characters[cursor] != ">" {
                attributes.append(characters[cursor])
                cursor += 1
            }
            guard cursor < characters.count else {
                textBuffer.append(contentsOf: characters[index...])
                break
            }
            flushText()
            let selfClosing = attributes.trimmingCharacters(in: .whitespaces).hasSuffix("/")
            let name = tag.lowercased()
            let parsed = parseAttributes(attributes)
            if selfClosing || isVoidTag(name) {
                append(.element(tag: name, attrs: parsed, children: []))
            } else {
                stack.append(Frame(tag: name, attrs: parsed, children: []))
            }
            index = cursor + 1
        }

        flushText()
        while !stack.isEmpty {
            let frame = stack.removeLast()
            append(.element(tag: frame.tag, attrs: frame.attrs, children: frame.children))
        }
        return roots
    }

    private static func isVoidTag(_ tag: String) -> Bool {
        ["br", "img", "hr", "meta", "link", "input", "area", "base", "col", "source"].contains(tag)
    }

    private static func parseAttributes(_ raw: String) -> [String: String] {
        var result: [String: String] = [:]
        // 逐个匹配 name="value" / name='value' / name=value
        let pattern = "([A-Za-z_:][-A-Za-z0-9_:.]*)\\s*=\\s*(\"[^\"]*\"|'[^']*'|[^\\s\"'>]+)"
        guard let regex = try? NSRegularExpression(pattern: pattern) else { return result }

        let ns = raw as NSString
        let range = NSRange(location: 0, length: ns.length)
        regex.enumerateMatches(in: raw, options: [], range: range) { match, _, _ in
            guard let match, match.numberOfRanges >= 3 else { return }
            let key = ns.substring(with: match.range(at: 1)).trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
            var value = ns.substring(with: match.range(at: 2)).trimmingCharacters(in: .whitespacesAndNewlines)
            if value.count >= 2, (value.hasPrefix("\"") && value.hasSuffix("\"")) || (value.hasPrefix("'") && value.hasSuffix("'")) {
                value = String(value.dropFirst().dropLast())
            }
            guard !key.isEmpty else { return }
            let decoded = decodeEntities(value)
            if key == "style" {
                // style 允许多次出现，合并而不是覆盖，避免字号与颜色互相丢失
                if let existing = result[key], !existing.isEmpty {
                    result[key] = existing + ";" + decoded
                } else {
                    result[key] = decoded
                }
            } else if result[key] == nil {
                // 其余属性与浏览器一致：重复出现时以首个为准
                result[key] = decoded
            }
        }
        return result
    }

    static func decodeEntities(_ text: String) -> String {
        var result = text
        let map = [
            "&amp;": "&",
            "&lt;": "<",
            "&gt;": ">",
            "&quot;": "\"",
            "&#39;": "'",
            "&apos;": "'",
            "&nbsp;": " "
        ]
        for (entity, replacement) in map {
            result = result.replacingOccurrences(of: entity, with: replacement)
        }
        return result
    }
}
