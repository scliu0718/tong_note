import AppKit
import SwiftUI
import WorkLogCore
import WorkLogRichText

/// 选中文字后浮出的格式面板内容。
///
/// 把编辑器工具栏里的文字选项全部搬到这里：段落样式、行内样式、字号、
/// 文字颜色、背景颜色、列表、引用、代码块、链接、图片。
struct SelectionFormatBar: View {
    let controller: RichTextEditorController
    var onLink: () -> Void
    var onImage: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            firstRow
            secondRow
            colorRow("文字", colors: TextFormatPalette.textColors) { hex in
                if let color = NSColor(hex: hex) { controller.setTextColor(color) }
            }
            HStack(spacing: 6) {
                colorRow("背景", colors: TextFormatPalette.backgroundColors) { hex in
                    if let color = NSColor(hex: hex) { controller.setBackgroundColor(color) }
                }
                actionText("清除") { controller.clearBackgroundColor() }
                    .help("清除背景色")
            }
        }
        .font(.system(size: 11))
        .foregroundColor(Theme.textPrimary)
    }

    // MARK: - 第一行：段落样式 + 行内样式
    private var firstRow: some View {
        HStack(spacing: 3) {
            actionText("正文", width: 32) { controller.applyBlockStyle(.paragraph) }
                .help("正文")
            actionText("H1") { controller.applyBlockStyle(.heading1) }.help("一级标题")
            actionText("H2") { controller.applyBlockStyle(.heading2) }.help("二级标题")
            actionText("H3") { controller.applyBlockStyle(.heading3) }.help("三级标题")
            actionIcon("text.quote") { controller.applyBlockStyle(.quote) }.help("引用")
            actionIcon("chevron.left.forwardslash.chevron.right") {
                controller.applyBlockStyle(.code)
            }.help("代码块")
            actionIcon("list.bullet") { controller.toggleList(.bulletItem) }.help("无序列表")
            actionIcon("list.number") { controller.toggleList(.orderedItem) }.help("有序列表")

            barDivider

            actionIcon("bold") { controller.toggleBold() }.help("加粗")
            actionIcon("italic") { controller.toggleItalic() }.help("斜体")
            actionIcon("underline") { controller.toggleUnderline() }.help("下划线")
            actionIcon("strikethrough") { controller.toggleStrike() }.help("删除线")
        }
    }

    // MARK: - 第二行：字号 + 链接 / 图片
    private var secondRow: some View {
        HStack(spacing: 3) {
            Text("字号")
                .font(.system(size: 10))
                .foregroundColor(Theme.textQuaternary)
                .frame(width: 24, alignment: .leading)
            ForEach(TextFormatPalette.quickFontSizes, id: \.self) { size in
                actionText("\(Int(size))") { controller.setFontSize(size) }
                    .help("字号 \(Int(size))")
            }
            barDivider
            actionIcon("link") { onLink() }.help("插入链接")
            actionIcon("photo") { onImage() }.help("插入图片")
        }
    }

    // MARK: - 颜色行
    private func colorRow(_ label: String,
                          colors: [FormatPaletteColor],
                          action: @escaping (UInt32) -> Void) -> some View {
        HStack(spacing: 4) {
            Text(label)
                .font(.system(size: 10))
                .foregroundColor(Theme.textQuaternary)
                .frame(width: 24, alignment: .leading)
            ForEach(colors) { item in
                Button { action(item.hex) } label: {
                    ColorChip(hex: item.hex, size: 16)
                }
                .buttonStyle(.plain)
                .help(item.name)
            }
        }
    }

    // MARK: - 复用控件
    private func actionText(_ title: String, width: CGFloat = 22,
                            action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Text(title)
                .font(.system(size: 10.5, weight: .medium))
                .foregroundColor(Theme.textSecondary)
                .frame(width: width, height: 20)
                .background(RoundedRectangle(cornerRadius: 4).fill(Color(hex: 0xF4F4F6)))
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .fixedSize()
    }

    private func actionIcon(_ systemName: String,
                            action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: systemName)
                .font(.system(size: 11, weight: .medium))
                .foregroundColor(Theme.textSecondary)
                .frame(width: 22, height: 20)
                .background(RoundedRectangle(cornerRadius: 4).fill(Color(hex: 0xF4F4F6)))
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .fixedSize()
    }

    private var barDivider: some View {
        Rectangle()
            .fill(Theme.line)
            .frame(width: 1, height: 16)
            .padding(.horizontal, 4)
    }
}
