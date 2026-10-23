import Foundation

/// 百度网盘 OAuth 令牌（authorization_code / refresh_token 共用）。
public struct BaiduPanToken: Codable, Equatable {
    public var accessToken: String
    public var refreshToken: String
    /// 有效期（秒），通常 2592000 = 30 天
    public var expiresIn: Int
    public var scope: String?
    /// 本地记录获取时间，用于判断是否过期
    public var createdAt: Date

    public init(accessToken: String,
                refreshToken: String,
                expiresIn: Int,
                scope: String?,
                createdAt: Date) {
        self.accessToken = accessToken
        self.refreshToken = refreshToken
        self.expiresIn = expiresIn
        self.scope = scope
        self.createdAt = createdAt
    }

    /// 是否已过期（提前 5 分钟视为过期，避免边界）
    public var isExpired: Bool {
        let deadline = createdAt.addingTimeInterval(TimeInterval(expiresIn))
        return Date() >= deadline.addingTimeInterval(-300)
    }
}

/// 网盘用户信息（xpan uinfo）。
public struct BaiduPanUserInfo: Codable {
    public let baiduName: String?
    public let netdiskName: String?
    public let avatarURL: String?
    public let vipType: Int?

    enum CodingKeys: String, CodingKey {
        case baiduName = "baidu_name"
        case netdiskName = "netdisk_name"
        case avatarURL = "avatar_url"
        case vipType = "vip_type"
    }

    /// 优先展示网盘昵称，其次百度账号名
    public var displayName: String {
        if let n = netdiskName, !n.isEmpty { return n }
        if let n = baiduName, !n.isEmpty { return n }
        return "百度网盘用户"
    }
}

/// 网盘配额（xpan quota）。
public struct BaiduPanQuota: Codable {
    public let total: Int64?
    public let used: Int64?
}

/// token 端点原始响应（字段命名与百度返回一致）。
struct BaiduPanTokenResponse: Codable {
    let access_token: String
    let refresh_token: String
    let expires_in: Int
    let scope: String?
    let error: String?
    let error_description: String?
}
