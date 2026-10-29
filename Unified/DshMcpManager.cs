using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DSHGuard.Unified;

/// <summary>
/// 一台 DSH MCP 服务器（cordis.patch.yml 里 name=@deepseek-ai/dsh-mcp-client 的一条条目）。
///
/// 形态（实测 profiles/desktop/cordis.patch.yml）：
/// <code>
/// - insert:
///     - id: unified-memory-mcp
///       name: '@deepseek-ai/dsh-mcp-client'
///       config:
///         serverName: unified-memory
///         transport: streamable-http
///         url: 'http://127.0.0.1:8765/mcp'
/// </code>
/// <see cref="StartLine"/>/<see cref="EndLine"/> 是 0 基行号（含两端），指向该条目
/// <c>- id:</c> 行到块内最后一个非空行 —— 写回时靠它定位，**不整份重排 YAML**。
/// </summary>
public sealed class DshMcpServer
{
    public string ProfileId { get; init; } = "";
    public string ProfileName { get; init; } = "";
    public string PatchFile { get; init; } = "";

    public string Id { get; init; } = "";
    public string ServerName { get; init; } = "";
    public string Transport { get; init; } = "";

    public string? Command { get; init; }
    public string? Url { get; init; }
    public IReadOnlyList<string> Args { get; init; } = Array.Empty<string>();
    public int ToolCallTimeoutMs { get; init; } = 60000;
    public bool Disabled { get; init; }

    /// <summary>条目 <c>- id:</c> 行号（0 基）。</summary>
    public int StartLine { get; init; }

    /// <summary>条目 <c>- id:</c> 行的缩进（同级的 name:/config:/disabled: 在 +2）。</summary>
    public int Indent { get; init; }

    /// <summary>被 <c>- insert:</c> 包裹时的父行号。</summary>
    public int ParentInsertLine { get; init; } = -1;

    /// <summary>该 insert 块里与它同级的条目数（=1 时删除必须连包裹行一起删）。</summary>
    public int SiblingCount { get; init; } = 1;

    /// <summary>解析期发现的问题（空 = 正常）。</summary>
    public string Problem { get; init; } = "";

    /// <summary>形如 <c>mcp__unified-memory__xxx</c> 里能对上的部分。</summary>
    public string ToolPrefix => "mcp__" + ServerName + "__";

    /// <summary>一行摘要：transport + 端点。</summary>
    public string Endpoint
    {
        get
        {
            if (string.Equals(Transport, "stdio", StringComparison.OrdinalIgnoreCase))
            {
                string cmd = Command ?? "";
                if (cmd.Length == 0) return "stdio · （缺 command）";
                string shortCmd = Path.GetFileName(cmd.Replace('/', '\\'));
                string tail = Args.Count > 0 ? " " + string.Join(" ", Args.Take(3)) : "";
                if (Args.Count > 3) tail += " …";
                return "stdio · " + shortCmd + tail;
            }
            return Transport + " · " + (Url ?? "（缺 url）");
        }
    }
}

/// <summary>
/// 新增 / 编辑一台 MCP 服务器时的草稿（界面上一个表单填出来的东西）。
/// </summary>
public sealed class DshMcpDraft
{
    public string ServerName = "";
    public string Transport = "stdio";
    public string Command = "";
    public List<string> Args = new();
    public string Url = "";
    public int ToolCallTimeoutMs = 60000;
}

/// <summary>
/// DSH MCP 服务器的读写。**只动 cordis.patch.yml 里 name=@deepseek-ai/dsh-mcp-client 的条目**，
/// 不碰其它插件条目、不整份重排 YAML（用户手写的注释与顺序都保留）。
///
/// 三条硬规则（沿用 TogglePluginAsync 的判据）：
///   1) 写之前必留 <c>.bak-mcp-&lt;时间戳&gt;</c>；
///   2) 写之后**重读磁盘**校验，不拿"我以为写成了"当事实；
///   3) 成败以重读结果为准，返回给界面的是可直接显示的中文句子。
/// </summary>
public static class DshMcpManager
{
    private const string PackageName = "@deepseek-ai/dsh-mcp-client";

    private static readonly Regex ServerNameRx = new("^[A-Za-z0-9_-]{1,32}$", RegexOptions.CultureInvariant);

    /// <summary>条目 id 的合法形状（回滚时用来校验原 id 还能不能直接写回去）。</summary>
    private static readonly Regex IdRx = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant);

    /// <summary>扫描所有存在的 profile，列出全部 MCP 服务器。绝不抛。</summary>
    public static List<DshMcpServer> Scan()
    {
        var list = new List<DshMcpServer>();
        foreach (var p in DshProfileRegistry.Existing())
        {
            string patch;
            try { patch = p.PatchFile(); }
            catch (Exception ex) { Logger.LogError("DshMcpManager.Scan.PatchFile", ex); continue; }
            if (!File.Exists(patch)) continue;
            try { list.AddRange(ScanFile(p, patch)); }
            catch (Exception ex) { Logger.LogError("DshMcpManager.ScanFile", ex); }
        }
        return list;
    }

    /// <summary>解析单个 patch 文件里的 MCP 条目。抽出来是为了自检能喂夹具（不碰真实配置）。</summary>
    public static List<DshMcpServer> ScanFile(DshProfileInfo profile, string patchPath)
        => ScanText(profile.Id, profile.DisplayName, patchPath, File.ReadAllText(patchPath));

    /// <summary>纯文本解析（自检入口）。</summary>
    public static List<DshMcpServer> ScanText(string profileId, string profileName, string patchPath, string text)
    {
        var result = new List<DshMcpServer>();
        var lines = SplitLines(text);
        if (lines.Count == 0) return result;

        for (int i = 0; i < lines.Count; i++)
        {
            string t = lines[i].TrimStart();
            if (!t.StartsWith("- id:", StringComparison.Ordinal)) continue;

            int indent = lines[i].Length - t.Length;
            int end = EntryEnd(lines, i, indent);
            if (end < i) continue;

            // 只认 name 指向 dsh-mcp-client 的条目
            string name = ValueOf(lines, i, end, indent + 2, "name") ?? "";
            if (name.IndexOf("dsh-mcp-client", StringComparison.OrdinalIgnoreCase) < 0) continue;

            int parent = FindParentInsert(lines, i, indent);
            int siblings = parent >= 0 ? CountSiblings(lines, parent, indent) : 1;

            string serverName = ValueOf(lines, i, end, indent + 4, "serverName")
                             ?? ValueOf(lines, i, end, indent + 2, "serverName")
                             ?? "";
            string transport = ValueOf(lines, i, end, indent + 4, "transport")
                            ?? ValueOf(lines, i, end, indent + 2, "transport")
                            ?? "";
            string? command = ValueOf(lines, i, end, indent + 4, "command");
            string? url = ValueOf(lines, i, end, indent + 4, "url");
            var args = ListUnder(lines, i, end, indent + 4, "args");

            string problem = "";
            if (serverName.Length == 0) problem = "缺 serverName（引擎加载时会拒）";
            else if (!ServerNameRx.IsMatch(serverName)) problem = "serverName 不合规（只许 [A-Za-z0-9_-]，≤32 字符）";
            else if (string.Equals(transport, "stdio", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(command))
                problem = "stdio 但缺 command";
            else if (string.Equals(transport, "streamable-http", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(url))
                problem = "streamable-http 但缺 url";

            string idValue = (t.Substring("- id:".Length).Trim()).Trim('\'', '"');

            result.Add(new DshMcpServer
            {
                ProfileId = profileId,
                ProfileName = profileName,
                PatchFile = patchPath,
                Id = idValue,
                ServerName = serverName,
                Transport = transport,
                Command = command,
                Url = url,
                Args = args,
                ToolCallTimeoutMs = ParseInt(ValueOf(lines, i, end, indent + 4, "toolCallTimeoutMs"), 60000),
                Disabled = IsDisabled(lines, i, end, indent + 2),
                StartLine = i,
                Indent = indent,
                ParentInsertLine = parent,
                SiblingCount = siblings,
                Problem = problem,
            });
        }
        return result;
    }

    // ── 写操作 ────────────────────────────────────────────────────────

    /// <summary>启用 / 禁用一台服务器（写/删条目里的 <c>disabled: true</c>）。</summary>
    public static string SetEnabled(DshMcpServer target, bool enable)
    {
        try
        {
            string patch = target.PatchFile;
            if (!File.Exists(patch)) return "配置文件不存在：" + patch;

            var (lines, crlf) = ReadLines(patch);
            var fresh = ScanText(target.ProfileId, target.ProfileName, patch,
                                 string.Join(crlf ? "\r\n" : "\n", lines));

            var cur = fresh.FirstOrDefault(x =>
                string.Equals(x.Id, target.Id, StringComparison.Ordinal) &&
                string.Equals(x.ServerName, target.ServerName, StringComparison.Ordinal));
            if (cur == null) return "配置文件里已找不到该条目（可能刚被别的程序改过），请点「刷新」。";
            // 目标状态是「Disabled == !enable」。相等才是「已经是目标值」，
            // 原先写成 != 是反的：想停用时 cur.Disabled(false) != !enable(true) 成立，
            // 于是直接返回「状态已是目标值」——「停用」按钮点了不动。
            if (cur.Disabled == !enable) return "状态已是目标值，未改动。";

            if (enable)
            {
                int at = FindDisabledLine(lines, cur);
                if (at < 0) return "状态已是启用，未改动。";
                lines.RemoveAt(at);
            }
            else
            {
                int at = FindDisabledLine(lines, cur);
                string pad = new(' ', cur.Indent + 2);
                if (at >= 0) lines[at] = pad + "disabled: true";
                else
                {
                    // 插在该条目最后一行之后（与 name: / config: 同级、在 config 块之下）。
                    // 紧贴 `- id:` 插在 name: 之前引擎也认，但会打乱原有可读顺序，
                    // 与真实文件里已有的写法不一致。
                    int end = Math.Max(cur.StartLine, EntryEnd(lines, cur.StartLine, cur.Indent));
                    lines.Insert(end + 1, pad + "disabled: true");
                }
            }

            string backup = Backup(patch, "mcp");
            WriteAtomic(patch, string.Join(crlf ? "\r\n" : "\n", lines));

            // 重读校验：不拿"我以为写成了"当事实
            var after = ScanFile(DshProfileRegistry.ById(target.ProfileId) ?? DshProfileRegistry.Get(DshProfileKind.Desktop), patch)
                        .FirstOrDefault(x => string.Equals(x.Id, target.Id, StringComparison.Ordinal));
            if (after == null) return $"写入后重读找不到该条目 —— 配置可能已损坏，已备份 {Path.GetFileName(backup)}，请检查。";
            if (after.Disabled == !enable)
                return enable
                    ? $"{target.ServerName} @ {target.ProfileName}：已启用（重启 DSH 后生效）"
                    : $"{target.ServerName} @ {target.ProfileName}：已禁用（重启 DSH 后生效）";
            return $"写入后重读状态不符（期望 {(enable ? "启用" : "禁用")}），已备份 {Path.GetFileName(backup)}。";
        }
        catch (Exception ex)
        {
            Logger.LogError("DshMcpManager.SetEnabled", ex);
            return "操作失败：" + ex.Message;
        }
    }

    /// <summary>删除一台服务器。独占 insert 块时连包裹行与头顶注释一起删，避免留下空块。</summary>
    public static string Remove(DshMcpServer target)
    {
        try
        {
            string patch = target.PatchFile;
            if (!File.Exists(patch)) return "配置文件不存在：" + patch;

            var (lines, crlf) = ReadLines(patch);
            string joined = string.Join(crlf ? "\r\n" : "\n", lines);
            var fresh = ScanText(target.ProfileId, target.ProfileName, patch, joined);
            var cur = fresh.FirstOrDefault(x =>
                string.Equals(x.Id, target.Id, StringComparison.Ordinal) &&
                string.Equals(x.ServerName, target.ServerName, StringComparison.Ordinal));
            if (cur == null) return "配置文件里已找不到该条目，请点「刷新」。";

            int from = cur.StartLine;
            int to = EntryEnd(lines, from, cur.Indent);

            if (cur.ParentInsertLine >= 0 && cur.SiblingCount <= 1)
            {
                // 这个 insert 块只装它一个：连包裹行一起删，否则留下 `- insert:` 空块 → YAML 坏
                from = cur.ParentInsertLine;
                // 头顶紧邻的注释是写给这条用的，一并带走（只吃同缩进的连续 # 行）
                int pIn = IndentOf(lines[from]);
                while (from - 1 >= 0)
                {
                    string prev = lines[from - 1];
                    string pt = prev.TrimStart();
                    if (pt.Length == 0) break;
                    if (!pt.StartsWith("#", StringComparison.Ordinal)) break;
                    if (IndentOf(prev) != pIn) break;
                    from--;
                }
            }

            // 吃掉块尾的空白行，别留下连续空行
            while (to + 1 < lines.Count && lines[to + 1].Trim().Length == 0) to++;

            int count = to - from + 1;
            if (count <= 0) return "定位失败，未改动。";
            lines.RemoveRange(from, count);

            string backup = Backup(patch, "mcp");
            WriteAtomic(patch, string.Join(crlf ? "\r\n" : "\n", lines));

            var after = ScanFile(DshProfileRegistry.ById(target.ProfileId) ?? DshProfileRegistry.Get(DshProfileKind.Desktop), patch);
            if (after.Any(x => string.Equals(x.ServerName, cur.ServerName, StringComparison.Ordinal)))
                return $"删除后重读仍存在 {cur.ServerName}，已备份 {Path.GetFileName(backup)}。";
            return $"{cur.ServerName} @ {cur.ProfileName}：已删除（备份 {Path.GetFileName(backup)}，重启 DSH 后生效）";
        }
        catch (Exception ex)
        {
            Logger.LogError("DshMcpManager.Remove", ex);
            return "删除失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 新增一台服务器（写成一个 <c>- insert:</c> 块，插在既有 MCP 条目之后）。
    /// </summary>
    /// <param name="idOverride">
    /// 指定条目 id。**回滚时必须传原 id**：本机真实 id 形如 <c>unified-memory-mcp</c>、
    /// <c>threejs-mcp</c>，并不是本方法默认生成的 <c>mcp-&lt;name&gt;</c> 形状；
    /// 若不指定，回滚会把 id 换掉，DSH 侧按 id 引用的地方就对不上了。
    /// </param>
    public static string Add(DshProfileInfo profile, DshMcpDraft draft, string? idOverride = null)
    {
        try
        {
            string name = (draft.ServerName ?? "").Trim();
            if (!ServerNameRx.IsMatch(name))
                return "serverName 不合规：只允许字母 / 数字 / 下划线 / 连字符，长度 1–32。";

            string transport = (draft.Transport ?? "stdio").Trim();
            bool isStdio = string.Equals(transport, "stdio", StringComparison.OrdinalIgnoreCase);
            bool isHttp = string.Equals(transport, "streamable-http", StringComparison.OrdinalIgnoreCase);
            if (!isStdio && !isHttp) return "transport 只支持 stdio 或 streamable-http。";
            if (isStdio && string.IsNullOrWhiteSpace(draft.Command)) return "stdio 必须填 command。";
            if (isHttp && string.IsNullOrWhiteSpace(draft.Url)) return "streamable-http 必须填 url。";

            string patch = profile.PatchFile();
            var existing = File.Exists(patch) ? ScanFile(profile, patch) : new List<DshMcpServer>();
            if (existing.Any(x => string.Equals(x.ServerName, name, StringComparison.OrdinalIgnoreCase)))
                return $"该 profile 里已有同名 serverName「{name}」—— 引擎加载时会以明确错误拒绝重复项。";

            // id：指定就先尊重指定值（回滚路径）；没指定才按默认规则生成并去重。
            string id;
            string? want = idOverride?.Trim();
            if (!string.IsNullOrEmpty(want))
            {
                if (!IdRx.IsMatch(want!))
                    return $"回滚失败：原 id「{want}」不是合法的条目 id（只允许字母 / 数字 / 下划线 / 连字符）。";
                if (existing.Any(x => string.Equals(x.Id, want, StringComparison.Ordinal)))
                    return $"回滚失败：id「{want}」已被同 profile 里的另一条目占用，请手工检查配置文件。";
                id = want!;
            }
            else
            {
                string baseId = "mcp-" + Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9_-]", "-");
                id = baseId;
                for (int n = 2; existing.Any(x => string.Equals(x.Id, id, StringComparison.Ordinal)) && n < 100; n++)
                    id = baseId + "-" + n;
            }

            var sb = new StringBuilder();
            sb.Append("# MCP 服务器（DSHGuard 综合管理添加 · ")
              .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("）\n");
            sb.Append("- insert:\n");
            sb.Append("    - id: ").Append(YamlScalar(id)).Append('\n');
            sb.Append("      name: '").Append(PackageName).Append("'\n");
            sb.Append("      config:\n");
            sb.Append("        serverName: ").Append(YamlScalar(name)).Append('\n');
            sb.Append("        transport: ").Append(transport).Append('\n');
            if (isStdio)
            {
                sb.Append("        command: '").Append((draft.Command ?? "").Replace("'", "''")).Append("'\n");
                var args = (draft.Args ?? new List<string>())
                    .Select(a => (a ?? "").Trim()).Where(a => a.Length > 0).ToList();
                if (args.Count > 0)
                {
                    sb.Append("        args:\n");
                    foreach (var a in args)
                        sb.Append("          - '").Append(a.Replace("'", "''")).Append("'\n");
                }
            }
            else
            {
                sb.Append("        url: '").Append((draft.Url ?? "").Trim().Replace("'", "''")).Append("'\n");
            }
            int tmo = draft.ToolCallTimeoutMs > 0 ? draft.ToolCallTimeoutMs : 60000;
            sb.Append("        toolCallTimeoutMs: ").Append(tmo).Append('\n');
            sb.Append("        failOnStartupError: false\n");

            var (lines, crlf) = File.Exists(patch) ? ReadLines(patch) : (new List<string>(), false);
            string nl = crlf ? "\r\n" : "\n";
            string block = sb.ToString().TrimEnd('\n');
            var chunk = new List<string> { "" };
            chunk.AddRange(block.Split('\n'));

            if (existing.Count > 0)
            {
                // 跟着既有 MCP 分组走：插在最后一个 MCP 条目所在 insert 块的后面。
                // 盲目堆到文件末尾会脱离既有归属注释，也可能落到别的插件条目后面；
                // 顺带也不能为了插一条就把文件原有的尾部空行吃掉。
                var last = existing.OrderBy(x => x.StartLine).Last();
                int anchor = last.ParentInsertLine >= 0 ? last.ParentInsertLine : last.StartLine;
                int end = Math.Max(anchor, EntryEnd(lines, anchor, IndentOf(lines[anchor])));
                lines.InsertRange(Math.Min(end + 1, lines.Count), chunk);
            }
            else
            {
                // 文件里还没有 MCP 条目：正常追加到末尾（与上一块之间空一行，保持可读）
                while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
                if (lines.Count > 0) lines.Add("");
                lines.AddRange(block.Split('\n'));
            }

            string backup = File.Exists(patch) ? Backup(patch, "mcp") : "";
            WriteAtomic(patch, string.Join(nl, lines) + nl);

            var after = ScanFile(profile, patch);
            var added = after.FirstOrDefault(x => string.Equals(x.ServerName, name, StringComparison.Ordinal));
            if (added == null) return "写入后重读没有解析到新条目 —— 请检查配置文件，已留备份。";
            if (added.Problem.Length > 0) return $"已写入但解析告警：{added.Problem}";
            return $"已添加 {name} @ {profile.DisplayName}（{(backup.Length > 0 ? "备份 " + Path.GetFileName(backup) + "，" : "")}重启 DSH 后生效）";
        }
        catch (Exception ex)
        {
            Logger.LogError("DshMcpManager.Add", ex);
            return "添加失败：" + ex.Message;
        }
    }

    /// <summary><c>config:</c> 块里界面认识的子键（其余键一律原样保留）。</summary>
    private static readonly HashSet<string> KnownKeys = new(StringComparer.Ordinal)
    { "serverName", "transport", "command", "args", "url", "toolCallTimeoutMs" };

    // ── 原地修改（serverName 不变时保住用户的注释与顺序） ─────────────────

    /// <summary>
    /// 原地改一台服务器的字段。
    ///
    /// 与 <see cref="Add"/> 的分工：serverName 没变时走这条路 —— 只重写 <c>config:</c>
    /// 块里那几个已知子键，用户的注释、字段顺序、以及 <c>failOnStartupError</c>
    /// 之类界面不认识的键全部原样保留。为了改一个参数就把整块换成 DSHGuard
    /// 自己的模板，代价与收益完全不成比例。
    ///
    /// 返回值沿用本类约定：**空串 = 成功，非空 = 给用户看的失败原因**。
    /// </summary>
    public static string Update(DshProfileInfo profile, DshMcpServer cur, DshMcpDraft draft)
    {
        try
        {
            string name = (draft.ServerName ?? "").Trim();
            if (!ServerNameRx.IsMatch(name))
                return "serverName 不合规：只允许字母 / 数字 / 下划线 / 连字符，长度 1–32。";

            string transport = (draft.Transport ?? "stdio").Trim();
            bool isStdio = string.Equals(transport, "stdio", StringComparison.OrdinalIgnoreCase);
            if (!isStdio && !string.Equals(transport, "streamable-http", StringComparison.OrdinalIgnoreCase))
                return "transport 只支持 stdio 或 streamable-http。";
            if (isStdio && string.IsNullOrWhiteSpace(draft.Command)) return "stdio 必须填 command。";
            if (!isStdio && string.IsNullOrWhiteSpace(draft.Url)) return "streamable-http 必须填 url。";

            string patch = profile.PatchFile();
            if (!File.Exists(patch)) return "配置文件不存在：" + patch;

            var (lines, crlf) = ReadLines(patch);
            if (cur.StartLine < 0 || cur.StartLine >= lines.Count)
                return "没能在文件里定位到这条条目，请重新打开本页再试。";
            int entryEnd = EntryEnd(lines, cur.StartLine, cur.Indent);

            int keyIndent = cur.Indent + 4;
            string keyPad = new(' ', keyIndent);
            int cfgLine = -1;
            for (int i = cur.StartLine; i <= entryEnd; i++)
            {
                string t = lines[i].Trim();
                if (t.Length == 0) continue;
                if (IndentOf(lines[i]) == cur.Indent + 2 && t.StartsWith("config:", StringComparison.Ordinal))
                { cfgLine = i; break; }
            }
            if (cfgLine < 0)
                return "这条条目里没有可识别的 config: 块，请先删除后重新添加。";

            int childEnd = cfgLine;
            for (int i = cfgLine + 1; i <= entryEnd; i++)
            {
                string t = lines[i].Trim();
                if (t.Length == 0) continue;
                if (IndentOf(lines[i]) <= cur.Indent + 2) break;
                childEnd = i;
            }

            var args = (draft.Args ?? new List<string>())
                .Select(a => (a ?? "").Trim()).Where(a => a.Length > 0).ToList();
            string tmo = (draft.ToolCallTimeoutMs > 0 ? draft.ToolCallTimeoutMs : 60000).ToString();

            var wanted = new List<(string Key, string Render)>
            {
                ("serverName", "serverName: " + YamlScalar(name)),
                ("transport", "transport: " + transport),
            };
            if (isStdio)
            {
                wanted.Add(("command", "command: '" + (draft.Command ?? "").Trim().Replace("'", "''") + "'"));
                if (args.Count > 0) wanted.Add(("args", "args"));   // "args" 是标记，按列表渲染
            }
            else
            {
                wanted.Add(("url", "url: '" + (draft.Url ?? "").Trim().Replace("'", "''") + "'"));
            }
            wanted.Add(("toolCallTimeoutMs", "toolCallTimeoutMs: " + tmo));

            var render = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (k, r) in wanted) render[k] = r;

            var outp = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int at = cfgLine + 1;
            while (at <= childEnd)
            {
                string raw = lines[at];
                string t = raw.Trim();
                if (t.Length == 0 || IndentOf(raw) != keyIndent)
                { outp.Add(raw); at++; continue; }

                int colon = t.IndexOf(':');
                string key = colon > 0 ? t.Substring(0, colon).Trim() : t.TrimEnd(':');

                int subEnd = at;
                for (int j = at + 1; j <= childEnd; j++)
                {
                    if (lines[j].Trim().Length == 0) continue;
                    if (IndentOf(lines[j]) <= keyIndent) break;
                    subEnd = j;
                }

                if (render.TryGetValue(key, out var r2))
                {
                    AppendKey(outp, keyPad, key, r2, args);
                    seen.Add(key);
                }
                else if (!KnownKeys.Contains(key))
                {
                    // 界面不认识的键（failOnStartupError / headers …）：整块原样保留
                    for (int j = at; j <= subEnd; j++) outp.Add(lines[j]);
                }
                // 认识但新形态下不再适用（stdio ↔ http 切换）：连同子块一起去掉

                at = subEnd + 1;
            }
            foreach (var (k, r) in wanted)
            {
                if (seen.Contains(k)) continue;
                AppendKey(outp, keyPad, k, r, args);
            }

            lines.RemoveRange(cfgLine + 1, Math.Max(0, childEnd - cfgLine));
            lines.InsertRange(cfgLine + 1, outp);

            Backup(patch, "mcp");
            WriteAtomic(patch, string.Join(crlf ? "\r\n" : "\n", lines) + (crlf ? "\r\n" : "\n"));

            var after = ScanFile(profile, patch);
            var now = after.FirstOrDefault(x => string.Equals(x.ServerName, name, StringComparison.Ordinal)
                && string.Equals(x.ProfileId, profile.Id, StringComparison.OrdinalIgnoreCase));
            if (now == null) return "写入后重读没有解析到这条条目 —— 已留备份，请检查配置文件。";
            if (now.Problem.Length > 0) return $"已写入但解析告警：{now.Problem}";
            return "";
        }
        catch (Exception ex)
        {
            Logger.LogError("DshMcpManager.Update", ex);
            return "更新失败：" + ex.Message;
        }
    }

    private static void AppendKey(List<string> outp, string keyPad, string key, string render, List<string> args)
    {
        if (render == "args")
        {
            outp.Add(keyPad + "args:");
            foreach (var a in args) outp.Add(keyPad + "  - '" + a.Replace("'", "''") + "'");
        }
        else outp.Add(keyPad + render);
    }

    // ── 连通性探测（明确说明是「端点/进程可达」，不是协议级连通） ──────────

    /// <summary>
    /// 探测一台服务器：
    ///   · streamable-http → 真发一次 JSON-RPC <c>initialize</c>，报 HTTP 状态码；
    ///   · stdio → 起子进程，3 秒内没退出即视为可执行，随后杀掉。
    /// 返回一句话，**如实写清探到了什么、没探到什么**。
    /// </summary>
    public static string Probe(DshMcpServer s, int timeoutMs = 5000)
    {
        try
        {
            if (string.Equals(s.Transport, "streamable-http", StringComparison.OrdinalIgnoreCase))
                return ProbeHttp(s, timeoutMs);
            if (string.Equals(s.Transport, "stdio", StringComparison.OrdinalIgnoreCase))
                return ProbeStdio(s, Math.Min(timeoutMs, 4000));
            return $"未知 transport「{s.Transport}」，未探测。";
        }
        catch (Exception ex)
        {
            Logger.LogError("DshMcpManager.Probe", ex);
            return "探测失败：" + ex.Message;
        }
    }

    private static string ProbeHttp(DshMcpServer s, int timeoutMs)
    {
        if (string.IsNullOrWhiteSpace(s.Url)) return "缺 url，无法探测。";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
            const string body =
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{" +
                "\"protocolVersion\":\"2024-11-05\",\"capabilities\":{}," +
                "\"clientInfo\":{\"name\":\"DSHGuard\",\"version\":\"2.3.0\"}}}";
            using var req = new HttpRequestMessage(HttpMethod.Post, s.Url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");

            using var resp = http.Send(req);
            int code = (int)resp.StatusCode;
            string text = "";
            try { text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult() ?? ""; } catch { }
            string head = text.Length > 160 ? text.Substring(0, 160) + "…" : text;

            if (code >= 200 && code < 300)
                return $"可达：HTTP {code}，initialize 有响应（{Shorten(head, 120)}）";
            if (code == 401 || code == 403)
                return $"端点可达但被拒：HTTP {code}（可能需要鉴权头）";
            if (code == 405)
                return "端点可达但拒绝 POST（HTTP 405）—— 该地址可能不是 MCP 端点";
            return $"端点返回 HTTP {code}：{Shorten(head, 120)}";
        }
        catch (TaskCanceledException) { return $"超时（>{timeoutMs}ms）：{s.Url}"; }
        catch (HttpRequestException ex) { return "连不上：" + Shorten(ex.Message, 160); }
    }

    private static string ProbeStdio(DshMcpServer s, int timeoutMs)
    {
        if (string.IsNullOrWhiteSpace(s.Command)) return "缺 command，无法探测。";
        try
        {
            var psi = new ProcessStartInfo(s.Command)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in s.Args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p == null) return "进程没能启动。";

            bool exited = p.WaitForExit(timeoutMs);
            if (!exited)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return $"可执行：进程 {timeoutMs / 1000} 秒内未退出（已结束探测进程）—— 只证明 command/args 能起来，未做 MCP 握手";
            }

            string err = "";
            try { err = p.StandardError.ReadToEnd() ?? ""; } catch { }
            string line = err.Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? "";
            return $"进程立即退出（exit {p.ExitCode}）" + (line.Length > 0 ? "：" + Shorten(line.Trim(), 160) : "，无 stderr 输出");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return "command 无法执行（找不到或不可执行）：" + Shorten(ex.Message, 160);
        }
    }

    // ── 解析辅助 ──────────────────────────────────────────────────────

    private static List<string> SplitLines(string text)
    {
        if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1);
        var raw = text.Replace("\r\n", "\n").Split('\n').ToList();
        if (raw.Count > 0 && raw[^1].Length == 0) raw.RemoveAt(raw.Count - 1);
        return raw;
    }

    private static (List<string> Lines, bool Crlf) ReadLines(string path)
    {
        string text = File.ReadAllText(path);
        if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1);
        bool crlf = text.Contains("\r\n", StringComparison.Ordinal);
        return (SplitLines(text), crlf);
    }

    private static int IndentOf(string line)
    {
        int n = 0;
        while (n < line.Length && (line[n] == ' ' || line[n] == '\t')) n++;
        return n;
    }

    /// <summary>条目块的最后一行（含）；找不到时返回 <paramref name="start"/>。</summary>
    private static int EntryEnd(List<string> lines, int start, int indent)
    {
        int last = start;
        for (int j = start + 1; j < lines.Count; j++)
        {
            string t = lines[j].TrimStart();
            if (t.Length == 0) continue;
            int ind = lines[j].Length - t.Length;
            if (ind < indent) break;                                   // 回到父层
            if (ind == indent && (t.StartsWith("- ", StringComparison.Ordinal) || t == "-")) break;  // 同级新条目
            last = j;
        }
        return last;
    }

    private static string? ValueOf(List<string> lines, int start, int end, int indent, string key)
    {
        string prefix = key + ":";
        for (int j = start; j <= end && j < lines.Count; j++)
        {
            string line = lines[j];
            if (IndentOf(line) != indent) continue;
            string t = line.TrimStart();
            if (!t.StartsWith(prefix, StringComparison.Ordinal)) continue;
            string v = t.Substring(prefix.Length).Trim();
            if (v.Length == 0) return null;
            return Unquote(v);
        }
        return null;
    }

    private static List<string> ListUnder(List<string> lines, int start, int end, int indent, string key)
    {
        var items = new List<string>();
        string prefix = key + ":";
        int found = -1;
        for (int j = start; j <= end && j < lines.Count; j++)
        {
            if (IndentOf(lines[j]) == indent && lines[j].TrimStart().StartsWith(prefix, StringComparison.Ordinal))
            { found = j; break; }
        }
        if (found < 0) return items;
        for (int j = found + 1; j <= end && j < lines.Count; j++)
        {
            string t = lines[j].TrimStart();
            if (t.Length == 0) continue;
            int ind = IndentOf(lines[j]);
            if (ind <= indent) break;
            if (!t.StartsWith("- ", StringComparison.Ordinal)) continue;
            items.Add(Unquote(t.Substring(2).Trim()));
        }
        return items;
    }

    private static bool IsDisabled(List<string> lines, int start, int end, int indent)
    {
        string? v = ValueOf(lines, start, end, indent, "disabled");
        return v != null && string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static int FindDisabledLine(List<string> lines, DshMcpServer s)
    {
        for (int j = s.StartLine; j <= EntryEnd(lines, s.StartLine, s.Indent) && j < lines.Count; j++)
        {
            if (IndentOf(lines[j]) != s.Indent + 2) continue;
            if (lines[j].TrimStart().StartsWith("disabled:", StringComparison.Ordinal)) return j;
        }
        return -1;
    }

    private static int FindParentInsert(List<string> lines, int start, int indent)
    {
        for (int j = start - 1; j >= 0; j--)
        {
            string t = lines[j].TrimStart();
            if (t.Length == 0) continue;
            int ind = lines[j].Length - t.Length;
            if (ind >= indent) continue;
            return t.StartsWith("- insert:", StringComparison.Ordinal) ? j : -1;
        }
        return -1;
    }

    private static int CountSiblings(List<string> lines, int insertLine, int childIndent)
    {
        int n = 0;
        int parentIndent = IndentOf(lines[insertLine]);
        for (int j = insertLine + 1; j < lines.Count; j++)
        {
            string t = lines[j].TrimStart();
            if (t.Length == 0) continue;
            int ind = lines[j].Length - t.Length;
            if (ind <= parentIndent) break;
            if (ind == childIndent && t.StartsWith("- ", StringComparison.Ordinal)) n++;
        }
        return Math.Max(n, 1);
    }

    private static string Unquote(string v)
    {
        if (v.Length >= 2)
        {
            if (v[0] == '\'' && v[^1] == '\'') return v.Substring(1, v.Length - 2).Replace("''", "'");
            if (v[0] == '"' && v[^1] == '"') return v.Substring(1, v.Length - 2).Replace("\\\"", "\"");
        }
        return v;
    }

    private static string YamlScalar(string v)
    {
        if (v.Length == 0) return "''";
        char c = v[0];
        bool needsQuote = "-?:,[]{}#&*!|>'\"%@` ".IndexOf(c) >= 0 || v.Contains(": ", StringComparison.Ordinal);
        return needsQuote ? "'" + v.Replace("'", "''") + "'" : v;
    }

    private static int ParseInt(string? v, int fallback)
        => int.TryParse(v, out int n) && n > 0 ? n : fallback;

    private static string Shorten(string s, int max)
    {
        s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length <= max ? s : s.Substring(0, max) + "…";
    }

    // ── 磁盘辅助 ──────────────────────────────────────────────────────

    private static string Backup(string path, string tag)
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string bak = path + ".bak-" + tag + "-" + stamp;
        int n = 2;
        while (File.Exists(bak)) bak = path + ".bak-" + tag + "-" + stamp + "-" + n++;
        try { File.Copy(path, bak, overwrite: false); } catch (Exception ex) { Logger.LogError("DshMcpManager.Backup", ex); }
        return bak;
    }

    private static void WriteAtomic(string path, string text) => AtomicFile.WriteAllText(path, text, keepBackup: false);
}
