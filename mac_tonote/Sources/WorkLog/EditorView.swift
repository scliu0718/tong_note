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
    @StateObject private var controller = RichTextEditorController()
    @State private var newTag = ""
    @State private var showAddTag = false
    @State private var pendingDelete: DeleteAction?
    @State private var errorMessage: String?

    /// 右上角删除按钮的两种确认：移入回收站 / 永久删除（回收站内）
    private enum DeleteAction: String {
        case trash
        case permanent
    }
    @State private var showLinkPrompt = false
    @State private var linkURL = "https://"
    @State private var linkText = ""
    @State private var showColorPicker = false
    @State private var customColor = Color(hex: 0x1D1D1F)

    /// 常用字号
    private let fontSizes: [CGFloat] = [11, 12, 13, 14, 15, 16, 18, 20, 24, 28, 32, 40]
    /// 调色板
    private struct PaletteColor: Identifiable {
        let id: String
        let name: String
        let hex: UInt32
    }

    private let palette: [PaletteColor] = [
        PaletteColor(id: "primary", name: "正文", hex: 0x1D1D1F),
        PaletteColor(id: "secondary", name: "次级", hex: 0x48484A),
        PaletteColor(id: "tertiary", name: "浅灰", hex: 0x86868B),
        PaletteColor(id: "red", name: "强调红", hex: 0xE5484D),
        PaletteColor(id: "amber", name: "琥珀", hex: 0xF5A623),
        PaletteColor(id: "green", name: "绿色", hex: 0x1A9E5C),
        PaletteColor(id: "greenDeep", name: "深绿", hex: 0x147A46),
        PaletteColor(id: "baidu", name: "百度蓝", hex: 0x06A7FF),
        PaletteColor(id: "baiduDeep", name: "深蓝", hex: 0x0084D6)
    ]

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
                           onEditorError: { errorMessage = $0 },
                           readOnly: showingTrash)
                .background(Theme.panelBg)
        }
        .background(Theme.panelBg)
        .alert("添加标签", isPresented: $showAddTag) {
            TextField("标签名", text: $newTag)
            Button("添加") { addTag() }
            Button("取消", role: .cancel) { newTag = "" }
        }
        .alert("插入链接", isPresented: $showLinkPrompt) {
            TextField("链接地址", text: $linkURL)
            TextField("显示文字（可留空）", text: $linkText)
            Button("插入") { insertLink() }
            Button("取消", role: .cancel) { }
        }
        .alert("操作失败", isPresented: Binding(
            get: { errorMessage != nil },
            set: { if !$0 { errorMessage = nil } }
        )) {
            Button("好") { errorMessage = nil }
        } message: {
            Text(errorMessage ?? "")
        }
        // 删除二次确认：普通笔记移入回收站，回收站笔记永久删除
        .alert(deleteAlertTitle,
               isPresented: Binding(get: { pendingDelete != nil },
                                    set: { if !$0 { pendingDelete = nil } })) {
            Button("取消", role: .cancel) { pendingDelete = nil }
            Button(pendingDelete == .permanent ? "彻底删除" : "移入回收站", role: .destructive) {
                let action = pendingDelete
                pendingDelete = nil
                if action == .permanent { onPermanentDelete() } else { onDelete() }
            }
        } message: {
            Text(pendingDelete == .permanent
                 ? "此操作不可恢复，笔记将被永久移除。"
                 : "笔记将移入回收站，可随时在回收站中恢复。")
        }
    }

    private var deleteAlertTitle: String {
        pendingDelete == .permanent ? "彻底删除笔记？" : "删除笔记？"
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
                    // 「快同步」和「快任务」一样是独立条目：不归属任何笔记本分类，
                    // 因此不再显示分类下拉、标签和添加标签入口。
                    independentBadge
                    Text("·").foregroundColor(Theme.textQuaternary)
                    Text("编辑于 \(note.timeText)")
                        .font(.system(size: 12)).foregroundColor(Theme.textQuaternary)
                } else {
                    Menu {
                        ForEach(Notebooks.list, id: \.self) { nb in
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
                    Button(action: { showAddTag = true }) {
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

    /// 「快同步」独立条目标识：明确它不属于任何笔记本分类、也不参与分类筛选。
    private var independentBadge: some View {
        HStack(spacing: 5) {
            Image(systemName: "square.dashed")
                .font(.system(size: 10, weight: .medium))
            Text("独立笔记 · 不归属任何分类")
                .font(.system(size: 11, weight: .medium))
        }
        .foregroundColor(Theme.textTertiary)
        .padding(.horizontal, 8).padding(.vertical, 3)
        .background(Color(hex: 0xF2F2F4))
        .clipShape(RoundedRectangle(cornerRadius: 5))
        .fixedSize(horizontal: true, vertical: false)
        .help("「快同步」与「快任务」一样是独立条目，不参与笔记本分类筛选")
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
            .help("把笔记恢复到原分类")

            Button(action: { pendingDelete = .permanent }) {
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
            .fixedSize()
            .help("设置字号")

            // 文字颜色
            Menu {
                ForEach(palette) { item in
                    Button {
                        if let color = NSColor(hex: item.hex) {
                            controller.setTextColor(color)
                        }
                    } label: {
                        HStack(spacing: 6) {
                            RoundedRectangle(cornerRadius: 2)
                                .fill(Color(hex: item.hex))
                                .frame(width: 12, height: 12)
                                .overlay(RoundedRectangle(cornerRadius: 2).stroke(Color.black.opacity(0.08)))
                            Text(item.name)
                        }
                    }
                }
                Divider()
                Button("自定义颜色…") { showColorPicker = true }
            } label: {
                HStack(spacing: 4) {
                    Image(systemName: "paintpalette").font(.system(size: 13))
                    Image(systemName: "chevron.down").font(.system(size: 8))
                }
                .foregroundColor(Theme.textSecondary)
                .frame(width: 30, height: 30)
                .contentShape(Rectangle())
            }
            .menuStyle(.borderlessButton)
            .fixedSize()
            .help("文字颜色")
            .popover(isPresented: $showColorPicker, arrowEdge: .bottom) {
                ColorPicker("文字颜色", selection: $customColor, supportsOpacity: false)
                    .padding(10)
                    .frame(width: 200)
                    .onChange(of: customColor) { _, newValue in
                        controller.setTextColor(NSColor(newValue))
                    }
            }

            // 分段（段落样式）
            Menu {
                Button("正文") { controller.applyBlockStyle(.paragraph) }
                Button("标题 1") { controller.applyBlockStyle(.heading1) }
                Button("标题 2") { controller.applyBlockStyle(.heading2) }
                Button("标题 3") { controller.applyBlockStyle(.heading3) }
                Divider()
                Button("引用") { controller.applyBlockStyle(.quote) }
                Button("代码块") { controller.applyBlockStyle(.code) }
            } label: {
                HStack(spacing: 3) {
                    Text("分段").font(.system(size: 12))
                    Image(systemName: "chevron.down").font(.system(size: 8))
                }
                .foregroundColor(Theme.textSecondary)
                .frame(height: 30)
                .padding(.horizontal, 7)
                .contentShape(Rectangle())
            }
            .menuStyle(.borderlessButton)
            .fixedSize()
            .help("段落样式")

            toolbarDivider

            formatButton("list.bullet", .bullet, "无序列表") { controller.toggleList(.bulletItem) }
            formatButton("list.number", .ordered, "有序列表（序号）") { controller.toggleList(.orderedItem) }
            formatButton("text.quote", .quote, "引用") { controller.applyBlockStyle(.quote) }
            formatButton("chevron.left.forwardslash.chevron.right", .code, "代码块") { controller.applyBlockStyle(.code) }

            toolbarDivider

            formatButton("link", .link, "插入链接") {
                linkURL = "https://"
                linkText = ""
                showLinkPrompt = true
            }
            formatButton("photo", .image, "插入图片") { insertImage() }

            Spacer()

            Text("\(plainWordCount) 字").font(.system(size: 12)).foregroundColor(Theme.textTertiary)
            // 删除按钮回到单个图标，功能靠悬浮说明 + 二次确认交代
            if !isQuickSync {
                Button(action: { pendingDelete = .trash }) {
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
            errorMessage = error.localizedDescription
        }
    }

    private func insertLink() {
        let trimmed = linkURL.trimmingCharacters(in: .whitespaces)
        guard !trimmed.isEmpty, let url = URL(string: trimmed), url.scheme != nil else {
            errorMessage = "请输入有效的链接地址"
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

// MARK: - NSColor 便捷构造
extension NSColor {
    convenience init?(hex: UInt32) {
        self.init(
            srgbRed: CGFloat((hex >> 16) & 0xFF) / 255,
            green: CGFloat((hex >> 8) & 0xFF) / 255,
            blue: CGFloat(hex & 0xFF) / 255,
            alpha: 1
        )
    }
}
