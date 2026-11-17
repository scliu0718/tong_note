using Tonote.Core;

namespace Tonote.App;

/// <summary>
/// 一次登录期间的共享状态。三个本地仓库 + 网盘客户端 + 同步引擎 + 设备/头像状态。
/// 与 Mac 版一致：同步引擎只在登录成功后启动，本机数据始终可离线编辑。
/// </summary>
public sealed class AppSession
{
    private bool _syncStarted;
    private CancellationTokenSource? _pollCancellation;

    public AppSession(string? root = null)
    {
        Root = root ?? AppPaths.DefaultRoot();
        Notes = new NoteStore(Root);
        Todos = new TodoStore(Root);
        Notebooks = new NotebookStore(Root);
        Pan = new BaiduPanClient(Root);
        Devices = new DeviceRegistry(Root, Pan);
        Avatar = new AvatarStore(Root, Pan, Devices);
        Sync = new SyncEngine(Notes, Todos, Notebooks, Root, Pan);
    }

    public string Root { get; }
    public NoteStore Notes { get; }
    public TodoStore Todos { get; }
    public NotebookStore Notebooks { get; }
    public BaiduPanClient Pan { get; }
    public DeviceRegistry Devices { get; }
    public AvatarStore Avatar { get; }
    public SyncEngine Sync { get; }

    public bool IsSignedIn => Pan.CurrentToken != null;

    /// <summary>登录成功后调用：开始防抖监听、登记本次设备、拉云端头像、跑一轮同步。</summary>
    public void StartSync()
    {
        if (_syncStarted) return;
        _syncStarted = true;
        Sync.Start();
        _ = AfterSignInAsync();
        StartPolling();
    }

    /// <summary>
    /// Mac MainView.swift:120-127 的 45 秒轮询：本地改动之外，定期拉取其它设备的变更。
    /// 单轮异常不影响后续轮次，状态由 SyncEngine 的状态机落盘。
    /// </summary>
    private void StartPolling()
    {
        _pollCancellation = new CancellationTokenSource();
        var token = _pollCancellation.Token;
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(45));
            try
            {
                while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    try { await Sync.QuickSyncAsync().ConfigureAwait(false); }
                    catch { /* 网络瞬态由下一轮重试 */ }
                }
            }
            catch (OperationCanceledException) { }
        }, token);
    }

    public void StopPolling()
    {
        _pollCancellation?.Cancel();
        _pollCancellation?.Dispose();
        _pollCancellation = null;
    }

    private async Task AfterSignInAsync()
    {
        try
        {
            await Devices.BeginLoginAsync().ConfigureAwait(false);
        }
        catch
        {
            // 设备登记失败不阻塞笔记同步，下一轮 ensureNotKicked 会补登记
        }

        await Avatar.PullFromCloudIfNeededAsync().ConfigureAwait(false);
        await Sync.QuickSyncAsync().ConfigureAwait(false);
    }

    public void Logout()
    {
        Pan.Logout();
    }
}
