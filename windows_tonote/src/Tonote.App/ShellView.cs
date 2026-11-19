using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Tonote.Core;

namespace Tonote.App;

/// <summary>
/// Windows 端主界面。按「同笔记-Mac端UI设计.html」8 视图与 Mac 源码 1:1 对齐：
/// 三栏（侧栏 52/112/200 三档 · 列表 292 · 内容区）、快同步/快任务固定卡、
/// 编辑器头部与元信息、任务面板四组与展开详情、同步状态、全部确认弹窗。
/// 只依赖 Tonote.Core 既有数据契约，不改动 JSON 字段。
/// </summary>
public sealed class ShellView : UserControl
{
    private enum Pane { Notes, Trash, Todos, Settings }
    private enum SortMode { Updated, Created, Title }

    private readonly AppSession _session;
    private readonly StackPanel _sidebar = new() { Spacing = 4 };
    private readonly StackPanel _sidebarBottom = new() { Spacing = 4, Margin = new Thickness(10, 4, 10, 8) };
    private readonly StackPanel _quick = new() { Spacing = 8, Margin = new Thickness(12, 8, 12, 0) };
    private readonly Grid _sortRow = new()
    {
        ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*,Auto"),
        Margin = new Thickness(12, 8, 12, 4),
    };
    private readonly StackPanel _cards = new();
    private readonly ContentControl _right = new();
    private readonly TextBox _search = new()
    {
        Watermark = "搜索笔记、标签…",
        FontSize = 13,
    };
    private readonly Button _clearSearch = Ui.Small("×", () => { }, null, 12);
    private readonly TextBox _title = new()
    {
        FontSize = Metrics.FontTitle,
        FontWeight = FontWeight.Bold,
        BorderThickness = new Thickness(0),
        Background = Brushes.Transparent,
        Padding = new Thickness(0),
        Foreground = Palette.TextPrimary,
    };
    private readonly RichTextEditor _richEditor;
    private readonly TextBlock _meta = new() { Foreground = Palette.TextTertiary, FontSize = 12 };
    private readonly TextBlock _banner = new()
    {
        Foreground = Palette.TextTertiary,
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private string _accountName = "同笔记";
    private bool _accountNameLoaded;
    private bool _quickExpanded = true;
    private bool _showingTodos;
    private readonly ContentControl _syncSlot = new()
    {
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Right,
    };
    private readonly TextBlock _count = new() { Foreground = Palette.TextTertiary, FontSize = 11.5 };
    private readonly ComboBox _notebook = new() { MinWidth = 120, FontSize = 12 };
    private readonly StackPanel _tags = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly TextBox _tagInput = new() { Watermark = "添加标签", Width = 120, FontSize = 12 };
    private readonly StackPanel _metaRow = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    // 分隔条箭头：5×12 的矢量折线，夹角约 118°（比字符「>」更平），左右各 1px 白边正好撑满 7px 宽的分隔区
    private static readonly Geometry HandleRight = Geometry.Parse("M1,1 L4,6 L1,11");
    private static readonly Geometry HandleLeft = Geometry.Parse("M4,1 L1,6 L4,11");
    private readonly Avalonia.Controls.Shapes.Path _handleGlyph = new()
    {
        Data = HandleRight,
        Width = 5, Height = 12,
        Stroke = Palette.TextTertiary, StrokeThickness = 1.2,
        StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private void SetHandleGlyph(bool left) => _handleGlyph.Data = left ? HandleLeft : HandleRight;
    private readonly Border _handle;
    private readonly Button _pinButton;
    private readonly Button _trashButton;
    private readonly Button _restoreButton;
    private readonly Button _purgeButton;
    private readonly Button _syncOneButton;

    private Pane _pane = Pane.Notes;
    private SortMode _sort = SortMode.Updated;
    private string? _notebookFilter;
    private string? _tagFilter;
    private Guid? _selected;
    private int _sidebarWidth = (int)Metrics.SidebarCompact;
    private bool _loading = true;
    private bool _dragging;
    private bool _didSwitch;
    private Point _press;

    // 任务面板状态（对应 Mac TodoView.swift 的 filter / collapsed / expanded / editing 状态机）
    private bool _todoShowAll = true;
    private readonly HashSet<TodoStatus> _collapsedGroups = [];
    private Guid? _expandedTodo;
    private Guid? _editingNotesTodo;
    /// <summary>W-27：展开任务期间冻结列表顺序，避免状态 / 时间变化导致跳动。</summary>
    private readonly Dictionary<Guid, int> _frozenOrder = new();
    private Control? _editorCached;

    public event Action? LogoutRequested;

    public ShellView(AppSession session)
    {
        _session = session;
        _pinButton = Ui.Small("置顶", TogglePin);
        _trashButton = Ui.Small("删除笔记", MoveCurrentToTrash);
        _restoreButton = Ui.Primary("恢复笔记", RestoreCurrent);
        _purgeButton = Ui.Danger("彻底删除", PurgeCurrent, icon: Glyphs.Trash);
        _syncOneButton = Ui.Small("同步本篇", async () =>
        {
            if (_selected == null) return;
            await _session.Sync.SyncSingleNoteAsync(_selected.Value);
        });
        _handle = BuildHandle();
        _richEditor = new RichTextEditor(session) { Margin = new Thickness(0) };
        _richEditor.StatusChanged += message => OnUi(() => _banner.Text = message);
        _richEditor.DeleteRequested += MoveCurrentToTrash;
        _richEditor.SyncRequested += () => _ = _session.Sync.QuickSyncAsync();
        _richEditor.WordCountChanged += count => OnUi(() => _count.Text = count + " 字");
        _richEditor.ToolbarTrailing = _count;
        KeyDown += OnShellKeyDown;
        _search.TextChanged += (_, _) =>
        {
            _clearSearch.IsVisible = !string.IsNullOrEmpty(_search.Text);
            RebuildCards();
        };
        _clearSearch.Click += (_, _) => { _search.Text = ""; _search.Focus(); };
        _clearSearch.IsVisible = false;
        _clearSearch.Width = 26;
        _clearSearch.Height = 26;
        _clearSearch.Padding = new Thickness(0);
        _clearSearch.Background = Brushes.Transparent;
        _title.TextChanged += (_, _) => SaveTitle();
        _notebook.SelectionChanged += (_, _) => SaveNotebook();
        _tagInput.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) AddTag();
        };

        // 搜索框套一层自绘外壳：灰底 8px 圆角 + 聚焦淡绿细环，
        // 左侧放大镜图标 + 输入 + 右侧清除按钮（对齐 Mac searchBar：⌕ + 搜索笔记、标签… + ✕）。
        var searchField = Ui.Field(_search);
        var searchGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        searchField.Child = null;
        var magnifier = Ui.Icon(Glyphs.Search, 13, Palette.TextQuaternary);
        magnifier.VerticalAlignment = VerticalAlignment.Center;
        magnifier.Margin = new Thickness(10, 0, 8, 0);
        searchGrid.Children.Add(magnifier);
        _search.Padding = new Thickness(0, 7, 0, 7);
        searchGrid.Children.Add(_search);
        Grid.SetColumn(_search, 1);
        searchGrid.Children.Add(_clearSearch);
        Grid.SetColumn(_clearSearch, 2);
        _clearSearch.Margin = new Thickness(0, 0, 4, 0);
        searchField.Child = searchGrid;
        searchField.Margin = new Thickness(12, 10, 12, 0);
        var cardsScroll = new ScrollViewer { Content = _cards };
        var list = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*"),
            Background = Palette.Panel,
            Children = { searchField, _sortRow, _quick, cardsScroll },
        };
        Grid.SetRow(_sortRow, 1);
        Grid.SetRow(_quick, 2);
        Grid.SetRow(cardsScroll, 3);

        var sidebarScroll = new ScrollViewer { Content = _sidebar, AllowAutoHide = false };
        sidebarScroll.Classes.Add("slim");
        var sidebarPanel = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            Background = Palette.SidebarBg,
            Children = { sidebarScroll, _sidebarBottom },
        };
        Grid.SetRow(sidebarScroll, 0);
        Grid.SetRow(_sidebarBottom, 1);

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions($"{Metrics.SidebarCompact},{Metrics.SidebarHandle},{Metrics.NoteListWidth},1,*"),
            Background = Palette.WindowBg,
            Children =
            {
                sidebarPanel,
                _handle,
                list,
                new Border { Background = Palette.Line },
                _right,
            },
        };
        Grid.SetColumn(_handle, 1);
        Grid.SetColumn(list, 2);
        Grid.SetColumn((Control)grid.Children[3], 3);
        Grid.SetColumn(_right, 4);
        Content = grid;

        // 同步引擎 / 网盘回调都在线程池线程里发出事件，统一投递到 UI 线程。
        _session.Notes.Changed += () => OnUi(Refresh);
        _session.Notebooks.Changed += () => OnUi(Refresh);
        _session.Todos.Changed += () => OnUi(() =>
        {
            if (_showingTodos) ShowTodos();
            RebuildSidebar();
            RebuildQuick();
        });
        _session.Sync.StatusChanged += () => OnUi(UpdateSyncLabels);
        _session.Avatar.Changed += () => OnUi(RebuildSidebar);

        _loading = false;
        var first = _session.Notes.ActiveNotes.FirstOrDefault(note => !note.IsQuickSync);
        if (first != null) Select(first.Id);
        else Refresh();
    }

    /// <summary>把后台线程的事件投递到 UI 线程；用 Post 而非 InvokeAsync，避免后台等 UI 死锁。</summary>
    private static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    private void Refresh()
    {
        if (_loading) return;
        RebuildSidebar();
        RebuildQuick();
        RebuildSort();
        RebuildCards();
        UpdateChrome();
        var current = Current();
        if (current != null) _richEditor.LoadNote(current);
        UpdateSyncLabels();
    }

    private void UpdateSyncLabels()
    {
        var status = _session.Sync.Status;
        var last = _session.Sync.LastSyncDate;
        var suffix = last == null ? "" : " · 上次 " + last.Value.ToString("HH:mm");
        var shortText = status.Kind switch
        {
            SyncStatusKind.Syncing => "正在同步…",
            SyncStatusKind.Success => "已同步" + suffix,
            SyncStatusKind.Failed => "同步失败",
            _ => "等待同步",
        };
        _syncSlot.Content = BuildSyncControl(status, shortText);
        var note = Current();
        if (note?.IsQuickSync == true)
            _banner.Text = shortText + (string.IsNullOrWhiteSpace(status.Message) ? "" : " · " + status.Message);
    }

    /// <summary>排序栏右侧的同步组件：同步中＝文字；否则＝绿色「一键同步」按钮。</summary>
    private Control BuildSyncControl(SyncStatus status, string shortText)
    {
        if (status.Kind == SyncStatusKind.Syncing)
            return Ui.Text(shortText, 12, null, Palette.Accent);

        if (status.Kind == SyncStatusKind.Failed)
            return Ui.Text(shortText, 12, null, Palette.Red);

        var button = new Button
        {
            Content = "一键同步",
            Background = Palette.Accent,
            Foreground = Palette.White,
            FontSize = 11.5,
            FontWeight = FontWeight.SemiBold,
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(11, 4),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        button.Click += (_, _) => _ = _session.Sync.QuickSyncAsync();
        return button;
    }

    private void Select(Guid id)
    {
        _richEditor.Flush();
        _selected = id;
        _showingTodos = false;
        if (_pane is Pane.Todos or Pane.Settings) _pane = Pane.Notes;
        LoadEditor();
        Refresh();
    }

    private void LoadEditor()
    {
        var note = Current();
        _right.Content = note == null ? Ui.EmptyState("选择或新建一篇笔记") : Editor();
        if (note == null) return;

        _loading = true;
        _title.Text = note.Title;
        _title.IsReadOnly = note.IsTrashed;
        _richEditor.LoadNote(note);
        _notebook.ItemsSource = NotebookChoices(note);
        _notebook.SelectedItem = note.Notebook;
        _notebook.IsVisible = !note.IsQuickSync && !note.IsTrashed;
        _tagInput.IsVisible = !note.IsQuickSync && !note.IsTrashed;
        _metaRow.IsVisible = !note.IsQuickSync;
        RenderTags(note);
        _loading = false;
        UpdateChrome();
    }

    private void UpdateChrome()
    {
        var note = Current();
        if (note == null || !ReferenceEquals(_right.Content, _editorCached)) return;
        _meta.Text = note.IsTrashed ? "回收站中的笔记" : "已保存到本地 · " + note.TimeText;
        _count.Text = note.WordCount + " 字";
        var editable = !note.IsTrashed && !note.IsQuickSync;
        _syncOneButton.IsVisible = !note.IsTrashed;
        _syncOneButton.Content = note.IsQuickSync ? "立即同步本篇" : "同步本篇";
        _pinButton.IsVisible = editable;
        _pinButton.Content = note.Pinned ? "取消置顶" : "置顶";
        _trashButton.IsVisible = editable;
        _restoreButton.IsVisible = note.IsTrashed;
        _purgeButton.IsVisible = note.IsTrashed;
        _banner.Text = note.IsTrashed
            ? "回收站里的笔记不能编辑。"
            : note.IsQuickSync
                ? "快同步：点「立即同步本篇」只把这一篇推到网盘，不阻塞其它笔记。"
                : "正文自动保存到本机并同步到网盘。工具条支持标题、加粗、颜色、列表、链接、图片和表格。";
        SetHandleGlyph(_sidebarWidth == Metrics.SidebarExpanded);
    }

    /// <summary>W-25：取真实百度昵称；失败静默，保留「同笔记」。仅加载一次。</summary>
    private async Task LoadAccountName()
    {
        if (_accountNameLoaded) return;
        _accountNameLoaded = true;
        try
        {
            var token = _session.Pan.CurrentToken;
            if (token == null) return;
            var info = await _session.Pan.UserInfoAsync(token.AccessToken).ConfigureAwait(false);
            var name = info.DisplayName;
            if (string.IsNullOrWhiteSpace(name) || name == "百度网盘用户") name = "同笔记";
            _accountName = name;
            OnUi(RebuildSidebar);
        }
        catch
        {
            // 失败静默，不弹错误、不写 diagnostics（纯展示信息）
        }
    }

    // ── 编辑器容器（Mac EditorView.swift：标题区 → 元数据 → 1pt 线 → 工具条 → 正文） ──
    private Control Editor()
    {
        if (_editorCached != null) return _editorCached;

        _metaRow.Children.Add(_notebook);
        _metaRow.Children.Add(_tags);
        _metaRow.Children.Add(_tagInput);

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto,Auto"),
            Margin = new Thickness(Metrics.EditorInset, 16, Metrics.EditorInset, 10),
        };
        header.Children.Add(_title);
        var buttons = new[] { _syncOneButton, _pinButton, _trashButton, _restoreButton, _purgeButton };
        for (var i = 0; i < buttons.Length; i++)
        {
            buttons[i].Margin = new Thickness(6, 0, 0, 0);
            Grid.SetColumn(buttons[i], i + 1);
            header.Children.Add(buttons[i]);
        }

        var metaStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(Metrics.EditorInset, 0, Metrics.EditorInset, 10),
            Children =
            {
                _metaRow,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    Children = { Ui.Text("●", 10, FontWeight.Bold, Palette.Accent), _meta },
                },
            },
        };

        var page = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*"),
            Background = Palette.Panel,
            Children =
            {
                header,
                metaStack,
                new Border { Height = 1, Background = Palette.LineSoft },
                new Border { Child = _banner, Margin = new Thickness(Metrics.EditorInset, 6, Metrics.EditorInset, 0) },
                _richEditor,
            },
        };
        for (var row = 1; row < page.Children.Count; row++)
            Grid.SetRow((Control)page.Children[row], row);

        _editorCached = page;
        return page;
    }

    // ── 任务面板（Mac TodoView.swift 的 1:1 结构） ──────────────────────
    private void ShowTodos()
    {
        // Mac：只在「进入」任务页时插队拉一次，避免任务变更事件反过来触发无限同步轮次。
        // 用户要求：点「快任务」只在编辑窗口显示任务页，笔记列表保持不动（不切 _pane、不隐藏列表）。
        var entered = !_showingTodos;
        _showingTodos = true;
        _selected = null;
        if (entered) _ = _session.Sync.QuickSyncAsync();

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(22, 18, 22, 12),
        };
        var headLeft = new StackPanel { Spacing = 4 };
        headLeft.Children.Add(Ui.Title("任务", Metrics.FontHeader));
        headLeft.Children.Add(Ui.Text(_session.Todos.SummaryText, 12, null, Palette.TextTertiary));
        header.Children.Add(headLeft);

        var headRight = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        headRight.Children.Add(FilterButton("全部", true));
        headRight.Children.Add(FilterButton("仅未完成", false));
        var last = _session.Sync.LastSyncDate;
        headRight.Children.Add(Ui.Text(last == null ? "尚未同步" : "上次同步 " + last.Value.ToString("MM-dd HH:mm"),
            11, null, Palette.TextQuaternary));
        headRight.Children.Add(Ui.Primary("同步", () => _ = _session.Sync.QuickSyncAsync(), 11.5));
        Grid.SetColumn(headRight, 1);
        header.Children.Add(headRight);

        var groups = new StackPanel { Spacing = Metrics.GroupSpacing, Margin = new Thickness(22, 16, 22, 24) };
        var any = false;
        foreach (var status in new[] { TodoStatus.todo, TodoStatus.doing, TodoStatus.done, TodoStatus.terminated })
        {
            if (!_todoShowAll && status is TodoStatus.done or TodoStatus.terminated) continue;
            var items = _session.Todos.InStatus(status);
            if (_expandedTodo != null && _frozenOrder.Count > 0)
                items = items.OrderBy(todo => _frozenOrder.TryGetValue(todo.Id, out var index) ? index : int.MaxValue).ToList();
            groups.Children.Add(GroupHeader(status, items.Count));
            if (_collapsedGroups.Contains(status)) continue;
            if (items.Count == 0)
            {
                groups.Children.Add(Ui.Text("暂无任务", 12, null, Palette.TextQuaternary));
                continue;
            }
            any = true;
            foreach (var todo in items) groups.Children.Add(TodoRow(todo));
        }
        if (!any && _session.Todos.Todos.Count(todo => !todo.Purged) == 0)
            groups.Children.Add(Ui.EmptyState("还没有任务 · 在下方输入第一条任务，回车即可创建。", Glyphs.Check));

        var input = new TextBox
        {
            Watermark = "添加任务，回车即可创建…",
            FontSize = 13,
            Padding = new Thickness(12, 9),
            CornerRadius = new CornerRadius(9),
            BorderBrush = Palette.Line,
        };
        void AddQuick()
        {
            var value = (input.Text ?? "").Trim();
            if (value.Length == 0) return;
            _session.Todos.Add(value);
            input.Text = "";
        }
        input.KeyDown += (_, e) => { if (e.Key == Key.Enter) AddQuick(); };
        var foot = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(20, 12),
            Children = { input, Ui.Primary("添加", AddQuick) },
        };
        Grid.SetColumn((Control)foot.Children[1], 1);

        var page = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Background = Palette.Panel,
            Children =
            {
                new Border { Child = header, BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = Palette.LineSoft },
                new ScrollViewer { Content = groups },
                new Border { Child = foot, BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = Palette.LineSoft },
            },
        };
        Grid.SetRow((Control)page.Children[1], 1);
        Grid.SetRow((Control)page.Children[2], 2);
        _right.Content = page;
        // 首次进入时刷新侧栏与快卡高亮（快任务选中态），笔记列表主体不动
        if (entered)
        {
            RebuildSidebar();
            RebuildQuick();
        }
    }

    private Control FilterButton(string text, bool all)
    {
        var active = all == _todoShowAll;
        var label = Ui.Text(text, 12, active ? FontWeight.SemiBold : null,
            active ? Palette.Accent : Palette.TextTertiary);
        var border = new Border
        {
            Child = label,
            Padding = new Thickness(9, 4),
            CornerRadius = new CornerRadius(6),
            Background = active ? Palette.AccentTint : Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        border.PointerPressed += (_, e) =>
        {
            _todoShowAll = all;
            ShowTodos();
            e.Handled = true;
        };
        return border;
    }

    private Control GroupHeader(TodoStatus status, int count)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var dot = new Border
        {
            Width = 8, Height = 8,
            CornerRadius = new CornerRadius(4),
            Background = Ui.GroupColor(status),
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(dot);
        row.Children.Add(Ui.Text(status.Label(), 13, FontWeight.SemiBold));
        row.Children.Add(Ui.Pill(count.ToString(), Palette.Alpha(ColorOf(status), 26), Ui.GroupColor(status), 11, 9));
        var chevron = Ui.Icon(_collapsedGroups.Contains(status) ? Glyphs.ChevronRight : Glyphs.ChevronDown,
            13, Palette.TextTertiary);
        row.Children.Add(chevron);
        var border = new Border { Child = row, Padding = new Thickness(0, 4), Cursor = new Cursor(StandardCursorType.Hand) };
        border.PointerPressed += (_, e) =>
        {
            if (!_collapsedGroups.Add(status)) _collapsedGroups.Remove(status);
            ShowTodos();
            e.Handled = true;
        };
        return border;
    }

    private static uint ColorOf(TodoStatus status) => status switch
    {
        TodoStatus.todo => 0xE5484D,
        TodoStatus.doing => 0xF5A623,
        TodoStatus.done => 0x1A9E5C,
        _ => 0x86868B,
    };

    private Control TodoRow(Todo todo)
    {
        var readOnly = todo.IsTerminal;
        var expanded = _expandedTodo == todo.Id;

        var ringButton = new Button
        {
            Content = Ui.StateRing(todo.Status),
            Width = 20, Height = 20,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(ringButton, readOnly ? "恢复为待完成" : "标记完成");
        ringButton.Click += (_, _) => _session.Todos.SetStatus(todo.Id,
            readOnly ? TodoStatus.todo : TodoStatus.done);

        var title = Ui.Text(todo.Title, 13, FontWeight.Medium,
            todo.IsOverdue ? Palette.Red : Palette.TextPrimary);
        title.Cursor = new Cursor(StandardCursorType.Hand);
        title.PointerPressed += (_, e) =>
        {
            _expandedTodo = expanded ? null : todo.Id;
            if (expanded) _frozenOrder.Clear();
            else
            {
                var index = 0;
                foreach (var state in new[] { TodoStatus.todo, TodoStatus.doing, TodoStatus.done, TodoStatus.terminated })
                foreach (var item in _session.Todos.InStatus(state)) _frozenOrder[item.Id] = index++;
                if (!readOnly && string.IsNullOrWhiteSpace(VisibleText.From(todo.Notes)))
                    _editingNotesTodo = todo.Id;
            }
            ShowTodos();
            e.Handled = true;
        };

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        if (!readOnly)
        {
            if (todo.Status != TodoStatus.doing)
                actions.Children.Add(Ui.Small("开始", () => _session.Todos.SetStatus(todo.Id, TodoStatus.doing), null, 11));
            if (todo.Status != TodoStatus.done)
                actions.Children.Add(Ui.Small("完成", () => _session.Todos.SetStatus(todo.Id, TodoStatus.done), null, 11));
            if (todo.Status != TodoStatus.terminated)
                actions.Children.Add(Ui.Small("终止", () => _session.Todos.SetStatus(todo.Id, TodoStatus.terminated), null, 11));
        }
        else
        {
            actions.Children.Add(Ui.Small("恢复", () => _session.Todos.SetStatus(todo.Id, TodoStatus.todo), null, 11));
        }
        actions.Children.Add(Ui.Small(Glyphs.Trash, async () =>
        {
            var yes = await Dialogs.Confirm(this, "删除任务？",
                "「" + todo.Title + "」将被永久删除，其他已同步设备上也会一并移除，此操作不可恢复。", "永久删除");
            if (yes) _session.Todos.Remove(todo.Id);
        }, Palette.Red, 11));

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        head.Children.Add(ringButton);
        // W-27：任务行显示「文字[图片]」摘要与最近修改时间
        var titleStack = new StackPanel
        {
            Spacing = 2,
            Margin = new Thickness(9, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        titleStack.Children.Add(title);
        var plainNotes = VisibleText.From(todo.Notes).Trim();
        var imageCount = Regex.Matches(todo.Notes ?? "", "<img[^>]+").Count;
        var preview = plainNotes.Length > 42 ? plainNotes[..42] + "…" : plainNotes;
        if (imageCount > 0) preview += (preview.Length > 0 ? " " : "") + "[图片]";
        if (preview.Length > 0)
            titleStack.Children.Add(Ui.Text(preview, 10.5, null, Palette.TextQuaternary));
        titleStack.Children.Add(Ui.Text("最近修改 " + todo.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            10, null, Palette.TextQuaternary));
        Grid.SetColumn(titleStack, 1);
        head.Children.Add(titleStack);
        if (todo.PlannedAt != null)
        {
            var local = todo.PlannedAt.Value.ToLocalTime();
            var pill = Ui.Pill(local.ToString("M月d日"),
                todo.IsOverdue ? Palette.Panel : Palette.Pill,
                todo.IsOverdue ? Palette.Red : Palette.TextTertiary, 11, 9);
            pill.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(pill, 2);
            head.Children.Add(pill);
        }
        Grid.SetColumn(actions, 3);
        head.Children.Add(actions);

        var frame = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(9, 8, 8, 8),
            Background = todo.IsOverdue ? Palette.RedTint : Brushes.Transparent,
            BorderBrush = todo.IsOverdue ? Palette.Red : Brushes.Transparent,
            BorderThickness = new Thickness(todo.IsOverdue ? 1 : 0),
            Child = head,
        };

        var stack = new StackPanel { Children = { frame } };
        if (!todo.IsOverdue) stack.Children.Add(Ui.Hairline());
        if (expanded) stack.Children.Add(TodoDetail(todo));
        return stack;
    }

    private Control TodoDetail(Todo todo)
    {
        var readOnly = todo.IsTerminal;
        var editing = !readOnly && _editingNotesTodo == todo.Id;
        var body = new StackPanel { Spacing = 8 };

        var topRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        topRow.Children.Add(Ui.Text("备注", 11.5, FontWeight.SemiBold, Palette.TextSecondary));
        if (readOnly) topRow.Children.Add(Ui.Pill("只读", Palette.Pill, Palette.TextTertiary, 10));
        if (!readOnly && !editing && !string.IsNullOrWhiteSpace(todo.Notes))
            topRow.Children.Add(Ui.Small("编辑", () => { _editingNotesTodo = todo.Id; ShowTodos(); }, null, 11));
        body.Children.Add(topRow);

        if (editing)
        {
            var originalPlain = VisibleText.From(todo.Notes);
            var notes = new TextBox
            {
                Text = originalPlain,
                Watermark = "写点备注…",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 90,
                FontSize = 12.5,
                Padding = new Thickness(10),
                CornerRadius = new CornerRadius(6),
                BorderBrush = Palette.Line,
            };
            var noteText = notes;
            void SaveNotes(bool collapse)
            {
                var edited = noteText.Text ?? "";
                // W-27：内容没变就不写盘，避免展开 / 失焦把 updatedAt 推到今天
                if (edited.Trim() != originalPlain.Trim())
                    _session.Todos.UpdateNotes(todo.Id, RebuildTodoNotes(todo.Notes, edited));
                _editingNotesTodo = null;
                if (collapse) { _expandedTodo = null; _frozenOrder.Clear(); }
                ShowTodos();
            }
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(Ui.Primary("保存", () => SaveNotes(false), 11));
            row.Children.Add(Ui.Ghost("保存并收起", () => SaveNotes(true), 11));
            row.Children.Add(Ui.Ghost("插入图片", () => InsertTodoImage(todo.Id), 11));
            row.Children.Add(Ui.Ghost("取消", () => { _editingNotesTodo = null; ShowTodos(); }, 11));
            body.Children.Add(notes);
            body.Children.Add(row);
        }
        else
        {
            var plain = VisibleText.From(todo.Notes);
            if (!string.IsNullOrWhiteSpace(plain))
                body.Children.Add(new TextBlock { Text = plain, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Foreground = Palette.TextSecondary });
            else
                body.Children.Add(Ui.Text(readOnly ? "（无备注）" : "还没有备注", 12, null, Palette.TextQuaternary));
            foreach (var image in TodoImages(todo.Notes)) body.Children.Add(image);
        }

        // 计划日期：年 / 月 / 日紧凑步进 + 日历 + 清除（Mac TodoView.swift:543-699）
        var date = todo.PlannedAt?.ToLocalTime() ?? DateTime.Today;
        var dateRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        dateRow.Children.Add(Ui.Text("设置截止时间", 11.5, null, Palette.TextSecondary));
        if (!readOnly)
        {
            dateRow.Children.Add(Stepper(date.Year.ToString(), () => ChangeDate(todo.Id, date.AddYears(-1)), () => ChangeDate(todo.Id, date.AddYears(1))));
            dateRow.Children.Add(Ui.Text("年", 11, null, Palette.TextTertiary));
            dateRow.Children.Add(Stepper(date.Month.ToString(), () => ChangeDate(todo.Id, date.AddMonths(-1)), () => ChangeDate(todo.Id, date.AddMonths(1))));
            dateRow.Children.Add(Ui.Text("月", 11, null, Palette.TextTertiary));
            dateRow.Children.Add(Stepper(date.Day.ToString(), () => ChangeDate(todo.Id, date.AddDays(-1)), () => ChangeDate(todo.Id, date.AddDays(1))));
            dateRow.Children.Add(Ui.Text("日", 11, null, Palette.TextTertiary));
            dateRow.Children.Add(Ui.Small(Glyphs.Calendar, async () =>
            {
                var picked = await Dialogs.PickDate(this, todo.PlannedAt?.ToLocalTime());
                if (picked != null) ChangeDate(todo.Id, picked.Value);
            }, null, 11));
            dateRow.Children.Add(Ui.Small("清除", () => _session.Todos.SetPlannedAt(todo.Id, null), null, 11));
        }
        else
        {
            dateRow.Children.Add(Ui.Text(todo.PlannedAt?.ToLocalTime().ToString("yyyy-MM-dd") ?? "未设置", 11.5, null, Palette.TextQuaternary));
        }
        body.Children.Add(dateRow);

        var info = new List<string>();
        if (todo.StartedAt != null) info.Add("开始 " + todo.StartedAt.Value.ToLocalTime().ToString("MM-dd HH:mm"));
        if (todo.CompletedAt != null) info.Add("完成 " + todo.CompletedAt.Value.ToLocalTime().ToString("MM-dd HH:mm"));
        if (todo.Status == TodoStatus.terminated) info.Add("已终止");
        if (info.Count > 0)
            body.Children.Add(Ui.Text(string.Join(" · ", info), 11, null, Palette.TextQuaternary));

        return new Border
        {
            Background = Palette.PanelSoft,
            BorderBrush = Palette.Alpha(0xCFE9D9, 166),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12),
            Margin = new Thickness(0, 6, 0, 10),
            Child = body,
        };
    }

    private static Control Stepper(string value, Action dec, Action inc)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        row.Children.Add(Ui.Small("−", dec, null, 11));
        row.Children.Add(Ui.Pill(value, Palette.Panel, Palette.TextPrimary, 11, 5));
        row.Children.Add(Ui.Small("＋", inc, null, 11));
        return row;
    }

    private void ChangeDate(Guid id, DateTime value)
    {
        // W-27：年步进钳制在 2000–2100
        var year = Math.Clamp(value.Year, 2000, 2100);
        var date = new DateTime(year, value.Month, value.Day, 12, 0, 0, DateTimeKind.Local);
        _session.Todos.SetPlannedAt(id, date.ToUniversalTime());
    }

    private async void InsertTodoImage(Guid id)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider == null) return;
        try
        {
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择一张图片",
                AllowMultiple = false,
                FileTypeFilter = [FilePickerFileTypes.ImageAll],
            });
            var file = files.FirstOrDefault();
            var source = file?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(source)) return;
            var relative = _session.Todos.Attachments.ImportImage(source, id);
            var todo = _session.Todos.Find(id);
            if (todo == null) return;
            _session.Todos.UpdateNotes(id, (todo.Notes ?? "") + "<img src=\"" + relative + "\">");
        }
        catch (Exception error)
        {
            _banner.Text = "插入图片失败：" + error.Message;
        }
    }

    private IEnumerable<Control> TodoImages(string notes)
    {
        if (string.IsNullOrEmpty(notes)) yield break;
        foreach (Match match in Regex.Matches(notes, "<img[^>]+src=\"([^\"]+)\"[^>]*>"))
        {
            var relative = match.Groups[1].Value;
            var path = _session.Todos.Attachments.AbsolutePath(relative);
            if (!File.Exists(path)) continue;
            Image? thumb = null;
            try
            {
                thumb = new Image
                {
                    Source = new Bitmap(path),
                    MaxWidth = 320,
                    MaxHeight = 200,
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(0, 4, 0, 0),
                };
            }
            catch { /* 损坏的图片不影响备注显示 */ }
            if (thumb != null) yield return thumb;
        }
    }

    /// <summary>备注里的图片标签要保留，其余文字重新包成受控 &lt;p&gt;。</summary>
    private static string RebuildTodoNotes(string original, string plain)
    {
        var images = "";
        if (!string.IsNullOrEmpty(original))
            images = string.Concat(Regex.Matches(original, "<img[^>]+src=\"attachments/[^\"]+\"[^>]*>")
                .Select(match => match.Value));
        var text = plain.Trim();
        if (text.Length == 0) return images;
        var escaped = text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
            .Replace("\r\n", "</p><p>").Replace("\n", "</p><p>");
        return "<p>" + escaped + "</p>" + images;
    }

    // ── 侧栏 ───────────────────────────────────────────────────────────
    private void RebuildSidebar()
    {
        _sidebar.Children.Clear();
        _sidebar.Margin = new Thickness(_sidebarWidth <= Metrics.SidebarTiny ? 6 : 10, 12, 6, 12);
        _sidebarBottom.Margin = new Thickness(_sidebarWidth <= Metrics.SidebarTiny ? 6 : 10, 4, _sidebarWidth <= Metrics.SidebarTiny ? 6 : 10, 8);

        var avatar = Ui.AvatarBadge(_session.Avatar.Current, _sidebarWidth <= Metrics.SidebarTiny ? 24 : 36);
        avatar.Margin = new Thickness(4, 4, 0, 6);
        avatar.Cursor = new Cursor(StandardCursorType.Hand);
        avatar.PointerPressed += (_, e) =>
        {
            _ = Dialogs.PickAvatar(this, _session);
            e.Handled = true;
        };
        ToolTip.SetTip(avatar, "点击更换头像 · " + _session.Avatar.Current.Glyph);

        // 齿轮按钮：宽档在账号区右侧、窄/中档在头像下方（Mac accountHeader）
        var gearIcon = Ui.Icon(Glyphs.Gear, _sidebarWidth == Metrics.SidebarExpanded ? 22 : 20, Palette.TextTertiary);
        var gearButton = new Button
        {
            Content = gearIcon,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Width = 32, Height = 32,
            Padding = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        gearButton.PointerEntered += (_, _) => Icons.Tint(gearIcon, Palette.Accent);
        gearButton.PointerExited += (_, _) => Icons.Tint(gearIcon, Palette.TextTertiary);
        gearButton.Click += (_, _) => OpenSettings();
        ToolTip.SetTip(gearButton, "同步与存储设置");

        if (_sidebarWidth == Metrics.SidebarExpanded)
        {
            // 宽档账号区 = 头像 + 真实昵称 + 绿点 + 齿轮（取不到昵称显示「同笔记」）
            var account = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                Margin = new Thickness(0, 4, 0, 8),
            };
            account.Children.Add(avatar);
            var accountNameBlock = new TextBlock
            {
                Text = _accountName,
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                Foreground = Palette.TextPrimary,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var details = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 6, 0),
                Spacing = 2,
                Children =
                {
                    accountNameBlock,
                    Ui.Text("● 已保存到本地", 10, null, Palette.Accent),
                },
            };
            account.Children.Add(details);
            Grid.SetColumn(details, 1);
            gearButton.VerticalAlignment = VerticalAlignment.Center;
            account.Children.Add(gearButton);
            Grid.SetColumn(gearButton, 2);
            _sidebar.Children.Add(account);
            _ = LoadAccountName();
        }
        else
        {
            // 窄/中档：头像 + 齿轮（垂直，居中）
            avatar.Margin = new Thickness(0);
            var header = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                Spacing = 4,
                Margin = new Thickness(0, 4, 0, 8),
                Children = { avatar, gearButton },
            };
            _sidebar.Children.Add(header);
        }
        _sidebar.Children.Add(AccentButton(_sidebarWidth == Metrics.SidebarExpanded ? "新建笔记" : "＋", NewNote));

        // groupDivider：新建笔记与「全部笔记」之间的分割线（非宽档，Mac SidebarView groupDivider）
        if (_sidebarWidth < Metrics.SidebarExpanded) _sidebar.Children.Add(Ui.Divider(marginV: 8));
        _sidebar.Children.Add(NotebookButton(Notebooks.All,
            _pane == Pane.Notes && _notebookFilter == null && _tagFilter == null,
            () => { _pane = Pane.Notes; _notebookFilter = null; _tagFilter = null; Refresh(); },
            _session.Notes.CountInNotebook(Notebooks.All), deletable: false));

        foreach (var name in _session.Notebooks.List)
        {
            var notebook = name;
            _sidebar.Children.Add(NotebookButton(notebook,
                _pane == Pane.Notes && _notebookFilter == notebook && _tagFilter == null,
                () => { _pane = Pane.Notes; _notebookFilter = notebook; _tagFilter = null; Refresh(); },
                _session.Notes.CountInNotebook(notebook),
                deletable: _session.Notebooks.Custom.Contains(notebook)));
        }
        // sectionDivider：笔记本与标签之间的分割线（三档，Mac SidebarView sectionDivider）
        _sidebar.Children.Add(Ui.Divider(marginV: 8));
        var tags = _session.Notes.AllTags;
        if (tags.Count == 0)
        {
            _sidebar.Children.Add(Ui.Text(_sidebarWidth <= Metrics.SidebarTiny ? "" : "暂无标签", 11, null, Palette.TextQuaternary));
        }
        else
        {
            foreach (var tag in tags)
            {
                var name = tag;
                var button = SideButton(name, _tagFilter == name,
                    () => { _pane = Pane.Notes; _tagFilter = name; _notebookFilter = null; Refresh(); },
                    _session.Notes.CountWithTag(name), glyph: Glyphs.Tag);
                var menu = new ContextMenu();
                var remove = new MenuItem { Header = "删除标签" };
                remove.Click += async (_, _) =>
                {
                    var yes = await Dialogs.Confirm(this, "删除标签？",
                        "「" + name + "」会从所有笔记上移除，笔记本身不会删除。", "删除标签");
                    if (yes) _session.Notes.RemoveTag(name);
                };
                menu.Items.Add(remove);
                button.ContextMenu = menu;
                _sidebar.Children.Add(button);
            }
        }

        // 回收站固定在侧栏底部（不随列表滚动），带图标；「快任务」从侧栏移除
        _sidebarBottom.Children.Clear();
        _sidebarBottom.Children.Add(Ui.Divider());
        _sidebarBottom.Children.Add(SideButton("回收站", _pane == Pane.Trash,
            () => { _pane = Pane.Trash; _notebookFilter = null; _tagFilter = null; Refresh(); },
            _session.Notes.TrashNotes.Count, glyph: Glyphs.Trash, ghost: true));
    }

    /// <summary>右键菜单「新建笔记本」：弹出输入框，成功后切到该笔记本。</summary>
    private async void PromptNewNotebook()
    {
        var name = await Dialogs.Prompt(this, "新建笔记本", "输入笔记本名称（≤12 字）", "新笔记本（≤12 字）");
        if (string.IsNullOrWhiteSpace(name)) return;
        var message = _session.Notebooks.Add(name);
        if (message == null)
        {
            _pane = Pane.Notes;
            _notebookFilter = name.Trim();
            _tagFilter = null;
            Refresh();
        }
        else
        {
            await Dialogs.Confirm(this, "无法新建笔记本", message, "知道了", "取消", danger: false);
        }
    }

    private void RebuildQuick()
    {
        _quick.Children.Clear();
        _quick.IsVisible = _pane == Pane.Notes;
        if (_pane != Pane.Notes) return;

        _quick.Children.Add(QuickToggle());
        if (!_quickExpanded) return;

        _quick.Children.Add(QuickCard("快同步", "多设备共享 · 一处编辑处处可见", _selected == Note.QuickSyncNoteId,
            () => { _pane = Pane.Notes; Select(Note.QuickSyncNoteId); }, 0, 0, Glyphs.Cloud));
        _quick.Children.Add(QuickCard("快任务",
            $"待完成 {_session.Todos.PendingCount} · 进行中 {_session.Todos.DoingCount}",
            _showingTodos, ShowTodos, _session.Todos.ActiveCount, _session.Todos.OverdueCount, Glyphs.Check));
        // 快任务与第一篇笔记之间的分割线（_quick 已含 12px 左右边距，这里不再重复）
        var divider = Ui.Hairline();
        _quick.Children.Add(divider);
    }

    /// <summary>「线 - 收起/展开图标 - 线」分组头（Mac quickEntriesToggle）。</summary>
    private Control QuickToggle()
    {
        var chevron = Ui.Icon(_quickExpanded ? Glyphs.ChevronUp : Glyphs.ChevronDown, 9, Palette.TextQuaternary);
        var button = new Button
        {
            Content = chevron,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(5, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        button.Click += (_, _) =>
        {
            _quickExpanded = !_quickExpanded;
            RebuildQuick();
        };
        ToolTip.SetTip(button, _quickExpanded ? "收起快同步与快任务" : "展开快同步与快任务");

        var left = Ui.Hairline();
        left.VerticalAlignment = VerticalAlignment.Center;
        var right = Ui.Hairline();
        right.VerticalAlignment = VerticalAlignment.Center;
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,*") };
        row.Children.Add(left);
        Grid.SetColumn(button, 1);
        row.Children.Add(button);
        Grid.SetColumn(right, 2);
        row.Children.Add(right);
        return row;
    }

    private void RebuildSort()
    {
        _sortRow.Children.Clear();
        _sortRow.IsVisible = _pane is Pane.Notes or Pane.Trash;
        AddSort("最近更新", SortMode.Updated, 0);
        AddSort("按创建", SortMode.Created, 1);
        AddSort("按标题", SortMode.Title, 2);
        // 同步进度移到排序条件之后（最右，Mac filterBar 的 syncButton 位置）
        Grid.SetColumn(_syncSlot, 4);
        _sortRow.Children.Add(_syncSlot);
    }

    private void AddSort(string label, SortMode mode, int column)
    {
        var active = _sort == mode;
        var text = Ui.Text(label, 12, active ? FontWeight.SemiBold : null,
            active ? Palette.Accent : Palette.TextTertiary);
        text.Cursor = new Cursor(StandardCursorType.Hand);
        text.PointerPressed += (_, _) => { _sort = mode; RebuildSort(); RebuildCards(); };
        var wrap = new Border
        {
            Child = text,
            BorderThickness = new Thickness(0, 0, 0, active ? 2 : 0),
            BorderBrush = Palette.Accent,
            Padding = new Thickness(0, 0, 0, 3),
            Margin = new Thickness(0, 0, 14, 0),
        };
        Grid.SetColumn(wrap, column);
        _sortRow.Children.Add(wrap);
    }

    private void RebuildCards()
    {
        _cards.Children.Clear();
        var notes = Filtered().ToList();
        if (notes.Count == 0)
        {
            _cards.Children.Add(Ui.EmptyState(_pane == Pane.Trash ? "回收站是空的" : "没有匹配的笔记", Glyphs.Note));
            _cards.Margin = new Thickness(0, 40, 0, 0);
            return;
        }
        _cards.Margin = new Thickness(0);

        foreach (var note in notes)
        {
            var current = note;
            var selected = _selected == current.Id;
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            if (current.Pinned)
                titleRow.Children.Add(Ui.Icon(Glyphs.Pin, 12, Palette.Amber));
            var headline = Ui.Text(current.Title, Metrics.FontCardTitle, FontWeight.SemiBold, Palette.TextPrimary);
            headline.TextTrimming = TextTrimming.CharacterEllipsis;
            titleRow.Children.Add(headline);

            var snippet = Ui.Text(current.Snippet, Metrics.FontSnippet, null, Palette.TextTertiary);
            snippet.TextWrapping = TextWrapping.Wrap;
            snippet.MaxLines = 2;
            var metaRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            metaRow.Children.Add(Ui.Text(current.TimeText, Metrics.FontMeta, null, Palette.TextQuaternary));
            foreach (var tag in current.Tags.Take(2))
                metaRow.Children.Add(Ui.Pill("#" + tag, Palette.TagChip, Palette.TextTertiary, Metrics.FontTag, 999));

            // 设计稿 .note-card：11px padding / 8px 圆角 / 1px 透明边框。
            // 旧实现未选中时是直角 + 底部硬线，看起来像列表控件而非卡片。
            var card = new Border
            {
                Background = selected ? Palette.CardSelected : Palette.Panel,
                BorderBrush = selected ? Palette.CardSelectedBorder : Palette.Transparent,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(Metrics.RadiusCard),
                Margin = new Thickness(12, 4),
                Padding = new Thickness(Metrics.CardPadding, 10),
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = new StackPanel { Spacing = 4, Children = { titleRow, snippet, metaRow } },
            };
            if (!selected)
            {
                card.WithHover(Palette.Panel, Palette.CardHover);
                card.WithElevate();
            }
            card.PointerPressed += (_, e) => { Select(current.Id); e.Handled = true; };

            var menu = new ContextMenu();
            if (_pane == Pane.Trash)
            {
                var restore = new MenuItem { Header = "恢复笔记" };
                restore.Click += (_, _) => { _session.Notes.Restore(current.Id); _pane = Pane.Notes; Select(current.Id); };
                menu.Items.Add(restore);
                menu.Items.Add(new Separator());
                var purge = new MenuItem { Header = "彻底删除笔记", Foreground = Palette.Red };
                purge.Click += async (_, _) =>
                {
                    var yes = await Dialogs.Confirm(this, "彻底删除笔记？",
                        "此操作不可恢复，笔记将被永久移除。", "彻底删除");
                    if (yes) { _session.Notes.PermanentlyDelete(current.Id); SelectFirstRemaining(); }
                };
                menu.Items.Add(purge);
            }
            else
            {
                var pin = new MenuItem { Header = current.Pinned ? "取消置顶笔记" : "置顶笔记" };
                pin.Click += (_, _) => _session.Notes.TogglePin(current.Id);
                menu.Items.Add(pin);
                menu.Items.Add(new Separator());
                var trash = new MenuItem { Header = "删除笔记", Foreground = Palette.Red };
                trash.Click += async (_, _) =>
                {
                    var yes = await Dialogs.Confirm(this, "删除笔记？",
                        "「" + current.Title + "」将移入回收站，可随时在回收站中恢复。", "删除笔记", "取消", true);
                    if (yes) _session.Notes.MoveToTrash(current.Id);
                };
                menu.Items.Add(trash);
            }
            card.ContextMenu = menu;
            _cards.Children.Add(card);
            // 每条笔记下方 1pt 发丝线（Mac noteDivider：#EFEFF2，含最后一篇）
            var divider = Ui.Hairline();
            divider.Margin = new Thickness(12, 0, 12, 0);
            _cards.Children.Add(divider);
        }
    }

    private IEnumerable<Note> Filtered()
    {
        IEnumerable<Note> source = _pane == Pane.Trash
            ? _session.Notes.TrashNotes
            : _session.Notes.ActiveNotes.Where(note => !note.IsQuickSync);
        if (_pane != Pane.Trash && _notebookFilter != null)
            source = source.Where(note => note.Notebook == _notebookFilter);
        if (_pane != Pane.Trash && _tagFilter != null)
            source = source.Where(note => note.Tags.Contains(_tagFilter));
        // W-24：回收站内搜索同样生效（PC 修正 Mac 缺陷），并支持按标签命中
        if (!string.IsNullOrWhiteSpace(_search.Text))
        {
            var query = _search.Text.Trim();
            source = source.Where(note =>
                note.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || note.VisibleBody.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || note.Tags.Any(tag => tag.Contains(query, StringComparison.CurrentCultureIgnoreCase)));
        }
        return _sort switch
        {
            SortMode.Created => source.OrderByDescending(note => note.CreatedAt),
            SortMode.Title => source.OrderBy(note => note.Title, StringComparer.CurrentCulture),
            // W-42：回收站按删除时间排序，不是更新时间
            _ => _pane == Pane.Trash
                ? source.OrderByDescending(note => note.DeletedAt ?? note.UpdatedAt)
                : source.OrderByDescending(note => note.UpdatedAt),
        };
    }

    // ── 编辑器数据操作 ─────────────────────────────────────────────────
    private void SaveTitle()
    {
        if (_loading || _selected == null) return;
        var note = Current();
        if (note == null || note.IsTrashed || note.Title == _title.Text) return;
        _session.Notes.UpdateTitle(note.Id, _title.Text ?? "");
    }

    private void SaveNotebook()
    {
        if (_loading || _selected == null || _notebook.SelectedItem is not string name) return;
        var note = Current();
        if (note == null || note.IsQuickSync || note.Notebook == name) return;
        _session.Notes.SetNotebook(note.Id, name);
    }

    private void AddTag()
    {
        if (_selected == null) return;
        var note = Current();
        if (note == null || note.IsQuickSync || note.IsTrashed) return;
        var text = (_tagInput.Text ?? "").Trim();
        if (text.Length == 0) return;
        var tags = note.Tags.ToList();
        if (!tags.Contains(text)) tags.Add(text);
        _tagInput.Text = "";
        _session.Notes.UpdateTags(note.Id, tags);
        RenderTags(note);
    }

    private void RenderTags(Note note)
    {
        _tags.Children.Clear();
        foreach (var tag in note.Tags)
        {
            var name = tag;
            _tags.Children.Add(Ui.Chip(name, () =>
            {
                var latest = Current();
                if (latest == null) return;
                _session.Notes.UpdateTags(latest.Id, latest.Tags.Where(item => item != name));
                var refreshed = Current();
                if (refreshed != null) RenderTags(refreshed);
            }));
        }
    }

    private void TogglePin()
    {
        if (_selected != null) _session.Notes.TogglePin(_selected.Value);
    }

    private async void MoveCurrentToTrash()
    {
        if (_selected == null) return;
        var note = Current();
        if (note == null) return;
        var yes = await Dialogs.Confirm(this, "删除笔记？",
            "「" + note.Title + "」将移入回收站，可随时在回收站中恢复。", "删除笔记", "取消", true);
        if (!yes) return;
        var id = note.Id;
        _session.Notes.MoveToTrash(id);
        var next = _session.Notes.ActiveNotes.FirstOrDefault(item => !item.IsQuickSync);
        if (next == null)
        {
            _selected = null;
            _right.Content = Ui.EmptyState("选择或新建一篇笔记");
            Refresh();
        }
        else Select(next.Id);
    }

    private void RestoreCurrent()
    {
        if (_selected == null) return;
        var id = _selected.Value;
        _session.Notes.Restore(id);
        _pane = Pane.Notes;
        Select(id);
    }

    private async void PurgeCurrent()
    {
        if (_selected == null) return;
        var note = Current();
        if (note == null) return;
        var yes = await Dialogs.Confirm(this, "彻底删除笔记？",
            "此操作不可恢复，笔记将被永久移除。", "彻底删除");
        if (!yes) return;
        _session.Notes.PermanentlyDelete(note.Id);
        SelectFirstRemaining();
    }

    /// <summary>W-26：删除 / 彻底删除后统一回退选中下一条，避免空白编辑器。</summary>
    private void SelectFirstRemaining()
    {
        var next = _pane == Pane.Trash
            ? _session.Notes.TrashNotes.FirstOrDefault()
            : _session.Notes.ActiveNotes.FirstOrDefault(item => !item.IsQuickSync);
        if (next == null)
        {
            _selected = null;
            _right.Content = Ui.EmptyState(_pane == Pane.Trash ? "回收站是空的" : "选择或新建一篇笔记");
            Refresh();
        }
        else Select(next.Id);
    }

    /// <summary>W-20：App 级快捷键。WebView2 内的键位由 EditorHtml 的 JS 处理。</summary>
    private void OnShellKeyDown(object? sender, KeyEventArgs e)
    {
        var mod = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (mod && e.Key == Key.N)
        {
            NewNote();
            e.Handled = true;
        }
        else if (mod && e.Key == Key.F)
        {
            _search.Focus();
            _search.SelectAll();
            e.Handled = true;
        }
        else if (mod && e.Key == Key.W)
        {
            (TopLevel.GetTopLevel(this) as Window)?.Close();
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            _ = _session.Sync.QuickSyncAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && _pane == Pane.Trash)
        {
            PurgeCurrent();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (!string.IsNullOrEmpty(_search.Text)) _search.Text = "";
            e.Handled = true;
        }
    }

    /// <summary>W-22：关窗 / 失活前把编辑器里最后一次内容立刻落盘。</summary>
    public void Flush() => _richEditor.Flush();

    private void NewNote()
    {
        var notebook = _notebookFilter is null || _notebookFilter == Notebooks.All ? "工作日志" : _notebookFilter;
        var note = _session.Notes.AddNote(notebook);
        if (_tagFilter != null) _session.Notes.UpdateTags(note.Id, [_tagFilter]);
        _pane = Pane.Notes;
        Select(note.Id);
    }

    private async void OpenSettings()
    {
        _pane = Pane.Settings;
        RebuildSidebar();
        await Dialogs.ShowSettings(this, _session, () => LogoutRequested?.Invoke());
        _pane = Pane.Notes;
        RebuildSidebar();
    }

    private List<string> NotebookChoices(Note note)
    {
        var list = _session.Notebooks.List.ToList();
        if (!list.Contains(note.Notebook)) list.Insert(0, note.Notebook);
        return list;
    }

    private Note? Current() => _selected == null ? null : _session.Notes.Find(_selected.Value);

    // ── 侧栏三档吸附与分隔条 ───────────────────────────────────────────
    private Border BuildHandle()
    {
        // 竖线（3 宽，聚焦时变浅绿）居中，「>」小块叠在线上，左右白边各 1
        var line = new Border
        {
            Width = 3,
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = Palette.SidebarBg,
        };
        var handle = new Border
        {
            Background = Palette.SidebarBg,
            Cursor = new Cursor(StandardCursorType.SizeWestEast),
            Child = new Grid
            {
                Children =
                {
                    line,
                    new Border
                    {
                        Padding = new Thickness(1, 0),
                        Height = 18,
                        Background = Palette.Panel,
                        Child = _handleGlyph,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        IsHitTestVisible = false,
                    },
                },
            },
        };
        // 聚焦色：主题绿再浅一半（与白色 50% 混合）
        var hoverBrush = new SolidColorBrush(Color.Parse("#8CCEAD"));
        // 整个分隔区域（全高、全宽）聚焦时底色再浅一半，中间 3px 竖线保持浅绿
        var areaBrush = new SolidColorBrush(Color.Parse("#F5F5F5")); // 灰度 4%
        void Hover(bool on)
        {
            handle.Background = areaBrush; // 常态与聚焦都是灰白
            line.Background = on ? hoverBrush : areaBrush;
        }
        Hover(false);
        handle.PointerEntered += (_, _) => Hover(true);
        handle.PointerExited += (_, _) => { if (!_dragging) Hover(false); };
        handle.PointerPressed += (_, e) =>
        {
            _dragging = true; _didSwitch = false;
            _press = e.GetPosition(this);
            e.Pointer.Capture(handle);
        };
        handle.PointerMoved += (_, e) =>
        {
            if (!_dragging || _didSwitch) return;
            var point = e.GetPosition(this);
            if (Math.Abs(point.X - _press.X) < 4) return;
            var width = point.X < Metrics.SidebarThresholdTiny ? Metrics.SidebarTiny
                : point.X < Metrics.SidebarThresholdCompact ? Metrics.SidebarCompact
                : Metrics.SidebarExpanded;
            if ((int)width == _sidebarWidth) return;
            _didSwitch = true;
            SetSidebar((int)width);
        };
        handle.PointerReleased += (_, e) =>
        {
            var moved = _didSwitch;
            _dragging = false;
            e.Pointer.Capture(null);
            Hover(false);
            if (!moved)
                SetSidebar(_sidebarWidth == Metrics.SidebarTiny ? (int)Metrics.SidebarCompact
                    : _sidebarWidth == Metrics.SidebarCompact ? (int)Metrics.SidebarExpanded
                    : (int)Metrics.SidebarTiny);
        };
        return handle;
    }

    private void SetSidebar(int width)
    {
        _sidebarWidth = width;
        if (Content is Grid grid) grid.ColumnDefinitions[0].Width = new GridLength(width);
        SetHandleGlyph(width == Metrics.SidebarExpanded);
        RebuildSidebar();
    }

    // ── 通用控件 ───────────────────────────────────────────────────────
    private Button AccentButton(string text, Action click)
    {
        var expanded = _sidebarWidth == Metrics.SidebarExpanded;
        var tiny = _sidebarWidth <= Metrics.SidebarTiny;
        // Mac newNoteButton：宽档＝全宽圆角条「＋ 新建笔记」；窄/中档＝圆形（44）
        var plus = Ui.Icon(Glyphs.Plus, expanded ? 13 : tiny ? 12 : 18, Palette.White);
        Control content = expanded
            ? new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children = { plus, Ui.Text(text, 13, FontWeight.SemiBold, Palette.White) },
            }
            : plus;
        var size = tiny ? 24 : 44;
        var button = new Button
        {
            Content = content,
            Background = Palette.Accent,
            Foreground = Palette.White,
            FontWeight = FontWeight.SemiBold,
            FontSize = 13,
            HorizontalAlignment = expanded ? HorizontalAlignment.Stretch : HorizontalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(expanded ? Metrics.RadiusCard : size / 2),
            Width = expanded ? double.NaN : size,
            Height = expanded ? 34 : size,
            Margin = new Thickness(0, 4, 0, 8),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        button.Click += (_, _) => click();
        return button;
    }

    private Button SideButton(string text, bool selected, Action click, int? count = null,
        int? badge = null, string? glyph = null, bool iconOnly = false, bool ghost = false)
    {
        var tiny = _sidebarWidth <= Metrics.SidebarTiny;
        var label = tiny && glyph == null ? text[..1].ToString()
            : count == null ? text : $"{text}  {count}";
        var fg = selected ? Palette.AccentHover : Palette.TextSecondary;
        var iconColor = selected ? Palette.Accent : Palette.TextTertiary;
        var icon = glyph == null ? null : Ui.Icon(glyph, tiny ? 15 : 17, iconColor);
        var button = new Button
        {
            Background = (selected && !ghost) ? Palette.AccentTint : Brushes.Transparent,
            Foreground = fg,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = iconOnly || _sidebarWidth != Metrics.SidebarExpanded
                ? HorizontalAlignment.Center : HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(_sidebarWidth == Metrics.SidebarExpanded ? 7 : Metrics.RadiusCard),
            Padding = new Thickness(8, 7),
            FontSize = tiny ? Metrics.FontSidebarTiny
                : _sidebarWidth == Metrics.SidebarExpanded ? Metrics.FontSidebarWide : Metrics.FontSidebarCompact,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        button.Transitions = new Avalonia.Animation.Transitions
        {
            new Avalonia.Animation.BrushTransition
            {
                Property = Button.BackgroundProperty, Duration = System.TimeSpan.FromMilliseconds(120),
            },
        };

        TextBlock? labelBlock = null;
        if (iconOnly)
        {
            button.Content = icon;
        }
        else if (badge is > 0 && !tiny)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            grid.Children.Add(Ui.Text(label, 12, selected ? FontWeight.SemiBold : null, fg));
            var chip = Ui.Badge(badge.Value);
            chip.Margin = new Thickness(6, 0, 0, 0);
            Grid.SetColumn(chip, 1);
            grid.Children.Add(chip);
            button.Content = grid;
        }
        else if (icon != null && !tiny && count != null)
        {
            // 标准/宽档：图标 + 名称居左，数量居右，撑满整行
            labelBlock = Ui.Text(text, 12, selected ? FontWeight.SemiBold : null, fg);
            labelBlock.TextTrimming = TextTrimming.CharacterEllipsis;
            var left = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 9,
                HorizontalAlignment = HorizontalAlignment.Left,
                Children = { icon, labelBlock },
            };
            var countBlock = Ui.Text(count.Value.ToString(), 11, null, Palette.TextQuaternary);
            countBlock.VerticalAlignment = VerticalAlignment.Center;
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            grid.Children.Add(left);
            Grid.SetColumn(countBlock, 1);
            grid.Children.Add(countBlock);
            button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            button.Content = grid;
        }
        else if (icon != null && !tiny)
        {
            labelBlock = Ui.Text(label, 12, selected ? FontWeight.SemiBold : null, fg);
            button.Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 9,
                Children = { icon, labelBlock },
            };
        }
        else if (icon != null) button.Content = icon;
        else button.Content = Ui.Text(label, button.FontSize, selected ? FontWeight.SemiBold : null, fg);

        if (ghost)
        {
            // 无背景变体：hover / 选中只让文字与图标变绿，不给底色块
            var ic = icon;
            var lbl = labelBlock;
            void TintGhost(IBrush brush)
            {
                if (ic != null) Icons.Tint(ic, brush);
                if (lbl != null) lbl.Foreground = brush;
            }
            button.PointerEntered += (_, _) => TintGhost(Palette.AccentHover);
            button.PointerExited += (_, _) => TintGhost(selected ? Palette.AccentHover : Palette.TextSecondary);
        }
        else if (!selected)
        {
            button.PointerEntered += (_, _) => button.Background = Palette.SidebarHover;
            button.PointerExited += (_, _) => button.Background = Brushes.Transparent;
        }
        button.Click += (_, _) => click();
        // 极窄 / 窄档的 hover 名称浮窗（Mac SidebarView.swift:638-665）
        if (_sidebarWidth < Metrics.SidebarExpanded)
            ToolTip.SetTip(button, text + (count == null ? "" : " · " + count + " 篇笔记"));
        return button;
    }

    /// <summary>笔记本按名称映射专属图标（对应 Mac notebookIcon 语义）；未匹配的自定义笔记本用默认文件夹。</summary>
    private static string NotebookIcon(string name) => name switch
    {
        "工作日志" => Glyphs.Pencil,
        "读书笔记" => Glyphs.BookClosed,
        "投资研究" => Glyphs.Chart,
        "灵感速记" => Glyphs.Bulb,
        _ => Glyphs.Folder,
    };

    /// <summary>笔记本条目：名称/图标居左、数量居右，无背景，hover/选中只让图标与文字变绿；右键菜单「新建笔记本 / 删除笔记本」。</summary>
    private Button NotebookButton(string name, bool selected, Action click, int count, bool deletable)
    {
        var tiny = _sidebarWidth <= Metrics.SidebarTiny;
        var expandedFont = _sidebarWidth == Metrics.SidebarExpanded;
        var wide = expandedFont; // 仅宽档：名称居左、数量居右；标准档为上下结构
        var nameColor = selected ? Palette.AccentHover : Palette.TextSecondary;
        var iconGlyph = name == Notebooks.All ? Glyphs.Tray : NotebookIcon(name);
        var icon = Ui.Icon(iconGlyph, tiny ? 14 : wide ? 13 : 17, selected ? Palette.Accent : Palette.TextTertiary);
        var nameBlock = Ui.Text(name, tiny ? Metrics.FontSidebarTiny : expandedFont ? Metrics.FontSidebarWide : Metrics.FontSidebarCompact,
            selected ? FontWeight.SemiBold : null, nameColor);
        nameBlock.TextTrimming = TextTrimming.CharacterEllipsis;

        Control content;
        if (tiny)
        {
            content = icon; // 极窄档只显示图标
        }
        else if (wide)
        {
            var left = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 9,
                HorizontalAlignment = HorizontalAlignment.Left,
                Children = { icon, nameBlock },
            };
            var countBlock = Ui.Text(count.ToString(), 11, null, Palette.TextQuaternary);
            countBlock.VerticalAlignment = VerticalAlignment.Center;
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            grid.Children.Add(left);
            Grid.SetColumn(countBlock, 1);
            grid.Children.Add(countBlock);
            content = grid;
        }
        else
        {
            // 标准档：图标在上、文案在下
            nameBlock.HorizontalAlignment = HorizontalAlignment.Center;
            nameBlock.TextAlignment = TextAlignment.Center;
            content = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Spacing = 3,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children = { icon, nameBlock },
            };
        }

        var button = new Button
        {
            Content = content,
            Background = selected ? Palette.AccentTint : Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = wide ? HorizontalAlignment.Stretch : HorizontalAlignment.Center,
            CornerRadius = new CornerRadius(wide ? 7 : Metrics.RadiusCard),
            Padding = new Thickness(wide ? 4 : 8, 7),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        button.Transitions = new Avalonia.Animation.Transitions
        {
            new Avalonia.Animation.BrushTransition
            {
                Property = Button.BackgroundProperty, Duration = System.TimeSpan.FromMilliseconds(120),
            },
        };

        // 选中＝浅绿底 + 图标主题绿 + 文字深绿；hover＝浅灰底 + 变绿
        void Tint(IBrush iconBrush, IBrush nameBrush)
        {
            Icons.Tint(icon, iconBrush);
            nameBlock.Foreground = nameBrush;
        }
        button.PointerEntered += (_, _) =>
        {
            if (!selected) button.Background = Palette.SidebarHover;
            Tint(Palette.Accent, Palette.AccentHover);
        };
        button.PointerExited += (_, _) =>
        {
            button.Background = selected ? Palette.AccentTint : Brushes.Transparent;
            Tint(selected ? Palette.Accent : Palette.TextTertiary,
                 selected ? Palette.AccentHover : Palette.TextSecondary);
        };
        button.Click += (_, _) => click();

        // 右键菜单：新建笔记本 / 删除笔记本（Mac SidebarView.swift:372-378）
        var menu = new ContextMenu();
        var addItem = new MenuItem { Header = "新建笔记本" };
        addItem.Click += (_, _) => PromptNewNotebook();
        menu.Items.Add(addItem);
        if (deletable)
        {
            var renameItem = new MenuItem { Header = "修改笔记本" };
            renameItem.Click += async (_, _) =>
            {
                var newName = await Dialogs.Prompt(this, "修改笔记本", "输入新的笔记本名称（≤12 字）", "笔记本名称（≤12 字）", name);
                if (string.IsNullOrWhiteSpace(newName) || newName.Trim() == name) return;
                var err = _session.Notebooks.Rename(name, newName);
                if (err != null)
                {
                    await Dialogs.Confirm(this, "无法修改笔记本", err, "知道了", "取消", danger: false);
                    return;
                }
                _session.Notes.MoveNotes(name, newName.Trim());
                if (_notebookFilter == name) _notebookFilter = newName.Trim();
                Refresh();
            };
            menu.Items.Add(renameItem);
            var delItem = new MenuItem { Header = "删除笔记本" };
            delItem.Click += async (_, _) =>
            {
                var cnt = _session.Notes.CountInNotebook(name);
                var yes = await Dialogs.Confirm(this, "删除该笔记本？",
                    "「" + name + "」下的 " + cnt + " 篇笔记会移动到「全部笔记」，笔记本身不会删除。", "删除笔记本");
                if (!yes) return;
                _session.Notes.MoveNotes(name, Notebooks.All);
                _session.Notebooks.Remove(name);
                if (_notebookFilter == name) _notebookFilter = null;
                Refresh();
            };
            menu.Items.Add(delItem);
        }
        button.ContextMenu = menu;

        if (_sidebarWidth < Metrics.SidebarExpanded)
            ToolTip.SetTip(button, name + " · " + count + " 篇笔记");
        return button;
    }

    private static Control QuickCard(string title, string subtitle, bool selected, Action click,
        int pending = 0, int overdue = 0, string? icon = null)
    {
        var text = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                Ui.Text(title, 13.5, FontWeight.SemiBold, Palette.TextPrimary),
                Ui.Text(subtitle, 11, null, Palette.TextTertiary),
            },
        };
        var badges = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        if (pending > 0) badges.Children.Add(Ui.Badge(pending, red: false));
        if (overdue > 0) badges.Children.Add(Ui.Badge(overdue));
        Control trailing = text;
        if (badges.Children.Count > 0)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            grid.Children.Add(text);
            Grid.SetColumn(badges, 1);
            grid.Children.Add(badges);
            trailing = grid;
        }
        // Mac .quick .qi：34px 圆形 + 主题色矢量图标（无底色，仅图标）
        Control content = trailing;
        if (!string.IsNullOrEmpty(icon))
        {
            var badge = new Border
            {
                Width = 34, Height = 34,
                CornerRadius = new CornerRadius(17),
                Background = Brushes.Transparent,
                VerticalAlignment = VerticalAlignment.Center,
                Child = Ui.Icon(icon, 18, Palette.Accent),
            };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            row.Children.Add(badge);
            Grid.SetColumn(trailing, 1);
            trailing.Margin = new Thickness(10, 0, 0, 0);
            row.Children.Add(trailing);
            content = row;
        }
        // 右侧尖括号（Mac .quick .chev）：加大并加深，确保无背景、清晰可见
        var chevron = Ui.Icon(Glyphs.ChevronRight, 11, Palette.TextSecondary);
        chevron.VerticalAlignment = VerticalAlignment.Center;
        chevron.Margin = new Thickness(4, 0, 0, 0);
        var chevRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        chevRow.Children.Add(content);
        Grid.SetColumn(chevron, 1);
        chevRow.Children.Add(chevron);
        content = chevRow;
        var card = new Border
        {
            Background = selected ? Palette.CardSelected : Palette.WindowBg,
            BorderBrush = selected ? Palette.CardSelectedBorder : Palette.CardHoverBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Metrics.RadiusCard),
            BoxShadow = selected ? Palette.ShadowXs : default,
            Padding = new Thickness(11),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = content,
        };
        if (!selected) card.WithHover(Palette.WindowBg, Palette.CardHover).WithElevate();
        card.PointerPressed += (_, e) => { click(); e.Handled = true; };
        return card;
    }
}
