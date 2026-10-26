using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DSHGuard;

/// <summary>
/// 2.1.0：桌面版这一侧。
///
/// 为什么单独一个文件：管理对象有两种（Web 引擎 / 官方桌面版），右栏的「服务控制」与「DSH 版本」
/// 在两种目标下**该说的话完全不同** —— Web 那边是端口、引擎版本、固定版本策略；桌面版那边是
/// 主程序版本、安装目录、以及"官方更新源里有没有新版"。把它们混在一个方法里靠一堆 if 分支，
/// 现场就会再次出现"站在桌面版看到的却是 Web 的版本号"这类串轨。
///
/// 更新走官方更新源（与桌面版自己的 electron-updater 同一个源）：
///   <c>resources/app-update.yml</c> 声明了 provider=generic / channel=nightly，
///   对应的清单就是 <c>{url}{channel}.yml</c>。这里只做「读清单 → 比版本 → 下载安装包 → 交给它自己装」，
///   不自己解包、不自己覆盖文件，避免把用户已经装好的桌面版弄坏。
/// </summary>
public partial class MainWindow
{
    /// <summary>官方桌面版更新源（来自 app-update.yml 的 url + channel）。</summary>
    private const string DesktopFeedUrl = "https://download.deepseek.com/dsh-desk/feeds/win-x64/";
    private const string DesktopFeedChannel = "nightly";

    /// <summary>桌面版主程序完整路径；未装时为空串（判据仍只此一处：<see cref="GuardPaths.DesktopExeFound"/>）。</summary>
    internal static string DesktopExePath
        => GuardPaths.DesktopExeFound
            ? Path.Combine(GuardPaths.DesktopInstallDir, GuardPaths.DesktopExeName)
            : "";

    /// <summary>桌面版是否正在运行（按进程名判，不按窗口 —— 它最小化到托盘时也算在跑）。</summary>
    internal static bool DesktopAppRunning()
    {
        try
        {
            string name = Path.GetFileNameWithoutExtension(GuardPaths.DesktopExeName);
            if (name.Length == 0) return false;
            var procs = Process.GetProcessesByName(name);
            try { return procs.Length > 0; }
            finally { foreach (var p in procs) p.Dispose(); }
        }
        catch (Exception ex) { Logger.LogError("DesktopAppRunning", ex); return false; }
    }

    /// <summary>
    /// 桌面版插件写命令前的运行态闸门。DSH 桌面版的终端子进程会在 node_modules 内长期持有
    /// bash/sh 文件；仅关闭窗口或只等 Web 端口空闲都不够，必须确认整个桌面版进程已经退出。
    /// 用户明确同意后复用「结束桌面版」的同一套正常关闭 → 等待 → 强杀逻辑，不另造第二套。
    /// </summary>
    internal async Task<bool> EnsureDesktopStoppedForPluginWriteAsync()
    {
        if (!_ctx.IsDesktop || !DesktopAppRunning()) return true;

        AddEvent("DSH 桌面版正在运行，更新或卸载插件前需要先结束它", EventKind.Warn);
        var answer = GuardDialog.ShowCustom(
            DesktopPluginWriteGuardTextForTest(),
            DesktopPluginWriteGuardTitleForTest(),
            MessageBoxImage.Warning,
            new GuardDialog.DialogButton("结束并继续", MessageBoxResult.Yes,
                Color.FromRgb(0xFF, 0x3B, 0x30), IsDefault: true),
            new GuardDialog.DialogButton("取消", MessageBoxResult.No,
                Color.FromRgb(0x8E, 0x8E, 0x93), IsCancel: true));
        if (answer != MessageBoxResult.Yes) return false;

        Logger.NoteDiagnosis("插件写操作前：用户确认结束 DSH 桌面版", GuardTarget.Desktop);
        StopDesktopAppQuiet();

        // 正常关闭最多等 8 秒；这里再等进程树和文件句柄收敛。若仍有同名进程，
        // 宁可本次不执行命令，也不让 pnpm 跑几分钟后才在 os error 5 上失败。
        for (int i = 0; i < 40 && DesktopAppRunning(); i++)
            await Task.Delay(250);
        if (DesktopAppRunning())
        {
            GuardDialog.Show(DesktopPluginWriteStillRunningTextForTest(),
                "插件操作已取消", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        // Electron/Cygwin 子进程退出后，Windows 可能还会短暂保留文件句柄。
        await Task.Delay(350);
        AddEvent("DSH 桌面版已结束，继续执行插件操作", EventKind.Info);
        return true;
    }

    internal static string DesktopPluginWriteGuardTitleForTest() => "更新前需要结束桌面版";

    internal static string DesktopPluginWriteGuardTextForTest() =>
        "检测到 DSH 桌面版正在运行。\n\n" +
        "更新或卸载插件需要替换 profile 里的文件；桌面版及其终端进程可能正占用这些文件。" +
        "如果直接继续，可能更新失败并留下半更新状态。\n\n" +
        "是否先结束桌面版并继续？\n\n" +
        "· 未保存的对话或编辑内容会丢失\n" +
        "· 只会结束桌面版，不会关闭 DSHGuard";

    internal static string DesktopPluginWriteStillRunningTextForTest() =>
        "DSH 桌面版还没有完全退出，本次没有执行插件写命令。\n\n" +
        "请手动结束所有桌面版进程后重试。";

    /// <summary>
    /// 右栏按管理对象改写：桌面版显示桌面版自己的信息，Web 照旧。
    /// 由 <see cref="ApplyTargetChrome"/> 在每次换目标后调用（唯一入口，与其它目标相关外观同一处）。
    /// </summary>
    internal void ApplyRightPanelForTarget()
    {
        try
        {
            bool desktop = _ctx.Target == GuardTarget.Desktop;

            if (desktop)
            {
                // 桌面版：三颗按钮按桌面版进程状态摆（启动 / 结束 / 打开）
                RefreshDesktopRunState();
            }
            else
            {
                // 回到 Web：把桌面版改过的文案与提示还原（否则切回 Web 还挂着"启动桌面版"）
                if (MainBtnText != null) MainBtnText.Text = "一键启动引擎";
                if (StopBtnText != null) StopBtnText.Text = "终止引擎";
                if (OpenBtnText != null) OpenBtnText.Text = "加载引擎";
                if (IdleButtonPanel != null) IdleButtonPanel.ToolTip = "启动 DSH Web 引擎（首次较慢，需要下载运行环境）";
                if (StopBtnBorder != null) StopBtnBorder.ToolTip = "终止 DSH 引擎（会中断正在运行的任务；外部启动的引擎也可终止）";
                if (OpenEngineBorder != null) OpenEngineBorder.ToolTip = null;
                UpdateUI();     // Web 那套按钮可见性立刻回到引擎真实状态
            }

            // 中间那片主页：桌面版显示桌面版那一份，Web 显示引擎状态 + 实时输出
            if (StatusViewTitle != null) StatusViewTitle.Text = desktop ? "桌面版总览" : "状态总览";
            if (WebStatusCard != null) WebStatusCard.Visibility = desktop ? Visibility.Collapsed : Visibility.Visible;
            if (WebOutputCard != null) WebOutputCard.Visibility = desktop ? Visibility.Collapsed : Visibility.Visible;
            if (DesktopHomeCard != null) DesktopHomeCard.Visibility = desktop ? Visibility.Visible : Visibility.Collapsed;
            if (desktop) RenderDesktopHome();

            UpdateVersionCard();
        }
        catch (Exception ex) { Logger.LogError("ApplyRightPanelForTarget", ex); }
    }


    /// <summary>
    /// 桌面版目标下的「服务控制」：未运行 ⇒ 一颗「启动桌面版」；正在运行 ⇒ 「结束桌面版」+「打开桌面版」。
    /// 判据只看桌面版进程在不在（DesktopAppRunning），不掺 Web 引擎的 _isRunning 与端口 ——
    /// 这两套东西是两回事，混在一起就会出现"Web 引擎没跑，所以桌面版按钮显示成未运行"。
    /// </summary>
    internal void RefreshDesktopRunState()
    {
        if (_ctx.Target != GuardTarget.Desktop) return;
        try
        {
            bool running = DesktopAppRunning();
            if (IdleButtonPanel != null) IdleButtonPanel.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
            if (RunningButtonPanel != null) RunningButtonPanel.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            if (LoadingButtonPanel != null) LoadingButtonPanel.Visibility = Visibility.Collapsed;   // 桌面版没有"启动中"进度态
            if (MainBtnText != null) MainBtnText.Text = "启动桌面版";
            if (StopBtnText != null) StopBtnText.Text = "结束桌面版";
            if (OpenBtnText != null) OpenBtnText.Text = "打开桌面版";
            if (IdleButtonPanel != null) IdleButtonPanel.ToolTip = "启动官方 DSH 桌面版（DeepSeek Harness）";
            if (StopBtnBorder != null) StopBtnBorder.ToolTip = "结束桌面版进程（里面未保存的内容会丢失）";
            if (OpenEngineBorder != null) OpenEngineBorder.ToolTip = "把桌面版窗口唤到前台（已在运行的话）";
            if (_trayIcon != null) _trayIcon.Text = $"DSH 守护壳 · {GuardVersion.Version} · 桌面版{(running ? "运行中" : "未运行")}";
            SyncDesktopHome(running);       // 中间的总览与右栏同源，状态一变就跟着重画
        }
        catch (Exception ex) { Logger.LogError("RefreshDesktopRunState", ex); }
    }

    private string _desktopHomeSig = "";

    /// <summary>总览内容的“指纹”：在不在跑 + 安装目录 + 主程序修改时间（装了新版它会变）。指纹变了才重画，避免每秒重建。</summary>
    private static string DesktopHomeSignature(bool running)
    {
        long stamp = 0;
        try
        {
            string exe = Path.Combine(GuardPaths.DesktopInstallDir, GuardPaths.DesktopExeName);
            if (File.Exists(exe)) stamp = File.GetLastWriteTimeUtc(exe).Ticks;
        }
        catch { }
        return $"{running}|{GuardPaths.DesktopInstallDir}|{GuardPaths.DesktopExeFound}|{stamp}";
    }

    /// <summary>
    /// 让中间的桌面版总览跟上实际状态。总览原先只在切换目标时画一次，之后桌面版退出、
    /// 或安装器换了新版，它还停在旧的“正在运行 / 旧版本号”上，与右栏对不上。
    /// </summary>
    internal void SyncDesktopHome(bool running)
    {
        if (_ctx.Target != GuardTarget.Desktop || DesktopHomePanel == null) return;
        if (DesktopHomeSignature(running) == _desktopHomeSig) return;
        RenderDesktopHome();
    }

    /// <summary>结束桌面版：先请求正常关闭，不肯退再强杀。只结束桌面版，不碰守护壳自己。</summary>
    private void StopDesktopApp()
    {
        try
        {
            string name = Path.GetFileNameWithoutExtension(GuardPaths.DesktopExeName);
            var procs = Process.GetProcessesByName(name);
            if (procs.Length == 0)
            {
                AddEvent("桌面版当前未在运行", EventKind.Info, GuardTarget.Desktop);
                RefreshDesktopRunState();
                return;
            }

            var answer = GuardDialog.ShowCustom(
                "结束 DSH 桌面版？\n\n· 里面未保存的内容会丢失\n· 只结束桌面版，不影响守护壳",
                "结束桌面版", MessageBoxImage.Question,
                new GuardDialog.DialogButton("结束它", MessageBoxResult.Yes, Color.FromRgb(0xFF, 0x3B, 0x30), IsDefault: true),
                new GuardDialog.DialogButton("取消", MessageBoxResult.No, Color.FromRgb(0x8E, 0x8E, 0x93), IsCancel: true));
            if (answer != MessageBoxResult.Yes) { foreach (var q in procs) q.Dispose(); return; }

            EndDesktopProcesses(procs);
            foreach (var q in procs) q.Dispose();
        }
        catch (Exception ex)
        {
            Logger.LogError("StopDesktopApp", ex, GuardTarget.Desktop);
            GuardDialog.Show("结束桌面版失败：" + ex.Message + "\n\n详细原因已记入日志。",
                "结束桌面版", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 真正去结束桌面版的那些进程（先请求正常关闭，6 秒内不退再强杀），并记账。
    /// <see cref="StopDesktopApp"/> 与 <see cref="StopDesktopAppQuiet"/> 共用它，
    /// 两者的差别只在于问不问用户。
    /// </summary>
    private void EndDesktopProcesses(Process[] procs)
    {
        // 2026-10-16 审计 M：
        //   · 只认**安装目录里那个主程序**：按进程名匹配会误伤同名的别的程序；路径读不到（权限）时才退回按名认；
        //   · Electron 的渲染 / GPU 子进程没有主窗口，旧逻辑对它们 CloseMainWindow 失败就**立刻强杀**，
        //     往往赶在主进程收到"请关闭"之前 —— 正常关闭那一档形同虚设。现在先只请主窗口关，
        //     再**统一**等所有进程退出（共 8 秒上限，旧写法是每个进程各等 6 秒、串行），剩下的才强杀整棵树。
        string expected = "";
        // 安装目录没配 / 没探测到时 DesktopInstallDir 为空串，拼出来的是"当前目录下的同名 exe"、永远对不上 ⇒
        //   那种情况不按路径筛，退回旧行为按名认（复查修正：原先会一个都不结束、却报"已结束 0 个"）
        if (GuardPaths.DesktopInstallDir.Length > 0)
            try { expected = Path.GetFullPath(Path.Combine(GuardPaths.DesktopInstallDir, GuardPaths.DesktopExeName)); } catch { }
        var mine = new List<Process>();
        foreach (var q in procs)
        {
            string? path = null;
            try { path = q.MainModule?.FileName; } catch { }
            if (path == null || expected.Length == 0
                || string.Equals(Path.GetFullPath(path), expected, StringComparison.OrdinalIgnoreCase))
                mine.Add(q);
            else
                Logger.NoteDiagnosis($"结束桌面版：跳过同名但不在安装目录的进程 PID {q.Id}（{path}）", GuardTarget.Desktop);
        }

        int asked = 0;
        foreach (var q in mine)
        {
            try { if (q.MainWindowHandle != IntPtr.Zero && q.CloseMainWindow()) asked++; }
            catch { }
        }

        var deadline = DateTime.UtcNow.AddSeconds(asked > 0 ? 8 : 0);
        int closed = 0, killed = 0;
        foreach (var q in mine)
        {
            try
            {
                int left = (int)Math.Max(0, (deadline - DateTime.UtcNow).TotalMilliseconds);
                if (q.WaitForExit(left)) { closed++; continue; }
                q.Kill(entireProcessTree: true);
                q.WaitForExit(3000);
                killed++;
            }
            catch (InvalidOperationException) { closed++; }   // 已随主进程一起退出
            catch (Exception ex) { Logger.NoteDiagnosis("结束桌面版时出错：" + ex.Message, GuardTarget.Desktop); }
        }
        AddEvent($"已结束桌面版（正常关闭 {closed} 个，强制 {killed} 个）", EventKind.Warn, GuardTarget.Desktop);
        // 全部正常关闭是日常操作，不写日志；只有走到"强杀"这一档才留证据（说明桌面版没肯自己退）。
        if (killed > 0)
            Logger.NoteDiagnosis($"结束桌面版：请求关闭主窗口 {asked} 个 · 正常 {closed} / 强制 {killed}", GuardTarget.Desktop);
        RefreshDesktopCardSoon();
    }

    /// <summary>
    /// 静默结束桌面版：不问"结束桌面版？"，也不弹失败框。
    ///
    /// 用于「已经问过用户要不要重启」之后的重启动作（<see cref="MainWindow.RestartCurrentTargetAsync"/>）：
    /// 那条路上再问一遍等于让用户把同一件事答两遍。异常只记日志 —— 重启场景里
    /// 桌面版退不下去也不该拦下整个流程，上限是这次重启没生效，下次启动时自然带上新插件。
    /// </summary>
    private void StopDesktopAppQuiet()
    {
        try
        {
            string name = Path.GetFileNameWithoutExtension(GuardPaths.DesktopExeName);
            var procs = Process.GetProcessesByName(name);
            if (procs.Length == 0)
            {
                RefreshDesktopRunState();
                return;
            }
            EndDesktopProcesses(procs);
            foreach (var q in procs) q.Dispose();
        }
        catch (Exception ex)
        {
            Logger.LogError("StopDesktopAppQuiet", ex, GuardTarget.Desktop);
        }
    }

    // ══════════════ 中间「主页」的桌面版内容 ══════════════

    /// <summary>主页里的一行文字：在 <see cref="SimpleText"/> 之上只多一个上边距（分组用）。</summary>
    private static TextBlock HomeLine(string text, double size, Color color, double topMargin = 3)
    {
        var t = SimpleText(text, size, color);
        t.Margin = new Thickness(0, topMargin, 0, 0);
        return t;
    }

    /// <summary>
    /// 把中间那片主页渲染成**桌面版**的样子（Web 引擎那两张卡同时收起）。
    ///
    /// 为什么用代码而不是 XAML：内容里有本机实测值（版本号、安装目录、日志目录、在不在跑），
    /// 而且要和右栏卡片保持同源判据；写死在 XAML 里就会出现"两处各说一个版本号"的老问题。
    /// 每次换目标/刷新都整体重建，避免旧值残留。
    /// </summary>
    internal void RenderDesktopHome()
    {
        try
        {
            if (DesktopHomePanel == null) return;
            DesktopHomePanel.Children.Clear();

            string ver = DesktopDetector.ReadVersion(GuardPaths.DesktopInstallDir);
            bool installed = GuardPaths.DesktopExeFound;
            bool running = DesktopAppRunning();
            _desktopHomeSig = DesktopHomeSignature(running);
            var grey = Color.FromRgb(0x8E, 0x8E, 0x93);
            var white = Color.FromRgb(0xF5, 0xF5, 0xF7);
            var green = Color.FromRgb(0x34, 0xC7, 0x59);
            var blue = Color.FromRgb(0x5A, 0xC8, 0xFA);

            // ① 状态
            var statusRow = new StackPanel { Orientation = Orientation.Horizontal };
            statusRow.Children.Add(new Border
            {
                Width = 10, Height = 10, CornerRadius = new CornerRadius(5),
                Background = new SolidColorBrush(running ? green : Color.FromRgb(0x48, 0x48, 0x4A)),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0)
            });
            statusRow.Children.Add(SimpleText(running ? "桌面版正在运行" : "桌面版未运行", 15, white));
            DesktopHomePanel.Children.Add(statusRow);

            DesktopHomePanel.Children.Add(HomeLine(
                installed ? "安装在：" + GuardPaths.DesktopInstallDir : "未检测到桌面版（可到「设置 → 路径」填写安装目录）",
                11.5, grey, 8));
            DesktopHomePanel.Children.Add(HomeLine(
                ver.Length > 0 ? "版本 " + ver : "版本：读取不到", 11.5, white, 4));

            // ② 快捷操作：只留两颗（2.2.1，用户："去掉繁杂的按钮，就一个检查更新和打开安装目录"）。
            //   「启动 / 结束 / 打开桌面版」右栏「服务控制」已经有，「打开日志目录」日志页有入口，这里不再重复。
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
            row.Children.Add(HomeButton("检查更新", Color.FromRgb(0x00, 0x7A, 0xFF), () => _ = CheckDesktopUpdateAsync()));
            row.Children.Add(HomeButton("打开安装目录", blue, () =>
            {
                if (installed) OpenFolder(GuardPaths.DesktopInstallDir);
                else GuardDialog.Show("未检测到桌面版安装目录。", "打开安装目录", MessageBoxButton.OK, MessageBoxImage.Information);
            }));
            DesktopHomePanel.Children.Add(row);
        }
        catch (Exception ex) { Logger.LogError("RenderDesktopHome", ex); }
    }

    /// <summary>
    /// 桌面版安装包直链（纯函数，便于自检）。官方源同一主机、文件名规律固定（2026-10-25 实测：
    /// <c>deepseek-harness-0.2.0-rc.2 / 0.2.1-rc.1 / rc.2 / rc.3</c> 全部 HEAD 200，历史安装包不删）：
    /// <c>https://download.deepseek.com/dsh-desk/bin/win-x64/deepseek-harness-{版本}-win-x64.exe</c>。
    /// 版本为空 / 超长 / 含路径或查询字符 / 非语义化字符 ⇒ 空串（调用方拒绝，绝不拼 URL）。
    /// </summary>
    internal static string DesktopInstallerUrlFor(string? version)
    {
        string v = (version ?? "").Trim();
        if (v.Length == 0 || v.Length > 64 || v.Contains("..")) return "";
        foreach (char c in v)
            if (!(char.IsAsciiLetterOrDigit(c) || c == '.' || c == '-' || c == '+')) return "";
        return $"https://{DesktopDownloadHost}/dsh-desk/bin/win-x64/deepseek-harness-{Uri.EscapeDataString(v)}-win-x64.exe";
    }

    /// <summary>桌面版回滚列表用的 npm 包名：官方 CLI 与桌面版同一版本线（0.2.1-rc.3 双侧一致，2026-10-25 实测）。</summary>
    internal const string DesktopNpmPackage = "@deepseek-ai/dsh";

    /// <summary>
    /// 合并桌面版「可选版本」列表（纯函数，便于自检）：已装版本与官方清单最新版置顶去重，
    /// 其余按 npm 版本表（已是新到旧排序）追加，截 <paramref name="limit"/> 条。输入里的空串丢弃。
    /// </summary>
    internal static List<string> MergeDesktopVersionChoices(string? installed, string? feedLatest,
        IEnumerable<string?> npmVersions, int limit)
    {
        var result = new List<string>();
        void Add(string? v)
        {
            string s = (v ?? "").Trim();
            if (s.Length == 0 || result.Contains(s)) return;
            if (result.Count < limit) result.Add(s);
        }
        Add(installed);
        Add(feedLatest);
        if (npmVersions != null)
            foreach (var v in npmVersions) Add(v);
        return result;
    }

    /// <summary>
    /// 逐个 HEAD 探测「这个版本的安装包在官方源还在不在」，收满 <paramref name="want"/> 个或探完为止。
    /// 单个探测失败（网络抖动/404）只跳过该版本，不抛。历史安装包实测长期保留（见 <see cref="DesktopInstallerUrlFor"/>）。
    /// </summary>
    private static async Task<List<string>> ProbeDesktopInstallersAsync(IEnumerable<string> versions, int want)
    {
        var ok = new List<string>();
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("DSHGuard");
            foreach (var v in versions)
            {
                if (ok.Count >= want) break;
                string url = DesktopInstallerUrlFor(v);
                if (url.Length == 0) continue;
                try
                {
                    using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Head, url);
                    using var resp = await http.SendAsync(req);
                    if (resp.StatusCode == System.Net.HttpStatusCode.OK) ok.Add(v);
                }
                catch { /* 单个版本探不到就跳过，不挡住整张列表 */ }
            }
        }
        catch (Exception ex) { Logger.LogError("ProbeDesktopInstallersAsync", ex, GuardTarget.Desktop); }
        return ok;
    }

    /// <summary>桌面版「选择版本」下拉的状态旗（防 DropDownOpened 连发两次导致重复拉取）。</summary>
    private sealed class DesktopVersionComboState { public bool Loaded; }

    private void DesktopVersionCombo_DropDownOpened(object? sender, EventArgs e)
    {
        if (sender is not ComboBox combo) return;
        if (combo.Tag is not DesktopVersionComboState state || state.Loaded) return;
        state.Loaded = true;
        _ = LoadDesktopVersionsForComboAsync(combo);
    }

    /// <summary>
    /// 填桌面版卡的「选择版本」下拉：npm 版本表（官方 CLI 与桌面版同一版本线）+ 已装版本 + 官方清单
    /// 最新版合并，再逐个 HEAD 探测安装包在不在，只留还在的。标记与插件页同一套口径：(当前)/(最新)。
    /// </summary>
    /// <summary>
    /// 桌面版下拉的真实行（纯函数，便于自检）：当前版本 (当前)、清单最新版 (最新)。
    /// </summary>
    internal static List<(string Text, object? Tag, string? Tip)> BuildDesktopVersionRows(
        IReadOnlyList<string> available, string installed, string feedLatest)
    {
        var rows = new List<(string, object?, string?)>();
        foreach (var v in available)
        {
            bool isCurrent = installed.Length > 0 && v.Equals(installed, StringComparison.OrdinalIgnoreCase);
            bool isLatest = feedLatest.Length > 0 && v.Equals(feedLatest, StringComparison.OrdinalIgnoreCase);
            string text = v + (isCurrent ? " (当前)" : "") + (isLatest && !isCurrent ? " (最新)" : "");
            rows.Add((text, v,
                isCurrent ? $"桌面版当前就是 {v}"
                          : $"把桌面版安装/回退为 {v}（安装包来自官方源 download.deepseek.com）"));
        }
        return rows;
    }

    private async Task LoadDesktopVersionsForComboAsync(ComboBox combo)
    {
        try
        {
            string installed = DesktopDetector.ReadVersion(GuardPaths.DesktopInstallDir);
            string feedLatest = "";
            try { feedLatest = (await QueryDesktopFeedAsync())?.Version ?? ""; } catch { }
            var npm = await PluginManager.FetchNpmVersionsAsync(DesktopNpmPackage, GuardTarget.Desktop, 12);
            var merged = MergeDesktopVersionChoices(installed, feedLatest, npm, 12);
            var available = await ProbeDesktopInstallersAsync(merged, 8);

            await Dispatcher.InvokeAsync(() =>
            {
                var rows = BuildDesktopVersionRows(available, installed, feedLatest);
                combo.ToolTip = rows.Count == 0
                    ? "官方源与 npm 版本表都暂时不可用（离线？），稍后再试。"
                    : null;
                ApplyVersionComboRows(combo, rows);   // 共享出口：占位重建 + 选中占位（见 Tools.cs 方法注释）
                combo.SelectionChanged += DesktopVersionCombo_SelectionChanged;
            });
        }
        catch (Exception ex)
        {
            Logger.LogError("LoadDesktopVersionsForComboAsync", ex, GuardTarget.Desktop);
            await Dispatcher.InvokeAsync(() =>
            {
                combo.Items.Clear();
                combo.Items.Add(new ComboBoxItem
                {
                    Content = "加载失败",
                    IsEnabled = false,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30))
                });
                combo.SelectedIndex = 0;
            });
        }
    }

    private async void DesktopVersionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo) return;
        if (combo.SelectedItem is not ComboBoxItem item) return;
        if (item.Tag is not string version || version.Length == 0) return;   // 占位项：no-op，事件保持挂着

        string installed = DesktopDetector.ReadVersion(GuardPaths.DesktopInstallDir);
        if (installed.Length > 0 && version.Equals(installed, StringComparison.OrdinalIgnoreCase))
        {
            // 选的就是当前版本：拨回占位项（"动作入口"不是"状态显示"，且同一项再点一次不触发）
            combo.SelectedIndex = 0;
            return;
        }

        // 真的要切版本了才摘事件
        combo.SelectionChanged -= DesktopVersionCombo_SelectionChanged;
        try
        {
            bool downgrade = installed.Length > 0 && VersionInfo.Compare(version, installed) < 0;
            var r = GuardDialog.Show(
                $"把桌面版（DSH Desktop）安装为 {version} 吗？\n本机当前：{(installed.Length > 0 ? installed : "未检测到")}\n\n" +
                (downgrade ? "· 这是**回退到旧版本**，可用于规避新版问题\n" : "") +
                "· 安装包来自官方源 download.deepseek.com，下载后先验数字签名再启动\n" +
                "· 安装时会先关闭正在运行的桌面版",
                "切换桌面版版本", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) { UpdateVersionCard(); return; }
            await InstallDesktopVersionAsync(version);
        }
        finally
        {
            UpdateVersionCard();
        }
    }

    /// <summary>
    /// 下载指定版本的桌面版安装包并启动安装器（回滚/指定版本共用入口）。
    /// 校验与「检查更新桌面版」走**同一个唯一入口** <see cref="VerifyDesktopInstaller"/>：
    /// 旧版本清单不在手上（无 SHA-512），靠 Authenticode 数字签名把关 —— 无有效签名一律删除拒装。
    /// </summary>
    private async Task InstallDesktopVersionAsync(string version)
    {
        string url = DesktopInstallerUrlFor(version);
        if (url.Length == 0)
        {
            GuardDialog.Show($"「{version}」不是可用的版本号，已取消。", "切换桌面版版本", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        string dir = Path.Combine(Path.GetTempPath(), "DSHGuard-DesktopUpdate");
        string file = Path.Combine(dir, $"deepseek-harness-{version}-win-x64.exe");
        try
        {
            AddEvent($"开始下载桌面版 {version} 安装包（来源：官方更新源）…", EventKind.Update, GuardTarget.Desktop);
            Directory.CreateDirectory(dir);
            using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(30) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("DSHGuard");
                using var resp = await http.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(file);
                await resp.Content.CopyToAsync(fs);
            }
            var fi = new FileInfo(file);
            if (!fi.Exists || fi.Length == 0) throw new IOException("下载到的安装包为空");

            string trustNote = await Task.Run(() => VerifyDesktopInstaller(file, ""));
            Logger.NoteDiagnosis($"桌面版指定版本 {version}：安装包校验通过（{trustNote}）", GuardTarget.Desktop);

            AddEvent($"已下载桌面版 {version} 安装包（{GuardPaths.HumanSize(fi.Length)}），启动安装器", EventKind.Update, GuardTarget.Desktop);
            if (DesktopAppRunning())
            {
                GuardDialog.Show("桌面版正在运行。安装器马上会启动，请按它的提示关闭桌面版。",
                    "切换桌面版版本", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true, WorkingDirectory = dir });
            AddEvent($"已启动桌面版 {version} 安装器，安装完成后可点「启动桌面版」", EventKind.Good, GuardTarget.Desktop);
        }
        catch (Exception ex)
        {
            Logger.LogError("InstallDesktopVersionAsync", ex, GuardTarget.Desktop);
            GuardDialog.Show($"下载桌面版 {version} 安装包失败：{ex.Message}\n\n详细原因已记入日志（日志页可导出诊断）。",
                "切换桌面版版本", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 「设置 → 版本」页在桌面版目标下的那张卡（替代 Web 引擎的「运行中的 DSH」与「版本记忆」两张卡）：
    /// 桌面版的版本、安装目录、日志目录、运行状态，外加「检查更新桌面版 / 打开安装目录 / 打开日志目录」。
    /// 判据与右栏版本卡同源（DesktopDetector.ReadVersion + GuardPaths.DesktopExeFound），不另立一套。
    /// </summary>
    private Border BuildDesktopVersionCard()
    {
        var card = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
            Padding = new Thickness(14, 12, 14, 12)
        };
        var sp = new StackPanel();
        card.Child = sp;
        var grey = Color.FromRgb(0x8E, 0x8E, 0x93);
        var white = Color.FromRgb(0xF5, 0xF5, 0xF7);
        var green = Color.FromRgb(0x34, 0xC7, 0x59);

        sp.Children.Add(SimpleText("桌面版（DSH Desktop）", 13, Color.FromRgb(0x5A, 0xC8, 0xFA), true));
        string ver = DesktopDetector.ReadVersion(GuardPaths.DesktopInstallDir);
        sp.Children.Add(SimpleText(ver.Length > 0 ? "版本 " + ver : "版本：读取不到（未检测到安装）", 12.5, white));
        sp.Children.Add(SimpleText(GuardPaths.DesktopExeFound
            ? "安装目录：" + GuardPaths.DesktopInstallDir
            : "安装目录：未检测到（可到「路径」页填写）", 11, grey));
        sp.Children.Add(SimpleText("运行状态：" + (DesktopAppRunning() ? "正在运行" : "未运行"), 11,
            DesktopAppRunning() ? green : grey));

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        row.Children.Add(HomeButton("检查更新桌面版", Color.FromRgb(0x00, 0x7A, 0xFF), () => _ = CheckDesktopUpdateAsync()));
        row.Children.Add(HomeButton("打开安装目录", Color.FromRgb(0x5A, 0xC8, 0xFA), () =>
        {
            if (GuardPaths.DesktopExeFound) OpenFolder(GuardPaths.DesktopInstallDir);
            else GuardDialog.Show("未检测到桌面版安装目录。", "打开安装目录", MessageBoxButton.OK, MessageBoxImage.Information);
        }));
        row.Children.Add(HomeButton("打开日志目录", HomeActionFill, () => OpenFolder(Logger.LogDirForTarget(GuardTarget.Desktop))));
        sp.Children.Add(row);

        // 2026-10-25（2.2.5）：「选择版本」回滚下拉，与插件页"选择版本…"同一套交互（懒加载/标记/确认）。
        //   数据源见 LoadDesktopVersionsForComboAsync：npm 版本表 + 已装版本 + 官方清单最新版，
        //   逐个 HEAD 探测安装包在官方源还在不在，只列还在的。
        var versionRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var versionCombo = new ComboBox
        {
            Width = 132,
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)FindResource("SlimCombo"),
            Tag = new DesktopVersionComboState(),
            ToolTip = "选一个版本把桌面版安装/回退过去（含旧版本，用于回退）"
        };
        versionCombo.Items.Add(new ComboBoxItem
        {
            // (_08 P1) 占位项保持可选中（Tag=null ⇒ SelectionChanged no-op），加载后选中停在它身上。
            Content = "选择版本…",
            Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93))
        });
        versionCombo.SelectedIndex = 0;
        versionCombo.DropDownOpened += DesktopVersionCombo_DropDownOpened;
        versionRow.Children.Add(versionCombo);
        sp.Children.Add(versionRow);
        return card;
    }

    /// <summary>
    /// 主页里「打开日志目录」那颗按钮的底色：品牌蓝 <c>#007AFF</c>。
    ///
    /// 走过两轮才对：先是实心中灰 <c>#8E8E93</c>（用户 2026-10-04 反馈"发暗"，日间映射成
    /// <c>#6B6B70</c> 深灰配近黑字，像颗禁用按钮），中途试过半透明白（<c>#18FFFFFF</c>，日间
    /// 映射成半透明黑 ⇒ 灰底深字），**仍然被同一句反馈打回**（"这俩按钮还是发黑，
    /// 你改个有颜色的得了"）。结论写在这里免得再改第四遍：这个位置要的是**实心强调色**，
    /// 不是半透明底。取 <c>#007AFF</c> 是因为日志页 / 快照页的「打开目录」也是它 ——
    /// 同名同动作的按钮在全应用长同一个样。
    /// 不在主题映射表里 ⇒ 两套主题同一个值；满足 <c>IsAccent</c> 的 b&gt;180 &amp;&amp; r&lt;130
    /// ⇒ 白字在两套主题下都保持白色（见 <c>ThemeManager.RemapText</c> 的近白文字规则）。
    /// </summary>
    private static readonly Color HomeActionFill = Color.FromRgb(0x00, 0x7A, 0xFF);

    /// <summary>主页里的扁按钮（沿用迷你按钮那套配色与手型光标 ⇒ ButtonFx 会自动给它悬停/按下动效）。</summary>
    private static Border HomeButton(string text, Color background, Action onClick)
    {
        var b = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(13, 7, 13, 7),
            Margin = new Thickness(0, 0, 8, 0),
            Background = new SolidColorBrush(background),
            Cursor = Cursors.Hand,
            Child = new TextBlock { Text = text, FontSize = 12, Foreground = Brushes.White }
        };
        b.MouseLeftButtonDown += (_, _) => { try { onClick(); } catch (Exception ex) { Logger.LogError("HomeButton:" + text, ex); } };
        return b;
    }

    /// <summary>用资源管理器打开一个目录（不存在就如实说，不静默）。</summary>
    private static void OpenFolder(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                GuardDialog.Show("目录不存在：" + dir, "打开目录", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Logger.LogError("OpenFolder", ex); }
    }

    /// <summary>右栏主按钮在桌面版目标下的动作：启动桌面版主程序。</summary>
    private void LaunchDesktopApp()
    {
        try
        {
            string exe = DesktopExePath;
            if (exe.Length == 0)
            {
                GuardDialog.Show(
                    "未检测到 DSH 桌面版。\n\n如果装在自定义位置，可到「设置 → 路径」填写桌面版安装目录。",
                    "启动桌面版", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!File.Exists(exe))
            {
                GuardDialog.Show("桌面版主程序不存在：\n" + exe,
                    "启动桌面版", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            bool wasRunning = DesktopAppRunning();
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                WorkingDirectory = GuardPaths.DesktopInstallDir
            });
            AddEvent(wasRunning ? "已唤起 DSH 桌面版" : "已启动 DSH 桌面版",
                EventKind.Good, GuardTarget.Desktop);
            // 正常启动不写日志（2.2.1，用户："正常启动也会被计入日志"）：异常日志只留异常，
            //   这件事事件信息栏里已有「已启动 DSH 桌面版」；启动失败走下面 catch 的 LogError，照常留证。
            RefreshDesktopCardSoon();
        }
        catch (Exception ex)
        {
            Logger.LogError("LaunchDesktopApp", ex);
            GuardDialog.Show("启动桌面版失败：" + ex.Message + "\n\n详细原因已记入日志。",
                "启动桌面版", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>启动后隔一会儿把卡片里的"在不在跑"刷新一次（进程起来需要时间，立刻读会读成未运行）。</summary>
    private async void RefreshDesktopCardSoon()
    {
        try
        {
            await Task.Delay(2500);
            if (_ctx.Target == GuardTarget.Desktop) { RefreshDesktopRunState(); UpdateVersionCard(); }
        }
        catch (Exception ex) { Logger.LogError("RefreshDesktopCardSoon", ex); }
    }

    /// <summary>桌面版目标下渲染「DSH 版本」卡（当前版本 / 安装目录 / 运行状态）。</summary>
    private void RenderDesktopVersionCard()
    {
        if (VerCardCurrent == null) return;

        var grey = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93));
        var green = new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x59));
        string ver = DesktopDetector.ReadVersion(GuardPaths.DesktopInstallDir);

        VerCardCurrent.Text = ver.Length > 0 ? "桌面版 " + ver : "桌面版未检测到";
        VerCardCurrent.Foreground = Brushes.White;
        VerCardCurrent.FontSize = 14;
        VerCardCurrent.FontWeight = FontWeights.SemiBold;

        VerCardSub.Text = GuardPaths.DesktopExeFound
            ? "安装目录：" + GuardPaths.DesktopInstallDir
            : "未找到安装目录（可到「设置 → 路径」填写）";
        VerCardSub.Foreground = grey;

        VerCardLatest.Text = "点击检查更新";
        VerCardLatest.Foreground = grey;

        if (VerCardPin != null)
        {
            bool running = DesktopAppRunning();
            VerCardPin.Text = running ? "● 桌面版正在运行" : "○ 桌面版未运行";
            VerCardPin.Foreground = running ? green : grey;
            SyncDesktopHome(running);
        }
    }

    // ══════════════ 桌面版更新（官方 electron-updater 源） ══════════════

    /// <summary>
    /// 清单里的一项：版本号、安装包文件名（只作落盘名）、下载地址与其 SHA-512（Base64，清单没给为空串）。
    /// </summary>
    internal readonly record struct DesktopFeedInfo(string Version, string Path, string Sha512 = "", string Url = "");

    /// <summary>安装包允许来自的主机（与 <see cref="DesktopFeedUrl"/> 同一官方域名）。</summary>
    private const string DesktopDownloadHost = "download.deepseek.com";

    /// <summary>
    /// 把 electron-updater 清单展开成「(缩进, 键, 值)」序列（纯函数）。只处理清单实际用到的写法：
    /// 普通标量、引号标量、列表项 <c>- key: v</c>，以及折叠/字面块 <c>&gt;-</c> <c>&gt;</c> <c>|</c> <c>|-</c>
    /// （值写在下一行、缩进更深 —— 官方源 2026-10 实测就是这种写法，旧解析把 <c>path</c> 读成了字面量 <c>&gt;-</c>）。
    /// </summary>
    internal static List<(int Indent, string Key, string Value)> FlattenFeedYaml(string yml)
    {
        var lines = yml.Replace("\r\n", "\n").Split('\n');
        var result = new List<(int, string, string)>();
        for (int i = 0; i < lines.Length; i++)
        {
            string raw = lines[i];
            if (raw.Trim().Length == 0 || raw.TrimStart().StartsWith("#")) continue;
            int indent = raw.Length - raw.TrimStart().Length;
            string body = raw.Trim();
            if (body.StartsWith("- ")) { indent += 2; body = body.Substring(2).Trim(); }
            int colon = body.IndexOf(':');
            if (colon <= 0) continue;
            string key = body.Substring(0, colon).Trim();
            string val = body.Substring(colon + 1).Trim();
            if (val is ">-" or ">" or "|" or "|-" or ">+" or "|+")
            {
                bool literal = val.StartsWith("|");
                var parts = new List<string>();
                while (i + 1 < lines.Length)
                {
                    string nxt = lines[i + 1];
                    if (nxt.Trim().Length == 0) { i++; continue; }
                    int ni = nxt.Length - nxt.TrimStart().Length;
                    if (ni <= indent) break;
                    parts.Add(nxt.Trim());
                    i++;
                }
                val = string.Join(literal ? "\n" : " ", parts);
            }
            else val = val.Trim('"', '\'');
            result.Add((indent, key, val));
        }
        return result;
    }

    /// <summary>
    /// 从官方更新清单里解析出最新版本、安装包地址与 SHA-512（纯函数，便于自检）。
    /// <para>
    /// <c>path</c> 两种写法都认：同源相对文件名（拼到 <see cref="DesktopFeedUrl"/> 后），
    /// 或绝对地址 —— 绝对地址必须是 https 且主机恰为 <see cref="DesktopDownloadHost"/>，
    /// 否则整份清单作废（更新源被篡改时的最后一道闸）。落盘文件名取地址最后一段，同样过非法字符检查。
    /// </para>
    /// </summary>
    internal static DesktopFeedInfo? ParseDesktopFeed(string? yml)
    {
        if (string.IsNullOrWhiteSpace(yml)) return null;
        string ver = "", path = "", topSha = "", curUrl = "";
        var fileSha = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (indent, key, val) in FlattenFeedYaml(yml!))
        {
            bool top = indent == 0;
            switch (key.ToLowerInvariant())
            {
                case "version" when top: ver = val; break;
                case "path" when top: path = val; break;
                case "sha512" when top: topSha = val; break;
                case "sha512": if (curUrl.Length > 0) fileSha[curUrl] = val; break;
                case "url": curUrl = val; break;
            }
        }
        if (ver.Length == 0) return null;
        if (path.Length == 0) path = $"DSH-Desk-Setup-{ver}.exe";

        string url, fileName;
        if (path.Contains("://"))
        {
            if (!Uri.TryCreate(path, UriKind.Absolute, out var u)
                || u.Scheme != Uri.UriSchemeHttps
                || !string.Equals(u.Host, DesktopDownloadHost, StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrEmpty(u.Query) || !string.IsNullOrEmpty(u.Fragment))
                return null;
            url = u.AbsoluteUri;
            fileName = Uri.UnescapeDataString(u.Segments.Length > 0 ? u.Segments[^1] : "");
        }
        else
        {
            fileName = path;
            url = DesktopFeedUrl + Uri.EscapeDataString(path);
        }
        if (fileName.Length == 0 || fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\')
            || fileName.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0
            || !fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return null;

        string sha = topSha.Length > 0 ? topSha : (fileSha.TryGetValue(path, out var s) ? s : "");
        return new DesktopFeedInfo(ver, fileName, sha, url);
    }

    /// <summary>
    /// 桌面版安装包执行前的校验：摘要不符或签名无效 ⇒ 删文件并抛 <see cref="IOException"/>（调用方按下载失败处理）；
    /// 通过则返回一句给日志看的结论。
    /// </summary>
    internal static string VerifyDesktopInstaller(string file, string expectedSha512,
                                                  Func<string, FileTrust.SignatureState>? verifySignature = null)
    {
        string exp = (expectedSha512 ?? "").Trim();
        string shaNote;
        if (exp.Length > 0)
        {
            string act = FileTrust.Sha512Base64(file);
            if (!string.Equals(exp, act, StringComparison.Ordinal))
            {
                try { File.Delete(file); } catch { }
                throw new IOException("安装包校验失败（SHA-512 与官方清单不一致），已删除，未执行");
            }
            shaNote = "SHA-512 与清单一致";
        }
        else shaNote = "清单未提供 SHA-512";

        // 签名检查可注入：自检只想验证"摘要 + 签名结论"如何组合，不该依赖系统对一个假 exe 返回什么错误码
        var sig = (verifySignature ?? FileTrust.VerifySignature)(file);
        if (sig == FileTrust.SignatureState.Invalid)
        {
            try { File.Delete(file); } catch { }
            throw new IOException("安装包的数字签名校验失败，已删除，未执行");
        }
        // 既没有摘要也没有有效签名 ⇒ 没有任何来源保证，宁可让用户去官网手动下载
        if (exp.Length == 0 && sig != FileTrust.SignatureState.Valid)
        {
            try { File.Delete(file); } catch { }
            throw new IOException("官方清单未提供校验值且安装包无有效签名，无法确认来源，已删除，未执行");
        }
        return shaNote + "；" + FileTrust.Describe(sig);
    }

    /// <summary>当前固定源里有没有比本机更新的桌面版；null = 查不到（离线 / 源不可用）。</summary>
    private async Task<DesktopFeedInfo?> QueryDesktopFeedAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("DSHGuard");
            string url = DesktopFeedUrl + DesktopFeedChannel + ".yml";
            string yml = await http.GetStringAsync(url);
            return ParseDesktopFeed(yml);
        }
        catch (Exception ex)
        {
            Logger.NoteDiagnosis("桌面版更新清单查询失败：" + ex.Message, GuardTarget.Desktop);
            return null;
        }
    }

    /// <summary>
    /// 「一键更新桌面版」：查官方清单 → 比版本 → 下载安装包 → 交给它自己的安装器。
    /// 与守护壳自身更新同一套纪律：先落临时目录、校验文件存在、不覆盖用户已装的程序。
    /// </summary>
    private async Task CheckDesktopUpdateAsync()
    {
        if (VerCardLatest != null)
        {
            VerCardLatest.Text = "正在检查桌面版更新…";
            VerCardLatest.Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93));
        }

        string installed = DesktopDetector.ReadVersion(GuardPaths.DesktopInstallDir);
        if (installed.Length == 0)
        {
            GuardDialog.Show("未检测到 DSH 桌面版，无法检查更新。\n\n可到「设置 → 路径」填写桌面版安装目录。",
                "桌面版更新", MessageBoxButton.OK, MessageBoxImage.Information);
            UpdateVersionCard();
            return;
        }

        var feed = await QueryDesktopFeedAsync();
        if (feed == null)
        {
            GuardDialog.Show("查不到桌面版更新清单（离线，或官方更新源暂时不可用）。\n\n稍后再试即可；详细原因已记入日志。",
                "桌面版更新", MessageBoxButton.OK, MessageBoxImage.Warning);
            UpdateVersionCard();
            return;
        }

        var latest = feed.Value;
        bool newer = VersionInfo.Compare(latest.Version, installed) > 0;
        if (!newer)
        {
            GuardDialog.Show($"桌面版已是最新。\n\n本机：{installed}\n官方源：{latest.Version}",
                "桌面版更新", MessageBoxButton.OK, MessageBoxImage.Information);
            UpdateVersionCard();
            return;
        }

        bool running = DesktopAppRunning();
        var answer = GuardDialog.ShowCustom(
            $"发现桌面版新版本：{latest.Version}\n本机当前：{installed}\n\n" +
            "现在下载并启动官方安装包吗？\n" +
            "· 安装时会先关闭正在运行的桌面版\n" +
            "· 安装包的来源是官方更新源：" + DesktopFeedUrl,
            "桌面版更新", MessageBoxImage.Question,
            new GuardDialog.DialogButton("下载并安装", MessageBoxResult.Yes,
                Color.FromRgb(0x34, 0xC7, 0x59), IsDefault: true),
            new GuardDialog.DialogButton("稍后", MessageBoxResult.No,
                Color.FromRgb(0x8E, 0x8E, 0x93), IsCancel: true));
        if (answer != MessageBoxResult.Yes) { UpdateVersionCard(); return; }

        string dir = Path.Combine(Path.GetTempPath(), "DSHGuard-DesktopUpdate");
        string file = Path.Combine(dir, latest.Path);
        try
        {
            Directory.CreateDirectory(dir);
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("DSHGuard");
                using var resp = await http.GetAsync(latest.Url.Length > 0 ? latest.Url : DesktopFeedUrl + latest.Path, HttpCompletionOption.ResponseHeadersRead);
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(file);
                await resp.Content.CopyToAsync(fs);
            }
            var fi = new FileInfo(file);
            if (!fi.Exists || fi.Length == 0)
                throw new IOException("下载到的安装包为空");

            // 2026-10-16 审计 H1：执行前校验。清单给了 sha512 ⇒ 必须一致；签名存在却不通过 ⇒ 拒绝。
            string trustNote = await Task.Run(() => VerifyDesktopInstaller(file, latest.Sha512));   // 近 300 MB 的哈希 + 验签不放在界面线程
            Logger.NoteDiagnosis("桌面版安装包校验：" + trustNote, GuardTarget.Desktop);

            AddEvent($"已下载桌面版安装包 {latest.Version}（{GuardPaths.HumanSize(fi.Length)}）", EventKind.Update, GuardTarget.Desktop);
            if (running)
            {
                GuardDialog.Show("桌面版正在运行。安装器马上会启动，请按它的提示关闭桌面版。",
                    "桌面版更新", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true, WorkingDirectory = dir });
            AddEvent("已启动桌面版安装器，" + latest.Version + " 安装完成后可点「启动桌面版」", EventKind.Good, GuardTarget.Desktop);
        }
        catch (Exception ex)
        {
            Logger.LogError("CheckDesktopUpdateAsync", ex, GuardTarget.Desktop);
            GuardDialog.Show("下载桌面版安装包失败：" + ex.Message + "\n\n详细原因已记入日志（日志页可导出诊断）。",
                "桌面版更新", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        UpdateVersionCard();
    }
}