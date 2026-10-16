using System;
using System.IO;
using System.Text;

namespace DSHGuard;

/// <summary>
/// 原子写文件（2026-10-16 审计 M）：先写同目录的临时文件并刷盘，再一次性替换目标。
/// <para>
/// 直接 <c>File.WriteAllText</c> 在崩溃 / 断电 / 磁盘满时会留下截断的文件；对 settings.json、
/// versions.json 这类状态文件，下次启动读不出来就会回落默认值（版本记忆回落成 latest、
/// 用户路径被默认值覆盖）。替换要么完整成功、要么原文件原封不动。
/// </para>
/// <para>
/// 同时保留一份 <c>.bak</c>（替换前的上一版），供读取失败时回退（见 <see cref="ReadAllTextWithBackup"/>）。
/// </para>
/// </summary>
internal static class AtomicFile
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    internal static void WriteAllText(string path, string content, Encoding? encoding = null, bool keepBackup = true)
    {
        string full = Path.GetFullPath(path);
        string dir = Path.GetDirectoryName(full) ?? ".";
        Directory.CreateDirectory(dir);
        string tmp = Path.Combine(dir, "." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var w = new StreamWriter(fs, encoding ?? Utf8NoBom))
            {
                w.Write(content);
                w.Flush();
                fs.Flush(flushToDisk: true);
            }
            if (File.Exists(full))
            {
                string? bak = keepBackup ? full + ".bak" : null;
                try { File.Replace(tmp, full, bak, ignoreMetadataErrors: true); }
                catch (PlatformNotSupportedException) { File.Move(tmp, full, overwrite: true); }
                catch (IOException)
                {
                    // File.Replace 在部分文件系统（网络盘 / FAT）上不可用：退回覆盖式移动（仍然是整份替换）
                    if (bak != null) { try { File.Copy(full, bak, overwrite: true); } catch { } }
                    File.Move(tmp, full, overwrite: true);
                }
            }
            else
            {
                File.Move(tmp, full);
            }
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    /// <summary>原子写字节（不留 .bak；用于回滚撤回这类"写回原样"的场景）。</summary>
    internal static void WriteAllBytes(string path, byte[] data)
    {
        string full = Path.GetFullPath(path);
        string dir = Path.GetDirectoryName(full) ?? ".";
        string tmp = Path.Combine(dir, "." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(data, 0, data.Length);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, full, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    /// <summary>
    /// 读文件；主文件不存在时抛 <see cref="FileNotFoundException"/>（调用方据此区分"首次运行"）。
    /// 主文件存在但 <paramref name="isValid"/> 判它无效（截断 / 损坏）且 <c>.bak</c> 有效时，返回 <c>.bak</c> 的内容，
    /// 并通过 <paramref name="usedBackup"/> 告知调用方。
    /// </summary>
    internal static string ReadAllTextWithBackup(string path, Func<string, bool> isValid, out bool usedBackup)
    {
        usedBackup = false;
        string text = File.ReadAllText(path);
        if (isValid(text)) return text;
        string bak = path + ".bak";
        try
        {
            if (File.Exists(bak))
            {
                string b = File.ReadAllText(bak);
                if (isValid(b)) { usedBackup = true; return b; }
            }
        }
        catch { }
        return text;
    }
}
