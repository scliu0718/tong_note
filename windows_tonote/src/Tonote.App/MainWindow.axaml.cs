using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Tonote.Core;

namespace Tonote.App;

public partial class MainWindow : Window
{
    private readonly AppSession _session;
    private readonly BaiduLogin _login;

    public MainWindow()
    {
        InitializeComponent();
        FontFamily = FontFamily.Parse("Microsoft YaHei UI, PingFang SC, Segoe UI, Inter");
        _session = new AppSession();
        _login = new BaiduLogin(_session.Pan.Tokens);

        void ShowShell()
        {
            if (Root.Content is not ShellView)
            {
                var shell = new ShellView(_session);
                shell.LogoutRequested += () =>
                {
                    _session.Logout();
                    ShowLogin();
                };
                Root.Content = shell;
            }
            _session.StartSync();
        }

        void ShowLogin()
        {
            _login.SignedIn -= ShowShell;
            _login.SignedIn += ShowShell;
            Root.Content = new LoginView(_login, ShowShell);
        }

        _login.SignedIn += ShowShell;
        _session.Sync.Kicked += () => Dispatcher.UIThread.Post(() =>
        {
            _session.Logout();
            ShowLogin();
        });
        AppInstance.Callback += _login.Accept;

        if (_login.IsSignedIn)
            ShowShell();
        else
            ShowLogin();

        if (AppInstance.StartupCallback != null)
            _login.Accept(AppInstance.StartupCallback);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _session.Sync.Dispose();
        base.OnClosing(e);
    }
}
