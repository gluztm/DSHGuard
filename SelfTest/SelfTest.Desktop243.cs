#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DSHGuard.Unified;

namespace DSHGuard;

public static partial class SelfTest
{
    internal static void RunDesktop243Checks(MainWindow w, Action<string, bool, string> check, Action<string, string> skip)
    {
        // ── 2.4.3-A: 主窗圆角与 Win11 DWM 原生圆角对齐（消除双圆角重影）+ 最大化/还原往返 ──
        {
            var rootBorder = w.FindName("RootBorder") as Border;
            bool cornerAligned = rootBorder != null
                && Math.Abs(rootBorder.CornerRadius.TopLeft - BlurHelper.WindowCornerRadiusDip) < 0.01
                && Math.Abs(rootBorder.CornerRadius.TopRight - BlurHelper.WindowCornerRadiusDip) < 0.01
                && Math.Abs(rootBorder.CornerRadius.BottomLeft - BlurHelper.WindowCornerRadiusDip) < 0.01
                && Math.Abs(rootBorder.CornerRadius.BottomRight - BlurHelper.WindowCornerRadiusDip) < 0.01;
            var (maxCorner, restoredCorner) = w.MaximizeCornerRoundtripForTest();
            bool roundtripOk = Math.Abs(maxCorner) < 0.01
                && Math.Abs(restoredCorner - BlurHelper.WindowCornerRadiusDip) < 0.01;
            check("2.4.3-A · 主窗 RootBorder 圆角严格对齐 8 DIP 且最大化/还原往返恢复正常",
                cornerAligned && roundtripOk,
                $"常态={rootBorder?.CornerRadius.TopLeft ?? -1} · 最大化={maxCorner} · 还原={restoredCorner} · DWM原生={BlurHelper.HasDwmNativeCornerForTest(w)}");
        }

        // ── 2.4.3-B: 凭据平台预设移除私人 Yomi + 改为带专属矢量 Logo 下拉列表 ──
        {
            var presets = DshCredentialManager.BuiltInPresets;
            bool noYomi = presets.Count == 7 && presets.All(p =>
                !p.PresetId.Contains("yomi", StringComparison.OrdinalIgnoreCase)
                && !p.Label.Contains("yomi", StringComparison.OrdinalIgnoreCase)
                && !p.ProviderId.Contains("yomi", StringComparison.OrdinalIgnoreCase)
                && !p.EnvKey.Contains("DSH_IMAGE_GEN", StringComparison.OrdinalIgnoreCase));

            int logoOkCount = 0;
            foreach (var p in presets)
            {
                var badge = MainWindow.BuildProviderPresetLogo(p.PresetId, 22);
                if (badge.Child is System.Windows.Shapes.Path path && path.Data != null && !path.Data.IsEmpty())
                    logoOkCount++;
            }
            var fallbackBadge = MainWindow.BuildProviderPresetLogo(null, 22);
            bool fallbackOk = fallbackBadge.Child is System.Windows.Shapes.Path fp && fp.Data != null && !fp.Data.IsEmpty();

            var wizard = w.BuildCredentialWizardForTest();
            var triggers = LogicalTreeDescendants<Border>(wizard).Where(b => b.Name == "CredPresetTrigger").ToList();
            var legacyButtons = LogicalTreeDescendants<Button>(wizard)
                .Where(b => (b.Content?.ToString() ?? "").Contains("Yomi", StringComparison.OrdinalIgnoreCase)
                         || (b.Content?.ToString() ?? "").Contains("DeepSeek 官方", StringComparison.OrdinalIgnoreCase))
                .ToList();

            check("2.4.3-B1 · 平台预设已移除私人 Yomi 并保留 7 个通用平台，各平台均有独立非空矢量 Path Logo",
                noYomi && logoOkCount == 7 && fallbackOk,
                $"预设数={presets.Count} · 无Yomi={noYomi} · 矢量徽章={logoOkCount}/7 · 占位Logo={fallbackOk}");
            check("2.4.3-B2 · 添加提供商向导使用下拉触发器（CredPresetTrigger）代替平铺按钮",
                triggers.Count == 1 && legacyButtons.Count == 0,
                $"触发器={triggers.Count} · 旧平铺预设按钮残留={legacyButtons.Count}");

            bool popupClickHoldFixed = false;
            bool afterDownOpen = true;
            bool afterUpOpen = false;
            bool afterSecondClickOpen = true;
            if (triggers.Count == 1)
            {
                try
                {
                    var trg = triggers[0];
                    var down1 = new System.Windows.Input.MouseButtonEventArgs(
                        System.Windows.Input.Mouse.PrimaryDevice,
                        Environment.TickCount,
                        System.Windows.Input.MouseButton.Left)
                    {
                        RoutedEvent = UIElement.MouseLeftButtonDownEvent,
                        Source = trg,
                    };
                    trg.RaiseEvent(down1);
                    afterDownOpen = w.IsCredPresetPopupOpenForTest;

                    var up1 = new System.Windows.Input.MouseButtonEventArgs(
                        System.Windows.Input.Mouse.PrimaryDevice,
                        Environment.TickCount,
                        System.Windows.Input.MouseButton.Left)
                    {
                        RoutedEvent = UIElement.MouseLeftButtonUpEvent,
                        Source = trg,
                    };
                    trg.RaiseEvent(up1);
                    afterUpOpen = w.IsCredPresetPopupOpenForTest;

                    var down2 = new System.Windows.Input.MouseButtonEventArgs(
                        System.Windows.Input.Mouse.PrimaryDevice,
                        Environment.TickCount,
                        System.Windows.Input.MouseButton.Left)
                    {
                        RoutedEvent = UIElement.MouseLeftButtonDownEvent,
                        Source = trg,
                    };
                    trg.RaiseEvent(down2);

                    var up2 = new System.Windows.Input.MouseButtonEventArgs(
                        System.Windows.Input.Mouse.PrimaryDevice,
                        Environment.TickCount,
                        System.Windows.Input.MouseButton.Left)
                    {
                        RoutedEvent = UIElement.MouseLeftButtonUpEvent,
                        Source = trg,
                    };
                    trg.RaiseEvent(up2);
                    afterSecondClickOpen = w.IsCredPresetPopupOpenForTest;

                    popupClickHoldFixed = !afterDownOpen && afterUpOpen && !afterSecondClickOpen;
                }
                finally
                {
                    w.CloseCredentialPopups();
                }
            }
            check("2.4.3-B3 · 平台预设下拉单次左键点击松手后保持展开，再次点击正常收起（不需按住不放）",
                popupClickHoldFixed,
                $"Down后展开={afterDownOpen}(应False) · Up后展开={afterUpOpen}(应True) · 二次点击后展开={afterSecondClickOpen}(应False)");
        }

        // ── 2.4.3-C: 更新防卡死（快速失败环境变量、停滞进展识别、真实子进程熔断、停止更新交互）──
        {
            var fastEnv = PluginManager.NetworkFastFailEnv();
            bool hasFastEnv = fastEnv.Any(kv => kv.Key == "GIT_TERMINAL_PROMPT" && kv.Value == "0")
                && fastEnv.Any(kv => kv.Key == "pnpm_config_fetch_timeout")
                && fastEnv.Any(kv => kv.Key == "GIT_HTTP_LOW_SPEED_TIME");

            string sig = "";
            bool p1 = PluginManager.IsMeaningfulCommandProgress("Progress: resolved 0, reused 1, downloaded 0, added 0\r\n", ref sig);
            bool p2 = PluginManager.IsMeaningfulCommandProgress("\x1b[2K\rProgress: resolved 0, reused 1, downloaded 0, added 0", ref sig);
            bool p3 = PluginManager.IsMeaningfulCommandProgress("Progress: resolved 3, reused 1, downloaded 1, added 0", ref sig);
            bool p4 = PluginManager.IsMeaningfulCommandProgress("   \r\n  ", ref sig);

            bool stallMatch = GitHubRoute.LooksLikeGitHubUnreachable(
                "下载或安装连接无响应（连续 45 秒无新进展，已自动中止） https://codeload.github.com/o/r/tar.gz/1")
                && GitHubRoute.LooksLikeGitHubUnreachable("Failed to fetch https://codeload.github.com/o/r/tar.gz/1")
                && !GitHubRoute.LooksLikeGitHubUnreachable("404 Not Found - GET https://registry.npmmirror.com/x");

            check("2.4.3-C1 · 防卡死网络环境与停滞进展识别：空刷 Progress 不喂狗、真实推进才喂狗",
                hasFastEnv && p1 && !p2 && p3 && !p4 && stallMatch,
                $"FastEnv={fastEnv.Count} · 首行={p1} · 重复空刷={p2}(应False) · 新进展={p3} · 空白={p4}(应False) · 停滞识别={stallMatch}");

            // 真子进程停滞熔断测试：进程睡 9 秒不输出，看门狗 350ms 必须在 4 秒内触发 Stalled=True 并杀掉子进程
            bool stallProbeOk = false;
            string stallDetail = "";
            try
            {
                var sw = Stopwatch.StartNew();
                var psi = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add("echo Progress: resolved 0, reused 1, downloaded 0, added 0 & ping -n 10 127.0.0.1 >nul");
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    long lastTick = Environment.TickCount64;
                    string probeSig = "";
                    void Touch(byte[] buf, int n)
                    {
                        string t = Encoding.UTF8.GetString(buf, 0, n);
                        if (PluginManager.IsMeaningfulCommandProgress(t, ref probeSig))
                            System.Threading.Interlocked.Exchange(ref lastTick, Environment.TickCount64);
                    }
                    var r = Task.Run(async () =>
                    {
                        var outTask = ConsoleTextDecoder.ReadDecodedAsync(proc.StandardOutput.BaseStream, onChunk: Touch);
                        var errTask = ConsoleTextDecoder.ReadDecodedAsync(proc.StandardError.BaseStream, onChunk: Touch);
                        return await MainWindow.CollectProcessWithStallAsync(
                            proc, outTask, errTask, timeoutMs: 10000, stallTimeoutMs: 350,
                            getLastProgressTicks: () => System.Threading.Interlocked.Read(ref lastTick), drainMs: 1500);
                    }).GetAwaiter().GetResult();
                    sw.Stop();
                    bool killed = false;
                    try { killed = proc.HasExited; } catch { killed = true; }
                    stallProbeOk = !r.Exited && r.Stalled && killed && sw.ElapsedMilliseconds < 5000;
                    stallDetail = $"Exited={r.Exited} · Stalled={r.Stalled} · Killed={killed} · 耗时={sw.ElapsedMilliseconds}ms";
                }
                else
                {
                    stallDetail = "无法启动探针进程";
                }
            }
            catch (Exception ex)
            {
                stallDetail = $"{ex.GetType().Name}: {ex.Message}";
            }
            check("2.4.3-C2 · 停滞看门狗真进程实测：连续无进展超过 stallTimeoutMs 立即熔断并结束子进程树",
                stallProbeOk, stallDetail);

            // 单插件「停止更新」按钮接管与原色还原 + 右栏取消/底部入口存在性
            var fakePlugin = new PluginManager.Plugin { Name = "dsh-selftest-dummy", Version = "0.1.0" };
            var origBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x9E, 0xFF));
            var fakeBtn = new Button { Content = "更新到最新提交", Background = origBrush };
            w.AdoptUpdateButtonForTest(fakeBtn, fakePlugin);
            bool adopted = (fakeBtn.Content?.ToString() == "停止更新")
                && (fakeBtn.Background as SolidColorBrush)?.Color == MainWindow.StopButtonColor
                && w.IsUpdatingSinglePluginForTest(fakePlugin);
            w.EndUpdateButtonStateForTest();
            bool restored = (fakeBtn.Content?.ToString() == "更新到最新提交")
                && ReferenceEquals(fakeBtn.Background, origBrush)
                && !w.IsUpdatingSinglePluginForTest(fakePlugin);
            var cancelBtn = w.FindName("CancelRunningCmdBtn") as Border;
            check("2.4.3-C3 · 单插件「停止更新」按钮接管/还原完整且页脚存在中断入口 CancelRunningCmdBtn",
                adopted && restored && cancelBtn != null,
                $"接管={adopted} · 还原={restored} · 页脚取消按钮={cancelBtn != null}");
        }

        // ── 2.4.3-D: Profile 目录占用分类安全边界（防误杀外部工作区/宿主/独立软件）──
        {
            const string prof = @"C:\Users\demo\.dsh\profiles\desktop";
            const string deskInst = @"D:\Applications\DeepSeek Harness";
            const string guardDir = @"D:\Applications\DSHGuard";
            const int selfPid = 9999;

            var kSelf = ProfileLockManager.ClassifyOccupier(selfPid, selfPid, "DSHGuard", @"D:\Applications\DSHGuard\DSHGuard.exe", "", "", prof, deskInst, guardDir, true);
            var kGuard = ProfileLockManager.ClassifyOccupier(1001, selfPid, "DSHGuard", @"D:\Applications\DSHGuard\DSHGuard.exe", prof, "", prof, deskInst, guardDir, true);
            var kDsh = ProfileLockManager.ClassifyOccupier(1002, selfPid, "DeepSeek Harness", @"D:\Applications\DeepSeek Harness\DeepSeek Harness.exe", "", "", prof, deskInst, guardDir, false);
            var kBashInProf = ProfileLockManager.ClassifyOccupier(1003, selfPid, "bash", @"C:\Users\demo\.dsh\profiles\desktop\node_modules\@dsh-ops\bash-win32-x64\bin\bash.exe", "", "", prof, deskInst, guardDir, false);
            var kNodeCwd = ProfileLockManager.ClassifyOccupier(1004, selfPid, "node", @"C:\Program Files\nodejs\node.exe", prof, "node server.js", prof, deskInst, guardDir, false);
            var kPyCmd = ProfileLockManager.ClassifyOccupier(1005, selfPid, "python", @"D:\Applications\Python312\python.exe", @"C:\Windows", "\"python.exe\" \"C:\\Users\\demo\\.dsh\\profiles\\desktop\\node_modules\\x\\run.py\"", prof, deskInst, guardDir, false);
            var kOtherWorkspace = ProfileLockManager.ClassifyOccupier(1006, selfPid, "node", @"C:\Program Files\nodejs\node.exe", @"D:\Agents WorkSpace\Antigravity-Data", @"node D:\Agents WorkSpace\Codex-Data\mcp\server.js", prof, deskInst, guardDir, false);
            var kSimilarPrefix = ProfileLockManager.ClassifyOccupier(1007, selfPid, "node", @"C:\Program Files\nodejs\node.exe", @"C:\Users\demo\.dsh\profiles\desktop-backup", "", prof, deskInst, guardDir, false);
            var kVsCodeLock = ProfileLockManager.ClassifyOccupier(1008, selfPid, "Code", @"C:\Users\demo\AppData\Local\Programs\Microsoft VS Code\Code.exe", "", "", prof, deskInst, guardDir, true);
            var kExplorerIdle = ProfileLockManager.ClassifyOccupier(1009, selfPid, "explorer", @"C:\Windows\explorer.exe", "", "", prof, deskInst, guardDir, false);

            var extracted = ProfileLockManager.ExtractLockedPathsFromOutput("EPERM: operation not permitted, unlink 'C:\\not-exist\\x'", prof);
            var unlockBtn = w.FindName("UnlockProfileBtn") as Border;

            bool classifyOk = kSelf == null
                && kGuard == null
                && kDsh == ProfileLockManager.OccupierKind.DshHostProcess
                && kBashInProf == ProfileLockManager.OccupierKind.PluginResidue
                && kNodeCwd == ProfileLockManager.OccupierKind.PluginResidue
                && kPyCmd == ProfileLockManager.OccupierKind.PluginResidue
                && kOtherWorkspace == null
                && kSimilarPrefix == null
                && kVsCodeLock == ProfileLockManager.OccupierKind.ExternalApp
                && kExplorerIdle == null;

            check("2.4.3-D · Profile 占用分类边界：精准识别残留插件进程、排除自身/外部工作区、ExternalApp 只提示不自动强杀",
                classifyOk && extracted.Count == 0 && unlockBtn != null && unlockBtn.IsEnabled,
                $"分类全对={classifyOk} · 越界过滤={extracted.Count == 0} · 解除占用按钮={unlockBtn != null && unlockBtn.IsEnabled}");
        }
    }

    private static IEnumerable<T> LogicalTreeDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root == null) yield break;
        foreach (object childObj in LogicalTreeHelper.GetChildren(root))
        {
            if (childObj is DependencyObject child)
            {
                if (child is T match) yield return match;
                foreach (var nested in LogicalTreeDescendants<T>(child))
                    yield return nested;
            }
        }
    }
}
