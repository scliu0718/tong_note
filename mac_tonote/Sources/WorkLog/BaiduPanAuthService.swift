import SwiftUI
import WorkLogCore

/// 百度网盘登录状态与账号信息（应用级单例，注入 environmentObject）。
@MainActor
final class BaiduPanAuthService: ObservableObject {

    enum State: Equatable {
        case signedOut
        case authorizing
        case signedIn
        case failed(String)
    }

    @Published private(set) var state: State = .signedOut
    @Published private(set) var userInfo: BaiduPanUserInfo?
    @Published private(set) var quota: BaiduPanQuota?

    private let client = BaiduPanClient.shared

    /// 是否已登录（含本地已有令牌的自动恢复）
    var isSignedIn: Bool {
        if case .signedIn = state { return true }
        return false
    }

    var errorMessage: String? {
        if case .failed(let msg) = state { return msg }
        return nil
    }

    var isAuthorizing: Bool {
        if case .authorizing = state { return true }
        return false
    }

    init() {
        // 启动时若本地已有令牌，直接视为已登录，并异步刷新账号信息
        if client.currentToken != nil {
            state = .signedIn
        }
    }

    /// 发起授权登录。
    func signIn() {
        guard BaiduPanConfig.isConfigured else {
            state = .failed("尚未配置 AppKey / AppSecret，请先在 BaiduPanConfig.swift 中填写")
            return
        }
        state = .authorizing
        Task {
            do {
                let code = try await BaiduPanAuthSession.authorize()
                _ = try await client.exchangeCode(code)
                await loadAccount()
                state = .signedIn
            } catch {
                state = .failed(error.localizedDescription)
            }
        }
    }

    /// 拉取用户信息与配额（失败不阻塞登录态）。
    func loadAccount() async {
        do {
            let token = try await client.validToken()
            async let u = client.userInfo(accessToken: token.accessToken)
            async let q = client.quota(accessToken: token.accessToken)
            let (user, quotaInfo) = try await (u, q)
            userInfo = user
            quota = quotaInfo
        } catch {
            // 忽略信息拉取失败，登录态仍有效
        }
    }

    /// 退出登录，清除本地令牌。
    func signOut() {
        client.logout()
        state = .signedOut
        userInfo = nil
        quota = nil
    }
}
