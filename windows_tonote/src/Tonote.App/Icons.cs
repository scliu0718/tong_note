using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
// 隐式 using 会把 Path 解析成静态类 System.IO.Path，必须显式别名到矢量形状
using AvaloniaPath = Avalonia.Controls.Shapes.Path;

namespace Tonote.App;

/// <summary>
/// Mac 端 SF Symbols 在 Windows 上的等价实现：24×24 viewBox 的线性描边图标。
/// 旧实现直接用 emoji / 全角符号 / 「Segoe MDL2 Assets」字体里的 Unicode 码位，
/// 会出现彩色 emoji、缺字豆腐块、字形粗细不一，是界面「粗糙」的主要来源。
/// 这里全部改成矢量 Path：统一 1.7 描边、圆头圆角、单色，可随 hover 换色。
/// 路径以 StreamGeometry.Parse 解析；任何语法问题都回退成文字，不会让窗口启动失败。
/// </summary>
public static class Icons
{
    private static readonly Dictionary<string, string> Paths = new(StringComparer.Ordinal)
    {
        // ── 通用操作 ────────────────────────────────────────────────
        [Glyphs.Plus] = "M12 5 V19 M5 12 H19",
        [Glyphs.Minus] = "M5 12 H19",
        [Glyphs.Close] = "M6.5 6.5 L17.5 17.5 M17.5 6.5 L6.5 17.5",
        [Glyphs.Check] = "M4.5 12.5 L9.5 17.5 L19.5 6.5",
        [Glyphs.ChevronLeft] = "M14.5 5.5 L8 12 L14.5 18.5",
        [Glyphs.ChevronRight] = "M9.5 5.5 L16 12 L9.5 18.5",
        [Glyphs.ChevronDown] = "M5.5 9.5 L12 16 L18.5 9.5",
        [Glyphs.More] = "M6 12 H7 M11.5 12 H12.5 M17 12 H18",

        // ── 侧栏 / 导航 ──────────────────────────────────────────────
        [Glyphs.Gear] =
            "M3.5 7 H9.5 M14.5 7 H20.5 M3.5 12 H9.5 M14.5 12 H20.5 M3.5 17 H9.5 M14.5 17 H20.5 " +
            "M12 5.4 A1.6 1.6 0 1 0 12 8.6 A1.6 1.6 0 1 0 12 5.4 Z " +
            "M12 10.4 A1.6 1.6 0 1 0 12 13.6 A1.6 1.6 0 1 0 12 10.4 Z " +
            "M12 15.4 A1.6 1.6 0 1 0 12 18.6 A1.6 1.6 0 1 0 12 15.4 Z",
        [Glyphs.Trash] = "M4 7 H20 M9 7 V4.8 H15 V7 M6.6 7 L7.6 20 H16.4 L17.4 7 M10 11 V17 M14 11 V17",
        [Glyphs.Pin] = "M9 3.5 H15 M10 3.5 V9 L7 12 V14 H17 V12 L14 9 V3.5 M12 14 V20.5",
        [Glyphs.Book] = "M5 4 H18.5 V20 H5 V4 Z M5 17 H18.5 M8.5 8 H14.5 M8.5 11 H14.5",
        [Glyphs.Tag] = "M4 4 H12 L20 12 L12 20 L4 12 V4 Z M7.5 7.5 H9.5 V9.5 H7.5 Z",
        [Glyphs.Note] = "M5 4 H14 L19 9 V20 H5 V4 Z M14 4 V9 H19 M8 12 H16 M8 15.5 H16 M8 19 H12.5",
        [Glyphs.Cloud] = "M6.5 18 H17.5 A4 4 0 0 0 17.5 10 A5.5 5.5 0 0 0 6.8 10.5 A3.8 3.8 0 0 0 6.5 18 Z",
        [Glyphs.Clock] = "M12 3.5 A8.5 8.5 0 1 0 12 20.5 A8.5 8.5 0 1 0 12 3.5 Z M12 7 V12 L15.5 14",
        [Glyphs.Search] = "M10.5 3.5 A7 7 0 1 0 10.5 17.5 A7 7 0 1 0 10.5 3.5 Z M15.6 15.6 L21 21",

        // ── 编辑器工具条 ──────────────────────────────────────────────
        [Glyphs.Quote] = "M5 6 V18 M9 9 H19 M9 13 H19 M9 17 H14.5",
        [Glyphs.Code] = "M9 7 L4 12 L9 17 M15 7 L20 12 L15 17 M13.6 5 L10.4 19",
        [Glyphs.Link] =
            "M10 12 H14 M10.5 8 H8 A4 4 0 0 0 8 16 H10.5 M13.5 8 H16 A4 4 0 0 1 16 16 H13.5",
        [Glyphs.Image] =
            "M3.5 4.5 H20.5 V19.5 H3.5 V4.5 Z M3.5 15.5 L8.5 10.5 L12 14 L15.5 10.5 L20.5 15.5 " +
            "M15.5 8 A1.3 1.3 0 1 0 15.5 10.6 A1.3 1.3 0 1 0 15.5 8 Z",
        [Glyphs.Highlight] = "M5.5 19.5 H18.5 M8.5 14.5 L14 5 L19 8 L13.5 17.5 Z M8.5 14.5 L13.5 17.5",
        [Glyphs.Table] = "M3.5 5 H20.5 V19 H3.5 V5 Z M3.5 10 H20.5 M3.5 14.5 H20.5 M10 5 V19 M15 5 V19",
        [Glyphs.Align] = "M4 6 H20 M4 10 H14 M4 14 H20 M4 18 H14",
        [Glyphs.AlignLeft] = "M4 6 H20 M4 10 H13 M4 14 H20 M4 18 H13",
        [Glyphs.AlignCenter] = "M4 6 H20 M7 10 H17 M4 14 H20 M7 18 H17",
        [Glyphs.AlignRight] = "M4 6 H20 M11 10 H20 M4 14 H20 M11 18 H20",
        [Glyphs.Calendar] = "M4 6 H20 V20 H4 V6 Z M4 10.5 H20 M8 3.5 V7.5 M16 3.5 V7.5 M8 14 H10 M14 14 H16 M8 17 H10",
    };

    /// <summary>
    /// 启动自检：逐个解析路径，任何一条语法不兼容都会写到 stderr，
    /// 避免某个图标悄悄回退成 emoji / 豆腐块却没人发现。
    /// </summary>
    static Icons()
    {
        foreach (var pair in Paths)
        {
            try { StreamGeometry.Parse(pair.Value); }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[Tonote.Icons] 路径解析失败: " + pair.Key + " / " + ex.Message);
            }
        }
    }

    /// <summary>描边图标；找不到对应路径或路径解析失败时返回 null，让调用方回退成文字。</summary>
    public static Control? Vector(string glyph, double size, IBrush color)
    {
        if (!Paths.TryGetValue(glyph, out var markup))
            return null;
        Geometry geometry;
        try { geometry = StreamGeometry.Parse(markup); }
        catch { return null; }

        return new AvaloniaPath
        {
            Data = geometry,
            Stroke = color,
            // 空态大图标若沿用 1.7 会显得过细，按尺寸微调保持视觉重量一致
            StrokeThickness = size >= 28 ? 2.2 : 1.7,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Fill = null,
            Stretch = Stretch.Uniform,
            Width = size,
            Height = size,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    /// <summary>有矢量用矢量，没有（例如 B / I / U / 字号数字）保留文字按钮。</summary>
    public static Control Render(string glyph, double size, IBrush color)
        => Vector(glyph, size, color) ?? new TextBlock
        {
            Text = glyph,
            FontSize = size,
            FontWeight = FontWeight.Medium,
            Foreground = color,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        };

    /// <summary>hover 换色：Path 走 Stroke，文字走 Foreground。</summary>
    public static void Tint(Control content, IBrush brush)
    {
        if (content is AvaloniaPath path) path.Stroke = brush;
        else if (content is TextBlock block) block.Foreground = brush;
    }
}
