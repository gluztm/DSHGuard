#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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

/// <summary>单模型结构化配置（严格对齐 DSH <c>@deepseek-ai/dsh-llm-pi-ai</c> 引擎 Schema）。</summary>
public sealed class DshModelConfig
{
    public static readonly string[] AllowedReasoningLevels = { "minimal", "low", "medium", "high", "xhigh", "max" };

    private static readonly string[] VisionHints =
    {
        "claude-3", "claude-4", "claude-5", "claude-sonnet", "claude-opus", "claude-haiku",
        "gpt-4o", "gpt-4.1", "gpt-5", "o3", "o4", "gemini", "vl", "vision", "pixtral", "qvq",
    };

    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public List<string> InputModalities { get; set; } = new() { "text" };
    public bool ToolUse { get; set; } = true;
    public int ContextWindow { get; set; }
    public int MaxTokens { get; set; }

    /// <summary><c>inherit</c>（跟随提供商）/ <c>custom</c>（自定义档位）/ <c>disabled</c>（关闭推理 false）。</summary>
    public string ReasoningMode { get; set; } = "inherit";
    public List<string> ReasoningLevels { get; set; } = new();

    /// <summary>Anthropic 模型级覆盖：null 跟随提供商 / true / false。</summary>
    public bool? ForceAdaptiveThinking { get; set; }

    /// <summary>OpenAI Completions 模型级覆盖（空串表示跟随提供商）。</summary>
    public string ThinkingFormat { get; set; } = "";
    public bool? SupportsReasoningEffort { get; set; }
    public Dictionary<string, string> CustomKwargs { get; set; } = new(StringComparer.Ordinal);

    [JsonIgnore]
    public string EffectiveDisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name.Trim();

    [JsonIgnore]
    public bool SupportsImage => InputModalities.Any(m => string.Equals(m, "image", StringComparison.OrdinalIgnoreCase));

    public void SetSupportsImage(bool supported)
    {
        var list = new List<string> { "text" };
        if (supported) list.Add("image");
        InputModalities = list;
    }

    public DshModelConfig Clone() => new()
    {
        Id = Id,
        Name = Name,
        Enabled = Enabled,
        InputModalities = InputModalities.ToList(),
        ToolUse = ToolUse,
        ContextWindow = ContextWindow,
        MaxTokens = MaxTokens,
        ReasoningMode = ReasoningMode,
        ReasoningLevels = ReasoningLevels.ToList(),
        ForceAdaptiveThinking = ForceAdaptiveThinking,
        ThinkingFormat = ThinkingFormat,
        SupportsReasoningEffort = SupportsReasoningEffort,
        CustomKwargs = new Dictionary<string, string>(CustomKwargs, StringComparer.Ordinal),
    };

    public static DshModelConfig InferDefaults(string modelId, string? protocol = null)
    {
        string id = (modelId ?? "").Trim();
        bool inferImage = VisionHints.Any(h => id.Contains(h, StringComparison.OrdinalIgnoreCase));
        int cw = id.Contains("1m", StringComparison.OrdinalIgnoreCase) ? 1_000_000 : 0;
        var cfg = new DshModelConfig
        {
            Id = id,
            Name = id,
            Enabled = true,
            InputModalities = inferImage ? new List<string> { "text", "image" } : new List<string> { "text" },
            ToolUse = true,
            ContextWindow = cw,
        };
        if (string.Equals(protocol, "anthropic-messages", StringComparison.OrdinalIgnoreCase)
            && (id.Contains("claude-4", StringComparison.OrdinalIgnoreCase)
                || id.Contains("claude-5", StringComparison.OrdinalIgnoreCase)
                || id.Contains("opus", StringComparison.OrdinalIgnoreCase)
                || id.Contains("sonnet", StringComparison.OrdinalIgnoreCase)))
        {
            // 不自动强制模型级，由提供商级或默认控制；保留干净默认
        }
        return cfg;
    }
}

/// <summary>提供商路由级特色参数（按四大协议家族自动门控写出，绝不生成非法 compat 键）。</summary>
public sealed class DshProviderSpecialConfig
{
    public string Reasoning { get; set; } = "";
    public int ThinkingBudgetMedium { get; set; }
    public string CacheRetention { get; set; } = "";
    public bool DefaultImageInput { get; set; }

    // Anthropic Messages
    public bool? ForceAdaptiveThinking { get; set; }
    public bool? SupportsCacheControlOnTools { get; set; }
    public bool? SupportsLongCacheRetention { get; set; }

    // OpenAI Completions / Responses
    public string ThinkingFormat { get; set; } = "";
    public bool? SupportsReasoningEffort { get; set; }
    public bool? SupportsDeveloperRole { get; set; }
    public bool? RequiresReasoningContent { get; set; }
    public string MaxTokensField { get; set; } = "";
    public bool? SupportsStrictMode { get; set; }

    public DshProviderSpecialConfig Clone() => new()
    {
        Reasoning = Reasoning,
        ThinkingBudgetMedium = ThinkingBudgetMedium,
        CacheRetention = CacheRetention,
        DefaultImageInput = DefaultImageInput,
        ForceAdaptiveThinking = ForceAdaptiveThinking,
        SupportsCacheControlOnTools = SupportsCacheControlOnTools,
        SupportsLongCacheRetention = SupportsLongCacheRetention,
        ThinkingFormat = ThinkingFormat,
        SupportsReasoningEffort = SupportsReasoningEffort,
        SupportsDeveloperRole = SupportsDeveloperRole,
        RequiresReasoningContent = RequiresReasoningContent,
        MaxTokensField = MaxTokensField,
        SupportsStrictMode = SupportsStrictMode,
    };
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
    public List<DshModelConfig> ModelConfigs { get; init; } = new();
    public DshProviderSpecialConfig Special { get; init; } = new();
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
    public List<DshModelConfig> ModelConfigs { get; init; } = new();
    public DshProviderSpecialConfig Special { get; init; } = new();
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
    public string ProviderId { get; set; } = "";
    public string DisplayName { get; init; } = "";
    public string ApiKeyEnv { get; init; } = "";
    public string Api { get; init; } = "openai-completions";
    public string BaseUrl { get; init; } = "";
    public IReadOnlyList<string> Models { get; init; } = Array.Empty<string>();
    public List<DshModelConfig>? ModelConfigs { get; init; }
    public DshProviderSpecialConfig? Special { get; init; }
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
public sealed record DshCredentialWriteResult(bool Ok, string Message, string BackupPath = "", string ProviderId = "");

/// <summary>
/// 2.4.0 CC Switch 风格凭据收纳与模型提供商跨端管理器：
///   1. <c>~/.dsh/.credentials.yaml</c> 无损读写：严守 <c>version: 1</c> 与 <c>records:</c> 授权段落，
///      对 <c>*_ACCOUNT_*</c> 与多行 JSON 账号态令牌实行只读保护，写入前留校验，守护壳零缓存；
///   2. DSH 各 profile（web / desktop / headless）的 <c>cordis.patch.yml</c> 中 <c>llm-pi-ai</c> 结构化无损投影；
///   3. 四大协议自动 URL 后缀（/v1 / /v1beta / 裸根）、模型列表在线探测、单模型真实连通性测速。
/// </summary>
public static class DshCredentialManager
{
    private const int MaxBackupsPerFile = 10;

    private static readonly Regex ValidEnvKeyRegex = new("^[A-Za-z_][A-Za-z0-9_]{0,95}$", RegexOptions.Compiled);
    private static readonly Regex ValidProviderIdRegex = new("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$", RegexOptions.Compiled);

    private static readonly HashSet<string> ValidThinkingLevels = new(StringComparer.OrdinalIgnoreCase)
    {
        "off", "minimal", "low", "medium", "high", "xhigh", "max",
    };

    private static readonly HashSet<string> ValidCacheRetentions = new(StringComparer.OrdinalIgnoreCase)
    {
        "none", "short", "long",
    };

    // 与 @deepseek-ai/dsh-llm-pi-ai 的 COMPAT_GATES 严格对齐：超出协议门控的键绝不写入
    private static readonly HashSet<string> CompletionsCompatGate = new(StringComparer.Ordinal)
    {
        "supportsStore", "supportsDeveloperRole", "supportsReasoningEffort", "supportsUsageInStreaming",
        "supportsFinishReason", "maxTokensField", "requiresToolResultName", "requiresAssistantAfterToolResult",
        "requiresThinkingAsText", "requiresReasoningContentOnAssistantMessages", "thinkingFormat",
        "chatTemplateKwargs", "chatTemplateArgs", "supportsThinkingTokenBudget", "supportsStrictMode",
        "cacheControlFormat", "supportsLongCacheRetention",
    };

    private static readonly HashSet<string> ResponsesCompatGate = new(StringComparer.Ordinal)
    {
        "supportsDeveloperRole", "supportsStrictMode", "supportsLongCacheRetention",
    };

    private static readonly HashSet<string> AnthropicCompatGate = new(StringComparer.Ordinal)
    {
        "supportsEagerToolInputStreaming", "supportsLongCacheRetention", "supportsCacheControlOnTools",
        "supportsTemperature", "forceAdaptiveThinking", "allowEmptySignature", "supportsStrictTools",
    };

    private static readonly HashSet<string> ManagedRouteTopKeys = new(StringComparer.Ordinal)
    {
        "displayName", "apiKeyEnv", "api", "baseURL", "models",
        "reasoning", "thinkingBudgets", "cacheRetention", "defaultInput", "compat",
    };

    private static readonly HashSet<string> ManagedRouteCompatKeys = new(StringComparer.Ordinal)
    {
        "forceAdaptiveThinking", "supportsCacheControlOnTools", "supportsLongCacheRetention",
        "thinkingFormat", "supportsReasoningEffort", "supportsDeveloperRole",
        "requiresReasoningContentOnAssistantMessages", "maxTokensField", "supportsStrictMode",
    };

    private static readonly JsonSerializerOptions DisabledJsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly HttpClient ProbeHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    /// <summary>8 套常用模型与生图插件一键预设。</summary>
    public static IReadOnlyList<DshProviderPreset> BuiltInPresets { get; } = new[]
    {
        new DshProviderPreset("deepseek", "DeepSeek 官方", "deepseek", "DeepSeek",
            "DEEPSEEK_API_KEY", "openai-completions", "https://api.deepseek.com/v1",
            new[] { "deepseek-chat", "deepseek-reasoner" },
            "DeepSeek 官方 OpenAI 兼容接口（/v1）"),
        new DshProviderPreset("openai", "OpenAI / 通用中继", "openai-relay", "OpenAI Relay",
            "OPENAI_API_KEY", "openai-completions", "https://api.openai.com/v1",
            new[] { "gpt-4o", "o3-mini" },
            "OpenAI 官方或兼容 /v1 中继站"),
        new DshProviderPreset("anthropic", "Anthropic Claude", "anthropic", "Claude",
            "ANTHROPIC_API_KEY", "anthropic-messages", "https://api.anthropic.com",
            new[] { "claude-sonnet-4-5", "claude-opus-4-5" },
            "Anthropic 官方或兼容 Messages API 中继（支持 Adaptive Thinking）"),
        new DshProviderPreset("gemini", "Google Gemini", "google", "Gemini",
            "GEMINI_API_KEY", "google-generative-ai", "https://generativelanguage.googleapis.com/v1beta",
            new[] { "gemini-2.5-pro", "gemini-2.5-flash" },
            "Google Gemini 原生 /v1beta 协议（自动映射到 google 原生路由）"),
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

    /// <summary>根据目标协议自动规范化 Base URL 后缀：OpenAI 家族自动挂 /v1，Google 自动挂 /v1beta，Anthropic 保持根地址。</summary>
    public static string SwitchBaseUrlForProtocol(string? currentUrl, string targetProtocol)
    {
        string raw = (currentUrl ?? "").Trim().TrimEnd('/');
        string proto = (targetProtocol ?? "").Trim().ToLowerInvariant();

        if (raw.Length == 0)
        {
            return proto switch
            {
                "anthropic-messages" => "https://api.anthropic.com",
                "google-generative-ai" => "https://generativelanguage.googleapis.com/v1beta",
                _ => "https://api.openai.com/v1",
            };
        }

        string root = raw;
        while (true)
        {
            if (root.EndsWith("/v1beta/openai", StringComparison.OrdinalIgnoreCase))
            {
                root = root[..^"/v1beta/openai".Length].TrimEnd('/');
                continue;
            }
            if (root.EndsWith("/v1beta", StringComparison.OrdinalIgnoreCase))
            {
                root = root[..^"/v1beta".Length].TrimEnd('/');
                continue;
            }
            if (root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            {
                root = root[..^"/v1".Length].TrimEnd('/');
                continue;
            }
            break;
        }

        return proto switch
        {
            "openai-completions" or "openai-responses" => root + "/v1",
            "google-generative-ai" => root + "/v1beta",
            "anthropic-messages" => root,
            _ => root,
        };
    }

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

    /// <summary>按需从官方 <c>.credentials.yaml</c> 瞬时读取一次密钥用于发起探测或连通测试，用完即弃，绝不做静态缓存。</summary>
    public static string? TryResolveEnvSecretForTransientProbe(string? apiKeyEnv, string? pathOverride = null)
        => string.IsNullOrWhiteSpace(apiKeyEnv) ? null : TryReadRawSecret(apiKeyEnv.Trim(), pathOverride);

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
                ? new DshCredentialWriteResult(true, $"已收纳 {k}（{MaskSecret(trimmedSecret)}）")
                : new DshCredentialWriteResult(false, "写入后磁盘回读校验失败，未做任何缓存保留");
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

            var lines = SplitLinesPreserveTrailing(original, out bool hadTrailingNewline);
            lines.RemoveRange(existing.StartLine, existing.EndLine - existing.StartLine + 1);

            string output = JoinLines(lines, eol, hadTrailingNewline);
            AtomicFile.WriteAllText(path, output, new UTF8Encoding(false), keepBackup: false);

            var recheck = ScanCredentials(path);
            bool gone = recheck.Entries.All(e => !string.Equals(e.Key, k, StringComparison.OrdinalIgnoreCase));
            return gone
                ? new DshCredentialWriteResult(true, $"已删除 {existing.Key}")
                : new DshCredentialWriteResult(false, "删除后仍读到该条目，请检查 .credentials.yaml 权限");
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
                    version = UnquoteScalar(trimmed["version:".Length..].Trim());
                else if (trimmed.StartsWith("records:", StringComparison.Ordinal))
                    inRecords = true;
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

    // ══════════════ disabled-models.json（纯非敏感 UI 元数据）══════════════

    internal static string DisabledModelsFilePath
        => Path.Combine(GuardPaths.FixtureDefaultRoot ?? GuardPaths.ConfigDir, "disabled-models.json");

    private static Dictionary<string, List<DshModelConfig>> LoadDisabledModelsMap()
    {
        var map = new Dictionary<string, List<DshModelConfig>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string path = DisabledModelsFilePath;
            if (!File.Exists(path)) return map;
            string json = File.ReadAllText(path, Encoding.UTF8);
            var raw = JsonSerializer.Deserialize<Dictionary<string, List<DshModelConfig>>>(json, DisabledJsonOpts);
            if (raw != null)
            {
                foreach (var (k, list) in raw)
                {
                    if (string.IsNullOrWhiteSpace(k) || list == null) continue;
                    var clean = list.Where(m => m != null && !string.IsNullOrWhiteSpace(m.Id))
                        .Select(m =>
                        {
                            var c = m.Clone();
                            c.Enabled = false;
                            NormalizeModelModalities(c);
                            return c;
                        })
                        .ToList();
                    if (clean.Count > 0) map[k.Trim()] = clean;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("DshCredentialManager.LoadDisabledModelsMap", ex);
        }
        return map;
    }

    private static void SaveDisabledModelsForProvider(string effectivePid, string? migratedFromPid, List<DshModelConfig> disabled)
    {
        try
        {
            var map = LoadDisabledModelsMap();
            if (!string.IsNullOrWhiteSpace(migratedFromPid)
                && !string.Equals(migratedFromPid, effectivePid, StringComparison.OrdinalIgnoreCase))
            {
                map.Remove(migratedFromPid.Trim());
            }

            if (disabled.Count > 0)
            {
                map[effectivePid] = disabled.Select(m =>
                {
                    var c = m.Clone();
                    c.Enabled = false;
                    return c;
                }).ToList();
            }
            else
            {
                map.Remove(effectivePid);
            }

            string path = DisabledModelsFilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? GuardPaths.ConfigDir);
            string json = JsonSerializer.Serialize(map, DisabledJsonOpts);
            AtomicFile.WriteAllText(path, json + "\n", new UTF8Encoding(false), keepBackup: false);
        }
        catch (Exception ex)
        {
            Logger.LogError("DshCredentialManager.SaveDisabledModelsForProvider", ex);
        }
    }

    public static void ClearDisabledModels(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return;
        try
        {
            var map = LoadDisabledModelsMap();
            if (map.Remove(providerId.Trim()))
            {
                string path = DisabledModelsFilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? GuardPaths.ConfigDir);
                string json = JsonSerializer.Serialize(map, DisabledJsonOpts);
                AtomicFile.WriteAllText(path, json + "\n", new UTF8Encoding(false), keepBackup: false);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("DshCredentialManager.ClearDisabledModels", ex);
        }
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

        var disabledMap = LoadDisabledModelsMap();
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
                    ModelConfigs = item.ModelConfigs,
                    Special = item.Special,
                };
            }
        }

        var result = new List<DshLlmProvider>(byProvider.Count);
        foreach (var (providerId, states) in byProvider)
        {
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

            var mergedConfigs = new List<DshModelConfig>();
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cfg in primary.ModelConfigs)
            {
                if (string.IsNullOrWhiteSpace(cfg.Id) || !seenIds.Add(cfg.Id)) continue;
                var clone = cfg.Clone();
                clone.Enabled = true;
                mergedConfigs.Add(clone);
            }
            foreach (var s in states.Values)
            {
                foreach (var cfg in s.ModelConfigs)
                {
                    if (string.IsNullOrWhiteSpace(cfg.Id) || !seenIds.Add(cfg.Id)) continue;
                    var clone = cfg.Clone();
                    clone.Enabled = true;
                    mergedConfigs.Add(clone);
                }
            }
            if (disabledMap.TryGetValue(providerId, out var disabledList))
            {
                foreach (var dc in disabledList)
                {
                    if (string.IsNullOrWhiteSpace(dc.Id) || !seenIds.Add(dc.Id)) continue;
                    var clone = dc.Clone();
                    clone.Enabled = false;
                    mergedConfigs.Add(clone);
                }
            }

            var mergedEnabledIds = mergedConfigs
                .Where(m => m.Enabled)
                .Select(m => m.Id)
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
                Models = mergedEnabledIds,
                ModelConfigs = mergedConfigs,
                Special = primary.Special.Clone(),
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
        string requestedPid = NormalizeProviderId(draft.ProviderId);
        if (!ValidProviderIdRegex.IsMatch(requestedPid))
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
            string rawApi = string.IsNullOrWhiteSpace(draft.Api) ? "openai-completions" : draft.Api.Trim();
            string displayName = string.IsNullOrWhiteSpace(draft.DisplayName) ? requestedPid : draft.DisplayName.Trim();
            var special = draft.Special?.Clone() ?? new DshProviderSpecialConfig();

            string backup = File.Exists(patchPath) ? CreateBackup(patchPath, "provider") : "";

            string original = File.Exists(patchPath) ? File.ReadAllText(patchPath, Encoding.UTF8) : "";
            string eol = string.IsNullOrWhiteSpace(original) ? "\n" : DetectEol(original);
            var lines = string.IsNullOrWhiteSpace(original)
                ? new List<string>()
                : SplitLinesPreserveTrailing(original, out _);

            LlmPluginLocation? rawLoc = null;
            bool hasPlugin = lines.Count > 0 && TryLocateLlmPlugin(lines, out rawLoc) && rawLoc != null;
            LlmPluginLocation pluginLoc = hasPlugin
                ? rawLoc!
                : new LlmPluginLocation(-1, 0, -1, 0, -1, 0, -1, -1, new List<ParsedPatchProvider>());

            // 解析有效协议与 Google /v1beta 原生路由映射规则
            string effectivePid = requestedPid;
            string effectiveApi = rawApi;
            bool omitApi = false;
            if (string.Equals(rawApi, "google-generative-ai", StringComparison.OrdinalIgnoreCase))
            {
                bool anotherGoogleExists = hasPlugin && pluginLoc.Entries.Any(e =>
                    string.Equals(e.ProviderId, "google", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(e.ProviderId, requestedPid, StringComparison.OrdinalIgnoreCase));

                if (!anotherGoogleExists)
                {
                    effectivePid = "google";
                    omitApi = true;
                }
                else
                {
                    effectiveApi = "openai-completions";
                    omitApi = false;
                    if (baseUrl.EndsWith("/v1beta", StringComparison.OrdinalIgnoreCase))
                        baseUrl += "/openai";
                }
            }

            var existing = hasPlugin
                ? pluginLoc.Entries.FirstOrDefault(e => string.Equals(e.ProviderId, requestedPid, StringComparison.OrdinalIgnoreCase))
                    ?? pluginLoc.Entries.FirstOrDefault(e => string.Equals(e.ProviderId, effectivePid, StringComparison.OrdinalIgnoreCase))
                : null;

            // 构建启用/禁用模型清单（若草稿仅提供 Models IDs 则匹配现有详细配置或按推理缺省构造）
            List<DshModelConfig> enabledModels;
            List<DshModelConfig>? disabledModelsToSave = null;
            if (draft.ModelConfigs != null)
            {
                var dedup = new List<DshModelConfig>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in draft.ModelConfigs)
                {
                    if (item == null || string.IsNullOrWhiteSpace(item.Id) || !seen.Add(item.Id.Trim())) continue;
                    var c = item.Clone();
                    c.Id = item.Id.Trim();
                    NormalizeModelModalities(c);
                    dedup.Add(c);
                }
                enabledModels = dedup.Where(m => m.Enabled).ToList();
                var disabled = dedup.Where(m => !m.Enabled).ToList();
                if (enabledModels.Count == 0 && disabled.Count > 0)
                {
                    disabled[0].Enabled = true;
                    enabledModels.Add(disabled[0]);
                    disabled.RemoveAt(0);
                }
                disabledModelsToSave = disabled;
            }
            else
            {
                var existingById = (existing?.ModelConfigs ?? new List<DshModelConfig>())
                    .ToDictionary(m => m.Id, m => m, StringComparer.OrdinalIgnoreCase);
                enabledModels = draft.Models
                    .Where(m => !string.IsNullOrWhiteSpace(m))
                    .Select(m => m.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(id => existingById.TryGetValue(id, out var old)
                        ? old.Clone()
                        : DshModelConfig.InferDefaults(id, effectiveApi))
                    .ToList();
            }

            if (enabledModels.Count == 0)
                enabledModels.Add(DshModelConfig.InferDefaults("default", effectiveApi));

            if (lines.Count == 0)
            {
                var freshLines = new List<string> { "- id: llm-pi-ai" };
                if (profile.Kind == DshProfileKind.Desktop)
                    freshLines.Add("  name: \"@deepseek-ai/dsh-llm-pi-ai\"");
                freshLines.Add("  config:");
                freshLines.Add("    providers:");
                freshLines.AddRange(BuildProviderBlock(6, effectivePid, displayName, envKey, effectiveApi, omitApi, baseUrl, enabledModels, special, null, null));
                AtomicFile.WriteAllText(patchPath, string.Join("\n", freshLines) + "\n", new UTF8Encoding(false), keepBackup: false);
            }
            else if (hasPlugin)
            {
                if (pluginLoc.ProvidersMapLine >= 0)
                {
                    int entryIndent = pluginLoc.ProvidersMapIndent + 2;
                    if (existing != null)
                    {
                        var (topExtras, compatExtras) = ExtractPreservedSubBlocks(lines, existing.StartLine, existing.EndLine, entryIndent + 2);
                        var replacement = BuildProviderBlock(entryIndent, effectivePid, displayName, envKey, effectiveApi, omitApi, baseUrl, enabledModels, special, topExtras, compatExtras);
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
                        var block = BuildProviderBlock(entryIndent, effectivePid, displayName, envKey, effectiveApi, omitApi, baseUrl, enabledModels, special, null, null);
                        lines.InsertRange(insertAt, block);
                    }
                }
                else if (pluginLoc.ConfigLine >= 0)
                {
                    int provIndent = pluginLoc.ConfigIndent + 2;
                    int insertAt = pluginLoc.ConfigLine + 1;
                    var inject = new List<string> { new string(' ', provIndent) + "providers:" };
                    inject.AddRange(BuildProviderBlock(provIndent + 2, effectivePid, displayName, envKey, effectiveApi, omitApi, baseUrl, enabledModels, special, null, null));
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
                    inject.AddRange(BuildProviderBlock(childIndent + 4, effectivePid, displayName, envKey, effectiveApi, omitApi, baseUrl, enabledModels, special, null, null));
                    lines.InsertRange(pluginLoc.PluginEndLine + 1, inject);
                }

                string updated = JoinLines(lines, eol, trailingNewline: true);
                AtomicFile.WriteAllText(patchPath, updated, new UTF8Encoding(false), keepBackup: false);
            }
            else
            {
                if (lines.Count > 0 && lines[^1].Length > 0) lines.Add("");
                lines.Add("- id: llm-pi-ai");
                if (profile.Kind == DshProfileKind.Desktop)
                    lines.Add("  name: \"@deepseek-ai/dsh-llm-pi-ai\"");
                lines.Add("  config:");
                lines.Add("    providers:");
                lines.AddRange(BuildProviderBlock(6, effectivePid, displayName, envKey, effectiveApi, omitApi, baseUrl, enabledModels, special, null, null));

                string updated = JoinLines(lines, eol, trailingNewline: true);
                AtomicFile.WriteAllText(patchPath, updated, new UTF8Encoding(false), keepBackup: false);
            }

            if (disabledModelsToSave != null)
                SaveDisabledModelsForProvider(effectivePid, requestedPid, disabledModelsToSave);

            if (!string.Equals(requestedPid, effectivePid, StringComparison.OrdinalIgnoreCase))
            {
                DshFavorites.MigrateKey(DshFavorites.ProviderKey(requestedPid), DshFavorites.ProviderKey(effectivePid));
                draft.ProviderId = effectivePid;
            }

            return VerifyProviderPersisted(profile, effectivePid, envKey, baseUrl, backup);
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

            if (loc.Entries.Count == 1 && loc.ProvidersMapLine >= 0 && loc.ProvidersMapLine < lines.Count)
                lines[loc.ProvidersMapLine] = new string(' ', loc.ProvidersMapIndent) + "providers: {}";

            AtomicFile.WriteAllText(patchPath, JoinLines(lines, eol, hadTrail), new UTF8Encoding(false), keepBackup: false);

            var after = ParsePatchProviders(patchPath);
            bool stillThere = after.Any(x => string.Equals(x.ProviderId, providerId.Trim(), StringComparison.OrdinalIgnoreCase));
            return !stillThere
                ? new DshCredentialWriteResult(true, $"已从 {profile.DisplayName} 移除 {providerId}", backup, providerId.Trim())
                : new DshCredentialWriteResult(false, "移除后回读仍存在该条目，请查看备份", backup, providerId.Trim());
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
            return new DshCredentialWriteResult(true, $"已同步到 {profile.DisplayName}（{pid}）", backup, pid);
        }
        return new DshCredentialWriteResult(false, $"写入 {profile.DisplayName} 后磁盘回读校验失败", backup, pid);
    }

    private enum RouteSubBlock { None, DefaultInput, ThinkingBudgets, Compat, Models }
    private enum ModelSubBlock { None, Input, ReasoningEfforts, Compat }

    private sealed record ParsedPatchProvider(
        string ProviderId,
        string DisplayName,
        string ApiKeyEnv,
        string Api,
        string BaseUrl,
        List<string> Models,
        List<DshModelConfig> ModelConfigs,
        DshProviderSpecialConfig Special,
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
                        string display = pid, envKey = "", rawApi = "", baseUrl = "";
                        var special = new DshProviderSpecialConfig();
                        var modelConfigs = new List<DshModelConfig>();

                        int fieldIndent = -1;
                        RouteSubBlock subBlock = RouteSubBlock.None;

                        // models 状态机
                        int modelItemIndent = -1;
                        int modelPropIndent = -1;
                        ModelSubBlock modelSub = ModelSubBlock.None;
                        int nestedCompatIndent = -1;
                        bool inChatTemplateKwargs = false;
                        DshModelConfig? currentModel = null;

                        void FlushCurrentModel()
                        {
                            if (currentModel == null) return;
                            if (!string.IsNullOrWhiteSpace(currentModel.Id))
                            {
                                currentModel.Id = currentModel.Id.Trim();
                                if (string.IsNullOrWhiteSpace(currentModel.Name))
                                    currentModel.Name = currentModel.Id;
                                currentModel.Enabled = true;
                                NormalizeModelModalities(currentModel);
                                modelConfigs.Add(currentModel);
                            }
                            currentModel = null;
                            modelPropIndent = -1;
                            modelSub = ModelSubBlock.None;
                            nestedCompatIndent = -1;
                            inChatTemplateKwargs = false;
                        }

                        for (int m = k + 1; m <= providersEnd; m++)
                        {
                            string lm = lines[m];
                            string tm = lm.Trim();
                            if (tm.Length == 0 || tm.StartsWith("#", StringComparison.Ordinal)) continue;
                            int mind = IndentOf(lm);
                            if (mind <= entryIndent) break;
                            pEnd = m;

                            if (fieldIndent < 0 && !tm.StartsWith("-", StringComparison.Ordinal))
                                fieldIndent = mind;

                            if (fieldIndent > 0 && mind == fieldIndent && !tm.StartsWith("-", StringComparison.Ordinal))
                            {
                                FlushCurrentModel();
                                subBlock = RouteSubBlock.None;

                                if (TrySplitYamlKv(tm, out string field, out string val))
                                {
                                    switch (field)
                                    {
                                        case "displayName": display = UnquoteScalar(val); break;
                                        case "apiKeyEnv": envKey = UnquoteScalar(val); break;
                                        case "api": rawApi = UnquoteScalar(val); break;
                                        case "baseURL": baseUrl = UnquoteScalar(val); break;
                                        case "reasoning": special.Reasoning = UnquoteScalar(val).ToLowerInvariant(); break;
                                        case "cacheRetention": special.CacheRetention = UnquoteScalar(val).ToLowerInvariant(); break;
                                        case "defaultInput":
                                            if (TryParseInlineFlowList(val, out var diList))
                                                special.DefaultImageInput = diList.Any(x => string.Equals(x, "image", StringComparison.OrdinalIgnoreCase));
                                            else
                                                subBlock = RouteSubBlock.DefaultInput;
                                            break;
                                        case "thinkingBudgets":
                                            subBlock = RouteSubBlock.ThinkingBudgets;
                                            break;
                                        case "compat":
                                            subBlock = RouteSubBlock.Compat;
                                            break;
                                        case "models":
                                            subBlock = RouteSubBlock.Models;
                                            modelItemIndent = -1;
                                            break;
                                    }
                                }
                                continue;
                            }

                            if (mind <= fieldIndent) continue;

                            switch (subBlock)
                            {
                                case RouteSubBlock.DefaultInput:
                                    if (tm.StartsWith("- ", StringComparison.Ordinal))
                                    {
                                        string mod = UnquoteScalar(tm[2..].Trim());
                                        if (string.Equals(mod, "image", StringComparison.OrdinalIgnoreCase))
                                            special.DefaultImageInput = true;
                                    }
                                    break;

                                case RouteSubBlock.ThinkingBudgets:
                                    if (TrySplitYamlKv(tm, out string bKey, out string bVal)
                                        && string.Equals(bKey, "medium", StringComparison.OrdinalIgnoreCase)
                                        && int.TryParse(UnquoteScalar(bVal), out int medBudget) && medBudget > 0)
                                    {
                                        special.ThinkingBudgetMedium = medBudget;
                                    }
                                    break;

                                case RouteSubBlock.Compat:
                                    if (TrySplitYamlKv(tm, out string ck, out string cv))
                                    {
                                        string ucv = UnquoteScalar(cv);
                                        switch (ck)
                                        {
                                            case "forceAdaptiveThinking":
                                                if (bool.TryParse(ucv, out bool fat)) special.ForceAdaptiveThinking = fat;
                                                break;
                                            case "supportsCacheControlOnTools":
                                                if (bool.TryParse(ucv, out bool scc)) special.SupportsCacheControlOnTools = scc;
                                                break;
                                            case "supportsLongCacheRetention":
                                                if (bool.TryParse(ucv, out bool slc)) special.SupportsLongCacheRetention = slc;
                                                break;
                                            case "thinkingFormat":
                                                special.ThinkingFormat = ucv;
                                                break;
                                            case "supportsReasoningEffort":
                                                if (bool.TryParse(ucv, out bool sre)) special.SupportsReasoningEffort = sre;
                                                break;
                                            case "supportsDeveloperRole":
                                                if (bool.TryParse(ucv, out bool sdr)) special.SupportsDeveloperRole = sdr;
                                                break;
                                            case "requiresReasoningContentOnAssistantMessages":
                                                if (bool.TryParse(ucv, out bool rrc)) special.RequiresReasoningContent = rrc;
                                                break;
                                            case "maxTokensField":
                                                special.MaxTokensField = ucv;
                                                break;
                                            case "supportsStrictMode":
                                                if (bool.TryParse(ucv, out bool ssm)) special.SupportsStrictMode = ssm;
                                                break;
                                        }
                                    }
                                    break;

                                case RouteSubBlock.Models:
                                    // 精准锁定 models 下第一条 "- " 的缩进：只有同层缩进的 "- " 才是新模型，
                                    // input: 下面的 "- text" / "- image" 缩进更深，绝不会误当成模型 ID！
                                    if (modelItemIndent < 0 && (tm == "-" || tm.StartsWith("- ", StringComparison.Ordinal)))
                                        modelItemIndent = mind;

                                    if (modelItemIndent >= 0 && mind == modelItemIndent && (tm == "-" || tm.StartsWith("- ", StringComparison.Ordinal)))
                                    {
                                        FlushCurrentModel();
                                        currentModel = new DshModelConfig
                                        {
                                            Enabled = true,
                                            ToolUse = true,
                                            InputModalities = new List<string> { "text" },
                                        };

                                        if (tm == "-")
                                        {
                                            modelPropIndent = -1;
                                        }
                                        else
                                        {
                                            string afterDash = tm[2..].TrimStart();
                                            if (TrySplitYamlKv(afterDash, out string fk, out string fv))
                                            {
                                                modelPropIndent = mind + 2;
                                                ApplyModelField(currentModel, fk, fv, ref modelSub);
                                            }
                                            else
                                            {
                                                currentModel.Id = UnquoteScalar(afterDash);
                                                currentModel.Name = currentModel.Id;
                                                FlushCurrentModel();
                                            }
                                        }
                                        break;
                                    }

                                    if (currentModel == null || mind <= modelItemIndent) break;

                                    if (modelPropIndent < 0 && !tm.StartsWith("-", StringComparison.Ordinal))
                                        modelPropIndent = mind;

                                    if (modelPropIndent > 0 && mind == modelPropIndent && !tm.StartsWith("-", StringComparison.Ordinal))
                                    {
                                        modelSub = ModelSubBlock.None;
                                        nestedCompatIndent = -1;
                                        inChatTemplateKwargs = false;
                                        if (TrySplitYamlKv(tm, out string mField, out string mVal))
                                            ApplyModelField(currentModel, mField, mVal, ref modelSub);
                                    }
                                    else if (mind > modelPropIndent)
                                    {
                                        switch (modelSub)
                                        {
                                            case ModelSubBlock.Input:
                                                if (tm.StartsWith("- ", StringComparison.Ordinal))
                                                {
                                                    string mod = UnquoteScalar(tm[2..].Trim()).ToLowerInvariant();
                                                    if ((mod == "text" || mod == "image")
                                                        && !currentModel.InputModalities.Contains(mod, StringComparer.OrdinalIgnoreCase))
                                                    {
                                                        currentModel.InputModalities.Add(mod);
                                                    }
                                                }
                                                break;

                                            case ModelSubBlock.ReasoningEfforts:
                                                if (TrySplitYamlKv(tm, out string lvl, out string wire))
                                                {
                                                    string nl = lvl.Trim().ToLowerInvariant();
                                                    string uw = UnquoteScalar(wire).Trim();
                                                    if (nl != "off"
                                                        && DshModelConfig.AllowedReasoningLevels.Contains(nl, StringComparer.OrdinalIgnoreCase)
                                                        && uw.Length > 0 && uw != "null" && uw != "~"
                                                        && !currentModel.ReasoningLevels.Contains(nl, StringComparer.OrdinalIgnoreCase))
                                                    {
                                                        currentModel.ReasoningLevels.Add(nl);
                                                    }
                                                }
                                                break;

                                            case ModelSubBlock.Compat:
                                                if (inChatTemplateKwargs && nestedCompatIndent > 0 && mind > nestedCompatIndent)
                                                {
                                                    if (TrySplitYamlKv(tm, out string kwK, out string kwV))
                                                        currentModel.CustomKwargs[kwK] = UnquoteScalar(kwV);
                                                }
                                                else if (TrySplitYamlKv(tm, out string mck, out string mcv))
                                                {
                                                    inChatTemplateKwargs = false;
                                                    nestedCompatIndent = -1;
                                                    string umcv = UnquoteScalar(mcv);
                                                    switch (mck)
                                                    {
                                                        case "forceAdaptiveThinking":
                                                            if (bool.TryParse(umcv, out bool mfat)) currentModel.ForceAdaptiveThinking = mfat;
                                                            break;
                                                        case "thinkingFormat":
                                                            currentModel.ThinkingFormat = umcv;
                                                            break;
                                                        case "supportsReasoningEffort":
                                                            if (bool.TryParse(umcv, out bool msre)) currentModel.SupportsReasoningEffort = msre;
                                                            break;
                                                        case "chatTemplateKwargs":
                                                            inChatTemplateKwargs = true;
                                                            nestedCompatIndent = mind;
                                                            break;
                                                    }
                                                }
                                                break;
                                        }
                                    }
                                    break;
                            }
                        }

                        FlushCurrentModel();

                        bool isGoogleNative = (pid.Equals("google", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(rawApi))
                            || (string.IsNullOrWhiteSpace(rawApi)
                                && baseUrl.Contains("/v1beta", StringComparison.OrdinalIgnoreCase)
                                && !baseUrl.Contains("/v1beta/openai", StringComparison.OrdinalIgnoreCase));
                        string resolvedApi = isGoogleNative
                            ? "google-generative-ai"
                            : (string.IsNullOrWhiteSpace(rawApi) ? "openai-completions" : rawApi);
                        if (isGoogleNative && string.IsNullOrWhiteSpace(baseUrl))
                            baseUrl = "https://generativelanguage.googleapis.com/v1beta";

                        var modelIds = modelConfigs.Select(x => x.Id).ToList();
                        entries.Add(new ParsedPatchProvider(pid, display, envKey, resolvedApi, baseUrl, modelIds, modelConfigs, special, k, pEnd));
                        k = pEnd;
                    }
                }
            }

            location = new LlmPluginLocation(i, itemIndent, configLine, configIndent, providersLine, providersIndent, providersEnd, pluginEnd, entries);
            return true;
        }

        return false;
    }

    private static void ApplyModelField(DshModelConfig model, string field, string rawVal, ref ModelSubBlock sub)
    {
        string val = UnquoteScalar(rawVal);
        switch (field)
        {
            case "id":
                if (string.IsNullOrWhiteSpace(model.Id))
                {
                    model.Id = val;
                    if (string.IsNullOrWhiteSpace(model.Name)) model.Name = val;
                }
                break;
            case "name":
                model.Name = val;
                break;
            case "contextWindow":
                if (int.TryParse(val, out int cw) && cw > 0) model.ContextWindow = cw;
                break;
            case "maxTokens":
                if (int.TryParse(val, out int mt) && mt > 0) model.MaxTokens = mt;
                break;
            case "input":
                if (TryParseInlineFlowList(rawVal, out var flowMods))
                {
                    model.InputModalities = flowMods
                        .Select(x => x.ToLowerInvariant())
                        .Where(x => x is "text" or "image")
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    NormalizeModelModalities(model);
                }
                else
                {
                    model.InputModalities = new List<string>();
                    sub = ModelSubBlock.Input;
                }
                break;
            case "reasoningEfforts":
                if (string.Equals(val, "false", StringComparison.OrdinalIgnoreCase))
                {
                    model.ReasoningMode = "disabled";
                    model.ReasoningLevels.Clear();
                }
                else
                {
                    model.ReasoningMode = "custom";
                    model.ReasoningLevels.Clear();
                    sub = ModelSubBlock.ReasoningEfforts;
                }
                break;
            case "compat":
                sub = ModelSubBlock.Compat;
                break;
        }
    }

    private static void NormalizeModelModalities(DshModelConfig model)
    {
        bool hasImage = model.InputModalities.Any(x => string.Equals(x, "image", StringComparison.OrdinalIgnoreCase));
        model.InputModalities = hasImage ? new List<string> { "text", "image" } : new List<string> { "text" };
    }

    private sealed record PreservedCompatEntry(string Key, List<string> Lines);

    private static (List<string> TopExtras, List<PreservedCompatEntry> CompatExtras) ExtractPreservedSubBlocks(
        List<string> lines, int startLine, int endLine, int fieldIndent)
    {
        var topExtras = new List<string>();
        var compatExtras = new List<PreservedCompatEntry>();

        bool capturingTop = false;
        bool inCompat = false;
        int compatKeyIndent = -1;
        bool capturingCompatChild = false;
        string currentCompatKey = "";
        List<string>? currentCompatLines = null;

        void FlushCompat()
        {
            if (currentCompatLines != null && currentCompatLines.Count > 0 && currentCompatKey.Length > 0)
                compatExtras.Add(new PreservedCompatEntry(currentCompatKey, currentCompatLines));
            currentCompatLines = null;
            currentCompatKey = "";
            capturingCompatChild = false;
        }

        for (int i = startLine + 1; i <= endLine; i++)
        {
            string line = lines[i];
            string trim = line.Trim();
            if (trim.Length == 0) continue;
            int ind = IndentOf(line);

            if (ind == fieldIndent && !trim.StartsWith("-", StringComparison.Ordinal))
            {
                FlushCompat();
                int c = trim.IndexOf(':');
                string key = c > 0 ? trim[..c].Trim() : "";
                inCompat = string.Equals(key, "compat", StringComparison.Ordinal);
                compatKeyIndent = -1;
                capturingTop = !inCompat && !ManagedRouteTopKeys.Contains(key);
                if (capturingTop) topExtras.Add(line);
                continue;
            }

            if (inCompat)
            {
                if (compatKeyIndent < 0 && !trim.StartsWith("-", StringComparison.Ordinal))
                    compatKeyIndent = ind;

                if (compatKeyIndent > 0 && ind == compatKeyIndent && !trim.StartsWith("-", StringComparison.Ordinal))
                {
                    FlushCompat();
                    int c = trim.IndexOf(':');
                    string ck = c > 0 ? trim[..c].Trim() : "";
                    if (ck.Length > 0 && !ManagedRouteCompatKeys.Contains(ck))
                    {
                        capturingCompatChild = true;
                        currentCompatKey = ck;
                        currentCompatLines = new List<string> { trim };
                    }
                }
                else if (capturingCompatChild && currentCompatLines != null && ind > compatKeyIndent)
                {
                    int delta = ind - compatKeyIndent;
                    currentCompatLines.Add(new string(' ', delta) + trim);
                }
            }
            else if (capturingTop)
            {
                topExtras.Add(line);
            }
        }
        FlushCompat();
        return (topExtras, compatExtras);
    }

    private static List<string> BuildProviderBlock(
        int baseIndent,
        string providerId,
        string displayName,
        string apiKeyEnv,
        string api,
        bool omitApi,
        string baseUrl,
        IReadOnlyList<DshModelConfig> enabledModels,
        DshProviderSpecialConfig special,
        List<string>? topExtras,
        List<PreservedCompatEntry>? compatExtras)
    {
        string p0 = new(' ', baseIndent);
        string p1 = new(' ', baseIndent + 2);
        string p2 = new(' ', baseIndent + 4);
        string p3 = new(' ', baseIndent + 6);

        var outLines = new List<string>
        {
            p0 + providerId + ":",
            p1 + "displayName: " + YamlSafeScalar(displayName),
            p1 + "apiKeyEnv: " + YamlSafeScalar(apiKeyEnv),
        };
        if (!omitApi)
            outLines.Add(p1 + "api: " + YamlSafeScalar(api));
        outLines.Add(p1 + "baseURL: " + YamlSafeScalar(baseUrl));

        // 路由级思考/缓存/模态
        string reasoning = (special.Reasoning ?? "").Trim().ToLowerInvariant();
        if (reasoning.Length > 0 && ValidThinkingLevels.Contains(reasoning))
            outLines.Add(p1 + "reasoning: " + reasoning);

        string cache = (special.CacheRetention ?? "").Trim().ToLowerInvariant();
        if (cache.Length > 0 && ValidCacheRetentions.Contains(cache))
            outLines.Add(p1 + "cacheRetention: " + cache);

        if (special.DefaultImageInput)
        {
            outLines.Add(p1 + "defaultInput:");
            outLines.Add(p2 + "- text");
            outLines.Add(p2 + "- image");
        }

        if (string.Equals(api, "google-generative-ai", StringComparison.OrdinalIgnoreCase)
            && special.ThinkingBudgetMedium > 0)
        {
            int med = Math.Clamp(special.ThinkingBudgetMedium, 1024, 32768);
            int low = Math.Max(1024, med / 2);
            int high = Math.Min(32768, med * 2);
            outLines.Add(p1 + "thinkingBudgets:");
            outLines.Add(p2 + "minimal: 1024");
            outLines.Add(p2 + "low: " + low.ToString(CultureInfo.InvariantCulture));
            outLines.Add(p2 + "medium: " + med.ToString(CultureInfo.InvariantCulture));
            outLines.Add(p2 + "high: " + high.ToString(CultureInfo.InvariantCulture));
        }

        // 路由级 compat（按 COMPAT_GATES 严格过滤，Google 协议绝不写出）
        var routeCompatLines = BuildFilteredRouteCompatLines(p2, api, special, compatExtras);
        if (routeCompatLines.Count > 0)
        {
            outLines.Add(p1 + "compat:");
            outLines.AddRange(routeCompatLines);
        }

        // 结构化 models 列表
        outLines.Add(p1 + "models:");
        foreach (var mc in enabledModels)
        {
            outLines.Add(p2 + "- id: " + YamlSafeScalar(mc.Id));
            outLines.Add(p2 + "  name: " + YamlSafeScalar(mc.EffectiveDisplayName));
            if (mc.ContextWindow > 0)
                outLines.Add(p2 + "  contextWindow: " + mc.ContextWindow.ToString(CultureInfo.InvariantCulture));
            if (mc.MaxTokens > 0)
                outLines.Add(p2 + "  maxTokens: " + mc.MaxTokens.ToString(CultureInfo.InvariantCulture));

            outLines.Add(p2 + "  input:");
            outLines.Add(p2 + "    - text");
            if (mc.SupportsImage)
                outLines.Add(p2 + "    - image");

            if (string.Equals(mc.ReasoningMode, "disabled", StringComparison.OrdinalIgnoreCase))
            {
                outLines.Add(p2 + "  reasoningEfforts: false");
            }
            else if (string.Equals(mc.ReasoningMode, "custom", StringComparison.OrdinalIgnoreCase))
            {
                var lvls = mc.ReasoningLevels
                    .Select(l => (l ?? "").Trim().ToLowerInvariant())
                    .Where(l => DshModelConfig.AllowedReasoningLevels.Contains(l, StringComparer.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (lvls.Count > 0)
                {
                    outLines.Add(p2 + "  reasoningEfforts:");
                    outLines.Add(p3 + "  off: null");
                    foreach (string lvl in lvls)
                        outLines.Add(p3 + "  " + lvl + ": " + lvl);
                }
            }

            var modelCompat = BuildFilteredModelCompatLines(p3 + "  ", api, mc);
            if (modelCompat.Count > 0)
            {
                outLines.Add(p2 + "  compat:");
                outLines.AddRange(modelCompat);
            }
        }

        if (topExtras is { Count: > 0 })
            outLines.AddRange(topExtras);
        return outLines;
    }

    private static HashSet<string>? CompatGateForProtocol(string api) => (api ?? "").Trim().ToLowerInvariant() switch
    {
        "openai-completions" => CompletionsCompatGate,
        "openai-responses" => ResponsesCompatGate,
        "anthropic-messages" => AnthropicCompatGate,
        _ => null,
    };

    private static List<string> BuildFilteredRouteCompatLines(
        string indent, string api, DshProviderSpecialConfig sp, List<PreservedCompatEntry>? compatExtras)
    {
        var gate = CompatGateForProtocol(api);
        if (gate == null) return new List<string>();

        var lines = new List<string>();
        string normApi = (api ?? "").Trim().ToLowerInvariant();
        if (normApi == "anthropic-messages")
        {
            if (sp.ForceAdaptiveThinking.HasValue)
                lines.Add(indent + "forceAdaptiveThinking: " + YamlBool(sp.ForceAdaptiveThinking.Value));
            if (sp.SupportsCacheControlOnTools.HasValue)
                lines.Add(indent + "supportsCacheControlOnTools: " + YamlBool(sp.SupportsCacheControlOnTools.Value));
            if (sp.SupportsLongCacheRetention.HasValue)
                lines.Add(indent + "supportsLongCacheRetention: " + YamlBool(sp.SupportsLongCacheRetention.Value));
        }
        else if (normApi == "openai-completions")
        {
            if (!string.IsNullOrWhiteSpace(sp.ThinkingFormat))
                lines.Add(indent + "thinkingFormat: " + YamlSafeScalar(sp.ThinkingFormat.Trim()));
            if (sp.SupportsReasoningEffort.HasValue)
                lines.Add(indent + "supportsReasoningEffort: " + YamlBool(sp.SupportsReasoningEffort.Value));
            if (sp.SupportsDeveloperRole.HasValue)
                lines.Add(indent + "supportsDeveloperRole: " + YamlBool(sp.SupportsDeveloperRole.Value));
            if (sp.RequiresReasoningContent.HasValue)
                lines.Add(indent + "requiresReasoningContentOnAssistantMessages: " + YamlBool(sp.RequiresReasoningContent.Value));
            if (!string.IsNullOrWhiteSpace(sp.MaxTokensField))
                lines.Add(indent + "maxTokensField: " + YamlSafeScalar(sp.MaxTokensField.Trim()));
            if (sp.SupportsStrictMode.HasValue)
                lines.Add(indent + "supportsStrictMode: " + YamlBool(sp.SupportsStrictMode.Value));
            if (sp.SupportsLongCacheRetention.HasValue)
                lines.Add(indent + "supportsLongCacheRetention: " + YamlBool(sp.SupportsLongCacheRetention.Value));
        }
        else if (normApi == "openai-responses")
        {
            if (sp.SupportsDeveloperRole.HasValue)
                lines.Add(indent + "supportsDeveloperRole: " + YamlBool(sp.SupportsDeveloperRole.Value));
            if (sp.SupportsStrictMode.HasValue)
                lines.Add(indent + "supportsStrictMode: " + YamlBool(sp.SupportsStrictMode.Value));
            if (sp.SupportsLongCacheRetention.HasValue)
                lines.Add(indent + "supportsLongCacheRetention: " + YamlBool(sp.SupportsLongCacheRetention.Value));
        }

        if (compatExtras != null)
        {
            foreach (var extra in compatExtras)
            {
                if (!gate.Contains(extra.Key)) continue;
                foreach (string subLine in extra.Lines)
                    lines.Add(indent + subLine);
            }
        }
        return lines;
    }

    private static List<string> BuildFilteredModelCompatLines(string indent, string api, DshModelConfig mc)
    {
        string normApi = (api ?? "").Trim().ToLowerInvariant();
        var lines = new List<string>();
        if (normApi == "anthropic-messages")
        {
            if (mc.ForceAdaptiveThinking.HasValue)
                lines.Add(indent + "forceAdaptiveThinking: " + YamlBool(mc.ForceAdaptiveThinking.Value));
        }
        else if (normApi == "openai-completions")
        {
            if (!string.IsNullOrWhiteSpace(mc.ThinkingFormat))
                lines.Add(indent + "thinkingFormat: " + YamlSafeScalar(mc.ThinkingFormat.Trim()));
            if (mc.SupportsReasoningEffort.HasValue)
                lines.Add(indent + "supportsReasoningEffort: " + YamlBool(mc.SupportsReasoningEffort.Value));

            var cleanKwargs = mc.CustomKwargs
                .Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                .ToList();
            if (cleanKwargs.Count > 0)
            {
                lines.Add(indent + "chatTemplateKwargs:");
                foreach (var kv in cleanKwargs)
                    lines.Add(indent + "  " + kv.Key.Trim() + ": " + YamlKwargValue(kv.Value.Trim()));
            }
        }
        return lines;
    }

    // ══════════════ 在线探测 /models 与单模型连通性测速 ══════════════

    public static Task<(bool Ok, string Message, IReadOnlyList<string> Models)> ProbeEndpointModelsAsync(
        string baseUrl,
        string? apiKey,
        CancellationToken ct = default)
        => ProbeEndpointModelsAsync(baseUrl, apiKey, null, ct);

    public static async Task<(bool Ok, string Message, IReadOnlyList<string> Models)> ProbeEndpointModelsAsync(
        string baseUrl,
        string? apiKey,
        string? protocol,
        CancellationToken ct = default)
    {
        string trimmed = (baseUrl ?? "").Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out _))
            return (false, "Base URL 无效", Array.Empty<string>());

        string proto = (protocol ?? "").Trim().ToLowerInvariant();
        string? key = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        var candidates = new List<(string Url, bool GoogleQueryKey)>();

        if (proto == "google-generative-ai" || trimmed.Contains("/v1beta", StringComparison.OrdinalIgnoreCase))
        {
            string gBase = trimmed.EndsWith("/models", StringComparison.OrdinalIgnoreCase)
                ? trimmed
                : trimmed + "/models";
            candidates.Add((gBase, false));
            if (key != null)
                candidates.Add((gBase + (gBase.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "key=" + Uri.EscapeDataString(key), true));

            string normG = SwitchBaseUrlForProtocol(trimmed, "google-generative-ai") + "/models";
            if (!string.Equals(normG, gBase, StringComparison.OrdinalIgnoreCase))
                candidates.Add((normG, false));
            string openAiFallback = SwitchBaseUrlForProtocol(trimmed, "openai-completions") + "/models";
            candidates.Add((openAiFallback, false));
        }
        else if (trimmed.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add((trimmed, false));
        }
        else
        {
            candidates.Add((trimmed + "/models", false));
            if (!trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                candidates.Add((trimmed + "/v1/models", false));
        }

        string lastError = "未获取到模型列表";
        foreach (var (endpoint, _) in candidates.DistinctBy(c => c.Url))
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                if (key != null)
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                    req.Headers.TryAddWithoutValidation("x-api-key", key);
                    req.Headers.TryAddWithoutValidation("x-goog-api-key", key);
                    req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                }

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(8));
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
                lastError = "请求超时（8 秒）";
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
            }
        }

        return (false, lastError, Array.Empty<string>());
    }

    public static async Task<(bool Ok, int LatencyMs, string Detail)> TestModelConnectivityAsync(
        string baseUrl,
        string protocol,
        string? apiKey,
        string modelId,
        CancellationToken ct = default)
    {
        string url = (baseUrl ?? "").Trim().TrimEnd('/');
        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
            return (false, 0, "Base URL 无效");
        string mid = (modelId ?? "").Trim();
        if (mid.Length == 0)
            return (false, 0, "模型 ID 不能为空");

        string proto = (protocol ?? "openai-completions").Trim().ToLowerInvariant();
        string? key = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        var sw = Stopwatch.StartNew();

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(12));

            async Task<(int Code, string Body)> PostJsonAsync(string targetUrl, string jsonPayload, bool useAnthropicHeader, bool useGoogleHeader, bool useBearer)
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, targetUrl);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                if (key != null)
                {
                    if (useBearer) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                    if (useAnthropicHeader)
                    {
                        req.Headers.TryAddWithoutValidation("x-api-key", key);
                        req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                    }
                    if (useGoogleHeader)
                    {
                        req.Headers.TryAddWithoutValidation("x-goog-api-key", key);
                    }
                }
                req.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                using var resp = await ProbeHttp.SendAsync(req, timeoutCts.Token).ConfigureAwait(false);
                string body = await resp.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
                return ((int)resp.StatusCode, body);
            }

            int status;
            string respBody;

            if (proto == "anthropic-messages")
            {
                string endpoint = SwitchBaseUrlForProtocol(url, "anthropic-messages") + "/v1/messages";
                string payload = JsonSerializer.Serialize(new
                {
                    model = mid,
                    max_tokens = 16,
                    messages = new[] { new { role = "user", content = "Hi" } },
                });
                (status, respBody) = await PostJsonAsync(endpoint, payload, useAnthropicHeader: true, useGoogleHeader: false, useBearer: false).ConfigureAwait(false);
                if (status is 401 or 403 && key != null)
                {
                    (status, respBody) = await PostJsonAsync(endpoint, payload, useAnthropicHeader: true, useGoogleHeader: false, useBearer: true).ConfigureAwait(false);
                }
            }
            else if (proto == "google-generative-ai")
            {
                string endpoint = SwitchBaseUrlForProtocol(url, "google-generative-ai")
                    + "/models/" + Uri.EscapeDataString(mid) + ":generateContent";
                string payload = JsonSerializer.Serialize(new
                {
                    contents = new[] { new { role = "user", parts = new[] { new { text = "Hi" } } } },
                    generationConfig = new { maxOutputTokens = 16 },
                });
                (status, respBody) = await PostJsonAsync(endpoint, payload, useAnthropicHeader: false, useGoogleHeader: true, useBearer: false).ConfigureAwait(false);
                if (status is 401 or 403 && key != null)
                {
                    (status, respBody) = await PostJsonAsync(endpoint, payload, useAnthropicHeader: false, useGoogleHeader: true, useBearer: true).ConfigureAwait(false);
                }
            }
            else if (proto == "openai-responses")
            {
                string endpoint = SwitchBaseUrlForProtocol(url, "openai-responses") + "/responses";
                string payload = JsonSerializer.Serialize(new
                {
                    model = mid,
                    input = "Hi",
                    max_output_tokens = 16,
                });
                (status, respBody) = await PostJsonAsync(endpoint, payload, useAnthropicHeader: false, useGoogleHeader: false, useBearer: true).ConfigureAwait(false);
            }
            else
            {
                string endpoint = SwitchBaseUrlForProtocol(url, "openai-completions") + "/chat/completions";
                string payload = JsonSerializer.Serialize(new
                {
                    model = mid,
                    max_tokens = 16,
                    messages = new[] { new { role = "user", content = "Hi" } },
                });
                (status, respBody) = await PostJsonAsync(endpoint, payload, useAnthropicHeader: false, useGoogleHeader: false, useBearer: true).ConfigureAwait(false);
                if (status == 400 && respBody.Contains("max_completion_tokens", StringComparison.OrdinalIgnoreCase))
                {
                    string retryPayload = JsonSerializer.Serialize(new
                    {
                        model = mid,
                        max_completion_tokens = 16,
                        messages = new[] { new { role = "user", content = "Hi" } },
                    });
                    (status, respBody) = await PostJsonAsync(endpoint, retryPayload, useAnthropicHeader: false, useGoogleHeader: false, useBearer: true).ConfigureAwait(false);
                }
            }

            int elapsed = (int)Math.Max(1, sw.ElapsedMilliseconds);
            if (status is >= 200 and < 300)
                return (true, elapsed, $"连通正常 ({elapsed}ms)");

            string errDetail = ExtractSafeHttpError(respBody);
            string msg = string.IsNullOrEmpty(errDetail) ? $"HTTP {status}" : $"HTTP {status}: {errDetail}";
            return (false, elapsed, msg);
        }
        catch (OperationCanceledException)
        {
            return (false, (int)sw.ElapsedMilliseconds, "连接超时（12 秒）");
        }
        catch (Exception ex)
        {
            return (false, (int)sw.ElapsedMilliseconds, "连接异常：" + ex.Message);
        }
    }

    private static string ExtractSafeHttpError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        string msg = "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("error", out var err))
                {
                    if (err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var em) && em.ValueKind == JsonValueKind.String)
                        msg = em.GetString() ?? "";
                    else if (err.ValueKind == JsonValueKind.String)
                        msg = err.GetString() ?? "";
                }
                else if (root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                {
                    msg = m.GetString() ?? "";
                }
            }
        }
        catch
        {
            msg = body.Trim();
        }
        msg = Regex.Replace(msg.Replace('\r', ' ').Replace('\n', ' ').Trim(), @"\s+", " ");
        msg = Regex.Replace(msg, @"sk-[A-Za-z0-9_-]{8,}", "sk-••••");
        return msg.Length > 64 ? msg[..64] + "…" : msg;
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
        return list.Distinct(StringComparer.Ordinal).Take(200).ToList();
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

    private static bool TrySplitYamlKv(string trimmedLine, out string key, out string rawVal)
    {
        key = "";
        rawVal = "";
        int c = trimmedLine.IndexOf(':');
        if (c <= 0) return false;
        // 确认冒号后面是行尾或空白，防止把含冒号的 URL/模型 ID 误切成键值对
        if (c + 1 < trimmedLine.Length && !char.IsWhiteSpace(trimmedLine[c + 1])) return false;
        key = UnquoteScalar(trimmedLine[..c].Trim());
        rawVal = c + 1 < trimmedLine.Length ? trimmedLine[(c + 1)..].Trim() : "";
        return key.Length > 0;
    }

    private static bool TryParseInlineFlowList(string rawVal, out List<string> items)
    {
        items = new List<string>();
        string t = (rawVal ?? "").Trim();
        if (!t.StartsWith("[", StringComparison.Ordinal) || !t.EndsWith("]", StringComparison.Ordinal))
            return false;
        string inner = t[1..^1].Trim();
        if (inner.Length == 0) return true;
        foreach (string part in inner.Split(','))
        {
            string u = UnquoteScalar(part.Trim());
            if (u.Length > 0) items.Add(u);
        }
        return true;
    }

    private static string YamlBool(bool b) => b ? "true" : "false";

    private static string YamlKwargValue(string raw)
    {
        if (raw.Equals("true", StringComparison.OrdinalIgnoreCase)) return "true";
        if (raw.Equals("false", StringComparison.OrdinalIgnoreCase)) return "false";
        if (raw.Equals("null", StringComparison.OrdinalIgnoreCase) || raw == "~") return "null";
        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) return raw;
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && double.IsFinite(d)) return raw;
        return YamlSafeScalar(raw);
    }

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
