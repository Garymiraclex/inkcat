using System;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace InkCat
{
    sealed class App : Application
    {
        readonly Forms.NotifyIcon tray = new Forms.NotifyIcon();
        readonly Forms.ContextMenuStrip menu = new Forms.ContextMenuStrip();
        RawInput input;
        CatWindow window;
        KeyCounter counter;
        SettingsWindow settings;
        bool away;

        CatView Cat => window.Cat;

        [STAThread]
        static void Main()
        {
            using (new Mutex(true, "InkCat.Running", out bool first))
            {
                if (!first) return;
                Forms.Application.EnableVisualStyles();
                new App { ShutdownMode = ShutdownMode.OnExplicitShutdown }.Run();
            }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            input = new RawInput();
            window = new CatWindow();
            Cat.ShowsBubbles = Prefs.Get("bubbles", false);
            window.MenuRequested += () =>
            {
                Native.SetForegroundWindow(new System.Windows.Interop.WindowInteropHelper(window).Handle);  // 否则点别处菜单不会收起
                menu.Show(Forms.Cursor.Position);
            };

            menu.Opening += (s, a) => { FillMenu(); a.Cancel = false; };
            tray.Icon = new Icon(GetResourceStream(new Uri("pack://application:,,,/InkCat;component/icon.ico")).Stream, Forms.SystemInformation.SmallIconSize);
            tray.Text = "墨团";
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += (s, a) => OpenSettings();
            tray.Visible = true;

            counter = new KeyCounter(input);
            counter.Tap += Cat.TapAnonymously;
            input.Key += Cat.Key;
            input.Button += Cat.Press;
            input.Wheel += delta => Cat.Scroll(delta > 0);
            input.Moved += Cat.Follow;

            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.DisplaySettingsChanged += (s, a) => Dispatcher.BeginInvoke(new Action(window.KeepOnScreen));

            Visible = true;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.3) };
            timer.Tick += (s, a) =>
            {
                SyncListening();
                if (Visible) Cat.Heartbeat();
            };
            timer.Start();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            tray.Dispose();
        }

        void OnSessionSwitch(object sender, SessionSwitchEventArgs e) => Dispatcher.BeginInvoke(new Action(() =>
        {
            switch (e.Reason)
            {
                case SessionSwitchReason.SessionLock:
                case SessionSwitchReason.ConsoleDisconnect:
                case SessionSwitchReason.RemoteDisconnect: away = true; break;
                case SessionSwitchReason.SessionUnlock:
                case SessionSwitchReason.ConsoleConnect:
                case SessionSwitchReason.RemoteConnect: away = false; break;
            }
            SyncListening();
        }));

        bool Visible
        {
            get => window.IsVisible;
            set
            {
                if (value) { window.Show(); input.StartMouse(); }
                else { window.Hide(); input.StopMouse(); }
                SyncListening();
            }
        }

        // ———— 键盘来源 ————

        static bool Privacy => Prefs.Get("privacy", true);

        /// <summary>隐藏或离开时全部停止；隐私模式只感知节奏；否则读取按键。</summary>
        void SyncListening()
        {
            bool active = Visible && !away, reading = active && !Privacy;
            PasswordGuard.Watch(reading);
            if (reading) input.StartKeys();
            else if (input.IsReadingKeys)
            {
                input.StopKeys();
                Cat.ForgetKeys();
            }
            if (active && !input.IsReadingKeys) counter.Start(); else counter.Stop();
        }

        /// <summary>关闭隐私模式前说明后果；菜单和设置窗口都走这里。返回是否已切换。</summary>
        bool SetPrivacy(bool on)
        {
            if (!on && MessageBox.Show(
                    "关闭后，墨团会读取每一次按键，爪子和亮起的键会让看到屏幕的人知道你按了什么。\n\n" +
                    "Windows 没有可靠的「正在输密码」信号，部分密码框认不出来（例如终端、远程桌面）。输入密码或共享屏幕前，请先打开隐私模式或隐藏小猫。\n\n" +
                    "按键不记录、不保存、不上传。要关闭隐私模式吗？",
                    "墨团", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return false;
            Prefs.Set("privacy", on);
            SyncListening();
            return true;
        }

        // ———— 菜单 ————

        void FillMenu()
        {
            Forms.ToolStripMenuItem Item(string title, Action action, bool check = false) =>
                new Forms.ToolStripMenuItem(title, null, (s, e) => action()) { Checked = check };

            var items = menu.Items;
            items.Clear();
            items.Add(new Forms.ToolStripMenuItem(!Visible ? "已隐藏：没有监听" : input.IsReadingKeys ? "正在读取按键，会显示在屏幕上" : "只感知敲击节奏，不读是哪个键") { Enabled = false });
            items.Add(Item("隐私模式（不读取按键）", () => SetPrivacy(!Privacy), Privacy));
            items.Add(new Forms.ToolStripSeparator());
            items.Add(Item("显示小猫", () => Visible = !Visible, Visible));
            if (!Privacy) items.Add(Item("按键气泡", () => Prefs.Set("bubbles", Cat.ShowsBubbles = !Cat.ShowsBubbles), Cat.ShowsBubbles));
            items.Add(Item("锁定位置（鼠标穿透）", () => window.Locked = !window.Locked, window.Locked));
            var size = new Forms.ToolStripMenuItem("大小");
            foreach (int percent in new[] { 60, 80, 100, 125, 150 })
                size.DropDownItems.Add(Item($"{percent}%", () => window.Scale = percent / 100.0, Math.Round(window.Scale * 100) == percent));
            items.Add(size);
            items.Add(new Forms.ToolStripSeparator());
            items.Add(Item("设置与演示…", OpenSettings));
            items.Add(Item("退出", Shutdown));
        }

        void OpenSettings()
        {
            if (settings == null)
            {
                settings = new SettingsWindow(SetPrivacy, skin => Cat.Skin = skin);
                settings.Closed += (s, e) => settings = null;
            }
            settings.Show();
            settings.Activate();
        }
    }
}
