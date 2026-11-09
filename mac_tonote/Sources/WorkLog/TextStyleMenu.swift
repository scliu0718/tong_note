import AppKit
import SwiftUI
import WorkLogCore
import WorkLogRichText

/// 工具栏「T」样式入口。
///
/// 层级固定为 1 个正文（14pt 常规字重）+ 4 个标题（18 / 24 / 32 / 40pt，全部加粗），
/// 有序列表与无序列表也从原来的两个独立图标收进这里。
/// 用浮窗而不是原生 Menu：可以精确控制左侧字形、字号提示、右侧勾选，
/// 也不会出现 NSMenu 不渲染自定义 View、系统额外补一个下拉箭头的问题。
struct TextStyleMenu: View {
    @ObservedObject var controller: RichTextEditorController
    @State private var isPresented = false

    var body: some View {
        Button {
            isPresented = true
        } label: {
            HStack(spacing: 3) {
                Text("T")
                    .font(.system(size: 15, weight: .semibold, design: .serif))
                    .foregroundColor(Theme.textSecondary)
                    .frame(width: 18)
                Image(systemName: "chevron.down")
                    .font(.system(size: 8))
                    .foregroundColor(Theme.textTertiary)
            }
            .frame(height: 30)
            .padding(.horizontal, 6)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .fixedSize()
        .help("文本样式：正文 / 四级标题 / 有序列表 / 无序列表")
        .popover(isPresented: $isPresented, arrowEdge: .bottom) {
            TextStylePopover(controller: controller, isPresented: $isPresented)
        }
    }
}

// MARK: - 浮窗内容

private struct TextStylePopover: View {
    @ObservedObject var controller: RichTextEditorController
    @Binding var isPresented: Bool

    private struct StyleItem: Identifiable {
        let style: RichTextBlockStyle
        let glyph: String
        let title: String
        let sizeHint: String
        var id: String { style.rawValue }
    }

    /// 1 个正文 + 4 个标题；右侧数字与字号一一对应
    private let items: [StyleItem] = [
        StyleItem(style: .paragraph, glyph: "T", title: "正文", sizeHint: "14"),
        StyleItem(style: .heading1, glyph: "H1", title: "一级标题", sizeHint: "40"),
        StyleItem(style: .heading2, glyph: "H2", title: "二级标题", sizeHint: "32"),
        StyleItem(style: .heading3, glyph: "H3", title: "三级标题", sizeHint: "24"),
        StyleItem(style: .heading4, glyph: "H4", title: "四级标题", sizeHint: "18")
    ]

    private var orderedTrailing: String {
        let labels = ["1.", "a.", "i."]
        return labels[min(max(controller.currentListLevel, 0), labels.count - 1)]
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            ForEach(items) { item in
                StyleRow(controller: controller,
                         glyph: item.glyph,
                         title: item.title,
                         sizeHint: item.sizeHint,
                         style: item.style,
                         accentGlyph: item.style == .paragraph) {
                    controller.applyBlockStyle(item.style)
                    isPresented = false
                }
                .help("\(item.title) · \(item.sizeHint)pt")
            }

            Divider().padding(.vertical, 5)

            StyleRow(controller: controller,
                     symbol: "list.number",
                     title: "有序列表",
                     trailing: orderedTrailing,
                     style: .orderedItem) {
                controller.toggleList(.orderedItem)
                isPresented = false
            }
            .help("再次点击切换子级编号（1. → a.）；也可在列表中按 Tab / Shift+Tab 调整")

            StyleRow(controller: controller,
                     symbol: "list.bullet",
                     title: "无序列表",
                     trailing: "•",
                     style: .bulletItem) {
                controller.toggleList(.bulletItem)
                isPresented = false
            }
            .help("蓝色小圆点；再次点击提升缩进层级")
        }
        .padding(7)
        .frame(width: 226)
        // 浮窗挂载后再按当前光标刷一次，保证首次打开时勾选与层级就是最新的
        .onAppear { controller.refreshCurrentStyle() }
    }
}

// MARK: - 单行

private struct StyleRow: View {
    @ObservedObject var controller: RichTextEditorController
    var glyph: String? = nil
    var symbol: String? = nil
    let title: String
    /// 右侧灰色字号提示（标题层级）
    var sizeHint: String? = nil
    /// 右侧编号体系提示（列表层级）
    var trailing: String? = nil
    var style: RichTextBlockStyle? = nil
    var accentGlyph: Bool = false
    let action: () -> Void

    @State private var hovering = false

    private static let accentBlue = Color(hex: 0x2E7CF6)
    private var isSelected: Bool {
        guard let style else { return false }
        return controller.currentBlockStyle == style
    }

    var body: some View {
        Button(action: action) {
            HStack(spacing: 10) {
                Group {
                    if let symbol {
                        Image(systemName: symbol).font(.system(size: 13, weight: .medium))
                    } else {
                        Text(glyph ?? "")
                            .font(.system(size: 13, weight: .semibold, design: .serif))
                    }
                }
                .foregroundColor(accentGlyph ? Self.accentBlue : Theme.textSecondary)
                .frame(width: 30, alignment: .leading)

                Text(title)
                    .font(.system(size: 13))
                    .foregroundColor(Theme.textPrimary)

                Spacer(minLength: 6)

                if let sizeHint {
                    Text(verbatim: sizeHint)
                        .font(.system(size: 10, weight: .medium, design: .monospaced))
                        .foregroundColor(Theme.textQuaternary)
                } else if let trailing {
                    Text(verbatim: trailing)
                        .font(.system(size: 11, weight: .semibold))
                        .foregroundColor(Self.accentBlue.opacity(0.75))
                }

                if isSelected {
                    Image(systemName: "checkmark")
                        .font(.system(size: 12, weight: .semibold))
                        .foregroundColor(Self.accentBlue)
                }
            }
            .padding(.horizontal, 9)
            .padding(.vertical, 6)
            .background(
                RoundedRectangle(cornerRadius: 5)
                    .fill(hovering ? Color(hex: 0xF2F2F4) : Color.clear)
            )
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
    }
}
