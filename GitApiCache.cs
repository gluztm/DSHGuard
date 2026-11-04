using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
/// <item>直连**连接失败**（连不上 / DNS / 断流 / 整体超时）按**主机**记 10 分钟负缓存（2026-11-01）：
///       缓存键是原始 URL，同主机的另一条 URL 本来还会重付一次死连接（被墙时 api 查询每条都先卡
///       一次 12～22 秒就是它）；按主机记才能让其余 URL 直接从镜像开始。只记直连不记镜像
///       （镜像抖一次就封 10 分钟等于把临时故障放大成墙），到期自动重试直连；「刷新」按钮的
///       <see cref="Clear"/> 不清它（与限流状态同一待遇：刷新解决的是数据过期，不是网络通断）。</item>
/// </list>
/// 缓存的失败会重新抛出**同状态码**的 <see cref="HttpRequestException"/>，调用方既有的 404 分支与
/// <c>ClassifyRefProbeFailure</c> 分诊一个字都不用改。只缓存在内存里，重启即清空。
/// </summary>
internal static class GitApiCache
{
    internal static readonly TimeSpan OkTtl = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan NotFoundTtl = TimeSpan.FromMinutes(30);
    /// <summary>直连连接失败的负缓存时长：短到网络恢复后很快自己回来，长到省掉一整轮 12～22 秒的死连接。</summary>
    internal static readonly TimeSpan DirectFailTtl = TimeSpan.FromMinutes(10);
    private const int MaxEntries = 2000;

    private sealed record Entry(DateTime ExpiresUtc, string? Body, HttpStatusCode? FailStatus, string FailMessage);

    private static readonly ConcurrentDictionary<string, Entry> _cache = new(StringComparer.Ordinal);

    /// <summary>某个主机被限流到什么时候（UTC）；期间该主机的请求直接按 403 失败。</summary>
    private static readonly ConcurrentDictionary<string, DateTime> _throttledUntil = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 直连**连接失败**的负缓存，按主机记（UTC 到期时刻）。
    /// ⚠ 只记直连、不记镜像：镜像抖一次就封 10 分钟等于把临时故障放大成墙。
    /// </summary>
    private static readonly ConcurrentDictionary<string, DateTime> _directDeadUntil = new(StringComparer.OrdinalIgnoreCase);

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
        // 2.3.2：直连刚连接失败过（同主机负缓存内）⇒ 序列里的直连档拿掉，直接从镜像开始取，
        //   免得每个查询都先付一次注定失败的连接（被墙时 12～22 秒/次）。
        int routeCountBefore = routes.Count;
        routes = RoutesSkippingDeadDirect(routes, url, DirectDead(host, now));
        if (routes.Count < routeCountBefore)
            Logger.NoteDiagnosis($"托管站直连 {host} 刚连接失败（{DirectFailTtl.TotalMinutes:0} 分钟负缓存内），"
                               + "本次直接从镜像取同一份数据");
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
                // 2.3.2：直连档的连接失败按**主机**记负缓存——同主机的其余 URL（api 另一条查询、
                //   翻页的下一页、别的仓库）直接从镜像开始，不再逐条重付一次死连接。镜像不记。
                lastError = ex;
                if (!viaMirror && host.Length > 0)
                    _directDeadUntil[host] = now + DirectFailTtl;
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

    /// <summary>该主机当前是否处于直连负缓存期内（UTC 比较；主机解析不出一律 false）。</summary>
    internal static bool DirectDead(string host, DateTime now)
        => host.Length > 0 && _directDeadUntil.TryGetValue(host, out var until) && until > now;

    /// <summary>
    /// 直连负缓存生效期间，把线路序列里的**直连档**拿掉（纯函数，自检可逐格断言）。
    /// <para>
    /// ⚠ 只处理「直连排第一」的序列（Auto 档的实际形态）：从第二条起原样返回。
    /// Mirror 档的直连在表尾兜底，**保留**——兜底的本意就是"明知可能失败也试最后一次"；
    /// 而且负缓存只在 Auto 档记账（Mirror 档直连是最后一档，失败时没有"下一条"，本就记不上）。
    /// 未失效 / 直连不排第一 / 序列不足两条 ⇒ 原样返回。
    /// </para>
    /// </summary>
    internal static List<string> RoutesSkippingDeadDirect(List<string> routes, string url, bool directDead)
    {
        if (!directDead || routes.Count < 2) return routes;
        string direct = (url ?? "").Trim();
        return routes[0] == direct ? routes.Skip(1).ToList() : routes;
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

    /// <summary>「刷新」按钮之类明确要求重新查询时清空（成功与失败一起清；直连负缓存与限流状态保留——
    ///   刷新解决的是数据过期，不是网络通断，与既有 <c>_throttledUntil</c> 同一待遇，避免立刻再撞限/再付一次死连接）。</summary>
    internal static void Clear() => _cache.Clear();
}
