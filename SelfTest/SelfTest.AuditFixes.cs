using System;
using System.IO;

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
            try { MainWindow.VerifyDesktopInstaller(tmp, "QUJD"); } catch (IOException) { mismatchThrows = true; }
            bool deleted = !File.Exists(tmp);
            File.WriteAllBytes(tmp, System.Text.Encoding.ASCII.GetBytes("ABC"));
            string okNote = "";
            try { okNote = MainWindow.VerifyDesktopInstaller(tmp, abc); } catch (Exception ex) { okNote = "抛了：" + ex.Message; }
            check("审计 H1 · 桌面版安装包：SHA-512 不符 ⇒ 抛错并删除；一致 ⇒ 放行",
                mismatchThrows && deleted && okNote.StartsWith("SHA-512 与清单一致"),
                $"不符抛错={mismatchThrows} 已删={deleted} 一致结论=«{okNote}»");
        }
        finally { try { File.Delete(tmp); } catch { } }
    }
}
