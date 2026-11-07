import SwiftUI
import WorkLogCore

struct NoteListView: View {
    @ObservedObject var store: NoteStore
    @ObservedObject var todos: TodoStore
    @Binding var selectedID: UUID?
    @Binding var searchText: String
    @Binding var sortMode: SortMode
    let notes: [Note]
    let showingTrash: Bool
    let onOpenTodos: () -> Void

    @EnvironmentObject private var sync: SyncEngine
    @State private var confirmDeleteID: UUID?

    var body: some View {
        VStack(spacing: 0) {
            searchBar
            filterBar
            ScrollView(showsIndicators: false) {
                VStack(spacing: 8) {
                    // 「快同步」固定第一行：不随排序/搜索变化，始终置顶
                    if !showingTrash {
                        QuickSyncCard(status: sync.status, message: sync.lastMessage)
                            .contentShape(Rectangle())
                            .onTapGesture { selectedID = NoteStore.quickSyncNoteID }
                        // 「任务」固定第二行
                        TodoEntryCard(todos: todos)
                            .contentShape(Rectangle())
                            .onTapGesture(perform: onOpenTodos)
                    }
                    if notes.isEmpty {
                        emptyState
                    } else {
                        ForEach(notes) { note in
                            NoteCard(note: note, selected: note.id == selectedID)
                                .contentShape(Rectangle())
                                .onTapGesture { selectedID = note.id }
                                .contextMenu { contextMenu(for: note) }
                        }
                    }
                }
                .padding(8)
            }
        }
        .background(Theme.panelBg)
        .alert("彻底删除笔记？",
               isPresented: Binding(get: { confirmDeleteID != nil },
                                    set: { if !$0 { confirmDeleteID = nil } })) {
            Button("取消", role: .cancel) { confirmDeleteID = nil }
            Button("彻底删除", role: .destructive) {
                if let id = confirmDeleteID {
                    store.permanentlyDelete(id)
                    selectFirstNote(afterRemoving: id)
                }
                confirmDeleteID = nil
            }
        } message: {
            Text("此操作不可恢复，笔记将被永久移除。")
        }
    }

    /// 删除 / 彻底删除 / 恢复后，选中当前分类里的第一条笔记；分类空了才回退「快同步」。
    private func selectFirstNote(afterRemoving removedID: UUID) {
        guard selectedID == removedID else { return }
        selectedID = notes.filter { $0.id != removedID }.first?.id
            ?? NoteStore.quickSyncNoteID
    }

    private var emptyState: some View {
        VStack(spacing: 10) {
            Image(systemName: showingTrash ? "trash" : "magnifyingglass")
                .font(.system(size: 28))
                .foregroundColor(Theme.textQuaternary)
            Text(showingTrash ? "回收站是空的" : "没有匹配的笔记")
                .font(.system(size: 12))
                .foregroundColor(Theme.textTertiary)
        }
        .frame(maxWidth: .infinity)
        .padding(.top, 60)
    }

    /// 右键浮动菜单：文案统一带「笔记」二字，配 SF Symbol 图标与分组，视觉更规整。
    @ViewBuilder
    private func contextMenu(for note: Note) -> some View {
        if showingTrash {
            Button {
                store.restore(note.id)
                selectFirstNote(afterRemoving: note.id)
            } label: {
                Label("恢复笔记", systemImage: "arrow.uturn.backward")
            }
            Divider()
            Button(role: .destructive) {
                confirmDeleteID = note.id
            } label: {
                Label("彻底删除笔记", systemImage: "trash.slash")
            }
        } else {
            Button {
                store.togglePin(note.id)
            } label: {
                Label(note.pinned ? "取消置顶笔记" : "置顶笔记",
                      systemImage: note.pinned ? "pin.slash" : "pin")
            }
            Divider()
            Button(role: .destructive) {
                store.moveToTrash(note.id)
                selectFirstNote(afterRemoving: note.id)
            } label: {
                Label("删除笔记", systemImage: "trash")
            }
        }
    }

    private var searchBar: some View {
        HStack(spacing: 8) {
            Image(systemName: "magnifyingglass")
                .font(.system(size: 13))
                .foregroundColor(Theme.textQuaternary)
            TextField("搜索笔记、标签…", text: $searchText)
                .textFieldStyle(.plain)
                .font(.system(size: 13))
            if !searchText.isEmpty {
                Button(action: { searchText = "" }) {
                    Image(systemName: "xmark.circle.fill")
                        .font(.system(size: 12))
                        .foregroundColor(Theme.textQuaternary)
                }
                .buttonStyle(.plain)
            }
        }
        .padding(.horizontal, 10).padding(.vertical, 7)
        .background(Color(hex: 0xF2F2F4))
        .clipShape(RoundedRectangle(cornerRadius: 8))
        .padding(.horizontal, 12).padding(.top, 12).padding(.bottom, 8)
    }

    private var filterBar: some View {
        HStack(spacing: 14) {
            ForEach(SortMode.allCases, id: \.self) { mode in
                Text(mode.rawValue)
                    .font(.system(size: 12, weight: sortMode == mode ? .semibold : .regular))
                    .foregroundColor(sortMode == mode ? Theme.accent : Theme.textTertiary)
                    .contentShape(Rectangle())
                    .onTapGesture { sortMode = mode }
            }
            Spacer(minLength: 0)
            syncButton
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(.horizontal, 14).padding(.bottom, 8)
        .overlay(Rectangle().fill(Theme.lineSoft).frame(height: 1), alignment: .bottom)
    }

    private var syncButton: some View {
        Button(action: { Task { await sync.quickSync(store: store, todos: todos) } }) {
            HStack(spacing: 5) {
                if sync.status.isSyncing {
                    ProgressView()
                        .controlSize(.small)
                        .scaleEffect(0.6)
                } else {
                    Image(systemName: "arrow.triangle.2.circlepath")
                        .font(.system(size: 12, weight: .semibold))
                }
                if case .success = sync.status {
                    Text("已同步").font(.system(size: 12, weight: .semibold))
                } else {
                    Text("一键同步").font(.system(size: 12, weight: .semibold))
                }
            }
            .lineLimit(1)
            .fixedSize(horizontal: true, vertical: false)
            .foregroundColor(.white)
            .padding(.horizontal, 11).padding(.vertical, 6)
            .background(Theme.accent)
            .clipShape(RoundedRectangle(cornerRadius: 7))
        }
        .buttonStyle(.plain)
        .disabled(sync.status.isSyncing)
    }
}

struct NoteCard: View {
    let note: Note
    let selected: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: 6) {
                if note.pinned {
                    Image(systemName: "pin.fill")
                        .font(.system(size: 10))
                        .foregroundColor(Theme.amber)
                }
                Text(note.title)
                    .font(.system(size: 13.5, weight: .semibold))
                    .foregroundColor(Theme.textPrimary)
                    .lineLimit(1)
                Spacer(minLength: 0)
            }
            Text(note.snippet)
                .font(.system(size: 12))
                .foregroundColor(Theme.textTertiary)
                .lineLimit(2)
                .lineSpacing(2)
            HStack(spacing: 6) {
                Text(note.timeText).font(.system(size: 11)).foregroundColor(Theme.textQuaternary)
                ForEach(note.tags, id: \.self) { tag in
                    Text(tag)
                        .font(.system(size: 10))
                        .foregroundColor(selected ? Theme.accentHover : Theme.textTertiary)
                        .padding(.horizontal, 7).padding(.vertical, 1)
                        .background(selected ? Color(hex: 0xE7F3EB) : Color(hex: 0xEFEFF1))
                        .clipShape(Capsule())
                }
                Spacer(minLength: 0)
            }
        }
        .padding(11)
        .frame(maxWidth: .infinity, alignment: .leading)
        // 选中态比「快同步 / 任务」两个固定卡片更淡，避免视觉上混为一类
        .background(selected ? Color(hex: 0xF4FAF6) : Color.clear)
        .overlay(RoundedRectangle(cornerRadius: 8).stroke(selected ? Color(hex: 0xDCEDE2) : Color.clear, lineWidth: 1))
        .clipShape(RoundedRectangle(cornerRadius: 8))
    }
}

/// 列表顶部固定的「快同步」控制行：点击即触发一次拉取→对比→上传。
struct QuickSyncCard: View {
    let status: SyncEngine.Status
    let message: String?

    var body: some View {
        HStack(spacing: 10) {
            ZStack {
                Circle()
                    .fill(Theme.accentTint)
                    .frame(width: 34, height: 34)
                if status.isSyncing {
                    ProgressView()
                        .controlSize(.small)
                        .scaleEffect(0.6)
                } else {
                    Image(systemName: "icloud.and.arrow.up")
                        .font(.system(size: 16, weight: .semibold))
                        .foregroundColor(Theme.accent)
                }
            }
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 5) {
                    Text("快同步")
                        .font(.system(size: 13.5, weight: .semibold))
                        .foregroundColor(Theme.textPrimary)
                    if status.isSyncing {
                        Text("同步中…")
                            .font(.system(size: 11))
                            .foregroundColor(Theme.accent)
                    }
                }
                Text(statusSubtitle)
                    .font(.system(size: 11))
                    .foregroundColor(statusColor)
                    .lineLimit(1)
            }
            Spacer(minLength: 0)
            Image(systemName: "chevron.right")
                .font(.system(size: 11, weight: .semibold))
                .foregroundColor(Theme.textQuaternary)
        }
        .padding(11)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Theme.accentTint)
        .overlay(RoundedRectangle(cornerRadius: 8).stroke(Theme.accentTint2, lineWidth: 1))
        .clipShape(RoundedRectangle(cornerRadius: 8))
    }

    private var statusSubtitle: String {
        switch status {
        case .idle:
            return "点击同步到百度网盘"
        case .syncing:
            return "正在拉取云端并对比…"
        case .success(let date):
            let t = Self.timeFormatter.string(from: date)
            let extra = message.map { " · \($0)" } ?? ""
            return "上次同步 \(t)\(extra)"
        case .failed(let msg):
            return "同步失败：\(msg)"
        }
    }

    private var statusColor: Color {
        switch status {
        case .idle: return Theme.textTertiary
        case .syncing: return Theme.accent
        case .success: return Theme.textTertiary
        case .failed: return Theme.red
        }
    }

    private static let timeFormatter: DateFormatter = {
        let f = DateFormatter()
        f.dateFormat = "HH:mm"
        return f
    }()
}

/// 列表顶部固定的「任务」入口行：显示未完成数量，点击进入任务面板。
struct TodoEntryCard: View {
    @ObservedObject var todos: TodoStore

    var body: some View {
        HStack(spacing: 10) {
            ZStack {
                Circle()
                    .fill(Theme.accentTint)
                    .frame(width: 34, height: 34)
                Image(systemName: "checklist")
                    .font(.system(size: 15, weight: .semibold))
                    .foregroundColor(Theme.accent)
            }
            VStack(alignment: .leading, spacing: 2) {
                Text("任务")
                    .font(.system(size: 13.5, weight: .semibold))
                    .foregroundColor(Theme.textPrimary)
                Text("待完成 \(todos.pendingCount) · 进行中 \(todos.doingCount)")
                    .font(.system(size: 11))
                    .foregroundColor(Theme.textTertiary)
                    .lineLimit(1)
            }
            Spacer(minLength: 0)
            if todos.incompleteCount > 0 {
                Text("\(todos.incompleteCount)")
                    .font(.system(size: 11, weight: .semibold))
                    .foregroundColor(.white)
                    .frame(minWidth: 18, minHeight: 18)
                    .background(Theme.accent)
                    .clipShape(Circle())
            }
            Image(systemName: "chevron.right")
                .font(.system(size: 11, weight: .semibold))
                .foregroundColor(Theme.textQuaternary)
        }
        .padding(11)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Theme.accentTint)
        .overlay(RoundedRectangle(cornerRadius: 8).stroke(Theme.accentTint2, lineWidth: 1))
        .clipShape(RoundedRectangle(cornerRadius: 8))
    }
}
