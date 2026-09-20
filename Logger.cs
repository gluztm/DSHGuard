using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;

namespace DSHGuard;

/// <summary>
/// 日志策略：**只留真出事的现场**。
///   · 仅异常落盘（异常-*.log，含调用栈），可在「日志」页一键复制。
///   · 过程记录不落盘：<see cref="LogDiagnosis"/> 与 <see cref="Log"/> 均为空操作，运行时观测由界面右下「事件信息」承担。
///   · 启动诊断（启动-*.log）**引擎起来了就不留、启动真失败才留**：启动期照旧即时落盘（便于硬崩时也留得下），
///     但引擎端口就绪（或沿用外部引擎）时调 <see cref="DiscardStartupLogIfEngineRunning"/> 把本次这份删掉；
///     判据只有一个 —— <see cref="MarkStartupFailed"/> 有没有被调过（引擎没拉起 / 没等到端口就绪），
///     所以"第一次尝试失败、自动重试成功"这类**最终成功**的启动同样不留日志。
///     另有一层兜底：凡写过 [ERROR]/[FATAL] 或调过 <see cref="MarkStartupUnhealthy"/> ⇒
///     <see cref="DiscardStartupLogIfHealthy"/> 也不会删（见 <see cref="HasFailureEvidence"/>）。
///   · 命令诊断（「命令结束」这类）**只有失败（退出码非 0）才落盘**，见 <see cref="ShouldPersistCommandLog"/>；
///     落盘内容按每流 <see cref="CommandLogMaxLines"/> 行 / <see cref="CommandLogMaxChars"/> 字符截断，
///     见 <see cref="TruncateCommandOutput"/>（整段 dump 抄进日志是"内容特别多"的直接原因）。
///   · 目录默认 &lt;exe&gt;\Logs（= <see cref="GuardPaths.LogDir"/>，可在设置页「路径」页修改）；
///     自检/截图这类测试夹具用 <see cref="UseLogDirForRun"/> 切到临时目录，**绝不写进用户日志目录**。
///   · 启动时清理：异常日志删超过 <see cref="MaxAgeDays"/> 天的、仍超过 <see cref="MaxFiles"/> 个则从最旧删起；
///     启动诊断日志**另算一套更严的配额**（<see cref="MaxStartupAgeDays"/> 天 / <see cref="MaxStartupFiles"/> 份，
///     见 <see cref="PruneStartupLogs"/>），且**带失败证据的那份永不删**（判据见 <see cref="StartupLogHasFailureEvidence"/>：
///     级别列 <c>[ERROR]</c>/<c>[FATAL]</c>/<c>[FAIL]</c>/<c>[WARN]</c> 与真实会写出的中文失败前缀 <c>[失败]</c> 等）。
///   · 独立脚本 Tools\clean-logs.ps1 是可手动执行/挂计划任务的**粗粒度覆盖**（按 -MaxDays/-MaxFiles
///     对整个 Logs 目录一刀切，不区分异常/启动日志）；本类的规则才是程序自己的策略，两者不必一致。
/// </summary>
public static class Logger
{
    /// <summary>
    /// 本次运行实际生效的日志目录，优先级**显式设置 &gt; 运行期覆盖 &gt; 默认**：
    ///   · 「显式设置」= <see cref="GuardPaths.LogDirExplicit"/>（<c>GuardPaths.Apply</c> 被显式调用过，
    ///     例如设置页「路径」页保存了自定义日志目录，或自检为了验保留策略临时把数据目录指到测试目录）；
    ///   · 「运行期覆盖」= <see cref="UseLogDirForRun"/>（自检/截图这类夹具把日志整体挪到 %TEMP%）；
    ///   · 都没有才回落 <see cref="GuardPaths.DefaultLogDir"/>。
    ///
    /// ⚠ 为什么显式设置必须压过运行期覆盖：自检那条「日志保留策略：超过 15 天的先删、总数不超过 50 份」
    ///   会自己造一个目录、`GuardPaths.Apply(testLogDir, …)` 显式指过去，再调 `CleanupOldLogs()`
    ///   断言**那个目录**被清干净。若运行期覆盖无条件压过一切，CleanupOldLogs 就会去清夹具目录、
    ///   测试目录一根手指都没被碰 ⇒ 既有断言 FAIL（实测「清掉 0 个，剩 61 个」就是这么来的）。
    ///   夹具要的是"**默认别落用户目录**"，而不是"抢掉所有显式指定" —— 所以覆盖只当默认值参与。
    /// </summary>
    private static string LogDir
    {
        get
        {
            string explicitDir = GuardPaths.LogDirExplicit;
            if (explicitDir.Length > 0) return explicitDir;
            return _runLogDir ?? GuardPaths.LogDir;
        }
    }

    /// <summary>测试夹具的日志目录覆盖（null = 用 <see cref="GuardPaths.LogDir"/>）。</summary>
    private static string? _runLogDir;

    /// <summary>
    /// 本次运行实际生效的日志目录（「日志」页 / 导出诊断 / 清理脚本都以它为准）。
    /// 与内部 <c>LogDir</c> 同源，自检可直接断言"夹具没把日志写进用户目录"。
    /// </summary>
    public static string EffectiveLogDir => LogDir;

    /// <summary>本次运行的日志目录是不是被夹具覆盖过（用于自检与排查）。</summary>
    internal static bool HasRunLogDirOverride => _runLogDir != null;

    /// <summary>
    /// 「日志到底写没写进去」的出口：日志目录不可用（只读盘 / 已拔出的移动盘 / 无权限）时的原因，
    /// **可用时为空串**。上层（设置页 / 日志页）据此可以告诉用户"这份日志没落盘"，而不是继续让界面
    /// 承诺「详细输出已记入日志，可在「日志」页查看」。
    ///
    /// 为什么需要它：<see cref="Init"/> 与 <see cref="WriteToFile"/> 原来都是 <c>catch { }</c>，
    /// 于是用户把日志目录指到不可写位置之后，<see cref="LogError"/> / <see cref="NoteDiagnosis"/> /
    /// <see cref="NoteCommandResult"/> 全部无声失败，用户去「日志」页什么都找不到、也没有任何提示。
    /// 语义边界：本属性只描述"日志"这条链路，**不改变**成功路径（成功时依旧不落盘，依旧为空串）。
    /// 一旦主目录恢复正常并真的写出一个文件，这里会自动回到空串。
    /// </summary>
    public static string UnavailableReason { get; private set; } = "";

    /// <summary>
    /// 主目录写不进去时本次实际改用的兜底日志目录（<c>%TEMP%\DSHGuard-Logs</c>；没启用时为空串）。
    /// 上层若要在「日志」页指向它，直接读本属性即可（见 <see cref="UnavailableReason"/> 的说明）。
    /// </summary>
    public static string FallbackLogDir { get; private set; } = "";

    /// <summary>兜底日志目录名（落在 %TEMP% 下；**只在主目录写不进去时**才会被创建）。</summary>
    private const string FallbackFolderName = "DSHGuard-Logs";

    private static string? _logFile;
    private static StreamWriter? _writer;
    private static readonly object _lock = new();

    /// <summary>异常日志文件名前缀。</summary>
    public const string ErrorPrefix = "异常-";
    /// <summary>历史遗留前缀（过程/细节/guard），启动时一并清理。</summary>
    private static readonly string[] LegacyPrefixes = { "过程-", "细节-", "guard-" };
    /// <summary>日志保留天数上限。</summary>
    public const int MaxAgeDays = 15;
    /// <summary>日志文件数量上限。</summary>
    public const int MaxFiles = 50;

    /// <summary>
    /// 启动诊断日志的**数量上限**（与异常日志的 <see cref="MaxFiles"/> 分开算）。
    ///
    /// 为什么单独一条、且明显更小：异常日志是排查素材，留久一点有用；启动日志只在"这次启动出问题"
    /// 时才有价值，正常启动的那一堆纯属堆积 —— 实测用户目录里就躺了 24 份 09-15~09-17 的正常启动日志
    /// （每份 100 B~662 B，全是"环境一行"），用户的原话就是"日志里还会记录正常启动的日志"。
    /// 取 10 份：足够覆盖"最近几次启动"的复盘需求（真要留证据的那份因为带失败证据永远不参与裁剪，见
    /// <see cref="PruneStartupLogs"/>），又不至于像 50 份那样一躺半个月。
    /// </summary>
    public const int MaxStartupFiles = 10;

    /// <summary>
    /// 启动诊断日志的**天数上限**，比异常日志的 <see cref="MaxAgeDays"/> 更短，理由同上。
    /// 这是"兜底"：正常启动日志在前一次启动收尾时就被 <see cref="DiscardStartupLogIfHealthy"/> 删了，
    /// 还能活过 3 天的只有两种 —— 硬崩留下的、带失败证据的。硬崩现场留 3 天够排查；
    /// 带失败证据的那份**不受本条约束**（见 <see cref="PruneStartupLogs"/>）。
    /// </summary>
    public const int MaxStartupAgeDays = 3;

    public static string CurrentLogFile => _logFile ?? "";
    public static string OpenLogFolderPath => LogDir;

    /// <summary>日志文件集合发生变化（首次因错误创建文件时触发）。</summary>
    public static event Action? LogFilesChanged;

    public static void Init()
    {
        try
        {
            if (!Directory.Exists(LogDir)) Directory.CreateDirectory(LogDir);
            RemoveLegacyLogs();
            CleanupOldLogs();
            try { LogFilesChanged?.Invoke(); } catch { }
            // 目录建得动、清理跑得完 ⇒ 之前记下的"不可用"若已被用户改回来，这里就该收回
            if (_logFile == null) UnavailableReason = "";
        }
        catch (Exception ex)
        {
            // 原来这里是空 catch：日志目录整个不可用时，从 Init 起就没留下任何痕迹（本单缺陷②的一半）
            NoteLogUnavailable("初始化", ex);
        }
    }

    /// <summary>
    /// 记一次"日志写不进去"：把原因留给 <see cref="UnavailableReason"/>，并**尽力**把这条事实写进日志
    /// （主目录不可写时就落兜底目录，并且不会再递归调用本方法）。
    /// </summary>
    private static void NoteLogUnavailable(string what, Exception ex)
    {
        string reason = $"{what}失败：{ex.GetType().Name}: {ex.Message}";
        UnavailableReason = reason;
        try
        {
            FallbackLogDir = PrepareFallbackDir();
            if (FallbackLogDir.Length == 0) return;
            TryAppendFallback($"[{DateTime.Now:HH:mm:ss.fff}] [WARN] 日志目录不可用（{LogDir}）：{reason}");
        }
        catch { }
    }

    /// <summary>准备兜底目录（<c>%TEMP%\DSHGuard-Logs</c>）；拿不到可写目录时返回空串。</summary>
    private static string PrepareFallbackDir()
    {
        try
        {
            string dir = Path.Combine(ProcessEnv.UserTempDir, FallbackFolderName);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return dir;
        }
        catch { return ""; }
    }

    /// <summary>
    /// 往兜底目录追加一行（**不碰** <c>_writer</c> / <c>_logFile</c>，因此不会与主链路的状态互相污染）。
    /// 兜底目录同样写不进去时静默返回 —— 此时已无路可走，但 <see cref="UnavailableReason"/> 仍然如实非空。
    /// </summary>
    private static void TryAppendFallback(string line)
    {
        try
        {
            string dir = FallbackLogDir.Length > 0 ? FallbackLogDir : PrepareFallbackDir();
            if (dir.Length == 0) return;
            FallbackLogDir = dir;
            File.AppendAllText(Path.Combine(dir, "日志不可用记录.txt"), line + Environment.NewLine, Encoding.UTF8);
        }
        catch { }
    }

    /// <summary>
    /// 把**本次运行**的日志目录切到 <paramref name="dir"/>（自检/截图等测试夹具用）。
    /// 必须在 <see cref="Init"/> 之前调用：一旦日志文件已打开，这里会先 <see cref="Close"/> 再换目录，
    /// 免得同一进程里两个目录的写句柄互相打架。
    /// 传 null / 空串 = 恢复默认（<see cref="GuardPaths.LogDir"/>）。
    /// ⚠ 只改 Logger 自己落到哪儿；<see cref="GuardPaths.LogDir"/> 的语义**保持不变**（设置页 / 日志页 / 导出诊断照旧指向用户真实目录）。
    /// </summary>
    public static void UseLogDirForRun(string? dir)
    {
        lock (_lock)
        {
            string? next = string.IsNullOrWhiteSpace(dir) ? null : dir!.Trim();
            if (string.Equals(_runLogDir, next, StringComparison.OrdinalIgnoreCase)) return;
            // 换目录前先收掉旧句柄，否则 _writer 还指着上一个目录的文件
            try { _writer?.Close(); } catch { }
            _writer = null;
            _logFile = null;
            try { _startWriter?.Close(); } catch { }
            _startWriter = null;
            _startLogFile = null;
            _runLogDir = next;
            _hasFailureEvidence = false;
            _startupDirty = false;
            _startupFailed = false;
            // 换了目录就重新起算"写不写得进去"：主目录的状态不跟着跨目录漂
            UnavailableReason = "";
            FallbackLogDir = "";
        }
    }

    /// <summary>删除历史遗留的「过程- / 细节- / guard-」日志，返回删除数量。</summary>
    public static int RemoveLegacyLogs()
    {
        int removed = 0;
        try
        {
            if (!Directory.Exists(LogDir)) return 0;
            foreach (var f in new DirectoryInfo(LogDir).GetFiles("*.log"))
            {
                if (f.Name.StartsWith(ErrorPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                bool legacy = LegacyPrefixes.Any(p => f.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
                if (!legacy) continue;
                try { f.Delete(); removed++; } catch { }
            }
        }
        catch { }
        return removed;
    }

    /// <summary>普通日志：不落盘，保留空实现以避免改动既有调用点。</summary>
    public static void Log(string message) { }

    // ═══ 引擎输出环形缓冲（只在内存）═══
    // 异常日志原则上只在出错时落盘；但"引擎为什么没起来"的答案全在它自己的输出里，
    // 全部丢弃会让远端机器（别人的电脑）无从排查。这里保留最后一小段，
    // 只在启动失败时随异常日志/弹窗一起给出。
    private const int RunOutputKeep = 120;
    private static readonly Queue<string> _runOutput = new();
    private static readonly object _runLock = new();

    /// <summary>记一条引擎输出（仅内存，定长覆盖）。</summary>
    public static void NoteRunOutput(string line)
    {
        try
        {
            lock (_runLock)
            {
                _runOutput.Enqueue(line.Length > 500 ? line.Substring(0, 500) + " …(截断)" : line);
                while (_runOutput.Count > RunOutputKeep) _runOutput.Dequeue();
            }
        }
        catch { }
    }

    /// <summary>引擎输出尾部（最多 maxLines 行，按时间先后排列；没有则返回空串）。</summary>
    public static string RunOutputTail(int maxLines = 30)
    {
        try
        {
            lock (_runLock)
            {
                if (_runOutput.Count == 0) return "";
                return string.Join(Environment.NewLine,
                    _runOutput.Skip(Math.Max(0, _runOutput.Count - maxLines)));
            }
        }
        catch { return ""; }
    }

    /// <summary>自检用：清空引擎输出缓冲。</summary>
    internal static void ClearRunOutputForTest()
    {
        lock (_runLock) _runOutput.Clear();
    }

    // ══════════════ 引擎 stderr 分诊：已知无害 vs 真问题（纯函数，自检可直接断言）══════════════
    //
    // 现场问题（用户报告）：日志里出现
    //     [stderr] Error: AttachConsole failed
    //     [stderr]     at Object.<anonymous> (…\node_modules\node-pty\lib\conpty_console_list_agent.js:13:26)
    //     …（node 内部栈）…
    // 它来自**引擎自己的依赖 node-pty**，不是本壳代码：那个 agent 要 `AttachConsole` 去列控制台进程，
    // 而引擎是本壳以 `CreateNoWindow = true`（无控制台）拉起的 ⇒ 必然失败。
    // 关键事实：**它无害** —— agent 是 `child_process.fork` 出来的独立进程，失败只让它自己以非 0 退出，
    // 引擎既没崩也没少功能（本机日志里这条从未伴随过启动失败）。
    // 但本壳把引擎 stderr 原样摆进日志/事件栏 ⇒ 用户以为出故障了。所以这里做**分诊**而不是丢弃。
    //
    // 三条硬规矩：
    //   ① **失败关闭**：只对下表里逐字匹配的形态放行，其余（含任何没见过的 stderr、任何非 stderr 输出）
    //      一律当**真问题** —— 宁可多打扰，不可漏报真故障。
    //   ② **只降级、不销毁证据**：命中行原样进「日志」页实时面板（AppendLiveLog），**不进导出诊断包**（包内引擎输出取自 RunOutputTail，命中行按规矩③不入该缓冲）。
    //      它不被削减的只有两件事：不当「启动失败原因」、不写红色事件（改为灰色一行提示）。
    //   ③ **绝不参与启动失败判定**：命中的行不进 <see cref="NoteRunOutput"/>（启动失败弹窗的证据链），
    //      于是启动失败原因/弹窗里不会再出现这句无害的话。

    /// <summary>已知无害的引擎 stderr 形态（逐字包含匹配；`tokens` 必须**全部**命中，`anyOf` 命中其一即可）。</summary>
    public sealed record KnownStderrNoise(string Id, string Summary, string[] Tokens, string[]? AnyOf = null);

    /// <summary>
    /// 已知无害 stderr 清单 —— **每一条都必须有理由**，说不清是无害的一律不许收进来（留给"真问题"）。
    /// 收录依据：本机日志目录全量只读翻查（51 个日志）+ node-pty 1.1.0 源码。
    ///
    /// ⚠ 判据必须落在**那一行自己**身上：例如 node-pty 的报错首行就是光秃秃的
    /// <c>Error: AttachConsole failed</c>（"node-pty" 与 agent 路径只在**下一行**的栈帧里），
    /// 所以这里不能要求首行同时含"node-pty" —— 那样等于永远匹配不上（实测踩过）。
    /// </summary>
    public static readonly KnownStderrNoise[] KnownStderrNoises =
    {
        // ① 用户报告的那条。整段（首行 + 其后全部栈帧 + 收尾脚注）由 <see cref="ClassifyStderrStep"/> 一起归类。
        //    只认「Error:」+「AttachConsole failed」两个片段同时出现：该调用是 Win32 控制台 API，
        //    Node 生态里只有 pty 这类库会去连控制台；本壳自己从不调用它（见 ProcessManager 里的说明）。
        new("node-pty AttachConsole", "node-pty 的控制台探测子进程（引擎无控制台窗口，属预期）",
            new[] { "error:", "attachconsole failed" }),

        // ② 本机日志里最常见的 stderr 噪音（只读统计：24 + 21 + 6 次）。是**警告**而非错误：
        //    npm 不认识某些 env config，只在"下一个大版本"才会失效；命令本身可以完全正常。
        //    本壳给 pnpm 注入的放开策略变量（pnpm_config_*）会被 pnpm 转成 npm_config_* 传给 npm ⇒ 必然出现。
        new("npm Unknown env config", "npm 不认识某个环境变量配置项（仅提示，不影响本次命令）",
            new[] { "npm warn unknown env config" }),

        // ③ pnpm 的升级提示第二行。刻意**只认带 pnpm 标识的行**（见下面对 "Update available!" 的取舍）。
        new("pnpm changelog hint", "pnpm 升级提示里的 changelog 链接（仅提示）",
            new[] { "changelog:", "pnpm.io" }),

        // ④ 同族噪音：pnpm 自升级提示行（这一行自己就写着 pnpm，可判定）。
        new("pnpm self-update hint", "pnpm 自升级提示行（仅提示）",
            new[] { "to update, run:", "pnpm self-update" }),

        // ⑤ Node 自己打印的实验性 API 警告。纯提示，不影响功能。
        //    刻意要求 Node 的固有前缀 "(node:"（真实格式：`(node:12345) ExperimentalWarning: …`）——
        //    只认那一个词太松：`Error: ExperimentalWarning: …` 这类真报错会被误吞（失败关闭）。
        new("Node ExperimentalWarning", "Node 实验性 API 警告（仅提示）",
            new[] { "(node:", "experimentalwarning:" }),

        // ── 以下形态本机日志里也**反复出现**，但**刻意不收进清单**（失败关闭：说不清就不放行）→ 仍按"真问题"处理：
        //    · `Update available! 12.3.4 → 12.4.2.` —— 实测该行**不含任何 pnpm 字样**（pnpm 的标识在它的下一行），
        //      单看这一行无法与其它工具的升级提示区分 ⇒ 宁可多打扰。它没有 "error"/"not found" 字样，
        //      所以只会进"引擎输出尾部"证据，不会变成启动失败原因。
        //    · `Downloading foo: 7.98 MB/27.33 MB` 之类的进度行：属 stdout（本程序只分诊 stderr），不涉及。
    };

    /// <summary>
    /// 命中已知无害噪音则返回对应条目，否则返回 <c>null</c>（= 按真问题处理）。
    /// <paramref name="line"/> 为空/空白时返回 <c>null</c>（空行不是证据，但也不算"已知无害"）。
    /// </summary>
    public static KnownStderrNoise? MatchKnownNoise(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        string s = line!;
        foreach (var n in KnownStderrNoises)
        {
            bool all = true;
            foreach (string t in n.Tokens)
                if (!s.Contains(t, StringComparison.OrdinalIgnoreCase)) { all = false; break; }
            if (!all) continue;
            if (n.AnyOf is { Length: > 0 })
            {
                bool any = false;
                foreach (string t in n.AnyOf)
                    if (s.Contains(t, StringComparison.OrdinalIgnoreCase)) { any = true; break; }
                if (!any) continue;
            }
            return n;
        }
        return null;
    }

    /// <summary>
    /// 是不是 V8/Node 调用栈的**续行**（首行之外的"同一段报错"）。
    /// 形态固定：<c>    at …</c>、<c>    at async …</c>，或源码回显后的 <c>      ^</c> 指针对齐行。
    /// 判据刻意**窄**：只有"整行 trim 后以 <c>at </c> 起头"或"整行去掉空格后只剩 ^"才算 ——
    /// 正因如此，紧随其后又打印的 <c>Error: something else</c> 不会被视为续行（也就不会被吞掉）。
    /// </summary>
    public static bool IsStackFrameLine(string? line)
    {
        try
        {
            if (string.IsNullOrEmpty(line)) return false;
            string t = line!.Trim();
            if (t.Length == 0) return false;
            if (t.StartsWith("at ", StringComparison.Ordinal)) return true;
            return t[0] == '^' && t.Trim('^').Trim().Length == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// 是不是 Node **崩溃报告的收尾行**：整行形如 <c>Node.js v24.20.0</c>
    /// （node 打印未捕获异常后固定带的版本脚注）。
    /// 它自己不含任何可判定信息，**只有紧跟在一个已知无害段之后**才随段归无害（见 ①b）；
    /// 单独出现时仍按真问题处理 —— 这是失败关闭的一部分。
    /// </summary>
    public static bool IsNodeVersionFooter(string? line)
    {
        try
        {
            if (string.IsNullOrEmpty(line)) return false;
            string t = line!.Trim();
            if (!t.StartsWith("Node.js v", StringComparison.OrdinalIgnoreCase)) return false;
            string rest = t.Substring("Node.js v".Length);
            if (rest.Length == 0) return false;
            foreach (char c in rest)
                if (!char.IsDigit(c) && c != '.') return false;
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// **整块**分诊的一步（纯函数、无状态）：把"当前这一段"的状态与下一行喂进来，返回这一步的结果。
    /// 为什么必须"整块"而不是"逐行"：`AttachConsole failed` 抛错后，node 会把调用栈逐行打到 stderr，
    /// 那些栈帧行本身就**不含**任何可识别字样（`    at Object.&lt;anonymous&gt; (…conpty_console_list_agent.js:13:26)`），
    /// 逐行判就全部落到"真问题"里去了。
    ///
    /// **这一段到哪里结束**：由"下一行第一个既不是栈帧、也不是收尾脚注的行"决定 ——
    ///   · 还在无害块里、本行是栈帧（<see cref="IsStackFrameLine"/>）⇒ 继续归无害，块继续；
    ///   · 还在无害块里、本行是收尾脚注（<see cref="IsNodeVersionFooter"/>）⇒ 归无害，**块到此结束**；
    ///   · 一旦遇到别的行 ⇒ 本块立即结束，该行**重新独立判定**（绝不继承上一段的结论）。
    /// 所以 `Error: something else` 这种后续无关行只会按它自己的样子判，不会被首行的结论带走。
    /// </summary>
    /// <param name="inBenignBlock">上一行处理完之后，是否仍处在"已知无害"块里。</param>
    /// <param name="line">本行原文（可空）。</param>
    /// <returns>
    /// <c>Benign</c>=本行按"已知无害"处理（不参与失败判定）；
    /// <c>InBenignBlock</c>=下一行要传进来的新状态（真问题行一律复位为 false，避免误吞后续行）。
    /// </returns>
    public static (bool Benign, bool InBenignBlock) ClassifyStderrStep(bool inBenignBlock, string? line)
    {
        // ① 仍在无害块里，且本行是栈帧 ⇒ 这一段（连同它的调用栈）一起归无害
        if (inBenignBlock && IsStackFrameLine(line)) return (true, true);

        // ①b 仍在无害块里，本行是崩溃报告收尾脚注 ⇒ 也归无害，但这一段到此为止
        if (inBenignBlock && IsNodeVersionFooter(line)) return (true, false);

        // ② 命中清单 ⇒ 本行开一个新的无害块（后续栈帧会被 ① 接着吞掉）
        if (MatchKnownNoise(line) != null) return (true, true);

        // ③ 其余一律真问题，并把块状态复位
        return (false, false);
    }

    /// <summary>已被判为"已知无害"的行数（自检可断言；也是事件栏只提示一次的计数依据）。</summary>
    private static int _knownNoiseCount;

    /// <summary>本次运行已判为已知无害的行数。</summary>
    public static int KnownNoiseCount => _knownNoiseCount;

    /// <summary>记一条"引擎 stderr 但已知无害"。**不落盘、不进 <see cref="RunOutputTail"/>**（见本节顶部三条硬规矩）。</summary>
    public static void NoteKnownNoise(string line)
    {
        try { System.Threading.Interlocked.Increment(ref _knownNoiseCount); } catch { }
    }

    /// <summary>自检用：复位"已知无害"计数。</summary>
    internal static void ResetKnownNoiseCountForTest() => _knownNoiseCount = 0;

    /// <summary>
    /// 给用户看的一句话提示（事件栏用；不含任何路径/栈，避免又变成一条"吓人的长日志"）。
    /// ⚠ 界面文案纪律：**不许出现技术名词**（node-pty / npm / pnpm 之类只属于日志原文与
    ///   <see cref="KnownStderrNoises"/> 的理由注释）；那条 stderr 原文照样原样进「日志」页，细节不丢。
    /// </summary>
    public const string KnownNoiseNote = "引擎输出里的少量无害提示，可忽略；原文见「日志」页。";

    /// <summary>
    /// 过程记录：**刻意不落盘**（不是"还没实现"，是既定策略，见类头第一条）。
    /// 保留空实现是为了不动既有调用点；运行时观测由界面右下「事件信息」承担。
    ///
    /// ⚠ 调用方注意：调了本方法**一点痕迹都不会留下** —— 现有 11 处真实调用点（「毛玻璃」10 处 +
    ///   <c>DiagnoseStartupFailure</c> 的诊断文本 1 处，后者主要消费者是它的返回值）全都只是"顺手记一笔"，
    ///   真正的现场由别的入口写。要真留痕，按代价挑入口，别指望本方法：
    ///     · <see cref="NoteDiagnosis"/>：<c>[WARN] 单行</c>写进**异常日志**，不置失败标记、不弹窗，代价最小；
    ///     · <see cref="NoteStartup(string)"/>：写进**启动日志**（算启动现场，健康启动收尾时会被删掉）；
    ///     · <see cref="LogError"/> / <see cref="NoteFailure"/>：多行 + **置失败标记** ⇒
    ///       本次「启动-*.log」不再按"健康"清理（见 <see cref="DiscardStartupLogIfHealthy"/>），只该用于真出错。
    ///
    /// ⚠ 要改本方法的语义，必须**同时**改自检第 10 条的两条断言：「过程记录与普通日志都不落盘（磁盘只留报错）」
    ///   与「切换日夜模式不写日志」（后者按字面扫 <see cref="EffectiveLogDir"/> 下所有 .log 找探针文本）。
    ///   另外先想清楚代价：「毛玻璃」那 10 处在拖动/改尺寸时会高频触发，直接接上 <see cref="NoteDiagnosis"/>
    ///   既是日志洪水，又会因 <c>WriteToFile</c> → <c>EnsureWriter</c> 让**健康启动也建出「异常-*.log」**。
    /// </summary>
    public static void LogDiagnosis(string text) { }

    private static void WriteToFile(string line)
    {
        try
        {
            // 这一行只要带着异常/致命标记，就说明本次运行**真出了事** ⇒ 启动诊断日志不许再被当作"健康"删掉
            if (line.Contains("[ERROR]", StringComparison.Ordinal) ||
                line.Contains("[FATAL]", StringComparison.Ordinal))
                _hasFailureEvidence = true;

            lock (_lock)
            {
                EnsureWriter();
                _writer?.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {line}");
                _writer?.Flush();
            }
            // 真写进去了 ⇒ 此前记下的"不可用"作废（用户把目录改回来之后不该继续报故障）
            if (_writer != null) UnavailableReason = "";
        }
        catch (Exception ex)
        {
            // 原来这里是空 catch：日志目录在被指向只读盘 / 已拔出的移动盘之后，
            // LogError / NoteDiagnosis / NoteCommandResult **全部无声失败**，而界面同时还在承诺
            // 「详细输出已记入日志，可在「日志」页查看」⇒ 用户去日志页什么都找不到（本单缺陷②）。
            // 现在：① 把原因挂到 UnavailableReason 让上层看得见；② 尽力把这一行落进兜底目录。
            NoteLogUnavailable("写入", ex);
            TryAppendFallback($"[{DateTime.Now:HH:mm:ss.fff}] [WARN] 主日志目录写入失败，本条未落原目录：{line}");
        }
    }

    // ═══ 命令输出落盘闸门与限量（纯函数，自检可直接断言，不需要起进程）═══

    /// <summary>
    /// 单条命令的每个输出流最多落盘的行数。超出的部分被 <see cref="TruncateCommandOutput"/> 折叠成一行省略号。
    /// </summary>
    public const int CommandLogMaxLines = 60;

    /// <summary>
    /// 单条命令的每个输出流最多落盘的字符数（约 4 KB）。行数与字符数**先到先截**。
    /// 定这两个上限的原因：dsh --dump-config 一次能吐几百行 JSON，
    /// 整段抄进日志就是用户说的"内容特别多"（实测单个异常日志 30 KB、其中 14/16 条是成功命令）。
    /// </summary>
    public const int CommandLogMaxChars = 4000;

    /// <summary>被截断时插在头尾之间的省略行（保留原文案格式，便于肉眼比对）。</summary>
    public const string CommandLogTruncationMark = "…（已截断）…";

    /// <summary>
    /// 「命令结束」这类诊断日志该不该落盘：**只有失败（退出码非 0）才落**；
    /// 退出码为 0 = 成功，成功路径不留日志（用户明确要求）。
    /// 纯函数，便于自检直接断言；<c>null</c>（进程没能启动/没拿到退出码）按失败处理，照样落盘。
    /// </summary>
    public static bool ShouldPersistCommandLog(int? exitCode) => exitCode is not 0;

    /// <summary>
    /// 命令输出限量的**纯函数**实现：头尾各取一段，中间以 <see cref="CommandLogTruncationMark"/> 标出；
    /// 行数与字符数**先到先截**（行数优先）。
    /// 未超限时**原样返回**（不 Trim：调用方若已自行截断，这里应当是无损的）。
    /// </summary>
    public static string TruncateCommandOutput(string? text,
        int maxLines = CommandLogMaxLines, int maxChars = CommandLogMaxChars)
    {
        try
        {
            if (string.IsNullOrEmpty(text)) return "";
            string s = text!;

            // ① 先按行数限量：头尾各留一半，中间折叠
            var lines = s.Replace("\r\n", "\n").Split('\n');
            if (lines.Length > maxLines)
            {
                int head = Math.Max(1, maxLines / 2);
                int tail = Math.Max(1, maxLines - head);
                s = string.Join("\n", lines.Take(head))
                    + $"\n{CommandLogTruncationMark}（原文 {lines.Length} 行，只留头尾各 {head}/{tail} 行）\n"
                    + string.Join("\n", lines.Skip(lines.Length - tail));
            }

            // ② 再按字符数限量：同样保头也保尾（报错行几乎都压在末尾）
            if (s.Length > maxChars)
            {
                int half = Math.Max(1, maxChars / 2);
                s = s.Substring(0, half)
                    + $"\n{CommandLogTruncationMark}（原文 {s.Length} 字符，只留头尾各 {half} 字符）\n"
                    + s.Substring(s.Length - half);
            }
            return s;
        }
        catch { return "(输出截断失败)"; }
    }

    /// <summary>
    /// 记一条**命令诊断**：只有失败（退出码非 0）才落盘，且两个流都按
    /// <see cref="CommandLogMaxLines"/> 行 / <see cref="CommandLogMaxChars"/> 字符截断。
    /// 成功（退出码 0）时什么也不做 —— 这是"异常日志记录得特别频繁"的主要来源。
    /// </summary>
    /// <param name="what">动作描述（如「命令结束：npx …」）。</param>
    /// <param name="exitCode">命令退出码；null = 没能启动/没拿到，按失败处理。</param>
    /// <param name="stdout">标准输出原文（可为 null/空）。</param>
    /// <param name="stderr">标准错误原文（可为 null/空）。</param>
    /// <param name="streamLabel">两个流的前缀文案，默认「stdout 头尾」/「stderr 头尾」。</param>
    /// <returns>是否真的落盘了（自检可断言成功路径返回 false）。</returns>
    public static bool NoteCommandResult(string what, int? exitCode,
        string? stdout, string? stderr, string streamLabel = "头尾")
    {
        if (!ShouldPersistCommandLog(exitCode)) return false;
        var sb = new StringBuilder();
        sb.Append($"{what} → 退出码={(exitCode.HasValue ? exitCode.Value.ToString() : "(未知)")}");
        sb.Append($"\nstdout {streamLabel}：{TruncateCommandOutput(stdout)}");
        sb.Append($"\nstderr {streamLabel}：{TruncateCommandOutput(stderr)}");
        NoteDiagnosis(sb.ToString());
        return true;
    }

    private static void EnsureWriter()
    {
        if (_writer != null) return;
        _logFile = Path.Combine(LogDir, $"{ErrorPrefix}{DateTime.Now:yyyyMMdd-HHmmss}.log");
        _writer = new StreamWriter(_logFile, append: true, Encoding.UTF8) { AutoFlush = true };
        _writer.WriteLine("═══════════════════════════════════════");
        _writer.WriteLine($"DSHGuard 异常日志  {DateTime.Now}");
        _writer.WriteLine($"Exe: {Environment.ProcessPath}");
        try { LogFilesChanged?.Invoke(); } catch { }
    }

    public static void LogError(string context, Exception ex)
    {
        WriteToFile($"[ERROR] {context}: {ex.GetType().Name}: {ex.Message}");
        WriteToFile($"  StackTrace: {ex.StackTrace}");
        if (ex.InnerException != null) WriteToFile($"  Inner: {ex.InnerException.Message}");
    }

    /// <summary>
    /// 写入异常日志并弹窗提示，用于致命错误。
    /// dialogText 是给人看的短话；details 是给排查用的全量内容（只落盘、不进弹窗）——
    /// 弹窗一旦塞进几十行堆栈就会高过屏幕，按钮被顶出可见区域、用户无法关闭（现场 bug）。
    /// **提示走非模态**：模态框被加载动画/渲染饿住时会点不动，用户只能杀进程（现场 bug）。
    /// </summary>
    public static void ShowError(string title, string dialogText, string? details = null)
    {
        WriteToFile($"[FATAL] {title}: {dialogText}");
        if (!string.IsNullOrWhiteSpace(details)) WriteToFile(details!);
        try
        {
            GuardDialog.ShowNonModal(dialogText, $"DSH 守护壳 · {title}", MessageBoxImage.Error);
        }
        catch { }
    }

    /// <summary>写异常日志但**不弹窗**（调用方自己组织对话框，例如带"换版本"按钮的那种）。</summary>
    public static void NoteFailure(string title, string dialogText, string? details = null)
    {
        WriteToFile($"[FATAL] {title}: {dialogText}");
        if (!string.IsNullOrWhiteSpace(details)) WriteToFile(details!);
    }

    /// <summary>诊断留痕：写进异常日志但不当作异常、不弹窗（例如"弹窗超过 2 分钟没关掉"这种现场证据）。</summary>
    public static void NoteDiagnosis(string text) => WriteToFile($"[WARN] {text}");

    /// <summary>普通信息弹窗（不落盘）。同样走非模态：只告知，不阻塞。</summary>
    public static void ShowInfo(string title, string message)
    {
        try
        {
            GuardDialog.ShowNonModal(message, $"DSH 守护壳 · {title}", MessageBoxImage.Information);
        }
        catch { }
    }

    /// <summary>列出历史异常日志（新→旧）。</summary>
    public static string[] ListLogFiles()
    {
        try
        {
            if (!Directory.Exists(LogDir)) return Array.Empty<string>();
            return Directory.GetFiles(LogDir, $"{ErrorPrefix}*.log")
                .OrderByDescending(File.GetLastWriteTime)
                .ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    // ══════════════ 启动诊断日志（每次启动一条可诊断记录） ══════════════
    // 内测反馈里"第一次安装启动不了"这类问题，光有异常日志不够：
    // 还得知道这次用的是哪个版本、哪个端口、哪个配置文件、等了多久、清理与补链做了什么。
    // 这里把每次启动尝试的完整现场记成一条，日志页可直接选、也可一键打包发给作者。

    /// <summary>启动诊断日志前缀（与异常日志分开，日志页能按来源选）。</summary>
    public const string StartPrefix = "启动-";

    /// <summary>
    /// 本次运行是否出现过异常/致命证据（写过 [ERROR]/[FATAL]，或被显式标记为不健康）。
    /// 只要为 true，启动诊断日志就**绝不删除**（这是当初加它的原因：启动失败要有完整证据）。
    /// </summary>
    private static bool _hasFailureEvidence;

    /// <summary>本次是否已经写过启动诊断（没写过就不必谈"删不删"）。</summary>
    private static bool _startupDirty;

    /// <summary>
    /// 本次运行**确实判定过启动失败**（进程没拉起 / 等不到端口就绪）——由
    /// <see cref="MarkStartupFailed"/> 置位，是 <see cref="DiscardStartupLogIfEngineRunning"/> 唯一认的"别删"依据。
    ///
    /// 为什么不能只看 <see cref="HasFailureEvidence"/>：那个标志太宽 —— 任何 <c>LogError</c>、
    /// 任何 <see cref="MarkStartupUnhealthy"/>，乃至**上一次尝试**留下的痕迹，都会让它保持为 true。
    /// 于是出现用户看到的那一幕：第一次尝试失败 → 自动重试成功 → 引擎跑起来了，日志里
    /// 「[自检]/[阶段]/[拉起]」成对出现两遍、末尾还写着「[成功] 端口 3080 就绪」，
    /// 却因为第一次尝试的证据一直亮着而**一份不差地留在盘上**（"还是会记录一堆正常的启动日志"）。
    /// 只有"启动到底成不成"才该决定留不留：成了 ⇒ 不留；没成 ⇒ 一个字不少，且带着现场被
    /// <see cref="StartupLogHasFailureEvidence"/> 按文件内容永久保护。
    /// </summary>
    private static bool _startupFailed;

    private static string? _startLogFile;
    private static StreamWriter? _startWriter;

    public static string CurrentStartupLogFile => _startLogFile ?? "";

    /// <summary>本次运行有没有异常/失败证据（自检可断言成功路径为 false）。</summary>
    public static bool HasFailureEvidence => _hasFailureEvidence;

    /// <summary>
    /// 自检用：把"失败证据"复位，便于在同一个进程里**分别**断言
    /// "健康 ⇒ 删掉"与"出过事 ⇒ 一定留"这两条相反的路
    /// （否则前面跑过的任何失败用例都会把这个标志位永久点亮）。
    /// 刻意**不动** <c>_startupDirty</c>：那会让已经打开的启动日志文件变成没人认领的孤儿。
    /// </summary>
    internal static void ResetFailureEvidenceForTest()
    {
        lock (_lock) { _hasFailureEvidence = false; }
    }

    /// <summary>
    /// 显式标记"这次启动不健康"（[ERROR]/[FATAL] 之外的情形：引擎拉起失败、被回退、自动诊断判定异常…）。
    /// 标记过之后 <see cref="DiscardStartupLogIfHealthy"/> 就不会删启动日志。
    /// </summary>
    public static void MarkStartupUnhealthy() => _hasFailureEvidence = true;

    /// <summary>
    /// 启动流程**正常走完**时调用：本次若没出过任何失败/异常证据，就把这次写下的「启动-*.log」删掉
    /// （用户要求：启动没问题就不应当留日志）。返回是否真的删了。
    ///
    /// 为什么是"先写后删"而不是"攒在内存里再决定"：硬崩（任务管理器强杀 / .NET 域崩）时内存缓冲
    /// 会一起没掉，而"第一次安装启动不了"恰恰是那类现场 ⇒ 必须留得住。所以宁可先落盘（AutoFlush），
    /// 健康时再撤 —— 只要没走到本方法，文件就是完整的。
    /// 判断依据只有 <see cref="HasFailureEvidence"/> 一个：任何 [ERROR]/[FATAL]、任何
    /// <see cref="NoteStartup(string, StartupLevel)"/> 的非 <see cref="StartupLevel.Note"/> 级别、
    /// 任何 <see cref="MarkStartupUnhealthy"/> 都会让它保持为 true ⇒ 失败现场一定留得下。
    /// （跨运行的保护由 <see cref="PruneStartupLogs"/> 按**文件内容**判，见 <see cref="StartupLogHasFailureEvidence"/>。）
    /// </summary>
    public static bool DiscardStartupLogIfHealthy()
    {
        lock (_lock)
        {
            try
            {
                if (!_startupDirty) return false;          // 本次没写过启动诊断，无需处理
                if (_hasFailureEvidence) return false;     // 出过事 ⇒ 证据必须留下
                string? file = _startLogFile;
                try { _startWriter?.Close(); } catch { }
                _startWriter = null;
                _startLogFile = null;
                _startupDirty = false;
                if (string.IsNullOrEmpty(file) || !File.Exists(file)) return false;
                File.Delete(file);
                try { LogFilesChanged?.Invoke(); } catch { }
                return true;
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// 判定"本次启动**失败**"（引擎没能拉起 / 等不到端口就绪）时调用：此后本次的「启动-*.log」一律留住。
    /// 与 <see cref="MarkStartupUnhealthy"/> 的分工：那一位说"这次运行出过事"（回退提示 / 版本履历要它），
    /// 这一位只说"启动没成"（日志留不留要它）—— 自动重试成功时前者仍为 true，后者却已让
    /// <see cref="DiscardStartupLogIfEngineRunning"/> 把这份日志按"成功启动"撤掉。
    /// </summary>
    public static void MarkStartupFailed() => _startupFailed = true;

    /// <summary>
    /// 引擎**确实跑起来了**（端口就绪 / 沿用外部引擎）时调用：撤掉本次的「启动-*.log」。
    ///
    /// 与 <see cref="DiscardStartupLogIfHealthy"/> 的区别只有一个：**不看 <see cref="HasFailureEvidence"/>**。
    /// 那个标志会把"任何 LogError / 任何 MarkStartupUnhealthy / 上一次尝试的痕迹"都算成"出事"，
    /// 结果是引擎明明起来了、日志里还写着「[成功] 端口 … 就绪」的**正常启动记录**照样留盘
    /// （用户报的"还是会记录一堆正常的启动日志"，1.3.55 实测）。
    /// 这里只认一个事实：<see cref="MarkStartupFailed"/> 有没有被调过。
    ///   · 没调过 = 启动成功（哪怕中间有过一次失败尝试、或开壳时某次后台请求报了错，临时证据在
    ///     「异常-*.log」与诊断摘要里一字不少）⇒ 删；
    ///   · 调过 = 启动真失败 ⇒ 不删，整份现场连同一行 [失败] 原样留下，并被
    ///     <see cref="StartupLogHasFailureEvidence"/> 按文件内容永久保护。
    /// 与"先写后删"的取舍：硬崩时本方法根本走不到，文件仍在（<see cref="DiscardStartupLogIfHealthy"/> 的注释已论证）。
    /// </summary>
    public static bool DiscardStartupLogIfEngineRunning()
    {
        lock (_lock)
        {
            try
            {
                if (!_startupDirty) return false;           // 本次没写过启动诊断，无需处理
                if (_startupFailed) return false;           // 启动真失败 ⇒ 现场一个字都不许少
                string? file = _startLogFile;
                try { _startWriter?.Close(); } catch { }
                _startWriter = null;
                _startLogFile = null;
                _startupDirty = false;
                if (string.IsNullOrEmpty(file) || !File.Exists(file)) return false;
                File.Delete(file);
                try { LogFilesChanged?.Invoke(); } catch { }
                return true;
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// 启动诊断行的**级别列**（落到行首的 ASCII 标记，例如 <c>[FAIL]</c>）。
    ///
    /// 为什么要有这一列：<see cref="PruneStartupLogs"/> 判断"这份日志是不是失败现场"原来只能靠
    /// 正文里的中文方括号前缀去猜，而真正的失败现场写的是 <c>[失败]</c>（见 <c>MainWindow.xaml.cs</c>
    /// 的 NoteStartup 调用点），猜的集合里却没有它 ⇒ **唯一的失败现场被当成健康日志删掉**（本单 H1）。
    /// 有了级别列之后，判据只认这一列，不必再维护一张"哪些中文前缀算失败"的清单。
    ///
    /// 兼容性：老格式文件（没有级别列）继续被 <see cref="HasFailureMark"/> 的中文前缀规则覆盖，
    /// 一份都不会失效；新签名只是**新增**，原有单参 <see cref="NoteStartup(string)"/> 的落盘字节不变。
    /// </summary>
    public enum StartupLevel
    {
        /// <summary>普通过程记录（[阶段]/[环境]/[自检]/[成功]/[拉起]…）。**不算**失败证据。</summary>
        Note,
        /// <summary>警告：没失败但值得留意。保守起见**算**失败证据（多留几 KB 无害，错删现场不可逆）。</summary>
        Warn,
        /// <summary>失败（真实写法 <c>[失败] 类型=… 等待=… 秒 重试过=…</c>）。**算**失败证据。</summary>
        Fail,
        /// <summary>致命。**算**失败证据。</summary>
        Fatal
    }

    /// <summary>写一条启动诊断记录（多行用 \n 即可；不弹窗、不影响主流程）。</summary>
    public static void NoteStartup(string block)
    {
        try
        {
            lock (_lock)
            {
                EnsureStartWriter();
                _startWriter?.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {block}");
                _startWriter?.Flush();
                _startupDirty = true;
            }
        }
        catch { }
    }

    /// <summary>
    /// 写一条**带级别**的启动诊断记录：行首多一列 ASCII 级别标记（<c>[NOTE]</c>/<c>[WARN]</c>/
    /// <c>[FAIL]</c>/<c>[FATAL]</c>），正文 <paramref name="block"/> **原样保留**（可读性不变）。
    /// 落盘形如：<c>[2026-09-16 12:00:29.257] [FAIL] [失败] 类型=ProcessExited …</c>
    ///
    /// 除 <see cref="StartupLevel.Note"/> 外一律点亮相 <see cref="HasFailureEvidence"/> ——
    /// 保证"收尾时没删的"与"裁剪时保护的"始终是同一批文件（见 <see cref="DiscardStartupLogIfHealthy"/>）。
    ///
    /// ⚠ 真实的失败现场调用点在 <c>MainWindow.xaml.cs</c>（<c>NoteStartup($"[失败] 类型=…")</c>），
    ///   那个文件不在本单写权限内 ⇒ 这里是**已就绪的升级入口**，尚未接线；接线后判据即可只认级别列。
    ///   在那之前，判据同时认级别列与真实中文前缀两条路（见 <see cref="HasFailureMark"/>）。
    /// </summary>
    public static void NoteStartup(string block, StartupLevel level)
    {
        try
        {
            lock (_lock)
            {
                EnsureStartWriter();
                _startWriter?.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {LevelTag(level)} {block}");
                _startWriter?.Flush();
                _startupDirty = true;
            }
        }
        catch { }
        // 写在锁外：_hasFailureEvidence 是个 bool 标志位，赋值本身原子，且不能被 catch 吞掉语义
        if (level != StartupLevel.Note) _hasFailureEvidence = true;
    }

    /// <summary>级别列的落盘写法（判据 <see cref="HasFailureMark"/> 认同一批标记）。</summary>
    private static string LevelTag(StartupLevel level) => level switch
    {
        StartupLevel.Warn => "[WARN]",
        StartupLevel.Fail => "[FAIL]",
        StartupLevel.Fatal => "[FATAL]",
        _ => "[NOTE]"
    };

    private static void EnsureStartWriter()
    {
        if (_startWriter != null) return;
        if (!Directory.Exists(LogDir)) Directory.CreateDirectory(LogDir);
        string file = Path.Combine(LogDir, $"{StartPrefix}{DateTime.Now:yyyyMMdd-HHmmss}.log");
        _startWriter = new StreamWriter(file, append: true) { AutoFlush = true };
        _startLogFile = file;
    }

    /// <summary>列出启动诊断日志（新→旧）。</summary>
    public static string[] ListStartupLogs()
    {
        try
        {
            if (!Directory.Exists(LogDir)) return Array.Empty<string>();
            return Directory.GetFiles(LogDir, $"{StartPrefix}*.log")
                .OrderByDescending(File.GetLastWriteTime)
                .ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>日志页用：异常日志 + 启动诊断日志合并，新→旧。</summary>
    public static string[] ListAllLogs()
        => ListLogFiles().Concat(ListStartupLogs())
            .OrderByDescending(f => { try { return File.GetLastWriteTime(f); } catch { return DateTime.MinValue; } })
            .ToArray();


    /// <summary>读取日志文件尾部（默认最后 400 行），超长行截断，避免 UI 卡死。</summary>
    public static string ReadLogFile(string path, int maxLines = 400, int maxLineChars = 2000)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return "(暂无日志文件 —— 本次运行未发生错误)";
            // 必须显式 FileShare.ReadWrite：日志文件正被本进程的写句柄占用（share=Read），
            // 而 File.ReadAllLines 默认 share=Read，不允许并发写入，会抛出
            // "The process cannot access the file ... being used by another process"。
            var lines = new List<string>();
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            {
                while (sr.ReadLine() is { } line) lines.Add(line);
            }
            var tail = lines.Count > maxLines ? lines.Skip(lines.Count - maxLines) : lines;
            var sb = new StringBuilder();
            foreach (var line in tail)
                sb.AppendLine(line.Length > maxLineChars ? line.Substring(0, maxLineChars) + " …(截断)" : line);
            return sb.ToString();
        }
        catch (Exception ex) { return $"(读取日志失败: {ex.Message})"; }
    }

    /// <summary>
    /// 清理日志。异常日志与启动诊断日志**各算各的**，两条路互不干扰：
    ///   ① 异常日志：超过 <see cref="MaxAgeDays"/> 天的先删，剩余仍超过 <see cref="MaxFiles"/> 个则从最旧删起（原样保留）；
    ///   ② 启动诊断日志：**整条交给 <see cref="PruneStartupLogs"/>**（<see cref="MaxStartupAgeDays"/> /
    ///      <see cref="MaxStartupFiles"/>，且带失败证据的一份永不删）。
    /// 返回删除数量。
    /// </summary>
    public static int CleanupOldLogs()
    {
        int removed = 0;
        try
        {
            if (!Directory.Exists(LogDir)) return 0;
            var cutoff = DateTime.Now.AddDays(-MaxAgeDays);

            // ⚠ 启动日志**不参与**这两轮：它们有自己的年龄线（3 天 < 15 天）与配额，
            //   而且"带失败证据的永不删"这条在这一轮里没法表达 —— 15 天一到就会先把证据删掉，
            //   等 PruneStartupLogs 再想去保护就已经晚了。所以这里只认异常日志。
            var errors = new DirectoryInfo(LogDir).GetFiles("*.log")
                .Where(f => f.Name.StartsWith(ErrorPrefix, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var f in errors)
            {
                if (f.LastWriteTime < cutoff)
                {
                    try { f.Delete(); removed++; } catch { }
                }
            }

            var remain = errors.Where(f => f.Exists)
                .OrderByDescending(f => f.LastWriteTime)
                .ToList();
            foreach (var f in remain.Skip(MaxFiles))
            {
                try { f.Delete(); removed++; } catch { }
            }

            // ② 启动日志：独立配额 + 独立年龄线 + 失败证据保护
            removed += PruneStartupLogs();
        }
        catch { }
        return removed;
    }

    /// <summary>
    /// 这份启动日志里有没有**失败证据**。
    ///
    /// ⚠ 判据必须与"启动日志真实会写出来的内容"对齐 —— 这正是本单 H1 修复的核心：
    ///   原判据只认 <c>[ERROR]</c>/<c>[FATAL]</c>，而真实的失败现场写的是
    ///   <c>[失败] 类型=ProcessExited 等待=… 秒 重试过=…</c>（<c>MainWindow.xaml.cs</c> 的 NoteStartup），
    ///   **从不**写 <c>[ERROR]</c>/<c>[FATAL]</c>（那两个标记只落在**异常日志**里，由 <see cref="WriteToFile"/> 写）。
    ///   实测 24 份真实 <c>启动-*.log</c>：含 <c>[失败]</c> 的 1 份、含 <c>[ERROR]</c>/<c>[FATAL]</c> 的 **0 份**
    ///   ⇒ 原判据对那份**唯一**的失败现场返回 false ⇒ 先过 3 天年龄线、再按 10 份配额从最旧删起 ⇒ 现场永久丢失。
    ///
    /// 现在认三类，**只收窄到"行首级别/前缀"，不做全文乱猜**：
    ///   ① 级别列：<c>[ERROR]</c> <c>[FATAL]</c> <c>[FAIL]</c> <c>[WARN]</c>
    ///      （<c>[FAIL]</c>/<c>[WARN]</c> 来自 <see cref="NoteStartup(string, StartupLevel)"/> 的级别列）；
    ///   ② <see cref="LevelTag"/> 写出的那一列，与 <see cref="WriteToFile"/> 点亮
    ///      <see cref="HasFailureEvidence"/> 的那一处保持同一语义；
    ///   ③ 老格式的中文失败前缀：<c>[失败]</c> <c>[错误]</c> <c>[异常]</c> <c>[警告]</c>
    ///      <c>[超时]</c> <c>[中断]</c> <c>[致命]</c> —— 老文件（无级别列）靠这一条继续被保护。
    ///
    /// 全文件搜而不是只看首行：宁可多留（正文里恰好提到这些标记 ⇒ 多留一份几 KB 的日志），
    /// 也绝不错删（漏判的代价是把启动失败的唯一现场永久删掉）。读不了（占用/损坏/无权限）同样返回 true。
    /// </summary>
    public static bool StartupLogHasFailureEvidence(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
            // 启动日志实际 100 B~几 KB；限读 64 KB 是防损坏/异常大的文件把清理拖慢
            const int maxChars = 64 * 1024;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var buf = new char[maxChars];
            int n = sr.Read(buf, 0, buf.Length);
            return HasFailureMark(new string(buf, 0, n));
        }
        catch
        {
            return true;   // 读不了 ⇒ 保守当作"有证据"（绝不因为读失败而删掉可能是唯一的现场）
        }
    }

    /// <summary>
    /// 失败证据标记的**全量清单**（行首级别列 + 老格式中文前缀）。
    ///
    /// 收录依据是"**实测**启动日志真实会出现哪些前缀"，不是凭空枚举：
    ///   · 级别列 <c>[ERROR]</c>/<c>[FATAL]</c>：<see cref="WriteToFile"/> 的两处唯一写法（落在异常日志）；
    ///   · <c>[FAIL]</c>/<c>[WARN]</c>：<see cref="NoteStartup(string, StartupLevel)"/> 的级别列；
    ///   · <c>[失败]</c>：**真实失败现场的唯一写法**（实测唯一那份失败日志里出现 7 次）；
    ///   · 其余中文前缀：产品当前没写过，但语义上只能是"出事了"，先收进来防未来回归
    ///     （收错的代价只是一份几 KB 的日志多留，漏收的代价是删掉唯一现场，两者不对称）。
    ///
    /// <c>[警告]</c>/<c>[WARN]</c> **算**失败证据：取舍是刻意偏保守的 —— 实测这类前缀当前**一次都没出现过**
    /// （24 份启动日志的全集是 <c>[阶段] [拉起] [环境] [成功] [自检] [失败] [外部引擎]</c>），
    /// 所以"把警告当证据"在真实数据上**零误报成本**；而只要将来真有警告被写进启动日志，
    /// 一份带警告的现场显然比一份纯成功的日志更值得留。
    /// </summary>
    private static readonly string[] FailureMarks =
    {
        "[ERROR]", "[FATAL]", "[FAIL]", "[WARN]",
        "[失败]", "[错误]", "[异常]", "[警告]", "[超时]", "[中断]", "[致命]"
    };

    /// <summary>
    /// 整份文本里有没有失败证据标记（见 <see cref="FailureMarks"/>）。
    /// 用 <see cref="StringComparison.Ordinal"/> 而不是忽略大小写：这些标记都是本程序自己写的固定写法，
    /// 忽略大小写会让引擎输出里的 <c>[Error]</c>/<c>[warn]</c> 这类第三方文本被算成证据（多留但语义变糊）。
    /// </summary>
    private static bool HasFailureMark(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (string mark in FailureMarks)
            if (text.Contains(mark, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// 启动诊断日志的独立保留策略（**只删启动日志**，异常日志一根指头都不碰）。返回删除数量。
    ///
    /// ① 带失败证据的先挑出来**整轮不参与裁剪** —— 这是 <see cref="DiscardStartupLogIfHealthy"/> 的
    ///    "健康的删、有问题的留"语义在裁剪侧的延续：这份日志是启动失败时的唯一现场，删了就再也拿不回来。
    /// ② 其余（正常启动 / 硬崩）先按 <see cref="MaxStartupAgeDays"/> 天过期，
    /// ③ 仍超过 <see cref="MaxStartupFiles"/> 份则**从最旧删起**（排序依据 <c>LastWriteTime</c>，与列表接口一致）。
    /// </summary>
    public static int PruneStartupLogs()
    {
        int removed = 0;
        try
        {
            if (!Directory.Exists(LogDir)) return 0;

            // 排序一进来就定下来：读文件内容与删除都不改变这份清单的相对顺序
            var startup = new DirectoryInfo(LogDir).GetFiles($"{StartPrefix}*.log")
                .OrderByDescending(f => f.LastWriteTime)
                .ToList();

            // ① 先分类，判据是**文件内容**（不是内存里的标志位）：本方法也可能被下一次启动调用，
            //    那时判断的是"上一次留下的"文件，内存标志早没了。
            var protect = startup.Where(f => StartupLogHasFailureEvidence(f.FullName)).ToList();
            var healthy = startup.Where(f => !protect.Contains(f)).ToList();   // ← protect 在前

            // ② 过期的正常启动日志
            var cutoff = DateTime.Now.AddDays(-MaxStartupAgeDays);
            var fresh = new List<FileInfo>();
            foreach (var f in healthy)
            {
                if (f.LastWriteTime < cutoff)
                {
                    try { f.Delete(); removed++; }
                    catch { fresh.Add(f); }      // 删不掉（被占用）⇒ 仍算"留着的"，下一轮再算配额
                }
                else fresh.Add(f);
            }

            // ③ 仍超配额的，从最旧删起
            foreach (var f in fresh.Skip(MaxStartupFiles))
            {
                try { f.Delete(); removed++; } catch { }
            }
        }
        catch { }
        return removed;
    }

    public static void Flush() { try { _writer?.Flush(); } catch { } }

    /// <summary>
    /// 关掉写句柄。必须把 <c>_writer</c> 置空：否则 EnsureWriter 的 "非空就返回" 会让后续写入
    /// 继续打到已释放的 StreamWriter 上，异常又被 WriteToFile 的空 catch 吞掉 → 退出阶段的日志静默丢失。
    /// </summary>
    public static void Close()
    {
        lock (_lock)
        {
            try { _writer?.Close(); } catch { }
            _writer = null;
            try { _startWriter?.Close(); } catch { }     // 启动诊断日志同理：必须置空，否则后续写入静默丢失
            _startWriter = null;
        }
    }
}
