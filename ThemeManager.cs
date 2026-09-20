using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace DSHGuard;

/// <summary>
/// 日间 / 夜间主题切换。
///   · 界面颜色为固定的硬编码深色值；此处维护一张双向颜色映射表，切换时遍历视觉树，
///     逐项替换 Border/Panel/Control/TextBlock/Shape 的画刷颜色（夜间→日间用正向表，
///     日间→夜间用反向表，往返切换幂等）。
///   · 未改用 DynamicResource 全局画刷：需将数百处硬编码颜色重写为资源引用，改动面与风险过大。
///   · 映射表方案的代价是动态新建的控件需再次刷色，由 <c>ApplySoon()</c> 在切页或重渲染后补刷。
/// </summary>
public static class ThemeManager
{
    /// <summary>当前是否为夜间模式（默认夜间）。</summary>
    public static bool IsDark { get; private set; } = true;

    private static Color C(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
    private static Color A(byte a, byte r, byte g, byte b) => Color.FromArgb(a, r, g, b);

    /// <summary>
    /// 夜间 → 日间 的映射（反向查表即为日间 → 夜间）。
    /// 约束：键集合与值集合必须不相交，否则映射不幂等——若某个目标色同时是另一条的键，
    /// 界面补刷第二次时该颜色会被继续改写，导致文字色偏移。因此所有目标色均避开任何键。
    /// </summary>
    private static readonly Dictionary<Color, Color> DarkToLight = new()
    {
        // ── 文字 ──
        [C(0xF5, 0xF5, 0xF7)] = C(0x1C, 0x1C, 0x1E),   // 主文字
        [C(0xCF, 0xCF, 0xD6)] = C(0x2E, 0x2E, 0x30),   // 导航图标/文字
        [C(0xC7, 0xC7, 0xCC)] = C(0x3C, 0x3C, 0x3E),
        [C(0xA8, 0xA8, 0xB0)] = C(0x4A, 0x4A, 0x4C),
        [C(0x8E, 0x8E, 0x93)] = C(0x6B, 0x6B, 0x70),   // 次要文字
        [C(0x6E, 0x6E, 0x73)] = C(0x90, 0x90, 0x95),   // 三级文字
        [C(0x48, 0x48, 0x4A)] = C(0x8A, 0x8A, 0x8F),
        // ── 强调色（链接蓝在浅色底上需加深）──
        [C(0x5A, 0xC8, 0xFA)] = C(0x00, 0x71, 0xE3),
        [C(0xBF, 0xE3, 0xFB)] = C(0x0B, 0x5F, 0xA5),
        [C(0x8F, 0xB8, 0xD8)] = C(0x3B, 0x6E, 0x9E),
        [C(0xFF, 0x8A, 0x80)] = C(0xD7, 0x00, 0x15),   // 浅红（退出 UI）在浅色底上需加深
        // 日志正文使用 #EDEDF2 一类近白字，由「近白文字按上下文映射」规则处理：
        // 日间转为深色、夜间保持主文字色，无需单独配置映射项。
        // ── 表面：白色半透明 → 浅色（卡片取近白，避免纯白）──
        [A(0x10, 0xFF, 0xFF, 0xFF)] = A(0x99, 0xED, 0xF1, 0xF7),   // 输入框底色（半透明）
        [A(0x12, 0xFF, 0xFF, 0xFF)] = A(0xB3, 0xFF, 0xFF, 0xFF),   // 卡片底色（70% 白，避开纯白以保证文字对比度）
        [A(0x14, 0xFF, 0xFF, 0xFF)] = A(0x14, 0x00, 0x00, 0x00),
        [A(0x18, 0xFF, 0xFF, 0xFF)] = A(0x16, 0x00, 0x00, 0x00),
        [A(0x1A, 0xFF, 0xFF, 0xFF)] = A(0x18, 0x00, 0x00, 0x00),
        [A(0x1F, 0x5A, 0xC8, 0xFA)] = A(0x1F, 0x00, 0x71, 0xE3),
        [A(0x22, 0xFF, 0xFF, 0xFF)] = A(0x1E, 0x00, 0x00, 0x00),
        [A(0x30, 0xFF, 0xFF, 0xFF)] = A(0x26, 0x00, 0x00, 0x00),
        [A(0x33, 0xFF, 0xFF, 0xFF)] = A(0x2A, 0x00, 0x00, 0x00),
        [A(0x40, 0xFF, 0xFF, 0xFF)] = A(0x33, 0x00, 0x00, 0x00),
        [A(0x4D, 0xFF, 0xFF, 0xFF)] = A(0x40, 0x00, 0x00, 0x00),   // 滚动条拇指
        [A(0x24, 0x34, 0xC7, 0x59)] = A(0x30, 0x34, 0xC7, 0x59),   // 「已安装」绿底
        // ── 实心表面 ──
        [C(0x3A, 0x3A, 0x3C)] = C(0xD2, 0xD3, 0xD8),   // 进度条槽 / 灰按钮
        [C(0x1C, 0x20, 0x29)] = C(0xF2, 0xF3, 0xF7),   // 筛选下拉底色（近白，不使用纯白）
        [A(0xE6, 0x1C, 0x20, 0x29)] = A(0xE6, 0xF2, 0xF3, 0xF7),
        [A(0xE8, 0x0A, 0x0A, 0x0E)] = A(0xE8, 0xF2, 0xF2, 0xF7),   // 看图遮罩
        // ── 窗口底色（根 Border 的渐变三段；夜间约三成不透明，日间透明度更高）──
        [A(0x3D, 0x1A, 0x1E, 0x2E)] = A(0x26, 0xE8, 0xED, 0xF5),
        [A(0x35, 0x10, 0x15, 0x1C)] = A(0x20, 0xF5, 0xF7, 0xFA),
        [A(0x3D, 0x16, 0x1B, 0x22)] = A(0x26, 0xEE, 0xF2, 0xF7),
        // 下拉项（日志来源筛选下拉）
        [C(0x2C, 0x31, 0x3B)] = C(0xE3, 0xE7, 0xEE),
        [C(0x25, 0x2B, 0x36)] = C(0xE8, 0xEC, 0xF3),   // 自绘下拉的悬停底色
    };

    /// <summary>返回键与值的交集（自检用：非空表示映射不幂等）。</summary>
    internal static IReadOnlyList<string> OverlappingMapKeys()
    {
        var clash = new List<string>();
        foreach (var v in DarkToLight.Values)
            if (DarkToLight.ContainsKey(v)) clash.Add(v.ToString());
        return clash;
    }

    private static readonly Dictionary<Color, Color> LightToDark = BuildReverse();

    private static Dictionary<Color, Color> BuildReverse()
    {
        var rev = new Dictionary<Color, Color>();
        foreach (var kv in DarkToLight) rev[kv.Value] = kv.Key;
        return rev;
    }

    /// <summary>将整棵视觉树刷成目标主题；重复调用安全（同向映射不会二次变色）。</summary>
    public static void Apply(DependencyObject root, bool dark)
    {
        IsDark = dark;
        var map = dark ? LightToDark : DarkToLight;
        // 视觉树那趟走过谁，就记在这里：逻辑树那趟只跳过"真被走过"的节点。
        // 不能用"有没有视觉父级"代替——见 Walk 的注释。
        Walk(root, map, dark, new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance));
    }

    /// <summary>
    /// 按**当前**主题补刷一棵游离的子树（不改变 <see cref="IsDark"/>）。
    ///
    /// 给"从根出发那趟够不到的弹层"用：<c>MainWindow.Batch.cs</c> 的 <c>BatchActionPopup</c> 是
    /// <c>new Popup { …, Child = menuRoot }</c> 造出来的，只设了 PlacementTarget、**从没加进任何
    /// Children 集合**，所以它既不在窗口的视觉树上、也不在逻辑树上——<see cref="Apply"/> 从根怎么走
    /// 都碰不到它（探针实测：只加 <c>case Popup</c> 分支对它是无效的，它仍停在夜间底色）。
    /// 这类弹层只能在"打开的那一刻"由持有它引用的代码补刷一次：调用点见
    /// <c>MainWindow.Images.cs</c> 的 <c>HookPopupAnimations</c>（那条 <c>Opened</c> 钩子已经挂在
    /// 三个弹层上，包括这个游离的）。弹层只在打开时可见，因此这一刷正是它需要正确配色的时刻。
    /// </summary>
    public static void ApplyTo(DependencyObject root)
    {
        var map = IsDark ? LightToDark : DarkToLight;
        Walk(root, map, IsDark, new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance));
    }

    /// <summary>
    /// 把某个**游离弹层**的内容登记进来：它打开时按当前主题刷一次，之后"主题在它开着的时候变了"也能补刷。
    ///
    /// 与 <see cref="ApplyTo"/> 的分工：那条 <c>Opened</c> 钩子只解决"打开那一刻"的配色，
    /// 而弹层内容**建一次就长期复用**（批量功能框的内容只在第一次进入选择模式时建，之后反复开关都是同一棵子树），
    /// 于是"开着时切主题"这一态没有任何人负责 —— 这里补上这一段。
    ///
    /// 登记内容 = <see cref="System.Windows.Controls.Primitives.Popup.Child"/> 而**不是** Popup 本身：
    /// 刷色只需要那棵内容子树，拿着它就不会把 Popup 的宿主窗口一起钉在内存里。
    /// 本项目够不到根的那类节点共两种，各有一套同风格的登记表：
    ///   · 真非模态窗 —— <c>GuardDialog._nonModalOpen</c>（<c>ReskinNonModalOpen</c> 负责补刷）；
    ///   · 游离弹层   —— 本表（<see cref="ReskinOrphanPopups"/> 负责补刷）。
    /// 模态 <c>ShowDialog()</c> 窗两类都不进：它们开着时主窗点不动、主题根本改不了（见 <c>GuardDialog</c> 的类注释）。
    ///
    /// 防重复登记 + 防泄漏：<paramref name="content"/> 已在表里就直接返回，不在 <c>Opened</c> 里重复添加；
    /// <c>Closed</c> 与它对偶摘除，且**只在"确实登记过"时才挂** —— 重复调用不会挂上第二个 Closed。
    /// 线程：开弹层、关弹层（<c>IsOpen</c> 由代码改）、切主题（<c>MainWindow.ApplyTheme</c>）全在 UI 线程
    /// ⇒ 与 <c>GuardDialog._nonModalOpen</c> 同一口径，不加锁。
    /// </summary>
    internal static void RegisterOrphanPopup(System.Windows.Controls.Primitives.Popup pop)
    {
        try
        {
            if (pop == null) return;
            if (pop.Child is not DependencyObject content) return;
            if (_orphanPopups.Contains(content)) return;      // 已登记：不再挂第二对 Opened/Closed

            pop.Opened += (_, _) =>
            {
                try
                {
                    if (pop.Child is not DependencyObject c) return;
                    if (!_orphanPopups.Contains(c)) _orphanPopups.Add(c);
                }
                catch (Exception ex) { Logger.LogError("ThemeManager.RegisterOrphanPopup(Opened)", ex); }
            };
            // 防泄漏：弹层无论怎么关（外部点击 / Esc / 代码收起）都必须摘掉；
            // 只增不减 = 内存泄漏 + 之后切主题去刷一棵早已不显示的子树
            pop.Closed += (_, _) => UnregisterOrphanPopup(pop);
        }
        catch (Exception ex) { Logger.LogError("ThemeManager.RegisterOrphanPopup", ex); }
    }

    /// <summary>摘掉一个游离弹层的登记（幂等：没登记过时什么也不做）。</summary>
    internal static void UnregisterOrphanPopup(System.Windows.Controls.Primitives.Popup pop)
    {
        try
        {
            if (pop?.Child is not DependencyObject content) return;
            _orphanPopups.Remove(content);                    // 与 Opened 里的 Add 严格对偶
        }
        catch (Exception ex) { Logger.LogError("ThemeManager.UnregisterOrphanPopup", ex); }
    }

    /// <summary>
    /// 按**当前**主题给所有打开中的游离弹层补刷一次（<see cref="ApplyTo"/> 只刷、不改 <see cref="IsDark"/>）。
    /// 由主题切换统一入口 <c>MainWindow.ApplyTheme</c> 调用，紧跟 <c>GuardDialog.ReskinNonModalOpen()</c>。
    ///
    /// 先 <c>ToArray()</c> 快照再遍历：补刷期间若有弹层正好被收起，<c>Closed</c> 会改这个集合 ——
    /// 直接遍历会被抛"集合已修改"。逐项 try/catch：一个弹层刷新失败不连累主题切换本身。
    /// </summary>
    internal static void ReskinOrphanPopups()
    {
        foreach (var content in _orphanPopups.ToArray())
        {
            try { ApplyTo(content); }
            catch (Exception ex) { Logger.LogError("ThemeManager.ReskinOrphanPopups", ex); }
        }
    }

    /// <summary>
    /// 打开中的游离弹层内容（只由 <see cref="RegisterOrphanPopup"/> 登记、<c>Closed</c> 里摘除）。
    /// 与 <see cref="ApplyTo"/> 的关系见 <see cref="RegisterOrphanPopup"/>：一个管"打开那一刻"，
    /// 一个管"开着时主题变了"。全在 UI 线程访问，故不加锁（与 <c>GuardDialog._nonModalOpen</c> 同口径）。
    /// </summary>
    private static readonly List<DependencyObject> _orphanPopups = new();

    private static void Walk(DependencyObject o, Dictionary<Color, Color> map, bool dark,
                             HashSet<DependencyObject> visualSeen)
    {
        // 只有视觉树那趟才登记；逻辑树那趟补刷的节点登记了也无害（不会有人再来问它）。
        bool viaVisualTree = o is Visual or System.Windows.Media.Media3D.Visual3D;
        if (viaVisualTree) visualSeen.Add(o);

        switch (o)
        {
            case Border b:
                Paint(b, Border.BackgroundProperty, b.Background, RemapBrush(b.Background, map));
                Paint(b, Border.BorderBrushProperty, b.BorderBrush, RemapBrush(b.BorderBrush, map));
                break;
            case Panel p:
                Paint(p, Panel.BackgroundProperty, p.Background, RemapBrush(p.Background, map));
                break;
            case Control c:
                Paint(c, Control.BackgroundProperty, c.Background, RemapBrush(c.Background, map));
                Paint(c, Control.BorderBrushProperty, c.BorderBrush, RemapBrush(c.BorderBrush, map));
                Paint(c, Control.ForegroundProperty, c.Foreground, RemapText(c.Foreground, c, map, dark));
                break;
            case TextBlock t:
                Paint(t, TextBlock.ForegroundProperty, t.Foreground, RemapText(t.Foreground, t, map, dark));
                break;
            case System.Windows.Shapes.Shape sh:
                Paint(sh, System.Windows.Shapes.Shape.FillProperty, sh.Fill, RemapBrush(sh.Fill, map));
                Paint(sh, System.Windows.Shapes.Shape.StrokeProperty, sh.Stroke, RemapBrush(sh.Stroke, map));
                break;
            // Popup 不在视觉树上（它自带一个顶层窗口），GetChildrenCount(Popup) 恒为 0，
            // 必须手动走到它的 Child；否则整个弹层永远刷不到。
            case System.Windows.Controls.Primitives.Popup pop:
                if (pop.Child is DependencyObject pc) Walk(pc, map, dark, visualSeen);
                break;
        }

        // 同时遍历视觉树与逻辑树：折叠（Collapsed）页面里的 ScrollViewer 内容尚未经过 measure，
        // 模板未应用 → 内容不在视觉树上；只走视觉树会漏掉整页（表现为首次进入该页仍是旧主题配色）。
        if (viaVisualTree)
        {
            int n = VisualTreeHelper.GetChildrenCount(o);
            for (int i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(o, i), map, dark, visualSeen);
        }

        foreach (object? child in LogicalTreeHelper.GetChildren(o))
        {
            if (child is not DependencyObject d) continue;
            // 护栏：只跳过"视觉树那趟真的走过"的节点。
            // 不能再用 IsInVisualTree（有没有视觉父级）——Popup.Child 只要**被打开过一次**，
            // 视觉父级就会一直是 NonLogicalAdornerDecorator，于是两条路都进不去它：
            // 视觉树那趟从 Popup 出发拿不到子节点，逻辑树那趟又被"已在视觉树"误判跳过。
            if (visualSeen.Contains(d)) continue;
            Walk(d, map, dark, visualSeen);
        }
    }

    /// <summary>
    /// 落一次主题色，但**不盖掉绑定，也不建立 local value**。
    ///
    /// 改前的写法是 <c>el.Prop = 值</c>（即 <c>SetValue</c>，写的是 local value，优先级高于样式 setter
    /// 与模板触发器），一旦落下就会永久打死两样东西：
    /// <list type="number">
    /// <item><b>绑定</b>：值被换成常量后 <c>BindingExpression</c> 被移除，此后源属性怎么变都画不出来
    /// （<c>RoundBtn</c> 模板把内层 Border.Background 绑到 Control.Background，正是这一条）；</item>
    /// <item><b>模板触发器</b>：如 ComboBoxItem 的 <c>IsSelected</c> ⇒ 选中蓝底、<c>IsMouseOver</c> ⇒ 悬停高亮，
    /// 触发器再也压不过 local value，选中/悬停从此没有高亮（且切主题不会恢复）。</item>
    /// </list>
    /// 因此这里只做两件事：能改的用 <see cref="DependencyObject.SetCurrentValue"/> 改**当前值**
    /// （值立即生效、色照样换，但不建立 local value ⇒ 样式与触发器之后仍能正常压过它）；
    /// 该让路的让路。**不是**"整棵子树跳过"，判据逐属性、逐个来。
    ///
    /// 让路两类（均以探针实测为准）：
    ///   · <see cref="BindingOperations.GetBindingExpressionBase"/> 非空 ⇒ 有绑定（含 TemplateBinding）⇒ 跳过。
    ///     绑定源自身会被 <see cref="Walk"/> 独立走到（RoundBtn：模板 Border 绑的是 TemplatedParent 的
    ///     Button.Background，那颗 Button 就在视觉树上），所以不写它也不会漏色，反而保住了绑定的活性；
    ///   · <see cref="BaseValueSource.Inherited"/> ⇒ 值来自祖先继承（如 Control.Foreground 继承到模板里的
    ///     ContentPresenter），写它等于把继承钉死、切断祖先换色的传导 ⇒ 跳过。
    /// 其余来源（Default / DefaultStyle / Style / ParentTemplate / Local …）照常换色——主题切换不会失效。
    /// 注意**不能**拿 TemplateTrigger / ParentTemplate 当"模板管着的"判据：模板里由普通 Setter 或元素属性
    /// 写下的常量也报这两类来源，而它们是**静态配色**、必须能换（SlimCombo 弹出层的 <c>#1C2029</c> 就写在
    /// 模板内联属性上）；真被触发器或绑定管着的那些，前面的绑定判据已经拦住了。
    /// 另：映射表没改写颜色时（新旧是同一支画刷）一律不动——少写一次，就少一次不必要的扰动。
    /// </summary>
    private static void Paint(DependencyObject owner, DependencyProperty dp, object? before, object? after)
    {
        try
        {
            if (after == null) return;
            if (ReferenceEquals(before, after)) return;                                 // 映射没改写：不落值
            if (BindingOperations.GetBindingExpressionBase(owner, dp) != null) return;   // 有绑定：不夺人所管
            var src = DependencyPropertyHelper.GetValueSource(owner, dp);
            if (src.BaseValueSource == BaseValueSource.Inherited) return;                // 继承来的：不钉死继承
            if (src.IsAnimated) return;                                                 // 动画在管的：让动画管
            owner.SetCurrentValue(dp, after);                                           // 只改当前值，不建 local value
        }
        catch { /* 只读/已冻结等异常一律跳过这一个属性，不影响其余刷色 */ }
    }

    /// <summary>
    /// 文字色：普通颜色查表；纯白/近白按上下文判断——
    /// 强调色按钮（绿/橙/红/蓝）上的白字保持不变，其余白字在日间转为深色，否则形成白底白字。
    /// </summary>
    private static Brush? RemapText(Brush? brush, DependencyObject owner, Dictionary<Color, Color> map, bool dark)
    {
        try
        {
            if (brush is SolidColorBrush sb && !sb.HasAnimatedProperties)
            {
                bool nearWhite = sb.Color.A > 200 && sb.Color.R > 235 && sb.Color.G > 235 && sb.Color.B > 235;
                if (nearWhite)
                {
                    if (HasAccentBackground(owner)) return brush;             // 强调色按钮：保持白色
                    return dark ? brush : new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E));
                }
                if (map.TryGetValue(sb.Color, out var c)) return new SolidColorBrush(c);
                return brush;
            }
            return RemapBrush(brush, map);
        }
        catch { return brush; }
    }

    /// <summary>向上查找最近的不透明底色，判断是否为强调色按钮（绿/橙/红/蓝）。</summary>
    private static bool HasAccentBackground(DependencyObject owner)
    {
        DependencyObject? o = owner;
        for (int depth = 0; o != null && depth < 8; depth++)
        {
            Brush? bg = o switch
            {
                Border b => b.Background,
                Panel p => p.Background,
                Control c => c.Background,
                _ => null
            };
            if (bg is SolidColorBrush s)
            {
                if (s.Color.A >= 200)      // 不透明底色即为判定依据
                    return IsAccent(s.Color);
            }
            o = VisualTreeHelper.GetParent(o);
        }
        return false;
    }

    /// <summary>判断是否为强调色（绿 / 橙 / 红 / 蓝）：两套主题中均不变，其上的白字需保留。</summary>
    private static bool IsAccent(Color c)
    {
        if (c.A < 200) return false;
        int r = c.R, g = c.G, b = c.B;
        if (r > 150 && g < 120 && b < 110) return true;                    // 红 #FF3B30 / #FF453A
        if (r > 200 && g > 120 && g < 200 && b < 90) return true;          // 橙 #FF9F0A
        if (r < 140 && g > 150 && b < 140) return true;                    // 绿 #34C759
        if (b > 180 && r < 130) return true;                               // 蓝 #007AFF / #4A9EFF / #5AC8FA
        return false;
    }

    /// <summary>
    /// 把**某一套主题里的**颜色换算成另一套主题里的对应色（查同一张映射表，无对应项则原样返回）。
    ///
    /// 给"按主题现算颜色"的代码用，典型是 <c>GuardDialog</c> 的按钮悬停态：
    /// 它不能再靠 <c>±0x18</c> 这类算术偏移去推另一套主题的颜色——映射表两侧的偏移量并不相等
    /// （灰按钮夜间 <c>#8E8E93</c> → 日间 <c>#6B6B70</c>，差 0x23，不是 0x18），
    /// 算出来的会是一个**两张表里都没有**的新色（探针实测：<c>0x8E8E93 − 0x18</c> 落成 <c>#76767B</c>），
    /// 于是"切主题时鼠标正悬停"这个按钮又留下了旧主题色。
    /// 这里保证任何"另一套主题的底色"都取自映射表本身，不会再造出新色。
    /// </summary>
    /// <param name="c">某套主题里的颜色（通常是构建控件时用的那张表的键侧取值）。</param>
    /// <param name="targetDark">要换算到的目标主题。</param>
    public static Color RemapColor(Color c, bool targetDark)
    {
        try
        {
            var map = targetDark ? LightToDark : DarkToLight;
            return map.TryGetValue(c, out var mapped) ? mapped : c;
        }
        catch { return c; }
    }

    /// <summary>替换画刷颜色；映射中无对应项时原样返回。</summary>
    private static Brush? RemapBrush(Brush? brush, Dictionary<Color, Color> map)
    {
        try
        {
            switch (brush)
            {
                case SolidColorBrush sb:
                    // 正在执行动画的画刷跳过，避免替换动画目标
                    if (sb.HasAnimatedProperties) return brush;
                    // 单色交给 RemapColor：与"按当前主题现算颜色"的调用点共用同一份查表逻辑
                    var sc = RemapColor(sb.Color, ReferenceEquals(map, LightToDark));
                    if (sc == sb.Color) return brush;
                    return new SolidColorBrush(sc);
                case LinearGradientBrush lg:
                    foreach (var stop in lg.GradientStops)
                        if (map.TryGetValue(stop.Color, out var lc)) stop.Color = lc;
                    return lg;
                case RadialGradientBrush rg:
                    foreach (var stop in rg.GradientStops)
                        if (map.TryGetValue(stop.Color, out var rc)) stop.Color = rc;
                    return rg;
                default:
                    return brush;
            }
        }
        catch { return brush; }
    }

    /// <summary>
    /// 毛玻璃着色：日间取浅色，避免深色垫在浅色界面下导致发灰；日间透明度高于夜间。
    /// </summary>
    public static (byte alpha, byte r, byte g, byte b) Tint => IsDark
        ? ((byte)0x1E, (byte)0x12, (byte)0x16, (byte)0x1E)     // 夜间 12%
        : ((byte)0x10, (byte)0xF2, (byte)0xF4, (byte)0xF8);    // 日间 6%

    // ═══ 窗口根底色（RootBorder 渐变的三段）═══
    // glass=true：毛玻璃开着，用半透明让系统材质透出来；
    // glass=false：毛玻璃关着或系统不支持，必须换成**不透明**版本——分层窗口本身是透明的，
    // 底色再半透明就整窗透空，桌面壁纸直接透进来（关掉开关后的现场 bug）。
    private static readonly Color[] WindowDarkGlass =
        { A(0x3D, 0x1A, 0x1E, 0x2E), A(0x35, 0x10, 0x15, 0x1C), A(0x3D, 0x16, 0x1B, 0x22) };
    private static readonly Color[] WindowDarkSolid =
        { A(0xFF, 0x1A, 0x1E, 0x2E), A(0xFF, 0x10, 0x15, 0x1C), A(0xFF, 0x16, 0x1B, 0x22) };
    private static readonly Color[] WindowLightGlass =
        { A(0x26, 0xE8, 0xED, 0xF5), A(0x20, 0xF5, 0xF7, 0xFA), A(0x26, 0xEE, 0xF2, 0xF7) };
    private static readonly Color[] WindowLightSolid =
        { A(0xFF, 0xE8, 0xED, 0xF5), A(0xFF, 0xF5, 0xF7, 0xFA), A(0xFF, 0xEE, 0xF2, 0xF7) };

    /// <summary>取窗口根底色的三段（顺序与 MainWindow.xaml 里 RootBorder 的渐变一致）。</summary>
    public static Color[] WindowStops(bool dark, bool glass) => dark
        ? (glass ? WindowDarkGlass : WindowDarkSolid)
        : (glass ? WindowLightGlass : WindowLightSolid);

    /// <summary>
    /// 开关（IosSwitch）轨道颜色：开 = 绿（两套主题一致）；关 = 夜间深灰 / 日间浅灰。
    /// 开关颜色由代码显式给，不走颜色映射表——模板里的颜色动画会把画刷的动画时钟一直挂着，
    /// 映射表对"带动画的画刷"是跳过的，结果就是日间模式下轨道仍停在夜间深灰。
    /// </summary>
    public static Color SwitchColor(bool on) => on
        ? Color.FromRgb(0x34, 0xC7, 0x59)
        : (IsDark ? Color.FromRgb(0x3A, 0x3A, 0x3C) : Color.FromRgb(0xD2, 0xD3, 0xD8));

    /// <summary>夜间整体不透明度（着色与窗口底色合成后的近似值），自检用。</summary>
    public static double NightOpacitySum => 0x1E / 255.0 + (0x3D / 255.0) * (1 - 0x1E / 255.0);

    /// <summary>日间整体不透明度，应低于夜间值。</summary>
    public static double DayOpacitySum => 0x10 / 255.0 + (0x26 / 255.0) * (1 - 0x10 / 255.0);
}
