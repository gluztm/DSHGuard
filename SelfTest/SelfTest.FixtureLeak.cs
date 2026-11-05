using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DSHGuard;

/// <summary>
/// 2026-11-05 P2：自检夹具 %TEMP 泄漏的回归断言（junction 先摘后删 + launchcount .bak +
/// 报告编码无 BOM 三路共用一处定义）。全纯函数 / 一次性临时物，不联网、不碰 profile。
///   · 为什么必须造真联接：裸 recursive:true 的失败形态只有带着 reparse point 才暴露，
///     普通子树两种写法都能删干净 —— 没有联接的对照等于没断言。
///   · BOM 那两路（tempReport / --selftest-out）在文件体内不可拆测（编码内联在收尾写盘点），
///     这里钉的是"三路共用 ReportFileEncoding 这一处定义 + 该定义确实无 BOM"，
///     再用变异档补"把 :11269 改回 true ⇒ 有断言红"的那一半。
/// 挂点与接线由门禁 UninstallEvidenceText / 本方法名双钉（§70 逐点路子同型）。
/// </summary>
public static partial class SelfTest
{
    internal static void RunFixtureLeakChecks(Action<string, bool, string> check, Action<string, string>? skip = null)
    {
        // ── 1) 正向：带联接的夹具树，用 helper 删 ⇒ 根与联接一起消失 ──
        //    对照：同样一棵树，用旧的裸 recursive:true 删 ⇒ 停在骨架（这就是 271/190 空壳的来由）。
        string mkErrA = "", mkErrB = "";
        string rootA = Path.Combine(Path.GetTempPath(), "dshguard-p2lnk-ok-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        string rootB = Path.Combine(Path.GetTempPath(), "dshguard-p2lnk-bare-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        string linkA = "", linkB = "";
        try
        {
            linkA = BuildJunctionFixture(rootA, out mkErrA);
            linkB = BuildJunctionFixture(rootB, out mkErrB);
            if (linkA.Length == 0 || linkB.Length == 0)
            {
                check("P2 夹具清理 · junction 先摘后删（helper vs 裸 recursive 对照）", false,
                    $"联接造不出（mklink 被拒？）A={mkErrA} B={mkErrB} —— 本条未验证须如实红/跳，不得静默");
            }
            else
            {
                CleanupJunctionFixture(linkA, rootA);
                bool helperClean = !Directory.Exists(rootA) && !File.Exists(linkA);
                // 旧写法原样复刻（不落进生产代码，只在本断言里演示病形）
                try { Directory.Delete(rootB, recursive: true); } catch { }
                bool bareLeft = Directory.Exists(rootB);
                // 两个都必须真：helperClean=修复生效；bareLeft=病形在这棵树里**确实复现** —— 若哪天
                //   运行时的 recursive:true 会自己处理联接（bareLeft=False），这档对照就空转了，
                //   必须红着提醒重写场景，而不是悄悄退化成「helper 删了个本来就好删的东西」。
                check("P2 夹具清理 · junction 先摘后删（helper vs 裸 recursive 对照）",
                    helperClean && bareLeft,
                    $"helper 删净={helperClean}（须 True）· 裸 recursive 留骨架={bareLeft}（须 True，即 271/190 空壳病形复现；False=运行时行为变了、本对照空转，重写场景）");
            }
        }
        finally
        {
            CleanupJunctionFixture(linkA, rootA);
            CleanupJunctionFixture(linkB, rootB);
        }

        // ── 2) helper 对「空 link」的容错：联接压根没建成时不得抛，根照删 ──
        string rootNoLink = Path.Combine(Path.GetTempPath(), "dshguard-p2lnk-nolink-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        try
        {
            Directory.CreateDirectory(Path.Combine(rootNoLink, "sub"));
            CleanupJunctionFixture("", rootNoLink);
            check("P2 夹具清理 · 联接未建成（link 传空串）时 helper 不抛、根照删",
                !Directory.Exists(rootNoLink), $"根还在={Directory.Exists(rootNoLink)}");
        }
        finally { try { if (Directory.Exists(rootNoLink)) Directory.Delete(rootNoLink, true); } catch { } }

        // ── 3) 报告编码：三路共用定义必须无 BOM ──
        byte[] bom = ReportFileEncoding.GetPreamble();
        check("P2 报告编码 · ReportFileEncoding 无 BOM 前缀（三路共用的那一定义）",
            bom.Length == 0, $"前缀 {bom.Length} 字节（必须 0；≠0 时按 ^[ 锚定数断言会少报首行）");
        // 源码扫描走现成的 FindSourceFile（§70 / 审计 M2 同一条路：向上找含 csproj 的开发树；
        //   单文件部署读不到 ⇒ 有 skip 通道就真 SKIP、没有就如实红，绝不"读不到=过"）。
        string src = "";
        try { string? p = FindSourceFile("SelfTest.cs"); src = p != null ? File.ReadAllText(p) : ""; } catch { }
        const string encName = "报告编码 · SelfTest.cs 内两路文件写盘点都走 ReportFileEncoding 且无手写 BOM 编码";
        if (src.Length == 0)
        {
            if (skip != null) skip(encName, "单文件部署：读不到 SelfTest.cs 源码 ⇒ 本条未验证");
            else check(encName, false, "源码读不到且调用方没传 skip ⇒ 装配错误，如实红");
        }
        else
        {
            // 逐点判定（§70 同口径，聚合计数会放过"改坏一处"）：
            //   ① 全文不得再出现 new UTF8Encoding(true)（带 BOM 的手写编码）；
            //   ② tempReport 与 --selftest-out 两处写盘必须都显式走共用 ReportFileEncoding。
            int oldBom = System.Text.RegularExpressions.Regex.Matches(src, @"new UTF8Encoding\(true\)").Count;
            bool tempOk = src.Contains("File.WriteAllText(tempReport, text, ReportFileEncoding)", StringComparison.Ordinal);
            bool outOk = src.Contains("File.WriteAllText(Path.GetFullPath(outPath), text, ReportFileEncoding)", StringComparison.Ordinal);
            check(encName,
                oldBom == 0 && tempOk && outOk,
                $"手写 BOM 编码={oldBom}（须 0）· tempReport 走共用={tempOk} · --selftest-out 走共用={outOk}");
        }

        // ── 4) launchcount 的 .bak：用探针断言 Bump 真的留下 .bak，且清理写法覆盖它 ──
        string lcProbe = Path.Combine(Path.GetTempPath(), "dshguard-launchcount-p2-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json");
        try
        {
            LaunchCounter.Bump(lcProbe);
            LaunchCounter.Bump(lcProbe);
            bool bakExists = File.Exists(lcProbe + ".bak");
            check("P2 launchcount · Bump 原子写确实留同名 .bak（清理 finally 必须连它一起删）",
                bakExists,
                $".bak 在盘={bakExists}（False 说明 AtomicFile 的 File.Replace 语义变了 —— 那要同步改 :9715 的清理与这条判据，不许静默失守）");
        }
        finally
        {
            try { File.Delete(lcProbe); } catch { }
            try { File.Delete(lcProbe + ".bak"); } catch { }
        }

        // ── 5) §70 式接线断言：SelfTest.cs 里 RunFixtureLeakChecks( 的调用点恰好 1 处 ──
        //   语义是「自检内部接线」，与门禁的「生产接线」两本账：门禁刻意排除 SelfTest.cs / SelfTest\
        //   （被排除区内符号对该行必 0/0，是结构性的），所以这行不进生产门禁（小蓝 11-05 裁决），由本断言钉。
        //   阈值 ==1 而非 >=1，§70 同款理由：聚合阈值放过「将来有人新加一处调用点」。先剥整行注释再数
        //   （复用 SelfTest.AuditFixes.cs 的 StripWholeCommentLines，一个剥法一处真相，不另起 stripper）：
        //   「调用点被注释掉」= 静默转绿那半边必红；行尾 // 若把计数顶到 2 ⇒ ==1 直接红，响而不漏。
        //   只数 SelfTest.cs 一份内容：声明与本扫描行都在这个旁挂文件里，不进计数面，无自指恒真。
        {
            const string wireName = "自检接线 · SelfTest.cs 内 RunFixtureLeakChecks( 调用点恰好 1 处";
            string wireSrc = "";
            try { string? wp = FindSourceFile("SelfTest.cs"); wireSrc = wp != null ? File.ReadAllText(wp) : ""; } catch { }
            if (wireSrc.Length == 0)
            {
                if (skip != null) skip(wireName, "单文件部署：读不到 SelfTest.cs 源码 ⇒ 本条未验证");
                else check(wireName, false, "源码读不到且调用方没传 skip ⇒ 装配错误，如实红");
            }
            else
            {
                string[] wLines = StripWholeCommentLines(wireSrc).Split('\n');
                int wHits = 0;
                var wAt = new List<string>();
                for (int wi = 0; wi < wLines.Length; wi++)
                    if (wLines[wi].Contains("RunFixtureLeakChecks(", StringComparison.Ordinal))
                    { wHits++; wAt.Add((wi + 1).ToString()); }
                check(wireName, wHits == 1,
                    $"剥整行注释后 Contains(\"RunFixtureLeakChecks(\") 命中行数={wHits}（须 ==1；命中行=[{string.Join(",", wAt)}]，将来若红，凭行号即可分辨多算还是少算）");
            }
        }

        // ── 6) 孤儿检测器三条对照（App.ReportStaleSelfTestFixtures，钉精确计数而非「有没有」）──
        //   返回 int = 写出的告警行数。livePids / nowUtc 两个注入参数就是为这三条准备的：
        //   不碰真进程、不等 2 小时阈值。before 差值法：机器上若有别的轮次留下的真孤儿，会被
        //   基线吸收，不会把本断言搞红；三态期望 = +1 / +0 / +0。假目录用 data 族的真实前缀名
        //   （前缀常量与检测器同源，不另抄一份），finally 删净、绝不碰真目录。
        {
            string fakeDir = Path.Combine(Path.GetTempPath(),
                App.SelfTestDataRootBaseName + "-90001");
            var deadSet = new System.Collections.Generic.HashSet<int>();            // 不含 90001 ⇒ 判死
            var aliveSet = new System.Collections.Generic.HashSet<int> { 90001 };    // 含       ⇒ 判活
            int orphanBase = App.ReportStaleSelfTestFixtures(() => deadSet);         // 基线（先于造目录）
            int r1 = -1, r2 = -1, r3 = -1;
            try
            {
                Directory.CreateDirectory(fakeDir);
                Directory.SetLastWriteTimeUtc(fakeDir, DateTime.UtcNow.AddHours(-3));
                r1 = App.ReportStaleSelfTestFixtures(() => deadSet);   // 死 pid + 超阈 ⇒ 恰 +1
                r2 = App.ReportStaleSelfTestFixtures(() => aliveSet);  // 活 pid      ⇒ +0（第一道闸）
                Directory.SetLastWriteTimeUtc(fakeDir, DateTime.UtcNow);
                r3 = App.ReportStaleSelfTestFixtures(() => deadSet);   // 未超阈      ⇒ +0（第二道闸）
            }
            finally { try { Directory.Delete(fakeDir, recursive: true); } catch { } }
            check("孤儿检测器三条对照：死+旧=+1、活+旧=+0、死+新=+0（只写不删）",
                r1 - orphanBase == 1 && r2 - orphanBase == 0 && r3 - orphanBase == 0,
                $"基线={orphanBase} · 死+旧={r1}（须 +1）· 活+旧={r2}（须 +0）· 死+新={r3}（须 +0）· 假目录残留={Directory.Exists(fakeDir)}（须 False——它自己就验证了零删除之外没乱删）");
        }
    }

    /// <summary>造一棵「根 + node_modules + 一个真目录联接」的最小夹具树，返回联接路径（""=没建成）。</summary>
    private static string BuildJunctionFixture(string root, out string mkErr)
    {
        mkErr = "";
        try
        {
            string target = Path.Combine(root, "target-mod");
            string link = Path.Combine(root, "node_modules", "trap");
            Directory.CreateDirectory(target);
            Directory.CreateDirectory(Path.GetDirectoryName(link)!);
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            { CreateNoWindow = true, UseShellExecute = false };
            using var mk = System.Diagnostics.Process.Start(psi);
            if (mk == null || !mk.WaitForExit(5000)) { mkErr = "mklink 超时"; return ""; }
            return Directory.Exists(link) ? link : "";
        }
        catch (Exception ex) { mkErr = ex.Message; return ""; }
    }
}
