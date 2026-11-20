import SwiftUI

/// 「背景颜色」图标：油漆桶 + 水滴 + 底部色条（与 Material format_color_fill 一致）。
/// 设计画布 24x24，等比缩放填满所给 frame。
struct PaintBucketIcon: View {
    var body: some View {
        PaintBucketShape()
            .fill(style: FillStyle(eoFill: true))
    }
}

private struct PaintBucketShape: Shape {
    func path(in rect: CGRect) -> Path {
        var p = Path()
        func pt(_ x: CGFloat, _ y: CGFloat) -> CGPoint { CGPoint(x: x, y: y) }

        // 桶身外轮廓
        p.move(to: pt(16.56, 8.94))
        p.addLine(to: pt(7.62, 0))
        p.addLine(to: pt(6.21, 1.41))
        p.addLine(to: pt(8.59, 3.79))
        p.addLine(to: pt(3.44, 8.94))
        p.addCurve(to: pt(3.44, 11.06), control1: pt(2.85, 9.53), control2: pt(2.85, 10.48))
        p.addLine(to: pt(8.94, 16.56))
        p.addCurve(to: pt(10, 17), control1: pt(9.23, 16.85), control2: pt(9.62, 17))
        p.addCurve(to: pt(11.06, 16.56), control1: pt(10.38, 17), control2: pt(10.77, 16.85))
        p.addLine(to: pt(16.56, 11.06))
        p.addCurve(to: pt(16.56, 8.94), control1: pt(17.15, 10.48), control2: pt(17.15, 9.53))
        p.closeSubpath()

        // 桶内镂空（奇偶填充）
        p.move(to: pt(5.21, 10))
        p.addLine(to: pt(10, 5.21))
        p.addLine(to: pt(14.79, 10))
        p.closeSubpath()

        // 水滴
        p.move(to: pt(19, 11.5))
        p.addCurve(to: pt(17, 15), control1: pt(19, 11.5), control2: pt(17, 13.67))
        p.addCurve(to: pt(19, 17), control1: pt(17, 16.1), control2: pt(17.9, 17))
        p.addCurve(to: pt(21, 15), control1: pt(20.1, 17), control2: pt(21, 16.1))
        p.addCurve(to: pt(19, 11.5), control1: pt(21, 13.67), control2: pt(19, 11.5))
        p.closeSubpath()

        // 底部色条
        p.addRect(CGRect(x: 2, y: 20, width: 20, height: 4))

        let scale = min(rect.width, rect.height) / 24
        let dx = rect.minX + (rect.width - 24 * scale) / 2
        let dy = rect.minY + (rect.height - 24 * scale) / 2
        return p.applying(CGAffineTransform(scaleX: scale, y: scale).concatenating(
            CGAffineTransform(translationX: dx, y: dy)))
    }
}
