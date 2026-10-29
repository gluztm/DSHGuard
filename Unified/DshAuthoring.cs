using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DSHGuard.Unified;

/// <summary>
/// 综合管理里「自己动手加东西」的那一半：新建提示词、新建技能、从 GitHub 导入技能。
///
/// 为什么单独一个文件：这三件事都要**写磁盘**，而且都有同一套底线——
/// 只在 <c>~/.dsh</c> 自己的目录里动手、不覆盖已有文件夹、名字过一遍 kebab 校验
/// （DSH 只认 kebab 名字的技能，名字不合规它就直接静默跳过）。
/// 把这些纪律集中在一处，界面上每张卡片就不用各写一遍。
/// </summary>
public static class DshAuthoring
{
    // ── 路径 ──────────────────────────────────────────────────────────

    /// <summary>自建提示词目录（和 DSH 自己的文件同处一地，用户好找）。</summary>
    public static string CustomPromptRoot => DshPromptReader.CustomPromptRoot;

    /// <summary>自建技能根目录：DSH 用户级技能目录。</summary>
    public static string CustomSkillRoot => DshSkillReader.UserAgentsSkillRoot;

    // ── 提示词：新建 / 删除 ───────────────────────────────────────────

    /// <summary>
    /// 新建一个自建提示词文件，返回写好的路径。失败抛 <see cref="IOException"/>（带人话说明）。
    /// </summary>
    public static string CreateCustomPrompt(string rawName, string? content = null)
    {
        string name = SanitizeFileName(rawName);
        if (name.Length == 0) throw new IOException("先给提示词起个名字。");

        string dir = CustomPromptRoot;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        string path = Path.Combine(dir, name + ".md");
        int n = 2;
        while (File.Exists(path))
        {
            path = Path.Combine(dir, name + "-" + n + ".md");
            n++;
            if (n > 999) throw new IOException("同名文件过多，请更换名称。");
        }

        string body = string.IsNullOrWhiteSpace(content)
            ? "（在此处填写要注入的正文。）"
            : content.Trim();

        string skeleton =
            "<!-- 自建提示词：" + name + "　由 DSH 守护壳创建，随时可以改。 -->\n" +
            "\n" +
            "## 用途\n" +
            "\n（这条提示词是干什么的：一句话说清。）\n" +
            "\n" +
            "## 内容\n" +
            "\n" + body + "\n";

        AtomicFile.WriteAllText(path, skeleton);
        return path;
    }

    /// <summary>删掉一个自建提示词。返回空串＝成功，非空＝给用户看的提示。</summary>
    public static string DeleteCustomPrompt(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return "路径是空的。";
            string full = Path.GetFullPath(path);
            string root = Path.GetFullPath(CustomPromptRoot);

            // 只允许删自己目录下的 .md：别的文件不归这里管
            if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                return "该文件不在自建提示词目录内，不予删除。";
            if (!string.Equals(Path.GetExtension(full), ".md", StringComparison.OrdinalIgnoreCase))
                return "只删 .md 提示词文件。";
            if (!File.Exists(full)) return "文件已经不在了。";

            File.Delete(full);
            return "";
        }
        catch (Exception ex)
        {
            Logger.LogError("DshAuthoring.DeleteCustomPrompt", ex);
            return "删不掉：" + ex.Message;
        }
    }

    // ── 技能：新建 / 删除 ────────────────────────────────────────────

    /// <summary>
    /// 新建一个技能：<c>~/.agents/skills/&lt;kebab&gt;/SKILL.md</c>。
    /// 返回 SKILL.md 路径。名字不合规直接抛（DSH 会静默跳过，不如当场说清楚）。
    /// </summary>
    public static string CreateSkill(string rawName, string description, string whenToUse)
    {
        string name = ToKebab(rawName);
        if (name.Length == 0) throw new IOException("技能名要用小写字母、数字和单个连字符，比如 my-skill。");

        string root = CustomSkillRoot;
        string dir = Path.Combine(root, name);
        if (Directory.Exists(dir)) throw new IOException("已经有一个叫「" + name + "」的技能了，换个名字。");

        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "SKILL.md");

        string desc = (description ?? "").Trim();
        if (desc.Length == 0) desc = "（一句话说明这个技能干什么，DSH 用它判断什么时候自动用。）";

        var sb = new StringBuilder();
        sb.Append("---\n");
        sb.Append("name: ").Append(name).Append('\n');
        sb.Append("description: ").Append(YamlScalar(desc)).Append('\n');
        string when = (whenToUse ?? "").Trim();
        if (when.Length > 0) sb.Append("when-to-use: ").Append(YamlScalar(when)).Append('\n');
        sb.Append("---\n\n");
        sb.Append("# ").Append(name).Append("\n\n");
        sb.Append("## 什么时候用\n\n").Append(when.Length > 0 ? when : "（写上触发场景，模型靠这段判断何时调用。）").Append("\n\n");
        sb.Append("## 怎么做\n\n1. （第一步）\n2. （第二步）\n");

        AtomicFile.WriteAllText(path, sb.ToString());
        return path;
    }

    /// <summary>删掉一个自建技能目录。只允许删技能根目录下的直属子目录。</summary>
    public static string DeleteSkill(DshSkill skill)
    {
        try
        {
            if (skill == null) return "技能不存在。";
            if (skill.IsPlugin) return "该技能由插件提供，请到「插件」页停用对应插件。";
            if (!skill.Writable) return "该技能目录为只读，无法删除。";

            string file = Path.GetFullPath(skill.FullPath);
            string dir = Path.GetDirectoryName(file) ?? "";
            if (dir.Length == 0) return "找不到技能目录。";

            string root = Path.GetFullPath(CustomSkillRoot).TrimEnd(Path.DirectorySeparatorChar);
            string parent = Path.GetFullPath(Path.GetDirectoryName(dir) ?? "").TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(parent, root, StringComparison.OrdinalIgnoreCase))
                return "该技能不在自建技能目录内，不予删除。";

            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            return "";
        }
        catch (Exception ex)
        {
            Logger.LogError("DshAuthoring.DeleteSkill", ex);
            return "删不掉：" + ex.Message;
        }
    }

    // ── 技能：从 GitHub 仓库导入 ────────────────────────────────────

    public sealed class ImportResult
    {
        public int Found { get; set; }
        public int Imported { get; set; }
        public int Skipped { get; set; }
        public List<string> Names { get; } = new();
        public List<string> SkippedNames { get; } = new();
        public string Note { get; set; } = "";

        public string Describe()
        {
            if (Note.Length > 0 && Imported == 0) return Note;

            var sb = new StringBuilder();
            sb.Append("仓库里找到 ").Append(Found).Append(" 个 SKILL.md，导入 ").Append(Imported).Append(" 个");
            if (Skipped > 0) sb.Append("，跳过 ").Append(Skipped).Append(" 个（同名已存在）");
            if (Names.Count > 0) sb.Append("：").Append(string.Join("、", Names));
            if (Note.Length > 0) sb.Append("。").Append(Note);
            return sb.ToString();
        }
    }

    /// <summary>
    /// 从 GitHub 仓库导入技能。<paramref name="spec"/> 接受
    /// <c>owner/repo</c>、<c>https://github.com/owner/repo</c>、
    /// <c>https://github.com/owner/repo/tree/分支</c>、<c>git@github.com:owner/repo.git</c>。
    ///
    /// 只读公开仓库的 <c>SKILL.md</c>（最多 40 个），落到自建技能目录，
    /// 每个技能一个文件夹，**不覆盖**已存在的同名文件夹。
    /// 全部走 https + 系统证书，不执行仓库里的任何代码。
    /// </summary>
    public static async Task<ImportResult> ImportFromGitHubAsync(string spec, CancellationToken ct)
    {
        var result = new ImportResult();
        (string owner, string repo, string branch) = ParseRepoSpec(spec);
        if (owner.Length == 0 || repo.Length == 0)
        {
            result.Note = "看不懂这个仓库地址。写成 owner/repo 或 https://github.com/owner/repo。";
            return result;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DSHGuard");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        try
        {
            // 1) 仓库信息：拿默认分支（用户没指定分支时用）
            string repoUrl = "https://api.github.com/repos/" + Uri.EscapeDataString(owner) + "/" + Uri.EscapeDataString(repo);
            string repoJson = await http.GetStringAsync(repoUrl, ct);
            using (JsonDocument doc = JsonDocument.Parse(repoJson))
            {
                if (branch.Length == 0 && doc.RootElement.TryGetProperty("default_branch", out var db))
                    branch = db.GetString() ?? "main";
            }
            if (branch.Length == 0) branch = "main";

            // 2) 文件树：找所有 SKILL.md
            string treeUrl = repoUrl + "/git/trees/" + Uri.EscapeDataString(branch) + "?recursive=1";
            string treeJson = await http.GetStringAsync(treeUrl, ct);

            var skillPaths = new List<string>();
            using (JsonDocument doc = JsonDocument.Parse(treeJson))
            {
                if (doc.RootElement.TryGetProperty("tree", out var tree) && tree.ValueKind == JsonValueKind.Array)
                {
                    foreach (var node in tree.EnumerateArray())
                    {
                        if (skillPaths.Count >= 40) break;
                        if (!node.TryGetProperty("type", out var type) || type.GetString() != "blob") continue;
                        if (!node.TryGetProperty("path", out var pathEl)) continue;
                        string p = pathEl.GetString() ?? "";
                        if (p.EndsWith("/SKILL.md", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(p, "SKILL.md", StringComparison.OrdinalIgnoreCase))
                            skillPaths.Add(p);
                    }
                }
            }

            result.Found = skillPaths.Count;
            if (skillPaths.Count == 0)
            {
                result.Note = "这个仓库里没有 SKILL.md（技能必须放在以 SKILL.md 命名的文件里）。";
                return result;
            }

            string root = CustomSkillRoot;
            if (!Directory.Exists(root)) Directory.CreateDirectory(root);

            foreach (string path in skillPaths)
            {
                ct.ThrowIfCancellationRequested();

                string raw = "https://raw.githubusercontent.com/" + owner + "/" + repo + "/" + branch + "/" + path;
                string body;
                try { body = await http.GetStringAsync(raw, ct); }
                catch (Exception ex)
                {
                    Logger.NoteDiagnosis("导入技能时下载失败：" + raw + " — " + ex.Message);
                    continue;
                }

                string parentDir = ParentSegment(path);
                string frontName = ReadFrontmatterValue(body, "name");
                if (frontName.Length == 0) frontName = parentDir;
                if (frontName.Length == 0) frontName = repo;
                string name = ToKebab(frontName);
                if (name.Length == 0) name = ToKebab(repo) + "-skill";

                string dir = Path.Combine(root, name);
                if (Directory.Exists(dir))
                {
                    result.Skipped++;
                    result.SkippedNames.Add(name);
                    continue;
                }

                string desc = ReadFrontmatterValue(body, "description");
                if (desc.Length == 0) desc = "从 GitHub 导入：" + owner + "/" + repo;

                try
                {
                    Directory.CreateDirectory(dir);
                    string provenance =
                        "<!-- 导入自 github.com/" + owner + "/" + repo + " · 分支 " + branch +
                        " · 文件 " + path + " · 由 DSH 守护壳导入，内容未改动 -->\n";
                    AtomicFile.WriteAllText(Path.Combine(dir, "SKILL.md"), EnsureFrontmatter(body, name, desc, provenance));
                    result.Imported++;
                    result.Names.Add(name);
                }
                catch (Exception ex)
                {
                    Logger.LogError("DshAuthoring.ImportFromGitHubAsync(write " + name + ")", ex);
                    try { if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0) Directory.Delete(dir); } catch { }
                }
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            result.Note = "已取消。";
            return result;
        }
        catch (Exception ex)
        {
            Logger.LogError("DshAuthoring.ImportFromGitHubAsync", ex);
            result.Note = "导入失败：" + ex.Message;
            return result;
        }
    }

    // ── 小工具 ────────────────────────────────────────────────────────

    /// <summary>解析仓库写法，返回 (owner, repo, branch)；分支可能是空串（用默认分支）。</summary>
    public static (string Owner, string Repo, string Branch) ParseRepoSpec(string spec)
    {
        string s = (spec ?? "").Trim();
        if (s.Length == 0) return ("", "", "");

        // git@github.com:owner/repo.git
        if (s.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
        {
            int colon = s.IndexOf(':');
            if (colon > 0) s = "https://" + s.Substring(4, colon - 4) + "/" + s.Substring(colon + 1);
        }

        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(s, UriKind.Absolute, out var uri)) return ("", "", "");
            s = uri.AbsolutePath.Trim('/');
            // 去掉 /tree/<branch> 尾巴
            int tree = s.IndexOf("/tree/", StringComparison.OrdinalIgnoreCase);
            string branchFromUrl = "";
            if (tree > 0)
            {
                branchFromUrl = s.Substring(tree + 6).Trim('/');
                s = s.Substring(0, tree);
            }
            s = s.Trim('/');
            if (!s.Contains('/')) return ("", "", "");
            string[] parts = s.Split('/');
            string owner = parts[0];
            string repo = parts[1];
            if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) repo = repo.Substring(0, repo.Length - 4);
            return (owner, repo, branchFromUrl);
        }

        s = s.Trim('/');
        if (s.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4);
        int slash = s.IndexOf('/');
        if (slash <= 0 || slash >= s.Length - 1) return ("", "", "");
        return (s.Substring(0, slash), s.Substring(slash + 1), "");
    }

    /// <summary>把任意名字压成 kebab-case（小写字母、数字、单个连字符，首尾不能是连字符）。</summary>
    public static string ToKebab(string raw)
    {
        string s = (raw ?? "").Trim();
        if (s.Length == 0) return "";

        var sb = new StringBuilder(s.Length);
        bool lastDash = false;
        foreach (char ch in s)
        {
            char c = char.ToLowerInvariant(ch);
            bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
            if (ok)
            {
                sb.Append(c);
                lastDash = false;
            }
            else if (!lastDash && sb.Length > 0)
            {
                sb.Append('-');
                lastDash = true;
            }
        }

        string outp = sb.ToString().Trim('-');
        if (outp.Length > 64) outp = outp.Substring(0, 64).Trim('-');
        return outp;
    }

    /// <summary>文件名安全化：去掉路径分隔符与 Windows 保留字符，限长 60。</summary>
    public static string SanitizeFileName(string raw)
    {
        string s = (raw ?? "").Trim();
        if (s.Length == 0) return "";

        foreach (char bad in Path.GetInvalidFileNameChars()) s = s.Replace(bad, '-');
        s = s.Replace('/', '-').Replace('\\', '-').Replace(':', '-').Replace('*', '-')
             .Replace('?', '-').Replace('"', '-').Replace('<', '-').Replace('>', '-').Replace('|', '-');
        s = s.Trim().Trim('.', '-', ' ');
        if (s.Length > 60) s = s.Substring(0, 60).Trim();
        return s;
    }

    private static string ParentSegment(string path)
    {
        string p = (path ?? "").Replace('\\', '/');
        string[] parts = p.Split('/');
        return parts.Length >= 2 ? parts[parts.Length - 2] : "";
    }

    /// <summary>读 frontmatter 里某个 key 的值（只认首个 <c>---</c> 到下一个 <c>---</c>）。</summary>
    public static string ReadFrontmatterValue(string text, string key)
    {
        if (string.IsNullOrEmpty(text)) return "";
        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (lines.Length < 3) return "";
        if (lines[0].Trim() != "---") return "";

        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Trim() == "---") break;
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            if (!string.Equals(line.Substring(0, colon).Trim(), key, StringComparison.OrdinalIgnoreCase)) continue;
            string v = line.Substring(colon + 1).Trim();
            if (v.Length >= 2 &&
                ((v[0] == '"' && v[v.Length - 1] == '"') || (v[0] == '\'' && v[v.Length - 1] == '\'')))
                v = v.Substring(1, v.Length - 2);
            return v.Trim();
        }
        return "";
    }

    /// <summary>
    /// 保证落盘的 SKILL.md 有合规 frontmatter：缺 name/description 就补上，
    /// 原有正文一个字不动（只在前面加/改 frontmatter，后面加一行来源注释）。
    /// </summary>
    private static string EnsureFrontmatter(string body, string name, string description, string provenance)
    {
        string text = body.Replace("\r\n", "\n");
        string[] lines = text.Split('\n');

        bool hasFront = lines.Length > 2 && lines[0].Trim() == "---";
        int close = -1;
        if (hasFront)
        {
            for (int i = 1; i < lines.Length; i++)
                if (lines[i].Trim() == "---") { close = i; break; }
        }

        if (hasFront && close > 0)
        {
            var head = new List<string>(lines.Length);
            head.Add("---");
            for (int i = 1; i < close; i++)
            {
                string line = lines[i];
                int colon = line.IndexOf(':');
                string k = colon > 0 ? line.Substring(0, colon).Trim() : "";
                if (colon > 0 && string.Equals(k, "name", StringComparison.OrdinalIgnoreCase))
                {
                    if (ReadFrontmatterValue(text, "name").Length > 0) { head.Add(line); continue; }
                    head.Add("name: " + name);
                    continue;
                }
                if (colon > 0 && string.Equals(k, "description", StringComparison.OrdinalIgnoreCase))
                {
                    if (ReadFrontmatterValue(text, "description").Length > 0) { head.Add(line); continue; }
                    head.Add("description: " + YamlScalar(description));
                    continue;
                }
                if (line.Trim().Length > 0) head.Add(line);
            }

            bool hasName = false, hasDesc = false;
            foreach (string h in head)
            {
                int c = h.IndexOf(':');
                string k = c > 0 ? h.Substring(0, c).Trim() : "";
                if (string.Equals(k, "name", StringComparison.OrdinalIgnoreCase)) hasName = true;
                if (string.Equals(k, "description", StringComparison.OrdinalIgnoreCase)) hasDesc = true;
            }
            if (!hasName) head.Insert(1, "name: " + name);
            if (!hasDesc) head.Add("description: " + YamlScalar(description));
            head.Add("---");

            string rest = string.Join("\n", lines, close + 1, lines.Length - close - 1).TrimStart('\n');
            return string.Join("\n", head) + "\n\n" + provenance + rest.TrimEnd('\n') + "\n";
        }

        // 没有 frontmatter：整篇当正文，前面补一段
        var sb = new StringBuilder();
        sb.Append("---\nname: ").Append(name).Append('\n');
        sb.Append("description: ").Append(YamlScalar(description)).Append('\n');
        sb.Append("---\n\n");
        sb.Append(provenance);
        sb.Append(text.TrimEnd('\n')).Append('\n');
        return sb.ToString();
    }

    /// <summary>YAML 标量：含特殊字符就加双引号并转义反斜杠/引号；顺手压成一行。</summary>
    private static string YamlScalar(string raw)
    {
        string s = (raw ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        if (s.Length == 0) return "\"\"";
        bool needQuote = s.IndexOf(':') >= 0 || s.IndexOf('#') >= 0 || s.IndexOf('"') >= 0 ||
                         s.IndexOf('\'') >= 0 || s.StartsWith("-") || s.StartsWith("[") || s.StartsWith("{");
        if (!needQuote) return s;
        return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}

