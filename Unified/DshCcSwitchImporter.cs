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

/// <summary>一条来自 CC Switch / 剪贴板 / 深链 / 外部文件的待导入模型提供商或凭据候选。</summary>
public sealed class ImportedProviderCandidate
{
    public string Source { get; init; } = "";        // CC Switch (dsh/claude/codex/gemini) / ccswitch:// / 剪贴板 / 文件
    public string AppType { get; init; } = "";
    public string ProviderId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string EnvKey { get; init; } = "";
    public string Api { get; init; } = "openai-completions";
    public string BaseUrl { get; init; } = "";
    public string ApiKey { get; init; } = "";        // 仅在内存供导入写盘；界面一律用 MaskedKey
    public string MaskedKey => DshCredentialManager.MaskSecret(ApiKey);
    public IReadOnlyList<string> Models { get; init; } = Array.Empty<string>();
    public bool IsCurrent { get; init; }
    public string Note { get; init; } = "";
}

/// <summary>SQLite 或文本导入扫描的汇总报告。</summary>
public sealed class CcSwitchScanReport
{
    public string SourcePath { get; init; } = "";
    public bool Available { get; init; }
    public string Summary { get; init; } = "";
    public string Error { get; init; } = "";
    public IReadOnlyList<ImportedProviderCandidate> Candidates { get; init; } = Array.Empty<ImportedProviderCandidate>();
}

/// <summary>
/// 2.4.0 多源一键导入器（对标 CC Switch）：
///   1. 本机 SQLite 直连只读（经 Windows 内置 <c>System32\winsqlite3.dll</c> 读 <c>cc-switch.db</c>）；
///   2. <c>ccswitch://</c> 深度链接解析；
///   3. 剪贴板 / 文本智能提取（JSON、curl 命令、.env、裸 sk- 密钥）；
///   4. 外部文件导入（.db / .sqlite / .yaml / .bak / .env / .json / .txt）。
/// </summary>
public static class DshCcSwitchImporter
{
    private const int SqliteOk = 0;
    private const int SqliteRow = 100;
    private const int SqliteOpenReadOnly = 0x00000001;

    private static readonly Regex BareSkRegex = new(@"\bsk-[A-Za-z0-9_\-]{16,160}\b", RegexOptions.Compiled);
    private static readonly Regex CurlUrlRegex = new(@"https?://[^\s'""\\]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TomlBaseUrlRegex = new(@"base_url\s*=\s*['""]([^'""]+)['""]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
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

    // ══════════════ 剪贴板 / 文本智能识别 ══════════════

    /// <summary>智能解析剪贴板或文本中的 ccswitch://、JSON、curl 命令、.env 键值对或裸 sk- 密钥。</summary>
    public static IReadOnlyList<ImportedProviderCandidate> ParseSmartText(string? rawText)
    {
        var results = new List<ImportedProviderCandidate>();
        if (string.IsNullOrWhiteSpace(rawText)) return results;
        string text = rawText.Trim();

        // 1. 提取文本中的所有 ccswitch:// 链接
        foreach (Match m in Regex.Matches(text, @"ccswitch://[^\s""'<>]+", RegexOptions.IgnoreCase))
            results.AddRange(ParseCcSwitchDeepLink(m.Value));
        if (results.Count > 0) return results;

        // 2. JSON 块（单个提供商 / 凭据字典 / CC Switch 导出）
        if (text.StartsWith("{", StringComparison.Ordinal) || text.StartsWith("[", StringComparison.Ordinal))
        {
            var fromJson = TryParseJsonBlock(text);
            if (fromJson.Count > 0) return fromJson;
        }

        // 3. curl 命令
        if (text.Contains("curl ", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("curl", StringComparison.OrdinalIgnoreCase))
        {
            var fromCurl = TryParseCurlCommand(text);
            if (fromCurl != null)
            {
                results.Add(fromCurl);
                return results;
            }
        }

        // 4. .env / KEY=VALUE 多行（兼容 Anthropic / OpenAI / DeepSeek / 自定义 KEY）
        var fromEnv = TryParseEnvLines(text);
        if (fromEnv.Count > 0) return fromEnv;

        // 5. 裸 API Key（sk-... 等）
        var skMatch = BareSkRegex.Match(text);
        if (skMatch.Success)
        {
            results.Add(new ImportedProviderCandidate
            {
                Source = "裸密钥识别",
                ProviderId = "custom-key",
                DisplayName = "识别到的 API Key",
                EnvKey = "CUSTOM_API_KEY",
                Api = "openai-completions",
                BaseUrl = "",
                ApiKey = skMatch.Value,
                Note = "可直接在下方改名或搭配预设使用",
            });
        }

        return results;
    }

    // ══════════════ 外部文件导入 ══════════════

    /// <summary>按扩展名智能导入 .db / .sqlite / .yaml / .bak / .env / .json / .txt 文件。</summary>
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

            string content = File.ReadAllText(filePath, Encoding.UTF8);
            var list = new List<ImportedProviderCandidate>();

            if (ext is ".yaml" or ".yml" || filePath.Contains(".credentials.yaml", StringComparison.OrdinalIgnoreCase)
                || filePath.Contains(".bak-", StringComparison.OrdinalIgnoreCase))
            {
                // 提取 YAML 中的普通 refs 密钥（严格跳过 *_ACCOUNT_*）
                foreach (var c in ParseCredentialsYamlForImport(content))
                    list.Add(c);
            }

            if (list.Count == 0)
                list.AddRange(ParseSmartText(content));

            return new CcSwitchScanReport
            {
                SourcePath = filePath,
                Available = true,
                Summary = list.Count > 0 ? $"从文件解析出 {list.Count} 条候选配置" : "文件中未识别到可导入的 API Key 或提供商配置",
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

        // 优先配对常见成组变量（如 ANTHROPIC_BASE_URL + ANTHROPIC_AUTH_TOKEN / OPENAI_BASE_URL + OPENAI_API_KEY）
        var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        TryPairEnv(kv, consumed, list, "ANTHROPIC_BASE_URL", new[] { "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_API_KEY" }, "anthropic", "Claude Relay", "anthropic-messages");
        TryPairEnv(kv, consumed, list, "OPENAI_BASE_URL", new[] { "OPENAI_API_KEY" }, "openai-relay", "OpenAI Relay", "openai-completions");
        TryPairEnv(kv, consumed, list, "DEEPSEEK_BASE_URL", new[] { "DEEPSEEK_API_KEY" }, "deepseek", "DeepSeek", "openai-completions");
        TryPairEnv(kv, consumed, list, "GOOGLE_GEMINI_BASE_URL", new[] { "GEMINI_API_KEY" }, "gemini", "Gemini", "google-generative-ai");

        foreach (var (k, v) in kv)
        {
            if (consumed.Contains(k)) continue;
            if (k.EndsWith("_URL", StringComparison.OrdinalIgnoreCase) || k.Equals("HTTP_PROXY", StringComparison.OrdinalIgnoreCase))
                continue;
            if (DshCredentialManager.IsAccountBlob(k, v)) continue;

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
