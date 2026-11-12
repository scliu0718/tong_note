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
