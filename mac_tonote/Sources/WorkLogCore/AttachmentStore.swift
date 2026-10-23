import Foundation

/// 图片附件仓库。
///
/// 目录结构（固定，可直接映射到百度网盘 `/apps/WorkLog`）：
/// ```
/// <root>/attachments/<note-uuid>/<image-uuid>.<ext>
/// ```
/// 正文里只保存相对路径 `attachments/<note-uuid>/<image-uuid>.<ext>`，
/// 因此 macOS / Android 同步后仍能正确指向同一份附件。
public final class AttachmentStore {

    public let rootDirectory: URL
    public let attachmentsDirectory: URL

    public init(rootDirectory: URL) {
        self.rootDirectory = rootDirectory
        self.attachmentsDirectory = rootDirectory.appendingPathComponent("attachments", isDirectory: true)
        try? FileManager.default.createDirectory(at: attachmentsDirectory, withIntermediateDirectories: true)
    }

    /// 某篇笔记的附件目录
    public func directory(for noteID: UUID) -> URL {
        attachmentsDirectory.appendingPathComponent(noteID.uuidString, isDirectory: true)
    }

    /// 允许的图片扩展名
    public static let allowedExtensions: Set<String> = ["png", "jpg", "jpeg", "gif", "webp", "heic", "heif", "tiff", "tif", "bmp"]

    /// 导入一张图片：复制到附件目录并返回相对路径。
    /// - Throws: 源文件不存在、类型不支持或写入失败。
    @discardableResult
    public func importImage(at sourceURL: URL, noteID: UUID) throws -> String {
        let fm = FileManager.default
        guard fm.fileExists(atPath: sourceURL.path) else {
            throw AttachmentError.sourceNotFound(sourceURL.path)
        }

        let ext = sourceURL.pathExtension.lowercased()
        guard Self.allowedExtensions.contains(ext) else {
            throw AttachmentError.unsupportedType(ext.isEmpty ? "未知" : ext)
        }

        let targetDirectory = directory(for: noteID)
        try fm.createDirectory(at: targetDirectory, withIntermediateDirectories: true)

        let fileName = "\(UUID().uuidString).\(ext)"
        let targetURL = targetDirectory.appendingPathComponent(fileName)

        do {
            try fm.copyItem(at: sourceURL, to: targetURL)
        } catch {
            throw AttachmentError.copyFailed(error.localizedDescription)
        }

        return relativePath(for: targetURL)
    }

    /// 由绝对路径推出相对路径
    public func relativePath(for absoluteURL: URL) -> String {
        let base = rootDirectory.standardizedFileURL.path
        let target = absoluteURL.standardizedFileURL.path
        if target.hasPrefix(base) {
            let relative = String(target.dropFirst(base.count))
            return relative.hasPrefix("/") ? String(relative.dropFirst()) : relative
        }
        return absoluteURL.lastPathComponent
    }

    /// 由相对路径还原绝对路径
    public func absoluteURL(for relativePath: String) -> URL {
        let trimmed = relativePath.hasPrefix("/") ? String(relativePath.dropFirst()) : relativePath
        return rootDirectory.appendingPathComponent(trimmed)
    }

    /// 文件是否存在
    public func fileExists(relativePath: String) -> Bool {
        FileManager.default.fileExists(atPath: absoluteURL(for: relativePath).path)
    }

    /// 删除一篇笔记的全部附件（彻底删除笔记时调用）
    public func removeAttachments(for noteID: UUID) {
        let target = directory(for: noteID)
        if FileManager.default.fileExists(atPath: target.path) {
            try? FileManager.default.removeItem(at: target)
        }
    }

    /// 统计一篇笔记的附件数量与占用空间
    public func usage(for noteID: UUID) -> (count: Int, bytes: Int) {
        let target = directory(for: noteID)
        guard let contents = try? FileManager.default.contentsOfDirectory(
            at: target,
            includingPropertiesForKeys: [.fileSizeKey],
            options: [.skipsHiddenFiles]
        ) else { return (0, 0) }

        var bytes = 0
        for url in contents {
            let size = (try? url.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0
            bytes += size
        }
        return (contents.count, bytes)
    }
}

public enum AttachmentError: LocalizedError {
    case sourceNotFound(String)
    case unsupportedType(String)
    case copyFailed(String)

    public var errorDescription: String? {
        switch self {
        case .sourceNotFound(let path):
            return "找不到图片文件：\(path)"
        case .unsupportedType(let ext):
            return "不支持的图片格式：\(ext)（支持 png / jpg / gif / webp / heic / tiff / bmp）"
        case .copyFailed(let reason):
            return "图片写入失败：\(reason)"
        }
    }
}
