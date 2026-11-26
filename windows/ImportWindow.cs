using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace InkCat
{
    /// <summary>「导入生图」：把 AI 画的原图拖进对应格子，整理好存成素材包。</summary>
    sealed class ImportWindow : Window
    {
        static readonly Brush faint = SystemColors.GrayTextBrush, red = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C)),
            orange = new SolidColorBrush(Color.FromRgb(0xC2, 0x5A, 0x10)), green = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32));
        static readonly Demo[] demos = new[] { "左右手打字", "输密码捂眼", "打盹", "甩尾巴" }.Select(n => Demo.All.First(d => d.Name == n)).ToArray();
        static ImportWindow current;

        public static ImportWindow Open()
        {
            if (current == null) { current = new ImportWindow(); current.Closed += (s, e) => current = null; }
            current.Show();
            current.Activate();
            return current;
        }

        public readonly Dictionary<Pose, Raster> Raw = new Dictionary<Pose, Raster>();
        public ImportResult Result { get; private set; } = new ImportResult();
        public bool Busy { get; private set; }
        public readonly TextBox NameBox = new TextBox { Padding = new Thickness(4, 3, 4, 3) };
        public readonly TextBlock Message = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };

        readonly Dictionary<Pose, string> unreadable = new Dictionary<Pose, string>();
        readonly List<Action> refreshers = new List<Action>();
        readonly CatView cat = new CatView(preview: true, look: new Look()) { ShowsBubbles = true, Width = 390, Height = 291, ClipToBounds = true };
        readonly TextBlock overlay = new TextBlock { Foreground = faint, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        readonly TextBlock warning = new TextBlock { FontSize = 11.5, Foreground = orange, TextWrapping = TextWrapping.Wrap };
        readonly Button save = new Button { Content = "保存并换上", Padding = new Thickness(12, 4, 12, 4), IsDefault = true };
        readonly Button watch = new Button { Content = "看动作", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0) };
        readonly Composer composer = new Composer();
        readonly string preview = Path.Combine(Path.GetDirectoryName(Library.Cache), "import");
        int turn;
        CancellationTokenSource cancel;

        ImportWindow()
        {
            Title = "墨团 · 导入生图";
            Icon = BitmapFrame.Create(new Uri("pack://application:,,,/InkCat;component/icon.ico"));
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.CanMinimize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
            FontSize = 13;
            Background = new SolidColorBrush(Color.FromRgb(0xF6, 0xF6, 0xF6));

            var root = new StackPanel { Margin = new Thickness(18), Width = 720 };
            root.Children.Add(new TextBlock { Text = "把 AI 画的原图拖进对应的格子，文件名不用管。程序会去掉背景和水印、对齐位置，再检查能不能用；原图不会被改动。", Foreground = faint, TextWrapping = TextWrapping.Wrap });
            var tiles = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 14) };
            foreach (var pose in Poses.All) tiles.Children.Add(Tile(pose));
            root.Children.Add(tiles);
            root.Children.Add(new Separator());

            var stage = new Grid { Width = 390, Height = 291, Children = { cat, overlay } };
            var left = new Border
            {
                CornerRadius = new CornerRadius(12), Child = stage, ClipToBounds = true,
                Background = new LinearGradientBrush(Color.FromRgb(0xCC, 0xDE, 0xE8), Color.FromRgb(0xED, 0xE6, 0xD6), 90),
            };
            var help = new TextBlock { Text = "图怎么画、给 AI 的提示词，见素材文件夹里的「素材说明」。", FontSize = 11.5, Foreground = faint, TextWrapping = TextWrapping.Wrap };
            var guide = new Button { Content = "打开素材说明", Padding = new Thickness(10, 2, 10, 2), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
            guide.Click += (s, e) => { try { Process.Start(Path.Combine(Library.Folder, "素材说明.txt")); } catch (Exception) { } };
            var right = new DockPanel { Margin = new Thickness(16, 0, 0, 0), Height = 291 };
            var bottom = new StackPanel { Children = { help, guide } };
            DockPanel.SetDock(bottom, Dock.Bottom);
            right.Children.Add(bottom);
            right.Children.Add(new StackPanel
            {
                Children =
                {
                    warning,
                    new TextBlock { Text = "名字", Margin = new Thickness(0, 4, 0, 4) },
                    NameBox,
                    new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0), Children = { save, watch } },
                    Message,
                },
            });
            var row = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
            row.Children.Add(left);
            row.Children.Add(right);
            root.Children.Add(row);
            Content = root;

            NameBox.TextChanged += (s, e) => Refresh();
            save.Click += (s, e) => Save();
            watch.Click += (s, e) => Play();
            Closed += (s, e) => cancel?.Cancel();
            Refresh();
        }

        UIElement Tile(Pose pose)
        {
            var template = new Image { Source = pose.Template().Image, Opacity = 0.18, Margin = new Thickness(4) };
            var picture = new Image { Margin = new Thickness(4) };
            var hint = new TextBlock { Text = "拖到这里", Foreground = faint, FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var box = new Border
            {
                Width = 128, Height = 96, CornerRadius = new CornerRadius(8), AllowDrop = true,
                Background = new SolidColorBrush(Color.FromArgb(0x10, 0, 0, 0)),
                Child = new Grid { Children = { template, hint, picture } },
            };
            box.DragEnter += (s, e) => { box.Background = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)); };
            box.DragLeave += (s, e) => { box.Background = new SolidColorBrush(Color.FromArgb(0x10, 0, 0, 0)); };
            box.DragOver += (s, e) =>
            {
                e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Bitmap) ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true;
            };
            box.Drop += (s, e) =>
            {
                box.Background = new SolidColorBrush(Color.FromArgb(0x10, 0, 0, 0));
                try
                {
                    if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0) Put(pose, Read(files[0]));
                    else if (e.Data.GetData(DataFormats.Bitmap) is BitmapSource image)
                        using (var copy = new MemoryStream())
                        {
                            new PngBitmapEncoder { Frames = { BitmapFrame.Create(image) } }.Save(copy);
                            Put(pose, copy.ToArray());
                        }
                }
                catch (Problem p) { Fail(pose, p.Message); }
            };

            var title = new TextBlock { Text = pose.Title() + (pose.IsOptional() ? "（可选）" : ""), VerticalAlignment = VerticalAlignment.Center };
            var remove = new Button { Content = "✕", Padding = new Thickness(5, 0, 5, 0), FontSize = 10, ToolTip = "移除", Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
            remove.Click += (s, e) => Remove(pose);
            DockPanel.SetDock(remove, Dock.Right);
            var status = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Width = 128 };
            Raster last = null;
            refreshers.Add(() =>
            {
                var shown = Result.Images.TryGetValue(pose, out var done) ? done : Raw.TryGetValue(pose, out var r) ? r : null;
                if (shown != last) (last, picture.Source) = (shown, shown?.Image);
                template.Visibility = hint.Visibility = shown == null ? Visibility.Visible : Visibility.Collapsed;
                remove.Visibility = Raw.ContainsKey(pose) || unreadable.ContainsKey(pose) ? Visibility.Visible : Visibility.Hidden;
                (string text, Brush color) =
                    unreadable.TryGetValue(pose, out var bad) || Result.Problems.TryGetValue(pose, out bad) ? (bad, red)
                    : Result.Notes.TryGetValue(pose, out var note) ? (note, faint)
                    : Result.Images.ContainsKey(pose) ? ("可以用", green)
                    : Raw.ContainsKey(pose) ? ("整理中…", faint)
                    : pose.IsOptional() ? ("右手接身体露馅时再画", faint) : ("", faint);
                (status.Text, status.Foreground) = (text, color);
            });
            return new StackPanel { Width = 128, Margin = new Thickness(0, 0, 10, 0), Children = { box, new DockPanel { Margin = new Thickness(0, 4, 0, 2), Children = { remove, title } }, status } };
        }

        static byte[] Read(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists || (info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0) throw new Problem("这不是一张图");
            if (info.Length > 20 << 20) throw new Problem("图太大，不能超过 20 MB");
            try { return File.ReadAllBytes(info.FullName); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { throw new Problem("打不开这张图"); }
        }

        public void Put(Pose pose, byte[] data)
        {
            try
            {
                Raw[pose] = Importer.Load(data);
                unreadable.Remove(pose);
                Changed();
            }
            catch (Problem p) { Fail(pose, p.Message); }
        }

        void Fail(Pose pose, string text)
        {
            Raw.Remove(pose);
            unreadable[pose] = text;
            Changed();
        }

        void Remove(Pose pose)
        {
            Raw.Remove(pose);
            unreadable.Remove(pose);
            Changed();
        }

        /// <summary>重新整理全部；整套齐了就合成一身，在预览里动起来。</summary>
        void Changed()
        {
            int mine = ++turn;
            Message.Text = "";
            Busy = true;
            var raw = new Dictionary<Pose, Raster>(Raw);
            var pack = new Pack { Id = "import", Title = "", Folder = preview, Slots = new[] { Slot.Cat } };
            var outfit = Wardrobe.Shared.Outfit(pack);
            var fallback = Wardrobe.Shared.Classic;
            Refresh();
            Task.Run(() =>
            {
                lock (composer)
                {
                    if (mine != Volatile.Read(ref turn)) return;
                    var result = Importer.Process(raw);
                    Look look = null;
                    if (result.IsReady)
                        try
                        {
                            if (Directory.Exists(preview)) Directory.Delete(preview, true);
                            Directory.CreateDirectory(preview);
                            foreach (var p in result.Images)
                                using (var file = File.Create(Path.Combine(preview, p.Key.Id() + ".png")))
                                    new PngBitmapEncoder { Frames = { BitmapFrame.Create(p.Value.Image) } }.Save(file);
                            look = composer.Compose(outfit, fallback).Look;
                        }
                        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is Problem) { }
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (mine != turn) return;
                        (Result, Busy) = (result, false);
                        if (look != null) { cat.Look = look; Play(); }
                        Refresh();
                    }));
                }
            });
        }

        void Refresh()
        {
            foreach (var r in refreshers) r();
            cat.Opacity = Result.IsReady ? 1 : 0.15;
            overlay.Text = Busy ? "整理中…" : Result.IsReady ? "" : "四张都能用后，在这里动起来";
            warning.Text = Result.Warning ?? "";
            warning.Visibility = Result.Warning == null ? Visibility.Collapsed : Visibility.Visible;
            save.IsEnabled = Result.IsReady && !Busy && NameBox.Text.Trim() != "";
            watch.IsEnabled = Result.IsReady && !Busy;
        }

        async void Play()
        {
            cancel?.Cancel();
            var mine = cancel = new CancellationTokenSource();
            foreach (var demo in demos)
            {
                if (mine.IsCancellationRequested) return;
                try { await demo.Play(cat, mine.Token); }
                catch (OperationCanceledException) { }
                cat.Settle();
                try { await Task.Delay(450, mine.Token); } catch (OperationCanceledException) { }
            }
        }

        public void Save()
        {
            try
            {
                string id = Importer.Save(Result.Images, NameBox.Text);
                Wardrobe.Shared.Adopt(id);
                string name = NameBox.Text.Trim();
                cancel?.Cancel();
                Raw.Clear();
                unreadable.Clear();
                Result = new ImportResult();
                NameBox.Text = "";
                cat.Look = new Look();
                (Message.Text, Message.Foreground) = ($"已存好并换上「{name}」，在设置的「猫」里也能选", green);
            }
            catch (Problem p) { (Message.Text, Message.Foreground) = (p.Message, red); }
            Refresh();
        }
    }
}
