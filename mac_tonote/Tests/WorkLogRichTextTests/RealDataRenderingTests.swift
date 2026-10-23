import XCTest
import AppKit
@testable import WorkLogCore
@testable import WorkLogRichText

/// 用真实磁盘数据验证：修复后的正文能否正确渲染出真实图片附件。
@MainActor
final class RealDataRenderingTests: XCTestCase {

    /// 读取真实 notes.json（仅在环境变量指定时运行，避免污染真实数据）
    func testRealNotesRenderImagesFromAttachments() throws {
        let realDir = ProcessInfo.processInfo.environment["WORKLOG_DATA_DIR"]
        guard let realDir else {
            throw XCTSkip("未设置 WORKLOG_DATA_DIR，跳过真实数据验证")
        }
        let dir = URL(fileURLWithPath: realDir)
        guard FileManager.default.fileExists(atPath: dir.appendingPathComponent("notes.json").path) else {
            throw XCTSkip("真实数据目录不存在")
        }

        let store = NoteStore(directory: dir)
        var checkedImages = 0

        for note in store.notes where note.contentFormat == .richText {
            let document = RichTextHTMLCodec.decode(note.body)
            let attributed = RichTextBridge.attributedString(from: document, attachments: store.attachments)

            // 正文里的每个图片 run 都必须能还原成真实附件，而不是占位图
            for run in document.blocks.flatMap({ $0.runs }) {
                guard let image = run.image else { continue }
                checkedImages += 1

                XCTAssertTrue(image.path.hasPrefix("attachments/"),
                              "图片路径必须是相对路径，实际：\(image.path)")
                XCTAssertFalse(image.path.contains("file://"),
                               "图片路径不得包含 file:// 引用")

                let absolute = store.attachments.absoluteURL(for: image.path)
                XCTAssertTrue(FileManager.default.fileExists(atPath: absolute.path),
                              "附件文件必须存在：\(image.path)")

                // 渲染时必须能加载出真实图片
                let loaded = NSImage(contentsOf: absolute)
                XCTAssertNotNil(loaded, "附件必须能加载为图片：\(image.path)")

                // 正文记录的显示尺寸必须与附件文件真实尺寸一致
                let natural = loaded?.size ?? .zero
                if let width = image.width {
                    XCTAssertEqual(width, natural.width, accuracy: 1,
                                   "正文记录宽度应等于文件真实宽度：\(image.path)")
                }
                if let height = image.height {
                    XCTAssertEqual(height, natural.height, accuracy: 1,
                                   "正文记录高度应等于文件真实高度：\(image.path)")
                }

                // attributed string 里必须能找到对应的真实 NSTextAttachment，
                // 且它的显示尺寸等于文件真实尺寸（不能退化成 160×90 占位图）
                var matched: NSTextAttachment?
                attributed.enumerateAttribute(.attachment, in: NSRange(location: 0, length: attributed.length)) { value, range, _ in
                    guard let attachment = value as? NSTextAttachment else { return }
                    let path = attributed.attribute(RichTextKeys.imagePath, at: range.location, effectiveRange: nil) as? String
                    if path == image.path {
                        matched = attachment
                    }
                }
                XCTAssertNotNil(matched, "渲染结果里应存在对应附件：\(image.path)")
                XCTAssertNotNil(matched?.image)
                XCTAssertEqual(matched?.bounds.width ?? 0, natural.width, accuracy: 1,
                               "显示宽度应等于文件真实宽度：\(image.path)")
                XCTAssertEqual(matched?.bounds.height ?? 0, natural.height, accuracy: 1,
                               "显示高度应等于文件真实高度：\(image.path)")
            }
        }

        XCTAssertGreaterThan(checkedImages, 0, "真实数据里应当至少有一张图片")
        print("已验证图片数量：\(checkedImages)")
    }
}
