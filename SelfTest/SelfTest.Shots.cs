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

/// <summary>自检入口判定与截图 / 对话框样张（--shot / --dialog-shot / --uninstall）。</summary>
public static partial class SelfTest
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

            // 1.3.7：更新进度窗的样张 —— 它正是"点「立即更新」却点不开"那次现场的主角
            //   （构造函数对冻结画刷赋值当场抛 ⇒ 窗口连一帧都出不来）。能出图本身就是一个结论：
            //   构造函数这一关过得去。配色/按钮/文案一并目视核对。
            var guardShot = new GuardUpdateProgressWindow();
            guardShot.SetStage(42, "正在下载更新（42%）");
            Add(guardShot.BuildForShot());

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
                case "plugins-desktop":
                    // 1.5：同一套插件界面切到「桌面版」引擎 —— 双轨化深化后已具备完整管理能力，
                    // 插件卡片给出完整按钮组（启用/禁用/更新/卸载/重装），市场页保持可用。样本不依赖本机是否真装了桌面版：
                    // 只切目标与全局开关外观，插件列表沿用本机 Web profile 的扫描结果，
                    // 出图看的是"当前目标形态"而不是"桌面版装了什么"。
                    w.ShowViewForTest("plugins");
                    w.SetTargetForTest(true);
                    w.LayoutForTest(960, 640);
                    PumpUntil(() => false, 2500);
                    w.LayoutForTest(960, 640);
                    break;
                case "settings-version":
                    w.ShowViewForTest("settings");
                    w.ShowSettingsTabForTest("version");
                    PumpUntil(() => false, 3000);
                    // 出图用的"有新版本"这一态：真实结论要联网，截图不能依赖网络，
                    // 所以给一个显式开关把结论摆好，好看清「立即更新」那颗按钮长什么样。
                    // 远端版本取当前版本的下一个（写成 1.3.8 而不是当前号，免得样张上出现
                    // "当前 1.3.7 / 最新 1.3.7 / 有新版本可用"这种自相矛盾的一帧）。
                    if (args.Any(a => a.Equals("--guard-newer", StringComparison.OrdinalIgnoreCase)))
                        w.GuardUpdateStateForTest(GuardUpdateVerdict.NewerAvailable, GuardVersion.VersionFor(GuardVersion.Major, GuardVersion.Minor, GuardVersion.Patch + 1));
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
                    if (args.Any(a => a.Equals("--guard-newer", StringComparison.OrdinalIgnoreCase)))
                        w.GuardUpdateStateForTest(GuardUpdateVerdict.NewerAvailable,
                            GuardVersion.VersionFor(GuardVersion.Major, GuardVersion.Minor, GuardVersion.Patch + 1));
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
                case "trends":
                    // 1.4 生态趋势：真实数据要联网，出图不能依赖网络 ⇒ --trends-sample 灌一份**固定**样板数据
                    //（日期与检查时间都写死，样张才可复现、能跨次比对；见 CONTRIBUTING.md 的说明）。
                    // 灌样本会顺手把数据钉住（见 MainWindow._trendsPinned）：换页那一发联网请求的回包
                    // 不能再覆盖样板 —— 少了这条，样张里显示的其实是实时数据，每次都不同。
                    w.ShowViewForTest("trends");
                    if (args.Any(a => a.Equals("--trends-sample", StringComparison.OrdinalIgnoreCase)))
                        w.SetTrendsSampleForTest(BuildTrendsSampleForTest());
                    else
                        PumpUntil(() => false, 12000);   // 真联网：最多等 12 秒（与数据层超时同口径），取不到就是空状态样张
                    // 想看哪一榜：--trends-board downloads / stars（缺省就是涨星最快）。
                    if (ArgValue(args, "--trends-board") is { Length: > 0 } boardArg)
                        w.ShowTrendsTabForTest(boardArg);
                    w.LayoutForTest(960, 640);
                    PumpUntil(() => false, 400);
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

}
