using System;
using System.Runtime.InteropServices;
using System.Text;

namespace InkCat
{
    static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }
        [StructLayout(LayoutKind.Sequential)] public struct LASTINPUTINFO { public int Size; public int Time; }
        [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int Size; public RECT Monitor, Work; public int Flags; }
        [StructLayout(LayoutKind.Sequential)] public struct RAWINPUTDEVICE { public ushort Page, Usage; public int Flags; public IntPtr Target; }
        [StructLayout(LayoutKind.Sequential)]
        public struct GUITHREADINFO
        {
            public int Size, Flags;
            public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
            public RECT CaretRect;
        }

        public const int WM_INPUT = 0x00FF, WM_MOUSEACTIVATE = 0x0021, MA_NOACTIVATE = 3;
        public const int GWL_STYLE = -16, GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
        public const int SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOZORDER = 4, SWP_NOACTIVATE = 0x10;

        [DllImport("user32")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32")] public static extern short GetAsyncKeyState(int vk);
        [DllImport("user32")] public static extern int GetSystemMetrics(int index);
        [DllImport("user32")] public static extern uint MapVirtualKey(uint code, uint type);
        [DllImport("user32")] public static extern int GetMessageTime();
        [DllImport("user32")] public static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
        [DllImport("user32")] public static extern IntPtr MonitorFromPoint(POINT p, int flags);
        [DllImport("user32")] public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
        [DllImport("user32", SetLastError = true)] public static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, int count, int size);
        [DllImport("user32")] public static extern unsafe int GetRawInputData(IntPtr handle, int command, byte* data, ref int size, int headerSize);
        [DllImport("user32")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32")] public static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32")] public static extern int GetWindowThreadProcessId(IntPtr window, IntPtr process);
        [DllImport("user32")] public static extern bool GetGUIThreadInfo(int thread, ref GUITHREADINFO info);
        [DllImport("user32", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr window, StringBuilder name, int max);
        [DllImport("user32")] public static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32")] public static extern int SetWindowLong(IntPtr window, int index, int value);
        [DllImport("user32")] public static extern bool GetWindowRect(IntPtr window, out RECT rect);
        [DllImport("user32")] public static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int w, int h, int flags);

        public static POINT Cursor { get { GetCursorPos(out var p); return p; } }

        /// <summary>点所在的显示器；<paramref name="nearest"/> 为假且不在任何显示器上时返回 null。</summary>
        public static MONITORINFO? MonitorAt(POINT p, bool nearest)
        {
            var monitor = MonitorFromPoint(p, nearest ? 2 : 0);
            var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
            return monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info) ? info : (MONITORINFO?)null;
        }
    }
}
