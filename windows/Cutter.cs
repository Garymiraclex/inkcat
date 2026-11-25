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

        // ———— 模板轮廓 ————

        const string armL = "443,462 428,480 402,488 376,500 352,519 337,545 330,575 329,598 335,616 349,628 368,634 386,630 400,624 411,613 417,600 420,588 428,577 446,571 462,562 465,545 466,530 466,500 462,478";
        const string idleL = "440,462 428,486 408,505 380,521 348,535 322,548 306,564 303,582 310,601 326,612 346,617 372,615 394,608 418,600 474,598 472,570 468,530 466,495 460,474";
        const string armM = "740,528 743,548 748,562 748,578 741,594 731,611 722,629 717,650 716,673 720,689 730,701 742,703 757,710 773,708 786,704 800,704 809,692 816,675 824,661 842,643 859,624 867,606 871,590 873,570 869,548 860,526 840,510 800,502 762,508";
        const string armR = "742,508 725,533 700,544 672,552 649,559 620,575 590,600 571,624 566,646 573,664 587,677 600,684 613,686 633,685 651,677 662,661 680,648 696,643 730,644 762,643 801,638 826,632 846,619 861,602 868,578 871,555 864,530 842,510 800,498";

        /// <summary>右臂搭着的画法里，盖在手臂根部上面的胸毛和鬃毛。</summary>
        static readonly float[] ruff = Mask("405,430 408,470 424,484 430,496 437,503 446,497 450,518 461,538 478,556 494,574 520,585 520,430", blur: 1, smooth: false);

        /// <summary>捂眼时抬起的双臂，配件要垫在它们下面。</summary>
        public static readonly float[] RaisedArms = Union(
            Mask("584,287 571,297 558,304 553,318 556,333 556,346 562,358 573,369 590,378 608,386 621,397 632,414 642,431 652,447 662,465 672,483 682,499 693,515 705,530 720,544 740,556 762,562 805,558 862,562 894,520 862,450 803,405 785,407 764,404 752,398 742,387 733,377 723,366 710,353 694,337 678,320 660,307 645,299 628,291 612,286 599,284", grow: 1),
            Mask("498,294 506,305 508,318 506,332 505,345 501,356 490,364 476,371 467,380 461,390 457,403 457,420 458,440 461,460 462,480 465,500 466,530 464,557 440,556 415,549 395,534 377,513 364,488 359,460 361,430 366,405 375,380 385,350 394,334 405,317 420,302 440,289 460,280 478,279 490,285", grow: 1));

        /// <summary>桌沿：以下归桌面，以上归猫。</summary>
        public static bool OnDesk(int x, int y) => y > (x < 455 ? 579 + 0.16 * (x - 330) : 634 + 0.145 * (x - 660));

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

        /// <summary>尾巴所在处：身体右缘之外、桌沿线之上。</summary>
        static bool InTail(int x, int y)
        {
            if (y < 280 || y >= 690 || y >= 659 + 0.176 * (x - 910)) return false;
            int i = Array.FindLastIndex(tailEdge, e => e.y <= y);
            var (a, b) = (tailEdge[i], tailEdge[Math.Min(i + 1, tailEdge.Length - 1)]);
            double t = b.y > a.y ? (y - a.y) / (b.y - a.y) : 0;
            return x > a.x + (b.x - a.x) * t + (y < 604 ? 16 : 6);
        }

        public static Raster Tailless(Raster a)
        {
            var o = a.Copy();
            for (int y = 280; y < 690; y++) for (int x = 780; x < 1100; x++) if (InTail(x, y)) o[x, y] = Vector4.Zero;
            return o;
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
            /// <summary>type_both 里右臂画在敲键的位置，还是和 idle 一样搭着。</summary>
            public readonly bool RightArmRests;

            public Cat(Raster idle, Raster typing, Raster cover, Raster sleep)
            {
                (Idle, Typing, Cover, Sleep) = (idle, typing, cover, sleep);
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

            /// <summary>双臂所在处换成桌面或透明，肩根保留。</summary>
            public Raster Armless(Raster f)
            {
                var mask0 = Union(Mask(armL, 4), Mask(idleL, 4));
                var o = Trim(Mix(Mix(f, Cover, Mask(armM, 3, 1)), Cover, mask0));
                for (int y = 440; y < 640; y++)
                    for (int x = 300; x < 500; x++)
                    {
                        if (mask0[y * W + x] <= 0) continue;
                        if (x < 461 && y < 572) o[x, y] = Vector4.Zero;  // cover 里抬起的左臂
                        if (RightArmRests) o[x, y] = o[x, y] * (1 - ruff[y * W + x]) + f[x, y] * ruff[y * W + x];
                        else if (x >= 458 && y < 548 || Hypot(x - 458, y - 504) < 37) o[x, y] = f[x, y];  // 肩根保持不动
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
                        for (int y = 280; y < 690; y++) for (int x = 780; x < 1100; x++) if (InTail(x, y) && r[x, y].W > 0.5f) n++;
                        return n;
                    }
                    var f = Area(Typing) > Area(Idle) * 102 / 100 ? Typing : Idle;
                    var o = new Raster();
                    for (int y = 280; y < 690; y++) for (int x = 780; x < 1100; x++) if (InTail(x, y)) o[x, y] = f[x, y];
                    for (int y = 606; y < 666; y++) for (int x = 878; x < 924; x++) o[x, y] = o[924 + (924 - x), y];
                    return o;
                }
            }

            /// <summary>肩头：叠在右臂根部之上。一般只在内收时出现，朝手臂一侧羽化；右臂搭着的画法里是一直盖着的鬃毛。</summary>
            public Raster Shoulder
            {
                get
                {
                    if (RightArmRests) return Cut(Idle, ruff);
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
                var src = Cut(Typing, Mask(RightArmRests
                    ? "414,499 405,504 390,512 370,522 352,533 335,540 324,547 314,558 308,570 307,582 310,592 318,601 328,609 340,613 352,612 358,610 368,612 380,610 392,604 402,599 412,594 430,593 450,594 470,590 486,588 486,566 470,552 456,540 446,526 436,514 424,506"
                    : "443,462 428,480 402,488 376,500 352,519 337,545 330,575 329,598 335,616 349,628 368,634 386,630 400,624 411,613 416,600 418,588 440,585 470,583 496,580 498,540 490,500 462,476"));
                var o = new Raster(140, (int)table[1000].length);
                for (int y = 0; y < o.Height; y++)
                {
                    var (p, v) = Bezier(table.First(e => e.length >= y).t);
                    for (int x = 0; x < o.Width; x++) o[x, y] = src.Sample(p + new Vector(v.Y, -v.X) * (x - 70)) * Unit(y / 20.0);
                }
                if (RightArmRests) ExtendRoot(o);
                var full = o.Copy();
                for (int y = 0; y < o.Height - 74; y++)
                {
                    float limit = 104 + Math.Max(0, y - (o.Height - 100)) * 0.15f;
                    for (int x = 0; x < o.Width; x++) o[x, y] *= Unit((limit - x) / 14);
                }
                return (full, o);
            }

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
                for (int y = 25; y < 90; y++) for (int x = 50; x < 90; x++) if (o[x, y].W > 0.95f) tones.Add(o[x, y]);
                var ink = rim.Count == 0 ? new Vector4(0x3B, 0x24, 0x18, 255) / 255 : rim.OrderBy(Luma).ElementAt(rim.Count / 6);
                var fur = tones.Count == 0 ? ink : tones.OrderBy(Luma).ElementAt(tones.Count / 2);
                var edges = Enumerable.Range(0, from).Select(Edge).ToArray();
                for (int y = 0; y < from; y++)
                {
                    double t = (double)(from - y) / from, start = x0 + (46 - x0) * t * t, old = Math.Min(edges[y], 100);
                    for (int x = (int)start; x < (int)Math.Max(old, start) + 14; x++)
                    {
                        double d = x - start;
                        float keep = Unit((x - old - 4) / 10);  // 与原有部分渐变衔接
                        var v = d < 5 ? ink * (float)Math.Min(1, d + 0.3) : fur;
                        o[x, y] = v * Unit(y / 20.0) * (1 - keep) + o[x, y] * keep;
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
