namespace Tonote.Core;

public sealed class NoteStore
{
    private readonly string _path;
    private readonly SyncCursorStore _cursors;

    public NoteStore(string directory)
    {
        Directory.CreateDirectory(directory);
        RootDirectory = directory;
        _path = Path.Combine(directory, "notes.json");
        _cursors = new SyncCursorStore(Path.Combine(directory, "note-sync.json"));
        Attachments = new AttachmentStore(directory);
        Load();
        // 本地还没有笔记时保持空列表，由同步引擎从真实百度网盘拉取。
        // 早期版本会在这里灌入 NoteSamples 的示例笔记，结果首次启动看到的是
        // 演示数据、登录后又被当成「本地新增」推上云端，和真实网盘数据混在一起。
        // 示例数据仍保留在 NoteSamples 中，仅在明确需要时由调用方显式写入。

        EnsureQuickSync();
    }

    public string RootDirectory { get; }

    public AttachmentStore Attachments { get; }

    public List<Note> Notes { get; private set; } = [];

    public event Action? Changed;

    public IReadOnlyList<Note> ActiveNotes => Notes
        .Where(note => !note.Purged && !note.IsTrashed)
        .OrderByDescending(note => note.UpdatedAt)
        .ToList();

    public IReadOnlyList<Note> TrashNotes => Notes
        .Where(note => note.IsTrashed && !note.Purged)
        .OrderByDescending(note => note.DeletedAt ?? DateTime.MinValue)
        .ToList();

    public IReadOnlyList<string> AllTags
    {
        get
        {
            var tags = new SortedSet<string>(StringComparer.CurrentCulture);
            foreach (var note in Notes)
            {
                if (note.IsTrashed || note.Purged || note.IsQuickSync)
                    continue;
                foreach (var tag in note.Tags)
                    tags.Add(tag);
            }

            return tags.ToList();
        }
    }

    public int CountInNotebook(string name)
    {
        var list = ActiveNotes.Where(note => !note.IsQuickSync);
        return name == Notebooks.All
            ? list.Count()
            : list.Count(note => note.Notebook == name);
    }

    public int CountWithTag(string tag)
        => ActiveNotes.Count(note => note.Tags.Contains(tag));

    public Note? Find(Guid id) => Notes.FirstOrDefault(note => note.Id == id);

    /// <summary>本机同步基线。本地无记录时返回「未同步」游标。</summary>
    public SyncCursor Cursor(Guid id) => _cursors.Cursor(id);

    /// <summary>
    /// 同步回写前的竞态检查：自 snapshot 取出以来，这篇笔记有没有被用户继续编辑过。
    /// 同步要走网络（下载、上传、回读），期间用户可能还在打字；此时若直接拿旧快照 /
    /// 云端版本覆盖，刚写的内容就会被换掉。所有编辑入口都会刷新 UpdatedAt 与正文，
    /// 所以对比这两项即可。与 Mac NoteStore.isUnchanged 一致。
    /// 注意 Windows 的 Note 是引用类型，调用方传入的必须是 CopyOf 出来的独立快照。
    /// </summary>
    public bool IsUnchanged(Guid id, Note? snapshot)
    {
        var current = Find(id);
        if (snapshot == null) return current == null;
        if (current == null) return false;
        return current.UpdatedAt == snapshot.UpdatedAt && current.Body == snapshot.Body;
    }

    /// <summary>只更新同步对照表、不动笔记内容（回写时发现用户又改过笔记的场景）。</summary>
    public void ApplySyncedCursor(SyncCursor cursor, Guid id)
    {
        _cursors.Set(cursor, id);
    }

    /// <summary>
    /// 同步结果回写。不标脏、不触发新一轮同步，只刷新界面。
    /// 与 Mac 版 NoteStore.applySyncedNote 一致。
    /// </summary>
    public void ApplySyncedNote(Note note, SyncCursor cursor)
    {
        var index = Notes.FindIndex(item => item.Id == note.Id);
        if (index >= 0) Notes[index] = Note.CopyOf(note);
        else Notes.Add(Note.CopyOf(note));

        var prefix = "attachments/" + note.Id.ToString("D").ToUpperInvariant() + "/";
        if (note.Purged && !Notes.Any(other => other.Id != note.Id && !other.Purged
                                              && other.Body.Contains(prefix, StringComparison.Ordinal)))
            Attachments.RemoveAttachments(note.Id);

        _cursors.Set(cursor, note.Id);
        Save();
        Changed?.Invoke();
    }

    /// <summary>整体替换笔记集合（旧清单迁移等场景）。</summary>
    public void SetNotes(IEnumerable<Note> notes)
    {
        Notes = notes.Select(Note.CopyOf).ToList();
        Save();
        Changed?.Invoke();
    }

    public Note AddNote(string notebook = "工作日志")
    {
        var note = new Note
        {
            Title = "未命名笔记",
            Body = "",
            Notebook = notebook == Notebooks.All ? "工作日志" : notebook,
        };
        Notes.Insert(0, note);
        Save();
        Touch(note.Id);
        return note;
    }

    public void UpdateTitle(Guid id, string title)
    {
        var note = Find(id);
        if (note == null)
            return;
        note.Title = title;
        note.UpdatedAt = DateTime.UtcNow;
        Save();
        Touch(id);
    }

    public void UpdateContent(Guid id, string body, NoteContentFormat format)
    {
        var note = Find(id);
        if (note == null)
            return;
        note.Body = body;
        note.ContentFormat = format;
        note.UpdatedAt = DateTime.UtcNow;
        Save();
        Touch(id);
    }

    public void UpdateTags(Guid id, IEnumerable<string> tags)
    {
        var note = Find(id);
        if (note == null)
            return;
        note.Tags = tags
            .Select(tag => tag.Trim())
            .Where(tag => tag.Length > 0)
            .Distinct()
            .ToList();
        note.UpdatedAt = DateTime.UtcNow;
        Save();
        Touch(id);
    }

    public void SetNotebook(Guid id, string notebook)
    {
        var note = Find(id);
        if (note == null || note.IsQuickSync)
            return;
        note.Notebook = notebook;
        note.UpdatedAt = DateTime.UtcNow;
        Save();
        Touch(id);
    }

    public void MoveToTrash(Guid id)
    {
        var note = Find(id);
        if (note == null || note.IsQuickSync)
            return;
        var now = DateTime.UtcNow;
        note.DeletedAt = now;
        note.UpdatedAt = now;
        note.Pinned = false;
        Save();
        Touch(id);
    }

    public void Restore(Guid id)
    {
        var note = Find(id);
        if (note == null)
            return;
        note.DeletedAt = null;
        note.UpdatedAt = DateTime.UtcNow;
        Save();
        Touch(id);
    }

    public void PermanentlyDelete(Guid id)
    {
        var note = Find(id);
        if (note == null)
            return;
        note.Purged = true;
        note.UpdatedAt = DateTime.UtcNow;
        note.Pinned = false;
        // W-26：两篇笔记共用 attachments/<id>/ 时，删其中一篇不能误删另一篇的图。
        // 与 ApplySyncedNote 的引用检查保持同一规则。
        var prefix = "attachments/" + id.ToString("D").ToUpperInvariant() + "/";
        if (!Notes.Any(other => other.Id != id && !other.Purged
                                && other.Body.Contains(prefix, StringComparison.Ordinal)))
            Attachments.RemoveAttachments(id);
        Save();
        Touch(id);
    }

    public void TogglePin(Guid id)
    {
        var note = Find(id);
        if (note == null || note.IsQuickSync)
            return;
        note.Pinned = !note.Pinned;
        note.UpdatedAt = DateTime.UtcNow;
        Save();
        Touch(id);
    }

    public void MoveNotes(string from, string to)
    {
        var touched = new List<Guid>();
        foreach (var note in Notes.Where(note => note.Notebook == from))
        {
            note.Notebook = to;
            note.UpdatedAt = DateTime.UtcNow;
            touched.Add(note.Id);
        }

        if (touched.Count == 0)
            return;
        Save();
        TouchMany(touched);
    }

    /// <summary>重命名标签：把旧标签替换为新标签，已含新标签的笔记去重；标脏并通知。返回错误说明，成功为 null。</summary>
    public string? RenameTag(string from, string to)
    {
        if (from == to) return null;
        if (AllTags.Contains(to)) return "标签「" + to + "」已存在。";
        var touched = new List<Guid>();
        foreach (var note in Notes.Where(note => note.Tags.Contains(from)))
        {
            var replaced = note.Tags.Select(t => t == from ? to : t).Distinct().ToList();
            note.Tags.Clear();
            note.Tags.AddRange(replaced);
            note.UpdatedAt = DateTime.UtcNow;
            touched.Add(note.Id);
        }

        if (touched.Count == 0)
            return null;
        Save();
        TouchMany(touched);
        return null;
    }

    public void RemoveTag(string tag)
    {
        var touched = new List<Guid>();
        foreach (var note in Notes.Where(note => note.Tags.Contains(tag)))
        {
            note.Tags.RemoveAll(item => item == tag);
            note.UpdatedAt = DateTime.UtcNow;
            touched.Add(note.Id);
        }

        if (touched.Count == 0)
            return;
        Save();
        TouchMany(touched);
    }

    /// <summary>本机修改：标脏并通知界面（同步引擎只监听标脏事件，避免回写触发死循环）。</summary>
    private void Touch(Guid id)
    {
        _cursors.MarkDirty(id);
        Changed?.Invoke();
    }

    private void TouchMany(IEnumerable<Guid> ids)
    {
        foreach (var id in ids) _cursors.MarkDirty(id);
        Changed?.Invoke();
    }

    private void Load()
    {
        // W-38：损坏文件先备份再回退空列表，绝不静默覆盖
        Notes = TonoteJson.TryDeserializeFile<List<Note>>(_path) ?? [];
    }

    private void Save() => TonoteJson.WriteAtomic(_path, TonoteJson.Serialize(Notes));

    private void EnsureQuickSync()
    {
        if (Notes.Any(note => note.Id == Note.QuickSyncNoteId))
            return;
        Notes.Add(NoteSamples.QuickSync());
        Save();
    }
}
