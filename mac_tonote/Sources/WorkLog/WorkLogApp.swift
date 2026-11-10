import SwiftUI
import WorkLogCore

@main
struct WorkLogApp: App {
    @StateObject private var auth = BaiduPanAuthService()
    @StateObject private var sync = SyncEngine()

    var body: some Scene {
        WindowGroup("同笔记") {
            AppRoot()
                .environmentObject(auth)
                .environmentObject(sync)
        }
        .defaultSize(width: 1120, height: 700)
        .windowResizability(.contentMinSize)
    }
}

private struct AppRoot: View {
    @EnvironmentObject private var auth: BaiduPanAuthService
    @Environment(\.scenePhase) private var scenePhase

    var body: some View {
        Group {
            if auth.isSignedIn {
                MainView()
            } else {
                LoginView()
            }
        }
        .frame(minWidth: 960, minHeight: 620)
        // 浏览器扫码登录的回调：tongnote://oauth/callback
        .onOpenURL { url in auth.handleCallback(url) }
        .task(id: auth.isSignedIn) {
            guard auth.isSignedIn else { return }
            await auth.enforceDeviceSession()
        }
        .onChange(of: scenePhase) { _, phase in
            guard phase == .active, auth.isSignedIn else { return }
            Task { await auth.enforceDeviceSession() }
        }
        .onReceive(NotificationCenter.default.publisher(for: .tonoteDeviceKicked)) { _ in
            auth.signOutFromKick()
        }
    }
}
