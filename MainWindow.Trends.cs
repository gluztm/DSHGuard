// 「生态趋势」一屏：数据抓取入口 + 整屏渲染。
//
// 为什么单独一个 partial 文件：这一屏和插件页/快照页一样是"纯代码生成"的界面
// （XAML 里只有一个空容器 TrendsPanel），把它隔离出来，出事时一眼能定位，
// 也不会把 MainWindow.xaml.cs 撑得更大。
//
// 铁律（本文件必须守住）：
//   1) 绝不进启动路径。抓取只在用户真正切到这一屏时才发生（RefreshTrendsAsync），
//      外部站点坏了、网络断了，都只能让这一屏难看，绝不能拖慢或拖垮开壳。
//   2) 界面上绝不出现网址。榜单行只显示名称与数字，跳转一律走 OpenExternalLink 闸门
//      （白名单 + 诊断留痕），URL 只存在于内存与 ToolTip 文案里。
//   3) 取不到数据时不许写"暂无数据"。那句话会被读成"今天没有内容"，
//      必须说清是"取不到（可能离线）"；陈旧数据必须自报家门"下面是上次取到的数据"。

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DSHGuard;

public partial class MainWindow : Window
{
    /// <summary>当前已拿到手的生态数据；null 表示本次还没取到过（不是"没有内容"）。</summary>
    private EcosystemTrends? _trends;

    /// <summary>
    /// 抓取闸门。重复进页/连点刷新时用它挡住并发抓取——一次抓取要打外部站点，
    /// 并发不仅浪费流量，还会让两个请求各自回填 _trends，界面来回跳。
    /// </summary>
    private bool _trendsLoading;

    /// <summary>当前显示哪一个榜，默认"涨星最快"（最能回答"谁在起势"）。</summary>
    private TrendsBoard _trendsBoard = TrendsBoard.Rising;

    /// <summary>
    /// 真正上屏的榜单行数。自检为什么返回它而不是去视觉树里数 TextBlock：
    /// 视觉树里还有标题、按钮、状态行等文字，数出来必然多；这个计数只在
    /// 逐行 Add 的那一刻自增，等于"用户真的能看到几行"。
    /// </summary>
    private int _trendsRowsOnScreen;

    /// <summary>当前状态文案（加载中/取不到/过期）。空串＝数据新鲜、没有话要说。</summary>
    private string _trendsStatus = "";

    /// <summary>
    /// 数据是否已被"钉住"（自检/出图灌了样板数据之后置起）。
    ///
    /// 为什么必须有这个闸门（真实竞态，出图时实测踩到）：`ShowViewForTest("trends")` 会走
    /// `ShowView` 的换页动作 ⇒ `_ = RefreshTrendsAsync()` 真的发一次网络请求。若出图脚本随后灌样板，
    /// 那一发请求的回包会**晚于**灌样到点、把样板整个覆盖掉 ⇒ 样张里显示的是实时数据，
    /// **每次出图都不一样**，"改动前后逐像素比对"这个用途当场作废（实测 `--trends-sample`
    /// 与不带它的样张字节完全相同，就是这么来的）。
    /// 置起后 `RefreshTrendsAsync` 直接返回，联网路径不再回填 —— 只影响自检与出图，
    /// 正常使用永远不会走到这里（没有任何生产代码调 SetTrendsSampleForTest）。
    /// </summary>
    private bool _trendsPinned;

    /// <summary>
    /// 本次渲染时"统计周期"那一行的文字（不适用/取不到时为空串）。
    /// 存成字段而不是让自检去视觉树里捞：那一行在别的榜上是 Collapsed（不参与布局、
    /// 也量不出文字），而"这一刻该显示什么"由渲染时的判断唯一决定，存下来最稳。
    /// </summary>
    private string _trendsPeriodText = "";

    /// <summary>
    /// 本次渲染出的榜单卡片（TrendsPanel 第 5 行那个 Border）。只给自检量"卡片下方还剩多少空白"用：
    /// 它每次渲染都会被换成一个新对象，而自检是"先渲染、再问数"的两步，
    /// 只有把当时那颗记下来，量到的才是屏幕上真正的那一颗。
    /// </summary>
    private Border? _trendsCardForTest;

    /// <summary>
    /// 四个榜。字符串映射（"rising"/"downloads"/"stars"/"popular"）由本文件负责，自检只认这四种小写串。
    ///
    /// Popular（总计下载，主人 2026-09-28 要求新增）刻意**加在末尾**：既有三个的枚举序数不能动，
    /// 因为自检/出图脚本里可能有按序数写死的地方，插在中间等于无声地换掉那些榜的身份。
    /// </summary>
    internal enum TrendsBoard { Rising, Downloads, Stars, Popular }

    /// <summary>
    /// 「生态趋势」整屏重绘。每次进来都从 TrendsPanel 清空重建：
    /// 这一屏内容全是代码生成的，重建比增量更新更不容易留下残影，
    /// 也顺手解决了"重复进页越叠越多"的老毛病。
    ///
    /// 线程：本方法只在 UI 线程/派发器上被调用（进页、切榜、抓取收尾）。
    /// 但仍然整段兜住异常——渲染是"锦上添花"，绝不能因为一个格式化异常
    /// 把整扇窗的事件循环掀翻。
    /// </summary>
    internal void RenderTrends()
    {
        try
        {
            // 先清空：任何一次重绘都以空面板为起点，不让上一版残留。
            // （下面收尾的 SwapThemed 内部也会 Clear 一次；这里显式再写一遍，是让"不叠加"
            //   这个保证就近可见，不必让读者去翻 SwapThemed 的实现才能确认。）
            TrendsPanel.Children.Clear();

            // 本次要上屏的顶层元素先攒在这里，最后一次性交给 SwapThemed 挂载。
            // 为什么必须走它：本文件所有颜色都是深色主题的字面量，而日间模式下
            // ThemeManager 是靠"逐节点重映射颜色"换肤的（#F5F5F7→#1C1C1E 等）。
            // 若直接 Add 上屏，日间模式下会先亮出一帧"近白字配浅底"再被补刷纠正——
            // 快照页与市场页都踩过这个中间态，本页一律沿用它们的做法。
            //
            // 每颗零件上屏前都要显式 Grid.SetRow 指定行号：TrendsPanel 现在是带 6 行定义的 Grid
            // （见 MainWindow.xaml），行号不设就全挤在第 0 行、互相叠着。
            // 注意 SwapThemed 只做"着色 + 挂载"，行号挂在元素自身的附加属性上，它清 Children 时不会丢。
            var parts = new List<UIElement>();

            // ── 1. 标题（与其它页同款：18px + SemiBold） ──
            var title = SimpleText("生态趋势", 18, Color.FromRgb(0xF5, 0xF5, 0xF7), true);
            Grid.SetRow(title, 0);
            parts.Add(title);

            // ── 2. 副标题：**整段已删除**（主人 2026-09-28 要求删掉这行副标题） ──
            // 原来这里是「DSH 生态里谁在起势、谁被用得最多」那行 11px 灰字。删它的理由：
            // 标题「生态趋势」四个字已经说清这一屏是干什么的，副标题只是把同一句话再说一遍，
            // 白占一行高度；而这一屏的高度是稀缺资源 —— 省下来的那二十来像素直接归了榜单卡片
            // （TrendsPanel 第 5 行是 Height="*"，上面任何一行变矮，卡片就长高一截）。
            // 数据来源那句话也没有丢：它有正式的位置，就是右下角 TrendsCornerHost 上的灰字行（本方法第 6 段）。
            //
            // 为什么 XAML 一个字都不用改：TrendsPanel 第 1 行是 Height="Auto"，
            // 这一行没有任何子元素时它自然量成 0 高、不占位。行定义**故意留着**而不是删掉 ——
            // 删了后面所有行号要整体上移，而"第 0/1/2/3/4/5 行"这套编号在注释、
            // Grid.SetRow 与既有自检里到处都是，改行号是纯风险、零收益。

            // ── 3. 四榜切换 ──
            // 用横向 StackPanel + MiniButton：本程序所有小按钮都走自绘模板（RoundBtn），
            // 选中态靠底色区分——重绘时按钮整体重建，所以不需要维护 ToggleButton 的选中状态机。
            var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            tabs.Children.Add(TrendsTabButton("涨星最快", TrendsBoard.Rising, "rising"));
            tabs.Children.Add(TrendsTabButton("本周下载", TrendsBoard.Downloads, "downloads"));
            tabs.Children.Add(TrendsTabButton("星标榜", TrendsBoard.Stars, "stars"));
            tabs.Children.Add(TrendsTabButton("总计下载", TrendsBoard.Popular, "popular"));
            Grid.SetRow(tabs, 2);
            parts.Add(tabs);

            // ── 3.5 统计周期（按当前榜决定文案） ──
            // 为什么要这一行：涨星榜的"净增星数"与总计下载榜的"累计下载量"都是**区间量**，
            // 不写清是哪一段区间，"+1.2k" 或 "145.3k" 就没法解释（一周还是一个月，量级差一个数量级）。
            // 各榜口径（唯一入口见 PeriodTextForBoard）：
            //   · Rising   → 服务层纯函数 WindowText(...) 给的起止区间；拿不到起止返回空串。
            //   · Popular  → 累计数据的截止日 TotalsAsOf。**拿不到就返回空串，绝不编一个日期** ——
            //                编出来的日期比不显示更坏：用户会拿它去对比别处的数字，对不上还以为是我们的数错了。
            //   · 另两榜   → 空串（本周下载的周期写在榜名里；星标榜是时点量，没有周期可言）。
            // 节点**始终建、始终占第 3 行**，靠 Visibility 控制显隐（而不是"没话就不 Add"）：
            // 这样"什么时候该显示"只有一个开关，自检要观察这一刻的状态也有稳定的落点。
            // 这里用 Collapsed 是**对的、与副行的 Hidden 不矛盾**：这是"整行有没有话要说"，
            // 没话时本该让 Auto 行高度塌成 0、把高度让给卡片；而副行是"同一榜内行与行之间要齐平"，
            // 必须在行**内部**保住高度（见 BuildTrendRow 里那段 Hidden 的说明）。
            _trendsPeriodText = PeriodTextForBoard();
            var period = SimpleText(_trendsPeriodText, 10.5, Color.FromRgb(0x8E, 0x8E, 0x93));
            period.TextWrapping = TextWrapping.Wrap;
            period.Margin = new Thickness(0, 0, 0, 8);
            period.Visibility = _trendsPeriodText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            Grid.SetRow(period, 3);
            parts.Add(period);

            // 当前榜的数据。Rising 等列表来自反序列化，样本/异常路径下可能为 null，
            // 统一在入口处兜成空表，后面所有 Count/遍历都不必再判空。
            List<TrendRow> rows = CurrentRows();
            bool hasAnyRows = HasAnyRows();

            // ── 4. 状态行（只在有话要说的时候出现） ──
            _trendsStatus = BuildTrendsStatusText(hasAnyRows);
            string statusLine = _trendsStatus;
            // 过期数据必须紧跟一个"这是什么时候的"，否则用户会把旧数字当成此刻的行情。
            if (_trends?.Stale == true && statusLine.Length > 0)
                statusLine = statusLine + " · " + EcosystemTrendsService.FreshnessText(_trends.SnapshotDate, _trends.CheckedAt, DateTime.Now);

            if (statusLine.Length > 0)
            {
                Color tone = _trendsLoading ? Color.FromRgb(0x8E, 0x8E, 0x93)
                           : hasAnyRows ? Color.FromRgb(0xFF, 0x9F, 0x0A)   // 有数据但过期/出错：橙色提醒
                           : Color.FromRgb(0x8E, 0x8E, 0x93);              // 完全取不到：中性灰，不吓人
                var st = SimpleText(statusLine, 11.5, tone);
                st.Margin = new Thickness(0, 0, 0, 8);
                Grid.SetRow(st, 4);
                parts.Add(st);
            }

            // ── 5. 榜单列表 ──
            // 卡片外观与全程序一致（10 圆角 + #12FFFFFF 底 + 14/12 内边距）。
            // 这里没调 MakeCard/Body 助手：它们不是类成员，而是 MainWindow.Tools.cs 里
            // 某个方法内部的局部函数（外部方法看不见），所以按同一份数值就地复刻。
            var card = new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
                Padding = new Thickness(14, 12, 14, 12)
            };
            var listHost = new StackPanel();

            // 层级必须是 card → scroller → listHost（外面是卡片、里面才滚），不能反过来：
            // 反过来的话是"滚动区里装一张卡片"，卡片高度＝内容高度，卡片底边就到不了行底，
            // 行数少时下面仍是一片空白 —— 正是主人这次要消掉的那片。
            // 现在卡片被放进第 5 行（Height="*"，默认 Stretch），卡片背景就铺满整行剩余高度；
            // 行数多到装不下时，内部滚动条出现，卡片本身不越界。
            var scroller = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                // 这里**不再**手算 MaxHeight（原来是 Math.Max(220, ActualHeight - 360) 的 hack）：
                // 那个上限假设了标题/切换/状态行加起来恒定 360px，任何一行多行折行、
                // 统计周期行出现，假设立刻失真；而它真正想解决的问题（StackPanel 给无限高度 ⇒
                // 滚动区一路长高、被外层裁掉）已经由 Grid 的 * 行从根上解决：可用高度有限且精确。
                Content = listHost
            };
            card.Child = scroller;
            Grid.SetRow(card, 5);
            parts.Add(card);
            _trendsCardForTest = card;   // 留给自检量"卡片下方还剩多少空白"

            // _trendsRowsOnScreen 是自检的唯一依据，必须在逐行渲染时同步累加。
            _trendsRowsOnScreen = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                listHost.Children.Add(BuildTrendRow(rows[i], i));
                _trendsRowsOnScreen++;   // 真的排进了列表才计数（这个数＝用户看得到的行数）
            }

            // ── 6. 数据来源：压在卡片下方的灰字行（主人 2026-09-28 定稿）──
            // 不随列表滚动：它挂在 TrendsView 第 1 行（Auto）的独立 TrendsCornerHost 上，
            // 而榜单卡片在 TrendsView 第 0 行（*）里 —— 两行互不相交，底部永不重叠。
            // （旧实现是"钉在右下角 + 手算 MaxHeight 上限"来避让，现已由行划分从根上解决。）
            // 灰字用 #8E8E93（映射表登记过，日间自动转 #6B6B70）；链接仍是手型可点，
            // 但颜色与正文同灰——"知道来源在哪"比"招人去点"重要，灰色更不抢视线。
            // 仍走 SwapThemed：确保日间模式下不闪深色帧、颜色正确重映射。
            var foot = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            var src = new TextBlock
            {
                Text = EcosystemTrendsService.AttributionText,
                FontSize = 10.5,
                TextWrapping = TextWrapping.Wrap,
                Cursor = Cursors.Hand,                       // 手型：告诉用户这里可以点
                Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93))
            };
            src.MouseLeftButtonDown += (_, _) => OpenExternalLink("https://www.dsh.so/", "生态趋势来源");
            foot.Children.Add(src);

            foot.Children.Add(new TextBlock
            {
                Text = " · ",
                FontSize = 10.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0x48, 0x48, 0x4A))
            });

            foot.Children.Add(new TextBlock
            {
                Text = EcosystemTrendsService.FreshnessText(_trends?.SnapshotDate, _trends?.CheckedAt, DateTime.Now),
                FontSize = 10.5,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0x48, 0x48, 0x4A))
            });

            // 角落宿主同样先过 SwapThemed 的着色路径：单独 ThemeManager.Apply +
            // 赋值回宿主，语义与 SwapThemed 完全一致（着色在挂载之前）。
            ThemeManager.Apply(foot, ThemeManager.IsDark);
            TrendsCornerHost.Children.Clear();
            TrendsCornerHost.Children.Add(foot);

            // ── 收尾：一次性挂载 ──
            // 走 SwapThemed 而不是逐个 Add：它先按当前主题给每个节点着色、再挂上屏，
            // 日间模式下才不会先闪一帧深色（见方法上方注释）。
            SwapThemed(TrendsPanel, parts);
        }
        catch (Exception ex) { Logger.LogError("RenderTrends", ex); }
    }

    /// <summary>
    /// 造一颗榜切换按钮。选中态用品牌蓝实底、未选中用 #18FFFFFF 的半透明底——
    /// 与程序里其它"分段控件"观感一致，且两种底色在深浅主题下都能分辨。
    /// </summary>
    private Button TrendsTabButton(string text, TrendsBoard board, string key)
    {
        bool on = _trendsBoard == board;
        var btn = MiniButton(text, on ? "#007AFF" : "#18FFFFFF", 13, 5, on ? "#FFFFFF" : "#F5F5F7", 11.5);
        btn.Margin = new Thickness(0, 0, 8, 0);   // 四颗之间 8px 间距（最后一颗多出的右间距无伤大雅）
        // 自绘模板包住了 ContentPresenter，点击仍然落在 Button 自己身上，直接走 Click。
        btn.Click += (_, _) => ShowTrendsTab(key);
        return btn;
    }

    /// <summary>当前榜对应的行表；空引用统一兜成空表，调用方不必再判空。</summary>
    private List<TrendRow> CurrentRows()
    {
        if (_trends == null) return new List<TrendRow>();
        List<TrendRow>? src = _trendsBoard switch
        {
            TrendsBoard.Downloads => _trends.Downloads,
            TrendsBoard.Stars => _trends.Stars,
            TrendsBoard.Popular => _trends.Popular,
            _ => _trends.Rising
        };
        return src ?? new List<TrendRow>();
    }

    /// <summary>
    /// 四个榜里是否任意一个有行。用它（而不是只看当前榜）判断"到底取没取到数据"：
    /// 某一榜为空可能只是该口径没有内容，四榜全空才说明这次真的没拿到东西。
    ///
    /// Popular 必须算进来（主人 2026-09-28 新增第 4 榜时的要害）：漏了它就会出现
    /// "只有总计下载榜有数据"这种真实情形被误判成"完全取不到" —— 界面一边列出满屏行，
    /// 一边打出一句"暂时取不到（可能离线）"，自相矛盾，自检也会按错误口径判红。
    /// </summary>
    private bool HasAnyRows()
    {
        if (_trends == null) return false;
        return (_trends.Rising?.Count ?? 0) > 0
            || (_trends.Downloads?.Count ?? 0) > 0
            || (_trends.Stars?.Count ?? 0) > 0
            || (_trends.Popular?.Count ?? 0) > 0;
    }

    /// <summary>
    /// 「统计周期」那一行该显示什么 —— **按当前榜决定的唯一入口**。
    ///
    /// 为什么抽成方法而不是留在 RenderTrends 里写三元表达式：加上第 4 榜之后，
    /// 分支从"涨星榜/其余"变成"涨星榜/总计下载榜/其余两榜"三种口径，
    /// 写在渲染流程里会把"何时显示哪句话"埋进一屏几百行的布局代码中间，
    /// 以后再加榜必然漏改；收成一个纯查询，渲染处只剩"取文案、建节点"两件事。
    ///
    /// 三种口径：
    ///   · Rising  → 服务层纯函数 WindowText(...)：拿不到起止区间就返回空串（不编区间）。
    ///   · Popular → 累计数据的截止日 TotalsAsOf，拼成"统计截至 2026-09-24"。
    ///               拿不到（空串）时**返回空串**，绝不填一个猜测的日期 —— 见调用处注释。
    ///   · 其余    → 空串：本周下载的周期写在榜名里；星标榜是时点量，没有周期。
    /// </summary>
    private string PeriodTextForBoard() => _trendsBoard switch
    {
        TrendsBoard.Rising => EcosystemTrendsService.WindowText(_trends?.RisingWindowDays ?? 0,
                                                                _trends?.RisingWindowStart,
                                                                _trends?.RisingWindowEnd),
        TrendsBoard.Popular => TotalsAsOfText(),
        _ => ""
    };

    /// <summary>
    /// 总计下载榜的周期文案："统计截至 {TotalsAsOf}"。截止日读不到（null/空白）时返回空串。
    /// 单独一个方法只为把"空串就是不显示"这条约定写在最显眼处：调用方拿到空串会走 Collapsed，
    /// 界面上就是干干净净没有那一行，而不是"统计截至 "这种半截话。
    /// </summary>
    private string TotalsAsOfText()
    {
        string asOf = (_trends?.TotalsAsOf ?? "").Trim();
        return asOf.Length > 0 ? $"统计截至 {asOf}" : "";
    }

    /// <summary>
    /// 星标榜专用：一律用 k 作单位（主人 2026-09-28 要求"统一单位为 k"）。
    ///
    /// 为什么另写一个而不复用 ShortCount：ShortCount（MainWindow.Market.cs）是插件页/市场页共用的
    /// "万/k 混用"口径 —— 43484 出来是"4.3万"、4716 出来是"4.7k"，单独看都合理，
    /// 但同一个榜里两种单位并排，眼睛要先换算一遍才能比大小，量级差反而被单位切换掩盖了。
    /// 而**改 ShortCount 会牵连别的页面**（市场卡片、插件页统计都吃它），所以在这里另起一个。
    ///
    /// 小于 1000 也照样带 k（152 ⇒ "0.2k"）：主人要的是"统一单位"，
    /// 若在小数处退回整数（"152"）就又是两种写法混排，等于没统一。
    /// 精度损失由数值列的悬停提示补齐 —— 提示里给的是带千分位的原数（"★ 43,484"），信息一个都没丢。
    /// </summary>
    private static string StarK(long n) => $"{n / 1000.0:0.#}k";

    /// <summary>
    /// 状态文案。三种情况的措辞是刻意选过的：
    ///   · 加载中 → "正在获取生态数据…"
    ///   · 取不到 → "暂时取不到（可能离线）"。绝不用"暂无数据"——那会被读成
    ///     "今天生态里没有内容"，与事实（我们的请求没成功）不是一回事；
    ///     若服务层给了 Error，就直接把真实原因摆出来，比笼统措辞有用。
    ///   · 过期   → 必须自报"下面是上次取到的数据"，把旧数字和此刻行情区分开。
    /// 数据新鲜且齐全时返回空串，界面不加任何噪音。
    /// </summary>
    private string BuildTrendsStatusText(bool hasAnyRows)
    {
        if (_trendsLoading) return "正在获取生态数据…";
        if (_trends == null) return "暂时取不到（可能离线）";

        string err = (_trends.Error ?? "").Trim();

        if (!hasAnyRows)
        {
            // 四榜全空：没有任何行可看，先给"取不到"的实话。
            string miss = err.Length > 0 ? err : "暂时取不到（可能离线）";
            // 陈旧帧 + 空表：仍然要点明这是上次那份（本次没刷新成功），
            // 免得用户以为我们刚取回来一个"今天没有内容"的结果。
            return _trends.Stale ? "下面是上次取到的数据（本次未能刷新） · " + miss : miss;
        }

        var bits = new List<string>();
        if (_trends.Stale) bits.Add("下面是上次取到的数据");
        if (err.Length > 0) bits.Add(err);
        return string.Join(" · ", bits);
    }

    /// <summary>
    /// 渲染一行榜单：序号 ｜ 名称 ｜ 数值（右对齐）。三列用 Grid 而不是拼字符串，
    /// 数字才能按位对齐、一眼看出量级差。
    /// </summary>
    private Grid BuildTrendRow(TrendRow row, int index)
    {
        var g = new Grid { Margin = new Thickness(0, 5, 0, 5) };   // 行间留白，别挤成一片
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 序号：补零两位，等宽看着整齐（01…09 不会因为位数变化左右跳动）。
        var rank = new TextBlock
        {
            Text = $"{index + 1:00}",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0x48, 0x48, 0x4A))
        };
        Grid.SetColumn(rank, 0);
        g.Children.Add(rank);

        // 名称：有页面地址才可点。URL 只进内存与 ToolTip，界面上一个字符都不出现（项目铁律）。
        string name = string.IsNullOrWhiteSpace(row.Name) ? (row.Id ?? "") : row.Name!;
        var nameTb = new TextBlock
        {
            Text = name,
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF7))
        };
        if (!string.IsNullOrWhiteSpace(row.PageUrl))
        {
            nameTb.Cursor = Cursors.Hand;
            string url = row.PageUrl!;   // 先落成局部变量，避免闭包在重绘后读到被换掉的 row

            // 悬停提示（主人 2026-09-28 要求）：优先显示这个插件的中文简介。
            // 简介来自社区目录（MarketDescriptionFor 是纯查询，不联网、不阻塞）；
            // 目录还没准备好/这条没收录/没写简介时都返回空串，那就退回一句轻提示 ——
            // 措辞跟着**点击行为**走：点击已经改成进本程序的插件市场，所以这里不能再写"在 dsh.so 查看"。
            string desc = MarketDescriptionFor(name);
            nameTb.ToolTip = desc.Length > 0 ? BuildTrendTip(desc) : "在插件市场里查看";

            nameTb.MouseLeftButtonDown += (_, _) =>
            {
                // 主人 2026-09-28 要求：点名称进**本程序的插件市场**，而不是跳外部站点。
                // 目录里确实没有这一条时（实测 reactive-resume-2 / dsh-plugin-finder 就不在收录里），
                // 退回打开它的 dsh.so 条目页 —— 有去处，好过点了没反应。
                if (!ShowPluginInMarket(name) && url.Length > 0)
                    OpenExternalLink(url, "生态趋势榜单（社区目录未收录，退回 dsh.so）");
            };
        }
        Grid.SetColumn(nameTb, 1);
        g.Children.Add(nameTb);

        // 数值列：主值一行 + **恒存在的**副行，整体右对齐。
        var valCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        string mainText;
        Color mainColor;
        // 副行类型是 string 而不是 string?（主人 2026-09-28 第 4 条要求"副行恒存在"）：
        // 既然每一榜都必定给出一条副行文案（可能为空串），类型上就没有"没有副行"这个状态，
        // 后面也就不需要一处 `!= null` 判空 —— 少一个分支就少一个将来会写漏的地方。
        string subText = "";
        // 数值列的悬停提示：给**精确原数**。为什么需要它：主值为了对齐做了缩写
        // （星标榜统一成 k 之后 "43.5k" 丢掉了 4 位数字），提示里补回带千分位的原数，
        // 信息一个都不丢，"统一单位"与"不丢精度"就不必二选一。
        string tipText;

        switch (_trendsBoard)
        {
            case TrendsBoard.Downloads:
                // 主值＝本周下载量；副行＝累计下载量（源端没有基线的包累计数就是 null，
                // 那种情况给空串、由副行自己用 Hidden 占住高度，见下方注释）。
                mainText = ShortCount(row.Week ?? 0);
                mainColor = Color.FromRgb(0xF5, 0xF5, 0xF7);
                subText = row.Total.HasValue ? $"累计 {ShortCount(row.Total.Value)}" : "";
                tipText = $"{row.Week ?? 0:N0} 次（本周）"
                        + (row.Total.HasValue ? $" · 累计 {row.Total.Value:N0} 次下载" : "");
                break;

            case TrendsBoard.Popular:
                // 总计下载榜（主人 2026-09-28 新增第 4 榜）：主值＝累计下载量；副行＝本周量。
                // 两个都是"下载次数"，与"本周下载"榜同一口径（ShortCount），跨榜数字才对得上。
                // ⚠ 主值**不兜 0**：源端只收"有基线初值的包"（scope.packagesWithBaselineOnly），
                //   最近才收录的包 total 就是 null —— 那是"源端没有这个数"，不是"零次下载"。
                //   写成 ShortCount(row.Total ?? 0) 会把"没采到"显示成"0"，与"本周下载"榜
                //   副行"缺就空着"的口径相反（同一份数据两种说法，用户没法判断该信哪个）。
                //   这里用项目既有的缺值写法「—」（同 MainWindow.Market.cs 的「↓ —」）。
                mainText = row.Total.HasValue ? ShortCount(row.Total.Value) : "—";
                mainColor = Color.FromRgb(0xF5, 0xF5, 0xF7);
                subText = row.Week.HasValue ? $"本周 {ShortCount(row.Week.Value)}" : "";
                tipText = (row.Total.HasValue ? $"{row.Total.Value:N0} 次下载" : "累计下载量暂未采到")
                        + (row.Week.HasValue ? $" · {row.Week.Value:N0} 次（本周）" : "");
                break;

            case TrendsBoard.Stars:
                // 星标榜主值一律用 StarK（k 作单位），**不再用 ShortCount**：
                // ShortCount 是"万/k 混用"口径，同一个榜里会出现 "4.3万" 与 "4.7k" 并排，
                // 看着就是两种单位、比大小要先换算。理由与取舍见 StarK 的注释。
                mainText = $"★ {StarK(row.Stars)}";
                // 星标色按主题给（夜间亮金 / 日间深琥珀）：原先写死 #FFD60A，在日间浅底上几乎看不见
                // —— 用户 2026-10-04 反馈"让⭐更明显一点"。判据集中在 ThemeManager.StarColor。
                mainColor = ThemeManager.StarColor;
                // 副行也配一条（四榜节奏一致）：给出同一颗星的另一个口径，顺便让行高与别榜齐平。
                subText = $"共 {ShortCount(row.Stars)}";
                tipText = $"★ {row.Stars:N0}";
                break;

            default:
                // 涨星榜：主值就是这个区间净增的星数，前面补 "+" 表明增量。
                mainText = $"+{ShortCount(row.DeltaStars)}";
                mainColor = Color.FromRgb(0x34, 0xC7, 0x59);
                // 副行给出起止量，用户能自己判断"这个增量是基数大还是真起势"。
                subText = $"{ShortCount(row.FromStars)} → {ShortCount(row.ToStars)}";
                tipText = $"{row.FromStars:N0} → {row.ToStars:N0} 星（净增 {row.DeltaStars:N0}）";
                break;
        }

        valCol.Children.Add(new TextBlock
        {
            Text = mainText,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Right,
            HorizontalAlignment = HorizontalAlignment.Right,
            // 等宽字体：数字不等宽时（比如 "1" 与 "8"），右对齐只能让**最后一个字符**对齐，
            // 小数点/量级位仍然参差，一列数字看着像波浪。等宽后小数点上下成一条线，量级差一眼可读。
            // 口径沿用本程序既有做法（MainWindow.Console.cs 的日志框、XAML 里的输入框都用 "Consolas"），
            // 不新引字体族，免得同一个程序里出现两种"等宽"。
            // ⚠ 只给数值列设：名称列是中文，中文走等宽会又挤又难看。
            FontFamily = new FontFamily("Consolas"),
            Foreground = new SolidColorBrush(mainColor)
        });

        // 副行**永远创建**，文案为空串时用 Hidden 而不是 Collapsed —— 这条是"行高统一"的要害：
        //   · Hidden   ：节点还在布局里，占着它那一行文字的高度 ⇒ 同一榜内所有行高**完全一致**；
        //   · Collapsed：节点退出布局 ⇒ 没有副行的行矮一截，"行高忽高忽低"原样复现。
        // 真实数据里这差别是看得见的：本周下载榜 100 条里有 35 条的 total 是 null
        // （源端只收"有基线初值的包"，那 35 条是最近才收录、从来没有过初值 ⇒ 累计数在源端就不存在），
        // 用 Collapsed 的话这 35 行会比别的行矮一行，列表看起来像断了几处。
        // 注意"副行存在但文字为空"和"这一行没有副行数据"是两件事，界面上表现为"空着"而不是"少一行"。
        valCol.Children.Add(new TextBlock
        {
            Text = subText,
            FontSize = 10.5,
            TextAlignment = TextAlignment.Right,
            HorizontalAlignment = HorizontalAlignment.Right,
            Visibility = subText.Length > 0 ? Visibility.Visible : Visibility.Hidden,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93))
        });

        // 悬停提示挂在数值列这个容器上（而不是某一行文字上）：鼠标落在主值或副行上都能弹出来。
        // 用 BuildTrendTip 自绘深底气泡，不用默认 ToolTip —— 默认气泡是浅底深字，
        // 在深色主题下一悬停就刺眼，而且它不在主视觉树里、换肤链路够不到（详见 BuildTrendTip 注释）。
        // 只放数字，绝不出现网址（项目铁律）。
        if (tipText.Length > 0) valCol.ToolTip = BuildTrendTip(tipText);

        Grid.SetColumn(valCol, 2);
        g.Children.Add(valCol);
        return g;
    }

    /// <summary>
    /// 造悬停简介气泡。**必须自绘**，不能用默认 ToolTip：
    ///   1) 默认气泡是浅底深字，本程序是深色主题，一悬停就刺眼；
    ///   2) ToolTip 弹出的是**独立窗口**，不在主视觉树里，ThemeManager 的"逐节点换肤"够不到它 ——
    ///      也就是说它不可能随主题自动变色。所以这里直接写死"深底 + 灰字"这一组
    ///      在深浅两套主题下都成立的配色（深色下是常规观感，日间下是一个不透明的小气泡，
    ///      同样清楚可读），不去依赖换肤链路。
    /// 注意它也不进 SwapThemed：气泡不在面板子元素里，SwapThemed 遍历不到（见上面第 2 点）。
    /// </summary>
    private static ToolTip BuildTrendTip(string desc) => new()
    {
        Content = new TextBlock
        {
            Text = desc,
            MaxWidth = 360,                       // 长简介自动折行，不让气泡横着长成一条
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0xC7, 0xC7, 0xCC))   // 灰字
        },
        Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x22)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3E)),
        BorderThickness = new Thickness(1),
        Padding = new Thickness(10, 8, 10, 8)
    };

    /// <summary>
    /// 切榜。契约是字符串（自检与界面的点击都传小写串），映射在这里统一收口：
    /// 认不出的串一律当 Rising，绝不因为一个拼错的键把整屏变成空白。
    /// </summary>
    internal void ShowTrendsTab(string board)
    {
        _trendsBoard = board switch
        {
            "downloads" => TrendsBoard.Downloads,
            "stars" => TrendsBoard.Stars,
            "popular" => TrendsBoard.Popular,
            _ => TrendsBoard.Rising
        };
        RenderTrends();
    }

    /// <summary>
    /// 抓取入口。注意它"绝不进启动路径"：App 启动、开壳、自带自检都不碰它，
    /// 只有用户真的切到「生态趋势」这一屏时（或点了刷新）才调用——外部站点
    /// 慢也好、坏也好，代价只由这一屏承担，不能影响开壳时间，也不能把异常
    /// 抛到启动链上。
    ///
    /// force=true 时忽略服务层的缓存（用户手动刷新语义）；服务层 LoadAsync 承诺
    /// 永不抛异常，这里再兜一层只是防止未来契约被改坏时整窗陪葬。
    /// </summary>
    internal async Task RefreshTrendsAsync(bool force = false)
    {
        if (_trendsPinned) return;               // 已灌样板（自检/出图）⇒ 不许联网回包覆盖它，见 _trendsPinned
        if (_trendsLoading) return;              // 闸门：不并发，重复进页直接复用正在跑的那次
        _trendsLoading = true;
        _trendsStatus = "正在获取生态数据…";
        RenderTrends();                          // 先让"正在获取…"上屏，别让用户对着空屏等

        try
        {
            var loaded = await EcosystemTrendsService.LoadAsync(force);
            // ⚠ 写回处必须再查一次"钉住"（不能只靠方法开头那一查）：
            //   换页动作发起的那一发请求是**在飞**的，灌样板发生在它返回之前 —— 只查开头的话，
            //   这一发回来照样会覆盖样板，而且它还会在 finally 里重绘一次，把覆盖结果推上屏。
            //   实测就是这样：自检里行数变成 100（真实数据）而不是样板的 3 ——
            //   即两次"钉住"检查缺一不可：开头挡新增，这里挡在飞。
            if (!_trendsPinned) _trends = loaded;
        }
        catch (Exception ex) { Logger.LogError("RefreshTrendsAsync", ex); }
        finally
        {
            // 收尾一定重绘：异常路径同样要走这里，否则界面会永远停在"正在获取…"。
            _trendsLoading = false;
            if (!_trendsPinned) RenderTrends();
        }

        // 社区目录只用于"悬停看简介"：静默准备，好了再重绘一次补上简介。
        // 绝不 await 在关键路径上 —— 目录要走网络，不能拖慢这一屏的首次显示。
        // 位置刻意放在所有 return 闸门（含 _trendsPinned）**之后**：一旦数据被钉住（自检/出图灌样板），
        // 整段就不执行 —— 既不联网、也不会再多一次重绘，样张保持逐像素可比（见 _trendsPinned）。
        _ = EnsureMarketQuietAsync().ContinueWith(t =>
        {
            try { if (t.Result && TrendsView.Visibility == Visibility.Visible) Dispatcher.BeginInvoke(new Action(RenderTrends)); }
            catch { }
        });
    }

    // ── 自检钩子（名字冻结，SelfTest 按下面这些断言） ──

    /// <summary>灌入样本数据并重绘。顺手把加载闸门放开：样本已经到位，没有"正在获取"这回事。
    /// 同时**钉住**数据（见 <see cref="_trendsPinned"/>）：换页动作那一发联网请求的回包
    /// 不能再把这些样板覆盖掉，否则样张每次都不同、比对失去意义。</summary>
    internal void SetTrendsSampleForTest(EcosystemTrends t)
    {
        _trendsPinned = true;
        _trends = t;
        _trendsLoading = false;
        _trendsStatus = "";
        RenderTrends();
    }

    /// <summary>切榜的自检入口，行为与界面点击完全一致。</summary>
    internal void ShowTrendsTabForTest(string board) => ShowTrendsTab(board);

    /// <summary>
    /// 自检要的三件事：真实上屏行数、底部来源行+状态行的整段文字、当前榜名。
    /// Rows 取渲染时累加的计数（比在视觉树里数字段更稳，也更反映实际渲染）。
    /// </summary>
    internal (int Rows, string Foot, string Board) TrendsFactsForTest
    {
        get
        {
            string fresh = EcosystemTrendsService.FreshnessText(_trends?.SnapshotDate, _trends?.CheckedAt, DateTime.Now);
            string foot = EcosystemTrendsService.AttributionText + " · " + fresh;
            if (!string.IsNullOrEmpty(_trendsStatus)) foot = foot + " · " + _trendsStatus;
            return (_trendsRowsOnScreen, foot, BoardKey());
        }
    }

    /// <summary>_trendsBoard 反推回小写串，与 ShowTrendsTab 的入参口径互为逆映射。</summary>
    private string BoardKey() => _trendsBoard switch
    {
        TrendsBoard.Downloads => "downloads",
        TrendsBoard.Stars => "stars",
        TrendsBoard.Popular => "popular",
        _ => "rising"
    };

    /// <summary>左侧导航第 8 项是否真的存在（XAML 里掉了名字的话，这一屏就进不去）。</summary>
    internal int NavTrendsExistsForTest() => FindName("NavTrends") != null ? 1 : 0;

    /// <summary>视图当前是否可见。由 ShowView 统一切换，这里只做只读观察。</summary>
    internal bool TrendsViewVisibleForTest => TrendsView.Visibility == Visibility.Visible;

    /// <summary>榜单卡片下方还剩多少空白（TrendsPanel 高度 - 卡片底边）。要求"铺满"就是要求这个数接近 0。</summary>
    internal double TrendsBlankBelowCardForTest
    {
        get
        {
            // 拿不到就返回 NaN（不是 0）：0 会被读成"已经铺满、检查通过"，
            // 而"量不出来"和"量出来是满的"是两件事。自检对 NaN 判红，才不会假过。
            var card = _trendsCardForTest;
            if (card == null) return double.NaN;
            try
            {
                // 还没走过一次布局（ActualHeight 都是 0）时量不出有意义的值 —— 同样给 NaN。
                if (card.ActualHeight <= 0 || TrendsPanel.ActualHeight <= 0) return double.NaN;

                // 把卡片底边（自身坐标系的 (0, ActualHeight)）换算到 TrendsPanel 坐标系，
                // 再与面板高度相减 —— 这就是"卡片下面还剩几像素"。
                Point bottom = card.TranslatePoint(new Point(0, card.ActualHeight), TrendsPanel);
                if (double.IsNaN(bottom.Y)) return double.NaN;
                return TrendsPanel.ActualHeight - bottom.Y;
            }
            catch { return double.NaN; }   // 未接入视觉树时 TranslatePoint 会抛，一律当"量不出来"
        }
    }

    /// <summary>当前统计周期那一行的文字（不适用时为空串）。</summary>
    internal string TrendsPeriodTextForTest => _trendsPeriodText;

    /// <summary>
    /// 第 index 行名称上的悬停提示文字（取不到返回空串）。
    /// 真的去视觉树里捞，而不是读渲染时记下的副本：这个钩子要回答的是"屏幕上那一格到底挂了什么"，
    /// 读副本只能证明我们"打算挂什么"，挂载环节出错（比如 ToolTip 被别处覆盖）它照样绿。
    /// 路径：TrendsPanel 第 5 行是卡片 → ScrollViewer → listHost(StackPanel) → 第 index 个行 Grid，
    /// 行内只有名称格挂 ToolTip，DFS 找到第一个带 ToolTip 的 TextBlock 即为它。
    /// ToolTip 有两种形态：有简介时是自绘 ToolTip 对象（文字在 Content.Text 上），
    /// 无简介时是字符串轻提示 —— 两种都要能取到。任何一步对不上就返回空串。
    /// </summary>
    internal string TrendsTooltipTextForTest(int index)
    {
        try
        {
            if (index < 0) return "";
            if (_trendsCardForTest?.Child is not ScrollViewer sc) return "";
            if (sc.Content is not StackPanel listHost) return "";
            if (index >= listHost.Children.Count) return "";
            if (listHost.Children[index] is not DependencyObject rowRoot) return "";

            var nameTb = FindFirstToolTipTextBlock(rowRoot);
            if (nameTb == null) return "";

            object? tip = nameTb.ToolTip;
            if (tip is ToolTip t) return (t.Content as TextBlock)?.Text ?? "";
            return tip as string ?? "";
        }
        catch { return ""; }   // 取不到一律空串，绝不把异常甩给自检
    }

    /// <summary>
    /// 深度优先找第一个挂了 ToolTip 的 TextBlock。榜单行里**只有名称格**会在 TextBlock 上直接挂 ToolTip
    /// （数值列那条提示挂在它的 StackPanel 容器上，容器不是 TextBlock，DFS 不会认错），所以找到的必然是名称格。
    /// </summary>
    private static TextBlock? FindFirstToolTipTextBlock(DependencyObject root)
    {
        if (root is TextBlock tb && tb.ToolTip != null) return tb;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var hit = FindFirstToolTipTextBlock(VisualTreeHelper.GetChild(root, i));
            if (hit != null) return hit;
        }
        return null;
    }

    // ── 自检钩子：逐行读数（主人 2026-09-28 第 3/4 条要求，名字冻结） ──
    //
    // 三个钩子回答的是同一个问题域："屏幕上第 index 行**真的**长什么样" ——
    // 单位统一（k）、副行不编数据、同一榜内行高一致。所以都走同一条路：
    // _trendsCardForTest → ScrollViewer → listHost(StackPanel) → 第 index 个行 Grid。
    // 与 TrendsTooltipTextForTest 一样**去视觉树里捞**而不是读渲染时记的副本：
    // 副本只能证明"我们打算这么画"，挂载环节出错它照样绿。
    // 行的结构（见 BuildTrendRow）：Grid，第 0 列序号 / 第 1 列名称 / 第 2 列数值列 StackPanel；
    // 数值列里 Children[0] 是主值 TextBlock、Children[1] 是**恒存在的**副行 TextBlock。

    /// <summary>
    /// 取第 index 行的行 Grid（取不到返回 null）。三个读数钩子共用的定位逻辑，
    /// 收成一处是为了"三个钩子看的是同一行"这件事在代码上也是同一个事实 ——
    /// 若哪天行的层级变了，改一个地方三个钩子一起跟上，不会出现两个钩子各认一行。
    /// </summary>
    private Grid? TrendRowForTest(int index)
    {
        try
        {
            if (index < 0) return null;
            if (_trendsCardForTest?.Child is not ScrollViewer sc) return null;
            if (sc.Content is not StackPanel listHost) return null;
            if (index >= listHost.Children.Count) return null;
            return listHost.Children[index] as Grid;
        }
        catch { return null; }   // 未接入视觉树等异常一律当"取不到"，绝不甩给自检
    }

    /// <summary>取第 index 行数值列里的那个 StackPanel（取不到返回 null）。</summary>
    private StackPanel? TrendValColForTest(int index)
    {
        var row = TrendRowForTest(index);
        if (row == null) return null;
        foreach (UIElement child in row.Children)
        {
            // 数值列是唯一带 Grid.Column == 2 标记的子元素；按列号找比按 Children 下标找稳 ——
            // 下标会随"以后再加一列"整体挪位，列号是布局契约本身。
            if (child is StackPanel sp && Grid.GetColumn(sp) == 2) return sp;
        }
        return null;
    }

    /// <summary>当前榜第 index 行的**主值**文字（星标榜用来验"单位统一为 k"）。取不到返回空串。</summary>
    internal string TrendsMainTextForTest(int index)
    {
        try
        {
            var val = TrendValColForTest(index);
            if (val == null || val.Children.Count < 1) return "";
            return (val.Children[0] as TextBlock)?.Text ?? "";
        }
        catch { return ""; }
    }

    /// <summary>
    /// 当前榜第 index 行的**副行**文字（没数据时为空串 —— 用来验"副行不编数据"）。取不到返回空串。
    /// 注意"取不到"与"副行文字为空串"在这里**返回同一个值**：调用方若想区分，
    /// 应当先确认行存在（例如同时问行高）；这个钩子的契约是"文字是什么"，不是"行在不在"。
    /// </summary>
    internal string TrendsSubTextForTest(int index)
    {
        try
        {
            var val = TrendValColForTest(index);
            // 副行**恒存在**（见 BuildTrendRow），所以下标 1 取不到就说明结构不对 ⇒ 空串。
            if (val == null || val.Children.Count < 2) return "";
            return (val.Children[1] as TextBlock)?.Text ?? "";
        }
        catch { return ""; }
    }

    /// <summary>
    /// 当前榜第 index 行的**行高**（用来验"同一榜内行高一致"）。取不到返回 double.NaN（自检据此判红，不假过）。
    ///
    /// 为什么取不到必须给 NaN 而不是 0：0 会被读成"量出来是 0 高"，
    /// 而"同一榜内行高一致"这条判据在"所有行都量成 0"时反而**恒真** ——
    /// 没走过布局的空窗期就会假过。NaN 与 0 在断言里一眼可辨（NaN 参与相等比较永远为 false）。
    /// 与 TrendsBlankBelowCardForTest 同一套约定。
    /// </summary>
    internal double TrendsRowHeightForTest(int index)
    {
        try
        {
            var row = TrendRowForTest(index);
            if (row == null) return double.NaN;
            // 还没走过一次布局时 ActualHeight 是 0，量不出有意义的值 —— 同样给 NaN。
            return row.ActualHeight > 0 ? row.ActualHeight : double.NaN;
        }
        catch { return double.NaN; }
    }
}
