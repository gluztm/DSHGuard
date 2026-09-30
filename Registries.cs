namespace DSHGuard;

/// <summary>
/// 下载来源（npm 镜像源）的唯一出处：版本查询、插件市场、插件安装都读这里。
/// 设置里留空 = 社区镜像；界面上「下载来源」那一行点击即切换。
/// </summary>
public static class Registries
{
    public const string Community = "https://registry.npmmirror.com";
    public const string Official = "https://registry.npmjs.org";

    /// <summary>当前生效的镜像源地址（由设置驱动）。</summary>
    public static string Current { get; private set; } = Community;

    /// <summary>
    /// 桌面版**专用**的镜像源地址；默认 <see cref="Community"/>，
    /// 且"未单独配置"时（未调用 <see cref="ConfigureDesktop"/> 或传空）始终跟随 <see cref="Current"/>。
    /// </summary>
    public static string Desktop { get; private set; } = Community;

    /// <summary>
    /// 桌面版是否**显式**配过专用源。为什么要记这个：只有它才知道 <see cref="Desktop"/>
    /// 当前存的是"用户配的地址"还是"跟随 Current 的回落值" —— 否则切全局源时无从判断该不该带着桌面版一起切。
    /// 默认 false（跟随），故既有单套配置的行为一个字节不变。
    /// </summary>
    private static bool _desktopExplicit = false;

    /// <summary>按设置里的值切换当前镜像源（空值 = 社区镜像）。</summary>
    public static void Configure(string? setting)
    {
        Current = Resolve(setting);
        // 桌面版没单独配过 ⇒ 它的回落值必须跟着新的 Current 一起刷新，否则"切全局源"对桌面版不生效。
        if (!_desktopExplicit) Desktop = Current;
    }

    /// <summary>
    /// 按目标取镜像源地址：桌面版返回 <see cref="Desktop"/>，Web（及默认）返回 <see cref="Current"/>。
    /// 为什么要按目标分：桌面版是官方 Electron 包，取包通道与 Web 引擎（npx）不必同源。
    /// 未单独配置时两侧同值 ⇒ 既有调用点（插件市场、五个 `--profile web` 构造器）行为一个字节不变。
    /// </summary>
    public static string For(GuardTarget target) => target == GuardTarget.Desktop ? Desktop : Current;

    /// <summary>
    /// 配桌面版专用源（空值 = 跟随 <see cref="Current"/>，即"未单独配置"）。
    /// 传了地址就钉死为那份地址，之后切全局源**不再**带着它走（<see cref="_desktopExplicit"/> 为此而设）；
    /// 传空则回到"跟随"语义 —— 且此刻立即把 <see cref="Desktop"/> 刷成当前的 <see cref="Current"/>。
    /// 与 <see cref="Configure"/> 的调用顺序因此**无关**（两个方向都会得到正确的回落值）。
    /// </summary>
    public static void ConfigureDesktop(string? setting)
    {
        _desktopExplicit = !string.IsNullOrWhiteSpace(setting);
        Desktop = _desktopExplicit ? Resolve(setting) : Current;
    }

    /// <summary>设置值 → 实际地址。</summary>
    public static string Resolve(string? setting)
        => string.Equals(setting, Official, System.StringComparison.OrdinalIgnoreCase) ? Official : Community;

    /// <summary>设置值 → 界面上显示的短名（小白化，不带括号说明）。</summary>
    public static string Label(string? setting)
        => string.Equals(setting, Official, System.StringComparison.OrdinalIgnoreCase) ? "官方源" : "社区镜像";

    /// <summary>点击后切换到哪个源（返回新的设置值）。</summary>
    public static string Toggle(string? setting)
        => string.Equals(setting, Official, System.StringComparison.OrdinalIgnoreCase) ? Community : Official;
}
