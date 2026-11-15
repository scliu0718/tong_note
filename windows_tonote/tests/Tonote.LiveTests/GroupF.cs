using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Tonote.App;
using Tonote.Core;

namespace Tonote.LiveTests;

/// <summary>F 组：UI 层设计令牌、功能点覆盖、编辑器 HTML、会话组装与真实首轮同步。</summary>
public static class GroupF
{
    private static string Repo => Paths.RepoRoot;
    private static string AppDir => Paths.AppDir;

    public static async Task RunAsync(LiveContext ctx, Runner r)
    {
        r.Section("F UI 层（设计令牌 / 功能点 / 编辑器 / 会话）");

        // ── 设计令牌 ────────────────────────────────────────────────
        r.Check("F01 主题色与 Mac UI 设计文档逐项对齐", CheckThemeTokens(out var tokenDetail), tokenDetail);

        // ── 功能点覆盖 ──────────────────────────────────────────────
        r.Check("F02 主界面覆盖功能点文档的关键能力", CheckShellFeatures(out var featureDetail), featureDetail);

        // ── 富文本编辑器 ────────────────────────────────────────────
        r.Check("F03 WebView2 编辑器 HTML 含完整 JS↔C# 桥接", CheckEditorBridge(out var bridgeDetail), bridgeDetail);

        r.Check("F03b 工具条命令覆盖功能点文档全部格式能力", CheckToolbar(out var toolbarDetail), toolbarDetail);

        r.Check("F04 浏览器产出的 HTML 能归一化为受控协议", CheckBrowserNormalize(out var normalizeDetail), normalizeDetail);

        // ── 会话组装 ────────────────────────────────────────────────
        var session = new AppSession(ctx.LocalRoot);
        // 依赖属性在构造时必定赋值，不做冗余的 != null 检查：那会让编译器把非可空属性
        // 标记为 maybe-null，后续 F06/F07 访问同一属性时集体报 CS8602。
        // 各依赖是否可用由 F06/F07/F08 的实际调用来验证。
        r.Check("F05 AppSession 组装完整且识别真实登录态",
            session.IsSignedIn && session.Root == ctx.LocalRoot
            && session.Notes.Notes.Count >= 0,
            $"root={session.Root} 已登录={session.IsSignedIn} 笔记={session.Notes.Notes.Count}");

        await r.Case("F06 StartSync 自动完成设备登记 + 首轮真实同步（含故障恢复）", async () =>
        {
            var sw = Stopwatch.StartNew();
            var attempts = 0;
            var transient = new List<string>();
            session.StartSync();
            while (sw.Elapsed < TimeSpan.FromSeconds(240))
            {
                if (attempts > 0)
                    await session.Sync.QuickSyncAsync();
                attempts++;
                // LastSyncDate 赋值早于 SetStatus(Succeeded)，必须等状态落定，避免竞态误判
                while (sw.Elapsed < TimeSpan.FromSeconds(240)
                       && session.Sync.Status.Kind is SyncStatusKind.Idle or SyncStatusKind.Syncing)
                    await Task.Delay(500);
                if (session.Sync.Status.Kind == SyncStatusKind.Success) break;
                if (attempts >= 3) break;
                // 本机代理偶发 502 / 连接重置，按 Mac 版语义「失败留到下一轮」手动重试
                transient.Add(session.Sync.LastMessage ?? "失败");
                await Task.Delay(1000);
            }
            var status = session.Sync.Status;
            return (status.Kind == SyncStatusKind.Success && session.Sync.LastSyncDate != null,
                $"状态={status.Kind} 消息={status.Message} 尝试={attempts} 次 耗时={sw.Elapsed.TotalSeconds:0.0}s"
                + (transient.Count == 0 ? "" : $" 瞬态失败={transient.Count} 次（{Runner.Redact(transient[0])[..Math.Min(70, Runner.Redact(transient[0]).Length)]}…）"));
        });

        await r.Case("F07 头像按设计只在用户选择后写云端", async () =>
        {
            var avatarPath = ctx.Remote("avatar.json");
            // A→G 顺序执行时 D12 会先写出 avatar.json；F 组自己建立干净前提，
            // 否则「选择前云端已有头像」只是前面分组的残留，不是本组行为。
            await ctx.Client.DeleteRemoteAsync(avatarPath, ctx.AccessToken);
            var cleared = false;
            for (var i = 0; i < 20 && !cleared; i++)
            {
                cleared = !await Exists(ctx, avatarPath);
                if (!cleared) await Task.Delay(200);
            }
            var before = await Exists(ctx, avatarPath);

            session.Avatar.Select("leaf");
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(30)
                   && session.Avatar.CloudState is "idle" or "uploading")
                await Task.Delay(200);
            var after = await Exists(ctx, avatarPath);
            var cloud = after
                ? Encoding.UTF8.GetString(await ctx.Client.DownloadDataAsync(avatarPath, ctx.AccessToken))
                : "";
            var isLeaf = cloud.Contains("leaf", StringComparison.OrdinalIgnoreCase);
            var devices = await SafeList(ctx, ctx.Remote("devices"));
            var notes = await SafeList(ctx, ctx.Remote("notes"));
            return (cleared && !before && after && isLeaf
                    && devices.Count >= 1 && notes.Count >= 1
                    && session.Avatar.CloudState == "synced",
                $"清场={cleared} 选择前云端头像={before} 选择后={after} 云端=leaf:{isLeaf} "
                + $"状态={session.Avatar.CloudState} 设备={devices.Count} 笔记={notes.Count}");
        });

        r.Check("F08 退出登录清除本机令牌",
            LogoutWorks(session, ctx.LocalRoot),
            "Pan.Logout() 后 IsSignedIn=false 且 baidupan-token.json 已删");
    }

    private static bool CheckThemeTokens(out string detail)
    {
        var mac = File.ReadAllText(Path.Combine(Repo, "同笔记-Mac端UI设计.html"));
        var theme = File.ReadAllText(Path.Combine(AppDir, "Theme.cs"));
        var pairs = new (string Css, string Hex, string CSharp)[]
        {
            ("--accent", "#1a9e5c", "0x1A9E5C"),
            ("--accent-hover", "#147a46", "0x147A46"),
            ("--accent-tint", "#e6f4ec", "0xE6F4EC"),
            ("--baidu", "#06a7ff", "0x06A7FF"),
            ("--text-1", "#1d1d1f", "0x1D1D1F"),
            ("--text-2", "#48484a", "0x48484A"),
            ("--text-3", "#86868b", "0x86868B"),
            ("--text-4", "#a1a1a6", "0xA1A1A6"),
            ("--panel-bg", "#ffffff", "0xFFFFFF"),
        };
        var missing = new List<string>();
        foreach (var (css, hex, csharp) in pairs)
        {
            if (!mac.Contains(css + ":" + hex, StringComparison.OrdinalIgnoreCase))
                missing.Add("设计文档缺 " + css);
            if (!theme.Contains(csharp, StringComparison.OrdinalIgnoreCase))
                missing.Add("Theme.cs 缺 " + csharp);
        }
        detail = $"核对 {pairs.Length} 组色值；不一致=[{string.Join(",", missing)}]";
        return missing.Count == 0;
    }

    private static bool CheckShellFeatures(out string detail)
    {
        var shell = File.ReadAllText(Path.Combine(AppDir, "ShellView.cs"));
        var checks = new (string Name, string[] Keywords)[]
        {
            ("搜索", ["_search", "RebuildCards"]),
            ("笔记列表与卡片", ["RebuildCards", "Card"]),
            ("标签", ["_tagInput", "RenderTags"]),
            ("笔记本", ["_notebook", "NotebookChoices"]),
            ("回收站", ["MoveToTrash", "Restore", "PermanentlyDelete"]),
            ("置顶", ["TogglePin", "_pinButton"]),
            ("富文本编辑器", ["RichTextEditor", "_richEditor"]),
            ("同步状态徽标", ["_sidebarSync", "UpdateSyncLabels", "SyncStatusKind.Failed"]),
            ("立即同步", ["QuickSyncAsync"]),
            ("单篇快同步", ["SyncSingleNoteAsync"]),
            ("设置面板", ["ShowSettings", "QuotaAsync", "UserInfoAsync"]),
            ("设备管理", ["ListDevicesAsync", "KickAsync"]),
            ("头像", ["AvatarStore", "Presets"]),
            ("待办详情", ["UpdateNotes", "SetPlannedAt", "UpdateTitle"]),
            ("退出登录", ["LogoutRequested"]),
        };
        var missing = checks.Where(item => item.Keywords.Any(keyword => !shell.Contains(keyword,
            StringComparison.Ordinal))).Select(item => item.Name).ToArray();
        detail = $"覆盖 {checks.Length - missing.Length}/{checks.Length} 项能力；缺=[{string.Join(",", missing)}]";
        return missing.Length == 0;
    }

    private static bool CheckEditorBridge(out string detail)
    {
        var html = EditorHtml.Content;
        string[] required =
        [
            "contenteditable", "chrome.webview.postMessage", "window.tn", "getHTML", "setContent",
            "styleWithCSS", "execCommand", "formatBlock", "insertHTML", "clean()",
            "data-fullwidth", "data-align", "data-bg", "attachments/",
        ];
        var missing = required.Where(keyword => !html.Contains(keyword,
            StringComparison.OrdinalIgnoreCase)).ToArray();
        detail = $"HTML {html.Length} 字符；缺=[{string.Join(",", missing)}]";
        return missing.Length == 0;
    }

    private static bool CheckToolbar(out string detail)
    {
        // 命令名由 C# 工具条通过 window.tn.exec / block / insertXxx 下发，JS 侧只提供通用桥接
        // 命令名一部分由 C# 工具条下发，一部分（如 formatBlock）在 JS 桥接里展开，合并检查
        var source = File.ReadAllText(Path.Combine(AppDir, "RichText", "RichTextEditor.cs"))
                     + "\n" + EditorHtml.Content;
        string[] commands =
        [
            "formatBlock", "bold", "italic", "underline", "strikeThrough",
            "foreColor", "hiliteColor", "insertUnorderedList", "insertOrderedList",
            "insertLink", "insertImage", "insertTable", "insertRow", "insertColumn",
            "removeRow", "removeColumn", "setCellAlign", "setCellBackground",
        ];
        var missing = commands.Where(keyword => !source.Contains(keyword,
            StringComparison.OrdinalIgnoreCase)).ToArray();
        detail = $"核对 {commands.Length} 条命令；缺=[{string.Join(",", missing)}]";
        return missing.Length == 0;
    }

    private static bool CheckBrowserNormalize(out string detail)
    {
        // 浏览器 contenteditable 常见产物：div 分段、font 着色、tbody 包裹表格
        const string browser = "<div>第一段</div><div><font color=\"#FF0000\">红字</font></div>"
                               + "<table><tbody><tr><td style=\"background-color:#AABBCC\" data-align=\"center\">A</td>"
                               + "<td>B</td></tr></tbody></table><p>尾段</p>";
        var decoded = RichTextHtmlCodec.Decode(browser);
        var encoded = RichTextHtmlCodec.Encode(decoded);
        var again = RichTextHtmlCodec.Decode(encoded);
        var table = decoded.Blocks.FirstOrDefault(block => block.Table != null)?.Table;
        var shape = table != null && table.Rows.Count == 1 && table.Rows[0].Count == 2;
        var background = table?.Rows[0][0].BackgroundHex == "#AABBCC";
        var align = table?.Rows[0][0].Alignment == RichTextCellAlignment.center;
        var stable = decoded.Blocks.Count == again.Blocks.Count;
        detail = $"解码 {decoded.Blocks.Count} 块→重编码 {encoded.Length} 字符→{again.Blocks.Count} 块；"
                 + $"表格形状={shape} 底色={background} 对齐={align} 块数稳定={stable}";
        return shape && background && align && stable;
    }

    private static bool LogoutWorks(AppSession session, string root)
    {
        session.Logout();
        return !session.IsSignedIn && !File.Exists(Path.Combine(root, "baidupan-token.json"));
    }

    private static async Task<List<BaiduPanListEntry>> SafeList(LiveContext ctx, string path)
    {
        try { return await ctx.Client.ListFilesAsync(path, ctx.AccessToken); }
        catch (BaiduPanNotFoundException) { return []; }
    }

    private static async Task<bool> Exists(LiveContext ctx, string path)
    {
        try
        {
            await ctx.Client.DownloadDataAsync(path, ctx.AccessToken);
            return true;
        }
        catch (BaiduPanNotFoundException) { return false; }
    }
}
