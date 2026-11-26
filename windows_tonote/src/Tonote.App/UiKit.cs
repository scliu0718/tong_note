using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Tonote.Core;

namespace Tonote.App;

/// <summary>
/// 图标语义键。值是唯一的字符串标记（不再直接作为显示字符），
/// 由 Icons.cs 统一映射到线性矢量路径；少数工具条按钮仍以字母/数字显示。
/// </summary>
public static class Glyphs
{
    public const string Plus = "＋";
    public const string Minus = "−";
    public const string Close = "✕";
    public const string Check = "✓";
    // 标签输入框专用：用户提供的带圆环对勾 / 叉号填充图标（独立键，不改通用 Check/Close）
    public const string TagCheck = "tag_check";
    public const string TagClear = "tag_clear";
    public const string ChevronLeft = "‹";
    public const string ChevronRight = "›";
    public const string ChevronDown = "⌄";
    public const string ChevronUp = "⌃";
    public const string More = "⋯";

    public const string Gear = "⚙";
    public const string Sync = "⟳";
    public const string Trash = "⌫";
    public const string Pin = "◆";
    public const string Book = "▤";
    // 注意：Icons 字典以字符串值为键，常量值必须互不相同（Folder 曾与 Image 都是 ▧，Tray 曾与 Table 都是 ▦，互相覆盖）
    public const string Folder = "▨";
    public const string Tray = "▥";
    public const string Tag = "🔖";
    // 笔记本按名称映射的专属图标（对应 Mac notebookIcon 的语义）
    public const string Pencil = "✏";
    public const string BookClosed = "📕";
    public const string Chart = "📈";
    public const string Bulb = "💡";
    public const string Note = "✎";
    public const string Cloud = "☁";
    public const string Clock = "◷";
    public const string Search = "⌕";
    public const string Calendar = "▩";
    // 截止时间行日历按钮：用户提供的 iconfont 填充路径（日历+对勾），1024 viewBox
    public const string CalendarFilled = "calendar_filled";
    public const string StateTerminated = "state_terminated";
    public const string StateTodo = "state_todo";
    public const string Chain = "chain";
    public const string StateDone = "state_done";
    public const string StateDoing = "state_doing";

    public const string Table = "▦";
    public const string Quote = "❝";
    public const string Code = "‹ ›";
    public const string Link = "🔗";
    public const string Image = "▧";
    public const string Highlight = "◩";
    public const string Align = "☰";
    public const string AlignLeft = "◧";
    public const string AlignCenter = "◫";
    public const string AlignRight = "◨";

    public const string Bold = "B";
    public const string Italic = "I";
    public const string Underline = "U";
    public const string Strike = "S";
    public const string TextStyle = "T";
}

/// <summary>
/// Mac 设计稿组件的 Windows 实现：按钮 / 胶囊 / 角标 / 状态圈 / 卡片 / 浮层 / 分割线。
/// 统一走 Palette + Metrics + Icons，避免 ShellView 里再出现散落的色值与字符图标。
/// </summary>
public static class Ui
{
    // ── 文本 ──────────────────────────────────────────────────────────
    public static TextBlock Text(string value, double size = Metrics.FontSidebarWide,
        FontWeight? weight = null, IBrush? color = null) => new()
    {
        Text = value,
        FontSize = size,
        FontWeight = weight ?? FontWeight.Normal,
        Foreground = color ?? Palette.TextPrimary,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>线性矢量图标；无对应路径时回退成文字（B / I / U / 字号数字）。</summary>
    public static Control Icon(string glyph, double size = 14, IBrush? color = null)
        => Icons.Render(glyph, size, color ?? Palette.TextSecondary);

    /// <summary>1pt 发丝线。Mac 特意要求 ≥1pt，0.5pt 会被抗锯齿抹掉。</summary>
    public static Border Hairline() => new()
    {
        Height = 1,
        Background = Palette.Hairline,
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };

    public static Border Divider(IBrush? brush = null, double thickness = 1,
        double marginV = 8) => new()
    {
        Height = thickness,
        Background = brush ?? Palette.Line,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Margin = new Thickness(0, marginV),
    };

    public static Border VDivider(double height = 18) => new()
    {
        Width = 1,
        Height = height,
        Background = Palette.LineSoft,
        Margin = new Thickness(6, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static TextBlock Section(string text, double size = 10.5) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = FontWeight.SemiBold,
        Foreground = Palette.TextQuaternary,
        Margin = new Thickness(4, 10, 0, 2),
    };

    public static TextBlock Title(string text, double size = Metrics.FontTitle) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = FontWeight.Bold,
        Foreground = Palette.TextPrimary,
    };

    /// <summary>圆形渐变头像徽标，与 Mac AvatarBadge 1:1（渐变 + 白色字形居中）。</summary>
    public static Border AvatarBadge(AvatarPreset preset, double size)
    {
        var glyph = new TextBlock
        {
            Text = preset.Glyph,
            FontSize = size * 0.42,
            FontWeight = FontWeight.Bold,
            Foreground = Palette.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        };
        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Background = Palette.AvatarGradient(preset.Color1, preset.Color2),
            Child = glyph,
        };
    }

    // ── 按钮 ──────────────────────────────────────────────────────────
    public static Button Primary(string text, Action click, double size = 12, string? icon = null) =>
        Styled(text, click, Palette.Accent, Palette.White, null, size, icon);

    public static Button Ghost(string text, Action click, double size = 12) =>
        Styled(text, click, Palette.Panel, Palette.TextSecondary, Palette.Line, size);

    public static Button Danger(string text, Action click, double size = 12, string? icon = null) =>
        Styled(text, click, Palette.Panel, Palette.Red, Palette.DangerBorder, size, icon);

    public static Button DangerFill(string text, Action click, double size = 12) =>
        Styled(text, click, Palette.Red, Palette.White, null, size);

    private static Button Styled(string text, Action click, IBrush background, IBrush foreground,
        IBrush? border, double size, string? icon = null)
    {
        Control content = string.IsNullOrEmpty(icon)
            ? Text(text, size, FontWeight.SemiBold, foreground)
            : new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children = { Icon(icon, size, foreground), Text(text, size, FontWeight.SemiBold, foreground) },
            };
        var button = new Button
        {
            Content = content,
            FontSize = size,
            FontWeight = FontWeight.SemiBold,
            Foreground = foreground,
            Background = background,
            BorderBrush = border ?? Brushes.Transparent,
            BorderThickness = new Thickness(border == null ? 0 : 1),
            CornerRadius = new CornerRadius(Metrics.RadiusButton),
            Padding = new Thickness(11, 6),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>编辑器工具条按钮：30×30、hover 淡绿底绿字、深色气泡提示、0.12s 过渡。</summary>
    public static Button Tool(string glyph, string tip, Action click, bool wide = false,
        double fontSize = 13, IBrush? normalColor = null)
    {
        var normal = normalColor ?? Palette.TextSecondary;
        var hover = Palette.Accent;
        var vector = Icons.Vector(glyph, 16, normal);
        Control content = vector ?? Text(glyph, fontSize, FontWeight.SemiBold, normal);
        content.HorizontalAlignment = HorizontalAlignment.Center;
        content.VerticalAlignment = VerticalAlignment.Center;

        var button = new Button
        {
            Content = content,
            FontSize = fontSize,
            FontWeight = FontWeight.SemiBold,
            Width = wide ? double.NaN : Metrics.ToolButtonSize,
            MinWidth = wide ? 34 : Metrics.ToolButtonSize,
            Height = Metrics.ToolButtonSize,
            Padding = wide ? new Thickness(7, 0) : new Thickness(0),
            Margin = new Thickness(1, 0),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Foreground = normal,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        button.Transitions = new Transitions
        {
            new BrushTransition { Property = Button.BackgroundProperty, Duration = TimeSpan.FromMilliseconds(120) },
        };
        ToolTip.SetTip(button, tip);
        button.PointerEntered += (_, _) =>
        {
            button.Background = Palette.AccentTint;
            button.Foreground = hover;
            Icons.Tint(content, hover);
        };
        button.PointerExited += (_, _) =>
        {
            button.Background = Brushes.Transparent;
            button.Foreground = normal;
            Icons.Tint(content, normal);
        };
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>
    /// 组合式工具条按钮：内容由多个字形拼成（如 T⌄ / ⌄字号⌄ / A⌄ / 油漆桶⌄，对齐 Mac EditorView）。
    /// 外框、hover 淡绿底与 0.12s 过渡与 <see cref="Tool"/> 完全一致。
    /// </summary>
    public static Button ToolComposite(Control content, string tip, Action click, bool wide = true,
        bool recolorOnHover = true)
    {
        var button = new Button
        {
            Content = content,
            Width = wide ? double.NaN : Metrics.ToolButtonSize,
            MinWidth = wide ? 40 : Metrics.ToolButtonSize,
            Height = Metrics.ToolButtonSize,
            Padding = wide ? new Thickness(5, 0) : new Thickness(0),
            Margin = new Thickness(1, 0),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        button.Transitions = new Transitions
        {
            new BrushTransition { Property = Button.BackgroundProperty, Duration = TimeSpan.FromMilliseconds(120) },
        };
        ToolTip.SetTip(button, tip);
        button.PointerEntered += (_, _) =>
        {
            button.Background = Palette.AccentTint;
            if (recolorOnHover) Icons.Tint(content, Palette.Accent);
        };
        button.PointerExited += (_, _) =>
        {
            button.Background = Brushes.Transparent;
            if (recolorOnHover) Icons.Tint(content, Palette.TextSecondary);
        };
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>工具条里的小下拉箭头（chevron.down），字号比主字形小、颜色更浅。</summary>
    public static TextBlock ToolChevron(double size = 9)
        => Text(Glyphs.ChevronDown, size, null, Palette.TextTertiary);

    /// <summary>轻量文字 / 图标按钮（列表行操作、侧栏小动作、日期步进）。</summary>
    public static Button Small(string text, Action click, IBrush? foreground = null,
        double size = 12)
    {
        var fg = foreground ?? Palette.TextSecondary;
        var vector = Icons.Vector(text, 14, fg);
        var button = new Button
        {
            Content = vector ?? (object)text,
            FontSize = size,
            FontWeight = FontWeight.Medium,
            Foreground = fg,
            Background = Brushes.Transparent,
            BorderBrush = Palette.LineSoft,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 4),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        button.Transitions = new Transitions
        {
            new BrushTransition { Property = Button.BackgroundProperty, Duration = TimeSpan.FromMilliseconds(120) },
        };
        button.PointerEntered += (_, _) =>
        {
            button.Background = Palette.SidebarHover;
            if (vector != null) Icons.Tint(vector, Palette.TextPrimary);
        };
        button.PointerExited += (_, _) =>
        {
            button.Background = Brushes.Transparent;
            // 恢复成「当前」Foreground 而非创建时的 fg：调用方若动态改色（如置顶按钮变黄），
            // hover 离开后不会丢失新颜色。
            if (vector != null) Icons.Tint(vector, button.Foreground ?? fg);
        };
        return button.WithClick(click);
    }

    private static Button WithClick(this Button button, Action click)
    {
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>输入框外壳：灰底圆角 + 聚焦时淡绿细环，避免 WinUI 的蓝色粗边框。</summary>
    public static Border Field(TextBox inner, IBrush? background = null)
    {
        inner.BorderThickness = new Thickness(0);
        inner.Background = Brushes.Transparent;
        inner.Padding = new Thickness(10, 7);
        var frame = new Border
        {
            Background = background ?? Palette.Pill,
            BorderBrush = Palette.Transparent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = inner,
        };
        frame.Transitions = new Transitions
        {
            new BrushTransition { Property = Border.BorderBrushProperty, Duration = TimeSpan.FromMilliseconds(140) },
        };
        inner.GotFocus += (_, _) => frame.BorderBrush = Palette.FieldFocusRing;
        inner.LostFocus += (_, _) => frame.BorderBrush = Palette.Transparent;
        return frame;
    }

    // ── 胶囊 / 角标 ───────────────────────────────────────────────────
    public static Border Pill(string text, IBrush? background = null, IBrush? foreground = null,
        double size = 11.5, double radius = Metrics.RadiusPill) => new()
    {
        Background = background ?? Palette.Pill,
        CornerRadius = new CornerRadius(radius),
        Padding = new Thickness(8, 2),
        Child = Text(text, size, FontWeight.SemiBold, foreground ?? Palette.TextTertiary),
    };

    public static Border Chip(string text, Action? onRemove = null)
    {
        var content = new Grid();
        var label = Text(text, 12, null, Palette.TextSecondary);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.VerticalAlignment = VerticalAlignment.Center;
        if (onRemove != null) label.Margin = new Thickness(0, 0, 4, 0); // 给右上角叉号让位
        content.Children.Add(label);
        if (onRemove != null)
        {
            var remove = new Button
            {
                Content = Icons.Vector(Glyphs.Close, 6, Palette.Brush(0xBFBFBF)) ?? (object)Glyphs.Close,
                FontSize = 6,
                Width = 12,
                Height = 12,
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = Palette.Brush(0xBFBFBF),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(-2, -3, -7, 0), // 顶到芯片右上角（左右边距各缩小 2）
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            remove.Click += (_, _) => onRemove();
            content.Children.Add(remove);
        }
        return new Border
        {
            Background = Palette.TagChipActive,
            BorderBrush = Palette.Accent,
            BorderThickness = new Thickness(1), // 0.5px 在圆角矩形上会被抗锯齿抹掉，改 1px 才看得见
            CornerRadius = new CornerRadius(Metrics.RadiusPill),
            Padding = new Thickness(8, 3),
            Child = content,
        };
    }

    /// <summary>18px 圆形数量角标。绿色=未完成，红色=逾期，>99 显示 99+。</summary>
    public static Border Badge(int count, bool red = true)
    {
        if (count <= 0)
            return new Border { IsVisible = false };
        return new Border
        {
            Background = red ? Palette.Red : Palette.Accent,
            CornerRadius = new CornerRadius(Metrics.BadgeSize / 2),
            MinWidth = Metrics.BadgeSize,
            Height = Metrics.BadgeSize,
            Padding = new Thickness(5, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = Text(count > 99 ? "99+" : count.ToString(), 11, FontWeight.Bold, Palette.White),
        };
    }

    /// <summary>任务状态圈：待完成红空心 / 进行中琥珀空心 / 已完成绿实心带勾 / 已终止灰实心横杠。</summary>
    public static Control StateRing(TodoStatus status)
    {
        var size = Metrics.StateRingSize;
        var ring = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            BorderThickness = new Thickness(1.5),
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        switch (status)
        {
            case TodoStatus.todo:
                // 待完成：用户提供的红色圆形播放三角图标（#F66060）
                ring.BorderThickness = new Thickness(0);
                ring.Child = Icons.Render(Glyphs.StateTodo, size, Palette.Brush(0xF66060));
                break;
            case TodoStatus.doing:
                // 进行中：用户提供的黄色圆形时钟指针图标（#FEB621）
                ring.BorderThickness = new Thickness(0);
                ring.Child = Icons.Render(Glyphs.StateDoing, size, Palette.Brush(0xFEB621));
                break;
            case TodoStatus.done:
                // 已完成：用户提供的绿色圆形对勾图标（#029B00）
                ring.BorderThickness = new Thickness(0);
                ring.Child = Icons.Render(Glyphs.StateDone, size, Palette.Brush(0x029B00));
                break;
            case TodoStatus.terminated:
                // 已终止：用户提供的灰色圆形减号图标（#8A8A8A，镂空横杠）
                ring.BorderThickness = new Thickness(0);
                ring.Background = Brushes.Transparent;
                ring.Child = Icons.Render(Glyphs.StateTerminated, size, Palette.Brush(0x8A8A8A));
                break;
        }
        return ring;
    }

    /// <summary>分组圆点色：待完成红 / 进行中琥珀 / 已完成绿 / 已终止灰。</summary>
    public static IBrush GroupColor(TodoStatus status) => status switch
    {
        TodoStatus.todo => Palette.Red,
        TodoStatus.doing => Palette.Amber,
        TodoStatus.done => Palette.Accent,
        _ => Palette.TextTertiary,
    };

    // ── 卡片 / 浮层 ───────────────────────────────────────────────────
    public static Border Card(Control child, IBrush? background = null, double radius = Metrics.RadiusSheet)
        => new()
        {
            Background = background ?? Palette.Panel,
            BorderBrush = Palette.LineSoft,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(radius),
            BoxShadow = Palette.ShadowSm,
            Padding = new Thickness(16, 14),
            Child = child,
        };

    public static Border Popover(Control child, double width) => new()
    {
        Width = width,
        Background = Palette.Panel,
        BorderBrush = Palette.LineSoft,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(Metrics.RadiusPop),
        BoxShadow = Palette.ShadowMd,
        Padding = new Thickness(9),
        Child = child,
    };

    public static Control EmptyState(string text, string glyph = Glyphs.Note) => new StackPanel
    {
        Spacing = 10,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Children =
        {
            Icon(glyph, 40, Palette.TextQuaternary),
            Text(text, 14, null, Palette.TextTertiary),
        },
    };

    /// <summary>给 Border 挂 Mac 风格 hover 底色，并加 0.12s 的背景过渡。</summary>
    public static Border WithHover(this Border border, IBrush normal, IBrush hover)
    {
        border.Background = normal;
        border.Transitions = new Transitions
        {
            new BrushTransition { Property = Border.BackgroundProperty, Duration = TimeSpan.FromMilliseconds(120) },
        };
        border.PointerEntered += (_, _) => border.Background = hover;
        border.PointerExited += (_, _) => border.Background = normal;
        return border;
    }

    /// <summary>hover 时叠加一层极轻的抬升阴影（列表卡片）。</summary>
    public static Border WithElevate(this Border border)
    {
        border.PointerEntered += (_, _) => border.BoxShadow = Palette.ShadowXs;
        border.PointerExited += (_, _) => border.BoxShadow = default;
        return border;
    }

    public static void Hand(Control control) =>
        control.Cursor = new Cursor(StandardCursorType.Hand);
}
