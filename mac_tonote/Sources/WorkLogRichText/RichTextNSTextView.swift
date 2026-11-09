import AppKit
import UniformTypeIdentifiers
import WorkLogCore

/// 支持「粘贴即成图」的富文本视图。
///
/// 覆盖三类来源：
/// 1. 剪贴板里是图片文件 URL（访达复制图片文件、微信「复制文件」）
/// 2. 剪贴板里是**原始位图数据**（系统截图 Ctrl+Cmd+Shift+4、微信/QQ 截图、
///    浏览器复制图片）——按剪贴板实际声明的类型逐个尝试，直接落盘
/// 3. 剪贴板里是图片路径文本（微信「复制路径」、手动复制的路径）
///
/// 三种情况都会把图片写入临时目录，再由上层导入附件仓库并插入正文。
/// 全链路带 `[TNPaste]` 日志，便于排查「粘贴没反应」时确认到底断在哪一步。
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

    private static let logTag = "[TNPaste]"

    private static func log(_ message: String) {
        NSLog("%@", "\(logTag) \(message)")
    }

    override func keyDown(with event: NSEvent) {
        // keyCode 48 = Tab。直接在最底层拦截，避免窗口的 key-view loop
        // 把 Tab 当成「切换焦点」而 NSTextView 根本收不到 insertTab:。
        if event.keyCode == 48, let handler = onTabInList {
            let delta = event.modifierFlags.contains(.shift) ? -1 : 1
            if handler(delta) { return }
        }

        // Cmd+V 直接走我们的粘贴逻辑，不依赖编辑菜单 paste: action 的转发。
        // NSViewRepresentable 嵌进 SwiftUI 后菜单 action 链并不总是可靠，
        // 而只要正文视图是第一响应者，keyDown 一定会到这里。
        let flags = event.modifierFlags.intersection(.deviceIndependentFlagsMask)
        if flags.contains(.command),
           !flags.contains(.control),
           !flags.contains(.option),
           event.charactersIgnoringModifiers?.lowercased() == "v" {
            Self.log("keyDown 直接拦截 Cmd+V")
            paste(nil)
            return
        }

        super.keyDown(with: event)
    }

    /// 编辑菜单里的「粘贴」和「粘贴并匹配样式」走的是两个不同入口，统一拦截。
    override func paste(_ sender: Any?) {
        handlePaste(fallback: { super.paste(sender) })
    }

    override func pasteAsPlainText(_ sender: Any?) {
        handlePaste(fallback: { super.pasteAsPlainText(sender) })
    }

    private func handlePaste(fallback: () -> Void) {
        let pasteboard = NSPasteboard.general
        let names = (pasteboard.types ?? []).map(\.rawValue)
        Self.log("paste 触发，剪贴板类型 = [\(names.joined(separator: ", "))]")

        if let imageURL = extractImageURLFromPasteboard() {
            Self.log("命中图片文件：\(imageURL.path)")
            // 回调丢失时也不能静默吞掉，否则又变成「粘贴没反应」
            guard let onPasteImage else {
                Self.log("onPasteImage 未绑定")
                onPasteFailure?("粘贴回调未就绪，请切换一次笔记后重试。")
                return
            }
            onPasteImage(imageURL)
            return
        }

        if let text = pasteboard.string(forType: .string),
           ImagePathScanner.containsImagePath(in: text) {
            Self.log("命中图片路径文本")
            onPasteText?(text)
            return
        }

        // 剪贴板里确实有图片但前面全失败了：明确报错，不要静默回落到
        // NSTextView 默认粘贴（那会插入 file:///Attachment.tiff 临时引用，
        // 序列化后图片会丢）。
        if hasImageOnPasteboard {
            let message = "未能读取剪贴板中的图片。剪贴板类型：\(names.joined(separator: "、"))。"
                + "可改用工具栏「插入图片」选择截图文件。"
            Self.log("读取失败：\(message)")
            onPasteFailure?(message)
            return
        }

        Self.log("剪贴板没有图片，回落系统默认粘贴")
        fallback()
    }

    /// 宽松识别：既认标准 UTType，也认各家用自定义名字塞进来的图片类型。
    private static func isImageType(_ type: NSPasteboard.PasteboardType) -> Bool {
        if let ut = UTType(type.rawValue), ut.conforms(to: .image) { return true }
        let raw = type.rawValue.lowercased()
        let keywords = ["png", "tiff", "jpeg", "jpg", "heic", "heif", "gif", "webp", "bmp", "image"]
        return keywords.contains { raw.contains($0) }
    }

    private var hasImageOnPasteboard: Bool {
        (NSPasteboard.general.types ?? []).contains { Self.isImageType($0) }
    }

    private static func fileExtension(for type: NSPasteboard.PasteboardType) -> String {
        let raw = type.rawValue.lowercased()
        if raw.contains("jpeg") || raw.contains("jpg") { return "jpg" }
        if raw.contains("png") { return "png" }
        if raw.contains("tiff") { return "tiff" }
        if raw.contains("heic") { return "heic" }
        if raw.contains("heif") { return "heif" }
        if raw.contains("gif") { return "gif" }
        if raw.contains("webp") { return "webp" }
        if raw.contains("bmp") { return "bmp" }
        return "png"
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
            Self.log("分支 1：文件 URL \(imageURL.path)")
            return imageURL
        }

        // 1b) 老式 NSFilenamesPboardType：部分截图工具只写这个，不写 public.file-url
        let filenameType = NSPasteboard.PasteboardType("NSFilenamesPboardType")
        if let paths = pasteboard.propertyList(forType: filenameType) as? [String],
           let path = paths.first(where: {
               AttachmentStore.allowedExtensions.contains(($0 as NSString).pathExtension.lowercased())
           }) {
            Self.log("分支 1b：NSFilenamesPboardType \(path)")
            return URL(fileURLWithPath: path)
        }

        // 2) 按剪贴板实际声明的图片类型逐个取原始数据直接落盘。
        //    旧实现只查固定类型表，遇到工具自定义类型就整条链路静默失效。
        for type in pasteboard.types ?? [] where Self.isImageType(type) {
            guard let data = pasteboard.data(forType: type), !data.isEmpty else { continue }
            let ext = Self.fileExtension(for: type)
            if let url = writeTemporaryImage(data, ext: ext) {
                Self.log("分支 2：\(type.rawValue) → \(url.lastPathComponent)")
                return url
            }
        }

        // 3) 兜底：类型完全陌生、但 pasteboard 能构造 NSImage
        if let image = NSImage(pasteboard: pasteboard), let png = pngData(from: image),
           let url = writeTemporaryImage(png, ext: "png") {
            Self.log("分支 3：NSImage 兜底 → \(url.lastPathComponent)")
            return url
        }

        return nil
    }

    /// 把剪贴板数据写成临时图片文件，并**验证真的能读出图片**。
    /// 有些工具把 PNG 数据声明成 TIFF，按声明扩展名落盘会读不出来，
    /// 这时统一转成 PNG 再试一次。
    private func writeTemporaryImage(_ data: Data, ext: String) -> URL? {
        let fm = FileManager.default
        let directory = fm.temporaryDirectory
            .appendingPathComponent("tonotePaste", isDirectory: true)
        do {
            try fm.createDirectory(at: directory, withIntermediateDirectories: true)
            let url = directory.appendingPathComponent("paste-\(UUID().uuidString).\(ext)")
            try data.write(to: url, options: .atomic)
            if NSImage(contentsOf: url) != nil { return url }

            try? fm.removeItem(at: url)
            if let normalized = normalizedPNG(data) {
                let pngURL = directory.appendingPathComponent("paste-\(UUID().uuidString).png")
                try normalized.write(to: pngURL, options: .atomic)
                if NSImage(contentsOf: pngURL) != nil { return pngURL }
                try? fm.removeItem(at: pngURL)
            }
        } catch {
            Self.log("临时图片落盘失败：\(error.localizedDescription)")
        }
        return nil
    }

    private func normalizedPNG(_ data: Data) -> Data? {
        if let bitmap = NSBitmapImageRep(data: data) {
            return bitmap.representation(using: .png, properties: [:])
        }
        if let image = NSImage(data: data) {
            return pngData(from: image)
        }
        return nil
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
