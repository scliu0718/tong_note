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
    @ObservedObject private var notebooks = NotebookStore.shared
    @State private var showAvatarPicker = false
    @State private var hoveredTag: String?
    @State private var newNotebookName = ""
    /// 侧栏所有弹窗统一走一个状态：多个 .alert 串在同一视图上时容易只有一个生效。
    @State private var activeDialog: SidebarDialog?

    private enum SidebarDialog {
        case deleteTag(String)
        case newNotebook
        case notebookError(String)
        case deleteNotebook(String)
    }

    private var isWide: Bool { width >= SidebarWidth.expandedThreshold }
    /// 极窄档：只显示图标，所有文字与计数交给 hover 浮窗。
    private var isTiny: Bool { width < SidebarWidth.compactThreshold }

    var body: some View {
        VStack(spacing: 0) {
            accountHeader
            newNoteButton
            ScrollView(showsIndicators: false) {
                VStack(alignment: .leading, spacing: 0) {
                    groupDivider

                    notebookItem(Notebooks.all, icon: "tray.full")
                    ForEach(notebooks.list, id: \.self) { nb in
                        notebookItem(nb, icon: notebookIcon(nb))
                    }

                    // 笔记本与标签之间的分割线：三档都显示，避免两组混在一起
                    sectionDivider

                    ForEach(store.allTags, id: \.self) { tag in
                        if isTiny {
                            tinyTagItem(tag)
                        } else {
                            tagItem(tag)
                        }
                    }
                    if store.allTags.isEmpty && !isTiny {
                        Text("暂无标签")
                            .font(.system(size: isWide ? 12 : 10))
                            .foregroundColor(Theme.textQuaternary)
                            .frame(maxWidth: .infinity)
                            .padding(.vertical, 8)
                    }
                }
                .padding(.horizontal, isWide ? 8 : (isTiny ? 6 : 10))
            }
            footerView
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Theme.sidebarBg)
        .alert(dialogTitle,
               isPresented: Binding(get: { activeDialog != nil },
                                    set: { if !$0 { activeDialog = nil } })) {
            if let dialog = activeDialog {
                switch dialog {
                case .newNotebook:
                    TextField("笔记本名称", text: $newNotebookName)
                    Button("创建") { createNotebook() }
                    Button("取消", role: .cancel) { newNotebookName = "" }
                case .deleteTag(let tag):
                    Button("取消", role: .cancel) {}
                    Button("删除标签", role: .destructive) { confirmDeleteTag(tag) }
                case .deleteNotebook(let name):
                    Button("取消", role: .cancel) {}
                    Button("删除该笔记本", role: .destructive) { confirmDeleteNotebook(name) }
                case .notebookError:
                    Button("好") {}
                }
            }
        } message: {
            Text(dialogMessage)
        }
    }

    private var dialogTitle: String {
        guard let dialog = activeDialog else { return "" }
        switch dialog {
        case .deleteTag(let tag): return "删除标签「\(tag)」？"
        case .newNotebook: return "新建笔记本"
        case .notebookError: return "无法新建笔记本"
        case .deleteNotebook(let name): return "删除笔记本「\(name)」？"
        }
    }

    private var dialogMessage: String {
        guard let dialog = activeDialog else { return "" }
        switch dialog {
        case .deleteTag: return "该标签会从所有笔记上移除，笔记本身不会被删除。"
        case .newNotebook: return "新笔记本会出现在笔记本列表中，编辑器里也能把笔记切换过去。"
        case .notebookError(let message): return message
        case .deleteNotebook(let name): return deleteNotebookMessage(name)
        }
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
                VStack(spacing: isTiny ? 4 : 6) {
                    avatar
                    Button(action: onSettings) {
                        Image(systemName: "gearshape")
                            .font(.system(size: isTiny ? 11 : 12))
                            .foregroundColor(Theme.textTertiary)
                    }
                    .buttonStyle(.plain).help("同步与存储设置")
                }
                .padding(.vertical, isTiny ? 8 : 12)
            }
        }
    }

    /// 可点击头像：点击弹出候选图标，更换后立即保存到本地并上传云端。
    private var avatar: some View {
        Button(action: { showAvatarPicker.toggle() }) {
            AvatarBadge(preset: avatarStore.current, size: isTiny ? 26 : 32)
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
                        .font(.system(size: isTiny ? 15 : 18, weight: .bold))
                        .foregroundColor(.white)
                        .frame(width: isTiny ? 34 : 44, height: isTiny ? 34 : 44)
                        .background(Theme.accent)
                        .clipShape(Circle())
                        .shadow(color: Theme.accent.opacity(0.28), radius: 5, y: 2)
                }
                .buttonStyle(.plain)
                .padding(.bottom, isTiny ? 10 : 14)
                .help("新建笔记")
            }
        }
    }

    private var groupDivider: some View {
        if isWide { return AnyView(EmptyView()) }
        return AnyView(Rectangle().fill(Theme.line).frame(height: 1)
            .padding(.vertical, isTiny ? 6 : 8).padding(.horizontal, isTiny ? 2 : 4))
    }

    /// 笔记本与标签之间的分隔线，三档都显示。
    private var sectionDivider: some View {
        Rectangle()
            .fill(Theme.line)
            .frame(height: 1)
            .padding(.horizontal, isWide ? 8 : (isTiny ? 2 : 4))
            .padding(.vertical, isWide ? 10 : 8)
    }

    private func createNotebook() {
        let name = newNotebookName
        newNotebookName = ""
        if let error = notebooks.add(name) {
            // 先关掉输入框弹窗，再单独展示错误，避免两个弹窗互相抢状态。
            activeDialog = nil
            DispatchQueue.main.async { activeDialog = .notebookError(error) }
            return
        }
        activeDialog = nil
    }

    private func confirmDeleteTag(_ tag: String) {
        store.removeTag(tag)
        if selectedTag == tag { selectedTag = nil }
        if hoveredTag == tag { hoveredTag = nil }
        activeDialog = nil
    }

    /// 删除笔记本：先把笔记转移到「全部笔记」，再从自建笔记本里移除。
    private func confirmDeleteNotebook(_ name: String) {
        // 双保险：即便弹窗状态被异常触发，也不允许删掉「全部笔记」这个兜底容器。
        guard name != Notebooks.all, notebooks.custom.contains(name) else {
            activeDialog = nil
            return
        }
        store.moveNotes(from: name, to: Notebooks.all)
        notebooks.remove(name)
        if selectedNotebook == name { selectedNotebook = Notebooks.all }
        activeDialog = nil
    }

    private func deleteNotebookMessage(_ name: String) -> String {
        let count = store.count(inNotebook: name)
        if count > 0 {
            return "该笔记本下有 \(count) 篇笔记，删除后会自动转移到「全部笔记」，笔记本身不会丢失。"
        }
        return "该笔记本下暂无笔记，删除后会从笔记本列表中移除。"
    }

    // MARK: 笔记本/标签项
    private func notebookItem(_ name: String, icon: String) -> some View {
        let active = !showingTrash && selectedTag == nil && selectedNotebook == name
        // 「全部笔记」是虚拟汇总项，也是删除其它笔记本时笔记的兜底归属，永远不可删除；
        // 内置四个笔记本同样不可删除，只有用户自建笔记本才提供「删除该笔记本」。
        let isProtected = name == Notebooks.all
        let isDeletable = !isProtected && notebooks.custom.contains(name)
        let count = store.count(inNotebook: name)
        let action = {
            selectedNotebook = name
            selectedTag = nil
            showingTrash = false
        }
        return Group {
            if isTiny {
                tinySideItem(title: name, icon: icon, count: count,
                             active: active, action: action)
            } else {
                sideItem(title: name, icon: icon, count: count,
                         active: active, action: action)
            }
        }
        .contextMenu {
            Button("新建笔记本") { activeDialog = .newNotebook }
            if isDeletable {
                Divider()
                Button("删除该笔记本", role: .destructive) { activeDialog = .deleteNotebook(name) }
            }
        }
    }

    /// 极窄档笔记本项：只显示图标，完整名称与计数交给 hover 浮窗。
    private func tinySideItem(title: String, icon: String, count: Int, active: Bool,
                              action: @escaping () -> Void) -> some View {
        Image(systemName: icon)
            .font(.system(size: 16))
            .foregroundColor(active ? Theme.accent : Theme.textTertiary)
            .frame(maxWidth: .infinity)
            .padding(.vertical, 9)
            .background(active ? Theme.accentTint : Color.clear)
            .clipShape(RoundedRectangle(cornerRadius: 7))
            .contentShape(Rectangle())
            .onTapGesture(perform: action)
            .help("\(title) · \(count) 篇笔记")
    }

    /// 极窄档标签项：只显示书页图标，hover 浮窗显示标签名与笔记数。
    private func tinyTagItem(_ tag: String) -> some View {
        let active = !showingTrash && selectedTag == tag
        let hovering = hoveredTag == tag
        return BookTagIcon(size: 15, color: active ? Theme.accent : Theme.textTertiary)
            .frame(maxWidth: .infinity)
            .padding(.vertical, 8)
            .background(active ? Theme.accentTint : (hovering ? Color(hex: 0xEFEFF1) : Color.clear))
            .clipShape(RoundedRectangle(cornerRadius: 7))
            .contentShape(Rectangle())
            .onTapGesture {
                selectedTag = tag
                selectedNotebook = Notebooks.all
                showingTrash = false
            }
            .onHover { hoveredTag = $0 ? tag : nil }
            .contextMenu {
                Button("删除标签", role: .destructive) { activeDialog = .deleteTag(tag) }
            }
            .help("标签 \(tag) · \(store.count(withTag: tag)) 篇笔记")
    }

    /// 标签项：图标 + 文字单行横排；删除只走右键菜单，保持行内简洁。
    private func tagItem(_ tag: String) -> some View {
        let active = !showingTrash && selectedTag == tag
        let hovering = hoveredTag == tag
        // 紧凑档下压缩图标、间距与内边距，让标签名能完整显示四个字
        return HStack(spacing: isWide ? 9 : 3) {
            // 按用户提供样式重绘的书页图标，取代原来的 tag 标签牌
            BookTagIcon(size: isWide ? 17 : 15,
                        color: active ? Theme.accent : Theme.textTertiary)
                .frame(width: isWide ? 19 : 15)
            Text(tag)
                .font(.system(size: isWide ? 13 : 11, weight: active ? .semibold : .regular))
                .foregroundColor(active ? Theme.accentHover : Theme.textSecondary)
                .lineLimit(1)
                // 直接吃掉剩余空间，省掉 Spacer 带来的两段额外间距
                .frame(maxWidth: .infinity, alignment: .leading)
            Text(verbatim: "\(store.count(withTag: tag))")
                .font(.system(size: isWide ? 11 : 10))
                .foregroundColor(Theme.textQuaternary)
                .fixedSize(horizontal: true, vertical: false)
        }
        .padding(.horizontal, isWide ? 8 : 6).padding(.vertical, isWide ? 7 : 6)
        .background(active ? Theme.accentTint : (hovering ? Color(hex: 0xEFEFF1) : Color.clear))
        .clipShape(RoundedRectangle(cornerRadius: 7))
        .contentShape(Rectangle())
        .onTapGesture {
            selectedTag = tag
            selectedNotebook = Notebooks.all
            showingTrash = false
        }
        .onHover { hoveredTag = $0 ? tag : nil }
        .contextMenu {
            Button("删除标签", role: .destructive) { activeDialog = .deleteTag(tag) }
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
            if isTiny {
                Image(systemName: "trash")
                    .font(.system(size: 16))
                    .foregroundColor(showingTrash ? Theme.accent : Theme.textTertiary)
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 9)
                    .background(showingTrash ? Theme.accentTint : Color.clear)
                    .clipShape(RoundedRectangle(cornerRadius: 7))
                    .contentShape(Rectangle())
                    .onTapGesture { showingTrash.toggle(); selectedTag = nil }
                    .help("回收站 · \(store.trashNotes.count) 篇笔记")
            } else if isWide {
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

            // 极窄档隐藏「本地存储 / N 篇笔记」两行文字
            if !isTiny { storageMeter }
        }
        .padding(.horizontal, isWide ? 8 : (isTiny ? 6 : 10))
        .padding(.vertical, 10)
        .background(Theme.sidebarBg)
    }

    private var storageMeter: some View {
        // 与「全部笔记」分类计数同口径：只算有效笔记，
        // 排除回收站里的笔记和「快同步」这条独立虚拟笔记。
        let count = store.count(inNotebook: Notebooks.all)
        return VStack(spacing: 0) {
            if isWide {
                Text("本地存储").font(.system(size: 11)).foregroundColor(Theme.textTertiary)
                    .frame(maxWidth: .infinity, alignment: .leading)
                HStack {
                    Text(verbatim: "\(count) 篇笔记").font(.system(size: 10)).foregroundColor(Theme.textQuaternary)
                    Spacer()
                    Text("本地").font(.system(size: 10)).foregroundColor(Theme.textQuaternary)
                }
                .padding(.top, 4)
            } else {
                // 窄侧栏原本只显示一个裸数字，容易被误读成回收站计数，补上「本地 N 篇」
                Text(verbatim: "本地 \(count) 篇")
                    .font(.system(size: 9))
                    .foregroundColor(Theme.textQuaternary)
                    .lineLimit(1)
            }
        }
        // 宽版：外层已有 8，再补 8 → 左内边距 16，与回收站图标（8+8）对齐
        .padding(.leading, isWide ? 8 : 0)
        .help("本地有效笔记数，不含回收站与「快同步」独立笔记")
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

// MARK: - 标签项图标（按用户提供样式重绘）

/// 圆角书页框：上边中部浅凹、下边中部向下凸出，四角为圆角。
/// 凹/凸用两段三次曲线拼接，保证与水平边相接处切线连续，不会形成生硬的折角。
private struct BookFrameShape: Shape {
    func path(in rect: CGRect) -> Path {
        let w = rect.width
        let h = rect.height
        let cx = w / 2
        // 参考图里外框偏方，圆角半径约为框宽的 15%
        let r = min(w, h) * 0.15
        let top = h * 0.05
        let bottom = h * 0.87
        let notchW = w * 0.30
        let notch = h * 0.085
        let shoulder = notchW * 0.30
        let waist = notchW * 0.20

        var p = Path()
        p.move(to: CGPoint(x: r, y: top))
        p.addLine(to: CGPoint(x: cx - notchW / 2, y: top))
        // 顶部凹口：左半段下沉、右半段回升，两端与中点切线均保持水平
        p.addCurve(to: CGPoint(x: cx, y: top + notch),
                   control1: CGPoint(x: cx - notchW / 2 + shoulder, y: top),
                   control2: CGPoint(x: cx - waist, y: top + notch))
        p.addCurve(to: CGPoint(x: cx + notchW / 2, y: top),
                   control1: CGPoint(x: cx + waist, y: top + notch),
                   control2: CGPoint(x: cx + notchW / 2 - shoulder, y: top))
        p.addLine(to: CGPoint(x: w - r, y: top))
        p.addQuadCurve(to: CGPoint(x: w, y: top + r), control: CGPoint(x: w, y: top))
        p.addLine(to: CGPoint(x: w, y: bottom - r))
        p.addQuadCurve(to: CGPoint(x: w - r, y: bottom), control: CGPoint(x: w, y: bottom))
        p.addLine(to: CGPoint(x: cx + notchW / 2, y: bottom))
        // 底部凸出：路径方向从右往左，与顶部镜像
        p.addCurve(to: CGPoint(x: cx, y: bottom + notch),
                   control1: CGPoint(x: cx + notchW / 2 - shoulder, y: bottom),
                   control2: CGPoint(x: cx + waist, y: bottom + notch))
        p.addCurve(to: CGPoint(x: cx - notchW / 2, y: bottom),
                   control1: CGPoint(x: cx - waist, y: bottom + notch),
                   control2: CGPoint(x: cx - notchW / 2 + shoulder, y: bottom))
        p.addLine(to: CGPoint(x: r, y: bottom))
        p.addQuadCurve(to: CGPoint(x: 0, y: bottom - r), control: CGPoint(x: 0, y: bottom))
        p.addLine(to: CGPoint(x: 0, y: top + r))
        p.addQuadCurve(to: CGPoint(x: r, y: top), control: CGPoint(x: 0, y: top))
        p.closeSubpath()
        return p
    }
}

/// 书页框 + 中间三条圆头竖线（中间长、两侧短且更浅）。
private struct BookTagIcon: View {
    let size: CGFloat
    let color: Color

    var body: some View {
        ZStack {
            BookFrameShape()
                .stroke(color, style: StrokeStyle(lineWidth: size * 0.10,
                                                  lineCap: .round,
                                                  lineJoin: .round))
                // 给描边与底部凸出留出余量，避免被 frame 裁掉
                .padding(size * 0.07)
            // 三条竖线紧凑排布：略粗一点更贴近参考图，间隙仍能把三条分开
            HStack(spacing: size * 0.07) {
                Capsule().fill(color.opacity(0.6))
                    .frame(width: size * 0.085, height: size * 0.24)
                Capsule().fill(color.opacity(0.6))
                    .frame(width: size * 0.085, height: size * 0.40)
                Capsule().fill(color.opacity(0.6))
                    .frame(width: size * 0.085, height: size * 0.24)
            }
        }
        .frame(width: size, height: size)
    }
}
