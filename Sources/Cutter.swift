import CoreGraphics
import simd

/// 把整帧图拆成活动部件。坐标为 1200×896 像素，左上原点；轮廓按模板描，所以换猫时姿势要照模板画。
enum Cutter {
    typealias P = CGPoint
    static let W = 1200, H = 896
    static let sRGB = CGColorSpace(name: CGColorSpace.sRGB)!
    static func pts(_ a: [Double]) -> [P] { stride(from: 0, to: a.count, by: 2).map { P(x: a[$0], y: a[$0 + 1]) } }

    /// 预乘 RGBA 浮点图。
    struct Raster {
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
        init(_ cg: CGImage) {
            var b = [UInt8](repeating: 0, count: W * H * 4)
            CGContext(data: &b, width: W, height: H, bitsPerComponent: 8, bytesPerRow: W * 4, space: sRGB,
                      bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!.draw(cg, in: CGRect(x: 0, y: 0, width: W, height: H))
            for i in 0..<W * H { px[i] = SIMD4(Float(b[i * 4]), Float(b[i * 4 + 1]), Float(b[i * 4 + 2]), Float(b[i * 4 + 3])) / 255 }
        }
        init(width: Int, height: Int) { (w, h, px) = (width, height, Array(repeating: .zero, count: width * height)) }
        var cg: CGImage {
            var b = px.flatMap { v in (0..<4).map { UInt8(max(0, min(255, (v[$0] * 255).rounded()))) } }
            return CGContext(data: &b, width: w, height: h, bitsPerComponent: 8, bytesPerRow: w * 4, space: sRGB,
                             bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!.makeImage()!
        }
    }

    /// 闭合 Catmull-Rom 平滑轮廓的覆盖率；`grow` 外扩，`blur` 羽化。
    static func mask(_ p: [P], grow: CGFloat = 0, blur: Int = 0, smooth: Bool = true) -> [Float] {
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

    static func mix(_ a: Raster, _ b: Raster, _ m: [Float]) -> Raster {
        var o = a
        for i in 0..<W * H { o.px[i] = a.px[i] * (1 - m[i]) + b.px[i] * m[i] }
        return o
    }
    static func cut(_ a: Raster, _ m: [Float]) -> Raster {
        var o = a
        for i in 0..<W * H { o.px[i] = a.px[i] * m[i] }
        return o
    }

    static func luma(_ v: SIMD4<Float>) -> Float { (v.x + v.y + v.z) / 3 }

    /// 用扩散填补挖空区域，只取亮度在 `pad` 内的桌垫作边界（忽略描边与透明处），并补上被挡住的桌垫上沿。
    static func inpaint(_ a: Raster, _ m: [Float], pad: ClosedRange<Float> = 0.62...1,
                 edge: (P, P) = (P(x: 752, y: 653), P(x: 862, y: 676)), ink: SIMD4<Float> = SIMD4(0.24, 0.16, 0.13, 1)) -> Raster {
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
    static func upright(_ a: Raster, pivot: P, tip: P, size: CGSize, pivotAt: P) -> Raster {
        var o = Raster(width: Int(size.width), height: Int(size.height))
        let l = hypot(tip.x - pivot.x, tip.y - pivot.y), v = P(x: (tip.x - pivot.x) / l, y: (tip.y - pivot.y) / l), u = P(x: v.y, y: -v.x)
        for y in 0..<o.h { for x in 0..<o.w {
            let dx = CGFloat(x) - pivotAt.x, dy = CGFloat(y) - pivotAt.y
            o[x, y] = a.sample(P(x: pivot.x + u.x * dx + v.x * dy, y: pivot.y + u.y * dx + v.y * dy))
        } }
        return o
    }

    // MARK: 模板轮廓

    static let armL = pts([443,462, 428,480, 402,488, 376,500, 352,519, 337,545, 330,575, 329,598, 335,616, 349,628, 368,634, 386,630, 400,624, 411,613, 417,600, 420,588, 428,577, 446,571, 462,562, 465,545, 466,530, 466,500, 462,478])
    static let idleL = pts([440,462, 428,486, 408,505, 380,521, 348,535, 322,548, 306,564, 303,582, 310,601, 326,612, 346,617, 372,615, 394,608, 418,600, 474,598, 472,570, 468,530, 466,495, 460,474])
    static let armM = pts([740,528, 743,548, 748,562, 748,578, 741,594, 731,611, 722,629, 717,650, 716,673, 720,689, 730,701, 742,703, 757,710, 773,708, 786,704, 800,704, 809,692, 816,675, 824,661, 842,643, 859,624, 867,606, 871,590, 873,570, 869,548, 860,526, 840,510, 800,502, 762,508])
    static let armR = pts([742,508, 725,533, 700,544, 672,552, 649,559, 620,575, 590,600, 571,624, 566,646, 573,664, 587,677, 600,684, 613,686, 633,685, 651,677, 662,661, 680,648, 696,643, 730,644, 762,643, 801,638, 826,632, 846,619, 861,602, 868,578, 871,555, 864,530, 842,510, 800,498])
    static let mouse = pts([689,742, 691,725, 701,706, 724,679, 749,663, 775,656, 800,656, 823,661, 838,676, 844,695, 841,713, 828,731, 806,751, 781,768, 756,777, 730,777, 706,770, 694,758])
    static let mouseHole = pts([688,745, 690,725, 701,706, 723,678, 748,660, 775,653, 802,652, 828,656, 848,670, 858,692, 856,716, 842,738, 818,760, 788,778, 758,787, 728,786, 702,778, 688,762])

    /// 右臂搭着的画法里，盖在手臂根部上面的胸毛和鬃毛。
    static let ruff = mask(pts([405,430, 408,470, 424,484, 430,496, 437,503, 446,497, 450,518, 461,538, 478,556, 494,574, 520,585, 520,430]), blur: 1, smooth: false)

    /// 捂眼时抬起的双臂，配件要垫在它们下面。
    static let raisedArms: [Float] = {
        let right = mask(pts([584,287, 571,297, 558,304, 553,318, 556,333, 556,346, 562,358, 573,369, 590,378, 608,386, 621,397, 632,414, 642,431, 652,447, 662,465, 672,483, 682,499, 693,515, 705,530, 720,544, 740,556, 762,562, 805,558, 862,562, 894,520, 862,450, 803,405, 785,407, 764,404, 752,398, 742,387, 733,377, 723,366, 710,353, 694,337, 678,320, 660,307, 645,299, 628,291, 612,286, 599,284]), grow: 1)
        let left = mask(pts([498,294, 506,305, 508,318, 506,332, 505,345, 501,356, 490,364, 476,371, 467,380, 461,390, 457,403, 457,420, 458,440, 461,460, 462,480, 465,500, 466,530, 464,557, 440,556, 415,549, 395,534, 377,513, 364,488, 359,460, 361,430, 366,405, 375,380, 385,350, 394,334, 405,317, 420,302, 440,289, 460,280, 478,279, 490,285]), grow: 1)
        return zip(right, left).map { max($0, $1) }
    }()

    /// 桌沿：以下归桌面，以上归猫。
    static func onDesk(_ x: Int, _ y: Int) -> Bool { Double(y) > (x < 455 ? 579 + 0.16 * Double(x - 330) : 634 + 0.145 * Double(x - 660)) }

    /// 桌垫向右延展，给鼠标留出活动空间。
    static func widenPad(_ a: Raster, by extend: P) -> Raster {
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

    /// 去掉左侧悬空的桌沿线。
    static func trim(_ a: Raster) -> Raster {
        var o = a
        for y in 558..<610 { for x in 260..<462 where Double(y) < 0.18 * Double(x - 340) + 583 && Double(y) > 0.18 * Double(x - 300) + 558 { o[x, y] = .zero } }
        return o
    }

    /// 尾巴所在处：身体右缘之外、桌沿线之上。
    static func inTail(_ x: Int, _ y: Int) -> Bool {
        let edge: [(Double, Double)] = [(280, 780), (385, 820), (440, 854), (500, 884), (550, 902), (600, 912), (660, 915), (690, 916)]
        guard y >= 280, y < 690, Double(y) < 659 + 0.176 * Double(x - 910) else { return false }
        let i = edge.lastIndex { $0.0 <= Double(y) }!, a = edge[i], b = edge[min(i + 1, edge.count - 1)]
        let t = b.0 > a.0 ? (Double(y) - a.0) / (b.0 - a.0) : 0
        return Double(x) > a.1 + (b.1 - a.1) * t + (y < 604 ? 16 : 6)
    }
    static func tailless(_ a: Raster) -> Raster {
        var o = a
        for y in 280..<690 { for x in 780..<1100 where inTail(x, y) { o[x, y] = .zero } }
        return o
    }

    /// 手臂描边外残留的浅色细边，衬在深色桌面上会显出来：从 `top` 行往下向内削，碰到描边为止。
    static func trimmed(_ a: Raster, top: Int) -> Raster {
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

    // MARK: 拆猫

    /// 四张整帧图。
    struct Cat {
        let idle: Raster, typing: Raster, cover: Raster, sleep: Raster

        typealias P = Cutter.P

        /// 双臂所在处换成桌面或透明，肩根保留。
        func armless(_ f: Raster) -> Raster {
            let mask0 = zip(mask(armL, grow: 4), mask(idleL, grow: 4)).map { max($0, $1) }
            let resting = rightArmRests
            var o = mix(f, cover, mask(armM, grow: 3, blur: 1))
            o = mix(o, cover, mask0)
            o = trim(o)
            for y in 440..<640 { for x in 300..<500 where mask0[y * W + x] > 0 {
                if x < 461, y < 572 { o[x, y] = .zero }                                  // cover 里抬起的左臂
                if x >= 458 && y < 548 || hypot(Double(x) - 458, Double(y) - 504) < 37, !resting { o[x, y] = f[x, y] }  // 肩根保持不动
                if resting { o[x, y] = o[x, y] * (1 - ruff[y * W + x]) + f[x, y] * ruff[y * W + x] }
            } }
            return o
        }

        /// 闭眼的脸，边缘羽化。
        var asleep: Raster { cut(sleep, mask(pts([420,240, 560,220, 700,240, 730,330, 690,430, 560,446, 440,420, 412,330]), blur: 4)) }

        /// 眯眼：把原画的眼睛沿竖向压扁，上眼皮压得多、内眼角略低，四周平滑过渡
        func squint(_ amount: CGFloat, tilt: CGFloat) -> Raster {
            var o = Raster()
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

        /// 尾巴：取更完整的那一帧；根部镜像延伸进身体，摆动时不露缝。
        var tail: Raster {
            func area(_ f: Raster) -> Int {
                var n = 0
                for y in 280..<690 { for x in 780..<1100 where inTail(x, y) && f[x, y].w > 0.5 { n += 1 } }
                return n
            }
            let f = area(typing) > area(idle) * 102 / 100 ? typing : idle
            var o = Raster()
            for y in 280..<690 { for x in 780..<1100 where inTail(x, y) { o[x, y] = f[x, y] } }
            for y in 606..<666 { for x in 878..<924 { o[x, y] = o[924 + (924 - x), y] } }
            return o
        }

        /// 肩头：叠在右臂根部之上。一般只在内收时出现，朝手臂一侧羽化；右臂搭着的画法里是一直盖着的鬃毛。
        var shoulder: Raster {
            if rightArmRests { return cut(idle, ruff) }
            var o = cut(idle, mask(idleL, grow: 2))
            for y in 0..<H { for x in 0..<W where o[x, y].w > 0 {
                o[x, y] *= Float(max(0, min(1, (50 - hypot(Double(x) - 462, Double(y) - 496)) / 14)))
            } }
            return o
        }

        /// `type_both` 里右臂画在敲键的位置，还是和 `idle` 一样搭着。
        var rightArmRests: Bool {
            let m = zip(mask(armL), mask(idleL)).map { max($0, $1) }
            var moved = 0, total = 0
            for y in 470..<630 { for x in 300..<430 where m[y * W + x] > 0.5 {
                let d = typing[x, y] - idle[x, y]
                total += 1
                if max(d.max(), -d.min()) > 0.16 { moved += 1 }
            } }
            return moved * 5 < total
        }

        /// 右臂较短、活动角度大：沿手臂中线展平成直条，运行时再按曲线弯回去。内收版修掉腋下，内侧与胸毛羽化衔接。
        var rightArm: (out: Raster, tucked: Raster) {
            let resting = rightArmRests
            let (a, b, c, d) = resting ? (P(x: 480, y: 545), P(x: 425, y: 535), P(x: 347, y: 535), P(x: 345, y: 614))
                : (P(x: 480, y: 528), P(x: 420, y: 526), P(x: 374, y: 562), P(x: 369, y: 637))
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
            let full = resting
                ? pts([414,499, 405,504, 390,512, 370,522, 352,533, 335,540, 324,547, 314,558, 308,570, 307,582, 310,592, 318,601, 328,609, 340,613, 352,612, 358,610, 368,612, 380,610, 392,604, 402,599, 412,594, 430,593, 450,594, 470,590, 486,588, 486,566, 470,552, 456,540, 446,526, 436,514, 424,506])
                : pts([443,462, 428,480, 402,488, 376,500, 352,519, 337,545, 330,575, 329,598, 335,616, 349,628, 368,634, 386,630, 400,624, 411,613, 416,600, 418,588, 440,585, 470,583, 496,580, 498,540, 490,500, 462,476])
            let src = cut(typing, mask(full))
            var o = Raster(width: 140, height: Int(table.last!.0))
            for y in 0..<o.h {
                let (p, v) = bezier(table.first { $0.0 >= CGFloat(y) }!.1)
                for x in 0..<o.w {
                    let n = CGFloat(x) - 70
                    o[x, y] = src.sample(P(x: p.x + v.y * n, y: p.y - v.x * n)) * Float(min(1, CGFloat(y) / 20))
                }
            }
            if resting {  // 根部的上沿原本藏在毛里，顺着露出的部分补到肩
                func edge(_ y: Int) -> Int { (0..<o.w).first { o[$0, y].w > 0.5 * Float(min(1, CGFloat(y) / 20)) } ?? o.w }
                let from = (45..<80).min { edge($0) < edge($1) }!, x0 = edge(from), ink = SIMD4<Float>(0x3B, 0x24, 0x18, 255) / 255
                var tones: [SIMD4<Float>] = []
                for y in 25..<90 { for x in 50..<90 where o[x, y].w > 0.95 { tones.append(o[x, y]) } }
                tones.sort { luma($0) < luma($1) }
                let fur = tones.isEmpty ? ink : tones[tones.count / 2]
                let edges = (0..<from).map(edge)
                for y in 0..<from {
                    let t = CGFloat(from - y) / CGFloat(from), start = CGFloat(x0) + (46 - CGFloat(x0)) * t * t, old = CGFloat(min(edges[y], 100))
                    let fade = Float(min(1, CGFloat(y) / 20))
                    for x in Int(start)..<Int(max(old, start)) + 14 {
                        let d = CGFloat(x) - start, keep = Float(max(0, min(1, (CGFloat(x) - old - 4) / 10)))  // 与原有部分渐变衔接
                        let v = d < 5 ? ink * Float(min(1, d + 0.3)) : fur
                        o[x, y] = v * fade * (1 - keep) + o[x, y] * keep
                    }
                }
            }
            let out = o
            for y in 0..<o.h - 74 { for x in 0..<o.w {
                let limit = 104 + max(0, Float(y) - Float(o.h - 100)) * 0.15
                o[x, y] *= max(0, min(1, (limit - Float(x)) / 14))
            } }
            return (out, o)
        }

        var leftArm: Raster { arm(typing, armR, pivot: P(x: 815, y: 572), tip: P(x: 610, y: 682), fade: 50) }
        var mouseArm: Raster { arm(idle, armM, pivot: P(x: 804, y: 560), tip: P(x: 765, y: 706), fade: 52) }
    }

    /// 手臂：肩端羽化，便于旋转衔接
    static func arm(_ f: Raster, _ outline: [P], pivot: P, tip: P, fade: CGFloat) -> Raster {
        let m = mask(outline)
        var o = cut(f, m)
        let l = hypot(tip.x - pivot.x, tip.y - pivot.y)
        for y in 0..<H { for x in 0..<W where o[x, y].w > 0 {  // 越过肩点后渐隐
            let along = ((CGFloat(x) - pivot.x) * (tip.x - pivot.x) + (CGFloat(y) - pivot.y) * (tip.y - pivot.y)) / l
            o[x, y] *= Float(max(0, min(1, (along + fade) / (fade * 0.7))))
        } }
        return upright(o, pivot: pivot, tip: tip, size: CGSize(width: 240, height: 400), pivotAt: P(x: 120, y: 80))
    }
}
