using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DSHGuard;

/// <summary>
/// 「寻找插件」的数据层：拉取 Oh My DSH 收录的社区插件目录（与 dsh-market 同源）。
///
/// 两条路由：
///   ① 快路：registry.npmmirror.com/dsh-plugin-catalog/latest → dist.tarball → 解压出 package/plugins.json
///      （本机实测元数据 0.2s + 805KB / 0.6s）；
///   ② 备用：https://awesome-dsh-plugin.com/plugins.json（本机实测 3.1MB / 约 22s，仅在快路失败时使用）。
/// 目录结构：{name,url,source,updated,count,categories,plugins[]}；
/// categories 为 slug → {en,zh} 的标签表，直接用作分类中文名，不硬编码。
/// 条目字段：name,owner,url,page,category(可数组),description{en,zh},npm,version,stars,downloads,install,added。
///
/// 缓存：%APPDATA%\DSHGuard\catalog.json（原始 JSON）+ catalog-meta.json（版本/时间/来源）。
/// npm 路由返回的 version 即校验器：一致则跳过下载；TTL 6 小时；离线时使用旧缓存并标记 Stale。
/// </summary>
public static class PluginMarket
{
    /// <summary>npm 镜像源地址：跟随设置里的「下载来源」（Registries.Current）。</summary>
    public static string NpmRegistry => Registries.Current;
    public const string CatalogPackage = "dsh-plugin-catalog";
    public const string FallbackCatalogUrl = "https://awesome-dsh-plugin.com/plugins.json";

    private static readonly HttpClient Http = CreateClient();
    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("DSHGuard/1.0 (+plugin-market)");
        return c;
    }

    private static string CatalogFile => Path.Combine(GuardPaths.CacheDirMarket, "catalog.json");
    private static string CatalogMetaFile => Path.Combine(GuardPaths.CacheDirMarket, "catalog-meta.json");

    public enum MarketSort { Downloads, Stars, Published }

    public sealed class MarketPlugin
    {
        public string Name { get; set; } = "";
        public string Owner { get; set; } = "";
        public string RepoUrl { get; set; } = "";
        public string PageUrl { get; set; } = "";
        public string DescZh { get; set; } = "";
        public string DescEn { get; set; } = "";
        public string Npm { get; set; } = "";
        public string Version { get; set; } = "";
        public string Install { get; set; } = "";
        public string Added { get; set; } = "";
        public List<string> Categories { get; set; } = new();
        public long Stars { get; set; }
        public long? Downloads { get; set; }

        /// <summary>GitHub 截图与配图（目录中 588 条收录带图，均为 raw.githubusercontent 直链）。</summary>
        public List<string> Screenshots { get; set; } = new();

        /// <summary>目录未收录截图、需从仓库 README 抓取一次的条目（避免每次渲染重复抓取）。</summary>
        public bool ReadmeScraped { get; set; }

        // ── 懒加载回填（按需查询 registry）──
        public bool MetaLoaded { get; set; }
        public string LatestPublished { get; set; } = "";
        public string Requirement { get; set; } = "";
        public string RequirementSource { get; set; } = "";
        public PluginManager.Compat Band { get; set; } = PluginManager.Compat.Unknown;

        /// <summary>安装源：优先进 npm 包名（可带版本），否则取目录中 install 命令的最后一个参数，如 github:o/r。</summary>
        public string InstallSource
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Npm))
                    return Version.Length > 0 ? $"{Npm}@{Version}" : Npm!;
                string raw = Install.Trim();
                if (raw.Length == 0) return "";
                var parts = raw.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                string last = parts.Length > 0 ? parts[^1] : "";
                return last.Contains(':') || last.Contains('/') ? last : "";
            }
        }

        /// <summary>判断是否已安装所用的键：npm 包名或 owner/repo。</summary>
        public string MatchKey => !string.IsNullOrWhiteSpace(Npm) ? Npm! : $"{Owner}/{Name}";

        /// <summary>
        /// 显示名：将「仓库#子路径」形式的来源树写法规整为「名称 - 父级」。
        /// 例：<c>hindsight#coding-agents</c> → 「Coding Agents - Hindsight」；
        /// <c>@scope/pkg-name</c> → 「Pkg Name - Scope」。原始名保留在 ToolTip。
        /// </summary>
        public string DisplayName => FormatDisplayName(Name);

        /// <summary>将「来源树 / 作用域」写法统一为「名称 - 父级」（两个插件页面共用）。</summary>
        public static string FormatDisplayName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            int hash = raw.IndexOf('#');
            if (hash > 0)
            {
                string parent = raw.Substring(0, hash).Trim();
                string path = raw.Substring(hash + 1).Trim();
                string leaf = path.Split('/', '\\').LastOrDefault(s => s.Length > 0) ?? path;
                string pretty = PrettifyName(leaf);
                return pretty.Length > 0 && parent.Length > 0 ? $"{pretty} - {PrettifyName(parent)}" : raw;
            }
            if (raw.StartsWith("@") && raw.Contains('/'))
            {
                string scope = raw.Substring(1, raw.IndexOf('/') - 1);
                string leaf = raw.Substring(raw.IndexOf('/') + 1);
                return $"{PrettifyName(leaf)} - {PrettifyName(scope)}";
            }
            return raw;
        }

        /// <summary>将 kebab/snake 写法转为首字母大写形式；dsh→DSH、ui→UI 等专有缩写单独处理。</summary>
        public static string PrettifyName(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            var parts = s.Split(new[] { '-', '_', '.', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var words = new List<string>();
            foreach (string p in parts)
            {
                if (Acronyms.TryGetValue(p, out string? fixedForm)) { words.Add(fixedForm); continue; }
                words.Add(char.ToUpperInvariant(p[0]) + p.Substring(1));
            }
            return string.Join(" ", words);
        }

        private static readonly Dictionary<string, string> Acronyms = new(StringComparer.OrdinalIgnoreCase)
        {
            ["dsh"] = "DSH", ["ui"] = "UI", ["ai"] = "AI", ["api"] = "API", ["cli"] = "CLI",
            ["sdk"] = "SDK", ["mcp"] = "MCP", ["ssh"] = "SSH", ["url"] = "URL", ["uri"] = "URI",
            ["json"] = "JSON", ["css"] = "CSS", ["html"] = "HTML", ["id"] = "ID", ["tui"] = "TUI",
            ["llm"] = "LLM", ["gpt"] = "GPT", ["db"] = "DB", ["os"] = "OS", ["pc"] = "PC",
        };

        /// <summary>作者主页（GitHub 个人页）；无法获取时返回空串。</summary>
        public string AuthorUrl => Owner.Length > 0 ? "https://github.com/" + Owner : "";

        public string CategoryText(IReadOnlyDictionary<string, string> zhMap)
            => Categories.Count == 0 ? "" : string.Join(" · ",
                Categories.Select(c => zhMap.TryGetValue(c, out var zh) && zh.Length > 0 ? zh : c));

        /// <summary>兼容标识：只写版本号（与插件页同一套四色语义）。</summary>
        public string BandText => Band switch
        {
            PluginManager.Compat.Ok => Version.Length > 0 ? Version : "未声明",
            PluginManager.Compat.Partial => TargetVersion,
            PluginManager.Compat.Broken => TargetVersion,
            _ => "未声明"      // 未声明版本要求时留空会难以辨识，直接写明
        };

        public string TargetVersion => string.IsNullOrEmpty(Requirement)
            ? ""
            : (VersionInfo.RequirementVersions(Requirement)
                 .OrderByDescending(v => v, Comparer<string>.Create(VersionInfo.Compare)).FirstOrDefault() ?? "");
    }

    public sealed class MarketCatalog
    {
        public string Version { get; set; } = "";
        public string FetchedAt { get; set; } = "";
        public string Source { get; set; } = "";
        public string Updated { get; set; } = "";
        public bool Stale { get; set; }
        public Dictionary<string, string> CategoryZh { get; set; } = new();
        public List<MarketPlugin> Plugins { get; set; } = new();
        /// <summary>解析时跳过的条目数（正常为 0；不为 0 表明目录格式有变化）。</summary>
        public int Skipped { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>获取目录：缓存 → npm 快路 → 官方 URL → 旧缓存兜底。</summary>
    public static async Task<MarketCatalog> LoadAsync(bool force = false)
    {
        var cached = ReadCache();
        if (!force && cached != null && Age(cached.FetchedAt) < TimeSpan.FromHours(6))
        {
            cached.Source = "缓存";
            return cached;
        }

        // ① npm 快路（版本号作为校验器）
        try
        {
            var meta = await Http.GetStringAsync($"{NpmRegistry}/{CatalogPackage}/latest");
            using var doc = JsonDocument.Parse(meta);
            string version = doc.RootElement.TryGetProperty("version", out var v) ? (v.GetString() ?? "") : "";
            string tarball = doc.RootElement.TryGetProperty("dist", out var d) && d.TryGetProperty("tarball", out var t)
                ? (t.GetString() ?? "") : "";

            if (version.Length > 0 && cached != null && cached.Version == version && !force && File.Exists(CatalogFile))
            {
                Logger.Log($"插件目录：版本未变（{version}），用本地缓存");
                cached.Source = "缓存";
                return cached;
            }
            if (tarball.Length > 0)
            {
                byte[] tgz = await Http.GetByteArrayAsync(tarball);
                string json = ExtractPluginsJson(tgz);
                var cat = ParseCatalog(json, version, "镜像源");
                WriteCache(json, version, cat.FetchedAt, "npmmirror");
                Logger.Log($"插件目录：npmmirror 拉取成功 {cat.Plugins.Count} 条（{version}）");
                return cat;
            }
        }
        catch (Exception ex) { Logger.Log($"插件目录 npm 路由失败: {ex.Message}"); }

        // ② 官方地址兜底
        try
        {
            string json = await Http.GetStringAsync(FallbackCatalogUrl);
            var cat = ParseCatalog(json, "", "官方地址");
            WriteCache(json, cat.Version, cat.FetchedAt, "awesome-dsh-plugin.com");
            Logger.Log($"插件目录：官方地址拉取成功 {cat.Plugins.Count} 条（{cat.Updated}）");
            return cat;
        }
        catch (Exception ex) { Logger.Log($"插件目录 官方地址失败: {ex.Message}"); }

        // ③ 旧缓存兜底（明确标记为过期）
        if (cached != null)
        {
            cached.Stale = true;
            cached.Source = "缓存（已过期）";
            cached.Error = "目录获取失败（可能离线），当前显示的是本地缓存";
            return cached;
        }

        return new MarketCatalog { Error = "目录获取失败（可能离线，或下载来源暂时不可达）", FetchedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm") };
    }

    private static TimeSpan Age(string fetchedAt)
        => DateTime.TryParse(fetchedAt, out var t) ? DateTime.Now - t : TimeSpan.MaxValue;

    /// <summary>从 npm tgz 里取出 package/plugins.json。</summary>
    public static string ExtractPluginsJson(byte[] tgz, string entry = "package/plugins.json")
    {
        using var ms = new MemoryStream(tgz, writable: false);
        using var gz = new GZipStream(ms, CompressionMode.Decompress);
        using var reader = new TarReader(gz);
        while (reader.GetNextEntry() is { } e)
        {
            if (!string.Equals(e.Name, entry, StringComparison.OrdinalIgnoreCase)) continue;
            using var s = e.DataStream ?? Stream.Null;
            using var sr = new StreamReader(s, Encoding.UTF8);
            return sr.ReadToEnd();
        }
        throw new InvalidDataException($"tgz 里没有 {entry}");
    }

    /// <summary>解析目录 JSON（纯函数，便于自检）。</summary>
    public static MarketCatalog ParseCatalog(string json, string version, string source)
    {
        var cat = new MarketCatalog
        {
            Version = version,
            FetchedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            Source = source
        };

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        cat.Updated = Str(root, "updated");

        if (root.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Object)
        {
            foreach (var c in cats.EnumerateObject())
            {
                string zh = "";
                if (c.Value.ValueKind == JsonValueKind.Object && c.Value.TryGetProperty("zh", out var zhEl))
                    zh = zhEl.GetString() ?? "";
                cat.CategoryZh[c.Name] = zh;
            }
        }

        if (!root.TryGetProperty("plugins", out var arr) || arr.ValueKind != JsonValueKind.Array) return cat;

        foreach (var e in arr.EnumerateArray())
        {
            try
            {
                var p = new MarketPlugin
                {
                    Name = Str(e, "name"),
                    Owner = Str(e, "owner"),
                    RepoUrl = Str(e, "url"),
                    PageUrl = Str(e, "page"),
                    Npm = Str(e, "npm"),
                    Version = Str(e, "version"),
                    Install = Str(e, "install"),
                    Added = Str(e, "added"),
                    Stars = Num(e, "stars"),
                    Downloads = NumOrNull(e, "downloads"),
                };
                if (e.TryGetProperty("description", out var desc) && desc.ValueKind == JsonValueKind.Object)
                {
                    p.DescZh = Str(desc, "zh");
                    p.DescEn = Str(desc, "en");
                }
                if (p.DescZh.Length == 0) p.DescZh = p.DescEn;

                if (e.TryGetProperty("category", out var cg))
                {
                    if (cg.ValueKind == JsonValueKind.Array)
                        p.Categories.AddRange(cg.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0));
                    else if (cg.ValueKind == JsonValueKind.String)
                        p.Categories.Add(cg.GetString() ?? "");
                }

                if (e.TryGetProperty("screenshots", out var sh) && sh.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in sh.EnumerateArray())
                    {
                        if (s.ValueKind != JsonValueKind.String) continue;
                        string url = NormalizeImageUrl(s.GetString() ?? "");
                        if (url.Length > 0 && !p.Screenshots.Contains(url)) p.Screenshots.Add(url);
                        if (p.Screenshots.Count >= MaxScreenshots) break;
                    }
                }

                if (p.Name.Length > 0) cat.Plugins.Add(p);
                else cat.Skipped++;
            }
            catch (Exception ex)
            {
                // 逐条计入跳过数并记录日志：此前静默忽略异常，导致 1701 条 github-only 收录被整体丢弃。
                cat.Skipped++;
                if (cat.Skipped <= 3) Logger.Log($"插件目录：跳过条目 #{cat.Skipped}（{ex.GetType().Name}: {ex.Message}）");
            }
        }
        if (cat.Skipped > 0) Logger.Log($"插件目录：解析 {cat.Plugins.Count} 条，跳过 {cat.Skipped} 条");
        return cat;
    }

    private static string Str(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

    // ══════════════ 图片（截图）线路 ══════════════
    // 本机实测：直连 raw.githubusercontent.com 返回 502；
    // gh-proxy.com/<原url> 返回 200 / 0.9s，ghfast.top 返回 200 / 6.5s。
    // 与 dsh-market 中国区策略一致：代理优先，直连兜底。

    public const int MaxScreenshots = 6;
    public const string ImageProxy1 = "https://gh-proxy.com";
    public const string ImageProxy2 = "https://ghfast.top";

    /// <summary>截图仅允许 GitHub 自有图床（与 dsh-market 同一限制：其他域名一律不加载）。</summary>
    private static readonly string[] AllowedImageHosts =
    {
        "raw.githubusercontent.com", "github.com", "objects.githubusercontent.com",
        "user-images.githubusercontent.com", "private-user-images.githubusercontent.com",
        "camo.githubusercontent.com", "avatars.githubusercontent.com"
    };

    /// <summary>
    /// 将目录中的图片地址规整为可下载的直链：
    /// github.com/o/r/blob/&lt;ref&gt;/path → raw.githubusercontent.com/o/r/&lt;ref&gt;/path
    /// （前者是 HTML 页面，并非图片）。
    /// </summary>
    public static string NormalizeImageUrl(string url)
    {
        url = (url ?? "").Trim();
        if (url.Length == 0) return "";
        if (url.StartsWith("//")) url = "https:" + url;
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return "";

        var m = System.Text.RegularExpressions.Regex.Match(url,
            @"^https://github\.com/([^/]+)/([^/]+)/blob/([^/]+)/(.+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success)
            url = $"https://raw.githubusercontent.com/{m.Groups[1].Value}/{m.Groups[2].Value}/{m.Groups[3].Value}/{m.Groups[4].Value}";

        return IsAllowedImageUrl(url) ? url : "";
    }

    public static bool IsAllowedImageUrl(string url)
    {
        try { return AllowedImageHosts.Contains(new Uri(url).Host, StringComparer.OrdinalIgnoreCase); }
        catch { return false; }
    }

    /// <summary>
    /// 可**用系统浏览器打开**的链接 host 白名单（信任边界，与图片白名单同一闭合范式）。
    /// 目录 JSON 的 <c>url</c> / <c>page</c> 字段是外部输入，可被投毒成
    /// <c>\\attacker\share\evil.exe</c> 或任意 scheme；只有本清单里的 https host 才允许打开。
    /// </summary>
    private static readonly string[] AllowedLinkHosts =
    {
        "github.com", "gitee.com", "gitlab.com", "bitbucket.org",
        "raw.githubusercontent.com", "gist.github.com",
        // npmmirror 是**两个不同的 host**，必须各登记一条（本判据是 host 精确相等，不是后缀包含）：
        //   · registry.npmmirror.com —— 镜像源的 **API** host（npm 元数据 / 下载）
        //   · www.npmmirror.com      —— 镜像站的**网页** host（包页 HTML）
        // 与上面 npmjs.com / www.npmjs.com 同一范式：API 与网页各占一条，缺一不可。
        // 漏掉 www. 会让 PluginManager.Plugin.LinkUrl 第④档拼出的
        // https://www.npmmirror.com/package/<name> 被本闸门**自己拦下**
        // （点插件名弹「该链接不在允许打开的网站范围内」）—— 即自锁 bug，勿删。
        "npmjs.com", "www.npmjs.com", "registry.npmmirror.com", "www.npmmirror.com",
        "ohmydsh.github.io", "deepseekharness.github.io"
    };

    /// <summary>
    /// 这个链接能不能交给系统打开（纯函数，供自检断言）：
    /// 只放行 https + <see cref="AllowedLinkHosts"/>；file: / UNC / http 明文 / 其它 host / 解析失败一律拒绝。
    /// <para>
    /// 另拒**带凭据（userinfo）**的 URL：<c>https://user:pw@www.npmmirror.com/x</c> 的
    /// <see cref="Uri.Host"/> 仍是白名单内的 host ⇒ 仅比对 host 会放行。
    /// 白名单的语义是"只放行明确认识的站点"，凭据不属于该概念；且带凭据的地址会把凭据
    /// 一并交给系统浏览器/外壳。本项为**合取项**：只使判定集合变小，不会放行任何原本被拒的 URL。
    /// </para>
    /// </summary>
    public static bool IsAllowedLinkUrl(string url)
    {
        try
        {
            var uri = new Uri((url ?? "").Trim());
            return uri.Scheme == Uri.UriSchemeHttps
                && uri.UserInfo.Length == 0                                    // 带凭据一律拒绝（UserInfo 无凭据时为空串，非 null）
                && AllowedLinkHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>单张图片的候选下载线路（代理优先，直连最后）。</summary>
    public static List<string> ImageRoutes(string url)
    {
        var list = new List<string>();
        if (!IsAllowedImageUrl(url)) return list;

        bool raw = new Uri(url).Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase);
        if (raw)
        {
            list.Add($"{ImageProxy1}/{url}");
            list.Add($"{ImageProxy2}/{url}");
        }
        list.Add(url);
        return list;
    }

    /// <summary>目录未收录截图时，从仓库 README 中抓取图片。返回 raw 直链列表。</summary>
    public static async Task<List<string>> ScrapeReadmeImagesAsync(string owner, string repo)
    {
        var found = new List<string>();
        if (owner.Length == 0 || repo.Length == 0) return found;

        // 总时长上限：本动作由用户点击触发，属辅助功能，超时即放弃，不阻塞界面
        var sw = System.Diagnostics.Stopwatch.StartNew();
        const int BudgetMs = 25000;

        foreach (var branch in new[] { "HEAD", "main", "master" })
        {
            if (sw.ElapsedMilliseconds > BudgetMs) { Logger.Log($"捞图超预算（{owner}/{repo}）"); break; }

            string baseUrl = $"https://raw.githubusercontent.com/{owner}/{repo}/{branch}/";
            string? text = await FetchTextAsync($"{ImageProxy1}/{baseUrl}README.md")
                        ?? await FetchTextAsync($"{ImageProxy2}/{baseUrl}README.md")
                        ?? await FetchTextAsync($"{baseUrl}README.md");
            if (text == null) continue;

            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                         text, @"!\[[^\]]*\]\(\s*([^)\s]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                AddMarkdownImage(found, baseUrl, m.Groups[1].Value);

            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                         text, @"<img[^>]+src\s*=\s*[""']([^""']+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                AddMarkdownImage(found, baseUrl, m.Groups[1].Value);

            if (found.Count > 0) break;
        }
        return found;
    }

    private static void AddMarkdownImage(List<string> found, string baseUrl, string raw)
    {
        try
        {
            string src = raw.Trim().Trim('<', '>');
            if (src.Length == 0) return;
            if (!src.Contains("://")) src = new Uri(new Uri(baseUrl), src).ToString();
            string url = NormalizeImageUrl(src);
            if (url.Length == 0) return;

            string low = url.ToLowerInvariant();
            // 过滤徽章与图标类噪音（CI 徽章、许可徽章、logo）
            if (low.EndsWith(".svg") || low.Contains("shields.io") || low.Contains("badge")
                || low.Contains("codecov") || low.Contains("coveralls") || low.Contains("badgen")) return;

            if (!found.Contains(url) && found.Count < MaxScreenshots) found.Add(url);
        }
        catch { }
    }

    private static async Task<string?> FetchTextAsync(string url)
    {
        try
        {
            // 单次请求上限 12 秒：抓图为辅助动作，线路过慢时立即放弃并尝试下一条
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(12));
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            string text = await resp.Content.ReadAsStringAsync(cts.Token);
            // 代理失败时返回 HTML 错误页，而 README 不会是 HTML 页面
            return text.Contains("<html", StringComparison.OrdinalIgnoreCase) ? null : text;
        }
        catch { return null; }
    }

    /// <summary>数字字段：JSON null 及其他类型均按 0 处理；JsonElement.TryGetInt64 对非数字会抛异常，须先判断 ValueKind。</summary>
    private static long Num(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n : 0;

    private static long? NumOrNull(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n : null;

    /// <summary>搜索、分类与排序（desc=false 为升序）。</summary>
    public static List<MarketPlugin> Filter(MarketCatalog cat, string? query, string? category, MarketSort sort, bool desc = true)
    {
        IEnumerable<MarketPlugin> q = cat.Plugins;
        if (!string.IsNullOrWhiteSpace(category) && category != "全部")
            q = q.Where(p => p.Categories.Any(c =>
                string.Equals(c, category, StringComparison.OrdinalIgnoreCase) ||
                (cat.CategoryZh.TryGetValue(c, out var zh) && zh == category)));

        string kw = (query ?? "").Trim();
        if (kw.Length > 0)
            q = q.Where(p =>
                p.Name.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                p.Owner.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                p.Npm.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                p.DescZh.Contains(kw, StringComparison.OrdinalIgnoreCase));

        return sort switch
        {
            MarketSort.Downloads => (desc
                ? q.OrderByDescending(p => p.Downloads ?? -1)
                : q.OrderBy(p => p.Downloads ?? long.MaxValue))
                .ThenByDescending(p => p.Stars).ToList(),
            MarketSort.Published => (desc
                ? q.OrderByDescending(p => SortDate(p), StringComparer.Ordinal)
                : q.OrderBy(p => SortDate(p), StringComparer.Ordinal)).ToList(),
            _ => (desc
                ? q.OrderByDescending(p => p.Stars)
                : q.OrderBy(p => p.Stars)).ThenByDescending(p => p.Downloads ?? -1).ToList(),
        };
    }

    /// <summary>排序与「发布时间范围」所用的日期：优先 npm 最近发布时间，缺失时用收录日期。</summary>
    public static string SortDate(MarketPlugin p)
        => p.LatestPublished.Length > 0 ? p.LatestPublished : p.Added;

    /// <summary>
    /// 「适配当前版本」的判定：已查询过，且不属于明确不兼容的档位。
    /// 未查询与未声明的一律保留显示，避免凭推测隐藏插件。
    /// </summary>
    public static bool IsAdapted(MarketPlugin p)
        => p.MetaLoaded && p.Band != PluginManager.Compat.Broken;

    /// <summary>该收录是否在最近 days 天内更新或收录；无日期的按不在范围内处理。</summary>
    public static bool WithinDays(MarketPlugin p, int days)
    {
        string d = SortDate(p);
        if (d.Length < 8) return false;
        if (!DateTime.TryParse(d.Length >= 10 ? d.Substring(0, 10) : d, out var dt)) return false;
        return (DateTime.Now - dt).TotalDays <= days;
    }

    /// <summary>为目录条目补充「最近更新 / 声明要求 / 兼容档」，仅 npm 条目可用。</summary>
    public static void ApplyMeta(MarketPlugin p, string packumentJson, string currentDsh)
    {
        using var doc = JsonDocument.Parse(packumentJson);
        var root = doc.RootElement;

        string latest = "";
        if (root.TryGetProperty("dist-tags", out var tags) && tags.TryGetProperty("latest", out var l))
            latest = l.GetString() ?? "";
        if (latest.Length > 0 && root.TryGetProperty("time", out var time) && time.TryGetProperty(latest, out var t))
        {
            string iso = t.GetString() ?? "";
            p.LatestPublished = DateTimeOffset.TryParse(iso, out var dt)
                ? dt.ToLocalTime().ToString("yyyy-MM-dd") : iso;
        }
        if (p.Version.Length == 0) p.Version = latest;

        // 判据只留一份：转调 VersionInfo.ExtractDshRequirement —— 本地插件列表（PluginManager.Scan）
        // 走的是同一个函数，所以同一份 package.json 在「市场」与「本地」必然得出同一个 requirement/source。
        // 这里原先手写过一份（dsh.engines.dsh 优先 + peerDependencies 取最高），字段优先级与本地那份不同、
        // 初值也不同 —— 现场「市场能读到、本地一律未声明」正是两份判据各自演化出来的结果。
        string req = "", src = "";
        if (latest.Length > 0 && root.TryGetProperty("versions", out var vers) && vers.TryGetProperty(latest, out var vo))
        {
            var r = VersionInfo.ExtractDshRequirement(vo);
            req = r.Requirement;
            src = r.Source;
        }

        p.Requirement = req;
        p.RequirementSource = src;
        p.Band = PluginManager.EvaluateBand(req, currentDsh);
        p.MetaLoaded = true;
    }

    /// <summary>
    /// 为目录条目补充「最近更新 / 声明要求 / 兼容档」，按需查询 registry：仅查询当前页，避免 3000+ 次请求。
    /// 返回是否获取到新信息；404 等失败同样标记 MetaLoaded，避免反复重试。
    /// </summary>
    public static async Task<bool> FillMetaAsync(MarketPlugin p, string currentDsh)
    {
        if (p.MetaLoaded) return false;
        if (string.IsNullOrWhiteSpace(p.Npm)) { p.MetaLoaded = true; return false; }
        try
        {
            string json = await Http.GetStringAsync($"{NpmRegistry}/{Uri.EscapeDataString(p.Npm!)}");
            ApplyMeta(p, json, currentDsh);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Log($"市场元数据 {p.Npm} 查询失败: {ex.Message}");
            p.MetaLoaded = true;
            p.Band = PluginManager.Compat.Unknown;
            return false;
        }
    }

    // ══ 缓存读写 ══
    private static MarketCatalog? ReadCache()
    {
        try
        {
            if (!File.Exists(CatalogFile)) return null;
            string json = File.ReadAllText(CatalogFile, Encoding.UTF8);
            string version = "", at = "", src = "";
            if (File.Exists(CatalogMetaFile))
            {
                using var m = JsonDocument.Parse(File.ReadAllText(CatalogMetaFile, Encoding.UTF8));
                version = Str(m.RootElement, "catalogVersion");
                at = Str(m.RootElement, "fetchedAt");
                src = Str(m.RootElement, "source");
            }
            var cat = ParseCatalog(json, version, src.Length > 0 ? src + "（缓存）" : "缓存");
            cat.FetchedAt = at.Length > 0 ? at : cat.FetchedAt;
            return cat;
        }
        catch (Exception ex) { Logger.Log($"插件目录缓存读取失败: {ex.Message}"); return null; }
    }

    private static void WriteCache(string json, string version, string fetchedAt, string source)
    {
        try
        {
            Directory.CreateDirectory(GuardPaths.CacheDirMarket);
            File.WriteAllText(CatalogFile, json, new UTF8Encoding(false));
            File.WriteAllText(CatalogMetaFile,
                JsonSerializer.Serialize(new { catalogVersion = version, fetchedAt, source },
                    new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        }
        catch (Exception ex) { Logger.Log($"插件目录缓存写入失败: {ex.Message}"); }
    }
}
