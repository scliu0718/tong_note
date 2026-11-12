using Avalonia;

namespace Tonote.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (AppInstance.HandOffAndExit(args))
            return;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
