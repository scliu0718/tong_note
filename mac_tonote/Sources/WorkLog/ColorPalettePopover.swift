import AppKit
import SwiftUI

/// 「文字颜色 / 背景颜色」色板浮窗。
///
/// 之前用原生 `Menu` + 自定义 SwiftUI 色块做菜单项，macOS 的 NSMenu 不会渲染
/// 这类自定义 View，结果菜单里只剩空白（透明）行。改成自定义浮窗后，
/// 9 个默认色块由 SwiftUI 正常绘制，一定能显示出来。
///
/// 色块有三种反馈：悬停放大 + 投影、按下缩小、当前使用的颜色带强调色描边和对勾。
struct ColorPalettePopover: View {
    let title: String
    let colors: [FormatPaletteColor]
    let showsClear: Bool
    /// 读取光标 / 选区当前的颜色（0xRRGGBB）；nil 表示没有颜色（背景的「透明」）
    let currentColor: () -> UInt32?
    let onSelect: (NSColor) -> Void
    let onClear: () -> Void
    @Binding var customColor: Color
    let onCustom: (NSColor) -> Void

    /// 当前选中的色值；`loaded` 之前不显示选中态，避免首帧闪一下
    @State private var selected: UInt32?
    @State private var loaded = false

    private let columns = Array(repeating: GridItem(.fixed(28), spacing: 8), count: 5)

    var body: some View {
        VStack(alignment: .leading, spacing: 11) {
            Text(title)
                .font(.system(size: 12, weight: .semibold))
                .foregroundColor(Theme.textSecondary)

            LazyVGrid(columns: columns, spacing: 8) {
                // 透明（无背景色）放第一位：等同于清除背景色
                if showsClear {
                    Button {
                        onClear()
                        refreshSelection()
                    } label: {
                        TransparentChip(size: 28)
                            .selectionMark(loaded && selected == nil, checkColor: Theme.red)
                    }
                    .buttonStyle(PaletteChipButtonStyle())
                    .help("透明（清除背景色）")
                }
                ForEach(colors) { item in
                    Button {
                        if let color = NSColor(hex: item.hex) { onSelect(color) }
                        refreshSelection()
                    } label: {
                        ColorChip(hex: item.hex, size: 28)
                            .selectionMark(loaded && selected == item.hex,
                                           checkColor: Self.checkColor(for: item.hex))
                    }
                    .buttonStyle(PaletteChipButtonStyle())
                    .help(item.name)
                }
            }

            Divider()

            HStack(spacing: 8) {
                ColorPicker("自定义", selection: $customColor, supportsOpacity: false)
                    .font(.system(size: 11))
                    .controlSize(.small)
                    .fixedSize(horizontal: true, vertical: false)
                    .onChange(of: customColor) { _, newValue in
                        onCustom(NSColor(newValue))
                        refreshSelection()
                    }
                Spacer(minLength: 4)
            }
        }
        .padding(12)
        .frame(width: 236)
        .onAppear { refreshSelection() }
    }

    private func refreshSelection() {
        selected = currentColor()
        loaded = true
    }

    /// 深色块上用白色对勾，浅色块上用深灰对勾，保证任何颜色都看得清。
    private static func checkColor(for hex: UInt32) -> Color {
        let r = Double((hex >> 16) & 0xFF), g = Double((hex >> 8) & 0xFF), b = Double(hex & 0xFF)
        let luminance = (0.299 * r + 0.587 * g + 0.114 * b) / 255
        return luminance < 0.62 ? .white : Color(hex: 0x1D1D1F)
    }
}

/// 色块按钮的交互反馈：悬停放大并带投影，按下时缩小。
struct PaletteChipButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        PaletteChipBody(configuration: configuration)
    }
}

private struct PaletteChipBody: View {
    let configuration: ButtonStyle.Configuration
    @State private var hovering = false

    var body: some View {
        configuration.label
            .scaleEffect(configuration.isPressed ? 0.9 : (hovering ? 1.1 : 1))
            .shadow(color: Color.black.opacity(hovering ? 0.2 : 0), radius: 3, x: 0, y: 1)
            .opacity(configuration.isPressed ? 0.85 : 1)
            .animation(.easeOut(duration: 0.12), value: hovering)
            .animation(.easeOut(duration: 0.08), value: configuration.isPressed)
            .contentShape(Rectangle())
            .onHover { hovering = $0 }
    }
}

extension View {
    /// 选中态：强调色描边（向外一圈）+ 居中对勾。
    func selectionMark(_ isSelected: Bool, checkColor: Color) -> some View {
        overlay {
            if isSelected {
                ZStack {
                    RoundedRectangle(cornerRadius: 8)
                        .stroke(Theme.accent, lineWidth: 2)
                        .padding(-3)
                    Image(systemName: "checkmark")
                        .font(.system(size: 11, weight: .bold))
                        .foregroundColor(checkColor)
                }
            }
        }
    }
}
