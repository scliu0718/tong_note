import Foundation
import Combine

/// 笔记仓库：完整 CRUD + 回收站 + 置顶，本地 JSON 持久化（原子写入防损坏）
public final class NoteStore: ObservableObject {
    @Published public private(set) var notes: [Note] = []

    private let fileURL: URL
    private let encoder: JSONEncoder
    private let decoder: JSONDecoder

    /// 数据根目录（notes.json 与 attachments 同级）
    public let rootDirectory: URL

    /// 「快同步」虚拟笔记的固定 ID：它作为一条特殊笔记可在编辑器中打开，
    /// 其顶部右上角提供「单独同步本篇」按钮。它不出现在普通笔记列表中。
    public static let quickSyncNoteID = UUID(uuidString: "E1C2D3A4-B5F6-47C8-9D0E-1F2A3B4C5D6E")!
    /// 附件仓库
    public let attachments: AttachmentStore

    public convenience init() {
        let fm = FileManager.default
        let appSupport = fm.urls(for: .applicationSupportDirectory, in: .userDomainMask).first!
        let dir = appSupport.appendingPathComponent("tonote", isDirectory: true)
        // 迁移旧目录（WorkLog → tonote），避免升级后本地数据丢失
        let legacyDir = appSupport.appendingPathComponent("WorkLog", isDirectory: true)
        if !fm.fileExists(atPath: dir.path), fm.fileExists(atPath: legacyDir.path) {
            try? fm.moveItem(at: legacyDir, to: dir)
        }
        self.init(directory: dir)
    }

    /// 指定存储目录（测试/备份用）
    public init(directory: URL) {
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        rootDirectory = directory
        fileURL = directory.appendingPathComponent("notes.json")
        attachments = AttachmentStore(rootDirectory: directory)

        encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        encoder.dateEncodingStrategy = .iso8601
        decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601

        load()
        if notes.isEmpty {
            notes = Note.samples
            save()
        }
        ensureQuickSyncNote()
    }

    /// 确保「快同步」虚拟笔记存在（不存在则创建并保存）。
    private func ensureQuickSyncNote() {
        guard notes.first(where: { $0.id == NoteStore.quickSyncNoteID }) == nil else { return }
        var q = Note(id: NoteStore.quickSyncNoteID,
                     title: "快同步",
                     body: """
                     这是「快同步」专属笔记。点击右上角的「同步」按钮即可**单独**把本篇同步到百度网盘（先拉云端、对比、再上传）。

                     - 同步逻辑：仅针对本篇笔记与云端同 ID 笔记对比，以「更新时间较新者」为准，再回写本地并上传。
                     - 需要登录并已授权百度网盘（netdisk 读写）才能生效。
                     - 在左侧「全部笔记」顶部也能看到「快同步」卡片，点击可在此打开；右上角「一键同步」按钮则同步整个资料库。
                     """,
                     notebook: "工作日志")
        q.contentFormat = .markdown
        q.pinned = false
        notes.append(q)
        save()
    }

    public var storagePath: String { fileURL.path }

    // MARK: - 派生数据
    public var activeNotes: [Note] {
        notes.filter { !$0.isTrashed }.sorted { $0.updatedAt > $1.updatedAt }
    }

    public var trashNotes: [Note] {
        notes.filter { $0.isTrashed }
            .sorted { ($0.deletedAt ?? .distantPast) > ($1.deletedAt ?? .distantPast) }
    }

    public var allTags: [String] {
        var set = Set<String>()
        for n in notes where !n.isTrashed && n.id != NoteStore.quickSyncNoteID {
            for t in n.tags { set.insert(t) }
        }
        return set.sorted()
    }

    public func count(inNotebook name: String) -> Int {
        let list = activeNotes.filter { $0.id != NoteStore.quickSyncNoteID }
        return name == Notebooks.all ? list.count : list.filter { $0.notebook == name }.count
    }

    public func count(withTag tag: String) -> Int {
        activeNotes.filter { $0.tags.contains(tag) }.count
    }

    /// 删除目录时把该目录下的笔记（含回收站）整体转移到目标分类，笔记本身不删除。
    public func moveNotes(from: String, to: String) {
        var changed = false
        for i in notes.indices where notes[i].notebook == from {
            notes[i].notebook = to
            notes[i].updatedAt = Date()
            changed = true
        }
        if changed { save() }
    }

    /// 删除一个标签：从所有笔记（含回收站）上移除，笔记本身与正文保留。
    public func removeTag(_ tag: String) {
        var changed = false
        for i in notes.indices where notes[i].tags.contains(tag) {
            notes[i].tags.removeAll { $0 == tag }
            notes[i].updatedAt = Date()
            changed = true
        }
        if changed { save() }
    }

    // MARK: - CRUD
    @discardableResult
    public func addNote(notebook: String = "工作日志") -> Note {
        let note = Note(title: "未命名笔记", body: "", notebook: notebook)
        notes.insert(note, at: 0)
        save()
        return note
    }

    public func moveToTrash(_ id: UUID) {
        guard let i = notes.firstIndex(where: { $0.id == id }) else { return }
        notes[i].deletedAt = Date()
        notes[i].pinned = false
        save()
    }

    public func restore(_ id: UUID) {
        guard let i = notes.firstIndex(where: { $0.id == id }) else { return }
        notes[i].deletedAt = nil
        save()
    }

    public func permanentlyDelete(_ id: UUID) {
        notes.removeAll { $0.id == id }
        attachments.removeAttachments(for: id)
        save()
    }

    public func togglePin(_ id: UUID) {
        guard let i = notes.firstIndex(where: { $0.id == id }) else { return }
        notes[i].pinned.toggle()
        save()
    }

    public func note(with id: UUID) -> Note? {
        notes.first { $0.id == id }
    }

    /// 整体替换笔记集合（同步合并后回写）。会刷新更新时间并原子保存。
    public func setNotes(_ newNotes: [Note]) {
        notes = newNotes
        save()
    }

    /// 更新一条笔记（编辑写回），自动刷新更新时间并保存
    public func update(_ note: Note) {
        guard let idx = notes.firstIndex(where: { $0.id == note.id }) else { return }
        var updated = note
        updated.updatedAt = Date()
        notes[idx] = updated
        save()
    }

    /// 原子更新正文与正文格式（富文本编辑器每次保存都走这里）
    public func updateContent(_ id: UUID, body: String, format: NoteContentFormat) {
        guard let idx = notes.firstIndex(where: { $0.id == id }) else { return }
        notes[idx].body = body
        notes[idx].contentFormat = format
        notes[idx].updatedAt = Date()
        save()
    }

    // MARK: - 持久化
    private func load() {
        guard let data = try? Data(contentsOf: fileURL),
              let loaded = try? decoder.decode([Note].self, from: data) else { return }
        notes = loaded
    }

    private func save() {
        guard let data = try? encoder.encode(notes) else { return }
        try? data.write(to: fileURL, options: .atomic)
    }
}
