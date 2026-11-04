using System;
using System.Collections.Generic;
using System.Linq;

namespace DSHGuard;

/// <summary>
/// 2.3.2：GitApiCache 直连负缓存的纯函数断言（<see cref="GitApiCache.RoutesSkippingDeadDirect"/>）。
/// 全部离线：只断"负缓存期内直连档怎么被拿掉、什么必须保留"，不联网。
/// </summary>
public static partial class SelfTest
{
    internal static void RunGitApiCacheChecks(Action<string, bool, string> check, Action<string, string> skip)
    {
        const string apiUrl = "https://api.github.com/repos/gluztm/DSHGuard/releases/latest";
        const string rawUrl = "https://raw.githubusercontent.com/gluztm/DSHGuard/main/README.md";

        var now = DateTime.UtcNow;

        // ── ① TTL 与查询口径 ──
        check("GitApiCache 直连负缓存 · 时长 10 分钟（短到恢复后很快回来，长到省掉一整轮死连接）",
            GitApiCache.DirectFailTtl == TimeSpan.FromMinutes(10),
            $"{GitApiCache.DirectFailTtl.TotalMinutes:0} 分钟");
        check("GitApiCache 直连负缓存 · 从没失败过的主机 / 解析不出的主机都不在负缓存期",
            GitApiCache.DirectDead("api.github.com", now) == false && GitApiCache.DirectDead("", now) == false,
            "查一个不可能失败过的键 + 空主机，都 false");

        // ── ② Auto 档：负缓存期内直连档被拿掉，镜像还在 ──
        var autoApi = GitHubRoute.Routes(apiUrl, GitHubRoute.Mode.Auto);
        var autoApiSkip = GitApiCache.RoutesSkippingDeadDirect(autoApi, apiUrl, true);
        check("GitApiCache 直连负缓存 · Auto 档 api：直连档拿掉后只剩镜像（不再先付一次注定失败的连接）",
            autoApi.Count == 2 && autoApi[0] == apiUrl && autoApiSkip.Count == 1 &&
            GitHubRoute.IsMirrored(autoApiSkip[0]),
            $"原 {autoApi.Count} 条 → 剩 {autoApiSkip.Count} 条，首条是镜像");

        var autoRaw = GitHubRoute.Routes(rawUrl, GitHubRoute.Mode.Auto);
        var autoRawSkip = GitApiCache.RoutesSkippingDeadDirect(autoRaw, rawUrl, true);
        check("GitApiCache 直连负缓存 · Auto 档 raw（三前缀）：只拿掉直连那一条，三个镜像顺序原样",
            autoRaw.Count == 4 && autoRawSkip.Count == 3 &&
            autoRawSkip.SequenceEqual(autoRaw.Skip(1)),
            $"原 {autoRaw.Count} 条 → 剩 {autoRawSkip.Count} 条");

        // ── ③ Mirror 档：表尾的直连兜底必须保留（兜底的本意就是明知可能失败也试最后一次）──
        var mirrorApi = GitHubRoute.Routes(apiUrl, GitHubRoute.Mode.Mirror);
        var mirrorApiSkip = GitApiCache.RoutesSkippingDeadDirect(mirrorApi, apiUrl, true);
        check("GitApiCache 直连负缓存 · Mirror 档：负缓存只省「每次必付」的直连，表尾直连兜底一个不动",
            mirrorApi.Count == 2 && mirrorApiSkip.Count == 2 && mirrorApiSkip.SequenceEqual(mirrorApi) &&
            mirrorApiSkip[^1] == apiUrl,
            $"原 {mirrorApi.Count} 条 → 仍 {mirrorApiSkip.Count} 条，末条直连还在");

        // ── ④ 未失效 / 无直连可拿：序列原样 ──
        check("GitApiCache 直连负缓存 · 未失效（false）时原序列原样返回（不改不排）",
            GitApiCache.RoutesSkippingDeadDirect(autoApi, apiUrl, false).SequenceEqual(autoApi),
            "directDead=false ⇒ 逐条相等");
        check("GitApiCache 直连负缓存 · Off 档只有直连一条 ⇒ 不动（没有镜像可换，拿掉等于自断）",
            GitHubRoute.Routes(apiUrl, GitHubRoute.Mode.Off) is var offApi &&
            offApi.Count == 1 &&
            GitApiCache.RoutesSkippingDeadDirect(offApi, apiUrl, true).Count == 1,
            "1 条进 1 条出");
        check("GitApiCache 直连负缓存 · 空表进空表出（不抛不补）",
            GitApiCache.RoutesSkippingDeadDirect(new List<string>(), apiUrl, true).Count == 0,
            "0 条");
    }
}
