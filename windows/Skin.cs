using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace InkCat
{
    /// <summary>外观：猫是同一只，只换耳机、键盘、鼠标和桌垫。皮肤里没有的图沿用原版。</summary>
    sealed class Skin
    {
        public readonly string Id, Title;
        /// <summary>鼠标的左键、右键和滚轮，坐标取自鼠标素材（左上原点）。</summary>
        public readonly string LeftButton, RightButton, Wheel;

        Skin(string id, string title, string left, string right, string wheel)
        {
            (Id, Title) = (id, title);
            (LeftButton, RightButton, Wheel) = ($"M{left}Z", $"M{right}Z", $"M{wheel}Z");
        }

        public static readonly Skin[] All =
        {
            new Skin("classic", "原版",
                "40,109 43,98 48,91 54,86 64,68 72,50 98,60 112,72 101,92 90,104 76,110 56,112",
                "35,108 38,98 42,90 39,84 43,72 50,62 60,59 64,50 50,35 38,47 24,67 17,84 18,97 26,104",
                "42,87 42,80 47,71 54,64 62,64 62,69 56,79 51,87 46,89"),
            new Skin("gamer", "电竞",
                "30,117 33,102 40,100 47,96 55,84 64,67 73,55 86,54 98,56 103,61 92,79 80,100 71,112 61,119 51,122 38,120",
                "26,115 30,101 28,93 31,83 37,72 43,61 48,51 49,46 42,43 33,42 25,48 17,60 11,72 5,85 4,96 8,104 16,110",
                "34,90 35,83 40,72 47,63 54,62 57,65 52,73 46,85 43,93 38,93"),
        };

        public static Skin Current => Array.Find(All, s => s.Id == Prefs.Get("skin", "")) ?? All[0];

        public ImageSource Image(string name, double scale = 1)
        {
            try { return Load($"{Id}/{name}", scale); }
            catch (IOException) { return Load(name, scale); }
        }

        /// <summary>按显示尺寸预先缩小，动起来就不必每帧重新采样。</summary>
        public static ImageSource Load(string path, double scale = 1)
        {
            BitmapSource image = new BitmapImage(new Uri($"pack://application:,,,/InkCat;component/assets/{path}.png"));
            if (scale < 1) image = new CachedBitmap(new TransformedBitmap(image, new ScaleTransform(scale, scale)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            image.Freeze();
            return image;
        }
    }
}
