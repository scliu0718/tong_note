import SwiftUI
import WorkLogCore

/// 任务面板：待完成 / 进行中 / 已完成 三组，支持勾选、开始、完成、删除、编辑备注与日期。
struct TodoView: View {
    @ObservedObject var store: TodoStore

    @State private var newTitle = ""
    @State private var expandedID: UUID?
    @State private var hoveredID: UUID?
    @State private var filter: Filter = .all
    @State private var showCalendar = false

    private enum Filter { case all, incomplete }

    var body: some View {
        VStack(spacing: 0) {
            header
            ScrollView(showsIndicators: false) {
                // 分组之间的间距：由 16 提升到 32（两倍），层次更清晰
                VStack(alignment: .leading, spacing: 32) {
                    if store.todos.isEmpty {
                        emptyState
                    } else {
                        group("待完成", color: Theme.red, items: store.pending, showStart: true)
                        group("进行中", color: Theme.amber, items: store.doing, showComplete: true)
                        if filter == .all {
                            group("已完成", color: Theme.accent, items: store.done)
                        }
                    }
                }
                .padding(.horizontal, 22).padding(.vertical, 20)
            }
            .background(Theme.panelBg)
            quickAddBar
        }
        .background(Theme.panelBg)
    }

    // MARK: 头部
    private var header: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(alignment: .top) {
                VStack(alignment: .leading, spacing: 4) {
                    Text("任务")
                        .font(.system(size: 20, weight: .bold))
                        .foregroundColor(Theme.textPrimary)
                    Text(statText)
                        .font(.system(size: 12))
                        .foregroundColor(Theme.textTertiary)
                }
                Spacer()
                Button(action: { filter = filter == .all ? .incomplete : .all }) {
                    Text(filter == .all ? "全部" : "仅未完成")
                        .font(.system(size: 12, weight: .medium))
                        .foregroundColor(Theme.textSecondary)
                        .padding(.horizontal, 11).padding(.vertical, 6)
                        .background(
                            RoundedRectangle(cornerRadius: 8)
                                .fill(Color.white)
                                .overlay(RoundedRectangle(cornerRadius: 8).stroke(Theme.line, lineWidth: 1))
                        )
                }
                .buttonStyle(.plain)

                AccentButton(title: "新建任务", icon: "plus") { addTask() }
            }
            if store.doneCount > 0 && filter == .all {
                HStack {
                    Spacer()
                    Button("清空已完成") { store.clearCompleted() }
                        .buttonStyle(.plain)
                        .font(.system(size: 11))
                        .foregroundColor(Theme.textTertiary)
                }
            }
        }
        .padding(.horizontal, 22).padding(.top, 18).padding(.bottom, 12)
        .overlay(Rectangle().fill(Theme.lineSoft).frame(height: 1), alignment: .bottom)
    }

    private var statText: String {
        var parts = ["待完成 \(store.pendingCount)", "进行中 \(store.doingCount)", "已完成 \(store.doneCount)"]
        if store.overdueCount > 0 { parts.append("逾期 \(store.overdueCount)") }
        return parts.joined(separator: " · ")
    }

    // MARK: 分组
    private func group(_ title: String, color: Color,
                       items: [Todo], showStart: Bool = false, showComplete: Bool = false) -> some View {
        VStack(alignment: .leading, spacing: 10) {
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
                Rectangle().fill(Theme.lineSoft).frame(height: 1)
                    .padding(.leading, 5)
            }

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
                    todoRow(todo, showStart: showStart, showComplete: showComplete)
                }
            }
        }
    }

    // MARK: 任务行
    @ViewBuilder
    private func todoRow(_ todo: Todo, showStart: Bool, showComplete: Bool) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 11) {
                checkbox(for: todo)
                VStack(alignment: .leading, spacing: 3) {
                    Text(todo.title)
                        .font(.system(size: 13, weight: .medium))
                        .foregroundColor(todo.status == .done ? Theme.textQuaternary : Theme.textPrimary)
                        .strikethrough(todo.status == .done, color: Theme.textQuaternary)
                        .lineLimit(2)
                    HStack(spacing: 6) {
                        if !todo.notes.isEmpty {
                            Text(todo.notes)
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
                        SmallActionButton(title: "开始", tint: Theme.textSecondary) { store.start(todo.id) }
                    }
                    if showComplete {
                        SmallActionButton(title: "完成", tint: Theme.accent) { store.complete(todo.id) }
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

                Button(action: { store.remove(todo.id) }) {
                    Image(systemName: "trash")
                        .font(.system(size: 12))
                        .foregroundColor(Theme.textQuaternary)
                        .frame(width: 22, height: 22)
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain).help("删除任务")
            }
            .padding(.horizontal, 8).padding(.vertical, 9)
            .background(hoveredID == todo.id ? Color(hex: 0xF7FAF8) : Color.clear)
            .contentShape(Rectangle())
            .onHover { inside in
                if inside { hoveredID = todo.id }
                else if hoveredID == todo.id { hoveredID = nil }
            }
            .onTapGesture { toggleExpand(todo.id) }

            if expandedID == todo.id {
                detail(todo)
                    .padding(.horizontal, 8)
                    .padding(.bottom, 10)
            }
        }
        .overlay(Rectangle().fill(Theme.lineSoft).frame(height: 1), alignment: .bottom)
    }

    private func toggleExpand(_ id: UUID) {
        withAnimation(.easeInOut(duration: 0.14)) {
            expandedID = expandedID == id ? nil : id
        }
    }

    private func checkbox(for todo: Todo) -> some View {
        let border: Color = todo.status == .doing ? Theme.amber
            : (todo.status == .done ? Theme.accent : Color(hex: 0xC7C7CC))
        return Button(action: { store.toggleDone(todo.id) }) {
            ZStack {
                if todo.status == .done {
                    Circle().fill(Theme.accent).frame(width: 17, height: 17)
                    Image(systemName: "checkmark")
                        .font(.system(size: 9, weight: .bold))
                        .foregroundColor(.white)
                } else {
                    Circle().stroke(border, lineWidth: 1.5).frame(width: 17, height: 17)
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
                sectionLabel("备注")
                TextEditor(text: notesBinding(todo))
                    .font(.system(size: 12))
                    .foregroundColor(Theme.textPrimary)
                    .frame(minHeight: 56, maxHeight: 130)
                    .padding(8)
                    .background(Color.white)
                    .clipShape(RoundedRectangle(cornerRadius: 8))
                    .overlay(RoundedRectangle(cornerRadius: 8).stroke(Theme.line, lineWidth: 1))
            }

            deadlineSection(todo)

            if todo.status == .done, let done = todo.completedAt {
                Text("完成时间：\(Self.dateTimeFmt.string(from: done))")
                    .font(.system(size: 11)).foregroundColor(Theme.textQuaternary)
            }

            HStack {
                Spacer()
                AccentButton(title: "保存") { expandedID = nil }
                    .help("保存并收起")
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

    // MARK: 截止时间（年月日步进 + 日历）
    @ViewBuilder
    private func deadlineSection(_ todo: Todo) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            plannedToggle(todo)

            if todo.plannedAt != nil {
                let c = components(of: todo)
                HStack(spacing: 8) {
                    stepField("年", value: c.year, range: 2000...2100, wrap: false) { setDate(todo, y: $0, m: c.month, d: min(c.day, daysIn(y: $0, m: c.month))) }
                    stepField("月", value: c.month, range: 1...12, wrap: true) { setDate(todo, y: c.year, m: $0, d: min(c.day, daysIn(y: c.year, m: $0))) }
                    stepField("日", value: c.day, range: 1...daysIn(y: c.year, m: c.month), wrap: false) { setDate(todo, y: c.year, m: c.month, d: $0) }
                    calendarButton(todo)
                    Spacer(minLength: 0)
                }
            }
        }
    }

    /// 自定义主题色勾选框（替代系统蓝色 checkbox，与整体配色统一）
    private func plannedToggle(_ todo: Todo) -> some View {
        let on = todo.plannedAt != nil
        return Button(action: {
            guard var t = store.todo(with: todo.id) else { return }
            t.plannedAt = on ? nil : Date()
            store.update(t)
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
            HStack(spacing: 5) {
                Image(systemName: "calendar").font(.system(size: 12, weight: .medium))
                Text("日历").font(.system(size: 11, weight: .semibold))
            }
            .foregroundColor(Theme.accent)
            .padding(.horizontal, 11).padding(.vertical, 8)
            .background(
                RoundedRectangle(cornerRadius: 8)
                    .fill(Color.white)
                    .overlay(RoundedRectangle(cornerRadius: 8).stroke(Theme.accentTint2, lineWidth: 1))
                    .shadow(color: Color.black.opacity(0.03), radius: 3, x: 0, y: 1)
            )
        }
        .buttonStyle(.plain)
        .popover(isPresented: $showCalendar, arrowEdge: .bottom) {
            DatePicker("", selection: dateBinding(todo, todo.plannedAt ?? Date()),
                       displayedComponents: .date)
                .datePickerStyle(.graphical)
                .labelsHidden()
                .tint(Theme.accent)
                .padding(14)
                .frame(width: 280)
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
    }

    private func stepField(_ label: String, value: Int, range: ClosedRange<Int>, wrap: Bool,
                           onChange: @escaping (Int) -> Void) -> some View {
        HStack(spacing: 3) {
            Text(label)
                .font(.system(size: 10, weight: .medium))
                .foregroundColor(Theme.textQuaternary)
                .padding(.trailing, 2)
            StepButton(systemName: "minus") { onChange(step(value, -1, range: range, wrap: wrap)) }
            // 用 verbatim 避免 LocalizedStringKey 把 2028 格式化成 "2,028"
            Text(verbatim: "\(value)")
                .font(.system(size: 13, weight: .semibold, design: .monospaced))
                .foregroundColor(Theme.textPrimary)
                .frame(minWidth: label == "年" ? 40 : 26)
            StepButton(systemName: "plus") { onChange(step(value, 1, range: range, wrap: wrap)) }
        }
        .padding(.horizontal, 7).padding(.vertical, 5)
        .background(
            RoundedRectangle(cornerRadius: 8)
                .fill(Color.white)
                .overlay(RoundedRectangle(cornerRadius: 8).stroke(Theme.line, lineWidth: 1))
                .shadow(color: Color.black.opacity(0.03), radius: 3, x: 0, y: 1)
        )
    }

    private func step(_ value: Int, _ delta: Int, range: ClosedRange<Int>, wrap: Bool) -> Int {
        let v = value + delta
        if v < range.lowerBound { return wrap ? range.upperBound : range.lowerBound }
        if v > range.upperBound { return wrap ? range.lowerBound : range.upperBound }
        return v
    }

    private func notesBinding(_ todo: Todo) -> Binding<String> {
        Binding(get: { store.todo(with: todo.id)?.notes ?? "" },
                set: { newValue in
                    if var t = store.todo(with: todo.id) {
                        t.notes = newValue
                        store.update(t)
                    }
                })
    }

    private func dateBinding(_ todo: Todo, _ current: Date) -> Binding<Date> {
        Binding(get: { store.todo(with: todo.id)?.plannedAt ?? current },
                set: { newValue in
                    if var t = store.todo(with: todo.id) {
                        t.plannedAt = newValue
                        store.update(t)
                    }
                })
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
        store.add(title: t)
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
}

// MARK: - 精致化基础组件

/// 步进按钮：带圆形浅底与悬停反馈
private struct StepButton: View {
    let systemName: String
    let action: () -> Void
    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            Image(systemName: systemName)
                .font(.system(size: 9, weight: .bold))
                .foregroundColor(hovering ? Theme.accent : Theme.textSecondary)
                .frame(width: 20, height: 20)
                .background(
                    RoundedRectangle(cornerRadius: 5)
                        .fill(hovering ? Theme.accentTint : Color(hex: 0xF4F4F6))
                )
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
    }
}

/// 主按钮：主题绿 + 轻投影 + 悬停加深
private struct AccentButton: View {
    let title: String
    var icon: String? = nil
    let action: () -> Void
    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            HStack(spacing: 5) {
                if let icon = icon {
                    Image(systemName: icon).font(.system(size: 12, weight: .bold))
                }
                Text(title).font(.system(size: 12, weight: .semibold))
            }
            .foregroundColor(.white)
            .padding(.horizontal, 15).padding(.vertical, 7)
            .background(
                RoundedRectangle(cornerRadius: 8)
                    .fill(hovering ? Theme.accentHover : Theme.accent)
                    .shadow(color: Theme.accent.opacity(0.22), radius: 5, x: 0, y: 2)
            )
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
