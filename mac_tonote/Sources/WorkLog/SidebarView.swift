import SwiftUI
import WorkLogCore

struct SidebarView: View {
    @Binding var width: CGFloat
    @ObservedObject var store: NoteStore
    @Binding var selectedNotebook: String
    @Binding var selectedTag: String?
    @Binding var showingTrash: Bool
    var onNewNote: () -> Void
    var onSettings: () -> Void

    @ObservedObject private var avatarStore = AvatarStore.shared
    @State private var showAvatarPicker = false

    private var isWide: Bool { width >= 200 }

    var body: some View {
        VStack(spacing: 0) {
            accountHeader
            newNoteButton
            ScrollView(showsIndicators: false) {
                VStack(alignment: .leading, spacing: 0) {
                    groupDivider

                    notebookItem(Notebooks.all, icon: "tray.full")
                    ForEach(Notebooks.list, id: \.self) { nb in
                        notebookItem(nb, icon: notebookIcon(nb))
                    }

                    groupDivider

                    ForEach(store.allTags, id: \.self) { tag in
                        tagItem(tag)
                    }
                    if store.allTags.isEmpty {
                        Text("暂无标签")
                            .font(.system(size: isWide ? 12 : 10))
                            .foregroundColor(Theme.textQuaternary)
                            .frame(maxWidth: .infinity)
                            .padding(.vertical, 8)
                    }
                }
                .padding(.horizontal, isWide ? 8 : 10)
            }
            footerView
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Theme.sidebarBg)
    }

    private func notebookIcon(_ name: String) -> String {
        switch name {
        case "工作日志": return "square.and.pencil"
        case "读书笔记": return "book.closed"
        case "投资研究": return "chart.line.uptrend.xyaxis"
        case "灵感速记": return "lightbulb"
        default: return "folder"
        }
    }

    // MARK: 账号区
    private var accountHeader: some View {
        Group {
            if isWide {
                HStack(spacing: 9) {
                    avatar
                    VStack(alignment: .leading, spacing: 1) {
                        Text("老王的工作台").font(.system(size: 12, weight: .semibold))
                        HStack(spacing: 4) {
                            Circle().fill(Theme.accent).frame(width: 6, height: 6)
                            Text("已保存到本地").font(.system(size: 11)).foregroundColor(Theme.textTertiary)
                        }
                    }
                    Spacer()
                    Button(action: onSettings) {
                        Image(systemName: "gearshape").font(.system(size: 13)).foregroundColor(Theme.textTertiary)
                    }
                    .buttonStyle(.plain).help("同步与存储设置")
                }
                .padding(.horizontal, 14).padding(.vertical, 12)
            } else {
                VStack(spacing: 6) {
                    avatar
                    Button(action: onSettings) {
                        Image(systemName: "gearshape").font(.system(size: 12)).foregroundColor(Theme.textTertiary)
                    }
                    .buttonStyle(.plain).help("同步与存储设置")
                }
                .padding(.vertical, 12)
            }
        }
    }

    /// 可点击头像：点击弹出候选图标，更换后立即保存到本地并上传云端。
    private var avatar: some View {
        Button(action: { showAvatarPicker.toggle() }) {
            AvatarBadge(preset: avatarStore.current, size: 32)
        }
        .buttonStyle(.plain)
        .help("更换头像")
        .popover(isPresented: $showAvatarPicker, arrowEdge: .bottom) {
            avatarPicker
        }
        .task { await avatarStore.pullFromCloudIfNeeded() }
    }

    private var avatarPicker: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("选择头像")
                .font(.system(size: 13, weight: .semibold))
                .foregroundColor(Theme.textPrimary)
            LazyVGrid(columns: Array(repeating: GridItem(.fixed(44), spacing: 12), count: 4), spacing: 12) {
                ForEach(AvatarStore.presets) { preset in
                    Button(action: { avatarStore.select(preset.id) }) {
                        ZStack(alignment: .bottomTrailing) {
                            AvatarBadge(preset: preset, size: 42)
                            if avatarStore.currentID == preset.id {
                                Image(systemName: "checkmark.circle.fill")
                                    .font(.system(size: 13))
                                    .foregroundColor(Theme.accent)
                                    .background(Circle().fill(Color.white))
                            }
                        }
                    }
                    .buttonStyle(.plain)
                    .help(preset.id == "tong" ? "同笔记（默认）" : preset.id)
                }
            }
            Divider()
            HStack(spacing: 5) {
                Image(systemName: cloudIcon).font(.system(size: 11))
                Text(cloudText).font(.system(size: 11)).lineLimit(2)
            }
            .foregroundColor(Theme.textTertiary)
        }
        .padding(16)
        .frame(width: 244)
    }

    private var cloudIcon: String {
        switch avatarStore.cloudState {
        case .idle: return "icloud"
        case .uploading: return "icloud.and.arrow.up"
        case .synced: return "checkmark.icloud"
        case .failed: return "exclamationmark.icloud"
        }
    }

    private var cloudText: String {
        switch avatarStore.cloudState {
        case .idle: return "更换后自动上传到百度网盘"
        case .uploading: return "正在上传到百度网盘…"
        case .synced: return "已同步到百度网盘"
        case .failed(let msg): return "上传失败（已保存到本地）：\(msg)"
        }
    }

    private var newNoteButton: some View {
        Group {
            if isWide {
                Button(action: onNewNote) {
                    HStack(spacing: 6) {
                        Image(systemName: "plus").font(.system(size: 13, weight: .bold))
                        Text("新建笔记").font(.system(size: 13, weight: .semibold))
                    }
                    .foregroundColor(.white)
                    .frame(maxWidth: .infinity).frame(height: 34)
                    .background(Theme.accent)
                    .clipShape(RoundedRectangle(cornerRadius: 8))
                }
                .buttonStyle(.plain)
                .padding(.horizontal, 12).padding(.bottom, 12)
            } else {
                Button(action: onNewNote) {
                    Image(systemName: "plus")
                        .font(.system(size: 18, weight: .bold))
                        .foregroundColor(.white)
                        .frame(width: 44, height: 44)
                        .background(Theme.accent)
                        .clipShape(Circle())
                        .shadow(color: Theme.accent.opacity(0.28), radius: 5, y: 2)
                }
                .buttonStyle(.plain).padding(.bottom, 14).help("新建笔记")
            }
        }
    }

    private var groupDivider: some View {
        if isWide { return AnyView(EmptyView()) }
        return AnyView(Rectangle().fill(Theme.line).frame(height: 1).padding(.vertical, 8).padding(.horizontal, 4))
    }

    // MARK: 笔记本/标签项
    private func notebookItem(_ name: String, icon: String) -> some View {
        let active = !showingTrash && selectedTag == nil && selectedNotebook == name
        return sideItem(title: name, icon: icon, count: store.count(inNotebook: name), active: active) {
            selectedNotebook = name
            selectedTag = nil
            showingTrash = false
        }
    }

    private func tagItem(_ tag: String) -> some View {
        let active = !showingTrash && selectedTag == tag
        return sideItem(title: tag, icon: "tag", count: store.count(withTag: tag), active: active) {
            selectedTag = tag
            selectedNotebook = Notebooks.all
            showingTrash = false
        }
    }

    private func sideItem(title: String, icon: String, count: Int, active: Bool,
                          action: @escaping () -> Void) -> some View {
        Group {
            if isWide {
                HStack(spacing: 9) {
                    Image(systemName: icon).font(.system(size: 14))
                        .foregroundColor(active ? Theme.accent : Theme.textTertiary).frame(width: 18)
                    Text(title)
                        .font(.system(size: 13, weight: active ? .semibold : .regular))
                        .foregroundColor(active ? Theme.accentHover : Theme.textSecondary)
                    Spacer()
                    Text("\(count)").font(.system(size: 11)).foregroundColor(Theme.textQuaternary)
                }
                .padding(.horizontal, 8).padding(.vertical, 7)
                .background(active ? Theme.accentTint : Color.clear)
                .clipShape(RoundedRectangle(cornerRadius: 7))
                .contentShape(Rectangle())
                .onTapGesture(perform: action)
            } else {
                VStack(spacing: 4) {
                    Image(systemName: icon).font(.system(size: 18))
                        .foregroundColor(active ? Theme.accent : Theme.textTertiary)
                    Text(title).font(.system(size: 10)).lineLimit(1)
                        .foregroundColor(active ? Theme.accentHover : Theme.textTertiary)
                }
                .frame(maxWidth: .infinity).padding(.vertical, 9)
                .background(active ? Theme.accentTint : Color.clear)
                .clipShape(RoundedRectangle(cornerRadius: 8))
                .contentShape(Rectangle())
                .onTapGesture(perform: action)
            }
        }
    }

    // MARK: 底部
    private var footerView: some View {
        VStack(spacing: 0) {
            if isWide {
                HStack(spacing: 9) {
                    Image(systemName: "trash").font(.system(size: 14))
                        .foregroundColor(showingTrash ? Theme.accent : Theme.textTertiary)
                    Text("回收站")
                        .font(.system(size: 13, weight: showingTrash ? .semibold : .regular))
                        .foregroundColor(showingTrash ? Theme.accentHover : Theme.textSecondary)
                    Spacer()
                    Text("\(store.trashNotes.count)").font(.system(size: 11)).foregroundColor(Theme.textQuaternary)
                }
                .padding(.horizontal, 8).padding(.vertical, 7)
                .background(showingTrash ? Theme.accentTint : Color.clear)
                .clipShape(RoundedRectangle(cornerRadius: 7))
                .contentShape(Rectangle())
                .onTapGesture { showingTrash.toggle(); selectedTag = nil }
            } else {
                VStack(spacing: 4) {
                    Image(systemName: "trash").font(.system(size: 18))
                        .foregroundColor(showingTrash ? Theme.accent : Theme.textTertiary)
                    Text("回收站").font(.system(size: 10))
                        .foregroundColor(showingTrash ? Theme.accentHover : Theme.textTertiary)
                }
                .frame(maxWidth: .infinity).padding(.vertical, 9)
                .background(showingTrash ? Theme.accentTint : Color.clear)
                .clipShape(RoundedRectangle(cornerRadius: 8))
                .contentShape(Rectangle())
                .onTapGesture { showingTrash.toggle(); selectedTag = nil }
            }

            storageMeter
        }
        .padding(.horizontal, isWide ? 8 : 10)
        .padding(.vertical, 10)
        .background(Theme.sidebarBg)
    }

    private var storageMeter: some View {
        VStack(spacing: 0) {
            if isWide {
                Text("本地存储").font(.system(size: 11)).foregroundColor(Theme.textTertiary)
                    .frame(maxWidth: .infinity, alignment: .leading)
                HStack {
                    Text("\(store.notes.count) 篇笔记").font(.system(size: 10)).foregroundColor(Theme.textQuaternary)
                    Spacer()
                    Text("本地").font(.system(size: 10)).foregroundColor(Theme.textQuaternary)
                }
                .padding(.top, 4)
            } else {
                Text("\(store.notes.count)").font(.system(size: 10)).foregroundColor(Theme.textQuaternary)
            }
        }
    }
}

/// 圆形渐变头像徽标：文字或 SF Symbol。
private struct AvatarBadge: View {
    let preset: AvatarPreset
    var size: CGFloat = 32

    var body: some View {
        ZStack {
            Circle()
                .fill(LinearGradient(colors: [Color(hex: preset.color1), Color(hex: preset.color2)],
                                     startPoint: .topLeading, endPoint: .bottomTrailing))
            Group {
                if preset.isSymbol {
                    Image(systemName: preset.glyph)
                        .font(.system(size: size * 0.46, weight: .semibold))
                } else {
                    Text(preset.glyph)
                        .font(.system(size: size * 0.42, weight: .bold))
                }
            }
            .foregroundColor(.white)
        }
        .frame(width: size, height: size)
        .clipShape(Circle())
    }
}
