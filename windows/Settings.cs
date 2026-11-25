using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace InkCat
{
    /// <summary>左边演示和设置，右边动作列表。</summary>
    sealed class SettingsWindow : Window
    {
        static readonly Brush faint = SystemColors.GrayTextBrush, warning = new SolidColorBrush(Color.FromRgb(0xC2, 0x5A, 0x10));

        readonly CatView cat = new CatView(preview: true) { ShowsBubbles = true, Width = 390, Height = 291, ClipToBounds = true };
        readonly TextBlock caption = new TextBlock { Foreground = faint, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 10) };
        readonly Button stop = new Button { Content = "停止", Padding = new Thickness(12, 4, 12, 4) };
        readonly Dictionary<string, List<CheckBox>> switches = new Dictionary<string, List<CheckBox>>();

        public SettingsWindow(Func<bool, bool> setPrivacy, Action<Skin> setSkin)
        {
            Title = "墨团 · 设置与演示";
            Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/InkCat;component/icon.ico"));
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.CanMinimize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
            FontSize = 13;

            var left = new StackPanel { Width = 390, Margin = new Thickness(16) };
            left.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(12), Child = cat,
                Background = new LinearGradientBrush(Color.FromRgb(0xCC, 0xDE, 0xE8), Color.FromRgb(0xED, 0xE6, 0xD6), 90),
            });
            left.Children.Add(caption);
            left.Children.Add(Row(HorizontalAlignment.Center, PlayButton("演示新动作", Demo.All.Where(d => d.IsNew).ToArray()), PlayButton("全部演示", Demo.All), stop));
            left.Children.Add(PrivacyPanel(setPrivacy));
            left.Children.Add(Picker("外观", Skin.All.Select(s => (s.Title, s.Id)), "skin", Skin.Current.Id, id =>
            {
                cat.Skin = Skin.Current;
                setSkin(Skin.Current);
            }));
            left.Children.Add(Picker("表情灵敏度", new[] { ("灵敏", 0.7), ("普通", 1.0), ("迟钝", 1.3), ("很迟钝", 1.7) }, "moodScale", 1.0));
            left.Children.Add(Picker("无操作后打盹", new[] { ("30 秒", 30.0), ("90 秒", 90.0), ("3 分钟", 180.0), ("不打盹", 0.0) }, "nap", 90.0));

            var right = new StackPanel { Margin = new Thickness(14, 10, 14, 10) };
            foreach (var group in Demo.All.GroupBy(d => d.IsNew))
            {
                right.Children.Add(new TextBlock { Text = group.Key ? "新增动作" : "原有动作", FontWeight = FontWeights.SemiBold, Foreground = faint, Margin = new Thickness(0, 6, 0, 4) });
                foreach (var demo in group) right.Children.Add(DemoRow(demo));
            }

            var root = new DockPanel { Height = 570 };
            root.Children.Add(left);
            root.Children.Add(new ScrollViewer { Content = right, Width = 340, Background = Brushes.White, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            Content = root;
            Background = new SolidColorBrush(Color.FromRgb(0xF6, 0xF6, 0xF6));

            stop.Click += (s, e) => Stop();
            Closed += (s, e) => Stop();
            ShowCurrent(null);
        }

        static StackPanel Row(HorizontalAlignment align, params UIElement[] items)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = align };
            foreach (FrameworkElement item in items)
            {
                item.Margin = new Thickness(4, 0, 4, 0);
                row.Children.Add(item);
            }
            return row;
        }

        static UIElement PrivacyPanel(Func<bool, bool> setPrivacy)
        {
            bool Current() => Prefs.Get("privacy", true);
            var box = new CheckBox { Content = "隐私模式（不读取按键）" };
            var note = new TextBlock { FontSize = 11.5, Margin = new Thickness(20, 2, 0, 0) };
            void Refresh()
            {
                box.IsChecked = Current();
                note.Text = Current() ? "只感知敲击节奏" : "按了什么键会显示在屏幕上";
                note.Foreground = Current() ? faint : warning;
            }
            box.Click += (s, e) =>
            {
                setPrivacy(box.IsChecked == true);
                Refresh();
            };
            Refresh();
            var panel = new StackPanel { Margin = new Thickness(4, 22, 0, 8), Children = { box, note } };
            panel.IsVisibleChanged += (s, e) => Refresh();
            return panel;
        }

        static UIElement Picker<T>(string title, IEnumerable<(string name, T value)> options, string pref, T fallback, Action<T> changed = null)
        {
            var all = options.ToArray();
            var box = new ComboBox { Width = 120, ItemsSource = all.Select(o => o.name), SelectedIndex = Array.FindIndex(all, o => Equals(o.value, Prefs.Get(pref, fallback))) };
            box.SelectionChanged += (s, e) =>
            {
                Prefs.Set(pref, all[box.SelectedIndex].value);
                changed?.Invoke(all[box.SelectedIndex].value);
            };
            var row = Row(HorizontalAlignment.Left, new TextBlock { Text = title, Width = 96, VerticalAlignment = VerticalAlignment.Center }, box);
            row.Margin = new Thickness(0, 4, 0, 4);
            return row;
        }

        // ———— 演示：逐个播放，新的播放会打断旧的 ————

        CancellationTokenSource cancel;
        Task playing = Task.CompletedTask;

        Button PlayButton(string title, params Demo[] demos)
        {
            var b = new Button { Content = title, Padding = new Thickness(12, 4, 12, 4) };
            b.Click += (s, e) => playing = Play(demos, playing);
            return b;
        }

        async Task Play(Demo[] demos, Task previous)
        {
            cancel?.Cancel();
            var mine = cancel = new CancellationTokenSource();
            await previous;
            foreach (var demo in demos)
            {
                if (mine.IsCancellationRequested) break;
                ShowCurrent(demo);
                try { await demo.Play(cat, mine.Token); }
                catch (OperationCanceledException) { }
                cat.Settle();
                await Task.Delay(450);
            }
            if (cancel == mine) ShowCurrent(null);
        }

        void Stop() => cancel?.Cancel();

        void ShowCurrent(Demo demo)
        {
            caption.Text = demo == null ? "点右侧 ▶ 查看单个动作" : "正在演示：" + demo.Name;
            stop.IsEnabled = demo != null;
        }

        UIElement DemoRow(Demo demo)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var play = PlayButton("▶", demo);
            play.Padding = new Thickness(7, 2, 7, 2);
            play.Margin = new Thickness(0, 0, 10, 0);
            play.VerticalAlignment = VerticalAlignment.Center;
            play.ToolTip = "演示";
            row.Children.Add(play);
            if (demo.Pref != null)
            {
                var toggle = Toggle(demo.Pref);
                DockPanel.SetDock(toggle, Dock.Right);
                row.Children.Add(toggle);
            }
            row.Children.Add(new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = demo.Name },
                    new TextBlock { Text = demo.Detail, FontSize = 11.5, Foreground = faint, TextWrapping = TextWrapping.Wrap },
                },
            });
            return row;
        }

        /// <summary>两种表情共用一个开关，要一起变。</summary>
        CheckBox Toggle(string pref)
        {
            var box = new CheckBox { IsChecked = Prefs.Get(pref, true), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            if (!switches.TryGetValue(pref, out var same)) switches[pref] = same = new List<CheckBox>();
            same.Add(box);
            box.Click += (s, e) =>
            {
                Prefs.Set(pref, box.IsChecked == true);
                foreach (var other in same) other.IsChecked = box.IsChecked;
            };
            return box;
        }
    }
}
