import Foundation
import Security

/// 百度网盘令牌存储：本地文件（0600 权限，仅当前用户可读写）。
///
/// 之所以不用 Keychain：App 采用临时签名打包，每次重新构建签名都会变化，
/// Keychain 会把新构建视为陌生程序，导致访问令牌时反复弹出「钥匙串验证」密码框。
/// 本地文件方案与笔记数据（notes.json 明文存储）处于同等安全等级，且无任何弹窗。
public final class BaiduPanTokenStore {
    private let fileURL: URL
    /// 旧版本遗留的 Keychain 条目（迁移后删除，不再读取）
    private let legacyService = "com.tong.log.app.baidupan"
    private let legacyAccount = "oauth-token"

    public init() {
        let fm = FileManager.default
        let dir = fm.urls(for: .applicationSupportDirectory, in: .userDomainMask).first!
            .appendingPathComponent("tonote", isDirectory: true)
        try? fm.createDirectory(at: dir, withIntermediateDirectories: true)
        fileURL = dir.appendingPathComponent("baidupan-token.json")
        cleanupLegacyKeychain()
    }

    public func load() -> BaiduPanToken? {
        guard let data = try? Data(contentsOf: fileURL),
              let token = try? JSONDecoder().decode(BaiduPanToken.self, from: data) else { return nil }
        return token
    }

    public func save(_ token: BaiduPanToken) {
        guard let data = try? JSONEncoder().encode(token) else { return }
        try? data.write(to: fileURL, options: .atomic)
        // 限制为仅当前用户可读写
        try? FileManager.default.setAttributes([.posixPermissions: 0o600],
                                               ofItemAtPath: fileURL.path)
    }

    public func clear() {
        try? FileManager.default.removeItem(at: fileURL)
    }

    /// 清理旧版 Keychain 条目（SecItemDelete 不需要 ACL 授权，不会弹框）。
    private func cleanupLegacyKeychain() {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: legacyService,
            kSecAttrAccount as String: legacyAccount,
        ]
        SecItemDelete(query as CFDictionary)
    }
}
