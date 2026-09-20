using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace DSHGuard;

public partial class App : Application
{
    private Mutex? _mutex;

    /// <summary>本进程是否真的持有单实例锁（第二个实例不持有）。</summary>
    private bool _ownsMutex;

    /// <summary>自检夹具在 %TEMP% 下的日志根目录名（见 <see cref="IsolationLogDirForRun"/>）。</summary>
    private const string SelfTestLogRootName = "DSHGuard-selftest-logs";

    /// <summary>自检夹具在 %TEMP% 下的数据根目录名（见 <see cref="OnStartup"/> 第 -1 步）。</summary>
    private const string SelfTestDataRootName = "dshguard-selftest-data";

    /// <summary>
    /// <see cref="IsolationLogDirForRun"/> 生成的目录叶子名格式。收尾清理拿它当"这确实是本程序造的目录"
    /// 的第二条判据（第一条是路径本身来自本次运行）。
    /// </summary>
    private const string SelfTestLogStampFormat = "yyyyMMdd-HHmmss-fff";

    /// <summary>
    /// 本次运行自己的夹具日志目录（<see cref="IsolationLogDirForRun"/> 的返回值；非夹具运行恒为 null）。
    /// 收尾清理按**这条记下来的路径**删，不在退出时重新取一次时间 —— 重新取会算成另一个目录，
    /// 结果是"删了个不存在的路径、真目录反而留着"。
    /// </summary>
    private static string? _selfTestLogDir;

    /// <summary>
    /// 本次 <c>--selftest</c> 的退出码（null = 本次不是 <c>--selftest</c> ⇒ 收尾一律不清理）。
    /// 只有走 <c>SelfTest.ShouldRun</c> 那一支才会被赋值 ⇒ <c>--shot</c> / <c>--dialog-shot</c> /
    /// 正常启动天然拿不到这个值，收尾清理对它们不可能生效。
    /// </summary>
    private static int? _selfTestExitCode;

    /// <summary>
    /// 测试夹具模式下本次运行该用的日志目录（纯函数，自检可直接断言）：
    /// <c>--selftest</c> / <c>--shot</c> / <c>--dialog-shot</c> ⇒ <c>%TEMP%\DSHGuard-selftest-logs\&lt;时间戳&gt;\</c>；
    /// 其它参数（含正常启动、<c>--uninstall</c>）⇒ <c>null</c>（= 照常写 <see cref="GuardPaths.LogDir"/>）。
    ///
    /// <c>--uninstall</c> 特意不算夹具：它走的是与主程序同款的真实界面与安装器调用，
    /// 出问题同样需要留在用户日志目录里可查。
    /// </summary>
    internal static string? IsolationLogDirForRun(string[] args)
    {
        if (!IsTestFixtureRun(args)) return null;
        try
        {
            return Path.Combine(Path.GetTempPath(),
                SelfTestLogRootName, DateTime.Now.ToString(SelfTestLogStampFormat));
        }
        catch { return null; }
    }

    /// <summary>本次是不是"测试夹具"运行（只跑自检/截图，不碰用户环境）。</summary>
    internal static bool IsTestFixtureRun(string[] args)
    {
        try
        {
            return Array.Exists(args, a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)
                                        || a.Equals("--shot", StringComparison.OrdinalIgnoreCase)
                                        || a.Equals("--dialog-shot", StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    /// <summary>
    /// 启动时把用户设置读进来，并同步到各处缓存 —— **必须早于任何"按设置裁剪用户数据"的动作**。
    ///
    /// 为什么需要这一步（现场缺陷，用户快照被误删且不可恢复）：
    ///   保留策略原先直接排在 <c>GuardPaths.EnsureLayout()</c> 之后，而用户设置要到
    ///   <c>MainWindow</c> 构造函数里才被读入 ⇒ 清理时 <c>SnapshotManager.SettingsCache.AutoSnapshotKeep</c>
    ///   还是静态默认值 <c>10</c>。用户把份数配成 30，**每次启动仍被裁到 10，多删的 20 份不可恢复**。
    ///   这里把"读设置"提到裁剪之前，让保留策略用上用户真正的值。
    ///
    /// 为什么不直接把 TrimAll 挪到窗口构造之后：
    ///   ① 窗口构造时机不可控（<c>StartupUri="MainWindow.xaml"</c> 由 WPF 在 <c>base.OnStartup</c> 里构造，
    ///      自检又是在 <c>SelfTest.RunCore</c> 里手动构造），把"清理用户数据"绑到一个会因为界面原因
    ///      被跳过、被重复或抛异常的时机上，风险比收益大；
    ///   ② 保留策略要的是"配置已就绪"，不是"窗口已就绪"——在这里显式加载配置，恰好只满足前者。
    ///
    /// 读两遍不算浪费：本方法只负责"裁剪前配置已就绪"这一件事，<c>MainWindow</c> 构造函数里仍会照常
    /// 读一遍（那里顺带初始化 <c>Registries</c>）。两处写入 的值一致：<c>SettingsManager.Load</c> 已保证
    /// 结果 &gt; 0，窗口那句 <c>&gt; 0 ? … : 10</c> 的兜底不会改写它。读一个几 KB 的本地 JSON，
    /// 代价可以忽略，换来的是"保留策略不再依赖窗口构造时机"这一硬保证。
    ///
    /// ⚠ 这里**只加载设置，不调用 <c>GuardPaths.Apply</c>**：路径改根会同时改变
    ///   <c>SnapshotRoot</c> / <c>ProfileDir</c> 的指向，在启动半途悄悄换仓库位置比原缺陷更危险。
    ///   （另注：路径目前只在设置页点"保存"时才 Apply，重启后不会自动生效 —— 属另一处独立问题，不在本次改动内。）
    /// </summary>
    internal static void LoadUserConfigForStartup()
    {
        try
        {
            var settings = new SettingsManager();
            settings.Load();
            SnapshotManager.SettingsCache.AutoSnapshotKeep = settings.AutoSnapshotKeep;
            // ⑥′ 把"这个份数可不可信"一并交下去：读不出来时 LastLoadTrusted 为假 ⇒ 自动档裁剪整轮跳过，
            //     免得拿类型默认 10 去裁用户配的 30 份（静默多删、不可恢复）。
            //     ⚠ 必须取这个**局部 settings**：本方法与 MainWindow._settings 是两个不同实例，
            //       去读 MainWindow 那个会拿到"没 Load 过"的默认值，把裁剪永久钉死在关闭状态。
            SnapshotManager.SettingsCache.KeepTrusted = settings.LastLoadTrusted;
            // 单独取出来再拼：避免在插值的 {} 里塞三元 + 引号字面量（那种写法依赖编译器对 ':' 的判定，
            // 本单禁 build、无法当场验证，写成最朴素的形式最稳）。
            string trustNote = settings.LastLoadTrusted
                ? "可信"
                : "不可信 ⇒ 本次不裁快照：" + settings.LastLoadError;
            Logger.Log($"用户配置已加载：自动快照保留 {settings.AutoSnapshotKeep} 份（{trustNote}）");
        }
        catch (Exception ex) { Logger.LogError("LoadUserConfigForStartup", ex); }
    }

    /// <summary>启动清理是否已经跑过（0 = 还没跑；见 <see cref="TrimSnapshotsOnce"/>）。</summary>
    private static int _startupTrimRan;

    /// <summary>
    /// 启动清理：**整个进程恰好一次**。
    ///
    /// 为什么要防重：清理是按"用户配置的份数"删快照，删掉的不可恢复。原先它挂在 <c>OnStartup</c> 里
    /// （天然只跑一次），一旦将来有人把它挪到窗口构造之后、或出现第二个 <c>MainWindow</c>，
    /// 就会每构造一次窗口就对着用户数据裁一刀。这里用一次性闸门把它钉死。
    ///
    /// 同时保证"清理不会因为改顺序而变成不跑"：有配置时它一定跑，只是跑在 <see cref="LoadUserConfigForStartup"/>
    /// 之后；重入时返回 0（表示本次没有额外删除），不会让快照无限增长。
    /// </summary>
    internal static int TrimSnapshotsOnce()
    {
        if (Interlocked.Exchange(ref _startupTrimRan, 1) != 0) return 0;
        return SnapshotManager.TrimAll();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        SelfTest.Trace("App.OnStartup 进入（Logger.Init 之前）");

        // ★ 第 -1 步：测试夹具（自检 / 截图 / 对话框预览）**绝不写用户数据目录**。
        //   压测 E 实测：`--selftest` 会写 <exe>\Config\loader-ids.json（PluginManager.LoaderIdCacheFile），
        //   而 App.OnStartup 在进入自检分支**之前**就跑了 patch 自愈等读配置的步骤 ——
        //   那时 DSHGUARD_DATA_DIR 还没设置（SelfTest.RunCore 里才设），自检隔离形同虚设。
        //   这里把它的设置**提前到 OnStartup 最开头**（比日志隔离、比一切迁移/自愈都早），
        //   GuardPaths.ConfigDir / CacheDir 随即全部改根到 %TEMP%，后续任何落盘都碰不到用户目录。
        try
        {
            if (IsTestFixtureRun(e.Args))
            {
                string dataDir = Path.Combine(Path.GetTempPath(), SelfTestDataRootName);
                Directory.CreateDirectory(dataDir);
                Environment.SetEnvironmentVariable("DSHGUARD_DATA_DIR", dataDir);
            }
        }
        catch { }

        // 先把临时目录自净（安装器最后一步拉起本程序时，TEMP 指向安装器自己的临时目录，
        // 那个目录在安装器退出后会被删 → 引擎第一次启动拿不到可写临时目录就会秒退，现场 bug）。
        string envNote = ProcessEnv.EnsureUsableTemp();

        // ★ 第 0 步：测试夹具（自检 / 截图 / 对话框预览）**绝不写用户的 Logs 目录**。
        //   这些模式会反复跑夹具（不存在的版本 8.8.8、不存在目录、故意喂的坏 JSON…），
        //   每跑一次就往真实日志里落一条（实测 25 个异常日志 144 条「命令结束」里 114 条是成功命令、
        //   22 个「启动-*」只剩环境一行）——日志就是这么被刷满的。
        //   这里把**本次运行**的日志目录整体切到 %TEMP%\DSHGuard-selftest-logs\<时间戳>\：自检**失败**时原样留着
        //   （便于排查，异常日志/启动日志都在那儿），**成功**时在收尾把这一份删掉（见 CleanupSelfTestFixturesOnExit）——
        //   成功的日志没有排查价值，留着只会让 %TEMP% 每跑一轮就多一个常驻目录。
        //   但一个字节也不落 GuardPaths.LogDir（该属性的语义保持不变）。
        //   必须放在最前：① 号步骤 MigrateFolderCasing 失败时就会 LogError 落盘，放到它后面就白切了。
        try
        {
            string? iso = IsolationLogDirForRun(e.Args);
            if (iso != null)
            {
                Logger.UseLogDirForRun(iso);
                Directory.CreateDirectory(iso);
                _selfTestLogDir = iso;   // 收尾清理认的就是这一条绝对路径（见 CleanupSelfTestFixturesOnExit）
            }
        }
        catch { }

        // ① 文件夹名规范化：logs/tools/config/cache → Logs/Tools/Config/Cache
        //    须在 Logger.Init 之前执行，日志文件一旦被打开占用则无法重命名
        try
        {
            string? renamed = GuardPaths.MigrateFolderCasing();
            if (renamed != null) Logger.Log($"文件夹已规范命名：{renamed}");
        }
        catch (Exception ex) { Logger.LogError("MigrateFolderCasing(startup)", ex); }

        Logger.Init();
        Logger.Log("App.OnStartup 开始");
        Logger.NoteStartup(envNote);   // 环境一行：临时目录原值/是否可用（首启失败时一眼能看出是不是这个原因）
        SelfTest.Trace("Logger.Init 完成");

        // ② 配置迁移（一次性）：%APPDATA%\DSHGuard → 主目录\Config；同时创建 Cache 分级目录
        try
        {
            string? moved = GuardPaths.MigrateLegacyConfig();
            GuardPaths.EnsureCacheDirs();
            if (moved != null)
            {
                Logger.Log($"配置已迁移到 {GuardPaths.ConfigDir}：{moved}");
                // 此处 DSHGuard.MainWindow 须写全名，否则 MainWindow 会被解析为 Application.MainWindow 属性
                DSHGuard.MainWindow.AddEvent($"配置已搬到程序目录的 Config 文件夹（{moved}）",
                    DSHGuard.MainWindow.EventKind.Good);
            }
        }
        catch (Exception ex) { Logger.LogError("MigrateLegacyConfig(startup)", ex); }
        // 配置文件体检：历史版本曾把未加引号的 `- id: @scope/name` 写进 cordis.patch.yml
        // （`@` 是 YAML 保留字符，不能作纯量开头）⇒ 整份文件解析失败、插件被误判为禁用。
        // 这里做一次定点修复：只改我们认得的那种行，改前先备份。
        try
        {
            string patch = PluginManager.PatchFile;
            if (File.Exists(patch))
            {
                var (ok, fixedText, problems) = PluginManager.ValidatePatchText(File.ReadAllText(patch));
                if (!ok)
                {
                    string bak = patch + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    File.Copy(patch, bak, overwrite: true);
                    File.WriteAllText(patch, fixedText, new System.Text.UTF8Encoding(false));
                    Logger.Log($"已修复 cordis.patch.yml 的 {problems.Count} 处非法写法（备份 {Path.GetFileName(bak)}）");
                    DSHGuard.MainWindow.AddEvent(
                        $"已修复配置文件中的非法写法（{problems.Count} 处，原文件已备份）",
                        DSHGuard.MainWindow.EventKind.Warn);
                }
            }
        }
        catch (Exception ex) { Logger.LogError("patch self-heal", ex); }

        // ②′ 用户配置必须在这里就绪：下面第 ⑥ 步的启动清理要按"用户配置的份数"裁剪快照，
        //     而用户设置原先要到 MainWindow 构造函数才读进来 ⇒ 清理用的是静态默认值 10。
        //     必须放在 ② 配置迁移之后（否则读的还是迁移前的老位置），且早于 ⑥ 的裁剪。
        LoadUserConfigForStartup();

        // 记下当前父链：终止引擎前据此判断"引擎是否正在托管守护壳"（父链一旦有中间进程退出就会断）
        ProcessManager.CaptureAncestors();
        Logger.Log($"守护壳版本 {GuardVersion.Version}（第 {GuardVersion.Batch} 批）");

        // 全局异常捕获
        AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
        {
            Logger.LogError("AppDomain.UnhandledException", (Exception)ex.ExceptionObject!);
            Logger.Flush();
        };
        DispatcherUnhandledException += (s, ex) =>
        {
            Logger.LogError("DispatcherUnhandledException", ex.Exception);
            ex.Handled = true;
            Logger.Flush();
        };
        TaskScheduler.UnobservedTaskException += (s, ex) =>
        {
            Logger.LogError("TaskScheduler.UnobservedTaskException", ex.Exception);
            Logger.Flush();
        };

        // 自检模式：不创建窗口、不获取单实例锁、不访问引擎，执行完毕后直接返回退出码
        if (SelfTest.ShouldRun(e.Args))
        {
            Logger.Log("进入自检模式 --selftest");
            int code = SelfTest.Run(e.Args);
            Logger.Log($"自检结束，退出码 {code}");
            _selfTestExitCode = code;   // 收尾清理只认这一支（见 CleanupSelfTestFixturesOnExit）
            Shutdown(code);
            return;
        }

        // 截图模式：把界面渲染为 PNG，便于在改动 UI 后直接核对渲染结果
        if (SelfTest.ShouldShot(e.Args))
        {
            int code = SelfTest.Shot(e.Args);
            Logger.Log($"截图结束，退出码 {code}");
            Shutdown(code);
            return;
        }

        // 对话框样式预览：一次渲染三种典型对话框，用于核对圆角与日/夜配色
        if (SelfTest.ShouldDialogShot(e.Args))
        {
            int code = SelfTest.DialogShot(e.Args);
            Shutdown(code);
            return;
        }

        // 卸载界面：主程序同款外观收集"要删哪些数据"，再交给 Inno 卸载器执行
        if (SelfTest.ShouldUninstall(e.Args))
        {
            RunUninstallUi();
            Shutdown(0);
            return;
        }

        // 目录契约：把 Config / Cache / Logs / Snapshots 建好，程序所有落盘数据都只写这些目录
        // （放在自检/截图分支之后：那几种模式不碰用户的真实目录）
        try
        {
            var ready = GuardPaths.EnsureLayout();
            Logger.Log("目录就绪：" + string.Join("、", ready));
            // ⑥ 启动时执行一次快照保留策略。用户配置已在 ②′ 加载完毕，
            //    这里用的是用户配的份数（而不是静态默认值 10）。走 TrimSnapshotsOnce：
            //    闸门保证整个进程只跑一次，不因窗口重复构造而对用户快照重复裁剪。
            int trimmed = TrimSnapshotsOnce();
            if (trimmed > 0) Logger.Log($"快照保留策略：启动清理 {trimmed} 份");
        }
        catch (Exception ex) { Logger.LogError("EnsureLayout", ex); }

        // 单实例锁
        try
        {
            _mutex = new Mutex(true, "DSHGuard-SingleInstance", out bool createdNew);
            _ownsMutex = createdNew;
            if (!createdNew)
            {
                Logger.Log("已有实例运行,激活已有窗口");
                NativeMethods.FindAndActivateWindow();
                Shutdown();
                return;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("Mutex创建失败", ex);
        }

        Logger.Log("单实例检查通过,继续启动");
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Logger.Log("App.OnExit");
        // 兜底：正常退出时若本次启动从未出过事，把「启动-*.log」撤掉。
        // 覆盖 MainWindow_Loaded 之外的两条出口 —— ①「已有实例在运行」那一支（`Shutdown()` 后直接 return，
        // 根本走不到窗口 Loaded），② 任何未及加载窗口的正常退出。出过事则一律拒绝删。
        Logger.DiscardStartupLogIfHealthy();
        Logger.Close();
        // 自检收尾：句柄关掉之后才删得掉（见下面方法注释里的去留规则）
        CleanupSelfTestFixturesOnExit();
        ReleaseSingleInstance(_mutex, _ownsMutex);
        base.OnExit(e);
    }

    /// <summary>
    /// 自检收尾：把本程序**自己在 %TEMP% 下造的**夹具收拾掉。<b>只对 <c>--selftest</c> 生效</b>
    /// （<see cref="_selfTestExitCode"/> 为 null 时第一句就返回 ⇒ <c>--shot</c> / <c>--dialog-shot</c> /
    /// 正常启动一个字节都不动，用户的 %TEMP% 不归本程序管）。
    ///
    /// 去留规则：
    ///   ① 本次运行那个带时间戳的日志目录：**成功（退出码 0）才清**，失败原样留着 —— 它是为排查准备的
    ///      （异常日志 / 启动日志都落在里面），清了失败现场就只剩一句"退出码 1"。原注释那句"便于排查"
    ///      只在失败这一侧才成立，成功那一轮留着只是纯增长。
    ///   ② 夹具数据根 <c>dshguard-selftest-data</c>：**一律清**（不论成败）。它是机器态（夹具 Config / Cache），
    ///      不是证据（报告在 dshguard-selftest.txt、跟踪在 .trace、异常在①的日志目录里），而且下一轮自检
    ///      本来就按"目录可能是冷的"设计（SelfTest.RunCore 会把 catalog.json 重新播种进去）⇒ 留着它只会
    ///      让 %TEMP% 里常驻一堆可重建的文件。
    ///   ③ <c>dshguard-selftest.txt</c> / <c>.trace</c> 不动：报告与"卡在哪一步"的跟踪，都由下一轮自检覆写
    ///      （Run 开头先删 .trace）⇒ 恒为一份、不增长，且都是证据。
    ///
    /// ⚠ 必须排在 <see cref="Logger.Close"/> 之后：日志文件还开着时删不掉（Windows 共享冲突），
    ///   早删只会静默失败 —— 看着像清了，其实一个都没清。
    /// ⚠ 绝不抛、绝不扫目录：只对上面两条**确定路径**做"存在则删"，逐条校验见 <see cref="TryDeleteSelfTestFixtureDir"/>。
    /// </summary>
    private static void CleanupSelfTestFixturesOnExit()
    {
        if (_selfTestExitCode == null) return;   // 不是 --selftest ⇒ 一个字节都不动
        try
        {
            string temp = Path.GetTempPath();

            // ① 本次运行自己的日志目录：路径取自本次运行、叶子名还必须是本程序生成的时间戳格式。
            if (_selfTestExitCode == 0 && !string.IsNullOrEmpty(_selfTestLogDir))
            {
                string stamp = Path.GetFileName(
                    _selfTestLogDir!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (DateTime.TryParseExact(stamp, SelfTestLogStampFormat,
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out _))
                    TryDeleteSelfTestFixtureDir(Path.Combine(temp, SelfTestLogRootName), stamp);
            }

            // ② 夹具数据根：固定名，只有自检夹具会用它（正常启动走的是用户真实 Config / Cache）
            TryDeleteSelfTestFixtureDir(temp, SelfTestDataRootName);
        }
        catch { }
    }

    /// <summary>
    /// 删掉 <paramref name="root"/> 下名为 <paramref name="leaf"/> 的**那一个**夹具目录（存在才删，递归）。
    /// 刻意**不做**"扫 %TEMP% 找 <c>dshguard-*</c> 前缀"那种事：那样既会碰到别的程序的东西，
    /// 也会碰到同时跑着的另一轮自检正在用的夹具。四条校验全过才动手：
    ///   ① 叶子名是单一名字（不含目录分隔符 / 驱动器冒号，也不是 <c>.</c> / <c>..</c>）；
    ///   ② <c>Path.Combine</c> 之后仍然**正好是 <paramref name="root"/> 的直接子目录**
    ///      （挡住 rooted 路径让 <c>Path.Combine</c> 丢掉前缀那一类陷阱，同第 54 批「清理入口路径安全」）；
    ///   ③ 落点确实在 %TEMP% 内；
    ///   ④ 不是重解析点（联接 / 符号链接不算"本程序造的目录"，删它可能删到别处）。
    /// 返回是否真的删掉了；任何异常一律吞掉返回 false（收尾路径不许抛，与本文件既有风格一致）。
    /// internal 供自检断言路径安全（同 <see cref="IsolationLogDirForRun"/> 的"纯函数可直接断言"做法）。
    /// </summary>
    internal static bool TryDeleteSelfTestFixtureDir(string root, string leaf)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(leaf)) return false;
            if (leaf == "." || leaf == ".." || leaf.IndexOfAny(new[] { '\\', '/', ':' }) >= 0) return false;

            string fullRoot = Path.GetFullPath(root);
            string full = Path.GetFullPath(Path.Combine(fullRoot, leaf));
            string parent = Path.GetDirectoryName(full) ?? "";
            if (!string.Equals(parent, fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                                StringComparison.OrdinalIgnoreCase)) return false;
            if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) return false;
            if (!Directory.Exists(full)) return false;
            if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) return false;

            Directory.Delete(full, recursive: true);
            return !Directory.Exists(full);
        }
        catch { return false; }
    }

    /// <summary>
    /// 释放单实例锁。只有真正持有它的进程才能释放：第二个实例走的是"未创建"分支，
    /// 并不持有该互斥体，直接 ReleaseMutex 会抛 ApplicationException
    /// （现场：弹窗无法关闭时再启动一个实例，进程直接崩，只能任务管理器强杀）。永不抛。
    /// </summary>
    internal static bool ReleaseSingleInstance(Mutex? mutex, bool owns)
    {
        if (mutex == null || !owns) return false;
        try { mutex.ReleaseMutex(); return true; }
        catch { return false; }
    }

    /// <summary>--uninstall：主程序同款卸载界面 → 收集选择 → 交给 Inno 卸载器（本进程先退出）。</summary>
    private static void RunUninstallUi()
    {
        try
        {
            // 守护壳还开着时先请用户关掉：卸载器要删的就是这个 exe，正在运行的文件删不掉，
            // 硬来的结果是"卸载完了程序还在"。
            int others = 0;
            try
            {
                foreach (var p in System.Diagnostics.Process.GetProcessesByName("DSHGuard"))
                    if (p.Id != Environment.ProcessId) others++;
            }
            catch { }
            if (others > 0)
            {
                GuardDialog.Show("守护壳还在运行。请先退出它（托盘图标 → 退出UI），再回来卸载。", "卸载",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var settings = new SettingsManager();
            settings.Load();
            var win = new UninstallWindow(NetworkHelper.IsPortListening(settings.Port));
            win.ShowDialog();
            if (win.Cancelled) return;

            string dir = System.IO.Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? AppContext.BaseDirectory;
            if (!UninstallWindow.LaunchUninstaller(dir, win.DeleteList, out string detail,
                                                   UninstallPlan.ModeArg(win.Mode)))
            {
                Logger.LogError("RunUninstallUi", new InvalidOperationException(detail));
                GuardDialog.Show("无法启动卸载程序。可以到「设置 → 应用」里卸载。", "卸载",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Current?.Shutdown();
        }
        catch (Exception ex) { Logger.LogError("RunUninstallUi", ex); }
    }
}

internal static class NativeMethods
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    // ── 窗口拖动与合成重画（MainWindow 用）──
    internal const int WM_NCLBUTTONDOWN = 0x00A1;
    internal const int HTCAPTION = 2;
    internal const uint RDW_INVALIDATE = 0x0001;
    internal const uint RDW_FRAME = 0x0400;
    internal const uint RDW_ALLCHILDREN = 0x0080;
    internal const uint RDW_UPDATENOW = 0x0100;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool ReleaseCapture();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool RedrawWindow(IntPtr hWnd, IntPtr rect, IntPtr region, uint flags);

    // ── 窗口位置（重画时的 1 像素微调用）──
    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOACTIVATE = 0x0010;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    internal struct RECT { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_RESTORE = 9;

    public static void FindAndActivateWindow()
    {
        var procs = System.Diagnostics.Process.GetProcessesByName("DSHGuard");
        foreach (var proc in procs)
        {
            if (proc.MainWindowHandle != IntPtr.Zero)
            {
                ShowWindow(proc.MainWindowHandle, SW_RESTORE);
                SetForegroundWindow(proc.MainWindowHandle);
                break;
            }
        }
    }
}
