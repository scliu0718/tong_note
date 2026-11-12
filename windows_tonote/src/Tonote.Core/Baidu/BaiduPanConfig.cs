namespace Tonote.Core;

/// <summary>
/// 百度网盘开放平台配置。凭证只从本机 baidupan.secret.json 读取，不写进源码。
/// 查找顺序：调用方指定的路径、程序目录、当前目录，再向上找几层。
/// </summary>
public static class BaiduPanConfig
{
    public const string RedirectUri = "tongnote://oauth/callback";
    public const string CallbackScheme = "tongnote";
    public const string Scope = "basic,netdisk";
    public const string RemoteRoot = "/apps/tong_log";

    public static readonly Uri AuthorizeUrl = new("https://openapi.baidu.com/oauth/2.0/authorize");
    public static readonly Uri TokenUrl = new("https://openapi.baidu.com/oauth/2.0/token");

    public static string AppKey { get; private set; } = "";
    public static string AppSecret { get; private set; } = "";
    public static string AppId { get; private set; } = "";

    public static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(AppKey) && !string.IsNullOrWhiteSpace(AppSecret);

    static BaiduPanConfig() => Load();

    public static void Load(IEnumerable<string>? paths = null)
    {
        foreach (var path in Candidates(paths))
        {
            if (!File.Exists(path))
                continue;
            try
            {
                var secret = TonoteJson.Deserialize<Secret>(File.ReadAllText(path));
                if (secret == null
                    || string.IsNullOrWhiteSpace(secret.AppKey)
                    || string.IsNullOrWhiteSpace(secret.AppSecret)
                    || string.IsNullOrWhiteSpace(secret.AppID))
                    continue;

                AppKey = secret.AppKey;
                AppSecret = secret.AppSecret;
                AppId = secret.AppID;
                return;
            }
            catch
            {
                // 解码失败就试下一个位置，和 Mac 版一样不把凭证错误写进日志。
            }
        }
    }

    private static IEnumerable<string> Candidates(IEnumerable<string>? paths)
    {
        if (paths != null)
        {
            foreach (var path in paths)
                yield return path;
        }

        yield return Path.Combine(AppContext.BaseDirectory, "baidupan.secret.json");
        yield return Path.Combine(Environment.CurrentDirectory, "baidupan.secret.json");

        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = new DirectoryInfo(start);
            for (var i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
                yield return Path.Combine(dir.FullName, "baidupan.secret.json");
        }
    }

    private sealed class Secret
    {
        public string AppKey { get; set; } = "";
        public string AppSecret { get; set; } = "";
        public string AppID { get; set; } = "";
    }
}
