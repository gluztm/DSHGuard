using System;
using System.Threading.Tasks;
using System.Windows;

namespace DSHGuard;

/// <summary>
/// 2.1.0：「重启 / 启动」引导的**双轨统一出口**。
///
/// 为什么要有这个文件：双轨制之后，"让改动生效"要重启的东西有两个 —— 桌面版主程序，
/// 或 Web 引擎进程。2.0 之前只有后者，所以全项目的提示一律写「重启 DSH 后生效」、
/// 动作一律走 <c>StartEngineAsync()</c>。双轨之后这套话术没跟着改，串轨就出现了：
///   · 站在桌面版改完插件，弹窗说「重启 DSH 后生效」，用户照做 —— 重启的是 Web 引擎，
///     桌面版那份 profile 一个字节没变，等于白等一场（用户原话：「其实是启动了 web」）；
///   · <see cref="OfferRestartAsync"/> 更直接：它调的就是 <c>StartEngineAsync()</c>。
///     在桌面版目标下点「是」，本程序去起了 Web 引擎，桌面版照旧跑着旧插件。
///
/// 现在三条口径都收在这里，别处只许取用、不许自己拼：
///   · 文案 —— <see cref="RestartVerb"/> 及其派生短语（<see cref="RestartEffectHint"/> 等）；
///   · 动作 —— <see cref="OfferRestartAsync"/>（问一句、点「是」才动手）与 <see cref="RestartCurrentTargetAsync"/>；
///   · 作用域 —— 回滚一类按**快照自己的 scope** 取文案（<see cref="RestartVerbFor"/>）而不是当前页签：
///     用户停在桌面版却回滚了一份 Web 快照时，那句该说的仍然是 Web。
/// </summary>
public partial class MainWindow
{
    // ═══ 文案：全部从 TargetContext.RestartVerbOf 派生，保证与右栏、服务控制完全一致 ═══

    /// <summary>按作用域取「重启 &lt;哪个&gt;」。带中英混排空格的口径统一在
    /// <see cref="TargetContext.RestartVerbOf"/>，这里只是带作用域的入口别名。</summary>
    internal static string RestartVerbFor(GuardTarget scope) => TargetContext.RestartVerbOf(scope);

    /// <summary>尾句「重启 DSH（桌面版）后生效。」—— 自带句号，接在句子后面。</summary>
    internal static string RestartEffectHintFor(GuardTarget scope) => RestartVerbFor(scope) + "后生效。";

    /// <summary>片段「需重启 DSH（桌面版）才生效」—— 无句号，嵌在句中。</summary>
    internal static string RestartEffectPhraseFor(GuardTarget scope) => "需" + RestartVerbFor(scope) + "才生效";

    /// <summary>片段「需要重启 DSH（桌面版）才生效」—— 无句号，句首起头用。</summary>
    internal static string NeedRestartPhraseFor(GuardTarget scope) => "需要" + RestartVerbFor(scope) + "才生效";

    /// <summary>「正在运行会占用文件」那一句里的对象名（桌面版占的是自己的 node_modules）。</summary>
    internal static string OccupierNameFor(GuardTarget scope) => scope == GuardTarget.Desktop ? "DSH 桌面版" : "DSH";

    /// <summary>「建议先把它停掉」的建议动作（桌面版是结束桌面版，Web 是停止引擎）。</summary>
    internal static string StopAdviceFor(GuardTarget scope) => scope == GuardTarget.Desktop ? "（建议先结束桌面版）" : "（建议先停止引擎）";

    /// <summary>同上但不带括号：嵌进「建议先 ___，再重试」这种句子里用。</summary>
    internal static string StopAdviceVerbFor(GuardTarget scope) => scope == GuardTarget.Desktop ? "结束桌面版" : "停止引擎";

    // ═══ 上面那几个是给「按作用域」用的（回滚快照）；下面这几个是当前管理对象的快捷写法 ═══

    /// <summary>当前管理对象的「重启 <哪个>」。界面上的每一句重启提示都该含它。</summary>
    private string RestartVerb => RestartVerbFor(_ctx.Target);

    private string RestartEffectHint => RestartEffectHintFor(_ctx.Target);

    private string RestartEffectPhrase => RestartEffectPhraseFor(_ctx.Target);

    private string NeedRestartPhrase => NeedRestartPhraseFor(_ctx.Target);

    private string OccupierName => OccupierNameFor(_ctx.Target);

    private string StopBeforeWriteAdvice => StopAdviceFor(_ctx.Target);

    // ═══ 动作：真正去重启「当前管理对象」的那一个 ═══

    /// <summary>
    /// 重启当前管理对象（不问、不弹框）。
    /// 桌面版 ⇒ 结束旧进程再拉起；Web ⇒ 优雅停止后重新启动引擎。
    ///
    /// 桌面版这一支走 <see cref="StopDesktopAppQuiet"/> 而不是 <see cref="StopDesktopApp"/>：
    /// 后者自带一个「结束桌面版？」确认框，而这里的确认在上游的「现在就重启吗？」已经问过了，
    /// 连着弹两个框等于让用户把同一件事答两遍。
    /// </summary>
    internal async Task RestartCurrentTargetAsync()
    {
        try
        {
            if (_ctx.IsDesktop)
            {
                StopDesktopAppQuiet();
                await Task.Delay(1200);      // 给它一点真正退出的时间，别紧接着拉起一个抢文件的
                LaunchDesktopApp();
                return;
            }
            if (_isRunning)
            {
                await GracefulStopAsync();
                await Task.Delay(800);
            }
            await StartEngineAsync();
        }
        catch (Exception ex) { Logger.LogError("RestartCurrentTargetAsync", ex); }
    }

    /// <summary>
    /// 改动落盘后询问是否立即重启**当前管理对象**。
    ///
    /// 桌面版轨：桌面版主程序没在跑时只说明"下次启动桌面版后生效"（没什么可重启的，
    /// 也**绝不能**去动 Web 引擎）；在跑时才问要不要重启它。
    /// Web 轨：行为与 2.0 一字未动 —— 外部引擎（终端里启动的）不接管，只提示手动重启；
    /// 本程序启动的引擎也需用户点「是」才重启（重启会中断正在运行的任务）。
    /// </summary>
    internal async Task OfferRestartAsync(string what)
    {
        try
        {
            if (_ctx.IsDesktop)
            {
                if (!DesktopAppRunning())
                {
                    GuardDialog.Show(
                        $"已应用：{what}。\n\n" +
                        "当前管理对象是 DSH 桌面版，桌面版主程序此刻没有在运行 —— " +
                        "改动会在你下次启动桌面版后生效。",
                        "稍后生效", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var rd = GuardDialog.Show(
                    $"已应用：{what}。\n\n现在就重启 DSH 桌面版吗？重启会中断桌面版里正在运行的任务。\n" +
                    "（选「否」则下次手动启动桌面版时生效）",
                    "重启 DSH 桌面版", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (rd != MessageBoxResult.Yes) return;

                await RestartCurrentTargetAsync();
                return;
            }

            if (_engineExternal)
            {
                GuardDialog.Show(
                    $"已应用：{what}。\n\n" +
                    "当前引擎由外部（例如终端）启动，本程序不接管其进程，请手动重启引擎以生效。",
                    "稍后生效", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var r = GuardDialog.Show(
                $"已应用：{what}。\n\n现在就重启引擎吗？重启会中断正在运行的任务。\n" +
                "（选「否」则下次手动启动引擎时生效）",
                "重启引擎", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;

            await RestartCurrentTargetAsync();
        }
        catch (Exception ex) { Logger.LogError("OfferRestartAsync", ex); }
    }
}
