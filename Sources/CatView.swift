import AppKit

/// 可在设置里开关的动作。
enum Feature: String {
    case tail, mood, shake, pop
    var isOn: Bool { UserDefaults.standard.object(forKey: rawValue) as? Bool ?? true }
}

private extension NSColor {
    convenience init(_ hex: Int) {
        self.init(srgbRed: CGFloat(hex >> 16 & 255) / 255, green: CGFloat(hex >> 8 & 255) / 255,
                  blue: CGFloat(hex & 255) / 255, alpha: 1)
    }
}

private let ink = NSColor(0x4A3428), paper = NSColor(0xFFF8EC), glow = NSColor(0xFF8A4C), spark = NSColor(0xDD7F5C)

private func withoutAnimation(_ body: () -> Void) {
    CATransaction.begin()
    CATransaction.setDisableActions(true)
    body()
    CATransaction.commit()
}

private extension CALayer {
    /// 按素材原始像素尺寸建层；`origin` 为它在画面中的左上角。
    convenience init(asset: String, origin: CGPoint, size: CGSize) {
        self.init()
        contents = Bundle.main.image(forResource: asset)
        frame = CGRect(origin: Scene.at(origin.x, origin.y + size.height), size: size)
        minificationFilter = .trilinear
    }

    @discardableResult
    func add(_ path: CGPath, fill: NSColor? = nil, stroke: NSColor? = nil, width: CGFloat = 0) -> CAShapeLayer {
        let s = CAShapeLayer()
        s.path = path
        s.fillColor = fill?.cgColor
        s.strokeColor = stroke?.cgColor
        s.lineWidth = width
        s.lineCap = .round
        s.lineJoin = .round
        addSublayer(s)
        return s
    }

    func add(text: String, size: CGFloat, color: NSColor) {
        let t = CATextLayer()
        let font = NSFont.systemFont(ofSize: size, weight: .semibold)
        let box = (text as NSString).size(withAttributes: [.font: font])
        t.string = text
        t.font = font
        t.fontSize = size
        t.foregroundColor = color.cgColor
        t.alignmentMode = .center
        t.frame = CGRect(x: -box.width / 2 - 4, y: -box.height / 2, width: box.width + 8, height: box.height)
        addSublayer(t)
    }
}

/// 一条手臂：素材竖直放置，肩点在 (120, 80)。旋转指向目标，只拉伸小臂中段，爪子保持原样。
private final class Arm {
    let layer = CALayer()
    private let shoulder: CGPoint, length: CGFloat
    private static let image = CGSize(width: 240, height: 400), root: CGFloat = 80

    init(_ asset: String, shoulder: CGPoint, length: CGFloat, in parent: CALayer) {
        self.shoulder = shoulder
        self.length = length
        layer.contents = Bundle.main.image(forResource: asset)
        layer.contentsCenter = CGRect(x: 0, y: 1 - (Self.root + length * 0.6) / Self.image.height, width: 1, height: length * 0.4 / Self.image.height)
        layer.position = shoulder
        parent.addSublayer(layer)
    }

    func reach(_ tip: CGPoint, duration: CFTimeInterval) {
        let dx = tip.x - shoulder.x, dy = tip.y - shoulder.y
        let reach = min(max(hypot(dx, dy), length * 0.9), length * 2)
        let height = Self.image.height + reach - length
        CATransaction.begin()
        CATransaction.setAnimationDuration(duration)
        CATransaction.setAnimationTimingFunction(CAMediaTimingFunction(name: .easeOut))
        layer.bounds = CGRect(x: 0, y: 0, width: Self.image.width, height: height)
        layer.anchorPoint = CGPoint(x: 0.5, y: 1 - Self.root / height)
        layer.setAffineTransform(CGAffineTransform(rotationAngle: atan2(dx, -dy)))
        CATransaction.commit()
    }
}

/// 短臂：素材是展平的直条（宽 140，肩在上），切成薄片沿曲线排布，肘部自然弯、爪子始终朝下。
private final class BendyArm {
    let layer = CALayer()
    private static let width: CGFloat = 140, length: CGFloat = 175, count = 42, paw: CGFloat = 76
    private let images: [NSImage?]  // 外展、内收
    private var slices: [CALayer] = [], tucked: Bool?
    /// 内收时盖住根部的肩头。
    private let shoulder = CALayer(asset: "shoulder", origin: CGPoint(x: 396, y: 440), size: CGSize(width: 120, height: 120))

    init(_ asset: String, in parent: CALayer) {
        images = [asset, asset + "_in"].map { Bundle.main.image(forResource: $0) }
        let n = CGFloat(Self.count)
        for i in 0..<Self.count {
            let s = CALayer()
            s.minificationFilter = .trilinear
            // 上下各多取半片，避免弯曲处露缝
            let top = max(0, CGFloat(i) - 0.5), bottom = min(n, CGFloat(i) + 1.5)
            s.contentsRect = CGRect(x: 0, y: 1 - bottom / n, width: 1, height: (bottom - top) / n)
            s.anchorPoint = CGPoint(x: 0.5, y: 1 - (CGFloat(i) + 0.5 - top) / (bottom - top))
            layer.addSublayer(s)
            slices.append(s)
        }
        layer.addSublayer(shoulder)
        shoulder.zPosition = 1
        parent.addSublayer(layer)
    }

    func reach(_ target: CGPoint, duration: CFTimeInterval) {
        // 目标越靠内，手臂起点越靠胸前、起势越朝下
        let home = Scene.at(369, 637)
        let inward = min(max((target.x - home.x) / 125, 0), 1), outward = min(max((home.x - target.x) / 150, 0), 1)
        let start = Scene.at(480 - 2 * inward, 528 - 6 * inward)
        let launch = (1 - inward) * .pi + inward * 1.42 * .pi + outward * 0.04 * .pi  // 从向左到朝下
        let lean = -0.1 - 0.35 * outward + 0.12 * inward
        let dir = CGVector(dx: sin(lean), dy: -cos(lean))
        let d = hypot(target.x - start.x, target.y - start.y)
        let c1 = CGPoint(x: start.x + cos(launch) * d * 0.36, y: start.y + sin(launch) * d * 0.36)
        let c2 = CGPoint(x: target.x - dir.dx * d * 0.5, y: target.y - dir.dy * d * 0.5)

        func point(_ t: CGFloat) -> CGPoint {
            let u = 1 - t, w = (u * u * u, 3 * u * u * t, 3 * u * t * t, t * t * t)
            return CGPoint(x: w.0 * start.x + w.1 * c1.x + w.2 * c2.x + w.3 * target.x,
                           y: w.0 * start.y + w.1 * c1.y + w.2 * c2.y + w.3 * target.y)
        }
        let samples = (0...80).map { point(CGFloat($0) / 80) }
        var arc: [CGFloat] = [0]
        for i in 1...80 { arc.append(arc[i - 1] + hypot(samples[i].x - samples[i - 1].x, samples[i].y - samples[i - 1].y)) }
        let total = arc[80]
        /// 素材上的位置 → 曲线弧长：爪子不伸缩，只拉伸臂段。
        func place(_ y: CGFloat) -> CGFloat {
            let sleeve = Self.length - Self.paw, room = max(total - Self.paw, 20)
            return y < sleeve ? y / sleeve * room : room + (y - sleeve)
        }
        func on(_ s: CGFloat) -> CGPoint {
            let i = min(max((arc.firstIndex { $0 >= s } ?? 80), 1), 80)
            let f = (s - arc[i - 1]) / max(arc[i] - arc[i - 1], 0.001)
            return CGPoint(x: samples[i - 1].x + (samples[i].x - samples[i - 1].x) * f, y: samples[i - 1].y + (samples[i].y - samples[i - 1].y) * f)
        }
        let step = Self.length / CGFloat(Self.count)
        if tucked != (inward > 0.15) {
            tucked = inward > 0.15
            withoutAnimation { slices.forEach { $0.contents = images[tucked! ? 1 : 0] } }
        }
        CATransaction.begin()
        CATransaction.setAnimationDuration(duration)
        CATransaction.setAnimationTimingFunction(CAMediaTimingFunction(name: .easeOut))
        shoulder.opacity = Float(min(inward * 4, 1))
        for (i, s) in slices.enumerated() {
            let a = place(CGFloat(i) * step), b = place(CGFloat(i + 1) * step)
            let p = on((a + b) / 2), before = on((a + b) / 2 - 9), after = on((a + b) / 2 + 9)  // 取宽一点的切线更平滑
            s.bounds = CGRect(x: 0, y: 0, width: Self.width, height: (b - a) * s.contentsRect.height * CGFloat(Self.count))
            s.position = p
            s.setAffineTransform(CGAffineTransform(rotationAngle: atan2(after.x - before.x, -(after.y - before.y))))
        }
        CATransaction.commit()
    }
}

/// 事件驱动：没有输入时只有偶尔的眨眼和摇尾。
final class CatView: NSView {
    static let baseSize = CGSize(width: 300, height: 224)
    var showsBubbles = true

    /// 设置窗口里的演示猫：不看真实输入，所有动作可用。
    private let isPreview: Bool

    private enum Pose: String { case normal = "plate", cover = "plate_cover" }
    private let images = Dictionary(uniqueKeysWithValues: [Pose.normal, .cover].map { ($0, Bundle.main.image(forResource: $0.rawValue)) })
    private let tail = CALayer(asset: "tail", origin: CGPoint(x: 860, y: 290), size: CGSize(width: 240, height: 400))
    private let mood = CALayer(), blush = CALayer(), sweat = CALayer()
    private let squints = ["eyes_focus", "eyes_tense"].map { CALayer(asset: $0, origin: CGPoint(x: 400, y: 250), size: CGSize(width: 290, height: 150)) }
    private var keyTimes: [Date] = [], moodLevel = 0
    private let stage = CALayer(), plate = CALayer(), arms = CALayer(), bubbles = CALayer()
    private let face = CALayer(asset: "face_sleep", origin: CGPoint(x: 400, y: 200), size: CGSize(width: 360, height: 272))
    private let mouse = CALayer(asset: "mouse", origin: CGPoint(x: 680, y: 648), size: CGSize(width: 176, height: 136))
    /// 以猫自己的左右命名：猫的左手在画面右侧，兼管鼠标。
    private var left: Arm!, right: BendyArm!, mouser: Arm!
    private var buttons: [CALayer] = [], wheel: CAShapeLayer!, lastScroll = Date.distantPast
    private var glows: [UInt16: CAShapeLayer] = [:]
    private var tapHand = Hand.left, tapped: [Hand: CGPoint] = [:]  // 隐私模式下正按着的落点
    private var leftRest = CatView.leftHome, rightRest = CatView.rightHome, spaceHand = Hand.left

    private static let leftHome = Scene.at(600, 640), rightHome = Scene.at(362, 612)
    private var held: [UInt16] = []
    private var leftOnMouse = true, secure = false, napping = false
    private var lastActivity = Date(), lastKey = Date(), lastMouseUpdate = Date.distantPast
    private var nextBlink = Date(), nextWag = Date(), nextZ = Date()

    init(preview: Bool = false) {
        isPreview = preview
        super.init(frame: .zero)
        wantsLayer = true
        layer!.addSublayer(stage)
        stage.anchorPoint = .zero
        withoutAnimation(build)
    }

    required init?(coder: NSCoder) { fatalError() }

    override func setFrameSize(_ size: NSSize) {
        super.setFrameSize(size)
        let k = size.width / Scene.size.width
        withoutAnimation { stage.setAffineTransform(CGAffineTransform(scaleX: k, y: k)) }
    }

    // MARK: 搭建

    private func build() {
        let root = Scene.at(916, 652)
        tail.anchorPoint = CGPoint(x: (root.x - tail.frame.minX) / tail.frame.width, y: (root.y - tail.frame.minY) / tail.frame.height)
        tail.position = root
        stage.addSublayer(tail)
        plate.frame = CGRect(origin: .zero, size: Scene.size)
        plate.minificationFilter = .trilinear
        stage.addSublayer(plate)
        setPose(.normal)
        face.opacity = 0
        buildMood()
        stage.addSublayer(face)

        for key in Keyboard.keys.values {
            let path = CGMutablePath(), h = key.half, d = CGVector(dx: 8, dy: 7)  // d 为键深方向的一半
            let w = 1 - 5 / hypot(h.dx, h.dy)
            path.addLines(between: [(1, 1), (1, -1), (-1, -1), (-1, 1)].map { (a: CGFloat, b: CGFloat) in
                CGPoint(x: key.center.x + h.dx * w * a + d.dx * b, y: key.center.y + h.dy * w * a + d.dy * b)
            })
            path.closeSubpath()
            let shape = stage.add(path, fill: glow, stroke: glow, width: 5)
            shape.opacity = 0
            glows[key.code] = shape
        }

        mouse.shadowOpacity = 0.22
        mouse.shadowRadius = 5
        mouse.shadowOffset = CGSize(width: 9, height: -7)
        buildButtons()
        stage.addSublayer(mouse)

        stage.addSublayer(arms)
        buildJoint()
        right = BendyArm("arm_right", in: arms)
        left = Arm("arm_left", shoulder: Scene.at(815, 572), length: 233, in: arms)
        mouser = Arm("arm_mouse", shoulder: Scene.at(804, 560), length: 151, in: arms)
        moveMouse(to: CGPoint(x: 0.5, y: 0.5))
        left.reach(leftRest, duration: 0)
        right.reach(rightRest, duration: 0)
        mouser.reach(mousePaw, duration: 0)
        showLeftArm()
        stage.addSublayer(bubbles)
    }

    /// 右肩外侧的圆弧，手臂内收时补住轮廓。
    private func buildJoint() {
        let center = Scene.at(450, 531), arc = CGMutablePath(), pie = CGMutablePath()
        arc.addArc(center: center, radius: 41, startAngle: .pi * 0.62, endAngle: .pi * 1.05, clockwise: false)
        pie.move(to: center)
        pie.addArc(center: center, radius: 41, startAngle: .pi * 0.62, endAngle: .pi * 1.05, clockwise: false)
        pie.closeSubpath()
        arms.add(pie, fill: NSColor(0xB59B70))
        arms.add(arc, stroke: NSColor(0x3B2418), width: 6)
    }

    private func setPose(_ pose: Pose) {
        withoutAnimation { plate.contents = images[pose]! }
    }

    private func showLeftArm() {
        withoutAnimation {
            left.layer.isHidden = leftOnMouse
            mouser.layer.isHidden = !leftOnMouse
        }
    }

    /// 左右键高亮、火花与滚轮，坐标取自鼠标素材（左上原点）。
    private func buildButtons() {
        func shape(_ p: [(CGFloat, CGFloat)], closed: Bool = true) -> CGPath {
            let path = CGMutablePath()
            path.addLines(between: p.map { CGPoint(x: $0.0, y: mouse.bounds.height - $0.1) })
            if closed { path.closeSubpath() }
            return path
        }
        let areas: [[(CGFloat, CGFloat)]] = [
            [(40, 109), (43, 98), (48, 91), (54, 86), (64, 68), (72, 50), (98, 60), (112, 72), (101, 92), (90, 104), (76, 110), (56, 112)],
            [(35, 108), (38, 98), (42, 90), (39, 84), (43, 72), (50, 62), (60, 59), (64, 50), (50, 35), (38, 47), (24, 67), (17, 84), (18, 97), (26, 104)],
        ]
        let rays: [[[(CGFloat, CGFloat)]]] = [
            [[(44, 140), (36, 164)], [(78, 140), (80, 168)], [(110, 132), (124, 155)]],
            [[(2, 70), (-22, 62)], [(-2, 96), (-28, 102)], [(12, 122), (-8, 142)]],
        ]
        for (area, ray) in zip(areas, rays) {  // 左键、右键
            let button = CALayer()
            button.add(shape(area), fill: glow.withAlphaComponent(0.85))
            ray.forEach { button.add(shape($0, closed: false), stroke: spark, width: 10) }
            button.opacity = 0
            mouse.addSublayer(button)
            buttons.append(button)
        }
        wheel = mouse.add(shape([(42, 87), (42, 80), (47, 71), (54, 64), (62, 64), (62, 69), (56, 79), (51, 87), (46, 89)]), fill: glow, stroke: ink, width: 4)
        wheel.opacity = 0
    }

    /// `p` 为光标在屏幕内的比例位置。猫面向观众，它的右和前对应画面的左和下。
    private func moveMouse(to p: CGPoint) {
        func mix(_ a: CGPoint, _ b: CGPoint, _ t: CGFloat) -> CGPoint { CGPoint(x: a.x + (b.x - a.x) * t, y: a.y + (b.y - a.y) * t) }
        let far = mix(Scene.at(880, 742), Scene.at(774, 719), p.x)
        let near = mix(Scene.at(806, 783), Scene.at(704, 776), p.x)
        mouse.position = mix(far, near, p.y)
    }

    /// 按下时爪子往前压一点。
    private var mousePaw: CGPoint {
        let pressed = buttons.contains { $0.opacity > 0 }
        return CGPoint(x: mouse.position.x - (pressed ? 9 : 3), y: mouse.position.y + (pressed ? -2 : 10))
    }

    // MARK: 拖动

    /// 画面的不透明区域，每点一字节。
    private lazy var hitMap: [UInt8] = {
        let w = Int(Self.baseSize.width), h = Int(Self.baseSize.height)
        var pixels = [UInt8](repeating: 0, count: w * h)
        let k = Self.baseSize.width / Scene.size.width
        pixels.withUnsafeMutableBytes { buffer in
            let context = CGContext(data: buffer.baseAddress, width: w, height: h, bitsPerComponent: 8, bytesPerRow: w,
                                    space: CGColorSpaceCreateDeviceGray(), bitmapInfo: CGImageAlphaInfo.alphaOnly.rawValue)
            images[.normal]!?.cgImage(forProposedRect: nil, context: nil, hints: nil).map {
                context?.draw($0, in: CGRect(x: 0, y: 0, width: w, height: h))
            }
            (tail.contents as? NSImage)?.cgImage(forProposedRect: nil, context: nil, hints: nil).map {
                context?.draw($0, in: tail.frame.applying(CGAffineTransform(scaleX: k, y: k)))
            }
        }
        return pixels
    }()

    func contains(screenPoint p: CGPoint) -> Bool {
        guard let window else { return false }
        let k = bounds.width / Self.baseSize.width
        let x = Int((p.x - window.frame.minX) / k), y = Int(Self.baseSize.height - (p.y - window.frame.minY) / k)
        guard x >= 0, y >= 0, x < Int(Self.baseSize.width), y < Int(Self.baseSize.height) else { return false }
        return hitMap[y * Int(Self.baseSize.width) + x] > 25
    }

    override func acceptsFirstMouse(for _: NSEvent?) -> Bool { true }
    override func mouseDown(with event: NSEvent) { window?.performDrag(with: event) }

    /// 光标在猫身上时，全局监听收不到事件，改由跟踪区接收。
    override func updateTrackingAreas() {
        trackingAreas.forEach(removeTrackingArea)
        addTrackingArea(NSTrackingArea(rect: .zero, options: [.mouseMoved, .activeAlways, .inVisibleRect], owner: self))
    }

    // MARK: 键盘

    func key(_ code: UInt16, down: Bool) {
        if InputMonitor.isSecure, !isPreview { return }
        guard let key = Keyboard.key(code) else { return }
        if !down { return release(code) }
        guard !held.contains(code) else { return }
        wake()
        noteTyping()
        held.append(code)
        if key.hand == .either { spaceHand = spaceHand == .left ? .right : .left }
        light(key.code, true)
        if showsBubbles, Keyboard.modifiers[code] == nil { bubble(chord(key.label), above: key) }
        updateArms()
    }

    private func noteTyping() {
        lastKey = Date()
        if !isPreview { keyTimes.append(lastKey) }
        updateMood()
        holdTail()
    }

    private static func lifted(_ p: CGPoint) -> CGPoint { CGPoint(x: p.x, y: p.y + 40) }

    /// 隐私模式：只知道有人敲了一下。用哪只手、落在哪里都是随机的，不亮键、不冒气泡。
    func tapAnonymously() {
        guard !secure, isPreview || !InputMonitor.isSecure else { return }  // 连密码的长度和节奏也不显示
        wake()
        noteTyping()
        if Int.random(in: 0..<4) > 0 { tapHand = tapHand == .left ? .right : .left }
        let hand = tapHand, p = Self.tapSpots[hand]!.randomElement()!
        tapped[hand] = p
        updateArms()
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.1) { [self] in
            guard tapped[hand] == p else { return }
            tapped[hand] = nil
            updateArms()
        }
    }

    /// 字母区各键的位置，按手分组。
    private static let tapSpots: [Hand: [CGPoint]] = Dictionary(
        grouping: Keyboard.keys.values.filter { $0.label.count == 1 && $0.label.first!.isLetter && $0.label.first!.isASCII }, by: \.hand
    ).mapValues { $0.map(\.center) }

    private func release(_ code: UInt16) {
        guard let i = held.firstIndex(of: code), let key = Keyboard.key(code) else { return }
        held.remove(at: i)
        if !held.contains(where: { Keyboard.key($0)?.code == key.code }) { light(key.code, false) }
        updateArms()
    }

    func releaseAll() {
        tapped = [:]
        held.reversed().forEach(release)
    }

    /// 抹掉刚才按键的痕迹：松开所有键、清掉气泡、爪子回到起手位。
    func forgetKeys() {
        releaseAll()
        withoutAnimation { bubbles.sublayers?.forEach { $0.removeFromSuperlayer() } }
        rest()
    }

    private func rest() {
        (leftRest, rightRest) = (Self.leftHome, Self.rightHome)
        updateArms()
    }

    private func chord(_ label: String) -> String {
        let symbols: [(CGEventFlags, String)] = [(.maskControl, "⌃"), (.maskAlternate, "⌥"), (.maskShift, "⇧"), (.maskCommand, "⌘")]
        let flags = held.compactMap { Keyboard.modifiers[$0] }
        return symbols.filter { flags.contains($0.0) }.map(\.1).joined() + label
    }

    private func light(_ code: UInt16, _ on: Bool) {
        CATransaction.begin()
        CATransaction.setAnimationDuration(on ? 0.03 : 0.3)
        glows[code]?.opacity = on ? 0.75 : 0
        CATransaction.commit()
    }

    private func updateArms() {
        /// 这只手最后按住的键；空格按本次轮到的手偏向一侧。
        func target(_ hand: Hand) -> CGPoint? {
            held.compactMap(Keyboard.key).last { ($0.hand == .either ? spaceHand : $0.hand) == hand }.map {
                $0.hand == .either ? CGPoint(x: $0.center.x + (hand == .left ? 50 : -60), y: $0.center.y) : $0.center
            } ?? tapped[hand]
        }
        if let p = target(.right) {
            right.reach(p, duration: 0.05)
            rightRest = Self.lifted(p)
        } else {
            right.reach(rightRest, duration: 0.14)
        }
        if let p = target(.left) {
            leftOnMouse = false
            showLeftArm()
            left.reach(p, duration: 0.05)
            leftRest = Self.lifted(p)
        } else if !leftOnMouse {
            left.reach(leftRest, duration: 0.14)
        }
    }

    private func bubble(_ text: String, above key: Key) {
        let p = key.center, pop = enabled(.pop)
        if bubbles.sublayers?.count ?? 0 > 7 { bubbles.sublayers?.first?.removeFromSuperlayer() }
        let b = CALayer()
        let width = max(96, CGFloat(text.count) * 42 + 48)
        b.add(CGPath(roundedRect: CGRect(x: -width / 2, y: -46, width: width, height: 92), cornerWidth: 30, cornerHeight: 30, transform: nil),
              fill: pop ? NSColor(key.tint) : paper, stroke: ink, width: 6)
        b.add(text: text, size: 54, color: ink)
        float(b, from: CGPoint(x: max(p.x, width / 2 + 8), y: p.y + 130), rise: 120, life: 0.8, pop: pop)
    }

    private func float(_ b: CALayer, from p: CGPoint, rise: CGFloat, life: CFTimeInterval, pop: Bool = false) {
        withoutAnimation {
            b.position = CGPoint(x: p.x, y: p.y + rise)
            b.opacity = 0
            bubbles.addSublayer(b)
            CATransaction.setCompletionBlock { b.removeFromSuperlayer() }
            let up = CABasicAnimation(keyPath: "position.y")
            up.fromValue = p.y
            let fade = CAKeyframeAnimation(keyPath: "opacity")
            fade.values = [1, 1, 0]
            let group = CAAnimationGroup()
            group.animations = [up, fade]
            if pop {  // 弹出并略微飘移
                let scale = CAKeyframeAnimation(keyPath: "transform.scale")
                scale.values = [0.6, 1.12, 1, 1]
                scale.keyTimes = [0, 0.1, 0.22, 1]
                let drift = CABasicAnimation(keyPath: "position.x")
                drift.byValue = CGFloat.random(in: -26...26)
                group.animations! += [scale, drift]
            }
            group.duration = life
            group.timingFunction = CAMediaTimingFunction(name: .easeOut)
            b.add(group, forKey: nil)
        }
    }

    // MARK: 鼠标

    func mouse(_ event: NSEvent) {
        switch event.type {
        case .leftMouseDown, .leftMouseUp: press(0, event.type == .leftMouseDown)
        case .rightMouseDown, .rightMouseUp: press(1, event.type == .rightMouseDown)
        case .scrollWheel: scroll(event.scrollingDeltaY)
        default: break
        }
        guard Date().timeIntervalSince(lastMouseUpdate) > 1 / 30 else { return }
        lastMouseUpdate = Date()
        follow(NSEvent.mouseLocation)
    }

    func press(_ button: Int, _ down: Bool) {
        wake()
        holdTail()
        grabMouse()
        CATransaction.begin()
        CATransaction.setAnimationDuration(down ? 0.02 : 0.25)
        buttons[button].opacity = down ? 1 : 0
        CATransaction.commit()
        mouser.reach(mousePaw, duration: down ? 0.05 : 0.14)
    }

    func scroll(_ delta: CGFloat) {
        guard abs(delta) > 0.5, Date().timeIntervalSince(lastScroll) > 0.18 else { return }
        lastScroll = Date()
        wake()
        grabMouse()
        let a = CABasicAnimation(keyPath: "opacity")
        a.fromValue = 1
        a.toValue = 0
        a.duration = 0.4
        wheel.add(a, forKey: "flash")
        let up = delta > 0
        let b = CALayer()
        b.add(CGPath(ellipseIn: CGRect(x: -34, y: -34, width: 68, height: 68), transform: nil), fill: paper, stroke: ink, width: 5)
        b.add(text: up ? "↑" : "↓", size: 46, color: ink)
        float(b, from: CGPoint(x: mouse.position.x - 120, y: mouse.position.y - 10 + (up ? 0 : 40)), rise: up ? 50 : -50, life: 0.5)
    }

    /// 猫的左手空着时回到鼠标上。
    private func grabMouse() {
        guard !leftOnMouse, tapped[.left] == nil, !held.compactMap(Keyboard.key).contains(where: { $0.hand == .left }) else { return }
        leftOnMouse = true
        showLeftArm()
        mouser.reach(mousePaw, duration: 0)
    }

    private func follow(_ p: CGPoint) {
        guard let screen = NSScreen.screens.first(where: { $0.frame.contains(p) })?.frame else { return }
        slideMouse(to: CGPoint(x: (p.x - screen.minX) / screen.width, y: (p.y - screen.minY) / screen.height))
    }

    func slideMouse(to p: CGPoint) {
        wake()
        CATransaction.begin()
        CATransaction.setAnimationDuration(0.06)
        moveMouse(to: p)
        CATransaction.commit()
        mouser.reach(mousePaw, duration: 0.06)
        grabMouse()
    }

    // MARK: 神态

    private func enabled(_ f: Feature) -> Bool { isPreview || f.isOn }

    /// 眯眼、腮红、汗滴，默认透明。
    private func buildMood() {
        squints.forEach(mood.addSublayer)
        for box in [CGRect(x: 432, y: 342, width: 48, height: 24), CGRect(x: 614, y: 350, width: 52, height: 24)] {
            blush.add(CGPath(ellipseIn: CGRect(origin: Scene.at(box.minX, box.maxY), size: box.size), transform: nil), fill: NSColor(0xEB755F))
        }
        let drop = CGMutablePath()
        drop.move(to: CGPoint(x: 0, y: 26))
        drop.addCurve(to: CGPoint(x: 0, y: -24), control1: CGPoint(x: 22, y: -2), control2: CGPoint(x: 18, y: -24))
        drop.addCurve(to: CGPoint(x: 0, y: 26), control1: CGPoint(x: -18, y: -24), control2: CGPoint(x: -22, y: -2))
        sweat.add(drop, fill: NSColor(0x9EDDF5), stroke: ink, width: 5)
        sweat.add(CGPath(ellipseIn: CGRect(x: -7, y: -8, width: 7, height: 12), transform: nil), fill: .white)
        sweat.position = Scene.at(716, 246)
        (squints + [blush, sweat]).forEach { $0.opacity = 0 }
        mood.addSublayer(blush)
        mood.addSublayer(sweat)
        stage.addSublayer(mood)
    }

    private func updateMood() {
        guard !isPreview else { return }
        let now = Date()
        keyTimes.removeAll { now.timeIntervalSince($0) > 1.2 }
        showMood(speed: secure ? 0 : Double(keyTimes.count) / 1.2)
    }

    /// 打字越快神情越紧：每秒 5 键起眯眼，7.5 键起脸红冒汗，9 键起兴奋抖动；可按灵敏度整体缩放。
    func showMood(speed rawSpeed: Double) {
        let scale = isPreview ? 1 : UserDefaults.standard.object(forKey: "moodScale") as? Double ?? 1
        let speed = rawSpeed / scale
        // 回落要比触发再低 1 键，避免来回跳
        let thresholds = [5, 7.5, 9]
        moodLevel = thresholds.enumerated().filter { speed >= $1 - ($0 < moodLevel ? 1 : 0) }.count
        let on = enabled(.mood)
        let squint = !on || moodLevel == 0 ? nil : min(moodLevel, 2) - 1
        let heat: Float = on && moodLevel >= 2 ? 1 : 0
        for (i, layer) in squints.enumerated() {  // 眼形不同，快切以免重影
            CATransaction.begin()
            CATransaction.setAnimationDuration(0.08)
            layer.opacity = i == squint ? 1 : 0
            CATransaction.commit()
        }
        CATransaction.begin()
        CATransaction.setAnimationDuration(0.35)
        blush.opacity = heat * 0.32
        sweat.opacity = heat
        CATransaction.commit()

        func loop(_ layer: CALayer, _ key: String, active: Bool, _ make: () -> CAAnimation) {
            if !active { layer.removeAnimation(forKey: key) } else if layer.animation(forKey: key) == nil { layer.add(make(), forKey: key) }
        }
        loop(sweat, "bob", active: heat > 0) {
            let a = CABasicAnimation(keyPath: "position.y")
            a.byValue = -12
            a.duration = 0.35
            a.autoreverses = true
            a.repeatCount = .infinity
            return a
        }
        loop(stage, "shake", active: moodLevel >= 3 && enabled(.shake)) {
            let a = CAKeyframeAnimation(keyPath: "position.x")
            a.values = [0, 0.8, 0, -0.8, 0]
            a.isAdditive = true
            a.duration = 0.12
            a.repeatCount = .infinity
            return a
        }
    }

    /// 甩两下尾巴：主摆加一点尾尖抖动，首尾平滑。
    func swish() {
        guard tail.animation(forKey: "swish") == nil else { return }
        let a = CAKeyframeAnimation(keyPath: "transform.rotation.z")
        a.values = (0...60).map { i -> CGFloat in
            let t = Double(i) / 60, p = t * 4 * .pi
            return CGFloat(-(10.5 * sin(p) + 3.8 * sin(2 * p + 0.7)) * min(1, min(t, 1 - t) * 6) * .pi / 180)
        }
        a.duration = 3.5
        tail.add(a, forKey: "swish")
    }

    /// 一有操作就收住尾巴。
    private func holdTail() {
        nextWag = Date() + 1.5
        guard tail.animation(forKey: "swish") != nil else { return }
        let back = CABasicAnimation(keyPath: "transform.rotation.z")
        back.fromValue = tail.presentation()?.value(forKeyPath: "transform.rotation.z")
        back.toValue = 0
        back.duration = 0.25
        tail.removeAnimation(forKey: "swish")
        tail.add(back, forKey: "hold")
    }

    func blink() {
        withoutAnimation { face.opacity = 1 }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.13) { [self] in withoutAnimation { face.opacity = napping ? 1 : 0 } }
    }

    func floatZ() {
        let z = CALayer()
        z.add(text: "z", size: 64, color: paper)
        float(z, from: Scene.at(800, 210), rise: 120, life: 2.5)
    }

    /// 演示结束后复原。
    func settle() {
        releaseAll()
        buttons.indices.forEach { press($0, false) }
        if secure { setSecure(false) }
        wake()
        showMood(speed: 0)
        rest()
        grabMouse()
    }

    /// 每 0.3 秒一次：密码捂眼、补漏抬键、眨眼、甩尾、打盹。
    func heartbeat() {
        let now = Date()
        if secure != InputMonitor.isSecure { setSecure(!secure) }
        updateMood()
        if secure { return }
        for code in held where !CGEventSource.keyState(.combinedSessionState, key: CGKeyCode(code)) { release(code) }

        if held.isEmpty, now.timeIntervalSince(lastKey) > 1, leftRest != Self.leftHome || rightRest != Self.rightHome {
            rest()  // 停手后回到起手位
        }
        for i in buttons.indices where buttons[i].opacity > 0 && NSEvent.pressedMouseButtons & (1 << i) == 0 { press(i, false) }

        let napAfter = UserDefaults.standard.object(forKey: "nap") as? Double ?? 90
        if napAfter > 0, held.isEmpty, now.timeIntervalSince(lastActivity) > napAfter { setNapping(true) }
        if napping {
            guard now > nextZ else { return }
            nextZ = now + 3
            floatZ()
            return
        }
        if now > nextBlink {
            nextBlink = now + .random(in: 3...6)
            blink()
        }
        if now > nextWag, held.isEmpty, enabled(.tail) {
            nextWag = now + 3.5 + .random(in: 0.5...2.5)
            swish()
        }
    }

    func setSecure(_ on: Bool) {
        releaseAll()
        wake()
        secure = on
        withoutAnimation {
            arms.isHidden = on
            if on { bubbles.sublayers?.forEach { $0.removeFromSuperlayer() } }
        }
        setPose(on ? .cover : .normal)
    }

    private func wake() {
        lastActivity = Date()
        setNapping(false)
    }

    func setNapping(_ on: Bool) {
        guard napping != on else { return }
        napping = on
        face.opacity = on ? 1 : 0
    }
}
