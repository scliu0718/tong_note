using System.Text;
using System.Text.RegularExpressions;
using Tonote.Core;

namespace Tonote.App;

/// <summary>
/// W-19 / W-38：本地诊断日志。每类 2MB × 3 滚动，严格排除令牌 / 密钥 / 笔记正文。
/// 记录的是「发生了什么」而不是「内容是什么」，失败原因只保留异常类型与摘要。
/// </summary>
public static class DiagnosticLog
{
    private const long MaxBytes = 2L * 1024 * 1024;
    private const int Keep = 3;
    private static readonly object Gate = new();

    private static readonly Regex SecretPattern = new(
        @"(access[_-]?token|refresh[_-]?token|client[_-]?secret|code|state)\s*[=:]\s*[^&\s""']+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LongBase64 = new(
        @"[A-Za-z0-9+/=_-]{80,}", RegexOptions.Compiled);

    public static void Write(string category, string message, Exception? error = null)
    {
        try
        {
            var dir = Path.Combine(AppPaths.DefaultRoot(), "logs");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, category + "-debug.log");
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{category}] {Sanitize(message)}"
                       + (error == null ? "" : $" | {Sanitize(error.GetType().Name + ": " + error.Message)}");
            lock (Gate)
            {
                Rotate(file);
                File.AppendAllText(file, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // 诊断失败绝不影响主流程
        }
    }

    private static void Rotate(string file)
    {
        if (!File.Exists(file) || new FileInfo(file).Length < MaxBytes) return;
        for (var i = Keep - 1; i >= 1; i--)
        {
            var from = i == 1 ? file : file + "." + (i - 1);
            var to = file + "." + i;
            if (!File.Exists(from)) continue;
            try
            {
                if (File.Exists(to)) File.Delete(to);
                File.Move(from, to);
            }
            catch { /* 滚动失败就保持原文件，不中断写入 */ }
        }
        try { File.Delete(file); } catch { /* 同上 */ }
    }

    private static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var value = text.Replace('\r', ' ').Replace('\n', ' ');
        value = SecretPattern.Replace(value, m => m.Groups[1].Value + "=***");
        value = LongBase64.Replace(value, "***");
        return value.Length > 600 ? value[..600] + "…" : value;
    }
}
