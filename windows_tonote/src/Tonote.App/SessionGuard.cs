using System.Runtime.InteropServices;

namespace Tonote.App;

/// <summary>
/// W-22 / W-23：用一条只收消息的隐藏 Win32 窗口接收 WM_QUERYENDSESSION / WM_ENDSESSION，
/// 关机、注销、远程会话断开前同步落盘。不改变 Avalonia 主窗口的消息循环。
/// </summary>
internal sealed class SessionGuard : IDisposable
{
    private const uint WmQueryEndSession = 0x0011;
    private const uint WmEndSession = 0x0016;
    private const IntPtr HwndMessage = -3;

    private static readonly SessionGuard?[] Instance = new SessionGuard?[1];
    private static readonly WndProc Proc = Handle;

    private readonly Action _onEnding;
    private readonly Thread _thread;
    private IntPtr _hwnd;

    private SessionGuard(Action onEnding)
    {
        _onEnding = onEnding;
        Instance[0] = this;
        _thread = new Thread(Run) { IsBackground = true, Name = "TonoteSessionGuard" };
        _thread.Start();
    }

    public static SessionGuard Start(Action onEnding) => new(onEnding);

    private void Run()
    {
        var className = "TonoteSessionGuard" + Guid.NewGuid().ToString("N");
        var wc = new WndClassEx
        {
            cbSize = Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = Proc,
            lpszClassName = className,
        };
        if (RegisterClassExW(ref wc) == IntPtr.Zero) return;
        _hwnd = CreateWindowExW(0, className, "", 0, 0, 0, 0, 0, HwndMessage,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) return;

        var msg = new Msg();
        while (GetMessageW(out msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    private static IntPtr Handle(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message is WmQueryEndSession or WmEndSession)
        {
            try
            {
                DiagnosticLog.Write("app", "收到系统结束会话通知，正在保存未落盘内容");
                Instance[0]?._onEnding();
            }
            catch (Exception error)
            {
                DiagnosticLog.Write("app", "关机前保存失败：" + error.Message);
            }
            return new IntPtr(1);
        }
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero) PostQuitMessage(0);
        Instance[0] = null;
    }

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public int cbSize;
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string? lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr RegisterClassExW(ref WndClassEx wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(int exStyle, string className, string windowName,
        int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu,
        IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern sbyte GetMessageW(out Msg msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref Msg msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);
}
