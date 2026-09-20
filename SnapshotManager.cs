using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace DSHGuard;

/// <summary>
/// 守护壳自带的快照功能（不依赖任何 DSH 插件）。
///
/// 仓库布局（本程序自己写）:
///   &lt;程序目录&gt;\Snapshots\&lt;id&gt;\
///     manifest.json  — id/kind/reason/timeUtc/files[{name,size,sha256,target,skipped,skipReason}]
///     profile-*      — 对应 &lt;ProfileDir&gt;\&lt;去掉 profile- 前缀&gt;
///     home-*         — 对应 &lt;~/.dsh&gt;\&lt;去掉 home- 前缀&gt;
///
/// 旧 undo 插件仓库（~/.dsh/undo-snapshots）只读列出、可显式导入，不再作为本程序的写目标。
///
/// 安全策略:
///   1. 凭据文件不采集（快照落明文等于泄密），在清单里记为「跳过」；
///   2. 回滚**先自动存一份「回滚前」快照**（用户的反悔素材，配额 PreRestoreKeep），再按选定快照覆盖；
///   3. 只回滚映射表中已知的目标文件，未知名称只报告不执行；
///   4. 还原目标必须落在 ProfileDir / DshHome 之下（清单里的 Target 是**当时那台机器**的绝对路径，
///      换过 profile 目录后就会指向别处），越界记 ❌ 并跳过该文件，不中止整次回滚；
///   5. 覆盖前比对清单里的 sha256：不符（快照被改动/损坏）记 ❌ 并跳过；清单没记哈希的老快照放行。
/// </summary>
public static class SnapshotManager
{
    // ══════════ 路径 ══════════
    public static string DshHome => GuardPaths.DshHome;
    public static string ProfileDir => GuardPaths.ProfileDir;
    /// <summary>快照仓库（程序目录下，完全自建，不读取任何第三方插件的目录）。</summary>
    public static string SnapshotRoot => GuardPaths.SnapshotRoot;

    // ══════════ 类型 ══════════
    public const string KindManual = "manual";
    public const string KindAuto = "auto";
    public const string KindBeforeSwitch = "before-switch";
    public const string KindPreRestore = "pre-restore";
    /// <summary>
    /// 自动-时间：**引擎每连续运行满 1 小时**自动存一份（时间驱动，与插件动作无关）。
    /// 与「自动-插件」的区别是触发源：那一档是"动作前预存"，本档是"跑够时长就存"。
    /// </summary>
    public const string KindTimed = "timed";

    /// <summary>
    /// 快照标识（列表左侧方括号里那一段，纯函数）。
    /// 现行三类：手动 / 自动-插件 / 自动-时间。
    ///   · 手动（manual）：用户点「保存当前快照」；
    ///   · 自动-插件（auto）：插件动作前预存 —— 由**快照类型**给出，类型本身由动作决定
    ///     （见 <see cref="SnapshotPolicy.KindFor"/>：更新/卸载插件 ⇒ auto；
    ///       升级/回滚 DSH 自 2026-09-18 用户指令起**不再自动预存**，故不再产生 before-switch）；
    ///   · 自动-时间（timed）：引擎连续运行满整小时由计时器存入，见 <see cref="ShouldTakeTimedSnapshot"/>。
    /// before-switch（自动-版本）这一档已关停，但**历史遗留快照仍要显示正确**
    /// ⇒ 标签映射保留为「自动-版本（旧）」，绝不让老快照显示成空白或裸 kind。
    /// pre-restore（回滚前）由 <see cref="Restore"/> 在真正覆盖前自动产生（回退素材），保留原名称不并入三类。
    /// </summary>
    public static string KindLabel(string kind) => kind switch
    {
        KindManual => "手动",
        KindAuto => "自动-插件",
        // 时间驱动：引擎连续运行满整小时自动存，与「自动-插件」（动作前预存）是两条独立的线
        KindTimed => "自动-时间",
        // 已关停不再新增；保留映射只为让历史遗留快照显示得出来，加「（旧）」以免被当成现行策略
        KindBeforeSwitch => "自动-版本（旧）",
        KindPreRestore => "回滚前",
        _ => kind.Length > 0 ? kind : "快照"
    };

    public sealed class SnapshotFile
    {
        public string Name { get; set; } = "";
        public long Size { get; set; }
        /// <summary>还原目标绝对路径；空串表示跳过（凭据 / 未知映射）。</summary>
        public string Target { get; set; } = "";
        public string SkipReason { get; set; } = "";
        public string Hash { get; set; } = "";
        public bool Restorable => Target.Length > 0;
        public bool Skipped => Target.Length == 0;
    }

    public sealed class Snapshot
    {
        public string Id { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Reason { get; set; } = "(无说明)";
        public DateTime Time { get; set; }
        public string Dir { get; set; } = "";
        /// <summary>采集时的"未完整采集"说明（空串 = 无）。</summary>
        public string CaptureNote { get; set; } = "";
        public List<SnapshotFile> Files { get; set; } = new();

        public string LocalTime => Time.ToString("yyyy-MM-dd HH:mm:ss");
        public string KindLabel => SnapshotManager.KindLabel(Kind);
        public int RestorableCount => Files.Count(f => f.Restorable);
        /// <summary>
        /// 列表项 / 确认框里那句摘要 —— **只数快照里存了几个文件**（采集口径）。
        /// ⚠ 它与右侧详情表头「可回滚: 7 / 8 项配置」**不是同一个口径**：那一处数的是"详情页真画出来的
        ///   勾选行"（同类文件收成 1 行聚合行 ⇒ 行数 ≠ 文件数），数法见
        ///   <see cref="CheckRowCountsFor"/>。两边摆在一起会互相打架（本机同一份快照：13 个文件，
        ///   详情页只有 8 个勾选框）⇒ 列表项**不再用这个属性**，改用与详情同源的
        ///   <see cref="CheckRowCountsFor"/>（见 MainWindow.Console.cs 的 RefreshSnapshots）；
        ///   这里保留它，只为"删除快照"确认框里如实报出快照里存了几个文件。
        /// </summary>
        public string Summary => $"{Files.Count} 个文件";

        /// <summary>列表项上"回滚行数"（与右侧详情同源的显示口径；<b>纯显示，不参与回滚判定</b>）。
        /// <para><b>分母</b> = 右侧详情页真的画出来的**勾选行**数：按
        /// <see cref="BuildDisplayRows"/> 分好行，再剔掉界面层整行隐藏的那些（<see cref="IsHiddenRow"/>，
        /// 凭据行），最后加上两个开关行（「回退 DSH 版本」/「回退插件」）。
        /// <b>分子</b> = 这批行里**勾得动**的行数 —— 界面层建行时一律 <c>IsChecked = IsEnabled</c>，
        /// 所以"勾得动"就等于"有东西可回滚"；开关行算不算由
        /// <paramref name="versionRowEnabled"/> / <paramref name="pluginsRowEnabled"/> 给出
        /// （这两项要读版本记忆与当前锁文件，属于界面层上下文，故由调用方传入）。
        /// 为什么分子取"勾得动"而不是"勾没勾"：表头说的是"可回滚"，而用户把勾全取消时
        /// 「回滚勾选项」按整组回滚 ⇒ 用勾选数会在那个状态下说反话（0 / 8 却整组回滚）。</para>
        /// <para>⚠ 绝不用 <c>Snapshot.RestorableCount</c> 当这里的分子：那是**回滚用的真值**（文件口径），
        /// 语义一个字节都不动，只许在"回滚"语境里用。</para></summary>
        public (int Rollbackable, int Total) CheckRowCountsFor(bool versionRowEnabled, bool pluginsRowEnabled)
        {
            int total = 0, rollbackable = 0;
            if (versionRowEnabled) rollbackable++;
            if (pluginsRowEnabled) rollbackable++;
            total += 2;                                  // 两个开关行永远画出来
            foreach (var row in BuildDisplayRows(Files))
            {
                if (row.File != null && IsHiddenRow(row.File)) continue;   // 与界面层逐字同一条过滤
                total++;
                if (row.Kind == DisplayRowKind.Group) rollbackable += row.RestorableCount > 0 ? 1 : 0;
                else if (row.File != null && row.File.Restorable) rollbackable++;
            }
            return (rollbackable, total);
        }
        public string Display => $"{LocalTime}  [{KindLabel}]  {Reason}";
    }

    // ══════════ 采集清单 ══════════
    /// <summary>要采集的文件：快照里的名字 → 实际路径。凭据不在其中（见 SkippedFiles）。</summary>
    private static List<(string Name, string Target)> CaptureList()
    {
        var list = new List<(string, string)>
        {
            ("profile-package.json", Path.Combine(ProfileDir, "package.json")),
            ("profile-cordis.patch.yml", Path.Combine(ProfileDir, "cordis.patch.yml")),
            ("profile-cordis.yml", Path.Combine(ProfileDir, "cordis.yml")),
            ("profile-pnpm-workspace.yaml", Path.Combine(ProfileDir, "pnpm-workspace.yaml")),
            // 锁文件是"插件真实版本"的唯一凭据（git 源解析到的 commit 也记在里面），
            // 回滚插件版本必须靠它，否则只还原 package.json 里的范围号等于没还原。
            ("profile-pnpm-lock.yaml", Path.Combine(ProfileDir, "pnpm-lock.yaml")),
            ("home-settings.yaml", Path.Combine(DshHome, "settings.yaml")),
        };
        // profile 目录里的散装脚本（如 router-global.mjs）：体积小，一并纳入
        try
        {
            foreach (var f in Directory.GetFiles(ProfileDir, "*.mjs"))
            {
                string name = "profile-" + Path.GetFileName(f);
                if (!list.Any(x => x.Item1 == name)) list.Add((name, f));
            }
        }
        catch { }

        // profile\plugins\：link: 安装的本地插件实体在这里（package.json 里只有 link:./plugins/xxx 一行，
        // 不采这个目录 ⇒ 回滚后"插件在、内容回不来"）。目录可能不存在 ⇒ 跳过不报错。
        //
        // 落位约束：SnapshotFile.Target 由 manifest 的 target 字段给出（真实绝对路径，见 LoadNative），
        // ResolveTarget 只负责**老快照/无清单兜底**。正常路径下 Target 形如
        // <ProfileDir>\plugins\<包>\<文件>，在 profile 之下，越界护栏放行。
        //
        // ⚠ 旧注释在这里断言过"扁平名经 ResolveTarget 绝不会变成指向 <ProfileDir>\plugins **目录本身**
        //   的 Target"，**该断言已被证伪**（对抗性复查【中 1】）：名字恰好等于 "profile-plugins" 时，
        //   去掉前缀就拼出 plugins 目录本身；plugins 目录**不存在**时 File.Copy 会创建一个同名文件
        //   把目录名占掉，真实插件的 plugins\ 从此不可用（实测复现）。
        //   ⇒ 现已三重设防：ResolveTarget 只认已知文件后缀（见 KnownProfileSuffixes）、
        //     CheckRestoreTarget 对"允许根之下的已存在目录"判 TargetsDir、Restore 在 File.Copy 前按
        //     当时的磁盘事实再确认一次。本注释保留这段历史，免得日后有人又把它当成"不可能发生"。
        try
        {
            var (pluginItems, note) = PluginCaptureItems();
            foreach (var it in pluginItems)
                if (!list.Any(x => x.Item1 == it.Name)) list.Add(it);
            PluginsCaptureNote = note;
        }
        catch { }
        return list;
    }

    /// <summary>只登记、不采集的文件（凭据）。</summary>
    private static readonly (string Name, string Reason)[] SkippedFiles =
    {
        ("home-.credentials.yaml", "凭据不备份（避免明文落盘）")
    };

    // ══════════ plugins\ 采集上限 ══════════
    /// <summary>
    /// &lt;ProfileDir&gt;\plugins\ 的递归采集上限。取值理由（本机实测）：
    ///   本机 profile\plugins = **6 个文件 / 30,361 字节**，最大单文件 index.js 19,054 字节 ⇒
    ///   256 文件 / 8 MB 相当于实测值的 40 倍以上，正常"本地链接插件"（link:./plugins/xxx）永远够用；
    ///   同时把最坏情况钉死在"单份快照最多多 8 MB、多 256 次 File.Copy + SHA256"，
    ///   相对现有采集量（pnpm-lock 约 300 KB）是可接受的启动开销。
    ///   插件的真实依赖走 node_modules（不在 plugins\ 下），所以这里不会撞上"依赖树爆炸"。
    /// </summary>
    public const int PluginsMaxFiles = 256;
    public const long PluginsMaxBytes = 8L * 1024 * 1024;

    /// <summary>上一次采集时 plugins\ 的"未完整采集"说明（空串 = 采全了或目录不存在）。</summary>
    public static string PluginsCaptureNote { get; private set; } = "";

    /// <summary>
    /// &lt;ProfileDir&gt;\plugins —— 采集与回滚都把它当**容器（目录）**用（见 <see cref="EnumeratePluginFiles"/>、
    /// <see cref="PluginCaptureItems"/>）。单独抽出来是为了让"这是容器、不是文件"这件事只有一个定义处。
    /// </summary>
    public static string PluginsDir => Path.Combine(ProfileDir, "plugins");

    /// <summary>
    /// plugins\ 递归枚举：目录不存在 ⇒ 返回空表（**跳过，不报错**）；
    /// 跳过重解析点（junction / 符号链接）避免链接成环；按相对路径排序保证每次采集顺序稳定。
    /// </summary>
    private static List<string> EnumeratePluginFiles()
    {
        var found = new List<string>();
        string root = PluginsDir;
        if (!Directory.Exists(root)) return found;
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            try
            {
                foreach (var sub in Directory.GetDirectories(dir))
                {
                    try
                    {
                        if ((new DirectoryInfo(sub).Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    }
                    catch { continue; }
                    stack.Push(sub);
                }
                foreach (var f in Directory.GetFiles(dir)) found.Add(f);
            }
            catch { }   // 单个子目录读不动（权限/占用）⇒ 跳过它，别让整份快照失败
        }
        found.Sort(StringComparer.OrdinalIgnoreCase);
        return found;
    }

    /// <summary>相对 profile 的路径 → 扁平快照名（分隔符换成 -）：plugins\dsh-imagegen\index.js → profile-plugins-dsh-imagegen-index.js</summary>
    private static string PluginEntryName(string fileFullPath)
    {
        string rel = Path.GetRelativePath(ProfileDir, fileFullPath).Replace('\\', '-').Replace('/', '-');
        return "profile-" + rel;
    }

    /// <summary>
    /// plugins\ 的采集清单（只读目录、不写盘、不抛）。
    /// 超限时返回 TruncationNote（空串 = 采全了或目录不存在），由调用方**如实登记**，绝不静默丢。
    ///
    /// ⚠ 这里只返回 (Name, Target) 供采集用；**"未完整采集"的说明绝不作为一条 profile- 前缀的
    ///   跳过项混进 SnapshotFile 列表** —— ResolveTarget 会先清 SkipReason、再按 profile- 前缀
    ///   拼路径，"profile-plugins" 这种精确名会拼成指向 &lt;ProfileDir&gt;\plugins **目录**的 Target，
    ///   回滚时 File.Copy 往目录上写直接报错（目录不存在时更糟：会创建一个同名文件把目录名占掉）。
    ///   说明只走 manifest 字段 pluginsCaptureNote。
    /// </summary>
    public static (List<(string Name, string Target)> Items, string TruncationNote) PluginCaptureItems()
    {
        var items = new List<(string, string)>();
        string note = "";
        try
        {
            var all = EnumeratePluginFiles();
            if (all.Count == 0) return (items, "");
            long total = 0;
            int taken = 0;
            foreach (var f in all)
            {
                if (taken >= PluginsMaxFiles)
                {
                    note = $"plugins\\ 因体积过大未完整采集：文件数超上限 {PluginsMaxFiles}（共 {all.Count} 个，已采 {taken} 个）";
                    break;
                }
                long len = 0;
                try { len = new FileInfo(f).Length; } catch { }
                if (total + len > PluginsMaxBytes)
                {
                    note = $"plugins\\ 因体积过大未完整采集：总字节超上限 {PluginsMaxBytes / (1024 * 1024)} MB"
                         + $"（共 {all.Count} 个，已采 {taken} 个）";
                    break;
                }
                total += len; taken++;
                items.Add((PluginEntryName(f), f));
            }
        }
        catch (Exception ex) { Logger.LogError("SnapshotManager.PluginCaptureItems", ex); }
        return (items, note);
    }

    // ══════════ 建快照（原生） ══════════
    /// <summary>新建一份快照，返回它；失败返回 null（原因写日志）。</summary>
    public static Snapshot? Create(string kind, string reason)
    {
        try
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string id = $"{stamp}-{kind}";
            string dir = Path.Combine(SnapshotRoot, id);
            int suffix = 1;
            while (Directory.Exists(dir)) dir = Path.Combine(SnapshotRoot, $"{id}-{++suffix}");
            Directory.CreateDirectory(dir);

            var files = new List<SnapshotFile>();
            foreach (var (name, target) in CaptureList())
            {
                try
                {
                    if (!File.Exists(target)) continue;
                    string dst = Path.Combine(dir, name);
                    File.Copy(target, dst, overwrite: true);
                    files.Add(new SnapshotFile
                    {
                        Name = name,
                        Size = new FileInfo(dst).Length,
                        Target = target,
                        Hash = Hash(dst)
                    });
                }
                catch (Exception ex) { Logger.LogError($"快照采集 {name}", ex); }
            }

            foreach (var (name, why) in SkippedFiles)
                files.Add(new SnapshotFile { Name = name, SkipReason = why });

            if (PluginsCaptureNote.Length > 0) Logger.Log($"快照 {id}：{PluginsCaptureNote}");

            var meta = new
            {
                version = 1,
                id = Path.GetFileName(dir),
                kind,
                reason,
                timeUtc = DateTime.UtcNow.ToString("o"),
                app = "DSHGuard",
                appVersion = GuardVersion.Version,
                // plugins\ 因体积过大未完整采集时的说明（空串 = 采全了）。写进清单才能跨重启可见。
                pluginsCaptureNote = PluginsCaptureNote,
                // 快照点的 DSH 版本策略：回滚时据此把引擎钉回当时的版本（只还原文件是不够的）
                dshSpec = VersionMemory.Spec,
                dshPin = VersionMemory.Pin,
                files = files.Select(f => new
                {
                    name = f.Name,
                    size = f.Size,
                    sha256 = f.Hash,
                    target = f.Target,
                    skipped = f.Skipped,
                    skipReason = f.SkipReason
                })
            };
            File.WriteAllText(Path.Combine(dir, "manifest.json"),
                JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));

            Logger.Log($"快照已创建：{dir}（{files.Count(f => f.Restorable)} 个文件）");
            TrimAll();     // 每次新建后按保留策略统一清理（自动 20 / 换版本前 10 / 回滚前 10；手动不动）
            return LoadNative(dir);
        }
        catch (Exception ex)
        {
            Logger.LogError("SnapshotManager.Create", ex);
            return null;
        }
    }

    /// <summary>
    /// 自动快照保留最近 N 份，超出的删掉（N 取设置，默认 20）。
    ///
    /// ⚠ 配置不可信（<see cref="SettingsCache.KeepTrusted"/> 为假）时**一份都不裁**，直接返回 0。
    /// 这是"失败关闭"：读不出 settings.json 时 <see cref="SettingsCache.AutoSnapshotKeep"/> 会停在类型默认
    /// <c>10</c>，拿它去裁用户配的 30 份就是**静默多删 20 份、不可恢复**。宁可这一轮不裁。
    ///
    /// ⚠ 实现上必须写成 `return 0`，**绝不能**写成 <c>TrimKind(KindAuto, 0)</c>：后者会被
    /// <see cref="TrimKind"/> 开头的 `if (keep &lt; 1) keep = 1;` 夹成 1 ⇒ 变成"删到只剩 1 份"，
    /// 比本函数要防的原缺陷更狠。
    /// </summary>
    public static int TrimAuto(int keep = 0)
    {
        if (!SettingsCache.KeepTrusted) return 0;
        return TrimKind(KindAuto, keep > 0 ? keep : Math.Max(1, SettingsCache.AutoSnapshotKeep));
    }

    /// <summary>手动快照上限：满了要"删旧存新"，且必须先弹窗确认（绝不静默删用户的手动快照）。</summary>
    public const int ManualKeep = 5;
    /// <summary>换版本前 / 回滚前各保留最近这么多份（留得住最近几次，又不会无限堆积）。</summary>
    public const int BeforeSwitchKeep = 5;
    public const int PreRestoreKeep = 5;
    /// <summary>
    /// 「自动-时间」保留份数 = 5，与其它自动档（<see cref="BeforeSwitchKeep"/> /
    /// <see cref="PreRestoreKeep"/>）保持一致。
    /// 取值理由：本档的触发间隔是**每小时一份**，5 份正好覆盖"最近 5 小时"的连续运行 ——
    ///   按每天挂机 8 小时算约等于最近大半天，够回答"两三个小时前那会儿配置是什么样"；
    ///   同时把最坏增长的钉死为"每多跑 1 小时才多 1 份、总量恒定 5 份"，
    ///   长时间挂机（几天不关）也不会把快照仓库堆爆。
    /// 为什么不做成用户可配：与 <see cref="SettingsCache.AutoSnapshotKeep"/> 不同，
    ///   本档没有对应的界面输入项（设置页 XAML 里没有这个字段，加一项要动 XAML+事件+保存三处），
    ///   而它的量级天然有限（每小时 1 份），5 份已足够 —— 按"低成本才做、否则用常量"的取舍直接钉常量。
    /// </summary>
    public const int TimedKeep = 5;

    /// <summary>一小时的秒数（「自动-时间」门槛的书写基准，也供自检算门槛用）。</summary>
    public const long RunSecondsPerHour = 60L * 60L;

    /// <summary>
    /// 「自动-时间」的触发门距：引擎**每连续运行满 1 小时**存一份。
    ///
    /// 用户原话是"引擎运行时间达到 1 小时"，且明确要求**不是"启动后 1 小时只存一次"**，
    /// 而是"每满一小时一份"（长时间挂机也要有多个还原点）⇒ 门距取整小时，
    /// 第 60 / 120 / 180… 分钟各触发一次。
    /// 判定逻辑完全由本常量推出（见 <see cref="ShouldTakeTimedSnapshot"/>），
    /// 日后若要改成"每 2 小时一份"，只改这一个数就够，不必动别处。
    /// </summary>
    public const long TimedSnapshotEverySeconds = RunSecondsPerHour;

    /// <summary>
    /// 「自动-时间」累计秒数的**荒谬上限**：超过它一律按脏状态处理 ⇒ 不存快照（见 <see cref="ShouldTakeTimedSnapshot"/>）。
    /// 取值 = 10 年 = 10 × 365 × 24 × 3600 = 315,360,000 秒。
    ///
    /// 为什么取"10 年"这个值：
    ///   1. 正常路径**永远够不着** —— 需要一次连续运行满 10 年才会越界，远超本程序与任何个人机的寿命，
    ///      所以这个上限只可能在"累计秒数被写脏"（内存被改、反序列化出天文数字、系统时钟跳变、
    ///      测试直接塞极值等）时生效 —— 它守的是脏数据，不是用户行为；
    ///   2. 为什么**必须**有：判据里原有的溢出保护只护了 lastTakenAtSeconds 一侧。若 ranSeconds ≈ long.MaxValue
    ///      而 lastTakenAtSeconds = 0（本次运行还没存过），threshold 仍然只是 3600 ⇒ 判据**恒为 True**；
    ///      而 <see cref="TimedSnapshotNextMark"/> 每次只推进一个门距（3600）⇒ 每个 30 秒结算点都会存一份，
    ///      要让"已存到哪"追上 ranSeconds 需约 2.6×10¹⁵ 个结算点（≈2.9 亿年）⇒ 理论上等于永不收敛。
    ///      该情形现实不可达，属**理论完备性**问题，但修法极便宜，且主旨是"脏数据不产生副作用" ⇒ 直接判不存；
    ///   3. 为什么取"10 年"而不是 long.MaxValue：给出的是**人看得懂的荒谬线** —— 任何大于它的 ranSeconds
    ///      都只可能是脏数据，不存在"跑得久所以合法"的争议；且 10 年 × 3600 远在 long 范围内，运算本身不溢出。
    /// </summary>
    public const long TimedSnapshotMaxRunSeconds = 10L * 365 * 24 * 3600;   // = 315_360_000

    /// <summary>
    /// 「自动-时间」该不该在**此刻**存一份（纯函数：不读盘、不写盘、不碰任何状态、不抛）。
    ///
    /// 判据 = 本次运行的累计秒数是否跨过了下一个"整门槛"：
    ///   threshold = (lastTakenAtSeconds / 门距 + 1) × 门距
    ///   ⇒ 累计秒数 ≥ threshold 就存，且把"已存到哪"推进到 threshold ⇒ **同一个门槛只存一次**。
    /// 为什么必须有 lastTakenAtSeconds 这个"已存到哪"的状态：结算点是**每 30 秒**一跳的，
    ///   只看"ranSeconds ≥ 门槛"会让 3700 / 3730 / 3760… 每一跳都再存一份（一次挂机存一屋子快照）。
    /// 门槛是由"上一次存到哪"推出的，所以调用方**不需要**自己记"下一次该在几秒存"：
    ///   · lastTakenAtSeconds = 0（本次运行还没存过）⇒ 门槛 3600 ⇒ 第 3600 秒那一跳存第一份；
    ///   · 存完把 lastTakenAtSeconds 置为 3600 ⇒ 下一门槛 7200 ⇒ 第 7200 秒那一跳存第二份；
    ///   · 引擎停止后由调用方把 lastTakenAtSeconds **清零** ⇒ 重启从 0 重新数满 3600 才存。
    /// 边界一律取"**大于等于门槛就存**"：恰好 3600 秒（60 分）要存 —— 用户点名的边界。
    /// 防御：负数 / 极大值都不崩 —— 负数按 0 处理；ranSeconds 超过 <see cref="TimedSnapshotMaxRunSeconds"/>
    ///   按脏状态直接返回不存，lastTakenAtSeconds 极大时走下面的溢出保护返回不存。
    /// </summary>
    /// <param name="ranSeconds">本次运行开始至今的**累计**秒数（引擎停止后重新从 0 起算）。</param>
    /// <param name="lastTakenAtSeconds">本次运行里**上一次存快照时**的累计秒数（还没存过传 0）。</param>
    public static bool ShouldTakeTimedSnapshot(long ranSeconds, long lastTakenAtSeconds)
    {
        if (ranSeconds <= 0) return false;                      // 0 / 负数：没跑够，不可能存
        // 脏状态保险丝①：累计秒数到了荒谬区间（> 10 年，现实不可达，见常量注释）⇒ 直接判不存。
        // 只护 done 一侧不够：ran≈long.MaxValue 且 last=0 时 threshold 仍是 3600 ⇒ 判据恒 True，
        // 而"已存到哪"每次只推进 3600 ⇒ 每个 30 秒结算点都存一份（要追上 ran 约需 2.9 亿年）。
        // 取 > 而非 >=：恰好等于上限仍走正常逻辑（最多多存 1 份，ran 再增长即越界），正常区间行为一字不改。
        if (ranSeconds > TimedSnapshotMaxRunSeconds) return false;
        long step = TimedSnapshotEverySeconds > 0 ? TimedSnapshotEverySeconds : RunSecondsPerHour;
        long done = lastTakenAtSeconds > 0 ? lastTakenAtSeconds : 0;   // 负数按"没存过"处理，不崩
        // 算出下一个门槛。done 极大（脏状态）时 baseDone 可能顶到 long.MaxValue 附近 ⇒ 用减法判溢出
        long baseDone = done / step * step;
        if (baseDone > long.MaxValue - step) return false;      // 溢出保护：这种脏状态下不存，绝不抛
        long threshold = baseDone + step;
        return ranSeconds >= threshold;
    }

    /// <summary>
    /// 存完一份「自动-时间」之后，"已存到哪"该推进到几秒（纯函数，与判据同源）。
    /// 返回上一次存快照所跨过的那个门槛；调用方把它记进"已存到哪"，于是同一门槛不会重复触发。
    /// 脏状态保险丝②（与 <see cref="ShouldTakeTimedSnapshot"/> 的护栏配对）：
    ///   ranSeconds 已到荒谬区间时，不再"只推进一个门距"，而是**直接把 last 推到 ranSeconds** ——
    ///   这样即便调用方漏过了判据（或日后有人改了判据、或用别处调用），也只会落下这一份，
    ///   下一跳 ranSeconds 已越过 <see cref="TimedSnapshotMaxRunSeconds"/> ⇒ 判据必然 False ⇒ 不会每 30 秒连发。
    ///   "推进到 ranSeconds"在语义上也是对的：lastTakenAtSeconds 的定义就是"上一次存快照时的累计秒数"。
    ///   正常区间（≤ 上限）一个字节的行为都不变 —— 仍返回跨过的整门槛，保证 60/120/180 分各存一次、落点整齐。
    /// </summary>
    public static long TimedSnapshotNextMark(long ranSeconds, long lastTakenAtSeconds)
    {
        long step = TimedSnapshotEverySeconds > 0 ? TimedSnapshotEverySeconds : RunSecondsPerHour;
        long done = lastTakenAtSeconds > 0 ? lastTakenAtSeconds : 0;
        if (ranSeconds > TimedSnapshotMaxRunSeconds) return ranSeconds;   // 脏状态：一次性推平，绝不连发
        long baseDone = done / step * step;
        if (baseDone > long.MaxValue - step) return baseDone;   // 溢出保护
        return baseDone + step;
    }

    /// <summary>手动快照已超出的份数（0 = 还有空位，可直接存）。</summary>
    public static int ManualOverflow()
        => Math.Max(0, ListNative().Count(s => s.Kind == KindManual) - ManualKeep + 1);

    /// <summary>最旧的若干份手动快照（用于弹窗里告知将删哪些）。</summary>
    public static List<Snapshot> OldestManual(int count)
    {
        try
        {
            return ListNative()
                .Where(s => s.Kind == KindManual)
                .OrderBy(s => s.Time)
                .Take(Math.Max(0, count))
                .ToList();
        }
        catch { return new List<Snapshot>(); }
    }

    /// <summary>腾位置：把手动快照裁到「上限 - 1」份（删最旧的），返回删除数量。</summary>
    public static int TrimManualToMakeRoom()
        => TrimKind(KindManual, Math.Max(1, ManualKeep - 1));

    /// <summary>按种类裁剪到保留份数，超出的从最旧起删；手动保存的快照**永不**自动删除。返回删除数量。</summary>
    public static int TrimKind(string kind, int keep)
    {
        int removed = 0;
        try
        {
            if (keep < 1) keep = 1;
            var olds = ListNative()
                .Where(s => s.Kind == kind)
                .OrderByDescending(s => s.Time)
                .Skip(keep)
                .ToList();
            foreach (var s in olds)
                if (Delete(s)) removed++;
            if (removed > 0) Logger.Log($"快照保留策略：{kind} 超过 {keep} 份，清理了 {removed} 份");
        }
        catch (Exception ex) { Logger.LogError($"SnapshotManager.TrimKind({kind})", ex); }
        return removed;
    }

    /// <summary>
    /// 一次性执行全部保留策略：自动 20 份、换版本前 10 份、回滚前 10 份、自动-时间 5 份；
    /// 手动保存的从不自动删除。启动时与每次新建快照后各跑一次。
    /// </summary>
    public static int TrimAll()
    {
        int removed = TrimAuto();
        removed += TrimKind(KindBeforeSwitch, BeforeSwitchKeep);
        removed += TrimKind(KindPreRestore, PreRestoreKeep);
        // 自动-时间必须一起裁：它每小时自己冒一份，不裁就是无上限增长
        removed += TrimKind(KindTimed, TimedKeep);
        return removed;
    }

    /// <summary>
    /// 保留策略的人话说明（界面与日志共用一份口径）。
    /// 只列**现行**档位：「自动-版本」已关停不再新增，故不入此串（历史遗留快照的清理仍由
    /// <see cref="TrimAll"/> 内的 <see cref="BeforeSwitchKeep"/> 兜底，只是不再对外报口径）。
    /// </summary>
    public static string RetentionText =>
        $"自动-插件 {SettingsCache.AutoSnapshotKeep} 份 · 自动-时间 {TimedKeep} 份（每连续运行满 1 小时一份）"
        + $" · 手动最多 {ManualKeep} 份（满了先问再删旧存新）";

    /// <summary>自动快照保留份数（由主窗口在启动时写入设置值）。</summary>
    public static class SettingsCache
    {
        public static int AutoSnapshotKeep { get; set; } = 10;

        /// <summary>
        /// <see cref="AutoSnapshotKeep"/> 这个数**是否可信** —— 即它是不是用户在 settings.json 里写的那个值。
        ///
        /// 默认 <c>false</c>（不可信），由两个启动接线点按 <c>SettingsManager.LastLoadTrusted</c> 写入。
        /// 为什么默认取 false：这是一个"读不出来就按默认值删用户数据"的下游，默认值必须倒向**不动作**那一侧。
        ///
        /// ⚠ 为假时 <see cref="TrimAuto"/> **一份都不裁**（返回 0）。这就是"失败关闭"：
        /// 读不出配置时 <see cref="AutoSnapshotKeep"/> 会停在类型默认 <c>10</c>，拿它去裁用户配的 30 份
        /// 就是静默多删 20 份、不可恢复。宁可这一轮不裁。
        ///
        /// ⚠ 本闸门**只管「自动-插件」这一档**，其余三档（自动-时间 / 换版本前 / 回滚前）不受影响：
        /// 它们的份数是 <see cref="TimedKeep"/> / <see cref="BeforeSwitchKeep"/> / <see cref="PreRestoreKeep"/>
        /// 这些**编译期常量**，根本不来自 settings.json ⇒ 设置读不出来也污染不到它们，再停它们只是白停。
        /// 换句话说：**"少裁"只发生在真正依赖配置的那一档上**。
        /// </summary>
        public static bool KeepTrusted { get; set; } = false;
    }

    // ══════════ 列快照 ══════════
    /// <summary>列出全部快照（只读程序目录下自己的仓库），按时间倒序。</summary>
    public static List<Snapshot> ListSnapshots() => ListNative();

    /// <summary>列程序自己的快照仓库。</summary>
    public static List<Snapshot> ListNative()
    {
        var list = new List<Snapshot>();
        try
        {
            if (!Directory.Exists(SnapshotRoot)) return list;
            foreach (var sub in Directory.GetDirectories(SnapshotRoot))
            {
                var s = LoadNative(sub);
                if (s != null) list.Add(s);
            }
        }
        catch (Exception ex) { Logger.LogError("SnapshotManager.ListNative", ex); }
        return list.OrderByDescending(s => s.Time).ToList();
    }

    /// <summary>
    /// 归一化 pnpm-lock 里的"已解析版本"：剥掉同伴依赖后缀，只留核心版本号（纯函数，不读盘、不抛）。
    ///
    /// 为什么必须归一化：pnpm v9 的锁文件对**同一个已解析版本**会按当时解析到的同伴依赖写后缀，
    /// 形如 <c>0.4.7(@deepseek-ai/schemastery@3.18.2)</c>。同一份包在两份锁文件里后缀不同
    /// （快照当时没解析出同伴、之后又解析到了，或反之）会被"整串比较"判成版本变了
    /// ⇒ 回滚确认框把**根本没变的插件**也数进去（现场：只升级 1 个插件，却显示 7 个）。
    ///
    /// 口径：
    ///   · 没有 <c>(</c> ⇒ 原样返回（纯版本号、git 源、<c>file:</c> / <c>link:</c> 写法都走这条）；
    ///   · 有 <c>(</c> 且括号前**以数字开头** ⇒ 只留括号前那段（<c>0.4.7(a@1)</c> → <c>0.4.7</c>）；
    ///   · 括号前不是版本号形态（git 源解析出的 URL / 提交号等）⇒ 原样返回，仍按整串比较 ——
    ///     绝不因为"长得像后缀"就破坏 git 源的判定依据；
    ///   · 空串仍为空串。
    /// </summary>
    public static string NormalizeLockVersion(string? v)
    {
        string s = (v ?? "").Trim();
        if (s.Length == 0) return "";
        int at = s.IndexOf('(');
        if (at <= 0) return s;
        string core = s.Substring(0, at).Trim();
        return core.Length > 0 && char.IsAsciiDigit(core[0]) ? core : s;
    }

    /// <summary>
    /// 两份锁文件之间**插件层面**的变化：返回"名字变了或只在一侧"的插件名。
    /// 只读 `importers:` 下 `dependencies:` 那一段（每行形如 `      '@scope/name':` + `specifier:`/`version:`），
    /// 所以给出来的是**插件名**，界面可以据此写「回滚 N 个插件」与悬停里的名单。
    /// 比"按行数差"准确得多（以前显示"约 77 处差异"，用户完全不知道会动几个插件）。
    ///
    /// **没法比时返回空表**：两份里只要有一份没有 importers 依赖段（老快照没记锁文件、文件半截等），
    /// 就不冒充"没有变化"。界面用 <see cref="HasImporterDeps"/> 把这种情况单独说清楚。
    /// </summary>
    public static List<string> ChangedPlugins(string? oldLock, string? newLock)
    {
        var changed = new List<string>();
        try
        {
            var a = DepVersions(oldLock);
            var b = DepVersions(newLock);
            // 任一侧根本没有可比的依赖段（缺段 / 空文本 / 文件半截）⇒ 无法判断，返回空表
            if (a.Count == 0 || b.Count == 0) return changed;
            foreach (var kv in a)
            {
                if (!b.TryGetValue(kv.Key, out var v2)) { changed.Add(kv.Key); continue; }   // 快照有、现在没有 ⇒ 会被装回来
                // 按**归一化后**的版本比：同伴依赖后缀 `(...)` 的差异不算版本变化（见 NormalizeLockVersion）
                if (!string.Equals(NormalizeLockVersion(kv.Value), NormalizeLockVersion(v2), StringComparison.OrdinalIgnoreCase))
                    changed.Add(kv.Key);
            }
            foreach (var kv in b)
                if (!a.ContainsKey(kv.Key)) changed.Add(kv.Key);                             // 现在有、快照没有 ⇒ 会被退掉
            changed.Sort(StringComparer.OrdinalIgnoreCase);
        }
        catch { }
        return changed;
    }

    /// <summary>
    /// 这份锁文件文本里到底有没有"可比的插件清单"（`importers:` 下的依赖段）。
    /// 用于把"确实没变化"与"根本无法比对"分开说：前者空表且有清单，后者空表且没有清单。
    /// 纯文本判断、不读盘、不抛（坏文本一律按"没有清单"处理）。
    /// </summary>
    public static bool HasImporterDeps(string? lockText)
    {
        try { return DepVersions(lockText).Count > 0; }
        catch { return false; }
    }

    /// <summary>
    /// 解析锁文件里"直接依赖"的名字 → 已解析版本。
    ///
    /// **按结构取值，不按固定缩进**（这正是原先的 bug：老实现写死"依赖名行缩进 = 4 格"，
    /// 而真实 pnpm-lock 是 `importers:`(0) → `  .:`(2) → `    dependencies:`(4) → `      '@scope/name':`(6)，
    /// 于是恒解析出空表 ⇒ 所有快照都显示"不涉及插件"）：
    ///   1. 只在 `importers:` 段里找；出现 `packages:` 或缩进回退到 0 即结束；
    ///   2. 只认 `dependencies:`；`devDependencies:` 不算（见下面的取舍说明）；
    ///   3. 依赖名是"比 `dependencies:` 缩进更深"的那一行（`'@scope/name':` / `"name":` / `name:` 都认），
    ///      名字必须不含空格（这样 `version: 1.2.3` 这类不会被误当名字）；
    ///   4. 名字行之后**更深缩进**里的 `version:` 就是已解析版本（没有就记空串）；
    ///   5. 遇到缩进回退（回到 importer 级 / 依赖名同级）即结束当前依赖的收集。
    ///
    /// 取舍：只收 `dependencies`、不收 `devDependencies`——用户能感知的是"装了哪些插件"，
    /// 开发依赖属于构建期；把两类混在一起会让"回滚 N 个插件"的数字虚高。
    /// 代价是某个插件若从 dependencies 挪到 devDependencies（反之亦然），不会被认成变化。
    /// </summary>
    private static Dictionary<string, string> DepVersions(string? lockText)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (string.IsNullOrWhiteSpace(lockText)) return map;
            var lines = lockText!.Split('\n');

            bool inImporters = false;   // 已进入 importers: 段
            bool inDeps = false;        // 正处在某个 dependencies: 段内
            int depsIndent = -1;        // dependencies: 那一行的缩进
            string? cur = null;         // 当前依赖名
            int curIndent = -1;         // 当前依赖名那一行的缩进
            bool curGotVersion = false; // 当前依赖是否已经取到 version:

            foreach (var raw in lines)
            {
                string line = raw.TrimEnd();
                string t = line.Trim();
                if (t.Length == 0 || t.StartsWith("#", StringComparison.Ordinal)) continue;   // 空行/注释不打断缩进判定
                int indent = line.Length - line.TrimStart().Length;                           // 前导空格数（这里不用 Tab，按列算更稳）

                // 顶层键（缩进 0）：importers 进段，其它顶层键（packages:/settings:…）结束 importers
                if (indent == 0 && !t.StartsWith("-", StringComparison.Ordinal))
                {
                    inImporters = t.StartsWith("importers:", StringComparison.Ordinal);
                    inDeps = false; cur = null; depsIndent = -1;
                    continue;
                }
                if (!inImporters) continue;
                // 万一 importers 段里出现比它更深的 packages: 之类，也按段落结束处理
                if (t.StartsWith("packages:", StringComparison.Ordinal)) break;

                if (!inDeps)
                {
                    // dependencies:（devDependencies/optionalDependencies 不认；: 后面可能有注释）
                    if (t.StartsWith("dependencies:", StringComparison.Ordinal)) { inDeps = true; depsIndent = indent; cur = null; }
                    continue;
                }

                // ① 缩进回退到 dependencies: 同级或更浅 ⇒ 这一段结束（回到 importer 级 / 出现 devDependencies: 都走这里）
                if (indent <= depsIndent) { inDeps = false; cur = null; continue; }
                // ② 依赖名：比 dependencies: 更深、且是 `名字:` 这种键行（名字不含空格 ⇒ 不会把 `version:` 误当名字）
                if (cur == null)
                {
                    if (t.EndsWith(":", StringComparison.Ordinal))
                    {
                        string nm = t.Substring(0, t.Length - 1).Trim().Trim('\'', '"');
                        if (nm.Length > 0 && nm.IndexOf(' ') < 0)
                        {
                            cur = nm; curIndent = indent; curGotVersion = false;
                            if (!map.ContainsKey(nm)) map[nm] = "";
                        }
                    }
                    continue;
                }
                // ③ 已是当前依赖：更深缩进里的 version: 才是"已解析版本"
                if (indent > curIndent)
                {
                    if (!curGotVersion && t.StartsWith("version:", StringComparison.Ordinal))
                    {
                        // 解析出来就归一化：这个 map 的值同时用于**比较**与**展示**
                        //（界面若要写"现在是 X"，写的就是归一化后的核心版本号，不带 `(...)` 后缀）
                        map[cur] = NormalizeLockVersion(t.Substring("version:".Length).Trim().Trim('\'', '"'));
                        curGotVersion = true;
                    }
                    continue;
                }
                // ④ 缩进回到依赖名同级 ⇒ 当前依赖结束，这一行按"新依赖名"重新判定
                cur = null;
                if (t.EndsWith(":", StringComparison.Ordinal))
                {
                    string nm = t.Substring(0, t.Length - 1).Trim().Trim('\'', '"');
                    if (nm.Length > 0 && nm.IndexOf(' ') < 0)
                    {
                        cur = nm; curIndent = indent; curGotVersion = false;
                        if (!map.ContainsKey(nm)) map[nm] = "";
                    }
                }
            }
        }
        catch { }
        return map;
    }
    /// <summary>读快照清单（manifest.json）里的一个字符串字段；无法读取时返回空串。</summary>
    public static string ManifestValue(string snapDir, string key)
    {
        try
        {
            string manifest = Path.Combine(snapDir, "manifest.json");
            if (!File.Exists(manifest)) return "";
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            return doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                ? (v.GetString() ?? "") : "";
        }
        catch { return ""; }
    }

    /// <summary>
    /// 两份锁文件之间的包版本变化条数（用于回滚前的预览："会动 N 个插件版本"）。
    /// 逐行比较里含 `resolution:` / 包名行的差异，粗略但足够给出预期。
    /// </summary>
    public static int CountLockChanges(string? oldLock, string? newLock)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(oldLock) || string.IsNullOrWhiteSpace(newLock)) return 0;
            static HashSet<string> Norm2(string s)
            {
                var set = new HashSet<string>(StringComparer.Ordinal);
                foreach (var raw in s.Split('\n'))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    set.Add(line);
                }
                return set;
            }
            var a = Norm2(oldLock!);
            var b = Norm2(newLock!);
            int diff = 0;
            foreach (var x in a) if (!b.Contains(x)) diff++;
            return diff;
        }
        catch { return 0; }
    }
    private static Snapshot? LoadNative(string dir)
    {
        try
        {
            string name = Path.GetFileName(dir);
            var snap = new Snapshot { Dir = dir, Id = name, Kind = KindManual };
            string manifest = Path.Combine(dir, "manifest.json");

            if (File.Exists(manifest))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                var root = doc.RootElement;
                snap.Id = GetString(root, "id", name);
                snap.Kind = GetString(root, "kind", KindManual);
                snap.Reason = GetString(root, "reason", "(无说明)");
                snap.CaptureNote = GetString(root, "pluginsCaptureNote", "");
                string t = GetString(root, "timeUtc", "");
                snap.Time = DateTime.TryParse(t, out var parsed) ? parsed.ToLocalTime() : Directory.GetLastWriteTime(dir);
                if (root.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
                {
                    foreach (var f in files.EnumerateArray())
                    {
                        var sf = new SnapshotFile
                        {
                            Name = GetString(f, "name", ""),
                            Hash = GetString(f, "sha256", "")
                        };
                        sf.Size = f.TryGetProperty("size", out var sz) && sz.TryGetInt64(out long l) ? l : 0;
                        string target = GetString(f, "target", "");
                        string skip = GetString(f, "skipReason", "");
                        if (target.Length > 0) sf.Target = target;
                        else { sf.SkipReason = skip.Length > 0 ? skip : "已跳过"; }
                        if (sf.Name.Length > 0) snap.Files.Add(sf);
                    }
                }
            }

            if (snap.Files.Count == 0)
            {
                // 没有清单（或解析失败）：按目录里的实际文件兜底
                foreach (var f in Directory.GetFiles(dir))
                {
                    if (Path.GetFileName(f).Equals("manifest.json", StringComparison.OrdinalIgnoreCase)) continue;
                    snap.Files.Add(new SnapshotFile { Name = Path.GetFileName(f), Size = new FileInfo(f).Length });
                }
                snap.Time = snap.Time == default ? Directory.GetLastWriteTime(dir) : snap.Time;
            }

            foreach (var f in snap.Files) ResolveTarget(f);
            if (snap.Time == default) snap.Time = Directory.GetLastWriteTime(dir);
            return snap;
        }
        catch (Exception ex)
        {
            Logger.LogError($"LoadNative({dir})", ex);
            return null;
        }
    }


    private static string GetString(JsonElement el, string prop, string fallback)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? fallback) : fallback;

    /// <summary>快照文件名到中文功能描述的映射（列表只显示名称，细节见 <see cref="FriendlyDetail"/>）。</summary>
    public static string FriendlyName(string name) => name switch
    {
        "profile-package.json" => "插件清单",
        "profile-cordis.patch.yml" => "补丁层配置",
        "profile-cordis.yml" => "配置根清单",
        "profile-pnpm-workspace.yaml" => "pnpm 工作区设置",
        "home-settings.yaml" => "DSH 全局设置",
        "home-.credentials.yaml" => "凭据",
        _ => name.StartsWith("profile-", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(name.Substring("profile-".Length)) + " 脚本"
            : name
    };

    /// <summary>
    /// profile- 前缀的兜底映射**只认这些文件后缀**（收尾收紧，对抗性复查【中 1】）。
    /// <para>
    /// 为什么必须限定：采集名里的 `-` 是**目录分隔符换来的**（见 <see cref="PluginEntryName"/>），
    /// 而 `-` 本身也是合法文件名字符 ⇒ 名字与"相对路径"无法互相区分。于是名字恰好等于
    /// <c>"profile-plugins"</c> 时（既不是任何真实采集名，也永远不该被兜底映射出目标），
    /// 去掉前缀会拼出 &lt;ProfileDir&gt;\plugins —— **plugins 目录本身**。此时若该目录不存在，
    /// File.Copy 就会创建一个名为 plugins 的**文件**把目录名占掉（实测复现，真实插件的
    /// plugins\ 从此不可用）。
    /// </para>
    /// <para>
    /// 后缀白名单覆盖现行全部采集名：固定清单（package.json / cordis.patch.yml / cordis.yml /
    /// pnpm-workspace.yaml / pnpm-lock.yaml）与 ProfileDir 下的 *.mjs、plugins\ 下的 JS 实体
    /// （.js / .mjs / .cjs）；.ts/.tsx 一并放行（插件源码常见，零成本）。
    /// **只作用于 profile- 分支的兜底映射**：正常快照的 Target 由 manifest 的 target 字段给出
    /// （真实绝对路径，<see cref="ResolveTarget"/> 开头就 return），一个字节都不经过这里。
    /// </para>
    /// </summary>
    private static readonly string[] KnownProfileSuffixes =
    {
        ".json", ".yml", ".yaml", ".js", ".mjs", ".cjs", ".ts", ".tsx"
    };

    /// <summary>名字是否以某个已知文件后缀收尾（大小写不敏感；纯函数，不读盘）。</summary>
    private static bool HasKnownProfileSuffix(string name)
    {
        foreach (string suf in KnownProfileSuffixes)
            if (name.EndsWith(suf, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// 名称对应的补充说明（悬停提示里用）。
    /// </summary>
    public static string FriendlyDetail(string name) => name switch
    {
        "profile-package.json" => "插件依赖与加载项",
        "profile-cordis.patch.yml" => "插件的挂载与禁用开关",
        "home-.credentials.yaml" => "不备份，避免密钥明文落盘",
        _ => ""
    };

    /// <summary>
    /// 是不是"凭据"这一类采集名（纯函数：只看名字，不读盘、不抛）。
    ///
    /// <para>
    /// 判据只留这一份：<see cref="ResolveTarget"/> 用它标"跳过"，界面层用它把那行**整行隐藏**
    /// （用户 1.3.56 实机要求："凭据不要出现"）。两处必须同一口径，否则会出现
    /// "界面藏了一个其实可回滚的项"这类静默偏差。
    /// </para>
    /// <para>
    /// ⚠ 只回答"是不是凭据"，**不回答"该不该显示"**：调用方若要隐藏，必须自己再加
    /// <c>!f.Restorable</c> —— 万一将来凭据变成可回滚，那一项就不会被悄悄藏起来。
    /// </para>
    /// </summary>
    public static bool IsCredentialFile(SnapshotFile f)
        => f != null
           && (f.Name.Equals(".credentials.yaml", StringComparison.OrdinalIgnoreCase)
               || f.Name.Equals("home-.credentials.yaml", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 会不会被界面**整行隐藏**（纯函数）：凭据名 + 不可回滚 —— 与详情页的行过滤同一口径
    /// （见 <c>MainWindow.Console.cs</c> 的 <c>BuildFileRows</c>）。
    ///
    /// 只用于**显示口径**（表头分母）：不加这一项时，表头写着"13 个文件"而清单上只剩 12 行，
    /// 分母与看得见的列表对不上。**任何回滚判断都不许读它**。
    /// </summary>
    public static bool IsHiddenRow(SnapshotFile f) => IsCredentialFile(f) && !f!.Restorable;

    /// <summary>把快照文件名映射到还原目标；未知或不可回滚的给出原因。</summary>
    public static void ResolveTarget(SnapshotFile f)
    {
        if (f.Target.Length > 0) return;        // 原生清单里已带真实路径
        f.SkipReason = "";

        if (IsCredentialFile(f))
        {
            f.SkipReason = "凭据不备份，跳过";
            return;
        }

        if (f.Name.StartsWith("profile-", StringComparison.OrdinalIgnoreCase))
        {
            // ★ 收尾收紧（对抗性复查【中 1】）：兜底映射出来的必须是**文件**，不是目录。
            //   采集名里的 - 是目录分隔符换来的（PluginEntryName），恰恰等于 "plugins" 这种
            //   目录名时无法区分 ⇒ 用后缀白名单把"目录名"整类挡在门外（见 KnownProfileSuffixes）。
            //   不匹配就如实记"未知映射，跳过"（与孤儿名同一条口径），绝不猜一个目标去写。
            string rest = f.Name.Substring("profile-".Length);
            if (HasKnownProfileSuffix(rest))
                f.Target = Path.Combine(ProfileDir, rest);
            else
                f.SkipReason = "未知映射，跳过";
        }
        else if (f.Name.StartsWith("home-", StringComparison.OrdinalIgnoreCase))
            f.Target = Path.Combine(DshHome, f.Name.Substring("home-".Length));
        else
            f.SkipReason = "未知映射，跳过";
    }

    // ══════════ 详情页的显示分组（纯显示层） ══════════
    // 这一节**只决定"详情页上怎么摆"**，不参与采集、不参与回滚：
    //   · 没有任何 Catalog 的回滚函数读这里的值；
    //   · 分组行只影响 MainWindow 建几个勾选框，勾选框最终仍然换算回**逐文件的名字列表**
    //     （DoRestore 的 restoreNames，见 Restore() 的 restoreNames.Contains(f.Name)）。
    // ⇒ 加/改这一节**不可能**改变回滚结果。

    /// <summary>
    /// 详情页的文件大类。取值是**稳定 id**（不是画面上的中文），中文由 <see cref="CategoryLabel"/> 给，
    /// 列出顺序由 <see cref="CategoryOrder"/> 给 —— 三张表都不读盘、不抛。
    ///
    /// 为什么不直接用中文当 id：以后改文案时自检与分组逻辑不受影响（id 稳定、可断言）。
    /// </summary>
    public static class SnapshotCategory
    {
        /// <summary>插件清单（<c>profile-package.json</c>）。</summary>
        public const string PluginManifest = "plugin-manifest";
        /// <summary>补丁层配置（<c>profile-cordis.patch.yml</c>）。</summary>
        public const string PatchLayer = "patch-layer";
        /// <summary>配置根清单（<c>profile-cordis.yml</c>）。</summary>
        public const string ConfigRoot = "config-root";
        /// <summary>工作区与锁文件（workspace.yaml / pnpm-lock.yaml 及 profile 下的散装脚本）。</summary>
        public const string WorkspaceLock = "workspace-lock";
        /// <summary>全局设置（<c>home-settings.yaml</c>）。</summary>
        public const string GlobalSettings = "global-settings";
        /// <summary>plugins\ 下的插件实体文件（采集名形如 <c>profile-plugins-&lt;包&gt;-&lt;文件&gt;</c>）。</summary>
        public const string PluginFiles = "plugin-files";
        /// <summary>兜底：本函数认不出来的名字。</summary>
        public const string Other = "other";
    }

    /// <summary>
    /// ★ 大类判定的**唯一**判据（纯函数：只看名字字符串，不读盘、不抛）。
    ///
    /// 映射表（判据只留这一份，界面层只调用、不复制）：
    /// <code>
    ///   profile-package.json          → PluginManifest   插件清单
    ///   profile-cordis.patch.yml      → PatchLayer       补丁层配置
    ///   profile-cordis.yml            → ConfigRoot       配置根清单
    ///   profile-pnpm-workspace.yaml   → WorkspaceLock    工作区与锁文件
    ///   profile-pnpm-lock.yaml        → WorkspaceLock    工作区与锁文件
    ///   profile-*.mjs / *.js / ...    → WorkspaceLock    工作区与锁文件（profile 下的散装脚本，含 router-global.mjs）
    ///   home-settings.yaml            → GlobalSettings   全局设置
    ///   profile-plugins-*             → PluginFiles      插件文件（\ 换成 - 的采集名，见 PluginEntryName）
    ///   其余（home-*、未知名）        → Other            其它
    /// </code>
    ///
    /// ⚠ 判定依据就是 <see cref="CaptureList"/> / <see cref="PluginEntryName"/> 产出的**采集名**，
    ///   所以"某一项归哪一类"与"它实际是哪个文件"永远对得上，不需要第二张表。
    /// ⚠ <c>profile-plugins-*</c> 的判定必须排在"profile- 下散装脚本"之前：两者都带 profile- 前缀，
    ///   顺序反过来会把插件文件错归到工作区类里（顺序即语义，别调）。
    /// </summary>
    public static string CategoryOf(string name) => name switch
    {
        "profile-package.json" => SnapshotCategory.PluginManifest,
        "profile-cordis.patch.yml" => SnapshotCategory.PatchLayer,
        "profile-cordis.yml" => SnapshotCategory.ConfigRoot,
        "profile-pnpm-workspace.yaml" => SnapshotCategory.WorkspaceLock,
        "profile-pnpm-lock.yaml" => SnapshotCategory.WorkspaceLock,
        "home-settings.yaml" => SnapshotCategory.GlobalSettings,
        _ => name.StartsWith("profile-plugins-", StringComparison.OrdinalIgnoreCase)
            ? SnapshotCategory.PluginFiles
            : name.StartsWith("profile-", StringComparison.OrdinalIgnoreCase)
                ? SnapshotCategory.WorkspaceLock
                : SnapshotCategory.Other
    };

    /// <summary>大类 id → 画面上的中文（纯函数；界面层只调用这一处）。</summary>
    public static string CategoryLabel(string id) => id switch
    {
        SnapshotCategory.PluginManifest => "插件清单",
        SnapshotCategory.PatchLayer => "补丁层配置",
        SnapshotCategory.ConfigRoot => "配置根清单",
        SnapshotCategory.WorkspaceLock => "工作区与锁文件",
        SnapshotCategory.GlobalSettings => "全局设置",
        SnapshotCategory.PluginFiles => "插件文件",
        SnapshotCategory.Other => "其它",
        _ => "其它"
    };

    /// <summary>大类的固定列出顺序（纯函数）：固定清单在前、插件文件在后、兜底收尾。</summary>
    public static int CategoryOrder(string id) => id switch
    {
        SnapshotCategory.PluginManifest => 0,
        SnapshotCategory.PatchLayer => 1,
        SnapshotCategory.ConfigRoot => 2,
        SnapshotCategory.WorkspaceLock => 3,
        SnapshotCategory.GlobalSettings => 4,
        SnapshotCategory.PluginFiles => 5,
        _ => 6
    };

    /// <summary>详情页一行的种类：聚合行 / 逐文件行。</summary>
    public enum DisplayRowKind
    {
        /// <summary>聚合行：一个勾选框代表该大类的全部可回滚文件。</summary>
        Group,
        /// <summary>逐文件行：一个勾选框代表一个文件。</summary>
        File
    }

    /// <summary>
    /// 详情页的一行（**只读**显示结构；由 <see cref="BuildDisplayRows"/> 产出，界面层照着建控件）。
    /// <see cref="File"/> 只在 <see cref="DisplayRowKind.File"/> 行上非空。
    /// </summary>
    public sealed class SnapshotDisplayRow
    {
        /// <summary>该行所属大类（见 <see cref="SnapshotCategory"/>）。</summary>
        public string Category { get; set; } = SnapshotCategory.Other;
        /// <summary>行种类。</summary>
        public DisplayRowKind Kind { get; set; } = DisplayRowKind.File;
        /// <summary>逐文件行对应的那个文件；聚合行为 null。</summary>
        public SnapshotFile? File { get; set; }
        /// <summary>本行涉及的**全部**文件（聚合行 = 整类；逐文件行 = 就它一个）。</summary>
        public List<SnapshotFile> Files { get; set; } = new();
        /// <summary>本行里可回滚（有还原目标）的文件。</summary>
        public List<SnapshotFile> RestorableFiles { get; set; } = new();
        /// <summary>本行里不可回滚（"跳过"）的文件 —— 聚合行也要把它们留着可见。</summary>
        public List<SnapshotFile> SkippedFiles { get; set; } = new();
        /// <summary>全部文件的大小合计（含"跳过"的项，它们通常就是 0）。</summary>
        public long TotalBytes { get; set; }
        /// <summary>可回滚文件的数量（= <see cref="RestorableFiles"/>.Count）。</summary>
        public int RestorableCount { get; set; }
        /// <summary>不可回滚（"跳过"）文件的数量。</summary>
        public int SkippedCount { get; set; }
    }

    /// <summary>
    /// 聚合行的阈值：**少于这么多件可回滚文件的类不聚合**，保持单行 ——
    /// 免得为 1 个文件也套一层（多一层"整类勾选"只增加理解成本）。
    /// 取 2 的理由：诉求是"回滚时直接丢到某个大类里"（**同类收进大类**）⇒ 2 件的类也要聚合；
    /// 按 3 时「工作区与锁文件」（pnpm 工作区设置 + pnpm-lock 脚本）恰好差一件而漏掉，白多一行。
    /// 本机实测（同一份 13 个文件的快照）：阈值 3 ⇒ 详情页 8 行；阈值 2 ⇒ 该桶收成 1 行聚合行 ⇒ 7 行。
    /// </summary>
    public const int GroupMinFiles = 2;

    /// <summary>
    /// 把快照文件列表整理成详情页要显示的行（纯函数：不读盘、不抛、不改动传入的对象）。
    ///
    /// 规则（与需求一一对应）：
    ///   ① 先按 <see cref="CategoryOf"/> 分桶，桶按 <see cref="CategoryOrder"/> 列出；
    ///   ② 桶内**可回滚**文件 ≥ <see cref="GroupMinFiles"/> ⇒ 出一行聚合行；
    ///      少于阈值 ⇒ 该桶的可回滚文件各出一行逐文件行（保持单行）；
    ///   ③ 桶内**不可回滚（"跳过"）**的文件**永远逐行列出**（哪怕该桶已经聚合）——
    ///      它们本来就没有勾选框、点不动，塞进聚合行只会让"整类勾选"的含义变浑；
    ///      它们仍然排在自己那个大类的下面，悬停里也写清大类名（"不可回滚的项仍可见"）。
    ///   ④ 逐文件行按文件名排序（Ordinal，与采集顺序无关），保证每次列出的顺序完全一样；
    ///      "跳过"的项排在自己那个大类里可回滚项的后面。
    ///
    /// ⚠ 这一层**不过滤凭据**：凭据行（见 <see cref="IsHiddenRow"/>）由界面层在建控件前整行丢掉
    ///   （用户 1.3.56 实机要求"凭据不要出现"）⇒ 这里仍然如实产出全部行，纯函数层零副作用，
    ///   也免得"藏行"这件事渗进任何与回滚沾边的判定。
    /// </summary>
    public static List<SnapshotDisplayRow> BuildDisplayRows(IEnumerable<SnapshotFile>? files)
    {
        var rows = new List<SnapshotDisplayRow>();
        var buckets = new Dictionary<string, List<SnapshotFile>>(StringComparer.Ordinal);
        if (files != null)
        {
            foreach (var f in files)
            {
                if (f == null || string.IsNullOrEmpty(f.Name)) continue;
                string cat = CategoryOf(f.Name);
                if (!buckets.TryGetValue(cat, out var list))
                {
                    list = new List<SnapshotFile>();
                    buckets[cat] = list;
                }
                list.Add(f);
            }
        }

        // 按 CategoryOrder 排（同序者按 id 兜底，保证每次列出的顺序完全一样）
        var cats = buckets.Keys.ToList();
        cats.Sort((a, b) =>
        {
            int d = CategoryOrder(a).CompareTo(CategoryOrder(b));
            return d != 0 ? d : string.CompareOrdinal(a, b);
        });

        foreach (string cat in cats)
        {
            var restorable = new List<SnapshotFile>();
            var skipped = new List<SnapshotFile>();
            foreach (var f in buckets[cat])
                (f.Restorable ? restorable : skipped).Add(f);
            restorable.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            skipped.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

            if (restorable.Count >= GroupMinFiles)
                rows.Add(MakeGroupRow(cat, restorable, skipped.Count));
            else
                foreach (var f in restorable)
                    rows.Add(MakeFileRow(cat, f));      // 少于阈值：该桶的可回滚文件保持逐行

            // 不可回滚项：无论该桶聚不聚合，都逐行列出（可见性优先）
            foreach (var f in skipped)
                rows.Add(MakeFileRow(cat, f));
        }
        return rows;
    }

    /// <summary>建一个聚合行（把该大类的可回滚文件装进一行）。</summary>
    private static SnapshotDisplayRow MakeGroupRow(string cat, List<SnapshotFile> files, int skippedCount)
    {
        var row = new SnapshotDisplayRow { Category = cat, Kind = DisplayRowKind.Group };
        AddToRow(row, files);
        row.SkippedCount = skippedCount;
        return row;
    }

    /// <summary>建一个逐文件行。</summary>
    private static SnapshotDisplayRow MakeFileRow(string cat, SnapshotFile f)
    {
        var row = new SnapshotDisplayRow { Category = cat, Kind = DisplayRowKind.File, File = f };
        AddToRow(row, new List<SnapshotFile> { f });
        return row;
    }

    /// <summary>把一组文件装进行结构，顺手把计数与字节合计算好。</summary>
    private static void AddToRow(SnapshotDisplayRow row, List<SnapshotFile> files)
    {
        long total = 0;
        foreach (var f in files)
        {
            row.Files.Add(f);
            total += f.Size;
            if (f.Restorable) row.RestorableFiles.Add(f);
            else row.SkippedFiles.Add(f);
        }
        row.TotalBytes = total;
        row.RestorableCount = row.RestorableFiles.Count;
        row.SkippedCount = row.SkippedFiles.Count;
    }

    /// <summary>
    /// 聚合行右列的常量文案（纯函数）：可回滚件数 + 大小合计。
    /// 逐文件行不用它（那边右列只有大小或「跳过」，见 MainWindow.Console.cs）。
    ///
    /// ⚠ 这里**刻意不写**「整类」二字：用户 1.3.56 实机要求"去掉那四个字、清单要工整对齐"，
    ///   右列只留"件数 · 合计尺寸"；"整类"语义改由 <see cref="GroupToolTip"/> 承载。
    /// </summary>
    public static string GroupSizeText(int restorableCount, long totalBytes)
        => restorableCount <= 1 ? GuardPaths.HumanSize(totalBytes) : $"{restorableCount} 件 · {GuardPaths.HumanSize(totalBytes)}";

    /// <summary>
    /// 聚合行的悬停：把**具体内容**列清楚 —— 文件名（插件行显示插件名）+ 大小、大小合计、跳过项，
    /// 细节一个不丢，但不占版面（纯函数）。
    ///
    /// 第 2 行是"整类"语义的唯一落点：版面上已不再写「整类勾选」（用户 1.3.56 实机要求去掉那四个字），
    /// 所以"这一行代表一整类、勾它等于把该类逐个勾一遍"必须在这里显式说清楚。
    /// </summary>
    public static string GroupToolTip(SnapshotDisplayRow row)
    {
        if (row == null) return "";
        string text = Headline(row);       // 聚合行会带上插件名（见 GroupSubject）
        if (row.Kind == DisplayRowKind.Group)
        {
            // 标题行（插件名）不追加"（本类共 N 件…）"：那一行会因此过长，明细改挂到第 2 行。
            text += "\n"
                  + $"本行为整类：共 {row.Files.Count} 件"
                  + (row.SkippedCount > 0 ? $"，其中 {row.SkippedCount} 件不可回滚" : "")
                  + $"，合计 {GuardPaths.HumanSize(row.TotalBytes)}\n"
                  + $"勾选本行等效于逐项勾选该类的全部 {row.RestorableCount} 个可回滚文件：\n";
        }
        foreach (var f in row.RestorableFiles)
            text += $"· {ItemCaption(row.Category, f.Name)}  {GuardPaths.HumanSize(f.Size)}\n";
        foreach (var f in row.SkippedFiles)
            text += $"· {ItemCaption(row.Category, f.Name)}  ⏭ 不可回滚"
                  + (f.SkipReason.Length > 0 ? "：" + f.SkipReason : "") + "\n";
        return text.TrimEnd('\n');
    }

    /// <summary>
    /// 悬停里"这一件是什么"的写法（纯函数）：插件文件写 <c>&lt;插件&gt;/&lt;文件&gt;</c>，
    /// 其余走 <see cref="FriendlyName"/>。
    ///
    /// ⚠ 分隔符统一用 <c>/</c>：采集名里的 <c>-</c>（见 <see cref="PluginEntryName"/>）同时代表
    ///   **目录分隔符**与**文件名里本来就有的连字符**，两者无法互相还原 ——
    ///   本机那个插件目录叫 <c>dsh-imagegen</c>（一个目录、名字里有连字符），
    ///   若按 <c>\</c> 反向展开会假造出 <c>dsh\imagegen</c> 这样并不存在的层级。
    ///   ⇒ 这里只把 <b>第一段</b>当插件名（与 <see cref="GroupSubject"/> 同一口径），
    ///     余下整段原样当文件名，绝不假造路径。
    /// </summary>
    public static string ItemCaption(string category, string name)
    {
        if (category != SnapshotCategory.PluginFiles
            || !name.StartsWith("profile-plugins-", StringComparison.OrdinalIgnoreCase))
            return FriendlyName(name);
        string rest = name.Substring("profile-plugins-".Length);
        int dash = rest.IndexOf('-');
        return dash <= 0 ? rest : rest.Substring(0, dash) + "/" + rest.Substring(dash + 1);
    }

    /// <summary>
    /// 行的"主题"（纯函数）：聚合行在插件类下给出**插件名**
    /// （悬停第一行写它，一眼看出动的是哪个插件），其余情况就是大类名。
    /// 非聚合行不猜插件名（可能是另一批文件，见 <see cref="GroupSubject"/> 的空串约定）。
    /// </summary>
    public static string Headline(SnapshotDisplayRow row)
    {
        if (row == null) return "";
        string subject = row.Kind == DisplayRowKind.Group
            ? GroupSubject(row.Category, row.RestorableFiles.Select(f => f.Name))
            : "";
        return subject.Length > 0 ? CategoryLabel(row.Category) + " · " + subject : CategoryLabel(row.Category);
    }

    /// <summary>
    /// 悬停的标题行（纯函数）：非聚合行只写"大类名 + 这是哪一件"，聚合行交给 <see cref="Headline"/>。
    /// 这样"未聚合的单行"也看得出自己属于哪一类，而聚合行写的是插件名。
    /// </summary>
    public static string RowCaption(SnapshotDisplayRow row)
        => row == null ? ""
            : row.Kind == DisplayRowKind.Group
                ? Headline(row)
                : CategoryLabel(row.Category) + " · " + ItemCaption(row.Category, row.File?.Name ?? "");

    /// <summary>
    /// 插件文件的**插件名**（纯函数）：取同一次采集里各文件的公共路径前缀 ——
    /// 本机实测 6 个采集名全部以 <c>profile-plugins-dsh-imagegen-</c> 开头 ⇒ 得出 <c>dsh-imagegen</c>；
    /// 若快照里混了多个插件的文件，公共前缀会退化成 <c>plugins</c> ⇒ 如实返回空串
    /// （不编一个"共同插件名"出来）。
    /// </summary>
    public static string GroupSubject(string category, IEnumerable<string>? names)
    {
        if (category != SnapshotCategory.PluginFiles || names == null) return "";
        const string prefix = "profile-plugins-";
        string common = "";
        bool first = true;
        foreach (var n in names)
        {
            if (n == null || !n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return "";
            if (first) { common = n.Substring(prefix.Length); first = false; continue; }
            int i = 0;
            while (i < common.Length && i < n.Length - prefix.Length
                   && char.ToLowerInvariant(common[i]) == char.ToLowerInvariant(n[prefix.Length + i])) i++;
            common = common.Substring(0, i);
        }
        // 前缀是被 '-' 截断的（'-' 就是原来的目录分隔符）⇒ 退到最后一个完整段
        int cut = common.LastIndexOf('-');
        return cut <= 0 ? "" : common.Substring(0, cut);
    }

    // ══════════ 回滚 ══════════

    /// <summary>还原目标路径的越界判据（纯函数，不读盘、不抛）。</summary>
    public enum RestoreTargetVerdict
    {
        /// <summary>归一化后落在 ProfileDir / DshHome 之下，可还原。</summary>
        Allowed,
        /// <summary>不在允许根之下（或归一化失败）：拒绝覆盖。</summary>
        OutOfBounds,
        /// <summary>自身就是某允许根（以目录身份指向 profile / .dsh）：那是目录不是文件，同样拒绝。</summary>
        TargetsRoot,
        /// <summary>
        /// 目标落在允许根之下，但**磁盘上已经是一个目录**（例如 &lt;ProfileDir&gt;\plugins）：
        /// 照样是"那是目录不是文件" ⇒ 拒绝。收录【中 1】的实测现场就在这里 ——
        /// 只挡"恰好等于根"时，profile-plugins 这种精确名会把 plugins 目录写成同名文件。
        /// </summary>
        TargetsDir,
        /// <summary>空路径 / 纯空白：没有可还原的目标。</summary>
        Empty
    }

    /// <summary>
    /// 越界报告里的说明（纯函数）。
    ///
    /// ⚠ 文案里**不留目标路径**：这一行会整份进回滚结果弹窗（MainWindow.Console.cs:1745 把整份 report
    ///   送进 GuardDialog），而界面不许出现盘符路径 —— 目标路径改由调用方走
    ///   <see cref="NoteRefusedRestore"/> 落日志：界面只说"是哪个文件、为什么没还原"。
    /// ⚠ "超出允许范围"这几个字被自检钉着（SelfTest.cs:3284 `StartsWith("❌") &amp;&amp; Contains("超出允许范围")`），
    ///   改写这几个字会让自检变红 —— 本行的 `❌ ` 前缀同理（SummarizeRestoreReport 按前缀数失败数）。
    /// </summary>
    public static string OutOfBoundsNote(SnapshotFile file)
        => $"❌ {FriendlyName(file.Name)}: 目标位置超出允许范围，已跳过";

    /// <summary>
    /// 目标已是目录时的拒绝说明（纯函数）。
    /// ⚠ 同 <see cref="OutOfBoundsNote"/>：不收路径、目标路径只走 <see cref="NoteRefusedRestore"/> 落日志，
    ///   本行只留"哪个文件 + 为什么没还原"（自检未钉这一句的关键词，只有 `❌ ` 前缀是硬约束）。
    /// 收录【中 1】：目录不存在时 File.Copy 不会报错，而是**创建一个同名文件**把目录名占掉 ——
    /// 所以"没报错"绝不能当成"还原成功"，必须在写之前按磁盘事实拒绝。
    /// </summary>
    public static string TargetsDirNote(SnapshotFile file)
        => $"❌ {FriendlyName(file.Name)}: 目标已是目录，已跳过";

    /// <summary>
    /// 被拒绝还原时，把**目标路径**送到日志的唯一出口 —— 界面上一律不出现（收口：结果弹窗正文由
    /// DoRestore 整份给 GuardDialog，见 MainWindow.Console.cs:1745）。
    ///
    /// 分工与下方 catch 支路完全一致：走 <see cref="Logger.NoteDiagnosis"/>（<c>[WARN]</c> 单行写进异常日志，
    /// 不置失败标记、不弹窗）。落盘内容比原来界面上的那句话更全：为什么拒 + 哪个文件 + 目标路径。
    /// </summary>
    private static void NoteRefusedRestore(string why, SnapshotFile file, string dst)
        => Logger.NoteDiagnosis(
            $"拒绝还原（{why}）：{FriendlyName(file.Name)}（{file.Name}）→ "
            + (string.IsNullOrWhiteSpace(dst) ? "(未给出目标路径)" : dst));

    /// <summary>
    /// 目标是不是本程序已知的**容器目录**（收录【中 1】）。
    /// <para>
    /// 与 <c>Directory.Exists</c> 的分工：那条看"此刻在不在"，这条看"**路径身份**上是不是容器"，
    /// 所以**目录不存在时照样成立** —— 而目录不存在恰恰是最危险的一支（File.Copy 会创建一个
    /// 同名文件把目录名占掉）。当前已知容器只有 &lt;ProfileDir&gt;\plugins。
    /// </para>
    /// <para>
    /// 判据是<b>归一化后整段相等</b>，不是前缀：<c>&lt;Profile&gt;\plugins\pkg\index.js</c> 这类
    /// 正常还原目标**不受影响**，只有"正好就是容器本身"才拦住。
    /// 纯函数（除 NormalizeForCompare 的路径归一外不读盘、不抛）。
    /// </para>
    /// </summary>
    public static bool IsKnownContainerDir(string? target)
    {
        string? full = NormalizeForCompare(target);
        string? plugins = NormalizeForCompare(PluginsDir);
        return full != null && plugins != null && full.Equals(plugins, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>哈希不符的说明（纯函数）。</summary>
    public static string HashMismatchNote(string name)
        => $"❌ {FriendlyName(name)}: 快照文件已损坏或被改动，已跳过";

    /// <summary>
    /// 比较用的路径归一：绝对化（相对路径按当前目录补全）后去掉全部尾分隔符与外层引号。
    /// 失败返回 null —— 调用方**按越界处理**（失败关闭，绝不赌一个没归一成功的路径）。
    /// </summary>
    public static string? NormalizeForCompare(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            string full = Path.GetFullPath(path.Trim().Trim('"'));
            full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return full.Length == 0 ? null : full;
        }
        catch { return null; }
    }

    /// <summary>
    /// full（必须已归一化）是否就是 root 本身、或落在 root 之下。
    ///
    /// 判据是**带分隔符的前缀**：`C:\a\prof2` 不以 `C:\a\prof\` 开头 ⇒ 不会被当成 profile 里的文件
    /// （这正是 <c>C:\a</c> 与 <c>C:\ab</c> 那个前缀陷阱）。
    /// root 自身算"在之下"，由调用方按目录/文件区分要不要放行。
    /// </summary>
    public static bool UnderRoot(string? full, string? root)
    {
        string? r = NormalizeForCompare(root);
        if (full == null || r == null) return false;
        return full.Equals(r, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 还原目标判据（纯函数）：必须落在 <see cref="ProfileDir"/> 或 <see cref="DshHome"/> 之下。
    ///
    /// 为什么必须卡这一道：清单里的 Target 是**建快照那台机器**的绝对路径，
    /// 用户改过 profile 目录（或 profile 被指到别处）之后，照着它写就是写到一个用户根本不知道的地方 ——
    /// 当前配置纹丝不动、报告还显示 ✅（假成功）；被指向 profile 之外的可写文件时就是**直接覆盖人家**。
    /// </summary>
    public static RestoreTargetVerdict CheckRestoreTarget(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return RestoreTargetVerdict.Empty;
        string? full = NormalizeForCompare(target);
        // 归一失败（非法字符 / 超长 / 相对路径算不出来）⇒ 失败关闭，一律当越界
        if (full == null) return RestoreTargetVerdict.OutOfBounds;
        string? profile = NormalizeForCompare(ProfileDir);
        string? home = NormalizeForCompare(DshHome);
        // 自身就是允许根 ⇒ 那是目录不是文件，拒绝（必须排在 UnderRoot 前面：UnderRoot 认"等于根"）
        if ((profile != null && full.Equals(profile, StringComparison.OrdinalIgnoreCase)) ||
            (home != null && full.Equals(home, StringComparison.OrdinalIgnoreCase)))
            return RestoreTargetVerdict.TargetsRoot;
        // ★ ① 已知的**容器目录** ⇒ 那是目录不是文件，拒绝（收录【中 1】）。
        //   <ProfileDir>\plugins 是真实插件的存放目录，由本文件的采集/回滚当成容器使用。
        //   ⚠ 这一道**不能**改成 Directory.Exists(full)：目录**不存在**时判据会失效，
        //     而"不存在"恰恰是最危险的一支 —— File.Copy 不报错，直接创建一个同名**文件**
        //     把目录名永久占掉（实测复现）。所以这里按"路径就是那个已知容器"判，与目录此刻在不在无关。
        //   必须排在 Allowed 之前，否则又会被放行。
        string? plugins = NormalizeForCompare(PluginsDir);
        if (plugins != null && full.Equals(plugins, StringComparison.OrdinalIgnoreCase))
            return RestoreTargetVerdict.TargetsDir;
        // ★ ② 兜底判据：允许根之下任何**此刻已是目录**的目标，照样是"目录不是文件" ⇒ 拒绝。
        //   覆盖面比 ① 宽（用户在 profile 里另建了别的目录时也拦住），
        //   但它在"目录不存在"时给不出结论 ⇒ ① 才是那条不看磁盘的硬判据，两者缺一不可。
        //   这里不会误伤正常文件：File.Exists 的文件 Directory.Exists 恒为 false。
        if (Directory.Exists(full)) return RestoreTargetVerdict.TargetsDir;
        return UnderRoot(full, profile) || UnderRoot(full, home)
            ? RestoreTargetVerdict.Allowed
            : RestoreTargetVerdict.OutOfBounds;
    }

    /// <summary>
    /// 选择性回滚：restoreNames 为空表示回滚该快照全部可回滚文件，返回逐文件结果。
    ///
    /// 顺序与理由：
    ///   ① 先把**当前**这些文件存一份「回滚前」快照（用户的反悔素材）—— 打不了不阻断回滚，只在报告里如实说；
    ///   ② 逐文件判越界（Target 必须落在 profile / ~/.dsh 之下）⇒ 越界记 ❌ 跳过，**不中止**整次回滚；
    ///   ③ 覆盖前比对清单里的 sha256 ⇒ 不符记 ❌ 跳过（快照被改动后回滚等于写垃圾）；
    ///      清单没记哈希（老快照）放行但注明，不破坏老快照的可用性。
    /// </summary>
    public static List<string> Restore(Snapshot snap, ICollection<string>? restoreNames)
    {
        var report = new List<string>();

        var targets = snap.Files
            .Where(f => f.Restorable)
            .Where(f => restoreNames == null || restoreNames.Count == 0 || restoreNames.Contains(f.Name))
            .ToList();

        if (targets.Count == 0)
        {
            // ★ 一个文件都没选中 ⇒ 什么都没执行，绝不能长成"✅"的样子（否则又是一次假成功）
            report.Add(NoRestorableFilesLine);
            return report;
        }

        report.Add($"回滚快照 {snap.Id}（{targets.Count} 个文件）");

        // ① 回滚前自动存一份：这是用户被覆盖后唯一的反悔素材（配额 PreRestoreKeep，复用既有入口）
        var preSnap = Create(KindPreRestore, $"回滚 {snap.Id} 前");
        if (preSnap != null)
            report.Add($"🛟 回滚前已自动存快照 {preSnap.Id}（{preSnap.RestorableCount} 个文件）");
        else
            report.Add("⚠️ 未能创建回滚前备份，本次回滚不可撤销（继续回滚）");

        foreach (var f in targets)
        {
            string src = Path.Combine(snap.Dir, f.Name);
            string dst = f.Target;
            try
            {
                if (!File.Exists(src)) { report.Add($"✕ {f.Name}: 快照文件缺失"); continue; }

                // ② 越界护栏：Target 来自清单，可能是**当时那台机器**的绝对路径，绝不能不问就写
                var verdict = CheckRestoreTarget(dst);
                if (verdict != RestoreTargetVerdict.Allowed)
                {
                    // ⚠ 这三行的 `❌ ` 前缀一个字都不许动（SummarizeRestoreReport 按它数失败数，
                    //   见 MainWindow.Console.cs:1727 的疤）；路径一律不上屏，只经 NoteRefusedRestore 落日志。
                    if (verdict == RestoreTargetVerdict.TargetsRoot)
                    {
                        NoteRefusedRestore("目标指向目录而非文件", f, dst);
                        report.Add($"❌ {FriendlyName(f.Name)}: 目标指向目录而非文件，已跳过");
                    }
                    else if (verdict == RestoreTargetVerdict.TargetsDir)
                    {
                        NoteRefusedRestore("目标已是目录", f, dst);
                        report.Add(TargetsDirNote(f));
                    }
                    else if (verdict == RestoreTargetVerdict.Empty)
                    {
                        // 空目标本来就没有路径可丢 ⇒ 不记日志；这一行与它的判据都保持原样
                        report.Add("❌ 拒绝还原：目标路径为空，已跳过");
                    }
                    else
                    {
                        NoteRefusedRestore("目标超出允许范围", f, dst);
                        report.Add(OutOfBoundsNote(f));
                    }
                    continue;
                }

                // ②′ 落盘前的最后一道，按**路径身份 + 此刻的磁盘事实**双判（收录【中 1】）。
                //    为什么在 CheckRestoreTarget 之外还要单独再来一次：
                //      · File.Copy 往**已存在**的目录上写会抛（报告 ❌、无损伤），
                //        而目标**不存在**时它压根不报错 —— 直接创建一个同名文件把目录名永久占掉
                //        （真实插件的 plugins\ 从此不可用）。这一支没有异常兜底，必须在这里拦住；
                //      · 本文件里的判据是瞬时的，目录也可能在上一步之后才出现（并发 / 用户手动建）。
                //    ★ 这一道是"覆盖所有来源"的那一道：不管 Target 来自 manifest 字段还是兜底映射，
                //      都从这里过 —— 与它为什么被放行无关。
                if (IsKnownContainerDir(dst) || Directory.Exists(dst))
                {
                    // ⚠ 同样只把路径交给日志（这一处与上面 TargetsDir 支路是同一个拒绝理由）
                    NoteRefusedRestore("目标已是目录", f, dst);
                    report.Add(TargetsDirNote(f));
                    continue;
                }

                // ③ 哈希校验：Create 写清单时算的就是这个 Hash()，这里必须用同一个函数
                if (f.Hash.Length > 0)
                {
                    string got = Hash(src);
                    if (!got.Equals(f.Hash, StringComparison.OrdinalIgnoreCase))
                    {
                        report.Add(HashMismatchNote(f.Name));
                        continue;
                    }
                }
                else
                {
                    report.Add($"ℹ️ {FriendlyName(f.Name)}: 老快照未记哈希，跳过校验");
                }

                string? dstDir = Path.GetDirectoryName(dst);
                if (!string.IsNullOrEmpty(dstDir) && !Directory.Exists(dstDir)) Directory.CreateDirectory(dstDir);
                File.Copy(src, dst, overwrite: true);

                // 读写回验：磁盘上这份必须与清单哈希一致，否则"报告 ✅ 但内容不对"又是假成功
                string written = Hash(dst);
                if (f.Hash.Length > 0 && !written.Equals(f.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    report.Add($"❌ {FriendlyName(f.Name)}: 写入后校验不符（磁盘 {written} ≠ 清单 {f.Hash}）");
                    continue;
                }
                report.Add($"✅ {FriendlyName(f.Name)} → 已还原");
            }
            catch (Exception ex)
            {
                // ⚠ 前缀 `❌ ` 一个字都不许动：SummarizeRestoreReport 按它数失败数（见 MainWindow.Console.cs:1728 的疤）。
                // 界面上只留中性中文：原文（英文异常，还可能带盘符路径）一律不上屏。
                // 原文不丢 —— 用户操作类失败 ⇒ NoteDiagnosis（[WARN]，不置失败标记、不弹窗，见 Logger.cs 的分工），
                // 落盘内容比原来界面上的 ex.Message 更全：异常类型 + 消息 + 是哪个文件 + 写到哪个目标。
                Logger.NoteDiagnosis(
                    $"回滚还原失败：{FriendlyName(f.Name)}（{f.Name}）→ {dst}\n"
                    + $"  {ex.GetType().Name}: {ex.Message}");
                // 承诺要能兑现：日志目录不可写时如实补一句（与 MainWindow.Tools.cs 的 LogPromise 同一口径；
                // 那个方法在 MainWindow 的 partial 里，本文件够不到，故就地取 Logger.UnavailableReason）。
                report.Add($"❌ {FriendlyName(f.Name)}: 还原失败（详细原因已记入日志）"
                    + (Logger.UnavailableReason.Length == 0 ? "" : "（日志目录当前不可写，这次没能记入日志）"));
            }
        }

        Logger.Log(string.Join(Environment.NewLine, report));
        return report;
    }

    public static List<string> RestoreSingle(Snapshot snap, SnapshotFile file)
        => Restore(snap, new[] { file.Name });

    // ══════════ 回滚结果的分类（★ 本文件是唯一判据，调用方只许调用、不许复制）══════════

    /// <summary>一行回滚报告的结果形态。各态互斥且**必须穷尽**，没有"其它的都不是失败"这种兜底。</summary>
    public enum RestoreLineOutcome
    {
        /// <summary>确实完成了一项还原（逐文件还原 / 版本钉回 / 按锁文件重装）。</summary>
        Success,
        /// <summary>看着没事，但**成败没核实到位 / 有风险**（校验被跳过、无法核对、备份或停引擎没做成）。</summary>
        Unverified,
        /// <summary>**没还原成功**（含"快照文件缺失"这种压根没执行的）。</summary>
        Failed,
        /// <summary>**未执行**：按设计主动跳过或明说没做，但绝不是"成功"。</summary>
        NotPerformed,
        /// <summary>说明 / 进度类的行，不参与统计，也不影响成败。</summary>
        Info
    }

    /// <summary>
    /// 逐文件回滚必然出现的结果前缀（全角符号，与 <see cref="RestoreLineOutcome"/> **一一对应**）。
    /// </summary>
    private static readonly string[] RestoreOutcomeMarks =
    {
        "✅",   // Success      真还原了（逐文件回写的实测就是这种）
        "❓",   // Unverified   没验证到，不能报成成功
        "❌",   // Failed       拒绝还原 / 缺失 / 写入后校验不符 / 异常
        "✕",   // Failed       ★ 本单的疤：既不是 ✅ 也不是 ❌，当年被漏计 -> 弹窗写「成功 0 / 失败 0」
        "⛔",   // NotPerformed 没有可回滚的文件（一个字节都没动）
        "⚠️",  // Unverified   没核实到位 / 有风险（读不到已装版本、缺清单、备份或停引擎没做成）
        "ℹ️",  // NotPerformed 按设计没做的说明（没勾回退插件 / 顺手停过引擎）
        "🛟"    // Info         回滚前自动存的那份快照
    };

    /// <summary>回滚报告里尚未投产的说明 / 进度前缀（先占好位，免得将来它们被漏判成成功）。</summary>
    private static readonly string[] RestoreInfoMarks = { "🧭", "📁", "🐾" };

    /// <summary>
    /// 回滚结果行的**唯一分类判据**（纯函数，不读盘、不抛）。
    ///
    /// 为什么必须只有一个判据：本缺陷就是"用前缀字符串分类"这一做法**扩散**出来的 ——
    /// 产出方写了 <c>✕</c>，消费方却只数 <c>✅</c> / <c>❌</c>，于是失败恒为 0、
    /// 弹窗还配 Information 图标。所以修法不是"再多补一个前缀"，而是把判据收成一份：
    /// 产出方与消费方都从这里过，将来加前缀只改这一处。
    ///
    /// 分类口径（**纯前缀、不嗅文案** —— 嗅文案就是把同一份判据又抄一遍）：
    ///   · <see cref="RestoreLineOutcome.Success"/> 才算"这一项成了"；
    ///   · <see cref="RestoreLineOutcome.Failed"/>：拒绝还原、快照文件缺失、哈希不符、
    ///     写入后校验不符、异常、插件重装失败；
    ///   · <see cref="RestoreLineOutcome.Unverified"/>：**没核实到位**（老快照没记哈希 ⇒ 写入后
    ///     校验被跳过、磁盘上读不到已装版本、快照缺插件清单、回滚前备份 / 停引擎没做成）——
    ///     这类不许报成成功，因为"没验证"和"验证通过"完全不是一回事；
    ///   · <see cref="RestoreLineOutcome.NotPerformed"/>：**未执行**（一个文件都没选中、
    ///     没勾回退插件）—— 是主动跳过，不是失败，所以**不算失败**；
    ///   · 认不出来 ⇒ <see cref="RestoreLineOutcome.Info"/>：不参与统计。**绝不认成成功。**
    ///
    /// 判据是前缀，不是"含 ✅ 字样就算成功"：报告的正文里本来就带 ✅/❌ 之外的符号，
    /// 用 Contains 会把一整段说明误判成成功。
    /// </summary>
    public static RestoreLineOutcome ClassifyRestoreLine(string? line)
    {
        string t = (line ?? "").TrimStart();
        if (t.Length == 0) return RestoreLineOutcome.Info;

        // ★ 只认"已知且想清楚了"的形态；其余落到 Info，绝不落到 Success
        string mark = RestoreOutcomeMarks.FirstOrDefault(m => t.StartsWith(m, StringComparison.Ordinal))
                   ?? RestoreInfoMarks.FirstOrDefault(m => t.StartsWith(m, StringComparison.Ordinal))
                   ?? "";

        switch (mark)
        {
            case "✅": return RestoreLineOutcome.Success;
            case "❓":
            case "⚠️": return RestoreLineOutcome.Unverified;
            case "❌":
            case "✕": return RestoreLineOutcome.Failed;
            case "⛔":
            case "ℹ️": return RestoreLineOutcome.NotPerformed;
            default: return RestoreLineOutcome.Info;
        }
    }

    /// <summary>没有可回滚文件时的报告行（★ 必须不是一个 <c>✅</c> 开头的样子）。</summary>
    public const string NoRestorableFilesLine = "⛔ 没有可回滚的文件（本次未执行任何还原）。";

    /// <summary>回滚报告的汇总（成功 / 失败 / **未执行** / 未验证 / 说明行 各自计数）。</summary>
    public readonly record struct RestoreReportSummary(
        int Success, int Failed, int NotPerformed, int Unverified, int Info)
    {
        /// <summary>
        /// ★ 本次回滚能不能被说成"成功"。判据是**全称**的，只要有一条不满足就不算：
        ///   · <c>Success &gt; 0</c>：**必须至少真还原了一项**。"一个文件都没选中"（⛔）时
        ///     Success=0，照样不能说"完成"（本单现场那句「成功 0」就是这么来的）；
        ///   · <c>Failed == 0</c>：任何一项没还原成功都不行 —— "快照文件缺失"（✕）、
        ///     "目标不可写"（❌）都在这里被一票否决；
        ///   · <c>Unverified == 0</c>：任何一项"没核实到位"同样不行 —— 没验证过的"成功"不是成功。
        ///
        /// <see cref="RestoreLineOutcome.NotPerformed"/>（跳过）**不**参与否决：主动跳过是如实记录，
        /// 把它算成失败会制造假失败。但它也**永远不会**被算成成功。
        /// </summary>
        public bool FullyRestored => Success > 0 && Failed == 0 && Unverified == 0;
    }

    /// <summary>
    /// 把报告逐行过一遍唯一的分类判据再汇总（纯函数）。调用方**只调用、不重新数前缀**。
    /// </summary>
    public static RestoreReportSummary SummarizeRestoreReport(IEnumerable<string>? report)
    {
        int ok = 0, bad = 0, notPerformed = 0, unverified = 0, info = 0;
        foreach (string line in report ?? Enumerable.Empty<string>())
        {
            switch (ClassifyRestoreLine(line))
            {
                case RestoreLineOutcome.Success: ok++; break;
                case RestoreLineOutcome.Failed: bad++; break;
                case RestoreLineOutcome.NotPerformed: notPerformed++; break;
                case RestoreLineOutcome.Unverified: unverified++; break;
                default: info++; break;
            }
        }
        return new RestoreReportSummary(ok, bad, notPerformed, unverified, info);
    }

    /// <summary>
    /// 状态栏 / 事件栏那句话（纯函数，与结果框**同源同判据**，不允许各写一套文案）。
    ///
    /// 只在真的"每一件都成了"时才说"回滚完成"：有失败或没验证到就改成"回滚未完成"，
    /// 绝不出现"成功 0 / 失败 0"这种看着没事的句子（本单的现场就是这一句）。
    /// </summary>
    public static string RollbackSummaryText(RestoreReportSummary s)
        => $"{RollbackSummaryHead(s)}：成功 {s.Success} / 未还原 {s.Failed} / 未执行 {s.NotPerformed}"
           + (s.Unverified > 0 ? $" / 未验证 {s.Unverified}" : "");

    /// <summary>那句话的抬头，单独抽出来便于自检只断言这一半。</summary>
    private static string RollbackSummaryHead(RestoreReportSummary s)
        => s.FullyRestored ? "回滚完成" : "回滚未完成";

    /// <summary>删除一份快照。</summary>
    public static bool Delete(Snapshot snap)
    {
        try
        {
            if (Directory.Exists(snap.Dir)) Directory.Delete(snap.Dir, recursive: true);
            Logger.Log($"快照已删除：{snap.Dir}");
            return true;
        }
        catch (Exception ex) { Logger.LogError("SnapshotManager.Delete", ex); return false; }
    }

    /// <summary>仓库占用（字节），用于设置页显示。</summary>
    public static long TotalSize()
    {
        long total = 0;
        try
        {
            if (!Directory.Exists(SnapshotRoot)) return 0;
            foreach (var f in Directory.GetFiles(SnapshotRoot, "*", SearchOption.AllDirectories))
                total += new FileInfo(f).Length;
        }
        catch { }
        return total;
    }

    // ══════════ 工具 ══════════
    private static string Hash(string path)
    {
        try
        {
            using var sha = SHA256.Create();
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
        }
        catch { return ""; }
    }
}
