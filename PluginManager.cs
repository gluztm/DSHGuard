using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace DSHGuard;

/// <summary>
/// 插件清单扫描、兼容性判定、启用/禁用。
/// 数据来源：profile\package.json（dependencies）、各插件 node_modules\&lt;pkg&gt;\package.json、
/// cordis.patch.yml（disabled 行）、`dsh --dump-config` 输出（包名 -> loader id）。
/// 卸载动作由 UI 层用 RunCommandAsync 执行（本类只提供命令行）。
/// </summary>
public static class PluginManager
{
    public static string ProfileDir => GuardPaths.ProfileDir;
    // ══════════ 加载器标识的落盘缓存 ══════════
    // 取 id 要靠 `dsh --dump-config`（子进程），它会因为工作目录/网络/超时而失败；
    // 失败时若没有兜底，界面就会变成"所有插件都禁用不了"（现场已发生）。
    // 因此每次成功都落盘一份缓存，失败时用上次的映射顶着，并在界面注明。
    /// <summary>自检用：把加载器标识缓存指到临时目录（默认 null = 真实 Config 目录，绝不误写；经 GuardPaths.ConfigDir 走，随 DSHGUARD_DATA_DIR 改根）。</summary>
    internal static string? LoaderIdCacheOverrideForTest;

    private static string LoaderIdCacheFile =>
        LoaderIdCacheOverrideForTest ?? Path.Combine(GuardPaths.ConfigDir, "loader-ids.json");

    /// <summary>自检用：读取默认缓存文件路径（验证它落点在 GuardPaths.ConfigDir，随 DSHGUARD_DATA_DIR 改根）。</summary>
    internal static string LoaderIdCacheFileForTest() => LoaderIdCacheFile;

    /// <summary>把"包名 -> 真实 loader id"写入缓存（失败不抛）。</summary>
    public static void SaveLoaderIdCache(IDictionary<string, string> map)
    {
        try
        {
            if (map == null || map.Count == 0) return;
            string dir = Path.GetDirectoryName(LoaderIdCacheFile)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var json = System.Text.Json.JsonSerializer.Serialize(
                new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase),
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(LoaderIdCacheFile, json, new UTF8Encoding(false));
            Logger.Log($"已缓存 {map.Count} 个加载器标识（供下次读取失败时兜底）");
        }
        catch (Exception ex) { Logger.LogError("PluginManager.SaveLoaderIdCache", ex); }
    }

    /// <summary>读缓存（没有或损坏返回空表）。</summary>
    public static Dictionary<string, string> LoadLoaderIdCache()
    {
        try
        {
            if (!File.Exists(LoaderIdCacheFile)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var got = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(LoaderIdCacheFile));
            return got != null ? new Dictionary<string, string>(got, StringComparer.OrdinalIgnoreCase)
                               : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) { Logger.LogError("PluginManager.LoadLoaderIdCache", ex); return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); }
    }

    /// <summary>
    /// 取 id 的来源判定（纯函数，便于自检）：dump 跑通且输出里确实有 `- id:` -> 用 dump 里的权威映射；
    /// 否则退回上次落盘的缓存（<c>FromCache=true</c>）；两边都没有 -> 空表（调用方必须如实报告，不能假装有）。
    ///
    /// 这条兜底为什么必须在：当清单已登记但本地未安装时，dump 命令会整体失败
    /// （退出码 1、stdout 为空、stderr 输出 cannot resolve profile bundle "包名"），
    /// 此时无法读取任何 id -> 若没有缓存作为兜底数据源，界面将退化为"所有插件都无法禁用"（现场已发生）。
    /// </summary>
    public static (Dictionary<string, string> Ids, bool FromCache, int ByNameCount) ResolveLoaderIds(
        bool dumpOk, string? dumpOutput, IDictionary<string, string>? cached)
    {
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (dumpOk && (dumpOutput ?? "").IndexOf("- id:", StringComparison.Ordinal) >= 0)
        {
            var ids = ParseLoaderIds(dumpOutput!);
            // 权威映射：dump 里 `- id: X` 紧跟的 `name: '包名'` 才是"包名->真实 id"
            byName = ParseLoaderIdNames(dumpOutput);
            foreach (var kv in byName) ids[kv.Key] = kv.Value;
            if (ids.Count > 0) return (ids, false, byName.Count);
            // dump 跑通却一条都解析不出 -> 与"拿不到"等价，继续往下走缓存
        }

        var fallback = cached == null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(cached, StringComparer.OrdinalIgnoreCase);
        return (fallback, fallback.Count > 0, byName.Count);
    }

    /// <summary>自检用：把 patch 文件指到临时目录（默认 null = 真实 profile，绝不误写）。</summary>
    internal static string? PatchFileOverrideForTest;

    public static string PatchFile => PatchFileOverrideForTest ?? Path.Combine(ProfileDir, "cordis.patch.yml");
    /// <summary>
    /// pnpm 的"供应链策略"覆盖参数。两个开关必须一起给，只给一个都过不去：
    ///   · <c>--trust-lockfile</c>：把已有锁文件当作已信任、跳过整份锁文件的供应链复核
    ///     （对应报错 "Lockfile failed supply-chain policy check (N entries)"）；
    ///   · <c>--config.minimumReleaseAge=0</c>：把包龄门槛降为 0（对应报错
    ///     <c>ERR_PNPM_MINIMUM_RELEASE_AGE_VIOLATION</c>，
    ///     "4 lockfile entries failed verification: … was published at …, within the minimumReleaseAge cutoff"）。
    /// 现场实测：只给 --trust-lockfile 挡不住包龄判定 —— 锁文件复核通过后，卡住安装的正是这条包龄策略；
    /// 反过来只给 --config.minimumReleaseAge=0 也挡不住锁文件复核。所以两个都保留。
    /// 两者都只作用在本程序执行的这一条命令上（命令行临时覆盖），不修改任何策略文件
    /// （pnpm-workspace.yaml / .npmrc 一律不动）。
    ///
    /// 注意：pnpm 不支持按次覆盖 minimumReleaseAge（见 pnpm#11224 / #10358），真正生效的是调用处注入的
    /// 环境变量（<see cref="SupplyChainRelaxEnv"/>，由 MainWindow 的两个运行命令方法写进
    /// <c>ProcessStartInfo.Environment</c>）。本机 pnpm 12.3.4 只读实测：<c>--config.minimumReleaseAge=0</c>
    /// 这种 camelCase 写法不被识别（kebab 的 <c>--config.minimum-release-age=0</c> 才被认），
    /// 所以这两个参数留着无害、但不要再依赖它。
    /// </summary>
    public const string PolicyOverride = "--trust-lockfile --config.minimumReleaseAge=0";

    /// <summary><see cref="PolicyOverride"/> 里的每个开关，供 <see cref="WithoutPolicyOverride"/> 逐个清除。</summary>
    private static readonly string[] PolicyFlags =
        PolicyOverride.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// 去掉策略覆盖参数的命令（供"参数不被识别时重试一次"使用）：
    /// 两个开关都要清掉 —— 不管先后顺序、不管中间多余的空格；
    /// 12.3.4 上应当支持 --config.&lt;key&gt;，但万一不支持也不能让安装/卸载彻底失败。
    /// </summary>
    public static string WithoutPolicyOverride(string args)
    {
        string s = args ?? "";
        foreach (var flag in PolicyFlags)
            s = Regex.Replace(s, @"\s*" + Regex.Escape(flag), "");   // 连同参数前面的空白一起清掉
        return Regex.Replace(s, @"\s{2,}", " ").Trim();              // 残留空档压平，去掉首尾空白
    }

    /// <summary>
    /// 真正生效的「放开 pnpm 包龄限制」手段：给这一次子进程注入的环境变量（纯函数，便于自检）。
    /// 两套前缀 × 三个键，顺序固定。
    ///
    /// 为什么是环境变量：pnpm 12.3.4 内置了一条 1440 分钟（24 小时）的包龄默认门槛，
    /// 它不落在任何配置文件里 —— 所以 <c>pnpm config get minimumReleaseAge</c> 读出来是 undefined，
    /// 而装新发布的包照样被 <c>ERR_PNPM_MINIMUM_RELEASE_AGE_VIOLATION</c> 拦下。要按次放开，
    /// 只能换"配置来源"（命令行 / 环境变量 / 配置文件），命令行那条已被证明不认（见 <see cref="PolicyOverride"/>）。
    ///
    /// 本机只读实测（pnpm 12.3.4，profile 目录内 <c>pnpm config get minimumReleaseAge</c>）：
    ///   · 不带环境变量 -> <c>undefined</c>；
    ///   · <c>npm_config_minimum_release_age=0</c> -> 仍是 <c>undefined</c>（这个版本不读 npm_ 前缀）；
    ///   · <c>pnpm_config_minimum_release_age=0</c> -> 读到 <c>0</c>（生效）。
    /// 两套前缀都注入：本机生效的是 <c>pnpm_config_</c>，留 <c>npm_config_</c> 是为了兼容只认 npm 前缀的版本。
    /// 只进 <c>ProcessStartInfo.Environment</c>，不写盘、不改任何配置文件、不影响本进程全局环境。
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> SupplyChainRelaxEnv() => new[]
    {
        // 本机 pnpm 12.3.4 实测生效的一组（pnpm 前缀 + 键名小写下划线）
        new KeyValuePair<string, string>("pnpm_config_minimum_release_age", "0"),
        new KeyValuePair<string, string>("pnpm_config_minimum_release_age_strict", "false"),
        new KeyValuePair<string, string>("pnpm_config_trust_lockfile", "true"),
        // npm 命名法的同一组（本机不读，留着兼容）
        new KeyValuePair<string, string>("npm_config_minimum_release_age", "0"),
        new KeyValuePair<string, string>("npm_config_minimum_release_age_strict", "false"),
        new KeyValuePair<string, string>("npm_config_trust_lockfile", "true"),
    };

    /// <summary>把 <see cref="SupplyChainRelaxEnv"/> 压成一行「键=值」（顺序固定），供日志留痕与自检。</summary>
    public static string SupplyChainRelaxEnvLog()
        => string.Join("、", SupplyChainRelaxEnv().Select(kv => $"{kv.Key}={kv.Value}"));

    /// <summary>
    /// 这条命令行是不是「改插件清单」的 dsh 插件命令（add / remove / update / install）。
    /// 用途：给那些改不动的调用点兜底（批量更新/批量卸载走的是同一对运行命令方法），
    /// 让它们也自动带上包龄放行；dump-config、引擎启动之类一律不匹配。
    /// 判定只看 dsh 的 plugin 子命令形状（纯函数，便于自检）。
    ///
    /// <c>update</c> 是 2026-09-18 加进来的，不可删除：git 源的更新命令从
    ///   <c>add &lt;仓库地址&gt;</c> 改成了 <c>update &lt;包名&gt;</c>（见 <see cref="BuildUpdateArgs"/> 的长文），
    ///   它同样会改锁文件与 node_modules -> 必须和 add/remove/install 一视同仁地拿到包龄放行，
    ///   否则 git 源更新会卡在 pnpm 的 minimumReleaseAge 上。
    /// </summary>
    public static bool LooksLikePluginMutation(string args)
    {
        string s = " " + Regex.Replace(args ?? "", @"\s+", " ").Trim() + " ";
        if (!s.Contains(" plugin ") || !s.Contains(" --profile ")) return false;   // 必须是 dsh 的 plugin 子命令
        return s.Contains(" add ") || s.Contains(" remove ") || s.Contains(" install ") || s.Contains(" update ");
    }

    /// <summary>
    /// 插件退回"放宽一次安全检查"这条更宽松的路子时，追加给用户看的一句（不含命令行写法）。
    ///
    /// 文案口径（用户要求：界面不出现代码片段/命令行/内部标识/路径与专业术语，措辞仍要官方正式）：
    ///   只说发生了什么 + 下一步怎么办 —— 「已放宽更新来源的安全检查」是用户能懂的功能动作，
    ///   而具体实现（pnpm 的 minimumReleaseAge 包龄门槛 + 六个 <c>pnpm_config_*</c>/<c>npm_config_*</c>
    ///   环境变量、<see cref="SupplyChainRelaxEnv"/>、<c>--trust-lockfile</c>）一律不进界面。
    /// 注意：这些实现细节没有丢，落在两个地方，改文案时不可将它们删除：
    ///   ① 就在本类 <see cref="SupplyChainRelaxEnv"/> 的实测记录里（键名/取值/验法齐全，本文件不在用户可见路径上）；
    ///   ② 命令失败时由 <c>MainWindow.xaml.cs</c> 的 <c>NoteSupplyChainRelaxOnFailure</c> 经
    ///      <see cref="Logger.NoteDiagnosis"/> 落盘（形如「已放开 pnpm 供应链策略（环境变量，仅本次命令 …）：键=值…」）。
    /// 共享语义：本常量被多个调用点共用（事件栏 / 各类提示框，横跨 MainWindow.Tools / Batch / Market）
    /// -> 改这一处即全变，调用点不要再各自拼技术词。
    /// </summary>
    public const string SupplyChainRelaxNote = "已放宽更新来源的安全检查，以完成这次操作";

    /// <summary>
    /// 失败提示框里用的完整版说明（比事件流那条多给一句下一步）。
    /// 后半句按用户口径改成中性中文：原文「多半是网络或镜像源的问题」把专业名词摆到了界面上；
    /// 与完整判据/现场记录的衔接关系同 <see cref="SupplyChainRelaxNote"/>（技术细节走日志，不上界面）。
    /// </summary>
    public const string SupplyChainRelaxHint =
        "已放宽更新来源的安全检查，仍未能完成本次操作；可能是网络或更新来源方面的问题，可以稍后重试。";

    public static string PackageFile => PackageFileOverrideForTest ?? Path.Combine(ProfileDir, "package.json");

    /// <summary>
    /// 自检用：把清单文件（package.json）指到临时目录（默认 null = 真实 profile，绝不误写）。
    ///
    /// 为什么单给清单一个口子：<see cref="ManagedPluginAliases"/>「我们管的插件」与
    /// <see cref="HasDependency"/> 都只看这一份清单，而自检不能拿真机清单下断言 ——
    /// 那量的是"这台机器上恰好装了什么插件"，不是"这段判定对不对"（本项目已有同类教训）。
    /// 与 <see cref="PatchFileOverrideForTest"/> / <see cref="LoaderIdCacheOverrideForTest"/> 同一套做法：
    /// 只影响本进程内的读路径，只读不写，用完立刻还原。
    /// </summary>
    internal static string? PackageFileOverrideForTest;

    /// <summary>
    /// 兼容性四档（对应界面四色语义）：
    /// Ok（绿）= 满足声明要求，且正好是作者指向的版本；
    /// Partial（橙）= 在声明范围内，但非作者优先适配的版本；
    /// Unknown（灰）= 未声明 dsh 版本要求，无法判定；
    /// Broken（红）= 不满足声明要求。
    /// </summary>
    public enum Compat { Ok, Partial, Unknown, Broken }

    /// <summary>按声明要求与被检查版本计算兼容档位。</summary>
    public static Compat EvaluateBand(string requirement, string version)
    {
        if (string.IsNullOrWhiteSpace(requirement) || string.IsNullOrWhiteSpace(version) || version == "未知")
            return Compat.Unknown;
        if (!VersionInfo.Satisfies(requirement, version)) return Compat.Broken;

        // 满足要求后，再判断是否为作者指向的版本（>=0.1.5-rc.1 指向 0.1.5-rc.1，^0.1.0-rc.6 指向 0.1.0-rc.6）
        bool isTarget = VersionInfo.RequirementVersions(requirement)
            .Any(v => VersionInfo.Compare(v, version) == 0);
        return isTarget ? Compat.Ok : Compat.Partial;
    }

    public sealed class Plugin
    {
        /// <summary>
        /// 「包内没有作者信息」时的占位文本（<see cref="Author"/> 的默认值，也是
        /// <see cref="ExtractAuthor"/> 取不到时的返回值）。集中成常量，免得两处各写一份、
        /// 其中一处被改动后判定悄悄失效。
        /// </summary>
        public const string AuthorUnknown = "—";

        public string Name { get; set; } = "";
        public string Version { get; set; } = "?";
        public string Author { get; set; } = AuthorUnknown;
        public string Requirement { get; set; } = "";
        public string RequirementSource { get; set; } = "";
        public Compat Compatibility { get; set; } = Compat.Unknown;
        public bool Disabled { get; set; }
        public string? LoaderId { get; set; }
        public string Description { get; set; } = "";
        public string Dir { get; set; } = "";
        /// <summary>兼容性计算所依据的 DSH 版本（卡片直接显示该版本号）。</summary>
        public string CheckedAgainst { get; set; } = "";
        /// <summary>规整为 https 的仓库地址，供点击插件名打开主页。</summary>
        public string RepositoryUrl { get; set; } = "";
        public string Homepage { get; set; } = "";

        /// <summary>
        /// <see cref="Author"/> 是不是"仓库归属"兜底值（包内没写 author，只有仓库地址可推）。
        /// true 时界面必须标明这一点（悬停写「来自仓库地址」），不得把它当成作者本人展示。
        /// </summary>
        public bool AuthorFromRepo { get; set; }

        /// <summary>
        /// 作者区的悬停说明：兜底来的名字必须在此标明"这是仓库归属、不是作者本人"，
        /// 免得用户把它当成作者名。真作者信息与无作者两种情形各自给一句如实的话。
        /// </summary>
        public string AuthorTip => AuthorFromRepo
            ? $"来自仓库地址：{Author}（包内没写作者信息，这里显示的是仓库归属）"
            : Author == AuthorUnknown || string.IsNullOrWhiteSpace(Author)
                ? "包内没写作者信息"
                : $"作者：{Author}";

        /// <summary>
        /// 点击插件名打开的链接，优先级：清单里声明的来源（git 源唯一的可靠出处）->
        /// 包内 repository -> 包内 homepage -> 镜像站包页面。
        ///
        /// 两条硬规则：
        ///   ① git 源（`git+https://…`、`github:o/r`）必须指向仓库本身（任意托管站，含 gitee）；
        ///      镜像站上没有 git 源包，拼出来的包页面点开必然 404 —— 现场两张卡片的死链就是这么来的。
        ///   ② 镜像站 URL 只给 npm 包（能在 npm 上查到的普通包）。git 源与本地路径一律不用镜像站；
        ///      取不到仓库地址时返回空串（界面便不显示那个"有链接"的小箭头，也不再有可点的死链）。
        /// </summary>
        public string LinkUrl
        {
            get
            {
                try
                {
                    // ① 清单声明：git 源包磁盘上的 package.json 里没有 repository 字段，
                    //    来源只能从 package.json 的 dependencies spec 拿，这里就是唯一出处。
                    string spec = DepSpec(Name);
                    string declared = RepoUrlFromSpec(spec);
                    if (declared.Length > 0) return declared;

                    // ② ③ 包内字段（npm 包通常齐全）：repository -> homepage
                    if (!string.IsNullOrWhiteSpace(RepositoryUrl)) return RepositoryUrl;
                    if (!string.IsNullOrWhiteSpace(Homepage)) return Homepage!;

                    // ④ 镜像站：仅限 npm 包。git 源 / 本地路径（file:）与认不出的来源在这里被排除，
                    //    宁可给空串，也不给一个点开 404 的"死链"。
                    if (!AllowsNpmPage(spec)) return "";
                    return string.IsNullOrWhiteSpace(Name) ? "" : $"https://www.npmmirror.com/package/{Name}";
                }
                catch (Exception ex) { Logger.LogError("PluginManager.Plugin.LinkUrl", ex); return ""; }
            }
        }

        /// <summary>
        /// 本插件是不是本地链接 / 本地路径来源（`link:` / `file:` / 相对或绝对路径）。
        ///
        /// 界面靠这个标记把"没有网址"与"是本地插件"分开：本地插件没有可打开的网址，
        /// 但有可打开的本地目录 —— 点击行为因此从"打开网页"换成"在文件管理器中打开目录"，
        /// 悬停提示也据此如实说明"为什么没有网址"（见 <see cref="LocalSourceTip"/>）。
        /// </summary>
        public bool IsLocalSource => ClassifySource(DepSpec(Name)) == PluginSourceKind.Local;

        /// <summary>
        /// 本地链接插件实际该打开的目录；不是本地类、或解析不到时返回空串（界面便保持不可点）。
        ///
        /// 取值与校验都在 <see cref="ResolveLocalPluginDir"/> 里（声明路径按 profile 解析 ->
        /// 退回 node_modules 下的实际目录；越界与"目录不在"都给空串），这里只是把它接到卡片上。
        /// </summary>
        public string LocalDir
        {
            get
            {
                try
                {
                    string spec = DepSpec(Name);
                    if (ClassifySource(spec) != PluginSourceKind.Local) return "";
                    return ResolveLocalPluginDir(spec, Name).Dir;
                }
                catch (Exception ex) { Logger.LogError("PluginManager.Plugin.LocalDir", ex); return ""; }
            }
        }

        /// <summary>
        /// 本地链接插件的标题悬停说明。必须回答"为什么没有网址"，不能只说"作者未声明"：
        ///   · 目录拿得到 -> 如实写出来源（清单里那截声明路径）并说明点击会做什么；
        ///   · 目录拿不到 -> 说明是"本机没有它的目录"（未安装/已移动）还是"路径越界已拒绝"。
        /// 不是本地类时返回空串（调用方据此不挂这条提示）。
        /// </summary>
        public string LocalSourceTip
        {
            get
            {
                try
                {
                    string spec = DepSpec(Name);
                    if (ClassifySource(spec) != PluginSourceKind.Local) return "";

                    var r = ResolveLocalPluginDir(spec, Name);
                    if (r.Dir.Length > 0) return "本地插件：来自本机目录，点击在文件管理器中打开";
                    return r.Rejected
                        ? "本地插件：来自本机目录 —— 该目录不在插件目录范围内，已拒绝打开"
                        : "本地插件：来自本机目录 —— 本机没有它的目录（未安装或已被移动），因此没有可打开的网址";
                }
                catch (Exception ex) { Logger.LogError("PluginManager.Plugin.LocalSourceTip", ex); return ""; }
            }
        }

        public string StatusText => Disabled ? "已禁用" : "启用中";

        /// <summary>作者指向的版本，即声明中抽出的最高版本号（^0.1.0-rc.6 || ^0.1.5-rc.1 -> 0.1.5-rc.1）。</summary>
        public string RequirementTarget
        {
            get
            {
                if (string.IsNullOrEmpty(Requirement)) return "";
                var versions = VersionInfo.RequirementVersions(Requirement);
                if (versions.Count == 0) return Requirement;
                return versions.OrderByDescending(v => v, Comparer<string>.Create(VersionInfo.Compare)).First();
            }
        }

        /// <summary>
        /// 卡片的兼容标识：有版本号只显示版本号，否则显示「未声明」。
        /// 颜色由 UI 层按档位给出（灰=未声明）。
        /// </summary>
        public string CompatText => Compatibility switch
        {
            Compat.Ok => string.IsNullOrWhiteSpace(CheckedAgainst) ? "未声明" : CheckedAgainst,
            Compat.Partial => string.IsNullOrWhiteSpace(RequirementTarget) ? "未声明" : RequirementTarget,
            Compat.Broken => string.IsNullOrWhiteSpace(RequirementTarget) ? "未声明" : RequirementTarget,
            _ => "未声明"
        };

        /// <summary>ToolTip：补全只显示版本号时省略的说明。</summary>
        public string CompatDetail => Compatibility switch
        {
            Compat.Ok => $"完全兼容：正好是插件声明指向的 {CheckedAgainst}{ReqNote()}",
            Compat.Partial => $"能用，但不是作者优先适配的版本：作者面向 {RequirementTarget}，你当前 {CheckedAgainst}{ReqNote()}",
            Compat.Broken => $"不兼容：它需要 {RequirementTarget}，你当前 {CheckedAgainst}{ReqNote()}",
            _ => "未声明 dsh 版本要求，兼容性只能实测"
        };

        private string ReqNote() => string.IsNullOrEmpty(Requirement)
            ? ""
            : $"（声明 {Requirement}，来源 {RequirementSource}）";
    }

    // ══════════════════════════════════════════════════════════════════
    //  来源声明 -> 网页仓库地址（纯函数，便于自检）
    // ══════════════════════════════════════════════════════════════════
    //  为什么解析放在这里而不是 PluginSource：PluginSource.ParseGitRepo 只认 github.com，
    //  而现场两个 git 源插件里有一个在 gitee（git+https://gitee.com/iJetLi/…），
    //  只认 github 就解析不出仓库 -> 界面回落到镜像站包页面 -> 那个包在镜像站上不存在，
    //  点开必然 404（现场：dsh-codearts-auth 显示「未查询到」）。这里按任意托管站解析。

    /// <summary>
    /// 从清单里声明的依赖来源解析出网页仓库地址（去 <c>#ref</c>、去结尾 <c>.git</c>）。
    /// npm 的几种 git 写法都认：<c>git+https://host/o/r.git</c>、<c>https://host/o/r</c>、
    /// <c>git://host/o/r</c>、<c>ssh://git@host/o/r.git</c>、<c>git@host:o/r.git</c>、
    /// <c>github:o/r#ref</c>（gitee / gitlab / bitbucket 同理）。
    /// 认不出来（npm 版本范围、本地路径、其它协议）返回空串，绝不猜（纯函数）。
    /// </summary>
    public static string RepoUrlFromSpec(string? spec)
    {
        var (host, path) = ParseRepoSpec(spec);
        return host.Length > 0 && path.Length > 0 ? $"https://{host}/{path}" : "";
    }

    /// <summary>
    /// 这个来源声明是不是"能在 npm 上查到的普通包"——只有它是，镜像站包页面才是有效链接。
    /// git 源（git+/github:/git@/ssh://git://）、本地路径与认不出的来源一律 false。
    /// </summary>
    public static bool AllowsNpmPage(string? spec)
    {
        string s = (spec ?? "").Trim();
        if (s.Length == 0) return true;          // 声明读不到：按 npm 包处理（旧行为，不误伤老机器）
        if (IsGitSpec(s)) return false;
        // 版本范围绝不会以 http(s):// 开头 -> 这是仓库/压缩包直链，镜像站上没有对应包页
        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;
        if (Regex.IsMatch(s, @"^(file|link|portal|workspace|npm):", RegexOptions.IgnoreCase)) return false;
        // 其余（^1.2.3 / ~1.2.3 / 1.2.3 / latest / * 这类版本范围）都是能在 npm 上查到的普通包
        return true;
    }

    /// <summary>来源声明是不是 git 源（与 <see cref="PluginSource.Classify"/> 同一套形状判断）。</summary>
    public static bool IsGitSpec(string? spec)
    {
        string s = (spec ?? "").Trim();
        if (s.Length == 0) return false;
        return s.StartsWith("github:", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("gitlab:", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("bitbucket:", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("gitee:", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("git+", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("git://", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(s, @"^[^@/]+@[A-Za-z0-9._-]+:", RegexOptions.IgnoreCase)   // git@host:o/r.git
            || (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
               && (s.EndsWith(".git", StringComparison.OrdinalIgnoreCase) || s.Contains(".git#", StringComparison.OrdinalIgnoreCase));
    }

    // ══════════════════════════════════════════════════════════════════
    //  来源分类：本地链接也是一类（现场：dsh-imagegen 标题不可点）
    // ══════════════════════════════════════════════════════════════════
    //
    // 【现场 bug】卡片 dsh-imagegen 的标题不可点、没有"有链接"的小箭头，用户描述为
    // "无法识别，不能进入超链接"。取证结论：不是识别不到包 ——
    //   · 清单声明 `link:./plugins/dsh-imagegen`（profile\package.json）；
    //   · node_modules\dsh-imagegen 是指向 profile\plugins\dsh-imagegen 的 Junction；
    //   · loader id 缓存里有它 -> 引擎认得它。
    // 真正的原因是没有网址可点：本地链接走不到任何仓库/镜像站，LinkUrl 如实给空串
    // （那是刻意的，不能退回死链）。但"没网址"不等于"没东西可打开" ——
    // 这类插件有本地目录可打开，界面却连"它为什么没有网址"都没告诉用户。
    //
    // 这里把"本地链接/本地路径"升为明确的一类，与 Registry / Git 平级，
    // 供界面区分「没有网址」与「是本地插件」（不清不楚地都显示成"未声明"就是这次的根因）。
    //
    // 为什么不用 PluginSource.Kind：那是 PluginSource.cs 里的枚举（管"该怎么更新它"），
    // 且把 `file:` 归到了 git 源 —— 本地插件既不该按 git 源更新、也不该按 npm 包更新。
    // 这里自带一个小枚举，不动那个共用类型（改动面越小越安全）。

    /// <summary>一条依赖来源的大类（界面据此刻画"标题该不该可点、点了做什么"）。</summary>
    public enum PluginSourceKind
    {
        Unknown,    // 认不出来（声明读不到等），不要动
        Local,      // 本地链接 / 本地路径：link: / file: / 相对或绝对路径
        Git,        // git 源：github:o/r、git+https://…、ssh://… 等
        Registry    // 普通 npm 包（可带 ^ / ~ / 精确版本）
    }

    /// <summary>
    /// 判来源大类（纯函数，便于自检）。判据顺序有意义：先本地、再 git、最后才是 npm 包。
    ///   · `link:./plugins/x`、`file:../x`、`./x`、`../x`、`.\x`、`..\x`、`C:\path`、`/abs/path`
    ///     -> <see cref="PluginSourceKind.Local"/>；
    ///   · 其余非 git 的一律 <see cref="PluginSourceKind.Registry"/>（版本范围 / latest / *）。
    /// 会不会把 npm 包误判成本地：不会 —— npm 包名不许以 `.` 或 `/` 开头、也不许含 `:`，
    /// 而 `link:` / `file:` 前缀、`./` `../`（含反斜杠写法）与盘符/UNC 形态都只可能是路径。
    /// 本地先判还有一层理由：共用类型 <see cref="PluginSource.Classify"/> 把 `file:` 归进了"git 源"
    /// （那边只关心"该怎么更新"），而本地插件既不该按 git 源更新、也不该按 npm 包更新 ——
    /// 所以这里不能直接照抄那套顺序，否则 `file:` 会被吞进 git 档。
    /// </summary>
    public static PluginSourceKind ClassifySource(string? spec)
    {
        string s = (spec ?? "").Trim();
        if (s.Length == 0) return PluginSourceKind.Unknown;
        if (IsLocalSourceSpec(s)) return PluginSourceKind.Local;
        return IsGitSpec(s) ? PluginSourceKind.Git : PluginSourceKind.Registry;
    }

    /// <summary>
    /// 这条声明是不是"指向本机某个目录"（`link:` / `file:` / 相对或绝对路径）。
    /// 注意与 <see cref="IsGitSpec"/> 判据独立：git 那些前缀（`git+` / `git://` / `ssh://` /
    /// `github:` / `git@host:` …）落到这里都是 false，两类互不重叠。
    /// </summary>
    public static bool IsLocalSourceSpec(string? spec)
    {
        string s = (spec ?? "").Trim();
        if (s.Length == 0) return false;
        // ① 显式协议前缀：npm/pnpm/yarn 都认 link: 与 file:（file:// / file:/// 也一并认）
        if (Regex.IsMatch(s, @"^(link|file):", RegexOptions.IgnoreCase)) return true;
        // ② 相对路径：以 ./ ../ .\ ..\ 开头（`plugins/x` 这种裸相对路径 npm 不认，不当成本地插件）
        if (Regex.IsMatch(s, @"^\.\.?[\\/]")) return true;
        // ③ 绝对路径：`C:\x`、`C:/x`、`\\server\share\x`、`/abs/x`
        try { if (Path.IsPathRooted(s)) return true; } catch { }
        return false;
    }

    /// <summary>
    /// 从声明里取出"给人看的那截路径"：去掉 `link:` / `file:` 前缀，并把 `file://` / `file:///`
    /// 还原成 Windows 形态（`file:///C:/x` -> `C:/x`；`file://server/share` -> `\\server\share`）。
    /// 取不出（不是本地类）返回空串。纯函数。
    /// </summary>
    public static string LocalSpecPath(string? spec)
    {
        string s = (spec ?? "").Trim();
        if (!IsLocalSourceSpec(s)) return "";
        s = Regex.Replace(s, @"^(link|file):", "", RegexOptions.IgnoreCase);
        if (s.StartsWith("///", StringComparison.Ordinal)) s = s.Substring(3);      // file:///C:/x -> C:/x
        else if (s.StartsWith("//", StringComparison.Ordinal))                      // file://…
        {
            string rest = s.Substring(2);
            // 盘符形态（file://C:/x）去掉那两条斜杠；其余按 UNC 还原（file://server/share -> \\server\share）
            s = Regex.IsMatch(rest, @"^[A-Za-z]:[\\/]") ? rest : @"\\" + rest;
        }
        return s.Trim();
    }

    /// <summary>
    /// 「本地插件目录」的解析结果。三个字段把"能不能打开 / 打开哪儿 / 为什么不能"一次说清，
    /// 免得上层各自拼字符串（也就不会有人漏掉越界这一档而写出一句误导的话）。
    /// </summary>
    public readonly record struct LocalDirResult(string Dir, bool Rejected, string Reason)
    {
        /// <summary>越界拒绝（路径不落在允许的根之下）—— 界面/日志据此给中性提示。</summary>
        public bool Ok => Dir.Length > 0;
    }

    /// <summary>
    /// 解析本地链接插件实际该打开的目录，并做两道安全校验；任何一道不过都返回空目录
    /// （界面据此保持不可点，绝不"退而求其次"打开一个没校验过的路径）。
    ///
    /// 取值顺序（前者拿不到才看后者）：
    ///   ① 声明里的路径，按 profile 目录解析（`link:./plugins/x` 相对的是 profile 根）；
    ///   ② 包的实际目录 <c>&lt;profile&gt;\node_modules\&lt;包名&gt;</c>
    ///      —— 现场那个 Junction 就指向真实目录，这条能兜住"声明是相对的、包却被挪走"的情形。
    ///
    /// 安全边界（两条都必须过）：
    ///   · 候选目录必须真的存在（Directory.Exists）—— 不存在就不打开，也不猜；
    ///   · 归一化（GetFullPath）后必须落在允许根之下：profile 目录，或 &lt;profile&gt;\node_modules。
    ///     按前缀比对并带上分隔符，因此 `…\web-evil` 不会被误当成 `…\web` 的子目录；
    ///     前缀比对同时消掉了 `..` 穿越与"rooted 参数吃掉前缀"的陷阱 ——
    ///     `Path.Combine(profile, "D:\\other")` 会把 profile 整个丢掉，只靠 Combine 拼接挡不住穿越，
    ///     所以一律以归一化后的绝对路径作判据，而不是以拼接过程作判据。
    /// 两条都被拒时 <see cref="LocalDirResult.Rejected"/> 为 true（与"目录不在"区分开，提示口径不同）。
    /// </summary>
    public static LocalDirResult ResolveLocalPluginDir(string? spec, string? packageName, string? profileDir = null)
    {
        try
        {
            string root = string.IsNullOrWhiteSpace(profileDir) ? ProfileDir : profileDir!.Trim();
            if (root.Length == 0) return new LocalDirResult("", false, "profile 目录未知，无法定位本地插件目录");

            string profileFull;
            try { profileFull = Path.GetFullPath(root); }
            catch { return new LocalDirResult("", false, "profile 目录无法归一化，拒绝打开"); }

            string nodeModulesFull;
            try { nodeModulesFull = Path.GetFullPath(Path.Combine(profileFull, "node_modules")); }
            catch { return new LocalDirResult("", false, "node_modules 路径无法归一化，拒绝打开"); }

            bool anyRejected = false;
            string firstReason = "";

            // ① 声明里的路径，按 profile 目录解析
            string rel = LocalSpecPath(spec);
            if (rel.Length > 0)
            {
                string cand = TryUnderRoots(rel, profileFull, nodeModulesFull, ref anyRejected, ref firstReason);
                if (cand.Length > 0) return new LocalDirResult(cand, false, "");
            }

            // ② 退回"包实际所在的目录"：<profile>\node_modules\<包名>
            string n = (packageName ?? "").Trim();
            if (n.Length > 0 && IsValidPackageName(n))
            {
                string pkgRel = Path.Combine("node_modules", n.Replace('/', Path.DirectorySeparatorChar));
                string cand = TryUnderRoots(pkgRel, profileFull, nodeModulesFull, ref anyRejected, ref firstReason);
                if (cand.Length > 0) return new LocalDirResult(cand, false, "");
            }

            return anyRejected
                ? new LocalDirResult("", true, firstReason.Length > 0
                    ? firstReason : "本地插件目录越出插件目录范围，已拒绝打开")
                : new LocalDirResult("", false, "本机没有该插件的目录（未安装或已被移动）");
        }
        catch (Exception ex)
        {
            Logger.LogError("PluginManager.ResolveLocalPluginDir", ex);
            return new LocalDirResult("", false, "解析本地插件目录时出错，未打开任何目录");
        }
    }

    /// <summary>
    /// 把一段（常见的相对）路径按 <paramref name="root"/> 解析成一个已校验的绝对目录：
    /// 归一化 -> 必须落在允许根之下且目录存在 -> 返回它；否则返回空串。
    /// 越界时置 <paramref name="rejected"/> 并记下首条理由（供上层区分"目录不在"与"越界拒绝"）。
    /// </summary>
    private static string TryUnderRoots(string relative, string root, string nodeModulesRoot,
                                        ref bool rejected, ref string firstReason)
    {
        string full;
        try
        {
            // root 已归一化（绝对、无尾分隔符）-> Combine 在此只做拼接；
            // 万一 relative 是 rooted（`D:\other`），Combine 会丢掉 root —— 那正是下面的前缀校验要挡的。
            full = Path.GetFullPath(Path.Combine(root, relative));
        }
        catch (Exception ex)
        {
            Logger.LogError("PluginManager.TryUnderRoots(GetFullPath)", ex);
            rejected = true;
            if (firstReason.Length == 0) firstReason = "本地插件目录无法归一化，拒绝打开";
            return "";
        }

        // 归一化之后再比前缀（拿 absolute 当判据，不拿拼接过程当判据）：
        // 剥掉一层引号是为了 `link:"…"` 这类写法不留下一对引号把自己判成越界。
        string trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                             .Trim('"').TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        bool underProfile = trimmed.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        bool underNodeModules = trimmed.StartsWith(nodeModulesRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                             || trimmed.Equals(nodeModulesRoot, StringComparison.OrdinalIgnoreCase);

        if (!underProfile && !underNodeModules)
        {
            rejected = true;
            if (firstReason.Length == 0)
                firstReason = "该目录不在允许打开的插件目录范围之内，已拒绝打开";
            return "";
        }

        return Directory.Exists(full) ? full : "";
    }

    /// <summary>
    /// 把一份来源声明拆成（站点, 仓库路径）：路径已去掉结尾 <c>.git</c> 与 <c>#ref</c>。
    /// 认不出来返回 ("","")。纯函数，便于自检。
    /// </summary>
    public static (string Host, string Path) ParseRepoSpec(string? spec)
    {
        try
        {
            string s = PluginSource.RepoWithoutRef((spec ?? "").Trim());   // 先去掉 #ref
            if (s.Length == 0) return ("", "");

            // 非 git 协议（本地路径 / 工作区 / npm 别名）：不给任何网页链接
            if (Regex.IsMatch(s, @"^(file|link|portal|workspace|npm):", RegexOptions.IgnoreCase)) return ("", "");

            if (s.StartsWith("git+", StringComparison.OrdinalIgnoreCase)) s = s.Substring(4);

            // 短写：github:o/r（gitee / gitlab / bitbucket 同理）
            var shortForm = Regex.Match(s, @"^(github|gitlab|bitbucket|gitee):(.+)$", RegexOptions.IgnoreCase);
            if (shortForm.Success)
                return NormalizeHostPath(HostAlias(shortForm.Groups[1].Value), shortForm.Groups[2].Value);

            // ssh://git@host[:port]/o/r.git
            var ssh = Regex.Match(s, @"^ssh://(?:[^@/]+@)?([^/:]+)(?::\d+)?/(.+)$", RegexOptions.IgnoreCase);
            if (ssh.Success) return NormalizeHostPath(ssh.Groups[1].Value, ssh.Groups[2].Value);

            // git@host:o/r.git（scp 写法）。两条约束缺一不可：
            //   · `(?!//)`：否则 `https://github.com/o/r.git` 会被这里的 host 正则抢成 host="https"；
            //   · host 必须带点（真实域名）：否则 Windows 盘符路径 `D:/foo/bar` 会被当成 host="D"，
            //     拼出一个 https://d/foo/bar 这样的假链接。
            var scp = Regex.Match(s, @"^(?:[^@/]+@)?([A-Za-z0-9._-]*\.[A-Za-z0-9._-]+):(?!//)(.+)$");
            if (scp.Success) return NormalizeHostPath(scp.Groups[1].Value, scp.Groups[2].Value);

            // http(s):// / git://host[:port]/o/r(.git)
            var url = Regex.Match(s, @"^(?:https?|git)://(?:[^@/]+@)?([^/:]+)(?::\d+)?/(.+)$", RegexOptions.IgnoreCase);
            if (url.Success) return NormalizeHostPath(url.Groups[1].Value, url.Groups[2].Value);

            return ("", "");
        }
        catch { return ("", ""); }
    }

    /// <summary>短写前缀 -> 真实站点；认不出来的原样返回（交给后面统一小写）。</summary>
    private static string HostAlias(string shortName) => (shortName ?? "").Trim().ToLowerInvariant() switch
    {
        "github" => "github.com",
        "gitlab" => "gitlab.com",
        "bitbucket" => "bitbucket.org",
        "gitee" => "gitee.com",
        _ => (shortName ?? "").Trim().ToLowerInvariant()
    };

    /// <summary>站点名小写；路径去首尾斜杠、去结尾 .git；不足 owner/repo 两段就不认。</summary>
    private static (string Host, string Path) NormalizeHostPath(string host, string path)
    {
        host = (host ?? "").Trim().ToLowerInvariant();
        path = (path ?? "").Trim().Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path.Substring(0, path.Length - 4);
        path = path.TrimEnd('/');
        if (host.Length == 0 || path.Length == 0) return ("", "");
        return path.Contains('/') ? (host, path) : ("", "");
    }

    /// <summary>
    /// 仓库归属（站点路径的第一段：owner / 组织名）。仅供"包内没写 author"时的兜底显示，
    /// 它不是作者本人，界面必须标明「来自仓库地址」。认不出来返回空串。
    /// </summary>
    public static string RepoOwnerFromSpec(string? spec)
    {
        var (host, path) = ParseRepoSpec(spec);
        if (host.Length == 0) return "";
        int i = path.IndexOf('/');
        return i > 0 ? path.Substring(0, i) : "";
    }

    /// <summary>扫描 profile 依赖，附带版本 / 作者 / 兼容性 / 启用状态。</summary>
    public static List<Plugin> Scan(string currentDshVersion)
    {
        var list = new List<Plugin>();
        try
        {
            if (!File.Exists(PackageFile)) return list;
            using var doc = JsonDocument.Parse(File.ReadAllText(PackageFile));
            if (!doc.RootElement.TryGetProperty("dependencies", out var deps)) return list;

            var disabled = ReadDisabledIds();

            foreach (var d in deps.EnumerateObject())
            {
                string name = d.Name;
                // 声明值一律安全读取：GetString() 对非字符串（数字 / 对象）会抛，
                // 而这里在 foreach 体内、抛出去会让整轮扫描中断（原实现靠 DepSpec 容错）。
                string declaredSpec = d.Value.ValueKind == JsonValueKind.String ? (d.Value.GetString() ?? "") : "";
                var p = new Plugin { Name = name, CheckedAgainst = currentDshVersion };
                string modDir = Path.Combine(ProfileDir, "node_modules", name.Replace('/', Path.DirectorySeparatorChar));
                p.Dir = modDir;
                string authorField = "";

                string mp = Path.Combine(modDir, "package.json");
                if (File.Exists(mp))
                {
                    try
                    {
                        using var mdoc = JsonDocument.Parse(File.ReadAllText(mp));
                        var m = mdoc.RootElement;
                        if (m.TryGetProperty("version", out var v)) p.Version = v.GetString() ?? "?";
                        if (m.TryGetProperty("description", out var de)) p.Description = de.GetString() ?? "";
                        authorField = ExtractAuthor(m);
                        (p.Requirement, p.RequirementSource) = ExtractRequirement(m);
                        p.Compatibility = EvaluateBand(p.Requirement, currentDshVersion);
                        p.RepositoryUrl = ExtractRepoUrl(m);
                        p.Homepage = GetStringProp(m, "homepage");
                    }
                    catch { }
                }
                else p.Version = "(未安装)";

                // 作者兜底：现场两个 git 源包的 package.json 里连 author / repository 都没有
                // （只有 name / version / description），于是界面只能显示「—」——
                // 而清单里的声明本身就是仓库地址，owner 是现成的、有信息量的线索。
                // 只在"包内没写 author"时兜底，且必须标明这是仓库归属（AuthorFromRepo=true，
                // 悬停另写说明），不能让它冒充作者本人。
                //   · git 源：先看清单声明里的仓库（它磁盘上多半没有 repository 字段），
                //     再看包内 repository；
                //   · npm 包：只认包内 repository（npm 版本范围不是仓库地址，不能拿去解析）。
                // 两条路都拿不到 owner 时给占位「—」，绝不编一个名字出来 —— 这是既有行为，未改。
                bool gitDeclared = IsGitSpec(declaredSpec);
                var (author, fromRepo) = ResolveAuthor(
                    authorField,
                    gitDeclared ? declaredSpec : "",
                    p.RepositoryUrl);
                p.Author = author.Length > 0 ? author : Plugin.AuthorUnknown;
                p.AuthorFromRepo = fromRepo;

                p.Disabled = disabled.Contains(name) || (p.LoaderId != null && disabled.Contains(p.LoaderId));
                list.Add(p);
            }
        }
        catch (Exception ex) { Logger.LogError("PluginManager.Scan", ex); }
        return list.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// 拿到 loader id 之后重算「已禁用」标记：部分插件以 loader id 禁用
    /// （例如 dsh-zh 对应 deepseek-harness-zh_pro），Scan 阶段尚不知晓 id，必须补算一次。
    /// </summary>
    public static void RefreshDisabledFlags(IEnumerable<Plugin> plugins)
    {
        var disabled = ReadDisabledIds();
        foreach (var p in plugins)
            p.Disabled = disabled.Contains(p.Name) || (p.LoaderId != null && disabled.Contains(p.LoaderId));
    }

    // ══════════════ 「是否已禁用」以引擎自己的视图为准（纯函数，便于自检） ══════════════
    //
    // 【现场 bug】用户将 dsh-client-auto-continue 禁用（用于验证禁用是否真正生效），随后在网页/插件市场
    // 将其重新启用 —— 返回守护壳后，卡片仍显示已禁用、按钮仍为「启用插件」。
    //
    // 根因是"两边各记各的"：
    //   · 守护壳这一侧读的是 cordis.patch.yml 里的禁用记录（ReadDisabledIds）；
    //   · 网页/市场那一侧走的是它自己的开关，它并不把结果写回 patch 文件。
    // 于是 patch 中那条 `- id: "auto-continue" / disabled: true` 始终保留，
    // 而引擎早就把它启用了 -> 壳与引擎天然会不同步。
    // 本机 patch 文件原文（…\.dsh\profiles\web\cordis.patch.yml 尾部）：
    //   # DSHGuard 于 2026-09-16 00:33:51 禁用（备份 cordis.patch.yml.bak-20260916-003351）
    //   - id: "auto-continue"
    //     disabled: true
    //
    // 唯一的权威事实在引擎自己的视图里：守护壳为了取加载器标识本来就会跑一次
    // `--dump-config`，那份输出的每个条目都带 `name:`，被禁用的还带 `disabled: true` ——
    // 也就是说同一次输出里就能拿到"哪些插件此刻实际处于禁用状态"。
    // 本机实测输出片段（Logs\异常-20260916-122458.log，退出码=0，头尾各留了一段）：
    //   stdout 头尾：# == @deepseek-ai/dsh-base
    //   - id: timer
    //     name: '@deepseek-ai/cordis-plugin-timer'
    //   - id: hmr
    //     name: '@deepseek-ai/cordis-plugin-hmr'
    //     disabled: true
    //     config:
    //       root:
    //         - .
    //   - id: llm
    //     name: '@
    // 所以下面这一层把"引擎视图"当成唯一事实来源，patch 记录只在拿不到引擎视图时降级使用。

    /// <summary>
    /// 把 <c>disabled:</c> 这一行的值判成"确实禁用了"（纯函数）。只认字面量 true：
    ///   · <c>disabled: true</c> / <c>disabled: 'true'</c> / <c>disabled: "true"</c> -> true
    ///   · <c>disabled: true  # 说明</c> -> true（行尾注释先剥掉）
    ///   · <c>disabled: !!js …</c>          -> false（YAML 标签表达式，值不是字面量 true；
    ///                                        本程序不解析 JS，宁可不判定，也不能把它当成"已禁用"）
    ///   · <c>disabled: false</c> / 空 / 其它 -> false
    ///
    /// 两个"必须这么写"的细节（真机自检抓出来的，不是理论推测）：
    ///   ① 先 <see cref="StripInlineComment"/> 再判 —— 否则 `disabled: true  # …` 会判成 false
    ///      （本程序自己写入 patch 的行即带行尾注释，dump 中也可能带）；
    ///   ② 剥完注释后两边都要 <c>Trim()</c> —— 调用方可能传带缩进的整行（自检就是这么调的），
    ///      只按 <c>StartsWith("disabled:")</c> 判会把带缩进的整行判成 false。
    /// </summary>
    public static bool IsDisabledTrue(string? line)
    {
        string rest = StripInlineComment(line ?? "").Trim();
        if (!rest.StartsWith("disabled:", StringComparison.Ordinal)) return false;
        string v = rest.Substring("disabled:".Length).Trim().Trim('\'', '"');
        return v.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 去掉行尾的 <c># 注释</c>（纯函数）：注释起点必须是"行首或前面是空白"的 <c>#</c>，
    /// 且不在引号里 —— 否则 <c>name: '@a/b#next'</c> 这种内嵌 <c>#</c> 会被误当注释截断。
    /// 保留行首缩进：调用方要用它判 YAML 层级，缩进一丢层级就没了。
    /// </summary>
    public static string StripInlineComment(string? line)
    {
        string s = line ?? "";
        bool inSingle = false, inDouble = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\'' && !inDouble) inSingle = !inSingle;
            else if (c == '"' && !inSingle) inDouble = !inDouble;
            else if (c == '#' && !inSingle && !inDouble && (i == 0 || char.IsWhiteSpace(s[i - 1])))
                return s.Substring(0, i);      // 只砍掉注释，缩进原样留在这段里
        }
        return s;
    }

    /// <summary>
    /// 从 <c>dsh --dump-config</c> 的输出里收集引擎视图下确实被禁用的条目标识（纯函数，便于自检）：
    /// 每个 <c>- id: …</c> 条目下，与它同层或更深缩进的 <c>disabled: true</c> 才算数。
    ///
    /// 为什么必须看缩进（沿用本类解析器一贯的谨慎风格）：
    ///   · 只看"最近一个 id 之后出现 disabled: true"就会跨条目串行 ——
    ///     文件末尾/相邻条目里任何一条 <c>disabled: true</c> 都会被算到前面那个 id 头上；
    ///   · 缩进浅于本条目的 <c>disabled:</c> 属于外层（bundle / 分组 / 别的条目），不是它的。
    ///   · <c>disabled: !!js …</c> 是表达式而不是字面量 true -> 一律不算"已禁用"（见 <see cref="IsDisabledTrue"/>）。
    ///
    /// 返回值区分两件不同的事（调用方必须分开对待，不许混成一句"没有禁用项"）：
    ///   · <c>null</c>       —— 这份输出根本不是引擎视图（执行失败 / 为空 / 不含任何 <c>- id:</c> 条目）
    ///                          -> 调用方必须降级到 patch 记录并如实说明；
    ///   · 空集合            —— 是引擎视图，且此刻没有任何插件被禁用（属确定事实，不可与"无法读取"混同）。
    /// </summary>
    public static HashSet<string>? ParseDisabledIds(string? dump)
    {
        try
        {
            string text = dump ?? "";
            if (text.IndexOf("- id:", StringComparison.Ordinal) < 0) return null;   // 不是引擎视图

            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool inEntry = false;
            int entryIndent = 0;
            string currentId = "";

            foreach (var raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                string body = StripInlineComment(line).TrimEnd();
                if (body.Trim().Length == 0) continue;

                int indent = body.Length - body.TrimStart().Length;
                string t = body.Trim();

                if (t.StartsWith("- id:", StringComparison.Ordinal))
                {
                    currentId = NormalizeId(t.Substring("- id:".Length).Trim());
                    entryIndent = indent;
                    inEntry = currentId.Length > 0;
                    continue;
                }

                if (!inEntry) continue;
                if (indent < entryIndent) { inEntry = false; continue; }   // 已离开这个条目（更浅的兄弟/外层）

                // 与本条目同层或更深的 disabled: true -> 这条 id 确实被引擎禁用了
                if (IsDisabledTrue(t)) set.Add(currentId);
            }
            return set;
        }
        catch (Exception ex)
        {
            Logger.LogError("PluginManager.ParseDisabledIds", ex);
            return null;      // 解析炸了 = 拿不到引擎视图，绝不假装"没有禁用项"
        }
    }

    /// <summary>
    /// 「引擎视图 vs 本地 patch 记录」的对账结果（纯函数，便于自检）：
    /// <see cref="EngineIds"/> 是引擎说禁用了的、<see cref="PatchIds"/> 是 patch 文件里记着的。
    /// 两边不一致时以引擎为准（见 <see cref="ApplyDisabledFlagsFromEngine"/>），这里只负责把差异说清楚。
    /// </summary>
    public readonly struct DisabledSync
    {
        public bool EngineView { get; }
        /// <summary>引擎说"禁用了"、patch 里却没记（＝网页/市场那边关的，壳必须跟着显示已禁用）。</summary>
        public IReadOnlyList<string> EngineOnly { get; }
        /// <summary>patch 里记着、引擎却说"没禁用"（＝网页/市场那边重新启用了，壳必须跟着显示启用中）。</summary>
        public IReadOnlyList<string> PatchOnly { get; }
        /// <summary>对账覆盖的条数（引擎集合 ∪ patch 集合）。</summary>
        public int Compared { get; }

        public DisabledSync(bool engineView, IReadOnlyList<string> engineOnly,
                            IReadOnlyList<string> patchOnly, int compared)
        {
            EngineView = engineView;
            EngineOnly = engineOnly ?? Array.Empty<string>();
            PatchOnly = patchOnly ?? Array.Empty<string>();
            Compared = compared;
        }

        /// <summary>引擎视图与 patch 记录完全对得上（没有漂移）。</summary>
        public bool InSync => EngineOnly.Count == 0 && PatchOnly.Count == 0;
        /// <summary>有没有漂移（有 -> 事件栏会报一条"已按引擎同步"）。</summary>
        public bool Drifted => !InSync;
        /// <summary>
        /// 这次实际调整了几项（两个方向之和）。事件栏只报这个数字、不列名字 ——
        /// 名字（哪几个、各是什么方向）走 <c>Logger.NoteDiagnosis</c> 落盘留证。
        /// </summary>
        public int AdjustedCount => EngineOnly.Count + PatchOnly.Count;
    }

    /// <summary>
    /// 把「引擎视图里的禁用标识」与「patch 文件里的禁用记录」对一次账（纯函数，不写盘）。
    /// <paramref name="engineIds"/> 为 null 表示"没拿到引擎视图" -> <c>EngineView=false</c>、
    /// 两个差异列表都为空（这时谈"漂移"没有意义，调用方该说的是"当前状态来自本地记录"）。
    /// </summary>
    public static DisabledSync CompareEngineToPatch(IEnumerable<string>? engineIds, IEnumerable<string>? patchIds)
        => CompareEngineToPatch(engineIds, patchIds, null);

    /// <summary>
    /// 同上，但只对账我们管的插件（<paramref name="managedAliases"/>，见
    /// <see cref="ManagedPluginAliases"/>）：引擎自带条目（<c>@deepseek-ai/*</c> 之类不在
    /// profile 清单里的）不参与比对、也不进差异列表。
    ///
    /// 为什么必须加这一层（现场反馈）：引擎基座自己会禁用一批内部插件（<c>hmr</c>、
    /// <c>compaction-basic</c>、<c>command-compact</c>、<c>plan-mode</c>、<c>tool-bash</c>、
    /// <c>tool-fs</c>、<c>mnemon-strategy-*</c> …），patch 里当然没有它们的记录 -> 全被算成
    /// "引擎独有"的漂移，事件栏每次刷新都占满内部标识，既产生冗余信息，又向普通用户暴露内部实现。
    /// <paramref name="managedAliases"/> 为 null -> 不做过滤（与单参数重载同行为，老自检不许回归）。
    /// </summary>
    public static DisabledSync CompareEngineToPatch(IEnumerable<string>? engineIds, IEnumerable<string>? patchIds,
                                                   ISet<string>? managedAliases)
    {
        var eng = new HashSet<string>(
            (engineIds ?? Enumerable.Empty<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()),
            StringComparer.OrdinalIgnoreCase);
        var pat = new HashSet<string>(
            (patchIds ?? Enumerable.Empty<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()),
            StringComparer.OrdinalIgnoreCase);

        bool engineView = engineIds != null;
        if (!engineView) return new DisabledSync(false, Array.Empty<string>(), Array.Empty<string>(), 0);

        // 「我们管的」＝ 清单里登记的包名 ∪ 它们的 loader id。两边都认：
        // patch 记录按 loader id 写、引擎视图里也是 id，而清单里是包名。
        if (managedAliases != null)
        {
            eng.RemoveWhere(s => !managedAliases.Contains(s));
            pat.RemoveWhere(s => !managedAliases.Contains(s));
        }

        var engineOnly = eng.Except(pat, StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        var patchOnly = pat.Except(eng, StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        return new DisabledSync(true, engineOnly, patchOnly, eng.Union(pat, StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>
    /// 「我们管的插件」的全部别名（包名 + loader id）——漂移比对的白名单（纯函数，便于自检）：
    /// 取自 profile 清单（<c>package.json</c> 的 <c>dependencies</c>）里登记的每个包，
    /// 加上它当前已知的 loader id（<c>cordis:group</c> 这类分组条目没有可是无所谓）。
    ///
    /// 引擎自带条目（不在清单里的 <c>@deepseek-ai/*</c>）自然不在这个集合里 -> 一律不参与比对。
    /// 读不成清单（文件不在/不是合法 JSON）-> 返回空集合，调用方据此一条比对都不做
    /// （宁可本轮不报告，也不使用兜底数据，避免产生误导信息）。
    /// </summary>
    public static ISet<string> ManagedPluginAliases(IEnumerable<Plugin>? plugins)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (plugins == null) return set;

            // 清单只读一次（每个插件各调一次 HasDependency 会把 package.json 读 N 遍）
            var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(PackageFile))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(PackageFile));
                    if (doc.RootElement.TryGetProperty("dependencies", out var deps) &&
                        deps.ValueKind == JsonValueKind.Object)
                        foreach (var d in deps.EnumerateObject()) declared.Add(d.Name);
                }
            }
            catch (Exception ex) { Logger.LogError("PluginManager.ManagedPluginAliases(清单)", ex); }

            // 清单读不成（文件不在 / 不是合法 JSON）-> 返回空集合，调用方据此一条比对都不做。
            if (declared.Count == 0) return set;

            foreach (var p in plugins)
            {
                if (p == null || string.IsNullOrWhiteSpace(p.Name)) continue;
                if (!declared.Contains(p.Name.Trim())) continue;      // 不在清单里 = 不是我们管的
                set.Add(p.Name.Trim());
                if (!string.IsNullOrWhiteSpace(p.LoaderId)) set.Add(p.LoaderId!.Trim());
            }
        }
        catch (Exception ex) { Logger.LogError("PluginManager.ManagedPluginAliases", ex); }
        return set;
    }

    /// <summary>
    /// 「状态刷新」事件栏那一句的唯一生成入口（纯函数，便于自检）：只报大概结果、不列名字。
    /// 本程序管理的插件无漂移 -> 返回空串 -> 调用方不产生任何事件（避免冗余信息）。
    /// 细节（哪几个、各是什么方向）不进界面，走 <c>Logger.NoteDiagnosis</c> 落盘。
    /// </summary>
    public static string DriftEventText(int adjusted)
        => adjusted > 0 ? $"插件状态已按引擎同步（{adjusted} 项调整）" : "";

    /// <summary>
    /// 漂移细节的落盘文案（上屏的那句只有数字，名字全在这里）：
    /// <c>patch 记着禁用、引擎说启用</c>（＝网页/市场那边重新开了）与反向各一段。
    /// </summary>
    public static string DriftDetailText(in DisabledSync drift)
    {
        var parts = new List<string>();
        if (drift.EngineOnly.Count > 0)
            parts.Add($"引擎禁用、本地无记录（{drift.EngineOnly.Count} 项）：{string.Join("、", drift.EngineOnly)}");
        if (drift.PatchOnly.Count > 0)
            parts.Add($"本地记着禁用、引擎实为启用（{drift.PatchOnly.Count} 项）：{string.Join("、", drift.PatchOnly)}");
        return parts.Count > 0 ? string.Join("；", parts) : "（无漂移）";
    }

    /// <summary>
    /// 设置每个插件「是否已禁用」的唯一入口（纯函数，便于自检）：按 <paramref name="engineIds"/>
    /// 把标记重设一遍（既设 true 也设回 false —— 只置位不清位，就是现场那个"网页启用了、壳还显示禁用"）。
    ///
    /// <paramref name="engineIds"/>=null（dump 失败 / 输出不是引擎视图）-> 退回
    /// <see cref="PatchDisabledIds"/>（patch 记录口径，= 原有的 <see cref="RefreshDisabledFlags"/> 行为），
    /// 由调用方在事件栏注明"当前状态来自本地记录，可能与引擎不一致"。
    /// </summary>
    /// <returns>本次是按引擎视图判的（true）还是按本地记录降级判的（false）。</returns>
    public static bool ApplyDisabledFlagsFromEngine(IEnumerable<Plugin>? plugins, ISet<string>? engineIds,
                                                   ISet<string>? patchDisabledIds = null)
    {
        try
        {
            if (plugins == null) return engineIds != null;
            var known = engineIds ?? patchDisabledIds ?? ReadDisabledIds();
            foreach (var p in plugins)
            {
                if (p == null) continue;
                p.Disabled = known.Contains(p.Name)
                          || (!string.IsNullOrWhiteSpace(p.LoaderId) && known.Contains(p.LoaderId!));
            }
            return engineIds != null;
        }
        catch (Exception ex)
        {
            Logger.LogError("PluginManager.ApplyDisabledFlagsFromEngine", ex);
            return false;
        }
    }

    /// <summary>patch 记录口径的禁用标识（<see cref="ReadDisabledIds"/> 的 ISet 视图，供上面的纯函数注入用）。</summary>
    public static ISet<string> PatchDisabledIds() => ReadDisabledIds();

    /// <summary>
    /// 拿不到引擎视图（dump 失败、走了缓存兜底）时，事件栏必须补的那句人话（纯函数，便于自检）。
    /// 措辞要求：小白看得懂、不出现任何命令行写法。
    ///
    /// 走降级口径时顺带捎上"配置文件结构异常"的告知（<see cref="PatchConfigWarning"/>）：
    /// 这两句是同一个意思 —— 都是"不可完全采信这份状态"的提醒，而且结构异常时 <see cref="ReadDisabledIds"/>
    /// 读到的禁用状态本来就不可信（引擎整份读不了）。只告知、不擅自改写用户文件。
    ///
    /// 注意：刻意只在降级分支拼接：engineView=true 时引擎给的就是权威答案、不需要这句提醒，
    ///   而且这样本函数对"engineView=true"仍然恒为空串（既有断言 `DisabledFallbackNote(true) == ""`
    ///   不会因为某台机器上 patch 文件恰好是坏的而变红 —— 自检不该依赖用户磁盘状态）。
    /// </summary>
    public static string DisabledFallbackNote(bool engineView)
    {
        if (engineView) return "";
        return "当前状态来自本地记录，可能与引擎不一致；点「刷新」可重新核对。" + PatchConfigWarning();
    }

    /// <summary>
    /// 把 id 变成 YAML 安全的双引号标量：`@scope/name` -> `"@scope/name"`。
    ///
    /// 为什么必须加：YAML 里 `@` 与 `` ` `` 是保留指示符，不能作为纯量开头。
    /// 不加引号写出去的 `- id: @a/b` 是非法 YAML，会让整份 cordis.patch.yml 解析失败
    /// （现场已发生：插件被误判为禁用、客户端侧栏与监视台异常）。
    /// </summary>
    public static string QuoteId(string? id)
    {
        string s = (id ?? "").Trim();
        return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    /// <summary>
    /// 读回 id：剥掉外层单/双引号并还原转义。比较与删除一律先过这里，
    /// 于是「历史写入的未加引号记录」与「现在写入的带引号记录」都能认、都能删。
    /// </summary>
    public static string NormalizeId(string? raw)
    {
        string s = (raw ?? "").Trim();
        if (s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'')))
            s = s.Substring(1, s.Length - 2);
        return s.Replace("\\\"", "\"").Replace("\\\\", "\\").Trim();
    }

    /// <summary>从 `- id: xxx` 这一行里取出 id（统一走 NormalizeId，兼容带/不带引号）。</summary>
    public static string? IdFromLine(string? line)
    {
        var t = (line ?? "").Trim();
        if (!t.StartsWith("- id:", StringComparison.Ordinal)) return null;
        return NormalizeId(t.Substring("- id:".Length).Trim());
    }

    /// <summary>YAML 保留指示符：以这些字符开头的纯量必须加引号。</summary>
    private const string YamlReservedStarts = "@`*&!%{[,#>|";

    /// <summary>
    /// 写模板时的引导注释（两处「文件不存在先写模板」共用，避免两处措辞再次漂移）。
    /// 故意只有注释、没有 <c>[]</c>：见 <see cref="ValidatePatchStructure"/>。
    /// 空文档既合法又无害（<c>yaml.load</c> -> null），于是"先写模板再追加"不再有脏中间态：
    /// 追加块本身就以 <c>\n</c> 开头，<c>TrimEnd() + "\n" + block</c> -> 注释与 <c>- id:</c> 仍各占一行。
    /// </summary>
    private const string PatchTemplate =
        "# Your patch layer for this dsh profile, applied after every bundle layer:\n";

    /// <summary>
    /// <c>[]</c> / <c>[ ]</c>（独立成行的空流式序列，即引擎模板里的占位符）。
    /// </summary>
    private static readonly Regex TopLevelFlowListLine = new(@"^\[\s*\]$", RegexOptions.Compiled);

    /// <summary>
    /// 结构校验（本项目零依赖、不引入 YAML 库）：拦住"把块序列追加到 <c>[]</c> 后面"这类整份文件作废的写法。
    ///
    /// 【为什么必须单独有一道】现场事故：patch 文件不存在时先写模板（末行是空流式序列 <c>[]</c>），
    /// 紧接着把块序列追加进去 ->
    /// <code>
    /// # Your patch layer for this dsh profile, applied after every bundle layer:
    /// []
    /// - id: "xxx"
    ///   disabled: true
    /// </code>
    /// 一个文档里出现两个顶层节点（空的 flow sequence + block sequence）-> 引擎用的 YAML 4 方言
    /// 直接抛「expected a single document in the stream」、整份 patch 不生效。用 profile 自带的
    /// <c>yaml@2.9.0</c> 实测：模板+单条=7 个错误、多条=14 个错误，而正文里已有条目时反而合法。
    /// 而 <see cref="ValidatePatchText"/> 只看 <c>- id:</c> 行的引号、会跳过 <c>[]</c> 这一行，
    /// 于是写后校验"通过"、界面回「已禁用」，用户却拿到一个引擎读不了的文件 —— 正是本次要堵的洞。
    ///
    /// 【判据（只看结构，不碰引号/缩进等 <see cref="ValidatePatchText"/> 的口径）】
    ///   ① 顶层（第 0 列）出现 <c>[]</c> / <c>[ ]</c> 之类的空流式序列之后，又出现顶层块序列项 <c>- …</c>；
    ///   ② 顶层出现非空流式集合起头（<c>[1, 2]</c> / <c>{a: 1}</c>）之后，又出现顶层块序列项 ——
    ///      同理属于"同一文档里两个顶层节点"。
    /// 两种都是同一个根因：追加的块序列与已存在的顶层 flow 节点并列，文档只能有一个根。
    ///
    /// 【刻意不做的判定（宁松勿误伤，见自检 47-b/c/d）】
    ///   · 只有注释、没有 <c>[]</c> -> 合法（空白源 <c>yaml.load</c> -> null，是"没有这一层"，不是错误）；
    ///   · <c># []</c> / 单引号/双引号里含 <c>[]</c> / 行尾注释 / 缩进在块里的 <c>[]</c>
    ///     （如 <c>- id: x</c> + 缩进 <c>disabled: true</c>）-> 都不算顶层流式占位符；
    ///   · <c>- config: [1, 2]</c>（值本身是流式、行首是 <c>- </c>）-> 合法；
    ///   · <c>- id: "a"</c> 之后紧跟顶层的 <c>- id: b</c>（多条目）-> 合法；
    ///   · 不要求文件"必须有条目"、不要求引号规范 —— 交给 <see cref="ValidatePatchText"/>。
    /// 命中即拒绝写入（不自动改写用户文件）：由调用方如实告知用户，而不是谎报成功。
    /// </summary>
    /// <returns>结构合法 / 中性中文原因（首条问题，给界面直接显示）。</returns>
    public static (bool Ok, string Reason) ValidatePatchStructure(string? text)
    {
        // 空/null 不崩且合法：没有这一层内容，谈不上结构错误
        string src = text ?? "";
        var lines = src.Split('\n');

        bool openedByTopLevelFlow = false;      // 顶层已出现 flow 形态（[] 或 [1,2] / {a: 1}）
        string opener = "";
        for (int i = 0; i < lines.Length; i++)
        {
            // 行尾注释去掉（引号外的 #）：`disabled: true  # [] 说明` 不该被当成流式占位符。
            // 本程序自己写入的 id 只会有 \" / \\ 两种转义，故引号成对扫描足够。
            string line = StripTrailingComment(lines[i]).TrimEnd();
            string t = line.Trim();
            if (t.Length == 0 || t.StartsWith("#")) continue;

            // 「顶层」= 第 0 列就有内容（缩进的行属于块里的值，不参与顶层节点的判定）
            bool topLevel = line.Length > 0 && line[0] != ' ' && line[0] != '\t';
            if (!topLevel) continue;

            // 顶层块序列项：`- id: x` / `- insert:` / `- config: [1, 2]`（值本身 flow 也算块项）
            if (line.StartsWith("-", StringComparison.Ordinal))
            {
                if (!openedByTopLevelFlow) continue;      // 正常的多条目写法，合法
                return (false,
                    $"第 {i + 1} 行是顶层条目「- …」，但它前面（{opener}）已是顶层值；"
                    + "一个 YAML 文档只能有一个顶层，这样写引擎会整份读不了。");
            }

            // 顶层 flow 节点：`[]` / `[ ]` / `[1, 2]` / `{a: 1}`（多行 flow 也算，首行即可判定）
            if (t[0] == '[' || t[0] == '{')
            {
                openedByTopLevelFlow = true;
                opener = TopLevelFlowListLine.IsMatch(t)
                    ? $"第 {i + 1} 行的空序列占位符「{t}」"
                    : $"第 {i + 1} 行的顶层流式值「{t}」";
            }
        }
        return (true, "");
    }

    /// <summary>
    /// 去掉行尾注释（引号之外的 <c>#</c>）。`#` 必须是行首或前面有空白才算注释起始
    /// （YAML 语义：`a#b` 里的 `#` 是普通字符）。
    /// </summary>
    private static string StripTrailingComment(string line)
    {
        bool inSingle = false, inDouble = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '\'' && !inDouble) inSingle = !inSingle;
            else if (c == '"' && !inSingle) inDouble = !inDouble;
            else if (c == '#' && !inSingle && !inDouble && (i == 0 || line[i - 1] == ' ' || line[i - 1] == '\t'))
                return line.Substring(0, i);
        }
        return line;
    }

    /// <summary>
    /// patch 文件当前是否结构上就坏了（<c>[]</c> + 块序列那种，历史版本写出来的坏文件）。
    /// 只读判定，不改写磁盘：本壳只如实告知用户，修不修由用户决定
    /// （自动改写用户配置的风险大于收益 —— 见 <see cref="PatchConfigWarning"/>）。
    /// </summary>
    public static bool PatchFileLooksBroken(out string reason)
    {
        reason = "";
        try
        {
            if (!File.Exists(PatchFile)) return false;
            var (ok, why) = ValidatePatchStructure(File.ReadAllText(PatchFile));
            reason = ok ? "" : why;
            return !ok;
        }
        catch (Exception ex) { Logger.LogError("PluginManager.PatchFileLooksBroken", ex); return false; }
    }

    /// <summary>
    /// 配置文件结构异常时给用户看的一句话（结构正常 / 文件不存在时为空串，调用方可直接拼接）。
    /// 措辞是中性的：说明"引擎读不了、请自行修或重新禁用"，不出现命令行写法。
    /// </summary>
    public static string PatchConfigWarning()
        => PatchFileLooksBroken(out var why)
            ? $"检测到插件配置文件结构异常，引擎可能读不了这份配置（{why}）建议先备份后修正，或删掉该文件后重新禁用一次。"
            : "";

    /// <summary>
    /// 定点校验（本项目零依赖、不引入 YAML 库）：只检查本程序识别的 `- id:` 行——
    /// 纯量以 YAML 保留字符开头却没加引号，就是非法写法。
    /// 返回：是否全部合法 / 修好后的文本 / 问题行描述。
    /// </summary>
    public static (bool Ok, string FixedText, List<string> Problems) ValidatePatchText(string? text)
    {
        var problems = new List<string>();
        string src = text ?? "";
        var lines = src.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string t = lines[i].Trim();
            if (!t.StartsWith("- id:", StringComparison.Ordinal)) continue;

            string rest = t.Substring("- id:".Length).Trim();
            if (rest.Length == 0) continue;
            if (rest[0] == '"' || rest[0] == '\'') continue;                 // 已加引号，合法
            if (YamlReservedStarts.IndexOf(rest[0]) < 0) continue;            // 普通字符开头，合法

            // 保留原行尾的注释（若有），只把纯量换成带引号的形式
            string tail = "";
            int hash = rest.IndexOf(" #", StringComparison.Ordinal);
            if (hash > 0) { tail = rest.Substring(hash).TrimEnd(); rest = rest.Substring(0, hash).Trim(); }

            string indent = lines[i].Substring(0, lines[i].Length - lines[i].TrimStart().Length);
            lines[i] = indent + "- id: " + QuoteId(rest) + (tail.Length > 0 ? " " + tail : "");
            problems.Add($"第 {i + 1} 行：- id: {rest}（{rest[0]} 是 YAML 保留字符，必须加引号）");
        }
        return (problems.Count == 0, string.Join("\n", lines), problems);
    }

    /// <summary>
    /// 原子写 patch 文件：先写 .tmp，再用 File.Replace 顶替（保留原文件语义），
    /// 失败时删掉 .tmp 且不动原文件。避免"写到一半崩溃 -> 用户配置被截断"。
    /// </summary>
    private static bool WritePatchAtomic(string text)
    {
        string tmp = PatchFile + ".tmp";
        try
        {
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            if (File.Exists(PatchFile)) File.Replace(tmp, PatchFile, null);
            else File.Move(tmp, PatchFile);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError("PluginManager.WritePatchAtomic", ex);
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            return false;
        }
    }

    /// <summary>
    /// 写入并自检：校验不过就从备份还原，绝不留下半截/非法文件。
    ///
    /// 两道校验，各管一段：
    ///   · <see cref="ValidatePatchText"/> —— 定点修 `<c>- id:</c>` 的引号（可修，修完继续写）；
    ///   · <see cref="ValidatePatchStructure"/> —— 结构（`[]` + 块序列这种整份作废的形态）。
    ///     只校验、不自动改写：改用户的配置文件风险更大，判非法就拒绝写入并给出中性中文原因，
    ///     让界面显示"写入被拒绝"而不是谎报"已禁用"。
    /// </summary>
    private static (bool Ok, string Detail) WritePatchChecked(string text)
    {
        var (ok, fixedText, problems) = ValidatePatchText(text);
        if (!ok)
        {
            Logger.Log($"patch 文本含 {problems.Count} 处非法写法，先就地修正：{string.Join("；", problems)}");
            text = fixedText;
        }

        // 写前结构校验：拦住"这次写入的产物就不是合法 YAML"（本次缺陷：`[]` + 块序列）
        var (stOk, stWhy) = ValidatePatchStructure(text);
        if (!stOk)
        {
            Logger.LogError("PluginManager.WritePatchChecked", new Exception("写入前结构校验未通过：" + stWhy));
            return (false, "写入被拒绝：" + stWhy);
        }

        if (!WritePatchAtomic(text)) return (false, "写入失败（原文件未改动）");

        // 写后复核：磁盘上的真实产物必须同时满足两道校验（引号 + 结构）
        string onDisk = File.Exists(PatchFile) ? File.ReadAllText(PatchFile) : "";
        var after = ValidatePatchText(onDisk);
        if (!after.Ok)
        {
            Logger.LogError("PluginManager.WritePatchChecked", new Exception("写后校验仍未通过：" + string.Join("；", after.Problems)));
            return (false, "写后校验未通过");
        }
        var afterStructure = ValidatePatchStructure(onDisk);
        if (!afterStructure.Ok)
        {
            Logger.LogError("PluginManager.WritePatchChecked", new Exception("写后结构校验未通过：" + afterStructure.Reason));
            return (false, "写后结构校验未通过：" + afterStructure.Reason);
        }
        return (true, "");
    }
    /// <summary>
    /// 读插件清单（package.json 的 dependencies）里某个包是怎么声明的：
    /// `^1.2.3`（npm 包）、`github:o/r#sha` 或 `git+https://…`（git 源）。
    /// git 源的包在 npm 上查不到版本 -> 更新按钮不出现（现场：dsh-watcher、inline-edit）。
    /// </summary>
    public static string DepSpec(string name) => DepSpecIn(PackageFile, name);

    /// <summary>
    /// 同上，但读指定的那一份 package.json —— 快照目录里存着 `profile-package.json` 副本，
    /// 回滚后要拿"快照当时的声明"跟磁盘上真实装着的版本对账，就得读得动它。
    /// </summary>
    public static string DepSpecIn(string? packageJsonPath, string name)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(packageJsonPath) || !File.Exists(packageJsonPath)) return "";
            using var doc = JsonDocument.Parse(File.ReadAllText(packageJsonPath!));
            if (!doc.RootElement.TryGetProperty("dependencies", out var deps)) return "";
            foreach (var prop in deps.EnumerateObject())
                if (prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return prop.Value.GetString() ?? "";
            return "";
        }
        catch (Exception ex) { Logger.LogError("PluginManager.DepSpecIn", ex); return ""; }
    }

    /// <summary>
    /// 读磁盘上实际装着的版本（`&lt;profileDir&gt;\node_modules\&lt;包名&gt;\package.json` 的 version）。
    ///
    /// 回滚核对要用：清单退回 `^0.10.0` 只说明文件退回去了，磁盘上装的是不是 0.10.x 得看这里 ——
    /// 现场就是"清单退了、node_modules 里还是 0.11.0"（回滚没有真的重装）。
    /// 无法读取（目录不在 / package.json 未写 version 字段）时返回空串，由调用方如实说明"无法读取"，绝不推测。
    /// 注意：返回空串不等于"未安装" —— 部分插件存在目录存在但 version 字段缺失的情况，
    /// 因此任何"按版本判定是否跳过操作"的逻辑都不能据此认定"未安装"。
    /// </summary>
    public static string ReadInstalledVersion(string profileDir, string packageName)
    {
        try
        {
            string n = (packageName ?? "").Trim();
            if (n.Length == 0 || string.IsNullOrWhiteSpace(profileDir)) return "";
            string pkg = Path.Combine(profileDir!, "node_modules",
                n.Replace('/', Path.DirectorySeparatorChar), "package.json");
            if (!File.Exists(pkg)) return "";
            using var doc = JsonDocument.Parse(File.ReadAllText(pkg));
            return doc.RootElement.TryGetProperty("version", out var v) ? (v.GetString() ?? "").Trim() : "";
        }
        catch (Exception ex) { Logger.LogError("PluginManager.ReadInstalledVersion", ex); return ""; }
    }

    /// <summary>某个包在磁盘上的那份 package.json 路径（插件到底"在不在"就看它）。</summary>
    public static string PackageJsonFor(string profileDir, string packageName)
    {
        string n = (packageName ?? "").Trim();
        if (n.Length == 0 || string.IsNullOrWhiteSpace(profileDir)) return "";
        return Path.Combine(profileDir!, "node_modules", n.Replace('/', Path.DirectorySeparatorChar), "package.json");
    }

    /// <summary>
    /// 卸载的成败判据（事实，不看命令退出码）：卸载完成后，<c>node_modules\&lt;包名&gt;</c>
    /// 这个包目录还在不在。
    ///
    /// 为什么不能只看退出码：pnpm 在 Windows 上常因"另一个程序正在使用此文件 (os error 32)"、
    /// 或某个依赖的构建脚本失败而返回非零，包其实已经被删掉了 -> 只看退出码就会出现
    /// "显示失败、其实卸干净了"（与更新那条对称，现场已遇到过同类）。
    ///
    /// <see cref="UninstallCheck.Removed"/>=true 只在目录确实不在了时才给：
    ///   · 目录不在 -> 卸掉了（成功，哪怕命令退出码非零）；
    ///   · 目录还在、但里面的 package.json 已经没了 -> 算"半截状态"，<see cref="UninstallCheck.Removed"/>=false
    ///     （宁可多报一次失败让用户重试，也不谎报"已卸载"）；
    ///   · 目录还在 -> 未卸载。
    ///
    /// 注意：这条判据分不开"本来装着、卸掉了"与"这台机器上从来就没有过这个包"——
    ///   两者都是"目录不在"，于是都会给 <see cref="UninstallCheck.Removed"/>=true。
    ///   正因如此，只看本结构会得出错误的"卸载成功"（本单 H2 现场：对一张显示「未安装」的卡片点卸载，
    ///   命令必然失败，却仍报绿色「已卸载插件 X」）-> 调用方必须在跑命令之前先记下"它原本在不在"
    ///   （<see cref="PackageDirExists"/>），再交给 <see cref="EvaluateUninstall"/> 做三态判定。
    /// 空白包名 / 空白目录 -> 判不了（<see cref="UninstallCheck.Checked"/>=false），由调用方回落命令退出码。
    /// </summary>
    public readonly struct UninstallCheck
    {
        /// <summary>这次到底判没判成（false -> 参数不足，只能用命令退出码）。</summary>
        public bool Checked { get; }
        /// <summary>包目录还在（true -> 未卸载）。</summary>
        public bool StillThere { get; }
        /// <summary>包目录确实不在了（只有这一种算"卸掉了"）。</summary>
        public bool Removed => Checked && !StillThere;
        /// <summary>一句话说明依据（进日志与事件栏）。</summary>
        public string Note { get; }

        public UninstallCheck(bool checked_, bool stillThere, string note)
        {
            Checked = checked_; StillThere = stillThere; Note = note ?? "";
        }
    }

    /// <summary>
    /// 按"包目录还在不在"判这次卸载到底成没成（纯函数，便于自检；只读盘，不写盘）。
    ///
    /// 包名先过 <see cref="IsValidPackageName"/> 这道同一条白名单（与 <see cref="BuildUninstallArgs"/>
    /// 拼命令行前用的是同一个入口）：`..`、越界的分隔符分量、空白、超长、shell 元字符一律不核对磁盘、
    /// 也不执行命令，直接给 <see cref="UninstallCheck.Checked"/>=false。
    ///
    /// 为什么必须在这里拦：`Path.Combine(root, "node_modules", n.Replace('/', '\\'))` 不净化输入，
    /// `../../etc` 这类包名会让"包名非法"与"包已删除"产生同一个结论（目录不在 -> Removed=true -> 报成功），
    /// 把 H2 那条误报直接放大 —— 非法输入绝不能被读成"卸载成功"。
    /// </summary>
    public static UninstallCheck VerifyUninstalled(string? packageName, string? profileDir = null)
    {
        try
        {
            string n = (packageName ?? "").Trim();
            string root = string.IsNullOrWhiteSpace(profileDir) ? ProfileDir : profileDir!.Trim();
            if (n.Length == 0 || root.Length == 0)
                return new UninstallCheck(false, true, "包名或目录未知 ⇒ 只能按命令退出码判定");

            // 包名合法性先判：非法包名连"目录在不在"都不该问（问了只会得到一个会误导人的"已消失"）
            if (!IsValidPackageName(n))
                return new UninstallCheck(false, true,
                    $"包名「{n}」不是合法的 npm 包名（含越界分量/空白/非法字符或超长）⇒ 不核对磁盘、也不执行卸载命令");

            string dir = Path.Combine(root, "node_modules", n.Replace('/', Path.DirectorySeparatorChar));
            bool dirThere = Directory.Exists(dir);
            bool pkgThere = File.Exists(Path.Combine(dir, "package.json"));

            if (!dirThere) return new UninstallCheck(true, false, $"包目录已消失：{n}");
            // 目录在、package.json 没了：pnpm 删到一半的中间态 —— 不当成功（宁可让用户重试一次）
            return new UninstallCheck(true, true, pkgThere
                ? $"包目录还在：{n}（说明这次未卸载）"
                : $"包目录还在、里面已经空了：{n}（半截状态，按未卸载处理）");
        }
        catch (Exception ex)
        {
            Logger.LogError("PluginManager.VerifyUninstalled", ex);
            return new UninstallCheck(false, true, "核对卸载结果时出错 ⇒ 只能按命令退出码判定");
        }
    }

    /// <summary>
    /// 某个包的目录此刻在不在盘上 —— 供卸载流程在跑命令之前记下"它原本在不在"
    /// （<see cref="EvaluateUninstall"/> 的三态判定全靠这个前置事实）。
    ///
    /// 口径复用既有的 <see cref="EvaluateInstallState"/>：<c>Installed</c>（目录 + package.json）
    /// 与 <c>Broken</c>（目录在、package.json 缺的半截安装）都算"在" ——
    /// 半截残留同样要被卸载清掉，清掉之后也该判成功。
    /// 包名非法 / 空白 / 读盘出错 -> false（按"本来就没有"处理 -> 结论只会是「无需卸载」这个中性结果，
    /// 绝不会变成一次假的"卸载成功"）。
    /// </summary>
    public static bool PackageDirExists(string? packageName, string? profileDir = null)
        => EvaluateInstallState(packageName, profileDir) != InstallStateKind.NotInstalled;

    /// <summary>
    /// 卸载动作的唯一结论入口（与 <c>MainWindow.EvaluateUpdate</c> 对称）。
    /// 从 1.3.52 起是三态：先看"操作前它原本在不在"这个前置事实，再看"现在还在不在"，
    /// 判不了才回落命令退出码。见 <see cref="EvaluateUninstall"/>。
    /// </summary>
    public readonly struct UninstallResult
    {
        /// <summary>命令退出码是不是 0。</summary>
        public bool CmdOk { get; }
        /// <summary>磁盘事实能不能判（false -> 只能用 <see cref="CmdOk"/>）。</summary>
        public bool Measured { get; }
        /// <summary>这次到底卸掉没有（只有真正卸掉了才是 true；"本来就没装"是 false）。</summary>
        public bool Removed { get; }
        /// <summary>
        /// 操作前这个包本来就没装（或包名非法 -> 根本没得卸）。
        /// 这一态既不是成功也不是失败 —— 调用方要给中性文案「无需卸载」，
        /// 绝不许报绿色的"已卸载"（本单 H2：对显示「未安装」的卡片点卸载，命令必然失败却报成功）。
        /// </summary>
        public bool AlreadyAbsent { get; }
        /// <summary>命令失败、包却确实没了 —— "虚惊一场"，日志里要留证据。</summary>
        public bool NoteDowngraded { get; }
        /// <summary>判定说明。</summary>
        public string Note { get; }

        /// <summary>是否"真卸掉了"（= <see cref="Removed"/>，语义别名，便于调用点读起来直白）。</summary>
        public bool Succeeded => Removed;

        /// <summary>中性结果：无需卸载（本来就没有），既不报成功也不报失败。</summary>
        public bool Unnecessary => AlreadyAbsent;

        public UninstallResult(bool cmdOk, bool measured, bool removed, string note,
                               bool alreadyAbsent = false)
        {
            CmdOk = cmdOk; Measured = measured; Removed = removed;
            AlreadyAbsent = alreadyAbsent;
            // "虚惊一场"只在真卸掉了时才算：本来就没有、命令又失败的情形不是虚惊（是压根没得卸）
            NoteDowngraded = removed && !cmdOk; Note = note ?? "";
        }
    }

    /// <summary>
    /// 卸载成败判定（纯函数，便于自检；只读盘）。三态：
    ///   ① <paramref name="existedBefore"/>=false -> 「无需卸载」（中性；既不成功也不失败）
    ///      —— 这台机器上本来就没有这个包，命令失败是必然的，不能读成"卸载成功"；
    ///   ② 原本在、现在目录消失 -> 成功（哪怕命令退出码非零 —— 事实优先，这是既有正确部分，保留）；
    ///   ③ 原本在、目录还在   -> 失败（哪怕命令退出码是 0 —— 事实优先）；
    ///   ④ 判不了（包名/目录未知或包名非法）-> 如实回落命令退出码。
    ///
    /// <paramref name="existedBefore"/> 由调用方在跑命令之前用
    /// <see cref="PackageDirExists"/> 取好；不传（null）时退回两态旧口径 —— 这条兼容只是为了不改变
    /// 既有调用点的签名语义，产品路径必须传，否则 H2 那种"本来就没装却报成功"会原样复现。
    /// </summary>
    public static UninstallResult EvaluateUninstall(string packageName, bool cmdOk, string? profileDir = null,
                                                    bool? existedBefore = null)
    {
        // 包名非法：不核对磁盘、结论只能是"判不了"-> 回落命令退出码（与 VerifyUninstalled 同一条白名单）
        if (!IsValidPackageName((packageName ?? "").Trim()))
        {
            string shown = (packageName ?? "").Trim();
            return new UninstallResult(cmdOk, false, cmdOk,
                $"包名「{(shown.Length > 40 ? shown.Substring(0, 40) + "…" : shown)}」不是合法的 npm 包名 ⇒ "
                + (cmdOk ? "只能按命令退出码判成功" : "只能按命令退出码判失败"));
        }

        var v = VerifyUninstalled(packageName, profileDir);
        if (!v.Checked) return new UninstallResult(cmdOk, false, cmdOk,
            cmdOk ? "核对不了磁盘状态，按命令退出码判成功" : "核对不了磁盘状态，按命令退出码判失败");

        // ── ① 前置事实：本来就没装 -> 无需卸载（中性）──
        //    必须用操作前取到的值下这个结论：只看"现在目录不在"分不开"卸掉了"与"从来就没有过"。
        if (existedBefore == false && v.Removed)
            return new UninstallResult(cmdOk, true, false,
                $"无需卸载：{v.Note}（操作前本机就没有这个包，不是这次卸掉的）", alreadyAbsent: true);

        return new UninstallResult(cmdOk, true, v.Removed, v.Note);
    }

    /// <summary>
    /// 清单体检的结果。
    /// <see cref="Readable"/>=false 表示这份清单根本无法读取（文件不存在/非合法 JSON）——
    /// 那不属于"缺包"，绝不能误判为"清单中所有包均未安装"。
    /// </summary>
    public readonly struct ManifestCheck
    {
        /// <summary>清单本身读没读成（false -> <see cref="Missing"/> 一定为空，不可据此下结论）。</summary>
        public bool Readable { get; }
        /// <summary>清单的 dependencies 总条数。</summary>
        public int Declared { get; }
        /// <summary>「清单里有、盘上没有」的包名（按清单里的原始顺序）。</summary>
        public IReadOnlyList<string> Missing { get; }

        public ManifestCheck(bool readable, int declared, IReadOnlyList<string> missing)
        {
            Readable = readable; Declared = declared;
            Missing = missing ?? Array.Empty<string>();
        }

        public bool HasMissing => Missing.Count > 0;
    }

    /// <summary>
    /// 启动前的清单体检（纯函数）：读 profile\package.json 的 dependencies，
    /// 逐个看 node_modules\&lt;包&gt;\package.json 在不在；返回「清单里有、盘上没有」那些包名。
    ///
    /// 为什么要有这一步（现场诊断包的原文，1.3.36 自己导出的摘要）：
    ///   引擎状态: 未运行
    ///   Error: dsh: cannot resolve profile bundle "@furongjun1999/dsh-memory" from the dsh installation or
    ///   …\profiles\web; run 'dsh plugin --profile web install' if its dependency is not installed
    /// dsh 在加载 profile 时即会因这种缺失直接失败 -> 用户先看到"启动失败"弹窗；
    /// 而既有的自愈逻辑挂在"打开插件页 -> 取 loader id"这条流程中，需等用户点开插件页才执行（现场延迟约两分钟）。
    /// 这里将其提前到拉起引擎之前，判据不是解析 dsh 的报错，而是磁盘事实（清单 vs node_modules）。
    ///
    /// 判定只看 package.json 是否存在，不看版本 —— 部分插件存在目录存在、version 字段缺失的情况
    /// （见 <see cref="ReadInstalledVersion"/>），按版本判定会把正常插件误判为未安装。
    /// 无法读取清单时返回 Readable=false（<see cref="ManifestCheck.Missing"/> 为空），绝不误判。
    /// </summary>
    public static ManifestCheck FindMissingFromManifest(string? profileDir)
    {
        var missing = new List<string>();
        try
        {
            if (string.IsNullOrWhiteSpace(profileDir)) return new ManifestCheck(false, 0, missing);
            string manifest = Path.Combine(profileDir!, "package.json");
            if (!File.Exists(manifest)) return new ManifestCheck(false, 0, missing);

            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            if (!doc.RootElement.TryGetProperty("dependencies", out var deps) ||
                deps.ValueKind != JsonValueKind.Object)
                return new ManifestCheck(false, 0, missing);

            int declared = 0;
            foreach (var d in deps.EnumerateObject())
            {
                declared++;
                string pj = PackageJsonFor(profileDir!, d.Name);
                if (pj.Length > 0 && !File.Exists(pj)) missing.Add(d.Name);
            }
            return new ManifestCheck(true, declared, missing);
        }
        catch (Exception ex)
        {
            Logger.LogError("PluginManager.FindMissingFromManifest", ex);
            return new ManifestCheck(false, 0, missing);      // 读不成 -> 不误判
        }
    }

    /// <summary>
    /// 启动失败/体检失败时向用户展示的说明：「是哪个包未安装」。
    /// 现场要求：让人一眼知道该重装或卸载哪个插件 —— 所以包名必须点出来，且不许含糊成"某个插件"。
    /// </summary>
    public static string MissingPackagesNote(IEnumerable<string>? missing)
    {
        var list = (missing ?? Enumerable.Empty<string>())
            .Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct().ToList();
        if (list.Count == 0) return "";
        return $"插件清单中登记了 {string.Join("、", list)}，但本机未安装（启动前的自动重新安装也未成功）"
             + " —— 请在插件页重新安装该插件，或在「本地插件」页卸载它。";
    }

    /// <summary>插件清单中是否登记了该包（判定"是否已安装"的唯一事实依据）。</summary>
    public static bool HasDependency(string name) => DepSpec(name).Length > 0;

    /// <summary>
    /// 从安装源里取出包名（清单里的键）：
    ///   `@scope/name@0.3.2` -> `@scope/name`；`@scope/name` -> 原样；
    ///   `github:o/r` / `git+https://…` / `https://…` / 本地路径 -> 空串（这类装完不一定以包名登记，判不了就交给命令退出码）。
    /// </summary>
    public static string PackageNameFromSource(string? source)
    {
        try
        {
            string s = (source ?? "").Trim().Trim('"');
            if (s.Length == 0) return "";
            if (s.StartsWith("github:", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("git+", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("git://", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith(".") || s.Contains("\\")) return "";
            int at = s.LastIndexOf('@');
            return at > 0 ? s.Substring(0, at) : s;
        }
        catch { return ""; }
    }
    /// <summary>读锁文件文本（插件真实版本与 git 提交的唯一凭据）。</summary>
    public static string LockText()
    {
        try
        {
            string p = Path.Combine(ProfileDir, "pnpm-lock.yaml");
            return File.Exists(p) ? File.ReadAllText(p) : "";
        }
        catch { return ""; }
    }
    /// <summary>解析 cordis.patch.yml 中所有 `- id: X` + `disabled: true` 记录的 id。</summary>
    public static HashSet<string> ReadDisabledIds()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(PatchFile)) return set;
            string? lastId = null;
            foreach (var raw in File.ReadAllLines(PatchFile))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var idHere = IdFromLine(line);
                if (idHere != null) lastId = idHere;
                else if (line.StartsWith("disabled:") && lastId != null && line.Contains("true", StringComparison.OrdinalIgnoreCase))
                    set.Add(lastId);
            }
        }
        catch { }
        return set;
    }

    /// <summary>
    /// 包内 <c>author</c> 字段（字符串或对象的 <c>name</c>）。不做任何兜底：
    /// 取不到就返回空串，由 <see cref="ResolveAuthor"/> 决定怎么兜底、以及要不要标明来源。
    /// </summary>
    private static string ExtractAuthor(JsonElement m)
    {
        try
        {
            if (m.TryGetProperty("author", out var a))
            {
                if (a.ValueKind == JsonValueKind.String)
                {
                    var s = a.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) return s!.Trim();
                }
                else if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("name", out var n))
                {
                    var s = n.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) return s!.Trim();
                }
            }
        }
        catch { }
        return "";
    }

    /// <summary>
    /// 最终的作者显示值与"它是不是仓库归属"的判定（纯函数，便于自检）：
    ///   ① 包内写了 author -> 原样用，<c>FromRepo=false</c>（这是真的作者信息）；
    ///   ② 没写 author、但能认出仓库归属 -> 用站点路径的第一段做兜底，
    ///      此时 <c>FromRepo=true</c>，界面必须标明「来自仓库地址」，不得冒充作者本人；
    ///   ③ 两者都没有 -> 返回空串（界面显示占位），绝不编一个名字出来。
    ///
    /// 兜底顺序：清单声明里的仓库（git 源包磁盘上多半没有 repository 字段）-> 包内 repository。
    /// </summary>
    public static (string Author, bool FromRepo) ResolveAuthor(string? authorField, string? declaredSpec, string? repoUrl)
    {
        string author = (authorField ?? "").Trim();
        if (author.Length > 0) return (author, false);

        string owner = RepoOwnerFromSpec(declaredSpec);
        if (owner.Length == 0) owner = RepoOwnerFromSpec(repoUrl);
        return owner.Length > 0 ? (owner, true) : ("", false);
    }

    /// <summary>仓库地址：由 repository.url（字符串或对象）规整为可打开的 https。</summary>
    private static string ExtractRepoUrl(JsonElement m)
    {
        try
        {
            if (m.TryGetProperty("repository", out var r))
            {
                string? url = r.ValueKind == JsonValueKind.String ? r.GetString()
                    : r.TryGetProperty("url", out var u) ? u.GetString() : null;
                string norm = NormalizeRepoUrl(url);
                if (norm.Length > 0) return norm;
            }
        }
        catch { }
        return "";
    }

    /// <summary>
    /// 将 package.json 中的仓库地址规整为可打开的 https：
    /// git+https://github.com/x/y.git -> https://github.com/x/y；
    /// ssh://git@github.com/x/y.git、git@github.com:x/y.git、git://… 同样处理。
    /// 非 http 家族的地址无法识别，返回空串。
    /// </summary>
    public static string NormalizeRepoUrl(string? url)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url)) return "";
            string u = url!.Trim();
            if (u.StartsWith("git+", StringComparison.OrdinalIgnoreCase)) u = u.Substring(4);
            if (u.StartsWith("ssh://git@", StringComparison.OrdinalIgnoreCase)) u = "https://" + u.Substring("ssh://git@".Length);
            else if (u.StartsWith("git@", StringComparison.OrdinalIgnoreCase)) u = "https://" + u.Substring(4).Replace(":", "/");
            else if (u.StartsWith("git://", StringComparison.OrdinalIgnoreCase)) u = "https://" + u.Substring(6);
            if (u.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) u = u.Substring(0, u.Length - 4);
            if (!u.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return "";
            return u;
        }
        catch { return ""; }
    }

    private static string GetStringProp(JsonElement m, string prop)
    {
        try
        {
            if (m.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String)
                return (v.GetString() ?? "").Trim();
        }
        catch { }
        return "";
    }

    /// <summary>
    /// 兼容性要求：转调 <see cref="VersionInfo.ExtractDshRequirement"/> —— 判据只留那一份，
    /// 本地列表与插件市场因此对同一份 package.json 必然得出同一个 (requirement, source)。
    ///
    /// 字段优先级（① dsh.compatibility.dsh -> ② dsh.engines.dsh -> ③ peerDependencies 取最高候选）
    /// 与"③ 的初值必须可比、不能用空串"的根因说明，全部见该方法的注释 —— 这里不再保留任何判据副本。
    ///
    /// 旧实现曾经在这里有过缺陷：peer 分支初值写成 <c>bestVer = ""</c>，空串过不了 TrySplit ->
    /// <c>Compare(任意版本, "")</c> 恒返回 0 -> <c>0 &gt; 0</c> 恒为假 -> best 永远赋不上值 ->
    /// 整个 peerDependencies 分支静默失效（本地全显示「未声明」），而市场那支初值恰是 "0.0.0" 所以正常。
    /// </summary>
    private static (string req, string src) ExtractRequirement(JsonElement m)
        => VersionInfo.ExtractDshRequirement(m);

    /// <summary>
    /// 最近一次读取 id 时发现的"清单已登记、机器上未安装"的包名（无则空串），
    /// 以及该次是否使用了缓存作为兜底数据源（<see cref="LastCacheUsedCount"/>）。
    /// 用途：用户点击「禁用」却无法取得 id 时，弹窗必须指明是哪个包导致 ——
    /// 仅输出"无法读取内部标识"不构成有效信息（现场即卡在此处）。
    /// </summary>
    public static string LastUnresolvedBundle { get; private set; } = "";

    /// <summary>上一次兜底实际提供的映射条数（0 = 连缓存都不存在，不能向用户声称"已使用兜底数据"）。</summary>
    public static int LastCacheUsedCount { get; private set; }

    /// <summary>记下"清单里有、磁盘上没有"的包名（空串 = 清掉，例如 dump 又能读全了）。</summary>
    public static void NoteUnresolvedBundle(string? packageName, int cacheUsed = 0)
    {
        LastUnresolvedBundle = (packageName ?? "").Trim();
        LastCacheUsedCount = LastUnresolvedBundle.Length == 0 ? 0 : Math.Max(0, cacheUsed);
    }

    /// <summary>
    /// 取 id 成功时的唯一清空入口：包名置空、缓存计数归零（两项必须一起清）。
    ///
    /// 为什么单独给一个方法：这两个字段是"上一次读 id 的失败现场"，只对那一次有效。
    /// 一旦这次读全了（dump 跑通、或缓存里确实有），上次那条提示就必须作废 ——
    /// 否则界面会拿"上一次失败时记住的包名"说话（现场：dump 退出码 0 已经读全了，
    /// 点禁用还在弹上次那条缺包提示）。成功后不清、或只清一半，都会让弹窗撒谎。
    /// </summary>
    public static void ClearUnresolvedBundle()
    {
        LastUnresolvedBundle = "";
        LastCacheUsedCount = 0;
    }

    /// <summary>
    /// 拿不到 loader id 时的弹窗文案（纯函数，便于自检）。
    /// 已知是"清单已登记、机器上未安装"造成的，则写出具体包名并给出两条处理路径
    /// （重装它 / 卸载它）；否则退回原来的"点刷新再试"。全程不露命令行。
    /// </summary>
    public static string MissingIdMessage(string pluginName)
    {
        string name = (pluginName ?? "").Trim();
        string miss = LastUnresolvedBundle;
        if (miss.Length == 0)
            return $"无法读取「{name}」的内部标识，暂时无法禁用该插件。\n请先在插件页点「刷新」重新读取，再试一次。";

        string backed = LastCacheUsedCount > 0
            ? $"已沿用上次缓存的 {LastCacheUsedCount} 条映射记录。"
            : "上次缓存的映射记录中也没有该插件。";
        return $"无法读取「{name}」的内部标识，暂时无法禁用该插件。\n" +
               $"插件清单中登记了 {miss}，但本机未安装；{backed}\n" +
               $"可在「寻找插件」中重新安装 {miss}，或在「本地插件」中卸载它；\n" +
               $"处理后回到插件页点「刷新」，即可正常禁用插件。";
    }

    /// <summary>
    /// 把"包名 -> loader id"贴到每个插件对象上（纯函数，便于自检）：返回新贴上的条数。
    ///
    /// 为什么必须是一个可复用的入口：`Plugin.Scan` 每次都 new 一批新对象（`LoaderId` 为 null），
    /// 而 id 表是异步单独读回来的。只要"重扫"与"贴 id"不是绑在一起做，就会漏贴 ——
    /// 现场表现：dump 退出码为 0、缓存中也确实存在该包，点击「禁用」仍提示"无法读取内部标识"。
    /// 所以凡是 `_plugins` 被整体换掉的地方，换完都要过一遍这里。
    ///
    /// 只贴不清：id 表可能是"缓存兜底"这种部分表（dump 失败时的降级），
    /// 用它去清空已有 id 会把本来能禁用的插件变成无法禁用。表里没有的名字一律保持原样。
    /// </summary>
    public static int ApplyLoaderIds(IEnumerable<Plugin>? plugins, IDictionary<string, string>? ids)
    {
        try
        {
            if (plugins == null || ids == null || ids.Count == 0) return 0;
            int n = 0;
            foreach (var p in plugins)
            {
                if (p == null || string.IsNullOrWhiteSpace(p.Name)) continue;
                if (!ids.TryGetValue(p.Name, out var id) || string.IsNullOrWhiteSpace(id)) continue;
                if (string.Equals(p.LoaderId, id, StringComparison.Ordinal)) continue;   // 已经是这个 id，不重复计数
                p.LoaderId = id.Trim();
                n++;
            }
            return n;
        }
        catch (Exception ex) { Logger.LogError("PluginManager.ApplyLoaderIds", ex); return 0; }
    }

    /// <summary>
    /// 拿这个插件的 loader id（DSH 补丁层按它匹配，用包名写入不产生任何效果 —— 现场已验证）。
    /// 优先用 dsh --dump-config 给出的真实 id；拿不到就按现场验证过的规律推导：
    ///   `@scope/name` -> `scope-name`（已验证存在此命名形态的实际案例）。
    /// 都拿不到返回空串：调用方必须拒绝写入，而不是退回包名。
    /// </summary>
    public static string LoaderIdFor(Plugin p)
    {
        try
        {
            if (p == null) return "";
            if (!string.IsNullOrWhiteSpace(p.LoaderId)) return p.LoaderId!.Trim();
            // 不再按 `@scope/name -> scope-name` 推导：现场证明那是错的
            //（`@liustack/modsearch` 的真实 id 是 `modsearch`）-> 拿不到就返回空，由调用方拒绝写入。
            return "";
        }
        catch { return ""; }
    }

    /// <summary>按 `@scope/name -> scope-name` 推导 loader id（纯函数，便于自检）。</summary>
    public static string DeriveLoaderId(string? packageName)
    {
        string n = (packageName ?? "").Trim();
        if (n.Length == 0) return "";
        if (n[0] != '@') return n;                  // 无作用域的包名本身就是 id 形态
        int slash = n.IndexOf('/');
        if (slash <= 0 || slash == n.Length - 1) return n;
        string scope = n.Substring(1, slash - 1);   // 去掉 @
        string name = n.Substring(slash + 1);
        return scope + "-" + name;
    }

    /// <summary>
    /// 修复历史遗留：早期版本用包名写过禁用记录（DSH 按 loader id 匹配 -> 那些记录全都没生效）。
    /// 现在能拿到 id 了，就把这些记录的 id 改写成 loader id（先备份；改写失败则整块移除）。
    /// 返回修了几条（0 表示没有需要修的）。
    /// </summary>
    public static int RepairPackageNameRecords(IEnumerable<Plugin> plugins)
    {
        try
        {
            // 只修"确实知道真实 id"的：包名 -> 真实 loader id
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in plugins)
            {
                string id = LoaderIdFor(p);
                if (p?.Name != null && id.Length > 0 && !id.Equals(p.Name, StringComparison.OrdinalIgnoreCase))
                    map[p.Name] = id;
                // 以及"当前记的是猜出来的 id"的情形：id 恰好等于 scope-name 形态时也纳入
                string guess = DeriveLoaderId(p?.Name);
                if (p?.Name != null && id.Length > 0 && guess.Length > 0 && !guess.Equals(id, StringComparison.OrdinalIgnoreCase))
                    map[guess] = id;
            }
            if (map.Count == 0 || !File.Exists(PatchFile)) return 0;

            var lines = File.ReadAllLines(PatchFile).ToList();
            int fixedCount = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                string? cur = IdFromLine(lines[i]);
                if (cur == null || !map.TryGetValue(cur, out var better)) continue;
                lines[i] = lines[i].Replace(QuoteId(cur), QuoteId(better)).Replace(cur, better);
                fixedCount++;
            }
            if (fixedCount == 0) return 0;

            BackupPatchFile();
            var (ok, _) = WritePatchChecked(string.Join("\n", lines).TrimEnd() + "\n");
            if (!ok) return 0;
            Logger.Log($"已把 {fixedCount} 条用包名写的禁用记录改写成 loader id（原先都不生效）");
            return fixedCount;
        }
        catch (Exception ex) { Logger.LogError("PluginManager.RepairPackageNameRecords", ex); return 0; }
    }
    /// <summary>禁用插件：先备份 cordis.patch.yml，再追加 disabled 行（幂等）。</summary>
    public static string Disable(Plugin p)
    {
        try
        {
            // 只能按 loader id 写入：DSH 按 id 匹配，用包名写入不生效（现场：已写入但未禁用）
            string id = LoaderIdFor(p);
            if (id.Length == 0)
                return MissingIdMessage(p.Name);

            // 注意：模板故意不带 `[]`：带上它就会出现「`[]` + 追加的块序列」这种一个文档两个顶层节点的
            //   非法 YAML（引擎整份读不了）。原来的 `…bundle layer:\n[]\n` 正是本缺陷的源头。
            if (!File.Exists(PatchFile))
                File.WriteAllText(PatchFile, PatchTemplate, new UTF8Encoding(false));

            string text = File.ReadAllText(PatchFile);

            // 幂等检查
            var lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (IdFromLine(lines[i]) == id && i + 1 < lines.Length && lines[i + 1].Contains("disabled: true"))
                    return $"「{p.Name}」已经是禁用状态，无需重复操作。";
            }

            string bak = BackupPatchFile();

            // id 一律加引号：@ / ` 开头的包名不加引号就是非法 YAML（现场事故）
            string block = $"\n# DSHGuard 于 {DateTime.Now:yyyy-MM-dd HH:mm:ss} 禁用（备份 {Path.GetFileName(bak)}）\n- id: {QuoteId(id)}\n  disabled: true\n";
            var (wOk, wDetail) = WritePatchChecked(text.TrimEnd() + "\n" + block);
            if (!wOk) return $"禁用「{p.Name}」失败：{wDetail}";

            Logger.Log($"已禁用插件 {p.Name}（id={id}；备份 {Path.GetFileName(bak)}）");
            return $"已禁用「{p.Name}」。\n\n原配置文件已备份，重启 DSH 后生效。";
        }
        catch (Exception ex)
        {
            Logger.LogError("PluginManager.Disable", ex);
            return "禁用失败：未能改写插件配置，原因见日志。";
        }
    }

    /// <summary>
    /// 按目标 DSH 版本做升级前兼容性体检（四色分档）。
    /// 仅统计启用中的插件；橙（可用但非作者优先版本）与灰（未声明）不主动禁用，保留原状态。
    /// </summary>
    public static (List<Plugin> Ok, List<Plugin> Partial, List<Plugin> Broken, List<Plugin> Unknown) Evaluate(
        IEnumerable<Plugin> plugins, string targetVersion)
    {
        var ok = new List<Plugin>();
        var partial = new List<Plugin>();
        var broken = new List<Plugin>();
        var unknown = new List<Plugin>();

        foreach (var p in plugins)
        {
            if (p.Disabled || p.Version == "(未安装)") continue;
            switch (EvaluateBand(p.Requirement, targetVersion))
            {
                case Compat.Ok: ok.Add(p); break;
                case Compat.Partial: partial.Add(p); break;
                case Compat.Broken: broken.Add(p); break;
                default: unknown.Add(p); break;
            }
        }
        return (ok, partial, broken, unknown);
    }

    /// <summary>备份 cordis.patch.yml（同一秒内多次操作也不会互相覆盖）。</summary>
    private static string BackupPatchFile()
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string bak = PatchFile + ".bak-" + stamp;
        int n = 2;
        while (File.Exists(bak)) bak = $"{PatchFile}.bak-{stamp}-{n++}";
        File.Copy(PatchFile, bak, overwrite: true);
        return bak;
    }

    /// <summary>
    /// 启用插件：从 cordis.patch.yml 移除禁用记录，移除前先备份。
    /// 先查本程序写入的记录（带 <c># DSHGuard</c> 注释）；<paramref name="force"/> = true 时，
    /// 手工写入的 <c>- id: X</c> + <c>disabled: true</c> 也一并移除。
    /// </summary>
    public static string Enable(Plugin p, bool force = false)
    {
        try
        {
            string id = LoaderIdFor(p);
            // 历史记录可能是用包名写的 -> 两个都认，才能删干净
            bool MatchId(string? got) =>
                got != null && (got.Equals(id, StringComparison.OrdinalIgnoreCase)
                             || got.Equals(p.Name, StringComparison.OrdinalIgnoreCase));
            if (!File.Exists(PatchFile)) return "patch 文件不存在，无需启用。";

            var lines = File.ReadAllLines(PatchFile).ToList();

            string RemoveAndSave(int start, int end, string how)
            {
                string bak = BackupPatchFile();

                var keep = new List<string>();
                for (int k = 0; k < lines.Count; k++)
                    if (k < start || k > end) keep.Add(lines[k]);
                var (okW, detailW) = WritePatchChecked(string.Join("\n", keep).TrimEnd() + "\n");
                if (!okW) return $"启用「{p.Name}」失败：{detailW}";

                Logger.Log($"已启用插件 {p.Name}（id={id}，{how}；备份 {Path.GetFileName(bak)}）");
                return $"已重新启用「{p.Name}」。\n\n{how}\n原配置文件已备份，重启 DSH 后生效。";
            }

            // ① 本程序写入的块：注释行 + `- id: X` + `disabled: true`
            for (int i = 0; i + 2 < lines.Count; i++)
            {
                if (!lines[i].TrimStart().StartsWith("# DSHGuard", StringComparison.OrdinalIgnoreCase)) continue;
                if (!MatchId(IdFromLine(lines[i + 1]))) continue;
                if (!lines[i + 2].Contains("disabled: true", StringComparison.OrdinalIgnoreCase)) continue;
                return RemoveAndSave(i, i + 2, "移除了本程序写入的禁用记录");
            }

            // ② 手工写入的禁用记录（例如排查客户端崩溃时手动添加的 dsh-zh）
            if (force)
            {
                for (int i = 0; i + 1 < lines.Count; i++)
                {
                    if (!MatchId(IdFromLine(lines[i]))) continue;

                    int j = i + 1;
                    while (j < lines.Count && lines[j].TrimStart().StartsWith("#")) j++;   // 允许中间存在注释行
                    if (j >= lines.Count || !lines[j].Contains("disabled: true", StringComparison.OrdinalIgnoreCase)) continue;

                    return RemoveAndSave(i, j, "移除了手工写入的禁用记录（上面的说明注释保留）");
                }
            }

            return $"没有找到「{p.Name}」的禁用记录，未做改动。\n" +
                   "如果它仍显示已禁用，可能是被别的层（bundle / 环境变量）关掉的。";
        }
        catch (Exception ex)
        {
            Logger.LogError("PluginManager.Enable", ex);
            return "启用失败：未能改写插件配置，原因见日志。";
        }
    }

    /// <summary>
    /// 批量禁用：一次备份 + 一次写入（升级前禁用不兼容插件走这条，
    /// 避免"每个插件重写一次文件、连改十几次"这种既慢又危险的写法）。
    /// 返回：实际写入的插件名清单。
    /// </summary>
    public static (List<string> Disabled, string Detail) DisableMany(IEnumerable<Plugin> plugins)
    {
        var done = new List<string>();
        try
        {
            var targets = plugins.Where(p => p != null).ToList();
            if (targets.Count == 0) return (done, "没有需要禁用的插件。");

            // 注意：模板故意不带 `[]`：带上它就会出现「`[]` + 追加的块序列」这种一个文档两个顶层节点的
            //   非法 YAML（引擎整份读不了）。原来的 `…bundle layer:\n[]\n` 正是本缺陷的源头。
            if (!File.Exists(PatchFile))
                File.WriteAllText(PatchFile, PatchTemplate, new UTF8Encoding(false));

            string text = File.ReadAllText(PatchFile);
            var already = ReadDisabledIds();

            var blocks = new System.Text.StringBuilder();
            string bak = BackupPatchFile();
            foreach (var p in targets)
            {
                string id = LoaderIdFor(p);
                if (id.Length == 0) continue;               // 无法读取 id 的不写入（写入也不生效）
                if (already.Contains(id) || already.Contains(p.Name)) continue;   // 幂等（兼容历史包名记录）
                if (done.Contains(p.Name)) continue;

                blocks.Append($"\n# DSHGuard 于 {DateTime.Now:yyyy-MM-dd HH:mm:ss} 禁用（备份 {Path.GetFileName(bak)}）\n- id: {QuoteId(id)}\n  disabled: true\n");
                done.Add(p.Name);
            }

            if (done.Count == 0) return (done, "这些插件本来就已经禁用，未做改动。");

            var (ok, detail) = WritePatchChecked(text.TrimEnd() + "\n" + blocks.ToString());
            if (!ok) { done.Clear(); return (done, detail); }

            Logger.Log($"批量禁用 {done.Count} 个插件：{string.Join("、", done)}（备份 {Path.GetFileName(bak)}）");
            return (done, $"已禁用 {done.Count} 个插件。原配置文件已备份。");
        }
        catch (Exception ex)
        {
            Logger.LogError("PluginManager.DisableMany", ex);
            return (done, "批量禁用失败：未能改写插件配置，原因见日志。");
        }
    }

    /// <summary>
    /// 批量启用：与 <see cref="DisableMany"/> 对称 —— 一次备份 + 一次写入 + <see cref="WritePatchChecked"/> 写后校验，
    /// 避免"每个插件重写一次文件、连改十几次"这种既慢又危险的写法。
    /// 匹配口径与单个 <see cref="Enable"/> 完全一致：同时认 loader id 与包名（历史记录可能是用包名写的）。
    /// <paramref name="force"/> = true 时，手工写入的 <c>- id: X</c> + <c>disabled: true</c> 也一并移除（说明注释保留）。
    /// 返回：实际移除了禁用记录的插件名清单 / 给界面看的说明。
    /// </summary>
    public static (List<string> Enabled, string Detail) EnableMany(IEnumerable<Plugin> plugins, bool force)
    {
        var done = new List<string>();
        try
        {
            var targets = plugins.Where(p => p != null).ToList();
            if (targets.Count == 0) return (done, "没有需要启用的插件。");
            if (!File.Exists(PatchFile)) return (done, "patch 文件不存在，无需启用。");

            var lines = File.ReadAllLines(PatchFile).ToList();
            var drop = new bool[lines.Count];
            var claimed = new List<(int Start, int End)>();      // 已认领的行区间，不许被第二个插件重复删

            bool Overlaps(int start, int end)
            {
                foreach (var (s, e) in claimed)
                    if (start <= e && end >= s) return true;
                return false;
            }

            foreach (var p in targets)
            {
                if (done.Contains(p.Name)) continue;             // 同一个插件只处理一次

                string id = LoaderIdFor(p);
                // 历史记录可能是用包名写的 -> 两个都认，才能删干净（与 Enable 内的匹配一致）
                bool MatchId(string? got) =>
                    got != null && (got.Equals(id, StringComparison.OrdinalIgnoreCase)
                                 || got.Equals(p.Name, StringComparison.OrdinalIgnoreCase));

                // ① 本程序写入的块：注释行 + `- id: X` + `disabled: true`
                bool hit = false;
                for (int i = 0; i + 2 < lines.Count; i++)
                {
                    if (!lines[i].TrimStart().StartsWith("# DSHGuard", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!MatchId(IdFromLine(lines[i + 1]))) continue;
                    if (!lines[i + 2].Contains("disabled: true", StringComparison.OrdinalIgnoreCase)) continue;
                    if (Overlaps(i, i + 2)) continue;

                    for (int k = i; k <= i + 2; k++) drop[k] = true;
                    claimed.Add((i, i + 2));
                    done.Add(p.Name);
                    hit = true;
                    break;
                }
                if (hit) continue;

                // ② 手工写入的禁用记录（例如排查客户端崩溃时手动添加的 dsh-zh）；注释行保留，同单个 Enable
                if (!force) continue;
                for (int i = 0; i + 1 < lines.Count; i++)
                {
                    if (!MatchId(IdFromLine(lines[i]))) continue;

                    int j = i + 1;
                    while (j < lines.Count && lines[j].TrimStart().StartsWith("#")) j++;   // 允许中间存在注释行
                    if (j >= lines.Count || !lines[j].Contains("disabled: true", StringComparison.OrdinalIgnoreCase)) continue;
                    if (Overlaps(i, j)) continue;

                    for (int k = i; k <= j; k++) drop[k] = true;
                    claimed.Add((i, j));
                    done.Add(p.Name);
                    break;
                }
            }

            if (done.Count == 0)
                return (done, force
                    ? "没有找到这些插件的禁用记录，未做改动。"
                    : "没有找到本程序写入的禁用记录，未做改动。");

            string bak = BackupPatchFile();

            var keep = new List<string>();
            for (int i = 0; i < lines.Count; i++) if (!drop[i]) keep.Add(lines[i]);
            var (okW, detailW) = WritePatchChecked(string.Join("\n", keep).TrimEnd() + "\n");
            if (!okW) { done.Clear(); return (done, detailW); }    // 写入失败：如实报错，不谎报成功

            Logger.Log($"批量启用 {done.Count} 个插件：{string.Join("、", done)}（备份 {Path.GetFileName(bak)}）");
            return (done, $"已重新启用 {done.Count} 个插件。原配置文件已备份。");
        }
        catch (Exception ex)
        {
            Logger.LogError("PluginManager.EnableMany", ex);
            done.Clear();
            return (done, "批量启用失败：未能改写插件配置，原因见日志。");
        }
    }

    // ══════════════ 更新成败以「磁盘上的事实」为准（纯函数，便于自检） ══════════════
    //
    // 【现场 bug】用户对 dsh-mnemon 执行更新 -> 弹出「更新失败」，但卡片上的版本已是新版本。
    // 日志原文（异常-20260916-111220.log）：
    //   [11:15:52.979] 已放开 pnpm 供应链策略（… plugin --profile web add dsh-mnemon@0.5.9 …）
    //   [11:17:04.442] 命令结束：… add dsh-mnemon@0.5.9 … 退出码=1
    //       stderr 尾部：… node_modules\cpu-features install: Failed
    //                     dsh: pnpm failed in profile directory …\profiles\web
    // 包装上了、版本号确实变了，pnpm 却因为某个依赖的构建脚本失败返回非零 ->
    // 只看 `p.ExitCode == 0` 就报"失败"，与用户在卡片上看到的事实相反。
    //
    // 现在的判据：读取 node_modules\<包名>\package.json 的 version，与目标版本比较。
    // 注意"无法读取版本"≠"未安装"（部分插件存在目录存在、version 字段缺失的情况，
    // 见 ReadInstalledVersion 的注释）——因此本层仅在成功读取版本时才作出结论。

    /// <summary>更新/安装的版本比对结论。</summary>
    public enum VersionCheck
    {
        /// <summary>读到版本了，且它就是目标版本（或落在目标范围内）-> 成功。</summary>
        Satisfied,
        /// <summary>已读取版本，但它不是目标版本 -> 目标版本确实未安装，判定为失败。</summary>
        NotSatisfied,
        /// <summary>无法读取版本，或目标不是"可比较高低"的版本写法（git 源等）-> 交由命令退出码判定。</summary>
        Unknown
    }

    /// <summary>把「磁盘上的版本」与「这次要装的目标版本」比出结论 + 一句人话（UI/日志/自检共用）。</summary>
    public readonly struct VersionVerdict
    {
        public VersionCheck Check { get; }
        /// <summary>磁盘上读取到的版本（无法读取时为空串）。</summary>
        public string Installed { get; }
        /// <summary>本次命令要装的目标版本说明符（git 源可能是仓库地址）。</summary>
        public string Target { get; }
        /// <summary>一句话说明为什么这么判（直接可进弹窗/日志）。</summary>
        public string Note { get; }

        public VersionVerdict(VersionCheck check, string installed, string target, string note)
        {
            Check = check; Installed = installed ?? ""; Target = target ?? ""; Note = note ?? "";
        }

        /// <summary>只有这一种情况能算"版本确实到位了"。</summary>
        public bool Satisfied => Check == VersionCheck.Satisfied;
        /// <summary>仅此一种情况可判定为"版本确实未安装"（Unknown 不算，不得将无法读取版本报告为失败）。</summary>
        public bool NotSatisfied => Check == VersionCheck.NotSatisfied;
    }

    /// <summary>
    /// 一条「版本比对」语义样例：输入 + 期望结论 + 一句话说明。
    /// <see cref="Why"/> 是给人看的判据依据，同时也是自检断言失败时的报错文案 ——
    /// 所以它和 <see cref="Expected"/> 来自同一条记录，不可能"表里看着对、实际判错"。
    /// </summary>
    public sealed record UpdateVerdictCase(string Target, string? Installed, VersionCheck Expected, string Why);

    /// <summary>
    /// 版本比对的语义契约（唯一真源）：自检直接遍历这张表断言，界面/日志也用它解释判据。
    ///
    /// 注意：这张表存在的理由是一次真实返工：上一版自检把"说明文案"和"实际断言"写成了两套，
    /// 结果 `2.0.0` vs `^1.2.3` 被写进说明里当"达成"（npm 语义是 `>=1.2.3 &lt;2.0.0`，
    /// 2.0.0 恰好落在开区间外 -> 不达成），自检在真机上 FAIL 而文案看着全对。
    /// 现在脚本与文案同源：改表就是改判据，两边不可能再分叉。
    ///
    /// 全部按 npm 语义：
    ///   `^1.2.3` -> [1.2.3, 2.0.0)；`^0.2.3` -> [0.2.3, 0.3.0)（0.x 特例）；`~1.2.3` -> [1.2.3, 1.3.0)；
    ///   `&gt;=`/`&lt;=`/`||`/`1.x` 按常规；git 源与无法读取版本一律"不可判"（交由命令退出码）。
    /// </summary>
    public static readonly IReadOnlyList<UpdateVerdictCase> UpdateVerdictContracts = new[]
    {
        // ── ① 确切版本：相等才算达成 ──
        new UpdateVerdictCase("0.5.9",  "0.5.9",  VersionCheck.Satisfied,    "确切版本相等 ⇒ 目标达成（现场 dsh-mnemon 0.5.8→0.5.9 就是这条）"),
        new UpdateVerdictCase("0.5.8",  "0.5.8",  VersionCheck.Satisfied,    "确切版本相等 ⇒ 目标达成"),
        new UpdateVerdictCase("0.5.9",  "0.5.8",  VersionCheck.NotSatisfied, "磁盘上还是旧版本 ⇒ 未安装"),
        new UpdateVerdictCase("0.5.9",  "0.5.10", VersionCheck.NotSatisfied, "确切版本下比自己新也不算达成（要么相等、要么未安装）"),
        // ── ② `^` 范围：>=want 且 < 上限（主版本非 0 -> 下一主版本；0.x -> 下一次版本）──
        new UpdateVerdictCase("^1.2.3", "1.2.3",  VersionCheck.Satisfied,    "^1.2.3 = [1.2.3, 2.0.0)：下界本身达成"),
        new UpdateVerdictCase("^1.2.3", "1.5.0",  VersionCheck.Satisfied,    "^1.2.3 = [1.2.3, 2.0.0)：1.5.0 在范围内"),
        new UpdateVerdictCase("^1.2.3", "2.0.0",  VersionCheck.NotSatisfied, "^1.2.3 = [1.2.3, 2.0.0)：2.0.0 恰在**上界之外** ⇒ 不达成（上一版自检正是在这里写错了）"),
        new UpdateVerdictCase("^1.2.3", "1.2.2",  VersionCheck.NotSatisfied, "^1.2.3 = [1.2.3, 2.0.0)：1.2.2 低于下界"),
        new UpdateVerdictCase("^0.5.8", "0.5.10", VersionCheck.Satisfied,    "^0.5.8 = [0.5.8, 0.6.0)：0.5.10 在范围内（同一次更新的连字符版本不能算未安装）"),
        new UpdateVerdictCase("^0.5.8", "0.6.0",  VersionCheck.NotSatisfied, "^0.5.8 = [0.5.8, 0.6.0)：0.6.0 在上界之外"),
        new UpdateVerdictCase("^0.2.3", "0.2.9",  VersionCheck.Satisfied,    "^0.2.3 = [0.2.3, 0.3.0)：0.x 特例，下一次版本是上限"),
        new UpdateVerdictCase("^0.2.3", "0.3.0",  VersionCheck.NotSatisfied, "^0.2.3 = [0.2.3, 0.3.0)：0.3.0 在上界之外（0.x 特例）"),
        // ── ③ `~` 范围：>=want 且 < 下一主/次版本（want 的 major.minor 固定）──
        new UpdateVerdictCase("~1.2.3", "1.2.3",  VersionCheck.Satisfied,    "~1.2.3 = [1.2.3, 1.3.0)：下界本身达成"),
        new UpdateVerdictCase("~1.2.3", "1.2.9",  VersionCheck.Satisfied,    "~1.2.3 = [1.2.3, 1.3.0)：1.2.9 在范围内"),
        new UpdateVerdictCase("~1.2.3", "1.3.0",  VersionCheck.NotSatisfied, "~1.2.3 = [1.2.3, 1.3.0)：1.3.0 在上界之外"),
        new UpdateVerdictCase("~1.2.3", "1.5.0",  VersionCheck.NotSatisfied, "~1.2.3 = [1.2.3, 1.3.0)：1.5.0 在上界之外"),
        new UpdateVerdictCase("~0.5.8", "0.5.10", VersionCheck.Satisfied,    "~0.5.8 = [0.5.8, 0.6.0)：0.5.10 在范围内"),
        new UpdateVerdictCase("~0.5.8", "0.6.0",  VersionCheck.NotSatisfied, "~0.5.8 = [0.5.8, 0.6.0)：0.6.0 在上界之外"),
        new UpdateVerdictCase("~0.2.14","0.2.20", VersionCheck.Satisfied,    "~0.2.14 = [0.2.14, 0.3.0)：0.2.20 在范围内"),
        new UpdateVerdictCase("~0.2.14","0.3.0",  VersionCheck.NotSatisfied, "~0.2.14 = [0.2.14, 0.3.0)：0.3.0 在上界之外"),
        // ── ④ 预发布后缀（本程序引擎/插件大量使用 -rc.N）──
        // 期望值全部以本机 npm semver 7.8.5 实测为准（探针脚本 `semver.satisfies(...)`）：
        //   0.1.0-rc.6 ∈ ^0.1.0-rc.6 -> true ；0.1.9 ∈ -> true ；0.2.0 ∈ -> false ；
        //   0.1.5-rc.1 ∈ -> false（换核心号）；0.2.0-rc.1 ∈ -> false（上界是 <0.2.0-0）。
        // 后两条正是本轮真跑契约表抓出来的第二个 bug：曾把 0.2.0-rc.1 误判成落在范围内。
        new UpdateVerdictCase("0.1.5-rc.1",       "0.1.5-rc.1", VersionCheck.Satisfied,    "带 -rc.N 的确切版本相等 ⇒ 达成"),
        new UpdateVerdictCase("^0.1.0-rc.6",      "0.1.0-rc.6", VersionCheck.Satisfied,    "^0.1.0-rc.6：等于下界本身（npm 实测 true）"),
        new UpdateVerdictCase("^0.1.0-rc.6",      "0.1.5-rc.1", VersionCheck.NotSatisfied, "^0.1.0-rc.6：0.1.5-rc.1 换了核心号 ⇒ npm 实测 false"),
        new UpdateVerdictCase("^0.1.0-rc.6",      "0.2.0-rc.1", VersionCheck.NotSatisfied, "^0.1.0-rc.6：上界是 <0.2.0-0，0.2.0-rc.1 在其上 ⇒ npm 实测 false（曾误判达成）"),
        new UpdateVerdictCase("^0.1.0-rc.6",      "0.1.9",      VersionCheck.Satisfied,    "^0.1.0-rc.6：正式版 0.1.9 在范围内（npm 实测 true）"),
        new UpdateVerdictCase("^0.1.0-rc.6",      "0.2.0",      VersionCheck.NotSatisfied, "^0.1.0-rc.6：正式版 0.2.0 到上界（开区间）⇒ npm 实测 false"),
        new UpdateVerdictCase("~0.2.14",          "0.2.14",     VersionCheck.Satisfied,    "~0.2.14：恰好等于下界 ⇒ 达成（npm 实测 true）"),
        new UpdateVerdictCase("~0.2.14",          "0.2.0-rc.1", VersionCheck.NotSatisfied, "~0.2.14：0.2.0-rc.1 核心号低于下界 ⇒ npm 实测 false"),
        new UpdateVerdictCase(">=0.1.5-rc.1",     "0.1.5-rc.1", VersionCheck.Satisfied,    ">=0.1.5-rc.1：恰好等于下界 ⇒ npm 实测 true"),
        new UpdateVerdictCase(">=0.1.5-rc.1",     "0.1.4",      VersionCheck.NotSatisfied, ">=0.1.5-rc.1：低于下界 ⇒ npm 实测 false"),
        // ── ⑤ `||` 多段：取第一段（更新目标一律是单一版本，多段是历史遗留写法）──
        new UpdateVerdictCase("^1.2.3 || ^2.0.0", "1.2.3", VersionCheck.Satisfied,    "多段 `||` 只取第一段 ^1.2.3 ⇒ 1.2.3 达成"),
        new UpdateVerdictCase("^1.2.3 || ^2.0.0", "3.0.0", VersionCheck.NotSatisfied, "多段 `||` 只取第一段 ^1.2.3 = [1.2.3, 2.0.0) ⇒ 3.0.0 不达成"),
        // ── ⑥ git 源：没有版本号可比 -> 一律不可判（交给命令退出码）──
        new UpdateVerdictCase("git+https://github.com/xiaosurongjia/dsh-improved-inline-edit.git",
                              "0.1.0", VersionCheck.Unknown, "git 源 targets 没有版本号可比 ⇒ 不可判"),
        new UpdateVerdictCase("github:aa2246740/dsh-watcher#2d19cb5a318912b87eb80603318554adeecfade2",
                              "0.4.0-insights.1", VersionCheck.Unknown, "github:o/r#sha ⇒ 不可判"),
        new UpdateVerdictCase("git://example.com/x.git", "1.0.0", VersionCheck.Unknown, "git:// ⇒ 不可判"),
        new UpdateVerdictCase("https://example.com/x.tgz", "1.0.0", VersionCheck.Unknown, "http(s) 归档直链 ⇒ 不可判"),
        new UpdateVerdictCase("file:../local-pkg", "1.0.0", VersionCheck.Unknown, "本地路径源 ⇒ 不可判"),
        // ── ⑦ 非具体版本的目标写法 -> 不可判（不属于"未安装"）──
        new UpdateVerdictCase("latest", "1.0.0", VersionCheck.Unknown, "latest 不是具体版本 ⇒ 不可判"),
        new UpdateVerdictCase("*",      "1.0.0", VersionCheck.Unknown, "* ⇒ 不可判"),
        new UpdateVerdictCase("1.x",    "1.0.0", VersionCheck.Unknown, "1.x 是范围式写法 ⇒ 不可判"),
        new UpdateVerdictCase("next",   "1.0.0", VersionCheck.Unknown, "dist-tag 不是版本号 ⇒ 不可判"),
        new UpdateVerdictCase("",       "1.0.0", VersionCheck.Unknown, "空目标 ⇒ 不可判"),
        // ── ⑧ 无法读取版本 ≠ 未安装（部分插件存在目录存在、version 字段缺失的情况）──
        new UpdateVerdictCase("0.5.9", "",   VersionCheck.Unknown, "无法读取磁盘版本 ⇒ 不可判（绝不当成未安装）"),
        new UpdateVerdictCase("0.5.9", null, VersionCheck.Unknown, "磁盘版本为 null ⇒ 不可判"),
    };

    /// <summary>
    /// 跑一遍语义契约表，返回人类可读的实际输出（自检的 detail 文案直接用它）。
    ///
    /// 关键（上一版返工的教训）：文案里的每个结论都是真调用一次
    /// <see cref="CompareInstalledToTarget"/> 拿到的实际返回值，不是另写一份期望值 ——
    /// 所以不可能再出现"detail 看着全对、断言却判 FAIL"。
    /// <paramref name="failed"/> 只统计"实际值 ≠ 表里期望值"的条数，不含任何靠文案字面猜的规则。
    /// </summary>
    public static string DescribeVerdictContracts(out int failed)
    {
        failed = 0;
        var parts = new List<string>();
        foreach (var c in UpdateVerdictContracts)
        {
            var actual = CompareInstalledToTarget(c.Installed, c.Target).Check;
            if (actual != c.Expected) failed++;
            string inst = c.Installed == null ? "null" : (c.Installed.Length == 0 ? "(读不到)" : c.Installed);
            string tgt = c.Target.Length == 0 ? "(空)" : c.Target;
            parts.Add($"{inst} vs {tgt} → {actual}（期望 {c.Expected}）");
        }
        return string.Join(" · ", parts);
    }

    /// <summary>目标说明符是不是 git 源（没有可比的版本号，只能看命令退出码）。</summary>
    public static bool LooksLikeGitTarget(string? targetSpec)
    {
        string t = (targetSpec ?? "").Trim().Trim('"', '\'');
        return t.StartsWith("git+", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("git://", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("github:", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("gitlab:", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("bitbucket:", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            || t.Contains(".git#", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 目标说明符里那个"要装成的版本号"：
    ///   `0.5.9` / `^0.5.9` / `~0.5.9` / `>=0.5.9` -> `0.5.9`；`^1.2.3 || ^2.0.0` -> `1.2.3`（第一段）；
    ///   `latest` / `*` / 空 / git 源（`git+https://…`、`github:o/r#sha`）-> 空串（无从比高低）。
    /// 纯函数，便于自检（git 源不可比这一条就是靠它落地）。
    /// </summary>
    public static string ConcreteVersionOf(string? targetSpec)
    {
        try
        {
            string t = (targetSpec ?? "").Trim().Trim('"', '\'');
            if (t.Length == 0) return "";
            if (LooksLikeGitTarget(t)) return "";
            if (t.Equals("latest", StringComparison.OrdinalIgnoreCase)) return "";
            if (t == "*" || t == "x" || t == "X") return "";

            // `a || b` 只取第一段：更新时目标一律是单一版本，多段是历史遗留写法
            int or = t.IndexOf("||", StringComparison.Ordinal);
            if (or >= 0) t = t.Substring(0, or).Trim();
            t = t.Split(' ')[0].Trim();                       // 去掉 `>=1.2.3 <2.0.0` 的第二段
            if (t.Length == 0) return "";
            if (t.Contains('*') || t.Contains('x') || t.Contains('X')) return "";   // `1.x` 这类范围式写法
            t = t.TrimStart('^', '~', '>', '<', '=', 'v', ' ');
            // 信任边界：必须整体是版本号（^ 锚定）。旧写法用未锚定的 Regex.Match，
            //   会把 `99.0.0&calc` 静默截成 `99.0.0` 参与后续拼接/比较（尾部垃圾丢失）。
            return Regex.IsMatch(t, @"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.\-]+)?$") ? t : "";
        }
        catch { return ""; }
    }

    /// <summary>
    /// 更新/安装的真正判据：把磁盘上实际装着的版本与目标说明符比一比（纯函数，便于自检）。
    ///
    ///   · 目标可解析成具体版本 + 磁盘版本读到了：
    ///       相等 -> <see cref="VersionCheck.Satisfied"/>；
    ///       `^`/`~` 写法 -> 落在 npm 的兼容范围内也算满足（范围比较本就是"目标达成"的字面含义）；
    ///       否则 -> <see cref="VersionCheck.NotSatisfied"/>。
    ///   · 目标不可比（git 源 / latest / 空）但磁盘上存在任何可读版本 -> Unknown（交由命令退出码），
    ///     绝不因"无法比较高低"而误报失败或成功。
    ///   · 无法读取版本 -> 一律 Unknown（无法读取 ≠ 未安装，见 <see cref="ReadInstalledVersion"/> 的现场记录）。
    ///
    /// 预发布（`-rc.1` 这类）按 npm 规则把关，见 <see cref="IsInstalledWithinRange"/> 的说明：
    /// 上一版这里留了"预发布按核心号算范围内"的简化，真跑契约表时被
    /// `0.2.0-rc.1` vs `^0.1.0-rc.6` 抓出来（npm 判不满足，本程序曾误判满足）-> 已改为严格规则。
    /// 误判方向始终偏保守：只在"相等 / 确实落在范围内"时才判达成，比不出来就交回退出码。
    /// </summary>
    public static VersionVerdict CompareInstalledToTarget(string? installedVersion, string? targetSpec)
    {
        string inst = (installedVersion ?? "").Trim().Trim('"', '\'');
        string target = (targetSpec ?? "").Trim();

        if (inst.Length == 0)
            return new VersionVerdict(VersionCheck.Unknown, "", target,
                "无法读取磁盘上已安装的版本（不等于未安装）⇒ 只能按命令退出码判定");

        string want = ConcreteVersionOf(target);
        if (want.Length == 0)
            return new VersionVerdict(VersionCheck.Unknown, inst, target,
                LooksLikeGitTarget(target)
                    ? $"git 源没有版本号可比（磁盘上是 {inst}）⇒ 只能按命令退出码判定"
                    : $"目标「{target}」不是具体版本号（磁盘上是 {inst}）⇒ 只能按命令退出码判定");

        // 信任边界：两侧任一不可解析为纯版本号（如恶意 latest `99.0.0&calc`、尾部带垃圾）
        //   -> Unknown（失败关闭）。旧 Compare 的 0 值在这里曾会被误读成"相等"，
        //   把"比不出来"当成"版本到位"，故必须显式判可解析（Compare 语义修正的配套闸门）。
        if (!VersionInfo.IsComparableVersion(inst) || !VersionInfo.IsComparableVersion(want))
            return new VersionVerdict(VersionCheck.Unknown, inst, want,
                $"版本「{inst}」/「{want}」中有一侧无法解析为纯版本号 ⇒ 不可比，只能按命令退出码判定");

        if (VersionInfo.Compare(inst, want) == 0)
            return new VersionVerdict(VersionCheck.Satisfied, inst, want, $"磁盘版本 {inst} == 目标 {want}");

        bool range = target.TrimStart().StartsWith("^") || target.TrimStart().StartsWith("~");
        if (range && IsInstalledWithinRange(inst, want, target.TrimStart()[0]))
            return new VersionVerdict(VersionCheck.Satisfied, inst, want,
                $"磁盘版本 {inst} 落在目标范围 {target} 内（同一次更新里的连字符版本不能算未安装）");

        return new VersionVerdict(VersionCheck.NotSatisfied, inst, want,
            $"磁盘上仍是 {inst}，目标 {want} 未安装");
    }

    /// <summary>
    /// 磁盘版本是否落在 `^want` / `~want` 的范围里（纯函数）：
    ///   `^1.2.3` -> [1.2.3, 2.0.0)；`^0.2.3` -> [0.2.3, 0.3.0)（npm 对 0.x 的规则）；`~1.2.3` -> [1.2.3, 1.3.0)。
    ///
    /// 预发布单独把关（本轮真跑契约表抓出来的第二个 bug）：不能用
    /// <see cref="VersionInfo.Compare"/> 把带后缀的版本直接和"上界"比 —— 它把 `0.2.0-rc.1` 与 `0.2.0`
    /// 判相等（核心号相同、预发布只影响同核心号之间的先后），于是 `0.2.0-rc.1` 会被
    /// `^0.1.0-rc.6` 的上界判成"没超" -> 误报达成。
    ///
    /// 规则以本机 npm semver 7.8.5 实测为准（`node -e "require('semver').satisfies(...)"`，
    /// 探针输出见交付说明；`^` 展开为 `&gt;=下界 &lt;上界-0`）：
    ///   · 磁盘版本是正式版 -> 核心号落在 [下界核心号, 上界核心号) 即算范围内；
    ///   · 磁盘版本带预发布后缀 -> 只有它的核心号恰好等于下界或上界核心号时才算范围内。
    /// 实测对照：`0.1.0-rc.6 ∈ ^0.1.0-rc.6` ✓、`0.1.9 ∈` ✓、`0.2.0 ∉` ✗、
    /// `0.1.5-rc.1 ∉` ✗（换了核心号）、`0.2.0-rc.1 ∉` ✗（与上界核心号相同但 npm 仍判不满足 ——
    /// 因为上界是 `&lt;0.2.0-0`，而 `0.2.0-rc.1 &gt; 0.2.0-0`）-> 上界这一侧只认正式版。
    /// </summary>
    public static bool IsInstalledWithinRange(string installedVersion, string wantVersion, char rangeOp)
    {
        try
        {
            string inst = (installedVersion ?? "").Trim();
            if (inst.Length == 0) return false;

            var parts = wantVersion.Split('-')[0].Split('.');
            int major = parts.Length > 0 && int.TryParse(parts[0], out int a) ? a : 0;
            int minor = parts.Length > 1 && int.TryParse(parts[1], out int b) ? b : 0;

            // `^`：主版本非 0 时上限是下一个主版本；主版本为 0 时上限是下一个次版本（npm 规则）
            int upMajor = rangeOp == '^' ? (major == 0 ? 0 : major + 1) : major;
            int upMinor = rangeOp == '^' ? (major == 0 ? minor + 1 : 0) : minor + 1;
            string lowerCore = CoreVersionOf(wantVersion);
            string upperCore = $"{upMajor}.{upMinor}.0";
            string instCore = CoreVersionOf(inst);
            if (instCore.Length == 0) return false;

            if (VersionInfo.Compare(instCore, lowerCore) < 0) return false;      // 低于下界核心号
            if (VersionInfo.Compare(instCore, upperCore) >= 0) return false;     // 到/过上界核心号（开区间）

            // 预发布：只认"与下界同核心号"（上界 `-0` 意味着上界那一侧不认预发布）
            if (inst.Contains('-')) return instCore == lowerCore;
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 只取 `major.minor.patch` 那一段（丢掉 `-rc.1` 这类预发布后缀），供 npm 预发布规则的元组比较。
    /// `0.2.0-rc.1` -> `0.2.0`；`0.1.0-rc.6` -> `0.1.0`；空/异常 -> 空串。纯函数。
    /// </summary>
    public static string CoreVersionOf(string? version)
    {
        try
        {
            string v = (version ?? "").Trim().TrimStart('v');
            int dash = v.IndexOf('-');
            if (dash >= 0) v = v.Substring(0, dash);
            var m = Regex.Match(v, @"^(\d+\.\d+\.\d+)");
            return m.Success ? m.Groups[1].Value : "";
        }
        catch { return ""; }
    }

    /// <summary>
    /// 从 `dsh --dump-config` 输出里解析权威的"包名 -> loader id"映射。
    /// 输出形如：
    ///     - id: modsearch
    ///       name: '@liustack/modsearch'
    /// 必须用这里的 name 行来对号入座 —— 现场证明 id 无法从包名推导
    /// （`@liustack/modsearch` 的真实 id 就是 `modsearch`，按 scope-name 猜会得到 `liustack-modsearch` -> DSH 报 entry not found）。
    /// </summary>
    public static Dictionary<string, string> ParseLoaderIdNames(string? dump)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string? lastId = null;
            foreach (var raw in (dump ?? "").Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("- id:", StringComparison.Ordinal))
                {
                    lastId = NormalizeId(line.Substring("- id:".Length).Trim());
                    continue;
                }
                if (lastId != null && line.StartsWith("name:", StringComparison.Ordinal))
                {
                    string nm = line.Substring("name:".Length).Trim().Trim('\'', '"');
                    if (nm.Length > 0 && lastId.Length > 0) map[nm] = lastId;
                    lastId = null;
                }
            }
        }
        catch (Exception ex) { Logger.LogError("PluginManager.ParseLoaderIdNames", ex); }
        return map;
    }

    /// <summary>
    /// 从 dsh 的 stderr 中识别"插件清单已登记、机器上却未安装"的那个包名。
    ///
    /// 现场原文（本机实测：`npx --yes @deepseek-ai/dsh@0.1.5-rc.2 --profile web --dump-config`
    /// 直接崩、退出码 1、stdout 为空）：
    ///   Error: dsh: cannot resolve profile bundle "@furongjun1999/dsh-memory" from the dsh installation or
    ///   &lt;profileDir&gt;; run 'dsh plugin --profile web install' if its dependency is not installed
    /// 这句来自 dsh-app-boot 的 resolveBundleDir（包名经 JSON.stringify -> 一定是双引号；
    /// 单引号一并识别，以防后续版本改为手写引号）。dump 一旦如此失败，整份配置都无法读取 ->
    /// 所有插件均无法取得 id -> 界面出现"全部无法禁用"（现场根因）。
    /// 识别包名的用途：① 依照 dsh 自身的提示补装一次；② 补装失败时将是哪个包写入界面与弹窗。
    ///
    /// 只认"包名被引号包着"这一种形态：dsh 各版本的模板都带引号，放宽成"短语后随便取一个词"
    /// 会把 "cannot resolve profile bundle from …" 里的 from 当成包名（假阳性）。
    /// 认不出返回空串（纯函数，便于自检）。
    /// </summary>
    public static string ParseUnresolvedBundle(string? stderrText)
    {
        try
        {
            string s = stderrText ?? "";
            if (s.Length == 0) return "";

            var m = Regex.Match(s, @"cannot\s+resolve\s+profile\s+bundle\s*[""']([^""'\r\n]{1,214})[""']");
            return m.Success ? m.Groups[1].Value.Trim() : "";
        }
        catch (Exception ex) { Logger.LogError("PluginManager.ParseUnresolvedBundle", ex); return ""; }
    }

    /// <summary>DSH 报"patch entry X not found"的那些 id（说明补丁层里有无效条目）。</summary>
    public static List<string> ParseMissingPatchEntries(string? dump)
    {
        var list = new List<string>();
        try
        {
            foreach (var raw in (dump ?? "").Split('\n'))
            {
                var m = System.Text.RegularExpressions.Regex.Match(raw, "patch: entry \"([^\"]+)\" not found");
                if (m.Success) list.Add(m.Groups[1].Value);
            }
        }
        catch { }
        return list;
    }

    /// <summary>
    /// 清理无效的禁用条目：id 既不是任何插件的 loader id、也不是任何包名的，整块移除（先备份）。
    /// 依据是 DSH 自己的 "patch: entry X not found" 提示 —— 权威且零猜测。
    /// </summary>
    public static int DropMissingEntries(IEnumerable<string> missingIds)
    {
        try
        {
            var bad = new HashSet<string>(missingIds ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            if (bad.Count == 0 || !File.Exists(PatchFile)) return 0;

            var lines = File.ReadAllLines(PatchFile).ToList();
            var keep = new List<string>();
            int dropped = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                string? id = IdFromLine(lines[i]);
                if (id != null && bad.Contains(id))
                {
                    // 连同前面的注释行一起丢掉（注释以 # DSHGuard 开头时）
                    while (keep.Count > 0 && keep[^1].TrimStart().StartsWith("# DSHGuard", StringComparison.Ordinal)) keep.RemoveAt(keep.Count - 1);
                    i++;                                        // 跳过下一行（disabled/config）
                    while (i + 1 < lines.Count && lines[i + 1].StartsWith("  ")) i++;
                    dropped++;
                    continue;
                }
                keep.Add(lines[i]);
            }
            if (dropped == 0) return 0;
            BackupPatchFile();
            var (ok, _) = WritePatchChecked(string.Join("\n", keep).TrimEnd() + "\n");
            if (!ok) return 0;
            Logger.Log($"已清理 {dropped} 条 DSH 认不出的补丁条目（entry not found）");
            return dropped;
        }
        catch (Exception ex) { Logger.LogError("PluginManager.DropMissingEntries", ex); return 0; }
    }
    /// <summary>解析 `dsh --dump-config` 输出：包名 -> loader id。</summary>
    public static Dictionary<string, string> ParseLoaderIds(string dumpConfigOutput)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? lastId = null;
        foreach (var raw in (dumpConfigOutput ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("- id:")) { lastId = NormalizeId(line.Substring(5).Trim()); }
            else if (line.StartsWith("name:") && lastId != null)
            {
                var name = line.Substring(5).Trim().Trim('\'', '"');
                if (name.Length > 0) map[name] = lastId;
                lastId = null;
            }
        }
        return map;
    }

    /// <summary>
    /// 卸载参数，交给 <c>RunCommandAsync("npx", …)</c> 执行。
    /// 包名来自插件清单（外部输入），拼接前过 <see cref="IsValidPackageName"/> 白名单：
    /// 不合法（含 shell 元字符、<c>-</c> 开头 token、超长等）-> 返回空串，调用方不得执行。
    /// </summary>
    public static string BuildUninstallArgs(string packageName)
    {
        if (!IsValidPackageName(packageName))
        {
            Logger.NoteDiagnosis($"插件卸载被拒绝：包名「{packageName}」不是合法的 npm 包名（白名单拦截）");
            return "";
        }
        return $"--yes @deepseek-ai/dsh@{VersionMemory.Spec} plugin --profile web remove {packageName} --registry {Registries.Current} {PolicyOverride}";
    }

    /// <summary>
    /// 更新或安装指定版本的参数；版本说明符取自版本记忆，避免核心版本被改变。
    /// 包名与版本都来自外部输入（清单 dependencies 键 / 镜像源 dist-tags），分别过
    /// <see cref="IsValidPackageName"/> 与 <see cref="IsValidVersionSpec"/> 白名单：
    /// 任一不合法 -> 返回空串，调用方不得执行（<c>99.0.0&amp;calc</c> 这类恶意 latest 就死在这里）。
    /// </summary>
    public static string BuildAddArgs(string packageName, string version)
    {
        if (!IsValidPackageName(packageName) || !IsValidVersionSpec(version))
        {
            Logger.NoteDiagnosis($"插件安装被拒绝：包名「{packageName}」或版本「{version}」未通过白名单（npm 规则 / semver 范围）");
            return "";
        }
        return $"--yes @deepseek-ai/dsh@{VersionMemory.Spec} plugin --profile web add {packageName}@{version} --registry {Registries.Current} {PolicyOverride}";
    }

    /// <summary>
    /// 「按清单把缺的插件补齐」的参数 —— 就是 dsh 崩溃提示里让它自己跑的那条
    /// （<c>dsh plugin --profile web install</c>，不带包名 = 按 profile 的清单装齐）。
    /// 只补充本程序一贯使用的镜像源与供应链策略覆盖：在这台机器上不加这两个开关会卡在
    /// pnpm 的锁文件/包龄策略上（见 <see cref="PolicyOverride"/> 的现场记录）。
    /// </summary>
    public static string BuildInstallAllArgs() =>
        $"--yes @deepseek-ai/dsh@{VersionMemory.Spec} plugin --profile web install --registry {Registries.Current} {PolicyOverride}";

    /// <summary>
    /// 按「安装源」安装的参数（市场使用）：源可能是 <c>dsh-xxx@1.2.3</c>、<c>dsh-xxx</c> 或 <c>github:owner/repo</c>。
    /// 源中已含版本说明符时不再追加版本。
    ///
    /// 本方法是所有"按来源安装"的唯一入口，因此在此处拦截界面显示标签
    ///   （<c>仓库最新</c> / <c>最新</c> / <c>有新版（日期）</c> 这类含非 ASCII 字符或空格的整句）：
    ///   它们不是任何合法的 npm/git 来源，pnpm 只会报
    ///   <c>Failed to resolve dependency tree: "dsh-codearts-auth@仓库最新" isn't supported by any available resolver.</c>
    ///   （现场原文）。被拒绝的值返回空串，调用方不得执行 —— 由此"标签进入命令行"这一路径被完全封堵，
    ///   即使某个调用点遗漏修改亦不受影响。
    ///
    /// 放行口径刻意保持宽松（仅拦截标签，不拦截其它形式）：`dsh-xxx@1.2.3`、`@scope/name@1.2.3`、
    /// 裸包名、`github:o/r`、`git+https://…` 一律照旧可用 —— 市场的安装路径不得因本次修改而失效。
    ///
    /// 信任边界（本轮加固）：除显示标签外，来源 spec 在拼接前按形态过闭合白名单 ——
    ///   <see cref="IsValidGitSource"/>（git/URL 形态，host 限 github.com / gitee.com / gitlab.com /
    ///   bitbucket.org，拒 file:、UNC、本地路径）或 <see cref="IsValidPackageName"/> +
    ///   <see cref="IsValidVersionSpec"/>（npm 形态）。`x&amp;calc`、`--registry=https://evil.example`、
    ///   `\\attacker\share\evil.exe` 这类值在此被拒（返回空串），镜像源重定向与命令注入死于源头。
    /// </summary>
    public static string BuildAddSourceArgs(string source)
    {
        string src = (source ?? "").Trim();
        if (src.Length == 0 || IsDisplayLabel(src))
        {
            if (src.Length > 0)
                Logger.NoteDiagnosis($"插件安装被拒绝：来源「{src}」是界面显示标签，不是合法安装来源");
            return "";
        }

        // 调用方传入的是"清单中登记为 git 源的包名"（不带版本、也无 git 前缀）->
        //   需补全为清单中那个来源地址。此步骤的必要性：更新 git 源插件时，
        //   若写成 `add <包名>`（旧写法，交由 pnpm 按名称解析），
        //   git 源的包在镜像源上不存在、按名称解析必然失败 —— 必须使用仓库地址本身。
        //   仅在"清单确实将其声明为 git 源"时替换，因此不影响按名称安装 npm 包的正常流程。
        if (PluginSource.Classify(src) == PluginSource.Kind.Registry && !src.Contains('@'))
        {
            string declared = DepSpec(src);
            string git = GitSourceSpec(declared);
            // 仅当清单中声明的 spec 为地址形态时才替换；若清单中写的是裸包名
            // （理论上不会出现，git 源必定带前缀或 URL），则保持原值不变。
            if (git.Length > 0 && PluginSource.Classify(declared) != PluginSource.Kind.Registry) src = git;
        }

        // 白名单闸门：git/URL 形态按 IsValidGitSource；npm 形态（可带 @version）按包名+版本拆开验。
        if (PluginSource.Classify(src) != PluginSource.Kind.Registry)
        {
            if (!IsValidGitSource(src))
            {
                Logger.NoteDiagnosis($"插件安装被拒绝：git/URL 来源「{src}」不在允许的主机与形态白名单内");
                return "";
            }
        }
        else
        {
            string name = src, ver = "";
            int at = LastVersionAt(src);
            if (at > 0)
            {
                name = src.Substring(0, at);
                ver = src.Substring(at + 1);
                if (!IsValidVersionSpec(ver))
                {
                    Logger.NoteDiagnosis($"插件安装被拒绝：来源「{src}」里的版本段「{ver}」未通过 semver 白名单");
                    return "";
                }
            }
            if (!IsValidPackageName(name))
            {
                Logger.NoteDiagnosis($"插件安装被拒绝：来源「{src}」里的包名段「{name}」不是合法的 npm 包名");
                return "";
            }
        }

        return $"--yes @deepseek-ai/dsh@{VersionMemory.Spec} plugin --profile web add {src} --registry {Registries.Current} {PolicyOverride}";
    }

    /// <summary>
    /// 来源 spec 里版本分隔符 @ 的位置（纯函数）：从右往左找第一个可作为版本分隔的 @。
    /// scope 包名（<c>@scope/name</c>）开头的 @ 不算；`@scope/name@1.2.3` 里第二个 @ 才算。
    /// 找不到 -> -1。供 <see cref="BuildAddSourceArgs"/> 拆包名/版本段。
    /// </summary>
    private static int LastVersionAt(string src)
    {
        for (int i = src.Length - 1; i > 0; i--)
        {
            if (src[i] != '@') continue;
            char prev = src[i - 1];
            if (prev == '/') continue;              // @scope/ 的开头（@ 在 / 之后）
            if (i == 0 || prev == '@') continue;
            return i;                               // 前一个字符是包名成分 -> 这是版本分隔符
        }
        return -1;
    }

    /// <summary>
    /// git 源在"更新 / 重装"时应使用的来源 spec（纯函数，便于自检）：即仓库地址本身。
    ///   · <c>git+https://host/repo.git</c>（未指定 ref）    -> 原样（跟随默认分支）
    ///   · <c>github:o/r</c>（跟随默认分支）                 -> 原样
    ///   · <c>github:o/r#main</c>（跟随分支/标签）           -> 原样（ref 须保留，用户即跟随该 ref）
    ///   · <c>git+https://host/repo.git#&lt;sha&gt;</c> / <c>github:o/r#&lt;sha&gt;</c>（固定提交）
    ///     -> 移除 #ref（<see cref="PluginSource.RepoWithoutRef"/>），否则将永远只能安装该旧提交。
    /// 无法识别为 git 源 -> 空串（调用方不得执行）。
    ///
    /// 注意：不复用 <c>PluginSource.RefreshSpec</c>：其对 GitBare / GitRef 两个分支返回的是包名，
    /// 而包名无法在镜像源上解析 git 源（现场即因此失败）—— 此处一律返回仓库地址。
    /// </summary>
    public static string GitSourceSpec(string? spec)
    {
        var k = PluginSource.Classify(spec);
        return k switch
        {
            PluginSource.Kind.GitCommit => PluginSource.RepoWithoutRef(spec),
            PluginSource.Kind.GitRef => (spec ?? "").Trim(),
            PluginSource.Kind.GitBare => (spec ?? "").Trim(),
            _ => ""
        };
    }

    /// <summary>
    /// 更新参数的唯一入口（按"来源类型"分流；纯函数，便于自检）。
    ///
    /// 现场 bug（异常-20260916-135514.log 原文，13:59:24->13:59:25）：
    ///   npx … plugin --profile web add dsh-codearts-auth@仓库最新 … 退出码=1
    ///   ─▶ Failed to resolve dependency tree: "dsh-codearts-auth@仓库最新" isn't supported by any available resolver.
    /// 界面上的显示文案「仓库最新」被作为版本号拼入命令（`包名@仓库最新`），pnpm 必然无法解析。
    /// 「仓库最新」仅为界面显示标签，从不是版本号，绝不得出现在命令行中。
    ///
    /// 分流规则：
    ///   · npm 源 -> <c>包名@版本号</c>（<see cref="BuildAddArgs"/>）；
    ///   · git 源（<c>git+…</c> / <c>github:o/r</c> / …）-> 清单中的来源 spec 本身
    ///     （经 <see cref="GitSourceSpec"/> 归一后走 <see cref="BuildAddSourceArgs"/>）；
    ///   · 来源无法识别（spec 为空 / 无法得出来源地址）-> 返回空串，调用方不得执行
    ///     （宁可不动，也不得用无效版本号尝试）。
    ///
    /// <paramref name="spec"/> 一律传入清单中的原始声明（<see cref="DepSpec"/>），而非显示标签。
    /// </summary>
    public static string BuildUpdateArgs(string packageName, string? spec, string? latestLabel)
    {
        string name = (packageName ?? "").Trim();
        if (name.Length == 0) return "";

        var kind = PluginSource.Classify(spec);
        if (kind == PluginSource.Kind.Registry)
        {
            string ver = (latestLabel ?? "").Trim();
            // 版本位上是"显示标签"而非版本号：就地纠正为清单中声明的那一段版本
            // （`^1.2.3` -> `1.2.3`）。仅在无法得出具体版本号时拒绝拼接命令 ——
            // 无论如何都不会将「仓库最新」这类标签写入命令行（即现场该 bug 的表现形式）。
            if (ver.Length == 0 || IsDisplayLabel(ver))
            {
                ver = ConcreteVersionOf(spec);
                if (ver.Length == 0) return "";
            }
            return BuildAddArgs(name, ver);
        }
        if (kind == PluginSource.Kind.Unknown) return "";

        // git 源：改用 `update 包名`，不再用 `add 仓库地址`（2026-09-18 现场缺陷，下面是原话级记录）。
        //
        // 现场（用户：「dsh watcher更新以后又报更新，看看？」）：
        //   本分支原先给出 `add <仓库地址>`（无 ref）：
        //     npx … plugin --profile web add github:aa2246740/dsh-watcher
        //   pnpm 对已解析过的 git 源认为锁文件里那条旧提交已满足该 spec -> 直接
        //     「Lockfile is up to date, resolution step is skipped」
        //   退出码 0、锁文件也被重写（mtime 变），但提交没动 -> 界面记成"已更新"、下次照旧报有新版。
        //   本机隔离实测（pnpm 12.3.4，%TEMP% 自建对照工程）：
        //     add <裸地址>        -> 锁 118049af 不动、磁盘 0.4.0-insights.1 不动（复现 ✗）
        //     add --force         -> 同上，仍跳过解析（✗）
        //     install --force     -> 同上（✗）
        //     add <地址>#<sha>    -> 推进 ✓ 但把 #sha 写进用户 manifest -> 从"跟分支"变"钉死提交"（✗ 见下）
        //     update 包名         -> 锁 118049af->c7a048ae、磁盘 0.4.0-insights.1->0.4.1，
        //                           且 manifest 一字未改（仍是跟分支）✓ ← 采用这一支
        //
        // 为什么不用 `add <仓库地址>#<sha>`（必须避免的陷阱）：
        //   把 `#<sha>` 写进 add 的 spec，DSH 会把它原样写进用户的 package.json -> 用户的依赖
        //   从「跟默认分支」变成「钉死提交」-> <see cref="PluginSource.Classify"/> 从此判成
        //   <c>GitCommit</c> -> 本壳按设计"钉死的提交永不报更新" -> 等于把插件永久钉死。
        //   本机实测确认会发生：manifest 由 `github:aa2246740/dsh-watcher` 变成
        //   `github:aa2246740/dsh-watcher#c7a048ae…`。`update 包名` 不改写 manifest（实测同上），
        //   故这是唯一"既真推进、又不改用户声明"的形态。
        //
        // 为什么不是 `update 仓库地址`：`update` 的参数是包名（按清单里的声明重解析）；
        //   仓库地址/文件路径会被 pnpm 当成本地路径规格（dsh 还会把相对路径锚定到调用目录），语义不对。
        //
        // 信任边界：`update` 只接受包名，故这里过 <see cref="IsValidPackageName"/>
        //   （与 BuildAddArgs / BuildUninstallArgs 同一条白名单）。非法包名（含 @版本段、含 shell
        //   元字符、超长…）-> 返回空串，调用方不得执行 —— 命令里不再出现任何仓库地址/ref，注入面反而变小。
        //
        // 注意：钉死提交（<see cref="PluginSource.Kind.GitCommit"/>）走同一支也是安全的：
        //   提交不可变，`update 包名` 只按 manifest 里那个 `#sha` 重解析 -> 原地不动（本机实测），
        //   不会把用户钉的提交解开（那属于"改用户声明"，不是本壳该做的事）。
        if (!IsValidPackageName(name))
        {
            Logger.NoteDiagnosis($"插件更新被拒绝：包名「{name}」不是合法的 npm 包名（白名单拦截，git 源更新走 update 包名）");
            return "";
        }
        return $"--yes @deepseek-ai/dsh@{VersionMemory.Spec} plugin --profile web update {name} --registry {Registries.Current} {PolicyOverride}";
    }

    // ══════════ 外部输入白名单（信任边界的唯一源头） ══════════
    //
    // 包名 / 版本 / git 源都会被拼进 `npx dsh …` 命令行（BuildAddArgs / BuildUninstallArgs /
    // BuildAddSourceArgs / BuildUpdateArgs），而这些值的来源全是外部输入：
    // 插件清单 dependencies 的键（Scan :476）、镜像源 dist-tags、市场远程目录 JSON。
    // 防线口径与 Registries 一样是闭合白名单：只放行明确认识的形态，一律只拒绝、不清洗
    // —— 被拒的值返回空串（既有约定），调用方必须给中性提示且不得执行命令。
    //
    // 注意：未来新增任何 `Build*Args` 入口，必须在拼接前调用这里的校验函数；
    //   命令行里凡是来自外部的 token 都不得绕过本区直拼。

    /// <summary>npm 包名允许的字符（闭合白名单）：小写字母、数字、连字符、下划线、点、波浪线。</summary>
    private static bool IsPackageNameChar(char c) =>
        (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.' || c == '~';

    /// <summary>
    /// npm 包名白名单（纯函数）：支持 scope（`@scope/name`），单段内只允许小写字母数字与
    /// <c>- _ . ~</c>，恰好一段 <c>/</c>，整串长度 ≤214（npm 规则）。
    /// 拒绝：空、大写、空白、<c>&amp; | ^ &gt; &lt; % $ ; ' " ` ( )</c> 等一切 shell/命令行元字符、
    /// 非 ASCII、以 <c>-</c> 或 <c>.</c> 开头的 token、连续点、点结尾。
    /// </summary>
    public static bool IsValidPackageName(string? name)
    {
        string s = (name ?? "").Trim();
        if (s.Length == 0 || s.Length > 214) return false;
        if (s.StartsWith('-') || s.StartsWith('.')) return false;

        string[] segs = s.Split('/');
        if (segs.Length > 2) return false;                                  // 最多一段 /
        if (segs.Length == 2)
        {
            // scope 段：必须以 @ 开头，且去掉 @ 后非空、同样走字符白名单
            if (segs[0].Length < 2 || segs[0][0] != '@') return false;
            string scope = segs[0].Substring(1);
            if (scope[0] == '.' || scope[scope.Length - 1] == '.') return false;
            foreach (char c in scope) if (!IsPackageNameChar(c)) return false;
            if (segs[1].Length == 0 || segs[1][0] == '.' || segs[1][segs[1].Length - 1] == '.') return false;
            foreach (char c in segs[1]) if (!IsPackageNameChar(c)) return false;
            return true;
        }

        if (s[0] == '.' || s[s.Length - 1] == '.') return false;
        foreach (char c in s) if (!IsPackageNameChar(c)) return false;
        return true;
    }

    /// <summary>
    /// 版本/版本范围白名单（纯函数）：semver 核心号（可带预发布后缀）与常见范围写法
    /// （<c>^ ~ &gt;= &lt;= &gt; &lt; = 1.x 1.2.x * latest</c>，可带 <c>v</c> 前缀）。
    /// 闭合字符集：数字、字母、<c>. - _ ~ ^ &gt; &lt; = v V</c>（后者只在开头做前缀），其余一律拒绝。
    /// 额外拒绝：空、空白（含换行）、<c>-</c> 开头 token（<c>--registry=…</c> 这类参数注入）、
    /// <c>..</c>、非 ASCII、超长（&gt;64）、前缀符号出现在中后部。
    /// </summary>
    public static bool IsValidVersionSpec(string? version)
    {
        string s = (version ?? "").Trim();
        if (s.Length == 0 || s.Length > 64) return false;
        if (s.StartsWith('-')) return false;                                // 显式拒绝 - 开头 token
        foreach (char c in s)
        {
            if (char.IsWhiteSpace(c)) return false;                         // 空格 / 换行 / 制表
            if (c > 0x7F) return false;                                     // 非 ASCII
            if (c == '*') return s.Length == 1;                             // 仅孤立 * 放行
            // 闭合字符集（^ < > = 只能当范围前缀，见下：主体里不再出现）
            if (!(char.IsLetterOrDigit(c) && c <= 0x7F) && c != '.' && c != '-' &&
                c != '_' && c != '~' && c != '^' && c != '>' && c != '<' && c != '=') return false;
        }
        if (s.Contains("..")) return false;

        string t = s;
        if (t.Equals("latest", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.Equals("x", StringComparison.OrdinalIgnoreCase) || t.Equals("X", StringComparison.Ordinal)) return true;

        // 剥范围/版本前缀（可叠一层：如 ^v1.2.3、>=v1.2.3）
        t = t.TrimStart('^', '~', 'v', 'V');
        if (t.StartsWith(">=")) t = t.Substring(2);
        else if (t.StartsWith("<=")) t = t.Substring(2);
        else if (t.StartsWith('>') || t.StartsWith('<') || t.StartsWith('=')) t = t.Substring(1);
        t = t.TrimStart('^', '~', 'v', 'V');
        if (t.Length == 0) return false;

        // 前缀符号只许出现在开头：主体里再出现 ^ > < = 即拒绝（防 "1.2.3^x" 这类夹带）
        foreach (char c in t)
            if (c == '^' || c == '>' || c == '<' || c == '=') return false;

        // 1.x / 1.2.x 形态（x 大小写均可）
        if (Regex.IsMatch(t, @"^\d+(\.\d+)?\.x$", RegexOptions.IgnoreCase)) return true;

        // semver 核心 + 可选预发布后缀（后缀只允许字母数字与 . -）
        return Regex.IsMatch(t, @"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.\-]+)?$");
    }

    /// <summary>安装来源允许的 git 托管主机（闭合白名单；与任务口径一致）。</summary>
    private static readonly string[] AllowedGitHosts =
        { "github.com", "gitee.com", "gitlab.com", "bitbucket.org" };

    /// <summary>
    /// git / URL 安装来源白名单（纯函数）：
    ///   · <c>github:owner/repo[#ref]</c>、<c>gitlab:o/r</c>、<c>bitbucket:o/r</c> 简写；
    ///   · <c>https://host/…</c> 或 <c>git+https://host/…</c>，host 必须在 <see cref="AllowedGitHosts"/>；
    ///   · 带 <c>#ref</c> 时 ref 只允许提交/分支/标签常用字符（字母数字 . - _ /）。
    /// 拒绝：<c>file:</c>、<c>git://</c>、本地路径、UNC（<c>\\srv\share</c>）、含空白、
    /// 非 ASCII、<c>user@host:</c> 的 scp 形态、白名单之外的任何 host。
    /// </summary>
    public static bool IsValidGitSource(string? source)
    {
        string s = (source ?? "").Trim().Trim('"', '\'');
        if (s.Length == 0 || s.Length > 300) return false;
        foreach (char c in s)
        {
            if (char.IsWhiteSpace(c) || c > 0x7F) return false;
            if ("&|<>%\"'`();$".IndexOf(c) >= 0) return false;              // `#`、`@`、`:`、`/`、`=`、`?` 是合法成分
        }
        if (s.Contains("..")) return false;

        // 简写形态：github: / gitlab: / bitbucket: + owner/repo（可选 #ref）
        int colon = s.IndexOf(':');
        if (colon > 0 && !s.StartsWith("git+", StringComparison.OrdinalIgnoreCase))
        {
            string scheme = s.Substring(0, colon).ToLowerInvariant();
            if (scheme is "github" or "gitlab" or "bitbucket")
            {
                string rest = s.Substring(colon + 1);
                if (rest.StartsWith("//")) return false;                    // 防 github://host/… 变体
                return IsValidGitOwnerRepo(rest);
            }
            if (scheme is "file" or "git" or "http" or "ssh") return false; // file:/git:///http://(明文)/scp 形态
            return false;
        }

        // git+https://host/… 或 https://host/…（https 明确允许；git+ 前缀剥掉）
        string u = s.StartsWith("git+", StringComparison.OrdinalIgnoreCase) ? s.Substring(4) : s;
        if (!u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var uri = new Uri(u);
            if (uri.Scheme != Uri.UriSchemeHttps) return false;
            if (!AllowedGitHosts.Contains(uri.Host.ToLowerInvariant())) return false;
        }
        catch { return false; }
        return true;
    }

    /// <summary><c>owner/repo</c> 段白名单（可带 <c>#ref</c>）：两段非空、字符闭合、ref 字符闭合。</summary>
    private static bool IsValidGitOwnerRepo(string ownerRepo)
    {
        int hash = ownerRepo.IndexOf('#');
        string repoPart = hash >= 0 ? ownerRepo.Substring(0, hash) : ownerRepo;
        string? refPart = hash >= 0 ? ownerRepo.Substring(hash + 1) : null;

        var segs = repoPart.Split('/');
        if (segs.Length != 2 || segs[0].Length == 0 || segs[1].Length == 0) return false;
        foreach (string seg in segs)
            foreach (char c in seg)
                if (!(char.IsLetterOrDigit(c) && c <= 0x7F) && c != '-' && c != '_' && c != '.') return false;

        if (refPart == null) return true;
        if (refPart.Length == 0 || refPart.Length > 64) return false;       // 40 位 sha 也够
        foreach (char c in refPart)
            if (!(char.IsLetterOrDigit(c) && c <= 0x7F) && c != '.' && c != '-' && c != '_' && c != '/') return false;
        return true;
    }

    // ══════════ git 源插件的安装前提：这次要不要调起系统的 git ══════════
    //
    // 背景（本机实测）：npm / pnpm 装 git 源（`git+https://…`、`github:o/r`…）时必须调起系统的 git，
    //   本机 PATH 里没有 git 时它们只抛一句英文 —— 本机把 git 从 PATH 摘掉后实测：
    //     npm view git+https://github.com/octocat/Hello-World.git version
    //     ⇒ npm error code ENOENT / npm error syscall spawn git
    //   用户看不出缺什么、也不知道下一步做什么，故界面上必须换成中文并点名 Git。
    // 判定分两层，判据各只有一份：
    //   ① 这条来源要不要 git -> 本区的 NeedsGitFor / AnyNeedsGit（纯函数，便于自检直接断言）；
    //   ② 本机 PATH 里有没有 git -> MainWindow.GitOnPath（只扫 PATH，且探测出异常一律按"有"处理）。
    // 本区只回答"要不要"，不查盘、不查 PATH、不产生任何界面文案。

    /// <summary>
    /// 这条来源声明在安装 / 更新时会不会调起系统的 git（纯函数，不查盘、不查 PATH）。
    /// 判据刻意只有一个：它是不是安装侧白名单认下的 git 来源（<see cref="IsValidGitSource"/>）
    /// —— 不另写第二套 scheme / 站点判断，白名单将来增删形态这里自动跟上。
    ///
    /// 为什么可以拿"清单里的来源声明"当判据（而不是看命令里有没有仓库地址）：
    ///   本壳对 git 源的更新命令是 <c>update 包名</c>（见 <see cref="BuildUpdateArgs"/>），命令里确实
    ///   一个仓库地址都没有，但 pnpm 是**按清单里那条声明**重新解析的 —— 声明是 <c>git+…</c>，它就得起 git。
    ///   故"这条声明是不是 git 源"与"这次要不要 git"是同一件事。
    ///
    /// 白名单外的 git 形态（<c>git://…</c>、scp 形态…）一律 false：它们本来就被安装侧拒绝、
    ///   调用方拿到的是空串、根本不会执行命令 —— 即这里 false 不会放跑任何一条真会跑的命令。
    /// </summary>
    public static bool NeedsGitFor(string? spec) => IsValidGitSource(spec);

    /// <summary>
    /// 上面那条的合集版（纯函数）：这些声明里只要有一条是 git 源即 true。
    /// 用途：按清单整份安装（<c>plugin --profile web install</c>）的那两条自愈路径没有单一来源，
    ///   判据只能落在"清单里这几条声明"上；传 null / 空表一律 false（拿不准就不拦）。
    /// </summary>
    public static bool AnyNeedsGit(IEnumerable<string?>? specs)
        => specs != null && specs.Any(s => NeedsGitFor(s));

    /// <summary>
    /// 这个字符串是不是"界面上给人看的标签"而不是版本号 / 来源 spec（纯函数）。
    ///   · 含中文（「仓库最新」「最新」「有新版（2026-09-16）」…）-> 是标签；
    ///   · 就是 <c>latest</c> / <c>*</c> / <c>newest</c> 这类词 -> 是标签；
    ///   · 含空白的整句也当标签。
    /// 用途：<see cref="BuildUpdateArgs"/> 在 npm 分支上把这类值挡在命令行之外
    /// （现场原文就是 <c>dsh-codearts-auth@仓库最新</c>）。
    /// </summary>
    public static bool IsDisplayLabel(string? s)
    {
        string t = (s ?? "").Trim();
        if (t.Length == 0) return false;
        foreach (char c in t)
            if (c > 0x7F) return true;                                   // 任何非 ASCII（含中文全角括号）
        if (t.IndexOf(' ') >= 0) return true;                            // 带空白的整句
        return t.Equals("latest", StringComparison.OrdinalIgnoreCase)
            || t.Equals("newest", StringComparison.OrdinalIgnoreCase)
            || t.Equals("*", StringComparison.Ordinal);
    }

    // ══════════ git 源插件：「有没有新版」只认远端提交变没变 ══════════
    //
    // npm 源那套"比版本号大小"对 git 源没有意义：git 源的包在镜像源上根本不存在
    // （现场：check-plugin-updates.ps1 对它们必然报"镜像源里没有这个包"），
    // 而它们的真正版本凭据只有一个 —— pnpm-lock.yaml 里记着的那个 commit。
    // 所以这里把判据收敛成三个纯函数：远端提交、锁文件里的提交、以及这次到底查没查到
    // （<see cref="GitRemoteVerdict"/>）。少了第三项就会把"查不到"当成"有新版本"。

    /// <summary>
    /// 两端提交是不是同一个（纯函数）：只比前 7 位、忽略大小写，任一侧为空 -> <c>false</c>。
    ///
    /// 注意：这个 <c>false</c> 只表示"判不出相同"，不再等于"有新提交" —— 判有没有新版一律走
    /// <see cref="GitRemoteVerdict"/>（它会把"远端查不到"与"远端确实变了"分开）。
    /// 旧调用点直接拿这个 <c>false</c> 当"有更新"，正是现场"永远提示更新"的来源。
    ///
    /// 实现已下移到 <see cref="PluginSource.SameSha"/>（那里才是判据的唯一一份）；
    /// 本方法保留签名，老调用点与自检样本零改动。
    /// </summary>
    public static bool SameCommit(string? lockedCommit, string? remoteCommit)
        => PluginSource.SameSha(lockedCommit, remoteCommit);

    /// <summary>git 源插件「有没有新版」的三态（<see cref="GitUpdateVerdict"/> 的结果）。</summary>
    public enum GitUpdateVerdict
    {
        /// <summary>没查到远端提交 -> 未知，界面如实写"待确认"，绝不显示成"有新版"。</summary>
        Unknown,
        /// <summary>远端提交与锁文件里的同一个 -> 没有新版。</summary>
        UpToDate,
        /// <summary>远端提交与锁文件里的不一样 -> 确实有新提交。</summary>
        HasNewCommit,
        /// <summary>
        /// 确实有新提交，但该由用户决定（2026-09-18 用户要求）：
        /// 用户跟的是具名分支，或他钉的标签被移动了 —— 这两种情况都提示出来，
        /// 但不计入「一键更新 N 个」的计数（判据不在这里，见下方口径说明），
        /// 免得把用户有意钉住的 ref 一并升掉。默认分支不在此列（用户：「master 肯定要报」）。
        /// </summary>
        HasNewCommitAdvisory
    }

    // ── 本枚举上没有任何「硬更新 / 算不算硬更新」方法（2026-09-18 连删两个）──
    //
    // 这里曾有过两个同族判据，都已删除（2026-09-18），本注释刻意不写出那两个方法名，
    // 于是「该口径已不存在」是一条可以直接 grep 的硬断言：
    //   · 第一个（= HasNewCommit or HasNewCommitAdvisory，含「可选升级」）先删；
    //   · 第二个（= 只认 HasNewCommit）零调用点，却正是那个旧口径的隐患：留着，将来就有人
    //     拿它去判「有没有新版」，于是「可选升级」又被计入一键更新 —— 那正是用户明确不要的。
    //   删掉比标 [Obsolete] 干净（两处都没有调用点要迁移，可编译性不受影响）。
    //
    // 注意：口径就此固定成两处，不要再在这里加第三个判据：
    //   · 界面显示含"可选升级" —— MainWindow.Tools.cs 里内联判 decision is HardNewCommit or AdvisoryNewCommit；
    //   · 计数 / 一键更新 / 只看有新版 —— 一律 MainWindow.IsHardUpdatable（HasUpdate && !Advisory）。
    //     （插件页「更新」徽标计数、批量一键更新、自检断言都钉在它上面。）

    /// <summary>
    /// git 源插件「有没有新版」的唯一判据（纯函数）：必须要 <see cref="PluginSource.CommitConfidence"/>
    /// 一起判断，否则会把「查不到」说成「有新版」。
    ///
    /// 为什么要有它（现场：gitee 源插件永远提示更新）：
    ///   旧口径直接 <c>!SameCommit(commit, remote)</c>，而 <see cref="SameCommit"/> 对任一侧为空都返回
    ///   false -> 网络断了、站点不支持、仓库私有，全部落到"有新版"那一支 -> 用户点了更新也不会消失。
    ///   现在分两条：远端确实变了才报有更新；远端查不到只报"待确认"。
    ///
    /// 为什么 <c>Queried</c> 且拿到的是空串也归 Unknown：那种响应拿不出提交号，同样不能证明"有新版"；
    /// 反过来也不是"已是最新"（同样证明不了）。这正是原设计者用「失败关闭」想表达的意思 ——
    /// 只不过它把"说不清"错说成了"有更新"，这里改成如实说"待确认"。
    ///
    /// 注意：这个三参重载 = 2026-09-18 之前的旧口径，一字未改（默认分支语义）；
    ///   按 ref 类型分流要走下面那个五参版本（<see cref="PluginSource.RefKind"/> +
    ///   <see cref="PluginSource.RefMatchKind"/>）。保留它是因为老调用点与自检样本都钉在这儿。
    /// </summary>
    internal static GitUpdateVerdict GitRemoteVerdict(string? lockedCommit, string? remoteCommit,
                                                   PluginSource.CommitConfidence confidence)
        => GitRemoteVerdict(lockedCommit, remoteCommit, confidence,
                            PluginSource.RefKind.DefaultBranch, PluginSource.RefMatchKind.Unknown);

    /// <summary>
    /// git 源插件「有没有新版」的唯一判据（纯函数，按 ref 类型分流）。
    ///
    /// 为什么要有 ref 分流（用户两轮原话 2026-09-18）：
    ///   「master 肯定要报」「如果是分支版本的有另一种提示方法，可选升级」
    ///   「gitee 是个共同 contribute 的 git 平台，所以应该读的是参与者们的提交吧？」
    /// -> 本壳比的是仓库默认分支（或用户钉的那个 ref）的最新提交，与仓库的发行版/标签无关；
    ///   二者本来就会不一致（作者持续在 master 提交却不打新标签）—— 这不是 bug，但必须写明白。
    ///
    /// 分流表：
    ///   · <see cref="PluginSource.RefKind.DefaultBranch"/> -> 常态判"有新提交"（HARD，计入一键更新）；
    ///   · <see cref="PluginSource.RefKind.Commit"/>        -> 钉死在提交，提交不可变 -> 永不报；
    ///   · <see cref="PluginSource.RefKind.NamedRef"/> + Branch -> 可选升级（ADVISORY，不计入计数）；
    ///   · <see cref="PluginSource.RefKind.NamedRef"/> + Tag    -> 标签不可变 -> 默认不报；
    ///                                                            标签被移动（罕见）-> 可选升级；
    ///   · <see cref="PluginSource.RefKind.NamedRef"/> + Unknown/Missing -> 失败关闭：不报（说不清就不说）；
    ///   · <see cref="PluginSource.RefKind.Invalid"/>       -> ref 写法不合规范 -> 不判不报。
    ///
    /// 「失败关闭」在这里的含义是宁可不报，不可乱报：远端问不出来时，即使用户钉的分支真变了，
    /// 也只说"待确认"——因为此刻我们证明不了它变了，而报错的代价是诱导用户做一次不该做的升级。
    /// </summary>
    internal static GitUpdateVerdict GitRemoteVerdict(string? lockedCommit, string? remoteCommit,
                                                   PluginSource.CommitConfidence confidence,
                                                   PluginSource.RefKind refKind,
                                                   PluginSource.RefMatchKind match)
        => PluginSource.DecideUpdate(lockedCommit, remoteCommit, confidence, refKind, match) switch
        {
            PluginSource.UpdateDecision.HardNewCommit => GitUpdateVerdict.HasNewCommit,
            PluginSource.UpdateDecision.AdvisoryNewCommit => GitUpdateVerdict.HasNewCommitAdvisory,
            PluginSource.UpdateDecision.UpToDate => GitUpdateVerdict.UpToDate,
            // Unknown -> 未知。绝不落到"有新版"那一支（失败关闭）。
            _ => GitUpdateVerdict.Unknown
        };

    /// <summary>
    /// 「可选升级」那行的卡片文案（纯函数）：一律含「可选」二字（用户要求），并说清"跟的是谁"。
    /// 分支与"标签被移动"措辞不同 —— 前者是常态（分支本来就会往前走），后者是罕见异常，必须让用户看得出区别。
    ///
    /// 注意：可见性是 <c>internal</c>（不是 <c>public</c>）：参数是 <see cref="PluginSource"/> 的嵌套枚举，
    ///   而 <c>PluginSource</c> 是 <c>internal static class</c> -> 其成员有效可访问性是 internal。
    ///   public 方法不许接受更低的参数类型（CS0051），故与 <see cref="GitRemoteVerdict"/> /
    ///   <see cref="GitStatusUnknownText"/> 保持同一口径。实现见 <see cref="PluginSource.AdvisoryText"/>。
    /// </summary>
    internal static string GitAdvisoryText(PluginSource.RefMatchKind match, string? refName, string? shortSha)
        => PluginSource.AdvisoryText(match, refName, shortSha);

    /// <summary>
    /// 「远端已无这个分支或标签」/「无法确认是分支还是标签」这类说不清的情形，卡片上的中性文案
    /// （纯函数；能说清时返回空串 -> 老路径零改动）。与 <see cref="GitStatusUnknownText"/> 同一口径。
    /// 可见性同样是 <c>internal</c>（理由见 <see cref="GitAdvisoryText"/>）；实现见 <see cref="PluginSource.RefUnknownText"/>。
    /// </summary>
    internal static string GitRefUnknownText(PluginSource.RefKind refKind, PluginSource.RefMatchKind match)
        => PluginSource.RefUnknownText(refKind, match);

    /// <summary>
    /// 「远端查不到」时卡片状态行上的中性如实文案（纯函数；查到了返回空串 -> 老路径零改动）。
    /// 措辞只陈述"没取到"这个事实，既不说是新版、也不说是最新。
    /// </summary>
    internal static string GitStatusUnknownText(PluginSource.CommitConfidence confidence) =>
        confidence == PluginSource.CommitConfidence.Queried ? "" : "更新状态待确认（未取到仓库最新提交）";

    /// <summary>
    /// git 源插件的「界面上那个版本位」该写什么（纯函数）：已装版本号优先，
    /// 无法读取时才退回短提交号，两者均无则如实写"版本未知"。
    ///
    /// 为什么要有这一条：以前这个位置会被塞进「仓库最新」这种显示标签，
    /// 看着像版本号、又被当成版本号拼进命令（现场那条 <c>dsh-codearts-auth@仓库最新</c>）。
    /// 版本位只放真实的版本号或提交号，标签一律不进这一位。
    /// </summary>
    public static string GitVersionLabel(string? installedVersion, string? commit)
    {
        string v = (installedVersion ?? "").Trim();
        if (v.Length > 0 && !v.Equals("?", StringComparison.Ordinal) && !IsVersionPlaceholderText(v)) return v;
        string c = (commit ?? "").Trim();
        if (c.Length >= 7) return c.Substring(0, 7);
        return c.Length > 0 ? c : "版本未知";
    }

    /// <summary>版本位上的占位文本（数据层写进去的 <c>(未安装)</c>，全/半角括号都认）。</summary>
    private static bool IsVersionPlaceholderText(string v)
        => v.Replace('（', '(').Replace('）', ')').Equals("(未安装)", StringComparison.Ordinal);

    /// <summary>
    /// 「更新到 …」按钮上的目标文案（纯函数）：git 源不许出现版本号位置上的假标签，
    /// 一律写「更新到最新提交」；npm 源照旧写具体版本号。
    ///
    /// <paramref name="advisory"/> 为真（可选升级：具名分支 / 标签被移动）时写「更新到该 ref 最新提交」——
    /// 用户要求这种情况入口要在（点了能升），但文案要说清它跟的是哪个 ref、由用户自己决定。
    /// 该参数有默认值 -> 老调用点与自检样本零改动。
    /// </summary>
    public static string UpdateButtonText(string? spec, string? latestLabel, bool advisory = false)
    {
        var kind = PluginSource.Classify(spec);
        if (kind == PluginSource.Kind.Registry || kind == PluginSource.Kind.Unknown)
            return "更新到 " + (latestLabel ?? "").Trim();
        return advisory ? "更新到该分支/标签最新提交" : "更新到最新提交";
    }

    /// <summary>
    /// 版本位上的这个值能不能当"版本号"看（纯函数）：空串与 <c>?</c> 都不算
    /// （<c>?</c> = package.json 在、只是未填写 version 字段 —— 插件确实装着，只是版本未知）。
    /// </summary>
    public static bool IsVersionComparable(string? version)
    {
        string v = (version ?? "").Trim();
        return v.Length > 0 && !v.Equals("?", StringComparison.Ordinal) && !IsVersionPlaceholderText(v);
    }

    /// <summary>
    /// 磁盘上这个包解析到的 git 提交（读取 <c>pnpm-lock.yaml</c>；npm 包 / 无法读取 -> 空串）。
    /// 供 git 源插件的版本位显示"当前装的是哪个提交"，以及"远端提交变没变"的比对。
    /// </summary>
    public static string ReadInstalledCommit(string packageName, string? profileDir = null)
    {
        try
        {
            string root = string.IsNullOrWhiteSpace(profileDir) ? ProfileDir : profileDir!.Trim();
            string p = Path.Combine(root, "pnpm-lock.yaml");
            if (!File.Exists(p)) return "";
            return PluginSource.LockedCommit(File.ReadAllText(p), packageName);
        }
        catch (Exception ex) { Logger.LogError("PluginManager.ReadInstalledCommit", ex); return ""; }
    }

    /// <summary>兼容旧调用：返回完整命令行。</summary>
    public static string BuildUninstallCommand(string packageName) => "npx " + BuildUninstallArgs(packageName);

    // ══════════ 半截安装的三态判定与残留清理（现场：node_modules\@furongjun1999\dsh-memory
    //             目录在、package.json 缺 -> pnpm 报「目录已存在」拒绝安装 -> 死循环） ══════════

    /// <summary>插件包目录的三态判定结论。</summary>
    public enum InstallStateKind
    {
        /// <summary>目录不在 -> 未安装（正常，直接装即可）。</summary>
        NotInstalled,
        /// <summary>目录与 package.json 都在 -> 已安装。</summary>
        Installed,
        /// <summary>目录在、package.json 缺 -> 半截安装（安装中断残留，必须先清目录才能重装）。</summary>
        Broken
    }

    /// <summary>
    /// 三态判定（纯函数，便于自检；只读盘，不写盘）：
    ///   · 目录不在 -> <see cref="InstallStateKind.NotInstalled"/>；
    ///   · 目录在、package.json 在 -> <see cref="InstallStateKind.Installed"/>；
    ///   · 目录在、package.json 缺 -> <see cref="InstallStateKind.Broken"/>（半截安装，重装必被 pnpm 拒）。
    /// 包名/目录空白 -> NotInstalled（按"没有可用的安装"处理；不抛）。
    /// </summary>
    public static InstallStateKind EvaluateInstallState(string? packageName, string? profileDir = null)
    {
        try
        {
            string n = (packageName ?? "").Trim();
            string root = string.IsNullOrWhiteSpace(profileDir) ? ProfileDir : profileDir!.Trim();
            if (n.Length == 0 || root.Length == 0) return InstallStateKind.NotInstalled;

            string dir = Path.Combine(root, "node_modules", n.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(dir)) return InstallStateKind.NotInstalled;
            return File.Exists(Path.Combine(dir, "package.json"))
                ? InstallStateKind.Installed
                : InstallStateKind.Broken;
        }
        catch (Exception ex)
        {
            Logger.LogError("PluginManager.EvaluateInstallState", ex);
            return InstallStateKind.NotInstalled;
        }
    }

    /// <summary>残留清理的结论。</summary>
    public readonly struct BrokenInstallCleanup
    {
        /// <summary>有没有真去清理（目录在且确实损坏才为 true）。</summary>
        public bool Attempted { get; }
        /// <summary>清理是否成功（目录确实没了）。</summary>
        public bool Cleared { get; }
        /// <summary>越界拒绝（路径没落在 profile 的 node_modules 之下）-> Attempted=false、Reason 说明。</summary>
        public bool Rejected { get; }
        /// <summary>一句话说明（进日志 / 提示 / 自检 detail）。</summary>
        public string Reason { get; }

        public BrokenInstallCleanup(bool attempted, bool cleared, bool rejected, string reason)
        {
            Attempted = attempted; Cleared = cleared; Rejected = rejected; Reason = reason ?? "";
        }
    }

    /// <summary>
    /// 清理半截安装的残留目录（安装 / 更新 / 回滚重装开始前调用；批量路径复用同一入口）。
    ///
    /// 安全边界：
    ///   · 包名必须过 <see cref="IsValidPackageName"/> 白名单（杜绝 <c>..</c> / 绝对路径 / 元字符）；
    ///   · 最终路径必须落在 profile 的 <c>node_modules\</c> 之下 —— 用 GetFullPath 归一后按
    ///     前缀比对（Path.Combine 遇到 rooted path 会丢弃前缀，只靠 Combine 拼接挡不住穿越）；
    ///     越界一律拒绝、绝不动手；
    ///   · 只删 <c>node_modules\&lt;包名&gt;</c> 一层，带重试（pnpm/引擎可能短暂占用文件）；
    ///   · 非 Broken 态什么都不做（已安装/未安装都不清 —— 那是用户数据或本就无事）。
    /// 删除失败（引擎占用等）如实返回 Cleared=false，由调用方建议先停引擎。
    /// </summary>
    public static BrokenInstallCleanup CleanBrokenInstall(string packageName, string? profileDir = null)
    {
        try
        {
            string n = (packageName ?? "").Trim();
            string root = string.IsNullOrWhiteSpace(profileDir) ? ProfileDir : profileDir!.Trim();
            if (n.Length == 0 || root.Length == 0)
                return new BrokenInstallCleanup(false, false, true, "包名或 profile 目录未知，拒绝清理");

            // ① 包名白名单：`..`、绝对路径、元字符、非法形态一律拒绝
            if (!IsValidPackageName(n))
                return new BrokenInstallCleanup(false, false, true, $"包名「{n}」未通过白名单，拒绝清理（疑似路径穿越或注入）");

            // ② 最终路径必须真的落在 node_modules 之下（rooted-path 陷阱：Path.Combine 遇绝对路径会丢前缀）
            string nodeModules = Path.GetFullPath(Path.Combine(root, "node_modules"));
            string target = Path.Combine(nodeModules, n.Replace('/', Path.DirectorySeparatorChar));
            string fullTarget;
            try { fullTarget = Path.GetFullPath(target); }
            catch (Exception ex)
            {
                Logger.LogError("PluginManager.CleanBrokenInstall(GetFullPath)", ex);
                return new BrokenInstallCleanup(false, false, true, "清理路径无法归一化，拒绝清理");
            }
            if (!fullTarget.StartsWith(nodeModules + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || fullTarget.Length <= nodeModules.Length)
                return new BrokenInstallCleanup(false, false, true, $"清理路径「{fullTarget}」越出 node_modules 之外，拒绝清理");

            // ③ 只有"目录在、package.json 缺"的半截态才动手（已安装 / 未安装都不清）
            if (!Directory.Exists(fullTarget))
                return new BrokenInstallCleanup(false, true, false, "目录不在，无需清理");
            if (File.Exists(Path.Combine(fullTarget, "package.json")))
                return new BrokenInstallCleanup(false, false, false, "目录完好（package.json 在），不是半截安装，不清");

            // ④ 清理：带重试（引擎/pnpm 短暂占用文件是常态），失败如实报告
            Exception? lastErr = null;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try { Directory.Delete(fullTarget, recursive: true); }
                catch (Exception ex) { lastErr = ex; }
                if (!Directory.Exists(fullTarget))
                {
                    Logger.NoteDiagnosis($"已清理半截安装残留：{fullTarget}（目录在、package.json 缺，重装前先删除）");
                    return new BrokenInstallCleanup(true, true, false, "残留目录已清理");
                }
                Thread.Sleep(400 * attempt);     // 退避重试
            }
            string err = lastErr == null ? "未知原因" : lastErr.Message;
            Logger.NoteDiagnosis($"清理半截安装残留失败：{fullTarget}（{err}）——多为引擎或其它进程占用，建议先停止引擎后重试");
            return new BrokenInstallCleanup(true, false, false, $"残留目录清理失败（{err}），建议先停止引擎后重试");
        }
        catch (Exception ex)
        {
            Logger.LogError("PluginManager.CleanBrokenInstall", ex);
            return new BrokenInstallCleanup(false, false, false, "清理过程出错：" + ex.Message);
        }
    }

    // ── 插件更新检查（tools\check-plugin-updates.ps1 的结果） ──

    /// <summary>单条插件更新查询结果。</summary>
    public sealed class PluginUpdate
    {
        public string Name { get; set; } = "";
        public string Installed { get; set; } = "";
        public string Latest { get; set; } = "";
        /// <summary>
        /// 界面上"更新到哪"这一位该写什么（按钮 / 确认框 / 汇总行共用，避免三处各写各的）。
        /// git 源写「最新提交」这类不含假版本号的写法；npm 源就是 <see cref="Latest"/>（具体版本号）。
        /// 留空 -> 回落到 <see cref="Latest"/>（老调用点/自检样本不用改）。
        /// </summary>
        public string TargetLabel { get; set; } = "";
        /// <summary>界面上真正显示的目标文案（<see cref="TargetLabel"/> 优先）。</summary>
        public string TargetText => TargetLabel.Length > 0 ? TargetLabel : Latest;
        /// <summary>
        /// 卡片上「有新版 / 已是最新」那一行的中性补充：只有"远端查不到、说不清有没有新版"时才非空
        /// （<see cref="GitStatusUnknownText"/>）。留空 -> 显示逻辑一个字节都不变（老调用点/自检样本零改动）。
        /// </summary>
        public string StatusNote { get; set; } = "";
        public bool HasUpdate { get; set; }

        /// <summary>
        /// 这条"有新版"是可选升级（具名分支 / 标签被移动）而不是该自动跟的默认分支（2026-09-18 用户要求）。
        ///
        /// 它只用来分开口径，不改变 <see cref="HasUpdate"/>：
        ///   · 显示：卡片照旧写"有新版"，但文案含「可选」二字，并说清跟的是哪个 ref；
        ///   · 计数：<c>UpdatableCount</c>（「一键更新 N 个」与「只看有新版」的唯一口径）只数非 Advisory 的，
        ///     免得把用户有意钉住的分支/标签一并升掉；
        ///   · 入口：卡片上仍有「更新到最新提交」按钮，用户点了就能升。
        /// 留空（默认 false）-> 老调用点与自检样本零改动。
        /// </summary>
        public bool Advisory { get; set; }

        /// <summary>
        /// 「可选升级」的卡片行正文（<see cref="PluginSource.AdvisoryText"/>，一律含「可选」二字）：
        /// 例 <c>可选升级：远端分支 dev 有新提交 9669ee4</c>。仅 <see cref="Advisory"/> 为真时非空。
        /// 与 <see cref="TargetLabel"/> 分开：后者要能顺地读进「更新到 X？」这句话里，本属性是整句话的标题。
        /// </summary>
        public string AdvisoryNote { get; set; } = "";

        /// <summary>
        /// 「比的是谁、与发行版不同」的完整说明（<see cref="PluginSource.CompareBasisNote"/>），
        /// 卡片上有新版时作悬停提示用。留空 -> 显示逻辑一个字节都不变（老调用点/自检样本零改动）。
        /// </summary>
        public string CompareNote { get; set; } = "";

        /// <summary>
        /// 远端最新提交的「短号 · 日期 · 作者」（<see cref="PluginSource.NewCommitNote"/>）。
        /// 摆出作者是为了证明读的是参与者们的提交、不按作者过滤。
        /// 留空 -> 老调用点/自检样本零改动。
        /// </summary>
        public string CommitNote { get; set; } = "";

        /// <summary>
        /// 「待确认」时这一次到底为什么（分类；说得清时为 <see cref="PluginSource.RefProbeFailure.None"/>）。
        /// 文案唯一实现处：<see cref="PluginSource.RefProbeFailureHint"/>（显示层只照抄，不许另写一套）。
        /// 默认 <see cref="PluginSource.RefProbeFailure.None"/> -> 老赋值点零改动。
        ///
        /// 注意：必须 <c>internal</c>（理由与 <see cref="GitRefUnknownText"/> 逐字同类）：本类是 <c>public</c>，
        ///   而 <c>PluginSource</c> 是 <c>internal static class</c> -> 其嵌套枚举
        ///   <see cref="PluginSource.RefProbeFailure"/> 的有效可访问性只在程序集内。写成 <c>public</c>
        ///   会让"可访问性低于成员"（CS0053）—— 与 <see cref="GitRefUnknownText"/> 保持同一口径。
        ///
        /// 注意：它只是诊断位、只喂悬停文案：不参与 <see cref="HasUpdate"/> / <see cref="Advisory"/> 等任何判定
        ///   （"有没有新版"仍只有 <c>PluginSource.DecideUpdate</c> 一处说了算；失败关闭不因它而变：
        ///   任何类别都不会报"有更新"）。
        /// </summary>
        internal PluginSource.RefProbeFailure FailureHint { get; set; } = PluginSource.RefProbeFailure.None;

        /// <summary>
        /// 可点开的提交链接（<see cref="PluginSource.CommitUrl"/>）；站点已受托管站白名单约束，
        /// 打开前仍会过 <c>PluginMarket.IsAllowedLinkUrl</c> 闸门。留空 -> 不放链接。
        /// </summary>
        public string CommitUrl { get; set; } = "";

        public string Published { get; set; } = "";
        /// <summary>新版本声明的 dsh 版本要求（没有则空）。</summary>
        public string NewRequirement { get; set; } = "";
        /// <summary>
        /// <see cref="NewRequirement"/> 的声明位置（<c>dsh.compatibility</c> / <c>dsh.engines</c> /
        /// <c>engines</c> / <c>peerDependencies</c>，无声明时空串），与它同源同判、必须成对使用。
        /// </summary>
        public string NewRequirementSource { get; set; } = "";
        public string Error { get; set; } = "";

        /// <summary>
        /// 查询失败时的**界面**文案：一律只给中性结论，绝不把 <see cref="Error"/> 原文摆上屏
        /// （那是小工具里 <c>error</c> 字段的原样摘录，多为英文原始异常文本）。
        /// 404 通常表示私有或本地插件，故单给一句更具体的结论；其余情况只说"取不到"。
        /// 原文不丢：它在 <see cref="ParseUpdateReport"/> 里已随 <c>Logger.NoteDiagnosis</c> 落盘
        /// （<c>Logger.Log</c> 是空实现、不落盘），排查时去异常日志看，界面不出技术细节。
        /// 本属性只喂显示层，不参与任何判定。
        /// </summary>
        public string ErrorText => Error.Length == 0 ? ""
            : Error.Contains("404") ? "下载来源里没有这个包（可能是私有/本地插件）"
            : "查询失败：暂时无法从下载来源获取";
    }

    /// <summary>
    /// 解析 check-plugin-updates.ps1 输出的 JSON；容错处理：前后的无关输出与损坏条目均跳过。
    /// <c>hasUpdate</c> 由 C# 侧以 VersionInfo.Compare 重算，脚本内的简易比较仅供命令行单独使用。
    ///
    /// 注意：「新版本要求」的判据不取自脚本的结论：脚本只提供该版本声明的原文摘录
    /// （<c>newDeclarationJson</c>），这里反序列化成 package.json 根对象后转调
    /// <see cref="VersionInfo.ExtractDshRequirement"/> —— 字段优先级（① <c>dsh.compatibility.dsh</c>
    /// -> ② <c>dsh.engines.dsh</c> -> ③ 顶层 <c>engines.dsh</c> -> ④ <c>peerDependencies</c> 取最高候选）
    /// 全局只有那一份。脚本过去自己写过一份（dsh.engines -> peerDependencies）并直接输出
    /// <c>newDshRequirement</c>/<c>newRequirementSource</c>，缺 ①③ -> 同一份声明在更新卡、本地列表、
    /// 插件市场三处可能得出不同结论。这两个字段从此只给命令行肉眼参考，界面不再读它们。
    /// </summary>
    public static List<PluginUpdate> ParseUpdateReport(string json)
    {
        var list = new List<PluginUpdate>();
        try
        {
            if (string.IsNullOrWhiteSpace(json)) return list;
            int start = json.IndexOf('{');
            int end = json.LastIndexOf('}');
            if (start < 0 || end <= start) return list;

            using var doc = JsonDocument.Parse(json.Substring(start, end - start + 1));
            if (!doc.RootElement.TryGetProperty("packages", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return list;

            foreach (var e in arr.EnumerateArray())
            {
                try
                {
                    var u = new PluginUpdate
                    {
                        Name = Str(e, "name"),
                        Installed = Str(e, "installed"),
                        Latest = Str(e, "latest"),
                        Published = Str(e, "published"),
                        Error = Str(e, "error"),
                    };
                    if (u.Name.Length == 0) continue;

                    // 界面只留中性结论（<see cref="PluginUpdate.ErrorText"/>）：这条 error 是小工具
                    //   <c>error</c> 字段的原样摘录（多为英文原始异常文本），技术细节一律不上屏
                    //   —— 但也不能丢，故在解析处随异常日志落盘留证（[WARN] 单行、不弹窗、不当异常）。
                    //   `Logger.Log` 与 `LogDiagnosis` 都是空实现，真落盘的入口只有 `NoteDiagnosis`。
                    //   逐条记、只记一次：本方法是每份报告唯一的解析入口，且**不重复**既有的那两条
                    //   `Logger.Log`（MainWindow.Tools.cs:121 只在整份报告一条都没解析出来时记 ok/out 摘要；
                    //   同文件 :245 只记条数统计）—— 两者都不含这里的单包原文，写法也不同。
                    if (u.Error.Length > 0)
                        Logger.NoteDiagnosis($"插件更新查询：「{u.Name}」这一条没查成（界面只显示中性结论，原文如下）：{u.Error}");

                    u.HasUpdate = u.Error.Length == 0 && u.Latest.Length > 0 && u.Installed.Length > 0
                                  && VersionInfo.Compare(u.Latest, u.Installed) > 0;

                    // 判据只有一份：用脚本摘录的声明原文，转调 VersionInfo.ExtractDshRequirement。
                    //   requirement 与 source 必须同源同判 —— 悬停文案把两者并列显示，
                    //   来源取自另一套顺序（脚本旧口径）会让「要求」与「来源」对不上。
                    //   缺 newDeclarationJson（老版脚本 / 手工构造的样本）-> 保持 ("","")=未声明（失败关闭）。
                    var (req, src) = ExtractDeclarationRequirement(Str(e, "newDeclarationJson"));
                    u.NewRequirement = req;
                    u.NewRequirementSource = src;

                    list.Add(u);
                }
                catch { }
            }
        }
        catch (Exception ex) { Logger.LogError("PluginManager.ParseUpdateReport", ex); }
        return list;
    }

    /// <summary>
    /// 把脚本摘录的声明原文（<c>newDeclarationJson</c>）反序列化成 package.json 根对象，
    /// 再转调 <see cref="VersionInfo.ExtractDshRequirement"/>。
    /// 纯解析、不联网、不抛：不是对象 / 解析失败 / 缺失一律降级为 <c>("", "")</c>（= 未声明）。
    /// </summary>
    private static (string Requirement, string Source) ExtractDeclarationRequirement(string declarationJson)
    {
        if (string.IsNullOrWhiteSpace(declarationJson)) return ("", "");
        try
        {
            using var d = JsonDocument.Parse(declarationJson);
            return d.RootElement.ValueKind == JsonValueKind.Object
                ? VersionInfo.ExtractDshRequirement(d.RootElement)
                : ("", "");
        }
        catch (Exception ex)
        {
            // 外部输入（镜像站数据经脚本转手），坏 JSON 只影响这一条的"要求"显示，不能拖垮整轮扫描
            Logger.LogError("PluginManager.ExtractDeclarationRequirement", ex);
            return ("", "");
        }
    }

    private static string Str(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";
}
