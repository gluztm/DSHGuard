using System;
using System.Collections.Generic;

namespace DSHGuard.Unified;

/// <summary>
/// 综合管理里「星标收藏」的统一入口。
///
/// 为什么单独一层：五个分区（插件 / 外部工具 / 技能 / 提示词 / 会话）都要判断
/// 「这一项收藏了没有」，如果每张卡片都去读一次 json 就是几百次磁盘 IO。
/// 这里读一次缓存住，切换收藏时只失效一次。
///
/// 键的形状固定成 <c>&lt;分区&gt;:&lt;身份&gt;</c>，分区前缀由本类的 Key 方法统一给出，
/// 界面层不要自己拼字符串——拼错了收藏就会「点了没反应」。
/// </summary>
public static class DshFavorites
{
    private static readonly object Gate = new();
    private static HashSet<string>? _cache;

    /// <summary>扔掉缓存，下次判断时重新读盘（存档被外部改动时也叫它）。</summary>
    public static void Invalidate()
    {
        lock (Gate) _cache = null;
    }

    private static HashSet<string> Current()
    {
        lock (Gate)
        {
            if (_cache != null) return _cache;

            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string key in DshUnifiedStore.Load().Favorites) set.Add(key);
            }
            catch (Exception ex)
            {
                Logger.LogError("DshFavorites.Current", ex);
            }

            _cache = set;
            return set;
        }
    }

    public static bool IsStarred(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        return Current().Contains(key);
    }

    /// <summary>收藏 / 取消收藏。返回切换后的真实状态（落盘失败时返回盘上真实值）。</summary>
    public static bool Toggle(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;

        DshUnifiedStore.Mutate(doc =>
        {
            doc.Favorites ??= new List<string>();
            int at = doc.Favorites.FindIndex(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
            if (at >= 0) doc.Favorites.RemoveAt(at);
            else doc.Favorites.Add(key);
        });

        Invalidate();
        return IsStarred(key);
    }

    /// <summary>统计某一分区的收藏条数（工具栏摘要用）。</summary>
    public static int CountIn(string section)
    {
        string prefix = section + ":";
        int n = 0;
        foreach (string key in Current())
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) n++;
        return n;
    }

    // ── 键构造：界面层只用这几个 ──────────────────────────────────────

    public static string PluginKey(GuardTarget target, string packageName)
        => "plugin:" + TargetId(target) + ":" + (packageName ?? "");

    /// <summary>
    /// 插件行级收藏键。插件页是「一行两列」的矩阵（同一个插件在 Web 引擎和桌面版
    /// 各有一格），用户收藏的是**这一行**，不是某一边，所以行级键不带 target。
    /// </summary>
    public static string PluginRowKey(string packageName)
        => "plugin:row:" + (packageName ?? "");

    public static string McpKey(string profileId, string serverName)
        => "mcp:" + (profileId ?? "") + ":" + (serverName ?? "");

    public static string SkillKey(string skillFilePath)
        => "skill:" + (skillFilePath ?? "");

    public static string PromptKey(string promptId)
        => "prompt:" + (promptId ?? "");

    public static string SessionKey(string sessionId)
        => "session:" + (sessionId ?? "");

    private static string TargetId(GuardTarget target)
        => target == GuardTarget.Desktop ? "desktop" : "web";
}
