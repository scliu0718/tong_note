namespace Tonote.Core;

/// <summary>
/// 令牌存在数据目录的 baidupan-token.json。非 Windows 上限制为仅当前用户可读写。
/// </summary>
public sealed class BaiduPanTokenStore
{
    public const string FileName = "baidupan-token.json";

    public BaiduPanTokenStore(string root) => Root = root;

    public string Root { get; }

    public string Path => System.IO.Path.Combine(Root, FileName);

    public BaiduPanToken? Load()
    {
        if (!File.Exists(Path))
            return null;
        try
        {
            var token = TonoteJson.Deserialize<BaiduPanToken>(File.ReadAllText(Path));
            if (token == null || string.IsNullOrWhiteSpace(token.AccessToken))
                return null;
            return token;
        }
        catch
        {
            return null;
        }
    }

    public void Save(BaiduPanToken token)
    {
        TonoteJson.WriteAtomic(Path, TonoteJson.Serialize(token));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public void Clear()
    {
        if (File.Exists(Path))
            File.Delete(Path);
    }
}
