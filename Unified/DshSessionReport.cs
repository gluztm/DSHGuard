using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace DSHGuard.Unified;

/// <summary>
/// 会话的**只读报告**：把一条会话写成一份人看得懂的 Markdown。
///
/// 为什么不是「把 jsonl 转成 md」：本程序零第三方依赖，运行时装里没有 zstd 解码器，
/// 原始 <c>session.*.zstd</c> 那 40+ MB 的内容它根本读不出来（硬上就得引一个包，
/// 把「零依赖」这条判定打破）。所以这份报告只写**程序真的拿得到的事实**：
///   · DSH 自己写在会话缓存里的每轮大纲（<c>turnOutline</c>：用户原话 + 助手回复摘要）、
///     标题、轮数/步骤/耗时/token/花费/上下文压力、权限与模型；
///   · 磁盘上的文件清单与 SHA-256；
///   · 以及一段**实测可用**的解压脚本（<see cref="DecoderScriptName"/>），
///     想看对话原文的人跑一行命令就拿到，不用信程序假装转码。
///
/// 报告里每个小节都单独 try/catch：缓存里少一块、格式变一点，只丢那一节，不影响其余。
/// </summary>
internal static class DshSessionReport
{
    /// <summary>随报告一起落地的解压脚本文件名。</summary>
    public const string DecoderScriptName = "解开日志.js";

    /// <summary>Markdown 报告文件名（用真标题，用户一眼认得）。</summary>
    public static string ReportFileName(DshSession s) =>
        DshSessionManager.SafeNameForFile(s.DisplayName, 48) + "-会话报告.md";

    // ══════════════════════════════ 主入口 ══════════════════════════════

    /// <summary>
    /// 生成报告全文。调用方负责写盘。任何一段失败都以「（这一节读不出来：原因）」占位，不抛。
    /// </summary>
    public static string BuildMarkdown(
        DshSession s,
        string dest,
        IReadOnlyList<(string Name, long Bytes, string Sha256)> files)
    {
        var sb = new StringBuilder(64 * 1024);
        JsonElement? cache = TryReadSessionCache(s.SessionId);

        AppendHeader(sb, s, cache);
        AppendStats(sb, s, cache);
        AppendTurns(sb, cache);
        AppendGoal(sb, cache);
        AppendFiles(sb, s, dest, files);
        AppendHowToDecode(sb, s);

        return sb.ToString();
    }

    /// <summary>
    /// 反复读取会话缓存（<c>~/.dsh/storages/session_projcache/sessions/&lt;uuid&gt;.json</c>）。
    /// 不是每个会话都有：没有就返回 null，报告里各节自然留空。
    /// </summary>
    private static JsonElement? TryReadSessionCache(string sessionId)
    {
        try
        {
            string path = Path.Combine(DshSessionReader.TitleCacheRoot, sessionId + ".json");
            if (!File.Exists(path)) return null;
            string text = File.ReadAllText(path, Encoding.UTF8);
            if (text.Length == 0) return null;
            // 缓存文件 3 MB 上下，JsonDocument 一次读完没问题；但大文件别解两遍。
            var doc = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 128,
            });
            if (doc.RootElement.TryGetProperty("record", out var rec)
                && rec.TryGetProperty("rows", out var rows)
                && rows.ValueKind == JsonValueKind.Object)
                return rows;
            return null;
        }
        catch (Exception ex)
        {
            Logger.LogError("DshSessionReport.TryReadSessionCache", ex);
            return null;
        }
    }

    // ══════════════════════════════ 各小节 ══════════════════════════════

    private static void AppendHeader(StringBuilder sb, DshSession s, JsonElement? cache)
    {
        sb.AppendLine("# 会话报告：" + (s.HasTitle ? s.Title : "（这条会话没有标题）"));
        sb.AppendLine();
        sb.AppendLine("> 导出时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                      + "　·　导出工具：DSHGuard " + GuardVersion.Version);
        sb.AppendLine("> 本报告由程序**已掌握的事实**拼成：会话缓存里的每轮大纲 + 磁盘文件事实。");
        sb.AppendLine("> 原始日志是 zstd 压缩的多帧二进制，本程序不具备解压能力，故不转码、不放对话全文；");
        sb.AppendLine("> 需要完整原文时，参见文末「六、如何自行解压原始日志」，附一行命令。");
        sb.AppendLine();
        sb.AppendLine("## 一、这条会话是什么");
        sb.AppendLine();
        sb.AppendLine("| 项目 | 值 |");
        sb.AppendLine("| --- | --- |");
        sb.AppendLine("| 标题 | " + Cell(s.HasTitle ? s.Title : "（缓存里没有标题）") + " |");
        sb.AppendLine("| 会话 id | `" + s.SessionId + "` |");
        sb.AppendLine("| 项目目录 | `" + Cell(s.ProjectDir) + "` |");
        sb.AppendLine("| 原始日志 | `" + Path.GetFileName(s.FilePath) + "`（" + GuardPaths.HumanSize(s.SizeBytes) + "） |");
        sb.AppendLine("| 最后写入 | " + s.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " |");

        if (cache is JsonElement rows)
        {
            string cwd = Str(rows, "workingDirectory");
            if (cwd.Length > 0) sb.AppendLine("| 工作目录 | `" + Cell(cwd) + "` |");
            string preset = Str(rows, "agentPreset");
            if (preset.Length > 0) sb.AppendLine("| 引擎预设 | `" + Cell(preset) + "` |");
            string sandbox = Str(rows, "sandboxMode");
            if (sandbox.Length > 0) sb.AppendLine("| 沙箱模式 | `" + Cell(sandbox) + "` |");

            if (TryPath(rows, "permissions", out var perm) && perm.ValueKind == JsonValueKind.Object)
            {
                sb.AppendLine("| 权限 | preset=" + Cell(Prop(perm, "preset"))
                              + "　sandbox=" + Cell(Prop(perm, "sandbox"))
                              + "　approval=" + Cell(Prop(perm, "approval")) + " |");
            }

            if (TryPath(rows, "modelSelection", out var ms)
                && ms.ValueKind == JsonValueKind.Object
                && ms.TryGetProperty("lastUsed", out var lu)
                && lu.ValueKind == JsonValueKind.Object)
                sb.AppendLine("| 模型 | `" + Cell(Prop(lu, "provider")) + "` / `" + Cell(Prop(lu, "model")) + "` |");

            long created = Num(rows, "identity.createdAt", long.MinValue);
            if (created == long.MinValue) created = Num(rows, "sessionListMetadata.lastPromptAt", long.MinValue);
            if (created != long.MinValue)
                sb.AppendLine("| 首次记录 | " + Ms(created) + " |");
        }

        sb.AppendLine();
    }

    private static void AppendStats(StringBuilder sb, DshSession s, JsonElement? cache)
    {
        sb.AppendLine("## 二、它跑了多少");
        sb.AppendLine();
        if (cache is not JsonElement rows)
        {
            sb.AppendLine("（这条会话在 DSH 的会话缓存里没有记录，只有磁盘上的原始文件，统计信息拿不到。）");
            sb.AppendLine();
            return;
        }

        bool wrote = false;
        try
        {
            if (TryPath(rows, "sessionStats", out var st) && st.ValueKind == JsonValueKind.Object)
            {
                sb.AppendLine("- 轮数 **" + Cell(Prop(st, "turns")) + "**　·　步骤 **" + Cell(Prop(st, "steps")) + "**");
                sb.AppendLine("- 模型耗时 " + Dur(PropNum(st, "llmMs")) + "　·　工具耗时 " + Dur(PropNum(st, "toolMs"))
                              + "　·　首字累计 " + Dur(PropNum(st, "ttftMs")));
                long dt = PropNum(st, "decodeTokens");
                if (dt > 0) sb.AppendLine("- 生成输出 " + Fmt(dt) + " token");
                wrote = true;
            }
        }
        catch (Exception ex) { Logger.LogError("DshSessionReport.Stats(turns)", ex); }

        try
        {
            if (TryPath(rows, "tokenUsage.totals", out var tt) && tt.ValueKind == JsonValueKind.Object)
            {
                sb.AppendLine("- 输入（未命中缓存）" + Fmt(PropNum(tt, "uncachedInputTokens"))
                              + "　·　命中缓存 " + Fmt(PropNum(tt, "cacheReadTokens"))
                              + "　·　输出 " + Fmt(PropNum(tt, "outputTokens")));
                wrote = true;
            }
        }
        catch (Exception ex) { Logger.LogError("DshSessionReport.Stats(tokens)", ex); }

        try
        {
            if (TryPath(rows, "costUsage.totals", out var ct) && ct.ValueKind == JsonValueKind.Object)
            {
                sb.AppendLine("- 花费（按计费口径累计）" + Prop(ct, "cost"));
                if (TryPath(rows, "costUsage.byModel", out var bm) && bm.ValueKind == JsonValueKind.Object)
                {
                    foreach (var m in bm.EnumerateObject())
                    {
                        if (m.Value.ValueKind != JsonValueKind.Object) continue;
                        sb.AppendLine("  - `" + m.Name + "`：" + Fmt(PropNum(m.Value, "input")) + " in / "
                                      + Fmt(PropNum(m.Value, "output")) + " out / " + Cell(Prop(m.Value, "cost")));
                    }
                }
                wrote = true;
            }
        }
        catch (Exception ex) { Logger.LogError("DshSessionReport.Stats(cost)", ex); }

        try
        {
            if (TryPath(rows, "contextPressure", out var cp) && cp.ValueKind == JsonValueKind.Object)
            {
                long window = PropNum(cp, "contextWindow"), used = PropNum(cp, "pressureTokens");
                if (window > 0)
                    sb.AppendLine("- 上下文压力 " + Fmt(used) + " / " + Fmt(window)
                                  + "（" + (used * 100.0 / window).ToString("0.0", CultureInfo.InvariantCulture) + "%）");
                wrote = true;
            }
        }
        catch (Exception ex) { Logger.LogError("DshSessionReport.Stats(pressure)", ex); }

        if (!wrote) sb.AppendLine("（缓存里没有可读的统计字段。）");
        sb.AppendLine();
    }

    private static void AppendTurns(StringBuilder sb, JsonElement? cache)
    {
        sb.AppendLine("## 三、每一轮都干了什么");
        sb.AppendLine();
        if (cache is not JsonElement rows
            || !TryPath(rows, "turnOutline.turns", out var turns)
            || turns.ValueKind != JsonValueKind.Array
            || turns.GetArrayLength() == 0)
        {
            sb.AppendLine("（这条会话没有留下每轮大纲。）");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("> 以下逐轮大纲来自 DSH 自己的会话缓存，助手回复是**缓存里的摘要文本**（不是原文全文）。");
        sb.AppendLine();

        int total = turns.GetArrayLength();
        const int MaxTurns = 400;
        int shown = 0;
        foreach (var t in turns.EnumerateArray())
        {
            if (shown >= MaxTurns) break;
            shown++;
            string prompt = Cut(Prop(t, "prompt"), 600);
            string response = Cut(Prop(t, "response"), 1200);
            sb.AppendLine("### 第 " + Cell(Prop(t, "turn")) + " 轮（seq " + Cell(Prop(t, "seq")) + "）");
            sb.AppendLine();
            sb.AppendLine("**你：** " + (prompt.Length > 0 ? Indent(prompt) : "（空）"));
            sb.AppendLine();
            sb.AppendLine("**它：** " + (response.Length > 0 ? Indent(response) : "（没有回复记录）"));
            sb.AppendLine();
        }
        if (total > shown)
            sb.AppendLine("> 只列了前 " + shown + " 轮，这条会话共 " + total + " 轮。");
        sb.AppendLine();
    }

    private static void AppendGoal(StringBuilder sb, JsonElement? cache)
    {
        if (cache is not JsonElement rows) return;
        try
        {
            if (!TryPath(rows, "goal.current.goal", out var g) || g.ValueKind != JsonValueKind.Object) return;
            string obj = Trim(Collapse(Prop(g, "objective")), 2000);
            if (obj.Length == 0) return;
            sb.AppendLine("## 四、它当时的目标");
            sb.AppendLine();
            sb.AppendLine("- 目标：" + Indent(obj));
            sb.AppendLine("- 阶段：`" + Cell(Prop(g, "phase")) + "`");
            if (g.TryGetProperty("blockedReason", out var br) && br.ValueKind == JsonValueKind.Object)
            {
                string msg = Trim(Collapse(Prop(br, "message")), 1000);
                if (msg.Length > 0)
                    sb.AppendLine("- 卡住原因（" + Cell(Prop(br, "code")) + "）：" + Indent(msg));
            }
            sb.AppendLine();
        }
        catch (Exception ex) { Logger.LogError("DshSessionReport.AppendGoal", ex); }
    }

    private static void AppendFiles(
        StringBuilder sb, DshSession s, string dest,
        IReadOnlyList<(string Name, long Bytes, string Sha256)> files)
    {
        sb.AppendLine("## 五、导出目录里有什么");
        sb.AppendLine();
        sb.AppendLine("目录：`" + dest + "`");
        sb.AppendLine();
        sb.AppendLine("| 文件 | 大小 | SHA-256 |");
        sb.AppendLine("| --- | --- | --- |");
        sb.AppendLine("| `" + ReportFileName(s) + "` | 本报告 | — |");
        sb.AppendLine("| `" + DecoderScriptName + "` | 解压脚本 | — |");
        foreach (var f in files)
            sb.AppendLine("| `" + Cell(f.Name) + "` | " + GuardPaths.HumanSize(f.Bytes) + " | `" + f.Sha256 + "` |");
        sb.AppendLine();
        sb.AppendLine("> 原始文件是**原样字节**，一个比特都没动过；SHA-256 供你核对拷贝是否完整。");
        sb.AppendLine();
    }

    private static void AppendHowToDecode(StringBuilder sb, DshSession s)
    {
        string raw = Path.GetFileName(s.FilePath);
        string baseName = raw.EndsWith(".zstd", StringComparison.OrdinalIgnoreCase)
            ? raw.Substring(0, raw.Length - 5) : raw + ".out";

        sb.AppendLine("## 六、怎么自己解开原始日志");
        sb.AppendLine();
        sb.AppendLine("DSH 的会话日志是**多帧拼接**的 zstd：整个文件一次解压只会得到第一帧（实测 210 字节），"
                      + "必须逐帧解。下面这段脚本已经过实测（本机 14.6 MB / 7757 帧，0.3 秒解出 47.9 MB / 13541 行）：");
        sb.AppendLine();
        sb.AppendLine("```bat");
        sb.AppendLine("node \"" + DecoderScriptName + "\" \"" + raw + "\" \"" + baseName + "\"");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("解出来的 `.jsonl` 是每行一个 JSON 事件（`session/title`、`assistant/message`、"
                      + "`tool/result`……），记事本能打开，拖进编辑器也能按行过滤。");
        sb.AppendLine();
        sb.AppendLine("没有 Node 的话，用能识别多帧的 zstd 工具（新版 7-Zip、`zstd -d`）同样可以，"
                      + "注意要选「逐帧 / 全部帧」而不是只解第一帧。");
        sb.AppendLine();
        sb.AppendLine("> 本程序不替你转码，是因为它零第三方依赖、运行时装里没有 zstd 解码器；"
                      + "硬转码要么丢内容要么引原生库，转坏一次比不解更糟。");
        sb.AppendLine();
    }

    // ══════════════════════════════ 解压脚本原文 ══════════════════════════════

    /// <summary>
    /// 随报告落地的逐帧解压脚本（Node 内置 zlib 就能用，不需要 npm 装任何东西）。
    /// 逻辑与程序自己的会话标题扫描同源：扫魔数 → 逐帧解 → 按偏移顺序拼。
    /// </summary>
    public const string DecoderScript = @"// DSH 会话日志逐帧解压（DSHGuard 导出时附带）
// 用法: node 解开日志.js <session.x.jsonl.zstd> <输出.jsonl>
//
// 为什么不能一次解完：DSH 的日志是多帧拼接的 zstd。
// 整个 Buffer 交给 zstdDecompressSync 只会得到第一帧（实测 210 字节）。
// 正确做法是扫出每个帧的起点（魔数 28 b5 2f fd），再逐帧解、按偏移顺序拼起来。
const fs = require('fs');
const zlib = require('zlib');

const input = process.argv[2];
const output = process.argv[3];
if (!input || !output) {
  console.error('用法: node 解开日志.js <session.x.jsonl.zstd> <输出.jsonl>');
  process.exit(2);
}

const buf = fs.readFileSync(input);
const MAGIC = Buffer.from([0x28, 0xb5, 0x2f, 0xfd]);
const parts = [];
let frames = 0;
let skipped = 0;

let at = buf.indexOf(MAGIC, 0);
while (at !== -1) {
  try {
    parts.push(zlib.zstdDecompressSync(buf.subarray(at)));
    frames++;
  } catch (e) {
    skipped++;  // 压缩数据里碰巧出现的魔数，解不了就跳过
  }
  at = buf.indexOf(MAGIC, at + 4);
}

if (frames === 0) {
  console.error('一个帧都没解出来：这个文件可能不是 zstd，或者不是多帧拼接格式。');
  process.exit(1);
}

fs.writeFileSync(output, Buffer.concat(parts));
console.log('帧 ' + frames + ' / 跳过 ' + skipped + ' / 输出 ' + fs.statSync(output).size + ' 字节');
";

    // ══════════════════════════════ 小工具 ══════════════════════════════

    /// <summary>按点分路径取 rows.&lt;a&gt;.val.&lt;b&gt;…；取不到返回 false。</summary>
    private static bool TryPath(JsonElement rows, string dotted, out JsonElement value)
    {
        value = default;
        var cur = rows;
        var segs = dotted.Split('.');
        for (int i = 0; i < segs.Length; i++)
        {
            if (cur.ValueKind != JsonValueKind.Object) return false;
            if (cur.TryGetProperty(segs[i], out var next)) { cur = next; }
            else if (cur.TryGetProperty("val", out var viaVal)          // rows.<key>.val.<...>
                     && viaVal.ValueKind == JsonValueKind.Object
                     && viaVal.TryGetProperty(segs[i], out next)) { cur = next; }
            else return false;
        }
        value = cur;
        return true;
    }

    private static string Prop(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? Raw(v) : "";

    private static long PropNum(JsonElement e, string name)
    {
        string s = Prop(e, name);
        return long.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out long n) ? n : 0;
    }

    private static string Raw(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? "",
        JsonValueKind.Number => v.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        _ => v.GetRawText(),
    };

    /// <summary>取 rows.&lt;key&gt;.val 的字符串形态；非字符串返回空串。</summary>
    private static string Str(JsonElement rows, string key) =>
        TryPath(rows, key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static long Num(JsonElement rows, string key, long fallback)
    {
        if (!TryPath(rows, key, out var v)) return fallback;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n)) return n;
        if (v.ValueKind == JsonValueKind.String
            && long.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out long m)) return m;
        return fallback;
    }

    private static string Ms(long unixMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Fmt(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    private static string Dur(long ms)
    {
        if (ms <= 0) return "0";
        if (ms >= 3_600_000) return (ms / 3_600_000) + "h" + (ms / 60_000 % 60).ToString("00", CultureInfo.InvariantCulture) + "m";
        if (ms >= 60_000) return (ms / 60_000) + "m" + (ms / 1_000 % 60).ToString("00", CultureInfo.InvariantCulture) + "s";
        if (ms >= 1_000) return (ms / 1_000) + "s";
        return ms + "ms";
    }

    /// <summary>表格单元格：竖线转义、换行压成 &lt;br&gt;。</summary>
    private static string Cell(string s) =>
        (s ?? "").Replace("|", "\\|").Replace("\r\n", "<br>").Replace("\n", "<br>").Replace("\r", "<br>");

    /// <summary>正文段落：把多行压成单行并用续行缩进，避免 Markdown 里被当成新块。</summary>
    private static string Indent(string s) =>
        (s ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\n  ");

    private static string Collapse(string s) =>
        (s ?? "").Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();

    private static string Trim(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";

    /// <summary>截断长文本，并在末尾给出省略标记（保留原有换行结构）。</summary>
    private static string Cut(string s, int max)
    {
        s = (s ?? "").Trim();
        if (s.Length <= max) return s;
        return s.Substring(0, max) + "…（略，原文 " + s.Length.ToString("N0", CultureInfo.InvariantCulture) + " 字）";
    }
}
