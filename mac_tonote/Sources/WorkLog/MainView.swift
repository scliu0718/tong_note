import SwiftUI
import WorkLogCore

enum SortMode: String, CaseIterable {
    case updated = "最近更新"
    case created = "按创建"
    case title = "按标题"
}

/// 侧栏三档固定宽度：极窄（只有图标）/ 窄（图标+文字竖排）/ 宽（横排）。
/// 固定档位可以避免拖到中间宽度时排版混在一起、对不齐。
enum SidebarWidth {
    static let tiny: CGFloat = 52
    static let compact: CGFloat = 112
    static let expanded: CGFloat = 200

    /// 极窄 / 窄 的分界
    static let compactThreshold: CGFloat = (tiny + compact) / 2      // 82
    /// 窄 / 宽 的分界
    static let expandedThreshold: CGFloat = (compact + expanded) / 2 // 156

    /// 拖动时按投影宽度吸附到最近的一档。
    static func snapped(_ projected: CGFloat) -> CGFloat {
        if projected < compactThreshold { return tiny }
        if projected < expandedThreshold { return compact }
        return expanded
    }

    /// 点击分隔条：极窄 → 窄 → 宽 → 极窄，循环切换。
    static func next(after current: CGFloat) -> CGFloat {
        if current < compactThreshold { return compact }
        if current < expandedThreshold { return expanded }
        return tiny
    }

    /// 右箭头：向右放宽一档，到最宽档为止（不循环）。
    static func grow(from current: CGFloat) -> CGFloat {
        if current < compactThreshold { return compact }
        if current < expandedThreshold { return expanded }
        return expanded
    }

    /// 左箭头：向左收窄一档，到极窄档为止（不循环）。
    static func shrink(from current: CGFloat) -> CGFloat {
        if current >= expandedThreshold { return compact }
        if current >= compactThreshold { return tiny }
        return tiny
    }

    static func canGrow(_ current: CGFloat) -> Bool { current < expandedThreshold }
    static func canShrink(_ current: CGFloat) -> Bool { current >= compactThreshold }
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
                // 极窄档的 hover 名称浮窗会伸到侧栏右侧，需要盖在笔记列表之上
                .zIndex(1)

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
    @State private var hoveringBar = false
    @State private var hoveringSwitch = false
    @State private var dragging = false
    @State private var didSwitch = false

    private var hovering: Bool { hoveringBar || hoveringSwitch }

    var body: some View {
        ZStack {
            Rectangle()
                // 拖动期间分隔条可能因跳档移出鼠标位置，保持高亮避免忽明忽暗
                .fill(hovering || dragging ? Theme.accent : Color.clear)
                .frame(width: 2)
                .contentShape(Rectangle().inset(by: -3))
                .frame(width: 6)
                .onHover { hoveringBar = $0 }
                .gesture(
                    DragGesture()
                        .onChanged { value in
                            if initialWidth == 0 { initialWidth = width }
                            dragging = true
                            // 一次拖动最多切换一次：跨过阈值后立即锁定，
                            // 否则鼠标在阈值附近来回移动会让两档反复跳变、界面闪烁
                            guard !didSwitch else { return }
                            let projected = initialWidth + value.translation.width
                            // 三档吸附：极窄 / 窄 / 宽，跨档后立即锁定，避免来回跳变闪烁
                            let target = SidebarWidth.snapped(projected)
                            if width != target {
                                width = target
                                didSwitch = true
                            }
                        }
                        .onEnded { _ in
                            initialWidth = 0
                            didSwitch = false
                            dragging = false
                        }
                )
                .onTapGesture {
                    width = SidebarWidth.next(after: width)
                }
                .help("拖动或点击切换侧栏宽度：极窄 / 窄 / 宽")

            widthSwitch
        }
        .frame(width: 6)
        .frame(maxHeight: .infinity)
        .zIndex(2)
    }

    /// 分隔条中部的左右尖括号：左收窄一档、右放宽一档。
    /// 两个括号成对出现，直观表示这条分隔线可以向两边切换宽度。
    private var widthSwitch: some View {
        HStack(spacing: 0) {
            Button {
                width = SidebarWidth.shrink(from: width)
            } label: {
                Image(systemName: "chevron.left")
                    .font(.system(size: 9, weight: .semibold))
                    .foregroundColor(SidebarWidth.canShrink(width) ? Theme.textTertiary : Theme.textQuaternary)
                    .frame(width: 15, height: 18)
                    .contentShape(Rectangle())
            }
            .disabled(!SidebarWidth.canShrink(width))
            .help("向左：侧栏收窄一档")

            Rectangle().fill(Theme.line).frame(width: 1, height: 10)

            Button {
                width = SidebarWidth.grow(from: width)
            } label: {
                Image(systemName: "chevron.right")
                    .font(.system(size: 9, weight: .semibold))
                    .foregroundColor(SidebarWidth.canGrow(width) ? Theme.textTertiary : Theme.textQuaternary)
                    .frame(width: 15, height: 18)
                    .contentShape(Rectangle())
            }
            .disabled(!SidebarWidth.canGrow(width))
            .help("向右：侧栏放宽一档")
        }
        .buttonStyle(.plain)
        .background(
            RoundedRectangle(cornerRadius: 5)
                .fill(Color.white.opacity(hovering ? 0.98 : 0.88))
                .overlay(RoundedRectangle(cornerRadius: 5).stroke(Theme.line, lineWidth: 0.5))
        )
        .shadow(color: Color.black.opacity(hovering ? 0.12 : 0.06), radius: 3, y: 1)
        .opacity(hovering || dragging ? 1 : 0.75)
        .onHover { hoveringSwitch = $0 }
    }
}
