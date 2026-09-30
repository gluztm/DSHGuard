using System;
using System.IO;

namespace DSHGuard;

/// <summary>
/// 2.0.0 双轨化：写命令按目标分发的断言（由 RunCore 在插件页那组用例之后调用）。
/// 只构造命令、不执行；切目标走与全局开关同一条链 SetTarget，结束时切回 Web。
/// </summary>
public static partial class SelfTest
{
    internal static void RunDualTrackChecks(MainWindow w, Action<string, bool, string> check)
    {
        string webDir = GuardPaths.ProfileDirFor(GuardTarget.Web);
        string deskDir = GuardPaths.ProfileDirFor(GuardTarget.Desktop);
        try
        {
            w.SetTargetForTest(false);
            var webInstall = w.InstallCmdForTest("dsh-mnemon@0.5.9");
            var webRemove = w.UninstallCmdForTest("dsh-mnemon");

            w.SetTargetForTest(true);
            var deskInstall = w.InstallCmdForTest("dsh-mnemon@0.5.9");
            var deskRemove = w.UninstallCmdForTest("dsh-mnemon");
            var deskBad = w.InstallCmdForTest("dsh-mnemon --registry=https://evil.example");

            check("2.0.0 · 分发层（Web）：安装 / 卸载走 npx + `--profile web`，不指定工作目录与 profile 目录（与 1.x 逐字一致）",
                webInstall.Exe == "npx" && webInstall.Args.Contains("--profile web") &&
                webInstall.WorkDir == null && webInstall.ProfileDir == null &&
                webRemove.Exe == "npx" && webRemove.Args.Contains("--profile web"),
                $"安装=«{webInstall.Exe} {webInstall.Args}» 卸载=«{webRemove.Exe} {webRemove.Args}»");

            check("2.0.0 · 分发层（桌面版）：安装 / 卸载走 pnpm，工作目录与 profile 目录都指向桌面版 profile，参数里绝不出现 `--profile web`",
                deskInstall.Exe == "pnpm" && deskRemove.Exe == "pnpm" &&
                SamePath(deskInstall.WorkDir, deskDir) && SamePath(deskInstall.ProfileDir, deskDir) &&
                SamePath(deskRemove.WorkDir, deskDir) &&
                !deskInstall.Args.Contains("--profile") && !deskRemove.Args.Contains("--profile") &&
                !SamePath(deskInstall.WorkDir, webDir),
                $"安装=«{deskInstall.Exe} {deskInstall.Args}» @ {deskInstall.WorkDir} · 卸载=«{deskRemove.Exe} {deskRemove.Args}»");

            check("2.0.0 · 分发层（桌面版）：注入构造的来源同样给不出命令（空参数 ⇒ RunPluginCmdAsync 拒绝执行）",
                deskBad.IsEmpty,
                $"参数=«{deskBad.Args}»");

            check("2.0.0 · 目标只有一份：切到桌面版后窗口目标 / 静态镜像 / 提示文案 / 兼容性版本来源一致",
                w.TargetForTest == GuardTarget.Desktop &&
                MainWindow.StaticTargetForTest == GuardTarget.Desktop &&
                w.PluginScopeHintTextForTest == "管理对象：桌面版" &&
                w.SnapScopeHintTextForTest == "新建快照：桌面版",
                $"目标={w.TargetForTest} 镜像={MainWindow.StaticTargetForTest} 插件提示=«{w.PluginScopeHintTextForTest}» 快照提示=«{w.SnapScopeHintTextForTest}» 兼容版本=«{w.CompatEngineVersionForTest}»");
        }
        catch (Exception ex)
        {
            check("2.0.0 · 双轨分发断言组未抛异常", false, ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            try { w.SetTargetForTest(false); } catch { }
        }
    }

    private static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(a!).TrimEnd('\\'), Path.GetFullPath(b!).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
