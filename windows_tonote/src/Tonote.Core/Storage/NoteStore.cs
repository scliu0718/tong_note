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
        if (Notes.Count == 0)
        {
            Notes = NoteSamples.Create();
            Save();
        }

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
        try
        {
            if (!File.Exists(_path))
                return;
            Notes = TonoteJson.Deserialize<List<Note>>(File.ReadAllText(_path)) ?? [];
        }
        catch
        {
            Notes = [];
        }
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
