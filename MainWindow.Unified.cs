using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DSHGuard.Unified;

namespace DSHGuard;

public partial class MainWindow : Window
{
    private DshResourceKind _dshSection = DshResourceKind.Plugin;
    private DshUnifiedDocument? _dshDoc;

    /// <summary>插件缓存：键 = GuardTarget。</summary>
    private readonly Dictionary<GuardTarget, List<PluginManager.Plugin>> _dshPluginCache = new();

    /// <summary>各域各自的搜索关键字（切换分区不丢）。</summary>
    private readonly Dictionary<string, string> _dshSearch = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>外部工具区：新增表单是否展开（默认收起减少认知负担）。</summary>
    private bool _mcpFormOpen;

    /// <summary>技能区：新建技能表单是否展开。</summary>
    private bool _skillFormOpen;

    /// <summary>技能区：从 GitHub 导入表单是否展开。</summary>
    private bool _skillImportOpen;

    /// <summary>提示词区：新建提示词表单是否展开。</summary>
    private bool _promptFormOpen;

    /// <summary>
    /// 综合管理区的「一次只做一件事」闸门。
    ///
    /// 用 0/1 整数而不是 bool：检查-再赋值中间会被弹窗、异步回调、消息泵插进来的
    /// 第二次点击钻空子（两次写同一个 YAML 会互相覆盖）。Interlocked 把这两步合成
    /// 一次原子操作，第二次点击直接拿到 false 走人。
    /// </summary>
    private int _dshBusyFlag;

    /// <summary>占位时记下是哪个分区发起的，决定收尾要不要重建视图。</summary>
    private DshResourceKind _dshBusySection;

    /// <summary>
    /// 试着占住闸门。已经有人占着就返回 false，调用方应直接 return。
    /// </summary>
    private bool TryEnterDshBusy()
    {
        if (Interlocked.CompareExchange(ref _dshBusyFlag, 1, 0) != 0) return false;
        _dshBusySection = _dshSection;
        return true;
    }

    /// <summary>
    /// 只看一眼闸门有没有被占，不占它。
    ///
    /// 用在「先弹确认框、用户点了确定才真动手」的地方：弹框期间不能占着闸门，
    /// 否则用户一取消就再也放不开了。等确认过了、真要写盘之前才用
    /// <see cref="TryEnterDshBusy"/> 占住。
    /// </summary>
    private bool DshBusy => Volatile.Read(ref _dshBusyFlag) != 0;

    /// <summary>
    /// 放开闸门。
    ///
    /// 默认顺手重建一次视图；但只在**还停在发起操作的那个分区**时才重建 ——
    /// 用户中途切到别的分区时，重建会把刚建好的那棵树整棵拆掉重来
    /// （还会把滚动位置和展开状态冲掉），而他并没有在看原来那一页。
    /// 那种情况什么都不做，等他切回去时本来就会重建。
    /// </summary>
    private void ExitDshBusy(bool refresh = true)
    {
        DshResourceKind origin = _dshBusySection;
        Interlocked.Exchange(ref _dshBusyFlag, 0);
        if (refresh && _dshSection == origin) RefreshUnifiedView();
    }

    private static readonly Color DshTextPrimary = DshWidgets.TextPrimary;
    private static readonly Color DshTextSecondary = DshWidgets.TextSecondary;
    private static readonly Color DshTextTertiary = DshWidgets.TextTertiary;
    private static readonly Color DshAccent = DshWidgets.Accent;
    private static readonly Color DshWarn = DshWidgets.Warn;
    private static readonly Color DshError = DshWidgets.Danger;
    private static readonly Color DshOk = DshWidgets.Ok;
    private static readonly Color DshBorder = DshWidgets.Border;

    // 入口

    private void UnifiedButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ShowView(GuardView.Unified);
    }

    /// <summary>出图/自检用：切到指定资源域。</summary>
    internal void SetUnifiedSectionForTest(string section)
    {
        _dshSection = ParseSection(section);
        RefreshUnifiedView();
    }

    /// <summary>出图/自检用：展开外部工具的新增表单。</summary>
    internal void OpenMcpFormForTest()
    {
        _mcpFormOpen = true;
        _dshSection = DshResourceKind.Mcp;
        RefreshUnifiedView();
    }

    /// <summary>出图用：展开技能/提示词的新建或 GitHub 导入表单（form = add|import|prompt）。</summary>
    internal void OpenAuthoringFormForTest(string form)
    {
        _skillFormOpen = _skillImportOpen = _promptFormOpen = false;
        switch (form?.ToLowerInvariant())
        {
            case "import":
                _dshSection = DshResourceKind.Skill;
                _skillImportOpen = true;
                break;
            case "prompt":
                _dshSection = DshResourceKind.Prompt;
                _promptFormOpen = true;
                break;
            default:
                _dshSection = DshResourceKind.Skill;
                _skillFormOpen = true;
                break;
        }
        RefreshUnifiedView();
    }

    private static DshResourceKind ParseSection(string? section) => section?.ToLowerInvariant() switch
    {
        "mcp" or "mcps" => DshResourceKind.Mcp,
        "skill" or "skills" => DshResourceKind.Skill,
        "prompt" or "prompts" => DshResourceKind.Prompt,
        "session" or "sessions" => DshResourceKind.Session,
        _ => DshResourceKind.Plugin,
    };

    private string SectionId => _dshSection switch
    {
        DshResourceKind.Mcp => "mcp",
        DshResourceKind.Skill => "skill",
        DshResourceKind.Prompt => "prompt",
        DshResourceKind.Session => "session",
        _ => "plugin",
    };

    private string SearchText
    {
        get => _dshSearch.TryGetValue(SectionId, out var v) ? v : "";
        set => _dshSearch[SectionId] = value ?? "";
    }

    private void RefreshUnifiedView()
    {
        try
        {
            _dshDoc ??= DshUnifiedStore.Load();
            InvalidateDshScans();      // 重新进页 / 写操作后：全部数据重扫一次
            RenderDshUnified();
        }
        catch (Exception ex)
        {
            Logger.LogError("RefreshUnifiedView", ex);
            RenderDshError(ex);
        }
    }

    // ── 一次性扫描缓存 ──────────────────────────────────────────────
    // 工具条要显示「N 个」这类摘要，列表要显示明细，两者都来自同一次扫描。
    // 没有缓存的话每帧扫两遍（其中 MCP 要读 4 个 patch 文件、技能要遍历 138 个目录）。
    private List<DshMcpServer>? _dshMcpCache;
    private List<DshSkill>? _dshSkillCache;
    private List<DshSession>? _dshSessionCache;
    private List<DshPromptFile>? _dshPromptCache;

    private void InvalidateDshScans()
    {
        _dshPluginCache.Clear();
        _dshMcpCache = null;
        _dshSkillCache = null;
        _dshSessionCache = null;
        _dshPromptCache = null;
    }

    private List<DshMcpServer> McpServers() => _dshMcpCache ??= DshMcpManager.Scan();
    private List<DshSkill> Skills() => _dshSkillCache ??= DshSkillReader.Scan();
    private List<DshSession> Sessions() => _dshSessionCache ??= DshSessionReader.Scan();
    private List<DshPromptFile> Prompts() => _dshPromptCache ??= DshPromptReader.Scan();

    /// <summary>
    /// 整屏重绘：分区栏 → 工具条 → 内容。
    ///
    /// 工具条（含搜索框）**必须在内容之外**。上一版把它和列表塞在同一棵树里，
    /// 搜索时「只重建内容区」连搜索框自己一起销毁重建 —— 焦点丢失，
    /// 用户实际上打不进第二个字（评审 P1-1）。
    /// </summary>
    private void RenderDshUnified()
    {
        if (UnifiedRoot == null) return;
        UnifiedRoot.Children.Clear();

        UnifiedRoot.Children.Add(BuildDshSectionBar());
        UnifiedRoot.Children.Add(BuildDshToolbarForSection());

        _dshBody = BuildDshBody();
        UnifiedRoot.Children.Add(_dshBody);

        ButtonFx.Wire(UnifiedRoot);   // 动态建出来的按钮要自己挂动效（已在树上的不会重复挂）
    }

    /// <summary>
    /// 只重建内容区，**不动分区栏与工具条**。
    ///
    /// 搜索框每敲一个字都要刷新列表；如果用整屏重绘，搜索框自己也会被销毁重建，
    /// 焦点与光标全丢。这里只替换内容那一层，搜索框始终活着。
    /// </summary>
    private void RebuildDshBodyOnly()
    {
        try
        {
            if (UnifiedRoot == null || _dshBody == null)
            {
                RenderDshUnified();
                return;
            }
            int idx = UnifiedRoot.Children.IndexOf(_dshBody);
            if (idx < 0)
            {
                RenderDshUnified();
                return;
            }

            _dshSearchRefreshing = true;
            try
            {
                var fresh = BuildDshBody();
                UnifiedRoot.Children.RemoveAt(idx);
                UnifiedRoot.Children.Insert(idx, fresh);
                _dshBody = fresh;
                ButtonFx.Wire(fresh);   // 换上去的内容也要挂动效
            }
            finally
            {
                _dshSearchRefreshing = false;
            }
        }
        catch (Exception ex)
        {
            _dshSearchRefreshing = false;
            Logger.LogError("RebuildDshBodyOnly", ex);
            RenderDshError(ex);
        }
    }

    /// <summary>当前内容区元素（搜索时只换它，工具条与分区栏不动）。</summary>
    private UIElement? _dshBody;

    /// <summary>搜索刷新进行中（挡住 TextChanged 自触发）。</summary>
    private bool _dshSearchRefreshing;

    private UIElement BuildDshBody() => _dshSection switch
    {
        DshResourceKind.Mcp => BuildDshMcpSection(),
        DshResourceKind.Skill => BuildDshSkillSection(),
        DshResourceKind.Prompt => BuildDshPromptSection(),
        DshResourceKind.Session => BuildDshSessionSection(),
        _ => BuildDshPluginSection(),
    };

    /// <summary>
    /// 出错界面。**不显示异常堆栈** —— 那是给日志的；
    /// 界面上只说「哪一步没成、现在能做什么」。
    /// </summary>
    private void RenderDshError(Exception ex)
    {
        if (UnifiedRoot == null) return;
        UnifiedRoot.Children.Clear();
        UnifiedRoot.Children.Add(DshWidgets.Text("这个页面没能加载出来", 15, DshError, FontWeights.SemiBold));

        var why = DshWidgets.Hint(
            "可能是 DSH 的目录暂时读不到（比如正在重启），或者某个配置文件格式有问题。\n" +
            "你的数据没有被改动。");
        why.Margin = new Thickness(0, 6, 0, 0);
        UnifiedRoot.Children.Add(why);

        var detail = DshWidgets.Hint("技术细节（排查用）：" + ex.GetType().Name + " — " + ex.Message, 10);
        detail.Margin = new Thickness(0, 8, 0, 0);
        UnifiedRoot.Children.Add(detail);

        var retry = DshWidgets.SmallButton("再试一次", "primary");
        retry.Margin = new Thickness(0, 12, 0, 0);
        retry.Click += (_, _) =>
        {
            _dshPluginCache.Clear();
            _dshDoc = null;
            RefreshUnifiedView();
        };
        UnifiedRoot.Children.Add(retry);
        ButtonFx.Wire(UnifiedRoot);
    }

    // 分区栏
    //
    // 五个分区的「顺序 / 标签 / 存档 id」只在这一张表里写一次。
    // 以前这张表是写在 BuildDshSectionBar 的 foreach 里的匿名数组字面量，
    // 自检 CM-8 就只能写 `check(..., true, "见 BuildDshSectionBar")` ——
    // 常量比常量，永远不会失败，等于把那一项当装饰。
    // 抽成字段之后，自检可以拿**期望的字面量**去比实际渲染出来的标签。

    private static readonly (DshResourceKind Kind, string Label, string Id)[] DshSections =
    {
        (DshResourceKind.Plugin, DshText.SectionPlugin, "plugin"),
        (DshResourceKind.Mcp, DshText.SectionMcp, "mcp"),
        (DshResourceKind.Skill, DshText.SectionSkill, "skill"),
        (DshResourceKind.Prompt, DshText.SectionPrompt, "prompt"),
        (DshResourceKind.Session, DshText.SectionSession, "session"),
    };

    /// <summary>自检用：分区表本体（顺序 / 标签 / 存档 id）。</summary>
    internal static IReadOnlyList<(DshResourceKind Kind, string Label, string Id)> DshSectionsForTest
        => DshSections;

    /// <summary>
    /// 自检用：把整屏重绘一遍，再从**视觉树里把分区栏实际画出来的东西读回来**。
    ///
    /// 只读分区表是查不出「表对了但渲染写死了别的标签」这种事的，
    /// 所以这里真去数一数 Children 里的 TextBlock。
    /// 窗口还没建好（取不到 UnifiedRoot）时返回 null，由调用方 SKIP，不冒充通过。
    /// </summary>
    internal List<(string Label, bool Active, string Tip)>? SectionBarRenderedForTest()
    {
        RenderDshUnified();

        if (UnifiedRoot == null || UnifiedRoot.Children.Count == 0) return null;
        if (UnifiedRoot.Children[0] is not StackPanel wrap || wrap.Children.Count == 0) return null;
        if (wrap.Children[0] is not StackPanel bar) return null;

        var list = new List<(string, bool, string)>();
        foreach (var child in bar.Children)
        {
            // 分格是「圆角 Border 包一个 TextBlock」，回读时把 Border 拆掉看里面的字。
            var tb = child as TextBlock ?? (child as Border)?.Child as TextBlock;
            if (tb == null) continue;
            list.Add((tb.Text ?? "", tb.FontWeight == FontWeights.SemiBold, tb.ToolTip as string ?? ""));
        }
        return list.Count == 0 ? null : list;
    }

    private UIElement BuildDshSectionBar()
    {
        var wrap = new StackPanel();
        var bar = new StackPanel { Orientation = Orientation.Horizontal };

        foreach (var (kind, label, id) in DshSections)
        {
            bool active = _dshSection == kind;

            var tb = new TextBlock
            {
                Text = label,
                FontSize = 13.5,
                Foreground = new SolidColorBrush(active ? DshTextPrimary : DshTextSecondary),
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                ToolTip = DshText.SectionHint(id),
            };

            // 每一格做成圆角胶囊：和主程序的小按钮同一份圆角与内外边距口径，
            // 不再是一排贴着底的方块。未选中的格子悬停时描边 + 淡蓝底。
            var bg = new SolidColorBrush(active ? DshWidgets.FieldBg : Colors.Transparent);
            var edge = new SolidColorBrush(active ? DshWidgets.Accent : Colors.Transparent);
            var cell = new Border
            {
                Child = tb,
                CornerRadius = new CornerRadius(RoundBtn.Radius),
                Padding = new Thickness(15, 8, 15, 8),
                Margin = new Thickness(0, 0, 8, 6),
                BorderThickness = new Thickness(1),
                Background = bg,
                BorderBrush = edge,
                Cursor = Cursors.Hand,
            };

            if (!active)
            {
                cell.MouseEnter += (_, _) =>
                {
                    bg.Color = Color.FromArgb(0x3A, DshWidgets.Accent.R, DshWidgets.Accent.G, DshWidgets.Accent.B);
                    edge.Color = DshWidgets.Accent;
                };
                cell.MouseLeave += (_, _) =>
                {
                    bg.Color = Colors.Transparent;
                    edge.Color = Colors.Transparent;
                };
            }

            ButtonFx.Wire(cell);   // 悬停放大提亮、按下缩小变暗，与主程序同一套动效

            var captured = kind;
            var capturedId = id;
            cell.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                if (_dshSection == captured) return;
                _dshSection = captured;
                DshUnifiedStore.Mutate(d => d.LastSection = capturedId);
                RefreshUnifiedView();
            };
            bar.Children.Add(cell);
        }

        // 刷新入口做成与五格同口径的胶囊：同圆角、同内边距、同字号、同基线，
        // 只把配色换成强调色，表示它是动作而不是分格。
        //
        // ⚠ 里面**不能**直接放 TextBlock：SectionBarRenderedForTest 把「Border 包 TextBlock」
        // 当作一个分格读回去，CM-8b 数到的格数就会从 5 变 6。中间隔一层 StackPanel，
        // 回读时 `(child as Border)?.Child as TextBlock` 得到 null，自然跳过，自检口径不用动。
        var reloadBg = new SolidColorBrush(Color.FromArgb(0x2E, DshWidgets.Accent.R, DshWidgets.Accent.G, DshWidgets.Accent.B));
        var reloadLabel = new TextBlock
        {
            Text = "刷新",
            FontSize = 13.5,
            Foreground = new SolidColorBrush(Colors.White),
        };
        var reload = new Border
        {
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { reloadLabel } },
            CornerRadius = new CornerRadius(RoundBtn.Radius),
            Padding = new Thickness(15, 8, 15, 8),
            Margin = new Thickness(8, 0, 0, 6),
            BorderThickness = new Thickness(1),
            Background = reloadBg,
            BorderBrush = new SolidColorBrush(DshWidgets.Accent),
            Cursor = Cursors.Hand,
            ToolTip = "重新扫描磁盘。在 DSHGuard 之外修改过文件后使用。",
        };

        reload.MouseEnter += (_, _) =>
            reloadBg.Color = Color.FromArgb(0x5A, DshWidgets.Accent.R, DshWidgets.Accent.G, DshWidgets.Accent.B);
        reload.MouseLeave += (_, _) =>
            reloadBg.Color = Color.FromArgb(0x2E, DshWidgets.Accent.R, DshWidgets.Accent.G, DshWidgets.Accent.B);

        ButtonFx.Wire(reload);   // 悬停放大提亮、按下缩小变暗，与五格同一套动效

        reload.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            _dshPluginCache.Clear();
            _dshDoc = null;
            RefreshUnifiedView();
        };
        bar.Children.Add(reload);

        wrap.Children.Add(bar);

        return wrap;
    }

    // 插件

    /// <summary>
    /// 星标收藏按钮：一颗图标（已收藏实心金色 ★ / 未收藏空心灰 ☆）。
    /// 五个分区共用这一个造法，保证「点一下是收藏」只有一处实现。
    /// 收藏之后条目置顶，且「删除 / 移除」入口收起——先取消收藏才能删。
    /// 尺寸与字号刻意压到与同排标题文字同一基线，避免图标块把标题挤歪。
    /// </summary>
    private UIElement BuildDshStarButton(string key, string what)
    {
        bool starred = DshFavorites.IsStarred(key);
        var btn = new Button
        {
            Content = starred ? "★" : "☆",
            FontSize = 13,
            Padding = new Thickness(0),
            Width = 18,
            Height = 18,
            MinWidth = 0,
            MinHeight = 0,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(starred ? DshWarn : DshTextTertiary),
            Cursor = Cursors.Hand,
            Focusable = false,
            VerticalAlignment = VerticalAlignment.Center
        };
        btn.ToolTip = starred
            ? "已收藏：" + what + " 将置顶显示。点击取消收藏。"
            : "收藏「" + what + "」：置顶显示，取消收藏前不提供删除按钮。";
        btn.Margin = new Thickness(0, 0, 6, 0);
        RoundBtn.Apply(btn);
        ButtonFx.Wire(btn);
        btn.Click += (_, _) =>
        {
            bool now = DshFavorites.Toggle(key);
            ShowDshToast(now ? "已收藏，排到最前面。" : "已取消收藏。");
            RenderDshUnified();
        };
        return btn;
    }

    private UIElement BuildDshPluginSection()
    {
        var panel = new StackPanel();
        var columns = new[] { GuardTarget.Web, GuardTarget.Desktop };
        var titles = new[] { "Web 引擎", "桌面版" };


        if (columns.All(t => GetPlugins(t).Count == 0))
        {
            var (title, next) = DshText.Empty("plugin");
            var btn = DshWidgets.SmallButton("尝试读取", "primary");
            var es = DshWidgets.EmptyState(title, next, btn);
            btn.Click += (_, _) => { _dshPluginCache.Clear(); RenderDshUnified(); };
            panel.Children.Add(es);
            return panel;
        }

        var kw = SearchText;
        var nameSet = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in columns)
            foreach (var p in GetPlugins(t)) nameSet.Add(p.Name);

        var shown = nameSet
            .Where(n => kw.Length == 0 || n.Contains(kw, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => DshFavorites.IsStarred(DshFavorites.PluginRowKey(n)) ? 0 : 1)
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (shown.Count == 0)
        {
            panel.Children.Add(DshWidgets.EmptyState("没有匹配「" + kw + "」的插件。", "请更换关键词，或清空搜索框。"));
            return panel;
        }

        var head = new Grid { Margin = new Thickness(0, 10, 0, 6) };
        DefineDshColumns(head, columns.Length);
        var lbl = DshWidgets.Text("插件名", 11, DshTextSecondary, FontWeights.SemiBold);
        lbl.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(lbl, 0);
        head.Children.Add(lbl);
        for (int i = 0; i < columns.Length; i++)
        {
            // 这里原本是 "\uE774" / "\uE7F4" 两个 Segoe MDL2 图标 —— 字形在用户机器上
            // 落到了缺字区，渲染成一个方框，等于表头什么都没写。表头改回文字，
            // 不依赖任何图标字体（用户反馈「表头图标是乱码」）。
            var n = DshWidgets.Text(titles[i], 10.5, DshTextSecondary, FontWeights.SemiBold);
            n.HorizontalAlignment = HorizontalAlignment.Center;
            n.VerticalAlignment = VerticalAlignment.Center;
            n.ToolTip = TargetName(columns[i]) + " 的插件加载清单";
            Grid.SetColumn(n, i + 1);
            head.Children.Add(n);
        }
        panel.Children.Add(head);
        panel.Children.Add(DshWidgets.Rule());

        foreach (string name in shown)
        {
            var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            DefineDshColumns(row, columns.Length);

            var nameBox = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 3, 8, 3) };
            var titleLine = new StackPanel { Orientation = Orientation.Horizontal };
            titleLine.Children.Add(BuildDshStarButton(DshFavorites.PluginRowKey(name), DisplayNameOf(name)));
            var title = DshWidgets.Text(DisplayNameOf(name), 12, DshTextPrimary);
            title.VerticalAlignment = VerticalAlignment.Center;
            title.ToolTip = name;
            titleLine.Children.Add(title);
            nameBox.Children.Add(titleLine);

            var any = columns.Select(t => GetPlugins(t).FirstOrDefault(p =>
                p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(p => p != null);
            if (any != null)
            {
                if (any.Compatibility == PluginManager.Compat.Broken)
                {
                    var warn = DshWidgets.Text("⛔ 版本对不上", 9.5, DshError);
                    warn.ToolTip = "该插件要求 " + any.Requirement + "，当前 DSH 不满足，启用后可能无法正常工作。";
                    nameBox.Children.Add(warn);
                }
                else if (any.Compatibility == PluginManager.Compat.Partial)
                {
                    var warn = DshWidgets.Text("🟡 面向别的版本", 9.5, DshWarn);
                    warn.ToolTip = "该插件面向 " + any.RequirementTarget + "，当前 DSH 为其他版本，可能出现兼容问题。";
                    nameBox.Children.Add(warn);
                }
            }

            Grid.SetColumn(nameBox, 0);
            row.Children.Add(nameBox);

            for (int i = 0; i < columns.Length; i++)
            {
                var target = columns[i];
                var p = GetPlugins(target).FirstOrDefault(x =>
                    x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

                UIElement cell;
                if (p == null)
                {
                    var dash = DshWidgets.Text("—", 11, DshTextTertiary);
                    dash.HorizontalAlignment = HorizontalAlignment.Center;
                    dash.VerticalAlignment = VerticalAlignment.Center;
                    dash.ToolTip = TargetName(target) + " 里没有这个插件。";
                    cell = dash;
                }
                else cell = BuildDshToggleCell(p, target);

                Grid.SetColumn(cell, i + 1);
                row.Children.Add(cell);
            }

            panel.Children.Add(row);
            panel.Children.Add(DshWidgets.Rule());
        }

        return panel;
    }

    private static string DisplayNameOf(string pkg)
    {
        if (pkg.StartsWith('@') && pkg.Contains('/')) return pkg.Substring(pkg.LastIndexOf('/') + 1);
        return pkg;
    }

    /// <summary>
    /// 插件开关格：仿 cc-switch 的矩阵勾选框。
    ///
    /// 上一版是 20×20 的 Button：勾上=实心蓝、没勾=透明 + 1px 灰边，
    /// 在深色底上两态几乎看不出差别，用户反馈「勾选和取消勾选没有任何视觉变动，
    /// 对勾也没有真的取消」。现在：
    ///   · 22×22、圆角 4；
    ///   · 勾上 = 蓝底 + 白色实心 ✓；
    ///   · 没勾 = 深灰底 + 明显亮边 + 空槽（不是透明，所以格子始终看得见）；
    ///   · 悬停时未勾状态浮出 55% 透明度的 ✓ 做预告，告诉用户「这里可以点」。
    /// 勾/取消真的会取消由 <see cref="TogglePluginAsync"/> 负责（它在动完之后
    /// 回读磁盘状态核对，不再只看返回文案的前缀）。
    /// </summary>
    private UIElement BuildDshToggleCell(PluginManager.Plugin p, GuardTarget target)
    {
        var offBg = Color.FromRgb(0x22, 0x22, 0x2A);
        var offBorder = Color.FromRgb(0x55, 0x55, 0x62);

        var mark = new TextBlock
        {
            Text = "✓",
            FontSize = 12.5,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var box = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            Child = mark,
        };

        void Paint(bool on, bool hover)
        {
            if (on)
            {
                box.Background = new SolidColorBrush(DshAccent);
                box.BorderBrush = new SolidColorBrush(DshAccent);
                mark.Foreground = new SolidColorBrush(Colors.White);
                mark.Opacity = 1;
            }
            else
            {
                box.Background = new SolidColorBrush(offBg);
                box.BorderBrush = new SolidColorBrush(hover ? DshAccent : offBorder);
                mark.Foreground = new SolidColorBrush(DshTextSecondary);
                mark.Opacity = hover ? 0.55 : 0;   // 空槽：没勾时不留勾，悬停才预告
            }
        }

        Paint(!p.Disabled, false);

        box.MouseEnter += (_, _) => Paint(!p.Disabled, true);
        box.MouseLeave += (_, _) => Paint(!p.Disabled, false);
        box.ToolTip = p.Disabled
            ? TargetName(target) + "：当前未加载（点击开启）"
            : TargetName(target) + "：当前已加载（点击关闭）";
        box.MouseLeftButtonUp += async (_, e) =>
        {
            e.Handled = true;
            await TogglePluginAsync(p, target, on => Paint(on, false));
        };
        return box;
    }

    private List<PluginManager.Plugin> GetPlugins(GuardTarget target)
    {
        if (_dshPluginCache.TryGetValue(target, out var cached)) return cached;

        List<PluginManager.Plugin> list;
        try
        {
            string ver = target == GuardTarget.Desktop ? CompatEngineVersion : _currentDshVersion;
            list = PluginManager.Scan(ver ?? "", target);
        }
        catch (Exception ex)
        {
            Logger.LogError("GetPlugins(" + target + ")", ex);
            list = new List<PluginManager.Plugin>();
        }

        _dshPluginCache[target] = list;
        return list;
    }

    private static string TargetName(GuardTarget t) =>
        t == GuardTarget.Web ? "Web 引擎" : "桌面版";

    /// <summary>
    /// 切换插件启停。全程走 PluginManager 既有链路，**成败以磁盘回读为准**。
    ///
    /// 三处相对上一版的改动：
    /// 1. busy 闸挡下时不再静默 return（这是「点了没反应」的一半原因），改为弹提示；
    /// 2. 先就地翻状态并让调用方把那一格立刻重画（乐观更新），不再等磁盘写完；
    /// 3. 判定成功不再只看返回文案前缀「已重新启用/已启用/已禁用」，而是重扫磁盘
    ///    比对 Disabled 是否真的变成了想要的值 —— 否则文案一变、勾选就假成功。
    /// </summary>
    private async Task TogglePluginAsync(PluginManager.Plugin p, GuardTarget target, Action<bool>? repaint = null)
    {
        if (!TryEnterDshBusy())
        {
            ShowDshToast("上一操作尚未完成，请稍后再试。");
            return;
        }

        string name = DisplayNameOf(p.Name);
        bool wantEnable = p.Disabled;          // 想开 = 当前是停用
        bool original = p.Disabled;

        // 乐观更新：先让用户看见格子变了。写盘失败会在下面回滚。
        p.Disabled = !wantEnable;
        repaint?.Invoke(!p.Disabled);

        try
        {
            var ok = await Task.Run(() =>
            {
                try
                {
                    string? patchDir = target == GuardTarget.Desktop
                        ? GuardPaths.ProfileDirFor(GuardTarget.Desktop)
                        : null;

                    string msg = wantEnable
                        ? PluginManager.Enable(p, force: true, patchDir, target)
                        : PluginManager.Disable(p, patchDir, target);

                    bool said = msg.StartsWith("已重新启用", StringComparison.Ordinal)
                             || msg.StartsWith("已启用", StringComparison.Ordinal)
                             || msg.StartsWith("已禁用", StringComparison.Ordinal);
                    return (Said: said, Message: msg);
                }
                catch (Exception ex)
                {
                    Logger.LogError("TogglePlugin", ex);
                    return (Said: false, Message: ex.Message);
                }
            });

            _dshPluginCache.Remove(target);

            // 回读磁盘：真正决定成败的是「重新扫出来的 Disabled 值」。
            var after = GetPlugins(target).FirstOrDefault(x =>
                x.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase));
            bool landed = after != null && after.Disabled != wantEnable;

            if (landed)
            {
                RefreshUnifiedView();
                ShowDshToast(name + "：" + (wantEnable ? "已打开" : "已关掉") + "。" + DshText.RestartNeeded);
                return;
            }

            // 没落地：把乐观更新撤回去，并说明原因。
            p.Disabled = after?.Disabled ?? original;
            repaint?.Invoke(!p.Disabled);
            RefreshUnifiedView();

            ShowDshToast(ok.Said
                ? name + "：" + Shorten(ok.Message, 120) + "（重扫后状态未变化，请点击「刷新」再次确认）"
                : "无法" + (wantEnable ? "启用" : "停用") + "「" + name + "」。" + Shorten(ok.Message, 160));
        }
        catch (Exception ex)
        {
            Logger.LogError("TogglePluginAsync", ex);
            p.Disabled = original;
            repaint?.Invoke(!p.Disabled);
            ShowDshToast("操作失败：" + ex.Message);
        }
        finally
        {
            ExitDshBusy();
        }
    }

    // 外部工具（MCP）

    private UIElement BuildDshMcpSection()
    {
        var panel = new StackPanel();
        var servers = McpServers();

        if (_mcpFormOpen) panel.Children.Add(BuildDshMcpAddForm());

        if (servers.Count == 0)
        {
            var (title, next) = DshText.Empty("mcp");
            panel.Children.Add(DshWidgets.EmptyState(title, next));
            return panel;
        }

        var kw = SearchText;
        var groups = servers
            .GroupBy(s => s.ProfileId)
            .Select(g => new
            {
                Profile = g.First(),
                All = g.ToList(),
            })
            .Select(g => new
            {
                g.Profile,
                Items = g.All
                    .Where(s => kw.Length == 0
                             || s.ServerName.Contains(kw, StringComparison.OrdinalIgnoreCase)
                             || s.Id.Contains(kw, StringComparison.OrdinalIgnoreCase))
                    .ToList(),
                Starred = g.All.Any(s => DshFavorites.IsStarred(DshFavorites.McpKey(s.ProfileId, s.ServerName))),
            })
            // 有收藏的分组排到前面：用户点了星，就希望下次进来第一眼看到它
            .OrderBy(g => g.Starred ? 0 : 1)
            .ThenBy(g => g.Profile.ProfileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var group in groups)
        {
            var items = group.Items
                .OrderBy(s => DshFavorites.IsStarred(DshFavorites.McpKey(s.ProfileId, s.ServerName)) ? 0 : 1)
                .ThenBy(x => x.ServerName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (items.Count == 0) continue;

            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 5) };
            head.Children.Add(DshWidgets.Text(group.Profile.ProfileName, 11, DshTextSecondary, FontWeights.SemiBold));
            head.Children.Add(new Border { Width = 6 });
            head.Children.Add(DshWidgets.Badge(items.Count + " 个", DshTextTertiary));
            panel.Children.Add(head);

            foreach (var s in items) panel.Children.Add(BuildDshMcpCard(s));
        }

        return panel;
    }

    private UIElement BuildDshMcpCard(DshMcpServer s)
    {
        var inner = new StackPanel();

        var headRow = new StackPanel { Orientation = Orientation.Horizontal };
        headRow.Children.Add(BuildDshStarButton(DshFavorites.McpKey(s.ProfileId, s.ServerName), s.ServerName));
        headRow.Children.Add(DshWidgets.Text(s.ServerName, 12.5, DshTextPrimary, FontWeights.SemiBold));
        headRow.Children.Add(new Border { Width = 8 });

        if (s.Disabled) headRow.Children.Add(DshWidgets.Badge("已停用", DshWarn));
        else if (s.Transport == "streamable-http") headRow.Children.Add(DshWidgets.Badge("网址连接", DshTextTertiary));
        else headRow.Children.Add(DshWidgets.Badge("本机启动", DshTextTertiary));

        if (s.Problem.Length > 0)
        {
            headRow.Children.Add(new Border { Width = 6 });
            headRow.Children.Add(DshWidgets.Badge("配置有问题", DshError));
        }

        inner.Children.Add(headRow);

        string where = s.Transport == "streamable-http"
            ? (string.IsNullOrWhiteSpace(s.Url) ? "没填地址" : s.Url!)
            : (string.IsNullOrWhiteSpace(s.Command) ? "没填启动命令" : Shorten(s.Command!, 70));
        var desc = DshWidgets.Text(where, 10.5, DshTextTertiary);
        desc.ToolTip = s.Endpoint + "\n\n配置文件：" + s.PatchFile;
        inner.Children.Add(desc);

        if (s.Problem.Length > 0)
        {
            var pb = DshWidgets.Hint("⚠ " + s.Problem + " —— DSH 加载时会跳过它，点「编辑」改好。", 10);
            inner.Children.Add(pb);
        }

        var actions = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };

        var testBtn = DshWidgets.SmallButton("测试连接");
        testBtn.ToolTip = "发起一次连接测试，检查该服务当前是否可用。";
        {
            var captured = s;
            testBtn.Click += async (_, _) =>
            {
                if (!TryEnterDshBusy()) return;
                ShowDshToast("正在测试「" + captured.ServerName + "」能不能连上…");
                try
                {
                    string r = await Task.Run(() => DshMcpManager.Probe(captured));
                    ShowDshToast(r);
                }
                catch (Exception ex)
                {
                    Logger.LogError("ProbeMcp", ex);
                    ShowDshToast("测试失败：" + ex.Message);
                }
                finally { ExitDshBusy(refresh: false); }
            };
        }
        actions.Children.Add(testBtn);

        var toggle = DshWidgets.SmallButton(s.Disabled ? "重新启用" : "暂时停用");
        toggle.ToolTip = s.Disabled
            ? "重新打开它。下次启动 DSH 生效。"
            : "停用但保留配置，随时能再打开。";
        {
            var captured = s;
            bool enable = captured.Disabled;
            toggle.Click += (_, _) =>
            {
                if (!TryEnterDshBusy()) return;
                try
                {
                    string msg = DshMcpManager.SetEnabled(captured, enable);
                    ShowDshToast(msg.Length == 0
                        ? "「" + captured.ServerName + "」" + (enable ? "已重新启用" : "已停用") + "。" + DshText.RestartNeeded
                        : msg + " " + DshText.FailureSafeNote);
                }
                catch (Exception ex)
                {
                    Logger.LogError("SetMcpEnabled", ex);
                    ShowDshToast("没改成：" + ex.Message);
                }
                finally
                {
                    ExitDshBusy();
                }
            };
        }
        actions.Children.Add(toggle);

        var edit = DshWidgets.SmallButton("编辑");
        edit.ToolTip = "修改名称、连接地址或启动命令。";
        {
            var captured = s;
            edit.Click += (_, _) => OpenMcpEditor(captured);
        }
        actions.Children.Add(edit);

        // 用户要求：收藏的条目把「删除」收起来，取消收藏之后才能删。
        if (DshFavorites.IsStarred(DshFavorites.McpKey(s.ProfileId, s.ServerName)))
        {
            var locked = DshWidgets.Hint("★ 已收藏：删除入口已隐藏，取消收藏后可用。", 10);
            locked.Margin = new Thickness(0, 4, 0, 0);
            inner.Children.Add(locked);
        }
        else
        {
        var remove = DshWidgets.SmallButton("删除", "danger");
        {
            var captured = s;
            remove.Click += (_, _) =>
            {
                if (DshBusy) return;
                var (title, body) = DshText.ConfirmDeleteMcp(captured.ServerName, captured.ProfileName);
                var ok = MessageBox.Show(this, body, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (ok != MessageBoxResult.OK) return;
                if (!TryEnterDshBusy()) return;

                try
                {
                    string msg = DshMcpManager.Remove(captured);
                    ShowDshToast(msg.Length == 0
                        ? "「" + captured.ServerName + "」已删除。" + DshText.RestartNeeded
                        : msg + " " + DshText.FailureSafeNote);
                }
                catch (Exception ex)
                {
                    Logger.LogError("RemoveMcp", ex);
                    ShowDshToast("删除失败：" + ex.Message);
                }
                finally
                {
                    ExitDshBusy();
                }
            };
        }
        actions.Children.Add(remove);
        }

        inner.Children.Add(actions);

        return DshWidgets.Card(inner, dim: s.Disabled);
    }

    /// <summary>
    /// 编辑已有外部工具：只允许改「名字 / 地址 / 命令」，不改 profile。
    /// 换 profile 等于搬家，得删了重建 —— 否则容易留下半截配置。
    /// </summary>
    private void OpenMcpEditor(DshMcpServer s)
    {
        var sp = new StackPanel { Margin = new Thickness(6) };
        sp.Children.Add(DshWidgets.Text("编辑「" + s.ServerName + "」", 13, DshTextPrimary, FontWeights.SemiBold));

        var where = DshWidgets.Hint("属于：" + s.ProfileName + "　配置文件：" + s.PatchFile, 10);
        where.Margin = new Thickness(0, 6, 0, 10);
        sp.Children.Add(where);

        sp.Children.Add(DshWidgets.Text("名字（DSH 内部用它区分）", 11, DshTextSecondary));
        var (nameHost, nameBox) = DshWidgets.Input(DshText.Form.McpNamePlaceholder, 320);
        nameBox.Text = s.ServerName;
        nameHost.Margin = new Thickness(0, 4, 0, 10);
        sp.Children.Add(nameHost);

        bool isHttp = s.Transport == "streamable-http";

        sp.Children.Add(DshWidgets.Text(isHttp ? "服务地址" : "启动命令", 11, DshTextSecondary));
        var (valHost, valBox) = DshWidgets.Input(
            isHttp ? DshText.Form.McpUrlPlaceholder : DshText.Form.McpCommandPlaceholder, 460);
        valBox.Text = isHttp ? (s.Url ?? "") : BuildCommandLine(s);
        valHost.Margin = new Thickness(0, 4, 0, 6);
        sp.Children.Add(valHost);

        sp.Children.Add(DshWidgets.Hint(isHttp
            ? "改完地址后，DSH 下次启动会连新地址。"
            : "命令和参数用空格分隔。带空格的参数请用英文引号包起来。"));

        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        var save = DshWidgets.SmallButton("保存", "primary");
        save.MinWidth = 84;
        var cancel = DshWidgets.SmallButton("取消");
        cancel.MinWidth = 84;
        bar.Children.Add(save);
        bar.Children.Add(cancel);
        sp.Children.Add(bar);

        var dlg = new Window
        {
            Title = "编辑 MCP 服务",
            Content = sp,
            Owner = this,
            Width = 580,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = new SolidColorBrush(DshWidgets.CardBg),
            ResizeMode = ResizeMode.NoResize,
        };

        cancel.Click += (_, _) => dlg.Close();

        save.Click += (_, _) =>
        {
            if (DshBusy) return;
            string newName = nameBox.Text.Trim();
            string newVal = valBox.Text.Trim();

            if (!System.Text.RegularExpressions.Regex.IsMatch(newName, "^[A-Za-z0-9_-]{1,32}$"))
            {
                MessageBox.Show(this,
                    "名字只能用英文字母、数字、下划线或减号，长度 1 到 32。\n\n例如：github、my_tool、local-1",
                    "名称格式错误", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (newVal.Length == 0)
            {
                MessageBox.Show(this, isHttp ? "服务地址不能为空。" : "启动命令不能为空。",
                    "信息不完整", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var profile = DshProfileRegistry.ById(s.ProfileId);
            if (profile == null)
            {
                ShowDshToast("未找到对应的运行环境，未做改动。");
                return;
            }

            if (!TryEnterDshBusy()) return;
            try
            {
                var draft = new DshMcpDraft
                {
                    ServerName = newName,
                    Transport = s.Transport,
                    Url = isHttp ? newVal : "",
                };
                if (!isHttp)
                {
                    var (cmd, args) = SplitCommandLine(newVal);
                    draft.Command = cmd;
                    draft.Args = args;
                }

                string msg;
                if (string.Equals(newName, s.ServerName, StringComparison.Ordinal))
                {
                    // 名字没变 → 原地改字段（P3-2）：用户的注释、字段顺序、以及界面不认识
                    // 的键（failOnStartupError、headers…）全部保住，id 也不会被重算。
                    msg = DshMcpManager.Update(profile, s, draft);
                    if (msg.Length > 0)
                    {
                        ShowDshToast("修改失败：" + Shorten(msg, 160) + " " + DshText.FailureSafeNote);
                        return;
                    }
                }
                else
                {
                    // 改名 → 同 profile 内 serverName 必须唯一，只能删旧 + 加新。
                    //
                    // ⚠ 这里必须做成「要么改成、要么原样」：删掉旧的之后如果 Add 失败
                    // （名字冲突、磁盘只读、YAML 写不进去…），用户就白丢一条配置。
                    // 所以先拍下旧条目的快照，Add 不成功就按快照写回去，再报错。
                    //
                    // ⚠ 回滚必须带上旧 id（P0-2）：本机真实 id 形如 unified-memory-mcp /
                    // threejs-mcp，并不是 "mcp-" 前缀，让 Add 重新生成会得到另一个 id，
                    // DSH 侧按 id 引用的地方就全对不上了。
                    var old = new DshMcpDraft
                    {
                        ServerName = s.ServerName,
                        Transport = s.Transport,
                        Command = s.Command ?? "",
                        Args = s.Args.ToList(),
                        Url = s.Url ?? "",
                        ToolCallTimeoutMs = s.ToolCallTimeoutMs,
                    };
                    bool oldWasDisabled = s.Disabled;
                    string oldId = s.Id;

                    string removed = DshMcpManager.Remove(s);
                    if (removed.Length > 0)
                    {
                        ShowDshToast("替换旧条目失败：" + removed + " " + DshText.FailureSafeNote);
                        return;
                    }

                    msg = DshMcpManager.Add(profile, draft);
                    if (msg.Length > 0)
                    {
                        string rollback = DshMcpManager.Add(profile, old, oldId);
                        if (rollback.Length == 0)
                        {
                            if (oldWasDisabled)
                            {
                                var restored = DshMcpManager.Scan().FirstOrDefault(x =>
                                    string.Equals(x.ServerName, old.ServerName, StringComparison.Ordinal)
                                    && string.Equals(x.ProfileId, profile.Id, StringComparison.OrdinalIgnoreCase));
                                if (restored != null) DshMcpManager.SetEnabled(restored, false);
                            }
                            ShowDshToast("修改失败：" + Shorten(msg, 140) + " 原配置已恢复至修改前状态。");
                        }
                        else
                        {
                            ShowDshToast("修改失败，且原配置未能自动恢复：" + Shorten(msg, 100)
                                       + " 请到「" + Path.GetFileName(s.PatchFile) + "」的 .bak 备份里手工找回。");
                        }
                        return;
                    }
                }

                ShowDshToast("「" + newName + "」已更新。" + DshText.RestartNeeded);
                dlg.Close();
            }
            catch (Exception ex)
            {
                Logger.LogError("EditMcp", ex);
                ShowDshToast("保存失败：" + ex.Message);
            }
            finally
            {
                ExitDshBusy();
            }
        };

        dlg.ShowDialog();
    }

    private static string BuildCommandLine(DshMcpServer s)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(s.Command)) parts.Add(s.Command!);
        parts.AddRange(s.Args);
        return string.Join(" ", parts.Select(QuoteIfNeeded));
    }

    private static string QuoteIfNeeded(string v)
        => v.Contains(' ') ? "\"" + v + "\"" : v;

    /// <summary>
    /// 把一行命令拆成 command + args。
    ///
    /// 规则（**实测过，不是猜的**）：
    ///   · 空白分隔；英文单/双引号包住的部分算一个参数，可以含空格；
    ///   · 反斜杠是普通字符，Windows 路径原样保留（`C:\a b\node.exe` → 两个参数）；
    ///   · 空引号 <c>""</c> 算一个**空参数**（不是丢弃），因为有些工具确实要收空串；
    ///   · **引号没闭合返回 false**，由调用方报错 —— 静默把整行吞成一个参数会让用户
    ///     得到一条看起来正常、实际根本跑不起来的命令，比报错难查得多。
    /// </summary>
    internal static (string Command, List<string> Args) SplitCommandLine(string line)
        => SplitCommandLine(line, out _);

    /// <summary>同上，额外返回是否解析成功（未闭合引号 = false）。</summary>
    internal static (string Command, List<string> Args) SplitCommandLine(string line, out bool ok)
    {
        var parts = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool inQuote = false;
        char quoteChar = '"';
        bool hasContent = false;      // 当前 token 是否已有内容（用于区分 `""` 与「没有 token」）

        ok = true;

        foreach (char c in line ?? "")
        {
            if (inQuote)
            {
                if (c == quoteChar) { inQuote = false; continue; }
                cur.Append(c);
                hasContent = true;
                continue;
            }
            if (c == '"' || c == '\'')
            {
                inQuote = true;
                quoteChar = c;
                hasContent = true;    // 空引号也算生成了一个 token
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                if (hasContent) { parts.Add(cur.ToString()); cur.Clear(); hasContent = false; }
                continue;
            }
            cur.Append(c);
            hasContent = true;
        }

        if (inQuote) ok = false;      // 引号没闭合：如实报告，不静默吞

        if (hasContent) parts.Add(cur.ToString());

        if (parts.Count == 0) return ("", new List<string>());
        return (parts[0], parts.Skip(1).ToList());
    }

    private UIElement BuildDshMcpAddForm()
    {
        var sp = new StackPanel();

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(DshWidgets.Text("添加 MCP 服务", 12, DshTextPrimary, FontWeights.SemiBold));
        titleRow.Children.Add(new Border { Width = 6 });
        titleRow.Children.Add(DshWidgets.InfoDot("什么是 MCP",
            DshText.Explain("transport") + "\n\n添加后重启 DSH，它的工具就会出现在 DSH 的工具列表里。"));
        sp.Children.Add(titleRow);

        var presetRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        presetRow.Children.Add(LabelWithTip(DshText.Form.McpPreset, DshText.Form.McpPresetTip));
        var presetCombo = DshWidgets.Combo(new[] { "（我自己填）" }.Concat(DshText.Presets.Select(p => p.Display)).ToArray());
        presetCombo.Width = 190;
        presetCombo.Margin = new Thickness(6, 0, 0, 0);
        presetRow.Children.Add(presetCombo);
        sp.Children.Add(presetRow);

        var row2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        row2.Children.Add(LabelWithTip(DshText.Form.McpName, DshText.Form.McpNameTip));
        var (nameHost, nameBox) = DshWidgets.Input(DshText.Form.McpNamePlaceholder, 170);
        nameHost.Margin = new Thickness(6, 0, 12, 0);
        row2.Children.Add(nameHost);

        row2.Children.Add(LabelWithTip(DshText.Form.McpTarget, DshText.Form.McpTargetTip));
        var editable = DshProfileRegistry.Editable().ToList();
        var profileCombo = DshWidgets.Combo(editable.Select(p => p.DisplayName).ToArray());
        profileCombo.Width = 150;
        profileCombo.Margin = new Thickness(6, 0, 0, 0);
        row2.Children.Add(profileCombo);
        sp.Children.Add(row2);

        var row3 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        row3.Children.Add(LabelWithTip(DshText.Form.McpMode, DshText.Form.McpModeTip));
        var modeCombo = DshWidgets.Combo("本机启动（stdio）", "连接网址（http）");
        modeCombo.Width = 190;
        modeCombo.Margin = new Thickness(6, 0, 0, 0);
        row3.Children.Add(modeCombo);
        sp.Children.Add(row3);

        var row4 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var cmdLabel = LabelWithTip(DshText.Form.McpCommand, DshText.Form.McpCommandTip);
        row4.Children.Add(cmdLabel);
        var (cmdHost, cmdBox) = DshWidgets.Input(DshText.Form.McpCommandPlaceholder, 430);
        cmdHost.Margin = new Thickness(6, 0, 0, 0);
        row4.Children.Add(cmdHost);
        var (urlHost, urlBox) = DshWidgets.Input(DshText.Form.McpUrlPlaceholder, 430);
        urlHost.Margin = new Thickness(6, 0, 0, 0);
        urlHost.Visibility = Visibility.Collapsed;
        row4.Children.Add(urlHost);
        sp.Children.Add(row4);

        var modeHint = DshWidgets.Hint("本机启动：填工具的启动命令（工具官网一般会给）。");
        modeHint.Margin = new Thickness(0, 5, 0, 0);
        sp.Children.Add(modeHint);

        modeCombo.SelectionChanged += (_, _) =>
        {
            bool http = modeCombo.SelectedIndex == 1;
            cmdLabel.Visibility = http ? Visibility.Collapsed : Visibility.Visible;
            cmdHost.Visibility = http ? Visibility.Collapsed : Visibility.Visible;
            urlHost.Visibility = http ? Visibility.Visible : Visibility.Collapsed;
            modeHint.Text = http
                ? "连接网址：填已经在跑的 MCP 服务地址，通常以 /mcp 结尾。"
                : "本机启动：填工具的启动命令（工具官网一般会给）。";
        };

        // 预置填充
        presetCombo.SelectionChanged += (_, _) =>
        {
            int idx = presetCombo.SelectedIndex - 1;
            if (idx < 0 || idx >= DshText.Presets.Count) return;

            var p = DshText.Presets[idx];
            nameBox.Text = p.ServerName;
            bool http = p.Transport == "streamable-http";
            modeCombo.SelectedIndex = http ? 1 : 0;
            if (http) urlBox.Text = p.Url;
            else cmdBox.Text = string.Join(" ", new[] { p.Command }.Concat(p.Args).Select(QuoteIfNeeded));

            // 手动触发一次可见性同步（SelectedIndex 未变化时 SelectionChanged 不会触发）
            bool nowHttp = modeCombo.SelectedIndex == 1;
            cmdLabel.Visibility = nowHttp ? Visibility.Collapsed : Visibility.Visible;
            cmdHost.Visibility = nowHttp ? Visibility.Collapsed : Visibility.Visible;
            urlHost.Visibility = nowHttp ? Visibility.Visible : Visibility.Collapsed;
            modeHint.Text = nowHttp
                ? "连接网址：填已经在跑的 MCP 服务地址，通常以 /mcp 结尾。"
                : "本机启动：填工具的启动命令（工具官网一般会给）。";

            if (p.Note.Length > 0) ShowDshToast(p.Note);
        };

        // 按钮行
        var row5 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        var add = DshWidgets.SmallButton("添加", "primary");
        var cancel = DshWidgets.SmallButton("取消");
        row5.Children.Add(add);
        row5.Children.Add(cancel);

        var note = DshWidgets.Hint("添加前会自动备份配置文件；未确定填写内容时可先取消。", 10);
        note.Margin = new Thickness(8, 0, 0, 0);
        note.VerticalAlignment = VerticalAlignment.Center;
        row5.Children.Add(note);
        sp.Children.Add(row5);

        cancel.Click += (_, _) => { _mcpFormOpen = false; RefreshUnifiedView(); };

        add.Click += (_, _) =>
        {
            if (DshBusy) return;

            string name = nameBox.Text.Trim();
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z0-9_-]{1,32}$"))
            {
                MessageBox.Show(this,
                    "名字要用英文字母、数字、下划线或减号，长度 1 到 32。\n\n" +
                    "不要用空格或中文 —— DSH 内部靠它区分不同工具。\n\n" +
                    "例如：github、my_tool、local-1",
                    "名称格式错误", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (profileCombo.SelectedIndex < 0 || profileCombo.SelectedIndex >= editable.Count)
            {
                MessageBox.Show(this, "没有找到可写入的环境。请确认 DSH 至少装过一次并运行过。",
                    "没有可用环境", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            bool http = modeCombo.SelectedIndex == 1;
            string val = http ? urlBox.Text.Trim() : cmdBox.Text.Trim();
            if (val.Length == 0)
            {
                MessageBox.Show(this,
                    http ? "请填服务地址，例如 http://127.0.0.1:8765/mcp"
                         : "请填启动命令，例如 npx -y @modelcontextprotocol/server-github",
                    "信息不完整", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var profile = editable[profileCombo.SelectedIndex];
            var draft = new DshMcpDraft { ServerName = name, Transport = http ? "streamable-http" : "stdio" };

            if (http) draft.Url = val;
            else
            {
                var (cmd, args) = SplitCommandLine(val);
                draft.Command = cmd;
                draft.Args = args;
            }

            if (!TryEnterDshBusy()) return;
            try
            {
                string msg = DshMcpManager.Add(profile, draft);
                if (msg.Length == 0)
                {
                    _mcpFormOpen = false;
                    ShowDshToast("「" + name + "」已添加进 " + profile.DisplayName + "。" + DshText.RestartNeeded);
                }
                else ShowDshToast(msg);
            }
            catch (Exception ex)
            {
                Logger.LogError("AddMcp", ex);
                ShowDshToast("添加失败：" + ex.Message);
            }
            finally
            {
                ExitDshBusy();
            }
        };

        return DshWidgets.Panel(sp);
    }

    private UIElement BuildDshSkillAddForm()
    {
        var sp = new StackPanel();

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(DshWidgets.Text("新建一个技能", 12, DshTextPrimary, FontWeights.SemiBold));
        titleRow.Children.Add(new Border { Width = 6 });
        titleRow.Children.Add(DshWidgets.InfoDot("什么是技能",
            "技能就是一份写给模型的说明书：什么时候该用、怎么做。\n\n"
            + "本功能仅在自建技能目录内生成骨架（frontmatter 与两节正文），"
            + "正文需随后自行编辑；生成后会自动打开该文件。"));
        sp.Children.Add(titleRow);

        var row1 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        row1.Children.Add(LabelWithTip("技能名", "小写字母、数字和单个连字符。这就是文件夹名，也是 DSH 认的标识。"));
        var (nameHost, nameBox) = DshWidgets.Input("my-skill", 200);
        nameHost.Margin = new Thickness(6, 0, 12, 0);
        row1.Children.Add(nameHost);
        var nameHint = DshWidgets.Hint("例：pdf-tools、weekly-report", 10);
        nameHint.VerticalAlignment = VerticalAlignment.Center;
        row1.Children.Add(nameHint);
        sp.Children.Add(row1);

        var row2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        row2.Children.Add(LabelWithTip("一句话说明", "模型靠这句话判断什么时候该自动用它，写清楚干什么。"));
        var (descHost, descBox) = DshWidgets.Input("处理 PDF：拆分、合并、抽表格", 430);
        descHost.Margin = new Thickness(6, 0, 0, 0);
        row2.Children.Add(descHost);
        sp.Children.Add(row2);

        var row3 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        row3.Children.Add(LabelWithTip("什么时候用", "触发场景，可留空，之后再补。"));
        var (whenHost, whenBox) = DshWidgets.Input("用户提到 PDF、报表、抽表格的时候", 430);
        whenHost.Margin = new Thickness(6, 0, 0, 0);
        row3.Children.Add(whenHost);
        sp.Children.Add(row3);

        var row4 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        var create = DshWidgets.SmallButton("创建", "primary");
        var cancel = DshWidgets.SmallButton("取消");
        row4.Children.Add(create);
        row4.Children.Add(cancel);
        var note = DshWidgets.Hint("创建后立即生效，无需重启 DSH。", 10);
        note.Margin = new Thickness(8, 0, 0, 0);
        note.VerticalAlignment = VerticalAlignment.Center;
        row4.Children.Add(note);
        sp.Children.Add(row4);

        cancel.Click += (_, _) => { _skillFormOpen = false; RefreshUnifiedView(); };

        create.Click += (_, _) =>
        {
            if (DshBusy) return;
            string name = nameBox.Text.Trim();
            if (name.Length == 0)
            {
                MessageBox.Show(this, "请先填写技能名称，例如 my-skill。", "信息不完整",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!TryEnterDshBusy()) return;
            try
            {
                string path = DshAuthoring.CreateSkill(name, descBox.Text, whenBox.Text);
                _skillFormOpen = false;
                ShowDshToast("技能已建好：" + path + " " + DshText.TakeEffectImmediately);
                OpenNewFileInEditor(path, "编写技能内容", "上面那段是 DSH 认的头部，别改 name；正文写这个技能该怎么做。");
            }
            catch (IOException ex)
            {
                MessageBox.Show(this, ex.Message, "建不了这个技能",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Logger.LogError("CreateSkill", ex);
                ShowDshToast("创建失败：" + ex.Message);
            }
            finally
            {
                ExitDshBusy();
            }
        };

        return DshWidgets.Panel(sp);
    }

    /// <summary>刚建好的文件直接在壳内编辑窗口里打开，省得让用户去外面找记事本。</summary>
    private void OpenNewFileInEditor(string path, string title, string hint)
    {
        try
        {
            if (!File.Exists(path)) return;

            string body = DshPromptManager.ReadAll(path);
            var win = new DshTextEditorWindow(title, hint, body) { Owner = this };
            if (win.ShowDialog() != true) return;
            if (win.ResultText == body) return;

            string msg = DshPromptManager.Save(path, win.ResultText, DshPromptManager.HasBom(path));
            ShowDshToast(msg.Length == 0 ? "正文已写入。" + DshText.TakeEffectImmediately : msg);
        }
        catch (Exception ex)
        {
            Logger.LogError("OpenNewFileInEditor", ex);
            string err = DshSkillManager.OpenFile(path);
            if (err.Length > 0) ShowDshToast(err);
        }
    }

    private UIElement BuildDshSkillImportForm()
    {
        var sp = new StackPanel();

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(DshWidgets.Text("从 GitHub 仓库导入技能", 12, DshTextPrimary, FontWeights.SemiBold));
        titleRow.Children.Add(new Border { Width = 6 });
        titleRow.Children.Add(DshWidgets.InfoDot("导入规则",
            "只读公开仓库里的 SKILL.md（最多 40 个），一个技能落一个文件夹，"
            + "已存在的同名文件夹不覆盖。\n\n只下载文本，不执行仓库里的任何代码。"));
        sp.Children.Add(titleRow);

        var row1 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        row1.Children.Add(LabelWithTip("仓库地址", "owner/repo、https://github.com/owner/repo、带 /tree/分支 都认。"));
        var (urlHost, urlBox) = DshWidgets.Input("https://github.com/owner/repo", 430);
        urlHost.Margin = new Thickness(6, 0, 0, 0);
        row1.Children.Add(urlHost);
        sp.Children.Add(row1);

        var row2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        var go = DshWidgets.SmallButton("开始导入", "primary");
        var cancel = DshWidgets.SmallButton("取消");
        row2.Children.Add(go);
        row2.Children.Add(cancel);
        var status = DshWidgets.Hint("仓库必须是公开的；私有仓库这里读不到。", 10);
        status.Margin = new Thickness(8, 0, 0, 0);
        status.VerticalAlignment = VerticalAlignment.Center;
        row2.Children.Add(status);
        sp.Children.Add(row2);

        cancel.Click += (_, _) => { _skillImportOpen = false; RefreshUnifiedView(); };

        go.Click += async (_, _) =>
        {
            if (DshBusy) return;
            string spec = urlBox.Text.Trim();
            if (spec.Length == 0)
            {
                MessageBox.Show(this,
                    "请填写仓库地址，例如 https://github.com/owner/repo",
                    "信息不完整", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!TryEnterDshBusy()) return;
            go.IsEnabled = false;
            status.Text = "正在读仓库的文件清单…";
            try
            {
                var result = await DshAuthoring.ImportFromGitHubAsync(spec, CancellationToken.None);
                status.Text = result.Describe();
                if (result.Imported > 0)
                {
                    _skillImportOpen = false;
                    ShowDshToast(result.Describe() + " " + DshText.TakeEffectImmediately);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("ImportSkillsFromGitHub", ex);
                status.Text = "导入失败：" + ex.Message;
                ShowDshToast("导入失败：" + ex.Message);
            }
            finally
            {
                go.IsEnabled = true;
                ExitDshBusy();
            }
        };

        return DshWidgets.Panel(sp);
    }

    private UIElement BuildDshPromptAddForm()
    {
        var sp = new StackPanel();

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(DshWidgets.Text("新建一个提示词", 12, DshTextPrimary, FontWeights.SemiBold));
        titleRow.Children.Add(new Border { Width = 6 });
        titleRow.Children.Add(DshWidgets.InfoDot("提示词是什么",
            "提示词就是一段会被注入给模型的固定文字：规矩、偏好、项目背景。\n\n"
            + "生成的提示词文件与 DSH 自带的提示词注入、长期记忆存放于同一目录，"
            + "创建完成后会自动打开该文件以供编辑正文。"));
        sp.Children.Add(titleRow);

        var row1 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        row1.Children.Add(LabelWithTip("文件名", "中文也行，会存成同名 .md。重名自动加 -2、-3。"));
        var (nameHost, nameBox) = DshWidgets.Input("我的偏好", 220);
        nameHost.Margin = new Thickness(6, 0, 12, 0);
        row1.Children.Add(nameHost);
        var hint = DshWidgets.Hint("写入自建目录，保存后立即生效。", 10);
        hint.VerticalAlignment = VerticalAlignment.Center;
        row1.Children.Add(hint);
        sp.Children.Add(row1);

        var row2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        var create = DshWidgets.SmallButton("创建", "primary");
        var cancel = DshWidgets.SmallButton("取消");
        row2.Children.Add(create);
        row2.Children.Add(cancel);
        sp.Children.Add(row2);

        cancel.Click += (_, _) => { _promptFormOpen = false; RefreshUnifiedView(); };

        create.Click += (_, _) =>
        {
            if (DshBusy) return;
            string name = nameBox.Text.Trim();
            if (name.Length == 0)
            {
                MessageBox.Show(this, "请先填写提示词名称，例如 my-rules。", "信息不完整",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!TryEnterDshBusy()) return;
            try
            {
                string path = DshAuthoring.CreateCustomPrompt(name);
                _promptFormOpen = false;
                ShowDshToast("提示词已建好：" + path + " " + DshText.TakeEffectImmediately);
                OpenNewFileInEditor(path, "编写提示词内容", "将注入模型的正文写在此处，保存后立即生效。");
            }
            catch (IOException ex)
            {
                MessageBox.Show(this, ex.Message, "建不了这个提示词",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Logger.LogError("CreateCustomPrompt", ex);
                ShowDshToast("创建失败：" + ex.Message);
            }
            finally
            {
                ExitDshBusy();
            }
        };

        return DshWidgets.Panel(sp);
    }

    private static UIElement LabelWithTip(string label, string tip)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var t = DshWidgets.Text(label, 11, DshTextSecondary);
        t.VerticalAlignment = VerticalAlignment.Center;
        sp.Children.Add(t);
        if (!string.IsNullOrEmpty(tip))
        {
            sp.Children.Add(new Border { Width = 4 });
            sp.Children.Add(DshWidgets.InfoDot(label, tip));
        }
        return sp;
    }

    // 技能

    private UIElement BuildDshSkillSection()
    {
        var panel = new StackPanel();
        var skills = Skills();

        if (_skillFormOpen) panel.Children.Add(BuildDshSkillAddForm());
        if (_skillImportOpen) panel.Children.Add(BuildDshSkillImportForm());

        if (skills.Count == 0)
        {
            var (title, next) = DshText.Empty("skill");
            panel.Children.Add(DshWidgets.EmptyState(title, next));
            return panel;
        }

        var kw = SearchText;
        var filtered = skills
            .Where(s => kw.Length == 0
                     || s.DisplayName.Contains(kw, StringComparison.OrdinalIgnoreCase)
                     || s.Description.Contains(kw, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (filtered.Count == 0)
        {
            panel.Children.Add(DshWidgets.EmptyState("没有匹配「" + kw + "」的技能。", "请更换关键词，或清空搜索框。"));
            return panel;
        }

        var groups = filtered
            .GroupBy(s => s.SourceLabel)
            .Select(g => new
            {
                Label = g.Key,
                Items = g.ToList(),
                Starred = g.Any(s => DshFavorites.IsStarred(DshFavorites.SkillKey(s.FullPath))),
            })
            .OrderBy(g => g.Starred ? 0 : 1)
            .ThenBy(g => g.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var group in groups)
        {
            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 5) };
            head.Children.Add(DshWidgets.Text(group.Label, 11, DshTextSecondary, FontWeights.SemiBold));
            head.Children.Add(new Border { Width = 6 });
            head.Children.Add(DshWidgets.Badge(group.Items.Count + " 个", DshTextTertiary));
            panel.Children.Add(head);

            foreach (var s in group.Items
                .OrderBy(x => DshFavorites.IsStarred(DshFavorites.SkillKey(x.FullPath)) ? 0 : 1)
                .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase))
                panel.Children.Add(BuildDshSkillCard(s));
        }

        return panel;
    }

    private UIElement BuildDshSkillCard(DshSkill s)
    {
        var inner = new StackPanel();

        var headRow = new StackPanel { Orientation = Orientation.Horizontal };
        headRow.Children.Add(BuildDshStarButton(DshFavorites.SkillKey(s.FullPath), s.DisplayName));
        headRow.Children.Add(DshWidgets.Text(s.DisplayName, 12, DshTextPrimary, FontWeights.SemiBold));

        bool editable = s.Writable && !s.IsPlugin;
        if (editable)
        {
            if (!s.ModelVisible && !s.UserInvocable) headRow.Children.Add(DshWidgets.Badge("已关闭", DshWarn));
            else if (!s.ModelVisible) headRow.Children.Add(DshWidgets.Badge("仅手动用", DshTextTertiary));
            else if (!s.UserInvocable) headRow.Children.Add(DshWidgets.Badge("仅自动用", DshTextTertiary));
        }
        if (!s.Valid)
        {
            headRow.Children.Add(new Border { Width = 6 });
            headRow.Children.Add(DshWidgets.Badge("DSH 会忽略它", DshError));
        }
        headRow.Children.Add(new Border { Width = 6 });
        headRow.Children.Add(DshWidgets.Badge(s.SourceLabel, DshTextTertiary));
        inner.Children.Add(headRow);

        if (s.Description.Length > 0)
        {
            var d = DshWidgets.Text(s.Description, 10.5, DshTextTertiary);
            d.ToolTip = s.Description;
            inner.Children.Add(d);
        }

        if (!s.Valid)
        {
            var pb = DshWidgets.Hint("⚠ " + s.Problem + "。补齐文件中的名称与说明后即可使用。", 10);
            pb.Margin = new Thickness(0, 3, 0, 0);
            inner.Children.Add(pb);
        }

        var actions = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };

        if (editable)
        {
            var modelBtn = DshWidgets.SmallButton(s.ModelVisible ? "模型能用 ✓" : "模型不能用");
            modelBtn.ToolTip = DshText.Explain("disable-model-invocation");
            {
                var captured = s;
                bool next = !captured.ModelVisible;
                modelBtn.Click += (_, _) =>
                {
                    if (!TryEnterDshBusy()) return;
                    try
                    {
                        string msg = DshSkillManager.SetModelVisible(captured, next);
                        ShowDshToast(msg.Length == 0
                            ? "「" + captured.DisplayName + "」：模型" + (next ? "现在能" : "现在不能") + "自己用它。" + DshText.TakeEffectImmediately
                            : msg);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError("SetSkillModelVisible", ex);
                        ShowDshToast("没改成：" + ex.Message);
                    }
                    finally
                    {
                        ExitDshBusy();
                    }
                };
            }
            actions.Children.Add(modelBtn);

            var cmdBtn = DshWidgets.SmallButton(s.UserInvocable ? "手动能喊 ✓" : "手动喊不到");
            cmdBtn.ToolTip = DshText.Explain("user-invocable");
            {
                var captured = s;
                bool next = !captured.UserInvocable;
                cmdBtn.Click += (_, _) =>
                {
                    if (!TryEnterDshBusy()) return;
                    try
                    {
                        string msg = DshSkillManager.SetUserInvocable(captured, next);
                        ShowDshToast(msg.Length == 0
                            ? "「" + captured.DisplayName + "」：" + (next ? "已经能" : "已经不能") + "手动喊它了。" + DshText.TakeEffectImmediately
                            : msg);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError("SetSkillUserInvocable", ex);
                        ShowDshToast("没改成：" + ex.Message);
                    }
                    finally
                    {
                        ExitDshBusy();
                    }
                };
            }
            actions.Children.Add(cmdBtn);
        }

        var open = DshWidgets.SmallButton("打开看内容");
        open.ToolTip = "用系统默认编辑器打开该技能文件，保存后生效。";
        {
            string path = s.FullPath;
            open.Click += (_, _) =>
            {
                string msg = DshSkillManager.OpenFile(path);
                if (msg.Length > 0) ShowDshToast(msg);
            };
        }
        actions.Children.Add(open);

        var reveal = DshWidgets.SmallButton("定位文件");
        {
            string path = s.FullPath;
            reveal.Click += (_, _) => RevealInExplorer(path);
        }
        actions.Children.Add(reveal);

        if (!editable)
        {
            var ro = DshWidgets.Hint(s.IsPlugin ? "由插件自带，无法在 DSHGuard 内修改。" : "所在目录只读，无法在 DSHGuard 内修改。", 10);
            ro.Margin = new Thickness(8, 0, 0, 0);
            ro.VerticalAlignment = VerticalAlignment.Center;
            actions.Children.Add(ro);
        }

        // 自己建的技能才有删除键；收藏中的不给删（需求：取消收藏之后才能删）
        if (editable)
        {
            bool starred = DshFavorites.IsStarred(DshFavorites.SkillKey(s.FullPath));
            if (!starred)
            {
                var del = DshWidgets.SmallButton("删除", "danger");
                del.ToolTip = "删除整个技能目录，DSH 下次启动时不再加载。";
                {
                    var captured = s;
                    del.Click += (_, _) =>
                    {
                        if (DshBusy) return;
                        var ok = MessageBox.Show(this,
                            "要删掉「" + captured.DisplayName + "」吗？\n\n"
                            + "会删掉它自己的文件夹（连同里面的 SKILL.md 和附属文件）：\n"
                            + captured.FullPath + "\n\n"
                            + "删了不能撤销。",
                            "删除技能", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                        if (ok != MessageBoxResult.OK) return;
                        if (!TryEnterDshBusy()) return;
                        try
                        {
                            string msg = DshAuthoring.DeleteSkill(captured);
                            ShowDshToast(msg.Length == 0
                                ? "「" + captured.DisplayName + "」已删除。" + DshText.TakeEffectImmediately
                                : msg + " " + DshText.FailureSafeNote);
                        }
                        catch (Exception ex)
                        {
                            Logger.LogError("DeleteSkill", ex);
                            ShowDshToast("删除失败：" + ex.Message);
                        }
                        finally
                        {
                            ExitDshBusy();
                        }
                    };
                }
                actions.Children.Add(del);
            }
            else
            {
                var locked = DshWidgets.Hint("★ 已收藏：删除入口已隐藏，取消收藏后可用。", 10);
                locked.Margin = new Thickness(0, 4, 0, 0);
                inner.Children.Add(locked);
            }
        }

        inner.Children.Add(actions);

        return DshWidgets.Card(inner, dim: !s.ModelVisible && !s.UserInvocable);
    }

    // 提示词

    private UIElement BuildDshPromptSection()
    {
        var panel = new StackPanel();
        var prompts = Prompts();
        var hints = DshPromptManager.Known().ToDictionary(k => k.Id, k => k.Hint, StringComparer.OrdinalIgnoreCase);

        if (_promptFormOpen) panel.Children.Add(BuildDshPromptAddForm());

        if (prompts.Count == 0)
        {
            var (title, next) = DshText.Empty("prompt");
            panel.Children.Add(DshWidgets.EmptyState(title, next));
            return panel;
        }

        var kw = SearchText;
        var shown = prompts
            .Where(p => kw.Length == 0
                     || p.DisplayName.Contains(kw, StringComparison.OrdinalIgnoreCase)
                     || p.Path.Contains(kw, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => DshFavorites.IsStarred(DshFavorites.PromptKey(p.Id)) ? 0 : 1)
            .ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var p in shown)
        {
            var inner = new StackPanel();

            var headRow = new StackPanel { Orientation = Orientation.Horizontal };
            headRow.Children.Add(BuildDshStarButton(DshFavorites.PromptKey(p.Id), p.DisplayName));
            headRow.Children.Add(DshWidgets.Text(p.DisplayName, 12.5, DshTextPrimary, FontWeights.SemiBold));
            headRow.Children.Add(new Border { Width = 8 });
            headRow.Children.Add(DshWidgets.Badge(GuardPaths.HumanSize(p.SizeBytes), DshTextTertiary));
            if (!p.ReadOnly)
            {
                headRow.Children.Add(new Border { Width = 6 });
                headRow.Children.Add(DshWidgets.Badge("自己建的", DshAccent));
            }
            inner.Children.Add(headRow);

            string hintText = hints.TryGetValue(p.Id, out var h) ? h : "";
            if (hintText.Length > 0)
            {
                var hb = DshWidgets.Hint(hintText, 10);
                hb.Margin = new Thickness(0, 3, 0, 0);
                inner.Children.Add(hb);
            }

            if (p.Preview.Length > 0)
            {
                var pv = DshWidgets.Text(p.Preview, 10.5, DshTextTertiary);
                pv.ToolTip = "开头一段的内容";
                inner.Children.Add(pv);
            }

            var actions = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };

            var edit = DshWidgets.SmallButton("编辑内容", "primary");
            edit.ToolTip = "打开编辑窗口，保存前自动备份上一版本。";
            {
                var captured = p;
                edit.Click += (_, _) =>
                {
                    bool bom = DshPromptManager.HasBom(captured.Path);
                    string body = DshPromptManager.ReadAll(captured.Path);
                    long baseSize = body.Length;

                    string title = "编辑：" + captured.DisplayName;
                    string hint = hintText;
                    string runtimeWarn = DshRuntime.WriteWarning("文件");
                    if (runtimeWarn.Length > 0) hint = hint + "\n" + runtimeWarn;

                    var win = new DshTextEditorWindow(title, hint, body) { Owner = this };
                    bool? saved = win.ShowDialog();
                    if (saved != true) return;

                    if (win.ResultText == body)
                    {
                        ShowDshToast("内容没有变化，没有写入。");
                        return;
                    }

                    // 并发保护（评审 P1-6）：DSH 运行时也会写 MEMORY.md，
                    // 编辑期间它可能追加过内容；直接覆盖会让那条记忆无声消失。
                    if (File.Exists(captured.Path))
                    {
                        long diskSize;
                        try { diskSize = new FileInfo(captured.Path).Length; }
                        catch { diskSize = baseSize; }

                        // 用「磁盘现状」与「打开时的内容」比对：不一致说明被别的程序改过
                        string diskNow = DshPromptManager.ReadAll(captured.Path);
                        if (diskNow != body)
                        {
                            var (t, b) = DshText.WarnPromptOverwritten(captured.DisplayName, diskSize, baseSize);
                            var ans = MessageBox.Show(this, b, t, MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                            if (ans != MessageBoxResult.OK)
                            {
                                ShowDshToast("已放弃保存，编辑器里的内容没有写出去。");
                                return;
                            }
                        }
                    }

                    string msg = DshPromptManager.Save(captured.Path, win.ResultText, bom);
                    ShowDshToast(msg.Length == 0
                        ? "「" + captured.DisplayName + "」已保存。" + DshText.TakeEffectImmediately
                        : msg);
                    RefreshUnifiedView();
                };
            }
            actions.Children.Add(edit);

            var reveal = DshWidgets.SmallButton("定位文件");
            {
                string path = p.Path;
                reveal.Click += (_, _) => RevealInExplorer(path);
            }
            actions.Children.Add(reveal);

            // 自己建的提示词才能删；收藏中的不给删
            if (!p.ReadOnly)
            {
                bool starredPrompt = DshFavorites.IsStarred(DshFavorites.PromptKey(p.Id));
                if (!starredPrompt)
                {
                    var del = DshWidgets.SmallButton("删除", "danger");
                    del.ToolTip = "删除该提示词文件。";
                    {
                        var captured = p;
                        del.Click += (_, _) =>
                        {
                            if (DshBusy) return;
                            var ok = MessageBox.Show(this,
                                "要删掉自建提示词「" + captured.DisplayName + "」吗？\n\n"
                                + captured.Path + "\n\n"
                                + "只删这个文件，DSH 自己的提示词注入和长期记忆不动。删了不能撤销。",
                                "删除提示词", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                            if (ok != MessageBoxResult.OK) return;
                            if (!TryEnterDshBusy()) return;
                            try
                            {
                                string msg = DshAuthoring.DeleteCustomPrompt(captured.Path);
                                ShowDshToast(msg.Length == 0
                                    ? "「" + captured.DisplayName + "」已删除。" + DshText.TakeEffectImmediately
                                    : msg);
                            }
                            catch (Exception ex)
                            {
                                Logger.LogError("DeleteCustomPrompt", ex);
                                ShowDshToast("删除失败：" + ex.Message);
                            }
                            finally
                            {
                                ExitDshBusy();
                            }
                        };
                    }
                    actions.Children.Add(del);
                }
                else
                {
                    var locked = DshWidgets.Hint("★ 已收藏：删除入口已隐藏，取消收藏后可用。", 10);
                    locked.Margin = new Thickness(0, 4, 0, 0);
                    inner.Children.Add(locked);
                }
            }

            // 路径可能很长：单行裁尾，完整路径放 ToolTip（见 DshWidgets.PathLine 的说明）
            var pathText = DshWidgets.PathLine(p.Path, 340);
            pathText.Margin = new Thickness(8, 0, 0, 0);
            pathText.VerticalAlignment = VerticalAlignment.Center;
            actions.Children.Add(pathText);

            inner.Children.Add(actions);

            panel.Children.Add(DshWidgets.Card(inner));
        }

        return panel;
    }

    // 会话

    private UIElement BuildDshSessionSection()
    {
        var panel = new StackPanel();
        var sessions = Sessions();

        if (sessions.Count == 0)
        {
            var (title, next) = DshText.Empty("session");
            panel.Children.Add(DshWidgets.EmptyState(title, next));
            return panel;
        }

        var kw = SearchText;
        var filtered = sessions
            .Where(s => kw.Length == 0
                     || s.Title.Contains(kw, StringComparison.OrdinalIgnoreCase)
                     || s.ProjectName.Contains(kw, StringComparison.OrdinalIgnoreCase)
                     || s.SessionId.Contains(kw, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (filtered.Count == 0)
        {
            panel.Children.Add(DshWidgets.EmptyState("没有匹配「" + kw + "」的会话。", "请更换关键词，或清空搜索框。"));
            return panel;
        }

        panel.Children.Add(BuildDshSessionLane(filtered));

        int noTitle = filtered.Count(s => !s.HasTitle);
        var footRow = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var stat = DshWidgets.Text(
            "共 " + filtered.Count + " 条"
            + (noTitle > 0 ? " · " + noTitle + " 条没有标题（空会话，显示编号）" : ""), 10, DshTextTertiary);
        stat.Margin = new Thickness(0, 4, 8, 0);
        footRow.Children.Add(stat);
        panel.Children.Add(footRow);

        return panel;
    }

    /// <summary>会话列表，内部按项目分组，子代理会话挂在父会话下面。</summary>
    private UIElement BuildDshSessionLane(List<DshSession> items)
    {
        var wrap = new StackPanel();

        if (items.Count == 0)
        {
            var none = DshWidgets.Hint("暂时没有可显示的会话。", 10);
            none.Margin = new Thickness(4, 2, 0, 6);
            wrap.Children.Add(none);
            return wrap;
        }

        var groups = items
            .GroupBy(s => s.ProjectName)
            .Select(g => new
            {
                Project = g.Key,
                First = g.First(),
                Items = g.ToList(),
                Starred = g.Any(s => DshFavorites.IsStarred(DshFavorites.SessionKey(s.SessionId))),
            })
            .OrderBy(g => g.Starred ? 0 : 1)
            .ThenByDescending(g => g.Items.Count)
            .ToList();

        foreach (var group in groups)
        {
            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 4) };
            var proj = DshWidgets.Text(group.Project, 11, DshTextSecondary, FontWeights.SemiBold);
            proj.ToolTip = group.First.ProjectDir;
            head.Children.Add(proj);
            head.Children.Add(new Border { Width = 6 });
            head.Children.Add(DshWidgets.Badge(group.Items.Count + " 条", DshTextTertiary));
            head.Children.Add(new Border { Width = 6 });
            head.Children.Add(DshWidgets.Badge(GuardPaths.HumanSize(group.Items.Sum(s => s.SizeBytes)), DshTextTertiary));
            wrap.Children.Add(head);

            var all = group.Items
                .OrderBy(x => DshFavorites.IsStarred(DshFavorites.SessionKey(x.SessionId)) ? 0 : 1)
                .ThenByDescending(x => x.LastWriteUnix)
                .ToList();

            var byId = new HashSet<string>(all.Select(x => x.SessionId), StringComparer.OrdinalIgnoreCase);
            var kidsOf = new Dictionary<string, List<DshSession>>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in all.Where(x => x.IsChild))
            {
                if (!kidsOf.TryGetValue(c.ParentId, out var bucket))
                {
                    bucket = new List<DshSession>();
                    kidsOf[c.ParentId] = bucket;
                }
                bucket.Add(c);
            }

            int emitted = 0;
            var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in all)
            {
                if (emitted >= 60) break;
                if (!done.Add(s.SessionId)) continue;

                // 父会话不在本段里时，子会话没有可挂的父，就自己当一条显示。
                if (s.IsChild && byId.Contains(s.ParentId)) continue;

                var kids = kidsOf.TryGetValue(s.SessionId, out var found) ? found : new List<DshSession>();
                wrap.Children.Add(BuildDshSessionCard(s, kids));
                emitted++;

                foreach (var kid in kids)
                {
                    if (emitted >= 60) break;
                    if (!done.Add(kid.SessionId)) continue;
                    wrap.Children.Add(BuildDshSessionCard(kid, new List<DshSession>()));
                    emitted++;
                }
            }

            // 兜底：父会话被筛掉、自己却留在这个分组里的子会话，别让它消失。
            foreach (var s in all)
            {
                if (emitted >= 60) break;
                if (!done.Add(s.SessionId)) continue;
                var kids = kidsOf.TryGetValue(s.SessionId, out var found) ? found : new List<DshSession>();
                wrap.Children.Add(BuildDshSessionCard(s, kids));
                emitted++;
            }

            if (all.Count > 60)
            {
                var more = DshWidgets.Hint("这个项目还有 " + (all.Count - 60) + " 条更早的会话没显示，用上面的搜索框找。", 10);
                more.Margin = new Thickness(4, 2, 0, 6);
                wrap.Children.Add(more);
            }
        }

        return wrap;
    }

    private UIElement BuildDshSessionCard(DshSession s, List<DshSession> children)
    {
        var inner = new StackPanel();

        var titleRow = new DockPanel { LastChildFill = true };
        titleRow.Children.Add(BuildDshStarButton(DshFavorites.SessionKey(s.SessionId), s.DisplayName));

        string? report = DshSessionManager.FindReport(s);
        if (report != null)
        {
            string reportDir = Path.GetDirectoryName(report) ?? "";
            var folder = DshWidgets.SmallButton("📁");
            folder.ToolTip = "该会话的报告位于：" + reportDir;
            folder.Margin = new Thickness(8, 1, 0, 0);
            DockPanel.SetDock(folder, Dock.Right);
            folder.Click += (_, _) => RevealInExplorer(reportDir);
            titleRow.Children.Add(folder);
        }

        var titleText = DshWidgets.Text(s.DisplayName, 12.5, DshTextPrimary, FontWeights.SemiBold);
        titleText.TextDecorations = report != null ? TextDecorations.Underline : null;
        titleText.TextTrimming = TextTrimming.CharacterEllipsis;
        titleText.MaxWidth = report != null ? 396 : 440;
        titleText.Cursor = Cursors.Hand;
        titleText.ToolTip = "点击导出该会话的报告并打开；若本地已有报告则直接复用，不重复导出。"
            + (s.HasTitle ? "" : Environment.NewLine + "（该会话无标题，此处显示会话编号）");
        titleText.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            DownloadSessionLog(s);
        };
        titleRow.Children.Add(titleText);
        if (!s.HasTitle)
        {
            titleRow.Children.Add(new Border { Width = 6 });
            titleRow.Children.Add(DshWidgets.Badge("空会话", DshTextTertiary));
        }
        if (s.IsChild)
        {
            titleRow.Children.Add(new Border { Width = 6 });
            var childBadge = DshWidgets.Badge("子代理", DshTextTertiary);
            childBadge.ToolTip = "该会话由其他会话派发产生，非手动创建。";
            titleRow.Children.Add(childBadge);
        }
        if (children.Count > 0)
        {
            titleRow.Children.Add(new Border { Width = 6 });
            var kidsBadge = DshWidgets.Badge("派了 " + children.Count + " 个", DshTextTertiary);
            kidsBadge.ToolTip = "子代理会话：" + string.Join("、", children.Select(c => c.ShortId));
            titleRow.Children.Add(kidsBadge);
        }
        inner.Children.Add(titleRow);

        var headRow = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        headRow.Children.Add(DshWidgets.Text(s.DisplayTime, 10.5, DshTextTertiary));
        headRow.Children.Add(new Border { Width = 8 });
        headRow.Children.Add(DshWidgets.Text(s.ShortId, 11, DshTextSecondary));
        headRow.Children.Add(new Border { Width = 8 });
        headRow.Children.Add(DshWidgets.Badge(GuardPaths.HumanSize(s.SizeBytes), DshTextTertiary));
        if (s.IsChild)
        {
            headRow.Children.Add(new Border { Width = 8 });
            string from = s.ParentLabel.Length > 0
                ? s.ParentLabel
                : s.ParentId.Substring(0, Math.Min(8, s.ParentId.Length));
            var fromText = DshWidgets.Text("↑ 来自 " + from, 10.5, DshTextTertiary);
            fromText.ToolTip = "父会话：" + s.ParentId;
            headRow.Children.Add(fromText);
        }
        inner.Children.Add(headRow);

        var actions = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };

        var reveal = DshWidgets.SmallButton("定位文件");
        {
            string path = s.FilePath;
            reveal.Click += (_, _) => RevealInExplorer(path);
        }
        actions.Children.Add(reveal);

        var openProj = DshWidgets.SmallButton("打开项目目录");
        openProj.ToolTip = "打开该会话对应的项目目录。";
        {
            var captured = s;
            openProj.Click += (_, _) =>
            {
                string msg = DshSessionManager.RevealProject(captured);
                if (msg.Length > 0) ShowDshToast(msg);
            };
        }
        actions.Children.Add(openProj);

        bool starred = DshFavorites.IsStarred(DshFavorites.SessionKey(s.SessionId));
        if (!starred)
        {
            var remove = DshWidgets.SmallButton("移除", "danger");
            remove.ToolTip = "永久删除该会话，不可恢复；也可先移入回收区。";
            {
                var captured = s;
                remove.Click += (_, _) => RemoveSession(captured);
            }
            actions.Children.Add(remove);
        }

        inner.Children.Add(actions);

        if (starred)
        {
            var locked = DshWidgets.Hint("★ 已收藏：移除入口已隐藏，取消收藏后可用。", 10);
            locked.Margin = new Thickness(0, 4, 0, 0);
            inner.Children.Add(locked);
        }

        return DshWidgets.Card(inner);
    }

    private void DownloadSessionLog(DshSession s)
    {
        if (!TryEnterDshBusy())
        {
            ShowDshToast("上一操作尚未完成，请稍后再试。");
            return;
        }
        try
        {
            var export = DshSessionManager.DownloadLog(s);
            if (export.HasReport)
            {
                string err = DshSessionManager.OpenReport(export.ReportPath);
                if (err.Length == 0)
                {
                    ShowDshToast(export.Reused
                        ? "本地已有导出，直接给你打开：" + export.Dir
                        : "报告已打开。要原始日志就去：" + export.Dir);
                }
                else
                {
                    ShowDshToast(err + " 导出目录：" + export.Dir);
                    RevealInExplorer(export.Dir);
                }
            }
            else
            {
                ShowDshToast("报告没写出来，原始日志已导出到：" + export.Dir);
                RevealInExplorer(export.Dir);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("DownloadSessionLog", ex);
            ShowDshToast("导出失败：" + ex.Message);
        }
        finally
        {
            ExitDshBusy();
        }
    }

    private void RemoveSession(DshSession s)
    {
        if (DshBusy) return;

        var pick = MessageBox.Show(this,
            "「" + s.DisplayName + "」（" + s.ProjectName + "）怎么处理？" + Environment.NewLine + Environment.NewLine
            + "是：丢进回收区 —— 从 DSH 的会话目录里挪走，DSH 里不再显示，之后还能找回来。" + Environment.NewLine
            + "否：直接删掉 —— 彻底删除，找不回来。" + Environment.NewLine
            + "取消：什么都不做。",
            "移除会话", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (pick == MessageBoxResult.Cancel) return;
        bool trashIt = pick == MessageBoxResult.Yes;

        if (!TryEnterDshBusy()) return;
        try
        {
            string msg = trashIt ? DshSessionManager.MoveToTrash(s) : DshSessionManager.Delete(s);
            if (msg.Length == 0)
            {
                _dshSessionCache = null;
                ShowDshToast(trashIt ? "已移入回收区，可通过「打开回收文件夹」找回。" : "已删除。");
                RenderDshUnified();
            }
            else
            {
                ShowDshToast(msg + " " + DshText.FailureSafeNote);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("RemoveSession", ex);
            ShowDshToast("操作失败：" + ex.Message);
        }
        finally
        {
            ExitDshBusy();
        }
    }

    // 工具条：搜索（整行）+ 动作（整行）；摘要写到标题行右侧

    private UIElement BuildDshToolbar(string summary, string searchPlaceholder, UIElement? action = null)
    {
        // 摘要进标题行右端（蓝色小字），不再在工具条里独占一行 —— 少了那一行，
        // 后面的内容区就整体上移，视觉上不会出现一条孤零零的灰字。
        try { UnifiedViewSummary.Text = summary; } catch { }

        var stack = new StackPanel();

        // 两段式：先建好带初值的搜索框，再挂回调（避免闭包自引用与 TextChanged 重入）。
        // 回调只重建内容区 —— 这个搜索框本身在工具条里，不在被重建的那棵树内，所以焦点不会丢。
        // 宽度传 NaN 让搜索框拉伸到整列宽度：这样它和下面按钮行的左右边界严格对齐，
        // 不会出现「搜索框 220、按钮行 306」两条参差的右边界。
        var (searchHost, searchBox) = DshWidgets.SearchBox(searchPlaceholder, double.NaN, SearchText);
        searchBox.TextChanged += (_, _) =>
        {
            if (_dshSearchRefreshing) return;
            SearchText = searchBox.Text;
            RebuildDshBodyOnly();
        };

        searchHost.HorizontalAlignment = HorizontalAlignment.Stretch;
        searchHost.MinHeight = 30;
        searchHost.Margin = new Thickness(0, 0, 0, 8);
        searchBox.VerticalContentAlignment = VerticalAlignment.Center;
        stack.Children.Add(searchHost);

        // 动作区独占一行用 WrapPanel：内容列只有 ~455px，三个按钮合计可能超宽，
        // 超了就整块折到下一行，不会被裁成细缝（DockPanel / Grid 会裁，已实测）。
        if (action != null)
        {
            var row = new WrapPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(action);
            stack.Children.Add(row);
        }

        return stack;
    }

    /// <summary>
    /// 按当前分区生成工具条（搜索框 + 摘要 + 分区专属动作按钮）。
    ///
    /// 摘要用**真实扫描结果**，所以必须在工具条构建时就扫一遍；
    /// 内容区随后复用同一份缓存（<see cref="McpServers"/> 等），不会重复扫。
    /// </summary>
    private UIElement BuildDshToolbarForSection()
    {
        switch (_dshSection)
        {
            case DshResourceKind.Mcp:
            {
                var servers = McpServers();
                int off = servers.Count(s => s.Disabled);
                string summary = servers.Count == 0
                    ? "尚未配置 MCP 服务"
                    : servers.Count + " 个" + (off > 0 ? "（" + off + " 个已停用）" : "") + " · 改动重启 DSH 后生效";

                var addBtn = DshWidgets.SmallButton(_mcpFormOpen ? "收起新增" : "＋ 添加 MCP 服务", "primary");
                addBtn.Click += (_, _) => { _mcpFormOpen = !_mcpFormOpen; RenderDshUnified(); };
                return BuildDshToolbar(summary, "按名字筛选…", addBtn);
            }

            case DshResourceKind.Skill:
            {
                var skills = Skills();
                int editable = skills.Count(s => s.Writable && !s.IsPlugin);
                string summary = skills.Count == 0
                    ? "尚未安装技能"
                    : skills.Count + " 个技能" + (editable > 0 ? "（" + editable + " 个可在此修改）" : "");

                var strip = new StackPanel { Orientation = Orientation.Horizontal };

                var addSkill = DshWidgets.SmallButton(_skillFormOpen ? "收起新建" : "＋ 新建技能", "primary");
                addSkill.ToolTip = "新建技能：填写名称与说明，生成骨架文件后再编写正文。";
                addSkill.Click += (_, _) =>
                {
                    _skillFormOpen = !_skillFormOpen;
                    if (_skillFormOpen) _skillImportOpen = false;
                    RenderDshUnified();
                };
                strip.Children.Add(addSkill);

                var importSkill = DshWidgets.SmallButton(_skillImportOpen ? "收起导入" : "＋ 从 GitHub 导入");
                importSkill.ToolTip = "填写公开仓库地址，批量导入其中的 SKILL.md 文件。";
                importSkill.Click += (_, _) =>
                {
                    _skillImportOpen = !_skillImportOpen;
                    if (_skillImportOpen) _skillFormOpen = false;
                    RenderDshUnified();
                };
                strip.Children.Add(importSkill);

                var openDir = DshWidgets.SmallButton("打开技能目录");
                openDir.ToolTip = "在资源管理器中打开该目录，可手工放入技能目录。";
                openDir.Click += (_, _) =>
                {
                    try
                    {
                        string dir = DshSkillReader.UserAgentsSkillRoot;
                        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                        RevealInExplorer(dir);
                    }
                    catch (Exception ex) { ShowDshToast("打不开目录：" + ex.Message); }
                };
                strip.Children.Add(openDir);

                return BuildDshToolbar(summary, "按名字或说明筛选…", strip);
            }

            case DshResourceKind.Prompt:
            {
                var prompts = Prompts();
                string summary = prompts.Count == 0
                    ? "尚无提示词文件"
                    : prompts.Count + " 个文件 · 保存后立即生效，无需重启";

                var strip = new StackPanel { Orientation = Orientation.Horizontal };

                var addPrompt = DshWidgets.SmallButton(_promptFormOpen ? "收起新建" : "＋ 新建提示词", "primary");
                addPrompt.ToolTip = "新建提示词文件：生成骨架内容后再编写正文。";
                addPrompt.Click += (_, _) => { _promptFormOpen = !_promptFormOpen; RenderDshUnified(); };
                strip.Children.Add(addPrompt);

                var openDir = DshWidgets.SmallButton("打开所在目录");
                openDir.Click += (_, _) => RevealInExplorer(GuardPaths.DshHome);
                strip.Children.Add(openDir);

                return BuildDshToolbar(summary, "按名称筛选…", strip);
            }

            case DshResourceKind.Session:
            {
                var sessions = Sessions();
                int trashed = DshSessionManager.TrashCount();
                string summary = sessions.Count == 0
                    ? "尚无会话记录"
                    : sessions.Count + " 条会话 · 共 " + sessions.Select(s => s.ProjectName).Distinct().Count() + " 个项目"
                      + (trashed > 0 ? " · 回收区已有 " + trashed + " 个" : "");

                var openTrash = DshWidgets.SmallButton("打开回收文件夹");
                openTrash.ToolTip = "已移除的会话存放在此，可从此处恢复。";
                openTrash.Click += (_, _) =>
                {
                    try
                    {
                        string dir = DshSessionManager.TrashRoot;
                        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                        RevealInExplorer(dir);
                    }
                    catch (Exception ex) { ShowDshToast("打不开：" + ex.Message); }
                };
                return BuildDshToolbar(summary, "按项目名或会话编号筛选…", openTrash);
            }

            default:
            {
                var sums = new List<string>();
                foreach (var t in new[] { GuardTarget.Web, GuardTarget.Desktop })
                {
                    var list = GetPlugins(t);
                    int disabled = list.Count(p => p.Disabled);
                    sums.Add(TargetName(t) + " " + list.Count + " 个" + (disabled > 0 ? "（" + disabled + " 个已关）" : ""));
                }
                return BuildDshToolbar(string.Join(" · ", sums), "按名称筛选插件…");
            }
        }
    }

    // 公共

    private void ShowDshToast(string message)
    {
        try
        {
            if (UnifiedToast == null) return;
            UnifiedToast.Text = message ?? "";
            UnifiedToast.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        }
        catch { }
    }

    private void RevealInExplorer(string path)
    {
        try
        {
            // 两个坑都踩过：
            //  1) 手工拼 "/select,\"" + path + "\""：路径以反斜杠结尾时正好把收尾引号转义掉；
            //  2) 把 "/select," 和路径拼成一个 ArgumentList 元素：ArgumentList 遇到含空格的
            //     整体会自动包一层引号，结果 explorer 收到的是 "/select,C:\a b\x.txt" 这一整块，
            //     它不认（/select 必须是独立开关），于是退回到「文档」。
            // 正解是把开关和路径拆成两个元素，路径含空格时由 ArgumentList 单独加引号。
            if (File.Exists(path))
            {
                var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
                psi.ArgumentList.Add("/select,");
                psi.ArgumentList.Add(path);
                Process.Start(psi);
            }
            else if (Directory.Exists(path))
            {
                var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
                psi.ArgumentList.Add(path);
                Process.Start(psi);
            }
            else ShowDshToast("这个位置现在不存在：" + path);
        }
        catch (Exception ex) { Logger.LogError("RevealInExplorer", ex); }
    }

    /// <summary>名称列 Star + MinWidth 160（防压缩），目标列固定 62。</summary>
    private static void DefineDshColumns(Grid grid, int columnCount)
    {
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
            MinWidth = 160,
        });
        for (int i = 0; i < columnCount; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
    }
}
