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

    /// <summary>W-21：授权进行中，用于禁用登录按钮并拒绝并发点击。</summary>
    public bool IsBusy { get; private set; }

    public event Action? SignedIn;

    public event Action? BusyChanged;

    public event Action<string, bool>? Notice;

    public void Start(string display)
    {
        if (IsBusy) return; // 并发点击 / 重复回调直接拒绝，不覆盖已有 pending state
        if (!BaiduPanConfig.IsConfigured)
        {
            Notify("尚未配置 AppKey / AppSecret，请先在 baidupan.secret.json 中填写。", true);
            return;
        }

        IsBusy = true;
        BusyChanged?.Invoke();
        PendingState = Guid.NewGuid().ToString("D").ToUpperInvariant();
        var url = BaiduPanAuth.AuthorizeUrl(PendingState, display);
        try
        {
            BrowserLauncher.Open(url);
        }
        catch
        {
            PendingState = null;
            EndBusy();
            Notify("无法打开浏览器。", true);
            return;
        }

        Notify(
            display == "page"
                ? "已打开浏览器。请在授权页使用扫码登录，用百度 App 或百度网盘 App 扫一扫，完成后会回到同笔记。"
                : "已打开浏览器。请在页面登录百度账号并同意授权，完成后会回到同笔记。",
            false);
    }

    private void EndBusy()
    {
        IsBusy = false;
        BusyChanged?.Invoke();
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
            EndBusy();
            Notify(callback.Error == "access_denied" ? "已取消登录。" : $"授权被拒绝：{callback.Error}", true);
            return;
        }

        if (string.IsNullOrEmpty(callback.Code))
        {
            EndBusy();
            Notify("授权没有返回授权码。", true);
            return;
        }

        // W-21：state 无条件校验——空值、重复回调、并发回调一律拒绝
        if (string.IsNullOrEmpty(callback.State)
            || string.IsNullOrEmpty(PendingState)
            || !string.Equals(callback.State, PendingState, StringComparison.Ordinal))
        {
            PendingState = null;
            EndBusy();
            Notify("登录回调校验失败（state 不匹配），请重试。", true);
            return;
        }

        PendingState = null;
        try
        {
            var token = await BaiduPanClient.ExchangeCodeAsync(callback.Code);
            _store.Save(token);
            EndBusy();
            SignedIn?.Invoke();
        }
        catch (Exception ex)
        {
            EndBusy();
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
