import Foundation

public struct Note: Identifiable, Codable, Hashable {
    public var id: UUID
    public var title: String
    public var body: String
    public var createdAt: Date
    public var updatedAt: Date
    public var deletedAt: Date?
    public var tags: [String]
    public var notebook: String
    public var pinned: Bool
    /// 正文格式。旧笔记读取时默认为 markdown，用户实际编辑后升级为 richText。
    public var contentFormat: NoteContentFormat

    public init(id: UUID = UUID(),
                title: String,
                body: String = "",
                createdAt: Date = Date(),
                updatedAt: Date = Date(),
                deletedAt: Date? = nil,
                tags: [String] = [],
                notebook: String = "工作日志",
                pinned: Bool = false,
                contentFormat: NoteContentFormat = .markdown) {
        self.id = id
        self.title = title
        self.body = body
        self.createdAt = createdAt
        self.updatedAt = updatedAt
        self.deletedAt = deletedAt
        self.tags = tags
        self.notebook = notebook
        self.pinned = pinned
        self.contentFormat = contentFormat
    }

    // MARK: - 兼容解码
    private enum CodingKeys: String, CodingKey {
        case id, title, body, createdAt, updatedAt, deletedAt, tags, notebook, pinned, contentFormat
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        id = try container.decode(UUID.self, forKey: .id)
        title = try container.decode(String.self, forKey: .title)
        body = try container.decode(String.self, forKey: .body)
        createdAt = try container.decode(Date.self, forKey: .createdAt)
        updatedAt = try container.decode(Date.self, forKey: .updatedAt)
        deletedAt = try container.decodeIfPresent(Date.self, forKey: .deletedAt)
        tags = try container.decodeIfPresent([String].self, forKey: .tags) ?? []
        notebook = try container.decodeIfPresent(String.self, forKey: .notebook) ?? "工作日志"
        pinned = try container.decodeIfPresent(Bool.self, forKey: .pinned) ?? false
        contentFormat = try container.decodeIfPresent(NoteContentFormat.self, forKey: .contentFormat) ?? .markdown
    }

    public var isTrashed: Bool { deletedAt != nil }

    /// 正文纯文本（不含 HTML 标签与附件路径），用于搜索、摘要、字数统计
    public var plainBody: String {
        MarkdownConverter.plainText(from: body, format: contentFormat)
    }

    /// 列表摘要（从正文派生）
    public var snippet: String {
        let trimmed = plainBody
            .replacingOccurrences(of: "\n", with: " ")
            .trimmingCharacters(in: .whitespaces)
        if trimmed.isEmpty { return "空笔记" }
        return String(trimmed.prefix(60))
    }

    /// 相对时间（今天/昨天/M月d日）
    public var timeText: String {
        let cal = Calendar.current
        let fmt = DateFormatter()
        if cal.isDateInToday(updatedAt) {
            fmt.dateFormat = "HH:mm"
            return "今天 " + fmt.string(from: updatedAt)
        }
        if cal.isDateInYesterday(updatedAt) {
            fmt.dateFormat = "HH:mm"
            return "昨天 " + fmt.string(from: updatedAt)
        }
        fmt.dateFormat = "M月d日"
        return fmt.string(from: updatedAt)
    }
}

/// 固定笔记本（不含「全部笔记」这一虚拟项）
public enum Notebooks {
    public static let all = "全部笔记"
    public static let list = ["工作日志", "读书笔记", "投资研究", "灵感速记"]
}

public extension Note {
    static let samples: [Note] = {
        let now = Date()
        func ago(_ hours: Double) -> Date { now.addingTimeInterval(-3600 * hours) }
        return [
            Note(title: "长江材料 复盘 · 10月22日",
                 body: """
                 今日 #长江材料(001296) 走势回顾与明日预案。

                 ## 盘面回顾
                 早盘低开 -1.2%，随后下探至 20.28 一线获得承接，午后在 20.5 附近反复争夺，尾盘小幅回升，收盘 20.50，+0.39%。全天成交较昨日缩量约两成，属缩量企稳。

                 ## 关键观察
                 - 量能：缩量回踩，抛压明显减弱，但上方 20.8 仍有套牢盘压制。
                 - 筹码：20.2–20.5 区间筹码逐步密集，下方支撑逐步夯实。
                 - 均线：5 日线走平，10 日线仍在上方，短期仍需放量确认方向。

                 ## 明日预案
                 若放量站稳 20.8，可看 21.2 一线；若失守 20.2，则减仓控制风险。持仓不动，止损位 19.8。
                 """,
                 createdAt: ago(50), updatedAt: ago(2),
                 tags: ["A股复盘"], notebook: "投资研究", pinned: true),
            Note(title: "第四季度工作 OKR 草稿",
                 body: "Q4 核心目标：完成量化系统 V4.03 的实盘验证，跑通盘前报告自动化流程。",
                 createdAt: ago(60), updatedAt: ago(26),
                 tags: ["待办"], notebook: "工作日志"),
            Note(title: "《非对称风险》读书笔记",
                 body: "「风险共担」一章：不要听那些不承担后果的人的建议。决策者必须为决策承担后果。",
                 createdAt: ago(90), updatedAt: ago(70),
                 tags: ["读书"], notebook: "读书笔记"),
            Note(title: "产品经理周会纪要",
                 body: "1. 登录链路改为网盘 OAuth；2. 同步改为增量；3. 移动端下周评审。",
                 createdAt: ago(72), updatedAt: ago(72),
                 tags: ["会议纪要"], notebook: "工作日志"),
            Note(title: "灵感：笔记应用数据所有权",
                 body: "为什么用户愿意付费买「数据属于自己」？核心是迁移成本与信任。",
                 createdAt: ago(100), updatedAt: ago(96),
                 tags: ["灵感"], notebook: "灵感速记"),
            Note(title: "筹码峰用法整理",
                 body: "单峰密集 + 量价齐升是最稳的启动形态；双峰则看上方套牢盘压力。",
                 createdAt: ago(120), updatedAt: ago(120),
                 tags: ["A股复盘"], notebook: "投资研究"),
            Note(title: "macOS 开发环境备忘",
                 body: "Xcode 16、SwiftUI、PCS OpenAPI SDK 接入、Keychain 存储 token。",
                 createdAt: ago(170), updatedAt: ago(168),
                 tags: ["技术"], notebook: "工作日志"),
        ]
    }()
}
