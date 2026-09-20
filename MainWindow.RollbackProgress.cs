using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace DSHGuard;

/// <summary>
/// 「涉及回滚插件」那一路回滚的步骤（纯枚举）。文案由
/// <see cref="MainWindow.RollbackStepText"/> 统一生成，自检逐条断言。
/// </summary>
internal enum RollbackStep
{
    /// <summary>① 按快照覆盖配置与插件清单。</summary>
    Preparing,
    /// <summary>② 把 DSH 版本钉回快照记录的那一版。</summary>
    VersionPin,
    /// <summary>③ 重装插件之前先停引擎。</summary>
    StoppingEngine,
    /// <summary>③ 按快照重装插件（耗时最长的一步，可能几分钟）。</summary>
    Reinstalling,
    /// <summary>④ 逐项核对磁盘上插件的真实版本。</summary>
    Verifying,
    /// <summary>收尾。</summary>
    Done
}

/// <summary>用户想关掉进度窗的原因（纯枚举，便于把「谁的请求才放行」写成可自检的判据）。</summary>
internal enum RollbackCloseReason
{
    /// <summary>按了 Esc。</summary>
    UserEscape,
    /// <summary>Alt+F4 / 系统菜单这类系统关闭。</summary>
    UserSystemClose,
    /// <summary>流程自己走完了（唯一放行的一种）。</summary>
    Finished
}

/// <summary>
/// 快照回滚的进度窗（partial 部分类）：只放「涉及回滚插件」那一路需要的进度弹窗。
///
/// ══ 需求原文 ══
/// 「涉及到回滚插件的回滚操作时弹出弹窗显示进度，因为和别的回滚不一样，这个需要时间较长，
///   弹窗进度条，还是绿色滚动条动画的，不允许用户在此期间做其他操作。」
///
/// ══ 为什么不用 ShowDialog（关键，改动它之前先读这段）══
/// <see cref="MainWindow.DoRestoreAsync"/> 内部通篇是 await（每次推进度都要回 UI 线程）。
/// 若在 UI 线程上 ShowDialog：这个调用**要等窗口关闭才返回**，而"关窗"这句代码排在它后面
/// —— 于是关窗永远等不到，界面卡死、进度条也不动（典型的自锁）。
///
/// ══ 采用的方案：伪模态（Owner + 主窗 IsEnabled=false + Close 时还原）══
///   · Owner = 主窗 + WindowStartupLocation.CenterOwner：进度窗永远压在守护壳之上，居中，不会点开主窗就找不到它；
///   · 主窗 IsEnabled = false：WPF 里被禁用的元素不参与命中测试 ⇒ 主窗上的按钮 / 下拉 / 卡片 / 列表
///     一律点不动（与真模态同等的"不能操作"），但 Show() 立刻返回，await 的续体照常跑，动画照常动；
///   · Topmost = true：这一步要几分钟，不能被别的窗口盖住（用户看不到"还在跑"就会以为卡死了）；
///   · 关窗一定还原主窗可用（关窗动作统一走 <see cref="MainWindow.CloseRollbackProgress"/>，见 DoRestoreAsync 的 finally）
///     —— 不还原就会留下一个"看着正常、点哪儿都没反应"的壳（本程序反复遭遇的问题）。
/// </summary>
public partial class MainWindow : Window
{
    // ══════════════ 「正在回滚插件」进度窗 ══════════════

    /// <summary>当前显示着的回滚进度窗（空 = 没弹）。</summary>
    private RollbackProgressWindow? _rollbackProgress;

    /// <summary>弹进度窗之前主窗的可用状态；关窗后按**原值**还原（不硬写 true）。</summary>
    private bool _rollbackProgressOwnerEnabled = true;

    /// <summary>进度窗标题（实现与自检共用同一份文案）。</summary>
    internal const string RollbackProgressTitle = "正在回滚插件";

    /// <summary>
    /// 这次回滚要不要弹「正在回滚插件」的进度窗（纯函数，自检直接断言）。
    ///
    /// 判据 = **真的要重装插件**（用户勾了「回退插件」）**且**要重装的插件名单非空：
    ///   · 名单为空 ⇒ 没有"涉及回滚插件"这件事，回滚只是覆盖几份文件，几秒钟就完，
    ///     保持原来的底部进度提示即可，不该弹一个"必须等"的框；
    ///   · 没勾「回退插件」⇒ 根本不会执行那条耗时数分钟的安装命令，
    ///     此时弹模态框只会白拦一次（版本 / 磁盘核对那两步是纯读盘，一闪而过）。
    /// </summary>
    internal static bool RollbackProgressNeeded(bool restorePlugins, int pluginCount)
        => restorePlugins && pluginCount > 0;

    /// <summary>
    /// 步骤说明行（纯函数）。只讲"正在做什么"：不带路径、命令、参数、包名、内部术语，
    /// 不解释耗时、不替用户操心，技术细节一律留在日志里。
    /// </summary>
    internal static string RollbackStepText(RollbackStep step, int pluginCount)
    {
        int n = pluginCount < 0 ? 0 : pluginCount;
        return step switch
        {
            RollbackStep.Preparing => "正在按快照还原配置与插件清单…",
            RollbackStep.VersionPin => "正在将 DSH 版本还原为快照记录的版本…",
            RollbackStep.StoppingEngine => "正在停止运行中的引擎…",
            RollbackStep.Reinstalling => n > 0
                ? $"正在重装插件（{n} 个）…"
                : "正在重装插件…",
            RollbackStep.Verifying => "正在核对插件版本是否已还原…",
            _ => "正在收尾…"
        };
    }

    /// <summary>
    /// 「x / N」那一行（纯函数）：总数未知（≤ 0）时返回**空串** = 不显示这一行；
    /// 越界的 x 夹到 [0, N]，不会出现「15 / 13」这种数。
    /// </summary>
    internal static string RollbackCountText(int done, int total)
    {
        if (total <= 0) return "";
        return $"{Math.Clamp(done, 0, total)} / {total}";
    }

    /// <summary>
    /// 关窗请求的放行判定（纯函数）：**只有流程自己走完才放行**，用户的 Esc / 系统关闭一律不放行。
    /// 窗口的 Esc 拦截与 Closing 拦截都走这一个判据（不在事件处理器里再写第二份），
    /// 所以这条断言覆盖的就是真机上"关不关得掉"的那份逻辑。
    /// </summary>
    internal static bool AllowRollbackProgressClose(RollbackCloseReason reason)
        => reason == RollbackCloseReason.Finished;

    /// <summary>
    /// 弹出进度窗并把主窗置为不可用（伪模态，理由见类注释）。返回的窗口用于后续推进度；
    /// 构建失败返回 null —— 进度窗只是"给人看的"，它建不出来也**不能**阻断回滚。
    /// </summary>
    private RollbackProgressWindow? BeginRollbackProgress(string stepText, int pluginCount)
    {
        try
        {
            if (_rollbackProgress != null) return _rollbackProgress;      // 幂等：已经在显示就直接复用

            var dlg = new RollbackProgressWindow(RollbackProgressTitle, stepText);
            dlg.Owner = this;                                   // 永远压在守护壳之上，居中显示
            dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            dlg.Topmost = true;                                 // 几分钟的过程不能被别的窗口盖住
            dlg.Closed += (_, _) =>
            {
                // 无论窗口是怎么关掉的（代码关、进程退出、任何意外），主窗都必须回到弹窗前的可用状态
                IsEnabled = _rollbackProgressOwnerEnabled;
                _rollbackProgress = null;
                try { Activate(); } catch { }
            };

            _rollbackProgressOwnerEnabled = IsEnabled;
            IsEnabled = false;                                  // 伪模态：主窗一律点不动
            dlg.Show();                                         // 非阻塞；ShowDialog 会和本方法的 await 自锁
            try { dlg.Activate(); } catch { }
            _rollbackProgress = dlg;
            Logger.NoteDiagnosis($"回滚进度窗已显示：要重装的插件 {pluginCount} 个，主窗已临时置为不可用");
            return dlg;
        }
        catch (Exception ex)
        {
            Logger.LogError("BeginRollbackProgress", ex);
            // 弹不出来也不能把主窗留在"点不动"的状态
            try { IsEnabled = _rollbackProgressOwnerEnabled; } catch { }
            _rollbackProgress = null;
            return null;
        }
    }

    /// <summary>
    /// 关掉进度窗并还原主窗可用。**幂等**：没弹过、已经关掉、重复调用都安全。
    /// 由 <see cref="DoRestoreAsync"/> 的 finally 兜底调用 —— 成功、失败、异常
    /// （以及将来若支持中途取消）都必定走到这里，绝不会留下无法关闭的窗口或不响应的界面。
    /// </summary>
    private void CloseRollbackProgress()
    {
        var dlg = _rollbackProgress;
        _rollbackProgress = null;
        try { dlg?.FinishAndClose(); }
        catch (Exception ex) { Logger.LogError("CloseRollbackProgress", ex); }

        // 兜底二连：万一窗口因任何原因没关成（Closed 没触发），主窗也必须回到可用
        // —— 留下"看着正常、点哪儿都没反应"的界面是本程序反复遭遇的问题。
        // 正常情况下 Closed 事件已经还原过一次，这里是同值重写，幂等。
        if (dlg != null)
        {
            try { IsEnabled = _rollbackProgressOwnerEnabled; } catch { }
        }
    }
}

/// <summary>
/// 「正在回滚插件」进度窗：深色卡片 + 圆角 + 一根绿色流动进度条，**没有任何关闭入口**。
///
/// 不复用 <see cref="GuardDialog"/>：那个是"问一句就走"的决策框（有按钮、Esc 有语义、
/// ShowDialog 阻塞），语义与"过程可视化 + 不许被打断"恰好相反，套过来只会互相打架。
/// 外观（卡片底色 / 描边 / 圆角 / 阴影 / 文案层级）与 GuardDialog、UninstallWindow 完全同款。
/// </summary>
internal sealed class RollbackProgressWindow : Window
{
    /// <summary>卡片内容宽度（进度条轨道与它同宽）。</summary>
    private const double CardWidth = 360;
    /// <summary>进度条高度（与批量操作那条 5px 的观感一致，这里略厚一点更醒目的"在流动"）。</summary>
    private const double TrackHeight = 6;
    /// <summary>绿色滑块的宽度。</summary>
    private const double ChunkWidth = 96;
    /// <summary>绿色滑块从左边跑到右边一趟的秒数（流动动画的节奏）。</summary>
    private const double MarqueeSeconds = 1.6;
    /// <summary>开着这么久还没被关掉就往日志里留一行证据（这一步最长 ~30 分钟：两次安装各 15 分钟超时）。</summary>
    private const int WatchdogMinutes = 45;

    /// <summary>绿色：与全壳「成功 / 可用」同一个绿，日夜两套主题下都不变。</summary>
    private static readonly Color Green = Color.FromRgb(0x34, 0xC7, 0x59);

    private readonly TextBlock _stepText;
    private readonly TextBlock _countText;
    private readonly Border _track;
    private readonly List<Border> _chunks = new();
    private readonly List<TranslateTransform> _shifts = new();
    private readonly Storyboard _marquee = new();
    private readonly List<double> _beginTimes = new();

    /// <summary>只有流程自己结束才置真（关窗的唯一放行条件）。</summary>
    private bool _allowClose;
    /// <summary>"用户想关但被忽略"只记一次日志，免得狂按 Esc 刷屏。</summary>
    private bool _closeAttemptLogged;

    private DispatcherTimer? _watchdog;

    /// <param name="forceDark">
    /// 只给预览用：强制夜/日配色（<c>--dialog-shot</c> 出样张时指定），正常路径传 null = 跟随当前主题。
    /// </param>
    internal RollbackProgressWindow(string title, string stepText, bool? forceDark = null)
    {
        bool dark = forceDark ?? ThemeManager.IsDark;
        // 与 GuardDialog / UninstallWindow 同一套配色（同一批硬编码值，日夜由主题映射表负责）
        Color cardColor = dark ? Color.FromRgb(0x1C, 0x20, 0x29) : Color.FromRgb(0xF2, 0xF3, 0xF7);
        Color textColor = dark ? Color.FromRgb(0xF5, 0xF5, 0xF7) : Color.FromRgb(0x1C, 0x1C, 0x1E);
        Color subColor = dark ? Color.FromRgb(0xC7, 0xC7, 0xCC) : Color.FromRgb(0x4A, 0x4A, 0x4C);
        Color hintColor = dark ? Color.FromRgb(0x8E, 0x8E, 0x93) : Color.FromRgb(0x6B, 0x6B, 0x70);
        Color borderColor = dark ? Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x26, 0x00, 0x00, 0x00);
        // 轨道底色用半透明白：主题映射表认得这一项（日间会翻成浅灰），压在哪种底色上都不突兀
        Color trackColor = Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF);

        Title = title;
        WindowStyle = WindowStyle.None;          // 自绘卡片：没有系统标题栏 = 没有那个「×」
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        var content = new StackPanel { Margin = new Thickness(22, 20, 22, 20), Width = CardWidth };

        // ── 标题行：绿色图标 + 「正在回滚插件」 ──
        var titleRow = new Grid();
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.Children.Add(new TextBlock
        {
            Text = "\uE72C",                     // Segoe MDL2 Assets：刷新箭头
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 18,
            Foreground = new SolidColorBrush(Green),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        });
        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(textColor),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetColumn(titleText, 1);
        titleRow.Children.Add(titleText);
        content.Children.Add(titleRow);

        // ── 一行说明「现在在做什么」（随步骤更新）──
        _stepText = new TextBlock
        {
            Text = stepText,
            FontSize = 12.5,
            LineHeight = 19,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(subColor),
            Margin = new Thickness(0, 12, 0, 0)
        };
        content.Children.Add(_stepText);

        // ── 绿色流动进度条 ──
        // 不用 ProgressBar 的 IsIndeterminate：那套跑的是系统主题模板里的动画，
        // 段色、圆角、节奏都改不动（而且要额外跟主题映射表打招呼）。
        // 这里自己搭：圆角槽 + 两段绿色滑块，用 TranslateTransform 从左往右走同一条路线、
        // 错开半个周期 ⇒ 观感就是"一段绿光一直在流"，且完全由本文件的参数说了算。
        var trackInner = new Grid { Width = CardWidth, Height = TrackHeight };
        trackInner.Clip = new RectangleGeometry(new Rect(0, 0, CardWidth, TrackHeight), 3, 3);   // 滑块进出时按圆角裁掉
        for (int i = 0; i < 2; i++)
        {
            var shift = new TranslateTransform(-ChunkWidth, 0);
            _shifts.Add(shift);
            var chunk = new Border
            {
                Width = ChunkWidth,
                Height = TrackHeight,
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(Green),
                HorizontalAlignment = HorizontalAlignment.Left,
                RenderTransform = shift
            };
            _chunks.Add(chunk);
            trackInner.Children.Add(chunk);

            double begin = MarqueeSeconds * i / 2.0;      // 第二段延迟半个周期 ⇒ 看着是连续的流动
            _beginTimes.Add(begin);
            var slide = new DoubleAnimation
            {
                From = -ChunkWidth,
                To = CardWidth,
                Duration = TimeSpan.FromSeconds(MarqueeSeconds),
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromSeconds(begin)
            };
            Storyboard.SetTarget(slide, chunk);
            Storyboard.SetTargetProperty(slide, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.X)"));
            _marquee.Children.Add(slide);
        }
        _track = new Border
        {
            Child = trackInner,
            Height = TrackHeight,
            Width = CardWidth,
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(trackColor),
            Margin = new Thickness(0, 14, 0, 0)
        };
        content.Children.Add(_track);

        // ── 「x / N」：有确切数量时才出现（N 未知就整行收起，不摆一个空行占位）──
        _countText = new TextBlock
        {
            Text = "",
            FontSize = 11.5,
            Foreground = new SolidColorBrush(hintColor),
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed
        };
        content.Children.Add(_countText);

        content.Children.Add(new TextBlock
        {
            Text = "回滚期间请勿操作，完成后将显示结果。",
            FontSize = 11,
            LineHeight = 17,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(hintColor),
            Margin = new Thickness(0, 12, 0, 0)
        });

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
        card.MouseLeftButtonDown += (_, e) => { try { DragMove(); e.Handled = true; } catch { } };
        Content = root;

        // 动画要等窗口真的显示出来再起（没显示就没有渲染时钟；自检只建不显示，也走不到这里）
        Loaded += (_, _) =>
        {
            try { _marquee.Begin(this, true); }
            catch (Exception ex) { Logger.LogError("RollbackProgressWindow 动画启动", ex); }
        };

        // ── 没有任何关闭入口：没有按钮、Esc 不理、Alt+F4 也不理 ──
        // 放行判定只有 MainWindow.AllowRollbackProgressClose 这一份（自检直接断言它）。
        PreviewKeyDown += (_, e) =>
        {
            bool escape = e.Key == Key.Escape;
            bool altF4 = e.Key == Key.System && e.SystemKey == Key.F4;
            if (!escape && !altF4) return;
            var why = escape ? RollbackCloseReason.UserEscape : RollbackCloseReason.UserSystemClose;
            if (MainWindow.AllowRollbackProgressClose(why)) return;
            e.Handled = true;
            NoteCloseAttempt(escape ? "Esc" : "Alt+F4");
        };
        Closing += (_, e) =>
        {
            // 应用正在退出（托盘「退出」→ Application.Shutdown、系统注销）时一律放行：
            // 此时再拦截就会造成"无法关闭的窗口"，用户只能通过任务管理器结束进程 —— 正是本程序反复遭遇的问题。
            bool appExiting = Application.Current == null
                              || Application.Current.Dispatcher.HasShutdownStarted
                              || (Owner is { IsVisible: false });
            if (appExiting) return;

            var why = _allowClose ? RollbackCloseReason.Finished : RollbackCloseReason.UserSystemClose;
            if (MainWindow.AllowRollbackProgressClose(why)) return;
            e.Cancel = true;
            NoteCloseAttempt("系统关闭");
        };

        StartWatchdog();
    }

    /// <summary>推进「现在在做什么」那一行（只在 UI 线程调用；文案由 MainWindow.RollbackStepText 生成）。</summary>
    internal void SetStep(string text) => _stepText.Text = text ?? "";

    /// <summary>推进「x / N」那一行：空串 = 不显示（总数未知时就是这样）。</summary>
    internal void SetCount(string text)
    {
        _countText.Text = text ?? "";
        _countText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>流程自己走完了：**唯一**允许关窗的入口（用户点不出来，代码才调得到）。</summary>
    internal void FinishAndClose()
    {
        _allowClose = true;
        try { _watchdog?.Stop(); } catch { }
        try { _marquee.Stop(this); } catch { }
        try { Close(); } catch (Exception ex) { Logger.LogError("RollbackProgressWindow.FinishAndClose", ex); }
    }

    /// <summary>用户尝试关闭 → 忽略并留一条证据（只记一次，狂按 Esc 不刷屏）。</summary>
    private void NoteCloseAttempt(string how)
    {
        if (_closeAttemptLogged) return;
        _closeAttemptLogged = true;
        // Logger.Log 是空实现（写入不生效），留证必须走 NoteDiagnosis
        Logger.NoteDiagnosis($"回滚进度窗：收到用户的关闭请求（{how}），已忽略 —— 过程结束时会由代码关掉");
    }

    /// <summary>
    /// 看门狗：开着超过 <see cref="WatchdogMinutes"/> 分钟还没关，就往日志里留一行证据
    /// （下次出现"回滚卡住、窗口无法关闭"时能有据可查，与 GuardDialog 的弹窗看门狗同一套做法）。
    /// </summary>
    private void StartWatchdog()
    {
        try
        {
            _watchdog = new DispatcherTimer { Interval = TimeSpan.FromMinutes(WatchdogMinutes) };
            _watchdog.Tick += (_, _) =>
            {
                try { _watchdog?.Stop(); } catch { }
                Logger.NoteDiagnosis($"回滚进度窗已打开超过 {WatchdogMinutes} 分钟仍未被关闭"
                    + $"（UI 线程 {Environment.CurrentManagedThreadId}，主窗可用={Application.Current?.MainWindow?.IsEnabled}）");
            };
            _watchdog.Start();
            Closed += (_, _) => { try { _watchdog?.Stop(); } catch { } };
        }
        catch (Exception ex) { Logger.LogError("RollbackProgressWindow.StartWatchdog", ex); }
    }

    /// <summary>
    /// 自检用：只建不显示，回报这扇窗**能被钉死的事实**（有没有关闭按钮、绿色滑块与流动动画的参数、
    /// 两行文案与显隐、有没有系统标题栏）。
    /// 窗口长什么样（配色、圆角、动画是否真的在动）自检看不出来 —— 那部分要上屏拍图核对。
    /// </summary>
    internal (string Title, string Step, string Count, bool CountVisible, bool HasButton,
              string ChunkColor, string TrackColor, int ChunkCount, double Seconds, bool Forever,
              double[] BeginTimes, bool HasSystemTitleBar, bool InTaskbar) FactsForTest()
    {
        if (Content is FrameworkElement fe)
        {
            try
            {
                fe.Measure(new Size(1000, 1000));
                fe.Arrange(new Rect(0, 0, 420, 320));
                fe.UpdateLayout();
            }
            catch { }
        }

        var first = _marquee.Children.OfType<DoubleAnimation>().FirstOrDefault();
        return (Title ?? "",
                _stepText.Text ?? "",
                _countText.Text ?? "",
                _countText.Visibility == Visibility.Visible,
                HasButtonDeep(Content as DependencyObject),
                (_chunks.Count > 0 ? (_chunks[0].Background as SolidColorBrush)?.Color.ToString() : null) ?? "",
                (_track.Background as SolidColorBrush)?.Color.ToString() ?? "",
                _chunks.Count,
                first?.Duration.TimeSpan.TotalSeconds ?? 0,
                first?.RepeatBehavior == RepeatBehavior.Forever,
                _beginTimes.ToArray(),
                WindowStyle != System.Windows.WindowStyle.None,
                ShowInTaskbar);
    }

    /// <summary>自检用：程序自己能关得掉（用户无法关闭 ≠ 无法关闭）。</summary>
    internal bool ClosePermittedForTest() => _allowClose;

    /// <summary>
    /// 预览用：把卡片内容脱离窗口返回（不显示、不阻塞），由调用方渲染成 PNG ——
    /// 与 <see cref="GuardDialog.BuildForShot"/> 同一套做法，供 <c>--dialog-shot</c> 出样张人工核对外观。
    ///
    /// 样张是**静止**的一帧：动画要窗口显示后（Loaded）才起，所以这里把两段滑块按固定位置摆到轨道上
    /// （不然样张上是一条空槽，看不出"绿色流动条"长什么样）。
    /// </summary>
    internal FrameworkElement BuildForShot()
    {
        var root = (FrameworkElement)Content;
        Content = null;
        for (int i = 0; i < _shifts.Count; i++)
            _shifts[i].X = 24 + i * 150;      // 摆到轨道可见处
        return root;
    }

    /// <summary>视觉树里有没有按钮（本窗应当一个都没有：不给关闭入口，也没有别的可点项）。</summary>
    private static bool HasButtonDeep(DependencyObject? root)
    {
        if (root == null) return false;
        if (root is System.Windows.Controls.Primitives.ButtonBase) return true;
        int n = 0;
        try { n = VisualTreeHelper.GetChildrenCount(root); } catch { return false; }
        for (int i = 0; i < n; i++)
            if (HasButtonDeep(VisualTreeHelper.GetChild(root, i))) return true;
        return false;
    }
}
