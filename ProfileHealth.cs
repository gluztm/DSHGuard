using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace DSHGuard;

/// <summary>
/// 配置文件（DSH profile）模块链接体检。
///
/// DSH 会在 profile 的 node_modules 里用「目录联接」挂一批公共依赖（react 一类）。
/// 联接记录的是**绝对路径**：一旦它指向的位置消失（数据目录被搬走、临时数据被清理、
/// 换个盘跑过一次），引擎启动时解析这些模块就会失败并打印「找不到模块」，
/// 而端口永远不会就绪——从外面看只是一句"启动超时"，排查方向会被带偏。
/// 这里负责把它们找出来并接回仍然存在的位置。
/// </summary>
public static class ProfileHealth
{
    /// <summary>一个目标已消失的目录联接。</summary>
    public sealed class BrokenLink
    {
        public string Link { get; init; } = "";
        public string DeadTarget { get; init; } = "";
        public string Name => Path.GetFileName(Link);
    }

    /// <summary>找回退目录的最大层数（profile 根 → 子 profile → 回退目录）。</summary>
    private const int MaxSearchDepth = 3;

    /// <summary>扫描 profile 下各 node_modules 中指向已消失位置的目录联接（只读）。</summary>
    public static List<BrokenLink> FindBrokenModuleLinks(string profileRoot)
    {
        var found = new List<BrokenLink>();
        try
        {
            if (!Directory.Exists(profileRoot)) return found;
            foreach (string nm in EnumerateNodeModules(profileRoot))
            {
                IEnumerable<string> entries;
                try { entries = Directory.EnumerateDirectories(nm); }
                catch { continue; }

                foreach (string entry in entries)
                {
                    try
                    {
                        string? target = new DirectoryInfo(entry).LinkTarget;
                        if (string.IsNullOrEmpty(target)) continue;             // 普通目录，不碰
                        if (Directory.Exists(target) || File.Exists(target)) continue;   // 联接正常
                        found.Add(new BrokenLink { Link = entry, DeadTarget = target! });
                    }
                    catch { }
                }
            }
        }
        catch { }
        return found;
    }

    /// <summary>
    /// 接回失效联接：优先指向同名的「模块回退」目录，其次指向上级 node_modules 里的同名包。
    /// 只处理**目标已消失**的联接；找不到可用位置时保持原样，不做任何破坏性动作。
    /// </summary>
    public static int RepairBrokenModuleLinks(string profileRoot, IReadOnlyList<BrokenLink> links, out string detail)
    {
        int repaired = 0;
        var skipped = new List<string>();
        foreach (var link in links)
        {
            string? target = FindLiveTarget(profileRoot, link);
            if (target == null) { skipped.Add(link.Name); continue; }
            if (!DeleteLink(link.Link)) { skipped.Add(link.Name); continue; }
            if (!CreateJunction(link.Link, target)) { skipped.Add(link.Name); continue; }
            repaired++;
        }

        detail = repaired == 0
            ? (skipped.Count > 0 ? $"未能修复：{string.Join("、", skipped)}" : "没有需要修复的链接")
            : $"已修复 {repaired} 个失效的模块链接" + (skipped.Count > 0 ? $"（{string.Join("、", skipped)} 暂无法处理）" : "");
        return repaired;
    }

    /// <summary>为失效联接找一个仍然存在的位置：回退目录 → 上级 node_modules → profile 根。</summary>
    private static string? FindLiveTarget(string profileRoot, BrokenLink link)
    {
        var candidates = new List<string>();
        try
        {
            string nm = Path.GetDirectoryName(link.Link) ?? "";          // …\web\node_modules
            string owner = Path.GetDirectoryName(nm) ?? "";              // …\web
            if (owner.Length > 0)
            {
                candidates.Add(Path.Combine(owner, ".dsh-module-fallback", "node_modules", link.Name));
                string parent = Path.GetDirectoryName(owner) ?? "";      // …\profiles
                if (parent.Length > 0) candidates.Add(Path.Combine(parent, "node_modules", link.Name));
            }
            candidates.Add(Path.Combine(profileRoot, "node_modules", link.Name));
        }
        catch { }

        foreach (string c in candidates)
        {
            try { if (Directory.Exists(c)) return c; }
            catch { }
        }
        return null;
    }

    // ══════════════ 缺失包补链：首次安装拦路虎的自愈 ══════════════

    /// <summary>
    /// 从引擎报错文本里挖出「找不到的包名」（`Cannot find package 'X'` / ERR_MODULE_NOT_FOUND）。
    /// 这是首次安装最常见的拦路虎：配置文件里解析不到引擎自带的组件，引擎刚起来就退。
    /// </summary>
    public static List<string> FindMissingPackages(string? text)
    {
        var names = new List<string>();
        try
        {
            if (string.IsNullOrEmpty(text)) return names;
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                         text, @"Cannot find package '([^']+)'"))
            {
                string n = m.Groups[1].Value.Trim();
                if (n.Length > 0 && !names.Contains(n, StringComparer.OrdinalIgnoreCase)) names.Add(n);
            }
        }
        catch { }
        return names;
    }

    /// <summary>
    /// 从引擎输出里认出它自己所在的安装目录（调用栈里带 `…\_npx\&lt;hash&gt;\node_modules\@deepseek-ai\…`）。
    /// 补链必须指向「这次真正要跑的那个引擎」自己的包，不能拿别的版本的凑。
    /// </summary>
    public static string FindEngineNodeModules(string? text)
    {
        try
        {
            if (string.IsNullOrEmpty(text)) return "";
            // (?<![A-Za-z0-9]) 很关键：否则会把 "file:///C:/…" 里的 "e:" 当成盘符，截出半截路径
            var m = System.Text.RegularExpressions.Regex.Match(text,
                @"(?<![A-Za-z0-9])([A-Za-z]:[\\/][^\s""']*?[\\/]_npx[\\/][^\\/\s""']+[\\/]node_modules)[\\/]@deepseek-ai[\\/]",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return m.Success
                ? m.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar)   // 统一成 Windows 分隔符
                : "";
        }
        catch { return ""; }
    }

    /// <summary>删除目录联接本身（不动它指向的内容）。</summary>
    private static bool DeleteLink(string link)
    {
        try
        {
            if (!Directory.Exists(link))   // 目标已消失的联接：Directory.Exists 为 false，走命令行删
                return RunCmd($"rmdir \"{link}\"");
            Directory.Delete(link, recursive: false);
            return !Directory.Exists(link);
        }
        catch
        {
            try { return RunCmd($"rmdir \"{link}\""); }
            catch { return false; }
        }
    }

    /// <summary>建立目录联接（junction 不需要管理员权限，符号链接需要）。</summary>
    private static bool CreateJunction(string link, string target)
        => RunCmd($"mklink /J \"{link}\" \"{target}\"");

    /// <summary>自检用：按同样方式造一个目录联接。</summary>
    internal static bool CreateJunctionForTest(string link, string target) => CreateJunction(link, target);

    private static bool RunCmd(string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c " + args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            return p.WaitForExit(15000) && p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>列出 profile 下所有 node_modules 目录（含子 profile 与模块回退目录，层数有上限）。</summary>
    private static IEnumerable<string> EnumerateNodeModules(string root)
    {
        var result = new List<string>();
        Walk(root, 0);
        return result;

        void Walk(string dir, int depth)
        {
            if (depth > MaxSearchDepth) return;
            try
            {
                string nm = Path.Combine(dir, "node_modules");
                if (Directory.Exists(nm)) result.Add(nm);
                foreach (string sub in Directory.EnumerateDirectories(dir))
                {
                    if (Path.GetFileName(sub).Equals("node_modules", StringComparison.OrdinalIgnoreCase)) continue;
                    Walk(sub, depth + 1);
                }
            }
            catch { }
        }
    }
}
