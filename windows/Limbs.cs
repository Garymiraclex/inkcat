using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace InkCat
{
    /// <summary>手臂的姿势是一组数；换姿势时从当前插值到新值。</summary>
    abstract class Limb : FrameworkElement
    {
        static readonly DependencyProperty progress = DependencyProperty.Register(
            "Progress", typeof(double), typeof(Limb), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));
        static readonly IEasingFunction ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        double[] from, to;

        protected Limb() => RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.Linear);

        public abstract void Dress(Look look, double detail);
        protected abstract void Draw(DrawingContext dc, double[] pose);

        double[] Pose()
        {
            double t = (double)GetValue(progress);
            return from.Zip(to, (a, b) => a + (b - a) * t).ToArray();
        }

        protected void Move(double[] pose, double seconds)
        {
            from = seconds == 0 || to == null ? pose : Pose();
            to = pose;
            BeginAnimation(progress, seconds == 0 ? null : new DoubleAnimation(0, 1, TimeSpan.FromSeconds(seconds)) { EasingFunction = ease });
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (to != null) Draw(dc, Pose());
        }

        /// <summary>素材的一条横带，按比例取。</summary>
        protected static Brush Band(ImageSource image, double top, double bottom)
        {
            var brush = new ImageBrush(image) { Viewbox = new Rect(0, top, 1, bottom - top), Stretch = Stretch.Fill };
            brush.Freeze();
            return brush;
        }
    }

    /// <summary>一条手臂：素材竖直放置，肩点在 (120, 80)。旋转指向目标，只拉伸小臂中段，爪子保持原样。</summary>
    sealed class Arm : Limb
    {
        const double width = 240, height = 400, root = 80;
        readonly string asset;
        readonly Point shoulder;
        readonly double length, upper, lower;  // 拉伸段在素材里的上下边
        Brush top, middle, bottom;

        public Arm(string asset, Point shoulder, double length)
        {
            (this.asset, this.shoulder, this.length) = (asset, shoulder, length);
            (upper, lower) = (root + length * 0.2, root + length * 0.6);
        }

        public override void Dress(Look look, double detail)
        {
            var image = look.Image(asset, detail);
            top = Band(image, 0, upper / height);
            middle = Band(image, upper / height, lower / height);
            bottom = Band(image, lower / height, 1);
            InvalidateVisual();
        }

        public void Reach(Point tip, double seconds)
        {
            var d = tip - shoulder;
            double reach = Math.Min(Math.Max(d.Length, length * 0.9), length * 2);
            Move(new[] { -Math.Atan2(d.X, d.Y) * 180 / Math.PI, reach - length }, seconds);
        }

        protected override void Draw(DrawingContext dc, double[] pose)
        {
            double stretch = pose[1];
            dc.PushTransform(new TranslateTransform(shoulder.X, shoulder.Y));
            dc.PushTransform(new RotateTransform(pose[0]));
            dc.PushTransform(new TranslateTransform(-width / 2, -root));
            dc.DrawRectangle(middle, null, new Rect(0, upper - 0.5, width, lower - upper + stretch + 1));  // 上下各多半点，避免接缝
            dc.DrawRectangle(top, null, new Rect(0, 0, width, upper));
            dc.DrawRectangle(bottom, null, new Rect(0, lower + stretch, width, height - lower));
        }
    }

    /// <summary>短臂：素材是展平的直条（原版宽 140，自己的猫宽 180，肩在上），臂段切成薄片沿曲线排布，肘部自然弯；爪子是单独一块画在手腕上，只在竖直朝下附近小幅偏转。</summary>
    sealed class BendyArm : Limb
    {
        const double length = 175, paw = 76, lap = 10;
        const int count = 42, fields = 4;  // 每片：x、y、角度、高度
        static readonly int sleeveRows = (int)Math.Ceiling((length - paw + lap) * count / length);
        static readonly Brush pawFade = MakePawFade(), rootOnly = MakeRootOnly();
        double width = 140;
        readonly string asset;
        readonly Point rest;
        readonly Brush[][] bands = new Brush[2][];  // 外展、内收
        readonly Brush[] paws = new Brush[2];
        ImageSource shoulder;  // 内收时盖住根部的肩头
        bool tucked, covered, locked;

        /// <summary><paramref name="rest"/> 是放松时的落点：那时爪子随臂自然弯，不锁。</summary>
        public BendyArm(string asset, Point rest)
        {
            (this.asset, this.rest) = (asset, rest);
            RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);  // 切片边缘若半透明，叠起来会透出网纹；轮廓的平滑来自图片本身
        }

        static Brush MakePawFade()
        {
            var b = new LinearGradientBrush(Colors.Transparent, Colors.White, new Point(0, 0), new Point(0, lap))
            {
                MappingMode = BrushMappingMode.Absolute,
            };
            b.Freeze();
            return b;
        }

        /// <summary>肩头一直盖着的猫（胸毛鬃毛），手臂内收后只盖肩点 (462, 496) 半径 50 以内，免得把伸进胸前的手臂切掉一截。</summary>
        static Brush MakeRootOnly()
        {
            var b = new RadialGradientBrush(new GradientStopCollection
            {
                new GradientStop(Colors.White, 0),
                new GradientStop(Colors.White, 0.72),
                new GradientStop(Colors.Transparent, 1),
            })
            {
                MappingMode = BrushMappingMode.Absolute,
                Center = new Point(462, 496),
                GradientOrigin = new Point(462, 496),
                RadiusX = 50,
                RadiusY = 50,
            };
            b.Freeze();
            return b;
        }

        // 上下各多取半片，避免弯曲处露缝
        static double Top(int i) => Math.Max(0, i - 0.5);
        static double Bottom(int i) => Math.Min(count, i + 1.5);

        public override void Dress(Look look, double detail)
        {
            shoulder = look.Image("shoulder", detail);
            width = look.Image(asset).PixelWidth;
            covered = look.Joint.Length == 0;
            for (int k = 0; k < 2; k++)
            {
                var image = look.Image(k == 0 ? asset : asset + "_in", detail);
                bands[k] = Enumerable.Range(0, count).Select(i => Band(image, Top(i) / count, Bottom(i) / count)).ToArray();
                paws[k] = Band(image, 1 - paw / length, 1);
            }
            InvalidateVisual();
        }

        public void Reach(Point target, double seconds)
        {
            // 目标越靠内，手臂起点越靠胸前、起势越朝下
            const double home = 369;
            double inward = Clamp((target.X - home) / 125), outward = Clamp((home - target.X) / 150);
            var start = new Point(480 - 2 * inward, 528 - 6 * inward);
            double launch = ((1 - inward) + inward * 1.42 + outward * 0.04) * Math.PI;  // 从向左到朝下
            double lean = -0.1 - 0.35 * outward + 0.12 * inward, d = (target - start).Length;
            var c1 = start + new Vector(Math.Cos(launch), -Math.Sin(launch)) * d * 0.36;
            var c2 = target - new Vector(Math.Sin(lean), Math.Cos(lean)) * d * 0.5;

            Point Curve(double t)
            {
                double u = 1 - t, a = u * u * u, b = 3 * u * u * t, c = 3 * u * t * t, e = t * t * t;
                return new Point(a * start.X + b * c1.X + c * c2.X + e * target.X, a * start.Y + b * c1.Y + c * c2.Y + e * target.Y);
            }
            const int steps = 80;
            var samples = Enumerable.Range(0, steps + 1).Select(i => Curve((double)i / steps)).ToArray();
            var arc = new double[steps + 1];
            for (int i = 1; i <= steps; i++) arc[i] = arc[i - 1] + (samples[i] - samples[i - 1]).Length;

            // 素材上的位置 → 曲线弧长：爪子不伸缩，只拉伸臂段
            double sleeve = length - paw, room = Math.Max(arc[steps] - paw, 20);
            double Place(double y) => y < sleeve ? y / sleeve * room : room + (y - sleeve);
            Point On(double s)
            {
                int i = Array.FindIndex(arc, a => a >= s);
                i = i < 0 ? steps : Math.Max(i, 1);
                return samples[i - 1] + (samples[i] - samples[i - 1]) * ((s - arc[i - 1]) / Math.Max(arc[i] - arc[i - 1], 0.001));
            }

            tucked = inward > 0.15;
            locked = target != rest;
            var pose = new double[count * fields + 4];
            for (int i = 0; i < count; i++)
            {
                double a = Place(length * i / count), b = Place(length * (i + 1) / count), mid = (a + b) / 2;
                var p = On(mid);
                var tangent = On(mid + 9) - On(mid - 9);  // 取宽一点的切线更平滑
                pose[i * fields] = p.X;
                pose[i * fields + 1] = p.Y;
                pose[i * fields + 2] = -Math.Atan2(tangent.X, tangent.Y) * 180 / Math.PI;
                pose[i * fields + 3] = b - a;
            }
            pose[count * fields] = Math.Min(inward * 4, 1);
            // 爪子：朝手腕到指尖的方向，夹在 -20°~+25°（朝右为正）；沿爪轴推一推，没夹时指尖正好落在目标上
            var wrist = On(room);
            var tip = On(arc[steps]);
            var span = tip - wrist;
            double tilt = Math.Min(Math.Max(Math.Atan2(span.X, span.Y) * 180 / Math.PI, -20), 25);
            double rad = tilt * Math.PI / 180;
            pose[count * fields + 1] = wrist.X + Math.Sin(rad) * (span.Length - paw);
            pose[count * fields + 2] = wrist.Y + Math.Cos(rad) * (span.Length - paw);
            pose[count * fields + 3] = -tilt;
            Move(pose, seconds);
        }

        static double Clamp(double v) => Math.Min(Math.Max(v, 0), 1);

        protected override void Draw(DrawingContext dc, double[] pose)
        {
            int k = tucked ? 1 : 0, rows = locked ? sleeveRows : count;
            for (int i = 0; i < rows; i++)
            {
                double h = pose[i * fields + 3];
                dc.PushTransform(new TranslateTransform(pose[i * fields], pose[i * fields + 1]));
                dc.PushTransform(new RotateTransform(pose[i * fields + 2]));
                dc.DrawRectangle(bands[k][i], null, new Rect(-width / 2, (Top(i) - i - 0.5) * h, width, (Bottom(i) - Top(i)) * h));
                dc.Pop();
                dc.Pop();
            }
            if (locked)
            {
                dc.PushTransform(new TranslateTransform(pose[count * fields + 1], pose[count * fields + 2]));
                dc.PushTransform(new RotateTransform(pose[count * fields + 3]));
                dc.PushOpacityMask(pawFade);
                dc.DrawRectangle(paws[k], null, new Rect(-width / 2, 0, width, paw));
                dc.Pop();
                dc.Pop();
                dc.Pop();
            }
            if (covered && tucked) dc.PushOpacityMask(rootOnly);
            dc.PushOpacity(covered ? 1 : pose[count * fields]);
            dc.DrawImage(shoulder, new Rect(396, 440, 120, 120));
            dc.Pop();
            if (covered && tucked) dc.Pop();
        }
    }
}
