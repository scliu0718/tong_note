import Foundation
import Combine

/// 同笔记同步引擎：先拉取云端 manifest，按 id + 更新时间对比合并，
/// 再把合并结果写回本地并上传到百度网盘 /apps/tong_log/manifest.json。
///
/// 设计要点：
/// - 云端以单个 `manifest.json` 保存全部笔记与任务，一文件即全量索引，结构简洁、可靠性高。
/// - 冲突策略为「更新时间较新者胜出」（last-write-wins），适配单人多端场景。
public final class SyncEngine: ObservableObject {
    public enum Status: Equatable {
        case idle
        case syncing
        case success(Date)
        case failed(String)

        public var isSyncing: Bool { self == .syncing }
    }

    @Published public private(set) var status: Status = .idle
    /// 最近一次同步的统计信息（如「共 3 篇 · 上传 2 · 拉取 1」）
    @Published public private(set) var lastMessage: String?
    /// 最近一次成功同步时间（持久化，供设置页展示）
    @Published public private(set) var lastSyncDate: Date? {
        didSet {
            if let d = lastSyncDate { UserDefaults.standard.set(d, forKey: Self.lastSyncKey) }
        }
    }

    private let client = BaiduPanClient.shared
    private static let lastSyncKey = "tonote.lastSyncDate"
    private static let manifestName = "manifest.json"
    private var remoteManifestPath: String {
        BaiduPanConfig.remoteRoot + "/" + SyncEngine.manifestName
    }

    public init() {
        lastSyncDate = UserDefaults.standard.object(forKey: Self.lastSyncKey) as? Date
    }

    // MARK: - 对外入口

    /// 一键同步：拉远端 → 对比合并笔记与任务 → 写回本地并上传。
    @MainActor
    public func quickSync(store: NoteStore, todos: TodoStore) async {
        guard !status.isSyncing else { return }
        status = .syncing
        lastMessage = nil
        do {
            let token = try await client.validToken()
            let remote = try await pull(token: token)

            // 对比合并
            let mergedNotes = mergeNotes(local: store.notes, remote: remote.notes)
            let mergedTodos = mergeTodos(local: todos.todos, remote: remote.todos)
            let stats = diffStats(local: store.notes, remote: remote.notes, merged: mergedNotes)

            // 写回本地并上传
            store.setNotes(mergedNotes)
            todos.setTodos(mergedTodos)
            let manifest = try encodeManifest(notes: mergedNotes, todos: mergedTodos)
            try await client.uploadData(manifest, to: remoteManifestPath, accessToken: token.accessToken)

            let ts = Date()
            status = .success(ts)
            lastSyncDate = ts
            lastMessage = stats
        } catch {
            status = .failed(error.localizedDescription)
            lastMessage = error.localizedDescription
        }
    }

    /// 单独同步某一篇笔记：仅就该篇与远端对比，任务一并做拉取合并，保持云端完整。
    @MainActor
    public func syncSingleNote(store: NoteStore, todos: TodoStore, noteID: UUID) async {
        guard !status.isSyncing else { return }
        status = .syncing
        lastMessage = nil
        do {
            let token = try await client.validToken()
            let remote = try await pull(token: token)

            // 仅对比本篇笔记
            let localNote = store.note(with: noteID)
            let remoteNote = remote.notes.first { $0.id == noteID }
            let resolved: Note?
            if let local = localNote, let remote = remoteNote {
                resolved = local.updatedAt >= remote.updatedAt ? local : remote
            } else {
                resolved = localNote ?? remoteNote
            }

            var mergedNotes = store.notes
            if let r = resolved {
                if let idx = mergedNotes.firstIndex(where: { $0.id == noteID }) {
                    mergedNotes[idx] = r
                } else {
                    mergedNotes.append(r)
                }
            }
            let mergedTodos = mergeTodos(local: todos.todos, remote: remote.todos)

            store.setNotes(mergedNotes)
            todos.setTodos(mergedTodos)
            let manifest = try encodeManifest(notes: mergedNotes, todos: mergedTodos)
            try await client.uploadData(manifest, to: remoteManifestPath, accessToken: token.accessToken)

            let ts = Date()
            status = .success(ts)
            lastSyncDate = ts
            lastMessage = resolved == nil ? "本地与云端均无该笔记" : "已同步本篇"
        } catch {
            status = .failed(error.localizedDescription)
            lastMessage = error.localizedDescription
        }
    }

    // MARK: - 拉取与合并

    private func pull(token: BaiduPanToken) async throws -> (notes: [Note], todos: [Todo]) {
        do {
            let data = try await client.downloadData(remoteManifestPath, accessToken: token.accessToken)
            let m = try decodeManifest(data)
            return (m?.notes ?? [], m?.todos ?? [])
        } catch BaiduPanError.notFound {
            return ([], []) // 云端尚未同步过
        }
    }

    /// 笔记合并：按 id 聚合，updatedAt 较新者胜出。
    private func mergeNotes(local: [Note], remote: [Note]) -> [Note] {
        var map: [UUID: Note] = [:]
        for n in remote { map[n.id] = n }
        for n in local {
            if let r = map[n.id] {
                map[n.id] = n.updatedAt >= r.updatedAt ? n : r
            } else {
                map[n.id] = n
            }
        }
        return Array(map.values).sorted { $0.updatedAt > $1.updatedAt }
    }

    /// 任务合并：同 id 取更新时间较新者，仅一端存在的直接纳入。
    private func mergeTodos(local: [Todo], remote: [Todo]) -> [Todo] {
        var map: [UUID: Todo] = [:]
        for t in remote { map[t.id] = t }
        for t in local {
            if let r = map[t.id] {
                map[t.id] = t.updatedAt >= r.updatedAt ? t : r
            } else {
                map[t.id] = t
            }
        }
        return Array(map.values).sorted { $0.updatedAt > $1.updatedAt }
    }

    /// 生成同步统计文案（仅统计笔记，任务不额外计数）。
    private func diffStats(local: [Note], remote: [Note], merged: [Note]) -> String {
        let localIDs = Set(local.map { $0.id })
        let remoteIDs = Set(remote.map { $0.id })
        let addedFromLocal = localIDs.subtracting(remoteIDs).count
        let addedFromRemote = remoteIDs.subtracting(localIDs).count
        return "共 \(merged.count) 篇 · 上传 \(addedFromLocal) · 拉取 \(addedFromRemote)"
    }

    // MARK: - 编解码

    private struct CloudManifest: Codable {
        let deviceID: String
        let syncedAt: Date
        let notes: [Note]
        let todos: [Todo]

        enum CodingKeys: String, CodingKey {
            case deviceID = "device_id"
            case syncedAt = "synced_at"
            case notes
            case todos
        }

        init(deviceID: String, syncedAt: Date, notes: [Note], todos: [Todo]) {
            self.deviceID = deviceID
            self.syncedAt = syncedAt
            self.notes = notes
            self.todos = todos
        }

        init(from decoder: Decoder) throws {
            let c = try decoder.container(keyedBy: CodingKeys.self)
            deviceID = try c.decode(String.self, forKey: .deviceID)
            syncedAt = try c.decode(Date.self, forKey: .syncedAt)
            notes = try c.decodeIfPresent([Note].self, forKey: .notes) ?? []
            todos = try c.decodeIfPresent([Todo].self, forKey: .todos) ?? []
        }
    }

    private func decodeManifest(_ data: Data) throws -> CloudManifest? {
        let dec = JSONDecoder()
        dec.dateDecodingStrategy = .iso8601
        return try? dec.decode(CloudManifest.self, from: data)
    }

    private func encodeManifest(notes: [Note], todos: [Todo]) throws -> Data {
        let manifest = CloudManifest(
            deviceID: Host.current().name ?? "unknown",
            syncedAt: Date(),
            notes: notes,
            todos: todos
        )
        let enc = JSONEncoder()
        enc.outputFormatting = [.prettyPrinted, .sortedKeys]
        enc.dateEncodingStrategy = .iso8601
        return try enc.encode(manifest)
    }
}
