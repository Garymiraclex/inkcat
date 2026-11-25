import Foundation

/// 设置窗口里可播放的动作。
enum Demo: String, CaseIterable, Identifiable {
    case tail = "甩尾巴", focus = "专注眯眼", fluster = "脸红冒汗", shake = "兴奋抖动", pop = "彩色气泡"
    case secret = "隐私模式打字"
    case typing = "左右手打字", chord = "快捷键", space = "空格轮换", move = "移动鼠标", left = "左键", right = "右键", wheel = "滚轮"
    case blink = "眨眼", nap = "打盹", cover = "输密码捂眼"

    static let added: [Demo] = [.secret, .tail, .focus, .fluster, .shake, .pop]
    static let original = allCases.filter { !added.contains($0) }

    var id: String { rawValue }

    var detail: String {
        switch self {
        case .secret: return "只有敲击节奏，落点随机，不亮键、不冒气泡"
        case .tail: return "空闲时自然甩尾，一打字就收住"
        case .focus: return "每秒 5 键以上，眼睛微微眯起"
        case .fluster: return "每秒 7.5 键以上，眯得更紧，脸红冒汗"
        case .shake: return "每秒 9 键以上，全身微微抖动"
        case .pop: return "气泡弹出并飘移，按键种类不同颜色不同"
        case .typing: return "按标准指法分工，爪子落到具体的键"
        case .chord: return "气泡显示组合键"
        case .space: return "两只手轮流按空格"
        case .move: return "鼠标在桌垫上跟手移动"
        case .left, .right: return "对应半边亮起并弹出火花"
        case .wheel: return "滚轮亮起，气泡显示方向"
        case .blink: return "每 3–6 秒一次"
        case .nap: return "长时间没有操作时睡着"
        case .cover: return "系统开启密码保护时捂眼；ssh、部分终端不会触发"
        }
    }

    @MainActor
    func play(on cat: CatView) async throws {
        func wait(_ seconds: Double) async throws { try await Task.sleep(nanoseconds: UInt64(seconds * 1e9)) }
        func tap(_ codes: [UInt16], every gap: Double) async throws {
            for code in codes {
                cat.key(code, down: true)
                try await wait(gap * 0.55)
                cat.key(code, down: false)
                try await wait(gap * 0.45)
            }
        }
        let text: [UInt16] = [4, 14, 37, 37, 31, 49, 46, 31, 17, 32, 0, 45]  // hello motuan
        switch self {
        case .tail:
            cat.swish()
            try await wait(3.6)
        case .focus:
            cat.showMood(speed: 6)
            try await tap(Array(text.prefix(8)), every: 0.28)
        case .fluster:
            cat.showMood(speed: 8)
            try await tap(text + text.prefix(4), every: 0.17)
        case .shake:
            cat.showMood(speed: 10)
            try await tap(text + text, every: 0.12)
        case .pop: try await tap([0, 53, 48, 38, 36, 124], every: 0.45)
        case .secret:
            for _ in 0..<14 {
                cat.tapAnonymously()
                try await wait(0.22)
            }
        case .typing: try await tap(text, every: 0.3)
        case .chord:
            for code in [8, 9] as [UInt16] {
                cat.key(55, down: true)
                try await wait(0.2)
                try await tap([code], every: 0.5)
                cat.key(55, down: false)
                try await wait(0.3)
            }
        case .space: try await tap([49, 49, 49, 49], every: 0.5)
        case .move:
            for i in 0...80 {
                let a = Double(i) / 80 * 2 * .pi
                cat.slideMouse(to: CGPoint(x: 0.5 + 0.5 * sin(a), y: 0.5 - 0.5 * sin(2 * a)))
                try await wait(0.035)
            }
            cat.slideMouse(to: CGPoint(x: 0.5, y: 0.5))
        case .left, .right:
            for _ in 0..<3 {
                cat.press(self == .left ? 0 : 1, true)
                try await wait(0.25)
                cat.press(self == .left ? 0 : 1, false)
                try await wait(0.35)
            }
        case .wheel:
            for delta in [1, 1, 1, -1, -1, -1] as [CGFloat] {
                cat.scroll(delta)
                try await wait(0.35)
            }
        case .blink:
            for _ in 0..<2 {
                cat.blink()
                try await wait(0.7)
            }
        case .nap:
            cat.setNapping(true)
            cat.floatZ()
            try await wait(1.6)
            cat.floatZ()
            try await wait(2)
        case .cover:
            cat.setSecure(true)
            try await wait(2)
        }
    }
}
