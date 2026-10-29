using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace DSHGuard.Unified;

/// <summary>
/// 会话的写侧。
///
/// DSH 的会话是 <c>~/.dsh/sessions/&lt;项目转义名&gt;/&lt;uuid&gt;/session.*.zstd</c> 整个目录。
/// 本类**不动会话文件内容**（zstd 不是本程序的活），做四件事：
///   · 在资源管理器里打开会话目录或它所在项目；
///   · 把一整个会话目录移进回收区（可还原，不是删除）；
///   · 统计每个项目占了多少会话；
///   · 导出一份能读的会话报告（<see cref="DshSessionReport"/>）+ 原始日志 + 解压脚本。
///
/// 「移进回收区」而不是直接删：会话是用户唯一不可重建的东西。
/// </summary>
public static class DshSessionManager
{
    /// <summary>
    /// 一次「下载日志」的结果：落点目录 + 报告 md 的完整路径。
    /// <paramref name="HasReport"/> 为假表示 md 没写成（原始日志仍在），此时不要去打开文件。
    /// </summary>
    /// <param name="Reused">为真表示复用（或覆盖重写了）已有导出目录，没有新建目录。</param>
    public readonly record struct DshExport(string Dir, string ReportPath, bool HasReport, bool Reused = false);

    /// <summary>回收区根：&lt;ConfigDir&gt;/Unified/trash。</summary>
    public static string TrashRoot => Path.Combine(GuardPaths.ConfigDir, "Unified", "trash");

    /// <summary>会话目录（= 会话文件所在目录）。</summary>
    public static string SessionDir(DshSession s)
    {
        try { return Path.GetDirectoryName(s.FilePath) ?? ""; }
        catch { return ""; }
    }

    /// <summary>用资源管理器打开会话目录。返回错误文案，成功空串。</summary>
    public static string RevealSession(DshSession s)
    {
        return Reveal(SessionDir(s));
    }

    /// <summary>用资源管理器打开会话所属项目目录。</summary>
    public static string RevealProject(DshSession s)
    {
        try
        {
            if (!string.IsNullOrEmpty(s.ProjectDir) && Directory.Exists(s.ProjectDir))
                return Reveal(s.ProjectDir);
            return "项目目录不在了：" + s.ProjectDir;
        }
        catch (Exception ex)
        {
            Logger.LogError("DshSessionManager.RevealProject", ex);
            return "打开失败：" + ex.Message;
        }
    }

    private static string Reveal(string dir)
    {
        try
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return "目录不在了：" + dir;
            var psi = new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true };
            Process.Start(psi);
            return "";
        }
        catch (Exception ex)
        {
            Logger.LogError("DshSessionManager.Reveal", ex);
            return "打开失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 把一个会话目录移进回收区。返回空串成功，否则失败原因。
    /// 回收区路径：&lt;ConfigDir&gt;/Unified/trash/&lt;时间戳&gt;-&lt;会话短 id&gt;/&lt;会话目录名&gt;。
    /// </summary>
    public static string MoveToTrash(DshSession s)
    {
        try
        {
            string dir = SessionDir(s);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return "会话目录已经不在了，可能已被 DSH 清理。";

            // 防止误伤：只接受位于 sessions 根之下的目录
            string sessionsRoot = DshSessionReader.SessionsRoot;
            string full = Path.GetFullPath(dir).TrimEnd('\\', '/');
            string root = Path.GetFullPath(sessionsRoot).TrimEnd('\\', '/');
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return "这个会话不在 DSH 的 sessions 目录里，拒绝移动。";

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string dest = Path.Combine(TrashRoot, stamp + "-" + s.ShortId, Path.GetFileName(full));

            // 撞名保护：时间戳只到秒，同一秒内移两条（或同一会话被移两次）会撞。
            // 与 DshMcpManager.Backup 同法：加 -2/-3 后缀，绝不覆盖已有回收件。
            if (Directory.Exists(dest))
            {
                int n = 2;
                string alt;
                do
                {
                    alt = Path.Combine(TrashRoot, stamp + "-" + s.ShortId + "-" + n, Path.GetFileName(full));
                    n++;
                } while (Directory.Exists(alt) && n < 1000);
                dest = alt;
            }

            string? parent = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent)) Directory.CreateDirectory(parent);

            Directory.Move(full, dest);
            return "";
        }
        catch (Exception ex)
        {
            Logger.LogError("DshSessionManager.MoveToTrash", ex);
            return "移动失败：" + ex.Message;
        }
    }

    /// <summary>真删一条会话，只认 DSH 自己的 sessions 目录。不可恢复，界面上必须先明确告知。</summary>
    public static string Delete(DshSession s)
    {
        try
        {
            string dir = SessionDir(s);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return "会话目录已经不在了。";

            string full = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!IsUnder(full, DshSessionReader.SessionsRoot))
                return "这个会话不在 DSH 的 sessions 目录里，拒绝删除。";

            Directory.Delete(full, recursive: true);
            return "";
        }
        catch (Exception ex)
        {
            Logger.LogError("DshSessionManager.Delete", ex);
            return "删除失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 这条会话已经导出过一份能读的报告吗？有就返回 md 的完整路径，没有返回 null。
    /// 界面靠它决定标题右边要不要画那个文件夹图标。
    /// </summary>
    public static string? FindReport(DshSession s)
    {
        try
        {
            string project = SafeNameForFile(string.IsNullOrWhiteSpace(s.ProjectName) ? "未命名项目" : s.ProjectName, 60);
            string? latest = FindLatestExport(s, project);
            if (string.IsNullOrEmpty(latest)) return null;
            string report = Path.Combine(latest, DshSessionReport.ReportFileName(s));
            return File.Exists(report) ? report : null;
        }
        catch (Exception ex)
        {
            Logger.LogError("DshSessionManager.FindReport", ex);
            return null;
        }
    }

    private static bool IsUnder(string full, string root)
    {
        try
        {
            string r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return full.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// 找这个会话已有的导出目录：&lt;ExportRoot&gt;/&lt;项目&gt;/ 下名字里带 <c>-&lt;短id&gt;</c> 的最新一个。
    /// 目录名里的时间是会话最后写入时间而导出时间不同，所以只按短 id 认、按写盘时间取最新。
    /// </summary>
    private static string? FindLatestExport(DshSession s, string project)
    {
        try
        {
            string root = Path.Combine(ExportRoot, project);
            if (!Directory.Exists(root)) return null;

            string suffix = "-" + s.ShortId;
            string? best = null;
            DateTime bestTime = DateTime.MinValue;

            foreach (string d in Directory.EnumerateDirectories(root))
            {
                string name = Path.GetFileName(d);
                bool hit = name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                    || name.Contains(suffix + "-", StringComparison.OrdinalIgnoreCase);
                if (!hit) continue;

                DateTime t = Directory.GetLastWriteTimeUtc(d);
                if (t > bestTime) { bestTime = t; best = d; }
            }
            return best;
        }
        catch (Exception ex)
        {
            Logger.LogError("DshSessionManager.FindLatestExport", ex);
            return null;
        }
    }

    /// <summary>既有导出的报告是否还新鲜：报告存在，且不比原始日志旧。</summary>
    private static bool IsExportFresh(DshSession s, string dir)
    {
        try
        {
            string report = Path.Combine(dir, DshSessionReport.ReportFileName(s));
            if (!File.Exists(report)) return false;
            if (string.IsNullOrEmpty(s.FilePath) || !File.Exists(s.FilePath)) return true;
            return File.GetLastWriteTimeUtc(report) >= File.GetLastWriteTimeUtc(s.FilePath);
        }
        catch { return false; }
    }
    /// <summary>
    /// 用系统默认程序打开导出的报告 md。返回错误文案，成功返回空串。
    /// 「下载日志」现在直接把它打开 —— 用户拿到手的第一样东西就该是读得懂的文档，
    /// 而不是一个让人自己在文件夹里翻的目录。
    /// </summary>
    public static string OpenReport(string path)
    {
        try
        {
            if (!File.Exists(path)) return "报告文件已不存在：" + path;
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return "";
        }
        catch (Exception ex)
        {
            Logger.LogError("DshSessionManager.OpenReport", ex);
            return "打开报告失败：" + ex.Message;
        }
    }

    /// <summary>导出落点根：<c>Cache\Sessions</c>。会话导出是缓存性质、随时可删，不再写进程序主目录。</summary>
    public static string ExportRoot => GuardPaths.CacheDirSessions;

    /// <summary>
    /// 把一条会话导出成一个文件夹：人看得懂的报告 md + 解开日志.js + 原始 zstd 副本。
    ///
    /// 导出前先查本地：同一个会话已经导过、报告不比原始日志旧，就直接复用那个目录；
    /// 报告过期则复用同一目录覆盖重写，不新建。只有从没导过才新建。
    /// </summary>
    public static DshExport DownloadLog(DshSession s)
    {
        string dir = SessionDir(s);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            throw new DirectoryNotFoundException("会话目录已经不在了，可能已被 DSH 清理。");

        string project = SafeNameForFile(string.IsNullOrWhiteSpace(s.ProjectName) ? "未命名项目" : s.ProjectName, 60);

        // 反复点「下载日志」不该在磁盘上堆一串 -2/-3：先看本地有没有。
        string? latest = FindLatestExport(s, project);
        if (latest != null && IsExportFresh(s, latest))
        {
            string freshReport = Path.Combine(latest, DshSessionReport.ReportFileName(s));
            return new DshExport(latest, freshReport, File.Exists(freshReport), true);
        }

        bool reused = latest != null;
        string dest = latest ?? Path.Combine(ExportRoot, project,
            s.LastWriteTime.ToString("MMdd-HHmm") + "-" + s.ShortId);

        Directory.CreateDirectory(dest);

        // 先拷原始字节并算哈希：报告里要列这份清单，所以必须拷完再生成报告。
        var manifest = new List<(string Name, long Bytes, string Sha256)>();
        foreach (string f in Directory.EnumerateFiles(dir))
        {
            string name = Path.GetFileName(f);
            string target = Path.Combine(dest, name);
            File.Copy(f, target, overwrite: true);
            manifest.Add((name, new FileInfo(target).Length, Sha256OfFile(target)));
        }

        // 报告：生成失败也不该让「原始文件已经拷好了」这件事白费，所以单独兜住。
        string reportPath = Path.Combine(dest, DshSessionReport.ReportFileName(s));
        bool reportOk = false;
        try
        {
            File.WriteAllText(reportPath, DshSessionReport.BuildMarkdown(s, dest, manifest), Utf8Bom);
            reportOk = true;
        }
        catch (Exception ex)
        {
            Logger.LogError("DshSessionManager.DownloadLog(报告)", ex);
            try
            {
                File.WriteAllText(reportPath,
                    "# 会话报告生成失败\r\n\r\n"
                    + "会话：" + s.DisplayName + "\r\n\r\n"
                    + "会话 id：" + s.SessionId + "\r\n\r\n"
                    + "原始日志已完整拷贝到本目录。报告生成时报错：" + ex.Message + "\r\n",
                    Utf8Bom);
                reportOk = true;
            }
            catch { /* 连兜底都失败就作罢：原始文件仍然在，不会白导 */ }
        }

        // 解压脚本：同样「有没有都不该挡住建档」，单独兜住。
        try
        {
            File.WriteAllText(Path.Combine(dest, DshSessionReport.DecoderScriptName),
                DshSessionReport.DecoderScript, new UTF8Encoding(false));
        }
        catch (Exception ex) { Logger.LogError("DshSessionManager.DownloadLog(脚本)", ex); }

        return new DshExport(dest, reportPath, reportOk, reused);
    }

    /// <summary>带 BOM 的 UTF-8：报告里全是中文，Windows 记事本靠 BOM 认编码。</summary>
    private static readonly UTF8Encoding Utf8Bom = new UTF8Encoding(true);

    /// <summary>算文件 SHA-256（大写十六进制）；读不动返回 "—" 而不抛。</summary>
    private static string Sha256OfFile(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var sha = System.Security.Cryptography.SHA256.Create();
            byte[] hash = sha.ComputeHash(fs);
            var sb = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash) sb.Append(b.ToString("X2"));
            return sb.ToString();
        }
        catch (Exception ex)
        {
            Logger.LogError("DshSessionManager.Sha256OfFile", ex);
            return "—";
        }
    }

    /// <summary>
    /// 把任意字符串压成合法的 Windows 文件名/文件夹名（去非法字符、截断、防空）。
    /// 文件名和文件夹名共用一套规则，所以只留这一个公开入口；<paramref name="max"/> &lt;= 0 表示不截断。
    /// </summary>
    public static string SafeNameForFile(string raw, int max)
    {
        string src = raw ?? "";
        var bad = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(src.Length);
        foreach (char c in src)
            sb.Append(Array.IndexOf(bad, c) >= 0 ? '_' : c);
        string s = sb.ToString().Trim().TrimEnd('.');
        if (max > 0 && s.Length > max) s = s.Substring(0, max).TrimEnd().TrimEnd('.');
        return s.Length == 0 ? "未命名" : s;
    }

    /// <summary>回收区里已有多少份被移走的会话（给界面提示用）。</summary>
    public static int TrashCount()
    {
        try
        {
            if (!Directory.Exists(TrashRoot)) return 0;
            return Directory.GetDirectories(TrashRoot).Length;
        }
        catch { return 0; }
    }

    /// <summary>按项目汇总会话数与总字节。用于分区头部一行摘要。</summary>
    public static List<(string ProjectName, int Count, long Bytes)> Summary(IEnumerable<DshSession> sessions)
    {
        try
        {
            return sessions
                .GroupBy(s => s.ProjectName, StringComparer.OrdinalIgnoreCase)
                .Select(g => (g.Key, g.Count(), g.Sum(x => x.SizeBytes)))
                .OrderByDescending(t => t.Item2)
                .ToList();
        }
        catch (Exception ex)
        {
            Logger.LogError("DshSessionManager.Summary", ex);
            return new List<(string, int, long)>();
        }
    }
}
