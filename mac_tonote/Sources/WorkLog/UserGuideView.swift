import SwiftUI
import WebKit

/// 打开随应用打包的《使用说明》。登录页和设置页共用这一份。
enum UserGuideWindow {
    private static var window: NSWindow?

    static func show() {
        if let window {
            window.makeKeyAndOrderFront(nil)
            NSApp.activate(ignoringOtherApps: true)
            return
        }
        let host = NSHostingController(rootView: UserGuideWebView())
        let window = NSWindow(contentViewController: host)
        window.title = "同笔记使用说明"
        window.setContentSize(NSSize(width: 860, height: 740))
        window.styleMask = [.titled, .closable, .miniaturizable, .resizable]
        window.minSize = NSSize(width: 560, height: 420)
        window.isReleasedWhenClosed = false
        window.center()
        self.window = window
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }
}

private struct UserGuideWebView: NSViewRepresentable {
    func makeNSView(context: Context) -> WKWebView {
        let web = WKWebView()
        if let url = Bundle.main.url(forResource: "index", withExtension: "html", subdirectory: "guide") {
            web.loadFileURL(url, allowingReadAccessTo: url.deletingLastPathComponent())
        } else {
            web.loadHTMLString(
                "<p style=\"font-family:-apple-system;padding:28px\">没有找到使用说明。请完全退出后重新打开同笔记再试。</p>",
                baseURL: nil
            )
        }
        return web
    }

    func updateNSView(_ web: WKWebView, context: Context) {}
}
