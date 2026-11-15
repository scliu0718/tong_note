namespace Tonote.Core;

public sealed class TodoStore
{
    private readonly string _path;
    private readonly SyncCursorStore _cursors;

    public TodoStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "todos.json");
        _cursors = new SyncCursorStore(Path.Combine(directory, "todo-sync.json"));
        Attachments = new AttachmentStore(directory);
        Load();
    }

    public AttachmentStore Attachments { get; }

    public List<Todo> Todos { get; private set; } = [];

    public event Action? Changed;

    public int PendingCount => Todos.Count(todo => !todo.Purged && todo.Status == TodoStatus.todo);
    public int DoingCount => Todos.Count(todo => !todo.Purged && todo.Status == TodoStatus.doing);

    public IReadOnlyList<Todo> InStatus(TodoStatus status) => Todos
        .Where(todo => !todo.Purged && todo.Status == status)
        .OrderByDescending(todo => todo.IsOverdue)
        .ThenBy(todo => todo.PlannedAt ?? DateTime.MaxValue)
        .ThenBy(todo => todo.CreatedAt)
        .ToList();

    public Todo? Find(Guid id) => Todos.FirstOrDefault(todo => todo.Id == id);

    public SyncCursor Cursor(Guid id) => _cursors.Cursor(id);

    /// <summary>同步结果回写。不标脏、不触发新一轮同步。</summary>
    public void ApplySyncedTodo(Todo todo, SyncCursor cursor)
    {
        var index = Todos.FindIndex(item => item.Id == todo.Id);
        if (index >= 0) Todos[index] = Todo.CopyOf(todo);
        else Todos.Add(Todo.CopyOf(todo));
        _cursors.Set(cursor, todo.Id);
        Save();
        Changed?.Invoke();
    }

    public void SetTodos(IEnumerable<Todo> todos)
    {
        Todos = todos.Select(Todo.CopyOf).ToList();
        Save();
        Changed?.Invoke();
    }

    public Todo Add(string title)
    {
        var todo = new Todo
        {
            Title = title.Trim(),
            Order = Todos.Count,
        };
        Todos.Add(todo);
        Save();
        Touch(todo.Id);
        return todo;
    }

    public void Update(Todo todo)
    {
        var index = Todos.FindIndex(item => item.Id == todo.Id);
        if (index < 0)
            return;
        var updated = Todo.CopyOf(todo);
        updated.UpdatedAt = DateTime.UtcNow;
        Todos[index] = updated;
        Save();
        Touch(todo.Id);
    }

    public void SetStatus(Guid id, TodoStatus status)
    {
        var todo = Find(id);
        if (todo == null)
            return;
        todo.Status = status;
        var now = DateTime.UtcNow;
        if (status == TodoStatus.doing && todo.StartedAt == null)
            todo.StartedAt = now;
        if (status == TodoStatus.done)
            todo.CompletedAt = now;
        todo.UpdatedAt = now;
        Save();
        Touch(id);
    }

    public void SetPlannedAt(Guid id, DateTime? plannedAt)
    {
        var todo = Find(id);
        if (todo == null)
            return;
        todo.PlannedAt = plannedAt;
        todo.UpdatedAt = DateTime.UtcNow;
        Save();
        Touch(id);
    }

    public void UpdateNotes(Guid id, string notes)
    {
        var todo = Find(id);
        if (todo == null)
            return;
        todo.Notes = notes;
        todo.UpdatedAt = DateTime.UtcNow;
        Save();
        Touch(id);
    }

    public void UpdateTitle(Guid id, string title)
    {
        var todo = Find(id);
        if (todo == null)
            return;
        todo.Title = title;
        todo.UpdatedAt = DateTime.UtcNow;
        Save();
        Touch(id);
    }

    public void Remove(Guid id)
    {
        var todo = Find(id);
        if (todo == null)
            return;
        todo.Purged = true;
        todo.UpdatedAt = DateTime.UtcNow;
        Save();
        Touch(id);
    }

    private void Touch(Guid id)
    {
        _cursors.MarkDirty(id);
        Changed?.Invoke();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
                return;
            Todos = TonoteJson.Deserialize<List<Todo>>(File.ReadAllText(_path)) ?? [];
        }
        catch
        {
            Todos = [];
        }
    }

    private void Save() => TonoteJson.WriteAtomic(_path, TonoteJson.Serialize(Todos));
}
