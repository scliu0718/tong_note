using System.Text;
using System.Text.Json.Serialization;

namespace Tonote.Core;

public enum SyncStatusKind
{
    Idle,
    Syncing,
    Success,
    Failed,
}

public sealed class SyncStatus
{
    public SyncStatusKind Kind { get; init; } = SyncStatusKind.Idle;
    public string? Message { get; init; }
    public DateTime? Date { get; init; }
    public bool IsSyncing => Kind == SyncStatusKind.Syncing;

    public static SyncStatus Idle() => new() { Kind = SyncStatusKind.Idle };
    public static SyncStatus Syncing() => new() { Kind = SyncStatusKind.Syncing };
    public static SyncStatus Succeeded(string message, DateTime date)
        => new() { Kind = SyncStatusKind.Success, Message = message, Date = date };
    public static SyncStatus Failed(string message)
        => new() { Kind = SyncStatusKind.Failed, Message = message };
}

/// <summary>
/// 按篇同步：每篇笔记 / 每条任务一个云端文件。启动、保存后、手动触发都会跑同一轮。
/// 与 Mac 版 SyncEngine 逐条对齐：1.5s 防抖、重入标记、三次发布重试、冲突副本、旧清单迁移。
/// 失败只留到下一轮，不因此退出登录。
/// </summary>
public sealed class SyncEngine : IDisposable
{
    private const int DebounceMilliseconds = 1500;
    private const int PublishRetries = 3;

    private readonly NoteStore _notes;
    private readonly TodoStore _todos;
    private readonly NotebookStore _notebooks;
    private readonly BaiduPanClient _client;
    private readonly DeviceRegistry _registry;
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _debounce;
    private bool _needsAnotherRound;
    private bool _disposed;

    public SyncEngine(NoteStore notes, TodoStore todos, NotebookStore notebooks, string root,
        BaiduPanClient? client = null)
    {
        _notes = notes;
        _todos = todos;
        _notebooks = notebooks;
        _root = root;
        _client = client ?? new BaiduPanClient(root);
        _registry = new DeviceRegistry(root, _client);
        _lastSyncPath = Path.Combine(root, "last-sync.json");
        _statusPath = Path.Combine(root, "sync-status.json");
        LastSyncDate = LoadLastSync();
    }

    private readonly string _lastSyncPath;
    private readonly string _statusPath;

    public SyncStatus Status { get; private set; } = SyncStatus.Idle();

    public DateTime? LastSyncDate { get; private set; }

    public string? LastMessage { get; private set; }

    public event Action? StatusChanged;

    public event Action? Kicked;

    public BaiduPanClient Client => _client;

    public DeviceRegistry Registry => _registry;

    /// <summary>订阅本地数据变更，用户改一点就防抖同步。</summary>
    public void Start()
    {
        SyncCursorStore.LocalDataChanged += OnLocalDataChanged;
        DeviceEvents.Kicked += OnDeviceKicked;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SyncCursorStore.LocalDataChanged -= OnLocalDataChanged;
        DeviceEvents.Kicked -= OnDeviceKicked;
        _debounce?.Cancel();
        _debounce?.Dispose();
        _gate.Dispose();
    }

    private void OnDeviceKicked()
    {
        _client.Logout();
        Kicked?.Invoke();
    }

    private void OnLocalDataChanged() => Schedule();

    private void SetStatus(SyncStatus status)
    {
        Status = status;
        LastMessage = status.Message;
        // 状态落盘：GUI 进程退出/被关闭后仍能诊断「上一轮为什么没同步成功」，
        // 也便于真实网盘的自动化功能测试从外部观察同步结果。
        try
        {
            TonoteJson.WriteAtomic(_statusPath, TonoteJson.Serialize(new StatusSnapshot
            {
                Kind = status.Kind.ToString(),
                Message = status.Message,
                Date = status.Date ?? LastSyncDate,
            }));
        }
        catch
        {
            // 状态文件只是可观测性增强，写失败绝不影响同步
        }
        // 逐个隔离订阅者：界面层事件处理器一旦抛异常，不能把后台同步轮
        // 变成「未观察异常」而静默停在 Syncing 状态。
        if (StatusChanged is { } handlers)
        {
            foreach (Action handler in handlers.GetInvocationList())
            {
                try { handler(); }
                catch { /* 界面刷新失败不影响数据同步 */ }
            }
        }
    }

    private sealed class StatusSnapshot
    {
        [JsonPropertyName("kind")]
        public string Kind { get; set; } = "";

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("date")]
        public DateTime? Date { get; set; }
    }

    /// <summary>保存后一两秒再同步。连续保存只跑最后一轮。</summary>
    public void Schedule()
    {
        if (_disposed) return;
        _debounce?.Cancel();
        _debounce?.Dispose();
        _debounce = new CancellationTokenSource();
        var token = _debounce.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DebounceMilliseconds, token).ConfigureAwait(false);
                if (!token.IsCancellationRequested)
                    await QuickSyncAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 被新的保存打断，正常
            }
        }, token);
    }

    public Task QuickSyncAsync(CancellationToken cancellationToken = default)
        => SyncRoundAsync(null, cancellationToken);

    public Task SyncSingleNoteAsync(Guid id, CancellationToken cancellationToken = default)
        => SyncRoundAsync(id, cancellationToken);

    private async Task SyncRoundAsync(Guid? onlyNoteId, CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            _needsAnotherRound = true;
            return;
        }

        try
        {
            var round = 0;
            while (true)
            {
                round++;
                if (round > 4) break; // 防止异常情况下无限重入
                var kicked = false;
                SetStatus(SyncStatus.Syncing());
                try
                {
                    var token = await _client.ValidTokenAsync(cancellationToken).ConfigureAwait(false);
                    await _registry.EnsureNotKickedAsync(token.AccessToken, cancellationToken)
                        .ConfigureAwait(false);
                    var noteCount = await SyncNotesAsync(token.AccessToken, onlyNoteId, cancellationToken)
                        .ConfigureAwait(false);
                    var todoCount = 0;
                    if (onlyNoteId == null)
                    {
                        await SyncNotebooksAsync(token.AccessToken, cancellationToken).ConfigureAwait(false);
                        todoCount = await SyncTodosAsync(token.AccessToken, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    var now = DateTime.Now;
                    LastSyncDate = now;
                    SaveLastSync(now);
                    SetStatus(SyncStatus.Succeeded(
                        onlyNoteId == null
                            ? $"笔记 {noteCount} 篇 · 任务 {todoCount} 条"
                            : "已同步本篇", now));
                }
                catch (DeviceKickedException error)
                {
                    kicked = true;
                    DeviceEvents.RaiseKicked();
                    SetStatus(SyncStatus.Failed(error.Message));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    SetStatus(SyncStatus.Idle());
                    break;
                }
                catch (Exception error)
                {
                    SetStatus(SyncStatus.Failed(error.Message));
                    // 代理 502 / 连接重置 / 请求超时都是瞬态，自动再跑一轮；
                    // while 循环本身有 4 轮上限，不会形成无限重试。
                    if (round < 4 && IsTransient(error))
                        _needsAnotherRound = true;
                }

                var again = _needsAnotherRound && !kicked;
                _needsAnotherRound = false;
                if (!again) break;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>网络类瞬态故障才自动重试；数据/契约错误立即暴露，不掩盖问题。</summary>
    private static bool IsTransient(Exception error)
    {
        if (error is HttpRequestException or TaskCanceledException or TimeoutException)
            return true;
        if (error is not BaiduPanException)
            return false;
        var message = error.Message;
        return message.Contains("请求失败", StringComparison.Ordinal)
               || message.Contains("请求超时", StringComparison.Ordinal)
               || message.Contains("proxy", StringComparison.OrdinalIgnoreCase)
               || message.Contains("timed out", StringComparison.OrdinalIgnoreCase)
               || message.Contains("502", StringComparison.Ordinal)
               || message.Contains("503", StringComparison.Ordinal)
               || message.Contains("504", StringComparison.Ordinal);
    }

    // MARK: - 笔记

    private async Task<int> SyncNotesAsync(string accessToken, Guid? onlyNoteId,
        CancellationToken cancellationToken)
    {
        await MigrateNotesIfNeededAsync(accessToken, cancellationToken).ConfigureAwait(false);
        var remoteFiles = await ListJsonAsync(NotesDirectory, accessToken, cancellationToken)
            .ConfigureAwait(false);
        var localIds = _notes.Notes
            .Where(note => !note.IsQuickSync || onlyNoteId == note.Id)
            .Select(note => note.Id).ToHashSet();
        var remoteIds = remoteFiles.Keys.ToHashSet();
        if (onlyNoteId != null)
        {
            remoteIds = remoteIds.Contains(onlyNoteId.Value) ? [onlyNoteId.Value] : [];
            localIds = localIds.Contains(onlyNoteId.Value) ? [onlyNoteId.Value] : [];
        }

        var changed = 0;
        Exception? firstError = null;
        foreach (var id in remoteIds.Union(localIds))
        {
            try
            {
                if (await SyncOneNoteAsync(id, remoteFiles.GetValueOrDefault(id), accessToken,
                        cancellationToken).ConfigureAwait(false))
                    changed++;
            }
            catch (DeviceKickedException) { throw; }
            catch (Exception error) { firstError ??= error; }
        }
        if (firstError != null) throw firstError;
        return changed;
    }

    /// <summary>notes/ 还是空的、旧清单还在时，把清单拆成按篇文件。之后不再写清单。</summary>
    private async Task MigrateNotesIfNeededAsync(string accessToken, CancellationToken cancellationToken)
    {
        var existing = await ListJsonAsync(NotesDirectory, accessToken, cancellationToken)
            .ConfigureAwait(false);
        if (existing.Count > 0) return;
        var manifest = await PullManifestAsync(accessToken, cancellationToken).ConfigureAwait(false);
        var remote = manifest?.Notes ?? [];
        if (remote.Count == 0) return;
        var merged = MergeByUpdatedAt(_notes.Notes.Where(note => !note.IsQuickSync), remote);
        foreach (var item in merged)
        {
            var publishing = Note.CopyOf(item);
            publishing.Rev = Math.Max(publishing.Rev, 1);
            var cursor = _notes.Cursor(publishing.Id);
            await UploadAttachmentsAsync(publishing, publishing.Body, _notes.Attachments,
                cursor, _notes.Find(publishing.Id), accessToken, cancellationToken).ConfigureAwait(false);
            await UploadNoteAsync(publishing, accessToken, cancellationToken).ConfigureAwait(false);
            CopyCursorFrom(cursor, new SyncCursor
            {
                BaseRev = publishing.Rev,
                BaseUpdatedAt = publishing.UpdatedAt,
                Dirty = false,
                ServerMtime = null,
                UploadedAttachments = [.. cursor.UploadedAttachments],
            });
            _notes.ApplySyncedNote(publishing, cursor);
        }
    }

    private async Task<bool> SyncOneNoteAsync(Guid id, BaiduPanListEntry? listing,
        string accessToken, CancellationToken cancellationToken)
    {
        var local = _notes.Find(id);
        var cursor = local == null ? SyncCursor.Unsynced() : _notes.Cursor(id);
        if (listing?.ServerMtime != null
            && SyncRules.ShouldSkipFetch(listing.ServerMtime, cursor))
            return false;

        var remote = await DownloadNoteAsync(id, accessToken, cancellationToken).ConfigureAwait(false);
        var step = SyncRules.AfterFetch(cursor.Dirty, remote?.Rev, remote?.UpdatedAt,
            local?.UpdatedAt, cursor);
        switch (step.Kind)
        {
            case SyncStepKind.Skip:
                return false;
            case SyncStepKind.RememberMtime:
                if (local == null) return false;
                cursor.ServerMtime = listing?.ServerMtime;
                cursor.Dirty = false;
                _notes.ApplySyncedNote(local, cursor);
                return false;
            case SyncStepKind.UseRemote:
                if (remote == null) return false;
                CopyCursorFrom(cursor, new SyncCursor
                {
                    BaseRev = remote.Rev,
                    BaseUpdatedAt = remote.UpdatedAt,
                    ServerMtime = listing?.ServerMtime,
                    Dirty = false,
                    UploadedAttachments = [.. cursor.UploadedAttachments],
                });
                if (!await DownloadAttachmentsAsync(remote.Body, _notes.Attachments, cursor,
                        accessToken, cancellationToken).ConfigureAwait(false))
                    cursor.ServerMtime = null;
                _notes.ApplySyncedNote(remote, cursor);
                return true;
            case SyncStepKind.Upload:
                if (local == null) return false;
                return await PublishNoteAsync(local, step.Rev, cursor, listing?.ServerMtime,
                    accessToken, cancellationToken).ConfigureAwait(false);
            case SyncStepKind.Conflict:
                if (local == null || remote == null) return false;
                await ResolveNoteConflictAsync(local, remote, cursor, listing?.ServerMtime,
                    accessToken, cancellationToken).ConfigureAwait(false);
                return true;
            default:
                return false;
        }
    }

    /// <summary>发布一篇笔记，最多重试三次并回读校验，避免把「写成功但没落盘」当成成功。</summary>
    private async Task<bool> PublishNoteAsync(Note note, int rev, SyncCursor cursor,
        long? listingMtime, string accessToken, CancellationToken cancellationToken)
    {
        var attempt = cursor.Clone();
        var publishing = Note.CopyOf(note);
        var nextRev = rev;
        for (var retry = 0; retry < PublishRetries; retry++)
        {
            await _registry.EnsureNotKickedAsync(accessToken, cancellationToken).ConfigureAwait(false);
            publishing.Rev = nextRev;
            if (!publishing.Purged)
            {
                await UploadAttachmentsAsync(publishing, publishing.Body, _notes.Attachments,
                    attempt, _notes.Find(publishing.Id), accessToken, cancellationToken)
                    .ConfigureAwait(false);
            }
            await UploadNoteAsync(publishing, accessToken, cancellationToken).ConfigureAwait(false);

            var check = await DownloadNoteAsync(publishing.Id, accessToken, cancellationToken)
                .ConfigureAwait(false);
            if (check?.Rev == publishing.Rev
                && SyncRules.SameTime(check.UpdatedAt, publishing.UpdatedAt))
            {
                attempt.BaseRev = publishing.Rev;
                attempt.BaseUpdatedAt = publishing.UpdatedAt;
                attempt.Dirty = false;
                attempt.ServerMtime = listingMtime;
                if (publishing.Purged)
                {
                    var prefix = "attachments/" + publishing.Id.ToString("D").ToUpperInvariant() + "/";
                    var stillUsed = _notes.Notes.Any(other => other.Id != publishing.Id
                        && !other.Purged && other.Body.Contains(prefix, StringComparison.Ordinal));
                    if (!stillUsed)
                    {
                        try
                        {
                            await _client.DeleteRemoteAsync(AttachmentDirectory(publishing.Id),
                                accessToken, cancellationToken).ConfigureAwait(false);
                        }
                        catch { /* 附件目录清理失败不阻塞笔记同步 */ }
                    }
                }
                _notes.ApplySyncedNote(publishing, attempt);
                CopyCursorFrom(cursor, attempt);
                return true;
            }

            if (check != null)
            {
                var again = SyncRules.AfterFetch(true, check.Rev, check.UpdatedAt,
                    publishing.UpdatedAt, attempt);
                if (again.Kind == SyncStepKind.Upload)
                {
                    nextRev = again.Rev;
                    continue;
                }
                if (again.Kind == SyncStepKind.Conflict)
                {
                    var current = _notes.Find(publishing.Id) ?? publishing;
                    await ResolveNoteConflictAsync(current, check, attempt, listingMtime,
                        accessToken, cancellationToken).ConfigureAwait(false);
                    return true;
                }
            }
        }
        throw new BaiduPanException($"笔记「{publishing.Title}」连续三次没能写上云端");
    }

    private async Task ResolveNoteConflictAsync(Note local, Note remote, SyncCursor cursor,
        long? listingMtime, string accessToken, CancellationToken cancellationToken)
    {
        if (SyncRules.RemoteWins(local.UpdatedAt, remote.UpdatedAt))
        {
            var copy = ConflictCopy(local);
            var copyCursor = new SyncCursor { UploadedAttachments = [.. cursor.UploadedAttachments] };
            _notes.ApplySyncedNote(copy, copyCursor);
            await PublishNoteAsync(copy, 1, copyCursor, null, accessToken, cancellationToken)
                .ConfigureAwait(false);
            var clean = new SyncCursor
            {
                BaseRev = remote.Rev,
                BaseUpdatedAt = remote.UpdatedAt,
                ServerMtime = listingMtime,
                Dirty = false,
                UploadedAttachments = [.. cursor.UploadedAttachments],
            };
            await DownloadAttachmentsAsync(remote.Body, _notes.Attachments, clean,
                accessToken, cancellationToken).ConfigureAwait(false);
            _notes.ApplySyncedNote(remote, clean);
        }
        else
        {
            var copy = ConflictCopy(remote);
            var copyCursor = new SyncCursor { UploadedAttachments = [.. cursor.UploadedAttachments] };
            await DownloadAttachmentsAsync(copy.Body, _notes.Attachments, copyCursor,
                accessToken, cancellationToken).ConfigureAwait(false);
            _notes.ApplySyncedNote(copy, copyCursor);
            await PublishNoteAsync(copy, 1, copyCursor, null, accessToken, cancellationToken)
                .ConfigureAwait(false);
            var winnerCursor = cursor.Clone();
            await PublishNoteAsync(local, Math.Max(local.Rev, remote.Rev) + 1, winnerCursor,
                listingMtime, accessToken, cancellationToken).ConfigureAwait(false);
        }
    }

    private static Note ConflictCopy(Note note)
    {
        var copy = Note.CopyOf(note);
        copy.Id = Guid.NewGuid();
        if (!copy.Title.EndsWith("（冲突副本）", StringComparison.Ordinal))
            copy.Title += "（冲突副本）";
        copy.Rev = 0;
        copy.Purged = false;
        return copy;
    }

    // MARK: - 任务

    private async Task<int> SyncTodosAsync(string accessToken, CancellationToken cancellationToken)
    {
        await MigrateTodosIfNeededAsync(accessToken, cancellationToken).ConfigureAwait(false);
        var remoteFiles = await ListJsonAsync(TodosDirectory, accessToken, cancellationToken)
            .ConfigureAwait(false);
        var ids = remoteFiles.Keys.Union(_todos.Todos.Select(todo => todo.Id)).ToList();
        var changed = 0;
        Exception? firstError = null;
        foreach (var id in ids)
        {
            try
            {
                if (await SyncOneTodoAsync(id, remoteFiles.GetValueOrDefault(id), accessToken,
                        cancellationToken).ConfigureAwait(false))
                    changed++;
            }
            catch (DeviceKickedException) { throw; }
            catch (Exception error) { firstError ??= error; }
        }
        if (firstError != null) throw firstError;
        return changed;
    }

    private async Task MigrateTodosIfNeededAsync(string accessToken, CancellationToken cancellationToken)
    {
        var existing = await ListJsonAsync(TodosDirectory, accessToken, cancellationToken)
            .ConfigureAwait(false);
        if (existing.Count > 0) return;
        var manifest = await PullManifestAsync(accessToken, cancellationToken).ConfigureAwait(false);
        var remote = manifest?.Todos ?? [];
        if (remote.Count == 0) return;
        foreach (var item in MergeTodos(_todos.Todos, remote))
        {
            var publishing = Todo.CopyOf(item);
            publishing.Rev = Math.Max(publishing.Rev, 1);
            await UploadTodoAsync(publishing, accessToken, cancellationToken).ConfigureAwait(false);
            _todos.ApplySyncedTodo(publishing, new SyncCursor
            {
                BaseRev = publishing.Rev,
                BaseUpdatedAt = publishing.UpdatedAt,
                Dirty = false,
                ServerMtime = null,
            });
        }
    }

    private async Task<bool> SyncOneTodoAsync(Guid id, BaiduPanListEntry? listing,
        string accessToken, CancellationToken cancellationToken)
    {
        var local = _todos.Find(id);
        var cursor = local == null ? SyncCursor.Unsynced() : _todos.Cursor(id);
        if (listing?.ServerMtime != null
            && SyncRules.ShouldSkipFetch(listing.ServerMtime, cursor))
            return false;
        var remote = await DownloadTodoAsync(id, accessToken, cancellationToken).ConfigureAwait(false);
        var step = SyncRules.AfterFetch(cursor.Dirty, remote?.Rev, remote?.UpdatedAt,
            local?.UpdatedAt, cursor);
        switch (step.Kind)
        {
            case SyncStepKind.Skip:
                return false;
            case SyncStepKind.RememberMtime:
                if (local == null) return false;
                cursor.ServerMtime = listing?.ServerMtime;
                cursor.Dirty = false;
                _todos.ApplySyncedTodo(local, cursor);
                return false;
            case SyncStepKind.UseRemote:
                if (remote == null) return false;
                var clean = new SyncCursor
                {
                    BaseRev = remote.Rev,
                    BaseUpdatedAt = remote.UpdatedAt,
                    ServerMtime = listing?.ServerMtime,
                    Dirty = false,
                    UploadedAttachments = [.. cursor.UploadedAttachments],
                };
                if (!await DownloadAttachmentsAsync(remote.Notes, _todos.Attachments, clean,
                        accessToken, cancellationToken).ConfigureAwait(false))
                    clean.ServerMtime = null;
                _todos.ApplySyncedTodo(remote, clean);
                return true;
            case SyncStepKind.Upload:
                if (local == null) return false;
                return await PublishTodoAsync(local, step.Rev, cursor, listing?.ServerMtime,
                    accessToken, cancellationToken).ConfigureAwait(false);
            case SyncStepKind.Conflict:
                if (local == null || remote == null) return false;
                await ResolveTodoConflictAsync(local, remote, cursor, listing?.ServerMtime,
                    accessToken, cancellationToken).ConfigureAwait(false);
                return true;
            default:
                return false;
        }
    }

    private async Task<bool> PublishTodoAsync(Todo todo, int rev, SyncCursor cursor,
        long? listingMtime, string accessToken, CancellationToken cancellationToken)
    {
        var publishing = Todo.CopyOf(todo);
        var attempt = cursor.Clone();
        var nextRev = rev;
        for (var retry = 0; retry < PublishRetries; retry++)
        {
            await _registry.EnsureNotKickedAsync(accessToken, cancellationToken).ConfigureAwait(false);
            publishing.Rev = nextRev;
            if (!publishing.Purged)
            {
                await UploadAttachmentsAsync(publishing, publishing.Notes, _todos.Attachments,
                    attempt, _todos.Find(publishing.Id), accessToken, cancellationToken,
                    isTodo: true).ConfigureAwait(false);
            }
            await UploadTodoAsync(publishing, accessToken, cancellationToken).ConfigureAwait(false);
            var check = await DownloadTodoAsync(publishing.Id, accessToken, cancellationToken)
                .ConfigureAwait(false);
            if (check?.Rev == publishing.Rev
                && SyncRules.SameTime(check.UpdatedAt, publishing.UpdatedAt))
            {
                attempt.BaseRev = publishing.Rev;
                attempt.BaseUpdatedAt = publishing.UpdatedAt;
                attempt.Dirty = false;
                attempt.ServerMtime = listingMtime;
                _todos.ApplySyncedTodo(publishing, attempt);
                CopyCursorFrom(cursor, attempt);
                return true;
            }
            if (check != null)
            {
                var again = SyncRules.AfterFetch(true, check.Rev, check.UpdatedAt,
                    publishing.UpdatedAt, attempt);
                if (again.Kind == SyncStepKind.Upload)
                {
                    nextRev = again.Rev;
                    continue;
                }
                if (again.Kind == SyncStepKind.Conflict)
                {
                    var current = _todos.Find(publishing.Id) ?? publishing;
                    await ResolveTodoConflictAsync(current, check, attempt, listingMtime,
                        accessToken, cancellationToken).ConfigureAwait(false);
                    return true;
                }
            }
        }
        throw new BaiduPanException($"任务「{publishing.Title}」连续三次没能写上云端");
    }

    private async Task ResolveTodoConflictAsync(Todo local, Todo remote, SyncCursor cursor,
        long? listingMtime, string accessToken, CancellationToken cancellationToken)
    {
        if (SyncRules.RemoteWins(local.UpdatedAt, remote.UpdatedAt))
        {
            var clean = new SyncCursor
            {
                BaseRev = remote.Rev,
                BaseUpdatedAt = remote.UpdatedAt,
                ServerMtime = listingMtime,
                Dirty = false,
                UploadedAttachments = [.. cursor.UploadedAttachments],
            };
            await DownloadAttachmentsAsync(remote.Notes, _todos.Attachments, clean,
                accessToken, cancellationToken).ConfigureAwait(false);
            _todos.ApplySyncedTodo(remote, clean);
            var copy = ConflictCopy(local);
            var copyCursor = SyncCursor.Unsynced();
            _todos.ApplySyncedTodo(copy, copyCursor);
            await PublishTodoAsync(copy, 1, copyCursor, null, accessToken, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            var copy = ConflictCopy(remote);
            var copyCursor = SyncCursor.Unsynced();
            await DownloadAttachmentsAsync(copy.Notes, _todos.Attachments, copyCursor,
                accessToken, cancellationToken).ConfigureAwait(false);
            _todos.ApplySyncedTodo(copy, copyCursor);
            await PublishTodoAsync(copy, 1, copyCursor, null, accessToken, cancellationToken)
                .ConfigureAwait(false);
            var winnerCursor = cursor.Clone();
            await PublishTodoAsync(local, Math.Max(local.Rev, remote.Rev) + 1, winnerCursor,
                listingMtime, accessToken, cancellationToken).ConfigureAwait(false);
        }
    }

    private static Todo ConflictCopy(Todo todo)
    {
        var copy = Todo.CopyOf(todo);
        copy.Id = Guid.NewGuid();
        if (!copy.Title.EndsWith("（冲突副本）", StringComparison.Ordinal))
            copy.Title += "（冲突副本）";
        copy.Rev = 0;
        copy.Purged = false;
        return copy;
    }

    // MARK: - 笔记本

    private async Task SyncNotebooksAsync(string accessToken, CancellationToken cancellationToken)
    {
        var remote = await DownloadNotebooksAsync(accessToken, cancellationToken)
            .ConfigureAwait(false);
        var merged = NotebookMerge.Merge(_notebooks.Custom, _notebooks.Removed, remote);
        var changed = !SameCloud(remote, merged) || _notebooks.IsDirty;
        _notebooks.ApplyCloud(merged.Names, merged.Removed);
        if (changed)
        {
            var payload = Encoding.UTF8.GetBytes(TonoteJson.Serialize(merged));
            await _client.UploadDataAsync(payload, NotebooksPath, accessToken, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static bool SameCloud(NotebookCloud? lhs, NotebookCloud rhs)
        => lhs != null
           && lhs.Names.SequenceEqual(rhs.Names)
           && lhs.Removed.SequenceEqual(rhs.Removed);

    // MARK: - 附件

    private async Task UploadAttachmentsAsync(object owner, string body, AttachmentStore attachments,
        SyncCursor cursor, object? current, string accessToken,
        CancellationToken cancellationToken, bool isTodo = false)
    {
        foreach (var path in NoteAttachmentIndex.RelativePaths(body))
        {
            if (cursor.UploadedAttachments.Contains(path)) continue;
            var file = attachments.AbsolutePath(path);
            if (!File.Exists(file)) continue;
            var data = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
            await _client.UploadDataAsync(data, BaiduPanConfig.RemoteRoot + "/" + path,
                accessToken, cancellationToken).ConfigureAwait(false);
            cursor.UploadedAttachments.Add(path);
            if (!isTodo && current is Note note)
            {
                var persisted = cursor.Clone();
                persisted.Dirty = true;
                _notes.ApplySyncedNote(note, persisted);
            }
            else if (isTodo && current is Todo todo)
            {
                var persisted = cursor.Clone();
                persisted.Dirty = true;
                _todos.ApplySyncedTodo(todo, persisted);
            }
        }
    }

    /// <summary>返回 false 表示还有图片没拉下来，下一轮要再试。</summary>
    private async Task<bool> DownloadAttachmentsAsync(string body, AttachmentStore attachments,
        SyncCursor cursor, string accessToken, CancellationToken cancellationToken)
    {
        var complete = true;
        foreach (var path in NoteAttachmentIndex.RelativePaths(body))
        {
            if (!attachments.FileExists(path))
            {
                try
                {
                    var data = await _client.DownloadDataAsync(
                        BaiduPanConfig.RemoteRoot + "/" + path, accessToken, cancellationToken)
                        .ConfigureAwait(false);
                    var dest = attachments.AbsolutePath(path);
                    var dir = Path.GetDirectoryName(dest);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    await File.WriteAllBytesAsync(dest, data, cancellationToken).ConfigureAwait(false);
                }
                catch (BaiduPanNotFoundException) { continue; }
                catch { complete = false; continue; }
            }
            if (!cursor.UploadedAttachments.Contains(path))
                cursor.UploadedAttachments.Add(path);
        }
        return complete;
    }

    // MARK: - 网盘读写

    private static string NotesDirectory => BaiduPanConfig.RemoteRoot + "/notes";
    private static string TodosDirectory => BaiduPanConfig.RemoteRoot + "/todos";
    private static string NotebooksPath => BaiduPanConfig.RemoteRoot + "/notebooks.json";
    private static string ManifestPath => BaiduPanConfig.RemoteRoot + "/manifest.json";

    private static string NotePath(Guid id)
        => NotesDirectory + "/" + id.ToString("D").ToUpperInvariant() + ".json";

    private static string TodoPath(Guid id)
        => TodosDirectory + "/" + id.ToString("D").ToUpperInvariant() + ".json";

    private static string AttachmentDirectory(Guid id)
        => BaiduPanConfig.RemoteRoot + "/attachments/" + id.ToString("D").ToUpperInvariant();

    private async Task<Dictionary<Guid, BaiduPanListEntry>> ListJsonAsync(string directory,
        string accessToken, CancellationToken cancellationToken)
    {
        List<BaiduPanListEntry> entries;
        try
        {
            entries = await _client.ListFilesAsync(directory, accessToken, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (BaiduPanNotFoundException)
        {
            return [];
        }

        var result = new Dictionary<Guid, BaiduPanListEntry>();
        foreach (var entry in entries)
        {
            var name = entry.Name;
            if (entry.IsDir == 1 || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                continue;
            var stem = name[..^5];
            if (Guid.TryParse(stem, out var id))
                result[id] = entry;
        }
        return result;
    }

    private async Task<Note?> DownloadNoteAsync(Guid id, string accessToken,
        CancellationToken cancellationToken)
    {
        try
        {
            var data = await _client.DownloadDataAsync(NotePath(id), accessToken, cancellationToken)
                .ConfigureAwait(false);
            return TonoteJson.Deserialize<Note>(Encoding.UTF8.GetString(data));
        }
        catch (BaiduPanNotFoundException) { return null; }
    }

    private Task UploadNoteAsync(Note note, string accessToken, CancellationToken cancellationToken)
        => _client.UploadDataAsync(Encoding.UTF8.GetBytes(TonoteJson.Serialize(note)),
            NotePath(note.Id), accessToken, cancellationToken);

    private async Task<Todo?> DownloadTodoAsync(Guid id, string accessToken,
        CancellationToken cancellationToken)
    {
        try
        {
            var data = await _client.DownloadDataAsync(TodoPath(id), accessToken, cancellationToken)
                .ConfigureAwait(false);
            return TonoteJson.Deserialize<Todo>(Encoding.UTF8.GetString(data));
        }
        catch (BaiduPanNotFoundException) { return null; }
    }

    private Task UploadTodoAsync(Todo todo, string accessToken, CancellationToken cancellationToken)
        => _client.UploadDataAsync(Encoding.UTF8.GetBytes(TonoteJson.Serialize(todo)),
            TodoPath(todo.Id), accessToken, cancellationToken);

    private async Task<NotebookCloud?> DownloadNotebooksAsync(string accessToken,
        CancellationToken cancellationToken)
    {
        try
        {
            var data = await _client.DownloadDataAsync(NotebooksPath, accessToken, cancellationToken)
                .ConfigureAwait(false);
            return TonoteJson.Deserialize<NotebookCloud>(Encoding.UTF8.GetString(data));
        }
        catch (BaiduPanNotFoundException) { return null; }
    }

    private async Task<OldManifest?> PullManifestAsync(string accessToken,
        CancellationToken cancellationToken)
    {
        try
        {
            var data = await _client.DownloadDataAsync(ManifestPath, accessToken, cancellationToken)
                .ConfigureAwait(false);
            return TonoteJson.Deserialize<OldManifest>(Encoding.UTF8.GetString(data));
        }
        catch (BaiduPanNotFoundException) { return null; }
    }

    private sealed class OldManifest
    {
        [JsonPropertyName("notes")]
        public List<Note> Notes { get; set; } = [];

        [JsonPropertyName("todos")]
        public List<Todo> Todos { get; set; } = [];
    }

    private static List<Note> MergeByUpdatedAt(IEnumerable<Note> local, IEnumerable<Note> remote)
    {
        var map = new Dictionary<Guid, Note>();
        foreach (var note in remote) map[note.Id] = note;
        foreach (var note in local)
            map[note.Id] = map.TryGetValue(note.Id, out var other)
                           && note.UpdatedAt < other.UpdatedAt ? other : note;
        return map.Values.ToList();
    }

    private static List<Todo> MergeTodos(IEnumerable<Todo> local, IEnumerable<Todo> remote)
    {
        var map = new Dictionary<Guid, Todo>();
        foreach (var todo in remote) map[todo.Id] = todo;
        foreach (var todo in local)
            map[todo.Id] = map.TryGetValue(todo.Id, out var other)
                           && todo.UpdatedAt < other.UpdatedAt ? other : todo;
        return map.Values.ToList();
    }

    private static void CopyCursorFrom(SyncCursor target, SyncCursor source)
    {
        target.BaseRev = source.BaseRev;
        target.BaseUpdatedAt = source.BaseUpdatedAt;
        target.ServerMtime = source.ServerMtime;
        target.Dirty = source.Dirty;
        target.UploadedAttachments = [.. source.UploadedAttachments];
    }

    private DateTime? LoadLastSync()
    {
        try
        {
            if (!File.Exists(_lastSyncPath)) return null;
            var state = TonoteJson.Deserialize<LastSyncState>(File.ReadAllText(_lastSyncPath));
            return state?.Date?.ToLocalTime();
        }
        catch { return null; }
    }

    private void SaveLastSync(DateTime date)
    {
        try
        {
            TonoteJson.WriteAtomic(_lastSyncPath,
                TonoteJson.Serialize(new LastSyncState { Date = date.ToUniversalTime() }));
        }
        catch { /* 时间戳写失败不影响同步 */ }
    }

    private sealed class LastSyncState
    {
        [JsonPropertyName("date")]
        public DateTime? Date { get; set; }
    }
}
