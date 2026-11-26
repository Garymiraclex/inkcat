import SwiftUI

typealias Pose = Importer.Pose

/// 导入的进度：拖进来的原图、整理结果、预览。
final class ImportModel: ObservableObject {
    @Published private(set) var raw: [Pose: CGImage] = [:]
    @Published private(set) var unreadable: [Pose: String] = [:]
    @Published private(set) var result = Importer.Result()
    @Published private(set) var busy = false
    @Published var name = ""
    @Published var message: (text: String, ok: Bool)?
    let player = Player(look: Look())

    private let composer = Composer(), queue = DispatchQueue(label: "InkCat.import", qos: .userInitiated)
    private var turn = 0
    private let preview = Library.cache.deletingLastPathComponent().appendingPathComponent("import", isDirectory: true)

    var canSave: Bool { result.isReady && !busy && !name.trimmingCharacters(in: .whitespaces).isEmpty }

    func put(_ pose: Pose, _ data: Data) {
        do {
            raw[pose] = try Importer.load(data)
            unreadable[pose] = nil
        } catch {
            raw[pose] = nil
            unreadable[pose] = error.localizedDescription
        }
        refresh()
    }

    func remove(_ pose: Pose) {
        raw[pose] = nil
        unreadable[pose] = nil
        refresh()
    }

    /// 重新整理全部；整套齐了就合成一身，在预览里动起来。
    private func refresh() {
        turn += 1
        message = nil
        let mine = turn, raw = raw, folder = preview, pack = Pack(id: "import", title: "", folder: preview, slots: [.cat], config: PackConfig(), problem: nil)
        let outfit = Wardrobe.shared.outfit(cat: pack).choices, fallback = Wardrobe.shared.classic
        busy = true
        queue.async { [self] in
            guard mine == DispatchQueue.main.sync(execute: { turn }) else { return }
            let result = Importer.process(raw)
            var look: Look?
            if result.isReady {
                let fm = FileManager.default
                try? fm.removeItem(at: folder)
                try? fm.createDirectory(at: folder, withIntermediateDirectories: true)
                for (pose, image) in result.images {
                    try? NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:])?.write(to: folder.appendingPathComponent(pose.rawValue + ".png"))
                }
                look = composer.compose(outfit, fallback: fallback).look
            }
            DispatchQueue.main.async { [self] in
                guard mine == turn else { return }
                busy = false
                self.result = result
                if let look {
                    player.cat.look = look
                    player.play(ImportModel.demos)
                }
            }
        }
    }

    static let demos: [Demo] = [.typing, .cover, .nap, .tail]

    func save() {
        do {
            let id = try Importer.save(result.images, name: name)
            Wardrobe.shared.adopt(id)
            message = ("已存好并换上「\(name.trimmingCharacters(in: .whitespaces))」，在设置的「猫」里也能选", true)
            (raw, unreadable, result, name) = ([:], [:], Importer.Result(), "")
            player.stop()
            player.cat.look = Look()
        } catch {
            message = (error.localizedDescription, false)
        }
    }
}

/// 一个姿势的格子：没有图时显示淡淡的原版作参照。
private struct Tile: View {
    let pose: Pose
    @ObservedObject var model: ImportModel
    @State private var targeted = false
    private static let templates = Dictionary(uniqueKeysWithValues: Pose.allCases.map { ($0, $0.template) })

    private var status: (String, Color)? {
        if let text = model.unreadable[pose] ?? model.result.problems[pose] { return (text, .red) }
        if let text = model.result.notes[pose] { return (text, .secondary) }
        if model.result.images[pose] != nil { return ("可以用", .green) }
        if model.raw[pose] != nil { return ("整理中…", .secondary) }
        return pose.isOptional ? ("右手接身体露馅时再画", .secondary) : nil
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            ZStack {
                RoundedRectangle(cornerRadius: 8).fill(Color.primary.opacity(targeted ? 0.16 : 0.06))
                if let image = model.result.images[pose] ?? model.raw[pose] {
                    Image(decorative: image, scale: 1).resizable().scaledToFit().padding(4)
                } else {
                    if let t = Self.templates[pose] ?? nil { Image(decorative: t, scale: 1).resizable().scaledToFit().padding(4).opacity(0.18) }
                    Text("拖到这里").font(.caption).foregroundColor(.secondary)
                }
                DropTarget(onDrop: { model.put(pose, $0) }, targeted: $targeted)
            }
            .frame(width: 128, height: 96)
            HStack(spacing: 4) {
                Text(pose.title + (pose.isOptional ? "（可选）" : "")).font(.callout)
                Spacer()
                if model.raw[pose] != nil || model.unreadable[pose] != nil {
                    Button { model.remove(pose) } label: { Image(systemName: "xmark.circle.fill") }.buttonStyle(.borderless).foregroundColor(.secondary).help("移除")
                }
            }
            if let (text, color) = status {
                Text(text).font(.caption).foregroundColor(color).fixedSize(horizontal: false, vertical: true)
            }
        }
        .frame(width: 128, alignment: .top)
    }
}

private struct ImportView: View {
    @ObservedObject var model: ImportModel

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("把 AI 画的原图拖进对应的格子，文件名不用管。程序会去掉背景和水印、对齐位置，再检查能不能用；原图不会被改动。")
                .foregroundColor(.secondary).fixedSize(horizontal: false, vertical: true)
            HStack(alignment: .top, spacing: 10) {
                ForEach(Pose.allCases) { Tile(pose: $0, model: model) }
            }
            Divider()
            HStack(alignment: .top, spacing: 16) {
                CatPreview(cat: model.player.cat)
                    .frame(width: 390, height: 291)
                    .opacity(model.result.isReady ? 1 : 0.15)
                    .background(LinearGradient(colors: [Color(red: 0.80, green: 0.87, blue: 0.91), Color(red: 0.93, green: 0.90, blue: 0.84)], startPoint: .top, endPoint: .bottom))
                    .clipShape(RoundedRectangle(cornerRadius: 12))
                    .overlay {
                        if model.busy { ProgressView() }
                        else if !model.result.isReady { Text("四张都能用后，在这里动起来").foregroundColor(.secondary) }
                    }
                VStack(alignment: .leading, spacing: 10) {
                    if let warning = model.result.warning {
                        Text(warning).font(.caption).foregroundColor(.orange).fixedSize(horizontal: false, vertical: true)
                    }
                    Text("名字")
                    TextField("比如：我家的猫", text: $model.name).textFieldStyle(.roundedBorder)
                    HStack {
                        Button("保存并换上") { model.save() }.keyboardShortcut(.defaultAction).disabled(!model.canSave)
                        Button("看动作") { model.player.play(ImportModel.demos) }.disabled(!model.result.isReady || model.busy)
                    }
                    if let message = model.message {
                        Text(message.text).font(.caption).foregroundColor(message.ok ? .green : .red).fixedSize(horizontal: false, vertical: true)
                    }
                    Spacer()
                    Text("图怎么画、给 AI 的提示词，见素材文件夹里的「素材说明」。").font(.caption).foregroundColor(.secondary).fixedSize(horizontal: false, vertical: true)
                    Button("打开素材说明") { NSWorkspace.shared.open(Library.folder.appendingPathComponent("素材说明.txt")) }.controlSize(.small)
                }
                .frame(height: 291)
            }
        }
        .padding(18)
        .frame(width: 726)
    }
}

/// 接住从访达或浏览器拖来的图。用 AppKit 接收，沙盒里也能读拖进来的文件。
private struct DropTarget: NSViewRepresentable {
    let onDrop: (Data) -> Void
    @Binding var targeted: Bool

    final class View: NSView {
        var onDrop: (Data) -> Void = { _ in }
        var onTarget: (Bool) -> Void = { _ in }

        override init(frame: NSRect) {
            super.init(frame: frame)
            registerForDraggedTypes([.fileURL, .png, .tiff])
        }
        required init?(coder: NSCoder) { fatalError() }

        override func draggingEntered(_: NSDraggingInfo) -> NSDragOperation { onTarget(true); return .copy }
        override func draggingExited(_: NSDraggingInfo?) { onTarget(false) }
        override func performDragOperation(_ info: NSDraggingInfo) -> Bool {
            onTarget(false)
            let board = info.draggingPasteboard
            if let url = (board.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL])?.first {
                guard let data = try? Library.read(url) else { return false }
                onDrop(data)
            } else if let data = board.data(forType: .png) ?? board.data(forType: .tiff) {
                onDrop(data)
            } else { return false }
            return true
        }
    }

    func makeNSView(context: Context) -> View { View() }
    func updateNSView(_ view: View, context: Context) {
        view.onDrop = onDrop
        view.onTarget = { value in DispatchQueue.main.async { targeted = value } }
    }
}

final class ImportWindow {
    static let shared = ImportWindow()
    private let model = ImportModel()

    private lazy var window: NSWindow = {
        let w = NSWindow(contentViewController: NSHostingController(rootView: ImportView(model: model)))
        w.title = "墨团 · 导入生图"
        w.styleMask = [.titled, .closable]
        w.isReleasedWhenClosed = false
        w.center()
        NotificationCenter.default.addObserver(forName: NSWindow.willCloseNotification, object: w, queue: .main) { [model] _ in model.player.stop() }
        return w
    }()

    func show() {
        NSApp.activate(ignoringOtherApps: true)
        window.makeKeyAndOrderFront(nil)
    }
}
