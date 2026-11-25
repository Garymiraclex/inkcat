using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace InkCat
{
    /// <summary>设置窗口里可播放的动作。</summary>
    sealed class Demo
    {
        public readonly string Name, Detail, Pref;
        public readonly bool IsNew;
        readonly Func<CatView, Func<double, Task>, Task> script;

        Demo(string name, string detail, Func<CatView, Func<double, Task>, Task> script, string pref = null, bool isNew = false) =>
            (Name, Detail, this.script, Pref, IsNew) = (name, detail, script, pref, isNew);

        public Task Play(CatView cat, CancellationToken cancel) => script(cat, s => Task.Delay(TimeSpan.FromSeconds(s), cancel));

        static readonly int[] text = { 0x23, 0x12, 0x26, 0x26, 0x18, 0x39, 0x32, 0x18, 0x14, 0x16, 0x1E, 0x31 };  // hello motuan

        static async Task Tap(CatView cat, Func<double, Task> wait, double gap, params int[] codes)
        {
            foreach (int code in codes)
            {
                cat.Key(code, true);
                await wait(gap * 0.55);
                cat.Key(code, false);
                await wait(gap * 0.45);
            }
        }

        static async Task Click(CatView cat, Func<double, Task> wait, int button)
        {
            for (int i = 0; i < 3; i++)
            {
                cat.Press(button, true);
                await wait(0.25);
                cat.Press(button, false);
                await wait(0.35);
            }
        }

        public static readonly Demo[] All =
        {
            new Demo("隐私模式打字", "只有敲击节奏，落点随机，不亮键、不冒气泡", async (cat, wait) =>
            {
                for (int i = 0; i < 14; i++) { cat.TapAnonymously(); await wait(0.22); }
            }, isNew: true),
            new Demo("甩尾巴", "空闲时自然甩尾，一打字就收住", async (cat, wait) => { cat.Swish(); await wait(3.6); }, "tail", true),
            new Demo("专注眯眼", "每秒 5 键以上，眼睛微微眯起", async (cat, wait) =>
            {
                cat.ShowMood(6);
                await Tap(cat, wait, 0.28, text.Take(8).ToArray());
            }, "mood", true),
            new Demo("脸红冒汗", "每秒 7.5 键以上，眯得更紧，脸红冒汗", async (cat, wait) =>
            {
                cat.ShowMood(8);
                await Tap(cat, wait, 0.17, text.Concat(text.Take(4)).ToArray());
            }, "mood", true),
            new Demo("兴奋抖动", "每秒 9 键以上，全身微微抖动", async (cat, wait) =>
            {
                cat.ShowMood(10);
                await Tap(cat, wait, 0.12, text.Concat(text).ToArray());
            }, "shake", true),
            new Demo("彩色气泡", "气泡弹出并飘移，按键种类不同颜色不同", (cat, wait) => Tap(cat, wait, 0.45, 0x1E, 0x01, 0x0F, 0x24, 0x1C, 0xE04D), "pop", true),
            new Demo("左右手打字", "按标准指法分工，爪子落到具体的键", (cat, wait) => Tap(cat, wait, 0.3, text)),
            new Demo("快捷键", "气泡显示组合键", async (cat, wait) =>
            {
                foreach (int code in new[] { 0x2E, 0x2F })
                {
                    cat.Key(0x1D, true);
                    await wait(0.2);
                    await Tap(cat, wait, 0.5, code);
                    cat.Key(0x1D, false);
                    await wait(0.3);
                }
            }),
            new Demo("空格轮换", "两只手轮流按空格", (cat, wait) => Tap(cat, wait, 0.5, 0x39, 0x39, 0x39, 0x39)),
            new Demo("移动鼠标", "鼠标在桌垫上跟手移动", async (cat, wait) =>
            {
                for (int i = 0; i <= 80; i++)
                {
                    double a = i / 80.0 * 2 * Math.PI;
                    cat.SlideMouse(new Point(0.5 + 0.5 * Math.Sin(a), 0.5 - 0.5 * Math.Sin(2 * a)));
                    await wait(0.035);
                }
                cat.SlideMouse(new Point(0.5, 0.5));
            }),
            new Demo("左键", "对应半边亮起并弹出火花", (cat, wait) => Click(cat, wait, 0)),
            new Demo("右键", "对应半边亮起并弹出火花", (cat, wait) => Click(cat, wait, 1)),
            new Demo("滚轮", "滚轮亮起，气泡显示方向", async (cat, wait) =>
            {
                for (int i = 0; i < 6; i++) { cat.Scroll(i < 3); await wait(0.35); }
            }),
            new Demo("眨眼", "每 3–6 秒一次", async (cat, wait) =>
            {
                for (int i = 0; i < 2; i++) { cat.Blink(); await wait(0.7); }
            }),
            new Demo("打盹", "长时间没有操作时睡着", async (cat, wait) =>
            {
                cat.SetNapping(true);
                cat.FloatZ();
                await wait(1.6);
                cat.FloatZ();
                await wait(2);
            }),
            new Demo("输密码捂眼", "认出密码框时捂眼；终端等认不出来", async (cat, wait) => { cat.SetSecure(true); await wait(2); }),
        };
    }
}
