namespace Tonote.Core;

public static class NoteSamples
{
    public static List<Note> Create()
    {
        var now = DateTime.UtcNow;
        DateTime Ago(double hours) => now.AddHours(-hours);

        return
        [
            Make("长江材料 复盘 · 10月22日",
                """
                今日 #长江材料(001296) 走势回顾与明日预案。

                ## 盘面回顾
                早盘低开 -1.2%，随后下探至 20.28 一线获得承接，午后在 20.5 附近反复争夺，尾盘小幅回升。

                ## 明日预案
                若放量站稳 20.8，可看 21.2 一线；若失守 20.2，则减仓控制风险。
                """,
                Ago(50), Ago(2), ["A股复盘"], "投资研究", pinned: true),
            Make("第四季度工作 OKR 草稿",
                "Q4 核心目标：完成量化系统 V4.03 的实盘验证，跑通盘前报告自动化流程。",
                Ago(60), Ago(26), ["待办"], "工作日志", pinned: false),
            Make("《非对称风险》读书笔记",
                "「风险共担」一章：不要听那些不承担后果的人的建议。决策者必须为决策承担后果。",
                Ago(90), Ago(70), ["读书"], "读书笔记", pinned: false),
            Make("产品经理周会纪要",
                "1. 登录链路改为网盘 OAuth；2. 同步改为增量；3. 移动端下周评审。",
                Ago(72), Ago(72), ["会议纪要"], "工作日志", pinned: false),
            Make("灵感：笔记应用数据所有权",
                "为什么用户愿意付费买「数据属于自己」？核心是迁移成本与信任。",
                Ago(100), Ago(96), ["灵感"], "灵感速记", pinned: false),
            Make("筹码峰用法整理",
                "单峰密集 + 量价齐升是最稳的启动形态；双峰则看上方套牢盘压力。",
                Ago(120), Ago(120), ["A股复盘"], "投资研究", pinned: false),
            Make("开发环境备忘",
                "Windows 端与 Mac 端共用 notes.json。正文是受控 HTML 或 Markdown。",
                Ago(170), Ago(168), ["技术"], "工作日志", pinned: false),
        ];
    }

    public static Note QuickSync()
    {
        return new Note
        {
            Id = Note.QuickSyncNoteId,
            Title = "快同步",
            Notebook = "工作日志",
            ContentFormat = NoteContentFormat.markdown,
            Pinned = false,
            Body = """
                这是「快同步」专属笔记。Windows 端的单篇云同步还在迁移。

                打开这篇笔记不会发起同步。同步按钮会在百度网盘登录接入后出现。
                """,
        };
    }

    private static Note Make(
        string title,
        string body,
        DateTime createdAt,
        DateTime updatedAt,
        List<string> tags,
        string notebook,
        bool pinned)
    {
        return new Note
        {
            Title = title,
            Body = body,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            Tags = tags,
            Notebook = notebook,
            Pinned = pinned,
            ContentFormat = NoteContentFormat.markdown,
        };
    }
}
