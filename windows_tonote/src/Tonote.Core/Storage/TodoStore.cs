namespace Tonote.Core;

public sealed class TodoStore
{
    private readonly string _path;

    public TodoStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "todos.json");
        Load();
    }

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

    public Todo Add(string title)
    {
        var todo = new Todo
        {
            Title = title.Trim(),
            Order = Todos.Count,
        };
        Todos.Add(todo);
        Save();
        Changed?.Invoke();
        return todo;
    }

    public void SetStatus(Guid id, TodoStatus status)
    {
        var todo = Todos.FirstOrDefault(item => item.Id == id);
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
        Changed?.Invoke();
    }

    public void Remove(Guid id)
    {
        var todo = Todos.FirstOrDefault(item => item.Id == id);
        if (todo == null)
            return;
        todo.Purged = true;
        todo.UpdatedAt = DateTime.UtcNow;
        Save();
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
