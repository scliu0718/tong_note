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
    /// 附件仓库
    public let attachments: AttachmentStore

    public convenience init() {
        let fm = FileManager.default
        let appSupport = fm.urls(for: .applicationSupportDirectory, in: .userDomainMask).first!
        let dir = appSupport.appendingPathComponent("WorkLog", isDirectory: true)
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
        for n in notes where !n.isTrashed {
            for t in n.tags { set.insert(t) }
        }
        return set.sorted()
    }

    public func count(inNotebook name: String) -> Int {
        name == Notebooks.all ? activeNotes.count : activeNotes.filter { $0.notebook == name }.count
    }

    public func count(withTag tag: String) -> Int {
        activeNotes.filter { $0.tags.contains(tag) }.count
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
