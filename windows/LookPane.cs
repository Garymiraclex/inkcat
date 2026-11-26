using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace InkCat
{
    /// <summary>设置窗口的「外观」页：每个部位选哪个素材包，位置不准就微调。</summary>
    sealed class LookPane : StackPanel
    {
        static readonly Brush faint = SystemColors.GrayTextBrush, red = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C)), orange = new SolidColorBrush(Color.FromRgb(0xC2, 0x5A, 0x10));

        /// <summary>正在调整位置的部位。</summary>
        public Slot? Adjusting { get; private set; }
        public event Action AdjustingChanged;

        readonly Wardrobe wardrobe = Wardrobe.Shared;
        readonly List<Action> refreshers = new List<Action>();
        readonly TextBlock busy = new TextBlock { Text = "正在换装…", Foreground = faint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        readonly TextBlock troubles = Note();
        bool refreshing;

        public LookPane()
        {
            Margin = new Thickness(14);
            foreach (var slot in Slots.All) Children.Add(Row(slot));

            var import = new Button { Content = "导入生图…", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 0) };
            import.Click += (s, e) => ImportWindow.Open();
            var open = new Button { Content = "打开素材文件夹", Padding = new Thickness(10, 4, 10, 4) };
            open.Click += (s, e) => Process.Start("explorer.exe", "\"" + Library.Folder + "\"");
            var whole = new Button { Content = "整套换成 ▾", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(8, 0, 0, 0) };
            whole.Click += (s, e) =>
            {
                var menu = new ContextMenu { PlacementTarget = whole, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
                foreach (var pack in wardrobe.Packs.Where(p => p.Slots.Length > 0))
                {
                    var item = new MenuItem { Header = new TextBlock { Text = pack.Title } };
                    item.Click += (a, b) => { Adjust(null); wardrobe.WearAll(pack); };
                    menu.Items.Add(item);
                }
                menu.IsOpen = true;
            };
            Children.Add(new Separator { Margin = new Thickness(0, 10, 0, 10) });
            Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { import, open, whole, busy } });
            var help = Note();
            help.Text = "用 AI 画了自己的猫，点「导入生图」把原图拖进去就行。也可以把整理好的素材包直接放进文件夹，改图后自动更新。图怎么画，见文件夹里的「素材说明」和「_模板」。";
            help.Margin = new Thickness(0, 8, 0, 0);
            Children.Add(help);
            troubles.Foreground = orange;
            Children.Add(troubles);

            // 只在页面可见时跟着变，窗口关掉就不再引用
            Loaded += (s, e) => { wardrobe.Changed += Refresh; Refresh(); };
            Unloaded += (s, e) => { wardrobe.Changed -= Refresh; Adjust(null); };
        }

        static TextBlock Note() => new TextBlock { FontSize = 11.5, Foreground = faint, TextWrapping = TextWrapping.Wrap };

        void Refresh()
        {
            refreshing = true;
            foreach (var r in refreshers) r();
            busy.Visibility = wardrobe.Busy ? Visibility.Visible : Visibility.Hidden;
            troubles.Text = string.Join("\n", wardrobe.Packs.Where(p => p.Problem != null).Select(p => $"「{p.Title}」{p.Problem}"));
            refreshing = false;
        }

        void Adjust(Slot? slot)
        {
            Adjusting = slot;
            Refresh();
            AdjustingChanged?.Invoke();
        }

        UIElement Row(Slot slot)
        {
            var box = new ComboBox { DisplayMemberPath = "Value", SelectedValuePath = "Key", Width = 180 };
            box.SelectionChanged += (s, e) =>
            {
                if (refreshing || !(box.SelectedValue is string id)) return;
                if (Adjusting == slot) Adjust(null);
                wardrobe.Choose(slot, id);
            };
            var adjust = new Button { Content = "✥", ToolTip = "调整位置", Padding = new Thickness(7, 1, 7, 1), Margin = new Thickness(8, 0, 0, 0), Visibility = slot == Slot.Cat ? Visibility.Hidden : Visibility.Visible };
            adjust.Click += (s, e) => Adjust(Adjusting == slot ? (Slot?)null : slot);
            var note = Note();
            note.Margin = new Thickness(42, 2, 0, 0);
            var adjuster = Adjuster(slot);

            refreshers.Add(() =>
            {
                var options = wardrobe.PacksFor(slot).Select(p => new KeyValuePair<string, string>(p.Id, p.Title)).ToList();
                if (slot.IsOptional()) options.Insert(0, new KeyValuePair<string, string>("", "不戴"));
                if (!(box.ItemsSource is List<KeyValuePair<string, string>> old) || !old.SequenceEqual(options)) box.ItemsSource = options;
                var worn = wardrobe.Worn(slot);
                box.SelectedValue = worn?.Id ?? (slot.IsOptional() ? "" : Pack.Classic);  // 选中的素材包不在了，显示实际用的
                adjust.IsEnabled = worn != null;
                adjuster.Visibility = Adjusting == slot ? Visibility.Visible : Visibility.Collapsed;
                bool bad = wardrobe.Problems.TryGetValue(slot, out string text), warn = !bad && wardrobe.Warnings.TryGetValue(slot, out text);
                if (!bad && !warn && slot == Slot.Cat && options.Count == 1) text = "现在只有一只猫，把新猫的文件夹放进素材文件夹就能选";
                note.Text = text ?? "";
                note.Foreground = bad ? red : warn ? orange : faint;
                note.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
            });
            return new StackPanel
            {
                Margin = new Thickness(0, 0, 0, 10),
                Children =
                {
                    new StackPanel { Orientation = Orientation.Horizontal, Children = { new TextBlock { Text = slot.Title(), Width = 42, VerticalAlignment = VerticalAlignment.Center }, box, adjust } },
                    note, adjuster,
                },
            };
        }

        FrameworkElement Adjuster(Slot slot)
        {
            var panel = new StackPanel { Margin = new Thickness(42, 6, 0, 0) };
            void Slide(string title, double low, double high, Func<Place, double> get, Action<Place, double> set)
            {
                var slider = new Slider { Minimum = low, Maximum = high, Width = 190 };
                slider.ValueChanged += (s, e) =>
                {
                    if (refreshing) return;
                    var p = wardrobe.PlaceOf(slot);
                    set(p, Math.Round(slider.Value, 2));
                    wardrobe.Move(slot, p);
                };
                refreshers.Add(() => { if (Adjusting == slot && !slider.IsMouseCaptureWithin) slider.Value = get(wardrobe.PlaceOf(slot)); });
                panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { new TextBlock { Text = title, Width = 36, FontSize = 11.5 }, slider } });
            }
            Slide("大小", 0.5, 1.6, p => p.scale, (p, v) => p.scale = v);
            Slide("角度", -45, 45, p => p.angle, (p, v) => p.angle = v);
            var reset = new Button { Content = "恢复原位", FontSize = 11.5, Padding = new Thickness(8, 1, 8, 1), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
            reset.Click += (s, e) => wardrobe.Move(slot, new Place());
            panel.Children.Add(reset);
            return panel;
        }
    }
}
