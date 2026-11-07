import Foundation
import Combine

/// 笔记本分类仓库：内置 4 个固定分类 + 用户自建分类。
/// 用单例让侧栏与编辑器共享同一份可观察数据，新建分类后两处立即可选。
public final class NotebookStore: ObservableObject {
    public static let shared = NotebookStore()

    @Published public private(set) var custom: [String] = []

    private let defaultsKey = "tonote.customNotebooks"
    private let maxNameLength = 12

    private init() {
        custom = UserDefaults.standard.stringArray(forKey: defaultsKey) ?? []
    }

    /// 内置分类在前，自建分类在后，顺序稳定。
    public var list: [String] { Notebooks.list + custom }

    /// 新建分类；返回 nil 表示成功，否则返回给用户的错误提示。
    public func add(_ name: String) -> String? {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return "分类名称不能为空" }
        guard trimmed != Notebooks.all else { return "「全部笔记」是内置项，不能用作分类名" }
        guard !list.contains(trimmed) else { return "该分类已存在" }
        guard trimmed.count <= maxNameLength else { return "分类名称最多 \(maxNameLength) 个字" }
        custom.append(trimmed)
        UserDefaults.standard.set(custom, forKey: defaultsKey)
        return nil
    }

    /// 删除自建分类（内置分类不可删除）。
    public func remove(_ name: String) {
        guard custom.contains(name) else { return }
        custom.removeAll { $0 == name }
        UserDefaults.standard.set(custom, forKey: defaultsKey)
    }
}
