using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace DSHGuard;

/// <summary>
/// 2026-10-23：桌面版 pnpm-workspace.yaml 自检/自愈的回归断言。
/// 全部使用纯函数或临时目录，不接触真实 profile。
/// </summary>
public static partial class SelfTest
{
    internal static void RunPnpmRepairChecks(Action<string, bool, string> check)
    {
        const string workspace =
            "packages:\r\n" +
            "  - .\r\n" +
            "\r\n" +
            "patchedDependencies:\r\n" +
            "  dsh-purge@1.1.71: patches/dsh-purge@1.1.71.patch\r\n" +
            "\r\n" +
            "allowBuilds:\r\n" +
            "  keep-true: true\r\n" +
            "  '@google/genai': set this to true or false\r\n" +
            "  ssh2: false\r\n" +
            "\r\n" +
            "gitShallowHosts:\r\n" +
            "  - github.com\r\n";

        var plan = PluginManager.PlanPnpmWorkspaceRepair(
            workspace,
            pkg => pkg == "dsh-purge" ? "1.1.73" : "",
            unusedPatchKeys: null,
            ignoredBuildPackages: new[] { "@google/genai", "cloudflared", "@cloudflare/workers-types" });

        check("pnpm 自检 · 已装 1.1.73 时应移除 1.1.71 的失效补丁，并移除空块头",
            plan.Changed && plan.RemovedPatchCount == 1 &&
            !plan.Text.Contains("patchedDependencies:", StringComparison.Ordinal) &&
            !plan.Text.Contains("dsh-purge@1.1.71", StringComparison.Ordinal),
            string.Join("；", plan.Actions));

        check("pnpm 自检 · allowBuilds 占位值改成保守的 false，已有 true/false 不误改，并按错误点名补缺项",
            plan.NormalizedBuildCount == 1 && plan.AddedBuildCount == 2 &&
            plan.Text.Contains("'@google/genai': false", StringComparison.Ordinal) &&
            plan.Text.Contains("'cloudflared': false", StringComparison.Ordinal) &&
            plan.Text.Contains("keep-true: true", StringComparison.Ordinal) &&
            plan.Text.Contains("ssh2: false", StringComparison.Ordinal) &&
            !plan.Text.Contains("set this to true or false", StringComparison.Ordinal),
            $"normalized={plan.NormalizedBuildCount} added={plan.AddedBuildCount}\n{plan.Text}");

        check("pnpm 自检 · 新增缺失的 scoped allowBuilds 项必须写成 YAML 字符串键，不能生成裸 @scope/name",
            plan.Text.Contains("  '@cloudflare/workers-types': false", StringComparison.Ordinal) &&
            !plan.Text.Contains("  @cloudflare/workers-types: false", StringComparison.Ordinal),
            plan.Text);
        const string matchingPatch =
            "patchedDependencies:\n" +
            "  dsh-purge@1.1.73: patches/dsh-purge@1.1.73.patch\n";
        var kept = PluginManager.PlanPnpmWorkspaceRepair(
            matchingPatch, pkg => pkg == "dsh-purge" ? "1.1.73" : "");
        check("pnpm 自检 · 补丁版本与已装版本一致时必须保留，绝不无差别清空",
            !kept.Changed && kept.Text == matchingPatch &&
            kept.Text.Contains("dsh-purge@1.1.73", StringComparison.Ordinal),
            $"changed={kept.Changed}");

        const string scopedPatch =
            "patchedDependencies:\n" +
            "  '@scope/tool@2.0.0': patches/tool.patch\n";
        var scoped = PluginManager.PlanPnpmWorkspaceRepair(
            scopedPatch, pkg => pkg == "@scope/tool" ? "2.1.0" : "");
        check("pnpm 自检 · scoped 包名也能正确拆分包名/版本并识别失效补丁",
            scoped.Changed && scoped.RemovedPatchCount == 1 &&
            !scoped.Text.Contains("@scope/tool@2.0.0", StringComparison.Ordinal),
            string.Join("；", scoped.Actions));

        const string ignoredError =
            "Error: ERR_PNPM_IGNORED_BUILDS\n" +
            "  × adding a new package\n" +
            "  ╰─▶ Ignored build scripts: @google/genai@1.52.0, @google/genai@2.21.0,\n" +
            "      cloudflared@0.7.3, cpu-features@0.0.10, ssh2@1.17.0\n" +
            "  help: Run \"pnpm approve-builds\" to pick which dependencies should be allowed\n";
        var ignored = PluginManager.ParseIgnoredBuildPackages(ignoredError);
        check("pnpm 自检 · 从 ERR_PNPM_IGNORED_BUILDS 提取包名（去版本、去重、保留 scope）",
            ignored.Count == 4 &&
            ignored.Contains("@google/genai") && ignored.Contains("cloudflared") &&
            ignored.Contains("cpu-features") && ignored.Contains("ssh2"),
            string.Join("、", ignored));

        const string unusedError =
            "Error: ERR_PNPM_UNUSED_PATCH\n" +
            "  × adding a new package\n" +
            "  ╰─▶ The following patches were not used: dsh-purge@1.1.71\n" +
            "  help: Either remove them from \"patchedDependencies\" or update them to match packages in your dependencies.\n";
        var unused = PluginManager.ParseUnusedPatchKeys(unusedError);
        check("pnpm 自检 · 从 ERR_PNPM_UNUSED_PATCH 提取完整补丁键",
            unused.Count == 1 && unused[0] == "dsh-purge@1.1.71" &&
            PluginManager.LooksLikePnpmWorkspaceRepairError(unusedError) &&
            PluginManager.LooksLikePnpmWorkspaceRepairError(ignoredError) &&
            !PluginManager.LooksLikePnpmWorkspaceRepairError("ERR_PNPM_FETCH_404"),
            string.Join("、", unused));

        string dir = Path.Combine(Path.GetTempPath(), "dshguard-pnpmrepair-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "pnpm-workspace.yaml");
            File.WriteAllText(path, workspace);
            var fileRepair = PluginManager.RepairDesktopPnpmWorkspace(
                dir, new[] { "dsh-purge@1.1.71" }, new[] { "cloudflared" });
            string after = File.ReadAllText(path);
            int backups = Directory.GetFiles(dir, "pnpm-workspace.yaml.bak-pnpmrepair-*").Length;
            check("pnpm 自检 · 临时 profile 自愈写盘：先备份、原子写、写后复核，补丁与占位项都消掉",
                fileRepair.Ok && fileRepair.Detail.Contains("备份", StringComparison.Ordinal) &&
                backups == 1 &&
                !after.Contains("dsh-purge@1.1.71", StringComparison.Ordinal) &&
                !after.Contains("set this to true or false", StringComparison.Ordinal) &&
                after.Contains("'cloudflared': false", StringComparison.Ordinal),
                $"Ok={fileRepair.Ok} backups={backups} detail={fileRepair.Detail}");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }

        const string lockSample =
            "lockfileVersion: '9.0'\n\n" +
            "importers:\n\n" +
            "  .:\n" +
            "    dependencies:\n" +
            "      '@scope/tool':\n" +
            "        specifier: 2.0.0\n" +
            "        version: 2.0.0(react@18.3.1)\n" +
            "      plain-tool:\n" +
            "        specifier: 1.9.0\n" +
            "        version: 1.9.0\n" +
            "      range-tool:\n" +
            "        specifier: ^1.2.3 || >=2.0.0 <3.0.0\n" +
            "        version: 2.1.4\n\n" +
            "packages:\n\n" +
            "  '@scope/tool@9.9.9': {}\n";
        const string otherImporterOnly = "lockfileVersion: '9.0'\n\nimporters:\n\n" +
            "  .:\n    dependencies: {}\n\n" +
            "  packages/other:\n    dependencies:\n      plain-tool:\n" +
            "        specifier: 9.9.9\n        version: 9.9.9\n";
        check("pnpm 安装树自检 · 锁文件只读根 importer，scoped 包与 peer 后缀都能取出真实版本，其它 workspace importer 不冒充根包",
            PluginManager.LockedImporterVersion(lockSample, "@scope/tool") == "2.0.0" &&
            PluginManager.LockedImporterVersion(lockSample, "plain-tool") == "1.9.0" &&
            PluginManager.LockedImporterSpecifier(lockSample, "@scope/tool") == "2.0.0" &&
            PluginManager.LockedImporterSpecifier(lockSample, "range-tool") == "^1.2.3 || >=2.0.0 <3.0.0" &&
            PluginManager.LockedImporterVersion(lockSample, "missing-tool") == "" &&
            PluginManager.LockedImporterVersion(otherImporterOnly, "plain-tool") == "",
            $"scope={PluginManager.LockedImporterVersion(lockSample, "@scope/tool")} · plain={PluginManager.LockedImporterVersion(lockSample, "plain-tool")} · other={PluginManager.LockedImporterVersion(otherImporterOnly, "plain-tool")}");

        check("pnpm 安装树自检 · os error 32 / os error 5 / EPERM / EBUSY / 中文占用文案各自单独出现都识别为文件锁，网络与路径错误不误判",
            PluginManager.LooksLikePnpmFileLock("EPERM: operation not permitted, rename 'C:\\p\\a' -> 'C:\\p\\b'") &&
            PluginManager.LooksLikePnpmFileLock("Access is denied. (os error 5)") &&
            PluginManager.LooksLikePnpmFileLock("另一个程序正在使用此文件") &&
            PluginManager.LooksLikePnpmFileLock("ERR_PNPM_EBUSY resource busy or locked") &&
            PluginManager.LooksLikePnpmFileLock("rename 失败：EBUSY，请关闭占用它的程序") &&
            !PluginManager.LooksLikePnpmFileLock("ERR_PNPM_FETCH_404 not found") &&
            !PluginManager.LooksLikePnpmFileLock("os error 53: The network path was not found"),
            PluginManager.LooksLikePnpmFileLock("os error 32").ToString());
        check("pnpm 安装树自检 · 最终命令锁失败的可操作提示只对占用/拒绝访问出现，普通供应链错误不误导用户停 DSH",
            PluginManager.PnpmFailureHint("Access is denied. (os error 5)") == PluginManager.PnpmFileLockRetryHint &&
            PluginManager.PnpmFailureHint("EPERM: operation not permitted") == PluginManager.PnpmFileLockRetryHint &&
            PluginManager.PnpmFailureHint("ERR_PNPM_FETCH_404 not found") == "",
            $"os5=«{PluginManager.PnpmFailureHint("Access is denied. (os error 5)")}» · 404=«{PluginManager.PnpmFailureHint("ERR_PNPM_FETCH_404 not found")}»");

        string stateDir = Path.Combine(Path.GetTempPath(), "dshguard-pnpmstate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stateDir);
        try
        {
            string statePkg = Path.Combine(stateDir, "package.json");
            string stateLock = Path.Combine(stateDir, "pnpm-lock.yaml");
            string stateModules = Path.Combine(stateDir, "node_modules");
            string okPkg = Path.Combine(stateModules, "dsh-ok", "package.json");
            string halfDir = Path.Combine(stateModules, "@scope", "half");
            string halfPkg = Path.Combine(halfDir, "package.json");
            Directory.CreateDirectory(Path.GetDirectoryName(okPkg)!);
            Directory.CreateDirectory(halfDir);
            File.WriteAllText(statePkg,
                "{\"dependencies\":{\"dsh-ok\":\"1.0.0\",\"@scope/half\":\"2.0.0\"}}");
            File.WriteAllText(okPkg, "{\"name\":\"dsh-ok\",\"version\":\"1.0.0\"}");
            File.WriteAllText(halfPkg, "{\"name\":\"@scope/half\",\"version\":\"2.0.0\"}");
            File.WriteAllText(stateLock,
                "lockfileVersion: '9.0'\nimporters:\n  .:\n    dependencies:\n"
                + "      dsh-ok:\n        specifier: 1.0.0\n        version: 1.0.0\n"
                + "      '@scope/half':\n        specifier: 2.0.0\n        version: 2.0.0\n");

            var noMeta = PluginManager.EvaluatePnpmInstallHealth(stateDir);
            check("pnpm 安装树自检 · 包目录齐全但 .modules.yaml/.pnpm 缺失 ⇒ 必须要求先恢复依赖树",
                noMeta.NeedsRepair && !noMeta.MetadataPresent && noMeta.BrokenPackages.Count == 0,
                noMeta.Note);

            string repairArgsWithoutLock = PluginManager.BuildPnpmRepairInstallArgs(lockfilePresent: false);
            string repairArgsWithLock = PluginManager.BuildPnpmRepairInstallArgs(lockfilePresent: true);
            check("pnpm 安装树自检 · 无锁文件直接普通 install，有锁文件才先跑 frozen install",
                !repairArgsWithoutLock.Contains("--frozen-lockfile", StringComparison.Ordinal) &&
                repairArgsWithLock.Contains("--frozen-lockfile", StringComparison.Ordinal),
                $"无锁=«{repairArgsWithoutLock}» · 有锁=«{repairArgsWithLock}»");

            File.WriteAllText(Path.Combine(stateModules, ".modules.yaml"), "nodeLinker: isolated\n");
            var isolatedNoStore = PluginManager.EvaluatePnpmInstallHealth(stateDir);
            check("pnpm 安装树自检 · isolated 布局缺 .pnpm ⇒ 元数据仍判不完整",
                isolatedNoStore.NeedsRepair && !isolatedNoStore.MetadataPresent,
                isolatedNoStore.Note);

            File.WriteAllText(Path.Combine(stateModules, ".modules.yaml"), "nodeLinker: hoisted\n");
            var hoistedNoStore = PluginManager.EvaluatePnpmInstallHealth(stateDir);
            check("pnpm 安装树自检 · hoisted 布局没有 .pnpm 仍属元数据完整，不误报损坏",
                !hoistedNoStore.NeedsRepair && hoistedNoStore.MetadataPresent,
                hoistedNoStore.Note);

            File.Delete(halfPkg);
            var broken = PluginManager.EvaluatePnpmInstallHealth(stateDir);
            check("pnpm 安装树自检 · 目录在、package.json 缺的声明依赖会被点名，且触发修复",
                broken.NeedsRepair && broken.BrokenPackages.Count == 1 &&
                broken.BrokenPackages[0] == "@scope/half" && broken.MetadataPresent,
                broken.Note + " · broken=" + string.Join("、", broken.BrokenPackages));

            var cleaned = PluginManager.CleanBrokenInstall("@scope/half", stateDir);
            Directory.CreateDirectory(halfDir);
            File.WriteAllText(Path.Combine(halfDir, "package.json"),
                "{\"name\":\"@scope/half\",\"version\":\"2.0.0\"}");
            var healthy = PluginManager.EvaluatePnpmInstallHealth(stateDir);
            check("pnpm 安装树自检 · 清残目录并补回完整包后恢复健康（不把正常目录误判为损坏）",
                cleaned.Cleared && !healthy.NeedsRepair && healthy.MetadataPresent &&
                healthy.MissingPackages.Count == 0 && healthy.BrokenPackages.Count == 0,
                $"cleared={cleaned.Cleared} · {healthy.Note}");
        }
        finally
        {
            try { Directory.Delete(stateDir, recursive: true); } catch { }
        }

        // junction（reparse point）拒删回归：node_modules\<包名> 恰好是个链接、链接目标里没有
        // package.json（半截形态）时，清理必须拒绝递归删除——否则会隔着链接把目标里的东西一并删掉。
        // 诱饵目录里放一只 canary；清理越界跟进链接，canary 就会消失。
        string junctionDir = Path.Combine(Path.GetTempPath(), "dshguard-pnpmjunction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(junctionDir);
        try
        {
            string decoyRoot = Path.Combine(junctionDir, "decoy");
            string modules = Path.Combine(junctionDir, "node_modules");
            Directory.CreateDirectory(decoyRoot);
            Directory.CreateDirectory(modules);
            string canary = Path.Combine(decoyRoot, "canary.txt");
            File.WriteAllText(canary, "keep");
            string link = Path.Combine(modules, "dsh-trap");
            string mkErr = "";
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{decoyRoot}\"")
                { CreateNoWindow = true, UseShellExecute = false };
                using var mk = System.Diagnostics.Process.Start(psi);
                if (mk == null || !mk.WaitForExit(5000)) mkErr = "mklink 未在 5 秒内返回";
            }
            catch (Exception ex) { mkErr = ex.Message; }
            var trapClean = PluginManager.CleanBrokenInstall("dsh-trap", junctionDir);
            check("pnpm 安装树自检 · 残留目录是 junction（reparse point）⇒ 拒绝递归删除，链接目标毫发无损",
                mkErr.Length == 0 && Directory.Exists(link) && !File.Exists(Path.Combine(link, "package.json")) &&
                trapClean.Rejected && !trapClean.Cleared &&
                File.Exists(canary) && Directory.Exists(link),
                $"mkErr={mkErr} · rejected={trapClean.Rejected} cleared={trapClean.Cleared}" +
                $" · canary在={File.Exists(canary)} · {trapClean.Reason}");
        // 复审补洞：目标 junction 本身带 package.json、祖先目录是 junction、悬空 junction，
        // 三种形态都必须在任何业务分类之前被拒绝；否则 Directory.Exists/File.Exists 会先把它们
        // 归成“完好/不存在”，绕过递归删除闸门。
        string fullLinkRoot = Path.Combine(Path.GetTempPath(), "dshguard-pnpmfulllink-" + Guid.NewGuid().ToString("N"));
        string fullLink = Path.Combine(fullLinkRoot, "node_modules", "dsh-full-link");
        try
        {
            string decoy = Path.Combine(fullLinkRoot, "decoy");
            Directory.CreateDirectory(decoy);
            Directory.CreateDirectory(Path.GetDirectoryName(fullLink)!);
            File.WriteAllText(Path.Combine(decoy, "package.json"), "{\"name\":\"dsh-full-link\",\"version\":\"1.0.0\"}");
            string fullCanary = Path.Combine(decoy, "canary.txt");
            File.WriteAllText(fullCanary, "keep");
            var mk = TryCreateJunctionForTest(fullLink, decoy);
            var clean = PluginManager.CleanBrokenInstall("dsh-full-link", fullLinkRoot);
            check("pnpm 安装树自检 · 目标 junction 即使含 package.json 也拒绝递归删除，链接目标与清单均保持原样",
                mk.Ok && clean.Rejected && !clean.Cleared && Directory.Exists(fullLink) &&
                File.Exists(fullCanary) && File.Exists(Path.Combine(decoy, "package.json")),
                $"mk={mk.Ok}/{mk.Error} · rejected={clean.Rejected} cleared={clean.Cleared} · {clean.Reason}");
        }
        finally
        {
            try { Directory.Delete(fullLink, recursive: false); } catch { }
            try { if (Directory.Exists(fullLinkRoot)) Directory.Delete(fullLinkRoot, recursive: true); } catch { }
        }

        string ancestorRoot = Path.Combine(Path.GetTempPath(), "dshguard-pnpmancestor-" + Guid.NewGuid().ToString("N"));
        string ancestorLink = Path.Combine(ancestorRoot, "node_modules", "@scope");
        try
        {
            string decoyScope = Path.Combine(ancestorRoot, "decoy-scope");
            Directory.CreateDirectory(Path.Combine(decoyScope, "dsh-child"));
            Directory.CreateDirectory(Path.GetDirectoryName(ancestorLink)!);
            string ancestorCanary = Path.Combine(decoyScope, "dsh-child", "canary.txt");
            File.WriteAllText(ancestorCanary, "keep");
            var mk = TryCreateJunctionForTest(ancestorLink, decoyScope);
            var clean = PluginManager.CleanBrokenInstall("@scope/dsh-child", ancestorRoot);
            check("pnpm 安装树自检 · 祖先 @scope 是 junction 时拒绝向下清理，诱饵目录不受伤",
                mk.Ok && clean.Rejected && !clean.Cleared && File.Exists(ancestorCanary) && Directory.Exists(ancestorLink),
                $"mk={mk.Ok}/{mk.Error} · rejected={clean.Rejected} cleared={clean.Cleared} · 诱饵在={File.Exists(ancestorCanary)} · {clean.Reason}");
        }
        finally
        {
            try { Directory.Delete(ancestorLink, recursive: false); } catch { }
            try { if (Directory.Exists(ancestorRoot)) Directory.Delete(ancestorRoot, recursive: true); } catch { }
        }

        string danglingRoot = Path.Combine(Path.GetTempPath(), "dshguard-pnpmdangling-" + Guid.NewGuid().ToString("N"));
        string danglingLink = Path.Combine(danglingRoot, "node_modules", "dsh-dangling");
        try
        {
            string brokenTarget = Path.Combine(danglingRoot, "missing-target");
            Directory.CreateDirectory(brokenTarget);
            Directory.CreateDirectory(Path.GetDirectoryName(danglingLink)!);
            var mk = TryCreateJunctionForTest(danglingLink, brokenTarget);
            Directory.Delete(brokenTarget, recursive: true);       // 制造悬空链接
            bool stillLink;
            try { stillLink = new DirectoryInfo(danglingLink).LinkTarget != null; }
            catch { stillLink = false; }
            var clean = PluginManager.CleanBrokenInstall("dsh-dangling", danglingRoot);
            check("pnpm 安装树自检 · 悬空 junction 也拒绝清理（Directory.Exists=false 不能把它当成“无需清理”）",
                mk.Ok && stillLink && clean.Rejected && !clean.Cleared,
                $"mk={mk.Ok}/{mk.Error} · stillLink={stillLink} · rejected={clean.Rejected} cleared={clean.Cleared} · {clean.Reason}");
        }
        finally
        {
            try { Directory.Delete(danglingLink, recursive: false); } catch { }
            try { if (Directory.Exists(danglingRoot)) Directory.Delete(danglingRoot, recursive: true); } catch { }
        }

        string refusedRepairRoot = Path.Combine(Path.GetTempPath(), "dshguard-pnpmrefused-" + Guid.NewGuid().ToString("N"));
        string refusedLink = Path.Combine(refusedRepairRoot, "node_modules", "dsh-trap");
        try
        {
            string decoy = Path.Combine(refusedRepairRoot, "decoy");
            Directory.CreateDirectory(decoy);
            Directory.CreateDirectory(Path.GetDirectoryName(refusedLink)!);
            File.WriteAllText(Path.Combine(refusedRepairRoot, "package.json"),
                "{\"dependencies\":{\"dsh-trap\":\"1.0.0\"}}");
            var mk = TryCreateJunctionForTest(refusedLink, decoy);
            int runArgsCalls = 0;
            var refused = MainWindow.RepairPnpmInstallTreeForTest(refusedRepairRoot, _ =>
            {
                runArgsCalls++;
                return Task.FromResult((true, "should not run"));
            }).GetAwaiter().GetResult();
            check("pnpm 安装树自检 · junction 被拒后提示“人工确认链接”，不得误说成停 DSH 就能解决，也不得继续跑 install",
                mk.Ok && !refused.Success && runArgsCalls == 0 &&
                refused.Output.Contains("人工确认", StringComparison.Ordinal) &&
                !refused.Output.Contains("建议先停止 DSH", StringComparison.Ordinal),
                $"mk={mk.Ok}/{mk.Error} · calls={runArgsCalls} · {refused.Output}");
        }
        finally
        {
            try { Directory.Delete(refusedLink, recursive: false); } catch { }
            try { if (Directory.Exists(refusedRepairRoot)) Directory.Delete(refusedRepairRoot, recursive: true); } catch { }
        }
        }
        finally
        {
            try { Directory.Delete(junctionDir, recursive: true); } catch { }
        }

        string commitDir = Path.Combine(Path.GetTempPath(), "dshguard-pnpmcommit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(commitDir);
        try
        {
            string modules = Path.Combine(commitDir, "node_modules");
            string pluginDir = Path.Combine(modules, "dsh-fixture");
            Directory.CreateDirectory(pluginDir);
            File.WriteAllText(Path.Combine(modules, ".modules.yaml"), "nodeLinker: hoisted\n");
            File.WriteAllText(Path.Combine(pluginDir, "package.json"),
                "{\"name\":\"dsh-fixture\",\"version\":\"1.2.3\"}");

            void WriteProfile(string manifestVersion, string lockVersion, string? lockSpecifier = null)
            {
                string specifier = (lockSpecifier ?? lockVersion).Replace("'", "''", StringComparison.Ordinal);
                File.WriteAllText(Path.Combine(commitDir, "package.json"),
                    "{\"dependencies\":{\"dsh-fixture\":\"" + manifestVersion + "\"}}");
                File.WriteAllText(Path.Combine(commitDir, "pnpm-lock.yaml"),
                    "lockfileVersion: '9.0'\nimporters:\n  .:\n    dependencies:\n"
                    + "      dsh-fixture:\n        specifier: '" + specifier
                    + "'\n        version: " + lockVersion + "\n");
            }

            WriteProfile("1.2.3", "1.2.3");
            var committed = MainWindow.EvaluateUpdate("dsh-fixture", "1.2.3", false, "", commitDir, requirePnpmCommit: true);
            check("pnpm 安装树自检 · 命令非零但安装目录/清单/锁文件三方均为目标且元数据完整 ⇒ 判成功并记为虚惊",
                committed.Succeeded && committed.Measured && committed.NoteDowngraded &&
                committed.Check == PluginManager.VersionCheck.Satisfied,
                committed.Note);
            var completeCommitProof = MainWindow.PnpmCommandLooksCommitted("dsh-fixture", "1.2.3", commitDir, null);
            var pCommitComplete = new PluginManager.Plugin { Name = "dsh-fixture" };
            var uCommitComplete = new PluginManager.PluginUpdate
                { Name = "dsh-fixture", Installed = "1.2.2", Latest = "1.2.3", HasUpdate = true };
            var batchCommitComplete = MainWindow.BatchUpdateVerdictForTest(
                pCommitComplete, uCommitComplete, true, "", commitDir, commitBefore: null, requirePnpmCommit: true);
            check("pnpm 安装树自检 · 完整四方提交时 RunPluginCmdAsync 的补登门槛与批量判定入口同时放行",
                completeCommitProof && batchCommitComplete.Succeeded && batchCommitComplete.Measured,
                $"looksCommitted={completeCommitProof} · batch={batchCommitComplete.Succeeded} · {batchCommitComplete.Note}");

            bool installCommit = MainWindow.PnpmRegistryInstallLooksCommitted(
                "dsh-fixture", "1.2.3", commitDir, out string installCommitDetail);
            bool unpinnedInstallCommit = MainWindow.PnpmRegistryInstallLooksCommitted(
                "dsh-fixture", "", commitDir, out string unpinnedInstallCommitDetail);
            bool rangeTargetRejected = MainWindow.PnpmRegistryInstallLooksCommitted(
                "dsh-fixture", "^1.2.0", commitDir, out string rangeTargetDetail);
            check("pnpm 安装树自检 · 市场安装/重装的四方判据：完整提交才放行，未钉版本时仍按清单自洽确认，范围目标拒绝",
                installCommit && unpinnedInstallCommit && !rangeTargetRejected &&
                installCommitDetail.Contains("完整提交", StringComparison.Ordinal) &&
                unpinnedInstallCommitDetail.Contains("完整提交", StringComparison.Ordinal) &&
                rangeTargetDetail.Contains("不是具体版本", StringComparison.Ordinal),
                $"pinned={installCommit}/{installCommitDetail} · unpinned={unpinnedInstallCommit}/{unpinnedInstallCommitDetail} · rangeTarget={rangeTargetRejected}/{rangeTargetDetail}");

            WriteProfile("*", "1.2.3", "*");
            var wildcardCommitted = MainWindow.EvaluateUpdate(
                "dsh-fixture", "1.2.3", true, "", commitDir, requirePnpmCommit: true);
            bool wildcardInstallCommit = MainWindow.PnpmRegistryInstallLooksCommitted(
                "dsh-fixture", "1.2.3", commitDir, out string wildcardInstallDetail);
            check("pnpm 安装树自检 · 合法通配声明 * 由锁文件 specifier 与版本交叉证明，不得把正常安装误判为半提交",
                wildcardCommitted.Succeeded && wildcardCommitted.Measured && wildcardInstallCommit,
                $"update={wildcardCommitted.Succeeded}/{wildcardCommitted.Note} · install={wildcardInstallCommit}/{wildcardInstallDetail}");

            WriteProfile(">=1.0.0 <2.0.0", "1.2.3", ">=1.0.0 <2.0.0");
            var comparatorCommitted = MainWindow.EvaluateUpdate(
                "dsh-fixture", "1.2.3", true, "", commitDir, requirePnpmCommit: true);
            bool comparatorInstallCommit = MainWindow.PnpmRegistryInstallLooksCommitted(
                "dsh-fixture", "1.2.3", commitDir, out string comparatorInstallDetail);
            check("pnpm 安装树自检 · 合法比较器范围由锁文件同一 specifier 证明，不得因无法抽出单一下限而卡住更新",
                comparatorCommitted.Succeeded && comparatorCommitted.Measured && comparatorInstallCommit,
                $"update={comparatorCommitted.Succeeded}/{comparatorCommitted.Note} · install={comparatorInstallCommit}/{comparatorInstallDetail}");

            WriteProfile("*", "1.2.3", "^0.1.0");
            var staleSpecifierCommitted = MainWindow.EvaluateUpdate(
                "dsh-fixture", "1.2.3", true, "", commitDir, requirePnpmCommit: true);
            bool staleSpecifierInstallCommit = MainWindow.PnpmRegistryInstallLooksCommitted(
                "dsh-fixture", "1.2.3", commitDir, out string staleSpecifierInstallDetail);
            check("pnpm 安装树自检 · 清单声明与锁文件 specifier 不一致时，即便版本号碰巧相同也必须失败关闭",
                !staleSpecifierCommitted.Succeeded && !staleSpecifierInstallCommit &&
                staleSpecifierCommitted.NotSatisfied && staleSpecifierInstallDetail.Contains("specifier", StringComparison.OrdinalIgnoreCase),
                $"update={staleSpecifierCommitted.Succeeded}/{staleSpecifierCommitted.Note} · install={staleSpecifierInstallCommit}/{staleSpecifierInstallDetail}");

            WriteProfile("1.2.3", "1.2.3", "^1.2.0");
            var exactStaleSpecifier = MainWindow.EvaluateUpdate(
                "dsh-fixture", "1.2.3", true, "", commitDir, requirePnpmCommit: true);
            bool exactStaleInstallCommit = MainWindow.PnpmRegistryInstallLooksCommitted(
                "dsh-fixture", "1.2.3", commitDir, out string exactStaleInstallDetail);
            check("pnpm 安装树自检 · 精确声明的旧 specifier（--save-exact 场景，版本碰巧相同）同样失败关闭，不得只拦范围类",
                !exactStaleSpecifier.Succeeded && !exactStaleInstallCommit &&
                exactStaleSpecifier.NotSatisfied,
                $"update={exactStaleSpecifier.Succeeded}/{exactStaleSpecifier.Note} · install={exactStaleInstallCommit}/{exactStaleInstallDetail}");

            bool exactStaleReinstallCommit = MainWindow.PnpmRegistryManifestLooksCommitted(
                "dsh-fixture", commitDir, out string exactStaleReinstallDetail);
            check("pnpm 安装树自检 · 按清单重装也拒绝旧范围 specifier，即便安装目录版本碰巧满足新清单",
                !exactStaleReinstallCommit &&
                exactStaleReinstallDetail.Contains("specifier", StringComparison.OrdinalIgnoreCase),
                exactStaleReinstallDetail);

            WriteProfile("1.x", "1.2.3", "1.x");
            var xRangeCommitted = MainWindow.EvaluateUpdate(
                "dsh-fixture", "1.2.3", true, "", commitDir, requirePnpmCommit: true);
            check("pnpm 安装树自检 · 1.x 范围声明端到端由 specifier 与版本交叉证明放行（补齐范围类判定用例）",
                xRangeCommitted.Succeeded && xRangeCommitted.Measured,
                xRangeCommitted.Note);

            WriteProfile("^1.2.0", "1.2.3", "^1.2.0");
            var caretCommitted = MainWindow.EvaluateUpdate(
                "dsh-fixture", "1.2.3", true, "", commitDir, requirePnpmCommit: true);
            bool caretInstallCommitted = MainWindow.PnpmRegistryInstallLooksCommitted(
                "dsh-fixture", "1.2.3", commitDir, out string caretInstallDetail);
            WriteProfile("~1.2.0", "1.2.3", "~1.2.0");
            var tildeCommitted = MainWindow.EvaluateUpdate(
                "dsh-fixture", "1.2.3", true, "", commitDir, requirePnpmCommit: true);
            bool tildeInstallCommitted = MainWindow.PnpmRegistryInstallLooksCommitted(
                "dsh-fixture", "1.2.3", commitDir, out string tildeInstallDetail);
            check("pnpm 安装树自检 · ^ / ~ 范围内升级到高于声明下限的版本必须成功（不能拿下限冒充目标）",
                caretCommitted.Succeeded && caretCommitted.Measured && caretInstallCommitted &&
                tildeCommitted.Succeeded && tildeCommitted.Measured && tildeInstallCommitted,
                $"caret={caretCommitted.Succeeded}/{caretCommitted.Note}; install={caretInstallCommitted}/{caretInstallDetail}; " +
                $"tilde={tildeCommitted.Succeeded}/{tildeCommitted.Note}; install={tildeInstallCommitted}/{tildeInstallDetail}");

            // 生产重装路径不传“下限版本”，而是按清单 + 锁文件 specifier 验证完整提交。
            // 这组用例直接走同一个入口，防止以后又把 ^/~ 的下限抽出来冒充重装目标。
            WriteProfile("^1.2.0", "1.2.3", "^1.2.0");
            bool caretReinstallCommit = MainWindow.PnpmRegistryManifestLooksCommitted(
                "dsh-fixture", commitDir, out string caretReinstallDetail);
            WriteProfile("~1.2.0", "1.2.3", "~1.2.0");
            bool tildeReinstallCommit = MainWindow.PnpmRegistryManifestLooksCommitted(
                "dsh-fixture", commitDir, out string tildeReinstallDetail);
            WriteProfile(">=1.0.0 <2.0.0", "1.2.3", ">=1.0.0 <2.0.0");
            bool comparatorReinstallCommit = MainWindow.PnpmRegistryManifestLooksCommitted(
                "dsh-fixture", commitDir, out string comparatorReinstallDetail);
            check("pnpm 安装树自检 · 按清单重装走范围提交判据：^ / ~ / 比较器范围内实际装到 1.2.3 均放行",
                caretReinstallCommit && tildeReinstallCommit && comparatorReinstallCommit,
                $"caret={caretReinstallCommit}/{caretReinstallDetail}; " +
                $"tilde={tildeReinstallCommit}/{tildeReinstallDetail}; " +
                $"comparator={comparatorReinstallCommit}/{comparatorReinstallDetail}");

            // P2 回归：锁文件 specifier 与清单相同只能证明“写的是同一条声明”，不能证明安装目录
            // 真的满足这条声明。比较器范围必须按 npm 语义算出 True/False，越界时不得走 Unknown 豁免。
            WriteProfile(">=2.0.0 <3.0.0", "1.2.3", ">=2.0.0 <3.0.0");
            bool comparatorOutsideReinstallCommit = MainWindow.PnpmRegistryManifestLooksCommitted(
                "dsh-fixture", commitDir, out string comparatorOutsideReinstallDetail);
            check("pnpm 安装树自检 · 比较器范围越界时即便锁文件 specifier 相同也必须失败关闭",
                !comparatorOutsideReinstallCommit,
                comparatorOutsideReinstallDetail);

            WriteProfile("^2.0.0", "1.2.3", "^2.0.0");
            bool outOfRangeReinstallCommit = MainWindow.PnpmRegistryManifestLooksCommitted(
                "dsh-fixture", commitDir, out string outOfRangeReinstallDetail);
            check("pnpm 安装树自检 · 按清单重装仍失败关闭：安装目录 1.2.3 不在 ^2.0.0 内时不得记成功",
                !outOfRangeReinstallCommit,
                outOfRangeReinstallDetail);

            WriteProfile("1.2.3", "1.2.2");
            bool staleLockInstallCommit = MainWindow.PnpmRegistryInstallLooksCommitted(
                "dsh-fixture", "1.2.3", commitDir, out string staleLockInstallDetail);
            check("pnpm 安装树自检 · 市场安装命令成功但只有锁文件仍旧时，四方判据必须拒绝记成功",
                !staleLockInstallCommit && staleLockInstallDetail.Contains("锁文件", StringComparison.Ordinal),
                staleLockInstallDetail);

            File.Delete(Path.Combine(commitDir, "package.json"));
            bool unlistedRegistryRequiresProof = MainWindow.RegistryInstallNeedsCommitProof(
                "dsh-fixture@1.2.3", "dsh-fixture", commitDir);
            bool unlistedRegistryCommitted = MainWindow.PnpmRegistryInstallLooksCommitted(
                "dsh-fixture", "1.2.3", commitDir, out string unlistedRegistryDetail);
            check("pnpm 安装树自检 · 命令退出码 0 但清单没写入时仍按来源要求四方提交，不能因执行后 DepSpec 为空而误记成功",
                unlistedRegistryRequiresProof && !unlistedRegistryCommitted &&
                unlistedRegistryDetail.Contains("不是 npm registry 来源", StringComparison.Ordinal),
                $"required={unlistedRegistryRequiresProof} · committed={unlistedRegistryCommitted} · {unlistedRegistryDetail}");

            check("pnpm 安装树自检 · 四方提交门槛只拦桌面版 npm 来源，git/本地来源与无 profile 不误拦",
                !MainWindow.RegistryInstallNeedsCommitProof("github:owner/repo#main", "dsh-fixture", commitDir) &&
                !MainWindow.RegistryInstallNeedsCommitProof("dsh-fixture@1.2.3", "dsh-fixture", null),
                "source=github/无 profile 均不要求 registry 四方提交");

            WriteProfile("1.2.2", "1.2.2");
            var staleManifest = MainWindow.EvaluateUpdate("dsh-fixture", "1.2.3", true, "", commitDir, requirePnpmCommit: true);
            check("pnpm 安装树自检 · node_modules 已是新版本、但清单与锁文件仍是旧版 ⇒ 不再假成功（下次安装不会回退）",
                !staleManifest.Succeeded && staleManifest.Measured && staleManifest.NotSatisfied,
                staleManifest.Note);
            var staleCommitProof = MainWindow.PnpmCommandLooksCommitted("dsh-fixture", "1.2.3", commitDir, null);
            var pCommitStale = new PluginManager.Plugin { Name = "dsh-fixture" };
            var uCommitStale = new PluginManager.PluginUpdate
                { Name = "dsh-fixture", Installed = "1.2.2", Latest = "1.2.3", HasUpdate = true };
            var batchCommitStale = MainWindow.BatchUpdateVerdictForTest(
                pCommitStale, uCommitStale, true, "", commitDir, commitBefore: null, requirePnpmCommit: true);
            check("pnpm 安装树自检 · 半提交时补登门槛拒绝、批量入口也不许因退出码 0 记成功",
                !staleCommitProof && !batchCommitStale.Succeeded && batchCommitStale.Measured &&
                batchCommitStale.NotSatisfied,
                $"looksCommitted={staleCommitProof} · batch={batchCommitStale.Succeeded} · {batchCommitStale.Note}");

            WriteProfile("1.2.3", "1.2.3");
            File.Delete(Path.Combine(modules, ".modules.yaml"));
            var noMetadataCommit = MainWindow.EvaluateUpdate("dsh-fixture", "1.2.3", true, "", commitDir, requirePnpmCommit: true);
            check("pnpm 安装树自检 · 三方版本都对但 pnpm 元数据缺失 ⇒ 仍不判成功（防止更新完下次整树重建）",
                !noMetadataCommit.Succeeded && noMetadataCommit.NotSatisfied,
                noMetadataCommit.Note);

            bool noMetadataInstallCommit = MainWindow.PnpmRegistryInstallLooksCommitted(
                "dsh-fixture", "1.2.3", commitDir, out string noMetadataInstallDetail);
            check("pnpm 安装树自检 · 三方版本都对但 pnpm 元数据缺失 ⇒ 安装/重装入口同样不许记成功",
                !noMetadataInstallCommit && noMetadataInstallDetail.Contains("pnpm 依赖树不完整", StringComparison.Ordinal),
                noMetadataInstallDetail);
        }
        finally
        {
            try { Directory.Delete(commitDir, recursive: true); } catch { }
        }

        string repairDir = Path.Combine(Path.GetTempPath(), "dshguard-pnpmrepair-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repairDir);
        try
        {
            string modules = Path.Combine(repairDir, "node_modules");
            string halfDir = Path.Combine(modules, "@scope", "half");
            string okDir = Path.Combine(modules, "dsh-ok");
            Directory.CreateDirectory(halfDir);
            Directory.CreateDirectory(okDir);
            File.WriteAllText(Path.Combine(okDir, "package.json"),
                "{\"name\":\"dsh-ok\",\"version\":\"1.0.0\"}");
            File.WriteAllText(Path.Combine(repairDir, "package.json"),
                "{\"dependencies\":{\"dsh-ok\":\"1.0.0\",\"@scope/half\":\"2.0.0\"}}");
            File.WriteAllText(Path.Combine(repairDir, "pnpm-workspace.yaml"),
                "packages:\n  - .\nnodeLinker: hoisted\n");

            var calls = new System.Collections.Generic.List<string>();
            bool cleanedBeforeInstall = false;
            var repaired = MainWindow.RepairPnpmInstallTreeForTest(repairDir, args =>
            {
                calls.Add(args);
                cleanedBeforeInstall = !Directory.Exists(halfDir);
                File.WriteAllText(Path.Combine(modules, ".modules.yaml"), "nodeLinker: hoisted\n");
                Directory.CreateDirectory(Path.Combine(modules, "@scope", "half"));
                File.WriteAllText(Path.Combine(modules, "@scope", "half", "package.json"),
                    "{\"name\":\"@scope/half\",\"version\":\"2.0.0\"}");
                File.WriteAllText(Path.Combine(repairDir, "pnpm-lock.yaml"),
                    "lockfileVersion: '9.0'\nimporters:\n  .:\n    dependencies:\n"
                    + "      dsh-ok:\n        version: 1.0.0\n"
                    + "      '@scope/half':\n        version: 2.0.0\n");
                return Task.FromResult((true, "fake install ok"));
            }).GetAwaiter().GetResult();

            check("pnpm 安装树自检 · 恢复编排：无锁直接普通 install，先清半截包，装后复核完整才放行",
                repaired.Success && calls.Count == 1 &&
                !calls[0].Contains("--frozen-lockfile", StringComparison.Ordinal) &&
                cleanedBeforeInstall && repaired.Output.Contains("依赖树已恢复完整", StringComparison.Ordinal),
                $"calls={calls.Count} · cleaned={cleanedBeforeInstall} · {repaired.Output}");
        }
        finally
        {
            try { Directory.Delete(repairDir, recursive: true); } catch { }
        }

        string missingProfileDir = Path.Combine(Path.GetTempPath(),
            "dshguard-pnpmrepair-missing-" + Guid.NewGuid().ToString("N"));
        bool missingProfileInstallRan = false;
        var missingProfileRepair = MainWindow.RepairPnpmInstallTreeForTest(missingProfileDir, _ =>
        {
            missingProfileInstallRan = true;
            return Task.FromResult((true, "不应执行"));
        }).GetAwaiter().GetResult();
        check("pnpm 安装树自检 · profile 清单缺失时修复入口失败关闭，不把“读不到”当成健康树，也不执行安装",
            !missingProfileRepair.Success && !missingProfileInstallRan &&
            missingProfileRepair.Output.Contains("package.json", StringComparison.Ordinal),
            $"ran={missingProfileInstallRan} · {missingProfileRepair.Output}");

        string malformedProfileDir = Path.Combine(Path.GetTempPath(),
            "dshguard-pnpmrepair-malformed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(malformedProfileDir);
        try
        {
            File.WriteAllText(Path.Combine(malformedProfileDir, "package.json"), "{");
            bool malformedInstallRan = false;
            var malformedRepair = MainWindow.RepairPnpmInstallTreeForTest(malformedProfileDir, _ =>
            {
                malformedInstallRan = true;
                return Task.FromResult((true, "不应执行"));
            }).GetAwaiter().GetResult();
            check("pnpm 安装树自检 · package.json 损坏导致直接依赖读空时修复入口失败关闭，不执行安装",
                !malformedRepair.Success && !malformedInstallRan &&
                malformedRepair.Output.Contains("没有可核对", StringComparison.Ordinal),
                $"ran={malformedInstallRan} · {malformedRepair.Output}");
        }
        finally
        {
            try { Directory.Delete(malformedProfileDir, recursive: true); } catch { }
        }

        string lostProfileDir = Path.Combine(Path.GetTempPath(),
            "dshguard-pnpmrepair-lost-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(lostProfileDir);
        try
        {
            string lostManifest = Path.Combine(lostProfileDir, "package.json");
            File.WriteAllText(lostManifest, "{\"dependencies\":{\"dsh-ok\":\"1.0.0\"}}");
            var lostRepair = MainWindow.RepairPnpmInstallTreeForTest(lostProfileDir, _ =>
            {
                File.Delete(lostManifest);
                return Task.FromResult((true, "命令返回成功但清单消失"));
            }).GetAwaiter().GetResult();
            check("pnpm 安装树自检 · 修复命令退出 0 但 package.json 随后消失时，装后复核仍失败关闭",
                !lostRepair.Success && lostRepair.Output.Contains("恢复后仍不完整", StringComparison.Ordinal),
                lostRepair.Output);
        }
        finally
        {
            try { Directory.Delete(lostProfileDir, recursive: true); } catch { }
        }

        string workspaceRepairDir = Path.Combine(Path.GetTempPath(), "dshguard-pnpmworkspacerepair-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspaceRepairDir);
        try
        {
            string modules = Path.Combine(workspaceRepairDir, "node_modules");
            string okDir = Path.Combine(modules, "dsh-ok");
            Directory.CreateDirectory(okDir);
            File.WriteAllText(Path.Combine(okDir, "package.json"),
                "{\"name\":\"dsh-ok\",\"version\":\"1.0.0\"}");
            File.WriteAllText(Path.Combine(workspaceRepairDir, "package.json"),
                "{\"dependencies\":{\"dsh-ok\":\"1.0.0\"}}");
            File.WriteAllText(Path.Combine(workspaceRepairDir, "pnpm-lock.yaml"),
                "lockfileVersion: '9.0'\nimporters:\n  .:\n    dependencies:\n"
                + "      dsh-ok:\n        version: 1.0.0\n");
            string workspacePath = Path.Combine(workspaceRepairDir, "pnpm-workspace.yaml");
            File.WriteAllText(workspacePath,
                "packages:\n  - .\nnodeLinker: hoisted\nallowBuilds:\n  keep-true: true\n");

            int attempts = 0;
            bool repairedWorkspaceOnRetry = false;
            var repaired = MainWindow.RepairPnpmInstallTreeForTest(workspaceRepairDir, args =>
            {
                attempts++;
                if (attempts == 1)
                {
                    return Task.FromResult((false,
                        "ERR_PNPM_IGNORED_BUILDS\n  Ignored build scripts: ssh2@1.17.0"));
                }
                repairedWorkspaceOnRetry = File.ReadAllText(workspacePath)
                    .Contains("'ssh2': false", StringComparison.Ordinal);
                File.WriteAllText(Path.Combine(modules, ".modules.yaml"), "nodeLinker: hoisted\n");
                return Task.FromResult((true, "fake install retry ok"));
            }).GetAwaiter().GetResult();

            check("pnpm 安装树自检 · 预修复撞到 IGNORED_BUILDS 时也先修 workspace 再重试，不提前卡死",
                repaired.Success && attempts == 2 && repairedWorkspaceOnRetry &&
                repaired.Output.Contains("修复配置后重试", StringComparison.Ordinal),
                $"attempts={attempts} · workspaceFixed={repairedWorkspaceOnRetry} · {repaired.Output}");
        }
        finally
        {
            try { Directory.Delete(workspaceRepairDir, recursive: true); } catch { }
        }
    }
    /// <summary>自检专用：创建一个 junction；返回 false 时把 cmd/系统错误交给调用方显示，不把 SKIP 当 PASS。</summary>
    private static (bool Ok, string Error) TryCreateJunctionForTest(string link, string target)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(
                "cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return (false, "cmd.exe 未启动");
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return (false, "mklink 未在 5 秒内返回");
            }
            string stdout = p.StandardOutput.ReadToEnd().Trim();
            string stderr = p.StandardError.ReadToEnd().Trim();
            bool isLink;
            try { isLink = new DirectoryInfo(link).LinkTarget != null; }
            catch { isLink = false; }
            return isLink
                ? (true, "")
                : (false, $"exit={p.ExitCode} stdout=«{stdout}» stderr=«{stderr}»");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }
}
