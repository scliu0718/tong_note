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

    // 3. 删除 → 回收站
    func testMoveToTrash() {
        let note = store.addNote()
        store.moveToTrash(note.id)
        XCTAssertEqual(store.note(with: note.id)?.isTrashed, true)
        XCTAssertTrue(store.trashNotes.contains { $0.id == note.id })
        XCTAssertFalse(store.activeNotes.contains { $0.id == note.id })
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
        XCTAssertNil(store.note(with: note.id))
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
        XCTAssertEqual(store.count(inNotebook: Notebooks.all), store.activeNotes.count)
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
