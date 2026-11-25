using System;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace InkCat
{
    /// <summary>预乘 RGBA 浮点图，左上原点。</summary>
    sealed class Raster
    {
        public readonly int Width, Height;
        public readonly Vector4[] Pixels;

        public Raster(int width = Cutter.W, int height = Cutter.H)
        {
            (Width, Height) = (width, height);
            Pixels = new Vector4[width * height];
        }

        public Raster(BitmapSource source) : this(source.PixelWidth, source.PixelHeight)
        {
            var b = new byte[Width * Height * 4];
            new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0).CopyPixels(b, Width * 4, 0);
            for (int i = 0; i < Pixels.Length; i++) Pixels[i] = new Vector4(b[i * 4 + 2], b[i * 4 + 1], b[i * 4], b[i * 4 + 3]) / 255;
        }

        Raster(Raster other) : this(other.Width, other.Height) => Array.Copy(other.Pixels, Pixels, Pixels.Length);
        public Raster Copy() => new Raster(this);

        public Vector4 this[int x, int y]
        {
            get => x < 0 || y < 0 || x >= Width || y >= Height ? Vector4.Zero : Pixels[y * Width + x];
            set { if (x >= 0 && y >= 0 && x < Width && y < Height) Pixels[y * Width + x] = value; }
        }

        public Vector4 Sample(Point p)
        {
            int x = (int)Math.Floor(p.X), y = (int)Math.Floor(p.Y);
            float fx = (float)p.X - x, fy = (float)p.Y - y;
            return (this[x, y] * (1 - fx) + this[x + 1, y] * fx) * (1 - fy) + (this[x, y + 1] * (1 - fx) + this[x + 1, y + 1] * fx) * fy;
        }

        public BitmapSource Image
        {
            get
            {
                var b = new byte[Pixels.Length * 4];
                byte Level(float v) => (byte)Math.Max(0, Math.Min(255, Math.Round(v * 255, MidpointRounding.AwayFromZero)));
                for (int i = 0; i < Pixels.Length; i++)
                {
                    var v = Pixels[i];
                    (b[i * 4], b[i * 4 + 1], b[i * 4 + 2], b[i * 4 + 3]) = (Level(v.Z), Level(v.Y), Level(v.X), Level(v.W));
                }
                var image = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Pbgra32, null, b, Width * 4);
                image.Freeze();
                return image;
            }
        }

        public Raster Crop(int x0, int y0, int w, int h)
        {
            var o = new Raster(w, h);
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) o[x, y] = this[x0 + x, y0 + y];
            return o;
        }

        public Raster Crop(Int32Rect r) => Crop(r.X, r.Y, r.Width, r.Height);

        /// <summary>把 <paramref name="top"/> 叠在自己上面。</summary>
        public Raster Under(Raster top)
        {
            for (int i = 0; i < Pixels.Length; i++) Pixels[i] = top.Pixels[i] + Pixels[i] * (1 - top.Pixels[i].W);
            return this;
        }

        /// <summary>按 <paramref name="hole"/> 的不透明度挖空。</summary>
        public Raster Erase(Raster hole)
        {
            for (int i = 0; i < Pixels.Length; i++) Pixels[i] *= 1 - hole.Pixels[i].W;
            return this;
        }

        /// <summary>不透明区域的外框；全透明时为 null。</summary>
        public Int32Rect? Bounds
        {
            get
            {
                int x0 = Width, x1 = -1, y0 = Height, y1 = -1;
                for (int y = 0; y < Height; y++)
                    for (int x = 0; x < Width; x++)
                        if (Pixels[y * Width + x].W > 40 / 255f)
                        {
                            x0 = Math.Min(x0, x); x1 = Math.Max(x1, x);
                            y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
                        }
                return x1 < 0 ? (Int32Rect?)null : new Int32Rect(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
            }
        }

        /// <summary>移动、缩放、旋转后的样子，每点取 2×2 个样。</summary>
        public Raster Transformed(Matrix m)
        {
            m.Invert();
            var o = new Raster(Width, Height);
            for (int y = 0; y < Height; y++)
                for (int x = 0; x < Width; x++)
                {
                    var sum = Vector4.Zero;
                    for (int k = 0; k < 4; k++)
                    {
                        var p = m.Transform(new Point(x + 0.25 + 0.5 * (k & 1), y + 0.25 + 0.5 * (k >> 1)));
                        if (p.X > -2 && p.Y > -2 && p.X < Width + 2 && p.Y < Height + 2) sum += Sample(new Point(p.X - 0.5, p.Y - 0.5));
                    }
                    o.Pixels[y * Width + x] = sum / 4;
                }
            return o;
        }
    }
}
