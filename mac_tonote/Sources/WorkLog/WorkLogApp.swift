import SwiftUI

@main
struct WorkLogApp: App {
    @State private var isLoggedIn = false

    var body: some Scene {
        WindowGroup("同笔记") {
            Group {
                if isLoggedIn {
                    MainView(onLogout: { isLoggedIn = false })
                } else {
                    LoginView(onLogin: { isLoggedIn = true })
                }
            }
            .frame(minWidth: 960, minHeight: 620)
        }
        .defaultSize(width: 1120, height: 700)
        .windowResizability(.contentMinSize)
    }
}
