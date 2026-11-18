using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Microsoft.Web.WebView2.Core;

namespace Tonote.App;

/// <summary>
/// Avalonia 里承载 WebView2：NativeControlHost 建一个 Win32 子窗口，
/// 再用 CoreWebView2Controller 把浏览器挂上去。不依赖 WPF / WinForms 控件。
/// </summary>
public sealed class WebView2Host : NativeControlHost
{
    private readonly string _userDataFolder;
    private IntPtr _hwnd;
    private CoreWebView2Controller? _controller;
    private CoreWebView2? _webView;
    private string? _pendingUrl;
    private bool _ready;

    public WebView2Host(string userDataFolder)
    {
        _userDataFolder = userDataFolder;
        PropertyChanged += (_, args) =>
        {
            if (args.Property == BoundsProperty) Resize();
        };
        LayoutUpdated += (_, _) => Resize();
    }

    public bool IsReady => _ready;

    public CoreWebView2? WebView => _webView;

    public event Action<string>? WebMessageReceived;

    public event Action? Ready;

    public event Action<string>? Failed;

    /// <summary>等待浏览器初始化完成后再导航；未完成就先记下来。</summary>
    public void Navigate(string url)
    {
        if (_ready && _webView != null)
            _webView.Navigate(url);
        else
            _pendingUrl = url;
    }

    public Task<string?> ExecuteScriptAsync(string script)
        => _webView == null ? Task.FromResult<string?>(null) : _webView.ExecuteScriptAsync(script);

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (!OperatingSystem.IsWindows())
            return base.CreateNativeControlCore(parent);

        _hwnd = Win32.CreateChildWindow(parent.Handle);
        if (_hwnd == IntPtr.Zero)
        {
            Failed?.Invoke("无法创建 WebView2 宿主窗口");
            return base.CreateNativeControlCore(parent);
        }

        _ = InitializeAsync();
        return new PlatformHandle(_hwnd, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        _controller?.Close();
        _controller = null;
        _webView = null;
        _ready = false;
        if (_hwnd != IntPtr.Zero)
        {
            Win32.DestroyChildWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
        base.DestroyNativeControlCore(control);
    }

    private async Task InitializeAsync()
    {
        try
        {
            Directory.CreateDirectory(_userDataFolder);
            var environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: _userDataFolder,
                options: new CoreWebView2EnvironmentOptions());
            _controller = await environment.CreateCoreWebView2ControllerAsync(_hwnd);
            _webView = _controller.CoreWebView2;
            _webView.Settings.IsWebMessageEnabled = true;
            _webView.Settings.IsStatusBarEnabled = false;
            _webView.Settings.AreDefaultContextMenusEnabled = true;
            // W-18：编辑器内的外链绝不在 WebView 里导航，改成系统浏览器打开。
            // 编辑器自身用 file:///…/editor.html 加载，这部分放行。
            _webView.NavigationStarting += (_, args) =>
            {
                var uri = args.Uri ?? "";
                if (uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                    || uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                    || uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
                    return;
                args.Cancel = true;
                if (!RichTextEditor.IsSafeUrl(uri))
                {
                    DiagnosticLog.Write("editor", "已拦截编辑器内不安全导航：" + uri);
                    return;
                }
                try { BrowserLauncher.Open(uri); }
                catch (Exception error) { DiagnosticLog.Write("editor", "打开外链失败：" + error.Message); }
            };
            _webView.WebMessageReceived += (_, args) =>
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => WebMessageReceived?.Invoke(args.WebMessageAsJson));
            Resize();
            _controller.IsVisible = true;
            _ready = true;
            Ready?.Invoke();
            if (_pendingUrl != null)
            {
                _webView.Navigate(_pendingUrl);
                _pendingUrl = null;
            }
        }
        catch (Exception error)
        {
            Failed?.Invoke(error.Message);
        }
    }

    private void Resize()
    {
        if (_controller == null || _hwnd == IntPtr.Zero)
            return;
        if (!Win32.ClientRect(_hwnd, out var rect))
            return;
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
            return;
        try
        {
            _controller.Bounds = new Rectangle(0, 0, width, height);
        }
        catch
        {
            // 窗口正在销毁时会抛，忽略
        }
    }

    private static class Win32
    {
        private const int WsChild = 0x40000000;
        private const int WsVisible = 0x10000000;
        private const int WsClipChildren = 0x02000000;
        private const int WsClipSiblings = 0x04000000;

        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowExW(
            int exStyle, string className, string windowName, int style,
            int x, int y, int width, int height,
            IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyWindow(IntPtr window);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetClientRect(IntPtr window, out Rect rect);

        public static IntPtr CreateChildWindow(IntPtr parent)
            => CreateWindowExW(0, "static", "",
                WsChild | WsVisible | WsClipChildren | WsClipSiblings,
                0, 0, 1, 1, parent, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        public static void DestroyChildWindow(IntPtr window) => DestroyWindow(window);

        public static bool ClientRect(IntPtr window, out Rect rect) => GetClientRect(window, out rect);
    }
}
