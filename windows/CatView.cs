using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace InkCat
{
    /// <summary>事件驱动：没有输入时只有偶尔的眨眼和摇尾。</summary>
    sealed class CatView : Canvas
    {
        public static readonly Size Scene = new Size(1200, 896), BaseSize = new Size(300, 224);
        static readonly Random random = new Random();

        public bool ShowsBubbles;

        /// <summary>设置窗口里的演示猫：不看真实输入，所有动作可用。</summary>
        readonly bool isPreview;

        readonly ScaleTransform zoom = new ScaleTransform();
        readonly TranslateTransform shake = new TranslateTransform();
        readonly Canvas stage, arms = Layer(), lights = Layer(), joint = Layer(), bubbles = Layer(), mood = Layer(), blush = Layer(), sweat = Layer(), mouse = Layer(), desk = Layer();
        readonly Image plate = Picture(0, 0, 1200, 896), face = Picture(400, 200, 360, 272), mouseImage = Picture(0, 0, 176, 136);
        readonly Image tail = Picture(860, 290, 240, 400);
        readonly Image[] squints = { Picture(400, 250, 290, 150), Picture(400, 250, 290, 150) };
        readonly RotateTransform wag = new RotateTransform(0, 916 - 860, 652 - 290);
        readonly TranslateTransform mouseSpot = new TranslateTransform(), bob = new TranslateTransform();
        readonly Dictionary<int, Shape> glows = new Dictionary<int, Shape>();

        // 以猫自己的左右命名：猫的左手在画面右侧，兼管鼠标。
        readonly BendyArm right = new BendyArm("arm_right");
        readonly Arm left = new Arm("arm_left", new Point(815, 572), 233), mouser = new Arm("arm_mouse", new Point(804, 560), 151);

        readonly Canvas[] buttons = { Layer(), Layer() };
        readonly bool[] pressed = new bool[2];
        readonly Path wheel = new Path { Fill = glow, Stroke = ink, StrokeThickness = 4, Opacity = 0 };

        static readonly Point leftHome = new Point(600, 640), rightHome = new Point(362, 612);
        Point leftRest = leftHome, rightRest = rightHome, mousePosition, mouseAt = new Point(0.5, 0.5);
        readonly List<int> held = new List<int>();
        readonly Dictionary<Hand, Point> tapped = new Dictionary<Hand, Point>();  // 隐私模式下正按着的落点
        Hand spaceHand = Hand.Left, tapHand = Hand.Left;
        bool leftOnMouse = true, secure, napping, swishing, sweating, shaking;
        readonly List<DateTime> keyTimes = new List<DateTime>();
        int moodLevel;
        DateTime lastActivity = DateTime.UtcNow, lastKey = DateTime.UtcNow, lastFollow, lastScroll, nextBlink, nextWag, nextZ;

        Look look;
        public Look Look { set { look = value; Dress(); } }

        public CatView(bool preview = false, Look look = null)
        {
            isPreview = preview;
            this.look = look ?? new Look();
            var group = new TransformGroup { Children = { zoom, shake } };
            stage = new Canvas { Width = Scene.Width, Height = Scene.Height, RenderTransform = group };
            Children.Add(stage);
            Build();
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo info)
        {
            base.OnRenderSizeChanged(info);
            zoom.ScaleX = zoom.ScaleY = ActualWidth / Scene.Width;
            Dress();
        }

        protected override void OnDpiChanged(DpiScale before, DpiScale after)
        {
            base.OnDpiChanged(before, after);
            Dress();
        }

        // ———— 画图小工具 ————

        static SolidColorBrush Color(int hex, double alpha = 1)
        {
            var b = new SolidColorBrush(System.Windows.Media.Color.FromArgb((byte)(alpha * 255), (byte)(hex >> 16), (byte)(hex >> 8), (byte)hex));
            b.Freeze();
            return b;
        }

        static readonly Brush ink = Color(0x4A3428), paper = Color(0xFFF8EC), glow = Color(0xFF8A4C), spark = Color(0xDD7F5C);

        static Canvas Layer() => new Canvas();

        static Image Picture(double x, double y, double w, double h)
        {
            var i = new Image { Width = w, Height = h, Stretch = Stretch.Fill };
            SetLeft(i, x);
            SetTop(i, y);
            RenderOptions.SetBitmapScalingMode(i, BitmapScalingMode.HighQuality);
            return i;
        }

        static Path Outline(string data, Brush fill = null, Brush stroke = null, double width = 0) => Outline(Geometry.Parse(data), fill, stroke, width);

        static Path Outline(Geometry data, Brush fill = null, Brush stroke = null, double width = 0) => new Path
        {
            Data = data, Fill = fill, Stroke = stroke, StrokeThickness = width,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
        };

        static TextBlock Label(string text, double size, Brush color)
        {
            var t = new TextBlock { Text = text, FontSize = size, Foreground = color, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI") };
            t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            SetLeft(t, -t.DesiredSize.Width / 2);
            SetTop(t, -t.DesiredSize.Height / 2);
            return t;
        }

        static Duration Seconds(double s) => new Duration(TimeSpan.FromSeconds(s));

        static void Fade(UIElement e, double opacity, double seconds = 0)
        {
            e.BeginAnimation(OpacityProperty, seconds == 0 ? null : new DoubleAnimation(opacity, Seconds(seconds)));
            e.Opacity = opacity;
        }

        static void After(double seconds, Action action)
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            t.Tick += (s, e) => { t.Stop(); action(); };
            t.Start();
        }

        // ———— 搭建 ————

        void Build()
        {
            void Add(params UIElement[] all) { foreach (var e in all) stage.Children.Add(e); }

            tail.RenderTransform = wag;
            face.Opacity = 0;
            BuildMood();
            // 桌面：平时画在底图里，躲开时才单独拿出来
            deskPlate.Visibility = Visibility.Collapsed;
            desk.RenderTransform = deskShift;
            desk.Children.Add(deskPlate);
            desk.Children.Add(lights);
            Add(tail, plate, mood, face, desk);

            mouse.RenderTransform = mouseSpot;
            mouseImage.Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.22, BlurRadius = 12, ShadowDepth = 11.4, Direction = 322 };
            mouse.Children.Add(mouseImage);
            foreach (var b in buttons) mouse.Children.Add(b);
            mouse.Children.Add(wheel);
            foreach (var b in buttons) b.Opacity = 0;

            arms.Children.Add(joint);
            foreach (var limb in new Limb[] { right, left, mouser }) arms.Children.Add(limb);
            desk.Children.Add(mouse);
            Add(arms, bubbles);

            MoveMouse(new Point(0.5, 0.5), 0);
            left.Reach(leftRest, 0);
            right.Reach(rightRest, 0);
            mouser.Reach(MousePaw, 0);
            ShowLeftArm();
            Dress();
        }

        /// <summary>换上当前装扮的图。</summary>
        void Dress()
        {
            ReleaseAll();  // 键位可能变了
            double detail = Math.Min(1, 1.5 * zoom.ScaleX * VisualTreeHelper.GetDpi(this).DpiScaleX);
            tail.Source = look.Image("tail");
            face.Source = look.Image("face_sleep");
            squints[0].Source = look.Image("eyes_focus");
            squints[1].Source = look.Image("eyes_tense");
            mouseImage.Source = look.Image("mouse");
            (mouseImage.Width, mouseImage.Height) = (look.MouseSize.Width, look.MouseSize.Height);
            foreach (var limb in new Limb[] { right, left, mouser }) limb.Dress(look, detail);
            BuildLights();
            BuildJoint();
            BuildButtons();
            MoveMouse(mouseAt, 0);
            mouser.Reach(MousePaw, 0);
            ShowPose();
        }

        void ShowPose() => plate.Source = deskPlate.Source = look.Image(secure ? "plate_cover" : "plate");

        // ———— 躲开 ————

        readonly Image deskPlate = Picture(0, 0, 1200, 896);
        readonly TranslateTransform deskShift = new TranslateTransform();
        bool gone;

        /// <summary>桌沿以下归桌面：返回桌面那一侧或猫那一侧。</summary>
        static Geometry DeskSide(bool below) => Geometry.Parse($"M-10,524.6 L455,599 455,604.3 1210,713.8 1210,{(below ? 900 : -10)} -10,{(below ? 900 : -10)}Z");

        /// <summary>把底图拆成猫和桌面两层，桌面才能单独移动。</summary>
        void Split(bool on)
        {
            deskPlate.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            deskPlate.Clip = on ? DeskSide(true) : null;
            plate.Clip = on ? DeskSide(false) : null;
        }

        static void Animate(UIElement target, DependencyProperty property, double to, double seconds, EasingMode ease, Action then = null)
        {
            var a = new DoubleAnimation(to, Seconds(seconds)) { EasingFunction = new QuadraticEase { EasingMode = ease } };
            if (then != null) a.Completed += (s, e) => then();
            target.BeginAnimation(property, a);
        }

        void Slide(double x, double y, double seconds, EasingMode ease, Action then)
        {
            var a = new DoubleAnimation(y, Seconds(seconds)) { EasingFunction = new QuadraticEase { EasingMode = ease } };
            deskShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, Seconds(seconds)) { EasingFunction = new QuadraticEase { EasingMode = ease } });
            a.Completed += (s, e) => then();
            deskShift.BeginAnimation(TranslateTransform.YProperty, a);
        }

        /// <summary>躲开：键盘、桌垫、鼠标往左下滑走，猫再淡出，结束后调用 done。</summary>
        public void Vanish(Action done)
        {
            gone = true;
            Split(true);
            Animate(desk, OpacityProperty, 0, 0.4, EasingMode.EaseIn);
            Slide(-560, 420, 0.4, EasingMode.EaseIn, () =>
            {
                if (gone) Animate(stage, OpacityProperty, 0, 0.35, EasingMode.EaseInOut, () => { if (gone) done(); });
            });
        }

        /// <summary>回来：猫先淡入，桌面再从左下滑回来。</summary>
        public void Appear()
        {
            if (!gone) return;
            gone = false;
            Animate(stage, OpacityProperty, 1, 0.3, EasingMode.EaseInOut, () =>
            {
                if (gone) return;
                Animate(desk, OpacityProperty, 1, 0.4, EasingMode.EaseOut);
                Slide(0, 0, 0.4, EasingMode.EaseOut, () => { if (!gone) Split(false); });
            });
        }

        /// <summary>键帽亮光，平时透明。</summary>
        void BuildLights()
        {
            lights.Children.Clear();
            glows.Clear();
            foreach (var key in Keyboard.Keys.Values)
            {
                var h = key.Half * (1 - 5 / key.Half.Length);
                var d = new Vector(8, -7);  // 键深方向的一半
                var shape = new Polygon
                {
                    Points = new PointCollection { key.Center + h + d, key.Center + h - d, key.Center - h - d, key.Center - h + d },
                    Fill = glow, Stroke = glow, StrokeThickness = 5, StrokeLineJoin = PenLineJoin.Round, Opacity = 0,
                };
                glows[key.Code] = shape;
                lights.Children.Add(shape);
            }
        }

        /// <summary>右肩外侧的圆弧，手臂内收时补住轮廓。</summary>
        void BuildJoint()
        {
            joint.Children.Clear();
            if (look.Joint.Length != 2) return;
            Point Rim(double turn) => new Point(450 + 41 * Math.Cos(turn * Math.PI), 531 - 41 * Math.Sin(turn * Math.PI));
            string rim = FormattableString.Invariant($"{Rim(0.62)} A41,41 0 0 0 {Rim(1.05)}");
            joint.Children.Add(Outline($"M450,531 L{rim}Z", Color(look.Joint[0])));
            joint.Children.Add(Outline($"M{rim}", null, Color(look.Joint[1]), 6));
        }

        void ShowLeftArm()
        {
            left.Visibility = leftOnMouse ? Visibility.Hidden : Visibility.Visible;
            mouser.Visibility = leftOnMouse ? Visibility.Visible : Visibility.Hidden;
        }

        /// <summary>左右键高亮、火花与滚轮。</summary>
        void BuildButtons()
        {
            Point[][] areas = { look.LeftButton, look.RightButton }, classic = { Look.ClassicLeft, Look.ClassicRight };
            string[] rays = { "M44,140 36,164 M78,140 80,168 M110,132 124,155", "M2,70 -22,62 M-2,96 -28,102 M12,122 -8,142" };
            for (int i = 0; i < 2; i++)
            {
                buttons[i].Children.Clear();
                var lit = new Canvas { Width = mouseImage.Width, Height = mouseImage.Height, Children = { Outline(Area(areas[i]), Color(0xFF8A4C, 0.85)) } };
                if (look.Images.Count > 0) lit.OpacityMask = new ImageBrush(look.Image("mouse")) { ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(look.MouseSize) };  // 亮区不超出鼠标
                buttons[i].Children.Add(lit);
                var sparks = Outline(rays[i], null, spark, 10);
                var shift = Middle(areas[i]) - Middle(classic[i]);  // 火花跟着按键走
                sparks.RenderTransform = new TranslateTransform(shift.X, shift.Y);
                buttons[i].Children.Add(sparks);
            }
            wheel.Data = Area(look.Wheel);
        }

        static Geometry Area(Point[] points) => new PathGeometry(new[] { new PathFigure(points[0], new[] { new PolyLineSegment(points.Skip(1), true) }, true) });

        static Point Middle(Point[] p) => new Point((p.Min(q => q.X) + p.Max(q => q.X)) / 2, (p.Min(q => q.Y) + p.Max(q => q.Y)) / 2);

        /// <summary><paramref name="p"/> 为光标在屏幕内的比例位置（左下原点）。猫面向观众，它的右和前对应画面的左和下。</summary>
        void MoveMouse(Point p, double seconds)
        {
            var far = new Point(880, 742) + (new Point(774, 719) - new Point(880, 742)) * p.X;
            var near = new Point(806, 783) + (new Point(704, 776) - new Point(806, 783)) * p.X;
            mouseAt = p;
            mousePosition = far + (near - far) * p.Y;
            void Slide(DependencyProperty axis, double value)
            {
                mouseSpot.BeginAnimation(axis, seconds == 0 ? null : new DoubleAnimation(value, Seconds(seconds)));
                mouseSpot.SetValue(axis, value);
            }
            Slide(TranslateTransform.XProperty, mousePosition.X - mouseImage.Width / 2);
            Slide(TranslateTransform.YProperty, mousePosition.Y - mouseImage.Height / 2);
        }

        /// <summary>按下时爪子往前压一点。</summary>
        Point MousePaw => mousePosition + (pressed.Contains(true) ? new Vector(-9, 2) : new Vector(-3, -10));

        // ———— 键盘 ————

        public void Key(int code, bool down)
        {
            if (!isPreview && PasswordGuard.IsActive) return;
            var key = Keyboard.Find(code);
            if (key == null) return;
            if (!down) { Release(code); return; }
            if (held.Contains(code)) return;
            Wake();
            NoteTyping();
            held.Add(code);
            if (key.Hand == Hand.Either) spaceHand = Other(spaceHand);
            Fade(glows[key.Code], 0.75, 0.03);
            if (ShowsBubbles && Keyboard.Modifier(code) == null) Bubble(Chord(key.Label), key);
            UpdateArms();
        }

        static Hand Other(Hand h) => h == Hand.Left ? Hand.Right : Hand.Left;
        static Point Lifted(Point p) => p - new Vector(0, 40);

        void NoteTyping()
        {
            lastKey = DateTime.UtcNow;
            if (!isPreview) keyTimes.Add(lastKey);
            UpdateMood();
            HoldTail();
        }

        /// <summary>隐私模式：只知道有人敲了一下。用哪只手、落在哪里都是随机的，不亮键、不冒气泡。</summary>
        public void TapAnonymously()
        {
            if (secure || !isPreview && PasswordGuard.IsActive) return;  // 连密码的长度和节奏也不显示
            Wake();
            NoteTyping();
            if (random.Next(4) > 0) tapHand = Other(tapHand);
            var hand = tapHand;
            var spots = TapSpots[hand];
            var p = spots[random.Next(spots.Length)];
            tapped[hand] = p;
            UpdateArms();
            After(0.1, () =>
            {
                if (!tapped.TryGetValue(hand, out var now) || now != p) return;
                tapped.Remove(hand);
                UpdateArms();
            });
        }

        /// <summary>字母区各键的位置，按手分组。</summary>
        static Dictionary<Hand, Point[]> TapSpots => Keyboard.Keys.Values
            .Where(k => k.Label.Length == 1 && k.Label[0] >= 'A' && k.Label[0] <= 'Z')
            .GroupBy(k => k.Hand, k => k.Center).ToDictionary(g => g.Key, g => g.ToArray());

        void Release(int code)
        {
            if (!held.Remove(code)) return;
            var key = Keyboard.Find(code);
            if (held.All(c => Keyboard.Find(c).Code != key.Code)) Fade(glows[key.Code], 0, 0.3);
            UpdateArms();
        }

        void ReleaseAll()
        {
            tapped.Clear();
            foreach (int code in held.ToArray()) Release(code);
        }

        /// <summary>抹掉刚才按键的痕迹：松开所有键、清掉气泡、爪子回到起手位。</summary>
        public void ForgetKeys()
        {
            ReleaseAll();
            bubbles.Children.Clear();
            Rest();
        }

        void Rest()
        {
            (leftRest, rightRest) = (leftHome, rightHome);
            UpdateArms();
        }

        string Chord(string label)
        {
            var names = held.Select(Keyboard.Modifier).ToArray();
            return string.Join("+", new[] { "Ctrl", "Win", "Alt", "Shift" }.Where(names.Contains).Append(label));
        }

        void UpdateArms()
        {
            // 这只手最后按住的键；空格按本次轮到的手偏向一侧
            Point? Target(Hand hand)
            {
                var key = held.Select(Keyboard.Find).LastOrDefault(k => (k.Hand == Hand.Either ? spaceHand : k.Hand) == hand);
                if (key == null) return tapped.TryGetValue(hand, out var p) ? p : (Point?)null;
                return key.Hand == Hand.Either ? key.Center + new Vector(hand == Hand.Left ? 50 : -60, 0) : key.Center;
            }
            if (Target(Hand.Right) is Point r)
            {
                right.Reach(r, 0.05);
                rightRest = Lifted(r);
            }
            else right.Reach(rightRest, 0.14);

            if (Target(Hand.Left) is Point l)
            {
                leftOnMouse = false;
                ShowLeftArm();
                left.Reach(l, 0.05);
                leftRest = Lifted(l);
            }
            else if (!leftOnMouse) left.Reach(leftRest, 0.14);
        }

        void Bubble(string text, Key key)
        {
            bool pop = Enabled("pop");
            if (bubbles.Children.Count > 7) bubbles.Children.RemoveAt(0);
            var label = Label(text, 54, ink);
            double width = Math.Max(96, label.DesiredSize.Width + 48);
            var box = new Rectangle { Width = width, Height = 92, RadiusX = 30, RadiusY = 30, Fill = pop ? Color(key.Tint) : paper, Stroke = ink, StrokeThickness = 6 };
            SetLeft(box, -width / 2);
            SetTop(box, -46);
            var b = new Canvas { Children = { box, label } };
            Float(b, new Point(Math.Max(key.Center.X, width / 2 + 8), key.Center.Y - 130), 120, 0.8, pop);
        }

        void Float(Canvas b, Point from, double rise, double life, bool pop = false)
        {
            var scale = new ScaleTransform();
            var place = new TranslateTransform(from.X, from.Y);
            b.RenderTransform = new TransformGroup { Children = { scale, place } };
            b.IsHitTestVisible = false;
            bubbles.Children.Add(b);

            DoubleAnimationUsingKeyFrames Frames(params (double value, double time)[] all)
            {
                var a = new DoubleAnimationUsingKeyFrames { Duration = Seconds(life), DecelerationRatio = 1 };
                foreach (var (value, time) in all) a.KeyFrames.Add(new LinearDoubleKeyFrame(value, KeyTime.FromPercent(time)));
                return a;
            }
            place.BeginAnimation(TranslateTransform.YProperty, Frames((from.Y, 0), (from.Y - rise, 1)));
            if (pop)  // 弹出并略微飘移
            {
                var grow = Frames((0.6, 0), (1.12, 0.1), (1, 0.22), (1, 1));
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
                place.BeginAnimation(TranslateTransform.XProperty, Frames((from.X, 0), (from.X + random.NextDouble() * 52 - 26, 1)));
            }
            var fade = Frames((1, 0), (1, 0.5), (0, 1));
            fade.Completed += (s, e) => bubbles.Children.Remove(b);
            b.BeginAnimation(OpacityProperty, fade);
        }

        // ———— 鼠标 ————

        public void Press(int button, bool down)
        {
            Wake();
            HoldTail();
            GrabMouse();
            pressed[button] = down;
            Fade(buttons[button], down ? 1 : 0, down ? 0.02 : 0.25);
            mouser.Reach(MousePaw, down ? 0.05 : 0.14);
        }

        public void Scroll(bool up)
        {
            var now = DateTime.UtcNow;
            if ((now - lastScroll).TotalSeconds < 0.18) return;
            lastScroll = now;
            Wake();
            GrabMouse();
            wheel.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, Seconds(0.4)));
            var disc = new Ellipse { Width = 68, Height = 68, Fill = paper, Stroke = ink, StrokeThickness = 5 };
            SetLeft(disc, -34);
            SetTop(disc, -34);
            Float(new Canvas { Children = { disc, Label(up ? "↑" : "↓", 46, ink) } },
                  mousePosition + new Vector(-120, up ? 10 : -30), up ? 50 : -50, 0.5);
        }

        /// <summary>猫的左手空着时回到鼠标上。</summary>
        void GrabMouse()
        {
            if (leftOnMouse || tapped.ContainsKey(Hand.Left) || held.Any(c => Keyboard.Find(c).Hand == Hand.Left)) return;
            leftOnMouse = true;
            ShowLeftArm();
            mouser.Reach(MousePaw, 0);
        }

        /// <summary>跟随真实光标，每秒最多 30 次。</summary>
        public void Follow()
        {
            var now = DateTime.UtcNow;
            if ((now - lastFollow).TotalSeconds < 1 / 30.0) return;
            lastFollow = now;
            var p = Native.Cursor;
            if (!(Native.MonitorAt(p, true) is Native.MONITORINFO m)) return;
            SlideMouse(new Point((double)(p.X - m.Monitor.Left) / m.Monitor.Width, 1 - (double)(p.Y - m.Monitor.Top) / m.Monitor.Height));
        }

        public void SlideMouse(Point p)
        {
            Wake();
            MoveMouse(p, 0.06);
            mouser.Reach(MousePaw, 0.06);
            GrabMouse();
        }

        // ———— 神态 ————

        /// <summary>可在设置里开关的动作：tail、mood、shake、pop。</summary>
        bool Enabled(string feature) => isPreview || Prefs.Get(feature, true);

        /// <summary>眯眼、腮红、汗滴，默认透明。</summary>
        void BuildMood()
        {
            foreach (var squint in squints)
            {
                squint.Opacity = 0;
                mood.Children.Add(squint);
            }
            blush.Children.Add(Outline(new EllipseGeometry(new Rect(432, 342, 48, 24)), Color(0xEB755F)));
            blush.Children.Add(Outline(new EllipseGeometry(new Rect(614, 350, 52, 24)), Color(0xEB755F)));
            sweat.Children.Add(Outline("M0,-26 C22,2 18,24 0,24 C-18,24 -22,2 0,-26Z", Color(0x9EDDF5), ink, 5));
            sweat.Children.Add(Outline(new EllipseGeometry(new Rect(-7, -4, 7, 12)), Brushes.White));
            sweat.RenderTransform = new TransformGroup { Children = { new TranslateTransform(716, 246), bob } };
            foreach (var layer in new[] { blush, sweat })
            {
                layer.Opacity = 0;
                mood.Children.Add(layer);
            }
        }

        void UpdateMood()
        {
            if (isPreview) return;
            var now = DateTime.UtcNow;
            keyTimes.RemoveAll(t => (now - t).TotalSeconds > 1.2);
            ShowMood(secure ? 0 : keyTimes.Count / 1.2);
        }

        /// <summary>打字越快神情越紧：每秒 5 键起眯眼，7.5 键起脸红冒汗，9 键起兴奋抖动；可按灵敏度整体缩放。</summary>
        public void ShowMood(double speed)
        {
            if (!isPreview) speed /= Prefs.Get("moodScale", 1.0);
            // 回落要比触发再低 1 键，避免来回跳
            moodLevel = new[] { 5, 7.5, 9 }.Where((t, i) => speed >= t - (i < moodLevel ? 1 : 0)).Count();
            bool on = Enabled("mood"), hot = on && moodLevel >= 2;
            for (int i = 0; i < 2; i++) Fade(squints[i], on && moodLevel > 0 && i == Math.Min(moodLevel, 2) - 1 ? 1 : 0, 0.08);  // 眼形不同，快切以免重影
            if (hot != sweating)
            {
                sweating = hot;
                Fade(blush, hot ? 0.32 : 0, 0.35);
                Fade(sweat, hot ? 1 : 0, 0.35);
                bob.BeginAnimation(TranslateTransform.YProperty, !hot ? null : new DoubleAnimation(0, 12, Seconds(0.35)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            }
            bool shakes = moodLevel >= 3 && Enabled("shake");
            if (shakes != shaking)
            {
                shaking = shakes;
                shake.BeginAnimation(TranslateTransform.XProperty, !shakes ? null : new DoubleAnimationUsingKeyFrames
                {
                    KeyFrames = { new LinearDoubleKeyFrame(0), new LinearDoubleKeyFrame(0.8), new LinearDoubleKeyFrame(0), new LinearDoubleKeyFrame(-0.8), new LinearDoubleKeyFrame(0) },
                    Duration = Seconds(0.12), RepeatBehavior = RepeatBehavior.Forever,
                });
            }
        }

        /// <summary>甩两下尾巴：主摆加一点尾尖抖动，首尾平滑。</summary>
        public void Swish()
        {
            if (swishing) return;
            swishing = true;
            var a = new DoubleAnimationUsingKeyFrames { Duration = Seconds(3.5), FillBehavior = FillBehavior.Stop };
            for (int i = 0; i <= 60; i++)
            {
                double t = i / 60.0, p = t * 4 * Math.PI;
                double angle = (10.5 * Math.Sin(p) + 3.8 * Math.Sin(2 * p + 0.7)) * Math.Min(1, Math.Min(t, 1 - t) * 6);
                a.KeyFrames.Add(new LinearDoubleKeyFrame(angle, KeyTime.FromPercent(t)));
            }
            a.Completed += (s, e) => swishing = false;
            wag.BeginAnimation(RotateTransform.AngleProperty, a);
        }

        /// <summary>一有操作就收住尾巴。</summary>
        void HoldTail()
        {
            nextWag = DateTime.UtcNow.AddSeconds(1.5);
            if (!swishing) return;
            swishing = false;
            wag.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, Seconds(0.25)) { FillBehavior = FillBehavior.Stop });
        }

        public void Blink()
        {
            Fade(face, 1);
            After(0.13, () => Fade(face, napping ? 1 : 0));
        }

        public void FloatZ() => Float(new Canvas { Children = { Label("z", 64, paper) } }, new Point(800, 210), 120, 2.5);

        /// <summary>演示结束后复原。</summary>
        public void Settle()
        {
            ReleaseAll();
            Press(0, false);
            Press(1, false);
            if (secure) SetSecure(false);
            Wake();
            ShowMood(0);
            Rest();
            GrabMouse();
        }

        /// <summary>每 0.3 秒一次：密码捂眼、补漏抬键、眨眼、甩尾、打盹。</summary>
        public void Heartbeat()
        {
            var now = DateTime.UtcNow;
            if (secure != PasswordGuard.IsActive) SetSecure(!secure);
            UpdateMood();
            if (secure) return;
            foreach (int code in held.Where(c => !RawInput.IsDown(c)).ToArray()) Release(code);

            if (held.Count == 0 && (now - lastKey).TotalSeconds > 1 && (leftRest != leftHome || rightRest != rightHome)) Rest();  // 停手后回到起手位
            for (int i = 0; i < 2; i++) if (pressed[i] && !RawInput.IsButtonDown(i)) Press(i, false);

            double napAfter = Prefs.Get("nap", 90.0);
            if (napAfter > 0 && held.Count == 0 && (now - lastActivity).TotalSeconds > napAfter) SetNapping(true);
            if (napping)
            {
                if (now < nextZ) return;
                nextZ = now.AddSeconds(3);
                FloatZ();
                return;
            }
            if (now > nextBlink)
            {
                nextBlink = now.AddSeconds(3 + 3 * random.NextDouble());
                Blink();
            }
            if (now > nextWag && held.Count == 0 && Enabled("tail"))
            {
                nextWag = now.AddSeconds(4 + 2 * random.NextDouble());
                Swish();
            }
        }

        public void SetSecure(bool on)
        {
            ReleaseAll();
            Wake();
            secure = on;
            arms.Visibility = on ? Visibility.Hidden : Visibility.Visible;
            if (on) bubbles.Children.Clear();
            ShowPose();
        }

        void Wake()
        {
            lastActivity = DateTime.UtcNow;
            SetNapping(false);
        }

        public void SetNapping(bool on)
        {
            if (napping == on) return;
            napping = on;
            Fade(face, on ? 1 : 0, 0.25);
        }
    }
}
