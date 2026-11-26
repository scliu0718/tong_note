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
            // 单篇请求不能丢：忙时记下来，当前轮结束后补跑（否则点开「快同步」拉不到云端内容）。
            if (onlyNoteId != null) _pendingSingle = onlyNoteId;
            else _needsAnotherRound = true;
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
                            ? noteCount == 0 && todoCount == 0
                                ? "笔记 0 篇 · 任务 0 条（无变更，已与网盘一致）"
                                : $"笔记 {noteCount} 篇 · 任务 {todoCount} 条变更"
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

        var pending = _pendingSingle;
        _pendingSingle = null;
        if (pending != null && !cancellationToken.IsCancellationRequested)
            await SyncRoundAsync(pending, cancellationToken).ConfigureAwait(false);
    }

    private Guid? _pendingSingle;

    /// <summary>网络类瞬态故障才自动重试；数据/契约错误立即暴露，不掩盖问题。按异常类型判断，不靠文案子串。</summary>
    private static bool IsTransient(Exception error)
        => error is BaiduPanTransientException
           or HttpRequestException or TaskCanceledException or TimeoutException;

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
        // Windows 的 Note 是引用类型，UI 编辑会直接改到 local 指向的对象。
        // 取一份独立快照，供回写前判断「同步期间用户是否又改过」——对齐 Mac 的 Note 值语义。
        var snapshot = local == null ? null : Note.CopyOf(local);
        var cursor = local == null ? SyncCursor.Unsynced() : _notes.Cursor(id);
        if (listing?.ServerMtime != null
            && SyncRules.ShouldSkipFetch(listing.ServerMtime, cursor))
            return false;

        Note? remote;
        if (listing == null)
        {
            // 目录里没有这个文件。若本篇曾经同步过（baseRev != 0），说明是列表抖动或刚被别端删除，
            // 绝不能当成「云端无此文件」拿本地旧版覆盖，跳过等下一轮列表恢复。
            if (cursor.BaseRev != 0)
                return false;
            remote = null;
        }
        else
        {
            // 目录里既然有这个 id，任何下载/解析失败都只是临时错误：一律上抛，
            // 交给 SyncNotes 记失败、下一轮重试，绝不退化成用本地内容覆盖云端笔记。
            remote = await DownloadNoteAsync(id, accessToken, cancellationToken).ConfigureAwait(false);
        }

        // 「快同步」本机从未同步、正文还是出厂默认文案：不算用户改动，云端有就直接用云端，
        // 否则会被判成冲突，云端内容进不来。
        var dirty = cursor.Dirty;
        if (local != null && local.IsQuickSync && cursor.BaseRev == 0 && cursor.BaseUpdatedAt == null
            && local.Body == NoteSamples.QuickSync().Body)
            dirty = false;
        var step = SyncRules.AfterFetch(dirty, remote?.Rev, remote?.UpdatedAt,
            local?.UpdatedAt, cursor);
        switch (step.Kind)
        {
            case SyncStepKind.Skip:
                return false;
            case SyncStepKind.RememberMtime:
                if (local == null) return false;
                // 下载云端期间用户可能又在打字：此时不能拿旧快照写回，留给下一轮按「本地已改」处理
                if (!_notes.IsUnchanged(id, snapshot)) return false;
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
                // 下载（含图片）期间用户在本机继续写了内容：绝不能用云端版本覆盖刚写的字。
                // 放弃这次回写；本篇已被标脏，下一轮按「两边都改过」走冲突保留，不会丢内容。
                if (!_notes.IsUnchanged(id, snapshot)) return false;
                _notes.ApplySyncedNote(remote, cursor);
                return true;
            case SyncStepKind.Upload:
                if (local == null) return false;
                return await PublishNoteAsync(local, snapshot, step.Rev, cursor, listing?.ServerMtime,
                    accessToken, cancellationToken).ConfigureAwait(false);
            case SyncStepKind.Conflict:
                if (local == null || remote == null) return false;
                await ResolveNoteConflictAsync(local, snapshot, remote, cursor, listing?.ServerMtime,
                    accessToken, cancellationToken).ConfigureAwait(false);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// 上传成功后的本地回写。上传 + 回读要走好几秒网络，期间用户可能还在继续打字：
    /// 笔记没再被改过才把已发布版本写回；改过就只更新同步对照表并保持「待上传」，
    /// 下一轮再把新内容传上去。否则旧快照会把刚写的字覆盖掉，编辑器里内容就「跳」回去了。
    /// 与 Mac SyncEngine.commitPublished 一致。
    /// </summary>
    private void CommitPublishedNote(Note published, Note? snapshot, SyncCursor attempt)
    {
        if (_notes.IsUnchanged(published.Id, snapshot))
        {
            _notes.ApplySyncedNote(published, attempt);
        }
        else
        {
            attempt.Dirty = true;
            _notes.ApplySyncedCursor(attempt, published.Id);
        }
    }

    /// <summary>发布一篇笔记。create errno=0 即代表已写入，回读只用于确认，最多 3 次、400ms×retry 退避；
    /// 回读全失败不重传、不 rev+1，只记已发布状态留待下一轮校验。</summary>
    private async Task<bool> PublishNoteAsync(Note note, Note? snapshot, int rev, SyncCursor cursor,
        long? listingMtime, string accessToken, CancellationToken cancellationToken)
    {
        // snapshot 是进入同步时取的独立快照；冲突副本等场景传自身（新对象不在 UI 编辑链上）。
        snapshot ??= note;
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

            var check = await ReadBackNoteAsync(publishing.Id, accessToken, cancellationToken)
                .ConfigureAwait(false);
            if (check == null)
            {
                // 上传已成功但回读未确认：记下已发布状态，不重传、不 rev+1，留待下一轮校验
                attempt.BaseRev = publishing.Rev;
                attempt.BaseUpdatedAt = publishing.UpdatedAt;
                attempt.Dirty = false;
                attempt.ServerMtime = listingMtime;
                await CleanupPurgedNoteAttachmentsAsync(publishing, accessToken, cancellationToken)
                    .ConfigureAwait(false);
                CommitPublishedNote(publishing, snapshot, attempt);
                CopyCursorFrom(cursor, attempt);
                return true;
            }

            if (check.Rev == publishing.Rev
                && SyncRules.SameTime(check.UpdatedAt, publishing.UpdatedAt))
            {
                attempt.BaseRev = publishing.Rev;
                attempt.BaseUpdatedAt = publishing.UpdatedAt;
                attempt.Dirty = false;
                attempt.ServerMtime = listingMtime;
                await CleanupPurgedNoteAttachmentsAsync(publishing, accessToken, cancellationToken)
                    .ConfigureAwait(false);
                CommitPublishedNote(publishing, snapshot, attempt);
                CopyCursorFrom(cursor, attempt);
                return true;
            }

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
                await ResolveNoteConflictAsync(current, snapshot, check, attempt, listingMtime,
                    accessToken, cancellationToken).ConfigureAwait(false);
                return true;
            }
        }
        throw new BaiduPanException($"笔记「{publishing.Title}」连续三次没能写上云端");
    }

    /// <summary>回读最多 3 次、400ms×retry 退避，只做确认；全失败返回 null，不把抖动当业务失败。</summary>
    private async Task<Note?> ReadBackNoteAsync(Guid id, string accessToken,
        CancellationToken cancellationToken)
    {
        for (var retry = 0; retry < 3; retry++)
        {
            if (retry > 0)
                await Task.Delay(retry * 400, cancellationToken).ConfigureAwait(false);
            try
            {
                return await DownloadNoteAsync(id, accessToken, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // 回读失败只重试回读，不 rev+1 重传
            }
        }
        return null;
    }

    /// <summary>墓碑笔记删除后，若没有其它笔记引用它的附件目录，就清理远端附件。</summary>
    private async Task CleanupPurgedNoteAttachmentsAsync(Note publishing, string accessToken,
        CancellationToken cancellationToken)
    {
        if (!publishing.Purged) return;
        var prefix = "attachments/" + publishing.Id.ToString("D").ToUpperInvariant() + "/";
        var stillUsed = _notes.Notes.Any(other => other.Id != publishing.Id
            && !other.Purged && other.Body.Contains(prefix, StringComparison.Ordinal));
        if (stillUsed) return;
        try
        {
            await _client.DeleteRemoteAsync(AttachmentDirectory(publishing.Id),
                accessToken, cancellationToken).ConfigureAwait(false);
        }
        catch { /* 附件目录清理失败不阻塞笔记同步 */ }
    }

    private async Task ResolveNoteConflictAsync(Note local, Note? snapshot, Note remote, SyncCursor cursor,
        long? listingMtime, string accessToken, CancellationToken cancellationToken)
    {
        if (SyncRules.RemoteWins(local.UpdatedAt, remote.UpdatedAt))
        {
            var copy = ConflictCopy(local);
            var copyCursor = new SyncCursor { UploadedAttachments = [.. cursor.UploadedAttachments] };
            _notes.ApplySyncedNote(copy, copyCursor);
            await PublishNoteAsync(copy, copy, 1, copyCursor, null, accessToken, cancellationToken)
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
            // 处理冲突期间用户又改了本篇：保留刚写的内容，对照表指向云端版本并保持待上传，
            // 下一轮把它当作在云端版本之上的新修改传上去（与 Mac resolveConflict 一致）。
            if (_notes.IsUnchanged(local.Id, snapshot))
            {
                _notes.ApplySyncedNote(remote, clean);
            }
            else
            {
                clean.Dirty = true;
                _notes.ApplySyncedCursor(clean, local.Id);
            }
        }
        else
        {
            var copy = ConflictCopy(remote);
            var copyCursor = new SyncCursor { UploadedAttachments = [.. cursor.UploadedAttachments] };
            await DownloadAttachmentsAsync(copy.Body, _notes.Attachments, copyCursor,
                accessToken, cancellationToken).ConfigureAwait(false);
            _notes.ApplySyncedNote(copy, copyCursor);
            await PublishNoteAsync(copy, copy, 1, copyCursor, null, accessToken, cancellationToken)
                .ConfigureAwait(false);
            var winnerCursor = cursor.Clone();
            await PublishNoteAsync(local, snapshot, Math.Max(local.Rev, remote.Rev) + 1, winnerCursor,
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
        // 与笔记同理：Windows 的 Todo 是引用类型，取独立快照供回写前判断竞态。
        var snapshot = local == null ? null : Todo.CopyOf(local);
        var cursor = local == null ? SyncCursor.Unsynced() : _todos.Cursor(id);
        if (listing?.ServerMtime != null
            && SyncRules.ShouldSkipFetch(listing.ServerMtime, cursor))
            return false;

        Todo? remote;
        if (listing == null)
        {
            // 目录里没有这个文件。若本条曾经同步过（baseRev != 0），说明是列表抖动或刚被别端删除，
            // 绝不能当成「云端无此文件」拿本地旧版覆盖，跳过等下一轮列表恢复。
            if (cursor.BaseRev != 0)
                return false;
            remote = null;
        }
        else
        {
            // 目录里既然有这个 id，任何下载/解析失败都只是临时错误：一律上抛，
            // 交给 SyncTodos 记失败、下一轮重试，绝不退化成用本地内容覆盖云端任务。
            remote = await DownloadTodoAsync(id, accessToken, cancellationToken).ConfigureAwait(false);
        }

        var step = SyncRules.AfterFetch(cursor.Dirty, remote?.Rev, remote?.UpdatedAt,
            local?.UpdatedAt, cursor);
        switch (step.Kind)
        {
            case SyncStepKind.Skip:
                return false;
            case SyncStepKind.RememberMtime:
                if (local == null) return false;
                if (!_todos.IsUnchanged(id, snapshot)) return false;
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
                // 下载期间用户又改了这条任务：不用云端版本覆盖，留给下一轮走冲突保留。
                if (!_todos.IsUnchanged(id, snapshot)) return false;
                _todos.ApplySyncedTodo(remote, clean);
                return true;
            case SyncStepKind.Upload:
                if (local == null) return false;
                return await PublishTodoAsync(local, snapshot, step.Rev, cursor, listing?.ServerMtime,
                    accessToken, cancellationToken).ConfigureAwait(false);
            case SyncStepKind.Conflict:
                if (local == null || remote == null) return false;
                await ResolveTodoConflictAsync(local, snapshot, remote, cursor, listing?.ServerMtime,
                    accessToken, cancellationToken).ConfigureAwait(false);
                return true;
            default:
                return false;
        }
    }

    /// <summary>任务上传成功后的回写：没被继续编辑才回写已发布版本，否则保持待上传。</summary>
    private void CommitPublishedTodo(Todo published, Todo? snapshot, SyncCursor attempt)
    {
        if (_todos.IsUnchanged(published.Id, snapshot))
        {
            _todos.ApplySyncedTodo(published, attempt);
        }
        else
        {
            attempt.Dirty = true;
            _todos.ApplySyncedCursor(attempt, published.Id);
        }
    }

    private async Task<bool> PublishTodoAsync(Todo todo, Todo? snapshot, int rev, SyncCursor cursor,
        long? listingMtime, string accessToken, CancellationToken cancellationToken)
    {
        snapshot ??= todo;
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

            var check = await ReadBackTodoAsync(publishing.Id, accessToken, cancellationToken)
                .ConfigureAwait(false);
            if (check == null)
            {
                // 上传已成功但回读未确认：记下已发布状态，不重传、不 rev+1，留待下一轮校验
                attempt.BaseRev = publishing.Rev;
                attempt.BaseUpdatedAt = publishing.UpdatedAt;
                attempt.Dirty = false;
                attempt.ServerMtime = listingMtime;
                CommitPublishedTodo(publishing, snapshot, attempt);
                CopyCursorFrom(cursor, attempt);
                return true;
            }

            if (check.Rev == publishing.Rev
                && SyncRules.SameTime(check.UpdatedAt, publishing.UpdatedAt))
            {
                attempt.BaseRev = publishing.Rev;
                attempt.BaseUpdatedAt = publishing.UpdatedAt;
                attempt.Dirty = false;
                attempt.ServerMtime = listingMtime;
                CommitPublishedTodo(publishing, snapshot, attempt);
                CopyCursorFrom(cursor, attempt);
                return true;
            }

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
                await ResolveTodoConflictAsync(current, snapshot, check, attempt, listingMtime,
                    accessToken, cancellationToken).ConfigureAwait(false);
                return true;
            }
        }
        throw new BaiduPanException($"任务「{publishing.Title}」连续三次没能写上云端");
    }

    /// <summary>回读最多 3 次、400ms×retry 退避，只做确认；全失败返回 null，不把抖动当业务失败。</summary>
    private async Task<Todo?> ReadBackTodoAsync(Guid id, string accessToken,
        CancellationToken cancellationToken)
    {
        for (var retry = 0; retry < 3; retry++)
        {
            if (retry > 0)
                await Task.Delay(retry * 400, cancellationToken).ConfigureAwait(false);
            try
            {
                return await DownloadTodoAsync(id, accessToken, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // 回读失败只重试回读，不 rev+1 重传
            }
        }
        return null;
    }

    private async Task ResolveTodoConflictAsync(Todo local, Todo? snapshot, Todo remote, SyncCursor cursor,
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
            // 处理冲突期间用户又改了这条：保留刚写的内容，只更新对照表并保持待上传。
            if (_todos.IsUnchanged(local.Id, snapshot))
            {
                _todos.ApplySyncedTodo(remote, clean);
            }
            else
            {
                clean.Dirty = true;
                _todos.ApplySyncedCursor(clean, local.Id);
            }
            var copy = ConflictCopy(local);
            var copyCursor = SyncCursor.Unsynced();
            _todos.ApplySyncedTodo(copy, copyCursor);
            await PublishTodoAsync(copy, copy, 1, copyCursor, null, accessToken, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            var copy = ConflictCopy(remote);
            var copyCursor = SyncCursor.Unsynced();
            await DownloadAttachmentsAsync(copy.Notes, _todos.Attachments, copyCursor,
                accessToken, cancellationToken).ConfigureAwait(false);
            _todos.ApplySyncedTodo(copy, copyCursor);
            await PublishTodoAsync(copy, copy, 1, copyCursor, null, accessToken, cancellationToken)
                .ConfigureAwait(false);
            var winnerCursor = cursor.Clone();
            await PublishTodoAsync(local, snapshot, Math.Max(local.Rev, remote.Rev) + 1, winnerCursor,
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
        var data = await _client.DownloadDataAsync(NotePath(id), accessToken, cancellationToken)
            .ConfigureAwait(false);
        return TonoteJson.Deserialize<Note>(Encoding.UTF8.GetString(data))
               ?? throw new BaiduPanException("云端笔记解析为空：" + id.ToString("D"));
    }

    private Task UploadNoteAsync(Note note, string accessToken, CancellationToken cancellationToken)
        => _client.UploadDataAsync(Encoding.UTF8.GetBytes(TonoteJson.Serialize(note)),
            NotePath(note.Id), accessToken, cancellationToken);

    private async Task<Todo?> DownloadTodoAsync(Guid id, string accessToken,
        CancellationToken cancellationToken)
    {
        var data = await _client.DownloadDataAsync(TodoPath(id), accessToken, cancellationToken)
            .ConfigureAwait(false);
        return TonoteJson.Deserialize<Todo>(Encoding.UTF8.GetString(data))
               ?? throw new BaiduPanException("云端任务解析为空：" + id.ToString("D"));
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
        catch
        {
            // 云端尚无笔记本清单或临时读取失败：按无远端处理，不阻塞整轮
            return null;
        }
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
        catch
        {
            // 云端尚无旧清单或临时读取失败：按无旧数据处理
            return null;
        }
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
