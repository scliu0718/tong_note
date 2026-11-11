import Foundation

public extension Notification.Name {
    /// 本机笔记、任务或笔记本被用户改过。同步引擎用它做一两秒防抖，同步回写不发这则通知。
    static let tonoteLocalDataChanged = Notification.Name("tonote.localData.changed")
}

/// 本机对照表的一行：上次同步成功时云端的版本和时间，以及这篇是否还有未上传的修改。
public struct SyncCursor: Codable, Equatable, Sendable {
    public var baseRev: Int
    public var baseUpdatedAt: Date?
    public var serverMtime: Int?
    public var dirty: Bool
    public var uploadedAttachments: [String]

    public init(baseRev: Int = 0,
                baseUpdatedAt: Date? = nil,
                serverMtime: Int? = nil,
                dirty: Bool = true,
                uploadedAttachments: [String] = []) {
        self.baseRev = baseRev
        self.baseUpdatedAt = baseUpdatedAt
        self.serverMtime = serverMtime
        self.dirty = dirty
        self.uploadedAttachments = uploadedAttachments
    }

    public static let unsynced = SyncCursor()
}

public enum SyncStep: Equatable {
    /// 云端没变，本机也没改。
    case skip
    /// 只把目录修改时间记下来，正文不用换。
    case rememberMtime
    /// 用云端覆盖本机。
    case useRemote
    /// 把本机这份以给定 rev 上传。
    case upload(rev: Int)
    /// 两边都改过，要留冲突副本。
    case conflict
}

public enum SyncRules {
    public static func sameTime(_ lhs: Date, _ rhs: Date) -> Bool {
        abs(lhs.timeIntervalSince(rhs)) < 1
    }

    /// 目录项时间没变，且本机没有未上传修改时，不必下载正文。
    public static func shouldSkipFetch(serverMtime: Int?, cursor: SyncCursor) -> Bool {
        guard !cursor.dirty, let serverMtime, let known = cursor.serverMtime else { return false }
        return serverMtime == known
    }

    /// 已经拿到云端这篇之后怎么处理。`remote == nil` 表示网盘上还没有这个文件。
    /// 对照表还是空的（第一次同步）时，时间一致就认云端版本，避免把同一篇拆成冲突副本。
    public static func afterFetch(dirty: Bool,
                                  remoteRev: Int?,
                                  remoteUpdatedAt: Date?,
                                  localUpdatedAt: Date? = nil,
                                  cursor: SyncCursor) -> SyncStep {
        guard let remoteRev, let remoteUpdatedAt else {
            let rev = cursor.baseRev == 0 ? 1 : cursor.baseRev + (dirty ? 1 : 0)
            return .upload(rev: max(rev, 1))
        }
        let neverSynced = cursor.baseRev == 0 && cursor.baseUpdatedAt == nil
        if neverSynced {
            // 本机没有这篇，直接收下云端。没有对照表但时间一致，也认云端，避免拆成冲突副本。
            guard let localUpdatedAt else { return .useRemote }
            if sameTime(localUpdatedAt, remoteUpdatedAt) { return .useRemote }
            return dirty ? .conflict : .useRemote
        }
        let changed = remoteRev != cursor.baseRev
            || cursor.baseUpdatedAt == nil
            || !sameTime(remoteUpdatedAt, cursor.baseUpdatedAt!)
        if !dirty {
            return changed ? .useRemote : .rememberMtime
        }
        if !changed {
            return .upload(rev: cursor.baseRev == 0 ? 1 : cursor.baseRev + 1)
        }
        return .conflict
    }

    /// 冲突时谁留在原 id 上。返回 true 表示云端这份更新，应留在原笔记。
    public static func remoteWins(localUpdatedAt: Date, remoteUpdatedAt: Date) -> Bool {
        remoteUpdatedAt >= localUpdatedAt
    }
}

public enum UploadChunks {
    public static let byteSize = 4 * 1024 * 1024

    public static func slices(_ data: Data) -> [Data] {
        if data.isEmpty { return [Data()] }
        var result: [Data] = []
        var offset = 0
        while offset < data.count {
            let end = min(offset + byteSize, data.count)
            result.append(data.subdata(in: offset..<end))
            offset = end
        }
        return result
    }
}

public enum NoteAttachmentIndex {
    public static func relativePaths(in body: String) -> [String] {
        guard let regex = try? NSRegularExpression(
            pattern: #"attachments/[0-9A-Fa-f-]{36}/[0-9A-Fa-f-]{36}\.[A-Za-z0-9]+"#
        ) else { return [] }
        let ns = body as NSString
        let matches = regex.matches(in: body, range: NSRange(location: 0, length: ns.length))
        var seen = Set<String>()
        var result: [String] = []
        for match in matches {
            let path = ns.substring(with: match.range)
            if seen.insert(path).inserted { result.append(path) }
        }
        return result
    }
}

public struct NotebookCloud: Codable, Equatable, Sendable {
    public var names: [String]
    public var removed: [String]

    public init(names: [String] = [], removed: [String] = []) {
        self.names = names
        self.removed = removed
    }
}

public enum NotebookMerge {
    /// 名单取并集，删除记入 removed。内置笔记本不进这份文件。
    public static func merge(localNames: [String],
                             localRemoved: [String],
                             remote: NotebookCloud?) -> NotebookCloud {
        let remoteNames = remote?.names ?? []
        let remoteRemoved = remote?.removed ?? []
        var removed = Array(Set(localRemoved + remoteRemoved)).sorted()
        let removedSet = Set(removed)
        let names = Array(Set(localNames + remoteNames).subtracting(removedSet)).sorted()
        removed = removed.filter { !names.contains($0) }
        return NotebookCloud(names: names, removed: removed)
    }
}

/// 对照表落在本机，不上传。
public final class SyncCursorStore {
    private let fileURL: URL
    private var cursors: [UUID: SyncCursor] = [:]

    public init(fileURL: URL) {
        self.fileURL = fileURL
        load()
    }

    public func cursor(for id: UUID) -> SyncCursor {
        cursors[id] ?? .unsynced
    }

    public func markDirty(_ id: UUID) {
        var cursor = cursors[id] ?? .unsynced
        cursor.dirty = true
        cursors[id] = cursor
        save()
        NotificationCenter.default.post(name: .tonoteLocalDataChanged, object: nil)
    }

    public func set(_ cursor: SyncCursor, for id: UUID) {
        cursors[id] = cursor
        save()
    }

    private func load() {
        guard let data = try? Data(contentsOf: fileURL) else { return }
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        if let loaded = try? decoder.decode([UUID: SyncCursor].self, from: data) {
            cursors = loaded
        }
    }

    private func save() {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        encoder.dateEncodingStrategy = .iso8601
        guard let data = try? encoder.encode(cursors) else { return }
        try? data.write(to: fileURL, options: .atomic)
    }
}
