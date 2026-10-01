namespace DSHGuard;

/// <summary>
/// 守护壳自身版本号。
/// 规则（2026-09-13 起，用户定）：
///   **前两位是 release 号**——1.0 是第一个 release，1.1 是第二个；
///   同一个 release 里的小修改累加第三位（第 5 次修改 → 1.1.5）；
///   release 正式对外发布之后，下一轮从 1.2.x 重新计数。
///   
///   2.0.0（2026-09-30）：架构重构，Web 引擎与桌面版完全分离为两个独立入口，
///   插件/快照操作按目标选链路（pnpm for desktop, npx for web），
///   在 DSH 桌面版正式发布后进行的重大架构升级，故升主版本号到 2。
///   
///   Major = 主版本号（1 = 1.x 系列，2 = 2.x 系列）
///   Minor = release 序号（0 = x.0，1 = x.1，依此类推）
///   Patch = 本 release 内的修改序号（0 表示首个 release）
///
///   2.0.1（2026-10-01）：修复 2.0.0 混入的仓库坐标错误 ——
///   <see cref="RepoOwner"/> 曾被误改为 <c>JetLua</c>（该账号下无此仓库，接口回 404），
///   导致「检查更新」始终问不出来、发布页按钮打开一个不存在的地址。
/// </summary>
public static class GuardVersion
{
    /// <summary>主版本号：1 = 1.x 系列，2 = 2.x 系列。</summary>
    public const int Major = 2;
    
    /// <summary>release 序号：0 = x.0，1 = x.1，依此类推。</summary>
    public const int Minor = 0;

    /// <summary>本 release 内的修改序号：0 表示首个 release。</summary>
    /// <remarks>
    /// 2.0.0 是架构重构：目标分离（Web/桌面双轨化深化）+ pnpm 构造器 + PluginOps 分发层。
    /// 这是在 DSH 桌面版正式发布后进行的重大升级，影响所有插件写操作，故升主版本号。
    /// </remarks>
    public const int Patch = 1;

    /// <summary>交付批次计数：只做内部记账（日志/自检提示），不参与版本号。</summary>
    public const int Batch = 157;

    /// <summary>
    /// 本程序的代码仓库所有者（检测新版本时去这里看发行版）。
    ///
    /// ⚠ 这是**对外坐标**，写错不会报错、只会静默失效：2.0.0 曾误写成 <c>JetLua</c>，
    ///   于是「检查更新」查的是一个不存在的仓库（接口 404 ⇒ 判定 Unknown），
    ///   发布页按钮也指向不存在的地址，而整个自检全绿。
    ///   改动此值时**必须**同步改 <c>SelfTest</c> 里钉住仓库坐标的那组断言（会强制你确认）。
    /// </summary>
    public const string RepoOwner = "gluztm";

    /// <summary>本程序的代码仓库名称。</summary>
    public const string RepoName = "DSHGuard";

    /// <summary>格式化版本号字符串（对外显示）。2.0 起主版本号始终显示。</summary>
    public static string Version => VersionFor(Major, Minor, Patch);

    /// <summary>生成版本号字符串（纯函数，便于自检）。2.x 起始终显示三段。</summary>
    public static string VersionFor(int major, int minor, int patch)
    {
        if (major >= 2) return $"{major}.{minor}.{patch}";
        // 1.x 保持旧口径：patch=0 不显示第三段
        return patch == 0 ? $"1.{minor}" : $"1.{minor}.{patch}";
    }

    /// <summary>
    /// 显示用的完整版本信息（含内部批次号）：「2.0.0（批次 150）」。
    /// 自检断言、日志、设置页显示用它；对外发布的 Release 页不带批次。
    /// </summary>
    public static string Display => $"{Version}（批次 {Batch}）";

    /// <summary>
    /// 远程标签规范化：去掉可选的 <c>v</c> 前缀（同一个仓库上两种写法都可能有：<c>1.0</c> 与 <c>v1.0</c>），
    /// 再去掉首尾空白。只在 <c>v</c> 后面紧跟数字时才去掉 —— 否则 <c>version-2</c> 这类名字会被削成
    /// <c>ersion-2</c>（削坏了就再也比不出来，比不出来的后果是"拿不准"，见 <see cref="Judge"/>）。
    /// </summary>
    public static string NormalizeTag(string? tag)
    {
        string s = (tag ?? "").Trim();
        if (s.Length > 1 && (s[0] == 'v' || s[0] == 'V') && char.IsDigit(s[1])) s = s.Substring(1);
        return s.Trim();
    }

    /// <summary>
    /// 远程标签比本机版本新不新（纯函数，不查网，自检可直接断言）。
    ///
    /// 比较器是 <see cref="VersionInfo.Compare"/> —— 项目里既有的那一份 semver 比较（判据只有一处，
    /// 不在这里另写一套）。它**按数字段比较**：<c>1.10</c> 大于 <c>1.9</c>（字符串比会得出相反的结论），
    /// 且两侧都先过 <see cref="VersionInfo.IsComparableVersion"/>，认不成版本号的取值一律不可比。
    ///
    /// 注意（失败关门）：任一侧认不成版本号 ⇒ <see cref="GuardUpdateVerdict.Unknown"/>；
    ///   绝不落到"已是最新" —— 比不出来就报最新，正是本项目反复栽过的"谎报"。
    /// </summary>
    public static GuardUpdateVerdict Judge(string? remoteTag)
    {
        string local = Version;
        string remote = NormalizeTag(remoteTag);
        if (!VersionInfo.IsComparableVersion(local) || !VersionInfo.IsComparableVersion(remote))
            return GuardUpdateVerdict.Unknown;
        return VersionInfo.Compare(local, remote) < 0
            ? GuardUpdateVerdict.NewerAvailable
            : GuardUpdateVerdict.UpToDate;
    }
}

/// <summary>
/// 守护壳自身版本检测的结论（三态；<see cref="GuardVersion.Judge"/> 的产物，纯函数、便于自检）。
///
/// 为什么必须有 <see cref="Unknown"/> 这一档：远程问不出来（超时 / 断网 / 站点限流 / 仓库还没有发行版）
/// 与"远程就是当前这个版本"对用户的含义完全不同 —— 前者是"这次没问成，稍后再试"，
/// 后者是"你不用管"。把它们挤进同一个出口，就会出现"断网时显示已是最新"这种谎报。
/// </summary>
public enum GuardUpdateVerdict
{
    /// <summary>问不出来或比不出来（超时 / 断网 / 远程没有可比的版本号 / 报文读不懂）—— 不是"已是最新"。</summary>
    Unknown,
    /// <summary>远程不比本机新（含两边相同）。</summary>
    UpToDate,
    /// <summary>远程比本机新。</summary>
    NewerAvailable
}
