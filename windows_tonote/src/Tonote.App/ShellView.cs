using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
    private readonly TextBlock _meta = new() { Foreground = Palette.TextTertiary, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    // 用固定尺寸的圆形 Border 代替字符「●」：字符基线会让圆点下沉，圆形 + Center 才能与文字中线严格对齐。
    private readonly Border _metaDot = new() { Width = 5, Height = 5, CornerRadius = new CornerRadius(2.5), Background = Palette.Accent, VerticalAlignment = VerticalAlignment.Center };
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
    // 笔记本按内容自适应宽度，用 MinWidth=0 覆盖 Fluent 主题自带的 120 默认最小宽度。
    private readonly ComboBox _notebook = new() { MinWidth = 0, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly StackPanel _tags = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    // 标签输入框整体收窄 10px（120 → 110），右侧对勾只在有内容时出现。
    // 高度与 ComboBox 对齐：Fluent TextBox 默认 MinHeight=32，不压掉会把标签框撑到 34px。
    private readonly TextBox _tagInput = new() { Watermark = "添加标签", FontSize = 12, Padding = new Thickness(8, 4, 8, 2), MinHeight = 0, Height = 24, VerticalAlignment = VerticalAlignment.Stretch };
    private readonly Button _tagSubmit;
    private readonly Button _tagClear;
    private readonly Border _tagFrame;
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
    /// <summary>当前正在编辑的备注输入框引用，供收起 / 切换任务时自动保存。</summary>
    private TextBox? _editingNotesBox;
    /// <summary>W-27：展开任务期间冻结列表顺序，避免状态 / 时间变化导致跳动。</summary>
    private readonly Dictionary<Guid, int> _frozenOrder = new();
    private Control? _editorCached;

    // 同步按钮转圈动画：点循环箭头即转，同步结束停下（后台其实一直在同步，之前缺的是可见反馈）。
    private DispatcherTimer? _syncSpinTimer;
    private RotateTransform? _syncSpinTransform;
    private bool _syncSpinning;

    public event Action? LogoutRequested;

    public ShellView(AppSession session)
    {
        _session = session;
        // 标题区三个操作按钮统一用图标，说明文字放 tooltip。
        // 置顶是实心填充、笔划面积大，比另两个图标显重，单独调浅一档到 TextTertiary。
        _pinButton = Ui.Small(Glyphs.Pin, TogglePin, Palette.TextTertiary);
        ToolTip.SetTip(_pinButton, "置顶");
        // 删除入口改用图标（从编辑器工具条移来），外观与其它标题区按钮一致。
        _trashButton = Ui.Small(Glyphs.Trash, MoveCurrentToTrash);
        ToolTip.SetTip(_trashButton, "删除笔记（移入回收站）");
        _restoreButton = Ui.Primary("恢复笔记", RestoreCurrent);
        _purgeButton = Ui.Danger("彻底删除", PurgeCurrent, icon: Glyphs.Trash);
        _syncOneButton = Ui.Small(Glyphs.Sync, () => _ = SyncOneWithSpin());
        ToolTip.SetTip(_syncOneButton, "同步本篇");
        // 标题区三个图标按钮去掉边框线，只保留图标与 hover 反馈。
        _syncOneButton.BorderThickness = new Thickness(0);
        _pinButton.BorderThickness = new Thickness(0);
        _trashButton.BorderThickness = new Thickness(0);
        _handle = BuildHandle();
        _richEditor = new RichTextEditor(session) { Margin = new Thickness(0) };
        _richEditor.StatusChanged += message => OnUi(() => _banner.Text = message);
        _richEditor.SyncRequested += () => _ = _session.Sync.QuickSyncAsync();
        _richEditor.WordCountChanged += count => OnUi(() => _count.Text = count + " 字");
        // 字数已移到标题区右上角按钮下方，不再作为工具条尾部控件。
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

        // 标签输入：有内容才显示细边框 + 绿色圆环对勾（提交）+ 灰色圆环叉号（清空）。
        _tagSubmit = TagIconButton(Glyphs.TagCheck, Palette.Brush(0x069F21), "添加标签", AddTag, top: 4); // 勾上边距 +1 → 4
        _tagClear = TagIconButton(Glyphs.TagClear, Palette.Brush(0xBFBFBF), "清空输入", ClearTagInput, size: 11, top: 0, left: 2, right: 2); // 叉宽高 11，左间距 +2，右边距 2
        _tagSubmit.IsVisible = false;
        _tagClear.IsVisible = false;
        var tagGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        tagGrid.Children.Add(_tagInput);
        Grid.SetColumn(_tagSubmit, 1);
        tagGrid.Children.Add(_tagSubmit);
        Grid.SetColumn(_tagClear, 2);
        tagGrid.Children.Add(_tagClear);
        _tagFrame = new Border
        {
            Width = 110,
            Height = 26, // 与笔记本 ComboBox 的 MinHeight=26 严格一致（空/有内容都一样）
            Child = tagGrid,
            BorderThickness = new Thickness(1),
            BorderBrush = Palette.Transparent,
            CornerRadius = new CornerRadius(7),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _tagInput.TextChanged += (_, _) => UpdateTagFrame();
        _tagInput.GotFocus += (_, _) => UpdateTagFrame();
        _tagInput.LostFocus += (_, _) => UpdateTagFrame();
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

    /// <summary>点击「同步本篇」：图标持续转圈给出可见反馈，同步结束后停下并复位。</summary>
    private async Task SyncOneWithSpin()
    {
        if (_selected == null) return;
        var id = _selected.Value;
        StartSyncSpin();
        var started = DateTime.UtcNow;
        try
        {
            await _session.Sync.SyncSingleNoteAsync(id);
        }
        finally
        {
            // 无变更时同步可能几毫秒就返回；至少转够最短时长，否则用户看不到任何反馈。
            var remaining = TimeSpan.FromMilliseconds(700) - (DateTime.UtcNow - started);
            if (remaining > TimeSpan.Zero)
                await Task.Delay(remaining);
            OnUi(StopSyncSpin);
        }
    }

    private void StartSyncSpin()
    {
        if (_syncSpinning) return;
        _syncSpinning = true;
        // 图标是按钮 Content 里的 Path；绕自身中心旋转。
        if (_syncOneButton.Content is Control icon)
        {
            icon.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
            _syncSpinTransform = new RotateTransform(0);
            icon.RenderTransform = _syncSpinTransform;
        }
        _syncSpinTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _syncSpinTimer.Tick -= OnSyncSpinTick;
        _syncSpinTimer.Tick += OnSyncSpinTick;
        _syncSpinTimer.Start();
    }

    private void OnSyncSpinTick(object? sender, EventArgs e)
    {
        if (_syncSpinTransform != null)
            _syncSpinTransform.Angle = (_syncSpinTransform.Angle + 18) % 360;
    }

    private void StopSyncSpin()
    {
        _syncSpinning = false;
        _syncSpinTimer?.Stop();
        if (_syncOneButton.Content is Control icon)
            icon.RenderTransform = null;
        _syncSpinTransform = null;
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
        // 同步完成后 LastSyncDate 会更新，编辑器状态文案的「上次同步」时间也要跟着刷新。
        if (ReferenceEquals(_right.Content, _editorCached)) UpdateMetaText();
        var note = Current();
        if (note?.IsQuickSync == true)
            _banner.Text = shortText + (string.IsNullOrWhiteSpace(status.Message) ? "" : " · " + status.Message);
    }

    /// <summary>编辑器状态文案：回收站/上次同步时间，灰色次要信息。</summary>
    private void UpdateMetaText()
    {
        var note = Current();
        if (note == null) return;
        var lastSync = _session.Sync.LastSyncDate;
        _meta.Text = note.IsTrashed ? "回收站中的笔记"
            : lastSync == null ? "尚未同步"
            : "上次同步 " + lastSync.Value.ToString("MM-dd HH:mm");
        _meta.Foreground = Palette.TextTertiary;
        // 状态圆点用主题绿，文字仍保持灰色次要信息。
        _metaDot.Background = Palette.Accent;
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
        _tagFrame.IsVisible = !note.IsQuickSync && !note.IsTrashed;
        _metaRow.IsVisible = !note.IsQuickSync;
        RenderTags(note);
        _loading = false;
        UpdateChrome();
    }

    private void UpdateChrome()
    {
        var note = Current();
        if (note == null || !ReferenceEquals(_right.Content, _editorCached)) return;
        // 状态文案：灰色次要信息。改为展示上次同步时间，未同步过则提示尚未同步。
        UpdateMetaText();
        _count.Text = note.WordCount + " 字";
        var editable = !note.IsTrashed && !note.IsQuickSync;
        _syncOneButton.IsVisible = !note.IsTrashed;
        // 图标按钮不能再设 Content（会把矢量图标换成文字），状态变化只更新 tooltip。
        ToolTip.SetTip(_syncOneButton, note.IsQuickSync ? "立即同步本篇" : "同步本篇");
        _pinButton.IsVisible = editable;
        // 置顶状态：图钉变黄（与列表卡片一致），未置顶为灰色；同步更新 Foreground，
        // 这样 hover 离开后 Ui.Small 会按当前 Foreground 恢复，不会把黄色冲掉。
        var pinBrush = note.Pinned ? Palette.Amber : Palette.TextTertiary;
        _pinButton.Foreground = pinBrush;
        if (_pinButton.Content is Control pinIcon) Icons.Tint(pinIcon, pinBrush);
        ToolTip.SetTip(_pinButton, note.Pinned ? "取消置顶" : "置顶");
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
        _metaRow.Children.Add(_tagFrame);

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(Metrics.EditorInset, 16, Metrics.EditorInset, 10),
        };
        header.Children.Add(_title);
        // 右上角：一行图标按钮。字数已移到 meta 行，与「已保存到本地」同排。
        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var buttons = new[] { _syncOneButton, _pinButton, _trashButton, _restoreButton, _purgeButton };
        foreach (var button in buttons)
        {
            button.Margin = new Thickness(6, 0, 0, 0);
            buttonRow.Children.Add(button);
        }
        Grid.SetColumn(buttonRow, 1);
        header.Children.Add(buttonRow);

        // 元数据行：左侧笔记本/标签 + 状态文案；字数右对齐到最右侧（垃圾桶下方），
        // 但仍在同一个 meta 行内，与状态文案保持同一水平线。
        var metaStack = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(Metrics.EditorInset, 0, Metrics.EditorInset, 10),
        };
        var metaLeft = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                _metaRow,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children = { _metaDot, _meta },
                },
            },
        };
        metaStack.Children.Add(metaLeft);
        _count.HorizontalAlignment = HorizontalAlignment.Right;
        _count.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_count, 1);
        metaStack.Children.Add(_count);

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
            var groupHeader = GroupHeader(status, items.Count);
            // 分组与分组之间多留 6px（第一组不需要）
            // 展开时标题与第一条任务的间距缩小 4px（折叠的分组不动，免得挤到下一组标题）
            groupHeader.Margin = new Thickness(0, groups.Children.Count > 0 ? 6 : 0, 0,
                _collapsedGroups.Contains(status) ? 0 : -5);
            groups.Children.Add(groupHeader);
            if (_collapsedGroups.Contains(status)) continue;
            if (items.Count == 0)
            {
                var emptyHint = Ui.Text("暂无任务", 12, null, Palette.TextQuaternary);
                emptyHint.Margin = new Thickness(16, 0, 0, 0); // 圆点 8 + 间距 8，与分类文字左对齐
                groups.Children.Add(emptyHint);
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
            Padding = new Thickness(14, 8), // 上下各 -1，整体高度 -2
            CornerRadius = new CornerRadius(16),
            // 白色填充 + 浅灰边框，与页面灰底区分开
            Background = Brushes.White,
            BorderBrush = Palette.Line,
            BorderThickness = new Thickness(1),
        };
        input.Classes.Add("quick-add");
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
            Children = { input },
        };
        // 圆形「+」添加按钮：与输入框间距 16
        var addButton = new Button
        {
            Content = Icons.Render(Glyphs.Plus, 16, Palette.White),
            Width = 33, Height = 33, MinWidth = 0, MinHeight = 0,
            Margin = new Thickness(16, 0, 0, 0),
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(16.5),
            Background = Palette.Accent,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(addButton, "添加任务");
        addButton.Click += (_, _) => AddQuick();
        foot.Children.Add(addButton);
        Grid.SetColumn(addButton, 1);

        var page = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Background = Palette.TodoPanelBg,
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
        // 标题行：圆点 + 文字 + 数字胶囊 + 横线（占满整行剩余宽度），箭头叠在横线中间。
        // 必须用 Grid 的 * 列才能让横线铺满——横向 StackPanel 里的 Stretch 只按内容测量。
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        // 分组标识：8px 实心圆 + 外圈浅色光圈（Grid 仍占 8px，光圈向外溢出不推挤文字）
        var dotHost = new Grid
        {
            Width = 8, Height = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        dotHost.Children.Add(new Border
        {
            Width = 16, Height = 16,
            CornerRadius = new CornerRadius(8),
            Background = Palette.Alpha(ColorOf(status), 48),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        dotHost.Children.Add(new Border
        {
            Width = 8, Height = 8,
            CornerRadius = new CornerRadius(4),
            Background = Ui.GroupColor(status),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(dotHost);
        var label = Ui.Text(status.Label(), 13, FontWeight.SemiBold);
        label.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        var pill = Ui.Pill(count.ToString(), Palette.Alpha(ColorOf(status), 26), Ui.GroupColor(status), 11, 9);
        pill.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(pill, 2);
        row.Children.Add(pill);

        var lineHost = new Grid { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        lineHost.Children.Add(new Border
        {
            Height = 0.7,
            Background = Palette.Alpha(ColorOf(status), 110),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var collapsed = _collapsedGroups.Contains(status);
        var chevron = Ui.Icon(collapsed ? Glyphs.ChevronRight : Glyphs.ChevronDown,
            6.5, Palette.TextTertiary);
        // 缩小到原尺寸一半后同步调细描边，避免小箭头显得粗重
        if (chevron is Avalonia.Controls.Shapes.Path chevronPath) chevronPath.StrokeThickness = 1;
        // 白底盖住线条，使箭头真正叠在横线正中（水平、垂直都居中）
        var chevronBadge = new Border
        {
            Background = Brushes.White,
            Padding = new Thickness(3, 2),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = chevron,
        };
        // 朝下的 V 形视觉重心偏上，整体下移 1px；右箭头保持原位
        // 右箭头的尖端在右、视觉重心偏左，实测比白底中心偏左 1px，右移 1px 补正
        chevronBadge.RenderTransform = collapsed ? new TranslateTransform(1, 0) : new TranslateTransform(0, 1);
        lineHost.Children.Add(chevronBadge);
        Grid.SetColumn(lineHost, 3);
        row.Children.Add(lineHost);

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
            // 状态圈贴在条目上边，不垂直居中
            VerticalAlignment = VerticalAlignment.Top,
        };
        ToolTip.SetTip(ringButton, readOnly ? "恢复为待完成" : "标记完成");
        ringButton.Click += (_, _) => _session.Todos.SetStatus(todo.Id,
            readOnly ? TodoStatus.todo : TodoStatus.done);

        var title = Ui.Text(todo.Title, 13, FontWeight.Medium,
            todo.IsOverdue ? Palette.Red : Palette.TextPrimary);
        title.TextWrapping = TextWrapping.Wrap;
        title.MaxLines = 2;
        title.TextTrimming = TextTrimming.CharacterEllipsis;

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        // 右侧按钮宽高写死，避免不同状态/字数导致行高跳动。
        static Button ButtonSized(Button button)
        {
            button.Width = 36;
            button.Height = 24;
            button.MinHeight = 0;
            button.Padding = new Thickness(0);
            return button;
        }
        // 主题色小按钮：浅色调背景 + 同色系文字/边框。Ui.Small 自带 hover→灰底/exit→透明，
        // 这里在后面追加 handler 覆盖成主题色（Avalonia 事件按顺序触发，后注册生效）。
        static Button TintedSmall(string text, Action click, IBrush fg, IBrush bg, IBrush border, IBrush hoverBg)
        {
            var b = Ui.Small(text, click, fg, 11);
            b.Background = bg;
            b.BorderBrush = border;
            b.PointerEntered += (_, _) => b.Background = hoverBg;
            b.PointerExited += (_, _) => b.Background = bg;
            return b;
        }
        if (!readOnly)
        {
            if (todo.Status != TodoStatus.doing)
                actions.Children.Add(ButtonSized(TintedSmall("开始", () => _session.Todos.SetStatus(todo.Id, TodoStatus.doing),
                    Palette.Accent, Palette.AccentTint, Palette.AccentTint2, Palette.Brush(0xD5EBDD))));
            // 必须先「开始」进入进行中，才显示「完成」——待完成状态不能直接完成
            if (todo.Status == TodoStatus.doing)
                actions.Children.Add(ButtonSized(TintedSmall("完成", () => _session.Todos.SetStatus(todo.Id, TodoStatus.done),
                    Palette.SyncRunText, Palette.SyncRunBg, Palette.Brush(0xEBD3A0), Palette.Brush(0xFBE7BE))));
            if (todo.Status != TodoStatus.terminated)
                actions.Children.Add(ButtonSized(Ui.Small("终止", () => _session.Todos.SetStatus(todo.Id, TodoStatus.terminated), null, 11)));
        }
        else
        {
            actions.Children.Add(ButtonSized(Ui.Small("恢复", () => _session.Todos.SetStatus(todo.Id, TodoStatus.todo), null, 11)));
        }
        // 删除：纯图标按钮，无边框、无 hover 背景；指向时图标由红变灰，仍是垃圾桶样式
        var trashIcon = Icons.Render(Glyphs.Trash, 14, Palette.Red);
        var deleteButton = ButtonSized(new Button
        {
            Content = trashIcon,
            Cursor = new Cursor(StandardCursorType.Hand),
        });
        deleteButton.Classes.Add("plain-icon");
        deleteButton.PointerEntered += (_, _) => Icons.Tint(trashIcon, Palette.TextTertiary);
        deleteButton.PointerExited += (_, _) => Icons.Tint(trashIcon, Palette.Red);
        deleteButton.Click += async (_, _) =>
        {
            var yes = await Dialogs.Confirm(this, "删除任务？",
                "「" + todo.Title + "」将被永久删除，其他已同步设备上也会一并移除，此操作不可恢复。", "永久删除");
            if (yes) _session.Todos.Remove(todo.Id);
        };
        actions.Children.Add(deleteButton);

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
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
        // 完成日期胶囊放到「最近修改」前面，水平间距 6px
        var metaLine = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (todo.PlannedAt != null)
        {
            var local = todo.PlannedAt.Value.ToLocalTime();
            metaLine.Children.Add(Ui.Pill(local.ToString("M月d日"),
                todo.IsOverdue ? Palette.Panel : Palette.Pill,
                todo.IsOverdue ? Palette.Red : Palette.TextTertiary, 11, 9));
        }
        // 「最近修改」只保留在备注面板里，任务行不再显示
        if (metaLine.Children.Count > 0) titleStack.Children.Add(metaLine);
        Grid.SetColumn(titleStack, 1);
        head.Children.Add(titleStack);
        Grid.SetColumn(actions, 2);
        head.Children.Add(actions);

        var frame = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(9, 8, 8, 8),
            // 灰底面板上的悬浮卡片：普通任务白底无边框；逾期任务淡红底 + 浅一档红框。
            Background = todo.IsOverdue ? Palette.RedTintSoft : Palette.White,
            BorderBrush = todo.IsOverdue ? Palette.Alpha(0xE5484D, 120) : Brushes.Transparent,
            BorderThickness = new Thickness(todo.IsOverdue ? 0.7 : 0),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = head,
        };
        frame.PointerPressed += (_, e) =>
        {
            // 点在状态圈 / 操作按钮上时，交给按钮自己处理，不切换面板。
            if (e.Source is Visual src && src != frame && IsInsideButton(src, frame)) return;
            // 收起（或切换到别的任务）前，把正在编辑的备注自动保存。
            AutoSaveEditingNotes();
            if (expanded) { _expandedTodo = null; _frozenOrder.Clear(); }
            else
            {
                _expandedTodo = todo.Id;
                var index = 0;
                foreach (var state in new[] { TodoStatus.todo, TodoStatus.doing, TodoStatus.done, TodoStatus.terminated })
                foreach (var item in _session.Todos.InStatus(state)) _frozenOrder[item.Id] = index++;
                if (!readOnly && string.IsNullOrWhiteSpace(VisibleText.From(todo.Notes)))
                    _editingNotesTodo = todo.Id;
            }
            ShowTodos();
            e.Handled = true;
        };

        // 整条任务对齐分组标题光圈右缘：光圈 16px 居中于 8px dotHost，右缘溢出 12px，卡片缩进同量
        var stack = new StackPanel { Spacing = 6, Margin = new Thickness(12, 0, 0, 0), Children = { frame } };
        if (expanded) stack.Children.Add(TodoDetail(todo));
        return stack;
    }

    /// <summary>从点击源向上找，判断是否点在 Button 内（Button 自身含图标/文字子进程）。</summary>
    private static bool IsInsideButton(Visual source, Visual root)
    {
        Visual? v = source;
        while (v != null && v != root)
        {
            if (v is Button) return true;
            v = v.GetVisualParent();
        }
        return false;
    }

    /// <summary>切换任务 / 收起面板前自动保存正在编辑的备注；内容未变则不写盘。</summary>
    private void AutoSaveEditingNotes()
    {
        var box = _editingNotesBox;
        if (box == null) return;
        var id = _editingNotesTodo;
        _editingNotesBox = null;
        _editingNotesTodo = null;
        if (id == null) return;
        var todo = _session.Todos.Find(id.Value);
        if (todo == null) return;
        var originalPlain = NotesPlain(todo.Notes);
        var edited = box.Text ?? "";
        if (edited.Trim() != originalPlain.Trim())
            _session.Todos.UpdateNotes(id.Value, RebuildTodoNotes(todo.Notes, edited));
    }

    private Control TodoDetail(Todo todo)
    {
        var readOnly = todo.IsTerminal;
        var editing = !readOnly && _editingNotesTodo == todo.Id;
        var body = new StackPanel { Spacing = 8 };
        var date = todo.PlannedAt?.ToLocalTime() ?? DateTime.Today;

        // 标题行：左侧「备注 + 最近修改」；仅编辑态右上角放「插图(图片上传)」
        var topRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var titleGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        titleGroup.Children.Add(Ui.Text("备注", 11.5, FontWeight.SemiBold, Palette.TextSecondary));
        titleGroup.Children.Add(Ui.Text("最近修改 " + todo.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            11, null, Palette.TextQuaternary));
        if (readOnly) titleGroup.Children.Add(Ui.Pill("只读", Palette.Pill, Palette.TextTertiary, 10));
        topRow.Children.Add(titleGroup);

        var topActions = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Grid.SetColumn(topActions, 2);
        topRow.Children.Add(topActions);
        body.Children.Add(topRow);

        if (editing)
        {
            var originalPlain = NotesPlain(todo.Notes);
            var notes = new TextBox
            {
                Text = originalPlain,
                Watermark = "写点备注…",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 60, // 初始高度减半，行数增多时 TextBox 自动撑高
                FontSize = 12.5,
                Padding = new Thickness(10),
                CornerRadius = new CornerRadius(6),
                Background = Palette.White,
                BorderBrush = Palette.Line,
            };
            notes.Classes.Add("notes-input");
            // Ctrl+V / Shift+Insert：剪贴板里有截图/位图就作为图片附件加入，否则按普通文本粘贴
            notes.AddHandler(InputElement.KeyDownEvent, (_, e) =>
            {
                var paste = (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control))
                            || (e.Key == Key.Insert && e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                if (!paste) return;
                e.Handled = true;
                PasteIntoNotes(todo.Id, notes);
            }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            var noteText = notes;
            _editingNotesBox = notes;
            void SaveNotes(bool collapse)
            {
                var edited = noteText.Text ?? "";
                // W-27：内容没变就不写盘，避免展开 / 失焦把 updatedAt 推到今天
                if (edited.Trim() != originalPlain.Trim())
                    _session.Todos.UpdateNotes(todo.Id, RebuildTodoNotes(todo.Notes, edited));
                _editingNotesTodo = null;
                _editingNotesBox = null;
                if (collapse) { _expandedTodo = null; _frozenOrder.Clear(); }
                ShowTodos();
            }
            // 底部操作行：保存 / 取消 / 截止日期组件（勾选后才显示年月日与日历）
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var saveBtn = Ui.Primary("保存", () => SaveNotes(true), 11); // 保存后自动收起备注面板
            saveBtn.Width = 48; // 与「编辑」按钮同宽高
            saveBtn.Height = 25;
            saveBtn.Padding = new Thickness(0);
            row.Children.Add(saveBtn);
            var cancelBtn = Ui.Ghost("取消", () =>
            {
                _editingNotesTodo = null;
                _editingNotesBox = null;
                ShowTodos();
            }, 11);
            // 整个背景、边框都去掉（含 hover/pressed，用 plain-icon 压掉透明度反馈）
            cancelBtn.Background = Brushes.Transparent;
            cancelBtn.BorderBrush = Brushes.Transparent;
            cancelBtn.BorderThickness = new Thickness(0);
            cancelBtn.Classes.Add("plain-icon");
            row.Children.Add(cancelBtn);

            AddDateControls(row);

            body.Children.Add(notes);
            body.Children.Add(BuildImageStrip(todo, () => noteText.Text ?? ""));
            body.Children.Add(row);
        }
        else
        {
            var plain = NotesPlain(todo.Notes);
            var viewBox = new StackPanel { MinHeight = 60, Spacing = 2 };
            if (!string.IsNullOrWhiteSpace(plain))
                viewBox.Children.Add(new TextBlock { Text = plain, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Foreground = Palette.TextSecondary });
            else
                viewBox.Children.Add(Ui.Text(readOnly ? "（无备注）" : "还没有备注", 12, null, Palette.TextQuaternary));
            body.Children.Add(viewBox);
            // 图片不混在备注文字里，统一放在下方图片区（只读缩略图，点击放大查看）
            if (NoteImageRefs(todo.Notes).Count > 0)
                body.Children.Add(BuildImageStrip(todo, () => "", false));
            if (!readOnly)
            {
                var viewRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                var editBtn = Ui.Primary("编辑", () => { _editingNotesTodo = todo.Id; ShowTodos(); }, 11);
                editBtn.Width = 48; // 与「保存」按钮同宽高
                editBtn.Height = 25;
                editBtn.Padding = new Thickness(0);
                editBtn.Margin = new Thickness(0, 0, 8, 0); // 与右侧勾选框间距 +8
                viewRow.Children.Add(editBtn);
                viewRow.Spacing = 8;
                viewRow.VerticalAlignment = VerticalAlignment.Center;
                AddDateControls(viewRow);
                body.Children.Add(viewRow);
            }
        }

        // 日期控件辅助：勾选框未选中时只显示「设置截止时间」，选中后才显示年月日框与日历按钮。
        // 编辑态由调用方合并进底部按钮行；查看/只读态单独一行。

        DateTime SafeDate(int y, int m, int d)
        {
            y = Math.Clamp(y, 2000, 2100);
            m = Math.Clamp(m, 1, 12);
            d = Math.Clamp(d, 1, DateTime.DaysInMonth(y, m));
            return new DateTime(y, m, d, 12, 0, 0, DateTimeKind.Local);
        }

        Border DateField(string value, double width, Action<int> commit)
        {
            var box = new TextBox
            {
                Text = value, Width = width,
                // 外框只有 9 高，但输入框本体要有足够高度（≥ 一行字高）文字才不会被模板裁掉；
                // 本体垂直居中、溢出部分由外层裁剪，数字落在可见的 9px 带内。
                MinWidth = 0, MinHeight = 0, Height = 17,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                FontSize = 10, Padding = new Thickness(3, 2, 3, 0), // 顶部 +2：文字下移 1px 居中
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
            };
            box.LostFocus += (_, _) =>
            {
                if (int.TryParse(box.Text?.Trim(), out var n)) commit(n);
            };
            return new Border
            {
                Background = Palette.White,
                BorderBrush = Palette.Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(0),
                Height = 17,
                Child = box,
            };
        }

        // 步进分段：[−][数值][+] 同一行，圆角边框，竖线分隔；step(±1) 返回新日期（月/日会自动进退位）
        Border DateStepper(string value, double width, Action<int> commit, Func<int, DateTime> step)
        {
            Button StepBtn(string sign, int delta)
            {
                var label = Ui.Text(sign, 10, null, Palette.Accent);
                label.HorizontalAlignment = HorizontalAlignment.Center;
                label.VerticalAlignment = VerticalAlignment.Center;
                var b = new Button
                {
                    Content = label,
                    Width = 12, Height = 17, MinWidth = 0, MinHeight = 0,
                    FontSize = 10,
                    Padding = new Thickness(0),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    // 外框圆角 6 − 边线 1 = 5：灰底只在外侧两角带圆弧，贴合外框
                    CornerRadius = delta < 0 ? new CornerRadius(5, 0, 0, 5) : new CornerRadius(0, 5, 5, 0),
                    Cursor = new Cursor(StandardCursorType.Hand),
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center,
                };
                // 指向 / 按下：整块按钮浅灰底（plain-icon 去掉默认透明度变化）
                b.Classes.Add("plain-icon");
                var hoverBg = Palette.Brush(0xEEEFF2);
                var pressBg = Palette.Brush(0xDEE0E5);
                b.PointerEntered += (_, _) => b.Background = hoverBg;
                b.PointerExited += (_, _) => b.Background = Brushes.Transparent;
                b.AddHandler(InputElement.PointerPressedEvent, (_, _) => b.Background = pressBg,
                    Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble, true);
                b.AddHandler(InputElement.PointerReleasedEvent, (_, _) => b.Background = b.IsPointerOver ? hoverBg : Brushes.Transparent,
                    Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble, true);
                b.Click += (_, _) => ChangeDate(todo.Id, step(delta));
                return b;
            }
            Border Sep() => new Border { Width = 1, Background = Palette.Line };

            var field = DateField(value, width, commit);
            field.BorderThickness = new Thickness(0);
            field.CornerRadius = new CornerRadius(0);

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(StepBtn("−", -1));
            row.Children.Add(Sep());
            row.Children.Add(field);
            row.Children.Add(Sep());
            row.Children.Add(StepBtn("+", 1));
            return new Border
            {
                Background = Palette.White,
                BorderBrush = Palette.Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                ClipToBounds = true,
                // 否则会被所在行（含 25 高的按钮）纵向拉伸，比日历图标高
                VerticalAlignment = VerticalAlignment.Center,
                Child = row,
            };
        }

        void AddDateControls(StackPanel host)
        {
            // 自绘勾选框：方框 12×12（原 16，缩小 4），整体高度 16（原约 20）
            var hasDeadline = todo.PlannedAt != null;
            var tickBox = new Border
            {
                Width = 12, Height = 12,
                CornerRadius = new CornerRadius(2),
                BorderBrush = Palette.Accent,
                BorderThickness = new Thickness(1),
                Background = hasDeadline ? Palette.Accent : Palette.White,
                VerticalAlignment = VerticalAlignment.Center,
                Child = hasDeadline ? Icons.Render(Glyphs.Check, 9, Palette.White) : null,
            };
            var checkContent = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 5,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    tickBox,
                    Ui.Text("设置截止时间", 10.5, null, Palette.Link),
                },
            };
            var check = new Button
            {
                Content = checkContent,
                Height = 16,
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            check.Classes.Add("plain-icon");
            check.Click += (_, _) =>
            {
                if (!hasDeadline) ChangeDate(todo.Id, DateTime.Today);
                else _session.Todos.SetPlannedAt(todo.Id, null);
            };
            host.Children.Add(check);
            // 勾选后才显示后面的年月日输入框与日历按钮
            if (todo.PlannedAt != null)
            {
                // 年月日输入框：宽 -8、高 -4；框与前后间距 +2
                host.Children.Add(DateStepper(date.Year.ToString(), 34, v => ChangeDate(todo.Id, SafeDate(v, date.Month, date.Day)), d => date.AddYears(d)));
                host.Children.Add(DateStepper(date.Month.ToString(), 24, v => ChangeDate(todo.Id, SafeDate(date.Year, v, date.Day)), d => date.AddMonths(d)));
                host.Children.Add(DateStepper(date.Day.ToString(), 24, v => ChangeDate(todo.Id, SafeDate(date.Year, date.Month, v)), d => date.AddDays(d)));
                var cal = new Button
                {
                    Content = Icons.Render(Glyphs.CalendarFilled, 16, Palette.Accent),
                    Width = 16, Height = 16, MinWidth = 0, MinHeight = 0,
                    Padding = new Thickness(0),
                    Background = Brushes.Transparent,
                    BorderBrush = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(0),
                    Cursor = new Cursor(StandardCursorType.Hand),
                    VerticalContentAlignment = VerticalAlignment.Center,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                };
                cal.Classes.Add("plain-icon");
                cal.Click += async (_, _) =>
                {
                    var picked = await Dialogs.PickDate(this, todo.PlannedAt?.ToLocalTime());
                    if (picked == DateTime.MinValue) _session.Todos.SetPlannedAt(todo.Id, null);
                    else if (picked != null) ChangeDate(todo.Id, picked.Value);
                };
                host.Children.Add(cal);
            }
        }

        // 截止时间设置控件只在编辑态显示（已并入底部按钮行）；
        // 查看/只读态不显示——任务卡片头部的日期胶囊已展示截止时间。

        var info = new List<string>();
        if (todo.StartedAt != null) info.Add("开始 " + todo.StartedAt.Value.ToLocalTime().ToString("MM-dd HH:mm"));
        if (todo.CompletedAt != null) info.Add("完成 " + todo.CompletedAt.Value.ToLocalTime().ToString("MM-dd HH:mm"));
        if (todo.Status == TodoStatus.terminated) info.Add("已终止");
        if (info.Count > 0)
            body.Children.Add(Ui.Text(string.Join(" · ", info), 11, null, Palette.TextQuaternary));

        var panel = new Border
        {
            Background = Palette.PanelSoft,
            BorderBrush = Palette.Alpha(0xCFE9D9, 166),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12),
            Child = body,
        };
        // 吊绳：任务卡片下方两根细线垂直连到备注面板顶部，像把面板「吊」在任务下面
        // 链条图标原本是斜 45° 的两节链环，旋转 45° 后变成竖直下垂的链
        var strings = new Grid { Height = 24 };
        foreach (var side in new[] { HorizontalAlignment.Left, HorizontalAlignment.Right })
        {
            var chain = Icons.Render(Glyphs.Chain, 20, Palette.Brush(0x9FD6B8));
            chain.HorizontalAlignment = side;
            chain.VerticalAlignment = VerticalAlignment.Center;
            chain.Margin = side == HorizontalAlignment.Left ? new Thickness(120, 0, 0, 0) : new Thickness(0, 0, 120, 0);
            chain.RenderTransform = new RotateTransform(47); // 按截图实测微调：49° 时底端偏左约 2°
            strings.Children.Add(chain);
        }
        // 上边距 -6：抵消外层 StackPanel 在任务卡片与详情之间的 6px 间距，让链条真正接到卡片底边
        var wrapper = new StackPanel { Margin = new Thickness(0, -6, 0, 10) };
        wrapper.Children.Add(strings);
        wrapper.Children.Add(panel);
        return wrapper;
    }

    private void ChangeDate(Guid id, DateTime value)
    {
        // W-27：年步进钳制在 2000–2100
        var year = Math.Clamp(value.Year, 2000, 2100);
        var date = new DateTime(year, value.Month, value.Day, 12, 0, 0, DateTimeKind.Local);
        _session.Todos.SetPlannedAt(id, date.ToUniversalTime());
    }

    /// <summary>备注转纯文字，段落/换行标签还原成换行（VisibleText 直接去标签会把多行并成一行）。</summary>
    private static string NotesPlain(string? html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var text = Regex.Replace(html, @"</p>\s*<p[^>]*>|<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        return VisibleText.From(text);
    }

    private static List<string> NoteImageRefs(string? notes) =>
        string.IsNullOrEmpty(notes)
            ? []
            : Regex.Matches(notes, "<img[^>]+src=\"(attachments/[^\"]+)\"[^>]*>").Select(m => m.Groups[1].Value).ToList();

    /// <summary>编辑态图片区：横向多张缩略图（右上角叉删除）+ 末尾「＋」添加框。</summary>
    private void ShowImageViewer(string path)
    {
        Bitmap bmp;
        try { bmp = new Bitmap(path); } catch { return; }
        var window = new Window
        {
            Title = Path.GetFileName(path),
            Width = Math.Clamp(bmp.Size.Width + 40, 320, 1100),
            Height = Math.Clamp(bmp.Size.Height + 60, 240, 800),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brushes.Black,
            Content = new Image { Source = bmp, Stretch = Stretch.Uniform, Margin = new Thickness(10) },
        };
        window.KeyDown += (_, e) => { if (e.Key == Key.Escape) window.Close(); };
        window.PointerPressed += (_, _) => window.Close();
        if (TopLevel.GetTopLevel(this) is Window owner) window.Show(owner); else window.Show();
    }

    private Control BuildImageStrip(Todo todo, Func<string> currentText, bool editable = true)
    {
        const double Size = 56;
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        foreach (var rel in NoteImageRefs(todo.Notes))
        {
            var path = _session.Todos.Attachments.AbsolutePath(rel);
            if (!File.Exists(path)) continue;
            Bitmap bmp;
            try { bmp = new Bitmap(path); } catch { continue; }
            var cell = new Grid { Width = Size + 6, Height = Size + 6 };
            var thumb = new Border
            {
                Width = Size, Height = Size,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                CornerRadius = new CornerRadius(6),
                BorderBrush = Palette.Line, BorderThickness = new Thickness(1),
                ClipToBounds = true,
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = new Image { Source = bmp, Stretch = Stretch.UniformToFill },
            };
            ToolTip.SetTip(thumb, "点击查看大图");
            thumb.PointerPressed += (_, e) => { e.Handled = true; ShowImageViewer(path); };
            cell.Children.Add(thumb);
            // 图片名称：位于缩略图正下方，最大宽度 = 缩略图宽度，超出省略
            var nameLabel = new TextBlock
            {
                Text = Path.GetFileName(path),
                FontSize = 9.5,
                Foreground = Palette.TextTertiary,
                MaxWidth = Size,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 2, 0, 0),
            };
            ToolTip.SetTip(nameLabel, Path.GetFileName(path));
            var column = new StackPanel { Spacing = 0 };
            column.Children.Add(cell);
            column.Children.Add(nameLabel);
            if (!editable) { strip.Children.Add(column); continue; }
            var x = new Button
            {
                Content = Ui.Text("✕", 8, FontWeight.Bold, Palette.White),
                Width = 16, Height = 16, MinWidth = 0, MinHeight = 0,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(8),
                Background = Palette.TextTertiary,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            x.Classes.Add("plain-icon");
            var captured = rel;
            x.Click += (_, _) => CommitNoteImages(todo.Id, currentText(), imgs => imgs.Where(i => i != captured).ToList());
            cell.Children.Add(x);
            strip.Children.Add(column);
        }

        var plus = new Border
        {
            Width = Size, Height = Size,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 0, 0), // 与缩略图（顶部留 6 给 ✕）对齐
            CornerRadius = new CornerRadius(6),
            BorderBrush = Palette.Line, BorderThickness = new Thickness(1),
            Background = Palette.White,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = Ui.Text("+", 22, null, Palette.TextQuaternary),
        };
        if (plus.Child is Control pc)
        {
            pc.HorizontalAlignment = HorizontalAlignment.Center;
            pc.VerticalAlignment = VerticalAlignment.Center;
        }
        ToolTip.SetTip(plus, "添加图片");
        plus.PointerPressed += (_, e) => { e.Handled = true; AddTodoImages(todo.Id, currentText()); };
        if (editable) strip.Children.Add(plus);

        var holder = new StackPanel { Spacing = 4 };
        holder.Children.Add(Ui.Text("附件图片", 11.5, FontWeight.SemiBold, Palette.TextSecondary));
        holder.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = strip,
        });
        return holder;
    }

    /// <summary>先把当前输入框文字存下，再按 mutate 改图片列表，避免刷新时丢失未保存文字。</summary>
    private void CommitNoteImages(Guid id, string text, Func<List<string>, List<string>> mutate)
    {
        var todo = _session.Todos.Find(id);
        if (todo == null) return;
        var images = mutate(NoteImageRefs(todo.Notes));
        _session.Todos.UpdateNotes(id, RebuildTodoNotes(todo.Notes, text, images));
        ShowTodos();
    }

    private async void PasteIntoNotes(Guid id, TextBox box)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
            {
                var formats = await clipboard.GetFormatsAsync();
                byte[]? bytes = null;
                var ext = "png";
                async Task<byte[]?> Read(string name)
                {
                    var data = await clipboard.GetDataAsync(name);
                    if (data is byte[] raw) return raw;
                    if (data is Stream stream)
                    {
                        using var copy = new MemoryStream();
                        await stream.CopyToAsync(copy);
                        return copy.ToArray();
                    }
                    if (data is Bitmap bmpData)
                    {
                        using var copy = new MemoryStream();
                        bmpData.Save(copy);
                        return copy.ToArray();
                    }
                    return null;
                }
                string? Pick(params string[] keys) =>
                    formats.FirstOrDefault(f => keys.Any(k => f.Contains(k, StringComparison.OrdinalIgnoreCase)));
                var pngName = Pick("png");
                var dibName = Pick("DeviceIndependentBitmap", "CF_DIB", "dib");
                byte[]? dib = null;
                if (pngName != null) bytes = await Read(pngName);
                if (bytes == null && dibName != null) dib = await Read(dibName);
                if (bytes == null && dib != null && dib.Length > 40)
                {
                    // DIB 没有文件头：补 14 字节 BMP 头后即可解码，再统一转存为 PNG
                    var offset = 14 + BitConverter.ToInt32(dib, 0);
                    var bpp = BitConverter.ToUInt16(dib, 14);
                    var colors = BitConverter.ToInt32(dib, 32);
                    if (bpp <= 8) offset += (colors == 0 ? 1 << bpp : colors) * 4;
                    var bmp = new byte[14 + dib.Length];
                    bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
                    BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2);
                    BitConverter.GetBytes(offset).CopyTo(bmp, 10);
                    dib.CopyTo(bmp, 14);
                    using var src = new MemoryStream(bmp);
                    using var decoded = new Bitmap(src);
                    using var pngStream = new MemoryStream();
                    decoded.Save(pngStream);
                    bytes = pngStream.ToArray();
                }
                if (bytes != null)
                {
                    var relative = _session.Todos.Attachments.ImportImageBytes(bytes, ext, id);
                    CommitNoteImages(id, box.Text ?? "", imgs => imgs.Append(relative).ToList());
                    return;
                }
                // 没有可识别的图片：若剪贴板也没有文字，把格式列表提示出来便于排查
                if (!formats.Any(f => f.Contains("text", StringComparison.OrdinalIgnoreCase)))
                {
                    _banner.Text = "剪贴板中没有可粘贴的图片/文字，格式：" + string.Join(", ", formats);
                    return;
                }
            }
        }
        catch (Exception error)
        {
            _banner.Text = "粘贴图片失败：" + error.Message;
            return;
        }
        box.Paste();
    }

    private async void AddTodoImages(Guid id, string currentText)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider == null) return;
        try
        {
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择图片",
                AllowMultiple = true,
                FileTypeFilter = [FilePickerFileTypes.ImageAll],
            });
            var added = new List<string>();
            foreach (var file in files)
            {
                var source = file.TryGetLocalPath();
                if (string.IsNullOrWhiteSpace(source)) continue;
                added.Add(_session.Todos.Attachments.ImportImage(source, id));
            }
            if (added.Count == 0) return;
            CommitNoteImages(id, currentText, imgs => imgs.Concat(added).ToList());
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
    private static string RebuildTodoNotes(string original, string plain, IEnumerable<string>? imageRefs = null)
    {
        var images = "";
        if (imageRefs != null)
            images = string.Concat(imageRefs.Select(r => "<img src=\"" + r + "\">"));
        else if (!string.IsNullOrEmpty(original))
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
                var rename = new MenuItem { Header = "修改标签", Icon = Ui.Icon(Glyphs.Pencil, 14, Palette.TextSecondary) };
                rename.Click += async (_, _) =>
                {
                    var newName = await Dialogs.Prompt(this, "修改标签", "输入新的标签名称", "标签名称", name);
                    var trimmed = newName?.Trim();
                    if (string.IsNullOrWhiteSpace(trimmed) || trimmed == name) return;
                    var err = _session.Notes.RenameTag(name, trimmed!);
                    if (err != null)
                    {
                        await Dialogs.Confirm(this, "无法修改标签", err, "知道了", "取消", danger: false);
                        return;
                    }
                    if (_tagFilter == name) _tagFilter = trimmed;
                    Refresh();
                };
                menu.Items.Add(rename);
                menu.Items.Add(new Separator());
                var remove = new MenuItem { Header = "删除标签", Foreground = Palette.Red, Icon = Ui.Icon(Glyphs.Trash, 14, Palette.Red) };
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
            // 只让左键选中；右键留给 ContextMenu，避免 Handled=true 吞掉右键手势导致菜单弹不出来。
            card.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(card).Properties.IsLeftButtonPressed)
                {
                    Select(current.Id);
                    e.Handled = true;
                }
            };

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
                var add = new MenuItem { Header = "新建笔记", Icon = Ui.Icon(Glyphs.Plus, 14, Palette.Accent) };
                add.Click += (_, _) => NewNote();
                menu.Items.Add(add);
                menu.Items.Add(new Separator());
                // 置顶图标沿用笔记列表里的图钉：置顶时用黄色（Palette.Amber），未置顶为灰色。
                var pin = new MenuItem
                {
                    Header = current.Pinned ? "取消置顶" : "置顶笔记",
                    Icon = Ui.Icon(Glyphs.Pin, 14, current.Pinned ? Palette.Amber : Palette.TextTertiary),
                };
                pin.Click += (_, _) => _session.Notes.TogglePin(current.Id);
                menu.Items.Add(pin);
                menu.Items.Add(new Separator());
                var trash = new MenuItem
                {
                    Header = "删除笔记",
                    Foreground = Palette.Red,
                    Icon = Ui.Icon(Glyphs.Trash, 14, Palette.Red),
                };
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

    /// <summary>标签框里的图标按钮：固定颜色、无边框无 hover 换色，保持用户给的图标色。</summary>
    private static Button TagIconButton(string glyph, IBrush color, string tip, Action click,
        int size = 18, int top = 1, int left = 0, int right = 0)
    {
        var button = new Button
        {
            Content = Icons.Vector(glyph, size - 2, color),
            Width = size,
            Height = size,
            Margin = new Thickness(left, top, right, 0), // left 控制与前一图标间距，right 控制离框右缘距离
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => click();
        return button;
    }

    private void ClearTagInput()
    {
        _tagInput.Text = "";
        UpdateTagFrame();
        _tagInput.Focus();
    }

    /// <summary>标签输入：有内容才显示边框、对勾与叉号，聚焦时用主题绿描边。</summary>
    private void UpdateTagFrame()
    {
        var hasText = !string.IsNullOrWhiteSpace(_tagInput.Text);
        _tagSubmit.IsVisible = hasText;
        _tagClear.IsVisible = hasText;
        _tagFrame.BorderBrush = !hasText ? Palette.Transparent
            : _tagInput.IsFocused ? Palette.FieldFocusRing : Palette.Line;
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
        UpdateTagFrame();
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
            VerticalContentAlignment = VerticalAlignment.Center,
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
            labelBlock.VerticalAlignment = VerticalAlignment.Center;
            // 与右侧数字统一行高，让不同字号的文字基线对齐、视觉垂直居中。
            labelBlock.LineHeight = 16;
            icon!.VerticalAlignment = VerticalAlignment.Center;
            var left = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { icon, labelBlock },
            };
            var countBlock = Ui.Text(count.Value.ToString(), 11, null, Palette.TextQuaternary);
            countBlock.VerticalAlignment = VerticalAlignment.Center;
            countBlock.LineHeight = 16;
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            grid.Children.Add(left);
            Grid.SetColumn(countBlock, 1);
            grid.Children.Add(countBlock);
            button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            button.Content = grid;
        }
        else if (icon != null && !tiny)
        {
            labelBlock = Ui.Text(label, 12, selected ? FontWeight.SemiBold : null, fg);
            labelBlock.VerticalAlignment = VerticalAlignment.Center;
            icon!.VerticalAlignment = VerticalAlignment.Center;
            button.Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center,
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
            nameBlock.VerticalAlignment = VerticalAlignment.Center;
            icon.VerticalAlignment = VerticalAlignment.Center;
            var left = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 9,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { icon, nameBlock },
            };
            var countBlock = Ui.Text(count.ToString(), 11, null, Palette.TextQuaternary);
            countBlock.VerticalAlignment = VerticalAlignment.Center;
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                VerticalAlignment = VerticalAlignment.Center,
            };
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

        // 右键菜单：新建笔记本 / 修改笔记本 / 删除笔记本（与笔记菜单同款：语义色图标 + 细分割线）
        var menu = new ContextMenu();
        var addItem = new MenuItem { Header = "新建笔记本", Icon = Ui.Icon(Glyphs.Plus, 14, Palette.Accent) };
        addItem.Click += (_, _) => PromptNewNotebook();
        menu.Items.Add(addItem);
        if (deletable)
        {
            menu.Items.Add(new Separator());
            var renameItem = new MenuItem { Header = "修改笔记本", Icon = Ui.Icon(Glyphs.Pencil, 14, Palette.TextSecondary) };
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
            menu.Items.Add(new Separator());
            var delItem = new MenuItem { Header = "删除笔记本", Foreground = Palette.Red, Icon = Ui.Icon(Glyphs.Trash, 14, Palette.Red) };
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
