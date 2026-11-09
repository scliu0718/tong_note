import AppKit
import WorkLogCore

/// 支持「粘贴即成图」的富文本视图。
///
/// 覆盖三种来源：
/// 1. 剪贴板里是图片文件 URL（从访达复制图片文件、微信「复制文件」）
/// 2. 剪贴板里是**原始位图数据**（系统截图 Ctrl+Cmd+Shift+4、微信/QQ 截图、
///    浏览器复制图片）——优先按原始数据类型直接落盘，不做任何图像转换
/// 3. 剪贴板里是图片路径文本（微信「复制路径」、手动复制的路径）
///
/// 三种情况都会把图片写入 WorkLog 附件目录，再以内联图片插入正文。
final class RichTextNSTextView: NSTextView {

    /// 回调：把一张图片（本地文件）导入附件仓库并插入正文
    var onPasteImage: ((URL) -> Void)?
    /// 回调：粘贴的文本里含有本地图片路径，由上层按段落替换成内联图片
    var onPasteText: ((String) -> Void)?
    /// 回调：剪贴板里明明有图片却读不出来时，把原因显示给用户
    var onPasteFailure: ((String) -> Void)?
    /// 回调：列表段落里按 Tab / Shift+Tab 切换层级。
    /// 参数为 +1 / -1；返回 true 表示该按键已被消费，不再传给系统（避免焦点跳走）。
    var onTabInList: ((Int) -> Bool)?

    /// 原始位图类型 → 落盘扩展名，按优先级排列。
    /// 扩展名必须在 `AttachmentStore.allowedExtensions` 里。
    private static let imageTypes: [(type: NSPasteboard.PasteboardType, ext: String)] = [
        (.png, "png"),
        (.tiff, "tiff"),
        (NSPasteboard.PasteboardType("public.jpeg"), "jpg"),
        (NSPasteboard.PasteboardType("public.heic"), "heic"),
        (NSPasteboard.PasteboardType("public.heif"), "heif"),
        (NSPasteboard.PasteboardType("com.compuserve.gif"), "gif"),
        (NSPasteboard.PasteboardType("org.webmproject.webp"), "webp"),
        (NSPasteboard.PasteboardType("com.microsoft.bmp"), "bmp")
    ]

    override func keyDown(with event: NSEvent) {
        // keyCode 48 = Tab。直接在最底层拦截，避免窗口的 key-view loop
        // 把 Tab 当成「切换焦点」而 NSTextView 根本收不到 insertTab:。
        if event.keyCode == 48, let handler = onTabInList {
            let delta = event.modifierFlags.contains(.shift) ? -1 : 1
            if handler(delta) { return }
        }
        super.keyDown(with: event)
    }

    override func paste(_ sender: Any?) {
        // 1) 图片文件 / 2) 原始位图数据
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

        // 剪贴板里确实有图片但前面全失败了：明确报错，不要静默回落到
        // NSTextView 默认粘贴（那会插入 file:///Attachment.tiff 临时引用，
        // 序列化后图片会丢）。
        if hasImageOnPasteboard {
            onPasteFailure?("未能读取剪贴板中的图片，请改用工具栏「插入图片」选择截图文件")
            return
        }

        super.paste(sender)
    }

    private var hasImageOnPasteboard: Bool {
        let types = Set(NSPasteboard.general.types ?? [])
        return Self.imageTypes.contains { types.contains($0.type) }
    }

    /// 从剪贴板拿到一个可导入的图片文件 URL
    private func extractImageURLFromPasteboard() -> URL? {
        let pasteboard = NSPasteboard.general

        // 1) 文件 URL（从访达 / 微信复制图片文件）
        if let urls = pasteboard.readObjects(forClasses: [NSURL.self],
                                            options: [.urlReadingFileURLsOnly: true]) as? [URL],
           let imageURL = urls.first(where: {
               AttachmentStore.allowedExtensions.contains($0.pathExtension.lowercased())
           }) {
            return imageURL
        }

        // 2) 原始位图数据：直接写盘，绕开 NSImage 转换。
        //    旧实现依赖 `NSImage(pasteboard:)` + `tiffRepresentation`，
        //    部分截图工具给出的 image 拿不到 TIFF，就会静默失败。
        for entry in Self.imageTypes {
            guard let data = pasteboard.data(forType: entry.type), !data.isEmpty else { continue }
            if let url = writeTemporaryImage(data, ext: entry.ext) { return url }
        }

        // 3) 兜底：类型不在上表、但 pasteboard 能构造 NSImage
        if let image = NSImage(pasteboard: pasteboard),
           let png = pngData(from: image) {
            return writeTemporaryImage(png, ext: "png")
        }

        return nil
    }

    /// 把剪贴板数据写成临时图片文件，供附件仓库导入
    private func writeTemporaryImage(_ data: Data, ext: String) -> URL? {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("tonotePaste", isDirectory: true)
        do {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            let url = directory.appendingPathComponent("paste-\(UUID().uuidString).\(ext)")
            try data.write(to: url, options: .atomic)
            return url
        } catch {
            return nil
        }
    }

    /// NSImage → PNG，多种 representation 都试一遍
    private func pngData(from image: NSImage) -> Data? {
        if let tiff = image.tiffRepresentation,
           let bitmap = NSBitmapImageRep(data: tiff),
           let png = bitmap.representation(using: .png, properties: [:]) {
            return png
        }
        if let bitmap = image.representations.compactMap({ $0 as? NSBitmapImageRep }).first {
            return bitmap.representation(using: .png, properties: [:])
        }
        return nil
    }
}
