import AppKit
import UniformTypeIdentifiers
import WorkLogCore

/// 支持「粘贴即成图」的富文本视图。
///
/// 覆盖四类来源：
/// 1. 剪贴板里是图片文件 URL（访达复制图片文件、微信「复制文件」）
/// 2. 剪贴板里是**原始位图数据**（系统截图 Ctrl+Cmd+Shift+4、微信/QQ 截图、
///    浏览器复制图片）——按剪贴板实际声明的类型逐个尝试，直接落盘
/// 3. 剪贴板里是图片路径文本（微信「复制路径」、手动复制的路径）
/// 4. 剪贴板里是 **RTFD 富文本包**——从 App 笔记里右键「拷贝」图片时，
///    AppKit 只写 `com.apple.flat-rtfd` / `NeXT RTFD pasteboard type`，
///    完全没有 public.png / public.tiff，需要把包内附件还原成图片文件。
///
/// 四种情况都会把图片写入临时目录，再由上层导入附件仓库并插入正文。
/// 全链路带 `[TNPaste]` 日志，便于排查「粘贴没反应」时确认到底断在哪一步。
final class RichTextNSTextView: NSTextView {

    /// 用经典 NSLayoutManager 建视图。NSTextTable 只在 TextKit 1 里横排并画边框。
    static func makeTextKit1() -> RichTextNSTextView {
        let container = NSTextContainer()
        container.widthTracksTextView = true
        container.heightTracksTextView = false
        container.containerSize = NSSize(width: 0, height: CGFloat.greatestFiniteMagnitude)
        let layoutManager = NSLayoutManager()
        layoutManager.addTextContainer(container)
        let storage = NSTextStorage()
        storage.addLayoutManager(layoutManager)

        // frame 不能用 .zero：手动 init(frame:textContainer:) 不会像默认 init 那样
        // 配置好 min/maxSize，NSTextView 就没法在内容变长时自动撑高，滚动范围失效。
        let tv = RichTextNSTextView(frame: NSRect(x: 0, y: 0, width: 640, height: 0),
                                    textContainer: container)
        tv.minSize = NSSize(width: 0, height: 0)
        tv.maxSize = NSSize(width: CGFloat.greatestFiniteMagnitude,
                            height: CGFloat.greatestFiniteMagnitude)
        tv.isVerticallyResizable = true
        tv.isHorizontallyResizable = false
        tv.autoresizingMask = [.width]
        return tv
    }

    /// 回调：把一张图片（本地文件）导入附件仓库并插入正文
    var onPasteImage: ((URL) -> Void)?
    /// 回调：粘贴的文本里含有本地图片路径，由上层按段落替换成内联图片
    var onPasteText: ((String) -> Void)?
    /// 回调：剪贴板里明明有图片却读不出来时，把原因显示给用户
    var onPasteFailure: ((String) -> Void)?
    /// 回调：列表段落里按 Tab / Shift+Tab 切换层级。
    /// 参数为 +1 / -1；返回 true 表示该按键已被消费，不再传给系统（避免焦点跳走）。
    var onTabInList: ((Int) -> Bool)?
    /// 回调：表格边沿「+」插入行 / 列。参数为插入位置与该表内的字符位置。
    var onTableGutterInsertRow: ((Int, Int) -> Void)?
    var onTableGutterInsertColumn: ((Int, Int) -> Void)?
    /// 单元格下拉菜单直接调用命令层（同模块），不必把事件回传到 SwiftUI。
    weak var tableController: RichTextEditorController?

    /// 只画灰圆 / 蓝圆 / 下箭头，不接收事件；事件仍由 NSTextView 自己处理。
    private var tableGutter: RichTextTableGutterView?

    private static let logTag = "[TNPaste]"

    override init(frame frameRect: NSRect, textContainer container: NSTextContainer?) {
        super.init(frame: frameRect, textContainer: container)
        installTableGutter()
    }

    /// 覆盖了指定初始化方法后，原来的 `init(frame:)` 不会自动保留。
    convenience override init(frame frameRect: NSRect) {
        self.init(frame: frameRect, textContainer: nil)
    }

    required init?(coder: NSCoder) {
        super.init(coder: coder)
        installTableGutter()
    }

    deinit {
        NotificationCenter.default.removeObserver(self)
    }

    private func installTableGutter() {
        let gutter = RichTextTableGutterView(frame: bounds)
        gutter.autoresizingMask = [.width, .height]
        addSubview(gutter)
        tableGutter = gutter
        // 整块可见区域跟踪鼠标；hover 状态决定哪个灰圆变成蓝色「+」。
        addTrackingArea(NSTrackingArea(rect: .zero,
                                       options: [.mouseMoved, .mouseEnteredAndExited,
                                                 .cursorUpdate, .activeInKeyWindow, .inVisibleRect],
                                       owner: self,
                                       userInfo: nil))
        // 光标移到别的格子时，下箭头要跟着换位置
        NotificationCenter.default.addObserver(
            self,
            selector: #selector(tableSelectionDidChange(_:)),
            name: NSTextView.didChangeSelectionNotification,
            object: self)
    }

    /// 正文变长时 NSTextView 会改 frame，遮罩层必须一起长高，否则下方的表格没有灰圆。
    override func setFrameSize(_ newSize: NSSize) {
        super.setFrameSize(newSize)
        tableGutter?.frame = NSRect(origin: .zero, size: newSize)
        refreshTableGutter()
    }

    override func didChangeText() {
        super.didChangeText()
        // 表格重建后旧几何失效，立刻重算
        refreshTableGutter()
    }

    /// 横向滚动时表格左边界会移出屏幕；加行圆吸附在视口左缘，
    /// 必须随滚动重算几何，否则圆会停在旧位置、点不到。
    override func reflectScrolledClipView(_ aClipView: NSClipView) {
        super.reflectScrolledClipView(aClipView)
        refreshTableGutter()
    }

    /// 重新计算所有灰圆与格子矩形（文本变化、视图变宽、切换笔记后调用）。
    func refreshTableGutter() {
        guard isEditable else {
            tableGutter?.layout = nil
            tableGutter?.activeDot = nil
            tableGutter?.activeCell = nil
            tableGutter?.menuHover = false
            return
        }
        let layout = RichTextTableGutterGeometry.layout(in: self)
        tableGutter?.layout = layout
        refreshActiveCell(using: layout)
    }

    private func refreshActiveCell(using layout: RichTextTableGutterLayout? = nil) {
        guard isEditable,
              let current = layout ?? tableGutter?.layout,
              let storage = textStorage,
              storage.length > 0 else {
            tableGutter?.activeCell = nil
            return
        }
        let index = min(selectedRange().location, storage.length - 1)
        tableGutter?.activeCell = RichTextTableGutterGeometry.cell(at: index, in: current)
    }

    @objc private func tableSelectionDidChange(_ notification: Notification) {
        refreshActiveCell()
    }

    /// 带 `.cursorUpdate` 的 tracking area 会接管光标，避免 NSTextView 默认把
    /// hover 到灰圆上的光标又刷回 I 形（选文字）。
    override func cursorUpdate(with event: NSEvent) {
        if isEditable, isPointerArea {
            NSCursor.pointingHand.set()
        } else {
            NSCursor.iBeam.set()
        }
    }

    private var isPointerArea: Bool {
        tableGutter?.activeDot != nil || tableGutter?.menuHover == true
    }

    override func mouseMoved(with event: NSEvent) {
        super.mouseMoved(with: event)
        guard isEditable, let layout = tableGutter?.layout else {
            tableGutter?.activeDot = nil
            tableGutter?.menuHover = false
            NSCursor.arrow.set()
            return
        }
        let point = convert(event.locationInWindow, from: nil)
        let hoveringMenu = tableGutter?.activeCell.map {
            $0.menuRect.insetBy(dx: -3, dy: -3).contains(point)
        } ?? false
        if tableGutter?.menuHover != hoveringMenu { tableGutter?.menuHover = hoveringMenu }
        let dot = hoveringMenu ? nil : RichTextTableGutterGeometry.dot(at: point, in: layout)
        if tableGutter?.activeDot != dot { tableGutter?.activeDot = dot }
        // 灰圆 / 蓝圆 / 下箭头都是可点击控件，光标必须是手型而不是选文字的 I 形。
        if dot != nil || hoveringMenu {
            NSCursor.pointingHand.set()
        } else {
            NSCursor.iBeam.set()
        }
    }

    override func mouseExited(with event: NSEvent) {
        super.mouseExited(with: event)
        tableGutter?.activeDot = nil
        tableGutter?.menuHover = false
        NSCursor.arrow.set()
    }

    override func mouseDown(with event: NSEvent) {
        let point = convert(event.locationInWindow, from: nil)
        guard isEditable, let layout = tableGutter?.layout else {
            super.mouseDown(with: event)
            return
        }
        RichTextTableDebug.log("mouseDown (\(Int(point.x)),\(Int(point.y))) dots=\(layout.dots.count)")
        let nearest = RichTextTableGutterGeometry.dot(at: point, in: layout)

        // 1. 单元格右上角的下箭头 → 单元格菜单（别抢走边沿灰圆的点击）
        if nearest == nil,
           let cell = tableGutter?.activeCell,
           cell.menuRect.insetBy(dx: -3, dy: -3).contains(point),
           let controller = tableController,
           let gutter = tableGutter {
            let anchor = NSPoint(x: cell.menuRect.minX, y: NSMaxY(cell.menuRect) + 2)
            RichTextTableCellMenu.show(for: cell,
                                       at: anchor,
                                       in: gutter,
                                       textView: self,
                                       controller: controller)
            return
        }

        // 2. 小灰圆 / 蓝色加号 → 插入行或列。
        //    只认「+」图标本身：几何命中必须在蓝色圆（半径 10）范围内，
        //    不扩大成左侧/角落软命中区。hover 兜底只给极小余量（13pt）应对亚像素抖动。
        var resolved = nearest
        if resolved == nil, let current = tableGutter?.activeDot {
            let dx = abs(current.point.x - point.x)
            let dy = abs(current.point.y - point.y)
            if dx <= 13 && dy <= 13 { resolved = current }
        }
        guard let dot = resolved else {
            var nearestDistance = CGFloat.greatestFiniteMagnitude
            var nearestInfo = "none"
            for candidate in layout.dots {
                let dx = candidate.point.x - point.x
                let dy = candidate.point.y - point.y
                let delta = (dx * dx + dy * dy).squareRoot()
                if delta < nearestDistance {
                    nearestDistance = delta
                    nearestInfo = "\(candidate.kind == .column ? "col" : "row")@(\(Int(candidate.point.x)),\(Int(candidate.point.y)))"
                }
            }
            let vis = visibleRect
            RichTextTableDebug.log("miss：没命中，最近=\(nearestInfo) 距离=\(Int(nearestDistance))pt 视口左缘=\(Int(vis.minX)) 点击=(\(Int(point.x)),\(Int(point.y)))")
            super.mouseDown(with: event)
            return
        }

        NSCursor.pointingHand.set()
        let kind = dot.kind == .column ? "column" : "row"
        let rowCallback = onTableGutterInsertRow != nil
        let columnCallback = onTableGutterInsertColumn != nil
        let controllerReady = tableController != nil
        let controllerMatches = tableController?.textView === self
        RichTextTableDebug.log("hit \(kind) index=\(dot.index) probe=\(dot.probe) rowCB=\(rowCallback) colCB=\(columnCallback) controller=\(controllerReady) tvMatches=\(controllerMatches)")
        // 直接在被点击的视图上执行插入：SwiftUI 复用编辑器时 controller.textView
        // 可能还指向旧视图，走回调会把行列插到看不见的地方。
        switch dot.kind {
        case .column:
            if let controller = tableController {
                controller.textView = self
                RichTextTableEditor.insertColumn(at: dot.index, probe: dot.probe,
                                                 in: self, attachments: controller.attachments)
                controller.refreshCurrentStyle()
            } else if columnCallback {
                onTableGutterInsertColumn?(dot.index, dot.probe)
            } else {
                RichTextTableDebug.log("加列不可用：controller 与回调都为空")
            }
        case .row:
            if let controller = tableController {
                controller.textView = self
                RichTextTableEditor.insertRow(at: dot.index, probe: dot.probe,
                                              in: self, attachments: controller.attachments)
                controller.refreshCurrentStyle()
            } else if rowCallback {
                onTableGutterInsertRow?(dot.index, dot.probe)
            } else {
                RichTextTableDebug.log("加行不可用：controller 与回调都为空")
            }
        }
        tableGutter?.activeDot = nil
        refreshTableGutter()
        let length = textStorage?.length ?? -1
        let dots = tableGutter?.layout?.dots.count ?? -1
        RichTextTableDebug.log("after insert storageLen=\(length) dots=\(dots)")
    }

    private static func log(_ message: String) {
        let line = "\(logTag) \(message)"
        NSLog("%@", line)
        // 系统 `log show` 在沙盒化终端里不可用（Cannot run while sandboxed），
        // 同步落一份到可读文件，方便直接读取粘贴链路的诊断记录。
        let fm = FileManager.default
        guard let base = fm.urls(for: .applicationSupportDirectory, in: .userDomainMask).first else { return }
        let directory = base.appendingPathComponent("tonote", isDirectory: true)
        try? fm.createDirectory(at: directory, withIntermediateDirectories: true)
        let url = directory.appendingPathComponent("paste-debug.log")
        let entry = "\(ISO8601DateFormatter().string(from: Date())) \(line)\n"
        guard let data = entry.data(using: .utf8) else { return }
        if let handle = try? FileHandle(forWritingTo: url) {
            handle.seekToEndOfFile()
            try? handle.write(contentsOf: data)
            handle.closeFile()
        } else {
            try? data.write(to: url, options: .atomic)
        }
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

    /// 从笔记里「拷贝」一张图片时，直接往剪贴板写标准 PNG。
    /// 否则 AppKit 只放 RTFD 包，粘贴端识别不到任何位图类型。
    override func copy(_ sender: Any?) {
        if let png = standaloneImagePNGInSelection() {
            let pasteboard = NSPasteboard.general
            pasteboard.clearContents()
            pasteboard.setData(png, forType: .png)
            Self.log("copy：选区是纯图片，写入 public.png \(png.count) bytes")
            return
        }
        super.copy(sender)
    }

    /// 选区里全是图片附件（U+FFFC 占位符）时返回其 PNG 数据；
    /// 混合文字、纯文字、无选中都返回 nil，交给系统默认拷贝保留原有格式。
    private func standaloneImagePNGInSelection() -> Data? {
        let range = selectedRange()
        guard range.length > 0,
              let storage = textStorage,
              NSMaxRange(range) <= storage.length else { return nil }
        let text = (storage.string as NSString).substring(with: range)
            .trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty,
              text.unicodeScalars.allSatisfy({ $0.value == 0xFFFC }) else { return nil }

        var result: Data?
        storage.enumerateAttribute(.attachment, in: range, options: []) { value, _, stop in
            guard let attachment = value as? NSTextAttachment else { return }
            if let contents = attachment.fileWrapper?.regularFileContents,
               let bitmap = NSBitmapImageRep(data: contents),
               let png = bitmap.representation(using: .png, properties: [:]) {
                result = png
            } else if let image = attachment.image {
                result = pngData(from: image)
            }
            if result != nil { stop.pointee = true }
        }
        return result
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
        if hasImageOnPasteboard || hasRTFDImageOnPasteboard {
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

    /// 剪贴板里是 RTFD 且包内确实含图片附件（用于失败时给出明确提示）
    private var hasRTFDImageOnPasteboard: Bool {
        rtfdImageData() != nil
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

        // 4) RTFD 富文本包：从笔记正文里右键「拷贝」图片时剪贴板的唯一内容。
        //    不还原就会回落到系统默认粘贴，只插入一个空的附件占位符。
        if let (imageData, name) = rtfdImageData() {
            let ext = (name as NSString).pathExtension.lowercased()
            if let url = writeTemporaryImage(imageData, ext: ext.isEmpty ? "png" : ext) {
                Self.log("分支 4：RTFD 还原图片（\(name)）→ \(url.lastPathComponent)")
                return url
            }
        }

        return nil
    }

    /// 从 RTFD 包里取出第一张图片附件的原始数据与文件名。
    /// `com.apple.flat-rtfd` 是 RTFD 的扁平序列化格式，
    /// 用 `NSAttributedString(.rtfd)` 解析后附件在 `fileWrapper.regularFileContents` 里。
    private func rtfdImageData() -> (Data, String)? {
        let pasteboard = NSPasteboard.general
        let candidates = [
            NSPasteboard.PasteboardType("com.apple.flat-rtfd"),
            NSPasteboard.PasteboardType("NeXT RTFD pasteboard type"),
            NSPasteboard.PasteboardType.rtfd,
            NSPasteboard.PasteboardType("public.rtf"),
            NSPasteboard.PasteboardType("NeXT Rich Text Format v1.0 pasteboard type")
        ]
        for type in candidates {
            guard let data = pasteboard.data(forType: type), !data.isEmpty else { continue }
            guard let attributed = try? NSAttributedString(
                data: data,
                options: [.documentType: NSAttributedString.DocumentType.rtfd],
                documentAttributes: nil) else { continue }
            var found: (Data, String)?
            attributed.enumerateAttribute(
                .attachment,
                in: NSRange(location: 0, length: attributed.length),
                options: []) { value, _, stop in
                guard let attachment = value as? NSTextAttachment,
                      let contents = attachment.fileWrapper?.regularFileContents else { return }
                found = (contents, attachment.fileWrapper?.preferredFilename ?? "Attachment.png")
                stop.pointee = true
            }
            if let found { return found }
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
