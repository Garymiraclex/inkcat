import AppKit

/// 把生图 AI 给的原图整理成一只猫的素材包：去底、去水印、对齐、检查。
enum Importer {
    enum Pose: String, CaseIterable, Identifiable {
        case idle, type_both, sleep, cover_eyes, no_arms

        var id: String { rawValue }
        var title: String { ["平时", "打字", "睡觉", "捂眼", "无臂"][Self.allCases.firstIndex(of: self)!] }
        var isOptional: Bool { self == .no_arms }
        /// 原版的同一姿势；无臂没有原版，用平时。
        var template: CGImage? {
            let url = Bundle.main.url(forResource: self == .no_arms ? "idle" : rawValue, withExtension: "png", subdirectory: "packs/classic")
            return url.flatMap { CGImageSourceCreateWithURL($0 as CFURL, nil) }.flatMap { CGImageSourceCreateImageAtIndex($0, 0, nil) }
        }
    }

    struct Result {
        var images: [Pose: CGImage] = [:], notes: [Pose: String] = [:], problems: [Pose: String] = [:]
        /// 整套的提醒，如姿势和模板差得多。
        var warning: String?
        var isReady: Bool { problems.isEmpty && Pose.allCases.allSatisfy { $0.isOptional || images[$0] != nil } }
    }

    static func load(_ data: Data) throws -> CGImage {
        guard let source = CGImageSourceCreateWithData(data as CFData, nil), let image = CGImageSourceCreateImageAtIndex(source, 0, nil) else { throw Problem("打不开这张图") }
        guard Library.fits(width: image.width, height: image.height) else { throw Problem("图是 \(image.width)×\(image.height)，需要 4:3 横图，宽 600 到 4096") }
        return image
    }

    static func process(_ raw: [Pose: CGImage]) -> Result {
        var result = Result(), aligned: [Pose: [UInt8]] = [:]
        let zone = Self.zone()
        func note(_ pose: Pose, _ text: String) { result.notes[pose] = [result.notes[pose], text].compactMap { $0 }.joined(separator: "；") }

        // 先粗抠找位置，对齐后再按同一姿势的原版轮廓细抠，浅色的毛不会被当成背景
        func prepare(_ pose: Pose, to reference: [Float]?) {
            var b = Cutout.pixels(raw[pose]!)
            do {
                let paper = try Cutout.paper(b)
                var rough = b
                if let paper { Cutout.knock(&rough, paper: paper) }
                let template = pose.template.map { alpha(Cutout.pixels($0)) }
                let shift = align(alpha(rough), to: reference ?? template ?? alpha(rough))
                if !shift.isNone { b = moved(b, by: shift, fill: paper?.first) }
                if let paper { Cutout.knock(&b, paper: paper, template: pose == .no_arms ? nil : template) }
                if dropStrays(&b, zone: zone) { note(pose, "去掉了画面边上的零散小块（如水印）") }
                shift.text.map { note(pose, $0) }
                aligned[pose] = b
            } catch { result.problems[pose] = error.localizedDescription }
        }
        if raw[.idle] != nil { prepare(.idle, to: nil) }  // 平时那张对准原版，其余对准平时
        let base = aligned[.idle].map(alpha)
        for pose in Pose.allCases where pose != .idle && raw[pose] != nil { prepare(pose, to: base) }
        for (pose, b) in aligned { result.images[pose] = image(b) }

        // 两张一样，多半是存错了
        let poses = Pose.allCases.filter { aligned[$0] != nil }
        for (i, a) in poses.enumerated() { for b in poses[..<i] where same(aligned[a]!, aligned[b]!) {
            result.problems[a] = "和「\(b.title)」是同一张图"
        } }
        if let cover = aligned[.cover_eyes], let t = Pose.cover_eyes.template, !mouseShows(cover, Cutout.pixels(t)) {
            note(.cover_eyes, "鼠标好像被挡住或画歪了，捂眼时要完整露出")
        }
        let order: [Pose] = [.idle, .type_both, .sleep, .cover_eyes, .no_arms]
        if result.problems.isEmpty, order.prefix(4).allSatisfy({ aligned[$0] != nil }) {
            let frames = order.compactMap { aligned[$0] }.map { Raster(image($0)) }
            do {
                let note = try Composer.check(frames, template: Library.scan().first { $0.id == Pack.classic })
                result.warning = note.isEmpty ? nil : note
            } catch {
                let text = error.localizedDescription  // 如「type_both.png 和 idle.png 的头没有对齐」
                let pose = order.first { text.hasPrefix($0.rawValue + ".png") } ?? .idle
                result.problems[pose] = "头和「平时」那张对不上，可能不是同一套图"
            }
        }
        return result
    }

    /// 存成素材文件夹里的一个素材包，返回它的 id。
    static func save(_ images: [Pose: CGImage], name raw: String) throws -> String {
        let name = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !name.isEmpty, !name.hasPrefix("."), !name.hasPrefix("_"), !name.contains("/"), !name.contains(":") else { throw Problem("名字不能为空，不能以 . 或 _ 开头，不能有 / 和 :") }
        let fm = FileManager.default, folder = Library.folder.appendingPathComponent(name, isDirectory: true)
        guard !fm.fileExists(atPath: folder.path) else { throw Problem("素材文件夹里已经有「\(name)」了，换个名字") }
        let temp = Library.folder.appendingPathComponent("_导入中", isDirectory: true)  // _ 开头的不会被扫描；写完再改名
        try? fm.removeItem(at: temp)
        try fm.createDirectory(at: temp, withIntermediateDirectories: true)
        for (pose, image) in images {
            guard let data = NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:]) else { throw Problem("存不了图") }
            try data.write(to: temp.appendingPathComponent(pose.rawValue + ".png"))
        }
        try fm.moveItem(at: temp, to: folder)
        return "user/" + name
    }

    // MARK: 对齐

    struct Shift {
        var dx = 0, dy = 0, scale = 1.0
        var isNone: Bool { dx == 0 && dy == 0 && scale == 1 }
        var text: String? {
            if isNone { return nil }
            var parts: [String] = []
            if dx != 0 || dy != 0 { parts.append("挪了 \(Int(hypot(Double(dx), Double(dy)).rounded())) 像素") }
            if scale != 1 { parts.append(String(format: scale > 1 ? "放大 %.0f%%" : "缩小 %.0f%%", abs(scale - 1) * 100)) }
            let far = abs(dx) >= 22 || abs(dy) >= 22 || abs(scale - 1) > 0.035
            return "已对齐：" + parts.joined(separator: "，") + (far ? "。偏得很多，看看是不是放错了格子" : "")
        }
    }

    static func alpha(_ b: [UInt8]) -> [Float] { stride(from: 3, to: b.count, by: 4).map { Float(b[$0]) / 255 } }

    /// 找让轮廓最贴合的平移（±24 像素）和缩放（±4%）：先在四分之一大小上粗找，再在原尺寸上细调。
    static func align(_ a: [Float], to ref: [Float]) -> Shift {
        let W = Cutter.W, H = Cutter.H
        func shrink(_ m: [Float]) -> [Float] {
            var o = [Float](repeating: 0, count: (W / 4) * (H / 4))
            for y in 0..<H { for x in 0..<W { o[(y / 4) * (W / 4) + x / 4] += m[y * W + x] / 16 } }
            return o
        }
        /// 平移、缩放后与参考图的差；缩放绕画面中心。
        func cost(_ m: [Float], _ r: [Float], w: Int, h: Int, dx: Double, dy: Double, s: Double, step: Int) -> Float {
            let cx = Double(w) / 2, cy = Double(h) / 2
            var total: Float = 0
            for y in stride(from: 0, to: h, by: step) {
                let sy = Int(((Double(y) - cy - dy) / s + cy).rounded())
                for x in stride(from: 0, to: w, by: step) {
                    let sx = Int(((Double(x) - cx - dx) / s + cx).rounded())
                    let v = sx >= 0 && sy >= 0 && sx < w && sy < h ? m[sy * w + sx] : 0
                    total += abs(v - r[y * w + x])
                }
            }
            return total
        }
        let small = shrink(a), smallRef = shrink(ref)
        var best = (dx: 0, dy: 0, s: 1.0), least = cost(small, smallRef, w: W / 4, h: H / 4, dx: 0, dy: 0, s: 1, step: 1)
        for s in stride(from: 0.96, through: 1.0401, by: 0.01) { for dy in -6...6 { for dx in -6...6 {
            let c = cost(small, smallRef, w: W / 4, h: H / 4, dx: Double(dx), dy: Double(dy), s: s, step: 1)
            if c < least * 0.995 { (best, least) = ((dx, dy, s), c) }
        } } }
        var fine = Shift(dx: best.dx * 4, dy: best.dy * 4, scale: (best.s * 100).rounded() / 100)
        least = cost(a, ref, w: W, h: H, dx: Double(fine.dx), dy: Double(fine.dy), s: fine.scale, step: 2)
        let start = fine
        for dy in -3...3 { for dx in -3...3 where dx != 0 || dy != 0 {
            let c = cost(a, ref, w: W, h: H, dx: Double(start.dx + dx), dy: Double(start.dy + dy), s: start.scale, step: 2)
            if c < least * 0.998 { (fine.dx, fine.dy, least) = (start.dx + dx, start.dy + dy, c) }
        } }
        return fine
    }

    /// 挪动后空出来的边填上背景色。
    static func moved(_ b: [UInt8], by s: Shift, fill: Cutout.RGB? = nil) -> [UInt8] {
        var source = b, out = [UInt8](repeating: 0, count: b.count)
        let W = CGFloat(Cutter.W), H = CGFloat(Cutter.H), image = Cutout.make(&source)
        let c = CGContext(data: &out, width: Cutter.W, height: Cutter.H, bitsPerComponent: 8, bytesPerRow: Cutter.W * 4, space: Cutter.sRGB, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        c.interpolationQuality = .high
        if let fill {
            c.setFillColor(red: CGFloat(fill.x) / 255, green: CGFloat(fill.y) / 255, blue: CGFloat(fill.z) / 255, alpha: 1)
            c.fill(CGRect(x: 0, y: 0, width: W, height: H))
        }
        c.translateBy(x: W / 2 + CGFloat(s.dx), y: H / 2 - CGFloat(s.dy))  // 画布左下原点，向下挪是减
        c.scaleBy(x: s.scale, y: s.scale)
        c.draw(image, in: CGRect(x: -W / 2, y: -H / 2, width: W, height: H))
        return out
    }

    // MARK: 检查

    /// 原版各姿势占到的地方外扩 60 像素；完全在外面的小块是水印或杂点。
    private static func zone() -> [Bool] {
        let union = Pose.allCases.compactMap(\.template).map { alpha(Cutout.pixels($0)) }
        return Cutout.spread((0..<Cutter.W * Cutter.H).map { i in union.contains { $0[i] > 0.1 } }, by: 60)
    }

    private static func dropStrays(_ b: inout [UInt8], zone: [Bool]) -> Bool {
        let W = Cutter.W, n = Cutter.W * Cutter.H
        var seen = [Bool](repeating: false, count: n), dropped = false
        for start in 0..<n where !seen[start] && b[start * 4 + 3] > 0 {
            var part = [start], k = 0, inside = false
            seen[start] = true
            while k < part.count {
                let i = part[k], x = i % W
                inside = inside || zone[i]
                for j in [x > 0 ? i - 1 : -1, x < W - 1 ? i + 1 : -1, i - W, i + W] where j >= 0 && j < n && !seen[j] && b[j * 4 + 3] > 0 {
                    seen[j] = true
                    part.append(j)
                }
                k += 1
            }
            if !inside, part.count < 5000 {
                for i in part { for c in 0..<4 { b[i * 4 + c] = 0 } }
                dropped = true
            }
        }
        return dropped
    }

    private static func same(_ a: [UInt8], _ b: [UInt8]) -> Bool {
        var total = 0
        for i in stride(from: 0, to: a.count, by: 7) { total += abs(Int(a[i]) - Int(b[i])) }
        return Double(total) / Double(a.count / 7) < 0.6
    }

    /// 捂眼时鼠标那块和原版的捂眼图重合得够多。
    private static func mouseShows(_ b: [UInt8], _ t: [UInt8]) -> Bool {
        var both = 0, either = 0
        for y in 656...777 { for x in 689...844 {
            let i = (y * Cutter.W + x) * 4 + 3, p = b[i] > 128, q = t[i] > 128
            if p && q { both += 1 }
            if p || q { either += 1 }
        } }
        return both * 100 >= either * 85
    }

    static func image(_ b: [UInt8]) -> CGImage { var c = b; return Cutout.make(&c) }
}
