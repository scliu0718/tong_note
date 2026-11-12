using System.Diagnostics;

namespace Tonote.App;

internal static class BrowserLauncher
{
    public static void Open(string url)
    {
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            Process.Start("open", url);
            return;
        }

        Process.Start("xdg-open", url);
    }
}
