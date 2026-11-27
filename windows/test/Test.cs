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
            Section("鼠标优先", MousePriority);
            Section("拆猫", Cooking);
            Section("坏素材", Broken);
            Section("姿势", Poses);
            Section("尾巴", Tails);
            Section("即时更换", Swapping);
            Section("设置窗口", Settings);
            Section("导入生图", Importing);
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

        /// <summary>鼠标在用时左手留在鼠标上，按键只亮灯；鼠标闲下来才去键盘。</summary>
        static void MousePriority()
        {
            var cat = new CatView(preview: true) { Width = 600, Height = 448 };
            new Window { Content = cat, SizeToContent = SizeToContent.WidthAndHeight, WindowStyle = WindowStyle.None, ShowActivated = false }.Show();
            var at = new System.Windows.Point(0.5, 0.5);
            cat.Key(0x21, true);  // F
            Check(!cat.PawOnMouse, "鼠标没动时，按住 F，左手在键盘上");
            cat.SlideMouse(at);
            Check(cat.PawOnMouse, "按住 F 时移动鼠标，左手回到鼠标");
            cat.Press(0, true);
            Check(cat.PawOnMouse, "按住 F 时点击，左手在鼠标上");
            cat.Press(0, false);
            cat.Settle();
            Wait(0.7);
            cat.Key(0x2A, true);  // Shift
            Check(!cat.PawOnMouse, "鼠标闲着时，按住 Shift，左手去键盘");
            cat.Press(0, true);
            Check(cat.PawOnMouse, "按住 Shift 点击，左手回到鼠标");
            cat.Settle();
            Wait(0.7);
            int off = 0;
            for (int i = 0; i < 40; i++)
            {
                cat.TapAnonymously();
                cat.SlideMouse(new System.Windows.Point(i / 40.0, 0.5));
                Wait(0.07);
                if (!cat.PawOnMouse) off++;
            }
            Check(off == 0, $"隐私模式每秒 14 下 + 移动鼠标：左手离开鼠标 {off}/40 次");
            Wait(0.7);
            cat.Key(0x21, true);
            Check(!cat.PawOnMouse, "鼠标闲了 0.6 秒后，F 把左手叫去键盘");
            cat.Key(0x21, false);
            cat.SlideMouse(at);
            Check(cat.PawOnMouse, "松开 F 后再动鼠标，左手回到鼠标");
            cat.Settle();
        }

        // ———— 换装 ————

        static Composer.Result Compose(params (string slot, string pack)[] wear)
        {
            var packs = Library.Scan();
            var classic = packs.First(p => p.Id == Pack.Classic);
            var outfit = Slots.All.Where(s => !s.IsOptional()).ToDictionary(s => s, s => new Choice { Pack = classic });
            foreach (var (slot, id) in wear)
            {
                var s = Slots.All.First(x => x.Id() == slot);
                var pack = packs.First(p => p.Id == id);
                outfit[s] = new Choice { Pack = pack, Place = pack.Config.place != null && pack.Config.place.TryGetValue(slot, out var place) ? place.Clamped() : new Place() };
            }
            return new Composer().Compose(outfit, classic);
        }

        static string NewPack(string name)
        {
            string path = Path.Combine(Library.Folder, name);
            if (Directory.Exists(path)) Directory.Delete(path, true);
            Directory.CreateDirectory(path);
            return path;
        }

        static void Frames(string path, Func<string, string> pick)
        {
            foreach (string f in Slot.Cat.Files()) File.WriteAllBytes(Path.Combine(path, f + ".png"), Art.Read($"packs/classic/{pick(f)}.png"));
        }

        static void Picture(string path, int w, int h, Action<Graphics> draw)
        {
            using (var b = new Bitmap(w, h))
            {
                using (var g = Graphics.FromImage(b)) { (g.SmoothingMode, g.PixelOffsetMode) = (System.Drawing.Drawing2D.SmoothingMode.AntiAlias, System.Drawing.Drawing2D.PixelOffsetMode.Half); draw(g); }
                b.Save(path);
            }
        }

        /// <summary>两种画法各拆一遍，结果存下来和 Mac 版对比。</summary>
        static void Cooking()
        {
            Library.Prepare();
            Check(File.Exists(Path.Combine(Library.Folder, "素材说明.txt")) && Directory.GetFiles(Path.Combine(Library.Folder, "_模板", "原版")).Length == 8 && Directory.GetFiles(Path.Combine(Library.Folder, "_模板", "电竞")).Length == 6, "素材文件夹和模板");
            Frames(NewPack("搭着"), f => f == "type_both" ? "idle" : f);
            // 右臂搭着、左臂敲键：左半边用 idle，右半边用 type_both
            string half = NewPack("半搭");
            Frames(half, f => f);
            var (a, b) = (Library.Decode(Art.Read("packs/classic/idle.png"), ""), Library.Decode(Art.Read("packs/classic/type_both.png"), ""));
            for (int y = 0; y < Cutter.H; y++) for (int x = 0; x < 480; x++) b[x, y] = a[x, y];
            using (var file = File.Create(Path.Combine(half, "type_both.png"))) new PngBitmapEncoder { Frames = { BitmapFrame.Create(b.Image) } }.Save(file);
            string kit = NewPack("配件");
            Picture(Path.Combine(kit, "hat.png"), 2400, 1792, g =>  // 白底、双倍大小
            {
                g.Clear(System.Drawing.Color.White);
                using (var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(60, 36, 24), 10))
                {
                    g.FillEllipse(System.Drawing.Brushes.Firebrick, 920, 90, 440, 440);
                    g.DrawEllipse(pen, 920, 90, 440, 440);
                }
            });
            File.WriteAllBytes(Path.Combine(kit, "mouse.png"), Art.Read("packs/classic/mouse.png"));
            File.WriteAllText(Path.Combine(kit, "skin.json"), "{\"name\":\"测试配件\",\"place\":{\"mouse\":{\"scale\":1.2,\"angle\":8}},\"cat\":{\"scarf\":true}}");
            foreach (var (name, pack) in new[] { ("typing", Pack.Classic), ("resting", "user/搭着") })
            {
                var p = Library.Scan().First(x => x.Id == pack);
                var f = Slot.Cat.Files().Select(p.Image).ToArray();
                var watch = Stopwatch.StartNew();
                var cat = Composer.Cut(new Cutter.Cat(f[0], f[1], f[3], f[2]));
                Console.WriteLine($"       {name} 拆图 {watch.Elapsed.TotalSeconds:0.0}s");
                Directory.CreateDirectory(Path.Combine(output, "cook_" + name));
                foreach (var i in cat.Images) Save(i.Value.Image, $"cook_{name}/{i.Key}");
                Check(cat.Images.Count == 12 && cat.Joint.Length == (name == "typing" ? 2 : 0), $"{name} 12 个部件");
            }
            var r = Compose(("keyboard", "gamer"), ("pad", "gamer"), ("mouse", "gamer"), ("headset", "gamer"));
            Check(r.Problems.Count == 0 && r.Look.Images.Count == 15, "电竞合成");
            Directory.CreateDirectory(Path.Combine(output, "cook_gamer"));
            foreach (var i in r.Look.Images) Save(i.Value, "cook_gamer/" + i.Key);
            var again = Stopwatch.StartNew();
            Compose(("keyboard", "gamer"));
            Console.WriteLine($"       有缓存时合成 {again.Elapsed.TotalSeconds:0.0}s");
            Check(Compose().Look.Images.Count == 0, "全部原版时不合成");
            Check(Library.Scan().First(x => x.Id == "user/配件").Title == "测试配件", "读取 skin.json");

            // 后背更胖的猫：在后背加一条，尾巴层不能带上它
            string fat = NewPack("胖背");
            Frames(fat, f => f);
            foreach (string f in Slot.Cat.Files())
            {
                var fr = Library.Decode(File.ReadAllBytes(Path.Combine(fat, f + ".png")), f);
                for (int y = 420; y < 580; y++)  // 每行在后背外沿再画 12 像素，不碰到尾巴
                {
                    int e = 820;
                    while (fr[e, y].W > 0.3f) e++;
                    bool clear = Enumerable.Range(e, 20).All(x => fr[x, y].W < 0.1f);
                    if (clear) for (int x = e; x < e + 12; x++) fr[x, y] = fr[e - 4, y];
                }
                using (var file = File.Create(Path.Combine(fat, f + ".png"))) new PngBitmapEncoder { Frames = { BitmapFrame.Create(fr.Image) } }.Save(file);
            }
            var fatCat = Library.Scan().First(x => x.Id == "user/胖背");
            var ff = Slot.Cat.Files().Select(fatCat.Image).ToArray();
            var fc = new Cutter.Cat(ff[0], ff[1], ff[3], ff[2]);
            var tail = fc.Tail;
            int leak = 0;
            for (int y = 430; y < 570; y++)
            {
                int e = 820;
                while (ff[0][e, y].W > 0.3f) e++;  // 加宽后的后背外沿
                for (int x = 820; x < e - 1; x++) if (tail[x, y].W > 0.5f) leak++;
            }
            Check(leak == 0, $"后背更胖时尾巴不带后背（漏 {leak} 点）");

            // no_arms：给出无臂身体时也能拆
            string bare = NewPack("无臂");
            Frames(bare, f => f);
            File.WriteAllBytes(Path.Combine(bare, "no_arms.png"), Art.Read("packs/classic/cover_eyes.png"));
            var withBare = Compose(("cat", "user/无臂"));
            Check(withBare.Problems.Count == 0 && withBare.Look.Images.Count == 15, "带 no_arms.png 能拆");
            var blue = Compose(("cat", "british_blue"), ("scarf", "user/配件"));
            Check(Library.Scan().Count(p => p.IsBuiltin && p.Slots.Contains(Slot.Cat)) == 2 && blue.Problems.Count == 0 && blue.Look.Joint.Length == 0 && blue.Warnings.ContainsKey(Slot.Scarf), "自带英短蓝猫，身上有围巾不能再戴");
            foreach (var i in blue.Look.Images) if (i.Key.StartsWith("plate")) Save(i.Value, "blue_" + i.Key);
        }

        static void Broken()
        {
            void Expect(string slot, string what, string expected, Action<string> make, bool warning = false)
            {
                string name = "坏_" + what;
                string path = NewPack(name);
                make(path);
                try
                {
                    var pack = Library.Scan().First(p => p.Id == "user/" + name);
                    string text = pack.Problem;
                    if (pack.Slots.Any(s => s.Id() == slot))
                    {
                        var r = Compose((slot, "user/" + name));
                        text = (warning ? r.Warnings : r.Problems).Values.FirstOrDefault() ?? text;
                    }
                    Check((text ?? "").Contains(expected), $"{what}：{text ?? "没有提示"}");
                }
                finally { Directory.Delete(path, true); }
            }
            byte[] mouse = Art.Read("packs/classic/mouse.png");
            Expect("hat", "尺寸", "1000×1000", p => Picture(p + "/hat.png", 1000, 1000, g => { }));
            Expect("cat", "缺帧", "还缺", p => Picture(p + "/idle.png", 1200, 896, g => { }));
            Expect("hat", "花背景", "背景不是透明", p => Picture(p + "/hat.png", 1200, 896, g => { var colors = new[] { System.Drawing.Brushes.Tomato, System.Drawing.Brushes.SeaGreen, System.Drawing.Brushes.SteelBlue, System.Drawing.Brushes.Gold }; for (int i = 0; i < 108; i++) g.FillRectangle(colors[i * 7 % 4], i % 12 * 100, i / 12 * 100, 100, 100); }));
            Expect("hat", "损坏", "打不开", p => File.WriteAllBytes(p + "/hat.png", new byte[500]));
            Expect("mouse", "空文件", "打不开", p => File.WriteAllBytes(p + "/mouse.png", new byte[0]));
            Expect("mouse", "没写完", "不完整", p => File.WriteAllBytes(p + "/mouse.png", mouse.Take(mouse.Length / 2).ToArray()));
            Expect("pad", "超大", "太大", p => File.WriteAllBytes(p + "/pad.png", new byte[21 << 20]));
            Expect("mouse", "透明", "空的", p => Picture(p + "/mouse.png", 1200, 896, g => { }));
            Expect("mouse", "移出", "画面外", p => { File.WriteAllBytes(p + "/mouse.png", mouse); File.WriteAllText(p + "/skin.json", "{\"place\":{\"mouse\":{\"x\":-99999,\"scale\":1e308}},\"buttons\":{\"left\":[[1,2],[3]]},\"cat\":{\"joint\":[\"zz\",\"1\"]}}"); });
            Expect("mouse", "坏json", "看不懂", p => { File.WriteAllBytes(p + "/mouse.png", mouse); File.WriteAllText(p + "/skin.json", "{\"place\": [1,2"); });
            Expect("mouse", "坏json2", "看不懂", p => { File.WriteAllBytes(p + "/mouse.png", mouse); File.WriteAllText(p + "/skin.json", "{\"place\":{\"mouse\":{\"x\":\"a\"}}}"); });
            Expect("cat", "错位", "没有对齐", p =>
            {
                Frames(p, f => f);
                using (var s = new Bitmap(new MemoryStream(Art.Read("packs/classic/sleep.png")))) Picture(p + "/sleep.png", 1200, 896, g => g.DrawImage(s, 25, 12, 1200, 896));
            });
            Expect("cat", "反向", "差得比较多", p =>
            {
                foreach (string f in Slot.Cat.Files())
                    using (var s = new Bitmap(new MemoryStream(Art.Read($"packs/classic/{f}.png")))) { s.RotateFlip(RotateFlipType.RotateNoneFlipX); s.Save($"{p}/{f}.png"); }
            }, warning: true);
        }

        static void Swapping()
        {
            var w = Wardrobe.Shared;
            int dressed = 0;
            w.Dressed += l => dressed++;
            void Until(string what, Func<bool> ok)
            {
                var t = Stopwatch.StartNew();
                while (!ok() && t.Elapsed.TotalSeconds < 10) Wait(0.05);
                Check(ok(), $"{what}（{t.Elapsed.TotalSeconds:0.0}s）");
            }
            w.Start();
            Until("首次换装", () => dressed > 0);
            string path = NewPack("新猫");
            Frames(path, f => f == "type_both" ? "idle" : f);
            Until("放入后出现在列表", () => w.PacksFor(Slot.Cat).Any(p => p.Id == "user/新猫"));
            w.Choose(Slot.Cat, "user/新猫");
            Until("换猫", () => w.Look.Joint.Length == 0);
            var first = w.Look.Images["plate"];
            byte[] idle = File.ReadAllBytes(path + "/idle.png");
            File.WriteAllBytes(path + "/idle.png", Art.Read("packs/classic/sleep.png"));
            File.WriteAllBytes(path + "/type_both.png", Art.Read("packs/classic/sleep.png"));
            Until("覆盖图片后自动更新", () => w.Look.Images["plate"] != first && !w.Busy);
            var second = w.Look.Images["plate"];
            File.WriteAllBytes(path + "/idle.png", new byte[100]);
            Until("改坏后报错", () => w.Problems.ContainsKey(Slot.Cat));
            Check(w.Look.Images.TryGetValue("plate", out var still) && still == second, "继续穿着上一次的：" + w.Problems[Slot.Cat]);
            File.WriteAllBytes(path + "/idle.png", idle);
            Until("改好后恢复", () => !w.Problems.ContainsKey(Slot.Cat) && !w.Busy);
            Directory.Move(path, path + "2");
            Until("改名后提示并用原版", () => w.Problems.ContainsKey(Slot.Cat) && w.Look.Joint.Length == 2);
            Directory.Delete(path + "2", true);
            w.Choose(Slot.Cat, Pack.Classic);
            w.Choose(Slot.Mouse, "user/配件");
            for (int i = 0; i < 30; i++) { var p = w.PlaceOf(Slot.Mouse); p.x = i; w.Move(Slot.Mouse, p); Wait(0.01); }
            Until("连续拖动后停下来", () => !w.Busy && w.Look.Images.Count > 0);
            string json = File.ReadAllText(Path.Combine(Library.Folder, "配件", "skin.json"));
            Check(json.Contains("29") && json.Contains("测试配件") && json.Contains("scarf"), "位置写进 skin.json，别的内容保留");
            Wait(1.5);
            int before = dressed;
            Wait(3);
            Check(dressed == before, "静止时不重复合成");
            w.WearAll(w.Packs.First(p => p.Id == "gamer"));
            Until("整套换成电竞", () => !w.Busy && w.Choice(Slot.Headset) == "gamer" && w.Look.Images.Count == 15);
            w.WearAll(w.Packs.First(p => p.Id == Pack.Classic));
            Until("整套换回原版", () => !w.Busy && w.Look.Images.Count == 0);
            Keyboard.Rows = Keyboard.ClassicRows;
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
            foreach (var (name0, look) in new[] { ("classic", new Look()), ("gamer", Compose(("keyboard", "gamer"), ("pad", "gamer"), ("mouse", "gamer"), ("headset", "gamer")).Look), ("mix", Compose(("cat", "user/半搭"), ("keyboard", "gamer"), ("headset", "gamer"), ("hat", "user/配件"), ("mouse", "user/配件")).Look) })
            {
                Keyboard.Rows = look.Rows;
                var cat = new CatView(preview: true, look: look) { ShowsBubbles = true, Width = 600, Height = 448 };
                var window = new Window { Content = cat, SizeToContent = SizeToContent.WidthAndHeight, WindowStyle = WindowStyle.None, ShowActivated = false };
                window.Show();
                void Pose(string name, Action act, double wait = 0.3)
                {
                    act();
                    Wait(wait);
                    Shot(cat, $"{name0}_{name}");
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
                Pose("swish", () => cat.Swish(), 1.2);
                Pose("cover", () => cat.SetSecure(true));
                Pose("nap", () => { cat.SetNapping(true); cat.FloatZ(); }, 0.6);
                window.Close();
            }
            Keyboard.Rows = Keyboard.ClassicRows;
            Check(Directory.GetFiles(output, "*_*.png").Length >= 45, "45 张姿势图");
        }

        static void Tails()
        {
            var tail = new Tail();
            tail.Dress(new Look().Image("tail"));
            tail.Draw(1, 1);  // 预热
            var t = Stopwatch.StartNew();
            for (int i = 0; i < 30; i++) tail.Draw(1 + i / 30.0, 1);
            double ms = t.Elapsed.TotalMilliseconds / 30;
            Console.WriteLine($"       单帧 {ms:0.0} 毫秒");
            Check(ms < 33, "尾巴单帧算得过来（每秒 30 帧）");
            tail.Swish();
            Check(tail.Swishing, "甩尾开始");
            tail.Hold();
            Check(!tail.Swishing, "被打断后不再算在甩");
        }

        static void Settings()
        {
            var w = new SettingsWindow(on => true);
            w.Show();
            Wait(0.5);
            var target = new RenderTargetBitmap((int)w.ActualWidth, (int)w.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            target.Render(w);
            Save(target, "settings");
            Check(w.ActualWidth > 700, $"窗口 {w.ActualWidth:0}×{w.ActualHeight:0}");

            // 调整位置：点鼠标那一行的按钮，拖大小滑块
            IEnumerable<T> All<T>(DependencyObject o) where T : DependencyObject =>
                Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(o)).Select(i => VisualTreeHelper.GetChild(o, i)).SelectMany(c => (c is T t ? new[] { t } : new T[0]).Concat(All<T>(c)));
            Wardrobe.Shared.Choose(Slot.Mouse, "gamer");
            Wardrobe.Shared.Choose(Slot.Hat, "user/配件");
            Wardrobe.Shared.Choose(Slot.Scarf, "user/没有这个");
            Wait(1.5);
            All<System.Windows.Controls.Button>(w).Where(x => (x.Content as string) == "✥").ElementAt(3).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Wait(0.3);
            All<System.Windows.Controls.Slider>(w).First(x => x.IsVisible).Value = 1.4;
            Wait(1.5);
            Check(Math.Abs(Wardrobe.Shared.PlaceOf(Slot.Mouse).scale - 1.4) < 0.01 && Wardrobe.Shared.Look.MouseSize.Width > 200, "滑块放大内置鼠标");
            target.Clear();
            target.Render(w);
            Save(target, "settings_adjust");
            Wardrobe.Shared.Move(Slot.Mouse, new Place());
            Wardrobe.Shared.Choose(Slot.Hat, "");
            Wardrobe.Shared.Choose(Slot.Scarf, "");
            Wait(1);
            w.Close();
        }

        // ———— 导入生图 ————

        /// <summary>模拟 AI 原图：灰底（或棋盘格）、挪动缩放、角落水印、缩成 1024×768 的 JPG。</summary>
        static byte[] Degrade(Raster image, double dx, double dy, double s, bool checker = false, bool mark = false, params Raster[] over)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD5, 0xD5, 0xCC)), null, new Rect(0, 0, 1200, 900));
                if (checker)
                    for (int y = 0; y < 900; y += 24) for (int x = 0; x < 1200; x += 24) if ((x / 24 + y / 24) % 2 == 0) dc.DrawRectangle(System.Windows.Media.Brushes.White, null, new Rect(x, y, 24, 24));
                var m = new Matrix();
                m.Translate(-600, -450);
                m.Scale(s, s);
                m.Translate(600 + dx, 450 + dy);
                dc.PushTransform(new MatrixTransform(m));
                foreach (var layer in new[] { image }.Concat(over)) dc.DrawImage(layer.Image, new Rect(0, 2, 1200, 896));
                dc.Pop();
                if (mark) dc.DrawEllipse(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x80, 0x80, 0x8C)), null, new System.Windows.Point(1155, 865), 15, 15);
            }
            var target = new RenderTargetBitmap(1200, 900, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);
            using (var file = new MemoryStream())
            {
                new JpegBitmapEncoder { QualityLevel = 82, Frames = { BitmapFrame.Create(new TransformedBitmap(target, new ScaleTransform(1024 / 1200.0, 768 / 900.0))) } }.Save(file);
                return file.ToArray();
            }
        }

        static double Overlap(Raster a, Raster b)
        {
            int both = 0, either = 0;
            for (int i = 0; i < a.Pixels.Length; i++)
            {
                bool p = a.Pixels[i].W > 0.5f, q = b.Pixels[i].W > 0.5f;
                if (p && q) both++;
                if (p || q) either++;
            }
            return (double)both / either;
        }

        static void Importing()
        {
            var shifts = new Dictionary<Pose, (double dx, double dy, double s)> { [Pose.Idle] = (14, -9, 1.02), [Pose.TypeBoth] = (-6, 5, 1), [Pose.Sleep] = (3, 2, 0.99), [Pose.CoverEyes] = (-17, -11, 1.03) };
            var truth = shifts.Keys.ToDictionary(p => p, p => Library.Decode(Art.Read($"packs/british_blue/{p.Id()}.png"), p.Id()));
            var raw = new Dictionary<Pose, Raster>();
            foreach (bool checker in new[] { false, true })
            {
                string label = checker ? "棋盘格" : "灰底";
                raw = shifts.ToDictionary(p => p.Key, p => Importer.Load(Degrade(truth[p.Key], p.Value.dx, p.Value.dy, p.Value.s, checker, p.Key == Pose.Idle)));
                var t = Stopwatch.StartNew();
                var r = Importer.Process(raw);
                Check(r.IsReady, $"{label}、挪动、4:3 JPG：整理好（{t.Elapsed.TotalSeconds:0.0}s）{string.Join(" ", r.Problems.Values)}");
                foreach (var p in shifts.Keys.Where(r.Images.ContainsKey))
                {
                    double overlap = Overlap(r.Images[p], truth[p]);
                    Check(overlap > 0.985, $"{label} {p.Title()}：和原图重合 {overlap:P1}；{(r.Notes.TryGetValue(p, out var n) ? n : "")}");
                }
                Check(r.Notes.TryGetValue(Pose.Idle, out var idleNote) && idleNote.Contains("水印"), $"{label}：去掉角落水印");
                if (!checker) foreach (var p in r.Images) Save(p.Value.Image, "import_" + p.Key.Id());
            }

            // 耳机头梁内侧被围住的底；浅色的毛不当成背景
            var classic = Pose.Idle.Template();
            Raster Gamer(string f) => Library.Decode(Art.Read($"packs/gamer/{f}.png"), f);
            var worn = new Raster().Under(Gamer("headset_back")).Under(classic).Under(Gamer("headset"));
            var dressed = Importer.Load(Degrade(Gamer("headset_back"), 0, 0, 1, false, false, classic, Gamer("headset")));
            var knocked = Cutout.Knock(dressed, Cutout.Paper(dressed), Importer.Alpha(classic));
            Check(Overlap(knocked, worn) > 0.985, $"头梁内侧的底清掉：重合 {Overlap(knocked, worn):P1}");
            Save(knocked.Image, "import_headset");

            var twice = new Dictionary<Pose, Raster>(raw) { [Pose.TypeBoth] = raw[Pose.Idle] };
            Check(Importer.Process(twice).Problems.TryGetValue(Pose.TypeBoth, out var dup) && dup.Contains("同一张"), "同一张图放两格：" + dup);
            Check(Importer.Process(new Dictionary<Pose, Raster> { [Pose.Idle] = raw[Pose.Idle] }).Images.Count == 1, "只放一张也能整理");
            var sleepy = new Dictionary<Pose, Raster> { [Pose.Idle] = truth[Pose.Idle], [Pose.Sleep] = truth[Pose.Sleep] };  // 睡觉那张只有眼睛不同
            Check(!Importer.Process(sleepy).Problems.ContainsKey(Pose.Sleep), "只差眼睛的睡觉图不算重复");
            try { Importer.Load(new byte[300]); Check(false, "坏图"); } catch (Problem e) { Check(e.Message.Contains("打不开"), "坏图：" + e.Message); }

            // 窗口：放入、整理、预览、保存并换上
            foreach (string old in new[] { "窗口导入" }) if (Directory.Exists(Path.Combine(Library.Folder, old))) Directory.Delete(Path.Combine(Library.Folder, old), true);
            var w = ImportWindow.Open();
            Wait(0.5);
            foreach (var p in shifts) w.Put(p.Key, Degrade(truth[p.Key], p.Value.dx, p.Value.dy, p.Value.s, mark: p.Key == Pose.Idle));
            var clock = Stopwatch.StartNew();
            while ((w.Busy || !w.Result.IsReady) && clock.Elapsed.TotalSeconds < 60) Wait(0.1);
            Check(w.Result.IsReady, $"窗口整理并合成（{clock.Elapsed.TotalSeconds:0.0}s）");
            Wait(2.5);
            var shot = new RenderTargetBitmap((int)w.ActualWidth, (int)w.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            shot.Render(w);
            Save(shot, "import_window");
            w.NameBox.Text = "窗口导入";
            w.Save();
            Check(Wardrobe.Shared.Choice(Slot.Cat) == "user/窗口导入" && File.Exists(Path.Combine(Library.Folder, "窗口导入", "cover_eyes.png")), "保存并换上：" + w.Message.Text);
            foreach (var p in shifts) w.Put(p.Key, Degrade(truth[p.Key], 0, 0, 1));
            clock.Restart();
            while ((w.Busy || !w.Result.IsReady) && clock.Elapsed.TotalSeconds < 60) Wait(0.1);
            w.NameBox.Text = "窗口导入";
            w.Save();
            Check(w.Message.Text.Contains("已经有"), "重名：" + w.Message.Text);
            w.Close();
            Wardrobe.Shared.Choose(Slot.Cat, Pack.Classic);
            Wait(1);
            Directory.Delete(Path.Combine(Library.Folder, "窗口导入"), true);
        }

        // ———— 真程序 ————

        [DllImport("user32")] static extern void keybd_event(byte vk, byte scan, int flags, IntPtr extra);
        [DllImport("user32")] static extern void mouse_event(int flags, int x, int y, int data, IntPtr extra);
        [DllImport("user32")] static extern bool SetCursorPos(int x, int y);
        [DllImport("user32", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32")] static extern IntPtr WindowFromPoint(Native.POINT p);
        [DllImport("user32")] static extern IntPtr GetAncestor(IntPtr w, int flags);
        [DllImport("user32")] static extern bool IsWindowVisible(IntPtr w);

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
            Prefs.Set("tail", false);  // 尾巴会干扰截图对比
            Prefs.Set("bubbles", true);
            Wardrobe.Shared.WearAll(Wardrobe.Shared.Packs.First(p => p.Id == Pack.Classic));
            Prefs.Set("privacy", true);

            // 一个自己的窗口接住模拟的输入
            var box = new System.Windows.Controls.TextBox { FontSize = 20, AcceptsReturn = true };
            var secret = new System.Windows.Controls.PasswordBox { FontSize = 20, Margin = new Thickness(0, 12, 0, 0) };
            var native = new System.Windows.Forms.TextBox { UseSystemPasswordChar = true };
            var host = new System.Windows.Forms.Integration.WindowsFormsHost { Child = native, Margin = new Thickness(0, 12, 0, 0) };
            var target = new Window
            {
                Title = "target", Left = 0, Top = 0, Width = 1024, Height = 768, Background = System.Windows.Media.Brushes.LightSteelBlue,  // 垫在猫后面
                Content = new System.Windows.Controls.StackPanel { Children = { box, secret, host } },
            };
            target.Show();
            target.Activate();
            SetCursorPos(400, 330);
            Wait(0.3);
            mouse_event(2 | 4, 0, 0, 0, IntPtr.Zero);

            void Run(Action<Process> body)
            {
                var process = Process.Start(exe);
                try { Wait(4); body(process); }
                finally { if (!process.HasExited) process.Kill(); Wait(1); }
            }
            Run(p => Drive(p, target, box, secret, native));
            Run(p => Handle(exe));

            Prefs.Set("scale", 1.5);
            Prefs.Set("locked", true);
            Run(p =>
            {
                var r = Where();
                Check(r.Width == 450 && r.Height == 336, $"150% 大小 {r.Width}×{r.Height}");
                Check(r.Left == Prefs.Get("x", 0) && r.Top == Prefs.Get("y", 0), "重开后位置不变");
                Check(Owner(r, 0.45, 0.45) != Cat, "锁定后点击穿透");
                Screen("live_150");
            });
            Wardrobe.Shared.Choose(Slot.Keyboard, "gamer");
            Wardrobe.Shared.Choose(Slot.Headset, "gamer");
            Run(p => { Check(Cat != IntPtr.Zero, "穿着电竞启动"); Screen("live_gamer"); });
            Prefs.Set("scale", 1.0);
            Prefs.Set("locked", false);
            Prefs.Set("x", 99999);
            Run(p => Check(Where().Right <= 1024 && Where().Left > 0, "保存的位置在屏幕外时回到右下角"));
        }

        static IntPtr Cat => FindWindow(null, "墨团");
        static Native.RECT Where() { Native.GetWindowRect(Cat, out var r); return r; }
        static IntPtr Owner(Native.RECT r, double x, double y) =>
            GetAncestor(WindowFromPoint(new Native.POINT { X = r.Left + (int)(r.Width * x), Y = r.Top + (int)(r.Height * y) }), 2);

        static void Click(int x, int y, int down, int up)
        {
            SetCursorPos(x, y);
            Wait(0.3);
            mouse_event(down, 0, 0, 0, IntPtr.Zero);
            Wait(0.2);
            mouse_event(up, 0, 0, 0, IntPtr.Zero);
            Wait(0.8);
        }

        /// <summary>拖动、右键菜单、重复启动、浏览器密码框。</summary>
        static void Handle(string exe)
        {
            var r = Where();
            int x = r.Left + 135, y = r.Top + 100;
            SetCursorPos(x, y);
            Wait(0.3);
            mouse_event(2, 0, 0, 0, IntPtr.Zero);
            Wait(0.3);  // 等按下先处理完，否则第一段移动会被漏掉
            for (int i = 1; i <= 10; i++) { SetCursorPos(x - 12 * i, y - 5 * i); Wait(0.03); }
            mouse_event(4, 0, 0, 0, IntPtr.Zero);
            Wait(0.5);
            var moved = Where();
            Check(moved.Left == r.Left - 120 && moved.Top == r.Top - 50, $"拖动 {moved.Left - r.Left},{moved.Top - r.Top}");
            Check(Prefs.Get("x", 0) == moved.Left && Prefs.Get("y", 0) == moved.Top, "位置已保存");
            Check(Native.GetForegroundWindow() != Cat, "拖动不抢焦点");

            Click(moved.Left + 135, moved.Top + 100, 8, 16);
            Screen("live_menu");
            Click(400, 330, 2, 4);
            Screen("live_menu_closed");

            // 连点三下：躲开 10 秒再回来；两下、拖着点都不算
            void Clicks(int n, int gap, int dx = 0)
            {
                var at = Where();
                for (int i = 0; i < n; i++)
                {
                    SetCursorPos(at.Left + 135 + dx * i, at.Top + 110);
                    mouse_event(2, 0, 0, 0, IntPtr.Zero);
                    mouse_event(4, 0, 0, 0, IntPtr.Zero);
                    Wait(gap / 1000.0);
                }
                SetCursorPos(400, 330);
            }
            Clicks(2, 120);
            Wait(0.6);
            Check(IsWindowVisible(Cat), "连点两下不躲");
            Clicks(3, 800);
            Wait(0.6);
            Check(IsWindowVisible(Cat), "点得太慢不躲");
            Clicks(3, 120);
            foreach (int ms in new[] { 150, 350, 550 }) { Wait(ms == 150 ? 0.15 : 0.2); Screen($"live_out{ms}"); }
            Wait(0.6);
            Check(!IsWindowVisible(Cat), "连点三下躲起来");
            Wait(8.5);
            Check(!IsWindowVisible(Cat), "9 秒时还躲着");
            var gone = Stopwatch.StartNew();
            while (!IsWindowVisible(Cat) && gone.Elapsed.TotalSeconds < 3) Wait(0.1);
            Check(IsWindowVisible(Cat), $"约 10 秒后回来（消失后 {8.5 + gone.Elapsed.TotalSeconds:0.0}s）");
            foreach (int ms in new[] { 150, 450, 650 }) { Wait(ms == 150 ? 0.15 : 0.2 + (ms == 450 ? 0.1 : 0)); Screen($"live_in{ms}"); }
            Wait(0.6);
            Screen("live_back");
            Check(Where().Left == moved.Left && Where().Top == moved.Top, "回来后位置不变");

            var second = Process.Start(exe);
            Check(second.WaitForExit(5000), "重复启动时第二个自己退出");

            // 浏览器里的密码框
            string edge = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
            if (!File.Exists(edge)) { Console.WriteLine("       没有 Edge，跳过"); return; }
            string page = Path.GetFullPath(Path.Combine(output, "page.html"));
            File.WriteAllText(page, "<title>page</title><body style='background:#cde'><input id=a autofocus><p><input id=b type=password><script>addEventListener('load',()=>{setTimeout(()=>b.focus(),6000);setTimeout(()=>a.focus(),14000)})</script>");
            string profile = Path.Combine(Path.GetTempPath(), "edge-test");
            var before = Screen("live_edge_before");
            Process.Start(edge, $"--no-first-run --no-default-browser-check --user-data-dir=\"{profile}\" --window-position=0,0 --window-size=500,400 --app=\"file:///{page.Replace('\\', '/')}\"");
            Prefs.Set("privacy", false);
            r = Where();
            // 页面加载后 6 秒焦点进密码框、14 秒回到普通框；Edge 启动快慢不定，所以等着看变化
            Bitmap Poll(string name, Func<Bitmap, bool> ok, double seconds)
            {
                var t = Stopwatch.StartNew();
                Bitmap shot;
                do { Wait(0.4); shot = Screen(name); } while (!ok(shot) && t.Elapsed.TotalSeconds < seconds);
                return shot;
            }
            Wait(3);
            var text = Screen("live_edge_text");
            var password = Poll("live_edge_password", b => Difference(text, b, r) > 1500, 25);
            var again = Poll("live_edge_text2", b => Difference(text, b, r) < 600, 20);
            Console.WriteLine($"       Edge：普通框 {Difference(before, text, r)}，密码框 {Difference(text, password, r)}，回到普通框 {Difference(text, again, r)}");
            Check(Difference(text, password, r) > 1500 && Difference(text, again, r) < 600, "Edge 密码框捂眼、离开后恢复");
            foreach (var p in Process.GetProcessesByName("msedge")) try { p.Kill(); } catch (Exception) { }
            Prefs.Set("privacy", true);
            Wait(1);
        }

        static void Drive(Process process, Window target, System.Windows.Controls.TextBox box, System.Windows.Controls.PasswordBox secret, System.Windows.Forms.TextBox native)
        {
            if (process.HasExited)
                foreach (var entry in new EventLog("Application").Entries.Cast<EventLogEntry>().Reverse().Where(x => x.Source == ".NET Runtime").Take(1)) Console.WriteLine(entry.Message);
            var cat = FindWindow(null, "墨团");
            Check(cat != IntPtr.Zero, "猫窗口出现");
            Native.GetWindowRect(cat, out var r);
            Console.WriteLine($"       位置 {r.Left},{r.Top} {r.Width}×{r.Height}，屏幕 {System.Windows.Forms.Screen.PrimaryScreen.Bounds}");

            // 点击穿透：左上角透明处不归猫，身体中间归猫
            Check(Owner(r, 0.04, 0.04) != cat, "透明处点击穿透");
            Check(Owner(r, 0.45, 0.45) == cat, "身体可以点到");

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

            // 隐私模式下只动鼠标：不能误当成打字（猫的右爪和键盘那一块不动）
            var paw = new Native.RECT { Left = r.Left + 70, Top = r.Top + 95, Right = r.Left + 150, Bottom = r.Top + 185 };
            SetCursorPos(400, 330);
            mouse_event(1, 2, 1, 0, IntPtr.Zero);
            Wait(1.5);
            var quiet = Screen("live_mouse_before");
            int wrong = 0;
            for (int i = 0; i < 24; i++)
            {
                SetCursorPos(300 + 8 * i, 300 + 5 * (i % 7));
                mouse_event(1, 2, 1, 0, IntPtr.Zero);
                if (i % 6 == 0) mouse_event(0x800, 0, 0, 120, IntPtr.Zero);
                if (i % 6 == 3) { mouse_event(0x20, 0, 0, 0, IntPtr.Zero); mouse_event(0x40, 0, 0, 0, IntPtr.Zero); }
                Wait(0.1);
                if (Difference(quiet, Screen("live_mouse"), paw) > 100) wrong++;
            }
            Check(wrong == 0, $"只动鼠标不会误当成打字（24 次里误判 {wrong} 次）");

            // 关闭隐私模式：设置每 0.3 秒核对一次
            Prefs.Set("privacy", false);
            target.Activate();
            box.Focus();
            Wait(1);
            int letter = Change(() => Press(0x21, true), () => Press(0x21, false), "live_letter");
            Check(letter > 600, $"读取按键：F 亮起（变化 {letter} 点）");
            Wait(1.5);
            // 眨眼只有 0.13 秒，连拍两张一样才算睁眼的样子
            var openEyes = Screen("live_typed");
            for (int i = 0; i < 5; i++)
            {
                Wait(0.3);
                var again = Screen("live_typed");
                if (Difference(openEyes, again, r) < 100) break;
                openEyes = again;
            }

            // 密码框：猫捂眼；捂眼期间按键无反应
            secret.Focus();
            Wait(1.2);
            var covered = Screen("live_password_wpf");
            Check(Difference(openEyes, covered, r) > 1500, $"辅助功能识别的密码框捂眼（变化 {Difference(openEyes, covered, r)} 点）");
            int leak = Change(() => Press(0x21, true), () => Press(0x21, false), "live_password_key");
            Check(leak < 300, $"密码框里按键不显示（变化 {leak} 点）");

            box.Focus();
            Wait(1.2);
            Check(Enumerable.Range(0, 3).Any(i => { if (i > 0) Wait(0.3); return Difference(openEyes, Screen("live_uncovered"), r) < 600; }), "离开密码框后恢复");

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
