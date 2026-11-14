#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DSHGuard.Unified;

/// <summary>一条来自本地文件 / 其他 Agent 非订阅配置 / 剪贴板文本的待导入模型提供商或凭据候选。</summary>
public sealed class ImportedProviderCandidate
{
    public string Source { get; init; } = "";        // 文件读取 / Claude Code / Codex / Gemini CLI / OpenCode / 粘贴文本
    public string AppType { get; init; } = "";
    public string ProviderId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string EnvKey { get; init; } = "";
    public string Api { get; init; } = "openai-completions";
    public string BaseUrl { get; init; } = "";
    public string ApiKey { get; init; } = "";        // 仅在当次导入流程瞬时驻留内存，用完即清空；界面一律用 MaskedKey
    public string MaskedKey => DshCredentialManager.MaskSecret(ApiKey);
    public IReadOnlyList<string> Models { get; init; } = Array.Empty<string>();
    public bool IsCurrent { get; init; }
    public string Note { get; init; } = "";
}

/// <summary>本地 Agent 扫描或文件解析导入的汇总报告（结果仅实时生成，不落盘缓存）。</summary>
public sealed class CcSwitchScanReport
{
    public string SourcePath { get; init; } = "";
    public bool Available { get; init; }
    public string Summary { get; init; } = "";
    public string Error { get; init; } = "";
    public IReadOnlyList<ImportedProviderCandidate> Candidates { get; init; } = Array.Empty<ImportedProviderCandidate>();
}

/// <summary>
/// 2.4.0 零缓存凭据一键读取与多格式导入器：
///   1. 文件拖入 / 选取自动解析（包含 URL + API Key 的任意文本、.env、JSON、YAML、TOML、cURL、SQLite）；
///   2. 本机其他 Agent 非订阅明文凭据一键读取（Claude Code、Codex、Gemini CLI、OpenCode、Continue、Aider），
///      严格跳过 OAuth / ChatGPT Plus / Claude Pro 等订阅登录态；
///   3. 粘贴文本 / 命令即时提取；全流程不保留守护壳副本或缓存，仅写入官方表单。
/// </summary>
public static class DshCcSwitchImporter
{
    private const int SqliteOk = 0;
    private const int SqliteRow = 100;
    private const int SqliteOpenReadOnly = 0x00000001;

    private static readonly Regex BareSkRegex = new(@"\bsk-[A-Za-z0-9_\-]{16,160}\b", RegexOptions.Compiled);
    private static readonly Regex BareProviderKeyRegex = new(
        @"\b(?:sk-(?:ant|proj|svcacct)?-[A-Za-z0-9_\-]{14,180}|sk-[A-Za-z0-9_\-]{16,180}|AIza[0-9A-Za-z_\-]{28,48}|gsk_[A-Za-z0-9]{20,80}|xai-[A-Za-z0-9_\-]{20,120})\b",
        RegexOptions.Compiled);
    private static readonly Regex LabeledSecretRegex = new(
        @"(?im)^[^\r\n]*?(?:api[_\-\s]*key|auth[_\-\s]*token|secret[_\-\s]*key|access[_\-\s]*key|token|密钥|apikey)\s*[:=：]\s*[""']?([A-Za-z0-9][A-Za-z0-9._\-]{14,180})[""']?",
        RegexOptions.Compiled);
    private static readonly Regex CurlUrlRegex = new(@"https?://[^\s'""\\<>]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TomlBaseUrlRegex = new(@"base_url\s*=\s*['""]([^'""]+)['""]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TomlApiKeyRegex = new(@"(?:api_key|openai_api_key)\s*=\s*['""]([^'""]+)['""]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TomlModelRegex = new(@"^\s*model\s*=\s*['""]([^'""]+)['""]", RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);

    // ══════════════ Windows 内置 winsqlite3.dll P/Invoke ══════════════

    private static class WinSqlite3
    {
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int sqlite3_open_v2(byte[] filenameUtf8, out IntPtr db, int flags, IntPtr vfs);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int sqlite3_prepare_v2(IntPtr db, byte[] sqlUtf8, int nByte, out IntPtr stmt, IntPtr tail);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int sqlite3_step(IntPtr stmt);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int sqlite3_column_int(IntPtr stmt, int col);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int sqlite3_finalize(IntPtr stmt);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int sqlite3_close_v2(IntPtr db);

        internal static byte[] Utf8Z(string s)
        {
            byte[] raw = Encoding.UTF8.GetBytes(s);
            byte[] nul = new byte[raw.Length + 1];
            Buffer.BlockCopy(raw, 0, nul, 0, raw.Length);
            return nul;
        }

        internal static string ReadText(IntPtr stmt, int col)
        {
            IntPtr p = sqlite3_column_text(stmt, col);
            return p == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(p) ?? "";
        }
    }

    /// <summary>以严格只读模式扫描本机 CC Switch SQLite 数据库。</summary>
    public static CcSwitchScanReport ScanCcSwitchDb(string? dbPathOverride = null)
    {
        string path = string.IsNullOrWhiteSpace(dbPathOverride) ? GuardPaths.CcSwitchDefaultDb : dbPathOverride!;
        if (!File.Exists(path))
        {
            return new CcSwitchScanReport
            {
                SourcePath = path,
                Available = false,
                Summary = "未检测到本机 cc-switch.db",
            };
        }

        IntPtr db = IntPtr.Zero;
        IntPtr stmt = IntPtr.Zero;
        try
        {
            int rc = WinSqlite3.sqlite3_open_v2(WinSqlite3.Utf8Z(path), out db, SqliteOpenReadOnly, IntPtr.Zero);
            if (rc != SqliteOk || db == IntPtr.Zero)
            {
                return new CcSwitchScanReport
                {
                    SourcePath = path,
                    Available = false,
                    Error = $"打开数据库失败（SQLite rc={rc}）",
                    Summary = "无法只读打开 cc-switch.db",
                };
            }

            string sql = "SELECT id, app_type, name, settings_config, website_url, is_current FROM providers ORDER BY COALESCE(sort_index, 999999), name";
            int prep = WinSqlite3.sqlite3_prepare_v2(db, WinSqlite3.Utf8Z(sql), -1, out stmt, IntPtr.Zero);
            if (prep != SqliteOk || stmt == IntPtr.Zero)
            {
                // 兼容缺少 sort_index 的早期表结构
                sql = "SELECT id, app_type, name, settings_config, website_url, is_current FROM providers";
                prep = WinSqlite3.sqlite3_prepare_v2(db, WinSqlite3.Utf8Z(sql), -1, out stmt, IntPtr.Zero);
                if (prep != SqliteOk || stmt == IntPtr.Zero)
                {
                    return new CcSwitchScanReport
                    {
                        SourcePath = path,
                        Available = false,
                        Error = $"查询 providers 表失败（SQLite rc={prep}）",
                        Summary = "数据库结构不匹配",
                    };
                }
            }

            var items = new List<ImportedProviderCandidate>();
            while (WinSqlite3.sqlite3_step(stmt) == SqliteRow)
            {
                string id = WinSqlite3.ReadText(stmt, 0);
                string appType = WinSqlite3.ReadText(stmt, 1);
                string name = WinSqlite3.ReadText(stmt, 2);
                string settingsJson = WinSqlite3.ReadText(stmt, 3);
                string websiteUrl = WinSqlite3.ReadText(stmt, 4);
                bool isCurrent = WinSqlite3.sqlite3_column_int(stmt, 5) != 0;

                var candidate = ParseCcSwitchProviderRow(id, appType, name, settingsJson, websiteUrl, isCurrent);
                if (candidate != null) items.Add(candidate);
            }

            string summary = items.Count > 0
                ? $"检测到 {items.Count} 条 CC Switch 配置（含密钥 {items.Count(c => c.ApiKey.Length > 0)} 条）"
                : "cc-switch.db 中暂无可导入的模型配置";

            return new CcSwitchScanReport
            {
                SourcePath = path,
                Available = true,
                Summary = summary,
                Candidates = items,
            };
        }
        catch (Exception ex)
        {
            Logger.LogError("DshCcSwitchImporter.ScanCcSwitchDb", ex);
            return new CcSwitchScanReport
            {
                SourcePath = path,
                Available = false,
                Error = ex.Message,
                Summary = "读取 cc-switch.db 出错：" + ex.Message,
            };
        }
        finally
        {
            if (stmt != IntPtr.Zero)
            {
                try { _ = WinSqlite3.sqlite3_finalize(stmt); } catch { }
            }
            if (db != IntPtr.Zero)
            {
                try { _ = WinSqlite3.sqlite3_close_v2(db); } catch { }
            }
        }
    }

    private static ImportedProviderCandidate? ParseCcSwitchProviderRow(
        string id,
        string appType,
        string name,
        string settingsJson,
        string websiteUrl,
        bool isCurrent)
    {
        if (string.IsNullOrWhiteSpace(settingsJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(settingsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            string app = (appType ?? "").Trim().ToLowerInvariant();
            string displayName = !string.IsNullOrWhiteSpace(name) ? name.Trim() : id;
            string envKey = "";
            string api = "openai-completions";
            string baseUrl = "";
            string apiKey = "";
            var models = new List<string>();

            if (app == "dsh")
            {
                displayName = GetStr(root, "displayName") ?? displayName;
                envKey = GetStr(root, "apiKeyEnv") ?? "";
                api = GetStr(root, "api") ?? "openai-completions";
                baseUrl = GetStr(root, "baseURL") ?? GetStr(root, "baseUrl") ?? "";
                apiKey = GetStr(root, "apiKey") ?? "";

                if (root.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Object)
                {
                    if (apiKey.Length == 0) apiKey = GetStr(opts, "apiKey") ?? "";
                    if (baseUrl.Length == 0) baseUrl = GetStr(opts, "baseURL") ?? GetStr(opts, "baseUrl") ?? "";
                }

                if (root.TryGetProperty("models", out var mEl))
                    ExtractModelsFromElement(mEl, models);
            }
            else if (app is "claude" or "claude-desktop")
            {
                api = "anthropic-messages";
                if (root.TryGetProperty("env", out var envEl) && envEl.ValueKind == JsonValueKind.Object)
                {
                    baseUrl = GetStr(envEl, "ANTHROPIC_BASE_URL") ?? GetStr(envEl, "BASE_URL") ?? "";
                    apiKey = GetStr(envEl, "ANTHROPIC_AUTH_TOKEN") ?? GetStr(envEl, "ANTHROPIC_API_KEY") ?? "";
                    string? m = GetStr(envEl, "ANTHROPIC_MODEL");
                    if (!string.IsNullOrWhiteSpace(m)) models.Add(m!.Trim());
                }
            }
            else if (app == "codex")
            {
                api = "openai-completions";
                if (root.TryGetProperty("auth", out var authEl) && authEl.ValueKind == JsonValueKind.Object)
                    apiKey = GetStr(authEl, "OPENAI_API_KEY") ?? "";
                if (root.TryGetProperty("config", out var cfgEl) && cfgEl.ValueKind == JsonValueKind.String)
                {
                    string toml = cfgEl.GetString() ?? "";
                    var bm = TomlBaseUrlRegex.Match(toml);
                    if (bm.Success) baseUrl = bm.Groups[1].Value.Trim();
                    var mm = TomlModelRegex.Match(toml);
                    if (mm.Success) models.Add(mm.Groups[1].Value.Trim());
                }
                if (root.TryGetProperty("modelCatalog", out var catEl) && catEl.ValueKind == JsonValueKind.Object
                    && catEl.TryGetProperty("models", out var cmEl))
                {
                    ExtractModelsFromElement(cmEl, models);
                }
            }
            else if (app == "gemini")
            {
                api = "google-generative-ai";
                if (root.TryGetProperty("env", out var envEl) && envEl.ValueKind == JsonValueKind.Object)
                {
                    baseUrl = GetStr(envEl, "GOOGLE_GEMINI_BASE_URL") ?? "";
                    apiKey = GetStr(envEl, "GEMINI_API_KEY") ?? "";
                    string? m = GetStr(envEl, "GEMINI_MODEL");
                    if (!string.IsNullOrWhiteSpace(m)) models.Add(m!.Trim());
                }
            }
            else
            {
                // 通用 JSON 对象提取
                apiKey = GetStr(root, "apiKey") ?? GetStr(root, "token") ?? "";
                baseUrl = GetStr(root, "baseURL") ?? GetStr(root, "baseUrl") ?? GetStr(root, "endpoint") ?? "";
            }

            if (baseUrl.Length == 0 && Uri.TryCreate(websiteUrl, UriKind.Absolute, out _))
                baseUrl = websiteUrl.Trim();

            // 仅当至少存在密钥、URL 或模型之一时才作为有效候选（跳过空 official 占位）
            if (apiKey.Length == 0 && baseUrl.Length == 0 && models.Count == 0)
                return null;

            string pid = DshCredentialManager.NormalizeProviderId(id.Length > 0 && !Guid.TryParse(id, out _) ? id : displayName);
            string resolvedEnv = envKey.Length > 0
                ? DshCredentialManager.NormalizeEnvKey(envKey)
                : DshCredentialManager.NormalizeEnvKey(pid + "_API_KEY");

            return new ImportedProviderCandidate
            {
                Source = $"CC Switch ({app})",
                AppType = app,
                ProviderId = pid,
                DisplayName = displayName,
                EnvKey = resolvedEnv,
                Api = api,
                BaseUrl = baseUrl,
                ApiKey = apiKey,
                Models = models.Distinct(StringComparer.Ordinal).ToList(),
                IsCurrent = isCurrent,
                Note = isCurrent ? "CC Switch 当前激活" : "",
            };
        }
        catch
        {
            return null;
        }
    }

    // ══════════════ ccswitch:// 深度链接解析 ══════════════

    /// <summary>
    /// 解析 <c>ccswitch://...</c> 导入链接。
    /// 支持 query 参数（name/remark、endpoint/baseUrl/url、apiKey/key/token、app/protocol、model/models）
    /// 与 base64 编码的 <c>data</c> / <c>config</c> JSON 载荷。
    /// </summary>
    public static IReadOnlyList<ImportedProviderCandidate> ParseCcSwitchDeepLink(string rawUri)
    {
        var results = new List<ImportedProviderCandidate>();
        if (string.IsNullOrWhiteSpace(rawUri)) return results;

        string text = rawUri.Trim();
        if (!text.StartsWith("ccswitch://", StringComparison.OrdinalIgnoreCase))
            return results;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
            return results;

        var qs = ParseQueryString(uri.Query);
        string? b64Payload = FirstNonEmpty(qs, "data", "config", "payload");
        if (!string.IsNullOrWhiteSpace(b64Payload))
        {
            try
            {
                string decoded = DecodeBase64Flexible(b64Payload!);
                var fromPayload = ParseSmartText(decoded);
                foreach (var c in fromPayload)
                {
                    results.Add(new ImportedProviderCandidate
                    {
                        Source = "ccswitch:// 深链",
                        AppType = c.AppType,
                        ProviderId = c.ProviderId,
                        DisplayName = c.DisplayName,
                        EnvKey = c.EnvKey,
                        Api = c.Api,
                        BaseUrl = c.BaseUrl,
                        ApiKey = c.ApiKey,
                        Models = c.Models,
                        Note = "Base64 载荷",
                    });
                }
                if (results.Count > 0) return results;
            }
            catch { }
        }

        string? name = FirstNonEmpty(qs, "name", "remark", "displayName", "provider");
        string? endpoint = FirstNonEmpty(qs, "endpoint", "baseUrl", "baseURL", "url");
        string? apiKey = FirstNonEmpty(qs, "apiKey", "key", "token", "secret");
        string? app = FirstNonEmpty(qs, "app", "protocol", "api");
        string? modelCsv = FirstNonEmpty(qs, "models", "model");

        if (string.IsNullOrWhiteSpace(endpoint) && string.IsNullOrWhiteSpace(apiKey))
            return results;

        string resolvedName = !string.IsNullOrWhiteSpace(name)
            ? name!.Trim()
            : InferProviderNameFromUrl(endpoint, "ccswitch-import");
        string pid = DshCredentialManager.NormalizeProviderId(resolvedName);
        string api = InferApiProtocol(app, endpoint);
        var models = SplitCsvModels(modelCsv);

        results.Add(new ImportedProviderCandidate
        {
            Source = "ccswitch:// 深链",
            AppType = app ?? "",
            ProviderId = pid,
            DisplayName = resolvedName,
            EnvKey = DshCredentialManager.NormalizeEnvKey(pid + "_API_KEY"),
            Api = api,
            BaseUrl = (endpoint ?? "").Trim(),
            ApiKey = (apiKey ?? "").Trim(),
            Models = models,
            Note = "深链参数导入",
        });
        return results;
    }

    // ══════════════ 本机其他 Agent 非订阅明文凭据一键读取 ══════════════

    /// <summary>
    /// 只读扫描本机其他 AI Agent（Claude Code / Codex / Gemini CLI / OpenCode / Continue / Aider）
    /// 中以**明文 API Key** 存在的非订阅套餐配置；OAuth / ChatGPT Plus / Claude Pro 登录态自动跳过。
    /// </summary>
    public static CcSwitchScanReport ScanLocalAgents(string? homeOverride = null)
    {
        string home = string.IsNullOrWhiteSpace(homeOverride) ? GuardPaths.AgentHomeRoot : homeOverride!;
        var candidates = new List<ImportedProviderCandidate>();
        int skippedSubscription = 0;

        try
        {
            if (Directory.Exists(home))
            {
                ScanClaudeCodeAgent(home, candidates, ref skippedSubscription);
                ScanCodexAgent(home, candidates, ref skippedSubscription);
                ScanGeminiCliAgent(home, candidates, ref skippedSubscription);
                ScanOpenCodeAgent(home, candidates);
                ScanContinueAgent(home, candidates);
                ScanAiderAgent(home, candidates);

                // 若本地恰有未加密的 cc-switch.db，顺带提取其中的非账号明文条目
                string dbPath = Path.Combine(home, ".cc-switch", "cc-switch.db");
                if (File.Exists(dbPath))
                {
                    var dbRep = ScanCcSwitchDb(dbPath);
                    foreach (var c in dbRep.Candidates)
                    {
                        if (string.IsNullOrWhiteSpace(c.ApiKey) || IsSubscriptionOrPlaceholderSecret(c.EnvKey, c.ApiKey))
                        {
                            skippedSubscription++;
                            continue;
                        }
                        candidates.Add(c);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("DshCcSwitchImporter.ScanLocalAgents", ex);
        }

        // 去重：相同 BaseUrl + ApiKey + EnvKey 只留一条
        var dedup = new List<ImportedProviderCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c.ApiKey) && string.IsNullOrWhiteSpace(c.BaseUrl)) continue;
            if (!string.IsNullOrWhiteSpace(c.ApiKey) && IsSubscriptionOrPlaceholderSecret(c.EnvKey, c.ApiKey)) continue;
            string sig = $"{c.ApiKey.Trim()}|{TrimChatPathFromUrl(c.BaseUrl)}|{c.EnvKey}";
            if (seen.Add(sig)) dedup.Add(c);
        }

        string summary;
        if (dedup.Count > 0)
        {
            summary = skippedSubscription > 0
                ? $"读取到 {dedup.Count} 条其他 Agent 非订阅明文凭据（已自动跳过 {skippedSubscription} 处订阅 / OAuth 登录态）"
                : $"读取到 {dedup.Count} 条其他 Agent 非订阅明文凭据，核对后可一键写入 DSH 官方表单";
        }
        else if (skippedSubscription > 0)
        {
            summary = $"检测到 {skippedSubscription} 处 Agent 为订阅 / OAuth 登录态（按规则跳过），未发现非订阅明文 API Key";
        }
        else
        {
            summary = "未在本机其他 Agent（Claude Code / Codex / Gemini CLI / OpenCode 等）检测到明文 API Key";
        }

        return new CcSwitchScanReport
        {
            SourcePath = home,
            Available = Directory.Exists(home),
            Summary = summary,
            Candidates = dedup,
        };
    }

    private static void ScanClaudeCodeAgent(string home, List<ImportedProviderCandidate> target, ref int skippedSub)
    {
        bool foundPlain = false;
        foreach (string rel in new[] { Path.Combine(".claude", "settings.json"), Path.Combine(".claude", "settings.local.json") })
        {
            string p = Path.Combine(home, rel);
            if (!File.Exists(p)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(p, Encoding.UTF8));
                if (doc.RootElement.TryGetProperty("env", out var envEl) && envEl.ValueKind == JsonValueKind.Object)
                {
                    string? baseUrl = GetStr(envEl, "ANTHROPIC_BASE_URL") ?? GetStr(envEl, "OPENAI_BASE_URL");
                    string? token = GetStr(envEl, "ANTHROPIC_AUTH_TOKEN")
                                 ?? GetStr(envEl, "ANTHROPIC_API_KEY")
                                 ?? GetStr(envEl, "OPENAI_API_KEY");
                    string? model = GetStr(envEl, "ANTHROPIC_MODEL") ?? GetStr(doc.RootElement, "model");
                    if (!string.IsNullOrWhiteSpace(token) && !IsSubscriptionOrPlaceholderSecret("ANTHROPIC_API_KEY", token))
                    {
                        foundPlain = true;
                        bool isAnth = envEl.TryGetProperty("ANTHROPIC_AUTH_TOKEN", out _)
                                   || envEl.TryGetProperty("ANTHROPIC_API_KEY", out _)
                                   || envEl.TryGetProperty("ANTHROPIC_BASE_URL", out _);
                        string url = TrimChatPathFromUrl(baseUrl ?? "");
                        string disp = url.Length > 0 ? $"Claude · {InferProviderNameFromUrl(url, "relay")}" : "Claude Code";
                        target.Add(new ImportedProviderCandidate
                        {
                            Source = "Claude Code",
                            AppType = "claude",
                            ProviderId = DshCredentialManager.NormalizeProviderId(url.Length > 0 ? InferProviderNameFromUrl(url, "claude") : "anthropic"),
                            DisplayName = disp,
                            EnvKey = isAnth ? "ANTHROPIC_API_KEY" : "OPENAI_API_KEY",
                            Api = isAnth ? "anthropic-messages" : "openai-completions",
                            BaseUrl = url,
                            ApiKey = token!.Trim(),
                            Models = string.IsNullOrWhiteSpace(model) ? Array.Empty<string>() : new[] { model!.Trim() },
                            Note = "读取自 ~/.claude/settings.json（明文环境变量）",
                        });
                    }
                }
            }
            catch { }
        }

        // 检查 ~/.claude.json 中的 primaryApiKey（非订阅）或 oauthAccount（订阅跳过）
        string claudeRootJson = Path.Combine(home, ".claude.json");
        if (File.Exists(claudeRootJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(claudeRootJson, Encoding.UTF8));
                var root = doc.RootElement;
                string? pk = GetStr(root, "primaryApiKey");
                if (!string.IsNullOrWhiteSpace(pk) && !IsSubscriptionOrPlaceholderSecret("ANTHROPIC_API_KEY", pk))
                {
                    foundPlain = true;
                    target.Add(new ImportedProviderCandidate
                    {
                        Source = "Claude Code",
                        AppType = "claude",
                        ProviderId = "anthropic",
                        DisplayName = "Claude Code (primaryApiKey)",
                        EnvKey = "ANTHROPIC_API_KEY",
                        Api = "anthropic-messages",
                        BaseUrl = "",
                        ApiKey = pk!.Trim(),
                        Note = "读取自 ~/.claude.json",
                    });
                }
                else if (root.TryGetProperty("oauthAccount", out _))
                {
                    skippedSub++;
                }
            }
            catch { }
        }

        string oauthCred = Path.Combine(home, ".claude", ".credentials.json");
        if (!foundPlain && File.Exists(oauthCred))
        {
            try
            {
                string raw = File.ReadAllText(oauthCred, Encoding.UTF8);
                if (raw.Contains("claudeAiOauth", StringComparison.OrdinalIgnoreCase)
                    || raw.Contains("accessToken", StringComparison.OrdinalIgnoreCase))
                {
                    skippedSub++;
                }
            }
            catch { }
        }
    }

    private static void ScanCodexAgent(string home, List<ImportedProviderCandidate> target, ref int skippedSub)
    {
        string codexDir = Path.Combine(home, ".codex");
        if (!Directory.Exists(codexDir)) return;

        string tomlPath = Path.Combine(codexDir, "config.toml");
        string authPath = Path.Combine(codexDir, "auth.json");

        string baseUrl = "";
        string model = "";
        string envKey = "OPENAI_API_KEY";
        string providerName = "Codex";
        if (File.Exists(tomlPath))
        {
            try
            {
                string toml = File.ReadAllText(tomlPath, Encoding.UTF8);
                var parsedToml = TryParseTomlBlock(toml);
                if (parsedToml.Count > 0)
                {
                    baseUrl = parsedToml[0].BaseUrl;
                    envKey = parsedToml[0].EnvKey;
                    providerName = string.IsNullOrWhiteSpace(baseUrl)
                        ? "Codex"
                        : $"Codex · {InferProviderNameFromUrl(baseUrl, "openai")}";
                    if (parsedToml[0].Models.Count > 0) model = parsedToml[0].Models[0];
                }
            }
            catch { }
        }

        bool importedFromAuth = false;
        if (File.Exists(authPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(authPath, Encoding.UTF8));
                var root = doc.RootElement;
                string? authMode = GetStr(root, "auth_mode");
                string? apiKey = GetStr(root, "OPENAI_API_KEY") ?? GetStr(root, "api_key");
                bool hasPlainKey = !string.IsNullOrWhiteSpace(apiKey)
                                && !IsSubscriptionOrPlaceholderSecret(envKey, apiKey!);

                if (hasPlainKey)
                {
                    importedFromAuth = true;
                    string pid = DshCredentialManager.NormalizeProviderId(
                        baseUrl.Length > 0 ? InferProviderNameFromUrl(baseUrl, "codex") : "openai");
                    target.Add(new ImportedProviderCandidate
                    {
                        Source = "Codex",
                        AppType = "codex",
                        ProviderId = pid,
                        DisplayName = providerName,
                        EnvKey = DshCredentialManager.NormalizeEnvKey(envKey),
                        Api = "openai-completions",
                        BaseUrl = baseUrl,
                        ApiKey = apiKey!.Trim(),
                        Models = model.Length > 0 ? new[] { model } : Array.Empty<string>(),
                        Note = "读取自 ~/.codex/auth.json + config.toml",
                    });
                }
                else if (root.TryGetProperty("tokens", out _)
                      || root.TryGetProperty("access_token", out _)
                      || string.Equals(authMode, "chatgpt", StringComparison.OrdinalIgnoreCase))
                {
                    // ChatGPT Plus/Pro 订阅登录态：严格跳过
                    skippedSub++;
                }
            }
            catch { }
        }

        // 若 config.toml 本身带了非订阅明文 api_key（且 auth.json 没重复输出）
        if (!importedFromAuth && File.Exists(tomlPath))
        {
            try
            {
                foreach (var tc in TryParseTomlBlock(File.ReadAllText(tomlPath, Encoding.UTF8)))
                {
                    if (!string.IsNullOrWhiteSpace(tc.ApiKey) && !IsSubscriptionOrPlaceholderSecret(tc.EnvKey, tc.ApiKey))
                    {
                        target.Add(new ImportedProviderCandidate
                        {
                            Source = "Codex",
                            AppType = "codex",
                            ProviderId = tc.ProviderId,
                            DisplayName = string.IsNullOrWhiteSpace(tc.BaseUrl) ? "Codex" : $"Codex · {tc.DisplayName}",
                            EnvKey = tc.EnvKey,
                            Api = tc.Api,
                            BaseUrl = tc.BaseUrl,
                            ApiKey = tc.ApiKey,
                            Models = tc.Models,
                            Note = "读取自 ~/.codex/config.toml",
                        });
                    }
                }
            }
            catch { }
        }
    }

    private static void ScanGeminiCliAgent(string home, List<ImportedProviderCandidate> target, ref int skippedSub)
    {
        string gemDir = Path.Combine(home, ".gemini");
        if (!Directory.Exists(gemDir)) return;

        bool foundPlain = false;
        string envPath = Path.Combine(gemDir, ".env");
        if (File.Exists(envPath))
        {
            try
            {
                foreach (var c in TryParseEnvLines(File.ReadAllText(envPath, Encoding.UTF8)))
                {
                    if (string.IsNullOrWhiteSpace(c.ApiKey) || IsSubscriptionOrPlaceholderSecret(c.EnvKey, c.ApiKey)) continue;
                    foundPlain = true;
                    target.Add(new ImportedProviderCandidate
                    {
                        Source = "Gemini CLI",
                        AppType = "gemini",
                        ProviderId = c.ProviderId,
                        DisplayName = c.DisplayName == "gemini" ? "Gemini CLI" : c.DisplayName,
                        EnvKey = c.EnvKey,
                        Api = c.Api,
                        BaseUrl = c.BaseUrl,
                        ApiKey = c.ApiKey,
                        Note = "读取自 ~/.gemini/.env",
                    });
                }
            }
            catch { }
        }

        string oauthCreds = Path.Combine(gemDir, "oauth_creds.json");
        string settingsPath = Path.Combine(gemDir, "settings.json");
        bool isOAuthSelected = false;
        if (File.Exists(settingsPath))
        {
            try
            {
                string raw = File.ReadAllText(settingsPath, Encoding.UTF8);
                isOAuthSelected = raw.Contains("oauth-personal", StringComparison.OrdinalIgnoreCase);
            }
            catch { }
        }
        if (!foundPlain && (File.Exists(oauthCreds) || isOAuthSelected))
        {
            skippedSub++;
        }
    }

    private static void ScanOpenCodeAgent(string home, List<ImportedProviderCandidate> target)
    {
        foreach (string p in new[]
        {
            Path.Combine(home, ".config", "opencode", "opencode.json"),
            Path.Combine(home, ".opencode", "opencode.json"),
        })
        {
            if (!File.Exists(p)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(p, Encoding.UTF8));
                if (!doc.RootElement.TryGetProperty("provider", out var provs) || provs.ValueKind != JsonValueKind.Object) continue;
                foreach (var prop in provs.EnumerateObject())
                {
                    if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                    var opts = prop.Value.TryGetProperty("options", out var oEl) && oEl.ValueKind == JsonValueKind.Object
                        ? oEl
                        : prop.Value;
                    string? apiKey = GetStr(opts, "apiKey") ?? GetStr(prop.Value, "apiKey");
                    string? baseUrl = GetStr(opts, "baseURL") ?? GetStr(opts, "baseUrl") ?? GetStr(prop.Value, "baseURL");
                    if (string.IsNullOrWhiteSpace(apiKey) || apiKey.StartsWith("{env:", StringComparison.OrdinalIgnoreCase)) continue;
                    if (IsSubscriptionOrPlaceholderSecret(prop.Name, apiKey)) continue;
                    string pid = DshCredentialManager.NormalizeProviderId(prop.Name);
                    target.Add(new ImportedProviderCandidate
                    {
                        Source = "OpenCode",
                        AppType = "opencode",
                        ProviderId = pid,
                        DisplayName = prop.Name,
                        EnvKey = DshCredentialManager.NormalizeEnvKey(pid + "_API_KEY"),
                        Api = InferApiProtocol(prop.Name, baseUrl),
                        BaseUrl = TrimChatPathFromUrl(baseUrl ?? ""),
                        ApiKey = apiKey.Trim(),
                        Note = "读取自 opencode.json",
                    });
                }
            }
            catch { }
        }
    }

    private static void ScanContinueAgent(string home, List<ImportedProviderCandidate> target)
    {
        string p = Path.Combine(home, ".continue", "config.json");
        if (!File.Exists(p)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(p, Encoding.UTF8));
            if (!doc.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array) return;
            foreach (var item in models.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                string? apiKey = GetStr(item, "apiKey");
                if (string.IsNullOrWhiteSpace(apiKey) || IsSubscriptionOrPlaceholderSecret("API_KEY", apiKey)) continue;
                string? baseUrl = GetStr(item, "apiBase");
                string? title = GetStr(item, "title") ?? GetStr(item, "provider");
                string? model = GetStr(item, "model");
                string name = string.IsNullOrWhiteSpace(title) ? InferProviderNameFromUrl(baseUrl, "continue") : title!.Trim();
                string pid = DshCredentialManager.NormalizeProviderId(name);
                target.Add(new ImportedProviderCandidate
                {
                    Source = "Continue",
                    AppType = "continue",
                    ProviderId = pid,
                    DisplayName = name,
                    EnvKey = DshCredentialManager.NormalizeEnvKey(pid + "_API_KEY"),
                    Api = InferApiProtocol(GetStr(item, "provider"), baseUrl),
                    BaseUrl = TrimChatPathFromUrl(baseUrl ?? ""),
                    ApiKey = apiKey.Trim(),
                    Models = string.IsNullOrWhiteSpace(model) ? Array.Empty<string>() : new[] { model!.Trim() },
                    Note = "读取自 ~/.continue/config.json",
                });
            }
        }
        catch { }
    }

    private static void ScanAiderAgent(string home, List<ImportedProviderCandidate> target)
    {
        string p = Path.Combine(home, ".aider.conf.yml");
        if (!File.Exists(p)) return;
        try
        {
            string raw = File.ReadAllText(p, Encoding.UTF8);
            string url = Regex.Match(raw, @"(?m)^openai-api-base\s*:\s*[""']?([^\s""'#]+)").Groups[1].Value.Trim();
            foreach (var (yamlKey, envKey, api) in new[]
            {
                ("openai-api-key", "OPENAI_API_KEY", "openai-completions"),
                ("anthropic-api-key", "ANTHROPIC_API_KEY", "anthropic-messages"),
                ("deepseek-api-key", "DEEPSEEK_API_KEY", "openai-completions"),
            })
            {
                var m = Regex.Match(raw, $@"(?m)^{yamlKey}\s*:\s*[""']?([^\s""'#]+)");
                if (!m.Success) continue;
                string val = m.Groups[1].Value.Trim();
                if (IsSubscriptionOrPlaceholderSecret(envKey, val)) continue;
                string name = url.Length > 0 ? InferProviderNameFromUrl(url, yamlKey) : "Aider";
                target.Add(new ImportedProviderCandidate
                {
                    Source = "Aider",
                    AppType = "aider",
                    ProviderId = DshCredentialManager.NormalizeProviderId(name),
                    DisplayName = name,
                    EnvKey = envKey,
                    Api = api,
                    BaseUrl = yamlKey == "openai-api-key" ? TrimChatPathFromUrl(url) : "",
                    ApiKey = val,
                    Note = "读取自 ~/.aider.conf.yml",
                });
            }
        }
        catch { }
    }

    // ══════════════ 文本 / 文件自由提取 ══════════════

    /// <summary>智能解析文件文本或粘贴文本：JSON、cURL、TOML、.env、自由格式 URL + API Key 或深链。</summary>
    public static IReadOnlyList<ImportedProviderCandidate> ParseSmartText(string? rawText)
    {
        var results = new List<ImportedProviderCandidate>();
        if (string.IsNullOrWhiteSpace(rawText)) return results;
        string text = rawText.Trim();

        // 1. 提取文本中的 ccswitch:// 深链（兼容保留）
        foreach (Match m in Regex.Matches(text, @"ccswitch://[^\s""'<>]+", RegexOptions.IgnoreCase))
            results.AddRange(ParseCcSwitchDeepLink(m.Value));
        if (results.Count > 0) return FilterPlainCandidates(results);

        // 2. JSON 块（Claude settings.json / Codex auth / 提供商配置对象或数组）
        if (text.StartsWith("{", StringComparison.Ordinal) || text.StartsWith("[", StringComparison.Ordinal))
        {
            var fromJson = TryParseJsonBlock(text);
            if (fromJson.Count > 0) return FilterPlainCandidates(fromJson);
        }

        // 3. curl 命令
        if (text.Contains("curl ", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("curl", StringComparison.OrdinalIgnoreCase))
        {
            var fromCurl = TryParseCurlCommand(text);
            if (fromCurl != null)
            {
                results.Add(fromCurl);
                return FilterPlainCandidates(results);
            }
        }

        // 4. TOML（Codex config.toml 等）
        if (TomlBaseUrlRegex.IsMatch(text) || TomlApiKeyRegex.IsMatch(text))
        {
            var fromToml = TryParseTomlBlock(text);
            if (fromToml.Count > 0 && fromToml.Any(x => x.ApiKey.Length > 0 || x.BaseUrl.Length > 0))
                return FilterPlainCandidates(fromToml);
        }

        // 5. .env / KEY=VALUE 多行（支持通用 BASE_URL + API_KEY 配对）
        var fromEnv = TryParseEnvLines(text);
        if (fromEnv.Count > 0) return FilterPlainCandidates(fromEnv);

        // 6. 自由文本中配对出现的 URL + API Key（例如丢入记事本里的 url + api）
        var fromFreeform = TryParseFreeformUrlAndKeyBlocks(text);
        if (fromFreeform.Count > 0) return FilterPlainCandidates(fromFreeform);

        // 7. 兜底裸 API Key（sk-... / sk-ant-... / AIza... 等）
        var keyMatch = BareProviderKeyRegex.Match(text);
        if (keyMatch.Success && !IsSubscriptionOrPlaceholderSecret("", keyMatch.Value))
        {
            bool isGem = keyMatch.Value.StartsWith("AIza", StringComparison.Ordinal);
            bool isAnth = keyMatch.Value.StartsWith("sk-ant-", StringComparison.Ordinal);
            results.Add(new ImportedProviderCandidate
            {
                Source = "裸密钥识别",
                ProviderId = isGem ? "gemini" : (isAnth ? "anthropic" : "custom-key"),
                DisplayName = isGem ? "Gemini API Key" : (isAnth ? "Claude API Key" : "识别到的 API Key"),
                EnvKey = isGem ? "GEMINI_API_KEY" : (isAnth ? "ANTHROPIC_API_KEY" : "CUSTOM_API_KEY"),
                Api = isGem ? "google-generative-ai" : (isAnth ? "anthropic-messages" : "openai-completions"),
                BaseUrl = "",
                ApiKey = keyMatch.Value,
                Note = "未附带地址；可直接在下方改名或搭配预设使用",
            });
        }

        return results;
    }

    private static List<ImportedProviderCandidate> FilterPlainCandidates(IEnumerable<ImportedProviderCandidate> src)
        => src.Where(c => string.IsNullOrWhiteSpace(c.ApiKey) || !IsSubscriptionOrPlaceholderSecret(c.EnvKey, c.ApiKey)).ToList();

    private static bool IsSubscriptionOrPlaceholderSecret(string envKey, string secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) return false;
        string s = secret.Trim();
        if (DshCredentialManager.IsAccountBlob(envKey, s)) return true;
        // 典型 JWT（三段 base64url）通常为 OAuth / ChatGPT Plus / Google Account 临时访问令牌
        if (s.StartsWith("eyJ", StringComparison.Ordinal) && s.Count(ch => ch == '.') == 2 && s.Length > 60)
            return true;
        if (s.Contains("your-api-key", StringComparison.OrdinalIgnoreCase)
            || s.Contains("your_api_key", StringComparison.OrdinalIgnoreCase)
            || s.Contains("replace-me", StringComparison.OrdinalIgnoreCase)
            || s.Equals("sk-...", StringComparison.OrdinalIgnoreCase)
            || s.All(ch => ch == 'x' || ch == 'X' || ch == '*'))
            return true;
        return false;
    }

    // ══════════════ 外部文件导入 ══════════════

    /// <summary>智能读取任意装着 URL / API Key 的文本、配置、YAML、JSON、TOML 或只读 SQLite 文件。</summary>
    public static CcSwitchScanReport ImportFromFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return new CcSwitchScanReport
            {
                SourcePath = filePath ?? "",
                Available = false,
                Error = "文件不存在",
                Summary = "找不到指定文件",
            };
        }

        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        try
        {
            if (ext is ".db" or ".sqlite" or ".sqlite3")
                return ScanCcSwitchDb(filePath);

            var fi = new FileInfo(filePath);
            if (fi.Length > 2 * 1024 * 1024)
            {
                return new CcSwitchScanReport
                {
                    SourcePath = filePath,
                    Available = false,
                    Error = "文件过大（超过 2 MB）",
                    Summary = "文件超过 2 MB，请仅拖入包含 URL 与 API Key 的配置文件或文本",
                };
            }

            string content = File.ReadAllText(filePath, Encoding.UTF8);
            if (content.IndexOf('\0') >= 0)
            {
                return new CcSwitchScanReport
                {
                    SourcePath = filePath,
                    Available = false,
                    Error = "非纯文本文件",
                    Summary = "该文件似乎是二进制文件；请拖入纯文本、JSON、.env、TOML 或 YAML 文件",
                };
            }

            var list = new List<ImportedProviderCandidate>();

            if (ext is ".yaml" or ".yml" || filePath.Contains(".credentials.yaml", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var c in ParseCredentialsYamlForImport(content))
                    list.Add(c);
            }

            if (list.Count == 0)
                list.AddRange(ParseSmartText(content));

            // 确保来源标记清晰指向文件读取
            list = list.Select(c => new ImportedProviderCandidate
            {
                Source = string.IsNullOrWhiteSpace(c.Source) || c.Source == "裸密钥识别"
                    ? $"文件读取 ({Path.GetFileName(filePath)})"
                    : c.Source,
                AppType = c.AppType,
                ProviderId = c.ProviderId,
                DisplayName = c.DisplayName,
                EnvKey = c.EnvKey,
                Api = c.Api,
                BaseUrl = c.BaseUrl,
                ApiKey = c.ApiKey,
                Models = c.Models,
                IsCurrent = c.IsCurrent,
                Note = string.IsNullOrWhiteSpace(c.Note) ? Path.GetFileName(filePath) : c.Note,
            }).ToList();

            return new CcSwitchScanReport
            {
                SourcePath = filePath,
                Available = true,
                Summary = list.Count > 0
                    ? $"已从「{Path.GetFileName(filePath)}」自动解析出 {list.Count} 条非订阅凭据"
                    : $"文件「{Path.GetFileName(filePath)}」中未识别到有效 URL / 明文 API Key（订阅 OAuth 令牌会被自动跳过）",
                Candidates = list,
            };
        }
        catch (Exception ex)
        {
            Logger.LogError("DshCcSwitchImporter.ImportFromFile", ex);
            return new CcSwitchScanReport
            {
                SourcePath = filePath,
                Available = false,
                Error = ex.Message,
                Summary = "解析文件失败：" + ex.Message,
            };
        }
    }

    // ══════════════ 内部解析辅助 ══════════════

    private static List<ImportedProviderCandidate> TryParseJsonBlock(string json)
    {
        var list = new List<ImportedProviderCandidate>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                // Claude Code settings.json { "env": { ANTHROPIC_AUTH_TOKEN, ANTHROPIC_BASE_URL } }
                if (root.TryGetProperty("env", out var envEl) && envEl.ValueKind == JsonValueKind.Object)
                {
                    string? baseUrl = GetStr(envEl, "ANTHROPIC_BASE_URL") ?? GetStr(envEl, "OPENAI_BASE_URL");
                    string? token = GetStr(envEl, "ANTHROPIC_AUTH_TOKEN")
                                 ?? GetStr(envEl, "ANTHROPIC_API_KEY")
                                 ?? GetStr(envEl, "OPENAI_API_KEY");
                    if (!string.IsNullOrWhiteSpace(token) || !string.IsNullOrWhiteSpace(baseUrl))
                    {
                        bool isAnthropic = envEl.TryGetProperty("ANTHROPIC_AUTH_TOKEN", out _)
                                        || envEl.TryGetProperty("ANTHROPIC_API_KEY", out _)
                                        || envEl.TryGetProperty("ANTHROPIC_BASE_URL", out _);
                        string name = InferProviderNameFromUrl(baseUrl, isAnthropic ? "claude-relay" : "openai-relay");
                        list.Add(new ImportedProviderCandidate
                        {
                            Source = "JSON 配置",
                            ProviderId = DshCredentialManager.NormalizeProviderId(name),
                            DisplayName = name,
                            EnvKey = isAnthropic ? "ANTHROPIC_API_KEY" : "OPENAI_API_KEY",
                            Api = isAnthropic ? "anthropic-messages" : "openai-completions",
                            BaseUrl = (baseUrl ?? "").Trim(),
                            ApiKey = (token ?? "").Trim(),
                            Models = Array.Empty<string>(),
                        });
                    }
                }
                else
                {
                    string? apiKey = GetStr(root, "apiKey") ?? GetStr(root, "api_key") ?? GetStr(root, "key") ?? GetStr(root, "token");
                    string? baseUrl = GetStr(root, "baseURL") ?? GetStr(root, "baseUrl") ?? GetStr(root, "base_url") ?? GetStr(root, "endpoint") ?? GetStr(root, "url");
                    string? name = GetStr(root, "displayName") ?? GetStr(root, "name") ?? GetStr(root, "provider");
                    string? envKey = GetStr(root, "apiKeyEnv") ?? GetStr(root, "envKey");
                    string? api = GetStr(root, "api") ?? GetStr(root, "protocol");
                    var models = new List<string>();
                    if (root.TryGetProperty("models", out var mEl)) ExtractModelsFromElement(mEl, models);
                    string? singleModel = GetStr(root, "model");
                    if (!string.IsNullOrWhiteSpace(singleModel)) models.Add(singleModel!.Trim());

                    if (!string.IsNullOrWhiteSpace(apiKey) || !string.IsNullOrWhiteSpace(baseUrl))
                    {
                        string display = !string.IsNullOrWhiteSpace(name) ? name!.Trim() : InferProviderNameFromUrl(baseUrl, "json-import");
                        string pid = DshCredentialManager.NormalizeProviderId(display);
                        list.Add(new ImportedProviderCandidate
                        {
                            Source = "JSON 配置",
                            ProviderId = pid,
                            DisplayName = display,
                            EnvKey = !string.IsNullOrWhiteSpace(envKey)
                                ? DshCredentialManager.NormalizeEnvKey(envKey)
                                : DshCredentialManager.NormalizeEnvKey(pid + "_API_KEY"),
                            Api = InferApiProtocol(api, baseUrl),
                            BaseUrl = (baseUrl ?? "").Trim(),
                            ApiKey = (apiKey ?? "").Trim(),
                            Models = models.Distinct(StringComparer.Ordinal).ToList(),
                        });
                    }
                }
            }
        }
        catch { }
        return list;
    }

    private static ImportedProviderCandidate? TryParseCurlCommand(string cmd)
    {
        string normalized = cmd.Replace("\\\r\n", " ", StringComparison.Ordinal)
                               .Replace("\\\n", " ", StringComparison.Ordinal)
                               .Replace("^\r\n", " ", StringComparison.Ordinal);

        var urlMatch = CurlUrlRegex.Match(normalized);
        string rawUrl = urlMatch.Success ? urlMatch.Value : "";
        string baseUrl = TrimChatPathFromUrl(rawUrl);

        string apiKey = "";
        var bearer = Regex.Match(normalized, @"Authorization\s*:\s*Bearer\s+([^\s'""]+)", RegexOptions.IgnoreCase);
        if (bearer.Success) apiKey = bearer.Groups[1].Value.Trim();
        if (apiKey.Length == 0)
        {
            var xApi = Regex.Match(normalized, @"x-api-key\s*:\s*([^\s'""]+)", RegexOptions.IgnoreCase);
            if (xApi.Success) apiKey = xApi.Groups[1].Value.Trim();
        }
        if (apiKey.Length == 0 && baseUrl.Length == 0) return null;

        var models = new List<string>();
        var modelMatch = Regex.Match(normalized, @"""model""\s*:\s*""([^""]+)""", RegexOptions.IgnoreCase);
        if (modelMatch.Success) models.Add(modelMatch.Groups[1].Value.Trim());

        string display = InferProviderNameFromUrl(baseUrl, "curl-import");
        string pid = DshCredentialManager.NormalizeProviderId(display);
        bool isAnthropic = normalized.Contains("x-api-key", StringComparison.OrdinalIgnoreCase)
                        || rawUrl.Contains("anthropic", StringComparison.OrdinalIgnoreCase)
                        || rawUrl.EndsWith("/messages", StringComparison.OrdinalIgnoreCase);

        return new ImportedProviderCandidate
        {
            Source = "cURL 命令",
            ProviderId = pid,
            DisplayName = display,
            EnvKey = DshCredentialManager.NormalizeEnvKey(pid + "_API_KEY"),
            Api = isAnthropic ? "anthropic-messages" : "openai-completions",
            BaseUrl = baseUrl,
            ApiKey = apiKey,
            Models = models,
            Note = "由 cURL 自动提取端点与令牌",
        };
    }

    private static List<ImportedProviderCandidate> TryParseEnvLines(string text)
    {
        var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
            if (line.StartsWith("export ", StringComparison.OrdinalIgnoreCase))
                line = line[7..].Trim();
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line[..eq].Trim();
            string val = line[(eq + 1)..].Trim().Trim('"', '\'');
            if (Regex.IsMatch(key, "^[A-Za-z_][A-Za-z0-9_]*$") && val.Length > 0)
                kv[key] = val;
        }

        var list = new List<ImportedProviderCandidate>();
        if (kv.Count == 0) return list;

        // 优先配对常见成组变量（如 ANTHROPIC_BASE_URL + ANTHROPIC_AUTH_TOKEN / OPENAI_BASE_URL + OPENAI_API_KEY / 通用 URL+API_KEY）
        var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        TryPairEnv(kv, consumed, list, "ANTHROPIC_BASE_URL", new[] { "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_API_KEY" }, "anthropic", "Claude Relay", "anthropic-messages");
        TryPairEnv(kv, consumed, list, "OPENAI_BASE_URL", new[] { "OPENAI_API_KEY" }, "openai-relay", "OpenAI Relay", "openai-completions");
        TryPairEnv(kv, consumed, list, "DEEPSEEK_BASE_URL", new[] { "DEEPSEEK_API_KEY" }, "deepseek", "DeepSeek", "openai-completions");
        TryPairEnv(kv, consumed, list, "GOOGLE_GEMINI_BASE_URL", new[] { "GEMINI_API_KEY" }, "gemini", "Gemini", "google-generative-ai");
        TryPairEnv(kv, consumed, list, "BASE_URL", new[] { "API_KEY", "AUTH_TOKEN", "KEY" }, "custom-relay", "Relay API", "openai-completions");
        TryPairEnv(kv, consumed, list, "API_BASE", new[] { "API_KEY", "AUTH_TOKEN", "KEY" }, "custom-relay", "Relay API", "openai-completions");
        TryPairEnv(kv, consumed, list, "URL", new[] { "API_KEY", "AUTH_TOKEN", "KEY" }, "custom-relay", "Relay API", "openai-completions");
        TryPairEnv(kv, consumed, list, "ENDPOINT", new[] { "API_KEY", "AUTH_TOKEN", "KEY" }, "custom-relay", "Relay API", "openai-completions");

        foreach (var (k, v) in kv)
        {
            if (consumed.Contains(k)) continue;
            if (k.EndsWith("_URL", StringComparison.OrdinalIgnoreCase)
                || k.Equals("URL", StringComparison.OrdinalIgnoreCase)
                || k.Equals("BASE_URL", StringComparison.OrdinalIgnoreCase)
                || k.Equals("API_BASE", StringComparison.OrdinalIgnoreCase)
                || k.Equals("ENDPOINT", StringComparison.OrdinalIgnoreCase)
                || k.Equals("HTTP_PROXY", StringComparison.OrdinalIgnoreCase)
                || k.Equals("HTTPS_PROXY", StringComparison.OrdinalIgnoreCase))
                continue;
            if (IsSubscriptionOrPlaceholderSecret(k, v)) continue;

            string pid = DshCredentialManager.NormalizeProviderId(
                Regex.Replace(k, "(_API_KEY|_KEY|_TOKEN)$", "", RegexOptions.IgnoreCase));
            list.Add(new ImportedProviderCandidate
            {
                Source = ".env 变量",
                ProviderId = pid,
                DisplayName = pid,
                EnvKey = DshCredentialManager.NormalizeEnvKey(k),
                Api = k.Contains("ANTHROPIC", StringComparison.OrdinalIgnoreCase) ? "anthropic-messages" : "openai-completions",
                BaseUrl = "",
                ApiKey = v,
            });
        }
        return list;
    }

    private static List<ImportedProviderCandidate> TryParseTomlBlock(string toml)
    {
        var list = new List<ImportedProviderCandidate>();
        string scoped = toml;
        var activeProvM = Regex.Match(toml, @"^\s*model_provider\s*=\s*['""]([^'""]+)['""]", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        if (activeProvM.Success)
        {
            string activeId = Regex.Escape(activeProvM.Groups[1].Value.Trim());
            var secM = Regex.Match(toml, $@"(?ms)^\[model_providers\.{activeId}\]\s*\r?\n(.*?)(?=^\[|\z)");
            if (secM.Success) scoped = secM.Groups[1].Value;
        }

        string baseUrl = "";
        var urlM = TomlBaseUrlRegex.Match(scoped);
        if (!urlM.Success) urlM = TomlBaseUrlRegex.Match(toml);
        if (urlM.Success) baseUrl = TrimChatPathFromUrl(urlM.Groups[1].Value);

        string apiKey = "";
        var keyM = TomlApiKeyRegex.Match(scoped);
        if (!keyM.Success) keyM = TomlApiKeyRegex.Match(toml);
        if (keyM.Success) apiKey = keyM.Groups[1].Value.Trim();

        string model = "";
        var modM = TomlModelRegex.Match(toml);
        if (modM.Success) model = modM.Groups[1].Value.Trim();

        string envKey = "OPENAI_API_KEY";
        var envM = Regex.Match(scoped, @"env_key\s*=\s*['""]([^'""]+)['""]", RegexOptions.IgnoreCase);
        if (!envM.Success) envM = Regex.Match(toml, @"env_key\s*=\s*['""]([^'""]+)['""]", RegexOptions.IgnoreCase);
        if (envM.Success) envKey = DshCredentialManager.NormalizeEnvKey(envM.Groups[1].Value);

        if (baseUrl.Length == 0 && apiKey.Length == 0) return list;
        string display = InferProviderNameFromUrl(baseUrl, "codex-toml");
        string pid = DshCredentialManager.NormalizeProviderId(display);
        list.Add(new ImportedProviderCandidate
        {
            Source = "TOML 配置",
            ProviderId = pid,
            DisplayName = display,
            EnvKey = envKey,
            Api = InferApiProtocol(null, baseUrl),
            BaseUrl = baseUrl,
            ApiKey = apiKey,
            Models = model.Length > 0 ? new[] { model } : Array.Empty<string>(),
        });
        return list;
    }

    /// <summary>
    /// 从自由格式的纯文本块（按空行分隔的 URL + API Key 段落，或全文单组/成对 URL 与 Key）中自动配对提取。
    /// 支持无标签粘贴（如第一行 URL、第二行 sk-...）与常见中英文标签（URL / 地址 / API Key / 密钥 / Model）。
    /// </summary>
    private static List<ImportedProviderCandidate> TryParseFreeformUrlAndKeyBlocks(string text)
    {
        var list = new List<ImportedProviderCandidate>();
        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        string[] blocks = Regex.Split(normalized, @"\n\s*\n+")
            .Select(b => b.Trim())
            .Where(b => b.Length > 0)
            .ToArray();

        if (blocks.Length <= 1)
        {
            ExtractFreeformFromSingleChunk(normalized, list);
            return list;
        }

        // 检查多段落是否各自包含独立配对；如果 URL 与 Key 分散在上下两段（如空行隔开 URL 与 Key），退回全文配对
        int blocksWithBoth = 0;
        foreach (string b in blocks)
        {
            if (CurlUrlRegex.IsMatch(b) && (BareProviderKeyRegex.IsMatch(b) || LabeledSecretRegex.IsMatch(b)))
                blocksWithBoth++;
        }
        if (blocksWithBoth > 0)
        {
            foreach (string b in blocks)
                ExtractFreeformFromSingleChunk(b, list);
        }
        else
        {
            ExtractFreeformFromSingleChunk(normalized, list);
        }
        return list;
    }

    private static void ExtractFreeformFromSingleChunk(string chunk, List<ImportedProviderCandidate> target)
    {
        var urls = CurlUrlRegex.Matches(chunk)
            .Select(m => TrimChatPathFromUrl(m.Value.Trim().TrimEnd(',', ';', ')', ']')))
            .Where(u => u.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var secrets = new List<string>();
        foreach (Match m in BareProviderKeyRegex.Matches(chunk))
        {
            if (!secrets.Contains(m.Value, StringComparer.Ordinal))
                secrets.Add(m.Value);
        }
        foreach (Match m in LabeledSecretRegex.Matches(chunk))
        {
            string val = m.Groups[1].Value.Trim();
            if (!val.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                && !IsSubscriptionOrPlaceholderSecret("", val)
                && !secrets.Contains(val, StringComparer.Ordinal))
            {
                secrets.Add(val);
            }
        }

        if (urls.Count == 0 || secrets.Count == 0) return;

        var models = new List<string>();
        var mm = Regex.Match(chunk, @"(?im)^(?:model|模型)\s*[:=：]\s*[""']?([A-Za-z0-9._\-/:]+)[""']?");
        if (mm.Success) models.Add(mm.Groups[1].Value.Trim());

        int count = Math.Max(urls.Count, secrets.Count);
        for (int i = 0; i < count; i++)
        {
            string url = urls[Math.Min(i, urls.Count - 1)];
            string key = secrets[Math.Min(i, secrets.Count - 1)];
            if (IsSubscriptionOrPlaceholderSecret("", key)) continue;

            bool isAnth = key.StartsWith("sk-ant-", StringComparison.Ordinal)
                       || url.Contains("anthropic", StringComparison.OrdinalIgnoreCase)
                       || url.Contains("claude", StringComparison.OrdinalIgnoreCase);
            bool isGem = key.StartsWith("AIza", StringComparison.Ordinal)
                      || url.Contains("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase)
                      || url.Contains("gemini", StringComparison.OrdinalIgnoreCase);

            string display = InferProviderNameFromUrl(url, "file-import");
            string pid = DshCredentialManager.NormalizeProviderId(display);
            string envKey = isGem ? "GEMINI_API_KEY"
                          : isAnth ? "ANTHROPIC_API_KEY"
                          : DshCredentialManager.NormalizeEnvKey(pid + "_API_KEY");
            string api = isGem ? "google-generative-ai"
                       : isAnth ? "anthropic-messages"
                       : "openai-completions";

            target.Add(new ImportedProviderCandidate
            {
                Source = "文件/文本自动读取",
                ProviderId = pid,
                DisplayName = display,
                EnvKey = envKey,
                Api = api,
                BaseUrl = url,
                ApiKey = key,
                Models = models,
                Note = "由 URL + API Key 自动配对识别",
            });
        }
    }

    private static void TryPairEnv(
        Dictionary<string, string> kv,
        HashSet<string> consumed,
        List<ImportedProviderCandidate> target,
        string urlKey,
        string[] secretKeys,
        string defaultId,
        string defaultDisplay,
        string api)
    {
        kv.TryGetValue(urlKey, out string? url);
        string? secKey = secretKeys.FirstOrDefault(kv.ContainsKey);
        if (secKey == null && string.IsNullOrWhiteSpace(url)) return;

        if (kv.ContainsKey(urlKey)) consumed.Add(urlKey);
        string secret = "";
        if (secKey != null)
        {
            secret = kv[secKey];
            consumed.Add(secKey);
        }
        string display = InferProviderNameFromUrl(url, defaultDisplay);
        target.Add(new ImportedProviderCandidate
        {
            Source = ".env 变量组",
            ProviderId = defaultId,
            DisplayName = display,
            EnvKey = secKey ?? DshCredentialManager.NormalizeEnvKey(defaultId + "_API_KEY"),
            Api = api,
            BaseUrl = (url ?? "").Trim(),
            ApiKey = secret,
        });
    }

    private static List<ImportedProviderCandidate> ParseCredentialsYamlForImport(string yaml)
    {
        var list = new List<ImportedProviderCandidate>();
        bool inRefs = false;
        foreach (string rawLine in yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string trim = rawLine.Trim();
            if (trim.Length == 0 || trim.StartsWith("#", StringComparison.Ordinal)) continue;
            if (rawLine[0] != ' ')
            {
                inRefs = trim.StartsWith("refs:", StringComparison.Ordinal);
                continue;
            }
            if (!inRefs) continue;
            if (!rawLine.StartsWith("  ", StringComparison.Ordinal) || rawLine.StartsWith("   ", StringComparison.Ordinal)) continue;
            int c = rawLine.IndexOf(':', 2);
            if (c <= 2) continue;
            string key = rawLine[2..c].Trim();
            string val = rawLine[(c + 1)..].Trim();
            if (val.Length >= 2 && ((val[0] == '"' && val[^1] == '"') || (val[0] == '\'' && val[^1] == '\'')))
                val = val[1..^1];
            if (val.Length == 0 || DshCredentialManager.IsAccountBlob(key, val)) continue;

            list.Add(new ImportedProviderCandidate
            {
                Source = "YAML 备份导入",
                ProviderId = DshCredentialManager.NormalizeProviderId(Regex.Replace(key, "_API_KEY$", "", RegexOptions.IgnoreCase)),
                DisplayName = key,
                EnvKey = DshCredentialManager.NormalizeEnvKey(key),
                ApiKey = val,
            });
        }
        return list;
    }

    private static string? GetStr(JsonElement obj, string prop)
        => obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(prop, out var el)
        && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static void ExtractModelsFromElement(JsonElement el, List<string> target)
    {
        if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                    target.Add(item.GetString()!.Trim());
                else if (item.ValueKind == JsonValueKind.Object)
                {
                    string? id = GetStr(item, "id") ?? GetStr(item, "name");
                    if (!string.IsNullOrWhiteSpace(id)) target.Add(id!.Trim());
                }
            }
        }
        else if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in el.EnumerateObject())
            {
                if (!string.IsNullOrWhiteSpace(prop.Name)) target.Add(prop.Name.Trim());
            }
        }
    }

    private static Dictionary<string, string> ParseQueryString(string query)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string q = query.TrimStart('?');
        if (q.Length == 0) return dict;
        foreach (string pair in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            string k = Uri.UnescapeDataString(pair[..eq].Replace('+', ' '));
            string v = Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            dict[k] = v;
        }
        return dict;
    }

    private static string? FirstNonEmpty(Dictionary<string, string> dict, params string[] keys)
    {
        foreach (string k in keys)
        {
            if (dict.TryGetValue(k, out string? v) && !string.IsNullOrWhiteSpace(v))
                return v;
        }
        return null;
    }

    private static string DecodeBase64Flexible(string raw)
    {
        string s = raw.Trim().Replace('-', '+').Replace('_', '/');
        int mod = s.Length % 4;
        if (mod > 0) s = s.PadRight(s.Length + (4 - mod), '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(s));
    }

    private static string InferApiProtocol(string? protocolOrApp, string? url)
    {
        string p = (protocolOrApp ?? "").Trim().ToLowerInvariant();
        if (p.Contains("anthropic", StringComparison.Ordinal) || p == "claude") return "anthropic-messages";
        if (p.Contains("gemini", StringComparison.Ordinal) || p.Contains("google-gen", StringComparison.Ordinal)) return "google-generative-ai";
        if (p.Contains("openai", StringComparison.Ordinal) || p == "codex") return "openai-completions";
        if ((url ?? "").Contains("anthropic", StringComparison.OrdinalIgnoreCase)) return "anthropic-messages";
        return "openai-completions";
    }

    private static string InferProviderNameFromUrl(string? url, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var u))
        {
            string host = u.Host;
            if (host.StartsWith("api.", StringComparison.OrdinalIgnoreCase)) host = host[4..];
            int dot = host.IndexOf('.');
            if (dot > 1) return host[..dot];
            if (host.Length > 0) return host;
        }
        return fallback;
    }

    private static string TrimChatPathFromUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        string u = url.Trim().TrimEnd('/');
        foreach (string suffix in new[] { "/chat/completions", "/responses", "/messages", "/embeddings" })
        {
            if (u.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return u[..^suffix.Length];
        }
        return u;
    }

    private static List<string> SplitCsvModels(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return new List<string>();
        return csv.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}
