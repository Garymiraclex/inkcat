import CoreGraphics
import Foundation

/// 隐私模式的输入来源：只读系统累计的按键次数（长按连发不计）。拿不到是哪个键，也不需要键盘权限。
final class KeyCounter {
    var onTap: () -> Void = {}

    private var timer: Timer?
    private var last: UInt32 = 0, wasSecure = false, lastTap = Date.distantPast

    private static var count: UInt32 { CGEventSource.counterForEventType(.combinedSessionState, eventType: .keyDown) }

    func start() {
        guard timer == nil else { return }
        (last, wasSecure) = (Self.count, InputMonitor.isSecure)
        schedule()
    }

    func stop() {
        timer?.invalidate()
        timer = nil
    }

    /// 打字时每秒 30 次，空闲时每秒 10 次。
    private func schedule() {
        let t = Timer(timeInterval: Date().timeIntervalSince(lastTap) < 2 ? 1 / 30 : 0.1, repeats: false) { [weak self] _ in self?.poll() }
        RunLoop.main.add(t, forMode: .common)
        timer = t
    }

    private func poll() {
        let now = Self.count, secure = InputMonitor.isSecure
        let tapped = now != last, visible = !secure && !wasSecure  // 密码的最后一下可能在安全输入刚结束时才数到
        (last, wasSecure) = (now, secure)
        if tapped { lastTap = Date() }
        schedule()
        if tapped, visible { onTap() }
    }
}
