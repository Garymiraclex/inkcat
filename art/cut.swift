// 从原画生成内置素材，只在开发时用：
//   swiftc -O -parse-as-library Sources/Cutter.swift art/cut.swift -o cut && ./cut art Assets
import AppKit

@main
extension Cutter {
    static func main() {
        let art = CommandLine.arguments[1], out = CommandLine.arguments[2]
        func load(_ path: String) -> Raster { Raster(NSBitmapImageRep(data: try! Data(contentsOf: URL(fileURLWithPath: "\(art)/\(path).png")))!.cgImage!) }
        func save(_ cg: CGImage, _ name: String) {
            try! NSBitmapImageRep(cgImage: cg).representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: "\(out)/\(name).png"))
        }
        func crop(_ r: Raster, _ x: Int, _ y: Int, _ w: Int, _ h: Int) -> CGImage { r.cg.cropping(to: CGRect(x: x, y: y, width: w, height: h))! }

        // ———— 原版：十四张成品图 ————

        var cat = Cat(idle: load("frames/idle"), typing: load("frames/type_both"), cover: load("frames/cover_eyes"), sleep: load("frames/sleep"))
        cat.adaptive = false  // 原版只按模板拆，保证与之前一致
        let wider = P(x: 84, y: 20)
        var desk = mask(pts([670,640, 915,640, 915,870, 670,870]), smooth: false)
        for y in 640..<H { for x in 660..<W where Double(y) <= 0.195 * Double(x - 840) + 656 { desk[y * W + x] = 0 } }
        let hole = mask(mouseHole)
        let plate = tailless(widenPad(inpaint(mix(cat.armless(cat.idle), cat.cover, desk), hole), by: wider), back: cat.back)
        let plateCover = tailless(widenPad(inpaint(trim(cat.cover), hole), by: wider), back: cat.back)
        save(plate.cg, "plate")
        save(plateCover.cg, "plate_cover")
        save(crop(cat.squint(0.38, tilt: -0.02), 400, 250, 290, 150), "eyes_focus")
        save(crop(cat.squint(0.95, tilt: 0.05), 400, 250, 290, 150), "eyes_tense")
        save(crop(cat.tail, 860, 290, 240, 400), "tail")
        save(crop(cat.asleep, 400, 200, 360, 272), "face_sleep")
        save(crop(cut(cat.cover, mask(mouse)), 680, 648, 176, 136), "mouse")
        save(crop(cat.shoulder, 396, 440, 120, 120), "shoulder")
        let right = cat.rightArm
        save(right.out.cg, "arm_right")
        save(right.tucked.cg, "arm_right_in")
        save(cat.leftArm.cg, "arm_left")
        save(cat.mouseArm.cg, "arm_mouse")

        // 应用图标：头像特写
        let icon = CGContext(data: nil, width: 1024, height: 1024, bitsPerComponent: 8, bytesPerRow: 0, space: sRGB, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        icon.addPath(CGPath(roundedRect: CGRect(x: 100, y: 100, width: 824, height: 824), cornerWidth: 186, cornerHeight: 186, transform: nil))
        icon.clip()
        icon.setFillColor(CGColor(srgbRed: 0.93, green: 0.87, blue: 0.77, alpha: 1))
        icon.fill(CGRect(x: 0, y: 0, width: 1024, height: 1024))
        icon.draw(cat.idle.cg, in: CGRect(x: 512 - 590 * 1.75, y: 512 - (896 - 305) * 1.75, width: 1200 * 1.75, height: 896 * 1.75))
        save(icon.makeImage()!, "icon")

        // ———— 单独的部件：键盘、桌垫、鼠标、耳机 ————

        func part(_ r: Raster, _ name: String) { save(r.cg, "packs/\(name)") }
        /// 桌面拆成键盘和桌垫，键盘下面补成桌垫的颜色。
        func split(_ desk: Raster, grow: CGFloat, pad: ClosedRange<Float>, to pack: String) {
            var d = desk
            for y in 0..<H { for x in 0..<W where !onDesk(x, y) { d[x, y] = .zero } }
            let keys = mask(keyboard, grow: grow, smooth: false), near = mask(keyboard, grow: grow + 1.5, smooth: false), far = mask(keyboard, grow: grow + 4, smooth: false)
            // 键盘后面露出的桌垫很窄，少抹一点
            var ring = near
            for y in 0..<H { for x in 0..<W where x < 330 || Double(y) > 608 + 0.19 * Double(x - 330) { ring[y * W + x] = far[y * W + x] } }
            part(cut(d, keys), "\(pack)/keyboard")
            let nowhere = (P(x: 0, y: -9), P(x: 1, y: -9))
            var p = inpaint(d, ring, pad: pad, edge: nowhere)
            for i in 0..<W * H where ring[i] > 0 && keys[i] < 1 && d.px[i].w < 0.95 { p.px[i] = d.px[i] * (1 - keys[i]) + p.px[i] * keys[i] }
            for i in 0..<W * H where solid[i] < 1 && (keys[i] > 0 || ring[i] > 0 && i / W < 676) { p.px[i] *= solid[i] }
            part(p, "\(pack)/pad")
        }
        // 键盘左侧的桌垫边收成一条直线，免得换键盘后露出锯齿
        let solid = mask(pts([150,720, 160,696, 168,680, 178,667, 200,652, 298,597, 318,590, 345,586, 700,640, 740,690, 600,810, 430,780]), smooth: false)
        split(plateCover, grow: 0, pad: 0.62...1, to: "classic")
        part(cut(cat.cover, mask(mouse)), "classic/mouse")

        // 电竞：耳机前后两层，头梁和左耳罩垫在猫身后，右耳罩在前
        let holes = [P(x: 560, y: 120), P(x: 752, y: 208)]  // 头梁下、右耳旁的空隙
        let worn = knock(load("skins/gamer/idle"), seeds: holes)
        let rim: [Double] = [810,375, 820,354, 826,334, 829,312, 829,292, 826,278, 820,262, 812,250, 803,240, 797,228, 795,215, 774,222, 757,222, 742,227, 729,237, 718,252, 709,273, 703,297, 700,320]
        let cup = mask(pts(rim + [701,345, 706,368, 714,385, 725,397, 740,403, 760,404, 780,400, 797,389]), blur: 1)
        var back = Raster()
        for y in 0..<420 { for x in 360..<850 { back[x, y] = worn[x, y] * (1 - cat.idle[x, y].w) } }
        part(completeHeadband(cut(back, cup.map { 1 - $0 })), "gamer/headset_back")
        part(cut(worn, cup), "gamer/headset")

        let desktop = knock(load("skins/gamer/cover_eyes"), seeds: holes)
        let gamerMouse = pts([685,752, 687,738, 695,722, 706,703, 716,694, 726,684, 742,672, 760,663, 780,659, 800,658, 815,662, 828,669, 838,678, 843,687, 849,702, 855,715, 859,725, 858,732, 850,741, 836,753, 822,762, 806,765, 790,764, 778,768, 765,776, 750,784, 738,787, 724,786, 708,780, 697,770, 688,762])
        let gamerHole = mask(pts([678,760, 680,736, 695,710, 713,688, 738,667, 762,657, 790,652, 818,655, 846,671, 858,692, 868,712, 869,722, 862,730, 848,742, 834,755, 820,768, 804,784, 776,796, 744,804, 714,804, 688,790]))
        let empty = widenPad(inpaint(desktop, gamerHole, pad: 0.22...0.36, edge: (P(x: 740, y: 652), P(x: 870, y: 674)), ink: SIMD4(0.13, 0.11, 0.09, 1)), by: P(x: 108, y: 26))
        split(empty, grow: 2, pad: 0.22...0.36, to: "gamer")
        part(cut(desktop, mask(gamerMouse)), "gamer/mouse")
    }

    /// 原图里头梁被狸花猫的耳朵挡住、左耳罩被脸挡住，换别的头就露出空洞：头梁按弧线接上，左耳罩补成整块。
    static func completeHeadband(_ a: Raster) -> Raster {
        var o = a
        let c = P(x: 585, y: 330)
        func at(_ deg: Double, _ r: Double) -> P { P(x: c.x + r * cos(deg * .pi / 180), y: c.y - r * sin(deg * .pi / 180)) }
        // 每个角度上头梁的内外半径；被挡住的角度头梁会变窄或断开
        let angles = Array(stride(from: 24.0, through: 150.0, by: 0.25))
        var band: [(Double, Double)?] = angles.map { deg in
            var best: (Double, Double)?, start: Double?
            for r in stride(from: 150.0, through: 320.0, by: 0.5) {
                let p = at(deg, r), solid = a.sample(p).w > 0.5
                if solid, start == nil { start = r }
                if !solid, let s = start {
                    if r - s > (best.map { $0.1 - $0.0 } ?? 0) { best = (s, r) }
                    start = nil
                }
            }
            return best
        }
        let widths = band.compactMap { $0.map { $0.1 - $0.0 } }.sorted(), full = widths[widths.count * 3 / 4]
        for i in band.indices where (band[i].map { $0.1 - $0.0 } ?? 0) < full * 0.8 { band[i] = nil }
        let known = band.indices.filter { band[$0] != nil }
        for i in band.indices where band[i] == nil {
            guard let lo = known.last(where: { $0 < i }), let hi = known.first(where: { $0 > i }) else { continue }
            let near = i - lo < hi - i ? lo : hi, t = Double(i - lo) / Double(hi - lo), (a0, b0) = band[lo]!, (a1, b1) = band[hi]!
            let (rin, rout) = (a0 + (a1 - a0) * t, b0 + (b1 - b0) * t), (sin, sout) = band[near]!
            for r in stride(from: rin, through: rout, by: 0.5) {
                let u = (r - rin) / max(rout - rin, 1), v = a.sample(at(angles[near], sin + u * (sout - sin)))
                for d in [-0.15, 0, 0.15] {
                    let p = at(angles[i] + d, r)
                    if o[Int(p.x), Int(p.y)].w < v.w { o[Int(p.x), Int(p.y)] = v }
                }
            }
        }
        // 左耳罩：只剩外缘一条，按椭圆补满，内侧补一道描边
        func inCup(_ x: Double, _ y: Double, grow: Double = 0) -> Bool { pow((x - 410) / (40 + grow), 2) + pow((y - 310) / (86 + grow), 2) < 1 }
        var gap: [Int] = [], seed = SIMD4<Float>.zero, count: Float = 0
        for y in 220..<400 { for x in 365..<455 where inCup(Double(x) + 0.5, Double(y) + 0.5) {
            let i = y * W + x
            if a.px[i].w < 0.9 { gap.append(i) } else if luma(a.px[i]) > 0.55 { seed += a.px[i]; count += 1 }
        } }
        var fill = o
        for i in gap { fill.px[i] = seed / max(count, 1) }
        for _ in 0..<400 { for i in gap {
            var t = SIMD4<Float>.zero, n: Float = 0
            for j in [i - 1, i + 1, i - W, i + W] where inCup(Double(j % W) + 0.5, Double(j / W) + 0.5) { t += fill.px[j]; n += 1 }
            fill.px[i] += (t / n - fill.px[i]) * 1.5
        } }
        let ink = SIMD4<Float>(0x3B, 0x2B, 0x22, 255) / 255
        for i in gap {
            let x = Double(i % W) + 0.5, y = Double(i / W) + 0.5
            let edge = !inCup(x, y, grow: -3.2)  // 离椭圆边 3 像素内画描边
            o.px[i] = edge ? ink : fill.px[i]
        }
        return o
    }

    /// 键盘外沿，不含后面露出的桌垫。
    static let keyboard = pts([170,674, 172,667, 179,660, 192,653, 194,648, 204,641, 219,638, 222,633, 236,625.5, 244,625.5, 246,621, 260,611.5, 267,611.5, 270,605, 284,597.5, 293,597.5, 294,593, 306,585, 316,585, 329,587, 332,594, 341,591, 357,592.5, 361,598, 371,595, 386,597.5, 391,603, 402,601, 418,604.5, 424,608.5, 432,606.5, 577,631, 581,636, 591,633.5, 613,636, 618,641.5, 628,639.5, 652,644, 657,648.5, 668,646, 690,650, 697,657, 708,658.5, 713,669, 713.5,687, 706,697, 670,727, 610,779, 597,789.5, 586,791.5, 430,762, 250,722, 175.5,705, 172,691])

    /// 从透明处和 `seeds` 向内清掉残留的浅色背景，边缘按深色描边重算透明度。
    static func knock(_ a: Raster, seeds: [P] = []) -> Raster {
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
}
