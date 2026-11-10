import SwiftUI
import WorkLogCore
import WorkLogRichText

/// 单元格文字居左 / 居中 / 居右。
/// 工具栏里只在光标进入格子时出现，表格浮层里也复用同一组按钮。
struct TableAlignmentButton: View {
    @ObservedObject var controller: RichTextEditorController

    private struct Item: Identifiable {
        let alignment: RichTextCellAlignment
        let icon: String
        let hint: String
        var id: String { alignment.rawValue }
    }

    private let items: [Item] = [
        Item(alignment: .left, icon: "text.alignleft", hint: "单元格文字居左"),
        Item(alignment: .center, icon: "text.aligncenter", hint: "单元格文字居中"),
        Item(alignment: .right, icon: "text.alignright", hint: "单元格文字居右")
    ]

    var body: some View {
        HStack(spacing: 2) {
            ForEach(items) { item in
                let active = (controller.currentCellAlignment ?? .left) == item.alignment
                Button {
                    controller.setCellAlignment(item.alignment)
                } label: {
                    Image(systemName: item.icon)
                        .font(.system(size: 13))
                        .foregroundColor(active ? Theme.accent : Theme.textSecondary)
                        .frame(width: 28, height: 30)
                        .background(
                            RoundedRectangle(cornerRadius: 5)
                                .fill(active ? Theme.accentTint : Color.clear)
                        )
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .help(item.hint)
            }
        }
    }
}
