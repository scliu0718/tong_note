import AppKit
import AuthenticationServices
import WorkLogCore

/// 通过系统 Web 会话完成百度网盘 OAuth 授权，返回授权码（code）。
enum BaiduPanAuthSession {

    /// 发起授权，成功后返回授权码。
    @MainActor
    static func authorize() async throws -> String {
        guard BaiduPanConfig.isConfigured else { throw BaiduPanError.notConfigured }
        let state = UUID().uuidString
        let url = BaiduPanClient.shared.authorizationURL(state: state)

        return try await withCheckedThrowingContinuation { continuation in
            let session = ASWebAuthenticationSession(
                url: url,
                callbackURLScheme: BaiduPanConfig.callbackScheme
            ) { callbackURL, error in
                if let error = error {
                    let nsError = error as NSError
                    // 用户主动取消，不作为系统错误上抛
                    if nsError.domain == ASWebAuthenticationSessionErrorDomain &&
                        nsError.code == ASWebAuthenticationSessionError.canceledLogin.rawValue {
                        continuation.resume(throwing: BaiduPanError.server(message: "已取消登录"))
                    } else {
                        continuation.resume(throwing: error)
                    }
                    return
                }
                guard let callbackURL = callbackURL,
                      let comps = URLComponents(url: callbackURL, resolvingAgainstBaseURL: false),
                      let code = comps.queryItems?.first(where: { $0.name == "code" })?.value else {
                    continuation.resume(throwing: BaiduPanError.invalidResponse)
                    return
                }
                continuation.resume(returning: code)
            }
            // presentationContextProvider 为弱引用，需用单例长期持有
            session.presentationContextProvider = PresentationContext.shared
            session.prefersEphemeralWebBrowserSession = false
            session.start()
        }
    }
}

/// 提供授权会话展示所用的窗口（单例，避免弱引用被提前释放）。
private final class PresentationContext: NSObject, ASWebAuthenticationPresentationContextProviding {
    static let shared = PresentationContext()

    func presentationAnchor(for session: ASWebAuthenticationSession) -> ASPresentationAnchor {
        let windows = NSApplication.shared.windows
        if let key = windows.first(where: { $0.isKeyWindow }) { return key }
        if let visible = windows.first(where: { $0.isVisible }) { return visible }
        if let any = windows.first { return any }
        return NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1, height: 1),
                        styleMask: [.borderless],
                        backing: .buffered,
                        defer: false)
    }
}
