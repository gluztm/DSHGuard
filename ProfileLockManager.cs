using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace DSHGuard;

/// <summary>
/// 2.4.3：Profile 目录占用进程侦察与安全解除工具。
/// <para>
/// 问题背景：当 DSH 异常退出或某个插件子进程（如 <c>@dsh-ops/bash-win32-x64</c> 拉起的 <c>bash.exe</c> /
/// <c>sh.exe</c>、Python/uv/Node 后台工具进程）未随主程序一起退出时，会长期锁住
/// <c>profiles\desktop\node_modules</c> 下的文件，导致后续插件更新/卸载在 <c>os error 5 / 32 / EPERM / EBUSY</c> 上反复失败。
/// </para>
/// <para>
/// 安全边界（严防误杀）：
///   1. 永不触碰自身进程（<see cref="Environment.ProcessId"/>）与守护壳安装目录进程；
///   2. 永不自动强杀外部独立软件（如 VS Code、资源管理器等 <see cref="OccupierKind.ExternalApp"/>），仅作只读提示；
///   3. 永不误伤其他工作区（如 <c>Antigravity-Data</c>、<c>Codex-Data</c>）里运行中的 <c>node.exe</c> / <c>python.exe</c>，
///      仅当映像路径、工作目录、命令行边界明确指向目标 <c>profileDir</c> 或被 Restart Manager 证实持锁时，才归为 <see cref="OccupierKind.PluginResidue"/>。
/// </para>
/// </summary>
internal static class ProfileLockManager
{
    public enum OccupierKind
    {
        /// <summary>驻留或锁住目标 profile 目录的插件子进程（可安全自动/一键解除）。</summary>
        PluginResidue,
        /// <summary>DSH 主程序进程（应提示用户走「结束桌面版/引擎」流程）。</summary>
        DshHostProcess,
        /// <summary>占用了 profile 内文件但非插件宿主的其他外部应用（只提示用户手动关闭，绝不自动强杀）。</summary>
        ExternalApp,
    }

    public sealed record LockProcessEntry(
        int Pid,
        string ProcessName,
        string ExecutablePath,
        string Reason,
        string StartTimeText,
        OccupierKind Kind);

    private static readonly HashSet<string> PluginHostProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "node", "python", "python3", "pythonw", "uv", "uvx",
        "bun", "deno", "bash", "sh", "git", "cmd", "conhost",
        "esbuild", "rg", "tsx", "npx", "pnpm",
    };

    /// <summary>
    /// 进程分类纯函数（供扫描引擎与自检共同使用，严防误杀）。
    /// 返回 <c>null</c> 表示应忽略/排除（自身、守护壳目录、系统内核进程或与目标 profile 无关的外部进程）。
    /// </summary>
    internal static OccupierKind? ClassifyOccupier(
        int pid,
        int selfPid,
        string? processName,
        string? exePath,
        string? currentDir,
        string? commandLine,
        string profileDir,
        string? desktopInstallDir,
        string? guardBaseDir,
        bool lockedViaRestartManager)
    {
        if (pid <= 4 || pid == selfPid) return null;

        string normGuard = NormalizeDir(guardBaseDir);
        string normExe = NormalizePath(exePath);
        if (normGuard.Length > 0 && IsPathInsideDir(normExe, normGuard))
            return null;

        string rawName = StripExeExtension(processName);
        if (rawName.Length == 0 && normExe.Length > 0)
            rawName = StripExeExtension(Path.GetFileName(normExe));

        string normDesk = NormalizeDir(desktopInstallDir);
        string desktopExeBase = StripExeExtension(GuardPaths.DesktopExeName);
        if ((desktopExeBase.Length > 0 && string.Equals(rawName, desktopExeBase, StringComparison.OrdinalIgnoreCase))
            || (normDesk.Length > 0 && IsPathInsideDir(normExe, normDesk)))
        {
            return OccupierKind.DshHostProcess;
        }

        string normProfile = NormalizeDir(profileDir);
        if (normProfile.Length == 0) return null;

        if (IsPathInsideDir(normExe, normProfile))
            return OccupierKind.PluginResidue;

        if (PluginHostProcessNames.Contains(rawName))
        {
            bool cwdInProfile = IsPathInsideDir(NormalizeDir(currentDir), normProfile);
            bool cmdInProfile = ContainsPathPrefix(commandLine, normProfile);
            if (lockedViaRestartManager || cwdInProfile || cmdInProfile)
                return OccupierKind.PluginResidue;
            return null;
        }

        return lockedViaRestartManager ? OccupierKind.ExternalApp : null;
    }

    /// <summary>
    /// 从 pnpm / Node 失败输出中提取落在目标 <paramref name="profileDir"/> 内的被占用文件路径，供 Restart Manager 精准加扫。
    /// </summary>
    internal static List<string> ExtractLockedPathsFromOutput(string? output, string? profileDir)
    {
        var list = new List<string>();
        string normProfile = NormalizeDir(profileDir);
        if (string.IsNullOrWhiteSpace(output) || normProfile.Length == 0) return list;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 匹配带盘符的 Windows 路径（常出现在引号或 EPERM/EBUSY/os error 原文中）
        foreach (Match m in Regex.Matches(output, @"[A-Za-z]:\\[^""'\r\n\t<>|*?]+"))
        {
            string candidate = m.Value.Trim().TrimEnd('.', ',', ';', ')', ']', '}');
            string norm = NormalizePath(candidate);
            if (norm.Length == 0 || !IsPathInsideDir(norm, normProfile)) continue;
            if (File.Exists(norm) && seen.Add(norm))
                list.Add(norm);
        }
        return list;
    }

    /// <summary>
    /// 扫描锁住或驻留在 <paramref name="profileDir"/> 下的进程。永不抛异常。
    /// </summary>
    internal static List<LockProcessEntry> ScanProfileLocks(string? profileDir, IEnumerable<string>? extraPaths = null)
    {
        var result = new List<LockProcessEntry>();
        string normProfile = NormalizeDir(profileDir);
        if (normProfile.Length == 0 || !Directory.Exists(normProfile)) return result;

        int selfPid = Environment.ProcessId;
        string guardBase = AppContext.BaseDirectory;
        string deskInstall = GuardPaths.DesktopInstallDir;

        var rmLockedPids = new HashSet<int>();
        var rmAppNames = new Dictionary<int, string>();
        try
        {
            var probeFiles = CollectProbeFiles(normProfile, extraPaths);
            if (probeFiles.Count > 0)
                QueryRestartManagerLocks(probeFiles, rmLockedPids, rmAppNames);
        }
        catch (Exception ex) { Logger.LogError("ProfileLockManager.RM", ex); }

        var seenPids = new HashSet<int>();
        Process[] procs;
        try { procs = Process.GetProcesses(); }
        catch (Exception ex) { Logger.LogError("ProfileLockManager.GetProcesses", ex); return result; }

        foreach (var p in procs)
        {
            try
            {
                int pid = p.Id;
                if (!seenPids.Add(pid) || pid <= 4 || pid == selfPid) continue;

                string procName = "";
                try { procName = p.ProcessName ?? ""; } catch { }
                bool viaRm = rmLockedPids.Contains(pid);
                string exePath = QueryProcessImagePath(pid);
                if (exePath.Length == 0)
                {
                    try { exePath = p.MainModule?.FileName ?? ""; } catch { }
                }

                string rawName = StripExeExtension(procName);
                bool mightInspectPeb = viaRm
                    || PluginHostProcessNames.Contains(rawName)
                    || IsPathInsideDir(NormalizePath(exePath), normProfile);

                string cwd = "";
                string cmdLine = "";
                if (mightInspectPeb)
                    TryReadProcessParameters(pid, out cwd, out cmdLine);

                var kind = ClassifyOccupier(
                    pid,
                    selfPid,
                    procName,
                    exePath,
                    cwd,
                    cmdLine,
                    normProfile,
                    deskInstall,
                    guardBase,
                    viaRm);
                if (kind == null) continue;

                string startText = "";
                try { startText = p.StartTime.ToString("HH:mm:ss"); } catch { }
                if (procName.Length == 0 && rmAppNames.TryGetValue(pid, out var rmName) && rmName.Length > 0)
                    procName = rmName;

                string reason = BuildOccupierReason(kind.Value, exePath, cwd, cmdLine, normProfile, viaRm);
                result.Add(new LockProcessEntry(pid, procName, exePath, reason, startText, kind.Value));
            }
            catch { }
            finally { try { p.Dispose(); } catch { } }
        }

        // 若某进程已退出或权限受限未出现在上面列表但 RM 明确返回了它，按安全边界分类补入
        foreach (int pid in rmLockedPids)
        {
            if (seenPids.Contains(pid) || pid <= 4 || pid == selfPid) continue;
            string rmName = rmAppNames.TryGetValue(pid, out var n) ? n : "未知进程";
            var kind = ClassifyOccupier(pid, selfPid, rmName, "", "", "", normProfile, deskInstall, guardBase, true);
            if (kind == null) continue;
            result.Add(new LockProcessEntry(pid, rmName, "", "系统文件锁（Restart Manager 检测到正在占用文件）", "", kind.Value));
        }

        result.Sort((a, b) =>
        {
            int c = a.Kind.CompareTo(b.Kind);
            return c != 0 ? c : a.Pid.CompareTo(b.Pid);
        });
        return result;
    }

    /// <summary>
    /// 仅解除 <see cref="OccupierKind.PluginResidue"/> 残留插件进程的占用（绝不强杀 DSH 主程序或外部应用）。
    /// 复用 <see cref="AppSiblings.Terminate"/> 结束进程树，并在之后留出短暂窗口让系统释放文件句柄。
    /// </summary>
    internal static (int Closed, int Killed, int Failed) ReleasePluginResidues(
        IReadOnlyList<LockProcessEntry> entries,
        GuardTarget? logTarget = null)
    {
        if (entries == null || entries.Count == 0) return (0, 0, 0);
        var pids = entries
            .Where(e => e.Kind == OccupierKind.PluginResidue && e.Pid > 4 && e.Pid != Environment.ProcessId)
            .Select(e => e.Pid)
            .Distinct()
            .ToList();
        if (pids.Count == 0) return (0, 0, 0);

        foreach (var e in entries.Where(x => pids.Contains(x.Pid)))
        {
            Logger.NoteDiagnosis(
                $"准备解除 profile 目录占用：PID {e.Pid}（{e.ProcessName}）· {e.Reason}"
                + (e.ExecutablePath.Length > 0 ? $" · {e.ExecutablePath}" : ""),
                logTarget);
        }

        var r = AppSiblings.Terminate(pids, waitMs: 2500, logTarget: logTarget);
        // 进程树终止后稍微等待 Windows 内核回收句柄
        if (r.closed + r.killed > 0)
            Thread.Sleep(300);
        return r;
    }

    private static string BuildOccupierReason(
        OccupierKind kind, string exePath, string cwd, string cmdLine, string normProfile, bool viaRm)
    {
        if (kind == OccupierKind.DshHostProcess)
            return "DSH 主程序仍在运行";
        if (kind == OccupierKind.ExternalApp)
            return "外部程序正在打开 profile 目录内的文件（需手动关闭）";

        string pkgHint = ExtractPluginPackageHint(exePath)
            ?? ExtractPluginPackageHint(cwd)
            ?? ExtractPluginPackageHint(cmdLine)
            ?? "";
        string prefix = pkgHint.Length > 0 ? $"插件 {pkgHint} 残留子进程" : "profile 残留插件进程";

        var evidences = new List<string>();
        if (IsPathInsideDir(NormalizePath(exePath), normProfile)) evidences.Add("映像位于 profile 内");
        if (IsPathInsideDir(NormalizeDir(cwd), normProfile)) evidences.Add("工作目录在 profile 内");
        if (ContainsPathPrefix(cmdLine, normProfile)) evidences.Add("命令行指向 profile");
        if (viaRm) evidences.Add("持有文件锁");
        return evidences.Count > 0 ? $"{prefix}（{string.Join("、", evidences)}）" : prefix;
    }

    private static string? ExtractPluginPackageHint(string? pathOrCmd)
    {
        if (string.IsNullOrWhiteSpace(pathOrCmd)) return null;
        string s = pathOrCmd.Replace('/', '\\');
        int idx = s.IndexOf(@"\node_modules\", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        string tail = s[(idx + @"\node_modules\".Length)..].TrimStart('\\');
        if (tail.StartsWith(".pnpm\\", StringComparison.OrdinalIgnoreCase))
        {
            int innerIdx = tail.IndexOf(@"\node_modules\", StringComparison.OrdinalIgnoreCase);
            if (innerIdx >= 0)
                tail = tail[(innerIdx + @"\node_modules\".Length)..].TrimStart('\\');
        }
        var parts = tail.Split(new[] { '\\', ' ', '"', '\'' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        if (parts[0].StartsWith("@", StringComparison.Ordinal) && parts.Length >= 2)
            return parts[0] + "/" + parts[1];
        return parts[0];
    }

    private static List<string> CollectProbeFiles(string normProfile, IEnumerable<string>? extraPaths)
    {
        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        const int maxFiles = 256;

        void AddFile(string? p)
        {
            if (files.Count >= maxFiles || string.IsNullOrWhiteSpace(p)) return;
            string norm = NormalizePath(p);
            if (norm.Length > 0 && File.Exists(norm) && seen.Add(norm))
                files.Add(norm);
        }

        if (extraPaths != null)
        {
            foreach (string p in extraPaths) AddFile(p);
        }

        foreach (string rootName in new[] { "package.json", "pnpm-lock.yaml", "cordis.yml", "cordis.patch.yml" })
            AddFile(Path.Combine(normProfile, rootName));

        string nm = Path.Combine(normProfile, "node_modules");
        if (Directory.Exists(nm) && files.Count < maxFiles)
        {
            try
            {
                var opts = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.System,
                };
                foreach (string f in Directory.EnumerateFiles(nm, "*", opts))
                {
                    if (files.Count >= maxFiles) break;
                    string ext = Path.GetExtension(f);
                    if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                        || ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)
                        || ext.Equals(".node", StringComparison.OrdinalIgnoreCase)
                        || ext.Equals(".pyd", StringComparison.OrdinalIgnoreCase))
                    {
                        AddFile(f);
                    }
                }
            }
            catch { }
        }
        return files;
    }

    private static void QueryRestartManagerLocks(
        List<string> probeFiles,
        HashSet<int> pids,
        Dictionary<int, string> appNames)
    {
        uint session = 0;
        var keyBuf = new char[256];
        int rc = RmStartSession(out session, 0, keyBuf);
        if (rc != 0) return;

        try
        {
            const int batchSize = 64;
            for (int offset = 0; offset < probeFiles.Count; offset += batchSize)
            {
                var batch = probeFiles.Skip(offset).Take(batchSize).ToArray();
                if (batch.Length == 0) continue;
                int regRc = RmRegisterResources(session, (uint)batch.Length, batch, 0, IntPtr.Zero, 0, null);
                if (regRc != 0) continue;

                uint needed = 0;
                uint count = 0;
                uint reasons = 0;
                int listRc = RmGetList(session, out needed, ref count, null, ref reasons);
                if (listRc != 234 /* ERROR_MORE_DATA */ || needed == 0) continue;

                var infos = new RM_PROCESS_INFO[needed];
                count = needed;
                listRc = RmGetList(session, out needed, ref count, infos, ref reasons);
                if (listRc != 0) continue;

                for (int i = 0; i < count; i++)
                {
                    int pid = infos[i].Process.dwProcessId;
                    if (pid <= 4) continue;
                    pids.Add(pid);
                    string app = (infos[i].strAppName ?? "").Trim();
                    if (app.Length > 0 && !appNames.ContainsKey(pid))
                        appNames[pid] = app;
                }
            }
        }
        finally
        {
            try { _ = RmEndSession(session); } catch { }
        }
    }

    private static string QueryProcessImagePath(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return "";
        try
        {
            var buf = new char[1024];
            int len = buf.Length;
            if (QueryFullProcessImageName(h, 0, buf, ref len) && len > 0)
                return new string(buf, 0, len);
            return "";
        }
        catch { return ""; }
        finally { CloseHandle(h); }
    }

    private static void TryReadProcessParameters(int pid, out string currentDir, out string commandLine)
    {
        currentDir = "";
        commandLine = "";
        if (!Environment.Is64BitProcess) return;

        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, false, pid);
        if (h == IntPtr.Zero) return;
        try
        {
            var pbi = new PROCESS_BASIC_INFORMATION();
            if (NtQueryInformationProcess(h, 0, ref pbi, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _) != 0
                || pbi.PebBaseAddress == IntPtr.Zero)
                return;

            var ptrBuf = new byte[8];
            if (!ReadProcessMemory(h, IntPtr.Add(pbi.PebBaseAddress, 0x20), ptrBuf, 8, out _))
                return;
            IntPtr procParams = (IntPtr)BitConverter.ToInt64(ptrBuf, 0);
            if (procParams == IntPtr.Zero) return;

            currentDir = ReadRemoteUnicodeString(h, procParams, 0x38);
            commandLine = ReadRemoteUnicodeString(h, procParams, 0x70);
        }
        catch { }
        finally { CloseHandle(h); }
    }

    private static string ReadRemoteUnicodeString(IntPtr hProcess, IntPtr baseAddr, int offset)
    {
        var hdr = new byte[16];
        if (!ReadProcessMemory(hProcess, IntPtr.Add(baseAddr, offset), hdr, 16, out _)) return "";
        ushort len = BitConverter.ToUInt16(hdr, 0);
        IntPtr strPtr = (IntPtr)BitConverter.ToInt64(hdr, 8);
        if (len == 0 || len > 16384 || strPtr == IntPtr.Zero) return "";
        var data = new byte[len];
        if (!ReadProcessMemory(hProcess, strPtr, data, len, out _)) return "";
        return Encoding.Unicode.GetString(data).TrimEnd('\0');
    }

    private static string NormalizeDir(string? path)
    {
        string s = (path ?? "").Trim().Trim('"');
        if (s.Length == 0) return "";
        try { return Path.GetFullPath(s).TrimEnd('\\', '/'); }
        catch { return s.Replace('/', '\\').TrimEnd('\\'); }
    }

    private static string NormalizePath(string? path)
    {
        string s = (path ?? "").Trim().Trim('"');
        if (s.Length == 0) return "";
        try { return Path.GetFullPath(s); }
        catch { return s.Replace('/', '\\'); }
    }

    private static string StripExeExtension(string? name)
    {
        string n = (name ?? "").Trim();
        if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            n = n[..^4];
        return n;
    }

    internal static bool IsPathInsideDir(string? candidatePath, string? normDir)
    {
        string c = NormalizeDir(candidatePath);
        string d = NormalizeDir(normDir);
        if (c.Length == 0 || d.Length == 0) return false;
        return string.Equals(c, d, StringComparison.OrdinalIgnoreCase)
            || c.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool ContainsPathPrefix(string? haystack, string? normDir)
    {
        if (string.IsNullOrWhiteSpace(haystack)) return false;
        string d = NormalizeDir(normDir);
        if (d.Length == 0) return false;

        string normalized = haystack.Replace('/', '\\');
        int start = 0;
        while (start < normalized.Length)
        {
            int idx = normalized.IndexOf(d, start, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return false;
            bool leftOk = idx == 0 || normalized[idx - 1] is ' ' or '"' or '\'' or '=' or ':' or '\t';
            int after = idx + d.Length;
            bool rightOk = after >= normalized.Length || normalized[after] is '\\' or '"' or '\'' or ' ' or '\t';
            if (leftOk && rightOk) return true;
            start = idx + 1;
        }
        return false;
    }

    // ── Win32 P/Invoke ──────────────────────────────────────────────────────

    private const uint PROCESS_VM_READ = 0x0010;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string strServiceShortName;
        public uint ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bRestartable;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr Reserved3;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, [Out] char[] strSessionKey);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint pSessionHandle,
        uint nFiles,
        string[] rgsFileNames,
        uint nApplications,
        IntPtr rgApplications,
        uint nServices,
        string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmGetList(
        uint dwSessionHandle,
        out uint pnProcInfoNeeded,
        ref uint pnProcInfo,
        [In, Out] RM_PROCESS_INFO[]? rgAffectedApps,
        ref uint lpdwRebootReasons);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, [Out] char[] lpExeName, ref int lpdwSize);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr ProcessHandle,
        int ProcessInformationClass,
        ref PROCESS_BASIC_INFORMATION ProcessInformation,
        int ProcessInformationLength,
        out int ReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(
        IntPtr hProcess,
        IntPtr lpBaseAddress,
        [Out] byte[] lpBuffer,
        int nSize,
        out IntPtr lpNumberOfBytesRead);
}
