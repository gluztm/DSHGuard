using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DSHGuard;

/// <summary>
/// 「重置 DSH 配置」：把出问题的配置文件目录整体挪到一边（不删），让引擎下次启动**从零重建**。
///
/// 为谁做的（2026-09-13 现场反馈）：
///   全新机器、从没跑过 DSH，装好守护壳一点启动就报错，还劝人"回滚到 0.1.1-rc.2"——
///   可那台机器上根本没有那个版本，回滚是空头支票。这时候唯一有意义的一条路就是：
///   **把配置文件重置掉，让 DSH 自己重建**（本机没有插件可丢，风险最小）。
///
/// 安全约定：只做"改名搬走"，绝不删除；旧目录保留成 `web.bak-时间戳`，随时能搬回来。
/// </summary>
internal static class ProfileReset
{
    /// <summary>
    /// 「配置文件目录」到底在哪：**一律用 GuardPaths.ProfileDir**（`~\.dsh\profiles\web`）。
    /// 复查抓到的严重 bug：之前传的是 `_settings.PathProfile`，它默认为**空串**（只有用户在
    /// 「设置 → 路径」手填过才有值）⇒ 联接清理空转、重置按钮在全新机器上什么都不做，
    /// 而日志还写着「已清理」。所有需要这个目录的地方都必须走这里。
    /// </summary>
    public static string TargetProfileDir()
    {
        try
        {
            string dir = GuardPaths.ProfileDir;
            if (!string.IsNullOrWhiteSpace(dir)) return dir;
        }
        catch { }
        try { return Path.Combine(GuardPaths.DshHome, "profiles", "web"); }
        catch { return ""; }
    }

    /// <summary>
    /// 清理要扫的目录：**只扫本程序 1.1.12 那批补链去过的两个位置**。
    /// 特别说明：`profiles\node_modules\` 里那批指向 `_npx` 的联接是 **DSH 自己在激活时写的**
    /// （复查实测 166 个，形态与本程序造的几乎一样），删了会伤 DSH —— 所以**绝不进那一层**。
    /// </summary>
    public static List<string> CleanupRoots(string profileDir)
    {
        var roots = new List<string>();
        try
        {
            if (string.IsNullOrWhiteSpace(profileDir)) return roots;
            roots.Add(Path.Combine(profileDir, "node_modules", "@deepseek-ai"));
            roots.Add(Path.Combine(profileDir, ".dsh-module-fallback", "node_modules", "@deepseek-ai"));
        }
        catch { }
        return roots;
    }

    /// <summary>目标路径是不是真的在 npm 缓存 `_npx\` 目录下（比「含 _npx\ 子串」严得多）。</summary>
    public static bool IsUnderNpxCache(string? target)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(target)) return false;
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "npm-cache", "_npx") + Path.DirectorySeparatorChar;
            // 只认"确实落在这台机器的 npm 缓存 _npx 下"，不接受"路径里碰巧含 _npx\" ——
            // 免得把别人自己 mklink 到别处的联接当成本程序造的删掉（复查建议）
            return target!.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
    /// <summary>这台机器像不像"从没跑过 DSH"：配置文件目录不存在，或里面没有插件清单。</summary>
    public static bool LooksBrandNew(string profileDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(profileDir) || !Directory.Exists(profileDir)) return true;
            string pkg = Path.Combine(profileDir, "package.json");
            return !File.Exists(pkg);
        }
        catch { return false; }
    }

    /// <summary>配置文件目录里装了几个插件（用来提醒"重置会丢什么"；读不出来返回 -1）。</summary>
    public static int CountPlugins(string profileDir)
    {
        try
        {
            string nm = Path.Combine(profileDir, "node_modules");
            if (!Directory.Exists(nm)) return 0;
            int n = 0;
            foreach (string dir in Directory.GetDirectories(nm))
            {
                string name = Path.GetFileName(dir);
                if (name.StartsWith(".", StringComparison.Ordinal)) continue;
                if (name.StartsWith("@", StringComparison.Ordinal))
                {
                    try { n += Directory.GetDirectories(dir).Length; } catch { }
                }
                else n++;
            }
            return n;
        }
        catch { return -1; }
    }

    /// <summary>搬走时用的备份目录名（同秒重复调用也不会互相覆盖）。</summary>
    public static string BackupPathFor(string profileDir, DateTime now)
    {
        string stamp = now.ToString("yyyyMMdd-HHmmss");
        string basePath = profileDir.TrimEnd('\\', '/') + ".bak-" + stamp;
        string path = basePath;
        for (int i = 2; Directory.Exists(path) && i < 100; i++) path = basePath + "-" + i;
        return path;
    }

    /// <summary>
    /// 把配置文件目录整体改名搬走（**不删除**）。返回是否成功与搬到了哪里。
    /// 目录不存在时视为已完成（引擎下次启动会自己建新的）。
    /// </summary>
    public static (bool Ok, string Detail) MoveAside(string profileDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(profileDir)) return (false, "配置文件目录为空");
            if (!Directory.Exists(profileDir)) return (true, "配置文件目录本来就不存在，无需搬走");

            string backup = BackupPathFor(profileDir, DateTime.Now);
            try
            {
                Directory.Move(profileDir, backup);
            }
            catch (Exception ex)
            {
                // 跨盘/被占用等情况：退回"先复制再删"代价太大，这里直接如实上报
                return (false, $"搬走失败：{ex.Message}");
            }
            return (true, "已搬到 " + backup);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>
    /// 清掉**本程序自己造**的组件联接：配置文件目录里指向 `_npx` 缓存的那批目录联接。
    ///
    /// 为什么必须清（2026-09-13 实测）：npx 会优先从"当前目录的 node_modules"找包，
    /// 这些联接让 `npx @deepseek-ai/dsh@固定版本` 改成用缓存里碰巧链到的那份，
    /// **固定版本被绕过**、引擎入口 require 直接炸掉。判据很严：只删"是目录联接 **且** 目标在
    /// `_npx` 下"的条目；pnpm 装的正经包、普通目录一律不碰。
    /// </summary>
    public static string RemoveGuardJunctions(string profileDir)
    {
        int removed = 0, kept = 0;
        var failed = new List<string>();
        try
        {
            if (string.IsNullOrWhiteSpace(profileDir) || !Directory.Exists(profileDir)) return "配置文件目录不存在，无需清理";

            string[] roots = CleanupRoots(profileDir).ToArray();

            foreach (string root in roots)
            {
                if (!Directory.Exists(root)) continue;
                foreach (string entry in Directory.GetDirectories(root))
                {
                    try
                    {
                        var info = new DirectoryInfo(entry);
                        string? target = info.LinkTarget;
                        // 判据收紧：必须真的落在 %LOCALAPPDATA%\npm-cache\_npx\ 之下
                        bool ours = IsUnderNpxCache(target);
                        if (!ours) { kept++; continue; }

                        // 目录联接用 rmdir 删（不递归、不动目标内容）
                        Directory.Delete(entry, recursive: false);
                        if (Directory.Exists(entry)) { failed.Add(Path.GetFileName(entry)); }
                        else removed++;
                    }
                    catch { failed.Add(Path.GetFileName(entry)); }
                }
            }
        }
        catch (Exception ex) { return $"清理异常：{ex.Message}"; }

        string detail = removed == 0 && failed.Count == 0
            ? $"没有本程序创建的联接（保留 {kept} 个有效条目）"
            : $"已删除本程序创建的联接 {removed} 个"
              + (failed.Count > 0 ? $"，失败 {failed.Count} 个（{string.Join("、", failed.Take(6))}）" : "")
              + $"，保留 {kept} 个有效条目";
        return detail;
    }
}
