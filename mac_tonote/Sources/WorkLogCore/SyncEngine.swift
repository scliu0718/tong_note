import Foundation
import Combine

/// 按篇同步：每篇笔记一个云端文件。启动、保存后、回到窗口和定时都会跑同一轮。
/// 失败只留到下一轮，不因此退出登录。
public final class SyncEngine: ObservableObject {
    public enum Status: Equatable {
        case idle
        case syncing
        case success(Date)
        case failed(String)

        public var isSyncing: Bool { self == .syncing }
    }

    @Published public private(set) var status: Status = .idle
    @Published public private(set) var lastMessage: String?
    @Published public private(set) var lastSyncDate: Date? {
        didSet {
            if let date = lastSyncDate { UserDefaults.standard.set(date, forKey: Self.lastSyncKey) }
        }
    }

    private let client = BaiduPanClient.shared
    private var scheduled: Task<Void, Never>?
    private var needsAnotherRound = false
    private static let lastSyncKey = "tonote.lastSyncDate"

    private let encoder: JSONEncoder = {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        encoder.dateEncodingStrategy = .iso8601
        return encoder
    }()
    private let decoder: JSONDecoder = {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return decoder
    }()

    public init() {
        lastSyncDate = UserDefaults.standard.object(forKey: Self.lastSyncKey) as? Date
    }

    /// 保存后一两秒再同步。连续保存只跑最后一轮。
    @MainActor
    public func schedule(store: NoteStore, todos: TodoStore) {
        scheduled?.cancel()
        scheduled = Task { @MainActor in
            try? await Task.sleep(nanoseconds: 1_500_000_000)
            guard !Task.isCancelled else { return }
            await self.syncRound(store: store, todos: todos)
        }
    }

    @MainActor
    public func quickSync(store: NoteStore, todos: TodoStore) async {
        await syncRound(store: store, todos: todos)
    }

    @MainActor
    public func syncSingleNote(store: NoteStore, todos: TodoStore, noteID: UUID) async {
        await syncRound(store: store, todos: todos, onlyNoteID: noteID)
    }

    @MainActor
    public func syncRound(store: NoteStore, todos: TodoStore, onlyNoteID: UUID? = nil) async {
        if status.isSyncing {
            needsAnotherRound = true
            return
        }
        status = .syncing
        lastMessage = nil
        var kicked = false
        do {
            let token = try await client.validToken()
            try await DeviceRegistry.shared.ensureNotKicked(accessToken: token.accessToken)
            let noteCount = try await syncNotes(store: store, token: token.accessToken, onlyNoteID: onlyNoteID)
            var todoCount = 0
            if onlyNoteID == nil {
                try await syncNotebooks(token: token.accessToken)
                todoCount = try await syncTodos(store: todos, token: token.accessToken)
            }
            let date = Date()
            status = .success(date)
            lastSyncDate = date
            lastMessage = onlyNoteID == nil
                ? "笔记 \(noteCount) 篇 · 任务 \(todoCount) 条"
                : "已同步本篇"
        } catch DeviceSessionError.kicked {
            kicked = true
            status = .failed(DeviceSessionError.kicked.localizedDescription)
            lastMessage = DeviceSessionError.kicked.localizedDescription
        } catch {
            status = .failed(error.localizedDescription)
            lastMessage = error.localizedDescription
        }
        let again = needsAnotherRound && !kicked
        needsAnotherRound = false
        if again {
            await syncRound(store: store, todos: todos, onlyNoteID: onlyNoteID)
        }
    }

    // MARK: - 笔记

    private func syncNotes(store: NoteStore, token: String, onlyNoteID: UUID?) async throws -> Int {
        try await migrateNotesIfNeeded(store: store, token: token)
        let remoteFiles = try await listJSON(directory: notesDirectory, token: token)
        var remoteIDs = Set(remoteFiles.keys)
        var localIDs = Set(store.notes.map(\.id))
        if let onlyNoteID {
            remoteIDs = remoteIDs.contains(onlyNoteID) ? [onlyNoteID] : []
            localIDs = localIDs.contains(onlyNoteID) ? [onlyNoteID] : []
        }
        var changed = 0
        var firstError: Error?
        for id in remoteIDs.union(localIDs) {
            do {
                let didChange = try await syncOneNote(id: id,
                                                      listing: remoteFiles[id],
                                                      store: store,
                                                      token: token)
                if didChange { changed += 1 }
            } catch DeviceSessionError.kicked {
                throw DeviceSessionError.kicked
            } catch {
                firstError = firstError ?? error
            }
        }
        if let firstError { throw firstError }
        return changed
    }

    /// `notes/` 还是空的、旧清单还在时，把清单拆成按篇文件。之后不再写清单。
    private func migrateNotesIfNeeded(store: NoteStore, token: String) async throws {
        let existing = try await listJSON(directory: notesDirectory, token: token)
        guard existing.isEmpty else { return }
        let remote = try await pullManifestNotes(token: token)
        guard !remote.isEmpty else { return }
        let merged = mergeByUpdatedAt(local: store.notes, remote: remote)
        for note in merged {
            var publishing = note
            publishing.rev = max(publishing.rev, 1)
            var cursor = store.syncCursor(for: publishing.id)
            try await uploadAttachments(of: publishing, cursor: &cursor, store: store, token: token)
            try await uploadNote(publishing, token: token)
            cursor.baseRev = publishing.rev
            cursor.baseUpdatedAt = publishing.updatedAt
            cursor.dirty = false
            cursor.serverMtime = nil
            store.applySyncedNote(publishing, cursor: cursor)
        }
    }

    private func syncOneNote(id: UUID,
                             listing: BaiduPanListEntry?,
                             store: NoteStore,
                             token: String) async throws -> Bool {
        let local = store.note(with: id)
        var cursor = local == nil ? SyncCursor.unsynced : store.syncCursor(for: id)
        if let mtime = listing?.serverMtime, SyncRules.shouldSkipFetch(serverMtime: mtime, cursor: cursor) {
            return false
        }
        let remote = try await downloadNote(id: id, token: token)
        let step = SyncRules.afterFetch(dirty: cursor.dirty,
                                        remoteRev: remote?.rev,
                                        remoteUpdatedAt: remote?.updatedAt,
                                        localUpdatedAt: local?.updatedAt,
                                        cursor: cursor)
        switch step {
        case .skip:
            return false
        case .rememberMtime:
            guard let local else { return false }
            cursor.serverMtime = listing?.serverMtime
            cursor.dirty = false
            store.applySyncedNote(local, cursor: cursor)
            return false
        case .useRemote:
            guard let remote else { return false }
            cursor = SyncCursor(baseRev: remote.rev,
                                baseUpdatedAt: remote.updatedAt,
                                serverMtime: listing?.serverMtime,
                                dirty: false,
                                uploadedAttachments: cursor.uploadedAttachments)
            if await downloadAttachments(of: remote, cursor: &cursor, store: store, token: token) == false {
                cursor.serverMtime = nil
            }
            store.applySyncedNote(remote, cursor: cursor)
            return true
        case .upload(let rev):
            guard var note = local else { return false }
            let published = try await publish(note: &note,
                                              rev: rev,
                                              cursor: &cursor,
                                              store: store,
                                              token: token,
                                              listingMtime: listing?.serverMtime)
            return published
        case .conflict:
            guard let local, let remote else { return false }
            try await resolveConflict(local: local,
                                      remote: remote,
                                      cursor: cursor,
                                      store: store,
                                      token: token,
                                      listingMtime: listing?.serverMtime)
            return true
        }
    }

    private func publish(note: inout Note,
                         rev: Int,
                         cursor: inout SyncCursor,
                         store: NoteStore,
                         token: String,
                         listingMtime: Int?) async throws -> Bool {
        var attemptCursor = cursor
        var publishing = note
        var nextRev = rev
        for _ in 0..<3 {
            try await DeviceRegistry.shared.ensureNotKicked(accessToken: token)
            publishing.rev = nextRev
            if publishing.purged {
                try await uploadNote(publishing, token: token)
            } else {
                try await uploadAttachments(of: publishing, cursor: &attemptCursor, store: store, token: token)
                try await uploadNote(publishing, token: token)
            }
            let check = try await downloadNote(id: publishing.id, token: token)
            if check?.rev == publishing.rev,
               let updated = check?.updatedAt,
               SyncRules.sameTime(updated, publishing.updatedAt) {
                attemptCursor.baseRev = publishing.rev
                attemptCursor.baseUpdatedAt = publishing.updatedAt
                attemptCursor.dirty = false
                attemptCursor.serverMtime = listingMtime
                if publishing.purged {
                    let prefix = "attachments/\(publishing.id.uuidString)/"
                    let stillUsed = store.notes.contains { other in
                        other.id != publishing.id && !other.purged && other.body.contains(prefix)
                    }
                    if !stillUsed {
                        try? await client.deleteRemote(path: attachmentDirectory(publishing.id), accessToken: token)
                    }
                }
                store.applySyncedNote(publishing, cursor: attemptCursor)
                note = publishing
                cursor = attemptCursor
                return true
            }
            if let check {
                let again = SyncRules.afterFetch(dirty: true,
                                                 remoteRev: check.rev,
                                                 remoteUpdatedAt: check.updatedAt,
                                                 localUpdatedAt: publishing.updatedAt,
                                                 cursor: attemptCursor)
                if case .upload(let next) = again {
                    nextRev = next
                    continue
                }
                if case .conflict = again, let current = store.note(with: publishing.id) {
                    try await resolveConflict(local: current,
                                              remote: check,
                                              cursor: attemptCursor,
                                              store: store,
                                              token: token,
                                              listingMtime: listingMtime)
                    return true
                }
            }
        }
        throw BaiduPanError.server(message: "笔记「\(publishing.title)」连续三次没能写上云端")
    }

    private func resolveConflict(local: Note,
                                 remote: Note,
                                 cursor: SyncCursor,
                                 store: NoteStore,
                                 token: String,
                                 listingMtime: Int?) async throws {
        if SyncRules.remoteWins(localUpdatedAt: local.updatedAt, remoteUpdatedAt: remote.updatedAt) {
            var copy = conflictCopy(of: local)
            copy.rev = 0
            var copyCursor = SyncCursor(uploadedAttachments: cursor.uploadedAttachments)
            store.applySyncedNote(copy, cursor: copyCursor)
            _ = try await publish(note: &copy, rev: 1, cursor: &copyCursor, store: store, token: token, listingMtime: nil)
            var clean = SyncCursor(baseRev: remote.rev,
                                   baseUpdatedAt: remote.updatedAt,
                                   serverMtime: listingMtime,
                                   dirty: false,
                                   uploadedAttachments: cursor.uploadedAttachments)
            _ = await downloadAttachments(of: remote, cursor: &clean, store: store, token: token)
            store.applySyncedNote(remote, cursor: clean)
        } else {
            var copy = conflictCopy(of: remote)
            var copyCursor = SyncCursor(uploadedAttachments: cursor.uploadedAttachments)
            _ = await downloadAttachments(of: copy, cursor: &copyCursor, store: store, token: token)
            store.applySyncedNote(copy, cursor: copyCursor)
            _ = try await publish(note: &copy, rev: 1, cursor: &copyCursor, store: store, token: token, listingMtime: nil)
            var winner = local
            var winnerCursor = cursor
            let rev = max(local.rev, remote.rev) + 1
            _ = try await publish(note: &winner, rev: rev, cursor: &winnerCursor, store: store, token: token, listingMtime: listingMtime)
        }
    }

    private func conflictCopy(of note: Note) -> Note {
        var copy = note
        copy.id = UUID()
        if !copy.title.hasSuffix("（冲突副本）") {
            copy.title += "（冲突副本）"
        }
        copy.rev = 0
        copy.purged = false
        return copy
    }

    // MARK: - 图片

    private func uploadAttachments(of note: Note,
                                   cursor: inout SyncCursor,
                                   store: NoteStore,
                                   token: String) async throws {
        for path in NoteAttachmentIndex.relativePaths(in: note.body) where !cursor.uploadedAttachments.contains(path) {
            let file = store.attachments.absoluteURL(for: path)
            guard FileManager.default.fileExists(atPath: file.path) else { continue }
            let data = try Data(contentsOf: file)
            try await client.uploadData(data, to: BaiduPanConfig.remoteRoot + "/" + path, accessToken: token)
            cursor.uploadedAttachments.append(path)
            if let current = store.note(with: note.id) {
                var persisted = cursor
                persisted.dirty = true
                store.applySyncedNote(current, cursor: persisted)
            }
        }
    }

    /// 返回 false 表示还有图片没拉下来，下一轮要再试。
    private func downloadAttachments(of note: Note,
                                     cursor: inout SyncCursor,
                                     store: NoteStore,
                                     token: String) async -> Bool {
        var complete = true
        for path in NoteAttachmentIndex.relativePaths(in: note.body) {
            if !store.attachments.fileExists(relativePath: path) {
                do {
                    let data = try await client.downloadData(BaiduPanConfig.remoteRoot + "/" + path, accessToken: token)
                    let dest = store.attachments.absoluteURL(for: path)
                    try FileManager.default.createDirectory(at: dest.deletingLastPathComponent(),
                                                           withIntermediateDirectories: true)
                    try data.write(to: dest)
                } catch BaiduPanError.notFound {
                    continue
                } catch {
                    complete = false
                    continue
                }
            }
            if !cursor.uploadedAttachments.contains(path) {
                cursor.uploadedAttachments.append(path)
            }
        }
        return complete
    }

    // MARK: - 笔记本与任务

    private func syncNotebooks(token: String) async throws {
        let notebooks = NotebookStore.shared
        let remote = try await downloadNotebooks(token: token)
        let merged = NotebookMerge.merge(localNames: notebooks.custom,
                                         localRemoved: notebooks.removed,
                                         remote: remote)
        let changed = merged != remote || notebooks.isDirty
        notebooks.applyCloud(names: merged.names, removed: merged.removed)
        if changed {
            let data = try encoder.encode(merged)
            try await client.uploadData(data, to: notebooksPath, accessToken: token)
        }
    }

    private func syncTodos(store: TodoStore, token: String) async throws -> Int {
        try await migrateTodosIfNeeded(store: store, token: token)
        let remoteFiles = try await listJSON(directory: todosDirectory, token: token)
        let ids = Set(remoteFiles.keys).union(store.todos.map(\.id))
        var changed = 0
        var firstError: Error?
        for id in ids {
            do {
                if try await syncOneTodo(id: id, listing: remoteFiles[id], store: store, token: token) {
                    changed += 1
                }
            } catch DeviceSessionError.kicked {
                throw DeviceSessionError.kicked
            } catch {
                firstError = firstError ?? error
            }
        }
        if let firstError { throw firstError }
        return changed
    }

    private func migrateTodosIfNeeded(store: TodoStore, token: String) async throws {
        let existing = try await listJSON(directory: todosDirectory, token: token)
        guard existing.isEmpty else { return }
        let remote = try await pullManifestTodos(token: token)
        guard !remote.isEmpty else { return }
        let merged = mergeTodos(local: store.todos, remote: remote)
        for todo in merged {
            var publishing = todo
            publishing.rev = max(publishing.rev, 1)
            try await uploadTodo(publishing, token: token)
            let cursor = SyncCursor(baseRev: publishing.rev,
                                    baseUpdatedAt: publishing.updatedAt,
                                    serverMtime: nil,
                                    dirty: false,
                                    uploadedAttachments: [])
            store.applySyncedTodo(publishing, cursor: cursor)
        }
    }

    private func syncOneTodo(id: UUID,
                             listing: BaiduPanListEntry?,
                             store: TodoStore,
                             token: String) async throws -> Bool {
        let local = store.todo(with: id)
        var cursor = local == nil ? SyncCursor.unsynced : store.syncCursor(for: id)
        if let mtime = listing?.serverMtime, SyncRules.shouldSkipFetch(serverMtime: mtime, cursor: cursor) {
            return false
        }
        let remote = try await downloadTodo(id: id, token: token)
        let step = SyncRules.afterFetch(dirty: cursor.dirty,
                                        remoteRev: remote?.rev,
                                        remoteUpdatedAt: remote?.updatedAt,
                                        localUpdatedAt: local?.updatedAt,
                                        cursor: cursor)
        switch step {
        case .skip:
            return false
        case .rememberMtime:
            guard let local else { return false }
            cursor.serverMtime = listing?.serverMtime
            cursor.dirty = false
            store.applySyncedTodo(local, cursor: cursor)
            return false
        case .useRemote:
            guard let remote else { return false }
            var clean = SyncCursor(baseRev: remote.rev,
                                   baseUpdatedAt: remote.updatedAt,
                                   serverMtime: listing?.serverMtime,
                                   dirty: false,
                                   uploadedAttachments: cursor.uploadedAttachments)
            if await downloadTodoImages(remote, cursor: &clean, store: store, token: token) == false {
                clean.serverMtime = nil
            }
            store.applySyncedTodo(remote, cursor: clean)
            return true
        case .upload(let rev):
            guard var todo = local else { return false }
            return try await publishTodo(&todo, rev: rev, cursor: &cursor, store: store, token: token, listingMtime: listing?.serverMtime)
        case .conflict:
            guard let local, let remote else { return false }
            try await resolveTodoConflict(local: local, remote: remote, cursor: cursor, store: store, token: token, listingMtime: listing?.serverMtime)
            return true
        }
    }

    private func publishTodo(_ todo: inout Todo,
                             rev: Int,
                             cursor: inout SyncCursor,
                             store: TodoStore,
                             token: String,
                             listingMtime: Int?) async throws -> Bool {
        var publishing = todo
        var attempt = cursor
        var nextRev = rev
        for _ in 0..<3 {
            try await DeviceRegistry.shared.ensureNotKicked(accessToken: token)
            publishing.rev = nextRev
            if !publishing.purged {
                try await uploadTodoImages(publishing, cursor: &attempt, store: store, token: token)
            }
            try await uploadTodo(publishing, token: token)
            let check = try await downloadTodo(id: publishing.id, token: token)
            if check?.rev == publishing.rev,
               let updated = check?.updatedAt,
               SyncRules.sameTime(updated, publishing.updatedAt) {
                attempt.baseRev = publishing.rev
                attempt.baseUpdatedAt = publishing.updatedAt
                attempt.dirty = false
                attempt.serverMtime = listingMtime
                store.applySyncedTodo(publishing, cursor: attempt)
                todo = publishing
                cursor = attempt
                return true
            }
            if let check {
                let again = SyncRules.afterFetch(dirty: true,
                                                 remoteRev: check.rev,
                                                 remoteUpdatedAt: check.updatedAt,
                                                 localUpdatedAt: publishing.updatedAt,
                                                 cursor: attempt)
                if case .upload(let next) = again {
                    nextRev = next
                    continue
                }
                if case .conflict = again, let current = store.todo(with: publishing.id) {
                    try await resolveTodoConflict(local: current, remote: check, cursor: attempt,
                                                 store: store, token: token, listingMtime: listingMtime)
                    return true
                }
            }
        }
        throw BaiduPanError.server(message: "任务「\(publishing.title)」连续三次没能写上云端")
    }

    private func resolveTodoConflict(local: Todo,
                                     remote: Todo,
                                     cursor: SyncCursor,
                                     store: TodoStore,
                                     token: String,
                                     listingMtime: Int?) async throws {
        if SyncRules.remoteWins(localUpdatedAt: local.updatedAt, remoteUpdatedAt: remote.updatedAt) {
            var clean = SyncCursor(baseRev: remote.rev, baseUpdatedAt: remote.updatedAt,
                                   serverMtime: listingMtime, dirty: false,
                                   uploadedAttachments: cursor.uploadedAttachments)
            if await downloadTodoImages(remote, cursor: &clean, store: store, token: token) == false {
                clean.serverMtime = nil
            }
            store.applySyncedTodo(remote, cursor: clean)
            var copy = conflictCopy(of: local)
            store.applySyncedTodo(copy, cursor: .unsynced)
            var copyCursor = SyncCursor.unsynced
            _ = try await publishTodo(&copy, rev: 1, cursor: &copyCursor, store: store, token: token, listingMtime: nil)
        } else {
            var copy = conflictCopy(of: remote)
            var copyCursor = SyncCursor.unsynced
            if await downloadTodoImages(copy, cursor: &copyCursor, store: store, token: token) == false {
                copyCursor.serverMtime = nil
            }
            store.applySyncedTodo(copy, cursor: copyCursor)
            _ = try await publishTodo(&copy, rev: 1, cursor: &copyCursor, store: store, token: token, listingMtime: nil)
            var winner = local
            var winnerCursor = cursor
            _ = try await publishTodo(&winner, rev: max(local.rev, remote.rev) + 1, cursor: &winnerCursor, store: store, token: token, listingMtime: listingMtime)
        }
    }

    private func conflictCopy(of todo: Todo) -> Todo {
        var copy = todo
        copy.id = UUID()
        if !copy.title.hasSuffix("（冲突副本）") {
            copy.title += "（冲突副本）"
        }
        copy.rev = 0
        copy.purged = false
        return copy
    }

    /// 任务备注里的图片与笔记共用 `attachments/` 目录，上传时带上相对路径。
    private func uploadTodoImages(_ todo: Todo,
                                  cursor: inout SyncCursor,
                                  store: TodoStore,
                                  token: String) async throws {
        for path in NoteAttachmentIndex.relativePaths(in: todo.notes) where !cursor.uploadedAttachments.contains(path) {
            let file = store.attachments.absoluteURL(for: path)
            guard FileManager.default.fileExists(atPath: file.path) else { continue }
            let data = try Data(contentsOf: file)
            try await client.uploadData(data, to: BaiduPanConfig.remoteRoot + "/" + path, accessToken: token)
            cursor.uploadedAttachments.append(path)
            if let current = store.todo(with: todo.id) {
                var persisted = cursor
                persisted.dirty = true
                store.applySyncedTodo(current, cursor: persisted)
            }
        }
    }

    /// 返回 false 表示还有图片没拉下来，下一轮要再试。
    private func downloadTodoImages(_ todo: Todo,
                                    cursor: inout SyncCursor,
                                    store: TodoStore,
                                    token: String) async -> Bool {
        var complete = true
        for path in NoteAttachmentIndex.relativePaths(in: todo.notes) {
            if !store.attachments.fileExists(relativePath: path) {
                do {
                    let data = try await client.downloadData(BaiduPanConfig.remoteRoot + "/" + path, accessToken: token)
                    let dest = store.attachments.absoluteURL(for: path)
                    try FileManager.default.createDirectory(at: dest.deletingLastPathComponent(),
                                                           withIntermediateDirectories: true)
                    try data.write(to: dest)
                } catch BaiduPanError.notFound {
                    continue
                } catch {
                    complete = false
                    continue
                }
            }
            if !cursor.uploadedAttachments.contains(path) {
                cursor.uploadedAttachments.append(path)
            }
        }
        return complete
    }

    // MARK: - 网盘读写

    private var notesDirectory: String { BaiduPanConfig.remoteRoot + "/notes" }
    private var todosDirectory: String { BaiduPanConfig.remoteRoot + "/todos" }
    private var notebooksPath: String { BaiduPanConfig.remoteRoot + "/notebooks.json" }
    private var manifestPath: String { BaiduPanConfig.remoteRoot + "/manifest.json" }

    private func notePath(_ id: UUID) -> String { notesDirectory + "/" + id.uuidString + ".json" }
    private func todoPath(_ id: UUID) -> String { todosDirectory + "/" + id.uuidString + ".json" }
    private func attachmentDirectory(_ id: UUID) -> String {
        BaiduPanConfig.remoteRoot + "/attachments/" + id.uuidString
    }

    private func listJSON(directory: String, token: String) async throws -> [UUID: BaiduPanListEntry] {
        let entries: [BaiduPanListEntry]
        do {
            entries = try await client.listFiles(directory: directory, accessToken: token)
        } catch BaiduPanError.notFound {
            return [:]
        }
        var result: [UUID: BaiduPanListEntry] = [:]
        for entry in entries {
            let name = entry.serverFilename ?? entry.path?.split(separator: "/").last.map(String.init) ?? ""
            guard entry.isdir != 1, name.hasSuffix(".json") else { continue }
            let stem = name.replacingOccurrences(of: ".json", with: "")
            guard let id = UUID(uuidString: stem) else { continue }
            result[id] = entry
        }
        return result
    }

    private func downloadNote(id: UUID, token: String) async throws -> Note? {
        do {
            let data = try await client.downloadData(notePath(id), accessToken: token)
            return try decoder.decode(Note.self, from: data)
        } catch BaiduPanError.notFound {
            return nil
        }
    }

    private func uploadNote(_ note: Note, token: String) async throws {
        try await client.uploadData(try encoder.encode(note), to: notePath(note.id), accessToken: token)
    }

    private func downloadTodo(id: UUID, token: String) async throws -> Todo? {
        do {
            let data = try await client.downloadData(todoPath(id), accessToken: token)
            return try decoder.decode(Todo.self, from: data)
        } catch BaiduPanError.notFound {
            return nil
        }
    }

    private func uploadTodo(_ todo: Todo, token: String) async throws {
        try await client.uploadData(try encoder.encode(todo), to: todoPath(todo.id), accessToken: token)
    }

    private func downloadNotebooks(token: String) async throws -> NotebookCloud? {
        do {
            let data = try await client.downloadData(notebooksPath, accessToken: token)
            return try decoder.decode(NotebookCloud.self, from: data)
        } catch BaiduPanError.notFound {
            return nil
        }
    }

    private struct OldManifest: Codable {
        var notes: [Note]
        var todos: [Todo]
        enum CodingKeys: String, CodingKey { case notes, todos }
        init(from decoder: Decoder) throws {
            let c = try decoder.container(keyedBy: CodingKeys.self)
            notes = try c.decodeIfPresent([Note].self, forKey: .notes) ?? []
            todos = try c.decodeIfPresent([Todo].self, forKey: .todos) ?? []
        }
    }

    private func pullManifest(token: String) async throws -> OldManifest? {
        do {
            let data = try await client.downloadData(manifestPath, accessToken: token)
            return try decoder.decode(OldManifest.self, from: data)
        } catch BaiduPanError.notFound {
            return nil
        }
    }

    private func pullManifestNotes(token: String) async throws -> [Note] {
        try await pullManifest(token: token)?.notes ?? []
    }

    private func pullManifestTodos(token: String) async throws -> [Todo] {
        try await pullManifest(token: token)?.todos ?? []
    }

    private func mergeByUpdatedAt(local: [Note], remote: [Note]) -> [Note] {
        var map: [UUID: Note] = [:]
        for note in remote { map[note.id] = note }
        for note in local {
            if let other = map[note.id] {
                map[note.id] = note.updatedAt >= other.updatedAt ? note : other
            } else {
                map[note.id] = note
            }
        }
        return Array(map.values)
    }

    private func mergeTodos(local: [Todo], remote: [Todo]) -> [Todo] {
        var map: [UUID: Todo] = [:]
        for todo in remote { map[todo.id] = todo }
        for todo in local {
            if let other = map[todo.id] {
                map[todo.id] = todo.updatedAt >= other.updatedAt ? todo : other
            } else {
                map[todo.id] = todo
            }
        }
        return Array(map.values)
    }
}
