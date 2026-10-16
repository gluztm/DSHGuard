using System;
using System.IO;
using System.Linq;

namespace DSHGuard;

/// <summary>
/// 2026-10-16 审计修复的断言（由 RunCore 在双轨断言之后调用）。全部是纯函数 / 临时目录，
/// 不联网、不动用户的 profile 与设置。
/// </summary>
public static partial class SelfTest
{
    internal static void RunAuditFixChecks(Action<string, bool, string> check)
    {
        // ── H1 摘要关 ──
        string good = new string('a', 64), other = new string('b', 64);
        check("审计 H1 · digest 解析：只认 sha256:<64 位十六进制>，其余一律当作没给",
            PluginSource.ParseSha256Digest("sha256:" + good.ToUpperInvariant()) == good &&
            PluginSource.ParseSha256Digest("sha1:" + good) == "" &&
            PluginSource.ParseSha256Digest("sha256:xyz") == "" &&
            PluginSource.ParseSha256Digest(null) == "",
            "");
        check("审计 H1 · 摘要判定：有摘要必须相等；无摘要时直连放行、经镜像拒绝；本地哈希算不出一律拒绝",
            PluginSource.JudgeGuardSetupDigest(good, good, viaMirror: true).Ok &&
            !PluginSource.JudgeGuardSetupDigest(good, other, viaMirror: false).Ok &&
            PluginSource.JudgeGuardSetupDigest("", good, viaMirror: false).Ok &&
            !PluginSource.JudgeGuardSetupDigest("", good, viaMirror: true).Ok &&
            !PluginSource.JudgeGuardSetupDigest(good, "", viaMirror: false).Ok,
            "");
        check("审计 H1 · 发行版报文的 digest 进入 GuardReleaseAsset.Sha256",
            PluginSource.ParseGuardSetupAsset(
                "{\"assets\":[{\"name\":\"DSHGuard-Setup-9.9.9.exe\",\"size\":10," +
                "\"browser_download_url\":\"https://github.com/gluztm/DSHGuard/releases/download/v9.9.9/DSHGuard-Setup-9.9.9.exe\"," +
                "\"digest\":\"sha256:" + good + "\"}]}")?.Sha256 == good,
            "");

        // 桌面版清单：官方源 2026-10 实测写法（折叠块 + 绝对地址）
        const string feed =
            "version: 0.2.0-rc.2\nfiles:\n  - url: >-\n      https://download.deepseek.com/dsh-desk/bin/win-x64/deepseek-harness-0.2.0-rc.2-win-x64.exe\n" +
            "    sha512: >-\n      QUJD\n    size: 3\npath: >-\n  https://download.deepseek.com/dsh-desk/bin/win-x64/deepseek-harness-0.2.0-rc.2-win-x64.exe\n" +
            "sha512: >-\n  QUJD\nreleaseDate: '2026-09-29T10:35:27.666Z'\n";
        var f = MainWindow.ParseDesktopFeed(feed);
        check("审计 H1 · 桌面版清单：折叠块写法能取到版本 / 下载地址 / 文件名 / sha512（旧解析把 path 读成 \">-\"）",
            f != null && f.Value.Version == "0.2.0-rc.2" &&
            f.Value.Url == "https://download.deepseek.com/dsh-desk/bin/win-x64/deepseek-harness-0.2.0-rc.2-win-x64.exe" &&
            f.Value.Path == "deepseek-harness-0.2.0-rc.2-win-x64.exe" && f.Value.Sha512 == "QUJD",
            f?.ToString() ?? "null");
        check("审计 H1 · 桌面版清单：外站 / http / 路径穿越一律整份作废",
            MainWindow.ParseDesktopFeed("version: 1\npath: https://evil.example/x.exe\n") == null &&
            MainWindow.ParseDesktopFeed("version: 1\npath: http://download.deepseek.com/x.exe\n") == null &&
            MainWindow.ParseDesktopFeed("version: 1\npath: ../x.exe\n") == null,
            "");

        // ── H5 供应链策略只在被拦下时才放开 ──
        check("审计 H5 · 只有明确的策略报错才触发放开重试；网络 / 404 / 构建失败不触发",
            PluginManager.LooksLikeSupplyChainBlock("ERR_PNPM_MINIMUM_RELEASE_AGE_VIOLATION  4 lockfile entries failed verification") &&
            PluginManager.LooksLikeSupplyChainBlock("Lockfile failed supply-chain policy check (3 entries)") &&
            PluginManager.LooksLikeSupplyChainBlock("was published at …, within the minimumReleaseAge cutoff") &&
            !PluginManager.LooksLikeSupplyChainBlock("ERR_PNPM_FETCH_404  GET https://registry.npmmirror.com/x: Not Found") &&
            !PluginManager.LooksLikeSupplyChainBlock("ELIFECYCLE  Command failed with exit code 1.") &&
            !PluginManager.LooksLikeSupplyChainBlock(null),
            "");

        // ── M 原子写：整份替换 + 上一版 .bak；主文件损坏时能从 .bak 读回 ──
        {
            string dir = Path.Combine(Path.GetTempPath(), "dshguard-selftest-atomic-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(dir);
                string af = Path.Combine(dir, "state.json");
                AtomicFile.WriteAllText(af, "{\"v\":1}");
                AtomicFile.WriteAllText(af, "{\"v\":2}");
                bool mainOk = File.ReadAllText(af) == "{\"v\":2}";
                bool bakOk = File.Exists(af + ".bak") && File.ReadAllText(af + ".bak") == "{\"v\":1}";
                bool noTmp = Directory.GetFiles(dir, "*.tmp").Length == 0;
                File.WriteAllText(af, "{\"v\":");   // 模拟截断
                string got = AtomicFile.ReadAllTextWithBackup(af, t => t.TrimEnd().EndsWith("}"), out bool usedBak);
                check("审计 M · AtomicFile：整份替换、保留上一版 .bak、不留临时文件；主文件截断时从 .bak 读回",
                    mainOk && bakOk && noTmp && usedBak && got == "{\"v\":1}",
                    $"主={mainOk} 备份={bakOk} 无临时={noTmp} 用备份={usedBak} 读回=«{got}»");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        // ── M 版本比较：预发布按 semver 逐段比；空格分隔的比较器组按"全部成立" ──
        {
            var ord = new[] { "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta",
                              "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0" };
            bool sorted = true;
            for (int i = 0; i < ord.Length - 1; i++) if (VersionInfo.Compare(ord[i], ord[i + 1]) != -1) sorted = false;
            check("审计 M · 预发布比较按 semver §11：规范示例序列严格递增，rc.10 > rc.9",
                sorted && VersionInfo.Compare("0.1.5-rc.10", "0.1.5-rc.9") == 1, "");
            check("审计 M · 比较器组：\">=0.1.0 <0.3.0\" 认得、\">=0.1.0 <0.2.0\" 不放过 0.2.5；^ 仍为宽松下限",
                VersionInfo.Satisfies(">=0.1.0 <0.3.0", "0.2.0-rc.2") &&
                !VersionInfo.Satisfies(">=0.1.0 <0.2.0", "0.2.5") &&
                VersionInfo.Satisfies(">= 0.1.5", "0.2.0") &&
                VersionInfo.Satisfies("^0.1.0", "0.2.0"), "");
        }

        // ── M 快照：清单条目名 / 快照目录识别 / 疑似凭据文件 ──
        check("审计 M · 快照清单条目名：只认单一文件名（绝对路径 / 分隔符 / .. 一律拒绝）",
            SnapshotManager.IsSafeEntryName("profile-package.json") &&
            SnapshotManager.IsSafeEntryName("profile-plugins-a..b.js") &&
            !SnapshotManager.IsSafeEntryName(@"C:\Windows\win.ini") &&
            !SnapshotManager.IsSafeEntryName(@"..\..\x") &&
            !SnapshotManager.IsSafeEntryName("a/b") &&
            !SnapshotManager.IsSafeEntryName(".."), "");
        check("审计 M · 疑似凭据文件不进快照：.env / .npmrc / *.pem / *token* 等",
            SnapshotManager.LooksLikeSecretFile(@"x\plugins\p\.env") &&
            SnapshotManager.LooksLikeSecretFile(@"x\.env.local") &&
            SnapshotManager.LooksLikeSecretFile(@"x\.npmrc") &&
            SnapshotManager.LooksLikeSecretFile(@"x\server.key") &&
            SnapshotManager.LooksLikeSecretFile(@"x\secrets.json") &&
            SnapshotManager.LooksLikeSecretFile(@"x\credentials.yml") &&
            SnapshotManager.LooksLikeSecretFile(@"x\gh.token") &&
            !SnapshotManager.LooksLikeSecretFile(@"x\tokenizer.js") &&
            !SnapshotManager.LooksLikeSecretFile(@"x\password-field.vue") &&
            !SnapshotManager.LooksLikeSecretFile(@"x\index.js") &&
            !SnapshotManager.LooksLikeSecretFile(@"x\package.json"), "");
        {
            string root = Path.Combine(Path.GetTempPath(), "dshguard-selftest-snapdir-" + Guid.NewGuid().ToString("N"));
            try
            {
                string own = Directory.CreateDirectory(Path.Combine(root, "20261016-120000-manual")).FullName;
                string byManifest = Directory.CreateDirectory(Path.Combine(root, "renamed-by-user")).FullName;
                File.WriteAllText(Path.Combine(byManifest, "manifest.json"), "{\"app\":\"DSHGuard\"}");
                string foreign = Directory.CreateDirectory(Path.Combine(root, "Photos")).FullName;
                File.WriteAllText(Path.Combine(foreign, "a.jpg"), "x");
                check("审计 M · 快照目录识别：时间戳命名或清单声明 app=DSHGuard 才算；别的文件夹不列出、不删除",
                    SnapshotManager.LooksLikeSnapshotDir(own) && SnapshotManager.LooksLikeSnapshotDir(byManifest) &&
                    !SnapshotManager.LooksLikeSnapshotDir(foreign) &&
                    !SnapshotManager.Delete(new SnapshotManager.Snapshot { Dir = foreign }) &&
                    File.Exists(Path.Combine(foreign, "a.jpg")), "");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        // ── M 启用插件：条目里还有别的键时只删 disabled 那一行 ──
        {
            var plain = new System.Collections.Generic.List<string>
                { "- id: a", "  config: {}", "# DSHGuard 禁用", "- id: x", "  disabled: true", "- id: b" };
            var rich = new System.Collections.Generic.List<string>
                { "- id: a", "# DSHGuard 禁用", "- id: x", "  disabled: true", "  config:", "    k: v", "- id: b" };
            var dPlain = PluginManager.EnableDropLines(plain, 3, 4, 2);
            var dRich = PluginManager.EnableDropLines(rich, 2, 3, 1);
            check("审计 M · 启用插件：纯禁用块整块删；条目下还有 config 时只删标记注释与 disabled 行，条目保留",
                dPlain.OrderBy(i => i).SequenceEqual(new[] { 2, 3, 4 }) &&
                dRich.OrderBy(i => i).SequenceEqual(new[] { 1, 3 }),
                $"纯禁用删 [{string.Join(",", dPlain)}]；带配置删 [{string.Join(",", dRich)}]");
        }

        // ── M 标签分页：裸数组满页才翻下一页，地址只由首页模板拼 ──
        {
            string full = "[" + string.Join(",", Enumerable.Range(0, 100).Select(i => $"{{\"name\":\"v{i}\"}}")) + "]";
            string first = "https://api.github.com/repos/o/r/tags?per_page=100";
            bool fullOk = PluginSource.TryNextPageByCount(full, first, 1, out string next2);
            bool partialStop = !PluginSource.TryNextPageByCount("[{\"name\":\"v1\"}]", first, 1, out _);
            bool objStop = !PluginSource.TryNextPageByCount("{\"values\":[]}", first, 1, out _);
            check("审计 M · 标签分页：github 这类裸数组满 100 条 ⇒ page=N+1；不足一页或不是数组 ⇒ 到底",
                fullOk && next2 == first + "&page=2" && partialStop && objStop, next2);
        }

        // ── M/L 命令行安全：cmd 兜底拒绝特殊字符；引擎版本串白名单 ──
        check("审计 M · cmd 兜底：含 & | < > ^ % 的命令拒绝交给 cmd；普通插件命令与带引号的路径照常",
            ProcessManager.HasCmdMetachars("npx plugin add x@>=1.0.0") &&
            ProcessManager.HasCmdMetachars("npx a & calc") &&
            !ProcessManager.HasCmdMetachars("npx --yes @deepseek-ai/dsh@0.2.0-rc.2 plugin --profile web add dsh-mnemon@0.5.9 --registry https://registry.npmmirror.com") &&
            !ProcessManager.HasCmdMetachars("powershell -File \"C:\\Program Files\\x.ps1\""), "");
        check("审计 L · 引擎版本串白名单：版本号 / dist-tag 放行；空格与 shell 字符拒绝",
            VersionMemory.IsSafeSpec("0.2.0-rc.2") && VersionMemory.IsSafeSpec("latest") &&
            !VersionMemory.IsSafeSpec("1.0.0 -c calc") && !VersionMemory.IsSafeSpec("1.0&x") &&
            !VersionMemory.IsSafeSpec(""), "");

        // ── L 自动备份限量：本程序命名的按类留 10 份，手工命名的不碰 ──
        {
            string dir = Path.Combine(Path.GetTempPath(), "dshguard-selftest-bak-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(dir);
                string pj = Path.Combine(dir, "package.json");
                var t0 = new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc);
                for (int i = 0; i < 13; i++)
                {
                    string bf = Path.Combine(dir, $"package.json.bak-bundle-202610011000{i:00}");
                    File.WriteAllText(bf, "x"); File.SetLastWriteTimeUtc(bf, t0.AddMinutes(i));
                }
                File.WriteAllText(Path.Combine(dir, "package.json.bak-mcp-20261010101010"), "x");
                File.WriteAllText(Path.Combine(dir, "package.json.bak-handmade"), "x");
                int removed = PluginManager.PruneOwnBackups(pj);
                int bundleLeft = Directory.GetFiles(dir, "package.json.bak-bundle-*").Length;
                bool newestKept = File.Exists(Path.Combine(dir, "package.json.bak-bundle-20261001100012"));
                bool othersKept = File.Exists(Path.Combine(dir, "package.json.bak-mcp-20261010101010"))
                               && File.Exists(Path.Combine(dir, "package.json.bak-handmade"));
                check("审计 L · 自动备份限量：同类超过 10 份删最旧的；别的标记与手工命名的备份原样保留",
                    removed == 3 && bundleLeft == 10 && newestKept && othersKept,
                    $"删除={removed} 剩余 bundle={bundleLeft} 最新保留={newestKept} 其它保留={othersKept}");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        // ── M 终止引擎只动 node 监听者 ──
        check("审计 M · 端口监听者身份：node / node.exe 认作引擎；其它程序与读不到名字的一律不杀",
            ProcessManager.LooksLikeEngineOwner(new ProcessManager.PortOwner(1234, "node")) &&
            ProcessManager.LooksLikeEngineOwner(new ProcessManager.PortOwner(1234, "NODE.EXE")) &&
            !ProcessManager.LooksLikeEngineOwner(new ProcessManager.PortOwner(1234, "python")) &&
            !ProcessManager.LooksLikeEngineOwner(new ProcessManager.PortOwner(4, "System")) &&
            !ProcessManager.LooksLikeEngineOwner(new ProcessManager.PortOwner(1234, "?")),
            "");

        // ── H6 进程树：认得子进程、不把自己算进别人的树、停止后能清掉残留 ──
        if (OperatingSystem.IsWindows())
        {
            System.Diagnostics.Process? child = null;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(
                    Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\PING.EXE"))
                { UseShellExecute = false, CreateNoWindow = true };
                psi.ArgumentList.Add("-n"); psi.ArgumentList.Add("30"); psi.ArgumentList.Add("127.0.0.1");
                child = System.Diagnostics.Process.Start(psi);
                int cpid = child?.Id ?? 0;
                var selfTree = ProcessManager.TreeOf(Environment.ProcessId);
                var childTree = ProcessManager.TreeOf(cpid);
                var snap = ProcessManager.SnapshotTree(cpid);
                int cleaned = ProcessManager.KillLeftovers(snap);
                bool gone = child == null || child.WaitForExit(5000);
                check("审计 H6 · 进程树：子进程算在本进程树里；子进程的树不含本进程；按快照清残留能杀掉它",
                    cpid > 0 && selfTree.Contains(cpid) && !childTree.Contains(Environment.ProcessId) &&
                    cleaned == 1 && gone,
                    $"子进程={cpid} 本树含子={selfTree.Contains(cpid)} 子树含本={childTree.Contains(Environment.ProcessId)} 清理={cleaned} 已退出={gone}");
            }
            finally
            {
                try { if (child != null && !child.HasExited) child.Kill(); } catch { }
                child?.Dispose();
            }
        }

        string tmp = Path.Combine(Path.GetTempPath(), "dshguard-selftest-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            File.WriteAllBytes(tmp, System.Text.Encoding.ASCII.GetBytes("ABC"));
            // "ABC" 的 SHA-512 Base64
            string abc = Convert.ToBase64String(System.Security.Cryptography.SHA512.HashData(System.Text.Encoding.ASCII.GetBytes("ABC")));
            bool mismatchThrows = false;
            Func<string, FileTrust.SignatureState> unsigned = _ => FileTrust.SignatureState.NotSigned;
            Func<string, FileTrust.SignatureState> tampered = _ => FileTrust.SignatureState.Invalid;
            try { MainWindow.VerifyDesktopInstaller(tmp, "QUJD", unsigned); } catch (IOException) { mismatchThrows = true; }
            bool deleted = !File.Exists(tmp);
            File.WriteAllBytes(tmp, System.Text.Encoding.ASCII.GetBytes("ABC"));
            string okNote = "";
            try { okNote = MainWindow.VerifyDesktopInstaller(tmp, abc, unsigned); } catch (Exception ex) { okNote = "抛了：" + ex.Message; }
            // 摘要一致但签名存在却无效 ⇒ 仍然拒绝并删除；清单没给摘要又无有效签名 ⇒ 拒绝
            File.WriteAllBytes(tmp, System.Text.Encoding.ASCII.GetBytes("ABC"));
            bool badSigThrows = false;
            try { MainWindow.VerifyDesktopInstaller(tmp, abc, tampered); } catch (IOException) { badSigThrows = true; }
            bool badSigDeleted = !File.Exists(tmp);
            File.WriteAllBytes(tmp, System.Text.Encoding.ASCII.GetBytes("ABC"));
            bool noProofThrows = false;
            try { MainWindow.VerifyDesktopInstaller(tmp, "", unsigned); } catch (IOException) { noProofThrows = true; }
            check("审计 H1 · 桌面版安装包：SHA-512 不符 / 签名无效 / 既无摘要又无有效签名 ⇒ 抛错并删除；摘要一致且无签名 ⇒ 放行",
                mismatchThrows && deleted && okNote.StartsWith("SHA-512 与清单一致") &&
                badSigThrows && badSigDeleted && noProofThrows,
                $"不符抛错={mismatchThrows} 已删={deleted} 一致结论=«{okNote}» 签名无效抛错={badSigThrows} 无凭据抛错={noProofThrows}");
        }
        finally { try { File.Delete(tmp); } catch { } }
    }
}
