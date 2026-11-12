import AppKit
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
        .defaultPosition(.center)
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
        .background(MainWindowPlacerView())
    }
}

/// 还没有记住过位置时，主窗口出现在当前屏幕中间；之后记住用户拖动后的位置。
private struct MainWindowPlacerView: NSViewRepresentable {
    func makeNSView(context: Context) -> NSView { MainWindowPlacer() }
    func updateNSView(_ nsView: NSView, context: Context) {}
}

private final class MainWindowPlacer: NSView {
    private var placed = false

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        guard let window, !placed else { return }
        NotificationCenter.default.addObserver(
            self,
            selector: #selector(windowDidBecomeKey(_:)),
            name: NSWindow.didBecomeKeyNotification,
            object: window
        )
        if window.isKeyWindow {
            applyOnce(to: window)
        }
    }

    @objc private func windowDidBecomeKey(_ note: Notification) {
        guard let window = note.object as? NSWindow else { return }
        applyOnce(to: window)
    }

    private func applyOnce(to window: NSWindow) {
        guard !placed else { return }
        placed = true
        NotificationCenter.default.removeObserver(
            self,
            name: NSWindow.didBecomeKeyNotification,
            object: window
        )
        MainWindowFrame.apply(to: window)
    }

    deinit {
        NotificationCenter.default.removeObserver(self)
    }
}

private enum MainWindowFrame {
    static let autosaveName: NSWindow.FrameAutosaveName = "TonoteMainWindow"

    static func apply(to window: NSWindow) {
        let key = "NSWindow Frame \(autosaveName)"
        if UserDefaults.standard.object(forKey: key) == nil {
            window.center()
            window.saveFrame(usingName: autosaveName)
        }
        window.setFrameAutosaveName(autosaveName)
    }
}
