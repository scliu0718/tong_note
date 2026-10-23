import Foundation

/// 在文本中识别「本地图片路径」。
///
/// 纯 Foundation 实现，macOS 与 Android 客户端可共用同一套规则。
///
/// 解决的问题：用户从微信/备忘录里复制的是一段**路径文本**（而不是图片文件本身），
/// 直接粘贴只会得到一长串路径。这里把它识别出来，导入附件目录并以内联图片呈现。
public enum ImagePathScanner {

    /// 允许的图片扩展名（与 AttachmentStore 保持一致）
    public static let imageExtensions = AttachmentStore.allowedExtensions

    /// 文本中的一段：要么是普通文字，要么是一张本地图片
    public struct Segment: Equatable {
        public var text: String
        public var imageURL: URL?

        public var isImage: Bool { imageURL != nil }

        public static func text(_ value: String) -> Segment { Segment(text: value, imageURL: nil) }
        public static func image(_ value: String, url: URL) -> Segment { Segment(text: value, imageURL: url) }
    }

    /// 扫描文本，找出所有指向**真实存在的本地图片**的路径片段。
    ///
    /// 识别规则：从图片扩展名往前回溯到空白/引号边界，得到完整路径，
    /// 再验证文件确实存在。回溯覆盖了带空格的路径（如 `/Users/me/我的 图片/a.png`）。
    public static func segments(in text: String) -> [Segment] {
        let chars = Array(text)
        guard !chars.isEmpty else { return [] }

        var segments: [Segment] = []
        var pending = String()
        var index = 0

        while index < chars.count {
            if let match = matchImagePath(in: chars, at: index) {
                if !pending.isEmpty {
                    segments.append(.text(pending))
                    pending = ""
                }
                segments.append(.image(match.path, url: match.url))
                index = match.end
                continue
            }
            pending.append(chars[index])
            index += 1
        }

        if !pending.isEmpty {
            segments.append(.text(pending))
        }
        return segments
    }

    /// 文本中是否包含可用的本地图片路径
    public static func containsImagePath(in text: String) -> Bool {
        segments(in: text).contains { $0.isImage }
    }

    // MARK: - 私有

    private struct PathMatch {
        var path: String
        var url: URL
        var end: Int
    }

    /// 尝试把位置 `index` 解释为一张本地图片路径的起点
    private static func matchImagePath(in chars: [Character], at index: Int) -> PathMatch? {
        // 路径必须以 / 开头
        guard index < chars.count, chars[index] == "/" else { return nil }

        // 往前找到扩展名的结束位置
        var cursor = index
        var extEnd: Int?

        while cursor < chars.count {
            if chars[cursor] == "." {
                // 读扩展名
                var j = cursor + 1
                var ext = ""
                while j < chars.count, chars[j].isLetter || chars[j].isNumber {
                    ext.append(chars[j])
                    j += 1
                }
                let lowered = ext.lowercased()
                if imageExtensions.contains(lowered) {
                    // 扩展名后面不能紧跟字母数字（避免把 a.png2 当成图片）
                    if j < chars.count, chars[j].isLetter || chars[j].isNumber {
                        break
                    }
                    extEnd = j
                }
                break
            }
            if chars[cursor].isWhitespace || chars[cursor] == "\"" || chars[cursor] == "'" || chars[cursor] == "<" || chars[cursor] == ">" {
                break
            }
            cursor += 1
        }

        guard let extEnd else { return nil }

        // 从扩展名往前回溯，取出「可能的路径片段」。
        // 这里不能遇到空格就停：文件名本身可能含空格（如 `/Users/me/我的 图片/a.png`）。
        var start = index
        while start > 0 {
            let previous = chars[start - 1]
            if previous == "\"" || previous == "'" || previous == "(" || previous == "[" || previous == "<" {
                break
            }
            start -= 1
        }

        // 片段可能包含前面的普通文字（如「看这个 /a/b.png」），
        // 因此按斜杠边界从长到短逐个尝试，直到命中真实存在的文件。
        var candidateStart = start
        while candidateStart < extEnd {
            if chars[candidateStart] == "/" {
                let candidate = String(chars[candidateStart..<extEnd])
                if let url = validatedImageURL(candidate) {
                    return PathMatch(path: candidate, url: url, end: extEnd)
                }
            }
            candidateStart += 1
        }

        return nil
    }

    /// 校验路径确实指向一个存在的图片文件
    private static func validatedImageURL(_ path: String) -> URL? {
        guard path.hasPrefix("/") else { return nil }
        let url = URL(fileURLWithPath: path)
        var isDirectory: ObjCBool = false
        guard FileManager.default.fileExists(atPath: url.path, isDirectory: &isDirectory),
              !isDirectory.boolValue else {
            return nil
        }
        return url
    }
}
