import AppKit
import Carbon.HIToolbox

/// 读取按键的监听（隐私模式下键盘部分不启用）。只读，不记录、不落盘；但按了什么键会由小猫显示在屏幕上。
final class InputMonitor {
    var onKey: (UInt16, Bool) -> Void = { _, _ in }
    var onMouse: (NSEvent) -> Void = { _ in }

    private var tap: CFMachPort?
    private var mouseMonitors: [Any] = []
    private var heldModifiers: Set<UInt16> = []

    /// 系统处于安全输入状态（一般的密码框会开启；ssh 会话内、部分编辑器终端、远程桌面等不会）。
    static var isSecure: Bool { IsSecureEventInputEnabled() }
    var isListening: Bool { tap != nil }

    /// 鼠标无需授权。
    func startMouse() {
        stopMouse()
        let mask: NSEvent.EventTypeMask = [
            .mouseMoved, .leftMouseDragged, .rightMouseDragged, .scrollWheel,
            .leftMouseDown, .leftMouseUp, .rightMouseDown, .rightMouseUp,
        ]
        let global = NSEvent.addGlobalMonitorForEvents(matching: mask) { [weak self] in self?.onMouse($0) }
        let local = NSEvent.addLocalMonitorForEvents(matching: mask) { [weak self] in self?.onMouse($0); return $0 }
        mouseMonitors = [global, local].compactMap { $0 }
    }

    func stopMouse() {
        mouseMonitors.forEach(NSEvent.removeMonitor)
        mouseMonitors = []
    }

    /// 键盘需要「输入监控」授权；未授权时什么都不做。
    func startKeys() {
        guard tap == nil, CGPreflightListenEventAccess() else { return }
        let types: [CGEventType] = [.keyDown, .keyUp, .flagsChanged]
        let mask = types.reduce(CGEventMask(0)) { $0 | 1 << $1.rawValue }
        let callback: CGEventTapCallBack = { _, type, event, info in
            Unmanaged<InputMonitor>.fromOpaque(info!).takeUnretainedValue().handle(type, event)
            return Unmanaged.passUnretained(event)
        }
        guard let port = CGEvent.tapCreate(tap: .cgSessionEventTap, place: .tailAppendEventTap,
                                           options: .listenOnly, eventsOfInterest: mask, callback: callback,
                                           userInfo: Unmanaged.passUnretained(self).toOpaque()) else { return }
        CFRunLoopAddSource(CFRunLoopGetMain(), CFMachPortCreateRunLoopSource(nil, port, 0), .commonModes)
        tap = port
    }

    func stopKeys() {
        tap.map(CFMachPortInvalidate)
        tap = nil
        heldModifiers = []
    }

    private func handle(_ type: CGEventType, _ event: CGEvent) {
        if type == .tapDisabledByTimeout, let tap { CGEvent.tapEnable(tap: tap, enable: true) }
        if type == .tapDisabledByUserInput { stopKeys() }  // 由 syncListening 决定要不要重建
        guard type == .keyDown || type == .keyUp || type == .flagsChanged,
              event.getIntegerValueField(.keyboardEventAutorepeat) == 0 else { return }  // 忽略长按连发
        let code = UInt16(event.getIntegerValueField(.keyboardEventKeycode))
        guard type == .flagsChanged else { return onKey(code, type == .keyDown) }

        if let flag = Keyboard.modifiers[code] {
            // 左右同名修饰键共享标志位，因此按「是否已按住」判断抬起。
            let down = !heldModifiers.contains(code) && event.flags.contains(flag)
            if down { heldModifiers.insert(code) } else { heldModifiers.remove(code) }
            onKey(code, down)
        } else if code == UInt16(kVK_CapsLock) {
            onKey(code, true)
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.12) { self.onKey(code, false) }
        }
    }
}
