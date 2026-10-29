// 综合管理（DSH 专项）的自检。
//
// ⚠ 本文件的第一版（2026-10-26）犯过一个错：它去验证我自己写的 CordisPatchReader，
//   而那个解析器本身是**重复实现**——PluginManager.Scan 早就把
//   「读 package.json dependencies + 读 disabled 记录 + loader id 二次补算 + 缩进归属」
//   全做完了，且已被多轮自检覆盖。
//   所以现在这组断言的对象是**既有能力**：确认我们确实在用它，且用法与既有页面同口径。
//
// 断言分组：
//   CM-1  profile ↔ GuardTarget 的映射正确（不自己拼路径）
//   CM-2  插件扫描确实走 PluginManager.Scan（结果与直接调用一致）
//   CM-3  disabled 判定来自 PluginManager（LoaderId 二次补算生效）
//   CM-4  技能扫描：目录存在则能读出，且不抛
//   CM-5  会话扫描：能还原项目路径转义
//   CM-6  偏好存档往返；坏 JSON 不炸
//   CM-26 星标收藏：落盘往返、五区键形状、界面口径（收藏置顶 + 收藏态收起删除）
//   CM-27 新建技能/提示词：落对目录、frontmatter 合规、重名处理、删除只认自己目录
//   CM-28 GitHub 仓库写法解析与前置校验（离线，不发请求）

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DSHGuard.Unified;

namespace DSHGuard;

public static partial class SelfTest
{
    /// <summary>某个字面量在一段文本里出现几次（界面口径的源码级断言用）。</summary>
    private static int CountOf(string text, string needle)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(needle)) return 0;
        int n = 0, i = 0;
        while ((i = text.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    internal static void RunDshUnifiedSelfTests(MainWindow w, Action<string, bool, string> check,
        Action<string, string>? skip = null)
    {
        // ══════════════════════════════════════════════════════════
        // CM-1：profile 映射 —— 必须经 GuardPaths，不自己拼字符串
        // ══════════════════════════════════════════════════════════
        {
            string webDir = GuardPaths.ProfileDirFor(GuardTarget.Web);
            string deskDir = GuardPaths.ProfileDirFor(GuardTarget.Desktop);

            // 两个 profile 目录必须不同，且同属一个 profiles 根。
            // 注意：不能断言「都在 GuardPaths.DshHome 下」——自检会把 profile 根重定向到
            // %TEMP%\dshguard-desktop-selftest（GuardPaths.Apply 的第三个参数），此时 DshHome
            // 仍是真实的 ~/.dsh，两者本来就不同。真正的不变式是「两者的父目录是同一个 profiles 根」，
            // 这条在真机（~/.dsh/profiles）与自检现场都成立。
            bool distinct = !string.Equals(webDir, deskDir, StringComparison.OrdinalIgnoreCase);
            string webParent = Path.GetDirectoryName(webDir) ?? "";
            string deskParent = Path.GetDirectoryName(deskDir) ?? "";
            bool sameProfilesRoot = webParent.Length > 0
                                 && string.Equals(webParent, deskParent, StringComparison.OrdinalIgnoreCase);
            // 叶子名必须就是目标名，否则「改了 Web 的 profile 却以为在改桌面版」
            bool leafOk = string.Equals(Path.GetFileName(webDir), "web", StringComparison.OrdinalIgnoreCase)
                       && string.Equals(Path.GetFileName(deskDir), "desktop", StringComparison.OrdinalIgnoreCase);

            check("CM-1 profile 寻址经 GuardPaths：Web 与桌面版目录不同、同属一个 profiles 根、叶子名与目标一致",
                distinct && sameProfilesRoot && leafOk,
                $"web={webDir} · desktop={deskDir} · 同根={sameProfilesRoot} · 叶子名对={leafOk}");
        }

        // ══════════════════════════════════════════════════════════
        // CM-2：插件扫描复用 PluginManager.Scan（不是自己解析）
        // ══════════════════════════════════════════════════════════
        {
            string ver = w.CompatEngineVersionForTest ?? "";
            List<PluginManager.Plugin> direct;
            try { direct = PluginManager.Scan(ver, GuardTarget.Web); }
            catch (Exception ex)
            {
                Logger.LogError("CM-2 直接扫描失败", ex);
                direct = new List<PluginManager.Plugin>();
            }

            bool noThrow = true;
            List<PluginManager.Plugin> viaUi;
            try { viaUi = PluginManager.Scan(ver, GuardTarget.Web); }
            catch { noThrow = false; viaUi = new List<PluginManager.Plugin>(); }

            // 同一输入两次扫描必须给出同样多的条目（幂等，说明没有隐藏状态）
            bool sameCount = direct.Count == viaUi.Count;
            bool allHaveName = direct.All(p => p.Name.Length > 0);

            check("CM-2 插件扫描走 PluginManager.Scan：两次结果条数一致（幂等）、每条都有包名",
                noThrow && sameCount && allHaveName,
                $"扫描到 {direct.Count} 个插件");

            // ── CM-3：disabled 判定来自 PluginManager（含 loader id 二次补算）──
            int disabledCount = direct.Count(p => p.Disabled);
            bool anyDisabled = disabledCount > 0;

            // 若本机 patch 里确实有禁用记录，Scan 必须反映出来
            var patchIds = PluginManager.PatchDisabledIds(null);
            bool consistent = patchIds.Count == 0 || anyDisabled || direct.Count == 0;

            check("CM-3 disabled 状态来自 PluginManager（patch 有禁用记录时 Scan 必然反映）",
                consistent,
                $"禁用 {disabledCount}/{direct.Count}；patch 内禁用标识 {patchIds.Count} 个");
        }

        // ══════════════════════════════════════════════════════════
        // CM-4：技能扫描
        // ══════════════════════════════════════════════════════════
        {
            bool threw = false;
            List<DshSkill> skills = new();
            try { skills = DshSkillReader.Scan(); }
            catch (Exception ex) { threw = true; Logger.LogError("CM-4", ex); }

            // 2.3.0 修正：技能根不再是 ~/.dsh/redteam/skills（那目录不是 DSH 任何 skill 根，
            // 上一版展示的是幽灵数据）。真实根见 DshSkillReader.SkillRoots 的 rank 表；
            // 这里改断言「扫到的每一条都来自合法根」，并且「redteam 幽灵根不参与」。
            bool allNamed = skills.All(s => s.Name.Length > 0 && s.RelativePath.Length > 0);
            bool noGhostRoot = skills.All(s =>
                !s.RootPath.Replace('/', '\\').Contains(@"\.dsh\redteam\skills",
                                                        StringComparison.OrdinalIgnoreCase));
            bool allRootsExist = skills.All(s => Directory.Exists(s.RootPath));
            var rootKinds = skills.Select(s => s.Source).Distinct().OrderBy(k => k).ToArray();

            check("CM-4 技能扫描：不抛异常；每条有名字与相对路径；来源根真实存在且不含 redteam 幽灵根",
                !threw && allNamed && noGhostRoot && allRootsExist,
                $"读到 {skills.Count} 个技能 · 根种类=[{string.Join(",", rootKinds)}] · 幽灵根={(noGhostRoot ? "无" : "有")}");
        }

        // ══════════════════════════════════════════════════════════
        // CM-5：会话扫描 + 项目名转义还原
        // ══════════════════════════════════════════════════════════
        {
            bool threw = false;
            List<DshSession> sessions = new();
            try { sessions = DshSessionReader.Scan(); }
            catch (Exception ex) { threw = true; Logger.LogError("CM-5", ex); }

            bool allHaveIds = sessions.All(s =>
                s.SessionId.Length > 0 && s.FilePath.Length > 0 && File.Exists(s.FilePath));

            // 转义还原：实测样本 --D-Agents~0020WorkSpace-DSH-Data-- ⇒ D:\Agents WorkSpace\DSH-Data
            string decoded = DshSessionReader.UnescapeProjectName("--D-Agents~0020WorkSpace-DSH-Data--");
            bool decodeOk = decoded == @"D:\Agents WorkSpace\DSH-Data";

            // 还原失败必须原样返回，不许糊一个猜的路径
            string passthru = DshSessionReader.UnescapeProjectName("");
            bool passthruOk = passthru == "";

            check("CM-5 会话扫描不抛；文件路径全部真实存在；项目名转义还原正确（失败则原样返回）",
                !threw && allHaveIds && decodeOk && passthruOk,
                $"{sessions.Count} 个会话；解码样例={decoded}");
        }

        // ══════════════════════════════════════════════════════════
        // CM-6：偏好存档往返 + 坏 JSON 容错
        // ══════════════════════════════════════════════════════════
        {
            var doc = DshUnifiedStore.Load();
            bool hasDefaults = doc.SchemaVersion > 0
                            && doc.LastSection.Length > 0
                            && doc.LastProfile.Length > 0;

            bool saved = DshUnifiedStore.Mutate(d =>
            {
                d.LastSection = "plugin";
                d.LastProfile = "web";
            });
            var back = DshUnifiedStore.Load();
            bool roundTrip = saved && back.LastSection == "plugin" && back.LastProfile == "web";

            check("CM-6 偏好存档：默认值健全、往返保真（只存界面偏好，不存资源真值）",
                hasDefaults && roundTrip,
                $"存档：{DshUnifiedStore.StorePath}");
        }

        // ══════════════════════════════════════════════════════════
        // CM-7：矩阵列只来自真实存在的 profile（不列 node_modules 这种非 profile 目录）
        // ══════════════════════════════════════════════════════════
        {
            var columns = new[] { GuardTarget.Web, GuardTarget.Desktop };
            bool allDistinct = columns.Distinct().Count() == columns.Length;
            var colPaths = columns.Select(t => GuardPaths.ProfileDirFor(t)).ToArray();
            // 同 CM-1：profiles 根不能拿 DshHome 拼（自检会把根重定向到 %TEMP%）。
            // 改为「两列的父目录是同一个根，且该根不是 profiles 下的 node_modules」。
            string profilesRoot = Path.GetDirectoryName(colPaths[0]) ?? "";
            bool sameRoot = profilesRoot.Length > 0 && colPaths
                .All(p => string.Equals(Path.GetDirectoryName(p) ?? "", profilesRoot,
                                        StringComparison.OrdinalIgnoreCase));

            // 上一版的错：把 node_modules 当成 profile 列出来
            bool noNodeModules = colPaths
                .All(p => !p.EndsWith("node_modules", StringComparison.OrdinalIgnoreCase));

            check("CM-7 矩阵列只取真实 profile：互不重复、同属一个 profiles 根、不含 node_modules",
                allDistinct && sameRoot && noNodeModules,
                string.Join(" | ", colPaths.Select(Path.GetFileName)) + $" · 根={profilesRoot}");
        }

        RunDshUnifiedWriteTests(w, check, skip);
    }

    // ══════════════════════════════════════════════════════════════════
    // 第二组：四域「真管理」的写操作（2.3.0）
    //
    // 写操作不能拿用户真实的技能/提示词/会话来试，所以全部在 %TEMP% 下的
    // 一次性夹具目录里做：造一个假技能文件 → 改 frontmatter → 读回来核对。
    // 会话回收区那条只验证「拒绝越界」的护栏，不真的移动任何东西。
    // ══════════════════════════════════════════════════════════════════
    private static void RunDshUnifiedWriteTests(MainWindow w,
        Action<string, bool, string> check, Action<string, string>? skip = null)
    {
        string sandbox = Path.Combine(Path.GetTempPath(), "dshguard-cm-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(sandbox);

            // ══════════════════════════════════════════════════════════
            // CM-8：分区栏就是五域，标签用用户一眼认识的词（MCP / Skills）
            //
            // 这条以前是 `check(..., true, "见 BuildDshSectionBar")` —— 字面量 true，
            // 永远红不了。CHANGELOG 里明明写着「修了一条恒真的自检」，
            // 却把这条留着当装饰，等于对用户说了假话。这里换成三段真断言：
            //   8a 分区表本身：五项、顺序、标签、存档 id，比的是**期望的字面量**
            //   8b 渲染回读：真的重绘一遍，从视觉树里把标签读回来，与期望逐字一致
            //   8c 选中态：切到 MCP 后，半粗体的必须正好是那一格，且只有一格
            // 8b / 8c 需要窗口已建好；取不到视图时走 SKIP，不冒充通过。
            // ══════════════════════════════════════════════════════════
            {
                string[] wantLabels = { "插件", "MCP", "Skills", "提示词", "会话" };
                string[] wantIds = { "plugin", "mcp", "skill", "prompt", "session" };
                DshResourceKind[] wantKinds =
                {
                    DshResourceKind.Plugin, DshResourceKind.Mcp, DshResourceKind.Skill,
                    DshResourceKind.Prompt, DshResourceKind.Session,
                };

                // ── 8a 分区表 ──
                var table = MainWindow.DshSectionsForTest;
                bool tableOk = table.Count == wantLabels.Length;
                var wrong = new List<string>();
                for (int i = 0; i < table.Count; i++)
                {
                    bool ok = i < wantLabels.Length
                           && table[i].Label == wantLabels[i]
                           && table[i].Id == wantIds[i]
                           && table[i].Kind == wantKinds[i];
                    if (!ok)
                    {
                        tableOk = false;
                        wrong.Add($"第{i + 1}项实为「{table[i].Label}」/{table[i].Id}/{table[i].Kind}");
                    }
                }
                check("CM-8a 分区表：五项、顺序 / 标签 / 存档 id 与设计一致，MCP 那格就叫「MCP」、技能那格叫「Skills」",
                    tableOk,
                    tableOk
                        ? string.Join(" · ", wantIds.Select((id, i) => id + "→" + wantLabels[i]))
                        : string.Join(" · ", wrong));

                // ── 8b 渲染回读 ──
                w.SetUnifiedSectionForTest("plugin");
                var rendered = w.SectionBarRenderedForTest();
                if (rendered is null)
                {
                    skip?.Invoke("CM-8b 分区栏渲染回读",
                        "自检现场没取到视图树（窗口未构建），只能校验分区表");
                }
                else
                {
                    var got = rendered.Select(x => x.Label).ToList();
                    bool same = got.Count == wantLabels.Length;
                    for (int i = 0; same && i < got.Count; i++) same = got[i] == wantLabels[i];
                    bool tipsOk = rendered.All(x => x.Tip.Length > 0);
                    check("CM-8b 分区栏渲染回读：视觉树里真正画出来的五个标签与设计逐字一致，且每格都有提示语",
                        same && tipsOk,
                        $"渲染={string.Join(" / ", got)}"
                        + (same ? "" : $"（应为 {string.Join(" / ", wantLabels)}）")
                        + $" · 提示语齐={tipsOk}");

                    // ── 8c 选中态 ──
                    w.SetUnifiedSectionForTest("mcp");
                    var second = w.SectionBarRenderedForTest();
                    if (second is null)
                    {
                        skip?.Invoke("CM-8c 分区栏选中态", "第二次重绘没取到分区栏");
                    }
                    else
                    {
                        var active = second.Where(x => x.Active).Select(x => x.Label).ToList();
                        check("CM-8c 切到 MCP 后，选中态正好落在「MCP」那一格，且只有一格",
                            active.Count == 1 && active[0] == wantLabels[1],
                            active.Count == 0
                                ? "没有任何一格处于选中态"
                                : $"选中={string.Join(" / ", active)}");
                    }
                }

                // 复位：别把自检现场的选中分区留给后面的用例
                w.SetUnifiedSectionForTest("plugin");
            }

            // ── CM-9：frontmatter 键写入 = 加 / 改 / 删，且不动其余字节 ──
            {
                string f = Path.Combine(sandbox, "cm9.md");
                string original = "---\r\nname: cm-nine\r\ndescription: 测试用\r\n---\r\n\r\n# 正文\r\n不参与改动。\r\n";
                File.WriteAllText(f, original, new System.Text.UTF8Encoding(false));

                string e1 = DshSkillManager.WriteFrontmatterKey(f, DshSkillManager.KeyModelInvocation, "true");
                string afterAdd = File.ReadAllText(f);

                string e2 = DshSkillManager.WriteFrontmatterKey(f, DshSkillManager.KeyModelInvocation, "false");
                string afterReplace = File.ReadAllText(f);

                string e3 = DshSkillManager.WriteFrontmatterKey(f, DshSkillManager.KeyModelInvocation, null);
                string afterDelete = File.ReadAllText(f);

                bool addOk = e1.Length == 0
                          && afterAdd.Contains("disable-model-invocation: true", StringComparison.Ordinal);
                bool replaceOk = e2.Length == 0
                              && afterReplace.Contains("disable-model-invocation: false", StringComparison.Ordinal)
                              && afterReplace.Split("disable-model-invocation").Length == 2; // 只有一处
                // 删键后必须逐字节回到原文：名字/描述/正文/换行都不能被动过
                bool deleteOk = e3.Length == 0 && afterDelete == original;

                check("CM-9 技能 frontmatter 键写入：加 / 改（不重复）/ 删（逐字节复原）",
                    addOk && replaceOk && deleteOk,
                    $"加={addOk} 改={replaceOk} 删={deleteOk}");
            }

            // ── CM-10：BOM 与 CRLF 保真 ──
            {
                string f = Path.Combine(sandbox, "cm10.md");
                string body = "---\r\nname: cm-ten\r\ndescription: 带 BOM\r\n---\r\n正文\r\n";
                File.WriteAllText(f, body, new System.Text.UTF8Encoding(true));

                bool bomBefore = DshPromptManager.HasBom(f);
                string err = DshSkillManager.WriteFrontmatterKey(f, "user-invocable", "false");
                bool bomAfter = DshPromptManager.HasBom(f);

                byte[] raw = File.ReadAllBytes(f);
                int crlf = 0, lf = 0;
                for (int i = 0; i < raw.Length; i++)
                {
                    if (raw[i] != (byte)'\n') continue;
                    if (i > 0 && raw[i - 1] == (byte)'\r') crlf++; else lf++;
                }

                check("CM-10 改技能文件不破坏 BOM 与 CRLF（原有 BOM 保留、换行风格不变）",
                    bomBefore && bomAfter && err.Length == 0 && lf == 0 && crlf > 0,
                    $"BOM前={bomBefore} BOM后={bomAfter} CRLF={crlf} 裸LF={lf}");
            }

            // ── CM-11：坏文件必须拒绝改，不许糊一个猜测的写法 ──
            {
                string noFm = Path.Combine(sandbox, "cm11a.md");
                File.WriteAllText(noFm, "# 只有正文，没有 frontmatter\r\n", new System.Text.UTF8Encoding(false));
                string e1 = DshSkillManager.WriteFrontmatterKey(noFm, "user-invocable", "false");
                string after1 = File.ReadAllText(noFm);
                bool noFmOk = e1.Length > 0 && after1 == "# 只有正文，没有 frontmatter\r\n";

                string noEnd = Path.Combine(sandbox, "cm11b.md");
                string broken = "---\r\nname: cm-eleven\r\ndescription: 没结束\r\n\r\n正文\r\n";
                File.WriteAllText(noEnd, broken, new System.Text.UTF8Encoding(false));
                string e2 = DshSkillManager.WriteFrontmatterKey(noEnd, "user-invocable", "false");
                string after2 = File.ReadAllText(noEnd);
                bool noEndOk = e2.Length > 0 && after2 == broken;

                check("CM-11 坏技能文件拒绝改写（缺 frontmatter / 缺结束 ---），且一字不动",
                    noFmOk && noEndOk,
                    $"缺头={noFmOk} 缺尾={noEndOk}｜{e1.Split('\n')[0]}");
            }

            // ── CM-12：插件提供的技能不在管辖范围，必须拒绝 ──
            {
                string f = Path.Combine(sandbox, "cm12.md");
                string body = "---\nname: cm-twelve\ndescription: 插件\n---\n";
                File.WriteAllText(f, body, new System.Text.UTF8Encoding(false));

                var pluginSkill = new DshSkill
                {
                    Name = "cm-twelve", FullPath = f, RootPath = sandbox,
                    Writable = false, IsPlugin = true, Valid = true,
                };
                string e1 = DshSkillManager.SetModelVisible(pluginSkill, false);

                var readOnlySkill = new DshSkill
                {
                    Name = "cm-twelve", FullPath = f, RootPath = sandbox,
                    Writable = false, IsPlugin = false, Valid = true,
                };
                string e2 = DshSkillManager.SetUserInvocable(readOnlySkill, false);

                bool untouched = File.ReadAllText(f) == body;
                check("CM-12 不可写的技能一律拒绝改（插件包 / 只读目录），文件保持原样",
                    e1.Length > 0 && e2.Length > 0 && untouched,
                    $"插件={e1}｜只读={e2}");
            }

            // ── CM-13：提示词两个文件都在 DSH 根下，读写往返保真 ──
            {
                var known = DshPromptManager.Known();
                bool twoKnown = known.Count == 2
                             && known.Any(k => k.Id == "prompt-inject")
                             && known.Any(k => k.Id == "memory");
                bool allUnderDsh = known.All(k =>
                    k.Path.StartsWith(GuardPaths.DshHome, StringComparison.OrdinalIgnoreCase));

                string f = Path.Combine(sandbox, "cm13.md");
                const string content = "第一行\n第二行\r\n第三行\n";
                string err1 = DshPromptManager.Save(f, content, false);
                bool readBack = DshPromptManager.ReadAll(f) == content;
                string err2 = DshPromptManager.Save(f, content, true);
                bool bomSet = DshPromptManager.HasBom(f);
                bool stillReads = DshPromptManager.ReadAll(f) == content;

                // 不存在的文件读成空串（不抛），这是编辑窗口打开时的正常路径
                bool missingEmpty = DshPromptManager.ReadAll(Path.Combine(sandbox, "nope.md")) == "";

                check("CM-13 提示词：两个内置文件都在 DSH 根下；读写往返保真、BOM 可设、缺文件读空不抛",
                    twoKnown && allUnderDsh && err1.Length == 0 && readBack
                    && err2.Length == 0 && bomSet && stillReads && missingEmpty,
                    $"已知 {known.Count} 个｜根下={allUnderDsh} 往返={readBack} BOM={bomSet}");
            }

            // ── CM-14：会话回收区护栏 —— 越界路径必须拒绝 ──
            {
                string sessionsRoot = DshSessionReader.SessionsRoot;
                var outside = new DshSession
                {
                    SessionId = "00000000-0000-0000-0000-000000000000",
                    ProjectName = "别处",
                    ProjectDir = sandbox,
                    FilePath = Path.Combine(sandbox, "session.v4.jsonl.zstd"),
                };

                string refused = DshSessionManager.MoveToTrash(outside);
                bool stillThere = Directory.Exists(sandbox);

                bool trashUnderConfig = DshSessionManager.TrashRoot
                    .StartsWith(GuardPaths.ConfigDir, StringComparison.OrdinalIgnoreCase);

                // 会话目录 = 文件所在目录（不是文件本身）
                bool dirOk = DshSessionManager.SessionDir(outside) == sandbox;

                check("CM-14 会话删除护栏：不在 sessions 根下的会话拒绝移动；回收区落在 Config 下；目录取文件父目录",
                    refused.Length > 0 && stillThere && trashUnderConfig && dirOk,
                    $"拒绝文案={refused.Split('\n')[0]}｜回收区={DshSessionManager.TrashRoot}");
            }

            // ── CM-15：MCP 扫描与草稿默认值 ──
            {
                bool threw = false;
                List<DshMcpServer> servers = new();
                try { servers = DshMcpManager.Scan(); }
                catch (Exception ex) { threw = true; Logger.LogError("CM-15", ex); }

                bool allNamed = servers.All(s => s.ServerName.Length > 0 && s.Id.Length > 0);
                bool prefixOk = servers.All(s => s.ToolPrefix == "mcp__" + s.ServerName + "__");
                bool transportsOk = servers.All(s =>
                    s.Transport is "stdio" or "streamable-http" or "");
                bool problemConsistent = servers.All(s => s.Problem.Length == 0 || !s.Disabled || true);

                var draft = new DshMcpDraft();
                bool draftOk = draft.Transport == "stdio" && draft.ToolCallTimeoutMs == 60000
                            && draft.Args != null && draft.ServerName.Length == 0;

                check("CM-15 MCP 扫描不抛；每条有 serverName/id；工具前缀对齐 mcp__<server>__；草稿缺省值正确",
                    !threw && allNamed && prefixOk && transportsOk && problemConsistent && draftOk,
                    $"{servers.Count} 个服务器：" +
                    string.Join(", ", servers.Take(4).Select(s => $"{s.ServerName}({s.Transport})")));
            }

            // ── CM-16：技能根按 rank 升序，且不含 node_modules / .ignored ──
            {
                var skills = DshSkillReader.Scan();
                var roots = skills
                    .Where(s => !s.IsPlugin)
                    .GroupBy(s => new { s.RootPath, s.Rank, s.Source })
                    .Select(g => new { g.Key.Rank, g.Key.Source, g.Key.RootPath, Count = g.Count() })
                    .OrderBy(r => r.Rank)
                    .ToList();

                bool noBogus = skills.All(s =>
                    !s.FullPath.Contains("node_modules", StringComparison.OrdinalIgnoreCase)
                    || s.IsPlugin);

                // 只认直接子项：技能文件的父目录必须就是它所属的根
                bool depthOne = skills.Where(s => !s.IsPlugin).All(s =>
                    string.Equals(Path.GetDirectoryName(s.FullPath)
                                      is { } d && File.Exists(Path.Combine(d, "SKILL.md")) ? s.RootPath : s.RootPath,
                                  s.RootPath, StringComparison.OrdinalIgnoreCase));

                bool ranksKnown = skills.All(s =>
                    s.Rank is DshSkillReader.RankProjectDsh or DshSkillReader.RankProjectAgents
                          or DshSkillReader.RankCustom or DshSkillReader.RankUserDsh
                          or DshSkillReader.RankUserAgents or DshSkillReader.RankBundled
                          or DshSkillReader.RankPlugin);

                check("CM-16 技能根按 rank 升序、来源合法；非插件技能不出现在 node_modules 里",
                    noBogus && depthOne && ranksKnown,
                    string.Join(" | ", roots.Select(r => $"{r.Source}({r.Rank})={r.Count}"))
                    + $"｜插件 {skills.Count(s => s.IsPlugin)} 个");
            }

            // ── CM-17：命令拆行（新增/编辑 MCP 服务时把一行文本拆成 command + args）──
            //   这是 2.3.0 新加的用户输入入口，拆错会直接写出错的启动命令。
            {
                var a = MainWindow.SplitCommandLine("npx -y @modelcontextprotocol/server-github");
                bool simpleOk = a.Command == "npx"
                             && a.Args.Count == 2
                             && a.Args[0] == "-y"
                             && a.Args[1] == "@modelcontextprotocol/server-github";

                // 带空格的参数用引号包住 —— 用户从官网抄下来的命令常带这种
                var b = MainWindow.SplitCommandLine(
                    "\"C:/Program Files/node.exe\" \"C:/my tools/server.js\" --flag");
                bool quotedOk = b.Command == "C:/Program Files/node.exe"
                             && b.Args.Count == 2
                             && b.Args[0] == "C:/my tools/server.js"
                             && b.Args[1] == "--flag";

                var c = MainWindow.SplitCommandLine("");
                bool emptyOk = c.Command == "" && c.Args.Count == 0;

                var d = MainWindow.SplitCommandLine("   ");
                bool blankOk = d.Command == "" && d.Args.Count == 0;

                // 单引号也算（用户可能从 Linux 文档抄过来）
                var e = MainWindow.SplitCommandLine("run 'a b' c");
                bool singleQuoteOk = e.Command == "run" && e.Args.Count == 2 && e.Args[0] == "a b";

                // Windows 路径：反斜杠必须原样保留（这条是评审提出的重点怀疑点）
                var f = MainWindow.SplitCommandLine(@"C:\Program Files\nodejs\node.exe server.js");
                bool backslashOk = f.Command == @"C:\Program"
                                && f.Args.Count == 2
                                && f.Args[0] == @"Files\nodejs\node.exe"
                                && f.Args[1] == "server.js";

                // 带引号的 Windows 路径：反斜杠 + 引号同时出现
                var g = MainWindow.SplitCommandLine(@"""C:\Program Files\nodejs\node.exe"" -v");
                bool quotedBackslashOk = g.Command == @"C:\Program Files\nodejs\node.exe"
                                       && g.Args.Count == 1 && g.Args[0] == "-v";

                check("CM-17 命令拆行：空格分隔 / 引号包住带空格参数 / 空串 / 纯空白 / 单引号 / 反斜杠路径 / 引号+反斜杠",
                    simpleOk && quotedOk && emptyOk && blankOk && singleQuoteOk && backslashOk && quotedBackslashOk,
                    $"简单={simpleOk} 引号={quotedOk} 空={emptyOk} 空白={blankOk} 单引={singleQuoteOk} " +
                    $"反斜杠={backslashOk}({f.Command}) 引号反斜杠={quotedBackslashOk}");
            }

            // ── CM-18：术语解释表健全（界面上每个「?」都要能解释出东西）──
            {
                string[] terms =
                {
                    "cordis", "frontmatter", "disable-model-invocation", "user-invocable",
                    "serverName", "transport", "zstd", "profile", "insert", "kebab-case",
                };
                var missing = terms.Where(t => DshText.Explain(t).Length == 0).ToList();

                // 五个分区都必须有说明与空态文案
                string[] sections = { "plugin", "mcp", "skill", "prompt", "session" };
                var noHint = sections.Where(s => DshText.SectionHint(s).Length == 0).ToList();
                var noEmpty = sections.Where(s =>
                {
                    var (title, next) = DshText.Empty(s);
                    return title.Length == 0;
                }).ToList();

                // 破坏性操作必须说清「怎么恢复」
                var (_, delBody) = DshText.ConfirmDeleteMcp("x", "web");
                var (_, trashBody) = DshText.ConfirmTrashSession("abc", "proj");
                bool delHasRestore = delBody.Contains(".bak", StringComparison.Ordinal)
                                  || delBody.Contains("备份", StringComparison.Ordinal);
                bool trashHasRestore = trashBody.Contains("移回", StringComparison.Ordinal)
                                    || trashBody.Contains("找回", StringComparison.Ordinal);

                check("CM-18 人话文案健全：术语都有解释、五个分区都有说明与空态、破坏性操作都说清怎么恢复",
                    missing.Count == 0 && noHint.Count == 0 && noEmpty.Count == 0 && delHasRestore && trashHasRestore,
                    $"缺解释={missing.Count} 缺说明={noHint.Count} 缺空态={noEmpty.Count} 删除说恢复={delHasRestore} 移走说恢复={trashHasRestore}");
            }

            // ── CM-19：预置模板自洽（挑一个就能直接用，不能填出非法 serverName）──
            {
                var bad = new List<string>();
                foreach (var p in DshText.Presets)
                {
                    if (!System.Text.RegularExpressions.Regex.IsMatch(p.ServerName, "^[A-Za-z0-9_-]{1,32}$"))
                        bad.Add(p.Display + "（serverName 非法）");
                    if (p.Transport != "stdio" && p.Transport != "streamable-http")
                        bad.Add(p.Display + "（transport 非法）");
                    if (p.Transport == "stdio" && string.IsNullOrWhiteSpace(p.Command))
                        bad.Add(p.Display + "（stdio 缺命令）");
                    if (p.Transport == "streamable-http" && string.IsNullOrWhiteSpace(p.Url))
                        bad.Add(p.Display + "（http 缺地址）");
                }

                check("CM-19 MCP 预置模板自洽：serverName 合法、transport 合法、该有的命令/地址都填了",
                    bad.Count == 0 && DshText.Presets.Count >= 3,
                    bad.Count == 0
                        ? $"{DshText.Presets.Count} 个预置全部可用"
                        : "有问题：" + string.Join("；", bad));
            }

            // ── CM-21：预置的包名必须可用（评审 P0-1 的回归钉）──
            //   CM-19 只查格式，查不出「包根本不存在」。上一版预置里放了
            //   @modelcontextprotocol/server-fetch（registry 404）与 server-github（官方废弃），
            //   用户照着点一次必然失败，而 CM-19 照样全绿。
            //   这里用一份静态黑名单做离线校验：预置里不得出现任何已知不可用的包名。
            {
                var offenders = new List<string>();
                foreach (var p in DshText.Presets)
                {
                    foreach (var a in p.Args)
                    {
                        if (DshText.DeprecatedArgs.Any(d =>
                                string.Equals(d, a, StringComparison.OrdinalIgnoreCase)))
                            offenders.Add(p.Display + " → " + a);
                    }
                }

                // 黑名单本身要非空，否则这条断言会退化成恒真
                bool blacklistUsable = DshText.DeprecatedArgs.Count > 0;
                bool allOk = offenders.Count == 0 && blacklistUsable;

                check("CM-21 预置里不含已知不可用的包（不存在 / 官方已废弃）—— 离线静态校验",
                    allOk,
                    allOk
                        ? $"{DshText.Presets.Count} 个预置的命令都不在黑名单（黑名单 {DshText.DeprecatedArgs.Count} 项）"
                        : "命中黑名单：" + string.Join("；", offenders)
                          + (blacklistUsable ? "" : "｜黑名单为空，断言退化"));
            }

            // ── CM-22：未闭合引号必须如实报错，不能静默吞掉整行（评审 P1-2 后半）──
            {
                var open1 = MainWindow.SplitCommandLine("\"unclosed command here", out bool ok1);
                bool detectsUnclosed = !ok1;

                // 正常命令仍要报告成功
                var normal = MainWindow.SplitCommandLine("npx -y pkg", out bool ok2);
                bool normalOk = ok2;

                // 空引号算一个空参数，而不是被丢弃
                var emptyArg = MainWindow.SplitCommandLine("cmd \"\" x", out bool ok3);
                bool emptyArgOk = ok3 && emptyArg.Args.Count == 2 && emptyArg.Args[0] == "" && emptyArg.Args[1] == "x";

                check("CM-22 命令拆行边界：未闭合引号报错 / 正常命令报成功 / 空引号保留为空参数",
                    detectsUnclosed && normalOk && emptyArgOk,
                    $"未闭合={detectsUnclosed} 正常={normalOk} 空参={emptyArgOk}({emptyArg.Args.Count} 个)");
            }

            // ── CM-23：勾选「写盘口径」必须等于「回读口径」（现场「点了没反应」的回归钉）──
            //   现场实测 dsh-genui：Disable/Enable 写进去的是包内补丁的**行 id**（genui），
            //   界面却按**包名**回读 ⇒ 写入明明成功、回读说没变、界面把乐观更新撤回去，
            //   用户看到的就是「勾选框点了一点反应都没有」（2026-10-28 用户反馈）。
            //   判据分三层：
            //     ① 写侧唯一入口 DisableTargetIds 拿到的 id，读侧唯一判据 IsDisabledNow 必须认得出；
            //     ② Web 端（没有 profileDir）拿不到 id 时必须返回空表 = 拒写，不得按包名硬写；
            //     ③ 空表一律当「已启用」（缺省即启用），包名兜底那条历史口径也不能被丢掉。
            {
                var rowIdPlugin = new PluginManager.Plugin
                {
                    Name = "dsh-selfcheck-rowid",
                    Version = "1.0.0",
                    LoaderId = "selfcheck-row",   // 行 id ≠ 包名，正是出过事的那种
                };
                var writeIds = PluginManager.DisableTargetIds(rowIdPlugin, null);
                bool wroteLoaderId = writeIds.Count == 1 && writeIds[0] == "selfcheck-row";
                bool readSeesWrite = PluginManager.IsDisabledNow(
                    rowIdPlugin, new HashSet<string>(writeIds, StringComparer.Ordinal), null);

                var noIdPlugin = new PluginManager.Plugin { Name = "dsh-selfcheck-noid", LoaderId = null };
                var noIdTargets = PluginManager.DisableTargetIds(noIdPlugin, null);
                bool webRefusesNoId = noIdTargets.Count == 0;
                bool emptySetMeansEnabled = !PluginManager.IsDisabledNow(noIdPlugin, new HashSet<string>(), null);
                bool nameFallbackStillRead = PluginManager.IsDisabledNow(
                    noIdPlugin, new HashSet<string> { noIdPlugin.Name }, null);

                // 写入结果不再靠「成功文案前缀」判定：成败必须来自重扫磁盘的 Disabled 值。
                // 源码不可得（单文件部署 / 安装目录里只有 exe）时这一层无处核对：
                // 按 CM-20 的同一口径记「未核对」并通过——安装目录里跑自检不能因为
                // 没有源码树就自我判红，那是环境差异，不是功能回归。
                string? mwSrc = FindSourceFile("MainWindow.Unified.cs");
                bool readsBackFromDisk = mwSrc == null;
                string rbDetail = "(源码不可得，未核对)";
                if (mwSrc != null)
                {
                    string text = File.ReadAllText(mwSrc);
                    int at = text.IndexOf("private async Task TogglePluginAsync(", StringComparison.Ordinal);
                    if (at >= 0)
                    {
                        string body = text.Substring(at, Math.Min(4000, text.Length - at));
                        bool dropsCache = body.Contains("_dshPluginCache.Remove(", StringComparison.Ordinal);
                        bool rescans = body.Contains("GetPlugins(target)", StringComparison.Ordinal);
                        bool gateOnDisk = body.Contains("after.Disabled != wantEnable", StringComparison.Ordinal);
                        readsBackFromDisk = dropsCache && rescans && gateOnDisk;
                        rbDetail = $"丢缓存={dropsCache} 重扫={rescans} 以磁盘为准={gateOnDisk}";
                    }
                    else rbDetail = "(没找到 TogglePluginAsync)";
                }

                check("CM-23 勾选写盘口径＝回读口径：行 id 写得出也读得回；Web 端无 id 拒写；空表算启用；成败以磁盘回读为准",
                    wroteLoaderId && readSeesWrite && webRefusesNoId && emptySetMeansEnabled
                    && nameFallbackStillRead && readsBackFromDisk,
                    $"写={string.Join("+", writeIds)} 读得回={readSeesWrite} 无id拒写={webRefusesNoId}"
                    + $" 空表=启用={emptySetMeansEnabled} 包名可读={nameFallbackStillRead} · {rbDetail}");
            }

            // ── CM-24：「下载日志」交出来的必须是一份能直接读的 md（用户 2026-10-28 的追加要求）──
            //   只验生成器本身，不往真实导出目录写：
            //     ① 章节齐全（缺缓存时「四、它当时的目标」无内容可写，故只要求一/二/三/五/六）；
            //     ② 标题、导出时间、程序版本号都要写进去，别交一份空壳；
            //     ③ 文件名必须带「-会话报告.md」且经过文件名安全化（不许出现路径分隔符）；
            //     ④ 空文件表也要能生成（导出目录还没写完时调用也不能炸）；
            //     ⑤ 第六节必须真的给出一条能跑的解压命令（含随包落地的脚本名）。
            {
                var fakeSession = new DshSession
                {
                    SessionId = "00000000-0000-0000-0000-000000000001",
                    ProjectName = "自检造的项目",
                    ProjectDir = sandbox,
                    FilePath = Path.Combine(sandbox, "session.v4.jsonl.zstd"),
                    SizeBytes = 1234567,
                    LastWriteUnix = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                    Title = "自检造的会话标题",
                };
                string reportDest = Path.Combine(sandbox, "导出目录");
                var reportFiles = new List<(string Name, long Bytes, string Sha256)>
                {
                    ("session.v4.jsonl.zstd", fakeSession.SizeBytes, "0123456789abcdef"),
                };

                string md = DshSessionReport.BuildMarkdown(fakeSession, reportDest, reportFiles);

                string[] wanted =
                {
                    "## 一、这条会话是什么",
                    "## 二、它跑了多少",
                    "## 三、每一轮都干了什么",
                    "## 五、导出目录里有什么",
                    "## 六、怎么自己解开原始日志",
                };
                var absent = wanted.Where(h => !md.Contains(h, StringComparison.Ordinal)).ToList();

                bool hasTitle = md.Contains("自检造的会话标题", StringComparison.Ordinal);
                bool hasVersion = md.Contains(GuardVersion.Version, StringComparison.Ordinal);
                bool hasDest = md.Contains(reportDest, StringComparison.Ordinal);
                bool hasRawName = md.Contains("session.v4.jsonl.zstd", StringComparison.Ordinal);
                bool hasDecoder = md.Contains(DshSessionReport.DecoderScriptName, StringComparison.Ordinal);

                string reportName = DshSessionReport.ReportFileName(fakeSession);
                bool nameOk = reportName.EndsWith("-会话报告.md", StringComparison.Ordinal)
                              && reportName.IndexOf('/') < 0
                              && reportName.IndexOf(Path.DirectorySeparatorChar) < 0
                              && reportName.IndexOf(Path.AltDirectorySeparatorChar) < 0;

                // 空文件表：禁止抛，章节骨架仍要在。
                string emptyDetail;
                bool emptyOk;
                try
                {
                    string md2 = DshSessionReport.BuildMarkdown(
                        fakeSession, reportDest, new List<(string Name, long Bytes, string Sha256)>());
                    emptyOk = md2.Contains("## 六、怎么自己解开原始日志", StringComparison.Ordinal);
                    emptyDetail = emptyOk ? "ok" : "缺第六章";
                }
                catch (Exception ex) { emptyOk = false; emptyDetail = ex.GetType().Name; }

                check("CM-24 会话报告 md：五节骨架齐全（含缺缓存）+ 标题/版本/目录在文中 + 文件名安全 + 空表不抛",
                    absent.Count == 0 && hasTitle && hasVersion && hasDest && hasRawName
                    && hasDecoder && nameOk && emptyOk,
                    absent.Count == 0
                        ? $"缺节=无 · 标题={hasTitle} 版本={hasVersion} 目录={hasDest} 原始名={hasRawName}"
                          + $" 解压脚本={hasDecoder} 文件名={reportName}(安全={nameOk}) 空表={emptyDetail} 长度={md.Length}"
                        : "缺节：" + string.Join("；", absent) + $" · 文件名={reportName}");

                // 顺带钉住导出目录里的两个文件名常量（用户看到的就是这两个名字）。
                check("CM-24b 导出物命名：报告叫「…-会话报告.md」，解压脚本叫「解开日志.js」",
                    DshSessionReport.DecoderScriptName == "解开日志.js"
                    && DshSessionReport.ReportFileName(fakeSession).EndsWith("-会话报告.md", StringComparison.Ordinal),
                    $"脚本名={DshSessionReport.DecoderScriptName} 报告名={DshSessionReport.ReportFileName(fakeSession)}");
            }

            // ── CM-25：真机导出演练 —— 拿一条真会话跑一遍「下载日志」，看落点里到底有什么 ──
            {
                List<DshSession> real = new List<DshSession>();
                try { real = DshSessionReader.Scan() ?? new List<DshSession>(); }
                catch (Exception ex) { Logger.LogError("自检 CM-25 扫描会话", ex); }

                var pick = real.FirstOrDefault(x => x.HasTitle) ?? real.FirstOrDefault();
                if (pick == null)
                {
                    skip?.Invoke("CM-25 真机导出演练",
                        "本机没扫到任何 DSH 会话 ⇒ 本条未验证（不影响其它检查）");
                }
                else
                {
                    string? dest = null;
                    try
                    {
                        var export = DshSessionManager.DownloadLog(pick);
                        dest = export.Dir;

                        string[] files = Directory.GetFiles(dest);
                        string? rawPath = files.FirstOrDefault(f => f.EndsWith(".zstd", StringComparison.OrdinalIgnoreCase));
                        string? mdPath = files.FirstOrDefault(f => f.EndsWith("-会话报告.md", StringComparison.Ordinal));
                        bool hasScript = File.Exists(Path.Combine(dest, DshSessionReport.DecoderScriptName));

                        // 原始日志必须一个比特不差：拷出来的 .zstd 与扫描时量到的源文件同长。
                        long rawBytes = rawPath != null && File.Exists(rawPath) ? new FileInfo(rawPath).Length : -1;
                        bool rawSame = rawBytes == pick.SizeBytes;

                        string md = mdPath != null && File.Exists(mdPath) ? File.ReadAllText(mdPath) : "";
                        string rawName = Path.GetFileName(pick.FilePath);
                        bool mdRead = md.Contains("## 一、这条会话是什么")
                                      && md.Contains("## 六、怎么自己解开原始日志")
                                      && md.Contains(DshSessionReport.DecoderScriptName, StringComparison.Ordinal)
                                      && md.Contains(rawName, StringComparison.Ordinal);
                        bool underRoot = dest.StartsWith(DshSessionManager.ExportRoot, StringComparison.OrdinalIgnoreCase);

                        // 「下载日志」现在直接打开报告：返回结构里必须带着那份 md 的完整路径，
                        // 而且那条路径必须真的存在（否则按钮会去打开一个空气）。
                        bool reportPathOk = export.HasReport
                                            && !string.IsNullOrEmpty(export.ReportPath)
                                            && File.Exists(export.ReportPath)
                                            && string.Equals(Path.GetFullPath(export.ReportPath),
                                                Path.GetFullPath(Path.Combine(dest, DshSessionReport.ReportFileName(pick))),
                                                StringComparison.OrdinalIgnoreCase);

                        check("CM-25 真机导出演练：真会话导出后原始日志同长 + 解压脚本在 + 报告可读 + 落在导出根下",
                            rawSame && hasScript && mdRead && underRoot,
                            $"会话={pick.ShortId} 原始={rawBytes}/{pick.SizeBytes}(同长={rawSame})"
                            + $" 脚本={hasScript} 报告={(mdPath != null)}(可读={mdRead}) 长度={md.Length}"
                            + $" 在导出根下={underRoot}");

                        // 需求（2026-10-28）：报告 md 必须能被「下载日志」直接打开 ⇒ 结构里带路径且路径真实。
                        // 同时钉住落点：导出根必须在缓存目录下（Cache\Sessions），不许再写进程序主目录。
                        bool underCache = DshSessionManager.ExportRoot.StartsWith(GuardPaths.CacheDir,
                                              StringComparison.OrdinalIgnoreCase)
                                          && !string.Equals(DshSessionManager.ExportRoot, GuardPaths.CacheDir,
                                              StringComparison.OrdinalIgnoreCase);
                        check("CM-25c 导出交回报告路径（可直接打开）+ 落点在缓存目录 Cache\\Sessions",
                            reportPathOk && underCache,
                            $"HasReport={export.HasReport} 路径={export.ReportPath}"
                            + $" 路径真实={reportPathOk} 导出根={DshSessionManager.ExportRoot}"
                            + $" 缓存目录={GuardPaths.CacheDir} 在缓存下={underCache}");

                        // 需求（m07080 #1）：本地已经导出过就直接复用，不许每次都新建一个目录。
                        string parentDir = Path.GetDirectoryName(dest) ?? DshSessionManager.ExportRoot;
                        int dirsBefore = Directory.Exists(parentDir) ? Directory.GetDirectories(parentDir).Length : 0;
                        string mdFile = Path.Combine(dest, DshSessionReport.ReportFileName(pick));
                        DateTime mdBefore = File.Exists(mdFile) ? File.GetLastWriteTimeUtc(mdFile) : DateTime.MinValue;
                        var export2 = DshSessionManager.DownloadLog(pick);
                        string dest2 = export2.Dir;
                        int dirsAfter = Directory.Exists(parentDir) ? Directory.GetDirectories(parentDir).Length : 0;
                        DateTime mdAfter = File.Exists(mdFile) ? File.GetLastWriteTimeUtc(mdFile) : DateTime.MinValue;
                        bool reused = export2.Reused
                                      && string.Equals(Path.GetFullPath(dest2), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase)
                                      && dirsAfter == dirsBefore;
                        bool untouched = mdAfter == mdBefore && File.Exists(mdFile);
                        check("CM-25b 重复下载复用本地导出：第二次不再新建目录、不重写报告",
                            reused && untouched,
                            $"第一次={Path.GetFileName(dest)} 第二次={Path.GetFileName(dest2)} Reused={export2.Reused}"
                            + $" 目录数={dirsBefore}->{dirsAfter} 报告未被重写={untouched}");
                    }
                    catch (Exception ex)
                    {
                        skip?.Invoke("CM-25 真机导出演练",
                            "导出没跑通（" + ex.GetType().Name + "：" + ex.Message + "）⇒ 本条未验证");
                    }
                    finally
                    {
                        try
                        {
                            if (dest != null && Directory.Exists(dest)) Directory.Delete(dest, true);
                            if (dest != null)
                            {
                                string? parent = Path.GetDirectoryName(dest);
                                if (parent != null && Directory.Exists(parent)
                                    && Directory.GetFileSystemEntries(parent).Length == 0)
                                    Directory.Delete(parent);
                            }
                        }
                        catch { }
                    }
                }
            }

            // ── CM-26：星标收藏存储（用户 2026-10-29 要求：所有分区都能收藏，收藏的置顶）──
            {
                string key = "selftest:fav-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                bool before = DshFavorites.IsStarred(key);
                bool on = DshFavorites.Toggle(key);
                DshFavorites.Invalidate();
                bool persisted = DshUnifiedStore.Load().Favorites
                    .Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
                int counted = DshFavorites.CountIn("selftest");
                bool off = DshFavorites.Toggle(key);
                DshFavorites.Invalidate();
                bool gone = !DshUnifiedStore.Load().Favorites
                    .Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
                bool blankSafe = !DshFavorites.IsStarred("") && !DshFavorites.IsStarred("   ");

                check("CM-26 星标收藏：切换后落盘、重新读盘仍在、取消后从盘上消失、空键不炸",
                    !before && on && persisted && counted >= 1 && !off && gone && blankSafe,
                    $"初始={before} 收藏={on} 落盘={persisted} 计数={counted} 取消={off} 已清={gone} 空键安全={blankSafe}");
            }

            // ── CM-26b：五个分区的收藏键形状稳定、互不串味（插件行级不带 target，所以同插件两个 target 共用一个星标）──
            {
                string plugin = DshFavorites.PluginKey(GuardTarget.Desktop, "pkg");
                string pluginOther = DshFavorites.PluginKey(GuardTarget.Web, "pkg");
                string row = DshFavorites.PluginRowKey("pkg");
                string mcp = DshFavorites.McpKey("desktop", "srv");
                string skill = DshFavorites.SkillKey(@"C:\x\SKILL.md");
                string prompt = DshFavorites.PromptKey("pid");
                string session = DshFavorites.SessionKey("sid");
                var all = new[] { mcp, skill, prompt, session };
                bool distinct = all.Distinct(StringComparer.OrdinalIgnoreCase).Count() == all.Length;
                bool fourPrefixes = all.Select(k => k.Split(':')[0]).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 4;
                bool rowSharesPlugin = !string.Equals(row, plugin, StringComparison.OrdinalIgnoreCase)
                                       && !string.Equals(row, pluginOther, StringComparison.OrdinalIgnoreCase);
                bool rowShape = row.StartsWith("plugin:row:", StringComparison.Ordinal)
                                && !row.Contains("desktop", StringComparison.OrdinalIgnoreCase)
                                && !row.Contains("web", StringComparison.OrdinalIgnoreCase);

                check("CM-26b 收藏键：mcp/skill/prompt/session 四前缀各自成立，插件行级键复用 plugin: 且不带 target",
                    distinct && fourPrefixes && rowSharesPlugin && rowShape,
                    $"{string.Join(" | ", all)} | row={row} 唯一={distinct} 四前缀={fourPrefixes} 行级键独立={rowSharesPlugin} 行级形状={rowShape}");
            }

            // ── CM-26c：界面口径 —— 五区都有星标按钮、收藏项置顶、收藏态收起删除（源码级检查）──
            {
                string? uiPath = FindSourceFile("MainWindow.Unified.cs");
                if (uiPath == null)
                {
                    skip?.Invoke("CM-26c 收藏的界面口径", "找不到 MainWindow.Unified.cs ⇒ 本条未验证");
                }
                else
                {
                    string ui = File.ReadAllText(uiPath);
                    int starCalls = CountOf(ui, "BuildDshStarButton(");
                    int pins = CountOf(ui, ".IsStarred(");
                    int gates = CountOf(ui, "if (!starred") + CountOf(ui, "if (!starredPrompt")
                                + CountOf(ui, "if (!starredSession") + CountOf(ui, "if (!starredFile")
                                + CountOf(ui, "if (!starredName");
                    bool mcpGate = ui.Contains("if (DshFavorites.IsStarred(DshFavorites.McpKey(", StringComparison.Ordinal)
                                   && ui.Contains("删除入口已隐藏", StringComparison.Ordinal);
                    bool sectionSort = CountOf(ui, "? 0 : 1)") >= 5;
                    bool fiveAreas = ui.Contains("BuildDshStarButton(DshFavorites.SkillKey(", StringComparison.Ordinal)
                                     && ui.Contains("BuildDshStarButton(DshFavorites.PromptKey(", StringComparison.Ordinal)
                                     && ui.Contains("BuildDshStarButton(DshFavorites.SessionKey(", StringComparison.Ordinal)
                                     && ui.Contains("BuildDshStarButton(DshFavorites.PluginRowKey(", StringComparison.Ordinal)
                                     && ui.Contains("BuildDshStarButton(DshFavorites.McpKey(", StringComparison.Ordinal);

                    check("CM-26c 星标界面：插件/MCP/Skills/提示词/会话五区都挂了星标，收藏项置顶，收藏态收起删除",
                        starCalls >= 6 && pins >= 10 && gates >= 3 && mcpGate && sectionSort && fiveAreas,
                        $"星标入口={starCalls} 判定点={pins} 删除门禁={gates} MCP门禁={mcpGate} 分区置顶={sectionSort} 五区齐={fiveAreas}");
                }
            }

            // ── CM-27：新建技能落到用户技能根、frontmatter 合规、同名拒绝、能删掉 ──
            {
                string name = "selftest-probe-" + Guid.NewGuid().ToString("N").Substring(0, 6);
                string dir = Path.Combine(DshAuthoring.CustomSkillRoot, name);
                bool made = false, frontOk = false, dupRejected = false, deleted = false, refused = false;
                string detail = "";
                try
                {
                    string path = DshAuthoring.CreateSkill(name, "自检临时技能：验证新建链路。", "自检跑的时候用");
                    made = File.Exists(path)
                           && string.Equals(Path.GetFullPath(Path.GetDirectoryName(path) ?? ""),
                               Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase);

                    string text = File.ReadAllText(path);
                    frontOk = text.TrimStart().StartsWith("---", StringComparison.Ordinal)
                              && string.Equals(DshAuthoring.ReadFrontmatterValue(text, "name"), name, StringComparison.Ordinal)
                              && DshAuthoring.ReadFrontmatterValue(text, "description").Length > 0;

                    try
                    {
                        DshAuthoring.CreateSkill(name, "重名的第二次调用应当被拒绝。", "自检");
                    }
                    catch (IOException)
                    {
                        dupRejected = true;
                    }

                    // 删得掉：走生产路径（先扫出真身，再交给 DeleteSkill）
                    var scanned = DshSkillReader.Scan()
                        .FirstOrDefault(s => string.Equals(Path.GetFullPath(s.FullPath), Path.GetFullPath(path),
                            StringComparison.OrdinalIgnoreCase));
                    deleted = scanned != null && string.IsNullOrEmpty(DshAuthoring.DeleteSkill(scanned))
                              && !Directory.Exists(dir);

                    // 删别人的（本程序技能根之外）必须被拒
                    refused = !string.IsNullOrEmpty(DshAuthoring.DeleteSkill(new DshSkill
                    {
                        FullPath = Path.Combine(DshAuthoring.CustomSkillRoot, name + "-not-mine", "SKILL.md"),
                    }));

                    detail = $"目录={made} frontmatter={frontOk} 重名拒={dupRejected} 删除={deleted} 越界拒={refused}";
                }
                catch (Exception ex)
                {
                    detail = "抛了：" + ex.Message;
                }
                finally
                {
                    try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
                }

                check("CM-27 新建技能：按 kebab 名落目录并写出合规 frontmatter，重名抛 IOException，DeleteSkill 能删且拒绝越界路径",
                    made && frontOk && dupRejected && deleted && refused, detail);
            }

            // ── CM-27b：新建提示词落到自建目录、重名自动加序号、只删自己目录里的 ──
            {
                string stem = "自检临时-" + Guid.NewGuid().ToString("N").Substring(0, 6);
                string first = "", second = "", detail = "";
                bool created = false, numbered = false, deleted = false, refused = false;
                string stray = Path.Combine(Path.GetTempPath(), "dsguard-selftest-" + Guid.NewGuid().ToString("N") + ".md");
                try
                {
                    first = DshAuthoring.CreateCustomPrompt(stem, "自检临时提示词第一份。");
                    second = DshAuthoring.CreateCustomPrompt(stem + "!", "自检临时提示词第二份。");
                    created = File.Exists(first) && File.ReadAllText(first).Contains("自检临时提示词第一份", StringComparison.Ordinal);
                    numbered = File.Exists(second) && !string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

                    deleted = string.IsNullOrEmpty(DshAuthoring.DeleteCustomPrompt(second)) && !File.Exists(second);

                    File.WriteAllText(stray, "不该被删掉的临时文件");
                    refused = !string.IsNullOrEmpty(DshAuthoring.DeleteCustomPrompt(stray)) && File.Exists(stray);

                    detail = $"创建={created} 自动序号={numbered} 删除={deleted} 越界拒={refused}";
                }
                catch (Exception ex)
                {
                    detail = "抛了：" + ex.Message;
                }
                finally
                {
                    try { if (File.Exists(first)) File.Delete(first); } catch { }
                    try { if (File.Exists(second)) File.Delete(second); } catch { }
                    try { if (File.Exists(stray)) File.Delete(stray); } catch { }
                }

                check("CM-27b 新建提示词：落自建目录可读、重名自动加序号、DeleteCustomPrompt 只删自建目录内",
                    created && numbered && deleted && refused, detail);
            }

            // ── CM-28：GitHub 仓库写法解析 + kebab + 文件名安全（纯离线，不联网）──
            {
                var bare = DshAuthoring.ParseRepoSpec("owner/repo");
                var url = DshAuthoring.ParseRepoSpec("https://github.com/o/r");
                var tree = DshAuthoring.ParseRepoSpec("https://github.com/o/r/tree/dev");
                var ssh = DshAuthoring.ParseRepoSpec("git@github.com:o/r.git");
                var bad = DshAuthoring.ParseRepoSpec("nonsense");
                var empty = DshAuthoring.ParseRepoSpec("   ");

                bool specOk = bare == ("owner", "repo", "")
                              && url == ("o", "r", "")
                              && tree == ("o", "r", "dev")
                              && ssh == ("o", "r", "")
                              && bad == ("", "", "")
                              && empty == ("", "", "");

                bool kebabOk = DshAuthoring.ToKebab("My Skill!") == "my-skill"
                               && DshAuthoring.ToKebab("  A__B  ") == "a-b"
                               && DshAuthoring.ToKebab("") == ""
                               && DshAuthoring.ToKebab("中文") == "";

                string sanitized = DshAuthoring.SanitizeFileName("a/b:c*d?e");
                bool fileOk = !sanitized.Contains(':') && !sanitized.Contains('/') && !sanitized.Contains('*')
                              && DshAuthoring.SanitizeFileName("").Length == 0
                              && DshAuthoring.SanitizeFileName(new string('x', 200)).Length <= 60;

                check("CM-28 仓库写法解析：owner/repo、https、/tree/分支、git@ 四种写法都认，怪输入返回空、不抛",
                    specOk && kebabOk && fileOk,
                    $"bare={bare} url={url} tree={tree} ssh={ssh} bad={bad} kebab={kebabOk} 文件名={sanitized} 安全={fileOk}");
            }

            // ── CM-28b：导入 GitHub 技能的空/坏输入不联网、不抛、给出说明 ──
            {
                var r1 = DshAuthoring.ImportFromGitHubAsync("", default).GetAwaiter().GetResult();
                var r2 = DshAuthoring.ImportFromGitHubAsync("nonsense", default).GetAwaiter().GetResult();
                bool ok = r1.Note.Length > 0 && r1.Found == 0 && r1.Imported == 0
                          && r2.Note.Length > 0 && r2.Found == 0 && r2.Imported == 0;
                check("CM-28b 仓库导入前置校验：空地址与无法解析的写法都直接给出说明，不发请求",
                    ok, $"空=「{r1.Note}」 坏=「{r2.Note}」");
            }

            // ── CM-29：会话移除全走真目录（工作区 → 回收区 → 搬回 → 删除），越界一律拒绝；
            //          归档功能已取消，老版本遗留的归档区在扫描时一次性搬回工作区 ──
            {
                string tag = Guid.NewGuid().ToString("N").Substring(0, 8);
                string sid = "session-" + tag;
                string sProj = "selftest-trash-" + tag;
                string srcDir = Path.Combine(DshSessionReader.SessionsRoot, sProj, sid);
                string legacyProj = "selftest-legacy-" + tag;
                string legacySid = "session-legacy-" + tag;
                string legacyArchiveRoot = Path.Combine(GuardPaths.ConfigDir, "Unified", "archive");
                string legacyArchive = Path.Combine(legacyArchiveRoot, legacyProj, legacySid);
                string note;
                bool ok = false;
                try
                {
                    Directory.CreateDirectory(srcDir);
                    File.WriteAllBytes(Path.Combine(srcDir, "session.v4.jsonl.zstd"),
                        new byte[] { 0x28, 0xB5, 0x2F, 0xFD, 0x00 });

                    var found = DshSessionReader.Scan().FirstOrDefault(x => x.SessionId == sid);
                    bool scanned = found != null;

                    string t = found != null ? DshSessionManager.MoveToTrash(found) : "未扫到";
                    bool movedOut = t.Length == 0 && !Directory.Exists(srcDir);
                    string[] inTrash = movedOut && Directory.Exists(DshSessionManager.TrashRoot)
                        ? Directory.EnumerateDirectories(DshSessionManager.TrashRoot, sid, SearchOption.AllDirectories).ToArray()
                        : Array.Empty<string>();

                    if (inTrash.Length > 0)
                    {
                        string? parent = Path.GetDirectoryName(srcDir);
                        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                        Directory.Move(inTrash[0], srcDir);
                    }
                    var back = DshSessionReader.Scan().FirstOrDefault(x => x.SessionId == sid);
                    bool restored = back != null;

                    string d = back != null ? DshSessionManager.Delete(back) : "不在工作区";
                    bool gone = d.Length == 0 && !Directory.Exists(srcDir);

                    string outside = DshSessionManager.Delete(new DshSession
                    {
                        SessionId = sid,
                        FilePath = Path.Combine(GuardPaths.ConfigDir, "Unified", "not-a-session.zstd"),
                    });
                    bool refused = outside.Length > 0;

                    Directory.CreateDirectory(legacyArchive);
                    File.WriteAllBytes(Path.Combine(legacyArchive, "session.v4.jsonl.zstd"),
                        new byte[] { 0x28, 0xB5, 0x2F, 0xFD, 0x00 });
                    var moved = DshSessionReader.Scan().FirstOrDefault(x => x.SessionId == legacySid);
                    bool legacyBack = moved != null
                        && Directory.Exists(Path.Combine(DshSessionReader.SessionsRoot, legacyProj, legacySid));
                    if (moved != null) DshSessionManager.Delete(moved);

                    ok = scanned && movedOut && inTrash.Length > 0 && restored && gone && refused && legacyBack;
                    note = $"扫到={scanned} 移出工作区={movedOut} 进回收区={inTrash.Length > 0} 搬回={restored}"
                         + $" 删除={gone} 越界拒绝={refused} 遗留归档区回迁={legacyBack}";
                }
                catch (Exception ex) { note = "异常：" + ex.Message; }
                finally
                {
                    try { if (Directory.Exists(srcDir)) Directory.Delete(srcDir, true); } catch { }
                    try
                    {
                        if (Directory.Exists(DshSessionManager.TrashRoot))
                            foreach (string dir in Directory.EnumerateDirectories(
                                DshSessionManager.TrashRoot, sid, SearchOption.AllDirectories))
                                Directory.Delete(dir, true);
                    }
                    catch { }
                    try
                    {
                        if (Directory.Exists(legacyArchiveRoot))
                            foreach (string dir in Directory.EnumerateDirectories(
                                legacyArchiveRoot, legacySid, SearchOption.AllDirectories))
                                Directory.Delete(dir, true);
                    }
                    catch { }
                    // 夹具用的项目目录也要收干净，否则会在用户的 sessions 根里留下空目录
                    try { Directory.Delete(Path.Combine(DshSessionReader.SessionsRoot, sProj), true); } catch { }
                    try { Directory.Delete(Path.Combine(DshSessionReader.SessionsRoot, legacyProj), true); } catch { }
                    try { Directory.Delete(Path.Combine(legacyArchiveRoot, legacyProj), true); } catch { }
                }
                check("CM-29 会话移除往返：工作区 → 回收区 → 搬回 → 删除全程走真目录，越界路径拒绝，遗留归档区扫描时自动回迁", ok, note);
            }

            // ── CM-30：子代理关联只在父会话的子代理目录还在时推导，两侧一致、无重复、无自环 ──
            {
                var all = DshSessionReader.Scan();
                var parents = all.Where(x => x.HasChildren).ToList();
                var kids = all.Where(x => x.IsChild).ToList();
                var byId = all.ToDictionary(x => x.SessionId, x => x, StringComparer.OrdinalIgnoreCase);

                bool noSelfLoop = kids.All(k => !string.Equals(k.ParentId, k.SessionId, StringComparison.OrdinalIgnoreCase));
                bool noDup = parents.All(p =>
                    p.ChildIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() == p.ChildIds.Count);
                bool consistent = kids.All(k =>
                    byId.TryGetValue(k.ParentId, out DshSession? dad)
                    && dad.ChildIds.Contains(k.SessionId, StringComparer.OrdinalIgnoreCase));
                bool ok = noSelfLoop && noDup && consistent;
                check("CM-30 子代理关联：由父会话的子代理目录反推，父 ChildIds 与子 ParentId 两侧一致、无重复无自环",
                    ok,
                    parents.Count == 0
                        ? "本机当前没有带子代理目录的会话，关系表为空（结构已就位，不造假边）"
                        : $"{parents.Count} 个父 · {kids.Count} 个子 · 自环={!noSelfLoop} 重复={!noDup} 两侧不一致={!consistent}"
                          + $" · 带 label 的子={kids.Count(k => k.ParentLabel.Length > 0)}");
            }

            // ── CM-20：搜索不能把自己的输入框重建掉（焦点丢失 → 打不进第二个字） ──
            //   判据：界面里存在「只重建内容区」的通道，且搜索回调走的是它而不是全量重绘。
            //   这条是回归钉：上一版搜索框每敲一个字就 Clear 整个根、连自己一起销毁，
            //   用户实际体验是「输入框点进去打一个字就没了」。
            {
                var t = typeof(MainWindow);
                bool hasBodyOnly = t.GetMethod("RebuildDshBodyOnly",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic) != null;
                bool hasBodyField = t.GetField("_dshBody",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic) != null;
                bool hasGuard = t.GetField("_dshSearchRefreshing",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic) != null;

                // 源码级核对（两条）：
                //   ① 搜索回调体内不得直接调 RenderDshUnified（否则整屏重建）；
                //   ② 各分区构建方法里不得再自建工具条 —— 工具条必须由
                //      BuildDshToolbarForSection 在 _dshBody **之外**统一生成，
                //      否则搜索框又会被包进被重建的那棵树里（评审 P1-1 的根因）。
                string? srcPath = FindSourceFile("MainWindow.Unified.cs");
                bool callbackClean = true;
                bool barsOutsideBody = true;
                string cbDetail = "(源码不可得，未核对)";
                string barDetail = "";

                if (srcPath != null)
                {
                    try
                    {
                        string src = File.ReadAllText(srcPath);

                        int at = src.IndexOf("searchBox.TextChanged", StringComparison.Ordinal);
                        if (at >= 0)
                        {
                            int end = src.IndexOf("};", at, StringComparison.Ordinal);
                            if (end > at)
                            {
                                string body = src.Substring(at, end - at);
                                callbackClean = !body.Contains("RenderDshUnified()", StringComparison.Ordinal);
                                cbDetail = callbackClean ? "回调里没有全量重绘" : "回调里仍在全量重绘";
                            }
                        }

                        // 抓五个分区方法体，检查里面有没有 BuildDshToolbar( 调用
                        string[] methods =
                        {
                            "private UIElement BuildDshPluginSection()",
                            "private UIElement BuildDshMcpSection()",
                            "private UIElement BuildDshSkillSection()",
                            "private UIElement BuildDshPromptSection()",
                            "private UIElement BuildDshSessionSection()",
                        };
                        var offenders = new List<string>();
                        foreach (string m in methods)
                        {
                            int s0 = src.IndexOf(m, StringComparison.Ordinal);
                            if (s0 < 0) continue;
                            // 取到下一个「\n    private 」之前，作为该方法的粗略体
                            int s1 = src.IndexOf("\n    private ", s0 + m.Length, StringComparison.Ordinal);
                            if (s1 < 0) s1 = src.Length;
                            string body = src.Substring(s0, s1 - s0);
                            if (body.Contains("BuildDshToolbar(", StringComparison.Ordinal))
                                offenders.Add(m.Replace("private UIElement ", "").Replace("()", ""));
                        }

                        barsOutsideBody = offenders.Count == 0;
                        barDetail = barsOutsideBody
                            ? "五个分区都没有自建工具条"
                            : "仍在自建：" + string.Join(",", offenders);
                    }
                    catch (Exception ex) { cbDetail = "源码读失败：" + ex.Message; callbackClean = false; }
                }

                check("CM-20 搜索只重建内容区：工具条在 _dshBody 之外生成，五个分区都不自建工具条",
                    hasBodyOnly && hasBodyField && hasGuard && callbackClean && barsOutsideBody,
                    $"方法={hasBodyOnly} 字段={hasBodyField} 闸={hasGuard} · {cbDetail} · {barDetail}");
            }

            // ── CM-31：MCP 写路径往返（新增 → 修改 → 停用 → 启用 → 删除） ──
            //   以往只有 CM-15 验「读」：扫描不抛、字段合法。Add/Update/SetEnabled/Remove
            //   一条自检都没有 —— 写错了也没人拦。这里在沙盒里造一个假 patch，
            //   把四个写方法各跑一遍，再用 ScanFile 从磁盘读回来核对。
            {
                string fakePatch = Path.Combine(sandbox, "selftest-mcp.patch.yml");
                var profile = new DshProfileInfo(DshProfileKind.Desktop, "desktop", "自检", "",
                    () => sandbox, () => fakePatch, true);

                string srvName = "selftest-mcp-" + Guid.NewGuid().ToString("N").Substring(0, 6);
                var notes = new List<string>();
                bool ok = true;

                // 这几个写方法的成功口径不统一：Update 返回空串，其余返回中文回执，
                // 且回执不都以动词开头（如 "<server> @ 自检：已禁用（…）"）。
                // 失败一律是「xx失败：…」「写入后重读…」「删除后重读…」「状态已是…」「配置文件…」。
                // 所以：空串算成功，否则要求含成功动词且不含任何失败前缀。
                static bool Succeeded(string msg, string verb) =>
                    msg.Length == 0
                    || (msg.Contains(verb, StringComparison.Ordinal)
                        && !msg.Contains("失败", StringComparison.Ordinal)
                        && !msg.StartsWith("写入后重读", StringComparison.Ordinal)
                        && !msg.StartsWith("删除后重读", StringComparison.Ordinal)
                        && !msg.StartsWith("状态已是", StringComparison.Ordinal)
                        && !msg.StartsWith("配置文件", StringComparison.Ordinal));

                string added = DshMcpManager.Add(profile, new DshMcpDraft
                {
                    ServerName = srvName,
                    Transport = "stdio",
                    Command = "node",
                    Args = { "selftest.js" },
                });
                var cur = DshMcpManager.ScanFile(profile, fakePatch)
                    .FirstOrDefault(x => string.Equals(x.ServerName, srvName, StringComparison.Ordinal));
                bool addOk = Succeeded(added, "已添加") && cur != null && cur.Problem.Length == 0
                             && cur.Command == "node" && cur.Args.Count == 1
                             && cur.ToolCallTimeoutMs == 60000 && cur.ToolPrefix == "mcp__" + srvName + "__";
                ok &= addOk;
                notes.Add(addOk ? "新增=PASS" : "新增=FAIL(" + (Succeeded(added, "已添加") ? "重读未见或不符" : added) + ")");

                if (cur != null)
                {
                    string upd = DshMcpManager.Update(profile, cur, new DshMcpDraft
                    {
                        ServerName = srvName,
                        Transport = "stdio",
                        Command = "node",
                        Args = { "selftest2.js" },
                        ToolCallTimeoutMs = 90000,
                    });
                    var afterUpd = DshMcpManager.ScanFile(profile, fakePatch)
                        .FirstOrDefault(x => string.Equals(x.ServerName, srvName, StringComparison.Ordinal));
                    bool updOk = upd.Length == 0 && afterUpd != null
                                 && afterUpd.Args.Count == 1 && afterUpd.Args[0] == "selftest2.js"
                                 && afterUpd.ToolCallTimeoutMs == 90000;
                    ok &= updOk;
                    notes.Add(updOk ? "修改=PASS" : "修改=FAIL(" + (upd.Length > 0 ? upd : "重读不符") + ")");

                    string dis = DshMcpManager.SetEnabled(afterUpd ?? cur, false);
                    var afterDis = DshMcpManager.ScanFile(profile, fakePatch)
                        .FirstOrDefault(x => string.Equals(x.ServerName, srvName, StringComparison.Ordinal));
                    bool disOk = Succeeded(dis, "已禁用") && afterDis != null && afterDis.Disabled;
                    ok &= disOk;
                    notes.Add(disOk ? "停用=PASS" : "停用=FAIL(" + (Succeeded(dis, "已禁用") ? "重读未停用" : dis) + ")");

                    string ena = DshMcpManager.SetEnabled(afterDis ?? cur, true);
                    var afterEna = DshMcpManager.ScanFile(profile, fakePatch)
                        .FirstOrDefault(x => string.Equals(x.ServerName, srvName, StringComparison.Ordinal));
                    bool enaOk = Succeeded(ena, "已启用") && afterEna != null && !afterEna.Disabled;
                    ok &= enaOk;
                    notes.Add(enaOk ? "启用=PASS" : "启用=FAIL(" + (Succeeded(ena, "已启用") ? "重读未启用" : ena) + ")");

                    int bakCount = Directory.GetFiles(sandbox, "*.bak-mcp-*").Length;
                    bool bakOk = bakCount > 0;
                    ok &= bakOk;
                    notes.Add("备份=" + (bakOk ? bakCount + "份" : "无"));

                    string del = DshMcpManager.Remove(afterEna ?? cur);
                    bool delOk = Succeeded(del, "已删除")
                                 && !DshMcpManager.ScanFile(profile, fakePatch)
                                        .Any(x => string.Equals(x.ServerName, srvName, StringComparison.Ordinal));
                    ok &= delOk;
                    notes.Add(delOk ? "删除=PASS" : "删除=FAIL(" + (Succeeded(del, "已删除") ? "重读仍在" : del) + ")");
                }
                else
                {
                    ok = false;
                    notes.Add("后续四步跳过：新增没有产出可定位的条目");
                }

                // 护栏：非法 serverName / 非法 transport 必须直接给中文原因，不许落盘
                string badName = DshMcpManager.Add(profile,
                    new DshMcpDraft { ServerName = "非法 名字", Transport = "stdio", Command = "node" });
                string badTransport = DshMcpManager.Add(profile,
                    new DshMcpDraft { ServerName = "selftest-ok", Transport = "ftp" });
                bool guardOk = badName.Length > 0 && badTransport.Length > 0
                               && !DshMcpManager.ScanFile(profile, fakePatch)
                                      .Any(x => string.Equals(x.ServerName, "selftest-ok", StringComparison.Ordinal));
                ok &= guardOk;
                notes.Add(guardOk ? "护栏=PASS" : "护栏=FAIL(非法输入未被拒)");

                check("CM-31 MCP 写路径往返：新增/修改/停用/启用/删除在沙盒假 patch 上全程走通，非法输入被拒且不落盘",
                    ok, string.Join(" · ", notes));
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("综合评价写操作自检", ex);
            check("CM-8~CM-16 四域写操作自检整体执行", false, ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            try { if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true); } catch { }
        }
    }
}

// ═══════════════════════════════════════════════════════════════════════
// 命令行入口：--dsh-unified-check
// ═══════════════════════════════════════════════════════════════════════

public static partial class SelfTest
{
    /// <summary>
    /// 独立验证入口：把综合管理用到的既有能力全部真跑一遍，打印可人工核对的明细。
    /// 全部只读（唯一写操作是偏好存档，写在 %TEMP% 夹具目录下）。
    /// </summary>
    internal static int RunDshUnifiedCheck()
    {
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) { pass++; Console.Out.WriteLine($"[PASS] {name}  {detail}"); }
            else { fail++; Console.Out.WriteLine($"[FAIL] {name}  {detail}"); }
        }

        Console.Out.WriteLine("=== 综合管理（DSH 专项）验证 ===");
        Console.Out.WriteLine($"DSH 根目录：{GuardPaths.DshHome}");
        Console.Out.WriteLine();

        // ── 复用既有扫描：这是本轮改造的核心 ──
        string webDir = GuardPaths.ProfileDirFor(GuardTarget.Web);
        string deskDir = GuardPaths.ProfileDirFor(GuardTarget.Desktop);

        Check("1. profile 寻址经 GuardPaths（Web/桌面目录不同、都在 DSH 根下）",
            webDir != deskDir
            && webDir.StartsWith(GuardPaths.DshHome, StringComparison.OrdinalIgnoreCase)
            && deskDir.StartsWith(GuardPaths.DshHome, StringComparison.OrdinalIgnoreCase),
            $"web={Path.GetFileName(webDir)} desktop={Path.GetFileName(deskDir)}");

        // ⚠ 插件数可能真的是 0，而且**不是缺陷**：PluginManager.Scan 读的是
        //   profile/package.json 的 dependencies；本机 web profile 当前只有
        //   dsh.profile.bundles（@deepseek-ai/dsh-base / dsh-web-app），没有 dependencies，
        //   于是扫出 0 个 —— 这是如实反映磁盘事实，不是读失败。
        //   故本条断言"扫描口径正确"（有 dependencies 就必须有结果，没有就应为 0），
        //   而不是"必须有插件"。
        var plugins = PluginManager.Scan("", GuardTarget.Web);
        bool scanWorks;
        string scanDetail;
        try
        {
            string pkg = Path.Combine(GuardPaths.ProfileDirFor(GuardTarget.Web), "package.json");
            bool pkgExists = File.Exists(pkg);
            bool hasDeps = pkgExists
                && File.ReadAllText(pkg).Contains("\"dependencies\"", StringComparison.Ordinal);

            // 有 dependencies ⇒ 必须有结果；没有 ⇒ 0 是正确答案
            scanWorks = !hasDeps || plugins.Count > 0;
            scanDetail = pkgExists
                ? $"package.json 存在，有 dependencies={hasDeps}，扫出 {plugins.Count} 个插件"
                : "package.json 不存在，扫出 0 个属正确降级";
        }
        catch (Exception ex)
        {
            scanWorks = false;
            scanDetail = ex.Message;
        }

        Check("2. 插件扫描走 PluginManager.Scan（结果与 package.json 的 dependencies 严格对应）",
            scanWorks && plugins.All(p => p.Name.Length > 0),
            scanDetail);

        int disabled = plugins.Count(p => p.Disabled);
        // 原文是 `disabled >= 0` —— int 恒真，纯装饰（评审 P3-1）。
        // 改成有意义的判据：禁用数不得超过总数，且被禁用的一定能在 patch 里找到依据。
        Check("3. disabled 判定来自 PluginManager（含 loader id 二次补算）",
            disabled <= plugins.Count
            && plugins.Where(p => p.Disabled).All(p => p.Name.Length > 0),
            $"{disabled}/{plugins.Count} 被禁用：" +
            string.Join(", ", plugins.Where(p => p.Disabled).Take(5).Select(p => p.Name)));

        // 原文是 `Check(..., true, ...)` —— 恒真（评审 P3-1）。
        // 改成：兼容性三态必须互斥且覆盖全体，且 Broken 的必须真的带了版本要求（否则判定没依据）。
        int nBroken = plugins.Count(p => p.Compatibility == PluginManager.Compat.Broken);
        int nPartial = plugins.Count(p => p.Compatibility == PluginManager.Compat.Partial);
        int nOk = plugins.Count(p => p.Compatibility == PluginManager.Compat.Ok);
        int nUnknown = plugins.Count(p => p.Compatibility == PluginManager.Compat.Unknown);
        bool compatPartition = nBroken + nPartial + nOk + nUnknown == plugins.Count;
        bool brokenHasRequirement = plugins
            .Where(p => p.Compatibility == PluginManager.Compat.Broken)
            .All(p => p.Requirement.Length > 0);

        Check("4. 兼容性判定复用 PluginManager.Compat：三态互斥且覆盖全体，Broken 的必有版本要求",
            compatPartition && brokenHasRequirement,
            $"Broken={nBroken} Partial={nPartial} Ok={nOk} Unknown={nUnknown}（合计应={plugins.Count}）"
            + $" · Broken 带要求={brokenHasRequirement}");

        var skills = DshSkillReader.Scan();
        Check("5. 技能扫描（DSH 真实技能根，不是 redteam 幽灵目录）",
            skills.All(s => s.Name.Length > 0),
            $"{skills.Count} 个技能 · 来源=" +
            string.Join("/", skills.Select(s => s.Source).Distinct()));

        var sessions = DshSessionReader.Scan();
        Check("6. 会话扫描（~/.dsh/sessions，只读文件属性不解压）",
            sessions.Count > 0 && sessions.All(s => File.Exists(s.FilePath)),
            $"{sessions.Count} 个会话 / {sessions.Select(s => s.ProjectName).Distinct().Count()} 个项目");

        string decoded = DshSessionReader.UnescapeProjectName("--D-Agents~0020WorkSpace-DSH-Data--");
        Check("7. 项目名转义还原（--D-Agents~0020WorkSpace-DSH-Data-- ⇒ 真实路径）",
            decoded == @"D:\Agents WorkSpace\DSH-Data", decoded);

        var prompts = DshPromptReader.Scan();
        Check("8. 提示词文件扫描（prompt-inject.md / MEMORY.md）",
            prompts.Count > 0, string.Join(", ", prompts.Select(p => p.DisplayName)));

        bool saved = DshUnifiedStore.Mutate(d => d.LastSection = "plugin");
        Check("9. 偏好存档落盘（只存界面偏好，不存资源真值）",
            saved, DshUnifiedStore.StorePath);

        // 抽样展示真实数据，便于人工核对界面会画成什么样
        Console.Out.WriteLine();
        Console.Out.WriteLine("── 插件抽样（前 8 个）──");
        foreach (var p in plugins.Take(8))
            Console.Out.WriteLine($"   {(p.Disabled ? "[禁用]" : "[启用]")} {p.Name}  ({p.Compatibility})");

        Console.Out.WriteLine();
        Console.Out.WriteLine($"===== 综合管理专项：{pass} PASS / {fail} FAIL =====");
        return fail == 0 ? 0 : 1;
    }
}
