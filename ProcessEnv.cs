using System;
using System.IO;

namespace DSHGuard;

/// <summary>
/// 进程环境自净：把 <c>TEMP/TMP</c> 归一到用户真实可写的临时目录。
///
/// 为什么要做：安装器最后一步勾选「立即启动」时，守护壳继承的是**安装器自己的环境**，
/// 而安装器的临时目录在它退出后会被清掉；引擎（npx/node）第一次启动正好落在那之后，
/// 拿不到可写的临时目录就容易在解析/解包阶段炸掉——现象就是"装完第一次点启动必失败，
/// 关掉程序重开就正常"。
/// 归一化本身是无害的加固：哪怕将来发现真因在别处，临时目录指向"用户真实临时目录"也永远是对的。
/// </summary>
internal static class ProcessEnv
{
    /// <summary>用户真实临时目录（不读 TEMP，避免读到已被改坏的值）。</summary>
    public static string UserTempDir
    {
        get
        {
            try
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrWhiteSpace(local)) return Path.Combine(local, "Temp");
            }
            catch { }
            return Path.Combine(Path.GetTempPath());
        }
    }

    /// <summary>
    /// 这个临时目录能不能用：非空、存在、不是安装器留下的临时目录（<c>is-*.tmp</c>）。
    /// 纯函数，便于自检。
    /// </summary>
    public static bool IsUsableTemp(string? dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir)) return false;
            string d = dir!.Trim().Trim('"');
            if (d.Length == 0 || !Directory.Exists(d)) return false;

            // Inno 安装器的临时目录形如 %TEMP%\is-XXXXXX.tmp 或 %LOCALAPPDATA%\Temp\is-XXXXXX.tmp
            string name = Path.GetFileName(d.TrimEnd('\\', '/'));
            if (name.StartsWith("is-", StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 归一化本进程的 TEMP/TMP（子进程默认继承）。返回一行诊断文本，交调用方写进日志。
    /// </summary>
    public static string EnsureUsableTemp()
    {
        string before = Environment.GetEnvironmentVariable("TEMP") ?? "";
        try
        {
            string target = UserTempDir;
            if (!Directory.Exists(target)) Directory.CreateDirectory(target);

            // 能用就不动它：只把"不可用"的情况（空/不存在/安装器临时目录）纠正过来。
            // 短名与长名只是写法不同，不算问题——否则每次启动都会刷一条假的"已归一"日志。
            if (IsUsableTemp(before)) return $"[环境] TEMP 正常：{before}";

            Environment.SetEnvironmentVariable("TEMP", target);
            Environment.SetEnvironmentVariable("TMP", target);
            return $"[环境] TEMP 不可用（{before.Replace("\n", " ")}）→ 已改为 {target}";
        }
        catch (Exception ex)
        {
            return $"[环境] TEMP 归一失败（{ex.Message}），保持原值：{before}";
        }
    }
}
