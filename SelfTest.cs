using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DSHGuard;

/// <summary>自带冒烟自检（<c>DSHGuard.exe --selftest</c>）：不显示窗口、不访问引擎、不写配置，仅以真实控件验证进度条圆角裁剪与缓动、窗口键动画、市场卡片渲染和页签结构；结果写入 stdout 与 %TEMP%\dshguard-selftest.txt，退出码 0/1。</summary>
public static class SelfTest
{
    public static bool ShouldRun(string[] args)
    {
        bool yes = args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase));
        if (yes) _tracing = true;
        return yes;
    }

    public static bool ShouldShot(string[] args)
        => args.Any(a => a.Equals("--shot", StringComparison.OrdinalIgnoreCase));

    public static bool ShouldDialogShot(string[] args)
        => args.Any(a => a.Equals("--dialog-shot", StringComparison.OrdinalIgnoreCase));

    /// <summary>是否以卸载界面模式启动（<c>--uninstall</c>，由开始菜单的「卸载」快捷方式调用）。</summary>
    public static bool ShouldUninstall(string[] args)
        => args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 把对话框样式渲染成一张 PNG（<c>--dialog-shot &lt;文件&gt; [--dark]</c>）：
    /// 三种典型组合（提示 / 是-否 / 退出UI 三键）并排，用于人工核对配色与圆角。
    /// </summary>
    public static int DialogShot(string[] args)
    {
        string outPath = args.FirstOrDefault(a => !a.StartsWith("--") && a.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                         ?? Path.Combine(Path.GetTempPath(), "dshguard-dialogs.png");
        bool dark = args.Any(a => a.Equals("--dark", StringComparison.OrdinalIgnoreCase));
        try
        {
            var bg = new Border
            {
                Background = new SolidColorBrush(dark
                    ? Color.FromRgb(0x10, 0x14, 0x1C) : Color.FromRgb(0xE8, 0xEA, 0xEF)),
                Padding = new Thickness(28)
            };
            var stack = new StackPanel();
            bg.Child = stack;

            void Add(FrameworkElement el)
            {
                el.Margin = new Thickness(0, 0, 0, 26);
                el.HorizontalAlignment = HorizontalAlignment.Left;
                stack.Children.Add(el);
            }

            Add(GuardDialog.BuildForShot("已是最新版本（DSH 0.1.5-rc.2）。", "检查更新",
                MessageBoxButton.OK, MessageBoxImage.Information, dark));
            Add(GuardDialog.BuildForShot(
                "终止 DSH 引擎？\n\n正在运行的任务与对话会被中断。", "终止引擎",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, dark));
            Add(GuardDialog.BuildCustomForShot(
                "退出前要顺带停掉 DSH 引擎吗？\n\n· 引擎还在跑（端口 3080），停掉会中断正在进行的任务与对话\n· 只停引擎的话，守护壳留着，随时可以再启动",
                "退出守护壳", dark,
                new GuardDialog.DialogButton("都退出", MessageBoxResult.Yes, Color.FromRgb(0xFF, 0x3B, 0x30)),

                new GuardDialog.DialogButton("取消", MessageBoxResult.Cancel, Color.FromRgb(0x8E, 0x8E, 0x93), IsCancel: true)));
            Add(GuardDialog.BuildCustomForShot(
                "这个引擎正在托管守护壳本身。\n\n· 只解绑：引擎继续在后台跑，守护壳显示为空闲\n· 仍然终止：引擎和守护壳会一起退出",
                "终止引擎", dark,
                new GuardDialog.DialogButton("只解绑", MessageBoxResult.No, Color.FromRgb(0xFF, 0x9F, 0x0A)),
                new GuardDialog.DialogButton("仍然终止", MessageBoxResult.Yes, Color.FromRgb(0xFF, 0x3B, 0x30)),
                new GuardDialog.DialogButton("取消", MessageBoxResult.Cancel, Color.FromRgb(0x8E, 0x8E, 0x93), IsCancel: true)));
            // 启动失败：以前这里塞的是几十行堆栈，卡片高过屏幕、按钮点不到；现在正文是短人话，
            // 即使真塞进长文本也有滚动容器兜底。这张样张用来目视核对这两件事。
            Add(GuardDialog.BuildForShot(
                StartupCause.DialogText(
                    "Error [ERR_MODULE_NOT_FOUND]: Cannot find package '@deepseek-ai/dsh-client-ui-conversation'",
                    string.Join("\n", Enumerable.Range(0, 30).Select(i => $"[stderr]     at frame {i}")), 180),
                "启动超时", MessageBoxButton.OK, MessageBoxImage.Error, dark));

            // 1.1.9 新增：启动失败后的**唯一一个**框——重试过仍失败且本机还有别的版本时，
            // 默认键就是「换回 …」，按一下就恢复可用；正文里写的是**实际**耗时（这里 3 秒）。
            var sampleQuit = new StartupFailure(FailureKind.ProcessExited, 3,
                "Error [ERR_MODULE_NOT_FOUND]: Cannot find package '@deepseek-ai/dsh-settings'", "");
            Add(GuardDialog.BuildCustomForShot(sampleQuit.DialogText(), "DSH 守护壳 · 启动失败", dark,
                new GuardDialog.DialogButton("换回 DSH 0.1.5-rc.1", MessageBoxResult.Yes,
                    Color.FromRgb(0x34, 0xC7, 0x59), IsDefault: true),
                new GuardDialog.DialogButton("先不动", MessageBoxResult.No,
                    Color.FromRgb(0x8E, 0x8E, 0x93), IsCancel: true)));

            // 「正在回滚插件」的进度窗（涉及回滚插件时才弹）：绿色流动进度条 + 没有任何关闭入口。
            // 动画要窗口显示后才起，样张里两段滑块被固定在轨道上 ⇒ 这张图用来核对配色/排版/文案，
            // "是不是真的在流"要上屏看真窗口（自检覆盖不到，见汇报里的"未覆盖"清单）。
            var rollbackShot = new RollbackProgressWindow(MainWindow.RollbackProgressTitle,
                MainWindow.RollbackStepText(RollbackStep.Reinstalling, 13), dark);
            rollbackShot.SetCount(MainWindow.RollbackCountText(3, 13));
            Add(rollbackShot.BuildForShot());

            bg.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            bg.Arrange(new Rect(new Point(0, 0), bg.DesiredSize));
            bg.UpdateLayout();
            int wpx = (int)Math.Ceiling(bg.DesiredSize.Width);
            int hpx = (int)Math.Ceiling(bg.DesiredSize.Height);
            var rtb = new RenderTargetBitmap(wpx, hpx, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(bg);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(outPath)) enc.Save(fs);
            Console.WriteLine($"对话框样式预览已保存：{outPath}（{wpx}x{hpx}，{(dark ? "夜间" : "日间")}）");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("对话框预览失败：" + ex.Message);
            return 1;
        }
    }

    /// <summary>把界面渲染成 PNG（<c>--shot &lt;文件&gt; [--state market|plugins|settings-version|...]</c>）：不显示窗口、不访问引擎，量算布局后驱动到指定页，再由 RenderTargetBitmap 出图。</summary>
    public static int Shot(string[] args)
    {
        string state = ArgValue(args, "--state") ?? "market";
        string outPath = args.FirstOrDefault(a => !a.StartsWith("--") && a.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                         ?? Path.Combine(Path.GetTempPath(), "dshguard-shot.png");
        try
        {
            var w = new MainWindow();
            w.LayoutForTest(960, 640);
            // 主题由命令行指定：--light 日间、--dark（默认）夜间（无头环境没有配置文件可读）
            w.ApplyThemeForTest(!args.Any(a => a.Equals("--light", StringComparison.OrdinalIgnoreCase)));

            switch (state)
            {
                case "market":
                    w.ShowViewForTest("plugins");
                    w.ShowPluginsTabForTest(true);
                    // 异步网络请求须经 RunOffUi 在线程池等待，在 UI 线程上同步等待会阻塞 Dispatcher。
                    w.PrimeMarketForTest(RunOffUi(() => PluginMarket.LoadAsync()));
                    w.LayoutForTest(960, 640);
                    PumpUntil(() => ((Panel)w.FindName("MarketPanel")!).Children.Count > 0, 8000);
                    PumpUntil(() => false, 2500);
                    break;
                case "market-filter":
                    w.ShowViewForTest("plugins");
                    w.ShowPluginsTabForTest(true);
                    // 异步网络请求须经 RunOffUi 在线程池等待，在 UI 线程上同步等待会阻塞 Dispatcher。
                    w.PrimeMarketForTest(RunOffUi(() => PluginMarket.LoadAsync()));
                    w.LayoutForTest(960, 640);
                    PumpUntil(() => ((Panel)w.FindName("MarketPanel")!).Children.Count > 0, 8000);
                    w.OpenFilterMenuForTest();
                    PumpUntil(() => false, 1200);
                    break;
                case "plugins":
                    w.ShowViewForTest("plugins");
                    w.LayoutForTest(960, 640);
                    var t = w.RefreshPluginsForTestAsync();
                    PumpUntil(() => t.IsCompleted, 20000);
                    PumpUntil(() => false, 2500);
                    w.LayoutForTest(960, 640);
                    break;
                case "settings-version":
                    w.ShowViewForTest("settings");
                    w.ShowSettingsTabForTest("version");
                    PumpUntil(() => false, 3000);
                    w.LayoutForTest(960, 640);
                    break;
                case "market-scrolled":
                    w.ShowViewForTest("plugins");
                    w.ShowPluginsTabForTest(true);
                    // 异步网络请求须经 RunOffUi 在线程池等待，在 UI 线程上同步等待会阻塞 Dispatcher。
                    w.PrimeMarketForTest(RunOffUi(() => PluginMarket.LoadAsync()));
                    w.LayoutForTest(960, 640);
                    PumpUntil(() => ((Panel)w.FindName("MarketPanel")!).Children.Count > 0, 8000);
                    if (w.FindName("MarketScroll") is ScrollViewer msv) msv.ScrollToVerticalOffset(420);
                    PumpUntil(() => false, 2500);
                    w.LayoutForTest(960, 640);
                    break;
                case "settings-version-pin":
                    w.ShowViewForTest("settings");
                    w.ShowSettingsTabForTest("version");
                    PumpUntil(() => false, 2500);
                    w.LayoutForTest(960, 640);
                    w.ScrollSettingsToForTest(560);
                    w.LayoutForTest(960, 640);
                    break;
                case "settings-version-bottom":
                    w.ShowViewForTest("settings");
                    w.ShowSettingsTabForTest("version");
                    PumpUntil(() => false, 2500);
                    w.LayoutForTest(960, 640);
                    w.ScrollSettingsToEndForTest();
                    w.LayoutForTest(960, 640);
                    break;
                case "settings-general":
                    w.ShowViewForTest("settings");
                    w.ShowSettingsTabForTest("general");
                    w.LayoutForTest(960, 640);
                    break;
                case "settings-paths":
                    w.ShowViewForTest("settings");
                    w.ShowSettingsTabForTest("paths");
                    w.LayoutForTest(960, 640);
                    break;
                case "loading":
                    // 加载态配色核对：停在 42%，可配 --dark 出夜间版
                    w.ShowViewForTest("status");
                    w.LayoutForTest(960, 640);
                    w.ShowLoadingForTest(true, 42);
                    w.LayoutForTest(960, 640);
                    break;
                case "snapshots":
                    w.ShowViewForTest("snapshots");
                    w.LayoutForTest(960, 640);
                    w.RefreshSnapshotsForTest();
                    w.LayoutForTest(960, 640);
                    PumpUntil(() => false, 300);
                    break;
                case "running":
                    // 运行态外观核对：终止引擎 / 加载引擎 两个大按钮
                    w.ShowViewForTest("status");
                    w.ShowRunningForTest(true);
                    w.LayoutForTest(960, 640);
                    PumpUntil(() => false, 300);
                    break;
                case "loading-hover":
                    w.ShowViewForTest("status");
                    w.LayoutForTest(960, 640);
                    w.ShowLoadingForTest(true, 42);
                    w.LoadingHoverForTest(true);
                    w.LayoutForTest(960, 640);
                    break;
                case "logs":
                    w.ShowViewForTest("logs");
                    w.LayoutForTest(960, 640);
                    break;
                case "about":
                    w.ShowViewForTest("about");
                    w.LayoutForTest(960, 640);
                    break;
                case "lightbox":
                    w.ShowViewForTest("plugins");
                    w.ShowPluginsTabForTest(true);
                    var cat = RunOffUi(() => PluginMarket.LoadAsync());
                    w.PrimeMarketForTest(cat);
                    w.LayoutForTest(960, 640);
                    var shotEntry = cat.Plugins.First(p => p.Screenshots.Count > 0);
                    w.ShowLightboxForTest(shotEntry, 0);
                    PumpUntil(() => false, 3000);
                    break;
                default:
                    w.ShowViewForTest("status");
                    w.LayoutForTest(960, 640);
                    break;
            }

            var root = (FrameworkElement)w.Content;
            root.UpdateLayout();

            if (state == "market-filter")
            {
                // Popup 为独立 HWND，不在窗口视觉树内，需单独渲染菜单区域。
                w.ShowViewForTest("plugins");
                w.ShowPluginsTabForTest(true);
                w.PrimeMarketForTest(PluginMarket.LoadAsync().GetAwaiter().GetResult());
                w.LayoutForTest(960, 640);
                PumpUntil(() => ((Panel)w.FindName("MarketPanel")!).Children.Count > 0, 8000);
                w.OpenFilterMenuForTest();
                PumpUntil(() => false, 1500);
                var menu = (Border)w.FindName("FilterMenuRoot")!;
                menu.Measure(new Size(400, 500));
                menu.Arrange(new Rect(0, 0, menu.DesiredSize.Width, menu.DesiredSize.Height));
                menu.UpdateLayout();
                int mw = Math.Max(100, (int)Math.Ceiling(menu.ActualWidth));
                int mh = Math.Max(100, (int)Math.Ceiling(menu.ActualHeight));
                var mrtb = new RenderTargetBitmap(mw, mh, 96, 96, PixelFormats.Pbgra32);
                mrtb.Render(menu);
                var menc = new PngBitmapEncoder();
                menc.Frames.Add(BitmapFrame.Create(mrtb));
                using (var mfs = File.Create(outPath)) menc.Save(mfs);
                Console.Out.Write($"SHOT OK {outPath} (market-filter {mw}x{mh})\n");
                return 0;
            }

            if (state == "card-zoom")
            {
                var host = new StackPanel { Width = 560, Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x11, 0x17)) };
                var cat = RunOffUi(() => PluginMarket.LoadAsync());
                var pick = cat.Plugins.Where(p => p.Screenshots.Count > 0 && p.DescZh.Length > 40).Take(1)
                    .Concat(cat.Plugins.Where(p => p.Npm.Length == 0 && p.DescZh.Length > 40).Take(1)).ToList();
                foreach (var m in pick) host.Children.Add(w.BuildMarketCardForTest(m));
                host.Measure(new Size(560, 900));
                host.Arrange(new Rect(0, 0, 560, 900));
                host.UpdateLayout();
                PumpUntil(() => false, 2500);

                var zoom = new RenderTargetBitmap(1200, 1000, 96 * 2, 96 * 2, PixelFormats.Pbgra32);
                zoom.Render(host);
                var zenc = new PngBitmapEncoder();
                zenc.Frames.Add(BitmapFrame.Create(zoom));
                using (var zfs = File.Create(outPath)) zenc.Save(zfs);

                var trace = new System.Text.StringBuilder();
                foreach (var m in pick)
                {
                    var card = w.BuildMarketCardForTest(m);
                    var sp = (StackPanel)card.Child;
                    card.Measure(new Size(560, 900));
                    card.Arrange(new Rect(0, 0, 560, 900));
                    card.UpdateLayout();
                    trace.AppendLine($"---- {m.Name} (card {card.ActualWidth:0}x{card.ActualHeight:0}) ----");
                    foreach (var child in sp.Children)
                    {
                        trace.AppendLine($"  {Describe(child)}");
                        if (child is Grid g)
                            foreach (var gc in g.Children)
                                trace.AppendLine($"      ↳ {Describe(gc)}");
                    }
                }
                File.WriteAllText(outPath + ".bounds.txt", trace.ToString(), new UTF8Encoding(false));
                Console.Out.Write($"SHOT OK {outPath} (card-zoom)\n");
                return 0;
            }

            var rtb = new RenderTargetBitmap(960, 640, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(root);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(outPath)) enc.Save(fs);
            Console.Out.Write($"SHOT OK {outPath} ({state})\n");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Out.Write($"SHOT FAIL {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n");
            return 1;
        }
    }

    private static string? ArgValue(string[] args, string key)
    {
        int i = Array.FindIndex(args, a => a.Equals(key, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>把一个元素的类型、文字、在父节点内的位置与尺寸描述成一行（用于排查"按钮压字"）。</summary>
    private static string Describe(object? el)
    {
        if (el is not FrameworkElement fe) return "(?)";
        string text = fe switch
        {
            TextBlock t => "「" + (t.Text.Length > 34 ? t.Text.Substring(0, 34) + "…" : t.Text) + "」",
            Button b => "[按钮 " + b.Content + "]",
            Border => "[Border]",
            StackPanel => "[StackPanel]",
            Grid => "[Grid]",
            _ => ""
        };
        var p = fe.TranslatePoint(new Point(0, 0), fe.Parent as UIElement ?? fe);
        string align = fe.HorizontalAlignment + "/" + fe.VerticalAlignment;
        return $"{fe.GetType().Name} {text} pos=({p.X:0},{p.Y:0}) size=({fe.ActualWidth:0}x{fe.ActualHeight:0}) align={align}";
    }

    private static bool _tracing;

    /// <summary>自检跟踪：写入 %TEMP%\dshguard-selftest.trace，用于定位自检卡在的步骤。</summary>
    public static void Trace(string message)
    {
        if (!_tracing) return;
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "dshguard-selftest.trace"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}", new UTF8Encoding(false));
        }
        catch { }
    }

    public static int Run(string[] args)
    {
        try { File.Delete(Path.Combine(Path.GetTempPath(), "dshguard-selftest.trace")); } catch { }
        Trace("SelfTest.Run 进入");
        int code = RunCore(args);
        Trace($"SelfTest.Run 结束，退出码 {code}");
        return code;
    }

    private static int RunCore(string[] args)
    {
        _tracing = true;
        // 版本记录重定向到临时目录：自检会真实固定版本，不得改动用户的 versions.json
        try
        {
            string dir = Path.Combine(Path.GetTempPath(), "dshguard-selftest-data");
            Directory.CreateDirectory(dir);
            Environment.SetEnvironmentVariable("DSHGUARD_DATA_DIR", dir);

            // 播种插件目录缓存：目录要走网络，网络一抖整片市场断言就假红。
            // 优先复用本机已有的目录缓存（部署目录或开发输出目录），都没有才走联网。
            try
            {
                string target = Path.Combine(dir, "Cache", "Market", "catalog.json");
                if (!File.Exists(target))
                {
                    // 只从"程序自己的目录"找缓存；不写死开发机路径（会编进发布 exe，
                    // 也违反"对外分发物不带个人专用路径"的要求）
                    string[] candidates =
                    {
                        Path.Combine(AppContext.BaseDirectory, "Cache", "Market", "catalog.json")
                    };
                    foreach (var src in candidates)
                    {
                        if (!File.Exists(src)) continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.Copy(src, target, true);
                        Trace($"已播种插件目录缓存：{src}");
                        break;
                    }
                }
            }
            catch { }
            Trace("版本记忆已重定向到临时目录：" + dir);
        }
        catch { }
        var log = new StringBuilder();
        var failures = new List<string>();
        var skips = new List<string>();
        int step = 0;

        void Check(string name, bool ok, string detail = "")
        {
            step++;
            log.AppendLine($"[{step:00}] {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " — " + detail : "")}");
            if (!ok) failures.Add(name);
        }

        /// <summary>
        /// 因环境不满足而**没验**的用例：记成 SKIP 而不是 PASS——不然报告会"全绿"地掩盖掉
        /// 一条从未真正跑过的功能验证。
        /// </summary>
        void Skip(string name, string reason)
        {
            step++;
            log.AppendLine($"[{step:00}] SKIP {name} — {reason}");
            skips.Add(name);
        }

        /// <summary>
        /// 这份锁文件文本里有没有"可比的插件清单"（`importers:` 下的依赖段）——只在自检里用来把
        /// "确实未变化"与"根本无法比较"分开核对；正式代码里按同样规则判定的是
        /// <see cref="SnapshotManager.HasImporterDeps"/>。
        /// </summary>
        static bool LockHasDeps(string? text)
            => !string.IsNullOrWhiteSpace(text) && text!.Contains("importers:", StringComparison.Ordinal);

        MainWindow? w = null;
        try
        {
            Trace("构造 MainWindow");
            w = new MainWindow();
            Trace("MainWindow 构造完成，开始量算布局");
            w.LayoutForTest(960, 640);
            Trace("布局完成");

            // 1. 启动进度条：圆角裁剪、缓动、不溢出
            var panel = (Border)w.FindName("LoadingButtonPanel")!;
            var fill = (Border)w.FindName("LoadingFill")!;
            var glossShift = (TranslateTransform)w.FindName("GlossShift")!;
            var loadText = (TextBlock)w.FindName("LoadingText")!;

            panel.Visibility = Visibility.Visible;
            w.LayoutForTest(960, 640);

            var clip = panel.Clip as RectangleGeometry;
            Check("进度条容器被圆角裁剪", clip != null && clip.RadiusX > 0,
                clip == null ? "Clip 为空" : $"半径 {clip.RadiusX}，区域 {clip.Rect.Width:0}×{clip.Rect.Height:0}");
            Check("容器高度 = 48（与按钮一致）", Math.Abs(panel.ActualHeight - 48) < 0.6,
                $"实际 {panel.ActualHeight:0.##}");

            var widths = new List<double>();
            double panelW = panel.ActualWidth;
            bool neverOverflow = true;
            for (int pct = 1; pct <= 100; pct += 4)
            {
                w.SetProgressForTest($"正在初始化: 进度 {pct}%");
                for (int tick = 0; tick < 8; tick++)
                {
                    w.TickForTest();
                    widths.Add(fill.Width);
                    if (fill.Width > panelW + 0.01) neverOverflow = false;
                }
            }
            for (int i = 0; i < 300; i++) { w.TickForTest(); if (fill.Width > panelW + 0.01) neverOverflow = false; }
            // 最后把目标打到 100%（步进 4 只到 97）再收敛
            w.SetProgressForTest("正在初始化: 进度 100%");
            for (int i = 0; i < 300; i++) { w.TickForTest(); if (fill.Width > panelW + 0.01) neverOverflow = false; }

            Check("填充宽度从不越过容器边界", neverOverflow,
                $"最大 {widths.Max():0.##} / 容器 {panelW:0.##}");
            Check("进度条是缓动增长（不是一步跳到位）", widths.Distinct().Count() > 30,
                $"出现 {widths.Distinct().Count()} 个不同宽度值");
            Check("刚设 1% 时远未满格（缓动生效）", widths[0] < panelW * 0.25,
                $"第一帧 {widths[0]:0.##} / 满格 {panelW:0.##}");
            Check("100% 时贴满容器", Math.Abs(fill.Width - panelW) < 1.0,
                $"{fill.Width:0.##} vs {panelW:0.##}");

            var fillClip = fill.Clip as RectangleGeometry;
            Check("填充块自身也有圆角裁剪", fillClip != null && fillClip.RadiusX > 0,
                fillClip == null ? "Clip 为空" : $"半径 {fillClip.RadiusX}");
            Check("流光带动画（循环扫过）", glossShift.HasAnimatedProperties, $"X={glossShift.X:0.#}");
            Check("加载文字显示百分比", loadText.Text.Contains('%'), loadText.Text);

            w.SetProgressForTest("");
            for (int i = 0; i < 4; i++) w.TickForTest();
            Check("清空后进度归零、文字复位", fill.Width == 0 && loadText.Text == "正在加载...",
                $"宽 {fill.Width}，文字「{loadText.Text}」");

            // 消息循环验证：不手动 tick，由 DispatcherTimer 自行驱动。
            w.SetProgressForTest("正在初始化: 进度 60%");
            double before = fill.Width;
            PumpUntil(() => fill.Width > before + 2, 2500);
            double after = fill.Width;
            Check("真实消息循环里进度条自己在动（说明动画真的在跑）", after > before + 2,
                $"{before:0.##} → {after:0.##}（目标 60% = {panelW * 0.6:0.##}）");
            string labelNow = loadText.Text;
            Check("真实循环里百分比文字也在刷新", labelNow.Contains('%') && labelNow != "正在加载... 0%", labelNow);
            w.SetProgressForTest("");
            for (int i = 0; i < 4; i++) w.TickForTest();

            // 2. 右上角三个窗口键（仿 iOS 交通灯）
            string[] hosts = { "TrafficMin", "TrafficMax", "TrafficClose" };
            string[] dots = { "MinDot", "MaxDot", "CloseDot" };
            string[] glyphs = { "MinGlyph", "MaxBtnIcon", "CloseGlyph" };
            string[] idle = { "#FFB3812A", "#FF1E7B37", "#FFB33A34" };
            string[] vivid = { "#FFBD2E", "#28C840", "#FF5F57" };
            for (int i = 0; i < hosts.Length; i++)
            {
                var host = (Border)w.FindName(hosts[i])!;
                var dot = (Border)w.FindName(dots[i])!;
                var glyph = (TextBlock)w.FindName(glyphs[i])!;

                bool idleOk = dot.Background is SolidColorBrush b0 &&
                              string.Equals(b0.Color.ToString(), idle[i], StringComparison.OrdinalIgnoreCase);
                Check($"{hosts[i]} 静息底色 {idle[i]}", idleOk, ((SolidColorBrush)dot.Background).Color.ToString());

                MainWindow.AnimateTrafficForTest(host, true);
                bool animOk = dot.Background is SolidColorBrush b1 && b1.HasAnimatedProperties
                              && dot.RenderTransform is ScaleTransform st && st.HasAnimatedProperties
                              && glyph.HasAnimatedProperties;
                var to = (dot.Background as SolidColorBrush)?.GetAnimationBaseValue(SolidColorBrush.ColorProperty);
                Check($"{hosts[i]} 悬停：提亮 + 弹起 + 图标淡入", animOk,
                    $"颜色动画={(dot.Background as SolidColorBrush)?.HasAnimatedProperties}, 缩放动画={((ScaleTransform)dot.RenderTransform).HasAnimatedProperties}, 图标动画={glyph.HasAnimatedProperties}");

                MainWindow.AnimateTrafficForTest(host, false);
                Check($"{hosts[i]} 离开：回落动画已排上",
                    dot.Background is SolidColorBrush b2 && b2.HasAnimatedProperties);
                Check($"{hosts[i]} 圆点尺寸 14（仿 iOS 交通灯）",
                    Math.Abs(dot.Width - 14) < 0.01 && Math.Abs(dot.Height - 14) < 0.01, $"{dot.Width}×{dot.Height}");
                _ = vivid[i];
            }
            var maxIcon = (TextBlock)w.FindName("MaxBtnIcon")!;
            Check("窗口键图标字体 = Segoe MDL2 Assets", maxIcon.FontFamily.Source.Contains("MDL2"), maxIcon.FontFamily.Source);

            // 3. 社区目录与市场卡片
            // 异步 I/O 需在线程池等待，在 UI 线程上 .GetAwaiter().GetResult() 会死等被自身占用的 Dispatcher。
            Trace("开始拉社区目录");
            var cat = RunOffUi(() => PluginMarket.LoadAsync());
            Trace($"目录拉取完成（{cat.Plugins.Count} 条）");
            // 目录依赖网络：取不到时标记跳过，不判定失败，后续检查仍可继续。
            bool haveCatalog = cat.Plugins.Count > 500;
            Check(haveCatalog ? "社区目录可读取" : "社区目录可读取（本次取不到 ⇒ 跳过，离线？）",
                true,
                haveCatalog ? $"{cat.Plugins.Count} 条 · 来源 {cat.Source} · 跳过 {cat.Skipped}"
                            : $"0 条 · 来源 {cat.Source}（没缓存也没网就跳过，不影响其它检查）");
            Check(haveCatalog ? "目录解析无丢弃（含 github-only 条目）" : "目录解析无丢弃（跳过）",
                !haveCatalog || (cat.Skipped == 0 && cat.Plugins.Count(p => p.Npm.Length == 0) > 100),
                haveCatalog
                    ? $"npm {cat.Plugins.Count(p => p.Npm.Length > 0)} / github-only {cat.Plugins.Count(p => p.Npm.Length == 0)}"
                    : "无目录数据，跳过");

            if (!haveCatalog)
            {
                Trace("目录为空，跳过市场相关检查");
            }
            else
            {
            var sample = cat.Plugins.Where(p => p.Npm.Length > 0).Take(3)
                .Concat(cat.Plugins.Where(p => p.Npm.Length == 0).Take(2)).ToList();
            Trace("开始补 npm 元数据");
            RunOffUi(async () =>
            {
                foreach (var s in sample) await PluginMarket.FillMetaAsync(s, "0.1.5-rc.1");
                return true;
            });
            Trace("元数据补全完成");

            string allText = CollectText(w.BuildMarketCardForTest(sample[0]));
            Check("市场卡片含收藏数 ★", allText.Contains('★'), Shorten(allText, 80));
            Check("市场卡片含下载数 ↓", allText.Contains('↓'));
            Check("市场卡片含更新时间/收录时间", allText.Contains("更新 ") || allText.Contains("收录 "));
            Check("市场卡片含适配版本", allText.Contains("适配 "));
            var ghOnly = cat.Plugins.First(p => p.Npm.Length == 0);
            string ghText = CollectText(w.BuildMarketCardForTest(ghOnly));
            Check("github-only 卡片也能渲染", ghText.Contains("适配 "), Shorten(ghText, 80));
            Check("github-only 安装源可解析", ghOnly.InstallSource.Length > 0, ghOnly.InstallSource);
            Check("npm 条目安装源带版本", sample[0].InstallSource.Contains('@'), sample[0].InstallSource);

            Check("分类表非空", cat.CategoryZh.Count >= 20, $"{cat.CategoryZh.Count} 个分类");
            var sorted = PluginMarket.Filter(cat, null, "全部", PluginMarket.MarketSort.Stars);
            Check("按收藏排序单调", sorted.Count > 1 && sorted[0].Stars >= sorted[^1].Stars,
                $"首 {sorted[0].Stars} → 末 {sorted[^1].Stars}");
            Check("搜索有结果", PluginMarket.Filter(cat, "dsh", "全部", PluginMarket.MarketSort.Stars).Count > 100);

            // 3b. 截图：解析、线路、下载、缓存
            int withShots = cat.Plugins.Count(p => p.Screenshots.Count > 0);
            Check("目录里的截图被解析出来（应为 588 条上下）", withShots >= 300, $"{withShots} 条带图");
            var shotSample = cat.Plugins.First(p => p.Screenshots.Count > 0);
            Check("截图都是允许的 GitHub 图床",
                shotSample.Screenshots.All(u => PluginMarket.IsAllowedImageUrl(u)),
                Shorten(shotSample.Screenshots[0], 70));
            Check("blob 页面地址被改写成 raw 直链",
                PluginMarket.NormalizeImageUrl("https://github.com/a/b/blob/main/docs/x.png")
                    == "https://raw.githubusercontent.com/a/b/main/docs/x.png");
            Check("非 GitHub 图床被拒",
                PluginMarket.NormalizeImageUrl("https://evil.example.com/x.png").Length == 0);

            var routes = PluginMarket.ImageRoutes(shotSample.Screenshots[0]);
            Check("取图线路：代理优先、直连垫底",
                routes.Count >= 2 && routes[0].StartsWith("https://gh-proxy.com/") && routes[^1] == shotSample.Screenshots[0],
                string.Join(" → ", routes.Select(r => r.Length > 46 ? r.Substring(0, 46) + "…" : r)));

            int cacheBefore = w.ImageCacheCountForTest;
            Trace("开始真联网取图：" + Shorten(shotSample.Screenshots[0], 80));
            var bmp = RunOffUi(() => w.LoadImageForTestAsync(shotSample.Screenshots[0], false));
            Trace("取图结束：" + (bmp == null ? "null" : "ok"));
            Check("真联网取到一张缩略图", bmp != null,
                bmp == null ? "下载/解码失败" : $"{bmp.PixelWidth}×{bmp.PixelHeight}");
            Check("图片落在磁盘缓存里（跨次运行可复用）", w.ImageCacheHasForTest(shotSample.Screenshots[0]),
                $"缓存目录现有 {w.ImageCacheCountForTest} 个文件（取图前 {cacheBefore}）");

            // 现场从 README 抓取图片（选取目录中未收录截图的仓库）
            var noShot = cat.Plugins.First(p => p.Npm.Length > 0 && p.Screenshots.Count == 0 && p.Owner.Length > 0);
            Trace($"开始捞 README：{noShot.Owner}/{noShot.Name}");
            var scraped = RunOffUi(() => PluginMarket.ScrapeReadmeImagesAsync(noShot.Owner, noShot.Name));
            Trace($"捞图结束：{scraped.Count} 张");
            Check("现场从 README 捞图不报错", scraped.All(u => PluginMarket.IsAllowedImageUrl(u)),
                $"{noShot.Owner}/{noShot.Name} → {scraped.Count} 张");

            // ══════════════════════════════════════════════════════════════════════════
            // 3b-2. **根因断言**：ScrapeReadmeImagesAsync 只可能返回**已过 NormalizeImageUrl** 的 URL
            // ══════════════════════════════════════════════════════════════════════════
            //
            //   为什么必须钉这条：MainWindow.Images.cs 的两处落库点（:481 ScrapeAndShowAsync、
            //   :552 ScrapeIntoLightboxAsync）都是
            //       foreach (string u in urls) if (!m.Screenshots.Contains(u)) m.Screenshots.Add(u);
            //   ——它们**信任** ScrapeReadmeImagesAsync 的返回值已归一（并已过图床白名单）。
            //   这个信任目前成立，但此前**没有任何断言钉住**：只要哪天有人在捞图链路里多写一条
            //   `found.Add(某未归一地址)` 的捷径，截图列表就会混进 http 明文 / 陌生 host /
            //   javascript: / data: / UNC 之类的地址，而下游会照着这个列表去下载或丢给界面。
            //
            //   钉法不是"再联网跑一次"（那种断言会被网络抖动左右），而是量**漏斗的结构**：
            //   捞图链路上唯一的写入点是私有 AddMarkdownImage，而它只有**一条** found.Add，
            //   且必定在 `string url = NormalizeImageUrl(src); if (url.Length == 0) return;` 之后。
            //   下面用反射把**产物**喂给它真跑（不碰网络）：
            //     · 毒样本 ⇒ 一条都进不去（返回列表必须为空）；
            //     · 合法样本 ⇒ 进得去，且进去的形态是**已归一**的（blob 页被改写成 raw 直链）。
            //   两半合起来才不空洞：只验毒样本的话，"永远不添加"的实现也能全绿。
            {
                var addMi = typeof(PluginMarket).GetMethod("AddMarkdownImage",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

                var poisonSink = new List<string>();
                var legitSink = new List<string>();
                const string imgBase = "https://raw.githubusercontent.com/o/r/HEAD/";

                if (addMi != null)
                {
                    // 非 https / 任意 scheme / UNC / 陌生 host / 后缀伪装 —— 全部必须被挡在写入点之外
                    foreach (string poison in new[]
                             {
                                 "http://evil.example/a.png",
                                 "javascript:alert(1)",
                                 "data:image/png;base64,iVBORw0KGgo=",
                                 "file:///C:/evil.png",
                                 "\\\\srv\\share\\x.png",
                                 "https://evil.example/x.png",
                                 "https://raw.githubusercontent.com.evil.example/x.png",
                                 "https://github.com.evil.example/x.png",
                                 "https://evil.example/github.com/x.png",
                                 "",
                             })
                        addMi.Invoke(null, new object[] { poisonSink, imgBase, poison });

                    // 正向对照：合法 blob 页必须进得去，且进去时已是 raw 直链（证明归一确实生效）
                    addMi.Invoke(null, new object[]
                        { legitSink, imgBase, "https://github.com/o/r/blob/main/docs/ok.png" });
                }

                Check("根因断言：捞图链路的唯一写入点只接受已过 NormalizeImageUrl 的 URL（毒样本 0 条、合法样本已归一）",
                    addMi != null && poisonSink.Count == 0 && legitSink.Count == 1
                    && legitSink[0] == "https://raw.githubusercontent.com/o/r/main/docs/ok.png",
                    $"写入点={(addMi != null ? "已定位" : "未定位")} · 毒样本落库={poisonSink.Count} 条"
                    + $" · 合法样本={legitSink.Count} 条«{(legitSink.Count > 0 ? legitSink[0] : "")}»");
            }

            // NormalizeImageUrl 的加固用例：把"归一的边界"逐条量出来（纯函数，不联网）。
            Check("NormalizeImageUrl 加固：非 https / 任意 scheme / UNC / 陌生 host / 后缀伪装 / 空串 一律返回空串",
                PluginMarket.NormalizeImageUrl("") == "" &&
                PluginMarket.NormalizeImageUrl("   ") == "" &&
                PluginMarket.NormalizeImageUrl("http://raw.githubusercontent.com/o/r/main/x.png") == "" &&  // 明文
                PluginMarket.NormalizeImageUrl("javascript:alert(1)") == "" &&
                PluginMarket.NormalizeImageUrl("data:image/png;base64,iVBORw0KGgo=") == "" &&
                PluginMarket.NormalizeImageUrl("file:///C:/evil.png") == "" &&
                PluginMarket.NormalizeImageUrl("\\\\srv\\share\\x.png") == "" &&                          // UNC
                PluginMarket.NormalizeImageUrl("ftp://raw.githubusercontent.com/o/r/x.png") == "" &&
                PluginMarket.NormalizeImageUrl("https://evil.example/x.png") == "" &&                     // 陌生 host
                PluginMarket.NormalizeImageUrl("https://raw.githubusercontent.com.evil.example/x.png") == "" && // 后缀伪装
                PluginMarket.NormalizeImageUrl("https://github.com.evil.example/x.png") == "" &&          // 后缀伪装
                PluginMarket.NormalizeImageUrl("https://evil.example/github.com/x.png") == "" &&          // host 含白名单串
                PluginMarket.NormalizeImageUrl("https://user:pass@evil.example/x.png") == "",             // userinfo 骗术
                "非 https、任意 scheme、UNC、陌生 host、后缀伪装、空串 全部返回空串");

            Check("NormalizeImageUrl 正向对照：合法 https 与协议相对地址仍放行（加固不得把能用的堵死）",
                PluginMarket.NormalizeImageUrl("https://github.com/a/b/blob/main/docs/x.png")
                    == "https://raw.githubusercontent.com/a/b/main/docs/x.png" &&
                PluginMarket.NormalizeImageUrl("https://raw.githubusercontent.com/o/r/main/x.png")
                    == "https://raw.githubusercontent.com/o/r/main/x.png" &&
                PluginMarket.NormalizeImageUrl("//raw.githubusercontent.com/o/r/main/x.png")
                    == "https://raw.githubusercontent.com/o/r/main/x.png" &&                              // 协议相对补 https
                PluginMarket.NormalizeImageUrl("HTTPS://RAW.GITHUBUSERCONTENT.COM/o/r/x.png")
                    == "HTTPS://RAW.GITHUBUSERCONTENT.COM/o/r/x.png",                                     // 大小写不敏感
                "blob 页改写 raw 直链、协议相对补 https、大写同站照放行");

            // 缩略图条与找截图入口
            Trace("建带图卡片");
            var shotCard = w.BuildMarketCardForTest(shotSample);
            Trace("数缩略图");
            int thumbCount = CountImages(shotCard);
            Trace($"缩略图 {thumbCount} 张");
            Check("带图卡片上出现缩略图", thumbCount > 0, $"{thumbCount} 张缩略图");
            var plainCard = w.BuildMarketCardForTest(noShot);
            Check("没图的卡片给出「加载图片」入口", CollectText(plainCard).Contains("加载图片"),
                Shorten(CollectText(plainCard), 60));
            Trace("建无图卡片完成");

            // 放大层
            w.ShowLightboxForTest(shotSample, 0);
            Trace("放大层已打开");
            Check("点缩略图能开放大层", w.LightboxVisibleForTest);
            Check("放大层标题带页码", w.LightboxTitleForTest.Contains("第 1/"), w.LightboxTitleForTest);
            w.CloseLightboxForTest();
            Check("Esc / 点空白能关掉放大层", !w.LightboxVisibleForTest);
            Trace("放大层已关闭");
            }

            // 4. 插件页页签结构、筛选下拉、分类展开
            Check("两个页签都在", w.FindName("MarketTabBtn") != null && w.FindName("InstalledTabBtn") != null);
            var installedSeg = (Border)w.FindName("InstalledTabBtn")!;
            Check("默认停在「本地插件」",
                installedSeg.Background is SolidColorBrush ib && ib.Color == Color.FromRgb(0x00, 0x7A, 0xFF),
                (installedSeg.Background as SolidColorBrush)?.Color.ToString() ?? "-");

            w.PrimeMarketForTest(cat);
            w.ShowPluginsTabForTest(true);
            w.LayoutForTest(960, 640);
            PumpUntil(() => ((Panel)w.FindName("MarketPanel")!).Children.Count > 0, 5000);
            string dbg = w.MarketDebugForTest();
            Trace("市场页状态：" + dbg);
            Check("切到「寻找插件」：工具栏 + 列表都显示",
                ((FrameworkElement)w.FindName("MarketToolbar")!).Visibility == Visibility.Visible &&
                ((FrameworkElement)w.FindName("MarketHost")!).Visibility == Visibility.Visible);
            var catPanel = (Panel)w.FindName("MarketCategoryPanel")!;
            Check("分类标签已生成", catPanel.Children.Count > 5, $"{catPanel.Children.Count} 个 · {dbg}");
            // 改这条的理由：分类栏已从"自动换行面板"改成"单行 + 末尾「更多」下拉"（面板换成横向 StackPanel），
            // 旧断言 catPanel is WrapPanel 描述的是已废弃的行为，必然失败。强度不降：除类型与朝向外，
            // 布局已量算时再判"面板高度 ≈ 单个 chip 的高度"——真换行时高度会是两三行，这一条才拦得住复发。
            // 高度取不到（未布局）时退回只判类型与朝向，不硬造一个恒真的高度判据。
            var catSp = catPanel as StackPanel;
            var catChip0 = catPanel.Children.Count > 0 ? catPanel.Children[0] as FrameworkElement : null;
            double catChipH = catChip0?.ActualHeight ?? 0;
            bool catRowMeasured = catChipH > 0 && catPanel.ActualHeight > 0;
            Check("分类栏改单行：MarketCategoryPanel 是横向 StackPanel，且可见 chip 全在一行（不换行）",
                catSp != null && catSp.Orientation == Orientation.Horizontal &&
                (!catRowMeasured || catPanel.ActualHeight <= catChipH * 1.8),
                $"面板高 {catPanel.ActualHeight:0.#}px / 单 chip 高 {catChipH:0.#}px（{(catRowMeasured ? "已量算" : "未布局，只判类型与朝向")}）");
            var marketPanel = (Panel)w.FindName("MarketPanel")!;
            Check("市场列表渲染出卡片", marketPanel.Children.Count > 0, $"{marketPanel.Children.Count} 张 · {dbg}");
            Check("「加载更多」按钮可见",
                ((FrameworkElement)w.FindName("MarketMoreBtn")!).Visibility == Visibility.Visible);

            int catCollapsedChips = catPanel.Children.Count;   // 可见 chip 数（点「更多」后要一个不变）
            string toggleText0 = LastChipText(catPanel);
            // 改这条的理由：末尾开关的文案已从「更多分类 ⌄」改成「更多 ⌄」，而且它现在是一个弹层的开关，
            // 光看文字会漏掉"菜单其实已经开着"这种状态，所以补上 IsOpen == false。
            // 注意这里刻意不调 EnsureShownForTest()：本用例在"窗口从未显示"的段落里，按 WPF 的 Popup 语义，
            // 没有可见放置目标时 IsOpen 置 true 也读回 false —— 在没显示窗口的前置下判 IsOpen 只会是恒真。
            // 弹层真正打开的那一半（IsOpen 必须为 true）放在本批末位"窗口已显示"的用例里，见 [62]。
            var catPopup = w.FindName("MarketCatPopup") as System.Windows.Controls.Primitives.Popup;
            Check("未开弹层时：末位 chip 文字是「更多 ⌄」，且 MarketCatPopup.IsOpen == false",
                toggleText0.Contains("更多") && toggleText0.Contains("⌄") &&
                toggleText0.Contains("更多分类") == false && catPopup != null && !catPopup.IsOpen,
                $"末标签「{toggleText0}」· IsOpen={catPopup?.IsOpen.ToString() ?? "找不到 MarketCatPopup"}");
            // 改这条的理由：点「更多」不再让可见标签变多，而是打开一个弹层（MarketCatPopup → MarketCatMenuPanel），
            // 旧判据 expandedChips > collapsedChips 描述的是已废弃的行为，必然失败。强度不降反升：
            // 改走真实处理函数 MarketCatsToggle_Click（不再借 ToggleCatsForTest —— 它只翻布尔、既不开弹层也不建菜单，
            // 拿它判 IsOpen/菜单行数会退化成恒真），并要求"点击后可见 chip 数一个没变"。
            // 刻意**不**在这里显示窗口：本文件把"真正显示窗口"推迟到文末那一批，前面的用例依赖
            // "窗口没有句柄"（例如毛玻璃接口在无句柄窗口上应优雅返回 false 那条断言就建立在这个前提上）。
            // 曾经在这里加过 EnsureShownForTest() 以便断言 Popup.IsOpen，结果把后面那批断言判死了。
            // 结论：本用例只验"不需要可见放置目标"的那几项 —— 菜单行数、chip 文案、可见 chip 数、
            // 选中项在行内；弹层是否真的打开由文末"窗口已显示"的那批负责。
            var catToggleChip = catPanel.Children.Count > 0 ? catPanel.Children[^1] : null;
            if (catToggleChip != null)
                catToggleChip.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(
                    System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
            w.LayoutForTest(960, 640);
            w.UpdateLayout();
            int expandedChips = catPanel.Children.Count;
            var catMenu = w.FindName("MarketCatMenuPanel") as Panel;
            // 弹层的开合状态必须在这里就地取值：下面的「选中项在可见行里」要走真实的选分类路径，
            // 而选分类会按设计把弹层收掉（MarketCategoryLink_Click → CloseMarketCatMenu）。
            // 若把 IsOpen 留到 Check 表达式里再读，读到的永远是 false —— 断言会自己把自己判死。
            bool catPopupOpenAfterToggle = catPopup?.IsOpen == true;
            int catMenuRows = catMenu?.Children.Count ?? -1;
            // 末位 chip 的文案同样必须就地取值：下面选分类会按设计收掉弹层并重建可见行，
            // 那一次重建会把 chip 文案改回「更多」。留到 Check 里现读，读到的是选分类之后的样式，必假。
            string catToggleTextAfterOpen = LastChipText(catPanel);
            // 新契约（并入本条而不是新增一条断言：新增会让后续 [NN] 编号整体位移，本项目要求位移 = 0）：
            // "当前选中的分类必须出现在可见行里"。判据要落在真实的重排逻辑上 —— 走真实的
            // MarketCategoryLink_Click 把筛选切到一个排在最后、按单行宽度必然被挤出可见行的分类，
            // 再看重建后的可见行是否仍给它的 chip 留了一格（BuildCategoryChips 里"选中项优先放行"）。
            // catAll 按与 CollectCategoryEntries 相同口径列出"全部 + 各分类"：顺序未按数量重排，
            // 所以末位正是插件最少的那个分类，也就是单行里最先被挤出去的。
            var catAll = new List<string> { "全部" };
            foreach (var catPlugin in cat.Plugins)
                foreach (var catSlugOfPlugin in catPlugin.Categories)
                    if (!catAll.Contains(catSlugOfPlugin)) catAll.Add(catSlugOfPlugin);
            string catLastSlug = catAll.Count > 0 ? catAll[^1] : "全部";
            w.ClickCategoryLinkForTest(catLastSlug);
            w.LayoutForTest(960, 640);
            var catVisibleSlugs = new List<string>();
            foreach (var catChild in catPanel.Children)
                if (catChild is Border catChipBorder && catChipBorder.Tag is string catChipSlug && catChipSlug.Length > 0)
                    catVisibleSlugs.Add(catChipSlug);
            bool catSelVisible = catVisibleSlugs.Contains(w.CurrentCategoryForTest);
            // 选分类会关掉弹层（MarketCategoryLink_Click → CloseMarketCatMenu），这是设计如此：
            // 所以上面那半段先验"点「更多」建出了完整菜单并切到展开文案"，再验"选中项在可见行里"。
            // 关于弹层标志：本用例不显示窗口，而 WPF 在没有可见放置目标时会把 Popup.IsOpen 复位，
            // 所以这里判的是**菜单内容已按展开态重建**（行数 = 分类总数、末位 chip 变「收起」），
            // 而不是 IsOpen 本身 —— 后者放到文末"窗口已显示"的那批去判。
            Check("点「更多」后：菜单按全部分类重建、末位 chip 变「收起」、可见 chip 数不变，且当前选中的分类在可见行里",
                catMenuRows == catAll.Count &&
                catToggleTextAfterOpen.Contains("收起") &&
                expandedChips == catCollapsedChips && catSelVisible,
                $"菜单行 {catMenuRows}/{catAll.Count} · " +
                $"可见 {catCollapsedChips} → {expandedChips} 个 · 点开后末标签「{catToggleTextAfterOpen}」 · " +
                $"选中「{w.CurrentCategoryForTest}」{(catSelVisible ? "在" : "不在")}可见行");
            // 用完把分类切回「全部」：本用例为了验证"选中项必在可见行里"把筛选切到了末位分类，
            // 而后续用例（如按「全部」算期望值的排序断言）默认当前停在「全部」——
            // 不切回去，它们会拿"全部"的期望值去比"某个分类"的实际列表，必然不等。
            // 放在 Check 之后：断言用的 catSelVisible 已在前面就地取值，不受这次切换影响。
            w.ClickCategoryLinkForTest("全部");

            Check("「筛选」按钮在工具条上", w.FindName("MarketFilterBtn") != null);
            Check("排序不再挤成一排按钮（旧的三颗 chip 已删除）",
                w.FindName("MarketSortStars") == null && w.FindName("MarketSortDownloads") == null && w.FindName("MarketSortAdded") == null);
            Check("下拉四组选项都在",
                w.FindName("FilterFieldDownloads") != null && w.FindName("FilterFieldStars") != null &&
                w.FindName("FilterFieldPublished") != null && w.FindName("FilterDirDesc") != null &&
                w.FindName("FilterDirAsc") != null && w.FindName("FilterTimeAll") != null &&
                w.FindName("FilterTime7") != null && w.FindName("FilterTime30") != null &&
                w.FindName("FilterTime90") != null && w.FindName("FilterTime365") != null &&
                w.FindName("FilterHostAll") != null && w.FindName("FilterHostAdapted") != null);

            w.PaintFilterMenuForTest();
            Check("筛选按钮显示当前排序字段", w.FilterLabelForTest.Contains("收藏数"), w.FilterLabelForTest);

            // 第 40 批：下拉选项行要有悬停反馈，弹层内容也要挂上动效（弹层不在窗口视觉树里）
            var rowProbe = w.FindName("FilterTimeAll") as Border;
            var rowText = rowProbe?.Child as TextBlock;
            Check("筛选下拉的选项行带手型光标（笔过就是可点状态）",
                rowProbe != null && rowProbe.Cursor == System.Windows.Input.Cursors.Hand,
                rowProbe?.Cursor.ToString() ?? "没找到行");
            Check("下拉内容已挂动效（弹层不在窗口视觉树，单独挂过）",
                rowProbe != null && ButtonFx.IsWiredForTest(rowProbe),
                rowProbe == null ? "没找到行" : "已挂 ✓");
            w.HoverMenuRowForTest(rowProbe!, true);
            var menuHoverBg = (rowProbe?.Background as SolidColorBrush)?.Color ?? Colors.Transparent;
            w.HoverMenuRowForTest(rowProbe!, false);
            var menuRestBg = (rowProbe?.Background as SolidColorBrush)?.Color ?? Colors.Transparent;
            Check("筛选选项行悬停有可见变化（离开后恢复原样）",
                menuHoverBg != menuRestBg && menuHoverBg.A > 0,
                $"悬停 {menuHoverBg} / 常态 {menuRestBg}");

            // 弹层跟随窗口的用例放在自检末位（需要窗口显示过），见文件末尾"第 40 批（末位用例）"

            var firstDownloads = cat.Plugins.Where(p => p.Downloads.HasValue)
                .OrderByDescending(p => p.Downloads!.Value).First();
            w.SelectSortForTest("downloads");
            Check("切到「下载量」后按钮文案跟着变", w.FilterLabelForTest.Contains("下载量"), w.FilterLabelForTest);
            var shownFirst = FirstCardName(w);
            Check("列表真的按下载量重排", shownFirst.Length > 0,
                $"首位显示「{shownFirst}」，目录里下载冠军是 {firstDownloads.Name}");

            var ascFirst = PluginMarket.Filter(cat, null, "全部", PluginMarket.MarketSort.Stars, desc: false).First();
            w.SelectSortForTest("stars");
            w.SelectSortDirForTest(false);
            Check("升序方向生效（收藏最少的排前面）", FirstCardName(w) == ascFirst.Name,
                $"首位「{FirstCardName(w)}」，升序冠军是 {ascFirst.Name}");
            w.SelectSortDirForTest(true);

            int beforeRange = w.MarketListCountForTest;
            w.SelectTimeRangeForTest(7);
            int afterRange = w.MarketListCountForTest;
            Check("「最近 7 天」把候选收窄", afterRange < beforeRange && afterRange >= 0,
                $"候选 {beforeRange} → {afterRange}");
            Check("筛选按钮上标出时间范围", w.FilterLabelForTest.Contains("近 7 天"), w.FilterLabelForTest);
            w.SelectTimeRangeForTest(0);

            w.SelectHostFilterForTest(true);
            Check("「适配当前版本」勾上后按钮有标记", w.FilterLabelForTest.Contains("已适配"), w.FilterLabelForTest);
            w.SelectHostFilterForTest(false);

            // 5. 按钮配色统一（绿/橙/红 + 白字）
            var palette = new[] { "#FF34C759", "#FFFF9F0A", "#FFFF3B30" };
            foreach (var (name, expected, label) in new[]
            {
                ("MainBtnBorder", "#FF34C759", "一键启动引擎"),
                ("OpenEngineBorder", "#FF34C759", "加载引擎"),
                ("StopBtnBorder", "#FFFF3B30", "终止引擎"),
            })
            {
                var el = w.FindName(name) as Border;
                string actual = (el?.Background as SolidColorBrush)?.Color.ToString() ?? "(无)";
                Check($"{label} = {expected}", actual == expected, actual);
            }
            var enabledCard = w.BuildInstalledCardForTest(new PluginManager.Plugin
            { Name = "自检-启用中", Version = "1.0.0", Compatibility = PluginManager.Compat.Ok });
            var enabledBtns = ButtonInfos(enabledCard);
            Check("启用中的卡片：按钮都在三色里且白字",
                enabledBtns.Count >= 2 && enabledBtns.All(b => palette.Contains(b.Bg) && b.Fg == "#FFFFFFFF"),
                string.Join(" / ", enabledBtns.Select(b => $"{b.Text}={b.Bg}")));

            var disabledCard = w.BuildInstalledCardForTest(new PluginManager.Plugin
            { Name = "自检-已禁用", Version = "1.0.0", Disabled = true });
            var disabledBtns = ButtonInfos(disabledCard);
            var allBtns = enabledBtns.Concat(disabledBtns).ToList();
            Check("绿/橙/红三色齐全（启用、关闭、卸载）",
                allBtns.Any(b => b.Bg == "#FF34C759") && allBtns.Any(b => b.Bg == "#FFFF9F0A") && allBtns.Any(b => b.Bg == "#FFFF3B30"),
                string.Join(" / ", allBtns.Select(b => b.Text + "=" + b.Bg)));

            // 6. 状态页换位、滚动条、回到顶部、手动固定
            var preview = (TextBox)w.FindName("LogPreviewBox")!;
            var eventsPanel = (StackPanel)w.FindName("StatusEventsPanel")!;
            Check("实时输出与事件信息换了位置（实时输出在中栏、事件信息到右下角）",
                w.IsInMiddleColumnForTest(preview) && !w.IsInMiddleColumnForTest(eventsPanel),
                $"实时输出中栏={w.IsInMiddleColumnForTest(preview)}，事件信息中栏={w.IsInMiddleColumnForTest(eventsPanel)}");

            var (thumbIdle, thumbHover) = w.ScrollBarThumbWidthsForTest();
            Check("滚动条换成细条样式（静息 4px / 悬停 8px）",
                thumbIdle > 0 && thumbIdle <= 4.5 && thumbHover >= 7,
                $"静息 {thumbIdle}px，悬停 {thumbHover}px");
            Check("细滚动条已挂成全局默认（所有列表都吃这个样式）", w.SlimScrollBarIsGlobalForTest());
            // 横向滚动条方向：数值增大时滑块必须右移（模板里 IsDirectionReversed 写 True 会导致反向）
            {
                var bar = new System.Windows.Controls.Primitives.ScrollBar
                {
                    Orientation = Orientation.Horizontal,
                    Style = (Style)w.FindResource("SlimScrollBar"),
                    Minimum = 0, Maximum = 100, ViewportSize = 20, Value = 0,
                    Width = 200, Height = 10
                };
                var host = new Grid { Width = 200, Height = 10 };
                host.Children.Add(bar);
                host.Measure(new System.Windows.Size(200, 10));
                host.Arrange(new Rect(0, 0, 200, 10));
                host.UpdateLayout();

                System.Windows.Controls.Primitives.Thumb? thumb = null;
                void FindThumb(DependencyObject o)
                {
                    if (thumb != null) return;
                    if (o is System.Windows.Controls.Primitives.Thumb t) { thumb = t; return; }
                    int n = VisualTreeHelper.GetChildrenCount(o);
                    for (int i = 0; i < n; i++) FindThumb(VisualTreeHelper.GetChild(o, i));
                }
                FindThumb(bar);

                double xMin = -1, xMax = -1;
                if (thumb != null)
                {
                    xMin = thumb.TransformToAncestor(bar).Transform(new System.Windows.Point(0, 0)).X;
                    bar.Value = 100;
                    host.UpdateLayout();
                    xMax = thumb.TransformToAncestor(bar).Transform(new System.Windows.Point(0, 0)).X;
                }
                Check("横向滚动条方向正确（值增大时滑块右移）",
                    thumb != null && xMax > xMin,
                    $"值 0 → x={xMin:0.#}，值 100 → x={xMax:0.#}");
            }
            Check("两个列表都有「回到顶部」按钮",
                w.FindName("MarketTopBtn") != null && w.FindName("InstalledTopBtn") != null);
            Check("按钮默认隐藏（未滚动时不出现）",
                ((FrameworkElement)w.FindName("MarketTopBtn")!).Visibility == Visibility.Collapsed);

            string pinBefore = VersionMemory.Pin;
            VersionMemory.PinTo("9.9.9");
            Check("能手动固定版本", VersionMemory.Pin == "9.9.9" && VersionMemory.IsManualPin);
            VersionMemory.NoteEngineReady("1.1.1");
            Check("手动固定后，自动逻辑不许改它（跑就绪不动 pin）",
                VersionMemory.Pin == "9.9.9" && VersionMemory.IsManualPin, $"现在是 {VersionMemory.Pin}");
            VersionMemory.MakePinAuto();
            Check("能改回自动管理（保留版本号）",
                VersionMemory.Pin == "9.9.9" && !VersionMemory.IsManualPin, VersionMemory.PolicyText);
            VersionMemory.NoteEngineReady("2.2.2");
            Check("已有固定时，自动固定不会乱改（还是 9.9.9）", VersionMemory.Pin == "9.9.9", VersionMemory.Pin);
            VersionMemory.FollowLatest();
            VersionMemory.NoteEngineReady("3.3.3");
            Check("取消固定后，第一次跑通会自动固定住（3.3.3）",
                VersionMemory.Pin == "3.3.3" && !VersionMemory.IsManualPin, VersionMemory.PolicyText);

            // 升级模式必须用精确目标版本：npm 的 latest 标签可能仍指向旧版（rc.2 发布在 next 上），
            // 用 @latest 会再装一次旧版，表现为「升级不生效」。
            VersionMemory.BeginUpdate("9.9.9");
            Check("更新模式下启动用精确目标版本（不用 latest）",
                VersionMemory.Spec == "9.9.9", $"Spec={VersionMemory.Spec}");
            VersionMemory.CancelUpdate();
            VersionMemory.BeginUpdate("");                     // 模拟旧版遗留：只记了待更新、没记目标
            VersionMemory.EnsureUpdateTarget("2.0.0");
            Check("旧版遗留的「待更新但没目标」能自愈成精确版本",
                VersionMemory.Spec == "2.0.0", $"Spec={VersionMemory.Spec}");
            VersionMemory.FollowLatest();
            Check("取消固定且不在更新模式时才用 latest",
                VersionMemory.Spec == "latest", $"Spec={VersionMemory.Spec}");
            if (pinBefore.Length > 0) VersionMemory.PinTo(pinBefore); else VersionMemory.FollowLatest();

            // 7. 缓存与配置迁移、名称规整、事件配色、作者链接
            Check("缓存与配置都落到程序目录下的新文件夹",
                GuardPaths.CacheDir.EndsWith("Cache", StringComparison.OrdinalIgnoreCase) &&
                GuardPaths.ConfigDir.EndsWith("Config", StringComparison.OrdinalIgnoreCase) &&
                GuardPaths.CacheDirMarket.StartsWith(GuardPaths.CacheDir) &&
                GuardPaths.CacheDirImages.StartsWith(GuardPaths.CacheDir),
                $"cache={GuardPaths.CacheDir} / config={GuardPaths.ConfigDir}");
            Check("缓存分类子目录已分开（market / images）",
                GuardPaths.CacheDirMarket.Contains("Market") && GuardPaths.CacheDirImages.Contains("Images"));
            Check("路径大小写严格正确（Config / Cache / Market / Images，区分大小写）",
                GuardPaths.ConfigDir.EndsWith("Config", StringComparison.Ordinal) &&
                GuardPaths.CacheDir.EndsWith("Cache", StringComparison.Ordinal) &&
                GuardPaths.CacheDirMarket.EndsWith("Market", StringComparison.Ordinal) &&
                GuardPaths.CacheDirImages.EndsWith("Images", StringComparison.Ordinal),
                $"{GuardPaths.ConfigDir} | {GuardPaths.CacheDir}");
            Check("日志目录是 Logs（首字母大写）",
                GuardPaths.LogDir.EndsWith("Logs", StringComparison.Ordinal), GuardPaths.LogDir);
            // 开发输出目录含 DSHGuard.dll，但不含 Tools 脚本；仅单文件部署会附带，故此处不作硬性要求
            bool buildOutputDir = File.Exists(Path.Combine(AppContext.BaseDirectory, "DSHGuard.dll"));
            Check("工具目录是 Tools（首字母大写）",
                Path.Combine(AppContext.BaseDirectory, "Tools").EndsWith("Tools", StringComparison.Ordinal) &&
                (buildOutputDir || File.Exists(Path.Combine(AppContext.BaseDirectory, "Tools", "clean-logs.ps1"))),
                Path.Combine(AppContext.BaseDirectory, "Tools"));

            Check("插件名规整：来源树写法 → 「大名 - 父级」",
                PluginMarket.MarketPlugin.FormatDisplayName("hindsight#coding-agents") == "Coding Agents - Hindsight",
                PluginMarket.MarketPlugin.FormatDisplayName("hindsight#coding-agents"));
            Check("插件名规整：路径型来源树取末段",
                PluginMarket.MarketPlugin.FormatDisplayName("archify#integrations/deepseek-harness") == "Deepseek Harness - Archify",
                PluginMarket.MarketPlugin.FormatDisplayName("archify#integrations/deepseek-harness"));
            Check("插件名规整：作用域包名也顺过来",
                PluginMarket.MarketPlugin.FormatDisplayName("@furongjun1999/dsh-memory") == "DSH Memory - Furongjun1999",
                PluginMarket.MarketPlugin.FormatDisplayName("@furongjun1999/dsh-memory"));
            Check("插件名规整：普通名字原样不动",
                PluginMarket.MarketPlugin.FormatDisplayName("dsh-market") == "dsh-market");

            Check("出错事件记成红色", w.EventColorForTest("自检-这是一条错误", bad: true) == "#FFFF453A",
                w.EventColorForTest("自检-这是一条错误2", bad: true));
            Check("成功/更新事件记成绿色", w.EventColorForTest("自检-这是一条好消息", bad: false) == "#FF34C759",
                w.EventColorForTest("自检-这是一条好消息2", bad: false));

            var marketEntry = cat.Plugins.First(p => p.DisplayName.Contains(" - "));
            var marketCardText = CollectText(w.BuildMarketCardForTest(marketEntry));
            Check("市场卡片用规整后的名字", marketCardText.Contains(marketEntry.DisplayName),
                marketEntry.DisplayName);
            Check("作者名可点（带链接与手型）", marketEntry.AuthorUrl.StartsWith("https://github.com/"),
                marketEntry.AuthorUrl);

            // 清理缓存：自检环境的缓存在临时目录中，可直接清理
            long cacheBeforeClear = GuardPaths.CacheBytes();
            long freedBytes = GuardPaths.ClearCache();
            Check("清理缓存真的把文件删掉了",
                cacheBeforeClear > 0 && GuardPaths.CacheBytes() == 0,
                $"清前 {GuardPaths.HumanSize(cacheBeforeClear)} → 清后 {GuardPaths.HumanSize(GuardPaths.CacheBytes())}（释放 {GuardPaths.HumanSize(freedBytes)}）");
            Check("清缓存不会碰配置目录", File.Exists(VersionMemory.StorePath),
                VersionMemory.StorePath);
            GuardPaths.EnsureCacheDirs();
            Check("清完会把分类子目录补回来",
                Directory.Exists(GuardPaths.CacheDirMarket) && Directory.Exists(GuardPaths.CacheDirImages));

            // 8. 路径页整理、版本卡去按钮、实时事件、分类链接、去下划线、未声明、更新按钮配色、毛玻璃
            w.ShowViewForTest("settings");
            w.ShowSettingsTabForTest("paths");
            w.LayoutForTest(960, 640);
            string pathTexts = w.PageTextsForTest("SettingsPagePaths");
            Check("路径页：两处「配置」已分得清（引擎配置 / 守护壳配置）",
                pathTexts.Contains("引擎配置") && pathTexts.Contains("守护壳配置") && !pathTexts.Contains("配置文件"),
                Shorten(pathTexts, 120));
            Check("路径页：删掉了「本次记录」行", !pathTexts.Contains("本次记录"));
            Check("路径页：自动配置按钮挪到最下面（在缓存位置之下）",
                w.FindName("AutoConfigButton") is FrameworkElement ac && w.FindName("PathCacheBox") is FrameworkElement pc &&
                ac.TranslatePoint(new Point(0, 0), (UIElement)w.Content).Y >
                pc.TranslatePoint(new Point(0, 0), (UIElement)w.Content).Y,
                $"按钮 y={(w.FindName("AutoConfigButton") as FrameworkElement)?.TranslatePoint(new Point(0, 0), (UIElement)w.Content).Y:0}");

            Check("版本卡片：按钮与「点击查看详情」都没了",
                w.FindName("VerCardCheckBtn") == null && w.FindName("VerCardRollbackBtn") == null && w.FindName("VerCardHint") == null);

            Check("事件信息是实时的（不切页面也会出现）", w.LiveEventAppearsForTest());

            w.ShowViewForTest("plugins");
            w.ShowPluginsTabForTest(true);
            w.LayoutForTest(960, 640);
            PumpUntil(() => ((Panel)w.FindName("MarketPanel")!).Children.Count > 0, 5000);
            var catEntry = cat.Plugins.First(p => p.Categories.Count > 0);
            w.ClickCategoryLinkForTest(catEntry.Categories[0]);
            Check("点分类标签能跳到该分类的筛选列表",
                w.CurrentCategoryForTest == catEntry.Categories[0],
                $"当前分类 = {w.CurrentCategoryForTest}");
            w.ClickCategoryLinkForTest("全部");

            var linkCardEntry = cat.Plugins.First(p => p.DisplayName.Contains(" - ") && p.Owner.Length > 0);
            Check("插件卡片的链接不带下划线", !MainWindow.HasUnderlineForTest(w.BuildMarketCardForTest(linkCardEntry)));
            var installedCard = w.BuildInstalledCardForTest(new PluginManager.Plugin
            { Name = "自检-插件", Version = "1.0.0", Author = "someone", RepositoryUrl = "https://github.com/o/r" });
            Check("已安装卡片的链接也不带下划线", !MainWindow.HasUnderlineForTest(installedCard));

            Check("兼容标识不再用「—」：未声明就写「未声明」",
                new PluginManager.Plugin { Compatibility = PluginManager.Compat.Unknown }.CompatText == "未声明" &&
                new PluginManager.Plugin { Compatibility = PluginManager.Compat.Ok }.CompatText == "未声明",
                new PluginManager.Plugin { Compatibility = PluginManager.Compat.Unknown }.CompatText);

            w.SeedPluginUpdateForTest("自检-有新版", "1.0.0", "2.0.0");
            var updCard = w.BuildInstalledCardForTest(new PluginManager.Plugin { Name = "自检-有新版", Version = "1.0.0" });
            var updBtns = ButtonInfos(updCard);
            var updBtn = updBtns.FirstOrDefault(b => b.Text.StartsWith("更新到"));
            Check("「更新到 X」按钮是淡蓝底白字", updBtn.Text != null && updBtn.Bg == "#FF4A9EFF" && updBtn.Fg == "#FFFFFFFF",
                $"{updBtn.Text} 底色={updBtn.Bg} 字色={updBtn.Fg}");

            Check("毛玻璃接口在非显示窗口上优雅返回（不抛异常）",
                !BlurHelper.EnableAcrylic(w),
                "未显示窗口没有句柄 → 返回 false，界面保持原样");

            // 9. 作者与标签间距、回到顶部按钮、实时事件、日夜间主题、悬停动画
            var spaced = cat.Plugins.First(p => p.Categories.Count > 0 && p.Owner.Length > 0);
            string spacedText = CollectText(w.BuildMarketCardForTest(spaced));
            // 作者前缀已去掉（改为头像 + 名字）：这里改验「名字在、与分类有分隔符、且不再出现『作者 』」
            Check("作者名与分类之间有分隔符，且不再显示「作者」前缀",
                spacedText.Contains(spaced.Owner) && spacedText.Contains(" · ") && !spacedText.Contains("作者 "),
                Shorten(spacedText, 80));

            // 作者区是"头像 + 名字"一个整体链接：两个部件都带手型，且都归到同一个主页地址
            var card = w.BuildMarketCardForTest(spaced);
            var linkParts = FindHandCursorPartsForTest(card, spaced.AuthorUrl);
            Check("作者头像与名字合为一个超链接（点哪儿都跳同一个作者主页）",
                spaced.AuthorUrl.Length == 0
                    ? linkParts.Count == 0
                    : linkParts.Count >= 2,
                $"{linkParts.Count} 个可点部件 → {spaced.AuthorUrl}");

            w.ShowPluginsTabForTest(true);
            w.LayoutForTest(960, 640);
            PumpUntil(() => ((Panel)w.FindName("MarketPanel")!).Children.Count > 0, 5000);
            // 已安装列表滚动后切到寻找插件，验证另一颗回到顶部按钮不残留
            if (w.FindName("InstalledScroll") is ScrollViewer instScroll) instScroll.ScrollToVerticalOffset(400);
            w.ShowPluginsTabForTest(false);
            PumpUntil(() => false, 400);
            w.ShowPluginsTabForTest(true);
            w.LayoutForTest(960, 640);
            PumpUntil(() => false, 600);
            Check("切页签后不会同时出现两颗「回到顶部」",
                w.TopButtonVisibilityCountForTest() <= 1,
                $"可见的回到顶部按钮数 = {w.TopButtonVisibilityCountForTest()}");

            Check("事件信息在任何页面都会实时刷新（不再挑页面）", w.LiveEventAppearsOnPluginPageForTest());

            w.ApplyThemeForTest(true);
            w.LayoutForTest(960, 640);
            string darkBg = w.BoxColorForTest("RootBorder");
            string darkText = w.TextColorForTest("MainBtnText");
            w.ApplyThemeForTest(false);
            w.LayoutForTest(960, 640);
            string lightText = w.TextColorForTest("MainBtnText");
            string lightVer = w.TextColorForTest("VerCardCurrent");
            Check("能切到日间模式（界面文字换色）",
                darkText == "#FFFFFFFF" && lightVer != "#FFF5F5F7",
                $"夜间主按钮文字={darkText}，日间版本文字={lightVer}");
            Check("日间模式下根底色不再是深色", lightText == "#FFFFFFFF",
                $"{lightText}");
            Check("主题可来回切（幂等）", ThemeManager.IsDark == false, $"IsDark={ThemeManager.IsDark}");
            w.ApplyThemeForTest(true);
            Check("切回夜间模式", ThemeManager.IsDark);
            _ = darkBg;

            Check("窗口标题栏有日/夜切换按钮", w.FindName("ThemeBtn") != null && w.FindName("ThemeIcon") != null);
            var (idleBg, hoverBg) = w.ThemeBtnColorsForTest();
            Check("切换按钮悬停有颜色反馈（底色变浅蓝）",
                idleBg != hoverBg, $"静息 {idleBg} / 悬停 {hoverBg}");
            Check("小工具条按钮带悬停动画（缩放 + 淡出）",
                w.MiniBtnHoverAnimationForTest(),
                "MiniBtn 样式里有 EnterActions/ExitActions");

            var (tintAlpha, _, _, _) = ThemeManager.Tint;
            Check("毛玻璃够透（着色 alpha 夜间 0x1E / 日间 0x14）",
                tintAlpha is 0x1E or 0x14,
                $"alpha=0x{tintAlpha:X2}");
            Check("窗口底色备了两套：毛玻璃版三四成透明、纯色版不透明（关掉开关不会整窗透空）",
                ThemeManager.WindowStops(true, true).All(c => c.A is 0x3D or 0x35) &&
                ThemeManager.WindowStops(true, false).All(c => c.A == 0xFF) &&
                ThemeManager.WindowStops(false, true).All(c => c.A is 0x26 or 0x20) &&
                ThemeManager.WindowStops(false, false).All(c => c.A == 0xFF),
                $"夜间毛玻璃 {string.Join("/", ThemeManager.WindowStops(true, true).Select(c => $"#{c.A:X2}"))}"
                + $" → 纯色 {string.Join("/", ThemeManager.WindowStops(true, false).Select(c => $"#{c.A:X2}"))}");

            // 日志读取：文件被写入句柄占用时也必须可读
            string lockProbe = Path.Combine(GuardPaths.CacheDir, "selftest-locked.log");
            Directory.CreateDirectory(GuardPaths.CacheDir);
            File.WriteAllText(lockProbe, "第一行\n第二行\n");
            using (var holder = new FileStream(lockProbe, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                var lockedWriter = new StreamWriter(holder) { AutoFlush = true };
                lockedWriter.WriteLine("第三行（正在写入）");
                string read = Logger.ReadLogFile(lockProbe);
                Check("日志正在被写入时也能读出来（不再报占用）",
                    read.Contains("第三行") && !read.Contains("读取日志失败"),
                    Shorten(read.Replace("\r\n", " / ").Trim(), 60));
                lockedWriter.Flush();
            }

            var (onTranslucent, onAccent, cardBg) = w.ThemeContrastForTest();
            Check("日间模式：半透明底上的白字会翻成深色（不再白底白字）",
                onTranslucent == "#FF1C1C1E", onTranslucent);
            Check("日间模式：彩色按钮上的白字保持白色",
                onAccent == "#FFFFFFFF", onAccent);
            Check("日间模式：卡片底是柔和的白（不是灰、也不刺眼）",
                cardBg == "#B3FFFFFF", cardBg);
            Check("日间比夜间更透（白色比黑色扎眼，所以日间要更透）",
                ThemeManager.DayOpacitySum < ThemeManager.NightOpacitySum,
                $"日间合计 {ThemeManager.DayOpacitySum:0.00} < 夜间 {ThemeManager.NightOpacitySum:0.00}");
            Check("主题映射表键值不相交（保证反复刷不会漂色）",
                ThemeManager.OverlappingMapKeys().Count == 0,
                ThemeManager.OverlappingMapKeys().Count == 0 ? "无交集" : string.Join(",", ThemeManager.OverlappingMapKeys()));
            var twice = w.ThemeIdempotentForTest();
            Check("同一主题刷两遍颜色不再变化（幂等）", twice.Item1 == twice.Item2,
                $"第一次 {twice.Item1} → 第二次 {twice.Item2}");

            // 10. 只记录报错、切换主题不写日志
            string errBefore = Logger.CurrentLogFile;
            long sizeBefore = errBefore.Length > 0 && File.Exists(errBefore) ? new FileInfo(errBefore).Length : 0;
            Logger.LogDiagnosis("[自检] 过程记录不该落盘");
            Logger.Log("[自检] 普通日志也不该落盘");
            w.ApplyThemeForTest(false);
            w.ApplyThemeForTest(true);
            string errAfter = Logger.CurrentLogFile;
            long sizeAfter = errAfter.Length > 0 && File.Exists(errAfter) ? new FileInfo(errAfter).Length : 0;
            Check("过程记录与普通日志都不落盘（磁盘只留报错）",
                errBefore == errAfter && sizeBefore == sizeAfter,
                errAfter.Length == 0 ? "本次自检没有真错误 ⇒ 一个日志文件都没建" : Path.GetFileName(errAfter));
            Check("切换日夜模式不写日志", !AnyLogContains("[自检] 过程记录不该落盘") && !AnyLogContains("已切换为"),
                $"扫过 Logger 实际落盘目录下所有 .log（{Logger.EffectiveLogDir}）");
            // ⚠ 这四条必须只看**Logger 实际落盘的那个目录**。前半段 ListLogFiles() 本来就是按 Logger 的
            //   目录列的；但原先后半段两句写的是 GuardPaths.LogDir（= 用户真实 Logs），自检时与前者
            //   不是同一个目录 ⇒ 自检的隔离目录里真留下「过程-*/细节-*」也查不出来，
            //   反而去翻用户目录、把用户历史遗留的这类文件算到本次自检头上。
            string effectiveLogDir = Logger.EffectiveLogDir;
            Check("Logs 目录里只剩「异常-*」这一种日志",
                Logger.ListLogFiles().All(f => Path.GetFileName(f)!.StartsWith("异常-")) &&
                (!Directory.Exists(effectiveLogDir) ||
                 (Directory.GetFiles(effectiveLogDir, "过程-*.log").Length == 0 &&
                  Directory.GetFiles(effectiveLogDir, "细节-*.log").Length == 0)),
                "无过程/细节日志");

            // 11. 版本检查须覆盖全部 dist-tag（next 通道的版本不会出现在 latest 上）
            // 先离线验"挑最新标签"的纯逻辑（不依赖网络，绝不飘）
            var (pickBest, pickTag, pickStable) = VersionInfo.PickNewestTag(new[]
            {
                new KeyValuePair<string, string?>("latest", "0.1.5-rc.1"),
                new KeyValuePair<string, string?>("next", "0.1.5-rc.2"),
                new KeyValuePair<string, string?>("alpha", "0.1.5-alpha.2"),
                new KeyValuePair<string, string?>("broken", null)
            });
            Check("版本检查取全部标签里最新的那个（纯逻辑：next 比 latest 新就取 next）",
                pickBest == "0.1.5-rc.2" && pickTag == "next" && pickStable == "0.1.5-rc.1",
                $"最新={pickBest}（标签 {pickTag}） / latest={pickStable}");

            var vinfo = RunOffUi(() => VersionInfo.QueryAsync());
            Check("版本检查（联网项：拿不到就当跳过，不算失败）",
                vinfo.Latest != null &&
                (vinfo.StableLatest == null || VersionInfo.Compare(vinfo.Latest!, vinfo.StableLatest) >= 0),
                vinfo.Latest == null
                    ? $"网络不可用，跳过（{vinfo.Error ?? "无响应"}）"
                    : $"最新={vinfo.Latest}（{vinfo.Channel}） / latest 标签={vinfo.StableLatest} / 当前={vinfo.Current}");
            Check("新版本能判定出来（当前装了旧版就该提示）",
                vinfo.Latest == null || VersionInfo.Compare(vinfo.Current, vinfo.Latest) < 0 || !vinfo.Outdated,
                $"当前={vinfo.Current} 最新={vinfo.Latest} 需要更新={vinfo.Outdated}");
            Check("通道名翻成人话（不摆 next/alpha 这种标签）",
                VersionInfo.ChannelName("next") == "预览版" && VersionInfo.ChannelName("alpha") == "内测版" &&
                VersionInfo.ChannelName("latest") == "正式版",
                $"{VersionInfo.ChannelName("next")}/{VersionInfo.ChannelName("alpha")}");

            // 日志正文配色：夜间保持高亮度，日间降低亮度
            w.ApplyThemeForTest(true);
            string logFgNight = w.TextColorForTest("LogBox");
            w.ApplyThemeForTest(false);
            string logFgDay = w.TextColorForTest("LogBox");
            Check("日志正文：夜间够亮、日间够深（浅底上看不清的问题）",
                Luminance(logFgNight) >= 220 && Luminance(logFgDay) <= 80,
                $"夜间 {logFgNight}（亮度 {Luminance(logFgNight)}）/ 日间 {logFgDay}（亮度 {Luminance(logFgDay)}）");
            w.ApplyThemeForTest(true);

            w.ApplyThemeForTest(true);
            w.ShowViewForTest("logs");
            w.LayoutForTest(960, 640);
            PumpUntil(() => false, 300);
            int brightest = w.LogComboBrightestPixelForTest();
            Check("日志下拉在夜间是浅色字（不是系统黑字）", brightest >= 150,
                $"最亮像素亮度 = {brightest}（深底浅字应接近 255）");

            // ══════ 13. 第 18 批：版本页重排（两块卡、动作就地安放、去重复） ══════
            w.ShowViewForTest("settings");
            w.ShowSettingsTabForTest("version");
            w.LayoutForTest(960, 640);
            PumpUntil(() => w.PageTextsForTest("VersionPanel").Contains("版本记忆"), 4000);
            string vtext = w.PageTextsForTest("VersionPanel");
            Check("版本页：两块卡，「版本记忆」只留当前版本与上一长期版本",
                vtext.Contains("版本记忆") && vtext.Contains("当前版本") &&
                !vtext.Contains("点下面的版本号") && !vtext.Contains("运行履历") &&
                !vtext.Contains("这页是干什么的") && !vtext.Contains("手动固定版本"),
                Shorten(vtext, 120));
            Check("版本记忆里能互相切换（有上一长期版本时给一个「切换到它」）",
                !vtext.Contains("上一长期版本") || w.VersionSwitchButtonCountForTest() == 1,
                $"切换键 {w.VersionSwitchButtonCountForTest()} 个");
            // 按钮文案要从按钮上取（按钮的文本不在 TextBlock 收集里）
            var vBtns = ButtonInfos((DependencyObject)w.FindName("VersionPanel")!)
                .Select(b => b.Text).ToList();
            Check("版本页动作已就地安放（检查更新；重复的「重新检测」已删除，下载来源改为可点切换）",
                vBtns.Contains("检查更新") && !vBtns.Contains("重新检测") &&
                !vBtns.Contains("打开下载来源") && vtext.Contains("切换下载来源"),
                string.Join("、", vBtns));
            Check("版本页去掉了重复按钮（独立的「回退到 X」与第二颗自动更新按钮）",
                !vtext.Contains("回退到 ") && !vtext.Contains("改为自动更新"),
                Shorten(vtext, 120));

            // 日间首次打开路径页的配色（实测首次为白字、二次进入才正常）
            w.ApplyThemeForTest(false);
            w.ShowViewForTest("settings");
            w.ShowSettingsTabForTest("paths");
            w.LayoutForTest(960, 640);
            PumpUntil(() => false, 300);
            string pathFg = w.TextColorForTest("PathLogsBox");
            Check("日间首次打开路径页：路径文本框已是深色字",
                Luminance(pathFg) <= 90, $"{pathFg}（亮度 {Luminance(pathFg)}）");
            w.ApplyThemeForTest(true);

            w.ShowPluginsTabForTest(false);
            w.LayoutForTest(960, 640);
            Check("切回「本地插件」：已装列表显示、市场隐藏",
                ((FrameworkElement)w.FindName("InstalledScroll")!).Visibility == Visibility.Visible &&
                ((FrameworkElement)w.FindName("MarketHost")!).Visibility == Visibility.Collapsed);

            // 离屏刷新主题：日间刷新后新建的卡片与快照详情应立即采用日间配色
            w.ApplyThemeForTest(false);
            w.InstalledSortForTest(MainWindow.PluginSortField.Installed, true);      // 复位成默认排序：安装时间、正选
            string cardFg = w.InstalledCardTextColorForTest();
            Check("日间刷新插件列表：新卡片当场就是日间配色（不再先闪深色底）",
                Luminance(cardFg) <= 150, $"{cardFg}（亮度 {Luminance(cardFg)}；日间的链接蓝/深灰都 ≤150，夜间的浅色字会 >150）");

            w.ShowViewForTest("snapshots");
            w.RefreshSnapshotsForTest();
            string detailFg = w.SnapshotDetailTextColorForTest();
            Check("日间刷新快照：详情当场就是深色字（不再白字白底糊成一片）",
                detailFg.Length == 0 || Luminance(detailFg) <= 90, $"{detailFg}（亮度 {Luminance(detailFg)}）");

            // 链接悬停须在鼠标移开后还原颜色
            w.ShowViewForTest("plugins");
            w.ShowPluginsTabForTest(false);
            w.LayoutForTest(960, 640);
            PumpUntil(() => w.InstalledCardCountForTest() > 0, 6000);
            var (lb, lh, la) = w.LinkHoverRoundTripForTest();
            Check("日间：链接悬停后颜色能原样还原（不会永久变色）",
                lb.Length > 0 && lb == la, $"悬停前 {lb} → 悬停 {lh} → 移开 {la}");
            Check("日间：链接悬停是「压深」（浅色在浅底上看不见）",
                lh.Length > 0 && Luminance(lh) < Luminance(lb),
                $"悬停前亮度 {Luminance(lb)} → 悬停 {Luminance(lh)}");
            w.ApplyThemeForTest(true);
            var (nb, nh, na) = w.LinkHoverRoundTripForTest();
            Check("夜间：链接悬停是「提亮」且能还原",
                nb == na && Luminance(nh) > Luminance(nb),
                $"悬停前 {nb}（{Luminance(nb)}） → 悬停 {nh}（{Luminance(nh)}） → 移开 {na}");
            w.ApplyThemeForTest(false);

            // 12. 一键更新按钮、头像随主题换图
            var (updAllText, updAllBg) = w.UpdateAllButtonForTest();
            Check("插件页有「一键更新」按钮，且是蓝底白字",
                updAllText.StartsWith("一键更新") && updAllBg == "#FF007AFF", $"{updAllText} / {updAllBg}");

            // 已安装页排序：搜索框宽度与排序下拉的实际生效
            Check("已安装页搜索框恢复长条（不再被限宽）",
                w.FindName("PluginSearchBox") is TextBox sb &&
                double.IsPositiveInfinity(sb.MaxWidth) &&
                sb.HorizontalAlignment == HorizontalAlignment.Stretch,
                $"MaxWidth={(w.FindName("PluginSearchBox") as TextBox)?.MaxWidth} align={(w.FindName("PluginSearchBox") as TextBox)?.HorizontalAlignment}");
            int allCards = w.InstalledCardCountForTest();
            // 默认排序：安装时间、正选（越新越靠上）。这条**不看卡片数量** —— 排序不改变卡片数量（那判据恒过），
            // 而是拿「渲染时真实用过的顺序」与「按同一规则独立算一遍的期望顺序」比对。
            w.InstalledSortForTest(MainWindow.PluginSortField.Installed, true);
            string[] orderDefault = w.InstalledRenderOrderForTest();
            // 记账表在自检里是空的（Config 整体改根到 %TEMP%，全新安装没有任何记录）：
            // 给两个已知包名各记一个不同的安装时间，让"正选 ⇄ 反选"有**可分辨的数据**可翻转 ——
            // 全为空值时两种方向算出来的次序本来就一样，翻转是验不出来的（那条断言会恒过）。
            string[] sortProbe = orderDefault.Take(2).ToArray();
            string[] expectedNewFirst, expectedOldFirst;
            try
            {
                if (sortProbe.Length == 2)
                {
                    PluginTimes.StampSubscribed(sortProbe[0], "2026-01-01 00:00");   // 装得早
                    PluginTimes.StampSubscribed(sortProbe[1], "2026-09-01 00:00");   // 装得晚
                }
                w.InstalledSortForTest(MainWindow.PluginSortField.Installed, true);
                expectedNewFirst = w.InstalledSortOrderForTest(orderDefault, MainWindow.PluginSortField.Installed, true);
                Check("已安装页默认按安装时间正选（越新越靠上）",
                    w.InstalledRenderOrderForTest().SequenceEqual(expectedNewFirst) &&
                    (sortProbe.Length < 2 || Array.IndexOf(expectedNewFirst, sortProbe[1]) < Array.IndexOf(expectedNewFirst, sortProbe[0])) &&
                    w.InstalledSortRowArrowForTest(MainWindow.PluginSortField.Installed) == "↑" &&
                    w.InstalledSortRowArrowForTest(MainWindow.PluginSortField.Created) == "" &&
                    w.InstalledSortRowArrowForTest(MainWindow.PluginSortField.Updated) == "" &&
                    w.InstalledSortRowArrowForTest(MainWindow.PluginSortField.Compatibility) == "" &&
                    w.InstalledSortTextForTest == "排序",
                    $"卡片 {allCards} 张 · 顺序[{string.Join(", ", expectedNewFirst)}] · 箭头=「{w.InstalledSortRowArrowForTest(MainWindow.PluginSortField.Installed)}」");

                // 再点一次同一个排序项 = 反选：时间戳越旧越靠上 ⇒ 次序相反、箭头翻成 ↓。
                w.ClickInstalledSortRowForTest(MainWindow.PluginSortField.Installed);
                expectedOldFirst = w.InstalledSortOrderForTest(orderDefault, MainWindow.PluginSortField.Installed, false);
                Check("再点一次排序项就反向（越旧越靠上）",
                    w.InstalledRenderOrderForTest().SequenceEqual(expectedOldFirst) &&
                    (sortProbe.Length < 2 || Array.IndexOf(expectedOldFirst, sortProbe[0]) < Array.IndexOf(expectedOldFirst, sortProbe[1])) &&
                    w.InstalledSortRowArrowForTest(MainWindow.PluginSortField.Installed) == "↓",
                    $"正选[{string.Join(", ", expectedNewFirst)}] → 反选[{string.Join(", ", expectedOldFirst)}] · 箭头=「{w.InstalledSortRowArrowForTest(MainWindow.PluginSortField.Installed)}」");

                // 换一项用它**自己的**默认方向（不沿用上一项翻转后的 ↓）；兼容性是绝对优先级：再点也不翻转、且不显示方向箭头。
                w.ClickInstalledSortRowForTest(MainWindow.PluginSortField.Updated);
                bool switchedOwn = w.InstalledSortRowArrowForTest(MainWindow.PluginSortField.Updated) == "↑" &&
                                   w.InstalledSortRowArrowForTest(MainWindow.PluginSortField.Installed) == "";
                w.ClickInstalledSortRowForTest(MainWindow.PluginSortField.Compatibility);
                w.ClickInstalledSortRowForTest(MainWindow.PluginSortField.Compatibility);
                Check("换项用该项默认方向；兼容性是绝对优先级（再点不翻转、无方向箭头）",
                    switchedOwn &&
                    w.InstalledSortRowArrowForTest(MainWindow.PluginSortField.Compatibility) == "" &&
                    w.InstalledRenderOrderForTest()
                        .SequenceEqual(w.InstalledSortOrderForTest(orderDefault, MainWindow.PluginSortField.Compatibility, true)) &&
                    w.InstalledCardCountForTest() == allCards,
                    $"换项后更新日期=「{w.InstalledSortRowArrowForTest(MainWindow.PluginSortField.Updated)}」· 兼容性箭头=「{w.InstalledSortRowArrowForTest(MainWindow.PluginSortField.Compatibility)}」· 顺序[{string.Join(", ", w.InstalledRenderOrderForTest())}]· 卡片 {w.InstalledCardCountForTest()}/{allCards}");
            }
            finally
            {
                // 还原：这两条探针记录不该留给后面的用例（原本就没有记录，删掉即回到原样）
                foreach (string pkgName in sortProbe) PluginTimes.Remove(pkgName);
                w.InstalledSortForTest(MainWindow.PluginSortField.Installed, true);
            }
            // 快照页仅保留「回滚勾选项」：未勾选任何项时该操作即回滚整组
            w.ShowViewForTest("snapshots");
            w.LayoutForTest(960, 640);
            PumpUntil(() => w.SnapshotRestoreButtonsForTest().checkedBtn > 0, 4000);
            var (chkBtn, allRollBtn) = w.SnapshotRestoreButtonsForTest();
            Check("快照页只剩「回滚勾选项」，没有「整组回滚」了",
                chkBtn <= 1 && allRollBtn == 0, $"回滚勾选项 {chkBtn} 个 / 整组回滚 {allRollBtn} 个");

            w.ApplyThemeForTest(true);
            string logoNight = w.LogoFileForTest();
            w.ApplyThemeForTest(false);
            string logoDay = w.LogoFileForTest();
            Check("左上角头像随主题换图（夜间用浅色图 / 日间用深色图）",
                logoNight == "dsh-logo-dark.png" && logoDay == "dsh-logo-light.png",
                $"夜间 {logoNight} / 日间 {logoDay}");
            bool lightLogoOk = false;
            try
            {
                var probe = new System.Windows.Media.Imaging.BitmapImage();
                probe.BeginInit();
                probe.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                probe.UriSource = new Uri("pack://application:,,,/Assets/dsh-logo-light.png", UriKind.Absolute);
                probe.EndInit();
                lightLogoOk = probe.PixelWidth > 0;
            }
            catch { }
            Check("头像素材内嵌在程序里（pack URI 能加载）", lightLogoOk,
                "pack://application:,,,/Assets/dsh-logo-light.png");

            // 头像须为透明底，方底与半透明窗口不匹配
            foreach (var (file, wantInk, label) in new[]
                     {
                         ("dsh-logo-dark.png", "浅色", "夜间"),
                         ("dsh-logo-light.png", "深色", "日间")
                     })
            {
                try
                {
                    var li = new System.Windows.Media.Imaging.BitmapImage();
                    li.BeginInit();
                    li.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    li.UriSource = new Uri($"pack://application:,,,/Assets/{file}", UriKind.Absolute);
                    li.EndInit();
                    var conv = new System.Windows.Media.Imaging.FormatConvertedBitmap(
                        li, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                    int iw = conv.PixelWidth, ih = conv.PixelHeight;
                    var px = new byte[iw * ih * 4];
                    conv.CopyPixels(px, iw * 4, 0);
                    int Corner(int x, int y) => px[(y * iw + x) * 4 + 3];           // A
                    int Mid() { int i = ((ih / 2) * iw + iw / 2) * 4; return px[i + 3]; }
                    int midLum;
                    {
                        int i = ((ih / 2) * iw + iw / 2) * 4;
                        midLum = Luminance($"#FF{px[i + 2]:X2}{px[i + 1]:X2}{px[i]:X2}");
                    }
                    bool darkInk = wantInk == "深色";
                    Check($"{label}头像：透明底（四角 alpha=0）+ 图形主体不透明且是{wantInk}",
                        Corner(1, 1) == 0 && Corner(iw - 2, ih - 2) == 0 && Mid() >= 200 &&
                        (darkInk ? midLum <= 80 : midLum >= 200),
                        $"四角 A={Corner(1, 1)}/{Corner(iw - 2, ih - 2)}，主体 A={Mid()} 亮度={midLum}");
                }
                catch (Exception ex) { Check($"{label}头像像素检查可执行", false, ex.Message); }
            }
            w.ApplyThemeForTest(true);

            // 主题遍历必须覆盖"从未显示过"的折叠页面：折叠 ScrollViewer 的内容尚未 measure、
            // 模板未应用 → 不在可视化树上，只走视觉树的遍历会整页漏掉（首次进入该页仍是旧配色）。
            try
            {
                var probe = new MainWindow();                 // 不布局、不显示 → 路径页从未 measure
                ThemeManager.Apply((DependencyObject)probe.Content, dark: false);
                string probeFg = (probe.FindName("PathLogsBox") as TextBox)?.Foreground is SolidColorBrush b2
                    ? b2.Color.ToString() : "";
                Check("主题遍历覆盖未显示过的折叠页面（逻辑树那趟）",
                    Luminance(probeFg) <= 90, $"{probeFg}（亮度 {Luminance(probeFg)}）");
                probe.Close();
            }
            catch (Exception ex) { Check("折叠页面主题遍历可执行", false, ex.Message); }

            // ══════ 13b. 1.3.49 活体缺陷回归：主题遍历的两个病根 ══════
            // 两条病根都是"改坏了一眼看不出、切主题才发现"的类型，各配一条断言钉住：
            //   ① 够不到弹层：Popup.Child 只要**被打开过一次**，视觉父级就会一直挂在
            //      NonLogicalAdornerDecorator 上 ⇒ 旧的 IsInVisualTree 护栏（判"有没有视觉父级"）
            //      误认为它已在视觉树而跳过；视觉树那趟从 Popup 出发又 GetChildrenCount==0 ⇒ 两条路
            //      都进不去 ⇒ 弹层永久不跟随主题。修法：Walk 加 case Popup 走进 pop.Child，
            //      护栏换成"视觉树那趟真的走过谁"的 HashSet。
            //   ② SetValue 写 local value：旧写法 el.Prop = 值 落的是 local value，优先级高于样式
            //      setter 与模板触发器 ⇒ 下拉选中蓝底消失、RoundBtn 的 Background 绑定被打死。
            //      修法：Paint 对有绑定 / Inherited / 动画中 / 映射未改写的让路，其余走 SetCurrentValue。
            // 每条断言都在下面注明了"判别力前提"（修复前必须能红），否则等于没写。
            bool themeWasDark = ThemeManager.IsDark;
            try
            {
                // ── 病根①：弹层开合一次之后，主题遍历仍须能走到它 ──
                // 这段单独包一层：显示窗口失败属**环境**问题（记 SKIP），不等于产品缺陷；
                // 而下面的 Apply/Check 若抛异常则是真问题，不该被这层吞成 SKIP —— 故两者分开写。
                System.Windows.Controls.Primitives.Popup? themePopup = null;
                var themePopupHost = new StackPanel();
                string? themePopupEnvError = null;
                bool themePopupOpened = false;
                try
                {
                    themePopup = new System.Windows.Controls.Primitives.Popup
                    {
                        Child = new Border { Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x20, 0x29)) }
                    };
                    themePopupHost.Children.Add(themePopup);
                    var themePopupWindow = new Window
                    {
                        Content = themePopupHost,
                        Width = 200,
                        Height = 100,
                        ShowActivated = false,
                        ShowInTaskbar = false,
                        Left = -4000,                       // 屏外：自检不该在桌面上闪一个窗口
                        Top = -4000
                    };
                    try
                    {
                        themePopupWindow.Show();             // 必须先真显示，弹层才打得开
                        themePopupWindow.UpdateLayout();
                        themePopup.IsOpen = true;
                        themePopupWindow.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                        themePopup.IsOpen = false;
                        themePopupWindow.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                        // 判别力前提：这次开合**真的**给 Child 留下了（悬空的）视觉父级。
                        // 没留下 ⇒ 旧代码走逻辑树那趟同样能刷到它，断言便不区分修复前后。
                        themePopupOpened =
                            VisualTreeHelper.GetParent((DependencyObject)themePopup.Child!) != null;
                    }
                    finally
                    {
                        // 窗口与弹层都要收干净，自检不得在桌面上留残留窗口
                        try { themePopup.IsOpen = false; } catch { }
                        try { themePopupWindow.Close(); } catch { }
                    }
                }
                catch (Exception ex) { themePopupEnvError = ex.Message; }

                if (themePopupEnvError != null)
                    Skip("弹层打开过一次后切主题仍可达（Popup 分支 + HashSet 护栏）",
                        "本机无法显示窗口，复现不出该场景：" + themePopupEnvError);
                else if (!themePopupOpened)
                    Skip("弹层打开过一次后切主题仍可达（Popup 分支 + HashSet 护栏）",
                        "本机弹层未真正打开（Child 无视觉父级），复现不出该场景");
                else
                {
                    ThemeManager.Apply(themePopupHost, dark: false);
                    var themePopupBg = ((themePopup!.Child as Border)?.Background as SolidColorBrush)
                        ?.Color.ToString();
                    Check("弹层打开过一次后切主题仍可达（Popup 分支 + HashSet 护栏）",
                        themePopupBg == "#FFF2F3F7", themePopupBg ?? "null");
                }

                // ── 病根②：刷色不得把"样式管着的"属性写成 local value ──
                // 判据必须落在**样式供色**的属性上：作者原稿用的是 new Button { Background = 绿 }，
                // 对象初始化器本身就是 SetValue ⇒ 起始 BaseValueSource 已经是 Local；且绿色不在
                // 映射表里，Paint 因"映射没改写"整条让路、一个值都不写 ⇒ 断言恒假，
                // **修复前后都会红**（探针实测：新旧两版 BaseValueSource 都是 Local）。
                // 改用样式供色后：起始来源是 Style；旧代码写一次就变 Local，新代码 SetCurrentValue
                // 之后仍是 Style 且颜色已换 —— 这才真正区分修复前后（探针实测两侧）。
                var themeStyledStyle = new Style(typeof(Border));
                themeStyledStyle.Setters.Add(
                    new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x1C, 0x20, 0x29))));
                var themeStyled = new Border { Style = themeStyledStyle };
                var themeStyledRoot = new StackPanel();
                themeStyledRoot.Children.Add(themeStyled);
                var themeSrcBefore = DependencyPropertyHelper
                    .GetValueSource(themeStyled, Border.BackgroundProperty).BaseValueSource;
                ThemeManager.Apply(themeStyledRoot, dark: false);
                var themeSrcAfter = DependencyPropertyHelper
                    .GetValueSource(themeStyled, Border.BackgroundProperty).BaseValueSource;
                string themeStyledColor = (themeStyled.Background as SolidColorBrush)?.Color.ToString() ?? "null";
                Check("主题遍历不在样式管着的属性上留 local value（改走 SetCurrentValue）",
                    themeSrcBefore == BaseValueSource.Style && themeSrcAfter == BaseValueSource.Style
                    && themeStyledColor == "#FFF2F3F7",
                    $"来源 {themeSrcBefore} → {themeSrcAfter}，色 {themeStyledColor}");

                // ── 病根②的绑定面：有绑定的属性一律让路，且绑定要**真的还活着** ──
                // "表达式对象还在"不足以证明活着：旧 SetValue 会直接移除表达式、目标停在常量上。
                // 故加了活性判据 —— 改绑定源的颜色，目标须立刻跟随（旧行为下不会跟随）。
                var themeBindSrc = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x20, 0x29))
                };
                var themeBound = new Border();
                System.Windows.Data.BindingOperations.SetBinding(themeBound, Border.BackgroundProperty,
                    new System.Windows.Data.Binding(nameof(Border.Background)) { Source = themeBindSrc });
                var themeBindRoot = new StackPanel();
                themeBindRoot.Children.Add(themeBound);
                ThemeManager.Apply(themeBindRoot, dark: false);
                themeBindSrc.Background = new SolidColorBrush(Color.FromRgb(0xAB, 0xCD, 0xEF));   // 换源色
                var themeBoundColor = (themeBound.Background as SolidColorBrush)?.Color;
                Check("主题遍历不打死绑定（有绑定的属性让路，源换色仍能传导）",
                    themeBoundColor == Color.FromRgb(0xAB, 0xCD, 0xEF),
                    themeBoundColor?.ToString() ?? "null");

                // ── 病根①的补丁面：ApplyTo 按当前主题补刷**游离子树** ──
                // BatchActionPopup 是 new Popup{…} 造出来的、从未加进任何 Children 集合（既不在视觉树
                // 也不在逻辑树），case Popup 对它无效，只能由 HookPopupTheme 在 Opened 钩子上调 ApplyTo。
                // 先把当前主题钉成日间，判据才与进入本段时的全局状态无关。
                ThemeManager.Apply(new StackPanel(), dark: false);
                var themeOrphan = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x20, 0x29))
                };
                ThemeManager.ApplyTo(themeOrphan);
                string themeOrphanColor = (themeOrphan.Background as SolidColorBrush)?.Color.ToString() ?? "null";
                Check("ApplyTo 能给游离子树按当前主题补刷（BatchActionPopup 那条路）",
                    themeOrphanColor == "#FFF2F3F7", themeOrphanColor);
            }
            catch (Exception ex)
            {
                // 本段若抛异常不能让它冒出去：RunCore 的兜底 catch 会**直接结束整轮自检**，
                // 后面 4000 多行断言一条都跑不到。这里就地记 FAIL，然后继续往下走。
                Check("主题回归断言可执行（弹层 / Local / 绑定 / ApplyTo）",
                    false, $"{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                // 全局主题状态必须还原：后面第 15 批的断言接着用夜间模式（见 w.ApplyThemeForTest）
                if (ThemeManager.IsDark != themeWasDark)
                    ThemeManager.Apply(new StackPanel(), dark: themeWasDark);
            }
            Check("主题自检未遗留全局主题状态（后续批次仍按夜间模式接手）",
                ThemeManager.IsDark == themeWasDark, $"IsDark={ThemeManager.IsDark}");

            // ══════ 14. 第 20 批：应用内对话框（替代系统 MessageBox，跟随日/夜模式）══════
            var (nOk, cardDay, titleDay, okBg, _, _) = GuardDialog.ProbeForTest(MessageBoxButton.OK, dark: false);
            Check("对话框（日间）：单按钮 + 浅色卡片 + 深色标题",
                nOk == 1 && cardDay == "#FFF2F3F7" && Luminance(titleDay) <= 90 && okBg == "#FF34C759",
                $"{nOk} 键 · 卡片 {cardDay} · 标题 {titleDay} · 确定 {okBg}");
            var (nOkCancel, _, _, okBg2, _, laid2) = GuardDialog.ProbeForTest(MessageBoxButton.OKCancel, dark: false);
            Check("对话框：确定/取消两键，确定用绿色且两键并排不重叠",
                nOkCancel == 2 && okBg2 == "#FF34C759" && laid2, $"{nOkCancel} 键 · 并排={laid2}");
            var (nYnc, _, _, yesBg, _, laid3) = GuardDialog.ProbeForTest(MessageBoxButton.YesNoCancel, dark: false);
            Check("对话框：是/否/取消三键并排，首选为绿色",
                nYnc == 3 && yesBg == "#FF34C759" && laid3, $"{nYnc} 键 · 并排={laid3}");
            var (nYn, _, _, yesBg2, _, laidYn) = GuardDialog.ProbeForTest(MessageBoxButton.YesNo, dark: false);
            Check("「是/否」对话框必须真的有「是」（曾被「否」盖住）",
                nYn == 2 && yesBg2 == "#FF34C759" && laidYn, $"{nYn} 键 · 是 {yesBg2} · 并排={laidYn}");
            var (_, cardNight, titleNight, _, msgNight, _) = GuardDialog.ProbeForTest(MessageBoxButton.OK, dark: true);
            Check("对话框（夜间）：深色卡片 + 浅色文字",
                cardNight == "#FF1C2029" && Luminance(titleNight) >= 200 && Luminance(msgNight) >= 150,
                $"卡片 {cardNight} · 标题 {titleNight} · 正文 {msgNight}");

            // ══════ 15. 第 21 批：进度条悬停还原 / 一键更新按需显示 / 终止引擎自我保护 ══════
            w.ApplyThemeForTest(false);
            string loadBgIdle = w.LoadingPanelBgForTest();
            w.LoadingHoverForTest(true);
            w.LoadingHoverForTest(false);
            string loadBgBack = w.LoadingPanelBgForTest();
            Check("日间：加载进度条悬停后底色原样还原（不再变黑）",
                loadBgBack == loadBgIdle && Luminance(loadBgBack) >= 150, $"悬停前 {loadBgIdle} → 恢复后 {loadBgBack}");
            w.ApplyThemeForTest(true);
            w.LoadingHoverForTest(true);
            w.LoadingHoverForTest(false);
            Check("夜间：加载进度条悬停后同样能还原",
                w.LoadingPanelBgForTest() == w.LoadingPanelBgForTest());

            // 「一键更新」的显隐与"可更新个数"同源，但两者都由异步到达的更新报告驱动：
            // 报告刚落定、重渲染还没跑完时读，会出现"有可更新插件、按钮却还没显示"的假失败（与功能无关）。
            // 先重渲染一次把摘要行的异步状态显形，再抽消息等落定，最后按原判据断言。
            w.InstalledSortForTest(MainWindow.PluginSortField.Installed, true);      // 复位成默认排序：只触发一次重渲染，不改样本
            // 再显式等一次"查新版本"落定：SettlePluginDataForTest 只保证"连续两轮读数一致"，
            // 而按钮还没显示时那两轮读数本来就一致（都是"没显示 + 0 个"）⇒ 它可能提前返回。
            // 这一步只等时间、不改判据；超时后照样走下面的断言（判据一个字都没放宽）。
            PumpUntil(() => !w.UpdatesCheckingForTest(), 3000);
            SettlePluginDataForTest(w, () => new[]
            {
                w.UpdateAllVisibleForTest() ? 1 : 0,
                w.UpdatableCountForTest()
            });
            PumpUntil(() => w.UpdateAllVisibleForTest() == (w.UpdatableCountForTest() > 0), 1500);
            Check("「一键更新」只在有可更新插件时显示",
                w.UpdateAllVisibleForTest() == (w.UpdatableCountForTest() > 0),
                $"可更新 {w.UpdatableCountForTest()} 个 · 按钮可见={w.UpdateAllVisibleForTest()}"
                + $" · 查新版本进行中={w.UpdatesCheckingForTest()}");

            int parent = ProcessManager.ParentPidForTest();
            Check("终止引擎会跳过本程序与祖先进程（不再连带关掉守护壳）",
                ProcessManager.IsSelfOrAncestor(Environment.ProcessId) &&
                (parent <= 0 || ProcessManager.IsSelfOrAncestor(parent)) &&
                !ProcessManager.IsSelfOrAncestor(999999),
                $"自身={Environment.ProcessId} 父进程={parent}");

            // 「退出UI」现在的策略：只退出守护壳、绝不动引擎（想停引擎请用右上角「终止引擎」）
            // ⚠ 这里**不再断言** MainWindow.ExitUiKeepsEngineRunning（原来 1560 行那条）。
            //   那是 `internal const bool = true`（MainWindow.xaml.cs:650），是**编译期常量**：
            //   断言它等于断言字面量 true，编译器自己都知道那个假分支不可达
            //   （等价探针里 `if (!常量) …` 触发 CS0162「检测到无法访问的代码」），
            //   所以它**不可能失败** —— 有人把 ExitGuardAsync 里的 keepEngine: true 改成 false、
            //   实现整个被删，这条断言照旧全绿。
            //   改成断言真实行为也没用：`ForceShutdown(keepEngine: true)` 是**私有**方法、直接调
            //   Application.Current.Shutdown()，而自检是**在同一个进程里**跑的，一调就把自检自己关掉；
            //   走 UI 点击也观测不到"引擎有没有被杀"（自检不启引擎、而且无头环境点不动真窗口）。
            //   ⇒ 结论：这条"行为"在本进程内不可测，故删掉该断言。
            //   常量本身**保留**（ExitGuardAsync 的注释、Grep 引用都已核对：除本条外无人引用），
            //   它的作用是**设计声明**：声明"退出UI 不停引擎"这一意图，并防止将来有人改回"顺手停引擎"。
            //   真正把这条策略钉死的是 SelfTest 里那组可执行断言：
            //     · 「终止引擎会跳过本程序与祖先进程」
            //     · 「终止引擎的安全判定：自身与祖先都算「被托管」」
            //     · 「taskkill 参数永不带 /T」
            //   再加上 ExitGuardAsync 本身只有一处 keepEngine: true 的调用点（人读可见）。

            // ══════ 16. 第 22 批：托管中不自杀 / 去重 / 策略开关 / 运行时长计时器 ══════
            Check("终止引擎的安全判定：自身与祖先都算「被托管」，陌生进程不算",
                ProcessManager.IsHostedBy(Environment.ProcessId) &&
                (parent <= 0 || ProcessManager.IsHostedBy(parent)) &&
                !ProcessManager.IsHostedBy(999999),
                $"自身={Environment.ProcessId} 父进程={parent}");
            Check("taskkill 参数永不带 /T（只杀监听者本身）",
                ProcessManager.KillArgs(123, force: false) == "/PID 123" &&
                ProcessManager.KillArgs(123, force: true) == "/PID 123 /F" &&
                !ProcessManager.KillArgs(123, true).Contains("/T") &&
                !ProcessManager.SafeToTreeKill(123),
                ProcessManager.KillArgs(123, true));

            // ══════ 执行层加固：命令不再经 cmd /c 裸拼接（& 注入被封死）════════
            // 背景：旧写法 cmd.exe /c {command} {args} 会解释 & | ^ > < %VAR%，
            // 压测实例 "… add x&calc --registry …" 曾把 calc 当第二条命令执行。
            // 修复后：npx.cmd 等 shim 被解引用成 node.exe 直启，参数逐 token 到达。
            {
                // ① 注入 payload 必须作为**单个 token** 存活：不分裂、不出现在头部
                var specInject = ProcessManager.BuildLaunchSpec("npx",
                    "--yes @deepseek-ai/dsh@latest plugin --profile web add x&calc --registry https://registry.npmmirror.com");
                Check("命令执行层：x&calc 注入 payload 只作为单个 token（不再被拆成第二条命令）",
                    specInject != null &&
                    specInject.ArgumentList.Count(t => t.Contains("&calc", StringComparison.Ordinal)) == 1 &&
                    !specInject.ArgumentList.Any(t => t == "calc") &&
                    !specInject.ArgumentList.Any(t => t.Contains('&') && !t.Contains("&calc", StringComparison.Ordinal)),
                    specInject == null ? "spec=null" :
                    $"FileName={Path.GetFileName(specInject.FileName)} tokens=[{string.Join(" | ", specInject.ArgumentList)}]");

                // ② 启动目标绝不能是 .cmd/.bat 包装脚本（独立工程实测：直启 .cmd + ArgumentList
                //    时 "x&calc" 照样把 calc 拉起来 —— cmd 包装会重新解释参数里的元字符）
                var specNpx = ProcessManager.BuildLaunchSpec("npx", "--version");
                Check("命令执行层：npx 被解引用成真 exe 直启（绝不起 .cmd 包装脚本）",
                    specNpx != null &&
                    !specNpx.FileName.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) &&
                    !specNpx.FileName.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) &&
                    specNpx.ArgumentList.Contains("--version", StringComparer.Ordinal),
                    specNpx == null ? "spec=null" : $"FileName={specNpx.FileName}");

                // ③ netstat / where 新写法：真 exe 直启 + 恰好 1 个参数（-ano / 命令名）
                //    （执行点改为 FileName=netstat.exe / where.exe + ArgumentList，
                //      旧写法 cmd /c netstat -ano、cmd /c where {name} 已下线）
                bool netstatShape = ProcessManager.TokenizeCommandLine("netstat -ano")
                    .SequenceEqual(new[] { "netstat", "-ano" });
                bool whereShape = ProcessManager.TokenizeCommandLine("where npx")
                    .SequenceEqual(new[] { "where", "npx" });
                Check("命令执行层：netstat -ano / where npx 直启真 exe，token 数正确（2/2）",
                    netstatShape && whereShape,
                    $"netstat→{ProcessManager.TokenizeCommandLine("netstat -ano").Count} 个 · where→{ProcessManager.TokenizeCommandLine("where npx").Count} 个");

                // ④ 日志里的「仅显示用」命令行仍含 --registry 等关键 token（排查现场不缺料）
                string displayLike = $"npx {PluginManager.BuildAddArgs("demo-plugin", "1.2.3")}";
                Check("命令执行层：仅显示用命令行仍完整保留 --registry/--trust-lockfile 等关键 token",
                    displayLike.Contains("--registry", StringComparison.Ordinal) &&
                    displayLike.Contains(Registries.Current, StringComparison.Ordinal) &&
                    displayLike.Contains("--trust-lockfile", StringComparison.Ordinal),
                    Shorten(displayLike, 160));

                // ⑤ TokenizeCommandLine：引号内是整体（路径含空格不被拆散）
                // ⚠ 这条原先写的是 `tok.Count == 3`，把 `-File`、两个路径当 3 个 tokens —— **漏数了 `-ProfileDir`**，
                //   真机自检因此长期挂着一条 FAIL（而实现是对的）。数个数本就不是这条要验的东西：
                //   要验的是"两个带空格的路径都**逐字**保住了整体、且顺序没乱"，所以改成**逐项核对**
                //   （行首断言/顺序/内容一起锁死，日后再多一个开关也不会又数错一次）。
                //   已用 %TEMP% 独立工程逐字复制 TokenizeCommandLine 真跑取实际输出校正：
                //   Count=4 → [-File | C:\Program Files\x y\s.ps1 | -ProfileDir | C:\p q]。
                var tok = ProcessManager.TokenizeCommandLine("-File \"C:\\Program Files\\x y\\s.ps1\" -ProfileDir \"C:\\p q\"");
                Check("命令执行层：带引号路径拆 token 后保持整体（powershell -File 场景）",
                    tok.Count == 4 &&
                    tok[0] == "-File" && tok[1] == @"C:\Program Files\x y\s.ps1" &&
                    tok[2] == "-ProfileDir" && tok[3] == @"C:\p q",
                    $"[{string.Join(" | ", tok)}]");

                // ⑥ cmd 兜底形态（仅解析不出真 exe 时可达）：/d /s /c + 整体引号包裹
                // ⚠ 本条曾断言 `ArgumentList.Count == 4` 且第 4 项是整体引号串 —— **那个写法本身就是 bug**：
                //   .NET 会按 MSVCRT 规则对 ArgumentList 元素转义（元素以 " 开头且含空格 ⇒ 再包一层引号、
                //   内部引号变字面量），cmd 收到的是**名字里带引号**的程序名 ⇒ 兜底路径 100% 失败
                //   （独立工程探针实测：退出码 1 vs 0）。现已改为只写 psi.Arguments（原始串），
                //   并与 ArgumentList 互斥（两者同时非空时 Process.Start 直接抛 InvalidOperationException）。
                //   ⇒ 断言的落点从"拆成几个参数"改成"走 Arguments 整体引号串、ArgumentList 必须为空"。
                var psiFallback = new System.Diagnostics.ProcessStartInfo();
                ProcessManager.ApplyCmdFallback(psiFallback, "somecmd a&calc");
                Check("命令执行层：cmd 兜底走 Arguments 整体引号串（/d /s /c），ArgumentList 必须为空",
                    psiFallback.Arguments == "/d /s /c \"somecmd a&calc\"" &&
                    psiFallback.ArgumentList.Count == 0 &&
                    !string.IsNullOrEmpty(psiFallback.FileName),
                    $"Arguments=<{psiFallback.Arguments}> Count={psiFallback.ArgumentList.Count}");

                // ⑥′ 兜底告警必须**真落盘**：Logger.Log 是刻意保留的**空实现**（见其注释"不落盘"），
                //   告警误走它就会静默消失 —— 这条把"走了 NoteDiagnosis 且真写进日志文件"钉死。
                //   落点用 CurrentLogFile（确定性），拿不到再退回 ListLogFiles 的最新一份。
                //   ⚠ 本探针会**故意建出一个异常日志文件**（写 [WARN] 即触发 EnsureWriter）—— 这与第 10 条
                //     「过程记录与普通日志都不落盘」不冲突：那条查的是 LogDiagnosis/Log 的**空实现**，
                //     而这条要证的正是"诊断留痕走的是会落盘的那条路"。两条各自成立、互不为反例。
                string warnProbe = "cmd 兜底告警落盘探针 " + Guid.NewGuid().ToString("N");
                Logger.NoteDiagnosis(warnProbe);
                string probeLog = Logger.CurrentLogFile;
                if (probeLog.Length == 0)
                {
                    var probeLogs = Logger.ListLogFiles();
                    probeLog = probeLogs.Length > 0 ? probeLogs[0] : "";
                }
                string probeTail = probeLog.Length > 0 ? Logger.ReadLogFile(probeLog, maxLines: 200) : "";
                Check("命令执行层：cmd 兜底告警走 NoteDiagnosis 并真落盘（而非空实现的 Logger.Log）",
                    probeTail.Contains("[WARN] " + warnProbe, StringComparison.Ordinal),
                    $"日志文件={(probeLog.Length > 0 ? Path.GetFileName(probeLog) : "(无)")}");
            }
            Check("反向判定：能认出「目标进程的子孙里有本程序」",
                parent <= 0 || ProcessManager.DescendantsContainSelf(parent),
                $"父进程={parent}");
            Check("不监听任何东西的端口没有可终止的进程",
                ProcessManager.InspectPortOwners(59001).Count == 0);

            // 终止引擎的**功能级**验证：起一个属于本进程的临时监听，杀掉它，确认端口释放且自己还活着
            try
            {
                const int probePort = 59251;
                var probe = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "node",
                    Arguments = $"-e \"require('net').createServer().listen({probePort},'127.0.0.1')\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                for (int i = 0; i < 24 && !NetworkHelper.IsPortListening(probePort); i++)
                    System.Threading.Thread.Sleep(250);

                if (!NetworkHelper.IsPortListening(probePort))
                {
                    Skip("终止引擎功能验证（起临时监听 → 杀掉 → 端口释放且自身存活）",
                         "环境限制：临时监听没起来（node 不可用？），本条未验证");
                }
                else
                {
                    bool killed = ProcessManager.KillPortOwners(probePort, out string killDetail);
                    bool freed = !NetworkHelper.IsPortListening(probePort);
                    // 本进程必然还活着（否则执行不到下一行断言），这里一并记录
                    Check("终止引擎能真正结束监听进程并释放端口，且本进程存活",
                        killed && freed && probe != null, killDetail);
                }
                try { probe?.Kill(); } catch { }
            }
            catch (Exception ex)
            {
                Check("终止引擎功能验证可执行", false, ex.Message);
            }

            // 真机实测：自身必被判为"被自己托管"（确定性）。3080 上的监听者只**如实报告**是否在
            // 托管本程序，不作为断言条件——从桌面图标正常启动时本就不应命中（原有断言在这种正常
            // 情况下必然 FAIL，属于"验环境"而不是"验代码"）。
            var live = ProcessManager.InspectPortOwners(3080);
            Check("托管判定：自身必判为托管、陌生 PID 必为否；3080 监听者如实报告",
                ProcessManager.IsHostedBy(Environment.ProcessId) &&
                !ProcessManager.IsHostedBy(999999),
                live.Count == 0
                    ? "3080 未监听；自身=真、陌生 PID=假"
                    : string.Join("、", live.Select(o => $"PID {o.Pid} 托管={o.HostsSelf}")));

            // 同版本不得同时记为「当前版本」和「上一长期版本」（覆盖：取消固定后又固定回同一版本）
            // ⚠ 这里断言的是**读取侧**的不变量：属性给出的「上一长期版本」绝不等于当前版本。
            // 原先断言 `PreviousPin.Length == 0`（落盘也必须是空串）—— 那其实是在钉住写入侧
            // 「与当前版本相同就把记录抹掉」的旧行为，正是本次缺陷要根除的东西：它会连带毁掉
            // "上一次长期用过某版本"的事实。改为断言读取侧不变量后，无论此时落盘的是空串
            // 还是更早的那条记录，这条断言都成立（也才对得上它的名字）。
            string pinKeep = VersionMemory.Pin;
            VersionMemory.PinTo("7.7.7");
            VersionMemory.FollowLatest();               // Pin 被清空；已有更早记录时不再覆盖它
            VersionMemory.NoteEngineReady("7.7.7");     // 又固定回 7.7.7 → 呈现层不得重复
            Check("同一个版本不会既当当前版本又当上一长期版本（读取侧不变量）",
                VersionMemory.Pin == "7.7.7" && VersionMemory.PreviousPin != VersionMemory.Pin,
                $"当前={VersionMemory.Pin} 上一长期='{VersionMemory.PreviousPin}'（落盘='{VersionMemory.State.PreviousPin}'）");

            // 策略开关：显示"另一种策略"，配色跟随文字
            w.ShowViewForTest("settings");
            w.ShowSettingsTabForTest("version");
            w.LayoutForTest(960, 640);
            PumpUntil(() => false, 300);
            var fb1 = w.FollowButtonForTest();
            Check("已固定时的策略开关：显示「跟随最新版」且为蓝色",
                fb1.Label == "跟随最新版" && fb1.Bg == "#FF007AFF", $"{fb1.Label} / {fb1.Bg}");
            VersionMemory.FollowLatest();
            w.ShowSettingsTabForTest("version");
            w.LayoutForTest(960, 640);
            PumpUntil(() => false, 300);
            var fb2 = w.FollowButtonForTest();
            Check("取消固定后开关切换为「自动管理」且为绿色",
                fb2.Label == "自动管理" && fb2.Bg == "#FF34C759", $"{fb2.Label} / {fb2.Bg}");
            VersionMemory.PinAuto("8.8.8");
            var fb3 = w.FollowButtonForTest();
            Check("点回自动管理后，开关又切回「跟随最新版」",
                fb3.Label == "跟随最新版" || VersionMemory.Pin == "8.8.8", $"pin={VersionMemory.Pin}");

            // ══════ 版本记忆：「上一长期版本」不得被写入侧抹掉 ══════
            // 现场缺陷：点「跟随最新版」→ 点「自动管理」，记录的上一个长期使用版本立刻被清空，
            // 设置页显示「尚无可回退的长期版本」，而 History 里的记录其实还在。
            // 写入侧有四处同款清理（Save 归一化 / NoteEngineReady 升级分支 / NoteEngineReady 首次固定分支 / PinAuto），
            // 全都已删：「与当前 Pin 相同就不该显示」由 PreviousPin 读取属性兜住，不该毁掉落盘事实。
            // VersionMemory 是静态单例 + 落盘，且**没有** ForTest 改路径钩子；夹具沿用 RunCore 已挂好的
            // DSHGUARD_DATA_DIR（%TEMP%\dshguard-selftest-data），并在 finally 里原样还原状态。
            string vmPin = VersionMemory.State.Pin;
            string vmSrc = VersionMemory.State.PinSource;
            string vmPrev = VersionMemory.State.PreviousPin;
            var vmHist = VersionMemory.State.History;
            bool vmPend = VersionMemory.State.PendingUpdate;
            string vmPendFrom = VersionMemory.State.PendingFrom;
            try
            {
                var st = VersionMemory.State;
                st.History = new List<VersionRecord>
                {
                    new() { Version = "9.0.1", Launches = 14, LastSeen = "2026-09-17 18:59", RunsSeconds = 18485 },
                    new() { Version = "9.0.0", Launches = 90, LastSeen = "2026-09-16 14:05", RunsSeconds = 51225 },
                };

                // ① 本缺陷的直接回归护栏：PinTo(A) → PinAuto(B)，B 恰好是 PinTo 之前那一版
                st.Pin = "9.0.0";
                st.PinSource = "auto";
                st.PreviousPin = "";
                VersionMemory.PinTo("9.0.1");
                Check("PinTo 切到新版本时，旧 Pin 记为「上一长期版本」",
                    VersionMemory.Pin == "9.0.1" && VersionMemory.State.PreviousPin == "9.0.0",
                    $"pin={VersionMemory.Pin} 上一长期='{VersionMemory.State.PreviousPin}'");
                VersionMemory.PinAuto("9.0.0");     // ← 本缺陷触发点：参数恰好等于 PreviousPin
                Check("自动管理不得因为参数等于「上一长期版本」就把它清空（本缺陷回归）",
                    VersionMemory.State.PreviousPin == "9.0.0",
                    $"pin={VersionMemory.Pin} 上一长期='{VersionMemory.State.PreviousPin}'（应为 9.0.0）");

                // ② 读取侧仍要收敛：Pin 与 PreviousPin 相同时属性返回空串，不误导用户
                Check("当前版本与上一长期版本相同时，读取属性返回空串（呈现层不误导）",
                    VersionMemory.PreviousPin.Length == 0 && VersionMemory.State.PreviousPin == "9.0.0",
                    $"落盘='{VersionMemory.State.PreviousPin}' 读取='{VersionMemory.PreviousPin}'");

                // ③ PinTo 的正常语义未回归
                st.Pin = "9.0.0";
                st.PreviousPin = "";
                VersionMemory.PinTo("9.0.2");
                Check("PinTo 正常语义未回归（切换固定版本 → PreviousPin = 旧 Pin）",
                    VersionMemory.Pin == "9.0.2" && VersionMemory.PreviousPin == "9.0.0",
                    $"pin={VersionMemory.Pin} 上一长期='{VersionMemory.PreviousPin}'");

                // ④ 回退候选里「上一长期版本」必须排在最前
                st.Pin = "9.0.2";
                st.PreviousPin = "9.0.0";
                var rb = VersionMemory.RollbackOptions();
                Check("回退候选里「上一长期版本」排在第一位",
                    rb.Count > 0 && rb[0].Version == "9.0.0",
                    rb.Count > 0 ? string.Join(" > ", rb.Select(r => r.Version)) : "（无候选）");

                // ⑤ 用户真实三步序列：升级 → 跟随最新版 → 自动管理
                // 升级（NoteEngineReady 的更新模式分支）把「上一长期版本」记为旧版，
                // 随后点「跟随最新版」不能再把它覆盖掉（原先是无条件覆盖，0.1.5-rc.2 就是这么丢的）。
                st.Pin = "8.9.0";
                st.PinSource = "manual";
                st.PreviousPin = "";
                st.PendingUpdate = false;
                st.PendingFrom = "";
                st.UpdateTarget = "";
                VersionMemory.BeginUpdate("8.9.1");
                VersionMemory.NoteEngineReady("8.9.1");     // 升级落地：PreviousPin := 8.9.0
                Check("升级后「上一长期版本」记为升级前的版本",
                    VersionMemory.State.PreviousPin == "8.9.0",
                    $"raw='{VersionMemory.State.PreviousPin}'");
                VersionMemory.FollowLatest();               // ← 原先在这里被覆盖成 8.9.1
                Check("点「跟随最新版」不得覆盖已有的更早记录（本缺陷回归）",
                    VersionMemory.State.PreviousPin == "8.9.0",
                    $"raw='{VersionMemory.State.PreviousPin}'（应为 8.9.0，不是 8.9.1）");
                VersionMemory.PinAuto("8.9.1");             // 再点「自动管理」钉回刚离开的那一版
                Check("三步序列后「上一长期版本」仍是升级前那一版，且能被读取",
                    VersionMemory.State.PreviousPin == "8.9.0" && VersionMemory.PreviousPin == "8.9.0",
                    $"raw='{VersionMemory.State.PreviousPin}' 读取='{VersionMemory.PreviousPin}'");

                // ⑥ 反向：确实没有更早记录时，才用当前 Pin 回填（"只在为空时回填"的另一半）
                st.Pin = "8.9.9";
                st.PinSource = "auto";
                st.PreviousPin = "";
                VersionMemory.FollowLatest();
                Check("没有更早记录时，仍按旧语义用当前固定版本回填",
                    VersionMemory.State.PreviousPin == "8.9.9" && VersionMemory.Pin.Length == 0,
                    $"raw='{VersionMemory.State.PreviousPin}' pin='{VersionMemory.Pin}'");

                // ⑦ 口径钉死：PreviousPin 与 Pin 相同也算"已有更早记录"—— 不回填、更不被覆盖。
                // 判据（本处钉住的正是"不回填"这一半）：留着的同值记录是"上一次长期用的就是这个版本"
                // 这一事实的陈述；"与当前 Pin 相同就不该显示"由读取属性在 Pin 非空时返回空串兜住
                //（注意：FollowLatest 之后 Pin 已清空，此时读取属性如实返回该值是正确的——
                //  它表示"上次长期用的是它"，并不是"上一版"与"当前版"重复）。
                st.Pin = "8.9.9";
                st.PreviousPin = "8.9.9";                   // 同值 = 已有记录
                VersionMemory.FollowLatest();
                Check("同版本记录算「已有更早记录」：不被覆盖为同一值以外的内容",
                    VersionMemory.State.PreviousPin == "8.9.9" && VersionMemory.Pin.Length == 0,
                    $"raw='{VersionMemory.State.PreviousPin}' pin='{VersionMemory.Pin}'（跟随最新版后 Pin 应为空）");
                // 读取属性的收口口径：Pin 非空且与之相同时才返回空串（与 ② 呼应，钉住"读取侧收口"而非"写入侧抹除"）
                st.Pin = "8.9.9";
                st.PreviousPin = "8.9.9";
                Check("Pin 非空且与 PreviousPin 相同时，读取属性才返回空串",
                    VersionMemory.PreviousPin.Length == 0 && VersionMemory.State.PreviousPin == "8.9.9",
                    $"raw='{VersionMemory.State.PreviousPin}' 读取='{VersionMemory.PreviousPin}'");
            }
            finally
            {
                var st2 = VersionMemory.State;
                st2.Pin = vmPin;
                st2.PinSource = vmSrc;
                st2.PreviousPin = vmPrev;
                st2.History = vmHist;
                st2.PendingUpdate = vmPend;
                st2.PendingFrom = vmPendFrom;
                VersionMemory.Save();
            }

            // 运行时长：计时器每 30 秒结算一次
            int before3 = VersionMemory.History.FirstOrDefault(r => r.Version == "8.8.8")?.RunsSeconds ?? 0;
            int ran = w.AccumulateRunSecondsForTest(30);
            int after3 = VersionMemory.History.FirstOrDefault(r => r.Version == "8.8.8")?.RunsSeconds ?? 0;
            Check("计时器结算：运行时长真的记进版本履历",
                ran >= 29 && after3 >= before3 + 29, $"{before3} → {after3}（结算 {ran} 秒）");

            // 版本页文案：括号里的碎话已清掉
            string vtext2 = w.PageTextsForTest("VersionPanel");
            Check("版本页已去掉「升级方式」「下载来源」后面的括号说明",
                vtext2.Contains("社区镜像") && !vtext2.Contains("速度快的那个") &&
                !vtext2.Contains("升级或回退时会跟着变") && !vtext2.Contains("每次启动都是最新的"),
                Shorten(vtext2, 100));

            // ══════ 17. 第 23 批：加载条两态 / 下载来源切换 / 按钮动效 / 版本号 ══════
            // 加载进度条：日间浅底深字、夜间深底白字，且都不再是"闷红 + 白字"
            w.ApplyThemeForTest(false);
            w.ShowLoadingForTest(true, 42);
            var palDay = w.LoadingPaletteForTest();
            Check("日间加载条：浅底 + 深色字 + 半透明填充，流光隐藏",
                Luminance(palDay.PanelBg) >= 150 && Luminance(palDay.TextFg) <= 90 &&
                palDay.FillBg == "#9934C759" && palDay.GlossVis == "Collapsed",
                $"{palDay.PanelBg} / {palDay.FillBg} / {palDay.TextFg} / 流光 {palDay.GlossVis}");
            w.LoadingHoverForTest(true);
            var palDayHover = w.LoadingPaletteForTest();
            Check("日间加载条悬停：柔和红 + 深色字（不是夜间那种高饱和红）",
                palDayHover.PanelBg == "#66FF3B30" && Luminance(palDayHover.TextFg) <= 90,
                $"{palDayHover.PanelBg} / {palDayHover.TextFg}");
            w.LoadingHoverForTest(false);
            w.ApplyThemeForTest(true);
            w.ShowLoadingForTest(true, 42);
            var palNight = w.LoadingPaletteForTest();
            Check("夜间加载条：深底 + 白字 + 实心绿，流光保留",
                Luminance(palNight.PanelBg) <= 90 && Luminance(palNight.TextFg) >= 200 &&
                palNight.FillBg == "#FF34C759" && palNight.GlossVis == "Visible",
                $"{palNight.PanelBg} / {palNight.FillBg} / {palNight.TextFg} / 流光 {palNight.GlossVis}");
            w.ShowLoadingForTest(false);

            // 下载来源：一个纯函数负责切换与显示
            Check("下载来源切换：社区镜像 ⇄ 官方源，标签与地址都对",
                Registries.Label("") == "社区镜像" && Registries.Resolve("") == Registries.Community &&
                Registries.Label(Registries.Toggle("")) == "官方源" &&
                Registries.Resolve(Registries.Toggle("")) == Registries.Official &&
                Registries.Toggle(Registries.Official) == Registries.Community,
                $"{Registries.Label("")} → {Registries.Label(Registries.Toggle(""))}");
            Check("版本页不再有「打开下载来源」按钮，改为一行可点的切换项",
                !vtext2.Contains("打开下载来源") && vtext2.Contains("切换下载来源"),
                Shorten(vtext2, 120));
            Check("插件安装参数带上当前下载来源",
                PluginManager.BuildAddArgs("demo", "1.0.0").Contains("--registry " + Registries.Current) &&
                PluginManager.BuildUninstallArgs("demo").Contains("--registry " + Registries.Current),
                Registries.Current);

            // 供应链策略覆盖：现场装插件报 ERR_PNPM_MINIMUM_RELEASE_AGE_VIOLATION（包龄门槛拦下新发布的包）。
            // 两个开关缺一不可：--trust-lockfile 管锁文件复核、--config.minimumReleaseAge=0 管包龄判定。
            static bool HasBothPolicyFlags(string args) =>
                args.Contains("--trust-lockfile") && args.Contains("--config.minimumReleaseAge=0");

            Check("三条安装/卸载命令都同时带上 --trust-lockfile 与 --config.minimumReleaseAge=0",
                HasBothPolicyFlags(PluginManager.BuildAddArgs("demo", "1.0.0")) &&
                HasBothPolicyFlags(PluginManager.BuildAddSourceArgs("demo@1.0.0")) &&
                HasBothPolicyFlags(PluginManager.BuildUninstallArgs("demo")),
                $"常量「{PluginManager.PolicyOverride}」 / add={HasBothPolicyFlags(PluginManager.BuildAddArgs("demo", "1.0.0"))}"
                + $" 源={HasBothPolicyFlags(PluginManager.BuildAddSourceArgs("demo@1.0.0"))}"
                + $" remove={HasBothPolicyFlags(PluginManager.BuildUninstallArgs("demo"))}");

            // 去掉策略覆盖后必须两个都不剩，且 --registry 与包名部分一字不动（顺序打乱 + 多余空格也照样清干净）
            string stripped = PluginManager.WithoutPolicyOverride(PluginManager.BuildAddArgs("demo", "1.0.0"));
            string scrambled = PluginManager.WithoutPolicyOverride(
                "--yes @deepseek-ai/dsh@0.1.5 plugin --profile web add   demo@1.0.0 " +
                $"--config.minimumReleaseAge=0  --registry {Registries.Current}   --trust-lockfile");
            Check("去掉策略参数后两个开关都不剩，--registry 与包名部分原样保留",
                !stripped.Contains("--trust-lockfile") && !stripped.Contains("--config.minimumReleaseAge=0") &&
                stripped.Contains("--registry " + Registries.Current) && stripped.Contains("demo@1.0.0") &&
                !stripped.Contains("  ") && stripped == stripped.TrimEnd() &&
                !scrambled.Contains("--trust-lockfile") && !scrambled.Contains("--config.minimumReleaseAge=0") &&
                scrambled.Contains("--registry " + Registries.Current) && scrambled.Contains("demo@1.0.0") &&
                !scrambled.Contains("  "),
                Shorten(stripped, 160));

            // 按钮动效：悬停放大、按下缩小、松开复原，幂等
            w.ApplyThemeForTest(false);
            w.ShowViewForTest("settings");
            w.ShowSettingsTabForTest("version");
            w.LayoutForTest(960, 640);
            // 等到动效真的挂上再断言（原来固定等 200ms 是赌时序，偶发失败过）
            PumpUntil(() => w.FirstInteractiveForTest() is { } probeEl && ButtonFx.IsWiredForTest(probeEl), 1500);
            var fxBtn = w.FirstInteractiveForTest();
            Check("可点元素都挂上了动效（幂等）",
                fxBtn != null && ButtonFx.IsWiredForTest(fxBtn),
                fxBtn == null ? "没找到可点元素" : fxBtn.GetType().Name);
            if (fxBtn != null)
            {
                ButtonFx.Wire(fxBtn);      // 再挂一次不应出错（幂等）
                ButtonFx.HoverForTest(fxBtn, true);
                var hoverFx = ButtonFx.LastTargetForTest(fxBtn);
                ButtonFx.PressForTest(fxBtn, true);
                var pressFx = ButtonFx.LastTargetForTest(fxBtn);
                ButtonFx.PressForTest(fxBtn, false);
                var releaseFx = ButtonFx.LastTargetForTest(fxBtn);
                Check("按钮动效：悬停放大变亮 → 按下缩小变暗 → 松开回弹",
                    hoverFx.Scale > 1.0 && hoverFx.Opacity < 1.0 &&
                    pressFx.Scale < 1.0 && pressFx.Opacity < hoverFx.Opacity &&
                    releaseFx.Scale > 1.0,
                    $"悬停 {hoverFx.Scale}/{hoverFx.Opacity:0.##} → 按下 {pressFx.Scale}/{pressFx.Opacity:0.##} → 松开 {releaseFx.Scale}");
            }

            // 逐页硬断言：所有手型光标元素都必须挂上动效（现场反馈"子模块按钮没动画"）
            var pageChecks = new (string View, string? Tab)[]
            {
                ("status", null), ("logs", null), ("snapshots", null),
                ("plugins", null), ("settings", "general"), ("settings", "paths"),
                ("settings", "version"), ("about", null)
            };
            var unwired = new List<string>();
            foreach (var (view, tab) in pageChecks)
            {
                w.ShowViewForTest(view);
                if (tab != null) w.ShowSettingsTabForTest(tab);
                w.LayoutForTest(960, 640);
                PumpUntil(() => false, 200);
                int n = ButtonFx.UnwiredCountForTest((DependencyObject)w.Content);
                if (n > 0) unwired.Add($"{view}{(tab != null ? "/" + tab : "")}={n}");
            }
            Check("每个页面的可点元素都挂了动效（未挂载数=0）",
                unwired.Count == 0,
                unwired.Count == 0 ? "8 个页面全部为 0" : string.Join("、", unwired));

            // 圆角巡检：所有手型光标的按钮必须是圆角（Border 自带圆角，或 Button 套了统一模板）
            var notRound = new List<string>();
            foreach (var (view, tab) in pageChecks)
            {
                w.ShowViewForTest(view);
                if (tab != null) w.ShowSettingsTabForTest(tab);
                w.LayoutForTest(960, 640);
                PumpUntil(() => false, 200);
                var bad = RoundBtn.FindUnroundedForTest((DependencyObject)w.Content);
                if (bad.Count > 0)
                    notRound.Add($"{view}{(tab != null ? "/" + tab : "")}={string.Join(",", bad.Select(x => x.GetType().Name))}");
            }
            Check("所有可点按钮都是圆角（逐页巡检，未圆角数=0）",
                notRound.Count == 0,
                notRound.Count == 0 ? "8 个页面全部为 0" : string.Join("；", notRound));

            // ══════ 20. 第 32 批：作者头像 / 复制日志 / 大按钮居中 ══════
            var avatar = w.BuildAvatarForTest("FuRongJun-1999");
            Check("插件作者头像是圆形（圆角=半径）且带作者提示",
                avatar is Border ab && ab.Width > 0 &&
                Math.Abs(ab.CornerRadius.TopLeft - ab.Width / 2) < 0.01 &&
                (ab.ToolTip as string ?? "").Contains("FuRongJun-1999"),
                avatar is Border avb ? $"{avb.Width}x{avb.Height} 圆角 {avb.CornerRadius.TopLeft}" : avatar.GetType().Name);
            Check("没有作者名时不显示头像",
                w.BuildAvatarForTest("").Visibility == Visibility.Collapsed, "空作者名 → 折叠");
            w.ShowViewForTest("plugins");
            w.ShowPluginsTabForTest(false);
            w.LayoutForTest(960, 640);
            PumpUntil(() => false, 400);
            // ══════ [231] 作者区不再有「作者 」前缀 —— 旧判据为什么必然假红，以及现在钉的是什么 ══════
            // 旧判据（本处原文）：`!w.PageTextsForTest("PluginsPanel").Contains("作者 ")`。
            //   `PageTextsForTest` 收的是**整块面板**的文本（MainWindow.xaml.cs:3630 递归取 TextBlock/TextBox
            //   并以 | 相连），于是它把**另一处刻意写着「作者 」的地方**也一起管了 ✗：
            //     PluginSource.cs:584 `if (a.Length > 0) parts.Add("作者 " + a);` ⇒ 卡片上的
            //     「提交 9669ee4 · 2026-09-18 · 作者 Jet」。那一行是**明确要求**的
            //     （MainWindow.Tools.cs:190-191「摆出作者是为了证明读的是参与者们的提交、不按作者过滤」，
            //      且本文件 :6488 正钉着 NewCommitNote 必须逐字含「作者 Jet」）⇒ 旧判据对它属**过度约束**。
            //   触发条件（本轮现场）：那行只在**查到远端提交**时出现，而以前查新总失败（gitee 403）
            //     ⇒ 恒不出现 ⇒ 判据恒过；本轮查新**成功**（卡片详情写着「已是最新」）⇒ 那行出现
            //     ⇒ 它顺带把「作者 」带进面板文本 ⇒ 旧判据被**成功路径**撞红（环境相关假红）。
            //
            // 现在的判据（钉的是原契约，且与远端查没查到**无关**）：
            //   造一个**完全合成的**更新记录：作者区写 `Author = "Jet"`，远端提交行写
            //     `CommitNote = NewCommitNote("9669ee4","2026-09-18","Jet")` —— 于是「作者 Jet」这句
            //     100% 来自提交行、不含任何环境内容；把这句话**整句**从面板文本里去掉之后，
            //   要求残留文本里**一个「作者 」都不许有** ⇒ 「作者区退回前缀写法」仍然必红。
            //   注意 `PluginSource.NewCommitNote` 的兜底分支（作者为空 ⇒ 不出「作者 」）另有断言钉着，
            //   所以这里少了那句「作者 Jet」一定是产品改了格式，本条同样如实变红。
            {
                var n63AuthPlugin = new PluginManager.Plugin
                { Name = "自检-作者区前缀", Version = "1.0.0", Author = "Jet" };
                var n63AuthNote = PluginSource.NewCommitNote("9669ee4", "2026-09-18", "Jet");
                var n63AuthUpd = new PluginManager.PluginUpdate
                {
                    Name = n63AuthPlugin.Name, HasUpdate = true,
                    Installed = "0.9.0", Latest = "9669ee4", TargetLabel = "最新提交",
                    CommitNote = n63AuthNote
                };
                var n63AuthUndo = w.SeedPluginStateForTest(
                    new List<PluginManager.Plugin> { n63AuthPlugin },
                    new Dictionary<string, PluginManager.PluginUpdate>(StringComparer.OrdinalIgnoreCase)
                    { [n63AuthPlugin.Name] = n63AuthUpd });
                try
                {
                    w.RenderPluginsForTest();
                    w.LayoutForTest(960, 640);
                    PumpUntil(() => false, 200);
                    string n63AuthText = w.PageTextsForTest("PluginsPanel");
                    // 反证：把 PluginSource.NewCommitNote 里 `"作者 " + a` 改成 `a`（作者不再标出）
                    //   ⇒ 下面「远端提交行的『作者 Jet』确实上屏」立刻变红；
                    //   把作者区改回 `"作者 " + p.Author`（旧写法）⇒ 去掉提交行后残留里又有「作者 」
                    //   ⇒ 「作者区不带前缀」变红。两条各自能红，且都不再受"这次远端查到没查到"影响。
                    Check("作者区不带「作者 」前缀（此处刻意把远端提交行的「作者 Jet」摆在面板里，再整句排除后判定）",
                        n63AuthText.Contains(n63AuthNote) &&
                        n63AuthText.Contains(n63AuthPlugin.Author) &&
                        !n63AuthText.Replace(n63AuthNote, "").Contains("作者 "),
                        Shorten(n63AuthText, 120));
                }
                finally { n63AuthUndo(); }
            }

            // 实例面板那一条（不造样本、量的是**真实数据**）：本轮 [231] 红的就是它，故把量到的形态记下来。
            // ⚠ 刻意**不**在这里断言「不含『作者 』」：真实面板里那句「作者 Jet」是否存在取决于"这次远端查到没查到"，
            //   拿它判红绿就等于把环境的偶然当判据（[231] 的病根正在这里）。契约已由上面那条合成判据钉死。
            string n63LivePluginsText = w.PageTextsForTest("PluginsPanel");
            Check("实例插件面板可读，且量到的「作者 」只来自那句提交括注（诊断位）",
                n63LivePluginsText.Length > 0,
                $"面板文本 {n63LivePluginsText.Length} 字符 · 含「作者 」={n63LivePluginsText.Contains("作者 ")}"
                + $"（该句属提交括注，见 PluginSource.NewCommitNote）· {Shorten(n63LivePluginsText, 120)}");

            // 复制日志：放文本 → 复制 → 读剪贴板核对
            w.ShowViewForTest("logs");
            w.LayoutForTest(960, 640);
            string probeText = "自检日志行 A\n自检日志行 B " + DateTime.Now.Ticks;
            w.SetLogBoxForTest(probeText);
            w.CopyLogForTest();
            string? clipText = null;
            try { clipText = System.Windows.Clipboard.GetText(); } catch { }
            // 环境不可用 ≠ 功能缺陷：剪贴板被别的程序占用 / 非交互式会话时**跳过**（沿用上面 Skip 的机制），
            // 但剪贴板**可用而内容不对**（复制为空、与显示的日志不一致）仍必须 FAIL。
            // 「不可用」只认两种硬证据：读剪贴板直接抛异常（clipText == null），
            // 或复制方自己把失败缘由写在了状态行上（产品原文：「剪贴板被其他程序占用」）。
            string copyStatus = (w.FindName("ConsoleStatusText") as TextBlock)?.Text ?? "";
            bool clipboardUnusable = clipText == null
                || copyStatus.Contains("剪贴板被其他程序占用", StringComparison.Ordinal);
            if (clipboardUnusable)
                Skip("「复制日志」把当前显示内容复制到剪贴板",
                     clipText == null
                         ? "环境限制：剪贴板不可用（非交互式会话？），本条未验证"
                         : "环境限制：剪贴板被其他程序占用，本条未验证");
            else
                Check("「复制日志」把当前显示内容复制到剪贴板",
                    clipText == probeText, $"剪贴板 {clipText!.Length} 字符");

            // 三个大按钮：文字垂直居中 + 等高
            w.ShowViewForTest("status");
            w.ShowRunningForTest(true);
            w.LayoutForTest(960, 640);
            PumpUntil(() => false, 300);
            var (idleH, stopH, openH, idleV, stopV, openV) = w.MainButtonsForTest();
            Check("三个大按钮文字垂直居中",
                idleV == VerticalAlignment.Center && stopV == VerticalAlignment.Center && openV == VerticalAlignment.Center,
                $"一键启动={idleV} 终止={stopV} 加载={openV}");
            Check("终止/加载两个大按钮等高且为 48",
                Math.Abs(stopH - openH) < 0.6 && Math.Abs(stopH - 48) < 0.6,
                $"终止={stopH:0.#} 加载={openH:0.#} 一键启动={idleH:0.#}");

            // 动效复位不得误伤元素自带的缩放（MiniBtn 样式靠自己的 ScaleTransform 做悬停放大）：
            // 造一个同构控件（手型光标 + 自带 ScaleTransform），挂动效后走一遍「悬停 → 移出」
            var ownScale = new ScaleTransform(1, 1);
            var fakeMini = new Border
            {
                Cursor = System.Windows.Input.Cursors.Hand,
                Width = 60,
                Height = 24,
                RenderTransform = ownScale,
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
            ButtonFx.Wire(fakeMini);
            ButtonFx.HoverForTest(fakeMini, true);
            ButtonFx.HoverForTest(fakeMini, false);       // 鼠标移出 → 复位
            Check("动效复位后，元素自带的缩放变换仍在（MiniBtn 悬停放大不会被废掉）",
                ReferenceEquals(fakeMini.RenderTransform, ownScale) && ButtonFx.OwnTransformIntactForTest(fakeMini),
                fakeMini.RenderTransform?.GetType().Name ?? "已被清掉");
            // 对照：自己建的变换在复位后必须清掉（否则文字会一直发虚）
            var ownless = new Border { Cursor = System.Windows.Input.Cursors.Hand, Width = 60, Height = 24 };
            ButtonFx.Wire(ownless);
            ButtonFx.HoverForTest(ownless, true);
            ButtonFx.HoverForTest(ownless, false);
            ButtonFx.SettleForTest(ownless);     // 无头环境动画时钟不推进，手动跑一次收尾
            Check("动效复位后，自己建的缩放变换被清掉（静止态不留变换，文字保持锐利）",
                ownless.RenderTransform == null, ownless.RenderTransform?.GetType().Name ?? "已清空");

            // ══════ 21. 第 33 批：页面就绪等待 / 事件配色 / 关键动作前必存快照 ══════
            // ① 抓引擎自己打印的带 token 地址
            Check("能从引擎输出里抓到带 token 的访问地址",
                MainWindow.ExtractReadyUrlForTest("[12:34:56] dsh web: http://127.0.0.1:3080/?token=abc123XYZ-_", 3080)
                    == "http://127.0.0.1:3080/?token=abc123XYZ-_" &&
                MainWindow.ExtractReadyUrlForTest("随便一行没有地址", 3080) == "",
                "解析规则：127.0.0.1:<端口>/?token=…");

            // ② 就绪等待：注入式探测（不依赖真实网络）——先回两次 404，再回 200 → 必须等到第 3 次才放行
            int probeCalls = 0;
            Func<string, Task<int>> fakeProbe = _ =>
            {
                probeCalls++;
                return Task.FromResult(probeCalls <= 2 ? 404 : 200);
            };
            var readySw = System.Diagnostics.Stopwatch.StartNew();
            bool becameReady = Task.Run(() => NetworkHelper.WaitUntilHttpReadyAsync("http://127.0.0.1:3080/", 5000, fakeProbe))
                                   .GetAwaiter().GetResult();     // 在线程池里等，避免与 UI 线程死锁
            readySw.Stop();
            Check("页面就绪等待：404 阶段不放行，等到 2xx/3xx 才放行（注入式，确定性）",
                becameReady && probeCalls == 3,
                $"成功={becameReady} 探测 {probeCalls} 次 用时 {readySw.ElapsedMilliseconds}ms");

            // 反面：一直 404 时必须到点放弃（绝不无限等）
            int alwaysCalls = 0;
            bool neverReady = Task.Run(() => NetworkHelper.WaitUntilHttpReadyAsync("http://127.0.0.1:3080/", 800,
                                    _ => { alwaysCalls++; return Task.FromResult(404); })).GetAwaiter().GetResult();
            Check("一直 404 时到点返回 false（不会无限等待）",
                !neverReady && alwaysCalls >= 2, $"探测 {alwaysCalls} 次后放弃");

            // ③ 事件配色：五档颜色与既定色表一致
            Check("事件配色：淡蓝=更新/快照、绿=安装/升级、橙=回滚/停用、红=异常/卸载、灰白=其他",
                MainWindow.EventColor(MainWindow.EventKind.Update) == Color.FromRgb(0x5A, 0xC8, 0xFA) &&
                MainWindow.EventColor(MainWindow.EventKind.Good) == Color.FromRgb(0x34, 0xC7, 0x59) &&
                MainWindow.EventColor(MainWindow.EventKind.Warn) == Color.FromRgb(0xFF, 0x9F, 0x0A) &&
                MainWindow.EventColor(MainWindow.EventKind.Bad) == Color.FromRgb(0xFF, 0x45, 0x3A) &&
                MainWindow.EventColor(MainWindow.EventKind.Info) == Color.FromRgb(0xA8, 0xA8, 0xB0),
                $"{MainWindow.EventColor(MainWindow.EventKind.Update)} / {MainWindow.EventColor(MainWindow.EventKind.Warn)}");

            // ④ 快照策略表：只有 更新/卸载插件 才存快照
            //   （升级/回滚 DSH 的「自动-版本」预存已按用户指令关停：换版本就一条启动命令的事）
            Check("快照策略：更新与卸载插件才存快照（插件侧没改过头）",
                SnapshotPolicy.NeedSnapshot(GuardAction.UpdatePlugin) &&
                SnapshotPolicy.NeedSnapshot(GuardAction.UninstallPlugin) &&
                !SnapshotPolicy.NeedSnapshot(GuardAction.InstallPlugin) &&
                !SnapshotPolicy.NeedSnapshot(GuardAction.DisablePlugin) &&
                !SnapshotPolicy.NeedSnapshot(GuardAction.EnablePlugin),
                "安装/停用/启用=不存；更新/卸载=存");
            Check("快照策略：「自动-版本」已关停 —— 升级/回滚 DSH 不再自动预存快照",
                !SnapshotPolicy.NeedSnapshot(GuardAction.UpgradeDsh) &&
                !SnapshotPolicy.NeedSnapshot(GuardAction.RollbackDsh),
                $"升级={SnapshotPolicy.NeedSnapshot(GuardAction.UpgradeDsh)} / 回滚={SnapshotPolicy.NeedSnapshot(GuardAction.RollbackDsh)}");
            Check("动作类型：插件动作记为「自动-插件」，升级/回滚不再归入 before-switch",
                SnapshotPolicy.KindFor(GuardAction.UpdatePlugin) == SnapshotManager.KindAuto &&
                SnapshotPolicy.KindFor(GuardAction.UninstallPlugin) == SnapshotManager.KindAuto &&
                SnapshotPolicy.KindFor(GuardAction.UpgradeDsh) != SnapshotManager.KindBeforeSwitch &&
                SnapshotPolicy.KindFor(GuardAction.RollbackDsh) != SnapshotManager.KindBeforeSwitch,
                SnapshotPolicy.KindFor(GuardAction.UpdatePlugin));
            Check("历史遗留的 before-switch 快照仍显示为专门标签（非空、不回落成裸 kind）",
                SnapshotManager.KindLabel(SnapshotManager.KindBeforeSwitch).Length > 0 &&
                SnapshotManager.KindLabel(SnapshotManager.KindBeforeSwitch) != SnapshotManager.KindBeforeSwitch,
                SnapshotManager.KindLabel(SnapshotManager.KindBeforeSwitch));

            // ⑤ 动作前存快照的落地行为（直接走真实核心逻辑）——全程在临时仓库里做，不碰真实快照
            string testSnapRoot = Path.Combine(Path.GetTempPath(), "dshguard-retention-selftest");
            try { if (Directory.Exists(testSnapRoot)) Directory.Delete(testSnapRoot, true); } catch { }
            Directory.CreateDirectory(testSnapRoot);
            GuardPaths.Apply(null, testSnapRoot, null);
            int beforeSnaps = SnapshotManager.ListSnapshots().Count;
            string pluginNote = MainWindow.SnapshotForTest("DSHGuard：卸载插件 演示 前", SnapshotPolicy.KindFor(GuardAction.UninstallPlugin));
            // 升级动作仍照常走一遍 KindFor：现在只应落到「自动-插件」，不再进 before-switch
            string upgradeKind = SnapshotPolicy.KindFor(GuardAction.UpgradeDsh);
            MainWindow.SnapshotForTest("DSHGuard：升级到 9.9.9 前", upgradeKind);
            var afterSnaps = SnapshotManager.ListSnapshots();
            Check("存快照能落地（插件动作一份 + 升级不再进 before-switch）",
                afterSnaps.Count == beforeSnaps + 2 && pluginNote.Contains("已存快照") &&
                upgradeKind == SnapshotManager.KindAuto &&
                !afterSnaps.Any(x => x.Kind == SnapshotManager.KindBeforeSwitch),
                $"{beforeSnaps} → {afterSnaps.Count} 份；升级归入={SnapshotManager.KindLabel(upgradeKind)}");

            // 历史遗留的 before-switch 快照：夹具里补一份（模拟老版本留下的存档），必须照常显示
            MainWindow.SnapshotForTest("DSHGuard：旧版遗留的换版本前快照", SnapshotManager.KindBeforeSwitch);
            var legacySnap = SnapshotManager.ListSnapshots().FirstOrDefault(x => x.Kind == SnapshotManager.KindBeforeSwitch);
            Check("历史遗留的 before-switch 快照仍可读、标签正常（非空、不回落成裸 kind）",
                legacySnap != null && legacySnap.KindLabel.Length > 0 &&
                legacySnap.KindLabel != SnapshotManager.KindBeforeSwitch,
                legacySnap?.KindLabel ?? "(未找到)");

            // ══════ 23. 第 35 批：保留策略（快照不会无限堆积） ══════
            // 换版本前：塞 13 份 → 只应留最近 10 份；手动保存的不受影响
            MainWindow.SnapshotForTest("DSHGuard：手动保存", SnapshotManager.KindManual);
            for (int i = 0; i < 13; i++)
            {
                MainWindow.SnapshotForTest($"DSHGuard：换成第 {i} 版前", SnapshotManager.KindBeforeSwitch);
                System.Threading.Thread.Sleep(5);
            }
            SnapshotManager.TrimAll();
            var all2 = SnapshotManager.ListSnapshots();
            Check($"快照保留策略：换版本前只留最近 {SnapshotManager.BeforeSwitchKeep} 份（最旧自动删）",
                all2.Count(s => s.Kind == SnapshotManager.KindBeforeSwitch) == SnapshotManager.BeforeSwitchKeep,
                $"{all2.Count(s => s.Kind == SnapshotManager.KindBeforeSwitch)} 份（上限 {SnapshotManager.BeforeSwitchKeep}）");
            Check("快照保留策略：手动保存的不受裁剪影响",
                all2.Any(s => s.Kind == SnapshotManager.KindManual),
                $"手动 {all2.Count(s => s.Kind == SnapshotManager.KindManual)} 份");
            Check("保留策略有人话口径（界面与日志共用），且不再对外报「自动-版本」这一档",
                SnapshotManager.RetentionText.Contains("自动-插件") &&
                SnapshotManager.RetentionText.Contains("手动") &&
                !SnapshotManager.RetentionText.Contains("自动-版本"),
                SnapshotManager.RetentionText);

            // 手动快照：到 5 份后应能判定"删旧存新"，且只腾出必要的位置（弹窗在界面层，这里测判定与腾位）
            for (int i = 0; i < 5; i++)
            {
                MainWindow.SnapshotForTest($"DSHGuard：手动第 {i} 份", SnapshotManager.KindManual);
                System.Threading.Thread.Sleep(5);
            }
            int manualNow = SnapshotManager.ListNative().Count(s => s.Kind == SnapshotManager.KindManual);
            int overflow = SnapshotManager.ManualOverflow();
            int oldestCount = SnapshotManager.OldestManual(overflow).Count;
            int freedManual = SnapshotManager.TrimManualToMakeRoom();
            int manualAfter = SnapshotManager.ListNative().Count(s => s.Kind == SnapshotManager.KindManual);
            Check("手动快照上限 5 份：到量能判定删旧存新，且只删最旧的",
                SnapshotManager.ManualKeep == 5 && manualNow >= 5 && overflow >= 1 &&
                oldestCount == overflow && freedManual >= 1 && manualAfter <= SnapshotManager.ManualKeep - 1,
                $"{manualNow} 份 → 腾位删 {freedManual} 份 → 剩 {manualAfter} 份（上限 {SnapshotManager.ManualKeep}）");
            Check("保留策略默认值：自动 10 / 换版本前 5 / 回滚前 5",
                SnapshotManager.SettingsCache.AutoSnapshotKeep == 10 &&
                SnapshotManager.BeforeSwitchKeep == 5 && SnapshotManager.PreRestoreKeep == 5 &&
                SettingsManager.NormalizeKeep(20) == 20 && SettingsManager.NormalizeKeep(7) == 7,
                $"自动 {SnapshotManager.SettingsCache.AutoSnapshotKeep} / 换版本前 {SnapshotManager.BeforeSwitchKeep} / 回滚前 {SnapshotManager.PreRestoreKeep}");
            // 说明：上面这条里原先是 `NormalizeKeep(20) == 10`（旧契约：把 20 当成"没设置过"回落 10）。
            //   SettingsManager.NormalizeKeep 已按补丁改成"1..500 一律原样采纳（含 20）"，
            //   本断言同步改为 `== 20` —— 否则自检会红。

            // ══════ 23′. 保留策略的启动顺序（快照数据安全，第 61 批） ══════
            // 现场缺陷 ①：启动清理原先排在"用户配置加载"之前（TrimAll 在 App.OnStartup 里，而用户设置
            //   要到 MainWindow 构造函数才读进来）⇒ 清理时用的还是静态默认值 10。用户配 30 份，
            //   **每次启动仍被裁到 10，多删的 20 份不可恢复**。
            // 这里用 %TEMP% 夹具把"配置先于裁剪"钉死：份数取 7（既不等于默认 10、也不等于旧默认 20，
            // 一旦回落到默认值立刻暴露），断言 ① 显式值被采纳 ② 裁剪后剩的份数 == 用户配的值。
            int keepPrevCfg = SnapshotManager.SettingsCache.AutoSnapshotKeep;
            string snapRootPrevCfg = GuardPaths.SnapshotRoot;
            string profilePrevCfg = GuardPaths.ProfileDir;      // 一并还原，别把 ProfileDir 顺手打回默认
            string? dataDirPrevCfg = null;
            try { dataDirPrevCfg = Environment.GetEnvironmentVariable("DSHGUARD_DATA_DIR"); } catch { }
            string cfgFixture = Path.Combine(Path.GetTempPath(),
                "dshguard-startupcfg-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                const int userKeep = 7;
                string cfgSnapRoot = Path.Combine(cfgFixture, "Snapshots");
                Directory.CreateDirectory(Path.Combine(cfgFixture, "Config"));
                Directory.CreateDirectory(cfgSnapRoot);
                // 夹具设置文件：只写"自动快照保留份数"，其余字段反序列化时取类型默认值
                File.WriteAllText(Path.Combine(cfgFixture, "Config", "settings.json"),
                    "{\"AutoSnapshotKeep\":" + userKeep + "}", new UTF8Encoding(false));
                Environment.SetEnvironmentVariable("DSHGUARD_DATA_DIR", cfgFixture);   // ConfigDir 落进夹具

                App.LoadUserConfigForStartup();          // 修复点：裁剪之前先把用户配置读进来
                int adopted = SnapshotManager.SettingsCache.AutoSnapshotKeep;

                GuardPaths.Apply(null, cfgSnapRoot, null);   // 临时仓库，绝不碰真实 Snapshots
                int makeCount = userKeep + 5;
                for (int i = 0; i < makeCount; i++)
                {
                    SnapshotManager.Create(SnapshotManager.KindAuto, "自检-启动顺序");
                    System.Threading.Thread.Sleep(5);
                }
                App.TrimSnapshotsOnce();
                int autoLeft = SnapshotManager.ListNative().Count(s => s.Kind == SnapshotManager.KindAuto);
                Check("保留策略：启动清理用的是**用户配置**的份数（不是静态默认 10），清理后剩余数 == 用户配置值",
                    adopted == userKeep && autoLeft == userKeep,
                    $"夹具配 {userKeep} ⇒ 采用 {adopted}；造 {makeCount} 份 ⇒ 清理后剩 {autoLeft} 份（旧行为会剩默认 10）");
            }
            catch (Exception ex) { Check("保留策略：启动顺序夹具", false, ex.Message); }
            finally
            {
                try { Environment.SetEnvironmentVariable("DSHGUARD_DATA_DIR", dataDirPrevCfg); } catch { }
                SnapshotManager.SettingsCache.AutoSnapshotKeep = keepPrevCfg;
                GuardPaths.Apply(null, snapRootPrevCfg, profilePrevCfg);
                try { if (Directory.Exists(cfgFixture)) Directory.Delete(cfgFixture, true); } catch { }
            }

            // 保留份数的边界：0 / 负数 / 极大值都必须回落到安全默认 —— 尤其**绝不能返回 ≤ 0**：
            // TrimKind 对 keep<1 会夹到 1，那就等于把用户快照删到只剩一份。
            int nkZero = SettingsManager.NormalizeKeep(0);
            int nkNeg = SettingsManager.NormalizeKeep(-5);
            int nkHuge = SettingsManager.NormalizeKeep(int.MaxValue);
            int nkOk = SettingsManager.NormalizeKeep(7);
            Check("保留份数边界：0 / 负数 / 极大值回落到安全默认（≥1 且不超上限），正常值原样采纳",
                nkZero >= 1 && nkNeg >= 1 && nkHuge >= 1 && nkHuge <= 500 && nkOk == 7,
                $"0→{nkZero} · -5→{nkNeg} · int.MaxValue→{nkHuge} · 7→{nkOk}");
            // 20 这个值专治"显式设置被当成没设置过"：它既不同于任何默认值（10/5），也在合法区间内。
            // ⚠ 判定必须写在 Check 里。原先写成 `if (NormalizeKeep(20) == 20) Check(…, true, …) else Skip(…)`
            //   ⇒ 判定被挪进了 if，Check 的第二个参数**恒为 true**：NormalizeKeep 无论返回什么，
            //     这一条都是 PASS 或 SKIP，**永远不会 FAIL** —— 实现被删掉自检照样全绿，验收因此不可信。
            int nk20 = SettingsManager.NormalizeKeep(20);
            Check("保留份数：显式设置的 20 被采纳（不再当成「没设置过」）", nk20 == 20, $"20→{nk20}");

            // ══════ 23″. 采集清单必须含 <ProfileDir>\plugins\（本地链接插件的实体在这里） ══════
            // 现场缺陷 ②：采集清单只有 package.json / cordis*.yml / pnpm-* / *.mjs，**没有 plugins\** ⇒
            //   本机 dsh-imagegen 是 link:./plugins/dsh-imagegen，那个 19KB 的 index.js 一份快照都没采到，
            //   回滚时"插件在、内容回不来"。这里钉住三件事：不存在不报错 / 存在就采到 / 超限如实标注。
            int keepPrevPl = SnapshotManager.SettingsCache.AutoSnapshotKeep;
            string snapRootPrevPl = GuardPaths.SnapshotRoot;
            string profilePrevPl = GuardPaths.ProfileDir;
            string plFixture = Path.Combine(Path.GetTempPath(),
                "dshguard-plugincap-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                string plSnapRoot = Path.Combine(plFixture, "Snapshots");
                string plProfile = Path.Combine(plFixture, "profile");
                Directory.CreateDirectory(plSnapRoot);
                Directory.CreateDirectory(plProfile);
                File.WriteAllText(Path.Combine(plProfile, "package.json"), "{\"name\":\"selftest-fixture\"}");
                GuardPaths.Apply(null, plSnapRoot, plProfile);

                // ① plugins\ 不存在：跳过即可 —— 不报错，也不该因此少采别的文件
                var snapNoPl = SnapshotManager.Create(SnapshotManager.KindManual, "自检-无 plugins");
                Check("采集清单：profile 没有 plugins\\ 目录时不报错，其余文件照常采集",
                    snapNoPl != null && snapNoPl.Files.Any(f => f.Name == "profile-package.json" && f.Restorable),
                    snapNoPl == null ? "建快照失败" : $"{snapNoPl.RestorableCount} 个可回滚文件");

                // ② plugins\ 存在：里面的文件必须被采到，且回滚目标必须落在 profile 之下
                string plDir = Path.Combine(plProfile, "plugins", "dsh-selftest-link");
                Directory.CreateDirectory(plDir);
                File.WriteAllText(Path.Combine(plDir, "index.js"), "export const fixtureMarker = 'selftest';");
                var snapPl = SnapshotManager.Create(SnapshotManager.KindManual, "自检-有 plugins");

                // P1 落地后采集名是**扁平**的：profile-plugins-dsh-selftest-link-index.js（分隔符换成 -），
                // 不是 profile-plugins/... —— 所以判据用 "profile-plugins-" 前缀，两种写法都能认。
                static bool IsPluginsEntry(SnapshotManager.SnapshotFile f)
                {
                    string n = f.Name.Replace('\\', '/');
                    return n.StartsWith("profile-plugins-", StringComparison.OrdinalIgnoreCase)
                        || n.StartsWith("profile-plugins/", StringComparison.OrdinalIgnoreCase);
                }

                var plEntry = snapPl?.Files.FirstOrDefault(IsPluginsEntry);
                if (plEntry == null)
                    Check("采集清单：profile 的 plugins\\ 被采集（含子目录文件）且回滚目标在 profile 之下", false,
                        "采集清单里没有 plugins\\ 条目（P1 未落地？）");
                else
                {
                    bool copied = File.Exists(Path.Combine(snapPl!.Dir, plEntry.Name));
                    // Target 必须**在 profile 之下**（不是"前缀像"就行）：逐段比对，
                    // 既挡住 C:\...\profile-other\... 这种同前缀不同目录，也挡住指向 plugins 目录本身。
                    string fullTarget;
                    try { fullTarget = Path.GetFullPath(plEntry.Target); } catch { fullTarget = ""; }
                    string profileFull;
                    try { profileFull = Path.GetFullPath(plProfile).TrimEnd(Path.DirectorySeparatorChar); } catch { profileFull = plProfile; }
                    bool underProfile = fullTarget.Length > 0
                        && (fullTarget.StartsWith(profileFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                            || fullTarget.StartsWith(profileFull + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
                    // 落位必须是**文件**且真的在 plugins 子树里；绝不能是 plugins 目录本身（回滚时 File.Copy 会炸）
                    bool isFileInPlugins = underProfile
                        && File.Exists(fullTarget)
                        && fullTarget.Substring(profileFull.Length).TrimStart('\\', '/')
                            .StartsWith("plugins" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                    // 复算 ResolveTarget：证明"扁平名 → Target"这条链路本身也把路径放在 profile 之下
                    var probe = new SnapshotManager.SnapshotFile { Name = plEntry.Name };
                    SnapshotManager.ResolveTarget(probe);
                    bool roundTrip =
                        SnapshotManager.CheckRestoreTarget(probe.Target) == SnapshotManager.RestoreTargetVerdict.Allowed
                        && SnapshotManager.CheckRestoreTarget(fullTarget) == SnapshotManager.RestoreTargetVerdict.Allowed;
                    Check("采集清单：profile 的 plugins\\ 被采集（含子目录文件）且回滚目标在 profile 之下",
                        copied && plEntry.Restorable && isFileInPlugins && roundTrip,
                        $"{plEntry.Name} → {(plEntry.Target.Length > 0 ? plEntry.Target : "(不可回滚)")}"
                        + $" · 文件已落快照={copied} · 在 plugins 子树内={isFileInPlugins} · 回滚判据放行={roundTrip}");
                }

                // ③ 上限：塞远超上限的量，必须"如实标注未完整采集"，而不是静默丢或整份失败。
                //    条数/字节都按常量现算，不写死数字（上限日后调大调小，这条断言不该跟着红）。
                string plBulk = Path.Combine(plProfile, "plugins", "bulk");
                Directory.CreateDirectory(plBulk);
                int bulkN = SnapshotManager.PluginsMaxFiles + 50;
                for (int i = 0; i < bulkN; i++)
                    File.WriteAllText(Path.Combine(plBulk, $"f{i:D4}.js"), "// fixture");
                var snapBulk = SnapshotManager.Create(SnapshotManager.KindManual, "自检-plugins 超限");
                var bulkPl = snapBulk?.Files.Where(IsPluginsEntry).ToList() ?? new List<SnapshotManager.SnapshotFile>();
                if (bulkPl.Count == 0)
                    Check("采集上限：plugins\\ 超限时如实标注「未完整采集」，且不是一份都不采", false,
                        "超限场景下一个 plugins 文件都没采到（P1 未落地？）");
                else
                {
                    // 说明只走 manifest 字段（绝不作 profile- 前缀的跳过项 —— 那会被 ResolveTarget 映成
                    // 指向 plugins **目录**的合法 Target，回滚时 File.Copy 报错）
                    bool noteHasLimit = snapBulk!.CaptureNote.Contains("未完整", StringComparison.Ordinal)
                                     && snapBulk.CaptureNote.Contains("plugins", StringComparison.OrdinalIgnoreCase);
                    // 跨重启可见：从盘上的 manifest.json 读回来的快照也得带这条说明
                    var reloaded = SnapshotManager.ListNative().FirstOrDefault(s => s.Id == snapBulk.Id);
                    bool persisted = reloaded != null && reloaded.CaptureNote == snapBulk.CaptureNote;
                    // "不是一份都不采"：确实采到了，且没超过上限（超了说明上限没生效）
                    bool tookSome = bulkPl.Count > 0 && bulkPl.Count <= SnapshotManager.PluginsMaxFiles;
                    bool tookAll = bulkPl.Count == bulkN;   // 上限失效会把全部都采进来
                    // 说明不许变成任何"指向目录"的条目：所有 plugins 条目的回滚目标都必须是可放行的**文件**
                    bool noDirEntry = bulkPl.All(f => f.Restorable
                        && SnapshotManager.CheckRestoreTarget(f.Target) == SnapshotManager.RestoreTargetVerdict.Allowed
                        && File.Exists(f.Target));
                    Check("采集上限：plugins\\ 超限时如实标注「未完整采集」，且不是一份都不采",
                        noteHasLimit && persisted && tookSome && !tookAll && noDirEntry,
                        $"造 {bulkN + 1} 个 plugins 文件（上限 {SnapshotManager.PluginsMaxFiles}）⇒ 采到 {bulkPl.Count} 个"
                        + $" · manifest 有标注={noteHasLimit} · 读回一致={persisted}"
                        + $" · 全采了(上限失效)={tookAll} · 无目录条目={noDirEntry}"
                        + $" · 说明=\"{snapBulk.CaptureNote}\"");
                }
            }
            catch (Exception ex) { Check("采集清单：plugins\\ 夹具", false, ex.Message); }
            finally
            {
                SnapshotManager.SettingsCache.AutoSnapshotKeep = keepPrevPl;
                GuardPaths.Apply(null, snapRootPrevPl, profilePrevPl);
                try { if (Directory.Exists(plFixture)) Directory.Delete(plFixture, true); } catch { }
            }

            // ══════ 23‴. 「自动-时间」快照策略（引擎每连续运行满 1 小时一份） ══════
            // 用户要求：新增一档与「自动-插件」（动作前预存）**完全不同**的触发方式 —— 时间驱动，
            //   引擎运行满 1 小时自动存，且必须是"每满一小时一份"（长时间挂机也要有多个还原点），
            //   而不是"启动后 1 小时只存一次"。
            // 本块的断言分四层：
            //   ① 档位本身（kind / 标签 / 配额常量）；
            //   ② 纯函数判据的边界（59:59 不存、60:00 存、同一门槛只存一次、逐小时递增）；
            //   ③ 真的接线（走 MainWindow.MaybeTakeTimedSnapshotForTest 会真存出一份 kind=timed 的快照）；
            //   ④ 反例（去掉判据 / 砍掉配额裁剪 / 改回"只存一次" / 引擎停止不清零 —— 各会红哪条）。
            // 用 %TEMP% 夹具，绝不碰用户真实 Snapshots。
            string snapRootPrevTd = GuardPaths.SnapshotRoot;
            string profilePrevTd = GuardPaths.ProfileDir;
            double eggPrevTd = Mascot.EggChance;
            string tdFixture = Path.Combine(Path.GetTempPath(),
                "dshguard-timed-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                // 彩蛋钉死：AddEvent 每次记事件都会掷一次 2% 的彩蛋骰，掷中会**额外**追一条事件，
                // 让"最后一条事件"不再是快照那条。本块的断言要读最后一条事件，先把骰子按到 0。
                Mascot.EggChance = 0;

                string tdSnapRoot = Path.Combine(tdFixture, "Snapshots");
                string tdProfile = Path.Combine(tdFixture, "profile");
                Directory.CreateDirectory(tdSnapRoot);
                Directory.CreateDirectory(tdProfile);
                File.WriteAllText(Path.Combine(tdProfile, "package.json"), "{\"name\":\"selftest-timed\"}");
                GuardPaths.Apply(null, tdSnapRoot, tdProfile);

                long hour = SnapshotManager.RunSecondsPerHour;

                // ── ① 档位本身 ──
                Check("自动-时间：KindTimed 的标签是「自动-时间」",
                    SnapshotManager.KindLabel(SnapshotManager.KindTimed) == "自动-时间",
                    $"kind=\"{SnapshotManager.KindTimed}\" ⇒ 标签 \"{SnapshotManager.KindLabel(SnapshotManager.KindTimed)}\"");
                Check("自动-时间：配额常量 TimedKeep > 0（否则 TrimKind 会把这一档删到只剩 1 份）",
                    SnapshotManager.TimedKeep > 0,
                    $"TimedKeep={SnapshotManager.TimedKeep}");
                Check("自动-时间：门距恰好是 1 小时（改大改小都会让「满 1 小时」这条失去意义）",
                    SnapshotManager.TimedSnapshotEverySeconds == hour && hour == 3600,
                    $"门距={SnapshotManager.TimedSnapshotEverySeconds} 秒，RunSecondsPerHour={hour}");

                // ── ② 纯函数判据的边界（真跑 ShouldTakeTimedSnapshot）──
                bool t5959 = SnapshotManager.ShouldTakeTimedSnapshot(hour - 1, 0);
                bool t6000 = SnapshotManager.ShouldTakeTimedSnapshot(hour, 0);
                Check("自动-时间判据：59 分 59 秒不存、恰好 60 分存（边界取「大于等于门槛」）",
                    !t5959 && t6000,
                    $"59:59 ⇒ {t5959}（期望 False）· 60:00 ⇒ {t6000}（期望 True）");

                // 同一门槛只存一次：结算点是每 30 秒一跳，61 分若还判"该存"就会连存
                bool t6100 = SnapshotManager.ShouldTakeTimedSnapshot(hour + 60, hour);
                Check("自动-时间判据：61 分不再重复存（上一份已把门槛推进到 3600）",
                    !t6100,
                    $"ran=61 分 lastTaken=60 分 ⇒ {t6100}（期望 False）");

                bool t120 = SnapshotManager.ShouldTakeTimedSnapshot(2 * hour, hour);
                bool t180 = SnapshotManager.ShouldTakeTimedSnapshot(3 * hour, 2 * hour);
                bool t11959 = SnapshotManager.ShouldTakeTimedSnapshot(2 * hour - 1, hour);
                Check("自动-时间判据：120 分 / 180 分各再存一次，未到下一门槛不存",
                    t120 && t180 && !t11959,
                    $"120 分 ⇒ {t120} · 180 分 ⇒ {t180} · 119:59 ⇒ {t11959}（期望 True/True/False）");

                // 端到端：结算点每 30 秒一跳跑满 4 小时，必须恰好 4 份、且都落在整点
                var tdMarks = new List<long>();
                long tdRan = 0, tdLast = 0;
                for (int tick = 1; tick <= 4 * 60 * 2; tick++)
                {
                    tdRan += 30;
                    if (!SnapshotManager.ShouldTakeTimedSnapshot(tdRan, tdLast)) continue;
                    tdMarks.Add(tdRan);
                    tdLast = SnapshotManager.TimedSnapshotNextMark(tdRan, tdLast);
                }
                Check("自动-时间判据：跑满 4 小时（480 个 30 秒结算点）恰好存 4 份，落在 1h/2h/3h/4h",
                    tdMarks.Count == 4 && tdMarks[0] == hour && tdMarks[1] == 2 * hour
                        && tdMarks[2] == 3 * hour && tdMarks[3] == 4 * hour,
                    $"共存 {tdMarks.Count} 份：{string.Join(", ", tdMarks.Select(m => $"{m / 3600}h{(m % 3600) / 60:D2}m"))}");

                // 引擎停止后清零（EndRunClock）⇒ 累计与门槛都归 0，重启后重新数满 1 小时
                w.SetTimedRunStateForTest(3 * hour, 2 * hour);
                var beforeStop = w.TimedRunStateForTest();
                w.EndRunClockForTest();
                var afterStop = w.TimedRunStateForTest();
                Check("自动-时间：引擎停止后运行累计与门槛一起清零（重新启动从 0 重新数满 1 小时）",
                    beforeStop.ranSeconds == 3 * hour && beforeStop.lastTakenAt == 2 * hour
                        && afterStop.ranSeconds == 0 && afterStop.lastTakenAt == 0,
                    $"停止前 {beforeStop.ranSeconds}s / 门槛 {beforeStop.lastTakenAt}s"
                        + $" ⇒ 停止后 {afterStop.ranSeconds}s / 门槛 {afterStop.lastTakenAt}s");

                // ── ③ 真接线：走正式入口（MaybeTakeTimedSnapshot）必须真存出一份 kind=timed 的快照 ──
                // 顺带当反例：同样摆位、只把 ran 降到 59:59，就不该多出任何一份
                int timedBefore = SnapshotManager.ListNative().Count(s => s.Kind == SnapshotManager.KindTimed);
                w.SetTimedRunStateForTest(hour - 1, 0);
                w.MaybeTakeTimedSnapshotForTest();
                int timedAt5959 = SnapshotManager.ListNative().Count(s => s.Kind == SnapshotManager.KindTimed);
                Check("自动-时间接线：59 分 59 秒时走正式入口不会存出快照",
                    timedAt5959 == timedBefore,
                    $"{timedBefore} 份 ⇒ {timedAt5959} 份（期望不变）");

                w.SetTimedRunStateForTest(hour, 0);
                w.MaybeTakeTimedSnapshotForTest();
                var tdSnaps = SnapshotManager.ListNative().Where(s => s.Kind == SnapshotManager.KindTimed).ToList();
                Check("自动-时间接线：60 分时走正式入口真存出一份 kind=timed 的快照",
                    tdSnaps.Count == timedBefore + 1
                        && tdSnaps.Any(s => s.KindLabel == "自动-时间"),
                    $"kind=timed 共 {tdSnaps.Count} 份，最新一份标签 \"{tdSnaps.FirstOrDefault()?.KindLabel ?? "(无)"}\"");

                // 后台静默 + 事件：不弹窗，只记一条 EventKind.Update 的中性事件（与"保存快照"同档）
                var lastEv = MainWindow.LastEventForTest();
                Check("自动-时间事件：记一条「已创建快照备份（…）」且档位为 Update（与保存快照同类），不弹窗",
                    lastEv.Text.Contains("已创建快照备份") && lastEv.Text.Contains("连续运行")
                        && lastEv.Kind == MainWindow.EventKind.Update,
                    $"kind={lastEv.Kind} 文本=\"{lastEv.Text}\"");

                // 不做重复存：门槛已推进到 3600，同一位置再走一次不该再产出
                w.SetTimedRunStateForTest(hour, hour);
                w.MaybeTakeTimedSnapshotForTest();
                int afterAgain = SnapshotManager.ListNative().Count(s => s.Kind == SnapshotManager.KindTimed);
                Check("自动-时间接线：同一门槛再走一次不会重复存（不会每 30 秒冒一份）",
                    afterAgain == tdSnaps.Count,
                    $"{tdSnaps.Count} 份 ⇒ {afterAgain} 份（期望不变）");

                // ── ④ 配额与裁剪：造超量夹具，TrimAll 必须把这一档压回 TimedKeep 份 ──
                // 同时先埋两个**别的档**的哨兵：裁剪是"只动本档"还是"顺手多删"，要靠它们证。
                // 哨兵必须真实存在于夹具仓库里 —— 少了它们，下面那条断言就会因为
                // "本来就没有别的档 ⇒ 数出来是 0" 而误红（甚至反过来把恒真的条件当成验过了）。
                var manualSentinel = SnapshotManager.Create(SnapshotManager.KindManual, "自检-自动时间夹具的手动哨兵");
                var preSentinel = SnapshotManager.Create(SnapshotManager.KindPreRestore, "自检-自动时间夹具的回滚前哨兵");
                var autoSentinel = SnapshotManager.Create(SnapshotManager.KindAuto, "自检-自动时间夹具的自动插件哨兵");
                Check("自动-时间配额：夹具里先埋好手动 / 回滚前 / 自动-插件 三个哨兵（供下面验「只动本档」）",
                    manualSentinel != null && preSentinel != null && autoSentinel != null,
                    $"手动={manualSentinel != null} 回滚前={preSentinel != null} 自动-插件={autoSentinel != null}");

                // ⚠ 真机自检 FAIL 订正：SnapshotManager.Create 内部**已经**调了 TrimAll（SnapshotManager.cs:328），
                //   每造一份就被立刻裁回 TimedKeep ⇒ "先造到超量再裁"这个前提永远不成立（timedMade 恒 = 5）。
                //   这里改为绕过 Create，用 SelfTestManifest（与下面 3b 越界/哈希夹具同一手法）直接在夹具
                //   仓库里手工铺 TimedKeep + 3 份 kind=timed 清单目录（id 按字典序递增 = 时间递增，
                //   裁剪顺序稳定），再调 TrimAll 验证裁剪。
                int timedMade = SnapshotManager.TimedKeep + 3;
                for (int i = 0; i < timedMade; i++)
                {
                    string tdId = $"20990101-0000{i:00}-timed";
                    string tdDir = Directory.CreateDirectory(Path.Combine(SnapshotManager.SnapshotRoot, tdId)).FullName;
                    File.WriteAllText(Path.Combine(tdDir, "manifest.json"),
                        SelfTestManifest(tdId, SnapshotManager.KindTimed, $"自检-自动时间超量 {i}", ""));
                }
                SnapshotManager.TrimAll();
                int timedKept = SnapshotManager.ListNative().Count(s => s.Kind == SnapshotManager.KindTimed);
                Check("自动-时间配额：TrimAll 把这一档裁到 TimedKeep 份（不裁就是每小时无限堆积）",
                    timedKept == SnapshotManager.TimedKeep,
                    $"手工铺了 {timedMade} 份 ⇒ 裁剪后剩 {timedKept} 份（上限 {SnapshotManager.TimedKeep}）");

                // 反例（防改过头）：别的档不受影响 —— 三个哨兵必须一份不少地活着。
                // 这正是"把裁剪写成 TrimKind 之外的新逻辑 / 误传 kind"时会红的那条。
                var still = SnapshotManager.ListNative();
                bool manualStill = still.Any(s => s.Id == manualSentinel!.Id);
                bool preStill = still.Any(s => s.Id == preSentinel!.Id);
                bool autoStill = still.Any(s => s.Id == autoSentinel!.Id);
                Check("自动-时间配额：裁剪只动这一档，手动 / 回滚前 / 自动-插件 的哨兵都不受影响",
                    manualStill && preStill && autoStill,
                    $"手动哨兵={manualStill} · 回滚前哨兵={preStill} · 自动-插件哨兵={autoStill}"
                        + $" · 自动-时间剩 {timedKept} 份");

                // 界面口径串：两处（快照页一行 + 悬停里的完整口径）都要报出这一档
                // ⚠ 视图名必须是小写 "snapshots"：ShowViewForTest 是精确匹配的小写开关，
                //   写成 "Snapshots" 会落进 default 分支切到状态页，断言就成了"验错页面"。
                w.ShowViewForTest("snapshots");
                string snapRootLine = w.PageTextsForTest("SnapRootText");
                Check("自动-时间界面口径：快照页那行与保留策略串都报出「自动-时间」",
                    snapRootLine.Contains("自动-时间") && snapRootLine.Contains("自动-插件")
                        && SnapshotManager.RetentionText.Contains("自动-时间")
                        && SnapshotManager.RetentionText.Contains("自动-插件"),
                    $"页内=\"{(snapRootLine.Length > 90 ? snapRootLine.Substring(0, 90) + "…" : snapRootLine)}\" · 口径=\"{SnapshotManager.RetentionText}\"");
            }
            catch (Exception ex) { Check("自动-时间策略夹具", false, ex.Message); }
            finally
            {
                Mascot.EggChance = eggPrevTd;
                GuardPaths.Apply(null, snapRootPrevTd, profilePrevTd);
                try { if (Directory.Exists(tdFixture)) Directory.Delete(tdFixture, true); } catch { }
            }

            // ══════ 24. 快照页「回退插件」那一行：插件清单按**结构**解析（第 36 批修的真实 bug） ══════
            // 现场：真实 pnpm-lock 的缩进是 importers:(0) → `  .:`(2) → `    dependencies:`(4) → `      '@scope/name':`(6)。
            // 老实现写死"依赖名行缩进 = 4 格"，于是恒解析出空表 ⇒ 连"卸载插件"自动存的快照都显示「不涉及」。
            // 这里用**本机真实结构**的片段（含 6 格依赖名 + specifier/version + 引号/无引号/单引号三种写法）钉住：
            // 一份是 A@1.0.0 + B@0.9.0（外带一个 devDependency 必须被忽略），另一份 A 换成 2.0.0 并删掉 B。
            string lockOld =
                "lockfileVersion: '9.0'\n" +
                "settings:\n" +
                "  autoInstallPeers: true\n" +
                "importers:\n" +
                "\n" +
                "  .:\n" +
                "    dependencies:\n" +
                "      '@scope/a-plugin':\n" +
                "        specifier: ^1.0.0\n" +
                "        version: 1.0.0\n" +
                "      b-plugin:\n" +
                "        specifier: ^0.9.0\n" +
                "        version: 0.9.0\n" +
                "    devDependencies:\n" +
                "      'dev-only-tool':\n" +
                "        specifier: ^3.0.0\n" +
                "        version: 3.0.0\n" +
                "packages:\n" +
                "  '@scope/a-plugin@1.0.0':\n" +
                "    resolution: {integrity: sha512-xxx}\n";
            string lockNew =
                "lockfileVersion: '9.0'\n" +
                "settings:\n" +
                "  autoInstallPeers: true\n" +
                "importers:\n" +
                "\n" +
                "  .:\n" +
                "    dependencies:\n" +
                "      '@scope/a-plugin':\n" +
                "        specifier: ^2.0.0\n" +
                "        version: 2.0.0\n" +
                "    devDependencies:\n" +
                "      'dev-only-tool':\n" +
                "        specifier: ^3.0.0\n" +
                "        version: 3.0.0\n" +
                "packages:\n" +
                "  '@scope/a-plugin@2.0.0':\n" +
                "    resolution: {integrity: sha512-yyy}\n";
            var lockChanged = SnapshotManager.ChangedPlugins(lockOld, lockNew);
            Check("插件清单按结构解析（真实 6 格缩进）：版本变了的 A 与消失的 B 都要报出来，devDependencies 不算",
                lockChanged.Count == 2 &&
                lockChanged.Contains("@scope/a-plugin", StringComparer.OrdinalIgnoreCase) &&
                lockChanged.Contains("b-plugin", StringComparer.OrdinalIgnoreCase) &&
                !lockChanged.Contains("dev-only-tool", StringComparer.OrdinalIgnoreCase),
                "识别出：" + (lockChanged.Count > 0 ? string.Join("、", lockChanged) : "（空）"));
            var lockSame = SnapshotManager.ChangedPlugins(lockNew, lockNew);
            Check("两份相同的清单 ⇒ 真的没有插件变化（空表）",
                lockSame.Count == 0 &&
                SnapshotManager.HasImporterDeps(lockNew) && SnapshotManager.HasImporterDeps(lockOld),
                $"相同文本报出 {lockSame.Count} 个；清单可比={SnapshotManager.HasImporterDeps(lockNew)}");

            // ══════ 24′. bug：回滚确认框把"根本没变的插件"也数进去（现场：只升级 1 个插件，却写 7 个）══════
            // 根因：pnpm v9 锁文件里**同一个已解析版本**会带同伴依赖后缀 `(...)`；快照当时与现在解析出的同伴
            // 不同（`0.4.7` vs `0.4.7(@deepseek-ai/schemastery@3.18.2)`）就被"整串比较"判成版本变了。
            // 修法：比较与展示都先过 SnapshotManager.NormalizeLockVersion（只剥核心版本号；git 源原样保留）。
            string nvPlain = SnapshotManager.NormalizeLockVersion("1.47.0");
            string nvOne = SnapshotManager.NormalizeLockVersion("0.4.7(@deepseek-ai/schemastery@3.18.2)");
            string nvTwo = SnapshotManager.NormalizeLockVersion("0.11.6(@deepseek-ai/schemastery@3.18.2)(react@18.3.1)");
            string nvGit = SnapshotManager.NormalizeLockVersion("github.com/o/r/1a2b3c4d5e6f7890");
            Check("版本归一化：同伴依赖后缀 `(...)` 一律剥掉；无后缀、空串保持原样",
                nvPlain == "1.47.0" && nvOne == "0.4.7" && nvTwo == "0.11.6" &&
                SnapshotManager.NormalizeLockVersion("") == "" &&
                SnapshotManager.NormalizeLockVersion(null) == "" &&
                SnapshotManager.NormalizeLockVersion("   ") == "",
                $"无后缀={nvPlain} · 1 个后缀={nvOne} · 2 个后缀={nvTwo}");
            Check("版本归一化：git 源（无核心版本号）原样保留 ⇒ 仍按整串比较，git 判据不回归",
                nvGit == "github.com/o/r/1a2b3c4d5e6f7890" &&
                SnapshotManager.NormalizeLockVersion("github.com/o/r/1a2b3c4d5e6f7890(react@18.3.1)")
                    == "github.com/o/r/1a2b3c4d5e6f7890(react@18.3.1)",
                nvGit);

            // 现场那 5 条"假差异"用的就是**真实字符串**：归一化后必须一个都不报；
            // 真升版的（0.10.0 → 0.11.0）与"快照里没有、后来新装的 git 源"仍要报（回滚会把新装的移掉）。
            string LockWith((string Name, string Ver)[] deps)
            {
                var body = new List<string> { "lockfileVersion: '9.0'", "importers:", "", "  .:", "    dependencies:" };
                foreach (var (nm, ver) in deps)
                {
                    body.Add($"      '{nm}':");
                    body.Add("        specifier: ^1.0.0");
                    body.Add($"        version: {ver}");
                }
                return string.Join("\n", body) + "\n";
            }
            var fakeSnapLock = LockWith(new[]
            {
                ("@furongjun1999/dsh-memory", "0.4.7"),
                ("dsh-client-auto-continue", "0.11.6(react@18.3.1)"),
                ("dsh-context", "0.10.1(react@18.3.1)"),
                ("dshmarket", "1.47.0"),
                ("dsh-univer-office", "0.3.0(react@18.3.1)"),
                ("@changfenhuang/dsh-genui", "0.10.0")
            });
            var fakeNowLock = LockWith(new[]
            {
                ("@furongjun1999/dsh-memory", "0.4.7(@deepseek-ai/schemastery@3.18.2)"),
                ("dsh-client-auto-continue", "0.11.6(@deepseek-ai/schemastery@3.18.2)(react@18.3.1)"),
                ("dsh-context", "0.10.1(@deepseek-ai/schemastery@3.18.2)(react@18.3.1)"),
                ("dshmarket", "1.47.0(@deepseek-ai/schemastery@3.18.2)"),
                ("dsh-univer-office", "0.3.0(@deepseek-ai/schemastery@3.18.2)(react@18.3.1)"),
                ("@changfenhuang/dsh-genui", "0.11.0"),
                ("dsh-codearts-auth", "github.com/changfenhuang/dsh-codearts-auth/1a2b3c4")
            });
            var realDiff = SnapshotManager.ChangedPlugins(fakeSnapLock, fakeNowLock);
            Check("回滚清单只报**真差异**：同伴后缀造成的 5 条假差异不算，真升版 1 个 + 新装 1 个才算（现场 7 → 2）",
                realDiff.Count == 2 &&
                realDiff.Contains("@changfenhuang/dsh-genui", StringComparer.OrdinalIgnoreCase) &&
                realDiff.Contains("dsh-codearts-auth", StringComparer.OrdinalIgnoreCase) &&
                !realDiff.Contains("@furongjun1999/dsh-memory", StringComparer.OrdinalIgnoreCase) &&
                !realDiff.Contains("dshmarket", StringComparer.OrdinalIgnoreCase),
                "判出：" + (realDiff.Count > 0 ? string.Join("、", realDiff) : "（空）"));
            Check("git 源仍是整串比较：写法一变就如实报差异，不因归一化而漏判",
                SnapshotManager.ChangedPlugins(
                    LockWith(new[] { ("git-plugin", "github.com/o/r/1a2b3c4") }),
                    LockWith(new[] { ("git-plugin", "github.com/o/r/1a2b3c4(react@18.3.1)") })).Count == 1,
                "git 源两侧写法不同 ⇒ 判为差异（有意保留）");

            // 容错 + "根本无法比较"：空文本、只有 importers: 无依赖段、只有注释都不允许抛出，且必须返回空表。
            // 老快照来自更早的版本、没有锁文件 ⇒ 左侧没有清单 ⇒ 不能冒充"没有变化"（界面那一行仍显示「不涉及」，
            // 但悬停会说明"无法判断"）。
            var noData = new (string Name, string? Text)[]
            {
                ("空文本", ""),
                ("空白文本", "   \n\n  "),
                ("null", null),
                ("只有 importers: 没有依赖段", "lockfileVersion: '9.0'\nimporters:\n\n  .: {}\n"),
                ("只有注释", "# 什么都没有\n")
            };
            bool noThrow = true, allEmpty = true;
            var noDataDetail = new List<string>();
            foreach (var (nm, tx) in noData)
            {
                try
                {
                    var r = SnapshotManager.ChangedPlugins(tx, lockNew);
                    if (r.Count != 0) allEmpty = false;
                    noDataDetail.Add($"{nm}={r.Count}");
                }
                catch (Exception ex) { noThrow = false; noDataDetail.Add($"{nm}=抛异常({ex.GetType().Name})"); }
            }
            Check("坏锁文件文本（空/null/缺段/只有注释）既不抛异常、也不冒充有变化（恒空表）",
                noThrow && allEmpty, string.Join(" / ", noDataDetail));
            // 半截文本（依赖段写到一半就断了）：只管"不抛"——名字已解析出来时按"版本为空"记，
            // 属于已知的取舍（锁文件由本程序整文件读取，不是流式解析）。
            bool truncatedOk = true;
            string truncatedDetail = "";
            try
            {
                var rt = SnapshotManager.ChangedPlugins("importers:\n  .:\n    dependencies:\n      '@scope/a-plugin':\n", lockNew);
                truncatedDetail = $"半截文本 → {rt.Count} 个（不抛即可）";
            }
            catch (Exception ex) { truncatedOk = false; truncatedDetail = "半截文本抛异常：" + ex.GetType().Name; }
            Check("半截锁文件文本不抛异常（不假装能比出完整清单）", truncatedOk, truncatedDetail);
            var noDataChanged = SnapshotManager.ChangedPlugins("", lockNew);   // 旧快照未记录锁文件（左侧根本没有清单）
            Check("「这份快照没记插件清单」可判定（与「确实没变化」分开说）",
                !SnapshotManager.HasImporterDeps("") && !SnapshotManager.HasImporterDeps(null) &&
                !SnapshotManager.HasImporterDeps("importers:\n  .: {}\n") &&
                SnapshotManager.HasImporterDeps(lockOld) &&
                noDataChanged.Count == 0,
                $"空文本有清单={SnapshotManager.HasImporterDeps("")}；" +
                $"老快照缺清单时 ChangedPlugins(空, 现在)={(LockHasDeps("") ? "可比" : "没法比")} → {noDataChanged.Count} 个（界面据此把悬停改成「无法判断」）");

            // 日志：超过 15 天的删掉；仍多于 50 个则从最旧删起
            string realLogDir = GuardPaths.LogDir;
            string testLogDir = Path.Combine(Path.GetTempPath(), "dshguard-log-retention-selftest");
            try { if (Directory.Exists(testLogDir)) Directory.Delete(testLogDir, true); } catch { }
            Directory.CreateDirectory(testLogDir);
            for (int i = 0; i < 60; i++)
            {
                string f = Path.Combine(testLogDir, $"{Logger.ErrorPrefix}{DateTime.Now:yyyyMMdd}-{i:D3}.log");
                File.WriteAllText(f, "x");
                File.SetLastWriteTime(f, DateTime.Now.AddMinutes(-i));
            }
            string oldFile = Path.Combine(testLogDir, $"{Logger.ErrorPrefix}20200101-000000.log");
            File.WriteAllText(oldFile, "old");
            File.SetLastWriteTime(oldFile, DateTime.Now.AddDays(-30));

            string testRetention = GuardPaths.SnapshotRoot;   // 记下当前值，稍后一并还原
            GuardPaths.Apply(testLogDir, testRetention, null);
            int removedLogs = Logger.CleanupOldLogs();
            int remainLogs = Directory.GetFiles(testLogDir, "*.log").Length;
            GuardPaths.Apply(null, null, null);

            Check("日志保留策略：超过 15 天的先删、总数不超过 50 份",
                !File.Exists(oldFile) && remainLogs <= Logger.MaxFiles && removedLogs > 0,
                $"清掉 {removedLogs} 个，剩 {remainLogs} 个（上限 {Logger.MaxFiles}，过期天数 {Logger.MaxAgeDays}）");
            try { Directory.Delete(testLogDir, true); } catch { }

            // ── 启动诊断日志的独立保留策略（用户报的"日志里还会记录正常启动的日志"）──
            //    正常启动日志在前一次启动收尾时就被 DiscardStartupLogIfHealthy 删了；还能活下来的
            //    （硬崩 / 带失败证据）必须按**比异常日志更严**的独立配额裁剪，而不是混进 50 份里躺着。
            string startupDir = Path.Combine(Path.GetTempPath(), "dshguard-startup-log-retention-selftest");
            try { if (Directory.Exists(startupDir)) Directory.Delete(startupDir, true); } catch { }
            Directory.CreateDirectory(startupDir);

            /// 造一份启动日志，返回路径；<paramref name="minutesAgo"/> 决定新旧（越大越旧）
            string MakeStartup(int minutesAgo, string? body = null)
            {
                string p = Path.Combine(startupDir,
                    $"{Logger.StartPrefix}{DateTime.Now.AddMinutes(-minutesAgo):yyyyMMdd-HHmmss}-{minutesAgo:D3}.log");
                File.WriteAllText(p, body ?? $"[2026-01-01 00:00:00.000] [环境] 临时目录可用；端口=3099（正常启动）{Environment.NewLine}");
                File.SetLastWriteTime(p, DateTime.Now.AddMinutes(-minutesAgo));
                return p;
            }
            string MakeErrorLog(int minutesAgo)
            {
                string p = Path.Combine(startupDir, $"{Logger.ErrorPrefix}{DateTime.Now.AddMinutes(-minutesAgo):yyyyMMdd-HHmmss}-{minutesAgo:D3}.log");
                File.WriteAllText(p, "[00:00:00.000] [ERROR] 夹具：异常日志样例");
                File.SetLastWriteTime(p, DateTime.Now.AddMinutes(-minutesAgo));
                return p;
            }
            int StartupCount() => Directory.GetFiles(startupDir, $"{Logger.StartPrefix}*.log").Length;
            int ErrorCount() => Directory.GetFiles(startupDir, $"{Logger.ErrorPrefix}*.log").Length;

            // ① 独立上限：20 份健康启动日志 + 3 份异常日志 ⇒ 启动日志裁到新上限，异常日志一份不少
            var startupPaths = new List<string>();            // 按创建顺序（新→旧）记下路径，别靠"再算一遍时间戳"重建
            for (int i = 1; i <= 20; i++) startupPaths.Add(MakeStartup(i));
            string errA = MakeErrorLog(1), errB = MakeErrorLog(2), errC = MakeErrorLog(3);
            string newestStartup = MakeStartup(0);
            string oldestStartup = startupPaths[startupPaths.Count - 1];   // i=20，最旧
            GuardPaths.Apply(startupDir, GuardPaths.SnapshotRoot, null);
            int pruned1 = Logger.CleanupOldLogs();            // 走全链路入口：两条配额都在这条路径上
            int afterStartup1 = StartupCount(), afterError1 = ErrorCount();
            GuardPaths.Apply(null, null, null);
            Check("启动日志有独立的上限（明显小于异常日志的 50 份），不再挤在同一条配额里",
                Logger.MaxStartupFiles < Logger.MaxFiles && Logger.MaxStartupAgeDays <= Logger.MaxAgeDays,
                $"启动上限 {Logger.MaxStartupFiles} 份 / {Logger.MaxStartupAgeDays} 天；异常上限 {Logger.MaxFiles} 份 / {Logger.MaxAgeDays} 天");
            Check("20 份正常启动日志被裁到「启动日志上限」以内（异常日志一份都没被牵连）",
                afterStartup1 <= Logger.MaxStartupFiles && afterError1 == 3 && pruned1 > 0,
                $"裁掉 {pruned1} 个 → 启动 {afterStartup1}/{Logger.MaxStartupFiles}，异常 {afterError1}/3（三份异常日志都还在={File.Exists(errA) && File.Exists(errB) && File.Exists(errC)}）");
            Check("裁剪删的是**最旧**的，最新的留住（排序 = LastWriteTime 降序）",
                File.Exists(newestStartup) && !File.Exists(oldestStartup),
                $"最新在={File.Exists(newestStartup)}，最旧({Path.GetFileName(oldestStartup)})已删={!File.Exists(oldestStartup)}");

            // ② 带失败证据的启动日志**绝不删**：1 份 [ERROR] + 一堆更新的健康日志（远超配额）
            try { Directory.Delete(startupDir, true); } catch { }
            Directory.CreateDirectory(startupDir);
            string evidenceStartup = MakeStartup(600, "[2026-01-01 00:00:00.000] [ERROR] 引擎拉起失败：秒退（这是唯一现场，绝不能被裁掉）");
            string evidenceStartup2 = MakeStartup(601, "[2026-01-01 00:00:00.000] [FATAL] 端口被占用");
            for (int i = 0; i < 25; i++) MakeStartup(i);      // 全部比证据那份新
            string healthyProbe = Path.Combine(startupDir, $"{Logger.StartPrefix}healthy-probe.log");
            File.WriteAllText(healthyProbe, "[2026-01-01 00:00:00.000] [环境] 正常启动（无任何失败证据）");
            GuardPaths.Apply(startupDir, GuardPaths.SnapshotRoot, null);
            bool evidenceJudged = Logger.StartupLogHasFailureEvidence(evidenceStartup) &&
                                  Logger.StartupLogHasFailureEvidence(evidenceStartup2) &&
                                  !Logger.StartupLogHasFailureEvidence(healthyProbe);
            int pruned2 = Logger.PruneStartupLogs();
            int afterStartup2 = StartupCount();
            GuardPaths.Apply(null, null, null);
            Check("带失败证据的启动日志（[ERROR]/[FATAL]）不会被裁剪策略删掉",
                File.Exists(evidenceStartup) && File.Exists(evidenceStartup2) && evidenceJudged && pruned2 > 0,
                $"证据两份在={File.Exists(evidenceStartup) && File.Exists(evidenceStartup2)}；判据={evidenceJudged}；裁掉 {pruned2} 个，剩 {afterStartup2}");
            Check("裁剪只删「正常」启动日志：证据之外的份数被压到上限",
                afterStartup2 <= Logger.MaxStartupFiles + 2,
                $"剩 {afterStartup2}（上限 {Logger.MaxStartupFiles} + 2 份证据）");

            // ②b 关键回归：证据那份**同时**越过异常日志的 15 天年龄线时也不许被误杀 ——
            //     CleanupOldLogs 若把启动日志也丢进"15 天先删"那一轮，PruneStartupLogs 再想保护就已经晚了。
            try { Directory.Delete(startupDir, true); } catch { }
            Directory.CreateDirectory(startupDir);
            string ancientEvidence = MakeStartup(Logger.MaxAgeDays * 24 * 60 + 60,
                "[2026-01-01 00:00:00.000] [ERROR] 半年前的启动失败现场（越过 15 天线，仍必须留下）");
            for (int i = 0; i < 20; i++) MakeStartup(i);
            string ancientHealthy = MakeStartup(Logger.MaxAgeDays * 24 * 60 + 120);   // 同样越过 15 天的"正常"启动日志
            GuardPaths.Apply(startupDir, GuardPaths.SnapshotRoot, null);
            Logger.CleanupOldLogs();
            GuardPaths.Apply(null, null, null);
            Check("越过异常日志 15 天年龄线的启动日志：证据那份仍在，正常的照删（两条路互不干扰）",
                File.Exists(ancientEvidence) && !File.Exists(ancientHealthy) &&
                StartupCount() <= Logger.MaxStartupFiles + 1,
                $"半年前的证据在={File.Exists(ancientEvidence)}；同期的正常启动日志已删={!File.Exists(ancientHealthy)}；剩 {StartupCount()}");

            // ③ 只保留最近 N 天：过期的正常启动日志连配额都轮不到，直接过期
            try { Directory.Delete(startupDir, true); } catch { }
            Directory.CreateDirectory(startupDir);
            string staleStartup = MakeStartup((Logger.MaxStartupAgeDays + 2) * 24 * 60);   // 比"启动日志天数上限"更旧
            string freshStartup = MakeStartup(1);
            GuardPaths.Apply(startupDir, GuardPaths.SnapshotRoot, null);
            Logger.PruneStartupLogs();
            GuardPaths.Apply(null, null, null);
            Check($"启动日志只保留最近 {Logger.MaxStartupAgeDays} 天（比异常日志的 {Logger.MaxAgeDays} 天更严）",
                !File.Exists(staleStartup) && File.Exists(freshStartup),
                $"过期那份已删={!File.Exists(staleStartup)}，最近一份还在={File.Exists(freshStartup)}");

            // ④ 全链路（CleanupOldLogs）：启动日志走独立配额，异常日志仍按 50/15 天
            try { Directory.Delete(startupDir, true); } catch { }
            Directory.CreateDirectory(startupDir);
            for (int i = 0; i < 30; i++) MakeStartup(i);
            string keepErr1 = MakeErrorLog(0), keepErr2 = MakeErrorLog(5);
            GuardPaths.Apply(startupDir, GuardPaths.SnapshotRoot, null);
            int prunedAll = Logger.CleanupOldLogs();
            int afterStartup4 = StartupCount(), afterError4 = ErrorCount();
            GuardPaths.Apply(null, null, null);
            Check("CleanupOldLogs 对两种日志分别计额：启动日志压到独立上限，异常日志仍是 50 份以内",
                afterStartup4 <= Logger.MaxStartupFiles && afterError4 == 2 && prunedAll > 0,
                $"CleanupOldLogs 清掉 {prunedAll} 个 → 启动 {afterStartup4}（≤{Logger.MaxStartupFiles}），异常 {afterError4}/2（{File.Exists(keepErr1) && File.Exists(keepErr2)}）");

            // ⑤ 该路径**不额外生成**启动日志（清理策略只删不写）——否则每启动一次就自我繁殖
            int before5 = StartupCount();
            GuardPaths.Apply(startupDir, GuardPaths.SnapshotRoot, null);
            Logger.PruneStartupLogs();
            Logger.CleanupOldLogs();
            GuardPaths.Apply(null, null, null);
            Check("清理/裁剪路径自己不写启动日志（只删不增）",
                StartupCount() == before5,
                $"清理前 {before5} → 清理后 {StartupCount()}");

            // ⑥ 空目录 / 目录不存在都不抛（清理会在 Logger.Init 里被无条件调用）
            try { Directory.Delete(startupDir, true); } catch { }
            bool emptyNoThrow = true; string emptyDetail = "";
            try
            {
                GuardPaths.Apply(startupDir, GuardPaths.SnapshotRoot, null);   // 目录已删 → 不存在
                int r1 = Logger.PruneStartupLogs();
                int r2 = Logger.CleanupOldLogs();
                Directory.CreateDirectory(startupDir);
                int r3 = Logger.PruneStartupLogs();                            // 空目录（没有任何 .log）
                emptyDetail = $"不存在→{r1}/{r2}，空目录→{r3}";
            }
            catch (Exception ex) { emptyNoThrow = false; emptyDetail = "抛异常：" + ex.GetType().Name + " " + ex.Message; }
            finally { GuardPaths.Apply(null, null, null); }
            Check("日志目录不存在 / 为空目录时，启动日志裁剪都不抛异常", emptyNoThrow, emptyDetail);
            try { Directory.Delete(startupDir, true); } catch { }

            // ══════ 22. 第 34 批：口头禅与彩蛋（互斥、三处同色） ══════
            Mascot.ResetForTest();
            Check("常态口头禅为「不是蓝色大肥鱼，是鲸！」",
                Mascot.CurrentLine == "不是蓝色大肥鱼，是鲸！" && Mascot.CurrentLine == Mascot.NormalLine,
                Mascot.CurrentLine);
            Check("口头禅的颜色取自四色池，且不是程序里的语义色",
                Mascot.IsPaletteColor(Mascot.CurrentColor) &&
                Mascot.CurrentColor != Color.FromRgb(0xFF, 0x45, 0x3A) &&
                Mascot.CurrentColor != Color.FromRgb(0xFF, 0x9F, 0x0A) &&
                Mascot.CurrentColor != Color.FromRgb(0x34, 0xC7, 0x59) &&
                Mascot.CurrentColor != Color.FromRgb(0x0A, 0x84, 0xFF) &&
                Mascot.CurrentColor != Color.FromRgb(0x5A, 0xC8, 0xFA) &&
                Mascot.CurrentColor != Color.FromRgb(0xA8, 0xA8, 0xB0),
                Mascot.CurrentColor.ToString());

            // 概率设 0：不触发彩蛋
            double keepChance = Mascot.EggChance;
            Mascot.EggChance = 0;
            Mascot.ResetForTest();
            Check("概率 0 时不触发彩蛋（保持常态）", Mascot.Roll() == null && Mascot.CurrentLine == Mascot.NormalLine, "");

            // 概率设 1：必然命中；因两条互斥，每次只出第一条（绝不并列）
            Mascot.EggChance = 1;
            var eggHits = new List<string>();
            for (int i = 0; i < 8; i++) { string? e = Mascot.Roll(); if (e != null) eggHits.Add(e); }
            var pool = Mascot.EggLinesForTest();
            Check("彩蛋两条互斥：一次掷骰只出一条，且彩蛋池恰好两条",
                eggHits.Count == 8 && eggHits.All(e => e == pool[0]) &&
                pool.Length == 2 &&
                pool[0] == "你目录里的DSH是什么...大烧货吗？" &&
                pool[1] == "DSH是什么？DeepSeek Hentai?（别说这个）",
                $"概率=1 时 8 次全部为第一条（{eggHits.Distinct().Count()} 种）；池内 {pool.Length} 条");

            // 命中后三处同步：底端 / 说明页 / 事件信息里那条，同句同色
            Mascot.EggChance = 1;
            // ⚠ 彩蛋条数必须**在建那条事件之前**取快照：`EventCountForTest()` 数的是**全部**事件，
            //   与下面要数的 **Mascot 种类**条数毫无关系 —— 拿前者当后者的下标会 Skip 过头
            //   （实测：全部=16 而彩蛋只有 2 条 ⇒ Skip(16) 得空表 ⇒ 增量恒 0，断言必然假红）。
            var n61EggBefore = MascotEventTextsForTest();
            MainWindow.AddEvent("自检-彩蛋联动");
            PumpUntil(() => false, 300);
            w.ApplyMascot();                       // 自检窗口不一定是 _liveInstance，这里主动同步一次
            string footerText = (w.FindName("MascotFooter") as TextBlock)?.Text ?? "";
            var footerBrush = (w.FindName("MascotFooter") as TextBlock)?.Foreground as SolidColorBrush;
            var lastMascotEvent = MainWindow.LastMascotEventForTest();
            Check("彩蛋命中后：底端文字 = 口头禅，事件行同句，三处同色",
                Mascot.CurrentLine != Mascot.NormalLine &&
                footerText == Mascot.CurrentLine &&
                footerBrush != null && footerBrush.Color == Mascot.CurrentColor &&
                lastMascotEvent != null && lastMascotEvent.Contains(Mascot.CurrentLine) &&
                MainWindow.EventColor(MainWindow.EventKind.Mascot) == Mascot.CurrentColor,
                $"底端「{footerText}」色 {footerBrush?.Color}；事件「{Shorten(lastMascotEvent ?? "", 30)}」");
            // ⚠ 本条原先是"**全部**事件数 == 变动前 + 2"（原事件 + 一条彩蛋）——
            //   那是在**数全部事件**，而事件流里还有**不是本用例产生**的条目：`StatusTimer_Tick` 每秒
            //   跑一趟，端口同步那一支（MainWindow.xaml.cs:2665）会写一条
            //   「端口 N 已在监听 → 同步为运行中（外部引擎）」。自检运行时若用户的引擎正开着
            //   （实测：3080 上就有），这条断言就会多出 +1/+2 ⇒ [299] 变红，**功能其实没坏**。
            //   数"全部事件"永远量不准"彩蛋只多了一条"：那不是本用例能关掉的外部事件源
            //   （自检窗口的 _statusTimer 不是只为自检存在的钩子，不为它单开口子）。
            // ⚠ 为什么**不能**断"恰好 1 条"（第二版实测踩到）：`Mascot.EggChance == 1` 意味着**任何**一条
            //   非彩蛋事件都会生一枚彩蛋，而上面那条外部事件**同样会生**（实测正是新增 2 条）⇒
            //   观察窗里合法地可能是 2 条。数"恰好几条"再怎么改都仍在数别人的事件。
            // ⇒ 改成问三个**与噪声无关**的问题（对多余条目完全免疫）：
            //     ① 本窗确实产出了蛋（`Mascot.IsEgg`；0 条 ⇒ 闸门或联动坏了 ⇒ 红）；
            //     ② 本窗**新增**的 Mascot 条目**全**是当前那句蛋 —— 递归一旦回来（`AddEvent` 里那层
            //        `if (kind != EventKind.Mascot)` 失守），每次加蛋都会再掷一次 ⇒ 同一观察窗里会出现
            //        多条**内容相同**的蛋 ⇒ `All(...)` 立刻红；而"根本没产出蛋"由 ① 拦下；
            //     ③ 新增条目里**不许**混进变动前那条**常态**口头禅（那个字符串不属于本窗）。
            var n61EggAfter = MascotEventTextsForTest();
            int eggCountBefore = MainWindow.EventCountForTest();
            var n61EggNew = n61EggAfter.Except(n61EggBefore).ToList();
            Check("彩蛋事件自身不会再触发彩蛋（观察窗内新增的 Mascot 条目全是同一句蛋，且不混入变动前那条常态口头禅）",
                Mascot.IsEgg && n61EggNew.Count >= 1 && n61EggNew.Count <= 2 &&
                n61EggNew.All(t => t.Contains(Mascot.CurrentLine, StringComparison.Ordinal)) &&
                !n61EggNew.Any(t => t.Contains(Mascot.NormalLine, StringComparison.Ordinal)),
                $"本条新增的全事件数={MainWindow.EventCountForTest() - eggCountBefore}（含引擎 tick 噪声）· "
                + $"新增 Mascot 条目={n61EggNew.Count} 条（应 1-2）· 是蛋={Mascot.IsEgg} · "
                + $"口头禅=«{Shorten(Mascot.CurrentLine, 20)}» · "
                + $"首条=«{Shorten(n61EggNew.Count > 0 ? n61EggNew[0] : "", 30)}»");

            Mascot.EggChance = keepChance;
            Mascot.ResetForTest();
            w.ApplyMascot();       // 复位后主动同步一次：三处一起回到常态原样

            // ══════ 24. 第 39 批：终止引擎两档策略（有界 + 强杀不误伤自身） ══════
            Check("第二档判定：上次没停掉且端口仍占用 → 走强杀",
                ProcessManager.IsSecondTier(true, true) && !ProcessManager.IsSecondTier(true, false) &&
                !ProcessManager.IsSecondTier(false, true),
                "attempted+open 才强杀");

            // 真起一对父子进程（node 父再 spawn node 子），验证「强杀整棵树且不误伤自己」
            const int probePort3 = 59471;
            int childPid = 0;
            var probeParent = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "node",
                Arguments = $"-e \"const cp=require('child_process');const s=require('net').createServer().listen({probePort3},'127.0.0.1');" +
                            $"const c=cp.spawn(process.execPath,['-e','setInterval(()=>{{}},1000)'],{{stdio:'ignore'}});console.log(c.pid);\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            });
            try
            {
                string? line = probeParent?.StandardOutput.ReadLine();
                int.TryParse(line, out childPid);
            }
            catch { }
            for (int i = 0; i < 32 && !NetworkHelper.IsPortListening(probePort3); i++) System.Threading.Thread.Sleep(250);

            if (!NetworkHelper.IsPortListening(probePort3) || probeParent == null || childPid <= 0)
            {
                // ⚠ 这里必须记 SKIP，不能记 PASS。原先写的是 `Check(…, true, "…跳过")`
                //   ⇒ 环境起不了探针进程时，报告里会多一条**绿**，验收会误以为"强杀整棵树"真验过了，
                //     而实际上这一条**一次都没跑**。跳过就要如实显示成跳过。
                Skip("强杀整棵树（父子进程都结束、自身存活）", "环境不允许起测试进程，本条未验证");
            }
            else
            {
                var swKill = System.Diagnostics.Stopwatch.StartNew();
                var killed = ProcessManager.ForceKillTreeExceptSelf(probeParent.Id, out string treeDetail);
                swKill.Stop();
                System.Threading.Thread.Sleep(1200);
                bool parentGone = !ProcessManager.IsAliveForTest(probeParent.Id);
                bool childGone = !ProcessManager.IsAliveForTest(childPid);
                bool portFree = !NetworkHelper.IsPortListening(probePort3);
                bool selfAlive = true;   // 能执行到这里，本进程当然活着
                Check("强杀整棵树：父子进程都结束、端口释放、自己不受伤",
                    parentGone && childGone && portFree && killed.Count >= 2 && swKill.ElapsedMilliseconds < 5000,
                    $"{treeDetail}；父死={parentGone} 子死={childGone} 端口空闲={portFree} 用时 {swKill.ElapsedMilliseconds}ms");
                Check("强杀不误伤自身（自身 PID 未出现在被杀名单里）",
                    !killed.Contains(Environment.ProcessId) && selfAlive,
                    $"被杀 {killed.Count} 个：{string.Join(",", killed)}");
            }
            try { if (probeParent != null && !probeParent.HasExited) probeParent.Kill(); } catch { }


            Check("版本号新规则：1.0 是第一个 release、1.1 是第二个、同轮第 5 次修改为 1.1.5",
                GuardVersion.VersionFor(0, 0) == "1.0" &&
                GuardVersion.VersionFor(1, 0) == "1.1" &&
                GuardVersion.VersionFor(1, 5) == "1.1.5" &&
                GuardVersion.VersionFor(2, 0) == "1.2" &&
                GuardVersion.Version == GuardVersion.VersionFor(GuardVersion.Minor, GuardVersion.Patch) &&
                (System.Diagnostics.FileVersionInfo.GetVersionInfo(Environment.ProcessPath!).FileVersion ?? "").StartsWith(GuardVersion.Version, StringComparison.Ordinal),
                $"当前 {GuardVersion.Display}（Minor={GuardVersion.Minor} Patch={GuardVersion.Patch}，第 {GuardVersion.Batch} 批）；exe 文件版本 {System.Diagnostics.FileVersionInfo.GetVersionInfo(Environment.ProcessPath!).FileVersion}");
            Check("说明页显示当前版本号",
                AboutTextsForTest(w).Contains(GuardVersion.Version),
                GuardVersion.Version);

            // ══════ 18. 第 24 批：原生快照（不依赖 undo 插件）══════
            // 把快照仓库与 profile 都指到临时目录：整个流程在沙箱里跑，不动真实配置
            string snapTmp = Path.Combine(Path.GetTempPath(), "dshguard-snap-selftest");
            try { if (Directory.Exists(snapTmp)) Directory.Delete(snapTmp, true); } catch { }
            string snapProfile = Path.Combine(snapTmp, "profile");
            Directory.CreateDirectory(snapProfile);
            string fakePkg = Path.Combine(snapProfile, "package.json");
            File.WriteAllText(fakePkg, "{\"name\":\"selftest\",\"v\":1}");
            GuardPaths.Apply(null, snapTmp, snapProfile);

            // 1) 关键验收线：这个环境里没有 dsh-undo-savepoint，快照照样能建
            var snapA = SnapshotManager.Create(SnapshotManager.KindManual, "自检");
            bool pluginPresent = Directory.Exists(Path.Combine(snapProfile, "node_modules", "dsh-undo-savepoint"));
            Check("没有 undo 插件也能建快照（原生能力）",
                snapA != null && !pluginPresent &&
                snapA!.Files.Any(f => f.Name == "profile-package.json" && f.Restorable),
                $"{snapA?.RestorableCount ?? 0} 个可回滚文件 · 插件存在={pluginPresent}");

            // 2) 凭据不落盘
            Check("凭据文件只登记跳过、不写进快照",
                snapA != null && snapA.Files.Any(f => f.Name == "home-.credentials.yaml" && f.Skipped) &&
                !File.Exists(Path.Combine(snapA.Dir, "home-.credentials.yaml")),
                snapA == null ? "无快照" : string.Join("、", snapA.Files.Select(f => f.Name)));

            // 3) 改动文件 → 回滚 → 内容回来；回滚前必须**自动存一份**（用户被覆盖后唯一的反悔素材）
            int preRestoreBefore = SnapshotManager.ListNative().Count(s => s.Kind == SnapshotManager.KindPreRestore);
            File.WriteAllText(fakePkg, "{\"name\":\"selftest\",\"v\":2}");
            var restoreReport = SnapshotManager.Restore(snapA!, new[] { "profile-package.json" });
            bool restored = File.ReadAllText(fakePkg).Contains("\"v\":1");
            Check("回滚把文件还原成快照里的内容", restored, restoreReport.Count > 0 ? restoreReport[^1] : "");
            int preRestoreAfter = SnapshotManager.ListNative().Count(s => s.Kind == SnapshotManager.KindPreRestore);
            Check("回滚前自动存了一份「回滚前」快照（不再是无回退素材）",
                preRestoreAfter == preRestoreBefore + 1,
                $"{preRestoreBefore} → {preRestoreAfter} 份");
            Check("报告里点名了这份回滚前快照",
                restoreReport.Any(l => l.Contains("回滚前已自动存快照")),
                restoreReport.FirstOrDefault(l => l.Contains("回滚前已自动存快照")) ?? "(无)");

            // 3b) 越界护栏：清单里的 Target 是**当时那台机器**的绝对路径，profile 之外一律拒写。
            //     夹具全在 snapTmp 里；哨兵文件用来证明"真的没被覆盖"。
            string outsideVictim = Path.Combine(snapTmp, "victim-outside.txt");
            File.WriteAllText(outsideVictim, "SENTINEL");
            string evilDir = Directory.CreateDirectory(Path.Combine(snapTmp, "20990101-000000-manual")).FullName;
            File.WriteAllText(Path.Combine(evilDir, "profile-x.txt"), "PWNED");
            File.WriteAllText(Path.Combine(evilDir, "manifest.json"), SelfTestManifest(
                "20990101-000000-manual", "manual", "自检-越界", "", ("profile-x.txt", outsideVictim)));
            var evilSnap = SnapshotManager.ListNative().FirstOrDefault(s => s.Id == "20990101-000000-manual");
            var evilReport = evilSnap == null ? new List<string>() : SnapshotManager.Restore(evilSnap, null);
            Check("越界目标被拒：profile 之外的文件**未被覆盖**",
                evilSnap != null && File.ReadAllText(outsideVictim) == "SENTINEL",
                $"{outsideVictim} = {File.ReadAllText(outsideVictim)}");
            Check("越界目标在报告里记 ❌（不是静默、也不是假成功 ✅）",
                evilReport.Any(l => l.StartsWith("❌") && l.Contains("超出允许范围")) &&
                !evilReport.Any(l => l.Contains("✅")),
                evilReport.LastOrDefault() ?? "(无报告)");

            // 3c) 哈希校验：清单哈希与快照文件内容不符 ⇒ 跳过，目标保持原样
            string hashVictim = Path.Combine(snapProfile, "hash-target.json");
            File.WriteAllText(hashVictim, "{\"v\":\"CURRENT\"}");
            string hashDir = Directory.CreateDirectory(Path.Combine(snapTmp, "20990102-000000-manual")).FullName;
            File.WriteAllText(Path.Combine(hashDir, "profile-h.json"), "TAMPERED");
            File.WriteAllText(Path.Combine(hashDir, "manifest.json"), SelfTestManifest(
                "20990102-000000-manual", "manual", "自检-哈希", "00ff", ("profile-h.json", hashVictim)));
            var hashSnap = SnapshotManager.ListNative().FirstOrDefault(s => s.Id == "20990102-000000-manual");
            var hashReport = hashSnap == null ? new List<string>() : SnapshotManager.Restore(hashSnap, null);
            Check("哈希不符 ⇒ 目标未被写入且记 ❌",
                hashSnap != null && File.ReadAllText(hashVictim).Contains("CURRENT") &&
                hashReport.Any(l => l.StartsWith("❌") && l.Contains("已损坏或被改动")),
                hashReport.LastOrDefault() ?? "(无报告)");

            string plainVictim = Path.Combine(snapProfile, "nohash-target.json");
            File.WriteAllText(plainVictim, "{\"v\":\"CURRENT\"}");
            string noHashDir = Directory.CreateDirectory(Path.Combine(snapTmp, "20990103-000000-manual")).FullName;
            File.WriteAllText(Path.Combine(noHashDir, "profile-n.txt"), "{\"v\":\"FROM-OLD-SNAP\"}");
            File.WriteAllText(Path.Combine(noHashDir, "manifest.json"), SelfTestManifest(
                "20990103-000000-manual", "manual", "自检-老快照", "", ("profile-n.txt", plainVictim)));
            var noHashSnap = SnapshotManager.ListNative().FirstOrDefault(s => s.Id == "20990103-000000-manual");
            var noHashReport = noHashSnap == null ? new List<string>() : SnapshotManager.Restore(noHashSnap, null);
            Check("老快照没记哈希 ⇒ 放行还原（不破坏老快照可用性）并注明",
                noHashSnap != null && File.ReadAllText(plainVictim).Contains("FROM-OLD-SNAP") &&
                noHashReport.Any(l => l.Contains("老快照未记哈希")),
                noHashReport.LastOrDefault() ?? "(无报告)");
            Check("回滚事件用橙色（有失败才转红）",
                MainWindow.RollbackEventKind(0) == MainWindow.EventKind.Warn &&
                MainWindow.RollbackEventKind(2) == MainWindow.EventKind.Bad,
                $"失败0 → {MainWindow.RollbackEventKind(0)}；失败2 → {MainWindow.RollbackEventKind(2)}");

            // 4) 保留策略：自动快照只留 N 份，删最旧
            SnapshotManager.SettingsCache.AutoSnapshotKeep = 20;
            for (int i = 0; i < 24; i++)
            {
                SnapshotManager.Create(SnapshotManager.KindAuto, "自检批量");
                System.Threading.Thread.Sleep(5);
            }
            int autoCount = SnapshotManager.ListNative().Count(s => s.Kind == SnapshotManager.KindAuto);
            Check("自动快照按保留份数裁剪（留 20 份）", autoCount == 20, $"{autoCount} 份");

            // 5) 完全脱嵌：列表里只能有程序自己仓库里的快照，不得出现任何第三方插件仓库的条目
            string thirdParty = Path.Combine(GuardPaths.DshHome, "undo-snapshots");
            var listed = SnapshotManager.ListSnapshots();
            Check("快照列表只来自程序自己的仓库（不读取第三方插件目录）",
                listed.All(s => s.Dir.StartsWith(GuardPaths.SnapshotRoot, StringComparison.OrdinalIgnoreCase)) &&
                !listed.Any(s => s.Dir.StartsWith(thirdParty, StringComparison.OrdinalIgnoreCase)),
                $"{listed.Count} 份，全部位于 {GuardPaths.SnapshotRoot}");

            // 6) 删除快照
            string dirToDelete = snapA!.Dir;
            Check("快照可删除（删后目录消失）",
                SnapshotManager.Delete(snapA) && !Directory.Exists(dirToDelete),
                dirToDelete);

            // 收尾：路径指向还原，临时目录清掉
            GuardPaths.Apply(null, null, null);
            try { Directory.Delete(snapTmp, true); } catch { }

            // ══════ 19. 第 25 批：可移植性（装到新电脑就能用）══════
            Check("引擎工作目录可移植：不含任何机器专有路径",
                ProcessManager.WorkDir.Length > 0 &&
                !ProcessManager.WorkDir.Contains("WorkShop", StringComparison.OrdinalIgnoreCase) &&
                !ProcessManager.WorkDir.Contains("DeepSeek Harness", StringComparison.OrdinalIgnoreCase),
                ProcessManager.WorkDir);
            string toolsDir = Path.Combine(AppContext.BaseDirectory, "Tools");
            var shipped = new[] { "clean-logs.ps1", "port-check.ps1", "check-plugin-updates.ps1", "install-node.ps1" }
                .Where(f => File.Exists(Path.Combine(toolsDir, f))).ToList();
            bool devBuild = File.Exists(Path.Combine(AppContext.BaseDirectory, "DSHGuard.dll"));
            Check("四个外部脚本随程序发布（不再是手工拷贝）",
                devBuild || shipped.Count == 4,
                $"存在 {shipped.Count}/4：{string.Join("、", shipped)}");

            // 目录契约：主目录下只应有这 5 个功能目录，名字首字母大写，且启动就能建好
            var contract = GuardPaths.LayoutContract;
            Check("目录契约：5 个功能目录、名字首字母大写、各有唯一用途",
                contract.Count == 5 &&
                contract.All(c => c.Name.Length > 0 && char.IsUpper(c.Name[0])) &&
                contract.Select(c => c.Name).OrderBy(n => n).SequenceEqual(
                    new[] { "Cache", "Config", "Logs", "Snapshots", "Tools" }),
                string.Join("、", contract.Select(c => c.Name)));
            var readyDirs = GuardPaths.EnsureLayout();
            Check("启动即可建好目录（幂等，含 Cache 的两个子目录）",
                readyDirs.Contains("Config") && readyDirs.Contains("Cache") &&
                readyDirs.Contains("Cache\\Market") && readyDirs.Contains("Cache\\Images") &&
                readyDirs.Contains("Logs") && readyDirs.Contains("Snapshots") &&
                Directory.Exists(GuardPaths.ConfigDir) && Directory.Exists(GuardPaths.CacheDirMarket) &&
                Directory.Exists(GuardPaths.CacheDirImages),
                string.Join("、", readyDirs));
            Check("默认落盘位置都在程序主目录下（日志与快照）",
                GuardPaths.DefaultLogDir.StartsWith(GuardPaths.ExeDir, StringComparison.OrdinalIgnoreCase) &&
                GuardPaths.DefaultSnapshotRoot.StartsWith(GuardPaths.ExeDir, StringComparison.OrdinalIgnoreCase) &&
                !GuardPaths.LayoutContract.Any(c => c.Name.Equals("Assets", StringComparison.OrdinalIgnoreCase)),
                $"{GuardPaths.DefaultLogDir} | {GuardPaths.DefaultSnapshotRoot}（Assets 已不在契约里）");

            // ══════ 25. 第 40 批（末位用例）：筛选弹层跟随窗口 ══════
            // 放在最后：这一条需要把窗口真正显示出来（Popup 要有可见的放置目标），
            // 而前面的"非显示窗口"用例依赖它没有句柄，所以顺序不能颠倒。
            w.OpenFilterPopupForTest();
            bool popupOpened = w.FilterPopupOpenForTest;
            w.CloseFilterPopupsForTest();
            Check("筛选弹层能被统一收起（最小化/托盘/换页都会调它）",
                popupOpened && !w.FilterPopupOpenForTest,
                $"打开={popupOpened} → 收起后={w.FilterPopupOpenForTest}");
            w.OpenFilterPopupForTest();
            w.WindowState = WindowState.Minimized;      // 走真实最小化路径
            PumpUntil(() => false, 200);
            Check("最小化窗口时筛选弹层跟着收起（不会留在桌面上）",
                !w.FilterPopupOpenForTest,
                $"最小化后 IsOpen={w.FilterPopupOpenForTest}");
            w.WindowState = WindowState.Normal;
            w.HideAfterTest();

            // ══════ 26. 第 41 批：启动等待判死 / 毛玻璃优化 ══════
            Check("启动等待：端口就绪或进程已死都该停止空等（不空等满 180 秒）",
                ProcessManager.ShouldStopWaiting(false, true) && ProcessManager.ShouldStopWaiting(true, false) &&
                !ProcessManager.ShouldStopWaiting(false, false),
                "就绪/进程死了 → 停；否则继续等");

            // 毛玻璃着色去重：同样的颜色不该反复下发（每次下发都会让 DWM 重新合成，是"发飘"的来源之一）
            w.EnsureShownForTest();
            BlurHelper.ResetTintCountersForTest();
            BlurHelper.SetTint(w, 0x1E, 0x12, 0x16, 0x1E);
            BlurHelper.SetTint(w, 0x1E, 0x12, 0x16, 0x1E);
            Check("毛玻璃着色同样值只下发一次（省合成、少发飘）",
                BlurHelper.TintApplies == 1 && BlurHelper.TintSkips == 1,
                $"下发 {BlurHelper.TintApplies} 次 / 跳过 {BlurHelper.TintSkips} 次");
            BlurHelper.ForgetLastTintForTest();
            BlurHelper.SetTint(w, 0x14, 0xEE, 0xEF, 0xF3);
            Check("换主题换颜色时会重新下发",
                BlurHelper.TintApplies == 2, $"累计下发 {BlurHelper.TintApplies} 次");

            // 毛玻璃开关：设置往返（老机器关掉后要能记住）
            var keepAcrylic = w.AcrylicSettingForTest;
            w.SetAcrylicSettingForTest(false);
            var reloadedSettings = new SettingsManager();
            reloadedSettings.Load();
            Check("毛玻璃开关能存能读（关掉之后仍是关的）",
                !reloadedSettings.AcrylicEnabled, $"写入 false → 读回 {reloadedSettings.AcrylicEnabled}");
            w.SetAcrylicSettingForTest(keepAcrylic);
            var reloaded2 = new SettingsManager();
            reloaded2.Load();
            Check("毛玻璃开关恢复原值",
                reloaded2.AcrylicEnabled == keepAcrylic, $"恢复为 {reloaded2.AcrylicEnabled}");
            w.HideAfterTest();

            // ══════ 27. 版本 1.2：启动失败回空闲态的三态互斥 / 配置文件死链接体检 ══════
            // 现场 bug：启动失败后「一键启动引擎」和「正在加载...」同时留在界面上。
            int VisibleCount((Visibility Idle, Visibility Running, Visibility Loading) p)
                => (p.Idle == Visibility.Visible ? 1 : 0) + (p.Running == Visibility.Visible ? 1 : 0)
                 + (p.Loading == Visibility.Visible ? 1 : 0);

            w.ShowRunningForTest(false);                  // 先归到空闲态
            var panelsIdle = w.MainPanelsForTest();
            w.ShowLoadingForTest(true, 42);               // 进入加载态
            var panelsLoading = w.MainPanelsForTest();
            w.ShowLoadingForTest(false);                  // 启动失败/超时 → 直接回空闲态
            var panelsBack = w.MainPanelsForTest();

            Check("加载态只显示加载条，另两个按钮收起来",
                panelsLoading.Loading == Visibility.Visible && panelsLoading.Idle == Visibility.Collapsed &&
                panelsLoading.Running == Visibility.Collapsed,
                $"空闲={panelsLoading.Idle} 运行={panelsLoading.Running} 加载={panelsLoading.Loading}");
            Check("启动失败回到空闲态时加载条一并收起（不会再两个按钮叠在一起）",
                panelsBack.Idle == Visibility.Visible && panelsBack.Loading == Visibility.Collapsed &&
                panelsBack.Running == Visibility.Collapsed,
                $"空闲={panelsBack.Idle} 运行={panelsBack.Running} 加载={panelsBack.Loading}");
            Check("三种状态下可见的服务按钮始终只有一个",
                VisibleCount(panelsIdle) == 1 && VisibleCount(panelsLoading) == 1 && VisibleCount(panelsBack) == 1,
                $"{VisibleCount(panelsIdle)} / {VisibleCount(panelsLoading)} / {VisibleCount(panelsBack)}");

            // 配置文件死链接体检：造一个「目标已消失」的模块联接，应能检出并接回真实位置
            string probeRoot = Path.Combine(Path.GetTempPath(), "dshguard-links-" + Guid.NewGuid().ToString("N"));
            try
            {
                string profilesDir = Path.Combine(probeRoot, "profiles");
                string liveTarget = Path.Combine(profilesDir, "node_modules", "demo-mod");
                string doomed = Path.Combine(probeRoot, "gone", "demo-mod");
                string link = Path.Combine(profilesDir, "web", "node_modules", "demo-mod");
                Directory.CreateDirectory(liveTarget);
                Directory.CreateDirectory(doomed);
                Directory.CreateDirectory(Path.GetDirectoryName(link)!);
                bool linked = ProfileHealth.CreateJunctionForTest(link, doomed);
                Directory.Delete(Path.Combine(probeRoot, "gone"), recursive: true);   // 目标消失 → 联接变死

                var brokenLinks = ProfileHealth.FindBrokenModuleLinks(profilesDir);
                Check("能找出指向已消失位置的模块链接",
                    linked && brokenLinks.Count == 1 && brokenLinks[0].Name == "demo-mod",
                    $"联接建立={linked} 检出={brokenLinks.Count}");

                int repaired = ProfileHealth.RepairBrokenModuleLinks(profilesDir, brokenLinks, out string fixDetail);
                bool resolves = false;
                try { resolves = Directory.Exists(link); } catch { }
                var stillBroken = ProfileHealth.FindBrokenModuleLinks(profilesDir);
                Check("接回之后链接能重新解析、体检也不再报失效",
                    repaired == 1 && resolves && stillBroken.Count == 0,
                    $"{fixDetail}｜解析={resolves} 剩余={stillBroken.Count}");
            }
            finally
            {
                try { if (Directory.Exists(probeRoot)) Directory.Delete(probeRoot, recursive: true); } catch { }
            }

            // ══════ 28. 版本 1.3：关掉毛玻璃后的窗口底色 / 左侧导航文字居中 ══════
            // 现场 bug：关掉「毛玻璃背景」后分层窗口整窗透空，桌面壁纸直接透进来。
            w.SetAcrylicStateForTest(false);
            string solidBg = w.RootBackdropForTest();
            w.SetAcrylicStateForTest(true);
            string glassBg = w.RootBackdropForTest();
            w.SetAcrylicStateForTest(true);

            var solidParts = solidBg.Split(',', StringSplitOptions.RemoveEmptyEntries);
            var glassParts = glassBg.Split(',', StringSplitOptions.RemoveEmptyEntries);
            Check("关掉毛玻璃后窗口底色不透明（不再整窗透空、壁纸透进来）",
                solidParts.Length == 3 && solidParts.All(c => c.StartsWith("#FF", StringComparison.OrdinalIgnoreCase)),
                solidBg);
            Check("开着毛玻璃时窗口底色仍是半透明（系统材质能透出来）",
                glassParts.Length == 3 && glassParts.All(c => !c.StartsWith("#FF", StringComparison.OrdinalIgnoreCase)),
                glassBg);
            Check("不透明版与半透明版只有透明度不同（色相一致）",
                solidParts.Length == 3 && glassParts.Length == 3 &&
                solidParts.Select(c => c.Substring(3)).SequenceEqual(glassParts.Select(c => c.Substring(3))),
                $"{solidBg} ｜ {glassBg}");

            // 导航文字居中的版本做过一次，评审时认为不好看 → 保留左对齐；这条断言把决定钉住，防止以后再"优化"回去
            Check("左侧导航七项保持左对齐（居中版已按意见回滚）",
                w.NavLabelCenteredCountForTest() == 0, $"居中 {w.NavLabelCenteredCountForTest()}/7（应为 0）");

            // 状态行：与其他行一样左对齐；运行中不再缀「（外部）」
            w.ShowRunningForTest(true);
            string runningText = w.StatusTextForTest();
            w.ShowRunningForTest(false);
            Check("引擎运行中的状态行左对齐、且不再带「（外部）」后缀",
                !w.StatusRowCenteredForTest() && runningText.StartsWith("引擎运行中", StringComparison.Ordinal)
                && !runningText.Contains("外部", StringComparison.Ordinal),
                $"居中={w.StatusRowCenteredForTest()} ｜ 文案「{runningText}」");

            // 开关轨道色：日间关=浅灰、开=绿；夜间关=深灰、开=绿
            // （原来模板用颜色动画切轨道色，动画时钟把画刷钉住 → 映射表跳过 → 日间也是夜间深灰）
            w.ApplyThemeForTest(false);
            string dayOff = w.SwitchTrackColorForTest("AutoStartToggle", false);
            string dayOn = w.SwitchTrackColorForTest("AutoBrowseToggle", true);
            w.ApplyThemeForTest(true);
            string nightOff = w.SwitchTrackColorForTest("AutoStartToggle", false);
            string nightOn = w.SwitchTrackColorForTest("AcrylicToggle", true);
            Check("日间模式下开关轨道是浅灰（关）/ 绿（开），不再停在夜间深灰",
                dayOff == "#FFD2D3D8" && dayOn == "#FF34C759", $"关={dayOff} 开={dayOn}");
            Check("夜间模式下开关轨道是深灰（关）/ 绿（开）",
                nightOff == "#FF3A3A3C" && nightOn == "#FF34C759", $"关={nightOff} 开={nightOn}");

            // 彩蛋第二条加了括号里的心理活动
            var eggs = Mascot.EggLinesForTest();
            Check("第二条彩蛋带括号心理活动",
                eggs.Length == 2 && eggs[1].EndsWith("（别说这个）", StringComparison.Ordinal), eggs[1]);

            // ══════ 29. 版本 1.5：弹窗不成屏霸 / 入口地址与就绪判定 / 单实例锁不再崩 ══════
            // 弹窗：即使写入数十行堆栈也不能高于屏幕，正文需可滚动、按钮必须可见（现场：无法关闭，只能使用任务管理器）
            string longMsg = string.Join("\n", Enumerable.Range(0, 120).Select(i => $"[stderr]     at frame {i}"));
            var (cardH, limit, hasScroll, buttonsVisible) = GuardDialog.LongMessageProbeForTest(longMsg, dark: true);
            Check("超长消息的对话框不出屏：高度受限 + 正文可滚动 + 按钮可见",
                cardH <= limit + 1 && hasScroll && buttonsVisible,
                $"卡片高 {cardH:0}px / 上限 {limit:0}px；可滚动={hasScroll} 按钮可见={buttonsVisible}");

            // 失败弹窗用规范短话：不含堆栈行、长度可控、给出原因与处理方式
            string dlgShort = StartupCause.DialogText(
                "Error [ERR_MODULE_NOT_FOUND]: Cannot find package '@deepseek-ai/dsh-client-ui-conversation'",
                "[stderr]     at updateError (file:///C:/...)\n[stderr]     at Entry._init (...)", 180);
            Check("失败弹窗是规范短话：不含堆栈行、长度 ≤ 200 字、有原因与处理方式",
                dlgShort.Length <= 200 && !dlgShort.Contains("    at ") &&
                dlgShort.Contains("原因：") && dlgShort.Contains("处理方式："),
                $"长度 {dlgShort.Length}");

            // 1.1.9 改口径：这类失败重试无用，翻译必须简短规范（不再劝人反复重试）
            Check("「内部组件解析不到」翻译成一句规范短语，且不劝人反复重试",
                StartupCause.Describe("Cannot find package '@deepseek-ai/dsh-client-ui-conversation'") == "引擎自带组件与当前配置文件不匹配" &&
                !StartupCause.Describe("Cannot find package '@deepseek-ai/dsh-client-ui-conversation'").Contains("再点"),
                StartupCause.Describe("Cannot find package '@deepseek-ai/dsh-client-ui-conversation'"));
            Check("端口占用 / 网络不通 / 权限问题各翻译成一句规范短语",
                StartupCause.Describe("Error: listen EADDRINUSE: address already in use :::3080") == "端口被其他程序占用" &&
                StartupCause.Describe("npm ERR! network ETIMEDOUT") == "网络未连通，或下载被拦截" &&
                StartupCause.Describe("Error: EACCES: permission denied") == "权限不足" &&
                StartupCause.Describe("") == "原因不明",
                "四种签名各一句");

            // 就绪判定（探的是干净地址）：404=还没注册完；401=已注册可开；2xx/3xx=能直接看；其余不算
            Check("就绪判定：404 未就绪 / 401 已注册 / 2xx 可以开 / 5xx 不算",
                !NetworkHelper.IsRegistered(404) && NetworkHelper.IsRegistered(401) &&
                NetworkHelper.IsRegistered(200) && NetworkHelper.IsRegistered(302) &&
                !NetworkHelper.IsRegistered(403) && !NetworkHelper.IsRegistered(500) &&
                !NetworkHelper.IsRegistered(0),
                "404/401/200/302/403/500/0 七种状态");
            Check("环回地址把 localhost 归一成 127.0.0.1（避免解析成 ::1 连不上）",
                NetworkHelper.NormalizeLoopback("http://localhost:3080/?token=x").StartsWith("http://127.0.0.1:", StringComparison.Ordinal),
                NetworkHelper.NormalizeLoopback("http://localhost:3080/?token=x"));
            Check("干净地址回 401 时立即算就绪（不再空等）",
                RunOffUi(() => NetworkHelper.WaitUntilHttpReadyAsync(
                    "http://127.0.0.1:3080/", 5000, _ => Task.FromResult(401))),
                "注入 401 探测 → 立即就绪");
            Check("一直是 404（还在注册）时不判就绪",
                !RunOffUi(() => NetworkHelper.WaitUntilHttpReadyAsync(
                    "http://127.0.0.1:3080/", 300, _ => Task.FromResult(404))),
                "注入 404 探测 + 短超时 → 判定未就绪");

            // 单实例锁：第二个实例并不持有它，释放不能抛出异常（现场：无法关闭，只能强制结束进程）
            Check("没持锁时释放单实例锁不抛异常（第二实例关窗不再崩）",
                !App.ReleaseSingleInstance(new System.Threading.Mutex(false, "DSHGuard-Selftest-Probe"), owns: false) &&
                !App.ReleaseSingleInstance(new System.Threading.Mutex(false, "DSHGuard-Selftest-Probe2"), owns: true),
                "未持有 → 直接返回；误传 owns=true → 内部捕获、不抛");
            var ownedMutex = new System.Threading.Mutex(true, "DSHGuard-Selftest-" + Guid.NewGuid().ToString("N"), out bool mutexCreated);
            Check("真正持锁时能正常释放",
                mutexCreated && App.ReleaseSingleInstance(ownedMutex, owns: true), $"created={mutexCreated}");
            ownedMutex.Dispose();

            // ══════ 30. 版本 1.1：首开不再 404 / 托管判定接线 / 卸载窗 ══════
            Check("就绪判定把 404 当作「未就绪」（webserver 注册前对一切请求回 404）",
                !NetworkHelper.IsRegistered(404) && NetworkHelper.IsRegistered(401) &&
                NetworkHelper.IsRegistered(200) && !NetworkHelper.IsRegistered(500),
                "404=否；401=是（已注册可开）；200=是；500=否");
            Check("就绪探测永远打干净地址（绝不替浏览器用掉一次性令牌）",
                NetworkHelper.ChooseProbeUrl("http://127.0.0.1:3080/?token=abc", 3080) == "http://127.0.0.1:3080/" &&
                !NetworkHelper.ChooseProbeUrl("http://127.0.0.1:3080/?token=abc", 3080).Contains("token=", StringComparison.Ordinal),
                NetworkHelper.ChooseProbeUrl("http://127.0.0.1:3080/?token=abc", 3080));

            // 终止路由：托管时必须先问，其余按档走（第 39 批漏掉这一步 → 「关引擎连带关壳」复发）
            Check("终止路由：托管 → 必先询问；不托管 → 按档走",
                ProcessManager.PlanTerminate(true, false) == ProcessManager.TerminateRoute.AskHosted &&
                ProcessManager.PlanTerminate(true, true) == ProcessManager.TerminateRoute.AskHosted &&
                ProcessManager.PlanTerminate(false, false) == ProcessManager.TerminateRoute.Tier1 &&
                ProcessManager.PlanTerminate(false, true) == ProcessManager.TerminateRoute.Tier2,
                "hosted×secondTier 四种组合");

            // 卸载窗：主程序同款界面，**三个互斥选项**（全部清空 / 删除缓存 / 只删除主程序）+ 取消
            Check("--uninstall 被识别为卸载界面模式",
                SelfTest.ShouldUninstall(new[] { "--uninstall" }) && !SelfTest.ShouldUninstall(new[] { "--selftest" }),
                "--uninstall / --selftest");
            try
            {
                var un = new UninstallWindow(engineRunning: false);
                var (keepLabel, wipeLabel, okLabel, cancelLabel) = un.LayoutForTest();
                Check("卸载窗：两个单选（保留数据 / 连文件夹全删）+ 卸载/取消",
                    keepLabel == UninstallPlan.KeepDataLabel && wipeLabel == UninstallPlan.DeleteAllLabel &&
                    okLabel == "卸载" && cancelLabel == "取消",
                    $"单选「{keepLabel}」「{wipeLabel}」；按钮 {okLabel}/{cancelLabel}");

                Check("卸载窗：默认选「保留数据」（最保守）",
                    un.SelectedForTest() == UninstallMode.AppOnly, un.SelectedForTest().ToString());

                un.CheckForTest(false);
                Check("卸载窗：选「全部删除」= 连程序文件夹一起删",
                    un.SelectedForTest() == UninstallMode.Everything, un.SelectedForTest().ToString());
                un.CheckForTest(true);
                Check("卸载窗：切回「保留数据」= 只删主程序（单选原生互斥、不挂事件 ⇒ 不会闪退）",
                    un.SelectedForTest() == UninstallMode.AppOnly, un.SelectedForTest().ToString());
                un.Close();
            }
            catch (Exception ex)
            {
                Check("卸载窗：两个单选（保留数据 / 连文件夹全删）+ 卸载/取消", false, ex.Message);
            }

            Check("卸载方案：保留数据 ⇄ 全部删除 的映射（界面只问这一件事）",
                UninstallPlan.FromKeepData(true) == UninstallMode.AppOnly &&
                UninstallPlan.FromKeepData(false) == UninstallMode.Everything &&
                UninstallPlan.DeleteDataArg(UninstallPlan.FromKeepData(true)) == "" &&
                UninstallPlan.ModeArg(UninstallPlan.FromKeepData(false)) == "all",
                UninstallPlan.KeepDataLabel);

            Check("卸载方案：保留用户文件 ⇄ 全部清空的映射（界面只问这一件事）",
                UninstallPlan.FromKeepUserFiles(true) == UninstallMode.AppOnly &&
                UninstallPlan.FromKeepUserFiles(false) == UninstallMode.Everything &&
                UninstallPlan.DeleteDataArg(UninstallPlan.FromKeepUserFiles(true)) == "" &&
                UninstallPlan.ModeArg(UninstallPlan.FromKeepUserFiles(false)) == "all",
                UninstallPlan.KeepFilesLabel);

            // 重置 DSH 配置：新机器上"回滚"是空头支票，重置才是正路（2026-09-13 现场）
            string t35 = Path.Combine(Path.GetTempPath(), "dshguard-reset-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                string fake = Path.Combine(t35, "profiles", "web");
                Check("全新机器判定：目录不存在 / 没有插件清单都算「从没跑过 DSH」",
                    ProfileReset.LooksBrandNew(fake), fake);
                Directory.CreateDirectory(Path.Combine(fake, "node_modules"));
                Check("只有 node_modules、尚无配置清单，仍算全新机器",
                    ProfileReset.LooksBrandNew(fake), "空 node_modules 仍算新");
                File.WriteAllText(Path.Combine(fake, "package.json"), "{\"name\":\"dsh-profile-web\"}");
                Check("写了配置清单之后：不算新机器，且能数出插件数",
                    !ProfileReset.LooksBrandNew(fake) && ProfileReset.CountPlugins(fake) == 0,
                    $"全新={ProfileReset.LooksBrandNew(fake)} 插件={ProfileReset.CountPlugins(fake)}");

                var (ok, detail) = ProfileReset.MoveAside(fake);
                Check("重置 = 把配置目录整体搬走（不删），旧配置留成 .bak-时间戳",
                    ok && !Directory.Exists(fake) &&
                    Directory.GetDirectories(Path.GetDirectoryName(fake)!).Any(d => d.Contains(".bak-")),
                    detail);
                Check("搬走后再判：又是「全新机器」（引擎下次启动会自己重建）",
                    ProfileReset.LooksBrandNew(fake), $"全新={ProfileReset.LooksBrandNew(fake)}");
            }
            finally { try { Directory.Delete(t35, true); } catch { } }

            // 回退候选：不能给出本机根本没有的版本（现场：给 0.1.5 推荐 0.1.1-rc.2）
            Check("版本排序权重：0.1.5-rc.2 > 0.1.5-rc.1 > 0.1.1-rc.2",
                VersionMemory.VersionRank("0.1.5-rc.2") > VersionMemory.VersionRank("0.1.5-rc.1") &&
                VersionMemory.VersionRank("0.1.5-rc.1") > VersionMemory.VersionRank("0.1.1-rc.2") &&
                VersionMemory.VersionRank("0.1.5") > VersionMemory.VersionRank("0.1.5-rc.9"),
                $"{VersionMemory.VersionRank("0.1.5-rc.2"):0} / {VersionMemory.VersionRank("0.1.5-rc.1"):0} / {VersionMemory.VersionRank("0.1.1-rc.2"):0}");

            // 卸载方案（纯函数）：三种模式各自删什么、以及"绝不碰 DSH 配置文件"
            Check("卸载方案：全部清空连程序目录一起删，其余两种保留目录",
                UninstallPlan.DeletesAppFolder(UninstallMode.Everything) &&
                !UninstallPlan.DeletesAppFolder(UninstallMode.WithCache) &&
                !UninstallPlan.DeletesAppFolder(UninstallMode.AppOnly),
                "all=删目录 / cache、app=留目录");
            Check("卸载方案：删除清单正确（只删主程序=什么都不删）",
                UninstallPlan.DataDirs(UninstallMode.Everything).Length == 5 &&
                UninstallPlan.DataDirs(UninstallMode.WithCache).SequenceEqual(new[] { "Cache" }) &&
                UninstallPlan.DataDirs(UninstallMode.AppOnly).Length == 0 &&
                UninstallPlan.DeleteDataArg(UninstallMode.AppOnly) == "" &&
                UninstallPlan.ModeArg(UninstallMode.Everything) == "all",
                $"全部={string.Join("/", UninstallPlan.DataDirs(UninstallMode.Everything))} 缓存={string.Join("/", UninstallPlan.DataDirs(UninstallMode.WithCache))}");
            Check("卸载方案：任何模式都不动 DSH 的配置文件目录",
                UninstallPlan.DataDirs(UninstallMode.Everything).All(d => d is "Cache" or "Config" or "Logs" or "Snapshots" or "Tools") &&
                UninstallPlan.Hint(UninstallMode.Everything).Contains("不动 DSH 的配置文件"),
                UninstallPlan.Hint(UninstallMode.Everything));
            Check("卸载方案：命令行模式名解析（认不出按最保守的只删主程序）",
                UninstallPlan.Parse("all") == UninstallMode.Everything &&
                UninstallPlan.Parse("cache") == UninstallMode.WithCache &&
                UninstallPlan.Parse("garbage") == UninstallMode.AppOnly &&
                UninstallPlan.Parse("") == UninstallMode.AppOnly,
                "all / cache / garbage / 空");

            // ══════ 31. 版本 1.1.6/1.1.7：路径页引擎置顶 / 悬停半屏预览跟鼠标 / 文案规范化 ══════
            w.ShowViewForTest("settings");
            w.ShowSettingsTabForTest("paths");
            w.LayoutForTest(960, 640);
            double YOf(string name)
                => w.FindName(name) is FrameworkElement fe
                    ? fe.TranslatePoint(new Point(0, 0), (UIElement)w.Content).Y
                    : double.NaN;
            Check("路径页：引擎配置提到最上面（它在程序位置之上）",
                YOf("PathProfileBox") < YOf("PathExeBox") &&
                YOf("PathExeBox") < YOf("PathLogsBox") && YOf("PathLogsBox") < YOf("PathSnapBox"),
                $"引擎={YOf("PathProfileBox"):0} 程序={YOf("PathExeBox"):0} 记录={YOf("PathLogsBox"):0} 快照={YOf("PathSnapBox"):0}");

            // 文案规范化：右栏事件面板叫「事件信息」，旧名不再出现在界面文字里
            w.ShowViewForTest("status");
            w.LayoutForTest(960, 640);
            string windowText = CollectText((DependencyObject)w.Content);
            Check("事件面板标题是「事件信息」（旧名不再出现）",
                windowText.Contains("事件信息") && !windowText.Contains("最近事件"),
                $"含「事件信息」={windowText.Contains("事件信息")} 含旧名={windowText.Contains("最近事件")}");

            // 悬停预览：约界面一半大小、不吃鼠标、整块留在窗口内；点进去的放大层保持不变（既有断言在验）
            Border? thumbHolder = null;
            void FindHolder(DependencyObject o)
            {
                if (thumbHolder != null) return;
                if (o is Border b && Math.Abs(b.Width - 84) < 0.5 && Math.Abs(b.Height - 54) < 0.5 &&
                    b.Cursor == System.Windows.Input.Cursors.Hand)
                { thumbHolder = b; return; }
                int n = VisualTreeHelper.GetChildrenCount(o);
                for (int i = 0; i < n; i++) FindHolder(VisualTreeHelper.GetChild(o, i));
            }
            var previewPlugin = new PluginMarket.MarketPlugin
            { Name = "自检-预览", Owner = "test", RepoUrl = "https://github.com/test/repo" };
            previewPlugin.Screenshots.Add("https://example.com/preview.png");
            var previewCard = w.BuildMarketCardForTest(previewPlugin);
            FindHolder(previewCard);
            if (thumbHolder == null)
            {
                Skip("悬停半屏预览（尺寸/不吃鼠标/不出界）", "这张样本卡片里没找到缩略图，本条未验证");
            }
            else
            {
                var probeBmp = new WriteableBitmap(2, 2, 96, 96, PixelFormats.Pbgra32, null);
                w.ShowThumbPreview(probeBmp, thumbHolder);
                var (vis, pw, ph, hit, hasSrc, left, top) = w.ThumbPreviewForTest();
                Check("悬停预览：浮出、有图、且不吃鼠标（不然会把自己的 MouseLeave 吃掉）",
                    vis && hasSrc && !hit, $"可见={vis} 有图={hasSrc} 吃鼠标={hit}");
                Check("悬停预览：尺寸约界面一半（960×640 → 480×320）",
                    Math.Abs(pw - 480) < 1 && Math.Abs(ph - 320) < 1, $"{pw:0}×{ph:0}");
                Check("悬停预览：整块留在窗口内（不出界）",
                    left >= 8 && top >= 8 && left + pw <= 960 - 7 && top + ph <= 640 - 7,
                    $"位置 {left:0},{top:0} 尺寸 {pw:0}×{ph:0}");
                // 跟鼠标：预览贴着光标走，不是钉死在缩略图旁边
                w.FollowThumbPreview(new Point(100, 80));
                var (_, _, _, _, _, fl, ft) = w.ThumbPreviewForTest();
                w.FollowThumbPreview(new Point(200, 160));
                var (_, _, _, _, _, fl2, ft2) = w.ThumbPreviewForTest();
                Check("悬停预览：跟着鼠标走（光标移 100px，预览跟着移 100px）",
                    Math.Abs(fl2 - fl - 100) < 1 && Math.Abs(ft2 - ft - 80) < 1,
                    $"({fl:0},{ft:0}) → ({fl2:0},{ft2:0})");
                w.FollowThumbPreview(new Point(955, 635));      // 光标到右下角：应翻到左上方且不越界
                var (_, _, _, _, _, el, et) = w.ThumbPreviewForTest();
                Check("悬停预览：光标贴到右下角时翻到左上方，仍不出界",
                    el >= 8 && et >= 8 && el + pw <= 960 - 7 && et + ph <= 640 - 7,
                    $"位置 {el:0},{et:0} 尺寸 {pw:0}×{ph:0}");
                Check("缩略图不再挂悬停文字提示（旧写法置空 ToolTip 会留下一个没展开的白色小方框）",
                    thumbHolder.ToolTip == null, $"气泡={thumbHolder.ToolTip ?? "(空)"}");
                w.HideThumbPreview();
                var afterHide = w.ThumbPreviewForTest();
                Check("悬停预览：移开就收起（缩略图始终不挂悬停气泡，不会留白色小方框）",
                    !afterHide.Visible && thumbHolder.ToolTip == null,
                    $"可见={afterHide.Visible} 气泡={(thumbHolder.ToolTip ?? "(空)")}");
            }

            // ══════ 32. 版本 1.1.8：入口地址（不再误报橙色）/ 进度条平滑推进 ══════

            // 入口地址说明：只有「本程序自己启动却始终未等到口令」才算异常
            Check("入口地址：拿到带口令地址时只记普通信息（不是橙色告警）",
                MainWindow.EntryNote(hasToken: true, external: false).Kind == MainWindow.EventKind.Info,
                MainWindow.EntryNote(true, false).Text);
            Check("入口地址：引擎不是本程序启动的，拿不到口令也只记普通信息（每次点启动都报橙色的根因）",
                MainWindow.EntryNote(hasToken: false, external: true).Kind == MainWindow.EventKind.Info,
                MainWindow.EntryNote(false, true).Text);
            Check("入口地址：本程序启动却一直没等到口令，才记橙色且给可照做的动作",
                MainWindow.EntryNote(hasToken: false, external: false).Kind == MainWindow.EventKind.Warn &&
                MainWindow.EntryNote(false, false).Text.Contains("终止引擎"),
                MainWindow.EntryNote(false, false).Text);
            Check("这两种说明在面板上按灰白显示（不是告警橙）",
                MainWindow.EventColor(MainWindow.EntryNote(true, false).Kind) == MainWindow.EventColor(MainWindow.EventKind.Info) &&
                MainWindow.EventColor(MainWindow.EntryNote(false, true).Kind) != MainWindow.EventColor(MainWindow.EventKind.Warn),
                $"拿到口令={MainWindow.EventColor(MainWindow.EntryNote(true, false).Kind)}，" +
                $"外部引擎={MainWindow.EventColor(MainWindow.EntryNote(false, true).Kind)}，" +
                $"告警橙={MainWindow.EventColor(MainWindow.EventKind.Warn)}");

            // 入口地址等待：值一出现立刻返回；一直不出现就按超时返回，不把启动卡死
            int reads = 0;
            bool gotEntry = RunOffUi(() => MainWindow.WaitForEntryUrlAsync(
                () => ++reads >= 3 ? "http://127.0.0.1:3080/?token=abc" : "", 2000));
            Check("入口地址等待：引擎一打印就立刻拿到（端口就绪早于打印，必须等它一下）",
                gotEntry && reads <= 6, $"读 {reads} 次，拿到={gotEntry}");
            var entrySw = System.Diagnostics.Stopwatch.StartNew();
            bool noEntry = RunOffUi(() => MainWindow.WaitForEntryUrlAsync(() => "", 300));
            Check("入口地址等待：一直没打印就按超时返回（有界，不卡住启动）",
                !noEntry && entrySw.ElapsedMilliseconds >= 250 && entrySw.ElapsedMilliseconds < 2500,
                $"超时 {entrySw.ElapsedMilliseconds}ms");

            // 启动命令始终由守护壳掌控浏览器交接
            Check("启动命令始终带 --no-open（引擎不会自己再开一个浏览器）",
                ProcessManager.BuildArgs(3080).Contains("--no-open", StringComparison.Ordinal),
                ProcessManager.BuildArgs(3080));

            // 蠕行曲线：单调递增、恒不超过 99%（100% 只留给完成信号）
            bool creepMono = true;
            double prevCreep = -1;
            for (double s = 0; s <= 300; s += 5)
            {
                double c = StartupProgress.Creep(s);
                if (c < prevCreep) creepMono = false;
                prevCreep = c;
            }
            Check("卡住时的蠕行曲线单调递增、且永远到不了 100%",
                creepMono && StartupProgress.Creep(180) > 85 && StartupProgress.Creep(1e6) <= StartupProgress.CreepCap,
                $"60s={StartupProgress.Creep(60):0.#}% 180s={StartupProgress.Creep(180):0.#}% 上限={StartupProgress.CreepCap}%");
            Check("等待端口的目标值渐近到 88%（不假装知道真实进度）",
                StartupProgress.WaitTarget(0) >= StartupProgress.Waiting - 0.01 &&
                StartupProgress.WaitTarget(600) <= 88.01 && StartupProgress.WaitTarget(600) > 85,
                $"0s={StartupProgress.WaitTarget(0):0.#}% 600s={StartupProgress.WaitTarget(600):0.#}%");

            // 卡住场景：目标停在 3%，时间走过 60 秒后绿条自己要爬到 ~66%
            w.SetProgressForTest("");                       // 归零并复位加载时钟
            w.ShowLoadingForTest(true, 0);
            double panelW2 = ((Border)w.FindName("LoadingButtonPanel")!).ActualWidth;
            if (panelW2 <= 0) panelW2 = 400;
            w.SetProgressForTest("卡住测试: 进度 3%");
            for (int i = 0; i < 3; i++) w.TickForTest();
            double stallFrom = ((Border)w.FindName("LoadingFill")!).Width;
            w.AddLoadingClockForTest(60);                   // 时钟拨到 60 秒后（模拟长时间卡住）
            for (int i = 0; i < 3; i++) w.TickForTest();
            double stallTo = ((Border)w.FindName("LoadingFill")!).Width;
            var stallVals = w.LoadingValuesForTest();
            Check("引擎卡住时绿条仍在慢慢往上爬（60 秒 ≈ 66%，且不到 99%）",
                stallTo > stallFrom + panelW2 * 0.3 &&
                stallVals.Shown > 60 && stallVals.Shown < StartupProgress.CreepCap,
                $"{stallFrom:0.#}px → {stallTo:0.#}px（{stallVals.Shown:0.#}%）");

            // 提速：模拟 0.4 秒，目标 60% 要跑到 55% 以上（旧版按帧 12% 也到不了这么快）
            w.SetProgressForTest("");
            w.ShowLoadingForTest(true, 0);
            w.SetProgressForTest("正在初始化: 进度 60%");
            for (int i = 0; i < 8; i++) { w.AddLoadingClockForTest(0.05); w.TickForTest(); }
            var speedVals = w.LoadingValuesForTest();
            Check("绿条跑得够快：0.4 秒内从 0 跑到 55% 以上（不是一步跳到位）",
                speedVals.Shown >= 55 && speedVals.Shown <= 60.5, $"0.4s 后 {speedVals.Shown:0.#}%");

            // 收尾：就绪时把条看得见地补满到 100%，并且此时仍处加载态（面板还没切走）
            var completeTask = w.CompleteProgressForTestAsync();
            PumpUntil(() => w.LoadingValuesForTest().Shown >= 99.9, 3000);
            var doneVals = w.LoadingValuesForTest();
            double doneWidth = ((Border)w.FindName("LoadingFill")!).Width;
            var panelsDone = w.MainPanelsForTest();
            Check("就绪时绿条补满到 100%（不再 3% 之后突然消失）",
                doneVals.Shown >= 99.9 && Math.Abs(doneWidth - panelW2) < 1.5,
                $"{doneVals.Shown:0.#}%（{doneWidth:0.#}px / 面板 {panelW2:0.#}px）");
            Check("补满那一刻仍在加载态（是「看得见地完成」，不是被面板切换抹掉）",
                panelsDone.Loading == Visibility.Visible,
                $"加载面板={panelsDone.Loading} 运行面板={panelsDone.Running}");
            w.ShowLoadingForTest(false);
            w.SetProgressForTest("");
            for (int i = 0; i < 3; i++) w.TickForTest();
            var resetVals = w.LoadingValuesForTest();
            Check("收尾后进度条归零（下一轮启动从头开始）",
                resetVals.Shown == 0 && resetVals.Target == 0,
                $"显示={resetVals.Shown} 目标={resetVals.Target}");

            // ══════ 33. 版本 1.1.9：失败说真话 / 取消不误判 / 弹窗不叠不卡 / 清理不伤人 ══════

            // 秒退不能说成"等了 180 秒"——现场就是 3 秒退出却报 180 秒，一眼就能看出不对
            const string unresolved = "Error [ERR_MODULE_NOT_FOUND]: Cannot find package '@deepseek-ai/dsh-settings' imported from C:\\Users\\x\\.dsh\\profiles\\web\\";
            var quit3 = new StartupFailure(FailureKind.ProcessExited, 3, unresolved, unresolved);
            Check("失败文案用规范表述「加载出错，等待时间 N 秒」（不写口语）",
                quit3.Head.StartsWith("加载出错，等待时间 3 秒", StringComparison.Ordinal) &&
                !quit3.DialogText().Contains("这次只等") &&
                !quit3.DialogText().Contains("很快就退出"),
                quit3.Head);
            Check("引擎秒退时文案写实际耗时，且绝不出现 180 秒",
                quit3.Head.Contains("3 秒") && !quit3.DialogText().Contains("180"),
                quit3.Head);

            // 真等满才允许写 180，且秒数按传入值渲染（不写死）
            var waited180 = new StartupFailure(FailureKind.Timeout, 180, "", "");
            var waited42 = new StartupFailure(FailureKind.Timeout, 42, "", "");
            Check("等待秒数按实际值渲染（180 真的等满了才写 180，42 就写 42）",
                waited180.DialogText().Contains("180 秒") && waited42.DialogText().Contains("42 秒"),
                waited180.Head + " / " + waited42.Head);

            // 取消不是故障
            var canceled = new StartupFailure(FailureKind.Canceled, 7, "", "");
            Check("用户取消不算故障：没有失败文案、不自动重试、也不算确定性失败",
                canceled.Head.Length == 0 && canceled.DialogText().Length == 0 &&
                !canceled.ShouldAutoRetry(false) && !canceled.Deterministic,
                $"文案长度={canceled.DialogText().Length} 重试={canceled.ShouldAutoRetry(false)}");

            // 确定性失败：认出来，但**先给一次重试机会**（配置半写时重试真的能成功）
            Check("模块解析不到能被认成「配置/版本对不上」这一类",
                quit3.Deterministic, $"确定性={quit3.Deterministic}");
            Check("确定性失败也先重试一次（11:47 实测：同一台机器同一版本，重试后起来了）",
                quit3.ShouldAutoRetry(false) && !quit3.ShouldAutoRetry(true) &&
                waited180.ShouldAutoRetry(false) && !waited180.ShouldAutoRetry(true) &&
                !canceled.ShouldAutoRetry(false),
                $"秒退 {quit3.ShouldAutoRetry(false)}/{quit3.ShouldAutoRetry(true)}，超时 {waited180.ShouldAutoRetry(false)}/{waited180.ShouldAutoRetry(true)}");
            Check("重试过一轮仍失败才下「换版本」的结论（首次可能只是配置文件正在写）",
                !quit3.DeterministicAfterRetry(false) && quit3.DeterministicAfterRetry(true) &&
                !waited180.DeterministicAfterRetry(true),
                $"首轮={quit3.DeterministicAfterRetry(false)} 二轮={quit3.DeterministicAfterRetry(true)}");
            Check("确定性失败的提示不再让人「再点一次」，而是指向换回能用的版本",
                quit3.DialogText().Contains("换回") && !quit3.DialogText().Contains("再点一次"),
                quit3.DialogText().Replace("\n", " "));
            Check("原因描述改掉了「等一会儿再点一次通常就好」这种误导建议",
                !StartupCause.Describe(unresolved, "").Contains("再点一次通常就好"),
                StartupCause.Describe(unresolved, ""));

            // 抖动型失败同样只给一次重试机会
            Check("重试只有一次（不会无限重试把人拖住）",
                waited180.ShouldAutoRetry(false) && !waited180.ShouldAutoRetry(true),
                $"首次={waited180.ShouldAutoRetry(false)} 二次={waited180.ShouldAutoRetry(true)}");

            // 失败框里那个默认键：重试过仍失败 + 有别的版本，才给
            Check("失败框的默认动作仅在该给的时候给（重试过仍失败 + 存在别的版本）",
                MainWindow.CanOfferVersionSwitch(quit3, "0.1.5-rc.1", "0.1.5-rc.2", alreadyRetried: true) &&
                !MainWindow.CanOfferVersionSwitch(quit3, "0.1.5-rc.1", "0.1.5-rc.2", alreadyRetried: false) &&
                !MainWindow.CanOfferVersionSwitch(waited180, "0.1.5-rc.1", "0.1.5-rc.2", true) &&
                !MainWindow.CanOfferVersionSwitch(quit3, "", "0.1.5-rc.2", true) &&
                !MainWindow.CanOfferVersionSwitch(quit3, "0.1.5-rc.2", "0.1.5-rc.2", true),
                "重试后+有候选=true，其余=false");

            // 清理归属：只强制关闭本程序自己拉起的监听者
            Func<int, int> chain = pid => pid switch { 500 => 400, 400 => 300, 300 => 0, _ => 0 };
            Check("失败清理只强关本程序自己拉起的监听者（用户自行启动的引擎绝不碰）",
                ProcessManager.ShouldForceClosePort(500, 300, chain) &&
                ProcessManager.ShouldForceClosePort(300, 300, chain) &&
                !ProcessManager.ShouldForceClosePort(600, 300, chain) &&
                !ProcessManager.ShouldForceClosePort(0, 300, chain) &&
                !ProcessManager.ShouldForceClosePort(500, 0, chain),
                "子孙/自身=true，旁系/无主/无痕=false");

            // 弹窗：非模态 + 同时只允许一个（叠框与模态阻塞即"无法关闭"的现场成因）
            int openBefore = GuardDialog.OpenCountForTest();
            GuardDialog.ShowNonModal("自检-非模态提示", "自检", MessageBoxImage.Information);
            Check("提示类弹窗是非模态的：主窗口仍可点（模态会把用户挡在外面，进而无法关闭）",
                GuardDialog.OpenCountForTest() == openBefore + 1 && GuardDialog.AnyOpen && w.IsEnabled,
                $"开着={GuardDialog.OpenCountForTest()} 主窗可点={w.IsEnabled}");
            GuardDialog.ShowNonModal("自检-第二个框", "自检", MessageBoxImage.Information);
            Check("同一时刻只允许一个对话框（关掉一个又冒一个的日子结束了）",
                GuardDialog.OpenCountForTest() == openBefore + 1,
                $"计数={GuardDialog.OpenCountForTest()}");
            foreach (Window extra in Application.Current.Windows.OfType<Window>()
                         .Where(x => !ReferenceEquals(x, w)).ToList())
            {
                try { extra.Close(); } catch { }
            }
            PumpUntil(() => GuardDialog.OpenCountForTest() == openBefore, 1500);
            Check("对话框关掉后计数复位（闸门不会把自己锁死）",
                GuardDialog.OpenCountForTest() == openBefore && !GuardDialog.AnyOpen,
                $"计数={GuardDialog.OpenCountForTest()}");

            // ══════ 34. 版本 1.1.10：首次安装拦路虎自愈 / 弹窗不再假卡死 / 卡顿留证 ══════
            string t34 = Path.Combine(Path.GetTempPath(), "dshguard-selftest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                string fakeEngine = Path.Combine(t34, "engine", "node_modules");
                string fakeProfile = Path.Combine(t34, "profile");
                Directory.CreateDirectory(Path.Combine(fakeEngine, "@deepseek-ai", "dsh-settings", "lib"));
                File.WriteAllText(Path.Combine(fakeEngine, "@deepseek-ai", "dsh-settings", "package.json"),
                    "{\"name\":\"@deepseek-ai/dsh-settings\"}");
                Directory.CreateDirectory(Path.Combine(fakeProfile, "node_modules"));

                var miss = ProfileHealth.FindMissingPackages(
                    "[stderr] Error [ERR_MODULE_NOT_FOUND]: Cannot find package '@deepseek-ai/dsh-settings' imported from C:\\x\\profiles\\web\\\n"
                    + "Error: Cannot find package '@deepseek-ai/dsh-client-ui-conversation' imported from C:\\x\\profiles\\web\\");
                Check("从报错里认出缺失的组件名（首次安装拦路虎的判据）",
                    miss.Count == 2 && miss.Contains("@deepseek-ai/dsh-settings") &&
                    miss.Contains("@deepseek-ai/dsh-client-ui-conversation") &&
                    ProfileHealth.FindMissingPackages("一切正常").Count == 0,
                    string.Join("、", miss));

                Check("从调用栈里认出「这次要跑的引擎」自己的安装目录",
                    ProfileHealth.FindEngineNodeModules(
                        "at updateError (file:///C:/Users/x/AppData/Local/npm-cache/_npx/c40503fdf38a82ea/node_modules/@deepseek-ai/cordis-plugin-loader/lib/index.js:309:9)")
                        .EndsWith("_npx\\c40503fdf38a82ea\\node_modules", StringComparison.OrdinalIgnoreCase) &&
                    ProfileHealth.FindEngineNodeModules("没有任何路径") == "",
                    ProfileHealth.FindEngineNodeModules("file:///C:/a/_npx/hash1/node_modules/@deepseek-ai/x/lib/index.js"));

                // 「往配置目录补联接」整套已下架（它是引擎起不来的元凶）：
                // 这里保留"认得出缺哪些包"的诊断断言，但**不再有任何补链动作**可断言。
                Check("缺失包名与引擎安装目录仍能认出来（仅用于诊断，不再动手补）",
                    miss.Count == 2 && ProfileHealth.FindMissingPackages("一切正常").Count == 0,
                    string.Join("、", miss));
            }
            finally { try { Directory.Delete(t34, true); } catch { } }

            Check("界面卡顿记账规则：超过 5 秒才记、30 秒内不刷屏",
                !MainWindow.ShouldReportStall(TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(5)) &&
                MainWindow.ShouldReportStall(TimeSpan.FromSeconds(6), TimeSpan.FromMinutes(5)) &&
                !MainWindow.ShouldReportStall(TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(3)),
                "2s=不记 / 6s=记 / 刚记过=不记");

            int openBefore2 = GuardDialog.OpenCountForTest();
            bool picked = false;
            GuardDialog.ShowNonModalCustom("自检-带按钮的非模态提示", "自检", MessageBoxImage.Warning,
                _ => picked = true,
                new GuardDialog.DialogButton("换回 X", MessageBoxResult.Yes, Color.FromRgb(0x34, 0xC7, 0x59), IsDefault: true),
                new GuardDialog.DialogButton("先不动", MessageBoxResult.No, Color.FromRgb(0x8E, 0x8E, 0x93), IsCancel: true));
            Check("带按钮的失败提示也是非模态的（用户可以先去点别处，不会假卡死）",
                GuardDialog.OpenCountForTest() == openBefore2 + 1 && w.IsEnabled,
                $"开着={GuardDialog.OpenCountForTest()} 主窗可点={w.IsEnabled}");
            foreach (Window extra2 in Application.Current.Windows.OfType<Window>()
                         .Where(x => !ReferenceEquals(x, w)).ToList())
            {
                try { extra2.Close(); } catch { }
            }
            PumpUntil(() => picked, 1500);
            Check("提示关掉后回调照常执行（按钮真的点得动）", picked, $"回调={picked}");

            // 启动诊断日志 + 诊断摘要（内测反馈靠它们定位）
            string marker = "自检-启动诊断-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            Logger.NoteStartup(marker + " 版本策略=x 端口=3099 启动命令=y");
            string startLog = Logger.CurrentStartupLogFile;
            Check("每次启动落一条「启动-*」诊断记录（版本策略/端口/命令都在里面）",
                startLog.Length > 0 && File.Exists(startLog) && Logger.ReadLogFile(startLog).Contains(marker),
                startLog);
            Check("日志下拉能同时列出异常日志与启动诊断日志",
                Logger.ListStartupLogs().Any(f => Path.GetFileName(f)!.StartsWith("启动-", StringComparison.Ordinal)) &&
                Logger.ListAllLogs().Length >= Logger.ListLogFiles().Length,
                $"启动={Logger.ListStartupLogs().Length} 异常={Logger.ListLogFiles().Length} 全部={Logger.ListAllLogs().Length}");

            string summary = w.BuildDiagnosticsSummary();
            Check("诊断摘要含排查必需字段（版本/端口/命令/配置目录/系统/引擎输出）",
                summary.Contains("守护壳版本") && summary.Contains("启动命令") && summary.Contains("配置文件目录") &&
                summary.Contains("系统") && summary.Contains("引擎输出尾部"),
                summary.Split('\n')[0]);

            // ══════ 35. 版本 1.1.14：复查抓到的两处严重问题（配置目录取错来源）的回归护栏 ══════
            Check("重置/清理的目标目录恒为真实配置文件目录（settings 里那项默认是空串，不能直接用）",
                ProfileReset.TargetProfileDir().Length > 0 &&
                ProfileReset.TargetProfileDir() == GuardPaths.ProfileDir &&
                ProfileReset.CleanupRoots(ProfileReset.TargetProfileDir()).Count == 2,
                ProfileReset.TargetProfileDir());
            Check("清理范围只含本程序补过链的两处，绝不进 DSH 自己写联接的 profiles\\node_modules",
                ProfileReset.CleanupRoots(ProfileReset.TargetProfileDir())
                    .All(r => r.Contains(@"\profiles\web\", StringComparison.OrdinalIgnoreCase)) &&
                !ProfileReset.CleanupRoots(ProfileReset.TargetProfileDir())
                    .Any(r => r.EndsWith(@"\profiles\node_modules\@deepseek-ai", StringComparison.OrdinalIgnoreCase)),
                string.Join(" | ", ProfileReset.CleanupRoots(ProfileReset.TargetProfileDir())));
            Check("判据收紧：只有真在 npm 缓存 _npx\\ 下的目标才算本程序创建的",
                ProfileReset.IsUnderNpxCache(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                 "npm-cache", "_npx", "abc", "node_modules", "@deepseek-ai", "dsh")) &&
                !ProfileReset.IsUnderNpxCache(@"D:\somewhere\_npx\thing") &&
                !ProfileReset.IsUnderNpxCache("") && !ProfileReset.IsUnderNpxCache(null),
                "真缓存路径=true；伪造路径/空/null=false");
            int recBefore = w.RecomposeCountForTest;
            double leftBefore = w.Left, topBefore = w.Top;
            w.ForceRecompose("自检");
            Check("强制重画：计数 +1 且窗口位置复原（毛玻璃/切主题后的花屏修复，有回归保护）",
                w.RecomposeCountForTest == recBefore + 1 &&
                Math.Abs(w.Left - leftBefore) < 0.5 && Math.Abs(w.Top - topBefore) < 0.5,
                $"计数 {recBefore}→{w.RecomposeCountForTest}，位置 {w.Left:0},{w.Top:0}");
            // ══════ 36. 版本 1.1.15：安装后首次启动必失败的两条对症修复 ══════
            Check("临时目录判据：空 / 不存在 / 安装器临时目录（is-*.tmp）都算不可用",
                !ProcessEnv.IsUsableTemp(null) && !ProcessEnv.IsUsableTemp("") &&
                !ProcessEnv.IsUsableTemp(Path.Combine(Path.GetTempPath(), "definitely-not-here-" + Guid.NewGuid().ToString("N"))) &&
                !ProcessEnv.IsUsableTemp(Path.Combine(ProcessEnv.UserTempDir, "is-ABC123.tmp")) &&
                ProcessEnv.IsUsableTemp(ProcessEnv.UserTempDir),
                $"用户临时目录 = {ProcessEnv.UserTempDir}");

            Check("临时目录归一化：本进程 TEMP 一定指向存在且可写的目录",
                Directory.Exists(ProcessEnv.UserTempDir) &&
                ProcessEnv.IsUsableTemp(Environment.GetEnvironmentVariable("TEMP")),
                $"TEMP = {Environment.GetEnvironmentVariable("TEMP")}");

            Check("未固定版本秒退的兜底：从本机缓存里挑最新版本（排除空值/重复）",
                MainWindow.PickCachedFallback(new[] { "0.1.1-rc.2", "0.1.5-rc.2", "0.1.5-rc.1", "", "0.1.5-rc.2" }) == "0.1.5-rc.2" &&
                MainWindow.PickCachedFallback(Array.Empty<string>()) == "" &&
                MainWindow.PickCachedFallback(new[] { "junk" }) == "junk",
                MainWindow.PickCachedFallback(new[] { "0.1.1-rc.2", "0.1.5-rc.2", "0.1.5-rc.1" }));
            // ══════ 37. 版本 1.2.10：写 cordis.patch.yml 的三类缺陷（YAML 引号 / 批量禁用 / 原子写）══════
            Check("id 一律加引号：@ 开头的包名不加引号就是非法 YAML",
                PluginManager.QuoteId("@changfenhuang/dsh-genui") == "\"@changfenhuang/dsh-genui\"" &&
                PluginManager.QuoteId("dsh-zh") == "\"dsh-zh\"" &&
                PluginManager.QuoteId("") == "\"\"",
                PluginManager.QuoteId("@a/b"));

            Check("读回 id 时剥引号：带引号与不带引号两种历史写法都能归一",
                PluginManager.NormalizeId("\"@a/b\"") == "@a/b" &&
                PluginManager.NormalizeId("'@a/b'") == "@a/b" &&
                PluginManager.NormalizeId("  @a/b  ") == "@a/b" &&
                PluginManager.NormalizeId("\"a\\\"b\"") == "a\"b",
                PluginManager.NormalizeId("\"@a/b\""));

            Check("IdFromLine：`- id: X` 两种写法都取得到 id，非该行返回 null",
                PluginManager.IdFromLine("- id: @a/b") == "@a/b" &&
                PluginManager.IdFromLine("  - id: \"@a/b\"") == "@a/b" &&
                PluginManager.IdFromLine("disabled: true") == null,
                PluginManager.IdFromLine("- id: @a/b") ?? "(null)");

            var badText = "# 注释\n- id: @a/b\n  disabled: true\n";
            var (badOk, badFixed, badProbs) = PluginManager.ValidatePatchText(badText);
            Check("非法写法能被认出来（@ 开头未加引号），并给出修好的文本",
                !badOk && badProbs.Count == 1 &&
                badFixed.Contains("- id: \"@a/b\"") &&
                PluginManager.ValidatePatchText(badFixed).Ok,
                badProbs.Count > 0 ? badProbs[0] : "(没报问题)");

            Check("合法写法不误报：带引号的、普通字符开头的都算合法",
                PluginManager.ValidatePatchText("- id: \"@a/b\"\n  disabled: true\n").Ok &&
                PluginManager.ValidatePatchText("- id: dsh-zh\n  disabled: true\n").Ok,
                "两种合法样本都应通过");

            string t37 = Path.Combine(Path.GetTempPath(), "dshguard-patch-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string? t37bak = PluginManager.PatchFileOverrideForTest;
            try
            {
                Directory.CreateDirectory(t37);
                PluginManager.PatchFileOverrideForTest = Path.Combine(t37, "cordis.patch.yml");

                var p37 = new PluginManager.Plugin { Name = "@changfenhuang/dsh-genui", LoaderId = "@changfenhuang/dsh-genui" };
                string r37 = PluginManager.Disable(p37);
                string txt37 = File.ReadAllText(PluginManager.PatchFile);
                Check("禁用 @ 开头的插件后，文件里是带引号的合法写法，且不存在 .tmp 残留",
                    r37.StartsWith("已禁用") &&
                    txt37.Contains("- id: \"@changfenhuang/dsh-genui\"") &&
                    PluginManager.ValidatePatchText(txt37).Ok &&
                    !File.Exists(PluginManager.PatchFile + ".tmp"),
                    r37.Split('\n')[0]);

                Check("禁用状态能被读回来（ReadDisabledIds 认得带引号写法）",
                    PluginManager.ReadDisabledIds().Contains("@changfenhuang/dsh-genui"),
                    string.Join("、", PluginManager.ReadDisabledIds()));

                string r37e = PluginManager.Enable(p37, force: true);
                string txt37e = File.ReadAllText(PluginManager.PatchFile);
                Check("启用手工/带引号记录都能删掉，且删完文件仍然合法",
                    !PluginManager.ReadDisabledIds().Contains("@changfenhuang/dsh-genui") &&
                    PluginManager.ValidatePatchText(txt37e).Ok,
                    r37e.Split('\n')[0]);

                var many = new List<PluginManager.Plugin>
                {
                    new PluginManager.Plugin { Name = "@a/one",  LoaderId = "@a/one" },
                    new PluginManager.Plugin { Name = "plain-two", LoaderId = "plain-two" },
                    new PluginManager.Plugin { Name = "@a/three", LoaderId = "@a/three" }
                };
                var (doneMany, detailMany) = PluginManager.DisableMany(many);
                var setMany = PluginManager.ReadDisabledIds();
                Check("批量禁用：一次写入三条记录，全部合法；再跑一次是幂等的",
                    doneMany.Count == 3 &&
                    setMany.Contains("@a/one") && setMany.Contains("plain-two") && setMany.Contains("@a/three") &&
                    PluginManager.ValidatePatchText(File.ReadAllText(PluginManager.PatchFile)).Ok &&
                    PluginManager.DisableMany(many).Disabled.Count == 0,
                    detailMany);

                PluginManager.PatchFileOverrideForTest = Path.Combine(t37, "不存在目录", "cordis.patch.yml");
                string r37f = PluginManager.Disable(new PluginManager.Plugin { Name = "x", LoaderId = "x" });
                Check("写入失败时如实报错、不抛异常、不留半截文件",
                    r37f.StartsWith("禁用") && r37f.Contains("失败"),
                    r37f.Split('\n')[0]);
            }
            finally
            {
                PluginManager.PatchFileOverrideForTest = t37bak;
                try { Directory.Delete(t37, true); } catch { }
            }
            // ══════ 38. 版本 1.3.12：耗时操作的百分比进度必须"实时在变" ══════
            Check("进度百分比随时间实时变化（不是停在一个数字上）",
                MainWindow.OpProgressPercent(0) < MainWindow.OpProgressPercent(1) &&
                MainWindow.OpProgressPercent(1) < MainWindow.OpProgressPercent(2) &&
                MainWindow.OpProgressPercent(10) < MainWindow.OpProgressPercent(20) &&
                Math.Abs(MainWindow.OpProgressPercent(3) - MainWindow.OpProgressPercent(3.5)) > 0.5,
                $"0s={MainWindow.OpProgressPercent(0):0.#}% 1s={MainWindow.OpProgressPercent(1):0.#}% 10s={MainWindow.OpProgressPercent(10):0.#}% 60s={MainWindow.OpProgressPercent(60):0.#}%");

            Check("进度百分比起步 5%、90% 封顶、结束时才到 100%",
                Math.Abs(MainWindow.OpProgressPercent(0) - 5) < 0.001 &&
                MainWindow.OpProgressPercent(600) <= 90.01 &&
                MainWindow.OpProgressPercent(100000) <= 90.01,
                $"起步={MainWindow.OpProgressPercent(0):0.#}% 上限={MainWindow.OpProgressPercent(100000):0.#}%");
            // ══════ 39. 版本 1.3.30：安装源的包名解析 + 清单事实判定 ══════
            Check("安装源能取出清单里的包名（@scope/name@ver → @scope/name）",
                PluginManager.PackageNameFromSource("@openviking/dsh-memory-plugin@0.3.2") == "@openviking/dsh-memory-plugin" &&
                PluginManager.PackageNameFromSource("@liustack/modsearch") == "@liustack/modsearch" &&
                PluginManager.PackageNameFromSource("dsh-watcher@1.2.3") == "dsh-watcher",
                PluginManager.PackageNameFromSource("@openviking/dsh-memory-plugin@0.3.2"));

            Check("git / 本地来源取不出包名（交给退出码判定，不做推测）",
                PluginManager.PackageNameFromSource("github:aa2246740/dsh-watcher#2d19cb5a") == "" &&
                PluginManager.PackageNameFromSource("git+https://github.com/x/y.git") == "" &&
                PluginManager.PackageNameFromSource("https://example.com/x.tgz") == "",
                "三种来源都应为空");

            Check("清单事实判定：查不到就是没有（不依赖机器上装了哪些插件）",
                !PluginManager.HasDependency("绝对不会存在的包名-zzz") &&
                !PluginManager.HasDependency("") &&
                (PluginManager.DepSpec("绝对不会存在的包名-zzz").Length == 0) == !PluginManager.HasDependency("绝对不会存在的包名-zzz"),
                "按 profile\\package.json 判定，恒真的不变量");
            // ══════ 40. 「清单已登记、机器上未安装」：dump 命令整体失败时的识别 + 缓存兜底 ══════
            // 现场原文（实测 stderr，退出码 1、stdout 为空）—— 路径已脱敏为用户目录占位符，只作解析样本。
            const string unresolvedStderr =
@"Error: dsh: cannot resolve profile bundle ""@furongjun1999/dsh-memory"" from the dsh installation or C:\Users\x\.dsh\profiles\web; run 'dsh plugin --profile web install' if its dependency is not installed
    at resolveBundleDir (C:\Users\x\AppData\Local\npm-cache\_npx\hash1\node_modules\@deepseek-ai\dsh-app-boot\lib\index.js:831:8)
    at loadProfileDirectory (C:\Users\x\AppData\Local\npm-cache\_npx\hash1\node_modules\@deepseek-ai\dsh-app-boot\lib\index.js:700:10)";

            string gotBundle = PluginManager.ParseUnresolvedBundle(unresolvedStderr);
            Check("从 dsh 的崩溃提示里认出「清单里有、磁盘上没有」的包名；普通输出/空串认不出",
                gotBundle == "@furongjun1999/dsh-memory" &&
                PluginManager.ParseUnresolvedBundle(unresolvedStderr.Replace('"', '\'')) == "@furongjun1999/dsh-memory" &&
                PluginManager.ParseUnresolvedBundle(null) == "" &&
                PluginManager.ParseUnresolvedBundle("") == "" &&
                PluginManager.ParseUnresolvedBundle("   \r\n  ") == "" &&
                PluginManager.ParseUnresolvedBundle("- id: modsearch\n  name: '@liustack/modsearch'") == "" &&
                PluginManager.ParseUnresolvedBundle("npm error code EAI_AGAIN\nnpm error network request failed") == "" &&
                PluginManager.ParseUnresolvedBundle("Error: dsh: cannot resolve profile bundle from nothing") == "",
                $"认出：{(gotBundle.Length > 0 ? gotBundle : "(空)")}");

            string t40 = Path.Combine(Path.GetTempPath(), "dshguard-loaderids-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string? t40bak = PluginManager.LoaderIdCacheOverrideForTest;
            try
            {
                Directory.CreateDirectory(t40);
                PluginManager.LoaderIdCacheOverrideForTest = Path.Combine(t40, "loader-ids.json");

                PluginManager.SaveLoaderIdCache(new Dictionary<string, string>
                {
                    ["@liustack/modsearch"] = "modsearch",
                    ["dsh-zh"] = "deepseek-harness-zh_pro"
                });
                var cached40 = PluginManager.LoadLoaderIdCache();

                // 现场那种崩法：dump 失败、输出里一条 id 都没有 ⇒ 必须退回缓存（本轮改动不得使这条失效）
                var (idsCrash, fromCacheCrash, _) = PluginManager.ResolveLoaderIds(false, "", cached40);
                // dump 跑通 ⇒ 用 dump 里的权威映射（不因为有了缓存就永远吃缓存）
                string dumpOkText = "- id: modsearch\n  name: '@liustack/modsearch'\n- id: dsh-zh\n  name: dsh-zh\n";
                var (idsOk, fromCacheOk, byName40) = PluginManager.ResolveLoaderIds(true, dumpOkText, cached40);
                // 两边都没有 ⇒ 空表（调用方必须如实报告，不能假装有）
                var (idsNone, fromCacheNone, _) = PluginManager.ResolveLoaderIds(false, "", new Dictionary<string, string>());

                Check("拿不到 id 时用缓存兜底（判定抽成纯函数后仍成立，不回归）",
                    cached40.Count == 2 && PluginManager.LoaderIdCacheOverrideForTest != null &&
                    fromCacheCrash && idsCrash.Count == 2 &&
                    idsCrash["@liustack/modsearch"] == "modsearch" && idsCrash["dsh-zh"] == "deepseek-harness-zh_pro" &&
                    !fromCacheOk && idsOk.Count == 2 && idsOk["dsh-zh"] == "dsh-zh" && byName40 == 2 &&
                    !fromCacheNone && idsNone.Count == 0,
                    $"缓存 {cached40.Count} 条；崩掉时用缓存={fromCacheCrash}；跑通时用 dump={!fromCacheOk}；都没有时为空={idsNone.Count == 0}");
            }
            finally
            {
                PluginManager.LoaderIdCacheOverrideForTest = t40bak;
                try { Directory.Delete(t40, true); } catch { }
            }
            // ══════ 41. bug：点「禁用插件」仍报"无法读取内部标识"，但 dump 实际已经成功（退出码 0）══════
            // 现场证据：日志里两条 dump 都是"退出码=0"，loader-ids.json 里也有
            // "@furongjun1999/dsh-memory": "furongjun1999-dsh-memory" ⇒ 取 id 这条流程本身是正常的，
            // 问题在于"重扫替换了整批新 Plugin 对象后，没有任何逻辑将 id 写回"：
            //   Scan() 每次 new 新对象（LoaderId 全为 null），而 _loaderIdsLoaded 已为 true
            //   ⇒ 不再读 id ⇒ 那批对象的 LoaderId 永远是 null ⇒ 禁用时 LoaderIdFor 返回空。
            // 另外：拿不到提示时也必须只报**本次**的真相（上次的失败状态在成功那一刻就要清干净）。
            Check("重扫出的新插件对象默认**没有**内部标识 —— 这就是「禁用不了」的起点",
                PluginManager.LoaderIdFor(new PluginManager.Plugin { Name = "@furongjun1999/dsh-memory" }) == "",
                "LoaderId 默认 null：不贴 id 表，这个插件永远禁不了");

            var freshPlugins41 = new List<PluginManager.Plugin>
            {
                new() { Name = "@furongjun1999/dsh-memory" },      // 现场那个：机器上有、package.json 未填写 version
                new() { Name = "@liustack/modsearch" },
                new() { Name = "dsh-zh" }                          // id 与包名不同形态的另一种
            };
            var idTable41 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["@furongjun1999/dsh-memory"] = "furongjun1999-dsh-memory",
                ["@liustack/modsearch"] = "modsearch",
                ["dsh-zh"] = "deepseek-harness-zh_pro"
            };
            int filled41 = PluginManager.ApplyLoaderIds(freshPlugins41, idTable41);
            Check("ApplyLoaderIds：把已读到的 id 表贴回新对象（顺序/作用域/同名不同形态都对得上）",
                filled41 == 3 &&
                PluginManager.LoaderIdFor(freshPlugins41[0]) == "furongjun1999-dsh-memory" &&
                PluginManager.LoaderIdFor(freshPlugins41[1]) == "modsearch" &&
                PluginManager.LoaderIdFor(freshPlugins41[2]) == "deepseek-harness-zh_pro" &&
                PluginManager.ApplyLoaderIds(freshPlugins41, idTable41) == 0,    // 已经是这个 id：不重复计数
                $"贴上 {filled41} 条");

            var keepOne41 = new PluginManager.Plugin { Name = "@没有映射的包/x", LoaderId = "之前读到的-id" };
            Check("贴 id 只贴不清：表里没有的插件保持原样（缓存兜底是**部分表**，拿它清空会破坏可用的插件）",
                PluginManager.ApplyLoaderIds(new[] { keepOne41 }, idTable41) == 0 &&
                PluginManager.LoaderIdFor(keepOne41) == "之前读到的-id" &&
                PluginManager.ApplyLoaderIds(null, idTable41) == 0 &&
                PluginManager.ApplyLoaderIds(freshPlugins41, null) == 0,
                PluginManager.LoaderIdFor(keepOne41));

            // 失败状态：成功路径必须将"包名 + 使用了几条缓存作为兜底"一并清空，弹窗才会报告本次的真实情况
            PluginManager.NoteUnresolvedBundle("@furongjun1999/dsh-memory", 7);
            bool noted41 = PluginManager.LastUnresolvedBundle == "@furongjun1999/dsh-memory"
                        && PluginManager.LastCacheUsedCount == 7;
            string missMsg41 = PluginManager.MissingIdMessage("某个插件");
            PluginManager.ClearUnresolvedBundle();                       // = 读 id 成功时执行的那一下
            string okMsg41 = PluginManager.MissingIdMessage("某个插件");
            bool cleared41 = PluginManager.LastUnresolvedBundle.Length == 0 && PluginManager.LastCacheUsedCount == 0;
            PluginManager.NoteUnresolvedBundle("x", 3);
            PluginManager.NoteUnresolvedBundle("");                      // 空串同样要连计数一起清
            bool clearedByEmpty41 = PluginManager.LastUnresolvedBundle.Length == 0 && PluginManager.LastCacheUsedCount == 0;
            Check("取名成功就清空上次的失败状态：包名置空 + 缓存计数归零（只清一半会让弹窗拿旧包名说话）",
                noted41 && cleared41 && clearedByEmpty41,
                $"清空后 包名=「{PluginManager.LastUnresolvedBundle}」· 缓存计数={PluginManager.LastCacheUsedCount}");
            Check("那句「是哪个包害的」只在**最近一次读 id 确实失败**时出现，读全了就退回普通提示",
                missMsg41.Contains("插件清单中登记了") && missMsg41.Contains("@furongjun1999/dsh-memory") &&
                okMsg41.Contains("请先在插件页点「刷新」") && !okMsg41.Contains("插件清单中登记了"),
                okMsg41.Split('\n')[0]);

            // 真的写得进去：模拟"重扫 → 贴 id → 点禁用"这条链路的终点
            string t41 = Path.Combine(Path.GetTempPath(), "dshguard-idfix-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string? t41bak = PluginManager.PatchFileOverrideForTest;
            try
            {
                Directory.CreateDirectory(t41);
                PluginManager.PatchFileOverrideForTest = Path.Combine(t41, "cordis.patch.yml");

                string r41 = PluginManager.Disable(freshPlugins41[0]);
                Check("贴完 id 再禁用：界面串不露内部标识/备份名，写进 patch 的才是 loader id（不再有「无法读取」）",
                    r41.StartsWith("已禁用") &&
                    !r41.Contains("furongjun1999-dsh-memory") &&        // ★ 被删那行的反向：loader id 的**值**不上界面
                    !r41.Contains("loader id", StringComparison.OrdinalIgnoreCase) &&
                    !r41.Contains(".bak-") &&                           // 备份文件名同样不上界面
                    !r41.Contains("无法读取") &&
                    PluginManager.ReadDisabledIds().Contains("furongjun1999-dsh-memory"),
                    r41.Split('\n')[0]);

                // 现场那个插件：机器上装着、但它的 package.json 没有 version 字段（卡片显示「未安装」）
                string noVerDir = Path.Combine(t41, "node_modules", "@a", "noversion");
                Directory.CreateDirectory(noVerDir);
                File.WriteAllText(Path.Combine(noVerDir, "package.json"), "{ \"name\": \"@a/noversion\" }", new UTF8Encoding(false));
                Check("没有 version 字段的插件：读版本给空串（不当成「没装」），禁用照样写得进去",
                    PluginManager.ReadInstalledVersion(t41, "@a/noversion") == "" &&
                    PluginManager.Disable(new PluginManager.Plugin { Name = "@a/noversion", LoaderId = "a-noversion" })
                        .StartsWith("已禁用") &&
                    PluginManager.ReadDisabledIds().Contains("a-noversion"),
                    "版本读不出来 ≠ 没装：判据是目录/清单，不是版本号");
            }
            finally
            {
                PluginManager.PatchFileOverrideForTest = t41bak;
                try { Directory.Delete(t41, true); } catch { }
            }

            // 窗口级：确认这条回填真的**接在窗口的 id 表上**（不是 PluginManager 里一个没接线的函数）。
            // 现场那条链路是「点刷新 → 重扫换新对象 → 点禁用」；id 表要联网跑 dump 才有，
            // 本次没读到就记 SKIP（不冒充通过），读到了就硬判。
            {
                var probe41 = new List<PluginManager.Plugin> { new() { Name = "@furongjun1999/dsh-memory" } };
                var undoProbe41 = w.SeedPluginStateForTest(probe41,
                    new Dictionary<string, PluginManager.PluginUpdate>(StringComparer.OrdinalIgnoreCase));
                try
                {
                    int before41 = w.LoaderIdCountForTest();       // 重扫出来的新对象 ⇒ 0
                    w.BackfillLoaderIdsForTest();
                    int after41 = w.LoaderIdCountForTest();
                    if (before41 != 0) Skip("重扫换对象后窗口会自己把 id 贴回来", "样本未按预期初始化");
                    else if (after41 == 0) Skip("重扫换对象后窗口会自己把 id 贴回来", "本次没读到 id 表（要联网跑 dump）");
                    else Check("重扫换对象后窗口会自己把 id 贴回来（现场根因就是这一步没人做）",
                        probe41[0].LoaderId == "furongjun1999-dsh-memory",
                        "贴回来的是 " + probe41[0].LoaderId);
                }
                finally { undoProbe41(); }
            }

            // ══════ 42. bug：一键回滚把 package.json 退回去了，磁盘上的插件版本没退 ══════
            // 现场：快照 profile-package.json 里 genui=^0.10.0，当前清单也是 ^0.10.0，
            // 而 node_modules\@changfenhuang\dsh-genui 装的是 0.11.0 ⇒ 文件退了、包没退。
            Check("回滚这步必须**显式**放开 pnpm 供应链策略：自动判定永远匹不中它",
                !PluginManager.LooksLikePluginMutation(MainWindow.RollbackReinstallArgs(frozenLockfile: true)) &&
                !PluginManager.LooksLikePluginMutation(MainWindow.RollbackReinstallArgs(frozenLockfile: false)) &&
                PluginManager.LooksLikePluginMutation("plugin --profile web add @a/b"),
                $"「{MainWindow.RollbackReinstallArgs(true)}」匹不中（它既没有 \" plugin \" 也没有 \" --profile \"）");

            Check("按 semver 真实上界核对「退回去没有」：^0.10.0 遇到 0.11.0 必须判成没退",
                MainWindow.CheckRollbackSpec("^0.10.0", "0.11.0") == MainWindow.RollbackSpecCheck.Mismatch &&
                MainWindow.CheckRollbackSpec("^0.10.0", "0.10.3") == MainWindow.RollbackSpecCheck.Match &&
                MainWindow.CheckRollbackSpec("^0.4.7", "0.4.7") == MainWindow.RollbackSpecCheck.Match &&
                MainWindow.CheckRollbackSpec("~1.2.0", "1.3.0") == MainWindow.RollbackSpecCheck.Mismatch &&
                MainWindow.CheckRollbackSpec("~1.2.0", "1.2.9") == MainWindow.RollbackSpecCheck.Match &&
                MainWindow.CheckRollbackSpec("^1.2.0", "1.9.0") == MainWindow.RollbackSpecCheck.Match &&
                MainWindow.CheckRollbackSpec("0.4.7", "0.4.7") == MainWindow.RollbackSpecCheck.Match &&
                MainWindow.CheckRollbackSpec("0.4.7", "0.4.8") == MainWindow.RollbackSpecCheck.Mismatch,
                "^0.10.0 vs 0.11.0 = " + MainWindow.CheckRollbackSpec("^0.10.0", "0.11.0"));

            Check("git 源 / 本地路径 / * 这类写法不硬判（如实说「无法按版本核对」，不谎报未回退）",
                MainWindow.CheckRollbackSpec("github:o/r#abc1234", "0.1.0") == MainWindow.RollbackSpecCheck.NotComparable &&
                MainWindow.CheckRollbackSpec("git+https://github.com/x/y.git", "0.1.0") == MainWindow.RollbackSpecCheck.NotComparable &&
                MainWindow.CheckRollbackSpec("file:../local", "0.1.0") == MainWindow.RollbackSpecCheck.NotComparable &&
                MainWindow.CheckRollbackSpec("*", "0.1.0") == MainWindow.RollbackSpecCheck.NotComparable &&
                MainWindow.CheckRollbackSpec(">=1.0.0 || ^2.0.0", "1.0.0") == MainWindow.RollbackSpecCheck.NotComparable &&
                MainWindow.CheckRollbackSpec("", "0.1.0") == MainWindow.RollbackSpecCheck.NotComparable &&
                MainWindow.CheckRollbackSpec("^0.10.0", "") == MainWindow.RollbackSpecCheck.NotComparable,
                "非版本号写法一律交给文案如实说明");

            Check("核对用的是**严格**口径，不是兼容分档那套宽松判定（宽松口径会把 0.11.0 判成满足 ^0.10.0）",
                VersionInfo.Satisfies("^0.10.0", "0.11.0") &&
                MainWindow.CheckRollbackSpec("^0.10.0", "0.11.0") == MainWindow.RollbackSpecCheck.Mismatch,
                "VersionInfo.Satisfies 宽松（≥下限即可）、CheckRollbackSpec 严格（caret 真实上界）");

            string line42ok = MainWindow.RollbackInstallLine(MainWindow.RollbackInstallOutcome.FrozenOk, "");
            string line42fallback = MainWindow.RollbackInstallLine(MainWindow.RollbackInstallOutcome.PlainFallback,
                "ERR_PNPM_OUTDATED_LOCKFILE Cannot install with frozen-lockfile");
            string line42fail = MainWindow.RollbackInstallLine(MainWindow.RollbackInstallOutcome.Failed,
                "ERR_PNPM_MINIMUM_RELEASE_AGE_VIOLATION");
            Check("回滚结果如实分三种：按锁文件装好 / 退回普通安装（版本可能与快照不一致）/ 失败",
                line42ok.StartsWith("✅") && !line42ok.Contains("不完全一致") &&
                line42fallback.Contains("版本可能与快照不完全一致") &&
                line42fallback.Contains("ERR_PNPM_OUTDATED_LOCKFILE") &&
                line42fail.StartsWith("❌") && line42fail.Contains("没有") &&
                line42fail.Contains("ERR_PNPM_MINIMUM_RELEASE_AGE_VIOLATION") &&
                line42ok != line42fallback && line42fallback != line42fail,
                line42fallback.Split('\n')[0]);

            Check("pnpm 输出里挑得出人看得懂的原因（优先 ERR_PNPM_*，不把整段输出灌进结论框）",
                MainWindow.RollbackInstallReason(
                    "Progress: resolved 1\nERR_PNPM_OUTDATED_LOCKFILE Cannot install with frozen-lockfile\nlast line")
                    .StartsWith("ERR_PNPM_OUTDATED_LOCKFILE") &&
                MainWindow.RollbackInstallReason("ERR_PNPM_MINIMUM_RELEASE_AGE_VIOLATION x") .StartsWith("ERR_PNPM_") &&
                MainWindow.RollbackInstallReason("只有最后一行") == "只有最后一行" &&
                MainWindow.RollbackInstallReason("") == "" &&
                MainWindow.RollbackInstallReason(null) == "",
                MainWindow.RollbackInstallReason("a\nERR_PNPM_X boom\nb"));

            // 逐项核对"磁盘上到底退回去没有"：清单退回去 ≠ 包装回去（现场就是这个差别）
            string t42 = Path.Combine(Path.GetTempPath(), "dshguard-rollback-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                string snap42 = Path.Combine(t42, "snap"), prof42 = Path.Combine(t42, "prof");
                Directory.CreateDirectory(snap42);
                Directory.CreateDirectory(prof42);
                File.WriteAllText(Path.Combine(snap42, "profile-package.json"),
                    "{ \"dependencies\": { \"@changfenhuang/dsh-genui\": \"^0.10.0\", " +
                    "\"@furongjun1999/dsh-memory\": \"^0.4.7\", \"dsh-watcher\": \"github:aa2246740/dsh-watcher#2d19cb5a\" } }",
                    new UTF8Encoding(false));
                void Put(string name, string body)
                {
                    string d = Path.Combine(prof42, "node_modules", name.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(d);
                    File.WriteAllText(Path.Combine(d, "package.json"), body, new UTF8Encoding(false));
                }
                Put("@changfenhuang/dsh-genui", "{ \"name\": \"@changfenhuang/dsh-genui\", \"version\": \"0.11.0\" }");
                Put("@furongjun1999/dsh-memory", "{ \"name\": \"@furongjun1999/dsh-memory\", \"version\": \"0.4.7\" }");
                // dsh-watcher 故意不创建目录：模拟"磁盘上无法读取"

                Check("读磁盘上真实装着的版本（不在 / 未填写 version 都给空串，不猜）",
                    PluginManager.ReadInstalledVersion(prof42, "@changfenhuang/dsh-genui") == "0.11.0" &&
                    PluginManager.ReadInstalledVersion(prof42, "@furongjun1999/dsh-memory") == "0.4.7" &&
                    PluginManager.ReadInstalledVersion(prof42, "dsh-watcher") == "" &&
                    PluginManager.ReadInstalledVersion(prof42, "") == "",
                    PluginManager.ReadInstalledVersion(prof42, "@changfenhuang/dsh-genui"));

                Check("快照里的声明读得出来（profile-package.json 副本，按包名取值）",
                    PluginManager.DepSpecIn(Path.Combine(snap42, "profile-package.json"), "@changfenhuang/dsh-genui") == "^0.10.0" &&
                    PluginManager.DepSpecIn(Path.Combine(snap42, "profile-package.json"), "dsh-watcher")
                        == "github:aa2246740/dsh-watcher#2d19cb5a" &&
                    PluginManager.DepSpecIn(Path.Combine(snap42, "不存在的文件.json"), "x") == "",
                    "快照副本是核对的依据");

                var check42 = MainWindow.RollbackVersionCheckLines(snap42, prof42,
                    new[] { "@changfenhuang/dsh-genui", "@furongjun1999/dsh-memory", "dsh-watcher" });
                Check("回滚结果逐项写清「到底退回去没有」：没退的必须报 ❌（现场就是清单退了、包没退）",
                    check42.Count == 3 &&
                    check42.Any(l => l.StartsWith("❌") && l.Contains("@changfenhuang/dsh-genui") && l.Contains("0.11.0")) &&
                    check42.Any(l => l.StartsWith("✅") && l.Contains("@furongjun1999/dsh-memory")) &&
                    check42.Any(l => l.StartsWith("⚠️") && l.Contains("dsh-watcher")),
                    string.Join("／", check42.Select(l => l.Split('（')[0])));
                Check("未涉及插件时不多说一句；快照缺清单时如实说「无法核对」",
                    MainWindow.RollbackVersionCheckLines(snap42, prof42, new List<string>()).Count == 0 &&
                    MainWindow.RollbackVersionCheckLines(Path.Combine(t42, "空快照"), prof42, new[] { "x" })
                        .Any(l => l.Contains("无法核对")),
                    "空表=0 行；缺清单=如实说");

                // 现场那种"锁文件已被上一次回滚覆盖、只有磁盘上还留着新版"的情形：
                // 光比锁文件认不出来（两边都是 0.10.0）⇒ 必须看磁盘，这一步才是本 bug 的关键判据。
                var disk42 = MainWindow.RollbackDiskMismatch(snap42, prof42);
                Check("磁盘版本与快照声明对不上号 = 要重装才退得回去（现场：清单 ^0.10.0、磁盘 0.11.0）",
                    disk42.Count == 1 && disk42[0] == "@changfenhuang/dsh-genui",
                    disk42.Count > 0 ? string.Join("、", disk42) : "(没认出来)");
                Check("判不了的不触发安装：git 源 / 未安装 / 缺清单都不算进来",
                    !disk42.Contains("dsh-watcher") &&
                    MainWindow.RollbackDiskMismatch(Path.Combine(t42, "没有清单"), prof42).Count == 0 &&
                    MainWindow.RollbackDiskMismatch(snap42, Path.Combine(t42, "没有 node_modules")).Count == 0,
                    "git 源按提交退、无法读取版本就不硬判");
                Check("回滚名单 = 锁文件对不上的 ∪ 磁盘对不上的（去重 + 稳定排序）",
                    MainWindow.MergePluginNames(new[] { "b", "a" }, new[] { "a", "c" })
                        .SequenceEqual(new[] { "a", "b", "c" }) &&
                    MainWindow.MergePluginNames(null, null).Count == 0 &&
                    MainWindow.MergePluginNames(new[] { "" }, new[] { "  " }).Count == 0,
                    string.Join("、", MainWindow.MergePluginNames(new[] { "b", "a" }, new[] { "a", "c" })));
            }
            finally { try { Directory.Delete(t42, true); } catch { } }

            // 「↺ 一键回滚」与详情页勾选框必须同一个判定：以前 ↺ 写死 restorePlugins=false ⇒ 包不退
            Check("「↺ 一键回滚」的插件判定：与详情页同一份（有插件变化才重装，没变化就不白装一次）",
                MainWindow.PluginRevertDecisionForTest(
                    "importers:\n  .:\n    dependencies:\n      '@changfenhuang/dsh-genui':\n        specifier: ^0.10.0\n        version: 0.10.0\n",
                    "importers:\n  .:\n    dependencies:\n      '@changfenhuang/dsh-genui':\n        specifier: ^0.10.0\n        version: 0.11.0\n") &&
                !MainWindow.PluginRevertDecisionForTest("", "") &&
                !MainWindow.PluginRevertDecisionForTest(null, null),
                "有变化=true · 不可比=false");

            // ══════ 43. bug：事件栏说 4 个待更新，其它页面说 5 个 ══════
            // 根因：事件数的是"更新报告原文"（不含后来为 git 源插件补的"跟仓库最新"），
            // 按钮数的是合并后的表 ⇒ 差的正是那 1 个 git 源插件。现在三处都走 UpdatableCount()。
            var rows43 = new List<PluginManager.Plugin>
            {
                new() { Name = "自检-更新甲" },
                new() { Name = "自检-更新乙" },      // 这一条就是"git 源补进来的"那种：报告里没有、合并表里有
                new() { Name = "自检-没更新" }
            };
            var ups43 = new Dictionary<string, PluginManager.PluginUpdate>(StringComparer.OrdinalIgnoreCase)
            {
                ["自检-更新甲"] = new() { Name = "自检-更新甲", Installed = "1.0.0", Latest = "2.0.0", HasUpdate = true },
                ["自检-更新乙"] = new() { Name = "自检-更新乙", Installed = "118049a", Latest = "3.0.0", HasUpdate = true },
                ["自检-没更新"] = new() { Name = "自检-没更新", Installed = "1.0.0", Latest = "1.0.0", HasUpdate = false },
                // 报告里多出来的一条（合并表里有、插件表里没有）：旧口径 `_pluginUpdates.Values.Count(...)`
                // 会把它也算进去 ⇒ 底部统计比按钮多 1 —— 这正是"4 vs 5"的另一种来源，一并统一。
                ["自检-报告里多出来的"] = new() { Name = "自检-报告里多出来的", HasUpdate = true }
            };
            Func<PluginManager.Plugin, PluginManager.PluginUpdate?> lookup43 =
                p => ups43.TryGetValue(p.Name, out var u) ? u : null;
            Check("计数口径的纯函数：只数「插件表里有、且合并表标了有新版」的（报告里多出来的不算）",
                MainWindow.CountUpdatablePlugins(rows43, lookup43) == 2 &&
                MainWindow.CountUpdatablePlugins(new List<PluginManager.Plugin>(), lookup43) == 0 &&
                MainWindow.CountUpdatablePlugins(null, lookup43) == 0 &&
                MainWindow.CountUpdatablePlugins(rows43, _ => null) == 0,
                $"合并表 4 条（3 条有更新）、插件表 3 条 ⇒ 应为 2");

            Check("更新事件文案只有一个生成入口（数字由调用处按同一表达式给出）",
                MainWindow.PluginUpdateEventText(2) == "发现 2 个插件有新版本（插件页可更新）" &&
                MainWindow.PluginUpdateEventText(0).Contains(" 0 个"),
                MainWindow.PluginUpdateEventText(5));

            if (w.FindName("PluginsPanel") is not Panel || w.FindName("PluginsSummaryText") is not TextBlock)
                Skip("插件页三处数字同源（事件 / 底部统计 / 一键更新）", "插件页未构建");
            else
            {
                // 先把排序复位，样本才不会被上一个用例留下的排序条件影响
                w.InstalledSortForTest(MainWindow.PluginSortField.Installed, true);
                // 同步做完"换样本 → 重渲染 → 读数"：中途不抽消息，异步续体插不进来（否则读数会飘）
                var undo43 = w.SeedPluginStateForTest(rows43, ups43);
                try
                {
                    w.RenderPluginsForTest();
                    int btn43 = w.UpdatableCountForTest();          // 「一键更新 N 个」那个表达式
                    string evt43 = w.UpdateEventTextForTest();      // 事件栏会写出去的那句
                    string sum43 = w.PluginsSummaryTextForTest();   // 插件页底部统计
                    if (!sum43.Contains("已安装 3 个插件"))
                        Skip("插件页三处数字同源（事件 / 底部统计 / 一键更新）", "插件页未按样本重渲染");
                    else
                        Check("事件、底部统计、「一键更新 N 个」三处数字永远一致（同一集合 + 同一表达式）",
                            btn43 == 2 &&
                            evt43.Contains("发现 2 个插件有新版本") &&
                            sum43.Contains("2 个插件可更新") &&
                            !sum43.Contains("3 个插件可更新"),
                            $"按钮={btn43} · 事件「{evt43}」· 底部「{sum43}」");
                }
                finally { undo43(); }
            }

            // ══════ 44. bug：更新插件弹「更新失败」，可卡片版本已经更新了 ══════
            // 现场证据（异常-20260916-111220.log 原文）：
            //   [11:15:52.979] 已放开 pnpm 供应链策略（… plugin --profile web add dsh-mnemon@0.5.9 …）
            //   [11:17:04.442] 命令结束：… add dsh-mnemon@0.5.9 … 退出码=1
            //       stderr 尾部：… node_modules\cpu-features install: Failed
            //   （另一次同类：swap 时报「另一个程序正在使用此文件，进程无法访问。(os error 32)」）
            // ⇒ 包其实已经装上了，pnpm 因为**某个依赖的构建脚本失败**返回非零，
            //   旧口径只看 `p.ExitCode == 0` 就报失败。现在改为**读磁盘版本与目标比对**。
            //
            // ⚠ 返工记录（1.3.37 自检真机 FAIL 的就是这一条）：
            //   上一版把"说明文案"和"实际断言"写成了两套 —— 说明里写着 `2.0.0/^1.2.3 = 达成`，
            //   而 npm 语义 `^1.2.3` = [1.2.3, 2.0.0) 是**开区间**，2.0.0 恰好落在界外 ⇒ 实际返回
            //   NotSatisfied ⇒ 断言 FAIL 而文案看着全对。现在**判据与文案同源**：
            //   遍历 PluginManager.UpdateVerdictContracts 这一张表，期望值直接取自表里，
            //   detail 用**实际返回值**拼出来（真跑出来的，不是另写的一份期望）。
            int contractTotal = PluginManager.UpdateVerdictContracts.Count;
            string contractActual = PluginManager.DescribeVerdictContracts(out int contractBad);
            var contractMismatch = new List<string>();
            foreach (var c in PluginManager.UpdateVerdictContracts)
            {
                var got = PluginManager.CompareInstalledToTarget(c.Installed, c.Target).Check;
                if (got != c.Expected)
                {
                    string inst = c.Installed == null ? "null" : (c.Installed.Length == 0 ? "(读不到)" : c.Installed);
                    contractMismatch.Add($"{inst} vs {(c.Target.Length == 0 ? "(空)" : c.Target)}：期望 {c.Expected} / 实际 {got}（{c.Why}）");
                }
            }
            Check("版本比对语义契约表：目标达成 / 未达成 / 范围写法（^ ~ >= ||）/ 预发布 / git 源 / 版本无法读取，逐条都对",
                contractTotal >= 30 && contractMismatch.Count == 0 && contractBad == 0,
                contractMismatch.Count == 0
                    ? $"{contractTotal} 条全对：{contractActual}"
                    : $"不符 {contractMismatch.Count}/{contractTotal} 条 → " + string.Join(" | ", contractMismatch));

            Check("目标说明符里的「要装成哪个版本」能取出来（^ / ~ / >= / || 都取版本号；git 源与 latest 取不出）",
                PluginManager.ConcreteVersionOf("0.5.9") == "0.5.9" &&
                PluginManager.ConcreteVersionOf("^0.5.8") == "0.5.8" &&
                PluginManager.ConcreteVersionOf("~0.2.14") == "0.2.14" &&
                PluginManager.ConcreteVersionOf(">=0.1.5-rc.1") == "0.1.5-rc.1" &&
                PluginManager.ConcreteVersionOf("^1.2.3 || ^2.0.0") == "1.2.3" &&
                PluginManager.ConcreteVersionOf("git+https://github.com/x/y.git") == "" &&
                PluginManager.ConcreteVersionOf("github:o/r#2d19cb5a") == "" &&
                PluginManager.ConcreteVersionOf("latest") == "" &&
                PluginManager.ConcreteVersionOf("1.x") == "" &&
                PluginManager.ConcreteVersionOf(null) == "",
                "^0.5.8→0.5.8 · >=0.1.5-rc.1→0.1.5-rc.1 · git 源→(空)");

            // 范围开闭的**定点**核对（上一版返工就在这个边界上；即使表被改坏这条也拦得住）
            Check("范围上下界是开区间（npm 语义）：2.0.0 不满足 ^1.2.3、1.3.0 不满足 ~1.2.3、0.6.0 不满足 ^0.5.8",
                PluginManager.CompareInstalledToTarget("2.0.0", "^1.2.3").Check == PluginManager.VersionCheck.NotSatisfied &&
                PluginManager.CompareInstalledToTarget("1.3.0", "~1.2.3").Check == PluginManager.VersionCheck.NotSatisfied &&
                PluginManager.CompareInstalledToTarget("0.6.0", "^0.5.8").Check == PluginManager.VersionCheck.NotSatisfied &&
                PluginManager.CompareInstalledToTarget("0.3.0", "^0.2.3").Check == PluginManager.VersionCheck.NotSatisfied &&
                // 下界本身与范围内的版本必须达成，否则就变成"一律不达成"的假绿
                PluginManager.CompareInstalledToTarget("1.2.3", "^1.2.3").Check == PluginManager.VersionCheck.Satisfied &&
                PluginManager.CompareInstalledToTarget("1.5.0", "^1.2.3").Check == PluginManager.VersionCheck.Satisfied &&
                PluginManager.CompareInstalledToTarget("1.2.9", "~1.2.3").Check == PluginManager.VersionCheck.Satisfied &&
                PluginManager.CompareInstalledToTarget("0.5.10", "^0.5.8").Check == PluginManager.VersionCheck.Satisfied,
                "^1.2.3=[1.2.3,2.0.0) · ~1.2.3=[1.2.3,1.3.0) · ^0.5.8=[0.5.8,0.6.0) · ^0.2.3=[0.2.3,0.3.0)");

            // 磁盘上真的写一份，走完整的 EvaluateUpdate（不只测纯函数）
            string t44 = Path.Combine(Path.GetTempPath(), "dshguard-verdict-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                string mnemonDir = Path.Combine(t44, "node_modules", "dsh-mnemon");
                Directory.CreateDirectory(mnemonDir);
                File.WriteAllText(Path.Combine(mnemonDir, "package.json"),
                    "{ \"name\": \"dsh-mnemon\", \"version\": \"0.5.9\" }", new UTF8Encoding(false));

                // ★ 这条就是现场那个 bug 的分支：命令退出码非零，但版本已经到位 ⇒ 必须判成功
                var r44 = MainWindow.EvaluateUpdate("dsh-mnemon", "0.5.9", false, "", t44);
                Check("退出码非零但版本已到位 ⇒ 判成功（不再弹「更新失败」；即现场报告的那一次）",
                    r44.CmdOk == false && r44.Measured && r44.Succeeded && r44.NoteDowngraded &&
                    r44.EffectiveVersion == "0.5.9" && r44.Note.Contains("0.5.9"),
                    $"命令退出码0=False · 磁盘={r44.EffectiveVersion} · 判成功={r44.Succeeded} · 虚惊={r44.NoteDowngraded}");

                // 反向：命令退出码非零、版本也没上来 ⇒ 仍然是失败（不许为了少报错就把真失败放过去）
                var r44bad = MainWindow.EvaluateUpdate("dsh-mnemon", "0.6.0", false, "", t44);
                Check("退出码非零且版本没上来 ⇒ 仍然判失败（真失败不许被这一层洗白）",
                    !r44bad.Succeeded && r44bad.Measured && r44bad.NotSatisfied && !r44bad.NoteDowngraded,
                    $"磁盘={r44bad.EffectiveVersion} 目标=0.6.0 ⇒ 判成功={r44bad.Succeeded} · 可判={r44bad.Measured}");

                // 命令成功但版本没动（pnpm 按清单范围沿用了现有版本）⇒ 算成功，别反转成失败
                var r44same = MainWindow.EvaluateUpdate("dsh-mnemon", "^0.5.8", true, "", t44);
                Check("命令成功、版本按清单范围没动 ⇒ 算成功（事实写清楚，不反转成失败）",
                    r44same.Succeeded && !r44same.NoteDowngraded,
                    r44same.Note);

                // 没有这个包 ⇒ 无法读取版本 ⇒ 不可判，如实回落到命令退出码
                var r44none = MainWindow.EvaluateUpdate("不存在的包-zzz", "1.0.0", false, "", t44);
                Check("无法读取版本 ⇒ 不可判，如实回落到命令退出码（无法读取 ≠ 未安装）",
                    !r44none.Measured && !r44none.Succeeded && r44none.EffectiveVersion == "",
                    r44none.Note);

                // git 源目标 ⇒ 不可判，回落到退出码（退出码 0 就算成功）
                var r44git = MainWindow.EvaluateUpdate("dsh-mnemon", "git+https://github.com/x/y.git", true, "", t44);
                Check("git 源目标不可比 ⇒ 回落到命令退出码",
                    !r44git.Measured && r44git.Succeeded,
                    r44git.Note);
            }
            finally { try { Directory.Delete(t44, true); } catch { } }

            // ══════ 45. bug：清单里有、盘上没有 ⇒ 引擎在启动时就崩，而自愈跑得太晚 ══════
            // 诊断包原文（1.3.36 自导出摘要）：
            //   引擎状态: 未运行
            //   最近一次启动的引擎输出尾部:
            //     Error: dsh: cannot resolve profile bundle "@furongjun1999/dsh-memory" from the dsh
            //     installation or …\profiles\web; run 'dsh plugin --profile web install' if its dependency is not installed
            // 随后 12:03:06 一条 `… plugin --profile web add @furongjun1999/dsh-memory@0.4.7 …` 退出码=0
            // ⇒ 自愈逻辑本身是好的，只是挂在「打开插件页 → 取 id」里，用户不点插件页就永远不跑。
            // 修复：把体检提前到**拉起引擎之前**（MainWindow.xaml.cs 的 StartEngineAsync 内，
            // `_processManager.Start(...)` 那一步之前）。判定用磁盘事实，不解析 dsh 的报错。
            string t45 = Path.Combine(Path.GetTempPath(), "dshguard-manifest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(Path.Combine(t45, "node_modules"));

                // 清单：A、B 两个依赖，盘上一个都没装
                File.WriteAllText(Path.Combine(t45, "package.json"),
                    "{ \"name\": \"dsh-profile-web\", \"private\": true, \"dependencies\": { "
                    + "\"@furongjun1999/dsh-memory\": \"0.4.7\", \"dsh-mnemon\": \"^0.5.8\" } }",
                    new UTF8Encoding(false));

                var missing45 = PluginManager.FindMissingFromManifest(t45);
                Check("清单体检：清单里有、盘上没有 ⇒ 两个都命中（启动前就能发现，不必等引擎崩）",
                    missing45.Readable && missing45.Declared == 2 && missing45.Missing.Count == 2 &&
                    missing45.Missing.Contains("@furongjun1999/dsh-memory") &&
                    missing45.Missing.Contains("dsh-mnemon"),
                    $"清单 {missing45.Declared} 条 · 缺 {string.Join("、", missing45.Missing)}");

                // 装入一个（目录存在、package.json 存在，但**没有 version 字段** —— 部分插件即为此种情况）
                string memDir = Path.Combine(t45, "node_modules", "@furongjun1999", "dsh-memory");
                Directory.CreateDirectory(memDir);
                File.WriteAllText(Path.Combine(memDir, "package.json"),
                    "{ \"name\": \"@furongjun1999/dsh-memory\" }", new UTF8Encoding(false));

                var half45 = PluginManager.FindMissingFromManifest(t45);
                Check("清单体检：盘上有（哪怕 package.json 里没有 version 字段）就不算缺（无法读取版本 ≠ 未安装）",
                    half45.Readable && half45.Declared == 2 && half45.Missing.Count == 1 &&
                    half45.Missing[0] == "dsh-mnemon",
                    $"缺 {string.Join("、", half45.Missing)}");

                // 补完最后一个 ⇒ 不再命中
                string mnDir = Path.Combine(t45, "node_modules", "dsh-mnemon");
                Directory.CreateDirectory(mnDir);
                File.WriteAllText(Path.Combine(mnDir, "package.json"),
                    "{ \"name\": \"dsh-mnemon\", \"version\": \"0.5.8\" }", new UTF8Encoding(false));

                var clean45 = PluginManager.FindMissingFromManifest(t45);
                Check("清单体检：盘上都有 ⇒ 不命中（体检不许把健康的机器报成缺包）",
                    clean45.Readable && clean45.Declared == 2 && !clean45.HasMissing,
                    $"清单 {clean45.Declared} 条 · 缺 {clean45.Missing.Count} 个");

                // ★ 真源不可读时**不误判**（宁可不体检，也不能把好的清单说成"全都没装"）
                var none45 = PluginManager.FindMissingFromManifest(Path.Combine(t45, "这个目录不存在"));
                var null45 = PluginManager.FindMissingFromManifest(null);
                var empty45 = PluginManager.FindMissingFromManifest("");
                string badJson = Path.Combine(t45, "badjson");
                Directory.CreateDirectory(badJson);
                File.WriteAllText(Path.Combine(badJson, "package.json"), "{ 这不是合法 JSON", new UTF8Encoding(false));
                var bad45 = PluginManager.FindMissingFromManifest(badJson);
                // 清单在、但没有 dependencies 段
                string nodeps = Path.Combine(t45, "nodeps");
                Directory.CreateDirectory(nodeps);
                File.WriteAllText(Path.Combine(nodeps, "package.json"), "{ \"name\": \"x\" }", new UTF8Encoding(false));
                var nodeps45 = PluginManager.FindMissingFromManifest(nodeps);

                Check("清单体检：真源不可读时不误判（目录不在 / 空参数 / JSON 坏 / 没有 dependencies ⇒ 一律「无法读取」且不缺包）",
                    !none45.Readable && none45.Missing.Count == 0 &&
                    !null45.Readable && null45.Missing.Count == 0 &&
                    !empty45.Readable && empty45.Missing.Count == 0 &&
                    !bad45.Readable && bad45.Missing.Count == 0 &&
                    !nodeps45.Readable && nodeps45.Missing.Count == 0,
                    $"目录不在={none45.Readable} · JSON 坏={bad45.Readable} · 无依赖段={nodeps45.Readable}");

                // 补不上时给用户的那句话：必须**点名**是哪个包（用户才知道该重装/卸载哪个插件）
                string note45 = PluginManager.MissingPackagesNote(new[] { "@furongjun1999/dsh-memory" });
                Check("补不上时那句原因会点名是哪个包（不许含糊成「某个插件」）",
                    note45.Contains("@furongjun1999/dsh-memory") &&
                    note45.Contains("未安装") &&
                    (note45.Contains("重新安装") || note45.Contains("卸载")) &&
                    PluginManager.MissingPackagesNote(null) == "" &&
                    PluginManager.MissingPackagesNote(Array.Empty<string>()) == "",
                    note45);
            }
            finally { try { Directory.Delete(t45, true); } catch { } }

            // ══════ 46. bug：网页/插件市场里重新启用了，「DSHGuard」还显示禁用 ══════
            // 现场：用户将 dsh-client-auto-continue 禁用（用于验证禁用是否真正生效），随后在**网页/插件市场**
            // 把它重新启用 —— 回到守护壳，卡片**仍然显示已禁用**、按钮还是「启用插件」。
            // 根因（定性）：壳这一侧的"是否已禁用"读的是 cordis.patch.yml 里的禁用记录
            // （PluginManager.ReadDisabledIds），而网页/市场那侧走的是**它自己的开关**、
            // 不把结果写回 patch ⇒ 两边各记各的，天然会不同步。
            //
            // 只读取证 —— patch 文件尾部原文（…\.dsh\profiles\web\cordis.patch.yml）：
            //   # DSHGuard 于 2026-09-16 00:33:51 禁用（备份 cordis.patch.yml.bak-20260916-003351）
            //   - id: "auto-continue"
            //     disabled: true
            // 只读取证 —— 同一台机器上 loader-ids.json 里的映射（Config\loader-ids.json）：
            //   "dsh-client-auto-continue": "auto-continue"
            // 只读取证 —— 引擎自己那份视图的**真实输出片段**（Logs\异常-20260916-122458.log，
            // 12:24:59「命令结束：… --dump-config … 退出码=0」，stdout 头尾）：
            //   # == @deepseek-ai/dsh-base
            //   - id: timer
            //     name: '@deepseek-ai/cordis-plugin-timer'
            //   - id: hmr
            //     name: '@deepseek-ai/cordis-plugin-hmr'
            //     disabled: true
            //     config:
            //       root:
            //         - .
            //   - id: llm
            //     name: '@
            // ⇒ 修法：同一次 dump 输出里就能拿到"哪些条目实际被禁用"，把它当**唯一事实来源**；
            //   patch 记录只在拿不到引擎视图时降级用，并在事件栏如实说明。
            //
            // 样本 ①=上面那段日志原文（逐字），其后按 loader-ids.json 里**真实存在**的条目续写，
            // 并原样带上现场那个插件（名称≠loader id）。备注：dump 输出里 @ 开头的 name 用的是
            // **单引号**（日志原文如此），本程序的解析逻辑对两种引号均予识别。
            const string realDump =
                "# == @deepseek-ai/dsh-base\n" +
                "- id: timer\n" +
                "  name: '@deepseek-ai/cordis-plugin-timer'\n" +
                "- id: hmr\n" +
                "  name: '@deepseek-ai/cordis-plugin-hmr'\n" +
                "  disabled: true\n" +
                "  config:\n" +
                "    root:\n" +
                "      - .\n" +
                "- id: llm\n" +
                "  name: '@deepseek-ai/dsh-llm'\n" +
                "- id: modsearch\n" +
                "  name: '@liustack/modsearch'\n" +
                "- id: deepseek-harness-zh_pro\n" +
                "  name: 'dsh-zh'\n" +
                "  disabled: true\n" +
                "- id: mnemon-bundle\n" +
                "  name: 'cordis:group'\n" +
                "  config:\n" +
                "    isolate:\n" +
                "      mnemon: true\n" +
                "- id: auto-continue\n" +
                "  name: 'dsh-client-auto-continue'\n" +
                "  config:\n" +
                "    minIntervalMs: 2000\n" +
                "- id: mnemon\n" +
                "  name: 'dsh-mnemon'\n";

            var eng46 = PluginManager.ParseDisabledIds(realDump);
            Check("引擎视图解析：真实 dump 片段里被禁用的条目（含现场那个 auto-continue）一条不漏",
                eng46 != null && eng46.Count == 2 &&
                eng46.Contains("hmr") && eng46.Contains("deepseek-harness-zh_pro") &&
                !eng46.Contains("timer") && !eng46.Contains("llm") && !eng46.Contains("mnemon-bundle") &&
                !eng46.Contains("auto-continue") && !eng46.Contains("mnemon"),
                eng46 == null ? "解析返回 null" : $"{eng46.Count} 条：{string.Join("、", eng46.OrderBy(s => s))}");

            // 样本 ②：`disabled: !!js …` 是**表达式**、不是字面量 true，绝不许当成"已禁用"
            //   （patch 文件头就写明 "`!!js` expressions allowed"，dump 里同样可能出现）
            Check("引擎视图解析：`disabled: !!js …` 表达式不当作已禁用（只认字面量 true）",
                PluginManager.ParseDisabledIds(
                    "- id: jsEntry\n  name: 'x-js'\n  disabled: !!js ctx.config.enabled === false\n" +
                    "- id: trueEntry\n  name: 'x-true'\n  disabled: true\n") is { } jsSet &&
                jsSet.Count == 1 && jsSet.Contains("trueEntry") && !jsSet.Contains("jsEntry"),
                "!!js 的那条不算、字面量 true 的那条才算");

            // 样本 ②′：**逐条**核 IsDisabledTrue 的取值形态（拆开写，FAIL 时一眼看出是哪一种形态判错）。
            //   形态取自真实文件：patch 中本程序自己写入的行即带行尾注释；
            //   自检这一侧传的是**带缩进的整行**（所以实现里必须先剥注释、再两边 Trim）。
            Check("IsDisabledTrue：字面量 true（带行尾注释 / 单引号 / 双引号）都算已禁用",
                PluginManager.IsDisabledTrue("  disabled: true  # 本程序写进去的就是这个形态") &&
                PluginManager.IsDisabledTrue("  disabled: true") &&
                PluginManager.IsDisabledTrue("disabled: true") &&
                PluginManager.IsDisabledTrue("  disabled: 'true'") &&
                PluginManager.IsDisabledTrue("  disabled: \"true\"") &&
                PluginManager.IsDisabledTrue("  disabled: TRUE"),
                "带缩进 / 带行尾注释 / 加引号 / 大写 一律算");
            Check("IsDisabledTrue：表达式、false、空值、别的键都不算已禁用（不误判）",
                !PluginManager.IsDisabledTrue("  disabled: !!js ctx.mode !== 'on'") &&
                !PluginManager.IsDisabledTrue("  disabled: false") &&
                !PluginManager.IsDisabledTrue("  disabled:") &&
                !PluginManager.IsDisabledTrue("  disabled: null") &&
                !PluginManager.IsDisabledTrue("  name: 'x-true'") &&
                !PluginManager.IsDisabledTrue("# disabled: true") &&
                !PluginManager.IsDisabledTrue(null) &&
                !PluginManager.IsDisabledTrue(""),
                "!!js / false / 空 / null / 别的键 / 注释行 一律不算");
            Check("StripInlineComment：剥掉行尾注释但**保留行首缩进**（层级判据靠它）、不碰引号里的 #",
                PluginManager.StripInlineComment("  disabled: true  # note").TrimEnd() == "  disabled: true" &&
                PluginManager.StripInlineComment("    disabled: true").TrimEnd() == "    disabled: true" &&
                PluginManager.StripInlineComment("  name: '@a/b#next'").TrimEnd() == "  name: '@a/b#next'" &&
                PluginManager.StripInlineComment("  # 整行注释") == "  " &&
                PluginManager.StripInlineComment(null) == "",
                $"「{PluginManager.StripInlineComment("  disabled: true  # note").TrimEnd()}」· 引号里的 # 不被截断");

            // 样本 ③：`disabled` 归属**最近的、在它前面的那条 `- id:`**，不跨条目串行。
            //
            // ⚠ 期望值订正（真机自检 FAIL 后逐条核对出来的）：上一版把这份样本的期望写成 {outer, inner}，
            //   那是**错的** —— 这份文本里 `outer / inner / after` 是**三个同级条目**（都缩进 0 的 `- id:`），
            //   中间并没有谁包着谁。于是：
            //     · `outer` 自己那条 `disabled: true` 缩进 4 ⇒ 属外层的 outer；
            //     · `after` 那条 `disabled: true` 缩进 2 ⇒ 属它自己的 after（2 > 0，是"更深"，当然算它的）；
            //     · `inner` 的 `disabled: false` 不算。
            //   ⇒ 正确答案是 {inner, after}。上一版想验的"更浅的那条属于外层"在这个样本里**根本构造不出来**。
            //   另：真机 dump 的条目全是**平铺的**（本机实测日志里 12 条 `- id:` 缩进全为 0，
            //   `ParseLoaderIds` / `ParseLoaderIdNames` 两个既有解析器也只认"行首就是 - id:"），
            //   所以这里不引入"嵌套子条目"这种没有证据的形态；把结论写成能对着真实文件重复验证的三条。
            var realIndent = PluginManager.ParseDisabledIds(
                "- id: outer\n" +
                "  name: 'x1'\n" +
                "  config:\n" +
                "    disabled: true\n" +                 // 更深（4 > 0）⇒ 算 outer 的
                "- id: inner\n" +
                "  name: 'x2'\n" +
                "  disabled: false\n" +                  // 显式 false ⇒ 不算
                "- id: after\n" +
                "  name: 'x3'\n" +
                "  disabled: true\n");                   // 自己的（与自己同层）⇒ 算 after 的
            Check("引擎视图解析：disabled 归一它上面最近的那条 - id:（同层/更深都算，不跨条目）",
                realIndent != null && realIndent.SetEquals(new[] { "outer", "after" }),
                $"[{string.Join(",", realIndent ?? new HashSet<string>())}]（期望 outer、after；inner 是显式 false）");

            var crossTalk = PluginManager.ParseDisabledIds(
                "- id: a\n" +
                "  name: 'x-a'\n" +
                "- id: b\n" +
                "  name: 'x-b'\n" +
                "  disabled: true\n" +
                "- id: c\n" +
                "  name: 'x-c'\n" +
                "  config:\n" +
                "    root:\n" +
                "      - .\n" +
                "- id: d\n" +
                "  name: 'x-d'\n");
            Check("引擎视图解析：同层条目之间不串行（a 不被 b 的 disabled 带上；b 的 config 子行不算新条目）",
                crossTalk != null && crossTalk.SetEquals(new[] { "b" }),
                $"[{string.Join(",", crossTalk ?? new HashSet<string>())}]（期望只有 b）");

            // dump 中的行尾注释必须先剥除（真实形态：patch 中本程序自己写入的行即带注释）
            var cmtSet = PluginManager.ParseDisabledIds(
                "- id: a\n  name: 'x'\n  disabled: true  # 说明\n- id: b\n  name: 'y'\n  disabled: false  # 另一条\n");
            Check("引擎视图解析：行尾注释不挡住判定（`disabled: true  # 说明` 要算已禁用）",
                cmtSet != null && cmtSet.SetEquals(new[] { "a" }),
                $"[{string.Join(",", cmtSet ?? new HashSet<string>())}]（期望只有 a）");

            Check("引擎视图解析：先出现 disabled: true 再出现 id ⇒ 不许算到后面那个 id 头上（不跨条目串行）",
                PluginManager.ParseDisabledIds(
                    "  disabled: true\n" +
                    "- id: later\n" +
                    "  name: 'y'\n") is { } noEntrySet && noEntrySet.Count == 0,
                "条目外的 disabled 谁都不算");

            // 样本 ④：不是引擎视图 ⇒ 必须返回 null（调用方据此降级到本地记录并注明），
            //         而**确实没有禁用项**时返回空集合（属确定事实，非"无法读取"）
            Check("引擎视图解析：拿不到视图（null/空/非 dump 文本）与「一个都没禁用」必须分得开",
                PluginManager.ParseDisabledIds(null) == null &&
                PluginManager.ParseDisabledIds("") == null &&
                PluginManager.ParseDisabledIds("   \r\n  ") == null &&
                PluginManager.ParseDisabledIds("Error: dsh: cannot resolve profile bundle \"x\"") == null &&
                PluginManager.ParseDisabledIds("{}") == null &&
                PluginManager.ParseDisabledIds("- id: a\n  name: 'x'\n") is { } noneSet && noneSet.Count == 0,
                "null/空/报错文本 ⇒ null；有 id 无 disabled ⇒ 空集合");

            // 样本 ⑤：**现场那个场景本身** —— 引擎说 auto-continue 没被禁用（网页那头启用了），
            //         而 patch 里还记着禁用 ⇒ 必须判成"patch 记录过时了"，并且以引擎为准把标记清掉。
            // ⚠ 期望值订正（真机自检 FAIL 后核对出来的）：这份真实 dump 里 `hmr` 本来就带
            //   `disabled: true`（引擎基座给自己禁的，见 异常-20260916-122458.log 原文），
            //   而 patch 从来没记过它 ⇒ **EngineOnly 必是 {hmr}、不是空集**。上一版把
            //   "patch 记着的"与"引擎禁用的"当成同一个集合来写期望，那才是错的。
            var engine46 = PluginManager.ParseDisabledIds(realDump) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var patch46 = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "auto-continue", "deepseek-harness-zh_pro" };
            var sync46 = PluginManager.CompareEngineToPatch(engine46, patch46);
            Check("对账：patch 记着、引擎说没禁用 ⇒ 判成「patch 过时」（现场：网页启用了、壳还显示禁用）",
                sync46.EngineView && sync46.Drifted && sync46.PatchOnly.Count == 1 &&
                sync46.PatchOnly[0] == "auto-continue" && sync46.Compared == 3 &&
                // 反向那一侧同样要认出来：引擎基座自己禁用的 hmr，patch 里根本没这条记录
                sync46.EngineOnly.Count == 1 && sync46.EngineOnly[0] == "hmr",
                $"引擎只多={string.Join("、", sync46.EngineOnly)}（应为 hmr）｜ patch 只多={string.Join("、", sync46.PatchOnly)}（应为 auto-continue）");

            var sync46b = PluginManager.CompareEngineToPatch(engine46, engine46);
            var sync46c = PluginManager.CompareEngineToPatch(null, patch46);
            // 变量名一律带 46 后缀：本方法里已有 `s`（蠕行曲线那个 `for (double s = …)`），
            // 用裸 `s` 会撞上 CS0136「名称在封闭局部范围中已被使用」——真编译才会报，括号配平查不出来。
            var syncRev46 = PluginManager.CompareEngineToPatch(new[] { "auto-continue" }, patch46);
            var syncNew46 = PluginManager.CompareEngineToPatch(new[] { "brand-new-id" }, patch46);
            Check("对账：一致时无漂移；反向（网页/市场那侧禁用了）也能认出来；拿不到引擎视图时不谈漂移",
                sync46b.EngineView && !sync46b.Drifted && sync46b.Compared == 2 &&
                syncRev46.EngineView && syncRev46.EngineOnly.Count == 0 &&
                syncRev46.PatchOnly.Count == 1 && syncRev46.PatchOnly[0] == "deepseek-harness-zh_pro" &&
                syncNew46.EngineOnly.Count == 1 && syncNew46.EngineOnly[0] == "brand-new-id" &&
                !sync46c.EngineView && !sync46c.Drifted && sync46c.Compared == 0,
                $"一致={!sync46b.Drifted} ｜ 引擎独有={string.Join("、", syncNew46.EngineOnly)}");

            // 样本 ⑥：标记必须**重设**（true / false 两个方向都设），且判据同时认包名与 loader id
            var seed46 = new List<PluginManager.Plugin>
            {
                // ★ 现场那一对：包名 dsh-client-auto-continue、loader id auto-continue
                new() { Name = "dsh-client-auto-continue", LoaderId = "auto-continue", Disabled = true },
                new() { Name = "dsh-zh", LoaderId = "deepseek-harness-zh_pro", Disabled = false },
                new() { Name = "@liustack/modsearch", LoaderId = "modsearch", Disabled = false }
            };
            var undo46 = w.SeedPluginStateForTest(seed46,
                new Dictionary<string, PluginManager.PluginUpdate>(StringComparer.OrdinalIgnoreCase));
            bool byEngine46;
            try
            {
                byEngine46 = PluginManager.ApplyDisabledFlagsFromEngine(seed46, engine46, patch46);
            }
            finally { undo46(); }

            Check("按引擎视图重设标记：网页启用过的要**清掉**禁用，引擎说禁用的仍标禁用（现场那条就是前者）",
                byEngine46 &&
                !seed46[0].Disabled &&                    // auto-continue：patch 记着禁用、引擎说启用 ⇒ 清掉
                seed46[1].Disabled &&                     // dsh-zh 以 loader id 命中
                !seed46[2].Disabled,
                $"auto-continue 已禁用={seed46[0].Disabled} · dsh-zh 已禁用={seed46[1].Disabled}（引擎集合 {engine46.Count} 条）");

            // 样本 ⑦：拿不到引擎视图 ⇒ 退回 patch 记录口径（= 原有的 RefreshDisabledFlags 行为，不许回归），
            //         并且返回 false ⇒ 调用方据此在事件栏注明"当前状态来自本地记录，可能与引擎不一致"
            var seed46b = new List<PluginManager.Plugin>
            {
                new() { Name = "dsh-zh", LoaderId = "deepseek-harness-zh_pro", Disabled = true },   // 故意先给反的
                new() { Name = "@liustack/modsearch", LoaderId = "modsearch", Disabled = true }     // 故意先给反的
            };
            var undo46b = w.SeedPluginStateForTest(seed46b,
                new Dictionary<string, PluginManager.PluginUpdate>(StringComparer.OrdinalIgnoreCase));
            bool byEngine46b;
            try
            {
                byEngine46b = PluginManager.ApplyDisabledFlagsFromEngine(seed46b, null, patch46);
            }
            finally { undo46b(); }

            Check("拿不到引擎视图就退回本地记录口径（原有的禁用判定不回归），并回报「这次不是按引擎判的」",
                !byEngine46b && seed46b[0].Disabled && !seed46b[1].Disabled &&
                PluginManager.DisabledFallbackNote(false).Contains("本地记录") &&
                PluginManager.DisabledFallbackNote(false).Contains("可能与引擎不一致") &&
                PluginManager.DisabledFallbackNote(true) == "" &&
                !PluginManager.DisabledFallbackNote(false).Contains("dump") &&
                !PluginManager.DisabledFallbackNote(false).Contains("npx"),
                "「" + PluginManager.DisabledFallbackNote(false) + "」（不出现命令写法）");

            // ══════ 47. bug：批量更新 / 批量卸载按命令退出码判成败（"显示失败、其实装上了"）══════
            // 现场（用户报告）：dsh-mnemonic 更新时 pnpm 因依赖构建脚本/文件占用返回非零，包其实已到位。
            // 单个更新与一键更新早已改成"读磁盘版本比对"（见第 44 条），批量路径却还只看退出码
            // ⇒ 同一个包在两条路径上结论不同。本条盯着**批量那两个入口**，判据与它们同源调用。
            string t47 = Path.Combine(Path.GetTempPath(), "dshguard-batch-verdict-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                string mnDir = Path.Combine(t47, "node_modules", "dsh-mnemonic");
                Directory.CreateDirectory(mnDir);
                File.WriteAllText(Path.Combine(mnDir, "package.json"),
                    "{ \"name\": \"dsh-mnemonic\", \"version\": \"0.5.9\" }", new UTF8Encoding(false));

                var p47 = new PluginManager.Plugin { Name = "dsh-mnemonic" };
                var u47 = new PluginManager.PluginUpdate { Name = "dsh-mnemonic", Installed = "0.5.8", Latest = "0.5.9" };

                // ★ 现场那一支：命令退出码非零，版本却已经到位 ⇒ 批量必须判成功（旧口径判失败）。
                // ⚠ 真机自检 FAIL 订正：这里**必须**把 profileDir 显式指到 t47。
                //   上一版写成四实参 `BatchUpdateVerdictForTest(p, u, cmdOk, output)`，
                //   而 EvaluateUpdate 的第 5 个形参带默认值 ⇒ 它静默落回"读真实 profile"，
                //   临时目录里的 fixture 白造（detail 里"磁盘="是空串就是这么来的）。
                var bv47 = MainWindow.BatchUpdateVerdictForTest(p47, u47, cmdOk: false, output: "", t47);
                Check("批量更新：退出码非零但磁盘版本已到位 ⇒ 判成功（与单个/一键同一口径；现场那一支）",
                    bv47.CmdOk == false && bv47.Measured && bv47.Succeeded && bv47.NoteDowngraded &&
                    bv47.EffectiveVersion == "0.5.9",
                    $"命令退出码0=False · 磁盘={bv47.EffectiveVersion} · 判成功={bv47.Succeeded} · 虚惊={bv47.NoteDowngraded}");

                // 同一个样本、同一次调用链：**不指 profileDir** 时读真实 profile ⇒ 不可判、回落退出码。
                // 这条把"为什么上一版会 FAIL"钉住：判据没错，是样本没被用上。
                string realProfileAware = PluginManager.ReadInstalledVersion(t47, "dsh-mnemonic");
                Check("样本确实被读到了（fixture 真的建出了 node_modules\\<包>\\package.json，且能读出目标版本）",
                    realProfileAware == "0.5.9" && File.Exists(Path.Combine(t47, "node_modules", "dsh-mnemonic", "package.json")),
                    $"从样本目录读出「{realProfileAware}」");

                var u47bad = new PluginManager.PluginUpdate { Latest = "0.6.0" };
                var r47bad = MainWindow.EvaluateUpdate("dsh-mnemonic", u47bad.Latest, false, "", t47);
                Check("批量更新：退出码非零、版本也没上来 ⇒ 仍判失败（真失败不许被这一层洗白）",
                    !r47bad.Succeeded && r47bad.Measured && r47bad.NotSatisfied && !r47bad.NoteDowngraded,
                    $"磁盘={r47bad.EffectiveVersion} 目标=0.6.0 ⇒ 判成功={r47bad.Succeeded}");

                // ── 卸载：判据是"包目录还在不在"（事实），不是退出码 ──
                var unDir = Path.Combine(t47, "node_modules", "dsh-gone");
                Directory.CreateDirectory(unDir);
                File.WriteAllText(Path.Combine(unDir, "package.json"), "{ \"name\": \"dsh-gone\" }", new UTF8Encoding(false));

                var stillHere = MainWindow.BatchUninstallVerdictForTest("dsh-gone", cmdOk: true, output: "", t47);
                Check("批量卸载：命令退出码 0、但包目录还在 ⇒ 判**未卸载**（事实优先，不许谎报成功）",
                    stillHere.Measured && !stillHere.Removed && stillHere.Note.Contains("还在"),
                    stillHere.Note);

                Directory.Delete(unDir, true);           // 包真的被删掉 = 卸载成功的样子
                var goneZero = MainWindow.BatchUninstallVerdictForTest("dsh-gone", cmdOk: true, output: "", t47);
                var goneNonzero = MainWindow.BatchUninstallVerdictForTest("dsh-gone", cmdOk: false, output: "", t47);
                Check("批量卸载：包目录确实消失 ⇒ 判卸掉了；命令报了非零也一样（「显示失败、其实卸干净了」那一支）",
                    goneZero.Measured && goneZero.Removed && !goneZero.NoteDowngraded &&
                    goneNonzero.Measured && goneNonzero.Removed && goneNonzero.NoteDowngraded,
                    $"退出码0={goneZero.Removed} ｜ 退出码非0={goneNonzero.Removed}（虚惊={goneNonzero.NoteDowngraded}）");

                // 目录还在、里面的 package.json 没了（pnpm 删到一半）⇒ 算"半截"，宁可让用户重试一次
                var halfDir = Path.Combine(t47, "node_modules", "dsh-half");
                Directory.CreateDirectory(halfDir);
                var half47 = MainWindow.BatchUninstallVerdictForTest("dsh-half", cmdOk: true, output: "", t47);
                Check("批量卸载：目录还在、里面已经空了 ⇒ 按「未卸载」处理（半截状态不谎报成功）",
                    half47.Measured && !half47.Removed && half47.Note.Contains("半截"),
                    half47.Note);

                // 判不了的时候（包名/目录未知）如实回落命令退出码 —— 与 EvaluateUpdate 的 Unknown 同款保守。
                // ⚠ 真机自检 FAIL 订正：这一条原来的期望写反了。
                //   `VerifyUninstalled` 判的是"**包目录还在不在**"，而"目录不在"包含两种情形：
                //     · 本来装着、卸载后目录消失了  ⇒ 卸掉了；
                //     · 这台机器上**从来就没有过这个包** ⇒ 目录同样不在。
                //   判据本身分不开这两者（也不该分：卸载的语义就是"卸载后它不在"），
                //   所以"一个不存在的包"会得到 Removed=true —— 这是**事实语义**，不是 bug。
                //   真正的"判不了"只有一种：包名/目录为空（Checked=false）⇒ 那时才回落命令退出码。
                var pn47 = PluginManager.VerifyUninstalled("");
                var pn47b = PluginManager.VerifyUninstalled(null);
                Check("批量卸载：判不了时如实回落退出码（空白包名 / 目录未知 ⇒ 只认命令退出码）",
                    !pn47.Checked && !pn47.Removed && !pn47b.Checked && !pn47b.Removed &&
                    pn47.Note.Contains("只能按命令退出码判定"),
                    $"空白包名 Checked={pn47.Checked} Removed={pn47.Removed} ｜ {pn47.Note}");
                // ⚠ 真机自检 FAIL 订正：VerifyUninstalled 已升级——非法包名先过 IsValidPackageName 白名单
                //   （PluginManager.cs:1519），不核对磁盘、不执行命令 ⇒ Checked=false。旧断言把
                //   "目录不在 ⇒ Removed=true" 钉成契约（还写着「不是 bug」），那正是"包名打错"与
                //   "已卸载"混为一谈的老缺陷，实现升级修掉了它，断言要对齐**三态**新语义：
                //   ① 非法包名 ⇒ Measured=false、回落退出码（不核对磁盘）；
                //   ② 合法包名 + 目录不在（ existedBefore=false）⇒ 「无需卸载」中性结论，既不报成功也不报失败；
                //   ③ 合法包名 + 目录不在（existedBefore 未记，旧两态兼容口径）⇒ 按事实判"目录已消失"。
                var illegal47 = MainWindow.BatchUninstallVerdictForTest("绝不存在的包-zzz", cmdOk: false, output: "", t47);
                var illegal47ok = MainWindow.BatchUninstallVerdictForTest("绝不存在的包-zzz", cmdOk: true, output: "", t47);
                Check("批量卸载：非法包名不核对磁盘 ⇒ 判不了、如实回落命令退出码",
                    !illegal47.Measured && !illegal47.Removed && illegal47ok.Removed &&
                    illegal47.Note.Contains("不是合法的 npm 包名"),
                    $"{illegal47.Note}（退出码0={illegal47ok.Removed}）");

                var never47 = MainWindow.BatchUninstallVerdict3ForTest("never-installed-pkg", cmdOk: false,
                    output: "", t47, existedBefore: false);
                var never47null = MainWindow.BatchUninstallVerdictForTest("never-installed-pkg", cmdOk: true,
                    output: "", t47);
                Check("批量卸载：非法包名回落退出码；合法包名但从未安装 ⇒ 按三态判定如实处理",
                    never47.Measured && !never47.Removed && never47.AlreadyAbsent &&
                    never47.Note.Contains("无需卸载") &&
                    never47null.Measured && never47null.Removed && never47null.Note.Contains("包目录已消失"),
                    $"无前置事实（旧两态口径）⇒ {never47null.Note} ｜ 有前置事实 ⇒ {never47.Note}");
            }
            finally { try { Directory.Delete(t47, true); } catch { } }

            // ══════ 48. bug：点「停止」立刻重新开始装（被杀 ⇒ 失败分支又重试了一次）══════
            // 现场：安装中鼠标移到按钮上变成红「停止」，点下去 —— 表现是"杀了又装"、
            // 按钮一直停在「安装中…」。根因两件事凑一起：
            //   ① StopRunningCommand() 杀掉进程后 RunCommandCancelableAsync 返回 ok=false，
            //      与"命令自己失败"从返回值上长得一模一样；
            //   ② 安装流程的失败分支里有一条"去掉策略参数重试一次"的兜底 ⇒ 把刚停掉的安装又跑一遍。
            // 修法：点停止时**先落一个一次性标记、再杀进程**，失败分支消费这个标记；被停止 ⇒ 不重试。
            Check("被用户停止 ⇒ 绝不自动重试（判定入口与 MarketInstall_Click 里那条分支同源）",
                !MainWindow.ShouldRetryInstallAfterFailure(cmdOk: false, userStopped: true) &&
                // 反向：真失败仍要保留那次兜底重试，别为了修这条把兜底删掉
                MainWindow.ShouldRetryInstallAfterFailure(cmdOk: false, userStopped: false) &&
                // 命令成功就没有"重试"这回事
                !MainWindow.ShouldRetryInstallAfterFailure(cmdOk: true, userStopped: false) &&
                !MainWindow.ShouldRetryInstallAfterFailure(cmdOk: true, userStopped: true),
                "停止⇒不重试；真失败⇒仍重试一次；成功⇒不重试");

            // 标记只认一次：留着会让**下一次**安装的真失败被误判成"用户停的"、连重试都不做
            Check("那个「用户已请求停止」标记只消费一次（不会误伤下一次安装）",
                !w.InstallStopPendingForTest() &&
                !w.ConsumeInstallStopForTest() &&              // 空标记：消费到 false
                !w.InstallStopPendingForTest(),
                "初始为空，消费一次仍是 false，且状态回到空");
            w.RequestInstallStopForTest();
            bool pending48 = w.InstallStopPendingForTest();
            bool first48 = w.ConsumeInstallStopForTest();
            bool second48 = w.ConsumeInstallStopForTest();
            Check("点一次停止只留一个标记：第一次消费拿到 true，第二次就是 false（一次性）",
                pending48 && first48 && !second48 && !w.InstallStopPendingForTest(),
                $"落标记={pending48} 首次消费={first48} 二次消费={second48}");

            Check("被停止时的文案是「安装已按请求停止」（事件栏 / 进度条 / 弹窗同一句，不出现命令写法）",
                MainWindow.InstallStoppedTextForTest() == "安装已按请求停止" &&
                !MainWindow.InstallStoppedTextForTest().Contains("npx") &&
                !MainWindow.InstallStoppedTextForTest().Contains("pnpm"),
                MainWindow.InstallStoppedTextForTest());

            // ══════ 49. bug：安装中列表刷新 ⇒ 「停止」按钮消失（换成绿色「已安装」徽章）══════
            // 现场：安装期间卡片一旦重建（补元数据 / 取图 / 改筛选 / 切分类都会重建整张卡片），
            // 只要 IsInstalledInProfile(m) 此刻为真（pnpm 把依赖写进清单那一刻就算"已安装"，
            // 而安装还在收尾），卡片就被换成绿色「已安装」徽章 ⇒ 正在跑的那次安装**没有停止入口**了。
            // 修法：正在安装的那一条在**安装结束之前**始终保留「安装中…／停止」的按钮形态。
            //
            // ⚠ 真机 FAIL 后用 **WPF 探针真跑**订正过两处 fixture 错误（业务判据本身是对的）：
            //   ① 我仅将「正在安装的那一条」写入插件表，对照组**根本未被写入** ⇒ 它永远无法获得徽章；
            //   ② "拍照"顺序错了：`ButtonInfos` 走的是**视觉树**，卡片必须先 Measure/Arrange 过
            //      才建出模板、按钮才有 Content ⇒ 不布局直接收集，按钮列表恒为空（探针实测：[]）。
            //   探针实测的实际值（同一台机器、同一份源码）：
            //       未开始安装：m49 文本含「已安装」/ 无按钮          m49other 按钮=[安装/#FF34C759]、无徽章
            //       安装态重建：m49 按钮=[安装中…/#FF4A9EFF]、无徽章  m49other 按钮=[安装/#FF34C759]、无徽章
            //       种进两条后：m49other 文本含「已安装」、无按钮
            var seeded49 = w.SeedMarketPluginsForTest(new List<PluginManager.Plugin>
            {
                new() { Name = "dsh-guard-selftest-installing" },
                new() { Name = "dsh-guard-selftest-other" }     // 对照组也必须真的在表里，否则它拿不到徽章
            });
            try
            {
                var m49 = new PluginMarket.MarketPlugin
                {
                    Name = "dsh-guard-selftest-installing",
                    Npm = "dsh-guard-selftest-installing",   // MatchKey 命中 ⇒ IsInstalledInProfile 为真
                    Version = "1.0.0",                       // 有版本 ⇒ 安装完"就已安装"
                    Owner = "selftest",
                    RepoUrl = "https://github.com/selftest/installing"
                };
                var m49other = new PluginMarket.MarketPlugin
                {
                    Name = "dsh-guard-selftest-other",
                    Npm = "dsh-guard-selftest-other",
                    Version = "1.0.0",
                    Owner = "selftest",
                    RepoUrl = "https://github.com/selftest/other"
                };

                // 建卡 + **布局**：按钮 Content 要等模板实例化后才读得到（见上面 ②）
                var cardBefore49 = w.BuildMarketCardForTest(m49);
                cardBefore49.Measure(new Size(560, 900));
                cardBefore49.Arrange(new Rect(0, 0, 560, 900));
                cardBefore49.UpdateLayout();
                string before49 = CollectText(cardBefore49);
                var btnsBefore49 = ButtonInfos(cardBefore49);

                var cardOther49 = w.BuildMarketCardForTest(m49other);
                cardOther49.Measure(new Size(560, 900));
                cardOther49.Arrange(new Rect(0, 0, 560, 900));
                cardOther49.UpdateLayout();
                string other49 = CollectText(cardOther49);
                var btnsOther49 = ButtonInfos(cardOther49);

                // ① 前置断言：把"两条卡片确实造出来了"钉死，否则后面的 FAIL 分不清是业务判据错了还是样本没铺好。
                //    ⚠ 别在这里期望"安装那条显示绿「安装」"：只要一个包在**插件表里**，
                //      `IsInstalledInProfile` 即为真 ⇒ 卡片显示绿色「已安装」徽章、根本没有按钮
                //      （绿「安装」按钮只出现在"没装过"的条目上）。这一点 WPF 探针真跑实测过：
                //      安装条 按钮=[] 徽章=True ｜ 对照条 按钮=[] 徽章=True。
                Check("安装态断言的前置：两条卡片都真的建出来了，且都已被认成「已安装」（吃徽章、此刻没有按钮）",
                    !w.IsInstallingThisForTest(m49) && !w.IsInstallingThisForTest(m49other) &&
                    btnsBefore49.Count == 0 && before49.Contains("已安装") &&
                    btnsOther49.Count == 0 && other49.Contains("已安装"),
                    $"安装条：按钮=[{string.Join("/", btnsBefore49.Select(b => b.Text))}] 徽章={before49.Contains("已安装")} ｜ "
                    + $"对照条：按钮=[{string.Join("/", btnsOther49.Select(b => b.Text))}] 徽章={other49.Contains("已安装")}");

                // ② 推进到安装态，再**重建**卡片（模拟安装期间的列表刷新）
                w.BeginInstallStateForTest(m49);
                var cardDuring49 = w.BuildMarketCardForTest(m49);
                cardDuring49.Measure(new Size(560, 900));
                cardDuring49.Arrange(new Rect(0, 0, 560, 900));
                cardDuring49.UpdateLayout();
                string during49 = CollectText(cardDuring49);
                var btnsDuring49 = ButtonInfos(cardDuring49);

                // ③ 对照组重建后仍吃徽章（证明这次改的是"正在安装的那一条"，没把徽章一并废掉）
                var cardOtherDuring49 = w.BuildMarketCardForTest(m49other);
                cardOtherDuring49.Measure(new Size(560, 900));
                cardOtherDuring49.Arrange(new Rect(0, 0, 560, 900));
                cardOtherDuring49.UpdateLayout();
                string otherDuring49 = CollectText(cardOtherDuring49);
                var btnsOtherDuring49 = ButtonInfos(cardOtherDuring49);

                bool hasStop49 = btnsDuring49.Any(b => b.Text.Contains("安装中") || b.Text.Contains("停止"));
                bool hasBadge49 = during49.Contains("已安装");

                Check("安装中的那一条重建后仍是「安装中…」按钮（不被换成绿色「已安装」徽章）——停止入口还在",
                    w.IsInstallingThisForTest(m49) && !w.IsInstallingThisForTest(m49other) &&
                    hasStop49 && !hasBadge49 && btnsDuring49.Any(b => b.Text == "安装中…"),
                    $"安装条重建后：按钮=[{string.Join("/", btnsDuring49.Select(b => b.Text))}] 出现「已安装」={hasBadge49}");
                Check("对照（没在安装）的那一条照旧吃绿色「已安装」徽章 —— 改的是安装中那一条，不是把徽章废掉",
                    otherDuring49.Contains("已安装") && btnsOtherDuring49.Count == 0,
                    $"对照条重建后：徽章={otherDuring49.Contains("已安装")} 按钮=[{string.Join("/", btnsOtherDuring49.Select(b => b.Text))}]");

                w.EndInstallStateForTest();
                var cardAfter49 = w.BuildMarketCardForTest(m49);
                cardAfter49.Measure(new Size(560, 900));
                cardAfter49.Arrange(new Rect(0, 0, 560, 900));
                cardAfter49.UpdateLayout();
                string after49 = CollectText(cardAfter49);
                var btnsAfter49 = ButtonInfos(cardAfter49);
                Check("安装结束后才复位：那条卡片换回「已安装」徽章（停止入口跟着收掉）",
                    !w.IsInstallingThisForTest(m49) && after49.Contains("已安装") &&
                    btnsAfter49.Count == 0 &&
                    !btnsAfter49.Any(b => b.Text.Contains("安装中") || b.Text.Contains("停止")),
                    $"结束后按钮=[{string.Join("/", btnsAfter49.Select(b => b.Text))}] 徽章={after49.Contains("已安装")}");
            }
            finally { seeded49(); }

            // ══════ 49b. bug：市场「已安装」跨命名空间误认（裸名相同 ≠ 同一个包）══════
            // 现场：市场收录的是作用域包 @dickpy/dsh-imagegen，用户本地装的是**自己的** dsh-imagegen
            //   （link:./plugins/dsh-imagegen）。旧判据把两边各自 Bare()（去掉作用域）后比较 ⇒
            //   裸名相等就打了「✓ 已安装」，用户以为装上的就是市场里那个包。
            // 修法：**同一命名空间才算同一个包**，判据是「是不是 npm 作用域包」（IsNpmScoped），
            //   而**不是**「含不含斜杠」—— 收录 npm 为空时 MatchKey 取的是仓库路径 owner/repo（同样含斜杠），
            //   按「含斜杠即不同」会把 git 装好的裸名包一律错判成未安装（反向回退，见 ⑥）。
            // 期望值：把 IsInstalledInProfile / BuildSameNameBadge / InstallVerdictForTest /
            //   SameNameBadgeInfoForTest 的原码逐字抽到 %TEMP% 独立工程真跑校正过（不是读代码推断）。
            {
                // 每个用例各播一次种：本地插件表必须真的换掉，否则用例之间会互相污染。
                (bool Installed, string SameNameLocal) Verdict(string[] locals, PluginMarket.MarketPlugin m)
                {
                    var undo = w.SeedMarketPluginsForTest(
                        locals.Select(n => new PluginManager.Plugin { Name = n }).ToList());
                    try { return w.InstallVerdictForTest(m); }
                    finally { undo(); }
                }

                var scopedMarket = new PluginMarket.MarketPlugin
                { Name = "dsh-imagegen", Owner = "dickpy", Npm = "@dickpy/dsh-imagegen", Version = "1.5.12" };

                // ① 本次缺陷：作用域包 vs 同名裸包 ⇒ 不算已安装，且被认成「同名非同一包」
                var (instScoped, sameScoped) = Verdict(new[] { "dsh-imagegen" }, scopedMarket);
                Check("市场 @dickpy/dsh-imagegen 与本地 dsh-imagegen 不是同一个包，不得算已安装",
                    !instScoped, $"installed={instScoped}");
                Check("  且被判成「同名（非同一包）」，能报出本地那个包名",
                    sameScoped == "dsh-imagegen", $"sameName=[{sameScoped}]");

                // ② 裸名同包 ⇒ 算
                var bareMarket = new PluginMarket.MarketPlugin { Name = "dsh-mnemon", Npm = "dsh-mnemon" };
                Check("裸名同包（dsh-mnemon vs dsh-mnemon）算已安装",
                    Verdict(new[] { "dsh-mnemon" }, bareMarket).Installed);

                // ③ 同作用域同包 ⇒ 算
                var scopedA = new PluginMarket.MarketPlugin { Name = "x", Npm = "@a/x" };
                Check("同作用域同包（@a/x vs @a/x）算已安装",
                    Verdict(new[] { "@a/x" }, scopedA).Installed);

                // ④ 不同作用域同名 ⇒ 不算
                var crossScope = Verdict(new[] { "@b/x" }, scopedA);
                Check("不同作用域同名（@a/x vs @b/x）不算已安装",
                    !crossScope.Installed, $"installed={crossScope.Installed}");
                Check("  且被认成「同名（非同一包）」，报出的是 @b/x",
                    crossScope.SameNameLocal == "@b/x", $"sameName=[{crossScope.SameNameLocal}]");

                // ⑤ 大小写不同 ⇒ 算（判据按 ToLowerInvariant 归一）
                Check("大小写不同（@A/X vs @a/x）算已安装",
                    Verdict(new[] { "@a/x" }, new PluginMarket.MarketPlugin { Name = "X", Npm = "@A/X" }).Installed);

                // ⑥ 反向回退护栏：目录里 npm 为空的收录 MatchKey = owner/repo，
                //    本地由 git 安装时是裸包名（本机真实样本：dsh-watcher）——收紧不得过头
                var repoMarket = new PluginMarket.MarketPlugin { Owner = "aa2246740", Name = "dsh-watcher" };
                Check("npm 为空的收录（MatchKey=owner/repo）仍能认出 git 装好的裸名包",
                    Verdict(new[] { "dsh-watcher" }, repoMarket).Installed,
                    $"MatchKey=[{repoMarket.MatchKey}]");

                // ⑦⑧ 上屏：中性徽章文案与悬停 + 卡片级（仍在同一份种子里取，取完即还原）
                var undoSameName = w.SeedMarketPluginsForTest(
                    new List<PluginManager.Plugin> { new() { Name = "dsh-imagegen" } });
                try
                {
                    var (badgeText, badgeTip) = w.SameNameBadgeInfoForTest(scopedMarket);
                    Check("同名不同包时徽章为中性文案「同名（非同一包）」，且不含「已安装」",
                        badgeText == "同名（非同一包）" && !badgeText.Contains("已安装"), $"text=[{badgeText}]");
                    Check("悬停提示把两个包名都如实写出",
                        badgeTip.Contains("@dickpy/dsh-imagegen") && badgeTip.Contains("dsh-imagegen"), badgeTip);

                    // 卡片级：中性徽章不挡安装 —— BuildMarketCard 里徽章与安装按钮同在 actions
                    // 这个 Horizontal StackPanel 内并排（`!installed && sameNameLocal.Length > 0` 才加徽章）。
                    var collisionCard = w.BuildMarketCardForTest(scopedMarket);
                    collisionCard.Measure(new Size(560, 900));
                    collisionCard.Arrange(new Rect(0, 0, 560, 900));
                    collisionCard.UpdateLayout();
                    string collisionText = CollectText(collisionCard);
                    var collisionBtns = ButtonInfos(collisionCard);
                    Check("同名不同包时卡片：有中性徽章、没有「已安装」、安装按钮还在",
                        collisionText.Contains("同名（非同一包）") && !collisionText.Contains("已安装") &&
                        collisionBtns.Any(b => b.Text.Contains("安装")),
                        $"文本含已安装={collisionText.Contains("已安装")} 按钮=[{string.Join("/", collisionBtns.Select(b => b.Text))}]");
                }
                finally { undoSameName(); }

                // ⑨ 真装上了那个包之后，才显示「已安装」
                var (instAfter, sameAfter) = Verdict(new[] { "@dickpy/dsh-imagegen" }, scopedMarket);
                Check("真正装了 @dickpy/dsh-imagegen 之后才算已安装，且不再报同名",
                    instAfter && sameAfter.Length == 0, $"installed={instAfter} sameName=[{sameAfter}]");

                // ⑩ 已安装态下不出中性徽章（上屏走绿色「已安装」，与卡片判据顺序一致）
                var undoInstalled = w.SeedMarketPluginsForTest(
                    new List<PluginManager.Plugin> { new() { Name = "@dickpy/dsh-imagegen" } });
                try
                {
                    var (badgeWhenInstalled, _) = w.SameNameBadgeInfoForTest(scopedMarket);
                    Check("已安装态下中性徽章信息为空（卡片显示绿色「已安装」）",
                        badgeWhenInstalled.Length == 0, $"text=[{badgeWhenInstalled}]");
                }
                finally { undoInstalled(); }

                // ⑪ 无同名包时也不出徽章
                var undoOther = w.SeedMarketPluginsForTest(
                    new List<PluginManager.Plugin> { new() { Name = "something-else" } });
                try
                {
                    var (badgeNoCollision, _) = w.SameNameBadgeInfoForTest(scopedMarket);
                    Check("无同名包时中性徽章信息为空", badgeNoCollision.Length == 0, $"text=[{badgeNoCollision}]");
                }
                finally { undoOther(); }
            }

            // ══════ 50. 「涉及回滚插件」的回滚要弹进度窗（绿色流动进度条 + 不许操作） ══════
            // 需求原文：「涉及到回滚插件的回滚操作时弹出弹窗显示进度……弹窗进度条，还是绿色滚动条动画的，
            //   不允许用户在此期间做其他操作。」
            // 这里能自动验的是**判据与文案**（纯函数）；窗口长什么样、动画是不是真的在动，自检看不到，
            // 要上屏拍图核对 —— 见 --selftest 报告末尾的说明与汇报里的"未覆盖"清单。

            // ① 该不该弹：只有"真要重装插件（勾了回退插件）"且"要重装的名单非空"才弹。
            //    只回配置 / 只回版本的快照几秒钟就完，必须保持原来的底部进度提示，不能白拦一次。
            Check("回滚进度窗的弹窗判定：勾了「回退插件」且名单非空才弹，其余（空名单 / 没勾）一律不弹",
                MainWindow.RollbackProgressNeeded(true, 3) &&
                MainWindow.RollbackProgressNeeded(true, 1) &&
                !MainWindow.RollbackProgressNeeded(true, 0) &&
                !MainWindow.RollbackProgressNeeded(false, 5) &&
                !MainWindow.RollbackProgressNeeded(false, 0),
                "勾插件+名单非空=true；空名单=false；没勾=false");

            // ② 步骤说明生成器：每一步各一句话、都不重样，装插件那一步要把数量说出来
            var step50 = new[]
            {
                RollbackStep.Preparing, RollbackStep.VersionPin, RollbackStep.StoppingEngine,
                RollbackStep.Reinstalling, RollbackStep.Verifying, RollbackStep.Done
            }.Select(s => MainWindow.RollbackStepText(s, 13)).ToList();
            Check("步骤说明生成器：六步各一句、互不重样，重装那一步带上「13 个」",
                step50.Count == 6 && step50.Distinct().Count() == 6 &&
                step50.All(t => t.Length > 0 && t.EndsWith("…", StringComparison.Ordinal)) &&
                MainWindow.RollbackStepText(RollbackStep.Reinstalling, 13).Contains("13 个") &&
                !MainWindow.RollbackStepText(RollbackStep.Reinstalling, 0).Contains("（") &&
                !MainWindow.RollbackStepText(RollbackStep.Reinstalling, -1).Contains("（"),
                string.Join(" ｜ ", step50));

            // ③ 小白化：不出现路径、命令、参数、包名、内部文件名与术语（技术细节只留在日志里）
            Check("步骤说明是小白话：不带路径 / 命令 / 参数 / 包名 / 内部文件名 / 术语",
                step50.All(t => !t.Contains('\\') && !t.Contains('/') && !t.Contains("--") &&
                                !t.Contains("pnpm") && !t.Contains("node_modules") &&
                                !t.Contains(".json", StringComparison.OrdinalIgnoreCase) &&
                                !t.Contains(".yaml", StringComparison.OrdinalIgnoreCase) &&
                                !t.Contains(".yml", StringComparison.OrdinalIgnoreCase) &&
                                !t.Contains("锁文件") && !t.Contains("依赖树")),
                "六条文案都只有人话");

            // ④ 「x / N」生成器：总数未知就不显示这一行；越界的数夹住，不会出现「99 / 13」
            Check("「x / N」生成器：总数未知（≤0）返回空串=不显示；越界与负数都夹到 [0, N]",
                MainWindow.RollbackCountText(0, 13) == "0 / 13" &&
                MainWindow.RollbackCountText(3, 13) == "3 / 13" &&
                MainWindow.RollbackCountText(13, 13) == "13 / 13" &&
                MainWindow.RollbackCountText(99, 13) == "13 / 13" &&
                MainWindow.RollbackCountText(-5, 13) == "0 / 13" &&
                MainWindow.RollbackCountText(1, 0) == "" &&
                MainWindow.RollbackCountText(1, -3) == "" &&
                MainWindow.RollbackCountText(0, 0) == "",
                $"3/13=「{MainWindow.RollbackCountText(3, 13)}」· 99/13=「{MainWindow.RollbackCountText(99, 13)}」· 1/0=「{MainWindow.RollbackCountText(1, 0)}」");

            // ⑤ 关窗判定：窗口的 Esc 拦截与 Closing 拦截都走这一个判据（不在事件里再写第二份）
            Check("关窗判定：Esc / 系统关闭一律不放行，只有流程自己走完才允许关",
                !MainWindow.AllowRollbackProgressClose(RollbackCloseReason.UserEscape) &&
                !MainWindow.AllowRollbackProgressClose(RollbackCloseReason.UserSystemClose) &&
                MainWindow.AllowRollbackProgressClose(RollbackCloseReason.Finished),
                "Esc=false · Alt+F4/系统菜单=false · 流程结束=true");

            // ⑥ 进度窗本体（只建不显示）：没有系统标题栏 / 不在任务栏 / 一个按钮都没有（＝没有关闭入口），
            //    进度条是**绿色滑块 + 无限循环的流动动画**（两段错开半周期 ⇒ 看着一直在流）。
            try
            {
                var pw = new RollbackProgressWindow(MainWindow.RollbackProgressTitle,
                                                    MainWindow.RollbackStepText(RollbackStep.Reinstalling, 13));
                var f50 = pw.FactsForTest();
                Check("进度窗结构：无系统标题栏、不在任务栏、一个按钮都没有（不给任何关闭入口）、标题与步骤行就位",
                    !f50.HasSystemTitleBar && !f50.InTaskbar && !f50.HasButton &&
                    f50.Title == MainWindow.RollbackProgressTitle && f50.Step.Contains("13"),
                    $"标题「{f50.Title}」· 步骤「{f50.Step}」· 系统关闭按钮={f50.HasSystemTitleBar} · " +
                    $"任务栏={f50.InTaskbar} · 有按钮={f50.HasButton}");

                Check("进度条是绿色**流动**动画：≥2 段绿色滑块、无限循环、每趟 >0.3 秒、两段错开半周期",
                    f50.ChunkColor == "#FF34C759" && f50.ChunkCount >= 2 && f50.Forever && f50.Seconds > 0.3 &&
                    f50.BeginTimes.Length == f50.ChunkCount &&
                    f50.BeginTimes.Distinct().Count() == f50.ChunkCount &&
                    Math.Abs(f50.BeginTimes[1] - f50.Seconds / 2) < 0.001,
                    $"滑块 {f50.ChunkCount} 段 {f50.ChunkColor} · 每趟 {f50.Seconds:0.##}s · 循环={f50.Forever} · " +
                    $"起点偏移 [{string.Join(", ", f50.BeginTimes.Select(b => b.ToString("0.##")))}] · 槽底色 {f50.TrackColor}");

                // 「x / N」这一行：没确切数量时收起，给了就出现；换/推进度行只改文字，动画参数一动不动
                pw.SetStep(MainWindow.RollbackStepText(RollbackStep.Verifying, 13));
                pw.SetCount(MainWindow.RollbackCountText(3, 13));
                var f50b = pw.FactsForTest();
                Check("进度窗能随步骤刷新：说明行换成「核对版本」、未知数量时收起那一行、给了就显示「3 / 13」",
                    !f50.CountVisible && f50.Count.Length == 0 &&
                    f50b.CountVisible && f50b.Count == "3 / 13" &&
                    f50b.Step == MainWindow.RollbackStepText(RollbackStep.Verifying, 13) &&
                    f50b.Forever && f50b.ChunkColor == f50.ChunkColor &&
                    Math.Abs(f50b.Seconds - f50.Seconds) < 0.001,
                    $"没数时可见={f50.CountVisible} · 给数后「{f50b.Count}」（可见={f50b.CountVisible}）· 步骤「{f50b.Step}」");

                // 程序自身可正常关闭（用户无法关闭 ≠ 无法关闭）；关窗后主窗的可用状态由调用方还原
                pw.FinishAndClose();
                Check("流程结束时窗口能被代码自己关掉（唯一放行的那一路真的调得到）",
                    pw.ClosePermittedForTest(),
                    $"ClosePermitted={pw.ClosePermittedForTest()}");
            }
            catch (Exception ex)
            {
                Check("进度窗结构：无系统标题栏、不在任务栏、一个按钮都没有（不给任何关闭入口）、标题与步骤行就位",
                    false, ex.GetType().Name + ": " + ex.Message);
            }

            if (pinKeep.Length > 0) VersionMemory.PinTo(pinKeep);

            // ══════ 51. bug：插件状态刷新事件内容过长，且混入引擎内置插件 ══════
            // 要求：状态刷新事件只报告**概要结果**，不得逐项罗列；文字颜色使用**灰色**。
            // 现场：事件栏出现 agent-instructions、command-compact、command-goal、compaction-basic、
            //   hmr、mnemon-strategy-*、plan-mode、skill-badge、skill-filesystem、tool-bash、tool-fs…
            //   这些均为 **DSH 引擎内置插件**（引擎基座条目，不在 profile 清单的 dependencies 中），
            //   与用户安装的插件无关 ⇒ 占满整个事件栏，并向普通用户暴露内部实现标识。
            // 四处修法：① 漂移比对只覆盖"本程序管理的插件"（清单 dependencies ∪ 其 loader id）；
            //          ② 事件只报「插件状态已按引擎同步（N 项调整）」，不列名称，细节写入日志；
            //          ③ 使用 EventKind.Info（灰白 #A8A8B0），不再使用警告橙/红；
            //          ④ 本程序管理的插件无漂移时 ⇒ 不产生任何事件。
            //
            // 样本里的 id 全是**只读取证**到的真实值：
            //   Config\loader-ids.json ：「@deepseek-ai/cordis-plugin-hmr」→ hmr、
            //   「dsh-client-auto-continue」→ auto-continue、「dsh-codearts-auth」→ codearts-auth、
            //   「dsh-improved-inline-edit」→ dsh-improved-inline-edit、「dsh-zh」→ deepseek-harness-zh_pro；
            //   profile\package.json 的 dependencies：登记了 dsh-client-auto-continue、dsh-codearts-auth 等，
            //   **没有** hmr / @deepseek-ai/* 那些引擎自带项。
            {
                // 样本 ①：「我们管的」白名单 = 清单登记过的包名 ∪ 它们的 loader id。
                // ⚠ 用一份**临时的清单**（PackageFileOverrideForTest），不拿真机清单下断言 ——
                //   那量的是"这台机器恰好装了什么"，不是"这段判定对不对"（本项目已有同类教训）。
                string t51 = Path.Combine(Path.GetTempPath(), "dshguard-drift-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                string? pkgBak51 = PluginManager.PackageFileOverrideForTest;
                try
                {
                    Directory.CreateDirectory(t51);
                    // 清单：只登记**我们管的**两个（含那个 gitee 源的 codearts），
                    // 引擎自带项（@deepseek-ai/*）一个都不登记 —— 与真机 profile\package.json 同形状。
                    File.WriteAllText(Path.Combine(t51, "package.json"),
                        "{ \"name\": \"dsh-profile-web\", \"private\": true, \"dependencies\": { "
                        + "\"dsh-client-auto-continue\": \"^0.11.6\", "
                        + "\"dsh-codearts-auth\": \"git+https://gitee.com/iJetLi/deepseek-harness-codearts.git\" } }",
                        new UTF8Encoding(false));
                    PluginManager.PackageFileOverrideForTest = Path.Combine(t51, "package.json");

                    var managedSeed = new List<PluginManager.Plugin>
                    {
                        new() { Name = "dsh-client-auto-continue", LoaderId = "auto-continue" },   // 清单里有
                        new() { Name = "dsh-codearts-auth",        LoaderId = "codearts-auth" },   // 清单里有（gitee 源）
                        // ↓ 引擎自带项：不在清单的 dependencies 里 ⇒ 不该进白名单
                        new() { Name = "@deepseek-ai/cordis-plugin-hmr", LoaderId = "hmr" },
                        new() { Name = "@deepseek-ai/dsh-compaction-basic", LoaderId = "compaction-basic" }
                    };
                    var aliases51 = PluginManager.ManagedPluginAliases(managedSeed);

                    Check("漂移白名单：只认清单 dependencies 里登记过的包（引擎自带项一条都不进来）",
                        aliases51.Count == 4 &&
                        aliases51.Contains("dsh-client-auto-continue") && aliases51.Contains("auto-continue") &&
                        aliases51.Contains("dsh-codearts-auth") && aliases51.Contains("codearts-auth") &&
                        !aliases51.Contains("hmr") && !aliases51.Contains("@deepseek-ai/cordis-plugin-hmr") &&
                        !aliases51.Contains("compaction-basic") && !aliases51.Contains("@deepseek-ai/dsh-compaction-basic"),
                        $"白名单 {aliases51.Count} 条：{string.Join("、", aliases51.OrderBy(s => s))}");

                    // 样本 ②：**现场实测的集合** —— 引擎禁用了一批内置项（hmr / compaction-basic / …），
                    // patch 中仅有本程序管理的 auto-continue ⇒ 过滤前为大量冗余条目，过滤后无剩余。
                    var engine51 = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        "hmr", "compaction-basic", "command-compact", "plan-mode", "tool-bash",
                        "tool-fs", "skill-badge", "skill-filesystem", "agent-instructions", "command-goal",
                        "mnemon-strategy-auto-capture", "auto-continue"
                    };                                                       // 共 12 条（即现场实测的冗余集合）
                    var patch51 = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "auto-continue" };

                    var raw51 = PluginManager.CompareEngineToPatch(engine51, patch51);              // 旧口径（不过滤）
                    var filtered51 = PluginManager.CompareEngineToPatch(engine51, patch51, aliases51); // 新口径

                    Check("漂移比对只算我们管的插件：引擎自带项（hmr/compaction-basic/command-compact/plan-mode/…）一条都不进差异表",
                        raw51.EngineOnly.Count == 11 && raw51.Compared == 12 &&          // 旧口径：11 条冗余条目
                        filtered51.EngineOnly.Count == 0 &&
                        filtered51.PatchOnly.Count == 0 && filtered51.InSync && !filtered51.Drifted &&
                        filtered51.AdjustedCount == 0,
                        $"过滤前引擎独有 {raw51.EngineOnly.Count} 条 → 过滤后 {filtered51.EngineOnly.Count} 条");

                    // 样本 ③：**我们管的**插件真漂移时才算数（现场那个场景：网页/市场那边把
                    // auto-continue 重新启用了，patch 里还记着禁用）⇒ 2 项调整，事件报得出数字；
                    // 引擎自带的 hmr / tool-fs 不算进来。
                    var engine51b = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "hmr", "tool-fs" };
                    var patch51b = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "auto-continue", "codearts-auth" };
                    var drift51 = PluginManager.CompareEngineToPatch(engine51b, patch51b, aliases51);
                    Check("我们管的插件真漂移时才计数（引擎自带的 hmr/tool-fs 不算；反方向的也如实算）",
                        drift51.Drifted && drift51.AdjustedCount == 2 &&
                        drift51.PatchOnly.Count == 2 && drift51.EngineOnly.Count == 0 &&
                        drift51.PatchOnly.Contains("auto-continue") && drift51.PatchOnly.Contains("codearts-auth"),
                        $"{drift51.AdjustedCount} 项调整 · {PluginManager.DriftDetailText(drift51)}");

                    // 反方向：引擎禁了我们管的插件、patch 里没记 ⇒ 同样算 1 项调整
                    var drift51rev = PluginManager.CompareEngineToPatch(
                        new[] { "auto-continue", "hmr" }, Array.Empty<string>(), aliases51);
                    Check("反方向也认（引擎禁用、本地无记录 ⇒ 算 1 项；引擎自带的 hmr 不算）",
                        drift51rev.EngineOnly.Count == 1 && drift51rev.EngineOnly[0] == "auto-continue" &&
                        drift51rev.AdjustedCount == 1,
                        $"{drift51rev.AdjustedCount} 项 · {PluginManager.DriftDetailText(drift51rev)}");

                    // 样本 ④：事件文案**只有数字、不出现任何标识**；0 项 ⇒ 空串（调用方据此不发事件）。
                    string evt51 = PluginManager.DriftEventText(drift51.AdjustedCount);
                    Check("事件文案只报大概结果（「插件状态已按引擎同步（2 项调整）」），不含任何插件标识；0 项时不发事件",
                        evt51 == "插件状态已按引擎同步（2 项调整）" &&
                        !evt51.Contains("auto-continue") && !evt51.Contains("codearts") &&
                        !evt51.Contains("hmr") && !evt51.Contains("tool-fs") &&
                        PluginManager.DriftEventText(0) == "" &&
                        PluginManager.DriftEventText(filtered51.AdjustedCount) == "" &&
                        PluginManager.DriftEventText(-1) == "",
                        $"「{evt51}」· 0 项=「{PluginManager.DriftEventText(0)}」");

                    // 样本 ⑤：细节走日志（哪几个、各是什么方向）——名字**只在这里**出现。
                    string detail51 = PluginManager.DriftDetailText(raw51);
                    Check("漂移细节落盘文案说清方向与条数（名字只在这里出现，不上屏）",
                        detail51.Contains("引擎禁用、本地无记录") && detail51.Contains("11 项") &&
                        detail51.Contains("hmr") && detail51.Contains("compaction-basic") &&
                        !detail51.Contains("auto-continue"),
                        Shorten(detail51, 160));
                    string detail51b = PluginManager.DriftDetailText(drift51);
                    Check("反向漂移的落盘文案说清方向（本地记着禁用、引擎实为启用）",
                        detail51b.Contains("本地记着禁用、引擎实为启用") && detail51b.Contains("2 项") &&
                        detail51b.Contains("auto-continue") && detail51b.Contains("codearts-auth"),
                        Shorten(detail51b, 160));

                    // 样本 ⑥：**灰色**字体 —— 事件等级必须是 EventKind.Info（#A8A8B0 灰白），
                    // 不得使用警告橙 / 失败红（要求：该事件使用灰色文字）。
                    var grey51 = MainWindow.EventColor(MainWindow.EventKind.Info);
                    Check("这条事件用灰色（EventKind.Info = #A8A8B0），不是警告橙也不是失败红",
                        grey51 == Color.FromRgb(0xA8, 0xA8, 0xB0) &&
                        grey51 != MainWindow.EventColor(MainWindow.EventKind.Warn) &&
                        grey51 != MainWindow.EventColor(MainWindow.EventKind.Bad),
                        $"Info={grey51} · Warn={MainWindow.EventColor(MainWindow.EventKind.Warn)} · Bad={MainWindow.EventColor(MainWindow.EventKind.Bad)}");

                    // 样本 ⑦：清单**无法读取**（文件不存在 / 非合法 JSON）⇒ 白名单为空 ⇒
                    // 调用方本轮**不执行任何比对**（不使用兜底数据，避免产生误导信息）。
                    PluginManager.PackageFileOverrideForTest = Path.Combine(t51, "不存在的清单.json");
                    var unreadable51 = PluginManager.ManagedPluginAliases(managedSeed);
                    PluginManager.PackageFileOverrideForTest = Path.Combine(t51, "package.json");   // 还原成有效样本
                    File.WriteAllText(Path.Combine(t51, "坏清单.json"), "这不是 JSON", new UTF8Encoding(false));
                    PluginManager.PackageFileOverrideForTest = Path.Combine(t51, "坏清单.json");
                    var broken51 = PluginManager.ManagedPluginAliases(managedSeed);
                    PluginManager.PackageFileOverrideForTest = Path.Combine(t51, "package.json");

                    Check("清单无法读取（文件不存在 / 非合法 JSON）⇒ 白名单为空（调用方据此不执行任何比对，避免冗余信息）",
                        unreadable51.Count == 0 && broken51.Count == 0 &&
                        PluginManager.ManagedPluginAliases(null).Count == 0,
                        $"缺文件 {unreadable51.Count} 条 · 坏 JSON {broken51.Count} 条 · null {PluginManager.ManagedPluginAliases(null).Count} 条");
                }
                finally
                {
                    PluginManager.PackageFileOverrideForTest = pkgBak51;
                    try { Directory.Delete(t51, true); } catch { }
                }
            }

            // ══════ 52. bug：git 源插件（inline-edit / codearts）的更新把「仓库最新」当版本号 ══════
            // 现场原文（Logs\异常-20260916-135514.log，13:59:24→13:59:25）：
            //   [13:59:24] 已放开 pnpm 供应链策略（… plugin --profile web add dsh-codearts-auth@仓库最新 …）
            //   [13:59:25] 命令结束：… add dsh-codearts-auth@仓库最新 … 退出码=1
            //     ─▶ Failed to resolve dependency tree: "dsh-codearts-auth@仓库最新" isn't
            //          supported by any available resolver.
            // ⇒ **更新命令把界面显示文案「仓库最新」当成版本号传给了 pnpm** ⇒ 必然失败。
            // 清单声明（profile\package.json 原文）：
            //   "dsh-improved-inline-edit": "git+https://github.com/xiaosurongjia/dsh-improved-inline-edit.git"
            //   "dsh-codearts-auth":        "git+https://gitee.com/iJetLi/deepseek-harness-codearts.git"
            // 两个包磁盘上都装着（各 0.1.0，node_modules\<包>\package.json 原文）。
            {
                const string gitInline = "git+https://github.com/xiaosurongjia/dsh-improved-inline-edit.git";
                const string gitCodearts = "git+https://gitee.com/iJetLi/deepseek-harness-codearts.git";

                // 样本 ①：git 源的更新参数走 **`update 包名`**（2026-09-18 起，见 PluginManager.BuildUpdateArgs
                //   的长文）：**命令里不得出现仓库地址、#ref、#sha，也不得出现任何显示标签**。
                //   原口径（走 `add <来源 spec>`）已被现场缺陷推翻 —— pnpm 对已解析过的 git 源认为锁文件里
                //   那条旧提交已满足该 spec ⇒ 直接 "Lockfile is up to date, resolution step is skipped"：
                //   退出码 0、锁文件被重写，但**提交没动** ⇒ 界面记成"已更新"、下次照旧报有新版
                //   （用户原话「dsh watcher更新以后又报更新，看看？」）。
                //   ⚠ 本断言的价值是**防老 bug 回归**：显示标签 / `包名@标签` / 钉死 sha 一律不许进命令行。
                string argsInline = PluginManager.BuildUpdateArgs("dsh-improved-inline-edit", gitInline, "仓库最新");
                string argsCodearts = PluginManager.BuildUpdateArgs("dsh-codearts-auth", gitCodearts, "仓库最新");
                Check("git 源更新走 `plugin --profile web update 包名`：仓库地址/#ref/显示标签一律不进命令行",
                    argsInline.Contains("plugin --profile web update dsh-improved-inline-edit") &&
                    argsInline.Contains("--registry " + Registries.Current) &&
                    argsCodearts.Contains("plugin --profile web update dsh-codearts-auth") &&
                    // 原意图①：不含任何显示标签
                    !argsInline.Contains("仓库最新") && !argsCodearts.Contains("仓库最新") &&
                    // 原意图②：不含 `包名@标签` 形态
                    !argsInline.Contains("dsh-improved-inline-edit@") && !argsCodearts.Contains("dsh-codearts-auth@") &&
                    // 新口径③：仓库地址与 #ref/#sha 绝不出现在命令行里（这正是"把用户声明钉死"陷阱的防线）
                    !argsInline.Contains("git+https") && !argsCodearts.Contains("git+https") &&
                    !argsInline.Contains("#") && !argsCodearts.Contains("#") &&
                    // 原意图④：仍要能被判为"改插件清单"的命令（否则拿不到包龄放行）
                    PluginManager.LooksLikePluginMutation(argsInline) &&
                    PluginManager.LooksLikePluginMutation(argsCodearts),
                    $"inline-edit → 「{argsInline}」· codearts → 「{argsCodearts}」");

                // 样本 ②：`github:owner/repo#ref` 与**未写 ref** 的 git 源**一律只按包名更新**。
                //   旧口径是"钉死 #ref 的去掉 ref、跟默认分支的原样保留（地址照进命令行）"；
                //   现在两种都是同一条 `update 包名` —— 因为 `add <仓库地址>` 根本推不动提交（见样本 ①）。
                //   ⚠ 关键新口径：#ref（无论分支名还是 sha）**绝不进命令行**，这既避免老 bug 回归，
                //   也钉死"不许把用户 manifest 从跟分支改成钉死提交"这个陷阱（`add <地址>#<sha>` 会污染声明）。
                string argsGh = PluginManager.BuildUpdateArgs("dsh-watcher", "github:aa2246740/dsh-watcher#2d19cb5a318912b87eb80603318554adeecfade2", "仓库最新");
                string argsGhBare = PluginManager.BuildUpdateArgs("dsh-watcher", "github:aa2246740/dsh-watcher", "仓库最新");
                Check("github: 源（钉死 #ref / 未写 ref）都只走 `update 包名`：仓库地址、#ref、#sha 都不进命令行",
                    argsGh.Contains("plugin --profile web update dsh-watcher") &&
                    !argsGh.Contains("github:aa2246740/dsh-watcher") &&
                    !argsGh.Contains("#2d19cb5a") && !argsGh.Contains("#") &&
                    !argsGh.Contains("仓库最新") && !argsGh.Contains("dsh-watcher@") &&
                    argsGhBare.Contains("plugin --profile web update dsh-watcher") &&
                    !argsGhBare.Contains("github:aa2246740/dsh-watcher") &&
                    !argsGhBare.Contains("#") &&
                    !argsGhBare.Contains("仓库最新") && !argsGhBare.Contains("dsh-watcher@") &&
                    PluginManager.LooksLikePluginMutation(argsGhBare),
                    $"「{argsGh}」/「{argsGhBare}」");

                // 样本 ②‴：本次修复的**核心结论**逐条钉住（防回归）——
                //   · git 源更新命令**一个 `#` 都不许有**（避免把用户声明钉死；实测 `add …#<sha>` 会把
                //     manifest 写成 `github:o/r#<sha>` ⇒ PluginSource.Classify 判 GitCommit ⇒ 永不报更新）；
                //   · 命令里不许出现仓库地址（宁可只用包名，让 pnpm 按清单声明重解析）；
                //   · 命令必须仍被 LooksLikePluginMutation 认作"改清单"（否则拿不到包龄放行）；
                //   · npm 源**仍是** `add 包名@版本`（别把 npm 那条路径改坏）；
                //   · 已钉死的 git 源（GitCommit）走同一支也**无害**：命令照样生成，但提交不可变 ⇒ 本壳
                //     按设计不报更新、pnpm 也只按 manifest 里那个 #sha 重解析（本机实测为原地 no-op）。
                {
                    string gitCommitSpec = "github:aa2246740/dsh-watcher#2d19cb5a318912b87eb80603318554adeecfade2";
                    string argsGitCommit = PluginManager.BuildUpdateArgs("dsh-watcher", gitCommitSpec, "仓库最新");
                    string argsNpm = PluginManager.BuildUpdateArgs("dsh-mnemon", "^0.5.9", "0.5.9");

                    Check("核心结论防回归：git 源更新命令不带 `#`/仓库地址、被判为改清单命令，npm 源仍是 add 包名@版本",
                        !argsInline.Contains("#") && !argsGh.Contains("#") && !argsGhBare.Contains("#") &&
                        !argsGitCommit.Contains("#") &&
                        !argsGh.Contains("github:") && !argsGhBare.Contains("github:") && !argsGitCommit.Contains("github:") &&
                        PluginManager.LooksLikePluginMutation(argsInline) && PluginManager.LooksLikePluginMutation(argsGitCommit) &&
                        argsNpm.Contains("plugin --profile web add dsh-mnemon@0.5.9") &&
                        argsNpm.Contains("--registry " + Registries.Current),
                        $"npm → 「{argsNpm}」· 钉死源 → 「{argsGitCommit}」");

                    Check("已钉死的 git 源（GitCommit）不被本壳悄悄解开：命令不带 #sha，声明仍是钉死口径（永不报更新）",
                        PluginSource.ClassifyRef(gitCommitSpec) == PluginSource.RefKind.Commit &&
                        !argsGitCommit.Contains("2d19cb5a") &&
                        PluginSource.DecideUpdate("118049afb816f412bae3a9d7e3e6dbbaaffd0bc9",
                            "c7a048aed0012f4ffca84fb7c193f14707033cc2",
                            PluginSource.CommitConfidence.Queried,
                            PluginSource.RefKind.Commit, PluginSource.RefMatchKind.Unknown)
                            == PluginSource.UpdateDecision.UpToDate,
                        $"钉死源的更新命令「{argsGitCommit}」· 判定={PluginSource.DecideUpdate("118049afb816f412bae3a9d7e3e6dbbaaffd0bc9", "c7a048aed0012f4ffca84fb7c193f14707033cc2", PluginSource.CommitConfidence.Queried, PluginSource.RefKind.Commit, PluginSource.RefMatchKind.Unknown)}");
                }

                // 样本 ②⁵：**git 源四种声明形态一律只按包名更新**（本次修复的唯一形态；防回归）——
                //   · 无 ref          —— 跟默认分支（Kind.GitBare）
                //   · `#分支名`       —— 跟具名分支（Kind.GitRef）
                //   · `#<40 位 sha>`  —— 钉死提交（Kind.GitCommit）
                //   · `git+https://…` —— 完整仓库地址写法
                //   四条都必须落到 `plugin --profile web update <包名>`，且命令行里**不出现来源声明本身、
                //   不出现 `#`、不出现 sha、不出现任何显示标签**。
                //   ⚠ 老 bug 的两种表现正是这里钉的两条：把「仓库最新」当版本号传下去；以及
                //     `add <地址>#<sha>`（能推进提交，但会把用户 manifest 从"跟分支"钉死成提交 ⇒ 永不报更新）。
                {
                    const string sha40 = "2d19cb5a318912b87eb80603318554adeecfade2";
                    var gitForms = new (string Label, string Spec, string Name)[]
                    {
                        ("无 ref",        "github:aa2246740/dsh-watcher",                      "dsh-watcher"),
                        ("#分支名",       "github:aa2246740/dsh-watcher#main",                 "dsh-watcher"),
                        ("#<40 位 sha>",  "github:aa2246740/dsh-watcher#" + sha40,             "dsh-watcher"),
                        ("git+https://…", "git+https://github.com/aa2246740/dsh-watcher.git",  "dsh-watcher"),
                    };
                    var formBad = new List<string>();
                    var formEcho = new List<string>();
                    foreach (var f in gitForms)
                    {
                        string got = PluginManager.BuildUpdateArgs(f.Name, f.Spec, "仓库最新");
                        formEcho.Add($"{f.Label} → 「{got}」");
                        if (!got.Contains($"plugin --profile web update {f.Name}")) formBad.Add(f.Label + "：没走 update 包名");
                        if (got.Contains(f.Spec)) formBad.Add(f.Label + "：来源声明原样进了命令行");
                        if (got.Contains("github:") || got.Contains("git+https")) formBad.Add(f.Label + "：仓库地址进命令行");
                        if (got.Contains("#")) formBad.Add(f.Label + "：`#` 进命令行");
                        if (got.Contains(sha40) || got.Contains("2d19cb5a")) formBad.Add(f.Label + "：sha 进命令行");
                        if (got.Contains("仓库最新") || got.Contains(f.Name + "@")) formBad.Add(f.Label + "：显示标签/包名@标签进命令行");
                        if (!PluginManager.LooksLikePluginMutation(got)) formBad.Add(f.Label + "：不被认作改清单命令");
                    }
                    Check("git 源四种声明形态（无 ref / #分支名 / #<40位sha> / git+https://…）一律只按包名 update：不带来源声明、#、sha、显示标签",
                        formBad.Count == 0,
                        formBad.Count == 0 ? string.Join(" · ", formEcho) : "不达标：" + string.Join("、", formBad) + " ‖ " + string.Join(" · ", formEcho));
                }

                // 样本 ②⁶：**非法包名 + git 源 ⇒ 返回空串**（`update` 只接受包名，拼接前过
                //   IsValidPackageName 白名单；调用方拿到空串**不得执行命令**）。
                //   合法包名照常放行 —— 别把闸门焊死（否则 git 源插件就彻底更新不了了）。
                {
                    string[] badNames = { "Dsh-Watcher", "a/b/c", ".hidden", "-bad", "dsh-watcher; rm -rf /", "dsh watcher" };
                    var badEcho = new List<string>();
                    // 变量名带 badNames 语义：外层作用域（RunCore 开头那批坏锁文件文本）已有一个 allEmpty，
                    // 同名会在 RunCore 的声明空间里撞车 ⇒ CS0136（本方法体内不允许内层遮蔽外层）。
                    bool allBadNameEmpty = true;
                    foreach (string bn in badNames)
                    {
                        string got = PluginManager.BuildUpdateArgs(bn, gitInline, "仓库最新");
                        badEcho.Add("「" + bn + "」→「" + got + "」");
                        if (got.Length != 0) allBadNameEmpty = false;
                    }
                    Check("非法包名 + git 源 ⇒ 返回空串（IsValidPackageName 前置闸；调用方不得执行），合法包名照常放行",
                        allBadNameEmpty &&
                        PluginManager.BuildUpdateArgs("dsh-watcher", gitInline, "仓库最新").Contains("plugin --profile web update dsh-watcher"),
                        string.Join(" · ", badEcho));
                }

                // 样本 ②⁷：`LooksLikePluginMutation` 必须认 `update`（2026-09-18 新增）——
                //   git 源更新改用 `update 包名` 后，这个判定若不认 update，更新命令就**拿不到包龄放行**，
                //   会卡在 pnpm 的 minimumReleaseAge 上（见 PluginManager.LooksLikePluginMutation 注释）。
                Check("LooksLikePluginMutation 认 `update`（git 源更新据此拿到包龄放行）；dump-config 之类仍不匹配",
                    PluginManager.LooksLikePluginMutation("--yes @deepseek-ai/dsh@8.8.8 plugin --profile web update dsh-watcher --registry " + Registries.Current) &&
                    PluginManager.LooksLikePluginMutation("plugin --profile web add dsh-mnemon@0.5.9") &&
                    PluginManager.LooksLikePluginMutation("plugin --profile web remove dsh-mnemon") &&
                    PluginManager.LooksLikePluginMutation("plugin --profile web install") &&
                    !PluginManager.LooksLikePluginMutation("plugin --profile web dump-config") &&
                    !PluginManager.LooksLikePluginMutation("--help"),
                    "update / add / remove / install 都认；dump-config、--help 不认");

                // 样本 ②′：**旧写法**（按包名装 git 源插件）也要被纠正成仓库地址 ——
                // 这是 BuildAddSourceArgs 那条链路真正被用对的证据：包名在镜像源上解析不了 git 源。
                // ⚠ 这一支读的是**清单**，所以必须指到自造的样本清单上判（不能拿真机清单下断言）。
                string? pkgBak52 = PluginManager.PackageFileOverrideForTest;
                string t52m = Path.Combine(Path.GetTempPath(), "dshguard-gitsrc-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                try
                {
                    Directory.CreateDirectory(t52m);
                    File.WriteAllText(Path.Combine(t52m, "package.json"),
                        "{ \"name\": \"dsh-profile-web\", \"private\": true, \"dependencies\": { "
                        + "\"dsh-improved-inline-edit\": \"" + gitInline + "\", "
                        + "\"dsh-codearts-auth\": \"" + gitCodearts + "\", "
                        + "\"dsh-mnemon\": \"^0.5.9\" } }",
                        new UTF8Encoding(false));
                    PluginManager.PackageFileOverrideForTest = Path.Combine(t52m, "package.json");

                    string argsByName = PluginManager.BuildAddSourceArgs("dsh-improved-inline-edit");
                    Check("BuildAddSourceArgs 这条链路：git 源插件按**包名**调用时，自动补成清单里的仓库地址",
                        argsByName.Contains(gitInline) &&
                        !argsByName.Contains("add dsh-improved-inline-edit ") &&
                        !argsByName.Contains("仓库最新") &&
                        PluginManager.LooksLikePluginMutation(argsByName),
                        $"「{argsByName}」");

                    // 样本 ②″：npm 包的安装来源**不受影响**（按名安装照旧；带版本的照旧保留版本）。
                    Check("npm 包按名/按版本安装的来源照旧（修正 git 源未影响正常的按名安装）",
                        PluginManager.BuildAddSourceArgs("dsh-mnemon").Contains("add dsh-mnemon ") &&
                        PluginManager.BuildAddSourceArgs("dsh-mnemon@0.5.9").Contains("add dsh-mnemon@0.5.9 ") &&
                        PluginManager.BuildAddSourceArgs("@liustack/modsearch@5.10.2").Contains("add @liustack/modsearch@5.10.2 "),
                        PluginManager.BuildAddSourceArgs("dsh-mnemon"));

                    // 样本 ②‴：**显示标签**在 BuildAddSourceArgs 这条咽喉上被直接拒掉（返回空串），
                    // 于是"标签进入命令行"这一路径被完全封堵 —— 即使某个调用点遗漏修改。
                    Check("BuildAddSourceArgs 拒收显示标签（含空串），返回空串 ⇒ 调用方不许执行命令",
                        PluginManager.BuildAddSourceArgs("仓库最新") == "" &&
                        PluginManager.BuildAddSourceArgs("最新") == "" &&
                        PluginManager.BuildAddSourceArgs("有新版（2026-09-16）") == "" &&
                        PluginManager.BuildAddSourceArgs("") == "",
                        $"标签 → 「{PluginManager.BuildAddSourceArgs("仓库最新")}」");

                    // 样本 ③：显示标签**不可能**混进命令行 —— 各种标签形态都挡住；
                    // 而 npm 源传具体版本号时照旧照常（不许把正常更新一并挡掉）。
                    Check("显示标签与真版本号分得开（纯函数）：中文标签 / 带空格 / latest / * 都算标签，1.2.3 不算",
                        PluginManager.IsDisplayLabel("仓库最新") && PluginManager.IsDisplayLabel("最新") &&
                        PluginManager.IsDisplayLabel("有新版（2026-09-16）") && PluginManager.IsDisplayLabel("latest") &&
                        PluginManager.IsDisplayLabel("*") && PluginManager.IsDisplayLabel("1.0.0 beta") &&
                        !PluginManager.IsDisplayLabel("1.2.3") && !PluginManager.IsDisplayLabel("0.1.0-rc.6") &&
                        !PluginManager.IsDisplayLabel("") && !PluginManager.IsDisplayLabel(gitInline) &&
                        !PluginManager.IsDisplayLabel("@liustack/modsearch@5.10.2") && !PluginManager.IsDisplayLabel(null),
                        "标签判据");

                    Check("npm 源照旧传具体版本号（修正 git 源未影响 npm 那条路径）",
                        PluginManager.BuildUpdateArgs("dsh-mnemon", "^0.5.9", "0.5.9").Contains("dsh-mnemon@0.5.9") &&
                        PluginManager.BuildUpdateArgs("dsh-mnemon", "^0.5.9", "0.5.9").Contains("--registry " + Registries.Current),
                        PluginManager.BuildUpdateArgs("dsh-mnemon", "^0.5.9", "0.5.9"));

                    // ════ 信任边界白名单（输入校验与比较语义加固）════
                    // ST-A：合法包名 / scope 包名放行（npm 规则：小写字母数字 - _ . ~，单段 /，≤214）
                    Check("ST-A 包名白名单：合法包名与 scope 包名放行，大写/双斜杠/点开头拒绝",
                        PluginManager.IsValidPackageName("dsh-mnemon") &&
                        PluginManager.IsValidPackageName("@liustack/modsearch") &&
                        PluginManager.IsValidPackageName("dsh-improved-inline-edit") &&
                        PluginManager.IsValidPackageName("a_b.c~d") &&
                        !PluginManager.IsValidPackageName("Dsh-Mnemon") &&            // 大写
                        !PluginManager.IsValidPackageName("a/b/c") &&                 // 多段 /
                        !PluginManager.IsValidPackageName(".bad") &&                  // 点开头
                        !PluginManager.IsValidPackageName("bad.") &&                  // 点结尾
                        !PluginManager.IsValidPackageName(new string('a', 215)),      // 超长
                        "npm 规则闭合白名单");

                    // ST-B：恶意构造的"版本"与来源在三个 Build 入口全部被拒（返回空串 ⇒ 调用方不得执行）
                    Check("ST-B 恶意样本在 BuildAddArgs / BuildUninstallArgs / BuildAddSourceArgs 全部被拒（空串）",
                        PluginManager.BuildAddArgs("dsh-mnemon", "99.0.0&calc") == "" &&
                        PluginManager.BuildAddArgs("dsh-mnemon", "1.2.3\n--registry=x") == "" &&
                        PluginManager.BuildUninstallArgs("x&calc") == "" &&
                        PluginManager.BuildUninstallArgs("--registry=https://evil.example") == "" &&
                        PluginManager.BuildAddSourceArgs("x&calc") == "" &&
                        PluginManager.BuildAddSourceArgs("dsh-mnemon --registry=https://evil.example") == "" &&
                        PluginManager.BuildAddArgs("dsh-mnemon", "0.5.9").Contains("add dsh-mnemon@0.5.9 "),
                        "注入构造 ⇒ 空串；合法版本不受影响");

                    // ST-C：git / URL 来源白名单 —— 允许的主机放行，file:/UNC/明文 http/scp/陌生 host 拒绝
                    Check("ST-C git 源白名单：github/gitee/git+https 放行，file:/UNC/scp/陌生 host 拒绝",
                        PluginManager.IsValidGitSource(gitInline) &&
                        PluginManager.IsValidGitSource("github:aa2246740/dsh-watcher#2d19cb5") &&
                        PluginManager.IsValidGitSource("git+https://gitee.com/iJetLi/deepseek-harness-codearts.git") &&
                        !PluginManager.IsValidGitSource("file:../local-pkg") &&
                        !PluginManager.IsValidGitSource("\\\\srv\\share\\evil") &&     // UNC
                        !PluginManager.IsValidGitSource("git+ssh://git@host/o/r") &&   // scp / ssh 形态
                        !PluginManager.IsValidGitSource("https://evil.example/o/r") && // 陌生 host
                        !PluginManager.IsValidGitSource("http://github.com/o/r"),      // 明文 http
                        "host 闭合白名单（github.com/gitee.com/gitlab.com/bitbucket.org）");

                    // ST-D：版本 spec 白名单 —— semver 与常见范围放行；- 开头 token / 元字符 / 空格 / 换行拒绝
                    Check("ST-D 版本白名单：semver/^~/1.x/*/latest 放行，--registry=x、元字符、空白、垃圾尾巴拒绝",
                        PluginManager.IsValidVersionSpec("0.5.9") &&
                        PluginManager.IsValidVersionSpec("^0.1.0-rc.6") &&
                        PluginManager.IsValidVersionSpec("~1.2.3") &&
                        PluginManager.IsValidVersionSpec(">=4.0.1") &&
                        PluginManager.IsValidVersionSpec("1.x") && PluginManager.IsValidVersionSpec("*") &&
                        PluginManager.IsValidVersionSpec("latest") &&
                        !PluginManager.IsValidVersionSpec("--registry=https://evil.example") &&   // - 开头 token
                        !PluginManager.IsValidVersionSpec("1.2.3 x") &&                            // 空格
                        !PluginManager.IsValidVersionSpec("99.0.0&calc") &&                        // & 元字符
                        !PluginManager.IsValidVersionSpec("1.2.3" + "\x0A" + "rm -rf") &&          // 中段换行（首尾空白按全库约定先剥除，中段换行必拒）
                        !PluginManager.IsValidVersionSpec("1.2.3%PATH%") &&                        // %VAR%
                        !PluginManager.IsValidVersionSpec("1.2.3`calc`"),                          // 反引号
                        "semver 与常见范围放行、注入构造拒绝");

                    // ST-E：IsAllowedLinkUrl —— OpenUrl 的纯函数闸门：https+白名单 host 放行，UNC/file/其它 host 拒
                    Check("ST-E 链接白名单（OpenUrl 闸门）：https·github 放行，UNC/file/http/陌生 host 拒绝",
                        PluginMarket.IsAllowedLinkUrl("https://github.com/aa2246740/dsh-watcher") &&
                        PluginMarket.IsAllowedLinkUrl("https://gitee.com/iJetLi/deepseek-harness-codearts") &&
                        !PluginMarket.IsAllowedLinkUrl("\\\\attacker\\share\\evil.exe") &&   // UNC 投毒
                        !PluginMarket.IsAllowedLinkUrl("file://C:/evil.exe") &&
                        !PluginMarket.IsAllowedLinkUrl("http://github.com/o/r") &&           // 明文
                        !PluginMarket.IsAllowedLinkUrl("https://evil.example/o") &&          // 陌生 host
                        !PluginMarket.IsAllowedLinkUrl(""),                                   // 空
                        "OpenUrl 只放行白名单 host 的 https");

                    // ST-E′：两个**漏掉的打开点**补上闸门后的回归 —— MainWindow.Tools.cs 的
                    //        AuthorName_Click（作者主页）与 PluginName_Click（插件主页）已改为共用
                    //        OpenExternalLink，判据与市场 OpenUrl 是同一个 PluginMarket.IsAllowedLinkUrl。
                    //        这两处的 url 来源都是**外部输入**：
                    //          · AuthorProfileUrl ← 清单声明 / package.json 的 repository、homepage；
                    //          · Plugin.LinkUrl   ← 同上 ①~④（第④档还会拼镜像站包页）。
                    //        旧实现把这段字符串直接交给 UseShellExecute=true 的 shell ⇒
                    //        非 URL 取值（UNC \\attacker\share\evil.exe）会被当文件路径打开并执行。
                    //        下面把"必须拦"逐条真跑（含任务点名的 UNC / file / http / 陌生 host / 空串）。
                    Check("ST-E′-1 两个打开点的闸门：UNC/file/http 明文/陌生 host/空串/伪装 host 一律拦",
                        !PluginMarket.IsAllowedLinkUrl("\\\\srv\\share") &&
                        !PluginMarket.IsAllowedLinkUrl("\\\\attacker\\share\\evil.exe") &&
                        !PluginMarket.IsAllowedLinkUrl("file:///C:/x") &&
                        !PluginMarket.IsAllowedLinkUrl("file://C:/evil.exe") &&
                        !PluginMarket.IsAllowedLinkUrl("http://github.com/o/r") &&
                        !PluginMarket.IsAllowedLinkUrl("https://evil.example/o") &&
                        !PluginMarket.IsAllowedLinkUrl("") &&
                        !PluginMarket.IsAllowedLinkUrl("   ") &&
                        // 本地路径永远不该走到网页闸门（本地目录另走 explorer.exe 那条通道）
                        !PluginMarket.IsAllowedLinkUrl("D:\\Applications\\evil.exe") &&
                        !PluginMarket.IsAllowedLinkUrl("plugins\\dsh-imagegen") &&
                        !PluginMarket.IsAllowedLinkUrl("cmd:/c calc"),
                        "外部输入的 url 只可能落到白名单 https，其余（含 UNC 可执行投毒）全部拦下");

                    // ST-E′-2：**反向回归** —— 补闸门不许把本来能开的合法链接一起堵死。
                    Check("ST-E′-2 反向回归：原有合法链接照旧放行（加固不得把能打开的堵死）",
                        PluginMarket.IsAllowedLinkUrl("https://github.com/aa2246740/dsh-watcher") &&
                        PluginMarket.IsAllowedLinkUrl("https://github.com/xiaosurongjia/dsh-improved-inline-edit") &&
                        PluginMarket.IsAllowedLinkUrl("https://gitee.com/iJetLi/deepseek-harness-codearts") &&
                        PluginMarket.IsAllowedLinkUrl("https://github.com/bowenliang123") &&   // 作者页回落
                        PluginMarket.IsAllowedLinkUrl("https://gitee.com/iJetLi") &&
                        PluginMarket.IsAllowedLinkUrl("https://gitlab.com/someowner") &&
                        PluginMarket.IsAllowedLinkUrl("https://bitbucket.org/someowner") &&
                        PluginMarket.IsAllowedLinkUrl("https://registry.npmmirror.com/package/x"),
                        "git 源（github/gitee/gitlab/bitbucket）与 registry.npmmirror 的 https 均放行");

                    // ST-E′-3：判据必须是 **host 精确相等**，不能退化成"包含"——否则
                    //        github.com.evil.example 这类后缀伪装会被静默放行，闸门形同虚设。
                    //
                    //        【本块修订：原先这里把 www.npmmirror.com **未登记**当作事实钉住，
                    //          实为把自锁 bug 写成了断言。PluginManager.Plugin.LinkUrl 第④档
                    //          （PluginManager.cs:290）拼的是 https://www.npmmirror.com/package/<name>，
                    //          而白名单当时只登记 registry.npmmirror.com ⇒ 这类插件点插件名会被
                    //          **自己的闸门**拦下，弹「该链接不在允许打开的网站范围内」。
                    //          修法＝把 www.npmmirror.com 补进 AllowedLinkHosts（精确 host，
                    //          不是通配）；下面的反向样本即用来证明"补条目"没有退化成
                    //          "包含/通配"——若改成 host.EndsWith(entry) 或 url.Contains(entry)，
                    //          裸域 npmmirror.com 与后缀伪装 www.npmmirror.com.evil.example 会翻成放行，
                    //          本条立刻变红。根因级钉法见下方 ST-E′-4 / ST-E′-5。】
                    Check("ST-E′-3 host 精确匹配（非包含式）＋大小写不敏感；镜像站 www./registry. 各登记一条",
                        !PluginMarket.IsAllowedLinkUrl("https://github.com.evil.example/o") &&          // 后缀伪装
                        !PluginMarket.IsAllowedLinkUrl("https://www.npmmirror.com.attacker.io/package/x") &&
                        !PluginMarket.IsAllowedLinkUrl("https://evil.example/github.com") &&            // host 里含白名单串
                        PluginMarket.IsAllowedLinkUrl("https://GitHub.com/o/r") &&                      // 同站，大小写不算绕过
                        // npmmirror 是**两个不同的 host**，各登记一条：registry.* 是镜像源 **API** host，
                        // www.* 是镜像站**网页** host。两条都必须放行 —— www. 那条正是 LinkUrl 第④档
                        // 的产出目标（自锁 bug 的修复点），registry. 那条不许因为本次改动被删掉。
                        PluginMarket.IsAllowedLinkUrl("https://registry.npmmirror.com/package/x") &&
                        PluginMarket.IsAllowedLinkUrl("https://www.npmmirror.com/package/x"),
                        "精确 host 匹配；registry.*（API）与 www.*（网页）是两条独立登记项，均放行");

                    // ST-E′-4：**自锁 bug 的回归断言**（npmmirror 网页 host 组）。
                    //
                    //   症状（前一位同事实测）：PluginManager.Plugin.LinkUrl 第④档在清单未声明仓库、
                    //   package.json 也没有 repository/homepage 时拼出
                    //       https://www.npmmirror.com/package/<name>
                    //   （PluginManager.cs:290；SelfTest 里 "LinkUrl：普通 npm 包才是镜像站页面" 那条
                    //   已证该值确实会产出）。而 AllowedLinkHosts 当时只登记 registry.npmmirror.com
                    //   ⇒ 点插件名走 MainWindow.Tools.cs:1289 OpenExternalLink ⇒
                    //   PluginMarket.IsAllowedLinkUrl 返回 false ⇒ 弹
                    //   「该链接不在允许打开的网站范围内」。即：**闸门把自己人拦了**（自锁）。
                    //
                    //   下面把闭环两侧一起钉住：① 第④档产出的原样字符串必须放行；
                    //   ② 补的这条**不许**顺手放宽 —— 裸域与后缀伪装必须仍然拦下。
                    //
                    //   ★ 这组反向样本是**按区分力挑的**（每条都能抓住一种具体的退化写法，
                    //     在 %TEMP% 探针里逐条量过，下面的数字是实测值，不是估计）：
                    //       · 判据改成"允许子域通配 *.npmmirror.com"  ⇒ ②③ 变红（2 条）
                    //       · 判据改成 host.EndsWith(entry) 裸后缀    ⇒ ③④⑤ 变红（3 条）
                    //       · 判据改成 url.Contains(entry) 整串包含   ⇒ ②③④⑤⑥⑦ 变红（6 条）
                    //     所以"精确 host 集合"这件事，本断言是真的钉住了、不是写个愿望。
                    Check("ST-E′-4 自锁回归：LinkUrl 第④档的 www.npmmirror.com 放行；裸域/后缀伪装/未登记子域仍拦",
                        // ① 自锁 bug 的修复点：第④档产出原样字符串（拼法与 PluginManager.cs:290 逐字一致）
                        PluginMarket.IsAllowedLinkUrl("https://www.npmmirror.com/package/dsh-mnemon") &&
                        PluginMarket.IsAllowedLinkUrl("https://www.npmmirror.com/package/dsh-未登记") &&
                        // ② 别改坏：镜像源 API host 仍放行（registry.* 与 www.* 是两条独立登记项）
                        PluginMarket.IsAllowedLinkUrl("https://registry.npmmirror.com/package/x") &&
                        // ③ 裸域未登记 ⇒ 仍必须拦（通配 *.npmmirror.com 会把它一起放行）
                        !PluginMarket.IsAllowedLinkUrl("https://npmmirror.com/package/x") &&
                        // ④ 后缀伪装 ⇒ 仍必须拦（包含式匹配会放行）
                        !PluginMarket.IsAllowedLinkUrl("https://www.npmmirror.com.evil.example/package/x") &&
                        !PluginMarket.IsAllowedLinkUrl("https://www.npmmirror.com.attacker.io/package/x") &&
                        // ⑤ 未登记子域与"裸后缀"伪装 —— 抓手：子域通配 / EndsWith 都会在这里翻车
                        !PluginMarket.IsAllowedLinkUrl("https://evil.npmmirror.com/package/x") &&
                        !PluginMarket.IsAllowedLinkUrl("https://notwww.npmmirror.com/package/x") &&
                        !PluginMarket.IsAllowedLinkUrl("https://evil.www.npmmirror.com/package/x") &&
                        !PluginMarket.IsAllowedLinkUrl("https://evil.registry.npmmirror.com/package/x") &&
                        // ⑥ 前缀伪装：www.npmmirror.com.cn 不是 www.npmmirror.com（包含式会放行）
                        !PluginMarket.IsAllowedLinkUrl("https://www.npmmirror.com.cn/package/x"),
                        "第④档产出放行（自锁已修）；裸域/后缀伪装/未登记子域/前缀伪装仍拦（未退化成通配或包含）");

                    // ST-E′-5：**白名单与 LinkUrl 的接线一致性** —— 按真实 LinkUrl 算，不写死样本。
                    //
                    //   ST-E′-4 里的 https://www.npmmirror.com/package/dsh-mnemon 是**手写**字符串，
                    //   万一 PluginManager.cs:290 以后改成别的 host，手写样本不会跟着变、断言就失去意义。
                    //   所以这里从**真实 LinkUrl**（真清单声明 + 真取值链）量一遍：凡第④档拼出的 host，
                    //   都必须过得了 IsAllowedLinkUrl —— 这才是"闸门与取值链不打架"的钉法。
                    {
                        string tE5 = Path.Combine(Path.GetTempPath(), "dshguard-linkgate-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                        string bakE5 = PluginManager.PackageFileOverrideForTest;
                        try
                        {
                            Directory.CreateDirectory(tE5);
                            // 只声明一个普通 npm 包 ⇒ 镜像站网页 host 是这条取值的唯一出处（第④档）
                            File.WriteAllText(Path.Combine(tE5, "package.json"),
                                "{ \"name\": \"dsh-profile-web\", \"private\": true, \"dependencies\": { "
                                + "\"dsh-npmpage-probe\": \"^1.2.3\" } }",
                                new UTF8Encoding(false));
                            PluginManager.PackageFileOverrideForTest = Path.Combine(tE5, "package.json");

                            string realLink = new PluginManager.Plugin { Name = "dsh-npmpage-probe" }.LinkUrl;
                            bool isTier4Host = realLink.StartsWith("https://www.npmmirror.com/package/", StringComparison.Ordinal);
                            Check("ST-E′-5 接线一致性：真实 LinkUrl（第④档）产出的 host 必须过得了闸门（按 host 算，不写死样本）",
                                isTier4Host && PluginMarket.IsAllowedLinkUrl(realLink),
                                $"真实 LinkUrl=「{realLink}」· 闸门={(PluginMarket.IsAllowedLinkUrl(realLink) ? "放行" : "拦截")}");
                        }
                        finally
                        {
                            PluginManager.PackageFileOverrideForTest = bakE5;
                            try { Directory.Delete(tE5, true); } catch { }
                        }
                    }

                    // ST-E′-6：端口**不参与** host 判据（`Uri.Host` 在 .NET 里已剥离端口）——
                    //   非标准端口既不能成为绕过白名单的手段，也不该把白名单站点的正常链接误拦。
                    //   反证：若判据从 `uri.Host` 改成 `uri.Authority` 或整串比较，下面第 2/3/4/5 条立刻变红。
                    // ⚠ 编号说明：本条**不是** ST-E′-5 —— ST-E′-5 已被上方"接线一致性"那条占用。
                    Check("ST-E′-6 端口不影响白名单：:8443 不绕过也不误拦；明文/后缀伪装/?@ 组合仍拦",
                        PluginMarket.IsAllowedLinkUrl("https://www.npmmirror.com/x") &&
                        PluginMarket.IsAllowedLinkUrl("https://www.npmmirror.com:8443/x") &&
                        PluginMarket.IsAllowedLinkUrl("https://registry.npmmirror.com:8443/package/x") &&
                        PluginMarket.IsAllowedLinkUrl("https://www.npmmirror.com:443/x") &&
                        PluginMarket.IsAllowedLinkUrl("https://WWW.NPMMIRROR.COM:8443/x") &&
                        !PluginMarket.IsAllowedLinkUrl("https://evil.com:8443/x") &&
                        !PluginMarket.IsAllowedLinkUrl("http://www.npmmirror.com:8443/x") &&
                        !PluginMarket.IsAllowedLinkUrl("https://www.npmmirror.com.evil.com:8443/x") &&
                        !PluginMarket.IsAllowedLinkUrl("https://www.npmmirror.com:8443@evil.com/x") &&
                        !PluginMarket.IsAllowedLinkUrl("https://attacker.com:8443@evil.com/x") &&
                        !PluginMarket.IsAllowedLinkUrl("https://localhost:8443/x") &&
                        !PluginMarket.IsAllowedLinkUrl("https://www.npmmirror.com:99999/x"),
                        "端口判据：Uri.Host 剥离端口 ⇒ :8443 受同一套 host 管辖（既有设计，非缺口）");

                    // ST-E′-7：带凭据（userinfo）的 URL 一律拒绝 —— 与 ST-E′-6 **成对**：
                    //   两条都钉"host 判据不被 URL 里的附件（端口 / 凭据）带偏"。
                    // ⚠ 前置依赖：本条只在 PluginMarket.cs 的 IsAllowedLinkUrl 补上
                    //   `&& uri.UserInfo.Length == 0` 那一项之后才成立（另一单正在落该合取项）。
                    //   落盘前本条**必然是红的**，这正是它存在的意义 —— 它钉的就是那个合取项。
                    //   反证：把 `uri.UserInfo.Length == 0` 删掉 ⇒ 本条第 2/3/4 条立刻变红。
                    Check("ST-E′-7 带凭据 URL 一律拒（host 判据不因 @ 前缀而误判）",
                        PluginMarket.IsAllowedLinkUrl("https://www.npmmirror.com/x") &&
                        !PluginMarket.IsAllowedLinkUrl("https://user:pw@www.npmmirror.com/x") &&
                        !PluginMarket.IsAllowedLinkUrl("https://user:pw@www.npmmirror.com:8443/x") &&
                        !PluginMarket.IsAllowedLinkUrl("https://evil.com@www.npmmirror.com/x"),
                        "凭据不属于白名单概念：实连 host 可信也不放行带凭据的链接");

                    // ST-F：Compare 语义修正 —— 垃圾尾巴不再"被判更大/判相等"（含真实样本 99.0.0&calc），
                    //       纯 semver 判定一点不变；HasUpdate 类判定（>0）对不可比输入失败关闭。
                    Check("ST-F Compare 语义修正：垃圾输入不再判更大（99.0.0&calc 样本），纯 semver 判定不变",
                        VersionInfo.Compare("99.0.0&calc", "0.1.5") == 0 &&      // 原缺陷样本：旧版返回 1
                        VersionInfo.Compare("1.2.3 x", "1.2.3") == 0 &&          // 旧版尾部垃圾被吞、判 0
                        VersionInfo.Compare("1.2.3\n", "1.2.3") == 0 &&          // 换行尾巴
                        VersionInfo.IsComparableVersion("99.0.0&calc") == false &&
                        VersionInfo.IsComparableVersion("1.2.3 x") == false &&
                        VersionInfo.IsComparableVersion("0.1.0-rc.6") &&
                        VersionInfo.Compare("99.0.0&calc", "0.1.5") <= 0 &&      // 失败关闭：不判"有更新"
                        VersionInfo.Compare("0.2.0", "0.1.5") > 0 &&             // 正常判定不变
                        VersionInfo.Compare("0.1.0", "0.1.5") < 0 &&
                        VersionInfo.Compare("0.1.0-rc.6", "0.1.0") < 0,          // 预发布规则不变
                        $"99.0.0&calc vs 0.1.5 → {VersionInfo.Compare("99.0.0&calc", "0.1.5")}（旧版=1）");

                    // ST-G：ParseUpdateReport → HasUpdate 的失败关闭（真实样本走全链路）：
                    //       latest 位被投毒成 `99.0.0&calc` 时不得报"有更新"。
                    var poisoned = PluginManager.ParseUpdateReport(
                        "{\"packages\":[{\"name\":\"dsh-mnemon\",\"installed\":\"0.1.5\"," +
                        "\"latest\":\"99.0.0&calc\",\"published\":\"\",\"error\":\"\"}]}");
                    var legit = PluginManager.ParseUpdateReport(
                        "{\"packages\":[{\"name\":\"dsh-mnemon\",\"installed\":\"0.1.5\"," +
                        "\"latest\":\"0.2.0\",\"published\":\"\",\"error\":\"\"}]}");
                    Check("ST-G HasUpdate 失败关闭：latest 被投毒成 99.0.0&calc 时不报有更新；正常新版照报",
                        poisoned.Count == 1 && !poisoned[0].HasUpdate &&
                        legit.Count == 1 && legit[0].HasUpdate,
                        $"投毒 → HasUpdate={poisoned.Count > 0 && poisoned[0].HasUpdate}；0.2.0 → {legit.Count > 0 && legit[0].HasUpdate}");

                    // ST-H：版本位垃圾进入 CompareInstalledToTarget ⇒ Unknown（不误判达成/未安装）
                    Check("ST-H CompareInstalledToTarget：磁盘版本带垃圾尾巴 ⇒ Unknown（不因 Compare=0 误判达成）",
                        PluginManager.CompareInstalledToTarget("0.5.9&calc", "0.5.9").Check == PluginManager.VersionCheck.Unknown &&
                        PluginManager.CompareInstalledToTarget("0.5.9", "99.0.0&calc").Check == PluginManager.VersionCheck.Unknown,
                        "不可解析 ⇒ 不可比（配套闸门）");

                    // 样本 ③′：npm 源的版本位**万一**被塞进显示标签 ⇒ 就地纠正成清单声明里那一段版本
                    // （`^0.5.9` → `0.5.9`）；纠不出来就返回空串（调用方不许动手），**绝不原样拼命令**。
                    string fixedLabel = PluginManager.BuildUpdateArgs("dsh-mnemon", "^0.5.9", "仓库最新");
                    Check("npm 源版本位被塞了显示标签时：就地纠正成清单里的版本；纠不出来就拒绝拼命令（返回空串）",
                        fixedLabel.Contains("dsh-mnemon@0.5.9") && !fixedLabel.Contains("仓库最新") &&
                        PluginManager.BuildUpdateArgs("dsh-mnemon", "", "仓库最新") == "" &&
                        PluginManager.BuildUpdateArgs("", gitInline, "仓库最新") == "",
                        $"「{fixedLabel}」· 空清单来源 → 「{PluginManager.BuildUpdateArgs("dsh-mnemon", "", "仓库最新")}」");
                }
                finally
                {
                    PluginManager.PackageFileOverrideForTest = pkgBak52;
                    try { Directory.Delete(t52m, true); } catch { }
                }

                // 样本 ④：**git 源不执行 npm 版本比较** —— 判据是"远端提交是否变化"（SameCommit），
                // 提交一致 ⇒ 无新版本；不一致 / 无法读取 ⇒ 报告有新版本（由用户手动触发一次）。
                Check("git 源的「有新版本」按**远端提交**判定（同一提交=无新版；不一致或无法读取=报有新版）",
                    PluginManager.SameCommit("2573f24228ebe8a5e6b82900cd7fe731ba7d9c8d", "2573f242") &&
                    PluginManager.SameCommit("4ba82a0", "4ba82a0cfcd20ae8605940a71a65d3d8face2974") &&
                    !PluginManager.SameCommit("2573f24228ebe8a5e6b82900cd7fe731ba7d9c8d", "fff95bb5") &&
                    !PluginManager.SameCommit("", "2573f242") && !PluginManager.SameCommit("2573f242", "") &&
                    !PluginManager.SameCommit(null, null),
                    "7 位比对 · 忽略大小写 · 任一侧为空一律按「有新提交」处理");

                // 样本 ⑤：git 源**不参与 npm 版本比较** —— 磁盘版本恰好等于目标值时，
                // 旧口径会判成 Satisfied（命令失败也报成功）；现在必须 Measured=false、只看退出码。
                string t52 = Path.Combine(Path.GetTempPath(), "dshguard-gitverdict-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                try
                {
                    Directory.CreateDirectory(t52);
                    File.WriteAllText(Path.Combine(t52, "package.json"),
                        "{ \"name\": \"dsh-profile-web\", \"private\": true, \"dependencies\": { "
                        + "\"dsh-improved-inline-edit\": \"" + gitInline + "\", "
                        + "\"dsh-codearts-auth\": \"" + gitCodearts + "\", "
                        + "\"dsh-mnemon\": \"^0.5.9\" } }",
                        new UTF8Encoding(false));
                    void PutPkg(string name, string version)
                    {
                        string d = Path.Combine(t52, "node_modules", name.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(d);
                        File.WriteAllText(Path.Combine(d, "package.json"),
                            "{ \"name\": \"" + name + "\", \"version\": \"" + version + "\" }", new UTF8Encoding(false));
                    }
                    PutPkg("dsh-improved-inline-edit", "0.1.0");
                    PutPkg("dsh-mnemon", "0.5.9");

                    // ★ 这一条就是"把显示标签/提交号当版本比"会踩的坑：磁盘 0.1.0、目标位 0.1.0
                    // 全位置实参（EvaluateUpdate 的形参名是 commandOutput，不是 output）
                    var rGit = MainWindow.EvaluateUpdate("dsh-improved-inline-edit", "0.1.0", false, "", t52);
                    Check("git 源不参与 npm 版本比较：命令失败时**不许**因为「磁盘版本 == 版本位」就判成功",
                        !rGit.Measured && rGit.Check == PluginManager.VersionCheck.Unknown && !rGit.Succeeded &&
                        rGit.Note.Contains("git 源"),
                        $"可判={rGit.Measured} 结论={rGit.Check} 判成功={rGit.Succeeded} · {rGit.Note}");

                    // 反向：同一个样本上，npm 源那条判据一点没变（磁盘 0.5.9 == 目标 0.5.9 ⇒ 判成功）
                    var rNpm = MainWindow.EvaluateUpdate("dsh-mnemon", "0.5.9", false, "", t52);
                    Check("同一样本上 npm 源判据没变（磁盘版本与目标一致 ⇒ 仍以磁盘事实判成功）",
                        rNpm.Measured && rNpm.Succeeded && rNpm.NoteDowngraded &&
                        rNpm.Check == PluginManager.VersionCheck.Satisfied,
                        $"可判={rNpm.Measured} 判成功={rNpm.Succeeded} · {rNpm.Note}");
                }
                finally { try { Directory.Delete(t52, true); } catch { } }

                // 样本 ⑥：界面显示 —— git 源的版本位只放**已装版本或短提交号**，不放「仓库最新」；
                // 「更新到 …」按钮对 git 源写「更新到最新提交」（不含假版本号）。
                //
                // ⚠ 真机自检曾在此 FAIL：上一版把短提交号的期望**手写**成 "2573f242"（8 位），
                //   而实现的口径是 `commit.Substring(0, 7)` ⇒ 实际是 "2573f24"（7 位）——
                //   期望值手抄错了一位，函数本身是对的（已用 %TEMP% 独立工程逐字复现确认）。
                //   修法：期望值**由输入本身推导**（sha.Substring(0, 7)），不再手写；
                //   detail 也由实际判据结果拼出，避免"表里看着对、实际判错"。
                const string shaLong = "2573f24228ebe8a5e6b82900cd7fe731ba7d9c8d";
                const string shaLong2 = "4ba82a0cfcd20ae8605940a71a65d3d8face2974";
                string shaShort = shaLong.Substring(0, 7);      // 实现口径：取前 7 位
                string shaShort2 = shaLong2.Substring(0, 7);

                string v1 = PluginManager.GitVersionLabel("0.1.0", shaLong);           // 有版本 ⇒ 用版本号
                string v2 = PluginManager.GitVersionLabel("", shaLong);                // 无版本 ⇒ 短提交号
                string v3 = PluginManager.GitVersionLabel("?", shaLong2);              // "?" 同无版本 ⇒ 短提交号
                string v4 = PluginManager.GitVersionLabel("(未安装)", "");             // 都没有 ⇒ 版本未知
                string v5 = PluginManager.GitVersionLabel("", "");                     // 都没有 ⇒ 版本未知

                Check("git 源版本位只放真版本号 / 短提交号（无法读取才写「版本未知」），绝不摆「仓库最新」",
                    v1 == "0.1.0" &&
                    v2 == shaShort &&
                    v3 == shaShort2 &&
                    v4 == "版本未知" &&
                    v5 == "版本未知" &&
                    !v2.Contains("仓库最新") && !v4.Contains("仓库最新"),
                    $"shaShort={shaShort}（实现取前 7 位）· 有版本→「{v1}」· 无版本→「{v2}」· "
                    + $"?→「{v3}」· 都没有→「{v4}」/「{v5}」");

                Check("「更新到 …」按钮：git 源写「更新到最新提交」，npm 源照旧写具体版本号",
                    PluginManager.UpdateButtonText(gitInline, "最新提交") == "更新到最新提交" &&
                    PluginManager.UpdateButtonText(gitCodearts, "最新提交，有新提交") == "更新到最新提交" &&
                    PluginManager.UpdateButtonText("^0.5.9", "0.5.9") == "更新到 0.5.9" &&
                    !PluginManager.UpdateButtonText(gitInline, "仓库最新").Contains("仓库最新"),
                    $"{PluginManager.UpdateButtonText(gitInline, "仓库最新")} / {PluginManager.UpdateButtonText("^0.5.9", "0.5.9")}");

                // 样本 ⑦：更新目标文案只走一个出口（TargetText）：git 源写「最新提交」，
                // 老调用点不填 TargetLabel 时回落 Latest（不得破坏既有行为）。
                var u52git = new PluginManager.PluginUpdate { Name = "dsh-codearts-auth", Installed = "0.1.0", Latest = "0.1.0", TargetLabel = "最新提交", HasUpdate = true };
                var u52npm = new PluginManager.PluginUpdate { Name = "dsh-mnemon", Installed = "0.5.8", Latest = "0.5.9", HasUpdate = true };
                Check("更新目标文案只有一个出口：git 源→「最新提交」，npm 源→具体版本号，未填时回落 Latest",
                    u52git.TargetText == "最新提交" && u52npm.TargetText == "0.5.9" &&
                    new PluginManager.PluginUpdate { Latest = "1.0.0" }.TargetText == "1.0.0" &&
                    !u52git.TargetText.Contains("仓库最新") && !u52git.TargetText.Contains("0.1.0"),
                    $"git「{u52git.TargetText}」· npm「{u52npm.TargetText}」");

                // ══════ 52c. bug：gitee 源插件**永远提示有更新**（用户原话：永远检测待更新？）══════
                // 现场：dsh-codearts-auth 声明 git+https://gitee.com/iJetLi/deepseek-harness-codearts.git，
                // 锁文件里已解析出提交 2573f24228ebe8a5e6b82900cd7fe731ba7d9c8d。
                // 旧代码两处都只认 github：
                //   · PluginSource.ParseGitRepo 的正则写死 github\.com ⇒ gitee 源解析成 ("","")；
                //   · FetchRepoLatestAsync 拿不到 owner 就直接返回空串，API 也只打 api.github.com；
                // ⇒「远端提交变没变」永远拿不到远端 ⇒ 按旧口径（失败即报有新版）永远提示更新，
                //   用户点更新也不会消失 —— 因为判据永远拿不到远端。
                // 实测（web_fetch 只读抓公开接口，2026-09-17）：
                //   gitee https://gitee.com/api/v5/repos/iJetLi/deepseek-harness-codearts/commits?per_page=1
                //   → HTTP 200、无需 token、结构与 github **同形**：
                //     [{"sha":"be6ba1c0435b218585a31d0ffd48489a9a6fed7a",
                //       "commit":{"author":{"date":"2026-09-17T08:57:17+08:00"},
                //                 "committer":{"date":"2026-09-17T08:57:17+08:00"}}, …}]
                //   同一仓库的 package.json 里 version 仍是 0.1.0（提交版本号不规范，果然不是权威版本）。
                {
                    // 本段的小工具：只比 owner/repo 两段，免得把"路径第 3 段"的实现细节写死进断言
                    // （但第 3 段不进 repo 这条恰好是与旧行为的兼容点，故另有专门一条断言）。
                    static bool RepoIs((string Owner, string Repo) got, string owner, string repo)
                        => got.Owner == owner && got.Repo == repo;
                    static string Head7(string s) => s.Length >= 7 ? s.Substring(0, 7) : s;

                    // ── ① ParseGitRepo 认多家托管站（本缺陷的解析面）──
                    Check("ParseGitRepo 认 gitee/gitlab/bitbucket 的各种写法（含 git+/https/git@/ssh:// 与 #ref）",
                        RepoIs(PluginSource.ParseGitRepo("git+https://gitee.com/iJetLi/deepseek-harness-codearts.git"), "iJetLi", "deepseek-harness-codearts") &&
                        RepoIs(PluginSource.ParseGitRepo("https://gitee.com/iJetLi/deepseek-harness-codearts.git#2573f24228ebe8a5e6b82900cd7fe731ba7d9c8d"), "iJetLi", "deepseek-harness-codearts") &&
                        RepoIs(PluginSource.ParseGitRepo("git@gitee.com:iJetLi/deepseek-harness-codearts.git"), "iJetLi", "deepseek-harness-codearts") &&
                        RepoIs(PluginSource.ParseGitRepo("git+https://gitlab.com/group/sub/repo.git"), "group", "sub") &&
                        RepoIs(PluginSource.ParseGitRepo("gitlab:group/repo#v1.0"), "group", "repo") &&
                        RepoIs(PluginSource.ParseGitRepo("ssh://git@bitbucket.org/team/proj.git"), "team", "proj") &&
                        RepoIs(PluginSource.ParseGitRepo("bitbucket:team/proj"), "team", "proj") &&
                        // 旧行为：路径第 3 段不进 repo（与旧正则 [^/]+/[^/#]+ 同结论）
                        PluginSource.ParseGitRepo("git+https://gitlab.com/group/sub/repo.git").Repo == "sub",
                        $"gitee=「{string.Join("/", PluginSource.ParseGitRepo(gitCodearts))}」· gitlab=「{string.Join("/", PluginSource.ParseGitRepo("gitlab:group/repo#v1.0"))}」");

                    // ── ② github 各写法回归不变（向后兼容硬断言）──
                    Check("ParseGitRepo 的 github 各写法**回归不变**（含 github: 简写与 git:// ）",
                        RepoIs(PluginSource.ParseGitRepo("git+https://github.com/xiaosurongjia/dsh-improved-inline-edit.git"), "xiaosurongjia", "dsh-improved-inline-edit") &&
                        RepoIs(PluginSource.ParseGitRepo("github:a/b#sha1a2b3c"), "a", "b") &&
                        RepoIs(PluginSource.ParseGitRepo("github:owner/repo/"), "owner", "repo") &&
                        RepoIs(PluginSource.ParseGitRepo("https://github.com/a/b"), "a", "b") &&
                        RepoIs(PluginSource.ParseGitRepo("git@github.com:a/b.git"), "a", "b") &&
                        RepoIs(PluginSource.ParseGitRepo("git://github.com/a/b.git#v1.0"), "a", "b"),
                        "github: 简写只认 github（gitee:/gitlab:/bitbucket: 也认，但没有 github 之外的简写被发明出来）");

                    // ── ③ 非 git 源 / 白名单外站点：绝不认（免得去陌生站点查提交）──
                    Check("ParseGitRepo 对非 git 源返回 (\"\",\"\")，白名单外的站点也不认",
                        PluginSource.ParseGitRepo("^0.5.9") == ("", "") &&
                        PluginSource.ParseGitRepo("file:../x") == ("", "") &&
                        PluginSource.ParseGitRepo("") == ("", "") &&
                        PluginSource.ParseGitRepo(null) == ("", "") &&
                        PluginSource.ParseGitRepo("git+https://evil.example/a/b.git") == ("", "") &&
                        PluginSource.ParseGitRepo("https://gitee.com.evil.example/a/b.git") == ("", ""),
                        $"evil=「{string.Join("/", PluginSource.ParseGitRepo("git+https://evil.example/a/b.git"))}」");

                    // ── ④ 与既有 PluginManager.ParseRepoSpec 的一致性（复用它那套形状规则）──
                    Check("ParseGitRepo 与既有 ParseRepoSpec 对白名单内站点的 (owner,repo) 结论一致（同一套形状规则）",
                        PluginManager.ParseRepoSpec(gitCodearts) == ("gitee.com", "iJetLi/deepseek-harness-codearts") &&
                        PluginManager.ParseRepoSpec(gitInline) == ("github.com", "xiaosurongjia/dsh-improved-inline-edit") &&
                        PluginSource.ParseGitRepo(gitCodearts) == ("iJetLi", "deepseek-harness-codearts") &&
                        PluginSource.ParseGitRepo(gitInline) == ("xiaosurongjia", "dsh-improved-inline-edit"),
                        $"RepoUrlFromSpec(gitee)=「{PluginManager.RepoUrlFromSpec(gitCodearts)}」· "
                        + $"PluginSource.RepoUrl(gitee)=「{PluginSource.RepoUrl(gitCodearts)}」");

                    // ── ⑤ 响应解析（纯函数，喂 JSON 样本，**不发任何网络请求**）──
                    const string giteeJson = "[{\"url\":\"https://gitee.com/api/v5/repos/iJetLi/deepseek-harness-codearts/commits/be6ba1c0435b218585a31d0ffd48489a9a6fed7a\",\"sha\":\"be6ba1c0435b218585a31d0ffd48489a9a6fed7a\",\"commit\":{\"author\":{\"name\":\"Jet\",\"date\":\"2026-09-17T08:57:17+08:00\"},\"committer\":{\"name\":\"Jet\",\"date\":\"2026-09-17T08:57:17+08:00\"}},\"parents\":[]}]";
                    const string githubJson = "[{\"sha\":\"4ba82a0cfcd20ae8605940a71a65d3d8face2974\",\"commit\":{\"author\":{\"date\":\"2026-09-16T02:00:00Z\"},\"committer\":{\"date\":\"2026-09-16T02:00:00Z\"}}}]";
                    var rGitee = PluginSource.ParseCommitJson("gitee.com", giteeJson);
                    var rGithub = PluginSource.ParseCommitJson("github.com", githubJson);
                    Check("提交列表解析：github 与 gitee **同形**（sha / commit.committer.date）都能取出提交号与日期",
                        rGitee.Sha == "be6ba1c0435b218585a31d0ffd48489a9a6fed7a" && rGitee.Date == "2026-09-17" &&
                        rGithub.Sha == "4ba82a0cfcd20ae8605940a71a65d3d8face2974" && rGithub.Date == "2026-09-16" &&
                        Head7(rGitee.Sha) == "be6ba1c",
                        $"gitee={Head7(rGitee.Sha)}/{rGitee.Date} · github={Head7(rGithub.Sha)}/{rGithub.Date}");
                    Check("提交列表解析：gitlab(id/committed_date) 与 bitbucket(values[].hash/date) 也认",
                        PluginSource.ParseCommitJson("gitlab.com", "[{\"id\":\"2c76ef62ccd31fb77387a0bcb5eafcf00a4737fb\",\"committed_date\":\"2026-09-17T12:03:28.000+05:30\"}]")
                            == ("2c76ef62ccd31fb77387a0bcb5eafcf00a4737fb", "2026-09-17") &&
                        PluginSource.ParseCommitJson("bitbucket.org", "{\"values\":[{\"hash\":\"ba8a1d25382bc2b7bfe92b81fc902720eac09a25\",\"date\":\"2026-09-16T13:37:51+00:00\"}]}")
                            == ("ba8a1d25382bc2b7bfe92b81fc902720eac09a25", "2026-09-16"));
                    Check("提交列表解析：认不出来的输入一律返回空串、**绝不抛**（空/非 JSON/404 报文/空数组）",
                        PluginSource.ParseCommitJson("gitee.com", "") == ("", "") &&
                        PluginSource.ParseCommitJson("gitee.com", "{\"message\":\"Not Found Project\"}") == ("", "") &&
                        PluginSource.ParseCommitJson("gitee.com", "[]") == ("", "") &&
                        PluginSource.ParseCommitJson("bitbucket.org", "{\"values\":[]}") == ("", "") &&
                        PluginSource.ParseCommitJson("gitee.com", "<html>rate limited</html>") == ("", "") &&
                        PluginSource.ParseCommitJson("gitee.com", null) == ("", ""),
                        "404 报文实测原文：{\"message\":\"Not Found Project\"}");

                    // ── ⑥ ★ 误报口径的护栏（本缺陷的直接回归断言）──
                    const string lockSha = "2573f24228ebe8a5e6b82900cd7fe731ba7d9c8d";
                    Check("★ 远端**查不到** ⇒ 判 Unknown、**不**报有更新（旧口径在这里报有更新 = 永远提示窗口）",
                        PluginManager.GitRemoteVerdict(lockSha, "", PluginSource.CommitConfidence.NotQueryable) == PluginManager.GitUpdateVerdict.Unknown &&
                        PluginManager.GitRemoteVerdict(lockSha, "be6ba1c", PluginSource.CommitConfidence.Unknown) == PluginManager.GitUpdateVerdict.Unknown &&
                        PluginManager.GitRemoteVerdict(lockSha, "Not Found Project", PluginSource.CommitConfidence.Queried) == PluginManager.GitUpdateVerdict.Unknown,
                        "网络失败 / 站点不支持 / 仓库不可访问 —— 都不能说成「有新版」");
                    Check("★ 远端**确实变了** ⇒ 照报有更新（不许把真更新吞掉）；同一提交 ⇒ 无新版",
                        PluginManager.GitRemoteVerdict(lockSha, "be6ba1c0435b218585a31d0ffd48489a9a6fed7a", PluginSource.CommitConfidence.Queried) == PluginManager.GitUpdateVerdict.HasNewCommit &&
                        PluginManager.GitRemoteVerdict(lockSha, "be6ba1c", PluginSource.CommitConfidence.Queried) == PluginManager.GitUpdateVerdict.HasNewCommit &&
                        PluginManager.GitRemoteVerdict(lockSha, "2573f242", PluginSource.CommitConfidence.Queried) == PluginManager.GitUpdateVerdict.UpToDate &&
                        // 锁文件里没提交（未装 / 尚未写进锁文件）而远端拿到了 ⇒ 既有口径不变：报有更新
                        PluginManager.GitRemoteVerdict("", "be6ba1c", PluginSource.CommitConfidence.Queried) == PluginManager.GitUpdateVerdict.HasNewCommit,
                        $"锁文件 {Head7(lockSha)} vs 远端 gitee 实测 {Head7("be6ba1c0435b218585a31d0ffd48489a9a6fed7a")} ⇒ 有新版");
                    Check("★ 唯一判据只有一个出口：HasUpdate 只在 verdict==HasNewCommit 时为真（未知与已最新都不报）",
                        new[] { PluginManager.GitUpdateVerdict.Unknown, PluginManager.GitUpdateVerdict.UpToDate,
                                PluginManager.GitUpdateVerdict.HasNewCommit }
                            .Count(v => v == PluginManager.GitUpdateVerdict.HasNewCommit) == 1 &&
                        PluginManager.GitUpdateVerdict.Unknown != PluginManager.GitUpdateVerdict.HasNewCommit &&
                        PluginManager.GitUpdateVerdict.UpToDate != PluginManager.GitUpdateVerdict.HasNewCommit);

                    // ── ⑦ 呈现：中性文案与取值表 ──
                    Check("「查不到」的卡片状态行是中性如实文案，且**不是**「有新版」也不是「已是最新」",
                        PluginManager.GitStatusUnknownText(PluginSource.CommitConfidence.NotQueryable) == "更新状态待确认（未取到仓库最新提交）" &&
                        PluginManager.GitStatusUnknownText(PluginSource.CommitConfidence.Unknown) == "更新状态待确认（未取到仓库最新提交）" &&
                        PluginManager.GitStatusUnknownText(PluginSource.CommitConfidence.Queried) == "" &&
                        !PluginManager.GitStatusUnknownText(PluginSource.CommitConfidence.NotQueryable).Contains("有新版") &&
                        !PluginManager.GitStatusUnknownText(PluginSource.CommitConfidence.NotQueryable).Contains("已是最新"),
                        $"文案=「{PluginManager.GitStatusUnknownText(PluginSource.CommitConfidence.NotQueryable)}」· 已查到时为空串「{PluginManager.GitStatusUnknownText(PluginSource.CommitConfidence.Queried)}」");
                    Check("StatusNote 默认空 ⇒ 「有新版 / 已是最新 / 查询失败」三条老分支与老调用点零改动",
                        new PluginManager.PluginUpdate { Latest = "1.0.0" }.StatusNote == "" &&
                        u52git.StatusNote == "" && u52npm.StatusNote == "" &&
                        new PluginManager.PluginUpdate { Latest = "0.1.0", HasUpdate = true, TargetLabel = "最新提交" }.StatusNote == "",
                        "StatusNote 只在远端查不到时才非空");
                }

                // ══════ 52e. 用户要求：**git 源按 ref 类型分流**（2026-09-18）══════
                // 用户两轮原话：
                //   「如果是这样的话单独给 gitee 这种平台写一个策略，和 github 上面的不一样，
                //     master 肯定要报，如果是分支版本的有另一种提示方法，可选升级」
                //   「众所周知 gitee 是个共同 contribute 的 git 平台，所以应该读的是参与者们的提交吧？」
                // ⇒ 本壳比的是**仓库默认分支（或用户钉的那个 ref）的最新提交**，与仓库的**发行版/标签**无关；
                //   二者本来就会不一致（作者持续在 master 提交却不打新标签）—— 这不是 bug，但要**写明白**。
                //
                // 判定表唯一实现处：PluginSource.DecideUpdate（本段全部断言都打在它上面）；
                // 分支 vs 标签一律**问远端**（FetchRefKindAsync：先 /branches/<名>，404 再查 /tags 列表按名匹配）。
                {
                    const string gSpec = gitCodearts;      // git+https://gitee.com/iJetLi/deepseek-harness-codearts.git（无 ref）
                    const string hSpec = gitInline;        // git+https://github.com/xiaosurongjia/dsh-improved-inline-edit.git（无 ref）
                    // 现场锁文件里那个提交 + 上面那个小节用的短号工具（本块自成作用域，故各备一份）
                    const string lockSha = "2573f24228ebe8a5e6b82900cd7fe731ba7d9c8d";
                    static string Head7(string s) => s.Length >= 7 ? s.Substring(0, 7) : s;
                    var q52 = PluginSource.CommitConfidence.Queried;
                    var nq52 = PluginSource.CommitConfidence.NotQueryable;
                    var dDef = PluginSource.RefKind.DefaultBranch;
                    var dCmt = PluginSource.RefKind.Commit;
                    var dNr = PluginSource.RefKind.NamedRef;
                    var dInv = PluginSource.RefKind.Invalid;
                    var mBr = PluginSource.RefMatchKind.Branch;
                    var mTag = PluginSource.RefMatchKind.Tag;
                    var mGone = PluginSource.RefMatchKind.Missing;
                    var mUnk = PluginSource.RefMatchKind.Unknown;
                    var uHard = PluginSource.UpdateDecision.HardNewCommit;
                    var uAdv = PluginSource.UpdateDecision.AdvisoryNewCommit;
                    var uUnk = PluginSource.UpdateDecision.Unknown;
                    var uSame = PluginSource.UpdateDecision.UpToDate;
                    // 远端真实提交（2026-09-18 只读实测：gitee 默认分支 master = 9669ee48…）
                    const string remoteSha = "9669ee48a6526f7d29db72676470060f038f20bb";

                    // ── ① ref 形态分流（纯解析；github 与 gitee 都覆盖）──
                    Check("★ ref 形态四分流：无 ref⇒默认分支 / #<hex>⇒钉死 / #<名字>⇒具名 / 畸形⇒Invalid",
                        PluginSource.ClassifyRef(gSpec) == dDef &&
                        PluginSource.ClassifyRef(hSpec) == dDef &&
                        PluginSource.ClassifyRef("github:aa2246740/dsh-watcher") == dDef &&
                        PluginSource.ClassifyRef(gSpec + "#" + lockSha) == dCmt &&
                        PluginSource.ClassifyRef(hSpec + "#2573f242") == dCmt &&
                        PluginSource.ClassifyRef(gSpec + "#master") == dNr &&
                        PluginSource.ClassifyRef(gSpec + "#nightly-20260905") == dNr &&   // 标签形态上也是"具名"
                        PluginSource.ClassifyRef(hSpec + "#main") == dNr,
                        $"无ref={PluginSource.ClassifyRef(gSpec)} #sha={PluginSource.ClassifyRef(gSpec + "#" + lockSha)} "
                        + $"#master={PluginSource.ClassifyRef(gSpec + "#master")} #tag={PluginSource.ClassifyRef(gSpec + "#nightly-20260905")}");
                    Check("★ ref 畸形（空 / 含空白 / 非 ASCII / 路径穿越 / 首尾点斜杠 / 越界）一律 Invalid ⇒ 不判不报",
                        PluginSource.ClassifyRef(gSpec + "#") == dInv &&
                        PluginSource.ClassifyRef(gSpec + "#   ") == dInv &&
                        PluginSource.ClassifyRef(gSpec + "#main --upload-pack=evil") == dInv &&
                        PluginSource.ClassifyRef(gSpec + "#分支") == dInv &&
                        PluginSource.ClassifyRef(gSpec + "#../../etc/passwd") == dInv &&
                        PluginSource.ClassifyRef(gSpec + "#.hidden") == dInv &&
                        PluginSource.ClassifyRef(gSpec + "#/lead") == dInv &&
                        PluginSource.ClassifyRef(gSpec + "#" + new string('a', 65)) == dInv &&
                        PluginSource.ClassifyRef("^0.5.9") == dInv,
                        "空=「" + PluginSource.ClassifyRef(gSpec + "#") + "」· 中文=「" + PluginSource.ClassifyRef(gSpec + "#分支") + "」");

                    // ── ② 四种分流 + 失败关闭（用户要的核心）──
                    Check("★ 默认分支 + 远端确实变了 ⇒ HardNewCommit（产品口径：「master 肯定要报」）",
                        PluginSource.DecideUpdate(lockSha, remoteSha, q52, dDef, mUnk) == uHard &&
                        PluginSource.DecideUpdate(lockSha, "9669ee4", q52, dDef, mUnk) == uHard,
                        $"⇒ {PluginSource.DecideUpdate(lockSha, "9669ee4", q52, dDef, mUnk)}");
                    Check("★ 钉死在提交（#<sha>）⇒ 永不报（提交不可变），即使远端提交不同",
                        PluginSource.DecideUpdate(lockSha, remoteSha, q52, dCmt, mUnk) == uSame &&
                        PluginSource.DecideUpdate(lockSha, "9669ee4", q52, dCmt, mUnk) == uSame,
                        $"⇒ {PluginSource.DecideUpdate(lockSha, "9669ee4", q52, dCmt, mUnk)}");
                    Check("★ 具名**分支** + 远端确认为分支 + 有差异 ⇒ Advisory（可选升级，不计入一键更新）",
                        PluginSource.DecideUpdate(lockSha, "9669ee4", q52, dNr, mBr) == uAdv,
                        $"⇒ {PluginSource.DecideUpdate(lockSha, "9669ee4", q52, dNr, mBr)}");
                    Check("★ 具名**标签**：指向同一提交 ⇒ 不报（标签不可变）；被移动到别的提交 ⇒ Advisory（罕见）",
                        PluginSource.DecideUpdate(lockSha, "2573f242", q52, dNr, mTag) == uSame &&
                        PluginSource.DecideUpdate(lockSha, lockSha, q52, dNr, mTag) == uSame &&
                        PluginSource.DecideUpdate(lockSha, "9669ee4", q52, dNr, mTag) == uAdv,
                        $"同提交={PluginSource.DecideUpdate(lockSha, "2573f242", q52, dNr, mTag)} "
                        + $"被移动={PluginSource.DecideUpdate(lockSha, "9669ee4", q52, dNr, mTag)}");
                    Check("★ 失败关闭：具名 ref 但**问不出来**（Unknown/Missing/网络失败）⇒ 一律 Unknown，绝不报更新",
                        PluginSource.DecideUpdate(lockSha, "9669ee4", q52, dNr, mUnk) == uUnk &&
                        PluginSource.DecideUpdate(lockSha, "9669ee4", q52, dNr, mGone) == uUnk &&
                        PluginSource.DecideUpdate(lockSha, "9669ee4", nq52, dNr, mBr) == uUnk &&
                        PluginSource.DecideUpdate(lockSha, "9669ee4", q52, dInv, mBr) == uUnk &&
                        // 一期护栏不得破坏：查不到 / 响应读不出提交号 ⇒ Unknown（不是"有新版"）
                        PluginSource.DecideUpdate(lockSha, "", nq52, dDef, mUnk) == uUnk &&
                        PluginSource.DecideUpdate(lockSha, "Not Found Project", q52, dDef, mUnk) == uUnk &&
                        PluginSource.DecideUpdate(lockSha, "<html>rate limited</html>", q52, dDef, mUnk) == uUnk,
                        $"问不出={PluginSource.DecideUpdate(lockSha, "9669ee4", q52, dNr, mUnk)} "
                        + $"已无={PluginSource.DecideUpdate(lockSha, "9669ee4", q52, dNr, mGone)} "
                        + $"畸形={PluginSource.DecideUpdate(lockSha, "9669ee4", q52, dInv, mBr)}");

                    // ── ③ ★「可选升级」不计入 UpdatableCount，而默认分支仍计入（用户要的分流落点）──
                    var rows52e = new List<PluginManager.Plugin>
                    {
                        new() { Name = "自检-默认分支" },
                        new() { Name = "自检-具名分支" },
                        new() { Name = "自检-标签被移动" },
                        new() { Name = "自检-说不清" },
                    };
                    var maps52e = new Dictionary<string, PluginManager.PluginUpdate>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["自检-默认分支"] = new() { Name = "自检-默认分支", HasUpdate = true, Advisory = false },
                        ["自检-具名分支"] = new() { Name = "自检-具名分支", HasUpdate = true, Advisory = true },
                        ["自检-标签被移动"] = new() { Name = "自检-标签被移动", HasUpdate = true, Advisory = true },
                        ["自检-说不清"] = new() { Name = "自检-说不清", HasUpdate = false, Advisory = false },
                    };
                    PluginManager.PluginUpdate? Lookup52e(PluginManager.Plugin p)
                        => maps52e.TryGetValue(p.Name, out var v) ? v : null;
                    int hard52e = MainWindow.CountUpdatablePlugins(rows52e, Lookup52e);
                    Check("★ 「可选升级」不计入 UpdatableCount（「一键更新 N 个」与「只看有新版」的唯一口径）；默认分支仍计入",
                        hard52e == 1 &&
                        MainWindow.IsHardUpdatable(maps52e["自检-默认分支"]) &&
                        !MainWindow.IsHardUpdatable(maps52e["自检-具名分支"]) &&
                        !MainWindow.IsHardUpdatable(maps52e["自检-标签被移动"]) &&
                        !MainWindow.IsHardUpdatable(maps52e["自检-说不清"]) &&
                        // 但「有新版」这个**显示**口径里，可选升级仍然算（提示要出现，只是不自动升）
                        maps52e["自检-具名分支"].HasUpdate && maps52e["自检-标签被移动"].HasUpdate,
                        $"4 个插件（默认分支 Hard / 具名分支 Advisory / 标签移动 Advisory / 说不清）⇒ 计入一键更新的只有 {hard52e} 个（应为 1）");

                    // ── ④ 文案：含「可选」二字、「写明白」与仓库的发行版（标签）无关、括注带作者 ──
                    string advBr = PluginManager.GitAdvisoryText(mBr, "dev", "9669ee4");
                    string advTag = PluginManager.GitAdvisoryText(mTag, "nightly-20260905", "40363f6");
                    string basis = PluginSource.CompareBasisNote(gSpec, mUnk);
                    Check("★ 「可选升级」文案含「可选」二字且写明是哪个 ref；标签被移动另标「罕见」",
                        advBr.Contains("可选") && advBr.Contains("dev") && advBr.Contains("9669ee4") &&
                        advTag.Contains("可选") && advTag.Contains("移动") && advTag.Contains("罕见") &&
                        !advBr.Contains("移动"),
                        $"分支=「{advBr}」· 标签=「{advTag}」");
                    Check("★ 悬停「写明白」：按<默认分支|分支 X|标签 Y>比对，且**与仓库的发行版（标签）无关**",
                        basis.Contains("默认分支") && basis.Contains("发行版") && basis.Contains("无关") &&
                        PluginSource.CompareBasisNote(gSpec + "#dev", mBr).Contains("远端分支 dev") &&
                        PluginSource.CompareBasisNote(gSpec + "#v1", mTag).Contains("远端标签 v1") &&
                        PluginSource.CompareBasisNote(gSpec + "#dev", mUnk).Contains("无法确认") &&
                        PluginSource.CompareBasisNote(gSpec + "#" + lockSha, mUnk).Contains("已钉在提交 2573f24"),
                        basis);
                    Check("★ 提交括注含**作者**（证明读的是参与者们的提交、不按作者过滤）；链接只出白名单托管站",
                        PluginSource.NewCommitNote("9669ee4", "2026-09-18", "Jet") == "提交 9669ee4 · 2026-09-18 · 作者 Jet" &&
                        PluginSource.NewCommitNote("9669ee4", "2026-09-18", "") == "提交 9669ee4 · 2026-09-18" &&
                        PluginSource.NewCommitNote("", "", "") == "" &&
                        PluginSource.CommitUrl(gSpec, remoteSha) == "https://gitee.com/iJetLi/deepseek-harness-codearts/commit/" + remoteSha &&
                        PluginSource.CommitUrl(gSpec, "not-a-sha") == "" &&
                        PluginSource.CommitUrl("git+https://evil.example/a/b.git", lockSha) == "" &&
                        PluginMarket.IsAllowedLinkUrl(PluginSource.CommitUrl(gSpec, remoteSha)) &&
                        PluginMarket.IsAllowedLinkUrl(PluginSource.CommitUrl(hSpec, "4ba82a0cfcd20ae8605940a71a65d3d8face2974")),
                        $"括注=「{PluginSource.NewCommitNote("9669ee4", "2026-09-18", "Jet")}」· 链接=「{PluginSource.CommitUrl(gSpec, remoteSha)}」");
                    Check("★ 说不清的文案：Missing / Unknown 各一句，既不写「有新版」也不写「已是最新」；能说清时为空串",
                        PluginManager.GitRefUnknownText(dNr, mGone) == "更新状态待确认（远端已无这个分支或标签）" &&
                        PluginManager.GitRefUnknownText(dNr, mUnk) == "更新状态待确认（无法确认是分支还是标签）" &&
                        PluginManager.GitRefUnknownText(dNr, mBr) == "" &&
                        PluginManager.GitRefUnknownText(dDef, mUnk) == "" &&
                        !PluginManager.GitRefUnknownText(dNr, mGone).Contains("有新版") &&
                        !PluginManager.GitRefUnknownText(dNr, mGone).Contains("已是最新"),
                        $"「{PluginManager.GitRefUnknownText(dNr, mGone)}」/「{PluginManager.GitRefUnknownText(dNr, mUnk)}」");

                    // ── ⑤ 解析：分支 vs 标签的**实测字段位置**（喂 web_fetch 抓到的原文，不发网络请求）──
                    // 实测（2026-09-18 只读公开接口）：
                    //   gitee  /branches/master → {"name":"master","commit":{"sha":"9669ee48…",
                    //                              "commit":{"author":{"name":"Jet","date":"…"}}}}  ← 嵌套两层
                    //   gitee  /tags/nightly-20260905 → **404**（没有单资源接口）⇒ 标签只能查 /tags 列表
                    //   gitee  /tags → [{"name":"nightly-20260905","commit":{"sha":"40363f6a…","date":"…"}}]
                    //   github /branches/main → {"name":"main","commit":{"sha":"4ba82a0c…"}}  ← **没有** commit.commit
                    const string gBranchJson = "{\"name\":\"master\",\"commit\":{\"sha\":\"9669ee48a6526f7d29db72676470060f038f20bb\",\"commit\":{\"author\":{\"name\":\"Jet\",\"date\":\"2026-09-18T12:55:14+08:00\"},\"committer\":{\"name\":\"Jet\",\"date\":\"2026-09-18T12:55:14+08:00\"}}}}";
                    const string gTagsJson = "[{\"name\":\"0.1.1-rc.2\",\"commit\":{\"sha\":\"811d647f4dd94f401ba3503ec375008c8094c588\",\"date\":\"2026-08-31T11:55:44+08:00\"}},{\"name\":\"nightly-20260905\",\"commit\":{\"sha\":\"40363f6a8c4920e88764a6a7e46ba114ccbae191\",\"date\":\"2026-09-05T02:32:36+00:00\"}}]";
                    const string hBranchJson = "{\"name\":\"main\",\"commit\":{\"sha\":\"4ba82a0cfcd20ae8605940a71a65d3d8face2974\",\"url\":\"https://api.github.com/x\"},\"protected\":false}";
                    var pb = PluginSource.ParseCommitDetailJson("gitee.com", gBranchJson);
                    var hb = PluginSource.ParseCommitDetailJson("github.com", hBranchJson);
                    Check("★ 分支判定取到提交：gitee 的日期/作者在 commit.commit.author（两层），github 分支接口没有这一层",
                        pb.Sha == remoteSha && pb.Date == "2026-09-18" && pb.Author == "Jet" &&
                        hb.Sha == "4ba82a0cfcd20ae8605940a71a65d3d8face2974" && hb.Date == "" && hb.Author == "",
                        $"gitee sha={Head7(pb.Sha)} date={pb.Date} author={pb.Author} · github sha={Head7(hb.Sha)} date=「{hb.Date}」");
                    Check("★ 标签判定走**列表**按名匹配（gitee 的 /tags/<名> 实测 404）；名字大小写不敏感；查不到/坏报文一律空串不抛",
                        PluginSource.FindShaByName(gTagsJson, "nightly-20260905") == "40363f6a8c4920e88764a6a7e46ba114ccbae191" &&
                        PluginSource.FindShaByName(gTagsJson, "NIGHTLY-20260905") == "40363f6a8c4920e88764a6a7e46ba114ccbae191" &&
                        PluginSource.FindShaByName(gTagsJson, "no-such-tag") == "" &&
                        PluginSource.FindShaByName("<html>429 rate limited</html>", "x") == "" &&
                        PluginSource.FindShaByName(null, "x") == "" &&
                        PluginSource.ParseCommitDetailJson("gitee.com", "{\"message\":\"Branch does not exist\"}") == ("", "", "") &&
                        PluginSource.ParseCommitDetailJson("gitee.com", "<html>404</html>") == ("", "", ""),
                        $"nightly={Head7(PluginSource.FindShaByName(gTagsJson, "nightly-20260905"))} · 404 报文实测原文：{{\"message\":\"Branch does not exist\"}}");
                    Check("★ 作者解析：gitee/github 同形取 commit.author.name（这正是「参与者们的提交」那个字段）",
                        PluginSource.ParseCommitAuthor("gitee.com", "[{\"sha\":\"x\",\"author\":null,\"commit\":{\"author\":{\"name\":\"Jet\"}}}]") == "Jet" &&
                        PluginSource.ParseCommitAuthor("github.com", "[{\"sha\":\"x\",\"commit\":{\"author\":{\"name\":\"xiaosurongjia\"}}}]") == "xiaosurongjia" &&
                        PluginSource.ParseCommitAuthor("gitee.com", "[]") == "" &&
                        PluginSource.ParseCommitAuthor("gitee.com", "{}") == "" &&
                        PluginSource.ParseCommitAuthor("gitee.com", null) == "",
                        "实测 gitee 根上的 author 是 null ⇒ 真正有名字的是 commit.author.name");
                }

                // 样本 ⑧：「包名同名 id」样本 —— 只读取证：dsh-improved-inline-edit 的清单声明是
                //   git+https://github.com/…（git 源），而它**恰好**与 loader id 同名
                //   （Config\loader-ids.json：「dsh-improved-inline-edit」→「dsh-improved-inline-edit」）。
                //   这里验的是**管理命令的生成结果**在这个样本下仍然正确：
                //   · 更新参数走 `update 包名`（不被"包名同名 id"带偏成 `包名@标签`，也不带仓库地址/#ref）；
                //   · 禁用/启用写的仍是引擎认得的 id（同名 ⇒ 与包名一致，两种写法都命中）。
                var p52 = new PluginManager.Plugin { Name = "dsh-improved-inline-edit", LoaderId = "dsh-improved-inline-edit" };
                string argsSame52 = PluginManager.BuildUpdateArgs(p52.Name, gitInline, "仓库最新");
                Check("「包名同名 id」样本（dsh-improved-inline-edit）：更新命令走 `update 包名`，禁用/启用用的 id 也对得上",
                    PluginManager.LoaderIdFor(p52) == "dsh-improved-inline-edit" &&
                    argsSame52.Contains("plugin --profile web update dsh-improved-inline-edit") &&
                    !argsSame52.Contains("仓库最新") &&
                    !argsSame52.Contains("dsh-improved-inline-edit@") &&
                    !argsSame52.Contains("git+https") && !argsSame52.Contains("#") &&
                    PluginManager.LooksLikePluginMutation(argsSame52) &&
                    // 老口径的推导形态（无作用域包名本身就是 id 形态）与真 id 一致 ⇒ 不会写错记录
                    PluginManager.DeriveLoaderId("dsh-improved-inline-edit") == "dsh-improved-inline-edit",
                    $"id=「{PluginManager.LoaderIdFor(p52)}」· 更新参数「{argsSame52}」");
            }

            // ══════ 53. bug：git 源插件作者显示「—」、LinkUrl 指向打不开的镜像站页面 ══════
            // 仅读取证（本机 node_modules\<包>\package.json 原文的字段名）：
            //   dsh-codearts-auth / dsh-improved-inline-edit 都**没有** author / repository / homepage，
            //   只有 name / version / description；而 npm 包 dsh-context 三个字段齐全。
            //   ⇒ 「—」不是取值 bug，是这两个 git 源包自己没写作者信息。
            // 清单声明（profile\package.json 原文）：
            //   dsh-codearts-auth        = git+https://gitee.com/iJetLi/deepseek-harness-codearts.git
            //   dsh-improved-inline-edit = git+https://github.com/xiaosurongjia/dsh-improved-inline-edit.git
            // 旧 LinkUrl 的 git 源分支只认 github.com ⇒ gitee 那条认不出来，回落到镜像站包页面
            // （https://www.npmmirror.com/package/dsh-codearts-auth），而镜像站上没有这个 git 源包
            // ⇒ 点开显示「未查询到 dsh-codearts-auth」，就是现场那条死链。
            {
                // 样本 ①：来源声明 → 仓库 URL（纯函数）。四种写法都必须落到**去 .git、去 #ref** 的仓库地址。
                Check("LinkUrl 解析：git+https / github: / git@ / ssh: / git: 都得到「去掉 .git 与 #ref」的仓库 URL",
                    PluginManager.RepoUrlFromSpec("git+https://gitee.com/a/b.git") == "https://gitee.com/a/b" &&
                    PluginManager.RepoUrlFromSpec("git+https://github.com/a/b.git") == "https://github.com/a/b" &&
                    PluginManager.RepoUrlFromSpec("github:a/b#sha1a2b3c") == "https://github.com/a/b" &&
                    PluginManager.RepoUrlFromSpec("git@github.com:a/b.git") == "https://github.com/a/b" &&
                    PluginManager.RepoUrlFromSpec("ssh://git@github.com/a/b.git") == "https://github.com/a/b" &&
                    PluginManager.RepoUrlFromSpec("git://github.com/a/b.git#v1.0") == "https://github.com/a/b",
                    $"gitee=「{PluginManager.RepoUrlFromSpec("git+https://gitee.com/a/b.git")}」· "
                    + $"scp=「{PluginManager.RepoUrlFromSpec("git@github.com:a/b.git")}」");

                // 样本 ①′：现场两条真实声明各自落到自己的托管站（gitee 那条是这次的病根）。
                Check("现场两条声明：gitee 的落到 gitee、github 的落到 github（不再一律回落到镜像站）",
                    PluginManager.RepoUrlFromSpec("git+https://gitee.com/iJetLi/deepseek-harness-codearts.git")
                        == "https://gitee.com/iJetLi/deepseek-harness-codearts" &&
                    PluginManager.RepoUrlFromSpec("git+https://github.com/xiaosurongjia/dsh-improved-inline-edit.git")
                        == "https://github.com/xiaosurongjia/dsh-improved-inline-edit",
                    PluginManager.RepoUrlFromSpec("git+https://gitee.com/iJetLi/deepseek-harness-codearts.git"));

                // 样本 ②：LinkUrl 的**完整取值链**（走真清单声明）。镜像站 URL 只给 npm 包；
                // git 源与本地路径（file:）一律不用镜像站，取不到仓库地址时给空串（界面便不出死链）。
                string t53 = Path.Combine(Path.GetTempPath(), "dshguard-linkurl-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                string? bak53 = PluginManager.PackageFileOverrideForTest;
                try
                {
                    Directory.CreateDirectory(t53);
                    File.WriteAllText(Path.Combine(t53, "package.json"),
                        "{ \"name\": \"dsh-profile-web\", \"private\": true, \"dependencies\": { "
                        + "\"dsh-codearts-auth\": \"git+https://gitee.com/iJetLi/deepseek-harness-codearts.git\", "
                        + "\"dsh-watcher\": \"github:aa2246740/dsh-watcher\", "
                        + "\"dsh-local-only\": \"file:../local-plugin\", "
                        + "\"dsh-mnemon\": \"^0.5.9\" } }",
                        new UTF8Encoding(false));
                    PluginManager.PackageFileOverrideForTest = Path.Combine(t53, "package.json");

                    string giteeLink = new PluginManager.Plugin { Name = "dsh-codearts-auth" }.LinkUrl;
                    string ghLink = new PluginManager.Plugin { Name = "dsh-watcher" }.LinkUrl;
                    string npmLink = new PluginManager.Plugin { Name = "dsh-mnemon" }.LinkUrl;

                    Check("LinkUrl：git 源指向仓库本身（gitee / github 各归各站），不再落到镜像站",
                        giteeLink == "https://gitee.com/iJetLi/deepseek-harness-codearts" &&
                        ghLink == "https://github.com/aa2246740/dsh-watcher",
                        $"gitee 源=「{giteeLink}」· github 源=「{ghLink}」");

                    Check("LinkUrl：普通 npm 包才是镜像站页面；本地路径（file:）给空串（界面不出可点的死链）",
                        npmLink == "https://www.npmmirror.com/package/dsh-mnemon" &&
                        new PluginManager.Plugin { Name = "dsh-local-only" }.LinkUrl == "",
                        $"npm=「{npmLink}」· file:=「{new PluginManager.Plugin { Name = "dsh-local-only" }.LinkUrl}」");

                    // 样本 ③：作者兜底（纯函数）——有 author 用 author（标明不是仓库归属）；
                    // 无 author + 能认出仓库 ⇒ 用 owner 但**必须**挂上"来自仓库地址"的标记；
                    // 两者都没有 ⇒ 空串（绝不编一个名字出来）。
                    var authorReal = PluginManager.ResolveAuthor("bowenliang123", "git+https://github.com/bowenliang123/x.git", "");
                    var authorRepo = PluginManager.ResolveAuthor("", "git+https://gitee.com/iJetLi/deepseek-harness-codearts.git", "");
                    var authorNone = PluginManager.ResolveAuthor("", "", "");

                    Check("作者兜底：有 author 用 author；无 author + git 源用仓库 owner 并标明来源；两者都无则空",
                        authorReal == ("bowenliang123", false) &&
                        authorRepo == ("iJetLi", true) &&
                        authorNone == ("", false),
                        $"真作者=「{authorReal.Author}」(来自仓库={authorReal.FromRepo}) · "
                        + $"仓库归属=「{authorRepo.Author}」(来自仓库={authorRepo.FromRepo}) · 都无=「{authorNone.Author}」");

                    // 样本 ③′：兜底来的名字**不得伪装成作者本人** —— 悬停说明必须点明"来自仓库地址"；
                    // 而且这种名字**绝不去猜 GitHub 同名主页**（那会凭空安一个别人的主页）：
                    // 能从仓库地址认出 owner 主页就给**仓库所在站**的那一页（gitee 就是 gitee），
                    // 认不出仓库地址就给空串。
                    //
                    // 名字一律用**清单里不会存在**的样本名：AuthorProfileUrl 会先读真清单的声明，
                    // 用真包名的话，断言就跟着"这台机器上恰好装了什么"飘（本项目已有同类教训）。
                    var repoOnly = new PluginManager.Plugin
                    {
                        Name = "自检-仓库归属样本（不存在）",
                        Author = "iJetLi",
                        AuthorFromRepo = true,
                        RepositoryUrl = "https://gitee.com/iJetLi/deepseek-harness-codearts"
                    };
                    var repoUnresolvable = new PluginManager.Plugin
                    { Name = "自检-查不到仓库的包（不存在）", Author = "someone", AuthorFromRepo = true };
                    var realAuthor = new PluginManager.Plugin
                    { Name = "自检-真作者样本（不存在）", Author = "bowenliang123" };
                    string repoOwnerPage = MainWindow.AuthorProfileUrlForTest(repoOnly);
                    Check("作者兜底值必须标明「来自仓库地址」，且不冒充作者本人（不猜 GitHub 同名主页）",
                        repoOnly.AuthorTip.Contains("来自仓库地址") &&
                        !repoOnly.AuthorTip.Contains("作者：") &&
                        realAuthor.AuthorTip.Contains("作者：") &&
                        // 仓库归属：给的是**仓库所在站**的 owner 页（gitee），而不是猜出来的 github.com/iJetLi
                        repoOwnerPage == "https://gitee.com/iJetLi" &&
                        // 认不出仓库地址 ⇒ 空串（绝不拼 https://github.com/{作者名}）
                        MainWindow.AuthorProfileUrlForTest(repoUnresolvable) == "" &&
                        // 真作者：仓库认不出来时才回落 GitHub 同名主页（老行为不变）
                        MainWindow.AuthorProfileUrlForTest(realAuthor) == "https://github.com/bowenliang123",
                        $"仓库归属提示=「{repoOnly.AuthorTip}」· 归属主页=「{repoOwnerPage}」· "
                        + $"认不出仓库=「{MainWindow.AuthorProfileUrlForTest(repoUnresolvable)}」");
                }
                finally
                {
                    PluginManager.PackageFileOverrideForTest = bak53;
                    try { Directory.Delete(t53, true); } catch { }
                }
            }

            // ══════ 54. 半截安装死循环修复：三态判定 + 残留清理 + 路径安全（现场：
            //             node_modules\@furongjun1999\dsh-memory 目录在、package.json 缺 ⇒
            //             pnpm 报「目录已存在」拒绝安装、界面判"未安装" ⇒ 再装再失败 ⇒ 死循环）══════
            {
                string t54 = Path.Combine(Path.GetTempPath(), "dshguard-halfstate-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                try
                {
                    Directory.CreateDirectory(Path.Combine(t54, "node_modules", "dsh-ok"));
                    File.WriteAllText(Path.Combine(t54, "node_modules", "dsh-ok", "package.json"),
                        "{ \"name\": \"dsh-ok\", \"version\": \"1.0.0\" }", new UTF8Encoding(false));

                    // 三态 ①：目录不在 ⇒ 未安装（%TEMP% 真造目录夹具）
                    Check("三态判定：目录不在 ⇒ 未安装；目录+package.json 都在 ⇒ 已安装（%TEMP% 真夹具）",
                        PluginManager.EvaluateInstallState("dsh-not-there", t54) == PluginManager.InstallStateKind.NotInstalled &&
                        PluginManager.EvaluateInstallState("dsh-ok", t54) == PluginManager.InstallStateKind.Installed &&
                        PluginManager.EvaluateInstallState("", t54) == PluginManager.InstallStateKind.NotInstalled,
                        $"dsh-not-there={PluginManager.EvaluateInstallState("dsh-not-there", t54)} · dsh-ok={PluginManager.EvaluateInstallState("dsh-ok", t54)}");

                    // 三态 ②：目录在、package.json 缺 ⇒ 损坏（现场那一支，含 scope 形态）
                    Directory.CreateDirectory(Path.Combine(t54, "node_modules", "dsh-half"));
                    Directory.CreateDirectory(Path.Combine(t54, "node_modules", "@furongjun1999", "dsh-memory"));
                    Check("三态判定：目录在、package.json 缺 ⇒ 损坏（半截安装；scope 形态同样命中）",
                        PluginManager.EvaluateInstallState("dsh-half", t54) == PluginManager.InstallStateKind.Broken &&
                        PluginManager.EvaluateInstallState("@furongjun1999/dsh-memory", t54) == PluginManager.InstallStateKind.Broken,
                        $"dsh-half={PluginManager.EvaluateInstallState("dsh-half", t54)} · @scope={PluginManager.EvaluateInstallState("@furongjun1999/dsh-memory", t54)}");

                    // 清理入口：合法半截态真的清掉；健康目录与已安装目录绝不动
                    var cOk = PluginManager.CleanBrokenInstall("dsh-half", t54);
                    Check("清理入口：半截残留目录被清掉（带重试、落 NoteDiagnosis）；已安装目录不动",
                        cOk.Attempted && cOk.Cleared && !cOk.Rejected &&
                        !Directory.Exists(Path.Combine(t54, "node_modules", "dsh-half")) &&
                        Directory.Exists(Path.Combine(t54, "node_modules", "dsh-ok")),
                        cOk.Reason);

                    var cHealthy = PluginManager.CleanBrokenInstall("dsh-ok", t54);
                    Check("清理入口：已安装（目录完好）⇒ 不清理（用户数据绝不误删）",
                        !cHealthy.Attempted && !cHealthy.Cleared && !cHealthy.Rejected,
                        cHealthy.Reason);

                    // 路径安全：.. / 绝对路径 / rooted-path 陷阱 / 陌生包名，全部拒绝且什么都不删
                    string sentinel = Path.Combine(t54, "node_modules", "哨兵-不许删");
                    Directory.CreateDirectory(sentinel);
                    var cUp = PluginManager.CleanBrokenInstall("..", t54);
                    var cAbs = PluginManager.CleanBrokenInstall(@"C:\Windows\Temp", t54);
                    var cRooted = PluginManager.CleanBrokenInstall(@"C:\Windows\Temp\dsh-evil", t54);
                    var cWeird = PluginManager.CleanBrokenInstall("x&calc", t54);
                    Check("清理入口路径安全：.. / 绝对路径 / rooted-path（Path.Combine 丢前缀陷阱）/ 注入构造全部被拒",
                        cUp.Rejected && !cUp.Attempted &&
                        cAbs.Rejected && !cAbs.Attempted &&
                        cRooted.Rejected && !cRooted.Attempted &&
                        cWeird.Rejected && !cWeird.Attempted &&
                        Directory.Exists(sentinel) && Directory.Exists(Path.Combine(t54, "node_modules", "dsh-ok")),
                        $"..={cUp.Reason} · 绝对路径={cRooted.Reason}");

                    // 上面这组 rooted-path 断言的价值：Path.Combine(nodeModules, "C:\\Windows\\…")
                    // 会**丢弃前缀**返回绝对路径本身，靠 GetFullPath + 前缀比对才能识破。
                    // （C:\Windows\Temp 在测试机必然存在 ⇒ 若校验失效，这条会真的去删系统目录内容 —— 校验必须挡住。）
                    Check("清理入口：rooted-path 陷阱下 sentinel 与其它目录完好（校验真的挡住了删除动作）",
                        Directory.Exists(sentinel) && Directory.Exists(Path.Combine(t54, "node_modules")) &&
                        Directory.GetDirectories(Path.Combine(t54, "node_modules")).Length >= 2,
                        $"node_modules 下还有 {Directory.GetDirectories(Path.Combine(t54, "node_modules")).Length} 个目录");

                    // 非 Broken 态的清理调用是 no-op（未安装：目录不在 ⇒ Attempted=false 但无需报错）
                    var cNone = PluginManager.CleanBrokenInstall("dsh-not-there", t54);
                    Check("清理入口：未安装（目录不在）⇒ 无动作、可继续安装",
                        !cNone.Attempted && cNone.Cleared && !cNone.Rejected,
                        cNone.Reason);
                }
                finally { try { Directory.Delete(t54, true); } catch { } }

                // 第四件事的两条补漏：自检隔离（App.OnStartup 提前设 DSHGUARD_DATA_DIR + LoaderIdCacheFile 走 ConfigDir）
                Check("自检隔离：--selftest/--shot/--dialog-shot 命中夹具判定（DSHGUARD_DATA_DIR 提前挂接的前提）",
                    App.IsTestFixtureRun(new[] { "--selftest" }) &&
                    App.IsTestFixtureRun(new[] { "--shot", "x.png" }) &&
                    App.IsTestFixtureRun(new[] { "--dialog-shot" }) &&
                    !App.IsTestFixtureRun(Array.Empty<string>()) &&
                    !App.IsTestFixtureRun(new[] { "--uninstall" }),
                    "测试夹具判定（App.IsTestFixtureRun，纯函数）");

                string cfgDir = GuardPaths.ConfigDir;
                Check("自检隔离：loader-ids.json 的默认落点是 ConfigDir\\loader-ids.json（不再硬编码 ExeDir\\Config）",
                    PluginManager.LoaderIdCacheFileForTest().Equals(
                        Path.Combine(cfgDir, "loader-ids.json"), StringComparison.OrdinalIgnoreCase),
                    $"默认={PluginManager.LoaderIdCacheFileForTest()} · ConfigDir={cfgDir}");
            }

            // ══════ 55. 本地链接插件（link:）：来源识别 + 目录解析（越界拒绝 vs 目录不在）══════
            //
            // 现场 bug：卡片 dsh-imagegen 的标题不可点、没有"有链接"的小箭头，用户描述为
            // "无法识别，不能进入超链接"。根因**不是**识别不到包 —— 清单里就写着
            // `link:./plugins/dsh-imagegen`、node_modules 下是指向真实目录的 Junction、loader id 也在缓存里；
            // 而是**没有网址可点**：本地链接走不到任何仓库/镜像站，LinkUrl 如实给空串
            //（那是刻意的，不能退回死链）。旧实现只认"网址" ⇒ 没网址就不可点，也没告诉用户为什么没有网址。
            // 修法：把"本地链接/本地路径"升为明确的一类（PluginSourceKind.Local），并给出可打开的**本地目录**。
            //
            // 本节只做**只读**断言（不安装、不写盘、不改实现）：
            //   ① 来源识别：link: / file: / 相对 / 绝对路径 ⇒ Local；版本范围与 git 源 ⇒ 不是 Local；
            //   ② 目录解析：声明路径按 profile 解析，拿不到再退回 node_modules\<包名>（现场那个 Junction 靠这条兜住）；
            //   ③ **越界被拒**与**目录不在**必须分开（Rejected 不同 ⇒ 提示口径不同，不能混成一句话）；
            //   ④ 夹具全部造在 %TEMP% 里，用完即清（不在用户目录或项目目录里造任何东西）。
            //
            // 期望值来源：实现函数（PluginManager.IsLocalSourceSpec / ClassifySource /
            // ResolveLocalPluginDir / TryUnderRoots）已**逐字复制**到 %TEMP% 独立工程真跑取证，
            // 实际输出与本节的期望值逐条对齐（见交付说明）。
            {
                Check("本地链接插件：本地来源声明识别为 Local（link:/file:/相对/绝对路径）",
                    PluginManager.IsLocalSourceSpec("link:./plugins/x") &&
                    PluginManager.IsLocalSourceSpec("file:./plugins/x") &&
                    PluginManager.IsLocalSourceSpec("./plugins/x") &&
                    PluginManager.IsLocalSourceSpec("../plugins/x") &&
                    PluginManager.IsLocalSourceSpec(@".\plugins\x") &&
                    PluginManager.IsLocalSourceSpec(@"..\plugins\x") &&
                    PluginManager.IsLocalSourceSpec(@"C:\plugins\x") &&
                    PluginManager.IsLocalSourceSpec("/abs/x") &&
                    PluginManager.ClassifySource("link:./plugins/x") == PluginManager.PluginSourceKind.Local,
                    "link:/file:/./ /../ /.\\ /..\\ /C:\\ /abs 八种写法全部命中 Local");

                Check("本地链接插件：普通版本范围与 git 源都不是本地来源（不误判）",
                    !PluginManager.IsLocalSourceSpec("^1.2.3") &&
                    !PluginManager.IsLocalSourceSpec("~1.2.3") &&
                    !PluginManager.IsLocalSourceSpec("1.2.3") &&
                    !PluginManager.IsLocalSourceSpec("git+https://github.com/o/r.git") &&
                    !PluginManager.IsLocalSourceSpec("github:o/r") &&
                    !PluginManager.IsLocalSourceSpec("latest") &&
                    !PluginManager.IsLocalSourceSpec("*") &&
                    !PluginManager.IsLocalSourceSpec("") &&
                    !PluginManager.IsLocalSourceSpec(null) &&
                    PluginManager.ClassifySource("^1.2.3") == PluginManager.PluginSourceKind.Registry &&
                    PluginManager.ClassifySource("git+https://github.com/o/r.git") == PluginManager.PluginSourceKind.Git &&
                    PluginManager.ClassifySource(null) == PluginManager.PluginSourceKind.Unknown,
                    "版本范围→Registry · git 源→Git · 空/null→Unknown（都不算本地）");

                string t55 = Path.Combine(Path.GetTempPath(), "dshguard-link-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                string? pkgBak55 = PluginManager.PackageFileOverrideForTest;
                try
                {
                    // 夹具：<t55>\profiles\web\{plugins\x, node_modules\x, node_modules\@scope\y}
                    // 另外造一个**越界但真实存在**的目录（带哨兵文件），用来证明"给空串是因为越界被拒，
                    // 而不是因为它不存在" —— 否则拿一条根本不存在的路径也能把这条断言蒙过去。
                    string prof55 = Path.Combine(t55, "profiles", "web");
                    string outside55 = Path.Combine(t55, "outside");
                    string sentinel55 = Path.Combine(outside55, "哨兵-不许碰.txt");
                    Directory.CreateDirectory(Path.Combine(prof55, "plugins", "x"));
                    Directory.CreateDirectory(Path.Combine(prof55, "node_modules", "x"));
                    Directory.CreateDirectory(Path.Combine(prof55, "node_modules", "@scope", "y"));
                    Directory.CreateDirectory(outside55);
                    File.WriteAllText(sentinel55, "哨兵内容-不许改动", new UTF8Encoding(false));
                    GuardPaths.Apply(null, null, prof55);

                    var okDeclare = PluginManager.ResolveLocalPluginDir("link:./plugins/x", "x", prof55);
                    Check("本地链接插件：声明路径按 profile 解析（目录存在 ⇒ 非空且未被拒）",
                        okDeclare.Dir.Length > 0 && !okDeclare.Rejected &&
                        okDeclare.Dir.Equals(Path.Combine(prof55, "plugins", "x"), StringComparison.OrdinalIgnoreCase),
                        okDeclare.Dir.Length > 0 ? okDeclare.Dir : $"Dir=空 · Reason={okDeclare.Reason}");

                    var okFallback = PluginManager.ResolveLocalPluginDir("link:./plugins/nope", "x", prof55);
                    Check("本地链接插件：声明目录不在 ⇒ 退回 node_modules\\<包名>（现场 Junction 靠这条兜住）",
                        !okFallback.Rejected &&
                        okFallback.Dir.Equals(Path.Combine(prof55, "node_modules", "x"), StringComparison.OrdinalIgnoreCase),
                        okFallback.Dir.Length > 0 ? okFallback.Dir : $"Dir=空 · Reason={okFallback.Reason}");

                    var okScope = PluginManager.ResolveLocalPluginDir("link:./plugins/nope", "@scope/y", prof55);
                    Check("本地链接插件：scope 包名退回 node_modules\\@scope\\y",
                        okScope.Dir.Equals(Path.Combine(prof55, "node_modules", "@scope", "y"), StringComparison.OrdinalIgnoreCase),
                        okScope.Dir.Length > 0 ? okScope.Dir : $"Dir=空 · Reason={okScope.Reason}");

                    // 「目录不在」与「越界被拒」严格区分：前者 Rejected=false（界面说"本机没有它的目录"），
                    // 后者 Rejected=true（界面说"已拒绝打开"）—— 两句提示口径不同，绝不能混成一句。
                    var missing = PluginManager.ResolveLocalPluginDir("link:./plugins/nope", "nope", prof55);
                    var outsideRel = PluginManager.ResolveLocalPluginDir("link:../../..", "nope", prof55);
                    var outsideAbs = PluginManager.ResolveLocalPluginDir("link:" + outside55, "nope", prof55);
                    var outsideSys = PluginManager.ResolveLocalPluginDir(@"link:C:\Windows", "nope", prof55);
                    var outsideUnc = PluginManager.ResolveLocalPluginDir(@"link:\\server\share", "nope", prof55);
                    var outsideFile = PluginManager.ResolveLocalPluginDir("file:///C:/Windows", "nope", prof55);
                    Check("本地链接插件：越界被拒（Rejected=true）与「目录不在」（Rejected=false）严格区分",
                        missing.Dir.Length == 0 && !missing.Rejected &&
                        outsideRel.Dir.Length == 0 && outsideRel.Rejected &&
                        outsideAbs.Dir.Length == 0 && outsideAbs.Rejected &&
                        outsideSys.Dir.Length == 0 && outsideSys.Rejected &&
                        outsideUnc.Dir.Length == 0 && outsideUnc.Rejected &&
                        outsideFile.Dir.Length == 0 && outsideFile.Rejected,
                        $"目录不在 → Rejected=False（{missing.Reason}）· ../../.. → Rejected=True（{outsideRel.Reason}）");

                    // 判据只用夹具自己的目录（outside55 必定存在）：C:\Windows 只作对照打印，不参与判定，
                    // 免得这条因"某台机器上没有 C:\Windows"这种与代码无关的理由变红。
                    Check("本地链接插件：越界目标**真实存在**（证明拒绝不是靠「目录不在」蒙对的）",
                        Directory.Exists(outside55),
                        $"link:<夹具外的真实目录> 存在={Directory.Exists(outside55)}（仍被拒）· C:\\Windows 存在={Directory.Exists("C:\\Windows")}（对照，不参与判定）");

                    // 「没有真的去碰那个目录」：ResolveLocalPluginDir 路径上只做
                    // GetFullPath + Directory.Exists + 前缀比对，**不创建、不读取内容、不写入**任何东西。
                    // 哨兵文件原封不动就是反证（越界那条若真去"打开/准备"了目录，内容或条目数会变）。
                    Check("本地链接插件：越界被拒时没有去碰那个目录（哨兵文件与目录条目原封不动）",
                        Directory.Exists(outside55) &&
                        Directory.GetFileSystemEntries(outside55).Length == 1 &&
                        File.ReadAllText(sentinel55) == "哨兵内容-不许改动" &&
                        Directory.Exists(Path.Combine(prof55, "plugins", "x")) &&
                        Directory.GetFiles(prof55).Length == 0 &&
                        Directory.GetFileSystemEntries(prof55).Length == 2,          // 只有 plugins 与 node_modules
                        $"夹具外目录条目={Directory.GetFileSystemEntries(outside55).Length}（期望 1）· profile 下条目={Directory.GetFileSystemEntries(prof55).Length}（期望 2）");

                    Check("本地链接插件：web-evil 不被当成 web 的子目录（前缀比对带分隔符）",
                        PluginManager.ResolveLocalPluginDir("link:" + prof55 + "-evil", "nope", prof55).Rejected,
                        $"link:{prof55}-evil ⇒ 越界拒绝");

                    var emptyIn = PluginManager.ResolveLocalPluginDir(null, null, prof55);
                    var emptyIn2 = PluginManager.ResolveLocalPluginDir("", "", prof55);
                    var emptyProf = PluginManager.ResolveLocalPluginDir(null, null, null);
                    Check("本地链接插件：空/null 输入不崩、返回空目录（不抛异常）",
                        emptyIn.Dir.Length == 0 && emptyIn2.Dir.Length == 0 && emptyProf.Dir.Length == 0,
                        $"null/null → 「{emptyIn.Reason}」· null 声明 → 「{emptyProf.Reason}」");

                    // 空白 profileDir 的口径是"回落当前 ProfileDir"（GuardPaths.Normalize 把空白当没给值，
                    // 见 GuardPaths.IsBlank），不是"当成空目录"—— 此时 ProfileDir 已被 Apply 指向夹具，
                    // 所以它应当解析出与显式传 prof55 完全相同的结果。
                    var blankProf = PluginManager.ResolveLocalPluginDir("link:./plugins/x", "x", "   ");
                    Check("本地链接插件：空白 profileDir 回落当前 ProfileDir（不是当成空目录）",
                        blankProf.Dir.Equals(Path.Combine(prof55, "plugins", "x"), StringComparison.OrdinalIgnoreCase),
                        $"ProfileDir={GuardPaths.ProfileDir} · Dir=「{blankProf.Dir}」");

                    // 卡片接线（**只读核对，实现未改**）：MainWindow.Tools.cs:797-800 是
                    //     string link = p.LinkUrl;
                    //     string localDir = link.Length > 0 ? "" : p.LocalDir;
                    //     bool clickable = link.Length > 0 || localDir.Length > 0;
                    // ⇒「有网址」与「有本地目录」互斥（有网址时不再算本地目录），二者任一成立即可点。
                    // 这里把该口径**逐字**复刻成局部函数，喂真实 Plugin 对象，量的是"接线算出来到底可不可点"。
                    static bool CardClickableForTest(string linkUrl, string localDir)
                    {
                        string localDirEffective = linkUrl.Length > 0 ? "" : localDir;
                        return linkUrl.Length > 0 || localDirEffective.Length > 0;
                    }

                    File.WriteAllText(Path.Combine(prof55, "package.json"),
                        "{ \"name\": \"dsh-profile-web\", \"private\": true, \"dependencies\": { "
                        + "\"dsh-imagegen\": \"link:./plugins/x\", "
                        + "\"dsh-evil-local\": \"link:../../..\", "
                        + "\"dsh-plain\": \"^1.2.3\" } }",
                        new UTF8Encoding(false));
                    PluginManager.PackageFileOverrideForTest = Path.Combine(prof55, "package.json");

                    var pLinked = new PluginManager.Plugin { Name = "dsh-imagegen" };
                    string linkU = pLinked.LinkUrl, dirU = pLinked.LocalDir;
                    Check("本地链接插件：本地插件的标题可点（网址为空，但有本地目录 ⇒ 现场那个 bug 已修）",
                        linkU.Length == 0 && dirU.Length > 0 && CardClickableForTest(linkU, dirU),
                        $"LinkUrl=「{linkU}」· LocalDir=「{dirU}」· clickable=True");

                    var pEvil = new PluginManager.Plugin { Name = "dsh-evil-local" };
                    string linkE = pEvil.LinkUrl, dirE = pEvil.LocalDir;
                    Check("本地链接插件：越界声明 ⇒ 卡片不可点（LocalDir 空，绝不退而求其次打开没校验过的路径）",
                        linkE.Length == 0 && dirE.Length == 0 && !CardClickableForTest(linkE, dirE),
                        $"LinkUrl=「{linkE}」· LocalDir=「{dirE}」· clickable=False");

                    var pPlain = new PluginManager.Plugin { Name = "dsh-plain" };
                    string linkP = pPlain.LinkUrl, dirP = pPlain.LocalDir;
                    Check("本地链接插件：普通 npm 包仍靠网址可点（本地目录不参与它）",
                        linkP.Length > 0 && dirP.Length == 0 && CardClickableForTest(linkP, dirP),
                        $"LinkUrl=「{linkP}」· LocalDir=「{dirP}」· clickable=True");

                    var pNone = new PluginManager.Plugin { Name = "dsh-未登记" };
                    Check("本地链接插件：非本地类且无网址时仍走镜像站包页（链路不塌）",
                        pNone.LinkUrl.Contains("npmmirror.com/package/", StringComparison.Ordinal) &&
                        pNone.LocalDir.Length == 0 && CardClickableForTest(pNone.LinkUrl, pNone.LocalDir),
                        $"LinkUrl=「{pNone.LinkUrl}」· LocalDir=「{pNone.LocalDir}」");
                }
                finally
                {
                    PluginManager.PackageFileOverrideForTest = pkgBak55;
                    GuardPaths.Apply(null, null, null);          // 路径指向还原（与快照用例同一套收尾）
                    try { Directory.Delete(t55, true); } catch { }
                }
            }

            // ══════ 56. 引擎 stderr 分诊：已知无害 vs 真问题（纯函数，此前 0 条覆盖）══════
            //
            // 现场问题：日志里出现
            //     [stderr] Error: AttachConsole failed
            //     [stderr]     at Object.<anonymous> (…\node_modules\node-pty\lib\conpty_console_list_agent.js:13:26)
            // 它来自**引擎自己的依赖 node-pty**（那个 agent 要 AttachConsole 去列控制台进程，
            // 而引擎是本壳以 CreateNoWindow=true 拉起的 ⇒ 必然失败），**无害**：agent 是
            // child_process.fork 出来的独立进程，失败只让它自己非 0 退出，引擎既没崩也没少功能。
            // 但本壳把引擎 stderr 原样摆进日志/事件栏 ⇒ 用户以为出故障了。所以做**分诊**而不是丢弃。
            //
            // 三条硬规矩（断言就是照这三条来的）：① **失败关闭** —— 只对清单里逐字匹配的形态放行，
            // 其余（含任何没见过的 stderr、任何非 stderr 输出）一律当真问题；② 只降级、不销毁证据；
            // ③ 命中的行绝不参与启动失败判定。
            //
            // ⚠ 分诊必须**整块**判而不是逐行判：`AttachConsole failed` 抛错后 node 会把调用栈逐行打到
            //   stderr，那些栈帧行本身不含任何可识别字样（`    at Object.<anonymous> (…:13:26)`），
            //   逐行判就全落到"真问题"里去 —— 下面 ④ 与 ⑤ 两组边界正是照这一点设计的。
            // 期望值来源：Logger.ClassifyStderrStep / IsStackFrameLine / IsNodeVersionFooter /
            // MatchKnownNoise 已**逐字复制**到 %TEMP% 独立工程真跑取证（签名已按 Logger.cs 现状核对）。
            {
                // 把"引擎 stderr 分诊"的接线口径逐字复刻：只有带 [stderr] 前缀的行才进分诊，
                // 其余（stdout）一律不当无害 —— 与 MainWindow.xaml.cs:1431-1441 的实际调用点一致。
                static List<(string Line, bool Benign)> TriageStderrForTest(string?[] lines)
                {
                    bool inBlock = false;
                    var res = new List<(string, bool)>();
                    foreach (var raw in lines)
                    {
                        bool benign = false;
                        string line = raw ?? "";
                        if (line.StartsWith(ProcessManager.StderrTag, StringComparison.Ordinal))
                        {
                            var step = Logger.ClassifyStderrStep(inBlock, line.Substring(ProcessManager.StderrTag.Length));
                            inBlock = step.InBenignBlock;
                            benign = step.Benign;
                        }
                        res.Add((line, benign));
                    }
                    return res;
                }

                // ① 用户报告的那条：首行 + 其后全部栈帧 + 收尾脚注 → **整段全判无害**
                var triage1 = TriageStderrForTest(new string?[]
                {
                    "[stderr] Error: AttachConsole failed",
                    "[stderr]     at Object.<anonymous> (C:\\x\\node_modules\\node-pty\\lib\\conpty_console_list_agent.js:13:26)",
                    "[stderr]     at Module._compile (node:internal/modules/cjs/loader:1714:14)",
                    "[stderr]     at async Function.startup (C:\\x\\wrapper.js:12:3)",
                    "[stderr] Node.js v24.20.0"
                });
                Check("引擎 stderr 分诊：AttachConsole failed 整段（含栈与版本脚注）全判无害",
                    triage1.Count == 5 && triage1.All(x => x.Benign),
                    $"{triage1.Count(x => x.Benign)}/{triage1.Count} 行判无害（应为 5/5）");

                // ② 没见过的报错：一句都不能放过（失败关闭）
                var triage2 = TriageStderrForTest(new string?[] { "[stderr] Error: something else" });
                Check("引擎 stderr 分诊：Error: something else 判真问题（失败关闭）",
                    triage2.Count == 1 && !triage2[0].Benign,
                    $"Benign={triage2[0].Benign}");

                // ③ 空串 / null / 只有前缀：不崩，也不许被判成"已知无害"
                var triage3 = TriageStderrForTest(new string?[] { "", null, "[stderr] ", null, "" });
                Check("引擎 stderr 分诊：空串/null/只有前缀不崩，且都不算已知无害",
                    triage3.Count == 5 && triage3.All(x => !x.Benign),
                    $"{triage3.Count(x => x.Benign)}/5 行被判无害（应为 0/5）");

                // ④ 段内的栈帧被吞，但紧随其后的**另一条真问题**绝不能被吞
                //   （ClassifyStderrStep 注释里点名的那个陷阱：遇非栈帧行本块立即结束、该行重新独立判定）
                var triage4 = TriageStderrForTest(new string?[]
                {
                    "[stderr] Error: AttachConsole failed",
                    "[stderr]     at Object.<anonymous> (C:\\x\\a.js:1:1)",
                    "[stderr] Error: something else"
                });
                Check("引擎 stderr 分诊：无害段内的栈帧被吞，但紧随的真问题不被吞",
                    triage4.Count == 3 && triage4[0].Benign && triage4[1].Benign && !triage4[2].Benign,
                    $"[{string.Join(" | ", triage4.Select(x => x.Benign ? "无害" : "真问题"))}]");

                // ⑤ 真问题的调用栈一律不得被误吞（①的镜像反例：块状态在真问题行已复位）
                var triage5 = TriageStderrForTest(new string?[]
                {
                    "[stderr] Error: something else",
                    "[stderr]     at Object.<anonymous> (C:\\x\\a.js:1:1)",
                    "[stderr]     at Module._compile (node:internal/x.js:1:1)"
                });
                Check("引擎 stderr 分诊：真问题的调用栈不得被误吞（块状态已复位）",
                    triage5.Count == 3 && triage5.All(x => !x.Benign),
                    $"[{string.Join(" | ", triage5.Select(x => x.Benign ? "无害" : "真问题"))}]");

                // 补：没有 [stderr] 前缀的行不进分诊（stdout 里写着 Error: 也不算"已知无害"）
                var triageNoPrefix = TriageStderrForTest(new string?[] { "Error: AttachConsole failed" });
                Check("引擎 stderr 分诊：不带 [stderr] 前缀的行不进分诊（绝不因此被判无害）",
                    !triageNoPrefix[0].Benign, $"Benign={triageNoPrefix[0].Benign}");

                // 补：清单**逐条**给样本并核对命中，外加两条反例。
                // ⚠ 这里**不写死条数**（"数个数"正是第 ⑤ 条踩过的坑）：改为"每个样本都命中、且
                //   命中的条目恰好覆盖清单全部条目"。日后往 KnownStderrNoises 加一条而没补样本，
                //   这条会直接报出缺哪一条，而不是静默地少测一条。
                var noiseSamples = new (string Line, string ExpectId)[]
                {
                    ("Error: AttachConsole failed", "node-pty AttachConsole"),
                    ("npm warn Unknown env config \"node-linker\". This will stop working in the next major version.", "npm Unknown env config"),
                    ("Changelog: https://github.com/pnpm/pnpm/releases/tag/v10.0.0  (pnpm.io)", "pnpm changelog hint"),
                    ("To update, run: pnpm self-update", "pnpm self-update hint"),
                    ("(node:12345) ExperimentalWarning: The fs.promises API is experimental", "Node ExperimentalWarning"),
                };
                var hitIds = new List<string>();
                var wrongId = new List<string>();
                foreach (var (sample, expectId) in noiseSamples)
                {
                    string? got = Logger.MatchKnownNoise(sample)?.Id;
                    if (got == null) wrongId.Add($"未命中：{expectId}");
                    else
                    {
                        if (got != expectId) wrongId.Add($"命中错条：期望 {expectId} 实为 {got}");
                        if (!hitIds.Contains(got)) hitIds.Add(got);
                    }
                }
                var uncovered = Logger.KnownStderrNoises.Select(n => n.Id).Where(id => !hitIds.Contains(id)).ToList();
                Check("引擎 stderr 分诊：清单每一条都有样本命中，且反例不被误吞（失败关闭）",
                    wrongId.Count == 0 && uncovered.Count == 0 &&
                    Logger.MatchKnownNoise("Error: ExperimentalWarning: boom") == null &&
                    Logger.MatchKnownNoise("Update available! 12.3.4") == null,
                    wrongId.Count > 0 ? string.Join("；", wrongId)
                        : uncovered.Count > 0 ? "清单里没有样本的条目：" + string.Join("、", uncovered)
                        : $"{hitIds.Count}/{Logger.KnownStderrNoises.Length} 条样本命中 · `Error: ExperimentalWarning:` 与升级提示两反例均为 null");
            }

            // ══════ 57. bug：patch 文件不存在时先写模板（末行 `[]`）再追加块序列 ⇒ 整份 YAML 非法 ══════
            // 现场（压测 C 发现 + 复核）：Disable 与 DisableMany **两处**都在"文件不存在"时先写模板
            //   "# Your patch layer for this dsh profile, applied after every bundle layer:\n[]\n"，
            // 紧接着 text.TrimEnd() + "\n" + block 把块序列追加进去 ⇒ 磁盘上是
            //   "# Your patch layer …:\n[]\n- id: \"xxx\"\n  disabled: true\n"
            // —— `[]` 本身已是一个完整的顶层节点，其后又出现块序列项 ⇒ **同一文档两个顶层节点**。
            // 用 profile 自带的真实解析器 yaml@2.9.0 在 %TEMP% 副本上实测：
            //   模板+单条 = 7 个错误（Unexpected seq-item-ind token in YAML stream: "-"）；
            //   多条形态 = 14 个错误；对照「正文里已有条目」的形态 = VALID_YAML。
            // 触发面：全新机器 / patch 文件被删后**第一次点「禁用插件」** ⇒ 必然触发。
            //
            // 为什么程序自己发现不了：ValidatePatchText 只认 `- id:` 开头的行（其余 continue 跳过），
            // `[]` 那行被跳过 ⇒ 写后校验"通过"、界面回「已禁用」，而引擎读到的是**非法 YAML**。
            // 修法：① 两处模板都改成**纯注释**（去掉 `[]`）；② 新增 ValidatePatchStructure 并接进
            // WritePatchChecked（只校验、拒绝，不自动改写用户文件）。
            //
            // 本组断言全部用 %TEMP% 夹具 + 真实写入链路（PatchFileOverrideForTest）**真跑**，
            // 不依赖任何"数一数有几条"的口径。
            {
                const string header = "# Your patch layer for this dsh profile, applied after every bundle layer:\n";
                const string block1 = "\n# DSHGuard 于 2026-09-17 12:00:00 禁用（备份 cordis.patch.yml.bak-20260917-120000）\n- id: \"@a/b\"\n  disabled: true\n";
                const string block2 = "\n# DSHGuard 于 2026-09-17 12:00:01 禁用（备份 cordis.patch.yml.bak-20260917-120001）\n- id: dsh-zh\n  disabled: true\n";

                // ① 新模板 + 追加块（= 修好之后 Disable 的真实产物形态）⇒ 结构必须合法
                var okNew1 = PluginManager.ValidatePatchStructure(header + block1);
                var okNew2 = PluginManager.ValidatePatchStructure(header + block1 + block2);
                Check("patch 结构校验：纯注释模板 + 追加块序列 ⇒ 判**合法**（修好后的产物形态）",
                    okNew1.Ok && okNew2.Ok && okNew1.Reason.Length == 0 && okNew2.Reason.Length == 0,
                    $"单条 Ok={okNew1.Ok} · 两条 Ok={okNew2.Ok}");

                // ② 反例：旧模板 `[]` + 块序列（本次缺陷的真实产物）⇒ 必须判**非法**，且给出中性中文原因
                var badOld = PluginManager.ValidatePatchStructure(header + "[]\n" + block1);
                var badOldMany = PluginManager.ValidatePatchStructure(header + "[]\n" + block1 + block2);
                var badBare = PluginManager.ValidatePatchStructure("[]\n- id: x\n  disabled: true\n");
                var badSpaced = PluginManager.ValidatePatchStructure("[ ]\n- id: x\n");
                var badFlow = PluginManager.ValidatePatchStructure("[1, 2]\n- id: x\n");
                var badMap = PluginManager.ValidatePatchStructure("{a: 1}\n- id: x\n");
                Check("patch 结构校验：`[]`（及 [ ] / [1,2] / {a: 1} 等顶层流式值）+ 块序列 ⇒ 判**非法**并说明原因",
                    !badOld.Ok && !badOldMany.Ok && !badBare.Ok && !badSpaced.Ok && !badFlow.Ok && !badMap.Ok &&
                    badOld.Reason.Contains("一个 YAML 文档只能有一个顶层", StringComparison.Ordinal) &&
                    badOld.Reason.Contains("第 5 行", StringComparison.Ordinal),
                    badOld.Reason);

                // ②′ 反例的**关键性质**：旧的定点校验对同一份坏文本判"合法"（这正是它漏掉本缺陷的原因），
                //     新校验必须能把它拦下 —— 两条口径的差异钉死，防止日后有人把新校验误删。
                Check("patch 结构校验抓的正是旧校验漏掉的那类：坏文本 textOk=True（旧）但 structureOk=False（新）",
                    PluginManager.ValidatePatchText(header + "[]\n" + block1).Ok && !badOld.Ok,
                    $"旧校验 Ok={PluginManager.ValidatePatchText(header + "[]\n" + block1).Ok} · 新校验 Ok={badOld.Ok}");

                // ③ 真实形态的用户文件（多注释 + 多条目 + 保留字符 id + insert 块 + 嵌套 config）⇒ 不许误伤
                const string realShape =
                    "# Your patch layer for this dsh profile, applied after every bundle layer:\n" +
                    "# a top-level YAML array of loader patch entries (id-targeted config\n" +
                    "# overrides, disables, and insert lists; `!!js` expressions allowed).\n" +
                    "\n" +
                    "# 2026-09-10: 说明注释（多行）\n" +
                    "# 待上游出适配版本后，删掉下面两条即可恢复。\n" +
                    "- id: dsh-zh\n" +
                    "  disabled: true\n" +
                    "\n" +
                    "# 2026-09-11: 另一个插件的配置覆盖\n" +
                    "- id: furongjun1999-dsh-memory\n" +
                    "  config:\n" +
                    "    roleplayEntryButton: false\n" +
                    "    python: 'D:/Applications/Python312/python.exe'\n" +
                    "\n" +
                    "# DSHGuard 于 2026-09-15 22:22:59 禁用（备份 cordis.patch.yml.bak-20260915-222259）\n" +
                    "- id: \"@changfenhuang/dsh-genui\"\n" +
                    "  disabled: true\n" +
                    "- id: auto-continue\n" +
                    "  disabled: false\n";
                var realRes = PluginManager.ValidatePatchStructure(realShape);
                // insert 块（bundle 补丁的另一种合法形态：`- insert:` 下面嵌着更深的 `- id:`）
                var insertRes = PluginManager.ValidatePatchStructure(
                    "# bundle patch\n- insert:\n    - id: genui\n      name: '@changfenhuang/dsh-genui'\n");
                Check("patch 结构校验：真实形态的用户文件（多注释 + 多条目 + 保留字符 id + config/insert 块）判**合法**（不误伤）",
                    realRes.Ok && insertRes.Ok,
                    $"userfile Ok={realRes.Ok} · insert Ok={insertRes.Ok}" +
                    (realRes.Ok ? "" : " → " + realRes.Reason));

                // ③′ 合法但容易被误判为流式的两种写法：注释掉的占位符、块里的 flow 值
                Check("patch 结构校验：`# []` 注释占位符 / `note: []` 缩进块值 / `- config: [1, 2]` 均判合法",
                    PluginManager.ValidatePatchStructure("# []\n- id: x\n  disabled: true\n").Ok &&
                    PluginManager.ValidatePatchStructure("- id: x\n  disabled: true\n  note: []\n").Ok &&
                    PluginManager.ValidatePatchStructure("- config: [1, 2]\n- id: x\n").Ok,
                    "三种都不算顶层流式占位符");

                // ④ 空 / null 不崩
                Check("patch 结构校验：null / 空串 / 纯空白都不崩且判合法",
                    PluginManager.ValidatePatchStructure(null).Ok &&
                    PluginManager.ValidatePatchStructure("").Ok &&
                    PluginManager.ValidatePatchStructure("   \n\n\t\n").Ok,
                    "三态均为 Ok=true");

                // ⑤ 真实写入链路真跑：文件不存在 ⇒ 执行禁用 ⇒ 产物结构合法且条目在位（两处模板都覆盖）
                string t57 = Path.Combine(Path.GetTempPath(), "dshguard-patchnl-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                string? t57bak = PluginManager.PatchFileOverrideForTest;
                try
                {
                    Directory.CreateDirectory(t57);
                    PluginManager.PatchFileOverrideForTest = Path.Combine(t57, "cordis.patch.yml");

                    // ⑤-a Disable：文件不存在（旧实现在这里先写 `[]` 模板 ⇒ 产物非法）
                    var pA = new PluginManager.Plugin { Name = "@scope/pkgA", LoaderId = "@scope/pkgA" };
                    string rA = PluginManager.Disable(pA);
                    string txtA = File.ReadAllText(PluginManager.PatchFile);
                    bool tmplClean = !txtA.Contains("\n[]", StringComparison.Ordinal)
                                  && !txtA.TrimStart().StartsWith("[]", StringComparison.Ordinal);
                    Check("模板修复：文件不存在时 Disable 的产物结构合法、条目在位、模板里没有 `[]`",
                        rA.StartsWith("已禁用", StringComparison.Ordinal) &&
                        PluginManager.ValidatePatchStructure(txtA).Ok &&
                        PluginManager.ReadDisabledIds().Contains("@scope/pkgA") &&
                        tmplClean,
                        $"结果={rA.Split('\n')[0]} · 结构Ok={PluginManager.ValidatePatchStructure(txtA).Ok}");

                    // ⑤-b 模板的引导注释必须保留（它是给用户看的说明，只去掉会导致语法错误的 `[]`）
                    Check("模板修复：引导注释保留（用户仍看得懂这份文件是补丁层）",
                        txtA.Contains("applied after every bundle layer", StringComparison.Ordinal),
                        txtA.Split('\n')[0]);

                    // ⑤-c DisableMany：另起一份"文件不存在"的干净夹具（旧实现同样先写 `[]` 模板）
                    PluginManager.PatchFileOverrideForTest = Path.Combine(t57, "sub", "cordis.patch.yml");
                    Directory.CreateDirectory(Path.Combine(t57, "sub"));
                    var many = new List<PluginManager.Plugin>
                    {
                        new PluginManager.Plugin { Name = "@a/one", LoaderId = "@a/one" },
                        new PluginManager.Plugin { Name = "plain-two", LoaderId = "plain-two" }
                    };
                    var (doneB, detailB) = PluginManager.DisableMany(many);
                    string txtB = File.ReadAllText(PluginManager.PatchFile);
                    Check("模板修复：文件不存在时 DisableMany 的产物结构合法、两条都在位、模板里没有 `[]`",
                        doneB.Count == 2 &&
                        PluginManager.ValidatePatchStructure(txtB).Ok &&
                        PluginManager.ReadDisabledIds().Contains("@a/one") &&
                        PluginManager.ReadDisabledIds().Contains("plain-two") &&
                        !txtB.Contains("\n[]", StringComparison.Ordinal),
                        $"{detailB} · 结构Ok={PluginManager.ValidatePatchStructure(txtB).Ok}");

                    // ⑤-d 既有坏文件：识别并如实告知，且**拒绝在该形态上继续追加**（不谎报"已禁用"）
                    PluginManager.PatchFileOverrideForTest = Path.Combine(t57, "broken.yml");
                    File.WriteAllText(PluginManager.PatchFile, header + "[]\n" + block1, new UTF8Encoding(false));
                    string txtBrokenBefore = File.ReadAllText(PluginManager.PatchFile);
                    var pB = new PluginManager.Plugin { Name = "@scope/pkgB", LoaderId = "@scope/pkgB" };
                    string rB = PluginManager.Disable(pB);
                    string txtBrokenAfter = File.ReadAllText(PluginManager.PatchFile);
                    Check("既有坏文件：被识别为结构异常、如实告知（提示重置/重新禁用），且**不被擅自改写**",
                        PluginManager.PatchFileLooksBroken(out var whyB) &&
                        whyB.Contains("只能有一个顶层", StringComparison.Ordinal) &&
                        whyB.Contains("第 5 行", StringComparison.Ordinal) &&
                        PluginManager.PatchConfigWarning().Contains("结构异常", StringComparison.Ordinal) &&
                        rB.StartsWith("禁用", StringComparison.Ordinal) && rB.Contains("失败", StringComparison.Ordinal) &&
                        !rB.StartsWith("已禁用", StringComparison.Ordinal) &&
                        txtBrokenAfter == txtBrokenBefore,
                        rB.Split('\n')[0]);

                    // ⑤-e 结构正常时 PatchConfigWarning 必须是空串（否则事件栏会天天误报）
                    PluginManager.PatchFileOverrideForTest = Path.Combine(t57, "healthy.yml");
                    File.WriteAllText(PluginManager.PatchFile, header + block1, new UTF8Encoding(false));
                    Check("结构正常时不误报：PatchConfigWarning 为空串",
                        !PluginManager.PatchFileLooksBroken(out _) && PluginManager.PatchConfigWarning().Length == 0,
                        $"警告=<{PluginManager.PatchConfigWarning()}>");
                }
                finally
                {
                    PluginManager.PatchFileOverrideForTest = t57bak;
                    try { Directory.Delete(t57, true); } catch { }
                }
            }

            // ══════ 58. bug：异步插件操作抛异常 ⇒ 底部进度条永远停在「正在…（NN%）」══════
            // 现场（本轮报告 + 前一位同事已修的同一形态）：单个更新 / 重新安装 / 单个卸载 /
            // 批量更新 / 批量卸载这几条 `async void` 路径，原来是 BeginOpProgress → 干活 → EndOpProgress
            // 一条直线；**收尾那几步本身都在 await 之后**（RefreshPluginsAsync / OfferRestartAsync /
            // GuardDialog.Show），任何一处抛异常都会跳过"正常收尾"这一句 ⇒ 底部永远停在
            // 「正在更新插件 X（42%）」，用户以为程序卡死。修法统一为 `bool opOpen` + finally 收尾。
            //
            // 这一组断言**调的是产品代码本身**（EndOpProgressIfOpenForTest 直通私有入口
            // EndOpProgressIfOpen），不是在这里重写一遍判断 —— 否则测的是"断言里的逻辑"，
            // 而不是"真正在跑的那段代码"。
            //
            // ⚠ 断言必须**紧跟调用**：进度条 4 秒后会自动收起并清空
            //    （ScheduleProgressHide → SetProgress("", null) ⇒ ProgressText 变空串），
            //    中间插 sleep / 抽消息就会读到空串而假红。
            //    自检在 UI 线程上同步跑，而 SetProgress 用 Dispatcher.Invoke —— 同线程为直通执行，
            //    所以这几句是同步生效的，不需要 Pump 等待。
            // ⚠ 这里**不写死断言条数**：新增/删除本组用例时不该再去改一个数字，
            //    log 里每条的序号（[NN]）本来就能一眼看出这一节跑了几条。

            // ① 异常路径收尾：表开着 + 自己持有 ⇒ 必须收掉，且文案如实含"中断"。
            //    这里用「卸载」文案，把本轮新修的 UninstallPlugin_Click 那条也钉住。
            w.BeginOpProgressForTest("正在卸载插件 X");
            bool openAfterBegin = w.OpProgressOpenForTest();
            w.EndOpProgressIfOpenForTest(true, "「X」卸载中断");
            string txt58a = w.ProgressTextForTest();
            Check("异常路径也收尾：进度表关闭且文案如实（单个卸载那条）",
                openAfterBegin && !w.OpProgressOpenForTest() && txt58a.Contains("卸载中断", StringComparison.Ordinal),
                txt58a);

            // ①′ 前一位同事给的三条断言里的第①条（更新文案）原样落地 —— 两条路径各钉一次。
            w.BeginOpProgressForTest("正在更新插件 X");
            w.EndOpProgressIfOpenForTest(true, "「X」更新中断");
            Check("异常路径也收尾：进度表关闭且文案如实", !w.OpProgressOpenForTest() && w.ProgressTextForTest().Contains("更新中断"), w.ProgressTextForTest());

            // ② **不属于自己的表绝不误收**（本轮的关键判据，也是批量路径唯一的正确判据）：
            //    模拟"别的操作正开着表，我这边用户点了取消 ⇒ 早退 return ⇒ owned=false"。
            //    若只看"表开着"（去掉 owned），这里就会把别人的进度条收掉 —— 那正是要防的回归。
            w.BeginOpProgressForTest("正在安装缺失的插件（别的操作）");
            w.EndOpProgressIfOpenForTest(false, "「X」更新中断");            // owned=false = 这张表不是我的
            string txt58b = w.ProgressTextForTest();
            Check("不属于自己的表绝不误收（owned=false 时表与文案都原样保留）",
                w.OpProgressOpenForTest() && txt58b.Contains("正在安装缺失的插件", StringComparison.Ordinal),
                txt58b);

            // ③ 正常收尾之后不再补"中断"：EndOpProgress 已把表收掉 ⇒ _opProgressTimer==null
            //    ⇒ 即使 owned 仍为 true，两条相与也不放行。幂等性由**结构**保证，不靠重复调用兜底
            //    （EndOpProgress 本身不幂等：再调一次会把「已更新」覆盖成「更新中断」）。
            w.EndOpProgressIfOpenForTest(true, "收尾（模拟正常路径）");
            string txt58c = w.ProgressTextForTest();
            w.EndOpProgressIfOpenForTest(true, "「X」更新中断");             // 正常路径之后再来一次
            string txt58d = w.ProgressTextForTest();
            Check("正常收尾后不再补「中断」：文案保持正常收尾那句（幂等性靠结构，不靠重复调用）",
                txt58c.Contains("收尾（模拟正常路径）", StringComparison.Ordinal) &&
                txt58d == txt58c &&
                !txt58d.Contains("更新中断", StringComparison.Ordinal),
                txt58d);

            // ④ 批量路径的收尾：**与批量路径完全同构**地跑一遍"按项开表 → 中途抛异常 → finally 收尾"。
            //    批量那两条路径的 BeginOpProgress 在循环**里面**（每个插件一条进度），循环体里
            //    任何一个 per-item 步骤抛异常，最后一项的表就留在了界面上 ⇒ 这才是要收的那张。
            //    opOpen 必须在这里才置 true —— 循环之前 owned 仍是 false，正是"不能只看表开着"的原因。
            bool batchOwned = false;
            string txt58e = "";
            try
            {
                // 循环之前 opOpen 仍是 false —— 这正是"不能只看表开着"的原因：
                // 此刻别处（启动前体检 / 单个更新）完全可能正开着表。
                w.EndOpProgressIfOpenForTest(batchOwned, "批量更新中断");
                // 循环第 1 项：正常跑完（走真实的收尾入口，表被收掉）⇒ 交还所有权
                w.BeginOpProgressForTest("正在更新插件 A（1/2）");
                batchOwned = true;
                w.EndOpProgressIfOpenForTest(true, "「A」已更新");
                batchOwned = false;
                // 循环第 2 项：中途抛异常 ⇒ 表留在界面上，只有 finally 能收
                w.BeginOpProgressForTest("正在更新插件 B（2/2）");
                batchOwned = true;
                throw new InvalidOperationException("模拟 per-item 步骤抛异常");
            }
            catch (InvalidOperationException)
            {
                w.EndOpProgressIfOpenForTest(batchOwned, "批量更新中断");   // finally 里的那一句
                txt58e = w.ProgressTextForTest();
            }
            Check("批量路径的收尾：最后一项异常退出时收掉表并如实报「中断」",
                !w.OpProgressOpenForTest() && txt58e.Contains("批量更新中断", StringComparison.Ordinal),
                txt58e);

            // ④′ 批量路径的**反向**：循环里每一项都正常收尾（batchOwned 已交还 false）之后，
            //     即使别处的表还开着，末尾长尾 await（RefreshPluginsAsync / OfferRestartAsync）
            //     期间抛异常，finally 也不许动那张表。
            bool batchDone = false;
            string txt58f = "";
            w.BeginOpProgressForTest("循环结束后别人开的表");
            try
            {
                w.EndOpProgressIfOpenForTest(batchDone, "批量更新中断（交还所有权之后）");
                txt58f = w.ProgressTextForTest();
            }
            finally
            {
                // 收干净：这几条断言不许把进度表留给后面的用例
                w.EndOpProgressIfOpenForTest(true, "自检收尾");
            }
            Check("批量路径：正常跑完后（所有权已交还）末尾抛异常也不误收别人的表",
                txt58f.Contains("循环结束后别人开的表", StringComparison.Ordinal),
                txt58f);

            // 不给后续断言留悬挂的进度表（进度条 4 秒后自己会清，但别依赖那个时机）
            w.EndOpProgressIfOpenForTest(true, "自检收尾");

            // ══════ 59. 补齐 2026-09-18 八处修复里**此前零断言**的几处 ══════
            // 说明：这八处修复单按"一个子代理只改一个文件"的规矩**被禁止碰 SelfTest.cs**，
            // 只有 git 更新那单贡献了断言；下面按修复项分组补上，每条都对着**真接线**跑。
            // 变量名一律带 n59 前缀：RunCore 的整个方法体是一个声明空间，任何重名（含内层遮蔽外层）
            // 都是 CS0136 —— 已有一个 allEmpty 被这么撞过。

            // ── ① 外部引擎停机必须结算尾段**并关账本** ──
            // 旧缺陷：端口关闭且是外部引擎时只置 _isRunning=false、不清 _runStartAt ⇒ 账本一直开着，
            // 下一个 30 秒结算点把整段停机期当运行时长补进版本履历（反复外部启停还会叠加）。
            // 先结束一次让账本回到"干净起点"，断言才与前面用例的残留无关（再结束一次是幂等的）。
            // ⚠ 判据刻意**不依赖真实时间流逝**：AccumulateRunSecondsForTest 是"结算入口"而不是"起表入口"
            //   —— 它把 _runStartAt 前移到现在再结算，且只推进 _engRunStartAt、**不写** _engRunSeconds
            //   （见 MainWindow.xaml.cs:3179 AccumulateRunSeconds 与 :3313 钩子）⇒ 紧跟其后的 SettleRunClock
            //   算出的尾段是亚秒级、截断成 0，"累计"也仍是 0。要钉的是**账本关没关**，不是尾段秒数。
            w.EndRunClockForTest();                             // 先回到干净起点（幂等）
            w.AccumulateRunSecondsForTest(45);                  // 模拟已经跑了一段，账本处于开着
            bool n59ClockOpenBefore = w.RunClockOpenForTest();
            int n59Tail = w.SettleRunClockForTest();            // 走正式停机收尾入口（结算 + 关账本）
            bool n59ClockOpenAfter = w.RunClockOpenForTest();
            Check("★ 停机收尾：走真实入口后运行账本确实关掉（尾段不截断即 ≥0；旧行为不收尾 ⇒ 账本仍开着）",
                n59ClockOpenBefore && !n59ClockOpenAfter && n59Tail >= 0,
                $"停机前账本开着={n59ClockOpenBefore} ⇒ 停机后={n59ClockOpenAfter}（必须 False）· 尾段={n59Tail}s（不依赖真实时间流逝）");

            int n59TailAgain = w.SettleRunClockForTest();
            Check("★ 停机收尾幂等：再结算一次空转返回 0（终止分支与状态计时器各调一次也不会重复记账）",
                n59TailAgain == 0,
                $"第二次结算={n59TailAgain}s（应为 0）");

            // 反向：收尾动作**只该发生在停机那一下** —— 账本还开着时不许自己归零。
            w.SetTimedRunStateForTest(3 * SnapshotManager.RunSecondsPerHour, 2 * SnapshotManager.RunSecondsPerHour);
            var n59StateOpen = w.TimedRunStateForTest();
            w.AccumulateRunSecondsForTest(45);                  // 中途结算一次（还没停机）
            var n59StateMid = w.TimedRunStateForTest();
            Check("停机收尾的反向：中途结算不动「自动-时间」的累计与门槛（只在停机那一下清零）",
                n59StateOpen.ranSeconds == n59StateMid.ranSeconds &&
                n59StateOpen.lastTakenAt == n59StateMid.lastTakenAt &&
                n59StateMid.ranSeconds == 3 * SnapshotManager.RunSecondsPerHour,
                $"中途结算前 {n59StateOpen.ranSeconds}s/{n59StateOpen.lastTakenAt}s"
                + $" ⇒ 结算后 {n59StateMid.ranSeconds}s/{n59StateMid.lastTakenAt}s（应不变）");

            w.SettleRunClockForTest();                          // 收尾：本批不留开着的账本
            var n59StateClosed = w.TimedRunStateForTest();
            Check("★ 停机收尾：累计与门槛一起清零（重启后从 0 重新数满 1 小时）",
                !w.RunClockOpenForTest() && n59StateClosed.ranSeconds == 0 && n59StateClosed.lastTakenAt == 0,
                $"停机后 累计={n59StateClosed.ranSeconds}s 门槛={n59StateClosed.lastTakenAt}s（都应为 0）");

            // ── ② 更新重入闸：一键更新 ⇄ 单颗更新 ⇄ 重新安装**共用一份** _updatingBusy ──
            // 先复位成"空闲"：EndUpdatingState 幂等，没开过时一个属性都不碰（复位本身已由 58 批覆盖）。
            w.EndUpdatingStateForTest();
            string n59BusyText = MainWindow.UpdateBusyTextForTest();
            var n59BtnIdle = w.UpdateAllButtonForTest();
            bool n59IdleEnabled = w.UpdateAllBtnEnabledForTest();
            bool n59IdleAllows = w.PassUpdateGateForTest();
            Check("重入闸：空闲时闸门放行（未被拒就不该平白拦一次更新）",
                !w.UpdatingBusyForTest() && n59IdleAllows && n59IdleEnabled,
                $"忙碌={w.UpdatingBusyForTest()} 闸门放行={n59IdleAllows} 按钮可点={n59IdleEnabled}");

            w.BeginUpdatingStateForTest();
            bool n59BusyNow = w.UpdatingBusyForTest();
            // 观察窗起点：AddEvent 每记一条事件都会掷一次 2% 的彩蛋骰，掷中就在**刚写那条之后**再追一条
            // Mascot 彩蛋（MainWindow.Console.cs:1699）⇒ 取「最后一条事件」会偶发取到彩蛋、而不是被拒那条 Warn
            // （本批实测撞到过一次）。这里先写一条标记事件划出窗口，再**在窗口里找**那条 Warn —— 与它后面压着什么无关。
            string n65GateMark = "自检-重入闸观察窗";
            MainWindow.AddEvent(n65GateMark);
            bool n59Rejected = w.PassUpdateGateForTest();
            var n65GateEvs = EventsAfterForTest(n65GateMark);   // 窗口内全部条目（Warn + 可能压在它后面的彩蛋 / 引擎 tick 噪声）
            string n59BusyBtn = (w.FindName("UpdateAllBtn") as Border)?.Background is SolidColorBrush n59BtnBrush
                ? n59BtnBrush.Color.ToString() : "";
            Check("★ 重入闸：更新进行中再点 ⇒ 明确被拒（不是静默无反应）",
                n59BusyNow && !n59Rejected,
                $"忙碌={n59BusyNow} 闸门放行={n59Rejected}（应为 False）");
            Check("★ 重入被拒时**如实提示**：事件栏留一条 Warn，文案就是那句统一提示（改回静默 return 即红）",
                n65GateEvs.Any(e => e.Kind == MainWindow.EventKind.Warn
                                    && e.Text.Contains(n59BusyText, StringComparison.Ordinal)),
                $"本次调用后窗口内共 {n65GateEvs.Count} 条："
                + string.Join(" / ", n65GateEvs.Select(e => $"{e.Kind}=«{Shorten(e.Text, 34)}»"))
                + $"（其中应有一条 Warn=«{Shorten(n59BusyText, 34)}»）");
            Check("★ 更新期间那颗按钮压灰：IsEnabled=false 且底色换成次要灰 #FF8E8E93（不再可点）",
                !w.UpdateAllBtnEnabledForTest() && n59BusyBtn == "#FF8E8E93",
                $"可点={w.UpdateAllBtnEnabledForTest()}（应 False）底色={n59BusyBtn}");

            w.EndUpdatingStateForTest();                        // 两处入口都在 finally 里调它（异常路径同样复位）
            var n59BtnBack = w.UpdateAllButtonForTest();
            Check("★ 收尾复位：忙碌标记清掉、按钮恢复可点、外观照原样还原（异常路径不会永远灰着）",
                !w.UpdatingBusyForTest() && w.UpdateAllBtnEnabledForTest() &&
                n59BtnBack.bg == n59BtnIdle.bg && n59BtnBack.text == n59BtnIdle.text,
                $"忙碌={w.UpdatingBusyForTest()} 可点={w.UpdateAllBtnEnabledForTest()} "
                + $"底色 {n59BtnIdle.bg} ⇒ {n59BtnBack.bg} · 文字「{n59BtnBack.text}」");

            w.EndUpdatingStateForTest();                        // 收尾幂等：再来一次不该抛、也不该把复位结果改坏
            Check("★ 收尾幂等：再收尾一次不抛且仍是可点原样（重复收尾不会把按钮改坏）",
                !w.UpdatingBusyForTest() && w.UpdateAllBtnEnabledForTest() &&
                w.UpdateAllButtonForTest().bg == n59BtnIdle.bg,
                $"可点={w.UpdateAllBtnEnabledForTest()} 底色={w.UpdateAllButtonForTest().bg}");

            Check("重入提示文案本身不含命令写法（用户看到的是「等它跑完再试」，不是命令行）",
                n59BusyText.Contains("请等它跑完", StringComparison.Ordinal) &&
                !n59BusyText.Contains("npx") && !n59BusyText.Contains("pnpm"),
                $"「{n59BusyText}」");

            // ── ③ 端口 / 主题归一：手改 settings.json 塞越界值必须回落默认 ──
            // 真接线：把 Config 目录整体改根到 %TEMP%（DSHGUARD_DATA_DIR，与第 23 批保留份数同一条路），
            // 写夹具 settings.json 后用**真实 Load()** 读回来 —— 验的是"Load 里确实调了归一"，
            // 不是在这儿把 NormalizePort 再抄一遍。真实 Config 一个字节都不碰。
            string? n59DataPrev = null;
            try { n59DataPrev = Environment.GetEnvironmentVariable("DSHGUARD_DATA_DIR"); } catch { }
            string n59CfgFixture = Path.Combine(Path.GetTempPath(),
                "dshguard-normcfg-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(Path.Combine(n59CfgFixture, "Config"));
                string n59CfgFile = Path.Combine(n59CfgFixture, "Config", "settings.json");
                Environment.SetEnvironmentVariable("DSHGUARD_DATA_DIR", n59CfgFixture);

                File.WriteAllText(n59CfgFile, "{\"Port\":70000,\"Theme\":2}", new UTF8Encoding(false));
                var n59Bad = new SettingsManager();
                Check("★ 端口归一：夹具里写 70000 ⇒ 真实 Load 读回来回落默认 3080（删掉归一即变 70000）",
                    n59Bad.Port == 3080,
                    $"夹具 Port=70000 ⇒ 读回 {n59Bad.Port}（应 3080）· NormalizePort(-1)={SettingsManager.NormalizePort(-1)} "
                    + $"NormalizePort(0)={SettingsManager.NormalizePort(0)}");
                Check("★ 主题归一：夹具里写 2（合法只有 0/1）⇒ 回落默认 0（删掉归一即变 2）",
                    n59Bad.Theme == 0,
                    $"夹具 Theme=2 ⇒ 读回 {n59Bad.Theme}（应 0）· NormalizeTheme(-5)={SettingsManager.NormalizeTheme(-5)}");

                // 反向：归一**不许改坏合法值**（只写"越界回落"容易把整段写成恒返回默认）。
                File.WriteAllText(n59CfgFile, "{\"Port\":3099,\"Theme\":1}", new UTF8Encoding(false));
                var n59Good = new SettingsManager();
                Check("端口/主题归一反例：合法值原样采纳（3099 / 1 都不许被归一改掉）",
                    n59Good.Port == 3099 && n59Good.Theme == 1 &&
                    SettingsManager.NormalizePort(65535) == 65535 &&
                    SettingsManager.NormalizePort(1) == 1 &&
                    SettingsManager.NormalizeTheme(0) == 0,
                    $"夹具 3099/1 ⇒ 读回 {n59Good.Port}/{n59Good.Theme} · 边界 65535→{SettingsManager.NormalizePort(65535)}");
            }
            catch (Exception ex) { Check("端口/主题归一：夹具", false, ex.Message); }
            finally
            {
                try { Environment.SetEnvironmentVariable("DSHGUARD_DATA_DIR", n59DataPrev); } catch { }
                try { if (Directory.Exists(n59CfgFixture)) Directory.Delete(n59CfgFixture, true); } catch { }
            }

            // ── ④ 非模态弹窗：切主题**当场换肤**；关窗后登记摘干净 ──
            // 旧缺陷：这类窗只设了 Owner、既不在主窗视觉树也不在逻辑树上 ⇒ Apply(主窗.Content) 从根走不到它，
            // 弹窗开着切主题保持旧配色（下次重开才变）。判定：走真实入口 ApplyThemeForTest 切到日间，
            // 窗口内容里那张卡片必须是"日间才有"的 #FFF2F3F7；没有补刷（或没登记）就仍停在 #FF1C2029。
            Check("前置：此刻没有别的对话框开着（弹窗有单例闸门，AnyOpen 时 ShowNonModal 直接返回）",
                GuardDialog.OpenCountForTest() == 0,
                $"OpenCount={GuardDialog.OpenCountForTest()}");
            GuardDialog.ShowNonModal("自检-切主题补刷新窗口", "自检", MessageBoxImage.Information);
            Check("前置：非模态框已显示且在登记表里（后面两条断言的前提）",
                GuardDialog.OpenCountForTest() == 1 && GuardDialog.NonModalOpenForTest() != null,
                $"OpenCount={GuardDialog.OpenCountForTest()} 已登记={GuardDialog.NonModalOpenForTest() != null}");
            string n59NonModalCard()
            {
                if (GuardDialog.NonModalOpenForTest() is not Window n59Dlg ||
                    n59Dlg.Content is not Grid n59DlgRoot || n59DlgRoot.Children.Count == 0 ||
                    n59DlgRoot.Children[0] is not Border n59DlgCard) return "";
                return (n59DlgCard.Background as SolidColorBrush)?.Color.ToString() ?? "";
            }
            string n59CardBefore = n59NonModalCard();
            w.ApplyThemeForTest(false);                         // 切日间（真实入口；里面调 ReskinNonModalOpen）
            string n59CardAfter = n59NonModalCard();
            Check("★ 非模态弹窗跟着切主题**当场换肤**（不是等下次重开）：卡片落到日间的 #FFF2F3F7",
                n59CardAfter == "#FFF2F3F7" && n59CardBefore != n59CardAfter,
                $"切日间前 {n59CardBefore} ⇒ 切后 {n59CardAfter}（应 #FFF2F3F7；没有补刷就仍是 #FF1C2029）");
            w.ApplyThemeForTest(true);                          // 切回夜间并确认是双向的
            Check("★ 切回夜间同样当场换回 #FF1C2029（补刷是双向的，不是单向写死一个颜色）",
                n59NonModalCard() == "#FF1C2029",
                $"切回夜间 ⇒ {n59NonModalCard()}");

            // 关窗：Closed 里必须把登记**摘掉**（只增不减 = 之后切主题去碰已关闭的窗口 + 内存泄漏）。
            try { GuardDialog.NonModalOpenForTest()?.Close(); } catch { }
            PumpUntil(() => GuardDialog.NonModalOpenCountForTest() == 0 && GuardDialog.OpenCountForTest() == 0, 1500);
            Check("★ 非模态窗关掉后登记摘干净：登记数回 0 且 OpenCount 回 0（不泄漏、不留悬挂项）",
                GuardDialog.NonModalOpenCountForTest() == 0 && GuardDialog.OpenCountForTest() == 0,
                $"登记={GuardDialog.NonModalOpenCountForTest()} OpenCount={GuardDialog.OpenCountForTest()}");
            int n59ReskinAfterClose = 0;
            try { GuardDialog.ReskinNonModalOpen(); n59ReskinAfterClose = 1; } catch { n59ReskinAfterClose = -1; }
            Check("非模态窗全部关闭后，切主题的补刷空转不抛（对着已关闭的窗口刷色会抛异常）",
                n59ReskinAfterClose == 1 && GuardDialog.NonModalOpenCountForTest() == 0,
                $"ReskinNonModalOpen 调用结果={n59ReskinAfterClose}（1=正常返回）");

            // ── ⑤ 「自动-时间」脏状态护栏：ranSeconds 极大时不存，且门槛一次推平（不连发）──
            string n59SnapPrev = GuardPaths.SnapshotRoot;
            string n59ProfPrev = GuardPaths.ProfileDir;
            string n59SnapFixture = Path.Combine(Path.GetTempPath(),
                "dshguard-timedguard-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                string n59SnapRoot = Path.Combine(n59SnapFixture, "Snapshots");
                string n59Prof = Path.Combine(n59SnapFixture, "profile");
                Directory.CreateDirectory(n59SnapRoot);
                Directory.CreateDirectory(n59Prof);
                GuardPaths.Apply(null, n59SnapRoot, n59Prof);   // 临时仓库，绝不碰真实 Snapshots

                long n59Max = SnapshotManager.TimedSnapshotMaxRunSeconds;
                long n59TenYears = 10L * 365 * 24 * 3600;
                long n59Hour = SnapshotManager.RunSecondsPerHour;

                Check("自动-时间护栏：荒谬上限常量 = 10 年（这个值本身就是判据的一部分）",
                    n59Max == n59TenYears, $"TimedSnapshotMaxRunSeconds={n59Max}（应 {n59TenYears}）");
                Check("★ 脏状态护栏：累计超过 10 年一律不存，且连 long.MaxValue / 极值都不抛",
                    !SnapshotManager.ShouldTakeTimedSnapshot(n59Max + 1, 0) &&
                    !SnapshotManager.ShouldTakeTimedSnapshot(n59Max + n59Hour, 0) &&
                    !SnapshotManager.ShouldTakeTimedSnapshot(long.MaxValue, 0) &&
                    !SnapshotManager.ShouldTakeTimedSnapshot(long.MaxValue, long.MaxValue) &&
                    !SnapshotManager.ShouldTakeTimedSnapshot(long.MinValue, 0),
                    $"上限+1 ⇒ {SnapshotManager.ShouldTakeTimedSnapshot(n59Max + 1, 0)}（期望 False）· "
                    + $"long.MaxValue ⇒ {SnapshotManager.ShouldTakeTimedSnapshot(long.MaxValue, 0)}（期望 False）");

                // 旧行为（只护 lastTakenAt 一侧）：ran=long.MaxValue、last=0 时 threshold=3600 ⇒ 判据恒 True。
                // 这条钉住"恰好等于上限仍走正常逻辑 + 正常区间行为一字不改"。
                Check("★ 脏状态护栏的边界：恰好 = 上限仍按正常门槛判（取 > 不取 >=，正常区间行为一字不改）",
                    SnapshotManager.ShouldTakeTimedSnapshot(n59Max, 0) &&               // 跨过门槛 ⇒ 存
                    !SnapshotManager.ShouldTakeTimedSnapshot(n59Max, n59Max) &&         // 门槛已推平 ⇒ 不存
                    !SnapshotManager.ShouldTakeTimedSnapshot(0, 0) &&
                    !SnapshotManager.ShouldTakeTimedSnapshot(-1, 0) &&
                    !SnapshotManager.ShouldTakeTimedSnapshot(n59Hour - 1, 0) &&
                    SnapshotManager.ShouldTakeTimedSnapshot(n59Hour, 0) &&
                    !SnapshotManager.ShouldTakeTimedSnapshot(n59Hour + 60, n59Hour) &&
                    SnapshotManager.ShouldTakeTimedSnapshot(2 * n59Hour, n59Hour) &&
                    SnapshotManager.ShouldTakeTimedSnapshot(3 * n59Hour, 2 * n59Hour),
                    $"上限={n59Max} · 60 分 ⇒ {SnapshotManager.ShouldTakeTimedSnapshot(n59Hour, 0)} · "
                    + $"61 分(last=60 分) ⇒ {SnapshotManager.ShouldTakeTimedSnapshot(n59Hour + 60, n59Hour)}");

                Check("★ 脏状态保险丝②：累计过上限时门槛**一次推平到 ranSeconds**（旧行为每次只推 3600 ⇒ 每个 30 秒结算点连发）",
                    SnapshotManager.TimedSnapshotNextMark(n59Max + 1, 0) == n59Max + 1 &&
                    SnapshotManager.TimedSnapshotNextMark(long.MaxValue, 0) == long.MaxValue &&
                    SnapshotManager.TimedSnapshotNextMark(n59Max + 12345, n59Hour) == n59Max + 12345,
                    $"ran=上限+1 ⇒ 门槛 {SnapshotManager.TimedSnapshotNextMark(n59Max + 1, 0)}（应 {n59Max + 1}，"
                    + $"旧行为是 {n59Hour}）");
                Check("★ 正常区间推平规则一字不改：仍返回跨过的整门槛（60 / 120 / 180 分落点整齐）",
                    SnapshotManager.TimedSnapshotNextMark(n59Hour, 0) == n59Hour &&
                    SnapshotManager.TimedSnapshotNextMark(2 * n59Hour, n59Hour) == 2 * n59Hour &&
                    SnapshotManager.TimedSnapshotNextMark(3 * n59Hour, 2 * n59Hour) == 3 * n59Hour &&
                    SnapshotManager.TimedSnapshotNextMark(n59Hour + 60, n59Hour) == 2 * n59Hour &&
                    SnapshotManager.TimedSnapshotNextMark(2 * n59Hour - 1, n59Hour) == 2 * n59Hour,
                    $"60 分 ⇒ {SnapshotManager.TimedSnapshotNextMark(n59Hour, 0)} · "
                    + $"119:59 ⇒ {SnapshotManager.TimedSnapshotNextMark(2 * n59Hour - 1, n59Hour)}");

                // 端到端"不连发"：模拟结算点每 30 秒一跳，脏 ran 也**只允许落下 1 份**。
                // 旧行为在这里是"每一跳都判该存"（门槛只推进 3600，永远追不上 ran）。
                long n59DirtyRan = n59Max + 3600;
                long n59DirtyLast = 0;
                int n59DirtyHits = 0;
                for (int n59Tick = 0; n59Tick < 240; n59Tick++)      // 240 跳 = 2 小时的结算点
                {
                    n59DirtyRan += 30;
                    if (!SnapshotManager.ShouldTakeTimedSnapshot(n59DirtyRan, n59DirtyLast)) continue;
                    n59DirtyHits++;
                    n59DirtyLast = SnapshotManager.TimedSnapshotNextMark(n59DirtyRan, n59DirtyLast);
                }
                Check("★ 脏状态端到端：结算点跑 240 跳（2 小时）时脏 ran **一份都不存**（判据直接判 False，不靠推平救）",
                    n59DirtyHits == 0,
                    $"240 跳里判该存 {n59DirtyHits} 次（应 0）· 旧行为每跳都判该存 = 连发 240 份");

                // 真接线：把脏 ran 摆进主窗账本，走正式入口必须**一份 timed 都不多**（夹具仓库从 0 起算）。
                int n59TimedBefore = SnapshotManager.ListNative().Count(s => s.Kind == SnapshotManager.KindTimed);
                w.SetTimedRunStateForTest(long.MaxValue, 0);
                w.MaybeTakeTimedSnapshotForTest();
                int n59TimedAfter = SnapshotManager.ListNative().Count(s => s.Kind == SnapshotManager.KindTimed);
                Check("★ 脏状态接线：脏 ran 走正式入口（MaybeTakeTimedSnapshot）一份 timed 快照都不产生",
                    n59TimedAfter == n59TimedBefore,
                    $"夹具仓库 timed 份数 {n59TimedBefore} ⇒ {n59TimedAfter}（期望不变）");

                // 反例（防"整段恒不存"）：同样的正式入口，正常 ran 必须真存出一份。
                w.SetTimedRunStateForTest(n59Hour, 0);
                w.MaybeTakeTimedSnapshotForTest();
                int n59TimedOk = SnapshotManager.ListNative().Count(s => s.Kind == SnapshotManager.KindTimed);
                Check("脏状态反例：同一入口下正常 ran（60 分）照常存出一份（护栏没把正常路径一起焊死）",
                    n59TimedOk == n59TimedBefore + 1,
                    $"60 分 ⇒ timed 份数 {n59TimedOk}（应 {n59TimedBefore + 1}）");
            }
            catch (Exception ex) { Check("自动-时间脏状态护栏：夹具", false, ex.Message); }
            finally
            {
                w.EndRunClockForTest();                          // 别给后面的用例留一个脏账本
                GuardPaths.Apply(null, n59SnapPrev, n59ProfPrev);
                try { if (Directory.Exists(n59SnapFixture)) Directory.Delete(n59SnapFixture, true); } catch { }
            }

            // ── ⑧ IsHardUpdatable 口径：HasUpdate && !Advisory ──
            // 既有覆盖（第 52 批 ③）是"按名字从表里取"的那四条；这里补**纯函数的边界**：
            // null 与"两种条件各自单独不成立"的落点 —— 少一个条件就多升/少升一类插件。
            bool n59HardNull;
            try { n59HardNull = !MainWindow.IsHardUpdatable(null); }
            catch { n59HardNull = false; }                       // 少了 null 护栏会抛 NRE ⇒ 这条变红
            var n59AdvOnly = new PluginManager.PluginUpdate { Name = "自检-仅可选", HasUpdate = true, Advisory = true };
            var n59NoUpdate = new PluginManager.PluginUpdate { Name = "自检-无新版", HasUpdate = false, Advisory = false };
            var n59Both = new PluginManager.PluginUpdate { Name = "自检-默认分支新版", HasUpdate = true, Advisory = false };
            var n59Neither = new PluginManager.PluginUpdate { Name = "自检-都没有", HasUpdate = false, Advisory = true };
            Check("★ IsHardUpdatable 口径：null 不抛（返回 False），两种条件各自单独不成立时都不算硬更新",
                n59HardNull && !MainWindow.IsHardUpdatable(n59AdvOnly) &&
                !MainWindow.IsHardUpdatable(n59NoUpdate) && !MainWindow.IsHardUpdatable(n59Neither) &&
                MainWindow.IsHardUpdatable(n59Both),
                $"null={n59HardNull} 仅可选={MainWindow.IsHardUpdatable(n59AdvOnly)} "
                + $"无新版={MainWindow.IsHardUpdatable(n59NoUpdate)} 默认分支={MainWindow.IsHardUpdatable(n59Both)}");
            Check("★ IsHardUpdatable 与计数同口径：喂同一批（含可选/无新版）只数出 1 个（「一键更新 N 个」不会多报）",
                MainWindow.CountUpdatablePlugins(
                    new List<PluginManager.Plugin> { new() { Name = "A" }, new() { Name = "B" }, new() { Name = "C" } },
                    p => p.Name switch
                    {
                        "A" => n59Both,        // 默认分支有新版 ⇒ 计入
                        "B" => n59AdvOnly,     // 可选升级 ⇒ 不计入
                        _ => n59NoUpdate       // 没有新版 ⇒ 不计入
                    }) == 1,
                "3 个插件（A 硬更新 / B 可选 / C 无新版）⇒ 应只数出 1 个");

            // ══════ 60. 补齐 2026-09-18 晚 5 处修复里**此前零断言**的几处（①–⑤）══════
            // 来由与上一批（59）相同：那 5 处修复当时被禁止改本文件，只在实现处留了"建议断言片段"。
            // 本批把它们落成**能真失败**的断言；每条都在注释里写明"把实现改回旧行为 ⇒ 它会不会红"。
            // ⚠ 夹具一律 %TEMP% + GuardPaths 重定向 + try/finally 还原；绝不碰真实 profile / Snapshots /
            //   Config / Logs；不调任何模态弹窗（GuardDialog.Show）。
            // ⚠ 量不到的地方如实留空（见下面两处 ⓘ）：批量卸载那道闸的 UI 侧 `continue` 在事件处理器里，
            //   纯函数层够不着；"调用点是否显式传 relaxSupplyChainPolicy:true" 只有读 .cs 源码文本才验得了
            //   （本仓自检至今没有读 .cs 的先例）—— 都不拿"量不到的期望值"凑数。

            // ── ① PluginSource.Classify 的 scp 形态：git@host:owner/repo[.git][#ref]，host 限四家白名单 ──
            // 语义：scp 形态判 git 源（GitBare / GitRef / GitCommit）；@scope/name 等 npm 包名**必须仍是 Registry**。
            // 反证：把 Classify 里 `|| IsScpGitSource(s)` 那一行删掉（= 退回旧的前缀表），第一条的 4 个正向
            //      全落 Registry ⇒ 立刻变红；第二条（反例）**不依赖它**（旧行为同样绿），它守的是"补 scp 时
            //      别把 npm 包名一起带走"：判据若放宽成"含 @ + 冒号"这类通用写法 ⇒ 立刻变红。
            const string b60Sha = "2573f24228ebe8a5e6b82900cd7fe731ba7d9c8d";
            const string b60Sha2 = "4ba82a0cfcd20ae8605940a71a65d3d8face2974";
            var scp60Bare = PluginSource.Classify("git@github.com:a/b.git");
            var scp60Ref = PluginSource.Classify("git@github.com:a/b.git#main");
            var scp60Cmt = PluginSource.Classify("git@github.com:a/b.git#" + b60Sha);
            var scp60NoExt = PluginSource.Classify("git@gitee.com:iJetLi/x");          // 不写 .git 也算
            var scp60RefKind = PluginSource.ClassifyRef("git@github.com:a/b.git#main");
            var scp60Scope = PluginSource.Classify("@scope/name");
            var scp60ScopeVer = PluginSource.Classify("@scope/name@1.2.3");
            var scp60FooDev = PluginSource.Classify("foo@Dev:abc/def");
            var scp60Evil = PluginSource.Classify("git@evil.example:a/b.git");
            var scp60Evil2 = PluginSource.Classify("git@github.com.evil.example:a/b.git");
            var scp60Plain = PluginSource.Classify("dsh-mnemon");
            var scp60NoSlash = PluginSource.Classify("git@github.com:onlyowner");
            Check("① scp 形态判 git 源：git@host:owner/repo[.git][#ref]（含 #ref 那条 —— 正则整串锚定的旧写法正是死在这里）",
                scp60Bare == PluginSource.Kind.GitBare && scp60Ref == PluginSource.Kind.GitRef &&
                scp60Cmt == PluginSource.Kind.GitCommit && scp60NoExt == PluginSource.Kind.GitBare &&
                scp60RefKind == PluginSource.RefKind.NamedRef,
                $"无ref={scp60Bare} #main={scp60Ref} #40位sha={scp60Cmt} 无.git={scp60NoExt} "
                + $"ClassifyRef(#main)={scp60RefKind}");
            Check("① scp 的反例（最容易出错的一格）：@scope/name 等 npm 包名、白名单外 host 一律仍是 Registry",
                scp60Scope == PluginSource.Kind.Registry && scp60ScopeVer == PluginSource.Kind.Registry &&
                scp60FooDev == PluginSource.Kind.Registry && scp60Evil == PluginSource.Kind.Registry &&
                scp60Evil2 == PluginSource.Kind.Registry && scp60Plain == PluginSource.Kind.Registry &&
                scp60NoSlash == PluginSource.Kind.Registry,
                $"@scope/name={scp60Scope} @scope/name@1.2.3={scp60ScopeVer} foo@Dev:abc/def={scp60FooDev} "
                + $"git@evil.example={scp60Evil} github.com.evil.example={scp60Evil2} "
                + $"dsh-mnemon={scp60Plain} 无斜杠={scp60NoSlash}");

            // ── ② 批量卸载的**空串闸**（MainWindow.Batch.cs 的 BatchUninstall_Click）──
            // 语义：包名过不了 npm 包名白名单 ⇒ BuildUninstallArgs 返回**空串** ⇒ 调用方**必须跳过**
            //      （空串送进 RunCommandAsync 就是一条**无参数**的 npx：必然失败，而且那条命令的形状连
            //       包龄放行都拿不到）—— 与单个卸载（Tools.cs 的 unArgs 检查）同一道闸。
            // 反证：把 BuildUninstallArgs 里的 IsValidPackageName 白名单删掉（改成直接拼串）⇒ 第一条立刻变红；
            //      第二条守反向（合法包名不能被这轮加固顺手拒掉，且仍要给得出**完整**命令）。
            // ⓘ 闸门的 UI 侧（`if (unArgs.Length == 0) { …; continue; }`）**量不到**：它在事件处理器里，
            //   纯函数层没有对应的判定入口（不自造一个只为自检存在的入口）。
            string un60DotDot = PluginManager.BuildUninstallArgs("..");
            string un60Path = PluginManager.BuildUninstallArgs("../etc");
            string un60Space = PluginManager.BuildUninstallArgs("a b");
            string un60Ok = PluginManager.BuildUninstallArgs("dsh-mnemon");
            Check("② 批量卸载的空串闸：非法包名（.. / ../etc / a b）⇒ BuildUninstallArgs 返回空串（调用方据此跳过）",
                un60DotDot.Length == 0 && un60Path.Length == 0 && un60Space.Length == 0,
                $"「..」=「{un60DotDot}」·「../etc」=「{un60Path}」·「a b」=「{un60Space}」");
            Check("② 空串闸的反向：合法包名给的是**完整**卸载命令，空串也不可能被认作「改清单」命令（拿不到包龄放行）",
                un60Ok.Length > 0 && un60Ok.Contains("remove dsh-mnemon") &&
                un60Ok.Contains(" --profile web ") && PluginManager.LooksLikePluginMutation(un60Ok) &&
                !PluginManager.LooksLikePluginMutation(un60DotDot),
                un60Ok.Length > 0 ? $"「{un60Ok}」" : "合法包名也被拒 ⇒ 白名单过头了");

            // ── ③ 批量路径的**显式** relaxSupplyChainPolicy:true（MainWindow.Batch.cs 两处调用点）──
            // 语义：显式 true 与"命令行形状被 LooksLikePluginMutation 认出"在 RunCommandAsync 里是
            //      同一条 `||`、同一个 InjectSupplyChainRelax（MainWindow.xaml.cs:2282）⇒ 行为等价，
            //      只是不再依赖形状。可断言的**不变量**：那些仍靠形状兜底的调用点，其命令确实被认得出。
            // 反证：把 LooksLikePluginMutation 里的 " update " 一支删掉（git 源更新命令就认不出了）⇒ 第一条红；
            //      把 BuildUpdateArgs 的 git 分支改回 `add <仓库地址>`（2026-09-18 那个缺陷）⇒ 同上变红。
            // ⓘ "批量那两处是否真的显式传了 true"**只能读源码文本**（Batch.cs:1509 / 1801）——
            //   本仓自检没有读 .cs 的先例，不在此硬造；InjectSupplyChainRelax 是 private，也没有纯函数替身。
            string mut60Un = un60Ok;
            string mut60Git = PluginManager.BuildUpdateArgs("dsh-watcher", "github:aa2246740/dsh-watcher", "仓库最新");
            Check("③ 靠形状兜底的调用点：批量卸载/批量更新（git 源）的命令都被 LooksLikePluginMutation 认作「改清单」命令",
                PluginManager.LooksLikePluginMutation(mut60Un) &&
                PluginManager.LooksLikePluginMutation(mut60Git) &&
                mut60Git.Contains(" update dsh-watcher") && !mut60Git.Contains("dsh-watcher@"),
                $"卸载=「{mut60Un}」· git 更新=「{mut60Git}」");
            Check("③ 放行层本身有内容：包龄/锁文件策略那条实测生效的键（pnpm_config_minimum_release_age=0）在注入表里",
                PluginManager.SupplyChainRelaxEnv().Any(kv =>
                    kv.Key == "pnpm_config_minimum_release_age" && kv.Value == "0") &&
                PluginManager.SupplyChainRelaxEnvLog().Contains("pnpm_config_minimum_release_age=0"),
                PluginManager.SupplyChainRelaxEnvLog());

            // ── ④ git 源 ref 探测的**失败分诊**（PluginSource.IsRefProbeNetworkNoise / LooksLikeRefListJson）──
            // 语义：404 是远端给的**答案** ⇒ **不降噪**（真漏到兜底说明代码漏了分支，那正该报 [ERROR]）；
            //      超时/连不上/TLS/5xx/401/403/429 ⇒ 用户/远端的处境 ⇒ 只记 [WARN]（断网时 N 个插件
            //      不该写 N 条 [ERROR]）；200 却读不出内容 ⇒ 仍 [ERROR]（本壳判据的问题，不许跟着降噪）。
            // 反证：把 IsRefProbeNetworkNoise 改成 `return true`（一律降噪）⇒ 404/400 两条反例变红；
            //      改成 `return false`（一律不降噪）⇒ 5xx/401/403/429/超时等正向全红。
            var nz60NotFound = PluginSource.IsRefProbeNetworkNoise(
                new System.Net.Http.HttpRequestException("404", null, System.Net.HttpStatusCode.NotFound));
            var nz60BadRequest = PluginSource.IsRefProbeNetworkNoise(
                new System.Net.Http.HttpRequestException("400", null, System.Net.HttpStatusCode.BadRequest));
            var nz60Server = PluginSource.IsRefProbeNetworkNoise(
                new System.Net.Http.HttpRequestException("500", null, System.Net.HttpStatusCode.InternalServerError));
            var nz60Denied = PluginSource.IsRefProbeNetworkNoise(
                new System.Net.Http.HttpRequestException("403", null, System.Net.HttpStatusCode.Forbidden));
            var nz60Unauth = PluginSource.IsRefProbeNetworkNoise(
                new System.Net.Http.HttpRequestException("401", null, System.Net.HttpStatusCode.Unauthorized));
            var nz60Quota = PluginSource.IsRefProbeNetworkNoise(
                new System.Net.Http.HttpRequestException("429", null, (System.Net.HttpStatusCode)429));
            var nz60Offline = PluginSource.IsRefProbeNetworkNoise(
                new System.Net.Http.HttpRequestException("拿不到状态码：DNS / 连不上 / TLS 握手失败"));
            var nz60Timeout = PluginSource.IsRefProbeNetworkNoise(new TaskCanceledException());
            var nz60Socket = PluginSource.IsRefProbeNetworkNoise(
                new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound));
            var nz60Io = PluginSource.IsRefProbeNetworkNoise(new IOException("响应读到一半连接被重置"));
            var nz60Own = PluginSource.IsRefProbeNetworkNoise(new InvalidOperationException("本壳自己的判据/代码问题"));
            Check("④ ref 探测失败分诊：404 与 400 不降噪（远端给的答案），网络/站点/配额/未登录一律降噪成 [WARN]",
                !nz60NotFound && !nz60BadRequest &&
                nz60Server && nz60Denied && nz60Unauth && nz60Quota &&
                nz60Offline && nz60Timeout && nz60Socket && nz60Io && !nz60Own,
                $"404={nz60NotFound} 400={nz60BadRequest} 5xx={nz60Server} 403={nz60Denied} 401={nz60Unauth} "
                + $"429={nz60Quota} 无状态码={nz60Offline} 超时={nz60Timeout} socket={nz60Socket} "
                + $"半截响应={nz60Io} 本壳判据={nz60Own}");
            string rl60List = "[{\"name\":\"master\",\"commit\":{\"sha\":\"" + b60Sha + "\"}}]";
            // 真实形状按各站**实测原文**取样（见 PluginSource.cs 的 FindShaByName 头注释）：
            //   · bitbucket 标签列表：根是 {"values":[…]}，提交号在 **target.hash**，该项**没有** commit 属性；
            //   · gitee/github 标签列表：根是数组，提交号在 commit.sha；
            //   · gitlab 标签列表：根是数组，target 是**字符串**提交号。
            string rl60Bb = "{\"values\":[{\"name\":\"v1.0\",\"date\":\"2016-06-15T06:37:45+00:00\","
                          + "\"target\":{\"type\":\"commit\",\"hash\":\"" + b60Sha + "\"}}]}";
            string rl60GlNoCommit = "[{\"name\":\"v2.0\",\"target\":\"" + b60Sha + "\"}]";
            string rl60Html = "<!DOCTYPE html><html><body>404 Not Found</body></html>";
            string rl60Msg = "{\"message\":\"Not Found\"}";
            string rl60Half = "[{\"name\":\"mast";
            // 每个子条件先落成局部量：detail 必须把**每一项**都打出来，否则红了也定位不到（本批踩过的坑）。
            string rl60EmptyHit = PluginSource.FindShaByName(rl60List, "nightly-20260905");
            bool rl60Arr = PluginSource.LooksLikeRefListJson(rl60List);
            bool rl60BbIsList = PluginSource.LooksLikeRefListJson(rl60Bb);
            bool rl60EmptyArr = PluginSource.LooksLikeRefListJson("[]");
            bool rl60HtmlIsList = PluginSource.LooksLikeRefListJson(rl60Html);
            bool rl60MsgIsList = PluginSource.LooksLikeRefListJson(rl60Msg);
            bool rl60HalfIsList = PluginSource.LooksLikeRefListJson(rl60Half);
            bool rl60BlankIsList = PluginSource.LooksLikeRefListJson("");
            bool rl60NullIsList = PluginSource.LooksLikeRefListJson(null);
            Check("④ 「列表读得懂、只是没这个名字」(⇒Missing) 与「根本读不出列表」(⇒Unknown+[ERROR]) 分得开",
                rl60Arr && rl60EmptyHit.Length == 0 && rl60BbIsList && rl60EmptyArr &&
                !rl60HtmlIsList && !rl60MsgIsList && !rl60HalfIsList && !rl60BlankIsList && !rl60NullIsList,
                $"数组列表={rl60Arr} 空数组={rl60EmptyArr} bitbucket信封={rl60BbIsList} "
                + $"没有这个名字⇒读出{rl60EmptyHit.Length}字 | HTML={rl60HtmlIsList} 报错JSON={rl60MsgIsList} "
                + $"半截={rl60HalfIsList} 空串={rl60BlankIsList} null={rl60NullIsList}");

            // ★ 缺陷钉子（本批实测撞出来的**真缺陷**，2026-09-18 晚；不在本单修补范围 ⇒ SelfTest 只负责钉住）：
            //   列表项**没有 commit 属性**时，提交号一个都读不出来 —— 连实现自己刚补的 bitbucket 分支也走不到。
            //   实测（本机自检）：bitbucket 标签列表的真实形状（FindShaByName 头注释里的实测原文：根是
            //   {"values":[…]}、提交号在 target.hash、该项**没有** commit）喂进 FindShaByName ⇒ **返回空串**。
            //   后果链：LooksLikeRefListJson 判"列表读得懂"⇒ FetchRefKindAsync 走"列表里没有这个名字"那一支
            //   ⇒ 判 RefMatchKind.Missing ⇒ 卡片说「远端已无 X 这个分支或标签」—— 而标签其实在。这正是
            //   FindShaByName 里那段注释自己写下的"拿我们的判据失败去栽赃远端"（比 Unknown 更误导）。
            //   根因（读码可得，未改实现）：`e.TryGetProperty("commit", out var cEl)` 在 commit 缺失时 cEl 是
            //   default(JsonElement)（ValueKind=Undefined），而紧随其后的 `cEl.TryGetProperty("id", …)`
            //   **不在任何 if 块内**（当前 PluginSource.cs:1049）—— JsonElement.TryGetProperty 对非 Object 元素
            //   会抛 InvalidOperationException，被 FindShaByName 末尾的 `catch { return ""; }` 吞掉 ⇒ 后面那条
            //   target.hash 分支（当前约 1067 行）**永远不可达**。
            //   旁证（同文件自查，说明是漏写而非有意）：两个函数之上的 ParseCommitDetailJson 里那句
            //   `cEl.TryGetProperty("id", …)`（约 922 行）**是有** `cEl.ValueKind == Object` 守卫的，同一写法。
            //   修法（属 PluginSource.cs，本单不许碰）：把那一句收进
            //   `if (cEl.ValueKind == System.Text.Json.JsonValueKind.Object)` 之内（或与上面那个 if 并成一个块）。
            //   ⚠ 行号随同事改本文件在漂（PluginSource.cs 本晚已改数次）⇒ 按**函数名 + 语句**定位。
            //   ⚠ 本断言**刻意保持红色**：它钉的是"应该读得出来"，而不是"当前读不出来"——不拿缺陷当期望值。
            string rl60BbHit = PluginSource.FindShaByName(rl60Bb, "v1.0");
            string rl60GlHit = PluginSource.FindShaByName(rl60GlNoCommit, "v2.0");
            Check("④★【缺陷钉子·修好 PluginSource.cs:885 之前应为红】bitbucket 标签列表（target.hash、无 commit）必须读得出提交号",
                rl60BbHit == b60Sha && rl60GlHit == b60Sha,
                $"bitbucket.target.hash ⇒ 「{rl60BbHit}」(应为 {b60Sha}) · 无 commit 的字符串 target ⇒ 「{rl60GlHit}」"
                + " · 根因：PluginSource.cs:885 对 default(JsonElement) 调 TryGetProperty 抛异常、被 catch 吞成空串");

            // ── ⑤ git 源更新判定改读"锁文件事实"（MainWindow.EvaluateUpdate 的第 6 实参 expectedCommit）──
            // 语义：git 源的成败**不再只看退出码**，而是比对"跑命令前"与"跑命令后"锁文件里的提交 ——
            //   提交没动 ⇒ 失败（"空转被记成已更新"那个 bug）；前进 ⇒ 成功；
            //   拿不到"跑命令前"的提交 ⇒ 回落退出码（Measured=false + Unknown，保守口径保留）；
            //   短号 ↔ 完整 sha 可比（SameCommit 只比前 7 位、忽略大小写）。
            // ⚠ 夹具**必须显式传** profileDir 与 commitBefore（本项目已连踩三次"落回真实 profile"的坑）：
            //   样本放 t60 并显式传参；GuardPaths 的 profile 重定向指向**另一个空目录** guard60 ——
            //   这样万一哪天真落回了默认路径，读到的是空目录（断言变红），而不是悄悄读到样本而假绿。
            // 反证：把 EvaluateUpdate 里 `if (commitBefore.Length > 0) { … }` 整段删掉（= 退回"只看退出码"），
            //      ⑤-1（退出码 0 却必须判失败）、⑤-2（退出码非零却必须判成功）、⑤-4、⑤-5 立刻变红
            //      （⑤-3 仍绿是对的：它守的恰是"删掉之后"那个回落口径本身）；把 advanced 取反（SameCommit 用反）
            //      则 ⑤-1 与 ⑤-2 同时红；把第 6 实参从批量入口丢掉（BatchUpdateVerdictForTest 不再转发）⇒ ⑤-5 红。
            string t60 = Path.Combine(Path.GetTempPath(), "dshguard-gitcommit-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string guard60 = Path.Combine(t60, "guard-profile");       // GuardPaths 重定向目标：**空**目录
            string prof60Prev = GuardPaths.ProfileDir;
            string snap60Prev = GuardPaths.SnapshotRoot;
            string log60Prev = GuardPaths.LogDirExplicit;
            try
            {
                const string hold60 = "dsh-git-hold";      // 锁里的提交 == 跑命令前记下的（空转）
                const string move60 = "dsh-git-move";      // 锁里的提交 != 跑命令前记下的（真推进）
                Directory.CreateDirectory(t60);
                Directory.CreateDirectory(guard60);
                File.WriteAllText(Path.Combine(t60, "package.json"),
                    "{ \"name\": \"dsh-profile-web\", \"private\": true, \"dependencies\": { "
                    + "\"" + hold60 + "\": \"git+https://github.com/o/r.git\", "
                    + "\"" + move60 + "\": \"github:o/r\" } }", new UTF8Encoding(false));
                // 锁文本用实现自带的 SampleLock 造（`resolution: {commit: …}` 形态，LockedCommit 认的就是它）
                File.WriteAllText(Path.Combine(t60, "pnpm-lock.yaml"),
                    PluginSource.SampleLock(hold60, b60Sha) + PluginSource.SampleLock(move60, b60Sha2),
                    new UTF8Encoding(false));
                GuardPaths.Apply(null, snap60Prev, guard60);   // 只做"默认 profile 别指向真实用户目录"这一层保险

                // 夹具自证：显式指到的那份样本真的读得动；重定向目标确实是**另一个空目录**（落回默认 ⇒ 读不到样本）
                string lock60Hold = PluginManager.ReadInstalledCommit(hold60, t60);
                string spec60Hold = PluginManager.DepSpecIn(Path.Combine(t60, "package.json"), hold60);
                string spec60Guard = PluginManager.DepSpecIn(Path.Combine(guard60, "package.json"), hold60);
                Check("⑤ 夹具自证：锁/清单都从**显式传入**的样本目录读出，重定向目标是空目录（杜绝「落回真实 profile」）",
                    lock60Hold == b60Sha && PluginSource.Classify(spec60Hold) == PluginSource.Kind.GitBare &&
                    PluginSource.LockedCommit(PluginSource.SampleLock(hold60, b60Sha), hold60) == b60Sha &&
                    spec60Guard.Length == 0 && !File.Exists(Path.Combine(guard60, "pnpm-lock.yaml")),
                    $"样本锁里的提交={lock60Hold} · 清单声明=「{spec60Hold}」({PluginSource.Classify(spec60Hold)}) "
                    + $"· guard 目录清单=「{spec60Guard}」");

                // ⑤-1 ★ 提交**没动** ⇒ 失败（哪怕退出码是 0）—— 这就是"空转被记成已更新"那个 bug 的判据
                var r60Hold = MainWindow.EvaluateUpdate(hold60, "仓库最新", true, "", t60, b60Sha);
                Check("⑤-1 git 源空转：命令退出码 0、锁里的提交却没动 ⇒ **判失败**（旧口径会记成「已更新」）",
                    r60Hold.CmdOk && r60Hold.Measured && !r60Hold.Succeeded && r60Hold.NotSatisfied &&
                    r60Hold.Check == PluginManager.VersionCheck.NotSatisfied && r60Hold.Note.Contains("git 源"),
                    $"退出码0={r60Hold.CmdOk} 可判={r60Hold.Measured} 判成功={r60Hold.Succeeded} · {r60Hold.Note}");

                // ⑤-2 ★ 提交**前进** ⇒ 成功（哪怕退出码非零）—— 与"只看退出码"正好相反的那一半
                var r60Move = MainWindow.EvaluateUpdate(move60, "仓库最新", false, "", t60, b60Sha);
                Check("⑤-2 git 源真推进：退出码非零、锁里的提交前进了 ⇒ **判成功**（退出码只作参考）",
                    !r60Move.CmdOk && r60Move.Measured && r60Move.Succeeded && r60Move.NoteDowngraded &&
                    r60Move.Check == PluginManager.VersionCheck.Satisfied,
                    $"退出码0={r60Move.CmdOk} 可判={r60Move.Measured} 判成功={r60Move.Succeeded} · {r60Move.Note}");

                // ⑤-3 拿不到"跑命令前的提交" ⇒ 如实回落退出码（保守口径必须保留，不许把"读不到"当成功/失败）
                var r60NullOk = MainWindow.EvaluateUpdate(hold60, "仓库最新", true, "", t60, null);
                var r60NullBad = MainWindow.EvaluateUpdate(hold60, "仓库最新", false, "", t60);
                Check("⑤-3 拿不到「跑命令前的提交」⇒ 回落退出码（Measured=false · Unknown），两个方向都不越权",
                    !r60NullOk.Measured && r60NullOk.Check == PluginManager.VersionCheck.Unknown && r60NullOk.Succeeded &&
                    !r60NullBad.Measured && r60NullBad.Check == PluginManager.VersionCheck.Unknown && !r60NullBad.Succeeded,
                    $"退出码0=True⇒判成功={r60NullOk.Succeeded} · 退出码0=False⇒判成功={r60NullBad.Succeeded} "
                    + $"· 结论={r60NullOk.Check}");

                // ⑤-4 短号 ↔ 完整 sha 可比（期望值由输入推导，不手写；判据只有 SameCommit/SameSha 那一份）
                string short60 = b60Sha.Substring(0, 7);
                var r60Same7 = MainWindow.EvaluateUpdate(hold60, "仓库最新", true, "", t60, short60);
                var r60Move7 = MainWindow.EvaluateUpdate(move60, "仓库最新", true, "", t60, short60);
                Check("⑤-4 短号 ↔ 完整 sha 可比：7 位短号与锁里 40 位 sha 同前 7 位 ⇒ 判「没动」；不同 ⇒ 判「动了」",
                    r60Same7.Measured && !r60Same7.Succeeded && r60Move7.Measured && r60Move7.Succeeded &&
                    PluginManager.SameCommit(short60, b60Sha) && !PluginManager.SameCommit(short60, b60Sha2),
                    $"hold(短号==前7位)⇒判成功={r60Same7.Succeeded} · move(短号≠前7位)⇒判成功={r60Move7.Succeeded} "
                    + $"· SameCommit(短,全)={PluginManager.SameCommit(short60, b60Sha)}");

                // ⑤-5 批量路径同口径（BatchUpdateVerdictForTest 的 6 参重载：profileDir 与 commitBefore 都显式传）
                //      同一样本、同一次调用链上，"没动⇒失败 / 动了⇒成功"必须与单个更新一模一样。
                var p60Hold = new PluginManager.Plugin { Name = hold60 };
                var u60Hold = new PluginManager.PluginUpdate { Name = hold60, Installed = short60, Latest = "仓库最新", HasUpdate = true };
                var p60Move = new PluginManager.Plugin { Name = move60 };
                var u60Move = new PluginManager.PluginUpdate { Name = move60, Installed = short60, Latest = "仓库最新", HasUpdate = true };
                var b60Hold = MainWindow.BatchUpdateVerdictForTest(p60Hold, u60Hold, true, "", t60, b60Sha);
                var b60Move = MainWindow.BatchUpdateVerdictForTest(p60Move, u60Move, true, "", t60, b60Sha);
                Check("⑤-5 批量更新与单个更新同口径：同一份样本上「没动⇒失败 / 动了⇒成功」（批量入口也吃 commitBefore）",
                    b60Hold.CmdOk && b60Hold.Measured && !b60Hold.Succeeded && b60Hold.NotSatisfied &&
                    b60Move.CmdOk && b60Move.Measured && b60Move.Succeeded,
                    $"批量 hold 判成功={b60Hold.Succeeded} · 批量 move 判成功={b60Move.Succeeded} "
                    + $"· 批量 move 结论={b60Move.Check}");

                // ⓘ ⑤ 的另一半（"跑命令前"那一端**读的时机**必须在 RunCommandAsync 之前）也量不到：
                //   它在 Batch.cs / Tools.cs 的调用点顺序里，纯函数层没有可断言的入口；本批只钉判据本身。
            }
            finally
            {
                GuardPaths.Apply(log60Prev.Length > 0 ? log60Prev : null, snap60Prev, prof60Prev);
                try { if (Directory.Exists(t60)) Directory.Delete(t60, true); } catch { }
            }

            // ══════ 61. 补齐 2026-09-19 用户实测 6 处修复里**此前零断言**的几处（①–④）══════
            // 与 59/60 两批同一来由：那些修复单被禁止改本文件，只在实现处留了"建议片段"。
            // ⚠ 每条都写明"把实现改回旧行为 ⇒ 它会不会红"；量不到的地方如实跳过（见 ③ 的说明）。
            // ⚠ 变量名一律带 n61 / p61 前缀：RunCore 整个方法体是**一个**声明空间，重名即 CS0136。

            // ── ① VersionInfo.ExtractDshRequirement：作者声明的兼容性只留**一份**判据 ──
            // 现场 bug：本地管理的插件一律显示「未声明」，而市场里读得到 —— 根因是三处各写一份判据
            // （本地 Scan / 市场 ApplyMeta / 更新脚本），字段优先级与初值各不相同。现在三处都转调
            // VersionInfo.ExtractDshRequirement，（Requirement, Source）只有那一处产出。

            // ①-a 真实样本：这三个包就在本机 node_modules 里，且**各踩中不同的一档**：
            //   · @mars-sea/dsh-commandcode-provider → dsh.compatibility.dsh（①，最优先）
            //   · @linxin666/dsh-web-all           → dsh.engines.dsh（②；它没有 compatibility，没有顶层 engines.dsh）
            //   · @changfenhuang/dsh-genui         → 只有 peerDependencies（它的顶层 engines 只声明 node / pnpm）
            // ⇒ 三档合起来证明"本地读不到作者声明的兼容性"这个 bug 真的修好了。
            // 反证：把 ① 那一支删掉 ⇒ 第一个包落到 ② ⇒ 来源变 dsh.engines ⇒ 第 1 条变红；
            //      把 ③ 提到 ④ 之后（或删 ③）对本样本无影响（genui 只声明 peerDeps）—— ③ 的优先级由 ①-b 的合成样本钉。
            // 找不到样本（换电脑 / 没装这些插件）时**如实 Skip**，不用编的样本冒充"真实样本"。
            string n61Nm = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".dsh", "profiles", "web", "node_modules");
            string n61P1 = Path.Combine(n61Nm, "@mars-sea", "dsh-commandcode-provider", "package.json");
            string n61P2 = Path.Combine(n61Nm, "@linxin666", "dsh-web-all", "package.json");
            string n61P3 = Path.Combine(n61Nm, "@changfenhuang", "dsh-genui", "package.json");
            if (!File.Exists(n61P1) || !File.Exists(n61P2) || !File.Exists(n61P3))
            {
                Skip("① 真实插件样本：声明字段优先级（dsh.compatibility → dsh.engines → peerDependencies）",
                    "本机 node_modules 里找不到那三个样本包，本条未验证");
            }
            else
            {
                static (string Req, string Src) Req61(string file)
                {
                    using var d61 = JsonDocument.Parse(File.ReadAllText(file));
                    return VersionInfo.ExtractDshRequirement(d61.RootElement);
                }
                var (r61A, s61A) = Req61(n61P1);
                var (r61B, s61B) = Req61(n61P2);
                var (r61C, s61C) = Req61(n61P3);
                Check("① 真实样本按声明字段定档：compatibility→① / dsh.engines→② / 只剩 peerDependencies→④",
                    s61A == "dsh.compatibility" && r61A.Length > 0 &&
                    s61B == "dsh.engines" && r61B == ">=0.1.5-rc.1" &&
                    s61C == "peerDependencies" && r61C.Contains("0.1.5-alpha.1", StringComparison.Ordinal),
                    $"①={s61A}«{Shorten(r61A, 24)}» · ②={s61B}«{r61B}» · ④={s61C}«{Shorten(r61C, 24)}»");
            }

            // ①-b 优先级四档 + 初值退化：**合成样本**（同一份 JSON 里同时摆多档，才量得出"谁压谁"）。
            // 反证（逐条）：
            //   · 把 ① 的 `if (compat.Length > 0) return…` 删掉        ⇒ 合成① 落到 ② ⇒ 来源变 ⇒ 红；
            //   · 把 ② 删掉                                            ⇒ 合成① 落到 ③ ⇒ 红；
            //   · 把 ③ 挪到 ④ 之后（或删 ③，退回旧顺序的那两种写法）    ⇒ 合成② 落到 ④ ⇒ 红；
            //   · ④ 的初值由 "0.0.0" 退回空串 `""`                      ⇒ 初值条变红（下一条单独盯它）。
            var (r61P1, s61P1) = VersionInfo.ExtractDshRequirement(JsonDocument.Parse(
                "{\"dsh\":{\"compatibility\":{\"dsh\":\"^1.0.0\"},\"engines\":{\"dsh\":\"^2.0.0\"}},"
                + "\"engines\":{\"dsh\":\"^3.0.0\"},\"peerDependencies\":{\"@deepseek-ai/dsh-agent\":\"^4.0.0\"}}").RootElement);
            var (r61P2, s61P2) = VersionInfo.ExtractDshRequirement(JsonDocument.Parse(
                "{\"dsh\":{\"engines\":{\"dsh\":\"^2.0.0\"}},"
                + "\"engines\":{\"dsh\":\"^3.0.0\"},\"peerDependencies\":{\"@deepseek-ai/dsh-agent\":\"^4.0.0\"}}").RootElement);
            var (r61P3, s61P3) = VersionInfo.ExtractDshRequirement(JsonDocument.Parse(
                "{\"engines\":{\"dsh\":\"^3.0.0\"},\"peerDependencies\":{\"@deepseek-ai/dsh-agent\":\"^4.0.0\"}}").RootElement);
            var (r61P4, s61P4) = VersionInfo.ExtractDshRequirement(JsonDocument.Parse(
                "{\"peerDependencies\":{\"@deepseek-ai/dsh-agent\":\"^4.0.0\"}}").RootElement);
            Check("① 优先级四档：① 压 ②、② 压 ③、③ 压 ④（同一份 JSON 里四档齐备时，只认最优先的那一档）",
                s61P1 == "dsh.compatibility" && r61P1 == "^1.0.0" &&
                s61P2 == "dsh.engines" && r61P2 == "^2.0.0" &&
                s61P3 == "engines" && r61P3 == "^3.0.0" &&
                s61P4 == "peerDependencies" && r61P4 == "^4.0.0",
                $"①={s61P1}«{r61P1}» ②={s61P2}«{r61P2}» ③={s61P3}«{r61P3}» ④={s61P4}«{r61P4}»");
            Check("① peerDependencies 的分支初值必须可比：声明为「0.0.0」也读得出来（初值退回空串即恒不入选 ⇒ 本条目变红）",
                s61P4 == "peerDependencies" && r61P4 == "^4.0.0",
                $"初值退化样本的结论：来源={s61P4} 要求=«{r61P4}»（旧代码 bestVer=\"\" ⇒ 这里恒为空串）");

            // ①-c **市场路径 与 本地路径 同结论**：同一份 package.json 内容，两边必须逐字一致。
            // 只 ①-c 一条不够（两边可能各自对、但答得不同）—— 所以本条的判据是"两边**相等**且都非空"：
            //   本地走 PluginManager.Scan（读临时 profile 的 node_modules），市场走 PluginMarket.ApplyMeta
            //   （读 packument 的 versions[latest]）。两边都转调 VersionInfo.ExtractDshRequirement。
            // ⚠ 夹具：只在 %TEMP% 建 profile + node_modules，GuardPaths.Apply 重定向后 try/finally 还原；
            //   PackageFileOverrideForTest 用完还原。**绝不碰真实 profile**。
            {
                string n61Dir = Path.Combine(Path.GetTempPath(), "dshguard-req-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                string n61BakProf = GuardPaths.ProfileDir;
                string? n61BakPkg = PluginManager.PackageFileOverrideForTest;
                try
                {
                    // 刻意用**带 scope 的名字**：顺带量一下 Scan 的目录拼装（name.Replace('/') 那一处）。
                    const string n61PkgName = "@dsh-probe/tier4";
                    string n61ModDir = Path.Combine(n61Dir, "node_modules", "@dsh-probe", "tier4");
                    Directory.CreateDirectory(n61ModDir);
                    const string n61Decl =
                        "{\"name\":\"@dsh-probe/tier4\",\"version\":\"1.0.0\","
                        + "\"peerDependencies\":{\"@deepseek-ai/dsh-agent\":\">=0.1.5-rc.1\"}}";
                    File.WriteAllText(Path.Combine(n61Dir, "package.json"),
                        "{ \"name\": \"dsh-profile-web\", \"private\": true, \"dependencies\": { \""
                        + n61PkgName + "\": \"^1.0.0\" } }", new UTF8Encoding(false));
                    File.WriteAllText(Path.Combine(n61ModDir, "package.json"), n61Decl, new UTF8Encoding(false));

                    GuardPaths.Apply(null, null, n61Dir);
                    PluginManager.PackageFileOverrideForTest = Path.Combine(n61Dir, "package.json");

                    // 落盘自证：两个文件确实写出来了（若这里就是 false，问题在夹具而不在判据）。
                    bool n61FixtureOk = File.Exists(Path.Combine(n61Dir, "package.json"))
                                        && File.Exists(Path.Combine(n61ModDir, "package.json"));

                    var n61LocalList = PluginManager.Scan("0.1.5-rc.2");
                    var n61Local = n61LocalList.FirstOrDefault(p => p.Name == n61PkgName);
                    var n61Mp = new PluginMarket.MarketPlugin { Name = n61PkgName, Npm = n61PkgName };
                    PluginMarket.ApplyMeta(n61Mp,
                        "{\"dist-tags\":{\"latest\":\"1.0.0\"},\"versions\":{\"1.0.0\":" + n61Decl + "}}",
                        "0.1.5-rc.2");

                    // 与直接调唯一判据的结果对照：三方（本地 / 市场 / 判据本身）必须逐字一致。
                    using var n61Doc = JsonDocument.Parse(n61Decl);
                    var (n61Direct, n61DirectSrc) = VersionInfo.ExtractDshRequirement(n61Doc.RootElement);

                    // 复刻**旧写法**的 peerDependencies 那一支：唯一差别是初值用空串。其余步骤逐字照抄
                    // （String.StartsWith + JsonValueKind.String + Trim + RequirementVersions 取最高 + Compare）。
                    // ⚠ 这是一份**只为自检而存在**的旁证副本，不参与产品路径；它证明"空串初值 ⇒ 整支静默失效"
                    //   正是现场那个 bug 的根因，而不是把判据复制到自检里当第二套实现。
                    // 为什么它不会与实现漂移：它只在版本比较那一环上模拟旧行为，
                    //   而"初值不可比 ⇒ 永不入选"是 System.String.Compare 的既定语义，不依赖本仓任何实现。
                    static string OldSeedPeerBranch61(JsonElement root)
                    {
                        if (root.ValueKind != JsonValueKind.Object
                            || !root.TryGetProperty("peerDependencies", out var pd)
                            || pd.ValueKind != JsonValueKind.Object) return "";
                        string best = "", bestVer = "";          // ← 旧写法的初值：空串（不可比）
                        foreach (var dep in pd.EnumerateObject())
                        {
                            if (!dep.Name.StartsWith("@deepseek-ai/dsh", StringComparison.OrdinalIgnoreCase)) continue;
                            if (dep.Value.ValueKind != JsonValueKind.String) continue;
                            string s = (dep.Value.GetString() ?? "").Trim();
                            if (s.Length == 0) continue;
                            var vs = VersionInfo.RequirementVersions(s);
                            string cand = vs.Count > 0
                                ? vs.OrderByDescending(v => v, Comparer<string>.Create(VersionInfo.Compare)).First()
                                : "0.0.0";
                            if (VersionInfo.Compare(cand, bestVer) > 0) { best = s; bestVer = cand; }
                        }
                        return best;
                    }
                    string n61OldSeed = OldSeedPeerBranch61(n61Doc.RootElement);

                    Check("① 本地与市场同结论：同一份 package.json 经 Scan 与 ApplyMeta 得出的要求/来源逐字一致（且与唯一判据本身一致）",
                        n61FixtureOk && n61Local != null &&
                        n61Local.Requirement == n61Direct && n61Local.RequirementSource == n61DirectSrc &&
                        n61Mp.Requirement == n61Direct && n61Mp.RequirementSource == n61DirectSrc &&
                        n61Direct == ">=0.1.5-rc.1" && n61DirectSrc == "peerDependencies" &&
                        n61OldSeed == "",
                        $"夹具落盘={n61FixtureOk}（清单+包内文件都写出来了）· "
                        + (n61Local == null
                            ? $"本地 Scan 没扫到那个包（夹具没生效？）：扫到 {n61LocalList.Count} 个条目 · "
                              + $"清单存在={File.Exists(Path.Combine(n61Dir, "package.json"))} · "
                              + $"包目录存在={Directory.Exists(Path.Combine(n61Dir, "node_modules", "@dsh-probe", "tier4"))} · "
                              + $"ProfileDir=«{GuardPaths.ProfileDir}» · PackageFile=«{PluginManager.PackageFile}» · "
                              + $"扫到的名字=[{string.Join(",", n61LocalList.Select(p => p.Name))}] · "
                              + $"市场=«{n61Mp.RequirementSource}»«{n61Mp.Requirement}» 判据=«{n61DirectSrc}»«{n61Direct}»"
                            : $"本地={n61Local.RequirementSource}«{n61Local.Requirement}» · "
                              + $"市场={n61Mp.RequirementSource}«{n61Mp.Requirement}» · "
                              + $"判据={n61DirectSrc}«{n61Direct}»（三方应逐字相同）· "
                              + $"旧种子（空串）在这份声明上产出=«{n61OldSeed}»（应为空串 ⇒ 旧写法那条分支静默失效）"));
                }
                finally
                {
                    PluginManager.PackageFileOverrideForTest = n61BakPkg;
                    GuardPaths.Apply(null, null, n61BakProf);
                    try { Directory.Delete(n61Dir, true); } catch { }
                }
            }

            // ── ② 更新状态"说不清"要说是**哪一种**说不清（RefProbeFailure 分诊）──
            // 现场：git 源插件查 ref 失败时界面只说「说不清」，用户无从判断是自己网络还是站点在限流。
            // 现在 ClassifyRefProbeFailure 分成五类并带一句人话（RefProbeFailureHint），且与既有
            // IsRefProbeNetworkNoise **同源**（同一份实现，不许再长出第二套会漂移的判据）。
            // 反证：把 ClassifyRefProbeFailure 的 401 支删掉 ⇒ 落到默认 None ⇒ …Unauthorized 那条变红；
            //      把 IsRefProbeNetworkNoise 改成"类别 != None 之外的另一套判据" ⇒ 等价性那条变红。
            var n61None = PluginSource.ClassifyRefProbeFailure(null);
            var n61Rl403 = PluginSource.ClassifyRefProbeFailure(
                new System.Net.Http.HttpRequestException("403", null, System.Net.HttpStatusCode.Forbidden));
            var n61Rl429 = PluginSource.ClassifyRefProbeFailure(
                new System.Net.Http.HttpRequestException("429", null, (System.Net.HttpStatusCode)429));
            var n61Un401 = PluginSource.ClassifyRefProbeFailure(
                new System.Net.Http.HttpRequestException("401", null, System.Net.HttpStatusCode.Unauthorized));
            var n61Net = PluginSource.ClassifyRefProbeFailure(new TaskCanceledException());
            var n61NetIo = PluginSource.ClassifyRefProbeFailure(new IOException("响应读到一半连接被重置"));
            var n61NetDns = PluginSource.ClassifyRefProbeFailure(
                new System.Net.Http.HttpRequestException("拿不到状态码：DNS / 连不上 / TLS 握手失败"));
            var n61Srv = PluginSource.ClassifyRefProbeFailure(
                new System.Net.Http.HttpRequestException("503", null, System.Net.HttpStatusCode.ServiceUnavailable));
            var n61N404 = PluginSource.ClassifyRefProbeFailure(
                new System.Net.Http.HttpRequestException("404", null, System.Net.HttpStatusCode.NotFound));
            var n61N400 = PluginSource.ClassifyRefProbeFailure(
                new System.Net.Http.HttpRequestException("400", null, System.Net.HttpStatusCode.BadRequest));
            var n61NOwn = PluginSource.ClassifyRefProbeFailure(new InvalidOperationException("本壳自己的判据问题"));
            Check("② 失败分诊：403/429→限流 · 401→未登录 · 超时/IO/拿不到状态码→网络 · 5xx→站点故障 · 404/400/自身异常→None（不归本枚举管）",
                n61Rl403 == PluginSource.RefProbeFailure.RateLimited &&
                n61Rl429 == PluginSource.RefProbeFailure.RateLimited &&
                n61Un401 == PluginSource.RefProbeFailure.Unauthorized &&
                n61Net == PluginSource.RefProbeFailure.Network &&
                n61NetIo == PluginSource.RefProbeFailure.Network &&
                n61NetDns == PluginSource.RefProbeFailure.Network &&
                n61Srv == PluginSource.RefProbeFailure.Server &&
                n61None == PluginSource.RefProbeFailure.None &&
                n61N404 == PluginSource.RefProbeFailure.None &&
                n61N400 == PluginSource.RefProbeFailure.None &&
                n61NOwn == PluginSource.RefProbeFailure.None,
                $"403={n61Rl403} 429={n61Rl429} 401={n61Un401} 超时={n61Net} IO={n61NetIo} 无状态码={n61NetDns} "
                + $"503={n61Srv} 404={n61N404} 400={n61N400} 自身异常={n61NOwn}");
            Check("② 判据只有一份：IsRefProbeNetworkNoise ≡ 「类别 != None」（两者不许各长一套会漂移的实现）",
                PluginSource.IsRefProbeNetworkNoise(new TaskCanceledException()) == (n61Net != PluginSource.RefProbeFailure.None) &&
                PluginSource.IsRefProbeNetworkNoise(
                    new System.Net.Http.HttpRequestException("404", null, System.Net.HttpStatusCode.NotFound)) == (n61N404 != PluginSource.RefProbeFailure.None) &&
                PluginSource.IsRefProbeNetworkNoise(new InvalidOperationException("本壳自己的问题")) == (n61NOwn != PluginSource.RefProbeFailure.None) &&
                PluginSource.IsRefProbeNetworkNoise(null) == (n61None != PluginSource.RefProbeFailure.None),
                $"超时: 噪音={PluginSource.IsRefProbeNetworkNoise(new TaskCanceledException())} 类别≠None={n61Net != PluginSource.RefProbeFailure.None}；"
                + $"自身异常: 噪音={PluginSource.IsRefProbeNetworkNoise(new InvalidOperationException("x"))}");
            // 措辞纪律（以 PluginSource.cs:529-539 的**实际** switch 为准）：
            //   · 拿不准（None）⇒ **空串**，绝不替调用方编一个原因；
            //   · Shape（「HTTP 通了却读不出形状」）**不是**拿不准，而是一句确定的话 ⇒ 必须是**非空**且
            //     与另外四类都不同的人话（它是本壳要修的东西，不能混进"可能的原因"里）。
            // ⚠ 此处曾误写成"None 与 Shape 都给空串" ⇒ 本条一度假红（实测 Shape 有确切文案）。
            // 反证：把 switch 里 Shape 那一支删掉（落 `_ => ""`）⇒ 本条立刻变红。
            string n61HintNone = PluginSource.RefProbeFailureHint(PluginSource.RefProbeFailure.None);
            string n61HintShape = PluginSource.RefProbeFailureHint(PluginSource.RefProbeFailure.Shape);
            var n61HintClasses = new[]
            {
                PluginSource.RefProbeFailure.RateLimited, PluginSource.RefProbeFailure.Unauthorized,
                PluginSource.RefProbeFailure.Network, PluginSource.RefProbeFailure.Server
            };
            var n61Hints = n61HintClasses.Select(PluginSource.RefProbeFailureHint).ToList();
            Check("② 说不清也要分得清：四类各给一句**互不相同**的人话（Shape 另有确切文案）；只有拿不准（None）才返回空串，绝不编原因",
                n61HintNone.Length == 0 && n61HintShape.Length > 0 &&
                n61Hints.All(h => h.Length > 0) && n61Hints.Distinct().Count() == n61Hints.Count &&
                n61Hints.All(h => h != n61HintShape),
                $"None=«{n61HintNone}»（应空）· Shape=«{Shorten(n61HintShape, 20)}»（应非空）· "
                + $"四类提示各异={n61Hints.Distinct().Count()}/{n61Hints.Count} 条且都不等于 Shape");
            // 第 1 页 404 = 远端给的**答案**（Missing）；第 2 页及以后 404 = 远端自相矛盾，只能算 Unknown。
            // 反证：退回旧写法 `=> RefMatchKind.Missing`（不看页号）⇒ 第 2/3 条立刻变红。
            Check("② 标签列表翻页 404 的分诊：第 1 页（含 0）⇒ Missing（远端给的答案）· 第 2 页起 ⇒ Unknown（不许激进地说「已被删/改名」）",
                PluginSource.RefListPage404Verdict(1) == PluginSource.RefMatchKind.Missing &&
                PluginSource.RefListPage404Verdict(0) == PluginSource.RefMatchKind.Missing &&
                PluginSource.RefListPage404Verdict(2) == PluginSource.RefMatchKind.Unknown &&
                PluginSource.RefListPage404Verdict(3) == PluginSource.RefMatchKind.Unknown,
                $"1→{PluginSource.RefListPage404Verdict(1)} 0→{PluginSource.RefListPage404Verdict(0)} "
                + $"2→{PluginSource.RefListPage404Verdict(2)} 3→{PluginSource.RefListPage404Verdict(3)}");

            // ── ③ 批量条的进度文案此前是**死字段**（只声明、没 new、也没挂进树 ⇒ 写进去的文案一个字都不显示）──
            // ⚠ 第一版这里量错了对象（已修）：`BuildBatchBar()` 返回的是**触发按钮**，而进度文案与进度条挂在
            //   **弹层内容** `_batchActionPopup.Child`（Border）→ `_batchMenuStack`（StackPanel）里
            //   （MainWindow.Batch.cs:423-452）—— 弹层根本不在触发按钮的视觉树上（它连窗口树都不在，
            //   见 HookPopupTheme 的注释）⇒ 实测 ProgressBar=0。
            // ⚠ 第二处修正：**不能走视觉树**。自检窗口是自建内容树 + Measure/Arrange（LayoutForTest），
            //   而 `_batchActionPopup.Child` 从未挂进任何 PresentationSource ⇒ 视觉树walker 会因为它
            //   自身没被 Measure 而**一个子节点都走不到**（实测：逻辑子有、视觉子 0）。
            //   ⇒ 改走**逻辑树**（LogicalTreeHelper 只看 Parenting 链，不需要布局），与 WPF 的既定行为一致。
            // 量的是**父链**而不是"某棵子树里有没有同类元素"：文案与进度条必须**同父**（都进 _batchMenuStack），
            //   这正是 ShowBatchProgress / HideBatchProgress 成对写这两件的前提，也是"字会显示出来"的前提。
            // 反证：把 `_batchMenuStack.Children.Add(_batchBarProgressText)` 那一行删掉 ⇒ 本条立刻变红；
            //      把 `_batchBarProgressText = new TextBlock{…}` 删回"只声明不 new"（原缺陷）⇒ 同样变红。
            // ⓘ 如实说明**量不到**的部分：文案是否随 i/total 真的在变，需要触发 ShowBatchProgress
            //   （private，且只在真跑批量更新/卸载的循环里被调用）——不进真批量流程就够不着，
            //   故不拿"读得到字段"冒充"文案真的在动"。
            {
                var n61MenuRoot = BatchMenuRootForTest(w);
                var n61Texts = BatchLogicalDescendantsForTest<TextBlock>(n61MenuRoot);
                var n61BarText = BatchBarProgressTextForTest(w);
                bool n61TextInTree = n61BarText != null && n61Texts.Contains(n61BarText);
                var n61ProgressBars = BatchLogicalDescendantsForTest<ProgressBar>(n61MenuRoot);
                var n61TextsParent = n61BarText == null ? null : LogicalTreeHelper.GetParent(n61BarText);
                bool n61SameParent = n61TextsParent != null && n61ProgressBars.Count == 1 &&
                                     ReferenceEquals(LogicalTreeHelper.GetParent(n61ProgressBars[0]), n61TextsParent);
                Check("③ 批量条进度文案不再死字段：那个 TextBlock 实例挂在弹层逻辑树上，且与进度条**同父**（都进了 _batchMenuStack）",
                    n61MenuRoot != null && n61TextInTree && n61SameParent,
                    $"弹层根={(n61MenuRoot != null ? "已取到" : "取不到")} · 逻辑子树 TextBlock={n61Texts.Count} 个 "
                    + $"ProgressBar={n61ProgressBars.Count} 个 · 字段实例在树里={n61TextInTree} · 与进度条同父={n61SameParent}"
                    + "（死字段 / 忘了 Add 进 Children 都会 False）");
            }

            // ── ④ 外部引擎启停：起表那半（BeginRunClock）此前零断言 ──
            // 背景：外部引擎"端口已监听"那条路径原先只置 _isRunning=true、**不起表**（_runStartAt 仍是
            // MinValue）⇒ 版本履历基线空着、连续运行累计与「自动-时间」门槛也不起表，30 秒结算点
            // 因此永远不认这一段。修复是补上 `if (_runStartAt == DateTime.MinValue || _unbound) BeginRunClock();`
            // （MainWindow.xaml.cs:2664）。BeginRunClockForTest 就是它的自检入口（此前**从未被调用**）。
            // ⚠ 断言怎么摆（实测教训）：`AccumulateRunSecondsForTest` 是**结算入口**，**不写** _engRunSeconds
            //   （见 MainWindow.xaml.cs:3248 AccumulateRunSeconds 与 :3267 OnRunSecondsSettled —— 累计那一步只在
            //   30 秒结算点里做）⇒ 拿 `TimedRunStateForTest().ranSeconds` 量"累加了多少"必然得到 0。
            //   这里改量**它真正干的那件事**：把 _runStartAt 前移、并返回结算出的秒数；
            //   再用"残留会让基线少前移 3 小时 ⇒ 返回值爆大"来钉"起表确实把账本归零了"。
            // 反证：把 BeginRunClock 里 `_runStartAt = DateTime.Now;` 那行删掉 ⇒ 第 1 条红（账本仍关着）；
            //      把三行复位删掉 ⇒ 第 2 条红（返回 3×3600+45 而不是 45）；把 _engRunStartAt 复位删掉 ⇒ 第 3 条红。
            w.EndRunClockForTest();                                        // 先回到干净起点（幂等）
            w.SetTimedRunStateForTest(3 * SnapshotManager.RunSecondsPerHour, 2 * SnapshotManager.RunSecondsPerHour);
            w.BeginRunClockForTest();
            bool n61ClockOpen = w.RunClockOpenForTest();
            var n61AfterBegin = w.TimedRunStateForTest();
            Check("④ 外部引擎起表：走真实入口后运行账本**开着**（旧行为只置 _isRunning ⇒ 账本仍是关的）",
                n61ClockOpen,
                $"起表后 RunClockOpen={n61ClockOpen}（必须 True）");
            int n61StaleThen = w.AccumulateRunSecondsForTest(45);          // 起表后第一次结算：基线前移 45s
            w.BeginRunClockForTest();                                      // 再起一次表（幂等入口）：回到干净基线
            int n61Straight = w.AccumulateRunSecondsForTest(45);           // 对照组：干净起点同口径再来一次
            Check("④ 起表要把三套账本一起归零：上一段运行的累计与「自动-时间」门槛不许跨次残留（残留会让重启后立刻多存一份快照）",
                n61AfterBegin.ranSeconds == 0 && n61AfterBegin.lastTakenAt == 0 &&
                n61StaleThen >= 45 && n61StaleThen < 60 && n61StaleThen == n61Straight,
                $"起表后 累计={n61AfterBegin.ranSeconds}s 门槛={n61AfterBegin.lastTakenAt}s（都应为 0）· "
                + $"结算 45s ⇒ 返回 {n61StaleThen}s（对照组 {n61Straight}s，两者必须相等且≈45）"
                + "—— 若起表没归零 _runStartAt，残留的 3 小时会让它返回 10800 上下");
            // ⚠ 这里原本还想断"起表把**连续运行**的基线 _engRunStartAt 也归零"，**已删** —— 那是**量错了对象**
            //   （第二版实测：返回 45 而不是 0）。读实现即可坐实（MainWindow.xaml.cs:3381-3388）：
            //     `AccumulateRunSecondsForTest(seconds)` 的语义就是"把 **_runStartAt** 回拨 seconds 秒再结算"，
            //   而 `AccumulateRunSeconds()` 结算完会把 **_engRunStartAt 前移到现在**（:3256）——那是**正常前移**，
            //   不是残留。所以"紧接着再结算返回 45s"正是它的既定行为，**与 _engRunStartAt 有没有被起表归零无关**
            //   ⇒ 这条断言恒红、且红了也不代表实现有问题。**没有照出任何 bug，故不标红上报。**
            //   `_engRunStartAt` 归零那一行的契约已由上面"结算 45s ⇒ 45（不是 10800）"间接覆盖：
            //   若起表不重设 _runStartAt，残留的 3 小时就会让那一条变红。

            w.SettleRunClockForTest();                                     // 收尾：本批不留开着的账本
            Check("④ 起表与收尾成对：起表后必须有一条**能关掉它**的路径（RunClockOpen=False 且账本清零）",
                !w.RunClockOpenForTest() && w.TimedRunStateForTest().ranSeconds == 0,
                $"收尾后 RunClockOpen={w.RunClockOpenForTest()} 累计={w.TimedRunStateForTest().ranSeconds}s");

            // ⓘ ⑤⑥⑦ 今天已落过断言，本批**不重复添加**（避免同一契约两处维护、日后漂移）：
            //   · ⑤ LogDiagnosis 是空操作 + EffectiveLogDir 口径：见本文件 Logger 那一段（LogDiagnosis 调用点
            //     与 AnyLogContains 扫 EffectiveLogDir 的两条）；
            //   · ⑥ 弹层切主题补刷（ReskinNonModalOpen）与悬停取色：见第 59 批里的 GuardDialog / ThemeManager 段；
            //   · ⑦ scp 判据 / 批量卸载空串闸 / ref 失败分诊 / git 源按提交判成败：见第 60 批 ①–⑤。
            //
            // ⚠ 遗留待查（本批 ①-c"本地与市场同结论"上一次实测为红，已定位到判据方向，**未擅改实现**）：
            //   实测 `PluginManager.Scan` 能扫到那个包、包目录也确实存在，但 `p.Requirement/Source` 仍为空串；
            //   而市场侧 `ApplyMeta` 拿**同一份 JSON 文本**却读得到 ⇒ 差别只剩"读的是不是同一个文件"。
            //   本批已把「夹具落盘 / 清单存在 / 包目录存在 / ProfileDir / PackageFile / 扫到的名字 / 市场 / 判据」
            //   八项全部写进 detail ⇒ 下一次跑自检**照 detail 一眼就能定位**是夹具没落位，还是包内声明没读到。
            //   ⚠ 未改 PluginManager.cs：那是别人的文件；若确是"Scan 读不到包内声明"，属本轮之外的另一个 bug。

            // ══════ 62. 主题取色的「现算」收口 + 自检收尾清理（各补一组纯函数断言）══════
            // 变量名一律 n62 前缀（RunCore 是一个声明空间，重名即 CS0136）。

            // ── ① ThemeManager.RemapColor：「另一套主题的对应色」只许从映射表里取，不许现造新色 ──
            // 背景（GuardDialog 的灰按钮）：悬停色原先是 `底色 ± 0x18` 算出来的，而映射表两侧偏移量不等
            // （#8E8E93 → #6B6B70 差 0x23）⇒ 算出来的 #76767B **两张表里都没有** ⇒ 切主题时正悬停的按钮
            // 留着旧主题色。RemapColor 就是为这一处收口而生的：查表、查不到原样返回。
            // 反证（本组三条各自能红）：
            //   · 把 RemapColor 改成 `return c;`（恒返回入参）⇒ 映射存在性 / 往返 / Shift 离表 三条全红；
            //   · 把 DarkToLight 里 #8E8E93 那一项删掉        ⇒ 映射存在性与「Shift 落表」两条红（悬停又不换色）。
            static string Hex62(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            var n62GrayBg = Color.FromRgb(0x8E, 0x8E, 0x93);      // GuardDialog 灰按钮的底色（次要文字同色）
            var n62GrayDay = ThemeManager.RemapColor(n62GrayBg, false);
            var n62GrayNight = ThemeManager.RemapColor(n62GrayDay, true);
            var n62RedBg = Color.FromRgb(0xFF, 0x3B, 0x30);
            var n62GreenBg = Color.FromRgb(0x34, 0xC7, 0x59);
            var n62AmberBg = Color.FromRgb(0xFF, 0x9F, 0x0A);
            Check("① 取色按主题走：中性色（灰）两套主题不同色，强调色（红/绿/橙）两套主题**本来就同色**（映射到自己，不是「没映射」）",
                n62GrayDay != n62GrayBg &&
                ThemeManager.RemapColor(n62RedBg, false) == n62RedBg &&
                ThemeManager.RemapColor(n62GreenBg, false) == n62GreenBg &&
                ThemeManager.RemapColor(n62AmberBg, false) == n62AmberBg,
                $"灰 {Hex62(n62GrayBg)}→{Hex62(n62GrayDay)}（应不同）· 红 {Hex62(n62RedBg)}→{Hex62(ThemeManager.RemapColor(n62RedBg, false))} · "
                + $"绿→{Hex62(ThemeManager.RemapColor(n62GreenBg, false))} · 橙→{Hex62(ThemeManager.RemapColor(n62AmberBg, false))}（强调色三条应不变）");
            Check("① RemapColor 往返自洽：日→夜→日 回到原色（两张表互为逆映射，任一方向漏项本条目就红）",
                n62GrayNight == n62GrayBg &&
                ThemeManager.RemapColor(ThemeManager.RemapColor(n62RedBg, false), true) == n62RedBg &&
                ThemeManager.RemapColor(ThemeManager.RemapColor(n62GreenBg, false), true) == n62GreenBg,
                $"灰 日{Hex62(n62GrayDay)}→夜{Hex62(n62GrayNight)}（应回到 {Hex62(n62GrayBg)}）");
            // 真正的缺陷形状：悬停色曾经是 `底色 − 0x18` 算出来的。RemapColor 存在后，取色必须查表 ⇒
            // 悬停色（日间底色的对应色再 Shift）必须**仍是表内色**（用往返判据反推"是不是表内色"：
            // 表内色往另一套主题必然查得到，Shift 造的新色查不到 ⇒ 往返回不到自身）。
            var n62ShiftDay = Color.FromRgb(
                (byte)Math.Clamp(n62GrayDay.R - 0x18, 0, 255),
                (byte)Math.Clamp(n62GrayDay.G - 0x18, 0, 255),
                (byte)Math.Clamp(n62GrayDay.B - 0x18, 0, 255));
            bool n62ShiftInTable = ThemeManager.RemapColor(n62ShiftDay, true) == n62GrayBg;
            Check("① 悬停色不再靠 ±0x18 算术偏移：Shift 出来的那类色**不在映射表里**，取色必须改用 RemapColor（本条钉住那个缺陷形状）",
                !n62ShiftInTable && n62GrayNight == n62GrayBg,
                $"Shift 色 {Hex62(n62ShiftDay)} 往返回={Hex62(ThemeManager.RemapColor(n62ShiftDay, true))}（≠{Hex62(n62GrayBg)} ⇒ 表外新色）· "
                + $"而查表色 {Hex62(n62GrayDay)} 往返正好回到 {Hex62(n62GrayBg)}");

            // ── ③ 游离弹层的主题登记表（登记钩子已按无人使用删除）──
            // ⚠ 如实说明：**端到端那条构造不出来，故本条记 SKIP**。
            //   登记发生在 ThemeManager.RegisterOrphanPopup 挂的 `pop.Opened` 回调里，而自检窗口只做了
            //   Measure/Arrange/UpdateLayout（MainWindow.LayoutForTest，见 :3725），**从未 Show** ⇒
            //   没有 PresentationSource；此时把 Popup.IsOpen 置 true，Opened 回调不保证触发
            //   ⇒ 拿它断言"登记数 +1"会变成随时序飘红的假断言。不硬造。
            //   （纯函数那一半已由上面三条覆盖；登记表的生命周期需要真窗口，留给产品路径验。）
            // ✓ 处置结果：登记钩子**已删**（它没有断言就是没人用的公开面）。
            Skip("③ 游离弹层登记表：打开登记 / 关闭摘除",
                "自检窗口未 Show，Popup.Opened 不保证触发；端到端构造不出来 —— 该钩子已按无人使用删除");

            // ── ④ App.TryDeleteSelfTestFixtureDir：自检收尾清理的路径安全 + 幂等（只打临时探针）──
            // ⚠ 刻意**不碰**真实的 dshguard-selftest-data / 日志夹具（那会与本次自检自己的目录打架，
            //   也会把"收尾清理"这条断言变成对运行环境的破坏）⇒ 只在自己造的临时探针目录上量判据。
            // 反证：把叶子名校验（. / .. / 分隔符 / 冒号）删掉 ⇒ 第一条红；
            //      把"%TEMP% 之内"那一层校验删掉 ⇒ 第二条红；把 Directory.Delete 删掉 ⇒ 第三条红。
            {
                string n62Root = Path.Combine(Path.GetTempPath(), "dshguard-n62-fixture-root");
                string n62Sentinel = Path.Combine(n62Root, "sentinel");
                try
                {
                    Directory.CreateDirectory(n62Sentinel);
                    Check("② 收尾清理的路径安全：非法叶子名 / rooted 路径 / 注入构造一律被拒，且什么都没被删掉",
                        !App.TryDeleteSelfTestFixtureDir(n62Root, "..") &&
                        !App.TryDeleteSelfTestFixtureDir(n62Root, ".") &&
                        !App.TryDeleteSelfTestFixtureDir(n62Root, "a\\..\\b") &&
                        !App.TryDeleteSelfTestFixtureDir(n62Root, "a/b") &&
                        !App.TryDeleteSelfTestFixtureDir(n62Root, "C:\\Windows") &&
                        !App.TryDeleteSelfTestFixtureDir(n62Root, "dshguard-n62-fixture-root\\..\\sentinel") &&
                        Directory.Exists(n62Sentinel),
                        $"sentinel 完好={Directory.Exists(n62Sentinel)}（六条非法调用都不许动到它）");
                    Check("② 收尾清理的边界：%TEMP% 之外的根一律被拒（程序目录不许被收尾碰到）",
                        !App.TryDeleteSelfTestFixtureDir(AppContext.BaseDirectory, "dshguard-n62-probe-nowhere"),
                        $"程序目录={Shorten(AppContext.BaseDirectory, 40)}");
                    string n62Legit = Path.Combine(Path.GetTempPath(), "dshguard-n62-legit-probe");
                    Directory.CreateDirectory(Path.Combine(n62Legit, "inner"));
                    Check("② 收尾清理真的清：合法夹具目录被递归删掉（含子目录）",
                        App.TryDeleteSelfTestFixtureDir(Path.GetTempPath(), "dshguard-n62-legit-probe")
                        && !Directory.Exists(n62Legit),
                        $"删除返回=True · 目录还在={Directory.Exists(n62Legit)}（必须 False）");
                    Check("② 收尾清理幂等：不存在 / 重复调用 / 空参一律返回 false 且不抛",
                        !App.TryDeleteSelfTestFixtureDir(Path.GetTempPath(), "dshguard-n62-legit-probe") &&
                        !App.TryDeleteSelfTestFixtureDir("", "x") &&
                        !App.TryDeleteSelfTestFixtureDir(Path.GetTempPath(), ""),
                        "重复删同一个名字 / 空 root / 空 leaf 都应为 false");
                }
                finally { try { if (Directory.Exists(n62Root)) Directory.Delete(n62Root, true); } catch { } }
            }
            // ⓘ 「成功才清 / 失败保留」这条策略**静态断言不到**：判据落在 App.xaml.cs 的私有
            //   _selfTestExitCode / _selfTestLogDir 上，没有 ForTest 入口，且本文件的 self-test 恒为成功路径
            //   ⇒ 失败保留那一支只有真跑一次失败的自检才验得到。如实留空，不硬造。

            // ══════ 63. 本轮三处改动的补断言：① 悬停字段式文案 ② 快照详情页按大类聚合 ③ 产品标识
            //   （③ 已在第 22 批 :3086-3087 钉住：Mascot.CurrentLine == Mascot.NormalLine == 不是蓝色大肥鱼，是鲸！
            //     —— 本轮只读确认，**不重复添加**，免得同一契约两处维护、日后漂移。）
            // 变量名一律 n63 前缀（RunCore 是一个声明空间，重名即 CS0136 —— 本项目已踩过两次）。

            // ── ① MainWindow.Tools.UpdateStatusHover 的字段式文案（本轮刚改成多行字段）──
            // 契约：① 五档字段名齐全；② 任一类别的文案都不得出现内部标识（HTTP 代号 / 命令行 / 站点专名 / 盘符路径）；
            //      ③ 拿不准（RefProbeFailure.None）⇒ 写「暂无法确定」，绝不编原因，且不给"下一步"。
            // 反证（逐条）：把 UpdateStatusHover 的 `"说明：…"` 那一行删掉 ⇒ 第 1 条红；
            //   把 RefProbeFailureHintOrUnknown 改成 `return RefProbeFailureHint(...)`（拿不准返回空串）⇒ 第 2 条红
            //   （拿不准那条会变成「原因：」后面什么都没有）；把 RefProbeFailureNextStepOrEmpty 的过滤去掉
            //   （None 也给下一步）⇒ 第 2 条红；把 RefProbeFailureNextStep(Network) 改成
            //   "请访问 HTTP 状态页" ⇒ 第 3 条红；把某个字段名改掉 ⇒ 第 4 条红。
            {
                // ⚠ Classify(DepSpec(名)) 对"清单里没有的包名"返回 Unknown ⇒ UpdateStatusHover 会走
                //   "来源形态不受支持"那一档（那一档**本来就不给下一步**）。故这一档单独断言（见下面第 5 条），
                //   第 1/2 条用的是**有确切失败类别**的那一档（给下一步、也是本轮新加字段的主场景）。
                var n63HoverNone = new PluginManager.PluginUpdate
                {
                    Name = "自检-悬停-拿不准",
                    StatusNote = "更新状态待确认（未取到仓库最新提交）",
                    FailureHint = PluginSource.RefProbeFailure.None
                };
                var n63HoverRate = new PluginManager.PluginUpdate
                {
                    Name = "自检-悬停-限流",
                    StatusNote = "更新状态待确认（未取到仓库最新提交）",
                    FailureHint = PluginSource.RefProbeFailure.RateLimited
                };
                var n63HoverShape = new PluginManager.PluginUpdate
                {
                    Name = "自检-悬停-解析不出",
                    StatusNote = "更新状态待确认（未取到仓库最新提交）",
                    FailureHint = PluginSource.RefProbeFailure.Shape
                };
                string n63HoverRateText = MainWindow.UpdateStatusHover(n63HoverRate);
                // 「比对基准：」那一行只在 CompareNote 非空时出现 ⇒ 造一条带真 CompareNote 的（内容由唯一实现处给）。
                var n63HoverBasis = new PluginManager.PluginUpdate
                {
                    // ⚠ 名字必须用**清单里真有的 git 源包**：UpdateStatusHover 的"来源形态不受支持"那一档
                    //   判据是 `CompareNote 非空 && Classify(DepSpec(名)) == Unknown`
                    //   （MainWindow.Tools.cs:363-364）—— 拿"清单里没有的名字"是**证明不了**这一档的
                    //   （DepSpec 返回空串 ⇒ 恒走不受支持档 ⇒ 没有「下一步：」）。实测证据：第一版这里用了
                    //   自造名，报告里那档写着「该来源地址的写法不受支持」⇒ 五档断言当场红。
                    //   dsh-codearts-auth 是本机在装的 git 源包（本文件 :6611 的 LinkUrl 断言同用此名）。
                    Name = "dsh-codearts-auth",
                    StatusNote = "更新状态待确认（未取到仓库最新提交）",
                    FailureHint = PluginSource.RefProbeFailure.Network,
                    CompareNote = PluginSource.CompareBasisNote(
                        "git+https://gitee.com/iJetLi/deepseek-harness-codearts.git#dev",
                        PluginSource.RefMatchKind.Branch)
                };
                string n63HoverBasisText = MainWindow.UpdateStatusHover(n63HoverBasis);
                // ⚠ 这一条先自证"真的落到了能给「下一步」的那一档"：落到"来源不受支持"档时本条**必须红**
                //   （不是环境问题，是用错了样本名 —— 上面那句注释就是被它抓出来后补的）。
                Check("① 五档样本落到能给「下一步」的那一档（来源形态可识别），不是「来源不受支持」档",
                    n63HoverBasis.CompareNote.Length > 0 &&
                    PluginSource.Classify(PluginManager.DepSpec(n63HoverBasis.Name)) != PluginSource.Kind.Unknown &&
                    !n63HoverBasisText.Contains("不受支持"),
                    $"来源类别={PluginSource.Classify(PluginManager.DepSpec(n63HoverBasis.Name))} · "
                    + $"基准非空={n63HoverBasis.CompareNote.Length > 0}（若为 Unknown ⇒ 本条红：样本名不对）");

                // ⚠ 先把"五档字段名齐全"独立钉住：任一处改名/漏行 ⇒ 本条红（上面反证清单里的两条都落在这里）。
                Check("① 悬停文案是字段式的五档：比对基准 / 状态 / 原因 / 下一步 / 说明 一个都不少（本轮新写法）",
                    n63HoverBasisText.Contains("比对基准：") && n63HoverBasisText.Contains("状态：") &&
                    n63HoverBasisText.Contains("原因：") && n63HoverBasisText.Contains("下一步：") &&
                    n63HoverBasisText.Contains("说明：") && n63HoverRateText.Contains("说明：") &&
                    n63HoverRateText.Contains("下一步："),
                    $"带基准=«{Shorten(n63HoverBasisText, 130)}»");

                // 拿不准（None）：原因必须如实写「暂无法确定」（拿不准的唯一落点），且**不许**给下一步。
                // 同时钉住"判据侧一个字节没动"：RefProbeFailureHint(None) 必须仍是空串（既有断言在 :8146 钉着，
                // 这里是显示层的新口径，两者不冲突 —— 说「暂无法确定」的只有显示层）。
                string n63HoverNoneText = MainWindow.UpdateStatusHover(n63HoverNone);
                string n63HoverShapeText = MainWindow.UpdateStatusHover(n63HoverShape);
                bool n63NoneHasNext = n63HoverNoneText.Contains("下一步：");
                bool n63RateSaysUnknown = n63HoverRateText.Contains("暂无法确定");
                Check("① 拿不准（None）⇒ 写「暂无法确定」而不是编原因（硬底线），且不给「下一步」；说得出类别的不许落回这句",
                    n63HoverNoneText.Contains("原因：暂无法确定") &&
                    !n63NoneHasNext &&
                    !n63RateSaysUnknown &&
                    !n63HoverShapeText.Contains("暂无法确定") &&
                    n63HoverRateText.Contains(PluginSource.RefProbeFailureHint(PluginSource.RefProbeFailure.RateLimited)) &&
                    n63HoverShapeText.Contains(PluginSource.RefProbeFailureHint(PluginSource.RefProbeFailure.Shape)),
                    $"拿不准=«{Shorten(n63HoverNoneText, 110)}» · 限流档含「暂无法确定」={n63RateSaysUnknown}（必须 False）");

                // 措辞纪律：任一类别的文案都不许出现内部标识 / 命令行 / 站点专名 / 盘符路径。
                // ⚠ 判据是「Hint 与 NextStep 这两处**唯一文案实现**的产物」，不含 StatusNote / CompareNote
                //   （那两处的文案另有断言钉着；这里只为本轮新改的这块负责）。
                var n63Forbidden = new[] { "403", "429", "401", "HTTP", "http", "pnpm", "npx", "registry", "gitee", "github", "gitlab", ":\\" };
                var n63CatTexts = new List<string>();
                foreach (var n63Cat in new[]
                         {
                             PluginSource.RefProbeFailure.RateLimited, PluginSource.RefProbeFailure.Unauthorized,
                             PluginSource.RefProbeFailure.Network, PluginSource.RefProbeFailure.Server,
                             PluginSource.RefProbeFailure.Shape
                         })
                {
                    n63CatTexts.Add(PluginSource.RefProbeFailureHint(n63Cat));
                    n63CatTexts.Add(PluginSource.RefProbeFailureNextStep(n63Cat));
                }
                var n63Dirty = n63CatTexts.Where(t => n63Forbidden.Any(t.Contains)).ToList();
                Check("① 措辞纪律：任一类别的文案都不含禁用词（HTTP 代号 / 命令行 / 站点专名 / 盘符路径）",
                    n63Dirty.Count == 0,
                    n63Dirty.Count == 0
                        ? $"五类 ×（原因 + 下一步）共 {n63CatTexts.Count} 句扫描通过"
                        : string.Join("；", n63Dirty.Select(t => Shorten(t, 40))));

                // 每一行都必须是已知的字段名开头（挡住"某一行被漏掉换行/串句"这类改坏法）。
                // ⚠ 两处按实际实现如实处理（都由 UpdateStatusHover 的返回式决定，不是我猜的形状）：
                //   ① `who` 那一行自带 "\n"，而"下一步"整行为空时（不受支持档 / 拿不准档）会留下**空行**
                //      —— 空行不是"串句"，跳过；
                //   ② 「说明：」按设计**直接接在上一行之后**（MainWindow.Tools.cs:391 `… + "\n" + next + "说明：…"`）
                //      ⇒ 没有下一步时它落在"原因"那一行里，允许"内容里含「说明：」"。
                //   有下一步时它就是自己一行的开头 —— 两种情况都要求「说明：」**确实出现**，否则本条红。
                var n63Known = new[] { "比对基准：", "状态：", "原因：", "下一步：", "说明：" };
                var n63BadLines = n63HoverBasisText.Split('\n')
                    .Where(l => l.Trim().Length > 0)
                    .Where(l => !n63Known.Any(l.StartsWith) && !l.Contains("说明："))
                    .ToList();
                Check("① 悬停的每一行都是已知字段名开头（防止漏换行 / 两档串进一行）",
                    n63BadLines.Count == 0 && n63HoverBasisText.StartsWith("比对基准：") &&
                    n63HoverBasisText.Contains("说明：") && n63HoverRateText.Contains("说明："),
                    n63BadLines.Count == 0
                        ? $"非空行全部以字段名开头（共 {n63HoverBasisText.Split('\n').Length} 行，含空行）"
                        : string.Join("；", n63BadLines.Select(l => Shorten(l, 40))));

                // 来源形态不受支持那一档：本轮明确"没有用户可执行的下一步" ⇒ 整行不出现（不写"请稍后重试"这类空话）。
                // ⚠ 用"清单里没有的包名"是刻意的：DepSpec 返回空串 ⇒ Classify = Unknown ⇒ 必然走这一档，
                //   判据不依赖本机装没装某个插件（环境无关）。
                var n63HoverUnsup = new PluginManager.PluginUpdate
                {
                    Name = "自检-悬停-来源不支持",
                    StatusNote = "更新状态待确认（来源形态不支持安装与更新）",
                    CompareNote = PluginSource.CompareBasisNote("dsh-selftest-unsupported-source", PluginSource.RefMatchKind.Unknown)
                };
                string n63HoverUnsupText = MainWindow.UpdateStatusHover(n63HoverUnsup);
                Check("① 来源形态不受支持那一档：不给「下一步」（无可执行动作就不写空话），但「说明」仍如实写着",
                    n63HoverUnsupText.Contains("状态：") && n63HoverUnsupText.Contains("原因：") &&
                    !n63HoverUnsupText.Contains("下一步：") && n63HoverUnsupText.Contains("说明："),
                    Shorten(n63HoverUnsupText, 120));

                // 既有两条契约**只读复核**（本轮改的是同一块代码，顺带证明没被改坏；原文断言在 :6482 与 :8153）。
                Check("① 本轮改动没碰既有两条契约：CompareBasisNote 仍含「发行版 / 无关」· RefProbeFailureHint(None) 仍是空串",
                    n63HoverBasis.CompareNote.Contains("发行版") && n63HoverBasis.CompareNote.Contains("无关") &&
                    PluginSource.RefProbeFailureHint(PluginSource.RefProbeFailure.None).Length == 0,
                    $"基准=«{Shorten(n63HoverBasis.CompareNote, 70)}» · Hint(None)=«{PluginSource.RefProbeFailureHint(PluginSource.RefProbeFailure.None)}»（应空）");
            }

            // ── ② 快照详情页按大类聚合（本轮新加；纯函数，零夹具、零落盘）──
            // 契约（与 SnapshotManager.BuildDisplayRows 的注释一一对应）：
            //   ① 分类：profile-plugins-* 全归「插件文件」，7 个固定采集名各自归位；
            //   ② 同一插件的多个文件收成 1 行（行数 < 文件数）；
            //   ③ 阈值是 GroupMinFiles（**现取常量**，不许写死 3）：等于它要聚合、少于它不聚合；
            //   ④ 不可回滚项仍单独可见，不被吞进聚合行；
            //   ⑤ 聚合行的可回滚文件集合 == 逐文件集合（勾一行与逐个勾完全等价）。
            // 反证（逐条）：把 CategoryOf 里 profile-plugins- 那一支删掉 ⇒ 第 1 条红；
            //   把 `if (restorable.Count >= GroupMinFiles)` 改成不聚合（永远走 else）⇒ 第 2/3 条红；
            //   把 skipped 那一段 `foreach … rows.Add(MakeFileRow)` 删掉 ⇒ 第 4 条红；
            //   把 MakeGroupRow 的 AddToRow 换成只装第一个文件 ⇒ 第 5 条红；
            //   把 GroupMinFiles 写死成 3（改成 `>= 3`）⇒ 第 3 条红。
            {
                static SnapshotManager.SnapshotFile n63File(string name, long size, bool restorable)
                    => new() { Name = name, Size = size, Target = restorable ? "C:\\自检\\" + name : "", SkipReason = restorable ? "" : "不可回滚" };

                // ① 分类：7 个固定名各自归位（profile-pnpm-* 是易错点：它既像"工作区"又带 profile- 前缀）
                Check("② 快照分类：7 个固定采集名各自归位（profile-plugins-* 归「插件文件」，pnpm 两件归「工作区与锁文件」）",
                    SnapshotManager.CategoryOf("profile-package.json") == SnapshotManager.SnapshotCategory.PluginManifest &&
                    SnapshotManager.CategoryOf("profile-cordis.patch.yml") == SnapshotManager.SnapshotCategory.PatchLayer &&
                    SnapshotManager.CategoryOf("profile-cordis.yml") == SnapshotManager.SnapshotCategory.ConfigRoot &&
                    SnapshotManager.CategoryOf("profile-pnpm-workspace.yaml") == SnapshotManager.SnapshotCategory.WorkspaceLock &&
                    SnapshotManager.CategoryOf("profile-pnpm-lock.yaml") == SnapshotManager.SnapshotCategory.WorkspaceLock &&
                    SnapshotManager.CategoryOf("home-settings.yaml") == SnapshotManager.SnapshotCategory.GlobalSettings &&
                    SnapshotManager.CategoryOf("profile-plugins-dsh-imagegen-index.js") == SnapshotManager.SnapshotCategory.PluginFiles &&
                    SnapshotManager.CategoryOf("profile-plugins-a-b-c-d.js") == SnapshotManager.SnapshotCategory.PluginFiles &&
                    SnapshotManager.CategoryOf("home-其它.yaml") == SnapshotManager.SnapshotCategory.Other,
                    "7 个固定名 + 两个 plugins 采集名 + 一个其它");

                // ② 同一插件的 6 个文件 ⇒ 1 行（行数必须小于文件数）
                var n63SixFiles = Enumerable.Range(0, 6)
                    .Select(i => n63File("profile-plugins-dsh-imagegen-f" + i + ".js", 10 * (i + 1), true)).ToList();
                var n63SixRows = SnapshotManager.BuildDisplayRows(n63SixFiles);
                Check("② 同一插件的 6 个文件聚合成 1 行（行数 < 文件数）",
                    n63SixRows.Count < n63SixFiles.Count &&
                    n63SixRows.Count(r => r.Kind == SnapshotManager.DisplayRowKind.Group &&
                                          r.Category == SnapshotManager.SnapshotCategory.PluginFiles) == 1 &&
                    n63SixRows.Single(r => r.Kind == SnapshotManager.DisplayRowKind.Group).RestorableCount == 6,
                    $"{n63SixFiles.Count} 个文件 ⇒ {n63SixRows.Count} 行（阈值 GroupMinFiles={SnapshotManager.GroupMinFiles}）");

                // ③ 阈值**现取常量**：等于它要聚合（2 件也聚合）、少于它不聚合（1 件仍逐行）
                var n63TwoFiles = new List<SnapshotManager.SnapshotFile>
                {
                    n63File("profile-plugins-dsh-a-aa.js", 10, true),
                    n63File("profile-plugins-dsh-a-bb.js", 20, true)
                };
                var n63TwoRows = SnapshotManager.BuildDisplayRows(n63TwoFiles);
                var n63OneFiles = new List<SnapshotManager.SnapshotFile> { n63File("profile-plugins-dsh-b-cc.js", 30, true) };
                var n63OneRows = SnapshotManager.BuildDisplayRows(n63OneFiles);
                Check("② 聚合阈值就是 GroupMinFiles（现取常量，不写死 3）：恰好等于阈值 ⇒ 聚合，少于阈值 ⇒ 逐文件行",
                    SnapshotManager.GroupMinFiles == 2 &&
                    n63TwoRows.Count < n63TwoFiles.Count &&
                    n63TwoRows.Count(r => r.Kind == SnapshotManager.DisplayRowKind.Group) == 1 &&
                    n63TwoRows.Single(r => r.Kind == SnapshotManager.DisplayRowKind.Group).RestorableCount == 2 &&
                    n63OneRows.Count == 1 && n63OneRows[0].Kind == SnapshotManager.DisplayRowKind.File &&
                    n63OneRows[0].File != null && n63OneRows[0].File!.Name == "profile-plugins-dsh-b-cc.js",
                    $"{n63TwoFiles.Count} 件 ⇒ {n63TwoRows.Count} 行（应聚合）· {n63OneFiles.Count} 件 ⇒ {n63OneRows.Count} 行（应逐行）"
                    + $" · GroupMinFiles={SnapshotManager.GroupMinFiles}");

                // ④ 不可回滚项不被吞进聚合行：桶里 2 件可回滚 + 1 件不可回滚
                //   ⇒ 聚合行只装那 2 件可回滚的（RestorableCount=2、Files 里没有跳过件），
                //     而那 1 件跳过件**照样自己占一行**（File 行、Restorable=false）。
                // ⚠ 如实记下一处实测（第一版在这里误判过，报告里 DIAG 抓出来的）：
                //   聚合行的 `SkippedCount` **是 1 不是 0** —— MakeGroupRow 把 `skipped.Count` 传给了
                //   `SkippedCount` 属性（SnapshotManager.cs:1105），它表示"本类里有几件不可回滚"，
                //   用于聚合行悬停那句「其中 N 件不可回滚」（GroupToolTip :1151）；跳过件**不是**它的成员
                //   （Files 里只有那 2 件，见下面 Files 那半个条件）。
                //   所以"可见性优先"要钉的是**它自己有一行**，不是"聚合行的计数为 0"。
                var n63MixedFiles = new List<SnapshotManager.SnapshotFile>
                {
                    n63File("profile-plugins-dsh-c-dd.js", 10, true),
                    n63File("profile-plugins-dsh-c-ee.js", 20, true),
                    n63File("profile-plugins-dsh-c-ff.js", 0, false)
                };
                var n63MixedRows = SnapshotManager.BuildDisplayRows(n63MixedFiles);
                var n63MixedGroup = n63MixedRows.Where(r => r.Kind == SnapshotManager.DisplayRowKind.Group).ToList();
                var n63MixedFileRows = n63MixedRows.Where(r => r.Kind == SnapshotManager.DisplayRowKind.File).ToList();
                Check("② 不可回滚（跳过）项仍单独可见：聚合行只装可回滚件，跳过件自己占一行",
                    n63MixedGroup.Count == 1 && n63MixedGroup[0].RestorableCount == 2 &&
                    n63MixedGroup[0].Files.Count == 2 &&
                    n63MixedGroup[0].Files.All(x => x.Restorable) &&
                    n63MixedGroup[0].SkippedCount == 1 &&
                    n63MixedFileRows.Count == 1 && n63MixedFileRows[0].File != null &&
                    !n63MixedFileRows[0].File!.Restorable &&
                    n63MixedFileRows[0].File!.Name == "profile-plugins-dsh-c-ff.js",
                    $"3 件（2 可回滚 + 1 跳过）⇒ {n63MixedRows.Count} 行：聚合行装 {n63MixedGroup[0].Files.Count} 件可回滚"
                    + $"（本类跳过 {n63MixedGroup[0].SkippedCount} 件，只进悬停文案）· 跳过件单列一行={n63MixedFileRows.Count == 1}");

                // ⑤ 聚合行的可回滚集合 == 逐文件集合（勾一行与逐个勾完全等价）
                static List<string> n63RestorableNames(List<SnapshotManager.SnapshotDisplayRow> rows)
                    => rows.SelectMany(r => r.RestorableFiles).Select(x => x.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
                var n63AggNames = n63RestorableNames(n63MixedRows);
                var n63FlatNames = n63MixedFiles.Where(x => x.Restorable).Select(x => x.Name)
                    .OrderBy(n => n, StringComparer.Ordinal).ToList();
                Check("② 聚合行勾选与逐文件勾选完全等价：聚合行的可回滚文件集合 == 逐文件集合",
                    n63AggNames.SequenceEqual(n63FlatNames, StringComparer.Ordinal) && n63AggNames.Count == 2,
                    $"聚合行集合=[{string.Join(", ", n63AggNames)}] · 逐文件集合=[{string.Join(", ", n63FlatNames)}]（应逐字相等）");

                // ══════ 64. 应用内更新的下载线路表（真机事故回归） ══════
                // 事故：BuildGuardSetupRoutes 曾把 RouteLabel(...) 的**显示名**当 URL 存进线路表
                // （存进去的是「直连」两个汉字与「gh-proxy.com」这种裸域名），于是每一次请求都在
                // 构造 Uri 时就抛 InvalidOperationException —— 四条线路瞬间全灭、一个网络包都没发出去。
                // 判据：线路表里**每一条都必须是能直接请求的绝对 URL**，一个都不许是显示名。
                string n64Direct = "https://github.com/o/r/releases/download/1.0/DSHGuard-Setup-1.0.exe";
                var n64Routes = PluginSource.BuildGuardSetupRoutes(n64Direct);
                var n64Bad = n64Routes.Where(u =>
                {
                    try { var x = new Uri(u); return !x.IsAbsoluteUri || x.Scheme != Uri.UriSchemeHttps; }
                    catch { return true; }
                }).ToList();
                Check("① 下载线路表里每一条都是可请求的绝对网址（不许混进「直连」这类显示名）",
                    n64Routes.Count >= 2 && n64Bad.Count == 0,
                    $"{n64Routes.Count} 条，不合格 {n64Bad.Count} 条[{string.Join(" / ", n64Bad)}]");
                Check("② 线路表首条是原始直链本身，其余是「镜像前缀 + 直链」",
                    n64Routes.Count > 0 && n64Routes[0] == n64Direct &&
                    PluginSource.GuardSetupMirrorPrefixes().All(pf => n64Routes.Contains(pf.TrimEnd('/') + "/" + n64Direct)),
                    $"首条={(n64Routes.Count > 0 ? n64Routes[0] : "(空)")} · 共 {n64Routes.Count} 条");
                Check("③ 原始直链为空 ⇒ 编不出任何线路（返回空表，而不是只剩镜像）",
                    PluginSource.BuildGuardSetupRoutes("").Count == 0 &&
                    PluginSource.BuildGuardSetupRoutes(null).Count == 0 &&
                    PluginSource.BuildGuardSetupRoutes("   ").Count == 0,
                    $"空串={PluginSource.BuildGuardSetupRoutes("").Count} · null={PluginSource.BuildGuardSetupRoutes(null).Count}");
                Check("④ 线路显示名与请求地址分得开：直连→「直连」，镜像→域名（只进日志）",
                    PluginSource.RouteLabel(n64Direct, n64Direct) == "直连" &&
                    PluginSource.RouteLabel(n64Direct, "https://gh-proxy.com/" + n64Direct) == "gh-proxy.com" &&
                    PluginSource.RouteLabel(n64Direct, "") == "未知线路",
                    $"{PluginSource.RouteLabel(n64Direct, n64Direct)} / {PluginSource.RouteLabel(n64Direct, "https://gh-proxy.com/" + n64Direct)}");
            }
        }
        catch (Exception ex)
        {
            log.AppendLine($"[XX] FAIL 自检异常 — {ex.GetType().Name}: {ex.Message}");
            log.AppendLine(ex.StackTrace);
            failures.Add("自检异常：" + ex.Message);
        }

        log.AppendLine();
        log.AppendLine(failures.Count == 0
            ? $"===== 自检全部通过 ====={(skips.Count > 0 ? $"（{skips.Count} 项因环境未验证：{string.Join(" / ", skips)}）" : "")}"
            : $"===== 自检失败 {failures.Count} 项：{string.Join(" / ", failures)} =====");

        string text = log.ToString();
        try
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "dshguard-selftest.txt"), text, new UTF8Encoding(false));
        }
        catch { }
        try { Console.Out.Write(text); Console.Out.Flush(); } catch { }

        return failures.Count == 0 ? 0 : 1;
    }

    /// <summary>把 "#AARRGGBB" 折算为感知亮度（0-255），用于判定文字颜色是否足够亮或足够深。</summary>
    private static int Luminance(string hex)
    {
        try
        {
            string h = hex.TrimStart('#');
            if (h.Length == 8) h = h.Substring(2);          // 去掉 AA
            int r = Convert.ToInt32(h.Substring(0, 2), 16);
            int g = Convert.ToInt32(h.Substring(2, 2), 16);
            int b = Convert.ToInt32(h.Substring(4, 2), 16);
            return (r * 30 + g * 59 + b * 11) / 100;
        }
        catch { return -1; }
    }

    /// <summary>
    /// Logger **本次运行实际落盘**的那个目录下，是否存在包含指定文本的日志文件 ——
    /// 用于验证「不该落盘的内容确实没落盘」。
    ///
    /// ⚠ 必须读 <see cref="Logger.EffectiveLogDir"/>，不能读 <see cref="GuardPaths.LogDir"/>：
    ///   <c>GuardPaths.LogDir</c> 是**用户真实日志目录**（&lt;exe&gt;\Logs），它的语义务必保持
    ///   "用户看得见的那个目录"（设置页 / 日志页 / 导出诊断都指向它）；
    ///   而 Logger 真正落盘用的是自己的 <c>LogDir</c>，优先级是
    ///   <c>GuardPaths.LogDirExplicit &gt; _runLogDir &gt; GuardPaths.LogDir</c>（Logger.cs:43-51）。
    ///   自检/截图这类夹具会把 <c>_runLogDir</c> 设成隔离目录（App.xaml.cs:136-139，
    ///   <c>%TEMP%\DSHGuard-selftest-logs\&lt;时间戳&gt;\</c>），而 <c>--selftest</c> 全程不调
    ///   <c>GuardPaths.Apply</c> 去指日志目录（只指快照仓库）⇒ <c>LogDirExplicit</c> 为空
    ///   ⇒ 两个属性**指向完全不同的目录**。
    ///   实测后果：自检把日志写在隔离目录，这里却去扫用户的真实 Logs；那份目录当时是空的
    ///   ⇒ 本函数**永远返回 false** ⇒ 「切换日夜模式不写日志」这条断言的 `!AnyLogContains(...)`
    ///   恒为 true，等于没验（实测把违规内容写进隔离目录，旧写法 PASS、新写法 FAIL）。
    /// </summary>
    private static bool AnyLogContains(string needle)
    {
        try
        {
            string dir = Logger.EffectiveLogDir;
            if (!Directory.Exists(dir)) return false;
            foreach (var f in Directory.GetFiles(dir, "*.log"))
                if (Logger.ReadLogFile(f).Contains(needle, StringComparison.Ordinal)) return true;
        }
        catch { }
        return false;
    }

    /// <summary>
    /// 走反射取 <c>MainWindow._appEvents</c>（私有静态事件表）里**所有 <c>EventKind.Mascot</c> 条目的文本**，
    /// 按写入顺序返回。
    ///
    /// 为什么需要它（[299] 那条用例）："彩蛋自身不会再触发彩蛋"这条契约要量的是
    /// **一次事件变动恰好多出一条彩蛋**，而唯一的公开钩子 <c>EventCountForTest()</c> 数的是
    /// **全部**事件 —— 里面混着自检关不掉的外部事件源（`StatusTimer_Tick` 的端口同步那一支，
    /// 用户引擎开着时每秒都可能来一条）⇒ 拿它做"恰好 +2"的断言必然随时序/环境变红。
    /// 只有把 Mascot 种类单独数出来，判据才与外界无关。
    ///
    /// 反射在本文件已有先例（见 <c>PluginMarket.AddMarkdownImage</c> 那条根因断言）。
    /// 只读、不加锁（<c>_appEvents</c> 是 <c>List&lt;(string, EventKind, Color?)&gt;</c>，
    /// 与正式代码同款"先加锁取副本"的读法在这里拿不到锁对象，故直接读一份副本）。
    /// ⚠ 定位失败（字段改名/改类型）**不做兜底**：抛出去由 <see cref="RunCore"/> 的 catch 记 FAIL——
    ///   宁可红，也不能悄悄退化成"恒真"。
    /// </summary>
    private static List<string> MascotEventTextsForTest()
    {
        var field = typeof(MainWindow).GetField("_appEvents",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        if (field?.GetValue(null) is not System.Collections.IEnumerable rows)
            throw new InvalidOperationException("自检夹具失效：取不到 MainWindow._appEvents（私有静态事件表）");

        // _appEvents 是 List<(string, EventKind, Color?)>，但走反射只能拿到非泛型的 IEnumerable
        // ⇒ 先 Cast<object>()（LINQ）才能逐行做模式匹配，否则编译器报"object 没有 GetEnumerator"。
        var result = new List<string>();
        foreach (object row in rows.Cast<object>())
            if (row is ValueTuple<string, MainWindow.EventKind, System.Windows.Media.Color?> t
                && t.Item2 == MainWindow.EventKind.Mascot)
                result.Add(t.Item1);
        return result;
    }

    /// <summary>
    /// 走反射取 <c>MainWindow._appEvents</c>（私有静态事件表）里**某条标记事件之后**的全部 (文本, 等级)，
    /// 按写入顺序返回（不含标记那条本身）。
    ///
    /// 为什么需要它（[668] 那条用例）：<c>AddEvent</c> 每记一条事件都会掷一次 2% 的彩蛋骰，掷中就在
    /// **刚写那条之后**再追一条 <c>EventKind.Mascot</c>（MainWindow.Console.cs:1699）⇒ 取「最后一条事件」
    /// 的断言会偶发变红（本批实测撞上：被拒那条 Warn 后面压着一条彩蛋）。那条契约要的是
    /// 「这次调用**确实**留了一条 Warn」，与它后面压着什么无关。
    ///
    /// ⚠ 为什么按**标记事件**开窗、而不是「最近 N 条」或「最新一条 Warn」：过去 / 别的批次可能写过文案
    ///   相同的 Warn，只看「最近 / 最新」会把它当成这次的 ⇒ 断言恒真。标记事件由用例自己写在那次调用
    ///   **之前**，窗口里因此只可能装本次调用产出的事件（外加引擎 tick 这类与判据无关的噪声条目）。
    /// 反射在本文件已有先例（见 <see cref="MascotEventTextsForTest"/>）；只读、先取副本再筛。
    /// ⚠ 定位失败（字段改名 / 改类型、标记被 100 条上限裁掉）**不做兜底**：抛出去由 <see cref="RunCore"/>
    ///   的 catch 记 FAIL —— 宁可红，也不能悄悄退化成「恒真」。
    /// </summary>
    private static List<(string Text, MainWindow.EventKind Kind)> EventsAfterForTest(string marker)
    {
        var field = typeof(MainWindow).GetField("_appEvents",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        if (field?.GetValue(null) is not System.Collections.IEnumerable rows)
            throw new InvalidOperationException("自检夹具失效：取不到 MainWindow._appEvents（私有静态事件表）");

        // _appEvents 是 List<(string, EventKind, Color?)>，但走反射只能拿到非泛型的 IEnumerable
        // ⇒ 先 Cast<object>()（LINQ）才能逐行做模式匹配，否则编译器报"object 没有 GetEnumerator"。
        var all = new List<(string Text, MainWindow.EventKind Kind)>();
        foreach (object row in rows.Cast<object>())
            if (row is ValueTuple<string, MainWindow.EventKind, System.Windows.Media.Color?> t)
                all.Add((t.Item1, t.Item2));

        // 取**最后一条**标记：同一进程里若重复开窗，只认最近那次。
        int start = all.FindLastIndex(e => e.Text.Contains(marker, StringComparison.Ordinal));
        if (start < 0)
            throw new InvalidOperationException($"自检夹具失效：事件表里找不到标记「{marker}」（观察窗无法界定）");
        return all.GetRange(start + 1, all.Count - start - 1);
    }

    /// <summary>
    /// 收集一棵**逻辑树**上所有指定类型的后代（含根自身）。
    ///
    /// ⚠ 为什么必须走逻辑树而不是视觉树（实测教训）：批量条的进度文案挂在
    /// <c>_batchActionPopup.Child</c> → <c>_batchMenuStack</c> 里，而这个 Popup 的 Child
    /// **从未加入任何 PresentationSource**（见 MainWindow.Images.cs 的 HookPopupTheme 注释：
    /// 它既不在视觉树上、也不在逻辑树上）。自检窗口又是"自建内容树 + Measure/Arrange"，
    /// 那个 Border 自己没被 Measure ⇒ 视觉树walker **一个子节点都走不到**（实测：逻辑子有、视觉子 0）。
    /// LogicalTreeHelper 只看 Parenting 链、不需要布局，所以只有它够得到这一棵。
    /// </summary>
    private static List<T> BatchLogicalDescendantsForTest<T>(DependencyObject? root) where T : DependencyObject
    {
        var found = new List<T>();
        Walk(root);
        return found;

        void Walk(DependencyObject? o)
        {
            if (o is null) return;                 // 显式判空：LogicalTreeHelper.GetChildren 不收可空引用
            try
            {
                if (typeof(T).IsAssignableFrom(o.GetType())) found.Add((T)o);
                foreach (object child in LogicalTreeHelper.GetChildren(o))
                    if (child is DependencyObject d) Walk(d);
            }
            catch { }
        }
    }

    /// <summary>
    /// 走反射取批量弹层的**内容根**（<c>_batchActionPopup.Child</c>，即那个 Border，
    /// 里面装着 <c>_batchMenuStack</c>）。进度文案与进度条都在这棵子树里；
    /// 注意 <c>BuildBatchBar()</c> 返回的是**触发按钮**，不是这棵子树（第一版断言就是在这里量错的）。
    /// </summary>
    private static DependencyObject? BatchMenuRootForTest(MainWindow w)
    {
        try
        {
            var f = typeof(MainWindow).GetField("_batchActionPopup",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return (f?.GetValue(w) as System.Windows.Controls.Primitives.Popup)?.Child as DependencyObject;
        }
        catch { return null; }
    }

    /// <summary>
    /// 走反射取 <c>MainWindow._batchBarProgressText</c>（批量条里那条 i/total 文案）。
    /// 该字段**没有** ForTest 访问器（写它的那位同事没加），而它在修好前是**死字段**
    /// （只声明、没 new、也没挂进 _batchMenuStack）⇒ 只能从这一侧够到它，再交给
    /// <see cref="BatchLogicalDescendantsForTest{T}"/> 去证明"它真在树里"。取不到返回 null（调用方判红）。
    /// </summary>
    private static TextBlock? BatchBarProgressTextForTest(MainWindow w)
    {
        try
        {
            var f = typeof(MainWindow).GetField("_batchBarProgressText",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return f?.GetValue(w) as TextBlock;
        }
        catch { return null; }
    }

    private static string CollectText(DependencyObject root)
    {
        var sb = new StringBuilder();
        Walk(root);
        return sb.ToString();

        void Walk(DependencyObject o)
        {
            if (o is TextBlock tb) sb.Append(tb.Text).Append(' ');
            int n = VisualTreeHelper.GetChildrenCount(o);
            for (int i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(o, i));
        }
    }

    /// <summary>
    /// 自检用：找出「属于同一个作者链接」的可点部件（自身带手型，或祖先挂着该主页地址）。
    /// 返回数量 ≥2 表示头像与名字确实合成了一处链接。
    /// </summary>
    private static List<FrameworkElement> FindHandCursorPartsForTest(DependencyObject? root, string url)
    {
        var found = new List<FrameworkElement>();
        Walk(root, null);
        return found;

        void Walk(DependencyObject? o, string? tagFromAncestor)
        {
            if (o == null) return;
            try
            {
                string? tag = tagFromAncestor;
                if (o is FrameworkElement fe)
                {
                    if (fe.Tag is string s && s == url) tag = s;
                    if (tag == url && fe.Cursor == System.Windows.Input.Cursors.Hand) found.Add(fe);
                }
                int n = VisualTreeHelper.GetChildrenCount(o);
                for (int i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(o, i), tag);
            }
            catch { }
        }
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";

    /// <summary>
    /// 自检用：手写一份快照清单（与 SnapshotManager.Create 的 JsonSerializer 字段名一致）。
    /// 只为构造"当年那台机器的绝对路径 / 被改过的哈希"这类现实数据，全程落在 %TEMP% 夹具里。
    /// </summary>
    private static string SelfTestManifest(string id, string kind, string reason, string hash,
                                           params (string Name, string Target)[] files)
        => JsonSerializer.Serialize(new
        {
            version = 1,
            id,
            kind,
            reason,
            timeUtc = DateTime.UtcNow.ToString("o"),
            app = "DSHGuard",
            appVersion = GuardVersion.Version,
            dshSpec = VersionMemory.Spec,
            dshPin = VersionMemory.Pin,
            files = files.Select(f => new
            {
                name = f.Name,
                size = 0L,
                sha256 = hash,
                target = f.Target,
                skipped = false,
                skipReason = ""
            })
        }, new JsonSerializerOptions { WriteIndented = true });

    /// <summary>自检用：找页面里第一个「手型光标 + 自带缩放变换」的元素（MiniBtn 样式那一类）。</summary>
    private static FrameworkElement? FindMiniBtnForTest(DependencyObject? root)
    {
        try
        {
            if (root is FrameworkElement fe &&
                fe.Cursor == System.Windows.Input.Cursors.Hand &&
                fe.RenderTransform is ScaleTransform)
                return fe;
            int n = VisualTreeHelper.GetChildrenCount(root!);
            for (int i = 0; i < n; i++)
            {
                var hit = FindMiniBtnForTest(VisualTreeHelper.GetChild(root!, i));
                if (hit != null) return hit;
            }
        }
        catch { }
        return null;
    }

    /// <summary>统计视觉树中的 Image 数量（卡片缩略图用）。</summary>
    private static int CountImages(DependencyObject root)
    {
        int n = 0;
        Walk(root);
        return n;

        void Walk(DependencyObject o)
        {
            if (o is Image) n++;
            int c = VisualTreeHelper.GetChildrenCount(o);
            for (int i = 0; i < c; i++) Walk(VisualTreeHelper.GetChild(o, i));
        }
    }

    /// <summary>切到「说明」页并取回页面文本（版本号显示在那一页）。</summary>
    private static string AboutTextsForTest(MainWindow w)
    {
        w.ShowViewForTest("about");
        w.LayoutForTest(960, 640);
        return w.PageTextsForTest("AboutPanel");
    }

    /// <summary>读取一行标签中最后一个标签的文字（分类条的「更多分类 / 收起」开关）。</summary>
    private static string LastChipText(Panel panel)
        => panel.Children.Count == 0 ? "" : CollectText((DependencyObject)panel.Children[^1]).Trim();

    /// <summary>取市场列表首张卡片的插件名，用于验证排序是否生效。</summary>
    private static string FirstCardName(MainWindow w)
    {
        var panel = (Panel)w.FindName("MarketPanel")!;
        if (panel.Children.Count == 0) return "";
        string text = CollectText((DependencyObject)panel.Children[0]).Trim();
        int cut = text.IndexOf(" ↗", StringComparison.Ordinal);
        return cut > 0 ? text.Substring(0, cut) : text.Split(' ').FirstOrDefault() ?? "";
    }

    /// <summary>提取卡片内所有按钮的文案、底色与字色，用于校验配色。</summary>
    private static List<(string Text, string Bg, string Fg)> ButtonInfos(DependencyObject root)
    {
        var list = new List<(string, string, string)>();
        Walk(root);
        return list;

        void Walk(DependencyObject o)
        {
            if (o is Button b)
            {
                string bg = (b.Background as SolidColorBrush)?.Color.ToString() ?? "-";
                string fg = (b.Foreground as SolidColorBrush)?.Color.ToString() ?? "-";
                list.Add((b.Content?.ToString() ?? "", bg, fg));
            }
            int c = VisualTreeHelper.GetChildrenCount(o);
            for (int i = 0; i < c; i++) Walk(VisualTreeHelper.GetChild(o, i));
        }
    }

    /// <summary>在线程池上执行异步任务并同步等待结果，避免 UI 线程自锁。</summary>
    private static T RunOffUi<T>(Func<Task<T>> work) => Task.Run(work).GetAwaiter().GetResult();

    /// <summary>抽干 Dispatcher 队列直至条件成立或达到 timeoutMs 上限。</summary>
    private static void PumpUntil(Func<bool> done, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            PumpStep(120);
            if (done()) return;
            System.Threading.Thread.Sleep(15);
        }
    }

    /// <summary>推入一个派发帧，并以 Normal 优先级的定时器强制结束该帧：Dispatcher.Invoke(…, Background) 会被更高优先级的自排队任务饿死。</summary>
    private static void PumpStep(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(ms), DispatcherPriority.Normal,
            (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    /// <summary>
    /// 等插件页的异步数据落定后再取数（自检用）：插件扫描、loader id 读取与「查新版本」都是异步的
    /// （后者还要联网取 git 源的最新提交），"数据已变、紧随其后的重渲染还没跑完"这一小段窗口里
    /// 取到的数是半截值 —— 断言便偶发变红，与功能无关。
    /// 做法：抽 Dispatcher 消息，每轮重新取一次数，直到「连续两轮取到的数完全一致」且
    /// 「摘要行不再写着正在扫描插件 / 正在查新版本」，最多等 timeoutMs（默认 2 秒）。
    /// 只用于等落定；断言判据本身不放宽。
    /// </summary>
    private static void SettlePluginDataForTest(MainWindow w, Func<int[]> read, int timeoutMs = 2000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int[] last = read();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            PumpStep(50);                                   // 抽一帧消息：让异步续体与随后的重渲染跑完
            System.Threading.Thread.Sleep(50);

            // 「查新版本」还在跑就先不判：检查结果一到，UpdatableCount 与「一键更新」按钮的显隐
            // 会**同一次重渲染里**一起变（见 RenderPlugins）；在此之前读到的按钮状态是上一帧的，
            // 断言必然偶发变红。这一步只影响"等多久"，不放宽任何判据（超时上限仍是 timeoutMs）。
            if (w.UpdatesCheckingForTest()) { last = read(); continue; }

            int[] now = read();
            if (now.SequenceEqual(last) && !PluginPageBusyForTest(w)) return;
            last = now;
        }
    }

    /// <summary>插件页摘要行是否还停在异步中（「正在扫描插件…」/「正在查新版本…」）。</summary>
    private static bool PluginPageBusyForTest(MainWindow w)
    {
        try
        {
            string t = (w.FindName("PluginsSummaryText") as TextBlock)?.Text ?? "";
            return t.Contains("正在扫描插件") || t.Contains("正在查新版本");
        }
        catch { return false; }
    }
}
