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

        // 2.3.1：按 GitHub 访问策略排线路（整张表只有 GitHubRoute 一处，见那边的实测）。
        //   ⚠ 缓存键仍是**原始地址** url（不是实际请求的那条线路）：换源是"同一份数据的另一条路"，
        //     缓存必须按"要什么"记，否则换条线路就会命中另一份内容、或把同一份数据记成两条。
        //   ⚠ gitee / gitlab / bitbucket 的地址 GitHubRoute 只回一条（见 Routes 里的非 GitHub 分支），
        //     即那三家的行为一个字节都没变。
        var routes = GitHubRoute.Routes(url);
        Exception? lastError = null;

        for (int i = 0; i < routes.Count; i++)
        {
            string routeUrl = routes[i];
            bool viaMirror = GitHubRoute.IsMirrored(routeUrl);
            if (i > 0)
                Logger.NoteDiagnosis($"托管站线路：前一条未走通，改用第 {i + 1}/{routes.Count} 条"
                                   + $"（{RouteLabelFor(routeUrl, viaMirror)}）取同一份数据");

            HttpResponseMessage resp;
            try
            {
                resp = await http.GetAsync(routeUrl);
            }
            catch (Exception ex) when (i + 1 < routes.Count)
            {
                // 传输类失败（连不上 / 断流 / 超时）：还有下一条就换源，没有就把原因原样抛出。
                lastError = ex;
                Logger.NoteDiagnosis($"托管站线路「{RouteLabelFor(routeUrl, viaMirror)}」未走通"
                                   + $"（{ex.GetType().Name}: {ex.Message}）");
                continue;
            }

            using (resp)
            {
                if (resp.IsSuccessStatusCode)
                {
                    string body = await resp.Content.ReadAsStringAsync();
                    Put(url, new Entry(now + OkTtl, body, null, ""));
                    return body;
                }

                var code = resp.StatusCode;
                string msg = $"Response status code does not indicate success: {(int)code} ({resp.ReasonPhrase}).";

                // ⚠ 404 是**远端给的答案**（仓库没了 / 没有这个 ref），不是"这条路走不通"：
                //   换一条线路问同一件不存在的东西，答案仍是 404 ⇒ 当场按 404 缓存并抛出，
                //   调用方既有的 Missing / 发行版兜底分支一个字都不用改。
                if (code == HttpStatusCode.NotFound)
                {
                    Put(url, new Entry(now + NotFoundTtl, null, code, msg));
                    throw new HttpRequestException(msg, null, code);
                }

                // ⚠ 403/429 要分两种，**不能一律当限流**：镜像对 api 的 403 是"这条路不服务 api"
                //   （实测 ghfast.top / ghproxy.net 就是如此，响应里没有任何 RateLimit 头），
                //   把它记成限流会让整站的查询被自己封停到重置时间——那是把一次换源失败
                //   升级成一座几十分钟的墙。判据用既有的 RateLimitResetUtc（它只认真正的限流头）。
                DateTime? reset = (code == HttpStatusCode.Forbidden || (int)code == 429)
                    ? RateLimitResetUtc(resp, now) : null;
                if (reset != null && host.Length > 0)
                {
                    _throttledUntil[host] = reset.Value;
                    Logger.NoteDiagnosis($"{host} 接口限流（{(int)code}），{reset.Value.ToLocalTime():HH:mm:ss} 前不再请求该站点");
                    throw new HttpRequestException(msg, null, code);
                }

                // 不是限流的 403 / 5xx / 其它非 2xx：若是镜像这条路，换下一条再试（真实原因留在日志里）。
                if (viaMirror && i + 1 < routes.Count)
                {
                    Logger.NoteDiagnosis($"托管站线路「{RouteLabelFor(routeUrl, true)}」返回 HTTP {(int)code}"
                                       + "（不是限流）⇒ 换下一条");
                    lastError = new HttpRequestException(msg, null, code);
                    continue;
                }
                throw new HttpRequestException(msg, null, code);
            }
        }

        // 全部线路都没成：优先把最后一条的真实原因抛出（否则 404 会被下面的"没有线路"顶掉）。
        if (lastError != null) throw lastError;
        throw new HttpRequestException("没有可用的访问线路");
    }

    /// <summary>
    /// 线路的日志标签：镜像记域名、直连记「直连」。解析不出域名时记「镜像线路」——
    /// **绝不把整条地址写进日志**（与 PluginSource.RouteLabel 同一口径：地址不进日志，只进请求）。
    /// </summary>
    private static string RouteLabelFor(string routeUrl, bool viaMirror)
    {
        string r = (routeUrl ?? "").Trim();
        if (!viaMirror) return "直连";
        try { return new Uri(r).Host; }
        catch { return "镜像线路"; }
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
