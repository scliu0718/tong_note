import AppKit
import WorkLogCore

/// 可缩放的图片附件单元格。
///
/// 负责两件事：
/// 1. 按 `displaySize` 绘制图片（比例由调用方保证）；
/// 2. 绘制缩放边框与右下角拖拽手柄，让用户知道可以拖。
final class ScalableImageCell: NSTextAttachmentCell {

    /// 当前显示尺寸（pt）
    var displaySize: NSSize = .zero {
        didSet { invalidateDisplay() }
    }

    /// 是否显示缩放边框与手柄
    var showsResizeHandles: Bool = false {
        didSet { invalidateDisplay() }
    }

    /// 原图宽高比（用于拖拽时锁定比例）
    var aspectRatio: CGFloat = 1.0

    override var cellSize: NSSize {
        displaySize.width > 0 && displaySize.height > 0
            ? displaySize
            : super.cellSize
    }

    override func draw(withFrame frame: NSRect, in view: NSView) {
        super.draw(withFrame: frame, in: view)

        guard showsResizeHandles else { return }

        let accent = NSColor(srgbRed: 0x1A / 255, green: 0x9E / 255, blue: 0x5C / 255, alpha: 1)

        // 缩放边框
        let border = NSBezierPath(rect: frame.insetBy(dx: 0.5, dy: 0.5))
        border.lineWidth = 1.5
        accent.setStroke()
        border.stroke()

        // 右下角拖拽手柄
        let handleSize: CGFloat = 10
        let handleRect = NSRect(
            x: frame.maxX - handleSize,
            y: frame.minY,
            width: handleSize,
            height: handleSize
        )
        accent.setFill()
        NSBezierPath(rect: handleRect).fill()
        NSColor.white.setStroke()
        let handleBorder = NSBezierPath(rect: handleRect.insetBy(dx: 0.5, dy: 0.5))
        handleBorder.lineWidth = 1
        handleBorder.stroke()
    }

    /// 手柄在附件坐标系中的矩形（用于命中测试）
    func resizeHandleRect(in frame: NSRect) -> NSRect {
        let handleSize: CGFloat = 12
        return NSRect(
            x: frame.maxX - handleSize,
            y: frame.minY,
            width: handleSize,
            height: handleSize
        )
    }
}
