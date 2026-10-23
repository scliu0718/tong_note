import Foundation

/// 旧 Markdown 正文的兼容转换器。
///
/// 旧笔记里存在 `![](图片地址)` 这类占位符（不是真实附件），
/// 这里按纯文本处理，绝不会把占位符误当成真实图片写进附件目录。
public enum MarkdownConverter {

    /// Markdown → 富文本文档（用于旧笔记首次在富文本编辑器中打开时渲染）
    public static func toDocument(_ markdown: String) -> RichTextDocument {
        var blocks: [RichTextBlock] = []
        let lines = markdown.components(separatedBy: .newlines)

        var listBuffer: [(RichTextBlockStyle, [RichTextRun])] = []

        func flushList() {
            guard !listBuffer.isEmpty else { return }
            for entry in listBuffer {
                blocks.append(RichTextBlock(style: entry.0, runs: entry.1))
            }
            listBuffer.removeAll()
        }

        var paragraphBuffer: [RichTextRun] = []

        func flushParagraph() {
            guard !paragraphBuffer.isEmpty else { return }
            blocks.append(RichTextBlock(style: .paragraph, runs: paragraphBuffer))
            paragraphBuffer.removeAll()
        }

        for rawLine in lines {
            let line = rawLine
            let trimmed = line.trimmingCharacters(in: .whitespaces)

            if trimmed.isEmpty {
                flushParagraph()
                flushList()
                continue
            }

            // 代码块围栏
            if trimmed.hasPrefix("```") {
                flushParagraph()
                flushList()
                continue
            }

            // 标题
            if let heading = headingRuns(trimmed) {
                flushParagraph()
                flushList()
                blocks.append(heading)
                continue
            }

            // 无序列表
            if trimmed.hasPrefix("- ") || trimmed.hasPrefix("* ") || trimmed.hasPrefix("+ ") {
                flushParagraph()
                let content = String(trimmed.dropFirst(2))
                listBuffer.append((.bulletItem, inlineRuns(content)))
                continue
            }

            // 有序列表
            if let content = orderedListContent(trimmed) {
                flushParagraph()
                listBuffer.append((.orderedItem, inlineRuns(content)))
                continue
            }

            // 引用
            if trimmed.hasPrefix("> ") {
                flushParagraph()
                flushList()
                blocks.append(RichTextBlock(style: .quote, runs: inlineRuns(String(trimmed.dropFirst(2)))))
                continue
            }

            flushList()
            paragraphBuffer.append(contentsOf: inlineRuns(line))
        }

        flushParagraph()
        flushList()

        if blocks.isEmpty {
            blocks = [RichTextBlock(style: .paragraph, runs: [])]
        }
        return RichTextDocument(blocks: blocks)
    }

    /// 富文本文档 → 纯文本（列表摘要、字数统计、搜索用）
    public static func plainText(fromDocument document: RichTextDocument) -> String {
        document.blocks.map { block in
            let text = block.runs.map { run in
                if run.image != nil { return "" }
                return run.text
            }.joined()
            switch block.style {
            case .bulletItem: return "• " + text
            case .orderedItem: return text
            default: return text
            }
        }.joined(separator: "\n")
    }

    /// 正文 → 纯文本，自动识别 Markdown / HTML 两种格式
    public static func plainText(from body: String, format: NoteContentFormat) -> String {
        switch format {
        case .markdown:
            return plainText(fromMarkdown: body)
        case .richText:
            return RichTextHTMLCodec.decode(body).plainText
        }
    }

    /// 旧 Markdown 去掉标记后的纯文本
    public static func plainText(fromMarkdown markdown: String) -> String {
        var lines: [String] = []
        for rawLine in markdown.components(separatedBy: .newlines) {
            var line = rawLine
            for marker in ["### ", "## ", "# ", "> ", "- ", "* ", "+ "] where line.hasPrefix(marker) {
                line = String(line.dropFirst(marker.count))
                break
            }
            if let range = line.range(of: "^\\d+\\.\\s*", options: .regularExpression) {
                line = String(line[range.upperBound...])
            }
            line = line
                .replacingOccurrences(of: "**", with: "")
                .replacingOccurrences(of: "*", with: "")
                .replacingOccurrences(of: "`", with: "")
                .replacingOccurrences(of: "~~", with: "")
            lines.append(line)
        }
        return lines.joined(separator: "\n")
    }

    // MARK: - 私有解析

    private static func headingRuns(_ line: String) -> RichTextBlock? {
        let styles: [(String, RichTextBlockStyle)] = [
            ("### ", .heading3),
            ("## ", .heading2),
            ("# ", .heading1)
        ]
        for (prefix, style) in styles where line.hasPrefix(prefix) {
            let content = String(line.dropFirst(prefix.count))
            return RichTextBlock(style: style, runs: inlineRuns(content))
        }
        return nil
    }

    private static func orderedListContent(_ line: String) -> String? {
        guard let range = line.range(of: "^\\d+\\.\\s+", options: .regularExpression) else { return nil }
        return String(line[range.upperBound...])
    }

    /// 行内标记解析：**加粗**、*斜体*、`代码`、~~删除~~、[文字](链接)、![图片](路径)
    private static func inlineRuns(_ text: String) -> [RichTextRun] {
        let chars = Array(text)
        var runs: [RichTextRun] = []
        var buffer = ""
        var index = 0

        func flush() {
            if !buffer.isEmpty {
                runs.append(RichTextRun(text: buffer))
                buffer = ""
            }
        }

        /// 从 start 开始查找 delimiter，返回结束位置（不含 delimiter）
        func findClose(_ delimiter: String, from start: Int) -> Int? {
            let d = Array(delimiter)
            var i = start
            while i + d.count <= chars.count {
                var matched = true
                for k in 0..<d.count where chars[i + k] != d[k] {
                    matched = false
                    break
                }
                if matched { return i }
                i += 1
            }
            return nil
        }

        func string(from start: Int, to end: Int) -> String {
            guard start < end else { return "" }
            return String(chars[start..<end])
        }

        while index < chars.count {
            let rest = string(from: index, to: chars.count)

            // **加粗**
            if rest.hasPrefix("**"), let close = findClose("**", from: index + 2) {
                flush()
                runs.append(RichTextRun(text: string(from: index + 2, to: close), isBold: true))
                index = close + 2
                continue
            }

            // ~~删除~~
            if rest.hasPrefix("~~"), let close = findClose("~~", from: index + 2) {
                flush()
                runs.append(RichTextRun(text: string(from: index + 2, to: close), isStrike: true))
                index = close + 2
                continue
            }

            // `代码`
            if rest.hasPrefix("`"), let close = findClose("`", from: index + 1) {
                flush()
                runs.append(RichTextRun(text: string(from: index + 1, to: close), fontSize: 13))
                index = close + 1
                continue
            }

            // ![图片](路径)
            if rest.hasPrefix("!["),
               let altClose = findClose("]", from: index + 2),
               altClose + 1 < chars.count, chars[altClose + 1] == "(",
               let urlClose = findClose(")", from: altClose + 2) {
                flush()
                let path = string(from: altClose + 2, to: urlClose)
                // 仅当路径形如 attachments/... 时才当作真实图片附件
                if path.hasPrefix("attachments/") {
                    runs.append(RichTextRun(image: RichTextImage(path: path)))
                } else {
                    let alt = string(from: index + 2, to: altClose)
                    runs.append(RichTextRun(text: "![\(alt)](\(path))"))
                }
                index = urlClose + 1
                continue
            }

            // [文字](链接)
            if rest.hasPrefix("["),
               let labelClose = findClose("]", from: index + 1),
               labelClose + 1 < chars.count, chars[labelClose + 1] == "(",
               let urlClose = findClose(")", from: labelClose + 2) {
                flush()
                let label = string(from: index + 1, to: labelClose)
                let url = string(from: labelClose + 2, to: urlClose)
                runs.append(RichTextRun(text: label, link: url))
                index = urlClose + 1
                continue
            }

            // *斜体*
            if rest.hasPrefix("*"), let close = findClose("*", from: index + 1), close > index + 1 {
                flush()
                runs.append(RichTextRun(text: string(from: index + 1, to: close), isItalic: true))
                index = close + 1
                continue
            }

            buffer.append(chars[index])
            index += 1
        }

        flush()
        return runs.isEmpty ? [RichTextRun(text: "")] : runs
    }
}
