using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Net.NetworkInformation;

namespace DSHGuard;

public class ProcessManager
{
    // ═══ 启动方式：npx（不依赖本地克隆目录） ═══
    // 引擎工作目录按"可移植优先"解析：环境变量 → 用户目录下的 .dsh → exe 同级。
    // 不写死任何机器专有路径，安装到别的电脑也能直接跑。
    public static readonly string WorkDir = PickWorkDir();
    /// <summary>旧版默认命令（迁移用：设置中保存该值视为未自定义）。</summary>
    public const string LegacyLaunchCommand = "npx --yes @deepseek-ai/dsh@latest web";

    /// <summary>默认启动命令：--yes 跳过安装确认；版本说明符由版本记忆决定（未固定用 @latest，已固定则用固定版本）。</summary>
    public static string LaunchCommand => VersionMemory.LaunchCommand;

    /// <summary>
    /// 构造完整启动参数（baseCommand 为空时使用默认 npx 命令）。
    /// --no-open：dsh web 默认自行打开浏览器，与守护壳的「就绪弹窗」重复，故统一由守护壳控制。
    /// --port   ：端口必须显式传给 DSH；否则改端口只影响守护壳的检测，DSH 仍在默认端口监听并导致等待超时。
    /// </summary>
    public static string BuildArgs(int port, string? baseCommand = null)
    {
        string cmd = string.IsNullOrWhiteSpace(baseCommand) ? LaunchCommand : baseCommand.Trim();
        return $"{cmd} --no-open --port {port}";
    }

    /// <summary>
    /// 解析引擎工作目录（可移植，不依赖任何机器专有路径）：
    /// ① 环境变量 DSHGUARD_WORKDIR；② 用户目录下的 .dsh（DSH 自己的根）；③ exe 同级。
    /// </summary>
    private static string PickWorkDir()
    {
        try
        {
            string? env = Environment.GetEnvironmentVariable("DSHGUARD_WORKDIR");
            if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env!.Trim()))
                return env.Trim();
        }
        catch { }

        try
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(profile)) return Path.Combine(profile, ".dsh");
        }
        catch { }

        return AppContext.BaseDirectory;
    }

    /// <summary>启动方式预览（诊断/日志用）。</summary>
    public static string GetLaunchPreview(int port, string? baseCommand = null) =>
        $"{BuildArgs(port, baseCommand)}\r\n  工作目录: {WorkDir}";

    /// <summary>
    /// 清理 0 字节的 package.json：该文件会导致 pnpm/npm 解析失败
    /// （"EOF while parsing a value at line 1 column 0"）。启动前执行清理。
    /// </summary>
    private static void PurgeBrokenPackageJson(string dir)
    {
        try
        {
            string pj = Path.Combine(dir, "package.json");
            if (File.Exists(pj) && new FileInfo(pj).Length == 0)
            {
                File.Delete(pj);
                Logger.Log($"已清理损坏的空 package.json: {pj}");
            }
        }
        catch (Exception ex) { Logger.Log($"清理 package.json 跳过: {ex.Message}"); }
    }

    private Process? _process;

    /// <summary>
    /// 护住"取 _process 快照 / 换引用 / 清引用"以及与它配套的三个处理器字段。
    /// 为什么必须有它：引擎自己的退出回调（<see cref="OnEngineProcessExited"/>）与停止路径
    /// 可能同时处理同一个对象，没有互斥就可能两个线程一起动手 —— 双重释放，或者把新一轮
    /// 启动刚装上的引擎对象误释放。锁内只做极短的动作（取快照、换引用、清引用），
    /// 绝不在锁内等进程退出、也绝不在锁内释放句柄。
    /// </summary>
    private readonly object _processLock = new();

    /// <summary>
    /// 当前引擎对象的三个回调处理器。**必须由字段持有**：stdout / stderr 是匿名 lambda，
    /// 不留引用就无从退订，那个闭包会被已死的 Process 一直持有 —— 它捕获的正是订阅方
    /// <c>OutputReceived</c>（主窗口订阅后从不退订）⇒ 陈旧引擎的输出会混进新一轮的日志面板。
    /// "已退出"处理器是实例方法，同样登记在字段里，让三个订阅的退订成对、可核对。
    /// </summary>
    private DataReceivedEventHandler? _stdoutHandler;
    private DataReceivedEventHandler? _stderrHandler;
    private EventHandler? _exitedHandler;

    /// <summary>
    /// 进程是否已退出。不能直接读取 _process?.HasExited：Process 被 Dispose 后再读 HasExited
    /// 会抛 InvalidOperationException（"No process is associated with this object"），
    /// 重复停止时会触发，故统一按"已退出"处理。
    /// </summary>
    public bool HasExited
    {
        get
        {
            Process? p;
            lock (_processLock) { p = _process; }
            if (p == null) return true;
            try { return p.HasExited; }
            catch (InvalidOperationException) { return true; }
            catch { return true; }
        }
    }

    /// <summary>
    /// 引擎进程自己退出时（含等待启动时秒退）**退订自己的两个输出回调**，从根上断掉"陈旧输出"。
    /// <para>
    /// 这里只退订、**不及时 Dispose**：退出回调与"启动失败清理"是并发的两条路，而清理那一路
    /// 正要读 <c>TrackedPid</c>（<c>_process?.Id</c>）去收本程序拉起的进程树。
    /// 一旦这边先把它 Dispose，<c>TrackedPid</c> 就会抛
    /// <c>InvalidOperationException</c>（"No process is associated with this object"）——
    /// 那会把"启动失败清理"直接掀掉。句柄不靠这里收：它随下一次启动
    /// （<see cref="ReleasePreviousProcess"/>）或停止路径
    /// （<see cref="ReleaseStoppedEngine"/>）释放，两个时机都确定在"没人再需要这个 PID"之后。
    /// </para>
    /// <para>
    /// 只摘输出这两个、**留着 Exited**：它就是本回调自己，后续清理处那一句
    /// <c>p.Exited -= _exitedHandler</c> 才有实际对象可摘（不会退化成空操作）。
    /// </para>
    /// <para>
    /// 顺带解释"陈旧输出不再混进面板"：订阅在这里退掉之后，这个已退出的旧对象即便
    /// 因异步读线程收尾再触发 <c>OutputDataReceived</c>，也打不到任何处理器上 ——
    /// 那个捕获了订阅方 <c>OutputReceived</c> 的闭包不会再被调用，也就不会再往主窗口的
    /// 「日志」实时面板追加一行。
    /// </para>
    /// </summary>
    private void OnEngineProcessExited(object? sender, EventArgs e)
    {
        try
        {
            if (sender is not Process p) return;
            p.OutputDataReceived -= _stdoutHandler;
            p.ErrorDataReceived -= _stderrHandler;
        }
        catch { }
    }

    /// <summary>
    /// 旧的引擎对象交给新一次启动处理掉：摘引用、退订三个回调、放掉句柄。
    /// <para>
    /// 取代原先那个"还活着就 Dispose"的写法 —— 那会把活着的旧引擎变成"没有关联进程"的僵尸对象
    /// （其后的 PID 复用甚至可能让 taskkill 打到无辜进程）。判据严格限定为**非 null 且确定已退出**：
    /// 停止路径管着活着的进程，这里一手都不伸。
    /// </para>
    /// <para>
    /// 三个订阅在这里一起退掉（stdout / stderr / Exited），与 <see cref="Start"/> 里的三处
    /// <c>+=</c> 一一成对。
    /// </para>
    /// </summary>
    private void ReleasePreviousProcess()
    {
        Process? old;
        lock (_processLock) { old = _process; }
        if (old == null) return;

        bool exited;
        try { exited = old.HasExited; }
        catch (InvalidOperationException) { exited = true; }   // 已释放 → 只需再收一次尾
        catch { return; }                                      // 读不出来：保守不动，交给停止路径
        if (!exited) return;                                   // 还在跑：引用必须留着

        // ⚠ 确认"已退出"才摘引用。活着的进程绝不能从 _process 上摘掉：
        //   那会让 ForceKill 找不到它（关壳时引擎就可能残留下来）。
        //   引用相等才清：别的线程若已换上新一轮的引擎对象，不能把它误清掉。
        lock (_processLock) { if (ReferenceEquals(_process, old)) _process = null; }

        try
        {
            old.OutputDataReceived -= _stdoutHandler;
            old.ErrorDataReceived -= _stderrHandler;
            old.Exited -= _exitedHandler;
            old.Dispose();
        }
        catch { }
    }

    /// <summary>
    /// 订阅方（主窗口）丢弃本实例前的收尾：把手上那个**已退出**的引擎对象连同它的句柄放掉。
    /// <para>
    /// 为什么需要它：进程对象与句柄的释放时机挂在"下一次启动"或"停止成功"上，而这两条路都要求
    /// 同一个实例还被继续使用 —— 启动失败后主窗口直接换一个全新 ProcessManager，
    /// 旧实例连同它那个已退出的引擎对象就再没人碰了，句柄只能等 GC 终结器。
    /// </para>
    /// <para>
    /// 引擎还活着时一手都不伸（判据在 <see cref="ReleasePreviousProcess"/> 里）：活着的进程
    /// 归停止路径管，这里绝不能把它变成"没有关联进程"的对象。
    /// </para>
    /// </summary>
    public void ReleaseExitedProcess() => ReleasePreviousProcess();

    /// <summary>
    /// 停止确认成功后的收尾：退订三个回调 + 释放句柄。
    /// <para>
    /// 为什么必须在**停止成功后**也做：此前只有强杀那条路（<see cref="ForceKill"/>）会 Dispose，
    /// 首档 Ctrl+C 成功退出这条最常走的路从不释放 —— "启动 → 停止 → 再启动"反复操作就一路攒对象
    /// （连同它的 <c>OutputDataReceived</c> 闭包，只能等 GC 终结器）。
    /// </para>
    /// <para>
    /// 对象已释放时按"已释放"跳过，故 <see cref="GracefulStop"/> 的重复调用不会抛。
    /// </para>
    /// </summary>
    private static void ReleaseStoppedEngine(Process? p)
    {
        if (p == null) return;
        try
        {
            bool exited;
            try { exited = p.HasExited; }
            catch (InvalidOperationException) { return; }   // 已释放 → 无需再清
            catch { return; }
            if (!exited) return;                            // 没真退出就不动它

            p.Dispose();
        }
        catch { }
    }

    public event EventHandler<string>? OutputReceived;

    /// <summary>
    /// 引擎 stderr 行的固定前缀（订阅方靠它区分 stdout/stderr，也靠它做"已知无害"分诊）。
    /// **两侧必须同源**：这里写、订阅方读同一个常量，免得改了一处漏一处。
    /// </summary>
    public const string StderrTag = "[stderr] ";

    // ⚠ 这里本来还声明着四个控制台 API（AttachConsole / FreeConsole / SetConsoleCtrlHandler /
    //    GenerateConsoleCtrlEvent）外加配套的委托与枚举，全部**没有任何调用点**，属于死代码，已删除。
    //    它们的来历是 GracefulStop 的「方案 B（控制台 API 发 Ctrl+C）」，那段在本文件
    //    GracefulStop 的注释里写着"已移除"，理由是 GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0) 会把
    //    Ctrl+C 发给共享同一控制台的所有进程（包括可能与引擎同树的守护壳自身），风险过高；
    //    现在只剩「管道写 \x03 + taskkill 兜底」两档。
    //
    //    与用户报告的那句 `Error: AttachConsole failed` **毫无关系**：本壳是 WPF 的 WinExe，
    //    CreateNoWindow = true 拉起引擎，自己从不调用 AttachConsole，那条报错是**引擎自己的依赖
    //    node-pty** 打印的（它 fork 出的 conpty_console_list_agent 要连控制台来列控制台进程）。
    //    ⚠ 切勿因为"名字一样"而把这条 stderr 当成本壳的问题去修 —— 那样只会把一条无害提示
    //    改成一条真故障。分诊逻辑见 <see cref="Logger.ClassifyStderrStep"/>。

    /// <summary>占用端口的监听进程（用于终止"外部启动"的引擎）。</summary>
    public sealed record PortOwner(int Pid, string Name);

    /// <summary>
    /// 列出指定端口上处于 LISTENING 状态的进程（解析 netstat -ano；本地地址需以 :port 结尾，
    /// 避免把 30800 之类同前缀端口计入）。
    /// </summary>
    public static List<PortOwner> FindPortOwners(int port)
    {
        var list = new List<PortOwner>();
        try
        {
            // netstat 是真 exe，直接启动，不再经 cmd /c（旧写法 cmd /c netstat -ano）。
            var psi = new ProcessStartInfo
            {
                FileName = "netstat.exe",
                Arguments = "",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            psi.ArgumentList.Add("-ano");
            using var p = Process.Start(psi);
            if (p == null) return list;

            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);

            foreach (var raw in output.Split('\n'))
            {
                var parts = raw.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 4) continue;
                if (!parts[0].Equals("TCP", StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(parts[^2], "LISTENING", StringComparison.OrdinalIgnoreCase)) continue;

                // 本地地址形如 0.0.0.0:3080 / [::]:3080 / 127.0.0.1:3080
                string local = parts[1];
                int colon = local.LastIndexOf(':');
                if (colon < 0 || local.Substring(colon + 1) != port.ToString()) continue;

                if (!int.TryParse(parts[^1], out int pid) || pid <= 0) continue;
                if (list.Any(x => x.Pid == pid)) continue;

                string name = "?";
                // 只借一个名字，用完立刻还句柄（GetProcessById 每次都会新开一个进程句柄，
                // 不 Dispose 就得等 GC 终结器 —— 启动失败清理会连着调好几轮）
                try { using var owner = Process.GetProcessById(pid); name = owner.ProcessName; } catch { }
                list.Add(new PortOwner(pid, name));
            }
        }
        catch (Exception ex) { Logger.LogError("ProcessManager.FindPortOwners", ex); }
        return list;
    }

    /// <summary>自查：需要监听进程的信息与"是否可安全终止"。</summary>
    public static List<(int Pid, string Name, bool HostsSelf)> InspectPortOwners(int port)
    {
        var list = new List<(int, string, bool)>();
        try
        {
            foreach (var o in FindPortOwners(port))
                list.Add((o.Pid, o.Name, IsHostedBy(o.Pid)));
        }
        catch (Exception ex) { Logger.LogError("InspectPortOwners", ex); }
        return list;
    }

    /// <summary>
    /// 目标进程是否正在"托管"守护壳（本程序是它自身、它的子孙，或它是本程序的祖先）。
    /// 三重判定：自身 → 启动时记下的祖先集合 → 实时父链 → 反向枚举其子孙。
    /// 中间进程退出会让父链断裂，故必须多路兜底，否则会漏判成"安全"而误杀自己。
    /// </summary>
    public static bool IsHostedBy(int pid)
    {
        if (pid <= 0) return false;
        if (pid == Environment.ProcessId) return true;
        if (IsSelfOrAncestor(pid)) return true;
        return DescendantsContainSelf(pid);
    }

    /// <summary>反向判定：目标的现存子孙里是否有本进程（我们是活的，只要能连上就必然命中）。</summary>
    public static bool DescendantsContainSelf(int targetPid)
    {
        try
        {
            int self = Environment.ProcessId;
            var pairs = ProcessParents();
            var children = new Dictionary<int, List<int>>();
            foreach (var (id, parent) in pairs)
            {
                if (parent <= 0) continue;
                if (!children.TryGetValue(parent, out var bucket))
                    children[parent] = bucket = new List<int>();
                bucket.Add(id);
            }
            var queue = new Queue<int>();
            var seen = new HashSet<int>();
            queue.Enqueue(targetPid);
            while (queue.Count > 0)
            {
                int cur = queue.Dequeue();
                if (!seen.Add(cur)) continue;
                if (cur == self) return true;
                if (children.TryGetValue(cur, out var kids))
                    foreach (var k in kids) queue.Enqueue(k);
            }
        }
        catch (Exception ex) { Logger.LogError("DescendantsContainSelf", ex); }
        return false;
    }

    /// <summary>
    /// 是否可以对目标进程用整树终止（/T）。始终返回 false：本程序永不用 /T，
    /// 因为被终止的引擎很可能正是守护壳的祖先，整树终止会把守护壳一起带走。
    /// </summary>
    public static bool SafeToTreeKill(int pid) => false;

    /// <summary>taskkill 参数（纯函数，便于自检；不执行）。刻意不提供 /T。</summary>
    public static string KillArgs(int pid, bool force) => $"/PID {pid}{(force ? " /F" : "")}";

    // ══════════════ 安全执行层（不再把裸字符串交给 cmd.exe）══════════════
    //
    // 历史缺陷（对抗性压测证实）：命令以 cmd /c <command> <args> 的裸字符串形态启动，
    // cmd 会解释 & | ^ > < %VAR% ⇒ 任何插值含 & 即可注入第二条命令
    //（压测实例：… add x&calc --registry … ⇒ calc 被当独立命令执行）。
    //
    // 修复（执行层三层防线，参数永不落回裸字符串拼接）：
    //   ① TokenizeCommandLine —— 按 Windows 规则把命令行拆成 token（引号内是整体）；
    //   ② FindOnPath —— 自己按 PATHEXT 解析命令；**.cmd/.bat 永不直启**（cmd 包装脚本会把
    //      参数里的元字符重新解释一遍 —— 独立工程实测：直启 .cmd + ArgumentList，
    //      "x&calc" 照样把 calc 拉起来，即 BatBadBut 类漏洞；.NET 只对空格/引号加引号）；
    //   ③ TryResolveCmdShim —— 读 .cmd shim 的脚本原文，解引用出背后真正要跑的东西
    //      （npm 系：node.exe + 入口 js；pnpm：pnpm.exe），直启真 exe，参数逐 token 到达，
    //      全程无 cmd 参与（& 在 node/pnpm 的 argv 里只是普通字符，彻底失去注入面）；
    //   ④ 兜底 ApplyCmdFallback —— 实在解析不出才 cmd /d /s /c + 整体引号包裹
    //     （/S 语义下 cmd 只剥最外层一对引号，内容无法从中间截断出第二条命令）；
    //      仅限程序自控内容，当前调用点全部能走 ③，此兜底常态不可达。

    /// <summary>按 Windows 规则把命令行拆成 token（引号内的内容是整体；纯函数，便于自检）。</summary>
    public static List<string> TokenizeCommandLine(string commandLine)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine)) return tokens;
        var cur = new System.Text.StringBuilder();
        bool inQuotes = false;
        foreach (char c in commandLine)
        {
            if (c == '"') { inQuotes = !inQuotes; }
            else if (!inQuotes && (c == ' ' || c == '\t'))
            {
                if (cur.Length > 0) { tokens.Add(cur.ToString()); cur.Clear(); }
            }
            else { cur.Append(c); }
        }
        if (cur.Length > 0) tokens.Add(cur.ToString());
        return tokens;
    }

    private static IEnumerable<string> PathDirs()
    {
        string path = "";
        try { path = Environment.GetEnvironmentVariable("PATH") ?? ""; } catch { }
        foreach (string dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (dir.Length == 0) continue;
            string expanded = dir;
            try { expanded = Environment.ExpandEnvironmentVariables(dir); } catch { }
            yield return expanded;
        }
    }

    private static readonly string[] DefaultPathExts = { ".COM", ".EXE", ".BAT", ".CMD" };

    private static string[] PathExts()
    {
        try
        {
            string[] exts = (Environment.GetEnvironmentVariable("PATHEXT") ?? "")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return exts.Length > 0 ? exts : DefaultPathExts;
        }
        catch { return DefaultPathExts; }
    }

    private static bool IsCmdScript(string path)
        => path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

    /// <summary>是否可直接作为子进程映像（.exe/.com；无扩展名的 sh 脚本、.ps1 等一律不算）。</summary>
    private static bool IsExecutableImage(string path)
        => path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".com", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 在 PATH 里解析命令（where 的同款结果）。cmdScriptsOnly=false 时**拒绝 .cmd/.bat**
    /// （cmd 包装脚本会重新解释参数里的元字符，绝不能作为启动目标 —— 见 <see cref="TryResolveCmdShim"/>）；
    /// cmdScriptsOnly=true 时只找 .cmd/.bat（供 shim 解引用）。返回 null = 没找到。
    /// </summary>
    public static string? FindOnPath(string name, bool cmdScriptsOnly = false)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string bare = name.Trim().Trim('"');
        if (bare.Contains('\\') || bare.Contains('/'))
        {
            // 名字本身带路径：只做存在性检查（形态合法性由调用方把关）
            return File.Exists(bare) ? bare : null;
        }

        string[] exts = cmdScriptsOnly
            ? new[] { ".CMD", ".BAT" }
            : PathExts().Where(e => !e.Equals(".CMD", StringComparison.OrdinalIgnoreCase)
                                 && !e.Equals(".BAT", StringComparison.OrdinalIgnoreCase)).ToArray();

        foreach (string dir in PathDirs())
        {
            try
            {
                // 名字已带扩展名 ⇒ 精确匹配优先（npx.cmd → npx.cmd）
                string exact = Path.Combine(dir, bare);
                if (File.Exists(exact) && IsCmdScript(exact) == cmdScriptsOnly)
                    return exact;
                if (!Path.HasExtension(bare))
                {
                    foreach (string ext in exts)
                    {
                        string candidate = Path.Combine(dir, bare + ext);
                        if (File.Exists(candidate)) return candidate;
                    }
                }
            }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// 解析 .cmd 包装脚本背后真正要启动的东西（只读文件、不执行、不联网）。
    /// 两种已知形态（本机实测 npx.cmd / pnpm.cmd）：
    ///   ① <c>"&lt;node.exe&gt;" "&lt;入口.js&gt;" %*</c> —— node + 入口 js（npm 系 shim）；
    ///   ② <c>"&lt;…&gt;\pnpm.exe"   %*</c> —— 真 exe（pnpm shim）。
    /// 脚本里的 <c>SET "VAR=…"</c> 批处理局部变量与 <c>%~dp0</c> 会被展开。
    /// 解析不出（形态超出上面两种）⇒ false，调用方走 cmd 兜底，**绝不直启 .cmd 本身**。
    /// </summary>
    public static bool TryResolveCmdShim(string shimPath, out string exe, out List<string> prefixArgs)
    {
        exe = "";
        prefixArgs = new List<string>();
        try
        {
            if (string.IsNullOrWhiteSpace(shimPath) || !File.Exists(shimPath)) return false;
            string text = File.ReadAllText(shimPath);
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            string scriptDir = Path.GetFullPath(Path.GetDirectoryName(Path.GetFullPath(shimPath)) ?? ".");

            // ① 收集 SET 赋值（都是脚本局部变量；%~dp0 在收的时候就展开成脚本目录）。
            //   同一变量可能被赋**多次**（npx.cmd 的 IF EXIST 块会重赋 NPX_CLI_JS），
            //   全部保留 —— 替换时按「值落在真实存在的文件上」近似 IF EXIST 的运行时语义。
            var vars = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            string dp0 = scriptDir.EndsWith('\\') ? scriptDir : scriptDir + "\\";
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.StartsWith('@')) line = line[1..].Trim();
                if (!line.StartsWith("SET", StringComparison.OrdinalIgnoreCase)) continue;
                string rest = line[3..].Trim();
                string name, value;
                if (rest.StartsWith('"'))
                {
                    int end = rest.IndexOf('"', 1);
                    int eq = end > 0 ? rest.IndexOf('=', 1, end - 1) : -1;
                    if (eq < 0) continue;
                    name = rest[1..eq];
                    value = rest[(eq + 1)..end];
                }
                else
                {
                    int eq = rest.IndexOf('=');
                    if (eq < 0) continue;
                    name = rest[..eq].Trim();
                    value = rest[(eq + 1)..].Trim().Trim('"');
                }
                value = value.Replace("%~dp0", dp0, StringComparison.OrdinalIgnoreCase);
                if (!vars.TryGetValue(name, out var list))
                {
                    list = new List<string>();
                    vars[name] = list;
                }
                list.Add(value);
            }

            // ② 最后一条以 %* 结尾的非注释行 = 真正的启动动作
            string? execLine = lines.Select(l => l.Trim()).LastOrDefault(l =>
                l.Length > 0 && !l.StartsWith("::") && !l.StartsWith("REM", StringComparison.OrdinalIgnoreCase)
                && l.EndsWith("%*", StringComparison.Ordinal));
            if (execLine == null) return false;

            string cmdPart = execLine[..^2].Trim();     // 剥掉尾部 %*
            foreach (var kv in vars)
            {
                string needle = "%" + kv.Key + "%";
                if (!cmdPart.Contains(needle, StringComparison.OrdinalIgnoreCase)) continue;
                // 多次赋值：取「是真实存在文件」的最后一个（近似 IF EXIST 分支的运行时取值）；都没有就用首次赋值
                string pick = kv.Value.LastOrDefault(File.Exists) ?? kv.Value[0];
                cmdPart = cmdPart.Replace(needle, pick, StringComparison.OrdinalIgnoreCase);
            }
            cmdPart = cmdPart.Replace("%~dp0", dp0, StringComparison.OrdinalIgnoreCase);

            // 含 for 变量（%%F）⇒ 该值只能由 cmd 运行时求值，静态解析不了 ⇒ 整行放弃，
            // 改用 SET 收集到的**不含 %% 变量**的静态 .js 候选（文件名含 shim 主名的优先，
            // 例如 npx.cmd → npx-cli.js；绝不选 npm-prefix.js 这类探测脚本）。
            if (cmdPart.Contains("%%"))
            {
                string stem = Path.GetFileNameWithoutExtension(shimPath);
                string? staticJs = vars.Values.SelectMany(v => v)
                    .Where(v => !v.Contains("%%")
                                && (v.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
                                    || v.EndsWith(".cjs", StringComparison.OrdinalIgnoreCase)))
                    .OrderByDescending(v => Path.GetFileNameWithoutExtension(v)
                        .Contains(stem, StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(v => Path.GetFileName(v)
                        .EndsWith("-cli.js", StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault(File.Exists);
                if (staticJs == null) return false;
                string? nodeExe = FindOnPath("node");
                if (nodeExe == null) return false;
                exe = Path.GetFullPath(nodeExe);
                // 规范化路径（shim 的 %~dp0\ 形态会产生 nodejs\\node_modules 这类双反斜杠；
                // Windows 能容忍，但日志与后续拼接都按干净路径来）
                try { staticJs = Path.GetFullPath(staticJs); } catch { }
                prefixArgs = new List<string> { staticJs };
                return true;
            }
            try { cmdPart = Environment.ExpandEnvironmentVariables(cmdPart); } catch { }

            List<string> tokens = TokenizeCommandLine(cmdPart);
            if (tokens.Count == 0 || tokens.Count > 2) return false;

            // ③ 第一个 token 必须落成一个真 exe（node / node.exe / 某 .exe）
            string exeRaw = tokens[0];
            string exePath = exeRaw;
            if (!File.Exists(exePath))
            {
                string? found = FindOnPath(exeRaw);
                if (found == null || !IsExecutableImage(found)) return false;
                exePath = found;
            }
            if (!IsExecutableImage(exePath)) return false;      // 套娃 shim / 脚本 ⇒ 保守拒绝
            if (tokens.Count == 2
                && !tokens[1].EndsWith(".js", StringComparison.OrdinalIgnoreCase)
                && !tokens[1].EndsWith(".cjs", StringComparison.OrdinalIgnoreCase))
                return false;                                    // 第二 token 不是 js 入口 ⇒ 形态不认识

            exe = Path.GetFullPath(exePath);
            prefixArgs = tokens.Count == 2 ? new List<string> { tokens[1] } : new List<string>();
            return true;
        }
        catch { return false; }
    }

    /// <summary>安全启动规格：真可执行文件 + 逐 token 参数 + 仅显示用命令行（不用于执行）。</summary>
    public sealed record LaunchSpec(string FileName, List<string> ArgumentList, string? WorkDir, string Display)
    {
        /// <summary>把这条规格填进 ProcessStartInfo（全部走 ArgumentList；Redirect 等由调用方按场景补充）。</summary>
        public void ApplyTo(ProcessStartInfo psi)
        {
            psi.FileName = FileName;
            psi.Arguments = "";                                  // 旧字段清零：参数只走 ArgumentList
            psi.ArgumentList.Clear();
            foreach (string t in ArgumentList) psi.ArgumentList.Add(t);
            if (WorkDir != null) psi.WorkingDirectory = WorkDir;
        }
    }

    /// <summary>
    /// 安全启动规格（执行层统一入口，纯函数级）：把 command + args 解析成「真 exe + 逐 token 参数」。
    ///   · 裸命令名：先按 PATHEXT 找真 exe（.cmd/.bat 拒绝、非 exe/com 形态拒绝），
    ///     找不到再找同名 .cmd shim 解引用（npx → node.exe + npx-cli.js）；
    ///   · 带路径：.cmd/.bat 解引用，其余必须是真实存在的可执行映像；
    ///   · 解析不出 ⇒ null，调用方走 <see cref="ApplyCmdFallback"/> 兜底。
    /// </summary>
    public static LaunchSpec? BuildLaunchSpec(string command, IReadOnlyList<string> argTokens, string? workDir = null)
    {
        List<string> headTokens = TokenizeCommandLine(command);
        if (headTokens.Count == 0) return null;
        string head = headTokens[0];
        var rest = new List<string>(headTokens.Skip(1));
        rest.AddRange(argTokens);

        string exe;
        var prefix = new List<string>();
        bool hasDir = head.Contains('\\') || head.Contains('/');

        if (hasDir)
        {
            if (IsCmdScript(head))
            {
                if (!File.Exists(head) || !TryResolveCmdShim(head, out exe, out prefix)) return null;
            }
            else
            {
                if (!File.Exists(head) || !IsExecutableImage(head)) return null;
                exe = head;
            }
        }
        else
        {
            string? direct = FindOnPath(head);
            if (direct != null && !IsExecutableImage(direct)) direct = null;   // 非 exe 形态（sh 脚本等）不直启
            if (direct != null)
            {
                exe = direct;
            }
            else
            {
                string? shim = FindOnPath(head, cmdScriptsOnly: true);
                if (shim == null || !TryResolveCmdShim(shim, out exe!, out prefix)) return null;
            }
        }

        var finalArgs = new List<string>(prefix);
        finalArgs.AddRange(rest);
        string display = command.Trim() + (rest.Count > 0 ? " " + string.Join(" ", rest) : "");
        return new LaunchSpec(Path.GetFullPath(exe), finalArgs, workDir, display);
    }

    /// <summary>字符串便捷重载：args 按 Windows 规则拆 token 后走同一条解析链。</summary>
    public static LaunchSpec? BuildLaunchSpec(string command, string args, string? workDir = null)
        => BuildLaunchSpec(command, (IReadOnlyList<string>)TokenizeCommandLine(args), workDir);

    /// <summary>
    /// cmd 兜底（仅当 BuildLaunchSpec 解析不出真 exe；本机没有 node/npx、或 LaunchCommand 指向
    /// PATH 里不存在的命令时可达）：把整条命令行作为**单个参数**交给 <c>cmd /d /s /c</c>，外层整体加引号 ——
    /// /S 语义下 cmd 剥掉最外层一对引号，其余内容原样交给 cmd 解释。
    ///
    /// ⚠ 只用于程序自控的可信内容；任何携带外部插值的命令都应在 BuildLaunchSpec 里解析成功、走逐 token 路径。
    /// ⚠ 外层引号只解决「整条命令行必须成为 /c 的同一个参数」这一**形态**问题，**不能**中和 cmd 对
    ///   <c>&amp; | ^ &gt; &lt; %VAR%</c> 的解释（独立工程实测：命令里含 &amp; 时第二条命令照样执行，
    ///   三种写法都如此）——真正的安全边界是上面那条「可信内容」，不是这层引号。
    ///
    /// ⚠ 必须走 <see cref="ProcessStartInfo.Arguments"/> 原始字符串，**不能**把整体引号串当单个元素
    ///   塞进 <see cref="ProcessStartInfo.ArgumentList"/>：.NET 会按 MSVCRT 规则转义（元素以 <c>"</c>
    ///   开头且含空格 ⇒ 整个元素再被包一层引号、内部引号变字面量），cmd 收到的是一个**名字里带引号**
    ///   的程序名，必然报「不是内部或外部命令」并以 1 退出（独立工程实测：退出码=1，兜底 100% 失败）。
    ///   故此处清零 ArgumentList、只写 Arguments：两者同时非空时 <c>Process.Start</c> 直接抛
    ///   <c>InvalidOperationException: Only one of Arguments or ArgumentList may be used.</c>
    /// </summary>
    public static void ApplyCmdFallback(ProcessStartInfo psi, string commandLine)
    {
        psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";   // cmd.exe
        // Arguments 是原始拼接（与 ArgumentList 互斥）：必须先清空后者，否则 Process.Start 抛异常
        psi.ArgumentList.Clear();
        psi.Arguments = "/d /s /c \"" + (commandLine ?? "") + "\"";
    }

    /// <summary>
    /// 结束占用该端口的进程（只杀监听者本身，不用 /T）。
    /// 监听端口的是 node：杀掉它引擎即停，而作为它子孙的守护壳不受影响
    /// （Windows 语义：结束父进程不会连带结束子进程，只有 /T 才会）。
    /// </summary>
    public static bool KillPortOwners(int port, out string detail)
    {
        detail = "";
        try
        {
            var owners = FindPortOwners(port);
            if (owners.Count == 0) { detail = $"端口 {port} 上没有找到监听进程"; return true; }

            var notes = new List<string>();
            foreach (var o in owners)
            {
                notes.Add($"PID {o.Pid}（{o.Name}）");
                try
                {
                    Logger.Log($"终止端口 {port} 的监听进程 PID={o.Pid}（{o.Name}）");
                    // 刻意不用 /T：进程树里可能包含守护壳自己的祖先，那会把本程序一起带走
                    KillPid(o.Pid, force: false);
                    Thread.Sleep(800);
                    if (IsAlive(o.Pid)) KillPid(o.Pid, force: true);
                }
                catch (Exception ex) { Logger.LogError($"KillPortOwners({o.Pid})", ex); }
            }

            // 等待端口释放（最多 10 秒）
            for (int i = 0; i < 20; i++)
            {
                if (!NetworkHelper.IsPortListening(port))
                {
                    detail = "已终止：" + string.Join("、", notes);
                    return true;
                }
                Thread.Sleep(500);
            }
            detail = "已发出终止命令，但端口 " + port + " 仍在监听：" + string.Join("、", notes);
            return false;
        }
        catch (Exception ex)
        {
            Logger.LogError("ProcessManager.KillPortOwners", ex);
            detail = "终止失败：" + ex.Message;
            return false;
        }
    }

    /// <summary>终止路由：托管时必须先问用户，其余按第几档走。</summary>
    public enum TerminateRoute
    {
        /// <summary>引擎正托管着本程序：必须先弹三键框，绝不静默自杀、也不静默放过。</summary>
        AskHosted,
        /// <summary>常规有界终止（只杀监听者，等端口释放）。</summary>
        Tier1,
        /// <summary>上次没停掉后的强杀档。</summary>
        Tier2
    }

    /// <summary>
    /// 终止路由判定（纯函数，便于自检）。
    /// 托管判定在第 39 批重写两档终止时被漏掉了——三键框只剩自检样张，主流程从不询问，
    /// 于是"关引擎连带关壳"反复复发。这里把它接回决策链的最前面。
    /// </summary>
    public static TerminateRoute PlanTerminate(bool hosted, bool secondTier)
        => hosted ? TerminateRoute.AskHosted
                  : (secondTier ? TerminateRoute.Tier2 : TerminateRoute.Tier1);

    /// <summary>当前托管的引擎（端口监听者里任一在托管本程序）。</summary>
    public static bool IsHostedByPortOwner(int port)
    {
        try
        {
            foreach (var o in FindPortOwners(port))
                if (IsHostedBy(o.Pid)) return true;
        }
        catch (Exception ex) { Logger.LogError("IsHostedByPortOwner", ex); }
        return false;
    }

    /// <summary>
    /// 有界终止引擎：先按端口结束监听进程（温和→强制），最多等 waitSeconds 秒；
    /// 仍占用端口就强杀监听进程**及其子进程**，再给 settleSeconds 秒复检。
    /// 全程有上限，绝不无限等待。不使用 /T（避免连带守护壳的祖先）。
    /// </summary>
    public static bool TerminateEngineBounded(int port, int waitSeconds, int settleSeconds,
                                              out string detail, out bool forceUsed)
    {
        forceUsed = false;
        bool killed = KillPortOwners(port, out string first);
        Logger.Log($"终止引擎第一档：{first}");

        int waited = 0;
        while (waited < waitSeconds * 1000 && NetworkHelper.IsPortListening(port))
        {
            Thread.Sleep(500);
            waited += 500;
        }

        if (!NetworkHelper.IsPortListening(port))
        {
            detail = waited > 0 ? $"{first}（等待 {waited / 1000} 秒后端口已释放）" : first;
            return true;
        }

        // 到点还占着 → 强杀。**托管场景绝不整树终止**：进程树里可能有本程序的祖先链，
        // 整树会把它一起带走；这种情况下退化为"只杀监听者本身"。
        forceUsed = true;
        var notes = new List<string>();
        foreach (var o in FindPortOwners(port))
        {
            if (IsHostedBy(o.Pid))
            {
                KillPid(o.Pid, force: true);
                notes.Add($"PID {o.Pid}（托管中：只杀监听者本身，不做整树终止）");
            }
            else
            {
                ForceKillTreeExceptSelf(o.Pid, out string treeNote);
                notes.Add(treeNote);
            }
            Logger.Log($"强杀 PID {o.Pid}：{notes[notes.Count - 1]}");
        }

        int settle = 0;
        while (settle < settleSeconds * 1000 && NetworkHelper.IsPortListening(port))
        {
            Thread.Sleep(500);
            settle += 500;
        }

        if (!NetworkHelper.IsPortListening(port))
        {
            detail = $"{first}；超时后强杀：{(notes.Count > 0 ? string.Join("、", notes) : "无监听者")}（端口已释放）";
            return true;
        }

        detail = $"端口 {port} 仍被占用（已等 {waitSeconds} 秒 + 复检 {settleSeconds} 秒）：{(notes.Count > 0 ? string.Join("、", notes) : first)}";
        return false;
    }

    /// <summary>
    /// 强关端口：结束该端口所有监听进程**及其子进程**（跳过本程序与祖先链），再复检。
    /// 供「第二次点终止引擎」的强杀档使用。
    /// </summary>
    public static bool ForceClosePort(int port, int settleSeconds, out string detail)
    {
        var notes = new List<string>();
        try
        {
            var owners = FindPortOwners(port);
            foreach (var o in owners)
            {
                if (IsHostedBy(o.Pid))
                {
                    // 托管中：整树终止会连带本程序的祖先链，只杀监听者本身
                    KillPid(o.Pid, force: true);
                    notes.Add($"PID {o.Pid}（托管中：只杀监听者本身）");
                }
                else
                {
                    ForceKillTreeExceptSelf(o.Pid, out string treeNote);
                    notes.Add(treeNote);
                }
                Logger.Log($"强关端口 {port}：{notes[notes.Count - 1]}");
            }
            if (owners.Count == 0) notes.Add("（当前没有监听者）");

            int settle = 0;
            while (settle < settleSeconds * 1000 && NetworkHelper.IsPortListening(port))
            {
                Thread.Sleep(500);
                settle += 500;
            }

            // 复检：可能有新进程又抢了端口（同样要守"托管场景绝不整树终止"的规矩）
            if (NetworkHelper.IsPortListening(port))
            {
                foreach (var o in FindPortOwners(port))
                {
                    if (IsHostedBy(o.Pid))
                    {
                        KillPid(o.Pid, force: true);
                        notes.Add($"复检时又发现：PID {o.Pid}（托管中：只杀监听者本身）");
                    }
                    else
                    {
                        ForceKillTreeExceptSelf(o.Pid, out string again);
                        notes.Add("复检时又发现：" + again);
                    }
                }
                Thread.Sleep(1000);
            }

            detail = string.Join("、", notes);
            return !NetworkHelper.IsPortListening(port);
        }
        catch (Exception ex)
        {
            Logger.LogError("ProcessManager.ForceClosePort", ex);
            detail = "强关失败：" + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 强杀一棵进程树：结束 rootPid 及其所有子孙（先深后浅），
    /// **跳过本程序自身与其祖先链**（那是守护壳赖以存活的链路），且不使用 /T。
    /// </summary>
    public static List<int> ForceKillTreeExceptSelf(int rootPid, out string detail)
    {
        var killed = new List<int>();
        detail = "";
        try
        {
            if (rootPid <= 0) { detail = "无效 PID"; return killed; }

            var parents = ProcessParents();
            var toKill = new List<int>();
            var stack = new Stack<int>();
            stack.Push(rootPid);
            while (stack.Count > 0)
            {
                int cur = stack.Pop();
                toKill.Add(cur);
                foreach (var (id, parent) in parents)
                    if (parent == cur) stack.Push(id);
            }
            toKill.Reverse();      // 先子后父

            var skipped = new List<int>();
            foreach (int pid in toKill)
            {
                if (pid == Environment.ProcessId || IsSelfOrAncestor(pid)) { skipped.Add(pid); continue; }
                try { KillPid(pid, force: true); killed.Add(pid); } catch { }
            }

            detail = killed.Count > 0
                ? $"已强杀 {killed.Count} 个进程（PID {string.Join(",", killed)}）"
                : "没有可强杀的进程";
            if (skipped.Count > 0)
                detail += $"；跳过自身/祖先 {skipped.Count} 个（PID {string.Join(",", skipped)}）";
            return killed;
        }
        catch (Exception ex)
        {
            Logger.LogError("ProcessManager.ForceKillTreeExceptSelf", ex);
            detail = "强杀失败：" + ex.Message;
            return killed;
        }
    }

    /// <summary>第二档判定：上次终止没成功、且端口仍被占用时，再点一次就走强杀。</summary>
    public static bool IsSecondTier(bool attemptedBefore, bool portStillOpen) => attemptedBefore && portStillOpen;

    /// <summary>启动等待的停止条件：端口就绪、或引擎进程已死（死了就别空等）。</summary>
    public static bool ShouldStopWaiting(bool childExited, bool portListening) => portListening || childExited;

    /// <summary>当前托管的子进程 PID（0 = 没有；供启动失败的清理使用）。</summary>
    public int TrackedPid => _process?.Id ?? 0;

    /// <summary>自检用：进程是否还活着。</summary>
    public static bool IsAliveForTest(int pid) => IsAlive(pid);

    // ═══ 启动引擎（node.exe + npx-cli.js @deepseek-ai/dsh@latest web --no-open --port N）═══

    /// <summary>
    /// 引擎的工作目录：**必须是 `WorkDir`（`~\.dsh`），不能改成配置文件目录**。
    ///
    /// 这条是花钱买来的教训（2026-09-13 实测对照）：
    ///   cwd = profiles\web → `npx @deepseek-ai/dsh@0.1.5-rc.2 --version` 打出 **0.1.5-rc.1**（版本指定被绕过！）
    ///   cwd = .dsh         → 同一条命令打出 **0.1.5-rc.2**（正确）
    /// npx 会优先从"当前目录的 node_modules"里找包，配置目录里一旦有 `@deepseek-ai/*` 条目
    /// （哪怕是链接），它就会用那个副本、完全无视 `@版本`，随后在引擎入口 require 阶段炸成
    /// `MODULE_NOT_FOUND`——现象就是"点一键启动，一秒就退"。
    /// </summary>
    public static string EngineWorkDir => WorkDir;

    public bool Start(int port, CancellationToken ct = default, string? baseCommand = null)
    {
        // 上一轮的对象先处理掉：退订它的三个回调并放掉句柄。
        //   这一步必须在 Process.Start 之前（原缺陷：赋值前不处理旧对象，旧对象连同它的
        //   OutputDataReceived 闭包一路攒着）。也正因为先退订，"陈旧引擎的输出混进新一轮日志面板"
        //   这条根因才被切断 —— 订阅一旦退掉，旧闭包里的 OutputReceived 就不会再被调用。
        ReleasePreviousProcess();
        try
        {
            if (!Directory.Exists(WorkDir)) Directory.CreateDirectory(WorkDir);
            PurgeBrokenPackageJson(WorkDir);

            string workDir = EngineWorkDir;
            string args = BuildArgs(port, baseCommand);
            // 日志仍保留完整命令行（仅显示用；执行只走下面的 ArgumentList）
            Logger.Log($"启动: {args} @ {workDir}");

            // 安全启动：不再经 cmd /c 拼裸字符串（cmd 会解释 & | ^ > < %VAR%，任何插值含 & 即可注入
            // 第二条命令）。args 按 Windows 规则拆成 token；npx 的 .cmd 包装脚本被解引用成
            // node.exe + npx-cli.js 直启，参数逐 token 到达，& 在 argv 里只是普通字符。
            var psi = new ProcessStartInfo
            {
                WorkingDirectory = workDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                // Node/dsh 输出为 UTF-8；未显式指定时 .NET 按系统 ANSI(GBK) 解码，中文会乱码
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };
            var spec = BuildLaunchSpec(args, "", workDir);
            if (spec != null)
            {
                spec.ApplyTo(psi);
            }
            else
            {
                // 解析不出真 exe（本机没有 node？）：cmd 兜底，整体引号包裹（/S 语义只剥最外层）
                Logger.NoteDiagnosis("启动告警：未能把启动命令解析成真可执行文件，走 cmd 兜底（/d /s /c 整体引号）");
                ApplyCmdFallback(psi, args);
            }

            // 显式给子进程带上可写临时目录（不依赖继承来的值，安装器拉起的进程尤其需要）
            try
            {
                string tmp = ProcessEnv.UserTempDir;
                if (!Directory.Exists(tmp)) Directory.CreateDirectory(tmp);
                psi.EnvironmentVariables["TEMP"] = tmp;
                psi.EnvironmentVariables["TMP"] = tmp;
            }
            catch (Exception ex) { Logger.LogError("engine TEMP", ex); }

            var started = Process.Start(psi);
            if (started == null)
            {
                OutputReceived?.Invoke(this, "ERROR: failed to start engine process");
                return false;
            }

            Logger.Log($"引擎进程 PID: {started.Id}（{Path.GetFileName(started.StartInfo.FileName)}）");

            // 处理器装进字段（匿名 lambda 不留引用就无从退订 —— 见 _stdoutHandler 的说明），
            // 并且订阅与"换引用"在同一次加锁里一次做完：避免别的线程在这两步之间只看到
            // "对象已换、处理器还没挂上"的中间态，进而在释放旧对象时误伤新对象。
            // 用局部变量做 +=/-=：既避开 nullable 的"可能为 null"告警，也让配对一目了然。
            DataReceivedEventHandler stdoutHandler = (s, e) =>
            {
                if (e.Data != null) OutputReceived?.Invoke(this, e.Data);
            };
            DataReceivedEventHandler stderrHandler = (s, e) =>
            {
                // stderr 打标记：订阅方据此分辨来源（并只对 stderr 做"已知无害"分诊）。
                // stdout 的处理**保持不变**。
                if (e.Data != null) OutputReceived?.Invoke(this, StderrTag + e.Data);
            };
            // 引擎自己退出时只退订、不及时释放句柄（释放时机见 OnEngineProcessExited 的说明）
            EventHandler exitedHandler = OnEngineProcessExited;

            lock (_processLock)
            {
                _stdoutHandler = stdoutHandler;
                _stderrHandler = stderrHandler;
                _exitedHandler = exitedHandler;

                _process = started;
                started.OutputDataReceived += stdoutHandler;
                started.ErrorDataReceived += stderrHandler;
                started.EnableRaisingEvents = true;
                started.Exited += exitedHandler;
            }

            started.BeginOutputReadLine();
            started.BeginErrorReadLine();

            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError("ProcessManager.Start", ex);
            OutputReceived?.Invoke(this, $"ERROR: {ex.Message}");
            return false;
        }
    }

    public bool GracefulStop(int timeoutSeconds = 20)
    {
        Process? p;
        lock (_processLock) { p = _process; }
        if (p == null || HasExited) return true;

        OutputReceived?.Invoke(this, "发送 Ctrl+C...");
        Logger.Log("GracefulStop: 发送 Ctrl+C");

        // 方案 A：管道写入 Ctrl+C
        try { p.StandardInput.Write("\x03"); p.StandardInput.Flush(); }
        catch (Exception ex) { Logger.Log($"管道 Ctrl+C 失败: {ex.Message}"); }

        // 方案 B（控制台 API）已移除：GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0) 会把 Ctrl+C
        // 发给共享该控制台的所有进程，包括可能与本引擎同树的守护壳自身，风险过高。
        // 现在靠 stdio 管道 + taskkill 兜底，都只针对目标进程。

        OutputReceived?.Invoke(this, $"等待进程退出({timeoutSeconds}s)...");
        if (SafeWait(p, timeoutSeconds * 1000))
        {
            Logger.Log("进程已退出(Ctrl+C 后)");
            // 成功退出也要收尾（原缺陷：这一档从不释放，"启动→停止→再启动"反复操作就一路攒对象）
            ReleaseStoppedEngine(p);
            return true;
        }

        OutputReceived?.Invoke(this, "进程未退出,尝试注入 y...");
        Logger.Log("超时,注入 y");
        try { p.StandardInput.Write("y\n"); p.StandardInput.Flush(); }
        catch { }

        if (SafeWait(p, 5000))
        {
            Logger.Log("进程已退出(注入 y 后)");
            ReleaseStoppedEngine(p);
            return true;
        }

        Logger.Log("强制终止");
        ForceKill();
        return false;
    }

    /// <summary>WaitForExit 的容错版：对象已释放或进程不存在时按"已退出"处理，不抛异常。</summary>
    private static bool SafeWait(Process p, int milliseconds)
    {
        try { return p.WaitForExit(milliseconds); }
        catch (InvalidOperationException) { return true; }
        catch { return true; }
    }

    public void ForceKill()
    {
        Process? p;
        lock (_processLock)
        {
            p = _process;
            _process = null;   // 先解除引用：重复停止时不会再操作已释放的 Process
        }
        if (p == null) return;

        try
        {
            bool exited;
            try { exited = p.HasExited; }
            catch (InvalidOperationException) { exited = true; }   // 对象已释放或进程已不存在

            if (!exited)
            {
                // 不用 /T：本程序启动的 cmd 树里可能有别的活，但整树终止的风险大于收益
                KillPid(p.Id, force: true);
            }
        }
        catch (Exception ex) { Logger.LogError("ForceKill", ex); }
        // 强杀这一档也要把三个订阅退掉，与 Start 里的三处 += 成对
        try
        {
            p.OutputDataReceived -= _stdoutHandler;
            p.ErrorDataReceived -= _stderrHandler;
            p.Exited -= _exitedHandler;
        }
        catch { }
        try { p.Dispose(); } catch { }
    }

    /// <summary>终止单个进程（force=true 时加 /F）。只针对该 PID，永不带 /T。</summary>
    private static void KillPid(int pid, bool force)
    {
        try
        {
            string args = KillArgs(pid, force);
            Logger.Log($"taskkill {args}");
            // taskkill 是真 exe，直接启动 + ArgumentList，不再经 cmd /c
            var psi = new ProcessStartInfo
            {
                FileName = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\taskkill.exe"),
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("/PID");
            psi.ArgumentList.Add(pid.ToString());
            if (force) psi.ArgumentList.Add("/F");
            // taskkill 是一次性进程：同一个对象读完退出状态就 Dispose，不留给 GC 终结器
            using var tk = Process.Start(psi);
            tk?.WaitForExit(5000);
        }
        catch (Exception ex) { Logger.LogError($"KillPid({pid})", ex); }
    }

    private static bool IsAlive(int pid)
    {
        // 读到退出状态就还句柄：本方法在终止/清理流程里会被连着调好几轮（自检与现场都如此），
        // 不 Dispose 就是每轮漏一个进程句柄。返回 true 的分支同样在 using 里读到值后再返回。
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    /// <summary>启动时记下的祖先进程集合（那时中间进程还在，父链完整）。</summary>
    private static readonly HashSet<int> RememberedAncestors = new();

    /// <summary>启动时调用一次：把当前父链上的进程记下来，供后续托管判定使用。</summary>
    public static void CaptureAncestors()
    {
        try
        {
            RememberedAncestors.Clear();
            int cur = Environment.ProcessId;
            for (int depth = 0; depth < 32; depth++)
            {
                cur = ParentProcessId(cur);
                if (cur <= 0) break;
                RememberedAncestors.Add(cur);
            }
            Logger.Log("祖先进程：" + string.Join("、", RememberedAncestors));
        }
        catch (Exception ex) { Logger.LogError("CaptureAncestors", ex); }
    }

    /// <summary>pid 是不是 ancestorPid 本身或它的子孙（沿父链向上找，深度有界）。</summary>
    public static bool IsDescendantOf(int pid, int ancestorPid) => IsDescendantOf(pid, ancestorPid, ParentProcessId);

    /// <summary>同上，父链查询可注入（自检用假数据即可覆盖）。</summary>
    internal static bool IsDescendantOf(int pid, int ancestorPid, Func<int, int> parentOf)
    {
        if (pid <= 0 || ancestorPid <= 0) return false;
        int cur = pid;
        for (int depth = 0; depth < 32 && cur > 0; depth++)
        {
            if (cur == ancestorPid) return true;
            cur = parentOf(cur);
        }
        return false;
    }

    /// <summary>
    /// 启动失败清理时该不该强关端口：**只关本程序自己拉起来的监听者**。
    /// 若端口由其他进程占用（例如用户自行启动的 DSH 引擎），强制终止会中断用户正在使用的进程——
    /// 失败清理绝不允许有这种副作用。
    /// </summary>
    internal static bool ShouldForceClosePort(int ownerPid, int trackedPid, Func<int, int> parentOf)
        => ownerPid > 0 && trackedPid > 0 && IsDescendantOf(ownerPid, trackedPid, parentOf);

    /// <summary>该进程是不是本程序自身或它的祖先进程。终止引擎前用它做保护：
    /// 守护壳可能是从这个引擎里被拉起来的，一旦把它杀掉，守护壳也会跟着消失。
    /// </summary>
    public static bool IsSelfOrAncestor(int pid)
    {
        try
        {
            if (pid == Environment.ProcessId) return true;
            if (RememberedAncestors.Contains(pid)) return true;   // 启动时记下的（父链可能已断）
            int cur = Environment.ProcessId;
            for (int depth = 0; depth < 32 && cur > 0; depth++)
            {
                if (cur == pid) return true;
                cur = ParentProcessId(cur);
            }
        }
        catch { }
        return false;
    }

    /// <summary>自检用：本进程的父进程 ID。</summary>
    public static int ParentPidForTest() => ParentProcessId(Environment.ProcessId);

    /// <summary>父进程 id（0 = 未知）。供"这个监听者是不是本程序拉起来的"归属判断使用。</summary>
    public static int ParentPidOf(int pid) => ParentProcessId(pid);

    /// <summary>取父进程 ID（取不到返回 0）。用进程快照实现，不引入额外依赖。</summary>
    private static int ParentProcessId(int pid)
    {
        try
        {
            foreach (var (id, parent) in ProcessParents())
                if (id == pid) return parent;
        }
        catch { }
        return 0;
    }

    /// <summary>一次性列出 (进程ID, 父进程ID)。</summary>
    private static List<(int Id, int Parent)> ProcessParents()
    {
        var list = new List<(int, int)>();
        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return list;
        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snap, ref entry)) return list;
            do
            {
                list.Add(((int)entry.th32ProcessID, (int)entry.th32ParentProcessID));
                entry.dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>();
            } while (Process32Next(snap, ref entry));
        }
        finally { CloseHandle(snap); }
        return list;
    }

    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);
}
