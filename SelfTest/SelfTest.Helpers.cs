using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DSHGuard;

/// <summary>自检共用的辅助函数（取色、找控件、泵消息、夹具）。</summary>
public static partial class SelfTest
{
    /// <summary>把 "#AARRGGBB" 折算为感知亮度（0-255），用于判定文字颜色是否足够亮或足够深。</summary>
    private static int Luminance(string hex)
    {
        try
        {
            string h = hex.TrimStart('#');
            if (h.Length == 8) h = h.Substring(2);          // 去掉 AA
            int r = Convert.ToInt32(h.Substring(0, 2), 16);
            int g = Convert.ToInt32(h.Substring(2, 2), 16);
            int b = Convert.ToInt32(h.Substring(4, 2), 16);
            return (r * 30 + g * 59 + b * 11) / 100;
        }
        catch { return -1; }
    }

    /// <summary>
    /// Logger **本次运行实际落盘**的那个目录下，是否存在包含指定文本的日志文件 ——
    /// 用于验证「不该落盘的内容确实没落盘」。
    ///
    /// ⚠ 必须读 <see cref="Logger.EffectiveLogDir"/>，不能读 <see cref="GuardPaths.LogDir"/>：
    ///   <c>GuardPaths.LogDir</c> 是**用户真实日志目录**（&lt;exe&gt;\Logs），它的语义务必保持
    ///   "用户看得见的那个目录"（设置页 / 日志页 / 导出诊断都指向它）；
    ///   而 Logger 真正落盘用的是自己的 <c>LogDir</c>，优先级是
    ///   <c>GuardPaths.LogDirExplicit &gt; _runLogDir &gt; GuardPaths.LogDir</c>（Logger.cs:43-51）。
    ///   自检/截图这类夹具会把 <c>_runLogDir</c> 设成隔离目录（App.xaml.cs:136-139，
    ///   <c>%TEMP%\DSHGuard-selftest-logs\&lt;时间戳&gt;\</c>），而 <c>--selftest</c> 全程不调
    ///   <c>GuardPaths.Apply</c> 去指日志目录（只指快照仓库）⇒ <c>LogDirExplicit</c> 为空
    ///   ⇒ 两个属性**指向完全不同的目录**。
    ///   实测后果：自检把日志写在隔离目录，这里却去扫用户的真实 Logs；那份目录当时是空的
    ///   ⇒ 本函数**永远返回 false** ⇒ 「切换日夜模式不写日志」这条断言的 `!AnyLogContains(...)`
    ///   恒为 true，等于没验（实测把违规内容写进隔离目录，旧写法 PASS、新写法 FAIL）。
    /// </summary>
    private static bool AnyLogContains(string needle)
    {
        try
        {
            string dir = Logger.EffectiveLogDir;
            if (!Directory.Exists(dir)) return false;
            foreach (var f in Directory.GetFiles(dir, "*.log"))
                if (Logger.ReadLogFile(f).Contains(needle, StringComparison.Ordinal)) return true;
        }
        catch { }
        return false;
    }

    /// <summary>
    /// 走反射取 <c>MainWindow._appEvents</c>（私有静态事件表）里**所有 <c>EventKind.Mascot</c> 条目的文本**，
    /// 按写入顺序返回。
    ///
    /// 为什么需要它（[299] 那条用例）："彩蛋自身不会再触发彩蛋"这条契约要量的是
    /// **一次事件变动恰好多出一条彩蛋**，而唯一的公开钩子 <c>EventCountForTest()</c> 数的是
    /// **全部**事件 —— 里面混着自检关不掉的外部事件源（`StatusTimer_Tick` 的端口同步那一支，
    /// 用户引擎开着时每秒都可能来一条）⇒ 拿它做"恰好 +2"的断言必然随时序/环境变红。
    /// 只有把 Mascot 种类单独数出来，判据才与外界无关。
    ///
    /// 反射在本文件已有先例（见 <c>PluginMarket.AddMarkdownImage</c> 那条根因断言）。
    /// 只读、不加锁（<c>_appEvents</c> 是 <c>List&lt;(string, EventKind, Color?)&gt;</c>，
    /// 与正式代码同款"先加锁取副本"的读法在这里拿不到锁对象，故直接读一份副本）。
    /// ⚠ 定位失败（字段改名/改类型）**不做兜底**：抛出去由 <see cref="RunCore"/> 的 catch 记 FAIL——
    ///   宁可红，也不能悄悄退化成"恒真"。
    /// </summary>
    private static List<string> MascotEventTextsForTest()
    {
        var field = typeof(MainWindow).GetField("_appEvents",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        if (field?.GetValue(null) is not System.Collections.IEnumerable rows)
            throw new InvalidOperationException("自检夹具失效：取不到 MainWindow._appEvents（私有静态事件表）");

        // _appEvents 是 List<(string, EventKind, Color?)>，但走反射只能拿到非泛型的 IEnumerable
        // ⇒ 先 Cast<object>()（LINQ）才能逐行做模式匹配，否则编译器报"object 没有 GetEnumerator"。
        var result = new List<string>();
        foreach (object row in rows.Cast<object>())
            if (row is ValueTuple<string, MainWindow.EventKind, System.Windows.Media.Color?> t
                && t.Item2 == MainWindow.EventKind.Mascot)
                result.Add(t.Item1);
        return result;
    }

    /// <summary>
    /// 走反射取 <c>MainWindow._appEvents</c>（私有静态事件表）里**某条标记事件之后**的全部 (文本, 等级)，
    /// 按写入顺序返回（不含标记那条本身）。
    ///
    /// 为什么需要它（[668] 那条用例）：<c>AddEvent</c> 每记一条事件都会掷一次 2% 的彩蛋骰，掷中就在
    /// **刚写那条之后**再追一条 <c>EventKind.Mascot</c>（MainWindow.Console.cs:1699）⇒ 取「最后一条事件」
    /// 的断言会偶发变红（本批实测撞上：被拒那条 Warn 后面压着一条彩蛋）。那条契约要的是
    /// 「这次调用**确实**留了一条 Warn」，与它后面压着什么无关。
    ///
    /// ⚠ 为什么按**标记事件**开窗、而不是「最近 N 条」或「最新一条 Warn」：过去 / 别的批次可能写过文案
    ///   相同的 Warn，只看「最近 / 最新」会把它当成这次的 ⇒ 断言恒真。标记事件由用例自己写在那次调用
    ///   **之前**，窗口里因此只可能装本次调用产出的事件（外加引擎 tick 这类与判据无关的噪声条目）。
    /// 反射在本文件已有先例（见 <see cref="MascotEventTextsForTest"/>）；只读、先取副本再筛。
    /// ⚠ 定位失败（字段改名 / 改类型、标记被 100 条上限裁掉）**不做兜底**：抛出去由 <see cref="RunCore"/>
    ///   的 catch 记 FAIL —— 宁可红，也不能悄悄退化成「恒真」。
    /// </summary>
    private static List<(string Text, MainWindow.EventKind Kind)> EventsAfterForTest(string marker)
    {
        var field = typeof(MainWindow).GetField("_appEvents",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        if (field?.GetValue(null) is not System.Collections.IEnumerable rows)
            throw new InvalidOperationException("自检夹具失效：取不到 MainWindow._appEvents（私有静态事件表）");

        // _appEvents 是 List<(string, EventKind, Color?)>，但走反射只能拿到非泛型的 IEnumerable
        // ⇒ 先 Cast<object>()（LINQ）才能逐行做模式匹配，否则编译器报"object 没有 GetEnumerator"。
        var all = new List<(string Text, MainWindow.EventKind Kind)>();
        foreach (object row in rows.Cast<object>())
            if (row is ValueTuple<string, MainWindow.EventKind, System.Windows.Media.Color?> t)
                all.Add((t.Item1, t.Item2));

        // 取**最后一条**标记：同一进程里若重复开窗，只认最近那次。
        int start = all.FindLastIndex(e => e.Text.Contains(marker, StringComparison.Ordinal));
        if (start < 0)
            throw new InvalidOperationException($"自检夹具失效：事件表里找不到标记「{marker}」（观察窗无法界定）");
        return all.GetRange(start + 1, all.Count - start - 1);
    }

    /// <summary>
    /// 收集一棵**逻辑树**上所有指定类型的后代（含根自身）。
    ///
    /// ⚠ 为什么必须走逻辑树而不是视觉树（实测教训）：批量条的进度文案挂在
    /// <c>_batchActionPopup.Child</c> → <c>_batchMenuStack</c> 里，而这个 Popup 的 Child
    /// **从未加入任何 PresentationSource**（见 MainWindow.Images.cs 的 HookPopupTheme 注释：
    /// 它既不在视觉树上、也不在逻辑树上）。自检窗口又是"自建内容树 + Measure/Arrange"，
    /// 那个 Border 自己没被 Measure ⇒ 视觉树walker **一个子节点都走不到**（实测：逻辑子有、视觉子 0）。
    /// LogicalTreeHelper 只看 Parenting 链、不需要布局，所以只有它够得到这一棵。
    /// </summary>
    private static List<T> BatchLogicalDescendantsForTest<T>(DependencyObject? root) where T : DependencyObject
    {
        var found = new List<T>();
        Walk(root);
        return found;

        void Walk(DependencyObject? o)
        {
            if (o is null) return;                 // 显式判空：LogicalTreeHelper.GetChildren 不收可空引用
            try
            {
                if (typeof(T).IsAssignableFrom(o.GetType())) found.Add((T)o);
                foreach (object child in LogicalTreeHelper.GetChildren(o))
                    if (child is DependencyObject d) Walk(d);
            }
            catch { }
        }
    }

    /// <summary>
    /// 走反射取批量弹层的**内容根**（<c>_batchActionPopup.Child</c>，即那个 Border，
    /// 里面装着 <c>_batchMenuStack</c>）。进度文案与进度条都在这棵子树里；
    /// 注意 <c>BuildBatchBar()</c> 返回的是**触发按钮**，不是这棵子树（第一版断言就是在这里量错的）。
    /// </summary>
    private static DependencyObject? BatchMenuRootForTest(MainWindow w)
    {
        try
        {
            var f = typeof(MainWindow).GetField("_batchActionPopup",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return (f?.GetValue(w) as System.Windows.Controls.Primitives.Popup)?.Child as DependencyObject;
        }
        catch { return null; }
    }

    /// <summary>
    /// 走反射取 <c>MainWindow._batchBarProgressText</c>（批量条里那条 i/total 文案）。
    /// 该字段**没有** ForTest 访问器（写它的那位同事没加），而它在修好前是**死字段**
    /// （只声明、没 new、也没挂进 _batchMenuStack）⇒ 只能从这一侧够到它，再交给
    /// <see cref="BatchLogicalDescendantsForTest{T}"/> 去证明"它真在树里"。取不到返回 null（调用方判红）。
    /// </summary>
    private static TextBlock? BatchBarProgressTextForTest(MainWindow w)
    {
        try
        {
            var f = typeof(MainWindow).GetField("_batchBarProgressText",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return f?.GetValue(w) as TextBlock;
        }
        catch { return null; }
    }

    /// <summary>
    /// 1.4 生态趋势的出图样板数据（**内容全固定**，只有"检查时间"是相对当前时刻算的）。
    ///
    /// 为什么要固定：样张的用处是"改动前后能逐像素比对"。榜单内容只要有一个跟着运行时刻走，
    /// 每次出图都会不同，比对就失去意义 —— 这与 §65 那条"给结论摆好再用断言看界面"是同一个思路。
    ///
    /// 但“检查时间”必须**相对 now**：它要过 FreshnessText 算相对时长（「8 分钟前检查」）。
    /// 写成固定字符串的话，样张会随真实时间流逝而变形（一小时后同一份数据变成「1 小时前」），
    /// 逐字节比对当场失效 —— 这正是第一版踩到的坑。取「now 减去一个固定分钟数」，
    /// 于是**同一份样张在任何时刻出图都逐字节一致**（已实测：两次跑 SHA256 相同）。
    ///
    /// 内容刻意覆盖三种行形态（涨星榜带 from→to 副行、下载榜带累计副行、星标榜纯数值），
    /// 且数量少（各 3 行），一眼能核对完整版面而不必滚动。
    /// </summary>
    private static EcosystemTrends BuildTrendsSampleForTest()
    {
        // 固定"8 分钟前"：既落在分钟档（不是"刚刚"那种看不出算没算的边界），
        // 又不会跨到小时档 —— 样张因此稳定显示「数据快照 2026-09-26 · 8 分钟前检查」。
        string checkedAt = DateTime.Now.AddMinutes(-8).ToString("yyyy-MM-dd HH:mm");
        return new EcosystemTrends
        {
            GeneratedAt = "2026-09-28T07:02:02.535Z",
            SnapshotDate = "2026-09-26",
            BaselineDate = "2026-09-18",
            // 涨星榜的统计周期：不填的话"统计周期"那一行在样张与断言里永远是空的
            //（实测踩到：出图时这一行不显示，一度以为是渲染坏了，其实是样板缺字段）。
            RisingWindowDays = 8,
            RisingWindowStart = "2026-09-18",
            RisingWindowEnd = "2026-09-26",
            FetchedAt = checkedAt,
            CheckedAt = checkedAt,
            Source = "dsh.so",
            Rising = new List<TrendRow>
            {
                new() { Rank = 1, Id = "dsh-liang-skin",  Name = "dsh-liang-skin",  FromStars = 171, ToStars = 218, DeltaStars = 47, Stars = 218 },
                new() { Rank = 2, Id = "dsh-model-context-catalog", Name = "dsh-model-context-catalog", FromStars = 15, ToStars = 34, DeltaStars = 19, Stars = 34 },
                new() { Rank = 3, Id = "dsh-plugin-finder", Name = "dsh-plugin-finder", FromStars = 1204, ToStars = 1219, DeltaStars = 15, Stars = 1219 },
            },
            Downloads = new List<TrendRow>
            {
                new() { Rank = 1, Id = "dsh-market",    Name = "dsh-market",    Week = 145277, Total = 1151420, Stars = 4716 },
                new() { Rank = 2, Id = "dsh-codex-ui",  Name = "dsh-codex-ui",  Week = 144755, Total = 115142,  Stars = 98 },
                new() { Rank = 3, Id = "dsh-skills-manager", Name = "dsh-skills-manager", Week = 110000, Total = null, Stars = 42 },
            },
            Stars = new List<TrendRow>
            {
                new() { Rank = 1, Id = "reactive-resume-2", Name = "@reactive-resume/reactive-resume", Stars = 43484 },
                new() { Rank = 2, Id = "dsh-market",        Name = "dsh-market",        Stars = 4716 },
                new() { Rank = 3, Id = "modlens",           Name = "@liustack/modlens",  Stars = 4054 },
            },
            // 第 4 榜（总计下载，主人 2026-09-28 新增）：主值＝累计量 total、副行＝本周量 week，
            // 与「本周下载」榜正好主副互换。★ 第 3 行**故意不给 Total**：源端确实有这种行
            //（只收"有基线初值的包"，实测本周榜 100 条里 35 条累计数为 null）—— 它是"副行占位、
            //  行高一致"那条断言的判据来源：没有这条数据，行高断言就退化成"三行都有副行，当然一样高"。
            // 每行都保留 Week：副行读的是另一个字段，缺累计数不该把这一行也弄没（缺的是主值）。
            Popular = new List<TrendRow>
            {
                new() { Rank = 1, Id = "dsh-better-sidebar", Name = "DSH-better-sidebar", Total = 424981, Week = 52418, Stars = 3836 },
                new() { Rank = 2, Id = "modlens",            Name = "modlens",            Total = 215861, Week = 24356, Stars = 4054 },
                new() { Rank = 3, Id = "dsh-no-baseline",    Name = "dsh-no-baseline",    Total = null,  Week = 1200,  Stars = 42 },
            },
            // 累计数据的截止日（源端顶层 totalsAsOf）：不填的话第 4 榜的"统计截至 …"那一行永远是空的，
            // 界面上少一行却看不出是数据缺了还是渲染坏了（§67 补涨星窗口字段时踩过同一个坑）。
            TotalsAsOf = "2026-09-24",
        };
    }

    private static string CollectText(DependencyObject root)
    {
        var sb = new StringBuilder();
        Walk(root);
        return sb.ToString();

        void Walk(DependencyObject o)
        {
            if (o is TextBlock tb) sb.Append(tb.Text).Append(' ');
            int n = VisualTreeHelper.GetChildrenCount(o);
            for (int i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(o, i));
        }
    }

    /// <summary>
    /// 自检用：找出「属于同一个作者链接」的可点部件（自身带手型，或祖先挂着该主页地址）。
    /// 返回数量 ≥2 表示头像与名字确实合成了一处链接。
    /// </summary>
    private static List<FrameworkElement> FindHandCursorPartsForTest(DependencyObject? root, string url)
    {
        var found = new List<FrameworkElement>();
        Walk(root, null);
        return found;

        void Walk(DependencyObject? o, string? tagFromAncestor)
        {
            if (o == null) return;
            try
            {
                string? tag = tagFromAncestor;
                if (o is FrameworkElement fe)
                {
                    if (fe.Tag is string s && s == url) tag = s;
                    if (tag == url && fe.Cursor == System.Windows.Input.Cursors.Hand) found.Add(fe);
                }
                int n = VisualTreeHelper.GetChildrenCount(o);
                for (int i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(o, i), tag);
            }
            catch { }
        }
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";

    /// <summary>
    /// 走反射调 <c>MainWindow.ShortCount(long)</c>（private static，市场页/插件页/趋势榜共用的数量缩写口径：
    /// ≥1 亿 ⇒ "N.N亿"、≥1 万 ⇒ "N.N万"、≥1000 ⇒ "N.Nk"、否则原数）。
    ///
    /// 为什么自检要用它（§68 那条"总计下载榜主值＝累计量"的断言）：判据必须写成
    /// 「界面上那一格 == ShortCount(424981)」而**不是**写死 "42.5万"。写死字符串的话，
    /// 将来口径微调（比如小数位改成两位）会让这条断言变红，而那时该红的是"口径真的变了"这件事、
    /// 不该由一条"主值读的是不是 total"的断言来报；反过来，若界面哪天改读了 week，
    /// 与 ShortCount(424981) 一比立刻红 —— 判据的鉴别力一点没少。
    ///
    /// ⚠ 取不到（改名/改签名/改成非静态）返回 <c>null</c>，调用方据此走 <c>Skip</c>：
    ///   这是"夹具失效、本条没验"，不是"验过了"，绝不能兜成空串去和一个空串比（那就成了恒真判据）。
    /// 反射在本文件已有先例（见 §67 取 <c>BuildTrendTip</c>、<c>MascotEventTextsForTest</c> 取 <c>_appEvents</c>）。
    /// </summary>
    private static string? ShortCountForTest(long n)
    {
        try
        {
            var m = typeof(MainWindow).GetMethod("ShortCount",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            return m?.Invoke(null, new object[] { n }) as string;
        }
        catch { return null; }
    }

    /// <summary>
    /// 自检用：手写一份快照清单（与 SnapshotManager.Create 的 JsonSerializer 字段名一致）。
    /// 只为构造"当年那台机器的绝对路径 / 被改过的哈希"这类现实数据，全程落在 %TEMP% 夹具里。
    /// </summary>
    private static string SelfTestManifest(string id, string kind, string reason, string hash,
                                           params (string Name, string Target)[] files)
        => JsonSerializer.Serialize(new
        {
            version = 1,
            id,
            kind,
            reason,
            timeUtc = DateTime.UtcNow.ToString("o"),
            app = "DSHGuard",
            appVersion = GuardVersion.Version,
            dshSpec = VersionMemory.Spec,
            dshPin = VersionMemory.Pin,
            files = files.Select(f => new
            {
                name = f.Name,
                size = 0L,
                sha256 = hash,
                target = f.Target,
                skipped = false,
                skipReason = ""
            })
        }, new JsonSerializerOptions { WriteIndented = true });

    /// <summary>自检用：找页面里第一个「手型光标 + 自带缩放变换」的元素（MiniBtn 样式那一类）。</summary>
    private static FrameworkElement? FindMiniBtnForTest(DependencyObject? root)
    {
        try
        {
            if (root is FrameworkElement fe &&
                fe.Cursor == System.Windows.Input.Cursors.Hand &&
                fe.RenderTransform is ScaleTransform)
                return fe;
            int n = VisualTreeHelper.GetChildrenCount(root!);
            for (int i = 0; i < n; i++)
            {
                var hit = FindMiniBtnForTest(VisualTreeHelper.GetChild(root!, i));
                if (hit != null) return hit;
            }
        }
        catch { }
        return null;
    }

    /// <summary>统计视觉树中的 Image 数量（卡片缩略图用）。</summary>
    private static int CountImages(DependencyObject root)
    {
        int n = 0;
        Walk(root);
        return n;

        void Walk(DependencyObject o)
        {
            if (o is Image) n++;
            int c = VisualTreeHelper.GetChildrenCount(o);
            for (int i = 0; i < c; i++) Walk(VisualTreeHelper.GetChild(o, i));
        }
    }

    /// <summary>切到「说明」页并取回页面文本（版本号显示在那一页）。</summary>
    private static string AboutTextsForTest(MainWindow w)
    {
        w.ShowViewForTest("about");
        w.LayoutForTest(960, 640);
        return w.PageTextsForTest("AboutPanel");
    }

    /// <summary>读取一行标签中最后一个标签的文字（分类条的「更多分类 / 收起」开关）。</summary>
    private static string LastChipText(Panel panel)
        => panel.Children.Count == 0 ? "" : CollectText((DependencyObject)panel.Children[^1]).Trim();

    /// <summary>取市场列表首张卡片的插件名，用于验证排序是否生效。</summary>
    private static string FirstCardName(MainWindow w)
    {
        var panel = (Panel)w.FindName("MarketPanel")!;
        if (panel.Children.Count == 0) return "";
        string text = CollectText((DependencyObject)panel.Children[0]).Trim();
        int cut = text.IndexOf(" ↗", StringComparison.Ordinal);
        return cut > 0 ? text.Substring(0, cut) : text.Split(' ').FirstOrDefault() ?? "";
    }

    /// <summary>提取卡片内所有按钮的文案、底色与字色，用于校验配色。</summary>
    private static List<(string Text, string Bg, string Fg)> ButtonInfos(DependencyObject root)
    {
        var list = new List<(string, string, string)>();
        Walk(root);
        return list;

        void Walk(DependencyObject o)
        {
            if (o is Button b)
            {
                string bg = (b.Background as SolidColorBrush)?.Color.ToString() ?? "-";
                string fg = (b.Foreground as SolidColorBrush)?.Color.ToString() ?? "-";
                list.Add((b.Content?.ToString() ?? "", bg, fg));
            }
            int c = VisualTreeHelper.GetChildrenCount(o);
            for (int i = 0; i < c; i++) Walk(VisualTreeHelper.GetChild(o, i));
        }
    }

    /// <summary>
    /// 按 <c>x:Name</c> 取一颗「MiniBtn 样式的 Border 按钮」的文案与底色。
    ///
    /// 为什么要单独开一个：日志页/快照页的「刷新」是 **Border**（走 MiniBtn 样式 + 手型光标），
    /// 不是 <c>Button</c>，于是 <see cref="ButtonInfos"/> 那套（只收 <c>Button</c>）根本收不到它们。
    /// 底色必须原样取控件上的 <c>Background</c>：MiniBtn 的静息底色是**半透明**的 #18FFFFFF，
    /// 与实心绿 #FF34C759 的差别只在 alpha 上，不逐字比就分不出"变色了没有"。
    /// 返回「找没找到」而不是抛：找不到时由调用方判红/判 SKIP，而不是把整段自检炸掉。
    /// </summary>
    private static (bool Found, string Text, string Bg) BorderBtnFacts(MainWindow w, string name)
    {
        try
        {
            if (w.FindName(name) is not Border b) return (false, "", "-");
            string bg = (b.Background as SolidColorBrush)?.Color.ToString() ?? "-";
            return (true, CollectText(b).Trim(), bg);
        }
        catch { return (false, "", "-"); }
    }

    /// <summary>按文案找一颗按钮（版本卡上三颗同名按钮只在**同一张卡**里找，不拿整页找）。</summary>
    private static Button? FindButtonByText(DependencyObject root, string text)
    {
        try
        {
            if (root is Button b && (b.Content?.ToString() ?? "") == text) return b;
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var hit = FindButtonByText(VisualTreeHelper.GetChild(root, i), text);
                if (hit != null) return hit;
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 把一个元素的矩形换算到**另一个参照元素**的坐标系里（都用 TranslatePoint，不用手算祖先偏移）。
    /// 判"三颗按钮互不叠压"必须换算到同一参照物：各取自己的父级坐标，两两之间根本没有可比性。
    /// 取不到时返回空矩形（全 0）——调用方据此判 SKIP，而不是拿 0 当"没相交"假过。
    /// </summary>
    private static Rect RectInForTest(FrameworkElement el, FrameworkElement reference)
    {
        try
        {
            var p = el.TranslatePoint(new Point(0, 0), reference);
            return new Rect(p.X, p.Y, el.ActualWidth, el.ActualHeight);
        }
        catch { return new Rect(0, 0, 0, 0); }
    }

    /// <summary>
    /// 两个矩形是否真的**叠压**：两轴的交集都 &gt; 0.5px 才算。
    ///
    /// 为什么不能只看横坐标：版本卡上三颗按钮装在 WrapPanel 里，卡片窄到摆不下时溢出的那颗会**整颗换行**，
    /// 那时横坐标必然重叠、纵坐标却分得开 —— 只比横向会把正常的换行误判成"叠压"。
    /// 0.5px 的死区是给布局取整用的：真叠压至少压住小半个字，不会只差零点几像素。
    /// </summary>
    private static bool RectsOverlapForTest(Rect a, Rect b)
    {
        double ox = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        double oy = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        return ox > 0.5 && oy > 0.5;
    }

    /// <summary>在线程池上执行异步任务并同步等待结果，避免 UI 线程自锁。</summary>
    private static T RunOffUi<T>(Func<Task<T>> work) => Task.Run(work).GetAwaiter().GetResult();

    /// <summary>抽干 Dispatcher 队列直至条件成立或达到 timeoutMs 上限。</summary>
    private static void PumpUntil(Func<bool> done, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            PumpStep(120);
            if (done()) return;
            System.Threading.Thread.Sleep(15);
        }
    }

    /// <summary>推入一个派发帧，并以 Normal 优先级的定时器强制结束该帧：Dispatcher.Invoke(…, Background) 会被更高优先级的自排队任务饿死。</summary>
    private static void PumpStep(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(ms), DispatcherPriority.Normal,
            (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    /// <summary>
    /// 等插件页的异步数据落定后再取数（自检用）：插件扫描、loader id 读取与「查新版本」都是异步的
    /// （后者还要联网取 git 源的最新提交），"数据已变、紧随其后的重渲染还没跑完"这一小段窗口里
    /// 取到的数是半截值 —— 断言便偶发变红，与功能无关。
    /// 做法：抽 Dispatcher 消息，每轮重新取一次数，直到「连续两轮取到的数完全一致」且
    /// 「摘要行不再写着正在扫描插件 / 正在查新版本」，最多等 timeoutMs（默认 2 秒）。
    /// 只用于等落定；断言判据本身不放宽。
    /// </summary>
    private static void SettlePluginDataForTest(MainWindow w, Func<int[]> read, int timeoutMs = 2000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int[] last = read();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            PumpStep(50);                                   // 抽一帧消息：让异步续体与随后的重渲染跑完
            System.Threading.Thread.Sleep(50);

            // 「查新版本」还在跑就先不判：检查结果一到，UpdatableCount 与「一键更新」按钮的显隐
            // 会**同一次重渲染里**一起变（见 RenderPlugins）；在此之前读到的按钮状态是上一帧的，
            // 断言必然偶发变红。这一步只影响"等多久"，不放宽任何判据（超时上限仍是 timeoutMs）。
            if (w.UpdatesCheckingForTest()) { last = read(); continue; }

            int[] now = read();
            if (now.SequenceEqual(last) && !PluginPageBusyForTest(w)) return;
            last = now;
        }
    }

    /// <summary>插件页摘要行是否还停在异步中（「正在扫描插件…」/「正在查新版本…」）。</summary>
    private static bool PluginPageBusyForTest(MainWindow w)
    {
        try
        {
            string t = (w.FindName("PluginsSummaryText") as TextBlock)?.Text ?? "";
            return t.Contains("正在扫描插件") || t.Contains("正在查新版本");
        }
        catch { return false; }
    }

    /// <summary>
    /// 版本页**某一张卡**上的文字与按钮文案。
    ///
    /// 为什么必须按卡片给范围：版本页上有三张卡（① 运行中的 DSH ② 版本记忆 ③ 守护壳版本），
    /// "守护壳版本"是第一张卡上**也有一颗「检查更新」**的同名按钮 —— 拿整页去断言会把它数成两颗、
    /// 还会把 DSH 的版本号当成守护壳的远端版本号（本批自检第一版就是这么误判的）。
    /// 卡片对象由 <see cref="MainWindow.VersionCardsForTest"/> 给出（顺序＝页上从上到下）。
    /// </summary>
    private static (string Texts, List<string> Buttons) CardFacts(DependencyObject? card)
    {
        try
        {
            if (card == null) return ("", new List<string>());
            return (TreeViewsText(card), ButtonInfos(card).Select(b => b.Text).ToList());
        }
        catch { return ("", new List<string>()); }
    }

    /// <summary>按视觉树收一份"文字 + 按钮文案"的流水（与 <c>PageTextsForTest</c> 同一走法，锚点由调用方给）。</summary>
    private static string TreeViewsText(DependencyObject root)
    {
        var sb = new System.Text.StringBuilder();
        Walk(root);
        return sb.ToString();

        void Walk(DependencyObject o)
        {
            if (o is TextBlock tb) sb.Append(tb.Text).Append('|');
            if (o is TextBox bx) sb.Append(bx.Text).Append('|');
            if (o is Button b) sb.Append(b.Content?.ToString() ?? "").Append('|');
            int n = VisualTreeHelper.GetChildrenCount(o);
            for (int i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(o, i));
        }
    }

    /// <summary>
    /// 「冻结画刷」护栏（1.3.7 修的那个"点「立即更新」点不开"）：
    /// 自查一支**确定冻结**的实心刷子 —— 先按旧写法直接改它，确认真会抛（这就是现场那行异常）；
    /// 再走修复后的兜底：冻结的要能被换成可写刷子，非实心刷返回空（跳过动效，而不是抛出去）。
    ///
    /// 判据不看具体色值（本机系统按键色是 #FFF0F0F0、现场那台是 #FFDDDDDD，随系统配色变），只看 IsFrozen。
    /// </summary>
    private static bool FreezeBrushGuardFacts()
    {
        try
        {
            var frozen = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD));
            frozen.Freeze();
            if (!frozen.IsFrozen) return false;

            // ① 旧写法确实会抛（否则这条护栏就没有意义 —— 也就说明 bug 的根因判断错了）
            bool oldThrew = false;
            try { frozen.Color = Colors.Red; }
            catch (InvalidOperationException) { oldThrew = true; }
            if (!oldThrew || frozen.Color != Color.FromRgb(0xDD, 0xDD, 0xDD)) return false;

            // ② 兜底：冻结 ⇒ 换一支可写刷子；非实心刷 ⇒ 返回空（不抛）
            var btn = new Button { Background = frozen };
            var fixedUp = GuardUpdateProgressWindow.SafeHoverBrush(btn);
            if (fixedUp == null || fixedUp.IsFrozen) return false;
            try { fixedUp.Color = Colors.Red; } catch { return false; }   // 换成之后必须真能改
            if (!GuardUpdateProgressWindow.BrushAnimatableForTest(btn)) return false;

            var gradient = new Button { Background = new LinearGradientBrush(Colors.Black, Colors.White, 0) };
            if (GuardUpdateProgressWindow.SafeHoverBrush(gradient) != null) return false;

            // ③ 顺手确认"本就可写"的那支**原样返回**（不无谓地换新刷子，避免动效互相踩）
            var plain = new Button { Background = new SolidColorBrush(Colors.Green) };
            if (!ReferenceEquals(GuardUpdateProgressWindow.SafeHoverBrush(plain), plain.Background)) return false;

            // ④ 解冻只是把"只读"变成"可写"，颜色一字不改（观感不变 ⇒ fixedUp 改之前应仍是原色）
            //    注：上面已把 fixedUp 改成红色以证明可写，这里改用另一颗按钮看"换刷子不改色"。
            var btn2 = new Button { Background = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)) };
            ((SolidColorBrush)btn2.Background).Freeze();
            return GuardUpdateProgressWindow.SafeHoverBrush(btn2)?.Color == Color.FromRgb(0xDD, 0xDD, 0xDD);
        }
        catch { return false; }
    }
}
