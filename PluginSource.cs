using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DSHGuard;

/// <summary>
/// 插件依赖来源的识别与"该怎么更新它"。
///
/// 为什么需要它（2026-09-15 现场）：
///   DSH 的插件清单里既有 npm 包（能查到最新版本），也有 git 源：
///     · `github:owner/repo#&lt;40位 sha&gt;` —— 钉死在某个提交，版本查询永远查不到新版；
///     · `git+https://host/repo.git`       —— 未写版本号，同样查不到。
///   于是「更新」按钮对它们永远显示"已是最新"或完全不出现，而 dsh-market 这种
///   走另一条通道的插件却能更新。这里把来源分门别类，各自给出正确的更新方式。
/// </summary>
internal static class PluginSource
{
    public enum Kind
    {
        Unknown,        // 认不出来，不要动
        Registry,       // 普通 npm 包（可带 ^ / ~ / 精确版本）
        GitCommit,      // git 源且钉了某个提交 sha —— 必须去掉 ref 才能更新
        GitRef,         // git 源跟分支或 tag
        GitBare         // git 源未写 ref（默认分支）
    }

    /// <summary>
    /// <c>#ref</c> 的形态（纯解析，只看字符串，不查网）。
    ///
    /// 为什么与 <see cref="Kind"/> 分开（2026-09-18 现场要求）：
    ///   <see cref="Kind"/> 只回答"要不要跟远端提交比"，粒度不够 —— 用户要的是按 ref 类型分流提示：
    ///   「默认分支肯定要报；跟具名分支的另给一种提示，可选升级」。
    ///   而一条 <c>#v1.2.0</c> 与一条 <c>#main</c> 在 <see cref="Kind"/> 眼里都是 <c>GitRef</c>，
    ///   对用户却完全不是一回事：前者指向标签（不可变，默认不报），后者指向分支（跟着走，可选升级）。
    ///
    /// 注意：这里只判形态：「那个名字到底算分支还是标签」必须去问远端
    ///   （见 <see cref="FetchRefKindAsync"/>）。查不到时上层按 <see cref="RefKind.NamedRef"/>
    ///   保守处理并在文案里写明"无法确认"—— 宁可提示得保守，也不许把看不懂的东西说成"已是最新"。
    /// </summary>
    public enum RefKind
    {
        /// <summary>没写 <c>#ref</c>（或只写了一个空 <c>#</c>），即跟仓库默认分支（gitee/github 实测均为 master）。</summary>
        DefaultBranch,
        /// <summary><c>#&lt;hex&gt;</c>，即钉死在某个提交。提交不可变，即永远不报"有新提交"。</summary>
        Commit,
        /// <summary><c>#&lt;名字&gt;</c>，即跟分支或标签；究竟哪种由远端判定给出（<see cref="FetchRefKindAsync"/>）。</summary>
        NamedRef,
        /// <summary><c>#ref</c> 形态本身不合规范（含空白 / 控制字符 / 非 ASCII / 长度越界 / 路径穿越），即不判、不报。</summary>
        Invalid
    }

    /// <summary>
    /// 「仓库最新提交」这次到底查没查到 —— 判定 git 源有没有新版时必须先看它。
    ///
    /// 为什么要有这个枚举（现场：gitee 源插件永远提示更新）：
    ///   旧口径只有"远端提交与锁文件一不一样"一个判据，而 <c>SameCommit</c> 对任一侧为空
    ///   一律返回 false，即「远端查不到」与「远端确实变了」挤在同一个出口，全都报"有更新"。
    ///   网络断了、站点不支持、仓库私有 —— 这些都不是"有新版本"，只能如实说"查不到"。
    /// </summary>
    public enum CommitConfidence
    {
        /// <summary>拿不到远端提交（网络失败 / 站点不支持 / 仓库不可访问），即未知，不是"有更新"。</summary>
        NotQueryable,
        /// <summary>真去查过（HTTP 通了、也解析了响应），即拿到了就是拿到了，拿不到就是这仓库没有可读提交。</summary>
        Queried,
        /// <summary>响应读到了但里面没有能用的提交号（结构变了 / 报文不是提交列表），即未知。</summary>
        Unknown
    }

    /// <summary>
    /// 这条依赖长什么样（纯函数，便于自检）。
    ///
    ///  · 认得的写法（来源口径只有这一份，安装/更新/查新/展示都从这里取结论）：
    ///   · <c>github:o/r[#ref]</c>（另有 <c>gitlab:</c> / <c>bitbucket:</c> / <c>gitee:</c> 简写）；
    ///   · <c>git+https://host/o/r.git</c> / <c>git://host/o/r.git</c> / <c>https://host/o/r.git</c>；
    ///   · <c>file:</c>（本地路径形态，归 git 档；界面归类另见 <c>PluginManager.ClassifySource</c>）；
    ///   · <c>git@host:o/r.git[#ref]</c>（scp 形态，见 <see cref="IsScpGitSource"/>）。
    ///   其余一律 <see cref="Kind.Registry"/>（普通 npm 包，可带 <c>^</c> / <c>~</c> / 精确版本）。
    ///
    ///  · 为什么补 scp 形态（2026-09-18 现场：同一份声明两套身份）：
    ///   展示层 <see cref="ParseGitRepo"/> 一直认 <c>git@host:o/r.git</c>（"作者主页"等链接照常给出），
    ///   而本方法旧的前缀表不含 <c>git@</c>，即同一条 spec 在展示层是 git 仓库、在这里却是 npm 包
    ///   所以 卡片会拿它去镜像源查"最新版本"（必然查不到，即显示成失败/待确认），两套解析各说各话。
    ///   本壳的安装白名单 <c>PluginManager.IsValidGitSource</c> 拒绝 scp 形态（明令不许把凭据形态
    ///   当安装来源），即判成 git 源后，查新侧那道形态闸门（<c>MainWindow.CanInstallPluginSource</c>）
    ///   自动接管，即该插件落 <see cref="Kind"/> 之外记为 <c>Unknown</c>：不报有新版、不出更新按钮，
    ///   与"装不上也升不动"这一事实一致（失败关闭），不再去 npm 侧瞎查。
    ///
    /// 注意：识别必须严（前缀表是闭合的：普通 npm 包名绝不许被误判成 git 源）：
    ///   仅凭"含 <c>@</c> + 冒号"这种通用写法是不够的 —— <c>foo@Dev:abc/def</c> 也能凑出那个形状，
    ///   那样会把包名误判成 git 源（最典型的是 <c>@scope/name</c>：必须仍是 <see cref="Kind.Registry"/>）。
    ///   故只认字面 <c>git@</c> 开头 + 站点在 <see cref="GitHosts"/> 白名单内 + 路径含 <c>/</c>，
    ///   判据与 <see cref="ParseGitRepo"/> 完全同一套（展示/更新/查新三处不再各说各话）。
    /// </summary>
    public static Kind Classify(string? spec)
    {
        string s = (spec ?? "").Trim();
        if (s.Length == 0) return Kind.Unknown;

        bool gitLike = s.StartsWith("github:", StringComparison.OrdinalIgnoreCase)
                    || s.StartsWith("gitlab:", StringComparison.OrdinalIgnoreCase)
                    || s.StartsWith("bitbucket:", StringComparison.OrdinalIgnoreCase)
                    || s.StartsWith("git+", StringComparison.OrdinalIgnoreCase)
                    || s.StartsWith("git://", StringComparison.OrdinalIgnoreCase)
                    || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && s.Contains(".git", StringComparison.OrdinalIgnoreCase)
                    || s.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                    || IsScpGitSource(s);        // scp 形态：git@host:owner/repo[.git][#ref]（见下方方法）
        if (!gitLike) return Kind.Registry;

        int hash = s.LastIndexOf('#');
        if (hash < 0) return Kind.GitBare;

        string refPart = s.Substring(hash + 1).Trim();
        if (refPart.Length == 0) return Kind.GitBare;
        return Regex.IsMatch(refPart, "^[0-9a-fA-F]{7,40}$") ? Kind.GitCommit : Kind.GitRef;
    }

    /// <summary>
    /// scp 形态地址的形状（闭合，比 <see cref="SplitHostPath"/> 里那条展示用正则更严）。
    ///
    /// <c>(?&lt;host&gt;[^:/@?#\s]+)</c> 里的 <c>@ ? #</c> 是刻意排除的：
    ///   · 收起 <c>@</c> -> <c>git@github.com@evil:a/b</c> 这类"两个 @"的怪写法不进 git 档；
    ///   · 收起 <c>?</c> / <c>#</c> -> host 段不许挂查询串或 <c>#ref</c>。
    /// 中间的仓库路径不许含空白 / <c>#</c> / <c>?</c> / <c>\</c>，且必须含 <c>/</c>
    /// （无 <c>/</c> 的 <c>host:path</c> 可能只是 Windows 盘符写法，不能当成 git 源）。
    ///
    /// 注意：末尾那个可选 <c>(?&lt;hash&gt;#[\x21-\x7E]*)?</c> 少不得（第一版漏了它，自检当场抓住）：
    ///   path 段把 <c>#</c> 排除在外，即没有这一组时，<c>git@host:a/b.git#main</c> 这种带 ref 的写法
    ///   整串匹配不上，即 <see cref="Classify"/> 把它当 npm 包，即本缺陷（展示层认、判据不认）原样留着。
    ///   而展示层 <see cref="ParseGitRepo"/> 是先 <c>RepoWithoutRef</c> 再解析（见其 L693） ->
    ///   本形状用的是同一口径：ref 那截只用来认出整串的形状，不参与 host / owner / repo 判定，
    ///   合规性仍由 <see cref="ClassifyRef"/> / <see cref="IsWellFormedRef"/> 一处说了算（此处不另立判据）。
    ///   字符集取 <c>[\x21-\x7E]</c>（可打印 ASCII，去空白；<c>[ -~]</c> 含 ASCII 32 空格，是坑）：
    ///   非 ASCII ref（<c>#分支</c>）与含空白的 ref（<c>#main --upload-pack=evil</c>）都不匹配
    ///   所以 保守落 <see cref="Kind.Registry"/>（归属待确认）—— 与 <see cref="IsWellFormedRef"/> 的
    ///   "ref 只许 ASCII、不许内层空白"口径一致，且不会先被算成 git 源、更不会瞎报更新
    ///   （这类写法本来就装不上也升不动）。
    /// </summary>
    private static readonly Regex ScpGitShape = new(
        @"^git@(?<host>[^:/@?#\s]+):(?<path>[^#?\s\\]*/[^#?\s\\]*)(?<hash>#[\x21-\x7E]*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(200));

    /// <summary>
    /// 是不是 scp 形态的 git 地址（<c>git@host:owner/repo[.git][#ref]</c>）—— <see cref="Classify"/> 的第 7 种形态。
    ///
    /// 判据三条全中才算（缺一不可，这是"别把 npm 包名误判成 git 源"的全部保障）：
    ///   ① 字面以 <c>git@</c> 开头（不是笼统的 <c>user@host:</c> —— 那形状 <c>foo@Dev:abc/def</c> 照样能凑出来）；
    ///   ② 站点（去端口）在 <see cref="GitHosts"/> 闭合白名单内 —— 与 <see cref="ParseGitRepo"/> 同一份白名单；
    ///   ③ 仓库路径能切成 <c>owner/repo</c> 两段（复用 <see cref="SplitOwnerRepo"/>，即结尾 <c>.git</c> 与第 3 段之后的路径同 <see cref="ParseGitRepo"/> 口径）。
    ///
    ///  · 为什么必须这么严（`@scope/name` 是最典型的误判候选）：
    ///   npm 的 <c>@scope/name</c> 不含冒号，即连 <see cref="ScpGitShape"/> 都匹配不上；
    ///   而 <c>foo@Dev:abc/def</c>（含 <c>@</c> 又含冒号）只是"形状像"，倒在判据 ②（host 不在白名单）。
    ///   两条合起来，即普通 npm 包名一律落回 <see cref="Kind.Registry"/>，`@scope/name` 一个都没被带走。
    /// </summary>
    private static bool IsScpGitSource(string s)
    {
        if (!s.StartsWith("git@", StringComparison.OrdinalIgnoreCase)) return false;
        var m = ScpGitShape.Match(s);
        if (!m.Success) return false;

        string host = m.Groups["host"].Value.ToLowerInvariant();
        int colon = host.IndexOf(':');
        if (colon >= 0) host = host.Substring(0, colon);          // 去端口（host:port:owner/repo）
        if (Array.IndexOf(GitHosts, host) < 0) return false;       // 白名单外，即不是 git 源

        var (owner, repo) = SplitOwnerRepo(m.Groups["path"].Value);
        return owner.Length > 0 && repo.Length > 0;
    }

    /// <summary>去掉 `#ref` 之后的仓库地址（GitCommit 更新时用它跟默认分支最新）。</summary>
    public static string RepoWithoutRef(string? spec)
    {
        string s = (spec ?? "").Trim();
        int hash = s.LastIndexOf('#');
        return hash < 0 ? s : s.Substring(0, hash);
    }

    /// <summary>`#ref` 那一截（无 `#`，即空串；`#` 后为空也，即空串）。原样返回，不去空白。</summary>
    public static string RefPart(string? spec)
    {
        string s = (spec ?? "").Trim();
        int hash = s.LastIndexOf('#');
        return hash < 0 ? "" : s.Substring(hash + 1);
    }

    // —— ② 按 ref 类型分流：默认分支 / 钉死提交 / 具名分支 / 标签 / 非法 ——
    //
    // 用户两轮原话（2026-09-18）：
    //   「master 肯定要报」「如果是分支版本的有另一种提示方法，可选升级」
    //   「gitee 是个共同 contribute 的 git 平台，所以应该读的是参与者们的提交吧？」
    // 所以 本壳比的是「仓库默认分支（或用户钉的那个 ref）的最新提交」，与仓库的发行版/标签无关；
    //   二者本来就会不一致（作者持续在 master 提交却不打新标签）—— 这不是 bug，但必须写明白。
    //
    // 分流判据表（本段是唯一实现处，UI 与自检都从这里取结论）：
    //   · 无 ref -> DefaultBranch：常态判"有新提交"，计入一键更新（用户：肯定要报）；
    //   · #<40位/7位 hex> -> Commit：钉死，永不报（提交不可变）；
    //   · #<名字> 且远端是分支，即 NamedRef + Branch：报"可选升级"，不计入一键更新；
    //   · #<名字> 且远端是标签，即 NamedRef + Tag：默认不报；标签被移动（罕见）才提示；
    //   · #<名字> 远端问不出来，即 NamedRef + Unknown：保守不报（失败关闭），文案写明"无法确认"。

    /// <summary>
    /// 远端那个 ref 名字究竟是分支还是标签（查不到就是 <see cref="Unknown"/>，即失败关闭）。
    /// </summary>
    public enum RefMatchKind
    {
        /// <summary>`/branches` 与 `/tags` 都问过（或问到其一）但都没有这个名字 —— 具名 ref 已不存在。</summary>
        Missing,
        /// <summary>没查成（网络 / 站点不支持 / 未登录），即不许据此报更新。</summary>
        Unknown,
        /// <summary>远端确认为分支，即跟分支，可选升级。</summary>
        Branch,
        /// <summary>远端确认为标签，即不可变，默认不报。</summary>
        Tag
    }

    /// <summary>
    /// 探远端 ref 这一次为什么没问成（随 <see cref="RefProbe"/> 一起向上带的失败类别）。
    ///
    /// 为什么要有它（2026-09-19 用户实测，原话：「另外如图3，你这个更新状态是啥？」）：
    ///   卡片正文是「更新状态待确认（未取到仓库最新提交）」，而悬停把三种原因混成一句
    ///   「站点不支持或没登录、仓库私有、当时网络不通，都可能是原因」——
    ///   诚实，但不告诉用户这次到底是哪一种（实测现场：gitee 接口当时回 403 限流，
    ///   用户看到的仍是一句"都可能"）。判据本来就在 <see cref="FetchRefKindAsync"/> 的分诊里算出来了
    ///   （见 <see cref="IsRefProbeNetworkNoise"/>），只是没往上传，即本枚举就是那位"没传上去的信息"。
    ///
    /// 注意：与 <see cref="IsRefProbeNetworkNoise"/> 同源（本项目反复栽在"两份会漂移的判据"上，不许再来一套）：
    ///   两者的唯一实现都是 <see cref="ClassifyRefProbeFailure"/> —— 本枚举是它的返回值，
    ///   噪音判据就是"类别 != <see cref="None"/>"，等价性在那边的注释里逐条对过。
    ///
    /// 映射（HTTP 情形，即类别，即日志级别，级别沿用既有分诊、未改一字）：
    ///   · <c>403</c> / <c>429</c> -> <see cref="RateLimited"/>（站点风控 / 匿名限流 / 配额），即 [WARN]
    ///   · <c>401</c> -> <see cref="Unauthorized"/>（未登录，私有仓库看不出存不存在），即 [WARN]
    ///   · <c>5xx</c> -> <see cref="Server"/>（远端自己故障 / 维护），即 [WARN]
    ///   · 超时 / 连不上 / TLS / <c>SocketException</c> / <c>IOException</c> -> <see cref="Network"/> -> [WARN]
    ///   · <c>200</c> 却读不出内容（提交号 / ref 列表），即 <see cref="Shape"/> -> [ERROR]，不降噪
    ///   · <c>404</c>，即不归本枚举管（远端给的答案：走既有 <see cref="RefMatchKind.Missing"/>，语义一字不动）
    ///
    /// 注意：失败关闭不因本枚举而变：任何类别都不会让界面报"有更新"（不出更新按钮、
    ///   不进「一键更新 N 个」）—— 本枚举只决定"说不清时能不能说清是哪一种说不清"，
    ///   完全不参与"有没有新版"的判定（那仍然只有 <see cref="DecideUpdate"/> 一处说了算）。
    /// </summary>
    public enum RefProbeFailure
    {
        /// <summary>
        /// 没有可指名的失败类别：查成了、远端给了答案（Missing）、压根没去查（形态不合规范），
        /// 以及 <c>404</c> / <c>400</c> 这类不归本枚举管的情形（404 有自己的答案语义，见上面的映射表）。
        /// 所以 它不等于"一切正常"，只表示"这次失败没有可指名道姓的类别"（拿不准就别说，见
        /// <see cref="RefProbeFailureHint"/> 的措辞纪律：拿不准返回空串，绝不编一个原因）。
        /// </summary>
        None,
        /// <summary><c>403</c> / <c>429</c>：站点风控 / 匿名限流 / 配额（gitee 匿名查接口实测常回 403）。</summary>
        RateLimited,
        /// <summary><c>401</c>：未登录（私有仓库：我们根本看不出它存不存在）。</summary>
        Unauthorized,
        /// <summary>本机到远端不通：超时 / 连不上 / DNS / TLS 握手失败 / 连接被重置。</summary>
        Network,
        /// <summary>远端站点自己故障或维护（<c>5xx</c>）。</summary>
        Server,
        /// <summary>
        /// 本壳这侧的问题：HTTP 通了（200）却读不出内容（提交号 / ref 列表的形状不认得），
        /// 或判据/代码自己抛了（<see cref="IsRefProbeNetworkNoise"/> 明确判 false 的那类）——
        /// 仍落 [ERROR]、不许跟着网络类一起降噪：那是本壳要修的东西，
        /// 把它写成"你的网络不好"才是真误导（见 <see cref="FetchRefKindAsync"/> 的注释②）。
        /// 注意：两条路都是这个口径（<see cref="FetchRepoLatestDetailedAsync"/> 亦然，2026-09-19 对齐）：
        ///   网络类降 [WARN]、本类别永远 [ERROR]。
        /// </summary>
        Shape
    }

    /// <summary>某个具名 ref 的远端判定结果（<see cref="FetchRefKindAsync"/> 的产物）。</summary>
    /// <param name="Kind">分支 / 标签 / 不存在 / 问不出来。</param>
    /// <param name="Sha">该 ref 当前指向的提交（查询失败为空串）。</param>
    /// <param name="Date">该提交的日期（取不到为空串）。</param>
    /// <param name="Author">该提交的作者名（取不到为空串）。</param>
    /// <param name="Failure">
    /// <see cref="RefMatchKind.Unknown"/> 时为什么问不出来（见 <see cref="RefProbeFailure"/>）；
    /// 查成了（Branch/Tag）与"远端给了答案"（Missing）一律为 <see cref="RefProbeFailure.None"/>。
    /// 注意：带默认值，即既有的 4 参构造点（<c>new RefProbe(kind, sha, date, author)</c>）逐字不用改，
    ///   这是 2026-09-19 补字段时特意留的兼容口（本文件不许因为加一位就逼着调用点全改一遍）。
    /// </param>
    public readonly record struct RefProbe(RefMatchKind Kind, string Sha, string Date, string Author,
                                           RefProbeFailure Failure = RefProbeFailure.None);

    /// <summary>
    /// <c>#ref</c> 的纯形态判定（不查网，便于自检直接断言）。
    ///
    /// 注意：与 <see cref="Classify"/> 有一处刻意的不同，务必别改回去：
    ///   写了一个 <c>#</c> 却什么都没跟（<c>…repo.git#</c>）时，<see cref="Classify"/> 把它当
    ///   <see cref="Kind.GitBare"/>（"跟默认分支"），这里却判 <see cref="RefKind.Invalid"/>。
    ///   理由是失败关闭：用户既然写了 <c>#</c> 就是要钉某个 ref，只是写漏了 —— 此时按默认分支报更新
    ///   正是"乱报"的一种；宁可不判不报（界面如实写"待确认"），也不替他猜成"跟默认分支"。
    /// </summary>
    public static RefKind ClassifyRef(string? spec)
    {
        string s = (spec ?? "").Trim();
        var k = Classify(s);
        if (k is Kind.Registry or Kind.Unknown) return RefKind.Invalid;
        if (k == Kind.GitCommit) return RefKind.Commit;

        // 真· 没写 #，即跟默认分支（用户的"master 肯定要报"就是这一支）
        if (s.LastIndexOf('#') < 0) return RefKind.DefaultBranch;

        // 写了 # 却是空的（或只剩空白），即畸形写法，保守判 Invalid（见上面的理由）
        if (k == Kind.GitBare) return RefKind.Invalid;

        return IsWellFormedRef(RefPart(s)) ? RefKind.NamedRef : RefKind.Invalid;
    }

    /// <summary>
    /// 具名 ref 的字符白名单（闭合，仿 <c>PluginManager.IsValidGitOwnerRepo</c> 的口径）：
    ///   · 去首尾空白后长度 1..64；
    ///   · 只允许 ASCII 字母数字与 <c>. - _ /</c>；
    ///   · 不许含内层空白 / 控制字符（<c>#main --upload-pack=evil</c> 这类夹带在此被拒）；
    ///   · 不许 <c>..</c>、不许首尾 <c>/</c> 或 <c>.</c>（git 自己的 ref 规则）。
    /// 不合规范，即上层不判不报（见 <see cref="RefKind.Invalid"/>）。
    /// </summary>
    internal static bool IsWellFormedRef(string? rawRef)
    {
        string r = (rawRef ?? "").Trim();
        if (r.Length == 0 || r.Length > 64) return false;
        foreach (char c in r)
        {
            if (c > 0x7F) return false;                                        // 非 ASCII（含中文全角）
            if (char.IsWhiteSpace(c) || char.IsControl(c)) return false;       // 内层空白 / 控制字符
            if (char.IsLetterOrDigit(c)) continue;
            if (c == '.' || c == '-' || c == '_' || c == '/') continue;
            return false;
        }
        if (r.Contains("..")) return false;
        if (r.StartsWith('/') || r.EndsWith('/')) return false;
        if (r.StartsWith('.') || r.EndsWith('.')) return false;
        return true;
    }

    /// <summary>这个字符串像不像一个提交号（与 <c>PluginManager.GitRemoteVerdict</c> 同一口径）。</summary>
    public static bool LooksLikeSha(string? s) => System.Text.RegularExpressions.Regex.IsMatch((s ?? "").Trim(), "^[0-9a-fA-F]{7,40}$");

    /// <summary>
    /// 两端提交是不是同一个（纯函数）：只比前 7 位、忽略大小写，任一侧为空，即 <c>false</c>。
    /// 这是 <c>PluginManager.SameCommit</c> 的实现处（那边是转发，判据只有这一份）。
    /// 注意：这个 <c>false</c> 只表示"判不出相同"，不再等于"有新提交" —— 判有没有新版一律走
    /// <see cref="DecideUpdate"/>（它会把"远端查不到"与"远端确实变了"分开）。
    /// </summary>
    public static bool SameSha(string? lockedCommit, string? remoteCommit)
    {
        string a = (lockedCommit ?? "").Trim();
        string b = (remoteCommit ?? "").Trim();
        if (a.Length < 7 || b.Length < 7) return false;
        return string.Equals(a.Substring(0, 7), b.Substring(0, 7), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// git 源「有没有新版」的唯一判定结果（<see cref="DecideUpdate"/> 的产物）。
    /// <c>PluginManager.GitUpdateVerdict</c> 是它在界面层的镜像（那边转发到这里，判据只有这一份）。
    /// </summary>
    public enum UpdateDecision
    {
        /// <summary>说不清（远端查不到 / 响应读不出提交号 / ref 写法不合规范），即界面如实写"待确认"，不报。</summary>
        Unknown,
        /// <summary>远端提交与锁文件里的同一个，即没有新版。</summary>
        UpToDate,
        /// <summary>确实有新提交，且该自动跟（默认分支），即计入「一键更新 N 个」。</summary>
        HardNewCommit,
        /// <summary>确实有新提交，但该由用户决定（具名分支 / 标签被移动），即提示"可选升级"，不计入计数。</summary>
        AdvisoryNewCommit
    }

    /// <summary>
    /// git 源「有没有新版」的唯一判据（纯函数，按 ref 类型分流）。
    ///
    /// 为什么要有 ref 分流（用户两轮原话 2026-09-18）：
    ///   「master 肯定要报」「如果是分支版本的有另一种提示方法，可选升级」
    ///   「gitee 是个共同 contribute 的 git 平台，所以应该读的是参与者们的提交吧？」
    /// 所以 本壳比的是仓库默认分支（或用户钉的那个 ref）的最新提交，与仓库的发行版/标签无关；
    ///   二者本来就会不一致（作者持续在 master 提交却不打新标签）—— 这不是 bug，但必须写明白。
    ///
    /// 分流表：
    ///   · <see cref="RefKind.DefaultBranch"/>，即常态判"有新提交"（HARD，计入一键更新）；
    ///   · <see cref="RefKind.Commit"/>，即钉死在提交，提交不可变，即永不报；
    ///   · <see cref="RefKind.NamedRef"/> + Branch，即可选升级（ADVISORY，不计入计数）；
    ///   · <see cref="RefKind.NamedRef"/> + Tag，即标签不可变，即默认不报；
    ///                                               标签被移动（罕见），即可选升级；
    ///   · <see cref="RefKind.NamedRef"/> + Unknown/Missing，即失败关闭：不报（说不清就不说）；
    ///   · <see cref="RefKind.Invalid"/> -> ref 写法不合规范，即不判不报。
    ///
    /// 「失败关闭」在这里的含义是宁可不报，不可乱报：远端问不出来时，即使用户钉的分支真变了，
    /// 也只说"待确认"——因为此刻我们证明不了它变了，而报错的代价是诱导用户做一次不该做的升级。
    /// </summary>
    public static UpdateDecision DecideUpdate(string? lockedCommit, string? remoteCommit,
                                              CommitConfidence confidence,
                                              RefKind refKind, RefMatchKind match)
    {
        if (confidence != CommitConfidence.Queried) return UpdateDecision.Unknown;
        string remote = (remoteCommit ?? "").Trim();
        // 远端必须是像提交号的东西才敢说"变了"：站点返回一坨 HTML/message 时不是提交变化。
        if (!LooksLikeSha(remote)) return UpdateDecision.Unknown;
        string locked = (lockedCommit ?? "").Trim();
        if (LooksLikeSha(locked) && SameSha(locked, remote)) return UpdateDecision.UpToDate;

        switch (refKind)
        {
            // 钉死在某个提交：提交不可变，"远端变了"只可能是我们自己换了 ref，不是有新版。
            case RefKind.Commit:
                return UpdateDecision.UpToDate;

            // ref 写法不合规范（空白/控制字符/非 ASCII/路径穿越）：不判不报。
            case RefKind.Invalid:
                return UpdateDecision.Unknown;

            // 跟具名分支或标签：必须由远端判定背书才敢报（失败关闭）。
            case RefKind.NamedRef:
                return match switch
                {
                    // 分支跟着走，即有新提交，但可选（用户有意钉住的分支，不许顺手升）。
                    RefMatchKind.Branch => UpdateDecision.AdvisoryNewCommit,
                    // 标签本应不可变；走到这里说明它就指到了另一个提交，即标签被移动了（罕见），即可选升级。
                    RefMatchKind.Tag => UpdateDecision.AdvisoryNewCommit,
                    // Unknown（没查成）/ Missing（远端已无这个名字）：证明不了它变了，即失败关闭，只报待确认。
                    _ => UpdateDecision.Unknown
                };

            // 默认分支：用户明确要求「肯定要报」。
            default:
                return UpdateDecision.HardNewCommit;
        }
    }

    /// <summary>提交号前 7 位（不足则原样）。</summary>
    public static string ShortSha(string? sha)
    {
        string s = (sha ?? "").Trim();
        return s.Length >= 7 ? s.Substring(0, 7) : s;
    }

    /// <summary>
    /// 「跟到了哪个 ref」——写进悬停与卡片，让用户一眼看清比的是谁
    /// （用户：「插件管理页一直在报 09-17 有新提交，但发布页最新是 09-05 —— 你应该写明白」）。
    /// </summary>
    public static string RefDescription(string? spec, RefMatchKind match)
    {
        switch (ClassifyRef(spec))
        {
            case RefKind.DefaultBranch: return "仓库默认分支";
            case RefKind.Commit: return $"已钉在提交 {ShortSha(RefPart(spec))}";
            case RefKind.Invalid: return "来源里的 ref 写法无法识别";
            default:
                string name = RefPart(spec).Trim();
                return match switch
                {
                    RefMatchKind.Branch => $"远端分支 {name}",
                    RefMatchKind.Tag => $"远端标签 {name}",
                    RefMatchKind.Missing => $"远端已无 {name} 这个分支或标签",
                    _ => $"#{name}（无法确认是分支还是标签）"
                };
        }
    }

    /// <summary>
    /// 「比的是谁」那句话的完整说明（纯函数，便于自检断言）。
    /// 核心是把事实说透：本壳比的是提交，与仓库的发行版（标签）无关（用户的硬要求：
    /// 这句话必须写明白，不许删）。
    ///
    /// 注意：标准表述（2026-09-19 文案标准化）：一句一事，不串句、不解释"为什么会不一致"
    ///   （那属于过度解释 —— 用户原话：不许弱智化或过度解释）。
    ///   · 分隔符一律用「；」（不用破折号串句）；
    ///   · 判据表述一律用「与仓库的发行版（标签）无关」：口语说法「不是一回事」已按用户实测反馈
    ///     （2026-09-19：「你这些注释太口语化了，标准化！」）换成正式表述；
    ///   · 「发行版（标签）」这五个字连同括号一个都不许丢 —— 它承担"比的是提交、不是标签"这一硬要求。
    /// </summary>
    public static string CompareBasisNote(string? spec, RefMatchKind match)
    {
        string who = RefDescription(spec, match);
        // 钉死的提交不会变，即那句话要换个说法，不能写成"按…的最新提交比对"
        if (ClassifyRef(spec) == RefKind.Commit)
            return $"{who}；钉死的提交不会移动，本壳不会提示它有更新；"
                 + "与仓库的发行版（标签）无关。";
        return $"按{who}的最新提交比对；与仓库的发行版（标签）无关。";
    }

    /// <summary>
    /// 「可选升级」那行的卡片文案（纯函数，唯一实现处）：一律含「可选」二字（用户要求），
    /// 并说清"跟的是谁"。分支与"标签被移动"措辞不同 —— 前者是常态（分支本来就会往前走），
    /// 后者是罕见异常，必须让用户看得出区别。
    ///
    /// 注意：本类（以及这里的枚举）是 internal，即上面 <c>PluginManager.GitAdvisoryText</c> 的转发
    ///   也必须是 internal（public 方法不许接受更低可访问性的参数类型，CS0051）。
    /// </summary>
    public static string AdvisoryText(RefMatchKind match, string? refName, string? shortSha)
    {
        string name = (refName ?? "").Trim();
        string sha = (shortSha ?? "").Trim();
        if (match == RefMatchKind.Tag)
            return sha.Length > 0
                ? $"可选升级：标签 {name} 已被移动到 {sha}（罕见）"
                : $"可选升级：标签 {name} 已指向另一个提交（罕见）";
        return sha.Length > 0
            ? $"可选升级：远端分支 {name} 有新提交 {sha}"
            : $"可选升级：远端分支 {name} 有新提交";
    }

    /// <summary>
    /// 「远端已无这个分支或标签」/「无法确认是分支还是标签」这类说不清的情形，卡片上的中性文案
    /// （纯函数；能说清时返回空串，即老路径零改动）。措辞只陈述事实，既不说是新版、也不说是最新。
    /// </summary>
    public static string RefUnknownText(RefKind refKind, RefMatchKind match)
    {
        if (refKind != RefKind.NamedRef) return "";
        return match switch
        {
            RefMatchKind.Missing => "更新状态待确认（远端已无这个分支或标签）",
            RefMatchKind.Unknown => "更新状态待确认（无法确认是分支还是标签）",
            _ => ""
        };
    }

    /// <summary>
    /// <see cref="RefProbeFailure"/>，即悬停里那句「原因」（纯函数；文案的唯一实现处）。
    ///
    /// 存在的理由（用户 2026-09-19 实测，原话：「另外如图3，你这个更新状态是啥？」）：
    ///   卡片正文是「更新状态待确认（未取到仓库最新提交）」，而悬停把三种原因混成一句
    ///   「站点不支持或没登录、仓库私有、当时网络不通，都可能是原因」——诚实但不指名。
    ///   本壳在这次查询里已经知道是哪一种（见 <see cref="RefProbeFailure"/> 的映射表），
    ///   这里把它翻成用户能懂的一句话；上层（卡片悬停）照抄这一句即可，别再自己写一套措辞。
    ///
    /// 注意：措辞纪律（与 <c>MainWindow.UnsupportedSourceNote</c> 同一口径，2026-09-19 文案标准化收紧）：
    ///   只陈述事实，不含内部标识、不含命令行、不出现 HTTP 代号、不出现站点专名、不猜；
    ///   一句一事（一行内不串"下一步"——那由 <see cref="RefProbeFailureNextStep"/> 单独给），
    ///   不用破折号串句，不解释"为什么会这样"。
    ///   拿不准（<see cref="RefProbeFailure.None"/>），即返回空串，
    ///   调用方（<c>MainWindow.UpdateStatusHover</c>）据此如实写「暂无法确定」——
    ///   这里绝不替它编一个原因，调用方也不许编。
    /// </summary>
    public static string RefProbeFailureHint(RefProbeFailure failure) => failure switch
    {
        // 实测现场：匿名查接口被站点风控拒掉（限流），即用户看到的却是一句"都可能"。
        RefProbeFailure.RateLimited => "远端站点拒绝了本次查询（请求过于频繁，或未允许匿名访问）。",
        RefProbeFailure.Unauthorized => "远端站点要求登录，该仓库可能是私有的。",
        RefProbeFailure.Network => "本机与远端站点之间连接未通（连接超时或被中途中断）。",
        RefProbeFailure.Server => "远端站点自身故障或正在维护。",
        RefProbeFailure.Shape => "远端站点已返回内容，但本壳未能解析（属本壳自身的问题，与本机网络无关）。",
        _ => ""
    };

    /// <summary>
    /// <see cref="RefProbeFailure"/>，即悬停里那句「下一步」（纯函数；文案的唯一实现处）。
    ///
    /// 与 <see cref="RefProbeFailureHint"/> 是同一个 switch 的两半：
    ///   前者答"为什么"，本方法答"怎么办"（用户的硬要求：可操作性要保留）。
    ///   · 两半共用同一个类别参数、同一套类别，即档位对应关系，不得单独增删一档
    ///     （类别枚举 <see cref="RefProbeFailure"/> 是唯一判据，本方法不新增任何类别）。
    ///
    /// 注意：拿不准（<see cref="RefProbeFailure.None"/>），即返回空串（与 Hint 同口径）：
    ///   不知道是哪一种就不给"下一步"，绝不编一个方向让用户白跑一趟。
    /// 注意：措辞纪律与 Hint 逐字相同：一句一事、不串"原因"、不出现 HTTP 代号 / 站点专名 / 命令行。
    /// </summary>
    public static string RefProbeFailureNextStep(RefProbeFailure failure) => failure switch
    {
        RefProbeFailure.RateLimited => "请稍后重试；若仍失败，可先在浏览器中登录该站点。",
        RefProbeFailure.Unauthorized => "请在浏览器中登录该站点后重试。",
        RefProbeFailure.Network => "请检查本机网络连接后重试。",
        RefProbeFailure.Server => "请稍后重试。",
        RefProbeFailure.Shape => "请将日志反馈给作者。",
        _ => ""
    };

    /// <summary>
    /// 卡片上「有新版」那一行右侧的括注：短号 + 日期 + 作者。
    /// 摆出作者是为了证明"读的是参与者们的提交、不按作者过滤"（用户问过这一点）；
    /// 而本壳查的确实是该 ref 上的全部提交，任何人推的提交都会算数。
    /// </summary>
    public static string NewCommitNote(string? shortSha, string? date, string? author)
    {
        string sha = (shortSha ?? "").Trim();
        string d = (date ?? "").Trim();
        string a = (author ?? "").Trim();
        var parts = new System.Collections.Generic.List<string>();
        if (sha.Length > 0) parts.Add("提交 " + sha);
        if (d.Length > 0) parts.Add(d);
        if (a.Length > 0) parts.Add("作者 " + a);
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// 某个提交在托管站上的网页地址（可点开给人看；认不出站点/提交号，即空串）。
    /// 只拼 <see cref="GitHosts"/> 白名单内的站点，且必须再经
    /// <c>PluginMarket.IsAllowedLinkUrl</c> 闸门（那两家的 host 都在链接白名单里）才允许打开。
    /// 这里刻意不复刻闸门判据（判据只有一处），只用闭合白名单把 url 拼出来。
    /// </summary>
    public static string CommitUrl(string? spec, string? sha)
    {
        string s = (sha ?? "").Trim();
        if (!LooksLikeSha(s)) return "";
        string repoUrl = RepoUrl(spec);                     // 已受 ParseGitRepo 的白名单约束
        return repoUrl.Length > 0 ? $"{repoUrl}/commit/{s}" : "";
    }

    /// <summary>
    /// 查某个具名 ref 到底是分支还是标签，并取回它当前的提交。
    ///
    /// 三档结果（失败关闭：三档都不报"有新版"，卡片只说"待确认"）：
    ///   · <see cref="RefMatchKind.Branch"/> / <see cref="RefMatchKind.Tag"/> —— 问到了；
    ///   · <see cref="RefMatchKind.Missing"/> —— 远端明确说没有这个名字（分支 404 且标签列表
    ///     第一页也 404；或列表读得懂却没有这个名字），即多是仓库被删 / 改名 / 转私有，
    ///     卡片如实写"远端已无这个分支或标签"（翻页途中的某页 404 不算——那判 Unknown，2026-09-19 收紧）；
    ///   · <see cref="RefMatchKind.Unknown"/> —— 问不出来（超时 / 连不上 / 5xx / 配额 / 未登录，
    ///     以及"HTTP 通了却读不出东西"这种我们自己的判据问题），即卡片写"无法确认是分支还是标签"，
    ///     而究竟是哪一种由 <see cref="RefProbe.Failure"/>（<see cref="RefProbeFailure"/>）随返回值带上去。
    ///
    /// 注意：这两条是 2026-09-18 对抗性复查点名的，判据的唯一实现处，别改回去：
    ///   ① 语义：远端"没有这个 ref"是远端给的答案（Missing），不是"问不出来"（Unknown）——
    ///      两者对用户的含义完全不同（前者是"这个插件被删了/改名了"，后者是"你网络不好，稍后重试"）；
    ///   ② 噪音：用户网络类失败（见 <see cref="IsRefProbeNetworkNoise"/>）只记
    ///      <see cref="Logger.NoteDiagnosis"/>（[WARN]）—— 断网时 N 个 git 源插件不许变成 N 条 [ERROR]。
    ///      但我们的判据类失败（200 却读不出提交号 / 读不出 ref 列表：站点改版、结构变了）仍落 [ERROR]，
    ///      不许跟着一起降噪：那是本壳要修的东西，把它写成"你的网络不好"才是真误导。
    ///
    /// 实测字段名（web_fetch 只读抓公开接口原文，2026-09-18）：
    ///   · gitee  <c>/branches/&lt;名&gt;</c> -> 200：<c>{"name":"master","commit":{"sha":"9669ee48…","commit":{"author":{"name":"Jet","date":"…"}}}}</c>
    ///            <c>/branches/&lt;标签名&gt;</c> -> 404：<c>{"message":"Branch does not exist"}</c>（404 即"没有这个分支"）
    ///            注意：<c>/tags/&lt;名&gt;</c> 实测 404（gitee 没有这个单资源接口），即标签只能查 `/tags` 列表后按名匹配
    ///   · github <c>/branches/&lt;名&gt;</c> -> 200：<c>{"name":"main","commit":{"sha":"4ba82a0c…"}}</c>
    ///            （上一轮实测；本轮本机 api.github.com 被 DNS 解析到 127.0.0.1、只读工具拒访，即未能复测；
    ///              有无 <c>commit.commit</c> 嵌套都不影响提交号那支 —— 读的是 <c>commit.sha</c>）
    ///   · github <c>/tags</c> 列表，即 <c>[{"name":"…","commit":{"sha":"…"}}]</c>（无日期，需再查一次提交详情）
    ///   · gitlab <c>/repository/branches/&lt;名&gt;</c> -> 200：<c>{"name":"master","commit":{"id":"52666b37…","short_id":"52666b37","committed_date":"2026-09-18T12:13:54.000+00:00","author_name":"GitLab Bot"}}</c>
    ///            注意：提交号在 <c>commit.id</c>、日期在 <c>commit.committed_date</c>：这层没有 <c>commit.sha</c>、
    ///              也没有 <c>commit.commit</c> 嵌套，即补上之前分支型 ref 永远读不出提交号（永远 Unknown / "待确认"）
    ///   · bitbucket <c>/refs/branches/&lt;名&gt;</c> -> 200：<c>{"name":"master","target":{"type":"commit","hash":"7294e77a…","date":"2026-09-14T06:49:27+00:00","author":{"raw":"…"}}}</c>
    ///            注意：提交号在 <c>target.hash</c>、日期在 <c>target.date</c>：整份报文里没有 <c>commit</c> 这一层
    ///              （同上，补上之前读不出提交号）
    /// 因此实现是：先查分支（带日期），即再查标签列表（按名匹配）：
    ///   分支 404，即这个名字不是分支，即继续查标签；标签列表第一页 404，即远端连仓库/这个名字都没了，即 Missing；
    ///   列表读得懂但没有这个名字，即也 Missing；列表读不出来（HTML / 改版），即 Unknown + [ERROR]；
    ///   翻页途中某页 404 -> Unknown（2026-09-19 收紧：前几页明明翻到了内容，即说不清，
    ///   绝不判 Missing —— 那比 Unknown 激进，见下面 catch 里的论证）。
    /// 标签的日期 / 作者通过 <c>ParseCommitDetailJson</c> 再查一次提交详情补齐（取不到就不显示，不影响判定）。
    /// </summary>
    public static async Task<RefProbe> FetchRefKindAsync(string? spec, string? refName)
    {
        //  · 失败类别的唯一出口（静态局部函数，不捕获任何状态）：本方法所有"问不出来"的返回都从这里造
        //   所以 不可能出现"某一支忘了分类"（类别映射见 RefProbeFailure 的注释）。
        static RefProbe Unanswered(RefProbeFailure failure) => new(RefMatchKind.Unknown, "", "", "", failure);

        string name = (refName ?? "").Trim();
        // 没写 ref 名 / 站点仓库解析不出来，即压根没去问远端，即没有可归类的远端失败（None）。
        if (name.Length == 0) return Unanswered(RefProbeFailure.None);
        var (owner, repo) = ParseGitRepo(spec);
        if (owner.Length == 0) return Unanswered(RefProbeFailure.None);

        // 站点与"问到第几步"放在 try 外：兜底的 [WARN]/[ERROR] 文案要说清是哪个站点的哪个 ref、哪一步问不成。
        // （host 先给空串，取不到也不影响判定；HostPathOf 只做字符串切分。）
        string host = "";
        string step = "① 分支接口";
        //  · 标签列表翻页：已经发出过请求的是第几页（0 = 一次都没发出去）。
        //   声明在 try 外是必须的 —— 下面那个"翻页途中 404"的 catch 里要判它（声明在 try 内则 catch 里不可见）。
        int pageNoAsked = 0;

        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("DSHGuard");
            (host, _) = HostPathOf(spec);

            // —— ① 分支：gitee 的 GET /branches/<名> 带日期与作者，一次就能拿到全部 ——
            try
            {
                string json = await http.GetStringAsync(BranchApiUrl(host, owner, repo, name));
                var (sha, date, author) = ParseCommitDetailJson(host, json);
                if (LooksLikeSha(sha))
                    return new RefProbe(RefMatchKind.Branch, sha, date, author);
                // HTTP 通了却读不出提交号，即站点结构变了 / 我们的判据出问题（既不是"没有这个分支"，
                // 也不是用户网络），即这条必须落 [ERROR]：它是本壳要修的东西，不许跟着网络类一起降噪。
                Logger.LogError("PluginSource.FetchRefKindAsync",
                    new InvalidDataException($"分支接口返回 200 但读不出提交号（host={host}，repo={owner}/{repo}，ref={name}）⇒ 判 Unknown"));
                return Unanswered(RefProbeFailure.Shape);
            }
            catch (System.Net.Http.HttpRequestException ex404)
                when (ex404.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // 404 = 这个名字不是分支（实测原文 {"message":"Branch does not exist"}），即继续查标签
            }

            // —— ② 标签：只能用列表接口按名匹配（gitee 实测无 /tags/<名>）——
            step = "② 标签列表接口";
            // 注意：解析输入只有第一页（firstPageJson）：名字在后面的页里就当场返回，不必拼接；
            //   多页拼起来不是合法 JSON，即绝不当输入（原实现的那个 tagListJson 由它替代）。
            string firstPageJson;
            bool pageIsRefList;      // 至少有一页确实是"读得懂的 ref 列表"（，即名字不在里面就是远端给的答案）
            try
            {
                //  · 分页（2026-09-19 实测补齐，见 TagListApiUrl 的注释）：列表是一页一页给的 ——
                //   bitbucket 实测默认一页 10 条而 atlassian/aui 有 1352 个标签，即原实现只查第一页，
                //   排在后面的标签一律读不出来，即被判 Missing（"远端已无这个分支或标签"）而它其实存在。
                //   现在：每页 100 条（RefListPageSize，四家实测上限），逐页按名找、找到就停（常见情形仍只发 1 次请求），
                //   没找到才跟着 next 翻下一页（最多 RefListPageLimit 页）。
                //   注意：下面那个 404 的 catch 第一页那支语义逐字未动（Missing）；翻页途中的那支是 2026-09-19 新收紧的
                //     （Unknown），理由写在那个 catch 里。
                pageIsRefList = false;
                firstPageJson = "";
                string nextUrl = TagListApiUrl(host, owner, repo);
                int pageLimit = RefListPageLimit(host);
                for (int pageNo = 1; pageNo <= pageLimit; pageNo++)
                {
                    step = $"② 标签列表接口（第 {pageNo} 页）";
                    //  · 发请求之前就记下页号：404 是在 GetStringAsync 里抛出来的，抛了才知道"问到的是第几页"
                    //   所以 只有记在 await 前面，下面的 catch 才分得清"第一页就 404"与"翻页途中某页 404"。
                    pageNoAsked = pageNo;
                    string pageJson = await http.GetStringAsync(nextUrl);
                    if (pageNo == 1) firstPageJson = pageJson;
                    if (LooksLikeRefListJson(pageJson)) pageIsRefList = true;
                    if (LooksLikeSha(FindShaByName(pageJson, name))) break;      // 找到了，即一页都不多拿
                    // 第一页都读不懂，即不必再翻（后面几页大概也是同一坨 HTML），即交给下面原有的判据处置
                    if (pageNo == 1 && !LooksLikeRefListJson(pageJson)) break;
                    if (!TryNextRefListPageUrl(pageJson, out nextUrl)) break;    // 没有下一页（实测最后一页缺 next 字段）
                }
            }
            catch (System.Net.Http.HttpRequestException ex404)
                when (ex404.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                //  · 语义混淆的修正（2026-09-18 对抗性复查点名）：分支 404 且标签列表 404
                //   所以 远端连仓库/这个名字都没了（被删 / 改名 / 转私有），即这是远端给的答案，判 Missing，
                //   而不是原来那样掉进外层 catch 变成"问不出来 Unknown"（后者会让用户以为是自己的网络问题）。
                //   此处不记日志：它跟下面"列表读得懂、但没有这个名字"是同一档答案、不是失败；
                //   每轮每个插件都写一条，就把刚降下去的噪音又还回去了。
                //
                //  ·· 翻页途中某页 404，即收紧为 Unknown（2026-09-19 补）：
                //   能翻到第 2 页，就说明前面几页明明翻到了内容（第一页是读得懂的列表、还有 next）——
                //   此时某一页突然 404 是远端自相矛盾，不是"远端已无这个 ref"。
                //   旧实现在这里把任何一页的 404 都判成 Missing（旧注释写的"由外层按网络噪音降级成 Unknown"是错的：
                //   这个 catch 就在页循环外面包着，每页的 404 都落在它手里，根本到不了外层），即判"远端已无这个分支或标签"
                //   比 Unknown 激进得多（会怂恿用户以为插件被删/改名了）。
                //   所以 只有第一页就 404（= 分支 404 且标签列表第一页也 404）才走上面那个"远端给的答案"，
                //     语义一字未动（pageNoAsked <= 1 即第一页；0 只在"请求根本没发出去"时出现，正常路径到不了）。
                //   判据本身抽成纯函数 RefListPage404Verdict，自检可直接断言（不必发真请求）。
                if (RefListPage404Verdict(pageNoAsked) == RefMatchKind.Unknown)
                {
                    // 日志级别 [WARN]（不写 [ERROR]）：404 不是 IsRefProbeNetworkNoise 认的噪音
                    //（它刻意不把 404 当噪音：漏到兜底说明代码漏了分支），但这一条也不是本壳的判据失败 ——
                    //   是远端对自己给出的 next 链接回了 404。它是"值得留痕的远端怪脾气"：
                    //   写 [ERROR] 会把一次远端打嗝记成"本次运行出过异常"（Logger.HasFailureEvidence），
                    //   而不写日志又会让"待确认"变得无从解释，即中性一行 [WARN]。
                    Logger.NoteDiagnosis($"标签列表翻页途中第 {pageNoAsked} 页返回 404"
                                       + $"（host={host}，repo={owner}/{repo}，ref={name}）"
                                       + "⇒ 判 Unknown：前面几页翻到了内容，就不能说「远端已无这个 ref」");
                    return Unanswered(RefProbeFailure.None);
                }
                return new RefProbe(RefMatchKind.Missing, "", "", "");
            }

            // 注意：上面这个 404 分支的分工（2026-09-19 收紧后）：第一页 404 -> Missing
            //   （既有语义逐字未改）；翻页途中某页 404 -> Unknown（远端自相矛盾，说不清）——
            //   失败关闭：绝不把一页的意外 404 当成"远端已无这个 ref"。
            string tagSha = FindShaByName(firstPageJson, name);
            if (!LooksLikeSha(tagSha))
            {
                // 「列表读得懂、只是没有这个名字」，即远端给的答案，即 Missing（老路径的判定原样保留）：
                //   · 第一页读得懂（= 老路径的 LooksLikeRefListJson(firstPageJson)，语义逐字等价），且
                //   · 翻完所有页都没有这个名字（TagListContainsName 只看 name 字段、不看提交号。
                //     只看第一页是不够的：分页没补之前，"标签排在第 101 个之后"就是这样被判成"远端没有"的）。
                //  · 若名字在列表里却读不出提交号（bitbucket 注解标签那种形状），即两个条件不成立
                //   所以 落到下面 [ERROR] + Unknown —— 「说不清」可以，「谎报远端没有」不行（见 FindShaByName 的闸门）。
                if (LooksLikeRefListJson(firstPageJson) && pageIsRefList && !TagListContainsName(firstPageJson, name))
                    return new RefProbe(RefMatchKind.Missing, "", "", "");
                // 「这坨东西根本不是列表」（HTML 报错页 / 站点改版 / 半截响应），即我们的判据出问题
                //   所以 [ERROR] + Unknown。少了这一判，就会把自己的解析失败说成"远端已无这个 ref"（栽赃远端）。
                Logger.LogError("PluginSource.FetchRefKindAsync",
                    new InvalidDataException($"标签列表接口返回 200 但读不出 ref 列表（host={host}，repo={owner}/{repo}）⇒ 判 Unknown"));
                return Unanswered(RefProbeFailure.Shape);
            }

            // 标签的日期 / 作者：标签列表接口没有这两项（实测），再查一次提交详情补齐。
            // 注意：报不报"标签被移动"只看 sha，与这里取不取得到日期无关（取不到只是不显示）。
            string tDate = "", tAuthor = "";
            try
            {
                string detail = await http.GetStringAsync(CommitDetailApiUrl(host, owner, repo, tagSha));
                var (dSha, dDate, dAuthor) = ParseCommitDetailJson(host, detail);
                if (LooksLikeSha(dSha)) { tDate = dDate; tAuthor = dAuthor; }
            }
            catch { }
            return new RefProbe(RefMatchKind.Tag, tagSha, tDate, tAuthor);
        }
        catch (Exception ex) when (IsRefProbeNetworkNoise(ex))
        {
            // 用户网络 / 远端站点不可达 / 配额 / 未登录：不是本壳的错误，即单行中性诊断（[WARN]），
            // 不写 [ERROR]。理由：断网时 N 个 git 源插件会写 N 条 [ERROR]（每条还是 3 行：正文+栈+Inner），
            // 而 [ERROR] 会把 Logger.HasFailureEvidence 置真，即一次普通断网被记成"本次运行出过异常"。
            Logger.NoteDiagnosis($"查远端 ref 没能问成（{step}，host={host}，repo={owner}/{repo}，ref={name}）："
                               + $"{ex.GetType().Name}: {ex.Message} ⇒ 判 Unknown（卡片写「待确认」，不报更新）");
            // 类别：403/429 -> RateLimited、401 -> Unauthorized、5xx -> Server、网络类，即 Network
            //（与 IsRefProbeNetworkNoise 同一个分类器；本 catch 是被它筛进来的，即类别必不为 None）。
            return Unanswered(ClassifyRefProbeFailure(ex));
        }
        catch (Exception ex)
        {
            // 兜底：真错误（我们自己的判据/代码问题，例如漏了一个 404 分支、URI 拼错）仍落 [ERROR]，不降噪。
            Logger.LogError("PluginSource.FetchRefKindAsync", ex);
            // 类别：本壳这侧的问题（含"HTTP 通了却读不出东西"以外的判据/代码异常），即 Shape，语义见枚举注释。
            return Unanswered(RefProbeFailure.Shape);
        }
    }

    /// <summary>
    /// 探 ref 失败时的日志分诊：这次失败是"用户/远端的处境"还是"本壳判据的错"（纯函数；
    /// 自检可喂样本断言，不必发真请求 —— 例如 <c>new System.Net.Http.HttpRequestException("x", null, System.Net.HttpStatusCode.NotFound)</c>）。
    ///
    /// 判据只有一条，与项目既有的"无害噪音"口径同源（见 <c>Logger.NoteKnownNoise</c> / <c>Logger.ClassifyStderrStep</c>：
    /// 先分清"可忽略的处境"与"真问题"，绝不让前者冒充后者）：
    ///   · <c>true</c>，即用户网络 / 远端站点 / 配额 / 未登录：重试可能就好，调用方只记
    ///     <see cref="Logger.NoteDiagnosis"/>（[WARN]），不写 [ERROR]；
    ///   · <c>false</c>，即本壳自己的问题（判据 / 结构 / 代码）：调用方照旧落 [ERROR]，不许降噪。
    ///
    /// 注意：<c>404</c> 刻意不算噪音：它是远端给的答案（不是错误），且已在各自那一步就地处置
    ///   （分支 404，即继续查标签；标签列表第一页 404 -> <see cref="RefMatchKind.Missing"/>，
    ///   翻页途中某页 404 -> Unknown 并单独记一行 [WARN]，见 <see cref="FetchRefKindAsync"/> 里那个 catch）。
    ///   真有一个 404 漏到兜底，说明代码漏了分支 —— 那正是该报 [ERROR] 的情形。
    ///
    ///  · 2026-09-19：本判据与失败类别 <see cref="RefProbeFailure"/> 合成了一份实现
    ///   （<see cref="ClassifyRefProbeFailure"/>）—— 原来这里那段 switch 与类别映射会是两份会漂移的判据，
    ///   正是本项目反复栽过的坑。等价性：本方法 = "类别 != <see cref="RefProbeFailure.None"/>"，
    ///   逐条对照（5xx/401/403/429/无状态码/超时/Socket/IO）与旧 switch 一模一样，
    ///   故自检里那批 <c>IsRefProbeNetworkNoise</c> 样本（含 404/400/本壳异常三条反例）结论不变。
    /// </summary>
    internal static bool IsRefProbeNetworkNoise(Exception? ex)
        => ClassifyRefProbeFailure(ex) != RefProbeFailure.None;

    /// <summary>
    /// 探远端 ref 失败的唯一分类器（纯函数；自检可喂样本断言，不必发真请求 —— 例如
    /// <c>new System.Net.Http.HttpRequestException("x", null, System.Net.HttpStatusCode.Forbidden)</c>）。
    ///
    /// 一个实现、两个出口（不许各写一套）：
    ///   · <see cref="IsRefProbeNetworkNoise"/> = 「类别 != <see cref="RefProbeFailure.None"/>」（既有判据，语义未变）；
    ///   · <see cref="RefProbeFailure"/> = 本方法的返回值，随 <see cref="RefProbe"/> 一路带到界面
    ///     所以 卡片"说不清"时能说清是哪一种说不清（用户 2026-09-19 实测要求）。
    ///
    /// 判据与映射（与 <see cref="IsRefProbeNetworkNoise"/> 的既有口径逐条对应）：
    ///   · <c>403</c> / <c>429</c> -> <see cref="RefProbeFailure.RateLimited"/>（站点风控 / 匿名限流 / 配额）；
    ///   · <c>401</c> -> <see cref="RefProbeFailure.Unauthorized"/>（未登录；私有仓库看不出存不存在）；
    ///   · <c>5xx</c> -> <see cref="RefProbeFailure.Server"/>（远端自己故障 / 维护）；
    ///   · 超时 / 连不上 / TLS / <c>SocketException</c> / <c>IOException</c> -> <see cref="RefProbeFailure.Network"/>；
    ///   · <c>404</c> 与其余状态码，即 <see cref="RefProbeFailure.None"/>（不归本枚举管：404 是远端给的答案，
    ///     在各自那一步就地处置；真漏到兜底说明代码漏了分支，即本方法返回 None，即噪音判据仍为 false
    ///     所以 调用方照旧落 [ERROR]，语义一字未改）；
    ///   · 别的异常（本壳自己的判据/代码问题），即同样是 <see cref="RefProbeFailure.None"/>
    ///     —— "分不出类别"不是"没问题"：该落 [ERROR] 的调用方照旧落。
    ///
    /// 注意：<see cref="RefProbeFailure.Shape"/> 不由本方法产出：它指"HTTP 通了（200）却读不出内容"，
    ///   在 <see cref="FetchRefKindAsync"/> / <see cref="FetchRepoLatestDetailedAsync"/> 的解析失败处就地填
    ///   （那两处必须落 [ERROR]，是本壳要修的东西；见 <see cref="RefProbeFailure.Shape"/> 的注释）。
    ///   本方法只分诊异常，所以"异常 ⇒ 类别"永远不可能是 Shape。
    ///
    ///  · 本方法返回"非 <see cref="RefProbeFailure.None"/>"时，两条路的调用方都只记
    ///   <see cref="Logger.NoteDiagnosis"/>（[WARN]）、不写 [ERROR] —— 2026-09-19 把
    ///   <see cref="FetchRepoLatestDetailedAsync"/> 那条路补齐成与 <see cref="FetchRefKindAsync"/> 同口径
    ///   （判据是同一份，级别也就必须同一档；见那边的 catch 与 RefProbeFailure 的映射表）。
    /// </summary>
    internal static RefProbeFailure ClassifyRefProbeFailure(Exception? ex)
    {
        switch (ex)
        {
            // HttpClient 的 Timeout 到点抛的是 TaskCanceledException（内含 TimeoutException）；
            // 本方法不传 CancellationToken，即它只可能是超时，不会是"用户取消"。
            case TaskCanceledException: return RefProbeFailure.Network;
            case OperationCanceledException: return RefProbeFailure.Network;          // 超时的基类（上面那条先命中）
            case System.Net.Sockets.SocketException: return RefProbeFailure.Network;  // DNS 失败 / 连接被拒 / 网络不可达
            case IOException: return RefProbeFailure.Network;                         // 响应读到一半断了（连接被重置）
            case System.Net.Http.HttpRequestException h:
                if (h.StatusCode is null) return RefProbeFailure.Network;   // 连 HTTP 状态都没拿到（DNS / 连不上 / TLS 握手失败）
                int code = (int)h.StatusCode;
                if (code == 401) return RefProbeFailure.Unauthorized;       // 未登录（私有仓库：我们根本看不出它存不存在）
                if (code == 403 || code == 429) return RefProbeFailure.RateLimited;   // 站点风控 / 配额（gitee· github 匿名限流常走 403）
                if (code >= 500) return RefProbeFailure.Server;             // 远端站点自己故障 / 维护
                return RefProbeFailure.None;                                // 404 / 400 / 其余：不归本枚举管（见上面的注释）
            default: return RefProbeFailure.None;
        }
    }

    /// <summary>
    /// 这份报文能不能当"ref 列表"来读（纯函数；自检可喂样本断言，不发真请求）。
    ///
    /// 为什么必须单独判一次：<see cref="FindShaByName"/> 的约定是"认不出来返回空串、绝不抛"，
    /// 于是两件完全不同的事都会得到空串：
    ///   · 列表读得懂、只是没有这个名字，即远端给的答案，即 <see cref="RefMatchKind.Missing"/>；
    ///   · 这坨东西根本不是列表（HTML 报错页 / 站点改版 / 半截响应），即我们的判据出问题
    ///     所以 <see cref="RefMatchKind.Unknown"/> + <c>[ERROR]</c>。
    /// 少了这一判，第二种会被说成"远端已无这个 ref" —— 那是拿自己的解析失败去栽赃远端。
    /// 认得的形状与 <see cref="FindShaByName"/> 完全一致（gitee/github 是根数组，bitbucket 是 <c>{"values":[…]}</c>）。
    /// </summary>
    internal static bool LooksLikeRefListJson(string? json)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json)) return false;
            using var doc = System.Text.Json.JsonDocument.Parse(json!);
            var root = doc.RootElement;
            if (root.ValueKind == System.Text.Json.JsonValueKind.Array) return true;
            return root.ValueKind == System.Text.Json.JsonValueKind.Object
                && root.TryGetProperty("values", out var vals)
                && vals.ValueKind == System.Text.Json.JsonValueKind.Array;
        }
        catch { return false; }
    }

    /// <summary>分支单资源接口（gitee 实测有；github 亦同形）。</summary>
    private static string BranchApiUrl(string host, string owner, string repo, string name) => host switch
    {
        "gitee.com" => $"https://gitee.com/api/v5/repos/{owner}/{repo}/branches/{Uri.EscapeDataString(name)}",
        "gitlab.com" => $"https://gitlab.com/api/v4/projects/{Uri.EscapeDataString(owner + "/" + repo)}/repository/branches/{Uri.EscapeDataString(name)}",
        "bitbucket.org" => $"https://api.bitbucket.org/2.0/repositories/{owner}/{repo}/refs/branches/{Uri.EscapeDataString(name)}",
        _ => $"https://api.github.com/repos/{owner}/{repo}/branches/{Uri.EscapeDataString(name)}"
    };

    /// <summary>
    /// 标签列表接口（gitee 的 <c>/tags/&lt;名&gt;</c> 实测 404，即只走列表）。
    ///
    ///  · 每页条数必须显式给（2026-09-19 实测补齐"分页"这一处完备性缺口）：
    ///   · bitbucket 实测 <c>api.bitbucket.org/2.0/repositories/atlassian/aui/refs/tags</c>
    ///     所以 响应里 <c>"pagelen": 10, "size": 1352, "page": 1, "next": "…?page=2"</c>：
    ///     默认只给 10 条、而这个仓库有 1352 个标签，即排在后面的标签根本不在这一页里
    ///     所以 <see cref="FindShaByName"/> 读不出来，即判 <see cref="RefMatchKind.Missing"/>
    ///     所以 卡片写「远端已无这个分支或标签」，而标签其实存在（同上一个坑：拿我们的判据失败去栽赃远端）。
    ///   · 上限实测：<c>pagelen=100</c> -> 200 + 100 条；<c>pagelen=101</c> -> 400（硬上限 100）；
    ///     github <c>per_page=200/1000</c> -> 200 但只回 100 条（静默截到 100、不报错）；
    ///     gitlab <c>per_page=100</c>，即响应头 <c>X-Per-Page: 100</c>（默认 <c>X-Per-Page: 20</c>）；
    ///     gitee 本机实测 403（限流），即未实测，按官方文档 <c>per_page</c> 写、见下方对照表注释。
    ///   所以 四家一律用 100（<see cref="RefListPageSize"/>）：这是四家共同的上限，不是猜的。
    ///   注意：超过 100 个标签的仓库仍要靠 <c>next</c> 翻页（见 <see cref="FetchRefKindAsync"/> 的页循环）——
    ///     只补 <c>pagelen</c> 只是把盲区从"第 11 个之后"缩到"第 101 个之后"，不算修完。
    /// </summary>
    private static string TagListApiUrl(string host, string owner, string repo) => host switch
    {
        // gitee 官方 API v5 文档：per_page 默认 20、最大 100（本机 403，未能实测，即按文档写，与另三家同为 100 不改变风险面）
        "gitee.com" => $"https://gitee.com/api/v5/repos/{owner}/{repo}/tags?per_page={RefListPageSize}",
        // gitlab 实测：默认 20、per_page=100 时响应头 X-Per-Page: 100、X-Total-Pages: 21（2025 个标签）
        "gitlab.com" => $"https://gitlab.com/api/v4/projects/{Uri.EscapeDataString(owner + "/" + repo)}/repository/tags?per_page={RefListPageSize}",
        // bitbucket 实测：默认 pagelen=10、pagelen=100 可用、pagelen=101 -> 400
        "bitbucket.org" => $"https://api.bitbucket.org/2.0/repositories/{owner}/{repo}/refs/tags?pagelen={RefListPageSize}",
        // github 实测：默认 30 条；per_page=100 -> 100 条（per_page=200/1000 静默截到 100）
        _ => $"https://api.github.com/repos/{owner}/{repo}/tags?per_page={RefListPageSize}"
    };

    /// <summary>
    /// 标签列表一页最多要几条 —— 四家实测共同的上限（实测值，不是猜的）：
    ///   bitbucket <c>pagelen=101</c> -> 400（100 是硬上限）；
    ///   github <c>per_page=200/1000</c> -> 200 但只回 100 条（静默截断）；
    ///   gitlab <c>per_page=100</c>，即响应头 <c>X-Per-Page: 100</c>；
    ///   gitee 本机 403 限流、未实测，即按官方文档 100（四家里唯一一条非实测项，如实标注）。
    /// 超过这个数就得靠 <c>next</c> 翻页（<see cref="FetchRefKindAsync"/>）。
    /// </summary>
    private const int RefListPageSize = 100;

    /// <summary>
    /// 每轮最多为一个 ref 翻几页标签列表（防失控：站点若不返回下一链接就自然停，这个数管住最坏情况）。
    /// bitbucket 实测的 <c>next</c> 形状：<c>{"…","next":"https://api.bitbucket.org/2.0/repositories/atlassian/aui/refs/tags?pagelen=100&amp;page=2","previous":null}</c>
    /// 所以 复制前必须过 <see cref="TryNextRefListPageUrl"/> 的守卫：只认 https + 四家已知主机。
    /// 100 条/页 × 20 页 = 2000 个标签（实测 atlassian/aui 有 1352 个，即 14 页够用）；
    /// 翻页是逐页按名找、找到就停，所以找不到的名字才是最坏情况。
    /// </summary>
    private static int RefListPageLimit(string host) => host switch
    {
        "gitee.com" => 20,
        "gitlab.com" => 40,     // 实测 gitlab-org/gitlab-foss 有 2025 个标签（X-Total-Pages=21，per_page=100）
        "bitbucket.org" => 20,
        // github 未实测过这么大的仓库：给同样的上限，宁可少认也绝不猜着放大
        _ => 20
    };

    /// <summary>
    /// 标签列表翻页时某一页返回 <c>404</c> 该怎么判（纯函数，自检可直接断言，不必发真请求）：
    ///   · 第 1 页（<paramref name="pageNoAsked"/> ≤ 1），即 <see cref="RefMatchKind.Missing"/>
    ///     —— "分支 404 且标签列表第一页也 404"是远端给的答案（可能被删 / 改名 / 转私有），
    ///     这一支是既有语义，一字未动；
    ///   · 第 2 页及以后，即 <see cref="RefMatchKind.Unknown"/> —— 能翻到第 2 页说明前面几页明明翻到了内容
    ///     （第一页读得懂、还有 <c>next</c>），此时某页突然 404 是远端自相矛盾，属于"说不清"。
    ///     判 <see cref="RefMatchKind.Missing"/> 会写成「远端已无这个分支或标签」——那比 Unknown 激进
    ///     （会怂恿用户以为插件被删/改名了），故 2026-09-19 收紧成 Unknown。
    ///
    /// 注意：0 只在"请求根本没发出去"时出现（正常路径到不了），按第 1 页处理，即保持收紧前的行为，不引入新语义。
    /// </summary>
    internal static RefMatchKind RefListPage404Verdict(int pageNoAsked)
        => pageNoAsked > 1 ? RefMatchKind.Unknown : RefMatchKind.Missing;

    /// <summary>
    /// 这份（可能由多页拼接起来的）标签列表里有没有这个名字（纯函数；自检可喂实测样本断言，不必发真请求）。
    ///
    /// 与 <see cref="FindShaByName"/> 是两件事，所以必须分开判：
    ///   · <see cref="FindShaByName"/> 回答"这个名字指向哪个提交"（认不出形状就返回空串）；
    ///   · 本方法只回答"这个名字在不在列表里"（完全不看提交号）。
    /// 为什么需要它（2026-09-19）：bitbucket 的注解标签有可能长得让 <see cref="FindShaByName"/> 认不出提交号，
    /// 若只看"提交号为空"就判 <see cref="RefMatchKind.Missing"/>，就会把明明存在的标签说成"远端已无这个分支或标签"
    /// —— 那是拿我们的判据失败去栽赃远端。有了这一判，就能把"列表里确无此名"（Missing）与
    /// "有此名但读不出提交号"（Unknown + [ERROR]）分开。
    /// 认得的名字形状与 <see cref="FindShaByName"/> 完全一致（同样的 <c>name</c> 字段、同样忽略大小写），
    /// 「认不出来返回 false、绝不抛」；拼接过的多页文本解析不了，即也是 false（失败关闭）。
    /// </summary>
    internal static bool TagListContainsName(string? json, string? name)
    {
        try
        {
            string want = (name ?? "").Trim();
            if (want.Length == 0 || string.IsNullOrWhiteSpace(json)) return false;
            using var doc = System.Text.Json.JsonDocument.Parse(json!);
            var root = doc.RootElement;
            System.Text.Json.JsonElement arr;
            if (root.ValueKind == System.Text.Json.JsonValueKind.Array) arr = root;
            else if (root.ValueKind == System.Text.Json.JsonValueKind.Object
                     && root.TryGetProperty("values", out var vals) && vals.ValueKind == System.Text.Json.JsonValueKind.Array) arr = vals;   // bitbucket
            else return false;

            foreach (var e in arr.EnumerateArray())
            {
                if (e.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                if (!e.TryGetProperty("name", out var nEl) || nEl.ValueKind != System.Text.Json.JsonValueKind.String) continue;
                if (string.Equals((nEl.GetString() ?? "").Trim(), want, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        catch { return false; }
    }

    /// <summary>
    /// 从标签列表的一页里读 <c>next</c> 下一页链接（纯函数；自检可喂实测样本断言，不必发真请求）。
    ///
    /// 认不出来 / 不该跟 一律返回 <c>false</c>（失败关闭：宁可只查第一页，也不跟着一个来路不明的 URL 走）：
    ///   · json 里没有 <c>next</c>、或 <c>next</c> 不是字符串（实测最后一页是缺少该字段、不是 null）
    ///   · 不是绝对 https（只认 https：列表里可能带 token 之类的查询参数，绝不让它掉到明文 http）
    ///   · 主机不是四家已知站点或其 API 子域（<see cref="GitHosts"/> 存的是站点主机 <c>bitbucket.org</c>，
    ///     而实测 next 的主机是 <c>api.bitbucket.org</c>，即判据放宽到"等于站点主机或以 <c>.站点主机</c> 结尾"；
    ///     本轮的守卫自检正是先发现"只做相等判断会把 bitbucket 翻页挡死"才改的 —— 防的是被响应牵着走到别处去）
    /// 注意：归一化：把 <c>next</c> 里已有的 <c>pagelen</c> / <c>per_page</c> 换成本工程的 <see cref="RefListPageSize"/>、
    ///   其余参数（如 bitbucket 的 <c>page</c>、gitlab 的 <c>order_by</c>/<c>sort</c>）原样保留 ——
    ///   站点在 <c>next</c> 里回带的那个 pagelen（我们请求时是多少它就是多少）直接用也不会更差，
    ///   但换成我们自己的常量能让"每页条数"只有一处判据、翻页边界可预期。
    /// </summary>
    internal static bool TryNextRefListPageUrl(string? json, out string nextUrl)
    {
        nextUrl = "";
        try
        {
            if (string.IsNullOrWhiteSpace(json)) return false;
            using var doc = System.Text.Json.JsonDocument.Parse(json!);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
            if (!doc.RootElement.TryGetProperty("next", out var nEl)) return false;
            if (nEl.ValueKind != System.Text.Json.JsonValueKind.String) return false;
            string raw = (nEl.GetString() ?? "").Trim();
            if (raw.Length == 0) return false;
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var u)) return false;
            if (u.Scheme != Uri.UriSchemeHttps) return false;
            // 注意：主机判据必须允许站点主机的 API 子域：实测 bitbucket 的 next 主机是
            //   `api.bitbucket.org`，而 GitHosts 里存的是 `bitbucket.org` —— 只做 Contains 相等判断的话
            //   bitbucket 的翻页会被自己这道守卫挡死（分页等于没修）。故按"等于站点主机，或它的子域"判
            //   （`api.bitbucket.org` 以 `.bitbucket.org` 结尾，即通过；`evilbitbucket.org` 不通过）。
            string hostLower = u.Host.ToLowerInvariant();
            bool hostOk = false;
            foreach (string known in GitHosts)
            {
                if (hostLower == known || hostLower.EndsWith("." + known, StringComparison.Ordinal)) { hostOk = true; break; }
            }
            if (!hostOk) return false;

            string pageName = hostLower == "bitbucket.org" || hostLower.EndsWith(".bitbucket.org", StringComparison.Ordinal)
                ? "pagelen" : "per_page";
            var kept = new List<string>();
            foreach (string kv in u.Query.TrimStart('?').Split('&'))
            {
                if (kv.Length == 0) continue;
                int eq = kv.IndexOf('=');
                string key = eq < 0 ? kv : kv.Substring(0, eq);
                if (string.Equals(Uri.UnescapeDataString(key), pageName, StringComparison.OrdinalIgnoreCase)) continue;
                kept.Add(kv);
            }
            kept.Add(pageName + "=" + RefListPageSize);
            nextUrl = u.GetLeftPart(UriPartial.Path) + "?" + string.Join("&", kept);
            return true;
        }
        catch { return false; }
    }

    /// <summary>提交详情接口（标签补齐日期 / 作者用；gitee 与 github 实测同形）。</summary>
    private static string CommitDetailApiUrl(string host, string owner, string repo, string sha) => host switch
    {
        "gitee.com" => $"https://gitee.com/api/v5/repos/{owner}/{repo}/commits/{sha}",
        "gitlab.com" => $"https://gitlab.com/api/v4/projects/{Uri.EscapeDataString(owner + "/" + repo)}/repository/commits/{sha}",
        "bitbucket.org" => $"https://api.bitbucket.org/2.0/repositories/{owner}/{repo}/commit/{sha}",
        _ => $"https://api.github.com/repos/{owner}/{repo}/commits/{sha}"
    };

    /// <summary>
    /// 分支单资源 / 标签列表 / 提交详情，即（提交号, 日期, 作者名）。纯函数（自检直接喂 JSON 样本）。
    ///
    /// 实测四个接口的字段位置并不一致（2026-09-18 直连只读抓公开接口原文），本函数按站点形状逐个认：
    ///   · gitee  /branches/&lt;名&gt;：<c>commit.sha</c>，日期与作者在 <c>commit.commit.author</c>（嵌套两层）
    ///   · gitee  /commits/&lt;sha&gt;：同上形状
    ///   · gitee  /tags（列表）：<c>[{name, commit:{sha, date}}]</c> —— 日期在 commit 的下一层（无 <c>commit.commit</c>）
    ///   · github /branches/&lt;名&gt;：<c>commit.sha</c>（上一轮实测；本轮本机 api.github.com 被 DNS 解析到
    ///            127.0.0.1、只读工具拒访，即未能复测；有无 <c>commit.commit</c> 嵌套都不影响提交号那一支）
    ///   · github /commits/&lt;sha&gt;：<c>sha</c> + <c>commit.author.name/date</c>
    ///   · gitlab /repository/branches/&lt;名&gt;：提交号在 <c>commit.id</c>（实测，不是 <c>commit.sha</c>），
    ///            日期在 <c>commit.committed_date</c> / <c>authored_date</c>（不是 <c>date</c>）、作者在 <c>commit.author_name</c>
    ///   · gitlab /repository/commits/&lt;sha&gt;：<c>id</c> + <c>committed_date</c> + <c>author_name</c>（都在根上）
    ///   · bitbucket /refs/branches/&lt;名&gt;：提交挂在 <c>target</c> 下 —— <c>target.hash</c> + <c>target.date</c>
    ///            （整份报文里没有 <c>commit</c> 这一层），作者 <c>target.author.raw</c>（"名字 &lt;邮箱&gt;"）
    ///            或 <c>target.author.user.display_name</c>
    ///   · bitbucket /commit/&lt;sha&gt;：<c>hash</c> + <c>date</c>（在根上，既有行为）
    ///   · bitbucket /refs/tags（列表）：<c>{"values":[…]}</c>，每项的 <c>target</c> 同样是对象（带 <c>hash</c>）—— 与 gitlab 的字符串 <c>target</c> 不同形
    ///            （已支持：<see cref="FindShaByName"/> 也认了 <c>target.hash</c> 这一支；
    ///             该项自己的 <c>date</c> 是标签日期、<c>target.date</c> 才是提交日期，即别串门）
    /// 注意：层次别串门：gitee 分支是两层 <c>commit.commit.author.date</c>、gitee 标签是一层 <c>commit.date</c>、
    ///   gitlab 是 <c>commit.committed_date</c>、bitbucket 是 <c>target.date</c> —— 把某家的层次套到别家就是读错日期。
    /// 认不出来一律返回空串，绝不抛。
    /// </summary>
    internal static (string Sha, string Date, string Author) ParseCommitDetailJson(string? host, string? json)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json)) return ("", "", "");
            using var doc = System.Text.Json.JsonDocument.Parse(json!);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return ("", "", "");

            // ① 提交号：对象根的 sha / hash / id
            string sha = "";
            if (root.TryGetProperty("sha", out var sEl) && sEl.ValueKind == System.Text.Json.JsonValueKind.String) sha = sEl.GetString() ?? "";
            else if (root.TryGetProperty("hash", out var hEl) && hEl.ValueKind == System.Text.Json.JsonValueKind.String) sha = hEl.GetString() ?? "";
            else if (root.TryGetProperty("id", out var iEl) && iEl.ValueKind == System.Text.Json.JsonValueKind.String) sha = iEl.GetString() ?? "";

            string date = "", author = "";

            // ② gitee/github 的 /branches/<名>：提交信息挂在 commit 下面
            if (root.TryGetProperty("commit", out var cEl) && cEl.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                if (sha.Length == 0 && cEl.TryGetProperty("sha", out var cs) && cs.ValueKind == System.Text.Json.JsonValueKind.String)
                    sha = cs.GetString() ?? "";
                //  · gitlab 的 /repository/branches/<名>：提交号在 commit.id（实测原文：
                //   {"name":"master","commit":{"id":"52666b3772e0869a105772a6a5cb11e973c9e4ae","short_id":"52666b37",
                //    "committed_date":"2026-09-18T12:13:54.000+00:00","author_name":"GitLab Bot",…}}）
                //   —— 这一层没有 commit.sha、也没有 commit.commit 嵌套，即不补这一支就永远读不出提交号。
                //   只在前两支都没读到时才看它，即 gitee/github 的形状解析逐字未变。
                if (sha.Length == 0 && cEl.TryGetProperty("id", out var cidEl) && cidEl.ValueKind == System.Text.Json.JsonValueKind.String)
                    sha = cidEl.GetString() ?? "";
                // 分支接口：sha 在 commit.sha，而日期/作者在 commit.commit.author（gitee 实测）
                if (cEl.TryGetProperty("commit", out var cc) && cc.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    ReadAuthorAndDate(cc, ref date, ref author);
                }
                else
                {
                    // 标签列表项：{name, commit:{sha, date}} —— 日期直接在 commit 层，且没有 author.name
                    if (cEl.TryGetProperty("date", out var dEl) && dEl.ValueKind == System.Text.Json.JsonValueKind.String)
                        date = dEl.GetString() ?? "";
                    //  · gitlab 这一层用的不是 date，而是 committed_date / authored_date（实测）。
                    //   只在 gitee/github 的 date 读不到时才看，即既有形状一字未改。
                    if (date.Length == 0 && cEl.TryGetProperty("committed_date", out var gcdEl) && gcdEl.ValueKind == System.Text.Json.JsonValueKind.String)
                        date = gcdEl.GetString() ?? "";
                    if (date.Length == 0 && cEl.TryGetProperty("authored_date", out var gadEl) && gadEl.ValueKind == System.Text.Json.JsonValueKind.String)
                        date = gadEl.GetString() ?? "";
                    if (cEl.TryGetProperty("author", out var aEl) && aEl.ValueKind == System.Text.Json.JsonValueKind.Object)
                        ReadAuthorAndDate(cEl, ref date, ref author);
                    //  · gitlab 的作者名是平铺的字符串 commit.author_name（这一层没有 author 对象）
                    if (author.Length == 0 && cEl.TryGetProperty("author_name", out var ganEl) && ganEl.ValueKind == System.Text.Json.JsonValueKind.String)
                        author = (ganEl.GetString() ?? "").Trim();
                }
            }

            // ③ bitbucket 与 gitlab 的直挂字段
            if (date.Length == 0 && root.TryGetProperty("date", out var bd) && bd.ValueKind == System.Text.Json.JsonValueKind.String)
                date = bd.GetString() ?? "";
            if (date.Length == 0 && root.TryGetProperty("committed_date", out var gd) && gd.ValueKind == System.Text.Json.JsonValueKind.String)
                date = gd.GetString() ?? "";
            // gitlab 的 /repository/commits/<sha>：作者名同样平铺在根上（author_name，没有 author 对象）
            if (author.Length == 0 && root.TryGetProperty("author_name", out var ranEl) && ranEl.ValueKind == System.Text.Json.JsonValueKind.String)
                author = (ranEl.GetString() ?? "").Trim();

            // ④ bitbucket 的 /refs/branches/<名>：提交不在 commit 层，而是挂在 target 下 ——
            //    2026-09-18 实测原文（api.bitbucket.org，只读抓取）：
            //      {"name": "master", "target": {"type": "commit", "hash": "7294e77a59cb69116628ed01ecab7748d1110b06",
            //       "date": "2026-09-14T06:49:27+00:00", "author": {"type": "author", "raw": "… <…@bots.bitbucket.org>", …}}, …}
            //    所以 原先这份报文里既没有 commit.sha 也没有根上的 hash/date，即分支型 ref 永远读不出提交号（判 Unknown）。
            //    `target` 这个键只出现在 bitbucket 的报文里（gitee/github/gitlab 实测都没有），且下面每一支
            //    都只在"还没读到"时才填，即别家零影响。
            if (root.TryGetProperty("target", out var tEl) && tEl.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                if (sha.Length == 0 && tEl.TryGetProperty("hash", out var thEl) && thEl.ValueKind == System.Text.Json.JsonValueKind.String)
                    sha = thEl.GetString() ?? "";
                if (date.Length == 0 && tEl.TryGetProperty("date", out var tdEl) && tdEl.ValueKind == System.Text.Json.JsonValueKind.String)
                    date = tdEl.GetString() ?? "";
                if (author.Length == 0 && tEl.TryGetProperty("author", out var taEl) && taEl.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    // bitbucket 的作者是 {type, raw:"名字 <邮箱>", user:{display_name}} —— 不是 gitee/github 的
                    // {name, date}，即不能交给 ReadAuthorAndDate（那是别家的形状），按 bitbucket 自己的形状读。
                    if (taEl.TryGetProperty("user", out var tuEl) && tuEl.ValueKind == System.Text.Json.JsonValueKind.Object
                        && tuEl.TryGetProperty("display_name", out var tdnEl) && tdnEl.ValueKind == System.Text.Json.JsonValueKind.String)
                        author = (tdnEl.GetString() ?? "").Trim();
                    if (author.Length == 0 && taEl.TryGetProperty("raw", out var trawEl) && trawEl.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        string raw = (trawEl.GetString() ?? "").Trim();
                        int lt = raw.IndexOf('<');
                        author = (lt > 0 ? raw.Substring(0, lt) : raw).Trim();
                    }
                }
            }

            if (date.Length >= 10) date = date.Substring(0, 10);
            return (sha, date, author);
        }
        catch { return ("", "", ""); }
    }

    /// <summary>从 <c>{author:{name,date}}</c> 这一层读作者名与日期（先 author、后 committer，与既有 ParseCommitJson 同序）。</summary>
    private static void ReadAuthorAndDate(System.Text.Json.JsonElement holder, ref string date, ref string author)
    {
        foreach (string who in new[] { "author", "committer" })
        {
            if (!holder.TryGetProperty(who, out var w) || w.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
            if (author.Length == 0 && w.TryGetProperty("name", out var nEl) && nEl.ValueKind == System.Text.Json.JsonValueKind.String)
                author = nEl.GetString() ?? "";
            if (date.Length == 0 && w.TryGetProperty("date", out var dEl) && dEl.ValueKind == System.Text.Json.JsonValueKind.String)
                date = dEl.GetString() ?? "";
            if (author.Length > 0 && date.Length > 0) return;
        }
    }

    /// <summary>
    /// 标签列表响应，即指定名字那个标签指向的提交号（纯函数）。实测形状（2026-09-18 直连只读抓公开接口原文）：
    ///   gitee  <c>[{"name":"nightly-20260905","commit":{"sha":"40363f6a…","date":"…"},…},…]</c>
    ///   github <c>[{"name":"v1.0","commit":{"sha":"…"},…},…]</c>
    ///   gitlab <c>[{"name":"v19.4.0","target":"9ebc27ed…","commit":{"id":"ac11717a…",…}},…]</c>
    ///          注意：<c>target</c> 是字符串提交号；<c>commit.id</c> 才是提交对象。根是数组（不是 <c>{"values":[…]}</c>）。
    ///   bitbucket <c>{"values":[{"name":"…","date":"…(标签自己的日期)","target":{"type":"commit","hash":"427be59d…","date":"…(提交日期)"}}]}</c>
    ///          注意：<c>target</c> 是对象（与 gitlab 的字符串 <c>target</c> 不同形）、提交号在 <c>target.hash</c>。
    /// 名字按忽略大小写匹配（git 的 ref 名本身大小写敏感，但这里只用来判定"是不是标签"，
    /// 同名的不同大小写写法在两家站点上都解析到同一个 ref，保守按同一个处理）；
    /// 注意：bitbucket 的 <c>target.hash</c> 只在 <c>target.type</c> 缺失或 <c>== "commit"</c> 时才认
    ///   （2026-09-19 加的保守闸门：注解标签的 <c>target.hash</c> 是标签对象的 hash、不是提交号；
    ///   本轮实测 6 个 bitbucket 仓库、1453 个标签全是 <c>type=="commit"</c>、注解标签未实测到
    ///   所以 不写猜测代码，认不出就返回空串，让上层判"说不清"而不是报假"有更新"）。
    /// 认不出来返回空串，绝不抛。
    ///
    /// 注意：层次别串门：这一层的 <c>date</c> 是标签对象自己的日期（实测 bitbucket 第一条：
    ///   标签 <c>date</c>=<c>06:37:45</c>、<c>target.date</c>=<c>06:37:44</c>，差一秒），
    ///   <c>target.date</c> 才是提交日期；本函数只取提交号、不取日期（日期由
    ///   <see cref="ParseCommitDetailJson"/> 另发一次请求补齐），即绝不把标签日期当提交日期用。
    /// </summary>
    internal static string FindShaByName(string? json, string? name)
    {
        try
        {
            string want = (name ?? "").Trim();
            if (want.Length == 0 || string.IsNullOrWhiteSpace(json)) return "";
            using var doc = System.Text.Json.JsonDocument.Parse(json!);
            var root = doc.RootElement;
            System.Text.Json.JsonElement arr;
            if (root.ValueKind == System.Text.Json.JsonValueKind.Array) arr = root;
            else if (root.ValueKind == System.Text.Json.JsonValueKind.Object
                     && root.TryGetProperty("values", out var vals) && vals.ValueKind == System.Text.Json.JsonValueKind.Array) arr = vals;   // bitbucket
            else return "";

            foreach (var e in arr.EnumerateArray())
            {
                if (e.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                if (!e.TryGetProperty("name", out var nEl) || nEl.ValueKind != System.Text.Json.JsonValueKind.String) continue;
                if (!string.Equals((nEl.GetString() ?? "").Trim(), want, StringComparison.OrdinalIgnoreCase)) continue;
                //  · 这里必须把 ValueKind 判断留在同一个 if 里（2026-09-19 修的真缺陷）：
                //   `commit` 属性不存在时 `TryGetProperty` 会让 `cEl` 停在 `default(JsonElement)`
                //   （实测 ValueKind == Undefined），而对非 Object 的 JsonElement 调 TryGetProperty 会抛
                //   InvalidOperationException（实测原文："Operation is not valid due to the current state of the object."，
                //   见汇报里的 %TEMP% 真跑）。bitbucket 的标签项本来就没有 commit 这一层，
                //   所以下面那句原来会抛，即被本方法末尾的 catch 吞成空串，即后面那条 target.hash 分支永远不可达（死代码）。
                //   写法与 ParseCommitDetailJson 的同款保持一致（那处的 commit 分支整段包在 ValueKind == Object 里）。
                if (e.TryGetProperty("commit", out var cEl) && cEl.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    if (cEl.TryGetProperty("sha", out var sEl) && sEl.ValueKind == System.Text.Json.JsonValueKind.String)
                        return sEl.GetString() ?? "";
                    //  · gitlab 的标签列表项里提交对象用的是 commit.id（实测原文：
                    //   {"name":"v19.4.0","target":"9ebc27ed…","commit":{"id":"ac11717a…","short_id":"ac11717a",…}}）。
                    //   与 ParseCommitDetailJson 的 commit.id 同一判据（那支服务分支/提交详情入口，这支服务标签列表项）；
                    //   只在 commit.sha 没读到（或读出空串）时才看它，即 gitee/github 的解析逐字未变。
                    if (cEl.TryGetProperty("id", out var cidEl) && cidEl.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        string idSha = (cidEl.GetString() ?? "").Trim();
                        if (idSha.Length > 0) return idSha;
                    }
                }
                if (e.TryGetProperty("target", out var tEl) && tEl.ValueKind == System.Text.Json.JsonValueKind.String)
                    return tEl.GetString() ?? "";                      // gitlab 的 target 是提交号
                //  · bitbucket 的标签列表项里 target 是对象（实测原文：
                //   {"values":[{"name":"0.0.0-…","date":"2016-06-15T06:37:45+00:00",
                //     "target":{"type":"commit","hash":"427be59da7b7323591364a1efe3bb681fb3cd4b9",
                //               "date":"2016-06-15T06:37:44+00:00",…}}]}）
                //   —— 提交号在 target.hash；target 这一层没有 name/commit.sha 的形状，
                //   补上之前这个标签永远读不出提交号，即列表读得懂却没名字，即被判 Missing（"远端已无这个分支或标签"），
                //   而标签其实存在 —— 那是拿我们的判据失败去栽赃远端（比 Unknown 更误导）。判据与
                //   ParseCommitDetailJson 的 target.hash 保持一致：都是"target 是对象 ⇒ 提交号在 target.hash"。
                //   注意：这里只取提交号：target.date 是提交日期、上面那个 date 是标签日期，两者都不在这里读（层次别串门）。
                //   位置守卫：写在既有两支之后，即 gitee（commit.sha）/gitlab（字符串 target）的形状一个字节都没变。
                if (e.TryGetProperty("target", out var bTgtEl) && bTgtEl.ValueKind == System.Text.Json.JsonValueKind.Object
                    && bTgtEl.TryGetProperty("hash", out var bHashEl) && bHashEl.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    //  ·· 注解标签（annotated tag）的保守闸门（2026-09-19 补）：
                    //   git 语义上，注解标签的 target 是标签对象、不是提交
                    //   （形状应为 {"target":{"type":"tag","hash":<标签对象hash>,"target":{"type":"commit","hash":<提交号>}}}），
                    //   此时 target.hash 不是提交号，即拿去跟锁文件里的提交比必然不等，即可能报出假"有更新"
                    //   （假"有更新"比"说不清"坏得多：会推着用户去装一个根本没变的版本）。
                    //   本项目"以实测为准、不许猜"：本轮抓了 6 个 bitbucket 仓库的标签列表（atlassian/aui 全 1352 个标签
                    //   分 14 页翻完 + atlassian-connect-spring-boot 101 个）一条 target.type=="tag" 都没实测到
                    //   所以 形状未确证，即不写猜测代码（不去读 target.target.hash），只按"宁可少认、不猜"加这道闸门。
                    //   · 2026-09-19 第二轮实测（换仓库 + 单资源接口，web_fetch 只读）把这件事摸到底了：
                    //     atlassian/aui 里确实有注解标签，而 bitbucket 在 /refs/tags 里已经把它解引用到提交了 ——
                    //     实测原文 `GET …/refs/tags/0.0.0-do-not-use`（HTTP 200）：
                    //       {"name":"0.0.0-do-not-use","type":"tag","message":"Release 0.0.0-do-not-use.\n",
                    //        "tagger":{"type":"author","raw":"bambooagent <…>"},
                    //        "target":{"type":"commit","hash":"3c2a48ec918a7da8f73f5006c9f3e1b37c7a63cd","date":"…",
                    //                  "author":{…},"message":"0.0.0-do-not-use\n","parents":[{"hash":"527f7ea2…"}],…}}
                    //     —— `tagger` 与标签自己的 message 都非空，即这是注解标签（轻量标签两者皆 null：同仓库
                    //        `0.0.0-8-0-0-SNAPSHOT-006-do-not-use` 实测 `"message":null,"tagger":null` 可对照）；
                    //        而它的 `target.type` 仍是 `"commit"`、`target.hash` 指着提交（该 target 带 `parents`、
                    //        links 也全指向 /commit/&lt;hash&gt;，即是提交对象，不是标签对象）。
                    //   所以 结论：保持这道保守闸门，且不去读 target.target.hash —— 那个形状在 bitbucket 上
                    //     至今一条都没实测到（本轮又加了 100 条样本 + 2 个单资源样本，全是 type=="commit"），
                    //     而 bitbucket 自己会把注解标签解引用成提交，即我们读 target.hash 拿到的就是提交号，
                    //     既不会假"有更新"，也不会漏判。将来若真在别处抓到 type=="tag" 的报文，再按实测支持 —— 绝不先写猜测代码。
                    //
                    //   判据：只有 type 缺失（字段形状没变）或 type=="commit"（实测形状）时才认 target.hash；
                    //   其余（含 type=="tag"），即返回空串（失败关闭），即上层判 Unknown/[ERROR]（"说不清"），
                    //   绝不谎报"远端已无这个 ref"、更不会报出假"有更新"（见 TagListContainsName 与 FetchRefKindAsync 的配套判定）。
                    //   注意：层次别串门：本函数只取提交号，target.date / 该层的 date 一概不在这里读。
                    bool bTypeUsable = !bTgtEl.TryGetProperty("type", out var bTypeEl)
                        || bTypeEl.ValueKind != System.Text.Json.JsonValueKind.String
                        || string.Equals((bTypeEl.GetString() ?? "").Trim(), "commit", StringComparison.Ordinal);
                    if (bTypeUsable) return bHashEl.GetString() ?? "";
                    return "";   // 注解标签（或我没实测过的 type），即认不出来就返回空串，绝不猜
                }
            }
            return "";
        }
        catch { return ""; }
    }

    /// <summary>给界面看的一行说明。</summary>
    public static string Describe(string? spec)
    {
        var k = Classify(spec);
        return k switch
        {
            Kind.Registry => "npm 包（可按最新版本更新）",
            Kind.GitCommit => "git 源（钉在某个提交，需要跟到默认分支最新）",
            Kind.GitRef => "git 源（跟分支或标签）",
            Kind.GitBare => "git 源（跟默认分支）",
            _ => "来源不认识（不会改动）"
        };
    }

    /// <summary>
    /// 更新这条依赖该用的参数（接在 `npx dsh plugin --profile web add` 后面）。
    /// Registry 走"@最新版"，git 源按各自方式重解析；认不出返回空串（调用方不许动手）。
    /// </summary>
    public static string RefreshSpec(string? name, string? spec)
    {
        var k = Classify(spec);
        return k switch
        {
            Kind.Registry => "",
            Kind.GitCommit => RepoWithoutRef(spec),
            Kind.GitRef => (name ?? "").Trim(),
            Kind.GitBare => (name ?? "").Trim(),
            _ => ""
        };
    }

    /// <summary>
    /// 从 pnpm-lock.yaml 里读某个包当前解析到的 git commit（无法读取时返回空串）。
    /// 锁文件里形如：`resolution: {commit: 3f2a…}`，紧跟在包名条目之后。
    /// </summary>
    public static string LockedCommit(string? lockText, string? packageName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(lockText) || string.IsNullOrWhiteSpace(packageName)) return "";
            var lines = lockText!.Split('\n');
            string want = packageName!.Trim();
            for (int i = 0; i < lines.Length; i++)
            {
                string t = lines[i].Trim();
                // 包名在锁文件里可能带引号、也可能带 (github:…) 这样的括号后缀
                if (!t.StartsWith(want, StringComparison.OrdinalIgnoreCase)) continue;
                if (t.Length > want.Length && t[want.Length] != ':' && t[want.Length] != '@' && t[want.Length] != ' ') continue;

                for (int j = i + 1; j < Math.Min(i + 12, lines.Length); j++)
                {
                    var m = Regex.Match(lines[j], "commit:\\s*['\"]?([0-9a-fA-F]{7,40})");
                    if (m.Success) return m.Groups[1].Value;
                    // github: / git+ 源在锁文件里也常写成 tarball 地址，末尾那串就是提交
                    var tb = Regex.Match(lines[j], "tarball:\\s*\\S*?([0-9a-fA-F]{40})");
                    if (tb.Success) return tb.Groups[1].Value;
                }
            }
            return "";
        }
        catch { return ""; }
    }

    /// <summary>
    /// 从 git 源说明符里解析出（owner, repo）（认不出来返回空）。
    ///
    /// 支持多家托管站（<see cref="GitHosts"/>：github / gitee / gitlab / bitbucket），写法：
    /// <c>github:o/r#ref</c>（只认 github 的简写，别家没有这种 npm 简写）、
    /// <c>git+https://host/o/r.git</c>、<c>https://host/o/r</c>、<c>git://host/o/r</c>、
    /// <c>ssh://git@host/o/r.git</c>、<c>git@host:o/r.git</c>；<c>#ref</c> 与结尾 <c>.git</c> 都去掉。
    ///
    /// 为什么加 gitee（2026 现场缺陷）：这里原先只认 <c>github\.com</c>，
    /// 而现场 <c>dsh-codearts-auth</c> 声明的是 <c>git+https://gitee.com/…</c>，即解析成空 ->
    /// 上层的"远端提交变没变"永远拿不到远端，即永远提示有更新。
    ///
    /// 形状规则与 <c>PluginManager.ParseRepoSpec</c>（展示用的"任意托管站"解析）同一套；
    /// 但这里只认白名单站点：查提交要真去那个站点发请求，不能凭一个陌生域名就去连。
    /// 本类是下层，不反向依赖 PluginManager（会成环），故在此镜像一份（两处都有断言钉着）。
    /// </summary>
    public static (string Owner, string Repo) ParseGitRepo(string? spec)
    {
        string s = (spec ?? "").Trim();
        if (s.Length == 0) return ("", "");
        s = RepoWithoutRef(s);
        if (s.StartsWith("git+", StringComparison.OrdinalIgnoreCase)) s = s.Substring(4);

        // github 的 npm 简写（旧行为一字未改）
        if (s.StartsWith("github:", StringComparison.OrdinalIgnoreCase))
            return SplitOwnerRepo(s.Substring("github:".Length));
        try
        {
            var (host, path) = SplitHostPath(s);
            if (host.Length == 0 || Array.IndexOf(GitHosts, host) < 0) return ("", "");
            return SplitOwnerRepo(path);
        }
        catch { return ("", ""); }
    }

    /// <summary>允许查提交的托管站（闭合白名单；与 <c>PluginManager.AllowedGitHosts</c> 同口径）。</summary>
    private static readonly string[] GitHosts = { "github.com", "gitee.com", "gitlab.com", "bitbucket.org" };

    /// <summary>短写前缀，即真实站点；认不出来的原样返回（交给后面统一小写）。</summary>
    private static string HostAlias(string shortName) => (shortName ?? "").Trim().ToLowerInvariant() switch
    {
        "github" => "github.com",
        "gitlab" => "gitlab.com",
        "bitbucket" => "bitbucket.org",
        "gitee" => "gitee.com",
        _ => (shortName ?? "").Trim().ToLowerInvariant()
    };

    /// <summary>站点名小写；路径去首尾斜杠、去结尾 .git；不足 owner/repo 两段就不认。</summary>
    private static (string Host, string Path) NormalizeHostPath(string host, string path)
    {
        host = (host ?? "").Trim().ToLowerInvariant();
        path = (path ?? "").Trim().Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path.Substring(0, path.Length - 4);
        path = path.TrimEnd('/');
        if (host.Length == 0 || path.Length == 0) return ("", "");
        return path.Contains('/') ? (host, path) : ("", "");
    }

    /// <summary>
    /// 把一份已去掉 #ref 与 git+ 前缀的声明拆成（站点, 仓库路径）。
    /// 形状规则逐条对应 <c>PluginManager.ParseRepoSpec</c>（那边多一层 git+ 剥离与 #ref 剥离）。
    /// </summary>
    private static (string Host, string Path) SplitHostPath(string s)
    {
        if (Regex.IsMatch(s, @"^(file|link|portal|workspace|npm):", RegexOptions.IgnoreCase)) return ("", "");

        var shortForm = Regex.Match(s, @"^(github|gitlab|bitbucket|gitee):(.+)$", RegexOptions.IgnoreCase);
        if (shortForm.Success) return NormalizeHostPath(HostAlias(shortForm.Groups[1].Value), shortForm.Groups[2].Value);

        var ssh = Regex.Match(s, @"^ssh://(?:[^@/]+@)?([^/:]+)(?::\d+)?/(.+)$", RegexOptions.IgnoreCase);
        if (ssh.Success) return NormalizeHostPath(ssh.Groups[1].Value, ssh.Groups[2].Value);

        // git@host:o/r.git（scp 写法）：`(?!//)` 防 https:// 被抢成 host="https"；
        // host 必须带点，否则 Windows 盘符路径 D:/foo/bar 会被当成 host="D"。
        var scp = Regex.Match(s, @"^(?:[^@/]+@)?([A-Za-z0-9._-]*\.[A-Za-z0-9._-]+):(?!//)(.+)$");
        if (scp.Success) return NormalizeHostPath(scp.Groups[1].Value, scp.Groups[2].Value);

        var url = Regex.Match(s, @"^(?:https?|git)://(?:[^@/]+@)?([^/:]+)(?::\d+)?/(.+)$", RegexOptions.IgnoreCase);
        if (url.Success) return NormalizeHostPath(url.Groups[1].Value, url.Groups[2].Value);

        return ("", "");
    }

    /// <summary>仓库路径的前两段就是 owner/repo（后两段的路径忽略，与旧正则 <c>[^/]+/[^/#]+</c> 同结论）。</summary>
    private static (string Owner, string Repo) SplitOwnerRepo(string path)
    {
        string p = (path ?? "").Trim().Trim('/');
        if (p.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) p = p.Substring(0, p.Length - 4);
        p = p.TrimEnd('/');
        var parts = p.Split('/');
        return parts.Length >= 2 ? (parts[0], parts[1]) : ("", "");
    }

    /// <summary>
    /// 仓库主页（供界面上的链接用；认不出来返回空）。站点跟着声明走（gitee 的就去 gitee，不再一律拼 github）。
    /// 站点仍受 <see cref="ParseGitRepo"/> 的白名单约束 —— 界面上的可点链接不因"支持多站点"而放开到任意域名。
    /// </summary>
    public static string RepoUrl(string? spec)
    {
        var (owner, repo) = ParseGitRepo(spec);
        if (owner.Length == 0 || repo.Length == 0) return "";
        var (host, _) = HostPathOf(spec);
        return host.Length > 0 ? $"https://{host}/{owner}/{repo}" : "";
    }

    /// <summary>
    /// 把一份声明拆成（站点, 仓库路径）：去掉 <c>#ref</c> 与 <c>git+</c> 前缀后走 <see cref="SplitHostPath"/>。
    /// <c>git://</c> 不剥（它是协议头，由 SplitHostPath 的正则匹配）。
    /// </summary>
    private static (string Host, string Path) HostPathOf(string? spec)
    {
        string s = RepoWithoutRef((spec ?? "").Trim());
        if (s.StartsWith("git+", StringComparison.OrdinalIgnoreCase)) s = s.Substring(4);
        return SplitHostPath(s);
    }

    /// <summary>
    /// 一次把「该跟谁比、比出来是什么」全部查清（调用方只需这一个入口）。
    ///
    /// 返回：
    ///   · <c>RefKind</c>    —— 用户声明的 <c>#ref</c> 形态（默认分支 / 钉死提交 / 具名 ref / 非法）；
    ///   · <c>Match</c>      —— 具名 ref 时远端判定（分支 / 标签 / 不存在 / 问不出来）；其余形态为 <c>Unknown</c>；
    ///   · <c>Version/Date/ShortSha/Author</c> —— 该 ref 当前指向提交的版本号（取自 package.json）、日期、短号、作者；
    ///   · <c>Confidence</c> —— 这次到底查没查到（判定"有没有新版"必须先看它，见 <see cref="DecideUpdate"/>）；
    ///   · <c>Decision</c>   —— 已经按 ref 类型分流好的最终判定（Hard / Advisory / UpToDate / Unknown）。
    ///
    /// 分流要点（用户 2026-09-18 要求）：
    ///   · 无 ref，即查默认分支最新提交（"master 肯定要报"）；
    ///   · <c>#&lt;sha&gt;</c>，即钉死，不去查远端、永不报（提交不可变）；
    ///   · <c>#&lt;名字&gt;</c>，即先查该分支，再查标签列表（见 <see cref="FetchRefKindAsync"/>）：
    ///                          是分支，即报"可选升级"；是标签，即默认不报，被移动才报；问不出来，即失败关闭。
    /// 纯网络操作，失败一律返回 Unknown/NotQueryable，绝不抛。
    ///
    /// 注意：调用方必须先过形态闸门（本条与安装侧同一条「失败关闭」原则，2026-09-18 补）：
    ///   本方法只看 <see cref="ClassifyRef"/> 的形态，不做"这个形态装不装得上"的判断 ——
    ///   而安装侧的白名单 <c>PluginManager.IsValidGitSource</c> 会拒绝 <c>git://…</c>（明文协议）、
    ///   <c>file:</c>、<c>http://</c>、scp 形态等；对它们本方法仍会照常查远端、有差异就报
    ///   <see cref="UpdateDecision.HardNewCommit"/>，即卡片给按钮、点了却无命令可执行（现场缺陷）。
    ///   故调用侧（<c>MainWindow.CheckPluginUpdatesAsync</c> 的 <c>CanInstallPluginSource</c>）在调本方法
    ///   之前就问一次那份白名单，不接受，即直接记"待确认"、不调本方法。
    ///   判据只有那一份（本类是下层，反向调 <c>PluginManager</c> 会成环）—— 别在此镜像复制。
    /// </summary>
    /// <param name="spec">清单里声明的来源（含可能的 <c>#ref</c>）。</param>
    /// <param name="lockedCommit">
    /// pnpm-lock.yaml 里这条依赖当前解析到的提交（<see cref="LockedCommit"/> 读出来的那个）。
    /// 必须传，否则一律判成"有新版"：<see cref="DecideUpdate"/> 在锁文件侧为空时会报
    /// <see cref="UpdateDecision.HardNewCommit"/>（"跟不了 ⇒ 当作有更新"的既有口径）——
    /// 那正是本函数第一版犯过的错（漏传，即永远提示有新提交），别改回去。
    /// </param>
    /// <remarks>
    ///  · 2026-09-19：要"这次为什么没查成"的那一位（<see cref="RefProbeFailure"/>）请用
    ///   <see cref="FetchRefAwareLatestDetailedAsync"/> —— 本方法是老签名，把它丢掉。
    ///   保持老签名是为了老调用点零改动（同 <see cref="FetchRepoLatestAsync"/> 的口径）。
    /// </remarks>
    public static async Task<(RefKind RefKind, RefMatchKind Match, string Version, string Date, string ShortSha,
                              string Author, CommitConfidence Confidence, UpdateDecision Decision)>
        FetchRefAwareLatestAsync(string? spec, string? lockedCommit)
    {
        var (rk, m, v, d, s, a, c, dec, _) = await FetchRefAwareLatestDetailedAsync(spec, lockedCommit);
        return (rk, m, v, d, s, a, c, dec);
    }

    /// <summary>
    /// 与 <see cref="FetchRefAwareLatestAsync"/> 完全同一套判定，只多带一位
    /// <see cref="RefProbeFailure"/>：「这次为什么没问出来」的类别（查成了 / 远端给了答案时为
    /// <see cref="RefProbeFailure.None"/>）。
    ///
    /// 为什么加它（用户 2026-09-19 实测）：卡片"待确认"时，悬停原来只有一句
    /// 「站点不支持或没登录、仓库私有、当时网络不通，都可能是原因」——诚实但不指名；
    /// 而本壳在这次调用里已经知道是哪一种（见 <see cref="RefProbeFailure"/> 的映射表），只是没往上传。
    ///
    /// 两条路都填：
    ///   · 具名 ref（<c>#名字</c>），即由 <see cref="FetchRefKindAsync"/> 的 <see cref="RefProbe.Failure"/> 带上来；
    ///   · 默认分支（没写 <c>#ref</c> 的 git 源插件走的就是它），即由
    ///     <see cref="FetchRepoLatestDetailedAsync"/> 用同一个分类器现分一次。
    ///   · 2026-09-19 起两条路的日志口径也一致：网络类只记 <see cref="Logger.NoteDiagnosis"/>（[WARN]），
    ///     <see cref="RefProbeFailure.Shape"/> 仍落 [ERROR]（理由见 <see cref="FetchRepoLatestDetailedAsync"/>）。
    ///     本方法只是把类别转上来，不参与"记什么账"。
    ///
    /// 注意：失败关闭与这一位无关：任何类别都不会让 <c>Decision</c> 变成"有新版"
    ///   （判定仍只有 <see cref="DecideUpdate"/> 一处说了算；本方法只是多回一个诊断位）。
    /// </summary>
    public static async Task<(RefKind RefKind, RefMatchKind Match, string Version, string Date, string ShortSha,
                              string Author, CommitConfidence Confidence, UpdateDecision Decision,
                              RefProbeFailure Failure)>
        FetchRefAwareLatestDetailedAsync(string? spec, string? lockedCommit)
    {
        var refKind = ClassifyRef(spec);

        // 钉死提交：提交不可变，远端就是锁文件里那个（或用户自己换过 ref），即不查也不报。
        if (refKind == RefKind.Commit)
            return (refKind, RefMatchKind.Unknown, "", "", ShortSha(RefPart(spec)), "", CommitConfidence.Queried,
                    UpdateDecision.UpToDate, RefProbeFailure.None);

        // ref 写法不合规范：不查不判（失败关闭）。
        if (refKind == RefKind.Invalid)
            return (refKind, RefMatchKind.Unknown, "", "", "", "", CommitConfidence.NotQueryable,
                    UpdateDecision.Unknown, RefProbeFailure.None);

        try
        {
            if (refKind == RefKind.DefaultBranch)
            {
                //  · 默认分支这条路（没写 #ref 的 git 源插件走的就是它）用带类别的那支：
                //   该分支内部自己分诊（网络类 [WARN] / Shape 仍 [ERROR]），类别随返回值带上来，
                //   同一个分类器现分一次，即判据仍只有一份（见 ClassifyRefProbeFailure 与那边的 catch）。
                var (v, d, s, _, a, c, f) = await FetchRepoLatestDetailedAsync(spec);
                return (refKind, RefMatchKind.Unknown, v, d, s, a, c,
                        DecideUpdate(lockedCommit, s, c, refKind, RefMatchKind.Unknown), f);
            }

            // 具名 ref：先判它到底是分支还是标签，同时把该 ref 的提交取回来。
            string name = RefPart(spec).Trim();
            var probe = await FetchRefKindAsync(spec, name);
            if (!LooksLikeSha(probe.Sha))
                return (refKind, probe.Kind, "", "", "", "", CommitConfidence.NotQueryable,
                        DecideUpdate(lockedCommit, "", CommitConfidence.NotQueryable, refKind, probe.Kind),
                        probe.Failure);

            // 版本号仍取自该提交的 package.json（查不到只是不显示，不影响判定）。
            string version = await FetchPackageVersionAsync(spec, probe.Sha);
            return (refKind, probe.Kind, version, probe.Date, ShortSha(probe.Sha), probe.Author,
                    CommitConfidence.Queried,
                    DecideUpdate(lockedCommit, probe.Sha, CommitConfidence.Queried, refKind, probe.Kind),
                    probe.Failure);
        }
        catch (Exception ex)
        {
            // 日志标签保持原名：老日志的检索口径不变（本方法就是那个入口的实现体，只是多回一位类别）。
            Logger.LogError("PluginSource.FetchRefAwareLatestAsync", ex);
            // 走到这里说明是本壳这侧抛了（网络调用都在各自内部吞掉了），即 Shape（[ERROR] 已落）。
            return (refKind, RefMatchKind.Unknown, "", "", "", "", CommitConfidence.NotQueryable,
                    UpdateDecision.Unknown, RefProbeFailure.Shape);
        }
    }

    /// <summary>某个提交上的 package.json 里声明的 version（取不到返回空串；纯网络，绝不抛）。</summary>
    private static async Task<string> FetchPackageVersionAsync(string? spec, string sha)
    {
        try
        {
            var (owner, repo) = ParseGitRepo(spec);
            if (owner.Length == 0 || !LooksLikeSha(sha)) return "";
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("DSHGuard");
            var (host, path) = HostPathOf(spec);
            string pkg = await http.GetStringAsync(PluginRawFileUrl(host, path, owner, repo, sha, "package.json"));
            using var pd = System.Text.Json.JsonDocument.Parse(pkg);
            return pd.RootElement.TryGetProperty("version", out var vEl) ? (vEl.GetString() ?? "") : "";
        }
        catch { return ""; }
    }

    /// <summary>
    /// 查仓库默认分支最新提交：返回 (版本号, 时间, 短提交, 说明, 作者, 可信度)。
    /// 版本号取自该提交的 package.json（git 源没有 npm 版本，这样才有"版本（时间）"可显示）；
    /// 取不到就退回短提交号。纯网络操作，失败一律返回空串，绝不抛。
    ///
    ///  · 与 <see cref="FetchRepoLatestAsync"/> 的区别：这里不吞掉"有没有真的查到"。
    ///   调用方判定"远端提交变没变"之前必须先看 <see cref="CommitConfidence"/> ——
    ///   「查不到」不等于「有更新」（现场：gitee 源解析不出仓库，即永远提示更新）。
    /// 按 ref 类型分流请用 <see cref="FetchRefAwareLatestAsync"/>（它内部按 <c>#ref</c> 形态分流；
    /// 默认分支那一支走 <see cref="FetchRepoLatestDetailedAsync"/>，与这里是同一套判定）。
    /// </summary>
    public static async Task<(string Version, string Date, string ShortSha, string Note, string Author, CommitConfidence Confidence)>
        FetchRepoLatestCheckedAsync(string? spec)
    {
        //  · 老签名保持不变（老调用点与自检样本零改动）：它就是下面那个带失败类别的版本，只是把类别丢掉。
        //   只要"查到没查到"的调用方照旧用这个；要说清"为什么没查到"的用 FetchRepoLatestDetailedAsync。
        var (v, d, s, n, a, c, _) = await FetchRepoLatestDetailedAsync(spec);
        return (v, d, s, n, a, c);
    }

    /// <summary>
    /// 与 <see cref="FetchRepoLatestCheckedAsync"/> 完全同一套判定，只多带一位 <see cref="RefProbeFailure"/>
    /// （"这次为什么没查到"的类别；查到了为 <see cref="RefProbeFailure.None"/>）。
    ///
    /// 加它的理由与 <see cref="FetchRefAwareLatestDetailedAsync"/> 同：卡片"待确认"时要说清是哪一个原因
    /// （现场：gitee 源插件实测 403，而悬停只给一句"都可能是原因"）。
    /// 注意：这条路原来把异常直接落 [ERROR] 后吞掉、从没分诊过，故这里用同一个分类器
    ///   <see cref="ClassifyRefProbeFailure"/> 现分一次 —— 判据仍只有一份，不新增第二套。
    ///
    ///  · 2026-09-19 日志口径与 <see cref="FetchRefKindAsync"/> 那条路对齐（本轮修的口径不一致）：
    ///   · 网络类（<see cref="RefProbeFailure.RateLimited"/> / <see cref="RefProbeFailure.Unauthorized"/> /
    ///     <see cref="RefProbeFailure.Server"/> / <see cref="RefProbeFailure.Network"/>），即单行
    ///     <see cref="Logger.NoteDiagnosis"/>（[WARN]），不写 [ERROR]；
    ///   · <see cref="RefProbeFailure.Shape"/>（HTTP 通了却读不出东西 / 本壳判据问题），即仍 [ERROR]，不降噪。
    ///   注意：为什么这条要紧：现场那颗 <c>dsh-codearts-auth</c> 声明里没有 <c>#ref</c>，即走的正是本方法；
    ///     而 gitee 当时对它回 403，即原来每轮都落一条 [ERROR] ->
    ///     <see cref="Logger.HasFailureEvidence"/> 被点亮，即健康启动的日志再也不会被按"健康"清理。
    ///     判据本身（<see cref="ClassifyRefProbeFailure"/>）、枚举成员、失败关闭一字未动。
    ///
    /// 注意：返回值与签名未变（仍是那 7 位，类别位本来就带着），即调用点零改动、非破坏式；
    ///   本轮改的只有"记什么账"，改的正是同事上一轮刻意留给本轮的这处。
    /// </summary>
    internal static async Task<(string Version, string Date, string ShortSha, string Note, string Author,
                                CommitConfidence Confidence, RefProbeFailure Failure)>
        FetchRepoLatestDetailedAsync(string? spec)
    {
        //  · 失败类别的唯一出口（静态局部函数，不捕获任何状态）：本方法所有"问不出来"的返回都从这里造
        //   —— 与 <see cref="FetchRefKindAsync"/> 里那个 Unanswered 同形（那边名字已占，这里叫 Unqueried），
        //     所以 不可能出现"某一支忘了分类"（两条路的返回形状与出口一一对应）。
        static (string, string, string, string, string, CommitConfidence, RefProbeFailure)
            Unqueried(RefProbeFailure failure)
            => ("", "", "", "", "", CommitConfidence.NotQueryable, failure);

        var (owner, repo) = ParseGitRepo(spec);
        // 站点仓库都解析不出来，即压根没去问远端，即没有可归类的远端失败（None）。
        if (owner.Length == 0) return Unqueried(RefProbeFailure.None);
        // 站点与"问到哪一步"放在 try 外：兜底的 [WARN]/[ERROR] 文案要说清是哪个站点的哪个仓库、哪一步问不成。
        // （host 先给空串，取不到也不影响判定；HostPathOf 只做字符串切分。）
        string host = "";
        string step = "① 提交列表接口";
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("DSHGuard");

            //  · 站点跟着声明走；github 的 URL 与解析一字未改（旧行为），
            //   其余站点各自用公开 API（字段名以实测为准，见 ParseCommitJson 的注释与自检样本）。
            // 注意：写成对 try 外那个局部变量赋值（不是 `var (host, path) = …`）：host 要留给下面兜底的
            //   catch 用，若在这里再声明一个局部 host 就会把它遮住（CS0136，本项目踩过），即 host 在 try 外声明，
            //   这里只声明本方法内唯一的那个 path（它只在 try 里用，catch 不需要）。
            string path;
            (host, path) = HostPathOf(spec);
            string api = PluginApiUrl(host, path, owner, repo);
            string json = await http.GetStringAsync(api);
            // 走到这里说明真去查过（HTTP 通了），即之后即使解析不出提交号也属于"查过但没拿到"。
            var (sha, date) = ParseCommitJson(host, json);
            // 注意：这一支必须落 [ERROR]（与下面那个 catch 的网络类降噪不是一回事）：HTTP 通了却读不出
            //   提交号 = 站点改版 / 本壳判据过时，是本壳要修的东西，写成"你网络不好"才是真误导。
            //   （原来这里完全不记日志，返回 Queried + Shape 被上层吞掉，即本轮补上这行 [ERROR]：
            //    这一支本来就该是 [ERROR]，只是先前连 [ERROR] 都没有。置信度逐字保持 Queried 不变。）
            if (sha.Length == 0)
            {
                Logger.LogError("PluginSource.FetchRepoLatestDetailedAsync",
                    new InvalidDataException($"提交列表接口返回 200 但读不出提交号（host={host}，repo={owner}/{repo}）⇒ 判 Unknown"));
                return ("", "", "", "", "", CommitConfidence.Queried, RefProbeFailure.Shape);
            }

            string version = "";
            try
            {
                string pkg = await http.GetStringAsync(PluginRawFileUrl(host, path, owner, repo, sha, "package.json"));
                using var pd = System.Text.Json.JsonDocument.Parse(pkg);
                if (pd.RootElement.TryGetProperty("version", out var vEl)) version = vEl.GetString() ?? "";
            }
            catch { }
            // 作者：从同一次响应里取（不额外发请求 —— 提交列表响应已含 commit.author.name）。
            // 用户问过「gitee 是共同 contribute 的平台，应该读的是参与者们的提交吧？」，即把作者摆出来，
            // 顺带证明读的是该 ref 上的全部提交、不按作者过滤（gitee 与 github 的字段位置实测一致）。
            string author = ParseCommitAuthor(host, json);
            string shortSha = sha.Length >= 7 ? sha.Substring(0, 7) : sha;
            return (version, date, shortSha,
                    version.Length > 0 ? "仓库最新提交的版本号" : "仓库最新提交（该仓库未声明版本号）",
                    author,
                    CommitConfidence.Queried, RefProbeFailure.None);
        }
        catch (Exception ex) when (IsRefProbeNetworkNoise(ex))
        {
            //  · 用户网络 / 远端站点不可达 / 配额 / 未登录：不是本壳的错误，即单行中性诊断（[WARN]），
            //   不写 [ERROR]。与 FetchRefKindAsync 那条路同一个判据、同一个入口（照着那边抄的）：
            //   断网时 N 个 git 源插件会写 N 条 [ERROR]（每条还是 3 行：正文+栈+Inner），
            //   而 [ERROR] 会把 Logger.HasFailureEvidence 置真，即一次普通断网被记成"本次运行出过异常"。
            //   注意：现场那颗 dsh-codearts-auth 没有 #ref，即走的正是本方法，而 gitee 对它回 403 ->
            //     原来每轮都落一条 [ERROR]，即健康启动的日志再也不会被按"健康"清理（本轮修的就是这处）。
            Logger.NoteDiagnosis($"查仓库最新提交没能问成（{step}，host={host}，repo={owner}/{repo}）："
                               + $"{ex.GetType().Name}: {ex.Message} ⇒ 判 Unknown（卡片写「待确认」，不报更新）");
            // 类别：403/429 -> RateLimited、401 -> Unauthorized、5xx -> Server、网络类，即 Network
            //（与 IsRefProbeNetworkNoise 同一个分类器；本 catch 是被它筛进来的，即类别必不为 None）。
            return Unqueried(ClassifyRefProbeFailure(ex));
        }
        catch (Exception ex)
        {
            // 兜底：真错误（我们自己的判据/代码问题，例如漏了一个 404 分支、URI 拼错）仍落 [ERROR]，不降噪。
            Logger.LogError("PluginSource.FetchRepoLatestCheckedAsync", ex);
            // 类别：本壳这侧的问题，即 Shape（[ERROR] 已落，与上面那个 catch 的 [WARN] 是两条路，别混）。
            return Unqueried(RefProbeFailure.Shape);
        }
    }

    /// <summary>
    /// 提交列表响应，即第一条提交的作者名（纯函数；取不到返回空串）。
    /// 实测字段位置（web_fetch 只读抓取公开接口原文，2026-09-18）：
    ///   · gitee  <c>[{"sha":"…","author":null,"commit":{"author":{"name":"Jet","date":"…"}}}]</c>
    ///            注意：根上的 <c>author</c> 实测为 <c>null</c>，真正有名字的是 <c>commit.author.name</c>
    ///   · github <c>[{"sha":"…","commit":{"author":{"name":"xiaosurongjia","date":"…"}}}]</c>（同形）
    ///   · gitlab <c>[{"id":"…","author_name":"…"}]</c>；bitbucket <c>{"values":[{"author":{"display_name":"…"}}]}</c>
    /// 一律不按作者过滤：本壳读的就是该 ref 上的全部提交，谁推的都算。
    /// </summary>
    internal static string ParseCommitAuthor(string? host, string? json)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json)) return "";
            using var doc = System.Text.Json.JsonDocument.Parse(json!);
            var root = doc.RootElement;
            System.Text.Json.JsonElement first;
            if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                if (root.GetArrayLength() == 0) return "";
                first = root[0];
            }
            else if (root.ValueKind == System.Text.Json.JsonValueKind.Object
                     && root.TryGetProperty("values", out var vals) && vals.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                if (vals.GetArrayLength() == 0) return "";
                first = vals[0];
            }
            else return "";

            if (first.ValueKind != System.Text.Json.JsonValueKind.Object) return "";

            // gitee / github：commit.author.name（提交者姓名；committer 兜底）
            if (first.TryGetProperty("commit", out var cEl) && cEl.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (string who in new[] { "author", "committer" })
                {
                    if (cEl.TryGetProperty(who, out var w) && w.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        string got = ReadName(w);
                        if (got.Length > 0) return got;
                    }
                }
            }
            // gitee 根上的 author 对象（部分接口 / 部分仓库会填）
            if (first.TryGetProperty("author", out var au) && au.ValueKind == System.Text.Json.JsonValueKind.Object)
                return ReadName(au);
            // bitbucket：author.display_name（是字符串，不是对象）
            if (first.TryGetProperty("author", out var bu) && bu.ValueKind == System.Text.Json.JsonValueKind.String)
                return (bu.GetString() ?? "").Trim();
            // gitlab：author_name / committer_name
            if (first.TryGetProperty("author_name", out var gn) && gn.ValueKind == System.Text.Json.JsonValueKind.String)
                return (gn.GetString() ?? "").Trim();
            if (first.TryGetProperty("committer_name", out var gcn) && gcn.ValueKind == System.Text.Json.JsonValueKind.String)
                return (gcn.GetString() ?? "").Trim();
            return "";
        }
        catch { return ""; }
    }

    /// <summary>从一个 <c>{…,"name":"…"}</c> 对象里读 name（取不到返回空串）。</summary>
    private static string ReadName(System.Text.Json.JsonElement obj)
        => obj.TryGetProperty("name", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.String
            ? (n.GetString() ?? "").Trim() : "";

    /// <summary>
    /// 查仓库默认分支最新提交：返回 (版本号, 时间, 短提交, 说明)。
    /// 旧签名保持不变（老调用点与自检样本零改动）：它就是上面那个带可信度的版本，
    /// 只是把可信度与作者丢掉 —— 只拿它显示的调用方可以这样用，
    /// 判定"有没有新版"的调用方必须用带可信度的那个。
    /// </summary>
    public static async Task<(string Version, string Date, string ShortSha, string Note)> FetchRepoLatestAsync(string? spec)
    {
        var (v, d, s, n, _, _) = await FetchRepoLatestCheckedAsync(spec);
        return (v, d, s, n);
    }

    /// <summary>提交列表接口（按托管站选择；github 与 PluginSource 旧版逐字一致）。</summary>
    private static string PluginApiUrl(string host, string path, string owner, string repo) => host switch
    {
        // 实测（2026 只读抓取）：与 github 同形 —— [{"sha":"…","commit":{"committer":{"date":"…"}}}]，无需 token
        "gitee.com" => $"https://gitee.com/api/v5/repos/{owner}/{repo}/commits?per_page=1",
        "gitlab.com" => $"https://gitlab.com/api/v4/projects/{Uri.EscapeDataString(path)}/repository/commits?per_page=1",
        "bitbucket.org" => $"https://api.bitbucket.org/2.0/repositories/{owner}/{repo}/commits?pagelen=1",
        _ => $"https://api.github.com/repos/{owner}/{repo}/commits?per_page=1"
    };

    /// <summary>某个提交上的文件直链（按托管站选择；github 与旧版逐字一致）。</summary>
    private static string PluginRawFileUrl(string host, string path, string owner, string repo, string sha, string file) => host switch
    {
        "gitee.com" => $"https://gitee.com/{owner}/{repo}/raw/{sha}/{file}",
        "gitlab.com" => $"https://gitlab.com/{path}/-/raw/{sha}/{file}",
        "bitbucket.org" => $"https://api.bitbucket.org/2.0/repositories/{owner}/{repo}/src/{sha}/{file}",
        _ => $"https://raw.githubusercontent.com/{owner}/{repo}/{sha}/{file}"
    };

    /// <summary>
    /// 提交列表响应，即（完整提交号, 日期）。纯函数（自检直接喂 JSON 样本，不发网络请求）。
    /// 实测字段名（web_fetch 只读抓取公开接口原文）：
    ///   · github  <c>[{"sha":"…","commit":{"committer":{"date":"…"}}}]</c>
    ///   · gitee   <c>[{"sha":"…","commit":{"author":{"date":"…"},"committer":{"date":"…"}}}]</c> <- 与 github 同形
    ///   · gitlab  <c>[{"id":"…","committed_date":"…"}]</c>
    ///   · bitbucket <c>{"values":[{"hash":"…","date":"…"}]}</c>
    /// 日期只取前 10 位；认不出来一律返回空串，绝不抛。
    /// </summary>
    internal static (string Sha, string Date) ParseCommitJson(string? host, string? json)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json)) return ("", "");
            using var doc = System.Text.Json.JsonDocument.Parse(json!);
            var root = doc.RootElement;
            System.Text.Json.JsonElement first;
            if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                if (root.GetArrayLength() == 0) return ("", "");
                first = root[0];
            }
            else if (root.ValueKind == System.Text.Json.JsonValueKind.Object
                     && HostAlias(host ?? "") == "bitbucket.org")
            {
                // bitbucket 的列表在 values[] 里（另有一个 Object 根的兜底：sha 直接挂在根上）
                if (root.TryGetProperty("values", out var vals) && vals.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    if (vals.GetArrayLength() == 0) return ("", "");
                    first = vals[0];
                }
                else if (!root.TryGetProperty("sha", out _) && !root.TryGetProperty("hash", out _) && !root.TryGetProperty("id", out _))
                    return ("", "");
                else first = root;
            }
            else return ("", "");

            if (first.ValueKind != System.Text.Json.JsonValueKind.Object) return ("", "");

            string sha = "";
            if (first.TryGetProperty("sha", out var sEl) && sEl.ValueKind == System.Text.Json.JsonValueKind.String) sha = sEl.GetString() ?? "";
            else if (first.TryGetProperty("hash", out var hEl) && hEl.ValueKind == System.Text.Json.JsonValueKind.String) sha = hEl.GetString() ?? "";
            else if (first.TryGetProperty("id", out var iEl) && iEl.ValueKind == System.Text.Json.JsonValueKind.String) sha = iEl.GetString() ?? "";

            string date = "";
            if (first.TryGetProperty("commit", out var cEl) && cEl.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                if (cEl.TryGetProperty("committer", out var cm) && cm.ValueKind == System.Text.Json.JsonValueKind.Object
                    && cm.TryGetProperty("date", out var dEl) && dEl.ValueKind == System.Text.Json.JsonValueKind.String)
                    date = dEl.GetString() ?? "";
                else if (cEl.TryGetProperty("author", out var au) && au.ValueKind == System.Text.Json.JsonValueKind.Object
                    && au.TryGetProperty("date", out var d2) && d2.ValueKind == System.Text.Json.JsonValueKind.String)
                    date = d2.GetString() ?? "";
            }
            else if (first.TryGetProperty("committed_date", out var gd) && gd.ValueKind == System.Text.Json.JsonValueKind.String) date = gd.GetString() ?? "";
            else if (first.TryGetProperty("date", out var bd) && bd.ValueKind == System.Text.Json.JsonValueKind.String) date = bd.GetString() ?? "";

            if (date.Length >= 10) date = date.Substring(0, 10);
            return (sha, date);
        }
        catch { return ("", ""); }
    }

    /// <summary>自检用：造一段假的锁文件文本。</summary>
    internal static string SampleLock(string pkg, string commit)
        => $"lockfileVersion: '9.0'\n\nimporters:\n\npackages:\n\n  {pkg}@github:o/r:\n    resolution: {{commit: {commit}}}\n";
}
