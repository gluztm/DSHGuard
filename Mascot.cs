using System;
using System.Windows.Media;

namespace DSHGuard;

/// <summary>
/// 底端口头禅与彩蛋。
///
/// · 常态一句话：「不是蓝色大肥鱼，是鲸！」；
/// · 彩蛋池含原味自嘲与 DeepSeek 经典梗句，每次「事件信息」发生变动时逐条互斥掷骰；
/// · 底端文字、说明页文字、事件信息里报出的那条，三处共用同一句话与同一个同源锚点色；
/// · 颜色从四色池里随机取，且不取程序里已在使用的语义色（红/橙/绿/蓝/淡蓝/灰白）。
/// </summary>
internal static class Mascot
{
    internal const string NormalLine = "不是蓝色大肥鱼，是鲸！";

    /// <summary>彩蛋语录池（互斥触发，点击底栏亦可逐条轮换；前两条为历史契约锚点）。</summary>
    private static readonly string[] EggLines =
    {
        "你目录里的DSH是什么...大烧货吗？",
        "DSH是什么？DeepSeek Hentai?（别说这个）",
        "卧槽，用户彻底怒了",
        "服务器繁忙，请稍候重试。",
        "已深度思考（用时 999 秒）",
        "好的，让我们重新审视一下……不对，怎么又报错了？",
        "嗯，用户想要个五彩斑斓的黑，这很合理（确信）"
    };

    /// <summary>备选色池：青绿 / 靛紫 / 紫 / 品红——都不在程序的语义配色里。</summary>
    private static readonly Color[] Palette =
    {
        Color.FromRgb(0x12, 0xE2, 0xC8),
        Color.FromRgb(0x8A, 0x6C, 0xFF),
        Color.FromRgb(0xC8, 0x6B, 0xFF),
        Color.FromRgb(0xFF, 0x6B, 0xC1)
    };

    private static readonly Random Rng = new();
    private static readonly object Gate = new();

    /// <summary>每条彩蛋的触发概率（自检可调整）。</summary>
    internal static double EggChance { get; set; } = 0.015;

    /// <summary>彩蛋的格式化样式：字号 / 字重 / 斜体 / 下划线 / 字宽。</summary>
    internal readonly record struct EggFormat(
        double Size, System.Windows.FontWeight Weight, bool Italic, bool Underline, System.Windows.FontStretch Stretch);

    private static readonly double[] Sizes = { 12.5, 13.0 };

    private static string _line = NormalLine;
    private static Color _color = Palette[0];
    private static EggFormat _format = DefaultFormat;

    /// <summary>常态格式：原样的字号与字重，无修饰。</summary>
    internal static EggFormat DefaultFormat => new(12, System.Windows.FontWeights.Normal, false, false, System.Windows.FontStretches.Normal);

    static Mascot() { _color = Palette[Rng.Next(Palette.Length)]; }

    internal static string CurrentLine { get { lock (Gate) return _line; } }
    internal static Color CurrentColor { get { lock (Gate) return _color; } }
    internal static EggFormat CurrentFormat { get { lock (Gate) return _format; } }

    /// <summary>当前是否是彩蛋（否则为常态）。</summary>
    internal static bool IsEgg { get { lock (Gate) return _line != NormalLine; } }

    /// <summary>一次性取走当前状态：避免分别读取时被后台掷骰插入，出现"旧文案配新颜色"的撕裂。</summary>
    internal static (string Line, Color Color, EggFormat Format, bool IsEgg) State()
    {
        lock (Gate)
            return (_line, _color, _format, _line != NormalLine);
    }

    /// <summary>
    /// 掷一次彩蛋骰：逐条按 <see cref="EggChance"/> 互斥抽取，一次变动最多命中一条；
    /// 命中则换句子、重掷同源锚点色并匹配克制醒目的字体修饰，返回该彩蛋文案；未命中返回 null。
    /// </summary>
    internal static string? Roll()
    {
        lock (Gate)
        {
            string? hit = null;
            for (int i = 0; i < EggLines.Length; i++)
            {
                if (Rng.NextDouble() < EggChance)
                {
                    hit = EggLines[i];
                    break;
                }
            }

            if (hit != null)
            {
                _line = hit;
                _color = Palette[Rng.Next(Palette.Length)];
                _format = BuildCleanEggFormat();
            }
            return hit;
        }
    }

    /// <summary>用户点击底栏时依次切换下一条 DeepSeek 梗句彩蛋，走完一轮后回到常态口头禅。</summary>
    internal static string CycleNextEgg()
    {
        lock (Gate)
        {
            int idx = _line == NormalLine ? -1 : Array.IndexOf(EggLines, _line);
            if (idx < EggLines.Length - 1)
            {
                _line = EggLines[idx + 1];
                int curColorIdx = Array.IndexOf(Palette, _color);
                _color = Palette[(curColorIdx + 1) % Palette.Length];
                _format = BuildCleanEggFormat();
            }
            else
            {
                _line = NormalLine;
                _format = DefaultFormat;
            }
            return _line;
        }
    }

    private static EggFormat BuildCleanEggFormat()
        => new(
            Sizes[Rng.Next(Sizes.Length)],
            System.Windows.FontWeights.SemiBold,
            Rng.Next(3) == 0,
            false,
            System.Windows.FontStretches.Normal);

    internal static bool IsPaletteColor(Color c)
    {
        foreach (var p in Palette) if (p == c) return true;
        return false;
    }

    /// <summary>自检用：彩蛋语录池只读快照。</summary>
    internal static string[] EggLinesForTest() => (string[])EggLines.Clone();

    /// <summary>自检用：恢复常态并重掷颜色。</summary>
    internal static void ResetForTest()
    {
        lock (Gate)
        {
            _line = NormalLine;
            _color = Palette[Rng.Next(Palette.Length)];
            _format = DefaultFormat;
        }
    }
}
