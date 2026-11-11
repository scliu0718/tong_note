import SwiftUI
import WorkLogCore
import WorkLogRichText

/// 工具栏里的对齐入口。光标在单元格里时显示一个按钮，点开后弹出左 / 居中 / 右 / 两端对齐。
struct TableAlignmentButton: View {
    @ObservedObject var controller: RichTextEditorController
    @State private var isPresented = false

    private struct Item: Identifiable {
        let alignment: RichTextCellAlignment
        let icon: String
        let title: String
        var id: String { alignment.rawValue }
    }

    private let items: [Item] = [
        Item(alignment: .left, icon: "text.alignleft", title: "左对齐"),
        Item(alignment: .center, icon: "text.aligncenter", title: "居中"),
        Item(alignment: .right, icon: "text.alignright", title: "右对齐"),
        Item(alignment: .justify, icon: "text.justify", title: "两端对齐")
    ]

    private var current: RichTextCellAlignment {
        controller.currentCellAlignment ?? .left
    }

    var body: some View {
        Button {
            isPresented = true
        } label: {
            HStack(spacing: 3) {
                Image(systemName: "text.alignleft")
                    .font(.system(size: 13))
                Image(systemName: "chevron.down")
                    .font(.system(size: 8, weight: .semibold))
            }
            .foregroundColor(Theme.textSecondary)
            .frame(height: 30)
            .padding(.horizontal, 6)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .fixedSize()
        .help("对齐")
        .popover(isPresented: $isPresented, arrowEdge: .bottom) {
            popover
        }
    }

    private var popover: some View {
        VStack(alignment: .leading, spacing: 2) {
            ForEach(items) { item in
                Button {
                    controller.setCellAlignment(item.alignment)
                    isPresented = false
                } label: {
                    AlignRow(icon: item.icon,
                             title: item.title,
                             selected: current == item.alignment)
                }
                .buttonStyle(.plain)
            }
        }
        .padding(6)
        .frame(width: 168)
        .onAppear { controller.refreshCurrentStyle() }
    }
}

private struct AlignRow: View {
    let icon: String
    let title: String
    let selected: Bool
    @State private var hovering = false

    private static let accentBlue = Color(hex: 0x2E7CF6)

    var body: some View {
        HStack(spacing: 8) {
            Image(systemName: "checkmark")
                .font(.system(size: 11, weight: .semibold))
                .foregroundColor(selected ? Self.accentBlue : .clear)
                .frame(width: 14)
            Image(systemName: icon)
                .font(.system(size: 13))
                .foregroundColor(Theme.textSecondary)
                .frame(width: 18)
            Text(title)
                .font(.system(size: 13))
                .foregroundColor(Theme.textPrimary)
            Spacer(minLength: 0)
        }
        .padding(.horizontal, 8)
        .padding(.vertical, 6)
        .background(
            RoundedRectangle(cornerRadius: 5)
                .fill(hovering ? Color(hex: 0xF2F2F4) : Color.clear)
        )
        .contentShape(Rectangle())
        .onHover { hovering = $0 }
    }
}
