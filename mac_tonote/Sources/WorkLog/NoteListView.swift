import SwiftUI
import WorkLogCore

struct NoteListView: View {
    @ObservedObject var store: NoteStore
    @Binding var selectedID: UUID?
    @Binding var searchText: String
    @Binding var sortMode: SortMode
    let notes: [Note]
    let showingTrash: Bool

    @State private var confirmDeleteID: UUID?

    var body: some View {
        VStack(spacing: 0) {
            searchBar
            filterBar
            ScrollView(showsIndicators: false) {
                VStack(spacing: 8) {
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
                    if selectedID == id { selectedID = nil }
                }
                confirmDeleteID = nil
            }
        } message: {
            Text("此操作不可恢复。")
        }
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

    @ViewBuilder
    private func contextMenu(for note: Note) -> some View {
        if showingTrash {
            Button("恢复") { store.restore(note.id) }
            Divider()
            Button("彻底删除", role: .destructive) { confirmDeleteID = note.id }
        } else {
            Button(note.pinned ? "取消置顶" : "置顶") { store.togglePin(note.id) }
            Divider()
            Button("删除", role: .destructive) {
                store.moveToTrash(note.id)
                if selectedID == note.id { selectedID = nil }
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
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(.horizontal, 14).padding(.bottom, 8)
        .overlay(Rectangle().fill(Theme.lineSoft).frame(height: 1), alignment: .bottom)
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
                        .background(selected ? Theme.accentTint2 : Color(hex: 0xEFEFF1))
                        .clipShape(Capsule())
                }
                Spacer(minLength: 0)
            }
        }
        .padding(11)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(selected ? Theme.accentTint : Color.clear)
        .overlay(RoundedRectangle(cornerRadius: 8).stroke(selected ? Theme.accentTint2 : Color.clear, lineWidth: 1))
        .clipShape(RoundedRectangle(cornerRadius: 8))
    }
}
