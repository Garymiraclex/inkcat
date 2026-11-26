using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace InkCat
{
    enum Pose { Idle, TypeBoth, Sleep, CoverEyes, NoArms }

    static class Poses
    {
        public static readonly Pose[] All = (Pose[])Enum.GetValues(typeof(Pose));
        public static string Id(this Pose p) => new[] { "idle", "type_both", "sleep", "cover_eyes", "no_arms" }[(int)p];
        public static string Title(this Pose p) => new[] { "平时", "打字", "睡觉", "捂眼", "无臂" }[(int)p];
        public static bool IsOptional(this Pose p) => p == Pose.NoArms;

        static readonly Dictionary<Pose, Raster> templates = new Dictionary<Pose, Raster>();
        /// <summary>原版的同一姿势；无臂没有原版，用平时。</summary>
        public static Raster Template(this Pose p)
        {
            lock (templates)
            {
                if (!templates.TryGetValue(p, out var t)) templates[p] = t = Library.Decode(Art.Read($"packs/classic/{(p == Pose.NoArms ? "idle" : p.Id())}.png"), p.Id());
                return t;
            }
        }
    }

    sealed class ImportResult
    {
        public readonly Dictionary<Pose, Raster> Images = new Dictionary<Pose, Raster>();
        public readonly Dictionary<Pose, string> Notes = new Dictionary<Pose, string>(), Problems = new Dictionary<Pose, string>();
        /// <summary>整套的提醒，如姿势和模板差得多。</summary>
        public string Warning;
        public bool IsReady => Problems.Count == 0 && Poses.All.All(p => p.IsOptional() || Images.ContainsKey(p));
    }

    /// <summary>把生图 AI 给的原图整理成一只猫的素材包：去底、去水印、对齐、检查。</summary>
    static class Importer
    {
        const int W = Cutter.W, H = Cutter.H;

        public static Raster Load(byte[] data)
        {
            BitmapSource frame;
            try { frame = BitmapDecoder.Create(new MemoryStream(data), BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad).Frames[0]; }
            catch (Exception) { throw new Problem("打不开这张图"); }
            if (!Library.Fits(frame.PixelWidth, frame.PixelHeight)) throw new Problem($"图是 {frame.PixelWidth}×{frame.PixelHeight}，需要 4:3 横图，宽 600 到 4096");
            return Library.Fit(frame);
        }

        public static ImportResult Process(Dictionary<Pose, Raster> raw)
        {
            var result = new ImportResult();
            var aligned = new Dictionary<Pose, Raster>();
            var zone = Zone();
            void Note(Pose pose, string text) => result.Notes[pose] = result.Notes.TryGetValue(pose, out var old) ? old + "；" + text : text;

            // 先粗抠找位置，对齐后再按同一姿势的原版轮廓细抠，浅色的毛不会被当成背景
            void Prepare(Pose pose, float[] reference)
            {
                var b = raw[pose].Copy();  // 原图留着，换格子时重算
                try
                {
                    var paper = Cutout.Paper(b);
                    var rough = paper == null ? b : Cutout.Knock(b, paper);
                    var template = Alpha(pose.Template());
                    var shift = Align(Alpha(rough), reference ?? template);
                    if (!shift.IsNone) b = Moved(b, shift, paper?[0]);
                    if (paper != null) b = Cutout.Knock(b, paper, pose == Pose.NoArms ? null : template);
                    if (DropStrays(b, zone)) Note(pose, "去掉了画面边上的零散小块（如水印）");
                    if (shift.Text != null) Note(pose, shift.Text);
                    aligned[pose] = b;
                }
                catch (Problem e) { result.Problems[pose] = e.Message; }
            }
            if (raw.ContainsKey(Pose.Idle)) Prepare(Pose.Idle, null);  // 平时那张对准原版，其余对准平时
            var baseline = aligned.TryGetValue(Pose.Idle, out var idle) ? Alpha(idle) : null;
            foreach (var pose in Poses.All.Where(p => p != Pose.Idle && raw.ContainsKey(p))) Prepare(pose, baseline);
            foreach (var p in aligned) result.Images[p.Key] = p.Value;

            // 两张一样，多半是存错了
            var have = Poses.All.Where(aligned.ContainsKey).ToArray();
            for (int i = 0; i < have.Length; i++)
                foreach (var other in have.Take(i))
                    if (Same(aligned[have[i]], aligned[other])) result.Problems[have[i]] = $"和「{other.Title()}」是同一张图";
            if (aligned.TryGetValue(Pose.CoverEyes, out var cover) && !MouseShows(cover, Pose.CoverEyes.Template())) Note(Pose.CoverEyes, "鼠标好像被挡住或画歪了，捂眼时要完整露出");
            if (result.Problems.Count == 0 && Poses.All.Take(4).All(aligned.ContainsKey))
            {
                var frames = Poses.All.Where(aligned.ContainsKey).Select(p => aligned[p]).ToArray();
                try
                {
                    string note = Composer.Check(frames, Library.Scan().First(p => p.Id == Pack.Classic));
                    result.Warning = note == "" ? null : note;
                }
                catch (Problem e)  // 如「type_both.png 和 idle.png 的头没有对齐」
                {
                    var pose = Poses.All.FirstOrDefault(p => e.Message.StartsWith(p.Id() + ".png"));
                    result.Problems[pose] = "头和「平时」那张对不上，可能不是同一套图";
                }
            }
            return result;
        }

        /// <summary>存成素材文件夹里的一个素材包，返回它的 id。</summary>
        public static string Save(Dictionary<Pose, Raster> images, string raw)
        {
            string name = (raw ?? "").Trim();
            if (name == "" || name.StartsWith(".") || name.StartsWith("_") || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new Problem("名字不能为空，不能以 . 或 _ 开头，不能有 \\ / : * ? \" < > |");
            string folder = Path.Combine(Library.Folder, name), temp = Path.Combine(Library.Folder, "_导入中");  // _ 开头的不会被扫描；写完再改名
            if (Directory.Exists(folder) || File.Exists(folder)) throw new Problem($"素材文件夹里已经有「{name}」了，换个名字");
            try
            {
                if (Directory.Exists(temp)) Directory.Delete(temp, true);
                Directory.CreateDirectory(temp);
                foreach (var p in images)
                    using (var file = File.Create(Path.Combine(temp, p.Key.Id() + ".png")))
                        new PngBitmapEncoder { Frames = { BitmapFrame.Create(p.Value.Image) } }.Save(file);
                Directory.Move(temp, folder);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { throw new Problem("存不了：" + e.Message); }
            return "user/" + name;
        }

        // ———— 对齐 ————

        public struct Shift
        {
            public int Dx, Dy;
            public double Scale;
            public bool IsNone => Dx == 0 && Dy == 0 && Scale == 1;
            public string Text
            {
                get
                {
                    if (IsNone) return null;
                    var parts = new List<string>();
                    if (Dx != 0 || Dy != 0) parts.Add($"挪了 {Math.Round(Math.Sqrt(Dx * Dx + Dy * Dy))} 像素");
                    if (Scale != 1) parts.Add($"{(Scale > 1 ? "放大" : "缩小")} {Math.Abs(Scale - 1) * 100:0}%");
                    bool far = Math.Abs(Dx) >= 22 || Math.Abs(Dy) >= 22 || Math.Abs(Scale - 1) > 0.035;
                    return "已对齐：" + string.Join("，", parts) + (far ? "。偏得很多，看看是不是放错了格子" : "");
                }
            }
        }

        public static float[] Alpha(Raster r) => r.Pixels.Select(p => p.W).ToArray();

        /// <summary>找让轮廓最贴合的平移（±24 像素）和缩放（±4%）：先在四分之一大小上粗找，再在原尺寸上细调。</summary>
        public static Shift Align(float[] a, float[] reference)
        {
            float[] Shrink(float[] m)
            {
                var o = new float[(W / 4) * (H / 4)];
                for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) o[(y / 4) * (W / 4) + x / 4] += m[y * W + x] / 16;
                return o;
            }
            // 平移、缩放后与参考图的差；缩放绕画面中心
            float Cost(float[] m, float[] r, int w, int h, double dx, double dy, double s, int step)
            {
                double cx = w / 2.0, cy = h / 2.0;
                float total = 0;
                for (int y = 0; y < h; y += step)
                {
                    int sy = (int)Math.Round((y - cy - dy) / s + cy, MidpointRounding.AwayFromZero);
                    for (int x = 0; x < w; x += step)
                    {
                        int sx = (int)Math.Round((x - cx - dx) / s + cx, MidpointRounding.AwayFromZero);
                        float v = sx >= 0 && sy >= 0 && sx < w && sy < h ? m[sy * w + sx] : 0;
                        total += Math.Abs(v - r[y * w + x]);
                    }
                }
                return total;
            }
            float[] small = Shrink(a), smallRef = Shrink(reference);
            var best = (dx: 0, dy: 0, s: 1.0);
            float least = Cost(small, smallRef, W / 4, H / 4, 0, 0, 1, 1);
            for (int k = 0; k <= 8; k++)
                for (int dy = -6; dy <= 6; dy++)
                    for (int dx = -6; dx <= 6; dx++)
                    {
                        double s = Math.Round(0.96 + 0.01 * k, 2);
                        float c = Cost(small, smallRef, W / 4, H / 4, dx, dy, s, 1);
                        if (c < least * 0.995f) (best, least) = ((dx, dy, s), c);
                    }
            var fine = new Shift { Dx = best.dx * 4, Dy = best.dy * 4, Scale = best.s };
            var start = fine;
            least = Cost(a, reference, W, H, fine.Dx, fine.Dy, fine.Scale, 2);
            for (int dy = -3; dy <= 3; dy++)
                for (int dx = -3; dx <= 3; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    float c = Cost(a, reference, W, H, start.Dx + dx, start.Dy + dy, start.Scale, 2);
                    if (c < least * 0.998f) (fine.Dx, fine.Dy, least) = (start.Dx + dx, start.Dy + dy, c);
                }
            return fine;
        }

        /// <summary>挪动后空出来的边填上背景色。</summary>
        static Raster Moved(Raster r, Shift s, Vector3? fill)
        {
            var m = new Matrix();
            m.Translate(-W / 2.0, -H / 2.0);
            m.Scale(s.Scale, s.Scale);
            m.Translate(W / 2.0 + s.Dx, H / 2.0 + s.Dy);
            var o = r.Transformed(m);
            if (fill is Vector3 f)
            {
                var paper = new Vector4(f / 255, 1);
                for (int i = 0; i < o.Pixels.Length; i++) o.Pixels[i] += paper * (1 - o.Pixels[i].W);
            }
            return o;
        }

        // ———— 检查 ————

        static bool[] zone;
        /// <summary>原版各姿势占到的地方外扩 60 像素；完全在外面的小块是水印或杂点。</summary>
        static bool[] Zone()
        {
            if (zone != null) return zone;
            var union = Poses.All.Select(p => p.Template()).ToArray();
            return zone = Cutout.Spread(Enumerable.Range(0, W * H).Select(i => union.Any(t => t.Pixels[i].W > 0.1f)).ToArray(), 60);
        }

        static bool DropStrays(Raster r, bool[] zone)
        {
            int n = W * H;
            var seen = new bool[n];
            bool dropped = false;
            for (int start = 0; start < n; start++)
            {
                if (seen[start] || r.Pixels[start].W == 0) continue;
                var part = new List<int> { start };
                bool inside = false;
                seen[start] = true;
                for (int k = 0; k < part.Count; k++)
                {
                    int i = part[k], x = i % W;
                    inside |= zone[i];
                    foreach (int j in new[] { x > 0 ? i - 1 : -1, x < W - 1 ? i + 1 : -1, i - W, i + W })
                        if (j >= 0 && j < n && !seen[j] && r.Pixels[j].W > 0) { seen[j] = true; part.Add(j); }
                }
                if (!inside && part.Count < 5000)
                {
                    foreach (int i in part) r.Pixels[i] = Vector4.Zero;
                    dropped = true;
                }
            }
            return dropped;
        }

        /// <summary>缩成 4×4 的小块比：重存、重新采样的细微差别会被平均掉，睡觉时闭上的眼睛是成片的变化，至少有几十块。</summary>
        static bool Same(Raster a, Raster b)
        {
            int differ = 0;
            for (int by = 0; by < H / 4; by++)
                for (int bx = 0; bx < W / 4; bx++)
                {
                    var d = Vector4.Zero;
                    for (int y = by * 4; y < by * 4 + 4; y++)
                        for (int x = bx * 4; x < bx * 4 + 4; x++) d += a.Pixels[y * W + x] - b.Pixels[y * W + x];
                    d = Vector4.Abs(d) / 16 * 255;
                    if (Math.Max(Math.Max(d.X, d.Y), Math.Max(d.Z, d.W)) > 24) differ++;
                }
            return differ < 12;
        }

        /// <summary>捂眼时鼠标那块和原版的捂眼图重合得够多。</summary>
        static bool MouseShows(Raster b, Raster t)
        {
            int both = 0, either = 0;
            for (int y = 656; y <= 777; y++)
                for (int x = 689; x <= 844; x++)
                {
                    bool p = b[x, y].W > 0.5f, q = t[x, y].W > 0.5f;
                    if (p && q) both++;
                    if (p || q) either++;
                }
            return both * 100 >= either * 85;
        }
    }
}
