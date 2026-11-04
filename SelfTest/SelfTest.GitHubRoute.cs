using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DSHGuard;

/// <summary>
/// 2.3.1：GitHub 访问线路（<see cref="GitHubRoute"/>）的纯函数断言。
/// 全部离线：只断"表怎么排、地址怎么拼、失败怎么认"，不联网 —— 联网那部分靠真机实测记录
/// （见 <see cref="GitHubRoute"/> 类注释里的实测表），自检里不重复跑（网络一抖整片就假红）。
/// </summary>
public static partial class SelfTest
{
    internal static void RunGitHubRouteChecks(Action<string, bool, string> check, Action<string, string> skip)
    {
        const string apiUrl = "https://api.github.com/repos/gluztm/DSHGuard/releases/latest";
        const string rawUrl = "https://raw.githubusercontent.com/gluztm/DSHGuard/main/README.md";
        const string assetUrl = "https://github.com/gluztm/DSHGuard/releases/download/2.3.0/DSHGuard-Setup-2.3.0.exe";
        const string pageUrl = "https://github.com/gluztm/DSHGuard";

        // ── ① 三档策略的线路顺序（这是本功能的行为契约：默认档不许把能直连的人变慢）──
        var autoApi = GitHubRoute.Routes(apiUrl, GitHubRoute.Mode.Auto);
        check("GitHub 访问线路 · 自动档：直连排第一，其后才是镜像（能直连的网络零代价；精确 2 条锁死注释契约）",
            autoApi.Count == 2 && autoApi[0] == apiUrl && autoApi.Skip(1).All(GitHubRoute.IsMirrored),
            $"{autoApi.Count} 条：[{string.Join(" / ", autoApi.Select(u => u.StartsWith("https://gh-") ? new Uri(u).Host : "直连"))}]");

        var mirrorApi = GitHubRoute.Routes(apiUrl, GitHubRoute.Mode.Mirror);
        check("GitHub 访问线路 · 镜像档：镜像排前面，直连留作最后一档兜底（全挂也还能试一次）",
            mirrorApi.Count == 2 && GitHubRoute.IsMirrored(mirrorApi[0]) && mirrorApi[^1] == apiUrl,
            $"首条={GitHubRoute.IsMirrored(mirrorApi[0])} · 末条直连={mirrorApi[^1] == apiUrl} · 共 {mirrorApi.Count} 条");

        var offApi = GitHubRoute.Routes(apiUrl, GitHubRoute.Mode.Off);
        check("GitHub 访问线路 · 关闭档：只有直连一条，与 2.3.0 逐字一致",
            offApi.Count == 1 && offApi[0] == apiUrl,
            $"{offApi.Count} 条");

        // ── ② 能力分道（实测 ghfast / ghproxy.net 对 api 一律 403，所以 api 这一路只有一个前缀）──
        check("GitHub 访问线路 · api 这一路只挂一个前缀（另外两个对 api.github.com 实测 403，多试一轮纯属白等）",
            GitHubRoute.PrefixesFor(GitHubRoute.Cap.Api).Length == 1 &&
            GitHubRoute.PrefixesFor(GitHubRoute.Cap.Api)[0] == GitHubRoute.PrefixGhProxy,
            $"{GitHubRoute.PrefixesFor(GitHubRoute.Cap.Api).Length} 条");
        check("GitHub 访问线路 · raw 与资产两路挂了三个前缀，且 gh-proxy 排第一（实测最快）",
            GitHubRoute.PrefixesFor(GitHubRoute.Cap.Raw).Length == 3 &&
            GitHubRoute.PrefixesFor(GitHubRoute.Cap.Raw)[0] == GitHubRoute.PrefixGhProxy &&
            GitHubRoute.PrefixesFor(GitHubRoute.Cap.Asset).Length == 3 &&
            GitHubRoute.PrefixesFor(GitHubRoute.Cap.Asset)[0] == GitHubRoute.PrefixGhProxy,
            $"raw={GitHubRoute.PrefixesFor(GitHubRoute.Cap.Raw).Length} 条 · 资产={GitHubRoute.PrefixesFor(GitHubRoute.Cap.Asset).Length} 条");
        check("GitHub 访问线路 · 网页这一路只挂 gh-proxy（另两个打开 github 页面实测 403）",
            GitHubRoute.PrefixesFor(GitHubRoute.Cap.Page).Length == 1,
            $"{GitHubRoute.PrefixesFor(GitHubRoute.Cap.Page).Length} 条");

        // ── ③ 地址分类（分错了就用到错的前缀表，等于把 403 当线路来试）──
        check("GitHub 访问线路 · 四种地址各有各的路：api / raw / 资产 / 网页",
            GitHubRoute.CapFor(apiUrl) == GitHubRoute.Cap.Api &&
            GitHubRoute.CapFor(rawUrl) == GitHubRoute.Cap.Raw &&
            GitHubRoute.CapFor(assetUrl) == GitHubRoute.Cap.Asset &&
            GitHubRoute.CapFor(pageUrl) == GitHubRoute.Cap.Page,
            $"{GitHubRoute.CapFor(apiUrl)} / {GitHubRoute.CapFor(rawUrl)} / {GitHubRoute.CapFor(assetUrl)} / {GitHubRoute.CapFor(pageUrl)}");

        check("GitHub 访问线路 · 非 GitHub 地址（gitee / npmmirror / 本地路径）一条前缀都不加",
            GitHubRoute.IsGitHubUrl("https://gitee.com/o/r") == false &&
            GitHubRoute.IsGitHubUrl("https://registry.npmmirror.com/x") == false &&
            GitHubRoute.IsGitHubUrl("file:///C:/x") == false &&
            GitHubRoute.Routes("https://gitee.com/o/r", GitHubRoute.Mode.Mirror).SequenceEqual(new[] { "https://gitee.com/o/r" }) &&
            GitHubRoute.Routes("https://registry.npmmirror.com/x", GitHubRoute.Mode.Mirror).Count == 1,
            "gitee / npmmirror 各自只有直连一条");

        check("GitHub 访问线路 · 后缀包含不算 GitHub（evil-github.com 不许被当成 github 加前缀）",
            GitHubRoute.IsGitHubUrl("https://evil-github.com/x") == false &&
            GitHubRoute.IsGitHubUrl("https://github.com.evil.com/x") == false &&
            GitHubRoute.IsGitHubUrl("https://github.com/x") == true,
            "evil 两个都判 false、真域名判 true");

        // ── ④ 拼接纪律（前缀写死、绝不来自报文；空值不产出半条线路）──
        check("GitHub 访问线路 · 镜像地址一律是「写死的前缀 + 原地址」，不重排、不改写原地址",
            mirrorApi.First(GitHubRoute.IsMirrored) == GitHubRoute.PrefixGhProxy + "/" + apiUrl,
            mirrorApi[0]);
        check("GitHub 访问线路 · 空地址编不出任何线路（不产出半条/仅镜像的怪表）",
            GitHubRoute.Routes("", GitHubRoute.Mode.Auto).Count == 0 &&
            GitHubRoute.Routes(null, GitHubRoute.Mode.Auto).Count == 0 &&
            GitHubRoute.Routes("   ", GitHubRoute.Mode.Mirror).Count == 0,
            "空串 / null / 空白各自 0 条");

        // ── ⑤ 策略往返与默认档 ──
        check("GitHub 访问线路 · 设置值三档往返一致，认不出的值落 auto（默认档倒向能用那一侧）",
            GitHubRoute.ToSetting(GitHubRoute.FromSetting("auto")) == "auto" &&
            GitHubRoute.ToSetting(GitHubRoute.FromSetting("mirror")) == "mirror" &&
            GitHubRoute.ToSetting(GitHubRoute.FromSetting("off")) == "off" &&
            GitHubRoute.FromSetting("") == GitHubRoute.Mode.Auto &&
            GitHubRoute.FromSetting("随便写的") == GitHubRoute.Mode.Auto &&
            GitHubRoute.FromSetting(null) == GitHubRoute.Mode.Auto,
            $"空={GitHubRoute.FromSetting("")} · 乱写={GitHubRoute.FromSetting("随便写的")}");
        check("GitHub 访问线路 · 点一下三态循环：自动 → 优先镜像 → 直连优先 → 自动",
            GitHubRoute.Next(GitHubRoute.Mode.Auto) == GitHubRoute.Mode.Mirror &&
            GitHubRoute.Next(GitHubRoute.Mode.Mirror) == GitHubRoute.Mode.Off &&
            GitHubRoute.Next(GitHubRoute.Mode.Off) == GitHubRoute.Mode.Auto,
            "三档循环成立");

        // ── ⑥ git 子进程环境变量（插件安装走 pnpm→git，只有 insteadOf 能改写它的地址）──
        var env0 = GitHubRoute.GitInsteadOfEnv(GitHubRoute.PrefixGhProxy, 0);
        check("GitHub 访问线路 · git 改写变量：新起一条时计数为 1，键是 url.<前缀>/https://github.com.insteadOf",
            env0.Count == 3 &&
            env0[0].Value == "1" &&
            env0[1].Key == "GIT_CONFIG_KEY_0" &&
            env0[1].Value == "url." + GitHubRoute.PrefixGhProxy + "/https://github.com.insteadOf" &&
            env0[2].Key == "GIT_CONFIG_VALUE_0" &&
            env0[2].Value == "https://github.com",
            $"{{{string.Join("；", env0.Select(kv => kv.Key + "=" + kv.Value))}}}");

        var env2 = GitHubRoute.GitInsteadOfEnv(GitHubRoute.PrefixGhProxy, 2);
        check("GitHub 访问线路 · git 改写变量必须叠加在用户已有的计数上（覆盖会静默吞掉用户自己的 git 配置）",
            env2[0].Value == "3" &&
            env2[1].Key == "GIT_CONFIG_KEY_2" &&
            env2[2].Key == "GIT_CONFIG_VALUE_2" &&
            !env2.Any(kv => kv.Key.EndsWith("_0", StringComparison.Ordinal) || kv.Key.EndsWith("_1", StringComparison.Ordinal)),
            $"existing=2 ⇒ {string.Join("；", env2.Select(kv => kv.Key + "=" + kv.Value))}");

        check("GitHub 访问线路 · 前缀为空时不产出一条匹配不上的规则",
            GitHubRoute.GitInsteadOfEnv("", 0).Count == 0 &&
            GitHubRoute.GitInsteadOfEnv("   ", 0).Count == 0,
            "空串 / 空白各自 0 条");

        // ── ⑦ 失败分诊：只有"明确到 github 走不通"才换源 ──
        string[] shouldRetry =
        {
            "fatal: unable to access 'https://github.com/o/r/': Failed to connect to github.com:443 after 21598 ms: Could not connect to server",
            "error: RPC failed; curl 56 OpenSSL SSL_read: Connection reset by peer",
            "fatal: the remote end hung up unexpectedly",
            "fatal: early EOF",
            "Could not resolve host: github.com",
        };
        check("GitHub 访问线路 · 现场原文五种都认得出来（这是真机直连失败的实录取样）",
            shouldRetry.All(x => GitHubRoute.LooksLikeGitHubUnreachable(x)),      // 单一参数的用法必须仍然可用
            $"{shouldRetry.Count(x => GitHubRoute.LooksLikeGitHubUnreachable(x))}/{shouldRetry.Length} 认出");

        string[] shouldNotRetry =
        {
            "ERR_PNPM_NO_MATCHING_VERSION: No matching version found for dsh-x@^1.2.0",
            "404 Not Found - GET https://registry.npmmirror.com/x",
            "fatal: repository 'https://github.com/o/r/' not found",
            "error: Your local changes would be overwritten by merge",
            "fatal: unable to access 'https://gitee.com/o/r/': Failed to connect to gitee.com:443",
        };
        check("GitHub 访问线路 · 主机名没写但命令点名 github 时也算（pnpm 装 git 源插件就是这条路）",
            GitHubRoute.LooksLikeGitHubUnreachable("error: RPC failed; curl 56", "plugin add dsh-x@github:o/r")
            && GitHubRoute.LooksLikeGitHubUnreachable("fatal: early EOF", "git clone https://github.com/o/r.git"),
            "两条带 github 上下文的 git 传输报错都判 true");

        // ⚠ 这一条断的是「兜底那一档的边界」，而不是「绝对不误判」—— 兜底档（RPC failed / early EOF /
        //   remote end hung up）**刻意**不看上下文：这三种词只有真去跑 git 传输才会出现，而 pnpm 装
        //   git 源插件时地址在 package.json 里、命令行上看不到。误判的代价只是对一条本来也会失败的命令
        //   多跑一轮（且只改写 github 地址，gitee 依赖根本匹配不上），而漏判的代价是最典型的断流场景
        //   一次都不换源 —— 两害相权取前者。所以这里断"纯 npm 包名 + 非 git 传输词"这条真边界。
        check("GitHub 访问线路 · 纯 npm 包名 + 非传输类报错不触发换源（换源改不了'包不存在'）",
            !GitHubRoute.LooksLikeGitHubUnreachable("ERR_PNPM_NO_MATCHING_VERSION: dsh-x@^1.2.0", "plugin add dsh-x@1.2.0")
            && !GitHubRoute.LooksLikeGitHubUnreachable("fatal: pathspec 'x' did not match any files", "git checkout x"),
            "包不存在与本地路径错各自 false");

                check("GitHub 访问线路 · 包不存在 / 权限 / 本地冲突 / 别的站点失败一律不触发换源（免得白跑一轮）",
            shouldNotRetry.All(x => !GitHubRoute.LooksLikeGitHubUnreachable(x)),
            $"{shouldNotRetry.Count(x => !GitHubRoute.LooksLikeGitHubUnreachable(x))}/{shouldNotRetry.Length} 正确放过");

        // ── ⑧ 哪些命令才值得重试（powershell / taskkill 失败时注入 git 变量既没用又搅浑现场）──
        check("GitHub 访问线路 · 只有 pnpm / npm / npx / git 这四条命令算「会动网络」的",
            MainWindow.LooksLikeNetworkCommand("pnpm", "add x") &&
            MainWindow.LooksLikeNetworkCommand("npm", "install") &&
            MainWindow.LooksLikeNetworkCommand("npx", "@deepseek-ai/dsh web") &&
            MainWindow.LooksLikeNetworkCommand("git", "clone x") &&
            MainWindow.LooksLikeNetworkCommand("pnpm.cmd", "add x") &&
            !MainWindow.LooksLikeNetworkCommand("powershell", "-Command x") &&
            !MainWindow.LooksLikeNetworkCommand("taskkill", "/PID 1") &&
            !MainWindow.LooksLikeNetworkCommand("node", "--version") &&
            !MainWindow.LooksLikeNetworkCommand("", ""),
            "四种真、四种假");

        // ── ⑨ 设置字段真的落盘（Save 是"重新 new 一个对象再序列化"，漏写字段不报错也不落盘）──
        //    这里在临时目录里跑一次真实的 SettingsManager 往返：写 → 读 → 断字段还在。
        //    ⚠ 必须重定向 DSHGUARD_DATA_DIR（否则会写进真实产品目录），RunCore 开头的固定
        //      已经设好了这个环境变量，这里再核对一遍：没设就整条 SKIP，绝不写真实配置。
        try
        {
            string dataDir = Environment.GetEnvironmentVariable("DSHGUARD_DATA_DIR") ?? "";
            if (dataDir.Length == 0 || !Directory.Exists(dataDir))
            {
                skip("GitHub 访问线路 · 设置字段往返（写盘 → 重读）", "未重定向 DSHGUARD_DATA_DIR（不给真实配置写盘）");
            }
            else
            {
                var temp = new SettingsManager();
                temp.GitHubMode = "mirror";
                temp.Save();
                var reloaded = new SettingsManager();
                check("GitHub 访问线路 · 设置字段往返：写 mirror 后重读仍是 mirror（漏进显式字段列表就会丢）",
                    reloaded.GitHubMode == "mirror" && reloaded.LastSaveFailed == false,
                    $"重读={reloaded.GitHubMode} · 保存失败={reloaded.LastSaveFailed}");
                // 认不出的值必须落 auto（归一在 Load 里做，不是只在界面上做）
                temp.GitHubMode = "乱写的";
                temp.Save();
                var reloaded2 = new SettingsManager();
                check("GitHub 访问线路 · 认不出的设置值读回后落 auto（归一在 Load 里，不是只在界面做）",
                    reloaded2.GitHubMode == "auto",
                    $"重读={reloaded2.GitHubMode}");
                temp.GitHubMode = "auto";
                temp.Save();
            }
        }
        catch (Exception ex)
        {
            skip("GitHub 访问线路 · 设置字段往返（写盘 → 重读）", $"{ex.GetType().Name}: {ex.Message}");
        }

        // ── ⑩ 既有线路表在 2.3.1 换实现后仍守原来的纪律 ──
        //    [64] 那四条断的是"每条都能直接请求 / 首条是直连 / 空地址空表"，与实现无关；
        //    这里补一条**新的**契约：镜像档下表里第一条就是镜像，且表尾仍有直连（兜底不许丢）。
        var assetMirror = GitHubRoute.Routes(assetUrl, GitHubRoute.Mode.Mirror, GitHubRoute.Cap.Asset);
        check("GitHub 访问线路 · 资产下载的镜像档：镜像在前、直连垫底（旧实现里换源只有单向，这条把它钉住）",
            assetMirror.Count == 4 && GitHubRoute.IsMirrored(assetMirror[0]) && assetMirror[^1] == assetUrl,
            $"共 {assetMirror.Count} 条 · 首条镜像={GitHubRoute.IsMirrored(assetMirror[0])} · 末条直连={assetMirror[^1] == assetUrl}");
    }
}
