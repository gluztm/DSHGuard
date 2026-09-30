namespace DSHGuard;

/// <summary>
/// 2.0.0：守护壳"当前管理目标"的**唯一**上下文（Web 引擎 / 官方桌面版）。
///
/// 为什么要有它：2.0.0 基线里"当前目标"散在四个字段（插件页、快照页、全局开关、静态镜像），
///   各自被不同入口改写，左上角开关与页内分段器可以指向不同的引擎 ——
///   用户站在桌面版点「保存快照」，存的却可能是 Web 那一份。
///   现在只有 <see cref="MainWindow"/> 的一个 <c>_ctx</c>，唯一改写点是 <c>SetTarget</c>。
///
/// 路径、下载来源都是**现读**的（设置页随时可改），不在这里缓存；
/// 只有桌面版引擎版本在创建时读一次（读主程序 exe 的文件版本，刷新插件列表时会重建上下文）。
/// </summary>
internal sealed class TargetContext
{
    public GuardTarget Target { get; }

    /// <summary>桌面版引擎版本（创建时读取）；Web 侧恒为空串 —— Web 版本由主窗口异步探测，见 CompatEngineVersion。</summary>
    public string DesktopVersion { get; }

    private TargetContext(GuardTarget target, string desktopVersion)
    {
        Target = target;
        DesktopVersion = desktopVersion;
    }

    public static TargetContext Create(GuardTarget target)
        => new(target, target == GuardTarget.Desktop ? DesktopDetector.ReadVersion(GuardPaths.DesktopInstallDir) : "");

    public bool IsDesktop => Target == GuardTarget.Desktop;

    /// <summary>该目标的 profile 目录（插件清单、补丁层、锁文件、快照采集都从它派生）。</summary>
    public string ProfileDir => GuardPaths.ProfileDirFor(Target);

    /// <summary>该目标的下载来源。</summary>
    public string Registry => Registries.For(Target);

    /// <summary>界面与事件里点名用的中文名。</summary>
    public string Label => LabelOf(Target);

    /// <summary>
    /// 这个目标此刻能不能被选中：Web 恒可用；桌面版要求安装目录里真有主程序
    /// （判据只此一处：<see cref="GuardPaths.DesktopExeFound"/>）。
    /// </summary>
    public bool Available => Target == GuardTarget.Web || GuardPaths.DesktopExeFound;

    public static string LabelOf(GuardTarget t) => t == GuardTarget.Desktop ? "桌面版" : "Web 引擎";

    /// <summary>设置文件里的存法（"web" / "desktop"）。</summary>
    public static string ToSetting(GuardTarget t) => t == GuardTarget.Desktop ? "desktop" : "web";

    /// <summary>设置值 → 目标；认不出的一律回落 Web（历史行为）。</summary>
    public static GuardTarget FromSetting(string? s)
        => string.Equals(s?.Trim(), "desktop", System.StringComparison.OrdinalIgnoreCase) ? GuardTarget.Desktop : GuardTarget.Web;
}
