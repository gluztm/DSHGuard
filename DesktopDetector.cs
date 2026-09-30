using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace DSHGuard;

/// <summary>
/// 官方 DSH 桌面版（Electron）安装位置探测。
///
/// 为什么不能"猜目录名就完事"：本机实测存在一个陈旧快捷方式
/// （开始菜单里的 <c>Deepseek Harness EAC.lnk</c>，TargetPath 已失效），
/// 说明"名字像"与"真的装了"是两件事。因此**每一个候选都必须过
/// <see cref="LooksInstalled"/>**（目录下真有 <c>DeepSeek Harness.exe</c>）才算命中，
/// 宁可返回空串（= 未安装）也不返回一个存在但装不了东西的目录。
///
/// 探测顺序：
///   ① 卸载登记表 —— 官方安装器把真实安装位置写在这里，最可靠。
///      实测本机在 <c>HKCU</c> 下（不在 HKLM），故三个位置都查：
///      HKCU、HKLM、HKLM\WOW6432Node（32 位安装器会落到最后一个）。
///   ② 固定候选目录 —— 覆盖"安装器没登记 / 用户手工解压"的情况。
///
/// 信任边界：本文件是全仓**唯一**读注册表卸载键的地方（<see cref="RegistryHelper"/>
/// 只负责自启动项）。不引入 COM / <c>IShellLink</c> 去解析快捷方式 ——
/// 注册表已经给出了安装位置，为同样的信息打破"零 COM"的既有边界不划算。
/// </summary>
internal static class DesktopDetector
{
    private const string UninstallKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string Wow6432UninstallKeyPath = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>卸载登记表里 DisplayName 需要包含的片段（实测 <c>"DeepSeek Harness 0.2.0-rc.1"</c>）。</summary>
    private const string NameNeedle = "DeepSeek Harness";

    /// <summary>
    /// 固定候选目录。刻意**不写死本机的 D:\Applications\…** —— 那台机器的路径已经由注册表
    /// 条目覆盖，把个人机器的盘符写进产品代码，换台机器就是纯粹的死代码。
    /// </summary>
    internal static IEnumerable<string> DefaultCandidates()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (local.Length > 0)
        {
            yield return Path.Combine(local, "Programs", "DeepSeek Harness");
            yield return Path.Combine(local, "Programs", "dsh-desktop");
            yield return Path.Combine(local, "Programs", "dsh");
            yield return Path.Combine(local, "dsh-desktop");
        }

        foreach (var pf in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                 })
        {
            if (pf.Length > 0) yield return Path.Combine(pf, "DeepSeek Harness");
        }
    }

    /// <summary>
    /// 探测桌面版安装目录。返回空串 = **未探测到**（不是"出错"）。
    ///
    /// <paramref name="candidates"/> 与 <paramref name="useRegistry"/> 都是为自检留的注入点：
    /// 自检必须能构造"注册表里有条目但目录已删"这类情形，且**不能**依赖跑测试那台机器的真实注册表
    /// —— 否则本机装了桌面版时，"探测不到"的用例永远无法成立。
    /// </summary>
    internal static string DetectInstallDir(IEnumerable<string>? candidates = null, bool useRegistry = true)
    {
        try
        {
            if (useRegistry)
            {
                string fromReg = FromRegistry();
                if (LooksInstalled(fromReg)) return FullPath(fromReg);
            }

            foreach (string c in candidates ?? DefaultCandidates())
                if (LooksInstalled(c)) return FullPath(c);
        }
        catch (Exception ex) { Logger.LogError("DesktopDetector.DetectInstallDir", ex); }
        return "";
    }

    /// <summary>
    /// 这个目录是不是一个**装好了**的桌面版（目录下有主程序）。
    /// 判据只有这一处：只看目录是否存在会把空目录、旧备份目录也算成"装了"。
    /// </summary>
    internal static bool LooksInstalled(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return false;
        try
        {
            string d = dir!.Trim().Trim('"');
            return d.Length > 0 && File.Exists(Path.Combine(d, GuardPaths.DesktopExeName));
        }
        catch { return false; }
    }

    /// <summary>
    /// 读桌面版版本号（如 <c>0.2.0-rc.1</c>）。读不到返回空串。
    /// 注意**不能**用 <see cref="VersionInfo.GetCurrentVersion"/> —— 那个读的是 npx 缓存里的
    /// web 引擎版本，与桌面版完全无关。
    /// </summary>
    internal static string ReadVersion(string? installDir)
    {
        try
        {
            if (!LooksInstalled(installDir)) return "";
            var info = FileVersionInfo.GetVersionInfo(
                Path.Combine(installDir!.Trim().Trim('"'), GuardPaths.DesktopExeName));
            return (info.FileVersion ?? "").Trim();
        }
        catch (Exception ex) { Logger.LogError("DesktopDetector.ReadVersion", ex); return ""; }
    }

    private static string FromRegistry()
    {
        foreach (var (hive, sub) in new[]
                 {
                     (Registry.CurrentUser, UninstallKeyPath),
                     (Registry.LocalMachine, UninstallKeyPath),
                     (Registry.LocalMachine, Wow6432UninstallKeyPath)
                 })
        {
            string hit = ScanUninstall(hive, sub);
            if (hit.Length > 0) return hit;
        }
        return "";
    }

    private static string ScanUninstall(RegistryKey hive, string sub)
    {
        try
        {
            using var root = hive.OpenSubKey(sub);
            if (root == null) return "";

            foreach (string name in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(name);
                if (k == null) continue;

                string display = k.GetValue("DisplayName") as string ?? "";
                if (display.IndexOf(NameNeedle, StringComparison.OrdinalIgnoreCase) < 0) continue;

                string loc = k.GetValue("InstallLocation") as string ?? "";
                if (LooksInstalled(loc)) return loc;

                // 兜底：部分安装器只登记 DisplayIcon（形如 "<dir>\DeepSeek Harness.exe,0"），
                // 从它反推目录。反推结果同样必须过 LooksInstalled，不做"看起来对就用"。
                string fromIcon = DirFromDisplayIcon(k.GetValue("DisplayIcon") as string);
                if (LooksInstalled(fromIcon)) return fromIcon;
            }
        }
        catch { }
        return "";
    }

    /// <summary>
    /// 从 <c>DisplayIcon</c> 取值反推所在目录：形如 <c>"C:\…\DeepSeek Harness.exe,0"</c>
    /// （末尾的 <c>,0</c> 是图标索引）。去引号、去索引、取目录名。
    /// </summary>
    internal static string DirFromDisplayIcon(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon)) return "";
        try
        {
            string s = icon!.Trim().Trim('"');
            int comma = s.LastIndexOf(',');
            if (comma > 0) s = s.Substring(0, comma);
            s = s.Trim().Trim('"');
            return Path.GetDirectoryName(s) ?? "";
        }
        catch { return ""; }
    }

    private static string FullPath(string dir)
    {
        try { return Path.GetFullPath(dir.Trim().Trim('"')); } catch { return dir; }
    }
}
