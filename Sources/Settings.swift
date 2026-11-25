import SwiftUI

private struct Preview: NSViewRepresentable {
    let cat: CatView
    func makeNSView(context: Context) -> CatView { cat }
    func updateNSView(_: CatView, context: Context) {}
}

/// 逐个播放动作；新的播放会打断旧的。
private final class Player: ObservableObject {
    let cat = CatView(preview: true, look: Wardrobe.shared.look)
    @Published private(set) var current: Demo?
    private var task: Task<Void, Never>?

    func play(_ demos: [Demo]) {
        let previous = task
        previous?.cancel()
        task = Task { @MainActor in
            await previous?.value
            for demo in demos where !Task.isCancelled {
                current = demo
                try? await demo.play(on: cat)
                cat.settle()
                try? await Task.sleep(nanoseconds: 450_000_000)
            }
            if !Task.isCancelled { current = nil }
        }
    }

    func stop() {
        task?.cancel()
        current = nil
    }
}

private struct SettingsView: View {
    private enum Tab { case look, moves }

    @ObservedObject var player: Player
    let onPrivacyChange: () -> Void
    @State private var tab = Tab.look
    @State private var adjusting: Slot?
    @AppStorage("privacy") private var privacy = true
    @AppStorage("tail") private var tail = true
    @AppStorage("mood") private var mood = true
    @AppStorage("shake") private var shake = true
    @AppStorage("pop") private var pop = true
    @AppStorage("nap") private var nap = 90.0
    @AppStorage("moodScale") private var moodScale = 1.0

    private func toggle(_ demo: Demo) -> Binding<Bool>? {
        switch demo {
        case .tail: return $tail
        case .focus, .fluster: return $mood  // 两种表情共用一个开关
        case .shake: return $shake
        case .pop: return $pop
        default: return nil
        }
    }

    private func row(_ demo: Demo) -> some View {
        HStack(spacing: 10) {
            Button { player.play([demo]) } label: {
                Image(systemName: player.current == demo ? "waveform" : "play.fill").frame(width: 14)
            }
            .help("演示")
            VStack(alignment: .leading, spacing: 1) {
                Text(demo.rawValue)
                Text(demo.detail).font(.caption).foregroundColor(.secondary)
            }
            Spacer()
            if let binding = toggle(demo) {
                Toggle("", isOn: binding).labelsHidden().toggleStyle(.switch).controlSize(.mini)
            }
        }
        .padding(.vertical, 2)
    }

    private var hint: String {
        if let demo = player.current { return "正在演示：\(demo.rawValue)" }
        if let adjusting, tab == .look { return "在图上拖动可以移动\(adjusting.title)" }
        return tab == .look ? "换好后可以点下面的按钮看动作" : "点右侧 ▶ 查看单个动作"
    }

    var body: some View {
        HStack(alignment: .top, spacing: 0) {
            VStack(spacing: 12) {
                Preview(cat: player.cat)
                    .frame(width: 390, height: 291)
                    .overlay { if let adjusting, tab == .look { Dragger(slot: adjusting) } }
                    .background(LinearGradient(colors: [Color(red: 0.80, green: 0.87, blue: 0.91), Color(red: 0.93, green: 0.90, blue: 0.84)],
                                               startPoint: .top, endPoint: .bottom))
                    .clipShape(RoundedRectangle(cornerRadius: 12))
                Text(hint).foregroundColor(.secondary)
                HStack {
                    Button("演示新动作") { player.play(Demo.added) }
                    Button("全部演示") { player.play(Demo.allCases) }
                    Button("停止") { player.stop() }.disabled(player.current == nil)
                }
                Spacer()
                Toggle("隐私模式（不读取按键）", isOn: $privacy).toggleStyle(.switch).controlSize(.small)
                Text(privacy ? "只感知敲击次数和节奏" : "按了什么键会显示在屏幕上（需「输入监控」权限）")
                    .font(.caption).foregroundColor(privacy ? .secondary : .orange).fixedSize()
                Picker("表情灵敏度", selection: $moodScale) {
                    Text("灵敏").tag(0.7)
                    Text("普通").tag(1.0)
                    Text("迟钝").tag(1.3)
                    Text("很迟钝").tag(1.7)
                }
                .frame(width: 220)
                Picker("无操作后打盹", selection: $nap) {
                    Text("30 秒").tag(30.0)
                    Text("90 秒").tag(90.0)
                    Text("3 分钟").tag(180.0)
                    Text("不打盹").tag(0.0)
                }
                .frame(width: 220)
            }
            .padding(16)
            Divider()
            VStack(spacing: 0) {
                Picker("", selection: $tab) {
                    Text("外观").tag(Tab.look)
                    Text("动作").tag(Tab.moves)
                }
                .pickerStyle(.segmented).labelsHidden().padding(10)
                if tab == .look {
                    LookPane(adjusting: $adjusting)
                } else {
                    List {
                        Section("新增动作") { ForEach(Demo.added, content: row) }
                        Section("原有动作") { ForEach(Demo.original, content: row) }
                    }
                }
            }
            .frame(width: 330)
        }
        .frame(height: 570)
        .onChange(of: privacy) { _ in onPrivacyChange() }
    }
}

final class SettingsWindow {
    private let player = Player(), onPrivacyChange: () -> Void

    init(onPrivacyChange: @escaping () -> Void) {
        self.onPrivacyChange = onPrivacyChange
        Wardrobe.shared.onChange.append { [player] in player.cat.look = $0 }
    }

    private lazy var window: NSWindow = {
        let w = NSWindow(contentViewController: NSHostingController(rootView: SettingsView(player: player, onPrivacyChange: onPrivacyChange)))
        w.title = "墨团 · 设置与演示"
        w.styleMask = [.titled, .closable]
        w.isReleasedWhenClosed = false
        w.center()
        NotificationCenter.default.addObserver(forName: NSWindow.willCloseNotification, object: w, queue: .main) { [player] _ in
            player.stop()
        }
        return w
    }()

    func show() {
        NSApp.activate(ignoringOtherApps: true)
        window.makeKeyAndOrderFront(nil)
    }
}
