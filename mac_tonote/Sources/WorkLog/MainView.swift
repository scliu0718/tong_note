import SwiftUI
import WorkLogCore

enum SortMode: String, CaseIterable {
    case updated = "最近更新"
    case created = "按创建"
    case title = "按标题"
}

struct MainView: View {
    @StateObject private var store = NoteStore()
    @State private var sidebarWidth: CGFloat = 112
    @State private var selectedID: UUID?
    @State private var showSync = false

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
                         selectedID: $selectedID,
                         searchText: $searchText,
                         sortMode: $sortMode,
                         notes: displayedNotes,
                         showingTrash: showingTrash)
                .frame(width: 292)

            Rectangle().fill(Theme.line).frame(width: 1)

            if let id = selectedID, store.note(with: id) != nil {
                EditorView(note: store.binding(for: id),
                           content: store.contentBinding(for: id),
                           attachments: store.attachments,
                           onDelete: { store.moveToTrash(id) })
                    .id(id)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                emptyEditor
            }
        }
        .background(Theme.windowBg)
        .onAppear {
            if selectedID == nil { selectedID = store.notes.first?.id }
        }
        .sheet(isPresented: $showSync) {
            SyncView()
                .frame(width: 900, height: 640)
        }
    }

    // MARK: - 过滤后的列表
    private var displayedNotes: [Note] {
        var list = showingTrash ? store.trashNotes : store.activeNotes
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

    private func newNote() {
        let notebook = selectedNotebook == Notebooks.all ? "工作日志" : selectedNotebook
        let note = store.addNote(notebook: notebook)
        selectedID = note.id
        showingTrash = false
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

    private let minWidth: CGFloat = 112
    private let maxWidth: CGFloat = 320

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
                        let newWidth = initialWidth + value.translation.width
                        width = min(max(newWidth, minWidth), maxWidth)
                    }
                    .onEnded { _ in initialWidth = 0 }
            )
    }
}
