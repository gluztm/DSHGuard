using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DSHGuard.Unified;

// ═══════════════════════════════════════════════════════════════════════
// 综合管理域的数据结构
//
// ⚠ 本文件**不重复实现任何既有的解析/扫描/写入**。已存在的能力全部复用：
//   · 插件扫描 / disabled 判定 / 启用禁用写入 → PluginManager（Scan / Enable / Disable）
//   · 路径寻址                                → GuardPaths（ProfileDirFor / ConfigDir…）
//   · 原子写                                  → AtomicFile
// 本文件只补 GuardPaths 与 PluginManager 都没覆盖的三项：
//   · profile 维度清单（web / desktop / headless）
//   · 技能文件扫描（~/.dsh 下的 skills 目录）
//   · 会话索引（~/.dsh/sessions 下的 zstd 会话）
// ═══════════════════════════════════════════════════════════════════════

/// <summary>
/// 一条提示词类单文件资源（prompt-inject.md / MEMORY.md）。
/// DSH 把它们当纯文本注入，本程序只读展示。
/// </summary>
public sealed class DshPromptFile
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Path { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Preview { get; set; } = "";
    public bool ReadOnly { get; set; } = true;
}

/// <summary>
/// 一条技能。字段对齐 DSH <c>@deepseek-ai/dsh-skill-filesystem</c> 的真实发现规则：
/// 只认被扫描根目录的直接子项（<c>&lt;name&gt;/SKILL.md</c> 或平铺 <c>&lt;name&gt;.md</c>），
/// 启停由 frontmatter 的 <c>disable-model-invocation</c> / <c>user-invocable</c> 决定。
/// </summary>
public sealed class DshSkill
{
    /// <summary>frontmatter 里的 name（kebab-case）；取不到时退回文件名。</summary>
    public string Name { get; set; } = "";

    /// <summary>相对所属技能根目录的路径（展示用）。</summary>
    public string RelativePath { get; set; } = "";

    public string Description { get; set; } = "";
    public string WhenToUse { get; set; } = "";

    /// <summary>根来源标签：project-dsh / project-agents / custom / user-dsh / user-agents / bundled / plugin。</summary>
    public string Source { get; set; } = "";

    /// <summary>该来源在 DSH 里的扫描优先级，数字越小越优先（plugin 不在 rank 表里，取 700）。</summary>
    public int Rank { get; set; }

    /// <summary>文件的绝对路径（读/写/删都靠它）。</summary>
    public string FullPath { get; set; } = "";

    /// <summary>所属技能根目录的绝对路径。</summary>
    public string RootPath { get; set; } = "";

    /// <summary>本程序能不能改它：文件系统根可改，插件 node_modules 里的只读。</summary>
    public bool Writable { get; set; }

    /// <summary><c>false</c> = frontmatter 写了 <c>disable-model-invocation: true</c>，模型侧看不到它。</summary>
    public bool ModelVisible { get; set; } = true;

    /// <summary><c>false</c> = frontmatter 写了 <c>user-invocable: false</c>，用户命令看不到它。</summary>
    public bool UserInvocable { get; set; } = true;

    /// <summary>frontmatter 是否合法（name 为 kebab-case 且 description 非空）。不合法时 DSH 会静默跳过。</summary>
    public bool Valid { get; set; }

    /// <summary>不合法/缺失的具体原因，给界面显示；合法时为空串。</summary>
    public string Problem { get; set; } = "";

    /// <summary>插件包名（如 <c>@liustack/modsearch</c>）；文件系统技能为空。</summary>
    public string PluginPackage { get; set; } = "";

    /// <summary>是否由插件包提供（这类技能不经过 filesystem provider，本程序只展示不改）。</summary>
    public bool IsPlugin { get; set; }

    /// <summary>
    /// 扫描时该文件原始字节的 SHA-256（十六进制小写）。写操作前拿它和目标文件比对：
    /// 不一致说明在用户看这一页的这段时间里，DSH 或别的程序把文件改过
    /// （改正文不用改 frontmatter，但整文件重写会覆盖对方刚写的正文），
    /// 这时应当拒绝写入并让用户刷新，而不是闭着眼睛把整份文件盖掉。
    /// </summary>
    public string Fingerprint { get; set; } = "";

    public string DisplayName => Name.Length > 0 ? Name : System.IO.Path.GetFileName(FullPath);

    /// <summary>给界面用的来源中文标签。</summary>
    public string SourceLabel => Source switch
    {
        "project-dsh" => "项目 · .dsh/skills",
        "project-agents" => "项目 · .agents/skills",
        "custom" => "自定义目录",
        "user-dsh" => "用户 · ~/.dsh/skills",
        "user-agents" => "用户 · ~/.agents/skills",
        "bundled" => "随包提供",
        "plugin" => "插件提供",
        _ => Source,
    };
}

/// <summary>
/// 技能发现器。严格按 DSH 的 <c>dsh-skill-filesystem</c> 规则扫描：
/// 根目录按 rank 依次为 项目 .dsh/skills(100) → 项目 .agents/skills(200) → customSkillDirs(300)
/// → ~/.dsh/skills(400, 跳过 .system) → ~/.agents/skills(500)；插件自带技能另立只读分组。
/// </summary>
public static class DshSkillReader
{
    public const int RankProjectDsh = 100;
    public const int RankProjectAgents = 200;
    public const int RankCustom = 300;
    public const int RankUserDsh = 400;
    public const int RankUserAgents = 500;
    public const int RankBundled = 600;
    public const int RankPlugin = 700;

    /// <summary>共享 agent 配置根：<c>$DSH_AGENTS_HOME</c>，未设置时为 <c>~/.agents</c>。</summary>
    public static string AgentsHome { get; } = ResolveAgentsHome();

    /// <summary>DSH 配置根下的技能目录（<c>~/.dsh/skills</c>）。</summary>
    public static string UserDshSkillRoot => Path.Combine(GuardPaths.DshHome, "skills");

    /// <summary>共享 agent 配置根下的技能目录（<c>~/.agents/skills</c>）。</summary>
    public static string UserAgentsSkillRoot => Path.Combine(AgentsHome, "skills");

    private static string ResolveAgentsHome()
    {
        string? env = Environment.GetEnvironmentVariable("DSH_AGENTS_HOME");
        if (!string.IsNullOrWhiteSpace(env)) return env!;
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) home = GuardPaths.DshHome;
        return Path.Combine(home, ".agents");
    }

    /// <summary>一个被扫描的技能根。</summary>
    public sealed class SkillRoot
    {
        public string Path { get; set; } = "";
        public string Source { get; set; } = "";
        public int Rank { get; set; }
        public bool Writable { get; set; }
        public bool SkipSystem { get; set; }

        /// <summary>项目根技能根所属的项目目录；用户级根为空串。</summary>
        public string ProjectDir { get; set; } = "";

        public bool Exists
        {
            get
            {
                try { return Directory.Exists(Path); }
                catch { return false; }
            }
        }

        public string SourceLabel => Source switch
        {
            "project-dsh" => "项目 · .dsh/skills",
            "project-agents" => "项目 · .agents/skills",
            "custom" => "自定义目录",
            "user-dsh" => "用户 · ~/.dsh/skills",
            "user-agents" => "用户 · ~/.agents/skills",
            "bundled" => "随包提供",
            _ => Source,
        };
    }

    /// <summary>全部被扫描的技能根（含暂不存在的，界面要能提示并新建）。</summary>
    public static List<SkillRoot> Roots()
    {
        var roots = new List<SkillRoot>();

        foreach (string project in ProjectRoots())
        {
            roots.Add(new SkillRoot
            {
                Path = Path.Combine(project, ".dsh", "skills"),
                Source = "project-dsh",
                Rank = RankProjectDsh,
                Writable = true,
                SkipSystem = false,
                ProjectDir = project,
            });
            roots.Add(new SkillRoot
            {
                Path = Path.Combine(project, ".agents", "skills"),
                Source = "project-agents",
                Rank = RankProjectAgents,
                Writable = true,
                SkipSystem = false,
                ProjectDir = project,
            });
        }

        foreach (string custom in CustomSkillDirs())
        {
            roots.Add(new SkillRoot
            {
                Path = custom,
                Source = "custom",
                Rank = RankCustom,
                Writable = true,
                SkipSystem = false,
            });
        }

        roots.Add(new SkillRoot
        {
            Path = UserDshSkillRoot,
            Source = "user-dsh",
            Rank = RankUserDsh,
            Writable = true,
            SkipSystem = true,
        });
        roots.Add(new SkillRoot
        {
            Path = UserAgentsSkillRoot,
            Source = "user-agents",
            Rank = RankUserAgents,
            Writable = true,
            SkipSystem = false,
        });

        return roots
            .GroupBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(r => r.Rank).First())
            .OrderBy(r => r.Rank)
            .ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// DSH 打开过的项目目录（从 <c>~/.dsh/sessions/*</c> 的项目名解码得到），只保留仍然存在的目录。
    /// 项目根技能根就是这些目录下的 <c>.dsh/skills</c> 与 <c>.agents/skills</c>。
    /// </summary>
    public static List<string> ProjectRoots()
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string root = DshSessionReader.SessionsRoot;
            if (!Directory.Exists(root)) return list;

            foreach (string dir in Directory.EnumerateDirectories(root))
            {
                string decoded;
                try { decoded = DshSessionReader.UnescapeProjectName(Path.GetFileName(dir)); }
                catch { continue; }

                if (decoded.Length == 0) continue;
                try { if (!Directory.Exists(decoded)) continue; }
                catch { continue; }
                if (!seen.Add(decoded)) continue;

                list.Add(decoded);
                if (list.Count >= 64) break;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("DshSkillReader(project roots)", ex);
        }
        return list;
    }

    /// <summary>从各 profile 的 cordis.patch.yml 里读 <c>customSkillDirs</c>（块序列或 flow 数组两种写法）。</summary>
    public static List<string> CustomSkillDirs()
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (DshProfileInfo profile in DshProfileRegistry.Existing())
        {
            string patch;
            try { patch = profile.PatchFile(); }
            catch (Exception ex)
            {
                Logger.LogError("DshSkillReader(custom dirs " + profile.Id + ")", ex);
                continue;
            }
            if (!File.Exists(patch)) continue;

            string[] lines;
            try { lines = File.ReadAllLines(patch); }
            catch (Exception ex)
            {
                Logger.LogError("DshSkillReader(custom dirs read " + patch + ")", ex);
                continue;
            }

            for (int i = 0; i < lines.Length; i++)
            {
                string t = lines[i].Trim();
                if (!t.StartsWith("customSkillDirs", StringComparison.Ordinal)) continue;
                int colon = t.IndexOf(':');
                if (colon < 0) continue;

                string tail = t.Substring(colon + 1).Trim();
                if (tail.StartsWith('['))
                {
                    int close = tail.IndexOf(']');
                    string inner = close > 0 ? tail.Substring(1, close - 1) : tail.Substring(1);
                    foreach (string piece in inner.Split(','))
                    {
                        string v = Unquote(piece.Trim());
                        if (v.Length > 0 && seen.Add(v)) list.Add(v);
                    }
                    continue;
                }

                int baseIndent = IndentOf(lines[i]);
                for (int j = i + 1; j < lines.Length; j++)
                {
                    string u = lines[j];
                    string ut = u.Trim();
                    if (ut.Length == 0) continue;
                    if (IndentOf(u) <= baseIndent) break;
                    if (!ut.StartsWith("- ")) continue;
                    string v = Unquote(ut.Substring(2).Trim());
                    if (v.Length > 0 && seen.Add(v)) list.Add(v);
                }
            }
        }

        return list;
    }

    /// <summary>扫描全部技能（文件系统根 + 插件自带）。</summary>
    public static List<DshSkill> Scan()
    {
        var list = new List<DshSkill>();
        var seenPath = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (SkillRoot root in Roots())
        {
            foreach (var candidate in SkillFilesIn(root))
            {
                var (file, nameHint) = candidate;
                if (!seenPath.Add(file)) continue;
                try
                {
                    list.Add(Make(file, root, nameHint));
                }
                catch (Exception ex)
                {
                    Logger.LogError("DshSkillReader(file " + file + ")", ex);
                }
            }
        }

        list.AddRange(ScanPluginSkills());

        return list
            .OrderBy(s => s.Rank)
            .ThenBy(s => s.SourceLabel, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>一个根下的技能文件：只认直接子项（目录 bundle 的 SKILL.md，或平铺 .md）。</summary>
    private static IEnumerable<(string File, string NameHint)> SkillFilesIn(SkillRoot root)
    {
        string[] dirs;
        string[] files;
        try
        {
            if (!Directory.Exists(root.Path)) yield break;
            dirs = Directory.GetDirectories(root.Path);
            files = Directory.GetFiles(root.Path);
        }
        catch (Exception ex)
        {
            Logger.LogError("DshSkillReader(root " + root.Path + ")", ex);
            yield break;
        }

        foreach (string dir in dirs)
        {
            string leaf = Path.GetFileName(dir);
            if (leaf.Length == 0 || leaf.StartsWith('.')) continue;
            if (root.SkipSystem && leaf.Equals(".system", StringComparison.OrdinalIgnoreCase)) continue;

            string skillFile = Path.Combine(dir, "SKILL.md");
            bool hasSkill;
            try { hasSkill = File.Exists(skillFile); }
            catch (Exception ex)
            {
                Logger.LogError("DshSkillReader(bundle " + skillFile + ")", ex);
                hasSkill = false;
            }

            if (hasSkill) yield return (skillFile, leaf);
        }

        foreach (string file in files)
        {
            string leaf = Path.GetFileName(file);
            if (leaf.Length == 0 || leaf.StartsWith('.')) continue;
            if (!leaf.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) continue;
            yield return (file, Path.GetFileNameWithoutExtension(leaf));
        }
    }

    /// <summary>插件包提供的技能：包根 <c>SKILL.md</c> 与 <c>skills/&lt;name&gt;/SKILL.md</c>。跨 profile 按「包 + 技能」去重。</summary>
    private static List<DshSkill> ScanPluginSkills()
    {
        var list = new List<DshSkill>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (DshProfileInfo profile in DshProfileRegistry.Existing())
        {
            string nodeModules;
            try { nodeModules = Path.Combine(profile.ProfileDir(), "node_modules"); }
            catch (Exception ex)
            {
                Logger.LogError("DshSkillReader(node_modules " + profile.Id + ")", ex);
                continue;
            }
            if (!Directory.Exists(nodeModules)) continue;

            foreach (string pkgDir in EnumeratePackages(nodeModules))
            {
                string pkgName;
                try { pkgName = Path.GetRelativePath(nodeModules, pkgDir).Replace('\\', '/'); }
                catch { pkgName = Path.GetFileName(pkgDir); }

                foreach (var candidate in PluginSkillFiles(pkgDir))
                {
                    var (file, skillName) = candidate;
                    if (file == null) continue;
                    if (!seen.Add(pkgName + "|" + skillName)) continue;
                    try
                    {
                        DshSkill skill = MakePlugin(file, pkgDir, pkgName, skillName);
                        list.Add(skill);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError("DshSkillReader(plugin " + file + ")", ex);
                    }
                }
            }
        }

        return list;
    }

    private static DshSkill MakePlugin(string file, string pkgDir, string pkgName, string skillName)
    {
        Dictionary<string, string> map = ReadFrontmatter(file);
        string fmName = Get(map, "name");
        string desc = Get(map, "description");

        return new DshSkill
        {
            Name = fmName.Length > 0 ? fmName : skillName,
            RelativePath = SafeRelative(pkgDir, file),
            Description = desc.Length > 0 ? Clip(desc, 110) : ReadBodySnippet(file),
            WhenToUse = Clip(Get(map, "whenToUse"), 110),
            Source = "plugin",
            Rank = RankPlugin,
            FullPath = file,
            RootPath = pkgDir,
            Writable = false,
            ModelVisible = !ParseBool(Get(map, "disable-model-invocation"), false),
            UserInvocable = ParseBool(Get(map, "user-invocable"), true),
            Valid = fmName.Length > 0 && desc.Length > 0,
            Problem = "",
            PluginPackage = pkgName,
            IsPlugin = true,
            Fingerprint = FileTrust.Sha256Hex(file),
        };
    }

    private static DshSkill Make(string file, SkillRoot root, string nameHint)
    {
        Dictionary<string, string> map = ReadFrontmatter(file);
        string fmName = Get(map, "name");
        string desc = Get(map, "description");

        string problem = "";
        if (fmName.Length == 0) problem = "frontmatter 缺 name";
        else if (!IsKebabCase(fmName)) problem = "frontmatter 的 name 不是 kebab-case";
        else if (desc.Length == 0) problem = "frontmatter 缺 description";

        return new DshSkill
        {
            Name = fmName.Length > 0 ? fmName : nameHint,
            RelativePath = SafeRelative(root.Path, file),
            Description = desc.Length > 0 ? Clip(desc, 110) : ReadBodySnippet(file),
            WhenToUse = Clip(Get(map, "whenToUse"), 110),
            Source = root.Source,
            Rank = root.Rank,
            FullPath = file,
            RootPath = root.Path,
            Writable = root.Writable,
            ModelVisible = !ParseBool(Get(map, "disable-model-invocation"), false),
            UserInvocable = ParseBool(Get(map, "user-invocable"), true),
            Valid = problem.Length == 0,
            Problem = problem.Length == 0 ? "" : problem + "（DSH 会跳过这个文件）",
            PluginPackage = "",
            IsPlugin = false,
            Fingerprint = FileTrust.Sha256Hex(file),
        };
    }

    /// <summary>读 frontmatter（文件开头的 <c>---</c> 块）成键值表；没有 frontmatter 返回空表。</summary>
    public static Dictionary<string, string> ReadFrontmatter(string file)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string[] lines = File.ReadAllLines(file);
            if (lines.Length == 0) return map;
            if (lines[0].Trim() != "---") return map;

            for (int i = 1; i < lines.Length; i++)
            {
                string t = lines[i].Trim();
                if (t == "---") break;
                if (t.Length == 0 || t.StartsWith('#')) continue;
                int colon = t.IndexOf(':');
                if (colon <= 0) continue;
                string key = t.Substring(0, colon).Trim();
                string val = Unquote(t.Substring(colon + 1).Trim());
                if (key.Length > 0) map[key] = val;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("DshSkillReader(frontmatter " + file + ")", ex);
        }
        return map;
    }

    /// <summary>按 DSH 的宽松布尔语法解析：true/false、yes/no、on/off、1/0（不分大小写）。无法解析时返回 <paramref name="fallback"/>。</summary>
    public static bool ParseBool(string? raw, bool fallback)
    {
        if (raw == null) return fallback;
        string v = raw.Trim().Trim('"', '\'').Trim();
        if (v.Length == 0) return fallback;
        switch (v.ToLowerInvariant())
        {
            case "true": case "yes": case "on": case "1": return true;
            case "false": case "no": case "off": case "0": return false;
            default: return fallback;
        }
    }

    /// <summary>name 必须是 kebab-case（小写字母/数字，单词间单连字符）。</summary>
    public static bool IsKebabCase(string name)
    {
        if (name.Length == 0) return false;
        bool prevDash = false;
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (c == '-')
            {
                if (i == 0 || i == name.Length - 1 || prevDash) return false;
                prevDash = true;
                continue;
            }
            prevDash = false;
            bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
            if (!ok) return false;
        }
        return true;
    }

    private static string Get(Dictionary<string, string> map, string key) =>
        map.TryGetValue(key, out string? v) && v != null ? v : "";

    private static string Clip(string value, int max)
    {
        string v = value.Trim();
        if (v.Length <= max) return v;
        return v.Substring(0, Math.Max(0, max - 3)) + "...";
    }

    private static string SafeRelative(string root, string file)
    {
        try { return Path.GetRelativePath(root, file).Replace('\\', '/'); }
        catch { return Path.GetFileName(file); }
    }

    private static int IndentOf(string line)
    {
        int i = 0;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
        return i;
    }

    private static string Unquote(string value)
    {
        string v = value.Trim();
        if (v.Length >= 2)
        {
            char a = v[0];
            char b = v[v.Length - 1];
            if ((a == '"' && b == '"') || (a == '\'' && b == '\''))
                return v.Substring(1, v.Length - 2);
        }
        return v;
    }

    /// <summary>枚举 node_modules 下的包目录（支持 @scope/pkg，跳过 . 开头的）。</summary>
    private static IEnumerable<string> EnumeratePackages(string nodeModules)
    {
        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories(nodeModules); }
        catch (Exception ex)
        {
            Logger.LogError("DshSkillReader(packages " + nodeModules + ")", ex);
            yield break;
        }

        foreach (string dir in dirs)
        {
            string leaf = Path.GetFileName(dir);
            if (leaf.Length == 0 || leaf.StartsWith('.') || leaf == "node_modules") continue;

            if (leaf.StartsWith('@'))
            {
                string[] subs;
                try { subs = Directory.GetDirectories(dir); }
                catch { continue; }
                foreach (string sub in subs)
                {
                    string subLeaf = Path.GetFileName(sub);
                    if (subLeaf.Length > 0 && !subLeaf.StartsWith('.')) yield return sub;
                }
                continue;
            }

            yield return dir;
        }
    }

    /// <summary>枚举一个插件包可能提供的技能文件。</summary>
    private static IEnumerable<(string? File, string SkillName)> PluginSkillFiles(string pkgDir)
    {
        string rootSkill = Path.Combine(pkgDir, "SKILL.md");
        bool hasRoot;
        try { hasRoot = File.Exists(rootSkill); }
        catch { hasRoot = false; }

        if (hasRoot) yield return (rootSkill, Path.GetFileName(pkgDir));

        string skillsDir = Path.Combine(pkgDir, "skills");
        if (!Directory.Exists(skillsDir)) yield break;

        string[] subs;
        try { subs = Directory.GetDirectories(skillsDir); }
        catch { yield break; }

        foreach (string sub in subs)
        {
            string leaf = Path.GetFileName(sub);
            if (leaf.Length == 0 || leaf.StartsWith('.')) continue;
            string f = Path.Combine(sub, "SKILL.md");
            bool exists;
            try { exists = File.Exists(f); }
            catch { exists = false; }
            if (exists) yield return (f, leaf);
        }
    }

    /// <summary>取 .md 里第一段正文当描述（跳过标题/分隔线/引用/空行）。取不到返回空串，不假造。</summary>
    private static string ReadBodySnippet(string file)
    {
        try
        {
            bool inFrontmatter = false;
            int seen = 0;
            foreach (string line in File.ReadLines(file))
            {
                string t = line.Trim();
                if (seen == 0 && t == "---")
                {
                    inFrontmatter = true;
                    seen = 1;
                    continue;
                }
                if (inFrontmatter)
                {
                    if (t == "---") inFrontmatter = false;
                    continue;
                }
                if (t.Length == 0 || t.StartsWith('#') || t.StartsWith("---") || t.StartsWith('>'))
                    continue;
                return Clip(t, 110);
            }
        }
        catch { }
        return "";
    }
}

/// <summary>
/// 提示词文件扫描。
///
/// 两个来源：
/// 1. DSH 自己的两个文件（prompt-inject.md / MEMORY.md）—— 只读展示，不代写；
/// 2. <c>~/.dsh/prompts/*.md</c> —— 用户在本程序里自建的提示词（§需求 3），
///    Id 统一加 <c>custom:</c> 前缀，<see cref="DshPromptFile.ReadOnly"/> 为 false，
///    所以卡片上会出现编辑与删除。
/// </summary>
public static class DshPromptReader
{
    /// <summary>自建提示词目录：放在 DSH 主目录下，和 DSH 自己的文件同处一地，好找。</summary>
    public static string CustomPromptRoot => Path.Combine(GuardPaths.DshHome, "prompts");

    public static List<DshPromptFile> Scan()
    {
        var list = new List<DshPromptFile>();
        var candidates = new (string Id, string Name, string Path)[]
        {
            ("prompt-inject", "提示词注入", Path.Combine(GuardPaths.DshHome, "prompt-inject.md")),
            ("memory", "长期记忆", Path.Combine(GuardPaths.DshHome, "MEMORY.md")),
        };

        foreach (var (id, name, path) in candidates)
        {
            try
            {
                if (!File.Exists(path)) continue;
                var fi = new FileInfo(path);
                list.Add(new DshPromptFile
                {
                    Id = id,
                    DisplayName = name,
                    Path = path,
                    SizeBytes = fi.Length,
                    Preview = ReadPreview(path),
                    ReadOnly = true,
                });
            }
            catch (Exception ex)
            {
                Logger.LogError("DshPromptReader(" + id + ")", ex);
            }
        }

        foreach (var file in ScanCustom(CustomPromptRoot)) list.Add(file);
        return list;
    }

    /// <summary>扫一个目录下的自建提示词（*.md，按文件名排序，最多 200 个）。</summary>
    public static List<DshPromptFile> ScanCustom(string root)
    {
        var list = new List<DshPromptFile>();
        try
        {
            if (!Directory.Exists(root)) return list;

            var files = new List<string>(Directory.GetFiles(root, "*.md", SearchOption.TopDirectoryOnly));
            files.Sort(StringComparer.OrdinalIgnoreCase);

            foreach (string path in files)
            {
                if (list.Count >= 200) break;
                try
                {
                    var fi = new FileInfo(path);
                    list.Add(new DshPromptFile
                    {
                        Id = "custom:" + fi.Name,
                        DisplayName = Path.GetFileNameWithoutExtension(fi.Name),
                        Path = path,
                        SizeBytes = fi.Length,
                        Preview = ReadPreview(path),
                        ReadOnly = false,
                    });
                }
                catch (Exception ex)
                {
                    Logger.LogError("DshPromptReader.ScanCustom(" + path + ")", ex);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("DshPromptReader.ScanCustom", ex);
        }
        return list;
    }

    private static string ReadPreview(string path)
    {
        try
        {
            // 只读前几行做预览，不整读大文件
            var lines = new List<string>();
            foreach (string line in File.ReadLines(path).Take(4))
            {
                string t = line.Trim();
                if (t.Length > 0) lines.Add(t.Length <= 90 ? t : t.Substring(0, 87) + "...");
                if (lines.Count >= 2) break;
            }
            return string.Join(" · ", lines);
        }
        catch { return ""; }
    }
}

/// <summary>
/// 会话扫描。
///
/// DSH 的会话布局（2026-10-27 实测）：
///   ~/.dsh/sessions/&lt;项目路径转义&gt;/&lt;session-uuid&gt;/session.v4.jsonl.zstd
/// 项目目录名是路径转义（如 <c>--D-Agents~0020WorkSpace-DSH-Data--</c>），
/// 需要还原成人能读的项目路径。
///
/// ⚠ 只读文件属性，**不解压 zstd**：
///   本程序零第三方依赖，没有 zstd 解码器；硬上会引入原生库依赖。
///   会话内容展示留给 DSH 自己（它有原生解码）。本页只回答
///   "有哪些会话、在哪个项目、多大、什么时候动过、文件在哪"。
///
/// 标题例外：不需要解压也能拿到 —— DSH 自己有一份明文缓存
///   <c>~/.dsh/storages/session_projcache/sessions/&lt;uuid&gt;.json</c>
/// 其中 <c>record.rows.title.val</c> 就是会话标题。本机实测 83 个会话命中 62 个，
/// 剩下 21 个是几百字节的空壳会话（解压全文也没有 title 事件），
/// 所以解 zstd 这条路在这个数据源上零收益。
/// </summary>
public static class DshSessionReader
{
    public static string SessionsRoot => Path.Combine(GuardPaths.DshHome, "sessions");

    /// <summary>会话标题缓存目录（DSH 自己写的明文 JSON，一 uuid 一个文件）。</summary>
    public static string TitleCacheRoot =>
        Path.Combine(GuardPaths.DshHome, "storages", "session_projcache", "sessions");

    public static List<DshSession> Scan()
    {
        var list = new List<DshSession>();
        try
        {
            Dictionary<string, string> titles = LoadTitleCache();
            MoveArchivedBackOnce();
            ScanInto(SessionsRoot, titles, list);
            AttachRelations(list);
        }
        catch (Exception ex)
        {
            Logger.LogError("DshSessionReader.Scan", ex);
        }
        return list.OrderByDescending(s => s.LastWriteUnix).ToList();
    }

    /// <summary>扫 DSH 自己的会话根：&lt;根&gt;/&lt;项目转义名&gt;/&lt;uuid&gt;/session.*.zstd。</summary>
    private static void ScanInto(string root, Dictionary<string, string> titles, List<DshSession> list)
    {
        try
        {
            if (!Directory.Exists(root)) return;

            foreach (string projectDir in Directory.EnumerateDirectories(root))
            {
                try
                {
                    string projectName = UnescapeProjectName(Path.GetFileName(projectDir));

                    foreach (string sessionDir in Directory.EnumerateDirectories(projectDir))
                    {
                        try
                        {
                            string? file = Directory.EnumerateFiles(sessionDir, "session.*.zstd")
                                .OrderByDescending(f => f)
                                .FirstOrDefault();
                            if (file == null) continue;

                            string sid = Path.GetFileName(sessionDir);
                            var fi = new FileInfo(file);
                            list.Add(new DshSession
                            {
                                SessionId = sid,
                                ProjectDir = projectName,
                                ProjectName = Path.GetFileName(projectName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                                RawProjectDir = Path.GetFileName(projectDir),
                                FilePath = file,
                                SizeBytes = fi.Length,
                                LastWriteUnix = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
                                Title = titles.TryGetValue(sid, out string? t) ? t : "",
                            });
                        }
                        catch (Exception ex)
                        {
                            Logger.LogError("DshSessionReader(session " + sessionDir + ")", ex);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError("DshSessionReader(project " + projectDir + ")", ex);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("DshSessionReader.ScanInto(" + root + ")", ex);
        }
    }

    /// <summary>
    /// 从 DSH 的会话缓存推导主子代理关系。
    ///
    /// DSH 的会话里没有 parentSessionId 这种字段，只能反向推：
    ///   · 父会话缓存 record.rows.subagentCatalog.head.values[].childId ⇒ 它派生的子会话 id；
    ///   · 子会话自己的 record.rows.subagent.identity.label ⇒ 父给它起的名字。
    /// 因此只有父会话的缓存还在时才推得出来；推不出来就如实留空，不猜。
    /// </summary>
    private static void AttachRelations(List<DshSession> list)
    {
        try
        {
            if (list.Count == 0 || !Directory.Exists(TitleCacheRoot)) return;

            var byId = new Dictionary<string, DshSession>(StringComparer.OrdinalIgnoreCase);
            foreach (DshSession s in list) byId[s.SessionId] = s;

            var parentOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (string file in Directory.EnumerateFiles(TitleCacheRoot, "*.json"))
            {
                try
                {
                    string parentId = Path.GetFileNameWithoutExtension(file);
                    if (!byId.TryGetValue(parentId, out DshSession? parent)) continue;

                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    if (!doc.RootElement.TryGetProperty("record", out var rec)) continue;
                    if (!rec.TryGetProperty("rows", out var rows)) continue;
                    if (!rows.TryGetProperty("subagentCatalog", out var cat)) continue;
                    if (!cat.TryGetProperty("head", out var head)) continue;
                    if (!head.TryGetProperty("values", out var vals) || vals.ValueKind != JsonValueKind.Array) continue;

                    foreach (var item in vals.EnumerateArray())
                    {
                        if (!item.TryGetProperty("childId", out var cid)) continue;
                        string child = (cid.GetString() ?? "").Trim();
                        if (child.Length == 0) continue;
                        if (!parent.ChildIds.Contains(child, StringComparer.OrdinalIgnoreCase))
                            parent.ChildIds.Add(child);
                        if (!parentOf.ContainsKey(child)) parentOf[child] = parentId;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError("DshSessionReader.AttachRelations(" + Path.GetFileName(file) + ")", ex);
                }
            }

            foreach (var kv in parentOf)
            {
                if (byId.TryGetValue(kv.Key, out DshSession? child)) child.ParentId = kv.Value;
            }

            foreach (DshSession s in list)
            {
                if (s.ParentId.Length == 0) continue;
                s.ParentLabel = LoadSubagentLabel(s.SessionId);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("DshSessionReader.AttachRelations", ex);
        }
    }

    /// <summary>读子会话缓存里的 record.rows.subagent.identity.label（父给子起的名字）。</summary>
    private static string LoadSubagentLabel(string sessionId)
    {
        try
        {
            if (sessionId.Length == 0) return "";
            string file = Path.Combine(TitleCacheRoot, sessionId + ".json");
            if (!File.Exists(file)) return "";

            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (!doc.RootElement.TryGetProperty("record", out var rec)) return "";
            if (!rec.TryGetProperty("rows", out var rows)) return "";
            if (!rows.TryGetProperty("subagent", out var sub)) return "";
            if (!sub.TryGetProperty("identity", out var id)) return "";
            if (!id.TryGetProperty("label", out var lab)) return "";
            if (lab.ValueKind != JsonValueKind.String) return "";
            return (lab.GetString() ?? "").Trim();
        }
        catch { return ""; }
    }

    /// <summary>
    /// 读会话标题缓存：<c>~/.dsh/storages/session_projcache/sessions/&lt;uuid&gt;.json</c>，
    /// 取 <c>record.rows.title.val</c>。键是会话 uuid。
    ///
    /// 容错口径（这里任何一条都不能把整页扫崩）：
    ///   · 目录不存在 / 无权读 → 返回空字典，界面全部退回短 id；
    ///   · 单个文件 JSON 坏了 → 跳过那一个，继续读别的；
    ///   · title 存在但不是字符串（版本变更）→ 跳过；
    ///   · title 是空串或纯空白 → 当作没有，不让空标题占位。
    /// </summary>
    private static Dictionary<string, string> LoadTitleCache()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!Directory.Exists(TitleCacheRoot)) return map;

            foreach (string file in Directory.EnumerateFiles(TitleCacheRoot, "*.json"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    if (!doc.RootElement.TryGetProperty("record", out var rec)) continue;
                    if (!rec.TryGetProperty("rows", out var rows)) continue;
                    if (!rows.TryGetProperty("title", out var trow)) continue;
                    if (!trow.TryGetProperty("val", out var val)) continue;
                    if (val.ValueKind != JsonValueKind.String) continue;

                    string title = (val.GetString() ?? "").Trim();
                    if (title.Length == 0) continue;

                    map[Path.GetFileNameWithoutExtension(file)] = title;
                }
                catch (Exception ex)
                {
                    Logger.LogError("DshSessionReader.LoadTitleCache(" + Path.GetFileName(file) + ")", ex);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("DshSessionReader.LoadTitleCache", ex);
        }
        return map;
    }

    /// <summary>
    /// 把 DSH 的项目目录名还原成路径。
    ///
    /// 实测转义规则（2026-10-27 本机 <c>~/.dsh/sessions</c>）：
    ///   <c>--D-Agents~0020WorkSpace-DSH-Data--</c> ⇒ <c>D:\Agents WorkSpace\DSH-Data</c>
    ///   · 首尾 <c>--</c> 是包裹符
    ///   · <c>~XXXX</c> 是四位十六进制 unicode 转义（<c>~0020</c> = 空格）
    ///   · <c>-</c> 是路径分隔符
    ///
    /// ⚠ 歧义：连字符 <c>-</c> 与分隔符用的是同一个字符，无法从字符串本身唯一反解
    /// （<c>DSH-Data</c> 是一个目录名还是 <c>DSH\Data</c> 两层？单看转义串无法区分）。
    /// 因此本方法**不猜**：它先按分隔符还原出候选路径，再拿磁盘事实校验 ——
    ///   ① 候选整体存在 ⇒ 用它
    ///   ② 否则逐段尝试"把 <c>-</c> 收回成连字符"的组合，取第一个真实存在的
    ///   ③ 全部不存在 ⇒ 返回候选（如实给出推导结果，不假装确定）
    /// 这样对真实存在的项目总能给出正确路径，对已删除的项目则给出可读的近似值。
    /// </summary>
    internal static string UnescapeProjectName(string raw)
    {
        try
        {
            string s = raw;
            if (s.StartsWith("--", StringComparison.Ordinal)) s = s.Substring(2);
            if (s.EndsWith("--", StringComparison.Ordinal)) s = s.Substring(0, s.Length - 2);
            if (s.Length == 0) return raw;

            // 先把 ~XXXX 解成字符（这部分无歧义）
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '~' && i + 4 < s.Length &&
                    int.TryParse(s.Substring(i + 1, 4), System.Globalization.NumberStyles.HexNumber,
                        null, out int code))
                {
                    sb.Append((char)code);
                    i += 4;
                }
                else sb.Append(s[i]);
            }
            string unescaped = sb.ToString();

            // 盘符：首字符单字母 + 分隔符 ⇒ 补冒号
            string best = ResolveByDiskFacts(unescaped);
            if (best.Length >= 2 && char.IsLetter(best[0]) && best[1] == '\\')
                best = best.Substring(0, 1) + ":" + best.Substring(1);

            return best.Length > 0 ? best : raw;
        }
        catch { return raw; }
    }

    /// <summary>
    /// 按磁盘事实确定 <c>-</c> 到底是分隔符还是连字符。
    ///
    /// 做法：把字符串按 <c>-</c> 切成片段，从第一段开始贪心 ——
    /// 先假设"下一段是连字符接上"，若拼出的前缀在磁盘上存在就接受，否则当作分隔符。
    /// 这样 <c>D-Agents~0020WorkSpace-DSH-Data</c> 会得到
    /// <c>D:\Agents WorkSpace\DSH-Data</c>（因为 <c>DSH-Data</c> 真实存在而 <c>DSH</c> 不存在）。
    /// </summary>
    private static string ResolveByDiskFacts(string unescaped)
    {
        string[] parts = unescaped.Split('-');
        if (parts.Length <= 1) return unescaped;

        // 首段是盘符（单字母）——先补上冒号，否则 "D" 在磁盘上永远查不到，
        // 后面每一段都会因为前缀不存在而走 "都不存在：按分隔符继续推" 的兜底，
        // 于是 DSH-Data 会被错拆成 DSH\Data（2026-10-27 实测踩到）。
        var result = new System.Text.StringBuilder(parts[0]);
        if (parts[0].Length == 1 && char.IsLetter(parts[0][0]))
            result.Append(':');

        for (int i = 1; i < parts.Length; i++)
        {
            string withHyphen = result + "-" + parts[i];   // 当连字符
            string withSep = result + "\\" + parts[i];     // 当分隔符

            bool hyphenExists = SafeExists(withHyphen);
            bool sepExists = SafeExists(withSep);

            if (hyphenExists && !sepExists) result.Clear().Append(withHyphen);
            else if (sepExists && !hyphenExists) result.Clear().Append(withSep);
            else if (hyphenExists && sepExists) result.Clear().Append(withHyphen);  // 都行时优先连字符（更贴原名）
            else result.Clear().Append(withSep);                                    // 都不存在：按分隔符继续推
        }

        return result.ToString();
    }

    private static bool SafeExists(string path)
    {
        try { return Directory.Exists(path) || File.Exists(path); }
        catch { return false; }
    }
    /// <summary>
    /// 归档功能已经取消，但老版本可能把会话搬进过 &lt;ConfigDir&gt;/Unified/archive。
    /// 启动扫描时把它们一次性搬回 DSH 的 sessions 根目录（撞名加 -restored-N），随后删掉空壳目录。
    /// 只记日志不弹窗：用户不需要知道这件事发生过，但数据不能丢。
    /// </summary>
    private static void MoveArchivedBackOnce()
    {
        string root = Path.Combine(GuardPaths.ConfigDir, "Unified", "archive");
        try
        {
            if (!Directory.Exists(root)) return;

            foreach (string proj in Directory.EnumerateDirectories(root))
            {
                string dest = Path.Combine(SessionsRoot, Path.GetFileName(proj));
                Directory.CreateDirectory(dest);
                foreach (string dir in Directory.EnumerateDirectories(proj))
                {
                    string name = Path.GetFileName(dir);
                    string target = Path.Combine(dest, name);
                    for (int i = 1; Directory.Exists(target) && i < 1000; i++)
                        target = Path.Combine(dest, name + "-restored-" + i);
                    Directory.Move(dir, target);
                }
                try { Directory.Delete(proj, true); } catch { }
            }
            try { Directory.Delete(root, true); } catch { }
            Logger.Log("DshResources：已把遗留的归档区会话搬回 sessions 根目录。");
        }
        catch (Exception ex)
        {
            Logger.LogError("DshSessionReader.MoveArchivedBackOnce", ex);
        }
    }

}
