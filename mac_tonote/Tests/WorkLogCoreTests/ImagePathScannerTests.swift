import XCTest
@testable import WorkLogCore

/// 图片路径识别器测试（纯 Foundation，macOS / Android 共用同一套规则）
final class ImagePathScannerTests: XCTestCase {

    private var tempDir: URL!

    override func setUp() async throws {
        tempDir = FileManager.default.temporaryDirectory
            .appendingPathComponent("WorkLogPathTests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: tempDir, withIntermediateDirectories: true)
    }

    override func tearDown() async throws {
        try? FileManager.default.removeItem(at: tempDir)
    }

    private func makeFile(named name: String) throws -> URL {
        let url = tempDir.appendingPathComponent(name)
        try Data([0x89, 0x50, 0x4E, 0x47]).write(to: url)
        return url
    }

    // MARK: - 基本识别

    func testRecognizesExistingImagePath() throws {
        let file = try makeFile(named: "shot.png")
        let segments = ImagePathScanner.segments(in: file.path)
        XCTAssertEqual(segments.count, 1)
        XCTAssertTrue(segments[0].isImage)
        XCTAssertEqual(segments[0].imageURL?.path, file.path)
    }

    func testRecognizesPathAmongText() throws {
        let file = try makeFile(named: "shot.jpg")
        let text = "看这个 \(file.path) 很清楚"
        let segments = ImagePathScanner.segments(in: text)

        XCTAssertEqual(segments.count, 3)
        XCTAssertEqual(segments[0].text, "看这个 ")
        XCTAssertTrue(segments[1].isImage)
        XCTAssertEqual(segments[2].text, " 很清楚")
    }

    func testRecognizesMultiplePaths() throws {
        let a = try makeFile(named: "a.png")
        let b = try makeFile(named: "b.png")
        let segments = ImagePathScanner.segments(in: "\(a.path) 和 \(b.path)")

        XCTAssertEqual(segments.filter { $0.isImage }.count, 2)
    }

    // MARK: - 拒绝误判

    func testNonExistingPathIsNotImage() {
        let segments = ImagePathScanner.segments(in: "/tmp/definitely-not-here-12345.png")
        XCTAssertFalse(segments.contains { $0.isImage })
    }

    func testNonImageExtensionIsNotImage() throws {
        let file = try makeFile(named: "doc.txt")
        let segments = ImagePathScanner.segments(in: file.path)
        XCTAssertFalse(segments.contains { $0.isImage })
    }

    func testPlainTextHasNoImage() {
        XCTAssertFalse(ImagePathScanner.containsImagePath(in: "这是一段普通文字"))
        XCTAssertFalse(ImagePathScanner.containsImagePath(in: "https://example.com/a.png"))
    }

    func testDirectoryIsNotImage() throws {
        let dir = tempDir.appendingPathComponent("folder.png")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        XCTAssertFalse(ImagePathScanner.containsImagePath(in: dir.path))
    }

    /// 文件名带数字后缀时不能误判（a.png2 不是图片）
    func testExtensionFollowedByAlphanumericIsNotImage() throws {
        let file = try makeFile(named: "shot.png")
        let segments = ImagePathScanner.segments(in: "\(file.path)2")
        XCTAssertFalse(segments.contains { $0.isImage })
    }

    /// 带空格的路径也要能识别
    func testPathWithSpacesIsRecognized() throws {
        let sub = tempDir.appendingPathComponent("我的 图片")
        try FileManager.default.createDirectory(at: sub, withIntermediateDirectories: true)
        let file = sub.appendingPathComponent("a.png")
        try Data([0x89, 0x50, 0x4E, 0x47]).write(to: file)

        let segments = ImagePathScanner.segments(in: file.path)
        XCTAssertTrue(segments.contains { $0.isImage }, "带空格的路径应被识别")
    }

    func testEmptyTextProducesNoSegments() {
        XCTAssertTrue(ImagePathScanner.segments(in: "").isEmpty)
    }
}
