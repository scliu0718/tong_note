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

/// <summary>本机已在其他设备被下线，需要清掉本地令牌回到登录页。</summary>
public sealed class DeviceKickedException : BaiduPanException
{
    public DeviceKickedException() : base("本机已在其他设备被下线，请重新登录") { }
}
