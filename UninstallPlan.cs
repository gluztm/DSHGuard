using System;

namespace DSHGuard;

/// <summary>卸载方式（用户定的三种，互斥）。</summary>
internal enum UninstallMode
{
    /// <summary>只删除主程序，数据全留。</summary>
    AppOnly,
    /// <summary>卸载 + 删除缓存（设置、快照、日志保留）。</summary>
    WithCache,
    /// <summary>全部清空：卸载 + 删全部数据 + 连程序文件夹一起删掉。</summary>
    Everything
}

/// <summary>
/// 卸载方案（纯函数，便于自检）：三种模式各自的文案、要删的数据目录、以及给 Inno 卸载器的参数。
/// 两个入口（程序内 <c>--uninstall</c> 窗口 / 安装包里的卸载程序）共用这一份，保证一字不差。
/// **任何模式都不碰 DSH 自己的配置文件目录**（那是用户的插件与配置，删了等于毁掉 DSH）。
/// </summary>
internal static class UninstallPlan
{
    /// <summary>选项标题（三选项按钮上的字）。</summary>
    public static string Label(UninstallMode mode) => mode switch
    {
        UninstallMode.Everything => "全部清空",
        UninstallMode.WithCache => "删除缓存",
        _ => "只删除主程序"
    };

    /// <summary>选项说明（一行，别啰嗦）。</summary>
    public static string Hint(UninstallMode mode) => mode switch
    {
        UninstallMode.Everything => "卸载并删除全部数据，程序文件夹一起删掉（不动 DSH 的配置文件）",
        UninstallMode.WithCache => "卸载并删除缓存；设置、快照、日志保留",
        _ => "只卸载程序本体，数据全部保留"
    };

    /// <summary>该模式要删的数据目录（程序目录下的子目录名）。</summary>
    public static string[] DataDirs(UninstallMode mode) => mode switch
    {
        UninstallMode.Everything => new[] { "Cache", "Config", "Logs", "Snapshots", "Tools" },
        UninstallMode.WithCache => new[] { "Cache" },
        _ => Array.Empty<string>()
    };

    /// <summary>是否连程序文件夹一起删。</summary>
    public static bool DeletesAppFolder(UninstallMode mode) => mode == UninstallMode.Everything;

    /// <summary>给 Inno 卸载器的 /DELETE_DATA= 参数（空串 = 不带该参数）。</summary>
    public static string DeleteDataArg(UninstallMode mode) => mode switch
    {
        UninstallMode.Everything => "Config,Snapshots,Logs,Cache",
        UninstallMode.WithCache => "Cache",
        _ => ""
    };

    /// <summary>给 Inno 卸载器的 /MODE= 参数（新增，语义比 /DELETE_DATA= 更全：含"连目录一起删"）。</summary>
    public static string ModeArg(UninstallMode mode) => mode switch
    {
        UninstallMode.Everything => "all",
        UninstallMode.WithCache => "cache",
        _ => "app"
    };

    /// <summary>按名称解析模式（命令行用；认不出按"只删主程序"处理，最保守）。</summary>
    public static UninstallMode Parse(string? name) => (name ?? "").Trim().ToLowerInvariant() switch
    {
        "all" or "everything" => UninstallMode.Everything,
        "cache" or "withcache" => UninstallMode.WithCache,
        _ => UninstallMode.AppOnly
    };

    /// <summary>
    /// 界面只问一件事：**保留不保留用户文件**（2026-09-13 定）。
    /// 勾上「保留」= 只删主程序（AppOnly）；取消勾选 = 全部清空（Everything）。
    /// 注：互斥多选项已下线——改勾选又触发自身事件会无限递归（现场：卸载器一点就闪退）。
    /// </summary>
    public static UninstallMode FromKeepUserFiles(bool keepUserFiles)
        => keepUserFiles ? UninstallMode.AppOnly : UninstallMode.Everything;

    /// <summary>对勾文案（程序内与安装包里一字不差）。</summary>
    public const string KeepFilesLabel = "保留我的设置、快照与日志（推荐）";

    /// <summary>对勾下面的说明。</summary>
    public const string KeepFilesHint = "不勾选 = 全部删除，连程序文件夹一起删掉。任何情况都不会动 DSH 自己的配置文件。";

    /// <summary>单选①：只卸载程序、保留数据（默认，最保守）。</summary>
    public const string KeepDataLabel = "只卸载程序，保留我的设置与数据（推荐）";
    public const string KeepDataHint = "设置、快照、日志都留在原处；以后重装还能接着用。";

    /// <summary>单选②：连程序文件夹一起删干净。</summary>
    public const string DeleteAllLabel = "卸载并删除全部文件（连程序文件夹一起删掉）";
    public const string DeleteAllHint = "程序目录下的设置、快照、日志一并删除，程序文件夹也会被删掉；不会动 DSH 自己的配置。";

    /// <summary>按「是否保留数据」映射到模式（界面只问这一件事）。</summary>
    public static UninstallMode FromKeepData(bool keepData) => FromKeepUserFiles(keepData);

    /// <summary>确认正文（MSI 风格的一句话）。</summary>
    public static string ConfirmText(string version)
        => $"将从计算机中删除 DSH 守护壳 {version}。确定要继续吗？";
}
