// 把整帧图拆成活动层：swift cut.swift <frames 目录> <输出目录>。坐标为 1200×896 像素，左上原点。
// 加第三个参数 <皮肤目录> 则只生成皮肤：猫取自 frames，键盘、鼠标、桌垫取自皮肤的 cover_eyes.png；
// 耳机取自皮肤的 idle.png 和 cover_eyes.png，没有 idle.png 就不换耳机。
import AppKit

typealias P = CGPoint
func pts(_ a: [Double]) -> [P] { stride(from: 0, to: a.count, by: 2).map { P(x: a[$0], y: a[$0 + 1]) } }
let sRGB = CGColorSpace(name: CGColorSpace.sRGB)!
let W = 1200, H = 896

/// 预乘 RGBA 浮点图。
struct Image {
    var w = W, h = H, px = [SIMD4<Float>](repeating: .zero, count: W * H)
    subscript(x: Int, y: Int) -> SIMD4<Float> {
        get { x < 0 || y < 0 || x >= w || y >= h ? .zero : px[y * w + x] }
        set { if x >= 0, y >= 0, x < w, y < h { px[y * w + x] = newValue } }
    }
    func sample(_ p: P) -> SIMD4<Float> {
        let x = Int(floor(p.x)), y = Int(floor(p.y)), fx = Float(p.x) - Float(x), fy = Float(p.y) - Float(y)
        return (self[x, y] * (1 - fx) + self[x + 1, y] * fx) * (1 - fy) + (self[x, y + 1] * (1 - fx) + self[x + 1, y + 1] * fx) * fy
    }
    init() {}
    init(_ path: String) {
        let cg = NSBitmapImageRep(data: try! Data(contentsOf: URL(fileURLWithPath: path)))!.cgImage!
        var b = [UInt8](repeating: 0, count: W * H * 4)
        CGContext(data: &b, width: W, height: H, bitsPerComponent: 8, bytesPerRow: W * 4, space: sRGB,
                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!.draw(cg, in: CGRect(x: 0, y: 0, width: W, height: H))
        for i in 0..<W * H { px[i] = SIMD4(Float(b[i * 4]), Float(b[i * 4 + 1]), Float(b[i * 4 + 2]), Float(b[i * 4 + 3])) / 255 }
    }
    var cg: CGImage {
        var b = px.flatMap { v in (0..<4).map { UInt8(max(0, min(255, (v[$0] * 255).rounded()))) } }
        return CGContext(data: &b, width: w, height: h, bitsPerComponent: 8, bytesPerRow: w * 4, space: sRGB,
                         bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!.makeImage()!
    }
}

let skin = CommandLine.arguments.count > 3 ? CommandLine.arguments[3] : nil
let skinned = ["plate", "plate_cover", "face_sleep", "mouse", "arm_left", "arm_mouse", "arm_right", "arm_right_in"]

func write(_ cg: CGImage, _ name: String) {
    if skin != nil, !skinned.contains(name) { return }
    try! NSBitmapImageRep(cgImage: cg).representation(using: .png, properties: [:])!
        .write(to: URL(fileURLWithPath: CommandLine.arguments[2] + "/\(name).png"))
}

/// 闭合 Catmull-Rom 平滑轮廓的覆盖率；`grow` 外扩，`blur` 羽化。
func mask(_ p: [P], grow: CGFloat = 0, blur: Int = 0, smooth: Bool = true) -> [Float] {
    let path = CGMutablePath(), n = p.count
    path.move(to: p[0])
    for i in 0..<n {
        let a = p[(i + n - 1) % n], b = p[i], c = p[(i + 1) % n], d = p[(i + 2) % n]
        if smooth {
            path.addCurve(to: c, control1: P(x: b.x + (c.x - a.x) / 6, y: b.y + (c.y - a.y) / 6),
                          control2: P(x: c.x - (d.x - b.x) / 6, y: c.y - (d.y - b.y) / 6))
        } else { path.addLine(to: c) }
    }
    path.closeSubpath()
    var b = [UInt8](repeating: 0, count: W * H)
    let ctx = CGContext(data: &b, width: W, height: H, bitsPerComponent: 8, bytesPerRow: W, space: CGColorSpaceCreateDeviceGray(), bitmapInfo: 0)!
    ctx.translateBy(x: 0, y: CGFloat(H)); ctx.scaleBy(x: 1, y: -1)
    ctx.setFillColor(gray: 1, alpha: 1); ctx.setStrokeColor(gray: 1, alpha: 1); ctx.setLineJoin(.round)
    ctx.addPath(path); ctx.fillPath()
    if grow > 0 { ctx.setLineWidth(grow * 2); ctx.addPath(path); ctx.strokePath() }
    var m = b.map { Float($0) / 255 }
    for _ in 0..<(blur > 0 ? 2 : 0) {  // 两次盒式模糊
        for horizontal in [true, false] {
            let s = m
            for y in 0..<H { for x in 0..<W {
                var t: Float = 0
                for k in -blur...blur { t += s[min(max(y + (horizontal ? 0 : k), 0), H - 1) * W + min(max(x + (horizontal ? k : 0), 0), W - 1)] }
                m[y * W + x] = t / Float(2 * blur + 1)
            } }
        }
    }
    return m
}

func mix(_ a: Image, _ b: Image, _ m: [Float]) -> Image {
    var o = a
    for i in 0..<W * H { o.px[i] = a.px[i] * (1 - m[i]) + b.px[i] * m[i] }
    return o
}
func cut(_ a: Image, _ m: [Float]) -> Image {
    var o = a
    for i in 0..<W * H { o.px[i] = a.px[i] * m[i] }
    return o
}

func luma(_ v: SIMD4<Float>) -> Float { (v.x + v.y + v.z) / 3 }

/// 用扩散填补挖空区域，只取亮度在 `pad` 内的桌垫作边界（忽略描边与透明处），并补上被挡住的桌垫上沿。
func inpaint(_ a: Image, _ m: [Float], pad: ClosedRange<Float> = 0.62...1,
             edge: (P, P) = (P(x: 752, y: 653), P(x: 862, y: 676)), ink: SIMD4<Float> = SIMD4(0.24, 0.16, 0.13, 1)) -> Image {
    var o = a
    func isPad(_ i: Int) -> Bool { a.px[i].w > 0.95 && pad.contains(luma(a.px[i])) }
    let idx = (0..<W * H).filter { m[$0] > 0 }
    let near = idx.map { i in [i - 1, i + 1, i - W, i + W].filter { m[$0] > 0 || isPad($0) } }
    var mean = SIMD4<Float>.zero, n: Float = 0
    for l in near { for j in l where m[j] == 0 { mean += a.px[j]; n += 1 } }
    for i in idx { o.px[i] = mean / n }
    for _ in 0..<1500 {
        for (i, l) in zip(idx, near) where !l.isEmpty {
            var t = SIMD4<Float>.zero
            for j in l { t += o.px[j] }
            o.px[i] += (t / Float(l.count) - o.px[i]) * 1.5
        }
    }
    let (a0, a1) = edge
    let line = mask([a0, a1, P(x: a1.x, y: a1.y + 3.5), P(x: a0.x, y: a0.y + 3.5)], blur: 1, smooth: false)
    for i in idx {
        let x = CGFloat(i % W), y = CGFloat(i / W)
        if y < a0.y + (x - a0.x) * (a1.y - a0.y) / (a1.x - a0.x) { o.px[i] = .zero }
    }
    for i in 0..<W * H where line[i] > 0 { o.px[i] = o.px[i] * (1 - line[i]) + ink * line[i] }
    return o
}

/// 以 pivot→tip 为轴转成竖直：pivot 在上、tip 在下。
func upright(_ a: Image, pivot: P, tip: P, size: CGSize, pivotAt: P) -> Image {
    var o = Image(); o.w = Int(size.width); o.h = Int(size.height); o.px = Array(repeating: .zero, count: o.w * o.h)
    let l = hypot(tip.x - pivot.x, tip.y - pivot.y), v = P(x: (tip.x - pivot.x) / l, y: (tip.y - pivot.y) / l), u = P(x: v.y, y: -v.x)
    for y in 0..<o.h { for x in 0..<o.w {
        let dx = CGFloat(x) - pivotAt.x, dy = CGFloat(y) - pivotAt.y
        o[x, y] = a.sample(P(x: pivot.x + u.x * dx + v.x * dy, y: pivot.y + u.y * dx + v.y * dy))
    } }
    return o
}

let dir = CommandLine.arguments[1]
func frame(_ n: String) -> Image { Image(dir + "/\(n).png") }
let idle = frame("idle"), cover = frame("cover_eyes"), typing = frame("type_both"), sleep = frame("sleep")

// 轮廓
let armL = pts([443,462, 428,480, 402,488, 376,500, 352,519, 337,545, 330,575, 329,598, 335,616, 349,628, 368,634, 386,630, 400,624, 411,613, 417,600, 420,588, 428,577, 446,571, 462,562, 465,545, 466,530, 466,500, 462,478])
let idleL = pts([440,462, 428,486, 408,505, 380,521, 348,535, 322,548, 306,564, 303,582, 310,601, 326,612, 346,617, 372,615, 394,608, 418,600, 474,598, 472,570, 468,530, 466,495, 460,474])
let armM = pts([740,528, 743,548, 748,562, 748,578, 741,594, 731,611, 722,629, 717,650, 716,673, 720,689, 730,701, 742,703, 757,710, 773,708, 786,704, 800,704, 809,692, 816,675, 824,661, 842,643, 859,624, 867,606, 871,590, 873,570, 869,548, 860,526, 840,510, 800,502, 762,508])
let armR = pts([742,508, 725,533, 700,544, 672,552, 649,559, 620,575, 590,600, 571,624, 566,646, 573,664, 587,677, 600,684, 613,686, 633,685, 651,677, 662,661, 680,648, 696,643, 730,644, 762,643, 801,638, 826,632, 846,619, 861,602, 868,578, 871,555, 864,530, 842,510, 800,498])
let mouse = pts([689,742, 691,725, 701,706, 724,679, 749,663, 775,656, 800,656, 823,661, 838,676, 844,695, 841,713, 828,731, 806,751, 781,768, 756,777, 730,777, 706,770, 694,758])
let mouseHole = pts([688,745, 690,725, 701,706, 723,678, 748,660, 775,653, 802,652, 828,656, 848,670, 858,692, 856,716, 842,738, 818,760, 788,778, 758,787, 728,786, 702,778, 688,762])

// 桌垫向右延展，给鼠标留出活动空间；皮肤的鼠标更宽，多留一些
let extend = skin == nil ? P(x: 84, y: 20) : P(x: 108, y: 26)
func widenPad(_ a: Image) -> Image {
    let s0 = P(x: 858, y: 655), s1 = P(x: 690, y: 862)  // 接缝
    func side(_ p: P) -> CGFloat { (s1.x - s0.x) * (p.y - s0.y) - (s1.y - s0.y) * (p.x - s0.x) }  // <0 在右
    func movable(_ x: Int, _ y: Int) -> Bool {
        let p = P(x: x, y: y)
        return side(p) < 0 && Double(y) > 0.195 * Double(x - 840) + 660 && x < 930
    }
    var o = a
    for y in 640..<H { for x in 680..<W where movable(x, y) { o[x, y] = .zero } }
    let den = (s1.x - s0.x) * extend.y - (s1.y - s0.y) * extend.x
    for y in 640..<H { for x in 680..<1060 {
        let p = P(x: x, y: y), q = P(x: p.x - extend.x, y: p.y - extend.y)
        var v = SIMD4<Float>.zero
        if side(q) < 0 {
            if movable(Int(q.x), Int(q.y)) { v = a[Int(q.x), Int(q.y)] }
        } else if side(p) < 0 {
            let t = side(p) / den  // 沿延展方向取接缝处的颜色
            let s = P(x: p.x - extend.x * t, y: p.y - extend.y * t)
            if s.y > 0.195 * (s.x - 840) + 660 { v = a.sample(s) }
        }
        if v.w > 0 { o[x, y] = v + o[x, y] * (1 - v.w) }
    } }
    return o
}

// 去掉鼠标和双臂的底图
let desk = mask(pts([670,640, 915,640, 915,870, 670,870]), smooth: false)
let hole = mask(mouseHole)
let mask0 = zip(mask(armL, grow: 4), mask(idleL, grow: 4)).map { max($0, $1) }
/// 去掉左侧悬空的桌沿线。
func trim(_ a: Image) -> Image {
    var o = a
    for y in 558..<610 { for x in 260..<462 where Double(y) < 0.18 * Double(x - 340) + 583 && Double(y) > 0.18 * Double(x - 300) + 558 { o[x, y] = .zero } }
    return o
}
func base(_ f: Image) -> Image {
    var o = mix(f, cover, mask(armM, grow: 3, blur: 1))
    o = mix(o, cover, mask0)
    o = trim(o)
    for y in 440..<640 { for x in 300..<500 where mask0[y * W + x] > 0 {
        if x < 461, y < 572 { o[x, y] = .zero }                                  // cover 里抬起的左臂
        if x >= 458 && y < 548 || hypot(Double(x) - 458, Double(y) - 504) < 37 { o[x, y] = f[x, y] }  // 肩根保持不动
    } }
    var below = desk
    for y in 640..<H { for x in 660..<W where Double(y) <= 0.195 * Double(x - 840) + 656 { below[y * W + x] = 0 } }
    o = mix(o, cover, below)
    return widenPad(inpaint(o, hole))
}

/// 尾巴所在处：身体右缘之外、桌沿线之上。
func inTail(_ x: Int, _ y: Int) -> Bool {
    let edge: [(Double, Double)] = [(280, 780), (385, 820), (440, 854), (500, 884), (550, 902), (600, 912), (660, 915), (690, 916)]
    guard y >= 280, y < 690, Double(y) < 659 + 0.176 * Double(x - 910) else { return false }
    let i = edge.lastIndex { $0.0 <= Double(y) }!, a = edge[i], b = edge[min(i + 1, edge.count - 1)]
    let t = b.0 > a.0 ? (Double(y) - a.0) / (b.0 - a.0) : 0
    return Double(x) > a.1 + (b.1 - a.1) * t + (y < 604 ? 16 : 6)
}
func tailless(_ a: Image) -> Image {
    var o = a
    for y in 280..<690 { for x in 780..<1060 where inTail(x, y) { o[x, y] = .zero } }
    return o
}
var plate = tailless(base(idle)), plateCover = tailless(widenPad(inpaint(trim(cover), hole)))
var asleep = cut(sleep, mask(pts([420,240, 560,220, 700,240, 730,330, 690,430, 560,446, 440,420, 412,330]), blur: 4))
var mouseImage = cut(cover, mask(mouse)).cg.cropping(to: CGRect(x: 680, y: 648, width: 176, height: 136))!

// 皮肤：猫身与原素材逐像素对齐，所以只换耳机和桌面
if let skin {
    /// 从透明处和 `seeds` 向内清掉残留的浅色背景，边缘按深色描边重算透明度。
    func knock(_ a: Image, seeds: [P] = []) -> Image {
        let paper: Float = 0.8, limit: Float = 0.5
        var out = a.px.map { $0.w < 0.5 }, queue = (0..<W * H).filter { out[$0] }
        for s in seeds { out[Int(s.y) * W + Int(s.x)] = true; queue.append(Int(s.y) * W + Int(s.x)) }
        while let i = queue.popLast() {
            let x = i % W
            for j in [x > 0 ? i - 1 : i, x < W - 1 ? i + 1 : i, max(i - W, 0), min(i + W, W * H - 1)] where !out[j] && luma(a.px[j]) > limit {
                out[j] = true
                queue.append(j)
            }
        }
        var seen = out  // 孤立的小碎点也算背景
        for start in 0..<W * H where !seen[start] {
            var island = [start], k = 0
            seen[start] = true
            while k < island.count {
                let i = island[k]
                k += 1
                for j in [i - 1, i + 1, i - W, i + W] where j >= 0 && j < W * H && !seen[j] {
                    seen[j] = true
                    island.append(j)
                }
            }
            if island.count < 400 { island.forEach { out[$0] = true } }
        }
        var o = a
        for y in 1..<H - 1 { for x in 1..<W - 1 {
            let i = y * W + x
            if out[i], out[i - 1], out[i + 1], out[i - W], out[i + W] { o.px[i] = .zero; continue }
            let near = [i - 1, i + 1, i - W, i + W, i - W - 1, i - W + 1, i + W - 1, i + W + 1]
            let rim = out[i] ? near.contains { !out[$0] } : near.contains { out[$0] }
            guard rim else { if out[i] { o.px[i] = .zero }; continue }
            let dark = (near + [i]).filter { a.px[$0].w > 0.5 }.map { a.px[$0] }.min { luma($0) < luma($1) } ?? .zero
            let alpha = a.px[i].w < 0.5 ? 0 : max(0, min(1, (paper - luma(a.px[i])) / max(paper - luma(dark), 0.1)))
            o.px[i] = dark * alpha
        } }
        return o
    }
    let holes = [P(x: 560, y: 120), P(x: 752, y: 208)]  // 头梁下、右耳旁的空隙
    let headset = FileManager.default.fileExists(atPath: skin + "/idle.png")
    let skinCover = knock(Image(skin + "/cover_eyes.png"), seeds: headset ? holes : [])

    // 耳机：猫身之外垫在下面，右耳罩盖在上面；捂眼时左臂又在耳罩前
    let rim: [Double] = [810,375, 820,354, 826,334, 829,312, 829,292, 826,278, 820,262, 812,250, 803,240, 797,228, 795,215, 774,222, 757,222, 742,227, 729,237, 718,252, 709,273, 703,297, 700,320]
    let cup = mask(pts(rim + [701,345, 706,368, 714,385, 725,397, 740,403, 760,404, 780,400, 797,389]), blur: 1)
    let cupCover = mask(pts(rim + [700,342, 718,357, 736,374.5, 752,392.5, 764,402, 780,400, 797,389]), blur: 1)
    func wear(_ cat: Image, _ new: Image, _ cup: [Float]) -> Image {
        var o = cat
        for y in 0..<420 { for x in 360..<850 {
            let i = y * W + x
            o.px[i] = cat.px[i] + new.px[i] * (1 - cat.px[i].w)
            o.px[i] = o.px[i] * (1 - cup[i]) + new.px[i] * cup[i]
        } }
        return o
    }

    // 桌面：猫身下缘以下整块换掉，两个姿势共用
    let mouse = pts([685,752, 687,738, 695,722, 706,703, 716,694, 726,684, 742,672, 760,663, 780,659, 800,658, 815,662, 828,669, 838,678, 843,687, 849,702, 855,715, 859,725, 858,732, 850,741, 836,753, 822,762, 806,765, 790,764, 778,768, 765,776, 750,784, 738,787, 724,786, 708,780, 697,770, 688,762])
    let hole = mask(pts([678,760, 680,736, 695,710, 713,688, 738,667, 762,657, 790,652, 818,655, 846,671, 858,692, 868,712, 869,722, 862,730, 848,742, 834,755, 820,768, 804,784, 776,796, 744,804, 714,804, 688,790]))
    func onDesk(_ x: Int, _ y: Int) -> Bool { Double(y) > (x < 455 ? 579 + 0.16 * Double(x - 330) : 634 + 0.145 * Double(x - 660)) }
    let light = luma(skinCover[640, 815]) > 0.5  // 桌垫是浅色还是深色
    let empty = widenPad(inpaint(skinCover, hole, pad: light ? 0.62...1 : 0.22...0.36, edge: (P(x: 740, y: 652), P(x: 870, y: 674)), ink: SIMD4(0.13, 0.11, 0.09, 1)))
    func furnish(_ cat: Image) -> Image {
        var o = cat
        for y in 540..<H { for x in 0..<W {
            if onDesk(x, y) { o[x, y] = empty[x, y]; continue }
            if x < 455 { continue }
            let t = Float(max(0, min(1, (Double(y) - 614 - 0.145 * Double(x - 660)) / 6)))  // 腹部下缘用 cover，没有浅色桌沿
            o[x, y] = o[x, y] * (1 - t) + plateCover[x, y] * t
        } }
        return o
    }
    if headset {
        plate = wear(plate, knock(Image(skin + "/idle.png"), seeds: holes), cup)
        plateCover = wear(plateCover, skinCover, cupCover)
        asleep = cut(asleep, cup.map { 1 - $0 })
    }
    plate = furnish(plate)
    plateCover = furnish(plateCover)
    mouseImage = cut(skinCover, mask(mouse)).cg.cropping(to: CGRect(x: 684, y: 655, width: 176, height: 136))!
}

/// 手臂描边外残留的浅色细边，衬在深色桌面上会显出来：从 `top` 行往下向内削，碰到描边为止。
func trimmed(_ a: Image, top: Int) -> Image {
    guard skin != nil else { return a }
    var o = a
    for _ in 0..<4 {
        let s = o
        for y in top..<o.h { for x in 0..<o.w where s[x, y].w > 0 {
            let v = s[x, y], open = [s[x - 1, y], s[x + 1, y], s[x, y - 1], s[x, y + 1]].contains { $0.w < 0.05 }
            if open, v.w < 0.3 || luma(v) / v.w > 0.42 { o[x, y] = .zero }
        } }
    }
    return o
}

write(plate.cg, "plate")
write(plateCover.cg, "plate_cover")

// 眯眼：把原画的眼睛沿竖向压扁，上眼皮压得多、内眼角略低，四周平滑过渡
func squint(_ amount: CGFloat, tilt: CGFloat) -> Image {
    var o = Image()
    for (c, r, inner) in [(P(x: 480, y: 320), P(x: 66, y: 62), CGFloat(1)), (P(x: 606, y: 325), P(x: 74, y: 64), CGFloat(-1))] {
        for y in Int(c.y - r.y)...Int(c.y + r.y) { for x in Int(c.x - r.x)...Int(c.x + r.x) {
            let nx = (CGFloat(x) - c.x) / r.x, ny = (CGFloat(y) - c.y) / r.y, d = nx * nx + ny * ny
            guard d < 1 else { continue }
            let w = (1 - d) * (1 - d), base = c.y + 10  // 下眼皮附近不动
            let dy = CGFloat(y) - base
            let source = base + dy * (1 + amount * w * (dy < 0 ? 1 : 0.45)) - tilt * inner * nx * r.x * w
            o[x, y] = idle.sample(P(x: CGFloat(x), y: source)) * Float(min(1, (1 - d) * 8))
        } }
    }
    return o
}
let eyes = CGRect(x: 400, y: 250, width: 290, height: 150)
write(squint(0.38, tilt: -0.02).cg.cropping(to: eyes)!, "eyes_focus")
write(squint(0.95, tilt: 0.05).cg.cropping(to: eyes)!, "eyes_tense")

// 尾巴：根部镜像延伸进身体，摆动时不露缝
var tail = Image()
for y in 280..<690 { for x in 780..<1060 where inTail(x, y) { tail[x, y] = idle[x, y] } }
for y in 606..<666 { for x in 878..<924 { tail[x, y] = tail[924 + (924 - x), y] } }
write(tail.cg.cropping(to: CGRect(x: 860, y: 290, width: 240, height: 400))!, "tail")
write(asleep.cg.cropping(to: CGRect(x: 400, y: 200, width: 360, height: 272))!, "face_sleep")
write(mouseImage, "mouse")

// 肩头：内收时叠在右臂根部之上，朝手臂一侧羽化
var cap = cut(idle, mask(idleL, grow: 2))
for y in 0..<H { for x in 0..<W where cap[x, y].w > 0 {
    cap[x, y] *= Float(max(0, min(1, (50 - hypot(Double(x) - 462, Double(y) - 496)) / 14)))
} }
write(cap.cg.cropping(to: CGRect(x: 396, y: 440, width: 120, height: 120))!, "shoulder")

// 手臂：肩端羽化，便于旋转衔接
func arm(_ f: Image, _ outline: [P], pivot: P, tip: P, fade: CGFloat, _ name: String) {
    let m = mask(outline)
    var o = cut(f, m)
    let l = hypot(tip.x - pivot.x, tip.y - pivot.y)
    for y in 0..<H { for x in 0..<W where o[x, y].w > 0 {  // 越过肩点后渐隐
        let along = ((CGFloat(x) - pivot.x) * (tip.x - pivot.x) + (CGFloat(y) - pivot.y) * (tip.y - pivot.y)) / l
        o[x, y] *= Float(max(0, min(1, (along + fade) / (fade * 0.7))))
    } }
    write(trimmed(upright(o, pivot: pivot, tip: tip, size: CGSize(width: 240, height: 400), pivotAt: P(x: 120, y: 80)), top: 150).cg, name)
    print(name, "length", l)
}

// 右臂较短、活动角度大：沿手臂中线展平成直条，运行时再按曲线弯回去
do {
    let a = P(x: 480, y: 528), b = P(x: 420, y: 526), c = P(x: 374, y: 562), d = P(x: 369, y: 637)
    func bezier(_ t: CGFloat) -> (P, P) {
        let u = 1 - t
        func f(_ k: KeyPath<P, CGFloat>) -> (CGFloat, CGFloat) {
            (u * u * u * a[keyPath: k] + 3 * u * u * t * b[keyPath: k] + 3 * u * t * t * c[keyPath: k] + t * t * t * d[keyPath: k],
             3 * u * u * (b[keyPath: k] - a[keyPath: k]) + 6 * u * t * (c[keyPath: k] - b[keyPath: k]) + 3 * t * t * (d[keyPath: k] - c[keyPath: k]))
        }
        let x = f(\.x), y = f(\.y), l = hypot(x.1, y.1)
        return (P(x: x.0, y: y.0), P(x: x.1 / l, y: y.1 / l))
    }
    var table: [(CGFloat, CGFloat)] = [(0, 0)]  // 弧长 → t
    for i in 1...1000 {
        let t = CGFloat(i) / 1000, p = bezier(t).0, q = bezier(t - 0.001).0
        table.append((table.last!.0 + hypot(p.x - q.x, p.y - q.y), t))
    }
    let full = pts([443,462, 428,480, 402,488, 376,500, 352,519, 337,545, 330,575, 329,598, 335,616, 349,628, 368,634, 386,630, 400,624, 411,613, 416,600, 418,588, 440,585, 470,583, 496,580, 498,540, 490,500, 462,476])
    let src = cut(typing, mask(full))
    var o = Image(); o.w = 140; o.h = Int(table.last!.0); o.px = Array(repeating: .zero, count: o.w * o.h)
    for y in 0..<o.h {
        let t = table.first { $0.0 >= CGFloat(y) }!.1, (p, v) = bezier(t)
        for x in 0..<o.w {
            let n = CGFloat(x) - 70
            o[x, y] = src.sample(P(x: p.x + v.y * n, y: p.y - v.x * n)) * Float(min(1, CGFloat(y) / 20))
        }
    }
    write(trimmed(o, top: 80).cg, "arm_right")
    // 内收版：修掉腋下，内侧与胸毛羽化衔接
    for y in 0..<o.h - 74 { for x in 0..<o.w {
        let limit = 104 + max(0, Float(y) - Float(o.h - 100)) * 0.15
        o[x, y] *= max(0, min(1, (limit - Float(x)) / 14))
    } }
    write(trimmed(o, top: 80).cg, "arm_right_in")
    print("arm_right length", o.h)
}
arm(typing, armR, pivot: P(x: 815, y: 572), tip: P(x: 610, y: 682), fade: 50, "arm_left")
arm(idle, armM, pivot: P(x: 804, y: 560), tip: P(x: 765, y: 706), fade: 52, "arm_mouse")

// 应用图标：头像特写
let icon = CGContext(data: nil, width: 1024, height: 1024, bitsPerComponent: 8, bytesPerRow: 0, space: sRGB, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
icon.addPath(CGPath(roundedRect: CGRect(x: 100, y: 100, width: 824, height: 824), cornerWidth: 186, cornerHeight: 186, transform: nil))
icon.clip()
icon.setFillColor(CGColor(srgbRed: 0.93, green: 0.87, blue: 0.77, alpha: 1))
icon.fill(CGRect(x: 0, y: 0, width: 1024, height: 1024))
icon.draw(idle.cg, in: CGRect(x: 512 - 590 * 1.75, y: 512 - (896 - 305) * 1.75, width: 1200 * 1.75, height: 896 * 1.75))
write(icon.makeImage()!, "icon")
