import SwiftUI
import WorkLogCore

extension NoteStore {
    /// 返回数组元素 Binding，编辑即写回并保存（UI 层桥接）
    func binding(for id: UUID) -> Binding<Note> {
        Binding(
            get: { self.note(with: id) ?? Note(title: "") },
            set: { self.update($0) }
        )
    }

    /// 正文 + 正文格式的原子 Binding：编辑器每次保存都同时写入两者
    func contentBinding(for id: UUID) -> Binding<NoteContent> {
        Binding(
            get: {
                let note = self.note(with: id)
                return NoteContent(body: note?.body ?? "", format: note?.contentFormat ?? .markdown)
            },
            set: { self.updateContent(id, body: $0.body, format: $0.format) }
        )
    }
}
