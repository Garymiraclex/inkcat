import CoreGraphics
import simd

/// 把图整理成透明底的 1200×896：比例接近的居中铺满，纯色（或棋盘格）背景去掉。
enum Cutout {
    static let W = Cutter.W, H = Cutter.H
    typealias RGB = SIMD3<Float>

    /// 居中铺满画面，多出的一两行裁掉。
    static func pixels(_ image: CGImage) -> [UInt8] {
        var b = [UInt8](repeating: 0, count: W * H * 4)
        let c = CGContext(data: &b, width: W, height: H, bitsPerComponent: 8, bytesPerRow: W * 4, space: Cutter.sRGB, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        c.interpolationQuality = .high
        let s = max(CGFloat(W) / CGFloat(image.width), CGFloat(H) / CGFloat(image.height))
        let w = CGFloat(image.width) * s, h = CGFloat(image.height) * s
        c.draw(image, in: CGRect(x: (CGFloat(W) - w) / 2, y: (CGFloat(H) - h) / 2, width: w, height: h))
        return b
    }

    /// 不透明区域的外框，左下原点。
    static func bounds(_ image: CGImage) -> CGRect? {
        let b = pixels(image)
        var x0 = W, x1 = -1, y0 = H, y1 = -1
        for y in 0..<H { for x in 0..<W where b[(y * W + x) * 4 + 3] > 40 {
            x0 = min(x0, x); x1 = max(x1, x); y0 = min(y0, y); y1 = max(y1, y)
        } }
        return x1 < 0 ? nil : CGRect(x: x0, y: H - 1 - y1, width: x1 - x0 + 1, height: y1 - y0 + 1)
    }

    static func clean(_ image: CGImage, name: String) throws -> CGImage {
        var b = pixels(image)
        do {
            guard let paper = try paper(b) else { return image.width == W && image.height == H ? image : make(&b) }
            knock(&b, paper: paper)
        } catch { throw Problem("\(name).png 的" + error.localizedDescription) }
        return make(&b)
    }

    /// 背景色：已经透明时为空；四边是一种颜色（纯色底）或两种（画出来的棋盘格），允许 2% 的杂点（如水印）。
    static func paper(_ b: [UInt8]) throws -> [RGB]? {
        var border: [Int] = []
        for x in 0..<W { border += [x, (H - 1) * W + x] }
        for y in 1..<H - 1 { border += [y * W, y * W + W - 1] }
        let solid = border.filter { b[$0 * 4 + 3] > 200 }
        if solid.count * 2 < border.count { return nil }
        let colors = solid.map { rgb(b, $0) }
        var rest = colors, paper: [RGB] = []
        func key(_ c: RGB) -> Int { Int(c.x) / 8 << 10 | Int(c.y) / 8 << 5 | Int(c.z) / 8 }
        while paper.count < 2, rest.count * 50 > colors.count {
            var counts: [Int: Int] = [:]
            for c in rest { counts[key(c), default: 0] += 1 }
            let top = counts.max { $0.value < $1.value }!.key
            let members = rest.filter { key($0) == top }
            paper.append(members.reduce(RGB(), +) / Float(members.count))
            rest = rest.filter { far($0, paper) > 30 }
        }
        guard rest.count * 50 <= colors.count else { throw Problem("背景不是透明的，也不是纯色") }
        return paper
    }

    /// 从四边往里清掉背景，紧贴背景的两圈变半透明并去掉混进去的底色，深色桌面上不留灰边。
    /// 给了 `template`（对齐后同一姿势的原版轮廓）时更稳：清背景不进入模板身体的内部；
    /// 被围住、大部分在模板之外的同色块（如耳机头梁内侧）也清掉。
    static func knock(_ b: inout [UInt8], paper: [RGB], template: [Float]? = nil) {
        let n = W * H, source = b
        let d = (0..<n).map { far(rgb(source, $0), paper) }
        let core = template.map { t in spread(t.map { $0 < 0.5 }, by: 20).map { !$0 } } ?? []
        func around(_ i: Int, _ body: (Int) -> Void) {
            let x = i % W
            if x > 0 { body(i - 1) }
            if x < W - 1 { body(i + 1) }
            if i >= W { body(i - W) }
            if i < n - W { body(i + W) }
        }
        func open(_ i: Int) -> Bool { d[i] < (core.isEmpty || !core[i] ? 16 : 7) }  // 模板身体里只认几乎一样的颜色，背景是平的，毛不是

        var bg = [Bool](repeating: false, count: n), queue: [Int] = []
        for i in 0..<n where (i < W || i >= n - W || i % W == 0 || i % W == W - 1) && open(i) { bg[i] = true; queue.append(i) }
        while let i = queue.popLast() {
            around(i) { j in if !bg[j], open(j) { bg[j] = true; queue.append(j) } }
        }
        if let template {
            var seen = bg
            for start in 0..<n where !seen[start] && d[start] < 16 {
                var part = [start], k = 0
                seen[start] = true
                while k < part.count {
                    around(part[k]) { j in if !seen[j], d[j] < 16 { seen[j] = true; part.append(j) } }
                    k += 1
                }
                if part.count >= 150, part.filter({ template[$0] < 0.1 }).count * 5 >= part.count * 4 { part.forEach { bg[$0] = true } }
            }
        }

        var ring = [UInt8](repeating: 0, count: n)
        for i in 0..<n where !bg[i] { around(i) { j in if bg[j] { ring[i] = 1 } } }
        for i in 0..<n where !bg[i] && ring[i] == 0 { around(i) { j in if ring[j] == 1 { ring[i] = 2 } } }
        for i in 0..<n {
            if bg[i] { for k in 0..<4 { b[i * 4 + k] = 0 }; continue }
            guard ring[i] > 0 else { continue }
            let a: Float = ring[i] == 1 ? min(1, max(0, (d[i] - 24) / 38)) : min(1, max(0, (d[i] - 18) / 26))
            guard a < 1 else { continue }
            let c = rgb(source, i), p = nearest(c, paper)
            for k in 0..<3 {
                let v: Float = c[k] - p[k] * (1 - a)
                b[i * 4 + k] = UInt8(min(255 * a, max(0, v)).rounded())
            }
            b[i * 4 + 3] = UInt8((255 * a).rounded())
        }
    }

    static func rgb(_ b: [UInt8], _ i: Int) -> RGB { RGB(Float(b[i * 4]), Float(b[i * 4 + 1]), Float(b[i * 4 + 2])) }

    /// 离背景色有多远；棋盘格的两色之间的过渡色也算背景。
    static func far(_ c: RGB, _ paper: [RGB]) -> Float { simd_distance(c, nearest(c, paper)) }

    private static func nearest(_ c: RGB, _ paper: [RGB]) -> RGB {
        if paper.count == 2 {
            let a = paper[0], ab = paper[1] - paper[0]
            return a + ab * min(1, max(0, simd_dot(c - a, ab) / max(simd_length_squared(ab), 1)))
        }
        return paper[0]
    }

    /// 每个为真的点向四周扩 `r` 像素（方形）。
    static func spread(_ m: [Bool], by r: Int) -> [Bool] {
        var sum = [Int](repeating: 0, count: (W + 1) * (H + 1))  // 积分图
        for y in 0..<H { for x in 0..<W {
            sum[(y + 1) * (W + 1) + x + 1] = (m[y * W + x] ? 1 : 0) + sum[y * (W + 1) + x + 1] + sum[(y + 1) * (W + 1) + x] - sum[y * (W + 1) + x]
        } }
        return (0..<W * H).map { i in
            let x = i % W, y = i / W, x0 = max(0, x - r), y0 = max(0, y - r), x1 = min(W, x + r + 1), y1 = min(H, y + r + 1)
            return sum[y1 * (W + 1) + x1] - sum[y0 * (W + 1) + x1] - sum[y1 * (W + 1) + x0] + sum[y0 * (W + 1) + x0] > 0
        }
    }

    static func make(_ b: inout [UInt8]) -> CGImage {
        CGContext(data: &b, width: W, height: H, bitsPerComponent: 8, bytesPerRow: W * 4, space: Cutter.sRGB, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!.makeImage()!
    }
}
