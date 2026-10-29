using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DSHGuard.Unified;

/// <summary>
/// 提示词单文件的读写（<c>~/.dsh/prompt-inject.md</c> 与 <c>~/.dsh/MEMORY.md</c>）。
///
/// DSH 把这两个文件当纯文本整份注入，所以本类只做「读全文 / 写全文」两件事：
/// 不做任何解析、不改结构、不裁剪行尾。写入走 <see cref="AtomicFile.WriteAllText"/>，
/// 每次保存都会在磁盘上留下上一版 <c>.bak</c>。
/// </summary>
public static class DshPromptManager
{
    /// <summary>可编辑的提示词文件清单（ID → 路径 + 说明）。</summary>
    public static List<(string Id, string Name, string Path, string Hint)> Known()
    {
        return new List<(string, string, string, string)>
        {
            ("prompt-inject", "提示词注入",
                Path.Combine(GuardPaths.DshHome, "prompt-inject.md"),
                "注入到每个会话开头的系统提示词。修改会影响所有会话，保存前请确认内容。"),
            ("memory", "长期记忆",
                Path.Combine(GuardPaths.DshHome, "MEMORY.md"),
                "跨会话的长期记忆文件，DSH 自己也会往里写；手工编辑后建议先整份备份。"),
        };
    }

    /// <summary>整读文件内容；不存在返回空串（不是错误，新建即用）。</summary>
    public static string ReadAll(string path)
    {
        try
        {
            if (!File.Exists(path)) return "";
            byte[] raw = File.ReadAllBytes(path);
            bool bom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
            return new UTF8Encoding(false).GetString(raw, bom ? 3 : 0, raw.Length - (bom ? 3 : 0));
        }
        catch (Exception ex)
        {
            Logger.LogError("DshPromptManager.ReadAll", ex);
            return "";
        }
    }

    /// <summary>
    /// 整份覆盖写入。返回空串表示成功，否则是给用户看的失败原因。
    /// <paramref name="originalBom"/> 由调用方从磁盘读到，保证不破坏原有编码习惯。
    /// </summary>
    public static string Save(string path, string content, bool originalBom)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) return "内部错误：目标路径为空。";

            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            AtomicFile.WriteAllText(path, content ?? "", new UTF8Encoding(originalBom));
            return "";
        }
        catch (Exception ex)
        {
            Logger.LogError("DshPromptManager.Save", ex);
            return "写入失败：" + ex.Message;
        }
    }

    /// <summary>磁盘上的文件是否带 BOM（编辑器保存时原样带回）。</summary>
    public static bool HasBom(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            int b0 = fs.ReadByte(), b1 = fs.ReadByte(), b2 = fs.ReadByte();
            return b0 == 0xEF && b1 == 0xBB && b2 == 0xBF;
        }
        catch { return false; }
    }
}
