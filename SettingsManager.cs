using System;
using System.IO;
using System.Text.Json;

namespace DSHGuard;

public class AppSettings
{
    public bool AutoStart { get; set; } = false;
    public bool AutoBrowseOnReady { get; set; } = false;
    /// <summary>毛玻璃背景（老机器可以关掉，换成纯色背景更流畅）。</summary>
    public bool AcrylicEnabled { get; set; } = true;
    public int Port { get; set; } = 3080;
    public int Theme { get; set; } = 0;
    /// <summary>自定义启动命令（不含 --no-open / --port，那两个由守护壳自动追加）。</summary>
    public string LaunchCommand { get; set; } = "";
    /// <summary>自定义路径（留空 = 自动探测默认值）。</summary>
    public string PathLogs { get; set; } = "";
    public string PathSnapshots { get; set; } = "";
    public string PathProfile { get; set; } = "";
    /// <summary>诊断包输出目录（留空 = 程序目录下的 Logs）。</summary>
    public string PathDiagnostics { get; set; } = "";
    /// <summary>是否已完成首次路径自动识别与依赖检查。</summary>
    public bool PathsInitialized { get; set; } = false;
    /// <summary>下载来源（npm 镜像源）：空 = 社区镜像，或官方源地址。</summary>
    public string Registry { get; set; } = "";
    /// <summary>自动快照保留份数（超过就删最旧的）。</summary>
    public int AutoSnapshotKeep { get; set; } = 10;
}

public class SettingsManager
{
    /// <summary>配置文件路径：程序目录\Config\settings.json。</summary>
    private static string SettingsPath => Path.Combine(GuardPaths.ConfigDir, "settings.json");

    public bool AutoStart { get; set; } = false;
    public bool AutoBrowseOnReady { get; set; } = false;
    /// <summary>毛玻璃背景（老机器可以关掉，换成纯色背景更流畅）。</summary>
    public bool AcrylicEnabled { get; set; } = true;
    public int Port { get; set; } = 3080;
    public int Theme { get; set; } = 0;
    /// <summary>自定义启动命令（留空 = 用默认 npx 命令）。</summary>
    public string LaunchCommand { get; set; } = "";
    /// <summary>自定义日志目录（留空 = exe 同级 logs）。</summary>
    public string PathLogs { get; set; } = "";
    /// <summary>自定义快照目录（留空 = 程序目录下的 Snapshots）。</summary>
    public string PathSnapshots { get; set; } = "";
    /// <summary>自定义 profile 目录（留空 = ~/.dsh/profiles/web）。</summary>
    public string PathProfile { get; set; } = "";
    /// <summary>诊断包输出目录（留空 = 程序目录下的 Logs）。</summary>
    public string PathDiagnostics { get; set; } = "";
    /// <summary>首次启动的自动识别 / 依赖检查是否已完成。</summary>
    public bool PathsInitialized { get; set; } = false;
    /// <summary>下载来源（npm 镜像源）：空 = 社区镜像。</summary>
    public string Registry { get; set; } = "";
    /// <summary>自动快照保留份数（超过就删最旧的）。</summary>
    public int AutoSnapshotKeep { get; set; } = 10;

    /// <summary>
    /// 最近一次 <see cref="Load"/> 读到的设置**是否可信**（默认不可信，直到某次 Load 证明它可信）。
    ///
    /// "不可信"的确切含义：本次内存里的值**不是**用户在 settings.json 里写的那个 —— 要么文件读取失败
    /// （字段停在类型默认值）、要么份数越界被回落。此时 <see cref="AutoSnapshotKeep"/> 只是**占位值**：
    /// 它**不许**被当作裁剪依据，否则就是"按默认份数静默删掉用户快照、且不可恢复"。
    ///
    /// 为什么默认值取 <c>false</c>：在一个"读不出来就默认删数据"的下游旁边，默认值应当倒向**不动作**那一侧。
    /// 首次 Load 一定会在读之前把它设回 true，故正常运行不会因此少裁。
    /// ⚠ 这正是给下游（裁剪那一侧）看的**只读状态**；调用点散落在别的文件里，
    ///   所以这里刻意**不改 <see cref="Save"/> / <see cref="Load"/> 的签名**，只新增状态。
    /// </summary>
    public bool LastLoadTrusted { get; private set; } = false;

    /// <summary>配置不可信时的人话原因（可信时为空串）。给日志/界面用，不参与判定。</summary>
    public string LastLoadError { get; private set; } = "";

    /// <summary>
    /// 最近一次 <see cref="Save"/> 是否**落盘失败**（默认 false = 没失败过）。
    ///
    /// 为什么需要它：<see cref="Save"/> 原先末尾是空 <c>catch { }</c>，写盘失败时**一个字都不留**，
    /// 而调用点紧接着就弹「已保存并立即生效」⇒ 界面在**谎报成功**，用户重启后设置全部回退。
    /// <see cref="Save"/> 的调用点全在别的文件（本类不许碰），故这里**不改签名**，
    /// 改为把真相放在这个只读状态上，由转派补丁让调用点在弹窗前查一次（见报告"转派补丁"）。
    /// </summary>
    public bool LastSaveFailed { get; private set; } = false;

    /// <summary>最近一次保存失败的人话原因（成功时为空串）。</summary>
    public string LastSaveError { get; private set; } = "";

    public SettingsManager()
    {
        Load();
    }

    /// <summary>启动命令归一化：旧版本写入的默认命令视为未自定义，否则会覆盖版本记忆固定的版本而始终拉取 @latest。</summary>
    public static string NormalizeLaunch(string? raw)
    {
        string s = (raw ?? "").Trim();
        if (s.Equals(ProcessManager.LegacyLaunchCommand, StringComparison.OrdinalIgnoreCase)) return "";
        if (s.Equals("npx --yes @deepseek-ai/dsh@latest web --no-open", StringComparison.OrdinalIgnoreCase)) return "";
        return s;
    }

    public void Load()
    {
        // 乐观起手：除非下面命中"根本没读到配置"或"份数回落"，本次读到的一切都可信。
        // 两个状态都必须在这里复位 —— 否则一次失败留下的 false 会把后面每一次成功读取永久拖住。
        LastLoadTrusted = true;
        LastLoadError = "";
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings != null)
                {
                    AutoStart = settings.AutoStart;
                    AutoBrowseOnReady = settings.AutoBrowseOnReady;
                    AcrylicEnabled = settings.AcrylicEnabled;
                    LaunchCommand = NormalizeLaunch(settings.LaunchCommand);
                    PathLogs = settings.PathLogs ?? "";
                    PathSnapshots = settings.PathSnapshots ?? "";
                    PathProfile = settings.PathProfile ?? "";
                    PathDiagnostics = settings.PathDiagnostics ?? "";
                    PathsInitialized = settings.PathsInitialized;
                    Registry = settings.Registry ?? "";
                    // 份数归一：1..500 原样采纳，越界回落 10（回落粒度见 NormalizeKeep）。
                    // ⚠ 回落也**不可信**：磁盘上的值是 30、只因越界或读残而变成 10，两者在内存里长得一样。
                    //   下游裁剪**只看这个数** —— 一旦拿 10 去裁用户配的 30，多删的 20 份不可恢复。
                    //   所以回落的**同一分支**里就必须把"本次不可信"记下来（下游裁剪会整轮跳过，见 SnapshotManager）。
                    AutoSnapshotKeep = NormalizeKeep(settings.AutoSnapshotKeep);
                    // 端口与主题同样必须归一：手改 settings.json 塞进来的越界值会一路传成 _port /
                    // 主题状态（IsPortListening(0)、ProcessManager.Start(0, …) 全是非法值）。
                    Port = NormalizePort(settings.Port);
                    Theme = NormalizeTheme(settings.Theme);
                    if (Port != settings.Port)
                        Logger.NoteDiagnosis($"配置文件里的端口 {settings.Port} 越界（合法区间 1..65535），已回落默认 {Port}");
                    if (Theme != settings.Theme)
                        Logger.NoteDiagnosis($"配置文件里的主题取值 {settings.Theme} 非法（合法取值 0 夜间 / 1 日间），已回落默认 {Theme}");
                    if (AutoSnapshotKeep != settings.AutoSnapshotKeep)
                    {
                        // 与上面端口 / 主题**同款**的回落留痕 —— 原先唯独这一处没有，于是"份数被降配"在日志里一个字都没有，
                        // 而它的下游是**真删用户快照**（TrimAll → TrimKind → Delete），代价比端口写错大得多。
                        Logger.NoteDiagnosis($"配置文件里的自动快照保留份数 {settings.AutoSnapshotKeep} 越界（合法区间 1..500），"
                                           + $"已回落默认 {AutoSnapshotKeep}；为避免按回落值误删用户快照，本次启动不执行快照裁剪");
                        // 失败关闭：本次的份数**不是用户写的那个数**，绝不能拿去当裁剪依据。
                        LastLoadTrusted = false;
                        LastLoadError = $"AutoSnapshotKeep={settings.AutoSnapshotKeep} 越界，已回落 {AutoSnapshotKeep}（份数不可信）";
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // 容错行为不变：仍然吞掉异常、回落默认值。只是不再**静默**——补一条诊断痕迹
            // （Logger.Log 是空实现，真落盘必须走 NoteDiagnosis；它自身 try/catch 包裹，不会外抛）。
            Logger.NoteDiagnosis($"配置文件读取失败，已回落默认设置：{ex.GetType().Name} {ex.Message}");
            // 失败关闭：连文件都没读成，字段全是类型默认值 —— 尤其 AutoSnapshotKeep 会停在 10。
            // 这个 10 **不是**用户的配置意图，绝不许拿它去裁用户快照（下游会整轮跳过裁剪）。
            LastLoadTrusted = false;
            LastLoadError = $"配置文件读取失败（{ex.GetType().Name}: {ex.Message}），已回落默认设置（份数不可信）";
        }
    }

    /// <summary>
    /// 快照保留份数归一：可选值 1..500 一律**原样采纳**（含 20）；越界（0 / 负数 / 极大值）回落默认 10。
    ///
    /// 为什么不再把 20 当"没设置过"：旧默认值确实是 20，但**用户显式配的也是 20**，二者无法区分；
    /// 一律回落 10 的后果是"用户配 20、实际留 10"—— 与本次修掉的"配 30 被裁到 10"是同一类静默降配。
    /// 宁可少数从旧默认 20 升上来的用户多留 10 份快照（可手动删），也不静默删用户快照。
    /// </summary>
    public static int NormalizeKeep(int v) => v is > 0 and <= 500 ? v : 10;

    /// <summary>
    /// 端口归一：合法区间 <c>1..65535</c> —— 与设置页输入侧的校验边界**刻意保持一致**
    /// （见 <c>MainWindow.ApplyPort</c> 的 <c>port &gt; 0 &amp;&amp; port &lt;= 65535</c>），
    /// 免得"手改配置文件允许 0、界面输入不允许 0"这种两条入口口径不一。
    /// 越界（0 / 负数 / 70000）回落默认 3080。纯函数，便于自检直接断言。
    /// </summary>
    public static int NormalizePort(int v) => v is > 0 and <= 65535 ? v : 3080;

    /// <summary>
    /// 主题归一：<see cref="Theme"/> 的实际类型是 <c>int</c>，合法取值**只有两个** ——
    /// 0 = 夜间（默认）、1 = 日间；写入侧见 <c>MainWindow.ApplyTheme</c> 的 <c>dark ? 0 : 1</c>，
    /// 读取侧见 <c>ApplyTheme(_settings.Theme != 1)</c>。非法值回落默认 0。纯函数，便于自检直接断言。
    /// </summary>
    public static int NormalizeTheme(int v) => v is 0 or 1 ? v : 0;

    /// <summary>
    /// 把当前设置写盘。
    ///
    /// ⚠ **不改签名**（返回值仍是 <c>void</c>）：调用点共 11 处、全部在别的文件里，
    ///   改成 <c>bool</c> 会同时动到那些文件，超出本单"只改 SettingsManager.cs"的边界。
    ///   失败改由 <see cref="LastSaveFailed"/> / <see cref="LastSaveError"/> 暴露，调用点在弹窗前查一次即可。
    ///
    /// 成功时清状态、**失败时真落盘留证**（<c>Logger.Log</c> 是空实现，必须走 <see cref="Logger.LogError"/>）。
    /// 不再有"写失败却静默"的路径：这正是"界面说已保存、磁盘上一个字没有"的根因。
    /// </summary>
    public void Save()
    {
        // 每次保存都先复位，免得上一次的失败状态被这一次的成功继承（调用点只会在弹窗前读一次）。
        LastSaveFailed = false;
        LastSaveError = "";
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var settings = new AppSettings
            {
                AutoStart = AutoStart,
                AutoBrowseOnReady = AutoBrowseOnReady,
                AcrylicEnabled = AcrylicEnabled,
                LaunchCommand = LaunchCommand,
                PathLogs = PathLogs,
                PathSnapshots = PathSnapshots,
                PathProfile = PathProfile,
                PathDiagnostics = PathDiagnostics,
                PathsInitialized = PathsInitialized,
                Registry = Registry,
                AutoSnapshotKeep = AutoSnapshotKeep,
                Port = Port,
                Theme = Theme
            };
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception ex)
        {
            // 原先这里是空 catch `{ }`：写盘失败连一条日志都没有，而调用点紧接着弹「已保存并立即生效」。
            // 现在①真落盘（LogError ⇒ [ERROR] + 堆栈），②把真相记进只读状态，供调用点改口径。
            LastSaveFailed = true;
            LastSaveError = $"{ex.GetType().Name}: {ex.Message}";
            Logger.LogError("SettingsManager.Save", ex);
            Logger.NoteDiagnosis($"设置保存失败（{SettingsPath}）：{LastSaveError} ⇒ 本次改动**没有写盘**，"
                               + "重启后会回退；界面不得再提示「已保存」");
        }
    }
}
