using Microsoft.Win32;

namespace Tonote.App;

/// <summary>
/// 让浏览器里的 tongnote:// 能回到本程序。写当前用户的注册表，不需要管理员。
/// </summary>
internal static class ProtocolRegistration
{
    public static void Ensure()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return;

        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\tongnote");
        key.SetValue("", "URL:同笔记授权");
        key.SetValue("URL Protocol", "");
        using var command = key.CreateSubKey(@"shell\open\command");
        command.SetValue("", $"\"{path}\" \"%1\"");
    }
}
