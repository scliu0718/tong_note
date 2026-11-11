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

    func testNotebookNamesUnionAndRemovals() {
        let merged = NotebookMerge.merge(localNames: ["客户", "草稿"],
                                         localRemoved: ["旧本"],
                                         remote: NotebookCloud(names: ["草稿", "项目"], removed: ["草稿"]))
        XCTAssertEqual(merged.names, ["客户", "项目"])
        XCTAssertEqual(merged.removed, ["旧本", "草稿"])
    }
}
