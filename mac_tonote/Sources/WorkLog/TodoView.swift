import SwiftUI
import WorkLogCore
import WorkLogRichText

/// 任务面板：待完成 / 进行中 / 已完成 / 终止 四组，支持折叠、勾选、流转、删除、编辑备注与日期。
/// 任何任务变更后防抖自动同步到百度网盘。
struct TodoView: View {
    @ObservedObject var store: TodoStore
    @ObservedObject var noteStore: NoteStore
    @EnvironmentObject private var sync: SyncEngine

    @State private var newTitle = ""
    @State private var expandedID: UUID?
    /// 展开编辑期间冻结的列表顺序：避免一改截止时间就立刻跳到组内第一条
    @State private var frozenIDs: [UUID]?
    @State private var hoveredID: UUID?
    @State private var filter: Filter = .all
    @State private var showCalendar = false
    @State private var collapsed: Set<String> = []
    @State private var autoSyncTask: Task<Void, Never>?
    @StateObject private var notesController = RichTextEditorController()
    @State private var imageError: String?

    private enum Filter: String, CaseIterable, Identifiable {
        case all = "全部"
        case incomplete = "仅未完成"
        var id: String { rawValue }
    }

    var body: some View {
        VStack(spacing: 0) {
            header
            ScrollView(showsIndicators: false) {
                // 分组之间的间距：由 16 提升到 32（两倍），层次更清晰
                VStack(alignment: .leading, spacing: 32) {
                    if store.todos.isEmpty {
                        emptyState
                    } else {
                        group("待完成", color: Theme.red, items: displayItems(store.pending),
                              showStart: true, showTerminate: true)
                        group("进行中", color: Theme.amber, items: displayItems(store.doing),
                              showComplete: true, showTerminate: true)
                        if filter == .all {
                            group("已完成", color: Theme.accent, items: displayItems(store.done))
                            group("已终止", color: Theme.textTertiary,
                                  items: displayItems(store.terminated), showRestore: true)
                        }
                    }
                }
                .padding(.horizontal, 22).padding(.vertical, 20)
            }
            .background(Theme.panelBg)
            quickAddBar
        }
        .background(Theme.panelBg)
        .alert("无法插入图片", isPresented: Binding(
            get: { imageError != nil },
            set: { if !$0 { imageError = nil } }
        )) {
            Button("好", role: .cancel) { imageError = nil }
        } message: {
            Text(imageError ?? "")
        }
    }

    // MARK: 头部
    private var header: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(alignment: .center) {
                VStack(alignment: .leading, spacing: 4) {
                    Text("任务")
                        .font(.system(size: 20, weight: .bold))
                        .foregroundColor(Theme.textPrimary)
                    Text(statText)
                        .font(.system(size: 12))
                        .foregroundColor(Theme.textTertiary)
                }
                Spacer()

                Picker("", selection: $filter) {
                    ForEach(Filter.allCases) { f in
                        Text(f.rawValue).tag(f)
                    }
                }
                .labelsHidden()
                .pickerStyle(.menu)
                .fixedSize(horizontal: true, vertical: false)

                syncButton
            }
            // 原「清空已完成」入口移除，改为常驻显示最近一次成功同步的时间
            HStack {
                Spacer()
                Text(lastSyncText)
                    .font(.system(size: 11))
                    .foregroundColor(Theme.textQuaternary)
                    .help("最近一次成功同步到百度网盘的时间")
            }
        }
        .padding(.horizontal, 22).padding(.top, 18).padding(.bottom, 12)
        .overlay(Rectangle().fill(Theme.lineSoft).frame(height: 1), alignment: .bottom)
    }

    private var lastSyncText: String {
        guard let date = sync.lastSyncDate else { return "尚未同步" }
        return "上次同步 \(Self.syncTimeFormatter.string(from: date))"
    }

    private static let syncTimeFormatter: DateFormatter = {
        let f = DateFormatter()
        f.dateFormat = "MM-dd HH:mm"
        return f
    }()

    /// 与「快同步」一致的绿色同步按钮，触发全量同步（笔记 + 任务）。
    private var syncButton: some View {
        Button(action: {
            autoSyncTask?.cancel()
            Task { await sync.quickSync(store: noteStore, todos: store) }
        }) {
            HStack(spacing: 4) {
                if sync.status.isSyncing {
                    ProgressView()
                        .controlSize(.small)
                        .scaleEffect(0.6)
                } else {
                    Image(systemName: isSyncFailed ? "exclamationmark.triangle" : "arrow.triangle.2.circlepath")
                        .font(.system(size: 12, weight: .semibold))
                }
                Text(sync.status.isSyncing ? "同步中" : "同步")
                    .font(.system(size: 12, weight: .semibold))
            }
            .lineLimit(1)
            .fixedSize(horizontal: true, vertical: false)
            .foregroundColor(.white)
            // 与「快同步」篇右上角的同步按钮完全一致：h10 / v4 / 12pt / 圆角 6
            .padding(.horizontal, 10).padding(.vertical, 4)
            .background(isSyncFailed ? Theme.red : Theme.accent)
            .clipShape(RoundedRectangle(cornerRadius: 6))
        }
        .buttonStyle(.plain)
        .disabled(sync.status.isSyncing)
        .help("同步全部笔记与任务到百度网盘")
    }

    private var isSyncFailed: Bool {
        if case .failed = sync.status { return true }
        return false
    }

    private var statText: String {
        var parts = ["待完成 \(store.pendingCount)", "进行中 \(store.doingCount)", "已完成 \(store.doneCount)"]
        if store.terminatedCount > 0 { parts.append("终止 \(store.terminatedCount)") }
        if store.overdueCount > 0 { parts.append("逾期 \(store.overdueCount)") }
        return parts.joined(separator: " · ")
    }

    // MARK: 分组（可折叠）
    private func group(_ title: String, color: Color, items: [Todo],
                       showStart: Bool = false, showComplete: Bool = false,
                       showTerminate: Bool = false, showRestore: Bool = false) -> some View {
        let isCollapsed = collapsed.contains(title)
        return VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 9) {
                ZStack {
                    Circle().fill(color.opacity(0.13)).frame(width: 20, height: 20)
                    Circle().fill(color).frame(width: 8, height: 8)
                }
                Text(title)
                    .font(.system(size: 15, weight: .semibold))
                    .foregroundColor(color)
                Text(verbatim: "\(items.count)")
                    .font(.system(size: 11, weight: .semibold))
                    .foregroundColor(color)
                    .padding(.horizontal, 7).padding(.vertical, 2)
                    .background(color.opacity(0.10))
                    .clipShape(Capsule())
                // 横线用该组主题色，折叠图标置于横线正中间
                HStack(spacing: 7) {
                    Rectangle().fill(color.opacity(0.45)).frame(height: 1)
                    Image(systemName: isCollapsed ? "chevron.right" : "chevron.down")
                        .font(.system(size: 9, weight: .semibold))
                        .foregroundColor(color)
                    Rectangle().fill(color.opacity(0.45)).frame(height: 1)
                }
                .frame(maxWidth: .infinity)
                .padding(.leading, 6)
            }
            .contentShape(Rectangle())
            .onTapGesture { toggleCollapse(title) }

            if !isCollapsed {
                if items.isEmpty {
                    HStack(spacing: 6) {
                        Image(systemName: "tray")
                            .font(.system(size: 11))
                            .foregroundColor(Theme.textQuaternary)
                        Text("暂无任务")
                            .font(.system(size: 12))
                            .foregroundColor(Theme.textQuaternary)
                    }
                    .padding(.leading, 29)
                    .padding(.vertical, 6)
                } else {
                    ForEach(items) { todo in
                        todoRow(todo, showStart: showStart, showComplete: showComplete,
                                showTerminate: showTerminate, showRestore: showRestore)
                    }
                }
            }
        }
    }

    private func toggleCollapse(_ title: String) {
        withAnimation(.easeInOut(duration: 0.15)) {
            if collapsed.contains(title) { collapsed.remove(title) }
            else { collapsed.insert(title) }
        }
    }

    // MARK: 任务行
    @ViewBuilder
    private func todoRow(_ todo: Todo, showStart: Bool, showComplete: Bool,
                         showTerminate: Bool, showRestore: Bool) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            // spacing 11→6：标题与进度圆圈之间的间距缩小一半
            HStack(spacing: 6) {
                checkbox(for: todo)
                VStack(alignment: .leading, spacing: 3) {
                    Text(todo.title)
                        .font(.system(size: 13, weight: .medium))
                        .foregroundColor(rowTitleColor(todo))
                        .strikethrough(todo.status == .done, color: Theme.textQuaternary)
                        .lineLimit(2)
                    HStack(spacing: 6) {
                        let preview = notesPreview(todo.notes)
                        if !preview.isEmpty {
                            Text(preview)
                                .font(.system(size: 11))
                                .foregroundColor(Theme.textTertiary)
                                .lineLimit(1)
                        }
                        if todo.status == .done, let done = todo.completedAt {
                            Text("完成于 \(Self.dateFmt.string(from: done))")
                                .font(.system(size: 11)).foregroundColor(Theme.textQuaternary)
                        } else if todo.status == .doing, let start = todo.startedAt {
                            Text("开始于 \(Self.dateTimeFmt.string(from: start))")
                                .font(.system(size: 11)).foregroundColor(Theme.textTertiary)
                        } else if todo.status == .terminated {
                            Text("已终止")
                                .font(.system(size: 11)).foregroundColor(Theme.textQuaternary)
                        }
                        Spacer(minLength: 0)
                    }
                }
                Spacer(minLength: 0)

                if let planned = todo.plannedAt {
                    Text(Self.dateFmt.string(from: planned))
                        .font(.system(size: 11, weight: .medium))
                        .foregroundColor(todo.isOverdue ? Theme.red : Theme.textTertiary)
                        .padding(.horizontal, 8).padding(.vertical, 3)
                        .background(todo.isOverdue ? Color(hex: 0xFCEBEB) : Color(hex: 0xF2F2F4))
                        .clipShape(Capsule())
                }

                if todo.status != .done {
                    if showStart {
                        SmallActionButton(title: "开始", tint: Theme.textSecondary) {
                            mutate { store.start(todo.id) }
                        }
                    }
                    if showComplete {
                        SmallActionButton(title: "完成", tint: Theme.accent) {
                            mutate { store.complete(todo.id) }
                        }
                    }
                }
                if showTerminate {
                    SmallActionButton(title: "终止", tint: Theme.textQuaternary) {
                        mutate { store.terminate(todo.id) }
                    }
                }
                if showRestore {
                    SmallActionButton(title: "恢复", tint: Theme.accent) {
                        mutate { store.restore(todo.id) }
                    }
                }

                Button(action: { toggleExpand(todo.id) }) {
                    Image(systemName: expandedID == todo.id ? "chevron.up" : "chevron.down")
                        .font(.system(size: 10, weight: .semibold))
                        .foregroundColor(Theme.textQuaternary)
                        .frame(width: 20, height: 20)
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)

                Button(action: { mutate { store.remove(todo.id) } }) {
                    Image(systemName: "trash")
                        .font(.system(size: 12))
                        .foregroundColor(Theme.textQuaternary)
                        .frame(width: 22, height: 22)
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain).help("删除任务")
            }
            // 左内边距 26 + checkbox 内圆圈偏移 2.5 ≈ 29，正好对齐分类标题「待完成」的「待」字
            .padding(.leading, 26).padding(.trailing, 8).padding(.vertical, 9)
            .background(hoveredID == todo.id ? Color(hex: 0xF7FAF8) : Color.clear)
            .contentShape(Rectangle())
            .onHover { inside in
                if inside { hoveredID = todo.id }
                else if hoveredID == todo.id { hoveredID = nil }
            }
            .onTapGesture { toggleExpand(todo.id) }

            if expandedID == todo.id {
                detail(todo)
                    .padding(.leading, 26).padding(.trailing, 8)
                    .padding(.bottom, 10)
            }
        }
        .overlay(Rectangle().fill(Theme.lineSoft).frame(height: 1), alignment: .bottom)
    }

    private func rowTitleColor(_ todo: Todo) -> Color {
        switch todo.status {
        case .done, .terminated: return Theme.textQuaternary
        default: return Theme.textPrimary
        }
    }

    private func toggleExpand(_ id: UUID) {
        withAnimation(.easeInOut(duration: 0.14)) {
            if expandedID == id {
                finishEditing()
            } else {
                expandedID = id
                frozenIDs = currentOrder()
            }
        }
    }

    /// 展开那一刻的全局任务顺序，用于编辑期间保持行位置不动。
    private func currentOrder() -> [UUID] {
        (store.pending + store.doing + store.done + store.terminated).map(\.id)
    }

    /// 收起 / 保存：解除顺序冻结，让新的截止时间、备注等重新参与排序（置顶），并同步。
    private func finishEditing() {
        expandedID = nil
        frozenIDs = nil
        scheduleAutoSync()
    }

    /// 编辑期间按冻结顺序渲染；未冻结时直接用仓库的排序结果。
    private func displayItems(_ items: [Todo]) -> [Todo] {
        guard let frozen = frozenIDs else { return items }
        var rank: [UUID: Int] = [:]
        for (i, id) in frozen.enumerated() { rank[id] = i }
        return items.sorted { (rank[$0.id] ?? Int.max) < (rank[$1.id] ?? Int.max) }
    }

    private func checkbox(for todo: Todo) -> some View {
        let border: Color = todo.status == .doing ? Theme.amber
            : (todo.status == .done ? Theme.accent : Color(hex: 0xC7C7CC))
        return Button(action: { mutate { store.toggleDone(todo.id) } }) {
            ZStack {
                if todo.status == .done {
                    Circle().fill(Theme.accent).frame(width: 17, height: 17)
                    Image(systemName: "checkmark")
                        .font(.system(size: 9, weight: .bold))
                        .foregroundColor(.white)
                } else {
                    Circle().stroke(border, lineWidth: 1.5).frame(width: 17, height: 17)
                    if todo.status == .terminated {
                        Rectangle().fill(Theme.textQuaternary).frame(width: 7, height: 1.5)
                    }
                }
            }
            .frame(width: 22, height: 22)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help(todo.status == .done ? "取消完成" : "标记完成")
    }

    // MARK: 展开编辑
    private func detail(_ todo: Todo) -> some View {
        VStack(alignment: .leading, spacing: 14) {
            VStack(alignment: .leading, spacing: 6) {
                HStack(spacing: 8) {
                    sectionLabel("备注")
                    // 「最近修改」用极淡的灰色，不再用蓝色抢视线
                    Text("最近修改 \(Self.createdFmt.string(from: todo.updatedAt))")
                        .font(.system(size: 10.5))
                        .foregroundColor(Theme.textQuaternary)
                    Spacer(minLength: 0)
                    Button(action: { insertImage(into: todo) }) {
                        Image(systemName: "photo")
                            .font(.system(size: 12, weight: .medium))
                            .foregroundColor(Theme.textSecondary)
                            .frame(width: 22, height: 22)
                            .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .help("插入图片")
                }
                RichTextEditor(noteID: todo.id,
                               content: notesContentBinding(todo),
                               controller: notesController,
                               attachments: noteStore.attachments,
                               onEditorError: { imageError = $0 },
                               textInset: CGSize(width: 8, height: 6))
                    .id(todo.id)
                    .frame(height: 120)
                    .background(Color.white)
                    .clipShape(RoundedRectangle(cornerRadius: 8))
                    .overlay(RoundedRectangle(cornerRadius: 8).stroke(Theme.line, lineWidth: 1))
            }

            deadlineSection(todo)

            if todo.status == .done, let done = todo.completedAt {
                Text("完成时间：\(Self.dateTimeFmt.string(from: done))")
                    .font(.system(size: 11)).foregroundColor(Theme.textQuaternary)
            }
        }
        .padding(14)
        .background(
            RoundedRectangle(cornerRadius: 10)
                .fill(Color(hex: 0xF8FCFA))
                .overlay(RoundedRectangle(cornerRadius: 10).stroke(Theme.accentTint2.opacity(0.65), lineWidth: 1))
                .shadow(color: Color.black.opacity(0.04), radius: 6, x: 0, y: 2)
        )
    }

    private func sectionLabel(_ text: String) -> some View {
        Text(text)
            .font(.system(size: 11, weight: .semibold))
            .foregroundColor(Theme.textTertiary)
            .kerning(0.3)
    }

    // MARK: 截止时间（全部收进一行：保存 + 设置截止时间 + 紧凑年月日 + 日历）
    @ViewBuilder
    private func deadlineSection(_ todo: Todo) -> some View {
        HStack(spacing: 9) {
            Button("保存") { finishEditing() }
            .buttonStyle(.plain)
            .font(.system(size: 12, weight: .semibold))
            .foregroundColor(Theme.accent)
            .help("保存并收起")

            plannedToggle(todo)

            // 原来是独立第二行的三个大步进框，现缩小后紧跟「设置截止时间」
            if todo.plannedAt != nil {
                let c = components(of: todo)
                HStack(spacing: 5) {
                    miniStepField("年", value: c.year, range: 2000...2100, wrap: false) {
                        setDate(todo, y: $0, m: c.month, d: min(c.day, daysIn(y: $0, m: c.month)))
                    }
                    miniStepField("月", value: c.month, range: 1...12, wrap: true) {
                        setDate(todo, y: c.year, m: $0, d: min(c.day, daysIn(y: c.year, m: $0)))
                    }
                    miniStepField("日", value: c.day, range: 1...daysIn(y: c.year, m: c.month), wrap: false) {
                        setDate(todo, y: c.year, m: c.month, d: $0)
                    }
                    calendarButton(todo)
                }
            }
            Spacer(minLength: 0)
        }
    }

    /// 自定义主题色勾选框（替代系统蓝色 checkbox，与整体配色统一）
    private func plannedToggle(_ todo: Todo) -> some View {
        let on = todo.plannedAt != nil
        return Button(action: {
            mutate {
                if var t = store.todo(with: todo.id) {
                    t.plannedAt = on ? nil : Date()
                    store.update(t)
                }
            }
        }) {
            HStack(spacing: 8) {
                ZStack {
                    RoundedRectangle(cornerRadius: 4)
                        .stroke(on ? Theme.accent : Color(hex: 0xC7C7CC), lineWidth: 1.5)
                        .frame(width: 15, height: 15)
                    if on {
                        RoundedRectangle(cornerRadius: 4).fill(Theme.accent).frame(width: 15, height: 15)
                        Image(systemName: "checkmark")
                            .font(.system(size: 9, weight: .bold))
                            .foregroundColor(.white)
                    }
                }
                Text("设置截止时间")
                    .font(.system(size: 12, weight: .medium))
                    .foregroundColor(Theme.textSecondary)
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }

    private func calendarButton(_ todo: Todo) -> some View {
        Button(action: { showCalendar.toggle() }) {
            HStack(spacing: 4) {
                Image(systemName: "calendar").font(.system(size: 10, weight: .semibold))
                Text("日历").font(.system(size: 10, weight: .semibold))
            }
            .foregroundColor(Theme.accent)
            .padding(.horizontal, 8).padding(.vertical, 4)
            .background(
                RoundedRectangle(cornerRadius: 6)
                    .fill(Color.white)
                    .overlay(RoundedRectangle(cornerRadius: 6).stroke(Theme.accentTint2, lineWidth: 1))
            )
        }
        .buttonStyle(.plain)
        .help("在日历中选择日期")
        .popover(isPresented: $showCalendar, arrowEdge: .bottom) {
            TodoCalendarPopover(
                selected: todo.plannedAt ?? Date(),
                onPick: { y, m, d in setDate(todo, y: y, m: m, d: d) },
                onClose: { showCalendar = false }
            )
        }
    }

    private func components(of todo: Todo) -> (year: Int, month: Int, day: Int) {
        let d = todo.plannedAt ?? Date()
        let c = Calendar.current.dateComponents([.year, .month, .day], from: d)
        return (c.year ?? 2026, c.month ?? 1, c.day ?? 1)
    }

    private func daysIn(y: Int, m: Int) -> Int {
        guard let d = Calendar.current.date(from: DateComponents(year: y, month: m)),
              let r = Calendar.current.range(of: .day, in: .month, for: d) else { return 31 }
        return r.count
    }

    private func setDate(_ todo: Todo, y: Int, m: Int, d: Int) {
        guard var t = store.todo(with: todo.id) else { return }
        var c = DateComponents()
        c.year = y; c.month = m; c.day = d; c.hour = 12; c.minute = 0
        if let nd = Calendar.current.date(from: c) { t.plannedAt = nd }
        store.update(t)
        scheduleAutoSync()
    }

    /// 紧凑版年月日步进框：与「设置截止时间」同排，不再独占一行。
    private func miniStepField(_ label: String, value: Int, range: ClosedRange<Int>, wrap: Bool,
                               onChange: @escaping (Int) -> Void) -> some View {
        HStack(spacing: 2) {
            Text(label)
                .font(.system(size: 9, weight: .medium))
                .foregroundColor(Theme.textQuaternary)
            MiniStepButton(systemName: "minus") { onChange(step(value, -1, range: range, wrap: wrap)) }
            // 用 verbatim 避免 LocalizedStringKey 把 2028 格式化成 "2,028"
            Text(verbatim: "\(value)")
                .font(.system(size: 10.5, weight: .semibold, design: .monospaced))
                .foregroundColor(Theme.textPrimary)
                .frame(minWidth: label == "年" ? 32 : 20)
            MiniStepButton(systemName: "plus") { onChange(step(value, 1, range: range, wrap: wrap)) }
        }
        .padding(.horizontal, 5).padding(.vertical, 2)
        .background(
            RoundedRectangle(cornerRadius: 6)
                .fill(Color.white)
                .overlay(RoundedRectangle(cornerRadius: 6).stroke(Theme.line, lineWidth: 1))
        )
    }

    private func step(_ value: Int, _ delta: Int, range: ClosedRange<Int>, wrap: Bool) -> Int {
        let v = value + delta
        if v < range.lowerBound { return wrap ? range.upperBound : range.lowerBound }
        if v > range.upperBound { return wrap ? range.lowerBound : range.upperBound }
        return v
    }

    private func notesContentBinding(_ todo: Todo) -> Binding<NoteContent> {
        Binding(get: {
            NoteContent(body: store.todo(with: todo.id)?.notes ?? "", format: .richText)
        }, set: { newValue in
            guard var t = store.todo(with: todo.id), t.notes != newValue.body else { return }
            t.notes = newValue.body
            store.update(t)
            scheduleAutoSync()
        })
    }

    /// 列表里只显示可见文字。只有图片时显示「图片」，避免把 HTML 标签铺出来。
    private func notesPreview(_ notes: String) -> String {
        let text = RichTextHTMLCodec.visibleText(notes)
            .replacingOccurrences(of: "\n", with: " ")
            .trimmingCharacters(in: .whitespacesAndNewlines)
        if !text.isEmpty { return text }
        if !NoteAttachmentIndex.relativePaths(in: notes).isEmpty { return "图片" }
        return ""
    }

    private func insertImage(into todo: Todo) {
        guard let url = ImagePicker.pickImage(message: "选择一张图片插入任务备注") else { return }
        notesController.noteID = todo.id
        notesController.attachments = noteStore.attachments
        do {
            try notesController.insertImage(from: url)
        } catch {
            imageError = error.localizedDescription
        }
    }

    // MARK: 自动同步
    /// 用户操作触发任务变更后调用；同步写回（setTodos）不经过这里，避免循环。
    private func mutate(_ action: () -> Void) {
        action()
        scheduleAutoSync()
    }

    /// 防抖 1.2 秒后自动全量同步，避免连续输入/连续点击时反复请求云端。
    private func scheduleAutoSync() {
        autoSyncTask?.cancel()
        autoSyncTask = Task { @MainActor in
            try? await Task.sleep(nanoseconds: 1_200_000_000)
            guard !Task.isCancelled else { return }
            // 未登录时不打扰用户
            guard BaiduPanClient.shared.currentToken != nil else { return }
            // 若上一次同步仍在进行，最多等 10 秒再执行，避免变更被跳过
            var waited = 0
            while sync.status.isSyncing && waited < 20 {
                try? await Task.sleep(nanoseconds: 500_000_000)
                waited += 1
                if Task.isCancelled { return }
            }
            guard !Task.isCancelled, !sync.status.isSyncing else { return }
            await sync.quickSync(store: noteStore, todos: store)
        }
    }

    // MARK: 底部快捷添加
    private var quickAddBar: some View {
        HStack(spacing: 8) {
            HStack(spacing: 8) {
                Image(systemName: "plus.circle.fill")
                    .font(.system(size: 15))
                    .foregroundColor(Theme.accent.opacity(0.75))
                TextField("添加任务，回车即可创建…", text: $newTitle)
                    .textFieldStyle(.plain)
                    .font(.system(size: 13))
                    .onSubmit(addTask)
                if !newTitle.isEmpty {
                    Button("添加") { addTask() }
                        .buttonStyle(.plain)
                        .font(.system(size: 12, weight: .semibold))
                        .foregroundColor(Theme.accent)
                }
            }
            .padding(.horizontal, 12).padding(.vertical, 9)
            .background(
                RoundedRectangle(cornerRadius: 9)
                    .fill(Color.white)
                    .overlay(RoundedRectangle(cornerRadius: 9).stroke(Theme.line, lineWidth: 1))
            )
        }
        .padding(.horizontal, 20).padding(.vertical, 12)
        .background(Theme.panelBg)
        .overlay(Rectangle().fill(Theme.lineSoft).frame(height: 1), alignment: .top)
    }

    private func addTask() {
        let t = newTitle.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !t.isEmpty else { return }
        mutate { _ = store.add(title: t) }
        newTitle = ""
    }

    private var emptyState: some View {
        VStack(spacing: 10) {
            Image(systemName: "checkmark.circle")
                .font(.system(size: 34))
                .foregroundColor(Theme.textQuaternary)
            Text("还没有任务")
                .font(.system(size: 13, weight: .medium))
                .foregroundColor(Theme.textSecondary)
            Text("在下方输入第一条任务，回车即可创建。")
                .font(.system(size: 12))
                .foregroundColor(Theme.textTertiary)
        }
        .frame(maxWidth: .infinity)
        .padding(.top, 70)
    }

    private static let dateFmt: DateFormatter = {
        let f = DateFormatter(); f.dateFormat = "MM-dd"; return f
    }()
    private static let dateTimeFmt: DateFormatter = {
        let f = DateFormatter(); f.dateFormat = "MM-dd HH:mm"; return f
    }()
    private static let createdFmt: DateFormatter = {
        let f = DateFormatter(); f.dateFormat = "yyyy-MM-dd HH:mm"; return f
    }()
}

// MARK: - 精致化基础组件

/// 紧凑步进按钮：14×16，用于第一行日期微调
private struct MiniStepButton: View {
    let systemName: String
    let action: () -> Void
    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            Image(systemName: systemName)
                .font(.system(size: 8, weight: .bold))
                .foregroundColor(hovering ? Theme.accent : Theme.textSecondary)
                .frame(width: 14, height: 16)
                .background(
                    RoundedRectangle(cornerRadius: 4)
                        .fill(hovering ? Theme.accentTint : Color(hex: 0xF4F4F6))
                )
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
    }
}

/// 行内小按钮：白底描边，悬停填充主题色
private struct SmallActionButton: View {
    let title: String
    let tint: Color
    let action: () -> Void
    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            Text(title)
                .font(.system(size: 11, weight: .medium))
                .foregroundColor(hovering ? .white : tint)
                .padding(.horizontal, 9).padding(.vertical, 4)
                .background(
                    RoundedRectangle(cornerRadius: 6)
                        .fill(hovering ? tint : Color.white)
                        .overlay(RoundedRectangle(cornerRadius: 6).stroke(tint.opacity(0.35), lineWidth: 1))
                )
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
    }
}

// MARK: - 自定义日历弹窗（替代系统 graphical DatePicker）
/// 更宽、带月份导航 / 今天 / 已选日期 / 完成按钮的日历，与 App 主题色统一。
private struct TodoCalendarPopover: View {
    let selected: Date
    let onPick: (Int, Int, Int) -> Void
    let onClose: () -> Void

    @State private var page: Date
    @State private var hoveredDay: Date?

    private let cal = Calendar.current
    private static let weekdays = ["日", "一", "二", "三", "四", "五", "六"]
    private static let selectedFormatter: DateFormatter = {
        let f = DateFormatter()
        f.locale = Locale(identifier: "zh_CN")
        f.dateFormat = "yyyy年M月d日"
        return f
    }()

    init(selected: Date,
         onPick: @escaping (Int, Int, Int) -> Void,
         onClose: @escaping () -> Void) {
        self.selected = selected
        self.onPick = onPick
        self.onClose = onClose
        _page = State(initialValue: selected)
    }

    var body: some View {
        VStack(spacing: 0) {
            header
            Rectangle().fill(Theme.lineSoft).frame(height: 1)
            weekdayRow
            dayGrid
            Rectangle().fill(Theme.lineSoft).frame(height: 1)
            footer
        }
        .frame(width: 348)
        .background(Color.white)
    }

    private var header: some View {
        HStack(alignment: .center, spacing: 10) {
            VStack(alignment: .leading, spacing: 3) {
                Text(verbatim: monthTitle)
                    .font(.system(size: 15, weight: .semibold))
                    .foregroundColor(Theme.textPrimary)
                Text("选择任务截止日期")
                    .font(.system(size: 11))
                    .foregroundColor(Theme.textTertiary)
            }
            Spacer(minLength: 0)
            HStack(spacing: 5) {
                navButton("chevron.left") { moveMonth(-1) }
                Button("今天") { pick(Date()) }
                    .buttonStyle(.plain)
                    .font(.system(size: 11, weight: .medium))
                    .foregroundColor(Theme.accent)
                    .padding(.horizontal, 9).padding(.vertical, 5)
                    .background(Theme.accentTint)
                    .clipShape(RoundedRectangle(cornerRadius: 6))
                navButton("chevron.right") { moveMonth(1) }
            }
        }
        .padding(.horizontal, 18).padding(.vertical, 14)
    }

    private func navButton(_ icon: String, _ action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: icon)
                .font(.system(size: 11, weight: .semibold))
                .foregroundColor(Theme.textSecondary)
                .frame(width: 24, height: 24)
                .background(Color(hex: 0xF4F4F6))
                .clipShape(RoundedRectangle(cornerRadius: 6))
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }

    private var weekdayRow: some View {
        HStack(spacing: 0) {
            ForEach(Self.weekdays, id: \.self) { w in
                Text(w)
                    .font(.system(size: 10.5, weight: .medium))
                    .foregroundColor(w == "日" || w == "六" ? Theme.textTertiary : Theme.textQuaternary)
                    .frame(maxWidth: .infinity)
            }
        }
        .padding(.horizontal, 18).padding(.top, 10).padding(.bottom, 6)
    }

    private var dayGrid: some View {
        LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: 3), count: 7), spacing: 3) {
            ForEach(dayItems) { item in
                dayCell(item)
            }
        }
        .padding(.horizontal, 18).padding(.bottom, 14)
    }

    private func dayCell(_ item: DayItem) -> some View {
        let isSelected = cal.isDate(item.date, inSameDayAs: selected)
        let isToday = cal.isDateInToday(item.date)
        return Button(action: { pick(item.date) }) {
            ZStack {
                RoundedRectangle(cornerRadius: 7)
                    .fill(isSelected ? Theme.accent
                          : (hoveredDay == item.date ? Theme.accentTint : Color.clear))
                if isToday && !isSelected {
                    Circle().fill(Theme.accent).frame(width: 4, height: 4).offset(y: 11)
                }
                Text(verbatim: "\(item.number)")
                    .font(.system(size: 12, weight: isSelected ? .semibold : .regular,
                                  design: .monospaced))
                    .foregroundColor(isSelected ? .white
                                     : (item.inMonth ? Theme.textPrimary
                                        : Theme.textQuaternary.opacity(0.55)))
            }
            .frame(maxWidth: .infinity)
            .frame(height: 32)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { inside in hoveredDay = inside ? item.date : nil }
    }

    private var footer: some View {
        HStack(spacing: 8) {
            Image(systemName: "calendar.badge.clock")
                .font(.system(size: 12))
                .foregroundColor(Theme.accent)
            Text(verbatim: "已选 \(Self.selectedFormatter.string(from: selected))")
                .font(.system(size: 11, weight: .medium))
                .foregroundColor(Theme.textSecondary)
            Spacer(minLength: 0)
            Button("完成") { onClose() }
                .buttonStyle(.plain)
                .font(.system(size: 11, weight: .semibold))
                .foregroundColor(.white)
                .padding(.horizontal, 15).padding(.vertical, 5)
                .background(Theme.accent)
                .clipShape(RoundedRectangle(cornerRadius: 6))
        }
        .padding(.horizontal, 18).padding(.vertical, 12)
    }

    private struct DayItem: Identifiable {
        let id: Int
        let date: Date
        let number: Int
        let inMonth: Bool
    }

    private var dayItems: [DayItem] {
        let comps = cal.dateComponents([.year, .month], from: page)
        guard let first = cal.date(from: comps) else { return [] }
        let weekday = cal.component(.weekday, from: first)   // 1 = 周日
        let start = cal.date(byAdding: .day, value: 1 - weekday, to: first) ?? first
        return (0..<42).compactMap { i in
            guard let d = cal.date(byAdding: .day, value: i, to: start) else { return nil }
            let c = cal.dateComponents([.year, .month, .day], from: d)
            return DayItem(id: i, date: d, number: c.day ?? 1,
                           inMonth: c.year == comps.year && c.month == comps.month)
        }
    }

    private var monthTitle: String {
        let c = cal.dateComponents([.year, .month], from: page)
        return "\(c.year ?? 2026)年 \(c.month ?? 1)月"
    }

    private func moveMonth(_ delta: Int) {
        if let d = cal.date(byAdding: .month, value: delta, to: page) { page = d }
    }

    private func pick(_ date: Date) {
        let c = cal.dateComponents([.year, .month, .day], from: date)
        page = date
        onPick(c.year ?? 2026, c.month ?? 1, c.day ?? 1)
    }
}
