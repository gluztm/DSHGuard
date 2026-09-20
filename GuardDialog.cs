using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace DSHGuard;

/// <summary>
/// 应用内统一样式的对话框，替代系统 MessageBox。
/// 圆角卡片 + 图标标题行 + 右对齐按钮；配色跟随日间/夜间模式。
/// <see cref="Show(string, string, MessageBoxButton, MessageBoxImage)"/> 与 MessageBox 同名同参，
/// 各调用点可直接替换。
/// </summary>
internal static class GuardDialog
{
    /// <summary>自定义按钮：短文案 + 返回结果 + 底色（红=破坏性、橙=改变、灰=取消）。</summary>
    internal sealed record DialogButton(string Label, MessageBoxResult Result, Color Background,
        bool IsDefault = false, bool IsCancel = false);

    // ══════════════ 单对话框闸门 + 看门狗 ══════════════

    /// <summary>
    /// 同一时刻只允许一个对话框。现场教训：一次启动失败先弹「启动超时」、紧接着又弹「建议回退版本」，
    /// 关掉一个又冒一个，用户只能去任务管理器杀进程。
    /// </summary>
    private static int _openCount;
    internal static int OpenCountForTest() => _openCount;

    /// <summary>有对话框开/关时触发（界面据此暂停加载动画，别跟弹窗抢渲染）。</summary>
    internal static event Action? AnyOpenChanged;

    // ══════════════ 打开中的「真非模态」窗（切主题时要补刷） ══════════════

    /// <summary>
    /// 打开中的**真非模态**窗：只由 <see cref="ShowNonModal"/> / <see cref="ShowNonModalCustom"/> 登记。
    ///
    /// 为什么必须单独登记：这类窗只设了 <see cref="Window.Owner"/>、**从没加进主窗的任何 Children 集合**，
    /// 既不在主窗视觉树上、也不在逻辑树上 ⇒ <c>ThemeManager.Apply(主窗.Content, dark)</c> 从根那趟
    /// 怎么走都碰不到它，于是"弹窗开着时切主题"它保持旧配色（下次重开才变，现场外观缺陷）。
    /// 关窗时在 <c>Closed</c> 里摘掉（只增不减 = 内存泄漏 + 之后切主题去碰已关闭的窗口）。
    ///
    /// **模态 / 伪模态窗刻意不进这里**：模态 <c>ShowDialog()</c> 由 WPF 自动禁用 Owner、
    /// 回滚进度窗则显式把主窗 <c>IsEnabled = false</c>（伪模态）——两种情况下主窗都点不动，
    /// 用户根本点不到主题按钮，补刷没有意义（见 <c>MainWindow.RollbackProgress.cs</c> 的类注释）。
    ///
    /// 线程：开窗、切主题、<c>Closed</c> 全在 UI 线程 ⇒ 不需要锁。
    /// </summary>
    private static readonly List<Window> _nonModalOpen = new();

    /// <summary>登记一个刚显示成功的非模态窗（登记后关窗必定摘除，见 Closed）。</summary>
    private static void RegisterNonModal(Window dlg)
    {
        try
        {
            if (!_nonModalOpen.Contains(dlg)) _nonModalOpen.Add(dlg);
            // 防泄漏：窗口无论怎么关（按钮 / Esc / 代码 Close / 进程收尾）都必须从这里摘掉
            dlg.Closed += (_, _) =>
            {
                try { _nonModalOpen.Remove(dlg); } catch { }
            };
        }
        catch (Exception ex) { Logger.LogError("GuardDialog.RegisterNonModal", ex); }
    }

    /// <summary>打开中的非模态窗数量（自检用：验证关掉之后确实摘干净了）。</summary>
    internal static int NonModalOpenCountForTest() => _nonModalOpen.Count;

    /// <summary>
    /// 当前打开中的非模态窗（自检用：对着它断言"切主题后确实换肤了"）。
    /// 单对话框闸门保证同一时刻最多一个，所以返回单个即可；没有则返回 null。
    /// </summary>
    internal static Window? NonModalOpenForTest() => _nonModalOpen.Count > 0 ? _nonModalOpen[0] : null;

    /// <summary>
    /// 按**当前**主题给所有打开中的非模态窗补刷一次（<see cref="ThemeManager.ApplyTo"/> 只刷、不改 IsDark）。
    /// 由主题切换统一入口 <c>MainWindow.ApplyTheme</c> 调用。
    /// 关窗路径或某一个窗刷新失败都不该连累主题切换本身，故逐窗 try/catch。
    /// </summary>
    internal static void ReskinNonModalOpen()
    {
        // 先快照再遍历：补刷期间若有窗被关闭，Closed 处理器会改这个集合
        foreach (var w in _nonModalOpen.ToArray())
        {
            try { ThemeManager.ApplyTo(w); }
            catch (Exception ex) { Logger.LogError("GuardDialog.ReskinNonModalOpen", ex); }
        }
    }

    /// <summary>对话框开着超过这么久还没关掉，就往日志里留一行证据（下次出现"无法关闭"时能有据可查）。</summary>
    private const int StuckWatchdogSeconds = 120;

    /// <summary>登记一个即将显示的对话框：计数 + 看门狗 + 关闭时复位。</summary>
    private static void Track(Window dlg, string caption)
    {
        _openCount++;
        try { AnyOpenChanged?.Invoke(); } catch { }

        var watchdog = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(StuckWatchdogSeconds)
        };
        watchdog.Tick += (_, _) =>
        {
            watchdog.Stop();
            Logger.NoteDiagnosis($"弹窗看门狗：「{caption}」打开超过 {StuckWatchdogSeconds} 秒仍未被关闭"
                + $"（UI 线程 {Environment.CurrentManagedThreadId}，主窗可用={Application.Current?.MainWindow?.IsEnabled}）");
        };
        watchdog.Start();

        dlg.Closed += (_, _) =>
        {
            try { watchdog.Stop(); } catch { }
            _openCount = Math.Max(0, _openCount - 1);
            // 框全关掉了 = 这一次"有框开着"的时段结束 ⇒ 提示去重位复位，
            // 下一回再有框开着时被闸门挡下，用户又能收到一条提示。
            // 只动这一个"提示过没有"的标志位：计数与单例语义（AnyOpen）一个字节都不改。
            if (_openCount == 0) _gateSuppressedNoted = false;
            try { AnyOpenChanged?.Invoke(); } catch { }
        };
    }

    /// <summary>有没有对话框开着。</summary>
    internal static bool AnyOpen => _openCount > 0;

    // ══════════════ 闸门挡下时：绝不静默 ══════════════

    /// <summary>
    /// 最近一次弹框尝试（四个入口任一）**是不是被上面那道单例闸门挡掉的**。
    ///
    /// 为什么要这个可判别状态：<see cref="Show(string, string, MessageBoxButton, MessageBoxImage)"/> 与
    /// <see cref="ShowCustom"/> 被闸门挡掉时只能返回 <see cref="MessageBoxResult.Cancel"/>，
    /// 而调用点写的都是 <c>if (r != MessageBoxResult.OK) return;</c> ——「用户点了取消」与
    /// 「框根本就没弹出来」在**返回值上完全同形**。于是出现现场那一幕：非模态框还开着时点
    /// 「一键更新」/「卸载插件」/「批量禁用」/「清理缓存」/「回滚快照」，确认框被吞掉 ⇒ 直接收场
    /// ⇒ 事件栏没有、弹窗没有、日志没有（本项目头号雷区「以为有反应其实没有」）。
    /// 调用方在调用**紧后面**读一下本属性即可区分：
    /// <code>
    /// var r = GuardDialog.Show(...);
    /// if (GuardDialog.LastShowSuppressedByGate) { /* 框没弹出来，按"请先处理那个框"处理 */ }
    /// if (r != MessageBoxResult.OK) return;
    /// </code>
    /// 线程：四个入口都在 UI 线程被调用（按钮事件），读到它也在同一句之后、中间没有 await，
    /// 即不存在"读到别人那次尝试留下的值"的窗口。
    /// </summary>
    internal static bool LastShowSuppressedByGate { get; private set; }

    /// <summary>
    /// "当前这一轮有框开着"的时段里，是否已经就"闸门挡下一次弹框"提示过用户（事件栏与日志各一条）。
    ///
    /// 为什么同一轮里只提示一次：被挡下的原因与要做的动作每次都一模一样（"已经有一个框开着，先处理它"），
    /// 而一次点击之后的多个入口可能连续命中本闸（提示 + 提问 + 结果框），逐次喊只会把事件栏刷满。
    /// 与「引擎 stderr 已知无害只提示一次」（<c>_knownNoiseNoted</c>）同一个范式。
    ///
    /// <b>复位时机</b>：在 <see cref="Track"/> 的 <c>Closed</c> 里，当打开计数回到 0（框全关掉了）
    /// 时复位。即"用户按提示关掉了那个框、再点一次又被挡下"时能再收到一条 —— 否则提示只在本次运行
    /// 第一次被挡时出现，后面几次又变回静默。复位只碰这一个标志位，不碰 <c>_openCount</c>/
    /// <see cref="AnyOpen"/>（单例语义一个字节都不动）。
    /// </summary>
    private static bool _gateSuppressedNoted;

    /// <summary>
    /// 闸门挡下时的唯一一句话：说清**现在是什么状态**（有个框开着）与**接下来做什么**（关掉它再点一次）。
    /// 刻意不带被挡下的框名：同一句话要适配四个入口与任意 caption，带上反而会让人以为是那个框出了问题。
    /// </summary>
    internal const string GateSuppressedNote = "已有一个对话框开着，这次的操作没有生效；请先关掉它，再点一次。";

    /// <summary>
    /// 闸门拦下一次弹框时的统一出口：**静默必须变成有反应**。
    ///
    /// 原来这里只调 <see cref="Logger.Log(string)"/> —— 那是**空实现**（不落盘、不弹窗、不写事件栏），
    /// 等于什么都没留下：这正是"点了零反应"的成因。改为两条用户真看得见的路：
    ///   ① 界面「事件信息」一条橙色 Warn（<see cref="MainWindow.AddEvent"/>）—— 用户实际看到的那条；
    ///   ② 异常日志一条 <c>[WARN]</c>（<see cref="Logger.NoteDiagnosis"/>）—— 排查用；
    ///      真实落盘只能走它，因为 <see cref="Logger.Log"/> 是刻意保留的空实现。
    /// 两条都只做一次；但 <see cref="LastShowSuppressedByGate"/> **每次都置位**，
    /// 因为调用方要能判别的是"我这一次到底弹没弹出"，与提示去重是两回事。
    /// 全程 try/catch：提示本身失败绝不能连累调用方的正常返回路径（原样返回 Cancel 的语义不变）。
    /// </summary>
    private static void NoteGateSuppressed(string caption)
    {
        if (_gateSuppressedNoted) return;
        _gateSuppressedNoted = true;
        try
        {
            Logger.NoteDiagnosis($"单对话框闸门：已有对话框开着，跳过弹框：「{caption}」"
                + $"（当前开着 {_openCount} 个）；已同步到界面「事件信息」");
        }
        catch { }
        try { DSHGuard.MainWindow.AddEvent(GateSuppressedNote, DSHGuard.MainWindow.EventKind.Warn); } catch { }
    }

    /// <summary>挂到显示流程上的公共部分：计数、Owner、居中、激活。</summary>
    private static void Prepare(Window dlg, string caption)
    {
        var owner = Application.Current?.MainWindow;
        if (owner != null && owner.IsVisible && !ReferenceEquals(owner, dlg))
        {
            dlg.Owner = owner;
            dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        Track(dlg, caption);
    }

    /// <summary>
    /// 非模态提示：只告知、不决策，**永不阻塞界面**。
    /// 错误提示走这条路：模态框一旦被加载动画/渲染饿住就点不动，用户只能杀进程。
    /// </summary>
    public static void ShowNonModal(string messageBoxText, string caption, MessageBoxImage icon)
    {
        try
        {
            // 闸门照旧（同一时刻只允许一个框，防"关掉一个又冒一个"）；但挡下之后**必须有反应**：
            // LastShowSuppressedByGate 让调用方能判别"这次没弹出来"，NoteGateSuppressed 让用户看得见。
            if (AnyOpen) { LastShowSuppressedByGate = true; NoteGateSuppressed(caption ?? ""); return; }
            LastShowSuppressedByGate = false;
            var dlg = Build(messageBoxText ?? "", caption ?? "", MessageBoxButton.OK, icon);
            Prepare(dlg, caption ?? "");
            dlg.Show();
            RegisterNonModal(dlg);      // 显示成功才登记：Show 抛异常时不留永不关闭的悬挂项
            dlg.Activate();
        }
        catch (Exception ex)
        {
            Logger.LogError("GuardDialog.ShowNonModal", ex);
            try { System.Windows.MessageBox.Show(messageBoxText, caption, MessageBoxButton.OK, icon); } catch { }
        }
    }

    /// <summary>
    /// 非模态的「带按钮」提示：只报告 + 给一个可点的下一步，**不阻塞界面**。
    /// 现场教训：启动失败的决策框做成模态后，用户点主窗口毫无响应，看着就是"无法响应"，
    /// 最后只能去任务管理器杀进程。回调在窗口关闭后于 UI 线程执行。
    /// </summary>
    public static void ShowNonModalCustom(string messageBoxText, string caption, MessageBoxImage icon,
                                          Action<MessageBoxResult>? onPick, params DialogButton[] buttons)
    {
        try
        {
            // 同 ShowNonModal：闸门语义不变，只把"静默"换成可判别 + 用户看得见的提示。
            // ⚠ 这里**没有**回调可触发（框没弹出来，onPick 不会被调），所以提示必须在本方法内发出。
            if (AnyOpen) { LastShowSuppressedByGate = true; NoteGateSuppressed(caption ?? ""); return; }
            LastShowSuppressedByGate = false;
            var list = buttons is { Length: > 0 } ? buttons : null;
            var dlg = Build(messageBoxText ?? "", caption ?? "", MessageBoxButton.OK, icon,
                customButtons: list,
                escapeResult: list?.FirstOrDefault(b => b.IsCancel)?.Result ?? MessageBoxResult.Cancel,
                enterResult: list?.FirstOrDefault(b => b.IsDefault)?.Result ?? list?[0].Result ?? MessageBoxResult.OK);
            if (onPick != null)
            {
                dlg.Closed += (_, _) =>
                {
                    try { onPick(dlg.Result); }
                    catch (Exception ex) { Logger.LogError("ShowNonModalCustom.onPick", ex); }
                };
            }
            Prepare(dlg, caption ?? "");
            dlg.Show();
            RegisterNonModal(dlg);      // 显示成功才登记：Show 抛异常时不留永不关闭的悬挂项
            dlg.Activate();
        }
        catch (Exception ex)
        {
            Logger.LogError("GuardDialog.ShowNonModalCustom", ex);
            try { System.Windows.MessageBox.Show(messageBoxText, caption, MessageBoxButton.OK, icon); } catch { }
        }
    }

    public static MessageBoxResult Show(string messageBoxText) =>
        Show(messageBoxText, "DSH 守护壳", MessageBoxButton.OK, MessageBoxImage.Information);

    /// <summary>自定义按钮的对话框（用于「退出UI」这类多策略选择）。</summary>
    public static MessageBoxResult ShowCustom(string messageBoxText, string caption, MessageBoxImage icon,
        params DialogButton[] buttons)
    {
        try
        {
            // 闸门挡下 → 返回 Cancel 的语义一字不改（调用点 if (r != OK) return 照旧成立）；
            // 新增的是 LastShowSuppressedByGate：调用方据此把"没弹出来"与"用户点了取消"分开，
            // 以及 NoteGateSuppressed 给用户的一条可见提示。
            if (AnyOpen) { LastShowSuppressedByGate = true; NoteGateSuppressed(caption ?? ""); return MessageBoxResult.Cancel; }
            LastShowSuppressedByGate = false;
            var list = buttons is { Length: > 0 } ? buttons : null;
            var dlg = Build(messageBoxText ?? "", caption ?? "", MessageBoxButton.OK, icon,
                customButtons: list,
                escapeResult: list?.FirstOrDefault(b => b.IsCancel)?.Result ?? MessageBoxResult.Cancel,
                enterResult: list?.FirstOrDefault(b => b.IsDefault)?.Result ?? list?[0].Result ?? MessageBoxResult.OK);
            Prepare(dlg, caption ?? "");
            dlg.ShowDialog();
            return dlg.Result;
        }
        catch (Exception ex)
        {
            Logger.LogError("GuardDialog.ShowCustom", ex);
            return System.Windows.MessageBox.Show(messageBoxText, caption, MessageBoxButton.OK, icon);
        }
    }

    public static MessageBoxResult Show(string messageBoxText, string caption) =>
        Show(messageBoxText, caption, MessageBoxButton.OK, MessageBoxImage.Information);

    public static MessageBoxResult Show(string messageBoxText, string caption,
        MessageBoxButton button, MessageBoxImage icon)
    {
        try
        {
            // ★ 本单缺陷①的现场就在这一行：原来只有 Logger.Log（空实现）⇒ 返回 Cancel ⇒
            //   调用点的 `if (r != OK) return;` 直接收场，用户看到的是"点了完全没反应"。
            //   闸门本身**不删**（放行会堆叠出两个确认框，另一个缺陷），改的是挡下之后的两个动作：
            //   ① 置 LastShowSuppressedByGate，让调用方能判别；② NoteGateSuppressed 给用户一条看得见的提示。
            if (AnyOpen) { LastShowSuppressedByGate = true; NoteGateSuppressed(caption ?? ""); return MessageBoxResult.Cancel; }
            LastShowSuppressedByGate = false;
            var dlg = Build(messageBoxText ?? "", caption ?? "", button, icon);
            Prepare(dlg, caption ?? "");
            dlg.ShowDialog();
            return dlg.Result;
        }
        catch (Exception ex)
        {
            Logger.LogError("GuardDialog.Show", ex);
            // 样式窗口构建失败时退回系统对话框，保证提示不丢
            return System.Windows.MessageBox.Show(messageBoxText, caption, button, icon);
        }
    }

    /// <summary>自检/预览用：构建对话框内容并脱离窗口返回（不显示、不阻塞），由调用方渲染成图。</summary>
    internal static FrameworkElement BuildForShot(string message, string caption,
        MessageBoxButton button, MessageBoxImage icon, bool dark)
    {
        var dlg = Build(message, caption, button, icon, dark);
        var root = (FrameworkElement)dlg.Content;
        dlg.Content = null;
        return root;
    }

    /// <summary>同上，但用自定义按钮（红/橙/灰策略）。</summary>
    internal static FrameworkElement BuildCustomForShot(string message, string caption, bool dark,
        params DialogButton[] buttons)
    {
        var dlg = Build(message, caption, MessageBoxButton.OK, MessageBoxImage.Question, dark,
            customButtons: buttons);
        var root = (FrameworkElement)dlg.Content;
        dlg.Content = null;
        return root;
    }

    /// <summary>自检用：只构建不显示，回报按钮数量、关键配色与按钮是否并排（不重叠）。</summary>
    internal static (int buttonCount, string cardBg, string textFg, string firstButtonBg, string messageFg, bool sideBySide)
        ProbeForTest(MessageBoxButton button, bool dark)
    {
        var dlg = Build("探针文本", "探针标题", button, MessageBoxImage.Question, dark);
        var root = (Grid)dlg.Content;
        var card = (Border)root.Children[0];
        var content = (StackPanel)card.Child;
        // 正文在滚动容器里（超长消息不能把弹窗顶出屏幕）
        var body = (TextBlock)((ScrollViewer)content.Children[1]).Content;
        var buttons = (StackPanel)content.Children[2];
        var first = (Border)buttons.Children[0];

        // 量一遍：确认第二个按钮排在第一个右边（而不是叠在同一格）
        bool sideBySide = true;
        if (buttons.Children.Count > 1)
        {
            var second = (Border)buttons.Children[1];
            card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            card.Arrange(new Rect(card.DesiredSize));
            card.UpdateLayout();
            try
            {
                double right1 = first.TransformToAncestor(card).Transform(new Point(first.ActualWidth, 0)).X;
                double left2 = second.TransformToAncestor(card).Transform(new Point(0, 0)).X;
                sideBySide = left2 >= right1 - 0.5;
            }
            catch { sideBySide = false; }
        }

        var result = (
            buttons.Children.Count,
            (card.Background as SolidColorBrush)?.Color.ToString() ?? "",
            ((TextBlock)((Grid)content.Children[0]).Children[1]).Foreground is SolidColorBrush tb ? tb.Color.ToString() : "",
            (first.Background as SolidColorBrush)?.Color.ToString() ?? "",
            body.Foreground is SolidColorBrush mb ? mb.Color.ToString() : "",
            sideBySide);
        dlg.Close();
        return result;
    }

    /// <summary>
    /// 自检用：用一段超长文本建对话框，量出卡片高度上限、正文是否可滚动、按钮行是否可见。
    /// </summary>
    internal static (double cardHeight, double limit, bool hasScroll, bool buttonsVisible)
        LongMessageProbeForTest(string message, bool dark)
    {
        var dlg = Build(message, "探针标题", MessageBoxButton.OK, MessageBoxImage.Error, dark);
        var root = (Grid)dlg.Content;
        var card = (Border)root.Children[0];
        var content = (StackPanel)card.Child;
        var scroll = content.Children.OfType<ScrollViewer>().FirstOrDefault();
        var buttonRow = content.Children.OfType<StackPanel>().LastOrDefault();

        card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        card.Arrange(new Rect(card.DesiredSize));
        card.UpdateLayout();

        var result = (card.ActualHeight,
                      Math.Max(120, SystemParameters.WorkArea.Height - 160),
                      scroll is { ScrollableHeight: > 0 },
                      buttonRow is { ActualHeight: > 0 });
        dlg.Close();
        return result;
    }

    private sealed class DialogWindow : Window
    {
        public MessageBoxResult Result = MessageBoxResult.OK;
    }

    private static DialogWindow Build(string message, string caption, MessageBoxButton button,
        MessageBoxImage icon, bool? forceDark = null, DialogButton[]? customButtons = null,
        MessageBoxResult? escapeResult = null, MessageBoxResult? enterResult = null)
    {
        bool dark = forceDark ?? ThemeManager.IsDark;

        Color cardColor = dark ? Color.FromRgb(0x1C, 0x20, 0x29) : Color.FromRgb(0xF2, 0xF3, 0xF7);
        Color textColor = dark ? Color.FromRgb(0xF5, 0xF5, 0xF7) : Color.FromRgb(0x1C, 0x1C, 0x1E);
        Color subColor = dark ? Color.FromRgb(0xC7, 0xC7, 0xCC) : Color.FromRgb(0x4A, 0x4A, 0x4C);
        Color borderColor = dark ? Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x26, 0x00, 0x00, 0x00);

        (string glyph, Color tone) = icon switch
        {
            MessageBoxImage.Error => ("\uEA39", Color.FromRgb(0xFF, 0x3B, 0x30)),
            MessageBoxImage.Warning => ("\uE7BA", Color.FromRgb(0xFF, 0x9F, 0x0A)),
            MessageBoxImage.Question => ("\uE897", Color.FromRgb(0x5A, 0xC8, 0xFA)),
            _ => ("\uE946", Color.FromRgb(0x0A, 0x84, 0xFF))
        };

        var dlg = new DialogWindow
        {
            Title = caption,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            UseLayoutRounding = true
        };

        Border MakeButton(string label, Color bg, MessageBoxResult result)
        {
            var b = new Border
            {
                Child = new TextBlock
                {
                    Text = label,
                    FontSize = 12.5,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                },
                Background = new SolidColorBrush(bg),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(18, 7, 18, 7),
                Margin = new Thickness(8, 0, 0, 0),
                Cursor = Cursors.Hand
            };
            // 按下即关；若按下事件被其他元素消费，松开也补一次——按钮永远点得动
            void Pick()
            {
                try { dlg.Result = result; dlg.Close(); } catch { }
            }
            b.MouseLeftButtonDown += (_, e) => { Pick(); e.Handled = true; };
            b.MouseLeftButtonUp += (_, e) => { if (dlg.IsVisible) Pick(); e.Handled = true; };

            // 悬停 / 移出都按**当前**主题现算，绝不捕获 Build 时的 dark。
            // 现场缺陷：原来 MouseLeave 还原的是闭包抓到的旧主题底色（灰按钮 0x8E8E93），
            // 于是"切主题时鼠标正悬停在灰按钮上"会留下一个旧主题色的按钮：
            //   MouseEnter 落的 Shift(0x8E8E93, dark=true) = 0xA6A6AB **不在映射表里** ⇒
            //   这次切主题的 Paint 改不动它（映射没命中，Paint 整条让路）；
            //   随后 MouseLeave 又还原成构建时那个旧主题灰 ⇒ 该按钮停用旧主题配色，
            //   一直到下一次悬停交互才自愈。
            // 取色方式：**按当前主题现算底色**，而不是把 0xA6A6AB 加进映射表。
            //   ① 悬停色是"底色的一次函数"，两张表都查不到它：夜间悬停 0xA6A6AB 与
            //      日间悬停 Shift(0x6B6B70, false)=0x535358 各占一个主题，得往表里补两条；
            //   ② 映射表的硬约束是"键集合与值集合不相交"（见 ThemeManager 注释），
            //      为了一个悬停态去扩键集会破坏这个不变量、得不偿失；
            //   ③ 逐次现算后，四个组合（夜间/日间 × 常态/悬停）永远取自同一个主题，不存在残留。
            // 底色一律**查映射表**换算，不能按 ±0x18 去推：两张表两侧的偏移量并不相等
            //   （灰按钮夜间 0x8E8E93 → 日间 0x6B6B70，差 0x23），算出来会是一个两张表都没有的
            //   新色（探针实测：日间底算成 0x76767B）⇒ 等于又造了一个永远残留的旧主题色。
            Color BaseNow() => ThemeManager.RemapColor(bg, ThemeManager.IsDark);
            // 纯强调色按钮两套主题同色，悬停不做提亮——与改前观感一致（改前它们经 Shift 后恰好落到原色）。
            bool hoverLift = !IsPureAccent(bg);
            b.MouseEnter += (_, _) =>
            {
                Color baseNow = BaseNow();
                // +0x18 提亮：与改前**夜间**那一格逐字节一致（灰 0x8E8E93 → 0xA6A6AB、红 → 0xFF5348），
                // 也是本项目的统一悬停约定（见 ButtonFx「悬停轻微放大 + 变亮」）。
                // 日间这一格在改前是**空白**：底色只有夜间值，原式 Shift(夜底, dark=false) 算出的
                // 深色从未在日间渲染过（日间常态被 Paint 刷成 0x6B6B70 盖掉了它）⇒ 现在补的是新行为、不改旧观感。
                b.Background = new SolidColorBrush(hoverLift ? Shift(baseNow, true) : baseNow);
            };
            b.MouseLeave += (_, _) => b.Background = new SolidColorBrush(BaseNow());
            return b;
        }

        // 必须是横向 StackPanel：放进 Grid 会让所有按钮落在同一格里互相覆盖（曾导致「是」被「否」盖住）
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        var list = new List<Border>();
        if (customButtons != null)
        {
            foreach (var cb in customButtons) list.Add(MakeButton(cb.Label, cb.Background, cb.Result));
        }
        else
        {
            switch (button)
            {
                case MessageBoxButton.OKCancel:
                    list.Add(MakeButton("确定", Color.FromRgb(0x34, 0xC7, 0x59), MessageBoxResult.OK));
                    list.Add(MakeButton("取消", Color.FromRgb(0x8E, 0x8E, 0x93), MessageBoxResult.Cancel));
                    break;
                case MessageBoxButton.YesNo:
                    list.Add(MakeButton("是", Color.FromRgb(0x34, 0xC7, 0x59), MessageBoxResult.Yes));
                    list.Add(MakeButton("否", Color.FromRgb(0xFF, 0x9F, 0x0A), MessageBoxResult.No));
                    break;
                case MessageBoxButton.YesNoCancel:
                    list.Add(MakeButton("是", Color.FromRgb(0x34, 0xC7, 0x59), MessageBoxResult.Yes));
                    list.Add(MakeButton("否", Color.FromRgb(0xFF, 0x9F, 0x0A), MessageBoxResult.No));
                    list.Add(MakeButton("取消", Color.FromRgb(0x8E, 0x8E, 0x93), MessageBoxResult.Cancel));
                    break;
                default:
                    list.Add(MakeButton("确定", Color.FromRgb(0x34, 0xC7, 0x59), MessageBoxResult.OK));
                    break;
            }
        }
        foreach (var b in list) buttons.Children.Add(b);

        var titleRow = new Grid();
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 18,
            Foreground = new SolidColorBrush(tone),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        });
        var titleText = new TextBlock
        {
            Text = caption,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(textColor),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetColumn(titleText, 1);
        titleRow.Children.Add(titleText);

        var content = new StackPanel { Margin = new Thickness(20, 18, 20, 18) };
        content.Children.Add(titleRow);

        // 正文放进滚动容器并设屏高上限：消息可能很长（启动失败诊断 + 引擎输出），
        // 不设上限会把卡片顶得比屏幕还高——「确定」被推到屏幕外，用户无法关闭窗口（现场 bug）。
        double limit = Math.Max(120, SystemParameters.WorkArea.Height - 160);
        content.MaxHeight = limit;
        var messageScroll = new ScrollViewer
        {
            Content = new TextBlock
            {
                Text = message,
                FontSize = 12.5,
                LineHeight = 20,
                Foreground = new SolidColorBrush(subColor),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 400
            },
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = Math.Max(120, limit - 120),      // 再扣掉标题行、按钮行与内外边距
            Margin = new Thickness(0, 12, 0, 0)
        };
        content.Children.Add(messageScroll);
        content.Children.Add(buttons);                   // 按钮行在滚动区之外，任何长度下都可见可点

        var card = new Border
        {
            Child = content,
            Background = new SolidColorBrush(cardColor),
            BorderBrush = new SolidColorBrush(borderColor),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Margin = new Thickness(14),
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.45, Color = Colors.Black }
        };

        var root = new Grid { Background = Brushes.Transparent };
        root.Children.Add(card);
        ButtonFx.Wire(root);      // 弹窗按钮同样要有悬停/按下动效
        card.MouseLeftButtonDown += (_, e) =>
        {
            try { dlg.DragMove(); e.Handled = true; } catch { }
        };
        dlg.Content = root;

        dlg.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                dlg.Result = escapeResult ?? button switch
                {
                    MessageBoxButton.YesNo => MessageBoxResult.No,
                    MessageBoxButton.OKCancel or MessageBoxButton.YesNoCancel => MessageBoxResult.Cancel,
                    _ => MessageBoxResult.OK
                };
                dlg.Close();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                dlg.Result = enterResult ?? button switch
                {
                    MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel => MessageBoxResult.Yes,
                    _ => MessageBoxResult.OK
                };
                dlg.Close();
                e.Handled = true;
            }
        };
        return dlg;
    }

    /// <summary>悬停时把底色略微提亮（夜间）或压深（日间）。</summary>
    private static Color Shift(Color c, bool dark)
    {
        int d = dark ? 0x18 : -0x18;
        return Color.FromRgb(
            (byte)Math.Clamp(c.R + d, 0, 255),
            (byte)Math.Clamp(c.G + d, 0, 255),
            (byte)Math.Clamp(c.B + d, 0, 255));
    }

    /// <summary>
    /// 是否为"两套主题同色"的纯强调色按钮（确定绿 <c>#34C759</c> / 否橙 <c>#FF9F0A</c>）。
    /// 这类色两套主题一致、且都不在映射表里 ⇒ 底色恒定，悬停也就不该再提亮：
    /// 改前它们经 <c>Shift(c, dark=true) = c + 0x18</c> 后每个通道都 > 255 被 Clamp 回原色，
    /// 视觉上"悬停无变化"，这里是显式写出同一个结果（不再依赖 Clamp 兜底）。
    /// 灰（取消/仅确定）与红（卸载等）**不是**纯强调色：它们要参与 Shift 提亮，与改前一致。
    /// </summary>
    private static bool IsPureAccent(Color c) =>
        (c.R == 0x34 && c.G == 0xC7 && c.B == 0x59) ||      // 确定 / 是
        (c.R == 0xFF && c.G == 0x9F && c.B == 0x0A);        // 否
}
