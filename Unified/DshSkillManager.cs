using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace DSHGuard.Unified;

/// <summary>
/// 技能文件的写侧。全部写入都落在技能文件自己的 frontmatter 上——
/// 这是 DSH filesystem skill provider 唯一认的启停开关：
///   · <c>disable-model-invocation: true</c> → 从模型技能目录中排除；
///   · <c>user-invocable: false</c>        → 从用户命令列表中排除。
///
/// 三条约束：
/// 1. 只改这两行，其余内容（正文、其他键、换行风格、BOM）逐字节保留；
/// 2. 只写 <see cref="DshSkill.Writable"/> 为真的文件（插件包内的技能一律拒绝）；
/// 3. 写入走 <see cref="AtomicFile.WriteAllText"/>，自动留 .bak；
/// 4. 写入前比对扫描时记下的 <see cref="DshSkill.Fingerprint"/>，文件被别人改过就拒绝写入。
/// </summary>
public static class DshSkillManager
{
    public const string KeyModelInvocation = "disable-model-invocation";
    public const string KeyUserInvocable = "user-invocable";

    /// <summary>模型可见性。可见时删掉该键（回到 DSH 缺省），不可见时写 true。</summary>
    public static string SetModelVisible(DshSkill skill, bool visible)
    {
        if (skill == null) return "内部错误：技能对象为空。";
        if (!skill.Writable || skill.IsPlugin)
            return "这个技能由插件包提供，不在本页管辖范围内。";

        string? error = WriteFrontmatterKey(skill.FullPath, KeyModelInvocation, visible ? null : "true", skill.Fingerprint);
        if (error.Length > 0) return error;

        return visible
            ? "已让模型可以调用「" + skill.DisplayName + "」。"
            : "已让模型看不到「" + skill.DisplayName + "」。";
    }

    /// <summary>用户命令可见性。</summary>
    public static string SetUserInvocable(DshSkill skill, bool invocable)
    {
        if (skill == null) return "内部错误：技能对象为空。";
        if (!skill.Writable || skill.IsPlugin)
            return "这个技能由插件包提供，不在本页管辖范围内。";

        string? error = WriteFrontmatterKey(skill.FullPath, KeyUserInvocable, invocable ? null : "false", skill.Fingerprint);
        if (error.Length > 0) return error;

        return invocable
            ? "已让「" + skill.DisplayName + "」能作为命令调用。"
            : "已把「" + skill.DisplayName + "」从命令列表隐藏。";
    }

    /// <summary>用系统默认程序打开技能文件；返回错误文案，成功返回空串。</summary>
    public static string OpenFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return "文件已不存在：" + path;
            var psi = new ProcessStartInfo(path) { UseShellExecute = true };
            Process.Start(psi);
            return "";
        }
        catch (Exception ex)
        {
            Logger.LogError("DshSkillManager.OpenFile", ex);
            return "打开失败：" + ex.Message;
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // frontmatter 行级改写
    // ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// 改写一个 frontmatter 键。value 为 null 表示删除该键。
    /// 返回空串表示成功，否则是给用户看的失败原因。
    ///
    /// <paramref name="expectedSha256"/> 非空时做一次「读了之后有没有人动过」的校验
    /// （见 <see cref="DshSkill.Fingerprint"/>）。技能页只改两行 frontmatter，
    /// 但落盘是整文件重写，所以并发改动能把对方刚写的东西整段盖掉，必须挡。
    /// </summary>
    public static string WriteFrontmatterKey(string path, string key, string? value,
        string? expectedSha256 = null)
    {
        try
        {
            if (!File.Exists(path)) return "文件已不存在：" + path;

            if (!string.IsNullOrEmpty(expectedSha256))
            {
                string nowHash = FileTrust.Sha256Hex(path);
                if (nowHash.Length == 0)
                    return "读不到这个文件了（可能刚被删或正被占用），请刷新后重试。";
                if (!nowHash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                    return "这个文件在你打开这一页之后被改过了（可能是 DSH 或另一个程序写的）。"
                         + "为了避免把别人刚写的内容盖掉，这次没有写入 —— 请点「刷新」看最新内容再改。";
            }

            byte[] raw = File.ReadAllBytes(path);
            bool bom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
            string text = new UTF8Encoding(false).GetString(raw, bom ? 3 : 0, raw.Length - (bom ? 3 : 0));

            string nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var lines = new List<string>(text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None));

            int first = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].Trim().Length == 0) continue;
                if (lines[i].Trim() == "---") { first = i; }
                break;
            }
            if (first < 0) return "这个技能文件没有 frontmatter，DSH 会直接跳过它，改启停没有意义。";

            int last = -1;
            for (int i = first + 1; i < lines.Count; i++)
            {
                if (lines[i].Trim() == "---") { last = i; break; }
            }
            if (last < 0) return "frontmatter 没有结束的 ---，文件已损坏，不敢改。";

            int hit = -1;
            for (int i = first + 1; i < last; i++)
            {
                string line = lines[i];
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                if (line.Substring(0, colon).Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    hit = i;
                    break;
                }
            }

            if (value == null)
            {
                if (hit < 0) return "";            // 本来就没有这个键，等于已是目标状态
                lines.RemoveAt(hit);
            }
            else if (hit < 0)
            {
                lines.Insert(last, key + ": " + value);
            }
            else
            {
                lines[hit] = key + ": " + value;
            }

            string output = string.Join(nl, lines);
            AtomicFile.WriteAllText(path, output, new UTF8Encoding(bom));
            return "";
        }
        catch (Exception ex)
        {
            Logger.LogError("DshSkillManager.WriteFrontmatterKey", ex);
            return "写入失败：" + ex.Message;
        }
    }
}
