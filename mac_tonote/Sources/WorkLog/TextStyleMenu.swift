import AppKit
import SwiftUI
import WorkLogCore
import WorkLogRichText

/// 工具栏「T」样式入口。
///
/// 参考飞书文档：正文 / 一~三级标题在主列表，「其他标题」展开四~六级，
/// 有序列表与无序列表也从原来的两个独立图标收进这里。
/// 用浮窗而不是原生 Menu：可以精确控制左侧字形、右侧勾选和子级展开，
/// 也不会出现 NSMenu 不渲染自定义 View、系统额外补一个下拉箭头的问题。
struct TextStyleMenu: View {
    @ObservedObject var controller: RichTextEditorController
    @State private var isPresented = false
    @State private var showsOtherHeadings = false

    var body: some View {
        Button {
            showsOtherHeadings = false
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
        .help("文本样式：正文 / 标题 / 有序列表 / 无序列表")
        .popover(isPresented: $isPresented, arrowEdge: .bottom) {
            TextStylePopover(
                controller: controller,
                isPresented: $isPresented,
                showsOtherHeadings: $showsOtherHeadings
            )
        }
    }
}

// MARK: - 浮窗内容

private struct TextStylePopover: View {
    @ObservedObject var controller: RichTextEditorController
    @Binding var isPresented: Bool
    @Binding var showsOtherHeadings: Bool

    private struct StyleItem: Identifiable {
        let style: RichTextBlockStyle
        let glyph: String
        let title: String
        var id: String { style.rawValue }
    }

    private let mainItems: [StyleItem] = [
        StyleItem(style: .paragraph, glyph: "T", title: "正文"),
        StyleItem(style: .heading1, glyph: "H1", title: "一级标题"),
        StyleItem(style: .heading2, glyph: "H2", title: "二级标题"),
        StyleItem(style: .heading3, glyph: "H3", title: "三级标题")
    ]

    private let otherItems: [StyleItem] = [
        StyleItem(style: .heading4, glyph: "H4", title: "四级标题"),
        StyleItem(style: .heading5, glyph: "H5", title: "五级标题"),
        StyleItem(style: .heading6, glyph: "H6", title: "六级标题")
    ]

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            ForEach(mainItems) { item in
                row(item)
            }

            // 「其他标题」：点击就地展开四~六级，等价于参考图里的 Hn > 子菜单
            StyleRow(controller: controller,
                     glyph: "Hn",
                     title: "其他标题",
                     showsDisclosure: true,
                     expanded: showsOtherHeadings) {
                withAnimation(.easeOut(duration: 0.12)) { showsOtherHeadings.toggle() }
            }
            if showsOtherHeadings {
                ForEach(otherItems) { item in
                    row(item).padding(.leading, 12)
                }
            }

            Divider().padding(.vertical, 5)

            StyleRow(controller: controller,
                     symbol: "list.number",
                     title: "有序列表",
                     style: .orderedItem) {
                controller.toggleList(.orderedItem)
                isPresented = false
            }
            StyleRow(controller: controller,
                     symbol: "list.bullet",
                     title: "无序列表",
                     style: .bulletItem) {
                controller.toggleList(.bulletItem)
                isPresented = false
            }
        }
        .padding(7)
        .frame(width: 226)
        // 浮窗挂载后再按当前光标刷一次，保证首次打开时勾选就是最新的
        .onAppear { controller.refreshCurrentStyle() }
    }

    private func row(_ item: StyleItem) -> some View {
        StyleRow(controller: controller,
                 glyph: item.glyph,
                 title: item.title,
                 style: item.style,
                 accentGlyph: item.style == .paragraph) {
            controller.applyBlockStyle(item.style)
            isPresented = false
        }
    }
}

// MARK: - 单行

private struct StyleRow: View {
    @ObservedObject var controller: RichTextEditorController
    var glyph: String? = nil
    var symbol: String? = nil
    let title: String
    var style: RichTextBlockStyle? = nil
    var accentGlyph: Bool = false
    var showsDisclosure: Bool = false
    var expanded: Bool = false
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

                if isSelected {
                    Image(systemName: "checkmark")
                        .font(.system(size: 12, weight: .semibold))
                        .foregroundColor(Self.accentBlue)
                } else if showsDisclosure {
                    Image(systemName: expanded ? "chevron.down" : "chevron.right")
                        .font(.system(size: 10, weight: .medium))
                        .foregroundColor(Theme.textQuaternary)
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
