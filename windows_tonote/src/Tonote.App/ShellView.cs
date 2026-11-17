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
    private readonly StackPanel _quick = new() { Spacing = 8, Margin = new Thickness(12, 12, 12, 0) };
    private readonly StackPanel _sortRow = new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 12,
        Margin = new Thickness(12, 8, 12, 4),
    };
    private readonly StackPanel _cards = new();
    private readonly ContentControl _right = new();
    private readonly TextBox _search = new()
    {
        Watermark = "搜索标题或正文",
        FontSize = 13,
    };
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
    private readonly TextBlock _sidebarSync = new()
    {
        FontSize = 11,
        Foreground = Palette.Accent,
        TextWrapping = TextWrapping.Wrap,
    };
    private readonly TextBlock _count = new() { Foreground = Palette.TextTertiary, FontSize = 11.5 };
    private readonly ComboBox _notebook = new() { MinWidth = 120, FontSize = 12 };
    private readonly StackPanel _tags = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly TextBox _tagInput = new() { Watermark = "添加标签", Width = 120, FontSize = 12 };
    private readonly StackPanel _metaRow = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly TextBlock _handleGlyph = new()
    {
        Text = ">", HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = Palette.TextTertiary, FontSize = 12,
    };
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
    private Control? _editorCached;

    public event Action? LogoutRequested;

    public ShellView(AppSession session)
    {
        _session = session;
        _pinButton = Ui.Small("置顶", TogglePin);
        _trashButton = Ui.Small("移入回收站", MoveCurrentToTrash);
        _restoreButton = Ui.Primary("恢复笔记", RestoreCurrent);
        _purgeButton = Ui.Danger("彻底删除", PurgeCurrent);
        _syncOneButton = Ui.Small("同步本篇", async () =>
        {
            if (_selected == null) return;
            await _session.Sync.SyncSingleNoteAsync(_selected.Value);
        });
        _handle = BuildHandle();
        _richEditor = new RichTextEditor(session) { Margin = new Thickness(0) };
        _richEditor.StatusChanged += message => OnUi(() => _banner.Text = message);
        _richEditor.DeleteRequested += MoveCurrentToTrash;
        _richEditor.ToolbarTrailing = _count;
        _search.TextChanged += (_, _) => RebuildCards();
        _title.TextChanged += (_, _) => SaveTitle();
        _notebook.SelectionChanged += (_, _) => SaveNotebook();
        _tagInput.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) AddTag();
        };

        // 搜索框套一层自绘外壳：灰底 8px 圆角 + 聚焦淡绿细环，
        // 避免 Avalonia Fluent 默认的蓝色 focus 边框。
        var searchField = Ui.Field(_search);
        searchField.Margin = new Thickness(12, 10, 12, 0);
        var cardsScroll = new ScrollViewer { Content = _cards };
        var list = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*"),
            Background = Palette.Panel,
            Children = { _quick, searchField, _sortRow, cardsScroll },
        };
        Grid.SetRow(searchField, 1);
        Grid.SetRow(_sortRow, 2);
        Grid.SetRow(cardsScroll, 3);

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions($"{Metrics.SidebarCompact},{Metrics.SidebarHandle},{Metrics.NoteListWidth},1,*"),
            Background = Palette.WindowBg,
            Children =
            {
                new ScrollViewer { Content = _sidebar, Background = Palette.SidebarBg },
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
            if (_pane == Pane.Todos) ShowTodos();
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
        _sidebarSync.Text = _sidebarWidth > Metrics.SidebarTiny ? shortText : "●";
        _sidebarSync.Foreground = status.Kind == SyncStatusKind.Failed ? Palette.Red : Palette.Accent;
        var note = Current();
        if (note?.IsQuickSync == true)
            _banner.Text = shortText + (string.IsNullOrWhiteSpace(status.Message) ? "" : " · " + status.Message);
    }

    private void Select(Guid id)
    {
        _richEditor.Flush();
        _selected = id;
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
        _handleGlyph.Text = _sidebarWidth == Metrics.SidebarExpanded ? "<" : ">";
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
        // Mac：只在「进入」任务页时插队拉一次，避免任务变更事件反过来触发无限同步轮次
        var entered = _pane != Pane.Todos;
        _pane = Pane.Todos;
        _selected = null;
        if (entered) _ = _session.Sync.QuickSyncAsync();

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(22, 18, 22, 12),
        };
        var headLeft = new StackPanel { Spacing = 4 };
        headLeft.Children.Add(Ui.Title("快任务", Metrics.FontHeader));
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
            Watermark = "添加一条快任务，回车即可创建",
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
        Refresh();
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
            if (!expanded && !readOnly && string.IsNullOrWhiteSpace(VisibleText.From(todo.Notes)))
                _editingNotesTodo = todo.Id;
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
                "「" + todo.Title + "」将被永久删除，并会从其它设备的任务列表里移除。", "删除");
            if (yes) _session.Todos.Remove(todo.Id);
        }, Palette.Red, 11));

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        head.Children.Add(ringButton);
        Grid.SetColumn(title, 1);
        title.Margin = new Thickness(9, 0, 0, 0);
        head.Children.Add(title);
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
            var notes = new TextBox
            {
                Text = VisibleText.From(todo.Notes),
                Watermark = "写点备注…",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 80,
                FontSize = 12.5,
                Padding = new Thickness(10),
                CornerRadius = new CornerRadius(6),
                BorderBrush = Palette.Line,
            };
            var noteText = notes;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(Ui.Primary("保存", () =>
            {
                var html = RebuildTodoNotes(todo.Notes, noteText.Text ?? "");
                _session.Todos.UpdateNotes(todo.Id, html);
                _editingNotesTodo = null;
                ShowTodos();
            }, 11));
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
        var date = value.Date.AddHours(12);
        _session.Todos.SetPlannedAt(id, DateTime.SpecifyKind(date, DateTimeKind.Local).ToUniversalTime());
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

        _sidebar.Children.Add(Ui.Title(_sidebarWidth <= Metrics.SidebarTiny ? "同" : "同笔记",
            _sidebarWidth <= Metrics.SidebarTiny ? 14 : 16));
        var avatar = new Border
        {
            Width = 32, Height = 32,
            CornerRadius = new CornerRadius(16),
            Background = Palette.Brush(_session.Avatar.Current.Color1),
            Margin = new Thickness(4, 4, 0, 6),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = Ui.Text(_session.Avatar.Current.Glyph, 14, FontWeight.Bold, Palette.White),
        };
        avatar.PointerPressed += (_, e) =>
        {
            _ = Dialogs.PickAvatar(this, _session);
            e.Handled = true;
        };
        ToolTip.SetTip(avatar, "点击更换头像 · " + _session.Avatar.Current.Glyph);
        _sidebar.Children.Add(avatar);
        _sidebarSync.Margin = new Thickness(4, 0, 0, 8);
        _sidebarSync.IsVisible = _sidebarWidth > Metrics.SidebarTiny;
        UpdateSyncLabels();
        _sidebar.Children.Add(_sidebarSync);

        _sidebar.Children.Add(AccentButton(_sidebarWidth == Metrics.SidebarExpanded ? "新建笔记" : "＋", NewNote));
        _sidebar.Children.Add(SideButton("设置", _pane == Pane.Settings, OpenSettings, glyph: Glyphs.Gear));

        if (_sidebarWidth > Metrics.SidebarTiny) _sidebar.Children.Add(Ui.Section("笔记本"));
        _sidebar.Children.Add(SideButton("全部笔记", _pane == Pane.Notes && _notebookFilter == null && _tagFilter == null,
            () => { _pane = Pane.Notes; _notebookFilter = null; _tagFilter = null; Refresh(); },
            _session.Notes.CountInNotebook(Notebooks.All)));

        foreach (var name in _session.Notebooks.List)
        {
            var notebook = name;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var button = SideButton(notebook, _pane == Pane.Notes && _notebookFilter == notebook && _tagFilter == null,
                () => { _pane = Pane.Notes; _notebookFilter = notebook; _tagFilter = null; Refresh(); },
                _session.Notes.CountInNotebook(notebook));
            row.Children.Add(button);
            if (_sidebarWidth == Metrics.SidebarExpanded && _session.Notebooks.Custom.Contains(notebook))
            {
                var remove = Ui.Small(Glyphs.Close, async () =>
                {
                    var count = _session.Notes.CountInNotebook(notebook);
                    var yes = await Dialogs.Confirm(this, "删除该笔记本？",
                        "「" + notebook + "」下的 " + count + " 篇笔记会移动到「全部笔记」，笔记本身不会删除。", "删除笔记本");
                    if (!yes) return;
                    _session.Notes.MoveNotes(notebook, Notebooks.All);
                    _session.Notebooks.Remove(notebook);
                    if (_notebookFilter == notebook) _notebookFilter = null;
                });
                Grid.SetColumn(remove, 1);
                row.Children.Add(remove);
            }
            _sidebar.Children.Add(row);
        }

        if (_sidebarWidth == Metrics.SidebarExpanded) _sidebar.Children.Add(NewNotebookBox());
        if (_sidebarWidth > Metrics.SidebarTiny) _sidebar.Children.Add(Ui.Section("标签"));
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
                var button = SideButton("#" + name, _tagFilter == name,
                    () => { _pane = Pane.Notes; _tagFilter = name; _notebookFilter = null; Refresh(); },
                    _session.Notes.CountWithTag(name));
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

        _sidebar.Children.Add(Ui.Divider());
        _sidebar.Children.Add(SideButton("回收站", _pane == Pane.Trash,
            () => { _pane = Pane.Trash; _notebookFilter = null; _tagFilter = null; Refresh(); },
            _session.Notes.TrashNotes.Count));
        _sidebar.Children.Add(SideButton("快任务", _pane == Pane.Todos, ShowTodos,
            _session.Todos.ActiveCount, _session.Todos.OverdueCount));
    }

    private Control NewNotebookBox()
    {
        var input = new TextBox { Watermark = "新笔记本（≤12 字）", Margin = new Thickness(0, 4, 0, 0), FontSize = 12 };
        var error = Ui.Text("", 11, null, Palette.Red);
        error.IsVisible = false;
        var button = Ui.Small("新建笔记本", () =>
        {
            var name = input.Text ?? "";
            var message = _session.Notebooks.Add(name);
            if (message == null)
            {
                input.Text = "";
                error.IsVisible = false;
                _pane = Pane.Notes;
                _notebookFilter = name.Trim();
                _tagFilter = null;
                Refresh();
            }
            else
            {
                error.Text = message;
                error.IsVisible = true;
            }
        });
        return new StackPanel { Spacing = 4, Children = { input, button, error } };
    }

    private void RebuildQuick()
    {
        _quick.Children.Clear();
        _quick.IsVisible = _pane == Pane.Notes;
        if (_pane != Pane.Notes) return;

        _quick.Children.Add(QuickCard("快同步", "打开这篇独立笔记", _selected == Note.QuickSyncNoteId,
            () => { _pane = Pane.Notes; Select(Note.QuickSyncNoteId); }, 0, 0));
        _quick.Children.Add(QuickCard("快任务",
            $"待完成 {_session.Todos.PendingCount} · 进行中 {_session.Todos.DoingCount}",
            _pane == Pane.Todos, ShowTodos, _session.Todos.ActiveCount, _session.Todos.OverdueCount));
    }

    private void RebuildSort()
    {
        _sortRow.Children.Clear();
        _sortRow.IsVisible = _pane is Pane.Notes or Pane.Trash;
        AddSort("最近更新", SortMode.Updated);
        AddSort("按创建", SortMode.Created);
        AddSort("按标题", SortMode.Title);
    }

    private void AddSort(string label, SortMode mode)
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
        };
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
                Margin = new Thickness(6, 2),
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
                var purge = new MenuItem { Header = "彻底删除" };
                purge.Click += async (_, _) =>
                {
                    var yes = await Dialogs.Confirm(this, "彻底删除笔记？",
                        "「" + current.Title + "」将被永久移除，并同步删除云端附件。", "彻底删除");
                    if (yes) { _session.Notes.PermanentlyDelete(current.Id); Refresh(); }
                };
                menu.Items.Add(purge);
            }
            else
            {
                var pin = new MenuItem { Header = current.Pinned ? "取消置顶" : "置顶" };
                pin.Click += (_, _) => _session.Notes.TogglePin(current.Id);
                menu.Items.Add(pin);
                var trash = new MenuItem { Header = "移入回收站" };
                trash.Click += async (_, _) =>
                {
                    var yes = await Dialogs.Confirm(this, "移入回收站？",
                        "「" + current.Title + "」将从列表移到回收站，可随时恢复。", "移入回收站", "取消", false);
                    if (yes) _session.Notes.MoveToTrash(current.Id);
                };
                menu.Items.Add(trash);
            }
            card.ContextMenu = menu;
            _cards.Children.Add(card);
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
        if (_pane != Pane.Trash && !string.IsNullOrWhiteSpace(_search.Text))
        {
            var query = _search.Text.Trim();
            source = source.Where(note =>
                note.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || note.VisibleBody.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        }
        return _sort switch
        {
            SortMode.Created => source.OrderByDescending(note => note.CreatedAt),
            SortMode.Title => source.OrderBy(note => note.Title, StringComparer.CurrentCulture),
            _ => source.OrderByDescending(note => note.UpdatedAt),
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
        var yes = await Dialogs.Confirm(this, "移入回收站？",
            "「" + note.Title + "」将从列表移到回收站，可随时恢复。", "移入回收站", "取消", false);
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
            "「" + note.Title + "」将被永久移除，并同步删除云端附件。", "彻底删除");
        if (!yes) return;
        _session.Notes.PermanentlyDelete(note.Id);
        _selected = null;
        _right.Content = Ui.EmptyState("笔记已移除。删除会以 purged 墓碑同步到其它设备。");
        Refresh();
    }

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
        var handle = new Border
        {
            Background = Palette.SidebarBg,
            Cursor = new Cursor(StandardCursorType.SizeWestEast),
            Child = new Border
            {
                Width = 16, Height = 18,
                Background = Palette.Panel,
                Child = _handleGlyph,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        handle.PointerEntered += (_, _) => handle.Background = Palette.Accent;
        handle.PointerExited += (_, _) => { if (!_dragging) handle.Background = Palette.SidebarBg; };
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
            handle.Background = Palette.SidebarBg;
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
        _handleGlyph.Text = width == Metrics.SidebarExpanded ? "<" : ">";
        RebuildSidebar();
    }

    // ── 通用控件 ───────────────────────────────────────────────────────
    private Button AccentButton(string text, Action click)
    {
        var tiny = _sidebarWidth <= Metrics.SidebarTiny;
        var plus = Ui.Icon(Glyphs.Plus, 16, Palette.White);
        Control content = tiny ? plus : new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { plus, Ui.Text(text, 13, FontWeight.SemiBold, Palette.White) },
        };
        var button = new Button
        {
            Content = content,
            Background = Palette.Accent,
            Foreground = Palette.White,
            FontWeight = FontWeight.SemiBold,
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            CornerRadius = new CornerRadius(tiny ? 17 : Metrics.RadiusCard),
            Width = tiny ? 34 : double.NaN,
            Height = 34,
            Margin = new Thickness(0, 4, 0, 8),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        button.Click += (_, _) => click();
        return button;
    }

    private Button SideButton(string text, bool selected, Action click, int? count = null,
        int? badge = null, string? glyph = null)
    {
        var tiny = _sidebarWidth <= Metrics.SidebarTiny;
        var label = tiny && glyph == null ? text[..1].ToString()
            : count == null ? text : $"{text}  {count}";
        var fg = selected ? Palette.AccentHover : Palette.TextSecondary;
        var icon = glyph == null ? null : Ui.Icon(glyph, tiny ? 18 : 15, fg);
        var button = new Button
        {
            Background = selected ? Palette.AccentTint : Brushes.Transparent,
            Foreground = fg,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = _sidebarWidth == Metrics.SidebarExpanded
                ? HorizontalAlignment.Left : HorizontalAlignment.Center,
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

        if (badge is > 0 && !tiny)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            grid.Children.Add(Ui.Text(label, 12, selected ? FontWeight.SemiBold : null, fg));
            var chip = Ui.Badge(badge.Value);
            chip.Margin = new Thickness(6, 0, 0, 0);
            Grid.SetColumn(chip, 1);
            grid.Children.Add(chip);
            button.Content = grid;
        }
        else if (icon != null && !tiny)
        {
            button.Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 9,
                Children = { icon, Ui.Text(label, 12, selected ? FontWeight.SemiBold : null, fg) },
            };
        }
        else if (icon != null) button.Content = icon;
        else button.Content = Ui.Text(label, button.FontSize, selected ? FontWeight.SemiBold : null, fg);

        if (!selected)
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

    private static Control QuickCard(string title, string subtitle, bool selected, Action click,
        int pending = 0, int overdue = 0)
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
        var badges = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Top };
        if (pending > 0) badges.Children.Add(Ui.Badge(pending, red: false));
        if (overdue > 0) badges.Children.Add(Ui.Badge(overdue));
        Control content = text;
        if (badges.Children.Count > 0)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            grid.Children.Add(text);
            Grid.SetColumn(badges, 1);
            grid.Children.Add(badges);
            content = grid;
        }
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
