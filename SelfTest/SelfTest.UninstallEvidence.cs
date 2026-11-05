using System;
using System.IO;

namespace DSHGuard;

/// <summary>
/// 第 72 组：卸载结论的取证文本（目录 · 清单 · 退出码 · 结论 一次落齐）与它的落盘接线。
///
/// 背景（2026-11-05）：一次卸载到底成没成，复盘要能回答四件事 ——
///   哪个包、包目录还在不在、插件清单里还登记着吗、那条卸载命令跑成了没有。
/// 这四样以前散在几处各写各的，批量那条还手拼过一遍句子，于是出了三件事：
///   · 手拼的那份与单个那份口径不一致（同一个包，两种说法）；
///   · 取证文本写进了 Logger.Log —— 那是空实现，其实什么都没落盘；
///   · "目录没了但清单里还登记着"这种半卸载被写成了"卸干净了"，下次安装会照清单装回来。
///
/// 本节钉死两件事：
///   · 口径层：直接调真函数 UninstallEvidenceText，按四事实断结论；
///     并钉住"只有 Clean 才算卸干净"这条总闸（就算调用点误传 removed=true，也会被压回 false）；
///   · 结构层：两个调用点（批量 / 单个）都必须把取证文本交给会落盘的 Logger.NoteDiagnosis，
///     而不是空实现的 Logger.Log；且落盘点必须在判定之后（事实先定型，再落证）。
///
/// 断言口径（与第 71 组同型）：
///   · 纯函数部分直接调真函数、比对真实返回值，只钉关键判定片段，不写死整句文案；
///   · 源码部分先把整行注释剥掉再找调用点（注释里写了不算数），并断言顺序约束。
/// </summary>
public static partial class SelfTest
{
    /// <summary>第 72 组 · 口径层：卸载取证文本的四样与四态结论。</summary>
    internal static void RunUninstallEvidenceChecks(Action<string, bool, string> check)
    {
        // ① 卸干净：四样齐全，并且带上"依据"。
        var n72Clean = new PluginManager.UninstallResult(
            cmdOk: true, measured: true, removed: true, note: "包目录已消失",
            outcome: PluginManager.UninstallOutcome.Clean, manifestStillDeclared: false);
        string n72CleanText = PluginManager.UninstallEvidenceText("demo-plugin", n72Clean);
        check("第 72 组 · 卸载取证文本：卸干净时四样齐全（目录 · 清单 · 退出码 · 结论），并带上依据",
            n72CleanText.Contains("包目录已消失", StringComparison.Ordinal)
            && n72CleanText.Contains("清单里已移除", StringComparison.Ordinal)
            && n72CleanText.Contains("命令退出码0=True", StringComparison.Ordinal)
            && n72CleanText.Contains("结论=真卸干净", StringComparison.Ordinal)
            && n72CleanText.Contains("。依据：", StringComparison.Ordinal),
            $"实测：{n72CleanText}");

        // ② 半卸载：目录没了、清单还登记着 —— 必须写成半卸载，绝不能写成真卸干净。
        var n72Half = new PluginManager.UninstallResult(
            cmdOk: true, measured: true, removed: false, note: "清单里还登记着",
            outcome: PluginManager.UninstallOutcome.HalfDone, manifestStillDeclared: true);
        string n72HalfText = PluginManager.UninstallEvidenceText("demo-plugin", n72Half);
        check("第 72 组 · 卸载取证文本：半卸载必须写明「目录没了、清单还在」，且不得写成真卸干净",
            n72HalfText.Contains("包目录已消失", StringComparison.Ordinal)
            && n72HalfText.Contains("清单里仍登记着", StringComparison.Ordinal)
            && n72HalfText.Contains("结论=**半卸载：目录没了、清单还在**（下次安装会装回来）", StringComparison.Ordinal)
            && !n72HalfText.Contains("真卸干净", StringComparison.Ordinal),
            $"实测：{n72HalfText}");

        // ③ 判不了：清单读不出来 —— "判不了"与"没登记"必须分开（把读不出当干净就是又一次把失败报成成功）。
        var n72Undet = new PluginManager.UninstallResult(
            cmdOk: true, measured: true, removed: false, note: "清单读不出来",
            outcome: PluginManager.UninstallOutcome.Undetermined, manifestStillDeclared: null);
        string n72UndetText = PluginManager.UninstallEvidenceText("demo-plugin", n72Undet);
        check("第 72 组 · 卸载取证文本：清单判不了要说「判不了」，不许冒充干净",
            n72UndetText.Contains("清单判不了", StringComparison.Ordinal)
            && n72UndetText.Contains("结论=判不了（清单读不出，不敢算卸干净）", StringComparison.Ordinal)
            && !n72UndetText.Contains("真卸干净", StringComparison.Ordinal)
            && !n72UndetText.Contains("清单里已移除", StringComparison.Ordinal),
            $"实测：{n72UndetText}");

        // ④ 没卸掉：目录还在 —— 目录状态与退出码都要如实写出来。
        var n72Not = new PluginManager.UninstallResult(
            cmdOk: false, measured: true, removed: false, note: "目录还在",
            outcome: PluginManager.UninstallOutcome.NotRemoved, manifestStillDeclared: true);
        string n72NotText = PluginManager.UninstallEvidenceText("demo-plugin", n72Not);
        check("第 72 组 · 卸载取证文本：没卸掉时目录状态与退出码都要如实写",
            n72NotText.Contains("包目录仍在", StringComparison.Ordinal)
            && n72NotText.Contains("命令退出码0=False", StringComparison.Ordinal)
            && n72NotText.Contains("结论=没卸掉", StringComparison.Ordinal),
            $"实测：{n72NotText}");

        // ⑤ 磁盘判不了：只能说判不了，不许猜目录状态。
        var n72NoMeasure = new PluginManager.UninstallResult(
            cmdOk: false, measured: false, removed: false, note: "核对不了磁盘状态");
        string n72NoMeasureText = PluginManager.UninstallEvidenceText("demo-plugin", n72NoMeasure);
        check("第 72 组 · 卸载取证文本：磁盘判不了时写「判不了」，不许猜目录状态",
            n72NoMeasureText.Contains("包目录判不了", StringComparison.Ordinal)
            && !n72NoMeasureText.Contains("包目录仍在", StringComparison.Ordinal)
            && !n72NoMeasureText.Contains("包目录已消失", StringComparison.Ordinal),
            $"实测：{n72NoMeasureText}");

        // ⑥ 操作前就没有 —— 中性结论「无需卸载」，既不报成功也不报失败。
        var n72Unnec = new PluginManager.UninstallResult(
            cmdOk: false, measured: true, removed: false, note: "无需卸载：操作前本机就没有这个包",
            alreadyAbsent: true, outcome: PluginManager.UninstallOutcome.NotRemoved, manifestStillDeclared: null);
        string n72UnnecText = PluginManager.UninstallEvidenceText("demo-plugin", n72Unnec);
        check("第 72 组 · 卸载取证文本：操作前就没有 ⇒ 中性结论「无需卸载」，绝不许报成功",
            n72UnnecText.Contains("结论=无需卸载（操作前本机就没有）", StringComparison.Ordinal)
            && !n72UnnecText.Contains("真卸干净", StringComparison.Ordinal)
            && !n72UnnecText.Contains("已卸载", StringComparison.Ordinal),
            $"实测：{n72UnnecText}");

        // ⑦ 用户主动停止：写进同一行证据里；不传就不该出现。
        string n72StoppedText = PluginManager.UninstallEvidenceText("demo-plugin", n72Clean, true);
        check("第 72 组 · 卸载取证文本：用户主动停止要落进同一行证据里（不传就不写）",
            n72StoppedText.Contains("用户主动停止", StringComparison.Ordinal)
            && !n72CleanText.Contains("用户主动停止", StringComparison.Ordinal),
            $"实测：{n72StoppedText}");

        // ⑧ 口径同源：单个与批量只差前缀，其余逐字相同。
        string n72BatchLabelText = PluginManager.UninstallEvidenceText("demo-plugin", n72Half, false, "批量卸载判定");
        string n72SingleTail = n72HalfText.Substring("卸载判定".Length);
        check("第 72 组 · 卸载取证文本：单个与批量只差前缀（口径四样逐字同源）",
            n72BatchLabelText == "批量卸载判定" + n72SingleTail
            && n72BatchLabelText.StartsWith("批量卸载判定「demo-plugin」", StringComparison.Ordinal)
            && n72HalfText.StartsWith("卸载判定「demo-plugin」", StringComparison.Ordinal),
            $"实测：单个={n72HalfText} ／ 批量={n72BatchLabelText}");

        // ⑨ 防谎报总闸：调用点误传 removed=true、outcome 不是 Clean ⇒ 必须被压回 false。
        var n72Liar = new PluginManager.UninstallResult(
            cmdOk: true, measured: true, removed: true, note: "调用点误传 removed=true",
            outcome: PluginManager.UninstallOutcome.HalfDone, manifestStillDeclared: true);
        string n72LiarText = PluginManager.UninstallEvidenceText("demo-plugin", n72Liar);
        check("第 72 组 · 卸载取证文本：防谎报总闸（outcome 不是 Clean ⇒ 调用点说卸掉了也不许报成功）",
            !n72Liar.Removed && n72Liar.HalfDone
            && n72LiarText.Contains("结论=**半卸载", StringComparison.Ordinal)
            && !n72LiarText.Contains("真卸干净", StringComparison.Ordinal),
            $"实测：Removed={n72Liar.Removed} HalfDone={n72Liar.HalfDone} 文本={n72LiarText}");

        // ⑩ 判据本体（纯函数）：目录与清单两事实合成四态结论。
        string n72Classify =
            $"目录没了+清单还登记={PluginManager.ClassifyUninstall(true, true)}"
          + $" ／ 目录没了+清单已移除={PluginManager.ClassifyUninstall(true, false)}"
          + $" ／ 目录没了+清单判不了={PluginManager.ClassifyUninstall(true, null)}"
          + $" ／ 目录还在={PluginManager.ClassifyUninstall(false, null)}"
          + $" ／ 目录还在+清单还登记={PluginManager.ClassifyUninstall(false, true)}";
        check("第 72 组 · 卸载判据（纯函数）：目录与清单两事实合成四态结论",
            PluginManager.ClassifyUninstall(true, true) == PluginManager.UninstallOutcome.HalfDone
            && PluginManager.ClassifyUninstall(true, false) == PluginManager.UninstallOutcome.Clean
            && PluginManager.ClassifyUninstall(true, null) == PluginManager.UninstallOutcome.Undetermined
            && PluginManager.ClassifyUninstall(false, null) == PluginManager.UninstallOutcome.NotRemoved
            && PluginManager.ClassifyUninstall(false, true) == PluginManager.UninstallOutcome.NotRemoved,
            $"实测：{n72Classify}");

        // ⑪ 半卸载的下一步：说清"还会被装回来"，且不夹带命令行与网址。
        string n72Advice = PluginManager.HalfUninstallAdvice("demo-plugin");
        check("第 72 组 · 半卸载的下一步：必须说清下次还会被装回来，且不夹带命令行与网址",
            n72Advice.Contains("装回来", StringComparison.Ordinal)
            && !n72Advice.Contains("http", StringComparison.Ordinal)
            && !n72Advice.Contains("npm ", StringComparison.Ordinal),
            $"实测：{n72Advice}");
    }

    /// <summary>第 72 组 · 结构层：两个调用点的取证文本必须真的落盘（NoteDiagnosis，不是空实现的 Logger.Log）。</summary>
    internal static void RunUninstallEvidenceSourceChecks(Action<string, bool, string> check, Action<string, string> skip)
    {
        string? n72BatchPath = FindSourceFile("MainWindow.Batch.cs");
        string? n72ToolsPath = FindSourceFile("MainWindow.Tools.cs");
        if (n72BatchPath == null || n72ToolsPath == null)
        {
            skip("第 72 组 · 卸载取证文本的落盘接线",
                "单文件部署：读不到 MainWindow.Batch.cs / MainWindow.Tools.cs 源码 ⇒ 本条未验证");
            return;
        }

        // ── 批量那条：BatchUninstallVerdict ──
        string[] n72BatchLines = File.ReadAllLines(n72BatchPath);
        int n72BatchDef = -1;
        for (int i = 0; i < n72BatchLines.Length; i++)
        {
            if (n72BatchLines[i].Contains("private static PluginManager.UninstallResult BatchUninstallVerdict(", StringComparison.Ordinal))
            {
                n72BatchDef = i;
                break;
            }
        }
        if (n72BatchDef < 0)
        {
            check("第 72 组 · 批量卸载的落盘接线：取证文本必须交给会落盘的 NoteDiagnosis（不是空实现的 Logger.Log）",
                false, "实测：没找到 BatchUninstallVerdict 的定义行");
            return;
        }
        string[] n72BatchBody = StripWholeCommentLines(ExtractMethodBodyByBraceDepth(n72BatchLines, n72BatchDef)).Split('\n');
        int n72BatchCall = FindUninstallEvidenceCall(n72BatchBody);
        string n72BatchCallLine = n72BatchCall >= 0 ? n72BatchBody[n72BatchCall].Trim() : "(没找到调用点)";
        check("第 72 组 · 批量卸载的落盘接线：取证文本必须交给会落盘的 NoteDiagnosis（不是空实现的 Logger.Log）",
            n72BatchCall >= 0
            && n72BatchBody[n72BatchCall].Contains("Logger.NoteDiagnosis(", StringComparison.Ordinal)
            && !n72BatchBody[n72BatchCall].Contains("Logger.Log(", StringComparison.Ordinal)
            && n72BatchBody[n72BatchCall].Contains("批量卸载判定", StringComparison.Ordinal),
            $"实测：方法体第 {n72BatchCall + 1} 行 = {n72BatchCallLine}");

        int n72BatchEval = -1;
        int n72BatchReturnBefore = -1;
        for (int i = 0; i < n72BatchBody.Length; i++)
        {
            if (n72BatchBody[i].Contains("PluginManager.EvaluateUninstall(", StringComparison.Ordinal)) n72BatchEval = i;
            if (n72BatchReturnBefore < 0 && i < n72BatchCall
                && n72BatchBody[i].TrimStart().StartsWith("return", StringComparison.Ordinal)) n72BatchReturnBefore = i;
        }
        check("第 72 组 · 批量卸载的落盘顺序：先判定后落证（取证在 EvaluateUninstall 之后、return 之前）",
            n72BatchCall > 0 && n72BatchEval >= 0 && n72BatchCall > n72BatchEval && n72BatchReturnBefore < 0,
            $"实测：EvaluateUninstall 在方法体第 {n72BatchEval + 1} 行，取证在第 {n72BatchCall + 1} 行，取证之前有 return={n72BatchReturnBefore >= 0}");

        // ── 单个那条：UninstallPlugin_Click ──
        string[] n72ToolsLines = File.ReadAllLines(n72ToolsPath);
        int n72ToolsDef = -1;
        for (int i = 0; i < n72ToolsLines.Length; i++)
        {
            if (n72ToolsLines[i].Contains("private async void UninstallPlugin_Click(", StringComparison.Ordinal))
            {
                n72ToolsDef = i;
                break;
            }
        }
        if (n72ToolsDef < 0)
        {
            check("第 72 组 · 单个卸载的落盘接线：取证文本必须交给会落盘的 NoteDiagnosis（不是空实现的 Logger.Log），且带上用户主动停止",
                false, "实测：没找到 UninstallPlugin_Click 的定义行");
            return;
        }
        string[] n72ToolsBody = StripWholeCommentLines(ExtractMethodBodyByBraceDepth(n72ToolsLines, n72ToolsDef)).Split('\n');
        int n72ToolsCall = FindUninstallEvidenceCall(n72ToolsBody);
        string n72ToolsCallLine = n72ToolsCall >= 0 ? n72ToolsBody[n72ToolsCall].Trim() : "(没找到调用点)";
        check("第 72 组 · 单个卸载的落盘接线：取证文本必须交给会落盘的 NoteDiagnosis（不是空实现的 Logger.Log），且带上用户主动停止",
            n72ToolsCall >= 0
            && n72ToolsBody[n72ToolsCall].Contains("Logger.NoteDiagnosis(", StringComparison.Ordinal)
            && !n72ToolsBody[n72ToolsCall].Contains("Logger.Log(", StringComparison.Ordinal)
            && n72ToolsBody[n72ToolsCall].Contains("userStopped", StringComparison.Ordinal),
            $"实测：方法体第 {n72ToolsCall + 1} 行 = {n72ToolsCallLine}");

        int n72ToolsEvalLast = -1;
        for (int i = 0; i < n72ToolsBody.Length; i++)
        {
            if (n72ToolsBody[i].Contains("PluginManager.EvaluateUninstall(", StringComparison.Ordinal)) n72ToolsEvalLast = i;
        }
        check("第 72 组 · 单个卸载的落盘顺序：先判定后落证（取证在最后一次 EvaluateUninstall 之后）",
            n72ToolsCall > 0 && n72ToolsEvalLast >= 0 && n72ToolsCall > n72ToolsEvalLast,
            $"实测：最后一次 EvaluateUninstall 在方法体第 {n72ToolsEvalLast + 1} 行，取证在第 {n72ToolsCall + 1} 行");

        // ── 全文件各只有一处调用点（剥掉整行注释后）：杜绝手拼副本。 ──
        string n72BatchAll = StripWholeCommentLines(File.ReadAllText(n72BatchPath));
        string n72ToolsAll = StripWholeCommentLines(File.ReadAllText(n72ToolsPath));
        int n72BatchHits = CountOf(n72BatchAll, "PluginManager.UninstallEvidenceText");
        int n72ToolsHits = CountOf(n72ToolsAll, "PluginManager.UninstallEvidenceText");
        check("第 72 组 · 卸载取证文本的调用点：两个文件各只有一处（剥掉整行注释后），杜绝手拼副本",
            n72BatchHits == 1 && n72ToolsHits == 1,
            $"实测：MainWindow.Batch.cs={n72BatchHits} 处，MainWindow.Tools.cs={n72ToolsHits} 处");
    }

    /// <summary>在已剥掉整行注释的方法体里找 UninstallEvidenceText 的调用点（返回行号，-1 = 没有）。</summary>
    private static int FindUninstallEvidenceCall(string[] bodyLines)
    {
        for (int i = 0; i < bodyLines.Length; i++)
        {
            if (bodyLines[i].Contains("PluginManager.UninstallEvidenceText", StringComparison.Ordinal)) return i;
        }
        return -1;
    }
}
