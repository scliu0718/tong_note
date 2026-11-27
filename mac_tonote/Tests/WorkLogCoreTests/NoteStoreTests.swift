import XCTest
@testable import WorkLogCore

@MainActor
final class NoteStoreTests: XCTestCase {

    private var tempDir: URL!
    private var store: NoteStore!

    override func setUp() async throws {
        tempDir = FileManager.default.temporaryDirectory
            .appendingPathComponent("WorkLogTests-\(UUID().uuidString)", isDirectory: true)
        store = NoteStore(directory: tempDir)
    }

    override func tearDown() async throws {
        store = nil
        try? FileManager.default.removeItem(at: tempDir)
    }

    // 1. 新建笔记
    func testAddNote() {
        let before = store.notes.count
        let note = store.addNote(notebook: "投资研究")
        XCTAssertEqual(store.notes.count, before + 1)
        XCTAssertEqual(note.title, "未命名笔记")
        XCTAssertEqual(note.notebook, "投资研究")
        XCTAssertEqual(store.notes.first?.id, note.id, "新笔记应插入到最前")
    }

    // 2. 编辑（update 写回）
    func testEditNote() {
        let note = store.addNote()
        var edited = store.note(with: note.id)!
        edited.title = "新标题"
        edited.body = "正文内容"
        store.update(edited)
        let updated = store.note(with: note.id)
        XCTAssertEqual(updated?.title, "新标题")
        XCTAssertEqual(updated?.body, "正文内容")
    }

    // 同步回写的竞态保护：同步期间用户继续打字后，旧快照不能再覆盖本地
    func testIsUnchangedDetectsEditsDuringSync() {
        let note = store.addNote()
        let snapshot = store.note(with: note.id)!
        XCTAssertTrue(store.isUnchanged(note.id, since: snapshot))

        store.updateContent(note.id, body: "同步期间刚写的内容", format: .markdown)
        XCTAssertFalse(store.isUnchanged(note.id, since: snapshot), "正文被改过，必须判为已变化")

        XCTAssertTrue(store.isUnchanged(UUID(), since: nil), "本地没有且快照为空视为未变")
        XCTAssertFalse(store.isUnchanged(note.id, since: nil), "快照为空但本地已有，判为已变化")
    }

    // 3. 删除 → 回收站
    func testMoveToTrash() {
        let note = store.addNote()
        store.moveToTrash(note.id)
        XCTAssertEqual(store.note(with: note.id)?.isTrashed, true)
        XCTAssertTrue(store.trashNotes.contains { $0.id == note.id })
        XCTAssertFalse(store.activeNotes.contains { $0.id == note.id })
    }

    // 3b. 回收站超过 30 天自动彻底删除
    func testPurgeExpiredTrashOnlyOlderThan30Days() {
        let old = store.addNote()
        let fresh = store.addNote()
        let alive = store.addNote()
        store.moveToTrash(old.id)
        store.moveToTrash(fresh.id)
        let now = Date()
        // 直接改 deletedAt 模拟时间流逝：old 31 天前、fresh 29 天前
        var notes = store.notes
        notes[notes.firstIndex { $0.id == old.id }!].deletedAt = now.addingTimeInterval(-31 * 86_400)
        notes[notes.firstIndex { $0.id == fresh.id }!].deletedAt = now.addingTimeInterval(-29 * 86_400)
        store.setNotes(notes)

        XCTAssertEqual(store.purgeExpiredTrash(now: now), 1)
        XCTAssertEqual(store.note(with: old.id)?.purged, true)
        XCTAssertEqual(store.note(with: fresh.id)?.purged, false)
        XCTAssertEqual(store.note(with: alive.id)?.purged, false)
        XCTAssertEqual(store.purgeExpiredTrash(now: now), 0)
    }

    // 4. 恢复
    func testRestore() {
        let note = store.addNote()
        store.moveToTrash(note.id)
        store.restore(note.id)
        XCTAssertEqual(store.note(with: note.id)?.isTrashed, false)
        XCTAssertTrue(store.activeNotes.contains { $0.id == note.id })
    }

    // 5. 彻底删除
    func testPermanentlyDelete() {
        let note = store.addNote()
        store.permanentlyDelete(note.id)
        XCTAssertEqual(store.note(with: note.id)?.purged, true)
        XCTAssertFalse(store.activeNotes.contains { $0.id == note.id })
        XCTAssertFalse(store.trashNotes.contains { $0.id == note.id })
    }

    func testTrashBumpsUpdatedAt() {
        let note = store.addNote()
        let before = store.note(with: note.id)!.updatedAt
        store.moveToTrash(note.id)
        let trashed = store.note(with: note.id)!
        XCTAssertNotNil(trashed.deletedAt)
        XCTAssertGreaterThanOrEqual(trashed.updatedAt, before)
        XCTAssertTrue(store.syncCursor(for: note.id).dirty)
    }

    // 6. 置顶切换
    func testTogglePin() {
        let note = store.addNote()
        XCTAssertFalse(store.note(with: note.id)!.pinned)
        store.togglePin(note.id)
        XCTAssertTrue(store.note(with: note.id)!.pinned)
        store.togglePin(note.id)
        XCTAssertFalse(store.note(with: note.id)!.pinned)
    }

    // 7. 持久化（重新加载）
    func testPersistence() {
        let note = store.addNote()
        var edited = store.note(with: note.id)!
        edited.title = "持久化标题"
        store.update(edited)
        let reloaded = NoteStore(directory: tempDir)
        XCTAssertTrue(reloaded.notes.contains { $0.id == note.id && $0.title == "持久化标题" })
    }

    // 8. 标签聚合
    func testTagsAggregation() {
        let note = store.addNote()
        var edited = store.note(with: note.id)!
        edited.tags = ["测试标签", "工作"]
        store.update(edited)
        XCTAssertTrue(store.allTags.contains("测试标签"))
        XCTAssertTrue(store.allTags.contains("工作"))
        XCTAssertTrue(store.allTags.contains("A股复盘"), "样例标签应仍在聚合中")
    }

    // 9. 数量统计
    func testCount() {
        let note = store.addNote(notebook: "投资研究")
        var edited = store.note(with: note.id)!
        edited.tags = ["新标签"]
        store.update(edited)
        XCTAssertEqual(store.count(withTag: "新标签"), 1)
        let visible = store.activeNotes.filter { $0.id != NoteStore.quickSyncNoteID }
        XCTAssertEqual(store.count(inNotebook: Notebooks.all), visible.count)
    }

    // 10. 删除会取消置顶
    func testTrashClearsPin() {
        let note = store.addNote()
        store.togglePin(note.id)
        store.moveToTrash(note.id)
        XCTAssertFalse(store.note(with: note.id)!.pinned)
    }

    // 11. 多笔记正文按 UUID 隔离，并在重新加载后保持正确映射
    func testBodiesRemainIsolatedAcrossNotesAndPersistence() {
        let first = store.addNote()
        let second = store.addNote()
        let third = store.addNote()

        XCTAssertEqual(Set([first.id, second.id, third.id]).count, 3)

        var editedFirst = store.note(with: first.id)!
        editedFirst.body = "第一篇正文-A"
        store.update(editedFirst)

        var editedSecond = store.note(with: second.id)!
        editedSecond.body = "第二篇正文-B"
        store.update(editedSecond)

        var editedThird = store.note(with: third.id)!
        editedThird.body = "第三篇正文-C"
        store.update(editedThird)

        editedSecond = store.note(with: second.id)!
        editedSecond.body = "第二篇正文-B-再次修改"
        store.update(editedSecond)

        XCTAssertEqual(store.note(with: first.id)?.body, "第一篇正文-A")
        XCTAssertEqual(store.note(with: second.id)?.body, "第二篇正文-B-再次修改")
        XCTAssertEqual(store.note(with: third.id)?.body, "第三篇正文-C")

        let reloaded = NoteStore(directory: tempDir)
        XCTAssertEqual(reloaded.note(with: first.id)?.body, "第一篇正文-A")
        XCTAssertEqual(reloaded.note(with: second.id)?.body, "第二篇正文-B-再次修改")
        XCTAssertEqual(reloaded.note(with: third.id)?.body, "第三篇正文-C")
    }
}
