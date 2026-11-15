using System.Text;
using Tonote.Core;
using Tonote.LiveTests;

// 生产只读验收必须早于任何环境变量覆盖：本模式刻意不设 TONOTE_REMOTE_ROOT，
// 直接读 /apps/tong_log，且不写云端任何文件。
if (args.Contains("--prod", StringComparer.OrdinalIgnoreCase))
    return await ProdCheck.RunAsync();

// ── 安全护栏 ──────────────────────────────────────────────────────────────
// 全部真实网盘写操作只允许落在 /apps/tong_log/__selftest__/ 下。
// 环境变量必须在 BaiduPanConfig 静态构造之前设置。
var runId = "run_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..6];
var remoteRoot = BaiduPanConfig.DefaultRemoteRoot + "/__selftest__/" + runId;
var localRoot = Path.Combine(Paths.TestDataRoot, "data", runId);

Environment.SetEnvironmentVariable("TONOTE_REMOTE_ROOT", remoteRoot);
Environment.SetEnvironmentVariable("TONOTE_DATA_ROOT", localRoot);
Directory.CreateDirectory(localRoot);

// 凭证显式加载（测试程序不在应用目录，自动探测找不到）
BaiduPanConfig.Load(new[] { Paths.Secret });

if (!BaiduPanConfig.RemoteRoot.StartsWith(BaiduPanConfig.DefaultRemoteRoot + "/__selftest__/", StringComparison.Ordinal))
{
    Console.Error.WriteLine("拒绝执行：远端测试根没有落在 __selftest__ 沙箱中，可能是环境变量覆盖失效。");
    return 2;
}

// 令牌从真实登录态复制到隔离目录；刷新/写回只会影响测试目录
var sourceToken = Paths.SourceToken;
if (!File.Exists(sourceToken))
{
    Console.Error.WriteLine("拒绝执行：本机没有真实登录令牌，请先完成百度网盘授权。");
    return 2;
}
File.Copy(sourceToken, Path.Combine(localRoot, "baidupan-token.json"), overwrite: true);

var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
var client = new BaiduPanClient(localRoot, http);
var token = client.CurrentToken ?? throw new InvalidOperationException("令牌解析失败");
var ctx = new LiveContext
{
    LocalRoot = localRoot,
    RemoteRoot = BaiduPanConfig.RemoteRoot,
    Client = client,
    AccessToken = token.AccessToken,
    Http = http,
    RunId = runId,
};

Console.WriteLine($"TONOTE 真实网盘功能测试  runId={runId}");
Console.WriteLine($"  远端沙箱 : {ctx.RemoteRoot}");
Console.WriteLine($"  本地沙箱 : {ctx.LocalRoot}");
Console.WriteLine($"  令牌签发 : {token.CreatedAt:u} / 有效期 {token.ExpiresIn}s / 过期={token.IsExpired}");

var runner = new Runner();

// 维护模式：清掉因进程崩溃/中断残留的历史 __selftest__ 沙箱。只删测试目录，
// 绝不触碰 /apps/tong_log 下的生产数据；必须在没有其它测试实例运行时单独执行。
if (args.Contains("--clean-orphans", StringComparer.OrdinalIgnoreCase))
{
    var sandbox = BaiduPanConfig.DefaultRemoteRoot + "/__selftest__";
    try
    {
        var dirs = await client.ListFilesAsync(sandbox, ctx.AccessToken);
        var removed = 0;
        foreach (var dir in dirs.Where(item => item.IsDir == 1
                                               && item.Name.StartsWith("run_", StringComparison.Ordinal)))
        {
            await client.DeleteRemoteAsync(sandbox + "/" + dir.Name, ctx.AccessToken);
            removed++;
        }
        Console.WriteLine($"已清理历史测试沙箱 {removed} 个");
    }
    catch (BaiduPanNotFoundException)
    {
        Console.WriteLine("历史测试沙箱目录不存在，无需清理");
    }
    return 0;
}

var allGroups = new[] { "A", "B", "C", "D", "E", "F", "G" };
var groups = args.Where(arg => !arg.StartsWith("--", StringComparison.Ordinal)).ToArray();
if (groups.Length == 0) groups = allGroups;

foreach (var group in groups)
{
    try
    {
        switch (group.ToUpperInvariant())
        {
            case "A": await GroupA.RunAsync(ctx, runner); break;
            case "B": await GroupB.RunAsync(ctx, runner); break;
            case "C": await GroupC.RunAsync(ctx, runner); break;
            case "D": await GroupD.RunAsync(ctx, runner); break;
            case "E": await GroupE.RunAsync(ctx, runner); break;
            case "F": await GroupF.RunAsync(ctx, runner); break;
            case "G": await GroupG.RunAsync(ctx, runner); break;
            default: Console.WriteLine($"未知分组：{group}"); break;
        }
    }
    catch (Exception ex)
    {
        // 单组出现未预期异常时记为失败并继续跑后面的组，
        // 保证汇总、退出码和沙箱清理始终执行（异常不能中断整轮回归）。
        runner.Check($"[{group}] 分组执行未捕获异常", false, Runner.Redact(ex.ToString()));
    }
}

// 收尾：删除本次运行创建的远端沙箱目录（只删 run 自己，绝不碰生产目录）
if (!args.Contains("--keep", StringComparer.OrdinalIgnoreCase))
{
    try
    {
        await client.DeleteRemoteAsync(ctx.RemoteRoot, ctx.AccessToken);
        Console.WriteLine($"\n已清理远端沙箱：{ctx.RemoteRoot}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\n沙箱清理失败（可稍后手动删）：{Runner.Redact(ex.Message)}");
    }
}

return runner.Report();
