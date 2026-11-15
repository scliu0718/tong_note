using Tonote.Core;

namespace Tonote.LiveTests;

/// <summary>真实网盘测试上下文：本地数据隔离 + 远端沙箱子目录。</summary>
public sealed class LiveContext
{
    public required string LocalRoot { get; init; }
    public required string RemoteRoot { get; init; }
    public required BaiduPanClient Client { get; init; }
    public required string AccessToken { get; init; }
    public required HttpClient Http { get; init; }
    public required string RunId { get; init; }

    public string Remote(string relative) => RemoteRoot + "/" + relative;

    /// <summary>本次运行产生的所有远端路径，收尾时统一删除。</summary>
    public List<string> CreatedRemotePaths { get; } = [];
}
