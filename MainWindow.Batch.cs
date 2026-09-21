using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DSHGuard;

/// <summary>
/// 插件批量操作（多选 → 一次启用 / 禁用 / 更新）。
/// 与 MainWindow.xaml.cs 同属 partial class；本文件不含任何新的第三方依赖。
///
/// 设计口径：
///   ① 勾选状态只存**插件名**（<see cref="_batchSelected"/>，忽略大小写）——
///      重新扫描后插件对象会换新，按对象存会全丢。
///   ② 批量条默认隐藏，只有在勾选了条目时才出现（「没有选择就没有批量操作」）。
///   ③ 破坏性动作（禁用 / 更新）一律先弹确认框**列出清单**，取消即原样返回。
///   ④ 真实结果以文件写入的返回值 / 命令退出码为准，失败如实写进提示框、事件与日志。
///   ⑤ 选中入口从「点右上角小方框」改为「点整张卡片」；
///      小方框退化为**纯状态指示器**（IsHitTestVisible=false），不再接受点击。
///   ⑥ 批量条顶替插件页顶部那一行里「一键更新」的位置（和「筛选 / 刷新」同一行、
///      同一行高、同一种按钮观感），不设浮层压卡片。
///   ⑦ （本次改动）**改成下拉菜单式功能框**（与左边的「筛选」框同款同行为）：
///         · 平时只看得到一颗紧凑的圆角按钮：`[✓] 已选 N 个 ▾`——
///           同高、同圆角、同底色（#12FFFFFF）、同描边（#40FFFFFF）、白字 + 右侧 ▾ 字形；
///         · 点它才展开一个 Popup（名字固定为 <see cref="_batchActionPopup"/> =
///           <c>BatchActionPopup</c>，外部按这个名字接管"点别处收起"与 Esc），
///           展开时带弹出动画（PopupAnimation=Slide + 内容淡入上滑）；
///         · 弹层里纵向排：一行「已选 N 个」说明 + 动作按钮 + 「全选 / 反选 / 清空」工具项。
///      这样做的好处是：整条只占「一键更新」那一格的宽度（按钮 ~110px），
///      原来一行三列那 390px 的最小宽度与"按钮按需增减导致排布变化"的问题一并消失。
///   ⑧ 弹层里的按钮仍按「Office 逻辑」**按需出现**，只给"现在做了有意义"的动作：
///         · 选中里**有启用中**的 → 才出现「禁用（N 个）」
///         · 选中里**有已关闭**的 → 才出现「启用（N 个）」
///         · 选中里**有可更新**的 → 才出现「更新（N 个）」（N = 可更新个数）
///         · 选中里**有已安装**的 → 才出现「卸载（N 个）」（红 #FF3B30，排在最后：破坏性动作）
///         · 没有对应动作就**整块不出现**（不摆一颗灰着的按键占位）
///         · 一个都没有时给一句「现在没有可做的动作」，不再留一片空白
///      纵向排列 ⇒ 谁出现谁消失都不会让别的按钮左右挪位（原来是靠定宽栅格硬撑）。
///   ⑨ 每个操作**执行完就自动收起弹层**（禁用 / 启用 / 更新 / 卸载 / 全选 / 反选 / 清空都一样：
///      点下去先收弹层、再走原来的确认与执行流程），弹层不会挂在界面上挡视线。
///   ⑩ 逐个更新期间的进度显示在弹层的说明行下面（「正在更新 …」+ 进度条），
///      不新起一行、不动按钮排布。
///   ⑪ （本次改动）**按钮文案只留括号计数**：「禁用所选（N 个）」→「禁用（N 个）」，
///      启用 / 更新同理，新增的卸载直接用「卸载（N 个）」——"所选"两个字在弹层里是废话
///      （弹层本来就长在"已选 N 个"的下拉按钮上），去掉后每颗按钮都短了两个全角字，
///      文案不再顶着按钮两侧，弹层也就能跟着收窄。
///   ⑫ （本次改动）弹层宽度按「最长一条文案」定：内容 186（原 232）+ 左右各 8 的内边距 = 外宽 202，
///      比原来窄 46。宽度改了以后，弹层的**右边缘**改按触发按钮的实测宽度现算着对齐
///      （<see cref="AlignBatchMenuToTrigger"/>），不会再出现"框缩回去了、右边缘却跟按钮错开"。
/// </summary>
public partial class MainWindow : Window
{
    // ══════════════ 多选状态 ══════════════
    /// <summary>已勾选的插件名（忽略大小写）。语义未变：键仍是插件名。</summary>
    private readonly HashSet<string> _batchSelected = new(StringComparer.OrdinalIgnoreCase);

    // ══════════════ 批量功能框控件引用（BuildBatchBar 里赋值） ══════════════
    // 整条 = 一颗**下拉按钮**（Border，外观照抄左边的「筛选」框）+ 一个挂在它身上的 Popup。
    // 是一行内元素（Border + Popup 不占布局空间），所以能直接塞进「一键更新」那一格。
    private Border? _batchBar;                     // 触发按钮本体（与 InstalledFilterBtn 同款）
    private TextBlock? _batchCountText;            // 按钮上的「已选 N 个」
    private TextBlock? _batchCountHint;            // 弹层里那句说明 / 干活中的提示（浅灰小字）

    /// <summary>
    /// 批量功能框的下拉弹层。**名字固定为 <c>BatchActionPopup</c>**：
    /// 外部（MainWindow.Images.cs）按这个名字接管"点别处自动收起"与 Esc 关闭。
    /// </summary>
    private Popup? _batchActionPopup;

    private StackPanel? _batchMenuStack;           // 弹层里的纵向内容（定宽，动作按钮铺满 => 等高同宽）
    private Button? _batchDisableBtn;              // 「禁用（N 个）」：选中里有启用中的才出现
    private Button? _batchEnableBtn;               // 「启用（N 个）」：选中里有已关闭的才出现
    private Button? _batchApplyBtn;                // 「更新（N 个）」：选中里有可更新的才出现
    private Button? _batchUninstallBtn;            // 「卸载（N 个）」：选中里有已安装的才出现（红，排最后）
    private TextBlock? _batchEmptyHint;            // 一个动作都没有时的一句说明（不留空白）
    private TextBlock? _batchBarProgressText;      // 逐个更新的 i/total 文案
    private ProgressBar? _batchBarProgress;        // 同一批动作内的进度条（与文案同生共死）

    /// <summary>
    /// 批量动作正在进行：挡住重复点击（同一时刻只跑一批）。
    /// <para>它只管"批量之间不重入"；与「一键更新 / 单颗更新 / 重新安装」互斥是
    /// <c>_pluginWriteBusy</c>（在 MainWindow.Tools.cs）的职责 —— 只有批量里**真跑命令**的
    /// 更新 / 卸载占那一份，禁用 / 启用不占（见 BatchUpdate_Click / BatchUninstall_Click 的入口）。</para>
    /// </summary>
    private bool _batchBusy;

    /// <summary>
    /// 批量功能框的统一尺寸。触发按钮与「筛选」框量出来的高度一致（26），
    /// 所以批量框进出时那一行不会上下跳。
    /// 圆角 15 也是照「筛选」框取的（它比常规按钮的 8 更圆，是"胶囊"观感）。
    /// </summary>
    private const double BatchRowHeight = 26;
    private const double BatchPadX = 9;            // 按钮横向内边距（原 11 → 9，收一点）
    private const double BatchGap = 5;             // 弹层里按钮之间的统一间距（原 6 → 5，取 5 更紧凑）

    /// <summary>
    /// 弹层内容宽度 = 186（原 232，窄了 46）。
    ///
    /// 取值依据 = 弹层里最长的一条**不该换行**的文案：
    ///   · 说明行「选中的插件可以做这些：」= 11 个全角字 × 11px ≈ 121，加左右各 2 的边距 ≈ 125；
    ///   · 最长的动作按钮文案「卸载（128 个）」≈ 4 个全角字 × 11.5 + 3 位数字 + 一个空格 ≈ 68，
    ///     加左右各 <see cref="BatchPadX"/> 的内边距 ≈ 86（按钮是铺满的，这只是"文字不贴边"的下限）；
    ///   · 空态那句「现在没有可做的动作」= 9 字 × 11 ≈ 99。
    /// ⇒ 真正的下限是说明行的 125。取 186（实测区间 176~196 的中值）留约 1.5 倍余量，
    ///   用来兜住：三位数计数（「卸载（128 个）」）、日间 / 毛玻璃下的字体回退度量差、高 DPI 取整，
    ///   同时按钮仍是"宽宽松松包着文字"，绝不会出现文字被裁或按钮比文字还窄。
    /// </summary>
    private const double BatchMenuWidth = 186;

    private const double BatchMenuPadX = 8;        // 弹层本体（menuRoot）左右内边距
    /// <summary>
    /// 弹层外宽（menuRoot 的 Padding 由 <see cref="BatchMenuPadX"/> 给出，两者不会写岔）= 202。
    /// 刻意**不算** menuRoot 那左右各 1px 的描边：算进去右对齐会精确 2px，不算则弹层右边缘比按钮
    /// 右边缘多出 2px（仍然在按钮右侧、不会越出窗口），肉眼不可辨，不值得再多一个常量。
    /// </summary>
    private const double BatchMenuOuterWidth = BatchMenuWidth + BatchMenuPadX * 2;   // = 202

    /// <summary>
    /// 触发按钮宽度的约数（见文件上方：「只有触发按钮占宽（约 110，跟「筛选」框一个量级）」）。
    /// 只在 <see cref="AlignBatchMenuToTrigger"/> 拿不到实测宽度时兜底 —— 正常路径一律按实测值算。
    /// </summary>
    private const double BatchTriggerWidthFallback = 110;

    private const double BatchTriggerRadius = 15;  // 与 InstalledFilterBtn 的 CornerRadius 一致

    private static readonly Color BatchGray = Color.FromRgb(0x8E, 0x8E, 0x93);
    private static readonly Color BatchBlue = Color.FromRgb(0x00, 0x7A, 0xFF);
    private static readonly Color BatchGreen = Color.FromRgb(0x34, 0xC7, 0x59);
    private static readonly Color BatchOrange = Color.FromRgb(0xFF, 0x9F, 0x0A);

    /// <summary>
    /// 破坏性动作（卸载）的红色：<c>#FF3B30</c> 是项目里**既有的破坏性红**（单插件「卸载」按钮
    /// <c>MiniButton("卸载", "#FF3B30")</c>、终止引擎、"仍然终止"确认按钮都用它），这里只是把它
    /// 取成批量弹层能用的 <see cref="Color"/>，**没有新增任何色号**。
    /// </summary>
    private static readonly Color BatchRed = Color.FromRgb(0xFF, 0x3B, 0x30);

    /// <summary>
    /// 弹层里的动作按钮（禁用 / 启用 / 更新 / 卸载）：复用卡片按钮的那套样式
    /// （MiniButton 在 MainWindow.Tools.cs，内部已套 RoundBtn 统一圆角模板）。
    /// **不定宽**：纵向排列 + 铺满弹层 ⇒ 有几颗都是同宽等高；高度由 <see cref="BatchRowHeight"/> 统一锁死，
    /// 按钮不会被字号撑出参差。横向内边距只当"文字不贴边"的下限用（按钮铺满时它不额外加宽按钮），
    /// 因此 <see cref="BatchPadX"/> 取 9 就够，不必再大。
    /// </summary>
    private static Button BatchButton(string text, Color bg, string? tip = null)
    {
        var b = MiniButton(text, $"#{bg.R:X2}{bg.G:X2}{bg.B:X2}", padX: BatchPadX, padY: 0);
        b.Height = BatchRowHeight;
        b.MinHeight = BatchRowHeight;
        b.FontSize = 11.5;
        b.HorizontalAlignment = HorizontalAlignment.Stretch;   // 纵向铺满 ⇒ 颗颗同宽
        b.VerticalAlignment = VerticalAlignment.Center;
        b.HorizontalContentAlignment = HorizontalAlignment.Center;
        b.VerticalContentAlignment = VerticalAlignment.Center;
        if (!string.IsNullOrEmpty(tip)) b.ToolTip = tip;
        ButtonFx.Wire(b);       // 动态创建的按钮要自己挂动效（已在树上的不会重复挂）
        return b;
    }

    /// <summary>
    /// 工具按钮（全选 / 反选 / 清空）：刻意**比动作按钮轻一层** —— 中性灰底 + 一圈细描边 +
    /// 灰字，而不是"绿 / 橙 / 蓝 + 白字"的实心强调色。高度 / 圆角 / 字号与动作按钮完全一致，
    /// 差别只在"重不重"，一眼就能分出"哪些按钮执行操作、哪些仅用于选择"。
    /// 底色与描边都是**半透明白**（#12FFFFFF / #40FFFFFF，与「筛选」框和左侧筛选下拉同款）：
    /// 主题映射表里登记过，日间会转成半透明黑、毛玻璃下透出系统材质，三种样式都不会糊。
    /// 文字用映射表里的次要文字灰（夜间 #8E8E93 → 日间 #6B6B70），浅底深底都看得清。
    /// </summary>
    private static Button BatchToolButton(string text, string tip)
    {
        var b = new Button
        {
            Content = text,
            FontSize = 11.5,
            Height = BatchRowHeight,
            MinHeight = BatchRowHeight,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Stretch,   // 与动作按钮同宽
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Focusable = false,                            // 弹层里不给焦点虚线框
            Cursor = Cursors.Hand,
            Background = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            Foreground = new SolidColorBrush(BatchGray),   // 次要文字灰：日间映射为深灰，两种样式都看得清
            ToolTip = tip
        };
        RoundBtn.Apply(b);      // 统一圆角 8（与动作按钮同一份模板）
        ButtonFx.Wire(b);
        return b;
    }

    /// <summary>
    /// 显隐一颗动作按钮。这里用 <see cref="Visibility.Collapsed"/>（**不占位**）：
    /// 按钮是纵向排列的，藏起来的就该把位置也让出去，弹层才不会留一块空白；
    /// 而且纵向排列下"谁出现谁消失"本来就不会让别的按钮左右挪位。
    /// </summary>
    private static void SetBatchActionVisible(Button? b, bool visible)
    {
        if (b == null) return;
        b.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>现在这些名字还找得到对应的插件（卸载后勾选残留要靠它清掉）。</summary>
    private List<PluginManager.Plugin> SelectedPlugins()
        => _plugins.Where(p => p != null && _batchSelected.Contains(p.Name)).ToList();

    // ══════════════════════════════════════════════════════════════
    //  ① 批量操作条（下拉菜单式功能框）
    // ══════════════════════════════════════════════════════════════
    /// <summary>
    /// 建一个**下拉菜单式功能框**，默认隐藏。它由两部分组成：
    ///   · 一颗紧凑的触发按钮（外观照抄左边的「筛选」框：同高、同圆角、同底色 / 描边、白字 + ▾）；
    ///   · 挂在按钮上的 <c>BatchActionPopup</c>：点按钮才展开，里面是逐项排好的批量动作与工具项。
    ///
    /// 接法（由外部那一行负责，本文件自己不碰 XAML）：
    ///   ① 把返回的这个塞进插件页顶部工具条 <c>PluginToolbar</c> 里「一键更新」原来的位置
    ///      （XAML 的 Grid.Column="2"，与「筛选」「刷新」同一行）；
    ///   ② 每次刷新列表后，按"有没有选中"在两者之间二选一：
    ///        选中 > 0 → 本框 Visible、把「一键更新」那颗 Border 设成 Collapsed
    ///        选中 = 0 → 本框 Collapsed（它自己也会保持隐藏）、「一键更新」照旧按
    ///                   "有没有可更新项"决定显隐
    ///      ——「一键更新」不存在时不用特殊处理，这个二选一同一条逻辑就够。
    ///   ③ 用 Visibility（不是把元素摘掉）来切换：两者都留在行里，那一行的高度不会变。
    /// 高度：锁死 <see cref="BatchRowHeight"/> = 26，与「筛选」（Padding 12,5 + 边框）
    /// 和「刷新」（MiniBtn：Padding 12,6）量出来的高度一致，因此本框进出时列表不上下跳。
    /// 宽度：只有触发按钮占宽（约 110，跟「筛选」框一个量级），原来一行三列那 390 的最小宽度
    /// 与"动作按钮按需增减导致排布变化"的问题一并消失。
    /// </summary>
    internal FrameworkElement BuildBatchBar()
    {
        // ── 触发按钮：完全照「筛选」框那套写法（同样的圆角 15 / 内边距 12,5 /
        //    底色 #12FFFFFF / 描边 #40FFFFFF / 白字 + 左侧图标 + 右侧 ▾ 字形）──
        // 半透明白是主题映射表里登记过的颜色：日间转成半透明黑，毛玻璃下透出系统材质。
        _batchCountText = new TextBlock
        {
            Text = "已选 0 个",
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF7)),   // 主文字色：日间映射为深色
            VerticalAlignment = VerticalAlignment.Center
        };

        // 触发按钮的内容层：同样不许拿焦点（内容上不给焦点虚线框留可乘之机）。
        // 这一层只负责横向排布文字，关掉焦点不影响任何行为。
        var triggerPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Focusable = false,
            FocusVisualStyle = null
        };
        triggerPanel.Children.Add(new TextBlock
        {
            Text = "\uE73E",                       // 勾选字形，与「筛选」框的图标同一个图标字体
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF7)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        });
        triggerPanel.Children.Add(_batchCountText);
        triggerPanel.Children.Add(new TextBlock
        {
            Text = "\uE70D",                       // ▾：展开箭头
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 8,
            Foreground = new SolidColorBrush(BatchGray),   // 次要文字灰（映射表里登记过）
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0)
        });

        var trigger = new Border
        {
            CornerRadius = new CornerRadius(BatchTriggerRadius),
            Padding = new Thickness(12, 5, 12, 5),
            Background = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,                 // 手型光标：ButtonFx 靠它挂悬停 / 按下动效
            VerticalAlignment = VerticalAlignment.Center,

            // ── 本次改动：保证四边都是**一致实线**（颜色 / 圆角 / 边框粗细 / 箭头一律没动）──
            // Border 自己画不出虚线，现场"有的边虚、有的边实"只有两个来源：
            //   ① 可聚焦元素被 WPF 在**外面**补一圈点状焦点框（FocusVisualStyle）；
            //   ② 1px 描边落在非整数像素上被渲染成半透明（个别边看着就"虚"）。
            // 所以这里只做两件事：关掉焦点视觉 + 打开像素对齐。
            Focusable = false,                     // ① 不可聚焦 ⇒ 永远不画焦点虚线框
            FocusVisualStyle = null,               //    双保险：显式置空焦点视觉样式
            UseLayoutRounding = true,              // ② 布局值吸附到整数像素（与 ButtonFx.Attach 同口径）
            SnapsToDevicePixels = true,            //    描边按设备像素吸附，1px 不再被摊成半透明

            Visibility = Visibility.Collapsed,    // 默认隐藏：没有选择就没有批量操作
            ToolTip = "点卡片选中，再点一次取消；批量操作完成后自动取消选中。" +
                      "可对选中的插件批量禁用 / 启用 / 更新 / 卸载。",
            Child = triggerPanel
        };
        trigger.MouseLeftButtonDown += BatchBar_Click;
        ButtonFx.Wire(trigger);

        // ── 弹层内容：一行说明 + 进度 + 动作按钮 + 工具项，全部纵向排列 ──
        // 定宽 186（加左右各 8 的内边距 = 202）：刚好放下最长一条文案（见 BatchMenuWidth 的取值依据），
        // 按钮铺满 ⇒ 有几颗都同宽等高，间距统一 BatchGap=5；纵向排列 ⇒ 谁出现谁消失都不会左右挪位。
        _batchMenuStack = new StackPanel { Orientation = Orientation.Vertical, Width = BatchMenuWidth };

        _batchCountHint = new TextBlock
        {
            Text = "",
            FontSize = 11,
            Foreground = new SolidColorBrush(BatchGray),   // 次要文字灰：日间映射为深灰
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 0, 2, BatchGap),
            ToolTip = "点卡片选中，再点一次取消。"
        };
        _batchMenuStack.Children.Add(_batchCountHint);

        // 逐个更新期间的「这一项是谁 / 走到第几个」文案：与下面那条进度条同生共死
        // （ShowBatchProgress / HideBatchProgress 成对写这两件）。
        // 说明行在批量干活时只有一句「正在处理…」⇒ 不报出**是哪一项**；这条文案补的就是这一点，
        // 取文本与底部进度条用的同一份 label（「插件名（i/N）」）。
        // 外观照抄同层的 _batchCountHint（FontSize 11 + BatchGray）：
        // 一样是次要文字、一样是与「筛选」下拉同款的弹层小字，不新增任何色号或字号。
        // 注意它**必须**进 _batchMenuStack.Children：只声明不挂进树的 TextBlock 一个字都不会显示
        // （本字段原先是「只声明、没 new、也没挂」⇒ 编译器 CS0649，调用点写进去的文案全落空）。
        _batchBarProgressText = new TextBlock
        {
            Text = "",
            FontSize = 11,
            Foreground = new SolidColorBrush(BatchGray),   // 次要文字灰：日间映射为深灰
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 0, 2, BatchGap),
            Visibility = Visibility.Collapsed
        };
        _batchMenuStack.Children.Add(_batchBarProgressText);

        // 逐个更新期间的进度条：就在说明行下面，不新起一行、不动按钮排布
        _batchBarProgress = new ProgressBar
        {
            Height = 5,
            Minimum = 0,
            Maximum = 1,
            Value = 0,
            Margin = new Thickness(2, 0, 2, BatchGap),
            BorderThickness = new Thickness(0),
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            // 槽底用半透明白（主题映射认得这一项），压在哪种底色上都不突兀
            Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            Foreground = new SolidColorBrush(BatchBlue)
        };
        _batchMenuStack.Children.Add(_batchBarProgress);

        // 动作按钮：顺序固定为 禁用（橙）→ 启用（绿）→ 更新（蓝）→ 卸载（红），
        // 该不该出现由 UpdateBatchBar 按「Office 逻辑」决定。
        // 卸载排最后：它是这一组里唯一的破坏性动作（包会被删掉、不可逆），
        // 位置压在最下面，离「全选 / 反选 / 清空」最近，手滑的代价最小。
        _batchDisableBtn = BatchButton("禁用", BatchOrange,
            "禁用选中的插件（操作前列出清单确认，重启 DSH 后生效）");
        _batchDisableBtn.Margin = new Thickness(0, 0, 0, BatchGap);
        _batchDisableBtn.Click += BatchMenuAction_Click;
        _batchDisableBtn.Tag = new Action<object, RoutedEventArgs>(BatchDisable_Click);
        SetBatchActionVisible(_batchDisableBtn, false);   // 初始不显示，由 UpdateBatchBar 按需放出来
        _batchMenuStack.Children.Add(_batchDisableBtn);

        _batchEnableBtn = BatchButton("启用", BatchGreen,
            "启用选中的插件（操作前列出清单确认，重启 DSH 后生效）");
        _batchEnableBtn.Margin = new Thickness(0, 0, 0, BatchGap);
        _batchEnableBtn.Click += BatchMenuAction_Click;
        _batchEnableBtn.Tag = new Action<object, RoutedEventArgs>(BatchEnable_Click);
        SetBatchActionVisible(_batchEnableBtn, false);
        _batchMenuStack.Children.Add(_batchEnableBtn);

        _batchApplyBtn = BatchButton("更新", BatchBlue,
            "把选中的插件更新到新版本（更新前列出清单确认）");
        _batchApplyBtn.Margin = new Thickness(0, 0, 0, BatchGap);
        _batchApplyBtn.Click += BatchMenuAction_Click;
        _batchApplyBtn.Tag = new Action<object, RoutedEventArgs>(BatchUpdate_Click);
        SetBatchActionVisible(_batchApplyBtn, false);
        _batchMenuStack.Children.Add(_batchApplyBtn);

        // 卸载：破坏性动作，红色 #FF3B30（既有色号），排在三个动作的最后。
        // 工具提示里把"不可逆"说清楚 —— 它是这一排里唯一执行删除的动作。
        _batchUninstallBtn = BatchButton("卸载", BatchRed,
            "把选中的插件从 DSH 卸载（操作前列出清单确认；卸载不可逆，需重新安装才能使用）");
        _batchUninstallBtn.Margin = new Thickness(0, 0, 0, BatchGap);
        _batchUninstallBtn.Click += BatchMenuAction_Click;
        _batchUninstallBtn.Tag = new Action<object, RoutedEventArgs>(BatchUninstall_Click);
        SetBatchActionVisible(_batchUninstallBtn, false);
        _batchMenuStack.Children.Add(_batchUninstallBtn);

        // 一个动作都没有时的一句话（不留一片空白，也不摆灰按键占位）
        _batchEmptyHint = new TextBlock
        {
            Text = "当前没有可执行的操作",
            FontSize = 11,
            Foreground = new SolidColorBrush(BatchGray),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };
        _batchMenuStack.Children.Add(_batchEmptyHint);

        // 工具项「全选 / 反选 / 清空」：比动作按钮轻一层（中性灰底 + 细描边 + 灰字），
        // 全选 / 反选作用于「当前列表里的全部插件」（含已关闭的），不受筛选结果限制。
        var allBtn = BatchToolButton("全选", "选中列表中的全部插件（含已禁用的）");
        allBtn.Margin = new Thickness(0, BatchGap, 0, 0);
        allBtn.Click += (_, _) => { CloseBatchMenu(); BatchSelectAll_Click(allBtn, new RoutedEventArgs()); };
        _batchMenuStack.Children.Add(allBtn);

        var invertBtn = BatchToolButton("反选", "已选中的取消选中，未选中的选中");
        invertBtn.Margin = new Thickness(0, BatchGap, 0, 0);
        invertBtn.Click += (_, _) => { CloseBatchMenu(); BatchInvert_Click(invertBtn, new RoutedEventArgs()); };
        _batchMenuStack.Children.Add(invertBtn);

        var clearBtn = BatchToolButton("清空", "取消全部选中");
        clearBtn.Margin = new Thickness(0, BatchGap, 0, 0);
        clearBtn.Click += (_, _) => { CloseBatchMenu(); BatchClear_Click(clearBtn, new RoutedEventArgs()); };
        _batchMenuStack.Children.Add(clearBtn);

        // ── 弹层本体 ──
        // AllowsTransparency + StaysOpen 与「筛选」框同一套口径（StaysOpen=True 由外部点击 / Esc 收起）；
        // PopupAnimation=Slide 给出弹出动画，命中测试打开时再补一次内容淡入上滑（见 AnimateBatchMenu）。
        // 底色 #1C2029 是映射表里登记过的"下拉底色"（日间转近白），与「筛选」下拉长得一样。
        var menuRoot = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(BatchMenuPadX),   // 外宽 = BatchMenuWidth + 2×它（=202），与对齐算法同一份来源
            Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x20, 0x29)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            // 投影用全限定名：本文件没有 using System.Windows.Media.Effects（与 UninstallWindow 同一写法）
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            { BlurRadius = 16, ShadowDepth = 3, Opacity = 0.5, Color = Colors.Black },
            Child = _batchMenuStack
        };

        _batchActionPopup = new Popup
        {
            Name = "BatchActionPopup",           // 外部（Images.cs）按这个名字接管外部点击与 Esc
            AllowsTransparency = true,
            StaysOpen = true,                    // 与筛选框一致：关闭时机由代码控制
            PopupAnimation = PopupAnimation.Slide,
            PlacementTarget = trigger,
            Placement = PlacementMode.Bottom,
            // 右对齐到触发按钮、别越出窗口右边界：PlacementMode.Bottom 先把**左**边缘对齐到按钮左边缘，
            // 所以这里给一个负的水平偏移把整块左移，使**右**边缘落回按钮右侧
            // （偏移 = 按钮宽 − 弹层外宽）。宽度从 232 收到 186 之后原来写死的 -150 就对不上了
            // （弹层会整块左移 46、与按钮错开一截），所以改成按公式取：
            // 这里先按约数给个初值，真正展开前还会用按钮的实测宽度再算一次（AlignBatchMenuToTrigger）。
            HorizontalOffset = BatchTriggerWidthFallback - BatchMenuOuterWidth,
            VerticalOffset = 6,                  // 与「筛选」下拉的 6 一致
            Child = menuRoot
        };

        _batchBar = trigger;
        return trigger;
    }

    /// <summary>
    /// 点触发按钮：开 / 关下拉功能框（与已安装页「排序」框的 InstalledSortMenu_Click 同一套写法）。
    /// 刻意**不设 e.Handled**：卡片选中逻辑走的是卡片自己的预览事件，触发按钮不在卡片里，
    /// 两不相干；这里消费事件反而会挡住将来挂在同一处的其它处理。
    /// </summary>
    private void BatchBar_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (_batchActionPopup == null) return;
            if (_batchActionPopup.IsOpen) _batchActionPopup.IsOpen = false;
            else OpenBatchMenu();
        }
        catch (Exception ex) { Logger.LogError("BatchBar_Click", ex); }
    }

    /// <summary>
    /// 展开下拉功能框：先把内容按当前选中刷一遍（数量 + 该出现哪几个动作），再打开弹层并补弹出动画。
    /// 弹层内容不在窗口视觉树里，动效要在这里单独挂一次（与筛选下拉的 WirePopupContent 同理）。
    /// </summary>
    private void OpenBatchMenu()
    {
        try
        {
            if (_batchActionPopup == null) return;
            UpdateBatchBar();                    // 打开前先refresh一次：看到的数量与动作永远是最新的
            AlignBatchMenuToTrigger();           // 再按触发按钮的实测宽度对齐弹层右边缘（宽度收窄后必须现算）
            WirePopupContent(_batchActionPopup);
            _batchActionPopup.IsOpen = true;
            AnimateBatchMenu();
        }
        catch (Exception ex) { Logger.LogError("OpenBatchMenu", ex); }
    }

    /// <summary>
    /// 让弹层的**右边缘**对齐触发按钮的右边缘（每次展开前重算一次）。
    ///
    /// 为什么必须现算：<see cref="PlacementMode.Bottom"/> 是"弹层左边缘对齐按钮左边缘"，
    /// 原先靠一个写死的 <c>HorizontalOffset = -150</c> 配上 232 的宽度才刚好落到按钮右侧；
    /// 宽度一收窄（232 → 186，外宽 202），同一个 -150 就会让弹层整块左移一截、与按钮错开。
    /// 所以改成按公式算：偏移 = 按钮实测宽 − 弹层外宽 ⇒ 右边缘 = 按钮右边缘。
    ///
    /// 按钮宽度随计数文案变化（「已选 9 个」/「已选 128 个」差十几个像素），因此每次展开都重算，
    /// 而不是在构建时算一次。按钮不可能比弹层宽（≈110~114 vs 202），所以偏移恒为负 —— 弹层只会
    /// 往左伸，不会越出窗口右边界（这正是当初取负偏移的用意）。
    /// 拿不到实测宽度（理论上只在展开前才会这样）就退回文件里注明的约 110 那一档。
    /// </summary>
    private void AlignBatchMenuToTrigger()
    {
        try
        {
            if (_batchActionPopup == null || _batchBar == null) return;
            double triggerWidth = _batchBar.ActualWidth > 1 ? _batchBar.ActualWidth : BatchTriggerWidthFallback;
            _batchActionPopup.HorizontalOffset = triggerWidth - BatchMenuOuterWidth;
        }
        catch (Exception ex) { Logger.LogError("AlignBatchMenuToTrigger", ex); }
    }

    /// <summary>收起下拉功能框（幂等；弹层没开时什么也不做）。</summary>
    private void CloseBatchMenu()
    {
        try { if (_batchActionPopup != null) _batchActionPopup.IsOpen = false; }
        catch (Exception ex) { Logger.LogError("CloseBatchMenu", ex); }
    }

    /// <summary>
    /// 弹层的弹出动画：淡入 + 轻微上滑（约 140 毫秒）。
    /// 与 PopupAnimation=Slide 叠加（一个滑整个弹层、一个滑内容），弹出观感更明确。
    /// 动画只动 Opacity 与一层 TranslateTransform，跑在渲染线程上，不阻塞、不卡顿；
    /// 收尾时显式复位（FillBehavior.Stop + 写回静止值），绝不留"永久半透明"的残留。
    /// </summary>
    private void AnimateBatchMenu()
    {
        try
        {
            var child = _batchActionPopup?.Child as FrameworkElement;
            if (child == null) return;

            child.BeginAnimation(UIElement.OpacityProperty, null);
            child.Opacity = 1.0;
            child.RenderTransform = new TranslateTransform(0, 8);

            var dur = TimeSpan.FromMilliseconds(140);
            var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 };
            var fade = new DoubleAnimation(0.0, 1.0, dur) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
            fade.Completed += (_, _) =>
            {
                try
                {
                    child.BeginAnimation(UIElement.OpacityProperty, null);
                    child.Opacity = 1.0;
                    child.RenderTransform = null;    // 去掉变换，文字恢复锐利渲染
                }
                catch { }
            };
            child.BeginAnimation(UIElement.OpacityProperty, fade);

            var slide = new DoubleAnimation(8, 0, dur) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
            (child.RenderTransform as TranslateTransform)?.BeginAnimation(TranslateTransform.YProperty, slide);
        }
        catch (Exception ex) { Logger.LogError("AnimateBatchMenu", ex); }
    }

    /// <summary>
    /// 动作按钮统一入口：**先把弹层收起来**，再交给原来那三个处理器
    /// （禁用 / 启用 / 更新 / 卸载）—— 确认框、执行、结果汇报、日志、<c>_batchBusy</c> 防护
    /// 一行都没动，这里只是"点完就收"，免得弹层挂在界面上挡视线。
    /// </summary>
    private void BatchMenuAction_Click(object sender, RoutedEventArgs e)
    {
        var handler = (sender as Button)?.Tag as Action<object, RoutedEventArgs>;
        CloseBatchMenu();
        handler?.Invoke(sender!, e);
    }

    /// <summary>
    /// 「已选 N 个」的**唯一**刷新点（下拉按钮上的计数 + 弹层里的说明行 + 底部统计那句）。
    ///
    /// 事实来源只有一个：<see cref="_batchSelected"/>.Count —— 这里每次都**现算**，
    /// 不缓存、不在别处另存一份计数，也不管是谁调用的。所以点卡片 / 全选 / 反选 / 清空 /
    /// 刷新前后 / 动作收尾，看到的数字永远等于当前集合大小，不会停在某次刷新的旧值上。
    ///
    /// 计数文案固定为「已选 N 个」（N = 1 也是"1 个"，不放"1 个插件"这类不一致写法）；
    /// N = 0 时按钮文字照旧是「已选 0 个」，整条的隐藏交给调用方（<see cref="UpdateBatchBar"/>）。
    /// </summary>
    private void RefreshBatchCounts(bool show)
    {
        try
        {
            int n = _batchSelected.Count;                      // 唯一事实来源，现算

            if (_batchCountText != null) _batchCountText.Text = $"已选 {n} 个";

            // 弹层说明行：忙的时候显示「正在处理…」，空闲时说明这里能做什么。
            // 文案跟着「有没有可做的动作」走，免得给出与实际不符的指引。
            if (_batchCountHint != null)
                _batchCountHint.Text = _batchBusy
                    ? "正在处理…"
                    : (show ? "可对选中的插件执行以下操作：" : "");

            // 底部那句「…，已选中 N 个插件」：只在列表**重渲染**时才会更新，
            // 而点卡片根本不重建列表 —— 在此处一并补充一次（幂等，纯字符串拼接）。
            try { RefreshPluginsSummaryWithSelection(); }
            catch (Exception ex) { Logger.LogError("RefreshPluginsSummaryWithSelection", ex); }
        }
        catch (Exception ex) { Logger.LogError("RefreshBatchCounts", ex); }
    }

    /// <summary>自检用：当前真实选中数（唯一事实来源）。</summary>
    internal int BatchSelectionCountForTest() => _batchSelected.Count;

    /// <summary>自检用：下拉按钮上那行计数文案（没建出来时给空串）。</summary>
    internal string BatchCountLabelForTest() => _batchCountText?.Text ?? "";

    /// <summary>
    /// 按勾选情况刷新下拉功能框：触发按钮上的数量、弹层里的说明，以及**该出现哪几个动作按钮**。
    ///
    /// Office 口径：只给"现在做了有意义"的动作 ——
    ///   · 选中里只要有**一个还开着** → 「禁用（N 个）」
    ///   · 选中里只要有**一个已关闭** → 「启用（N 个）」
    ///   · 选中里只要有**一个可更新** → 「更新（N 个）」（N = 可更新个数，不是选中总数）
    ///   · 选中里只要有**一个已安装** → 「卸载（N 个）」（N = 已安装个数；排最后，红 #FF3B30）
    /// 没有对应动作的按钮就隐藏（<see cref="Visibility.Collapsed"/>，连位置一起让出去 ——
    /// 弹层是纵向排列的，谁出现谁消失都不会让别的按钮左右挪位）；
    /// 一个动作都没有时给一句「现在没有可做的动作」，不留一片空白。
    ///
    /// 这个方法在每次勾选变化后都会走一遍（点卡片、全选、反选、清空、刷新后重渲染），
    /// 所以按钮上的数量与弹层里的动作都是实时的。
    /// </summary>
    internal void UpdateBatchBar()
    {
        try
        {
            // 卡片是每次刷新重建的：在此处一并挂接选中逻辑（幂等，已挂过的直接跳过）。
            // 不依赖 MainWindow.Tools.cs 里多写一行接线代码，本文件自己闭环。
            EnsureCardSelectionWired();

            int n = _batchSelected.Count;
            bool show = n > 0;

            // 计数与显隐**先刷新、且不受下面任何 return 影响**：
            // 以前这一整段被 `if (_batchBar == null) return;` 挡在后面，只要批量框还没建出来
            // （或换了落脚处重建过），点卡片就一个数字都不更新 —— 现场表现就是
            // 「无论选中几个，都提示选中了 2 个」（停在最后一次成功刷新时的旧数字）。
            RefreshBatchCounts(show);

            // 批量条本体还没建出来：计数已经刷新完，这里到此为止（列表渲染完成后会再走一遍）。
            if (_batchBar == null) return;

            // 动作进行中（_batchBusy）**不碰显隐**：这时外面那一行正把批量框隐藏起来干活，
            // 期间又常会清空选中并刷新列表；在这儿写一次 Visible 只会让界面上闪一下。
            // 忙的时候显隐交给外面那一行，等收尾（_batchBusy 落回 false）再按选中数定夺。
            if (!_batchBusy) _batchBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

            // 一个都没选：整条藏起来、弹层也收掉（避免它挂在界面上指向一颗已经隐藏的按钮），
            // 一并把进度复位（下次露出来是干净的）。
            if (!show)
            {
                CloseBatchMenu();
                SetBatchActionVisible(_batchDisableBtn, false);
                SetBatchActionVisible(_batchEnableBtn, false);
                SetBatchActionVisible(_batchApplyBtn, false);
                SetBatchActionVisible(_batchUninstallBtn, false);
                if (_batchEmptyHint != null) _batchEmptyHint.Visibility = Visibility.Collapsed;
                HideBatchProgress();
                ApplyBatchToolbarVisibility();   // 一个都没选：外层功能框也要跟着收起来
                return;
            }

            // 分类统计：同一份选中清单只算一次，别为每颗按钮各遍历一遍。
            // uninstallable = 选中里**确实装着**的（批量卸载的目标），判据复用显示层的既有口径
            // IsVersionPlaceholder：版本是「(未安装)」占位的，说明清单里有它、机器上并没有它 ⇒ 没得卸。
            int enabledCount = 0, disabledCount = 0, updatable = 0, uninstallable = 0;
            foreach (var p in SelectedPlugins())
            {
                if (p.Disabled) disabledCount++; else enabledCount++;
                // 「可选升级」不计入 —— 与 UpdatableCount / 一键更新**同一个判据**（IsHardUpdatable）。
                // 原先这里数裸 HasUpdate，批量条上的「更新（N 个）」会比「一键更新 N 个」多算，
                // 同一个事实摆出两个不同的数（用户一眼就看出对不上）。
                if (IsHardUpdatable(UpdateOf(p))) updatable++;
                if (!IsVersionPlaceholder(p.Version)) uninstallable++;
            }

            // 数量写进按钮文字（「禁用（3 个）」）：一眼看清这一下会动几个。
            // 「所选」两个字不写：这颗按钮就长在「已选 N 个」的下拉框上，重复说明只是白占宽度。
            if (_batchDisableBtn != null) _batchDisableBtn.Content = enabledCount > 0 ? $"禁用（{enabledCount} 个）" : "禁用";
            if (_batchEnableBtn != null) _batchEnableBtn.Content = disabledCount > 0 ? $"启用（{disabledCount} 个）" : "启用";
            if (_batchApplyBtn != null) _batchApplyBtn.Content = updatable > 0 ? $"更新（{updatable} 个）" : "更新";
            if (_batchUninstallBtn != null) _batchUninstallBtn.Content = uninstallable > 0 ? $"卸载（{uninstallable} 个）" : "卸载";

            // 卸载按钮**不参与** anyAction：每个插件要么"启用中"要么"已禁用"，所以
            // uninstallable > 0 时 enabledCount + disabledCount 必然也 > 0 —— 有得卸就一定有前两个动作，
            // 加进来只会是一句永远为真的废话。
            bool anyAction = enabledCount > 0 || disabledCount > 0 || updatable > 0;
            SetBatchActionVisible(_batchDisableBtn, enabledCount > 0);
            SetBatchActionVisible(_batchEnableBtn, disabledCount > 0);
            SetBatchActionVisible(_batchApplyBtn, updatable > 0);
            SetBatchActionVisible(_batchUninstallBtn, uninstallable > 0);

            // 一个动作都没有：给一句话说明（比留一片空白更像个"功能框"）
            if (_batchEmptyHint != null)
                _batchEmptyHint.Visibility = anyAction ? Visibility.Collapsed : Visibility.Visible;

            // 忙的时候把动作按钮暂时压住（挡误点由 _batchBusy 兜底，这里只是别让人以为还能点）。
            // 按钮本身**照旧显示**（不隐藏）—— 批量更新跑到一半按钮整块消失会很突然，
            // 而且说明行下面此刻正被进度文案与进度条占着。
            // ★（本轮补）_pluginWriteBusy 也算忙：批量更新 / 批量卸载整轮占着写闸，这时点批量更新
            //   或批量卸载会被 PassPluginWriteGate 如实拒掉，按钮就该同步灰着。
            //   而"单颗更新跑着"或"市场安装 / 回滚重装跑着"的时候，这四颗按钮的
            //   **实际安全结论是分开的**（禁用 / 启用只写插件配置、不碰依赖图 ⇒ 完全可以继续：
            //   它们不跑任何命令、也不占这个写闸，单颗的「禁用插件」此刻照旧可点），
            //   ⇒ 外观就按这个结论分开走，别再统一压灰：把"本可以安全照做"的动作挡住，
            //   还会让同一件事的两个入口（单颗 vs 批量）出现两种可用性。
            bool batchIdle = !_batchBusy;                      // 四种批量动作之间不重入 —— 四颗都受它管
            bool graphIdle = !_pluginWriteBusy;                // 「会改依赖图」的写闸（市场安装 / 回滚重装也占着它）
            bool batchAndGraphIdle = batchIdle && graphIdle;   // 真跑命令的那两颗：两种忙都不行
            if (_batchDisableBtn != null) _batchDisableBtn.IsEnabled = batchIdle;                 // 只写配置 ⇒ 不写闸
            if (_batchEnableBtn != null) _batchEnableBtn.IsEnabled = batchIdle;                   // 只写配置 ⇒ 不写闸
            if (_batchApplyBtn != null) _batchApplyBtn.IsEnabled = batchAndGraphIdle;             // 跑命令 ⇒ 两边都管
            if (_batchUninstallBtn != null) _batchUninstallBtn.IsEnabled = batchAndGraphIdle;     // 跑命令 ⇒ 两边都管

            // 收尾：把**外层**（顶部那一行的落脚处 BatchBarHost 与「一键更新」）一起同步。
            // 点卡片 / 全选 / 反选 / 清空 / 动作完成都只会走到这里，而列表并不会重渲染 ——
            // 以前只有“列表渲染完成”那条路会设置外层显隐，于是选中了卡片外层却一直是 Collapsed，
            // 功能框永远不出现（本 bug 的根治点）。
            ApplyBatchToolbarVisibility();
        }
        catch (Exception ex) { Logger.LogError("UpdateBatchBar", ex); }
    }

    /// <summary>
    /// 清空选中（含卡片视觉：小方框与卡片高亮都跟着复位）。
    /// 旧版是遍历 PluginsPanel 的直接子元素找 CheckBox——改成卡片内嵌后那种找法一个也找不到，
    /// 这里改为「按卡片同步」。
    /// </summary>
    private void ClearBatchSelection()
    {
        _batchSelected.Clear();
        try
        {
            SyncAllCardVisuals();
        }
        catch (Exception ex) { Logger.LogError("ClearBatchSelection", ex); }
        UpdateBatchBar();
    }

    private void BatchClear_Click(object sender, RoutedEventArgs e) => ClearBatchSelection();

    // ══════════════════════════════════════════════════════════════
    //  ② 全选 / 反选：只改内存状态 + 原地同步视觉，不重建列表
    // ══════════════════════════════════════════════════════════════
    /// <summary>
    /// 全选：把**当前列表里的所有插件**都选中（含已禁用的）。
    /// <paramref name="invert"/> = true 时为反选（已选 ↔ 未选）。
    ///
    /// 性能口径（列表可能有几百张卡片，注意别在循环里反复重绘整页）：
    ///   · 只在内存里重算一次名字集合，**不重建卡片、不重渲染列表**；
    ///   · 卡片视觉走 <see cref="SyncAllCardVisuals"/> 原地改属性（一次 O(N) 遍历）；
    ///   · 批量条只刷新一次；写方框的 IsChecked 期间用内部开关挡住 Checked/Unchecked
    ///     回调（否则 N 张卡片会触发 N 次计数与可见性重算）。
    /// </summary>
    private void BatchSelectAll(bool invert)
    {
        try
        {
            var all = new List<string>();
            foreach (var p in _plugins)
            {
                if (p == null || string.IsNullOrWhiteSpace(p.Name)) continue;
                all.Add(p.Name);
            }

            // 反选：先定住「原来选中的是谁」，再整体重算——不能边遍历边改同一个集合
            var before = invert ? new HashSet<string>(_batchSelected, StringComparer.OrdinalIgnoreCase) : null;

            _batchSelected.Clear();
            foreach (string name in all)
                if (!invert || !before!.Contains(name)) _batchSelected.Add(name);
        }
        catch (Exception ex) { Logger.LogError("BatchSelectAll", ex); }

        SyncAllCardVisuals();
        UpdateBatchBar();
    }

    private void BatchSelectAll_Click(object sender, RoutedEventArgs e) => BatchSelectAll(invert: false);

    private void BatchInvert_Click(object sender, RoutedEventArgs e) => BatchSelectAll(invert: true);

    // ══════════════════════════════════════════════════════════════
    //  ③ 卡片选中：点整张卡片切换（小方框只做状态指示）
    // ══════════════════════════════════════════════════════════════
    //
    //  判定方式与理由（这层最容易踩坑，写清楚）：
    //  ── 为什么用 Preview 版事件（隧道阶段），而不是 MouseLeftButtonUp（冒泡阶段）：
    //     卡片里有「横向滚动的描述文本、图片缩略图」这类会消费鼠标事件的子元素
    //     （ScrollViewer 处理滚轮/拖拽、Image 参与命中测试），冒泡阶段的
    //     MouseLeftButtonUp 根本到不了卡片，表现就是「点卡片有时没反应」；
    //     而隧道阶段的 Preview* 一定先经过卡片，事件被谁标记 Handled 都不影响我们收到它。
    //  ── 为什么选 PreviewMouseLeftButtonDown 做判定、PreviewMouseLeftButtonUp 才切换：
    //     ① 按下就切换会在「想滚动/拖拽，手指按下去又松开」时误选，故按下只记录起点，
    //        松开时再核对（位移 ≤ <see cref="CardToggleMoveTolerance"/> 且起点在卡片内）才切换；
    //     ② 比 MouseLeftButtonUp 冒泡更可靠：无论子元素是否消费 up 事件，我们都能拿到。
    //  ── 怎么区分「卡片内的按钮 / 链接」：
    //     沿用项目里既有的「可点元素都标手型光标」约定（ButtonFx 的 IsInteractive 也是这条），
    //     从 e.OriginalSource 往上找最近的交互元素；按钮 / 小方框 / 滚动条一律跳过，不触发选中。
    //     用 OriginalSource（命中测试的**最深处**元素）而不是 Source，子元素标了 Handled 也照样能溯源。
    //  ── 不设 e.Handled = true：卡片按钮、插件名链接的既有 Click 行为必须原样保留；
    //     我们只是在旁边读一次事件。
    //  ── 卡片本身**不设**手型光标：一是以免 ButtonFx 一并给整张卡片挂上悬停缩放（和本文件的
    //     点击脉冲动画争同一个 RenderTransform），二是卡片不是"链接"。
    //     可点性靠小方框与批量条里的说明行表达。
    //  ── 链接走的是「手型光标的 TextBlock」这条既有约定（卡片里的插件名、作者名都是）。
    //     项目里没有用 FrameworkContentElement 的 Hyperlink，因此不额外处理那类元素；
    //     真要在卡片里加 Hyperlink，把它显式列进 IsInteractivePart 即可。

    /// <summary>按下与松开之间的允许位移（设备无关单位）：超过它视为滚动/拖拽，不算点击。</summary>
    private const double CardToggleMoveTolerance = 8.0;

    /// <summary>点击脉冲动画时长（毫秒）：短促、可连点。</summary>
    private const int CardTapAnimMs = 120;

    /// <summary>连点时的代次：只有最后一次动画允许清理变换（避免旧动画把新动画的缩放清掉）。</summary>
    private int _cardTapVersion;

    /// <summary>按下时是否落在某张卡片上（避免「卡片外按下、卡片内松开」也算选中）。</summary>
    private bool _batchPressOnCard;

    /// <summary>按下时的光标位置（用于识别滚动/拖拽）。</summary>
    private Point _batchPressPoint;

    /// <summary>写方框 IsChecked 期间置位：挡掉 Checked/Unchecked 回调里的重复刷新（数据已由外层写好）。</summary>
    private bool _batchSyncingVisuals;

    // ── 卡片选中态的两套配色（**固定最暗/最亮两端都在亮度 150 以内**，日间自检断言安全）──
    // 夜间：蓝 + 白 18% 提亮；日间：同色蓝 + 黑 5% 压深（在浅底上表现为一层很淡的灰蓝）。
    // 蓝色只用在描边上，不动卡片里的文字，因此不会影响「日间卡片文字亮度 ≤150」那条断言。
    private static readonly (Color Fill, Color Line) CardSelDark =
        (Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF), Color.FromRgb(0x00, 0x7A, 0xFF));
    private static readonly (Color Fill, Color Line) CardSelLight =
        (Color.FromArgb(0x0D, 0x00, 0x00, 0x00), Color.FromRgb(0x00, 0x7A, 0xFF));

    /// <summary>一张卡片的选中态相关对象（挂在卡片上的附加属性里，卡片重建即随之丢弃）。</summary>
    private sealed class BatchCardState
    {
        public Border Card = null!;      // 卡片本体（改描边）
        public CheckBox? Indicator;      // 遗留的小方框（现在不再创建，只在清理时用得到）
        public Grid? Wrap;               // 卡片内容（若是栅格，就把选中底板插进去当最底层）
        public Border? Overlay;          // 选中底板（独立一层，动画只动它的 Opacity）
    }

    /// <summary>状态对象挂在卡片上（附加属性）：卡片被重建后自然丢弃，不需要另建字典维护。</summary>
    private static readonly DependencyProperty BatchCardStateProperty =
        DependencyProperty.RegisterAttached("BatchCardState", typeof(BatchCardState), typeof(MainWindow),
            new PropertyMetadata(null));

    /// <summary>小方框上的名字（选中判定与视觉同步都靠它，忽略大小写）。</summary>
    private static readonly DependencyProperty BatchNameProperty =
        DependencyProperty.RegisterAttached("BatchName", typeof(string), typeof(MainWindow),
            new PropertyMetadata(""));

    private static string BatchNameOf(DependencyObject? o)
        => o?.GetValue(BatchNameProperty) as string ?? "";

    /// <summary>
    /// 给一张插件卡片挂上「点卡片 = 选中/取消」：记录名字 → 挂预览事件 → 建选中视觉层。
    /// 卡片每次刷新都是新对象，所以这一步由 <see cref="UpdateBatchBar"/> 统一补挂（幂等）。
    /// 卡片里若还留着旧版的勾选框，在此处一并收起（选中态只看卡片外框）。
    /// </summary>
    internal void AttachCardSelection(Border card, PluginManager.Plugin p)
        => AttachCardSelection(card, p?.Name ?? "");

    /// <summary>同上（只要名字的版本：自动补挂时名字取卡片上的 Tag，不必去翻插件对象）。</summary>
    private void AttachCardSelection(Border card, string name)
    {
        try
        {
            if (card == null || string.IsNullOrWhiteSpace(name)) return;
            if (card.GetValue(BatchCardStateProperty) is BatchCardState) return;   // 已经挂过了

            var box = FindCardIndicator(card);
            if (box != null) HideCardIndicator(box, name);

            var state = new BatchCardState
            {
                Card = card,
                Indicator = box,
                Wrap = card.Child as Grid
            };

            // 选中底板：插在内容最底层的一层半透明色块（ZIndex=-1）。
            // 做成**独立一层**而不是直接改卡片底色，有两个理由：
            //   ① 卡片底色是主题映射表里登记过的颜色，改它就得处理日间/夜间往返；
            //   ② 动画只动这一层的 Opacity，画刷本身没有动画时钟，主题补刷不会被跳过。
            // IsHitTestVisible=false：不抢命中测试，「点卡片空白处」照旧落到卡片本体上。
            if (state.Wrap != null)
            {
                state.Overlay = new Border
                {
                    CornerRadius = new CornerRadius(9),     // 比卡片自身圆角小一点，不压到描边
                    Background = new SolidColorBrush(Colors.Transparent),
                    IsHitTestVisible = false,
                    Opacity = 0.0
                };
                Panel.SetZIndex(state.Overlay, -1);
                state.Wrap.Children.Insert(0, state.Overlay);
            }

            card.SetValue(BatchCardStateProperty, state);
            card.SetValue(BatchNameProperty, name);
            card.Tag = name;                         // 与 AddPluginCheckbox 用同一套标记

            card.PreviewMouseLeftButtonDown += CardSelection_PreviewDown;
            card.PreviewMouseLeftButtonUp += CardSelection_PreviewUp;

            SyncCardVisual(state, _batchSelected.Contains(name));
        }
        catch (Exception ex) { Logger.LogError("AttachCardSelection", ex); }
    }

    /// <summary>
    /// 给列表里所有卡片补挂选中逻辑（幂等）。只在 Panel.Children 上走一层：
    /// 卡片是 PluginsPanel 的直接子元素，比遍历整棵视觉树便宜得多，也不会重复访问。
    /// 一并把每张卡片上残留的小方框强制收起（见 <see cref="HideCardIndicator"/>）——
    /// 现场反馈"勾选框没消失"，那多半是它被别处重新塞回卡片或又被重设成可见，
    /// 而挂载只发生一次；因此每次刷新列表时都补一刀，不依赖挂载那一刻的时序。
    /// </summary>
    private void EnsureCardSelectionWired()
    {
        try
        {
            if (PluginsPanel == null) return;
            foreach (var card in CardsInPanel())
            {
                string cardName = card.Tag as string ?? "";

                if (card.GetValue(BatchCardStateProperty) is BatchCardState state)
                {
                    // 已挂过：把残留的小方框补收掉。这里查"整张卡片"而不是只查记住的那一个 ——
                    // 挂载那一刻勾选框可能还没被塞进卡片，或者后来被别处重新塞了一次，
                    // 只认最初那一个的话，后塞进来的方框就永远留在界面上（现场两次反馈）。
                    if (state.Indicator == null) state.Indicator = FindCardIndicator(card);
                    if (state.Indicator != null) HideCardIndicator(state.Indicator, cardName);
                    HideCardIndicators(card, cardName);
                    continue;
                }

                if (cardName.Length == 0) continue;      // 没有名字 = 不是插件卡片（批量条自己就在这一层）
                // 注意：这里**不再**以"找得到勾选框"为前提。勾选框只是视觉残留物，
                // 拿它当开关的话，卡片结构一变（或它没被塞进卡片）整张卡片就点不动了。
                AttachCardSelection(card, cardName);     // 名字就记在卡片 Tag 上，不再去翻插件对象
            }
        }
        catch (Exception ex) { Logger.LogError("EnsureCardSelectionWired", ex); }
    }

    /// <summary>列表里的插件卡片（批量条子元素也是 Border 的可能来源，但它没有勾选框、也不在列表里）。</summary>
    private IEnumerable<Border> CardsInPanel()
        => PluginsPanel == null ? Enumerable.Empty<Border>() : PluginsPanel.Children.OfType<Border>();

    /// <summary>
    /// 把卡片右上角那个小方框彻底收掉：选中态一律由卡片的蓝色外框表示，
    /// 方框既不能点也不该再占地方（现场两条反馈："为什么勾选框虽然不能勾选了还有框"、
    /// "勾选框也完全没消失"）。幂等，可以反复调用；
    /// <c>IsHitTestVisible=false</c> 保证它万一被重新显示也不吃点击。
    /// </summary>
    private static void HideCardIndicator(CheckBox box, string name)
    {
        try
        {
            box.IsHitTestVisible = false;
            box.Focusable = false;
            box.Visibility = Visibility.Collapsed;
            box.Opacity = 0.0;
            box.Tag = null;                          // 名字改挂在卡片上
            box.ToolTip = null;                      // 不再解释一个已经看不见的元素
            if (!string.IsNullOrEmpty(name)) box.SetValue(BatchNameProperty, name);   // 兜底：别处还会按名字读它
        }
        catch (Exception ex) { Logger.LogError("HideCardIndicator", ex); }
    }

    /// <summary>
    /// 把卡片里**所有**残留的勾选框都收掉（不止第一个）：现场反馈"勾选框没消失"，
    /// 而卡片结构将来可能变（内容多包一层栅格、勾选框换位置），只找第一个容易漏。
    /// 走的是 WPF 逻辑树而不是视觉树：重建中的卡片还没经过布局，视觉子元素可能还没生成，
    /// 逻辑树却一定拿得到 —— 这条不依赖布局时序，因此"卡片刚建好就被清"也照样生效。
    /// </summary>
    private static void HideCardIndicators(DependencyObject? card, string name)
    {
        try
        {
            if (card == null) return;
            foreach (object? child in LogicalTreeHelper.GetChildren(card))
            {
                if (child is not DependencyObject d) continue;
                if (d is CheckBox box) HideCardIndicator(box, name);
                else HideCardIndicators(d, name);      // 递归往下：勾选框常被包在卡片内容的外层容器里
            }
        }
        catch (Exception ex) { Logger.LogError("HideCardIndicators", ex); }
    }

    /// <summary>
    /// 在卡片里找 <c>AddPluginCheckbox</c> 建的那个小方框。
    /// 主线结构是「卡片 → 两列栅格 → 右列方框」，但别把结构写死：先看直接子元素，
    /// 再在"卡片子元素"这一层往下找一格 —— 卡片内容将来多包一层栅格/面板也照样找得到，
    /// 免得方框找不到就留在界面上不消失。
    /// </summary>
    private static CheckBox? FindCardIndicator(DependencyObject? card)
    {
        if (card == null) return null;

        int n = VisualTreeHelper.GetChildrenCount(card);
        for (int i = 0; i < n; i++)
            if (VisualTreeHelper.GetChild(card, i) is CheckBox box) return box;

        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(card, i);
            int m = VisualTreeHelper.GetChildrenCount(child);
            for (int j = 0; j < m; j++)
                if (VisualTreeHelper.GetChild(child, j) is CheckBox nested) return nested;
        }
        return null;
    }

    /// <summary>卡片按下：只记录起点（真正切换在松开时判定，避免滚动/拖拽误选）。</summary>
    private void CardSelection_PreviewDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            var card = sender as Border;
            _batchPressOnCard = card != null && !IsInteractivePart(e.OriginalSource);
            _batchPressPoint = e.GetPosition(this);
        }
        catch (Exception ex) { Logger.LogError("CardSelection_PreviewDown", ex); }
    }

    /// <summary>卡片松开：起点在卡片内、且几乎没有位移，才算一次「点击卡片」。</summary>
    private void CardSelection_PreviewUp(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (sender is not Border card) return;
            if (!_batchPressOnCard) return;
            _batchPressOnCard = false;

            var now = e.GetPosition(this);
            if (Math.Abs(now.X - _batchPressPoint.X) > CardToggleMoveTolerance ||
                Math.Abs(now.Y - _batchPressPoint.Y) > CardToggleMoveTolerance) return;   // 拖拽/滚动，不当作点击

            ToggleCardSelection(card);
        }
        catch (Exception ex) { Logger.LogError("CardSelection_PreviewUp", ex); }
    }

    /// <summary>
    /// 这个被点到的元素算不算「卡片里的交互部件」（点它就不该切换选中）。
    /// 判据：往上找最近的**手型光标**元素——项目里按钮 / 链接 / 可点文字都这么标；
    /// 按钮 / 小方框 / 滚动条 / 输入框再显式兜一道（模板细节变了也不会漏）。
    /// </summary>
    private static bool IsInteractivePart(object? originalSource)
    {
        try
        {
            DependencyObject? o = originalSource as DependencyObject;
            for (int depth = 0; o != null && depth < 12; depth++)
            {
                if (o is ScrollBar || o is Thumb) return true;                     // 滚动条：让它自己滚
                if (o is TextBoxBase) return true;                                 // 输入类：让光标落进去
                if (o is ButtonBase) return true;                                  // 卡片按钮 / 小方框（模板细节变了也不会漏）
                if (o is FrameworkElement fe && fe.Cursor == Cursors.Hand) return true;   // 插件名、作者头像与名字等
                o = VisualTreeHelper.GetParent(o);
            }
        }
        catch { }
        return false;
    }

    /// <summary>切换一张卡片的选中状态：内存状态 → 这张卡片的视觉 → 批量条计数（点击路径只做这三步）。</summary>
    private void ToggleCardSelection(Border card)
    {
        try
        {
            string name = BatchNameOf(card);
            if (name.Length == 0) return;

            bool now = !_batchSelected.Contains(name);
            if (now) _batchSelected.Add(name); else _batchSelected.Remove(name);

            var state = card.GetValue(BatchCardStateProperty) as BatchCardState;
            if (state != null)
            {
                SyncCardVisual(state, now);
                PlayCardTapAnimation(state);   // 短促脉冲：不阻塞、可连点
            }
        }
        catch (Exception ex) { Logger.LogError("ToggleCardSelection", ex); }
        UpdateBatchBar();
    }

    /// <summary>
    /// 写入某张卡片的选中视觉：小方框打勾 + 卡片描边与底色。
    /// 走内部开关挡掉方框的 Checked/Unchecked 回调——数据在外层已经写好，
    /// 否则全选几百张时会触发几百次计数与整条批量条重算。
    /// </summary>
    private void SyncCardVisual(BatchCardState? state, bool selected)
    {
        if (state == null) return;
        try
        {
            _batchSyncingVisuals = true;
            try
            {
                if (state.Indicator != null && state.Indicator.IsChecked != selected)
                    state.Indicator.IsChecked = selected;
            }
            finally { _batchSyncingVisuals = false; }

            ApplyCardSelectionVisual(state, selected);
        }
        catch (Exception ex) { Logger.LogError("SyncCardVisual", ex); }
    }

    /// <summary>整页卡片视觉同步（全选 / 反选 / 清空 / 主题刷新走这里）：只改属性，不重建任何卡片。</summary>
    private void SyncAllCardVisuals()
    {
        try
        {
            bool anyWired = false;
            foreach (var card in CardsInPanel())
            {
                var state = card.GetValue(BatchCardStateProperty) as BatchCardState;
                if (state == null) continue;
                anyWired = true;
                bool selected = _batchSelected.Contains(BatchNameOf(card));
                _batchSyncingVisuals = true;
                try
                {
                    if (state.Indicator != null && state.Indicator.IsChecked != selected)
                        state.Indicator.IsChecked = selected;
                }
                finally { _batchSyncingVisuals = false; }
                ApplyCardSelectionVisual(state, selected);
            }
            if (anyWired) return;

            // 卡片还没挂过选中逻辑（首次渲染的时序）：先补挂，补挂时会各自同步一次
            EnsureCardSelectionWired();
        }
        catch (Exception ex) { Logger.LogError("SyncAllCardVisuals", ex); }
    }

    /// <summary>
    /// 选中态视觉：描边换成高亮蓝 + 一层很淡的底色。
    /// 两套主题各自取色（见 <see cref="CardSelDark"/> / <see cref="CardSelLight"/>），
    /// 且都**不走主题颜色映射表**（固定值，刻意不用映射项）：避免与映射表的键/值撞车导致二次改写。
    /// 主题切换后由 <see cref="UpdateBatchBar"/> → 同步一次即可重新取色。
    /// </summary>
    private static void ApplyCardSelectionVisual(BatchCardState state, bool selected)
    {
        try
        {
            if (state.Card == null) return;
            var sel = ThemeManager.IsDark ? CardSelDark : CardSelLight;

            state.Card.BorderThickness = new Thickness(selected ? 1.4 : 0);
            state.Card.BorderBrush = selected ? new SolidColorBrush(sel.Line) : null;

            if (state.Overlay != null)
            {
                // 只用**外框**表示选中：内部不要任何衬色（现场反馈"里面那点不一样的颜色"很难看，
                // 日间 / 夜间 / 毛玻璃三种样式下更明显）。这一层保留但完全透明，
                // 动画与主题补刷逻辑不受影响。
                state.Overlay.Background = Brushes.Transparent;
                state.Overlay.Opacity = selected ? 1.0 : 0.0;
            }

            // 右上角的小方框：一律收掉（选中与否只看外框）。这里每次写视觉都补一刀，
            // 是因为它可能被别处重新塞回卡片或又被设成可见（现场反馈"勾选框没消失"）。
            // 只查"记住的那一个"（O(1)）：整棵卡片的全量清理交给 EnsureCardSelectionWired /
            // AddPluginCheckbox 去做 —— 该路径在每次点击卡片时都会执行，别在这做全树遍历。
            if (state.Indicator == null) state.Indicator = FindCardIndicator(state.Card);
            if (state.Indicator != null) HideCardIndicator(state.Indicator, BatchNameOf(state.Card));
        }
        catch (Exception ex) { Logger.LogError("ApplyCardSelectionVisual", ex); }
    }

    /// <summary>
    /// 点击卡片的短促动效（约 <see cref="CardTapAnimMs"/> 毫秒）。
    ///
    /// 三条硬要求怎么满足的：
    ///   · 不卡顿：只动 Opacity 与 ScaleTransform 两个合成层属性，动画跑在渲染线程上，
    ///     不开线程、不 Thread.Sleep、不调 UpdateLayout，UI 线程不阻塞（不 await、不 DoEvents）。
    ///   · 可连点：每次点击自增代次，收尾时只有**最后一次**动画允许清理变换；
    ///     否则前一次动画的 Completed 会把后一次正在用的缩放清掉，出现「缩放卡住」。
    ///   · 动画结束后状态正确：缩放用 FillBehavior.Stop（终点 1.0 与静态值一致，收尾时清掉变换，
    ///     文字恢复锐利渲染）；Opacity 同样用 FillBehavior.Stop 并在结束后显式写回 1.0，
    ///     少写这一句的话动画结束后基础值会回退成 0.88，卡片会**永久变淡**。
    ///   · 卡片本体没有别的 RenderTransform（卡片不设手型光标 ⇒ ButtonFx 不会给它挂悬停缩放），
    ///     所以这里的 ScaleTransform 独占，不会和既有效果打架。
    /// </summary>
    private void PlayCardTapAnimation(BatchCardState state)
    {
        try
        {
            var card = state.Card;
            if (card == null) return;

            int version = ++_cardTapVersion;

            // 缩放贴边不溢出（1.0 → 0.972 → 1.0），缩放中心取卡片中心
            var scale = card.RenderTransform as ScaleTransform;
            if (scale == null)
            {
                scale = new ScaleTransform(1.0, 1.0);
                card.RenderTransformOrigin = new Point(0.5, 0.5);
                card.RenderTransform = scale;
            }

            var dur = TimeSpan.FromMilliseconds(CardTapAnimMs);
            var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 };

            var sx = new DoubleAnimation(0.972, 1.0, dur) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
            var sy = new DoubleAnimation(0.972, 1.0, dur) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
            sx.Completed += (_, _) =>
            {
                if (version != _cardTapVersion) return;      // 连点：只有最后一次负责收尾
                try { if (card.RenderTransform is ScaleTransform) card.RenderTransform = null; }
                catch { }
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, sx);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, sy);

            var op = new DoubleAnimation(0.88, 1.0, dur) { FillBehavior = FillBehavior.Stop };
            op.Completed += (_, _) => { try { card.Opacity = 1.0; } catch { } };
            card.BeginAnimation(UIElement.OpacityProperty, op);
        }
        catch (Exception ex) { Logger.LogError("PlayCardTapAnimation", ex); }
    }

    // ══════════════════════════════════════════════════════════════
    //  ④ 卡片右上角的小方框（**已废弃**：不再创建、不再上屏）
    // ══════════════════════════════════════════════════════════════
    /// <summary>
    /// 旧版会往卡片右上角塞一个 CheckBox 当"选中指示器"。现在**不再创建这个元素了**：
    /// 选中与否一律由卡片的蓝色外框表示（现场两次反馈："里面那点不一样的颜色是啥"、
    /// "为什么勾选框虽然不能勾选了还有框"）。没有元素，就没有"它怎么还没消失"这回事。
    ///
    /// 方法本身保留（名字、签名不变，外部调用点一个字都不用改），现在只做两件事：
    ///   ① 在卡片上登记插件名（<c>card.Tag</c>）—— 卡片选中逻辑靠它认人；
    ///   ② 把旧的指示器一并清掉（万一还有别处塞进来的方框，见 <see cref="HideCardIndicator"/>）。
    /// 卡片内容结构也不再被包成两列栅格：卡片子元素保持调用方给的原样，
    /// <see cref="SyncAllCardVisuals"/> 等按卡片同步的逻辑因此完全不受影响。
    /// </summary>
    internal void AddPluginCheckbox(Border card, PluginManager.Plugin p)
    {
        try
        {
            if (card == null || p == null) return;
            if (card.GetValue(BatchCardStateProperty) is BatchCardState) return;    // 已经登记过了

            card.Tag = p.Name;

            // 清残留：卡片里若还有勾选框（旧结构 / 别处塞进来的），一并收掉
            HideCardIndicators(card, p.Name);

            // 状态对象、选中底板、预览事件都由它一次性建好（与自动补挂走同一条路）
            AttachCardSelection(card, p.Name);
        }
        catch (Exception ex) { Logger.LogError("AddPluginCheckbox", ex); }
    }

    /// <summary>
    /// 勾选变化：写状态 → 刷新批量条（只有这一个入口改勾选，不会漏刷新）。
    /// 视觉同步期间（<see cref="_batchSyncingVisuals"/>）直接跳过：那批写入由外层一次性处理，
    /// 否则全选几百张卡片会触发几百次重算。
    /// </summary>
    internal void BatchCheckChanged(string name, bool selected)
    {
        if (_batchSyncingVisuals) return;
        try
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            if (selected) _batchSelected.Add(name);
            else _batchSelected.Remove(name);
        }
        catch (Exception ex) { Logger.LogError("BatchCheckChanged", ex); }
        UpdateBatchBar();
    }

    // ══════════════════════════════════════════════════════════════
    //  ⑤ 批量禁用
    // ══════════════════════════════════════════════════════════════
    private async void BatchDisable_Click(object sender, RoutedEventArgs e)
    {
        if (_batchBusy) return;
        var targets = SelectedPlugins();
        if (targets.Count == 0) { GuardDialog.Show("尚未选中任何插件。", "批量禁用", MessageBoxButton.OK, MessageBoxImage.Information); return; }

        var r = GuardDialog.Show(
            $"要将这 {targets.Count} 个插件一次性禁用？\n\n" +
            BatchNameList(targets) + "\n\n" +
            "将修改插件配置（修改前自动备份，也可在「快照」页回退），重启 DSH 后生效。",
            "确认批量禁用", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (r != MessageBoxResult.OK) return;

        _batchBusy = true;
        try
        {
            if (PluginsSummaryText != null) PluginsSummaryText.Text = $"正在关闭这 {targets.Count} 个插件…";
            // DisableMany 自己就是「一次备份 + 一次写入」，这里绝不再逐个调 Disable
            var (done, detail) = await Task.Run(() => PluginManager.DisableMany(targets));

            AddEvent(done.Count > 0
                    ? $"已批量禁用 {done.Count} 个插件：{string.Join("、", done)}"
                    : $"批量禁用没有改动：{detail}",
                done.Count > 0 ? EventKind.Warn : EventKind.Info);
            Logger.Log($"批量禁用：请求 {targets.Count} 个，实际写入 {done.Count} 个（{string.Join("、", done)}）；{detail}");

            GuardDialog.Show(
                (done.Count > 0
                    ? $"✅ 已关闭 {done.Count} 个插件。"
                    : "没有插件被改动。") + "\n\n" +
                detail + "\n\n" +
                (done.Count > 0
                    ? "重启 DSH 后生效。配置被改坏时可在「快照」页一键回退。"
                    : "这些插件可能本来就处于关闭状态。"),
                done.Count > 0 ? "批量禁用完成" : "批量禁用",
                MessageBoxButton.OK,
                done.Count > 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);

            if (done.Count > 0) ClearBatchSelection();
            await RefreshPluginsAsync(true);
        }
        catch (Exception ex)
        {
            Logger.LogError("BatchDisable_Click", ex);
            AddEvent("批量禁用没能完成，详情见弹窗与日志", EventKind.Bad);
            GuardDialog.Show("批量禁用没能完成，配置文件可能已被改动一部分，可在「快照」页回退。\n\n" + LogPromise("详细原因已记入日志，可在「日志」页查看。"),
                "批量禁用失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _batchBusy = false; UpdateBatchBar(); }
    }

    // ══════════════════════════════════════════════════════════════
    //  ⑥ 批量启用
    // ══════════════════════════════════════════════════════════════
    private async void BatchEnable_Click(object sender, RoutedEventArgs e)
    {
        if (_batchBusy) return;
        var targets = SelectedPlugins();
        if (targets.Count == 0) { GuardDialog.Show("尚未选中任何插件。", "批量启用", MessageBoxButton.OK, MessageBoxImage.Information); return; }

        // 不兼容的先说清楚（和单个启用时的提醒口径一致）
        int broken = targets.Count(p => p.Compatibility == PluginManager.Compat.Broken);
        int partial = targets.Count(p => p.Compatibility == PluginManager.Compat.Partial);

        var r = GuardDialog.Show(
            $"要将这 {targets.Count} 个插件重新启用？\n\n" +
            BatchNameList(targets) + "\n\n" +
            (broken > 0
                ? $"⛔ 其中 {broken} 个声明不支持当前引擎版本（{_currentDshVersion}），打开后很可能出问题；\n"
                : "") +
            (partial > 0
                ? $"🟡 其中 {partial} 个不是作者优先适配的版本，可能有个别小毛病；\n"
                : "") +
            ((broken > 0 || partial > 0) ? "\n" : "") +
            "将从插件配置中移除禁用记录（修改前自动备份），重启 DSH 后生效。",
            broken > 0 ? "确认批量启用 · 注意不兼容" : "确认批量启用",
            MessageBoxButton.OKCancel,
            broken > 0 ? MessageBoxImage.Warning : MessageBoxImage.Question);
        if (r != MessageBoxResult.OK) return;

        _batchBusy = true;
        try
        {
            if (PluginsSummaryText != null) PluginsSummaryText.Text = $"正在启用这 {targets.Count} 个插件…";
            // EnableMany 与 DisableMany 对称：一次备份 + 一次写入 + 写后校验
            var (enabled, detail) = await Task.Run(() => PluginManager.EnableMany(targets, force: true));

            AddEvent(enabled.Count > 0
                    ? $"已批量启用 {enabled.Count} 个插件：{string.Join("、", enabled)}"
                    : "批量启用没有改动：" + detail,
                enabled.Count > 0 ? EventKind.Good : EventKind.Info);
            Logger.Log($"批量启用：请求 {targets.Count} 个，实际移除禁用记录 {enabled.Count} 个（{string.Join("、", enabled)}）；{detail}");

            GuardDialog.Show(
                (enabled.Count > 0
                    ? $"✅ 已重新启用 {enabled.Count} 个插件。"
                    : "没有插件被改动。") + "\n\n" +
                detail + "\n\n" +
                (enabled.Count > 0
                    ? "重启 DSH 后生效。"
                    : "它们本来就没有禁用记录；若卡片仍显示已关闭，通常是被其他方式关闭的。"),
                enabled.Count > 0 ? "批量启用完成" : "批量启用",
                MessageBoxButton.OK,
                enabled.Count > 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);

            if (enabled.Count > 0) ClearBatchSelection();
            await RefreshPluginsAsync(true);
        }
        catch (Exception ex)
        {
            Logger.LogError("BatchEnable_Click", ex);
            AddEvent("批量启用没能完成，详情见弹窗与日志", EventKind.Bad);
            GuardDialog.Show("批量启用没能完成，可重新勾选后再试。\n\n" + LogPromise("详细原因已记入日志，可在「日志」页查看。"), "批量启用失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _batchBusy = false; UpdateBatchBar(); }
    }

    // ══════════════════════════════════════════════════════════════
    //  ⑦ 批量更新（逐个顺序执行，带 i/total 进度）
    // ══════════════════════════════════════════════════════════════
    private async void BatchUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_batchBusy) return;

        // 只有"确实查到了新版本"的才更新（没有更新信息的不推测目标版本）。
        // ★ 判据必须与「一键更新」**同一个**（BatchUpdate 与 Tools.cs 的「一键更新」目标集都用 IsHardUpdatable）：
        //   原先这里数裸 HasUpdate，会把「可选升级」（具名分支 / 标签被移动）的插件一起升掉
        //   ⇒ 直接绕过用户「提示出来、由用户决定」的要求。这不是显示问题，是**真动了用户钉住的版本**。
        var targets = SelectedPlugins().Where(p => IsHardUpdatable(UpdateOf(p))).ToList();
        if (targets.Count == 0)
        {
            GuardDialog.Show("选中的插件里没有可更新的。\n\n如果刚装完尚未查过新版本，先点工具条上的「刷新」再试。",
                "批量更新", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string current = VersionMemory.Pin.Length > 0 ? VersionMemory.Pin : _currentDshVersion;
        var lines = new List<string>();
        var atRisk = new List<string>();
        foreach (var p in targets)
        {
            var u = UpdateOf(p)!;
            var band = PluginManager.EvaluateBand(u.NewRequirement, current);
            string mark = band switch
            {
                PluginManager.Compat.Ok => "✅",
                PluginManager.Compat.Partial => "🟡",
                PluginManager.Compat.Broken => "⛔",
                _ => "❔"
            };
            lines.Add($"  {mark} {PluginMarket.MarketPlugin.FormatDisplayName(p.Name)}　{u.Installed} → {u.Latest}");
            if (band == PluginManager.Compat.Broken) atRisk.Add(p.Name);
        }

        var r = GuardDialog.Show(
            $"要更新这 {targets.Count} 个插件？下面是要装上的新版本：\n\n" +
            string.Join("\n", lines) + "\n\n" +
            "✅ 完全兼容　🟡 能用但不是作者优先适配的版本　⛔ 不兼容　❔ 作者未声明\n\n" +
            (atRisk.Count > 0
                ? $"注意：{string.Join("、", atRisk)} 声明不支持当前引擎版本，更新后可能报错（可在「快照」页回滚）。\n\n"
                : "") +
            // 与「批量卸载」的确认框同一句（逐字复用，不另编第二句）：两者都会改依赖图，
            // 引擎在跑时同样可能因文件被占用而失败。这一句是静态文本，不引入 await，
            // 以免撑开本方法里「确认框 → 写闸」那段无 await 的原子段。
            "若 DSH 正在运行，操作可能因文件被占用而失败（建议先停止引擎）。\n\n" +
            "它们会依次安装，过程中可查看进度；全部装完需重启 DSH 才会生效。是否继续？",
            atRisk.Count > 0 ? "确认批量更新 · 注意不兼容" : "确认批量更新",
            MessageBoxButton.OKCancel,
            atRisk.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Question);
        if (r != MessageBoxResult.OK) return;

        // ★（本轮补）跨流程的写闸：本方法要跑一串命令改依赖图，与「一键更新 / 单颗更新 / 重新安装」
        //   （_updatingBusy）跑的是同一类命令、动的是同一份 node_modules / pnpm-lock.yaml。
        //   原先两边各管各的 ⇒ 一键更新跑着的时候还能开始批量更新，两条命令并发改同一个包目录。
        //   闸门只有一份（PassPluginWriteGate），被拒时它自己会说一句，绝不静默无反应。
        //   放在确认框**之后**、与下面 _batchBusy = true **紧挨着**：既不在模态框那一段无谓占闸，
        //   中间也没有任何 await（查与落之间插不进第二个动作）。
        if (!PassPluginWriteGate()) return;

        // 只在**本方法真的开了表**之后才允许 finally 收尾。为什么不能只看 _opProgressTimer：
        // 这两条批量路径把 BeginOpProgress 放在循环**里面**（每个插件一条进度），
        // 循环体里任何一个 per-item 步骤抛异常，最后一项的表就留在了界面上
        // ⇒ finally 负责把这"最后一项"收掉。而确认框/早退 return 都在开表之前，
        // 此刻若别处（启动前体检 / 单个更新）正开着表，只看"表开着"就会把别人的进度条误收掉。
        // 这个局部量是"本方法开的表"的唯一事实。
        bool opOpen = false;
        _batchBusy = true;
        BeginPluginWriteState();
        int okCount = 0;
        var failed = new List<string>();
        try
        {
            // 改插件清单有一定风险 ⇒ 先按策略存一份快照兜底（与「一键更新」「单个更新」同一口径）
            string snapNote = "";
            if (SnapshotPolicy.NeedSnapshot(GuardAction.UpdatePlugin))
            {
                if (PluginsSummaryText != null) PluginsSummaryText.Text = $"正在给这 {targets.Count} 个插件打快照…";
                snapNote = await Task.Run(() => SnapshotBeforePluginBatch(targets.Count));
            }

            for (int i = 0; i < targets.Count; i++)
            {
                var p = targets[i];
                var u = UpdateOf(p)!;
                string depSpec = PluginManager.DepSpec(p.Name);

                // ★ 缺 Git 闸门（唯一入口）：清单里这条声明是 git 源、而本机 PATH 里确实没有 git
                //   ⇒ 不跑命令。位置压在"半截安装自愈"之前：拦下就不该再动磁盘（那一步会把残留目录
                //   清掉，清完却装不上）。与下面"定不出更新目标"那一支同款：记明原因、继续下一项，
                //   不中止整批 —— 批里其它 npm 源插件照常更新（本闸门对它们一律放行）。
                if (BlockedForMissingGit(p.Name, depSpec, "批量更新"))
                {
                    failed.Add(GitMissingItemText(p.Name));
                    continue;
                }

                // 半截安装自愈（复用与单个/一键更新同一入口）：残留态先清目录；
                // 越界或清理失败（引擎占用）⇒ 记失败、继续下一项，不中止整批
                if (!EnsureNotBrokenInstall(p.Name, out string batchBrokenNote))
                {
                    failed.Add($"{p.Name}（{batchBrokenNote}）");
                    Logger.NoteDiagnosis($"批量更新 {p.Name}：半截安装清理未通过 ⇒ 跳过这一项");
                    continue;
                }
                // 目标的取法（与单个更新**同一入口**）：一律经 PluginManager.BuildUpdateArgs，
                // 与 Tools.cs 的 UpdateArgsFor 同款（npm ⇒ 包名@版本号；git 源 ⇒ `update 包名`）。
                //
                // ★ 这里以前是「自己拼」的一条分流：npm 走 BuildAddArgs、其余走
                //   BuildAddSourceArgs(RefreshSpec(…))。分流本身没错，但 git 源那一支给的是
                //   `add <仓库地址>`：pnpm 对**已解析过**的 git 源（锁里落成不可变 codeload tarball）
                //   认为旧提交已满足该 spec ⇒ 跳过解析（Lockfile is up to date, resolution step is skipped），
                //   退出码 **0**、锁文件 mtime 也变，提交却**没动** ⇒ 本壳记成"已更新"、下次照旧报有新版。
                //   （2026-09-18 现场缺陷；隔离实测与取舍的长文见 PluginManager.BuildUpdateArgs）
                //   ⇒ 单颗更新修好后，批量路径**必须同改**，否则批量对 git 源仍会空转。
                //   注意分工：批量与单个**只共用同一个参数构造入口**，成败判据另算（BatchUpdateVerdict，
                //   下面注释里那段"同一口径"说的就是判据，两者不冲突、互不替代）。
                string args = PluginManager.BuildUpdateArgs(p.Name, depSpec, u.Latest);

                // 给不出可靠目标（来源认不出 / 目标位是显示标签又纠不出真版本）⇒ **不执行命令**，
                // 记失败并继续下一项 —— 与单个更新（Tools.cs 的 UpdateArgsFor 后紧跟的空串检查）、
                // 一键更新同款。本项还没 BeginOpProgress ⇒ 这里没有表要收，直接 continue 即可。
                // （原分流把 Unknown 送进 BuildAddArgs，那条路不会返回空；改走唯一入口后这一支才可能出现，
                //   故空串必须在这里挡住，否则会带着空参数去跑 npx。）
                if (args.Length == 0)
                {
                    Logger.NoteDiagnosis($"批量更新 {p.Name}：给不出可靠的目标（来源「{depSpec}」、目标位「{u.Latest}」）⇒ 未执行命令");
                    failed.Add($"{p.Name}（无法确定更新目标，已跳过）");
                    continue;
                }

                string label = $"正在更新插件 {p.Name}（{i + 1}/{targets.Count}）";
                BeginOpProgress(label);
                opOpen = true;
                ShowBatchProgress($"{p.Name}（{i + 1}/{targets.Count}）", i, targets.Count);
                try { if (PluginsSummaryText != null) PluginsSummaryText.Text = $"{label} …"; } catch { }

                // relaxSupplyChainPolicy：与批量卸载同一处加固（理由写在那边的调用点上）——
                //   显式声明这次要放开 pnpm 包龄/锁文件策略，不再依赖命令行形状被 LooksLikePluginMutation
                //   认出来；显式 true 与形状命中在 RunCommandAsync 里是同一条 `||`、同一个
                //   InjectSupplyChainRelax ⇒ 行为等价。
                // ★ 跑命令**之前**记下这条 git 依赖当时解析到的提交 —— git 源的成败全靠这一端
                //   （另一端由 EvaluateUpdate 在命令跑完后自己读）。位置必须在 RunCommandAsync 之前：
                //   挪到判定那一行去读，就成了"拿跑完的锁文件跟自己比"、永远相等 ⇒
                //   `add <仓库地址>` 那种空转（退出码 0、提交没动）照样被记成成功 —— 本单要修的就是它。
                //   与卸载侧同款（PluginManager.EvaluateUninstall 的 existedBefore 也是跑命令前记下的）；
                //   npm 源的包这一段读出来是空串 ⇒ 版本比对那一套一字不受影响。
                string gitCommitBefore = PluginManager.ReadInstalledCommit(p.Name);
                var (cmdOk, output) = await RunCommandAsync("npx", args, timeoutMs: 600000, relaxSupplyChainPolicy: true);

                // ★ 成败判据与「单个更新」「一键更新」**同一口径**：npm 源读 node_modules\<包名>\package.json
                //   的版本与目标比对；git 源另按"锁文件里的提交有没有真的变成跑命令前记下的那个"判
                //   （纯函数 = MainWindow.EvaluateUpdate，见 MainWindow.Tools.cs / PluginManager.CompareInstalledToTarget
                //   / PluginManager.SameCommit）。两边都比不出来才回落命令退出码。
                //   这里**不能**再看 `ok`（= 退出码 == 0）：pnpm 12 会因某个依赖的构建脚本失败、
                //   或 Windows 的"另一个程序正在使用此文件 (os error 32)"返回非零，而包其实已经到位
                //   ⇒ 旧口径会报"失败"，与用户卡片上看到的版本正好相反（用户报告案例：dsh-mnemonic 更新）。
                var verdict = BatchUpdateVerdict(p, u, cmdOk, output, null, gitCommitBefore);
                bool ok = verdict.Succeeded;
                // 记账：只在**这一档**（ok 为真）盖"更新时间"章 —— 判据就是上面这个 ok（= verdict.Succeeded），
                //   不另立一套"成没成"的判法（本项目要求判据只留一份）。
                //   为什么是这一档：ok 已按磁盘事实合成完毕，包含"命令退出码非零、但磁盘上版本（或 git 源的提交）
                //   已经到位"那一档（即 verdict.NoteDowngraded 的虚惊一场，下面 LogUpdateFalseAlarm 记的就是它）；
                //   其余各档 ok 均为假：磁盘上确实没到目标版本（NotSatisfied）、以及版本不可比时如实回落命令退出码
                //   得到的失败。若改用 cmdOk 另判一次，那批"命令非零、其实已更新"的项就会被漏记。
                //   本处**不需要**"用户停止"那一档守卫（单个更新那处写作 !userStopped）：
                //   批量路径走的是 RunCommandAsync，它从不设置 _runningCmd —— 而那是 StopRunningCommand()
                //   唯一能杀的对象（本文件里没有任何 RequestUninstallStop / RequestInstallStop 的落点），
                //   即本循环里不存在"被用户停止的这一次"，故盖章条件就是 ok 一个量。
                //   包名取 p.Name（= PluginManager.Scan 读清单 dependencies 时那个键，见 PluginManager.cs:759/763），
                //   与本地插件页读记账用的键（SortDataOf 传的也是 p.Name）**同一个**，不是任何显示名。
                //   时间格式照 VersionMemory.Now()（yyyy-MM-dd HH:mm）的写法直接给出：它就是记账模块写入的格式，
                //   而那个方法是 private，不去改 VersionMemory。
                if (ok)
                    PluginTimes.StampUpdated(p.Name, DateTime.Now.ToString("yyyy-MM-dd HH:mm"));

                EndOpProgress(ok ? $"「{p.Name}」已更新" : $"「{p.Name}」更新失败");
                // 这一项的表已由 EndOpProgress 收掉 ⇒ 交还所有权（与 Market 的 _installProgressOpen
                // 同一手法）。循环末尾还会 await OfferRestartAsync / RefreshPluginsAsync，
                // 那期间若别处开了表，opOpen 还挂着 true 就可能被 finally 误收 ⇒ 必须在这里还回去。
                opOpen = false;
                // 注意：这里**不能**再叫 kind（用 evKind）—— 本条目附近一直有 `PluginSource.Kind`
                // 这个类型名（"来源类别"），别再拿裸 kind 当"事件色号"用。
                EventKind evKind = ok ? EventKind.Good : EventKind.Bad;
                AddEvent(ok
                        ? (verdict.NoteDowngraded
                            ? $"插件已更新：{p.Name} → {verdict.EffectiveVersion}（{PluginManager.SupplyChainRelaxNote}，不影响使用）"
                            : $"插件已更新：{p.Name} → {u.Latest}")
                        : $"插件更新失败：{p.Name} · {PluginManager.SupplyChainRelaxNote}",
                    evKind);
                // ★ 收尾文案里的那句「已放宽更新来源的安全检查」是**降级成功**（命令非零、磁盘上版本已到位）的情形，
                //   而这条分支本来**只走 Logger.Log（空实现、不落盘）** ⇒ 用户界面上说了"放宽过"，日志里却查不到。
                //   这里按 Logger 的既定策略补一条 NoteDiagnosis（[WARN] 单行、不置失败标记、不弹窗；真落盘入口就它一个），
                //   让"这次到底放宽了什么"在日志里对得上 —— 与 MainWindow.xaml.cs 里失败路径那条
                //   NoteSupplyChainRelaxOnFailure 同一措辞（那条带具体键值，本处只有环境变量这一层事实）。
                //   成功路径**只在这一条降级分支**留痕：判据是 NoteDowngraded（判定成功、但命令非零），
                //   普通成功命令不留日志（既有策略不变）。
                if (ok && verdict.NoteDowngraded)
                    Logger.NoteDiagnosis($"已放宽更新来源的安全检查（环境变量，仅本次命令）⇒ 更新「{p.Name}」记为成功"
                                       + $"（命令非零、但磁盘上版本已到位 {verdict.EffectiveVersion}）");
                Logger.Log($"批量更新 {p.Name} {u.Installed}→{u.Latest}（来源 {PluginSource.Describe(depSpec)}）: "
                         + $"命令={cmdOk} 磁盘判定={verdict.Check} 判成功={ok}（{verdict.Note}）\n{output}");
                // 命令非零、版本却已到位 ⇒ 这是"虚惊一场"，落一条含 stderr 尾部的诊断
                //（与单个更新 / 一键更新同一套，见 LogUpdateFalseAlarm）
                if (ok && !cmdOk) LogUpdateFalseAlarm(p.Name, u.Latest, output, verdict.Note);

                if (ok) { okCount++; _pluginUpdates.Remove(p.Name); }        // 成功才让下次刷新重新查
                else failed.Add($"{p.Name}（{verdict.Note}）");

                ShowBatchProgress($"{p.Name}（{i + 1}/{targets.Count}）", i + 1, targets.Count);
            }

            HideBatchProgress();
            if (PluginsSummaryText != null)
                PluginsSummaryText.Text = failed.Count == 0
                    ? $"批量更新完成：{okCount} 个插件已更新，重启 DSH 后生效"
                    : $"批量更新完成：成功 {okCount} 个，失败 {failed.Count} 个";

            GuardDialog.Show(
                (failed.Count == 0
                    ? $"✅ {okCount} 个插件都更新好了。"
                    : $"更新完成：成功 {okCount} 个，失败 {failed.Count} 个。\n\n未成功的：\n" + Shorten(string.Join("\n", failed), 600)) +
                "\n\n" +
                (okCount > 0 ? "需要重启 DSH 才会生效。" : "均未更新成功，可先点「刷新」后重试。") +
                (failed.Count > 0
                    ? "\n\n失败的插件未装上新版本，可重试；逐个更新时前面的成功、后面的失败属正常现象。"
                    : "") +
                (snapNote.Length > 0 ? "\n\n" + snapNote : ""),
                failed.Count == 0 ? "批量更新完成" : "批量更新（部分失败）",
                MessageBoxButton.OK,
                failed.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);

            if (atRisk.Count > 0)
                AddEvent($"更新后 {atRisk.Count} 个插件与当前引擎不完全匹配，可按提示回滚", EventKind.Warn);

            if (targets.Count > 0) ClearBatchSelection();   // 试过的都取消选中（未成功的重新选择即可重试）
            await RefreshPluginsAsync(true);
            if (okCount > 0) await OfferRestartAsync($"更新 {okCount} 个插件");
        }
        catch (Exception ex)
        {
            HideBatchProgress();
            // 异常把这一项的正常收尾跳过了 ⇒ 底部会永远停在「正在更新插件 X（i/N）（NN%）」，
            // 用户以为程序卡死。这里只收"本方法开的、此刻还开着"的那张表（判据见方法开头 opOpen 处）。
            EndOpProgressIfOpen(opOpen, $"批量更新中断（已更新 {okCount} 个）");
            Logger.LogError("BatchUpdate_Click", ex);
            AddEvent("批量更新没能完成，详情见弹窗与日志", EventKind.Bad);
            GuardDialog.Show(
                "批量更新没能完成。\n\n" +
                $"已成功更新 {okCount} 个" + (failed.Count > 0 ? $"，失败 {failed.Count} 个" : "") + "。\n\n" +
                LogPromise("详细原因已记入日志，可在「日志」页查看。"),
                "批量更新失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            // 正常路径不走到这里收表（循环里每一项都 EndOpProgress 报过成败文案了），
            // 所以这里只会命中"最后一项异常退出"那一支；opOpen 为 true 且表还开着才补，
            // **不会**把别处（启动前体检 / 单个更新）的表误收掉。
            EndOpProgressIfOpen(opOpen, "批量更新中断");
            _batchBusy = false;
            EndPluginWriteState();      // ← 写闸与 _batchBusy 同一条 finally 收尾：异常路径也一定复位
            HideBatchProgress();
            UpdateBatchBar();
        }
    }

    /// <summary>
    /// **批量更新的成败判定入口（与「单个更新」「一键更新」同一口径）**：
    /// 直接转发 <see cref="MainWindow.EvaluateUpdate"/>（npm 源读磁盘版本与目标比对；git 源比对
    /// "跑命令前记下的提交"与锁文件里现在的提交；两边都比不出来才回落退出码），
    /// 再补一条**生效证据**（<see cref="Logger.Log"/> 是空实现，写入不生效，只有 NoteDiagnosis 真落盘）。
    ///
    /// 单独抽出来的理由：批量路径原来是自己看退出码的，与另外两条更新路径各判各的
    /// ⇒ 同一个包在"单个更新"里显示成功、在"批量更新"里显示失败（用户现场报告的就是这种情况）。
    /// 现在三条路径共用同一个判据，自检也盯着这里（<c>BatchUpdateVerdictForTest</c>）。
    ///
    /// <paramref name="profileDir"/> 只为自检而存在（正式调用一律缺省 ⇒ 读真实 profile）：
    /// 断言必须能**指着自己造的样本目录**判，否则它量的是"这台机器上恰好装没装那个包"，
    /// 而不是"这段判定对不对"——真机自检就是这么 FAIL 的（磁盘版本读成空串）。
    ///
    /// <paramref name="commitBefore"/>：**跑命令之前**锁文件里这条 git 依赖解析到的提交
    /// （<see cref="PluginManager.ReadInstalledCommit"/>，正式调用点见 <c>BatchUpdate_Click</c> 的
    /// <c>gitCommitBefore</c>）。git 源的成败就看它 —— 命令跑完后锁文件里的提交与它相等 ⇒ 成功；
    /// 不等 ⇒ 如实判失败（"命令跑完了、提交却没变，本次没真正升级，可以重试"），而不是像旧口径那样
    /// 只看命令退出码（退出码 0 的空转会被记成"已更新"，用户下次刷新又看到"有新提交"）。
    /// 传空 / 不传（默认 <c>null</c>）⇒ 完全退回旧口径（git 源按退出码判）；npm 源不受本参数影响。
    /// </summary>
    private static MainWindow.UpdateResult BatchUpdateVerdict(PluginManager.Plugin p, PluginManager.PluginUpdate u,
                                                             bool cmdOk, string? output, string? profileDir = null,
                                                             string? commitBefore = null)
    {
        var r = EvaluateUpdate(p.Name, u.Latest, cmdOk, output, profileDir, commitBefore);
        Logger.NoteDiagnosis(
            $"批量更新判定 {p.Name}：命令退出码0={cmdOk} · 磁盘判定={(r.Measured ? "可判" : "不可判")}"
            + $" · 磁盘版本=「{(r.EffectiveVersion.Length > 0 ? r.EffectiveVersion : "(读不到)")}」"
            + $" · 目标={u.Latest} · 结论={(r.Succeeded ? "成功" : "失败")}\n  {r.Note}");
        return r;
    }

    /// <summary>
    /// 自检用：批量更新那条判定（同源调用，不是另写一份期望）。
    /// **五个参数的重载**：<paramref name="profileDir"/> 指到样本目录，才能拿样本下断言。
    /// 别退回四参数形式去调 —— <c>EvaluateUpdate</c> 的第五个形参有默认值，
    /// 四实参调用会静默落到"读真实 profile"，fixture 白造（真机 FAIL 的根因）。
    ///
    /// git 源那条"按提交判成败"要拿样本下断言时，把第 6 实参 <c>commitBefore</c> 一起传上
    /// （全位置：<c>BatchUpdateVerdictForTest(p, u, cmdOk, output, profileDir, commitBefore)</c>）：
    /// 样本目录里放一份锁文件（用 <c>PluginSource.SampleLock</c> 造），<c>commitBefore</c>
    /// 传"跑命令前"的那个提交 —— 与锁里相同 ⇒ 判成功、不同 ⇒ 判失败，两向都能钉住。
    /// </summary>
    internal static MainWindow.UpdateResult BatchUpdateVerdictForTest(PluginManager.Plugin p, PluginManager.PluginUpdate u,
                                                                     bool cmdOk, string? output, string? profileDir,
                                                                     string? commitBefore = null)
        => BatchUpdateVerdict(p, u, cmdOk, output, profileDir, commitBefore);

    /// <summary>
    /// **批量卸载的成败判定入口（事实判据）**：命令跑完后看
    /// <c>node_modules\&lt;包名&gt;</c> 这个包目录**还在不在** —— 不在了才算卸掉，判不了才回落退出码
    /// （<see cref="PluginManager.EvaluateUninstall"/>）。为什么不能只看退出码、文案怎么写，见
    /// <c>BatchUninstall_Click</c> 顶部那段。
    ///
    /// <paramref name="existedBefore"/>：**跑命令之前**用 <see cref="PluginManager.PackageDirExists"/>
    /// 取到的"它原本在不在"。为 false ⇒ 结论是「无需卸载」这个**中性**结果，而不是成功（本单 H2）。
    /// 不传（null）⇒ 退回两态旧口径，仅供不改签名的旧调用点使用。
    /// </summary>
    private static PluginManager.UninstallResult BatchUninstallVerdict(string packageName, bool cmdOk,
                                                                      string? output, string? profileDir = null,
                                                                      bool? existedBefore = null)
    {
        var r = PluginManager.EvaluateUninstall(packageName, cmdOk, profileDir, existedBefore);
        Logger.NoteDiagnosis(
            $"批量卸载判定 {packageName}：命令退出码0={cmdOk} · 磁盘判定={(r.Measured ? "可判" : "不可判")}"
            + $" · 操作前在={existedBefore?.ToString() ?? "(未记)"}"
            + $" · 结论={(r.Unnecessary ? "无需卸载（本来就没有）" : r.HalfDone ? "**半卸载（目录没了、清单还在）**" : r.Removed ? "已卸掉" : "还在")}\n  {r.Note}");
        return r;
    }

    /// <summary>自检用：批量卸载那条判定（同源调用；可指定 profile 目录，便于拿样本目录断言）。</summary>
    internal static PluginManager.UninstallResult BatchUninstallVerdictForTest(string packageName, bool cmdOk,
                                                                              string? output, string? profileDir)
        => BatchUninstallVerdict(packageName, cmdOk, output, profileDir);

    /// <summary>自检用：带「操作前在不在」的三态卸载判定（本单 H2 的回归护栏）。</summary>
    internal static PluginManager.UninstallResult BatchUninstallVerdict3ForTest(string packageName, bool cmdOk,
                                                                               string? output, string? profileDir,
                                                                               bool? existedBefore)
        => BatchUninstallVerdict(packageName, cmdOk, output, profileDir, existedBefore);

    // ══════════════════════════════════════════════════════════════
    //  ⑧ 批量卸载（破坏性；逐个顺序执行，带 i/total 进度）
    // ══════════════════════════════════════════════════════════════    /// <summary>
    /// 批量卸载：弹层里**唯一的破坏性动作**，按钮排在三个动作的最后（<see cref="BatchRed"/>）。
    ///
    /// 口径（能复用的一律复用，不另立一套）：
    ///   · 目标 = 选中里**确实装着**的插件 —— 判据是显示层既有的 <see cref="IsVersionPlaceholder"/>
    ///     （版本字段是「(未安装)」占位或空 ⇒ 清单里有它、机器上并没有它 ⇒ 没得卸）；
    ///     「?」（package.json 在、只是未填写 version 字段）不算占位，照样算装着，与原口径一致。
    ///   · 先弹确认框，把**将被卸载的插件名逐个列出来**（名称 + 版本，复用 <see cref="BatchNameList"/>），
    ///     OKCancel + Warning 图标；取消即原样返回，什么都不做。
    ///   · 逐个执行：命令复用 <c>PluginManager.BuildUninstallArgs(包名)</c>，走既有的
    ///     <c>RunCommandAsync("npx", …)</c>，超时与既有的插件命令同一档（600000ms）；
    ///     **显式**带 <c>relaxSupplyChainPolicy: true</c>（不靠命令行的字符串形状兜底，理由见调用点注释）。
    ///     每个都先 <see cref="BeginOpProgress"/> 报「正在卸载插件 X（i/N）」（底部百分比进度），
    ///     弹层里那条进度也跟着走一步（与批量更新同一套 <see cref="ShowBatchProgress"/>）。
    ///   · 参数给不出可靠目标（包名过不了 npm 包名白名单 ⇒ <c>BuildUninstallArgs</c> 返回空串）⇒
    ///     **不执行命令**，记失败并跳过这一项（与批量更新、单个卸载同一口径；此处尚未开进度表，
    ///     故不需要收表）。
    ///   · **成败判据是事实、不是退出码**（与「单个卸载」同一口径）：命令跑完看
    ///     <c>node_modules\&lt;包名&gt;</c> 这个包目录**还在不在**，不在了才算卸掉；
    ///     判不了才回落命令退出码（<see cref="PluginManager.EvaluateUninstall"/>）。
    ///     失败那一条的说明写的是"包目录还在"这类磁盘事实，而不是命令输出片段 ——
    ///     卸载与更新一样会被 pnpm 的"文件被占用 / 依赖构建脚本失败"拖成非零退出码，
    ///     而包其实已经删干净了，只看退出码就会"显示失败、其实卸掉了"。
    ///   · ★ 三态（本单 H2）：每个包在**跑命令之前**先记一次 <see cref="PluginManager.PackageDirExists"/>，
    ///     原本就不在 ⇒ 结论是「**无需卸载**」这个中性档（不报成功、也不报失败，汇总里单独计数）。
    ///     不记这一笔，对一张「未安装」的卡片点卸载就会报绿色"已卸载"（命令必然失败）。
    ///   · 结束给一份成功 / 失败汇总；一个失败不影响其余插件的卸载。
    ///   · <c>_batchBusy</c> 全程压着（与禁用 / 启用 / 更新同一套：期间动作按钮禁用、不重入）；
    ///     <b>外加写闸</b> <c>_pluginWriteBusy</c>（本轮补）：它才是与「一键更新 / 单颗更新 / 重新安装」
    ///     互斥的那一份 —— 后三条看这个标志、这一条占这个标志，两边真正串起来。
    ///   · 有成功的就按既有习惯 <see cref="ClearBatchSelection"/> 清空勾选，再走既有的刷新入口
    ///     <c>RefreshPluginsAsync(true)</c> **重新读一遍插件集合**（不自己 new 集合）。
    ///
    /// 与「单个卸载」（<c>UninstallPlugin_Click</c>）刻意保留的两点差异，别当成漏写：
    ///   ① 单个卸载失败后会去掉策略覆盖参数重试一次；批量卸载**不重试** —— 一批里逐个重试会让
    ///      耗时与输出翻倍，失败的在汇总里带着输出摘要报出来，重选一次就能单独重跑；
    ///   ② 快照整批只打**一份**（与批量更新同一口径），而不是每个插件各打一份。
    /// </summary>
    private async void BatchUninstall_Click(object sender, RoutedEventArgs e)
    {
        if (_batchBusy) return;

        // 目标：选中里确实装着的（版本是「未安装」占位的没得卸，判据复用显示层的既有口径）
        var targets = SelectedPlugins().Where(p => !IsVersionPlaceholder(p.Version)).ToList();
        if (targets.Count == 0)
        {
            GuardDialog.Show("选中的插件中没有已安装的：版本显示「未安装」表示本机未安装该插件，无需卸载。",
                "批量卸载", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        bool needSnapshot = SnapshotPolicy.NeedSnapshot(GuardAction.UninstallPlugin);

        var r = GuardDialog.Show(
            $"要将这 {targets.Count} 个插件卸载？以下插件将被移除：\n\n" +
            BatchNameList(targets) + "\n\n" +
            "将逐个从 DSH 的插件清单中移除。\n\n" +
            "卸载不可逆：包会被删除，插件清单中的依赖也会一并移除" +
            (needSnapshot ? "（卸载前自动保存快照，可在「快照」页回退）" : "") + "。\n" +
            "若 DSH 正在运行，操作可能因文件被占用而失败（建议先停止引擎）。\n\n" +
            "卸载后需重启 DSH 才会完全生效。是否继续？",
            "确认批量卸载", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (r != MessageBoxResult.OK) return;

        // ★（本轮补）跨流程的写闸：与上面 BatchUpdate_Click 同款、同理由 —— 批量卸载也逐个跑命令改
        //   node_modules，与「一键更新 / 单颗更新 / 重新安装」「批量更新」互斥。位置同样压在确认框之后、
        //   与 _batchBusy = true 紧挨着（不占模态框那一段，查与落之间也没有 await）。
        if (!PassPluginWriteGate()) return;

        // 与 BatchUpdate_Click 完全同款（判据的理由写在那里的 opOpen 注释里）：
        // 批量卸载的 BeginOpProgress 也在循环**里面**，只有"本方法开的表"才允许 finally 收尾。
        bool opOpen = false;
        _batchBusy = true;
        BeginPluginWriteState();
        int okCount = 0;
        int skipCount = 0;          // 「无需卸载」（操作前本机就没有）—— 中性，既不算成功也不算失败
        int halfCount = 0;          // ★ 半卸载（本单新增）：包已删、清单里仍留着登记 —— 不算成功
        var failed = new List<string>();
        try
        {
            // 改插件清单有风险 ⇒ 先按策略存一份快照兜底（与「批量更新」「单个卸载」同一口径）
            string snapNote = "";
            if (needSnapshot)
            {
                if (PluginsSummaryText != null) PluginsSummaryText.Text = $"正在给这 {targets.Count} 个插件打快照…";
                snapNote = await Task.Run(() => SnapshotBeforePluginChange(
                    $"DSHGuard：卸载 {targets.Count} 个插件前",
                    SnapshotPolicy.KindFor(GuardAction.UninstallPlugin)));
            }

            for (int i = 0; i < targets.Count; i++)
            {
                var p = targets[i];

                // ★★ 本单 H2 的第一道闸（与单个卸载 Tools.cs 的 unArgs 检查、上面批量更新那一段同款）：
                //   包名给不出可靠的卸载目标 ⇒ **不执行命令**，记失败、继续下一项。
                //   BuildUninstallArgs 对过不了 npm 包名白名单（IsValidPackageName：`..`／路径分隔符／空白／
                //   shell 元字符／非 ASCII／超长等）的包名返回**空串**；空串再送进去只会得到一次
                //   **无参数**的 npx（必然失败），而且那条命令的形状根本不含 dsh 插件命令的样子
                //   ⇒ 连包龄放行都拿不到。位置必须压在 BeginOpProgress **之前**（与批量更新的次序一致）：
                //   本项还没开表，continue 就走，不需要也没有表要收（进度条收尾与 _batchBusy 一概不动）。
                string unArgs = PluginManager.BuildUninstallArgs(p.Name);
                if (unArgs.Length == 0)
                {
                    Logger.NoteDiagnosis($"批量卸载 {p.Name}：包名不是合法的 npm 包名 ⇒ 未执行命令");
                    failed.Add($"{p.Name}（无法确定卸载目标，已跳过）");
                    continue;
                }

                string label = $"正在卸载插件 {p.Name}（{i + 1}/{targets.Count}）";
                BeginOpProgress(label);
                opOpen = true;
                ShowBatchProgress($"{p.Name}（{i + 1}/{targets.Count}）", i, targets.Count);
                try { if (PluginsSummaryText != null) PluginsSummaryText.Text = $"{label} …"; } catch { }

                // ★★ 本单 H2：**跑命令之前**先记下"这个包原本在不在"。
                //   EvaluateUninstall 只看"现在目录在不在"，而后者的"不在"包含两种情形：
                //   「本来装着、卸掉了」与「这台机器上从来就没有过」—— 靠事后一次目录检查**分不开**。
                //   不记这一笔，对一张显示「未安装」的卡片点卸载就会报绿色「已卸载插件 X」（命令必然失败）。
                bool existedBefore = PluginManager.PackageDirExists(p.Name);
                // 参数在循环开头就已构造并判过空（unArgs），这里只负责执行 —— 不再就地构造，
                // 免得空串再从这条路径漏到命令行上（内联传参正是原来漏判空的原因）。
                // relaxSupplyChainPolicy：显式点明「这次要放 pnpm 包龄/锁文件策略」—— 卸载同更新一样会改
                //   node_modules。此前只靠 RunCommandAsync 里 LooksLikePluginMutation 的**字符串形状兜底**
                //   自动注入，参数写法一变就会**静默**丢掉这层放行（表现成"单个能卸、批量卸不动"且毫无提示）。
                //   显式 true 与形状命中在 RunCommandAsync 里是**同一条 `||`、同一个** InjectSupplyChainRelax
                //   ⇒ 行为与加固前完全等价，只是意图显式、不再依赖命令形状。
                var (cmdOk, output) = await RunCommandAsync("npx", unArgs, timeoutMs: 600000, relaxSupplyChainPolicy: true);

                // ★ 成败判据与「单个卸载」**同一口径**：看这个包的目录**还在不在**（事实），
                //   不在了才算卸掉；判不了才回落命令退出码。卸载同更新一样会被 pnpm 的
                //   "另一个程序正在使用此文件 (os error 32)" / 依赖构建脚本失败拖成非零退出码，
                //   而包其实已经删干净了 ⇒ 只看退出码会报"失败"，用户回插件页一看插件没了（与更新那条对称）。
                //   existedBefore=false ⇒ 结论落到「无需卸载」（中性），既不报成功也不报失败。
                var uVerdict = BatchUninstallVerdict(p.Name, cmdOk, output, null, existedBefore);
                bool ok = uVerdict.Removed;
                bool unnecessary = uVerdict.Unnecessary;
                // ★ 半卸载档（本单新增）：包目录确实已删掉、但插件清单里仍登记着它。
                //   Removed 只在真正卸干净时才为真 ⇒ 它天然落在下面的失败支（不会谎报成功），
                //   但失败文案说的是"没卸掉"，与"包已经没了"这个事实相反，故单列一档。
                bool halfDone = uVerdict.HalfDone;
                // 记账：只在**"真卸干净"这一档**（ok = uVerdict.Removed 为真）删掉该包的两条记录 ——
                //   判据就是上面这个 ok，不另立一套"成没成"的判法（本项目要求判据只留一份）。
                //   为什么只有这一档能盖章：uVerdict 与单个卸载**同一个结论入口**（BatchUninstallVerdict
                //   -> PluginManager.EvaluateUninstall），ok 只在 UninstallOutcome.Clean
                //   ——"包目录没了 **且** 清单里那一条也没了"—— 时为真；其余各档 ok 一律为假：
                //     · 半卸载（目录没了、清单里仍登记着，下次任何一次安装都会把它装回来）：不是成功，删记录等于
                //       把这次的"没卸干净"记成一次成功卸载 —— 正是本项目刚修过的"谎报成功"那类错误；
                //     · 没卸掉（目录还在）/ 判不了（清单读不出来）：都没卸干净，同上；
                //     · 「无需卸载」这个中性档（existedBefore=false，操作前本机就没有这个包）：本来就没有记录，
                //       更不该在这一档动账目。
                //   本处**不需要**"用户停止"那一档守卫（单个卸载那处写作 !userStopped）：
                //   批量路径走的是 RunCommandAsync，它从不设置 _runningCmd —— 而那是 StopRunningCommand()
                //   唯一能杀的对象（本文件里没有任何 RequestUninstallStop 的落点），即批量循环里不存在
                //   "被用户停止的这一次"；单个卸载之所以要那道守卫，是因为它走 RunUninstallCommandAsync
                //   （= RunCommandCancelableAsync，会设置 _runningCmd）、用户真停得掉。
                //   包名取 p.Name（= PluginManager.Scan 读清单 dependencies 时那个键，见 PluginManager.cs:759/763）：
                //   与本地插件页读记账用的键（SortDataOf 传的也是 p.Name）**同一个**，不是任何显示名。
                if (ok)
                    PluginTimes.Remove(p.Name);

                EndOpProgress(ok ? $"插件 {p.Name} 已卸载"
                                 : unnecessary ? $"插件 {p.Name} 无需卸载"
                                               : halfDone ? $"插件 {p.Name} 未卸干净"
                                                          : $"插件 {p.Name} 卸载失败");
                // 同批量更新：这一项的表已收掉 ⇒ 交还所有权，免得末尾长尾 await 期间
                // 别人开的表被 finally 误收。
                opOpen = false;
                // 命令非零、包却确实没了 ⇒ 说明里带上"已卸掉"，免得与"失败"这个色号打架（事实优先）。
                // 「无需卸载」单独一档：**中性**，不走"已卸载"的绿色文案，也不算失败。
                // 「半卸载」也单独一档（本单新增）：包已经删掉了，只是清单里还留着登记 ——
                //   旧文案"卸载插件失败"会让用户以为包还在，与事实相反。级别沿用既有那档，不动。
                AddEvent(ok
                        ? (uVerdict.NoteDowngraded ? $"已卸载插件 {p.Name}（命令报了非零，包已确认删掉）"
                                                   : $"已卸载插件 {p.Name}")
                        : unnecessary ? $"无需卸载插件 {p.Name}（本机本来就没装）"
                                      : halfDone ? $"插件 {p.Name} 未卸干净（清单里仍有登记，下次安装会装回来）"
                                      : $"卸载插件失败：{p.Name}",
                    unnecessary ? EventKind.Info : EventKind.Bad);
                Logger.Log($"批量卸载 {p.Name}: 命令={cmdOk} 操作前在={existedBefore} 磁盘判定={uVerdict.Measured} 判成功={ok}（{uVerdict.Note}）\n{output}");
                if (ok && !cmdOk)
                    Logger.NoteDiagnosis($"批量卸载「{p.Name}」：命令退出码非零，但包目录已消失 ⇒ 判成功，不报失败。依据：{uVerdict.Note}");
                if (unnecessary)
                    Logger.NoteDiagnosis($"批量卸载「{p.Name}」：操作前本机就没有这个包 ⇒ 无需卸载（不报成功也不报失败）。依据：{uVerdict.Note}");

                // ★ 半卸载只多记一个数（本单新增）：它仍然照旧落进下面的失败清单 —— 下一个安装
                //   会按清单把它装回来，这件事必须让用户看见，所以原判据与那一行一字不动。
                if (halfDone) halfCount++;
                if (ok) okCount++;
                else if (unnecessary) skipCount++;
                else failed.Add($"{p.Name}（{uVerdict.Note}）");

                ShowBatchProgress($"{p.Name}（{i + 1}/{targets.Count}）", i + 1, targets.Count);
            }

            HideBatchProgress();
            // 三态汇总（本单 H2）：成功 / 失败 / **无需卸载**（操作前本机就没有）三者分开数，
            // 「无需卸载」既不能算进成功（那是 H2 的谎报），也不该算进失败（什么都没坏）。
            string skipNote = skipCount > 0 ? $"，{skipCount} 个本来就没装（无需卸载）" : "";
            // ★ 半卸载提示（本单新增）：上面三态之外单独说一句 —— 这几个的包文件确实已经删掉，
            //   只是清单里那一条还留着，下次任何一次安装都会把它们装回来。没有半卸载项时为空串，
            //   汇总文案与旧情形逐字不变。
            string halfNote = halfCount > 0
                ? $"其中 {halfCount} 个只删掉了包文件、清单里仍留着登记，下次安装会装回来"
                : "";
            if (PluginsSummaryText != null)
                PluginsSummaryText.Text = failed.Count == 0
                    ? (okCount > 0
                        ? $"批量卸载完成：{okCount} 个插件已卸载，重启 DSH 后生效{skipNote}"
                        : $"批量卸载完成：无需卸载（选中的插件本机都没有安装{skipNote}）")
                    : $"批量卸载完成：成功 {okCount} 个，失败 {failed.Count} 个{skipNote}";

            GuardDialog.Show(
                (failed.Count == 0
                    ? (okCount > 0
                        ? $"✅ {okCount} 个插件都卸载了。" + skipNote
                        : $"选中的插件本机都没有安装，无需卸载（没有执行任何删除）。")
                    : $"卸载完成：成功 {okCount} 个，失败 {failed.Count} 个{skipNote}。\n\n未成功的：\n" + Shorten(string.Join("\n", failed), 600)) +
                // ★ 本单新增的一句（半卸载项 > 0 时才出现）：原有的汇总文案与条件一字不动，
                //   只是在其后**另起一段**补上这一档 —— 它说的正是"包已经没了、清单里那条还在"。
                //   （半卸载项必然同时进了上面的失败清单，故这一句与那段清单永远同现。）
                (halfNote.Length > 0 ? "\n\n" + halfNote + "。" : "") +
                "\n\n" +
                (okCount > 0 ? "需要重启 DSH 才会完全生效。"
                             : failed.Count > 0 ? "均未卸载成功，可先点「刷新」后重试。"
                                                : "") +
                (failed.Count > 0
                    ? "\n\n失败的插件未被卸载，可先停止引擎后重试（常因文件被占用而失败）。"
                    : "") +
                (snapNote.Length > 0 ? "\n\n" + snapNote : ""),
                failed.Count == 0
                    ? (okCount > 0 ? "批量卸载完成" : "批量卸载")
                    : "批量卸载（部分失败）",
                MessageBoxButton.OK,
                failed.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);

            // 按既有习惯收尾：有成功的就清空勾选（那些名字对应的插件已经没了），
            // 再走既有的刷新入口重读插件集合。
            if (okCount > 0) ClearBatchSelection();
            await RefreshPluginsAsync(true);
        }
        catch (Exception ex)
        {
            HideBatchProgress();
            // 同 BatchUpdate_Click：异常把这一项的正常收尾跳过了 ⇒ 补一句如实的收尾，
            // 否则底部永远停在「正在卸载插件 X（i/N）（NN%）」。判据见方法开头 opOpen 处。
            EndOpProgressIfOpen(opOpen, $"批量卸载中断（已卸载 {okCount} 个）");
            Logger.LogError("BatchUninstall_Click", ex);
            AddEvent("批量卸载没能完成，详情见弹窗与日志", EventKind.Bad);
            GuardDialog.Show(
                "批量卸载没能完成。\n\n" +
                $"已成功卸载 {okCount} 个" + (failed.Count > 0 ? $"，失败 {failed.Count} 个" : "") +
                "。\n\n" +
                // 只有真打了快照才提「快照」页：策略关掉快照时给这句就是空头支票
                (needSnapshot ? "可以在「快照」页回退配置文件。\n\n" : "") +
                LogPromise("详细原因已记入日志，可在「日志」页查看。"),
                "批量卸载失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            // 正常路径不走到这里收表（循环里每一项都 EndOpProgress 报过成败文案了）；
            // 只有"最后一项异常退出"那一支会命中，且 opOpen 与表状态两条相与才动手。
            EndOpProgressIfOpen(opOpen, "批量卸载中断");
            _batchBusy = false;
            EndPluginWriteState();      // ← 写闸与 _batchBusy 同一条 finally 收尾：异常路径也一定复位
            HideBatchProgress();
            UpdateBatchBar();
        }
    }

    /// <summary>
    /// 批量条内的进度显示：第 done/total 个已经处理完。
    /// 文案与进度条都摆在**弹层的同一段纵向流里**（说明行下面：一行文案 + 一条细进度条），
    /// 因此这里只改文字与数值、只翻显隐，不动布局、不动按钮排布。
    /// 文案 = 调用点给的「插件名（i/N）」；进度条只吃 done/total。
    /// </summary>
    private void ShowBatchProgress(string label, int done, int total)
    {
        try
        {
            if (_batchBarProgress != null)
            {
                _batchBarProgress.Visibility = Visibility.Visible;
                _batchBarProgress.Maximum = Math.Max(1, total);
                _batchBarProgress.Value = Math.Max(0, Math.Min(done, total));
            }
            // 与进度条同生共死：有进度条必有这条文案，收尾时两件一起收（HideBatchProgress）。
            if (_batchBarProgressText != null)
            {
                _batchBarProgressText.Text = label;
                _batchBarProgressText.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex) { Logger.LogError("ShowBatchProgress", ex); }
    }

    private void HideBatchProgress()
    {
        try
        {
            if (_batchBarProgress != null)
            {
                _batchBarProgress.Visibility = Visibility.Collapsed;
                _batchBarProgress.Value = 0;
            }
            // 文案清空并收起：留着「某个插件（2/5）」而进度条已经消失，会读成"卡在那一项"。
            if (_batchBarProgressText != null)
            {
                _batchBarProgressText.Text = "";
                _batchBarProgressText.Visibility = Visibility.Collapsed;
            }
        }
        catch { }
    }

    /// <summary>
    /// 确认框里的插件清单：一行一个「显示名 v版本」，不带任何内部标识。
    /// 版本是占位文本「(未安装)」时同样不加 v 前缀（与卡片右侧同一个口径，见
    /// <see cref="IsVersionPlaceholder"/>）—— 否则清单里也会出现「v(未安装)」。
    /// </summary>
    private static string BatchNameList(IEnumerable<PluginManager.Plugin> plugins)
        => string.Join("\n", plugins.Select(p =>
            $"  · {PluginMarket.MarketPlugin.FormatDisplayName(p.Name)}  "
            + (IsVersionPlaceholder(p.Version) ? VersionMissingLabel : $"v{p.Version}")));
}
