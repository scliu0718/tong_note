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
            SetTitleBarIcon();
        };
    }

    // 标题栏左上角：把「小图标」换成一支笔的矢量图标（渲染成 32×32 PNG 再设为小图标）；
    // 任务栏用的「大图标」不动，仍是应用图标。
    private const uint WmSetIcon = 0x0080;
    private const int IconSmall = 0;
    private static readonly IntPtr NoHandle = IntPtr.Zero;

    private const string PenPath =
        "M659.655431 521.588015q23.970037-6.71161 46.022472-13.423221 19.17603-5.752809 39.310861-11.505618t33.558052-10.546816l-13.423221 50.816479q-5.752809 21.093633-10.546816 31.640449-9.588015 25.88764-22.531835 47.940075t-24.449438 38.35206q-13.423221 19.17603-27.805243 35.475655l-117.932584 35.475655 96.838951 17.258427q-19.17603 16.299625-41.228464 33.558052-19.17603 14.382022-43.625468 30.202247t-51.29588 29.243446-59.925094 13.902622-62.801498-4.314607q-34.516854-4.794007-69.033708-16.299625 10.546816-16.299625 23.011236-36.434457 10.546816-17.258427 25.40824-40.749064t31.161049-52.254682q46.022472-77.662921 89.168539-152.449438t77.662921-135.191011q39.310861-69.992509 75.745318-132.314607-45.06367 51.775281-94.921348 116.014981-43.146067 54.651685-95.88015 129.917603t-107.385768 164.434457q-11.505618 18.217228-25.88764 42.187266t-30.202247 50.816479-32.599251 55.131086-33.078652 55.131086q-38.35206 62.322097-78.621723 130.397004 0.958801-20.134831 7.670412-51.775281 5.752809-26.846442 19.17603-67.116105t38.35206-94.921348q16.299625-34.516854 24.928839-53.692884t13.423221-29.722846q4.794007-11.505618 7.670412-15.340824-4.794007-5.752809-1.917603-23.011236 1.917603-15.340824 11.026217-44.58427t31.161049-81.977528q22.052434-53.692884 58.007491-115.535581t81.018727-122.726592 97.797753-117.932584 107.865169-101.153558 110.262172-72.389513 106.906367-32.11985q0.958801 33.558052-6.71161 88.689139t-19.17603 117.932584-25.88764 127.520599-27.805243 117.453184z";

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr CreateIconFromResourceEx(byte[] bits, uint size, bool icon,
        uint version, int width, int height, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    private void SetTitleBarIcon()
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return;
            var hwnd = TryGetPlatformHandle()?.Handle ?? NoHandle;
            if (hwnd == NoHandle) return;

            const int size = 32;
            var pen = new Avalonia.Controls.Shapes.Path
            {
                Data = Avalonia.Media.Geometry.Parse(PenPath),
                Fill = new SolidColorBrush(Color.Parse("#8A8A8A")),
                Stretch = Stretch.Uniform,
                Width = size - 4,
                Height = size - 4,
                Margin = new Thickness(2),
            };
            pen.Measure(new Size(size, size));
            pen.Arrange(new Rect(0, 0, size, size));
            using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(
                new PixelSize(size, size), new Vector(96, 96));
            bitmap.Render(pen);
            using var stream = new MemoryStream();
            bitmap.Save(stream);
            var png = stream.ToArray();

            // PNG 数据直接创建图标（Vista 及以上支持）
            var icon = CreateIconFromResourceEx(png, (uint)png.Length, true, 0x00030000, size, size, 0);
            if (icon != NoHandle)
                SendMessage(hwnd, WmSetIcon, (IntPtr)IconSmall, icon);
        }
        catch (Exception error) { DiagnosticLog.Write("app", "设置标题栏图标失败：" + error.Message); }
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
