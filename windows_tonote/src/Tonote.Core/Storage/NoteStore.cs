namespace Tonote.Core;

public sealed class NoteStore
{
    private readonly string _path;

    public NoteStore(string directory)
    {
        Directory.CreateDirectory(directory);
        RootDirectory = directory;
        _path = Path.Combine(directory, "notes.json");
        Load();
        if (Notes.Count == 0)
        {
            Notes = NoteSamples.Create();
            Save();
        }

        EnsureQuickSync();
    }

    public string RootDirectory { get; }

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
        Changed?.Invoke();
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
        Changed?.Invoke();
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
        Changed?.Invoke();
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
        Changed?.Invoke();
    }

    public void SetNotebook(Guid id, string notebook)
    {
        var note = Find(id);
        if (note == null || note.IsQuickSync)
            return;
        note.Notebook = notebook;
        note.UpdatedAt = DateTime.UtcNow;
        Save();
        Changed?.Invoke();
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
        Changed?.Invoke();
    }

    public void Restore(Guid id)
    {
        var note = Find(id);
        if (note == null)
            return;
        note.DeletedAt = null;
        note.UpdatedAt = DateTime.UtcNow;
        Save();
        Changed?.Invoke();
    }

    public void PermanentlyDelete(Guid id)
    {
        var note = Find(id);
        if (note == null)
            return;
        note.Purged = true;
        note.UpdatedAt = DateTime.UtcNow;
        note.Pinned = false;
        Save();
        Changed?.Invoke();
    }

    public void TogglePin(Guid id)
    {
        var note = Find(id);
        if (note == null || note.IsQuickSync)
            return;
        note.Pinned = !note.Pinned;
        note.UpdatedAt = DateTime.UtcNow;
        Save();
        Changed?.Invoke();
    }

    public void MoveNotes(string from, string to)
    {
        var changed = false;
        foreach (var note in Notes.Where(note => note.Notebook == from))
        {
            note.Notebook = to;
            note.UpdatedAt = DateTime.UtcNow;
            changed = true;
        }

        if (!changed)
            return;
        Save();
        Changed?.Invoke();
    }

    public void RemoveTag(string tag)
    {
        var changed = false;
        foreach (var note in Notes.Where(note => note.Tags.Contains(tag)))
        {
            note.Tags.RemoveAll(item => item == tag);
            note.UpdatedAt = DateTime.UtcNow;
            changed = true;
        }

        if (!changed)
            return;
        Save();
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
