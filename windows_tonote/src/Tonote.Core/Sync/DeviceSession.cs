using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Tonote.Core;

/// <summary>云端设备文件标明本机这次登录已下线。收到后清除本地令牌并回到登录页。</summary>
public static class DeviceEvents
{
    public static event Action? Kicked;

    public static void RaiseKicked() => Kicked?.Invoke();
}

/// <summary>读完本机设备文件后该怎么做。只有「本次 session 被标下线」才退出登录。</summary>
public enum DeviceSessionDecision
{
    /// <summary>云端记录就是这次登录，且没有下线标记。不要回写，避免冲掉刚被别的设备写上的标记。</summary>
    Unchanged,
    /// <summary>还没有云端文件，或云端是上一次登录。写回当前这次登录。</summary>
    Publish,
    Kicked,
}

/// <summary>/apps/tong_log/devices/&lt;设备编号&gt;.json</summary>
public sealed class DeviceRecord
{
    [JsonPropertyName("device_id")]
    public string DeviceId { get; set; } = "";

    [JsonPropertyName("device_name")]
    public string DeviceName { get; set; } = "";

    [JsonPropertyName("session_id")]
    public string SessionId { get; set; } = "";

    [JsonPropertyName("kicked")]
    public bool Kicked { get; set; }

    public DeviceRecord() { }

    public DeviceRecord(string deviceId, string deviceName, string sessionId, bool kicked)
    {
        DeviceId = deviceId;
        DeviceName = deviceName;
        SessionId = sessionId;
        Kicked = kicked;
    }

    /// <summary>下线只改标记，登录编号保持不变。对方重新登录会换成新编号并把标记改回 false。</summary>
    public DeviceRecord MarkedKicked()
        => new(DeviceId, DeviceName, SessionId, true);

    public static DeviceSessionDecision Decide(string localDeviceId, string localSessionId,
        DeviceRecord? remote)
    {
        if (remote == null || remote.DeviceId != localDeviceId)
            return DeviceSessionDecision.Publish;
        if (remote.Kicked && remote.SessionId == localSessionId)
            return DeviceSessionDecision.Kicked;
        if (!remote.Kicked && remote.SessionId == localSessionId)
            return DeviceSessionDecision.Unchanged;
        return DeviceSessionDecision.Publish;
    }

    private static readonly Regex SafeIdPattern = new("^[A-Za-z0-9-]{8,80}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsSafeDeviceId(string id) => SafeIdPattern.IsMatch(id ?? "");
}

/// <summary>本机设备编号与当前登录编号。设备编号生成后不变；退出登录不删除它。</summary>
public sealed class LocalDeviceIdentity
{
    [JsonPropertyName("device_id")]
    public string DeviceId { get; set; } = "";

    [JsonPropertyName("session_id")]
    public string? SessionId { get; set; }

    [JsonPropertyName("device_name")]
    public string DeviceName { get; set; } = "";
}

public sealed class DeviceIdentityStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public DeviceIdentityStore(string root)
    {
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "device.json");
    }

    public static string CurrentDeviceName()
    {
        var name = Environment.MachineName?.Trim() ?? "";
        return name.Length == 0 ? "Windows" : name;
    }

    public LocalDeviceIdentity? Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return null;
                return TonoteJson.Deserialize<LocalDeviceIdentity>(File.ReadAllText(_path));
            }
            catch
            {
                return null;
            }
        }
    }

    public LocalDeviceIdentity LoadOrCreate(string deviceName)
    {
        var existing = Load();
        if (existing != null && DeviceRecord.IsSafeDeviceId(existing.DeviceId))
            return existing;
        var created = new LocalDeviceIdentity
        {
            DeviceId = Guid.NewGuid().ToString("D").ToUpperInvariant(),
            SessionId = null,
            DeviceName = deviceName,
        };
        Save(created);
        return created;
    }

    /// <summary>开始一次新的登录。设备编号不变。</summary>
    public LocalDeviceIdentity UpdateSession(string sessionId, string deviceName)
    {
        var identity = LoadOrCreate(deviceName);
        identity.SessionId = sessionId;
        identity.DeviceName = deviceName;
        Save(identity);
        return identity;
    }

    public void Save(LocalDeviceIdentity identity)
    {
        lock (_gate)
            TonoteJson.WriteAtomic(_path, TonoteJson.Serialize(identity));
    }
}

/// <summary>云端设备名单。每台设备只写自己的 JSON；下线是把对方文件的 kicked 改成 true。</summary>
public sealed class DeviceRegistry
{
    private readonly DeviceIdentityStore _store;
    private readonly BaiduPanClient _client;

    public DeviceRegistry(string root, BaiduPanClient? client = null)
    {
        _store = new DeviceIdentityStore(root);
        _client = client ?? new BaiduPanClient(root);
    }

    public static string DevicesDirectory => BaiduPanConfig.RemoteRoot + "/devices";

    public static string FilePath(string deviceId) => DevicesDirectory + "/" + deviceId + ".json";

    public string CurrentDeviceId => _store.LoadOrCreate(DeviceIdentityStore.CurrentDeviceName()).DeviceId;

    /// <summary>设备列表还没从云端读到本机时，用本地登记补一行。</summary>
    public DeviceRecord LocalSnapshot()
    {
        var identity = _store.LoadOrCreate(DeviceIdentityStore.CurrentDeviceName());
        return new DeviceRecord(identity.DeviceId, identity.DeviceName, identity.SessionId ?? "", false);
    }

    /// <summary>授权成功后调用：换一个新的登录编号，并覆盖本机那份设备文件。</summary>
    public async Task BeginLoginAsync(CancellationToken cancellationToken = default)
    {
        var token = await _client.ValidTokenAsync(cancellationToken).ConfigureAwait(false);
        var identity = _store.UpdateSession(Guid.NewGuid().ToString("D").ToUpperInvariant(),
            DeviceIdentityStore.CurrentDeviceName());
        var record = new DeviceRecord(identity.DeviceId, identity.DeviceName,
            identity.SessionId ?? "", false);
        await UploadAsync(record, token.AccessToken, cancellationToken).ConfigureAwait(false);
    }

    public async Task EnsureNotKickedAsync(CancellationToken cancellationToken = default)
    {
        var token = await _client.ValidTokenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureNotKickedAsync(token.AccessToken, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>打开应用或上传前调用。网络失败会抛错；文件不存在则补登记。被下线时发通知并抛错。</summary>
    public async Task EnsureNotKickedAsync(string accessToken,
        CancellationToken cancellationToken = default)
    {
        var identity = _store.LoadOrCreate(DeviceIdentityStore.CurrentDeviceName());
        if (string.IsNullOrWhiteSpace(identity.SessionId))
            identity = _store.UpdateSession(Guid.NewGuid().ToString("D").ToUpperInvariant(),
                identity.DeviceName);
        var sessionId = identity.SessionId ?? "";

        DeviceRecord? remote = null;
        try
        {
            var data = await _client.DownloadDataAsync(FilePath(identity.DeviceId), accessToken,
                cancellationToken).ConfigureAwait(false);
            remote = TonoteJson.Deserialize<DeviceRecord>(System.Text.Encoding.UTF8.GetString(data));
        }
        catch (BaiduPanNotFoundException)
        {
            remote = null;
        }

        switch (DeviceRecord.Decide(identity.DeviceId, sessionId, remote))
        {
            case DeviceSessionDecision.Unchanged:
                return;
            case DeviceSessionDecision.Publish:
                await UploadAsync(new DeviceRecord(identity.DeviceId, identity.DeviceName,
                    sessionId, false), accessToken, cancellationToken).ConfigureAwait(false);
                return;
            case DeviceSessionDecision.Kicked:
                DeviceEvents.RaiseKicked();
                throw new DeviceKickedException();
        }
    }

    public async Task<List<DeviceRecord>> ListDevicesAsync(CancellationToken cancellationToken = default)
    {
        var token = await _client.ValidTokenAsync(cancellationToken).ConfigureAwait(false);
        List<BaiduPanListEntry> entries;
        try
        {
            entries = await _client.ListFilesAsync(DevicesDirectory, token.AccessToken,
                cancellationToken).ConfigureAwait(false);
        }
        catch (BaiduPanNotFoundException)
        {
            return [];
        }

        var records = new List<DeviceRecord>();
        foreach (var entry in entries)
        {
            var name = entry.Name;
            if (entry.IsDir == 1 || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                continue;
            var path = entry.Path ?? DevicesDirectory + "/" + name;
            try
            {
                var data = await _client.DownloadDataAsync(path, token.AccessToken,
                    cancellationToken).ConfigureAwait(false);
                var record = TonoteJson.Deserialize<DeviceRecord>(System.Text.Encoding.UTF8.GetString(data));
                if (record == null || !DeviceRecord.IsSafeDeviceId(record.DeviceId))
                    continue;
                records.Add(record);
            }
            catch (BaiduPanNotFoundException)
            {
                // 文件刚好被对方删掉，跳过
            }
        }

        var mine = CurrentDeviceId;
        return records
            .OrderBy(record => record.DeviceId == mine ? 0 : 1)
            .ThenBy(record => record.DeviceName, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>把另一台设备的文件标成下线。不改它的登录编号，也不写本机文件。</summary>
    public async Task KickAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        if (!DeviceRecord.IsSafeDeviceId(deviceId))
            throw new BaiduPanException("设备编号无效");
        var mine = _store.LoadOrCreate(DeviceIdentityStore.CurrentDeviceName());
        if (deviceId == mine.DeviceId)
            return;
        var token = await _client.ValidTokenAsync(cancellationToken).ConfigureAwait(false);
        var data = await _client.DownloadDataAsync(FilePath(deviceId), token.AccessToken,
            cancellationToken).ConfigureAwait(false);
        var remote = TonoteJson.Deserialize<DeviceRecord>(System.Text.Encoding.UTF8.GetString(data))
                     ?? throw new BaiduPanException("设备文件解析失败");
        if (remote.DeviceId != deviceId)
            throw new BaiduPanException("设备文件与编号不一致");
        if (remote.Kicked)
            return;
        await UploadAsync(remote.MarkedKicked(), token.AccessToken, cancellationToken)
            .ConfigureAwait(false);
    }

    private Task UploadAsync(DeviceRecord record, string accessToken,
        CancellationToken cancellationToken)
    {
        var payload = System.Text.Encoding.UTF8.GetBytes(TonoteJson.Serialize(record));
        return _client.UploadDataAsync(payload, FilePath(record.DeviceId), accessToken,
            cancellationToken);
    }
}
