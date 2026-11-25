import AppKit
import CoreServices

/// 可以单独更换的部位。
enum Slot: String, CaseIterable, Identifiable {
    case cat, keyboard, pad, mouse, headset, hat, scarf

    var id: String { rawValue }
    var title: String { ["猫", "键盘", "桌垫", "鼠标", "耳机", "帽子", "围巾"][Slot.allCases.firstIndex(of: self)!] }
    /// 可以不要的配件；其余部位必须有一个。
    var isOptional: Bool { [.headset, .hat, .scarf].contains(self) }
    var files: [String] { self == .cat ? ["idle", "type_both", "sleep", "cover_eyes"] : [rawValue] }
}

/// 对素材位置的微调：偏移（画面像素，向右、向下为正）、缩放和旋转（度，顺时针为正）。
struct Place: Codable, Equatable {
    var x = 0.0, y = 0.0, scale = 1.0, angle = 0.0

    init() {}
    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        func read(_ key: CodingKeys, _ fallback: Double, _ range: ClosedRange<Double>) throws -> Double {
            min(max(try c.decodeIfPresent(Double.self, forKey: key) ?? fallback, range.lowerBound), range.upperBound)
        }
        x = try read(.x, 0, -1200...1200)
        y = try read(.y, 0, -896...896)
        scale = try read(.scale, 1, 0.1...5)
        angle = try read(.angle, 0, -180...180)
    }
}

/// 素材包里可选的 skin.json。坐标都是 1200×896 画面像素，左上原点。
struct PackConfig: Codable {
    struct Buttons: Codable { var left: [[Double]]?, right: [[Double]]?, wheel: [[Double]]? }
    struct Cat: Codable { var scarf: Bool?, joint: [String]? }

    var name: String?
    var place: [String: Place]?
    /// 六排键的两端：x1, y1, x2, y2，从猫的左手端到右手端。
    var keys: [[Double]]?
    var buttons: Buttons?
    var cat: Cat?
}

/// 一个素材包就是一个文件夹，有哪些文件就提供哪些部位。
struct Pack: Identifiable, Equatable {
    let id: String, title: String, folder: URL, slots: [Slot], config: PackConfig, problem: String?
    var isBuiltin: Bool { !id.hasPrefix("user/") }
    static func == (a: Pack, b: Pack) -> Bool { a.id == b.id && a.title == b.title && a.slots == b.slots }

    static let classic = "classic"

    func file(_ name: String) -> URL { folder.appendingPathComponent(name + ".png") }
}

struct Problem: LocalizedError {
    let errorDescription: String?
    init(_ text: String) { errorDescription = text }
}

/// 素材包的所在：内置的在程序里，用户的在程序自己的文件夹里，不需要额外权限。
enum Library {
    static let folder: URL = {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        return base.appendingPathComponent("InkCat/素材", isDirectory: true)
    }()
    static let cache = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0].appendingPathComponent("InkCat/cats", isDirectory: true)
    private static let builtin = Bundle.main.resourceURL!.appendingPathComponent("packs", isDirectory: true)

    static func scan() -> [Pack] {
        func folders(_ root: URL) -> [URL] {
            let all = (try? FileManager.default.contentsOfDirectory(at: root, includingPropertiesForKeys: [.isDirectoryKey, .isSymbolicLinkKey], options: .skipsHiddenFiles)) ?? []
            return all.filter {
                let v = try? $0.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey])
                return v?.isDirectory == true && v?.isSymbolicLink != true && !$0.lastPathComponent.hasPrefix("_")
            }
        }
        let own = [Pack.classic, "gamer"].map { pack(builtin.appendingPathComponent($0, isDirectory: true), id: $0) }
        let users = folders(folder).map { pack($0, id: "user/" + $0.lastPathComponent) }
        return own + users.filter { !$0.slots.isEmpty || $0.problem != nil }.sorted { $0.title.localizedStandardCompare($1.title) == .orderedAscending }
    }

    private static func pack(_ url: URL, id: String) -> Pack {
        let names = Set((try? FileManager.default.contentsOfDirectory(atPath: url.path)) ?? [])
        var problem: String?
        var config = PackConfig()
        if names.contains("skin.json") {
            do { config = try JSONDecoder().decode(PackConfig.self, from: read(url.appendingPathComponent("skin.json"), limit: 1 << 20)) }
            catch { problem = "skin.json 看不懂，已忽略" }
        }
        let cat = Slot.cat.files.filter { !names.contains($0 + ".png") }
        if (1..<4).contains(cat.count) { problem = "猫还缺 " + cat.map { $0 + ".png" }.joined(separator: "、") }
        let slots = Slot.allCases.filter { $0.files.allSatisfy { names.contains($0 + ".png") } }
        return Pack(id: id, title: config.name ?? url.lastPathComponent, folder: url, slots: slots, config: config, problem: problem)
    }

    /// 只读普通文件，不跟随链接，限制大小。
    static func read(_ url: URL, limit: Int = 20 << 20) throws -> Data {
        let name = url.lastPathComponent
        let v = try url.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey, .fileSizeKey])
        guard v.isRegularFile == true, v.isSymbolicLink != true else { throw Problem("\(name) 不是普通文件") }
        guard v.fileSize ?? 0 <= limit else { throw Problem("\(name) 太大，不能超过 \(limit >> 20) MB") }
        return try Data(contentsOf: url)
    }

    /// 读成 1200×896；同比例的大图会缩小。
    static func image(_ data: Data, name: String) throws -> CGImage {
        guard let source = CGImageSourceCreateWithData(data as CFData, nil),
              let info = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any],
              let w = info[kCGImagePropertyPixelWidth] as? Int, let h = info[kCGImagePropertyPixelHeight] as? Int else { throw Problem("\(name).png 打不开") }
        guard w <= 4096, h <= 4096, w >= 600, abs(Double(w) / Double(h) - 1200 / 896.0) < 0.004 else { throw Problem("\(name).png 是 \(w)×\(h)，需要 1200×896") }
        guard let image = CGImageSourceCreateImageAtIndex(source, 0, nil) else { throw Problem("\(name).png 打不开") }
        return image
    }

    static func writeConfig(_ config: PackConfig, to pack: Pack) {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try? encoder.encode(config).write(to: pack.folder.appendingPathComponent("skin.json"), options: .atomic)
    }

    /// 建好素材文件夹，放一份模板；名字以 _ 开头的文件夹不会被当成素材包。
    static func prepare() {
        let fm = FileManager.default, template = folder.appendingPathComponent("_模板", isDirectory: true)
        try? fm.createDirectory(at: folder, withIntermediateDirectories: true)
        if let guide = Bundle.main.url(forResource: "素材说明", withExtension: "txt") {
            let copy = folder.appendingPathComponent("素材说明.txt")
            try? fm.removeItem(at: copy)
            try? fm.copyItem(at: guide, to: copy)
        }
        for name in [Pack.classic, "gamer"] {
            let to = template.appendingPathComponent(name == Pack.classic ? "原版" : "电竞", isDirectory: true)
            if fm.fileExists(atPath: to.path) { continue }
            try? fm.createDirectory(at: template, withIntermediateDirectories: true)
            try? fm.copyItem(at: builtin.appendingPathComponent(name, isDirectory: true), to: to)
        }
    }
}

/// 监听素材文件夹，改动停下来后通知一次。
final class FolderWatch {
    private var stream: FSEventStreamRef?
    private let changed: () -> Void

    init(_ url: URL, changed: @escaping () -> Void) {
        self.changed = changed
        var context = FSEventStreamContext(version: 0, info: Unmanaged.passUnretained(self).toOpaque(), retain: nil, release: nil, copyDescription: nil)
        let callback: FSEventStreamCallback = { _, info, _, _, _, _ in Unmanaged<FolderWatch>.fromOpaque(info!).takeUnretainedValue().changed() }
        stream = FSEventStreamCreate(nil, callback, &context, [url.path] as CFArray, FSEventStreamEventId(kFSEventStreamEventIdSinceNow), 0.5,
                                     FSEventStreamCreateFlags(kFSEventStreamCreateFlagFileEvents))
        if let stream {
            FSEventStreamSetDispatchQueue(stream, .main)
            FSEventStreamStart(stream)
        }
    }

    deinit {
        if let stream {
            FSEventStreamStop(stream)
            FSEventStreamInvalidate(stream)
            FSEventStreamRelease(stream)
        }
    }
}
