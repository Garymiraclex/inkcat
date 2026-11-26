using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace InkCat
{
    /// <summary>把图整理成透明底：纯色（或棋盘格）背景去掉。颜色按 0–255 计。</summary>
    static class Cutout
    {
        const int W = Cutter.W, H = Cutter.H, N = W * H;

        public static Raster Clean(Raster image, string name)
        {
            Vector3[] paper;
            try { paper = Paper(image); }
            catch (Problem e) { throw new Problem($"{name}.png 的{e.Message}"); }
            return paper == null ? image : Knock(image, paper);
        }

        static Vector3 Rgb(Raster r, int i) { var v = r.Pixels[i] * 255; return new Vector3(v.X, v.Y, v.Z); }

        /// <summary>背景色：已经透明时为 null；四边是一种颜色（纯色底）或两种（画出来的棋盘格），允许 2% 的杂点（如水印）。</summary>
        public static Vector3[] Paper(Raster image)
        {
            var border = Enumerable.Range(0, W).SelectMany(x => new[] { x, (H - 1) * W + x })
                .Concat(Enumerable.Range(1, H - 2).SelectMany(y => new[] { y * W, y * W + W - 1 }));
            var colors = border.Where(i => image.Pixels[i].W > 200 / 255f).Select(i => Rgb(image, i)).ToList();
            if (colors.Count * 2 < 2 * (W + H - 2)) return null;
            int Key(Vector3 c) => (int)c.X / 8 << 10 | (int)c.Y / 8 << 5 | (int)c.Z / 8;
            var rest = colors;
            var paper = new List<Vector3>();
            while (paper.Count < 2 && rest.Count * 50 > colors.Count)
            {
                int top = rest.GroupBy(Key).OrderByDescending(g => g.Count()).First().Key;
                var members = rest.Where(c => Key(c) == top).ToList();
                paper.Add(members.Aggregate(Vector3.Zero, (a, b) => a + b) / members.Count);
                var known = paper.ToArray();
                rest = rest.Where(c => Far(c, known) > 30).ToList();
            }
            if (rest.Count * 50 > colors.Count) throw new Problem("背景不是透明的，也不是纯色");
            return paper.ToArray();
        }

        /// <summary>离背景色有多远；棋盘格的两色之间的过渡色也算背景。</summary>
        public static float Far(Vector3 c, Vector3[] paper) => Vector3.Distance(c, Nearest(c, paper));

        static Vector3 Nearest(Vector3 c, Vector3[] paper)
        {
            if (paper.Length < 2) return paper[0];
            Vector3 a = paper[0], ab = paper[1] - paper[0];
            return a + ab * Math.Min(1, Math.Max(0, Vector3.Dot(c - a, ab) / Math.Max(ab.LengthSquared(), 1)));
        }

        static int Around(int i, int[] into)
        {
            int n = 0, x = i % W;
            if (x > 0) into[n++] = i - 1;
            if (x < W - 1) into[n++] = i + 1;
            if (i >= W) into[n++] = i - W;
            if (i < N - W) into[n++] = i + W;
            return n;
        }

        /// <summary>
        /// 从四边往里清掉背景，紧贴背景的两圈变半透明并去掉混进去的底色，深色桌面上不留灰边。
        /// 给了 <paramref name="template"/>（对齐后同一姿势的原版不透明度）时更稳：清背景不进入模板身体的内部；
        /// 被围住、大部分在模板之外的同色块（如耳机头梁内侧）也清掉。
        /// </summary>
        public static Raster Knock(Raster image, Vector3[] paper, float[] template = null)
        {
            var d = new float[N];
            for (int i = 0; i < N; i++) d[i] = Far(Rgb(image, i), paper);
            bool[] core = template == null ? null : Spread(template.Select(a => a < 0.5f).ToArray(), 20).Select(o => !o).ToArray();
            bool Open(int i) => d[i] < (core == null || !core[i] ? 16 : 7);  // 模板身体里只认几乎一样的颜色，背景是平的，毛不是
            var next = new int[4];

            var bg = new bool[N];
            var queue = new Stack<int>();
            for (int i = 0; i < N; i++)
                if ((i < W || i >= N - W || i % W == 0 || i % W == W - 1) && Open(i)) { bg[i] = true; queue.Push(i); }
            while (queue.Count > 0)
            {
                int i = queue.Pop();
                for (int k = Around(i, next) - 1; k >= 0; k--)
                    if (!bg[next[k]] && Open(next[k])) { bg[next[k]] = true; queue.Push(next[k]); }
            }
            if (template != null)
            {
                var seen = (bool[])bg.Clone();
                for (int start = 0; start < N; start++)
                {
                    if (seen[start] || d[start] >= 16) continue;
                    var part = new List<int> { start };
                    seen[start] = true;
                    for (int p = 0; p < part.Count; p++)
                        for (int k = Around(part[p], next) - 1; k >= 0; k--)
                            if (!seen[next[k]] && d[next[k]] < 16) { seen[next[k]] = true; part.Add(next[k]); }
                    if (part.Count >= 150 && part.Count(i => template[i] < 0.1f) * 5 >= part.Count * 4) foreach (int i in part) bg[i] = true;
                }
            }

            var ring = new byte[N];
            for (int i = 0; i < N; i++)
                if (!bg[i]) for (int k = Around(i, next) - 1; k >= 0; k--) if (bg[next[k]]) ring[i] = 1;
            for (int i = 0; i < N; i++)
                if (!bg[i] && ring[i] == 0) for (int k = Around(i, next) - 1; k >= 0; k--) if (ring[next[k]] == 1) ring[i] = 2;
            var o = new Raster();
            for (int i = 0; i < N; i++)
            {
                if (bg[i]) continue;
                o.Pixels[i] = image.Pixels[i];
                if (ring[i] == 0) continue;
                float a = ring[i] == 1 ? Math.Min(1, Math.Max(0, (d[i] - 24) / 38)) : Math.Min(1, Math.Max(0, (d[i] - 18) / 26));
                if (a >= 1) continue;
                var c = Rgb(image, i);
                var v = Vector3.Min(new Vector3(255 * a), Vector3.Max(Vector3.Zero, c - Nearest(c, paper) * (1 - a))) / 255;
                o.Pixels[i] = new Vector4(v, a);
            }
            return o;
        }

        /// <summary>每个为真的点向四周扩 <paramref name="r"/> 像素（方形）。</summary>
        public static bool[] Spread(bool[] m, int r)
        {
            var sum = new int[(W + 1) * (H + 1)];  // 积分图
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    sum[(y + 1) * (W + 1) + x + 1] = (m[y * W + x] ? 1 : 0) + sum[y * (W + 1) + x + 1] + sum[(y + 1) * (W + 1) + x] - sum[y * (W + 1) + x];
            var o = new bool[N];
            for (int i = 0; i < N; i++)
            {
                int x = i % W, y = i / W, x0 = Math.Max(0, x - r), y0 = Math.Max(0, y - r), x1 = Math.Min(W, x + r + 1), y1 = Math.Min(H, y + r + 1);
                o[i] = sum[y1 * (W + 1) + x1] - sum[y0 * (W + 1) + x1] - sum[y1 * (W + 1) + x0] + sum[y0 * (W + 1) + x0] > 0;
            }
            return o;
        }
    }
}
