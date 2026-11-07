import SwiftUI
import WorkLogCore

enum SortMode: String, CaseIterable {
    case updated = "最近更新"
    case created = "按创建"
    case title = "按标题"
}

/// 侧栏只允许两档固定宽度，避免拖到中间宽度时两种排版混在一起、对不齐。
enum SidebarWidth {
    static let compact: CGFloat = 112
    static let expanded: CGFloat = 200
    static let threshold: CGFloat = (compact + expanded) / 2   // 156
}

struct MainView: View {
    @StateObject private var store = NoteStore()
    @StateObject private var todos = TodoStore()
    @EnvironmentObject private var sync: SyncEngine
    @State private var sidebarWidth: CGFloat = SidebarWidth.compact
    @State private var selectedID: UUID?
    @State private var showSync = false
    @State private var showTodos = false

    // 过滤状态
    @State private var selectedNotebook: String = Notebooks.all
    @State private var selectedTag: String?
    @State private var searchText: String = ""
    @State private var sortMode: SortMode = .updated
    @State private var showingTrash = false

    var body: some View {
        HStack(spacing: 0) {
            SidebarView(width: $sidebarWidth,
                        store: store,
                        selectedNotebook: $selectedNotebook,
                        selectedTag: $selectedTag,
                        showingTrash: $showingTrash,
                        onNewNote: newNote,
                        onSettings: { showSync = true })
                .frame(width: sidebarWidth)

            ResizeHandle(width: $sidebarWidth)

            NoteListView(store: store,
                         todos: todos,
                         selectedID: $selectedID,
                         searchText: $searchText,
                         sortMode: $sortMode,
                         notes: displayedNotes,
                         showingTrash: showingTrash,
                         onOpenTodos: { showTodos = true; selectedID = nil })
                .frame(width: 292)

            Rectangle().fill(Theme.line).frame(width: 1)

            if showTodos {
                TodoView(store: todos, noteStore: store)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if let id = selectedID, store.note(with: id) != nil {
                EditorView(note: store.binding(for: id),
                           content: store.contentBinding(for: id),
                           attachments: store.attachments,
                           store: store,
                           todos: todos,
                           onDelete: {
                               store.moveToTrash(id)
                               selectFirstNote(afterRemoving: id)
                           },
                           onPermanentDelete: {
                               store.permanentlyDelete(id)
                               selectFirstNote(afterRemoving: id)
                           },
                           onRestore: {
                               store.restore(id)
                               selectFirstNote(afterRemoving: id)
                           },
                           showingTrash: showingTrash)
                    .id(id)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                emptyEditor
            }
        }
        .background(Theme.windowBg)
        .onAppear {
            if let id = selectedID, store.note(with: id) != nil { return }
            selectedID = store.activeNotes.first(where: { $0.id != NoteStore.quickSyncNoteID })?.id
                ?? NoteStore.quickSyncNoteID
        }
        // 兜底：当前选中的笔记被删除/消失时，选中当前分类第一条，而不是「快同步」
        .onReceive(store.$notes) { notes in
            guard let id = selectedID, id != NoteStore.quickSyncNoteID else { return }
            guard !notes.contains(where: { $0.id == id }) else { return }
            selectedID = displayedNotes.first?.id ?? NoteStore.quickSyncNoteID
        }
        .onChange(of: selectedID) { _, newValue in
            if newValue != nil { showTodos = false }
        }
        .sheet(isPresented: $showSync) {
            SyncView(store: store, todos: todos)
                .environmentObject(sync)
                .frame(width: 900, height: 640)
        }
    }

    // MARK: - 过滤后的列表
    private var displayedNotes: [Note] {
        var list = showingTrash ? store.trashNotes : store.activeNotes
        list = list.filter { $0.id != NoteStore.quickSyncNoteID }
        if !showingTrash {
            if selectedNotebook != Notebooks.all {
                list = list.filter { $0.notebook == selectedNotebook }
            }
            if let tag = selectedTag {
                list = list.filter { $0.tags.contains(tag) }
            }
            if !searchText.isEmpty {
                let q = searchText.lowercased()
                list = list.filter {
                    $0.title.lowercased().contains(q) || $0.plainBody.lowercased().contains(q)
                }
            }
        }
        switch sortMode {
        case .updated: list.sort { $0.updatedAt > $1.updatedAt }
        case .created: list.sort { $0.createdAt > $1.createdAt }
        case .title: list.sort { $0.title.localizedCompare($1.title) == .orderedAscending }
        }
        return list
    }

    /// 删除 / 彻底删除 / 恢复之后，自动选中当前筛选分类里的第一条笔记；
    /// 只有该分类已经空了，才回退到固定的「快同步」篇。
    private func selectFirstNote(afterRemoving removedID: UUID? = nil) {
        let remaining = displayedNotes.filter { $0.id != removedID }
        selectedID = remaining.first?.id ?? NoteStore.quickSyncNoteID
        showTodos = false
    }

    private func newNote() {
        let notebook = selectedNotebook == Notebooks.all ? "工作日志" : selectedNotebook
        let note = store.addNote(notebook: notebook)
        selectedID = note.id
        showingTrash = false
        showTodos = false
    }

    private var emptyEditor: some View {
        VStack(spacing: 12) {
            Image(systemName: "square.and.pencil")
                .font(.system(size: 40))
                .foregroundColor(Theme.textQuaternary)
            Text("选择或新建一篇笔记")
                .font(.system(size: 14))
                .foregroundColor(Theme.textTertiary)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Theme.panelBg)
    }
}

struct ResizeHandle: View {
    @Binding var width: CGFloat
    @State private var initialWidth: CGFloat = 0
    @State private var hovering = false

    var body: some View {
        Rectangle()
            .fill(hovering ? Theme.accent : Color.clear)
            .frame(width: 2)
            .contentShape(Rectangle().inset(by: -3))
            .frame(width: 6)
            .onHover { hovering = $0 }
            .gesture(
                DragGesture()
                    .onChanged { value in
                        if initialWidth == 0 { initialWidth = width }
                        let projected = initialWidth + value.translation.width
                        // 只吸附到两档，不再产生中间宽度
                        let target = projected >= SidebarWidth.threshold
                            ? SidebarWidth.expanded : SidebarWidth.compact
                        // 瞬间切换，不产生任何中间宽度的过渡帧
                        if width != target { width = target }
                    }
                    .onEnded { _ in initialWidth = 0 }
            )
            .onTapGesture {
                width = width >= SidebarWidth.threshold
                    ? SidebarWidth.compact : SidebarWidth.expanded
            }
            .help("拖动或点击切换侧栏宽度")
    }
}
