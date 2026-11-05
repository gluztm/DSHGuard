using System;
using System.IO;
using System.Text;

namespace DSHGuard;

/// <summary>
/// 第 71 组断言：启动前体检的「取证文本」（2026-11-04，小蓝拍板的第三件）。
///
/// 为什么要有这组断言：
///   启动前体检失败时，界面只点名"哪个包没装上"，日志里也只有零散几行。
///   复盘真正要回答的是四件事：清单当初登记了什么、盘上现在还缺什么、
///   那条安装命令到底跑没跑、跑成了没有。这四样必须**一次落齐、落在同一行**，
///   否则日志里隔着几十行、对不上号，事后只能靠猜。
///
/// 这组断言守的是**口径**，不是文案：
///   · 清单读不出 ⇒ 结论只能是"判不了"，不许说"补齐了"（读不出不等于装好了）；
///   · 命令退出码 0 ≠ 补齐了 —— 磁盘复核仍缺就是没补齐（退出码只说明命令自称成功）；
///   · 命令没跑 ⇒ 命令状态写"未执行"，不许写成"退出码0=否"
///     （那会误导成"跑过但失败"，把没做过的动作记成做过）；
///   · 补装后没复核成 ⇒ 盘上状态判不了，绝不拿补装**之前**那份缺项当"现在还缺"。
///
/// 口径类断言最容易的坏法是"文案一改就假绿"：断言里写死某句中文，
/// 换个说法就红；或者反过来，把关键判定埋在注释里、代码其实没做。
/// 所以这里的做法是：
///   · 纯函数部分**直接调真函数**比对真实返回值（不写死整句文案，只钉关键判定片段）；
///   · 源码文本部分先把整行注释剥掉再找调用点（注释里写了也不算数），
///     并且断言"落盘点在 try/catch 之后、installed 分支之前"这个位置约束。
///
/// 口径既有的不许动：上面四条是这轮改动的立论基础，不是文案口味。
/// </summary>
public static partial class SelfTest
{
    /// <summary>第 71 组：启动前体检取证文本的纯函数口径 + 落盘调用点接线。</summary>
    internal static void RunPreflightEvidenceChecks(Action<string, bool, string> check)
    {
        // ── 纯函数口径：结论与依据必须跟着事实走 ──────────────────────────────

        // ① 清单读得出、补齐了（stillMissing 空）⇒ 结论必须是"补齐了"。
        string n71Filled = PluginManager.PreflightEvidenceText(
            new[] { "demo-plugin" }, Array.Empty<string>(), true, true, true, true, true, "");
        check("第 71 组 · 启动前体检取证：清单读得出且复核后不缺 ⇒ 结论是「补齐了」",
            n71Filled.Contains("结论=补齐了", StringComparison.Ordinal)
                && n71Filled.Contains("盘上已补齐", StringComparison.Ordinal)
                && n71Filled.Contains("安装命令退出码0=是", StringComparison.Ordinal),
            $"实测：{n71Filled}");

        // ② 命令没跑（缺 Git 闸门）⇒ 命令状态必须是"未执行"，且不许出现"退出码0=否"。
        //    "退出码0=否" 会把没做过的动作记成做过 —— 这是这组断言最想守住的一条。
        string n71NoRun = PluginManager.PreflightEvidenceText(
            new[] { "demo-plugin" }, new[] { "demo-plugin" }, true, true, true, false, false, "本机 PATH 里没有 git");
        check("第 71 组 · 启动前体检取证：命令没跑 ⇒ 写「未执行（退出码不适用）」，不写「退出码0=否」",
            n71NoRun.Contains("安装命令未执行（退出码不适用）", StringComparison.Ordinal)
                && !n71NoRun.Contains("退出码0=否", StringComparison.Ordinal)
                && n71NoRun.Contains("结论=未补齐（没跑安装命令）", StringComparison.Ordinal)
                && n71NoRun.Contains("未执行安装命令：本机 PATH 里没有 git", StringComparison.Ordinal),
            $"实测：{n71NoRun}");

        // ③ 命令跑了但退出码非 0 ⇒ 结论是"未补齐（安装命令未成功执行）"，依据带原始原因。
        string n71Fail = PluginManager.PreflightEvidenceText(
            new[] { "demo-plugin" }, new[] { "demo-plugin" }, true, true, true, true, false, "退出码非 0：network timeout");
        check("第 71 组 · 启动前体检取证：命令跑了但退出码非 0 ⇒ 结论是「未补齐（安装命令未成功执行）」并带原因",
            n71Fail.Contains("结论=未补齐（安装命令未成功执行）", StringComparison.Ordinal)
                && n71Fail.Contains("安装命令退出码0=否", StringComparison.Ordinal)
                && n71Fail.Contains("安装命令未成功执行：退出码非 0：network timeout", StringComparison.Ordinal),
            $"实测：{n71Fail}");

        // ④ 命令退出码 0，但磁盘复核仍缺 ⇒ 结论是「未补齐（命令退出码 0，但磁盘上仍缺）」，
        //    依据必须指向磁盘复核（退出码 0 不作数）。
        string n71Exit0StillMissing = PluginManager.PreflightEvidenceText(
            new[] { "demo-plugin" }, new[] { "demo-plugin" }, true, true, true, true, true, "");
        check("第 71 组 · 启动前体检取证：退出码 0 但盘上仍缺 ⇒ 结论是「未补齐（命令退出码 0，但磁盘上仍缺）」",
            n71Exit0StillMissing.Contains("结论=未补齐（命令退出码 0，但磁盘上仍缺）", StringComparison.Ordinal)
                && n71Exit0StillMissing.Contains("以磁盘复核为准", StringComparison.Ordinal)
                && n71Exit0StillMissing.Contains("盘上仍缺 demo-plugin", StringComparison.Ordinal),
            $"实测：{n71Exit0StillMissing}");

        // ⑤ 复核那次清单读不出 ⇒ 结论只能是"判不了"，盘上状态也判不了
        //    （清单与盘上出自同一次核对，读不出就都判不了）。
        string n71Unreadable = PluginManager.PreflightEvidenceText(
            new[] { "demo-plugin" }, Array.Empty<string>(), true, true, false, true, true, "");
        check("第 71 组 · 启动前体检取证：复核清单读不出 ⇒ 结论是「判不了」，盘上不许说「已补齐」",
            n71Unreadable.Contains("判不了", StringComparison.Ordinal)
                && n71Unreadable.Contains("盘上判不了（复核清单读不出）", StringComparison.Ordinal)
                && !n71Unreadable.Contains("盘上已补齐", StringComparison.Ordinal)
                && !n71Unreadable.Contains("结论=补齐了", StringComparison.Ordinal),
            $"实测：{n71Unreadable}");

        // ⑥ 补装后没复核成（抛异常）⇒ 盘上判不了，绝不拿补装**之前**的缺项当"现在还缺"。
        string n71NoRecheck = PluginManager.PreflightEvidenceText(
            new[] { "demo-plugin" }, new[] { "demo-plugin" }, true, false, true, true, true, "自动重新安装未能完成");
        check("第 71 组 · 启动前体检取证：补装后没复核成 ⇒ 盘上判不了，不拿补装前的缺项当现状",
            n71NoRecheck.Contains("盘上判不了（补装后没复核成）", StringComparison.Ordinal)
                && n71NoRecheck.Contains("判不了", StringComparison.Ordinal)
                && !n71NoRecheck.Contains("盘上仍缺 demo-plugin", StringComparison.Ordinal),
            $"实测：{n71NoRecheck}");

        // ⑦ 初次体检那次清单就读不出 ⇒ 清单状态必须写"清单判不了"。
        string n71FirstUnreadable = PluginManager.PreflightEvidenceText(
            Array.Empty<string>(), Array.Empty<string>(), false, true, true, false, false, "");
        check("第 71 组 · 启动前体检取证：初次清单读不出 ⇒ 清单状态是「清单判不了」",
            n71FirstUnreadable.Contains("清单判不了", StringComparison.Ordinal)
                && !n71FirstUnreadable.Contains("清单里登记着", StringComparison.Ordinal),
            $"实测：{n71FirstUnreadable}");
    }

    /// <summary>
    /// 第 71 组 · 源码文本侧：取证文本必须真的落盘，而且落盘点必须在正确位置。
    ///
    /// 为什么单独做源码文本断言（纯函数断言管不到的部分）：
    ///   纯函数再对，调用方忘调 / 调错位置，结论照样落不了盘。
    ///   落盘点尤其有讲究：必须放在 try/catch 之后、installed 分支之前 ——
    ///     · 放 try 之前 ⇒ 异常时四个事实还没定型（recheckRan/recheckReadable 是初值），
    ///       落下来的取证文本会把"没复核成"记成"已补齐"；
    ///     · 放 installed 分支之后 ⇒ 补齐成功那轮不留证（"补齐了"也是结论）。
    /// </summary>
    internal static void RunPreflightEvidenceSourceChecks(Action<string, bool, string> check, Action<string, string> skip)
    {
        string? n71ToolsPath = FindSourceFile("MainWindow.Tools.cs");
        if (n71ToolsPath == null)
        {
            skip("第 71 组 · 启动前体检取证：落盘点必须在 try/catch 之后、installed 分支之前",
                "单文件部署：读不到 MainWindow.Tools.cs 源码 ⇒ 本条未验证");
            return;
        }

        string[] n71Tools = File.ReadAllLines(n71ToolsPath);

        // 找 PreflightManifestAsync 的定义行
        int n71Def = -1;
        for (int i = 0; i < n71Tools.Length; i++)
        {
            if (n71Tools[i].Contains("private async Task<string> PreflightManifestAsync", StringComparison.Ordinal))
            {
                n71Def = i;
                break;
            }
        }
        if (n71Def < 0)
        {
            check("第 71 组 · 启动前体检取证：MainWindow.Tools.cs 里能找到 PreflightManifestAsync", false,
                "实测：没找到 PreflightManifestAsync 的定义行");
            return;
        }

        // 取方法体（按大括号深度，不靠"第一行 Trim()==\"}\""——那会在第一个内层大括号处就把方法体截断，
        //  截断后根本看不到落盘点，断言会假绿）。
        string n71Body = ExtractMethodBodyByBraceDepth(n71Tools, n71Def);

        // 剥掉整行注释后再找：注释里写了不算数（否则改个注释就假绿）。
        string n71BodyCode = StripWholeCommentLines(n71Body);
        string[] n71Lines = n71BodyCode.Split('\n');

        // ① 取证调用点必须在（方法体里出现一次）
        int n71Call = -1;
        for (int i = 0; i < n71Lines.Length; i++)
        {
            if (n71Lines[i].Contains("PreflightEvidenceText", StringComparison.Ordinal)) { n71Call = i; break; }
        }
        check("第 71 组 · 启动前体检取证：PreflightManifestAsync 里调用了 PreflightEvidenceText",
            n71Call >= 0,
            $"实测：方法体={n71Lines.Length} 行，取证调用点行号={n71Call}");

        // ② 取证调用点必须交给 Logger.NoteDiagnosis 落盘（Logger.Log 是空实现，走它等于没记）
        check("第 71 组 · 启动前体检取证：取证文本交给 Logger.NoteDiagnosis 落盘（不是空实现的 Logger.Log）",
            n71BodyCode.Contains("Logger.NoteDiagnosis(PluginManager.PreflightEvidenceText", StringComparison.Ordinal),
            $"实测：方法体里同时出现 NoteDiagnosis={n71BodyCode.Contains("Logger.NoteDiagnosis", StringComparison.Ordinal)}"
            + $" / Logger.Log={n71BodyCode.Contains("Logger.Log(", StringComparison.Ordinal)}");

        // ③ 位置约束：落盘点在 try/catch 之后、installed 分支之前。
        //    找 try / catch / 调用点 / installed 分支的行号（都在剥注释后的正文里）。
        int n71Try = -1;
        int n71Catch = -1;
        int n71Installed = -1;
        for (int i = 0; i < n71Lines.Length; i++)
        {
            string t = n71Lines[i].Trim();
            if (n71Try < 0 && t.StartsWith("try", StringComparison.Ordinal)) n71Try = i;
            else if (n71Catch < 0 && t.StartsWith("catch", StringComparison.Ordinal)) n71Catch = i;
            else if (n71Installed < 0 && t.StartsWith("if (installed)", StringComparison.Ordinal)) n71Installed = i;
        }
        check("第 71 组 · 启动前体检取证：落盘点在 try/catch 之后、installed 分支之前（异常时事实才定型；补齐那轮也留证）",
            n71Call >= 0 && n71Try >= 0 && n71Catch >= 0 && n71Installed >= 0
                && n71Call > n71Catch && n71Call < n71Installed,
            $"实测：try@{n71Try} catch@{n71Catch} 取证调用点@{n71Call} if(installed)@{n71Installed}（方法体内行号）");

        // ④ 四个事实变量必须在调用点之前被赋值（否则落的是初值）
        check("第 71 组 · 启动前体检取证：四个事实变量在调用点之前都有赋值",
            n71Call >= 0
                && n71BodyCode.Contains("commandRan = true;", StringComparison.Ordinal)
                && n71BodyCode.Contains("commandOk = okInstall;", StringComparison.Ordinal)
                && n71BodyCode.Contains("recheckRan = true;", StringComparison.Ordinal)
                && n71BodyCode.Contains("recheckReadable = after.Readable;", StringComparison.Ordinal),
            $"实测：commandRan={n71BodyCode.Contains("commandRan = true;", StringComparison.Ordinal)}"
            + $" commandOk={n71BodyCode.Contains("commandOk = okInstall;", StringComparison.Ordinal)}"
            + $" recheckRan={n71BodyCode.Contains("recheckRan = true;", StringComparison.Ordinal)}"
            + $" recheckReadable={n71BodyCode.Contains("recheckReadable = after.Readable;", StringComparison.Ordinal)}");

        // ⑤ 落盘点之前不能有任何"提前 return"把取证绕过去：
        //    方法开头那个"清单没问题 ⇒ 直接 return"是合法的（根本没缺包、没体检可取证），
        //    但 try 之后的 return（installed 成功那轮）必须排在取证调用点之后。
        int n71ReturnAfterTry = -1;
        for (int i = n71Try >= 0 ? n71Try : 0; i < n71Lines.Length; i++)
        {
            if (n71Lines[i].Trim().StartsWith("return", StringComparison.Ordinal)) { n71ReturnAfterTry = i; break; }
        }
        check("第 71 组 · 启动前体检取证：try 之后没有提前 return 绕过取证调用点",
            n71Call >= 0 && (n71ReturnAfterTry < 0 || n71ReturnAfterTry > n71Call),
            $"实测：try 之后第一个 return@{n71ReturnAfterTry}，取证调用点@{n71Call}（方法体内行号）");
    }

    /// <summary>
    /// 按大括号深度取方法体（从定义行起，到该方法自己的收尾大括号止）。
    ///
    /// 为什么不用"第一行 Trim()==\"}\""那种写法：那会在**第一个内层**大括号处就把方法体截断
    /// （比如 if 块、foreach 块的收尾），截断后根本看不到后面的代码 —— 断言会"因为找不到而假绿"，
    /// 或者更糟：被截断的片段刚好没有违规点，于是门禁形同虚设。
    ///
    /// 扫描时跳过字符串字面量与行注释：$"…{name}…" 这种插值字符串里的花括号不是代码块，
    /// 按代码算会把深度算歪。
    /// </summary>
    private static string ExtractMethodBodyByBraceDepth(string[] lines, int defLine)
    {
        var sb = new StringBuilder();
        int depth = 0;
        bool started = false;
        for (int i = defLine; i < lines.Length; i++)
        {
            string line = lines[i];
            sb.Append(line);
            sb.Append('\n');

            bool inStr = false;
            char quote = '\0';
            for (int c = 0; c < line.Length; c++)
            {
                char ch = line[c];
                if (inStr)
                {
                    if (ch == '\\') { c++; continue; }
                    if (ch == quote) inStr = false;
                    continue;
                }
                if (ch == '"' || ch == '\'') { inStr = true; quote = ch; continue; }
                if (ch == '/' && c + 1 < line.Length && line[c + 1] == '/') break;   // 行注释
                if (ch == '{') { depth++; started = true; }
                else if (ch == '}') depth--;
            }

            // 方法自己的收尾大括号：深度从 1 回到 0 就算结束。
            // ⚠ 不能等 depth < 0：方法的收尾 "}" 只把深度降到 0，判 < 0 会一路扫到文件末尾
            //    （实测把 4589 行全吞进"方法体"，断言的位置比较全成废数）。
            if (started && depth <= 0) break;   // 方法自己的收尾大括号
        }
        return sb.ToString();
    }
}
