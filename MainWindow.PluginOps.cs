using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace DSHGuard;

/// <summary>
/// 2.0.0：插件写操作的**目标分发层**（唯一入口）。
///
/// 1.5 留下的根因：卡片上的「更新 / 重新安装」、「一键更新」、「批量更新 / 卸载」、市场「安装」
///   全部直接拼 <c>npx dsh plugin --profile web …</c>，只有单个卸载与启停认目标。
///   结果就是用户截图里那种错配：站在桌面版点「更新」，事件栏报"已更新"，
///   实际改的是 Web 引擎的目录，桌面版那一份原封不动（本机 2026-09-30 实测：
///   事件报 dsh-whale-widget 0.3.16→0.3.17，桌面版 package.json 仍是 0.3.16）。
///
/// 现在所有写路径都经过这里取"命令 + 工作目录 + profile 目录"，不再各自拼。
///   · Web：沿用官方 CLI（<c>npx @deepseek-ai/dsh plugin --profile web …</c>），与既有行为逐字一致；
///   · 桌面版：官方 CLI 拒绝 desktop profile，改为在桌面版 profile 目录里直接跑 pnpm，
///     并同步维护 <c>package.json</c> 的 <c>dsh.profile.bundles</c>（只写 dependencies 引擎不加载）。
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// 当前管理目标的**唯一**状态（插件页、快照页、全局开关共用这一份）。
    /// 只有 <see cref="SetTarget"/> 能替换它；其余代码只读。
    /// </summary>
    private TargetContext _ctx = TargetContext.Create(GuardTarget.Web);

    /// <summary>
    /// <see cref="_ctx"/> 的静态只读镜像：给不持有窗口实例的静态路径读（快照前置钩子等）。
    /// 与 <c>_ctx</c> 在 <see cref="SetTarget"/> 里同一行同步，不单独改写。
    /// </summary>
    internal static TargetContext CurrentTarget { get; private set; } = TargetContext.Create(GuardTarget.Web);

    private GuardTarget Target => _ctx.Target;

    private string? TargetProfileDirOrNull
        => _ctx.IsDesktop ? _ctx.ProfileDir : null;

    private bool DesktopTarget => _ctx.IsDesktop;

    /// <summary>
    /// 切换管理目标。**全程序唯一的改写点**：上下文、静态镜像、插件缓存清理、界面外观都在这里完成。
    /// 左上角开关与自检只调它；本方法不刷新列表（由调用方决定要不要重扫）。
    /// </summary>
    internal void SetTarget(GuardTarget target, bool force = false)
    {
        if (!force && target == _ctx.Target) { ApplyTargetChrome(); return; }

        _ctx = TargetContext.Create(target);
        CurrentTarget = _ctx;
        _plugins = new List<PluginManager.Plugin>();
        _pluginUpdates.Clear();
        _updatesCheckedAt = DateTime.MinValue;
        _updatesError = "";
        _updatesChecking = false;
        _loaderIdsLoaded = false;
        _loaderIds.Clear();
        _batchSelected.Clear();
        _installedRenderOrder = new List<string>();
        if (PluginSearchBox != null) PluginSearchBox.Text = "";
        ApplyTargetChrome();
    }

    /// <summary>重读桌面版引擎版本（刷新插件列表时调用：用户可能刚升级了桌面版）。目标不变。</summary>
    private void ReloadTargetContext()
    {
        _ctx = TargetContext.Create(_ctx.Target);
        CurrentTarget = _ctx;
    }

    // ═════════════ 命令构造：按目标出参 ═════════════

    /// <summary>
    /// 一条"写命令"的完整形状。<see cref="Args"/> 为空 = 给不出可靠目标，调用方不得执行。
    /// </summary>
    internal readonly record struct PluginCmd(string Exe, string Args, string? WorkDir, string? ProfileDir)
    {
        public bool IsEmpty => string.IsNullOrWhiteSpace(Args);
    }

    /// <summary>更新一个已装插件（npm ⇒ 包名@版本；git ⇒ update 包名）。</summary>
    internal PluginCmd UpdateCmdFor(PluginManager.Plugin p, PluginManager.PluginUpdate u)
    {
        if (!DesktopTarget)
            return new PluginCmd("npx", UpdateArgsFor(p, u), null, null);

        string dir = GuardPaths.ProfileDirFor(GuardTarget.Desktop);
        string spec = PluginManager.DepSpec(p.Name, dir);
        string args = PluginManager.BuildPnpmUpdateArgs(p.Name, spec, u.Latest, Registries.For(GuardTarget.Desktop));
        if (args.Length == 0)
            Logger.NoteDiagnosis($"桌面版更新 {p.Name}：给不出可靠的目标（来源「{spec}」、目标位「{u.Latest}」）⇒ 未执行命令");
        return new PluginCmd("pnpm", args, dir, dir);
    }

    /// <summary>按清单声明重装（「安装损坏」卡片的「重新安装」）。</summary>
    internal PluginCmd ReinstallCmdFor(PluginManager.Plugin p)
    {
        string? dir = TargetProfileDirOrNull;
        string depSpec = PluginManager.DepSpec(p.Name, dir);
        if (!DesktopTarget)
        {
            string args = PluginSource.Classify(depSpec) != PluginSource.Kind.Registry
                ? PluginManager.BuildAddSourceArgs(PluginManager.GitSourceSpec(depSpec))
                : PluginManager.BuildAddSourceArgs(PluginManager.ConcreteVersionOf(depSpec).Length > 0
                    ? $"{p.Name}@{PluginManager.ConcreteVersionOf(depSpec)}"
                    : p.Name);
            return new PluginCmd("npx", args, null, null);
        }
        // 桌面版：清单里已有这一条声明，`pnpm install` 会按它装回来；
        //   不用 add（add 会改写用户声明的范围）。
        return new PluginCmd("pnpm", PluginManager.BuildPnpmInstallArgs(Registries.For(GuardTarget.Desktop)), dir, dir);
    }

    /// <summary>卸载一个插件。</summary>
    internal PluginCmd UninstallCmdFor(string name)
    {
        if (!DesktopTarget)
            return new PluginCmd("npx", PluginManager.BuildUninstallArgs(name), null, null);
        string dir = GuardPaths.ProfileDirFor(GuardTarget.Desktop);
        return new PluginCmd("pnpm", PluginManager.BuildPnpmRemoveArgs(name), dir, dir);
    }

    /// <summary>从市场装一个新插件（来源可以是 npm 包名[@版本] 或受信 git 源）。</summary>
    internal PluginCmd InstallCmdFor(string source)
    {
        if (!DesktopTarget)
            return new PluginCmd("npx", PluginManager.BuildAddSourceArgs(source), null, null);
        string dir = GuardPaths.ProfileDirFor(GuardTarget.Desktop);
        return new PluginCmd("pnpm", PluginManager.BuildPnpmAddSourceArgs(source, Registries.For(GuardTarget.Desktop)), dir, dir);
    }

    // ═════════════ 执行：按目标跑、按目标维护清单 ═════════════

    /// <summary>
    /// 跑一条写命令。桌面版在命令成功后补登 <c>dsh.profile.bundles</c>（pnpm 只管 dependencies）；
    /// 桌面版命令失败时**回滚清单**到跑之前的原样（pnpm add 失败也可能半写 package.json）。
    /// </summary>
    internal async Task<(bool Ok, string Output)> RunPluginCmdAsync(PluginCmd cmd, string? ensureBundle = null,
        bool cancelable = false, int timeoutMs = 600000)
    {
        if (cmd.IsEmpty) return (false, "（未执行：给不出可靠的命令参数）");

        string? pjBackup = null;
        string? pjPath = cmd.ProfileDir != null ? Path.Combine(cmd.ProfileDir, "package.json") : null;
        if (pjPath != null && File.Exists(pjPath))
        {
            try { pjBackup = File.ReadAllText(pjPath); } catch { pjBackup = null; }
        }

        var (ok, output) = cancelable
            ? await RunCommandCancelableAsync(cmd.Exe, cmd.Args, workDir: cmd.WorkDir, timeoutMs: timeoutMs, relaxSupplyChainPolicy: true)
            : await RunCommandAsync(cmd.Exe, cmd.Args, workDir: cmd.WorkDir, timeoutMs: timeoutMs, relaxSupplyChainPolicy: true);

        if (cmd.ProfileDir != null && ensureBundle != null && PluginManager.IsValidPackageName(ensureBundle))
        {
            bool installedNow = PluginManager.HasDependency(ensureBundle, cmd.ProfileDir)
                                && PluginManager.PackageDirExists(ensureBundle, cmd.ProfileDir);
            if (installedNow)
            {
                var (bOk, bDetail) = PluginManager.EnsureBundleEntry(cmd.ProfileDir, ensureBundle);
                if (!bOk) Logger.NoteDiagnosis($"桌面版登记 bundles 失败（{ensureBundle}）：{bDetail}");
            }
            else if (!ok && pjBackup != null && pjPath != null)
            {
                try
                {
                    File.WriteAllText(pjPath, pjBackup, new System.Text.UTF8Encoding(false));
                    Logger.NoteDiagnosis($"桌面版命令失败，已把 package.json 还原到执行前（{ensureBundle}）");
                }
                catch (Exception ex) { Logger.LogError("RunPluginCmdAsync.restore", ex); }
            }
        }
        return (ok, output);
    }

    /// <summary>当前目标的中文名（弹窗 / 事件里点名，免得用户分不清改的是哪一边）。</summary>
    internal string TargetLabel => _ctx.Label;

    /// <summary>
    /// 拿来评兼容性的"当前引擎版本"——**按目标取**，判据只此一处。
    ///   · Web：版本页钉住的版本优先，否则取探测到的 npx 缓存版本；
    ///   · 桌面版：读主程序 exe 的文件版本（与 npx 缓存毫无关系，拿 Web 版本评桌面版必然错）。
    /// </summary>
    internal string CompatEngineVersion
        => _ctx.IsDesktop
            ? (_ctx.DesktopVersion.Length > 0 ? _ctx.DesktopVersion : "未知")
            : (VersionMemory.Pin.Length > 0 ? VersionMemory.Pin : _currentDshVersion);
}
