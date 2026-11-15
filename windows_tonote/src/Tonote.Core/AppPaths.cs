namespace Tonote.Core;

/// <summary>
/// 本地数据根目录。Windows 用 %LOCALAPPDATA%\tonote。
/// 在 macOS 上调试时改用 tonote-windows，避免写进 Mac 版的 Application Support/tonote。
/// </summary>
public static class AppPaths
{
    public const string WindowsFolderName = "tonote";
    public const string DevFolderName = "tonote-windows";

    public static string DefaultRoot()
    {
        // 冒烟测试 / 多数据目录场景可用环境变量覆盖，正式使用不受影响
        var overrideRoot = Environment.GetEnvironmentVariable("TONOTE_DATA_ROOT");
        if (!string.IsNullOrWhiteSpace(overrideRoot))
            return Path.GetFullPath(overrideRoot);

        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".tonote");
        }

        return RootFor(OperatingSystem.IsWindows(), baseDir);
    }

    public static string RootFor(bool windows, string localAppData)
        => Path.Combine(localAppData, windows ? WindowsFolderName : DevFolderName);
}
