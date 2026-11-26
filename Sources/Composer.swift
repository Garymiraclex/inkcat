import AppKit
import CryptoKit

/// 拆好的一只猫。
struct CookedCat {
    static let names = ["body", "body_cover", "raised_arms", "tail", "face_sleep", "eyes_focus", "eyes_tense", "shoulder",
                        "arm_left", "arm_mouse", "arm_right", "arm_right_in"]
    var images: [String: CGImage]
    /// 右肩补块的毛色和描边；不需要补块（鬃毛盖着手臂根部）时为空。
    var joint: [Int]
}

/// 一身装扮的成品：猫窗口直接使用的图和坐标。没有 `images` 就是原版，用内置的图。
struct Look {
    var images: [String: NSImage] = [:]
    /// 鼠标图里的左右键、滚轮轮廓。
    var buttons = Look.classicButtons
    var mouseSize = CGSize(width: 176, height: 136)
    var rows = Keyboard.classicRows
    /// 右肩补块的颜色；为空则不补，肩头一直盖在手臂上。
    var joint = [0xB59B70, 0x3B2418]

    func image(_ name: String) -> NSImage? { images[name] ?? Bundle.main.image(forResource: name) }

    struct Buttons { var left: [CGPoint], right: [CGPoint], wheel: [CGPoint] }
    /// 原版鼠标（画面坐标 689…844 × 656…777）的按键，以鼠标图左上角 (680, 648) 为原点。
    static let classicButtons = Buttons(
        left: Cutter.pts([40,109, 43,98, 48,91, 54,86, 64,68, 72,50, 98,60, 112,72, 101,92, 90,104, 76,110, 56,112]),
        right: Cutter.pts([35,108, 38,98, 42,90, 39,84, 43,72, 50,62, 60,59, 64,50, 50,35, 38,47, 24,67, 17,84, 18,97, 26,104]),
        wheel: Cutter.pts([42,87, 42,80, 47,71, 54,64, 62,64, 62,69, 56,79, 51,87, 46,89]))
}

/// 把选中的部位合成一身装扮。可在后台线程使用。
final class Composer {
    private var cats: [String: CookedCat] = [:]

    struct Choice { let pack: Pack, place: Place }
    struct Result { var look = Look(), problems: [Slot: String] = [:], warnings: [Slot: String] = [:] }

    func compose(_ outfit: [Slot: Choice], fallback: Pack) -> Result {
        var result = Result(), outfit = outfit
        func attempt<T>(_ slot: Slot, _ body: () throws -> T) -> T? {
            do { return try body() } catch {
                result.problems[slot] = error.localizedDescription
                return nil
            }
        }
        // 原版不合成，保证一个像素都不变
        if Slot.allCases.allSatisfy({ outfit[$0] == nil || outfit[$0]!.pack.id == Pack.classic && outfit[$0]!.place == Place() }) { return result }

        // 出错的部位退回原版，配件则不戴
        func layer(_ slot: Slot, _ name: String) -> CGImage? {
            guard let c = outfit[slot] else { return nil }
            if let image = attempt(slot, { try part(c, name) }) { return image }
            outfit[slot] = slot.isOptional ? nil : Choice(pack: fallback, place: Place())
            return slot.isOptional ? nil : try? part(outfit[slot]!, name)
        }
        func back(_ slot: Slot) -> CGImage? {
            guard let c = outfit[slot], FileManager.default.fileExists(atPath: c.pack.file(slot.rawValue + "_back").path) else { return nil }
            return attempt(slot) { try part(c, slot.rawValue + "_back") }
        }
        let chosen = outfit[.cat]?.pack ?? fallback
        guard let cat = attempt(.cat, { try cook(chosen, template: fallback, warn: { result.warnings[.cat] = $0 }) }) ?? (try? cook(fallback, template: fallback, warn: { _ in })) else { return result }
        guard let pad = layer(.pad, "pad"), let keyboard = layer(.keyboard, "keyboard"), let mouse = layer(.mouse, "mouse") else { return result }
        let headset = layer(.headset, "headset"), hat = layer(.hat, "hat")
        let scarf = chosen.config.cat?.scarf == false ? nil : layer(.scarf, "scarf")
        if outfit[.scarf] != nil, scarf == nil, result.problems[.scarf] == nil { result.warnings[.scarf] = "这只猫身上画着围巾，不能再戴" }
        let front = [scarf, headset, hat].compactMap { $0 }, behind = [back(.headset), back(.hat)].compactMap { $0 }

        var images: [String: CGImage] = cat.images
        func plate(_ body: String, _ over: [CGImage]) -> CGImage { Self.flatten(behind + [cat.images[body]!] + front + over + [pad, keyboard]) }
        images["plate"] = plate("body", [])
        images["plate_cover"] = plate("body_cover", [cat.images["raised_arms"]!])
        // 睡脸和眯眼贴在底图上面，要给配件让位
        for (name, box) in [("face_sleep", CGRect(x: 400, y: 200, width: 360, height: 272)), ("eyes_focus", CGRect(x: 400, y: 250, width: 290, height: 150)), ("eyes_tense", CGRect(x: 400, y: 250, width: 290, height: 150))] where !front.isEmpty {
            images[name] = Self.erase(images[name]!, front.map { $0.cropping(to: box)! })
        }
        if let scarf, cat.joint.isEmpty {  // 一直盖在手臂上的鬃毛，外面还有围巾
            let box = CGRect(x: 0, y: 0, width: 120, height: 120)
            images["shoulder"] = Self.draw(box.size) { c in
                c.draw(cat.images["shoulder"]!, in: box)
                c.draw(scarf.cropping(to: CGRect(x: 396, y: 440, width: 120, height: 120))!, in: box)
            }
        }
        let box = Self.mouseBox(mouse)
        images["mouse"] = mouse.cropping(to: box)!
        result.look.images = images.mapValues { NSImage(cgImage: $0, size: NSSize(width: $0.width, height: $0.height)) }
        result.look.mouseSize = box.size
        result.look.buttons = buttons(outfit[.mouse]!, box: box, image: mouse)
        result.look.rows = rows(outfit[.keyboard]!, image: keyboard)
        result.look.joint = cat.joint
        return result
    }

    // MARK: 单个部位

    private func part(_ c: Choice, _ name: String) throws -> CGImage {
        let image = try Cutout.clean(Library.image(Library.read(c.pack.file(name)), name: name), name: name)
        guard let box = Cutout.bounds(image) else { throw Problem("\(name).png 是空的") }
        guard c.place != Place() else { return image }
        let moved = Self.draw { $0.concatenate(c.place.transform(about: CGPoint(x: box.midX, y: box.midY))); $0.draw(image, in: Self.canvas) }
        guard Cutout.bounds(moved) != nil else { throw Problem("\(name).png 被移到画面外了") }
        return moved
    }

    /// 键位跟着键盘一起移动、缩放。
    private func rows(_ c: Choice, image: CGImage) -> [[CGPoint]] {
        var rows = Keyboard.classicRows
        if let own = c.pack.config.keys, own.count == 6, own.allSatisfy({ $0.count == 4 && $0.allSatisfy(\.isFinite) }) {
            rows = own.map { [Scene.at($0[0], $0[1]), Scene.at($0[2], $0[3])] }
        }
        guard c.place != Place(), let original = try? Library.image(Library.read(c.pack.file("keyboard")), name: "keyboard"),
              let clean = try? Cutout.clean(original, name: "keyboard"), let box = Cutout.bounds(clean) else { return rows }
        let t = c.place.transform(about: CGPoint(x: box.midX, y: box.midY))
        return rows.map { $0.map { $0.applying(t) } }
    }

    /// 鼠标图的裁切框：居中，至少和原版一样大。
    private static func mouseBox(_ mouse: CGImage) -> CGRect {
        let b = Cutout.bounds(mouse) ?? CGRect(x: 680, y: 112, width: 176, height: 136)
        let w = max(176, b.width + 4), h = max(136, b.height + 4)
        return CGRect(x: (b.midX - w / 2).rounded(), y: (Scene.size.height - b.midY - h / 2).rounded(), width: w, height: h).intersection(canvas)  // 左上原点
    }

    /// 素材包写了就用，没写就把原版的按键按鼠标外框缩放过去。
    private func buttons(_ c: Choice, box: CGRect, image: CGImage) -> Look.Buttons {
        let own = c.pack.config.buttons
        let b = Cutout.bounds(image)!
        func scaled(_ p: [CGPoint]) -> [CGPoint] {
            p.map { CGPoint(x: b.minX + ($0.x + 680 - 689) / 156 * b.width - box.minX, y: Scene.size.height - b.maxY + ($0.y + 648 - 656) / 122 * b.height - box.minY) }
        }
        func outline(_ given: [[Double]]?, _ fallback: [CGPoint]) -> [CGPoint] {
            guard let given, given.count > 2, given.allSatisfy({ $0.count == 2 && $0.allSatisfy(\.isFinite) }) else { return scaled(fallback) }
            let original = given.map { Scene.at($0[0], $0[1]) }
            var t = CGAffineTransform.identity
            if c.place != Place(), let raw = try? Library.image(Library.read(c.pack.file("mouse")), name: "mouse"), let clean = try? Cutout.clean(raw, name: "mouse"), let r = Cutout.bounds(clean) {
                t = c.place.transform(about: CGPoint(x: r.midX, y: r.midY))
            }
            return original.map { $0.applying(t) }.map { CGPoint(x: $0.x - box.minX, y: Scene.size.height - $0.y - box.minY) }
        }
        return Look.Buttons(left: outline(own?.left, Look.classicButtons.left), right: outline(own?.right, Look.classicButtons.right), wheel: outline(own?.wheel, Look.classicButtons.wheel))
    }

    // MARK: 拆猫

    private func cook(_ pack: Pack, template: Pack, warn: (String) -> Void) throws -> CookedCat {
        let data = try Slot.cat.files.map { try Library.read(pack.file($0)) }
        let bare = FileManager.default.fileExists(atPath: pack.file("no_arms").path) ? try Library.read(pack.file("no_arms")) : nil
        var hash = SHA256()
        hash.update(data: Data("15".utf8))  // 拆图算法变了就加一
        (data + [bare].compactMap { $0 }).forEach { hash.update(data: $0) }
        let key = hash.finalize().map { String(format: "%02x", $0) }.joined()
        let cached = Library.cache.appendingPathComponent(key, isDirectory: true)
        var cat: CookedCat
        if let known = cats[key] ?? Self.restore(cached) {
            cat = known
            if let note = try? String(contentsOf: cached.appendingPathComponent("note.txt"), encoding: .utf8), !note.isEmpty { warn(note) }
        } else {
            let frames = try zip(Slot.cat.files, data).map { Raster(try Cutout.clean(Library.image($1, name: $0), name: $0)) }
            let extra = try bare.map { Raster(try Cutout.clean(Library.image($0, name: "no_arms"), name: "no_arms")) }
            let notes = try Self.check(frames + [extra].compactMap { $0 }, template: pack.id == template.id ? nil : template)
            cat = Self.cut(Cutter.Cat(idle: frames[0], typing: frames[1], cover: frames[3], sleep: frames[2], bare: extra))
            Self.store(cat, at: cached, note: notes)
            if !notes.isEmpty { warn(notes) }
        }
        cats = [key: cat].merging(cats.count > 3 ? [:] : cats) { a, _ in a }
        if let own = pack.config.cat?.joint, own.count == 2, !cat.joint.isEmpty {
            let colors = own.compactMap { Int($0.drop { $0 == "#" }, radix: 16) }
            if colors.count == 2 { cat.joint = colors }
        }
        return cat
    }

    /// 帧与帧对不齐就没法用；和模板差太多则提醒。
    static func check(_ frames: [Raster], template: Pack?) throws -> String {
        for (name, f) in zip(Slot.cat.files + ["no_arms"], frames).dropFirst() {  // 比耳朵和头顶，各帧里都不该动
            var moved = 0, total = 0
            for y in stride(from: 40, to: 240, by: 2) { for x in stride(from: 380, to: 830, by: 2) {
                let d = f[x, y] - frames[0][x, y]
                total += 1
                if max(d.max(), -d.min()) > 0.16 { moved += 1 }
            } }
            if moved * 100 > total * 12 { throw Problem("\(name).png 和 idle.png 的头没有对齐") }
        }
        guard let template, let t = try? Raster(Library.image(Library.read(template.file("idle")), name: "idle")) else { return "" }
        var both = 0, either = 0
        for i in t.px.indices {
            let a = t.px[i].w > 0.5, b = frames[0].px[i].w > 0.5
            if a && b { both += 1 }
            if a || b { either += 1 }
        }
        return both * 100 < either * 88 ? "姿势和模板差得比较多，手臂和尾巴可能拆不干净" : ""
    }

    static func cut(_ c: Cutter.Cat) -> CookedCat {
        typealias R = Raster
        let cover = Cutter.tailless(Cutter.trim(c.cover), back: c.back)
        /// 去掉桌面；腹部下缘用捂眼那张，没有桌沿。
        func torso(_ f: R) -> R {
            var o = f
            for y in 540..<Cutter.H { for x in 0..<Cutter.W {
                if Cutter.onDesk(x, y) { o[x, y] = .zero; continue }
                if x < 455 { continue }
                let t = Float(max(0, min(1, (Double(y) - 614 - 0.145 * Double(x - 660)) / 6)))
                o[x, y] = o[x, y] * (1 - t) + cover[x, y] * t
            } }
            return o
        }
        func crop(_ r: R, _ x: Int, _ y: Int, _ w: Int, _ h: Int) -> CGImage { r.cg.cropping(to: CGRect(x: x, y: y, width: w, height: h))! }
        let right = c.rightArm
        let bodyCover = torso(cover)
        // 右肩补块：毛色取肩头附近的
        var near: [SIMD4<Float>] = []
        for y in 490..<520 { for x in 405..<435 where c.typing[x, y].w > 0.9 && hypot(Double(x - 420), Double(y - 505)) < 15 { near.append(c.typing[x, y]) } }
        near.sort { Cutter.luma($0) < Cutter.luma($1) }
        func hex(_ v: SIMD4<Float>) -> Int { (0..<3).reduce(0) { $0 << 8 | Int(max(0, min(255, (v[$1] * 255).rounded()))) } }
        let joint = c.rightArmRests ? [] : near.isEmpty ? Look().joint : [hex(near[near.count * 2 / 3]), 0x3B2418]
        return CookedCat(images: [
            "body": torso(Cutter.tailless(c.armless(c.idle), back: c.back)).cg,
            "body_cover": bodyCover.cg,
            "raised_arms": Cutter.cut(bodyCover, Cutter.raisedArms).cg,
            "tail": crop(c.tail, 860, 290, 240, 400),
            "face_sleep": crop(c.asleep, 400, 200, 360, 272),
            "eyes_focus": crop(c.squint(0.38, tilt: -0.02), 400, 250, 290, 150),
            "eyes_tense": crop(c.squint(0.95, tilt: 0.05), 400, 250, 290, 150),
            "shoulder": crop(c.shoulder, 396, 440, 120, 120),
            "arm_left": Cutter.trimmed(c.leftArm, top: 150).cg,
            "arm_mouse": Cutter.trimmed(c.mouseArm, top: 150).cg,
            "arm_right": Cutter.trimmed(right.out, top: 80).cg,
            "arm_right_in": Cutter.trimmed(right.tucked, top: 80).cg,
        ], joint: joint)
    }

    // MARK: 缓存

    private static func store(_ cat: CookedCat, at url: URL, note: String) {
        let fm = FileManager.default, temp = url.appendingPathExtension("part")
        try? fm.removeItem(at: temp)
        try? fm.createDirectory(at: temp, withIntermediateDirectories: true)
        for (name, image) in cat.images {
            try? NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:])?.write(to: temp.appendingPathComponent(name + ".png"))
        }
        try? JSONEncoder().encode(cat.joint).write(to: temp.appendingPathComponent("joint.json"))
        try? note.write(to: temp.appendingPathComponent("note.txt"), atomically: false, encoding: .utf8)
        try? fm.moveItem(at: temp, to: url)  // 写完整才启用
        func date(_ u: URL) -> Date { (try? u.resourceValues(forKeys: [.contentModificationDateKey]))?.contentModificationDate ?? .distantPast }
        let all = (try? fm.contentsOfDirectory(at: url.deletingLastPathComponent(), includingPropertiesForKeys: [.contentModificationDateKey])) ?? []
        all.sorted { date($0) > date($1) }.dropFirst(8).forEach { try? fm.removeItem(at: $0) }
    }

    private static func restore(_ url: URL) -> CookedCat? {
        var images: [String: CGImage] = [:]
        for name in CookedCat.names {
            guard let source = CGImageSourceCreateWithURL(url.appendingPathComponent(name + ".png") as CFURL, nil),
                  let image = CGImageSourceCreateImageAtIndex(source, 0, nil) else { return nil }
            images[name] = image
        }
        guard let data = try? Data(contentsOf: url.appendingPathComponent("joint.json")), let joint = try? JSONDecoder().decode([Int].self, from: data), joint.count != 1 else { return nil }
        return CookedCat(images: images, joint: joint)
    }

    // MARK: 画图

    static let canvas = CGRect(origin: .zero, size: Scene.size)

    static func draw(_ size: CGSize = Scene.size, _ body: (CGContext) -> Void) -> CGImage {
        let c = CGContext(data: nil, width: Int(size.width), height: Int(size.height), bitsPerComponent: 8, bytesPerRow: 0, space: Cutter.sRGB, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        c.interpolationQuality = .high
        body(c)
        return c.makeImage()!
    }

    private static func flatten(_ layers: [CGImage]) -> CGImage { draw { c in layers.forEach { c.draw($0, in: canvas) } } }

    private static func erase(_ image: CGImage, _ holes: [CGImage]) -> CGImage {
        let size = CGSize(width: image.width, height: image.height), box = CGRect(origin: .zero, size: size)
        return draw(size) { c in
            c.draw(image, in: box)
            c.setBlendMode(.destinationOut)
            holes.forEach { c.draw($0, in: box) }
        }
    }
}

extension Place {
    /// 画布坐标（左下原点）里的变换，绕 `c` 缩放旋转。
    func transform(about c: CGPoint) -> CGAffineTransform {
        CGAffineTransform(translationX: c.x + x, y: c.y - y).rotated(by: -angle * .pi / 180).scaledBy(x: scale, y: scale).translatedBy(x: -c.x, y: -c.y)
    }
}

typealias Raster = Cutter.Raster
