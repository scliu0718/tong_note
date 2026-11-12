using Avalonia.Threading;
using Tonote.Core;

namespace Tonote.App;

/// <summary>
/// 账号登录打开 display=popup，扫码登录打开 display=page。两边都等 tongnote:// 回调。
/// </summary>
public sealed class BaiduLogin
{
    private readonly BaiduPanTokenStore _store;

    public BaiduLogin(BaiduPanTokenStore store) => _store = store;

    public string? PendingState { get; private set; }

    public bool IsSignedIn => _store.Load() != null;

    public event Action? SignedIn;

    public event Action<string, bool>? Notice;

    public void Start(string display)
    {
        if (!BaiduPanConfig.IsConfigured)
        {
            Notify("尚未配置 AppKey / AppSecret。请把 baidupan.secret.json 放在 windows_tonote 目录后重新打开。", true);
            return;
        }

        PendingState = Guid.NewGuid().ToString("D").ToUpperInvariant();
        var url = BaiduPanAuth.AuthorizeUrl(PendingState, display);
        try
        {
            BrowserLauncher.Open(url);
        }
        catch
        {
            PendingState = null;
            Notify("无法打开浏览器。", true);
            return;
        }

        Notify(
            display == "page"
                ? "已打开浏览器。请在授权页使用扫码登录，用百度 App 或百度网盘 App 扫一扫，完成后会回到同笔记。"
                : "已打开浏览器。请在页面登录百度账号并同意授权，完成后会回到同笔记。",
            false);
    }

    public void Accept(string raw)
        => _ = Dispatcher.UIThread.InvokeAsync(() => HandleAsync(raw));

    private async Task HandleAsync(string raw)
    {
        if (!BaiduPanAuth.TryParseCallback(raw, out var callback))
        {
            Notify("登录回调无法识别。", true);
            return;
        }

        if (!string.IsNullOrEmpty(callback.Error))
        {
            PendingState = null;
            Notify(callback.Error == "access_denied" ? "已取消登录。" : $"授权被拒绝：{callback.Error}", true);
            return;
        }

        if (string.IsNullOrEmpty(callback.Code))
        {
            Notify("授权没有返回授权码。", true);
            return;
        }

        if (PendingState != null && !string.Equals(callback.State, PendingState, StringComparison.Ordinal))
        {
            PendingState = null;
            Notify("登录回调校验失败，请重试。", true);
            return;
        }

        PendingState = null;
        try
        {
            var token = await BaiduPanClient.ExchangeCodeAsync(callback.Code);
            _store.Save(token);
            SignedIn?.Invoke();
        }
        catch (Exception ex)
        {
            Notify(string.IsNullOrWhiteSpace(ex.Message) ? "登录失败。" : ex.Message, true);
        }
    }

    private void Notify(string text, bool error)
    {
        if (Dispatcher.UIThread.CheckAccess())
            Notice?.Invoke(text, error);
        else
            Dispatcher.UIThread.Post(() => Notice?.Invoke(text, error));
    }
}
