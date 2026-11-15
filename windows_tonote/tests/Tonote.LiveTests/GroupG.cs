using System.Diagnostics;
using System.Text;
using Tonote.Core;

namespace Tonote.LiveTests;

/// <summary>G 组：真正启动 Tonote.exe（GUI），用隔离数据目录连真实网盘跑首轮同步。</summary>
public static class GroupG
{
    private static string Exe => Paths.Exe;

    public static async Task RunAsync(LiveContext ctx, Runner r)
    {
        r.Section("G 真实 GUI 进程端到端（WebView2 + 自动同步）");
        var at = ctx.AccessToken;
        var guiRoot = ctx.LocalRoot + "_gui";
        var guiRemote = ctx.RemoteRoot + "_gui";
        Directory.CreateDirectory(guiRoot);
        File.Copy(Paths.SourceToken,
            Path.Combine(guiRoot, "baidupan-token.json"), overwrite: true);

        // 只清理本输出目录留下的旧实例，避免单实例回调把新进程吞掉
        foreach (var process in Process.GetProcessesByName("Tonote"))
        {
            try
            {
                if (string.Equals(process.MainModule?.FileName, Exe, StringComparison.OrdinalIgnoreCase))
                    process.Kill(entireProcessTree: true);
            }
            catch { /* 权限或已退出，忽略 */ }
        }

        r.Check("G01 可执行文件与凭证齐备",
            File.Exists(Exe) && File.Exists(Path.Combine(Path.GetDirectoryName(Exe)!, "baidupan.secret.json"))
            && File.Exists(Path.Combine(guiRoot, "baidupan-token.json")),
            Exe);

        Process? proc = null;
        var log = new StringBuilder();
        try
        {
            var psi = new ProcessStartInfo(Exe)
            {
                WorkingDirectory = Path.GetDirectoryName(Exe)!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.Environment["TONOTE_DATA_ROOT"] = guiRoot;
            psi.Environment["TONOTE_REMOTE_ROOT"] = guiRemote;
            proc = Process.Start(psi);
            if (proc != null)
            {
                proc.OutputDataReceived += (_, e) =>
                {
                    if (e.Data != null) lock (log) log.AppendLine(e.Data);
                };
                proc.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null) lock (log) log.AppendLine("ERR: " + e.Data);
                };
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
            }

            r.Check("G02 Tonote.exe 成功启动且 3 秒内未退出", proc != null && !WaitExit(proc, 3),
                proc == null ? "Process.Start 返回 null" : $"PID={proc.Id}");

            // 轮询等待：本地数据文件、WebView2 浏览器配置、云端按篇文件
            var sw = Stopwatch.StartNew();
            var ready = false;
            while (sw.Elapsed < TimeSpan.FromSeconds(120))
            {
                if (File.Exists(Path.Combine(guiRoot, "editor.html"))
                    && File.Exists(Path.Combine(guiRoot, "device.json")))
                {
                    ready = true;
                    break;
                }
                await Task.Delay(1000);
            }

            r.Check("G03 登录态直接进入主界面并生成编辑器 / 设备身份", ready,
                $"耗时={sw.Elapsed.TotalSeconds:0.0}s 目录文件=[{string.Join(",",
                    Directory.GetFiles(guiRoot).Select(Path.GetFileName).Take(10))}]");

            await r.Case("G04 WebView2 运行时初始化成功（EBWebView 配置目录）", async () =>
            {
                var target = Path.Combine(guiRoot, "webview2", "EBWebView");
                var sw3 = Stopwatch.StartNew();
                while (sw3.Elapsed < TimeSpan.FromSeconds(60) && !Directory.Exists(target))
                    await Task.Delay(1000);
                return (Directory.Exists(target), $"等待 {sw3.Elapsed.TotalSeconds:0.0}s 后存在={Directory.Exists(target)}");
            });

            await r.Case("G05 应用自动首轮同步把笔记写到真实网盘", async () =>
            {
                List<BaiduPanListEntry> notes = [];
                List<BaiduPanListEntry> devices = [];
                var statusPath = Path.Combine(guiRoot, "sync-status.json");
                var statusText = "";
                var sw2 = Stopwatch.StartNew();
                while (sw2.Elapsed < TimeSpan.FromSeconds(240))
                {
                    notes = await SafeList(ctx, guiRemote + "/notes");
                    devices = await SafeList(ctx, guiRemote + "/devices");
                    statusText = File.Exists(statusPath)
                        ? await File.ReadAllTextAsync(statusPath) : "";
                    if (notes.Count > 0)
                    {
                        // 第一篇上云不等于整轮完成，继续等状态落定
                        var settle = Stopwatch.StartNew();
                        while (settle.Elapsed < TimeSpan.FromSeconds(120))
                        {
                            statusText = File.Exists(statusPath)
                                ? await File.ReadAllTextAsync(statusPath) : "";
                            if (statusText.Contains("\"Success\"", StringComparison.Ordinal)
                                || File.Exists(Path.Combine(guiRoot, "last-sync.json")))
                                break;
                            await Task.Delay(2000);
                        }
                        break;
                    }
                    await Task.Delay(4000);
                }
                var flatStatus = statusText.Replace("\n", " ").Replace("\r", "");
                var settled = flatStatus.Contains("\"Success\"", StringComparison.Ordinal)
                              || File.Exists(Path.Combine(guiRoot, "last-sync.json"));
                return (notes.Count > 0 && devices.Count > 0 && settled,
                    $"云端笔记={notes.Count} 设备={devices.Count} 耗时={sw2.Elapsed.TotalSeconds:0.0}s "
                    + $"last-sync={File.Exists(Path.Combine(guiRoot, "last-sync.json"))} 整轮完成={settled} "
                    + $"同步状态={flatStatus[..Math.Min(220, flatStatus.Length)]}");
            });

            await r.Case("G06 编辑器 HTML 未被实体解码污染且含桥接代码", async () =>
            {
                var path = Path.Combine(guiRoot, "editor.html");
                if (!File.Exists(path)) return (false, "editor.html 未生成");
                var html = await File.ReadAllTextAsync(path);
                return (html.Contains("chrome.webview.postMessage", StringComparison.Ordinal)
                        && html.Contains("window.tn", StringComparison.Ordinal)
                        && html.Contains("&amp;", StringComparison.Ordinal) == false
                        && html.Length > 5000,
                    $"{html.Length} 字符，含 window.tn={html.Contains("window.tn", StringComparison.Ordinal)}");
            });

            r.Check("G07 进程运行期间日志无未处理异常",
                !log.ToString().Contains("Unhandled exception", StringComparison.OrdinalIgnoreCase)
                && !log.ToString().Contains("FATAL", StringComparison.OrdinalIgnoreCase),
                $"日志 {log.Length} 字符");
        }
        finally
        {
            if (proc != null)
            {
                try
                {
                    if (!proc.HasExited) proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(5000);
                }
                catch { /* 已退出 */ }
                proc.Dispose();
            }
            try { await ctx.Client.DeleteRemoteAsync(guiRemote, at); }
            catch { /* 沙箱清理失败不影响结论 */ }
            Console.WriteLine("  GUI 进程已结束，独立沙箱已清理");
        }
    }

    private static bool WaitExit(Process process, int seconds)
    {
        for (var i = 0; i < seconds * 2; i++)
        {
            if (process.HasExited) return true;
            Thread.Sleep(500);
        }
        return process.HasExited;
    }

    private static async Task<List<BaiduPanListEntry>> SafeList(LiveContext ctx, string path)
    {
        try { return await ctx.Client.ListFilesAsync(path, ctx.AccessToken); }
        catch (BaiduPanNotFoundException) { return []; }
    }
}
