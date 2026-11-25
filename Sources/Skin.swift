import AppKit

/// 外观：猫是同一只，只换耳机、键盘、鼠标和桌垫。皮肤里没有的图沿用原版。
enum Skin: String, CaseIterable {
    case classic, gamer

    var title: String { self == .classic ? "原版" : "电竞" }

    static var current: Skin { UserDefaults.standard.string(forKey: "skin").flatMap(Skin.init) ?? .classic }

    func image(_ name: String) -> NSImage? {
        Bundle.main.url(forResource: name, withExtension: "png", subdirectory: rawValue).flatMap(NSImage.init(contentsOf:))
            ?? Bundle.main.image(forResource: name)
    }

    /// 鼠标的左键、右键和滚轮，坐标取自鼠标素材（左上原点）。
    typealias Outline = [(CGFloat, CGFloat)]
    var mouseParts: (left: Outline, right: Outline, wheel: Outline) {
        switch self {
        case .classic: return (
            [(40, 109), (43, 98), (48, 91), (54, 86), (64, 68), (72, 50), (98, 60), (112, 72), (101, 92), (90, 104), (76, 110), (56, 112)],
            [(35, 108), (38, 98), (42, 90), (39, 84), (43, 72), (50, 62), (60, 59), (64, 50), (50, 35), (38, 47), (24, 67), (17, 84), (18, 97), (26, 104)],
            [(42, 87), (42, 80), (47, 71), (54, 64), (62, 64), (62, 69), (56, 79), (51, 87), (46, 89)])
        case .gamer: return (
            [(30, 117), (33, 102), (40, 100), (47, 96), (55, 84), (64, 67), (73, 55), (86, 54), (98, 56), (103, 61), (92, 79), (80, 100), (71, 112), (61, 119), (51, 122), (38, 120)],
            [(26, 115), (30, 101), (28, 93), (31, 83), (37, 72), (43, 61), (48, 51), (49, 46), (42, 43), (33, 42), (25, 48), (17, 60), (11, 72), (5, 85), (4, 96), (8, 104), (16, 110)],
            [(34, 90), (35, 83), (40, 72), (47, 63), (54, 62), (57, 65), (52, 73), (46, 85), (43, 93), (38, 93)])
        }
    }
}
