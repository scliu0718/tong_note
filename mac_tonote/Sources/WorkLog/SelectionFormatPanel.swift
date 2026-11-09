import AppKit
import SwiftUI

/// 选中文字后浮出的格式窗口（独立的、不抢焦点的浮动面板）。
///
/// 用 `NSPanel` 而不是 SwiftUI `popover`，原因：
/// 1. 浮窗不能抢走 NSTextView 的第一响应者，否则选区会失焦、后续输入跑到别处；
/// 2. 位置要能按选区的屏幕矩形精确摆放，并自动避开屏幕边缘。
final class SelectionFormatPanel {

    static let shared = SelectionFormatPanel()

    private static let barSize = NSSize(width: 332, height: 112)
    private static let cardPadding: CGFloat = 9
    private static let shadowMargin: CGFloat = 10
    private static var panelSize: NSSize {
        NSSize(width: barSize.width + 2 * (cardPadding + shadowMargin),
               height: barSize.height + 2 * (cardPadding + shadowMargin))
    }

    private var panel: NSPanel?
    private var isVisible = false
    private var resignObserver: NSObjectProtocol?

    private init() {
        resignObserver = NotificationCenter.default.addObserver(
            forName: NSApplication.didResignActiveNotification,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            self?.hide()
        }
    }

    deinit {
        if let resignObserver {
            NotificationCenter.default.removeObserver(resignObserver)
        }
    }

    func hide() {
        panel?.orderOut(nil)
        isVisible = false
    }

    /// 在选区上方显示格式窗口；上方空间不够时自动改到下方。
    func show<V: View>(at screenRect: NSRect, bar: V) {
        let panel = self.panel ?? makePanel()
        self.panel = panel

        // 滚动时选区位置会连续变化：只移动窗口，不反复重建 SwiftUI 内容。
        guard !isVisible || panel.contentView == nil else {
            position(panel, at: screenRect)
            return
        }

        let root = AnyView(
            bar
                .frame(width: Self.barSize.width, height: Self.barSize.height, alignment: .topLeading)
                .padding(Self.cardPadding)
                .background(
                    RoundedRectangle(cornerRadius: 11)
                        .fill(Color(hex: 0xFFFFFF))
                        .overlay(
                            RoundedRectangle(cornerRadius: 11)
                                .stroke(Color.black.opacity(0.10), lineWidth: 0.6)
                        )
                )
                .padding(Self.shadowMargin)
        )

        let hosting = NSHostingView(rootView: root)
        hosting.frame = NSRect(origin: .zero, size: Self.panelSize)
        panel.contentView = hosting
        panel.setContentSize(Self.panelSize)

        position(panel, at: screenRect)
        panel.orderFront(nil)
        isVisible = true
    }

    private func makePanel() -> NSPanel {
        let panel = NonActivatingPanel(
            contentRect: NSRect(origin: .zero, size: Self.panelSize),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )
        panel.isFloatingPanel = true
        panel.level = .floating
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = true
        panel.hidesOnDeactivate = true
        panel.becomesKeyOnlyIfNeeded = true
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .ignoresCycle]
        return panel
    }

    private func position(_ panel: NSPanel, at rect: NSRect) {
        let size = panel.frame.size
        let screen = NSScreen.screens.first { $0.frame.contains(rect.origin) } ?? NSScreen.main
        let visible = screen?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1440, height: 900)

        var x = rect.minX - Self.cardPadding - Self.shadowMargin
        var y = rect.maxY + 8
        if y + size.height > visible.maxY - 6 {
            y = rect.minY - 8 - size.height
        }
        x = min(max(x, visible.minX + 6), visible.maxX - size.width - 6)
        y = min(max(y, visible.minY + 6), visible.maxY - size.height - 6)
        panel.setFrameOrigin(NSPoint(x: x, y: y))
    }

    /// 永不接受键盘焦点，保证编辑器里的选区始终处于活动状态。
    private final class NonActivatingPanel: NSPanel {
        override var canBecomeKey: Bool { false }
        override var canBecomeMain: Bool { false }
    }
}
