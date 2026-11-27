using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Tonote.Core;

/// <summary>本机对照表的一行：上次同步成功时云端的版本和时间，以及这篇是否还有未上传的修改。</summary>
public sealed class SyncCursor
{
    [JsonPropertyName("baseRev")]
    public int BaseRev { get; set; }

    [JsonPropertyName("baseUpdatedAt")]
    public DateTime? BaseUpdatedAt { get; set; }

    [JsonPropertyName("serverMtime")]
    public long? ServerMtime { get; set; }

    [JsonPropertyName("dirty")]
    public bool Dirty { get; set; } = true;

    [JsonPropertyName("uploadedAttachments")]
    public List<string> UploadedAttachments { get; set; } = [];

    public static SyncCursor Unsynced() => new();

    public SyncCursor Clone() => new()
    {
        BaseRev = BaseRev,
        BaseUpdatedAt = BaseUpdatedAt,
        ServerMtime = ServerMtime,
        Dirty = Dirty,
        UploadedAttachments = [.. UploadedAttachments],
    };
}

public enum SyncStepKind
{
    Skip,
    RememberMtime,
    UseRemote,
    Upload,
    Conflict,
}

public readonly record struct SyncStep(SyncStepKind Kind, int Rev = 0)
{
    public static SyncStep Skip => new(SyncStepKind.Skip);
    public static SyncStep RememberMtime => new(SyncStepKind.RememberMtime);
    public static SyncStep UseRemote => new(SyncStepKind.UseRemote);
    public static SyncStep Upload(int rev) => new(SyncStepKind.Upload, rev);
    public static SyncStep Conflict => new(SyncStepKind.Conflict);
}

/// <summary>三方合并规则：rev 判断云端是否变化，updatedAt 定胜负，cursor 记基线。</summary>
public static class SyncRules
{
    public static bool SameTime(DateTime lhs, DateTime rhs)
        => Math.Abs((lhs.ToUniversalTime() - rhs.ToUniversalTime()).TotalSeconds) < 1;

    /// <summary>目录项时间没变且本机没有未上传修改时，不必下载正文。</summary>
    public static bool ShouldSkipFetch(long? serverMtime, SyncCursor cursor)
        => !cursor.Dirty && serverMtime != null && cursor.ServerMtime != null
           && serverMtime.Value == cursor.ServerMtime.Value;

    /// <summary>已经拿到云端这篇之后怎么处理。remoteRev == null 表示网盘上还没有这个文件。</summary>
    public static SyncStep AfterFetch(bool dirty, int? remoteRev, DateTime? remoteUpdatedAt,
        DateTime? localUpdatedAt, SyncCursor cursor)
    {
        if (remoteRev == null || remoteUpdatedAt == null)
        {
            var rev = cursor.BaseRev == 0 ? 1 : cursor.BaseRev + (dirty ? 1 : 0);
            return SyncStep.Upload(Math.Max(rev, 1));
        }

        var neverSynced = cursor.BaseRev == 0 && cursor.BaseUpdatedAt == null;
        if (neverSynced)
        {
            // 本机没有这篇，直接收下云端；时间一致也认云端，避免拆成冲突副本
            if (localUpdatedAt == null)
                return SyncStep.UseRemote;
            if (SameTime(localUpdatedAt.Value, remoteUpdatedAt.Value))
                return SyncStep.UseRemote;
            return dirty ? SyncStep.Conflict : SyncStep.UseRemote;
        }

        var changed = remoteRev.Value != cursor.BaseRev
                      || cursor.BaseUpdatedAt == null
                      || !SameTime(remoteUpdatedAt.Value, cursor.BaseUpdatedAt.Value);
        if (!dirty)
            return changed ? SyncStep.UseRemote : SyncStep.RememberMtime;
        if (!changed)
            return SyncStep.Upload(cursor.BaseRev == 0 ? 1 : cursor.BaseRev + 1);
        return SyncStep.Conflict;
    }

    /// <summary>冲突时谁留在原 id 上。返回 true 表示云端这份更新（平局云端胜）。</summary>
    public static bool RemoteWins(DateTime localUpdatedAt, DateTime remoteUpdatedAt)
        => remoteUpdatedAt.ToUniversalTime() >= localUpdatedAt.ToUniversalTime();
}

public static class UploadChunks
{
    public const int ByteSize = 4 * 1024 * 1024;

    public static List<byte[]> Slices(byte[] data)
    {
        if (data.Length == 0)
            return [Array.Empty<byte>()];
        var result = new List<byte[]>();
        var offset = 0;
        while (offset < data.Length)
        {
            var end = Math.Min(offset + ByteSize, data.Length);
            result.Add(data[offset..end]);
            offset = end;
        }

        return result;
    }
}

/// <summary>正文里的附件相对路径提取，去重保序。</summary>
public static class NoteAttachmentIndex
{
    private static readonly Regex Pattern = new(
        @"attachments/[0-9A-Fa-f-]{36}/[0-9A-Fa-f-]{36}\.[A-Za-z0-9]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static List<string> RelativePaths(string? body)
    {
        if (string.IsNullOrEmpty(body))
            return [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (Match match in Pattern.Matches(body))
        {
            if (seen.Add(match.Value))
                result.Add(match.Value);
        }

        return result;
    }
}

public sealed class NotebookCloud
{
    [JsonPropertyName("names")]
    public List<string> Names { get; set; } = [];

    [JsonPropertyName("removed")]
    public List<string> Removed { get; set; } = [];
}

/// <summary>名单取并集，删除记入 removed。内置笔记本不进这份文件。</summary>
public static class NotebookMerge
{
    public static NotebookCloud Merge(IEnumerable<string> localNames,
        IEnumerable<string> localRemoved, NotebookCloud? remote)
    {
        var remoteNames = remote?.Names ?? [];
        var remoteRemoved = remote?.Removed ?? [];
        var removed = localRemoved.Concat(remoteRemoved)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.CurrentCulture)
            .ToList();
        var removedSet = new HashSet<string>(removed, StringComparer.Ordinal);
        var names = localNames.Concat(remoteNames)
            .Where(name => !removedSet.Contains(name))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.CurrentCulture)
            .ToList();
        removed = removed.Where(name => !names.Contains(name)).ToList();
        return new NotebookCloud { Names = names, Removed = removed };
    }
}

/// <summary>对照表落在本机，不上传。markDirty 会发出本地数据变更通知。</summary>
public sealed class SyncCursorStore
{
    private readonly string _path;
    private Dictionary<string, SyncCursor> _cursors = new(StringComparer.OrdinalIgnoreCase);

    public SyncCursorStore(string file)
    {
        _path = file;
        Load();
    }

    /// <summary>笔记本被用户改过（没有逐篇游标），供同步引擎安排上传；同步回写不发这则通知。</summary>
    public static event Action? LocalDataChanged;

    /// <summary>笔记本的改动，用这个方法通知同步引擎。</summary>
    public static void NotifyLocalDataChanged() => LocalDataChanged?.Invoke();

    public SyncCursor Cursor(Guid id)
        => _cursors.TryGetValue(Key(id), out var cursor) ? cursor.Clone() : SyncCursor.Unsynced();

    public void MarkDirty(Guid id)
    {
        var key = Key(id);
        var cursor = _cursors.TryGetValue(key, out var existing)
            ? existing.Clone()
            : SyncCursor.Unsynced();
        cursor.Dirty = true;
        _cursors[key] = cursor;
        Save();
        // 笔记 / 任务的本地改动由各自 Store 的 LocalChanged 事件通知同步引擎，这里只管对照表。
    }

    public void Set(SyncCursor cursor, Guid id)
    {
        _cursors[Key(id)] = cursor.Clone();
        Save();
    }

    private static string Key(Guid id) => id.ToString("D").ToUpperInvariant();

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
                return;
            _cursors = TonoteJson.Deserialize<Dictionary<string, SyncCursor>>(File.ReadAllText(_path))
                       ?? new Dictionary<string, SyncCursor>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            _cursors = new Dictionary<string, SyncCursor>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Save() => TonoteJson.WriteAtomic(_path, TonoteJson.Serialize(_cursors));
}
