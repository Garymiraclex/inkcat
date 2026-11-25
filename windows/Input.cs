using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Threading;
using static InkCat.Native;

namespace InkCat
{
    /// <summary>
    /// 键盘和鼠标的只读监听（Raw Input）：不拦截、不改写、不记录、不落盘。
    /// 隐私模式下根本不登记键盘；关闭隐私模式后，按了什么键会由小猫显示在屏幕上。
    /// </summary>
    sealed class RawInput
    {
        public event Action<int, bool> Key;
        public event Action<int, bool> Button;
        public event Action<int> Wheel;
        public event Action Moved;

        public bool IsReadingKeys { get; private set; }
        /// <summary>最近一次鼠标事件的系统时刻（毫秒）。</summary>
        public int LastMouse { get; private set; }

        readonly HwndSource window;
        static readonly Dictionary<int, int> virtualKeys = new Dictionary<int, int>();
        const ushort mouse = 2, keyboard = 6;

        public RawInput()
        {
            window = new HwndSource(new HwndSourceParameters("InkCat.Input") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });  // 只收消息的窗口
            window.AddHook(Hook);
        }

        /// <summary>键此刻是否真的按着，用来补上漏掉的抬起。</summary>
        public static bool IsDown(int code) => virtualKeys.TryGetValue(code, out int vk) && GetAsyncKeyState(vk) < 0;
        public static bool IsButtonDown(int button) => GetAsyncKeyState((button == 0) == (GetSystemMetrics(23) == 0) ? 1 : 2) < 0;

        public void StartMouse() => Register(mouse, true);
        public void StopMouse() => Register(mouse, false);
        public void StartKeys() { if (!IsReadingKeys) IsReadingKeys = Register(keyboard, true); }

        public void StopKeys()
        {
            if (!IsReadingKeys) return;
            Register(keyboard, false);
            IsReadingKeys = false;
            virtualKeys.Clear();
        }

        bool Register(ushort usage, bool on) => RegisterRawInputDevices(
            new[] { new RAWINPUTDEVICE { Page = 1, Usage = usage, Flags = on ? 0x100 /* INPUTSINK */ : 1 /* REMOVE */, Target = on ? window.Handle : IntPtr.Zero } },
            1, Marshal.SizeOf<RAWINPUTDEVICE>());

        unsafe IntPtr Hook(IntPtr hwnd, int message, IntPtr w, IntPtr l, ref bool handled)
        {
            if (message != WM_INPUT) return IntPtr.Zero;
            int header = 8 + 2 * IntPtr.Size, size = 64;
            byte* data = stackalloc byte[size];
            if (GetRawInputData(l, 0x10000003 /* RID_INPUT */, data, ref size, header) <= 0) return IntPtr.Zero;
            ushort* body = (ushort*)(data + header);
            if (*(int*)data == 0) OnMouse(body[2], (short)body[3]);
            else if (*(int*)data == 1 && IsReadingKeys) OnKey(body[0], body[1], body[3]);
            return IntPtr.Zero;
        }

        void OnMouse(int flags, short wheel)
        {
            LastMouse = GetMessageTime();
            bool swapped = GetSystemMetrics(23 /* SM_SWAPBUTTON */) != 0;
            for (int i = 0; i < 2; i++)
            {
                int button = swapped ? 1 - i : i;
                if ((flags & 1 << 2 * i) != 0) Button?.Invoke(button, true);
                if ((flags & 2 << 2 * i) != 0) Button?.Invoke(button, false);
            }
            if ((flags & 0x400) != 0) Wheel?.Invoke(wheel);
            Moved?.Invoke();
        }

        void OnKey(int make, int flags, int vk)
        {
            if (vk == 0xFF || (flags & 4) != 0) return;  // 扩展键附带的假键、Pause
            int code = make != 0 ? make | ((flags & 2) != 0 ? 0xE000 : 0) : (int)MapVirtualKey((uint)vk, 4);  // 软件模拟的键可能没有扫描码
            virtualKeys[code] = vk;
            Key?.Invoke(code, (flags & 1) == 0);
        }
    }

    /// <summary>
    /// 隐私模式的输入来源：只问系统「最后一次输入是什么时候」，排除鼠标后当作敲了一下。
    /// 系统不说是哪个键，按下和抬起也分不开，所以次数是估计值。
    /// </summary>
    sealed class KeyCounter
    {
        public event Action Tap;

        readonly DispatcherTimer timer = new DispatcherTimer(DispatcherPriority.Input);
        readonly RawInput input;
        readonly Rhythm rhythm = new Rhythm();
        int last, pending;
        bool waiting, wasSecure;
        POINT cursor;

        public KeyCounter(RawInput input)
        {
            this.input = input;
            timer.Tick += (s, e) => Poll();
        }

        static int LastInput
        {
            get
            {
                var info = new LASTINPUTINFO { Size = Marshal.SizeOf<LASTINPUTINFO>() };
                GetLastInputInfo(ref info);
                return info.Time;
            }
        }

        public void Start()
        {
            if (timer.IsEnabled) return;
            (last, waiting, wasSecure) = (LastInput, false, true);
            timer.Interval = TimeSpan.FromMilliseconds(50);
            timer.Start();
        }

        public void Stop() => timer.Stop();

        void Poll()
        {
            int now = LastInput;
            var p = Cursor;
            bool moved = p.X != cursor.X || p.Y != cursor.Y, secure = PasswordGuard.IsActive;
            cursor = p;
            // 晚一拍再判断，等同一时刻的鼠标消息先到
            if (waiting && !moved && pending - input.LastMouse > 30 && rhythm.IsTap(pending) && !secure && !wasSecure) Tap?.Invoke();
            (waiting, pending, last, wasSecure) = (now != last, now, now, secure);
            timer.Interval = TimeSpan.FromMilliseconds(Environment.TickCount - now < 2000 ? 15 : 50);
        }
    }

    /// <summary>从一串「有输入」的时刻里估计敲击：按下算，抬起不算，长按连发不算。</summary>
    sealed class Rhythm
    {
        int previous, steady;
        bool down;

        public bool IsTap(int time)
        {
            int gap = time - previous;
            previous = time;
            steady = gap <= 47 ? steady + 1 : 0;
            if (steady >= 5) return down = false;
            return down = gap > 400 || !down;
        }
    }

    /// <summary>
    /// 密码框识别。Windows 没有「正在输密码」的系统信号，只能看焦点：
    /// 传统窗口的密码框随时可查；浏览器等要借助系统的辅助功能接口，只在读取按键时才查。
    /// </summary>
    static class PasswordGuard
    {
        static volatile bool automated;
        static Thread watcher;
        static volatile bool watching;

        public static bool IsActive => automated || IsNative;

        static bool IsNative
        {
            get
            {
                var info = new GUITHREADINFO { Size = Marshal.SizeOf<GUITHREADINFO>() };
                if (!GetGUIThreadInfo(GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero), ref info) || info.Focus == IntPtr.Zero) return false;
                var name = new StringBuilder(64);
                GetClassName(info.Focus, name, name.Capacity);
                return name.ToString().IndexOf("edit", StringComparison.OrdinalIgnoreCase) >= 0 && (GetWindowLong(info.Focus, GWL_STYLE) & 0x20 /* ES_PASSWORD */) != 0;
            }
        }

        public static void Watch(bool on)
        {
            if (watching == on) return;
            watching = on;
            if (!on) { automated = false; return; }
            watcher = new Thread(() =>
            {
                var me = Thread.CurrentThread;
                while (watching && watcher == me)
                {
                    bool found = false;
                    try { found = AutomationElement.FocusedElement?.Current.IsPassword ?? false; }
                    catch (Exception) { }  // 对方窗口刚关闭、没有响应或不允许查询
                    automated = found && watching;
                    Thread.Sleep(150);
                }
            }) { IsBackground = true };
            watcher.Start();
        }
    }
}
