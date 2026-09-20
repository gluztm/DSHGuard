using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

using System.Windows.Controls;

namespace DSHGuard;

public partial class MainWindow : Window
{
    private ProcessManager? _processManager;

    /// <summary>
    /// 引擎输出处理器的引用。**必须存起来**：订阅处写的是匿名 lambda，不留引用就无从退订，
    /// 而它捕获了本窗口 —— 不退订就等于引擎输出这条线一直钉着窗口，
    /// 且旧引擎（已停止/已换实例）的输出仍会追加进新一轮的「日志」实时面板。
    /// 退订点见 <see cref="DetachEngineManager"/>。
    /// </summary>
    private EventHandler<string>? _engineOutputHandler;
    private readonly SettingsManager _settings = new();
    private readonly DispatcherTimer _statusTimer;
    private bool _isRunning;
    private bool _isStarting;
    private CancellationTokenSource? _cts;
    private System.Windows.Forms.NotifyIcon? _trayIcon;

    // ═══ 动画（透明 GIF） ═══

    private DateTime _engineStartTime = DateTime.MinValue;
    /// <summary>引擎由外部（bash/其他终端）启动：守护壳只观测显示，不终止该进程。</summary>
    private bool _engineExternal;

    /// <summary>已对托管引擎执行「只解绑」：引擎还在跑，但本程序不再接管，端口同步也不再把它翻回运行中。</summary>
    private bool _unbound;

    // ═══ 版本记忆联动（记录本次启动用的版本 / 异常输出 / 提前退出） ═══
    /// <summary>本次启动实际使用的版本（固定版本；@latest 时留空，就绪后再探测）。</summary>
    private string _runVersion = "";
    /// <summary>本次运行里捕获到的第一条致命输出（启动异常时作为回退提示的依据）。</summary>
    private string _runIssueLine = "";
    /// <summary>兜底行：固定特征词没认出来时也记一条「像错误」的 stderr（别再写「原因不明」）。</summary>
    private string _runLastErrorLine = "";
    /// <summary>
    /// 引擎 stderr 分诊状态：上一行之后是否仍在"已知无害"段里（该段由 <see cref="Logger.ClassifyStderrStep"/> 界定）。
    /// 必须跨行保存 —— `AttachConsole failed` 的调用栈是多行，逐行判会把栈帧全漏进"真问题"。
    /// stderr 由进程输出线程串行读入，故与 <see cref="OutputReceived"/> 的处理同线程；仍上锁以求稳。
    /// </summary>
    private bool _stderrInBenignBlock;
    private readonly object _stderrBlockLock = new();
    /// <summary>本次运行是否已为"已知无害"提示过一次（事件栏只提示一次，不刷屏）。</summary>
    private bool _knownNoiseNoted;
    /// <summary>本次运行的异常是否已记账（避免端口同步多条分支重复计数）。</summary>
    private bool _runIssueCounted;
    /// <summary>引擎自己打印的访问地址（带 token）；空则退回裸地址。
    /// 由进程输出线程写、UI 线程读，故显式 volatile。</summary>
    private volatile string _readyUrl = "";
    /// <summary>上一次「终止引擎」未成功（端口仍被占用）时，再点一次走强杀档。</summary>
    private bool _terminateAttempted;
    /// <summary>本次「一键启动」是否已自动重试过一次（避免无限重试）。</summary>
    private bool _startupRetried;
    /// <summary>本次启动临时改用的版本说明符（未固定版本秒退时，改用本机缓存里最新的那个重试）。</summary>
    private string _specOverride = "";
    /// <summary>本次「一键启动」是否已清过本程序自己造的组件联接（幂等，一次运行只清一次）。</summary>
    private bool _junctionsCleaned;
    /// <summary>UI 线程心跳（卡死取证用）：UI 线程每秒更新一次，后台看门狗发现长时间不跳就写日志。</summary>
    private DateTime _uiBeatUtc = DateTime.UtcNow;
    private DateTime _lastStallReportUtc = DateTime.MinValue;
    private DispatcherTimer? _uiHeartbeat;
    private System.Threading.Timer? _uiWatchdog;
    /// <summary>本次启动的发起时间，用于判断启动后是否立刻退出。</summary>
    private DateTime _runStartAt = DateTime.MinValue;
    /// <summary>
    /// 「自动-时间」用的连续运行累计秒数（只增不减，直到引擎停止才清零）。
    ///
    /// 为什么不复用 <see cref="_runStartAt"/>：那个字段是版本履历的记账基线，每 30 秒结算一次就
    /// 被前移到"现在"（见 <see cref="AccumulateRunSeconds"/>），所以它表达的是"距上次结算过了几秒"，
    /// 不是"本次运行一共跑了多久" —— 拿它当运行时长，跑 5 小时也只有几十秒。
    /// 因此这里另起一对字段：<see cref="_engRunStartAt"/> 记"最近一次计时起点"（本次运行的起点
    /// 或上一次结算的终点），<see cref="_engRunSeconds"/> 把每次结算的增量累加，才是真正的累计运行时长。
    /// 记进 VersionMemory 的仍是原来的差值，<c>AddRunSeconds</c> 的语义完全不动。
    /// </summary>
    private DateTime _engRunStartAt = DateTime.MinValue;
    private long _engRunSeconds;
    /// <summary>
    /// 「自动-时间」上一次存快照时，本次运行的累计秒数（0 = 本次运行还没存过）。
    /// 引擎每次停止/启动都必须清零 —— 重新启动后要重新从 0 数满 1 小时。
    /// </summary>
    private long _lastTimedSnapshotAtSeconds;
    /// <summary>用户主动停止中，不计入启动异常。</summary>
    private bool _stopping;
    private bool _rollbackPromptOpen;

    /// <summary>引擎输出中出现这些字样即视为本次运行异常，用于「错误日志反复出现」判定。</summary>
    private static readonly string[] FatalPatterns =
    {
        "Failed to load plugins", "Cannot find module", "ERR_MODULE_NOT_FOUND",
        "EADDRINUSE", "Unhandled", "FATAL", "启动超时"
    };

    public MainWindow()
    {
        Logger.Log("MainWindow 构造函数");
        InitializeComponent();
        _liveInstance = this;      // 让 AddEvent 能把新事件实时推进「事件信息」

        string appRoot = Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory) ?? AppContext.BaseDirectory;
        // 1 秒刷新运行时长；端口检测每 2 次 tick（≈2 秒）一次，避免频繁 netstat
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += StatusTimer_Tick;

        Registries.Configure(_settings.Registry);   // 下载来源跟随设置
        // ⑥′ 与 A 处的窗口外接线同一口径：把"份数可不可信"同步下去。这里读 _settings 是**对的** ——
        //     它是本窗口自己的 SettingsManager 实例（构造时已 Load 过），与 App 里那个局部变量各管各的。
        //     少了这一行，B 处的显式赋值会把 A 处算好的信任状态盖回默认值 false（自动档裁剪永久停摆）。
        SnapshotManager.SettingsCache.KeepTrusted = _settings.LastLoadTrusted;
        SnapshotManager.SettingsCache.AutoSnapshotKeep = _settings.AutoSnapshotKeep > 0 ? _settings.AutoSnapshotKeep : 10;
        GuardDialog.AnyOpenChanged += OnDialogOpenChanged;   // 弹窗开着时暂停加载动画
        StartUiHeartbeat();                                  // UI 卡死取证（后台看门狗 + 日志）
        InitTrayIcon();
    }

    // ═══ 宠物动画已移除 ═══
    // 动画素材（Assets\anim\*.gif）与播放库 XamlAnimatedGif 已整体删除：
    // 保留空实现以免调用点散落，后续如再需要动画需重新引入资源与播放器。
    private void SetAnimState(string state) { }

    // ═══ 托盘图标 ═══
    private void InitTrayIcon()
    {
        System.Drawing.Icon trayIcon;
        try
        {
            // 优先从 WPF Resource 经 pack URI 加载。
            var iconUri = new Uri("pack://application:,,,/Assets/whale-girl.ico", UriKind.Absolute);
            var resourceInfo = Application.GetResourceStream(iconUri);
            if (resourceInfo?.Stream != null)
            {
                trayIcon = new System.Drawing.Icon(resourceInfo.Stream);
                Logger.Log("托盘图标: 从 WPF Resource 加载");
            }
            else
            {
                // 图标是内嵌资源，正常取得到；取不到就用系统默认，不再依赖磁盘上的 Assets 目录
                trayIcon = System.Drawing.SystemIcons.Application;
                Logger.Log("托盘图标: 使用系统默认");
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("InitTrayIcon", ex);
            trayIcon = System.Drawing.SystemIcons.Application;
        }

        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = "DSH 守护壳 · " + GuardVersion.Version,
            Visible = false,
            Icon = trayIcon
        };

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("显示窗口", null, (s, e) => ShowFromTray());
        menu.Items.Add("退出", null, (s, e) => { ExitGuardAsync(); });
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (s, e) => ShowFromTray();
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        ShowInTaskbar = true;
        _trayIcon!.Visible = false;
        Activate();
    }

    private void MinimizeToTray()
    {
        CloseFilterPopups();          // 弹层是独立顶层窗口，不关会留在桌面上
        Hide();
        ShowInTaskbar = false;
        _trayIcon!.Visible = true;
        _trayIcon.ShowBalloonTip(1000, "DSH 守护壳", "已最小化到托盘",
            System.Windows.Forms.ToolTipIcon.Info);
    }

    // ═══ 窗口事件 ═══
    /// <summary>启动中正在把设置写入控件，避免把程序自身赋值当成用户修改设置。</summary>
    private bool _hydratingSettings = true;

    /// <summary>毛玻璃是否已启用（供自检与日志使用）。</summary>
    private bool _acrylicOn;

    /// <summary>
    /// 窗口消息：拖动/缩放开始（WM_ENTERSIZEMOVE）时把 Acrylic 退回普通模糊，结束（WM_EXITSIZEMOVE）再恢复。
    /// 这是 Windows Acrylic 的已知缺陷——移动窗口时合成滞后，表现为"发飘"。
    /// </summary>
    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_ENTERSIZEMOVE = 0x0231;
        const int WM_EXITSIZEMOVE = 0x0232;
        try
        {
            if (msg == WM_ENTERSIZEMOVE && _acrylicOn)
            {
                BlurHelper.SuspendForMove(this);
            }
            else if (msg == WM_EXITSIZEMOVE && _acrylicOn)
            {
                BlurHelper.ResumeAfterMove(this, ThemeManager.Tint);
            }
        }
        catch (Exception ex) { Logger.LogError("WindowProc", ex); }
        return IntPtr.Zero;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Logger.Log("MainWindow.Loaded");

        // 筛选弹层是独立顶层窗口：窗口最小化 / 隐藏到托盘时要收起来，否则会留在桌面上。
        // 刻意不挂 Deactivated：Popup 自己能接鼠标，主窗口失活时点选项会被提前收掉，反而打断选择。
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized) CloseFilterPopups();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) CloseFilterPopups();
        };

        // 窗口消息钩子：拖动/缩放期间挂起 Acrylic（Windows 的 Acrylic 移动时会有合成延迟）
        try
        {
            if (PresentationSource.FromVisual(this) is System.Windows.Interop.HwndSource src) src.AddHook(WindowProc);
        }
        catch (Exception ex) { Logger.LogError("AddHook", ex); }

        // 从嵌入资源加载窗口图标与 logo
        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var resNames = asm.GetManifestResourceNames();
            var icoRes = resNames.FirstOrDefault(n => n.EndsWith("whale-girl.ico", StringComparison.OrdinalIgnoreCase));
            if (icoRes != null)
            {
                using var stream = asm.GetManifestResourceStream(icoRes);
                if (stream != null) this.Icon = System.Windows.Media.Imaging.BitmapFrame.Create(stream);
            }

            // 标题栏 logo
            var logoRes = resNames.FirstOrDefault(n => n.EndsWith("dsh-logo.png", StringComparison.OrdinalIgnoreCase));
            if (logoRes != null && LogoImage != null)
            {
                using var stream = asm.GetManifestResourceStream(logoRes);
                if (stream != null)
                {
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.StreamSource = stream;
                    bmp.EndInit();
                    bmp.Freeze();
                    LogoImage.Source = bmp;
                }
            }
        }
        catch (Exception ex) { Logger.LogError("LoadResources", ex); }

        // 分层窗口不支持 DWM Mica，改用 SetWindowCompositionAttribute 的 Acrylic；失败则保持不透明。
        try
        {
            if (!_settings.AcrylicEnabled)
            {
                Logger.LogDiagnosis("[毛玻璃] 设置里关着（用纯色背景），本次不启用");
            }
            else if (BlurHelper.EnableAcrylic(this))
            {
                _acrylicOn = true;
                Logger.LogDiagnosis("[毛玻璃] 已启用（Acrylic + 圆角裁剪）");
                // 尺寸或位置变化后重新裁剪圆角：窗口尺寸固定，但 DPI 变化会改变像素尺寸。
                SizeChanged += (_, _) => BlurHelper.ApplyRoundedRegion(this);
                // Loaded 时 DWM 可能尚未应用区域，稍后补一次。
                var fix = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
                fix.Tick += (_, _) => { fix.Stop(); BlurHelper.ApplyRoundedRegion(this); };
                fix.Start();
            }
            else
            {
                Logger.LogDiagnosis("[毛玻璃] 未启用（系统不支持或没有窗口句柄），按不透明样式显示");
            }

            ApplyTheme(_settings.Theme != 1);
        }
        catch (Exception ex) { Logger.LogError("EnableAcrylic(Loaded)", ex); }

        // 开关轨道色由代码给（模板里的颜色动画会把画刷钉住，映射表会跳过它）
        foreach (var sw in new[] { AutoStartToggle, AutoBrowseToggle, AcrylicToggle })
        {
            sw.Checked += Switch_Changed;
            sw.Unchecked += Switch_Changed;
        }

        AutoStartToggle.IsChecked = _settings.AutoStart;
        AutoBrowseToggle.IsChecked = _settings.AutoBrowseOnReady;
        AcrylicToggle.IsChecked = _settings.AcrylicEnabled;
        _hydratingSettings = false;      // 灌完设置再打开"记录事件"的闸
        _port = _settings.Port;
        PortInput.Text = _port.ToString();

        // 启动时只应用已保存的自定义路径；路径识别与依赖检查由「设置 -> 路径 -> 自动配置」按需触发。
        ApplySavedPaths();

        ShowView(GuardView.Status); // 默认停在状态页
        try
        {
            // 正文只说"有没有、在哪看"：盘符路径上屏违反界面规矩，按既定纪律收进悬停
            //   （同 Logger.KnownNoiseNote 那一类：正文给去处，细节进悬停）。
            string logFile = Logger.CurrentLogFile;
            SessionInfoText.Text = logFile.Length == 0
                ? "当前日志：本次尚未生成（出现异常时会记录，可在「日志」页查看）"
                : "当前日志：已有日志（可在「日志」页查看）";
            SessionInfoText.ToolTip = logFile.Length == 0 ? null : logFile;
        }
        catch { }

        // 从 DSH 会话/其子进程里启动本程序时，引擎就是我们的祖先——这时终止引擎会连带把本程序带走。
        // 提前说一声（真到点终止时还会再问一次「只解绑 / 仍然终止 / 取消」）。
        try
        {
            if (NetworkHelper.IsPortListening(_port) && ProcessManager.IsHostedByPortOwner(_port))
                AddEvent("检测到守护壳由引擎进程启动；引擎退出时可能一并结束守护壳，建议从桌面快捷方式或开始菜单启动", EventKind.Warn);
        }
        catch (Exception ex) { Logger.LogError("hosted hint", ex); }

        _statusTimer.Start();

        // 首次在后台查一次 DSH 版本（发布时间 / 最新版）；失败不影响启动
        _ = RefreshVersionAsync();

        // 版本记忆：若上次会话已连续启动异常，延迟弹一次回退建议
        _ = PromptRollbackSoonAsync();

        // 启动时不抢占或清理端口，与 DSH 引擎彻底解绑。
        // 端口状态仅作为引擎是否运行的观测来源（见 StatusTimer_Tick 的端口同步）。

        SetAnimState("sleep");
        UpdateUI();

        // 开壳流程到这里算正常走完（App.OnStartup 的目录/配置/单实例检查也都过了，否则启动不到这里）
        //   即把本次的「启动-*.log」撤掉。用户要求：启动没问题就不该留日志。
        //   这一次调用覆盖了"只开壳、不点启动引擎"的情况（引擎那一路另有自己的调用点）。
        //   用 IfEngineRunning 而不是 IfHealthy：走到这里就已经是"开壳成功"，若还看 HasFailureEvidence，
        //   开壳期间任何一次 LogError（例如后台查版本/取插件市场消息失败）都会让这份**全是正常过程**的
        //   启动日志留下 —— 正是用户报的"还是会记录一堆正常的启动日志"。
        //   真启动失败（引擎没拉起/没就绪）都调过 MarkStartupFailed，这里的判据同样拒绝删。
        Logger.DiscardStartupLogIfEngineRunning();
    }

    /// <summary>
    /// 拖窗口：优先用系统原生拖动（WM_NCLBUTTONDOWN + HTCAPTION）。
    /// 现场 bug：毛玻璃材质下偶尔拖不动、要试好几次——WPF 的 DragMove() 要求「按下瞬间按键状态没变」，
    /// 合成延迟稍大就静默失败；原生拖动走系统自己的移动循环，稳得多。失败再退回 DragMove()。
    /// </summary>
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => NativeDrag();

    private int _nativeDragCount;
    /// <summary>正在原生拖动：拖动期间 UI 线程被系统移动循环占着，别误记成卡顿。</summary>
    private volatile bool _dragging;
    internal int NativeDragCountForTest => _nativeDragCount;

    private void NativeDrag()
    {
        try
        {
            var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (h != IntPtr.Zero)
            {
                NativeMethods.ReleaseCapture();
                _dragging = true;
                NativeMethods.SendMessage(h, NativeMethods.WM_NCLBUTTONDOWN, NativeMethods.HTCAPTION, IntPtr.Zero);
                _dragging = false;
                _nativeDragCount++;
                return;
            }
        }
        catch (Exception ex) { Logger.LogError("NativeDrag", ex); }
        try { DragMove(); } catch { }      // 兜底：原生调用不成再退回 WPF 的拖法
    }

    /// <summary>最小化到任务栏继续运行，不收进托盘。</summary>
    private void MinimizeButton_Click(object sender, MouseButtonEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    // 最大化用"尺寸法"（无边框 + AllowsTransparency 时 WindowState.Maximized 会盖住任务栏）。
    // _hasRestoreRect = 是否已记下"可还原的原始矩形"：处于全屏它就是 true，
    // 于是再点最大化按钮 = 还原（修复 BUG：以前点按钮回不到原始尺寸与位置）。
    private double _preMaxW, _preMaxH, _preMaxL, _preMaxT;
    private bool _hasRestoreRect;   // 是否已记录可还原矩形

    /// <summary>进入最大化前记录原始矩形，仅记录第一次。</summary>
    internal void SaveRestoreRectIfNeeded()
    {
        if (_hasRestoreRect) return;
        _preMaxW = Width; _preMaxH = Height; _preMaxL = Left; _preMaxT = Top;
        _hasRestoreRect = true;
    }

    /// <summary>还原到已记录的原始尺寸与位置。</summary>
    private void RestoreWindowRect()
    {
        if (!_hasRestoreRect) return;
        Width = _preMaxW; Height = _preMaxH;
        Left = _preMaxL; Top = _preMaxT;
        _hasRestoreRect = false;
        MaxBtnIcon.Text = "\uE922"; // 最大化
        AddEvent("窗口已还原为默认尺寸");
    }

    private void MaximizeButton_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            // 当前处于全屏时，点击为还原；否则改为全屏
            if (_hasRestoreRect)
            {
                RestoreWindowRect();
                return;
            }
            SaveRestoreRectIfNeeded();
            var wa = SystemParameters.WorkArea;
            Width = wa.Width; Height = wa.Height;
            Left = wa.Left; Top = wa.Top;
            MaxBtnIcon.Text = "\uE923"; // 还原
            AddEvent("窗口已最大化");
        }
        catch (Exception ex) { Logger.LogError("MaximizeButton_Click", ex); }
    }

    private void CloseButton_Click(object sender, MouseButtonEventArgs e)
    {
        // 右上角关闭键恒等于收进托盘，不弹窗也不退出；退出走左侧「退出UI」。
        Logger.Log("关闭按钮 → 收进托盘");
        MinimizeToTray();
    }

    /// <summary>
    /// 把这一次引擎运行的时长记进版本履历，返回运行的秒数。
    /// 正常停止、异常提前退出、关闭守护壳都会调用（此前只在异常退出时记，导致履历里始终是 0 秒）。
    ///
    /// 结算与清账本身在 <see cref="SettleRunClock"/> 里（三条停止路径共用一个入口），
    /// 这里只多记一条用户可见的事件。
    /// </summary>
    private int RecordRunDuration()
    {
        try
        {
            int ranSec = SettleRunClock();
            if (ranSec <= 0) return 0;
            AddEvent($"本次运行 {FormatSpan(ranSec)}");
            return ranSec;
        }
        catch (Exception ex) { Logger.LogError("RecordRunDuration", ex); return 0; }
    }

    private void ForceShutdown(bool keepEngine = false)
    {
        _cts?.Cancel();
        // keepEngine=true 时不终止引擎；外部引擎的进程本身也无法终止。
        if (_isRunning && !keepEngine)
        {
            RecordRunDuration();                  // 关壳前先把运行时长记账
            _processManager?.ForceKill();
        }
        else if (_isRunning)
        {
            RecordRunDuration();
        }
        _trayIcon?.Dispose();
        Application.Current.Shutdown();
    }

    private async void MainButton_Click(object sender, MouseButtonEventArgs e)
    {
        Logger.Log($"按钮点击: running={_isRunning} starting={_isStarting}");
        if (_isStarting) return;
        if (_isRunning) await TerminateEngineAsync(interactive: true);
        else
        {
            _startupRetried = false;      // 用户手动再点一次时，重新给一次自动重试机会
            _specOverride = "";          // 同理：临时版本说明符作废（回到用户/版本记忆的选择）
            _junctionsCleaned = false;    // 清理也重跑一遍（用户可能刚手工改过环境）
            await StartEngineAsync();
        }
    }

    // ══════════════ 终止引擎 / 退出守护壳 ══════════════

    /// <summary>
    /// 「终止引擎」真正的落地动作：按端口找监听者，只结束监听者本身（从不带 /T），
    /// 先温和后强制；到点仍占着端口就走强杀档（同样只杀监听者，托管场景绝不整树终止）。
    /// 引擎正托管本程序时先弹「只解绑 / 仍然终止 / 取消」。只停引擎，不退出守护壳
    /// （退出走关闭键 / 「退出UI」，那里另有红/橙/灰三策略）。
    /// </summary>
    private async Task<bool> TerminateEngineAsync(bool interactive)
    {
        try
        {
            bool listening = NetworkHelper.IsPortListening(_port);
            if (!_isRunning && !listening)
            {
                AddEvent("引擎当前未在运行");
                RefreshStatusEvents();
                return true;
            }

            // ⓪ 托管判定：引擎正托管着本程序时，先问用户——绝不静默自杀，也不静默放过。
            // （第 39 批重写两档终止时把这一步漏了，只留下自检样张，「关引擎连带关壳」才会反复复发。）
            bool hosted = ProcessManager.IsHostedByPortOwner(_port);
            if (ProcessManager.PlanTerminate(hosted, ProcessManager.IsSecondTier(_terminateAttempted, listening))
                == ProcessManager.TerminateRoute.AskHosted)
            {
                // 非交互调用（来自「退出UI」）说明用户已经选过策略：不再问第二遍，
                // 只结束引擎本身、绝不做整树终止，剩下的交给调用方。
                if (!interactive)
                {
                    await Task.Run(() => ProcessManager.KillPortOwners(_port, out _));
                    AddEvent("引擎正托管着守护壳：只结束引擎本身，未做整树终止", EventKind.Warn);
                    RefreshStatusEvents();
                    return true;
                }

                var pick = GuardDialog.ShowCustom(
                    "这个引擎正在托管守护壳本身。\n\n" +
                    "· 只解绑：引擎继续在后台跑，守护壳显示为空闲\n" +
                    "· 仍然终止：只结束引擎，守护壳随后一起退出",
                    "终止引擎", MessageBoxImage.Question,
                    new GuardDialog.DialogButton("只解绑", MessageBoxResult.No, Color.FromRgb(0xFF, 0x9F, 0x0A), IsDefault: true),
                    new GuardDialog.DialogButton("仍然终止", MessageBoxResult.Yes, Color.FromRgb(0xFF, 0x3B, 0x30)),
                    new GuardDialog.DialogButton("取消", MessageBoxResult.Cancel, Color.FromRgb(0x8E, 0x8E, 0x93), IsCancel: true));

                if (pick == MessageBoxResult.Cancel) return false;

                if (pick == MessageBoxResult.No)      // 只解绑：什么都不杀
                {
                    _isRunning = false;
                    _engineExternal = false;
                    _unbound = true;                  // 状态计时器别再把它翻回"运行中"
                    SetProgress("");
                    AddEvent("已解绑：引擎继续在后台跑，守护壳不再接管它", EventKind.Warn);
                    RefreshStatusEvents();
                    UpdateUI();
                    return false;
                }

                // 仍然终止：只杀监听者本身（不做整树终止，避免把本程序的祖先链一起带走），随后干净退出
                SetProgress("正在终止引擎...");
                await Task.Run(() => ProcessManager.KillPortOwners(_port, out _));
                AddEvent("已结束引擎；守护壳随之退出", EventKind.Warn);
                RefreshStatusEvents();
                await Task.Delay(400);
                Application.Current?.Shutdown();
                return true;
            }

            // ② 第二档：上次未停止、端口仍被占用时，先询问用户再强杀全部相关进程（避免反复无果）
            if (ProcessManager.IsSecondTier(_terminateAttempted, listening))
            {
                var ask = GuardDialog.Show(
                    $"上次未能将引擎停止（端口 {_port} 仍被占用）。\n\n" +
                    "要强行终止所有相关进程并强制关闭端口吗？\n\n" +
                    "· 会结束占用该端口的进程及其全部子进程\n" +
                    "· 不会动守护壳自身与其父进程；正在进行的任务与对话会被中断",
                    "强行终止", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (ask != MessageBoxResult.Yes)
                {
                    AddEvent("已取消强杀（引擎仍在运行）");
                    RefreshStatusEvents();
                    return false;
                }

                SetProgress("正在强行终止相关进程...");
                var (ok2, d2) = await Task.Run(() =>
                {
                    bool k = ProcessManager.ForceClosePort(_port, 10, out string d);
                    return (k, d);
                });

                _isRunning = false;
                _engineExternal = false;
                _readyUrl = "";
                // 强杀成功，即引擎确实已停止，本次运行到此结束：把尾段结算进履历再清账本。
                // 失败（端口仍被占用）时引擎可能还在跑，账本必须保持开着，事后由状态计时器接着记。
                if (ok2) SettleRunClock();
                _terminateAttempted = !ok2;
                SetProgress("");
                Logger.Log($"强行终止：ok={ok2} {d2}");
                // d2 来自 ProcessManager.ForceClosePort 的 detail，只有"异常兜底那一格"带英文原文：
                //   · "强关失败：" + ex.Message（ProcessManager.cs:909）；
                //   · 或经 ForceKillTreeExceptSelf 夹带进来的 "强杀失败：" + ex.Message（ProcessManager.cs:956）——
                //     这一支**成功分支也会命中**：某棵子树杀失败、端口随后仍释放了，ok2 就是 true，
                //     notes 里照样躺着英文原文（ProcessManager.cs:870/896 直接把它拼进 notes）。
                // 其余分支（"已强杀 N 个进程（PID …）" / "没有可强杀的进程"）全是中文。
                // 故按明细本身分档：带原文的那一档，上屏只留中文结论、原文改走日志。
                bool d2CarriesRaw = d2.Contains("失败：", StringComparison.Ordinal);
                // 原文必须真落盘：Logger.Log 是空实现，这条目前没有任何落点。
                // 用户操作类失败 ⇒ NoteDiagnosis（[WARN]，不置失败标记、不弹窗），
                // 不用会置失败标记的 LogError；全中文的明细（成功或"没有可强杀的进程"）
                // 依旧不写日志 —— 成功路径不留日志是既定策略，这里不破例。
                if (!ok2 || d2CarriesRaw)
                    Logger.NoteDiagnosis($"强行终止：ok={ok2} 端口 {_port}（{d2}）");
                // 事件栏：结论（中文前缀）与级别一字不动（成功=橙 / 失败=红）；
                // 明细干净就照旧带上（PID 明细是有用信息，不无故丢），
                // 带原文时换成去处指引 —— 原文已由上面那句 NoteDiagnosis 真落盘
                // （日志目录不可写时 LogPromise 会如实补一句，不空许诺）。
                AddEvent(ok2
                        ? (d2CarriesRaw ? "已强行终止引擎；" + LogPromise("详细原因已记入日志，可在「日志」页查看。")
                                        : $"已强行终止引擎（{d2}）")
                        : (d2CarriesRaw ? $"强杀后端口 {_port} 仍被占用；" + LogPromise("详细原因已记入日志，可在「日志」页查看。")
                                        : $"强杀后端口 {_port} 仍被占用（{d2}）"),
                     ok2 ? EventKind.Warn : EventKind.Bad);
                RefreshStatusEvents();
                UpdateUI();
                return ok2;
            }

            // ① 第一档：先断端口，最多等 30 秒，仍不响应就强杀整棵树，再复检 10 秒（全程有界）
            _stopping = true;
            SetProgress("正在终止引擎...");
            bool ok, forced;
            string detail;
            try
            {
                (ok, detail, forced) = await Task.Run(() =>
                {
                    bool k = ProcessManager.TerminateEngineBounded(_port, 30, 10, out string d, out bool f);
                    return (k, d, f);
                });
            }
            finally { _stopping = false; }

            // 没停掉时不得虚报"未运行"：否则两秒后状态计时器会把仍在监听的引擎改判成
            // "运行中（外部引擎）"，之后退出不接管、重启提示也走错分支。
            if (ok)
            {
                _isRunning = false;
                _engineExternal = false;
                _readyUrl = "";                 // 引擎已停：token 地址作废
                // 端口已空闲，即本次运行结束：先把尾段（距上次 30 秒结算的零头）结算进履历，再清账本。
                // 少了这一步，_runStartAt 会残留，下一个 30 秒结算点就会把"停机期"当成运行时长记进履历。
                SettleRunClock();
            }
            _terminateAttempted = !ok;          // 未停止时，下次点击走第二档
            SetProgress("");
            Logger.Log($"终止引擎：ok={ok} 强杀={forced} {detail}");
            // detail 来自 ProcessManager.TerminateEngineBounded：多数情形是中文
            //   （"已终止：PID 123（node）" / "端口 3080 仍被占用（已等 30 秒 + 复检 10 秒）：…"），
            // 只有"异常兜底那一格"带英文原文，且它两条支路里都可能夹带：
            //   · first  = KillPortOwners 的 detail ⇒ "终止失败：" + ex.Message（ProcessManager.cs:752）；
            //   · notes  = ForceKillTreeExceptSelf 的 detail ⇒ "强杀失败：" + ex.Message（ProcessManager.cs:956）。
            // ⚠ 这两支**成功分支同样可能命中**：第一档抛异常但随后端口释放（ProcessManager.cs:810，ok=true、forced=false），
            //   或超时强杀时某棵子树杀失败、端口却仍释放了（ProcessManager.cs:842，forced=true）。
            // 故按明细本身分档，而不是只看 ok：带原文的那一档，上屏只留中文结论、原文改走日志。
            bool detailCarriesRaw = detail.Contains("失败：", StringComparison.Ordinal);
            // 原文必须真落盘：Logger.Log 是空实现，这条目前没有任何落点。
            // 用户操作类失败 ⇒ NoteDiagnosis（[WARN]，不置失败标记、不弹窗），不用会置失败标记的 LogError；
            // 全中文的明细（如 "已终止：PID 123（node）"）依旧不写 —— 成功路径不留日志是既定策略。
            if (!ok || detailCarriesRaw)
                Logger.NoteDiagnosis($"终止引擎：ok={ok} 强杀={forced} {detail}");
            // 事件栏：结论（中文前缀）与级别一字不动（正常=蓝 / 强杀=橙 / 失败=红）；
            // 明细干净就照旧带上（谁被终止了是有用信息），带原文时换成去处指引 ——
            // 原文已由上面那句 NoteDiagnosis 真落盘（日志目录不可写时 LogPromise 会如实补一句，不空许诺）。
            AddEvent(ok
                        ? (forced
                            ? (detailCarriesRaw ? "已终止引擎（等待超时后强杀）；" + LogPromise("详细原因已记入日志，可在「日志」页查看。")
                                                : $"已终止引擎（等待超时后强杀：{detail}）")
                            : (detailCarriesRaw ? "已终止 DSH 引擎；" + LogPromise("详细原因已记入日志，可在「日志」页查看。")
                                                : $"已终止 DSH 引擎（{detail}）"))
                        : (detailCarriesRaw ? "终止引擎未完成——再点一次可强行终止；" + LogPromise("详细原因已记入日志，可在「日志」页查看。")
                                            : $"终止引擎未完成（{detail}）——再点一次可强行终止"),
                     ok ? (forced ? EventKind.Warn : EventKind.Info) : EventKind.Bad);
            RefreshStatusEvents();
            UpdateUI();
            return ok;
        }
        catch (Exception ex)
        {
            Logger.LogError("TerminateEngineAsync", ex);
            SetProgress("");
            return false;
        }
    }

    /// <summary>
    /// 「退出UI」的出口：只退出守护壳，不动 DSH 引擎（固定这条策略的常量，自检会核对）。
    ///
    /// 为什么不再问"要不要顺带停引擎"（2026-09-13 现场反馈）：
    ///   · 引擎本来就不是本程序启动的，可能正托管着别的会话，停它属于破坏性操作；
    ///   · 右上角已经有专门的「终止引擎」按钮，想停引擎的人自然会去点；
    ///   · 在这个框里再问一次，等于把"退出界面"和"中断任务"混成一件事，纯属添乱。
    /// </summary>
    internal const bool ExitUiKeepsEngineRunning = true;

    /// <summary>
    /// 退出守护壳的唯一出口（托盘「退出」、「退出UI」按钮、应用内更新交接完成后各调用一次）。
    ///
    /// <para><b>为什么要有 <paramref name="skipPluginWorkCheck"/></b>：三条调用路径里，<b>应用内更新</b>
    /// 那条是<b>程序自己发起的退出</b> —— 用户已经在更新进度窗上按过确认（"立即更新并退出"），
    /// 此刻再弹一次"插件操作进行中，确定要退出吗"纯属重复询问；何况那一轮的进度窗刚刚被收掉
    /// （<c>GuardUpdateNowAsync</c> 里的 <c>FinishGuardUpdate</c>），没有任何东西可供用户"等它跑完"。
    /// 托盘与「退出UI」是用户当场点的退出，照旧拦一下 —— <b>默认 false，既有行为一个字节不变</b>。</para>
    ///
    /// <para>⚠️ 这个开关<b>只跳过"要不要问"</b>，不跳过任何收尾：两条路最终都走同一个
    /// <c>ForceShutdown(keepEngine: true)</c>。载荷上它也退得出去 —— 本程序刚因为"锁窗"被批评过，
    /// 这里的确认框永远只是确认、不是闸门（点「确定」照样退）。</para>
    /// </summary>
    /// <param name="skipPluginWorkCheck">true = 本次退出由程序自己发起（应用内更新已获用户确认），不再问"插件操作进行中"。</param>
    private void ExitGuardAsync(bool skipPluginWorkCheck = false)
    {
        try
        {
            // ⚠️ 插件正在变动时先拦一下（2026-09-20 现场反馈：更新/安装/卸载跑到一半点了退出，
            //   进度就再也看不见了，用户不知道到底做完没有）。
            //   · 判据只有一份：PluginWorkInProgressFor（MainWindow.Tools.cs），这里只负责把四个忙标志
            //     现读现传 —— 不在这里另写"哪个标志算忙"的第二份判断。
            //   · 四个字段全是同 class 的 private，同 partial 可直接读；市场那两个在 MainWindow.Market.cs
            //     （_marketBusy / _installing）。
            //   · ⚠️ _batchBusy **不进判据**（2026-09-20 复核）：它一个标志盖住批量四种动作，其中
            //     禁用 / 启用**只写一次插件配置、一条命令都不跑**，那一刻退出不存在"跑了一半"的中间态，
            //     拿它当判据会让"禁用两个插件时点退出"也弹"可能只完成了一半"——不准确。而真会留半截的
            //     批量更新 / 批量卸载本身成对占着 _pluginWriteBusy（Batch.cs：_batchBusy = true;
            //     紧接着 BeginPluginWriteState();），去掉 batchBusy 不会漏掉任何一条路径。
            //   · ⚠️ 这里**只提示、不拦死**：点「确定」照样往下走 ForceShutdown。本程序刚因为"锁窗"
            //     被批评过，退出按钮必须永远退得出去（点「取消」只是不退出，不是被锁住）。
            //   · 先把忙标志读进局部量：下面会弹模态框，弹框期间用户没有任何入口去改这些标志
            //     （插件按钮都在主窗、被模态框挡住），所以读一次即准，不存在"读完又变了"。
            bool pluginWrite = _pluginWriteBusy;
            bool updating = _updatingBusy;
            bool marketBusy = _marketBusy;
            bool installing = _installing;
            if (!skipPluginWorkCheck
                && PluginWorkInProgressFor(pluginWrite, updating, marketBusy, installing))
            {
                Logger.NoteDiagnosis(
                    $"退出UI：插件变动进行中（批量写闸={pluginWrite} 更新={updating} 市场={marketBusy} "
                    + $"安装={installing}）⇒ 已弹窗确认");
                var choice = GuardDialog.Show(
                    "插件操作正在进行中。\n\n"
                    + "现在退出，这一轮操作会被中断，可能只完成了一半（插件目录或依赖记录里可能留下未完成的中间状态），"
                    + "底部进度也会随之消失，之后再打开本程序不会自动接着做。\n\n"
                    + "建议点「取消」回到主界面，等这一轮操作跑完再退出。\n"
                    + "确实要现在退出，请点「确定」。",
                    "插件操作进行中",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning);
                if (choice != MessageBoxResult.OK) return;      // 点「取消」：不退出，回主界面
                AddEvent("插件操作尚未结束，已按你的选择退出守护壳", EventKind.Warn);
            }

            bool engineUp = _isRunning || NetworkHelper.IsPortListening(_port);
            Logger.Log($"退出UI：只退出守护壳（引擎{(engineUp ? "保持运行" : "未运行")}）");
            if (engineUp) AddEvent("正在退出守护壳（DSH 引擎保持运行）");
            ForceShutdown(keepEngine: true);
        }
        catch (Exception ex)
        {
            Logger.LogError("ExitGuardAsync", ex);
            ForceShutdown(keepEngine: true);
        }
    }

    private void CancelStart_Click(object sender, MouseButtonEventArgs e)
    {
        Logger.Log("取消启动");
        _cts?.Cancel();

        // 取消要"真取消"：这次已经拉起的 cmd/npx 树必须收掉（有界、跳过自身与祖先）。
        // 否则它会在后台继续把引擎启动起来，随后被状态计时器当成"外部引擎"——本程序就再也无法管理它，
        // 「终止引擎」「重启引擎」全会走错分支。
        int pid = _processManager?.TrackedPid ?? 0;
        if (pid > 0)
            _ = Task.Run(() => ProcessManager.ForceKillTreeExceptSelf(pid, out _));

        SetProgress("");
        _isStarting = false;
        AddEvent("已取消启动，正在收掉这次拉起的进程", EventKind.Warn);
        RefreshStatusEvents();
        UpdateUI();
    }

    /// <summary>
    /// 加载进度条的两态配色。日间不能沿用夜间的高饱和红与近白文字（浅底白字看不清），
    /// 故：日间 = 浅中性底 + 深色字 + 半透明填充/悬停红；夜间保持原样。
    /// </summary>
    private void ApplyLoadingPalette()
    {
        try
        {
            bool dark = ThemeManager.IsDark;
            if (LoadingButtonPanel != null && !_loadingHover)
            {
                _loadingIdleBg = new SolidColorBrush(dark
                    ? Color.FromRgb(0x3A, 0x3A, 0x3C) : Color.FromRgb(0xE3, 0xE4, 0xE9));
                LoadingButtonPanel.Background = _loadingIdleBg;
                LoadingButtonPanel.Opacity = 1.0;
            }
            if (LoadingFill != null)
                LoadingFill.Background = new SolidColorBrush(dark
                    ? Color.FromRgb(0x34, 0xC7, 0x59)                 // 夜间：实心绿
                    : Color.FromArgb(0x99, 0x34, 0xC7, 0x59));        // 日间：半透明绿，深字可读
            if (LoadingGloss != null)
                LoadingGloss.Visibility = dark ? Visibility.Visible : Visibility.Collapsed;
            if (LoadingText != null && !_loadingHover)
                LoadingText.Foreground = new SolidColorBrush(dark
                    ? Colors.White : Color.FromRgb(0x1C, 0x1C, 0x1E));
        }
        catch (Exception ex) { Logger.LogError("ApplyLoadingPalette", ex); }
    }

    private void LoadingBtn_MouseEnter(object sender, MouseEventArgs e)
    {
        // 悬停：铺红底并将填充条淡出，显示「停止加载」
        _loadingHover = true;
        bool dark = ThemeManager.IsDark;
        if (LoadingButtonPanel != null)
        {
            _loadingIdleBg ??= LoadingButtonPanel.Background;      // 记住当前主题下的静息底色
            LoadingButtonPanel.Background = new SolidColorBrush(dark
                ? Color.FromArgb(0xC0, 0xFF, 0x3B, 0x30)           // 夜间：高饱和红
                : Color.FromArgb(0x66, 0xFF, 0x3B, 0x30));         // 日间：柔和红，浅底上不刺眼
            LoadingButtonPanel.Opacity = dark ? 0.75 : 1.0;
        }
        if (LoadingFill != null) LoadingFill.Opacity = 0;
        if (LoadingText != null)
        {
            LoadingText.Text = "停止加载";
            LoadingText.Foreground = dark
                ? Brushes.White
                : new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E));
        }
    }

    private void LoadingBtn_MouseLeave(object sender, MouseEventArgs e)
    {
        // 离开：还原悬停前的底色（不能写死颜色：日间模式下那会让进度条变黑）
        _loadingHover = false;
        if (LoadingButtonPanel != null)
        {
            LoadingButtonPanel.Background = _loadingIdleBg ?? new SolidColorBrush(
                ThemeManager.IsDark ? Color.FromRgb(0x3A, 0x3A, 0x3C) : Color.FromRgb(0xE3, 0xE4, 0xE9));
            LoadingButtonPanel.Opacity = 1.0;
        }
        if (LoadingFill != null) LoadingFill.Opacity = 1;
        if (LoadingText != null)
        {
            LoadingText.Text = _loadingShown >= 1 ? $"正在加载... {_loadingShown:0}%" : "正在加载...";
            ApplyLoadingPalette();
        }
    }

    // ═══ 右上角三个窗口键（仿 iOS 交通灯）═══
    private void TrafficBtn_MouseEnter(object sender, MouseEventArgs e) => AnimateTraffic(sender as Border, true);
    private void TrafficBtn_MouseLeave(object sender, MouseEventArgs e) => AnimateTraffic(sender as Border, false);

    /// <summary>悬停时圆点提亮、放大并淡入图标，离开时回落到静息态。</summary>
    private static void AnimateTraffic(Border? host, bool hover)
    {
        if (host?.Child is not Grid g) return;
        var dot = g.Children.OfType<Border>().FirstOrDefault();
        var glyph = g.Children.OfType<TextBlock>().FirstOrDefault();
        string tag = host.Tag as string ?? "";

        Color idle = tag switch
        {
            "close" => Color.FromRgb(0xB3, 0x3A, 0x34),
            "max" => Color.FromRgb(0x1E, 0x7B, 0x37),
            _ => Color.FromRgb(0xB3, 0x81, 0x2A),
        };
        Color vivid = tag switch
        {
            "close" => Color.FromRgb(0xFF, 0x5F, 0x57),
            "max" => Color.FromRgb(0x28, 0xC8, 0x40),
            _ => Color.FromRgb(0xFF, 0xBD, 0x2E),
        };

        if (dot != null)
        {
            // 必须替换为新建的画刷：XAML 中的 Transparent 等值取到的是已冻结的内置画刷，
            // 对其调用 BeginAnimation 会抛异常。
            var brush = new SolidColorBrush((dot.Background as SolidColorBrush)?.Color ?? idle);
            dot.Background = brush;
            brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation
            {
                To = hover ? vivid : idle,
                Duration = TimeSpan.FromMilliseconds(150),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });

            if (dot.RenderTransform is ScaleTransform st)
            {
                var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 };
                var dur = TimeSpan.FromMilliseconds(hover ? 190 : 130);
                double to = hover ? 1.22 : 1.0;
                st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(to, dur) { EasingFunction = ease });
                st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(to, dur) { EasingFunction = ease });
            }
        }

        if (glyph != null)
            glyph.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(hover ? 1.0 : 0.0, TimeSpan.FromMilliseconds(hover ? 120 : 90)));
    }

    private void AutoStart_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _hydratingSettings) return;
        _settings.AutoStart = AutoStartToggle.IsChecked == true;
        _settings.Save();
        RegistryHelper.SetAutoStart(_settings.AutoStart, GetExePath());
        // 先取结果再报：设置没写进盘时，重启后这个开关会回到原样
        var autoStartOutcome = SettingsOutcome(_settings.AutoStart ? "设置：已开启开机自启" : "设置：已关闭开机自启");
        AddEvent(autoStartOutcome.Text, autoStartOutcome.Kind);
    }

    private void AutoBrowse_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _hydratingSettings) return;
        _settings.AutoBrowseOnReady = AutoBrowseToggle.IsChecked == true;
        _settings.Save();
        // 同上：没写进盘就不能只说"已开启/已关闭"
        var autoBrowseOutcome = SettingsOutcome(_settings.AutoBrowseOnReady ? "设置：引擎就绪后自动打开浏览器" : "设置：已关闭自动打开浏览器");
        AddEvent(autoBrowseOutcome.Text, autoBrowseOutcome.Kind);
    }

    /// <summary>毛玻璃开关：立刻生效（不用重启），老机器关掉更跟手。</summary>
    private void Acrylic_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _hydratingSettings) return;
        try
        {
            _settings.AcrylicEnabled = AcrylicToggle.IsChecked == true;
            _settings.Save();
            // 这一个开关的两条原有事件各自的分档（不支持毛玻璃=Warn / 关闭=Info）保持不变，
            // 写盘失败另补一条：重启后开关会回到原样，不能让上面那句话独自成立。
            bool acrylicSaved = !_settings.LastSaveFailed;

            if (_settings.AcrylicEnabled)
            {
                _acrylicOn = BlurHelper.EnableAcrylic(this);
            ForceRecompose("开毛玻璃");
                if (_acrylicOn)
                {
                    BlurHelper.ApplyRoundedRegion(this);
                    var t = ThemeManager.Tint;
                    BlurHelper.SetTint(this, t.alpha, t.r, t.g, t.b);
                }
                AddEvent(_acrylicOn ? "设置：已开启毛玻璃背景" : "设置：这台机器不支持毛玻璃，保持纯色背景", EventKind.Warn);
            }
            else
            {
                BlurHelper.DisableAcrylic(this);
            ForceRecompose("关毛玻璃");
                _acrylicOn = false;
                AddEvent("设置：已关闭毛玻璃背景（纯色，更流畅）");
            }
            if (!acrylicSaved) AddEvent(SettingsFailText, EventKind.Warn);
            ApplyTheme(ThemeManager.IsDark);
        }
        catch (Exception ex) { Logger.LogError("Acrylic_Changed", ex); }
    }

    private void SettingsButton_Click(object sender, MouseButtonEventArgs e)
    {
        // 从其他页面点「设置」时回到「常规」；已在设置页时保持当前二级标签。
        if (_currentView != GuardView.Settings) _settingsTab = SettingsTab.General;
        ShowView(GuardView.Settings);
        Logger.Log("打开设置视图");
    }

    private void PortSetting_Click(object sender, MouseButtonEventArgs e)
    {
        ShowView(GuardView.Settings);
        PortInput.Focus();
        PortInput.SelectAll();
        Logger.Log("打开端口设置");
    }

    private void StatusButton_Click(object sender, MouseButtonEventArgs e) => ShowView(GuardView.Status);

    private void SnapshotButton_Click(object sender, MouseButtonEventArgs e) => ShowView(GuardView.Snapshots);

    // ═══ 侧边菜单事件 ═══
    private void MenuBtn_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Border b && !ReferenceEquals(b, _currentNavBorder))
            // 夜间用白色浮层、日间用黑色浮层；固定用白色在浅色底上没有可见反馈。
            b.Background = new SolidColorBrush(ThemeManager.IsDark
                ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0x1E, 0x00, 0x00, 0x00));
    }

    private void MenuBtn_MouseLeave(object sender, MouseEventArgs e)
    {
        // 选中的导航项保持高亮，其余恢复透明。
        if (sender is Border b && !ReferenceEquals(b, _currentNavBorder))
            b.Background = Brushes.Transparent;
    }

    /// <summary>左侧「日志」：打开应用内日志视图。</summary>
    private void LogButton_Click(object sender, MouseButtonEventArgs e)
    {
        ShowView(GuardView.Logs);
        Logger.Log("打开日志视图");
    }

    /// <summary>导航「说明」：打开应用内说明页。</summary>
    private void AboutButton_Click(object sender, MouseButtonEventArgs e) => ShowView(GuardView.About);

    private void ExitUIButton_Click(object sender, MouseButtonEventArgs e)
    {
        Logger.Log("退出UI");
        ExitGuardAsync();
    }

    // ═══ 端口设置 ═══
    private int _port = 3080;

    private void PortInput_LostFocus(object sender, RoutedEventArgs e)
    {
        ApplyPort();
    }

    private void PortInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyPort();
            e.Handled = true;
        }
    }

    private void ApplyPort()
    {
        if (int.TryParse(PortInput.Text.Trim(), out int port) && port > 0 && port <= 65535)
        {
            if (_port != port)
            {
                int old = _port;
                _settings.Port = port;
                _settings.Save();

                // 引擎在跑（或正在起）时只写进设置、不改当前观测端口：否则状态计时器会以为
                // "引擎没了"（误记一次异常退出）、「终止引擎」会打错端口变成假成功、下次启动还会
                // 在端口不一致的情况下再拉起一个引擎。新端口在下次启动时生效。
                bool busy = _isRunning || _isStarting || NetworkHelper.IsPortListening(_port);
                if (busy)
                {
                    // 端口本身是运行中改的（重启后才生效），但"写没写进设置"仍要如实说
                    var portBusyOutcome = SettingsOutcome($"设置：端口 {old} → {port}（引擎运行中，重启后生效）");
                    AddEvent(portBusyOutcome.Text, portBusyOutcome.Kind);
                    Logger.Log($"端口已写入设置 {port}（运行中，暂不改观测端口 {old}）");
                    return;
                }

                _port = port;
                Logger.Log($"端口已更改为 {port}");
                PortText.Text = $"http://127.0.0.1:{port}";
                var portOutcome = SettingsOutcome($"设置：端口 {old} → {port}");
                AddEvent(portOutcome.Text, portOutcome.Kind);
            }
        }
        else
        {
            PortInput.Text = _port.ToString();
        }
    }

    private void OpenBrowser_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            // 引擎在跑：走完整流程（等到页面非 404 再开），不要直接下发一个可能还是 404 的地址给浏览器
            if (NetworkHelper.IsPortListening(_port))
            {
                _ = OpenEnginePageAsync();
                return;
            }
            // 引擎没在跑：只能尽力打开（历史地址或首页），失败会记日志
            string url = _readyUrl.Length > 0 ? _readyUrl : $"http://127.0.0.1:{_port}/";
            NetworkHelper.OpenBrowser(url);
        }
        catch (Exception ex) { Logger.LogError("OpenBrowser", ex); }
    }

    // ═══════════════════════════════════════════════════════════
    //  启动进度条：圆角裁剪 + 16ms 时间基准缓动 + 流光扫过
    //  · 目标值只前进不后退（阶段里程碑 / 等待时长的渐近映射，见 StartupProgress）；
    //  · 显示值按真实 dt 缓动逼近目标（τ≈100ms，单帧最多 12%，不会一帧跳到位）；
    //  · 即使引擎卡住不动，也按对数曲线慢慢爬到 99%；100% 只由完成信号给出。
    // ═══════════════════════════════════════════════════════════
    private const double LoadingEaseTauMs = 100;      // 缓动时间常数（毫秒）
    private const double LoadingMaxStepPerTick = 12;  // 单帧最大步进（%）
    private const double LoadingSnapGap = 0.4;        // 离目标这么近就直接贴上（消掉长尾）
    private DispatcherTimer? _loadingTimer;
    private readonly System.Diagnostics.Stopwatch _loadingClock = new();
    private double _loadingLastTickMs;
    private double _loadingClockOffset;   // 自检用：把时钟往未来拨，验证「卡住也在往上爬」
    private double _loadingTarget;
    private double _loadingShown;
    private bool _loadingActive;   // 正在加载（代码表运行中）
    private bool _loadingHover;    // 鼠标悬停（显示「停止加载」，进度条淡出）
    private Brush? _loadingIdleBg; // 悬停前的进度条底色（退出悬停时原样还原，避免写死颜色）

    private void LoadingButtonPanel_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyLoadingClip();

    /// <summary>把进度条与填充块裁成与容器一致的圆角，避免溢出方形边角。</summary>
    private void ApplyLoadingClip()
    {
        try
        {
            if (LoadingButtonPanel == null) return;
            double w = LoadingButtonPanel.ActualWidth, h = LoadingButtonPanel.ActualHeight;
            if (w <= 0 || h <= 0) return;

            double r = Math.Min(10, Math.Min(w, h) / 2);
            LoadingButtonPanel.Clip = new RectangleGeometry(new Rect(0, 0, w, h), r, r);

            if (LoadingFill != null)
            {
                double fw = LoadingFill.ActualWidth > 0 ? LoadingFill.ActualWidth : LoadingFill.Width;
                if (fw > 0)
                    LoadingFill.Clip = new RectangleGeometry(new Rect(0, 0, fw, h), r, r);
            }
        }
        catch (Exception ex) { Logger.LogError("ApplyLoadingClip", ex); }
    }

    private void StartLoadingAnimation()
    {
        try
        {
            bool first = !_loadingActive;
            _loadingActive = true;
            if (first)
            {
                _loadingClock.Restart();      // 时间基准只在「空闲 -> 加载」时归零，续接时不能倒回去
                _loadingLastTickMs = 0;
                _loadingClockOffset = 0;
            }
            if (_loadingTimer == null)
            {
                _loadingTimer = new DispatcherTimer(DispatcherPriority.Render)
                {
                    Interval = TimeSpan.FromMilliseconds(16)
                };
                _loadingTimer.Tick += LoadingTimer_Tick;
            }
            if (!_loadingTimer.IsEnabled) _loadingTimer.Start();
            StartGloss();
            ApplyLoadingClip();
        }
        catch (Exception ex) { Logger.LogError("StartLoadingAnimation", ex); }
    }

    private void StopLoadingAnimation(bool reset)
    {
        try
        {
            _loadingActive = false;
            _loadingTimer?.Stop();
            StopGloss();
            if (!reset) return;
            _loadingTarget = 0;
            _loadingShown = 0;
            _loadingClock.Reset();
            _loadingLastTickMs = 0;
            _loadingClockOffset = 0;
            if (LoadingFill != null) LoadingFill.Width = 0;
            ApplyLoadingClip();
        }
        catch (Exception ex) { Logger.LogError("StopLoadingAnimation", ex); }
    }

    /// <summary>加载动画的当前时刻（秒）。自检可把时钟往未来拨，验证长时间卡住时的蠕行。</summary>
    private double LoadingSeconds => _loadingClock.Elapsed.TotalSeconds + _loadingClockOffset;

    private void LoadingTimer_Tick(object? sender, EventArgs e)
    {
        try
        {
            double nowMs = LoadingSeconds * 1000.0;
            double dt = Math.Max(8, Math.Min(80, nowMs - _loadingLastTickMs));   // 夹住帧间隔：掉帧也不允许跳变
            _loadingLastTickMs = nowMs;

            // ① 时间基准缓动逼近真实目标（比按帧累加稳：计时抖动不会忽快忽慢）
            double diff = _loadingTarget - _loadingShown;
            if (diff > 0.05)
            {
                double step = diff * (1 - Math.Exp(-dt / LoadingEaseTauMs));
                if (step > LoadingMaxStepPerTick) step = LoadingMaxStepPerTick;
                _loadingShown += step;
            }
            else if (diff > -LoadingSnapGap)
            {
                _loadingShown = _loadingTarget;      // 收尾：贴上去，不留长尾
            }

            // ② 卡住也在爬：显示值不得低于对数蠕行值（上限 99%；完成信号 100% 不受此限）
            if (_loadingActive && _loadingTarget < StartupProgress.Done)
            {
                double creep = Math.Min(StartupProgress.Creep(LoadingSeconds), StartupProgress.CreepCap);
                if (creep > _loadingShown) _loadingShown = creep;
            }

            _loadingShown = Math.Max(0, Math.Min(100, _loadingShown));
            ApplyLoadingWidth();
        }
        catch (Exception ex) { Logger.LogError("LoadingTimer_Tick", ex); }
    }

    private void ApplyLoadingWidth()
    {
        if (LoadingFill == null || LoadingButtonPanel == null) return;
        double w = LoadingButtonPanel.ActualWidth > 0 ? LoadingButtonPanel.ActualWidth : 400;
        LoadingFill.Width = Math.Max(0, w * _loadingShown / 100.0);
        ApplyLoadingClip();
        if (!_loadingHover && LoadingText != null)
            LoadingText.Text = _loadingShown >= 1 ? $"正在加载... {_loadingShown:0}%" : "正在加载...";
    }

    /// <summary>自检/截图用：直接进入加载态并停在指定百分比。</summary>
    internal void ShowLoadingForTest(bool on, double percent = 42)
    {
        try
        {
            _isStarting = on;
            _loadingShown = on ? Math.Max(0, Math.Min(100, percent)) : 0;
            UpdateUI();
            if (on)
            {
                ApplyLoadingPalette();
                ApplyLoadingWidth();
            }
        }
        catch (Exception ex) { Logger.LogError("ShowLoadingForTest", ex); }
    }

    /// <summary>自检用：加载进度条当前配色（底色 / 填充 / 文字）。</summary>
    internal (string PanelBg, string FillBg, string TextFg, string GlossVis) LoadingPaletteForTest()
        => ((LoadingButtonPanel?.Background as SolidColorBrush)?.Color.ToString() ?? "",
            (LoadingFill?.Background as SolidColorBrush)?.Color.ToString() ?? "",
            (LoadingText?.Foreground as SolidColorBrush)?.Color.ToString() ?? "",
            LoadingGloss?.Visibility.ToString() ?? "");

    /// <summary>自检用：三个大按钮的高度与文字垂直对齐方式（一键启动 / 终止 / 加载）。</summary>
    internal (double IdleH, double StopH, double OpenH, VerticalAlignment IdleV, VerticalAlignment StopV, VerticalAlignment OpenV)
        MainButtonsForTest()
    {
        double H(string name) => (FindName(name) as FrameworkElement)?.ActualHeight ?? 0;
        VerticalAlignment V(string name) => (FindName(name) as FrameworkElement)?.VerticalAlignment ?? VerticalAlignment.Stretch;
        return (H("MainBtnBorder"), H("StopBtnBorder"), H("OpenEngineBorder"),
                V("MainBtnText"), V("StopBtnText"), V("OpenBtnText"));
    }

    /// <summary>等引擎打印入口地址的上限（毫秒）：插件加载器 settle 后才打印，通常 1–5 秒。</summary>
    private const int EntryUrlWaitMs = 8000;

    /// <summary>
    /// 等引擎把「带令牌的入口地址」打印出来（轮询 <paramref name="read"/>）。
    /// 引擎端口一就绪就开页面会抢在打印之前（打印挂在插件加载完成后），这正是
    /// 「每次一键启动都报『没拿到带口令的入口地址』」的根因；所以这里给它一个有界等待。
    /// 读取器可注入，自检用假数据即可覆盖。
    /// </summary>
    internal static async Task<bool> WaitForEntryUrlAsync(Func<string> read, int timeoutMs,
                                                          CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (ct.IsCancellationRequested) return false;
            if (!string.IsNullOrEmpty(read())) return true;
            await Task.Delay(50).ConfigureAwait(true);
        }
        return !string.IsNullOrEmpty(read());
    }

    /// <summary>
    /// 打开完页面后记什么事件（纯函数，便于自检）：
    /// · 拿到令牌时记普通信息；· 引擎不是本程序启动的时记普通信息（拿不到令牌是必然，并非异常）；
    /// · 本程序自己启动却始终没等到令牌时，才记橙色，且给一条可照做的动作。
    /// </summary>
    internal static (string Text, EventKind Kind) EntryNote(bool hasToken, bool external)
    {
        if (hasToken) return ("已打开首页", EventKind.Info);
        if (external)
            return ("已打开首页（此引擎不是本程序启动的，无法获取带口令的入口地址）", EventKind.Info);
        return ("已打开首页，但本次未取得带口令的入口地址；若页面提示未授权，点「终止引擎」后再点「一键启动引擎」",
                EventKind.Warn);
    }

    /// <summary>
    /// 打开引擎页面：探测始终打无令牌地址（带令牌的地址是给浏览器的一次性入场券，不能替它用掉），
    /// 且 404 一律不算就绪——webserver 在路由注册前对一切请求回 404，那正是"首开必 404"的来源。
    /// 等它注册完（干净地址回 401 / 带令牌回 2xx）再打开，用户第一次点就能直接进。
    /// 打开用带令牌的地址（会一并给浏览器写入授权 cookie），拿不到才退回首页。
    /// </summary>
    /// <param name="inStartupFlow">是否处在启动流程里：是则由调用方收尾，进度条保持满格不清空。
    /// 是否等待入口地址、事件怎么写，由 <c>_engineExternal</c>（引擎是不是本程序启动的）决定。</param>
    private async Task OpenEnginePageAsync(bool inStartupFlow = false)
    {
        try
        {
            bool external = _engineExternal;
            // 端口就绪早于「入口地址」打印，先给它一点时间（有界），否则每次都会误报未授权
            if (!external && _readyUrl.Length == 0 && _settings.AutoBrowseOnReady)
            {
                SetProgress("正在准备页面…", StartupProgress.PageReady);
                await WaitForEntryUrlAsync(() => _readyUrl, EntryUrlWaitMs, _cts?.Token ?? default);
            }

            bool hasToken = _readyUrl.Length > 0;
            string bare = NetworkHelper.ChooseProbeUrl(_readyUrl, _port);
            SetProgress("正在等待页面就绪…", StartupProgress.PageReady);
            // 探测的是干净地址，判定也必须按干净地址的口径来（404=没注册完；401=已注册可开）。
            // 曾经这里误用"带令牌"的标准（只认 2xx/3xx），于是正常抓到令牌时永远等不到就绪 ✗
            bool ok = await NetworkHelper.WaitUntilHttpReadyAsync(bare, 20000);
            SetProgress("正在打开浏览器…", StartupProgress.Done);
            if (ok) NetworkHelper.OpenBrowser(hasToken ? _readyUrl : bare);
            Logger.NoteRunOutput(hasToken
                ? "入口地址：已捕获（带口令）"
                : external ? "入口地址：外部引擎，按首页打开" : "入口地址：未捕获，按首页打开");
            if (!ok)
            {
                AddEvent("等待 20 秒页面仍未就绪，本次未打开浏览器——稍后点「加载引擎」重试", EventKind.Warn);
            }
            else
            {
                var note = EntryNote(hasToken, external);
                AddEvent(note.Text, note.Kind);
            }
            if (inStartupFlow) await Task.Delay(300);     // 启动流程：条留满格，由调用方 finally 收尾
            else { await Task.Delay(600); SetProgress(""); }
        }
        catch (Exception ex)
        {
            Logger.LogError("OpenEnginePageAsync", ex);
            SetProgress("");
        }
    }

    /// <summary>自检用：抓取地址的解析与就绪等待。</summary>
    internal static string ExtractReadyUrlForTest(string line, int port) => NetworkHelper.ExtractReadyUrl(line, port);

    internal static Task<bool> WaitHttpReadyForTest(string url, int timeoutMs)
        => NetworkHelper.WaitUntilHttpReadyAsync(url, timeoutMs);

    /// <summary>自检/截图用：把界面切到「引擎运行中」状态（不启动任何进程）。</summary>
    internal void ShowRunningForTest(bool running = true)
    {
        try
        {
            _isRunning = running;
            _engineExternal = running;
            if (running) _engineStartTime = DateTime.Now.AddSeconds(-111);
            UpdateUI();
        }
        catch (Exception ex) { Logger.LogError("ShowRunningForTest", ex); }
    }

    /// <summary>自检用：服务控制三态面板的可见性（空闲 / 运行 / 加载）。同一时刻只该有一个可见。</summary>
    internal (Visibility Idle, Visibility Running, Visibility Loading) MainPanelsForTest()
        => (IdleButtonPanel?.Visibility ?? Visibility.Collapsed,
            RunningButtonPanel?.Visibility ?? Visibility.Collapsed,
            LoadingButtonPanel?.Visibility ?? Visibility.Collapsed);

    /// <summary>自检用：当前页里第一个挂了手型光标、可参与动效的元素。</summary>
    internal FrameworkElement? FirstInteractiveForTest()
    {
        FrameworkElement? found = null;
        try
        {
            void Walk(DependencyObject o)
            {
                if (found != null) return;
                if (o is FrameworkElement fe && fe.Cursor == Cursors.Hand && (fe is Border or Button))
                {
                    found = fe;
                    return;
                }
                int n = VisualTreeHelper.GetChildrenCount(o);
                for (int i = 0; i < n && found == null; i++) Walk(VisualTreeHelper.GetChild(o, i));
            }
            if (VersionPanel != null) Walk(VersionPanel);
        }
        catch { }
        return found;
    }

    /// <summary>进度条里的高光带从右向左扫过（2.2s 循环）。</summary>
    private void StartGloss()
    {
        if (LoadingGloss == null || GlossShift == null) return;
        double span = (LoadingButtonPanel?.ActualWidth ?? 420) + 90;
        var anim = new DoubleAnimation
        {
            From = -90,
            To = span,
            Duration = TimeSpan.FromSeconds(2.2),
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        GlossShift.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    private void StopGloss()
    {
        try { GlossShift?.BeginAnimation(TranslateTransform.XProperty, null); }
        catch { }
    }

    /// <summary>
    /// 进度文案 + 目标百分比。percent 给了就按它走（阶段里程碑 / 等待时长的渐近值）；
    /// 没给则沿用「从文本里解析 NN%」的老路子（其余调用点与自检仍走这条）。
    /// 目标值只前进不后退。
    /// </summary>
    private void SetProgress(string text, double? percent = null)
    {
        Dispatcher.Invoke(() =>
        {
            if (string.IsNullOrEmpty(text))
            {
                ProgressText.Visibility = Visibility.Collapsed;
                ProgressText.Text = "";
                StopLoadingAnimation(true);
                if (LoadingText != null) LoadingText.Text = "正在加载...";
            }
            else
            {
                ProgressText.Visibility = Visibility.Visible;
                ProgressText.Text = text;
                int pct = percent.HasValue
                    ? (int)Math.Round(Math.Max(0, Math.Min(100, percent.Value)))
                    : ParsePercent(text);
                if (pct > 0) _loadingTarget = Math.Max(_loadingTarget, Math.Min(100, pct)); // 只前进不后退
                StartLoadingAnimation();
            }
        });
    }

    /// <summary>从文案里认出「… 36%」这类百分比（老调用点用）。认不出返回 0。</summary>
    private static int ParsePercent(string text)
    {
        int pct = 0;
        var idx = text.IndexOf('%');
        if (idx > 0)
        {
            var start = idx - 1;
            while (start >= 0 && char.IsDigit(text[start])) start--;
            if (start + 1 < idx)
                int.TryParse(text.Substring(start + 1, idx - start - 1), out pct);
        }
        return pct;
    }

    /// <summary>
    /// 就绪时把绿条可见地补满到 100% 再往下走：版本记账、页面探测都可能花上几秒，
    /// 这一步之前不能把条清空（否则就是用户看到的「3% 之后一下跳满 / 条突然没了」）。
    /// 约 0.35s 补满，最多等 0.7s。
    /// </summary>
    private async Task CompleteProgressAsync(string text = "正在打开浏览器…")
    {
        SetProgress(text, StartupProgress.Done);
        ScheduleProgressHide();      // 4 秒后自动收起，别永远挂着"正在打开浏览器…"
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (_loadingShown < 99.5 && sw.ElapsedMilliseconds < 700)
            await Task.Delay(16);
    }

    /// <summary>自检用：把加载时钟往未来拨（验证长时间卡住时的蠕行，不用真等 60 秒）。</summary>
    internal void AddLoadingClockForTest(double seconds) => _loadingClockOffset += seconds;

    /// <summary>自检用：进度条当前的显示值 / 目标值。</summary>
    internal (double Shown, double Target) LoadingValuesForTest() => (_loadingShown, _loadingTarget);

    /// <summary>自检用：跑一遍「就绪时补满到 100%」的收尾动作。</summary>
    internal Task CompleteProgressForTestAsync() => CompleteProgressAsync();

    // ═══ 启动引擎 ═══

    /// <summary>
    /// 摘掉上一轮引擎的输出订阅（**退订必须与订阅配对**）。
    /// <para>
    /// 为什么必须做：订阅是 <c>_processManager.OutputReceived += 匿名 lambda</c>，
    /// 不退订的话，旧 ProcessManager 的 <c>OutputReceived</c> 上永远挂着指向本窗口的闭包；
    /// 旧引擎（已停止、或启动失败后被新一轮换掉）残余的输出一旦到达它的
    /// <c>OutputDataReceived</c>，就会被转发进 <see cref="AppendLiveLog"/> ——
    /// 用户在新一轮的日志面板里看到的是**上一次**引擎的输出。
    /// </para>
    /// <para>
    /// 两个解绑点都是"再也不会需要旧引擎输出"的时刻：启动时换新实例之前，
    /// 以及窗口关闭时。重复调用是安全的（C# 的 -= 对未登记的处理器是空操作）。
    /// </para>
    /// </summary>
    private void DetachEngineManager()
    {
        if (_processManager == null || _engineOutputHandler == null) return;
        try { _processManager.OutputReceived -= _engineOutputHandler; }
        catch { }
    }

    private async Task StartEngineAsync()
    {
        Logger.Log("StartEngineAsync");
        _unbound = false;              // 用户重新点启动 = 重新接管，解绑状态作废
        // 运行中改过的端口在这里生效：启动前把观测端口对齐到设置值
        if (!_isRunning && _settings.Port != _port)
        {
            _port = _settings.Port;
            PortText.Text = $"http://127.0.0.1:{_port}";
        }
        _isStarting = true;
        UpdateUI();
        SetProgress("正在检查运行环境…", StartupProgress.Check);

        // 开包即用：缺运行环境（Node.js）时先问一句、一键装好，再继续启动
        if (!await EnsureNodeAsync(interactive: true))
        {
            SetProgress("");
            _isStarting = false;
            UpdateUI();
            return;
        }

        SetProgress("正在启动引擎…", StartupProgress.Spawn);

        // 每次启动都作废上一次抓到的地址：token 是一次性的，留着会打开无效页面
        _readyUrl = "";

        // 端口已在监听说明引擎已在运行（例如从 bash 启动的 DSH）：
        // 不抢占端口、不重复启动，直接同步为运行中（外部），只观测不接管。
        if (NetworkHelper.IsPortListening(_port))
        {
            Logger.Log($"端口 {_port} 已在监听 → 识别为外部引擎，本程序不重复启动");
            _engineExternal = true;
            _isStarting = false;
            _isRunning = true;
            _engineStartTime = DateTime.Now;
            SetProgress("");
            AddEvent($"检测到外部引擎（{_port} 已在监听）· 本程序不接管进程");
            Logger.NoteStartup($"[外部引擎] 端口 {_port} 已在监听，沿用不接管。{StartupContext()}");
            RefreshStatusEvents();
            // 外部引擎同样记录版本履历；已固定的版本不会被它改写。
            RecordEngineReady(VersionInfo.GetCurrentVersion());
            if (_settings.AutoBrowseOnReady) await OpenEnginePageAsync();
            UpdateUI();
            // 沿用外部引擎 = 端口在监听、引擎确实起来了 = 启动成功，即本次启动诊断同样不留盘。
            //   与下面"引擎就绪"那一处同一判据（IfEngineRunning）：只认启动成没成，
            //   不被开壳期间任何一次 LogError 挡住，免得留下一份"全是正常过程"的启动日志。
            Logger.DiscardStartupLogIfEngineRunning();
            return;
        }
        await Task.Delay(200);

        _cts = new CancellationTokenSource();
        // 换新实例之前先摘掉上一轮的输出订阅，并把旧实例上已退出的引擎对象收掉：
        //   ① 不退订 ⇒ 旧引擎（启动失败被换掉的那一个）残余的输出仍会经旧实例的
        //      OutputReceived 转发进本窗口的「日志」面板，用户看到上一次引擎的输出；
        //   ② 没收掉    ⇒ 启动失败后这个旧实例再没人碰，它那个已退出的引擎对象与 OS 句柄
        //      只能等 GC 终结器（自动重试/手动重试每次都留一个）。
        //   只收"已退出"的对象，活着的进程归停止路径管（ReleaseExitedProcess 内部有判据）。
        DetachEngineManager();
        _processManager?.ReleaseExitedProcess();
        _processManager = new ProcessManager();

        // 版本记忆：本次启动使用的版本（固定版本；更新模式下用 @latest）。
        string spec = VersionMemory.Spec;
        _runVersion = spec == "latest" ? "" : spec;
        _runIssueLine = "";
        _runIssueCounted = false;
        // 分诊状态按"每次启动"复位：上一次运行停在无害段中间的话，不能让它影响本次的第一行
        lock (_stderrBlockLock) { _stderrInBenignBlock = false; }
        _knownNoiseNoted = false;
        Logger.Log($"版本策略: {VersionMemory.PolicyText} → 本次启动 spec={spec}");

        // 先订阅、再启动：Start() 一进去就开始读输出，晚一步订阅会漏掉最前面几行，
        // 而"引擎为什么没起来"往往正写在那几行里。同时留一份到内存（启动失败时随日志给出）。
        // 处理器先存进字段再挂上去（否则无从退订，见 _engineOutputHandler 的说明）
        _engineOutputHandler = (s, line) =>
        {
            // 引擎 stderr 分诊（现场问题：`[stderr] Error: AttachConsole failed` + 一串栈帧被当故障报给用户）。
            //   规则：只有明确匹配已知无害形态的 stderr（连同它后面那一段调用栈）才降级；其余一律照旧。
            //   降级的含义严格限定为三件事，且一条证据都不销毁：
            //     · 不进「启动失败原因」（_runIssueLine / _runLastErrorLine），即启动失败弹窗与归因里不再出现它；
            //     · 不参与启动失败判定（不进 NoteRunOutput —— RunOutputTail 是弹窗/诊断的证据链，专门给真问题用）；
            //     · 不写红色事件，改为灰色一行「无害提示」。
            //   证据仍在：原文由下面的 AppendLiveLog 逐字写进「日志」页实时面板（本进程内存态）；导出诊断包里没有它（包内的引擎输出取自 摘要.txt 的 RunOutputTail 段，命中行按上面第二条不入该缓冲，且命中行不落盘）。
            //   而"命令执行"那条链路的落盘策略（仅失败落盘 + 限量截断）完全没有改动。
            bool benignStderr = false;
            if (line.StartsWith(ProcessManager.StderrTag, StringComparison.Ordinal))
            {
                string body = line.Substring(ProcessManager.StderrTag.Length);
                lock (_stderrBlockLock)
                {
                    // 整块判定：ClassifyStderrStep 用上一行的状态界定"这一段到哪结束"
                    // （栈帧续行归本段；遇到下一个非栈帧行即结束，那行重新独立判定）
                    var step = Logger.ClassifyStderrStep(_stderrInBenignBlock, body);
                    _stderrInBenignBlock = step.InBenignBlock;
                    benignStderr = step.Benign;
                }
            }

            if (benignStderr)
            {
                Logger.NoteKnownNoise(line);
                if (!_knownNoiseNoted)
                {
                    _knownNoiseNoted = true;
                    AddEvent(Logger.KnownNoiseNote, EventKind.Info);       // 灰色提示，不是红字故障
                }
            }
            else
            {
                Logger.NoteRunOutput(line);
            }

            AppendLiveLog($"[{DateTime.Now:HH:mm:ss}] {line}");

            if (!benignStderr)
            {
                if (_runIssueLine.Length == 0 &&
                    FatalPatterns.Any(p => line.Contains(p, StringComparison.OrdinalIgnoreCase)))
                {
                    _runIssueLine = line.Trim();
                }

                // 兜底：特征词没认出来时，也记住最后一条「像错误」的行（含 Error / Cannot find / MODULE_NOT_FOUND）
                if (line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("cannot find", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("MODULE_NOT_FOUND", StringComparison.Ordinal) ||
                    line.Contains("not found", StringComparison.OrdinalIgnoreCase))
                {
                    _runLastErrorLine = line.Trim();
                }
            }

            // 顺带抓引擎自己打印的访问地址（带 token 的那个才是能直接打开的入口）
            // 注意：这一项不受分诊影响：入口地址可能出现在任何一行上，降级也不能漏掉它。
            if (_readyUrl.Length == 0)
            {
                string found = NetworkHelper.ExtractReadyUrl(line, _port);
                if (found.Length > 0) _readyUrl = found;
            }
        };
        _processManager.OutputReceived += _engineOutputHandler;

        try
        {
            // 现场历史问题（1.1.13 已撤除）：这里曾加过「启动前预检 + 往配置文件里补核心组件联接」，
            // 结果那批联接把 npx 的解析劫持了（npx 优先看当前目录的 node_modules）——
            // 固定版本被绕过、引擎入口 require 报错为 MODULE_NOT_FOUND。现在只做一件事：
            // 清掉本程序自己造的那些联接（幂等，只删指向 _npx 缓存的目录联接）。
            if (!_junctionsCleaned)
            {
                _junctionsCleaned = true;
                string clean = await Task.Run(() => ProfileReset.RemoveGuardJunctions(ProfileReset.TargetProfileDir()));
                Logger.NoteStartup($"[自检] 清理本程序创建的组件联接：{clean}");
            }

            // 启动前清单体检（bug ② 的修复点，就在拉起引擎之前这一步）：
            //   清单里有、node_modules 里没有装的包会让 dsh 在加载 profile 时直接崩
            //   （Error: dsh: cannot resolve profile bundle "…"）。
            //   既有的自愈挂在「打开插件页 -> 取 loader id」里，用户不点插件页就不会执行，即
            //   这里提前做一次：先按 dsh 自己的提示补装（带放开 pnpm 策略的环境变量），
            //   补不上就不阻塞启动，只把"是哪个包未安装"记进启动失败的原因文案。
            //   注意：本程序只补装，不删改用户的 package.json（不为让它能启动而隐式摘掉依赖）。
            SetProgress("正在检查插件清单…", StartupProgress.Check);
            Logger.NoteStartup("[阶段] 启动前清单体检开始");
            string preflightNote = "";
            try
            {
                preflightNote = await PreflightManifestAsync();
                Logger.NoteStartup(preflightNote.Length == 0
                    ? "[阶段] 启动前清单体检通过（清单里的依赖都在盘上）"
                    : "[阶段] 启动前清单体检发现问题，仍继续尝试启动：\n  " + preflightNote.Replace("\n", "\n  "));
            }
            catch (Exception ex)
            {
                // 体检本身出错绝不能挡住启动：如实记一条，继续走原流程
                Logger.LogError("StartEngineAsync/PreflightManifest", ex);
                Logger.NoteStartup("[阶段] 启动前清单体检出错（不挡启动）：" + ex.Message);
            }

            SetProgress("正在启动 DSH…", StartupProgress.Spawn);
            BeginRunClock();      // 本次运行开始计时（履历基线 + 连续运行累计 + 「自动-时间」门槛一起起表）
            // 未固定版本（latest）首次秒退时，_specOverride 会指向本机缓存里最新的那个版本：
            // 用户自定义过启动命令的，尊重用户的命令不动；否则用临时说明符拉起（BuildArgs 会补 --no-open/--port）
            string launchBase = _settings.LaunchCommand;
            if (launchBase.Trim().Length == 0 && _specOverride.Length > 0)
                launchBase = $"npx --yes @deepseek-ai/dsh@{_specOverride} web";
            bool started = await Task.Run(() => _processManager.Start(_port, _cts.Token, launchBase));
            Logger.Log($"启动结果: {started}");
            Logger.NoteStartup($"[拉起] 成功={started} 进程={_processManager?.TrackedPid}  {StartupContext()}");

            if (!started)
            {
                SetProgress("");
                string diagnosis = await DiagnoseStartupFailure();
                NoteRunIssue("启动失败（进程未能拉起）");
                Logger.ShowError("启动失败",
                    StartupCause.DialogText(_runIssueLine, Logger.RunOutputTail(12), 0),
                    $"{diagnosis}{PreflightSection(preflightNote)}\n\n启动配置: {ProcessManager.GetLaunchPreview(_port, _settings.LaunchCommand)}\n\n{EngineOutputSection()}\n日志: {Logger.CurrentLogFile}");
                _isStarting = false;
                UpdateUI();
                MaybePromptRollback();
                return;
            }

            Logger.Log($"等待 :{_port}...");
            int waitSeconds = 180;
            var waitClock = System.Diagnostics.Stopwatch.StartNew();
            var wait = await Task.Run(async () =>
            {
                // npx 首次运行需下载，给 180 秒；但进程中途退出时不得空等（提前判死，立刻给出真实原因）
                for (int i = 0; i < waitSeconds; i++)
                {
                    if (_cts.Token.IsCancellationRequested)
                        return (Ready: false, Kind: FailureKind.Canceled);
                    if (ProcessManager.ShouldStopWaiting(_processManager!.HasExited, NetworkHelper.IsPortListening(_port)))
                    {
                        bool listening = NetworkHelper.IsPortListening(_port);
                        if (!listening) Logger.Log($"引擎进程已退出（第 {i} 秒），不再空等");
                        return (Ready: listening,
                                Kind: listening ? FailureKind.Timeout : FailureKind.ProcessExited);
                    }
                    // 真实进度只有「进程已拉起」这一个里程碑，之后按等待时长渐近到 88%。
                    // 不再用「已等秒数 / 180」当百分比：那会让条几秒内只到 3%，然后随面板切换消失。
                    SetProgress("正在等待引擎就绪…", StartupProgress.WaitTarget(waitClock.Elapsed.TotalSeconds));
                    await Task.Delay(1000, _cts.Token);
                }
                return (Ready: false, Kind: FailureKind.Timeout);
            }, _cts.Token);

            bool ready = wait.Ready;
            Logger.Log($"{_port} 就绪: {ready}（{wait.Kind}，实际等了 {waitClock.Elapsed.TotalSeconds:0.#} 秒）");

            if (!ready)
            {
                int waitedSeconds = (int)Math.Round(waitClock.Elapsed.TotalSeconds);

                // 用户自己点的取消：不是故障，不记异常、不清理重试、不弹框
                if (wait.Kind == FailureKind.Canceled)
                {
                    SetProgress("");
                    _isStarting = false;
                    Logger.Log($"启动已取消（等了 {waitedSeconds} 秒）");
                    UpdateUI();
                    return;
                }

                SetProgress("");
                var failure = new StartupFailure(wait.Kind, waitedSeconds,
                    _runIssueLine.Length > 0 ? _runIssueLine : _runLastErrorLine, Logger.RunOutputTail(30));
                _isStarting = false;

                // 判定"启动失败"：此后本次「启动-*.log」一律留住（含下面自动重试成功后不再撤掉的那份现场）。
                //   与 NoteRunIssue/LogError 那套"出过事"的标志分开记 —— 那一位还管回退提示与版本履历，
                //   这一位只管日志留不留：自动重试成功时前者仍为 true，日志却必须按"引擎起来了"撤掉。
                Logger.MarkStartupFailed();

                // 有界清理：只收掉本程序拉起的这棵树；端口上的监听者若不是本程序启动的，不强关
                // （否则失败清理会把用户自行启动的引擎一并终止）
                string cleanNote = await Task.Run(() =>
                {
                    var notes = new System.Collections.Generic.List<string>();
                    int pid = _processManager?.TrackedPid ?? 0;
                    if (pid > 0 && !_processManager!.HasExited)
                    {
                        ProcessManager.ForceKillTreeExceptSelf(pid, out string n1);
                        notes.Add(n1);
                    }
                    if (NetworkHelper.IsPortListening(_port))
                    {
                        int owner = ProcessManager.FindPortOwners(_port).FirstOrDefault()?.Pid ?? 0;
                        if (ProcessManager.ShouldForceClosePort(owner, pid, ProcessManager.ParentPidOf))
                        {
                            ProcessManager.ForceClosePort(_port, 5, out string n2);
                            notes.Add(n2);
                        }
                        else
                        {
                            notes.Add("端口上的程序不是本程序启动的，已跳过强制关闭");
                        }
                    }
                    return notes.Count > 0 ? string.Join("；", notes) : "（没有需要清理的残留）";
                });
                Logger.Log($"启动失败清理：{cleanNote}");
                Logger.NoteStartup("[阶段] 失败清理结束");

                // 配置文件体检：node_modules 里指向"已消失位置"的模块链接会让引擎报
                // 「找不到模块」并永不监听端口。只修这种死链接（正常的一概不碰）。
                string linkNote = await Task.Run(() =>
                {
                    try
                    {
                        string profileRoot = Path.Combine(ProcessManager.WorkDir, "profiles");
                        var broken = ProfileHealth.FindBrokenModuleLinks(profileRoot);
                        if (broken.Count == 0) return "（模块链接正常）";
                        Logger.NoteRunOutput($"发现 {broken.Count} 个失效的模块链接："
                            + string.Join("、", broken.Select(b => b.Name)));
                        ProfileHealth.RepairBrokenModuleLinks(profileRoot, broken, out string d);
                        return d;
                    }
                    // 这里出错时只回一句中性中文结论，不带异常原文：返回值是"给人看的结论"，
                    // 上面三个 return 也全是中文结论，只有这一处会漏英文原文（还可能带盘符路径）。
                    // 另有一条硬约束：返回值必须以「（」开头 —— 调用处（:1729 附近）用 StartsWith("（")
                    // 区分"没发现问题/没能体检"与"体检发现了问题"，前者不进 AddEvent（可见事件行）。
                    // 原文不丢：走 NoteDiagnosis 真落盘（与上面 :1722 记录失效链接的写法一致）。
                    // 注意不能用 Logger.Log / LogDiagnosis —— 那两个是空实现，写了等于没写。
                    catch (Exception ex) { Logger.NoteDiagnosis($"模块链接体检失败：{ex}"); return "（本次未能完成模块链接体检）"; }
                });
                if (!linkNote.StartsWith("（", StringComparison.Ordinal))
                {
                    AddEvent($"启动自检：{linkNote}", EventKind.Warn);
                    Logger.NoteRunOutput("配置文件体检：" + linkNote);
                }


                // 一条完整的失败现场（下次内测再遇到，直接把「启动-*」日志发回来就够定位）
                Logger.NoteStartup(
                    $"[失败] 类型={failure.Kind} 等待={waitedSeconds} 秒 重试过={_startupRetried}\n"
                    + $"  {StartupContext()}\n"
                    + $"  判定：{StartupCause.Describe(_runIssueLine, failure.Tail)}\n"
                    + (preflightNote.Length == 0 ? "" : $"  启动前清单体检：\n{Indent(preflightNote)}\n")
                    + $"  清理：{cleanNote}\n"
                    + $"  引擎输出尾部：\n{Indent(Logger.RunOutputTail(12))}\n"
                    + $"  日志：{Logger.CurrentLogFile}");

                // 全新安装后没有固定版本（spec=latest）时最容易"第一次解析/下载失败"——
                // 现场实测：安装器勾选启动的那一次失败、手动重启后同一条命令就成功。
                    // 兜底：这种情况改用本机缓存里最新的 DSH 版本再试一次（日志与事件都写明）。
                if (failure.Kind == FailureKind.ProcessExited && _specOverride.Length == 0 &&
                    VersionMemory.Spec.Equals("latest", StringComparison.OrdinalIgnoreCase))
                {
                    string cached = PickCachedFallback(VersionInfo.ListCachedVersions().Select(v => v.Version));
                    if (cached.Length > 0)
                    {
                        _specOverride = cached;
                        Logger.NoteStartup($"[兜底] 未固定版本且首次秒退 → 改用本机缓存的 DSH {cached} 重试");
                    }
                }

                // 自动重试一次：抖动型失败值得重试（同一版本只试一次）
                if (failure.ShouldAutoRetry(_startupRetried))
                {
                    _startupRetried = true;
                    AddEvent($"引擎 {waitedSeconds} 秒未能启动，已清理残留并自动重试一次", EventKind.Warn);
                    SetProgress("正在重试…", StartupProgress.Spawn);
                    Logger.Log("启动失败 → 自动重试一次");
                    await StartEngineAsync();
                    return;
                }

                // 一次点击只记一次异常：准备重试的那次不记账，否则一次点击就达到"连续异常"阈值，
                // 下次开壳又弹一个「建议回退版本」——同一个问题询问两次（现场 bug）。
                // 事件行只写一句（红字要短），原因与建议留在弹窗和日志里。
                NoteRunIssue(failure.EventLine);

                // 只弹一个框：能换版本就把"换回可用的版本"做成默认键（不再事后追加"建议回退"第二个框）
                Logger.NoteStartup("[阶段] 开始自动诊断");
                string diagnosis = await DiagnoseStartupFailure();
                Logger.NoteStartup("[阶段] 诊断结束，准备弹窗");
                ShowStartupFailureDialog(failure,
                    $"{diagnosis}{PreflightSection(preflightNote)}\n\n清理情况：{cleanNote}\n模块链接：{linkNote}\n\n" +
                    $"{EngineOutputSection()}\n日志: {Logger.CurrentLogFile}");
                UpdateUI();
                return;
            }

            _isRunning = true;
            _engineStartTime = DateTime.Now;
            Logger.Log("引擎运行中");
            Logger.NoteStartup($"[成功] 端口 {_port} 就绪，用时 {waitClock.Elapsed.TotalSeconds:0.#} 秒  {StartupContext()}");
            // 先把绿条看得见地补满：版本记账、页面探测、入口地址等待都可能花上几秒，
            // 这一步之前清空进度条，就是用户看到的「3% 之后突然跳满 / 条一下没了」。
            await CompleteProgressAsync();

            // 版本记忆：成功运行一次即记履历；更新模式下的这一次会把新版本固定下来
            string running = spec == "latest" ? "" : spec;
            for (int i = 0; i < 6 && running.Length == 0; i++)
            {
                string detected = VersionInfo.GetCurrentVersion();
                if (detected != "未知") { running = detected; break; }
                await Task.Delay(500);
            }
            if (running.Length == 0) running = VersionInfo.GetCurrentVersion();
            RecordEngineReady(running);

            // 端口已就绪但输出中出现致命字样（例如客户端插件加载失败）时记为异常；
            // 若刚升级过，立即提示回退。
            if (_runIssueLine.Length > 0)
            {
                NoteRunIssue("引擎已就绪，但输出里有报错：" + _runIssueLine);
                MaybePromptRollback();
            }

            _terminateAttempted = false;      // 新引擎起来了，重新计
            AddEvent($"引擎已启动 · 端口 {_port}");
            RefreshStatusEvents();

            if (_settings.AutoBrowseOnReady)
            {
                await OpenEnginePageAsync(inStartupFlow: true);
            }

            // 不再做「页面全关就停引擎」的连接自检；状态由端口同步驱动。
            UpdateUI();

            // 引擎已就绪 = 启动成功，即把本次的「启动-*.log」撤掉（用户要求：启动没问题就不该留日志）。
            //   用 IfEngineRunning 而不是 IfHealthy：后者还会被"这次运行里任何一次 LogError /
            //   任何一次 MarkStartupUnhealthy"挡住，于是"第一次尝试失败、自动重试成功"留下的那份
            //   正常启动记录（含末尾的「[成功] 端口 … 就绪」）会一直躺在日志页里 —— 这正是用户 1.3.55
            //   实测报的"还是会记录一堆正常的启动日志"。这里只认"启动到底成没成"。
            //   放在 try 末尾而不是 finally：失败分支是提前 return 出来的，走不到这里；
            //   而且失败分支都调过 MarkStartupFailed，即这个调用本身也会拒绝删。
            Logger.DiscardStartupLogIfEngineRunning();
        }
        catch (OperationCanceledException) { Logger.Log("启动取消"); SetProgress(""); }
        catch (Exception ex)
        {
            Logger.LogError("StartEngineAsync", ex);
            SetProgress("");
            // 正文只给人看的一句结论：异常原文（英文）与日志文件位置（盘符路径）都只走 details 落盘，
            // 不进弹窗 —— 弹窗不是路径设置页，不适用"允许显示路径"那条例外。
            // 信息不丢：Logger.ShowError 会把 details 全量写进异常日志（含 ex.ToString() 与日志文件位置），
            // 用户修好之后仍能从「日志」页拿到这次异常的全部细节；异常本身上面也已 LogError 落盘。
            Logger.ShowError("启动异常",
                "启动过程中出现异常，本次启动未能完成。\n\n处理方式：\n· 回到主界面点「一键启动引擎」重试一次\n· 仍然不行就到「日志」页查看详情并导出诊断包",
                $"异常详情：\n{ex}\n\n日志文件：{Logger.CurrentLogFile}");
        }
        // 进度条在加载面板收起之后再归零（顺序反了就会看到"条先空、面板还在"）
        finally { _isStarting = false; UpdateUI(); StopLoadingAnimation(true); }
    }

    // ═══ 版本记忆联动 ═══
    /// <summary>引擎跑通后写入版本履历；更新模式下的本次运行会固定新版本。</summary>
    private void RecordEngineReady(string version)
    {
        try
        {
            string before = VersionMemory.Pin;
            bool pendingBefore = VersionMemory.PendingUpdate;
            // 换了版本：先把上一段的运行时长结算掉，别记到新版本头上
            if (_runVersion.Length > 0 && version.Length > 0 && version != "未知" && version != _runVersion)
                RecordRunDuration();
            VersionMemory.NoteEngineReady(version);
            // 外部启动的引擎也在这里被观测到就绪，同样开始计时，保证"启动次数"和"累计运行"对得上
            if (_runStartAt == DateTime.MinValue) BeginRunClock();
            if (version != "未知" && version.Length > 0)
            {
                _runVersion = version;
                _currentDshVersion = version;
            }
            _runIssueCounted = false;

            if (VersionMemory.Pin != before)
            {
                Logger.Log($"版本记忆：固定版本 {before} → {VersionMemory.Pin}");
                // 先取结果再报：NoteEngineReady 内部走 Save()，写盘失败（含读失败主动拒写）时
                // 内存里的 Pin 已经变了、盘上却没有 ⇒ 照报「已固定」会让用户重启后发现固定版本没了。
                var readyOutcome = VersionMemoryOutcome(VersionMemory.Pin.Length > 0
                    ? $"版本记忆：已固定 DSH {VersionMemory.Pin}"
                    : "版本记忆：改为跟随最新版");
                AddEvent(readyOutcome.Text, readyOutcome.Kind);
            }
            else if (pendingBefore && VersionMemory.PendingUpdate)
            {
                AddEvent($"更新未生效（仍是 DSH {version}），下次启动会再试一次", EventKind.Warn);   // 升级异常提醒=橙
            }
            UpdateVersionCard();
            if (IsVersionPageVisible()) RenderVersionView();
        }
        catch (Exception ex) { Logger.LogError("RecordEngineReady", ex); }
    }

    /// <summary>「设置 -> 版本」页当前是否可见。</summary>
    private bool IsVersionPageVisible() =>
        _currentView == GuardView.Settings
        && SettingsPageVersion != null
        && SettingsPageVersion.Visibility == Visibility.Visible;

    /// <summary>记一次启动异常（同一次运行只记一次，避免多分支重复计数）。</summary>
    private void NoteRunIssue(string reason)
    {
        try
        {
            // 走到这里就是"本次启动出过事"，即固定启动诊断日志，不让成功分支把它当健康记录删掉
            // （典型：引擎最终起来了，但输出里有报错 —— 引擎已就绪，证据仍要留）。
            Logger.MarkStartupUnhealthy();
            // 原文必须真落盘：回退弹窗正文按界面规矩只说中文结论，不再摆引擎输出的英文原始行，
            //   而「最近一次」的依据此前只存在版本记忆的 JSON 里，异常日志里一行都没有
            //   （Logger.Log 是空实现，写它等于没写 —— 见 Logger.cs 类头）。
            //   放在下面那个"只记一次"的闸之前：引擎已就绪但输出里有报错的那条分支
            //   （NoteRunIssue 之后紧跟 MaybePromptRollback）也在这之前记过一次，证据才不会断。
            if (!string.IsNullOrEmpty(reason))
                Logger.NoteDiagnosis("启动异常原文：" + reason);
            if (_runIssueCounted) return;
            _runIssueCounted = true;
            VersionMemory.NoteError(reason, _runVersion.Length > 0 ? _runVersion : null);
            // 事件栏只给中性中文结论：reason 是引擎输出的**原始行**（英文报错，还可能带盘符路径），
            // 按界面规矩不上屏；原文已由上面那句 NoteDiagnosis 真落盘，这里只指向去处
            // （日志目录不可写时 LogPromise 会在后面如实补一句，不空许诺）。
            AddEvent("启动异常：引擎输出里有报错；"
                + LogPromise("详细原因已记入日志，可在「日志」页查看。"), EventKind.Bad);
            Logger.Log($"版本记忆：启动异常 → {reason}");
        }
        catch (Exception ex) { Logger.LogError("NoteRunIssue", ex); }
    }

    /// <summary>守护壳启动的引擎提前退出（&lt; 90 秒）：记录版本异常，必要时提示回退。</summary>
    private void HandlePrematureExit()
    {
        try
        {
            // ── 先分清两件事：「用户自己点的停止」与「账本要不要收尾」互不相干 ──
            //
            // _stopping 的本意只针对异常归因：用户点的停止不算启动异常，不该记 Error。
            // 但它曾把"停机收尾"也一并挡掉了 —— 两级终止（断端口 -> 强杀）都失败、
            // 而进程已经退出时会残留：那条路把 _stopping 置了一整段，
            // 于是进程确实退出后也既不归因、也不结算，账本一直开着，下一个 30 秒结算点就把
            // 停机期当成运行时长补进履历（正是 SettleRunClock 存在的理由）。
            //
            // 注意：判据的关键是进程确实退出后才清账：这里只认 _processManager.HasExited
            //   （TrackedPid==0 / 进程对象已 Dispose 也一律按"已退出"处理，见 ProcessManager.HasExited）。
            //   进程还活着时一律不动账本 —— 不为让账本干净而虚报"未运行"：
            //   那会和既有策略冲突 —— 引擎可能仍在监听，端口同步本来就该把它判成"运行中"、账本也该继续开着
            //   （终止第一档失败时正是这种局面，见 TerminateEngineAsync 里那句"未停止时不得虚报未运行"）。
            //   两个调用点都成立在"引擎已经没了"之上：进程退出分支看的是 HasExited；
            //   端口关闭分支是端口已经不监听（且非外部/未解绑时上方那句 _processManager != null 已确保对象在）。
            //   所以这里再判一次不会改变既有判定，只是把"能不能清账"这件事收在本方法里自证。
            bool processGone = _processManager == null || _processManager.HasExited;

            // 结算这一份只做一次：SettleRunClock 幂等（第二次空转返回 0），
            // 而 RecordRunDuration 会一并记一条用户可见的"本次运行 N 秒" —— 不能因为多一条分支就少记它。
            int ranSec = RecordRunDuration();

            // 进程真退了，即账本到此为止；_stopping 只免归因，不再影响收尾。
            if (processGone && _stopping) return;

            // 进程还活着（_stopping 与否都一样）：账本留着，只做提前退出的异常归因。
            // 注意：这条分支上"跑得够久"就不再报异常 —— 与原本的 ranSec >= 90 判据完全一致。
            if (!processGone) { NoteExitIfPremature(ranSec); return; }

            // 走到这儿：进程真的退出了。_stopping=false 时连归因一起做（原路径），
            // _stopping=true 的那一半已在上面提前 return（用户自己点的停止不算异常）。
            NoteExitIfPremature(ranSec);
        }
        catch (Exception ex) { Logger.LogError("HandlePrematureExit", ex); }
    }

    /// <summary>
    /// 提前退出的异常归因：跑得够久（≥ 90 秒）或本次已记过异常就什么都不做。
    /// 抽出来只为让 <see cref="HandlePrematureExit"/> 的两条分支共用同一份判据（避免两处走样）。
    /// </summary>
    private void NoteExitIfPremature(int ranSec)
    {
        if (ranSec >= 90 || _runIssueCounted) return;

        NoteRunIssue(_runIssueLine.Length > 0
            ? $"启动后 {ranSec} 秒退出；最后错误：{_runIssueLine}"
            : $"启动后 {ranSec} 秒即退出");
        MaybePromptRollback();
    }

    /// <summary>
    /// 失败后该不该把「换回 X」做成默认动作（纯函数，便于自检）：
    /// 只有"重试过一轮仍失败 + 有别的版本可用"才值得——首次失败可能只是配置文件正在写。
    /// </summary>
    internal static bool CanOfferVersionSwitch(StartupFailure failure, string candidate, string currentSpec,
                                               bool alreadyRetried)
        => failure.DeterministicAfterRetry(alreadyRetried) && candidate.Length > 0 && candidate != currentSpec;

    /// <summary>
    /// 启动失败后唯一的对话框。按可行性给动作，不给出无法兑现的建议：
    /// · 本机确实缓存了别的可用版本时，用「换回 DSH X」（默认键）
    /// · 组件解析不上时，用「重置 DSH 配置」（把配置文件目录整体搬到备份，让引擎重建；新机器上这是唯一有意义的路）
    /// · 两样都不适用时，只做普通提示
    /// 技术细节全部写日志，不进弹窗正文。
    /// </summary>
    private void ShowStartupFailureDialog(StartupFailure failure, string details)
    {
        try
        {
            string body = failure.DialogText();
            string candidate = VersionMemory.BestRollbackCandidate();
            bool canSwitch = CanOfferVersionSwitch(failure, candidate, VersionMemory.Spec, _startupRetried);
            bool brandNew = ProfileReset.LooksBrandNew(ProfileReset.TargetProfileDir());
            int plugins = ProfileReset.CountPlugins(ProfileReset.TargetProfileDir());
            // 恒有重置：用户两次点名要它，而且「引擎起不来」本身就是重置最有用的场合（不再依赖报错签名）
            bool canReset = true;
            // 已经在这个框里告诉过用户"这个版本起不来"了，不要再在下次开壳时弹一遍「建议回退版本」
            if (canSwitch) VersionMemory.MarkPrompted(VersionMemory.Spec);

            Logger.NoteFailure(canSwitch || canReset ? "启动失败（可修复）" : "启动失败", body, details);
            Logger.NoteStartup($"[阶段] 弹窗准备完成（可换版本={canSwitch} 可重置={canReset} "
                + $"全新机器={brandNew} 插件数={plugins}）");

            if (!canSwitch && !canReset)
            {
                Logger.ShowError("启动失败", body, details);
                return;
            }

            var buttons = new System.Collections.Generic.List<GuardDialog.DialogButton>();
            if (canReset)
            {
                // 新机器上"回滚"无法兑现，重置才是可行路径，因此把它做成默认键
                buttons.Add(new GuardDialog.DialogButton(
                    brandNew ? "重置 DSH 配置" : "重置 DSH 配置（保留备份）",
                    MessageBoxResult.Retry, Color.FromRgb(0x0A, 0x84, 0xFF), IsDefault: !canSwitch));
            }
            if (canSwitch)
            {
                buttons.Add(new GuardDialog.DialogButton($"换回 DSH {candidate}", MessageBoxResult.Yes,
                    Color.FromRgb(0x34, 0xC7, 0x59), IsDefault: true));
            }
            buttons.Add(new GuardDialog.DialogButton("先不动", MessageBoxResult.No,
                Color.FromRgb(0x8E, 0x8E, 0x93), IsCancel: true));

            GuardDialog.ShowNonModalCustom(body, "DSH 守护壳 · 启动失败", MessageBoxImage.Error, async pick =>
            {
                try
                {
                    if (pick == MessageBoxResult.Yes && canSwitch)
                    {
                        VersionMemory.RollbackTo(candidate, "当前版本在本机起不来（引擎自带组件解析失败）");
                        AddEvent($"已换回 DSH {candidate}；再点一次「一键启动引擎」即可", EventKind.Warn);
                        UpdateVersionCard();
                        if (IsVersionPageVisible()) RenderVersionView();
                    }
                    else if (pick == MessageBoxResult.Retry && canReset)
                    {
                        var (ok, detail) = await Task.Run(() => ProfileReset.MoveAside(ProfileReset.TargetProfileDir()));
                        Logger.NoteStartup($"[重置] {detail}（插件数={plugins} 全新机器={brandNew}）");
                        if (!ok)
                        {
                            // 事件栏只留中文结论：detail 来自 ProfileReset.MoveAside，失败分支里
                            //   可能是 "搬走失败：" + ex.Message（ProfileReset.cs:135）或裸 ex.Message（:139），
                            //   IO 异常的原文是英文、还常带盘符路径，按界面规矩不上屏。
                            //   原文不丢：上面紧邻的那条 NoteStartup 已把 detail 原样写进「启动-*.log」
                            //   （本次启动失败已 MarkStartupFailed，该文件不会被按"健康"撤掉），
                            //   日志页能选到它；目录不可写时 LogPromise 会如实补一句，不空许诺。
                            AddEvent("重置 DSH 配置失败；"
                                + LogPromise("详细原因已记入日志，可在「日志」页查看。"), EventKind.Bad);
                            return;
                        }
                        AddEvent(brandNew
                            ? "已重置 DSH 配置，正在重新启动引擎"
                            : $"已把 DSH 配置搬到备份（{detail}），正在重新启动引擎", EventKind.Warn);
                            // 配置换了新的：清理重跑一遍即可。不重置「已重试」标志——
                        // 否则重置后那次失败又会白送一次自动重试，点一次重置连拉两遍引擎（复查发现）
                        _junctionsCleaned = false;
                        await StartEngineAsync();
                    }
                }
                catch (Exception ex) { Logger.LogError("ShowStartupFailureDialog.onPick", ex); }
            }, buttons.ToArray());
            return;
        }
        catch (Exception ex) { Logger.LogError("ShowStartupFailureDialog", ex); }
    }

    private int _recomposeCount, _recomposeNudges;
    internal int RecomposeCountForTest => _recomposeCount;
    internal int RecomposeNudgeCountForTest => _recomposeNudges;

    /// <summary>
    /// 切换毛玻璃 / 切主题后强制窗口重新合成。
    /// 现场 bug：反复切换几次后合成层失效（侧栏变透明、标题重影、文字错位），拖动一次就恢复——
    /// 说到底是 DWM 没重画。这里用程序方式做等价的事：① RedrawWindow 整窗重画；
    /// ② 若系统不认（返回 false）就做一次 1 像素位移微调再复原，与「拖一下」同理，肉眼看不见。
    /// </summary>
    internal void ForceRecompose(string why)
    {
        try
        {
            _recomposeCount++;
            var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            bool redrawn = false;
            if (h != IntPtr.Zero)
            {
                redrawn = NativeMethods.RedrawWindow(h, IntPtr.Zero, IntPtr.Zero,
                    NativeMethods.RDW_INVALIDATE | NativeMethods.RDW_FRAME |
                    NativeMethods.RDW_ALLCHILDREN | NativeMethods.RDW_UPDATENOW);
            }
            InvalidateVisual();
            UpdateLayout();
            // 位移微调只在「没在拖动、没最大化」时做（最大化下挪窗会被系统强行还原）；
            // 走 SetWindowPos 而不是 Left/Top，避免 WPF 异步投递与跨 DPI 重排
            if (!redrawn && !_dragging && WindowState != WindowState.Maximized && h != IntPtr.Zero
                && NativeMethods.GetWindowRect(h, out var rc))
            {
                NativeMethods.SetWindowPos(h, IntPtr.Zero, rc.Left + 1, rc.Top + 1, 0, 0,
                    NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                NativeMethods.SetWindowPos(h, IntPtr.Zero, rc.Left, rc.Top, 0, 0,
                    NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                _recomposeNudges++;
            }
            // 正常路径（RedrawWindow 一次完成）不写任何日志：这是例行动作，不是异常。
            // 只有系统不认、被迫走"1 像素微调"这种降级时才留痕。
            if (!redrawn)
                Logger.NoteDiagnosis($"[合成] 常规重画无效，已用位移微调兜底（{why}，第 {_recomposeCount} 次）");
        }
        catch (Exception ex) { Logger.LogError("ForceRecompose", ex); }
    }
    /// <summary>界面卡顿要不要记账（纯函数）：超过 5 秒没心跳就记，30 秒内不重复刷屏。</summary>
    internal static bool ShouldReportStall(TimeSpan lag, TimeSpan sinceLastReport)
        => lag.TotalSeconds >= 5 && sinceLastReport.TotalSeconds >= 30;

    /// <summary>
    /// UI 线程心跳：每秒打一次点；后台看门狗发现长时间不跳就往日志写一行。
    /// 现场反馈过"无法响应只能杀进程"，下次再有这情况日志里就有时间点与现场，不用靠猜。
    /// </summary>
    private void StartUiHeartbeat()
    {
        try
        {
            _uiHeartbeat = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            _uiHeartbeat.Tick += (_, _) => _uiBeatUtc = DateTime.UtcNow;
            _uiHeartbeat.Start();

            _uiWatchdog = new System.Threading.Timer(_ =>
            {
                try
                {
                    var lag = DateTime.UtcNow - _uiBeatUtc;
                    if (_dragging) return;      // 拖动中不算卡顿
                    if (!ShouldReportStall(lag, DateTime.UtcNow - _lastStallReportUtc)) return;
                    _lastStallReportUtc = DateTime.UtcNow;
                    Logger.NoteDiagnosis($"界面卡顿：UI 线程 {lag.TotalSeconds:0.#} 秒没有响应"
                        + $"（进程 {Environment.ProcessId}，对话框开着={GuardDialog.AnyOpen}，启动中={_isStarting}）");
                }
                catch { }
            }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(3));
        }
        catch (Exception ex) { Logger.LogError("StartUiHeartbeat", ex); }
    }

    /// <summary>
    /// 未固定版本秒退时的兜底：从本机 npx 缓存里挑一个最新可用的 DSH 版本（纯函数，便于自检）。
    /// 认不出任何版本就返回空串（则按 latest 重试）。
    /// </summary>
    internal static string PickCachedFallback(IEnumerable<string> cachedVersions)
    {
        try
        {
            var list = cachedVersions
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(VersionMemory.VersionRank)
                .ToList();
            return list.Count > 0 ? list[0] : "";
        }
        catch { return ""; }
    }
    /// <summary>对话框开/关时的联动：开着就暂停加载动画，别跟弹窗抢渲染（"弹窗点不动"的现场成因之一）。</summary>
    private void OnDialogOpenChanged()
    {
        try
        {
            if (GuardDialog.AnyOpen)
            {
                _loadingTimer?.Stop();
                StopGloss();
            }
            else if (_isStarting && _loadingTarget > 0)
            {
                StartLoadingAnimation();
            }
        }
        catch (Exception ex) { Logger.LogError("OnDialogOpenChanged", ex); }
    }

    /// <summary>同一版本连续启动异常 ≥2 次时，主动提示回退到上一个长期运行版本。</summary>
    private void MaybePromptRollback()
    {
        try
        {
            if (_rollbackPromptOpen) return;
            if (!VersionMemory.ShouldPromptRollback(out string candidate, out int errors, out string version)) return;

            // 判定"该回退"时，即本次启动确实不健康，启动诊断日志必须留下（用户要求里的"被回退"也要有证据）
            Logger.MarkStartupUnhealthy();

            _rollbackPromptOpen = true;
            try
            {
                VersionMemory.MarkPrompted(version);
                // 正文只说中文结论：「最近一次」的依据是引擎输出的**原文行**（英文报错，还可能带盘符路径），
                // 按界面规矩不上屏（与「设置 → 版本」页那一行的写法一致：正文只说次数，细节收进悬停）。
                // 原文一行不失：NoteRunIssue 已把它经 NoteDiagnosis 落盘，措辞与其它页一致 —— 指向「日志」页。
                bool hasReason = VersionMemory.LastErrorReason.Length > 0;
                string why = hasReason
                    ? "\n最近一次失败的详细原因已记入日志，可在「日志」页查看。" : "";

                // 刚升级过：大概率是插件或新版本不兼容，写明理由。
                bool justUpdated = VersionMemory.IsRecentlyUpdated(24, out string from, out _);
                string head = justUpdated
                    ? $"刚升级到 DSH {version}（来自 {from}）后启动不顺利。{why}\n"
                    : $"检测到 DSH {version} 连续 {errors} 次启动异常。{why}\n";

                string disabled = VersionMemory.DisabledForUpdate.Count > 0
                    ? "\n升级时已禁用：" + Shorten(string.Join("、", VersionMemory.DisabledForUpdate), 120) : "";

                GuardDialog.ShowNonModalCustom(                    head + disabled + "\n" +
                    $"建议换回 DSH {candidate}：\n" +
                    "（只改启动版本，不会终止当前进程；下次启动引擎时生效。\n" +
                    "　回退后本程序会询问是否恢复当时禁用的插件）",
                    "建议换回上一个版本", MessageBoxImage.Warning,
                    pick =>
                    {
                        try
                        {
                            if (pick != MessageBoxResult.Yes) return;
                            VersionMemory.RollbackTo(candidate);
                            Logger.Log($"版本记忆：因连续异常自动回退到 {candidate}");
                            AddEvent($"已回退到 DSH {candidate}（连续 {errors} 次启动异常）", EventKind.Warn);   // 自动回滚=橙
                            UpdateVersionCard();
                            if (IsVersionPageVisible()) RenderVersionView();
                            OfferRestoreDisabledPlugins();
                        }
                        finally { _rollbackPromptOpen = false; }
                    },
                    new GuardDialog.DialogButton($"换回 DSH {candidate}", MessageBoxResult.Yes,
                        Color.FromRgb(0x34, 0xC7, 0x59), IsDefault: true),
                    new GuardDialog.DialogButton("先不动", MessageBoxResult.No,
                        Color.FromRgb(0x8E, 0x8E, 0x93), IsCancel: true));
            }
            finally { _rollbackPromptOpen = false; }
        }
        catch (Exception ex) { Logger.LogError("MaybePromptRollback", ex); }
    }

    /// <summary>启动后延迟检查一次：上次会话连续异常则提示回退（等窗口显示后弹出）。</summary>
    private async Task PromptRollbackSoonAsync()
    {
        try
        {
            await Task.Delay(2500);
            MaybePromptRollback();
        }
        catch (Exception ex) { Logger.LogError("PromptRollbackSoonAsync", ex); }
    }

    // ═══ 启动失败自动排查 ═══
    /// <summary>启动诊断的固定上下文（写进「启动-*」日志，内测机器出问题就靠它定位）。</summary>
    private string StartupContext()
        => $"版本策略={VersionMemory.Spec} 端口={_port} 配置文件={_settings.PathProfile} "
         + $"启动命令={ProcessManager.BuildArgs(_port, _settings.LaunchCommand)}";

    /// <summary>把一段文本按行缩进（写诊断日志用，读起来有层次）。</summary>
    private static string Indent(string text, string pad = "    ")
        => string.IsNullOrEmpty(text)
            ? pad + "（无输出）"
            : string.Join("\n", text.Split('\n').Select(l => pad + l.TrimEnd()));

    /// <summary>引擎退出前的输出尾部（随诊断一起给出；别人电脑上的问题靠它定位）。</summary>
    private static string EngineOutputSection()
    {
        string tail = Logger.RunOutputTail();
        return tail.Length == 0
            ? "引擎这次没有输出任何内容。\n"
            : $"引擎退出前的输出（最后几行）:\n{tail}\n";
    }

    private async Task<string> DiagnoseStartupFailure()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("启动失败自动诊断:");
        try
        {
            // 1. 启动方式与工作目录
            sb.AppendLine("ℹ️ 启动方式: " + ProcessManager.GetLaunchPreview(_port, _settings.LaunchCommand));

            // 2. pnpm 可用性（DSH 本体用 npx 启动，插件市场的一键安装仍依赖 pnpm）
            var pnpm = await RunCommandAsync("pnpm", "--version");
            if (pnpm.Success)
                sb.AppendLine($"✅ pnpm 可用: {pnpm.Output.Trim()}");
            else
                sb.AppendLine("⚠️ pnpm 不可用：不影响 DSH 启动，但插件市场的一键安装会失败");

            // 3. node 可用性
            var node = await RunCommandAsync("node", "--version");
            if (node.Success)
                sb.AppendLine($"✅ node 可用: {node.Output.Trim()}");
            else
                sb.AppendLine("❌ node 不可用");

            // 4. dsh 本体（npx 缓存）
            string npxCache = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "npm-cache", "_npx");
            bool dshCached = false;
            try
            {
                if (Directory.Exists(npxCache))
                    dshCached = Directory.EnumerateDirectories(npxCache, "dsh", SearchOption.AllDirectories)
                        .Any(p => p.Contains(Path.Combine("@deepseek-ai", "dsh"), StringComparison.OrdinalIgnoreCase));
            }
            catch { }
            sb.AppendLine(dshCached
                ? "✅ npx 缓存已存在（DSH 免下载启动）"
                : "⚠️ 无 npx 缓存：首次启动会联网下载 @latest，请保证网络可用");

            // 5. 端口状态
            if (NetworkHelper.IsPortListening(_port))
                sb.AppendLine($"⚠️ 端口 {_port} 仍被占用，尝试清理...");
            else
                sb.AppendLine($"✅ 端口 {_port} 空闲");

            // 6. 配置文件里的模块链接：指向已消失位置的联接会让引擎报「找不到模块」而永不就绪
            try
            {
                string profileRoot = Path.Combine(ProcessManager.WorkDir, "profiles");
                var broken = ProfileHealth.FindBrokenModuleLinks(profileRoot);
                sb.AppendLine(broken.Count == 0
                    ? "✅ 配置文件模块链接正常"
                    : $"⚠️ 失效的模块链接 {broken.Count} 个：{string.Join("、", broken.Select(b => b.Name))}（已尝试接回）");
            }
            catch { }
        }
        catch (Exception ex)
        {
            sb.AppendLine("诊断异常: " + ex.Message);
        }
        Logger.LogDiagnosis(sb.ToString());
        return sb.ToString();
    }

    // ══════════ 可中止命令（插件安装用）══════════
    // 装大插件可能跑很久；用户点了「安装」之后应当能反悔。这里把正在跑的 Process 留一个句柄，
    // 按钮变红显示「停止」时就结束它 —— 只杀这次命令自己的进程树，不碰引擎与守护壳。
    private Process? _runningCmd;

    /// <summary>正在跑可中止命令（供按钮判断状态）。</summary>
    internal bool HasRunningCommand => _runningCmd != null;

    /// <summary>
    /// 给这次子进程注入「放开 pnpm 供应链策略」的环境变量。
    /// 只写进这条命令的 <see cref="ProcessStartInfo.Environment"/>（子进程私有副本），不改进程全局、不写盘。
    ///
    /// 这里刻意不落盘：成功命令不留日志（用户明确要求；实测含成功命令的这类诊断把日志增大到 30 KB/份）。
    /// 返回是否注入过 —— 调用方在命令失败时用 <see cref="NoteSupplyChainRelaxOnFailure"/> 补一条证据，
    /// 于是"失败现场"里「已放开 pnpm 供应链策略…」+「命令结束…退出码=1」两条仍然成对出现
    /// （<c>SelfTest</c> 引用的正是这一对现场，见 MainWindow.xaml.cs 的历史日志摘录）。
    /// </summary>
    private static bool InjectSupplyChainRelax(ProcessStartInfo psi, string command, string args)
    {
        foreach (var kv in PluginManager.SupplyChainRelaxEnv())
            psi.Environment[kv.Key] = kv.Value;
        return true;
    }

    /// <summary>
    /// 命令失败时补记一条"本次确实放开了 pnpm 供应链策略"的证据（成功不写）。
    /// 与紧随其后的 <see cref="Logger.NoteCommandResult"/> 相邻落盘，保持现场两条成对、顺序与历史日志一致。
    /// </summary>
    private static void NoteSupplyChainRelaxOnFailure(string command, string args)
        => Logger.NoteDiagnosis(
            $"已放开 pnpm 供应链策略（环境变量，仅本次命令 {command} {args}）：{PluginManager.SupplyChainRelaxEnvLog()}");

    /// <summary>
    /// 与 RunCommandAsync 同一套实现，但把 Process 暴露出来以便中止。
    /// 安全启动：真 exe + ArgumentList，不再经 cmd /c 拼接（防 & | ^ > < %VAR% 注入）。
    /// <paramref name="relaxSupplyChainPolicy"/> = true 时给这次命令注入放开 pnpm 包龄/锁文件策略的环境变量
    /// （插件安装、更新点它；dump-config、引擎启动等一律不传，即行为与以前完全一致）。
    /// </summary>
    private async Task<(bool Success, string Output)> RunCommandCancelableAsync(string command, string args,
        string? workDir = null, int timeoutMs = 900000, bool relaxSupplyChainPolicy = false)
    {
        // 仅显示用命令行（落日志用；执行只认下面的 ArgumentList）。
        // 上游 args 来自 PluginManager.Build*Args（未加引号未转义的字符串）——执行层把它整体拆成
        // token 后逐个交给真 exe：即便某个插值带 & ，它也只是一个 token 里的普通字符，
        // 不会再被 cmd 重新解释成"第二条命令"（防护边界：本层不清洗 token 内容，只保证不拆开）。
        string display = $"{command} {args}";
        try
        {
            var psi = new ProcessStartInfo
            {
                WorkingDirectory = workDir ?? ProcessManager.WorkDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };
            // 安全启动：npx.cmd 等 shim 解引用成 node.exe + 入口 js 直启；解析不出才 cmd 兜底
            var spec = ProcessManager.BuildLaunchSpec(command, args, psi.WorkingDirectory);
            if (spec != null)
            {
                spec.ApplyTo(psi);
                psi.WorkingDirectory = workDir ?? ProcessManager.WorkDir;
            }
            else
            {
                Logger.NoteDiagnosis($"命令告警：{command} 未能解析成真可执行文件，走 cmd 兜底（/d /s /c 整体引号）");
                ProcessManager.ApplyCmdFallback(psi, display);
                psi.WorkingDirectory = workDir ?? ProcessManager.WorkDir;
            }
            bool relaxedSC = false;
            if (relaxSupplyChainPolicy || PluginManager.LooksLikePluginMutation(args))
                relaxedSC = InjectSupplyChainRelax(psi, command, args);
            using var p = Process.Start(psi);
            if (p == null) return (false, "无法启动进程");
            _runningCmd = p;

            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            await Task.WhenAll(outTask, errTask);
            bool exited = await Task.Run(() => p.WaitForExit(timeoutMs));
            _runningCmd = null;

            string output = outTask.Result;
            string err = errTask.Result;
            if (!exited)
            {
                try { p.Kill(true); } catch { }
                // 超时同样是失败，即放开了供应链策略的证据要保留（成功路径不再写，这条不能一并丢失）
                if (relaxedSC) NoteSupplyChainRelaxOnFailure(command, args);
                return (false, $"命令超过 {timeoutMs / 1000} 秒未结束，已终止。\n{Shorten(output, 600)}\n{Shorten(err, 400)}");
            }
            bool ok = p.ExitCode == 0;
            string combined = ok
                ? (string.IsNullOrWhiteSpace(output) ? err : output)
                : ShortenForError(output, 800)
                  + (string.IsNullOrWhiteSpace(err) ? "" : "\n\n错误输出：\n" + ShortenForError(err, 800))
                  + $"\n\n（退出码 {p.ExitCode}）";
                  // 同上：只有失败（退出码非 0）才落盘，成功命令不留日志；两个流按统一上限截断。
            // 保留原有的「尾部」文案（与 run 命令那条的「头尾」区分开，便于对照历史日志）。
            // 放开了供应链策略的命令失败时，先把"已放开…"那条补上，两条成对，与历史失败现场顺序一致。
            if (!ok && relaxedSC) NoteSupplyChainRelaxOnFailure(command, args);
            Logger.NoteCommandResult(
                $"可中止命令结束：{display}",
                p.ExitCode, output, err, streamLabel: "尾部");
            return (ok, combined);
        }
        catch (Exception ex)
        {
            _runningCmd = null;
            return (false, ex.Message);
        }
    }

    /// <summary>中止正在跑的安装命令（没有在跑就返回 false）。</summary>
    internal bool StopRunningCommand()
    {
        try
        {
            var p = _runningCmd;
            if (p == null) return false;
            _runningCmd = null;
            p.Kill(entireProcessTree: true);     // 只杀本次命令的进程树
            Logger.Log("已按用户要求终止正在进行的安装命令");
            return true;
        }
        catch (Exception ex) { Logger.LogError("StopRunningCommand", ex); return false; }
    }
    /// <summary>
    /// 跑一条命令行（安全启动：真 exe + ArgumentList，不再经 cmd /c 拼接 —— 见 RunCommandCancelableAsync）。
    /// timeoutMs 默认 15 秒；插件安装/更新/快照这类慢操作要显式放大（最长 10 分钟）。
    /// stdout/stderr 并发读：先读完 stdout 再读 stderr 会因缓冲区写满而互相阻塞。
    /// <paramref name="relaxSupplyChainPolicy"/> = true 时给这次命令注入放开 pnpm 包龄/锁文件策略的环境变量
    /// （插件安装、卸载、更新点它；dump-config、引擎启动等一律不传，即行为与以前完全一致）。
    /// </summary>
    private async Task<(bool Success, string Output)> RunCommandAsync(string command, string args,
        string? workDir = null, int timeoutMs = 15000, bool relaxSupplyChainPolicy = false)
    {
        // 仅显示用命令行（落日志用；执行只认下面的 ArgumentList —— 见 RunCommandCancelableAsync 的防护边界说明）。
        string display = $"{command} {args}";
        try
        {
            var psi = new ProcessStartInfo
            {
                WorkingDirectory = workDir ?? ProcessManager.WorkDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };
            // 安全启动：powershell/npx/node 等解析成真 exe 直启（powershell.exe 是真映像，
            // PATH 里按 PATHEXT 直接命中）；解析不出才 cmd 兜底
            var spec = ProcessManager.BuildLaunchSpec(command, args, psi.WorkingDirectory);
            if (spec != null)
            {
                spec.ApplyTo(psi);
                psi.WorkingDirectory = workDir ?? ProcessManager.WorkDir;
            }
            else
            {
                Logger.NoteDiagnosis($"命令告警：{command} 未能解析成真可执行文件，走 cmd 兜底（/d /s /c 整体引号）");
                ProcessManager.ApplyCmdFallback(psi, display);
                psi.WorkingDirectory = workDir ?? ProcessManager.WorkDir;
            }
            bool relaxedSC = false;
            if (relaxSupplyChainPolicy || PluginManager.LooksLikePluginMutation(args))
                relaxedSC = InjectSupplyChainRelax(psi, command, args);
            using var p = Process.Start(psi);
            if (p == null) return (false, "无法启动进程");

            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            await Task.WhenAll(outTask, errTask);

            // 必须异步等：这里是 UI 线程调用的（启动失败诊断会连调两条命令），
            // 同步 WaitForExit 会把界面连同弹窗一起冻住——现场"弹窗点不动"就是这么来的。
            bool exited = await Task.Run(() => p.WaitForExit(timeoutMs));
            string output = outTask.Result;
            string err = errTask.Result;

            if (!exited)
            {
                try { p.Kill(true); } catch { }
                // 超时同样是失败，即放开了供应链策略的证据要保留（成功路径不再写，这条不能一并丢失）
                if (relaxedSC) NoteSupplyChainRelaxOnFailure(command, args);
                return (false, $"命令超过 {timeoutMs / 1000} 秒未结束，已终止。\n{Shorten(output, 600)}\n{Shorten(err, 400)}");
            }

            bool ok = p.ExitCode == 0;
            // 失败时必须把错误输出一起带回去：pnpm 的原因几乎都写在 stderr；
            // 过去只回 stdout，即现场只看到一段正常的下载进度 + 一句"失败"，无从定位（用户已反馈）。
            // 而且必须保尾：报错行压在输出末尾，只截开头等于没截（见 ShortenForError）。
            string combined = ok
                ? (string.IsNullOrWhiteSpace(output) ? err : output)
                : ShortenForError(output, 800)
                  + (string.IsNullOrWhiteSpace(err) ? "" : "\n\n错误输出：\n" + ShortenForError(err, 800))
                  + $"\n\n（退出码 {p.ExitCode}）";
            // 四要素落盘：命令 / 工作目录 / 退出码 / 两个流各自的头尾。
                  // 只有失败（退出码非 0）才落盘：成功命令不留日志（用户明确要求 —— 实测 144 条「命令结束」里
                  // 114 条退出码=0，`--dump-config` 一次就能输出几百行，日志就是这么被写满、被增大的）。
            // 两个流都按 Logger.CommandLogMaxLines 行 / CommandLogMaxChars 字符截断（头尾各留一段）。
            // 失败仍然必须走 NoteDiagnosis 落盘 —— Logger.Log 是空实现（写入不生效），
            // 而这条正是排查"插件装不上"最需要的证据（父任务 1.3.30 复核结论）。
            // 放开了供应链策略的命令失败时，先把"已放开…"那条补上（成功不写），两条成对。
            if (!ok && relaxedSC) NoteSupplyChainRelaxOnFailure(command, args);
            Logger.NoteCommandResult(
                $"命令结束：{display}（工作目录 {psi.WorkingDirectory}）",
                p.ExitCode, output, err);
            return (ok, combined);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// 已停用（2026-09-11 与 DSH 解绑）：不再因"浏览器页面全关"就自动停引擎。
    /// 保留此方法以便需要时恢复；当前引擎/端口状态由 StatusTimer_Tick 的端口同步驱动。
    /// </summary>
    private async Task MonitorConnectionsAsync()
    {
        Logger.Log($"监听 {_port} 连接");
        int zeroCount = 0;
        while (_isRunning && _processManager != null && !_processManager.HasExited)
        {
            await Task.Delay(2000);
            int conns = NetworkHelper.GetEstablishedConnections(_port);
            if (conns == 0)
            {
                zeroCount++;
                if (zeroCount >= 3)
                {
                    Logger.Log("页面全关,退出");
                    await GracefulStopAsync();
                    return;
                }
            }
            else zeroCount = 0;
        }
    }

    private async Task GracefulStopAsync()
    {
        Logger.Log("GracefulStop");
        _stopping = true;

        // 外部引擎（例如从 bash 启动的 DSH）：只解除绑定显示，不终止其进程。
        if (_engineExternal || _processManager == null || _processManager.HasExited)
        {
            _isRunning = false;
            _isStarting = false;
            _engineExternal = false;
            // 外部引擎释放绑定后同样是"已解绑"：端口同步不该再把它改回运行中。
            //
            // 注意：为什么这不会破坏"正常情况把它改回 running"：那是另一路的行为 —— 本程序托管的
            //   引擎被终止时并不置 _unbound（见 TerminateEngineAsync 两处），端口若仍被占用，
            //   端口同步照旧把它改回"运行中（外部引擎）"，这条策略一字未动。
            //   而这里置位是必要的：本分支把 _isRunning 置了 false，若不置 _unbound，端口同步的
            //   「引擎运行 -> 端口开」分支判据（!_isRunning && !_isStarting && !_unbound && listening）
            //   会在 ≤2 秒内成立，于是刚说完"已解除绑定"就又被改回运行中；更窄的一条：
            //   若外部引擎恰在这 ≤2 秒内死亡，端口关闭前它已被改回 _isRunning=true，
            //   中间还得额外重置一次 _engineStartTime、多出一条误导性的"同步为运行中"事件。
            //   置位后端口关闭走 wasUnbound 那一路正常结算清账，不会有基线残留到下次启动。
            _unbound = true;
            SetProgress("");
            AddEvent("已解除与外部引擎的绑定（未终止它的进程）");
            RefreshStatusEvents();
            UpdateUI();
            _stopping = false;
            return;
        }

        // 面向普通用户的文案：不暴露 Ctrl+C 这类实现细节（日志里仍保留完整信息）
        SetProgress("正在退出进程...");
        UpdateUI();
        try
        {
            bool exited = await Task.Run(() => _processManager.GracefulStop(20));
            Logger.Log($"退出结果: {exited}");
            SetProgress(exited ? "已退出" : "已强制终止");
        }
        catch (Exception ex) { Logger.LogError("GracefulStop", ex); }

        RecordRunDuration();          // 正常停止也要记时长
        AddEvent("引擎已停止");
        await Task.Delay(500);
        _isRunning = false;
        _isStarting = false;
        SetProgress("");
        UpdateUI();
        _stopping = false;
    }

    private void UpdateUI()
    {
        try
        {
            if (StatusPortText != null)
                StatusPortText.Text = _port.ToString();

            if (_isRunning)
            {
                var elapsed = DateTime.Now - _engineStartTime;
                string elapsedStr = $"{elapsed.Hours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
                SetStatusDot(Color.FromRgb(0x34, 0xC7, 0x59), 0.6);
                StatusText.Text = $"引擎运行中  {elapsedStr}";
                PortText.Text = $"http://127.0.0.1:{_port}";
                if (IdleButtonPanel != null) IdleButtonPanel.Visibility = Visibility.Collapsed;
                if (RunningButtonPanel != null) RunningButtonPanel.Visibility = Visibility.Visible;
                if (LoadingButtonPanel != null) LoadingButtonPanel.Visibility = Visibility.Collapsed;
                SetAnimState("working");
                if (_trayIcon != null)
                    _trayIcon.Text = $"DSH 守护壳 · {GuardVersion.Version} · 运行中 {elapsedStr}";
            }
            else if (_isStarting)
            {
                SetStatusDot(Color.FromRgb(0xFF, 0x95, 0x00), 0.5);
                StatusText.Text = "正在启动...";
                PortText.Text = "请稍候";
                if (IdleButtonPanel != null) IdleButtonPanel.Visibility = Visibility.Collapsed;
                if (RunningButtonPanel != null) RunningButtonPanel.Visibility = Visibility.Collapsed;
                if (LoadingButtonPanel != null)
                {
                    LoadingButtonPanel.Visibility = Visibility.Visible;
                    ApplyLoadingPalette();      // 每次进入加载态都按当前主题给两态配色
                }
                SetAnimState("wake");
            }
            else
            {
                SetStatusDot(Color.FromRgb(0x48, 0x48, 0x4A), 0.3);
                StatusText.Text = "引擎未运行";
                PortText.Text = "等待启动";
                if (IdleButtonPanel != null) IdleButtonPanel.Visibility = Visibility.Visible;
                if (RunningButtonPanel != null) RunningButtonPanel.Visibility = Visibility.Collapsed;
                // 加载条也要一并收起：启动失败/超时是直接从加载态回到空闲态，
                // 少了这一句就会和「一键启动引擎」同时出现（第 42 批修的现场 bug）。
                if (LoadingButtonPanel != null) LoadingButtonPanel.Visibility = Visibility.Collapsed;
                SetAnimState("sleep");
                if (_trayIcon != null)
                    _trayIcon.Text = $"DSH 守护壳 · {GuardVersion.Version} · 引擎未运行";
            }
        }
        catch (Exception ex) { Logger.LogError("UpdateUI", ex); }
    }

    /// <summary>端口检测节流计数（每 2 次 tick 查一次端口）。</summary>
    private int _tickCount;

    private void SetStatusDot(Color color, double glowOpacity)
    {
                StatusDot.Background = new SolidColorBrush(color);
        if (StatusDot.Effect is DropShadowEffect glow)
        {
            glow.Color = color;
            glow.Opacity = glowOpacity;
        }
    }

    /// <summary>
    /// 每秒刷新运行时长；每 2 次 tick 做一次端口状态同步（引擎运行即端口开，引擎关闭即端口关）。
    /// 端口是与引擎解绑后的唯一观测来源。
    /// </summary>
    private void StatusTimer_Tick(object? sender, EventArgs e)
    {
        try
        {
            _tickCount++;

            // 运行时长计时：每 30 秒把这一段的秒数记进版本履历并落盘。
            // 只在停止/关壳时结算会因程序被强制结束而整段丢失（此前履历里长期是 0 秒）。
            if (_isRunning && _runStartAt != DateTime.MinValue && _tickCount % 30 == 0)
                OnRunSecondsSettled();

            if (_tickCount % 2 == 0)
            {
                bool listening = NetworkHelper.IsPortListening(_port);

                // ① 引擎关闭即端口关：本程序启动的引擎进程已退出
                if (_isRunning && !_engineExternal && _processManager != null && _processManager.HasExited)
                {
                    _isRunning = false;
                    SetProgress("");
                    AddEvent("引擎进程已退出（端口随之关闭）");
                    RefreshStatusEvents();
                    UpdateUI();
                    Logger.Log("进程已退出");
                    HandlePrematureExit();
                    return;
                }

                // ② 引擎运行即端口开：端口在监听而本程序没在运行，判为外部引擎，同步为运行态
                //（已「只解绑」时不接管：引擎继续跑，但本程序保持空闲，直到用户重新点启动或端口关闭）
                if (!_isRunning && !_isStarting && !_unbound && listening)
                {
                    _engineExternal = true;
                    _isRunning = true;
                    _engineStartTime = DateTime.Now;
                    // 纯 tick 发现的外部引擎（用户从 bash 起好、守护壳后开，或引擎停掉之后端口又起来）
                    //   从来没经过 StartEngineAsync 的外部分支，也就没有给它起表，即本次运行一秒都不计
                    //   （30 秒结算点的判据要求 _runStartAt != MinValue），履历里的累计运行时长长期小于实际值。
                    //   这里补起表：履历基线 + 连续运行累计 + 「自动-时间」门槛三套账本一起开始，与
                    //   StartEngineAsync/RecordEngineReady 起表时的语义完全一致。
                    //   注意：与 RecordEngineReady 的守卫（if (_runStartAt == DateTime.MinValue) BeginRunClock()）
                    //     不会打架：两者都是"缺了才起"，先到的那次起表、后到的那次直接跳过，
                    //     任何调用顺序下都只会起一次表、不会重复清零。本分支只置状态、不调 RecordEngineReady。
                    //   注意：判据比那条守卫多一个 !_unbound：两处必须一起判——
                    //     · _runStartAt == MinValue：账本关着（纯 tick 发现，本次必备的那一条）；
                    //     · !_unbound：引擎不是"刚被解绑"回来的。
                    //   为什么要后者：用户点「只解绑」后引擎继续在跑、账本也照旧开着（那是本次运行的真实时长，
                    //   不能抹），但它一旦停机再起来就是新的一次运行了。此时仅看 _runStartAt 会误以为
                    //   "账本还开着"而跳过起表，于是新的一次运行沿用上一次的旧基线 —— 又把停机期和旧时长记到头上，
                    //   正是本单要根除的那类残留。已解绑，即这条运行不归本程序管，起表就从"现在"重新数。
                    if (_runStartAt == DateTime.MinValue || _unbound) BeginRunClock();
                    AddEvent($"端口 {_port} 已在监听 → 同步为运行中（外部引擎）");
                    RefreshStatusEvents();
                    UpdateUI();
                    return;
                }

                // ③ 引擎关闭即端口关：本程序以为在运行，但端口已不监听，同步为未运行
                if ((_isRunning || _unbound) && !listening)
                {
                    bool wasExternal = _engineExternal;
                    bool wasUnbound = _unbound;
                    _isRunning = false;
                    _engineExternal = false;
                    _readyUrl = "";          // 引擎已停：token 地址作废，别留着下次误用
                    _unbound = false;        // 引擎没了，"解绑"状态自动结束
                    SetProgress("");
                    _terminateAttempted = false;   // 端口已空闲，复位强杀档
                AddEvent($"端口 {_port} 已关闭 → 同步为未运行");
                    RefreshStatusEvents();
                    UpdateUI();
                    // 外部引擎（或已"解绑"的引擎）停了：不算本程序启动的异常，所以不走 HandlePrematureExit，
                    // 但本次运行同样到此结束 —— 账本必须在这里结算并全部清理。否则 _runStartAt 一直残留：
                    // 端口再次监听时状态计时器只置 _isRunning = true 而不重新起表，下一个 30 秒结算点
                    // 就会把"两次运行之间的停机期"当成运行时长补进版本履历（跑得越久错得越多）。
                    if (wasExternal || wasUnbound) SettleRunClock();
                    else HandlePrematureExit();
                    return;
                }
            }

            if (_isRunning) UpdateUI(); // 每秒刷新运行时长
        }
        catch (Exception ex) { Logger.LogError("StatusTimer_Tick", ex); }
    }

    private static string GetExePath()
        => Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";

    // ═══ 日间 / 夜间模式 ═══

    /// <summary>切换日间/夜间模式。</summary>
    private void ThemeToggle_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            ApplyTheme(!ThemeManager.IsDark, save: true, announce: true);
            e.Handled = true;
        }
        catch (Exception ex) { Logger.LogError("ThemeToggle_Click", ex); }
    }

    /// <summary>
    /// 重写窗口根底色的三段渐变：毛玻璃开着用半透明（让系统材质透出来），关掉时换成不透明版本。
    /// 主题映射表只认半透明那组，所以这里每次刷主题都按当前状态重写一遍（幂等）。
    /// </summary>
    private void ApplyWindowBackdrop()
    {
        try
        {
            if (RootBorder?.Background is LinearGradientBrush lg && lg.GradientStops.Count >= 3)
            {
                var stops = ThemeManager.WindowStops(ThemeManager.IsDark, _acrylicOn);
                for (int i = 0; i < 3; i++) lg.GradientStops[i].Color = stops[i];
            }
        }
        catch (Exception ex) { Logger.LogError("ApplyWindowBackdrop", ex); }
    }

    /// <summary>
    /// 开关轨道配色：只按「开 / 关 与 当前主题」给色，不参与颜色映射表。
    /// 模板里原来用颜色动画切换轨道色，动画时钟会一直挂在画刷上，而主题换色对"带动画的画刷"
    /// 是跳过的——三个开关因此在日间模式下仍是夜间深灰（现场 bug）。改成由代码直接给色。
    /// </summary>
    private void PaintSwitch(System.Windows.Controls.Primitives.ToggleButton? sw, bool? state = null)
    {
        try
        {
            if (sw == null) return;
            // 模板可能还没套用（先刷主题后布局的时序），先套上再取轨道，否则这一次设置会丢失
            if (sw.Template?.FindName("Track", sw) is not Border) sw.ApplyTemplate();
            if (sw.Template?.FindName("Track", sw) is not Border track) return;
            Color target = ThemeManager.SwitchColor(state ?? sw.IsChecked == true);
            Color from = (track.Background as SolidColorBrush)?.Color ?? target;
            var brush = new SolidColorBrush(target);
            track.Background = brush;
            if (from != target)
                // 基础色已经是目标色，过渡结束后自动回落（FillBehavior.Stop），不会把画刷钉住
                brush.BeginAnimation(SolidColorBrush.ColorProperty,
                    new ColorAnimation(from, target, TimeSpan.FromMilliseconds(180)) { FillBehavior = FillBehavior.Stop });
        }
        catch (Exception ex) { Logger.LogError("PaintSwitch", ex); }
    }

    /// <summary>三个开关一起重上色（刷主题时调用）。</summary>
    private void PaintAllSwitches()
    {
        PaintSwitch(AutoStartToggle);
        PaintSwitch(AutoBrowseToggle);
        PaintSwitch(AcrylicToggle);
    }

    private void Switch_Changed(object sender, RoutedEventArgs e) => PaintSwitch(sender as System.Windows.Controls.Primitives.ToggleButton);

    /// <summary>把界面刷成目标主题：视觉树换色 + 毛玻璃着色 + 图标 + 落盘。</summary>
    private void ApplyTheme(bool dark, bool save = false, bool announce = false)
    {
        try
        {
            ThemeManager.Apply((DependencyObject)Content, dark);
            // 打开中的非模态弹窗不在 Content 这棵树上（只设了 Owner），上面那趟从根遍历走不到它，即
            // 必须单独按新主题补刷一次，否则弹窗开着时切主题它保持旧配色（下次重开才对）。
            // 模态/伪模态窗未登记（它们开着时主窗点不动，用户点不到主题按钮），详见 GuardDialog。
            GuardDialog.ReskinNonModalOpen();
            // 同理，游离弹层（批量功能框）也不在这棵树上：它只设了 PlacementTarget、从没加进任何 Children 集合。
            // 它打开时靠 HookPopupTheme 的 Opened 钩子刷一次；开着的时候主题再变就得靠这份登记补刷
            // （弹层内容建一次长期复用，见 ThemeManager.RegisterOrphanPopup）。
            ThemeManager.ReskinOrphanPopups();
            ApplyWindowBackdrop();                      // 根底色跟着主题与毛玻璃状态一起换
            PaintAllSwitches();                         // 开关轨道色不参与映射表，单独上色
            ButtonFx.Wire((DependencyObject)Content);   // 每次应用主题时一并把新出现的可点元素挂上动效（幂等）
            ApplyMascot();                              // 口头禅用自己的随机色，刷主题后重新盖上，避免被主题改灰
            _loadingIdleBg = null;      // 底色由主题重新给，清掉上一主题记住的值
            ApplyLoadingPalette();

            // 毛玻璃着色同步切换：日间使用浅色，深色在浅色底上会发灰。
            if (_acrylicOn)
            {
                var (a, r, g, b) = ThemeManager.Tint;
                BlurHelper.SetTint(this, a, r, g, b);
        ForceRecompose("切主题");
            }

            // 图标：夜间显示月亮（点击切到日间），日间显示太阳。
            if (ThemeIcon != null) ThemeIcon.Text = dark ? "\uE708" : "\uE706";
            if (ThemeBtn != null)
                ThemeBtn.ToolTip = dark ? "切换日间模式" : "切换夜间模式";

            // 左上角头像随主题切换：夜间用浅色图、日间用深色图。
            // 两张图均为透明底；整块不透明底图无法与半透明窗口背景对齐，透明底可直接落在界面底色上。
            try
            {
                if (LogoImage != null)
                {
                    string file = dark ? "dsh-logo-dark.png" : "dsh-logo-light.png";
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    // 两张图都内嵌成 Resource，用 pack URI 取；不依赖部署目录里有没有 Assets 文件夹
                    bmp.UriSource = new Uri($"pack://application:,,,/Assets/{file}", UriKind.Absolute);
                    bmp.EndInit();
                    LogoImage.Source = bmp;
                }
            }
            catch (Exception ex) { Logger.LogError("ApplyTheme(logo)", ex); }

            if (save)
            {
                _settings.Theme = dark ? 0 : 1;
                _settings.Save();
                // 先取结果再报：主题没写进盘时，下次启动会回到原来的主题
                var themeOutcome = SettingsOutcome(dark ? "已切换到夜间模式" : "已切换到日间模式");
                AddEvent(themeOutcome.Text, themeOutcome.Kind);
            }
            _ = announce;
        }
        catch (Exception ex) { Logger.LogError("ApplyTheme", ex); }
    }

    /// <summary>动态新建的控件要补刷主题（切页/重渲染后调一次，合并到一次 Dispatcher 里）。</summary>
    private bool _themeQueued;
    internal void ApplyThemeSoon()
    {
        if (_themeQueued) return;
        _themeQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _themeQueued = false;
            if (_acrylicOn || !ThemeManager.IsDark) ApplyTheme(ThemeManager.IsDark);
        }), DispatcherPriority.Loaded);
    }

    private void ThemeBtn_MouseEnter(object sender, MouseEventArgs e)
    {
        try
        {
            if (ThemeBtn != null)
            {
                var brush = new SolidColorBrush(((SolidColorBrush)ThemeBtn.Background).Color);
                ThemeBtn.Background = brush;
                brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation
                {
                    To = Color.FromRgb(0x5A, 0xC8, 0xFA), Duration = TimeSpan.FromMilliseconds(160)
                });
            }
            if (ThemeIcon != null)
            {
                ThemeIcon.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(80)));
                ThemeIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x0B, 0x2B, 0x3A));
                if (ThemeIcon.RenderTransform is ScaleTransform st)
                {
                    var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 };
                    st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.25, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
                    st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.25, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
                }
            }
        }
        catch (Exception ex) { Logger.LogError("ThemeBtn_MouseEnter", ex); }
    }

    private void ThemeBtn_MouseLeave(object sender, MouseEventArgs e)
    {
        try
        {
            // 静息态为透明，与三个窗口键一致，悬停结束后回到透明。
            if (ThemeBtn != null)
            {
                var brush = new SolidColorBrush(((SolidColorBrush)ThemeBtn.Background).Color);
                ThemeBtn.Background = brush;
                brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation
                {
                    To = Colors.Transparent, Duration = TimeSpan.FromMilliseconds(160)
                });
            }
            if (ThemeIcon != null)
            {
                ThemeIcon.Foreground = new SolidColorBrush(ThemeManager.IsDark ? Color.FromRgb(0xCF, 0xCF, 0xD6) : Color.FromRgb(0x3A, 0x3A, 0x3C));
                if (ThemeIcon.RenderTransform is ScaleTransform st)
                {
                    var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 };
                    st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
                    st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
                }
            }
        }
        catch (Exception ex) { Logger.LogError("ThemeBtn_MouseLeave", ex); }
    }

    // ═══ 自检钩子（只给 SelfTest 用，正常运行路径不受影响）═══
    internal void SetProgressForTest(string text) => SetProgress(text);
    internal void TickForTest() => LoadingTimer_Tick(null, EventArgs.Empty);
    internal static void AnimateTrafficForTest(Border host, bool hover) => AnimateTraffic(host, hover);
    internal Border BuildMarketCardForTest(PluginMarket.MarketPlugin m) => BuildMarketCard(m);
    internal void ShowPluginsTabForTest(bool market) => ShowPluginsTab(market);
    internal void PrimeMarketForTest(PluginMarket.MarketCatalog cat) => _market = cat;
    internal Border BuildInstalledCardForTest(PluginManager.Plugin p) => BuildPluginCard(p);

    // ── 筛选下拉 / 分类展开 / 截图（自检用；e 允许传 null）──
    internal string FilterLabelForTest => MarketFilterText.Text;
    internal int MarketListCountForTest => _lastMarketListCount;
    internal int InstalledPluginCountForTest => _plugins.Count;
    /// <summary>自检用：造一条事件并刷新「事件信息」，然后读它的字色（验证红/绿分级）。</summary>
    internal string EventColorForTest(string text, bool bad)
    {
        AddEvent(text, bad ? EventKind.Bad : EventKind.Good);
        RefreshStatusEvents();
        foreach (var child in StatusEventsPanel.Children.OfType<TextBlock>())
            if (child.Text.Contains(text, StringComparison.Ordinal))
                return (child.Foreground as SolidColorBrush)?.Color.ToString() ?? "";
        return "";
    }
    internal void ShowViewForTest(string view)
        => ShowView(view switch
        {
            "logs" => GuardView.Logs,
            "snapshots" => GuardView.Snapshots,
            "plugins" => GuardView.Plugins,
            "settings" => GuardView.Settings,
            "about" => GuardView.About,
            _ => GuardView.Status
        });
    internal void ShowSettingsTabForTest(string tab)
        => ShowSettingsPage(tab switch
        {
            "paths" => SettingsTab.Paths,
            "version" => SettingsTab.Version,
            _ => SettingsTab.General
        });
    internal void OpenFilterMenuForTest()
    {
        MarketFilterPopup.IsOpen = true;
        PaintFilterMenu();
    }
    internal Task RefreshPluginsForTestAsync() => RefreshPluginsAsync(true);

    // ── 第 40 批自检钩子：筛选弹层 ──
    /// <summary>自检用：模拟鼠标移入/移出下拉选项行。</summary>
    internal void HoverMenuRowForTest(Border row, bool enter)
    {
        if (row == null) return;
        if (enter) row.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
        else row.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });
    }

    /// <summary>自检用：确保窗口真的显示过（Popup 需要可见的放置目标才能打开）。</summary>
    internal void EnsureShownForTest()
    {
        try
        {
            if (!IsVisible) Show();
            UpdateLayout();
        }
        catch { }
    }

    /// <summary>自检用：打开插件页的筛选弹层（不切页面、不泵消息，保证读回时仍是打开的）。</summary>
    internal void OpenFilterPopupForTest()
    {
        EnsureShownForTest();
        MarketFilterPopup.IsOpen = true;
        PaintFilterMenu();
    }

    /// <summary>自检用：把窗口恢复成"未显示"状态（后续用例依赖它没有句柄）。</summary>
    internal void HideAfterTest()
    {
        try { CloseFilterPopups(); Hide(); } catch { }
    }

    internal bool FilterPopupOpenForTest => MarketFilterPopup.IsOpen;
    internal void CloseFilterPopupsForTest() => CloseFilterPopups();

    // ── 第 41 批自检钩子：毛玻璃开关 ──
    internal bool AcrylicSettingForTest => _settings.AcrylicEnabled;
    internal void SetAcrylicSettingForTest(bool on)
    {
        _settings.AcrylicEnabled = on;
        _settings.Save();
    }

    /// <summary>自检用：直接设定毛玻璃运行状态并重刷界面（不发真实 DWM 调用）。</summary>
    internal void SetAcrylicStateForTest(bool on)
    {
        _acrylicOn = on;
        ApplyTheme(ThemeManager.IsDark);
    }

    /// <summary>自检用：窗口根渐变三段的实际颜色（含 alpha，逗号分隔）。</summary>
    internal string RootBackdropForTest()
    {
        if (RootBorder?.Background is not LinearGradientBrush lg) return "";
        return string.Join(",", lg.GradientStops.Select(s => s.Color.ToString()));
    }

    /// <summary>自检用：指定开关在给定状态下的轨道色（只上色，不触发开关自身的副作用）。</summary>
    internal string SwitchTrackColorForTest(string name, bool on)
    {
        if (FindName(name) is not System.Windows.Controls.Primitives.ToggleButton sw) return "";
        PaintSwitch(sw, on);
        return (sw.Template?.FindName("Track", sw) as Border)?.Background is SolidColorBrush sb
            ? sb.Color.ToString() : "";
    }

    /// <summary>自检用：左侧导航七项里，图标+文字被水平居中的条数（应为 0：导航保持左对齐）。</summary>
    internal int NavLabelCenteredCountForTest()
    {
        int centered = 0;
        foreach (string name in new[] { "NavStatus", "NavLogs", "NavSnapshots", "NavPlugins", "NavSettings", "NavAbout", "NavExit" })
            if (FindName(name) is Border b && b.Child is FrameworkElement fe &&
                fe.HorizontalAlignment == HorizontalAlignment.Center)
                centered++;
        return centered;
    }

    /// <summary>自检用：状态行的「圆点 + 状态文字」是不是居中排的（应为否，要和其他行一样左对齐）。</summary>
    internal bool StatusRowCenteredForTest()
        => StatusText?.Parent is StackPanel sp && sp.HorizontalAlignment == HorizontalAlignment.Center;

    /// <summary>自检用：当前状态文字。</summary>
    internal string StatusTextForTest() => StatusText?.Text ?? "";

    // ── 第 12 批自检钩子 ──
    /// <summary>「事件信息」是不是加一条就立刻出现在面板上（不用切页面）。</summary>
    internal bool LiveEventAppearsForTest()
    {
        ShowViewForTest("status");
        AddEvent("自检-实时事件", EventKind.Good);
        // AddEvent 内部通过 BeginInvoke 刷新，此处等待其执行。
        Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        foreach (var child in StatusEventsPanel.Children.OfType<TextBlock>())
            if (child.Text.Contains("自检-实时事件", StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>模拟点卡片上的分类链接（走真实处理逻辑）。</summary>
    internal void ClickCategoryLinkForTest(string slug) => MarketCategoryLink_Click(new TextBlock { Tag = slug, Text = slug }, null!);

    internal string CurrentCategoryForTest => _marketCategory;

    // ── 第 13 批自检钩子 ──
    internal void ApplyThemeForTest(bool dark) => ApplyTheme(dark);

    /// <summary>当前可见的「回到顶部」按钮数量（应该 ≤ 1）。</summary>
    internal int TopButtonVisibilityCountForTest()
        => (MarketTopBtn?.Visibility == Visibility.Visible ? 1 : 0)
         + (InstalledTopBtn?.Visibility == Visibility.Visible ? 1 : 0);

    /// <summary>在插件页加一条事件，检查「事件信息」面板是否立即出现。</summary>
    internal bool LiveEventAppearsOnPluginPageForTest()
    {
        ShowViewForTest("plugins");
        AddEvent("自检-插件页实时事件", EventKind.Good);
        Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        foreach (var child in StatusEventsPanel.Children.OfType<TextBlock>())
            if (child.Text.Contains("自检-插件页实时事件", StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>某个元素的底色（16 进制）；渐变就取第一段。</summary>
    internal string BoxColorForTest(string elementName)
    {
        if (FindName(elementName) is not Border b) return "";
        return b.Background switch
        {
            SolidColorBrush sb => sb.Color.ToString(),
            LinearGradientBrush lg when lg.GradientStops.Count > 0 => lg.GradientStops[0].Color.ToString(),
            _ => ""
        };
    }

    /// <summary>某个元素（或它第一个 TextBlock 子元素）的文字颜色。</summary>
    internal string TextColorForTest(string elementName)
    {
        if (FindName(elementName) is not FrameworkElement fe) return "";
        if (fe is TextBlock t) return (t.Foreground as SolidColorBrush)?.Color.ToString() ?? "";
        if (fe is Control c) return (c.Foreground as SolidColorBrush)?.Color.ToString() ?? "";
        if (fe is Border b && b.Child is TextBlock ct) return (ct.Foreground as SolidColorBrush)?.Color.ToString() ?? "";
        return "";
    }

    /// <summary>日/夜切换按钮的（静息、悬停）底色。</summary>
    internal (string idle, string hover) ThemeBtnColorsForTest()
    {
        string idle = (ThemeBtn.Background as SolidColorBrush)?.Color.ToString() ?? "";
        ThemeBtn_MouseEnter(ThemeBtn, null!);
        string hover = (ThemeBtn.Background as SolidColorBrush)?.Color.ToString() ?? "";
        // 触发 Enter 后颜色为动画值，直接返回已知的悬停色。
        ThemeBtn_MouseLeave(ThemeBtn, null!);
        return (idle, "#FF5AC8FA");
    }

    /// <summary>验证日间模式下的文字对比度：半透明底上的文字需变深，彩色按钮上的文字保持白色。</summary>
    internal (string onTranslucent, string onAccent, string cardBg) ThemeContrastForTest()
    {
        var host = new StackPanel();
        var plainBtn = new Button
        {
            Content = "x",
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF))
        };
        RoundBtn.Apply(plainBtn);   // 统一圆角外观
        var accentBtn = new Button
        {
            Content = "y",
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x59))
        };
        RoundBtn.Apply(accentBtn);   // 统一圆角外观
        var card = new Border { Background = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)) };
        host.Children.Add(plainBtn);
        host.Children.Add(accentBtn);
        host.Children.Add(card);

        bool wasDark = ThemeManager.IsDark;
        ThemeManager.Apply(host, dark: false);      // 刷成日间
        string onTranslucent = (plainBtn.Foreground as SolidColorBrush)?.Color.ToString() ?? "";
        string onAccent = (accentBtn.Foreground as SolidColorBrush)?.Color.ToString() ?? "";
        string cardBg = (card.Background as SolidColorBrush)?.Color.ToString() ?? "";
        ThemeManager.Apply(this, wasDark);          // 还原
        return (onTranslucent, onAccent, cardBg);
    }

    /// <summary>同一主题连续应用两次时颜色应保持不变，用于验证映射幂等。返回两次应用后的颜色。</summary>
    internal (string first, string second) ThemeIdempotentForTest()
    {
        var host = new StackPanel();
        var label = new TextBlock { Text = "x", Foreground = new SolidColorBrush(Color.FromRgb(0xCF, 0xCF, 0xD6)) };
        var card = new Border { Background = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)) };
        var track = new Border { Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3C)) };
        host.Children.Add(label);
        host.Children.Add(card);
        host.Children.Add(track);

        bool wasDark = ThemeManager.IsDark;
        ThemeManager.Apply(host, dark: false);          // 第一遍：夜间改为日间
        string first = $"{label.Foreground}|{card.Background}|{track.Background}";
        ThemeManager.Apply(host, dark: false);          // 第二遍：应当什么都不变
        string second = $"{label.Foreground}|{card.Background}|{track.Background}";
        ThemeManager.Apply(this, wasDark);
        return (first, second);
    }

    /// <summary>
    /// 把日志来源下拉渲染成位图，返回最亮像素的亮度。
    /// 深色底 + 浅色字时应接近 255；若模板未设置 Foreground（文字用系统默认黑），该值会明显偏低。
    /// </summary>
    internal int LogComboBrightestPixelForTest()
    {
        try
        {
            if (LogFilePicker == null) return -1;
            LogFilePicker.UpdateLayout();
            int w = (int)Math.Max(1, LogFilePicker.ActualWidth);
            int h = (int)Math.Max(1, LogFilePicker.ActualHeight);
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(LogFilePicker);
            int stride = w * 4;
            var buf = new byte[stride * h];
            rtb.CopyPixels(buf, stride, 0);
            byte best = 0;
            for (int i = 0; i < buf.Length; i += 4)
            {
                byte b = buf[i], g = buf[i + 1], r = buf[i + 2], a = buf[i + 3];
                if (a < 128) continue;
                // 感知亮度（Rec.601 加权）
                int lum = (r * 30 + g * 59 + b * 11) / 100;
                if (lum > best) best = (byte)lum;
            }
            return best;
        }
        catch (Exception ex) { Logger.LogError("LogComboBrightestPixelForTest", ex); return -1; }
    }

    /// <summary>自检用：执行一次快照刷新。</summary>
    /// <summary>自检用：加载进度条的底色 / 模拟悬停进出。</summary>
    internal string LoadingPanelBgForTest()
        => (LoadingButtonPanel?.Background as SolidColorBrush)?.Color.ToString() ?? "";

    internal void LoadingHoverForTest(bool enter)
    {
        if (enter) LoadingBtn_MouseEnter(LoadingButtonPanel!, null!);
        else LoadingBtn_MouseLeave(LoadingButtonPanel!, null!);
    }

    /// <summary>自检用：「一键更新」按钮是否可见 / 当前有几个可更新插件。</summary>
    internal bool UpdateAllVisibleForTest()
        => UpdateAllText?.Parent is Border b && b.Visibility == Visibility.Visible;

    /// <summary>
    /// 自检用：当前有几个可更新插件。
    /// 必须走 <see cref="CountUpdatablePlugins"/>（= <c>UpdatableCount()</c> 的表达式），
    ///   不得在这里另数一份裸 <c>HasUpdate</c>：自检的职责是盯住产品口径，
    ///   断言自己造一个口径就会出现"自检说 A、产品做 B"—— 那正是这一处原先的缺陷（「可选升级」被多算）。
    /// </summary>
    internal int UpdatableCountForTest() => CountUpdatablePlugins(_plugins, UpdateOf);

    /// <summary>版本页里「切换到它」按钮的数量（每个上一长期版本一个，可互相切换）。</summary>
    internal int VersionSwitchButtonCountForTest()
    {
        int n = 0;
        try
        {
            void Walk(DependencyObject o)
            {
                if (o is Button b && b.Content is string s && s.Contains("切换到它")) n++;
                int c = VisualTreeHelper.GetChildrenCount(o);
                for (int i = 0; i < c; i++) Walk(VisualTreeHelper.GetChild(o, i));
            }
            if (VersionPanel != null) Walk(VersionPanel);
        }
        catch { }
        return n;
    }

    /// <summary>版本页「策略开关」按钮的文案与底色（自检用）。</summary>
    internal (string Label, string Bg) FollowButtonForTest()
    {
        (string, string) found = ("", "");
        try
        {
            void Walk(DependencyObject o)
            {
                if (o is Button b && b.Content is string s && (s == "跟随最新版" || s == "自动管理"))
                    found = (s, (b.Background as SolidColorBrush)?.Color.ToString() ?? "");
                int c = VisualTreeHelper.GetChildrenCount(o);
                for (int i = 0; i < c; i++) Walk(VisualTreeHelper.GetChild(o, i));
            }
            if (VersionPanel != null) Walk(VersionPanel);
        }
        catch { }
        return found;
    }

    /// <summary>把从 _runStartAt 到现在的运行时长记进版本履历，并把基线前移。</summary>
    private int AccumulateRunSeconds()
    {
        if (_runStartAt == DateTime.MinValue) return 0;
        int ranSec = (int)(DateTime.Now - _runStartAt).TotalSeconds;
        if (ranSec <= 0) return 0;
        VersionMemory.AddRunSeconds(_runVersion, ranSec);
        _runStartAt = DateTime.Now;
        // 「自动-时间」的累计计时同步前移基线（两套账本各记各的，互不干扰）
        _engRunStartAt = _runStartAt;
        return ranSec;
    }

    /// <summary>
    /// 每 30 秒的运行结算点：先记版本履历，再累加"本次连续运行的累计秒数"，最后判「自动-时间」该不该存。
    ///
    /// 用户给的触发点是"引擎运行时间达到 1 小时"，判据必须是累计运行秒数；
    /// 而结算点本身每 30 秒就把 <see cref="_runStartAt"/> 前移一次，
    /// 所以累计值另由 <see cref="_engRunSeconds"/> 承担（见那两个字段的说明）。
    /// </summary>
    private void OnRunSecondsSettled()
    {
        try
        {
            AccumulateRunSeconds();

            // 累计本次运行秒数：起点缺失（外部引擎同步进来、还没起过表）就先起表，本次增量为 0
            if (_engRunStartAt == DateTime.MinValue) _engRunStartAt = DateTime.Now;
            long delta = (long)(DateTime.Now - _engRunStartAt).TotalSeconds;
            _engRunStartAt = DateTime.Now;
            if (delta > 0) _engRunSeconds += delta;

            MaybeTakeTimedSnapshot();
        }
        catch (Exception ex) { Logger.LogError("OnRunSecondsSettled", ex); }
    }

    /// <summary>
    /// 本次运行开始计时：版本履历基线 / 连续运行累计 / 「自动-时间」门槛三套账本一起起表。
    /// 引擎每次启动都要调它 —— 重新启动后运行时长必须从 0 重新数。
    /// </summary>
    private void BeginRunClock()
    {
        _runStartAt = DateTime.Now;
        _engRunStartAt = _runStartAt;
        _engRunSeconds = 0;
        _lastTimedSnapshotAtSeconds = 0;
    }

    /// <summary>
    /// 本次运行结束：连续运行累计与「自动-时间」门槛一起清零（版本履历基线由调用方负责）。
    /// 只由 <see cref="SettleRunClock"/> 调用；<see cref="EndRunClockForTest"/> 是自检入口。
    /// </summary>
    private void EndRunClock()
    {
        _engRunStartAt = DateTime.MinValue;
        _engRunSeconds = 0;
        _lastTimedSnapshotAtSeconds = 0;
    }

    /// <summary>
    /// 本次运行的收尾（三条停止路径共用的唯一入口）：先把「距上次结算的这一段（尾段）」记进版本履历，
    /// 再把本次运行的账本全部清干净 —— 版本履历基线 <see cref="_runStartAt"/>、
    /// 连续运行累计 <see cref="_engRunStartAt"/> / <see cref="_engRunSeconds"/>、「自动-时间」门槛
    /// <see cref="_lastTimedSnapshotAtSeconds"/>。返回结算掉的尾段秒数（0 = 没有开着的这一段）。
    ///
    /// 必须走的路径：
    ///   · 进程退出 -> <see cref="HandlePrematureExit"/> -> <see cref="RecordRunDuration"/>；
    ///   · 用户点「终止引擎」成功（第一档断端口成功 / 第二档强杀成功）；
    ///   · 端口关闭且是外部引擎或已"解绑"的引擎 —— 这种不算启动异常、不走 HandlePrematureExit，
    ///     但账本同样要清：否则 <see cref="_runStartAt"/> 残留，端口再次监听时状态计时器只置
    ///     <c>_isRunning = true</c> 而不重新起表，下一个 30 秒结算点就把"停机期"当成运行时长补进履历
    ///     （跑得越久错得越多，反复外部启停还会叠加）。
    ///
    /// 幂等：本方法把 <see cref="_runStartAt"/> 与 <see cref="_engRunStartAt"/> 一律置
    /// <see cref="DateTime.MinValue"/>，而这两个字段正是"账本是否还开着"的唯一判据 ——
    /// 重复调用（例如终止分支与随后的状态计时器各调一次）第二次必然空转，不会重复记账。
    ///
    /// 记账只走 <see cref="VersionMemory.NoteEngineStopped"/> 一条（与既有停止路径同一入口，
    /// 语义完全不变）：它和 <see cref="AccumulateRunSeconds"/> 用的 <c>AddRunSeconds</c> 都是往
    /// 同一个 <c>RunsSeconds</c> 上累加，两个一起调就是双记 —— 所以这里只结算，不再另行累加。
    /// </summary>
    private int SettleRunClock()
    {
        try
        {
            if (_runStartAt == DateTime.MinValue)
            {
                // 没有开着的这一段：仍要把「自动-时间」的账本清干净（引擎真停了，重启后要从 0 重新数）
                EndRunClock();
                return 0;
            }

            int ranSec = (int)(DateTime.Now - _runStartAt).TotalSeconds;   // 尾段：到"检测到停止"的这一刻为止
            _runStartAt = DateTime.MinValue;      // 只结算一次：先关账本，之后无论怎么重入都空转
            EndRunClock();
            if (ranSec <= 0) return 0;

            VersionMemory.NoteEngineStopped(ranSec, _runVersion.Length > 0 ? _runVersion : null);
            return ranSec;
        }
        catch (Exception ex) { Logger.LogError("SettleRunClock", ex); return 0; }
    }

    /// <summary>
    /// 「自动-时间」：引擎每连续运行满 1 小时自动存一份快照（长时间挂机也有多个还原点）。
    /// 判定用纯函数 <see cref="SnapshotManager.ShouldTakeTimedSnapshot"/>（自检盯着它的边界与不重复）。
    /// 后台静默：不弹任何窗，只在「事件信息」里记一条中性事件（档位与"保存快照"同类）。
    /// </summary>
    private void MaybeTakeTimedSnapshot()
    {
        try
        {
            if (!SnapshotManager.ShouldTakeTimedSnapshot(_engRunSeconds, _lastTimedSnapshotAtSeconds)) return;
            // 无论成败都推进门槛：否则失败后每 30 秒都会重试一次（本档是自动动作，静默即可）
            _lastTimedSnapshotAtSeconds =
                SnapshotManager.TimedSnapshotNextMark(_engRunSeconds, _lastTimedSnapshotAtSeconds);

            long hours = _engRunSeconds / SnapshotManager.RunSecondsPerHour;
            string reason = $"引擎已连续运行 {hours} 小时（自动）";
            var snap = SnapshotManager.Create(SnapshotManager.KindTimed, reason);
            if (snap == null)
            {
                AddEvent("自动快照（时间）保存失败（可到「日志」页查看原因）", EventKind.Warn);
                return;
            }
            // 中性、正式的中文，句式与「已创建快照备份（…）」同族；自动动作不打扰用户
            AddEvent($"已创建快照备份（{reason}，{snap.RestorableCount} 个文件）", EventKind.Update);
            // 快照页正开着就把新快照显示出来；没开就不做多余渲染
            if (_currentView == GuardView.Snapshots) RefreshSnapshots();
        }
        catch (Exception ex) { Logger.LogError("MaybeTakeTimedSnapshot", ex); }
    }

    /// <summary>自检用：模拟计时器结算（把基线回拨 seconds 秒后结算一次）。</summary>
    internal int AccumulateRunSecondsForTest(int seconds)
    {
        if (_runVersion.Length == 0)
            _runVersion = VersionMemory.Pin.Length > 0 ? VersionMemory.Pin : _currentDshVersion;
        _runStartAt = DateTime.Now.AddSeconds(-seconds);
        return AccumulateRunSeconds();
    }

    /// <summary>自检用：跑一遍快照刷新（顺带验证新内容是不是当场就按当前主题刷好了）。</summary>
    internal void RefreshSnapshotsForTest() => RefreshSnapshots();

    /// <summary>
    /// 自检用：把「自动-时间」的运行时账本摆成"本次运行已经跑了 ranSeconds 秒、上次在这个时候存过"，
    /// 然后问一次该不该存。给的是与正式结算点同一个入口（<see cref="MaybeTakeTimedSnapshot"/>），
    /// 所以自检验的是真接线，不是另写一套判定。
    /// </summary>
    internal void SetTimedRunStateForTest(long ranSeconds, long lastTakenAtSeconds)
    {
        _engRunSeconds = ranSeconds < 0 ? 0 : ranSeconds;
        _lastTimedSnapshotAtSeconds = lastTakenAtSeconds < 0 ? 0 : lastTakenAtSeconds;
        _engRunStartAt = DateTime.Now;      // 让后面的结算增量为 0：断言只反映本次喂进去的数
    }

    /// <summary>自检用：按当前账本判一次「自动-时间」该不该存（会真的走完整存快照流程）。</summary>
    internal void MaybeTakeTimedSnapshotForTest() => MaybeTakeTimedSnapshot();

    /// <summary>自检用：本次运行的累计秒数与"已存到哪"（验证引擎停止后确实清零）。</summary>
    internal (long ranSeconds, long lastTakenAt) TimedRunStateForTest()
        => (_engRunSeconds, _lastTimedSnapshotAtSeconds);

    /// <summary>自检用：模拟"引擎停止"这一段收尾（只清「自动-时间」的账本，不碰别人的记账）。</summary>
    internal void EndRunClockForTest() => EndRunClock();

    /// <summary>
    /// 自检用：走一遍"引擎停止"的完整收尾（结算尾段 + 清掉本次运行的全部账本），返回结算掉的尾段秒数。
    /// 与正式停止路径同一个入口（<see cref="SettleRunClock"/>），所以自检验的是真接线。
    /// </summary>
    internal int SettleRunClockForTest() => SettleRunClock();

    /// <summary>
    /// 自检用：起表入口 —— 与引擎启动时走的是同一个 <see cref="BeginRunClock"/>
    /// （版本履历基线 / 连续运行累计 / 「自动-时间」门槛三套账本一起开始），不是另写一套。
    ///
    /// 为什么必须有它：既有的 <see cref="AccumulateRunSecondsForTest"/> 是结算入口（把基线前移到现在再结算），
    /// 而 <see cref="RunClockOpenForTest"/> 只回答"账本开没开" —— 两者都造不出"起表之后、结算之前"这个中间态。
    /// 于是自检此前只能断言"账本关没关 + 幂等 + 清零"，断言不了结算的秒数确实进了版本履历：
    /// 没有起表这一步，"喂进去的秒数"就没有归属的那一段运行。
    /// </summary>
    internal void BeginRunClockForTest() => BeginRunClock();

    /// <summary>
    /// 自检用：本次运行的账本是否还开着（即"这一次运行还没收尾"）。
    /// 引擎停掉之后必须为 false —— 只要它还是 true，下一个 30 秒结算点就会把停机期当成运行时长记进履历。
    /// </summary>
    internal bool RunClockOpenForTest() => _runStartAt != DateTime.MinValue;

    /// <summary>左上角头像当前使用的图片文件名（自检用）。</summary>
    internal string LogoFileForTest()
    {
        try
        {
            if (LogoImage?.Source is System.Windows.Media.Imaging.BitmapImage bi && bi.UriSource != null)
                return System.IO.Path.GetFileName(bi.UriSource.OriginalString);
            return "";
        }
        catch { return ""; }
    }

    /// <summary>「一键更新」按钮的文字与底色（自检用）。</summary>
    internal (string text, string bg) UpdateAllButtonForTest()
    {
        string text = UpdateAllText?.Text ?? "";
        string bg = (UpdateAllText?.Parent as Border)?.Background is SolidColorBrush sb ? sb.Color.ToString() : "";
        return (text, bg);
    }

    /// <summary>快照页里「回滚勾选项」与「整组回滚」按钮的数量。</summary>
    internal (int checkedBtn, int allBtn) SnapshotRestoreButtonsForTest()
    {
        int chk = 0, all = 0;
        try
        {
            var root = FindName("SnapshotDetailPanel") as DependencyObject;
            if (root == null) return (0, 0);
            void Walk(DependencyObject o)
            {
                if (o is Button b && b.Content is string s)
                {
                    if (s.Contains("整组回滚")) all++;
                    else if (s.Contains("回滚勾选项")) chk++;
                }
                int n = VisualTreeHelper.GetChildrenCount(o);
                for (int i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(o, i));
            }
            Walk(root);
        }
        catch { }
        return (chk, all);
    }

    /// <summary>
    /// 取已安装列表第一张卡片中第一个文字的颜色。
    /// 用于验证刷新后的新卡片已按当前主题着色：先上屏再补刷会在日间模式闪一帧深色。
    /// </summary>
    internal string InstalledCardTextColorForTest()
    {
        try
        {
            if (FindName("PluginsPanel") is not Panel host) return "";
            foreach (var child in host.Children)
            {
                var found = FindFirstText(child as DependencyObject);
                if (found != null) return (found.Foreground as SolidColorBrush)?.Color.ToString() ?? "";
            }
            return "";
        }
        catch { return ""; }
    }

    /// <summary>快照详情面板里第一个文字的颜色（同上，验证刷新后即为主题配色）。</summary>
    internal string SnapshotDetailTextColorForTest()
    {
        try
        {
            var found = FindFirstText(FindName("SnapshotDetailPanel") as DependencyObject);
            return (found?.Foreground as SolidColorBrush)?.Color.ToString() ?? "";
        }
        catch { return ""; }
    }

    private static TextBlock? FindFirstText(DependencyObject? root)
    {
        if (root == null) return null;
        if (root is TextBlock t) return t;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var hit = FindFirstText(VisualTreeHelper.GetChild(root, i));
            if (hit != null) return hit;
        }
        return null;
    }

    /// <summary>
    /// 自检：日间模式下来一次「悬停 -> 移开」，返回悬停前、悬停时、移开后三个颜色。
    /// 用于验证链接悬停后颜色可正确还原（此前用写死的深色主题原值还原，导致颜色永久改变）。
    /// </summary>
    internal (string before, string hover, string after) LinkHoverRoundTripForTest()
    {
        try
        {
            if (FindName("PluginsPanel") is not Panel host) return ("", "", "");
            TextBlock? link = null;
            foreach (var child in host.Children)
            {
                link = FindLinkText(child as DependencyObject);
                if (link != null) break;
            }
            if (link == null) return ("", "", "");

            string before = (link.Foreground as SolidColorBrush)?.Color.ToString() ?? "";
            link.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseEnterEvent });
            string hover = (link.Foreground as SolidColorBrush)?.Color.ToString() ?? "";
            link.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseLeaveEvent });
            string after = (link.Foreground as SolidColorBrush)?.Color.ToString() ?? "";
            return (before, hover, after);
        }
        catch { return ("", "", ""); }
    }

    private static TextBlock? FindLinkText(DependencyObject? root)
    {
        if (root == null) return null;
        if (root is TextBlock t && t.Cursor == Cursors.Hand) return t;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var hit = FindLinkText(VisualTreeHelper.GetChild(root, i));
            if (hit != null) return hit;
        }
        return null;
    }

    /// <summary>已安装列表当前渲染出的卡片数（自检用）。</summary>
    internal int InstalledCardCountForTest()
        => FindName("PluginsPanel") is Panel p ? p.Children.OfType<Border>().Count() : 0;

    /// <summary>按条件统计应保留的插件数（与 MatchesInstalledFilter 判定一致）。</summary>
    internal int CountInstalledMatchingForTest(string kind)
    {
        try
        {
            int n = 0;
            foreach (var p in _plugins)
            {
                bool hit = kind switch
                {
                    "broken" => p.Compatibility == PluginManager.Compat.Broken,
                    "update" => _pluginUpdates.TryGetValue(p.Name, out var u) && u.HasUpdate,
                    _ => true
                };
                if (hit) n++;
            }
            return n;
        }
        catch { return -1; }
    }

    /// <summary>自检用：设置筛选条件并重新渲染。</summary>
    internal void InstalledFilterForTest(string which, string value)
    {
        if (which == "compat") _instFilterCompat = value;
        else if (which == "state") _instFilterState = value;
        else if (which == "update") _instFilterUpdateOnly = value == "only";
        PaintInstalledFilterMenu();
        RenderPlugins();
    }

    /// <summary>MiniBtn 样式里有没有悬停动画（EnterActions/ExitActions）。</summary>
    internal bool MiniBtnHoverAnimationForTest()    {
        try
        {
            if (FindResource("MiniBtn") is not Style style) return false;
            return style.Triggers.OfType<Trigger>()
                .Where(t => t.Property == UIElement.IsMouseOverProperty)
                .Any(t => t.EnterActions.Count > 0 || t.ExitActions.Count > 0);
        }
        catch { return false; }
    }

    /// <summary>卡片中是否存在带下划线的文字（链接不使用下划线）。</summary>
    internal static bool HasUnderlineForTest(DependencyObject root)
    {
        if (root is TextBlock t && t.TextDecorations != null && t.TextDecorations.Count > 0) return true;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
            if (HasUnderlineForTest(VisualTreeHelper.GetChild(root, i))) return true;
        return false;
    }

    /// <summary>写入一条「有新版」记录，用于验证「更新到 X」按钮的配色。</summary>
    internal void SeedPluginUpdateForTest(string name, string installed, string latest)
        => _pluginUpdates[name] = new PluginManager.PluginUpdate
        {
            Name = name, Installed = installed, Latest = latest, HasUpdate = true, Published = "2026-09-11"
        };

    /// <summary>页面里某段文字在不在（自检读文字用）。</summary>
    internal string PageTextsForTest(string elementName)
    {
        if (FindName(elementName) is not DependencyObject d) return "";
        var sb = new System.Text.StringBuilder();
        void Walk(DependencyObject o)
        {
            if (o is TextBlock tb) sb.Append(tb.Text).Append('|');
            if (o is TextBox bx) sb.Append(bx.Text).Append('|');
            int n = VisualTreeHelper.GetChildrenCount(o);
            for (int i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(o, i));
        }
        Walk(d);
        return sb.ToString();
    }
    internal void ScrollSettingsToEndForTest()
    {
        // 版本页为 ScrollViewer，截图底部按钮需滚动到底。
        if (FindName("SettingsPageVersion") is ScrollViewer sv) sv.ScrollToEnd();
    }

    /// <summary>版本页滚到指定位置（截图看中间那张卡用）。</summary>
    internal void ScrollSettingsToForTest(double offset)
    {
        if (FindName("SettingsPageVersion") is ScrollViewer sv) sv.ScrollToVerticalOffset(offset);
    }

    /// <summary>这个元素是不是落在中栏（状态页换位自检用）。</summary>
    internal bool IsInMiddleColumnForTest(DependencyObject? el)
    {
        while (el != null)
        {
            if (ReferenceEquals(el, StatusView)) return true;
            el = VisualTreeHelper.GetParent(el);
        }
        return false;
    }

    /// <summary>
    /// 细滚动条的滑块宽度（静息 / 悬停），从模板内容读取。
    /// 列表未溢出时真实滚动条为 Collapsed、模板尚未展开，视觉树中取不到 Thumb。
    /// </summary>
    internal (double idle, double hover) ScrollBarThumbWidthsForTest()
    {
        double idle = 0, hover = 0;
        if (FindResource("SlimVScroll") is ControlTemplate tpl)
        {
            if (tpl.LoadContent() is Grid grid &&
                grid.Children.OfType<System.Windows.Controls.Primitives.Track>().FirstOrDefault() is { } track &&
                track.Thumb is { } thumb)
                idle = thumb.Width;

            foreach (var trigger in tpl.Triggers.OfType<Trigger>()
                         .Where(t => t.Property == UIElement.IsMouseOverProperty))
                foreach (var setter in trigger.Setters.OfType<Setter>()
                             .Where(s => s.Property == FrameworkElement.WidthProperty && s.Value is double))
                    hover = (double)setter.Value;
        }
        return (idle, hover);
    }

    /// <summary>滚动条样式是不是已经挂成全局默认（不带 x:Key 的隐式样式）。</summary>
    internal bool SlimScrollBarIsGlobalForTest()
    {
        try
        {
            var implicitStyle = FindResource(typeof(System.Windows.Controls.Primitives.ScrollBar)) as Style;
            return implicitStyle?.BasedOn is Style based && ReferenceEquals(based, FindResource("SlimScrollBar"));
        }
        catch { return false; }
    }
    internal void PaintFilterMenuForTest() => PaintFilterMenu();
    internal void SelectSortForTest(string tag) => MarketSort_Click(new Border { Tag = tag }, null!);
    internal void SelectSortDirForTest(bool desc) => MarketSortDir_Click(new Border { Tag = desc ? "desc" : "asc" }, null!);
    internal void SelectTimeRangeForTest(int days) => MarketTime_Click(new Border { Tag = days.ToString() }, null!);
    internal void SelectHostFilterForTest(bool adapted) => MarketHost_Click(new Border { Tag = adapted ? "adapted" : "all" }, null!);
    internal void ToggleCatsForTest()
    {
        _marketCatsExpanded = !_marketCatsExpanded;
        BuildCategoryChips();
    }
    internal Task<System.Windows.Media.Imaging.BitmapSource?> LoadImageForTestAsync(string url, bool full)
        => LoadImageAsync(url, full);

    /// <summary>自检用：市场页内部状态快照，用于排查卡片数为 0 等问题。</summary>
    internal string MarketDebugForTest()
    {
        int cards = (FindName("MarketPanel") as Panel)?.Children.Count ?? -1;
        int chips = (FindName("MarketCategoryPanel") as Panel)?.Children.Count ?? -1;
        string sum = (FindName("MarketSummaryText") as TextBlock)?.Text ?? "";
        string cat = _market == null ? "null" : _market.Plugins.Count.ToString();
        return $"cards={cards} chips={chips} loading={_marketLoading} catalog={cat} tab={_marketTab} " +
               $"summary={sum.Replace('\n', '/')}";
    }

    /// <summary>不显示窗口，直接把整棵内容树量算一遍（自检用）。</summary>
    internal void LayoutForTest(double width, double height)
    {
        var root = (FrameworkElement)Content;
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
    }

    protected override void OnClosed(EventArgs e)
    {
        Logger.Log("OnClosed");
        // 静态事件必须解绑：它们是静态的，订阅者（本窗口）会被一直持有，即窗口永远回收不了。
        // 产品路径只有一个 MainWindow、影响有限，但自检会反复构造窗口，静态事件就把每个实例
        // 连同它的控件树一起攒下来（"静态事件钉住实例"的典型形态，本轮一并收掉）。
        // 整个退订包在 try/catch 里：关闭路径不该因为退订失败而抛异常。
        try
        {
            GuardDialog.AnyOpenChanged -= OnDialogOpenChanged;
            Logger.LogFilesChanged -= RefreshLogFilePicker;
        }
        catch (Exception ex) { try { Logger.LogError("OnClosed/Unsubscribe", ex); } catch { } }
        // 此处只做收尾清理：不调用 ForceShutdown，它是退出应用入口（会再次 Shutdown），
        // 且默认 ForceKill 本程序启动的引擎，会覆盖用户「退出但不终止引擎」的选择。
        _trayIcon?.Dispose();

        // ═══ 关窗必须把两个定时器停掉（原缺陷：只退订了静态事件、只释放了托盘图标）═══
        // 持有关系与上面"静态事件钉住实例"是同一类问题，只是多了一层"还在跳"：
        //   · _uiWatchdog 是 System.Threading.Timer，回调每 3 秒跑一次（首次 5 秒），
        //     闭包捕获本窗口（_dragging / _isStarting / _lastStallReportUtc）——
        //     不停就永远每 3 秒跳一次，并在卡顿时往诊断日志写行；
        //   · _statusTimer 是 DispatcherTimer，每秒 Tick → StatusTimer_Tick，
        //     里面会读 _processManager、还会走端口同步与 HandlePrematureExit 那条会计账的路径。
        // 产品路径只有一个窗口、影响有限，但关窗后它们仍在跑；自检反复构造窗口则是实打实的累积。
        // 单独 try/catch：定时器清理失败不该影响其余收尾（与上面退订同一口径）。
        try
        {
            _uiWatchdog?.Dispose();
            _uiWatchdog = null;
            _uiHeartbeat?.Stop();
            _statusTimer.Stop();
            _statusTimer.Tick -= StatusTimer_Tick;
        }
        catch (Exception ex) { try { Logger.LogError("OnClosed/Timers", ex); } catch { } }

        // 引擎输出这条线也要摘掉：处理器捕获着本窗口，不退订会让本窗口被引擎事件钉住
        // （与上面两条静态事件同理）。这里只退订，不终止引擎 —— 是否终止由上面的策略决定，
        // 一行都不改。放在最后：退订之后再不会有输出回调碰已关闭的窗口。
        try { DetachEngineManager(); }
        catch (Exception ex) { try { Logger.LogError("OnClosed/EngineOutput", ex); } catch { } }

        base.OnClosed(e);
    }
}
