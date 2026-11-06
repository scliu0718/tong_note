import Foundation
import Combine

/// 一个预设头像：文字或 SF Symbol + 渐变底色（颜色以 0xRRGGBB 存储，View 层再转 Color）。
public struct AvatarPreset: Identifiable, Hashable {
    public let id: String
    public let glyph: String
    public let isSymbol: Bool
    public let color1: UInt32
    public let color2: UInt32

    public init(id: String, glyph: String, isSymbol: Bool = false,
                color1: UInt32, color2: UInt32) {
        self.id = id
        self.glyph = glyph
        self.isSymbol = isSymbol
        self.color1 = color1
        self.color2 = color2
    }
}

/// 用户头像设置：预设图标选择 + 本地持久化 + 变更后上传到百度网盘 /apps/tong_log/avatar.json。
/// 与笔记/任务同步相互独立，互不影响合并逻辑。
public final class AvatarStore: ObservableObject {
    public static let shared = AvatarStore()

    public enum CloudState: Equatable {
        case idle
        case uploading
        case synced
        case failed(String)
    }

    /// 默认提供的候选头像（「同」为默认）。
    public static let presets: [AvatarPreset] = [
        AvatarPreset(id: "tong", glyph: "同", color1: 0x19B46A, color2: 0x0E8A48),
        AvatarPreset(id: "leaf", glyph: "leaf.fill", isSymbol: true, color1: 0x34C77B, color2: 0x0E8A48),
        AvatarPreset(id: "note", glyph: "note.text", isSymbol: true, color1: 0x3AA0FF, color2: 0x0B68D6),
        AvatarPreset(id: "book", glyph: "book.closed.fill", isSymbol: true, color1: 0x8B7BF0, color2: 0x5B45D6),
        AvatarPreset(id: "pen", glyph: "pencil", isSymbol: true, color1: 0xF5A623, color2: 0xD97A06),
        AvatarPreset(id: "star", glyph: "star.fill", isSymbol: true, color1: 0xF2C14E, color2: 0xD9930B),
        AvatarPreset(id: "moon", glyph: "moon.stars.fill", isSymbol: true, color1: 0x5B7FB5, color2: 0x2F4C7E),
        AvatarPreset(id: "heart", glyph: "heart.fill", isSymbol: true, color1: 0xEF7A85, color2: 0xD0465C),
    ]

    private static let defaultsKey = "tonote.avatar.id"
    private static var remotePath: String { BaiduPanConfig.remoteRoot + "/avatar.json" }

    @Published public private(set) var currentID: String
    @Published public private(set) var cloudState: CloudState = .idle

    private let client = BaiduPanClient.shared

    public init() {
        let saved = UserDefaults.standard.string(forKey: Self.defaultsKey)
        currentID = Self.presets.first(where: { $0.id == saved })?.id ?? Self.presets[0].id
    }

    public var current: AvatarPreset {
        Self.presets.first(where: { $0.id == currentID }) ?? Self.presets[0]
    }

    /// 选择头像：立即写本地，然后异步上传到云端（未登录/失败不阻塞 UI）。
    public func select(_ id: String) {
        guard Self.presets.contains(where: { $0.id == id }) else { return }
        currentID = id
        UserDefaults.standard.set(id, forKey: Self.defaultsKey)
        Task { await uploadToCloud(id: id) }
    }

    /// 登录进入主界面时尝试拉取云端头像（仅当用户本地未做过选择时生效，避免覆盖本机选择）。
    public func pullFromCloudIfNeeded() async {
        guard UserDefaults.standard.string(forKey: Self.defaultsKey) == nil,
              let token = try? await client.validToken() else { return }
        do {
            let data = try await client.downloadData(Self.remotePath, accessToken: token.accessToken)
            guard let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let id = obj["id"] as? String,
                  Self.presets.contains(where: { $0.id == id }) else { return }
            await MainActor.run { currentID = id }
        } catch {
            // 云端尚无头像或网络异常：保持默认，不打扰用户
        }
    }

    private func uploadToCloud(id: String) async {
        await MainActor.run { cloudState = .uploading }
        do {
            let token = try await client.validToken()
            let payload = try JSONSerialization.data(withJSONObject: ["id": id])
            try await client.uploadData(payload, to: Self.remotePath, accessToken: token.accessToken)
            await MainActor.run { cloudState = .synced }
        } catch {
            await MainActor.run { cloudState = .failed(error.localizedDescription) }
        }
    }
}
