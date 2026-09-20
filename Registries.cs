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

    /// <summary>按设置里的值切换当前镜像源（空值 = 社区镜像）。</summary>
    public static void Configure(string? setting) => Current = Resolve(setting);

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
