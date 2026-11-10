import AppKit
import WorkLogCore

/// 表格的插入、跳格、增删行列。排版和读回在 `RichTextTableLayout`。
enum RichTextTableEditor {

    static func insertTable(rows: Int,
                            columns: Int,
                            in textView: NSTextView,
                            attachments: AttachmentStore?) {
        guard let storage = textView.textStorage else { return }
        let store = resolvedStore(attachments)
        let selected = textView.selectedRange()

        if storage.length > 0 {
            let probe = min(selected.location, storage.length - 1)
            if RichTextTableLayout.containsTable(storage, at: probe),
               let existing = RichTextTableLayout.read(from: storage, at: probe) {
                insertFresh(rows: rows,
                            columns: columns,
                            at: existing.next,
                            replacing: 0,
                            in: textView,
                            storage: storage,
                            attachments: store)
                return
            }
        }

        var location = min(selected.location, storage.length)
        var length = selected.length
        if length > 0,
           !RichTextTableLayout.shouldAllowChange(in: textView, range: selected, replacement: "") {
            location = min(NSMaxRange(selected), storage.length)
            length = 0
        }
        insertFresh(rows: rows,
                    columns: columns,
                    at: location,
                    replacing: length,
                    in: textView,
                    storage: storage,
                    attachments: store)
    }

    @discardableResult
    static func handleTab(in textView: NSTextView, shift: Bool, attachments: AttachmentStore?) -> Bool {
        guard let caret = RichTextTableLayout.caret(in: textView) else { return false }
        if shift {
            if caret.column > 0 {
                move(in: textView, row: caret.row, column: caret.column - 1)
            } else if caret.row > 0 {
                move(in: textView, row: caret.row - 1, column: caret.columnCount - 1)
            }
            return true
        }
        if caret.column + 1 < caret.columnCount {
            move(in: textView, row: caret.row, column: caret.column + 1)
            return true
        }
        if caret.row + 1 < caret.rowCount {
            move(in: textView, row: caret.row + 1, column: 0)
            return true
        }
        insertRowBelow(in: textView, attachments: attachments, focusColumn: 0)
        return true
    }

    static func insertRowBelow(in textView: NSTextView,
                               attachments: AttachmentStore?,
                               focusColumn: Int? = nil) {
        guard let caret = RichTextTableLayout.caret(in: textView),
              var read = currentTable(in: textView) else { return }
        guard read.table.insertRow(at: caret.row + 1) else { return }
        let column = min(focusColumn ?? caret.column, read.table.columnCount - 1)
        rewrite(read, table: read.table, in: textView, attachments: attachments, row: caret.row + 1, column: column)
    }

    static func insertColumnAfter(in textView: NSTextView, attachments: AttachmentStore?) {
        guard let caret = RichTextTableLayout.caret(in: textView),
              var read = currentTable(in: textView) else { return }
        guard read.table.insertColumn(at: caret.column + 1) else { return }
        rewrite(read, table: read.table, in: textView, attachments: attachments, row: caret.row, column: caret.column + 1)
    }

    /// 表格边沿的「+」加行：在第 index 行前插入。probe 是目标表格内任意字符位置，
    /// 因为点击加号时鼠标不一定在正文里，不能依赖当前选区去猜是哪张表。
    static func insertRow(at index: Int,
                          probe: Int,
                          in textView: NSTextView,
                          attachments: AttachmentStore?) {
        guard var read = tableSlice(in: textView, probe: probe) else { return }
        let insertion = min(max(index, 0), read.table.rowCount)
        guard read.table.insertRow(at: insertion) else { return }
        rewrite(read, table: read.table, in: textView,
                attachments: attachments, row: insertion, column: 0)
    }

    /// 表格边沿的「+」加列：在第 index 列前插入。
    static func insertColumn(at index: Int,
                             probe: Int,
                             in textView: NSTextView,
                             attachments: AttachmentStore?) {
        guard var read = tableSlice(in: textView, probe: probe) else { return }
        let insertion = min(max(index, 0), read.table.columnCount)
        guard read.table.insertColumn(at: insertion) else { return }
        rewrite(read, table: read.table, in: textView,
                attachments: attachments, row: 0, column: insertion)
    }

    static func deleteRow(in textView: NSTextView, attachments: AttachmentStore?) {
        guard let caret = RichTextTableLayout.caret(in: textView),
              var read = currentTable(in: textView) else { return }
        guard read.table.rowCount > 1 else {
            deleteTable(in: textView)
            return
        }
        guard read.table.removeRow(at: caret.row) else { return }
        let row = min(caret.row, read.table.rowCount - 1)
        let column = min(caret.column, read.table.columnCount - 1)
        rewrite(read, table: read.table, in: textView, attachments: attachments, row: row, column: column)
    }

    static func deleteColumn(in textView: NSTextView, attachments: AttachmentStore?) {
        guard let caret = RichTextTableLayout.caret(in: textView),
              var read = currentTable(in: textView) else { return }
        guard read.table.columnCount > 1 else {
            deleteTable(in: textView)
            return
        }
        guard read.table.removeColumn(at: caret.column) else { return }
        let row = min(caret.row, read.table.rowCount - 1)
        let column = min(caret.column, read.table.columnCount - 1)
        rewrite(read, table: read.table, in: textView, attachments: attachments, row: row, column: column)
    }

    static func deleteTable(in textView: NSTextView) {
        guard let storage = textView.textStorage, let read = currentTable(in: textView) else { return }
        let leavesNothing = read.range.location == 0 && NSMaxRange(read.range) >= storage.length
        let replacement: NSAttributedString = leavesNothing
            ? RichTextTableLayout.trailingParagraph()
            : NSAttributedString()
        replace(in: textView,
                range: read.range,
                with: replacement,
                caretNear: read.range.location,
                row: -1,
                column: -1)
    }

    // MARK: - 私有

    private struct TableSlice {
        var table: RichTextTable
        var range: NSRange
    }

    private static func resolvedStore(_ attachments: AttachmentStore?) -> AttachmentStore {
        attachments ?? AttachmentStore(rootDirectory: FileManager.default.temporaryDirectory)
    }

    private static func currentTable(in textView: NSTextView) -> TableSlice? {
        tableSlice(in: textView, probe: textView.selectedRange().location)
    }

    private static func tableSlice(in textView: NSTextView, probe: Int) -> TableSlice? {
        guard let storage = textView.textStorage, storage.length > 0 else { return nil }
        let safe = min(max(probe, 0), storage.length - 1)
        guard let read = RichTextTableLayout.read(from: storage, at: safe) else { return nil }
        return TableSlice(table: read.table, range: read.range)
    }

    private static func move(in textView: NSTextView, row: Int, column: Int) {
        let near = textView.selectedRange().location
        RichTextTableLayout.moveCaret(in: textView, near: near, row: row, column: column)
    }

    private static func rewrite(_ slice: TableSlice,
                                table: RichTextTable,
                                in textView: NSTextView,
                                attachments: AttachmentStore?,
                                row: Int,
                                column: Int) {
        let rendered = RichTextTableLayout.attributedString(for: table, attachments: resolvedStore(attachments))
        replace(in: textView,
                range: slice.range,
                with: rendered,
                caretNear: slice.range.location,
                row: row,
                column: column)
    }

    private static func insertFresh(rows: Int,
                                    columns: Int,
                                    at location: Int,
                                    replacing length: Int,
                                    in textView: NSTextView,
                                    storage: NSTextStorage,
                                    attachments: AttachmentStore) {
        let table = RichTextTable.make(rows: rows, columns: columns)
        let rendered = NSMutableAttributedString()
        if needsParagraphBreak(before: location, in: storage) {
            var attributes = storage.attributes(at: location - 1, effectiveRange: nil)
            attributes.removeValue(forKey: RichTextTableLayout.idKey)
            attributes.removeValue(forKey: RichTextTableLayout.weightsKey)
            rendered.append(NSAttributedString(string: "\n", attributes: attributes))
        }
        let tableStart = location + rendered.length
        rendered.append(RichTextTableLayout.attributedString(for: table, attachments: attachments))
        if location + length >= storage.length {
            rendered.append(RichTextTableLayout.trailingParagraph())
        }
        replace(in: textView,
                range: NSRange(location: location, length: length),
                with: rendered,
                caretNear: tableStart,
                row: 0,
                column: 0)
    }

    private static func needsParagraphBreak(before location: Int, in storage: NSTextStorage) -> Bool {
        guard location > 0, location <= storage.length else { return false }
        let previous = (storage.string as NSString).character(at: location - 1)
        return previous != 10 && previous != 13
    }

    private static func replace(in textView: NSTextView,
                                range: NSRange,
                                with replacement: NSAttributedString,
                                caretNear: Int,
                                row: Int,
                                column: Int) {
        guard let storage = textView.textStorage else { return }
        let safeLocation = min(max(range.location, 0), storage.length)
        let safeLength = min(max(range.length, 0), storage.length - safeLocation)
        let safeRange = NSRange(location: safeLocation, length: safeLength)

        RichTextTableLayout.allowsStructuralEdits = true
        defer { RichTextTableLayout.allowsStructuralEdits = false }
        guard textView.shouldChangeText(in: safeRange, replacementString: replacement.string) else { return }
        storage.replaceCharacters(in: safeRange, with: replacement)

        if row >= 0, storage.length > 0 {
            RichTextTableLayout.moveCaret(in: textView, near: caretNear, row: row, column: column)
        } else {
            let caret = min(max(caretNear, 0), storage.length)
            textView.setSelectedRange(NSRange(location: caret, length: 0))
        }
        textView.didChangeText()
    }
}
