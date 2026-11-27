using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace InkCat
{
    /// <summary>
    /// 尾巴：每个点按它沿尾巴离尾根的距离 u 绕尾根转一个角，转角是一道从根传到尖的行波，
    /// 所以尾尖比尾根晚，S 形也会变；贴桌的尾根部分 u 很小，几乎不动。
    /// 甩尾时每帧按转角场反向取样重画；不甩时直接显示原图。
    /// </summary>
    sealed class Tail : Canvas
    {
        // 原图尺寸和尾根位置（左上原点）；画布左右多 80、上方多 60，摆动时不被裁掉
        const int W = 240, H = 400, Side = 80, Top = 60, CW = W + 2 * Side, CH = H + Top, Stride = W + 2, Levels = 256;
        const float RootX = 56, RootY = 362;
        // 主摆和尾尖卷动的幅度（度）、频率（Hz）、尾尖相对尾根的相位差
        const double Swing = 11, Curl = 3.5, Hz = 0.6, Lag = 1.8;
        // 一次甩尾的时长、淡入、淡出，收尾时间，以及两帧的最短间隔（每秒 30 帧，留一点余量）
        const double Duration = 3.5, FadeIn = 0.4, FadeOut = 0.6, Release = 0.25, Interval = 0.03;

        readonly Image rest = CatView.Picture(860, 290, 240, 400), swing = CatView.Picture(780, 230, CW, CH);
        readonly WriteableBitmap canvas = new WriteableBitmap(CW, CH, 96, 96, PixelFormats.Pbgra32, null);
        readonly byte[] pixels = new byte[CW * CH * 4];
        readonly Stopwatch clock = Stopwatch.StartNew();
        /// <summary>原图四周加一圈透明边（预乘 BGRA），方便双线性取样。</summary>
        byte[] source;
        /// <summary>每个像素的 u：沿尾巴到尾根的距离占最长距离的比例。</summary>
        float[] along;
        bool active;
        double start, drawn, from;
        double? letGo;

        public Tail()
        {
            swing.Source = canvas;
            swing.Visibility = Visibility.Collapsed;
            Children.Add(rest);
            Children.Add(swing);
        }

        public bool Swishing => active && letGo == null;

        public void Dress(BitmapSource image)
        {
            Stop();
            rest.Source = image;
            source = null;
            if (image == null) return;
            var full = new FormatConvertedBitmap(image, PixelFormats.Pbgra32, null, 0);
            if (full.PixelWidth != W || full.PixelHeight != H) return;
            var flat = new byte[W * H * 4];
            full.CopyPixels(flat, W * 4, 0);
            source = new byte[Stride * (H + 2) * 4];
            for (int y = 0; y < H; y++) Buffer.BlockCopy(flat, y * W * 4, source, ((y + 1) * Stride + 1) * 4, W * 4);
            along = Distances(source);
            if (along == null) source = null;  // 整条尾巴是空的就不甩
        }

        public void Swish()
        {
            if (source == null || Swishing) return;
            Stop();
            start = drawn = clock.Elapsed.TotalSeconds;
            Frame(start);
            rest.Visibility = Visibility.Hidden;
            swing.Visibility = Visibility.Visible;
            active = true;
            CompositionTarget.Rendering += OnRendering;
        }

        /// <summary>转角幅度很快降到 0，再恢复原图。</summary>
        public void Hold()
        {
            if (!Swishing) return;
            double now = clock.Elapsed.TotalSeconds;
            letGo = now;
            from = Envelope(now - start);
        }

        void Stop()
        {
            CompositionTarget.Rendering -= OnRendering;
            active = false;
            letGo = null;
            rest.Visibility = Visibility.Visible;
            swing.Visibility = Visibility.Collapsed;
        }

        void OnRendering(object sender, EventArgs e)
        {
            double now = clock.Elapsed.TotalSeconds;
            if (now - drawn < Interval) return;
            drawn = now;
            Frame(now);
        }

        void Frame(double now)
        {
            double t = now - start, strength = Envelope(t);
            if (letGo is double at)
            {
                double k = (now - at) / Release;
                if (k >= 1) { Stop(); return; }
                strength = from * (1 - Smooth(k));
            }
            else if (t >= Duration) { Stop(); return; }
            Draw(t, strength);
        }

        static double Smooth(double x)
        {
            x = Math.Min(Math.Max(x, 0), 1);
            return x * x * (3 - 2 * x);
        }

        /// <summary>一次甩尾的强度：开头淡入、结尾淡出。</summary>
        static double Envelope(double t) => Smooth(Math.Min(t / FadeIn, (Duration - t) / FadeOut));

        /// <summary>从尾根出发在不透明像素里走（8 邻域，斜走 14、直走 10），得到每个像素的 u；
        /// 尾巴外和够不着的像素取最近的尾巴像素的值，反向取样时落在边缘外也有 u 可用。</summary>
        static float[] Distances(byte[] source)
        {
            bool Opaque(int i) => source[((i / W + 1) * Stride + i % W + 1) * 4 + 3] > 8;
            var best = Enumerable.Repeat(int.MaxValue, W * H).ToArray();
            void Visit(int i, Action<int, int> body)
            {
                int x = i % W, y = i / W;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        if ((dx != 0 || dy != 0) && x + dx >= 0 && x + dx < W && y + dy >= 0 && y + dy < H)
                            body((y + dy) * W + x + dx, dx * dy == 0 ? 10 : 14);
            }
            // 从尾根出发；尾根那一点透明时，从离它最近的不透明像素出发
            int at0 = (int)RootY * W + (int)RootX, seed = -1;
            long Gap(int i) { long dx = i % W - at0 % W, dy = i / W - at0 / W; return dx * dx + dy * dy; }
            if (Opaque(at0)) seed = at0;
            else foreach (int i in Enumerable.Range(0, W * H).Where(Opaque)) if (seed < 0 || Gap(i) < Gap(seed)) seed = i;
            if (seed < 0) return null;
            best[seed] = 0;
            var buckets = new List<List<int>> { new List<int> { seed } };  // 按距离分桶，代替优先队列
            for (int d = 0; d < buckets.Count; d++)
                foreach (int i in buckets[d])
                {
                    if (best[i] != d) continue;
                    int at = d;
                    Visit(i, (n, cost) =>
                    {
                        if (!Opaque(n) || at + cost >= best[n]) return;
                        best[n] = at + cost;
                        while (buckets.Count <= at + cost) buckets.Add(new List<int>());
                        buckets[at + cost].Add(n);
                    });
                }
            var reached = Enumerable.Range(0, best.Length).Where(i => best[i] != int.MaxValue).ToList();
            float far = Math.Max(reached.Max(i => best[i]), 1);
            for (int head = 0; head < reached.Count; head++)
            {
                int i = reached[head];
                Visit(i, (n, cost) =>
                {
                    if (best[n] != int.MaxValue) return;
                    best[n] = best[i];
                    reached.Add(n);
                });
            }
            return best.Select(b => b / far).ToArray();
        }

        /// <summary>按转角场画一帧：对画布上每个点，两次迭代求出它在原图里的来处，再双线性取色。</summary>
        internal void Draw(double t, double strength)
        {
            var cos = new float[Levels];
            var sin = new float[Levels];
            double phase = 2 * Math.PI * Hz * t;
            for (int i = 0; i < Levels; i++)
            {
                double u = i / (Levels - 1.0);
                double theta = strength * (Swing * Smooth(u / 0.85) * Math.Sin(phase - Lag * u) + Curl * u * u * Math.Sin(2 * phase - Lag * u - 0.6)) * Math.PI / 180;
                cos[i] = (float)Math.Cos(theta);
                sin[i] = (float)Math.Sin(theta);
            }
            Array.Clear(pixels, 0, pixels.Length);
            for (int oy = 0; oy < CH; oy++)
                for (int ox = 0; ox < CW; ox++)
                {
                    float dx = ox - Side + 0.5f - RootX, dy = oy - Top + 0.5f - RootY, qx = dx, qy = dy;
                    for (int k = 0; k < 2; k++)
                    {
                        int ux = Math.Min(Math.Max((int)(RootX + qx), 0), W - 1), uy = Math.Min(Math.Max((int)(RootY + qy), 0), H - 1);
                        int i = (int)(along[uy * W + ux] * (Levels - 1));
                        qx = dx * cos[i] + dy * sin[i];
                        qy = dy * cos[i] - dx * sin[i];
                    }
                    float sx = RootX + qx - 0.5f, sy = RootY + qy - 0.5f;
                    int x0 = (int)Math.Floor(sx), y0 = (int)Math.Floor(sy);
                    if (x0 < -1 || y0 < -1 || x0 >= W || y0 >= H) continue;
                    int wx = (int)((sx - x0) * 256), wy = (int)((sy - y0) * 256);
                    int a = ((y0 + 1) * Stride + x0 + 1) * 4, o = (oy * CW + ox) * 4;
                    if ((source[a + 3] | source[a + 7] | source[a + Stride * 4 + 3] | source[a + Stride * 4 + 7]) == 0) continue;
                    for (int c = 0; c < 4; c++)
                    {
                        int upper = source[a + c] * (256 - wx) + source[a + 4 + c] * wx;
                        int lower = source[a + Stride * 4 + c] * (256 - wx) + source[a + Stride * 4 + 4 + c] * wx;
                        pixels[o + c] = (byte)((upper * (256 - wy) + lower * wy + 32768) >> 16);
                    }
                }
            canvas.WritePixels(new Int32Rect(0, 0, CW, CH), pixels, CW * 4, 0);
        }
    }
}
