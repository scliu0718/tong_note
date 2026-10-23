import SwiftUI

@main
struct WorkLogApp: App {
    @StateObject private var auth = BaiduPanAuthService()

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
        }
        .defaultSize(width: 1120, height: 700)
        .windowResizability(.contentMinSize)
    }
}
