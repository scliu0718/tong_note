import Foundation
import Combine

/// 任务状态（四态，对应「待完成 / 进行中 / 已完成 / 终止」）。
public enum TodoStatus: String, Codable, CaseIterable {
    case todo, doing, done, terminated

    public var label: String {
        switch self {
        case .todo: return "待完成"
        case .doing: return "进行中"
        case .done: return "已完成"
        case .terminated: return "终止"
        }
    }
}

/// 一条任务：规划条目 + 完成状态 + 计划/完成时间 + 备注。
public struct Todo: Identifiable, Codable, Hashable {
    public var id: UUID
    public var title: String
    public var notes: String
    public var status: TodoStatus
    public var plannedAt: Date?
    public var completedAt: Date?
    public var startedAt: Date?
    public var createdAt: Date
    public var updatedAt: Date
    /// 组内排序权重（v1 按时间排序，预留字段）
    public var order: Int
    /// 已成功写到云端的版本。本机编辑不增加，上传成功才加 1。
    public var rev: Int
    /// 删除后的墓碑。列表里不再显示。
    public var purged: Bool

    public init(id: UUID = UUID(),
                title: String,
                notes: String = "",
                status: TodoStatus = .todo,
                plannedAt: Date? = nil,
                completedAt: Date? = nil,
                startedAt: Date? = nil,
                createdAt: Date = Date(),
                updatedAt: Date = Date(),
                order: Int = 0,
                rev: Int = 0,
                purged: Bool = false) {
        self.id = id
        self.title = title
        self.notes = notes
        self.status = status
        self.plannedAt = plannedAt
        self.completedAt = completedAt
        self.startedAt = startedAt
        self.createdAt = createdAt
        self.updatedAt = updatedAt
        self.order = order
        self.rev = rev
        self.purged = purged
    }

    /// 是否逾期：仍在进行中/待完成，且计划完成时间早于今天（已完成、终止均不算逾期）。
    public var isOverdue: Bool {
        guard status != .done, status != .terminated, let p = plannedAt else { return false }
        return p < Calendar.current.startOfDay(for: Date())
    }

    // MARK: - 兼容解码（缺省字段兜底）
    private enum CodingKeys: String, CodingKey {
        case id, title, notes, status, plannedAt, completedAt, startedAt, createdAt, updatedAt, order, rev, purged
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = try c.decode(UUID.self, forKey: .id)
        title = try c.decode(String.self, forKey: .title)
        notes = try c.decodeIfPresent(String.self, forKey: .notes) ?? ""
        status = try c.decodeIfPresent(TodoStatus.self, forKey: .status) ?? .todo
        plannedAt = try c.decodeIfPresent(Date.self, forKey: .plannedAt)
        completedAt = try c.decodeIfPresent(Date.self, forKey: .completedAt)
        startedAt = try c.decodeIfPresent(Date.self, forKey: .startedAt)
        createdAt = try c.decodeIfPresent(Date.self, forKey: .createdAt) ?? Date()
        updatedAt = try c.decodeIfPresent(Date.self, forKey: .updatedAt) ?? Date()
        order = try c.decodeIfPresent(Int.self, forKey: .order) ?? 0
        rev = try c.decodeIfPresent(Int.self, forKey: .rev) ?? 0
        purged = try c.decodeIfPresent(Bool.self, forKey: .purged) ?? false
    }

    public func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        try c.encode(id, forKey: .id)
        try c.encode(title, forKey: .title)
        try c.encode(notes, forKey: .notes)
        try c.encode(status, forKey: .status)
        try c.encodeIfPresent(plannedAt, forKey: .plannedAt)
        try c.encodeIfPresent(completedAt, forKey: .completedAt)
        try c.encodeIfPresent(startedAt, forKey: .startedAt)
        try c.encode(createdAt, forKey: .createdAt)
        try c.encode(updatedAt, forKey: .updatedAt)
        try c.encode(order, forKey: .order)
        try c.encode(rev, forKey: .rev)
        try c.encode(purged, forKey: .purged)
    }
}

/// 任务仓库：CRUD + 状态流转 + 分组 + 本地 JSON 持久化（原子写入）。
public final class TodoStore: ObservableObject {
    @Published public private(set) var todos: [Todo] = []

    private let fileURL: URL
    /// 任务备注里的图片，目录与笔记附件相同：`attachments/<任务 id>/`。
    public let attachments: AttachmentStore
    private let cursors: SyncCursorStore
    private let encoder: JSONEncoder
    private let decoder: JSONDecoder

    public convenience init() {
        let fm = FileManager.default
        let dir = fm.urls(for: .applicationSupportDirectory, in: .userDomainMask).first!
            .appendingPathComponent("tonote", isDirectory: true)
        try? fm.createDirectory(at: dir, withIntermediateDirectories: true)
        self.init(directory: dir)
    }

    public init(directory: URL) {
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        fileURL = directory.appendingPathComponent("todos.json")
        attachments = AttachmentStore(rootDirectory: directory)
        cursors = SyncCursorStore(fileURL: directory.appendingPathComponent("todo-sync.json"))
        encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        encoder.dateEncodingStrategy = .iso8601
        decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        load()
    }

    // MARK: - 派生数据

    public var pending: [Todo] { sorted(for: .todo) }
    public var doing: [Todo] { sorted(for: .doing) }
    public var done: [Todo] { sorted(for: .done) }
    public var terminated: [Todo] { sorted(for: .terminated) }

    public var incompleteCount: Int { todos.filter { !$0.purged && ($0.status == .todo || $0.status == .doing) }.count }
    public var pendingCount: Int { todos.filter { !$0.purged && $0.status == .todo }.count }
    public var doingCount: Int { todos.filter { !$0.purged && $0.status == .doing }.count }
    public var doneCount: Int { todos.filter { !$0.purged && $0.status == .done }.count }
    public var terminatedCount: Int { todos.filter { !$0.purged && $0.status == .terminated }.count }
    public var overdueCount: Int { todos.filter { !$0.purged && $0.isOverdue }.count }

    private func sorted(for status: TodoStatus) -> [Todo] {
        todos.filter { !$0.purged && $0.status == status }.sorted { a, b in
            if a.isOverdue != b.isOverdue { return a.isOverdue }
            let pa = a.plannedAt, pb = b.plannedAt
            if pa != nil || pb != nil {
                if let pa, let pb { if pa != pb { return pa < pb } }
                else { return pa != nil }
            }
            if status == .done {
                if let ca = a.completedAt, let cb = b.completedAt, ca != cb { return ca > cb }
            }
            return a.createdAt < b.createdAt
        }
    }

    // MARK: - CRUD

    @discardableResult
    public func add(title: String) -> Todo {
        let t = Todo(title: title, order: todos.count)
        todos.append(t)
        save()
        cursors.markDirty(t.id)
        return t
    }

    public func remove(_ id: UUID) {
        guard let i = index(of: id) else { return }
        todos[i].purged = true
        todos[i].updatedAt = Date()
        save()
        cursors.markDirty(id)
    }

    /// 勾选/取消勾选：完成 ↔ 待完成（完成自动记录时间，取消清空完成时间）。
    public func toggleDone(_ id: UUID) {
        guard let i = index(of: id) else { return }
        if todos[i].status == .done {
            todos[i].status = .todo
            todos[i].completedAt = nil
        } else {
            todos[i].status = .done
            todos[i].completedAt = Date()
        }
        todos[i].updatedAt = Date()
        save()
        cursors.markDirty(id)
    }

    /// 待完成 → 进行中，记录开始时间。
    public func start(_ id: UUID) {
        guard let i = index(of: id) else { return }
        todos[i].status = .doing
        todos[i].startedAt = Date()
        todos[i].updatedAt = Date()
        save()
        cursors.markDirty(id)
    }

    /// 进行中 → 已完成，记录完成时间。
    public func complete(_ id: UUID) {
        guard let i = index(of: id) else { return }
        todos[i].status = .done
        todos[i].completedAt = Date()
        todos[i].updatedAt = Date()
        save()
        cursors.markDirty(id)
    }

    /// 任意状态 → 终止（停止的任务），不记完成时间。
    public func terminate(_ id: UUID) {
        guard let i = index(of: id) else { return }
        todos[i].status = .terminated
        todos[i].completedAt = nil
        todos[i].updatedAt = Date()
        save()
        cursors.markDirty(id)
    }

    /// 终止 → 待完成（重新激活）。
    public func restore(_ id: UUID) {
        guard let i = index(of: id) else { return }
        todos[i].status = .todo
        todos[i].completedAt = nil
        todos[i].updatedAt = Date()
        save()
        cursors.markDirty(id)
    }

    /// 整体更新一条任务（编辑回写），自动刷新更新时间。
    public func update(_ todo: Todo) {
        guard let i = index(of: todo.id) else { return }
        var t = todo
        t.updatedAt = Date()
        todos[i] = t
        save()
        cursors.markDirty(todo.id)
    }

    public func todo(with id: UUID) -> Todo? {
        todos.first { $0.id == id }
    }

    /// 清空已完成（供「一键清空已完成」使用）。
    public func clearCompleted() {
        var touched: [UUID] = []
        for i in todos.indices where todos[i].status == .done && !todos[i].purged {
            todos[i].purged = true
            todos[i].updatedAt = Date()
            touched.append(todos[i].id)
        }
        if !touched.isEmpty {
            save()
            touched.forEach { cursors.markDirty($0) }
        }
    }

    public func syncCursor(for id: UUID) -> SyncCursor {
        cursors.cursor(for: id)
    }

    public func applySyncedTodo(_ todo: Todo, cursor: SyncCursor) {
        if let i = index(of: todo.id) {
            todos[i] = todo
        } else {
            todos.append(todo)
        }
        cursors.set(cursor, for: todo.id)
        save()
    }

    /// 同步合并后整体替换。
    public func setTodos(_ newTodos: [Todo]) {
        todos = newTodos
        save()
    }

    // MARK: - 持久化

    private func index(of id: UUID) -> Int? {
        todos.firstIndex { $0.id == id }
    }

    private func load() {
        guard let data = try? Data(contentsOf: fileURL),
              let loaded = try? decoder.decode([Todo].self, from: data) else { return }
        todos = loaded
    }

    private func save() {
        guard let data = try? encoder.encode(todos) else { return }
        try? data.write(to: fileURL, options: .atomic)
    }
}
