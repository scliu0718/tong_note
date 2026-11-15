using System.Text.Json.Serialization;

namespace Tonote.Core;

/// <summary>预设头像：文字字形 + 渐变底色（颜色以 0xRRGGBB 存储）。</summary>
public sealed record AvatarPreset(string Id, string Glyph, uint Color1, uint Color2)
{
    public string Color1Hex => "#" + Color1.ToString("X6");
    public string Color2Hex => "#" + Color2.ToString("X6");
}

/// <summary>
/// 用户头像设置：预设选择 + 本地持久化 + 变更后上传到 /apps/tong_log/avatar.json。
/// 与笔记/任务同步相互独立，互不影响合并逻辑。
/// </summary>
public sealed class AvatarStore
{
    public static readonly IReadOnlyList<AvatarPreset> Presets = new List<AvatarPreset>
    {
        new("tong", "同", 0x19B46A, 0x0E8A48),
        new("leaf", "叶", 0x34C77B, 0x0E8A48),
        new("note", "记", 0x3AA0FF, 0x0B68D6),
        new("book", "书", 0x8B7BF0, 0x5B45D6),
        new("pen", "笔", 0xF5A623, 0xD97A06),
        new("star", "星", 0xF2C14E, 0xD9930B),
        new("moon", "月", 0x5B7FB5, 0x2F4C7E),
        new("heart", "心", 0xEF7A85, 0xD0465C),
    };

    private readonly string _path;
    private readonly BaiduPanClient _client;
    private readonly DeviceRegistry _registry;

    public AvatarStore(string root, BaiduPanClient? client = null, DeviceRegistry? registry = null)
    {
        _client = client ?? new BaiduPanClient(root);
        _registry = registry ?? new DeviceRegistry(root, _client);
        _path = Path.Combine(root, "avatar-state.json");
        Directory.CreateDirectory(root);
        var state = LoadState();
        CurrentId = Presets.FirstOrDefault(preset => preset.Id == state?.SelectedId)?.Id
                    ?? Presets[0].Id;
        HasLocalChoice = state?.HasLocalChoice ?? false;
    }

    public string CurrentId { get; private set; }

    public AvatarPreset Current => Presets.FirstOrDefault(preset => preset.Id == CurrentId) ?? Presets[0];

    public bool HasLocalChoice { get; private set; }

    public string CloudState { get; private set; } = "idle";

    public string? CloudError { get; private set; }

    public event Action? Changed;

    private static string RemotePath => BaiduPanConfig.RemoteRoot + "/avatar.json";

    /// <summary>选择头像：立即写本地，然后异步上传到云端（未登录/失败不阻塞 UI）。</summary>
    public void Select(string id)
    {
        if (Presets.All(preset => preset.Id != id))
            return;
        CurrentId = id;
        HasLocalChoice = true;
        SaveState();
        Changed?.Invoke();
        _ = UploadToCloudAsync(id);
    }

    /// <summary>登录进入主界面时尝试拉取云端头像（仅当用户本地未做过选择时生效）。</summary>
    public async Task PullFromCloudIfNeededAsync(CancellationToken cancellationToken = default)
    {
        if (HasLocalChoice) return;
        try
        {
            var token = await _client.ValidTokenAsync(cancellationToken).ConfigureAwait(false);
            var data = await _client.DownloadDataAsync(RemotePath, token.AccessToken,
                cancellationToken).ConfigureAwait(false);
            var payload = System.Text.Json.JsonSerializer.Deserialize<AvatarPayload>(
                System.Text.Encoding.UTF8.GetString(data));
            if (payload?.Id != null && Presets.Any(preset => preset.Id == payload.Id))
            {
                CurrentId = payload.Id;
                Changed?.Invoke();
            }
        }
        catch
        {
            // 云端尚无头像或网络异常：保持默认，不打扰用户
        }
    }

    private async Task UploadToCloudAsync(string id)
    {
        CloudState = "uploading";
        CloudError = null;
        Changed?.Invoke();
        try
        {
            var token = await _client.ValidTokenAsync().ConfigureAwait(false);
            await _registry.EnsureNotKickedAsync(token.AccessToken).ConfigureAwait(false);
            var payload = System.Text.Encoding.UTF8.GetBytes("{\"id\":\"" + id + "\"}");
            await _client.UploadDataAsync(payload, RemotePath, token.AccessToken).ConfigureAwait(false);
            CloudState = "synced";
        }
        catch (Exception error)
        {
            CloudState = "failed";
            CloudError = error.Message;
        }
        Changed?.Invoke();
    }

    private AvatarState? LoadState()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            return TonoteJson.Deserialize<AvatarState>(File.ReadAllText(_path));
        }
        catch
        {
            return null;
        }
    }

    private void SaveState()
        => TonoteJson.WriteAtomic(_path, TonoteJson.Serialize(new AvatarState
        {
            SelectedId = CurrentId,
            HasLocalChoice = HasLocalChoice,
        }));

    private sealed class AvatarState
    {
        [JsonPropertyName("selectedId")]
        public string? SelectedId { get; set; }

        [JsonPropertyName("hasLocalChoice")]
        public bool HasLocalChoice { get; set; }
    }

    private sealed class AvatarPayload
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }
    }
}
