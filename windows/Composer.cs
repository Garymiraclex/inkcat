using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media.Imaging;

namespace InkCat
{
    /// <summary>拆好的一只猫。</summary>
    sealed class CookedCat
    {
        public static readonly string[] Names = { "body", "body_cover", "raised_arms", "tail", "face_sleep", "eyes_focus", "eyes_tense", "shoulder", "arm_left", "arm_mouse", "arm_right", "arm_right_in" };
        public Dictionary<string, Raster> Images;
        /// <summary>右肩补块的毛色和描边；不需要补块（鬃毛盖着手臂根部）时为空。</summary>
        public int[] Joint;
        /// <summary>给用户的提醒。</summary>
        public string Note = "";
    }

    /// <summary>一身装扮的成品：猫窗口直接使用的图和坐标。没有 Images 就是原版，用内置的图。</summary>
    sealed class Look
    {
        public Dictionary<string, BitmapSource> Images = new Dictionary<string, BitmapSource>();
        public Size MouseSize = new Size(176, 136);
        /// <summary>鼠标图里的左右键、滚轮轮廓，左上原点。</summary>
        public Point[] LeftButton = ClassicLeft, RightButton = ClassicRight, Wheel = ClassicWheel;
        public Point[][] Rows = Keyboard.ClassicRows;
        /// <summary>右肩补块的颜色；为空则不补，肩头一直盖在手臂上。</summary>
        public int[] Joint = { 0xB59B70, 0x3B2418 };

        public BitmapSource Image(string name, double scale = 1) => Art.Shrink(Images.TryGetValue(name, out var own) ? own : Art.Load(name), scale);

        /// <summary>原版鼠标（画面坐标 689…844 × 656…777）的按键，以鼠标图左上角 (680, 648) 为原点。</summary>
        public static readonly Point[]
            ClassicLeft = Cutter.Points("40,109 43,98 48,91 54,86 64,68 72,50 98,60 112,72 101,92 90,104 76,110 56,112"),
            ClassicRight = Cutter.Points("35,108 38,98 42,90 39,84 43,72 50,62 60,59 64,50 50,35 38,47 24,67 17,84 18,97 26,104"),
            ClassicWheel = Cutter.Points("42,87 42,80 47,71 54,64 62,64 62,69 56,79 51,87 46,89");
    }

    sealed class Choice
    {
        public Pack Pack;
        public Place Place = new Place();
    }

    /// <summary>把选中的部位合成一身装扮。只在一个后台线程里使用。</summary>
    sealed class Composer
    {
        public sealed class Result
        {
            public Look Look = new Look();
            public readonly Dictionary<Slot, string> Problems = new Dictionary<Slot, string>(), Warnings = new Dictionary<Slot, string>();
        }

        readonly Dictionary<string, CookedCat> cats = new Dictionary<string, CookedCat>();

        public Result Compose(Dictionary<Slot, Choice> wanted, Pack fallback)
        {
            var result = new Result();
            var outfit = new Dictionary<Slot, Choice>(wanted);
            T Attempt<T>(Slot slot, Func<T> body) where T : class
            {
                try { return body(); }
                catch (Problem e) { result.Problems[slot] = e.Message; return null; }
            }
            // 原版不合成，保证一个像素都不变
            if (outfit.Values.All(c => c.Pack.Id == Pack.Classic && c.Place.IsHome)) return result;

            // 出错的部位退回原版，配件则不戴
            Raster Layer(Slot slot)
            {
                if (!outfit.TryGetValue(slot, out var c)) return null;
                var image = Attempt(slot, () => Part(c, slot.Id()));
                if (image != null) return image;
                outfit.Remove(slot);
                if (slot.IsOptional()) return null;
                outfit[slot] = new Choice { Pack = fallback };
                return Part(outfit[slot], slot.Id());
            }
            Raster Back(Slot slot) => outfit.TryGetValue(slot, out var c) && c.Pack.Has(slot.Id() + "_back.png") ? Attempt(slot, () => Part(c, slot.Id() + "_back")) : null;

            var chosen = outfit[Slot.Cat].Pack;
            var cat = Attempt(Slot.Cat, () => Cook(chosen, fallback));
            if (cat == null) cat = Cook(fallback, fallback);
            else if (cat.Note != "") result.Warnings[Slot.Cat] = cat.Note;
            Raster pad = Layer(Slot.Pad), keyboard = Layer(Slot.Keyboard), mouse = Layer(Slot.Mouse), headset = Layer(Slot.Headset), hat = Layer(Slot.Hat);
            bool knitted = chosen.Config.cat?.scarf == false;
            var scarf = knitted ? null : Layer(Slot.Scarf);
            if (knitted && outfit.ContainsKey(Slot.Scarf)) result.Warnings[Slot.Scarf] = "这只猫身上画着围巾，不能再戴";
            var front = new[] { scarf, headset, hat }.Where(r => r != null).ToArray();
            var behind = new[] { Back(Slot.Headset), Back(Slot.Hat) }.Where(r => r != null).ToArray();

            Raster Flatten(params Raster[] layers) => layers.Aggregate(new Raster(), (o, layer) => o.Under(layer));
            Raster Plate(string body, params Raster[] over) => Flatten(behind.Append(cat.Images[body]).Concat(front).Concat(over).Append(pad).Append(keyboard).ToArray());
            var images = new Dictionary<string, Raster>(cat.Images)
            {
                ["plate"] = Plate("body"),
                ["plate_cover"] = Plate("body_cover", cat.Images["raised_arms"]),
            };
            // 睡脸和眯眼贴在底图上面，要给配件让位
            foreach (var (name, box) in new[] { ("face_sleep", new Int32Rect(400, 200, 360, 272)), ("eyes_focus", new Int32Rect(400, 250, 290, 150)), ("eyes_tense", new Int32Rect(400, 250, 290, 150)) })
                images[name] = front.Aggregate(images[name].Copy(), (o, f) => o.Erase(f.Crop(box)));
            if (scarf != null && cat.Joint.Length == 0) images["shoulder"] = images["shoulder"].Copy().Under(scarf.Crop(396, 440, 120, 120));  // 一直盖在手臂上的鬃毛，外面还有围巾
            var frame = MouseBox(mouse);
            images["mouse"] = mouse.Crop(frame);

            var look = result.Look;
            look.Images = images.ToDictionary(p => p.Key, p => p.Value.Image);
            look.MouseSize = new Size(frame.Width, frame.Height);
            look.Joint = cat.Joint;
            look.Rows = Rows(outfit[Slot.Keyboard]);
            var own = outfit[Slot.Mouse].Pack.Config.buttons;
            look.LeftButton = Button(outfit[Slot.Mouse], own?.left, Look.ClassicLeft, frame, mouse);
            look.RightButton = Button(outfit[Slot.Mouse], own?.right, Look.ClassicRight, frame, mouse);
            look.Wheel = Button(outfit[Slot.Mouse], own?.wheel, Look.ClassicWheel, frame, mouse);
            return result;
        }

        // ———— 单个部位 ————

        static Raster Part(Choice c, string name)
        {
            var image = c.Pack.Image(name);
            var box = image.Bounds ?? throw new Problem($"{name}.png 是空的");
            if (c.Place.IsHome) return image;
            var moved = image.Transformed(c.Place.Around(box));
            return moved.Bounds != null ? moved : throw new Problem($"{name}.png 被移到画面外了");
        }

        static bool Usable(double[][] points, int size) => points != null && points.All(p => p != null && p.Length == size && p.All(v => !double.IsNaN(v) && !double.IsInfinity(v)));

        /// <summary>键位跟着键盘一起移动、缩放。</summary>
        static Point[][] Rows(Choice c)
        {
            var rows = Keyboard.ClassicRows;
            var own = c.Pack.Config.keys;
            if (Usable(own, 4) && own.Length == 6) rows = own.Select(k => new[] { new Point(k[0], k[1]), new Point(k[2], k[3]) }).ToArray();
            if (c.Place.IsHome) return rows;
            var m = c.Place.Around(c.Pack.Image("keyboard").Bounds.Value);
            return rows.Select(r => r.Select(m.Transform).ToArray()).ToArray();
        }

        /// <summary>鼠标图的裁切框：居中，至少和原版一样大。</summary>
        static Int32Rect MouseBox(Raster mouse)
        {
            var b = mouse.Bounds.Value;
            int w = Math.Max(176, b.Width + 4), h = Math.Max(136, b.Height + 4);
            int Start(double middle, int size) => (int)Math.Round(middle - size / 2.0, MidpointRounding.AwayFromZero);
            int x = Math.Max(0, Start(b.X + b.Width / 2.0, w)), y = Math.Max(0, Start(b.Y + b.Height / 2.0, h));
            return new Int32Rect(x, y, Math.Min(w, Cutter.W - x), Math.Min(h, Cutter.H - y));
        }

        /// <summary>素材包写了就用，没写就把原版的按键按鼠标外框缩放过去。</summary>
        static Point[] Button(Choice c, double[][] given, Point[] classic, Int32Rect box, Raster mouse)
        {
            if (Usable(given, 2) && given.Length > 2)
            {
                var m = c.Place.IsHome ? System.Windows.Media.Matrix.Identity : c.Place.Around(c.Pack.Image("mouse").Bounds.Value);
                return given.Select(p => m.Transform(new Point(p[0], p[1])) - new Vector(box.X, box.Y)).ToArray();
            }
            var b = mouse.Bounds.Value;
            return classic.Select(p => new Point(b.X + (p.X + 680 - 689) / 156 * b.Width - box.X, b.Y + (p.Y + 648 - 656) / 122 * b.Height - box.Y)).ToArray();
        }

        // ———— 拆猫 ————

        CookedCat Cook(Pack pack, Pack template)
        {
            var data = Slot.Cat.Files().Select(f => pack.Read(f + ".png")).ToArray();
            var bare = pack.Has("no_arms.png") ? pack.Read("no_arms.png") : null;
            string key;
            using (var sha = SHA256.Create())
            {
                byte[] all = Encoding.UTF8.GetBytes("16").Concat(data.Concat(bare == null ? new byte[0][] : new[] { bare }).SelectMany(d => sha.ComputeHash(d))).ToArray();  // 拆图算法变了就加一
                key = string.Concat(sha.ComputeHash(all).Select(b => b.ToString("x2")));
            }
            string cached = Path.Combine(Library.Cache, key);
            if (!cats.TryGetValue(key, out var cat))
            {
                cat = Restore(cached);
                if (cat == null)
                {
                    var frames = Slot.Cat.Files().Zip(data, (name, d) => Cutout.Clean(Library.Decode(d, name), name)).ToArray();
                    var extra = bare == null ? null : Cutout.Clean(Library.Decode(bare, "no_arms"), "no_arms");
                    string note = Check(extra == null ? frames : frames.Append(extra).ToArray(), pack.Id == template.Id ? null : template);
                    cat = Cut(new Cutter.Cat(frames[0], frames[1], frames[3], frames[2], extra));
                    cat.Note = note;
                    Store(cat, cached);
                }
                if (cats.Count > 2) cats.Clear();
                cats[key] = cat;
            }
            var joint = pack.Config.cat?.joint;
            if (joint?.Length == 2 && cat.Joint.Length == 2
                && int.TryParse(joint[0]?.TrimStart('#'), NumberStyles.HexNumber, null, out int fur) && int.TryParse(joint[1]?.TrimStart('#'), NumberStyles.HexNumber, null, out int ink))
                return new CookedCat { Images = cat.Images, Joint = new[] { fur, ink }, Note = cat.Note };
            return cat;
        }

        /// <summary>帧与帧对不齐就没法用；和模板差太多则提醒。</summary>
        public static string Check(Raster[] frames, Pack template)
        {
            for (int n = 1; n < frames.Length; n++)  // 比耳朵和头顶，各帧里都不该动
            {
                int moved = 0, total = 0;
                for (int y = 40; y < 240; y += 2)
                    for (int x = 380; x < 830; x += 2)
                    {
                        total++;
                        if (Cutter.Differs(frames[n][x, y], frames[0][x, y])) moved++;
                    }
                if (moved * 100 > total * 12) throw new Problem($"{Slot.Cat.Files().Append("no_arms").ElementAt(n)}.png 和 idle.png 的头没有对齐");
            }
            if (template == null) return "";
            var t = template.Image("idle");
            int both = 0, either = 0;
            for (int i = 0; i < t.Pixels.Length; i++)
            {
                bool a = t.Pixels[i].W > 0.5f, b = frames[0].Pixels[i].W > 0.5f;
                if (a && b) both++;
                if (a || b) either++;
            }
            return both * 100 < either * 88 ? "姿势和模板差得比较多，手臂和尾巴可能拆不干净" : "";
        }

        public static CookedCat Cut(Cutter.Cat c)
        {
            var cover = Cutter.Tailless(Cutter.Trim(c.Cover), c.Back);
            // 去掉桌面；腹部下缘用捂眼那张，没有桌沿。每列最低处往上 10 像素的毛色（避开边缘一圈亮线）向下延到桌沿线以下 16 像素，垫在键盘、鼠标垫底下，免得露出缝
            Raster Torso(Raster f)
            {
                var o = f.Copy();
                for (int x = 0; x < Cutter.W; x++)
                {
                    int start = Cutter.H, last = 539;
                    for (int y = 540; y < Cutter.H; y++) if (Cutter.OnDesk(x, y)) { start = y; break; }
                    for (int y = 540; y < start; y++)
                    {
                        if (x >= 455)
                        {
                            float t = (float)Math.Max(0, Math.Min(1, (y - 614 - 0.145 * (x - 660)) / 6));
                            o[x, y] = o[x, y] * (1 - t) + cover[x, y] * t;
                        }
                        if (o[x, y].W > 0.5f) last = y;
                    }
                    var edge = o[x, last - 10];
                    int reach = edge.W > 0.5f && start - last < 30 ? start + 16 : 0;  // 离桌沿太远的不延
                    for (int y = reach > 0 ? last + 1 : start; y < Cutter.H; y++) o[x, y] = y < reach ? edge : System.Numerics.Vector4.Zero;
                }
                return o;
            }
            var (full, tucked) = c.RightArm();
            var bodyCover = Torso(cover);
            return new CookedCat
            {
                Images = new Dictionary<string, Raster>
                {
                    ["body"] = Torso(Cutter.Tailless(c.Armless(c.Idle), c.Back)),
                    ["body_cover"] = bodyCover,
                    ["raised_arms"] = Cutter.Cut(bodyCover, Cutter.RaisedArms),
                    ["tail"] = c.Tail.Crop(860, 290, 240, 400),
                    ["face_sleep"] = c.Asleep.Crop(400, 200, 360, 272),
                    ["eyes_focus"] = c.Squint(0.38, -0.02).Crop(400, 250, 290, 150),
                    ["eyes_tense"] = c.Squint(0.95, 0.05).Crop(400, 250, 290, 150),
                    ["shoulder"] = c.Shoulder.Crop(396, 440, 120, 120),
                    ["arm_left"] = Cutter.Trimmed(c.LeftArm, 150),
                    ["arm_mouse"] = Cutter.Trimmed(c.MouseArm, 150),
                    ["arm_right"] = Cutter.Trimmed(full, 80),
                    ["arm_right_in"] = Cutter.Trimmed(tucked, 80),
                },
                Joint = c.RightArmRests ? new int[0] : new[] { ShoulderFur(c.Typing), 0x3B2418 },
            };
        }

        /// <summary>右肩补块的毛色取肩头附近的。</summary>
        static int ShoulderFur(Raster typing)
        {
            var near = new List<System.Numerics.Vector4>();
            for (int y = 490; y < 520; y++)
                for (int x = 405; x < 435; x++)
                    if (typing[x, y].W > 0.9f && (x - 420) * (x - 420) + (y - 505) * (y - 505) < 225) near.Add(typing[x, y]);
            if (near.Count == 0) return 0xB59B70;
            var v = near.OrderBy(Cutter.Luma).ElementAt(near.Count * 2 / 3) * 255;
            return (int)Math.Round(v.X) << 16 | (int)Math.Round(v.Y) << 8 | (int)Math.Round(v.Z);
        }

        // ———— 缓存 ————

        static void Store(CookedCat cat, string path)
        {
            try
            {
                string temp = path + ".part";
                if (Directory.Exists(temp)) Directory.Delete(temp, true);
                Directory.CreateDirectory(temp);
                foreach (var p in cat.Images)
                    using (var file = File.Create(Path.Combine(temp, p.Key + ".png")))
                        new PngBitmapEncoder { Frames = { BitmapFrame.Create(p.Value.Image) } }.Save(file);
                File.WriteAllText(Path.Combine(temp, "joint.json"), new JavaScriptSerializer().Serialize(cat.Joint));
                File.WriteAllText(Path.Combine(temp, "note.txt"), cat.Note);
                if (Directory.Exists(path)) Directory.Delete(path, true);
                Directory.Move(temp, path);  // 写完整才启用
                foreach (var old in new DirectoryInfo(Library.Cache).GetDirectories().OrderByDescending(d => d.LastWriteTimeUtc).Skip(8)) old.Delete(true);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }  // 存不下就下次再拆
        }

        static CookedCat Restore(string path)
        {
            try
            {
                var cat = new CookedCat
                {
                    Images = CookedCat.Names.ToDictionary(n => n, n => new Raster(
                        BitmapDecoder.Create(new MemoryStream(File.ReadAllBytes(Path.Combine(path, n + ".png"))), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0])),
                    Joint = new JavaScriptSerializer().Deserialize<int[]>(File.ReadAllText(Path.Combine(path, "joint.json"))),
                    Note = File.ReadAllText(Path.Combine(path, "note.txt")),
                };
                return cat.Joint?.Length == 0 || cat.Joint?.Length == 2 ? cat : null;
            }
            catch (Exception) { return null; }  // 没有缓存或坏了
        }
    }
}
