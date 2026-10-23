import Foundation

/// 百度网盘开放平台配置。
///
/// 凭证从本地 `baidupan.secret.json`（已加入 .gitignore，不入库）读取，
/// 避免敏感信息硬编码进源码。该文件需包含 appKey / appSecret / appID 三个字段。
public enum BaiduPanConfig {

    // MARK: 凭证（从 baidupan.secret.json 读取）
    private struct Secret: Codable {
        let appKey: String
        let appSecret: String
        let appID: String
    }

    private static let secret: Secret? = loadSecret()

    /// AppKey（API Key / client_id）
    public static var appKey: String { secret?.appKey ?? "" }
    /// AppSecret（SecretKey / client_secret）
    public static var appSecret: String { secret?.appSecret ?? "" }
    /// AppID（部分接口需要）
    public static var appID: String { secret?.appID ?? "" }

    private static func loadSecret() -> Secret? {
        let bundleURL = Bundle.main.url(forResource: "baidupan.secret", withExtension: "json")
        let cwdURL = URL(fileURLWithPath: FileManager.default.currentDirectoryPath)
            .appendingPathComponent("baidupan.secret.json")
        for url in [bundleURL, cwdURL].compactMap({ $0 })
        where FileManager.default.fileExists(atPath: url.path) {
            if let data = try? Data(contentsOf: url),
               let s = try? JSONDecoder().decode(Secret.self, from: data) {
                return s
            }
        }
        return nil
    }

    // MARK: OAuth 回调
    /// 自定义 URL Scheme 回调地址，需与开放平台「安全设置」中的授权回调地址完全一致。
    public static let redirectURI = "tongnote://oauth/callback"
    /// ASWebAuthenticationSession 捕获回调所用的 scheme（不含 `://`）
    public static let callbackScheme = "tongnote"

    // MARK: 授权范围
    /// basic：基础信息；netdisk：网盘读写。
    public static let scope = "basic,netdisk"

    // MARK: 端点
    public static let authorizeURL = URL(string: "https://openapi.baidu.com/oauth/2.0/authorize")!
    public static let tokenURL = URL(string: "https://openapi.baidu.com/oauth/2.0/token")!
    /// 网盘 xpan 接口（用户信息 / 配额）
    public static let xpanAPI = URL(string: "https://pan.baidu.com/rest/2.0/xpan/nas")!

    /// 是否已填入有效凭证
    public static var isConfigured: Bool {
        !appKey.isEmpty && !appSecret.isEmpty
    }
}
