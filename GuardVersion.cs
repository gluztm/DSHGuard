namespace DSHGuard;

/// <summary>
/// 守护壳自身版本号。
/// 规则（2026-09-13 起，用户定）：
///   **前两位是 release 号**——1.0 是第一个 release，1.1 是第二个；
///   同一个 release 里的小修改累加第三位（第 5 次修改 → 1.1.5）；
///   release 正式对外发布之后，下一轮从 1.2.x 重新计数。
///   Minor = release 序号（0 → 1.0，1 = 1.1，2 = 1.2 …）
///   Patch = 本 release 内的第几次修改（0 = release 本身，版本号不带第三位）
/// </summary>
public static class GuardVersion
{
    /// <summary>release 序号：0 = 1.0，1 = 1.1，依此类推。</summary>
    public const int Minor = 2;

    /// <summary>本 release 内的修改序号：0 表示不带第三段（形如 1.2）。</summary>
    public const int Patch = 2;

    /// <summary>交付批次计数：只做内部记账（日志/自检提示），不参与版本号。</summary>
    public const int Batch = 133;

    /// <summary>本程序的代码仓库所有者（检测新版本时去这里看发行版）。</summary>
    public const string RepoOwner = "gluztm";

    /// <summary>本程序的仓库名（与 <see cref="RepoOwner"/> 合成仓库坐标）。</summary>
    public const string RepoName = "DSHGuard";

    public static string Version => VersionFor(Minor, Patch);

    /// <summary>版本号换算（纯函数，便于自检）。</summary>
    public static string VersionFor(int minor, int patch)
        => patch <= 0 ? $"1.{minor}" : $"1.{minor}.{patch}";

    /// <summary>界面上显示的一行文字。</summary>
    public static string Display => "守护壳版本 " + Version;

    /// <summary>
    /// 远端标签规范化：去掉可选的 <c>v</c> 前缀（同一个仓库上两种写法都可能有：<c>1.0</c> 与 <c>v1.0</c>），
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
    /// 远端标签比本机版本新不新（纯函数，不查网，自检可直接断言）。
    ///
    /// 比较走 <see cref="VersionInfo.Compare"/> —— 项目里既有的那一份 semver 比较（判据只有一处，
    /// 不在这里另写一套）。它**按数字段比**：<c>1.10</c> 大于 <c>1.9</c>（字符串比会得出相反的结论），
    /// 且两侧都先过 <see cref="VersionInfo.IsComparableVersion"/>，读不成版本号的取值一律不可比。
    ///
    /// 注意（失败关闭）：任一侧读不成版本号 ⇒ <see cref="GuardUpdateVerdict.Unknown"/>，
    ///   绝不落到"已是最新"——比不出来就报最新，正是本项目反复栽过的"谎报"。
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
/// 守护壳自身版本检测的结论（三态；<see cref="GuardVersion.Judge"/> 的产物，纯值、便于自检）。
///
/// 为什么必须有 <see cref="Unknown"/> 这一档：远端问不出来（超时 / 断网 / 站点限流 / 仓库还没有发行版）
/// 与"远端就是当前这个版本"对用户的含义完全不同 —— 前者是"这次没问成，稍后再试"，
/// 后者是"你不用管"。把它们挤进同一个出口，就会出现"断网时显示已是最新"这种谎报。
/// </summary>
public enum GuardUpdateVerdict
{
    /// <summary>问不出来或比不出来（超时 / 断网 / 远端没有可比的版本号 / 报文读不懂）—— 不是"已是最新"。</summary>
    Unknown,
    /// <summary>远端不比本机新（含两边相同）。</summary>
    UpToDate,
    /// <summary>远端比本机新。</summary>
    NewerAvailable
}
