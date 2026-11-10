import AppKit
import WorkLogCore

/// 单元格右上角「下箭头」弹出的菜单：背景色、匹配笔记宽度、对齐、删除行列 / 表格。
///
/// 用原生 NSMenu + 自定义色板 view，菜单在 AppKit 层直接调用 RichTextEditorController，
/// 不必把事件一路回传到 SwiftUI，也就不会打断正在输入的选区。
enum RichTextTableCellMenu {

    /// 第一格 nil = 「无颜色」（白圆 + 斜杠）。
    static let colors: [String?] = [
        nil, "#F2F2F4", "#D8D9DE", "#B9BCC5", "#8E929B",
        "#FBE4D6", "#FCEFB4", "#DCEFD8", "#D6EFF0", "#D9E6F8",
        "#5B5B60", "#8B8F98", "#7A5AF8", "#C86DD7", "#E5484D",
        "#F59E42", "#F7CE46", "#35A45A", "#2EB3AB", "#2F7BEE"
    ]

    /// 一次只弹一个菜单，强引用当前动作对象，避免菜单跟踪期间被释放。
    private static var currentActions: MenuActions?

    static func show(for cell: RichTextTableCellFrame,
                     at point: NSPoint,
                     in view: NSView,
                     textView: NSTextView,
                     controller: RichTextEditorController) {
        let info = currentInfo(for: cell, in: textView)
        let actions = MenuActions(textView: textView, controller: controller, cell: cell)
        let menu = NSMenu()
        menu.autoenablesItems = false
        menu.font = NSFont.systemFont(ofSize: 13)

        let title = NSMenuItem(title: "单元格背景", action: nil, keyEquivalent: "")
        title.isEnabled = false
        title.attributedTitle = NSAttributedString(
            string: "单元格背景",
            attributes: [.font: NSFont.systemFont(ofSize: 11, weight: .medium),
                         .foregroundColor: NSColor.secondaryLabelColor]
        )
        menu.addItem(title)

        let palette = PaletteView(colors: colors,
                                  selected: info.background,
                                  target: actions,
                                  action: #selector(MenuActions.pickColor(_:)))
        let paletteItem = NSMenuItem()
        paletteItem.view = palette
        menu.addItem(paletteItem)

        menu.addItem(.separator())

        let widthItem = NSMenuItem(title: "匹配笔记宽度",
                                   action: #selector(MenuActions.toggleFullWidth(_:)),
                                   keyEquivalent: "")
        widthItem.target = actions
        widthItem.state = info.fullWidth ? .on : .off
        menu.addItem(widthItem)

        let alignItem = NSMenuItem(title: "单元格对齐", action: nil, keyEquivalent: "")
        let alignMenu = NSMenu()
        alignMenu.autoenablesItems = false
        for option in AlignmentOption.all {
            let item = NSMenuItem(title: option.title,
                                  action: #selector(MenuActions.align(_:)),
                                  keyEquivalent: "")
            item.target = actions
            item.representedObject = option.value.rawValue
            item.state = info.alignment == option.value ? .on : .off
            alignMenu.addItem(item)
        }
        alignItem.submenu = alignMenu
        menu.addItem(alignItem)

        menu.addItem(.separator())
        for option in DestructiveOption.all {
            let item = NSMenuItem(title: option.title,
                                  action: option.action,
                                  keyEquivalent: "")
            item.target = actions
            menu.addItem(item)
        }

        currentActions = actions
        actions.menu = menu
        menu.popUp(positioning: nil, at: point, in: view)
    }

    private struct Info {
        var background: String?
        var fullWidth: Bool
        var alignment: RichTextCellAlignment
    }

    private static func currentInfo(for cell: RichTextTableCellFrame,
                                    in textView: NSTextView) -> Info {
        let fallback = Info(background: nil, fullWidth: true, alignment: .left)
        guard let storage = textView.textStorage, storage.length > 0 else { return fallback }
        let probe = min(cell.tableProbe, storage.length - 1)
        guard let read = RichTextTableLayout.read(from: storage, at: probe),
              read.table.rowCount > 0, read.table.columnCount > 0 else { return fallback }
        let row = min(max(cell.row, 0), read.table.rowCount - 1)
        let column = min(max(cell.column, 0), read.table.columnCount - 1)
        let target = read.table.rows[row][column]
        return Info(background: target.backgroundHex,
                    fullWidth: read.table.fullWidth,
                    alignment: target.alignment)
    }

    private struct AlignmentOption {
        let title: String
        let value: RichTextCellAlignment
        static let all: [AlignmentOption] = [
            AlignmentOption(title: "居左", value: .left),
            AlignmentOption(title: "居中", value: .center),
            AlignmentOption(title: "居右", value: .right)
        ]
    }

    private struct DestructiveOption {
        let title: String
        let action: Selector
        static let all: [DestructiveOption] = [
            DestructiveOption(title: "删除选中的列", action: #selector(MenuActions.deleteColumn(_:))),
            DestructiveOption(title: "删除选中的行", action: #selector(MenuActions.deleteRow(_:))),
            DestructiveOption(title: "删除表格", action: #selector(MenuActions.deleteTable(_:)))
        ]
    }

    // MARK: - 动作

    private final class MenuActions: NSObject {
        let textView: NSTextView
        let controller: RichTextEditorController
        let cell: RichTextTableCellFrame
        weak var menu: NSMenu?

        init(textView: NSTextView,
             controller: RichTextEditorController,
             cell: RichTextTableCellFrame) {
            self.textView = textView
            self.controller = controller
            self.cell = cell
        }

        /// 菜单弹出时鼠标不在正文里，执行依赖选区的命令前先把光标放回目标格。
        private func focusCell() {
            let length = textView.textStorage?.length ?? 0
            let location = min(max(cell.characterRange.location, 0), max(length - 1, 0))
            textView.setSelectedRange(NSRange(location: location, length: 0))
        }

        @objc func pickColor(_ sender: NSButton) {
            let colors = RichTextTableCellMenu.colors
            guard colors.indices.contains(sender.tag) else { return }
            controller.setTableCellBackground(colors[sender.tag],
                                              row: cell.row,
                                              column: cell.column,
                                              probe: cell.tableProbe)
            menu?.cancelTracking()
        }

        @objc func align(_ sender: NSMenuItem) {
            guard let raw = sender.representedObject as? String,
                  let value = RichTextCellAlignment(rawValue: raw) else { return }
            focusCell()
            controller.setCellAlignment(value)
        }

        @objc func toggleFullWidth(_ sender: NSMenuItem) {
            controller.setTableFullWidth(sender.state != .on, probe: cell.tableProbe)
        }

        @objc func deleteColumn(_ sender: NSMenuItem) {
            focusCell()
            controller.deleteTableColumn()
        }

        @objc func deleteRow(_ sender: NSMenuItem) {
            focusCell()
            controller.deleteTableRow()
        }

        @objc func deleteTable(_ sender: NSMenuItem) {
            focusCell()
            controller.deleteTable()
        }
    }

    // MARK: - 色板

    private static let accent = NSColor(srgbRed: 0.20, green: 0.48, blue: 0.92, alpha: 1)

    private final class PaletteView: NSView {
        override var isFlipped: Bool { true }

        init(colors: [String?], selected: String?, target: AnyObject, action: Selector) {
            let columns = 5
            let swatch: CGFloat = 20
            let gap: CGFloat = 9
            let pad: CGFloat = 10
            let rows = (colors.count + columns - 1) / columns
            let width = pad * 2 + CGFloat(columns) * swatch + CGFloat(columns - 1) * gap
            let height = pad * 2 + CGFloat(rows) * swatch + CGFloat(rows - 1) * gap
            super.init(frame: NSRect(x: 0, y: 0, width: width, height: height))

            for (index, hex) in colors.enumerated() {
                let column = index % columns
                let row = index / columns
                let button = NSButton(frame: NSRect(x: pad + CGFloat(column) * (swatch + gap),
                                                   y: pad + CGFloat(row) * (swatch + gap),
                                                   width: swatch,
                                                   height: swatch))
                let isSelected = RichTextCellColor.normalized(hex) == selected
                button.image = Self.swatchImage(hex: hex, selected: isSelected, size: swatch)
                button.imagePosition = .imageOnly
                button.isBordered = false
                button.setButtonType(.momentaryChange)
                button.target = target
                button.action = action
                button.tag = index
                addSubview(button)
            }
        }

        required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

        private static func swatchImage(hex: String?, selected: Bool, size: CGFloat) -> NSImage {
            let image = NSImage(size: NSSize(width: size, height: size))
            image.lockFocus()
            let circle = NSRect(x: 1.5, y: 1.5, width: size - 3, height: size - 3)
            let fill = hex.flatMap { RichTextBridge.color(fromHex: $0) } ?? NSColor.white
            fill.setFill()
            NSBezierPath(ovalIn: circle).fill()

            if hex == nil {
                let slash = NSBezierPath()
                slash.lineWidth = 1.2
                slash.lineCapStyle = .round
                slash.move(to: NSPoint(x: size * 0.32, y: size * 0.32))
                slash.line(to: NSPoint(x: size * 0.68, y: size * 0.68))
                NSColor(srgbRed: 0.72, green: 0.73, blue: 0.76, alpha: 1).setStroke()
                slash.stroke()
            }

            let ring = NSBezierPath(ovalIn: circle)
            ring.lineWidth = selected ? 2 : 0.8
            let stroke: NSColor = selected
                ? RichTextTableCellMenu.accent
                : NSColor(srgbRed: 0.87, green: 0.88, blue: 0.90, alpha: 1)
            stroke.setStroke()
            ring.stroke()
            if selected {
                let outer = NSBezierPath(ovalIn: NSRect(x: 0.5, y: 0.5,
                                                        width: size - 1, height: size - 1))
                outer.lineWidth = 1.5
                outer.stroke()
            }
            image.unlockFocus()
            return image
        }
    }
}
