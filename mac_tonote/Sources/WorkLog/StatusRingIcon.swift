import SwiftUI
import WorkLogCore

/// 任务状态图标：待完成=红色播放三角 / 进行中=黄色指针 / 已完成=绿色对勾 / 已终止=灰色减号。
/// 路径为 viewBox 1024 的 SVG 填充路径（与 Android / Windows 同源），按 SVG 默认 nonzero 规则填充。
struct StatusRingIcon: View {
    let status: TodoStatus

    var body: some View {
        switch status {
        case .todo:
            SVGPathShape(d: Self.todoPath).fill(Theme.statusTodo)
        case .doing:
            SVGPathShape(d: Self.doingPath).fill(Theme.statusDoing)
        case .done:
            SVGPathShape(d: Self.donePath).fill(Theme.statusDone)
        case .terminated:
            SVGPathShape(d: Self.terminatedPath).fill(Theme.statusTerminated)
        }
    }

    private static let todoPath =
        "M0 512c0 281.6 228.937143 512 512 512s512-228.937143 512-512c0.146286-281.453714-228.864-512-511.853714-512C230.546286 0 0 229.010286 0 512z m719.725714 21.211429L407.771429 713.289143a18.285714 18.285714 0 0 1-27.501715-15.945143V337.261714c0-14.116571 15.286857-22.966857 27.501715-15.945143L719.725714 501.394286a18.505143 18.505143 0 0 1 0 31.890285z"
    private static let doingPath =
        "M512 0C229.233778 0 0 229.233778 0 512s229.233778 512 512 512 512-229.233778 512-512S794.766222 0 512 0z m306.048 622.165333H437.347556v-10.311111h-3.825778V231.153778h64.725333v326.300444H818.062222v64.711111z"
    private static let donePath =
        "M512 1024C229.216 1024 0 794.784 0 512S229.216 0 512 0s512 229.216 512 512-229.216 512-512 512z m-49.568-377.152l-146.496-148.224-96.512 92.256c70.208 37.76 168.64 106.816 252.896 213.696 59.52-111.936 243.008-340.896 332.256-361.28-14.4-57.728-22.56-166.016 0-223.872-183.04 120.704-342.144 427.424-342.144 427.424z"
    private static let terminatedPath =
        "M512 64.512q92.16 0 173.568 34.816t142.336 95.744 95.744 142.336 34.816 173.568-34.816 173.568-95.744 142.336-142.336 95.744-173.568 34.816-174.08-34.816-142.336-95.744-95.744-142.336-35.328-173.568 35.328-173.568 95.744-142.336 142.336-95.744 174.08-34.816zM707.584 574.464q29.696 0 49.664-18.432t19.968-47.104q0-29.696-19.968-46.08t-49.664-16.384l-392.192 0q-29.696 0-49.664 16.384t-19.968 46.08q0 28.672 19.968 47.104t49.664 18.432l392.192 0z"
}

/// 把 SVG path 字符串（viewBox 1024×1024）画成 SwiftUI Shape，等比缩放并居中。
/// 支持 M/L/H/V/C/S/Q/T/A/Z 及其小写相对形式。
struct SVGPathShape: Shape {
    let d: String
    var viewBox: CGFloat = 1024

    func path(in rect: CGRect) -> Path {
        let raw = SVGPathParser.parse(d)
        let scale = min(rect.width, rect.height) / viewBox
        let dx = rect.minX + (rect.width - viewBox * scale) / 2
        let dy = rect.minY + (rect.height - viewBox * scale) / 2
        return raw.applying(CGAffineTransform(scaleX: scale, y: scale)
            .concatenating(CGAffineTransform(translationX: dx, y: dy)))
    }
}

private enum SVGPathParser {
    static func parse(_ d: String) -> Path {
        var s = PathScanner(Array(d))
        var path = Path()
        var cur = CGPoint.zero
        var start = CGPoint.zero
        var lastCubic: CGPoint?   // 上一段三次曲线的第二控制点（S 用）
        var lastQuad: CGPoint?    // 上一段二次曲线的控制点（T 用）
        var cmd: Character = " "

        while true {
            s.skipSeparators()
            if s.atEnd { break }
            if let c = s.peekLetter() {
                cmd = c
                s.advance()
                if cmd == "z" || cmd == "Z" {
                    path.closeSubpath()
                    cur = start
                    lastCubic = nil; lastQuad = nil
                    continue
                }
            } else if cmd == " " {
                break   // 没有起始命令，放弃
            }
            let rel = cmd.isLowercase
            let base = rel ? cur : .zero

            switch cmd.uppercased().first! {
            case "M":
                guard let x = s.number(), let y = s.number() else { return path }
                cur = CGPoint(x: base.x + x, y: base.y + y)
                start = cur
                path.move(to: cur)
                cmd = rel ? "l" : "L"   // 后续坐标对按 lineto 处理
                lastCubic = nil; lastQuad = nil
            case "L":
                guard let x = s.number(), let y = s.number() else { return path }
                cur = CGPoint(x: base.x + x, y: base.y + y)
                path.addLine(to: cur)
                lastCubic = nil; lastQuad = nil
            case "H":
                guard let x = s.number() else { return path }
                cur = CGPoint(x: base.x + x, y: cur.y)
                path.addLine(to: cur)
                lastCubic = nil; lastQuad = nil
            case "V":
                guard let y = s.number() else { return path }
                cur = CGPoint(x: cur.x, y: base.y + y)
                path.addLine(to: cur)
                lastCubic = nil; lastQuad = nil
            case "C":
                guard let x1 = s.number(), let y1 = s.number(),
                      let x2 = s.number(), let y2 = s.number(),
                      let x = s.number(), let y = s.number() else { return path }
                let c1 = CGPoint(x: base.x + x1, y: base.y + y1)
                let c2 = CGPoint(x: base.x + x2, y: base.y + y2)
                let end = CGPoint(x: base.x + x, y: base.y + y)
                path.addCurve(to: end, control1: c1, control2: c2)
                lastCubic = c2; lastQuad = nil
                cur = end
            case "S":
                guard let x2 = s.number(), let y2 = s.number(),
                      let x = s.number(), let y = s.number() else { return path }
                let c1 = lastCubic.map { CGPoint(x: 2 * cur.x - $0.x, y: 2 * cur.y - $0.y) } ?? cur
                let c2 = CGPoint(x: base.x + x2, y: base.y + y2)
                let end = CGPoint(x: base.x + x, y: base.y + y)
                path.addCurve(to: end, control1: c1, control2: c2)
                lastCubic = c2; lastQuad = nil
                cur = end
            case "Q":
                guard let x1 = s.number(), let y1 = s.number(),
                      let x = s.number(), let y = s.number() else { return path }
                let c = CGPoint(x: base.x + x1, y: base.y + y1)
                let end = CGPoint(x: base.x + x, y: base.y + y)
                path.addQuadCurve(to: end, control: c)
                lastQuad = c; lastCubic = nil
                cur = end
            case "T":
                guard let x = s.number(), let y = s.number() else { return path }
                let c = lastQuad.map { CGPoint(x: 2 * cur.x - $0.x, y: 2 * cur.y - $0.y) } ?? cur
                let end = CGPoint(x: base.x + x, y: base.y + y)
                path.addQuadCurve(to: end, control: c)
                lastQuad = c; lastCubic = nil
                cur = end
            case "A":
                guard let rx = s.number(), let ry = s.number(), let rot = s.number(),
                      let large = s.flag(), let sweep = s.flag(),
                      let x = s.number(), let y = s.number() else { return path }
                let end = CGPoint(x: base.x + x, y: base.y + y)
                addArc(&path, from: cur, rx: rx, ry: ry, rotation: rot,
                       large: large, sweep: sweep, to: end)
                cur = end
                lastCubic = nil; lastQuad = nil
            default:
                return path
            }
        }
        return path
    }

    /// SVG 椭圆弧（端点参数）→ 若干段三次贝塞尔。
    private static func addArc(_ path: inout Path, from p0: CGPoint, rx rx0: CGFloat, ry ry0: CGFloat,
                               rotation: CGFloat, large: Bool, sweep: Bool, to p1: CGPoint) {
        var rx = abs(rx0), ry = abs(ry0)
        if rx == 0 || ry == 0 || p0 == p1 { path.addLine(to: p1); return }
        let phi = rotation * .pi / 180
        let cosPhi = cos(phi), sinPhi = sin(phi)
        let dx2 = (p0.x - p1.x) / 2, dy2 = (p0.y - p1.y) / 2
        let x1p = cosPhi * dx2 + sinPhi * dy2
        let y1p = -sinPhi * dx2 + cosPhi * dy2
        let lambda = (x1p * x1p) / (rx * rx) + (y1p * y1p) / (ry * ry)
        if lambda > 1 { let k = sqrt(lambda); rx *= k; ry *= k }
        let sign: CGFloat = (large == sweep) ? -1 : 1
        let num = rx * rx * ry * ry - rx * rx * y1p * y1p - ry * ry * x1p * x1p
        let den = rx * rx * y1p * y1p + ry * ry * x1p * x1p
        let coef = sign * sqrt(max(0, num / den))
        let cxp = coef * (rx * y1p / ry)
        let cyp = coef * (-ry * x1p / rx)
        let cx = cosPhi * cxp - sinPhi * cyp + (p0.x + p1.x) / 2
        let cy = sinPhi * cxp + cosPhi * cyp + (p0.y + p1.y) / 2

        func angle(_ ux: CGFloat, _ uy: CGFloat, _ vx: CGFloat, _ vy: CGFloat) -> CGFloat {
            atan2(ux * vy - uy * vx, ux * vx + uy * vy)
        }
        let theta1 = angle(1, 0, (x1p - cxp) / rx, (y1p - cyp) / ry)
        var dTheta = angle((x1p - cxp) / rx, (y1p - cyp) / ry, (-x1p - cxp) / rx, (-y1p - cyp) / ry)
        if !sweep && dTheta > 0 { dTheta -= 2 * .pi }
        if sweep && dTheta < 0 { dTheta += 2 * .pi }

        let segments = max(1, Int(ceil(abs(dTheta) / (.pi / 2))))
        let delta = dTheta / CGFloat(segments)
        let t = 4.0 / 3.0 * tan(delta / 4)
        func map(_ x: CGFloat, _ y: CGFloat) -> CGPoint {
            CGPoint(x: cx + rx * cosPhi * x - ry * sinPhi * y,
                    y: cy + rx * sinPhi * x + ry * cosPhi * y)
        }
        var a1 = theta1
        for _ in 0..<segments {
            let a2 = a1 + delta
            let c1 = map(cos(a1) - t * sin(a1), sin(a1) + t * cos(a1))
            let c2 = map(cos(a2) + t * sin(a2), sin(a2) - t * cos(a2))
            let e = map(cos(a2), sin(a2))
            path.addCurve(to: e, control1: c1, control2: c2)
            a1 = a2
        }
    }

    /// 极简扫描器：数字（含科学计数、省略分隔的负号 / 小数点）、命令字母、圆弧标志位。
    private struct PathScanner {
        let chars: [Character]
        var i = 0
        init(_ chars: [Character]) { self.chars = chars }
        var atEnd: Bool { i >= chars.count }

        mutating func advance() { i += 1 }
        mutating func skipSeparators() {
            while i < chars.count, chars[i] == " " || chars[i] == "," || chars[i] == "\n" || chars[i] == "\t" { i += 1 }
        }
        func peekLetter() -> Character? {
            guard i < chars.count, chars[i].isLetter, chars[i] != "e", chars[i] != "E" else { return nil }
            return chars[i]
        }
        mutating func number() -> CGFloat? {
            skipSeparators()
            guard i < chars.count else { return nil }
            var j = i
            if chars[j] == "+" || chars[j] == "-" { j += 1 }
            var digits = false
            while j < chars.count, chars[j].isNumber { j += 1; digits = true }
            if j < chars.count, chars[j] == "." {
                j += 1
                while j < chars.count, chars[j].isNumber { j += 1; digits = true }
            }
            guard digits else { return nil }
            if j < chars.count, chars[j] == "e" || chars[j] == "E" {
                var k = j + 1
                if k < chars.count, chars[k] == "+" || chars[k] == "-" { k += 1 }
                if k < chars.count, chars[k].isNumber {
                    while k < chars.count, chars[k].isNumber { k += 1 }
                    j = k
                }
            }
            let text = String(chars[i..<j])
            i = j
            return Double(text).map { CGFloat($0) }
        }
        mutating func flag() -> Bool? {
            skipSeparators()
            guard i < chars.count, chars[i] == "0" || chars[i] == "1" else { return nil }
            let v = chars[i] == "1"
            i += 1
            return v
        }
    }
}
