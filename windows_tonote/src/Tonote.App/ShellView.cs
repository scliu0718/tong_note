using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
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
    private readonly TextBox _body = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = Brushes.Transparent, FontSize = 15, MinHeight = 360 };
    private readonly TextBlock _meta = new() { Foreground = Palette.TextTertiary, FontSize = 12 };
    private readonly TextBlock _banner = new() { Foreground = Palette.TextSecondary, FontSize = 12, TextWrapping = TextWrapping.Wrap };
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
        _handle = BuildHandle();
        _search.TextChanged += (_, _) => RebuildCards();
        _title.TextChanged += (_, _) => SaveTitle();
        _body.TextChanged += (_, _) => SaveBody();
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

        _session.Notes.Changed += Refresh;
        _session.Notebooks.Changed += Refresh;
        _session.Todos.Changed += () =>
        {
            if (_pane == Pane.Todos)
                ShowTodos();
            RebuildSidebar();
        };

        _loading = false;
        var first = _session.Notes.ActiveNotes.FirstOrDefault(note => !note.IsQuickSync);
        if (first != null)
            Select(first.Id);
        else
            Refresh();
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
    }

    private void Select(Guid id)
    {
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
        _body.Text = note.ContentFormat == NoteContentFormat.richText ? note.VisibleBody : note.Body;
        _body.IsReadOnly = note.IsTrashed || note.ContentFormat == NoteContentFormat.richText;
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
        _pinButton.IsVisible = editable;
        _trashButton.IsVisible = editable;
        _restoreButton.IsVisible = note.IsTrashed;
        _purgeButton.IsVisible = note.IsTrashed;
        _banner.Text = note.ContentFormat == NoteContentFormat.richText
            ? "这篇是富文本。编辑器还在迁移，先只读，避免把 HTML 写坏。"
            : note.IsTrashed
                ? "回收站里的笔记不能编辑。"
                : "正文会自动保存到本机。加粗、表格和图片随富文本编辑器一起迁移。";
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
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto"),
            Margin = new Thickness(20, 12, 20, 0),
            Children = { _count, _pinButton, _trashButton, _restoreButton, _purgeButton },
        };
        Grid.SetColumn(_pinButton, 1);
        Grid.SetColumn(_trashButton, 2);
        Grid.SetColumn(_restoreButton, 3);
        Grid.SetColumn(_purgeButton, 4);
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
                new ScrollViewer { Content = _body, Margin = new Thickness(20, 8, 20, 16) },
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
        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 2),
            Children = { title, actions },
        };
    }

    private void ShowSettings()
    {
        _pane = Pane.Settings;
        _selected = null;
        _right.Content = new StackPanel
        {
            Margin = new Thickness(28),
            Spacing = 12,
            Background = Palette.Panel,
            Children =
            {
                new TextBlock { Text = "同步与存储", FontSize = 22, FontWeight = FontWeight.Bold },
                new TextBlock { Text = "百度网盘登录、按篇同步和设备下线还在迁移。", TextWrapping = TextWrapping.Wrap, Foreground = Palette.TextSecondary },
                new TextBlock { Text = "本机目录", Foreground = Palette.TextTertiary, FontSize = 12 },
                new TextBlock { Text = _session.Root, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = "笔记文件是 notes.json，任务是 todos.json，和 Mac 版字段一致。", TextWrapping = TextWrapping.Wrap, Foreground = Palette.TextSecondary },
            },
        };
        Refresh();
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
        _sidebar.Children.Add(new TextBlock
        {
            Text = _sidebarWidth == 200 ? "已保存到本地" : "本地",
            FontSize = 11,
            Foreground = Palette.Accent,
            Margin = new Thickness(4, 0, 0, 8),
            IsVisible = _sidebarWidth > 52,
        });
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
        _sidebar.Children.Add(SideButton("快任务", _pane == Pane.Todos, ShowTodos, _session.Todos.PendingCount + _session.Todos.DoingCount));
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
            ShowTodos));
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

    private void SaveBody()
    {
        if (_loading || _selected == null)
            return;
        var note = Current();
        if (note == null || note.IsTrashed || note.ContentFormat == NoteContentFormat.richText)
            return;
        if (note.Body == (_body.Text ?? ""))
            return;
        _session.Notes.UpdateContent(note.Id, _body.Text ?? "", NoteContentFormat.markdown);
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
        _right.Content = Empty("笔记已从列表移除。云端墓碑会在同步接入后上传。");
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

    private Button SideButton(string text, bool selected, Action click, int? count = null)
    {
        var label = _sidebarWidth <= 52
            ? text[..1]
            : count == null ? text : $"{text}  {count}";
        var button = new Button
        {
            Content = label,
            Background = selected ? Palette.AccentTint : Brushes.Transparent,
            Foreground = selected ? Palette.Accent : Palette.TextSecondary,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = _sidebarWidth == 200 ? HorizontalAlignment.Left : HorizontalAlignment.Center,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 6),
            FontSize = 13,
        };
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

    private static Control QuickCard(string title, string subtitle, bool selected, Action click)
    {
        var card = new Border
        {
            Background = selected ? Palette.CardSelected : Palette.WindowBg,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, Foreground = Palette.TextPrimary },
                    new TextBlock { Text = subtitle, FontSize = 11, Foreground = Palette.TextTertiary },
                },
            },
        };
        card.PointerPressed += (_, e) =>
        {
            click();
            e.Handled = true;
        };
        return card;
    }
}
