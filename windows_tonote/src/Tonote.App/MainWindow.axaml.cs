using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Tonote.Core;

namespace Tonote.App;

public partial class MainWindow : Window
{
    private readonly AppSession _session;
    private readonly BaiduLogin _login;
    private readonly SessionGuard _sessionGuard;
    private ShellView? _shell;

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
            _shell = Root.Content as ShellView;
            // W-21：未登录绝不启动同步（否则必然失败）
            if (_session.IsSignedIn) _session.StartSync();
        }

        void ShowLogin()
        {
            _shell?.Flush();
            _shell = null;
            _login.SignedIn -= ShowShell;
            _login.SignedIn += ShowShell;
            Root.Content = new LoginView(_login, () => Dialogs.ShowGuide(this));
        }

        _login.SignedIn += ShowShell;
        _session.Sync.Kicked += () => Dispatcher.UIThread.Post(() =>
        {
            _session.Logout();
            ShowLogin();
        });
        // W-23：浏览器回调 / 再次双击 exe 都把已有窗口置前，不新开第二个
        AppInstance.Callback += text => Dispatcher.UIThread.Post(() =>
        {
            BringToFront();
            if (text == "tongnote://activate") return;
            _login.Accept(text);
        });

        if (_login.IsSignedIn)
            ShowShell();
        else
            ShowLogin();

        if (AppInstance.StartupCallback != null)
            _login.Accept(AppInstance.StartupCallback);

        _sessionGuard = SessionGuard.Start(FlushForShutdown);
        // Avalonia Window 没有 OnActivated/OnDeactivated 覆写，走事件
        Activated += (_, _) =>
        {
            if (_session.IsSignedIn) _ = _session.Sync.QuickSyncAsync();
        };
        Deactivated += (_, _) => FlushForShutdown();
        Opened += (_, _) =>
        {
            RestoreGeometry();
            HideTitleBarIcon();
        };
    }

    // 标题栏左上角只显示应用名称：把「小图标」换成全透明图标；
    // 任务栏用的「大图标」不动，仍是应用图标。
    private const uint WmSetIcon = 0x0080;
    private const int IconSmall = 0;
    private static readonly IntPtr NoHandle = IntPtr.Zero;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr CreateIcon(IntPtr instance, int width, int height, byte planes,
        byte bitsPixel, byte[] andBits, byte[] xorBits);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    private void HideTitleBarIcon()
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return;
            var hwnd = TryGetPlatformHandle()?.Handle ?? NoHandle;
            if (hwnd == NoHandle) return;
            const int size = 16;
            var andBits = new byte[size * size / 8];
            Array.Fill(andBits, (byte)0xFF);        // 掩码全 1 = 完全透明
            var xorBits = new byte[size * size / 8]; // 单色位图，全 0
            var blank = CreateIcon(NoHandle, size, size, 1, 1, andBits, xorBits);
            if (blank != NoHandle)
                SendMessage(hwnd, WmSetIcon, (IntPtr)IconSmall, blank);
        }
        catch (Exception error) { DiagnosticLog.Write("app", "隐藏标题栏图标失败：" + error.Message); }
    }

    private sealed class WindowStateData
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public bool Maximized { get; set; }
    }

    private static string GeometryPath => Path.Combine(AppPaths.DefaultRoot(), "window-state.json");

    /// <summary>W-34 / PC-N-02：恢复窗口几何；显示器已拔出则回到屏幕中央。</summary>
    private void RestoreGeometry()
    {
        try
        {
            if (!File.Exists(GeometryPath)) return;
            var state = JsonSerializer.Deserialize<WindowStateData>(File.ReadAllText(GeometryPath));
            if (state == null) return;
            var point = new PixelPoint((int)state.X, (int)state.Y);
            var visible = Screens?.All.Any(screen => screen.WorkingArea.Contains(point)) ?? false;
            if (visible && state.Width >= MinWidth && state.Height >= MinHeight)
            {
                Position = point;
                Width = state.Width;
                Height = state.Height;
            }
            if (state.Maximized) WindowState = WindowState.Maximized;
        }
        catch (Exception error) { DiagnosticLog.Write("app", "恢复窗口位置失败：" + error.Message); }
    }

    private void SaveGeometry()
    {
        try
        {
            var state = new WindowStateData
            {
                X = Position.X, Y = Position.Y, Width = Width, Height = Height,
                Maximized = WindowState == WindowState.Maximized,
            };
            File.WriteAllText(GeometryPath, JsonSerializer.Serialize(state));
        }
        catch (Exception error) { DiagnosticLog.Write("app", "保存窗口位置失败：" + error.Message); }
    }

    /// <summary>W-22：关窗 / 失活 / 关机前把编辑器内容落盘（最多丢 1 秒输入）。</summary>
    public void FlushForShutdown()
    {
        try { _shell?.Flush(); }
        catch (Exception error) { DiagnosticLog.Write("app", "关窗 flush 失败：" + error.Message); }
    }

    private void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        FlushForShutdown();
        SaveGeometry();
        _sessionGuard.Dispose();
        _session.StopPolling();
        _session.Sync.Dispose();
        base.OnClosing(e);
    }
}
