import AppKit
import UniformTypeIdentifiers
import SwiftUI
import WorkLogCore
import WorkLogRichText

/// 选择本地图片（NSOpenPanel）
enum ImagePicker {
    static func pickImage() -> URL? {
        let panel = NSOpenPanel()
        panel.title = "插入图片"
        panel.message = "选择一张图片插入笔记正文"
        panel.allowsMultipleSelection = false
        panel.canChooseDirectories = false
        panel.canChooseFiles = true
        panel.allowedContentTypes = [.png, .jpeg, .gif, .tiff, .heic, .bmp] + [UTType(filenameExtension: "webp")].compactMap { $0 }
        return panel.runModal() == .OK ? panel.url : nil
    }
}

struct EditorView: View {
    @Binding var note: Note
    @Binding var content: NoteContent
    var attachments: AttachmentStore
    var store: NoteStore
    var todos: TodoStore
    var onDelete: () -> Void
    var onPermanentDelete: () -> Void
    var onRestore: () -> Void
    var showingTrash: Bool

    @EnvironmentObject private var sync: SyncEngine
    @ObservedObject private var notebooks = NotebookStore.shared
    @StateObject private var controller = RichTextEditorController()
    @State private var newTag = ""
    /// 编辑器所有弹窗统一走一个状态：多个 .alert 串在同一视图上时只有部分会生效，
    /// 之前「操作失败」提示因此完全弹不出来，粘贴失败表现为「没反应」。
    @State private var activeDialog: EditorDialog?

    /// 右上角删除按钮的两种确认：移入回收站 / 永久删除（回收站内）
    private enum DeleteAction: String {
        case trash
        case permanent
    }

    private enum EditorDialog: Identifiable {
        case addTag
        case insertLink
        case failure(String)
        case deleteConfirm(DeleteAction)

        var id: String {
            switch self {
            case .addTag: return "addTag"
            case .insertLink: return "insertLink"
            case .failure(let message): return "failure-\(message)"
            case .deleteConfirm(let action): return "delete-\(action.rawValue)"
            }
        }
    }

    @State private var linkURL = "https://"
    @State private var linkText = ""
    @State private var showTextPalette = false
    @State private var showBackgroundPalette = false
    @State private var customTextColor = Color(hex: 0x1D1D1F)
    @State private var customBackgroundColor = Color(hex: 0xFFF3C4)

    /// 常用字号（与选中文字浮窗共用）
    private let fontSizes = TextFormatPalette.fontSizes

    var body: some View {
        VStack(spacing: 0) {
            header
            // 回收站里的笔记只读：整条格式功能区不显示，只保留一条分隔线
            if showingTrash {
                Rectangle().fill(Theme.lineSoft).frame(height: 1)
            } else {
                toolbar
            }
            RichTextEditor(noteID: note.id,
                           content: $content,
                           controller: controller,
                           attachments: attachments,
                           onEditorError: { activeDialog = .failure($0) },
                           readOnly: showingTrash,
                           // 浮动格式窗口按需求临时关闭：开关置 false 后不注册选区回调，
                           // SelectionFormatBar / SelectionFormatPanel 及选区监听代码全部保留。
                           onSelectionChange: SelectionFormatPanel.isEnabled ? { rect in
                               guard !showingTrash, let rect else {
                                   SelectionFormatPanel.shared.hide()
                                   return
                               }
                               SelectionFormatPanel.shared.show(
                                   at: rect,
                                   bar: SelectionFormatBar(
                                       controller: controller,
                                       onLink: {
                                           linkURL = "https://"
                                           linkText = ""
                                           activeDialog = .insertLink
                                       },
                                       onImage: {
                                           insertImage()
                                       }
                                   )
                               )
                           } : nil)
                .background(Theme.panelBg)
        }
        .background(Theme.panelBg)
        .onDisappear { SelectionFormatPanel.shared.hide() }
        .onChange(of: note.id) { _, _ in SelectionFormatPanel.shared.hide() }
        .onChange(of: showingTrash) { _, _ in SelectionFormatPanel.shared.hide() }
        // 单一 alert：添加标签 / 插入链接 / 操作失败 / 删除二次确认共用
        .alert(dialogTitle,
               isPresented: Binding(get: { activeDialog != nil },
                                    set: { if !$0 { activeDialog = nil } })) {
            if let dialog = activeDialog {
                switch dialog {
                case .addTag:
                    TextField("标签名", text: $newTag)
                    Button("添加") { addTag() }
                    Button("取消", role: .cancel) { newTag = "" }
                case .insertLink:
                    TextField("链接地址", text: $linkURL)
                    TextField("显示文字（可留空）", text: $linkText)
                    Button("插入") { insertLink() }
                    Button("取消", role: .cancel) { }
                case .failure:
                    Button("好") { activeDialog = nil }
                case .deleteConfirm(let action):
                    Button("取消", role: .cancel) { activeDialog = nil }
                    Button(action == .permanent ? "彻底删除" : "移入回收站", role: .destructive) {
                        activeDialog = nil
                        if action == .permanent { onPermanentDelete() } else { onDelete() }
                    }
                }
            }
        } message: {
            Text(dialogMessage)
        }
    }

    private var dialogTitle: String {
        guard let dialog = activeDialog else { return "" }
        switch dialog {
        case .addTag: return "添加标签"
        case .insertLink: return "插入链接"
        case .failure: return "操作失败"
        case .deleteConfirm(let action):
            return action == .permanent ? "彻底删除笔记？" : "删除笔记？"
        }
    }

    private var dialogMessage: String {
        guard let dialog = activeDialog else { return "" }
        switch dialog {
        case .failure(let message): return message
        case .deleteConfirm(let action):
            return action == .permanent
                ? "此操作不可恢复，笔记将被永久移除。"
                : "笔记将移入回收站，可随时在回收站中恢复。"
        default: return ""
        }
    }

    private var isQuickSync: Bool {
        note.id == NoteStore.quickSyncNoteID
    }

    private var syncStatusText: some View {
        Group {
            switch sync.status {
            case .idle, .syncing:
                EmptyView()
            case .success(let date):
                Text("已同步 \(Self.timeFormatter.string(from: date))")
                    .font(.system(size: 11)).foregroundColor(Theme.textTertiary)
            case .failed(let msg):
                Text("失败：\(msg)")
                    .font(.system(size: 11)).foregroundColor(Theme.red)
            }
        }
    }

    private static let timeFormatter: DateFormatter = {
        let f = DateFormatter()
        f.dateFormat = "HH:mm"
        return f
    }()

    // MARK: - 标题区
    private var header: some View {
        VStack(alignment: .leading, spacing: 8) {
            // 回收站：把「恢复 / 彻底删除」放在标题行右上角，正好位于「回收站中的笔记」状态上方
            HStack(spacing: 10) {
                if showingTrash {
                    Text(note.title.isEmpty ? "笔记标题" : note.title)
                        .font(.system(size: 22, weight: .bold))
                        .foregroundColor(Theme.textPrimary)
                        .lineLimit(1)
                    Spacer(minLength: 12)
                    if !isQuickSync {
                        trashActions
                    }
                } else {
                    TextField("笔记标题", text: $note.title)
                        .textFieldStyle(.plain)
                        .font(.system(size: 22, weight: .bold))
                        .foregroundColor(Theme.textPrimary)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)

            HStack(spacing: 6) {
                if isQuickSync {
                    // 「快同步」和「快任务」一样是独立条目：不归属任何笔记本，
                    // 因此不再显示笔记本下拉、标签和添加标签入口。
                    independentBadge
                    Text("·").foregroundColor(Theme.textQuaternary)
                    Text("编辑于 \(note.timeText)")
                        .font(.system(size: 12)).foregroundColor(Theme.textQuaternary)
                } else {
                    Menu {
                        // 笔记本被删除后笔记会落到「全部笔记」（未归属），
                        // 此时把它也列入选项，避免下拉里当前选中项为空。
                        ForEach(notebookChoices, id: \.self) { nb in
                            Button(nb) { note.notebook = nb }
                        }
                    } label: {
                        HStack(spacing: 4) {
                            Text(note.notebook).font(.system(size: 12))
                            Image(systemName: "chevron.down").font(.system(size: 9))
                        }
                        .foregroundColor(Theme.textTertiary)
                        .padding(.horizontal, 8).padding(.vertical, 3)
                        .background(Color(hex: 0xF2F2F4))
                        .clipShape(RoundedRectangle(cornerRadius: 5))
                    }
                    .menuStyle(.borderlessButton)
                    .fixedSize()

                    ForEach(note.tags, id: \.self) { tag in
                        HStack(spacing: 3) {
                            Text(tag).font(.system(size: 12)).foregroundColor(Theme.textTertiary)
                            Button(action: { removeTag(tag) }) {
                                Image(systemName: "xmark").font(.system(size: 8, weight: .bold))
                                    .foregroundColor(Theme.textQuaternary)
                            }
                            .buttonStyle(.plain)
                        }
                        .padding(.horizontal, 8).padding(.vertical, 3)
                        .background(Color(hex: 0xF2F2F4))
                        .clipShape(RoundedRectangle(cornerRadius: 5))
                    }
                    Button(action: { activeDialog = .addTag }) {
                        Image(systemName: "plus").font(.system(size: 10, weight: .bold))
                            .foregroundColor(Theme.textTertiary)
                            .frame(width: 20, height: 20)
                            .background(Color(hex: 0xF2F2F4))
                            .clipShape(Circle())
                    }
                    .buttonStyle(.plain).help("添加标签")

                    Text("·").foregroundColor(Theme.textQuaternary)
                    Text("编辑于 \(note.timeText)")
                        .font(.system(size: 12)).foregroundColor(Theme.textQuaternary)
                }

                Spacer()

                if isQuickSync {
                    // 「快同步」篇：右上角提供单独同步本篇的按钮 + 状态
                    HStack(spacing: 6) {
                        syncStatusText
                        Button(action: { Task { await sync.syncSingleNote(store: store, todos: todos, noteID: note.id) } }) {
                            HStack(spacing: 4) {
                                if sync.status.isSyncing {
                                    ProgressView().controlSize(.small).scaleEffect(0.6)
                                } else {
                                    Image(systemName: "arrow.triangle.2.circlepath")
                                        .font(.system(size: 12, weight: .semibold))
                                }
                                Text("同步").font(.system(size: 12, weight: .semibold))
                            }
                            .foregroundColor(.white)
                            .padding(.horizontal, 10).padding(.vertical, 4)
                            .background(Theme.accent)
                            .clipShape(RoundedRectangle(cornerRadius: 6))
                        }
                        .buttonStyle(.plain)
                        .disabled(sync.status.isSyncing)
                    }
                } else {
                    HStack(spacing: 5) {
                        Circle().fill(showingTrash ? Theme.textQuaternary : Theme.accent)
                            .frame(width: 6, height: 6)
                        Text(showingTrash ? "回收站中的笔记" : "已保存到本地")
                            .font(.system(size: 12, weight: .medium))
                            .foregroundColor(showingTrash ? Theme.textTertiary : Theme.accent)
                    }
                    .lineLimit(1)
                    .fixedSize(horizontal: true, vertical: false)
                }
            }
            // 回收站笔记的笔记本 / 标签等元数据也不允许修改
            .disabled(showingTrash)
        }
        .padding(.horizontal, 24)
        .padding(.top, 16).padding(.bottom, 10)
    }

    /// 笔记本下拉选项；当前笔记本已不存在时（例如被删除）额外保留它作为「未归属」项。
    private var notebookChoices: [String] {
        notebooks.list.contains(note.notebook)
            ? notebooks.list
            : [note.notebook] + notebooks.list
    }

    /// 「快同步」独立条目标识：明确它不属于任何笔记本、也不参与笔记本筛选。
    private var independentBadge: some View {
        HStack(spacing: 5) {
            Image(systemName: "square.dashed")
                .font(.system(size: 10, weight: .medium))
            Text("独立笔记 · 不归属任何笔记本")
                .font(.system(size: 11, weight: .medium))
        }
        .foregroundColor(Theme.textTertiary)
        .padding(.horizontal, 8).padding(.vertical, 3)
        .background(Color(hex: 0xF2F2F4))
        .clipShape(RoundedRectangle(cornerRadius: 5))
        .fixedSize(horizontal: true, vertical: false)
        .help("「快同步」与「快任务」一样是独立条目，不参与笔记本筛选")
    }

    /// 回收站笔记的标题行操作区：两个按钮强制横排，不再挤进工具栏导致文字竖排。
    private var trashActions: some View {
        HStack(spacing: 7) {
            Button(action: onRestore) {
                Label("恢复笔记", systemImage: "arrow.uturn.backward")
                    .font(.system(size: 11, weight: .medium))
                    .foregroundColor(Theme.accent)
                    .lineLimit(1)
                    .fixedSize(horizontal: true, vertical: false)
                    .padding(.horizontal, 9).padding(.vertical, 5)
                    .background(Theme.accentTint)
                    .clipShape(RoundedRectangle(cornerRadius: 6))
            }
            .buttonStyle(.plain)
            .help("把笔记恢复到原笔记本")

            Button(action: { activeDialog = .deleteConfirm(.permanent) }) {
                Label("彻底删除", systemImage: "trash.slash")
                    .font(.system(size: 11, weight: .medium))
                    .foregroundColor(Theme.red)
                    .lineLimit(1)
                    .fixedSize(horizontal: true, vertical: false)
                    .padding(.horizontal, 9).padding(.vertical, 5)
                    .background(Color(hex: 0xFCEBEB))
                    .clipShape(RoundedRectangle(cornerRadius: 6))
            }
            .buttonStyle(.plain)
            .help("永久删除这篇笔记，不可恢复")
        }
        .fixedSize(horizontal: true, vertical: false)
    }

    // MARK: - 工具栏
    private var toolbar: some View {
        HStack(spacing: 2) {
            // 「T」样式菜单：正文 / 一~三级标题 / 其他标题 / 有序·无序列表
            TextStyleMenu(controller: controller)

            toolbarDivider

            formatButton("bold", .bold, "加粗") { controller.toggleBold() }
            formatButton("italic", .italic, "斜体") { controller.toggleItalic() }
            formatButton("underline", .underline, "下划线") { controller.toggleUnderline() }
            formatButton("strikethrough", .strikethrough, "删除线") { controller.toggleStrike() }

            toolbarDivider

            // 字号
            Menu {
                ForEach(fontSizes, id: \.self) { size in
                    Button {
                        controller.setFontSize(size)
                    } label: {
                        if size == 15 {
                            Label("\(Int(size))", systemImage: "checkmark")
                        } else {
                            Text("\(Int(size))")
                        }
                    }
                }
            } label: {
                HStack(spacing: 3) {
                    Text("字号").font(.system(size: 12))
                    Image(systemName: "chevron.down").font(.system(size: 8))
                }
                .foregroundColor(Theme.textSecondary)
                .frame(height: 30)
                .padding(.horizontal, 7)
                .contentShape(Rectangle())
            }
            .menuStyle(.borderlessButton)
            .menuIndicator(.hidden)
            .fixedSize()
            .help("设置字号")

            // 文字颜色：自定义色板浮窗（原生菜单不会渲染自定义色块，会变透明空行）
            Button {
                showTextPalette = true
            } label: {
                HStack(spacing: 4) {
                    Image(systemName: "paintpalette").font(.system(size: 13))
                    Image(systemName: "chevron.down").font(.system(size: 8))
                }
                .foregroundColor(Theme.textSecondary)
                .frame(width: 30, height: 30)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .fixedSize()
            .help("文字颜色")
            .popover(isPresented: $showTextPalette, arrowEdge: .bottom) {
                ColorPalettePopover(
                    title: "文字颜色",
                    colors: TextFormatPalette.textColors,
                    showsClear: false,
                    onSelect: { controller.setTextColor($0) },
                    onClear: {},
                    customColor: $customTextColor,
                    onCustom: { controller.setTextColor($0) }
                )
            }

            // 背景颜色：默认 9 个浅色色块 + 自定义 + 清除
            Button {
                showBackgroundPalette = true
            } label: {
                HStack(spacing: 4) {
                    Image(systemName: "paintbrush.fill").font(.system(size: 13))
                    Image(systemName: "chevron.down").font(.system(size: 8))
                }
                .foregroundColor(Theme.textSecondary)
                .frame(width: 30, height: 30)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .fixedSize()
            .help("背景颜色")
            .popover(isPresented: $showBackgroundPalette, arrowEdge: .bottom) {
                ColorPalettePopover(
                    title: "背景颜色",
                    colors: TextFormatPalette.backgroundColors,
                    showsClear: true,
                    onSelect: { controller.setBackgroundColor($0) },
                    onClear: { controller.clearBackgroundColor() },
                    customColor: $customBackgroundColor,
                    onCustom: { controller.setBackgroundColor($0) }
                )
            }

            toolbarDivider

            // 有序 / 无序列表已收进左侧「T」样式菜单，这里只保留引用与代码块
            formatButton("text.quote", .quote, "引用") { controller.applyBlockStyle(.quote) }
            formatButton("chevron.left.forwardslash.chevron.right", .code, "代码块") { controller.applyBlockStyle(.code) }

            toolbarDivider

            formatButton("link", .link, "插入链接") {
                linkURL = "https://"
                linkText = ""
                activeDialog = .insertLink
            }
            formatButton("photo", .image, "插入图片") { insertImage() }
            TableInsertButton(controller: controller)

            // 光标进入表格格子时才出现，给格子里的文字设居左 / 居中 / 居右
            if controller.tableCaret != nil {
                toolbarDivider
                TableAlignmentButton(controller: controller)
            }

            Spacer()

            Text("\(plainWordCount) 字").font(.system(size: 12)).foregroundColor(Theme.textTertiary)
            // 删除按钮回到单个图标，功能靠悬浮说明 + 二次确认交代
            if !isQuickSync {
                Button(action: { activeDialog = .deleteConfirm(.trash) }) {
                    Image(systemName: "trash")
                        .font(.system(size: 14))
                        .foregroundColor(Theme.textTertiary)
                        .frame(width: 30, height: 30)
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .help("删除笔记（移入回收站）")
            }
        }
        .padding(.horizontal, 20).padding(.vertical, 6)
        .overlay(Rectangle().fill(Theme.lineSoft).frame(height: 1), alignment: .bottom)
    }

    /// 工具栏动作
    private enum ToolbarAction {
        case bold, italic, underline, strikethrough
        case bullet, ordered, quote, code, link, image
    }

    private func formatButton(_ icon: String, _ action: ToolbarAction, _ help: String,
                              perform: @escaping () -> Void) -> some View {
        ToolbarIconButton(icon: icon, hint: help, perform: perform)
    }

    private var toolbarDivider: some View {
        Rectangle().fill(Theme.line).frame(width: 1, height: 18).padding(.horizontal, 6)
    }

    /// 正文字数（不含 HTML 标签与附件路径）
    private var plainWordCount: Int {
        let text = content.format == .richText
            ? RichTextHTMLCodec.decode(content.body).plainText
            : MarkdownConverter.plainText(fromMarkdown: content.body)
        return text
            .replacingOccurrences(of: "\n", with: "")
            .replacingOccurrences(of: " ", with: "")
            .count
    }

    // MARK: - 标签操作
    private func addTag() {
        let t = newTag.trimmingCharacters(in: .whitespaces)
        if !t.isEmpty && !note.tags.contains(t) {
            note.tags.append(t)
        }
        newTag = ""
    }

    private func removeTag(_ tag: String) {
        note.tags.removeAll { $0 == tag }
    }

    // MARK: - 插入操作
    private func insertImage() {
        guard let url = ImagePicker.pickImage() else { return }
        do {
            try controller.insertImage(from: url)
        } catch {
            activeDialog = .failure(error.localizedDescription)
        }
    }

    private func insertLink() {
        let trimmed = linkURL.trimmingCharacters(in: .whitespaces)
        guard !trimmed.isEmpty, let url = URL(string: trimmed), url.scheme != nil else {
            activeDialog = .failure("请输入有效的链接地址")
            return
        }
        let text = linkText.trimmingCharacters(in: .whitespaces)
        controller.insertLink(url: url, displayText: text.isEmpty ? nil : text)
    }
}

/// 工具栏纯图标按钮：悬浮时立刻显示功能文字气泡，系统 help 作为兜底。
/// 覆盖加粗 / 斜体 / 下划线 / 删除线 / 无序列表 / 有序列表 / 引用 / 代码块 / 链接 / 图片。
private struct ToolbarIconButton: View {
    let icon: String
    let hint: String
    let perform: () -> Void

    @State private var hovering = false

    var body: some View {
        Button(action: perform) {
            Image(systemName: icon)
                .font(.system(size: 14))
                .foregroundColor(hovering ? Theme.accent : Theme.textSecondary)
                .frame(width: 30, height: 30)
                .background(
                    RoundedRectangle(cornerRadius: 6)
                        .fill(hovering ? Theme.accentTint : Color.clear)
                )
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help(hint)
        .onHover { hovering = $0 }
        .overlay(alignment: .top) {
            if hovering {
                Text(hint)
                    .font(.system(size: 10.5, weight: .medium))
                    .foregroundColor(.white)
                    .lineLimit(1)
                    .fixedSize(horizontal: true, vertical: false)
                    .padding(.horizontal, 8).padding(.vertical, 5)
                    .background(
                        RoundedRectangle(cornerRadius: 5)
                            .fill(Color(hex: 0x2C2C2E).opacity(0.95))
                    )
                    .offset(y: -28)
                    .allowsHitTesting(false)
                    .transition(.opacity)
            }
        }
        .zIndex(hovering ? 20 : 0)
        .animation(.easeOut(duration: 0.12), value: hovering)
    }
}

// 色板数据与色块控件已抽到 TextFormatPalette.swift，
// 由工具栏色板浮窗与「选中文字」浮动格式窗口共用。
