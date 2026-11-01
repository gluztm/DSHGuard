using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DSHGuard;

public static partial class SelfTest
{
    internal static void RunMarketAddedSortChecks(Action<string, bool, string> check, Action<string, string> skip)
    {
        // ── ① 「最近收录」比较器的三条判据（纯函数，用手工构造的条目逐格断言）──
        static PluginMarket.MarketPlugin P(string name, string added)
            => new() { Name = name, Npm = name, Added = added };

        var items = new List<PluginMarket.MarketPlugin>
        {
            P("a", "2026-10-01"),
            P("b", "2026-10-28"),
            P("c", ""),
            P("d", "2026-10-28"),
            P("e", "   "),
            P("f", "2026-09-15"),
        };
        var desc = PluginMarket.SortByAdded(items, true).Select(x => x.Name).ToList();
        check("最近收录排序 · 倒序：日期新的在前，同一天按名字稳定，缺日期垫底",
            desc.SequenceEqual(new[] { "b", "d", "a", "f", "c", "e" }),
            string.Join(" → ", desc));

        var asc = PluginMarket.SortByAdded(items, false).Select(x => x.Name).ToList();
        check("最近收录排序 · 正序：日期旧的在前，缺日期**仍然垫底**（缺信息不是'日期最小'）",
            asc.SequenceEqual(new[] { "f", "a", "b", "d", "c", "e" }),
            string.Join(" → ", asc));

        // 稳定性：同一批条目在不同顺序喂进去，结果必须一致（否则每次刷新顺序都在抖）
        var shuffled1 = new List<PluginMarket.MarketPlugin> { items[4], items[0], items[2], items[5], items[3], items[1] };
        var shuffled2 = new List<PluginMarket.MarketPlugin> { items[3], items[5], items[1], items[4], items[2], items[0] };
        var s1 = PluginMarket.SortByAdded(shuffled1, true).Select(x => x.Name).ToList();
        var s2 = PluginMarket.SortByAdded(shuffled2, true).Select(x => x.Name).ToList();
        check("最近收录排序 · 与输入顺序无关（同一批条目怎么打乱都排成同一个序列）",
            s1.SequenceEqual(s2) && s1.SequenceEqual(desc),
            $"{string.Join(" → ", s1)} ／ {string.Join(" → ", s2)}");

        // 带时分秒的日期：归一后按天比较（同一天不同时刻仍算同一天，交给名字序）
        var withTime = new List<PluginMarket.MarketPlugin>
        {
            P("x", "2026-10-28T18:00:00Z"),
            P("y", "2026-10-28"),
            P("z", "2026-10-29T01:00:00Z"),
        };
        var timed = PluginMarket.SortByAdded(withTime, true).Select(x => x.Name).ToList();
        check("最近收录排序 · 带时分秒的日期按天归一，同一天仍按名字定序",
            timed.SequenceEqual(new[] { "z", "x", "y" }),
            string.Join(" → ", timed));

        // ── ② 默认档就是「最近收录」（两处默认值必须同一方向：Filter 的默认参数与界面初值）──
        check("最近收录排序 · MarketSort 的枚举第一项就是 Added（默认档）",
            (int)PluginMarket.MarketSort.Added == 0,
            $"Added={PluginMarket.MarketSort.Added}（{PluginMarket.MarketSort.Added}）");

        // ── ③ 界面上那一行真的存在，且默认档显示为「最近收录」──
        //   这里只断控件与文案（控件由真实 MainWindow 造出来，不自造假对象）。
        //   ⚠ 需要窗口实例，这里改用既有的窗口级自检入口（见 SelfTest.cs [市场]）——
        //     那里断言 FilterFieldAdded 存在 + 筛选文案含「最近收录」。本文件只补纯函数部分，
        //     免得两处各造一个 MainWindow（那会让自检慢一倍，也会让失败定位变模糊）。

        // ── ④ 真实目录里的首屏顺序在窗口级自检里断（那里手上就有已加载的目录，见 SelfTest.cs [市场]）。
        //   本文件不自己 LoadAsync：那会多跑一次目录加载、还可能是联网的，把一条纯函数断言变成网络依赖。
    }
}
