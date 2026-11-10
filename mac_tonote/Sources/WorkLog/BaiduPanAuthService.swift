import AppKit
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
    /// 被其他设备下线后，登录页上的说明。下一次发起登录时清掉。
    @Published private(set) var signedOutNotice: String?

    /// 浏览器登录流程中用于校验回调的 state（防 CSRF）
    private var pendingBrowserState: String?

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

    var bannerMessage: String? {
        errorMessage ?? signedOutNotice
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
        signedOutNotice = nil
        state = .authorizing
        Task {
            do {
                let code = try await BaiduPanAuthSession.authorize()
                _ = try await client.exchangeCode(code)
                await finishSignIn()
            } catch {
                state = .failed(error.localizedDescription)
            }
        }
    }

    /// 浏览器扫码登录：在系统默认浏览器打开授权页（display=page，含「扫码登录」），
    /// 用户用百度 / 百度网盘 App 扫码授权后，浏览器跳转 tongnote://oauth/callback 拉回本 App，
    /// 由 `handleCallback(_:)` 完成登录。
    func signInViaBrowser() {
        guard BaiduPanConfig.isConfigured else {
            state = .failed("尚未配置 AppKey / AppSecret，请先在 baidupan.secret.json 中填写")
            return
        }
        signedOutNotice = nil
        let nonce = UUID().uuidString
        pendingBrowserState = nonce
        state = .authorizing
        let url = client.authorizationURL(state: nonce, display: "page")
        NSWorkspace.shared.open(url)
    }

    /// 处理浏览器授权后的回调（tongnote://oauth/callback?code=…&state=…）。
    func handleCallback(_ url: URL) {
        guard let comps = URLComponents(url: url, resolvingAgainstBaseURL: false) else { return }
        if let err = comps.queryItems?.first(where: { $0.name == "error" })?.value {
            pendingBrowserState = nil
            state = .failed("授权被拒绝：\(err)")
            return
        }
        guard let code = comps.queryItems?.first(where: { $0.name == "code" })?.value else { return }
        let returnedState = comps.queryItems?.first(where: { $0.name == "state" })?.value
        if let expected = pendingBrowserState, returnedState != expected {
            state = .failed("登录回调校验失败（state 不匹配），请重试")
            return
        }
        pendingBrowserState = nil
        signedOutNotice = nil
        state = .authorizing
        Task {
            do {
                _ = try await client.exchangeCode(code)
                await finishSignIn()
            } catch {
                state = .failed(error.localizedDescription)
            }
        }
    }

    /// 令牌已经落盘。登记失败不取消登录，下次打开或同步时会再写一次设备文件。
    private func finishSignIn() async {
        do {
            try await DeviceRegistry.shared.beginLogin()
        } catch {
            // 设备文件没写上时仍保持登录，本地可以继续记笔记
        }
        await loadAccount()
        state = .signedIn
    }

    /// 已有令牌时核对云端设备文件。读不到文件就保持登录；确认被下线才退出。
    func enforceDeviceSession() async {
        guard isSignedIn else { return }
        do {
            try await DeviceRegistry.shared.ensureNotKicked()
        } catch DeviceSessionError.kicked {
            signOutFromKick()
        } catch {
            // 离线或网盘暂时不可用：不退出
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

    /// 退出登录，清除本地令牌。设备编号保留，下次登录仍是同一台设备。
    func signOut() {
        client.logout()
        state = .signedOut
        userInfo = nil
        quota = nil
    }

    /// 其他设备把本机标成下线。
    func signOutFromKick() {
        signOut()
        signedOutNotice = DeviceSessionError.kicked.localizedDescription
    }
}
