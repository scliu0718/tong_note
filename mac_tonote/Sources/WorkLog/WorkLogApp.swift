import SwiftUI
import WorkLogCore

@main
struct WorkLogApp: App {
    @StateObject private var auth = BaiduPanAuthService()
    @StateObject private var sync = SyncEngine()

    var body: some Scene {
        WindowGroup("同笔记") {
            Group {
                if auth.isSignedIn {
                    MainView()
                } else {
                    LoginView()
                }
            }
            .frame(minWidth: 960, minHeight: 620)
            .environmentObject(auth)
            .environmentObject(sync)
            // 浏览器扫码登录的回调：tongnote://oauth/callback
            .onOpenURL { url in auth.handleCallback(url) }
        }
        .defaultSize(width: 1120, height: 700)
        .windowResizability(.contentMinSize)
    }
}
