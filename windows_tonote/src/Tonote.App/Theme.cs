using Avalonia;
using Avalonia.Media;

namespace Tonote.App;

/// <summary>
/// Mac 端 Sources/WorkLog/Theme.swift 与「同笔记-Mac端UI设计.html」:root 令牌的 1:1 映射。
/// 与 Mac 一致：固定浅色、全部为 sRGB 十六进制，不使用系统动态色 / 深色变体。
/// </summary>
public static class Palette
{
    public static Color Rgb(uint hex) => Color.FromRgb(
        (byte)((hex >> 16) & 0xFF),
        (byte)((hex >> 8) & 0xFF),
        (byte)(hex & 0xFF));

    public static SolidColorBrush Brush(uint hex) => new(Rgb(hex));

    public static SolidColorBrush Alpha(uint hex, byte alpha) => new(Color.FromArgb(
        alpha,
        (byte)((hex >> 16) & 0xFF),
        (byte)((hex >> 8) & 0xFF),
        (byte)(hex & 0xFF)));

    // ── 基础背景与文字（Theme.swift:16-23） ──────────────────────────────
    public static IBrush Bg { get; } = Brush(0xE9E9EC);
    public static IBrush WindowBg { get; } = Brush(0xF5F5F7);
    public static IBrush SidebarBg { get; } = Brush(0xECECEF);
    public static IBrush Panel { get; } = Brush(0xFFFFFF);
    public static IBrush EditorBg { get; } = Brush(0xFFFFFF);
    public static IBrush TextPrimary { get; } = Brush(0x1D1D1F);
    public static IBrush TextSecondary { get; } = Brush(0x48484A);
    public static IBrush TextTertiary { get; } = Brush(0x86868B);
    public static IBrush TextQuaternary { get; } = Brush(0xA1A1A6);

    // ── 分割线：Mac 把 line(0xE0E0E4) / lineSoft(0xECECEF) / hairline(0xEFEFF2) 分开 ──
    public static IBrush Line { get; } = Brush(0xE0E0E4);
    public static IBrush LineSoft { get; } = Brush(0xECECEF);
    public static IBrush Hairline { get; } = Brush(0xEFEFF2);

    // ── 主题绿 / 百度蓝 / 状态色 ────────────────────────────────────────
    public static IBrush Accent { get; } = Brush(0x1A9E5C);
    public static IBrush AccentHover { get; } = Brush(0x147A46);
    public static IBrush AccentTint { get; } = Brush(0xE6F4EC);
    public static IBrush AccentTint2 { get; } = Brush(0xCFE9D9);
    public static IBrush Baidu { get; } = Brush(0x06A7FF);
    public static IBrush BaiduDeep { get; } = Brush(0x0084D6);
    public static IBrush BaiduTint { get; } = Brush(0xE5F5FF);
    public static IBrush Red { get; } = Brush(0xE5484D);
    public static IBrush Amber { get; } = Brush(0xF5A623);

    // ── 设计稿补充令牌（Mac 代码里散落的字面量，统一收拢） ────────────────
    public static IBrush TableAccent { get; } = Brush(0x2E7CF6);
    public static IBrush PanelSoft { get; } = Brush(0xF8FCFA);
    public static IBrush RedTint { get; } = Brush(0xFCEBEB);
    public static IBrush DangerBorder { get; } = Brush(0xF3D2D3);
    public static IBrush CardSelected { get; } = Brush(0xF4FAF6);
    public static IBrush CardSelectedBorder { get; } = Brush(0xDCEDE2);
    public static IBrush CardHover { get; } = Brush(0xF7F7F8);
    public static IBrush SidebarHover { get; } = Brush(0xEFEFF1);
    public static IBrush Pill { get; } = Brush(0xF2F2F4);
    public static IBrush TagChip { get; } = Brush(0xEFEFF1);
    public static IBrush TagChipActive { get; } = Brush(0xE7F3EB);
    public static IBrush BodyText { get; } = Brush(0x47474A);
    public static IBrush QuoteBg { get; } = Brush(0xF7FAF8);
    public static IBrush QuoteText { get; } = Brush(0x86868B);
    public static IBrush CodeBg { get; } = Brush(0xF5F5F7);
    public static IBrush TableBorder { get; } = Brush(0xD8D9DE);
    public static IBrush GutterDot { get; } = Brush(0xC9CCD4);
    public static IBrush GutterPlus { get; } = Brush(0x337AEB);
    public static IBrush TooltipBg { get; } = Brush(0x2C2C2E);
    public static IBrush Link { get; } = Brush(0x1A73E6);
    public static IBrush ListMarker { get; } = Brush(0x2E7CF6);
    public static IBrush RingIdle { get; } = Brush(0xC7C7CC);
    public static IBrush SyncRunBg { get; } = Brush(0xFFF4DE);
    public static IBrush SyncRunText { get; } = Brush(0xA06A00);
    public static IBrush UsageBar { get; } = Brush(0xE5E5E8);
    public static IBrush White { get; } = Brush(0xFFFFFF);
    public static IBrush Transparent { get; } = Brushes.Transparent;

    // ── 阴影（设计稿 --shadow-sm / --shadow-md） ─────────────────────────
    // 直接构造 BoxShadow，不走字符串 Parse：旧代码的多阴影字符串解析会失败并静默返回 default，
    // 导致所有卡片 / 浮层实际上都没有阴影，看起来「平、糙」。
    // Avalonia 11.3 的 BoxShadows 构造函数只接受单个 BoxShadow，这里用一层调好 blur/alpha 的阴影
    // 逼近设计稿的双层效果，保证一定生效。
    public static BoxShadows ShadowSm { get; } = new(new BoxShadow
    {
        OffsetX = 0, OffsetY = 1, Blur = 4, Spread = 0,
        Color = Color.FromArgb(18, 20, 20, 22),
    });

    public static BoxShadows ShadowMd { get; } = new(new BoxShadow
    {
        OffsetX = 0, OffsetY = 8, Blur = 26, Spread = 0,
        Color = Color.FromArgb(34, 20, 20, 22),
    });

    /// <summary>比 ShadowSm 更淡的一档，用于列表卡片 hover 的极轻抬升。</summary>
    public static BoxShadows ShadowXs { get; } = new(new BoxShadow
    {
        OffsetX = 0, OffsetY = 1, Blur = 2, Spread = 0,
        Color = Color.FromArgb(11, 20, 20, 22),
    });

    // ── 精致化补充令牌 ──────────────────────────────────────────────────
    public static IBrush FieldFocusRing { get; } = Brush(0xCFE9D9);
    public static IBrush ScrollThumb { get; } = Brush(0xC9CCD4);
    public static IBrush CardHoverBorder { get; } = Brush(0xE8E8EC);
}

/// <summary>Mac 端写死的尺寸 / 字号 / 圆角常量（设计稿 + 功能点文档:128）。</summary>
public static class Metrics
{
    // 窗口与三栏
    public const double WindowWidth = 1120;
    public const double WindowHeight = 700;
    public const double WindowMinWidth = 960;
    public const double WindowMinHeight = 620;
    public const double SidebarTiny = 52;
    public const double SidebarCompact = 112;
    public const double SidebarExpanded = 200;
    public const double SidebarHandle = 6;
    public const double NoteListWidth = 292;
    public const double SidebarThresholdTiny = 82;
    public const double SidebarThresholdCompact = 156;

    // 字号
    public const double FontTitle = 22;
    public const double FontHeader = 20;
    public const double FontGroup = 16;
    public const double FontSidebarWide = 13;
    public const double FontSidebarCompact = 11;
    public const double FontSidebarTiny = 10;
    public const double FontCardTitle = 13.5;
    public const double FontSnippet = 12;
    public const double FontMeta = 11;
    public const double FontTag = 10;
    public const double FontBody = 14;
    public const double FontEditorBody = 15;

    // 圆角
    public const double RadiusButton = 7;
    public const double RadiusCard = 8;
    public const double RadiusPop = 9;
    public const double RadiusSheet = 12;
    public const double RadiusPill = 5;

    // 浮层
    public const double PopAvatar = 244;
    public const double PopTextStyle = 226;
    public const double PopColor = 236;
    public const double PopTableGrid = 248;
    public const double PopCellMenu = 248;
    public const double PopAlign = 168;
    public const double PopCalendar = 348;
    public const double SheetWidth = 900;
    public const double SheetHeight = 640;
    public const double GuideWidth = 860;
    public const double GuideHeight = 740;

    // 组件
    public const double ToolButtonSize = 30;
    public const double BadgeSize = 18;
    public const double StateRingSize = 17;
    public const double CardPadding = 11;
    public const double EditorInset = 24;
    public const double ToolbarPaddingH = 20;
    public const double ToolbarPaddingV = 6;
    public const double GroupSpacing = 32;
}
