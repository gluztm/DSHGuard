using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

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
    private TargetContext _ctx = TargetContext.Create(TargetContext.DefaultTarget());

    /// <summary>
    /// <see cref="_ctx"/> 的静态只读镜像：给不持有窗口实例的静态路径读（快照前置钩子等）。
    /// 与 <c>_ctx</c> 在 <see cref="SetTarget"/> 里同一行同步，不单独改写。
    /// </summary>
    internal static TargetContext CurrentTarget { get; private set; } = TargetContext.Create(TargetContext.DefaultTarget());

    private GuardTarget Target => _ctx.Target;

    private string? TargetProfileDirOrNull
        => _ctx.IsDesktop ? _ctx.ProfileDir : null;

    private bool DesktopTarget => _ctx.IsDesktop;

    /// <summary>静态路径用的"当前目标 profile 目录"（null = Web 默认路径，含自检覆盖语义）。</summary>
    private static string? CurrentProfileDirOrNull
        => CurrentTarget.IsDesktop ? CurrentTarget.ProfileDir : null;

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
        _updatesCheckedTarget = null;
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
            string args = PluginSource.Classify(depSpec) == PluginSource.Kind.Tarball
                // 归档：既不是 git 源（GitSourceSpec 对它返回空串 ⇒ 拼出一条空参数的 add 命令，
                //   用户点「重新安装」什么也不会发生），也不是 npm 包（按包名 add 必然找不到）。
                //   重装就得原样再装一次那条 URL。
                ? PluginManager.BuildAddSourceArgs(depSpec)
                : PluginSource.Classify(depSpec) != PluginSource.Kind.Registry
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
    /// 跑一条写命令。桌面版只有在"安装目录 / 清单 / 锁文件 / pnpm 元数据"完整提交时才补登
    /// <c>dsh.profile.bundles</c>；命令失败且没有完整提交时，把清单回滚到执行前。
    /// <paramref name="expectedVersionAfter"/> 传这次要达成的具体版本（git 源传「仓库最新」这类目标即可）；
    /// 不传时保留旧口径，兼容卸载等没有版本目标的调用。
    /// <paramref name="requireRegistryManifestAfter"/> 专供「按现有清单重装」：范围声明的目标版本
    /// 不是一个下限，必须按清单 + 锁文件 specifier + 安装目录的完整提交来判，不能拿
    /// <see cref="PluginManager.ConcreteVersionOf"/> 抽出的数字冒充目标。
    /// </summary>
    internal async Task<(bool Ok, string Output)> RunPluginCmdAsync(PluginCmd cmd, string? ensureBundle = null,
        bool cancelable = false, int timeoutMs = 180000, string? expectedVersionAfter = null,
        bool requireRegistryManifestAfter = false, int stallTimeoutMs = 45000)
    {
        if (cmd.IsEmpty) return (false, "（未执行：给不出可靠的命令参数）");

        // 第二道兜底：即使某个调用点漏了写操作前弹窗，也绝不在桌面版仍占用 profile 时启动
        // pnpm。现场两个 bash.exe 会长期持有 @dsh-ops/bash-win32-x64 里的 sh.exe，一旦命令
        // 跑进去就要几分钟后才会在 os error 5 上失败，且可能留下半提交的包目录。
        bool desktopRunning = ShouldBlockDesktopPluginCommand(cmd.ProfileDir, DesktopAppRunning());
        if (desktopRunning)
        {
            const string blocked = "DSH 桌面版仍在运行，本次插件写命令未执行。请先结束桌面版后重试。";
            Logger.NoteDiagnosis("插件写命令被运行态闸门拦下：DSH 桌面版仍在运行");
            return (false, blocked);
        }

        string targetProfDir = !string.IsNullOrWhiteSpace(cmd.ProfileDir)
            ? cmd.ProfileDir!
            : GuardPaths.ProfileDirFor(GuardTarget.Web);
        try
        {
            var preLocks = await Task.Run(() => ProfileLockManager.ScanProfileLocks(targetProfDir));
            if (!preLocks.Any(x => x.Kind == ProfileLockManager.OccupierKind.DshHostProcess)
                && preLocks.Any(x => x.Kind == ProfileLockManager.OccupierKind.PluginResidue))
            {
                var (closedPre, killedPre, _) = await Task.Run(
                    () => ProfileLockManager.ReleasePluginResidues(preLocks, _ctx.Target));
                int cleanedPre = closedPre + killedPre;
                if (cleanedPre > 0)
                    AddEvent($"检测到 {cleanedPre} 个未退出的残留插件进程占用目录，已自动清理", EventKind.Warn);
            }
        }
        catch (Exception ex) { Logger.LogError("RunPluginCmdAsync.preCleanLocks", ex); }

        string? pjBackup = null;
        string? pjPath = cmd.ProfileDir != null ? Path.Combine(cmd.ProfileDir, "package.json") : null;
        if (pjPath != null && File.Exists(pjPath))
        {
            try { pjBackup = File.ReadAllText(pjPath); } catch { pjBackup = null; }
        }
        string commitBefore = cmd.ProfileDir != null && ensureBundle != null
            ? PluginManager.ReadInstalledCommit(ensureBundle, cmd.ProfileDir)
            : "";

        var (ok, output) = cancelable
            ? await RunCommandCancelableAsync(cmd.Exe, cmd.Args, workDir: cmd.WorkDir, timeoutMs: timeoutMs,
                relaxSupplyChainPolicy: true, stallTimeoutMs: stallTimeoutMs)
            : await RunCommandAsync(cmd.Exe, cmd.Args, workDir: cmd.WorkDir, timeoutMs: timeoutMs,
                relaxSupplyChainPolicy: true, stallTimeoutMs: stallTimeoutMs);

        if (!ok && !_userCancelledActiveCmd && PluginManager.LooksLikePnpmFileLock(output))
        {
            try
            {
                var extraPaths = ProfileLockManager.ExtractLockedPathsFromOutput(output, targetProfDir);
                var postLocks = await Task.Run(() => ProfileLockManager.ScanProfileLocks(targetProfDir, extraPaths));
                if (!postLocks.Any(x => x.Kind == ProfileLockManager.OccupierKind.DshHostProcess)
                    && postLocks.Any(x => x.Kind == ProfileLockManager.OccupierKind.PluginResidue))
                {
                    var (closedPost, killedPost, _) = await Task.Run(
                        () => ProfileLockManager.ReleasePluginResidues(postLocks, _ctx.Target));
                    int released = closedPost + killedPost;
                    if (released > 0)
                    {
                        AddEvent($"命令遭遇文件占用，已自动结束 {released} 个残留插件进程并重试", EventKind.Warn);
                        var retry = cancelable
                            ? await RunCommandCancelableAsync(cmd.Exe, cmd.Args, workDir: cmd.WorkDir, timeoutMs: timeoutMs,
                                relaxSupplyChainPolicy: true, stallTimeoutMs: stallTimeoutMs)
                            : await RunCommandAsync(cmd.Exe, cmd.Args, workDir: cmd.WorkDir, timeoutMs: timeoutMs,
                                relaxSupplyChainPolicy: true, stallTimeoutMs: stallTimeoutMs);
                        ok = retry.Success;
                        output = retry.Output + $"\n\n[已自动清理 {released} 个残留占用进程并重试]\n" + output;
                    }
                }
            }
            catch (Exception ex) { Logger.LogError("RunPluginCmdAsync.postUnlockRetry", ex); }
        }

        if (cmd.ProfileDir != null && ensureBundle != null && PluginManager.IsValidPackageName(ensureBundle))
        {
            bool installedNow = PluginManager.HasDependency(ensureBundle, cmd.ProfileDir)
                                && PluginManager.EvaluateInstallState(ensureBundle, cmd.ProfileDir)
                                   == PluginManager.InstallStateKind.Installed;
            string completionDetail = "";
            bool completionKnown = requireRegistryManifestAfter
                ? PnpmRegistryManifestLooksCommitted(ensureBundle, cmd.ProfileDir, out completionDetail)
                : (string.IsNullOrWhiteSpace(expectedVersionAfter)
                    || PnpmCommandLooksCommitted(ensureBundle, expectedVersionAfter!, cmd.ProfileDir, commitBefore));
            if (installedNow && completionKnown)
            {
                var (bOk, bDetail) = PluginManager.EnsureBundleEntry(cmd.ProfileDir, ensureBundle);
                if (!bOk) Logger.NoteDiagnosis($"桌面版登记 bundles 失败（{ensureBundle}）：{bDetail}");
            }
            else if (!ok && pjBackup != null && pjPath != null)
            {
                try
                {
                    File.WriteAllText(pjPath, pjBackup, new System.Text.UTF8Encoding(false));
                    Logger.NoteDiagnosis($"桌面版命令未完整提交（{ensureBundle}），已把 package.json 还原到执行前");
                }
                catch (Exception ex) { Logger.LogError("RunPluginCmdAsync.restore", ex); }
            }
            else if (!completionKnown)
            {
                Logger.NoteDiagnosis($"桌面版命令未完整提交（{ensureBundle}），本次不补登 bundles、也不把半提交当成成功"
                    + (completionDetail.Length > 0 ? $"：{completionDetail}" : ""));
            }
        }
        return (ok, output);
    }

    /// <summary>桌面版 profile 的写命令是否必须因桌面版仍在运行而被拦下（纯函数，便于自检）。</summary>
    internal static bool ShouldBlockDesktopPluginCommand(string? profileDir, bool desktopRunning)
        => !string.IsNullOrWhiteSpace(profileDir) && desktopRunning;

    /// <summary>
    /// 判断命令结束后的磁盘状态是否真的形成了桌面版完整提交。
    /// 用 <c>cmdOk:true</c> 调四方判定，只问磁盘事实；这样就能区分"pnpm 返回非零但四方都落盘"
    /// 与"包目录/清单已变、锁文件或 pnpm 元数据仍旧"两种现场。
    /// </summary>
    internal static bool PnpmCommandLooksCommitted(string packageName, string targetVersion, string profileDir,
                                                   string? expectedCommitBefore)
    {
        var r = EvaluateUpdate(packageName, targetVersion, cmdOk: true, commandOutput: "", profileDir,
            expectedCommitBefore, requirePnpmCommit: true);
        return r.Measured && r.Succeeded;
    }

    /// <summary>
    /// 桌面版 npm 插件安装/重装的统一提交判据。命令退出码只能说明 pnpm 没报错，
    /// 不能证明插件清单、锁文件、安装目录和 pnpm 元数据是一起提交的。这里只返回
    /// 可复核的完整提交：四处都健康，安装目录版本能落到清单声明范围，且锁文件解析版本与安装目录一致。
    /// 目标版本为空时（例如未钉版本的市场安装）以清单为准，仍要求三处版本自洽。
    /// </summary>
    internal static bool PnpmRegistryInstallLooksCommitted(string packageName, string? expectedVersion,
                                                           string profileDir, out string detail)
    {
        detail = "";
        string name = (packageName ?? "").Trim();
        string dir = (profileDir ?? "").Trim();
        if (!PluginManager.IsValidPackageName(name) || dir.Length == 0)
        {
            detail = "包名或 profile 目录无效";
            return false;
        }

        string declared = PluginManager.DepSpec(name, dir);
        if (PluginSource.Classify(declared) != PluginSource.Kind.Registry)
        {
            detail = $"清单里的 {name} 不是 npm registry 来源";
            return false;
        }

        var state = PluginManager.EvaluateInstallState(name, dir);
        var health = PluginManager.EvaluatePnpmInstallHealth(dir);
        string installed = PluginManager.ReadInstalledVersion(dir, name);
        string locked = PluginManager.ReadLockedVersion(dir, name);
        if (state != PluginManager.InstallStateKind.Installed)
        {
            detail = $"安装目录状态={state}";
            return false;
        }
        if (!health.ProfilePresent || !health.LockfilePresent || !health.NodeModulesPresent
            || !health.MetadataPresent || health.NeedsRepair)
        {
            detail = $"pnpm 依赖树不完整：{health.Note}";
            return false;
        }
        if (installed.Length == 0 || locked.Length == 0)
        {
            detail = $"安装目录={VersionOrUnknown(installed)}、锁文件={VersionOrUnknown(locked)}";
            return false;
        }

        var manifestVerdict = PluginManager.CompareInstalledToTarget(installed, declared);
        var lockVerdict = PluginManager.CompareInstalledToTarget(locked, installed);
        if (!lockVerdict.Satisfied)
        {
            detail = $"安装目录={installed}、锁文件={locked}：{lockVerdict.Note}";
            return false;
        }

        // 精确版本、^ / ~ 与通配/范围一律要求锁文件根 importer 保留同一 specifier（判据与
        // EvaluateUpdate 共用 RegistryManifestCommitted，只留一份）。pnpm 每次提交都会同步写
        // 清单与锁文件这两处，不一致即半提交——版本号碰巧相同也失败关闭。
        string lockedSpecifier = PluginManager.ReadLockedSpecifier(dir, name);
        bool manifestCommitted = PluginManager.RegistryManifestCommitted(
            declared, lockedSpecifier, manifestVerdict.Check, rangeFactsSatisfied: true);
        if (!manifestCommitted)
        {
            detail = $"安装目录={installed}、清单={declared}、锁文件specifier={VersionOrUnknown(lockedSpecifier)}：{manifestVerdict.Note}";
            return false;
        }

        // expected 只接受调用方已经解析出的具体版本；范围声明必须走上面的 manifest 模式，
        // 不能在这里再次剥出下限冒充精确目标。非法/范围值显式失败关闭，防止未来调用点复现 P1。
        string expected = (expectedVersion ?? "").Trim().TrimStart('v');
        if (expected.Length > 0)
        {
            if (!PluginManager.IsConcreteVersionSpec(expected))
            {
                detail = $"目标版本不是具体版本：{expected}";
                return false;
            }
            var installedExpected = PluginManager.CompareInstalledToTarget(installed, expected);
            var lockedExpected = PluginManager.CompareInstalledToTarget(locked, expected);
            if (!installedExpected.Satisfied || !lockedExpected.Satisfied)
            {
                detail = $"安装目录={installed}、锁文件={locked}、目标={expected}";
                return false;
            }
        }

        detail = $"安装目录 / 清单 / 锁文件 / pnpm 元数据已完整提交（{installed}）";
        return true;
    }

    /// <summary>只关心结论时使用；诊断日志需要完整原因时用带 out 参数的重载。</summary>
    internal static bool PnpmRegistryInstallLooksCommitted(string packageName, string? expectedVersion, string profileDir)
        => PnpmRegistryInstallLooksCommitted(packageName, expectedVersion, profileDir, out _);

    /// <summary>
    /// 「按清单重装」的唯一提交判据：不把范围原点的数字当作目标版本，而是验证安装目录满足
    /// 清单声明、锁文件解析到同一版本、锁文件 specifier 与清单逐字一致、pnpm 元数据完整。
    /// 重装命令跑完后的内部补登门槛与调用方最终判定必须共用这一入口。
    /// </summary>
    internal static bool PnpmRegistryManifestLooksCommitted(string packageName, string profileDir, out string detail)
        => PnpmRegistryInstallLooksCommitted(packageName, "", profileDir, out detail);

    /// <summary>
    /// 桌面版 npm 来源是否需要四方提交证明。判据取“本次命令的来源”，不能取命令结束后的清单：
    /// 命令退出码为 0、但 package.json 根本没写入时，DepSpec 为空；若据此跳过 gate，旧口径会把
    /// “退出码 0”误记成安装成功。来源分类与包名白名单均不读执行后的清单，因此该判据可以失败关闭。
    /// </summary>
    internal static bool RegistryInstallNeedsCommitProof(string? source, string? packageName, string? profileDir)
    {
        string name = (packageName ?? "").Trim();
        return !string.IsNullOrWhiteSpace(profileDir)
               && PluginManager.IsValidPackageName(name)
               && PluginSource.Classify(source) == PluginSource.Kind.Registry;
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

    // ═════════════ 2.4.3 Profile 目录占用侦察与一键解除 ═════════════

    private bool _unlockingProfile;

    private async void UnlockProfile_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_unlockingProfile) return;
        if (_updatingBusy || _pluginWriteBusy || _uninstalling || _installing)
        {
            AddEvent("当前正在执行插件安装、更新或卸载，请先等待完成或中止后再解除占用", EventKind.Warn);
            return;
        }

        _unlockingProfile = true;
        var scope = _ctx.Target;
        string label = _ctx.Label;
        string profDir = GuardPaths.ProfileDirFor(scope);
        try
        {
            SetProgress($"正在扫描 {label} profile 目录占用进程…");
            var entries = await Task.Run(() => ProfileLockManager.ScanProfileLocks(profDir));
            SetProgress("");

            bool hostRunning = entries.Any(x => x.Kind == ProfileLockManager.OccupierKind.DshHostProcess)
                || (scope == GuardTarget.Desktop && DesktopAppRunning())
                || (scope == GuardTarget.Web && (_isRunning || NetworkHelper.IsPortListening(_port)));

            if (entries.Count == 0 && !hostRunning)
            {
                AddEvent($"{label} profile 目录未检测到残留进程占用", EventKind.Good);
                GuardDialog.Show(
                    $"当前 {label} profile 目录未检测到任何残留插件进程或文件锁占用，可正常进行插件安装、更新与卸载。",
                    "解除目录占用",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (hostRunning)
            {
                var residuesWithHost = entries.Where(x => x.Kind == ProfileLockManager.OccupierKind.PluginResidue).ToList();
                string extraLine = residuesWithHost.Count > 0
                    ? $"\n· 同时检测到 {residuesWithHost.Count} 个关联插件子进程"
                    : "";
                var hostAns = GuardDialog.ShowCustom(
                    $"检测到 DSH {label} 主程序仍在运行，这会持续占用 profile 目录文件。{extraLine}\n\n" +
                    "是否先结束 DSH，再扫描并清理残留的插件子进程？\n" +
                    "· 不会关闭 DSHGuard 本身\n" +
                    "· DSH 中未保存的会话内容会丢失",
                    "解除目录占用",
                    MessageBoxImage.Warning,
                    new GuardDialog.DialogButton("结束 DSH 并清理", MessageBoxResult.Yes, Color.FromRgb(0xFF, 0x3B, 0x30), IsDefault: true),
                    new GuardDialog.DialogButton("取消", MessageBoxResult.No, Color.FromRgb(0x8E, 0x8E, 0x93), IsCancel: true));
                if (hostAns != MessageBoxResult.Yes) return;

                SetProgress($"正在结束 {label} 并清理残留插件进程…");
                if (scope == GuardTarget.Desktop)
                {
                    StopDesktopAppQuiet();
                    for (int i = 0; i < 32 && DesktopAppRunning(); i++)
                        await Task.Delay(250);
                }
                else
                {
                    await GracefulStopAsync();
                    if (NetworkHelper.IsPortListening(_port))
                        await Task.Run(() => ProcessManager.ForceClosePort(_port, 10, out _));
                    await Task.Delay(350);
                }

                entries = await Task.Run(() => ProfileLockManager.ScanProfileLocks(profDir));
                SetProgress("");
                if (entries.Count == 0)
                {
                    AddEvent($"已结束 {label}，目录占用已全部释放", EventKind.Good);
                    GuardDialog.Show(
                        $"已结束 {label}，当前 profile 目录已无任何占用。",
                        "解除目录占用",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }
            }

            var residues = entries.Where(x => x.Kind == ProfileLockManager.OccupierKind.PluginResidue).ToList();
            var externals = entries.Where(x => x.Kind == ProfileLockManager.OccupierKind.ExternalApp).ToList();

            if (residues.Count == 0 && externals.Count > 0)
            {
                string extLines = string.Join("\n", externals.Take(10).Select(x => $"· {x.ProcessName}（PID {x.Pid}）— {x.Reason}"));
                GuardDialog.Show(
                    $"未检测到残留插件进程，但发现以下外部程序正在打开 {label} profile 目录内的文件：\n\n" +
                    extLines +
                    "\n\n为避免误伤你的工作区，守护壳不会自动强杀外部软件，请手动关闭对应窗口后再操作。",
                    "解除目录占用 · 需手动关闭外部软件",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (residues.Count == 0)
            {
                AddEvent($"{label} profile 目录占用已释放", EventKind.Good);
                return;
            }

            var sb = new System.Text.StringBuilder();
            sb.Append($"检测到 {residues.Count} 个滞留在 {label} profile 目录内的残留插件进程：\n\n");
            foreach (var r in residues.Take(12))
                sb.Append($"· {r.ProcessName}（PID {r.Pid}）— {r.Reason}\n");
            if (residues.Count > 12)
                sb.Append($"… 另有 {residues.Count - 12} 个同类进程\n");
            if (externals.Count > 0)
            {
                sb.Append($"\n另有 {externals.Count} 个外部程序持有文件（不会自动终止，请按需手动关闭）：\n");
                foreach (var ex in externals.Take(5))
                    sb.Append($"· {ex.ProcessName}（PID {ex.Pid}）\n");
            }
            sb.Append("\n是否立即终止这些残留插件进程以解除目录占用？");

            var confirm = GuardDialog.ShowCustom(
                sb.ToString(),
                "解除目录占用",
                MessageBoxImage.Question,
                new GuardDialog.DialogButton("立即解除占用", MessageBoxResult.Yes, Color.FromRgb(0xFF, 0x9F, 0x0A), IsDefault: true),
                new GuardDialog.DialogButton("取消", MessageBoxResult.No, Color.FromRgb(0x8E, 0x8E, 0x93), IsCancel: true));
            if (confirm != MessageBoxResult.Yes) return;

            SetProgress($"正在结束 {residues.Count} 个残留插件进程…");
            var (closed, killed, failed) = await Task.Run(() => ProfileLockManager.ReleasePluginResidues(residues, scope));
            SetProgress("");

            int doneCount = closed + killed;
            if (failed > 0)
            {
                AddEvent($"已解除 {doneCount} 个残留插件进程占用，另有 {failed} 个终止失败（详见日志）", EventKind.Warn);
                GuardDialog.Show(
                    $"已结束 {doneCount} 个残留进程，但有 {failed} 个进程未能终止（可能权限更高或已被系统锁定）。\n\n可在「日志」页查看详情。",
                    "解除目录占用",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            else
            {
                AddEvent($"已解除 {doneCount} 个残留插件进程的目录占用", EventKind.Good);
                GuardDialog.Show(
                    $"已成功结束 {doneCount} 个残留插件进程，{label} profile 目录占用已解除。",
                    "解除目录占用完成",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            SetProgress("");
            Logger.LogError("UnlockProfile_Click", ex);
            GuardDialog.Show("扫描或解除占用时出错：" + ex.Message, "解除目录占用", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _unlockingProfile = false;
        }
    }
}
