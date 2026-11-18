namespace Tonote.Core;

/// <summary>百度网盘接口异常。Message 会带上服务端原始响应片段，便于排查风控/网关问题。</summary>
public class BaiduPanException : Exception
{
    public BaiduPanException(string message) : base(message) { }
    public BaiduPanException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>远端文件不存在。同步链路把它当作「首次同步」的正常路径，不当作失败。</summary>
public sealed class BaiduPanNotFoundException : BaiduPanException
{
    public BaiduPanNotFoundException(string path) : base("远端文件不存在：" + path) { }
}

/// <summary>
/// 临时 / 网络类错误：断网、网关 5xx、连接重置、超时、下载直链抖动、分片上传失败等。
/// 同步引擎据此判断是否进入重试，不依赖中文错误文案。90 秒请求超时仍由 HttpClient 统一控制。
/// </summary>
public sealed class BaiduPanTransientException : BaiduPanException
{
    public BaiduPanTransientException(string message) : base(message) { }
    public BaiduPanTransientException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>本机已在其他设备被下线，需要清掉本地令牌回到登录页。</summary>
public sealed class DeviceKickedException : BaiduPanException
{
    public DeviceKickedException() : base("本机已在其他设备被下线，请重新登录") { }
}
