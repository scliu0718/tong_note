using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Tonote.Core;

namespace Tonote.App;

public sealed class ShellView : UserControl
{
    private enum Pane { Notes, Trash, Todos, Settings }
    private enum SortMode { Updated, Created, Title }

    private readonly AppSession _session;
    private readonly StackPanel _sidebar = new() { Spacing = 4 };
    private readonly StackPanel _quick = new() { Spacing = 8, Margin = new Thickness(12, 12, 12, 0) };
    private readonly StackPanel _sortRow = new() { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(12, 8, 12, 4) };
    private readonly StackPanel _cards = new();
    private readonly ContentControl _right = new();
    private readonly TextBox _search = new() { Watermark = "搜索标题或正文", Margin = new Thickness(12, 10, 12, 0) };
    private readonly TextBox _title = new() { FontSize = 22, FontWeight = FontWeight.Bold, BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    private readonly RichTextEditor _richEditor;
    private readonly TextBlock _meta = new() { Foreground = Palette.TextTertiary, FontSize = 12 };
    private readonly TextBlock _banner = new() { Foreground = Palette.TextSecondary, FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _sidebarSync = new() { FontSize = 11, Foreground = Palette.Accent, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _settingsSync = new() { FontSize = 13, Foreground = Palette.TextPrimary, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _settingsAccount = new() { FontSize = 13, Foreground = Palette.TextSecondary, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _settingsDevices = new() { Spacing = 4 };
    private readonly TextBlock _count = new() { Foreground = Palette.TextTertiary, FontSize = 12 };
    private readonly ComboBox _notebook = new() { MinWidth = 120 };
    private readonly StackPanel _tags = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly TextBox _tagInput = new() { Watermark = "添加标签", Width = 120 };
    private readonly StackPanel _metaRow = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly TextBlock _handleGlyph = new() { Text = ">", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Palette.TextTertiary, FontSize = 12 };
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
    private int _sidebarWidth = 112;
    private bool _loading = true;
    private bool _dragging;
    private bool _didSwitch;
    private Point _press;

    public ShellView(AppSession session)
    {
        _session = session;
        _pinButton = SmallButton("置顶", TogglePin);
        _trashButton = SmallButton("移入回收站", MoveCurrentToTrash);
        _restoreButton = SmallButton("恢复笔记", RestoreCurrent);
        _purgeButton = SmallButton("彻底删除", PurgeCurrent);
        _syncOneButton = SmallButton("立即同步本篇", async () =>
        {
            if (_selected == null) return;
            await _session.Sync.SyncSingleNoteAsync(_selected.Value);
        });
        _handle = BuildHandle();
        _richEditor = new RichTextEditor(session) { Margin = new Thickness(20, 8, 20, 16) };
        _richEditor.StatusChanged += message => OnUi(() => _banner.Text = message);
        _search.TextChanged += (_, _) => RebuildCards();
        _title.TextChanged += (_, _) => SaveTitle();
        _notebook.SelectionChanged += (_, _) => SaveNotebook();
        _tagInput.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
                AddTag();
        };

        var list = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*"),
            Background = Palette.Panel,
            Children = { _quick, _search, _sortRow, new ScrollViewer { Content = _cards } },
        };
        Grid.SetRow(_search, 1);
        Grid.SetRow(_sortRow, 2);
        Grid.SetRow((Control)list.Children[3], 3);

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("112,6,292,1,*"),
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

        // 同步引擎 / 网盘回调都在线程池线程里发出事件，直接改控件会让整轮同步
        // 卡死在 UI 校验上（曾导致 GUI 启动后状态长期停在 Syncing）。统一投递到 UI 线程。
        _session.Notes.Changed += () => OnUi(Refresh);
        _session.Notebooks.Changed += () => OnUi(Refresh);
        _session.Todos.Changed += () => OnUi(() =>
        {
            if (_pane == Pane.Todos)
                ShowTodos();
            RebuildSidebar();
        });
        _session.Sync.StatusChanged += () => OnUi(UpdateSyncLabels);
        _session.Avatar.Changed += () => OnUi(() =>
        {
            RebuildSidebar();
            if (_pane == Pane.Settings)
                ShowSettings();
        });

        _loading = false;
        var first = _session.Notes.ActiveNotes.FirstOrDefault(note => !note.IsQuickSync);
        if (first != null)
            Select(first.Id);
        else
            Refresh();
    }

    /// <summary>把后台线程的事件投递到 UI 线程；用 Post 而非 InvokeAsync，避免后台线程等 UI 造成死锁。</summary>
    private static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }

    private void Refresh()
    {
        if (_loading)
            return;
        RebuildSidebar();
        RebuildQuick();
        RebuildSort();
        RebuildCards();
        UpdateChrome();
        var current = Current();
        if (current != null)
            _richEditor.LoadNote(current);
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
        _sidebarSync.Text = _sidebarWidth > 52 ? shortText : "●";
        _sidebarSync.Foreground = status.Kind == SyncStatusKind.Failed ? Palette.Red : Palette.Accent;
        _settingsSync.Text = shortText + (string.IsNullOrWhiteSpace(status.Message) ? "" : " · " + status.Message);
    }

    private void Select(Guid id)
    {
        _richEditor.Flush();
        _selected = id;
        if (_pane is Pane.Todos or Pane.Settings)
            _pane = Pane.Notes;
        LoadEditor();
        Refresh();
    }

    private void LoadEditor()
    {
        var note = Current();
        _right.Content = note == null ? Empty("选择或新建一篇笔记") : Editor();
        if (note == null)
            return;

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
        if (note == null || !ReferenceEquals(_right.Content, _editorCached))
            return;
        _meta.Text = note.IsTrashed ? "回收站中的笔记" : "已保存到本地 · " + note.TimeText;
        _count.Text = note.WordCount + " 字";
        var editable = !note.IsTrashed && !note.IsQuickSync;
        _syncOneButton.IsVisible = !note.IsTrashed;
        _syncOneButton.Content = note.IsQuickSync ? "立即同步本篇" : "同步本篇";
        _pinButton.IsVisible = editable;
        _trashButton.IsVisible = editable;
        _restoreButton.IsVisible = note.IsTrashed;
        _purgeButton.IsVisible = note.IsTrashed;
        _banner.Text = note.IsTrashed
            ? "回收站里的笔记不能编辑。"
            : note.IsQuickSync
                ? "快同步：点「立即同步本篇」只把这一篇推到网盘，不阻塞其它笔记。"
                : "正文自动保存到本机并同步到网盘。工具条支持标题、加粗、颜色、列表、链接、图片和表格。";
        _handleGlyph.Text = _sidebarWidth == 200 ? "<" : ">";
    }

    private Control? _editorCached;

    private Control Editor()
    {
        if (_editorCached != null)
            return _editorCached;

        _metaRow.Children.Add(_notebook);
        _metaRow.Children.Add(_tags);
        _metaRow.Children.Add(_tagInput);

        var toolbar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto,Auto"),
            Margin = new Thickness(20, 12, 20, 0),
            Children = { _count, _syncOneButton, _pinButton, _trashButton, _restoreButton, _purgeButton },
        };
        Grid.SetColumn(_syncOneButton, 1);
        Grid.SetColumn(_pinButton, 2);
        Grid.SetColumn(_trashButton, 3);
        Grid.SetColumn(_restoreButton, 4);
        Grid.SetColumn(_purgeButton, 5);
        _count.VerticalAlignment = VerticalAlignment.Center;

        var page = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*"),
            Background = Palette.Panel,
            Children =
            {
                toolbar,
                new Border { Child = _banner, Margin = new Thickness(20, 8, 20, 0), Padding = new Thickness(10, 8), Background = Palette.AccentTint, CornerRadius = new CornerRadius(8) },
                new Border { Child = _title, Margin = new Thickness(20, 12, 20, 0) },
                new StackPanel
                {
                    Margin = new Thickness(20, 8, 20, 0),
                    Spacing = 6,
                    Children = { _metaRow, _meta },
                },
                _richEditor,
            },
        };
        for (var row = 1; row < page.Children.Count; row++)
            Grid.SetRow((Control)page.Children[row], row);

        _editorCached = page;
        return page;
    }

    private void ShowTodos()
    {
        _pane = Pane.Todos;
        _selected = null;
        var input = new TextBox { Watermark = "添加一条快任务", Margin = new Thickness(20, 16, 20, 0) };
        input.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter)
                return;
            var title = (input.Text ?? "").Trim();
            if (title.Length == 0)
                return;
            _session.Todos.Add(title);
            input.Text = "";
        };

        var stack = new StackPanel { Spacing = 16, Margin = new Thickness(20, 12, 20, 20) };
        stack.Children.Add(new TextBlock { Text = "快任务", FontSize = 22, FontWeight = FontWeight.Bold, Foreground = Palette.TextPrimary });
        stack.Children.Add(new TextBlock
        {
            Text = $"待完成 {_session.Todos.PendingCount} · 进行中 {_session.Todos.DoingCount}",
            Foreground = Palette.TextTertiary,
        });
        foreach (var status in new[] { TodoStatus.todo, TodoStatus.doing, TodoStatus.done, TodoStatus.terminated })
        {
            stack.Children.Add(new TextBlock { Text = status.Label(), FontWeight = FontWeight.SemiBold, Foreground = Palette.TextSecondary });
            var group = _session.Todos.InStatus(status);
            if (group.Count == 0)
            {
                stack.Children.Add(new TextBlock { Text = "没有", Foreground = Palette.TextQuaternary, FontSize = 12 });
                continue;
            }

            foreach (var todo in group)
                stack.Children.Add(TodoRow(todo));
        }

        _right.Content = new ScrollViewer
        {
            Background = Palette.Panel,
            Content = new StackPanel { Children = { input, stack } },
        };
        Refresh();
    }

    private Control TodoRow(Todo todo)
    {
        var title = new TextBlock
        {
            Text = todo.IsOverdue ? todo.Title + "  · 逾期" : todo.Title,
            Foreground = todo.IsOverdue ? Palette.Red : Palette.TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var status in new[] { TodoStatus.todo, TodoStatus.doing, TodoStatus.done, TodoStatus.terminated })
        {
            if (status == todo.Status)
                continue;
            var target = status;
            actions.Children.Add(SmallButton(target.Label(), () => _session.Todos.SetStatus(todo.Id, target)));
        }

        actions.Children.Add(SmallButton("删除", () => _session.Todos.Remove(todo.Id)));

        var titleInput = new TextBox { Text = todo.Title, Watermark = "标题", FontSize = 13 };
        titleInput.LostFocus += (_, _) =>
        {
            var text = (titleInput.Text ?? "").Trim();
            if (text.Length > 0 && text != todo.Title)
                _session.Todos.UpdateTitle(todo.Id, text);
        };
        var notesInput = new TextBox
        {
            Text = todo.Notes,
            Watermark = "备注（支持粘贴图片路径）",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 60,
            FontSize = 12,
        };
        notesInput.LostFocus += (_, _) =>
        {
            if ((notesInput.Text ?? "") != todo.Notes)
                _session.Todos.UpdateNotes(todo.Id, notesInput.Text ?? "");
        };
        var dateInput = new TextBox
        {
            Text = todo.PlannedAt?.ToLocalTime().ToString("yyyy-MM-dd") ?? "",
            Watermark = "计划日期 yyyy-MM-dd",
            Width = 160,
            FontSize = 12,
        };
        var dateRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        dateRow.Children.Add(dateInput);
        dateRow.Children.Add(SmallButton("设置日期", () =>
        {
            if (DateTime.TryParse((dateInput.Text ?? "").Trim(), out var parsed))
                _session.Todos.SetPlannedAt(todo.Id, DateTime.SpecifyKind(parsed.Date.AddHours(9), DateTimeKind.Utc));
        }));
        dateRow.Children.Add(SmallButton("清除", () =>
        {
            dateInput.Text = "";
            _session.Todos.SetPlannedAt(todo.Id, null);
        }));

        var detail = new StackPanel
        {
            Spacing = 6,
            Margin = new Thickness(0, 2, 0, 10),
            Background = Palette.WindowBg,
            IsVisible = false,
            Children = { titleInput, notesInput, dateRow },
        };
        title.PointerPressed += (_, e) =>
        {
            detail.IsVisible = !detail.IsVisible;
            e.Handled = true;
        };

        var head = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 2),
        };
        head.Children.Add(title);
        head.Children.Add(actions);
        Grid.SetColumn(actions, 1);

        // 逾期任务：红框 + 淡红底，标识超过设置的完成时间
        var headFrame = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = todo.IsOverdue ? new Thickness(8, 6) : new Thickness(0),
            Background = todo.IsOverdue ? Palette.Brush(0xFCEBEB) : Brushes.Transparent,
            BorderBrush = todo.IsOverdue ? Palette.Red : Brushes.Transparent,
            BorderThickness = new Thickness(todo.IsOverdue ? 1 : 0),
            Child = head,
        };

        return new StackPanel
        {
            Children = { headFrame, detail },
        };
    }

    public event Action? LogoutRequested;

    private void ShowSettings()
    {
        _pane = Pane.Settings;
        _selected = null;

        var accountRow = new StackPanel { Spacing = 6 };
        accountRow.Children.Add(new TextBlock { Text = "账号与网盘", FontSize = 16, FontWeight = FontWeight.SemiBold });
        accountRow.Children.Add(_settingsAccount);

        var avatarRow = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4) };
        foreach (var preset in AvatarStore.Presets)
        {
            var choice = preset;
            var button = new Button
            {
                Content = choice.Glyph,
                Width = 34,
                Height = 34,
                Margin = new Thickness(0, 0, 6, 6),
                CornerRadius = new CornerRadius(17),
                Background = Palette.Brush(choice.Color1),
                Foreground = Palette.White,
                FontWeight = FontWeight.Bold,
            };
            button.Click += (_, _) => _session.Avatar.Select(choice.Id);
            avatarRow.Children.Add(button);
        }
        accountRow.Children.Add(new TextBlock { Text = "头像", FontSize = 12, Foreground = Palette.TextTertiary });
        accountRow.Children.Add(avatarRow);

        var syncRow = new StackPanel { Spacing = 6 };
        syncRow.Children.Add(new TextBlock { Text = "同步", FontSize = 16, FontWeight = FontWeight.SemiBold });
        syncRow.Children.Add(_settingsSync);
        var syncButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        syncButtons.Children.Add(SmallButton("立即同步", () => _ = _session.Sync.QuickSyncAsync()));
        syncButtons.Children.Add(SmallButton("查看本机目录", () => BrowserLauncher.Open(_session.Root)));
        syncRow.Children.Add(syncButtons);

        var deviceRow = new StackPanel { Spacing = 6 };
        deviceRow.Children.Add(new TextBlock { Text = "已登录设备", FontSize = 16, FontWeight = FontWeight.SemiBold });
        deviceRow.Children.Add(_settingsDevices);
        deviceRow.Children.Add(SmallButton("刷新设备列表", () => _ = LoadDevicesAsync()));

        var storageRow = new StackPanel { Spacing = 4 };
        storageRow.Children.Add(new TextBlock { Text = "本地数据", FontSize = 16, FontWeight = FontWeight.SemiBold });
        storageRow.Children.Add(new TextBlock { Text = _session.Root, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Palette.TextSecondary });
        storageRow.Children.Add(new TextBlock
        {
            Text = "notes.json / todos.json / notebooks.json 与 Mac 端字段一致；同步按篇写入 /apps/tong_log/notes 与 todos。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = Palette.TextSecondary,
        });

        var stack = new StackPanel
        {
            Margin = new Thickness(28),
            Spacing = 18,
            Children =
            {
                new TextBlock { Text = "设置", FontSize = 22, FontWeight = FontWeight.Bold },
                accountRow,
                new Border { Height = 1, Background = Palette.Line },
                syncRow,
                new Border { Height = 1, Background = Palette.Line },
                deviceRow,
                new Border { Height = 1, Background = Palette.Line },
                storageRow,
                SmallButton("退出登录", () => LogoutRequested?.Invoke()),
            },
        };

        _right.Content = new ScrollViewer { Background = Palette.Panel, Content = stack };
        UpdateSyncLabels();
        _ = LoadAccountAsync();
        _ = LoadDevicesAsync();
        Refresh();
    }

    private async Task LoadAccountAsync()
    {
        if (!_session.IsSignedIn)
        {
            _settingsAccount.Text = "未登录百度网盘。登录后笔记、任务、笔记本会按篇同步。";
            return;
        }
        try
        {
            var token = await _session.Pan.ValidTokenAsync().ConfigureAwait(false);
            var info = await _session.Pan.UserInfoAsync(token.AccessToken).ConfigureAwait(false);
            var quota = await _session.Pan.QuotaAsync(token.AccessToken).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() =>
            {
                _settingsAccount.Text = $"{info.DisplayName} · 已用 {quota.UsedText} / {quota.TotalText}（{quota.UsedRatio:P0}）";
            });
        }
        catch (Exception error)
        {
            Dispatcher.UIThread.Post(() => _settingsAccount.Text = "读取网盘信息失败：" + error.Message);
        }
    }

    private async Task LoadDevicesAsync()
    {
        if (!_session.IsSignedIn)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _settingsDevices.Children.Clear();
                _settingsDevices.Children.Add(new TextBlock { Text = "登录后显示设备列表。", Foreground = Palette.TextTertiary, FontSize = 12 });
            });
            return;
        }
        try
        {
            var records = await _session.Devices.ListDevicesAsync().ConfigureAwait(false);
            var mine = _session.Devices.CurrentDeviceId;
            Dispatcher.UIThread.Post(() =>
            {
                _settingsDevices.Children.Clear();
                if (records.Count == 0)
                {
                    _settingsDevices.Children.Add(new TextBlock { Text = "暂无设备记录", Foreground = Palette.TextTertiary, FontSize = 12 });
                    return;
                }
                foreach (var record in records)
                {
                    var target = record.DeviceId;
                    var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
                    row.Children.Add(new TextBlock
                    {
                        Text = record.DeviceName + (target == mine ? "（本机）" : "") + (record.Kicked ? " · 已下线" : ""),
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = record.Kicked ? Palette.TextTertiary : Palette.TextPrimary,
                    });
                    var kick = SmallButton("下线", () => _ = KickDeviceAsync(target));
                    Grid.SetColumn(kick, 1);
                    kick.IsVisible = target != mine && !record.Kicked;
                    row.Children.Add(kick);
                    var id = new TextBlock { Text = target[..8], FontSize = 10, Foreground = Palette.TextQuaternary, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
                    Grid.SetColumn(id, 2);
                    row.Children.Add(id);
                    _settingsDevices.Children.Add(row);
                }
            });
        }
        catch (Exception error)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _settingsDevices.Children.Clear();
                _settingsDevices.Children.Add(new TextBlock { Text = "读取设备列表失败：" + error.Message, Foreground = Palette.Red, FontSize = 12, TextWrapping = TextWrapping.Wrap });
            });
        }
    }

    private async Task KickDeviceAsync(string deviceId)
    {
        try
        {
            await _session.Devices.KickAsync(deviceId).ConfigureAwait(false);
            await LoadDevicesAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Dispatcher.UIThread.Post(() =>
                _settingsDevices.Children.Add(new TextBlock { Text = "下线失败：" + error.Message, Foreground = Palette.Red, FontSize = 12 }));
        }
    }

    private void RebuildSidebar()
    {
        _sidebar.Children.Clear();
        _sidebar.Margin = new Thickness(_sidebarWidth <= 52 ? 6 : 10, 12, 6, 12);

        _sidebar.Children.Add(new TextBlock
        {
            Text = _sidebarWidth <= 52 ? "同" : "同笔记",
            FontWeight = FontWeight.Bold,
            FontSize = _sidebarWidth <= 52 ? 14 : 16,
            Foreground = Palette.TextPrimary,
            Margin = new Thickness(4, 0, 0, 4),
        });
        _sidebar.Children.Add(new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(14),
            Background = Palette.Brush(_session.Avatar.Current.Color1),
            Margin = new Thickness(4, 0, 0, 6),
            Child = new TextBlock
            {
                Text = _session.Avatar.Current.Glyph,
                Foreground = Palette.White,
                FontWeight = FontWeight.Bold,
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        });
        UpdateSyncLabels();
        _sidebar.Children.Add(_sidebarSync);
        _sidebarSync.Margin = new Thickness(4, 0, 0, 8);
        _sidebarSync.IsVisible = _sidebarWidth > 52;
        _sidebar.Children.Add(AccentButton(_sidebarWidth == 200 ? "＋ 新建笔记" : "＋", NewNote));
        _sidebar.Children.Add(SideButton("设置", _pane == Pane.Settings, ShowSettings));

        _sidebar.Children.Add(Section("笔记本"));
        _sidebar.Children.Add(SideButton("全部笔记", _pane == Pane.Notes && _notebookFilter == null && _tagFilter == null, () =>
        {
            _pane = Pane.Notes;
            _notebookFilter = null;
            _tagFilter = null;
            Refresh();
        }, _session.Notes.CountInNotebook(Notebooks.All)));

        foreach (var name in _session.Notebooks.List)
        {
            var notebook = name;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var button = SideButton(notebook, _pane == Pane.Notes && _notebookFilter == notebook && _tagFilter == null, () =>
            {
                _pane = Pane.Notes;
                _notebookFilter = notebook;
                _tagFilter = null;
                Refresh();
            }, _session.Notes.CountInNotebook(notebook));
            row.Children.Add(button);
            if (_sidebarWidth == 200 && _session.Notebooks.Custom.Contains(notebook))
            {
                var remove = SmallButton("×", () =>
                {
                    _session.Notes.MoveNotes(notebook, Notebooks.All);
                    _session.Notebooks.Remove(notebook);
                    if (_notebookFilter == notebook)
                        _notebookFilter = null;
                });
                Grid.SetColumn(remove, 1);
                row.Children.Add(remove);
            }

            _sidebar.Children.Add(row);
        }

        if (_sidebarWidth == 200)
            _sidebar.Children.Add(NewNotebookBox());

        _sidebar.Children.Add(Section("标签"));
        var tags = _session.Notes.AllTags;
        if (tags.Count == 0)
        {
            _sidebar.Children.Add(new TextBlock { Text = _sidebarWidth <= 52 ? "" : "暂无标签", Foreground = Palette.TextQuaternary, FontSize = 12, Margin = new Thickness(4, 2, 0, 2) });
        }
        else
        {
            foreach (var tag in tags)
            {
                var name = tag;
                _sidebar.Children.Add(SideButton("#" + name, _tagFilter == name, () =>
                {
                    _pane = Pane.Notes;
                    _tagFilter = name;
                    _notebookFilter = null;
                    Refresh();
                }, _session.Notes.CountWithTag(name)));
            }
        }

        _sidebar.Children.Add(new Border { Height = 1, Background = Palette.Line, Margin = new Thickness(0, 8) });
        _sidebar.Children.Add(SideButton("回收站", _pane == Pane.Trash, () =>
        {
            _pane = Pane.Trash;
            _notebookFilter = null;
            _tagFilter = null;
            Refresh();
        }, _session.Notes.TrashNotes.Count));
        _sidebar.Children.Add(SideButton("快任务", _pane == Pane.Todos, ShowTodos, _session.Todos.PendingCount + _session.Todos.DoingCount, _session.Todos.OverdueCount));
    }

    private Control NewNotebookBox()
    {
        var input = new TextBox { Watermark = "新笔记本", Margin = new Thickness(0, 4, 0, 0) };
        var error = new TextBlock { Foreground = Palette.Red, FontSize = 11, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        var button = SmallButton("新建笔记本", () =>
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
        if (_pane != Pane.Notes)
            return;

        _quick.Children.Add(QuickCard("快同步", "打开这篇独立笔记", _selected == Note.QuickSyncNoteId, () =>
        {
            _pane = Pane.Notes;
            Select(Note.QuickSyncNoteId);
        }));
        _quick.Children.Add(QuickCard(
            "快任务",
            $"待完成 {_session.Todos.PendingCount} · 进行中 {_session.Todos.DoingCount}",
            _pane == Pane.Todos,
            ShowTodos,
            _session.Todos.OverdueCount));
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
        var text = new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = _sort == mode ? Palette.Accent : Palette.TextTertiary,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        text.PointerPressed += (_, _) =>
        {
            _sort = mode;
            RebuildSort();
            RebuildCards();
        };
        _sortRow.Children.Add(text);
    }

    private void RebuildCards()
    {
        _cards.Children.Clear();
        var notes = Filtered().ToList();
        if (notes.Count == 0)
        {
            _cards.Children.Add(new TextBlock
            {
                Text = _pane == Pane.Trash ? "回收站是空的" : "没有匹配的笔记",
                Margin = new Thickness(16),
                Foreground = Palette.TextQuaternary,
            });
            return;
        }

        foreach (var note in notes)
        {
            var current = note;
            var selected = _selected == current.Id;
            var card = new Border
            {
                Background = selected ? Palette.CardSelected : Palette.Panel,
                BorderBrush = Palette.LineSoft,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(14, 10),
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = (current.Pinned ? "📌 " : "") + current.Title,
                            FontWeight = FontWeight.SemiBold,
                            Foreground = Palette.TextPrimary,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                        },
                        new TextBlock
                        {
                            Text = current.Snippet,
                            FontSize = 12,
                            Foreground = Palette.TextTertiary,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                        },
                        new TextBlock { Text = current.TimeText, FontSize = 11, Foreground = Palette.TextQuaternary },
                    },
                },
            };
            card.PointerPressed += (_, e) =>
            {
                if (_pane == Pane.Trash)
                    _pane = Pane.Trash;
                Select(current.Id);
                e.Handled = true;
            };
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

    private void SaveTitle()
    {
        if (_loading || _selected == null)
            return;
        var note = Current();
        if (note == null || note.IsTrashed || note.Title == _title.Text)
            return;
        _session.Notes.UpdateTitle(note.Id, _title.Text ?? "");
    }

    private void SaveNotebook()
    {
        if (_loading || _selected == null || _notebook.SelectedItem is not string name)
            return;
        var note = Current();
        if (note == null || note.IsQuickSync || note.Notebook == name)
            return;
        _session.Notes.SetNotebook(note.Id, name);
    }

    private void AddTag()
    {
        if (_selected == null)
            return;
        var note = Current();
        if (note == null || note.IsQuickSync || note.IsTrashed)
            return;
        var text = (_tagInput.Text ?? "").Trim();
        if (text.Length == 0)
            return;
        var tags = note.Tags.ToList();
        if (!tags.Contains(text))
            tags.Add(text);
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
            var chip = SmallButton(name + " ×", () =>
            {
                var latest = Current();
                if (latest == null)
                    return;
                _session.Notes.UpdateTags(latest.Id, latest.Tags.Where(item => item != name));
                var refreshed = Current();
                if (refreshed != null)
                    RenderTags(refreshed);
            });
            _tags.Children.Add(chip);
        }
    }

    private void TogglePin()
    {
        if (_selected != null)
            _session.Notes.TogglePin(_selected.Value);
    }

    private void MoveCurrentToTrash()
    {
        if (_selected == null)
            return;
        var id = _selected.Value;
        _session.Notes.MoveToTrash(id);
        var next = _session.Notes.ActiveNotes.FirstOrDefault(note => !note.IsQuickSync);
        if (next == null)
        {
            _selected = null;
            _right.Content = Empty("选择或新建一篇笔记");
            Refresh();
        }
        else
        {
            Select(next.Id);
        }
    }

    private void RestoreCurrent()
    {
        if (_selected == null)
            return;
        var id = _selected.Value;
        _session.Notes.Restore(id);
        _pane = Pane.Notes;
        Select(id);
    }

    private void PurgeCurrent()
    {
        if (_selected == null)
            return;
        _session.Notes.PermanentlyDelete(_selected.Value);
        _selected = null;
        _right.Content = Empty("笔记已移除。删除会以 purged 墓碑同步到其它设备。");
        Refresh();
    }

    private void NewNote()
    {
        var notebook = _notebookFilter is null || _notebookFilter == Notebooks.All
            ? "工作日志"
            : _notebookFilter;
        var note = _session.Notes.AddNote(notebook);
        if (_tagFilter != null)
            _session.Notes.UpdateTags(note.Id, [_tagFilter]);
        _pane = Pane.Notes;
        Select(note.Id);
    }

    private List<string> NotebookChoices(Note note)
    {
        var list = _session.Notebooks.List.ToList();
        if (!list.Contains(note.Notebook))
            list.Insert(0, note.Notebook);
        return list;
    }

    private Note? Current() => _selected == null ? null : _session.Notes.Find(_selected.Value);

    private Border BuildHandle()
    {
        var handle = new Border
        {
            Background = Palette.SidebarBg,
            Cursor = new Cursor(StandardCursorType.SizeWestEast),
            Child = new Border
            {
                Width = 16,
                Height = 22,
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
            _dragging = true;
            _didSwitch = false;
            _press = e.GetPosition(this);
            e.Pointer.Capture(handle);
        };
        handle.PointerMoved += (_, e) =>
        {
            if (!_dragging || _didSwitch)
                return;
            var point = e.GetPosition(this);
            if (Math.Abs(point.X - _press.X) < 4)
                return;
            var width = point.X < 82 ? 52 : point.X < 156 ? 112 : 200;
            if (width == _sidebarWidth)
                return;
            _didSwitch = true;
            SetSidebar(width);
        };
        handle.PointerReleased += (_, e) =>
        {
            var moved = _didSwitch;
            _dragging = false;
            e.Pointer.Capture(null);
            handle.Background = Palette.SidebarBg;
            if (!moved)
                SetSidebar(_sidebarWidth == 52 ? 112 : _sidebarWidth == 112 ? 200 : 52);
        };
        return handle;
    }

    private void SetSidebar(int width)
    {
        _sidebarWidth = width;
        if (Content is Grid grid)
            grid.ColumnDefinitions[0].Width = new GridLength(width);
        _handleGlyph.Text = width == 200 ? "<" : ">";
        RebuildSidebar();
    }

    private Button AccentButton(string text, Action click)
    {
        var button = new Button
        {
            Content = text,
            Background = Palette.Accent,
            Foreground = Palette.White,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 4, 0, 8),
        };
        button.Click += (_, _) => click();
        return button;
    }

    private Button SideButton(string text, bool selected, Action click, int? count = null, int? badge = null)
    {
        var label = _sidebarWidth <= 52
            ? text[..1]
            : count == null ? text : $"{text}  {count}";
        var button = new Button
        {
            Background = selected ? Palette.AccentTint : Brushes.Transparent,
            Foreground = selected ? Palette.Accent : Palette.TextSecondary,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = _sidebarWidth == 200 ? HorizontalAlignment.Left : HorizontalAlignment.Center,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 6),
            FontSize = 13,
        };
        // 逾期角标：极窄档（仅图标）不显示，与计数一致
        if (badge is > 0 && _sidebarWidth > 52)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var textBlock = new TextBlock
            {
                Text = label,
                Foreground = selected ? Palette.Accent : Palette.TextSecondary,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
            };
            grid.Children.Add(textBlock);
            var chip = Badge(badge.Value);
            chip.Margin = new Thickness(6, 0, 0, 0);
            chip.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(chip, 1);
            grid.Children.Add(chip);
            button.Content = grid;
        }
        else
        {
            button.Content = label;
        }
        button.Click += (_, _) => click();
        return button;
    }

    private static TextBlock Section(string text) => new()
    {
        Text = text,
        FontSize = 11,
        Foreground = Palette.TextQuaternary,
        Margin = new Thickness(4, 10, 0, 2),
    };

    private static Button SmallButton(string text, Action click)
    {
        var button = new Button
        {
            Content = text,
            FontSize = 12,
            Foreground = Palette.TextSecondary,
            Background = Brushes.Transparent,
            Padding = new Thickness(8, 4),
        };
        button.Click += (_, _) => click();
        return button;
    }

    private static Control Empty(string text) => new TextBlock
    {
        Text = text,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = Palette.TextTertiary,
        FontSize = 14,
    };

    private static Control QuickCard(string title, string subtitle, bool selected, Action click, int? badge = null)
    {
        var text = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, Foreground = Palette.TextPrimary },
                new TextBlock { Text = subtitle, FontSize = 11, Foreground = Palette.TextTertiary },
            },
        };

        Control content = text;
        if (badge is > 0)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            grid.Children.Add(text);
            var chip = Badge(badge.Value);
            chip.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(chip, 1);
            grid.Children.Add(chip);
            content = grid;
        }

        var card = new Border
        {
            Background = selected ? Palette.CardSelected : Palette.WindowBg,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = content,
        };
        card.PointerPressed += (_, e) =>
        {
            click();
            e.Handled = true;
        };
        return card;
    }

    /// <summary>红底白字的数量角标（逾期任务数）。</summary>
    private static Border Badge(int count)
    {
        return new Border
        {
            Background = Palette.Red,
            CornerRadius = new CornerRadius(9),
            MinWidth = 18,
            MinHeight = 18,
            Padding = new Thickness(5, 1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = count > 99 ? "99+" : count.ToString(),
                Foreground = Palette.White,
                FontSize = 10,
                FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }
}
