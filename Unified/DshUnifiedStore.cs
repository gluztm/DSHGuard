using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DSHGuard.Unified;

/// <summary>
/// 一条 DSH 会话（<c>~/.dsh/sessions/&lt;项目&gt;/&lt;uuid&gt;/session.*.zstd</c>）。
/// 只描述磁盘上真实存在的那份文件，不解析会话内容。
/// </summary>
public sealed class DshSession
{
    /// <summary>会话目录名，即 session uuid。</summary>
    public string SessionId { get; set; } = "";

    /// <summary>项目目录还原后的完整路径（解码失败时是原样的转义名）。</summary>
    public string ProjectDir { get; set; } = "";

    /// <summary>项目目录的叶子名，给界面显示。</summary>
    public string ProjectName { get; set; } = "";

    /// <summary>会话文件的完整路径。</summary>
    public string FilePath { get; set; } = "";

    public long SizeBytes { get; set; }

    /// <summary>最后写入时间（Unix 毫秒），用于倒序排列。</summary>
    public long LastWriteUnix { get; set; }

    public DateTimeOffset LastWriteTime =>
        DateTimeOffset.FromUnixTimeMilliseconds(LastWriteUnix < 0 ? 0 : LastWriteUnix).ToLocalTime();

    /// <summary>
    /// 会话的真标题，来自 DSH 自己的会话缓存
    /// （<c>~/.dsh/storages/session_projcache/sessions/&lt;uuid&gt;.json</c> 的
    /// <c>record.rows.title.val</c>）。拿不到时是空串。
    ///
    /// ⚠ 不用 <c>~/.dsh/sessions/&lt;项目&gt;/&lt;uuid&gt;/session.*.zstd</c> 解压取标题：
    ///   实测本机 83 个会话里，缓存能给出 62 个标题，剩下的 21 个是
    ///   330–3518 字节的空壳会话，解压全文只有 <c>{"type":"session",…}</c> 两行、
    ///   **根本没有 title 事件** —— 解 zstd 在这个数据源上零收益，
    ///   不值得为它把「零第三方依赖」打破一次。
    /// </summary>
    public string Title { get; set; } = "";

    /// <summary>是否拿到了真标题。</summary>
    public bool HasTitle => Title.Length > 0;

    /// <summary>没标题时的安定短名（前 8 位 id）。</summary>
    public string ShortId =>
        SessionId.Length > 8 ? SessionId.Substring(0, 8) : SessionId;

    /// <summary>
    /// 给界面用的名字：优先真标题，没有才退回短 id。
    /// 不造假标题（不拿第一条用户消息硬凑），否则用户会以为那是 DSH 起的名字。
    /// </summary>
    public string DisplayName => HasTitle ? Title : ShortId;

    /// <summary>最后写入时间的本地短文本，界面用。</summary>
    public string DisplayTime => LastWriteTime.ToString("MM-dd HH:mm");

    /// <summary>派生它的父会话 id；不是子代理会话时是空串。</summary>
    public string ParentId { get; set; } = "";

    /// <summary>父会话给它起的名字（子代理 label）；拿不到时是空串。</summary>
    public string ParentLabel { get; set; } = "";

    /// <summary>它派生出来的子代理会话 id（来自父会话缓存里的 subagentCatalog）。</summary>
    public List<string> ChildIds { get; set; } = new List<string>();

    /// <summary>它是别的会话派生出来的子代理会话。</summary>
    public bool IsChild => ParentId.Length > 0;

    /// <summary>它派生过子代理会话。</summary>
    public bool HasChildren => ChildIds.Count > 0;

    /// <summary>DSH 那边的原始项目目录名（转义过的）。</summary>
    public string RawProjectDir { get; set; } = "";
}

/// <summary>
/// 综合管理的界面偏好存档。
/// 只存界面状态（上次停在哪个分区、哪个 profile），**不存任何资源真值**——
/// 资源永远以磁盘实时扫描为准，存档坏了/丢了都不会让界面显示错误数据。
/// </summary>
public sealed class DshUnifiedDocument
{
    /// <summary>存档结构版本。读到的值小于当前版本时按缺省补全，不丢用户已选项。</summary>
    public int SchemaVersion { get; set; } = DshUnifiedStore.CurrentSchema;

    /// <summary>上次停留的分区：plugin / mcp / skill / prompt / session。</summary>
    public string LastSection { get; set; } = "plugin";

    /// <summary>上次选择的 profile id（web / desktop）。</summary>
    public string LastProfile { get; set; } = "";

    /// <summary>
    /// 星标收藏的项。键统一是 <c>&lt;分区&gt;:&lt;身份&gt;</c>：
    /// <c>plugin:&lt;target&gt;:&lt;包名&gt;</c>、<c>mcp:&lt;profile&gt;:&lt;服务名&gt;</c>、
    /// <c>skill:&lt;SKILL.md 绝对路径&gt;</c>、<c>prompt:&lt;id&gt;</c>、<c>session:&lt;uuid&gt;</c>。
    ///
    /// 收藏是**用户自己的排序偏好**，不是安全设置：收藏项排到各组最前面，
    /// 并且在取消收藏之前不显示「删除 / 移走」——避免手滑删掉自己天天用的东西。
    /// </summary>
    public List<string> Favorites { get; set; } = new();

    public DshUnifiedDocument Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        LastSection = LastSection,
        LastProfile = LastProfile,
        Favorites = new List<string>(Favorites ?? new List<string>()),
    };
}

/// <summary>
/// 偏好存档的读写。位置固定为 <c>&lt;ConfigDir&gt;/Unified/dsh-unified.json</c>。
///
/// 三条硬约束：
/// 1. 读不到/读坏了都返回**健全的缺省值**，永不抛给界面；
/// 2. 写入走 <see cref="AtomicFile.WriteAllText"/>（原子写 + 备份），不会写坏原文件；
/// 3. 写入前先读一遍：读失败时拒绝写回，避免用缺省值覆盖用户盘上已有的偏好。
/// </summary>
public static class DshUnifiedStore
{
    public const int CurrentSchema = 1;

    private static readonly object Gate = new();

    public static string StorePath => Path.Combine(GuardPaths.ConfigDir, "Unified", "dsh-unified.json");

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>读存档；文件不存在或内容不可用时返回缺省文档。</summary>
    public static DshUnifiedDocument Load()
    {
        lock (Gate) return LoadCore();
    }

    private static DshUnifiedDocument LoadCore()
    {
        try
        {
            if (!File.Exists(StorePath)) return Defaults();

            string text = AtomicFile.ReadAllTextWithBackup(StorePath, IsUsable, out bool usedBackup);
            if (usedBackup) Logger.NoteDiagnosis($"综合管理偏好存档损坏，已回退到备份：{StorePath}");

            DshUnifiedDocument? doc = Parse(text);
            if (doc == null) return Defaults();
            return Normalize(doc);
        }
        catch (Exception ex)
        {
            Logger.LogError("DshUnifiedStore.Load", ex);
            return Defaults();
        }
    }

    /// <summary>
    /// 读—改—写。返回是否真的落盘。
    /// 读失败（文件存在但读不出来）时返回 false 且不写，避免覆盖用户盘上已有的偏好。
    /// </summary>
    public static bool Mutate(Action<DshUnifiedDocument> mutate)
    {
        if (mutate == null) return false;

        lock (Gate)
        {
            DshUnifiedDocument current;
            try
            {
                if (File.Exists(StorePath))
                {
                    string text = AtomicFile.ReadAllTextWithBackup(StorePath, IsUsable, out bool usedBackup);
                    DshUnifiedDocument? parsed = Parse(text);
                    if (parsed == null && !usedBackup)
                    {
                        Logger.NoteDiagnosis($"综合管理偏好存档无法解析，本次拒绝写入以免覆盖（{StorePath}）");
                        return false;
                    }
                    current = parsed == null ? Defaults() : Normalize(parsed);
                }
                else
                {
                    current = Defaults();
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("DshUnifiedStore.Mutate(read)", ex);
                Logger.NoteDiagnosis($"综合管理偏好读取失败，本次拒绝写入以免覆盖用户设置（{StorePath}）");
                return false;
            }

            DshUnifiedDocument next = current.Clone();
            try { mutate(next); }
            catch (Exception ex)
            {
                Logger.LogError("DshUnifiedStore.Mutate(apply)", ex);
                return false;
            }

            next = Normalize(next);

            try
            {
                string? dir = Path.GetDirectoryName(StorePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                AtomicFile.WriteAllText(StorePath, JsonSerializer.Serialize(next, WriteOptions));
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError("DshUnifiedStore.Mutate(write)", ex);
                Logger.NoteDiagnosis($"综合管理偏好写入失败（{StorePath}）：{ex.Message}");
                return false;
            }
        }
    }

    private static DshUnifiedDocument Defaults() => new()
    {
        SchemaVersion = CurrentSchema,
        LastSection = "plugin",
        LastProfile = DefaultProfile(),
    };

    /// <summary>缺省 profile：取第一个真实存在的；一个都没有时退回 web。</summary>
    private static string DefaultProfile()
    {
        try
        {
            var existing = new List<DshProfileInfo>(DshProfileRegistry.Existing());
            if (existing.Count > 0) return existing[0].Id;
        }
        catch (Exception ex)
        {
            Logger.LogError("DshUnifiedStore.DefaultProfile", ex);
        }
        return "web";
    }

    private static DshUnifiedDocument? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            return JsonSerializer.Deserialize<DshUnifiedDocument>(text);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>可解析即视为可用（备份回落判定用）。</summary>
    private static bool IsUsable(string text) => Parse(text) != null;

    /// <summary>补全缺省值：版本号、空分区、不存在的 profile 都拉回有效值。</summary>
    private static DshUnifiedDocument Normalize(DshUnifiedDocument doc)
    {
        doc.SchemaVersion = doc.SchemaVersion > 0 ? doc.SchemaVersion : CurrentSchema;

        doc.LastSection = NormalizeSection(doc.LastSection);

        string profile = doc.LastProfile ?? "";
        if (profile.Length == 0) profile = DefaultProfile();
        else if (!ProfileExists(profile)) profile = DefaultProfile();
        doc.LastProfile = profile;

        // 收藏键：去空、去重、挡掉异常长行（存档是用户可编辑的文本文件，
        // 坏一行不该让整页排序乱掉）。
        doc.Favorites ??= new List<string>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keptKeys = new List<string>(doc.Favorites.Count);
        foreach (string rawKey in doc.Favorites)
        {
            string key = (rawKey ?? "").Trim();
            if (key.Length == 0 || key.Length > 512) continue;
            if (key.IndexOf(':') < 0) continue;
            if (!seenKeys.Add(key)) continue;
            keptKeys.Add(key);
        }
        doc.Favorites = keptKeys;

        return doc;
    }

    private static string NormalizeSection(string? section)
    {
        string s = (section ?? "").Trim().ToLowerInvariant();
        return s switch
        {
            "plugin" => "plugin",
            "mcp" => "mcp",
            "skill" => "skill",
            "prompt" => "prompt",
            "session" => "session",
            _ => "plugin",
        };
    }

    private static bool ProfileExists(string id)
    {
        try
        {
            foreach (DshProfileInfo p in DshProfileRegistry.Existing())
                if (string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch (Exception ex)
        {
            Logger.LogError("DshUnifiedStore.ProfileExists", ex);
        }
        return false;
    }
}
