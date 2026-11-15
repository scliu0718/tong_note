using Tonote.Core;

namespace Tonote.App;

/// <summary>
/// 一次登录期间的共享状态。三个本地仓库 + 网盘客户端 + 同步引擎 + 设备/头像状态。
/// 与 Mac 版一致：同步引擎只在登录成功后启动，本机数据始终可离线编辑。
/// </summary>
public sealed class AppSession
{
    private bool _syncStarted;

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
