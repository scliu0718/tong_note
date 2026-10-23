import Foundation

public enum BaiduPanError: LocalizedError {
    case notConfigured
    case invalidResponse
    case server(message: String)
    case noToken

    public var errorDescription: String? {
        switch self {
        case .notConfigured:
            return "尚未配置百度网盘 AppKey / AppSecret，请先在 BaiduPanConfig 中填写"
        case .invalidResponse:
            return "百度网盘返回数据异常"
        case .server(let m):
            return "百度网盘接口错误：\(m)"
        case .noToken:
            return "本地无有效令牌，请重新登录授权"
        }
    }
}

/// 百度网盘开放平台客户端：授权、令牌交换/续期、基础信息查询。
public final class BaiduPanClient {
    public static let shared = BaiduPanClient()

    public let tokenStore = BaiduPanTokenStore()
    private let session = URLSession.shared

    public init() {}

    public var currentToken: BaiduPanToken? { tokenStore.load() }

    // MARK: 授权

    /// 构造授权页 URL（授权码模式）。
    public func authorizationURL(state: String) -> URL {
        var comps = URLComponents(url: BaiduPanConfig.authorizeURL, resolvingAgainstBaseURL: false)!
        comps.queryItems = [
            URLQueryItem(name: "response_type", value: "code"),
            URLQueryItem(name: "client_id", value: BaiduPanConfig.appKey),
            URLQueryItem(name: "redirect_uri", value: BaiduPanConfig.redirectURI),
            URLQueryItem(name: "scope", value: BaiduPanConfig.scope),
            URLQueryItem(name: "display", value: "popup"),
            URLQueryItem(name: "state", value: state),
        ]
        return comps.url!
    }

    // MARK: 令牌

    /// 用授权码换取令牌并落盘。
    public func exchangeCode(_ code: String) async throws -> BaiduPanToken {
        var comps = URLComponents(url: BaiduPanConfig.tokenURL, resolvingAgainstBaseURL: false)!
        comps.queryItems = [
            URLQueryItem(name: "grant_type", value: "authorization_code"),
            URLQueryItem(name: "code", value: code),
            URLQueryItem(name: "client_id", value: BaiduPanConfig.appKey),
            URLQueryItem(name: "client_secret", value: BaiduPanConfig.appSecret),
            URLQueryItem(name: "redirect_uri", value: BaiduPanConfig.redirectURI),
        ]
        let raw = try await get(comps.url!)
        let resp = try JSONDecoder().decode(BaiduPanTokenResponse.self, from: raw)
        if let err = resp.error {
            throw BaiduPanError.server(message: resp.error_description ?? err)
        }
        let token = BaiduPanToken(accessToken: resp.access_token,
                                  refreshToken: resp.refresh_token,
                                  expiresIn: resp.expires_in,
                                  scope: resp.scope,
                                  createdAt: Date())
        tokenStore.save(token)
        return token
    }

    /// 用 refresh_token 续期并落盘。
    public func refresh(_ token: BaiduPanToken) async throws -> BaiduPanToken {
        guard !token.refreshToken.isEmpty else { throw BaiduPanError.noToken }
        var comps = URLComponents(url: BaiduPanConfig.tokenURL, resolvingAgainstBaseURL: false)!
        comps.queryItems = [
            URLQueryItem(name: "grant_type", value: "refresh_token"),
            URLQueryItem(name: "refresh_token", value: token.refreshToken),
            URLQueryItem(name: "client_id", value: BaiduPanConfig.appKey),
            URLQueryItem(name: "client_secret", value: BaiduPanConfig.appSecret),
        ]
        let raw = try await get(comps.url!)
        let resp = try JSONDecoder().decode(BaiduPanTokenResponse.self, from: raw)
        if let err = resp.error {
            throw BaiduPanError.server(message: resp.error_description ?? err)
        }
        // 百度刷新时若未下发新 refresh_token，沿用旧值
        let newRefresh = resp.refresh_token.isEmpty ? token.refreshToken : resp.refresh_token
        let newToken = BaiduPanToken(accessToken: resp.access_token,
                                     refreshToken: newRefresh,
                                     expiresIn: resp.expires_in,
                                     scope: resp.scope ?? token.scope,
                                     createdAt: Date())
        tokenStore.save(newToken)
        return newToken
    }

    /// 获取有效令牌（过期则自动续期）。
    public func validToken() async throws -> BaiduPanToken {
        guard let token = currentToken else { throw BaiduPanError.noToken }
        if token.isExpired {
            return try await refresh(token)
        }
        return token
    }

    // MARK: 基础信息

    /// 网盘用户信息。
    public func userInfo(accessToken: String) async throws -> BaiduPanUserInfo {
        let url = xpanURL(method: "uinfo", accessToken: accessToken)
        let (data, _) = try await session.data(from: url)
        return try JSONDecoder().decode(BaiduPanUserInfo.self, from: data)
    }

    /// 网盘配额。
    public func quota(accessToken: String) async throws -> BaiduPanQuota {
        let url = xpanURL(method: "quota", accessToken: accessToken)
        let (data, _) = try await session.data(from: url)
        return try JSONDecoder().decode(BaiduPanQuota.self, from: data)
    }

    // MARK: 退出

    public func logout() {
        tokenStore.clear()
    }

    // MARK: 私有

    private func get(_ url: URL) async throws -> Data {
        let (data, _) = try await session.data(from: url)
        return data
    }

    private func xpanURL(method: String, accessToken: String) -> URL {
        var comps = URLComponents(url: BaiduPanConfig.xpanAPI, resolvingAgainstBaseURL: false)!
        comps.queryItems = [
            URLQueryItem(name: "method", value: method),
            URLQueryItem(name: "access_token", value: accessToken),
        ]
        return comps.url!
    }
}
