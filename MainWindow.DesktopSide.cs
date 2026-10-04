using System;
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
    /// 右栏按管理对象改写：桌面版显示桌面版自己的信息，Web 照旧。
    /// 由 <see cref="ApplyTargetChrome"/> 在每次换目标后调用（唯一入口，与其它目标相关外观同一处）。
    /// </summary>
    internal void ApplyRightPanelForTarget()
    {
        try
        {
            bool desktop = _ctx.Target == GuardTarget.Desktop;

            if (MainBtnText != null)
                MainBtnText.Text = desktop ? "启动桌面版" : "一键启动引擎";
            if (IdleButtonPanel != null)
                IdleButtonPanel.ToolTip = desktop
                    ? "启动官方 DSH 桌面版（DeepSeek Harness）；已在运行时会把它再唤起来"
                    : "启动 DSH Web 引擎（首次较慢，需要下载运行环境）";

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
            DesktopHomePanel.Children.Add(HomeLine(
                "日志目录：" + Logger.LogDirForTarget(GuardTarget.Desktop), 11, grey, 4));
            DesktopHomePanel.Children.Add(HomeLine(
                "桌面版的运行报错与崩溃记录都写在这个目录里；「日志」页可按「桌面版」筛选查看。",
                11, grey, 2));

            // ② 快捷操作（与右栏那颗主按钮同源：都是"启动桌面版"）
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
            row.Children.Add(HomeButton("启动桌面版", green, () => LaunchDesktopApp()));
            row.Children.Add(HomeButton("打开安装目录", blue, () =>
            {
                if (installed) OpenFolder(GuardPaths.DesktopInstallDir);
                else GuardDialog.Show("未检测到桌面版安装目录。", "打开安装目录", MessageBoxButton.OK, MessageBoxImage.Information);
            }));
            row.Children.Add(HomeButton("检查更新", Color.FromRgb(0x00, 0x7A, 0xFF), () => _ = CheckDesktopUpdateAsync()));
            DesktopHomePanel.Children.Add(row);

            // ③ 日志目录一个入口（运维时最常用）
            var row2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            row2.Children.Add(HomeButton("打开日志目录", Color.FromRgb(0x8E, 0x8E, 0x93),
                () => OpenFolder(Logger.LogDirForTarget(GuardTarget.Desktop))));
            DesktopHomePanel.Children.Add(row2);
        }
        catch (Exception ex) { Logger.LogError("RenderDesktopHome", ex); }
    }

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
            Logger.NoteDiagnosis("桌面版启动：" + exe, GuardTarget.Desktop);
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
            if (_ctx.Target == GuardTarget.Desktop) UpdateVersionCard();
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

        VerCardLatest.Text = "点击这张卡：检查桌面版更新（官方更新源）";
        VerCardLatest.Foreground = grey;

        if (VerCardPin != null)
        {
            bool running = DesktopAppRunning();
            VerCardPin.Text = running ? "● 桌面版正在运行" : "○ 桌面版未运行";
            VerCardPin.Foreground = running ? green : grey;
        }
    }

    // ══════════════ 桌面版更新（官方 electron-updater 源） ══════════════

    /// <summary>清单里的一项：版本号与安装包文件名。</summary>
    internal readonly record struct DesktopFeedInfo(string Version, string Path);

    /// <summary>
    /// 从官方更新清单里解析出最新版本与安装包文件名（纯函数，便于自检）。
    /// 清单是 electron-updater 的 yml，这里只认需要的两行，不引入 YAML 依赖：
    ///   version: 0.2.0-rc.1
    ///   path: DeepSeek-Harness-Setup-0.2.0-rc.1.exe
    /// </summary>
    internal static DesktopFeedInfo? ParseDesktopFeed(string? yml)
    {
        if (string.IsNullOrWhiteSpace(yml)) return null;
        string ver = "", path = "";
        foreach (string raw in yml!.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("version:", StringComparison.OrdinalIgnoreCase))
                ver = line.Substring("version:".Length).Trim().Trim('"', '\'');
            else if (line.StartsWith("path:", StringComparison.OrdinalIgnoreCase))
                path = line.Substring("path:".Length).Trim().Trim('"', '\'');
        }
        if (ver.Length == 0) return null;
        if (path.Length == 0) path = $"DSH-Desk-Setup-{ver}.exe";
        // 只接受同源相对文件名，绝不接受清单里塞进来的绝对路径/跨站地址（更新源被篡改时的最后一道闸）。
        if (path.Contains("..") || path.Contains('/') || path.Contains('\\') || path.Contains("://")) return null;
        return new DesktopFeedInfo(ver, path);
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
                using var resp = await http.GetAsync(DesktopFeedUrl + latest.Path, HttpCompletionOption.ResponseHeadersRead);
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(file);
                await resp.Content.CopyToAsync(fs);
            }
            var fi = new FileInfo(file);
            if (!fi.Exists || fi.Length == 0)
                throw new IOException("下载到的安装包为空");

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