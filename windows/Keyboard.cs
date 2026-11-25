using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;

namespace InkCat
{
    /// <summary>使用者的手，也是猫的手：猫的左手在画面右侧。空格两手轮换。</summary>
    enum Hand { Left, Right, Either }

    sealed class Key
    {
        public int Code;
        public string Label;
        public Point Center;
        /// <summary>沿键盘长边的半宽（含方向）。</summary>
        public Vector Half;
        public Hand Hand;
        /// <summary>气泡配色，随画中键帽：珊瑚、灰绿、米白。</summary>
        public int Tint;
    }

    /// <summary>
    /// 画面坐标：1200×896，原点左上。键盘朝向猫：每排由猫的左手端排到右手端，即画面从右到左。
    /// 键码为物理扫描码，扩展键加 0xE000，与输入法和键盘布局无关。
    /// </summary>
    static class Keyboard
    {
        /// <summary>"扫描码:键帽:宽度"，宽度缺省为 1，每排合计 15。</summary>
        static readonly string[] rows =
        {
            "01:Esc:1.5 3B:F1 3C:F2 3D:F3 3E:F4 3F:F5 40:F6 41:F7 42:F8 43:F9 44:F10 57:F11 58:F12 E053:Del:1.5",
            "29:` 02:1 03:2 04:3 05:4 06:5 07:6 08:7 09:8 0A:9 0B:0 0C:- 0D:= 0E:⌫:2",
            "0F:Tab:1.5 10:Q 11:W 12:E 13:R 14:T 15:Y 16:U 17:I 18:O 19:P 1A:[ 1B:] 2B:\\:1.5",
            "3A:Caps:1.75 1E:A 1F:S 20:D 21:F 22:G 23:H 24:J 25:K 26:L 27:; 28:' 1C:Enter:2.25",
            "2A:Shift:2 2C:Z 2D:X 2E:C 2F:V 30:B 31:N 32:M 33:, 34:. 35:/ 36:Shift E048:↑ E04F:End",
            "1D:Ctrl:1.5 E05B:Win:1.25 38:Alt:1.5 39:␣:5.5 E038:Alt:1.25 E01D:Ctrl E04B:← E050:↓ E04D:→",
        };

        /// <summary>画中 65% 键盘各排的两端；它没有 F 区，与数字排共用一排。</summary>
        static readonly (Point, Point)[] ends =
        {
            (new Point(600, 722), new Point(195, 650)),
            (new Point(600, 722), new Point(195, 650)),
            (new Point(628, 702), new Point(222, 634)),
            (new Point(652, 682), new Point(248, 619)),
            (new Point(672, 667), new Point(270, 604)),
            (new Point(692, 655), new Point(297, 592)),
        };

        const int space = 0x39;
        static readonly int[] coral = { 0x01, 0x1C };
        static readonly int[] green =
        {
            0x0E, 0x0F, 0x3A, 0x2A, 0x36, 0x1D, 0x38, 0xE01D, 0xE038, 0xE05B, 0xE053, 0xE04F, 0xE048, 0xE04B, 0xE050, 0xE04D,
        };

        public static readonly Dictionary<int, Key> Keys = Build();

        static Dictionary<int, Key> Build()
        {
            var result = new Dictionary<int, Key>();
            for (int row = 0; row < rows.Length; row++)
            {
                var (a, b) = ends[row];
                var unit = (b - a) / 15;
                double u = 0;
                foreach (var f in rows[row].Split(' ').Select(item => item.Split(':')))
                {
                    double width = f.Length > 2 ? double.Parse(f[2], CultureInfo.InvariantCulture) : 1, mid = u + width / 2;
                    int code = Convert.ToInt32(f[0], 16);
                    result[code] = new Key
                    {
                        Code = code,
                        Label = f[1],
                        Center = a + unit * mid,
                        Half = unit * width / 2,
                        Hand = code == space ? Hand.Either : mid < 6.9 ? Hand.Left : Hand.Right,
                        Tint = coral.Contains(code) ? 0xF2A38C : green.Contains(code) ? 0xC7D9C2 : 0xFFF8EC,
                    };
                    u += width;
                }
            }
            return result;
        }

        /// <summary>小键盘与右 Win 映射到主键区。</summary>
        static readonly Dictionary<int, int> aliases = new Dictionary<int, int>
        {
            [0xE05C] = 0xE01D,
            [0x52] = 0x0B, [0x4F] = 0x02, [0x50] = 0x03, [0x51] = 0x04, [0x4B] = 0x05, [0x4C] = 0x06, [0x4D] = 0x07,
            [0x47] = 0x08, [0x48] = 0x09, [0x49] = 0x0A, [0x53] = 0x34, [0x37] = 0x09, [0x4E] = 0x0D, [0x4A] = 0x0C,
            [0xE035] = 0x35, [0xE01C] = 0x1C,
        };

        public static Key Find(int code)
        {
            Keys.TryGetValue(aliases.TryGetValue(code, out int main) ? main : code, out Key key);
            return key;
        }

        public static string Modifier(int code)
        {
            switch (code)
            {
                case 0x1D: case 0xE01D: return "Ctrl";
                case 0xE05B: case 0xE05C: return "Win";
                case 0x38: case 0xE038: return "Alt";
                case 0x2A: case 0x36: return "Shift";
                default: return null;
            }
        }
    }
}
