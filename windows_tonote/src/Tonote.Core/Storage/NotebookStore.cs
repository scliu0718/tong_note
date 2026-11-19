using System.Globalization;

namespace Tonote.Core;

public sealed class NotebookStore
{
    private readonly string _path;

    public NotebookStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "notebook-state.json");
        Load();
    }

    public List<string> Custom { get; private set; } = [];

    public List<string> Removed { get; private set; } = [];

    public bool IsDirty { get; private set; } = true;

    public event Action? Changed;

    public IReadOnlyList<string> List => Notebooks.BuiltIn.Concat(Custom).ToList();

    public string? Add(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
            return "笔记本名称不能为空";
        if (trimmed == Notebooks.All)
            return "「全部笔记」是内置项，不能用作笔记本名称";
        if (List.Contains(trimmed))
            return "该笔记本已存在";
        if (new StringInfo(trimmed).LengthInTextElements > Notebooks.MaxNameLength)
            return $"笔记本名称最多 {Notebooks.MaxNameLength} 个字";

        Custom.Add(trimmed);
        Removed.RemoveAll(item => item == trimmed);
        Persist(dirty: true);
        return null;
    }

    /// <summary>重命名自建笔记本（保持原位置）；成功返回 null，否则返回错误信息。笔记归属需由调用方迁移。</summary>
    public string? Rename(string oldName, string newName)
    {
        var trimmed = newName.Trim();
        var index = Custom.IndexOf(oldName);
        if (index < 0)
            return "只能修改手动创建的笔记本";
        if (trimmed == oldName)
            return null;
        if (trimmed.Length == 0)
            return "笔记本名称不能为空";
        if (trimmed == Notebooks.All)
            return "「全部笔记」是内置项，不能用作笔记本名称";
        if (List.Contains(trimmed))
            return "该笔记本已存在";
        if (new StringInfo(trimmed).LengthInTextElements > Notebooks.MaxNameLength)
            return $"笔记本名称最多 {Notebooks.MaxNameLength} 个字";

        Custom[index] = trimmed;
        if (!Removed.Contains(oldName))
            Removed.Add(oldName);
        Removed.RemoveAll(item => item == trimmed);
        Persist(dirty: true);
        return null;
    }

    public void Remove(string name)
    {
        if (name == Notebooks.All || !Custom.Contains(name))
            return;
        Custom.RemoveAll(item => item == name);
        if (!Removed.Contains(name))
            Removed.Add(name);
        Persist(dirty: true);
    }

    /// <summary>同步结果回写：清掉脏标记，不触发本地数据变更通知。</summary>
    public void ApplyCloud(IEnumerable<string> names, IEnumerable<string> removed)
    {
        Custom = names.Where(name => !Notebooks.BuiltIn.Contains(name)).Distinct().ToList();
        Removed = removed.Distinct().ToList();
        Persist(dirty: false);
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                IsDirty = true;
                return;
            }

            var state = TonoteJson.Deserialize<NotebookState>(File.ReadAllText(_path));
            if (state == null)
            {
                IsDirty = true;
                return;
            }

            Custom = state.Custom ?? [];
            Removed = state.Removed ?? [];
            IsDirty = state.Dirty;
        }
        catch
        {
            IsDirty = true;
        }
    }

    private void Persist(bool dirty)
    {
        IsDirty = dirty;
        var state = new NotebookState
        {
            Custom = Custom,
            Dirty = dirty,
            Removed = Removed,
        };
        TonoteJson.WriteAtomic(_path, TonoteJson.Serialize(state));
        if (dirty) SyncCursorStore.NotifyLocalDataChanged();
        Changed?.Invoke();
    }

    private sealed class NotebookState
    {
        public List<string> Custom { get; set; } = [];
        public bool Dirty { get; set; }
        public List<string> Removed { get; set; } = [];
    }
}
