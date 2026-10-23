import SwiftUI

extension Color {
    init(hex: UInt32) {
        self.init(
            .sRGB,
            red: Double((hex >> 16) & 0xFF) / 255,
            green: Double((hex >> 8) & 0xFF) / 255,
            blue: Double(hex & 0xFF) / 255,
            opacity: 1
        )
    }
}

enum Theme {
    static let bg = Color(hex: 0xE9E9EC)
    static let windowBg = Color(hex: 0xF5F5F7)
    static let sidebarBg = Color(hex: 0xECECEF)
    static let panelBg = Color(hex: 0xFFFFFF)
    static let textPrimary = Color(hex: 0x1D1D1F)
    static let textSecondary = Color(hex: 0x48484A)
    static let textTertiary = Color(hex: 0x86868B)
    static let textQuaternary = Color(hex: 0xA1A1A6)
    static let line = Color(hex: 0xE0E0E4)
    static let lineSoft = Color(hex: 0xECECEF)
    static let accent = Color(hex: 0x1A9E5C)
    static let accentHover = Color(hex: 0x147A46)
    static let accentTint = Color(hex: 0xE6F4EC)
    static let accentTint2 = Color(hex: 0xCFE9D9)
    static let baidu = Color(hex: 0x06A7FF)
    static let baiduDeep = Color(hex: 0x0084D6)
    static let baiduTint = Color(hex: 0xE5F5FF)
    static let red = Color(hex: 0xE5484D)
    static let amber = Color(hex: 0xF5A623)

    static let monoFont = Font.system(.body, design: .monospaced)
}
