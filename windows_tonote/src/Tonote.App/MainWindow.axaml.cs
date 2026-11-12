using Avalonia.Controls;
using Avalonia.Media;
using Tonote.Core;

namespace Tonote.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        FontFamily = FontFamily.Parse("Microsoft YaHei UI, PingFang SC, Segoe UI, Inter");
        var session = new AppSession();
        var login = new BaiduLogin(new BaiduPanTokenStore(session.Root));
        void ShowShell()
        {
            if (Root.Content is not ShellView)
                Root.Content = new ShellView(session);
        }

        login.SignedIn += ShowShell;
        AppInstance.Callback += login.Accept;
        if (login.IsSignedIn)
            ShowShell();
        else
            Root.Content = new LoginView(login, ShowShell);

        if (AppInstance.StartupCallback != null)
            login.Accept(AppInstance.StartupCallback);
    }
}
