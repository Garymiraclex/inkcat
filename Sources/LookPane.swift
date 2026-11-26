import SwiftUI

/// 设置窗口的「外观」页：每个部位选哪个素材包，位置不准就微调。
struct LookPane: View {
    @ObservedObject private var wardrobe = Wardrobe.shared
    @Binding var adjusting: Slot?

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 10) {
                ForEach(Slot.allCases) { slot in
                    row(slot)
                    if adjusting == slot { adjuster(slot) }
                }
                Divider().padding(.vertical, 4)
                HStack {
                    Button("导入生图…") { ImportWindow.shared.show() }
                    Button("打开素材文件夹") { NSWorkspace.shared.open(Library.folder) }
                    Menu("整套换成") {
                        ForEach(wardrobe.packs.filter { !$0.slots.isEmpty }) { pack in
                            Button(pack.title) { wardrobe.wear(all: pack) }
                        }
                    }
                    .fixedSize()
                    if wardrobe.busy { ProgressView().controlSize(.small) }
                }
                Text("用 AI 画了自己的猫，点「导入生图」把原图拖进去就行。也可以把整理好的素材包直接放进文件夹，改图后自动更新。图怎么画，见文件夹里的「素材说明」和「_模板」。")
                    .font(.caption).foregroundColor(.secondary).fixedSize(horizontal: false, vertical: true)
                ForEach(wardrobe.packs.filter { $0.problem != nil }) { pack in
                    Text("「\(pack.title)」\(pack.problem!)").font(.caption).foregroundColor(.orange).fixedSize(horizontal: false, vertical: true)
                }
            }
            .padding(14)
        }
        .onDisappear { adjusting = nil }
    }

    private func row(_ slot: Slot) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            HStack(spacing: 8) {
                Text(slot.title).frame(width: 34, alignment: .leading)
                Picker("", selection: Binding(get: { selection(slot) }, set: {
                    wardrobe.choose(slot, $0)
                    if adjusting == slot { adjusting = nil }
                })) {
                    if slot.isOptional { Text("不戴").tag("") }
                    ForEach(wardrobe.packs(for: slot)) { Text($0.title).tag($0.id) }
                }
                .labelsHidden()
                .frame(maxWidth: .infinity, alignment: .leading)
                Button { adjusting = adjusting == slot ? nil : slot } label: {
                    Image(systemName: "arrow.up.and.down.and.arrow.left.and.right").frame(width: 14)
                }
                .help("调整位置")
                .disabled(slot == .cat || wardrobe.pack(slot) == nil)
                .opacity(slot == .cat ? 0 : 1)
            }
            if slot == .cat, wardrobe.packs(for: .cat).count == 1, wardrobe.problems[.cat] == nil {
                note("现在只有一只猫，把新猫的文件夹放进素材文件夹就能选", .secondary)
            }
            if let problem = wardrobe.problems[slot] {
                note(problem, .red)
            } else if let warning = wardrobe.warnings[slot] {
                note(warning, .orange)
            }
        }
    }

    /// 选中的素材包不在了，下拉框显示实际用的。
    private func selection(_ slot: Slot) -> String { wardrobe.pack(slot)?.id ?? (slot.isOptional ? "" : Pack.classic) }

    private func note(_ text: String, _ color: Color) -> some View {
        Text(text).font(.caption).foregroundColor(color).fixedSize(horizontal: false, vertical: true).padding(.leading, 42)
    }

    private func adjuster(_ slot: Slot) -> some View {
        func bind(_ key: WritableKeyPath<Place, Double>) -> Binding<Double> {
            Binding(get: { wardrobe.place(slot)[keyPath: key] }, set: {
                var p = wardrobe.place(slot)
                p[keyPath: key] = $0
                wardrobe.move(slot, to: p)
            })
        }
        return VStack(alignment: .leading, spacing: 4) {
            HStack {
                Text("大小").frame(width: 30, alignment: .leading)
                Slider(value: bind(\.scale), in: 0.5...1.6)
            }
            HStack {
                Text("角度").frame(width: 30, alignment: .leading)
                Slider(value: bind(\.angle), in: -45...45)
            }
            Button("恢复原位") { wardrobe.move(slot, to: Place()) }.controlSize(.small)
        }
        .font(.caption)
        .padding(8)
        .background(RoundedRectangle(cornerRadius: 6).fill(Color.primary.opacity(0.05)))
        .padding(.leading, 42)
    }
}

/// 盖在预览上，拖动时移动选中的部位。
struct Dragger: View {
    let slot: Slot
    @State private var start: Place?

    var body: some View {
        Color.white.opacity(0.001).gesture(DragGesture(minimumDistance: 1).onChanged { g in
            let from = start ?? Wardrobe.shared.place(slot)
            start = from
            var p = from
            p.x = (from.x + g.translation.width * 1200 / 390).rounded()
            p.y = (from.y + g.translation.height * 1200 / 390).rounded()
            Wardrobe.shared.move(slot, to: p)
        }.onEnded { _ in start = nil })
    }
}
