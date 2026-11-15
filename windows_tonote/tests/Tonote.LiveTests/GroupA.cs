using Tonote.Core;

namespace Tonote.LiveTests;

/// <summary>A 组：真实凭证、令牌、账号信息与生产目录可读性。</summary>
public static class GroupA
{
    public static async Task RunAsync(LiveContext ctx, Runner r)
    {
        r.Section("A 认证与账号（真实凭证）");

        r.Check("A01 凭证已加载",
            BaiduPanConfig.IsConfigured && BaiduPanConfig.AppKey.Length == 32
            && BaiduPanConfig.AppSecret.Length == 32 && !string.IsNullOrWhiteSpace(BaiduPanConfig.AppId),
            $"appKey长度={BaiduPanConfig.AppKey.Length} appSecret长度={BaiduPanConfig.AppSecret.Length} appID={BaiduPanConfig.AppId}");

        var token = ctx.Client.CurrentToken;
        r.Check("A02 本机令牌未过期且字段完整",
            token != null && !token.IsExpired && token.AccessToken.Length > 40
            && token.RefreshToken.Length > 40 && token.ExpiresIn > 0,
            $"access长度={token?.AccessToken.Length} refresh长度={token?.RefreshToken.Length} expiresIn={token?.ExpiresIn}");

        r.Check("A03 授权范围包含网盘读写",
            (token?.Scope ?? "").Contains("netdisk", StringComparison.OrdinalIgnoreCase),
            $"scope={token?.Scope}");

        r.Check("A04 远端根隔离生效",
            ctx.RemoteRoot.StartsWith(BaiduPanConfig.DefaultRemoteRoot + "/__selftest__/", StringComparison.Ordinal)
            && ctx.RemoteRoot != BaiduPanConfig.DefaultRemoteRoot,
            ctx.RemoteRoot);

        await r.Case("A05 用户信息 uinfo", async () =>
        {
            var info = await ctx.Client.UserInfoAsync(ctx.AccessToken);
            var ok = !string.IsNullOrWhiteSpace(info.BaiduName) || !string.IsNullOrWhiteSpace(info.NetdiskName);
            return (ok, $"昵称={info.DisplayName} vip={info.VipType} 头像={(string.IsNullOrEmpty(info.AvatarUrl) ? "无" : "有")}");
        });

        await r.Case("A06 网盘配额 quota", async () =>
        {
            var quota = await ctx.Client.QuotaAsync(ctx.AccessToken);
            return (quota.Total > 0 && quota.Used >= 0,
                $"总量={quota.TotalText} 已用={quota.UsedText} 占用={quota.UsedRatio:P1}");
        });

        await r.Case("A07 生产应用目录可列举", async () =>
        {
            var entries = await ctx.Client.ListFilesAsync(BaiduPanConfig.DefaultRemoteRoot, ctx.AccessToken);
            var names = entries.Select(item => item.Name).Take(8).ToArray();
            return (true, $"共 {entries.Count} 项：" + string.Join(", ", names));
        });

        await r.Case("A08 不存在的目录映射为 NotFound", async () =>
        {
            try
            {
                await ctx.Client.ListFilesAsync(ctx.RemoteRoot, ctx.AccessToken);
                return (false, "未抛异常（沙箱目录本应不存在）");
            }
            catch (BaiduPanNotFoundException)
            {
                return (true, "正确抛出 BaiduPanNotFoundException");
            }
            catch (Exception ex)
            {
                return (false, "异常类型不符：" + ex.GetType().Name + " " + ex.Message);
            }
        });
    }
}
