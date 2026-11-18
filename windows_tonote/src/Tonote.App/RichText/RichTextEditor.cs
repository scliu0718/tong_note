using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Tonote.Core;

namespace Tonote.App;

/// <summary>
/// 富文本编辑区：16 项工具条（Mac EditorView.swift 1:1）+ WebView2 contenteditable。
/// 所有保存都经 RichTextHtmlCodec 归一化，保证写进 notes.json 的是三端受控 HTML。
/// WebView2 运行时缺失时自动退回纯文本框，不让编辑功能整体失效。
/// </summary>
public sealed class RichTextEditor : UserControl
{
    private static readonly string[] FontSizes =
        ["11", "12", "13", "14", "15", "16", "18", "20", "24", "28", "32", "40"];

    private static readonly (string Name, string Hex)[] CellColors =
    [
        ("白", "#FFFFFF"), ("灰", "#F2F2F4"), ("深灰", "#D8D9DE"), ("石墨", "#B9BCC5"), ("铅", "#8E929B"),
        ("肤", "#FBE4D6"), ("奶黄", "#FCEFB4"), ("浅绿", "#DCEFD8"), ("浅青", "#D6EFF0"), ("浅蓝", "#D9E6F8"),
        ("炭", "#5B5B60"), ("银", "#8B8F98"), ("紫", "#7A5AF8"), ("品红", "#C86DD7"), ("红", "#E5484D"),
        ("橙", "#F59E42"), ("黄", "#F7CE46"), ("绿", "#35A45A"), ("青", "#2EB3AB"), ("蓝", "#2F7BEE"),
    ];

    private static readonly (string Name, string Hex)[] TextColors =
    [
        ("黑", "#1D1D1F"), ("红", "#E5484D"), ("橙", "#F59E42"), ("金", "#E8A317"),
        ("绿", "#35A45A"), ("蓝", "#2E7CF6"), ("紫", "#7A5AF8"), ("粉", "#C86DD7"),
    ];

    private static readonly (string Name, string Hex)[] BackgroundColors =
    [
        ("无", "#FFFFFF"), ("淡灰", "#F2F2F4"), ("淡黄", "#FFF3D6"), ("淡绿", "#E6F4EC"),
        ("淡蓝", "#E5F5FF"), ("淡红", "#FCEBEB"), ("淡紫", "#F0EAFE"), ("淡橙", "#FBE4D6"),
    ];

    private readonly AppSession _session;
    private readonly WebView2Host _host;
    private readonly TextBox _fallback;
    private readonly WrapPanel _toolbar = new()
    {
        Orientation = Orientation.Horizontal,
        Margin = new Thickness(Metrics.ToolbarPaddingH, Metrics.ToolbarPaddingV),
    };
    private readonly Border _popover = new()
    {
        IsVisible = false,
        Margin = new Thickness(Metrics.ToolbarPaddingH, 0, Metrics.ToolbarPaddingH, 6),
        HorizontalAlignment = HorizontalAlignment.Left,
    };
    private readonly TextBlock _status = new()
    {
        FontSize = 11,
        Foreground = Palette.TextTertiary,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(8, 0, 0, 0),
    };
    private readonly TextBox _linkInput = new() { Watermark = "https://…", Width = 150, FontSize = 12 };

    private Guid _currentId;
    private string _currentBody = "";
    private NoteContentFormat _currentFormat = NoteContentFormat.markdown;
    private string? _pendingHtml;
    private string? _lastHtml;
    // W-09：打开 markdown 笔记时记下「由原稿转出的 HTML」基线。
    // 只有内容真的偏离基线（用户真实输入）才允许升级 contentFormat 并改写原稿。
    private string? _markdownBaseline;
    private bool _readOnly;
    private bool _webFailed;
    private bool _loadingFallback;
    private string _openPopover = "";
    private Control? _trailing;
    private CancellationTokenSource? _saveDelay;

    public event Action<string>? StatusChanged;
    public event Action? DeleteRequested;

    /// <summary>工具条右侧附加控件（字数统计由 ShellView 注入）。</summary>
    public Control? ToolbarTrailing
    {
        get => _trailing;
        set
        {
            _trailing = value;
            if (value != null) _toolbar.Children.Add(value);
        }
    }

    public RichTextEditor(AppSession session)
    {
        _session = session;
        Directory.CreateDirectory(session.Root);
        var htmlPath = Path.Combine(session.Root, "editor.html");
        File.WriteAllText(htmlPath, EditorHtml.Content);

        _host = new WebView2Host(Path.Combine(session.Root, "webview2"))
        {
            MinHeight = 300,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        _fallback = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            FontSize = Metrics.FontEditorBody,
            MinHeight = 300,
            IsVisible = false,
        };
        _fallback.TextChanged += (_, _) => SaveFallback();

        _host.WebMessageReceived += OnWebMessage;
        _host.Ready += OnReady;
        _host.Failed += OnFailed;

        BuildToolbar();

        var surface = new Grid { Children = { _host, _fallback } };
        var page = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*"),
            Children = { _toolbar, _popover, surface },
            Background = Palette.Panel,
        };
        Grid.SetRow(_popover, 1);
        Grid.SetRow(surface, 2);
        Content = page;

        _host.Navigate(new Uri(htmlPath).AbsoluteUri);
    }

    public bool IsWebViewReady => _host.IsReady;
    public Guid CurrentNoteId => _currentId;

    // ── 16 项工具条（Mac EditorView.swift:376-489） ─────────────────────
    private void BuildToolbar()
    {
        Add(Glyphs.TextStyle, "文本样式", () => TogglePopover("style", TextStylePopover()));
        Add("B", "加粗", () => Run("exec", "bold"));
        Add("I", "斜体", () => Run("exec", "italic"));
        Add("U", "下划线", () => Run("exec", "underline"));
        Add("S", "删除线", () => Run("exec", "strikeThrough"));
        _toolbar.Children.Add(Ui.VDivider());

        Add("14", "字号", () => TogglePopover("size", FontSizePopover()));
        Add("A", "文字颜色", () => TogglePopover("fore", ColorPopover(false)));
        Add(Glyphs.Highlight, "背景颜色", () => TogglePopover("back", ColorPopover(true)));
        _toolbar.Children.Add(Ui.VDivider());

        Add(Glyphs.Quote, "引用", () => Run("block", "blockquote"));
        Add(Glyphs.Code, "代码块", () => Run("block", "pre"));
        Add("1.", "有序列表", () => Run("exec", "insertOrderedList"));
        Add("•", "无序列表", () => Run("exec", "insertUnorderedList"));
        _toolbar.Children.Add(Ui.VDivider());

        Add(Glyphs.Link, "插入链接", () => TogglePopover("link", LinkPopover()));
        Add(Glyphs.Image, "插入图片", InsertImage);
        Add(Glyphs.Table, "插入表格", () => TogglePopover("table", TablePopover()));
        Add(Glyphs.Align, "单元格对齐", () => TogglePopover("align", AlignPopover()));
        _toolbar.Children.Add(Ui.VDivider());

        Add(Glyphs.Trash, "移入回收站", () => DeleteRequested?.Invoke(), wide: true);
        _toolbar.Children.Add(_status);
    }

    private void Add(string glyph, string tip, Action click, bool wide = false)
        => _toolbar.Children.Add(Ui.Tool(glyph, tip, click, wide));

    private void TogglePopover(string key, Control content)
    {
        if (_openPopover == key)
        {
            _openPopover = "";
            _popover.IsVisible = false;
            _popover.Child = null;
            return;
        }
        _openPopover = key;
        _popover.Child = Ui.Popover(content, WidthFor(key));
        _popover.IsVisible = true;
    }

    private void ClosePopover()
    {
        _openPopover = "";
        _popover.IsVisible = false;
        _popover.Child = null;
    }

    private static double WidthFor(string key) => key switch
    {
        "style" => Metrics.PopTextStyle,
        "fore" or "back" => Metrics.PopColor,
        "table" => Metrics.PopTableGrid,
        "align" => Metrics.PopAlign,
        _ => 260,
    };

    private static Button MenuOption(string text, Action click, bool active = false)
    {
        var button = new Button
        {
            Content = Ui.Text(text, 12.5, active ? FontWeight.SemiBold : null,
                active ? Palette.AccentHover : Palette.TextPrimary),
            Background = active ? Palette.Pill : Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(10, 7),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        button.Click += (_, _) => click();
        return button;
    }

    private Control TextStylePopover()
    {
        var stack = new StackPanel { Spacing = 2 };
        foreach (var (label, tag) in new[]
        {
            ("正文", "p"), ("标题 1 · 40", "h1"), ("标题 2 · 32", "h2"),
            ("标题 3 · 24", "h3"), ("标题 4 · 18", "h4"),
            ("有序列表", "ol"), ("无序列表", "ul"),
        })
        {
            var value = tag;
            stack.Children.Add(MenuOption(label, () =>
            {
                if (value == "ol") Run("exec", "insertOrderedList");
                else if (value == "ul") Run("exec", "insertUnorderedList");
                else Run("block", value);
                ClosePopover();
            }));
        }
        return stack;
    }

    private Control FontSizePopover()
    {
        var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var size in FontSizes)
        {
            var value = size;
            var button = new Button
            {
                Content = Ui.Text(value, 12, value == "15" ? FontWeight.Bold : null,
                    value == "15" ? Palette.TableAccent : Palette.TextPrimary),
                Width = 44, Height = 30,
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(5),
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            button.Click += (_, _) => { Run("setFontSize", value); ClosePopover(); };
            wrap.Children.Add(button);
        }
        return new StackPanel
        {
            Spacing = 6,
            Children = { Ui.Text("字号（默认 14）", 11, null, Palette.TextTertiary), wrap },
        };
    }

    private Control ColorPopover(bool background)
    {
        var grid = new WrapPanel { Orientation = Orientation.Horizontal };
        var colors = background ? BackgroundColors : TextColors;
        foreach (var (name, hex) in colors)
        {
            var value = hex;
            var chip = new Button
            {
                Width = 26, Height = 26,
                Margin = new Thickness(3),
                Background = Palette.Brush(HexToUInt(value)),
                BorderBrush = Palette.Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            ToolTip.SetTip(chip, name + " " + value);
            chip.Click += (_, _) =>
            {
                Run("exec", background ? "hiliteColor" : "foreColor", value);
                ClosePopover();
            };
            grid.Children.Add(chip);
        }
        var custom = new TextBox { Watermark = "#RRGGBB", Width = 96, FontSize = 11 };
        var apply = Ui.Primary("自定义", () =>
        {
            var value = (custom.Text ?? "").Trim();
            if (!value.StartsWith('#')) value = "#" + value;
            if (value.Length != 7) return;
            Run("exec", background ? "hiliteColor" : "foreColor", value);
            ClosePopover();
        }, 11);
        var customRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { custom, apply } };
        var stack = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                Ui.Text(background ? "背景 / 高亮色（9 色 + 自定义）" : "文字颜色（8 色 + 自定义）", 11, null, Palette.TextTertiary),
                grid,
                customRow,
                MenuOption(background ? "清除背景色" : "恢复默认文字色", () =>
                {
                    Run("exec", background ? "hiliteColor" : "foreColor",
                        background ? "#FFFFFF" : "#1D1D1F");
                    ClosePopover();
                }),
            },
        };
        return stack;
    }

    private static uint HexToUInt(string hex)
    {
        var value = hex.TrimStart('#');
        return Convert.ToUInt32(value, 16);
    }

    private Control LinkPopover()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(_linkInput);
        row.Children.Add(Ui.Primary("插入", () =>
        {
            var url = (_linkInput.Text ?? "").Trim();
            if (url.Length == 0) return;
            Run("insertLink", url, "");
            _linkInput.Text = "";
            ClosePopover();
        }, 11));
        return new StackPanel
        {
            Spacing = 6,
            Children = { Ui.Text("链接地址（预填 https://）", 11, null, Palette.TextTertiary), row },
        };
    }

    private Control TablePopover()
    {
        var grid = new Grid();
        for (var i = 0; i < 8; i++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        for (var i = 0; i < 6; i++) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (var r = 0; r < 6; r++)
        for (var c = 0; c < 8; c++)
        {
            var rows = r + 1;
            var cols = c + 1;
            var cell = new Button
            {
                Width = 18, Height = 18,
                Margin = new Thickness(2),
                Background = Palette.Panel,
                BorderBrush = Palette.Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
            };
            cell.Click += (_, _) => { Run("insertTable", rows.ToString(), cols.ToString()); ClosePopover(); };
            Grid.SetRow(cell, r);
            Grid.SetColumn(cell, c);
            grid.Children.Add(cell);
        }
        var actions = new StackPanel { Spacing = 2 };
        foreach (var (label, action) in new (string, Action)[]
        {
            ("在下方插入行", () => Run("insertRow")),
            ("在右侧插入列", () => Run("insertColumn")),
            ("删除当前行", () => Run("removeRow")),
            ("删除当前列", () => Run("removeColumn")),
            ("删除整个表格", () => Run("removeTable")),
            // W-08：满宽 = 移除 data-fullwidth 属性（契约默认）；固定宽 = 写 "0"
            ("匹配笔记宽度", () => Run("setFullWidth", "1")),
            ("固定列宽（每列 100px）", () => Run("setFullWidth", "0")),
        })
        {
            var run = action;
            actions.Children.Add(MenuOption(label, () => { run(); ClosePopover(); }));
        }
        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Ui.Text("插入表格（6 行 × 8 列，上限 100×32）", 11, null, Palette.TextTertiary),
                grid,
                Ui.Hairline(),
                actions,
            },
        };
    }

    private Control AlignPopover()
    {
        var stack = new StackPanel { Spacing = 2 };
        foreach (var (label, value) in new[]
        {
            ("左对齐", "left"), ("居中", "center"), ("右对齐", "right"), ("两端对齐", "justify"),
        })
        {
            var align = value;
            stack.Children.Add(MenuOption(label, () => { Run("setCellAlign", align); ClosePopover(); }));
        }
        stack.Children.Add(Ui.Hairline());
        stack.Children.Add(Ui.Text("单元格底色（20 色）", 11, null, Palette.TextTertiary));
        var colors = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var (name, hex) in CellColors)
        {
            var value = hex;
            var chip = new Button
            {
                Width = 20, Height = 20,
                Margin = new Thickness(3),
                Background = Palette.Brush(HexToUInt(value)),
                BorderBrush = Palette.Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
            };
            ToolTip.SetTip(chip, name + " " + value);
            chip.Click += (_, _) => { Run("setCellBackground", value); ClosePopover(); };
            colors.Children.Add(chip);
        }
        stack.Children.Add(colors);
        stack.Children.Add(MenuOption("清除单元格底色", () => { Run("setCellBackground", "#FFFFFF"); ClosePopover(); }));
        return stack;
    }

    // ── 内容载入 / 保存 ───────────────────────────────────────────────
    public void LoadNote(Note note)
    {
        if (note.Id == _currentId && note.Body == _currentBody && note.ContentFormat == _currentFormat)
        {
            SetReadOnly(note.IsTrashed);
            return;
        }

        _currentId = note.Id;
        _currentBody = note.Body;
        _currentFormat = note.ContentFormat;
        _pendingHtml = note.ContentFormat == NoteContentFormat.richText
            ? note.Body
            : RichTextHtmlCodec.Encode(MarkdownConverter.ToDocument(note.Body));
        // W-09：markdown 基线取「解码→再编码」的规范化结果，与 JS clean() 后的形态一致
        _markdownBaseline = note.ContentFormat == NoteContentFormat.markdown
            ? RichTextHtmlCodec.Encode(RichTextHtmlCodec.Decode(_pendingHtml))
            : null;
        ClosePopover();

        _loadingFallback = true;
        _fallback.Text = note.VisibleBody;
        _loadingFallback = false;
        SetReadOnly(note.IsTrashed);
        if (_host.IsReady) PushContent();
    }

    private void SetReadOnly(bool readOnly)
    {
        _readOnly = readOnly;
        _fallback.IsReadOnly = readOnly;
        // Mac 回收站只读态：整条工具栏不渲染（设计稿 :749/:815）
        _toolbar.IsVisible = !readOnly;
        _toolbar.IsEnabled = !readOnly;
        if (readOnly) ClosePopover();
        if (_host.IsReady)
            _ = _host.ExecuteScriptAsync("document.getElementById('editor').contentEditable = "
                + (readOnly ? "false" : "true") + ";");
    }

    private void OnReady()
    {
        _status.Text = "";
        PushContent();
    }

    private void OnFailed(string message)
    {
        _webFailed = true;
        _host.IsVisible = false;
        _fallback.IsVisible = true;
        _status.Text = "WebView2 不可用，已退回纯文本";
        StatusChanged?.Invoke("WebView2 运行时不可用，富文本暂以纯文本编辑（不会破坏已有 HTML）：" + message);
    }

    private void PushContent()
    {
        if (_pendingHtml == null || !_host.IsReady || _webFailed) return;
        var script = "window.tn && window.tn.setContent(" + JsonSerializer.Serialize(_pendingHtml) + ");"
                     + "document.getElementById('editor').contentEditable = "
                     + (_readOnly ? "false" : "true") + ";";
        _ = _host.ExecuteScriptAsync(script);
    }

    private void OnWebMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();
            if (type == "ready") { PushContent(); return; }
            if (type != "changed" || !root.TryGetProperty("html", out var htmlElement)) return;
            var html = htmlElement.GetString() ?? "";
            if (_readOnly) return;
            _lastHtml = html;

            _saveDelay?.Cancel();
            _saveDelay?.Dispose();
            _saveDelay = new CancellationTokenSource();
            var token = _saveDelay.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(500, token).ConfigureAwait(false);
                    await Dispatcher.UIThread.InvokeAsync(() => SaveHtml(html));
                }
                catch (OperationCanceledException) { }
            }, token);
        }
        catch (JsonException) { }
    }

    private void SaveHtml(string html)
    {
        if (_readOnly || _currentId == Guid.Empty) return;
        var note = _session.Notes.Find(_currentId);
        if (note == null || note.IsTrashed) return;

        var parsed = RichTextHtmlCodec.Decode(html);

        // W-10 ③：写盘前把所有内联 / 本地图片转存为 attachments 相对路径；
        // 任一图片无法转存就整篇拒写并报错，绝不把 blob:/data:/file:/// 写进云端 JSON。
        foreach (var run in parsed.AllRuns())
        {
            if (run.Image == null) continue;
            var result = InlineImageImport.Import(run.Image.Path, _currentId, _session.Notes.Attachments);
            if (result.Kind == InlineImageImport.Outcome.Failed)
            {
                StatusChanged?.Invoke("图片无法保存，笔记未修改：" + result.Error);
                return;
            }
            if (result.Kind == InlineImageImport.Outcome.Imported)
                run.Image.Path = result.Path;
        }

        var onlyEmpty = parsed.PlainText.Trim().Length == 0
                        && parsed.Blocks.All(block => block.Runs.All(run => run.Image == null));
        var normalized = onlyEmpty ? "" : RichTextHtmlCodec.Encode(parsed);
        if (note.ContentFormat == NoteContentFormat.richText && note.Body == normalized) return;

        // W-09：旧 Markdown 笔记仅打开（切换 / 失焦）不得改写原稿、升级格式或动 updatedAt。
        // 内容未偏离加载基线就只更新内存，绝不落盘。
        if (note.ContentFormat == NoteContentFormat.markdown)
        {
            var current = RichTextHtmlCodec.Encode(RichTextHtmlCodec.Decode(html));
            if (current == _markdownBaseline) return;
        }

        _currentBody = normalized;
        _currentFormat = NoteContentFormat.richText;
        _session.Notes.UpdateContent(_currentId, normalized, NoteContentFormat.richText);
    }

    private void SaveFallback()
    {
        if (_loadingFallback || !_webFailed || _readOnly || _currentId == Guid.Empty) return;
        _status.Text = "纯文本模式（原 HTML 未改动）";
    }

    private void Run(string method, params string[] arguments)
    {
        if (_readOnly || !_host.IsReady || _webFailed) return;
        var args = string.Join(",", arguments.Select(value => JsonSerializer.Serialize(value)));
        _ = _host.ExecuteScriptAsync("window.tn && window.tn." + method + "(" + args + ");");
    }

    private async void InsertImage()
    {
        if (_readOnly || _currentId == Guid.Empty) return;
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
            var source = files.FirstOrDefault()?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(source)) return;
            var relative = _session.Notes.Attachments.ImportImage(source, _currentId);
            if (!_host.IsReady)
            {
                StatusChanged?.Invoke("图片已导入附件目录：" + relative);
                return;
            }
            Run("insertImage", relative);
            StatusChanged?.Invoke("已插入图片，保存后会随笔记一起同步到网盘。");
        }
        catch (Exception error)
        {
            StatusChanged?.Invoke("插入图片失败：" + error.Message);
        }
    }

    /// <summary>切换笔记 / 关窗前把最后一次收到的内容立刻落盘。</summary>
    public void Flush()
    {
        if (_lastHtml == null || _readOnly) return;
        var html = _lastHtml;
        _lastHtml = null;
        _saveDelay?.Cancel();
        SaveHtml(html);
    }
}
