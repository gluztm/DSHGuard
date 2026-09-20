using System;
using System.Windows.Media;

namespace DSHGuard;

/// <summary>
/// 底端口头禅与彩蛋。
///
/// · 常态一句话：「不是蓝色大肥鱼，是鲸！」；
/// · 两条彩蛋各 2% 概率触发（每次「事件信息」发生变动时逐条掷骰）；
/// · 底端文字、说明页文字、事件信息里报出的那条，三处共用同一句话与同一个颜色；
/// · 颜色从四色池里随机取，且不取程序里已在使用的语义色（红/橙/绿/蓝/淡蓝/灰白）。
/// </summary>
internal static class Mascot
{
    internal const string NormalLine = "不是蓝色大肥鱼，是鲸！";

    /// <summary>两条彩蛋（各自 2%）。</summary>
    private static readonly string[] EggLines =
    {
        "你目录里的DSH是什么...大烧货吗？",
        "DSH是什么？DeepSeek Hentai?（别说这个）"
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
    internal static double EggChance { get; set; } = 0.02;

    /// <summary>彩蛋的随机格式：字号 / 字重 / 斜体 / 下划线 / 字宽。</summary>
    internal readonly record struct EggFormat(
        double Size, System.Windows.FontWeight Weight, bool Italic, bool Underline, System.Windows.FontStretch Stretch);

    private static readonly double[] Sizes = { 12, 13.5, 15, 16.5 };
    private static readonly System.Windows.FontWeight[] Weights =
    {
        System.Windows.FontWeights.Normal, System.Windows.FontWeights.SemiBold, System.Windows.FontWeights.Bold
    };
    private static readonly System.Windows.FontStretch[] Stretches =
    {
        System.Windows.FontStretches.Normal, System.Windows.FontStretches.Condensed, System.Windows.FontStretches.Expanded
    };

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
    /// 掷一次彩蛋骰：两条彩蛋各自的触发概率都是 <see cref="EggChance"/>，但**互斥**——
    /// 一次变动最多显示一条（先掷第一条，命中就不再掷第二条）。
    /// 命中则换句子并重掷颜色，返回该彩蛋文案；未命中返回 null。
    /// </summary>
    internal static string? Roll()
    {
        lock (Gate)
        {
            string? hit = null;
            if (Rng.NextDouble() < EggChance) hit = EggLines[0];
            else if (Rng.NextDouble() < EggChance) hit = EggLines[1];

            if (hit != null)
            {
                _line = hit;
                _color = Palette[Rng.Next(Palette.Length)];
                // 格式也随机：字号 / 字重 / 斜体 / 下划线 / 字宽 各掷一次
                _format = new EggFormat(
                    Sizes[Rng.Next(Sizes.Length)],
                    Weights[Rng.Next(Weights.Length)],
                    Rng.Next(2) == 1,
                    Rng.Next(2) == 1,
                    Stretches[Rng.Next(Stretches.Length)]);
            }
            return hit;
        }
    }

    internal static bool IsPaletteColor(Color c)
    {
        foreach (var p in Palette) if (p == c) return true;
        return false;
    }

    /// <summary>自检用：彩蛋池（应恰好两条）。</summary>
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
