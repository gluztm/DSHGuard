#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DSHGuard.Unified;

/// <summary>一条已收纳在 <c>.credentials.yaml</c> 的凭据或账号令牌条目。</summary>
public sealed class DshCredentialEntry
{
    public string Key { get; init; } = "";
    public string MaskedValue { get; init; } = "";
    public bool HasValue { get; init; }
    public bool IsReadOnlyAccount { get; init; }
    public string SourceLabel { get; init; } = "";
    public int LineCount { get; init; } = 1;
    public int StartLine { get; init; } = -1;
    public int EndLine { get; init; } = -1;
    public IReadOnlyList<string> LinkedProviders { get; init; } = Array.Empty<string>();
}

/// <summary>凭据仓扫描结果。</summary>
public sealed class DshCredentialScanResult
{
    public string FilePath { get; init; } = "";
    public bool Exists { get; init; }
    public string Version { get; init; } = "1";
    public int RecordsCount { get; init; }
    public IReadOnlyList<DshCredentialEntry> Entries { get; init; } = Array.Empty<DshCredentialEntry>();
    public string Error { get; init; } = "";
}

/// <summary>某个 DSH profile 里对单一 LLM 提供商的配置视图。</summary>
public sealed class DshProviderProfileState
{
    public string ProfileId { get; init; } = "";
    public string ProfileName { get; init; } = "";
    public string PatchFile { get; init; } = "";
    public bool Configured { get; init; }
    public string ProviderId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string ApiKeyEnv { get; init; } = "";
    public string Api { get; init; } = "";
    public string BaseUrl { get; init; } = "";
    public IReadOnlyList<string> Models { get; init; } = Array.Empty<string>();
}

/// <summary>跨 DSH profile 聚合后的模型提供商实体。</summary>
public sealed class DshLlmProvider
{
    public string ProviderId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string ApiKeyEnv { get; init; } = "";
    public string Api { get; init; } = "openai-completions";
    public string BaseUrl { get; init; } = "";
    public IReadOnlyList<string> Models { get; init; } = Array.Empty<string>();
    public bool CredentialReady { get; init; }
    public string CredentialMasked { get; init; } = "";
    public IReadOnlyDictionary<string, DshProviderProfileState> Profiles { get; init; }
        = new Dictionary<string, DshProviderProfileState>(StringComparer.OrdinalIgnoreCase);

    public bool EnabledIn(string profileId)
        => Profiles.TryGetValue(profileId, out var st) && st.Configured;
}

/// <summary>写入或编辑一条模型提供商时的表单草稿。</summary>
public sealed class DshProviderDraft
{
    public string ProviderId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string ApiKeyEnv { get; init; } = "";
    public string Api { get; init; } = "openai-completions";
    public string BaseUrl { get; init; } = "";
    public IReadOnlyList<string> Models { get; init; } = Array.Empty<string>();
}

/// <summary>内置一键预设模型提供商模板。</summary>
public sealed record DshProviderPreset(
    string PresetId,
    string Label,
    string ProviderId,
    string DisplayName,
    string EnvKey,
    string Api,
    string BaseUrl,
    IReadOnlyList<string> Models,
    string Note);

/// <summary>凭据或模型提供商落盘结果。</summary>
public sealed record DshCredentialWriteResult(bool Ok, string Message, string BackupPath = "");

/// <summary>
/// 2.4.0 CC Switch 风格凭据收纳与模型提供商跨端管理器：
///   1. <c>~/.dsh/.credentials.yaml</c> 无损读写：严守 <c>version: 1</c> 与 <c>records:</c> 授权段落，
///      对 <c>*_ACCOUNT_*</c> 与多行 JSON 账号态令牌实行只读保护，写入前留 <c>.bak-cred-*</c> 备份并回读校验；
///   2. DSH 各 profile（web / desktop / headless）的 <c>cordis.patch.yml</c> 中 <c>llm-pi-ai</c> 无损投影；
///   3. 脱敏展示与 <c>/models</c> 端点在线探测。
/// </summary>
public static class DshCredentialManager
{
    private const int MaxBackupsPerFile = 10;

    private static readonly Regex ValidEnvKeyRegex = new("^[A-Za-z_][A-Za-z0-9_]{0,95}$", RegexOptions.Compiled);
    private static readonly Regex ValidProviderIdRegex = new("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$", RegexOptions.Compiled);

    private static readonly HttpClient ProbeHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    /// <summary>8 套常用模型与生图插件一键预设。</summary>
    public static IReadOnlyList<DshProviderPreset> BuiltInPresets { get; } = new[]
    {
        new DshProviderPreset("deepseek", "DeepSeek 官方", "deepseek", "DeepSeek",
            "DEEPSEEK_API_KEY", "openai-completions", "https://api.deepseek.com/v1",
            new[] { "deepseek-chat", "deepseek-reasoner" },
            "DeepSeek 官方 OpenAI 兼容接口"),
        new DshProviderPreset("openai", "OpenAI / 通用中继", "openai-relay", "OpenAI Relay",
            "OPENAI_API_KEY", "openai-completions", "https://api.openai.com/v1",
            new[] { "gpt-4o", "o3-mini" },
            "OpenAI 官方或兼容 /v1 中继站"),
        new DshProviderPreset("anthropic", "Anthropic Claude", "anthropic", "Claude",
            "ANTHROPIC_API_KEY", "anthropic-messages", "https://api.anthropic.com",
            new[] { "claude-sonnet-4-5", "claude-opus-4-5" },
            "Anthropic 官方或兼容 Messages API 中继"),
        new DshProviderPreset("gemini", "Google Gemini 兼容", "gemini", "Gemini",
            "GEMINI_API_KEY", "openai-completions", "https://generativelanguage.googleapis.com/v1beta/openai",
            new[] { "gemini-2.5-pro", "gemini-2.5-flash" },
            "Gemini OpenAI 兼容端点"),
        new DshProviderPreset("openrouter", "OpenRouter", "openrouter", "OpenRouter",
            "OPENROUTER_API_KEY", "openai-completions", "https://openrouter.ai/api/v1",
            new[] { "anthropic/claude-sonnet-4", "deepseek/deepseek-chat" },
            "多模型聚合网关 OpenRouter"),
        new DshProviderPreset("siliconflow", "硅基流动 SiliconFlow", "siliconflow", "SiliconFlow",
            "SILICONFLOW_API_KEY", "openai-completions", "https://api.siliconflow.cn/v1",
            new[] { "deepseek-ai/DeepSeek-V3", "Qwen/Qwen2.5-72B-Instruct" },
            "国内高性价比模型平台"),
        new DshProviderPreset("stepfun", "阶跃星辰 StepFun", "stepfun", "StepFun",
            "STEPFUN_API_KEY", "openai-completions", "https://api.stepfun.com/v1",
            new[] { "step-2-16k", "step-1-flash" },
            "StepFun 对话与识图预设（兼容生图/视觉环境变量）"),
        new DshProviderPreset("yomi-image", "生图插件 Yomi Key", "yomi-image", "Yomi Image Compat",
            "DSH_IMAGE_GEN_OPENAI_COMPAT_KEY", "openai-completions", "https://api.openai.com/v1",
            new[] { "gpt-image-1" },
            "供 dsh-image-gen 生图插件复用的 OpenAI 兼容密钥"),
    };

    /// <summary>将密钥做严格脱敏（UI 与日志绝不输出完整明文）。</summary>
    public static string MaskSecret(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "（未配置）";
        string v = UnquoteScalar(raw.Trim());
        if (v.Length == 0) return "（未配置）";
        if (v.Length <= 4) return "••••";
        if (v.Length <= 8) return v[..2] + "••••";
        if (v.Length <= 14) return v[..3] + "••••" + v[^2..];
        return v[..6] + "••••••" + v[^4..];
    }

    /// <summary>识别是否为 DSH 多端登录账号态 JSON/OAuth 令牌（受只读保护，禁止被当作 API Key 覆盖或删掉）。</summary>
    internal static bool IsAccountBlob(string? key, string? rawValue)
    {
        string k = (key ?? "").Trim();
        if (k.Contains("_ACCOUNT_", StringComparison.OrdinalIgnoreCase)) return true;
        string v = UnquoteScalar((rawValue ?? "").Trim());
        if (v.Contains('\n') || v.Contains('\r')) return true;
        return v.StartsWith("{", StringComparison.Ordinal) && v.Contains("\"", StringComparison.Ordinal);
    }

    /// <summary>规范化为合规环境变量键名。</summary>
    public static string NormalizeEnvKey(string? raw)
    {
        string s = (raw ?? "").Trim().ToUpperInvariant();
        var sb = new StringBuilder(s.Length);
        foreach (char ch in s)
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch == '_') sb.Append(ch);
            else if (ch is '-' or ' ' or '.') sb.Append('_');
        }
        string cleaned = Regex.Replace(sb.ToString(), "_+", "_").Trim('_');
        if (cleaned.Length == 0) return "CUSTOM_API_KEY";
        if (char.IsAsciiDigit(cleaned[0])) cleaned = "KEY_" + cleaned;
        return cleaned.Length > 80 ? cleaned[..80] : cleaned;
    }

    /// <summary>规范化为合规 Provider ID（kebab-case）。</summary>
    public static string NormalizeProviderId(string? raw)
    {
        string s = (raw ?? "").Trim().ToLowerInvariant();
        var sb = new StringBuilder(s.Length);
        foreach (char ch in s)
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch == '-' || ch == '_') sb.Append(ch);
            else if (ch is ' ' or '.' or '/') sb.Append('-');
        }
        string cleaned = Regex.Replace(sb.ToString(), "-+", "-").Trim('-', '_');
        if (cleaned.Length == 0) return "custom-provider";
        return cleaned.Length > 48 ? cleaned[..48] : cleaned;
    }

    /// <summary>在夹具模式（<see cref="GuardPaths.FixtureDefaultRoot"/> 非空）下，确保所有 profile 都落进测试沙盒目录。</summary>
    internal static IReadOnlyList<DshProfileInfo> ResolveProfiles(IReadOnlyList<DshProfileInfo>? overrideProfiles = null)
    {
        if (overrideProfiles != null) return overrideProfiles;
        string? fixture = GuardPaths.FixtureDefaultRoot;
        if (!string.IsNullOrWhiteSpace(fixture))
        {
            string root = fixture!.Trim();
            return new[]
            {
                new DshProfileInfo(DshProfileKind.Web, "web", "Web 版", "\uE774",
                    () => Path.Combine(root, "profiles", "web"),
                    () => Path.Combine(root, "profiles", "web", "cordis.patch.yml"),
                    Editable: true),
                new DshProfileInfo(DshProfileKind.Desktop, "desktop", "桌面版", "\uE7F4",
                    () => Path.Combine(root, "profiles", "desktop"),
                    () => Path.Combine(root, "profiles", "desktop", "cordis.patch.yml"),
                    Editable: true),
                new DshProfileInfo(DshProfileKind.Headless, "headless", "无头", "\uE756",
                    () => Path.Combine(root, "profiles", "headless"),
                    () => Path.Combine(root, "profiles", "headless", "cordis.patch.yml"),
                    Editable: true),
            };
        }
        return DshProfileRegistry.Registry;
    }

    // ══════════════ .credentials.yaml 无损扫描与写入 ══════════════

    private sealed record RawRefSpan(string Key, string UnquotedValue, int StartLine, int EndLine, bool IsAccount);

    public static DshCredentialScanResult ScanCredentials(string? pathOverride = null)
    {
        string path = string.IsNullOrWhiteSpace(pathOverride) ? GuardPaths.CredentialsFile : pathOverride!;
        if (!File.Exists(path))
        {
            return new DshCredentialScanResult
            {
                FilePath = path,
                Exists = false,
                Version = "1",
            };
        }

        try
        {
            string text = File.ReadAllText(path, Encoding.UTF8);
            var parsed = ParseCredentialsDocument(text);
            var entries = new List<DshCredentialEntry>(parsed.Refs.Count);
            foreach (var r in parsed.Refs)
            {
                entries.Add(new DshCredentialEntry
                {
                    Key = r.Key,
                    MaskedValue = r.IsAccount ? "账号 JSON 令牌（受保护）" : MaskSecret(r.UnquotedValue),
                    HasValue = r.UnquotedValue.Length > 0,
                    IsReadOnlyAccount = r.IsAccount,
                    SourceLabel = r.IsAccount ? "账号态令牌 · 只读" : "环境变量凭据",
                    LineCount = r.EndLine - r.StartLine + 1,
                    StartLine = r.StartLine,
                    EndLine = r.EndLine,
                });
            }

            return new DshCredentialScanResult
            {
                FilePath = path,
                Exists = true,
                Version = parsed.Version,
                RecordsCount = parsed.RecordsCount,
                Entries = entries,
            };
        }
        catch (Exception ex)
        {
            Logger.LogError("DshCredentialManager.ScanCredentials", ex);
            return new DshCredentialScanResult
            {
                FilePath = path,
                Exists = true,
                Error = ex.Message,
            };
        }
    }

    internal static string? TryReadRawSecret(string key, string? pathOverride = null)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        string path = string.IsNullOrWhiteSpace(pathOverride) ? GuardPaths.CredentialsFile : pathOverride!;
        if (!File.Exists(path)) return null;
        try
        {
            string text = File.ReadAllText(path, Encoding.UTF8);
            var parsed = ParseCredentialsDocument(text);
            var hit = parsed.Refs.FirstOrDefault(r => string.Equals(r.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));
            return hit?.UnquotedValue;
        }
        catch
        {
            return null;
        }
    }

    public static DshCredentialWriteResult UpsertCredentialRef(string key, string secret, string? pathOverride = null)
    {
        string k = (key ?? "").Trim();
        if (!ValidEnvKeyRegex.IsMatch(k))
            return new DshCredentialWriteResult(false, "凭据键名必须符合环境变量格式（字母/下划线开头，仅含字母数字下划线）");
        if (string.IsNullOrWhiteSpace(secret))
            return new DshCredentialWriteResult(false, "API 密钥不能为空");
        string trimmedSecret = secret.Trim();
        if (IsAccountBlob(k, trimmedSecret))
            return new DshCredentialWriteResult(false, "检测到账号态 JSON/OAuth 令牌（*_ACCOUNT_*），受只读保护拒绝写入");

        string path = string.IsNullOrWhiteSpace(pathOverride) ? GuardPaths.CredentialsFile : pathOverride!;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? GuardPaths.DshHome);

            if (!File.Exists(path))
            {
                string fresh = "version: 1\nrecords: []\nrefs:\n  " + k + ": " + YamlDoubleQuoted(trimmedSecret) + "\n";
                AtomicFile.WriteAllText(path, fresh, new UTF8Encoding(false), keepBackup: false);
                var verifyFresh = ScanCredentials(path);
                bool okFresh = verifyFresh.Entries.Any(e => string.Equals(e.Key, k, StringComparison.OrdinalIgnoreCase) && e.HasValue);
                return okFresh
                    ? new DshCredentialWriteResult(true, $"已新建凭据仓并写入 {k}（{MaskSecret(trimmedSecret)}）")
                    : new DshCredentialWriteResult(false, "写入后磁盘回读未见到该凭据，请检查权限");
            }

            string original = File.ReadAllText(path, Encoding.UTF8);
            string eol = DetectEol(original);
            var parsed = ParseCredentialsDocument(original);

            var existing = parsed.Refs.FirstOrDefault(r => string.Equals(r.Key, k, StringComparison.OrdinalIgnoreCase));
            if (existing != null && existing.IsAccount)
                return new DshCredentialWriteResult(false, $"条目 {existing.Key} 是多端登录账号态令牌，受只读保护不可覆盖");

            string backup = CreateBackup(path, "cred");
            var lines = SplitLinesPreserveTrailing(original, out bool hadTrailingNewline);
            string newLine = "  " + k + ": " + YamlDoubleQuoted(trimmedSecret);

            if (existing != null)
            {
                int removeCount = existing.EndLine - existing.StartLine + 1;
                lines.RemoveRange(existing.StartLine, removeCount);
                lines.Insert(existing.StartLine, newLine);
            }
            else if (parsed.RefsHeaderLine >= 0)
            {
                string headerTrimmed = lines[parsed.RefsHeaderLine].Trim();
                if (headerTrimmed.Equals("refs: {}", StringComparison.Ordinal)
                    || headerTrimmed.Equals("refs: null", StringComparison.Ordinal))
                {
                    lines[parsed.RefsHeaderLine] = "refs:";
                }
                int insertAt = parsed.RefsEndLine + 1;
                if (insertAt > lines.Count) insertAt = lines.Count;
                lines.Insert(insertAt, newLine);
            }
            else
            {
                if (lines.Count > 0 && lines[^1].Length > 0) lines.Add("");
                lines.Add("refs:");
                lines.Add(newLine);
            }

            string output = JoinLines(lines, eol, hadTrailingNewline || true);
            AtomicFile.WriteAllText(path, output, new UTF8Encoding(false), keepBackup: false);

            var recheck = ScanCredentials(path);
            bool persisted = recheck.Entries.Any(e => string.Equals(e.Key, k, StringComparison.OrdinalIgnoreCase) && e.HasValue);
            return persisted
                ? new DshCredentialWriteResult(true, $"已收纳 {k}（{MaskSecret(trimmedSecret)}）", backup)
                : new DshCredentialWriteResult(false, $"写入后磁盘回读校验失败，备份位于：{Path.GetFileName(backup)}", backup);
        }
        catch (Exception ex)
        {
            Logger.LogError("DshCredentialManager.UpsertCredentialRef", ex);
            return new DshCredentialWriteResult(false, "保存凭据失败：" + ex.Message);
        }
    }

    public static DshCredentialWriteResult DeleteCredentialRef(string key, string? pathOverride = null)
    {
        string k = (key ?? "").Trim();
        if (k.Length == 0) return new DshCredentialWriteResult(false, "键名为空");

        string path = string.IsNullOrWhiteSpace(pathOverride) ? GuardPaths.CredentialsFile : pathOverride!;
        if (!File.Exists(path)) return new DshCredentialWriteResult(false, "凭据文件不存在");

        try
        {
            string original = File.ReadAllText(path, Encoding.UTF8);
            string eol = DetectEol(original);
            var parsed = ParseCredentialsDocument(original);
            var existing = parsed.Refs.FirstOrDefault(r => string.Equals(r.Key, k, StringComparison.OrdinalIgnoreCase));
            if (existing == null) return new DshCredentialWriteResult(false, $"未找到凭据 {k}");
            if (existing.IsAccount) return new DshCredentialWriteResult(false, $"条目 {existing.Key} 是账号态令牌，受只读保护不可删除");

            string backup = CreateBackup(path, "cred");
            var lines = SplitLinesPreserveTrailing(original, out bool hadTrailingNewline);
            lines.RemoveRange(existing.StartLine, existing.EndLine - existing.StartLine + 1);

            string output = JoinLines(lines, eol, hadTrailingNewline);
            AtomicFile.WriteAllText(path, output, new UTF8Encoding(false), keepBackup: false);

            var recheck = ScanCredentials(path);
            bool gone = recheck.Entries.All(e => !string.Equals(e.Key, k, StringComparison.OrdinalIgnoreCase));
            return gone
                ? new DshCredentialWriteResult(true, $"已删除 {existing.Key}", backup)
                : new DshCredentialWriteResult(false, "删除后仍读到该条目，请检查备份", backup);
        }
        catch (Exception ex)
        {
            Logger.LogError("DshCredentialManager.DeleteCredentialRef", ex);
            return new DshCredentialWriteResult(false, "删除失败：" + ex.Message);
        }
    }

    private sealed record ParsedCredDoc(
        string Version,
        int RecordsCount,
        int RefsHeaderLine,
        int RefsEndLine,
        List<RawRefSpan> Refs);

    private static ParsedCredDoc ParseCredentialsDocument(string text)
    {
        var lines = SplitLinesPreserveTrailing(text, out _);
        string version = "1";
        int recordsCount = 0;
        int refsHeaderLine = -1;
        int refsSectionEnd = -1;
        bool inRecords = false;
        bool inRefs = false;
        var refs = new List<RawRefSpan>();

        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
                continue;

            int indent = IndentOf(line);
            if (indent == 0 && !trimmed.StartsWith("-", StringComparison.Ordinal))
            {
                if (inRefs && refsSectionEnd < i - 1) refsSectionEnd = i - 1;
                inRecords = false;
                inRefs = false;

                if (trimmed.StartsWith("version:", StringComparison.Ordinal))
                {
                    version = UnquoteScalar(trimmed["version:".Length..].Trim());
                }
                else if (trimmed.StartsWith("records:", StringComparison.Ordinal))
                {
                    inRecords = true;
                }
                else if (trimmed.StartsWith("refs:", StringComparison.Ordinal))
                {
                    inRefs = true;
                    refsHeaderLine = i;
                    refsSectionEnd = i;
                }
                continue;
            }

            if (inRecords && indent <= 4 && trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                recordsCount++;
                continue;
            }

            if (inRefs && indent == 2 && !trimmed.StartsWith("-", StringComparison.Ordinal))
            {
                int colon = line.IndexOf(':', 2);
                if (colon > 2)
                {
                    string key = line[2..colon].Trim();
                    string firstRhs = line[(colon + 1)..].Trim();
                    int endLine = i;
                    var rawValBuilder = new StringBuilder(firstRhs);

                    for (int j = i + 1; j < lines.Count; j++)
                    {
                        string next = lines[j];
                        string nextTrim = next.Trim();
                        if (nextTrim.Length == 0)
                        {
                            // 若后续立即回到顶层或同级新键，空行不属于当前块
                            bool moreIndented = false;
                            for (int k = j + 1; k < lines.Count; k++)
                            {
                                string probe = lines[k];
                                if (string.IsNullOrWhiteSpace(probe)) continue;
                                moreIndented = IndentOf(probe) > 2;
                                break;
                            }
                            if (!moreIndented) break;
                            endLine = j;
                            rawValBuilder.Append('\n');
                            continue;
                        }
                        if (IndentOf(next) <= 2) break;
                        endLine = j;
                        rawValBuilder.Append('\n').Append(next.TrimStart());
                    }

                    refsSectionEnd = Math.Max(refsSectionEnd, endLine);
                    string rawVal = rawValBuilder.ToString();
                    string unquoted = UnquoteScalar(rawVal);
                    refs.Add(new RawRefSpan(key, unquoted, i, endLine, IsAccountBlob(key, unquoted)));
                    i = endLine;
                }
            }
            else if (inRefs)
            {
                refsSectionEnd = i;
            }
        }

        if (inRefs && refsSectionEnd < refsHeaderLine) refsSectionEnd = refsHeaderLine;
        return new ParsedCredDoc(version, recordsCount, refsHeaderLine, refsSectionEnd, refs);
    }

    // ══════════════ cordis.patch.yml (llm-pi-ai) 跨端扫描与投影 ══════════════

    public static IReadOnlyList<DshLlmProvider> ScanProviders(
        IReadOnlyList<DshProfileInfo>? profilesOverride = null,
        string? credentialsPathOverride = null)
    {
        var profiles = ResolveProfiles(profilesOverride);
        var creds = ScanCredentials(credentialsPathOverride);
        var credLookup = new Dictionary<string, DshCredentialEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in creds.Entries)
        {
            if (!credLookup.ContainsKey(entry.Key)) credLookup[entry.Key] = entry;
        }

        var byProvider = new Dictionary<string, Dictionary<string, DshProviderProfileState>>(StringComparer.OrdinalIgnoreCase);

        foreach (var profile in profiles)
        {
            string patchPath = profile.PatchFile();
            if (!File.Exists(patchPath)) continue;

            var parsedList = ParsePatchProviders(patchPath);
            foreach (var item in parsedList)
            {
                if (!byProvider.TryGetValue(item.ProviderId, out var map))
                {
                    map = new Dictionary<string, DshProviderProfileState>(StringComparer.OrdinalIgnoreCase);
                    byProvider[item.ProviderId] = map;
                }
                map[profile.Id] = new DshProviderProfileState
                {
                    ProfileId = profile.Id,
                    ProfileName = profile.DisplayName,
                    PatchFile = patchPath,
                    Configured = true,
                    ProviderId = item.ProviderId,
                    DisplayName = item.DisplayName,
                    ApiKeyEnv = item.ApiKeyEnv,
                    Api = item.Api,
                    BaseUrl = item.BaseUrl,
                    Models = item.Models,
                };
            }
        }

        var result = new List<DshLlmProvider>(byProvider.Count);
        foreach (var (providerId, states) in byProvider)
        {
            // 展示元信息优先取 web -> desktop -> 任意可用 profile
            DshProviderProfileState primary = states.TryGetValue("web", out var w) ? w
                : states.TryGetValue("desktop", out var d) ? d
                : states.Values.First();

            string envKey = primary.ApiKeyEnv;
            bool credOk = envKey.Length > 0
                && credLookup.TryGetValue(envKey, out var ce)
                && ce.HasValue;
            string masked = envKey.Length > 0 && credLookup.TryGetValue(envKey, out var ce2)
                ? ce2.MaskedValue
                : "（凭据库未收录）";

            var mergedModels = states.Values
                .SelectMany(s => s.Models)
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var fullProfiles = new Dictionary<string, DshProviderProfileState>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in profiles)
            {
                fullProfiles[p.Id] = states.TryGetValue(p.Id, out var found)
                    ? found
                    : new DshProviderProfileState
                    {
                        ProfileId = p.Id,
                        ProfileName = p.DisplayName,
                        PatchFile = p.PatchFile(),
                        Configured = false,
                        ProviderId = providerId,
                    };
            }

            result.Add(new DshLlmProvider
            {
                ProviderId = providerId,
                DisplayName = string.IsNullOrWhiteSpace(primary.DisplayName) ? providerId : primary.DisplayName,
                ApiKeyEnv = envKey,
                Api = string.IsNullOrWhiteSpace(primary.Api) ? "openai-completions" : primary.Api,
                BaseUrl = primary.BaseUrl,
                Models = mergedModels,
                CredentialReady = credOk,
                CredentialMasked = masked,
                Profiles = fullProfiles,
            });
        }

        return result.OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static DshCredentialWriteResult ApplyProviderToProfile(DshProfileInfo profile, DshProviderDraft draft)
    {
        if (profile == null) return new DshCredentialWriteResult(false, "未指定目标端");
        string pid = NormalizeProviderId(draft.ProviderId);
        if (!ValidProviderIdRegex.IsMatch(pid))
            return new DshCredentialWriteResult(false, "Provider ID 仅支持字母、数字、连字符和下划线");
        string envKey = (draft.ApiKeyEnv ?? "").Trim();
        if (!ValidEnvKeyRegex.IsMatch(envKey))
            return new DshCredentialWriteResult(false, "apiKeyEnv 必须是合法的环境变量名（如 DEEPSEEK_API_KEY）");
        string baseUrl = (draft.BaseUrl ?? "").Trim();
        if (baseUrl.Length == 0 || !Uri.TryCreate(baseUrl, UriKind.Absolute, out _))
            return new DshCredentialWriteResult(false, "Base URL 必须是完整的 http(s) 地址");

        string patchPath = profile.PatchFile();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(patchPath) ?? profile.ProfileDir());
            string api = string.IsNullOrWhiteSpace(draft.Api) ? "openai-completions" : draft.Api.Trim();
            string displayName = string.IsNullOrWhiteSpace(draft.DisplayName) ? pid : draft.DisplayName.Trim();
            var models = draft.Models.Where(m => !string.IsNullOrWhiteSpace(m))
                .Select(m => m.Trim()).Distinct(StringComparer.Ordinal).ToList();
            if (models.Count == 0) models.Add("default");

            string backup = File.Exists(patchPath) ? CreateBackup(patchPath, "provider") : "";

            if (!File.Exists(patchPath) || string.IsNullOrWhiteSpace(File.ReadAllText(patchPath, Encoding.UTF8)))
            {
                var freshLines = new List<string>
                {
                    "- id: llm-pi-ai",
                };
                if (profile.Kind == DshProfileKind.Desktop)
                    freshLines.Add("  name: \"@deepseek-ai/dsh-llm-pi-ai\"");
                freshLines.Add("  config:");
                freshLines.Add("    providers:");
                freshLines.AddRange(BuildProviderBlock(6, pid, displayName, envKey, api, baseUrl, models, preservedExtras: null));
                AtomicFile.WriteAllText(patchPath, string.Join("\n", freshLines) + "\n", new UTF8Encoding(false), keepBackup: false);
                return VerifyProviderPersisted(profile, pid, envKey, baseUrl, backup);
            }

            string original = File.ReadAllText(patchPath, Encoding.UTF8);
            string eol = DetectEol(original);
            var lines = SplitLinesPreserveTrailing(original, out _);

            if (TryLocateLlmPlugin(lines, out var pluginLoc))
            {
                if (pluginLoc.ProvidersMapLine >= 0)
                {
                    int entryIndent = pluginLoc.ProvidersMapIndent + 2;
                    var existing = pluginLoc.Entries.FirstOrDefault(e => string.Equals(e.ProviderId, pid, StringComparison.OrdinalIgnoreCase));
                    if (existing != null)
                    {
                        var preserved = ExtractPreservedSubBlocks(lines, existing.StartLine, existing.EndLine, entryIndent + 2);
                        var replacement = BuildProviderBlock(entryIndent, pid, displayName, envKey, api, baseUrl, models, preserved);
                        lines.RemoveRange(existing.StartLine, existing.EndLine - existing.StartLine + 1);
                        lines.InsertRange(existing.StartLine, replacement);
                    }
                    else
                    {
                        string headerTrimmed = lines[pluginLoc.ProvidersMapLine].Trim();
                        if (headerTrimmed.Equals("providers: {}", StringComparison.Ordinal)
                            || headerTrimmed.Equals("providers: null", StringComparison.Ordinal))
                        {
                            lines[pluginLoc.ProvidersMapLine] = new string(' ', pluginLoc.ProvidersMapIndent) + "providers:";
                        }
                        int insertAt = pluginLoc.ProvidersEndLine + 1;
                        if (insertAt <= pluginLoc.ProvidersMapLine) insertAt = pluginLoc.ProvidersMapLine + 1;
                        var block = BuildProviderBlock(entryIndent, pid, displayName, envKey, api, baseUrl, models, null);
                        lines.InsertRange(insertAt, block);
                    }
                }
                else if (pluginLoc.ConfigLine >= 0)
                {
                    int provIndent = pluginLoc.ConfigIndent + 2;
                    int insertAt = pluginLoc.ConfigLine + 1;
                    var inject = new List<string> { new string(' ', provIndent) + "providers:" };
                    inject.AddRange(BuildProviderBlock(provIndent + 2, pid, displayName, envKey, api, baseUrl, models, null));
                    lines.InsertRange(insertAt, inject);
                }
                else
                {
                    int childIndent = pluginLoc.ItemIndent + 2;
                    var inject = new List<string>
                    {
                        new string(' ', childIndent) + "config:",
                        new string(' ', childIndent + 2) + "providers:",
                    };
                    inject.AddRange(BuildProviderBlock(childIndent + 4, pid, displayName, envKey, api, baseUrl, models, null));
                    lines.InsertRange(pluginLoc.PluginEndLine + 1, inject);
                }
            }
            else
            {
                // 尚无 llm-pi-ai 节点：按官方格式追加到末尾
                if (lines.Count > 0 && lines[^1].Length > 0) lines.Add("");
                lines.Add("- id: llm-pi-ai");
                if (profile.Kind == DshProfileKind.Desktop)
                    lines.Add("  name: \"@deepseek-ai/dsh-llm-pi-ai\"");
                lines.Add("  config:");
                lines.Add("    providers:");
                lines.AddRange(BuildProviderBlock(6, pid, displayName, envKey, api, baseUrl, models, null));
            }

            string updated = JoinLines(lines, eol, trailingNewline: true);
            AtomicFile.WriteAllText(patchPath, updated, new UTF8Encoding(false), keepBackup: false);
            return VerifyProviderPersisted(profile, pid, envKey, baseUrl, backup);
        }
        catch (Exception ex)
        {
            Logger.LogError("DshCredentialManager.ApplyProviderToProfile", ex);
            return new DshCredentialWriteResult(false, $"写入 {profile.DisplayName} 失败：" + ex.Message);
        }
    }

    public static DshCredentialWriteResult RemoveProviderFromProfile(DshProfileInfo profile, string providerId)
    {
        if (profile == null || string.IsNullOrWhiteSpace(providerId))
            return new DshCredentialWriteResult(false, "参数无效");

        string patchPath = profile.PatchFile();
        if (!File.Exists(patchPath))
            return new DshCredentialWriteResult(true, $"{profile.DisplayName} 无配置文件");

        try
        {
            string original = File.ReadAllText(patchPath, Encoding.UTF8);
            string eol = DetectEol(original);
            var lines = SplitLinesPreserveTrailing(original, out bool hadTrail);
            if (!TryLocateLlmPlugin(lines, out var loc))
                return new DshCredentialWriteResult(true, $"{profile.DisplayName} 未启用该提供商");

            var existing = loc.Entries.FirstOrDefault(e => string.Equals(e.ProviderId, providerId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (existing == null)
                return new DshCredentialWriteResult(true, $"{profile.DisplayName} 已无此提供商");

            string backup = CreateBackup(patchPath, "provider");
            lines.RemoveRange(existing.StartLine, existing.EndLine - existing.StartLine + 1);

            // 若删完 providers 下已无条目，写回 providers: {} 保持合法 YAML mapping
            if (loc.Entries.Count == 1 && loc.ProvidersMapLine >= 0 && loc.ProvidersMapLine < lines.Count)
                lines[loc.ProvidersMapLine] = new string(' ', loc.ProvidersMapIndent) + "providers: {}";

            AtomicFile.WriteAllText(patchPath, JoinLines(lines, eol, hadTrail), new UTF8Encoding(false), keepBackup: false);

            var after = ParsePatchProviders(patchPath);
            bool stillThere = after.Any(x => string.Equals(x.ProviderId, providerId.Trim(), StringComparison.OrdinalIgnoreCase));
            return !stillThere
                ? new DshCredentialWriteResult(true, $"已从 {profile.DisplayName} 移除 {providerId}", backup)
                : new DshCredentialWriteResult(false, "移除后回读仍存在该条目，请查看备份", backup);
        }
        catch (Exception ex)
        {
            Logger.LogError("DshCredentialManager.RemoveProviderFromProfile", ex);
            return new DshCredentialWriteResult(false, $"移除失败：" + ex.Message);
        }
    }

    private static DshCredentialWriteResult VerifyProviderPersisted(
        DshProfileInfo profile, string pid, string envKey, string baseUrl, string backup)
    {
        var recheck = ParsePatchProviders(profile.PatchFile());
        var found = recheck.FirstOrDefault(p => string.Equals(p.ProviderId, pid, StringComparison.OrdinalIgnoreCase));
        if (found != null
            && string.Equals(found.ApiKeyEnv, envKey, StringComparison.Ordinal)
            && string.Equals(found.BaseUrl, baseUrl, StringComparison.Ordinal))
        {
            return new DshCredentialWriteResult(true, $"已同步到 {profile.DisplayName}（{pid}）", backup);
        }
        return new DshCredentialWriteResult(false, $"写入 {profile.DisplayName} 后磁盘回读校验失败", backup);
    }

    private sealed record ParsedPatchProvider(
        string ProviderId,
        string DisplayName,
        string ApiKeyEnv,
        string Api,
        string BaseUrl,
        List<string> Models,
        int StartLine,
        int EndLine);

    private sealed record LlmPluginLocation(
        int ItemStartLine,
        int ItemIndent,
        int ConfigLine,
        int ConfigIndent,
        int ProvidersMapLine,
        int ProvidersMapIndent,
        int ProvidersEndLine,
        int PluginEndLine,
        List<ParsedPatchProvider> Entries);

    private static List<ParsedPatchProvider> ParsePatchProviders(string patchPath)
    {
        if (!File.Exists(patchPath)) return new List<ParsedPatchProvider>();
        var lines = SplitLinesPreserveTrailing(File.ReadAllText(patchPath, Encoding.UTF8), out _);
        return TryLocateLlmPlugin(lines, out var loc) ? loc.Entries : new List<ParsedPatchProvider>();
    }

    private static bool TryLocateLlmPlugin(List<string> lines, out LlmPluginLocation location)
    {
        location = new LlmPluginLocation(-1, 0, -1, 0, -1, 0, -1, -1, new List<ParsedPatchProvider>());

        for (int i = 0; i < lines.Count; i++)
        {
            string trim = lines[i].Trim();
            if (!trim.StartsWith("- ", StringComparison.Ordinal)) continue;
            int itemIndent = IndentOf(lines[i]);

            // 找出此插件列表项的结束行
            int pluginEnd = i;
            bool isLlm = trim.StartsWith("- id: llm-pi-ai", StringComparison.Ordinal)
                      || trim.Equals("- id: 'llm-pi-ai'", StringComparison.Ordinal)
                      || trim.Equals("- id: \"llm-pi-ai\"", StringComparison.Ordinal);

            int configLine = -1, configIndent = -1;
            int providersLine = -1, providersIndent = -1, providersEnd = -1;

            for (int j = i + 1; j < lines.Count; j++)
            {
                string lj = lines[j];
                string tj = lj.Trim();
                if (tj.Length == 0 || tj.StartsWith("#", StringComparison.Ordinal)) continue;
                int ind = IndentOf(lj);
                if (ind < itemIndent || (ind == itemIndent && tj.StartsWith("-", StringComparison.Ordinal)))
                    break;
                pluginEnd = j;

                if (ind == itemIndent + 2 && tj.StartsWith("name:", StringComparison.Ordinal))
                {
                    string nameVal = UnquoteScalar(tj["name:".Length..].Trim());
                    if (nameVal.Contains("dsh-llm-pi-ai", StringComparison.OrdinalIgnoreCase)) isLlm = true;
                }
                else if (ind == itemIndent + 2 && tj.StartsWith("config:", StringComparison.Ordinal))
                {
                    configLine = j;
                    configIndent = ind;
                }
                else if (configLine >= 0 && ind == configIndent + 2 && tj.StartsWith("providers:", StringComparison.Ordinal))
                {
                    providersLine = j;
                    providersIndent = ind;
                    providersEnd = j;
                }
                else if (providersLine >= 0 && ind > providersIndent)
                {
                    providersEnd = j;
                }
                else if (providersLine >= 0 && ind <= providersIndent)
                {
                    // 已离开 providers 区块
                }
            }

            if (!isLlm)
            {
                i = pluginEnd;
                continue;
            }

            var entries = new List<ParsedPatchProvider>();
            if (providersLine >= 0 && providersEnd > providersLine)
            {
                int entryIndent = -1;
                for (int k = providersLine + 1; k <= providersEnd; k++)
                {
                    string lk = lines[k];
                    string tk = lk.Trim();
                    if (tk.Length == 0 || tk.StartsWith("#", StringComparison.Ordinal)) continue;
                    int ind = IndentOf(lk);
                    if (ind <= providersIndent) break;
                    if (entryIndent < 0) entryIndent = ind;
                    if (ind == entryIndent && tk.EndsWith(":", StringComparison.Ordinal) && !tk.StartsWith("-", StringComparison.Ordinal))
                    {
                        string pid = UnquoteScalar(tk[..^1].Trim());
                        int pEnd = k;
                        string display = pid, envKey = "", api = "", baseUrl = "";
                        var models = new List<string>();
                        bool inModels = false;
                        int modelsIndent = -1;

                        for (int m = k + 1; m <= providersEnd; m++)
                        {
                            string lm = lines[m];
                            string tm = lm.Trim();
                            if (tm.Length == 0 || tm.StartsWith("#", StringComparison.Ordinal)) continue;
                            int mind = IndentOf(lm);
                            if (mind <= entryIndent) break;
                            pEnd = m;

                            if (mind == entryIndent + 2 && !tm.StartsWith("-", StringComparison.Ordinal))
                            {
                                inModels = false;
                                int c = tm.IndexOf(':');
                                if (c > 0)
                                {
                                    string field = tm[..c].Trim();
                                    string val = UnquoteScalar(tm[(c + 1)..].Trim());
                                    switch (field)
                                    {
                                        case "displayName": display = val; break;
                                        case "apiKeyEnv": envKey = val; break;
                                        case "api": api = val; break;
                                        case "baseURL": baseUrl = val; break;
                                        case "models":
                                            inModels = true;
                                            modelsIndent = mind;
                                            break;
                                    }
                                }
                            }
                            else if (inModels && mind >= modelsIndent)
                            {
                                if (tm.StartsWith("- id:", StringComparison.Ordinal))
                                {
                                    string mid = UnquoteScalar(tm["- id:".Length..].Trim());
                                    if (mid.Length > 0) models.Add(mid);
                                }
                                else if (tm.StartsWith("- ", StringComparison.Ordinal) && !tm.Contains(':', StringComparison.Ordinal))
                                {
                                    string mid = UnquoteScalar(tm[2..].Trim());
                                    if (mid.Length > 0) models.Add(mid);
                                }
                            }
                        }

                        entries.Add(new ParsedPatchProvider(pid, display, envKey, api, baseUrl, models, k, pEnd));
                        k = pEnd;
                    }
                }
            }

            location = new LlmPluginLocation(i, itemIndent, configLine, configIndent, providersLine, providersIndent, providersEnd, pluginEnd, entries);
            return true;
        }

        return false;
    }

    private static List<string> ExtractPreservedSubBlocks(List<string> lines, int startLine, int endLine, int fieldIndent)
    {
        var preserved = new List<string>();
        bool capturing = false;
        for (int i = startLine + 1; i <= endLine; i++)
        {
            string line = lines[i];
            string trim = line.Trim();
            if (trim.Length == 0) continue;
            int ind = IndentOf(line);
            if (ind == fieldIndent && !trim.StartsWith("-", StringComparison.Ordinal))
            {
                int c = trim.IndexOf(':');
                string key = c > 0 ? trim[..c].Trim() : "";
                capturing = key is not ("displayName" or "apiKeyEnv" or "api" or "baseURL" or "models");
            }
            if (capturing) preserved.Add(line);
        }
        return preserved;
    }

    private static List<string> BuildProviderBlock(
        int baseIndent,
        string providerId,
        string displayName,
        string apiKeyEnv,
        string api,
        string baseUrl,
        IReadOnlyList<string> models,
        List<string>? preservedExtras)
    {
        string p0 = new(' ', baseIndent);
        string p1 = new(' ', baseIndent + 2);
        string p2 = new(' ', baseIndent + 4);
        var outLines = new List<string>
        {
            p0 + providerId + ":",
            p1 + "displayName: " + YamlSafeScalar(displayName),
            p1 + "apiKeyEnv: " + YamlSafeScalar(apiKeyEnv),
            p1 + "api: " + YamlSafeScalar(api),
            p1 + "baseURL: " + YamlSafeScalar(baseUrl),
            p1 + "models:",
        };
        foreach (string m in models)
        {
            outLines.Add(p2 + "- id: " + YamlSafeScalar(m));
            outLines.Add(p2 + "  name: " + YamlSafeScalar(m));
        }
        if (preservedExtras is { Count: > 0 })
            outLines.AddRange(preservedExtras);
        return outLines;
    }

    // ══════════════ 在线探测 /models ══════════════

    public static async Task<(bool Ok, string Message, IReadOnlyList<string> Models)> ProbeEndpointModelsAsync(
        string baseUrl,
        string? apiKey,
        CancellationToken ct = default)
    {
        string trimmed = (baseUrl ?? "").Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out _))
            return (false, "Base URL 无效", Array.Empty<string>());

        var candidates = new List<string>();
        if (trimmed.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
            candidates.Add(trimmed);
        else
        {
            candidates.Add(trimmed + "/models");
            if (!trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                candidates.Add(trimmed + "/v1/models");
        }

        string lastError = "未获取到模型列表";
        foreach (string endpoint in candidates.Distinct(StringComparer.Ordinal))
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    string key = apiKey.Trim();
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                    req.Headers.TryAddWithoutValidation("x-api-key", key);
                    req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                }

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(6));
                using var resp = await ProbeHttp.SendAsync(req, timeoutCts.Token).ConfigureAwait(false);
                string body = await resp.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode)
                {
                    lastError = $"HTTP {(int)resp.StatusCode} ({resp.ReasonPhrase})";
                    continue;
                }

                var models = ExtractModelIdsFromJson(body);
                if (models.Count > 0)
                    return (true, $"从端点探测到 {models.Count} 个模型", models);
                lastError = "端点响应 200 但未包含模型列表";
            }
            catch (OperationCanceledException)
            {
                lastError = "请求超时（6 秒）";
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
            }
        }

        return (false, lastError, Array.Empty<string>());
    }

    private static List<string> ExtractModelIdsFromJson(string json)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array)
                    CollectModelIds(dataEl, list);
                else if (root.TryGetProperty("models", out var modelsEl) && modelsEl.ValueKind == JsonValueKind.Array)
                    CollectModelIds(modelsEl, list);
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                CollectModelIds(root, list);
            }
        }
        catch { }
        return list.Distinct(StringComparer.Ordinal).Take(48).ToList();
    }

    private static void CollectModelIds(JsonElement arrayEl, List<string> target)
    {
        foreach (var item in arrayEl.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                string? s = item.GetString();
                if (!string.IsNullOrWhiteSpace(s)) target.Add(s!.Trim());
            }
            else if (item.ValueKind == JsonValueKind.Object)
            {
                string? id = item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                    ? idEl.GetString()
                    : item.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                        ? nameEl.GetString()
                        : null;
                if (!string.IsNullOrWhiteSpace(id))
                {
                    string clean = id!.Trim();
                    if (clean.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
                        clean = clean["models/".Length..];
                    target.Add(clean);
                }
            }
        }
    }

    // ══════════════ YAML / 备份基础工具 ══════════════

    private static int IndentOf(string line)
    {
        int n = 0;
        while (n < line.Length && line[n] == ' ') n++;
        return n;
    }

    private static string UnquoteScalar(string v)
    {
        if (v.Length >= 2)
        {
            if (v[0] == '\'' && v[^1] == '\'') return v[1..^1].Replace("''", "'", StringComparison.Ordinal);
            if (v[0] == '"' && v[^1] == '"') return v[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
        }
        return v;
    }

    private static string YamlDoubleQuoted(string v)
        => "\"" + (v ?? "").Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string YamlSafeScalar(string v)
    {
        v ??= "";
        if (v.Length == 0) return "''";
        bool quote = "-?:,[]{}#&*!|>'\"%@` ".IndexOf(v[0]) >= 0
                  || v.Contains(": ", StringComparison.Ordinal)
                  || v.Contains('\n');
        return quote ? "\"" + v.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : v;
    }

    private static string DetectEol(string text) => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private static List<string> SplitLinesPreserveTrailing(string text, out bool hadTrailingNewline)
    {
        string norm = (text ?? "").Replace("\r\n", "\n", StringComparison.Ordinal);
        hadTrailingNewline = norm.EndsWith('\n');
        if (hadTrailingNewline) norm = norm[..^1];
        return norm.Length == 0 ? new List<string>() : new List<string>(norm.Split('\n'));
    }

    private static string JoinLines(List<string> lines, string eol, bool trailingNewline)
    {
        if (lines.Count == 0) return "";
        return string.Join(eol, lines) + (trailingNewline ? eol : "");
    }

    private static string CreateBackup(string path, string tag)
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string bak = path + ".bak-" + tag + "-" + stamp;
        int n = 2;
        while (File.Exists(bak)) bak = path + ".bak-" + tag + "-" + stamp + "-" + n++;
        try
        {
            File.Copy(path, bak, overwrite: false);
            PruneBackups(path, tag);
        }
        catch (Exception ex)
        {
            Logger.LogError("DshCredentialManager.CreateBackup", ex);
        }
        return bak;
    }

    private static void PruneBackups(string path, string tag)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            string name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            string pattern = name + ".bak-" + tag + "-*";
            var old = new DirectoryInfo(dir).GetFiles(pattern)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(MaxBackupsPerFile);
            foreach (var f in old)
            {
                try { f.Delete(); } catch { }
            }
        }
        catch { }
    }
}
