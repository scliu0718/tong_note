import AppKit
import SwiftUI

/// 「文字颜色 / 背景颜色」色板浮窗。
///
/// 之前用原生 `Menu` + 自定义 SwiftUI 色块做菜单项，macOS 的 NSMenu 不会渲染
/// 这类自定义 View，结果菜单里只剩空白（透明）行。改成自定义浮窗后，
/// 9 个默认色块由 SwiftUI 正常绘制，一定能显示出来。
struct ColorPalettePopover: View {
    let title: String
    let colors: [FormatPaletteColor]
    let showsClear: Bool
    let onSelect: (NSColor) -> Void
    let onClear: () -> Void
    @Binding var customColor: Color
    let onCustom: (NSColor) -> Void

    private let columns = Array(repeating: GridItem(.fixed(28), spacing: 8), count: 5)

    var body: some View {
        VStack(alignment: .leading, spacing: 11) {
            Text(title)
                .font(.system(size: 12, weight: .semibold))
                .foregroundColor(Theme.textSecondary)

            LazyVGrid(columns: columns, spacing: 8) {
                ForEach(colors) { item in
                    Button {
                        if let color = NSColor(hex: item.hex) { onSelect(color) }
                    } label: {
                        ColorChip(hex: item.hex, size: 28)
                    }
                    .buttonStyle(.plain)
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
                    }
                Spacer(minLength: 4)
                if showsClear {
                    Button("清除背景色") { onClear() }
                        .font(.system(size: 11))
                        .buttonStyle(.borderless)
                        .fixedSize(horizontal: true, vertical: false)
                        .foregroundColor(Theme.textSecondary)
                }
            }
        }
        .padding(12)
        .frame(width: 236)
    }
}
