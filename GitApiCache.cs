using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace DSHGuard;

/// <summary>
/// GitHub / gitee 等代码托管站 API 的进程内缓存（2026-10-16 审计 M）。
/// <para>
/// 未认证的 GitHub API 每小时只给 60 次；一个 git 源插件的更新检查要问分支、标签（可能翻页）、
/// 提交详情好几次，插件页每刷新一轮就全部重问一遍。现场日志里同一个 404 的插件被问了 72 次。
/// </para>
/// <list type="bullet">
/// <item>成功响应缓存 <see cref="OkTtl"/>；</item>
/// <item>404 缓存 <see cref="NotFoundTtl"/>（仓库没了 / 改名，短时间内不会自己回来）；</item>
/// <item>403 / 429 且是限流（<c>X-RateLimit-Remaining: 0</c> 或 <c>Retry-After</c>）⇒ 到重置时间前一律不再发请求。</item>
/// </list>
/// 缓存的失败会重新抛出**同状态码**的 <see cref="HttpRequestException"/>，调用方既有的 404 分支与
/// <c>ClassifyRefProbeFailure</c> 分诊一个字都不用改。只缓存在内存里，重启即清空。
/// </summary>
internal static class GitApiCache
{
    internal static readonly TimeSpan OkTtl = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan NotFoundTtl = TimeSpan.FromMinutes(30);
    private const int MaxEntries = 2000;

    private sealed record Entry(DateTime ExpiresUtc, string? Body, HttpStatusCode? FailStatus, string FailMessage);

    private static readonly ConcurrentDictionary<string, Entry> _cache = new(StringComparer.Ordinal);

    /// <summary>某个主机被限流到什么时候（UTC）；期间该主机的请求直接按 403 失败。</summary>
    private static readonly ConcurrentDictionary<string, DateTime> _throttledUntil = new(StringComparer.OrdinalIgnoreCase);

    internal static async Task<string> GetStringAsync(HttpClient http, string url)
    {
        var now = DateTime.UtcNow;
        if (_cache.TryGetValue(url, out var e) && e.ExpiresUtc > now)
        {
            if (e.Body != null) return e.Body;
            throw new HttpRequestException(e.FailMessage, null, e.FailStatus);
        }

        string host = "";
        try { host = new Uri(url).Host; } catch { }
        if (host.Length > 0 && _throttledUntil.TryGetValue(host, out var until) && until > now)
            throw new HttpRequestException(
                $"{host} 接口限流中，{until.ToLocalTime():HH:mm} 后再试（本次未发请求）", null, HttpStatusCode.Forbidden);

        using var resp = await http.GetAsync(url);
        if (resp.IsSuccessStatusCode)
        {
            string body = await resp.Content.ReadAsStringAsync();
            Put(url, new Entry(now + OkTtl, body, null, ""));
            return body;
        }

        var code = resp.StatusCode;
        string msg = $"Response status code does not indicate success: {(int)code} ({resp.ReasonPhrase}).";
        if (code == HttpStatusCode.NotFound)
        {
            Put(url, new Entry(now + NotFoundTtl, null, code, msg));
        }
        else if (code == HttpStatusCode.Forbidden || (int)code == 429)
        {
            DateTime? reset = RateLimitResetUtc(resp, now);
            if (reset != null && host.Length > 0)
            {
                _throttledUntil[host] = reset.Value;
                Logger.NoteDiagnosis($"{host} 接口限流（{(int)code}），{reset.Value.ToLocalTime():HH:mm:ss} 前不再请求该站点");
            }
        }
        throw new HttpRequestException(msg, null, code);
    }

    /// <summary>从限流响应头推出重置时间；不是限流（普通 403）返回 null。</summary>
    private static DateTime? RateLimitResetUtc(HttpResponseMessage resp, DateTime now)
    {
        try
        {
            if (resp.Headers.RetryAfter is { } ra)
            {
                if (ra.Delta is TimeSpan d) return now + d;
                if (ra.Date is DateTimeOffset dt) return dt.UtcDateTime;
            }
            if (resp.Headers.TryGetValues("X-RateLimit-Remaining", out var rem) && rem.FirstOrDefault() == "0")
            {
                if (resp.Headers.TryGetValues("X-RateLimit-Reset", out var rs)
                    && long.TryParse(rs.FirstOrDefault(), out long epoch))
                    return DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime;
                return now + TimeSpan.FromMinutes(15);
            }
        }
        catch { }
        return null;
    }

    private static void Put(string url, Entry e)
    {
        if (_cache.Count >= MaxEntries)
        {
            var now = DateTime.UtcNow;
            foreach (var kv in _cache) if (kv.Value.ExpiresUtc <= now) _cache.TryRemove(kv.Key, out _);
            if (_cache.Count >= MaxEntries) _cache.Clear();
        }
        _cache[url] = e;
    }

    /// <summary>「刷新」按钮之类明确要求重新查询时清空（成功与失败一起清；限流状态保留，避免立刻再撞限）。</summary>
    internal static void Clear() => _cache.Clear();
}
