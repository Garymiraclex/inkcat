import AppKit

final class App: NSObject, NSApplicationDelegate, NSMenuDelegate {
    private let defaults = UserDefaults.standard
    private let input = InputMonitor(), counter = KeyCounter()
    private let cat = CatView()
    private let status = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
    private let panel = NSPanel(contentRect: .zero, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)

    private var dressed = false

    private var scale: CGFloat {
        get { CGFloat(defaults.object(forKey: "scale") as? Double ?? 1) }
        set { defaults.set(Double(newValue), forKey: "scale") }
    }

    func applicationDidFinishLaunching(_: Notification) {
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.level = .floating
        panel.hidesOnDeactivate = false
        panel.canHide = false  // 只由自己的菜单隐藏，隐藏时才能保证停止监听
        panel.ignoresMouseEvents = true  // 光标移到猫身上时才接收
        panel.collectionBehavior = [.canJoinAllSpaces, .stationary, .fullScreenAuxiliary, .ignoresCycle]
        panel.contentView = cat
        cat.showsBubbles = defaults.object(forKey: "bubbles") as? Bool ?? true

        let icon = Bundle.main.image(forResource: "status")
        icon?.size = NSSize(width: 18, height: 18)
        icon?.isTemplate = true
        status.button?.image = icon
        status.menu = NSMenu()
        status.menu?.delegate = self
        cat.menu = status.menu  // 右键猫也能打开

        counter.onTap = { [cat] in cat.tapAnonymously() }
        input.onKey = { [cat] in cat.key($0, down: $1) }
        input.onMouse = { [unowned self] in
            cat.mouse($0)
            updateHitTest()
        }
        NotificationCenter.default.addObserver(forName: NSWindow.didMoveNotification, object: panel, queue: nil) { [unowned self] _ in
            savePosition()
        }
        place(defaults.string(forKey: "origin").map(NSPointFromString))

        Wardrobe.shared.onChange.append { [unowned self] look in
            Keyboard.ends = look.rows
            cat.look = look
            if !dressed {  // 换好装才第一次露面
                dressed = true
                show()
            }
        }
        Wardrobe.shared.start()

        let timer = Timer(timeInterval: 0.3, repeats: true) { [unowned self] _ in
            syncListening()
            if panel.isVisible { cat.heartbeat() }
        }
        timer.tolerance = 0.1
        RunLoop.main.add(timer, forMode: .common)
    }

    // MARK: 窗口

    private func place(_ saved: NSPoint?) {
        let size = NSSize(width: CatView.baseSize.width * scale, height: CatView.baseSize.height * scale)
        let area = NSScreen.main?.visibleFrame ?? .zero
        let corner = NSPoint(x: area.maxX - size.width - 16, y: area.minY + 8)
        var frame = NSRect(origin: saved ?? corner, size: size)
        if !NSScreen.screens.contains(where: { $0.frame.contains(NSPoint(x: frame.midX, y: frame.midY)) }) { frame.origin = corner }
        panel.setFrame(frame, display: true)
    }

    private func savePosition() { defaults.set(NSStringFromPoint(panel.frame.origin), forKey: "origin") }

    private var locked: Bool {
        get { defaults.bool(forKey: "locked") }
        set { defaults.set(newValue, forKey: "locked") }
    }

    /// 只有光标落在猫身上时窗口才接收鼠标，其余区域点击直接穿透。
    private func updateHitTest() {
        guard NSEvent.pressedMouseButtons == 0 else { return }  // 拖动途中不切换
        let ignore = locked || !cat.contains(screenPoint: NSEvent.mouseLocation)
        if panel.ignoresMouseEvents != ignore { panel.ignoresMouseEvents = ignore }
    }

    private func show() {
        panel.orderFrontRegardless()
        input.startMouse()
        syncListening()
    }

    private func hide() {
        panel.orderOut(nil)
        input.stopMouse()
        syncListening()
    }

    // MARK: 键盘来源

    private var privacy: Bool {
        get { defaults.object(forKey: "privacy") as? Bool ?? true }
        set { defaults.set(newValue, forKey: "privacy") }
    }

    /// 锁屏或切换到其他用户时视为离开。
    private var away: Bool {
        guard let session = CGSessionCopyCurrentDictionary() as? [String: Any] else { return true }
        return session[kCGSessionOnConsoleKey as String] as? Bool != true || session["CGSSessionScreenIsLocked"] as? Bool == true
    }

    /// 决定键盘输入从哪来，每 0.3 秒核对一次：
    /// 隐藏或离开时全部停止；隐私模式或没有权限时只数次数；否则读取按键。
    private func syncListening() {
        let active = panel.isVisible && !away
        let reading = active && !privacy && CGPreflightListenEventAccess()
        if reading {
            input.startKeys()
        } else if input.isListening {
            input.stopKeys()
            cat.forgetKeys()
        }
        if active && !input.isListening { counter.start() } else { counter.stop() }
    }

    // MARK: 菜单

    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()
        func item(_ title: String, _ action: Selector?, on: Bool = false, tag: Int = 0, in target: NSMenu? = nil) {
            let i = NSMenuItem(title: title, action: action, keyEquivalent: "")
            i.target = self
            i.state = on ? .on : .off
            i.tag = tag
            (target ?? menu).addItem(i)
        }
        item(!panel.isVisible ? "已隐藏：没有监听" : input.isListening ? "正在读取按键，会显示在屏幕上" : "只感知敲击次数和节奏，不读是哪个键", nil)
        item("隐私模式（不读取按键）", #selector(togglePrivacy), on: privacy)
        if !privacy, panel.isVisible, !input.isListening {
            item("授予「输入监控」权限（之后需重开墨团）…", #selector(openSettings))
            menu.items.last?.toolTip = "列表里已有墨团却不起作用：先用「−」删掉，重开墨团，再关闭隐私模式"
        }
        menu.addItem(.separator())
        item("显示小猫", #selector(toggleVisible), on: panel.isVisible)
        if !privacy { item("按键气泡", #selector(toggleBubbles), on: cat.showsBubbles) }
        item("锁定位置（鼠标穿透）", #selector(toggleLock), on: locked)
        let sizes = NSMenu()
        for percent in [60, 80, 100, 125, 150] {
            item("\(percent)%", #selector(resize(_:)), on: Int((scale * 100).rounded()) == percent, tag: percent, in: sizes)
        }
        let size = NSMenuItem(title: "大小", action: nil, keyEquivalent: "")
        size.submenu = sizes
        menu.addItem(size)
        menu.addItem(.separator())
        item("设置与演示…", #selector(openPanel))
        let quit = NSMenuItem(title: "退出", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        menu.addItem(quit)
    }

    private lazy var settings = SettingsWindow(onPrivacyChange: { [unowned self] in applyPrivacy() })
    @objc private func openPanel() { settings.show() }

    @objc private func openSettings() {
        CGRequestListenEventAccess()  // 还没问过时，系统会弹窗并把墨团加进列表
        NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ListenEvent")!)
    }

    @objc private func toggleVisible() { panel.isVisible ? hide() : show() }

    @objc private func togglePrivacy() {
        privacy.toggle()
        applyPrivacy()
    }

    /// 菜单和设置窗口切换后都走这里；只有主动关闭隐私模式或点「授予权限」时才会申请键盘权限。
    private func applyPrivacy() {
        if !privacy, !CGPreflightListenEventAccess() { CGRequestListenEventAccess() }
        syncListening()
    }

    @objc private func toggleBubbles() {
        cat.showsBubbles.toggle()
        defaults.set(cat.showsBubbles, forKey: "bubbles")
    }

    @objc private func toggleLock() {
        locked.toggle()
        updateHitTest()
    }

    @objc private func resize(_ sender: NSMenuItem) {
        scale = CGFloat(sender.tag) / 100
        place(panel.frame.origin)
        savePosition()
    }
}

let app = NSApplication.shared
let delegate = App()
app.delegate = delegate
app.run()
