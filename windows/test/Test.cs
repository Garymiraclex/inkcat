using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace InkCat
{
    /// <summary>用法：InkCat.Test 输出目录 InkCat.exe。先离屏渲染各种姿势，再启动真程序，用模拟输入测试并截屏。</summary>
    static class Test
    {
        static string output;
        static int failures;

        [STAThread]
        static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            output = args[0];
            Directory.CreateDirectory(output);
            new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Section("键位", Keys);
            Section("节奏", Rhythms);
            Section("姿势", Poses);
            Section("设置窗口", Settings);
            Section("真机", () => Live(args[1]));
            Console.WriteLine(failures == 0 ? "全部通过" : $"{failures} 项失败");
            return failures == 0 ? 0 : 1;
        }

        static void Section(string name, Action body)
        {
            Console.WriteLine("== " + name);
            try { body(); }
            catch (Exception e) { Check(false, e.ToString()); }
        }

        static void Check(bool ok, string what)
        {
            Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what);
            if (!ok) failures++;
        }

        static void Wait(double seconds)
        {
            var frame = new DispatcherFrame();
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            t.Tick += (s, e) => { t.Stop(); frame.Continue = false; };
            t.Start();
            Dispatcher.PushFrame(frame);
        }

        static void Keys()
        {
            Check(Keyboard.Keys.Count == 78, $"{Keyboard.Keys.Count} 个键");
            Check(Keyboard.Find(0x21).Hand == Hand.Left && Keyboard.Find(0x24).Hand == Hand.Right, "F 归左手、J 归右手");
            Check(Keyboard.Find(0x47) == Keyboard.Find(0x08) && Keyboard.Find(0xE01C) == Keyboard.Find(0x1C), "小键盘映射");
            Check(Keyboard.Find(0x7777) == null, "未知键忽略");
        }

        static void Rhythms()
        {
            int Count(IEnumerable<int> times) { var r = new Rhythm(); return times.Count(r.IsTap); }
            var typing = Enumerable.Range(0, 20).SelectMany(i => new[] { 10000 + i * 180, 10080 + i * 180 });
            Check(Count(typing) == 20, $"20 次按下抬起 → {Count(typing)} 下");
            var hold = new[] { 10000 }.Concat(Enumerable.Range(0, 90).Select(i => 10500 + i * 33));
            Check(Count(hold) <= 6, $"长按 3 秒 → {Count(hold)} 下");
        }

        // ———— 离屏渲染 ————

        static void Shot(CatView cat, string name, int width = 600)
        {
            double dpi = 96 * width / cat.ActualWidth;
            var target = new RenderTargetBitmap(width, (int)Math.Round(width * 896 / 1200.0), dpi, dpi, PixelFormats.Pbgra32);
            var back = new DrawingVisual();
            using (var dc = back.RenderOpen()) dc.DrawRectangle(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xB9, 0xC9, 0xD2)), null, new Rect(0, 0, cat.ActualWidth, cat.ActualHeight));
            target.Render(back);
            target.Render(cat);
            Save(target, name);
        }

        static void Save(BitmapSource image, string name)
        {
            var encoder = new PngBitmapEncoder { Frames = { BitmapFrame.Create(image) } };
            using (var file = File.Create(Path.Combine(output, name + ".png"))) encoder.Save(file);
        }

        static void Poses()
        {
            foreach (var skin in Skin.All)
            {
                var cat = new CatView(preview: true) { Skin = skin, ShowsBubbles = true, Width = 600, Height = 448 };
                var window = new Window { Content = cat, SizeToContent = SizeToContent.WidthAndHeight, WindowStyle = WindowStyle.None, ShowActivated = false };
                window.Show();
                void Pose(string name, Action act, double wait = 0.3)
                {
                    act();
                    Wait(wait);
                    Shot(cat, $"{skin.Id}_{name}");
                    cat.Settle();
                    Wait(0.9);
                }
                void Down(params int[] codes) { foreach (int c in codes) cat.Key(c, true); }
                Pose("idle", () => { });
                Pose("letters", () => Down(0x21, 0x24));       // F J
                Pose("far", () => Down(0x10, 0x19));           // Q P
                Pose("edge", () => Down(0x1D, 0x1C));          // Ctrl Enter
                Pose("chord", () => Down(0x1D, 0x2A, 0x2E));   // Ctrl+Shift+C
                Pose("space", () => Down(0x39));
                Pose("left", () => cat.Press(0, true));
                Pose("right", () => cat.Press(1, true));
                Pose("wheel", () => cat.Scroll(true), 0.12);
                Pose("corner", () => cat.SlideMouse(new System.Windows.Point(1, 0)));
                Pose("corner2", () => cat.SlideMouse(new System.Windows.Point(0, 1)));
                Pose("fluster", () => cat.ShowMood(8));
                Pose("swish", () => cat.Swish(), 0.5);
                Pose("cover", () => cat.SetSecure(true));
                Pose("nap", () => { cat.SetNapping(true); cat.FloatZ(); }, 0.6);
                window.Close();
            }
            Check(Directory.GetFiles(output).Length >= 30, "30 张姿势图");
        }

        static void Settings()
        {
            var w = new SettingsWindow(on => true, s => { });
            w.Show();
            Wait(0.5);
            var target = new RenderTargetBitmap((int)w.ActualWidth, (int)w.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            target.Render(w);
            Save(target, "settings");
            Check(w.ActualWidth > 700, $"窗口 {w.ActualWidth:0}×{w.ActualHeight:0}");
            w.Close();
        }

        // ———— 真程序 ————

        [DllImport("user32")] static extern void keybd_event(byte vk, byte scan, int flags, IntPtr extra);
        [DllImport("user32")] static extern void mouse_event(int flags, int x, int y, int data, IntPtr extra);
        [DllImport("user32")] static extern bool SetCursorPos(int x, int y);
        [DllImport("user32", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32")] static extern IntPtr WindowFromPoint(Native.POINT p);
        [DllImport("user32")] static extern IntPtr GetAncestor(IntPtr w, int flags);

        static void Press(byte scan, bool down) => keybd_event((byte)Native.MapVirtualKey(scan, 1), scan, down ? 0 : 2, IntPtr.Zero);

        static Bitmap Screen(string name)
        {
            var bounds = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            var shot = new Bitmap(bounds.Width, bounds.Height);
            using (var g = Graphics.FromImage(shot)) g.CopyFromScreen(0, 0, 0, 0, shot.Size);
            shot.Save(Path.Combine(output, name + ".png"));
            return shot;
        }

        static int Difference(Bitmap a, Bitmap b, Native.RECT r)
        {
            int n = 0;
            for (int y = r.Top; y < r.Bottom; y++)
                for (int x = r.Left; x < r.Right; x++)
                {
                    var (p, q) = (a.GetPixel(x, y), b.GetPixel(x, y));
                    if (Math.Abs(p.R - q.R) + Math.Abs(p.G - q.G) + Math.Abs(p.B - q.B) > 60) n++;
                }
            return n;
        }

        static void Live(string exe)
        {
            foreach (var (key, value) in new[] { ("tail", false), ("bubbles", true) }) Prefs.Set(key, value);  // 尾巴会干扰截图对比
            Prefs.Set("privacy", true);

            // 一个自己的窗口接住模拟的输入
            var box = new System.Windows.Controls.TextBox { FontSize = 20, AcceptsReturn = true };
            var secret = new System.Windows.Controls.PasswordBox { FontSize = 20, Margin = new Thickness(0, 12, 0, 0) };
            var native = new System.Windows.Forms.TextBox { UseSystemPasswordChar = true };
            var host = new System.Windows.Forms.Integration.WindowsFormsHost { Child = native, Margin = new Thickness(0, 12, 0, 0) };
            var target = new Window
            {
                Title = "target", Left = 40, Top = 40, Width = 480, Height = 220, Topmost = false,
                Content = new System.Windows.Controls.StackPanel { Children = { box, secret, host } },
            };
            target.Show();
            target.Activate();
            SetCursorPos(400, 330);
            Wait(0.3);
            mouse_event(2 | 4, 0, 0, 0, IntPtr.Zero);

            var process = Process.Start(exe);
            try { Drive(process, target, box, secret, native); }
            finally { if (!process.HasExited) process.Kill(); }
        }

        static void Drive(Process process, Window target, System.Windows.Controls.TextBox box, System.Windows.Controls.PasswordBox secret, System.Windows.Forms.TextBox native)
        {
            Wait(4);
            if (process.HasExited)
                foreach (var entry in new EventLog("Application").Entries.Cast<EventLogEntry>().Reverse().Where(x => x.Source == ".NET Runtime").Take(1)) Console.WriteLine(entry.Message);
            var cat = FindWindow(null, "墨团");
            Check(cat != IntPtr.Zero, "猫窗口出现");
            Native.GetWindowRect(cat, out var r);
            Console.WriteLine($"       位置 {r.Left},{r.Top} {r.Width}×{r.Height}，屏幕 {System.Windows.Forms.Screen.PrimaryScreen.Bounds}");

            // 点击穿透：左上角透明处不归猫，身体中间归猫
            IntPtr Owner(double x, double y) => GetAncestor(WindowFromPoint(new Native.POINT { X = r.Left + (int)(r.Width * x), Y = r.Top + (int)(r.Height * y) }), 2);
            Check(Owner(0.04, 0.04) != cat, "透明处点击穿透");
            Check(Owner(0.45, 0.45) == cat, "身体可以点到");

            target.Activate();
            box.Focus();
            Wait(0.5);
            Check(Native.GetForegroundWindow() != cat, "猫没有抢焦点");

            // 眨眼会干扰对比，取差异较小的一次
            int Change(Action act, Action undo, string name, double wait = 0.25)
            {
                Wait(1.2);
                var before = Screen(name + "_before");
                act();
                Wait(wait);
                var after = Screen(name);
                undo();
                return Difference(before, after, r);
            }

            int still = Enumerable.Range(0, 3).Min(i => Change(() => { }, () => { }, "live_still"));
            Check(still < 300, $"没有输入时静止（变化 {still} 点）");

            int tap = Enumerable.Range(0, 6).Max(i => Change(() => Press(0x21, true), () => Press(0x21, false), "live_privacy", 0.07));
            Check(tap > 300, $"隐私模式下敲击有反应（变化 {tap} 点）");

            int move = Change(() => { SetCursorPos(60, 700); mouse_event(1, 3, 3, 0, IntPtr.Zero); }, () => { SetCursorPos(400, 330); mouse_event(1, 1, 1, 0, IntPtr.Zero); }, "live_move");
            Check(move > 300, $"鼠标跟手（变化 {move} 点）");
            int click = Change(() => mouse_event(8, 0, 0, 0, IntPtr.Zero), () => mouse_event(16, 0, 0, 0, IntPtr.Zero), "live_click");
            Check(click > 100, $"右键有反应（变化 {click} 点）");
            Press(0x01, true); Press(0x01, false);  // 收起可能弹出的右键菜单
            Wait(0.5);

            // 关闭隐私模式：设置每 0.3 秒核对一次
            Prefs.Set("privacy", false);
            target.Activate();
            box.Focus();
            Wait(1);
            int letter = Change(() => Press(0x21, true), () => Press(0x21, false), "live_letter");
            Check(letter > 600, $"读取按键：F 亮起（变化 {letter} 点）");
            Wait(0.6);

            // 密码框：猫捂眼；捂眼期间按键无反应
            secret.Focus();
            Wait(1.2);
            var covered = Screen("live_password_wpf");
            int eyes = Difference(Screen("live_after"), covered, r);
            var openEyes = new Bitmap(Path.Combine(output, "live_letter_before.png"));
            Check(Difference(openEyes, covered, r) > 1500, $"辅助功能识别的密码框捂眼（变化 {Difference(openEyes, covered, r)} 点）");
            int leak = Change(() => Press(0x21, true), () => Press(0x21, false), "live_password_key");
            Check(leak < 300, $"密码框里按键不显示（变化 {leak} 点）");

            box.Focus();
            Wait(1.2);
            Check(Difference(openEyes, Screen("live_uncovered"), r) < 600, "离开密码框后恢复");

            Prefs.Set("privacy", true);
            Wait(0.8);
            native.Focus();
            Wait(1.2);
            Check(Difference(openEyes, Screen("live_password_native"), r) > 1500, "隐私模式下传统密码框捂眼");
            box.Focus();
            Wait(1);

            // 空闲开销
            process.Refresh();
            var cpu = process.TotalProcessorTime;
            Wait(10);
            process.Refresh();
            double percent = (process.TotalProcessorTime - cpu).TotalSeconds / 10 * 100;
            Console.WriteLine($"       空闲 CPU {percent:0.0}%（单核），内存 {process.WorkingSet64 >> 20} MB，句柄 {process.HandleCount}");
            Check(percent < 8, "空闲开销");
            Check(!process.HasExited, "程序仍在运行");
            Check(!process.Modules.Cast<ProcessModule>().Any(m => new[] { "ws2_32.dll", "winhttp.dll", "wininet.dll" }.Contains(m.ModuleName.ToLower())), "没有加载联网组件");
        }
    }
}
