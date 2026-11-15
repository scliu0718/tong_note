using System.Text.RegularExpressions;

namespace Tonote.LiveTests;

/// <summary>逐项记录 PASS / FAIL，并统一给错误脱敏，避免令牌出现在测试输出里。</summary>
public sealed class Runner
{
    private readonly List<(string Group, string Name, bool Ok, string Detail)> _results = [];
    private string _group = "";

    public int Total => _results.Count;
    public int Failed => _results.Count(item => !item.Ok);
    public IReadOnlyList<(string Group, string Name, bool Ok, string Detail)> Results => _results;

    public void Section(string name)
    {
        _group = name;
        Console.WriteLine();
        Console.WriteLine($"=== {name} ===");
    }

    public void Check(string name, bool ok, string detail = "")
    {
        var text = Redact(detail);
        _results.Add((_group, name, ok, text));
        Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(string.IsNullOrWhiteSpace(text) ? "" : " — " + text)}");
    }

    public async Task Case(string name, Func<Task<(bool Ok, string Detail)>> body)
    {
        try
        {
            var (ok, detail) = await body();
            Check(name, ok, detail);
        }
        catch (Exception ex)
        {
            Check(name, false, ex.GetType().Name + ": " + ex.Message);
        }
    }

    public int Report()
    {
        Console.WriteLine();
        Console.WriteLine("================ 汇总 ================");
        foreach (var group in _results.GroupBy(item => item.Group))
        {
            var passed = group.Count(item => item.Ok);
            Console.WriteLine($"{group.Key,-28} {passed}/{group.Count()}");
        }
        Console.WriteLine($"合计 {Total - Failed}/{Total} 通过，失败 {Failed}");
        if (Failed > 0)
        {
            Console.WriteLine("---- 失败明细 ----");
            foreach (var item in _results.Where(item => !item.Ok))
                Console.WriteLine($"  · [{item.Group}] {item.Name} — {item.Detail}");
        }
        Console.WriteLine("======================================");
        return Failed == 0 ? 0 : 1;
    }

    internal static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var value = Regex.Replace(text, @"(access_token|AccessToken|code|client_secret|refresh_token)=[^&\s""）)]+",
            "$1=<redacted>", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"12[12]\.[A-Za-z0-9._\-]{20,}", "<token>", RegexOptions.IgnoreCase);
        return value.Length <= 400 ? value : value[..400] + "…";
    }
}
