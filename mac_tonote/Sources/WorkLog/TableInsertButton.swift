import SwiftUI
import WorkLogRichText

/// 工具栏里的表格入口：点格子选行列插入；光标已经在表格里时，同时提供增删行列。
struct TableInsertButton: View {
    @ObservedObject var controller: RichTextEditorController
    @State private var isPresented = false
    @State private var hoverRows = 0
    @State private var hoverColumns = 0

    private let pickerRows = 6
    private let pickerColumns = 8

    var body: some View {
        Button {
            hoverRows = 0
            hoverColumns = 0
            isPresented = true
        } label: {
            Image(systemName: "tablecells")
                .font(.system(size: 13))
                .foregroundColor(Theme.textSecondary)
                .frame(width: 30, height: 30)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .fixedSize()
        .help("插入表格。表格内 Tab 跳到下一格，最后一格再按 Tab 会新增一行")
        .popover(isPresented: $isPresented, arrowEdge: .bottom) {
            popover
        }
    }

    private var popover: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(sizeLabel)
                .font(.system(size: 12, weight: .medium))
                .foregroundColor(Theme.textSecondary)
                .frame(maxWidth: .infinity, alignment: .leading)

            VStack(spacing: 4) {
                ForEach(1...pickerRows, id: \.self) { row in
                    HStack(spacing: 4) {
                        ForEach(1...pickerColumns, id: \.self) { column in
                            cellButton(row: row, column: column)
                        }
                    }
                }
            }

            if controller.tableCaret != nil {
                Divider()
                action("在下方插入行") { controller.insertTableRowBelow() }
                action("在右侧插入列") { controller.insertTableColumnAfter() }
                action("删除当前行") { controller.deleteTableRow() }
                action("删除当前列") { controller.deleteTableColumn() }
                action("删除表格") { controller.deleteTable() }
            }
        }
        .padding(10)
        .frame(width: 248)
    }

    private var sizeLabel: String {
        if hoverRows > 0, hoverColumns > 0 {
            return "插入 \(hoverRows) 行 × \(hoverColumns) 列"
        }
        return "选择表格大小"
    }

    private func cellButton(row: Int, column: Int) -> some View {
        let selected = row <= hoverRows && column <= hoverColumns
        return Button {
            controller.insertTable(rows: row, columns: column)
            isPresented = false
        } label: {
            RoundedRectangle(cornerRadius: 2)
                .stroke(selected ? Theme.accent : Theme.line, lineWidth: 1)
                .background(
                    RoundedRectangle(cornerRadius: 2)
                        .fill(selected ? Theme.accentTint : Color.white)
                )
                .frame(width: 18, height: 18)
        }
        .buttonStyle(.plain)
        .onHover { hovering in
            if hovering {
                hoverRows = row
                hoverColumns = column
            }
        }
    }

    private func action(_ title: String, perform: @escaping () -> Void) -> some View {
        Button {
            perform()
            isPresented = false
        } label: {
            Text(title)
                .font(.system(size: 13))
                .foregroundColor(Theme.textPrimary)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.horizontal, 8)
                .padding(.vertical, 5)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }
}
