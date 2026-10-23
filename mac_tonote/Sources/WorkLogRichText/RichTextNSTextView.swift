import AppKit
import WorkLogCore

/// 支持「粘贴即成图」的富文本视图。
///
/// 覆盖三种来源：
/// 1. 剪贴板里是图片文件（从访达/微信复制图片文件）
/// 2. 剪贴板里是位图数据（从微信/浏览器复制的图片）
/// 3. 剪贴板里是**图片路径文本**（微信「复制路径」、手动复制的路径）
///
/// 三种情况都会把图片写入 WorkLog 附件目录，再以内联图片插入正文。
final class RichTextNSTextView: NSTextView {

    /// 回调：把一张图片（本地文件）导入附件仓库并插入正文
    var onPasteImage: ((URL) -> Void)?
    /// 回调：粘贴的文本里含有本地图片路径，由上层按段落替换成内联图片
    var onPasteText: ((String) -> Void)?

    override func paste(_ sender: Any?) {
        // 1) 图片文件 / 2) 位图数据
        if let imageURL = extractImageURLFromPasteboard() {
            onPasteImage?(imageURL)
            return
        }

        // 3) 剪贴板里是图片路径文本（微信「复制路径」等）
        if let text = NSPasteboard.general.string(forType: .string),
           ImagePathScanner.containsImagePath(in: text) {
            onPasteText?(text)
            return
        }

        superPaste()
    }

    /// 系统默认粘贴（无法识别为图片时回落）
    func superPaste() {
        super.paste(nil)
    }

    /// 从剪贴板中提取图片文件 URL
    private func extractImageURLFromPasteboard() -> URL? {
        let pasteboard = NSPasteboard.general

        // 1) 文件 URL（从访达/微信复制图片文件）
        if let urls = pasteboard.readObjects(forClasses: [NSURL.self],
                                            options: [.urlReadingFileURLsOnly: true]) as? [URL] {
            if let imageURL = urls.first(where: { AttachmentStore.allowedExtensions.contains($0.pathExtension.lowercased()) }) {
                return imageURL
            }
        }

        // 2) 剪贴板里的位图数据（从微信/浏览器复制的图片）
        if let image = NSImage(pasteboard: pasteboard) {
            return writeTemporaryPNG(image)
        }

        // 3) 剪贴板里只有路径文本 → 交给 onPasteText 处理（保留周围文字）
        return nil
    }

    /// 把剪贴板位图写成临时 PNG，供附件仓库导入
    private func writeTemporaryPNG(_ image: NSImage) -> URL? {
        guard let tiff = image.tiffRepresentation,
              let bitmap = NSBitmapImageRep(data: tiff),
              let png = bitmap.representation(using: .png, properties: [:]) else {
            return nil
        }
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("WorkLogPaste", isDirectory: true)
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let url = directory.appendingPathComponent("paste-\(UUID().uuidString).png")
        do {
            try png.write(to: url)
            return url
        } catch {
            return nil
        }
    }
}
