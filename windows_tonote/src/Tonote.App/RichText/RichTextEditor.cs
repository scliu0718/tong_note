using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
        ("默认", "#1D1D1F"), ("红", "#E5484D"), ("橙", "#F59E42"), ("金", "#E8A317"),
        ("绿", "#35A45A"), ("蓝", "#2E7CF6"), ("紫", "#7A5AF8"), ("粉", "#C86DD7"),
        ("灰", "#86868B"),
    ];

    private static readonly (string Name, string Hex)[] BackgroundColors =
    [
        ("无", "#FFFFFF"), ("淡灰", "#F2F2F4"), ("淡黄", "#FFF3D6"), ("淡绿", "#E6F4EC"),
        ("淡蓝", "#E5F5FF"), ("淡红", "#FCEBEB"), ("淡紫", "#F0EAFE"), ("淡橙", "#FBE4D6"),
        ("浅棕", "#F5E6D3"),
    ];

    private readonly AppSession _session;
    private readonly WebView2Host _host;
    private readonly TextBox _fallback;
    private readonly WrapPanel _toolbar = new()
    {
        Orientation = Orientation.Horizontal,
        Margin = new Thickness(Metrics.ToolbarPaddingH, Metrics.ToolbarPaddingV),
    };
    // WebView2 是 NativeControlHost 创建的原生子 HWND，Z 序永远在 Avalonia 绘制内容之上。
    // 普通 Border / Popup(OverlayLayer) 都会被它盖住，工具条弹窗只能用独立顶层 Window。
    private ToolbarPopover? _popover;
    // 浮窗改为 NOACTIVATE 后不再有 Deactivated 事件，点外部关闭改由主窗口指针按下监听。
    private Window? _popoverOwner;
    private bool _popoverOutsideHooked;
    private readonly TextBlock _status = new()
    {
        FontSize = 11,
        Foreground = Palette.TextTertiary,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(8, 0, 0, 0),
    };
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
    /// <summary>W-31：光标处真实字号（默认 14），不再硬编码勾选 15。</summary>
    private string _currentFontSize = "14";
    private Control? _trailing;
    private CancellationTokenSource? _saveDelay;

    public event Action<string>? StatusChanged;
    /// <summary>W-20：编辑器内 F5 触发的同步请求。</summary>
    public event Action? SyncRequested;
    /// <summary>W-31：编辑器内字数实时回传，供列表页顶栏显示。</summary>
    public event Action<int>? WordCountChanged;

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
            RowDefinitions = new RowDefinitions("Auto,*"),
            Children = { _toolbar, surface },
            Background = Palette.Panel,
        };
        Grid.SetRow(surface, 1);
        Content = page;

        _host.Navigate(new Uri(htmlPath).AbsoluteUri);
    }

    public bool IsWebViewReady => _host.IsReady;
    public Guid CurrentNoteId => _currentId;

    // ── 工具条（三端 1:1，顺序与 Mac EditorView.swift:369-505 一致） ──────
    // T⌄ | 加粗斜体下划线删除线 | 字号⌄ A⌄ 🖌⌄ | 引用 代码 | 链接 图片 表格
    private void BuildToolbar()
    {
        // 文本样式：衬线粗体 T + 下拉箭头（箭头样式/大小/间距/居中由 WithChevron 统一，
        // 与字号、A、背景色四个按钮完全一致，对齐 Mac TextStyleMenu）
        var tGlyph = new TextBlock
        {
            Text = "T",
            FontFamily = new FontFamily("Georgia, 'Times New Roman', serif"),
            FontSize = 16,
            FontWeight = FontWeight.Bold,
            Foreground = Palette.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var styleGlyph = WithChevron(tGlyph, gap: 7, yOffset: 2);
        AddComposite(styleGlyph, "文本样式", () => TogglePopover("style", TextStylePopover()));
        _toolbar.Children.Add(Ui.VDivider());

        // B/I/U/S：Mac 用「字形本身就是样式」的衬线字母（B 加粗、I 斜体、U 带下划线、S 带删除线），
        // 比抽象图标更直观。四个按钮统一用同一衬线体，hover 由 Icons.Tint 只改前景色、不破坏装饰。
        AddStyledLetter("B", "加粗", () => Run("exec", "bold"),
            weight: FontWeight.Bold);
        AddStyledLetter("I", "斜体", () => Run("exec", "italic"),
            style: FontStyle.Italic);
        AddStyledLetter("U", "下划线", () => Run("exec", "underline"),
            decorations: TextDecorations.Underline);
        AddStyledLetter("S", "删除线", () => Run("exec", "strikeThrough"),
            decorations: TextDecorations.Strikethrough);
        _toolbar.Children.Add(Ui.VDivider());

        // 字号 / 文字颜色 / 背景颜色：右侧箭头统一用 WithChevron（与 T 同款矢量细线 chevron，
        // 大小、描边、间距、垂直位置完全一致）；字号只保留右侧单个箭头。
        AddComposite(WithChevron(Ui.Text("字号", 12, null, Palette.TextSecondary)),
                     "字号", () => TogglePopover("size", FontSizePopover()));
        // Mac 的 A 固定 0xE8A317，不随 hover 变绿
        AddComposite(WithChevron(Ui.Text("A", 16, FontWeight.Bold, Palette.Gold)),
                     "文字颜色", () => TogglePopover("fore", ColorPopover(false)), recolorOnHover: false);
        AddComposite(WithChevron(Ui.Icon(Glyphs.Highlight, 16, Palette.TextSecondary)),
                     "背景颜色", () => TogglePopover("back", ColorPopover(true)));
        _toolbar.Children.Add(Ui.VDivider());

        Add(Glyphs.Quote, "引用", () => Run("block", "blockquote"));
        Add(Glyphs.Code, "代码块", () => Run("block", "pre"));
        _toolbar.Children.Add(Ui.VDivider());

        // 链接/图片/表格是填充图标、笔划面积大，单独调浅一档到 TextTertiary，与标题区图标一致。
        Add(Glyphs.Link, "插入链接", () => TogglePopover("link", LinkPopover()), normalColor: Palette.TextSecondary);
        Add(Glyphs.Image, "插入图片", InsertImage, normalColor: Palette.TextSecondary);
        Add(Glyphs.Table, "插入表格", () => TogglePopover("table", TablePopover()), normalColor: Palette.TextSecondary);
        // W-31：列表入口只在「文本样式」浮层；单元格对齐仅在光标进入表格时出现（Mac TableAlignmentButton）
        Add(Glyphs.Align, "单元格对齐", () => TogglePopover("align", AlignPopover()));
        _toolButtons["单元格对齐"].IsVisible = false;
        _toolbar.Children.Add(Ui.VDivider());
        // 删除入口移到标题区右上角（ShellView 的图标按钮），工具条不再放删除图标。
        _toolbar.Children.Add(_status);
    }

    private static StackPanel Composite(params Control[] parts)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        foreach (var part in parts) panel.Children.Add(part);
        return panel;
    }

    /// <summary>
    /// 主体 + 右侧下拉箭头（T / 字号 / A / 背景色统一样式）。
    /// 箭头是矢量细线 chevron（size 7、描边 0.85、TextTertiary），不再用文字字符箭头；
    /// Grid 两列把主体与箭头放进同一行高内垂直居中，yOffset 做视觉微调。
    /// </summary>
    private static Grid WithChevron(Control main, double gap = 7, double yOffset = 2)
    {
        var chevron = Ui.Icon(Glyphs.ChevronDown, 7, Palette.TextTertiary);
        chevron.Margin = new Thickness(gap, 0, 0, 0);
        chevron.RenderTransform = new TranslateTransform(0, yOffset);
        chevron.VerticalAlignment = VerticalAlignment.Center;
        main.VerticalAlignment = VerticalAlignment.Center;
        if (chevron is Avalonia.Controls.Shapes.Path path)
            path.StrokeThickness = 0.85; // 矢量 chevron 默认描边 1.7，细一半（对齐 Mac 纤细下箭头）
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto"),
            Children = { main, chevron },
        };
        Grid.SetColumn(main, 0);
        Grid.SetColumn(chevron, 1);
        return grid;
    }

    private readonly Dictionary<string, Button> _toolButtons = new();

    private void Add(string glyph, string tip, Action click, bool wide = false, IBrush? normalColor = null)
    {
        var button = Ui.Tool(glyph, tip, click, wide, normalColor: normalColor);
        _toolButtons[tip] = button;
        _toolbar.Children.Add(button);
    }

    /// <summary>
    /// 衬线字母字形按钮（对齐 Mac 的 B/I/U/S）：字形本身带加粗/斜体/下划线/删除线装饰，
    /// 直观表达样式而非抽象图标。hover 只改前景色，装饰由 Icons.Tint 保留。
    /// </summary>
    private void AddStyledLetter(string letter, string tip, Action click,
        FontWeight? weight = null, FontStyle style = FontStyle.Normal,
        TextDecorationCollection? decorations = null)
    {
        var glyph = new TextBlock
        {
            Text = letter,
            FontFamily = new FontFamily("Georgia, 'Times New Roman', serif"),
            FontSize = 16,
            FontWeight = weight ?? FontWeight.Normal,
            FontStyle = style,
            TextDecorations = decorations,
            Foreground = Palette.TextSecondary,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var button = Ui.ToolComposite(glyph, tip, click, wide: false);
        _toolButtons[tip] = button;
        _toolbar.Children.Add(button);
    }

    private void AddComposite(Control content, string tip, Action click, bool recolorOnHover = true)
    {
        var button = Ui.ToolComposite(content, tip, click, recolorOnHover: recolorOnHover);
        _toolButtons[tip] = button;
        _toolbar.Children.Add(button);
    }

    private void TogglePopover(string key, Control content)
    {
        if (_openPopover == key)
        {
            ClosePopover();
            return;
        }
        ClosePopover(restoreFocus: false); // 马上要开新浮窗，别把焦点抢回编辑区
        _openPopover = key;
        // 锚点用被点击的按钮本身，浮窗才会从按钮处弹出，而不是固定在编辑器左缘。
        var anchor = _toolButtons.TryGetValue(TipForPopover(key), out var btn) ? (Control)btn : this;
        // 靠右的入口（链接/表格/对齐）浮窗右缘对齐按钮右缘、向左展开，避免超出应用右边界；
        // 其余入口仍左缘对齐按钮。
        var alignRight = key is "link" or "table" or "align";
        var popover = new ToolbarPopover(content, WidthFor(key), anchor, alignRight);
        _popover = popover;
        popover.Closed += (_, _) =>
        {
            if (ReferenceEquals(_popover, popover)) { _popover = null; _openPopover = ""; }
        };
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner != null)
        {
            _popoverOwner = owner;
            HookPopoverOutsideClick(owner);
            popover.Show(owner);
        }
        else popover.Show();
        popover.DisableActivation();
    }

    /// <summary>
    /// 浮窗 NOACTIVATE 后不会失焦、也就没有 Deactivated 事件可用来「点外部关闭」。
    /// 改在主窗口上挂一次 Tunnel 指针监听：按下的目标不在浮窗窗口内就关闭浮窗。
    /// 用 Tunnel 是为了先于按钮自身点击处理，保证「点编辑区一下」既关浮窗又落光标。
    /// </summary>
    private void HookPopoverOutsideClick(Window owner)
    {
        if (_popoverOutsideHooked) return;
        _popoverOutsideHooked = true;
        owner.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (_popover == null) return;
            var source = e.Source as Visual;
            // 点在浮窗自己窗口内不算外部（浮窗是独立 TopLevel，其指针事件不会冒泡到主窗口）
            var top = TopLevel.GetTopLevel(source);
            if (top != null && ReferenceEquals(top, _popover)) return;
            // 点在工具条按钮上交给按钮自己的 Click 处理（否则再点同一按钮会变成「先关后开」）
            if (IsDescendantOf(source, _toolbar)) return;
            ClosePopover(restoreFocus: false);
        }, RoutingStrategies.Tunnel);
    }

    private static bool IsDescendantOf(Visual? node, Visual ancestor)
    {
        while (node != null)
        {
            if (ReferenceEquals(node, ancestor)) return true;
            node = node.GetVisualParent();
        }
        return false;
    }

    /// <summary>
    /// 浮窗全程 NOACTIVATE、不夺前台，主窗口始终保持激活，无需再 Activate()（那正是「闪一下」的来源）。
    /// 只需把光标放回编辑区，保证选完项后可直接继续输入。
    /// </summary>
    private void RestoreEditorFocus()
    {
        if (_webFailed || _readOnly) return;
        if (_host.IsReady)
            _ = _host.ExecuteScriptAsync("document.getElementById('editor')?.focus();");
        else
            _fallback.Focus();
    }

    private void ClosePopover(bool restoreFocus = true)
    {
        _openPopover = "";
        var popover = _popover;
        _popover = null;
        popover?.Close();
        if (restoreFocus) RestoreEditorFocus();
    }

    private static double WidthFor(string key) => key switch
    {
        "style" => Metrics.PopTextStyle,
        "fore" or "back" => Metrics.PopColor,
        "table" => Metrics.PopTableGrid,
        "align" => Metrics.PopAlign,
        _ => 260,
    };

    /// <summary>浮窗 key → 对应工具条按钮的 tip，用于把浮窗定位到被点击的按钮下方。</summary>
    private static string TipForPopover(string key) => key switch
    {
        "style" => "文本样式",
        "size" => "字号",
        "fore" => "文字颜色",
        "back" => "背景颜色",
        "link" => "插入链接",
        "table" => "插入表格",
        "align" => "单元格对齐",
        _ => "",
    };

    private static Button MenuOption(string text, Action click, bool active = false, bool bold = false)
    {
        var normal = active ? Palette.Pill : Brushes.Transparent;
        var button = new Button
        {
            Content = Ui.Text(text, 12.5, active ? FontWeight.SemiBold : (bold ? FontWeight.Bold : null),
                active ? Palette.AccentHover : Palette.TextPrimary),
            Background = normal,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(10, 7),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        // 指针悬停显示淡绿底色（与工具条按钮一致），离开恢复
        button.PointerEntered += (_, _) => button.Background = Palette.AccentTint;
        button.PointerExited += (_, _) => button.Background = normal;
        button.Click += (_, _) => click();
        return button;
    }

    private Control TextStylePopover()
    {
        var stack = new StackPanel { Spacing = 2 };
        foreach (var (label, tag) in new[]
        {
            ("正文", "p"), ("一级标题", "h1"), ("二级标题", "h2"),
            ("三级标题", "h3"), ("四级标题", "h4"),
            ("", "divider"), // 标题组与列表组之间的分隔线
            ("有序列表", "ol"), ("无序列表", "ul"),
        })
        {
            var value = tag;
            if (value == "divider")
            {
                stack.Children.Add(Ui.Divider(Palette.LineSoft, marginV: 5));
                continue;
            }
            // 标题项文字加粗（正文/列表保持常规），与 Mac 文本样式菜单一致
            stack.Children.Add(MenuOption(label, () =>
            {
                if (value == "ol") Run("exec", "insertOrderedList");
                else if (value == "ul") Run("exec", "insertUnorderedList");
                else Run("block", value);
                ClosePopover();
            }, bold: value is "h1" or "h2" or "h3" or "h4"));
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
                Content = Ui.Text(value, 12, value == _currentFontSize ? FontWeight.Bold : null,
                    value == _currentFontSize ? Palette.TableAccent : Palette.TextPrimary),
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
        var custom = new TextBox { Watermark = "#RRGGBB", FontSize = 11, Padding = new Thickness(6, 5) };
        var apply = Ui.Primary("自定义", () =>
        {
            var value = (custom.Text ?? "").Trim();
            if (!value.StartsWith('#')) value = "#" + value;
            if (!System.Text.RegularExpressions.Regex.IsMatch(value, @"^#[0-9A-Fa-f]{6}$"))
            {
                _ = Dialogs.ReportFailure(this, "无法应用颜色", "请输入 #RRGGBB 格式的色值，例如 #1A9E5C。", "editor");
                return;
            }
            Run("exec", background ? "hiliteColor" : "foreColor", value);
            ClosePopover();
        }, 11);
        var customRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { BorderedInput(custom, 96), apply } };
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

    /// <summary>W-18：只放行 http / https / mailto，拒绝 javascript: 等协议。</summary>
    internal static bool IsSafeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return false;
        var scheme = uri.Scheme.ToLowerInvariant();
        return scheme is "http" or "https" or "mailto";
    }

    /// <summary>全局 TextBox 样式把边框压成 0，浮窗输入框用外包 Border 画出可见边框。</summary>
    private static Border BorderedInput(TextBox input, double width) => new()
    {
        Width = width,
        Height = 32,
        // 固定宽度控件在 Stretch 的 StackPanel 里会被居中，导致第二行输入框缩进；
        // 统一左对齐，和「链接地址」标签、第一行输入框的左缘一致。
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Center,
        Background = Palette.Panel,
        BorderBrush = Palette.Line,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(5),
        Child = input,
    };

    private Control LinkPopover()
    {
        // 每次打开都新建输入框：浮窗关闭后控件仍带 visual parent，
        // 若复用字段级 TextBox，第二次 Add 到新面板会抛 "already has a visual parent" 崩溃。
        var urlInput = new TextBox { Text = "https://", Watermark = "https://…", FontSize = 12,
            Padding = new Thickness(6, 5), MinHeight = 0, VerticalAlignment = VerticalAlignment.Center };
        var textInput = new TextBox { Watermark = "显示文字（可留空）", FontSize = 12,
            Padding = new Thickness(6, 5), MinHeight = 0, VerticalAlignment = VerticalAlignment.Center };
        // 两行网格：左列输入框，右列按钮；「取消」与第二个输入框同一行、位于其右侧。
        var insert = Ui.Primary("插入", () =>
        {
            var url = (urlInput.Text ?? "").Trim();
            if (!IsSafeUrl(url))
            {
                _ = Dialogs.ReportFailure(this, "无法插入链接",
                    "仅支持 http、https、mailto 协议，请输入有效的链接地址。", "editor");
                return;
            }
            Run("insertLink", url, (textInput.Text ?? "").Trim());
            ClosePopover();
        }, 11);
        insert.Width = 52;
        insert.Margin = new Thickness(6, 0, 0, 0);
        var cancel = Ui.Ghost("取消", () => ClosePopover(), 11);
        cancel.Width = 52;
        cancel.Margin = new Thickness(6, 0, 0, 0);

        var urlRow = new Grid { ColumnDefinitions = new ColumnDefinitions("170,Auto") };
        var urlBox = BorderedInput(urlInput, 170);
        urlBox.VerticalAlignment = VerticalAlignment.Top;
        urlRow.Children.Add(urlBox);
        Grid.SetColumn(insert, 1);
        urlRow.Children.Add(insert);

        var textRow = new Grid { ColumnDefinitions = new ColumnDefinitions("170,Auto") };
        var textBox = BorderedInput(textInput, 170);
        textBox.VerticalAlignment = VerticalAlignment.Top;
        textRow.Children.Add(textBox);
        Grid.SetColumn(cancel, 1);
        textRow.Children.Add(cancel);

        return new StackPanel
        {
            Spacing = 6,
            Children =
            {
                Ui.Text("链接地址", 11, null, Palette.TextTertiary),
                urlRow,
                textRow,
            },
        };
    }

    private Control TablePopover()
    {
        var grid = new Grid();
        for (var i = 0; i < 8; i++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        for (var i = 0; i < 6; i++) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        // 对齐 Mac TableInsertButton：hover (row,col) 时，从 (1,1) 到该格的矩形全部高亮，
        // 直观表示「将插入几行几列」。绿边框 Accent + 浅绿填充 AccentTint。
        var cells = new List<(Button button, int row, int col)>();
        void Highlight(int hoverRow, int hoverCol)
        {
            foreach (var (button, row, col) in cells)
            {
                var selected = row <= hoverRow && col <= hoverCol;
                button.Background = selected ? Palette.AccentTint : Palette.Panel;
                button.BorderBrush = selected ? Palette.Accent : Palette.Line;
            }
        }

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
            cell.PointerEntered += (_, _) => Highlight(rows, cols);
            cell.Click += (_, _) => { Run("insertTable", rows.ToString(), cols.ToString()); ClosePopover(); };
            cells.Add((cell, rows, cols));
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
            if (type == "editorFocus") { if (_popover != null) ClosePopover(restoreFocus: false); return; }
            if (type == "openExternal")
            {
                var url = Field(root, "url");
                if (IsSafeUrl(url))
                {
                    try { BrowserLauncher.Open(url!); }
                    catch (Exception error)
                    {
                        _ = Dialogs.ReportFailure(this, "打开链接失败", error.Message, "editor");
                    }
                }
                return;
            }
            if (type == "failure")
            {
                _ = Dialogs.ReportFailure(this, Field(root, "title") ?? "操作失败",
                    Field(root, "reason") ?? "未知原因", Field(root, "category") ?? "editor");
                return;
            }
            if (type == "log")
            {
                DiagnosticLog.Write(Field(root, "category") ?? "editor", Field(root, "message") ?? "");
                return;
            }
            if (type == "toolbarState" && root.TryGetProperty("state", out var state))
            {
                MarkActive("加粗", state, "bold");
                MarkActive("斜体", state, "italic");
                MarkActive("下划线", state, "underline");
                MarkActive("删除线", state, "strikeThrough");
                var size = Field(state, "fontSize");
                if (!string.IsNullOrEmpty(size)) _currentFontSize = size!;
                // Mac TableAlignmentButton：仅光标进入表格时出现，否则隐藏（而非置灰占位）
                if (state.TryGetProperty("inTable", out var inTable)
                    && _toolButtons.TryGetValue("单元格对齐", out var alignButton))
                {
                    var inTbl = inTable.ValueKind == JsonValueKind.True;
                    alignButton.IsVisible = inTbl;
                    alignButton.IsEnabled = inTbl;
                }
                return;
            }
            if (type == "wordCount" && root.TryGetProperty("count", out var countElement))
            {
                WordCountChanged?.Invoke(countElement.GetInt32());
                return;
            }
            if (type == "command")
            {
                switch (Field(root, "name"))
                {
                    case "insertLink": TogglePopover("link", LinkPopover()); break;
                    case "save": Flush(); break;
                    case "escape": ClosePopover(); break;
                    case "sync": SyncRequested?.Invoke(); break;
                    case "pastePlain": _ = PastePlainAsync(); break;
                }
                return;
            }
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

    private static string? Field(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) ? value.GetString() : null;

    private void MarkActive(string tip, JsonElement state, string command)
    {
        if (!_toolButtons.TryGetValue(tip, out var button)) return;
        var active = state.TryGetProperty(command, out var value) && value.ValueKind == JsonValueKind.True;
        button.Background = active ? Palette.AccentTint : Brushes.Transparent;
        button.Foreground = active ? Palette.AccentHover : Palette.TextSecondary;
    }

    /// <summary>W-20：Ctrl+Shift+V 纯文本粘贴，由 C# 剪贴板取文本再交给编辑器。</summary>
    private async Task PastePlainAsync()
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            var text = clipboard == null ? null : await clipboard.GetTextAsync();
            if (!string.IsNullOrEmpty(text)) Run("exec", "insertText", text);
        }
        catch (Exception error)
        {
            _ = Dialogs.ReportFailure(this, "操作失败", "读取剪贴板失败：" + error.Message, "editor");
        }
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
                _ = Dialogs.ReportFailure(this, "操作失败",
                    "图片无法保存，笔记未修改：" + result.Error, "editor");
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
        if (_readOnly) return;
        if (!_host.IsReady || _webFailed)
        {
            _ = Dialogs.ReportFailure(this, "操作失败", "编辑器尚未就绪，请稍后再试。", "editor");
            return;
        }
        var args = string.Join(",", arguments.Select(value => JsonSerializer.Serialize(value)));
        _ = _host.ExecuteScriptAsync("window.tn && window.tn." + method + "(" + args + ");");
    }

    private async void InsertImage()
    {
        if (_readOnly || _currentId == Guid.Empty) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider == null)
        {
            _ = Dialogs.ReportFailure(this, "操作失败", "当前窗口不支持选择本地文件。", "editor");
            return;
        }
        try
        {
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择一张图片",
                AllowMultiple = false,
                FileTypeFilter = [FilePickerFileTypes.ImageAll],
            });
            var source = files.FirstOrDefault()?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(source))
            {
                // 用户在文件选择器里点「取消」属于正常操作，静默返回，不弹「操作失败」。
                return;
            }
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
            _ = Dialogs.ReportFailure(this, "操作失败", "插入图片失败：" + error.Message, "editor");
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

/// <summary>
/// 工具条下拉浮窗：独立无边框顶层窗口。
/// WebView2 是原生子 HWND，会盖住同一窗口内的一切 Avalonia 绘制（含 Popup/OverlayLayer），
/// 只有另一个顶层 Window 才能稳定浮在编辑区之上。
/// 关键：加 WS_EX_NOACTIVATE，浮窗全程不夺取 Windows 前台焦点——显隐都不触发应用失焦/闪烁。
/// 由此带来的「点外部关闭」由主窗口指针监听 + 编辑区 focus 消息补足（在 RichTextEditor 中）。
/// </summary>
internal sealed class ToolbarPopover : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private readonly Control? _anchor;

    public ToolbarPopover(Control content, double width, Control anchor, bool alignRight = false)
    {
        _anchor = anchor;
        Title = string.Empty;
        SystemDecorations = SystemDecorations.None;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent, WindowTransparencyLevel.AcrylicBlur };

        Content = new Border
        {
            Margin = new Thickness(8), // 留出阴影空间
            Child = Ui.Popover(content, width),
        };

        // 定位：浮窗左边缘对齐被点击按钮左边缘，顶部在按钮下方。
        // 这样每个下拉都从自己的入口弹出，而非统一贴到编辑器左缘。
        // PointToScreen / Position 都是物理像素，无需再乘 RenderScaling。
        var topLevel = TopLevel.GetTopLevel(anchor);
        var scale = topLevel?.RenderScaling ?? 1;
        var topLeft = anchor.PointToScreen(new Point(0, 0));
        var bottomLeft = anchor.PointToScreen(new Point(0, anchor.Bounds.Height));
        var shadowPx = (int)Math.Round(8 * scale);          // Border 阴影留白
        var popupWpx = (int)Math.Round((width + 16) * scale); // 浮窗宽 + 左右留白
        // 右对齐入口：面板右缘贴齐按钮右缘、向左展开，避免宽浮窗伸出应用右边界；
        // 左对齐入口：面板左缘贴齐按钮左缘（原行为）。
        var x = alignRight
            ? topLeft.X + (int)Math.Round(anchor.Bounds.Width * scale) + shadowPx - popupWpx
            : topLeft.X - shadowPx;
        var y = bottomLeft.Y + (int)Math.Round(2 * scale);   // 按钮下方留 2px
        // 右边界保护：超出屏幕则左移，至少贴左屏边
        var screens = topLevel?.Screens;
        var screen = screens?.ScreenFromPoint(bottomLeft);
        if (screen != null)
        {
            var area = screen.WorkingArea;
            if (x + popupWpx > area.Right) x = area.Right - popupWpx;
            if (x < area.X) x = area.X;
        }
        Position = new PixelPoint(x, y);
    }

    /// <summary>Show() 之后调用：加上 NOACTIVATE/TOOLWINDOW，浮窗永不成为前台窗口。</summary>
    public void DisableActivation()
    {
        var handle = TryGetPlatformHandle();
        if (handle == null || handle.Handle == IntPtr.Zero) return;
        var style = GetWindowLong(handle.Handle, GWL_EXSTYLE);
        SetWindowLong(handle.Handle, GWL_EXSTYLE, style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    protected override void OnClosed(EventArgs e)
    {
        _anchor?.Focus();
        base.OnClosed(e);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLongPtr32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLongPtr32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private static int GetWindowLong(IntPtr hWnd, int nIndex)
        => IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex).ToInt32() : GetWindowLongPtr32(hWnd, nIndex);

    private static void SetWindowLong(IntPtr hWnd, int nIndex, int value)
    {
        if (IntPtr.Size == 8) SetWindowLongPtr64(hWnd, nIndex, new IntPtr(value));
        else SetWindowLongPtr32(hWnd, nIndex, value);
    }
}
