using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using static InkCat.Native;

namespace InkCat
{
    /// <summary>透明、置顶、不抢焦点的窗口。透明的地方点击直接穿透。</summary>
    sealed class CatWindow : Window
    {
        public readonly CatView Cat = new CatView();
        public event Action MenuRequested;
        /// <summary>在猫身上连点三下。</summary>
        public event Action TripleClicked;
        readonly List<(POINT at, int time)> clicks = new List<(POINT, int)>();
        IntPtr handle;
        POINT grab;

        public CatWindow()
        {
            Title = "墨团";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            Content = Cat;
            (Width, Height) = (CatView.BaseSize.Width * Scale, CatView.BaseSize.Height * Scale);
            MouseRightButtonUp += (s, e) => MenuRequested?.Invoke();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(handle).AddHook(Hook);
            ApplyStyle();
            Place();
        }

        IntPtr Hook(IntPtr hwnd, int message, IntPtr w, IntPtr l, ref bool handled)
        {
            if (message != WM_MOUSEACTIVATE) return IntPtr.Zero;
            handled = true;
            return new IntPtr(MA_NOACTIVATE);
        }

        void ApplyStyle()
        {
            int style = GetWindowLong(handle, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            SetWindowLong(handle, GWL_EXSTYLE, Locked ? style | WS_EX_TRANSPARENT : style & ~WS_EX_TRANSPARENT);
        }

        /// <summary>锁定后整只猫都不接收鼠标。</summary>
        public bool Locked
        {
            get => Prefs.Get("locked", false);
            set { Prefs.Set("locked", value); ApplyStyle(); }
        }

        public double Scale
        {
            get => Prefs.Get("scale", 1.0);
            set
            {
                Prefs.Set("scale", value);
                double bottom = Top + Height;
                (Width, Height) = (CatView.BaseSize.Width * value, CatView.BaseSize.Height * value);
                Top = bottom - Height;  // 左下角不动
                KeepOnScreen();
                SavePosition();
            }
        }

        // ———— 位置：一律用物理像素，多屏缩放不同也不会错位 ————

        RECT Rect { get { GetWindowRect(handle, out var r); return r; } }
        void MoveTo(int x, int y) => SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

        void SavePosition()
        {
            Prefs.Set("x", Rect.Left);
            Prefs.Set("y", Rect.Top);
        }

        void Place()
        {
            MoveTo(Prefs.Get("x", int.MinValue / 2), Prefs.Get("y", int.MinValue / 2));
            KeepOnScreen();
        }

        /// <summary>没有保存过位置，或原来的显示器不在了，就放到主屏右下角。</summary>
        public void KeepOnScreen()
        {
            var r = Rect;
            if (MonitorAt(new POINT { X = r.Left + r.Width / 2, Y = r.Top + r.Height / 2 }, false) != null) return;
            var area = SystemParameters.WorkArea;
            Left = area.Right - Width - 16;
            Top = area.Bottom - Height - 8;
        }

        /// <summary>记下一次按下；原地连点到第三下时返回真。拖动过的不算。</summary>
        public bool CountClick(POINT p, int time)
        {
            if (clicks.Count > 0)
            {
                var (at, last) = clicks[clicks.Count - 1];
                if (time - last > System.Windows.Forms.SystemInformation.DoubleClickTime || Math.Abs(p.X - at.X) > 4 || Math.Abs(p.Y - at.Y) > 4) clicks.Clear();
            }
            clicks.Add((p, time));
            if (clicks.Count < 3) return false;
            clicks.Clear();
            return true;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            var (p, r) = (Cursor, Rect);
            if (CountClick(p, e.Timestamp)) { TripleClicked?.Invoke(); return; }
            grab = new POINT { X = p.X - r.Left, Y = p.Y - r.Top };
            CaptureMouse();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (IsMouseCaptured) MoveTo(Cursor.X - grab.X, Cursor.Y - grab.Y);
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (!IsMouseCaptured) return;
            ReleaseMouseCapture();
            SavePosition();
        }

        static new POINT Cursor => Native.Cursor;
    }
}
