using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace DSHGuard;

/// <summary>
/// 「界面卡住的那几秒，它到底在干什么」——只回答这一个问题的极轻量埋点。
///
/// 为谁做的（现场反馈 N-2）：新机上偶尔整页卡几秒，看门狗只报「UI 线程 N 秒没有响应」，
/// 于是只知道"卡了"、不知道"卡在哪一段"——插件扫描？建卡片？还是查新合并？三种原因
/// 的修法完全不同，日志里却看不出区别。这份埋点把「正在做哪一段」留在内存里，卡顿发生时
/// 由看门狗一次性写进异常日志（见 MainWindow.xaml.cs 的界面心跳段）。
///
/// 四条硬约束（不然埋点自己会变成新的卡顿源）：
///   1. 无锁：Enter/Exit 只做两次 Volatile 读写 + 一枚时间戳，绝不等待别人；
///   2. 常数时间：不遍历、不排序、不分配集合，只有一次字符串拼接；
///   3. 永不抛：名字为 null 也照记，埋点失败不该让正在做的事失败；
///   4. 只在内存里：不写盘、不上屏、不改界面，唯一出口是 Peek()。
///
/// 嵌套是允许的（RenderPlugins 里套扫描），Exit 会把「当前」退回外层那一段，
/// 并把刚结束的那段记成「上一段」——于是卡顿时既能看到踩在哪儿，也能看到刚做完什么。
/// </summary>
internal static class UiTrace
{
    /// <summary>一次 <see cref="Enter"/> 的凭据：退出时用它算出耗时并回退外层。</summary>
    internal sealed class Entry
    {
        internal Entry(string name, Snapshot previous, long startTicks, bool tracked = true)
        {
            Name = name;
            Previous = previous;
            StartTicks = startTicks;
            Tracked = tracked;
        }

        internal string Name { get; }

        internal Snapshot Previous { get; }

        internal long StartTicks { get; }

        internal bool Tracked { get; }
    }

    /// <summary>某一瞬间的读数：当前在做什么、这段已持续多久、上一段是什么、上一段用了多久。</summary>
    internal sealed class Snapshot
    {
        internal Snapshot(string current, long currentTicks, string last, double lastMs)
        {
            Current = current;
            CurrentTicks = currentTicks;
            Last = last;
            LastMs = lastMs;
        }

        /// <summary>当前段名；空串 = 空闲（没有包在任何埋点里）。</summary>
        internal string Current { get; }

        /// <summary>当前段的起始 <see cref="Stopwatch.GetTimestamp"/>；0 = 空闲。</summary>
        internal long CurrentTicks { get; }

        /// <summary>上一段的段名；空串 = 还没有任何一段结束过。</summary>
        internal string Last { get; }

        /// <summary>上一段耗时（毫秒）；负数 = 没有读数。</summary>
        internal double LastMs { get; }
    }

    private static readonly Snapshot Empty = new Snapshot("", 0, "", -1);

    /// <summary>当前读数。引用整体替换（不是逐字段改写），读的一方永远看到完整的一份。</summary>
    private static Snapshot _cur = Empty;

    private static bool IsUiThread()
    {
        var disp = System.Windows.Application.Current?.Dispatcher;
        return disp == null || disp.Thread.ManagedThreadId == Environment.CurrentManagedThreadId;
    }

    /// <summary>进入一段：记下段名与起点，返回退出时要用的凭据。非 UI 线程调用不污染主界面埋点。</summary>
    internal static Entry Enter(string name)
    {
        string n = string.IsNullOrEmpty(name) ? "（未命名）" : name;

        Snapshot prev = Volatile.Read(ref _cur) ?? Empty;
        if (!IsUiThread()) return new Entry(n, prev, 0, tracked: false);

        long ticks = Stopwatch.GetTimestamp();
        Volatile.Write(ref _cur, new Snapshot(n, ticks, prev.Last, prev.LastMs));
        return new Entry(n, prev, ticks, tracked: true);
    }

    /// <summary>离开一段：记下耗时，把「当前」退回外层那一段。</summary>
    internal static void Exit(Entry entry)
    {
        if (entry == null || !entry.Tracked) return;

        long now = Stopwatch.GetTimestamp();
        double ms = (now - entry.StartTicks) * 1000.0 / Stopwatch.Frequency;
        if (ms < 0) ms = 0;

        Snapshot prev = entry.Previous ?? Empty;
        Volatile.Write(ref _cur, new Snapshot(prev.Current, prev.CurrentTicks, entry.Name, ms));
        RecordMetric(entry.Name, ms);
    }

    // ══════════════ 2.4.0 分操作耗时聚合（有界环形缓冲 + TryEnter 非阻塞）══════════════

    internal sealed record OpMetric(string Name, int Count, double LastMs, double P95Ms, double MaxMs);

    private sealed class OpSeries
    {
        internal int TotalCount;
        internal double LastMs;
        internal double MaxMs;
        internal readonly double[] Ring = new double[64];
        internal int RingCount;
        internal int RingHead;
    }

    private static readonly object StatsLock = new();
    private static readonly Dictionary<string, OpSeries> Stats = new(StringComparer.Ordinal);
    private const int MaxTrackedOps = 64;

    private static void RecordMetric(string name, double ms)
    {
        if (string.IsNullOrEmpty(name)) return;
        bool taken = false;
        try
        {
            Monitor.TryEnter(StatsLock, 0, ref taken);
            if (!taken) return;

            if (!Stats.TryGetValue(name, out var s))
            {
                if (Stats.Count >= MaxTrackedOps) return;
                s = new OpSeries();
                Stats[name] = s;
            }
            s.TotalCount++;
            s.LastMs = ms;
            if (ms > s.MaxMs) s.MaxMs = ms;
            s.Ring[s.RingHead] = ms;
            s.RingHead = (s.RingHead + 1) % s.Ring.Length;
            if (s.RingCount < s.Ring.Length) s.RingCount++;
        }
        catch { }
        finally
        {
            if (taken) Monitor.Exit(StatsLock);
        }
    }

    internal static IReadOnlyList<OpMetric> SummarySnapshot()
    {
        lock (StatsLock)
        {
            var list = new List<OpMetric>(Stats.Count);
            foreach (var (name, s) in Stats)
            {
                double p95 = s.LastMs;
                if (s.RingCount > 0)
                {
                    var sample = new double[s.RingCount];
                    Array.Copy(s.Ring, sample, s.RingCount);
                    Array.Sort(sample);
                    int idx = (int)Math.Ceiling(sample.Length * 0.95) - 1;
                    if (idx < 0) idx = 0;
                    if (idx >= sample.Length) idx = sample.Length - 1;
                    p95 = sample[idx];
                }
                list.Add(new OpMetric(name, s.TotalCount, s.LastMs, p95, s.MaxMs));
            }
            list.Sort((a, b) => b.P95Ms.CompareTo(a.P95Ms));
            return list;
        }
    }

    internal static string DescribeSummary()
    {
        var items = SummarySnapshot();
        if (items.Count == 0) return "UI 埋点：暂无记录";
        var top = items[0];
        return "UI 埋点共 " + items.Count + " 项｜最慢 P95：" + top.Name + " " + Number(top.P95Ms)
            + "ms（Max " + Number(top.MaxMs) + "ms，" + top.Count + " 次）";
    }

    /// <summary>只读一次当前读数（给看门狗用）。</summary>
    internal static Snapshot Peek()
    {
        return Volatile.Read(ref _cur) ?? Empty;
    }

    /// <summary>
    /// 把读数写成一句人话，接在看门狗那句「界面卡顿：…」后面。
    /// 纯函数（只看入参、只读时钟），自检可以直接断言文案。
    /// </summary>
    internal static string Describe(Snapshot snapshot)
    {
        Snapshot s = snapshot ?? Empty;

        string now = string.IsNullOrEmpty(s.Current)
            ? "空闲（没包在任何埋点里）"
            : s.Current + "（已 " + Seconds(s.CurrentTicks) + " 秒）";

        string last = string.IsNullOrEmpty(s.Last)
            ? "无"
            : s.Last + " 用了 " + Number(s.LastMs) + " 毫秒";

        return "当时正在做：" + now + "｜上一段：" + last;
    }

    private static string Seconds(long startTicks)
    {
        if (startTicks <= 0) return "0";
        double sec = (Stopwatch.GetTimestamp() - startTicks) * 1.0 / Stopwatch.Frequency;
        if (sec < 0) sec = 0;
        return Number(sec);
    }

    private static string Number(double value)
    {
        if (value < 0) value = 0;
        // 固定用不变文化：日志是给人看的，但读数不该随系统区域设置变成 "1,5"（那是千位分隔）。
        return value.ToString("0.#", CultureInfo.InvariantCulture);
    }
}
