using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DSHGuard;

/// <summary>
/// DSH 版本探测：当前运行版本（npx 缓存副本）、发布时间与最新版（registry），以及 semver 比较。
/// 全部为只读查询，失败一律降级为"未知"，不抛异常给 UI。
/// </summary>
public static class VersionInfo
{
    public sealed record Info(
        string Current, string? Published, string? Latest, bool Outdated, string Registry, string? Error,
        string? Channel = null, string? StableLatest = null, string? LatestPublished = null);

    /// <summary>
    /// 查询 registry 的发布时间与最新版本（15s 超时）。current 为空时自动探测当前版本。
    ///
    /// npm 上的预发布版本常挂在 next/alpha 等标签上：`latest=0.1.5-rc.1`、
    /// `next=0.1.5-rc.2`、`alpha=0.1.5-alpha.2`，只读 dist-tags.latest 会漏掉其他标签上的更新版本
    /// 并始终显示「已是最新」。因此遍历全部 dist-tag，按语义化版本取最大者作为最新版，
    /// 同时返回标签名供界面显示通道。
    /// </summary>
    public static async Task<Info> QueryAsync(string? registry = null, string? current = null)
    {
        registry = string.IsNullOrWhiteSpace(registry) ? Registries.Current : registry!;
        current = string.IsNullOrWhiteSpace(current) ? GetCurrentVersion() : current!.Trim();
        string url = registry.TrimEnd('/') + "/@deepseek-ai%2Fdsh";

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            string json = await http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? best = null, bestTag = null, stable = null;
            if (root.TryGetProperty("dist-tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
            {
                foreach (var tag in tags.EnumerateObject())
                {
                    string? v = tag.Value.ValueKind == JsonValueKind.String ? tag.Value.GetString() : null;
                    if (string.IsNullOrWhiteSpace(v)) continue;
                    if (tag.Name.Equals("latest", StringComparison.OrdinalIgnoreCase)) stable = v;
                    // 不可解析为纯版本号的标签值（外部输入）不参与"取最大"，防恶意值抢占 best
                    if (best == null && IsComparableVersion(v)) { best = v; bestTag = tag.Name; }
                    else if (best != null && IsComparableVersion(v) && Compare(v!, best) > 0) { best = v; bestTag = tag.Name; }
                }
            }

            string? published = null, bestPublished = null;
            if (root.TryGetProperty("time", out var time) && time.ValueKind == JsonValueKind.Object)
            {
                if (time.TryGetProperty(current, out var t)) published = TryLocal(t.GetString());
                if (best != null && time.TryGetProperty(best, out var t2)) bestPublished = TryLocal(t2.GetString());
            }

            bool outdated = best != null && Compare(current, best) < 0;
            return new Info(current, published, best, outdated, url, null, ChannelName(bestTag), stable, bestPublished);
        }
        catch (Exception ex)
        {
            return new Info(current, null, null, false, url, ex.Message);
        }
    }

    /// <summary>
    /// 从 dist-tags 里挑最新版本（纯函数，便于离线自检）：返回 (最新版本, 它的标签名, latest 标签的值)。
    /// </summary>
    public static (string? Best, string? BestTag, string? Stable) PickNewestTag(
        IEnumerable<KeyValuePair<string, string?>> tags)
    {
        string? best = null, bestTag = null, stable = null;
        foreach (var tag in tags)
        {
            string? v = tag.Value;
            if (string.IsNullOrWhiteSpace(v)) continue;
            if (tag.Key.Equals("latest", StringComparison.OrdinalIgnoreCase)) stable = v;
            // 不可解析为纯版本号的标签值（外部输入）不参与"取最大"，防恶意值抢占 best
            if (best == null && IsComparableVersion(v)) { best = v; bestTag = tag.Key; }
            else if (best != null && IsComparableVersion(v) && Compare(v!, best) > 0) { best = v; bestTag = tag.Key; }
        }
        return (best, bestTag, stable);
    }

    /// <summary>将 npm 标签转为界面显示的中文通道名。</summary>
    public static string? ChannelName(string? tag) => tag switch
    {
        null or "" => null,
        "latest" => "正式版",
        "next" => "预览版",
        "rc" => "候选版",
        "beta" => "测试版",
        "alpha" => "内测版",
        _ => tag
    };

    private static string? TryLocal(string? iso)
    {
        if (string.IsNullOrEmpty(iso)) return null;
        return DateTimeOffset.TryParse(iso, out var dt) ? dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : iso;
    }

    /// <summary>
    /// 读取当前 DSH 版本：优先返回 prefer（本次启动所用的固定版本），否则返回 npx 缓存中最近写入的一份。
    /// _npx 下的哈希目录名不固定，需要遍历；同一版本可能存在多份缓存，按 package.json 写入时间取最新。
    /// </summary>
    public static string GetCurrentVersion(string? prefer = null)
    {
        var list = ListCachedVersions();
        if (list.Count == 0) return "未知";

        if (!string.IsNullOrWhiteSpace(prefer))
        {
            var hit = list.FirstOrDefault(x => x.Version == prefer!.Trim());
            if (hit.Version != null) return hit.Version;
        }
        return list[0].Version;
    }

    /// <summary>列出 npx 缓存中出现过的 dsh 版本，按最近写入时间倒序，供「回退版本」选单使用。</summary>
    public static List<(string Version, DateTime Time)> ListCachedVersions()
    {
        var result = new List<(string Version, DateTime Time)>();
        try
        {
            string npxRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "npm-cache", "_npx");
            if (!Directory.Exists(npxRoot)) return result;

            foreach (var dir in Directory.GetDirectories(npxRoot))
            {
                string pj = Path.Combine(dir, "node_modules", "@deepseek-ai", "dsh", "package.json");
                if (!File.Exists(pj)) continue;

                string? ver = null;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(pj));
                    if (doc.RootElement.TryGetProperty("version", out var v)) ver = v.GetString();
                }
                catch { continue; }

                if (string.IsNullOrWhiteSpace(ver)) continue;
                if (result.Any(r => r.Version == ver)) continue;
                result.Add((ver!, File.GetLastWriteTime(pj)));
            }
        }
        catch { }
        return result.OrderByDescending(r => r.Time).ToList();
    }

    /// <summary>
    /// 简化 semver 比较：-1 / 0 / 1；正确处理预发布（0.1.5-rc.1 &lt; 0.1.5）。
    ///
    /// ⚠ 信任边界（本轮加固）：两侧输入都来自外部（镜像源 dist-tags、插件 package.json）。
    /// 任一侧**不能解析为纯数字点分**（如 <c>99.0.0&amp;calc</c>、<c>1.2.3 x</c>、带换行）⇒
    /// 返回 0（"不可比"，不判大小）。旧实现用 TakeWhile(Char.IsDigit) 抽数字、
    /// **静默丢弃尾部垃圾**，导致恶意 latest <c>99.0.0&amp;calc</c> 被判比 <c>0.1.5</c> 大、
    /// 闯过"有更新"判定 —— 该语义已废除。调用方（HasUpdate 类判定）因此天然失败关闭：
    /// 比不出来就不会判"有更新"。
    /// </summary>
    public static int Compare(string a, string b)
    {
        if (!TrySplit(a, out var av, out var ap) || !TrySplit(b, out var bv, out var bp))
            return 0;   // 任一侧不可解析 ⇒ 不可比，不判大小（失败关闭由调用方保证：0 不构成"更新"）

        int n = Math.Max(av.Length, bv.Length);
        for (int i = 0; i < n; i++)
        {
            int x = i < av.Length ? av[i] : 0;
            int y = i < bv.Length ? bv[i] : 0;
            if (x != y) return x < y ? -1 : 1;
        }

        if (ap == null && bp == null) return 0;
        if (ap == null) return 1;   // 无预发布后缀者版本更高
        if (bp == null) return -1;
        return Math.Sign(string.CompareOrdinal(ap, bp));
    }

    /// <summary>
    /// 这个字符串能否解析为**纯数字点分**的版本号（可带 <c>^ ~ &gt; = v</c> 前缀与
    /// <c>-预发布</c> 后缀）。核心段每段必须全为数字且非空；预发布段只允许字母数字与 <c>. -</c>；
    /// 尾部垃圾（空格、<c>&amp;</c>、换行…）一律不可解析。纯函数，供自检与调用方做"可不可比"判断。
    /// </summary>
    public static bool IsComparableVersion(string? version) => TrySplit(version, out _, out _);

    private static bool TrySplit(string? v, out int[] ver, out string? pre)
    {
        ver = Array.Empty<int>();
        pre = null;

        string s = (v ?? "").Trim().TrimStart('^', '~', '>', '=', 'v', ' ');
        int dash = s.IndexOf('-');
        string core = dash >= 0 ? s.Substring(0, dash) : s;
        string? p = dash >= 0 ? s.Substring(dash + 1) : null;

        if (core.Length == 0) return false;
        var parts = core.Split('.');
        var nums = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            string seg = parts[i];
            if (seg.Length == 0 || seg.Any(c => !char.IsDigit(c))) return false;   // 不再静默丢垃圾
            if (!int.TryParse(seg, out nums[i])) return false;                     // 溢出的超长数字同样不可解析
        }
        if (p != null)
        {
            if (p.Length == 0) p = null;
            else if (!Regex.IsMatch(p, "^[0-9A-Za-z.\\-]+$")) return false;        // 预发布段也不许夹带垃圾
        }
        ver = nums;
        pre = p;
        return true;
    }

    /// <summary>
    /// 判断当前版本是否满足给定版本要求（如 "&gt;=0.1.5-rc.1" / "^0.1.0-rc.6" / ">=4.0.1 || ^5.0.0"）。
    /// 对 ^ / ~ 采用「当前版本 ≥ 声明下限」的宽松判定，满足插件生态的实际需要并避免误报不兼容。
    ///
    /// ⚠ 信任边界（与 <see cref="Compare"/> 的语义修正配套）：要求串来自插件的 package.json（外部输入），
    /// 每个比较的版本操作数必须先过 <see cref="IsComparableVersion"/> —— 否则"不可比 ⇒ Compare=0"
    /// 会撞上 <c>&gt;= 0</c> 判成满足（失败打开）。不可解析的分段一律视为不满足，跳过。
    /// </summary>
    public static bool Satisfies(string requirement, string current)
    {
        try
        {
            requirement = (requirement ?? "").Trim();
            if (requirement.Length == 0 || current == "未知") return true;

            foreach (var segRaw in requirement.Split(new[] { "||" }, StringSplitOptions.RemoveEmptyEntries))
            {
                var seg = segRaw.Trim();
                if (seg.Length == 0) continue;

                if (seg.StartsWith(">=") || seg.StartsWith("<="))
                {
                    string v = seg.Substring(2).Trim();
                    if (!IsComparableVersion(v)) continue;
                    if (seg.StartsWith(">=") ? Compare(current, v) >= 0 : Compare(current, v) <= 0) return true;
                }
                else if (seg.StartsWith(">") || seg.StartsWith("<"))
                {
                    string v = seg.Substring(1).Trim();
                    if (!IsComparableVersion(v)) continue;
                    if (seg.StartsWith(">") ? Compare(current, v) > 0 : Compare(current, v) < 0) return true;
                }
                else if (seg.StartsWith("^") || seg.StartsWith("~"))
                {
                    string v = seg.Substring(1).Trim();
                    if (!IsComparableVersion(v)) continue;
                    if (Compare(current, v) >= 0) return true;
                }
                else if (seg == "*" || seg.Equals("latest", StringComparison.OrdinalIgnoreCase)) return true;
                else
                {
                    if (!IsComparableVersion(seg)) continue;    // 不可解析的分段（含 1.x 这类）不判满足
                    if (Compare(current, seg) >= 0) return true;
                }
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// 取版本要求中出现的全部版本号（`&gt;=0.1.5-rc.1 || ^0.1.0-rc.6` → ["0.1.5-rc.1","0.1.0-rc.6"]）。
    /// 用于判断当前版本是否为作者指向的版本，见 PluginManager 的四色兼容分档。
    /// </summary>
    public static List<string> RequirementVersions(string requirement)
    {
        var list = new List<string>();
        try
        {
            foreach (Match m in Regex.Matches(requirement ?? "", @"\d+\.\d+\.\d+(?:-[0-9A-Za-z.\-]+)?"))
                if (!list.Contains(m.Value)) list.Add(m.Value);
        }
        catch { }
        return list;
    }

    /// <summary>
    /// 从插件 <c>package.json</c> 里读出**作者声明的 DSH 兼容性要求** —— 全局**唯一**的一处判据。
    /// 本地插件列表（<see cref="PluginManager"/> 扫描已装包）与插件市场（<see cref="PluginMarket"/> 读
    /// registry 的 versions[latest]）**都必须转调这里**：两边的 JSON 结构本来就是同一个 package.json，
    /// 各写一份判据只会让同一份声明在"市场"与"本地"得出不同结论（现场：市场有、本地一律「未声明」）。
    ///
    /// 字段优先级（按作者实际使用的权威度）：
    ///   ① <c>dsh.compatibility.dsh</c> —— 插件生态真实在用的声明字段（source 记 <c>"dsh.compatibility"</c>）；
    ///   ② <c>dsh.engines.dsh</c> —— 保留向后兼容，字段仍被作者使用（source 记 <c>"dsh.engines"</c>）；
    ///   ③ <c>engines.dsh</c>（**顶层**，npm 规范位）—— 与 ①② 同属作者显式声明的"要求 DSH 什么版本"，
    ///      只是没挂在 <c>dsh</c> 命名空间下；声明位置比 <c>peerDependencies</c> 更直接，故排在它之前
    ///      （source 记 <c>"engines"</c>）。本机 16 个插件里暂无"只声明它"的样本，但生态作者若只用它，
    ///      此前会一律显示「未声明」；
    ///   ④ <c>peerDependencies</c> 里全部 <c>@deepseek-ai/dsh*</c> 中**候选版本最高**的那条声明
    ///      （source 记 <c>"peerDependencies"</c>）。
    ///
    /// ⚠ ④（<c>peerDependencies</c> 那支）用 <see cref="RequirementVersions"/> 取**最高**候选，
    /// **不是**取 range 里第一个版本号 —— 形如 <c>&gt;=0.1.0-rc.6 &lt;0.2.0 || ^0.1.5-rc.1</c> 的 range，
    /// 第一个版本号未必最高。
    ///
    /// ⚠ ④ 的初值**必须能与候选比较**：<c>"0.0.0"</c>，**不能是空串**。空串过不了 <see cref="TrySplit"/>，
    /// <see cref="Compare"/> 便恒返回 0，于是 <c>Compare(cand, bestVer) &gt; 0</c> 恒为假、循环跑完一条都选不出来，
    /// 整个 <c>peerDependencies</c> 分支**静默失效**（返回 <c>("", "")</c>）—— 这正是"本地管理的插件读不到
    /// 作者声明的版本兼容性"的根因。种子用小写 <c>0.0.0</c> 时，任何可解析候选取值都 &gt; 0，必然能入选。
    ///
    /// 纯函数：只读传入的 JSON，不联网、不抛异常；结构不符或读不到一律降级为 <c>("", "")</c>（= 未声明）。
    /// </summary>
    /// <param name="packageRoot">package.json 的根对象（本地已装包、或镜像站 packument 的 versions[latest]，两者同构）。</param>
    /// <returns>(requirement, source)；读不到时 <c>("", "")</c>。</returns>
    public static (string Requirement, string Source) ExtractDshRequirement(JsonElement packageRoot)
    {
        try
        {
            if (packageRoot.ValueKind == JsonValueKind.Object
                && packageRoot.TryGetProperty("dsh", out var dsh) && dsh.ValueKind == JsonValueKind.Object)
            {
                // ① dsh.compatibility.dsh（作者真实声明的字段，优先）
                string compat = NestedString(dsh, "compatibility", "dsh");
                if (compat.Length > 0) return (compat, "dsh.compatibility");

                // ② dsh.engines.dsh（向后兼容，勿删：仍有作者用它声明）
                string eng = NestedString(dsh, "engines", "dsh");
                if (eng.Length > 0) return (eng, "dsh.engines");
            }
        }
        catch { }

        try
        {
            // ③ engines.dsh（**顶层**，npm 规范位；与 ①② 同为作者显式声明，故压在 ④ 之前）
            //    读法沿用 ①② 同款 NestedString（层缺失 / 非对象 / 非字符串 / 空白 ⇒ 空串 ⇒ 继续往下试）
            string top = NestedString(packageRoot, "engines", "dsh");
            if (top.Length > 0) return (top, "engines");
        }
        catch { }

        try
        {
            // ④ peerDependencies 中 @deepseek-ai/dsh* 的最高候选
            if (packageRoot.ValueKind == JsonValueKind.Object
                && packageRoot.TryGetProperty("peerDependencies", out var pd) && pd.ValueKind == JsonValueKind.Object)
            {
                string best = "", bestVer = "0.0.0";   // ← 种子必须可比；用空串会让整支恒不入选（见上方说明）
                foreach (var dep in pd.EnumerateObject())
                {
                    if (!dep.Name.StartsWith("@deepseek-ai/dsh", StringComparison.OrdinalIgnoreCase)) continue;
                    // 声明值安全读取：GetString() 对非字符串（数字 / 对象）会抛，抛出去会让整轮扫描中断
                    if (dep.Value.ValueKind != JsonValueKind.String) continue;
                    string s = (dep.Value.GetString() ?? "").Trim();
                    if (s.Length == 0) continue;

                    var vs = RequirementVersions(s);
                    string cand = vs.Count > 0
                        ? vs.OrderByDescending(v => v, Comparer<string>.Create(Compare)).First()
                        : "0.0.0";
                    if (Compare(cand, bestVer) > 0) { best = s; bestVer = cand; }
                }
                if (best.Length > 0) return (best, "peerDependencies");
            }
        }
        catch { }

        return ("", "");
    }

    /// <summary>
    /// 读 <c>root.a.b</c> 形态的字符串字段（如 <c>dsh.compatibility.dsh</c>）：任一层缺失 / 非对象 /
    /// 值非字符串 / 空白 ⇒ 空串。纯读取，不抛。
    /// </summary>
    private static string NestedString(JsonElement root, string a, string b)
    {
        try
        {
            if (root.ValueKind != JsonValueKind.Object) return "";
            if (!root.TryGetProperty(a, out var mid) || mid.ValueKind != JsonValueKind.Object) return "";
            if (!mid.TryGetProperty(b, out var v) || v.ValueKind != JsonValueKind.String) return "";
            return (v.GetString() ?? "").Trim();
        }
        catch { return ""; }
    }
}
