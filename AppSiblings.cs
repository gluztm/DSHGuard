using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace DSHGuard;

/// <summary>
/// 同名进程（另一个 DSHGuard.exe）的现场侦察与结束 —— 卸载前的最后一里。
/// <para>
/// 病象（2026-11-04 现场，P0-4）：机器上常驻一个 <c>DSHGuard.exe</c>，用户点卸载被告知
/// 「守护壳还在运行。请先退出它（托盘图标 → 退出UI），再回来卸载」，可用户根本不知道那是哪一个
/// 进程、从哪儿启动的、什么时候启动的 —— 托盘里也找不到它（它可能是另一份安装在
/// <c>%LOCALAPPDATA%\Programs</c> 的副本，托盘图标在别处）。于是卸载**永远**走不下去。
/// </para>
/// <para>
/// 这里只做三件纯事：**看清**（枚举 + 路径 + 启动时间）、**说清**（纯文本描述，可自检）、
/// **结束**（先请主窗口关，等一段时间，剩下的强杀整棵树）。界面记账留给调用方：
/// 同一套逻辑既被卸载路径用（<c>App</c> 的 static 方法、窗口正在关闭），也被桌面版用，
/// 所以刻意不碰 <c>AddEvent</c> / 不假设有窗口。
/// </para>
/// </summary>
internal static class AppSiblings
{
    /// <summary>一条同名进程的现场。字段全是取值快照，进程退出后依然可读（不持有句柄）。</summary>
    internal sealed class Entry
    {
        internal int Pid;
        internal string Path = "";
        internal string StartText = "";
        internal bool HasWindow;
        /// <summary>是否就是当前这个进程（卸载时要把自己排除掉）。</summary>
        internal bool IsSelf;
        /// <summary>是否落在调用方给的「本程序目录」里；ownDir 为空时恒为 false（无法判定）。</summary>
        internal bool InOwnDir;
    }

    /// <summary>
    /// 枚举同名进程并拍下现场。永不抛：单个进程的路径/启动时间读不到（权限、已退出）就留空，
    /// 因为「读不到」不等于「不是它」，反而更需要摆给用户看。
    /// </summary>
    internal static List<Entry> Inspect(string processName, int selfPid, string ownDir)
    {
        var list = new List<Entry>();
        if (string.IsNullOrWhiteSpace(processName)) return list;

        string norm = "";
        if (!string.IsNullOrEmpty(ownDir))
            try { norm = Path.GetFullPath(ownDir).TrimEnd('\\'); } catch { norm = ""; }

        Process[] procs;
        try { procs = Process.GetProcessesByName(processName); }
        catch (Exception ex) { Logger.LogError("AppSiblings.Inspect", ex); return list; }

        foreach (var p in procs)
        {
            var e = new Entry();
            try
            {
                e.Pid = p.Id;
                e.IsSelf = p.Id == selfPid;
                try { e.HasWindow = p.MainWindowHandle != IntPtr.Zero; } catch { }
                try
                {
                    string? path = p.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(path))
                    {
                        e.Path = path!;
                        if (norm.Length > 0)
                            try
                            {
                                e.InOwnDir = string.Equals(
                                    Path.GetFullPath(Path.GetDirectoryName(path) ?? "").TrimEnd('\\'),
                                    norm, StringComparison.OrdinalIgnoreCase);
                            }
                            catch { }
                    }
                }
                catch { }
                try { e.StartText = p.StartTime.ToString("yyyy-MM-dd HH:mm:ss"); } catch { }
            }
            catch { }
            finally { try { p.Dispose(); } catch { } }
            list.Add(e);
        }
        list.Sort((a, b) => a.Pid.CompareTo(b.Pid));
        return list;
    }

    /// <summary>
    /// 把现场写成给人看的话（纯函数，不碰进程，便于自检）。
    /// 逐条列出 PID / 启动时间 / 完整路径，并标明它是不是「本程序目录里的那个」——
    /// 这一行正是用户判断「哪来的第二个壳」的唯一线索。
    /// </summary>
    internal static string Describe(IReadOnlyList<Entry> list, int selfPid)
    {
        var others = new List<Entry>();
        foreach (var e in list) if (!e.IsSelf) others.Add(e);
        if (others.Count == 0) return "";

        var sb = new StringBuilder();
        sb.Append("挡着卸载的还有 ").Append(others.Count).Append(" 个 DSHGuard 进程：\n");
        foreach (var e in others)
        {
            sb.Append("\n· PID ").Append(e.Pid);
            if (e.StartText.Length > 0) sb.Append("　启动于 ").Append(e.StartText);
            sb.Append('\n');
            sb.Append("  ").Append(e.Path.Length > 0 ? e.Path : "（路径读不到，可能权限不足或已退出）").Append('\n');
            if (e.InOwnDir) sb.Append("  ← 本程序目录\n");
            else if (e.Path.Length > 0) sb.Append("  ← 不在本程序目录（多半是另一份安装留下的壳）\n");
            if (!e.HasWindow) sb.Append("  ← 没有主窗口\n");
        }
        sb.Append("\n卸载器要删的就是这个 exe，正在运行的文件删不掉。");
        sb.Append("\n\n现在结束它们并继续卸载吗？（先请它自己退出，8 秒内不退再强制结束）");
        return sb.ToString();
    }

    /// <summary>
    /// 结束给定的那些进程：先对**有主窗口**的发 <c>CloseMainWindow</c>，再在一个
    /// <paramref name="waitMs"/> 毫秒的**共享窗口内轮询**每个进程是否自己退了（<c>HasExited</c>，
    /// 50ms 一次），窗口用完还没退的才 <c>Kill(entireProcessTree)</c>。
    /// <para>
    /// 为什么是轮询而不是「串行 <c>WaitForExit(left)</c>、left = deadline - now」：
    ///   <b>不是为了修 bug</b>。我先前断言那种写法会「让第一个进程吃光预算、第二个拿到 left=0 被
    ///   冤枉强杀」，真进程实测（dlprobe，预算 8s）证明<b>该断言不成立</b>：left 用绝对 deadline 算，
    ///   p1=4.2s/p2=6.2s 时两种写法同为 closed=2 killed=0；p1 卡死 30s 时也同为 closed=1 killed=1。
    ///   改成轮询只是把「这批进程共享同一个窗口」写进代码结构里 —— 读的人不必再推理 left 的语义，
    ///   也就不会像我一样推错。总时长仍由 <paramref name="waitMs"/> 封顶，不会变成 N×waitMs
    ///   （那才是「每个进程各等一遍」）。
    /// </para>
    /// <para>
    /// 一律跳过 <see cref="Environment.ProcessId"/>：卸载界面就是自己，把自己杀了就没法继续开卸载器。
    /// 永不抛；返回 (正常退出数, 强制结束数, 失败数)。
    /// </para>
    /// </summary>
    /// <param name="logTarget">诊断日志归属的目标（桌面版传 <c>GuardTarget.Desktop</c>，
    /// 卸载路径留 null 走默认那本）。调用方不必再重复写一遍明细，本方法按此参数写盘。</param>
    internal static (int closed, int killed, int failed) Terminate(IReadOnlyCollection<int> pids, int waitMs = 8000, GuardTarget? logTarget = null)
    {
        int asked = 0, closed = 0, killed = 0, failed = 0;
        var procs = new List<Process>();
        foreach (int pid in pids)
        {
            if (pid == Environment.ProcessId) continue;
            try { procs.Add(Process.GetProcessById(pid)); }
            catch { closed++; }   // 已经退了：算正常退出，不算失败
        }
        foreach (var p in procs)
        {
            try { if (p.MainWindowHandle != IntPtr.Zero && p.CloseMainWindow()) asked++; }
            catch { }
        }

        // ★ 顺序要紧：deadline 必须在上面那个 CloseMainWindow 循环**之后**算。
        //   写在之前时 asked 恒为 0 ⇒ deadline = now + 0 ⇒ 下面的等待循环一次都不进 ⇒
        //   所有进程当场强杀 —— 恰好把本条要修的问题反转（2026-11-04 评审稿里踩过）。
        var deadline = DateTime.UtcNow.AddMilliseconds(asked > 0 ? Math.Max(0, waitMs) : 0);

        // 轮询而不是串行 WaitForExit(left)。**先记一条纠错**：我（小绿 2026-11-04）先前报给评审的
        //   「第一个进程吃光预算、第二个拿到 left=0 被当场强杀」经真进程实测**不成立** ——
        //   left 是拿**绝对** deadline 算的，p1 卡满 8 秒时 p2 早已在窗口内自己退完，
        //   WaitForExit(0) 对已退进程返回 true ⇒ 计入 closed。实测（dlprobe，预算 8s）：
        //     p1=4.2s / p2=6.2s：串行 closed=2 killed=0 wall=6250ms；轮询 closed=2 killed=0 wall=6237ms
        //     p1=30s(卡死) / p2=2s：串行与轮询同为 closed=1 killed=1
        //   ⇒ 两种写法**结果等价**，轮询不是缺陷修复，只是把「共享一个窗口」这件事写成代码里
        //   看得见、不用推理 left 语义的形式（评审 小蓝 当时按算术认可了那条缺陷，同样没跑实测）。
        //   总时长仍由 waitMs 封顶，与「每进程各等一遍」不同（那种才会变成 N×waitMs）。
        var pending = new List<Process>(procs);
        while (pending.Count > 0 && DateTime.UtcNow < deadline)
        {
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                bool gone;
                try { gone = pending[i].HasExited; } catch { gone = true; }   // 读不到=当作已退
                if (gone) { pending.RemoveAt(i); closed++; }
            }
            if (pending.Count == 0) break;
            System.Threading.Thread.Sleep(50);
        }
        foreach (var p in pending)
        {
            try
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(3000);
                killed++;
            }
            catch (InvalidOperationException) { closed++; }   // 中途退出了
            catch (Exception ex)
            {
                failed++;
                Logger.NoteDiagnosis($"结束进程失败（PID {SafePid(p)}）：{ex.Message}", logTarget);
            }
        }
        foreach (var p in procs) { try { p.Dispose(); } catch { } }
        if (killed > 0 || failed > 0)
            Logger.NoteDiagnosis($"结束同名进程：请求关闭 {asked} 个 · 正常 {closed} / 强制 {killed} / 失败 {failed}", logTarget);
        return (closed, killed, failed);
    }

    private static string SafePid(Process p)
    {
        try { return p.Id.ToString(); } catch { return "?"; }
    }
}
