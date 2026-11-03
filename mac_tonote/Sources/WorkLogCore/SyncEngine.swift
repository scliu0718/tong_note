import Foundation
import Combine

/// 同笔记同步引擎：先拉取云端 manifest，按 id + 更新时间对比合并，
/// 再把合并结果写回本地并上传到百度网盘 /apps/tong_log/manifest.json。
///
/// 设计要点：
/// - 云端以单个 `manifest.json` 保存全部笔记（含正文/标签/笔记本/回收站状态），
///   一文件即全量索引，结构简洁、可靠性高。
/// - 冲突策略为「更新时间较新者胜出」（last-write-wins），适配单人多端场景。
///   多端同时编辑同一篇且都改过时，较旧一方的修改会被覆盖——这是 MVP 的合理取舍。
public final class SyncEngine: ObservableObject {
    public enum Status: Equatable {
        case idle
        case syncing
        case success(Date)
        case failed(String)

        public var isSyncing: Bool { self == .syncing }
    }

    @Published public private(set) var status: Status = .idle
    /// 最近一次同步的统计信息（如「上传 2 · 拉取 1 · 合并 3」）
    @Published public private(set) var lastMessage: String?

    private let client = BaiduPanClient.shared
    private static let manifestName = "manifest.json"
    private var remoteManifestPath: String {
        BaiduPanConfig.remoteRoot + "/" + SyncEngine.manifestName
    }

    public init() {}

    // MARK: - 对外入口

    /// 一键同步：拉远端 → 对比合并 → 写回本地并上传。
    /// - Parameter store: 本地笔记仓库（结果会整体替换进去）。
    @MainActor
    public func quickSync(store: NoteStore) async {
        guard !status.isSyncing else { return }
        status = .syncing
        lastMessage = nil
        do {
            let token = try await client.validToken()

            // 1) 先拉远程笔记
            let remoteNotes: [Note]
            do {
                let data = try await client.downloadData(remoteManifestPath, accessToken: token.accessToken)
                remoteNotes = try decodeManifest(data)?.notes ?? []
            } catch BaiduPanError.notFound {
                remoteNotes = [] // 云端尚未同步过
            }

            // 2) 对比合并
            let merged = merge(local: store.notes, remote: remoteNotes)
            let stats = diffStats(local: store.notes, remote: remoteNotes, merged: merged)

            // 3) 写回本地并上传
            store.setNotes(merged)
            let manifest = try encodeManifest(merged)
            try await client.uploadData(manifest, to: remoteManifestPath, accessToken: token.accessToken)

            let ts = Date()
            status = .success(ts)
            lastMessage = stats
        } catch {
            status = .failed(error.localizedDescription)
            lastMessage = error.localizedDescription
        }
    }

    /// 单独同步某一篇笔记：拉取云端 manifest，仅就该篇与远端同 ID 笔记对比
    ///（更新时间较新者胜出），回写本地并上传整份 manifest。
    /// - Parameters:
    ///   - store: 本地笔记仓库。
    ///   - noteID: 目标笔记 ID（如 `NoteStore.quickSyncNoteID`）。
    @MainActor
    public func syncSingleNote(store: NoteStore, noteID: UUID) async {
        guard !status.isSyncing else { return }
        status = .syncing
        lastMessage = nil
        do {
            let token = try await client.validToken()

            // 1) 先拉远程
            let remoteNotes: [Note]
            do {
                let data = try await client.downloadData(remoteManifestPath, accessToken: token.accessToken)
                remoteNotes = try decodeManifest(data)?.notes ?? []
            } catch BaiduPanError.notFound {
                remoteNotes = []
            }

            // 2) 仅对比本篇
            let localNote = store.note(with: noteID)
            let remoteNote = remoteNotes.first { $0.id == noteID }
            let resolved: Note?
            if let local = localNote, let remote = remoteNote {
                resolved = local.updatedAt >= remote.updatedAt ? local : remote
            } else {
                resolved = localNote ?? remoteNote
            }

            // 3) 回写该篇（其余笔记保持本地不变）并上传
            var merged = store.notes
            if let r = resolved {
                if let idx = merged.firstIndex(where: { $0.id == noteID }) {
                    merged[idx] = r
                } else {
                    merged.append(r)
                }
            }
            store.setNotes(merged)
            let manifest = try encodeManifest(merged)
            try await client.uploadData(manifest, to: remoteManifestPath, accessToken: token.accessToken)

            let ts = Date()
            status = .success(ts)
            lastMessage = resolved == nil ? "本地与云端均无该笔记" : "已同步本篇"
        } catch {
            status = .failed(error.localizedDescription)
            lastMessage = error.localizedDescription
        }
    }

    // MARK: - 合并逻辑

    /// 按 id 聚合，updatedAt 较新者胜出；仅一端存在的直接纳入。
    private func merge(local: [Note], remote: [Note]) -> [Note] {
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

    /// 生成同步统计文案。
    private func diffStats(local: [Note], remote: [Note], merged: [Note]) -> String {
        let localIDs = Set(local.map { $0.id })
        let remoteIDs = Set(remote.map { $0.id })
        let addedFromLocal = localIDs.subtracting(remoteIDs).count   // 本地独有 → 上传
        let addedFromRemote = remoteIDs.subtracting(localIDs).count  // 云端独有 → 拉取
        let both = localIDs.intersection(remoteIDs)
        let updated = both.count // 两端都存在 → 已合并（取较新者）
        return "上传 \(addedFromLocal) · 拉取 \(addedFromRemote) · 合并 \(updated)"
    }

    // MARK: - 编解码

    private struct CloudManifest: Codable {
        let deviceID: String
        let syncedAt: Date
        let notes: [Note]

        enum CodingKeys: String, CodingKey {
            case deviceID = "device_id"
            case syncedAt = "synced_at"
            case notes
        }
    }

    private func decodeManifest(_ data: Data) throws -> CloudManifest? {
        let dec = JSONDecoder()
        dec.dateDecodingStrategy = .iso8601
        return try? dec.decode(CloudManifest.self, from: data)
    }

    private func encodeManifest(_ notes: [Note]) throws -> Data {
        let manifest = CloudManifest(
            deviceID: Host.current().name ?? "unknown",
            syncedAt: Date(),
            notes: notes
        )
        let enc = JSONEncoder()
        enc.outputFormatting = [.prettyPrinted, .sortedKeys]
        enc.dateEncodingStrategy = .iso8601
        return try enc.encode(manifest)
    }
}
