using Avalonia.Media;

namespace Tonote.App;

public static class Palette
{
    public static SolidColorBrush Brush(uint hex) => new(Color.FromRgb(
        (byte)((hex >> 16) & 0xFF),
        (byte)((hex >> 8) & 0xFF),
        (byte)(hex & 0xFF)));

    public static IBrush WindowBg { get; } = Brush(0xF5F5F7);
    public static IBrush SidebarBg { get; } = Brush(0xECECEF);
    public static IBrush Panel { get; } = Brush(0xFFFFFF);
    public static IBrush TextPrimary { get; } = Brush(0x1D1D1F);
    public static IBrush TextSecondary { get; } = Brush(0x48484A);
    public static IBrush TextTertiary { get; } = Brush(0x86868B);
    public static IBrush TextQuaternary { get; } = Brush(0xA1A1A6);
    public static IBrush Line { get; } = Brush(0xE0E0E4);
    public static IBrush LineSoft { get; } = Brush(0xEFEFF2);
    public static IBrush Accent { get; } = Brush(0x1A9E5C);
    public static IBrush AccentHover { get; } = Brush(0x147A46);
    public static IBrush AccentTint { get; } = Brush(0xE6F4EC);
    public static IBrush CardSelected { get; } = Brush(0xF4FAF6);
    public static IBrush Baidu { get; } = Brush(0x06A7FF);
    public static IBrush Red { get; } = Brush(0xE5484D);
    public static IBrush White { get; } = Brush(0xFFFFFF);
}
