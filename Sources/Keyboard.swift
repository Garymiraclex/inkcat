import CoreGraphics

/// 场景坐标：1200×896 像素画面，原点左下。素材坐标以左上为原点，经此转换。
enum Scene {
    static let size = CGSize(width: 1200, height: 896)
    static func at(_ x: CGFloat, _ y: CGFloat) -> CGPoint { CGPoint(x: x, y: size.height - y) }
}

struct Key {
    let code: UInt16
    let label: String
    let center: CGPoint
    /// 沿键盘长边的半宽（含方向）。
    let half: CGVector
    /// 标准指法的分工；空格两手轮换。
    let hand: Hand

    /// 气泡配色，随画中键帽：珊瑚、灰绿、米白。
    var tint: Int {
        if [53, 36].contains(code) { return 0xF2A38C }
        return [48, 57, 56, 60, 63, 59, 58, 55, 54, 61, 51, 117, 119, 123, 124, 125, 126].contains(code) ? 0xC7D9C2 : 0xFFF8EC
    }
}

/// 使用者的手，也是猫的手：猫的左手在画面右侧。
enum Hand { case left, right, either }

/// 键盘朝向猫：每排由猫的左手端排到右手端，即画面从右到左。
enum Keyboard {
    /// "键码:键帽:宽度"，宽度缺省为 1，每排合计 15。
    private static let rows = [
        "53:esc:1.5 122:F1 120:F2 99:F3 118:F4 96:F5 97:F6 98:F7 100:F8 101:F9 109:F10 103:F11 111:F12 117:⌦:1.5",
        "50:` 18:1 19:2 20:3 21:4 23:5 22:6 26:7 28:8 25:9 29:0 27:- 24:= 51:⌫:2",
        "48:⇥:1.5 12:Q 13:W 14:E 15:R 17:T 16:Y 32:U 34:I 31:O 35:P 33:[ 30:] 42:\\:1.5",
        "57:⇪:1.75 0:A 1:S 2:D 3:F 5:G 4:H 38:J 40:K 37:L 41:; 39:' 36:⏎:2.25",
        "56:⇧:2 6:Z 7:X 8:C 9:V 11:B 45:N 46:M 43:, 47:. 44:/ 60:⇧ 126:↑ 119:↘",
        "63:fn 59:⌃ 58:⌥ 55:⌘:1.25 49:␣:5.5 54:⌘:1.25 61:⌥ 123:← 125:↓ 124:→",
    ]

    /// 画中 65% 键盘各排的两端；它没有 F 区，与数字排共用一排。
    private static let ends: [(CGPoint, CGPoint)] = [
        (Scene.at(600, 722), Scene.at(195, 650)),
        (Scene.at(600, 722), Scene.at(195, 650)),
        (Scene.at(628, 702), Scene.at(222, 634)),
        (Scene.at(652, 682), Scene.at(248, 619)),
        (Scene.at(672, 667), Scene.at(270, 604)),
        (Scene.at(692, 655), Scene.at(297, 592)),
    ]

    static let keys: [UInt16: Key] = {
        var result: [UInt16: Key] = [:]
        for (row, (a, b)) in zip(rows, ends) {
            let unit = CGVector(dx: (b.x - a.x) / 15, dy: (b.y - a.y) / 15)
            var u: CGFloat = 0
            for item in row.split(separator: " ") {
                let f = item.split(separator: ":")
                let width = CGFloat(f.count > 2 ? Double(f[2])! : 1)
                let code = UInt16(f[0])!, mid = u + width / 2
                result[code] = Key(code: code, label: String(f[1]),
                                   center: CGPoint(x: a.x + unit.dx * mid, y: a.y + unit.dy * mid),
                                   half: CGVector(dx: unit.dx * width / 2, dy: unit.dy * width / 2),
                                   hand: code == 49 ? .either : mid < 6.9 ? .left : .right)
                u += width
            }
        }
        return result
    }()

    /// 小键盘与右 Control 映射到主键区。
    private static let aliases: [UInt16: UInt16] = [
        62: 59, 82: 29, 83: 18, 84: 19, 85: 20, 86: 21, 87: 23, 88: 22, 89: 26, 91: 28, 92: 25,
        65: 47, 67: 28, 69: 24, 71: 117, 75: 44, 76: 36, 78: 27, 81: 24,
    ]

    static func key(_ code: UInt16) -> Key? { keys[aliases[code] ?? code] }

    /// 修饰键 → 标志位。
    static let modifiers: [UInt16: CGEventFlags] = [
        55: .maskCommand, 54: .maskCommand, 56: .maskShift, 60: .maskShift,
        58: .maskAlternate, 61: .maskAlternate, 59: .maskControl, 62: .maskControl,
        63: .maskSecondaryFn,
    ]
}
