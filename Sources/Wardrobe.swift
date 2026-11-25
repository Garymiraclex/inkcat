import SwiftUI

/// 当前的装扮：每个部位选了哪个素材包。改选择、改位置、改素材文件都会重新合成，猫和设置窗口跟着更新。
final class Wardrobe: ObservableObject {
    static let shared = Wardrobe()

    @Published private(set) var packs = Library.scan()
    @Published private(set) var problems: [Slot: String] = [:]
    @Published private(set) var warnings: [Slot: String] = [:]
    @Published private(set) var busy = false
    private(set) var look = Look()
    /// 换好装后通知；第一次也会通知。
    var onChange: [(Look) -> Void] = []

    private let defaults = UserDefaults.standard
    private let composer = Composer(), queue = DispatchQueue(label: "InkCat.wardrobe", qos: .userInitiated)
    private var turn = 0, watch: FolderWatch?

    private init() {
        if defaults.string(forKey: "skin") == "gamer" {  // 1.1 的「电竞」
            for slot in [Slot.keyboard, .pad, .mouse, .headset] { defaults.set("gamer", forKey: key(slot)) }
        }
        defaults.removeObject(forKey: "skin")
    }

    func start() {
        Library.prepare()
        watch = FolderWatch(Library.folder) { [weak self] in self?.rescan() }
        rescan()
    }

    private func rescan() {
        packs = Library.scan()
        refresh()
    }

    // MARK: 选择

    private func key(_ slot: Slot) -> String { "wear." + slot.rawValue }

    /// 选中的素材包；配件没选时为空串。
    func choice(_ slot: Slot) -> String { defaults.string(forKey: key(slot)) ?? (slot.isOptional ? "" : Pack.classic) }

    func choose(_ slot: Slot, _ id: String) {
        objectWillChange.send()
        defaults.set(id, forKey: key(slot))
        refresh()
    }

    func packs(for slot: Slot) -> [Pack] { packs.filter { $0.slots.contains(slot) } }
    func pack(_ slot: Slot) -> Pack? { packs.first { $0.id == choice(slot) && $0.slots.contains(slot) } }

    /// 一次换成同一个素材包里有的所有部位，其余恢复原版。
    func wear(all pack: Pack) {
        objectWillChange.send()
        for slot in Slot.allCases { defaults.set(pack.slots.contains(slot) ? pack.id : slot.isOptional ? "" : Pack.classic, forKey: key(slot)) }
        refresh()
    }

    // MARK: 位置：自己的素材写进 skin.json，跟着素材包走；内置的记在设置里

    func place(_ slot: Slot) -> Place {
        guard slot != .cat, let pack = pack(slot) else { return Place() }
        if !pack.isBuiltin { return pack.config.place?[slot.rawValue] ?? Place() }
        return defaults.data(forKey: "place.\(slot.rawValue).\(pack.id)").flatMap { try? JSONDecoder().decode(Place.self, from: $0) } ?? Place()
    }

    func move(_ slot: Slot, to place: Place) {
        guard let i = packs.firstIndex(where: { $0.id == choice(slot) }), slot != .cat else { return }
        let pack = packs[i]
        if pack.isBuiltin {
            defaults.set(try? JSONEncoder().encode(place), forKey: "place.\(slot.rawValue).\(pack.id)")
        } else {
            var config = pack.config
            config.place = (config.place ?? [:]).merging([slot.rawValue: place]) { $1 }
            packs[i] = Pack(id: pack.id, title: pack.title, folder: pack.folder, slots: pack.slots, config: config, problem: pack.problem)
            Library.writeConfig(config, to: pack)
        }
        objectWillChange.send()
        refresh()
    }

    // MARK: 合成

    private func refresh() {
        turn += 1
        let mine = turn, fallback = packs.first { $0.id == Pack.classic }!
        var outfit: [Slot: Composer.Choice] = [:], missing: [Slot: String] = [:]
        for slot in Slot.allCases {
            if let pack = pack(slot) { outfit[slot] = .init(pack: pack, place: place(slot)) }
            else if !choice(slot).isEmpty { missing[slot] = "素材包「\(choice(slot).replacingOccurrences(of: "user/", with: ""))」里没有\(slot.title)" }
            if outfit[slot] == nil, !slot.isOptional { outfit[slot] = .init(pack: fallback, place: Place()) }
        }
        busy = true
        queue.async { [self] in
            guard mine == DispatchQueue.main.sync(execute: { turn }) else { return }  // 连续拖动时只算最后一次
            let result = composer.compose(outfit, fallback: fallback)
            DispatchQueue.main.async { [self] in
                guard mine == turn else { return }
                busy = false
                (problems, warnings) = (result.problems.merging(missing) { a, _ in a }, result.warnings)
                look = result.look
                onChange.forEach { $0(look) }
            }
        }
    }
}
