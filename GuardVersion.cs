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
    public const int Minor = 0;

    /// <summary>本 release 内的修改序号：0 表示不带第三段（形如 1.2）。</summary>
    public const int Patch = 0;

    /// <summary>交付批次计数：只做内部记账（日志/自检提示），不参与版本号。</summary>
    public const int Batch = 129;

    public static string Version => VersionFor(Minor, Patch);

    /// <summary>版本号换算（纯函数，便于自检）。</summary>
    public static string VersionFor(int minor, int patch)
        => patch <= 0 ? $"1.{minor}" : $"1.{minor}.{patch}";

    /// <summary>界面上显示的一行文字。</summary>
    public static string Display => "守护壳版本 " + Version;
}
