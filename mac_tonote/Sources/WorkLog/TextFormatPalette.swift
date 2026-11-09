import AppKit
import SwiftUI

/// 文字颜色 / 背景色 / 字号的共享定义。
///
/// 工具栏色板浮窗与「选中文字」浮动格式窗口共用同一份数据，
/// 避免两处颜色不一致，也便于以后统一增删默认颜色。
struct FormatPaletteColor: Identifiable {
    let id: String
    let name: String
    let hex: UInt32
}

enum TextFormatPalette {
    /// 文字颜色：以可读性较高的深 / 亮色为主
    static let textColors: [FormatPaletteColor] = [
        FormatPaletteColor(id: "primary", name: "正文黑", hex: 0x1D1D1F),
        FormatPaletteColor(id: "secondary", name: "次级灰", hex: 0x48484A),
        FormatPaletteColor(id: "tertiary", name: "浅灰", hex: 0x86868B),
        FormatPaletteColor(id: "red", name: "强调红", hex: 0xE5484D),
        FormatPaletteColor(id: "amber", name: "琥珀", hex: 0xF5A623),
        FormatPaletteColor(id: "green", name: "绿色", hex: 0x1A9E5C),
        FormatPaletteColor(id: "greenDeep", name: "深绿", hex: 0x147A46),
        FormatPaletteColor(id: "baidu", name: "亮蓝", hex: 0x06A7FF),
        FormatPaletteColor(id: "baiduDeep", name: "深蓝", hex: 0x0084D6)
    ]

    /// 背景颜色：浅色为主，作为文字高亮底色，不会盖住正文
    static let backgroundColors: [FormatPaletteColor] = [
        FormatPaletteColor(id: "bgYellow", name: "浅黄", hex: 0xFFF3C4),
        FormatPaletteColor(id: "bgGreen", name: "浅绿", hex: 0xDFF5E7),
        FormatPaletteColor(id: "bgCyan", name: "浅青", hex: 0xD9F2F0),
        FormatPaletteColor(id: "bgBlue", name: "浅蓝", hex: 0xDCEEFF),
        FormatPaletteColor(id: "bgPurple", name: "浅紫", hex: 0xEFE4FA),
        FormatPaletteColor(id: "bgPink", name: "浅粉", hex: 0xFCE4EC),
        FormatPaletteColor(id: "bgOrange", name: "浅橙", hex: 0xFFE8D6),
        FormatPaletteColor(id: "bgGray", name: "浅灰", hex: 0xEFEFF1),
        FormatPaletteColor(id: "bgWhite", name: "白色", hex: 0xFFFFFF)
    ]

    /// 工具栏字号菜单
    static let fontSizes: [CGFloat] = [11, 12, 13, 14, 15, 16, 18, 20, 24, 28, 32, 40]

    /// 浮动格式窗口里常用的几个字号
    static let quickFontSizes: [CGFloat] = [12, 14, 16, 18, 20, 24]
}

/// #RRGGBB → NSColor
extension NSColor {
    convenience init?(hex: UInt32) {
        self.init(
            srgbRed: CGFloat((hex >> 16) & 0xFF) / 255,
            green: CGFloat((hex >> 8) & 0xFF) / 255,
            blue: CGFloat(hex & 0xFF) / 255,
            alpha: 1
        )
    }
}

/// 纯色块：浮窗与浮动格式窗口共用，确保默认颜色一定可见。
struct ColorChip: View {
    let hex: UInt32
    var size: CGFloat = 18

    var body: some View {
        RoundedRectangle(cornerRadius: size * 0.22)
            .fill(Color(hex: hex))
            .frame(width: size, height: size)
            .overlay(
                RoundedRectangle(cornerRadius: size * 0.22)
                    .stroke(Color.black.opacity(0.18), lineWidth: 0.7)
            )
    }
}
