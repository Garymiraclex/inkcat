using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace InkCat
{
    /// <summary>可以单独更换的部位。</summary>
    enum Slot { Cat, Keyboard, Pad, Mouse, Headset, Hat, Scarf }

    static class Slots
    {
        public static readonly Slot[] All = (Slot[])Enum.GetValues(typeof(Slot));
        public static string Id(this Slot s) => s.ToString().ToLowerInvariant();
        public static string Title(this Slot s) => new[] { "猫", "键盘", "桌垫", "鼠标", "耳机", "帽子", "围巾" }[(int)s];
        /// <summary>可以不要的配件；其余部位必须有一个。</summary>
        public static bool IsOptional(this Slot s) => s >= Slot.Headset;
        public static string[] Files(this Slot s) => s == Slot.Cat ? new[] { "idle", "type_both", "sleep", "cover_eyes" } : new[] { s.Id() };
    }

    /// <summary>对素材位置的微调：偏移（画面像素，向右、向下为正）、缩放和旋转（度，顺时针为正）。</summary>
    sealed class Place
    {
        public double x, y, scale = 1, angle;

        [ScriptIgnore] public bool IsHome => x == 0 && y == 0 && scale == 1 && angle == 0;

        public Place Clamped()
        {
            double Limit(double v, double low, double high) => double.IsNaN(v) ? Math.Max(low, 0) : Math.Min(Math.Max(v, low), high);
            return new Place { x = Limit(x, -1200, 1200), y = Limit(y, -896, 896), scale = Limit(scale, 0.1, 5), angle = Limit(angle, -180, 180) };
        }

        /// <summary>绕外框中心缩放旋转，再平移。</summary>
        public Matrix Around(Int32Rect box)
        {
            double cx = box.X + box.Width / 2.0, cy = box.Y + box.Height / 2.0;
            var m = new Matrix();
            m.Translate(-cx, -cy);
            m.Scale(scale, scale);
            m.Rotate(angle);
            m.Translate(cx + x, cy + y);
            return m;
        }
    }

    /// <summary>素材包里可选的 skin.json。坐标都是 1200×896 画面像素，左上原点。</summary>
    sealed class PackConfig
    {
        public sealed class Buttons { public double[][] left, right, wheel; }
        public sealed class Cat { public bool? scarf; public string[] joint; }

        public string name;
        public Dictionary<string, Place> place;
        /// <summary>六排键的两端：x1, y1, x2, y2，从猫的左手端到右手端。</summary>
        public double[][] keys;
        public Buttons buttons;
        public Cat cat;
    }

    sealed class Problem : Exception
    {
        public Problem(string text) : base(text) { }
    }

    /// <summary>一个素材包就是一个文件夹，有哪些文件就提供哪些部位。内置的在程序资源里。</summary>
    sealed class Pack
    {
        public const string Classic = "classic";

        public string Id, Title, Folder, Problem;
        public Slot[] Slots;
        public PackConfig Config = new PackConfig();
        public bool IsBuiltin => Folder == null;

        public bool Has(string file) => IsBuiltin ? Art.Exists($"packs/{Id}/{file}") : File.Exists(Path.Combine(Folder, file));

        /// <summary>只读普通文件，不跟随链接，限制大小。</summary>
        public byte[] Read(string file, int limit = 20 << 20)
        {
            if (IsBuiltin) return Art.Read($"packs/{Id}/{file}");
            var info = new FileInfo(Path.Combine(Folder, file));
            if (!info.Exists) throw new Problem($"找不到 {file}");
            if ((info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0) throw new Problem($"{file} 不是普通文件");
            if (info.Length > limit) throw new Problem($"{file} 太大，不能超过 {limit >> 20} MB");
            try { return File.ReadAllBytes(info.FullName); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { throw new Problem($"{file} 打不开"); }
        }

        public Raster Image(string name) => Cutout.Clean(Library.Decode(Read(name + ".png"), name), name);
    }

    /// <summary>程序自带的图。</summary>
    static class Art
    {
        static Uri At(string path) => new Uri($"pack://application:,,,/InkCat;component/assets/{path}");

        public static bool Exists(string path)
        {
            try { using (Application.GetResourceStream(At(path)).Stream) return true; }
            catch (IOException) { return false; }
        }

        public static byte[] Read(string path)
        {
            using (var stream = Application.GetResourceStream(At(path)).Stream)
            using (var copy = new MemoryStream())
            {
                stream.CopyTo(copy);
                return copy.ToArray();
            }
        }

        public static BitmapSource Load(string name)
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = At(name + ".png");
            image.EndInit();
            image.Freeze();
            return image;
        }

        /// <summary>按显示尺寸预先缩小，动起来就不必每帧重新采样。</summary>
        public static BitmapSource Shrink(BitmapSource image, double scale)
        {
            if (scale >= 1) return image;
            image = new CachedBitmap(new TransformedBitmap(image, new ScaleTransform(scale, scale)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            image.Freeze();
            return image;
        }
    }

    /// <summary>素材文件夹在 %APPDATA%\InkCat\素材，拆好的猫缓存在 %LOCALAPPDATA%\InkCat\cats。</summary>
    static class Library
    {
        static string In(Environment.SpecialFolder root, params string[] path) => Path.Combine(new[] { Environment.GetFolderPath(root), "InkCat" }.Concat(path).ToArray());
        public static readonly string Folder = In(Environment.SpecialFolder.ApplicationData, "素材");
        public static readonly string Cache = In(Environment.SpecialFolder.LocalApplicationData, "cats");

        public static Pack[] Scan()
        {
            var own = new[] { Pack.Classic, "gamer" }.Select(id => Describe(new Pack { Id = id, Title = id }));
            var users = new DirectoryInfo(Folder).Exists
                ? new DirectoryInfo(Folder).GetDirectories()
                    .Where(d => !d.Name.StartsWith("_") && !d.Name.StartsWith(".") && (d.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Hidden)) == 0)
                    .Select(d => Describe(new Pack { Id = "user/" + d.Name, Title = d.Name, Folder = d.FullName }))
                    .Where(p => p.Slots.Length > 0 || p.Problem != null).OrderBy(p => p.Title, StringComparer.CurrentCulture)
                : Enumerable.Empty<Pack>();
            return own.Concat(users).ToArray();
        }

        static Pack Describe(Pack pack)
        {
            if (pack.Has("skin.json"))
                try { pack.Config = new JavaScriptSerializer().Deserialize<PackConfig>(Encoding.UTF8.GetString(pack.Read("skin.json", 1 << 20)).TrimStart('﻿')) ?? new PackConfig(); }
                catch (Exception) { pack.Problem = "skin.json 看不懂，已忽略"; }
            pack.Title = pack.Config.name ?? pack.Title;
            var missing = Slot.Cat.Files().Where(f => !pack.Has(f + ".png")).ToArray();
            if (missing.Length > 0 && missing.Length < 4) pack.Problem = "猫还缺 " + string.Join("、", missing.Select(f => f + ".png"));
            pack.Slots = Slots.All.Where(s => s.Files().All(f => pack.Has(f + ".png"))).ToArray();
            return pack;
        }

        /// <summary>读成 1200×896；同比例的大图会缩小。</summary>
        public static Raster Decode(byte[] data, string name)
        {
            if (data.Length > 12 && data[0] == 0x89 && data[1] == 'P' && Encoding.ASCII.GetString(data, data.Length - 12, 12).IndexOf("IEND", StringComparison.Ordinal) < 0)
                throw new Problem($"{name}.png 不完整");  // 还在复制
            try
            {
                var frame = BitmapDecoder.Create(new MemoryStream(data), BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
                int w = frame.PixelWidth, h = frame.PixelHeight;
                if (w > 4096 || h > 4096 || w < 600 || Math.Abs((double)w / h - 1200 / 896.0) >= 0.004) throw new Problem($"{name}.png 是 {w}×{h}，需要 1200×896");
                return w == Cutter.W && h == Cutter.H ? new Raster(frame) : Resize(frame);
            }
            catch (Exception e) when (!(e is Problem)) { throw new Problem($"{name}.png 打不开"); }
        }

        static Raster Resize(BitmapSource image)
        {
            image = new FormatConvertedBitmap(image, PixelFormats.Pbgra32, null, 0);
            var scaled = new TransformedBitmap(image, new ScaleTransform((double)Cutter.W / image.PixelWidth, (double)Cutter.H / image.PixelHeight));
            var r = new Raster(scaled);
            return r.Width == Cutter.W && r.Height == Cutter.H ? r : r.Crop(0, 0, Cutter.W, Cutter.H);
        }

        /// <summary>只改位置，skin.json 里别的内容原样保留。</summary>
        public static void SavePlace(Pack pack, Slot slot, Place place)
        {
            var json = new JavaScriptSerializer();
            string path = Path.Combine(pack.Folder, "skin.json");
            Dictionary<string, object> all = null;
            try { if (File.Exists(path)) all = json.DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>; }
            catch (Exception) { }
            all = all ?? new Dictionary<string, object>();
            if (!(all.TryGetValue("place", out var old) && old is Dictionary<string, object> places)) all["place"] = places = new Dictionary<string, object>();
            places[slot.Id()] = place;
            try { File.WriteAllText(path, json.Serialize(all)); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
        }

        /// <summary>建好素材文件夹，放一份模板；名字以 _ 开头的文件夹不会被当成素材包。</summary>
        public static void Prepare()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllBytes(Path.Combine(Folder, "素材说明.txt"), new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Art.Read("guide.txt")).ToArray());
                foreach (var (id, title) in new[] { (Pack.Classic, "原版"), ("gamer", "电竞") })
                {
                    string to = Path.Combine(Folder, "_模板", title);
                    if (Directory.Exists(to)) continue;
                    Directory.CreateDirectory(to);
                    var files = Slots.All.SelectMany(s => s.Files()).SelectMany(f => new[] { f + ".png", f + "_back.png" }).Append("skin.json");
                    foreach (string f in files.Where(f => Art.Exists($"packs/{id}/{f}"))) File.WriteAllBytes(Path.Combine(to, f), Art.Read($"packs/{id}/{f}"));
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
        }
    }

    /// <summary>把图整理成透明底。</summary>
    static class Cutout
    {
        /// <summary>背景不透明时，四边颜色一致就从边上往里清掉，否则报错。</summary>
        public static Raster Clean(Raster image, string name)
        {
            const int W = Cutter.W, H = Cutter.H;
            var px = image.Pixels;
            var border = Enumerable.Range(0, W).SelectMany(x => new[] { x, (H - 1) * W + x })
                .Concat(Enumerable.Range(0, H).SelectMany(y => new[] { y * W, y * W + W - 1 })).ToArray();
            if (border.Count(i => px[i].W > 200 / 255f) * 2 < border.Length) return image;

            float Far(int i, Vector4 q)
            {
                var d = Vector4.Abs(px[i] - q) * 255;
                return Math.Max(d.X, Math.Max(d.Y, d.Z));
            }
            var paper = px[0];
            if (border.Any(i => Far(i, paper) >= 24)) throw new Problem($"{name}.png 的背景不是透明的");
            var outside = new bool[W * H];
            var queue = new Stack<int>(border);
            foreach (int i in border) outside[i] = true;
            while (queue.Count > 0)
            {
                int i = queue.Pop(), x = i % W;
                foreach (int j in new[] { x > 0 ? i - 1 : i, x < W - 1 ? i + 1 : i, Math.Max(i - W, 0), Math.Min(i + W, W * H - 1) })
                    if (!outside[j] && Far(j, paper) < 30)
                    {
                        outside[j] = true;
                        queue.Push(j);
                    }
            }
            var o = new Raster();
            for (int y = 1; y < H - 1; y++)
                for (int x = 1; x < W - 1; x++)
                {
                    int i = y * W + x;
                    if (outside[i]) continue;
                    int open = (outside[i - 1] ? 1 : 0) + (outside[i + 1] ? 1 : 0) + (outside[i - W] ? 1 : 0) + (outside[i + W] ? 1 : 0);
                    if (open == 0) { o.Pixels[i] = px[i]; continue; }
                    float a = Math.Max(0.35f, 1 - 0.25f * open);  // 边缘一圈去掉底色、变半透明
                    var v = Vector4.Min(new Vector4(a), Vector4.Max(Vector4.Zero, px[i] - paper * (1 - a)));
                    v.W = a;
                    o.Pixels[i] = v;
                }
            return o;
        }
    }
}
