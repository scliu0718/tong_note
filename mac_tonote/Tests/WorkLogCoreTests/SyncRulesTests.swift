import XCTest
@testable import WorkLogCore

final class SyncRulesTests: XCTestCase {
    private let now = Date(timeIntervalSince1970: 1_700_000_000)

    func testSkipWhenMtimeMatchesAndClean() {
        let cursor = SyncCursor(baseRev: 2, baseUpdatedAt: now, serverMtime: 10, dirty: false)
        XCTAssertTrue(SyncRules.shouldSkipFetch(serverMtime: 10, cursor: cursor))
        XCTAssertFalse(SyncRules.shouldSkipFetch(serverMtime: 11, cursor: cursor))
        XCTAssertFalse(SyncRules.shouldSkipFetch(serverMtime: 10, cursor: SyncCursor(baseRev: 2, baseUpdatedAt: now, serverMtime: 10, dirty: true)))
    }

    func testCleanRemoteChangeReplacesLocal() {
        let cursor = SyncCursor(baseRev: 1, baseUpdatedAt: now, serverMtime: 1, dirty: false)
        let step = SyncRules.afterFetch(dirty: false,
                                        remoteRev: 2,
                                        remoteUpdatedAt: now.addingTimeInterval(5),
                                        localUpdatedAt: now,
                                        cursor: cursor)
        XCTAssertEqual(step, .useRemote)
    }

    func testDirtyUnchangedRemoteUploadsNextRev() {
        let cursor = SyncCursor(baseRev: 3, baseUpdatedAt: now, serverMtime: 1, dirty: true)
        let step = SyncRules.afterFetch(dirty: true,
                                        remoteRev: 3,
                                        remoteUpdatedAt: now,
                                        localUpdatedAt: now.addingTimeInterval(10),
                                        cursor: cursor)
        XCTAssertEqual(step, .upload(rev: 4))
    }

    func testDirtyRemoteChangeConflicts() {
        let cursor = SyncCursor(baseRev: 3, baseUpdatedAt: now, serverMtime: 1, dirty: true)
        let step = SyncRules.afterFetch(dirty: true,
                                        remoteRev: 4,
                                        remoteUpdatedAt: now.addingTimeInterval(2),
                                        localUpdatedAt: now.addingTimeInterval(9),
                                        cursor: cursor)
        XCTAssertEqual(step, .conflict)
        XCTAssertFalse(SyncRules.remoteWins(localUpdatedAt: now.addingTimeInterval(9),
                                            remoteUpdatedAt: now.addingTimeInterval(2)))
    }

    func testFirstSyncSameTimeAdoptsRemote() {
        let remoteTime = now
        let step = SyncRules.afterFetch(dirty: true,
                                        remoteRev: 1,
                                        remoteUpdatedAt: remoteTime,
                                        localUpdatedAt: remoteTime.addingTimeInterval(0.2),
                                        cursor: .unsynced)
        XCTAssertEqual(step, .useRemote)
    }

    func testCloudOnlyNoteIsAdopted() {
        let step = SyncRules.afterFetch(dirty: true,
                                        remoteRev: 2,
                                        remoteUpdatedAt: now,
                                        localUpdatedAt: nil,
                                        cursor: .unsynced)
        XCTAssertEqual(step, .useRemote)
    }

    func testMissingRemoteUploadsRevOne() {
        let step = SyncRules.afterFetch(dirty: true,
                                        remoteRev: nil,
                                        remoteUpdatedAt: nil,
                                        cursor: .unsynced)
        XCTAssertEqual(step, .upload(rev: 1))
    }

    func testChunksStayWithinFourMegabytes() {
        let data = Data(repeating: 1, count: UploadChunks.byteSize * 2 + 10)
        let slices = UploadChunks.slices(data)
        XCTAssertEqual(slices.count, 3)
        XCTAssertEqual(slices[0].count, UploadChunks.byteSize)
        XCTAssertEqual(slices[2].count, 10)
    }

    func testAttachmentPathsKeepOriginalExtension() {
        let body = """
        <img src="attachments/AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE/11111111-2222-3333-4444-555555555555.png">
        <img src="attachments/AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE/11111111-2222-3333-4444-555555555555.png">
        """
        XCTAssertEqual(
            NoteAttachmentIndex.relativePaths(in: body),
            ["attachments/AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE/11111111-2222-3333-4444-555555555555.png"]
        )
    }

    func testTodoMergeTakesRemoteNotesAndLocalStatus() {
        let id = UUID()
        let base = sampleTodo(id: id, title: "买菜", notes: "", status: .todo, rev: 1)
        var local = base
        local.status = .doing
        local.updatedAt = now.addingTimeInterval(5)
        var remote = base
        remote.notes = "记得带袋子"
        remote.updatedAt = now.addingTimeInterval(8)
        switch SyncRules.mergeTodos(base: base, local: local, remote: remote) {
        case .merged(let merged):
            XCTAssertEqual(merged.id, id)
            XCTAssertEqual(merged.status, .doing)
            XCTAssertEqual(merged.notes, "记得带袋子")
            XCTAssertEqual(merged.title, "买菜")
        case .unresolved:
            XCTFail("交叉改状态和备注应能自动合并")
        }
    }

    func testTodoMergeBothNotesStayUnresolved() {
        let id = UUID()
        let base = sampleTodo(id: id, notes: "原备注", rev: 2)
        var local = base
        local.notes = "本机改了"
        var remote = base
        remote.notes = "云端改了"
        XCTAssertEqual(SyncRules.mergeTodos(base: base, local: local, remote: remote), .unresolved)
    }

    func testTodoMergeWithoutSnapshotStaysUnresolved() {
        let local = sampleTodo(title: "A", status: .doing)
        let remote = sampleTodo(id: local.id, title: "A", notes: "云端")
        XCTAssertEqual(SyncRules.mergeTodos(base: nil, local: local, remote: remote), .unresolved)
    }

    func testTodoMergePrefersFurtherStatusAndTombstone() {
        let id = UUID()
        let base = sampleTodo(id: id, status: .todo, rev: 1)
        var local = base
        local.status = .done
        local.completedAt = now
        var remote = base
        remote.status = .terminated
        remote.purged = true
        switch SyncRules.mergeTodos(base: base, local: local, remote: remote) {
        case .merged(let merged):
            XCTAssertEqual(merged.status, .terminated)
            XCTAssertTrue(merged.purged)
            XCTAssertEqual(merged.completedAt, now)
        case .unresolved:
            XCTFail("状态和墓碑应能自动合并")
        }
    }

    func testTodoMergeDeadlineTakesTheSideThatSetIt() {
        let id = UUID()
        let base = sampleTodo(id: id, rev: 1)
        var local = base
        local.plannedAt = now.addingTimeInterval(86_400)
        var remote = base
        remote.title = "改名"
        switch SyncRules.mergeTodos(base: base, local: local, remote: remote) {
        case .merged(let merged):
            XCTAssertEqual(merged.plannedAt, now.addingTimeInterval(86_400))
            XCTAssertEqual(merged.title, "改名")
        case .unresolved:
            XCTFail("截止时间和标题交叉修改应能自动合并")
        }
    }

    private func sampleTodo(id: UUID = UUID(),
                            title: String = "任务",
                            notes: String = "",
                            status: TodoStatus = .todo,
                            rev: Int = 1) -> Todo {
        Todo(id: id,
             title: title,
             notes: notes,
             status: status,
             createdAt: now,
             updatedAt: now,
             rev: rev)
    }

    func testNotebookNamesUnionAndRemovals() {
        let merged = NotebookMerge.merge(localNames: ["客户", "草稿"],
                                         localRemoved: ["旧本"],
                                         remote: NotebookCloud(names: ["草稿", "项目"], removed: ["草稿"]))
        XCTAssertEqual(merged.names, ["客户", "项目"])
        XCTAssertEqual(merged.removed, ["旧本", "草稿"])
    }
}
