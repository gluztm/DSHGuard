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
        // ── 星标（★）──
        // 加进映射表是为了"建好之后切换主题也能跟着变"：光靠 ThemeManager.StarColor 只覆盖
        // **构造那一刻**，而趋势榜/市场卡片建好后若用户切主题，写死的 #FFD60A 不在表里就不会被换掉。
        [C(0xFF, 0xD6, 0x0A)] = C(0xC2, 0x7A, 0x00),   // 星标：夜间亮金 → 日间深琥珀（浅底上才看得清）
        // ── 强调色（链接蓝在浅色底上需加深）──
        [C(0x5A, 0xC8, 0xFA)] = C(0x00, 0x71, 0xE3),
        [C(0xBF, 0xE3, 0xFB)] = C(0x0B, 0x5F, 0xA5),
        [C(0x8F, 0xB8, 0xD8)] = C(0x3B, 0x6E, 0x9E),
        [C(0xFF, 0x8A, 0x80)] = C(0xD7, 0x00, 0x15),   // 浅红（退出 UI）在浅色底上需加深
        // 日志正文使用 #EDEDF2 一类近白字，由「近白文字按上下文映射」规则处理：
        // 日间转为深色、夜间保持主文字色，无需单独配置映射项。
        // ── 表面：白色半透明 → 浅色（卡片取近白，避免纯白）──
        [A(0x10, 0xFF, 0xFF, 0xFF)] = A(0x99, 0xED, 0xF1, 0xF7),   // 输入框底色（半透明）
        // 卡片底色：日间不用纯白（纯白在浅底上反而"发飘、扎眼"），改成略带冷调的柔白。
        // 这是 2026-10-04 用户反馈"日间模式有点白得扎眼"之后的调整：把三处最扎眼的近白都往
        // 冷灰方向压一点，同时保持与深色文字的高对比（正文仍是 #1C1C1E，对比度依旧远超 4.5:1）。
        [A(0x12, 0xFF, 0xFF, 0xFF)] = A(0xB8, 0xF6, 0xF7, 0xFB),   // 卡片底色（柔白，避开纯白）
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
        [C(0x1C, 0x20, 0x29)] = C(0xEE, 0xF0, 0xF6),   // 筛选下拉 / 对话框卡的底色（柔白，不用纯白）
        [A(0xE6, 0x1C, 0x20, 0x29)] = A(0xE6, 0xEE, 0xF0, 0xF6),
        [A(0xE8, 0x0A, 0x0A, 0x0E)] = A(0xE8, 0xF2, 0xF2, 0xF7),   // 看图遮罩
        // ── 窗口底色（根 Border 的渐变三段；舒适可读毛玻璃预设：保留模糊透感，不让桌面文字噪点穿透正文）──
        [A(0xAE, 0x1A, 0x1E, 0x2E)] = A(0xA2, 0xDD, 0xE3, 0xED),
        [A(0xA4, 0x10, 0x15, 0x1C)] = A(0x98, 0xEC, 0xEF, 0xF6),
        [A(0xAE, 0x16, 0x1B, 0x22)] = A(0xA2, 0xE5, 0xEA, 0xF2),
        // 下拉项（日志来源筛选下拉）
        [C(0x2C, 0x31, 0x3B)] = C(0xE3, 0xE7, 0xEE),
        [C(0x25, 0x2B, 0x36)] = C(0xE8, 0xEC, 0xF3),   // 自绘下拉的悬停底色

        // ── 「综合管理」那一套（DshWidgets）──
        // 为什么这一组非进表不可：DshWidgets 是**另一套**配色常量（卡片 #1E1E24 / 面板 #18181E /
        // 输入框 #2B2B34 / 描边 #33333C / 分隔线 #26262E / 次字 #9A9AA2 / 三级字 #6E6E78），
        // 与主窗那套一个值都不重合。原先它们只按夜间配色建好后直接上屏 —— 切日间时映射表里查不到，
        // 于是整块「综合管理」在浅色窗口里保持深色（现场：日间模式里那一片是黑的）。
        // ⚠ 目标色必须**避开所有键**（含上面主窗那几条），否则映射不幂等：
        //   OverlappingMapKeys 自检会当场报红。下面每个目标色都落在各自明度带上、且不与任何键相等。
        // ⚠ 必须**双向可用**：LightToDark 是这张表反查出来的，所以一个目标色也不许撞上别的键。
        [C(0x1E, 0x1E, 0x24)] = C(0xF2, 0xF3, 0xF7),   // 卡片底
        [C(0x18, 0x18, 0x1E)] = C(0xE9, 0xEB, 0xF1),   // 面板底（装表单，比卡片略深）
        [C(0x2B, 0x2B, 0x34)] = C(0xE1, 0xE3, 0xEA),   // 输入框底
        [C(0x33, 0x33, 0x3C)] = C(0xCF, 0xD2, 0xDA),   // 描边
        [C(0x26, 0x26, 0x2E)] = C(0xDD, 0xDF, 0xE6),   // 分隔线
        [C(0x9A, 0x9A, 0xA2)] = C(0x55, 0x55, 0x5E),   // 次要文字
        [C(0x6E, 0x6E, 0x78)] = C(0x84, 0x84, 0x8C),   // 三级文字
        [C(0x22, 0x22, 0x2A)] = C(0xEF, 0xEF, 0xF4),   // 开关空槽底色
        [C(0x55, 0x55, 0x62)] = C(0xAE, 0xAE, 0xB2),   // 开关空槽描边
        // ⚠ 徽标底（DshWidgets.Badge 的「主色 + 0x28 透明度」）**故意不进表**：
        //   它是半透明色块叠在卡片底上，两套主题下都成立；写进表就变成恒等映射
        //   （键 == 值），而恒等项会让 OverlappingMapKeys 当场报红、反向表也会互相覆盖。
        // ↑ 上面 #1E1E24 那一组是 2.4.0 之前的「暖紫死黑」旧值：DshWidgets 已不再使用，
        //   但旧版本建好的控件、第三方插件页、历史截图对比仍可能带着它们 ⇒ **保留不删**（删了就退回日间黑块）。

        // ── 2.4.0 Oceanic Slate（深海星盘）：DshWidgets 新一代冷调半透明配色 ──
        // 与 Themes/Tokens.*.xaml 同值；公开常量（TextPrimary 沿用主窗 #F5F5F7 那一条）各占一个唯一键，
        // 目标色全部落在「冷调柔白 / 石板蓝黑可读半透明」档，且逐一核对过：不与任何键相等（OverlappingMapKeys 为空）。
        [A(0x7E, 0x1E, 0x2A, 0x3D)] = A(0xAA, 0xF2, 0xF5, 0xFA),   // 卡片底（深海石板磨砂 → 冷调柔白磨砂）
        [A(0x72, 0x0E, 0x14, 0x1F)] = A(0xB4, 0xEC, 0xF0, 0xF8),   // 面板底（更沉一档，装表单）
        [A(0x5C, 0x12, 0x1B, 0x2A)] = A(0x94, 0xF0, 0xF4, 0xFA),   // 2.4.0 输入框底（深海微凹石板 → 清晰柔白）
        [A(0x7E, 0x2A, 0x37, 0x4D)] = A(0xB4, 0xE3, 0xE9, 0xF3),   // 次级灰按钮统一底（清晰冷石板 → 冷浅灰）
        [A(0x26, 0xFF, 0xFF, 0xFF)] = A(0xE6, 0xE4, 0xE9, 0xF3),   // 输入框底（旧常量兜底）
        [A(0x28, 0xFF, 0xFF, 0xFF)] = A(0x22, 0x0F, 0x17, 0x2A),   // 描边
        [A(0x1C, 0xFF, 0xFF, 0xFF)] = A(0x14, 0x0F, 0x17, 0x2A),   // 分隔线
        [C(0x94, 0xA0, 0xB2)] = C(0x52, 0x5B, 0x6B),               // 次要文字（冷灰蓝）
        [C(0x6D, 0x77, 0x8A)] = C(0x7E, 0x87, 0x98),               // 三级文字
        // 主窗共用的语义层（Tokens 里同名键；这里只为「有人写死了同值」时兜底）
        [A(0x46, 0x10, 0x18, 0x26)] = A(0x84, 0xEE, 0xF2, 0xF8),   // Brush.Surface.Rail 导航舱底衬
        // 注：Brush.Border.Window（#26FFFFFF）与 DshWidgets.FieldBg 同值，表里只能有一个目标色（取输入框那条）；
        //     窗口外描边请一律走 {DynamicResource Brush.Border.Window}，不要写死色值。
        // 防御映射：旧版说明页链接淡蓝，浅底上几乎看不见 ⇒ 日间压成深蓝
        [C(0x8F, 0xB4, 0xFF)] = C(0x00, 0x66, 0xCC),
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

    private static readonly Uri DarkTokensUri = new("pack://application:,,,/Themes/Tokens.Dark.xaml", UriKind.Absolute);
    private static readonly Uri LightTokensUri = new("pack://application:,,,/Themes/Tokens.Light.xaml", UriKind.Absolute);

    /// <summary>
    /// 切换应用级语义令牌字典（<c>Themes/Tokens.Dark.xaml</c> ↔ <c>Tokens.Light.xaml</c>）。
    /// <summary>当前是否处于毛玻璃透光态（false 时表面语义令牌切为实底纯色，与毛玻璃拉开明显质感）。</summary>
    public static bool IsGlass { get; private set; } = true;

    /// <summary>
    /// 2.4.0 统一语义引擎：切换 <see cref="Application.Resources"/> 里的主题令牌字典（<c>Themes/Tokens.Dark|Light.xaml</c>）。
    /// 所有通过 <c>DynamicResource</c> 或 <c>SetResourceReference</c> 绑定了 <c>Brush.*</c> 的控件会同步换肤；
    /// 当 <paramref name="glass"/> = false（普通实色模式）时，将表面层级覆写为高饱和实色底板。
    /// </summary>
    internal static void SwitchTokenDictionary(bool dark, bool glass = true)
    {
        try
        {
            var app = Application.Current;
            if (app == null) return;
            var merged = app.Resources.MergedDictionaries;
            var nextUri = dark ? DarkTokensUri : LightTokensUri;
            var nextDict = new ResourceDictionary { Source = nextUri };

            if (!glass)
            {
                ApplySolidSurfaceOverrides(nextDict, dark);
            }

            for (int i = 0; i < merged.Count; i++)
            {
                var src = merged[i].Source?.OriginalString ?? merged[i].Source?.ToString() ?? "";
                if (src.EndsWith("Themes/Tokens.Dark.xaml", StringComparison.OrdinalIgnoreCase)
                    || src.EndsWith("Themes/Tokens.Light.xaml", StringComparison.OrdinalIgnoreCase))
                {
                    merged[i] = nextDict;
                    return;
                }
            }
            merged.Insert(0, nextDict);
        }
        catch (Exception ex)
        {
            Logger.LogError("ThemeManager.SwitchTokenDictionary", ex);
        }
    }

    private static void ApplySolidSurfaceOverrides(ResourceDictionary dict, bool dark)
    {
        (string suffix, Color c)[] surfaces = dark
            ? new[]
            {
                ("Card",        Color.FromRgb(0x1B, 0x23, 0x32)),
                ("Panel",       Color.FromRgb(0x13, 0x19, 0x24)),
                ("Input",       Color.FromRgb(0x1E, 0x27, 0x37)),
                ("InputHover",  Color.FromRgb(0x25, 0x30, 0x43)),
                ("Button",      Color.FromRgb(0x2A, 0x37, 0x4D)),
                ("ButtonHover", Color.FromRgb(0x35, 0x45, 0x60)),
                ("Rail",        Color.FromRgb(0x12, 0x18, 0x24)),
            }
            : new[]
            {
                ("Card",        Color.FromRgb(0xF7, 0xF9, 0xFC)),
                ("Panel",       Color.FromRgb(0xED, 0xF1, 0xF8)),
                ("Input",       Color.FromRgb(0xE7, 0xEC, 0xF4)),
                ("InputHover",  Color.FromRgb(0xDD, 0xE4, 0xEF)),
                ("Button",      Color.FromRgb(0xDF, 0xE6, 0xF1)),
                ("ButtonHover", Color.FromRgb(0xD2, 0xDB, 0xEA)),
                ("Rail",        Color.FromRgb(0xE9, 0xEE, 0xF6)),
            };
        foreach (var (suffix, c) in surfaces)
        {
            dict["Color.Surface." + suffix] = c;
            var b = new SolidColorBrush(c);
            b.Freeze();
            dict["Brush.Surface." + suffix] = b;
        }
    }

    /// <summary>返回指定主题令牌字典的排好序键集合（2.4.0 自检核对夜间/日间对称性）。</summary>
    internal static string[] TokenKeysForTest(bool dark)
    {
        var dict = new ResourceDictionary { Source = dark ? DarkTokensUri : LightTokensUri };
        var keys = new List<string>(dict.Keys.Count);
        foreach (var k in dict.Keys)
        {
            if (k is string s) keys.Add(s);
        }
        keys.Sort(StringComparer.Ordinal);
        return keys.ToArray();
    }

    /// <summary>将整棵视觉树刷成目标主题；重复调用安全（同向映射不会二次变色）。</summary>
    public static void Apply(DependencyObject root, bool dark, bool glass = true)
    {
        IsDark = dark;
        IsGlass = glass;
        SwitchTokenDictionary(dark, glass);
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
                    AddOrphanContent(c);
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
    /// 把游离弹层内容登记进表（<see cref="RegisterOrphanPopup"/> 的 <c>Opened</c> 回调调的**就是**这个方法）。
    ///
    /// 单独提出来是为了可测：<c>Opened</c> 只有真窗口（有 PresentationSource）才保证触发，
    /// 自检窗口不 Show ⇒ 走不到；但"登记去重 + 与摘除严格对偶"这半条不依赖窗口，
    /// 自检直接调这个方法就能把整条生命周期量出来（见 <c>SelfTest.cs</c> 的"③ 游离弹层登记表"）。
    /// 把它改回内联会让那条断言失去着力点 ⇒ 再退化成 SKIP。
    /// </summary>
    internal static void AddOrphanContent(DependencyObject content)
    {
        if (!_orphanPopups.Contains(content)) _orphanPopups.Add(content);
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

    /// <summary>当前登记在表的游离弹层内容数（自检用只读探针；不暴露表本身，免得被改）。</summary>
    internal static int OrphanPopupCountForTest() => _orphanPopups.Count;

    /// <summary>某个内容是否已登记（自检用只读探针）。</summary>
    internal static bool IsOrphanPopupRegisteredForTest(DependencyObject content) => _orphanPopups.Contains(content);

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
            if (src.IsExpression) return;                                               // DynamicResource/Binding 在管的：避免把语义令牌钉死成静态值
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

    /// <summary>
    /// 给一个**夜间**配色，取它在**当前**主题下该用的颜色（夜间原样、日间查表；表外色原样返回）。
    /// 给「构造时一次性定色、之后不进视觉树补刷」的独立窗口用（如 <c>DshTextEditorWindow</c>），
    /// 保证它与 DshWidgets / Tokens 取自同一张表，不再各自手写一套日夜色。
    /// </summary>
    public static Color ForCurrentTheme(Color nightColor) => IsDark ? nightColor : RemapColor(nightColor, targetDark: false);

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
    /// 毛玻璃着色：适度遮盖以保留模糊层次，同时确保复杂桌面上字迹与卡片边界清晰锐利。
    /// </summary>
    public static (byte alpha, byte r, byte g, byte b) Tint => IsDark
        ? ((byte)0x48, (byte)0x12, (byte)0x16, (byte)0x1E)     // 夜间 28%（配合根渐变形成稳定深海磨砂底）
        : ((byte)0x38, (byte)0xE7, (byte)0xEA, (byte)0xF1);    // 日间 22%（温和冷白磨砂，柔和护眼不透花）

    // ═══ 窗口根底色（RootBorder 渐变的三段）═══
    // glass=true：可读性优先的磨砂玻璃，保留背景色彩微透与 Acrylic 模糊光影；
    // glass=false：纯色不透（0xFF），彻底零透景，与毛玻璃拉出鲜明对比。
    private static readonly Color[] WindowDarkGlass =
        { A(0xAE, 0x1A, 0x1E, 0x2E), A(0xA4, 0x10, 0x15, 0x1C), A(0xAE, 0x16, 0x1B, 0x22) };
    private static readonly Color[] WindowDarkSolid =
        { A(0xFF, 0x1A, 0x1E, 0x2E), A(0xFF, 0x10, 0x15, 0x1C), A(0xFF, 0x16, 0x1B, 0x22) };
    private static readonly Color[] WindowLightGlass =
        { A(0xA2, 0xDD, 0xE3, 0xED), A(0x98, 0xEC, 0xEF, 0xF6), A(0xA2, 0xE5, 0xEA, 0xF2) };
    private static readonly Color[] WindowLightSolid =
        { A(0xFF, 0xDD, 0xE3, 0xED), A(0xFF, 0xEC, 0xEF, 0xF6), A(0xFF, 0xE5, 0xEA, 0xF2) };

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

    /// <summary>
    /// 星标（★）颜色：夜间用亮金，日间换**深琥珀**。
    ///
    /// 为什么必须分主题：原先两套主题都用 #FFD60A。那个黄在深底上很跳，但在日间的浅底上
    /// 几乎与白底融在一起（用户 2026-10-04 反馈"让⭐更明显一点"）——同一个颜色不可能同时满足
    /// 深底与浅底，所以按主题给两档，深色版保持识别度、浅色版保证对比度。
    /// </summary>
    public static Color StarColor => IsDark
        ? Color.FromRgb(0xFF, 0xD6, 0x0A)     // 夜间：亮金
        : Color.FromRgb(0xC2, 0x7A, 0x00);    // 日间：深琥珀（落在浅底上仍然清楚）

    /// <summary>夜间整体不透明度（着色与窗口底色合成后的近似值），自检用。</summary>
    public static double NightOpacitySum => 0x48 / 255.0 + (0xAE / 255.0) * (1 - 0x48 / 255.0);

    /// <summary>日间整体不透明度，应低于夜间值。（与 <see cref="Tint"/> 的日间 alpha、窗口底色的最外层 alpha 同源。）</summary>
    public static double DayOpacitySum => 0x38 / 255.0 + (0xA2 / 255.0) * (1 - 0x38 / 255.0);
}
