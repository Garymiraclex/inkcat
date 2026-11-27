using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Point = System.Windows.Point;
using Vector = System.Windows.Vector;

namespace InkCat
{
    /// <summary>
    /// 把整帧图拆成活动部件。坐标为 1200×896 像素，左上原点；轮廓按模板描，所以换猫时姿势要照模板画。
    /// 与 Mac 版 Sources/Cutter.swift 对应，改一边要改另一边。
    /// </summary>
    static class Cutter
    {
        public const int W = 1200, H = 896;

        public static Point[] Points(string data) => data.Split(' ').Select(p =>
        {
            var f = p.Split(',');
            return new Point(double.Parse(f[0], CultureInfo.InvariantCulture), double.Parse(f[1], CultureInfo.InvariantCulture));
        }).ToArray();

        // ———— 图像运算 ————

        /// <summary>闭合 Catmull-Rom 平滑轮廓的覆盖率；grow 外扩，blur 羽化。</summary>
        public static float[] Mask(string outline, float grow = 0, int blur = 0, bool smooth = true)
        {
            var p = Points(outline).Select(q => new PointF((float)q.X, (float)q.Y)).ToArray();
            int n = p.Length;
            var m = new float[W * H];
            using (var path = new GraphicsPath())
            using (var bitmap = new Bitmap(W, H, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bitmap))
            {
                for (int i = 0; i < n; i++)
                {
                    PointF a = p[(i + n - 1) % n], b = p[i], c = p[(i + 1) % n], d = p[(i + 2) % n];
                    if (smooth) path.AddBezier(b, new PointF(b.X + (c.X - a.X) / 6, b.Y + (c.Y - a.Y) / 6), new PointF(c.X - (d.X - b.X) / 6, c.Y - (d.Y - b.Y) / 6), c);
                    else path.AddLine(b, c);
                }
                path.CloseFigure();
                path.FillMode = FillMode.Winding;
                g.Clear(Color.Black);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.FillPath(Brushes.White, path);
                if (grow > 0) using (var pen = new Pen(Color.White, grow * 2) { LineJoin = LineJoin.Round }) g.DrawPath(pen, path);
                var data = bitmap.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                var bytes = new byte[data.Stride * H];
                Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                bitmap.UnlockBits(data);
                for (int i = 0; i < m.Length; i++) m[i] = bytes[i * 4] / 255f;
            }
            for (int pass = 0; pass < (blur > 0 ? 4 : 0); pass++)  // 两次盒式模糊
            {
                bool horizontal = pass % 2 == 0;
                var s = m;
                m = new float[W * H];
                for (int y = 0; y < H; y++)
                    for (int x = 0; x < W; x++)
                    {
                        float t = 0;
                        for (int k = -blur; k <= blur; k++)
                            t += horizontal ? s[y * W + Clamp(x + k, 0, W - 1)] : s[Clamp(y + k, 0, H - 1) * W + x];
                        m[y * W + x] = t / (2 * blur + 1);
                    }
            }
            return m;
        }

        static int Clamp(int v, int low, int high) => Math.Min(Math.Max(v, low), high);
        static float Unit(double v) => (float)Math.Max(0, Math.Min(1, v));
        static double Hypot(double x, double y) => Math.Sqrt(x * x + y * y);
        static float[] Union(float[] a, float[] b) => a.Zip(b, Math.Max).ToArray();

        static Raster Mix(Raster a, Raster b, float[] m)
        {
            var o = new Raster();
            for (int i = 0; i < m.Length; i++) o.Pixels[i] = a.Pixels[i] * (1 - m[i]) + b.Pixels[i] * m[i];
            return o;
        }

        public static Raster Cut(Raster a, float[] m)
        {
            var o = new Raster();
            for (int i = 0; i < m.Length; i++) o.Pixels[i] = a.Pixels[i] * m[i];
            return o;
        }

        public static float Luma(Vector4 v) => (v.X + v.Y + v.Z) / 3;

        /// <summary>两点颜色差得明显。</summary>
        public static bool Differs(Vector4 a, Vector4 b)
        {
            var d = Vector4.Abs(a - b);
            return Math.Max(Math.Max(d.X, d.Y), Math.Max(d.Z, d.W)) > 0.16f;
        }

        /// <summary>只留下紧贴 seed（&gt;0.5）且不少于 minSize 像素的连通块，滤掉零星杂点。</summary>
        public static bool[] Attached(bool[] cand, float[] seed, int minSize = 40)
        {
            var keep = new bool[W * H];
            var seen = new bool[W * H];
            for (int s = 0; s < W * H; s++)
            {
                if (!cand[s] || seen[s]) continue;
                var block = new List<int> { s };
                bool touches = false;
                seen[s] = true;
                for (int i = 0; i < block.Count; i++)
                    foreach (int n in new[] { block[i] - 1, block[i] + 1, block[i] - W, block[i] + W })
                    {
                        if (n < 0 || n >= W * H) continue;
                        if (seed[n] > 0.5f) touches = true;
                        if (cand[n] && !seen[n]) { seen[n] = true; block.Add(n); }
                    }
                if (touches && block.Count >= minSize) foreach (int i in block) keep[i] = true;
            }
            return keep;
        }

        // ———— 模板轮廓 ————

        const string armL = "443,462 428,480 402,488 376,500 352,519 337,545 330,575 329,598 335,616 349,628 368,634 386,630 400,624 411,613 417,600 420,588 428,577 446,571 462,562 465,545 466,530 466,500 462,478";
        const string idleL = "440,462 428,486 408,505 380,521 348,535 322,548 306,564 303,582 310,601 326,612 346,617 372,615 394,608 418,600 474,598 472,570 468,530 466,495 460,474";
        const string armM = "740,528 743,548 748,562 748,578 741,594 731,611 722,629 717,650 716,673 720,689 730,701 742,703 757,710 773,708 786,704 800,704 809,692 816,675 824,661 842,643 859,624 867,606 871,590 873,570 869,548 860,526 840,510 800,502 762,508";
        const string armR = "742,508 725,533 700,544 672,552 649,559 620,575 590,600 571,624 566,646 573,664 587,677 600,684 613,686 633,685 651,677 662,661 680,648 696,643 730,644 762,643 801,638 826,632 846,619 861,602 868,578 871,555 864,530 842,510 800,498";

        /// <summary>右臂搭着的画法里，盖在手臂根部上面的胸毛和鬃毛。</summary>
        static readonly float[] ruff = Mask("405,430 408,470 411,496 414,503 437,503 446,497 450,518 461,538 478,556 494,574 520,585 520,430", blur: 1, smooth: false);

        /// <summary>捂眼时抬起的双臂，配件要垫在它们下面。</summary>
        public static readonly float[] RaisedArms = Union(
            Mask("584,287 571,297 558,304 553,318 556,333 556,346 562,358 573,369 590,378 608,386 621,397 632,414 642,431 652,447 662,465 672,483 682,499 693,515 705,530 720,544 740,556 762,562 805,558 862,562 894,520 862,450 803,405 785,407 764,404 752,398 742,387 733,377 723,366 710,353 694,337 678,320 660,307 645,299 628,291 612,286 599,284", grow: 1),
            Mask("498,294 506,305 508,318 506,332 505,345 501,356 490,364 476,371 467,380 461,390 457,403 457,420 458,440 461,460 462,480 465,500 466,530 464,557 440,556 415,549 395,534 377,513 364,488 359,460 361,430 366,405 375,380 385,350 394,334 405,317 420,302 440,289 460,280 478,279 490,285", grow: 1));

        /// <summary>桌沿：以下归桌面，以上归猫。</summary>
        public static bool OnDesk(int x, int y) => y > (x < 455 ? 579 + 0.16 * (x - 330) : 634 + 0.145 * (x - 660));

        /// <summary>原画里桌沿描边（深色细线）的上沿，返回直线 y = k·x + b：逐列在模板桌沿附近找最深的描边，往上走到毛色为止，拟合后再抬高一点，
        /// 身体在这条线以下全是桌面，不能留。找不到就退回模板桌沿上 12 像素。</summary>
        public static (double k, double b) DeskLine(Raster f)
        {
            var pts = new List<(double x, double y)>();
            for (int x = 500; x < 900; x += 3)
            {
                int y0 = (int)(634 + 0.145 * (x - 660));
                int m = -1;
                float best = float.MaxValue;
                for (int y = y0 - 16; y <= y0 + 2; y++)
                {
                    if (f[x, y].W <= 0.9f) continue;
                    float l = Luma(f[x, y]);
                    if (l < best) { best = l; m = y; }
                }
                if (m < 0 || best >= 0.36f) continue;
                float mid = (best + Luma(f[x, m - 12])) / 2;
                int top = m;
                while (top > m - 8 && f[x, top - 1].W > 0.5f && Luma(f[x, top - 1]) < mid) top--;
                pts.Add((x, top));
            }
            var kept = pts;
            double k = 0.145, b = 634 - 0.145 * 660 - 12;
            for (int iter = 0; iter < 3 && kept.Count >= 8; iter++)  // 最小二乘，剔掉偏离的点（爪子、毛发）后重拟
            {
                double n = kept.Count, sx = kept.Sum(p => p.x), sy = kept.Sum(p => p.y);
                double sxx = kept.Sum(p => p.x * p.x), sxy = kept.Sum(p => p.x * p.y);
                k = (n * sxy - sx * sy) / (n * sxx - sx * sx);
                b = (sy - k * sx) / n;
                kept = pts.Where(p => Math.Abs(p.y - (k * p.x + b)) < 2.5).ToList();
            }
            if (kept.Count >= 8) b += Math.Min(0, kept.Min(p => p.y - (k * p.x + b))) - 1.5;
            return (k, b);
        }

        /// <summary>去掉左侧悬空的桌沿线。</summary>
        public static Raster Trim(Raster a)
        {
            var o = a.Copy();
            for (int y = 558; y < 610; y++)
                for (int x = 260; x < 462; x++)
                    if (y < 0.18 * (x - 340) + 583 && y > 0.18 * (x - 300) + 558) o[x, y] = Vector4.Zero;
            return o;
        }

        static readonly (double y, double x)[] tailEdge = { (280, 780), (385, 820), (440, 854), (500, 884), (550, 902), (600, 912), (660, 915), (690, 916) };

        /// <summary>模板里身体右缘再往外留一点，逐行（y 280…689）。</summary>
        static readonly double[] backEdge = Enumerable.Range(280, 410).Select(y =>
        {
            int i = Array.FindLastIndex(tailEdge, e => e.y <= y);
            var (a, b) = (tailEdge[i], tailEdge[Math.Min(i + 1, tailEdge.Length - 1)]);
            double t = b.y > a.y ? (y - a.y) / (b.y - a.y) : 0;
            return a.x + (b.x - a.x) * t + (y < 604 ? 16 : 6);
        }).ToArray();

        /// <summary>尾巴所在处：身体右缘之外、桌沿线之上。back 是这只猫逐行的身体右缘。</summary>
        static bool InTail(int x, int y, double[] back) => y >= 280 && y < 690 && y < 659 + 0.176 * (x - 910) && x > back[y - 280];

        public static Raster Tailless(Raster a, double[] back)
        {
            var o = a.Copy();
            for (int y = 280; y < 690; y++) for (int x = 780; x < 1100; x++) if (InTail(x, y, back)) o[x, y] = Vector4.Zero;
            return o;
        }

        /// <summary>每一行从身体里往右找，第一段透明就是后背的边；身体和尾巴连在一起的行（尾巴根部）沿用上一行。</summary>
        static double[] MeasureBack(Raster f)
        {
            var edges = (double[])backEdge.Clone();
            double? last = null;
            for (int i = 0; i < edges.Length; i++)
            {
                int y = i + 280;
                double template = backEdge[i];
                int x = (int)template - 40;
                while (x < W && f[x, y].W > 0.3f) x++;
                double limit = Math.Min(template + 60, (last ?? template + 44) + 16);  // 跑太远、或比上一行突然外扩很多，说明连着尾巴
                double? found = x > (int)template - 40 && x + 2 < limit ? x + 2 : (double?)null;
                if (found != null) last = found;
                edges[i] = Math.Max(template, found ?? last ?? template);
            }
            return edges;
        }

        /// <summary>手臂描边外残留的浅色细边，衬在深色桌面上会显出来：从 top 行往下向内削，碰到描边为止。</summary>
        public static Raster Trimmed(Raster a, int top)
        {
            var o = a;
            for (int pass = 0; pass < 4; pass++)
            {
                var s = o;
                o = s.Copy();
                for (int y = top; y < o.Height; y++)
                    for (int x = 0; x < o.Width; x++)
                    {
                        var v = s[x, y];
                        if (v.W <= 0) continue;
                        bool open = s[x - 1, y].W < 0.05f || s[x + 1, y].W < 0.05f || s[x, y - 1].W < 0.05f || s[x, y + 1].W < 0.05f;
                        if (open && (v.W < 0.3f || Luma(v) / v.W > 0.42f)) o[x, y] = Vector4.Zero;
                    }
            }
            return o;
        }

        // ———— 拆猫 ————

        /// <summary>四张整帧图。</summary>
        public sealed class Cat
        {
            public readonly Raster Idle, Typing, Cover, Sleep;
            /// <summary>可选的 no_arms.png：拿掉两只前爪、胸口画完整的身体。没有时用捂眼那张补。</summary>
            public readonly Raster Bare;
            /// <summary>type_both 里右臂画在敲键的位置，还是和 idle 一样搭着。</summary>
            public readonly bool RightArmRests;
            /// <summary>这只猫逐行的身体右缘：比模板胖的，以实际轮廓为准。</summary>
            public readonly double[] Back;

            Raster Reference => Bare ?? Cover;

            public Cat(Raster idle, Raster typing, Raster cover, Raster sleep, Raster bare = null)
            {
                (Idle, Typing, Cover, Sleep, Bare) = (idle, typing, cover, sleep, bare);
                Back = MeasureBack(idle);
                var m = Union(Mask(armL), Mask(idleL));
                int moved = 0, total = 0;
                for (int y = 470; y < 630; y++)
                    for (int x = 300; x < 430; x++)
                    {
                        if (m[y * W + x] <= 0.5f) continue;
                        total++;
                        if (Differs(typing[x, y], idle[x, y])) moved++;
                    }
                RightArmRests = moved * 5 < total;
            }

            /// <summary>
            /// 右臂（画面左边那只）在这一帧里的实际范围。没有 no_arms 时只认伸出身体轮廓的部分（捂眼图里那里是空的）；
            /// 有 no_arms 时，和它颜色不同的也算。最后补上中间的洞，只留连着爪子的那一块。与 Mac 版对应。
            /// </summary>
            public float[] RightArmRegion(Raster f)
            {
                var zone = Union(Mask(idleL, 30), Mask(armL, 30));
                var reference = Reference;
                const int x0 = 270, x1 = 520, y0 = 440, y1 = 650;
                var hit = new bool[W * H];
                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++)
                    {
                        int i = y * W + x;
                        if (zone[i] <= 0 || f.Pixels[i].W <= 0.3f || OnDesk(x, y)) continue;
                        hit[i] = reference.Pixels[i].W < 0.3f || Bare != null && Differs(f.Pixels[i], reference.Pixels[i]);
                    }
                for (int pass = 0; pass < 2; pass++)  // 连起来
                {
                    var s = (bool[])hit.Clone();
                    for (int y = y0 + 1; y < y1 - 1; y++)
                        for (int x = x0 + 1; x < x1 - 1; x++)
                        {
                            int i = y * W + x;
                            if (!s[i] && (s[i - 1] || s[i + 1] || s[i - W] || s[i + W])) hit[i] = true;
                        }
                }
                bool Inside(int j) { int x = j % W, y = j / W; return x >= x0 && x < x1 && y >= y0 && y < y1; }
                var outside = new bool[W * H];  // 从框边往里找空白，剩下的是手臂和它包住的洞
                var stack = new Stack<int>();
                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++)
                        if ((x == x0 || x == x1 - 1 || y == y0 || y == y1 - 1) && !hit[y * W + x]) { outside[y * W + x] = true; stack.Push(y * W + x); }
                while (stack.Count > 0)
                {
                    int i = stack.Pop();
                    foreach (int j in new[] { i - 1, i + 1, i - W, i + W })
                        if (Inside(j) && !outside[j] && !hit[j]) { outside[j] = true; stack.Push(j); }
                }
                var label = Enumerable.Repeat(-1, W * H).ToArray();  // 只留最大的一块，去掉零碎的点
                var sizes = new List<int>();
                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++)
                    {
                        int start = y * W + x;
                        if (outside[start] || label[start] >= 0) continue;
                        int n = 0;
                        label[start] = sizes.Count;
                        stack.Push(start);
                        while (stack.Count > 0)
                        {
                            int i = stack.Pop();
                            n++;
                            foreach (int j in new[] { i - 1, i + 1, i - W, i + W })
                                if (Inside(j) && !outside[j] && label[j] < 0) { label[j] = sizes.Count; stack.Push(j); }
                        }
                        sizes.Add(n);
                    }
                var m = new float[W * H];
                if (sizes.Count == 0 || sizes.Max() <= 400) return m;
                int biggest = sizes.IndexOf(sizes.Max());
                for (int i = 0; i < m.Length; i++) if (label[i] == biggest) m[i] = 1;
                return m;
            }

            /// <summary>双臂所在处换成桌面或透明，肩根保留。</summary>
            public Raster Armless(Raster f)
            {
                var arm = RightArmRegion(f);
                var mask0 = Union(Union(Mask(armL, 4), Mask(idleL, 4)), arm);
                var o = Trim(Mix(Mix(f, Reference, Mask(armM, 3, 1)), Reference, mask0));
                for (int y = 440; y < 640; y++)
                    for (int x = 300; x < 500; x++)
                    {
                        if (mask0[y * W + x] <= 0) continue;
                        if (x < 461 && y < 572 && Bare == null) o[x, y] = Vector4.Zero;  // cover 里抬起的左臂
                        float r = ruff[y * W + x] * (1 - arm[y * W + x]);
                        if (RightArmRests) o[x, y] = o[x, y] * (1 - r) + f[x, y] * r;
                        else if (x >= 458 && y < 548 || Hypot(x - 458, y - 504) < 37) o[x, y] = f[x, y];  // 肩根保持不动
                    }
                if (RightArmRests)  // 手臂内收挪走后，肩下的躯干本来被手臂挡着：用原画的毛垫上，免得露出缺口
                {
                    var torso = Mask("411,506 414,497 437,497 446,497 450,518 441,552 432,546 420,530", blur: 1);
                    var ink = Enumerable.Range(470, 26).Select(y => f[Enumerable.Range(300, 160).First(x => f[x, y].W > 0.5f) + 2, y]).OrderBy(Luma).First();  // 肩边的描边色
                    var edge = new[] { (411.0, 506.0), (420.0, 530.0), (432.0, 546.0) };  // 躯干外沿补一道描边
                    double Near(int x, int y) => edge.Zip(edge.Skip(1), (a, b) =>
                    {
                        double dx = b.Item1 - a.Item1, dy = b.Item2 - a.Item2, t = Math.Max(0, Math.Min(1, ((x - a.Item1) * dx + (y - a.Item2) * dy) / (dx * dx + dy * dy)));
                        return Hypot(x - a.Item1 - dx * t, y - a.Item2 - dy * t);
                    }).Min();
                    for (int y = 490; y < 560; y++)
                        for (int x = 395; x < 455; x++)
                        {
                            float r = torso[y * W + x];
                            o[x, y] = o[x, y] * (1 - r) + (Near(x, y) < 2.5 ? ink : f[x, y]) * r;
                        }
                }
                return o;
            }

            /// <summary>闭眼的脸，边缘羽化。</summary>
            public Raster Asleep => Cut(Sleep, Mask("420,240 560,220 700,240 730,330 690,430 560,446 440,420 412,330", blur: 4));

            /// <summary>眯眼：把原画的眼睛沿竖向压扁，上眼皮压得多、内眼角略低，四周平滑过渡。</summary>
            public Raster Squint(double amount, double tilt)
            {
                var o = new Raster();
                foreach (var (cx, cy, rx, ry, inner) in new[] { (480.0, 320.0, 66.0, 62.0, 1.0), (606, 325, 74, 64, -1) })
                    for (int y = (int)(cy - ry); y <= (int)(cy + ry); y++)
                        for (int x = (int)(cx - rx); x <= (int)(cx + rx); x++)
                        {
                            double nx = (x - cx) / rx, ny = (y - cy) / ry, d = nx * nx + ny * ny;
                            if (d >= 1) continue;
                            double w = (1 - d) * (1 - d), low = cy + 10, dy = y - low;  // 下眼皮附近不动
                            double source = low + dy * (1 + amount * w * (dy < 0 ? 1 : 0.45)) - tilt * inner * nx * rx * w;
                            o[x, y] = Idle.Sample(new Point(x, source)) * (float)Math.Min(1, (1 - d) * 8);
                        }
                return o;
            }

            /// <summary>尾巴：取更完整的那一帧；根部镜像延伸进身体，摆动时不露缝。</summary>
            public Raster Tail
            {
                get
                {
                    int Area(Raster r)
                    {
                        int n = 0;
                        for (int y = 280; y < 690; y++) for (int x = 780; x < 1100; x++) if (InTail(x, y, Back) && r[x, y].W > 0.5f) n++;
                        return n;
                    }
                    var f = Area(Typing) > Area(Idle) * 102 / 100 ? Typing : Idle;
                    var o = new Raster();
                    for (int y = 280; y < 690; y++) for (int x = 780; x < 1100; x++) if (InTail(x, y, Back)) o[x, y] = f[x, y];
                    for (int y = 606; y < 666; y++) for (int x = 878; x < 924; x++) o[x, y] = o[924 + (924 - x), y];
                    return o;
                }
            }

            /// <summary>肩头：叠在右臂根部之上。一般只在内收时出现，朝手臂一侧羽化；右臂搭着的画法里是一直盖着的鬃毛。</summary>
            public Raster Shoulder
            {
                get
                {
                    if (RightArmRests) return Cut(Bare ?? Idle, ruff.Zip(RightArmRegion(Idle), (r, a) => r * (1 - a)).ToArray());  // 只留毛，不带手臂
                    var o = Cut(Idle, Mask(idleL, 2));
                    for (int y = 0; y < H; y++)
                        for (int x = 0; x < W; x++)
                            if (o[x, y].W > 0) o[x, y] *= Unit((50 - Hypot(x - 462, y - 496)) / 14);
                    return o;
                }
            }

            /// <summary>右臂较短、活动角度大：沿手臂中线展平成直条，运行时再按曲线弯回去。内收版修掉腋下，内侧与胸毛羽化衔接。</summary>
            public (Raster full, Raster tucked) RightArm()
            {
                var c = Points(RightArmRests ? "480,545 425,535 347,535 345,614" : "480,528 420,526 374,562 369,637");
                (Point, Vector) Bezier(double t)
                {
                    double u = 1 - t;
                    var p = (Point)((Vector)c[0] * (u * u * u) + (Vector)c[1] * (3 * u * u * t) + (Vector)c[2] * (3 * u * t * t) + (Vector)c[3] * (t * t * t));
                    var v = (c[1] - c[0]) * (3 * u * u) + (c[2] - c[1]) * (6 * u * t) + (c[3] - c[2]) * (3 * t * t);
                    v.Normalize();
                    return (p, v);
                }
                var table = new List<(double length, double t)> { (0, 0) };
                for (int i = 1; i <= 1000; i++)
                {
                    double t = i / 1000.0;
                    table.Add((table[i - 1].length + (Bezier(t).Item1 - Bezier(t - 0.001).Item1).Length, t));
                }
                string outline = RightArmRests
                    ? "414,499 405,504 390,512 370,522 352,533 335,540 324,547 314,558 308,570 307,582 310,592 318,601 328,609 340,613 352,612 358,610 368,612 380,610 392,604 402,599 412,594 430,593 450,594 470,590 486,588 486,566 470,552 456,540 446,526 436,514 424,506"
                    : "443,462 428,480 402,488 376,500 352,519 337,545 330,575 329,598 335,616 349,628 368,634 386,630 400,624 411,613 416,600 418,588 440,585 470,583 496,580 498,540 490,500 462,476";
                // 爪子比模板胖时，压在键盘上的那段外沿描边会落在轮廓外：轮廓外 12 像素内、和捂眼图（键盘完整露出）不同的也算手臂
                float[] inside = Mask(outline), rim = Mask(outline, grow: 12);
                var cand = new bool[inside.Length];
                for (int i = 0; i < cand.Length; i++)
                    cand[i] = rim[i] > inside[i] && OnDesk(i % W, i / W) && Differs(Typing.Pixels[i], Cover.Pixels[i]);
                var extra = Attached(cand, inside);
                for (int i = 0; i < inside.Length; i++) if (extra[i]) inside[i] = rim[i];
                var src = Cut(Typing, Union(RightArmRegion(Typing), inside));
                var o = new Raster(140 + 2 * margin, (int)table[1000].length);  // 自己的猫手臂可能更粗，直条两边各放宽
                for (int y = 0; y < o.Height; y++)
                {
                    var (p, v) = Bezier(table.First(e => e.length >= y).t);
                    for (int x = 0; x < o.Width; x++) o[x, y] = src.Sample(p + new Vector(v.Y, -v.X) * (x - o.Width / 2.0)) * Unit(y / 20.0);
                }
                if (RightArmRests) ExtendRoot(o);
                var full = o.Copy();
                for (int y = 0; y < o.Height - 74; y++)
                {
                    float limit = 104 + margin + Math.Max(0, y - (o.Height - 100)) * 0.15f;
                    for (int x = 0; x < o.Width; x++) o[x, y] *= Unit((limit - x) / 14);
                }
                return (full, o);
            }

            const int margin = 20;

            /// <summary>根部的上沿原本藏在毛里，顺着露出的部分补到肩。</summary>
            static void ExtendRoot(Raster o)
            {
                int Edge(int y)
                {
                    for (int x = 0; x < o.Width; x++) if (o[x, y].W > 0.5f * Unit(y / 20.0)) return x;
                    return o.Width;
                }
                int from = Enumerable.Range(45, 35).OrderBy(Edge).First(), x0 = Edge(from);
                var rim = new List<Vector4>();  // 描边色取露出部分的外沿
                for (int y = from; y < from + 40; y++) for (int x = Edge(y); x < Math.Min(Edge(y) + 6, o.Width); x++) if (o[x, y].W > 0.95f) rim.Add(o[x, y]);
                var tones = new List<Vector4>();
                for (int y = 25; y < 90; y++) for (int x = 50 + margin; x < 90 + margin; x++) if (o[x, y].W > 0.95f) tones.Add(o[x, y]);
                var ink = rim.Count == 0 ? new Vector4(0x3B, 0x24, 0x18, 255) / 255 : rim.OrderBy(Luma).ElementAt(rim.Count / 6);
                var fur = tones.Count == 0 ? ink : tones.OrderBy(Luma).ElementAt(tones.Count / 2);
                var edges = Enumerable.Range(0, from).Select(Edge).ToArray();
                for (int y = 0; y < from; y++)
                {
                    double t = (double)(from - y) / from, start = x0 + (46 + margin - x0) * t * t, old = Math.Min(edges[y], 100 + margin);
                    for (int x = (int)start; x < (int)Math.Max(old, start) + 14; x++)
                    {
                        float keep = Unit((x - old - 4) / 10);  // 与原有部分渐变衔接
                        o[x, y] = fur * Unit(y / 20.0) * (1 - keep) + o[x, y] * keep;  // 只补毛色不描边，手臂挪开后肩上不留线
                    }
                }
            }

            public Raster LeftArm => Arm(Typing, armR, new Point(815, 572), new Point(610, 682), 50);
            public Raster MouseArm => Arm(Idle, armM, new Point(804, 560), new Point(765, 706), 52);
        }

        /// <summary>手臂：肩端羽化便于旋转衔接，再以肩→爪为轴转成竖直，肩在 (120, 80)。</summary>
        static Raster Arm(Raster f, string outline, Point pivot, Point tip, double fade)
        {
            var src = Cut(f, Mask(outline));
            var v = tip - pivot;
            v.Normalize();
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    if (src[x, y].W > 0) src[x, y] *= Unit(((new Point(x, y) - pivot) * v + fade) / (fade * 0.7));  // 越过肩点后渐隐
            var u = new Vector(v.Y, -v.X);
            var o = new Raster(240, 400);
            for (int y = 0; y < 400; y++) for (int x = 0; x < 240; x++) o[x, y] = src.Sample(pivot + u * (x - 120) + v * (y - 80));
            return o;
        }
    }
}
