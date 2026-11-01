using System;
using System.Collections.Generic;

namespace DSHGuard;

/// <summary>
/// GitHub 访问线路（用户要求：「为本软件提供直连 github 的能力，免得有些人没办法更新或者上 github」）。
///
/// <para>
/// ⚠ 本类**不实现任何网络协议**，只回答两个纯问题：
///   ① 一个 GitHub 地址按当前策略该**依次尝试哪几个地址**（<see cref="Routes"/>）；
///   ② 给子进程（git / pnpm / npx）该**注入哪些环境变量**才能让 git 的 github 地址改走镜像
///      （<see cref="GitInsteadOfEnv"/>）。
/// 取数与重试仍在各自原本的调用点（<c>PluginSource</c> / <c>GitApiCache</c> / <c>PluginMarket</c>），
/// 本类一个字都不碰它们的判据 —— 只把"从哪条路取"这件事集中到一处，免得四个调用点各写一份会漂移的表。
/// </para>
///
/// <para>
/// ══ 实测依据（2026-10-29，本机 Windows + 家宽、未开代理；每条只读头部/前 80 KB）══
/// <list type="bullet">
/// <item>直连 github.com（仓库页 / releases 资产 / git smart-HTTP）：**全部 ECONNRESET**，
///       git 侧原文 Failed to connect to github.com:443 after 21598 ms；</item>
/// <item>直连 api.github.com：200 / 0.6 s；直连 raw.githubusercontent.com：200 / 1.2 s；</item>
/// <item>gh-proxy.com：api 200 / raw 200 / 资产 206 / 页面 200 / git info-refs 200 —— **唯一五项全绿**；</item>
/// <item>ghfast.top：raw 200、资产 206，但 **api 403、页面 403**；</item>
/// <item>ghproxy.net：raw 200（10.5 s）、资产 206（9.3 s），**api 403、页面 403**，明显偏慢；</item>
/// <item>另外 11 个常见前缀（gitmirror / llkk / ghp.ci / ghproxy.cc / gh.zone.id / gitproxy.click …）
///       实测**全灭**：ENOTFOUND、超时、EPROTO、证书过期、404、302 劫持。</item>
/// </list>
/// 结论（这三条是本类每一处判据的由来，改表前先重跑一遍上表）：
///   · **能走 api 的只有 gh-proxy.com** —— 另外两个对 api.github.com 一律 403，
///     所以"所有前缀都试一遍"是**错的**：对着 403 挨个试只会让每个查询多花两轮白等；
///   · **能力要分道**：JSON/API 一路、raw 文本一路、资产下载一路、网页一路，四路各有各的前缀集；
///   · **没有任何一条线路是全能的**，所以策略必须是"能直连就直连、不通才换源"，
///     而不是"一律绕镜像"—— 对能直连的用户，绕路只会更慢。
/// </para>
/// </summary>
internal static class GitHubRoute
{
    /// <summary>用户可选的策略（设置页那一行三选一）。</summary>
    internal enum Mode
    {
        /// <summary>默认：先直连，不通才换镜像（能直连的用户一点都不会变慢）。</summary>
        Auto = 0,
        /// <summary>一律先走镜像（直连那一侧被墙得很彻底、或者共用网络里直连必炸时用）。</summary>
        Mirror = 1,
        /// <summary>只用直连，与 2.3.0 及以前完全一致（关掉本功能）。</summary>
        Off = 2,
    }

    /// <summary>四路能力。分道的理由见类注释：ghfast.top / ghproxy.net 对 api 一律 403。</summary>
    internal enum Cap
    {
        /// <summary>JSON 接口（api.github.com）：只有一个前缀能用。</summary>
        Api,
        /// <summary>文本与图片（raw.githubusercontent.com）：三个前缀都能用。</summary>
        Raw,
        /// <summary>发行版资产下载（github.com/…/releases/download/…）：三个前缀都能用。</summary>
        Asset,
        /// <summary>网页（github.com 页面）：只 gh-proxy.com 能用（另两个 403）。</summary>
        Page,
    }

    /// <summary>唯一前缀常量。**只有这三个经过实测**，别凭"听说过"往里加（见类注释末段）。</summary>
    internal const string PrefixGhProxy = "https://gh-proxy.com";
    internal const string PrefixGhFast = "https://ghfast.top";
    internal const string PrefixGhProxyNet = "https://ghproxy.net";

    /// <summary>
    /// 某一路上可用的前缀，按实测可用性与速度排序（纯函数，自检可逐格断言）。
    /// <para>
    /// ⚠ 顺序即"先试谁"：api 只有一条；raw/asset 按 gh-proxy（1.2 s）→ ghfast（3.1 s）→ ghproxy.net（10.5 s）。
    /// 把慢的排前面等于让每个用户先等十秒。
    /// </para>
    /// </summary>
    internal static string[] PrefixesFor(Cap cap) => cap switch
    {
        Cap.Api => new[] { PrefixGhProxy },
        Cap.Raw => new[] { PrefixGhProxy, PrefixGhFast, PrefixGhProxyNet },
        Cap.Asset => new[] { PrefixGhProxy, PrefixGhFast, PrefixGhProxyNet },
        Cap.Page => new[] { PrefixGhProxy },
        _ => Array.Empty<string>(),
    };

    /// <summary>全部前缀（并集，按首次出现顺序）。自检与既有 GuardSetupMirrorPrefixes 用它。</summary>
    internal static string[] AllPrefixes()
    {
        var seen = new List<string>();
        foreach (var cap in new[] { Cap.Api, Cap.Raw, Cap.Asset, Cap.Page })
            foreach (string p in PrefixesFor(cap))
                if (!seen.Contains(p)) seen.Add(p);
        return seen.ToArray();
    }

    /// <summary>
    /// 当前生效的策略（进程内唯一出处，由设置驱动）。
    /// <para>
    /// 为什么给一个静态值而不是每次把设置传进去：本类被 <c>PluginSource</c> / <c>GitApiCache</c> /
    /// <c>PluginMarket</c> / <c>ProcessManager</c> 四处调用，它们都拿不到 <c>SettingsManager</c> 实例；
    /// 逐个改签名会把"只加一条线路"变成"动十几处调用点"。这里照 <c>Registries</c>（npm 下载来源）
    /// 的既有做法：启动时配一次、设置变更时再配一次，其余各处直接读。
    /// </para>
    /// </summary>
    public static Mode Current { get; private set; } = Mode.Auto;

    /// <summary>按设置里的值切换策略（空值 / 认不出的值 = <see cref="Mode.Auto"/>）。</summary>
    public static void Configure(string? setting) => Current = FromSetting(setting);

    /// <summary>按策略值切换（设置页点一下就调它）。</summary>
    public static void Configure(Mode mode) => Current = mode;

    /// <summary>设置值 → 策略（认不出的一律按 Auto：默认值必须倒向"能用"那一侧）。</summary>
    internal static Mode FromSetting(string? raw) => (raw ?? "").Trim().ToLowerInvariant() switch
    {
        "off" => Mode.Off,
        "mirror" => Mode.Mirror,
        _ => Mode.Auto,
    };

    /// <summary>策略 → 设置值（与 FromSetting 严格互逆，自检可断言往返）。</summary>
    internal static string ToSetting(Mode mode) => mode switch
    {
        Mode.Off => "off",
        Mode.Mirror => "mirror",
        _ => "auto",
    };

    /// <summary>策略 → 界面短名（界面上不出现网址与英文标识）。</summary>
    internal static string Label(Mode mode) => mode switch
    {
        Mode.Off => "直连优先",
        Mode.Mirror => "优先镜像",
        _ => "自动",
    };

    /// <summary>点一下切到下一档（三态循环）。与 Registries.Toggle 同一套交互口径。</summary>
    internal static Mode Next(Mode mode) => mode switch
    {
        Mode.Auto => Mode.Mirror,
        Mode.Mirror => Mode.Off,
        _ => Mode.Auto,
    };

    /// <summary>
    /// 这个地址是不是 GitHub 的（决定它该不该走本类的换源；gitee / gitlab / bitbucket 一律 false）。
    /// <para>
    /// ⚠ 只认**明确列出的主机**，不做后缀包含：evil-github.com 这类后缀匹配会把自己搭进去。
    /// 与 PluginMarket.IsAllowedLinkUrl 同一闭合范式（host 精确相等）。
    /// </para>
    /// </summary>
    internal static bool IsGitHubUrl(string? url)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            var host = new Uri(url!.Trim()).Host.ToLowerInvariant();
            return host switch
            {
                "github.com" => true,
                "api.github.com" => true,
                "raw.githubusercontent.com" => true,
                "gist.githubusercontent.com" => true,
                "codeload.github.com" => true,
                "objects.githubusercontent.com" => true,
                "avatars.githubusercontent.com" => true,
                "github.githubassets.com" => true,
                "desktop.githubusercontent.com" => true,
                _ => host.EndsWith(".github.com", StringComparison.Ordinal) && host.Length > "github.com".Length + 1,
            };
        }
        catch { return false; }
    }

    /// <summary>这个地址属于哪一路（决定用哪份前缀表）。认不出按 Raw（最宽松的一路）。</summary>
    internal static Cap CapFor(string? url)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url)) return Cap.Raw;
            var host = new Uri(url!.Trim()).Host.ToLowerInvariant();
            if (host == "api.github.com") return Cap.Api;
            if (host == "raw.githubusercontent.com") return Cap.Raw;
            if (host == "codeload.github.com" || url!.Contains("/releases/download/", StringComparison.OrdinalIgnoreCase))
                return Cap.Asset;
            if (host == "github.com") return Cap.Page;
        }
        catch { }
        return Cap.Raw;
    }

    /// <summary>
    /// 一个地址的**候选线路序列**（纯函数，自检可逐格断言）。入参不是 GitHub 地址 ⇒ 只回它自己一条。
    ///
    /// <para>
    /// Auto：原地址 + 各前缀 + 原地址 —— 直连排第一，
    /// "能直连的用户一点都不变慢"就靠这一条；直连失败才轮到镜像（本机实测直连必失败，所以这一档
    /// 对用户的实际效果就是"每次多花一次失败的连接"——但对**能直连的人**是零代价，值得）。
    /// </para>
    /// <para>
    /// Mirror：前缀 + 原地址排前面，直连留作最后一档兜底
    /// （镜像全挂时还能试一次直连，不至于连试都不试就报失败）。
    /// </para>
    /// <para>
    /// Off：只有原地址 —— 与 2.3.0 逐字一致，本功能等于不存在。
    /// </para>
    /// <para>
    /// ⚠ 生成镜像地址用的是「**写死的前缀常量 + 原地址**」，前缀**绝不来自远端报文**：
    /// 外部输入只被读、被核对，从不参与构造地址（投毒入口）。这与 GuardReleaseAsset 那条纪律同源。
    /// </para>
    /// </summary>
    internal static List<string> Routes(string? url, Mode mode)
        => Routes(url, mode, CapFor(url));

    /// <summary>按**当前生效策略**给一个地址排线路（各取数点直接用这一条，不必自己读设置）。</summary>
    internal static List<string> Routes(string? url) => Routes(url, Current, CapFor(url));

    /// <summary>同上，但能力由调用方指定（发行版资产那条路自己知道它是资产，不必再靠 URL 猜）。</summary>
    internal static List<string> Routes(string? url, Mode mode, Cap cap)
    {
        var list = new List<string>();
        string direct = (url ?? "").Trim();
        if (direct.Length == 0) return list;

        // 不是 GitHub 地址（gitee / npm / 本地文件）：本类无权给它加前缀，原样一条。
        if (!IsGitHubUrl(direct)) { list.Add(direct); return list; }

        if (mode != Mode.Mirror) { list.Add(direct); if (mode == Mode.Off) return list; }

        var mirrored = new List<string>();
        foreach (string prefix in PrefixesFor(cap))
        {
            string p = (prefix ?? "").Trim().TrimEnd('/');
            if (p.Length == 0) continue;
            mirrored.Add(p + "/" + direct);
        }

        if (mode == Mode.Mirror)
        {
            list.AddRange(mirrored);
            list.Add(direct);          // 直连留作最后一档兜底（镜像全挂也还能试一次）
        }
        else
        {
            list.AddRange(mirrored);   // Auto：直连已在前面，这里追加镜像
        }
        return list;
    }

    /// <summary>
    /// 某条线路是不是镜像（决定"经镜像取回的字节"要不要额外校验）。
    /// ⚠ 判据不能写成 routes[0] 之外即镜像：Mirror 档下 routes[0] 本身就是镜像；
    /// 所以这里逐个比对前缀常量，而不是看它在序列里的位置。
    /// </summary>
    internal static bool IsMirrored(string? url)
    {
        string r = (url ?? "").Trim();
        if (r.Length == 0) return false;
        foreach (string prefix in AllPrefixes())
            if (r.StartsWith(prefix.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // ══════════════ 子进程：让 git 的 github 地址改走镜像 ══════════════
    //
    // 为什么走**环境变量**而不是 git config：
    //   · 改全局 config 会留在用户机器上（程序卸载了它还在，且会悄悄影响用户自己的 git），
    //     本项目一贯纪律是"只对本次子进程生效、不写盘"（同 SupplyChainRelaxEnv）；
    //   · git 支持 GIT_CONFIG_COUNT / GIT_CONFIG_KEY_n / GIT_CONFIG_VALUE_n 这套按进程注入配置的入口，
    //     本机实测（2026-10-29）：
    //       GIT_CONFIG_COUNT=1、
    //       GIT_CONFIG_KEY_0=url.https://gh-proxy.com/https://github.com.insteadOf、
    //       GIT_CONFIG_VALUE_0=https://github.com
    //     ⇒ git clone --depth 1 https://github.com/octocat/Hello-World.git **成功**；
    //       同一命令不带注入时原文是 Failed to connect to github.com:443 after 21598 ms。
    //   · 直接给 git clone <前缀/repo> 也是通的（实测同样成功），但那只覆盖"我们自己拼地址"的那条路；
    //       插件安装走的是 pnpm → git，地址由 pnpm 自己拼（来自 package.json 的 git+https://…），
    //       **我们改不到它**，只有 insteadOf 能改写。

    /// <summary>
    /// 给这次子进程注入「github.com 改走镜像」的 git 环境变量（纯函数，自检可逐格断言）。
    ///
    /// <para>
    /// ⚠ 必须**叠加**在用户已有的 GIT_CONFIG_COUNT 之上而不是覆盖：
    /// 覆盖会静默吞掉用户自己注入的 git 配置（那是"改动别人的环境"，不是"加一条线路"）。
    /// 因此先读现有计数，再往后追加本类这一条。
    /// </para>
    /// <para>
    /// ⚠ 只改写 https://github.com 这一个前缀：git@github.com:（SSH）**不改** ——
    /// insteadOf 对 SSH 写法无效（git 的 URL 改写按字符串前缀匹配，SSH 形态不带 https://），
    /// 与其写一条看似生效、实际永远匹配不上的规则，不如让它原样失败并如实报出来。
    /// </para>
    /// </summary>
    internal static List<KeyValuePair<string, string>> GitInsteadOfEnv(string? prefix, int existingCount)
    {
        var list = new List<KeyValuePair<string, string>>();
        string p = (prefix ?? PrefixGhProxy).Trim().TrimEnd('/');
        if (p.Length == 0) return list;

        int start = Math.Max(0, existingCount);
        string count = (start + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

        list.Add(new KeyValuePair<string, string>("GIT_CONFIG_COUNT", count));
        list.Add(new KeyValuePair<string, string>("GIT_CONFIG_KEY_" + start,
            "url." + p + "/https://github.com.insteadOf"));
        list.Add(new KeyValuePair<string, string>("GIT_CONFIG_VALUE_" + start,
            "https://github.com"));
        return list;
    }

    /// <summary>读环境里现有的 GIT_CONFIG_COUNT（读不到按 0）。供 GitInsteadOfEnv 的调用点算起点。</summary>
    internal static int ExistingGitConfigCount()
    {
        try
        {
            string? raw = Environment.GetEnvironmentVariable("GIT_CONFIG_COUNT");
            return raw != null && int.TryParse(raw, out int n) && n > 0 ? n : 0;
        }
        catch { return 0; }
    }

    /// <summary>把 GitInsteadOfEnv 压成一行「键=值」，供日志留痕（与 SupplyChainRelaxEnvLog 同口径）。</summary>
    internal static string GitEnvLog(string? prefix, int existingCount)
        => string.Join("、", GitInsteadOfEnv(prefix, existingCount).ConvertAll(kv => kv.Key + "=" + kv.Value));

    // ══════════════ 失败分诊：这条失败是不是「github 连不上」 ══════════════

    /// <summary>
    /// 命令输出里是不是**明确的"到 github 走不通"**（纯函数，自检可逐格断言）。
    ///
    /// <para>
    /// 只有它为真时才注入 <see cref="GitInsteadOfEnv"/> 并重试一次 —— 与
    /// ShouldRetryWithRelaxedPolicy（pnpm 供应链策略）**完全同构**：
    /// 先按默认策略跑，只在现场特征出现时对**这一条命令**换一组参数重试，
    /// 绝不无条件把所有人的 github 地址都绕一遍（那会让能直连的用户凭空多一跳）。
    /// </para>
    /// <para>
    /// ⚠ 判据必须同时含「**github 主机名**」与「**连接类失败词**」，两个条件缺一不可：
    /// 只认 fatal: 会把"包不存在""权限不足"也当成网络问题，重试一轮纯属白等；
    /// 只认 ECONNRESET 又会在 gitee / gitlab 失败时误触发（那些站本类根本不换源）。
    /// </para>
    /// <para>
    /// 实测原文取样（本机 2026-10-29，作为判据的来处，可据此收紧）：
    ///   · git 直连：fatal: unable to access 'https://github.com/…/': Failed to connect to github.com:443 after 21598 ms: Could not connect to server
    ///   · 断流：error: RPC failed; curl 56 OpenSSL SSL_read: Connection reset by peer、
    ///     fatal: the remote end hung up unexpectedly、fatal: early EOF
    ///   · DNS：Could not resolve host: github.com
    /// </para>
    /// </summary>
    internal static bool LooksLikeGitHubUnreachable(string? output, string? context = null)
    {
        string s = output ?? "";
        if (s.Length == 0) return false;

        string[] connectFailures =
        {
            "Failed to connect to github.com",
            "Could not connect to server",
            "Could not resolve host",
            "Connection reset by peer",
            "Connection timed out",
            "Operation timed out",
            "timed out after",
            "SSL_ERROR_SYSCALL",
            "gnutls_handshake() failed",
            "OpenSSL SSL_read",
            "HTTP/2 stream",
            "Recv failure",
            "Empty reply from server",
            "RPC failed",
            "early EOF",
            "the remote end hung up unexpectedly",
        };
        bool anyConnectFailure = false;
        foreach (string needle in connectFailures)
            if (s.Contains(needle, StringComparison.OrdinalIgnoreCase)) { anyConnectFailure = true; break; }
        if (!anyConnectFailure) return false;

        // 「这条失败是不是在说 github」有两个来源，任一成立即可（见下面的分档）：
        //   ① 输出里直接写了 github.com —— git 的经典报错形如
        //      fatal: unable to access 'https://github.com/o/r/': Failed to connect to github.com:443 …
        //   ② 输出里没写主机名，但**这次执行的命令**本身点名了 github
        //      （pnpm 的 git 源依赖写成 github:owner/repo 或 git+https://github.com/…）。
        bool mentionsGitHub = s.Contains("github.com", StringComparison.OrdinalIgnoreCase)
            || (context ?? "").Contains("github", StringComparison.OrdinalIgnoreCase);
        if (mentionsGitHub) return true;

        // ③ 兜底：**git 传输协议特有**的失败词。这一类报错压根不带主机名 —— 实测原文就三行：
        //      error: RPC failed; curl 56 OpenSSL SSL_read: Connection reset by peer
        //      fatal: the remote end hung up unexpectedly
        //      fatal: early EOF
        //   它们只有在真有 git 取数时才出现（npm/pnpm registry 请求不会产出 RPC failed / early EOF），
        //   而 pnpm 装 / 更新 git 源插件正是走这条路（地址在 package.json 里，命令行上看不到）。
        //   ⚠ 误判代价是对一条本来也会失败的命令多跑一轮**且只改写 github 地址**：
        //     若那条依赖其实是 gitee / gitlab，改写规则根本不匹配，重试结果与首轮一致，
        //     不会把"能过的"变成"过不去"。所以这里宁可宽一格，也不让最典型的断流场景漏网。
        string[] gitTransportOnly =
        {
            "RPC failed",
            "early EOF",
            "the remote end hung up unexpectedly",
            "Empty reply from server",
        };
        foreach (string needle in gitTransportOnly)
            if (s.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }
}
