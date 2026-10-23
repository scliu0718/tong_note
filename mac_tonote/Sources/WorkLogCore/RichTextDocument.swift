import Foundation

/// 正文格式版本。旧笔记默认 markdown，编辑后升级为 richText（受控 HTML）。
public enum NoteContentFormat: String, Codable, Sendable {
    case markdown
    case richText
}

/// 正文与格式的原子载体：两者必须一起写入，避免出现「HTML 正文 + markdown 格式」的中间态。
public struct NoteContent: Equatable, Sendable {
    public var body: String
    public var format: NoteContentFormat

    public init(body: String = "", format: NoteContentFormat = .markdown) {
        self.body = body
        self.format = format
    }
}

/// 段落级样式
public enum RichTextBlockStyle: String, Codable, CaseIterable, Sendable {
    case paragraph
    case heading1
    case heading2
    case heading3
    case quote
    case code
    case bulletItem
    case orderedItem
}

/// 内嵌图片（相对路径，跨平台同步友好）
public struct RichTextImage: Equatable, Sendable {
    /// 相对路径，形如 `attachments/<note-uuid>/<image-uuid>.png`
    public var path: String
    public var width: Double?
    public var height: Double?

    public init(path: String, width: Double? = nil, height: Double? = nil) {
        self.path = path
        self.width = width
        self.height = height
    }
}

/// 段内的一段连续格式文本
public struct RichTextRun: Equatable, Sendable {
    public var text: String
    public var isBold: Bool
    public var isItalic: Bool
    public var isUnderline: Bool
    public var isStrike: Bool
    /// 显式字号（pt）。nil 表示跟随段落默认字号。
    public var fontSize: Double?
    /// 显式文字颜色（#RRGGBB）。nil 表示跟随段落默认颜色。
    public var colorHex: String?
    public var link: String?
    public var image: RichTextImage?

    public init(text: String = "",
                isBold: Bool = false,
                isItalic: Bool = false,
                isUnderline: Bool = false,
                isStrike: Bool = false,
                fontSize: Double? = nil,
                colorHex: String? = nil,
                link: String? = nil,
                image: RichTextImage? = nil) {
        self.text = text
        self.isBold = isBold
        self.isItalic = isItalic
        self.isUnderline = isUnderline
        self.isStrike = isStrike
        self.fontSize = fontSize
        self.colorHex = colorHex
        self.link = link
        self.image = image
    }
}

/// 一个段落 = 一行。段落样式 + 段内格式片段。
public struct RichTextBlock: Equatable, Sendable {
    public var style: RichTextBlockStyle
    public var runs: [RichTextRun]

    public init(style: RichTextBlockStyle = .paragraph, runs: [RichTextRun] = []) {
        self.style = style
        self.runs = runs
    }

    public var plainText: String {
        runs.map { $0.image != nil ? "" : $0.text }.joined()
    }
}

/// 完整富文本文档
public struct RichTextDocument: Equatable, Sendable {
    public var blocks: [RichTextBlock]

    public init(blocks: [RichTextBlock] = []) {
        self.blocks = blocks
    }

    /// 提取纯文本（用于搜索、摘要、字数统计）
    public var plainText: String {
        blocks.map { $0.plainText }.joined(separator: "\n")
    }
}
