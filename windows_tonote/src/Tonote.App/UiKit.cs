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
    public const string ChevronLeft = "‹";
    public const string ChevronRight = "›";
    public const string ChevronDown = "⌄";
    public const string More = "⋯";

    public const string Gear = "⚙";
    public const string Trash = "⌫";
    public const string Pin = "◆";
    public const string Book = "▤";
    public const string Tag = "🔖";
    public const string Note = "✎";
    public const string Cloud = "☁";
    public const string Clock = "◷";
    public const string Search = "⌕";
    public const string Calendar = "▩";

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

    // ── 按钮 ──────────────────────────────────────────────────────────
    public static Button Primary(string text, Action click, double size = 12) =>
        Styled(text, click, Palette.Accent, Palette.White, null, size);

    public static Button Ghost(string text, Action click, double size = 12) =>
        Styled(text, click, Palette.Panel, Palette.TextSecondary, Palette.Line, size);

    public static Button Danger(string text, Action click, double size = 12) =>
        Styled(text, click, Palette.Panel, Palette.Red, Palette.DangerBorder, size);

    public static Button DangerFill(string text, Action click, double size = 12) =>
        Styled(text, click, Palette.Red, Palette.White, null, size);

    private static Button Styled(string text, Action click, IBrush background, IBrush foreground,
        IBrush? border, double size)
    {
        var button = new Button
        {
            Content = text,
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
        double fontSize = 13)
    {
        var normal = Palette.TextSecondary;
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
            if (vector != null) Icons.Tint(vector, fg);
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
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        row.Children.Add(Text(text, 12, null, Palette.TextSecondary));
        if (onRemove != null)
        {
            var remove = new Button
            {
                Content = Icons.Vector(Glyphs.Close, 9, Palette.TextTertiary) ?? (object)Glyphs.Close,
                FontSize = 9,
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = Palette.TextTertiary,
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            remove.Click += (_, _) => onRemove();
            row.Children.Add(remove);
        }
        return new Border
        {
            Background = Palette.Pill,
            CornerRadius = new CornerRadius(Metrics.RadiusPill),
            Padding = new Thickness(8, 3),
            Child = row,
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
                ring.BorderBrush = Palette.Red;
                break;
            case TodoStatus.doing:
                ring.BorderBrush = Palette.Amber;
                break;
            case TodoStatus.done:
                ring.Background = Palette.Accent;
                ring.BorderBrush = Palette.Accent;
                ring.Child = Icons.Render(Glyphs.Check, 11, Palette.White);
                break;
            case TodoStatus.terminated:
                ring.Background = Palette.TextQuaternary;
                ring.BorderBrush = Palette.TextQuaternary;
                ring.Child = Text("—", 10, FontWeight.Bold, Palette.White);
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
