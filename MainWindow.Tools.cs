using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DSHGuard;

/// <summary>
/// 插件列表 / 版本详情 / 说明页 / 设置页（启动命令 · 工具扫描 · 分析钩子）。
/// 与 MainWindow.xaml.cs 同属 partial class。
/// </summary>
public partial class MainWindow : Window
{
    private List<PluginManager.Plugin> _plugins = new();
    private string _currentDshVersion = "未知";
    private VersionInfo.Info? _versionInfo;

    // ── 守护壳**自身**的版本检测（与上面 _versionInfo 那条"运行中的 DSH"完全是两件事：
    //    那条查的是引擎在下载来源上的版本，这条查的是本程序自己有没有新版本）──
    private GuardUpdateVerdict _guardUpdateVerdict = GuardUpdateVerdict.Unknown;
    private string _guardRemoteVersion = "";
    private bool _guardUpdateChecked;      // 本次会话查过没有（没查过 ⇒ 界面写「尚未检查」，不下任何结论）
    private bool _guardUpdateBusy;         // 正在查（挡住重入：连点「检查更新」不并发发多次请求）
    private bool _guardUpdateNotified;     // 「有新版本」的事件只追加一次，不刷屏

    // ── 应用内更新（下载 → 校验 → 退出 → 交给安装器）──
    private GuardUpdateProgressWindow? _guardUpdateProgress;   // 更新进度窗（空 = 没弹）
    private bool _guardUpdateOwnerEnabled = true;              // 弹窗前主窗的可用状态，收尾按**原值**还原
    private System.Threading.CancellationTokenSource? _guardUpdateCts;   // 「取消下载」用

    /// <summary>
    /// 「检查 → 下载 → 校验 → 就绪」这整段流程的总时长上限（整程兜底）。
    ///
    /// 为什么需要它：下载循环内部那两道闸（停滞 30 秒 / 总预算 20 分钟）只在**下载循环内部**生效 ——
    ///   ① 若某条线路"每次只挤一点字节"，停滞闸永远够不到触发条件，用户只能干等满 20 分钟；
    ///   ② 「查版本」「校验」「收尾」这几步在下载循环**之外**，原本没有任何时长上限，
    ///      请求被中间设备静默挂住时整个流程会无限期挂着，进度窗既不结束也不报错。
    ///
    /// 为什么是 25 分钟：下载自身的预算是 20 分钟，再给"查版本 / 校验 / 备份 / 收尾"留 5 分钟余量。
    /// 这道上限必须**严格大于**下载预算，否则一个正常的大包慢速下载会被兜底误杀。
    /// </summary>
    private static readonly TimeSpan GuardUpdateTotalTimeout = TimeSpan.FromMinutes(25);
    private bool _guardUpdateSweepDone;        // 启动补删只跑一次
    private bool _guardUpdateHandedOff;        // 安装包已交接给外部进程（此时**不许**再删暂存文件）
    private bool _guardUpdateExitGuardOn;      // 退出兜底只挂一次

    private bool _loaderIdsLoaded;
    private Dictionary<string, string> _loaderIds = new();

    private static readonly string[] ToolScripts = { "clean-logs.ps1", "port-check.ps1", "check-plugin-updates.ps1", "install-node.ps1" };

    // ── 插件更新检查的缓存（同一次会话内不重复查询；点「刷新」强制重查）──
    private readonly Dictionary<string, PluginManager.PluginUpdate> _pluginUpdates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 最近一次 <see cref="RenderPlugins"/> 实际写进面板的卡片顺序（包名）。
    /// 「自检怎么证明排序真的生效」本来只能靠 <c>面板子项数</c>，而排序**不改变卡片数量** ⇒ 数量恒等于样本数、
    /// 恒过、证明不了任何事。这里把"渲染时真实用过的那个列表"按顺序记下来，自检再拿它对同样的数据独立算一遍
    /// <see cref="SortInstalledPlugins"/> 的结果比对 —— 两处判据同源（同一个比较键函数），能真的验出排序失效。
    /// </summary>
    private List<string> _installedRenderOrder = new();
    private DateTime _updatesCheckedAt = DateTime.MinValue;
    private bool _updatesChecking;
    private string _updatesError = "";

    private static string ToolsDir => Path.Combine(AppContext.BaseDirectory, "Tools");

    // ═══ 导航入口 ═══
    private void PluginsButton_Click(object sender, MouseButtonEventArgs e) => ShowView(GuardView.Plugins);

    /// <summary>右栏「DSH 版本」卡片：跳转到「设置 -> 版本」二级菜单。</summary>
    private void VersionCard_Click(object sender, MouseButtonEventArgs e) => ShowVersionPage();

    /// <summary>跳转到「设置 -> 版本」页。</summary>
    private void ShowVersionPage()
    {
        try
        {
            _settingsTab = SettingsTab.Version;
            ShowView(GuardView.Settings);
            RenderVersionView();
        }
        catch (Exception ex) { Logger.LogError("ShowVersionPage", ex); }
    }

    // ══════════════ 插件列表 ══════════════
    private void RefreshPlugins_Click(object sender, MouseButtonEventArgs e) => _ = RefreshPluginsAsync(true);
    private void PluginSearchBox_TextChanged(object sender, TextChangedEventArgs e) => RenderPlugins();

    private async Task RefreshPluginsAsync(bool forceReload = false)
    {
        try
        {
            PluginsSummaryText.Text = "正在扫描插件…";
            if (forceReload || _currentDshVersion == "未知")
                _currentDshVersion = VersionInfo.GetCurrentVersion();

            if (forceReload || _plugins.Count == 0)
            {
                _plugins = await Task.Run(() => PluginManager.Scan(_currentDshVersion));
                // 重扫会替换整批新对象（LoaderId 全为 null），而 id 表为另外异步读取。
                // 若不在此处补一次，将出现"dump 退出码为 0、缓存中也存在该包，
                // 点击「禁用」仍提示无法读取内部标识"（现场根因：_loaderIdsLoaded 已为 true，即不再读取 id，
                // 因而无人将 id 写回新对象）。对象被替换即必须重新写入，与"是否读取过"无关。
                BackfillLoaderIds();
            }

            if (!_loaderIdsLoaded)
                await LoadLoaderIdsAsync();

            RenderPlugins();
            _ = CheckPluginUpdatesAsync(forceReload);   // 顺带查新版本（后台，不阻塞渲染）
        }
        catch (Exception ex)
        {
            Logger.LogError("RefreshPluginsAsync", ex);
            PluginsSummaryText.Text = "扫描失败：" + LogPromise("详细原因已记入日志，可在「日志」页查看。");
        }
    }

    // ══════════════ 插件更新检查（tools\check-plugin-updates.ps1） ══════════════
    /// <summary>
    /// 每次刷新插件列表时顺带检查新版本：执行脚本 -> 解析 JSON -> 回填到卡片。
    /// 结果缓存 10 分钟；force=true（点「刷新」）时忽略缓存。失败只提示，不打断列表。
    /// </summary>
    private async Task CheckPluginUpdatesAsync(bool force = false)
    {
        if (_updatesChecking) return;
        if (!force && _pluginUpdates.Count > 0 && (DateTime.Now - _updatesCheckedAt) < TimeSpan.FromMinutes(10)) return;

        _updatesChecking = true;
        try
        {
            string script = Path.Combine(ToolsDir, "check-plugin-updates.ps1");
            if (!File.Exists(script))
            {
                _updatesError = "查新版本的小工具不在，暂时查不了更新";
                return;
            }

            var (ok, output) = await RunCommandAsync("powershell",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -ProfileDir \"{PluginManager.ProfileDir}\"",
                timeoutMs: 180000);
            // （执行层：上面这条已由 RunCommandAsync 走 ArgumentList —— 引号由 token 拆分剥掉，
            //  路径里的空格/元字符都在单个 token 内，不会被 cmd 重新解释。）

            var list = PluginManager.ParseUpdateReport(output);
            if (list.Count == 0)
            {
                // 口径统一：全项目只说「下载来源」（版本页的切换项与提示都这么写，自检按字面钉着）；
                // 「软件源」是本页自造的第二种叫法，同一件事两个名字。只改叫法，判据（ok）与赋值语义不动。
                _updatesError = ok ? "未查到更新信息" : "无法连接下载来源（可能断网）";
                Logger.Log($"插件更新查询失败: ok={ok} out={Shorten(output, 200)}");
            }
            else
            {
                _pluginUpdates.Clear();
                foreach (var u in list) _pluginUpdates[u.Name] = u;
                _updatesCheckedAt = DateTime.Now;
                _updatesError = "";
                // git 源的插件（github:o/r#sha、git+https://…）永远查不到 npm 版本，即更新按钮不出现。
                // 这里按"跟到仓库最新"补一条可更新记录，命令改走仓库地址本身（现场：dsh-watcher、inline-edit）。
                string lockText = PluginManager.LockText();
                foreach (var p in _plugins)
                {
                    // 注意：更新报告里已经有这个包（标注"镜像源里没有/已是最新"），
                    // 那是按 npm 版本查的结论，对 git 源必然错，即这里要覆盖它，不能跳过。
                    string spec = PluginManager.DepSpec(p.Name);
                    var kind = PluginSource.Classify(spec);
                    if (kind == PluginSource.Kind.Registry || kind == PluginSource.Kind.Unknown) continue;
                    // npm 已给出"确实有新版本"的结论时不要覆盖（那种更新走版本号，比跟仓库更准）；
                    // 但显示标签不能当判据 —— 版本位现在只放真版本号/短提交号，所以判据改成
                    // "它不是我们给 git 源算出来的那种值"，即：只在 npm 侧拿得出具体版本号时才让位。
                    if (_pluginUpdates.TryGetValue(p.Name, out var prev) && prev.HasUpdate
                        && PluginManager.ConcreteVersionOf(prev.Latest).Length > 0) continue;

                    // 形态闸门（失败关闭，贯穿到查新侧）：白名单不接受的 git 来源形态（如 `git://…`
                    //   明文协议、scp 形态…）安装侧一律拒绝执行（`PluginManager.IsValidGitSource`
                    //   返回 false，即 `BuildAddSourceArgs` / `BuildUpdateArgs` 给不出目标）。
                    //   既然这份声明装不上也升不动，查新侧就不该拿它去跟远端比、更不该报"有新版"
                    //   —— 否则卡片永远显示「更新到最新提交」、用户一点却没有任何命令执行，即「按钮坏了」。
                    //   故这里先问一句安装侧的白名单（本方法是"调用侧"，两层都能调；`PluginSource`
                    //   是被依赖的下层，不能反向调 `PluginManager`，会成环）：
                    //   不可安装，即如实写「待确认」，不报更新、不出按钮。注意：判据只有这一份，
                    //   刻意不在 `PluginSource` 里镜像复制 —— 白名单将来加了 scheme，这里自动跟上。
                    if (!CanInstallPluginSource(spec))
                    {
                        _pluginUpdates[p.Name] = UnknownSourceUpdate(p.Name, spec);
                        continue;
                    }

                    string commit = PluginSource.LockedCommit(lockText, p.Name);
                    // 按 ref 类型分流（用户口径 2026-09-18）：
                    //   「master 肯定要报」「如果是分支版本的有另一种提示方法，可选升级」
                    //   「gitee 是个共同 contribute 的 git 平台，所以应该读的是参与者们的提交吧？」
                    //   本壳比的是默认分支（或用户钉的那个 ref）的最新提交，与仓库的发行版/标签无关；
                    //   二者本来就会不一致（作者持续在 master 提交却不打新标签）—— 这不是 bug，但必须写明白。
                    //   四个分支由 PluginSource.DecideUpdate 一处判定（无 ref 则用 Hard；#sha 则用不报；
                    //   #分支 则用 Advisory；#标签 则用默认不报、被移动才报；问不出来则用失败关闭 Unknown）。
                    //   必须把锁文件里那个提交（commit）一起传进去：它是"远端变没变"的另一端，
                    //     漏传，即 DecideUpdate 判不了"相同"，即永远提示有新提交（本函数第一版即因漏传而出过缺陷）。
                    // 2026-09-19：改调 Detailed 版（多解构末位 refFailure）—— 多出来的这一位就是
                    //   「这次到底为什么没问出来」的类别，下面原样带进卡片记录（FailureHint），
                    //   悬停再照抄 PluginSource.RefProbeFailureHint 的措辞，即不再把三种原因混成一句。
                    //   判定完全没动：Detailed 版与老签名是同一套 DecideUpdate，类别只是诊断位
                    //     （见 PluginSource.FetchRefAwareLatestDetailedAsync 的说明：任何类别都不会让
                    //     Decision 变成"有新版"），即失败关闭不变。
                    var (refKind, refMatch, repoVer, repoDate, repoShort, repoAuthor, repoConf, decision, refFailure) =
                        await PluginSource.FetchRefAwareLatestDetailedAsync(spec, commit);
                    // 版本位只放真实的版本号或短提交号（PluginManager.GitVersionLabel），
                    //   绝不放「仓库最新」这种显示标签 —— 那个标签以前既误导用户、
                    //   又被当成版本号拼进命令（现场：dsh-codearts-auth@仓库最新 被 pnpm 拒掉）。
                    string target = repoVer.Length > 0 ? repoVer : repoShort;

                    // 「有新版」的显示口径：Hard（默认分支，计入一键更新）与 Advisory（可选升级，不计入）
                    // 都算"看得到的新版"；两者靠 TargetLabel / StatusNote 的文案区分，靠 HasUpdate 分开计数。
                    bool hasNewCommit = decision is PluginSource.UpdateDecision.HardNewCommit
                                                   or PluginSource.UpdateDecision.AdvisoryNewCommit;
                    bool advisory = decision == PluginSource.UpdateDecision.AdvisoryNewCommit;
                    string sameVerNote = repoVer.Length > 0 && string.Equals(repoVer, p.Version ?? "", StringComparison.OrdinalIgnoreCase)
                        ? "，有新提交" : "";
                    // 远端当前提交的「短号 · 日期 · 作者」——摆出作者是为了证明读的是参与者们的提交、
                    // 不按作者过滤（用户问过这一点）。与发行版/标签无关，见 CompareBasisNote。
                    string commitNote = PluginSource.NewCommitNote(repoShort, repoDate, repoAuthor);
                    // 状态行的优先级：可选升级（另起一行整句说明，这里留空）-> 说不清
                    //   （远端查不到 / 远端已无该 ref / 无法确认是分支还是标签）-> 空串（走"已是最新"）。
                    //   前者是"确实有新提交但由用户决定"，后者是"根本无法证明"，两种措辞绝不混用。
                    string refUnknown = PluginManager.GitRefUnknownText(refKind, refMatch);
                    string statusNote = advisory
                        ? ""
                        : (refUnknown.Length > 0 ? refUnknown : PluginManager.GitStatusUnknownText(repoConf));
                    _pluginUpdates[p.Name] = new PluginManager.PluginUpdate
                    {
                        Name = p.Name,
                        // 「从哪个版本更新」：已安装版本号优先，无法读取时才回退为短提交号
                        // （git 源尚未安装时锁文件中没有记录，此处按卡片同一口径显示「未安装」）。
                        // 只是显示值，不参与判定（判定见上面的 decision）。
                        Installed = PluginManager.GitVersionLabel(
                            IsVersionPlaceholder(p.Version) ? "" : p.Version,
                            commit.Length >= 7 ? commit : ""),
                        // 版本位同样只放真版本号 / 短提交号；取不到就如实写「版本未知」。
                        Latest = PluginManager.GitVersionLabel(target.Length > 0 ? target : "", commit.Length >= 7 ? commit : "")
                               + (hasNewCommit ? sameVerNote : ""),
                        // 按钮 / 确认框 / 汇总行都显示这一位：git 源写「最新提交」，不放假版本号；
                        // 保留"更新到<这一位>？"能顺读（可选升级时仍是「最新提交（该分支/标签）」）。
                        TargetLabel = advisory ? "最新提交（该分支/标签）" : "最新提交",
                        // 「可选升级」的整句标题（含「可选」二字，用户的硬要求）：
                        // 例「可选升级：远端分支 dev 有新提交 9669ee4」。非可选时为空串（老显示逻辑零改动）。
                        AdvisoryNote = advisory
                            ? PluginManager.GitAdvisoryText(refMatch, PluginSource.RefPart(spec), repoShort)
                            : "",
                        // 优先级见上面 statusNote 的说明（可选升级留空 / 说不清如实写 / 其余空串）。
                        StatusNote = statusNote,
                        // 这次"说不清"的类别（只喂显示层的悬停；判定一个字节都不参与）。
                        //   None 有两种正常来源（查成了 / 查不成但无可指名类别），即悬停回落到原来那句可能式措辞。
                        FailureHint = refFailure,
                        // 只有 Hard / Advisory 才 HasUpdate=true；Unknown 与 UpToDate 都不报（失败关闭）。
                        //   计入「一键更新 N 个」的只有 Hard（见 UpdatableCount：advisory 不算）。
                        HasUpdate = hasNewCommit,
                        Advisory = advisory,
                        Published = repoDate,
                        NewRequirement = "",
                        NewRequirementSource = "",
                        // 「比的是谁」那句话：卡片的悬停与确认框都从这里取，用户一眼看清
                        // 按<默认分支|分支 X|标签 Y>的提交比对，与仓库的发行版（标签）不同
                        // （用户：「我认为应该写明白」）。
                        CompareNote = PluginSource.CompareBasisNote(spec, refMatch),
                        // 远端提交的「短号 · 日期 · 作者」（作者证明读的是参与者们的提交、不按作者过滤）。
                        CommitNote = commitNote,
                        // 可点开的提交链接（站点已受白名单约束；打开前仍会过既有链接闸门）。
                        CommitUrl = PluginSource.CommitUrl(spec, repoShort.Length > 0 ? repoShort : "")
                    };
                }
                // 计数必须与「一键更新 N 个」按钮同源：上面的 list 只是"更新报告原文"，
                // 后来给 git 源插件补的那条"跟仓库最新"不在里面 —— 现场就是事件说 4、按钮说 5。
                int n = UpdatableCount();
                Logger.Log($"插件更新检查：报告原文 {list.Count} 个包（其中 {list.Count(u => u.HasUpdate)} 个有更新），"
                         + $"并入 git 源后合并表里有更新 {n} 个");
                if (n > 0) AddEvent(PluginUpdateEventText(n), EventKind.Update);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("CheckPluginUpdatesAsync", ex);
            _updatesError = "查新版本时出了点问题";
        }
        finally
        {
            // 先结束检查状态再渲染：否则渲染时 _updatesChecking 仍为 true，摘要行会一直停在「正在查新版本…」
            _updatesChecking = false;
            RenderPlugins();
        }
    }

    /// <summary>
    /// 这条插件来源形态能不能被安装侧接受（纯函数，便于自检直接断言；不查网）。
    ///
    /// 存在的唯一理由：查新侧与安装侧必须同一个口径（失败关闭原则贯穿到查新侧）。
    ///   · 安装侧：<see cref="PluginManager.IsValidGitSource"/> 是 git/URL 来源的闭合白名单，
    ///     只有简写形态（<c>github:</c> / <c>gitlab:</c> / <c>bitbucket:</c> + owner/repo）与
    ///     <c>https://</c> / <c>git+https://</c>（host 限白名单站点）放行；
    ///     <c>git://</c>（明文协议）、<c>file:</c>、<c>http://</c>、scp 形态（<c>git@host:o/r</c>）
    ///     一律拒绝，即 <c>BuildAddSourceArgs</c> / <c>BuildUpdateArgs</c> 返回空串、调用方不得执行。
    ///   · 查新侧：<see cref="PluginSource.Classify"/> 却把 <c>git://…</c> 也算 <c>gitLike</c>，即会去查远端、
    ///     有差异就报「有新版」，即卡片给按钮、点了却什么都不执行（现场缺陷）。
    ///
    /// 这里不复刻那份白名单，而是直接调用它 —— 判据只有一份，将来白名单增删 scheme，
    ///   查新侧自动跟上，两份口径不可能漂移。反之（把白名单抄进 <see cref="PluginSource"/>）会有两份
    ///   会漂移的判据；且 <see cref="PluginSource"/> 是下层，反向调 <c>PluginManager</c> 会成环。
    ///
    /// 非 git 形态（npm 包名）不归本闸门管：<c>IsValidGitSource</c> 对包名必为 false，
    /// 故先由 <see cref="PluginSource.Classify"/> 确认"这确实是一条 git 来源"，再问白名单。
    /// </summary>
    internal static bool CanInstallPluginSource(string? spec)
        => PluginSource.Classify(spec) == PluginSource.Kind.Registry
           || PluginManager.IsValidGitSource(spec);

    /// <summary>
    /// 「这个 git 来源形态装不上也升不动」时卡片上的那条如实记录（纯函数，便于自检）：
    /// <c>HasUpdate = false</c>，即不报更新、不出更新按钮、不计入「一键更新 N 个」；
    /// <c>StatusNote</c> 走"待确认"那一支，措辞只陈述事实（既不说新版、也不说已是最新）。
    /// </summary>
    internal static PluginManager.PluginUpdate UnknownSourceUpdate(string? name, string? spec)
        => new()
        {
            Name = (name ?? "").Trim(),
            // 判定根本没走「跟仓库比提交」这条路，即这里没有能摆出来的提交/版本事实，一律留空
            // （显示回落为既有的「版本未知」口径）。
            Installed = "",
            Latest = "",
            // 中性、无命令行、无内部标识：这是一句"来源形态不受支持"的如实说明。
            StatusNote = UnsupportedSourceNote,
            HasUpdate = false,
            Advisory = false,
            // 「比的是谁」这一栏在本档必须改口：本壳**根本没去问远端**，也就不存在"比对基准"——
            //   照旧调 CompareBasisNote 会把 `file:` / scp / `git://` 这类形态说成「按仓库默认分支的
            //   最新提交比对」（ClassifyRef 对"没写 #ref"的它们一律返回 DefaultBranch），
            //   与同一张卡片正文的 UnsupportedSourceNote（"该来源地址的写法目前不支持安装与更新"）
            //   正面矛盾 —— 正文说根本不支持、悬停却说要按仓库默认分支比对（本单缺陷）。
            //   故本档改用下面那条中性说明（文案仍只有一处实现），不再复用"比对基准"那套措辞。
            CompareNote = UnsupportedSourceCompareNote
        };

    /// <summary>
    /// 上面那条 <c>StatusNote</c> 的唯一文案实现处（纯函数，便于自检断言措辞与"不含内部标识"）。
    /// 刻意与「远端查不到」（<c>PluginManager.GitStatusUnknownText</c>：「未取到仓库最新提交」）区分开：
    /// 本情形不是取不到，而是"这个来源形态安装侧本就不接受"，两句话不能混用。
    ///
    /// 标准表述（2026-09-19 文案标准化）：只陈述事实，不出现内部标识 / 命令行 / 站点专名；
    ///   实测现场的那条路径根本没去问远端（见 <see cref="CanInstallPluginSource"/> 的形态闸门），
    ///   故措辞不得暗示网络原因和站点专名。
    /// </summary>
    internal const string UnsupportedSourceNote = "更新状态待确认（该来源地址的写法目前不支持安装与更新）";

    /// <summary>
    /// 「来源形态不受支持」那一档摆进悬停「比对基准：」那一行的说明（唯一文案实现处）。
    ///
    /// 为什么本档不能沿用 <see cref="PluginSource.CompareBasisNote"/>（2026-09-19 正面矛盾缺陷）：
    ///   那些装不上的来源形态（<c>file:</c> / scp 形态 / <c>git://</c>）在
    ///   <see cref="PluginSource.ClassifyRef"/> 眼里都是"没写 <c>#ref</c>"（它们压根没有 <c>#</c>），
    ///   一律返回 <see cref="PluginSource.RefKind.DefaultBranch"/> ⇒ <c>CompareBasisNote</c> 便写成
    ///   「按仓库默认分支的最新提交比对；与仓库的发行版（标签）无关。」。
    ///   可本壳在这条路径上**从未向远端发起过查询**（形态闸门 <see cref="CanInstallPluginSource"/>
    ///   在查新之前就把它拦下了，见 <see cref="UnknownSourceUpdate"/>），
    ///   "按仓库默认分支比对"是一句没发生过的事 —— 它还与同一张卡片正文的
    ///   <see cref="UnsupportedSourceNote"/>（"该来源地址的写法目前不支持安装与更新"）正面打架。
    ///
    /// 标准表述（与 <see cref="UnsupportedSourceNote"/> 同一口径）：只陈述事实，一句一事；
    ///   不出现内部标识 / 命令行 / 站点专名；不暗示网络原因（本条根本没查网）。
    ///   刻意保留「比对基准：」这个字段名（悬停的五段式结构不变），只是把它后面的内容改成实话：
    ///   本档"比的是谁"这个问题的正确答案就是——不适用（压根没有比对）。
    ///   括注里的"该来源地址的写法不受支持"与「状态：」那一行逐字同形（同一件事不换两种叫法），
    ///   "未向远端发起查询"则与「原因：」那一行同形。
    /// </summary>
    internal const string UnsupportedSourceCompareNote = "不适用（该来源地址的写法不受支持，本壳未向远端发起查询）。";

    /// <summary>
    /// 卡片状态行的悬停文案（纯函数，便于自检直接断言；只读不查网）。
    ///
    /// 标准表述（2026-09-19 文案标准化）：一行一项、带字段名，扫一眼就懂 ——
    ///   <c>比对基准：</c>比的是谁（<c>CompareNote</c>，用户的硬要求：这句话必须写明白，不许删）
    ///   <c>状态：</c>是什么情形　<c>原因：</c>为什么　<c>下一步：</c>怎么办（可操作性，用户的硬要求）
    ///   <c>说明：</c>有什么影响。
    ///
    /// 本方法存在的唯一理由：<c>StatusNote</c> 有多种来源（未取到仓库最新提交 / 远端已无这个分支或标签 /
    ///   无法确认是分支还是标签 / 该来源地址的写法目前不支持安装与更新），所以"是什么情形"只能照抄正文那句。
    ///   以前悬停把原因硬编码成「网络不通、站点不支持或仓库不可访问」，即正文说"来源不支持"、悬停却说"网络不通"，
    ///   两处自相矛盾（现场缺陷）。任何"猜测式"的原因说明都不许再写回这里。
    ///
    /// 硬底线（拿不准时）：类别为 <see cref="PluginSource.RefProbeFailure.None"/> 且没有别的确凿事实时，
    ///   「原因」那一行只许写「暂无法确定」—— 绝不编原因，也绝不回落到"都可能是原因"这种可能式措辞。
    ///
    /// 签名只有一个入参 <c>upd</c>：分档要用到 <c>StatusNote</c> 之外的 <c>CompareNote</c> / 来源形态，
    ///   故整条记录传进来（不要再拆出"只收 string"的重载 —— 那种拆法在本项目已造成过 CS1503）。
    /// </summary>
    internal static string UpdateStatusHover(PluginManager.PluginUpdate upd)
    {
        // StatusNote 在赋值处一律取真实文案；?? "" 只为将来若被写成 null 兜底（字段声明是非空 string）。
        string reason = upd.StatusNote ?? "";
        // 空，即不显示悬停（与老行为一致；与渲染处 `upd.StatusNote.Length > 0` 的分支条件同口径）。
        if (reason.Length == 0) return "";

        // ── 第①项：比对基准（CompareNote）必须保留：用户明确要求过"应该写明白"
        //    （硬要求，短号/日期对不上时的解释全在这里）。CompareNote 本身已是一句完整说明，
        //    这里只补字段名 —— 措辞只有两处实现，且都按"本壳这次到底比没比"如实给：
        //      ① 真去比过的那些档 -> PluginSource.CompareBasisNote（"按…的最新提交比对"）；
        //      ② 压根没去问远端的"来源形态不受支持"档 -> 本类的 UnsupportedSourceCompareNote
        //         （"没有可用的比对基准"，与正文 UnsupportedSourceNote 同口径）。本处不另写。
        string who = string.IsNullOrEmpty(upd.CompareNote)
            ? ""
            : "比对基准：" + upd.CompareNote + "\n";

        // ── 分档判据（三档互斥、按"最具体优先"排；判据全是实例数据：正文那句的真实文案 ＋ 来源形态
        //    ＋ 本壳这次查到的失败类别；不动任何赋值处）：
        //  1. UnsupportedSourceNote：这条来源形态装不上也升不动，查新侧根本没去问远端
        //     因此这里既不说网络、也不给"重试/登录"这类下一步（这正是本单要修的那种不符）：有 CompareNote 的
        //     "待确认"记录必然出自 UnknownSourceUpdate。
        // ⚠ 判据是"**这份声明装不装得上**"，不是"Kind 是不是 Unknown"（2026-09-19 正面矛盾缺陷）：
        //   旧写法 `Classify(DepSpec(名)) == Kind.Unknown` 把两件事混成一件 —— 而 `file:` / scp 形态 /
        //   `git://` 的 `Classify` 都是 **GitBare**（不是 Unknown，见 PluginSource.Classify 的前缀表），
        //   于是"装不上"的那些来源全部漏判 ⇒ 悬停给出「尚未获取到最新提交 / 暂无法确定」⇒ 与正文
        //   「该来源地址的写法目前不支持安装与更新」正面矛盾（正文说压根没支持、悬停说只是还没查到）。
        //   现在直接问安装侧那道唯一的形态闸门（本类自己的 CanInstallPluginSource，它内部复用
        //   PluginManager.IsValidGitSource）—— 判据只有一份，将来白名单增删 scheme 这里自动跟上。
        //   前置的"CompareNote 非空"必须留着：没有比对新版记录的样本（CompareNote 为空）不该被本档吞掉。
        //  2. 「远端已无这个分支或标签」：问了远端、答案明确 —— 是这个 ref 不在了（改过名、被删掉、
        //     或这个标签在远端本来就查不到），与网络无关，即同样不提网络、不给网络式下一步。
        //  3. 其余（「未取到仓库最新提交」/「无法确认是分支还是标签」）：真的没问到，即先看
        //     upd.FailureHint 那一档是否说得出是哪一种（2026-09-19 起：说得出就写清那一种，不再一律混成一句）；
        //     说得出/说不出都由 PluginSource.RefProbeFailureHint / RefProbeFailureNextStep 一处说了算，
        //     本方法只照抄，绝不另写一套措辞。
        //  4. 类别也说不出（None）：如实写「暂无法确定」，不替它编原因（硬底线），下一步同为空。
        bool unsupportedSource = !string.IsNullOrEmpty(upd.CompareNote)
                                 && !CanInstallPluginSource(PluginManager.DepSpec(upd.Name));
        bool remoteMissing = reason.Contains("远端已无");

        // 「状态」那一行：不照抄正文（正文那句是给卡片行的紧凑括注），只按同一档位改述，
        // 信息量与正文等价（三档都能从正文原句推出来，未新增任何事实）。
        // 分隔符用「：」不用破折号 —— 状态行一句一事，不串句（文案标准化）。
        string status = unsupportedSource
            ? "状态：待确认（该来源地址的写法不受支持）\n"
            : remoteMissing
                ? "状态：待确认（远端已不存在所指定的分支或标签）\n"
                : "状态：待确认（尚未获取到最新提交）\n";

        // 「原因」那一行：说得出就写清是哪一种，说不出就如实写「暂无法确定」（不编）。
        string why = unsupportedSource
            ? "原因：本壳不支持该来源地址的写法，未向远端发起查询\n"
            : remoteMissing
                ? "原因：远端已不存在所指定的分支或标签，无法读取其提交\n"
                : "原因：" + RefProbeFailureHintOrUnknown(upd);

        // 「下一步」那一行：可操作性（用户硬要求）。只在真有可做的事时出现 ——
        //   "远端已无这个 ref" / "来源形态不支持"两档没有用户可执行的下一步，那一行就不出现，
        //   不写"请稍后重试"之类的空话（不唠叨、不给假方向）。
        string next = unsupportedSource || remoteMissing
            ? ""
            : RefProbeFailureNextStepOrEmpty(upd);

        // 「说明」那一行：影响说明（用户硬要求：不动内容/不影响现有功能）；不解释技术成因。
        return who + status + why + "\n"
             + next
             + "说明：本次查询结果不影响插件内容与现有功能。";
    }

    /// <summary>
    /// 悬停「原因」行的取值（纯函数，便于自检直接断言）：照抄
    /// <see cref="PluginSource.RefProbeFailureHint"/>（文案唯一实现处），
    /// 说不出类别（<see cref="PluginSource.RefProbeFailure.None"/>）时如实写「暂无法确定」——
    /// 这里是"拿不准"的唯一落点，绝不编原因（用户硬底线）。
    ///
    /// 刻意让 <c>RefProbeFailureHint(None)</c> 继续返回空串（既有的自检契约与措辞纪律都钉着这一点），
    ///   "暂无法确定"只在本层（显示层）出现，即判据侧一个字节不动。
    /// </summary>
    internal static string RefProbeFailureHintOrUnknown(PluginManager.PluginUpdate upd)
    {
        string hint = PluginSource.RefProbeFailureHint(upd.FailureHint);
        return hint.Length > 0 ? hint : "暂无法确定";
    }

    /// <summary>
    /// 悬停「下一步」行的取值（纯函数，便于自检直接断言）：照抄
    /// <see cref="PluginSource.RefProbeFailureNextStep"/>；说不出类别时返回空串
    /// （调用方整行不出现），与"拿不准就什么都不说"同口径。
    /// </summary>
    internal static string RefProbeFailureNextStepOrEmpty(PluginManager.PluginUpdate upd)
    {
        string step = PluginSource.RefProbeFailureNextStep(upd.FailureHint);
        return step.Length > 0 ? "下一步：" + step + "\n" : "";
    }

    private PluginManager.PluginUpdate? UpdateOf(PluginManager.Plugin p)
        => _pluginUpdates.TryGetValue(p.Name, out var u) ? u : null;

    /// <summary>
    /// 一条插件更新的命令参数（三条更新路径共用的唯一入口，见 <see cref="PluginManager.BuildUpdateArgs"/>）：
    /// npm 源则用 <c>包名@版本号</c>；git 源则用清单里的来源 spec（<c>git+…</c> / <c>github:o/r</c>）；
    /// 给不出可靠目标（来源认不出 / 目标位上是显示标签又纠不出真版本），即空串，调用方不许动手。
    ///
    /// 现场 bug：git 源插件的更新被拼成 <c>dsh-codearts-auth@仓库最新</c>（把界面标签当版本号），即
    ///   pnpm 必报 <c>Failed to resolve dependency tree: "dsh-codearts-auth@仓库最新" isn't supported…</c>。
    ///   这条链路（含 <c>BuildAddSourceArgs</c>）现在只有一个出口，不可能再各写各的。
    /// </summary>
    private static string UpdateArgsFor(PluginManager.Plugin p, PluginManager.PluginUpdate u)
    {
        string spec = PluginManager.DepSpec(p.Name);
        string args = PluginManager.BuildUpdateArgs(p.Name, spec, u.Latest);
        if (args.Length == 0)
            Logger.NoteDiagnosis($"更新 {p.Name}：给不出可靠的目标（来源「{spec}」、目标位「{u.Latest}」）⇒ 未执行命令");
        return args;
    }

    /// <summary>
    /// 安装 / 更新 / 回滚重装开始前的半截安装自愈（第三件事：单一入口，批量路径也走这里）。
    /// 目标包是「目录在、package.json 缺」的半截态，即 pnpm 会报「目录已存在」拒绝安装、
    /// 界面又按 package.json 判"未安装"，即用户反复点安装反复失败（死循环）。
    /// 这里先清掉残留目录再放行安装；越界 / 白名单拒绝，即返回 false，调用方中止并如实提示。
    /// </summary>
    /// <returns>true = 可以继续安装（未发现残留，或已清理成功）；false = 必须中止（越界或清理失败）。</returns>
    private static bool EnsureNotBrokenInstall(string packageName, out string userNote)
    {
        userNote = "";
        var state = PluginManager.EvaluateInstallState(packageName);
        if (state != PluginManager.InstallStateKind.Broken) return true;

        var clean = PluginManager.CleanBrokenInstall(packageName);
        if (clean.Rejected)
        {
            userNote = "无法确定安全的清理范围，本次未执行安装。请把日志发给作者。";
            Logger.NoteDiagnosis($"半截安装清理被拒绝（{packageName}）：{clean.Reason}");
            return false;
        }
        if (clean.Attempted && clean.Cleared)
        {
            // 中性提示：检测到残留、已清理后继续（不出现任何命令写法）
            userNote = "检测到上次安装残留，已清理后重新安装";
            AddEventStatic(userNote + "：" + packageName, DSHGuard.MainWindow.EventKind.Warn);
            return true;
        }
        if (clean.Attempted && !clean.Cleared)
        {
            userNote = "检测到上次安装的残留文件无法清理（可能被占用）。建议先停止 DSH 引擎，再重试安装。";
            return false;
        }
        // 目录不在 / 不是半截态，即照常继续
        return true;
    }

    /// <summary>静态转发 AddEvent（PluginManager 无 UI 上下文；MainWindow 的实例方法不可达）。</summary>
    private static void AddEventStatic(string text, DSHGuard.MainWindow.EventKind kind)
        => DSHGuard.MainWindow.AddEvent(text, kind);

    // ══════════════ 写盘失败 / 日志不可用的用户可见口径（接线用） ══════════════
    // 背景：底层已把三类"其实没写成功"暴露成只读状态 ——
    //   VersionMemory.LastSaveFailed（含 StateUnreliable 导致的主动拒写）、
    //   SettingsManager.LastSaveFailed、Logger.UnavailableReason；
    //   而本程序里 Logger.Log / LogDiagnosis 是空实现，所以在接线前，界面会在写盘失败时
    //   照报「已固定 DSH X」「已保存并立即生效」「详细输出已记入日志」——用户重启后才发现没生效、
    //   或者去「日志」页什么都找不到。
    //
    // 口径纪律（与全项目一致）：界面上只说中性结论与后果，**不拼底层原因原文** ——
    //   那些原因里可能带盘符路径（IO / 权限异常的 Message 就带路径）与异常类型名等内部标识；
    //   完整原因由底层各自的 NoteDiagnosis / LogError 落盘，排查时看日志与诊断包即可。
    //   因此下面三处都只读"失败 / 不可用"这个事实本身，从不引用 *Reason / *Error / UnavailableReason 的文本。

    /// <summary>设置写盘失败时的用户可见口径（中性、不含路径与内部标识）。</summary>
    private const string SettingsFailText = "保存失败：这次改动没有写入配置文件，重启后会回到原来的设置。";

    /// <summary>
    /// 版本记忆写入结果的用户可见口径。<paramref name="okText"/> 是**成功时的原话**，
    /// 成功路径一字不改地返回（等级仍是 <see cref="EventKind.Info"/>）。
    /// </summary>
    private static (string Text, EventKind Kind) VersionMemoryOutcome(string okText)
    {
        if (!VersionMemory.LastSaveFailed) return (okText, EventKind.Info);
        return VersionMemory.StateUnreliable
            // 读盘失败 ⇒ 底层主动拒写（避免用空状态覆盖掉用户的固定版本与履历），如实说明这一档
            ? ("未能写入版本记录：本次读不出已有的记录，为避免清空你固定的版本与履历，已跳过写入；重启后这里的选择不会保留。",
               EventKind.Warn)
            : ("未能写入版本记录：这次没有存上，重启后这里的选择不会保留。", EventKind.Warn);
    }

    /// <summary>
    /// 设置保存结果的用户可见口径。<paramref name="okText"/> 是**成功时的原话**，
    /// 成功路径一字不改地返回（等级仍是 <see cref="EventKind.Info"/>）。
    /// </summary>
    private (string Text, EventKind Kind) SettingsOutcome(string okText)
        => _settings.LastSaveFailed ? (SettingsFailText, EventKind.Warn) : (okText, EventKind.Info);

    /// <summary>
    /// 「详细输出 / 原因已记入日志，可在「日志」页查看。」的可兑现版本：
    /// 日志目录不可写时（<see cref="Logger.UnavailableReason"/> 非空）在后面追加一句实话，
    /// 免得用户翻遍「日志」页什么都找不到。可用时返回原话，一字不改。
    /// </summary>
    private static string LogPromise(string promise)
        => Logger.UnavailableReason.Length == 0
            ? promise
            : promise + "（日志目录当前不可写，这次没能记入日志）";


    /// <summary>
    /// 「有新版」的唯一计数来源（与「一键更新 N 个」按钮同一个集合、同一个表达式）。
    ///
    /// 现场：事件栏说 4 个待更新、按钮与底部统计说 5 个 —— 差的正是那个 git 源插件
    /// （git 源在更新报告里查不到 npm 版本，是后来按"跟仓库最新"补进合并表的）。
    /// 只要有一处去数"更新报告原文"，数字就必然对不上；所以计数一律走这里。
    ///
    /// 2026-09-18 起排除「可选升级」（<see cref="PluginManager.PluginUpdate.Advisory"/>）：
    ///   用户要求「如果是分支版本的有另一种提示方法，可选升级」—— 跟具名分支/被移动标签的插件
    ///   仍然会在卡片上提示出来（文案含「可选」），但不进这个计数，
    ///   因此也不会被「一键更新」或「只看有新版」一并升掉；默认分支（master）照旧计入（用户：「肯定要报」）。
    /// </summary>
    private int UpdatableCount() => CountUpdatablePlugins(_plugins, UpdateOf);

    /// <summary>
    /// 单个插件是否"有新版"（不含可选升级）——「一键更新」与「只看有新版」用它。
    /// 与 <see cref="UpdatableCount"/> 同一判定（同一表达式），三处口径永远一致。
    /// </summary>
    private bool IsUpdatable(PluginManager.Plugin p) => IsHardUpdatable(UpdateOf(p));

    /// <summary>
    /// 上式的纯函数形态（便于自检：同一集合 + 同一表达式）。
    /// 「可选升级」不计入 —— 见 <see cref="IsHardUpdatable"/>。
    /// </summary>
    internal static int CountUpdatablePlugins(
        IEnumerable<PluginManager.Plugin>? plugins,
        Func<PluginManager.Plugin, PluginManager.PluginUpdate?> updateOf)
        => (plugins ?? Enumerable.Empty<PluginManager.Plugin>())
           .Count(p => IsHardUpdatable(updateOf(p)));

    /// <summary>
    /// 这条更新记录算不算硬更新（要计入「一键更新 N 个」、要进「只看有新版」筛选）。
    ///
    /// 判据只有一条：<c>HasUpdate</c> 为真且不是 <see cref="PluginManager.PluginUpdate.Advisory"/>。
    /// 可选升级（具名分支 / 标签被移动）由用户自己决定，绝不被批量动作一并升掉；
    /// 但卡片上仍有「更新到最新提交」按钮，想升随时能升（入口在，只是不替他做主）。
    /// </summary>
    internal static bool IsHardUpdatable(PluginManager.PluginUpdate? u)
        => u != null && u.HasUpdate && !u.Advisory;

    /// <summary>更新事件文案（纯函数，便于自检）：数字必须来自 <see cref="UpdatableCount"/>，不许另算一份。</summary>
    internal static string PluginUpdateEventText(int count) => $"发现 {count} 个插件有新版本（插件页可更新）";

    /// <summary>
    /// 把已经读到的"包名 → loader id"贴回当前这批插件对象上（唯一入口），并**重算「是否已禁用」**。
    ///
    /// <c>PluginManager.Scan</c> 每次都 new 一批新对象（<c>LoaderId</c> 为 null），而 id 表为另外
    /// 异步读取，即凡 <c>_plugins</c> 被整体替换之处，替换后均必须执行本方法一次。
    /// 遗漏一处，该批插件将全部"没有内部标识"，点击禁用只会提示"无法读取内部标识"。
    /// 只写入不清除（id 表可能是 dump 失败时的缓存兜底数据，用它清空会破坏仍可用的数据）。
    ///
    /// ★ 贴完 id 还必须重算「是否已禁用」（见方法体最后一行）。理由：<c>Scan</c> 算 <c>Disabled</c>
    /// 的那一刻手里只有包名（<c>LoaderId</c> 还是 null），而禁用记录是按 **loader id** 写的
    /// —— 本机现场：包 <c>dsh-univer-office</c> 的记录是 <c>- id: "univer"</c>（包名与 id 不同）。
    /// 只按包名匹配永远匹不中，重扫出来的整批对象 <c>Disabled</c> 就全是 false，界面于是显示
    /// 「禁用插件」+「已启用 16 个插件」——数据其实已经写进配置文件了（现场缺陷：批量禁用后界面不更新，
    /// 只有重启才被 <see cref="LoadLoaderIdsAsync"/> 里的引擎视图纠正）。
    /// 这里用的口径与 <c>Scan</c> 完全一致（都只认 patch 记录），差别只是此刻 id 已知，按 id 写的记录认得出来；
    /// 拿到引擎视图的那一次（本方法之外的 <c>LoadLoaderIdsAsync</c>）照旧以引擎为准、覆盖这里的结果。
    /// </summary>
    private int BackfillLoaderIds()
    {
        int attached = PluginManager.ApplyLoaderIds(_plugins, _loaderIds);
        // id 到手后立刻按 patch 记录（id 与包名都认）重算一次禁用标记：这是本方法存在的第二个理由。
        try { PluginManager.RefreshDisabledFlags(_plugins); }
        catch (Exception ex) { Logger.LogError("RefreshDisabledFlags", ex); }
        return attached;
    }

    /// <summary>自检用：按真实链路把 id 表贴到当前插件表上（验证"重扫后仍能禁用"）。</summary>
    internal int BackfillLoaderIdsForTest() => BackfillLoaderIds();

    /// <summary>自检用：当前插件表里有多少个已经拿到内部标识。</summary>
    internal int LoaderIdCountForTest() => _plugins.Count(p => !string.IsNullOrWhiteSpace(p.LoaderId));

    /// <summary>自检用：把插件表与更新表换成本次断言用的样本，返回还原用的收尾动作。</summary>
    internal Action SeedPluginStateForTest(List<PluginManager.Plugin> plugins,
                                           Dictionary<string, PluginManager.PluginUpdate> updates)
    {
        var oldPlugins = _plugins;
        var oldUpdates = new Dictionary<string, PluginManager.PluginUpdate>(_pluginUpdates, StringComparer.OrdinalIgnoreCase);
        _plugins = plugins;
        _pluginUpdates.Clear();
        foreach (var kv in updates) _pluginUpdates[kv.Key] = kv.Value;
        return () =>
        {
            _plugins = oldPlugins;
            _pluginUpdates.Clear();
            foreach (var kv in oldUpdates) _pluginUpdates[kv.Key] = kv.Value;
        };
    }

    /// <summary>自检用：重渲染插件页（摘要行 / 一键更新按钮文字都在这条链上）。</summary>
    internal void RenderPluginsForTest() => RenderPlugins();

    /// <summary>自检用：作者主页的推断结果（纯函数，不起窗口）。</summary>
    internal static string AuthorProfileUrlForTest(PluginManager.Plugin p) => AuthorProfileUrl(p);

    /// <summary>自检用：插件页底部统计那一行的原文。</summary>
    internal string PluginsSummaryTextForTest() => PluginsSummaryText?.Text ?? "";

    /// <summary>自检用：真正会写进「事件信息」的那句话（数字与 <see cref="UpdatableCount"/> 同源）。</summary>
    internal string UpdateEventTextForTest() => PluginUpdateEventText(UpdatableCount());

    /// <summary>
    /// 自检用：「查新版本」这一轮异步检查是不是还在跑。
    ///
    /// 为什么要暴露它：摘要行「正在查新版本…」与「一键更新」按钮的显隐都由这同一个标志驱动
    /// （见 <c>RenderPlugins</c>：`bool hasUpdates = UpdatableCount() &gt; 0;` 与 <c>_updatesChecking</c>），
    /// 而检查结果是异步到达的 —— 报告刚落定、重渲染还没跑完时去读按钮显隐，就会读到上一帧的旧值，
    /// 断言于是偶发变红（自检里那条「一键更新只在有可更新插件时显示」就是这么飘的，与功能无关）。
    /// 有了它，断言前就能真的等到落定，而不是靠 sleep 猜时间。
    /// </summary>
    internal bool UpdatesCheckingForTest() => _updatesChecking;

    /// <summary>获取包名到 loader id 的映射（写入 disabled 时需要 id）。</summary>
    private async Task LoadLoaderIdsAsync()
    {
        try
        {
            // 取 id 必须用当前版本（不是 @latest），并且要显式指定工作目录与超时：
            // 本机实测在主目录下执行可用（741 行、映射齐全），换目录就可能失败，即失败即静默失效（现场根因）。
            string spec = VersionMemory.Spec;
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string dumpArgs = $"--yes @deepseek-ai/dsh@{spec} --profile web --dump-config";

            var (ok, output) = await RunCommandAsync("npx", dumpArgs, home, timeoutMs: 180000);
            bool haveIds = HasLoaderIds(ok, output);

            // ① 识别「清单已登记、机器上未安装」这一残局：它会导致 dump 命令整体失败
            //    （退出码 1、stdout 为空、stderr 输出 cannot resolve profile bundle "包名"），
            //    此时无法读取任何 id，即界面退化为"所有插件都无法禁用"（现场根因）。
            string unresolved = "";
            if (!haveIds)
            {
                LogDumpFailure("读取插件标识失败", spec, home, output);
                unresolved = ParseUnresolvedBundleFrom(output);
            }

            if (unresolved.Length > 0)
            {
                // ② 自愈一次（顺序固定、只试一次）：按 dsh 自己的提示把清单里的插件补齐。
                //    超时给足 15 分钟：这一步是真的在装包，可能很慢。
                // ★ 缺 Git 闸门（唯一入口）：这条自愈是**按清单整份 install**，而这个缺的包在清单里
                //   声明的正是 git 来源 ⇒ 本机没有 git 时就必然要调系统的 git（现场表现是一句英文
                //   spawn git）。拦下即不跑命令、如实说明缺什么；下面的第 ③ 步照旧执行 ——
                //   补不上就照旧降级到缓存兜底并如实提示，绝不假装已经修好。
                if (BlockedForMissingGit(unresolved, PluginManager.DepSpec(unresolved), "自动补装"))
                {
                    AddEvent(GitMissingEventText(unresolved), EventKind.Bad);
                }
                else
                {
                    AddEvent($"插件清单中登记了 {unresolved}，但本机未安装；正在自动重新安装…", EventKind.Warn);
                    var (okInstall, outInstall) = await RunCommandAsync("npx",
                        PluginManager.BuildInstallAllArgs(), home, timeoutMs: 900000, relaxSupplyChainPolicy: true);
                    LogDumpFailure(okInstall ? "自动补装已完成（输出留痕）" : "自动补装失败", spec, home, outInstall);
                }

                // ③ 重跑一次 dump：成功则回到正常流程（记一条 Good 事件）；失败则不再重试，走缓存兜底
                var (okAgain, outAgain) = await RunCommandAsync("npx", dumpArgs, home, timeoutMs: 180000);
                haveIds = HasLoaderIds(okAgain, outAgain);
                if (haveIds)
                {
                    ok = true;
                    output = outAgain;
                    AddEvent($"重新安装后已能读取插件标识（{unresolved} 已安装）", EventKind.Good);
                }
                else
                {
                    LogDumpFailure("补装后重读插件标识仍失败", spec, home, outAgain);
                    // 若补装后仍缺少其它包（清单中缺失的不止一个），按最新识别出的包名向用户报告
                    string againMiss = ParseUnresolvedBundleFrom(outAgain);
                    if (againMiss.Length > 0) unresolved = againMiss;
                    AddEvent($"仍无法读取插件标识（清单中的 {unresolved}）；本次不再重试", EventKind.Warn);
                }
            }
            else if (!haveIds)
            {
                // 不是"清单里有、磁盘上没有"那种崩法，即保留原有的一次 @latest 回退
                // （现场有过版本说明符失灵的情形；与本条自愈互不干扰）
                var (ok2, output2) = await RunCommandAsync("npx",
                    "--yes @deepseek-ai/dsh@latest --profile web --dump-config", home, timeoutMs: 180000);
                if (HasLoaderIds(ok2, output2)) { ok = true; output = output2; spec += "（回退 latest）"; }
                else LogDumpFailure("回退 latest 仍失败", "latest", home, output2);
            }

            // ④ 用 dump 还是用缓存：判定抽成纯函数（自检盯着这条，不许回归）
            var (ids, fromCache, byNameCount) = PluginManager.ResolveLoaderIds(ok, output, PluginManager.LoadLoaderIdCache());

            // ④′ 「哪些插件此刻真的被禁用」也在同一次 dump 输出里（每个条目带 name:，
            //     被禁用的带 disabled: true），即一次解析出两个集合，不必再跑第二条命令。
            //     haveIds=false（dump 跑失败 / 输出不是引擎视图），即返回 null，即降级到 patch 记录。
            //     这一步是本轮修的那个 bug 的关键：网页/插件市场那侧用的是它自己的开关、
            //     不写 cordis.patch.yml，所以只看 patch 就会出现"网页已启用、壳里还显示禁用"。
            var engineDisabled = PluginManager.ParseDisabledIds(haveIds ? output : null);
            bool engineView = engineDisabled != null;
            var patchDisabled = PluginManager.PatchDisabledIds();

            if (fromCache)
            {
                // 都失败，即用上次成功时的缓存顶着（并在界面说明），绝不静默变成"全都禁用不了"
                _loaderIds = ids;
                BackfillLoaderIds();
                _loaderIdsLoaded = true;
                PluginManager.NoteUnresolvedBundle(unresolved, ids.Count);   // 让「禁用」的弹窗能说出是哪个包
                // 拿不到引擎视图，即保留原有的 patch 记录口径（降级），并在事件栏如实注明可能与引擎不一致
                PluginManager.ApplyDisabledFlagsFromEngine(_plugins, engineDisabled, patchDisabled);
                RenderPlugins();
                AddEvent((unresolved.Length > 0
                    ? $"插件清单中登记了 {unresolved}，但本机未安装；已沿用上次缓存的 {ids.Count} 条映射记录"
                    : $"读取插件标识失败；已沿用上次缓存的 {ids.Count} 条映射记录（禁用/启用仍可用）")
                    + "；" + PluginManager.DisabledFallbackNote(engineView),
                    EventKind.Warn);
                return;
            }

            if (ids.Count == 0)
            {
                PluginManager.NoteUnresolvedBundle(unresolved);
                AddEvent(unresolved.Length > 0
                    ? $"插件清单中登记了 {unresolved}，但本机未安装；暂时无法读取插件标识，无法禁用该插件（可在「寻找插件」中重新安装后再试）"
                    : "读取插件标识失败，暂时无法禁用/启用插件：请点「刷新」重试（详情见日志）",
                    EventKind.Bad);
                return;
            }

            _loaderIds = ids;
            // 读取完整，即上一次的缺包提示作废（包名与"使用了几条缓存作为兜底"两个字段一并清空）。
            // 只清一半会让下一次的禁用弹窗拿旧包名说话（现场：dump 退出码 0 已读全，弹窗还在报缺包）。
            PluginManager.ClearUnresolvedBundle();
            Logger.Log($"加载器标识：{ids.Count} 个（其中按 name 权威映射 {byNameCount} 个）");
            PluginManager.SaveLoaderIdCache(ids);
            Logger.Log($"已读取 {ids.Count} 个加载器标识（版本 {spec}）");
            _loaderIdsLoaded = true;

            // 顺序很重要：① 先把真实 loader id 贴到每个插件上 ② 再修历史坏记录
            // ③ 最后按修好的配置重算「是否已禁用」—— 徽章必须以引擎的实际状态为准，不是按钮点一下就算数。
            BackfillLoaderIds();

            // 对账要在"修历史坏记录"之前取：RepairPackageNameRecords 会把用包名写的记录改写成
            // loader id（内容没变、写法变了），拿改写后的 patch 去比会把这次改写误报成"引擎与本地不一致"。
            //
            // 只对账我们管的插件（profile 清单 dependencies 里登记的那些，见
            //   PluginManager.ManagedPluginAliases）：引擎基座自己禁用的内部条目
            //   （hmr / compaction-basic / command-compact / plan-mode / tool-bash / tool-fs /
            //    mnemon-strategy-* …）此前均被计为"引擎独有"漂移，即事件栏每次刷新都占满内部标识。
            //   清单无法读取，即白名单为空集，即本轮不执行任何比对（不使用兜底数据，避免产生误导信息）。
            var managed = PluginManager.ManagedPluginAliases(_plugins);
            var drift = managed.Count > 0
                ? PluginManager.CompareEngineToPatch(engineDisabled, patchDisabled, managed)
                : PluginManager.CompareEngineToPatch(null, null);

            int repaired = await Task.Run(() => PluginManager.RepairPackageNameRecords(_plugins));
            if (repaired > 0)
                AddEvent($"已修正 {repaired} 条无效的禁用记录（原以包名书写，引擎无法识别）", EventKind.Warn);

            // DSH 自己会说"哪个条目认不出"（patch: entry X not found），即直接把无效条目清掉，零猜测
            int dropped = await Task.Run(() => PluginManager.DropMissingEntries(PluginManager.ParseMissingPatchEntries(output)));
            if (dropped > 0)
                AddEvent($"已清理 {dropped} 条引擎无法识别的补丁条目（此前已写入但未生效）", EventKind.Warn);

            // ③′ 「是否已禁用」以引擎自己的视图为唯一事实来源（拿不到才退回 patch 记录）；
            //     会把标记重设一遍 —— 既设 true 也设回 false，这正是修掉"网页启用了、壳还显示禁用"的那一下。
            bool byEngine = PluginManager.ApplyDisabledFlagsFromEngine(_plugins, engineDisabled, patchDisabled);
            RenderPlugins();
            if (!byEngine)
                AddEvent(PluginManager.DisabledFallbackNote(false), EventKind.Warn);
            else if (drift.Drifted)
            {
                // 事件栏只报大概结果（灰色 Info 档：普通提示，不用警告黄/红），不列名字；
                // 哪几个、各是什么方向走日志落盘留证（Logger.Log 是空实现，必须走 NoteDiagnosis）。
                Logger.NoteDiagnosis($"插件状态漂移已按引擎同步（{drift.AdjustedCount} 项）："
                    + PluginManager.DriftDetailText(drift));
                string driftNote = PluginManager.DriftEventText(drift.AdjustedCount);
                if (driftNote.Length > 0) AddEvent(driftNote, EventKind.Info);
            }
        }
        catch (Exception ex) { Logger.LogError("LoadLoaderIdsAsync", ex); }
    }

    /// <summary>这次 dump 算不算"读到了标识"（跑通 + 输出里确实有 id 行）。</summary>
    private static bool HasLoaderIds(bool ok, string? output)
        => ok && (output ?? "").IndexOf("- id:", StringComparison.Ordinal) >= 0;

    /// <summary>
    /// 从命令返回文本里认出"清单里有、磁盘上没有"的包名：先只看 stderr 那段（原因就在那儿），
    /// 分不出两段时（例如超时）再整段找一次。
    /// </summary>
    private static string ParseUnresolvedBundleFrom(string? combined)
    {
        var (so, se, split) = SplitCommandStreams(combined);
        string hit = PluginManager.ParseUnresolvedBundle(split ? se : so);
        return hit.Length > 0 ? hit : PluginManager.ParseUnresolvedBundle(combined);
    }

    /// <summary>
    /// 把 RunCommandAsync 的返回文本切成 stdout / stderr 两段。
    /// 失败时它给的是合并流：stdout + 「错误输出：」+ stderr + 退出码（见 RunCommandAsync）；
    /// 超时那条没有分隔标记，即 Split=false，调用方按"整段"记，绝不假装 stderr 是空的。
    /// </summary>
    private static (string Stdout, string Stderr, bool Split) SplitCommandStreams(string? combined)
    {
        string s = combined ?? "";
        const string marker = "\n\n错误输出：\n";
        int at = s.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return (s, "", false);

        string head = s.Substring(0, at);
        string tail = s.Substring(at + marker.Length);
        int code = tail.LastIndexOf("\n\n（退出码", StringComparison.Ordinal);   // 去掉尾部那行退出码
        if (code >= 0) tail = tail.Substring(0, code);
        return (head, tail, true);
    }

    /// <summary>
    /// 取文本尾部（日志只留最后一段，不把整份 dump 灌进去）。
    /// 注：<c>LogDumpFailure</c> 已不再用它（改用 <c>Logger.TruncateCommandOutput</c> 的头尾限量，
    /// 那里只保尾、无行数上限）；下列调用点仍在用，故保留本方法不删。
    /// </summary>
    private static string Tail(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Trim();
        return s.Length > max ? "…" + s.Substring(s.Length - max) : s;
    }

    /// <summary>
    /// 把"读插件标识失败"的现场证据落下来：命令行 + 工作目录 + stdout 与 stderr 各自的尾部。
    /// 必须走 NoteDiagnosis（诊断留痕，写进异常日志、不弹窗、不当异常）——
    /// Logger.Log 按现行日志策略是空实现，只写它等于把失败原因丢掉（这正是这次要修的第 3 件事）。
    /// 只记包名/路径这类排查必需信息，不打印任何凭据。
    /// </summary>
    private static void LogDumpFailure(string what, string spec, string workDir, string? combined)
    {
        // 成功路径不留日志：「补装已完成」这类收尾同样会把整段 npx 输出抄进日志，
        // 那正是用户报告的「内容特别多」。真失败仍必须留证（自检引用的失败现场靠它）。
        if (!DumpFailureShouldPersist(what)) return;

        var (so, se, split) = SplitCommandStreams(combined);
        // 换用 Logger 的统一限量（每流 60 行 / 4000 字符，头尾各留一段）：
        // 原先的 Tail(…, 300) 只保尾、且无行数上限，dump 的几百行会整段进日志。
        string soTail = Logger.TruncateCommandOutput(so);
        string seTail = Logger.TruncateCommandOutput(se);
        string detail = split
            ? $"stdout 头尾：{(soTail.Length > 0 ? soTail : "(空)")}\n  stderr 头尾：{(seTail.Length > 0 ? seTail : "(空)")}"
            : $"输出头尾（stdout/stderr 未拆分）：{(soTail.Length > 0 ? soTail : "(空)")}";
        // Logger.Log 是空实现（写入不生效），留证只能走 NoteDiagnosis
        Logger.NoteDiagnosis($"{what}（{spec}，工作目录 {workDir}）\n  {detail}");
    }

    /// <summary>
    /// 「读插件标识」这条链路上，哪些 <paramref name="what"/> 算失败、需要落盘：
    /// 只有真失败才留证据；「自动补装已完成」「启动前补装已完成」是成功收尾，不落盘。
    /// 纯函数，便于自检直接断言。
    /// </summary>
    internal static bool DumpFailureShouldPersist(string what)
        => what is not ("自动补装已完成（输出留痕）" or "启动前补装已完成（输出留痕）");

    /// <summary>
    /// 先按当前主题为一批新控件着色，再挂到面板上。
    /// 卡片与列表行按深色主题配色创建，而 ApplyThemeSoon 要等下一帧 Dispatcher 才执行，
    /// 直接上屏会在日间模式下先按深色显示一帧再变亮，快照页则会出现白字配白底的中间态。
    /// </summary>
    private static void SwapThemed(Panel host, IEnumerable<UIElement> items)
    {
        var staged = new List<UIElement>();
        foreach (var it in items)
        {
            if (it is DependencyObject d)
            {
                ThemeManager.Apply(d, ThemeManager.IsDark);
                ButtonFx.Wire(d);       // 动态内容在这里统一挂动效（快照/插件/市场都走这条路）
            }
            staged.Add(it);
        }
        host.Children.Clear();
        foreach (var it in staged) host.Children.Add(it);
    }

    private void RenderPlugins()
    {
        if (PluginsPanel == null) return;
        try
        {
            string filter = (PluginSearchBox?.Text ?? "").Trim();

            var list = _plugins.Where(p =>
                    filter.Length == 0 ||
                    p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    p.Author.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // 先搜索、再排序：筛选已整体移除（它与搜索、批量勾选功能重合），这里只剩"搜索 + 排序"两件事。
            SortInstalledPlugins(list);

            if (list.Count == 0)
            {
                _installedRenderOrder = new List<string>();
                SwapThemed(PluginsPanel, new UIElement[]
                {
                    SimpleText(_plugins.Count == 0 ? "（尚未安装插件）" : "（没有匹配的插件，换个关键词或清空搜索试试）",
                        12, Color.FromRgb(0x8E, 0x8E, 0x93))
                });
                if (PluginsSummaryText != null)
                    PluginsSummaryText.Text = $"已安装 {_plugins.Count} 个插件，当前搜索下 0 个";
                return;
            }

            // 自检据此验算排序（见 InstalledRenderOrderForTest 与 InstalledSortOrderForTest）
            _installedRenderOrder = list.Select(p => p.Name).ToList();

            // 一次性替换为新卡片（先按当前主题着色，避免逐张上屏导致闪烁）
            SwapThemed(PluginsPanel, list.Select(p => (UIElement)BuildPluginCard(p)));

            int okCount = _plugins.Count(p => p.Compatibility == PluginManager.Compat.Ok);
            int partial = _plugins.Count(p => p.Compatibility == PluginManager.Compat.Partial);
            int broken = _plugins.Count(p => p.Compatibility == PluginManager.Compat.Broken);
            int unknown = _plugins.Count(p => p.Compatibility == PluginManager.Compat.Unknown);
            int dis = _plugins.Count(p => p.Disabled);
            int updatable = UpdatableCount();       // 「有新版」的唯一口径（见 UpdatableCount）

            // 查新状态里**只有**基础段没说过的那几种才追加：
            //   · 正在查新版本… / 连不上下载来源 / 没查到更新信息 —— 查新侧的新信息，基础段没有；
            //   · 全部都是最新的 —— 基础段只在"可更新 > 0"时才写一段，没写就等于"一个可更新的都没有"，
            //     这一句是**把那个沉默补成明文**，仍是新信息；
            //   · "有 N 个可以更新" —— 删掉：summaryParts 里 `{updatableNow} 个插件可更新` 已经说过这件事，
            //     同一个数字同一件事在这行末尾说第二遍，现场上屏就是
            //     「……，1 个插件可更新 · 有 1 个可以更新」。口径没变（可更新数仍由 UpdatableCount() 唯一给出，
            //     仍由基础段承载），少的只是重复。这里保持空串继续往下拼（不把 updNote 拆掉：
            //     它仍是这一行末尾唯一的状态出口，上面的"正在查新…"就靠它上屏）。
            string updNote;
            if (_updatesChecking) updNote = " · 正在查新版本…";
            else if (_pluginUpdates.Count == 0) updNote = _updatesError.Length > 0 ? $" · {_updatesError}" : "";
            else updNote = updatable > 0 ? "" : " · 全部都是最新的";

            // 底部统计按"人话、有才写"来：
            //   已安装 N 个插件，已启用 x 个插件，已禁用 x 个插件，x 个插件可更新
            // 后三项为 0 时不显示（不输出"已启用 0 个"这类无信息量的内容）。
            int enabledNow = _plugins.Count(p => !p.Disabled);
            // 底部统计与「一键更新 N 个」按钮、事件栏同一个来源：
            // 原先这里数的是 _pluginUpdates.Values（合并表本身），表里有、插件表里没有的条目会把它顶高一位
            // 因此现场表现是"底部说 5、事件说 4"。只统一口径，展示位置与文案不动。
            int updatableNow = updatable;
            var summaryParts = new List<string> { $"已安装 {_plugins.Count} 个插件" };
            if (enabledNow > 0) summaryParts.Add($"已启用 {enabledNow} 个插件");
            if (dis > 0) summaryParts.Add($"已禁用 {dis} 个插件");
            if (updatableNow > 0) summaryParts.Add($"{updatableNow} 个插件可更新");
            _pluginsSummaryBase = string.Join("，", summaryParts);
            if (!_loaderIdsLoaded) _pluginsSummaryBase += "（部分插件信息未读全，「禁用插件」暂时不能用）";
            // 查新状态（正在查 / 查到什么 / 没连上）接到这一行末尾 —— 上面那段 updNote 以前算完就没人看，
            // 于是「正在查新版本…」永远不上屏；而同页其它提示走的就是这一个控件（见 RefreshPluginsSummaryWithSelection）。
            // 排在最后：先讲"装了什么"，再讲"查新查到哪一步"。空串照样拼（不显示的空串拼上去等于没提），
            // 即检查中/查完/没连上三种状态都走同一条路，不另设显隐开关。
            _pluginsSummaryBase += updNote;
            RefreshPluginsSummaryWithSelection();   // 末尾按需带上"已选中 x 个插件"

            // 「一键更新」按钮文字：没有可更新项时按钮整颗不显示（显隐规则见 ApplyBatchToolbarVisibility）
            if (UpdateAllText != null && UpdateAllText.Parent is Border host)
            {
                host.Opacity = 1.0;
                UpdateAllText.Text = $"一键更新 {updatable} 个";
            }
            ApplyThemeSoon();   // 新卡片要补刷主题

            ApplyBatchToolbarVisibility();   // 顶部这一行谁显谁隐：唯一一份规则
            UpdateBatchBar();   // 刷新勾选计数与显隐（末尾同样会同步一次外层显隐）
        }
        catch (Exception ex) { Logger.LogError("RenderPlugins", ex); }
    }

    /// <summary>
    /// 插件页顶部那一行（<c>PluginToolbar</c> 的 <c>Grid.Column=2</c>）谁显谁隐全是这一份规则：
    ///
    ///   · 批量功能框（<c>BatchBarHost</c>）—— 有选中就出现，没选中就收起；
    ///   · 「一键更新」（<c>UpdateAllBtn</c>）—— 有可更新项 且没有选中 才出现（选中时让位给功能框）。
    ///
    /// 以前这段规则在 RenderPlugins / RenderVersionView 里各写了一份（还有一份老写法会把
    /// 「一键更新」硬拉回 Visible），谁后跑谁说了算 —— 现场表现就是「选了卡片，一键更新在、
    /// 功能框不在」。现在收敛到这里；更关键的是 <c>UpdateBatchBar()</c> 末尾也会调它，
    /// 所以点卡片 / 全选 / 反选 / 清空 / 动作收尾都能立刻同步外层显隐（点卡片不再重建列表，
    /// 原来没人更新外层，即外层永远 Collapsed，即功能框永远不出现，这是本 bug 的根治点）。
    ///
    /// 只碰这两个元素的 Visibility，别的一律不动；每次都重算，重复调用无副作用。
    /// </summary>
    internal void ApplyBatchToolbarVisibility()
    {
        try
        {
            bool choosing = _batchSelected.Count > 0;

            // 批量功能框：先备好内容（紧凑下拉 + 弹层菜单，只有没有内容时才构建一次）
            if (BatchBarHost != null)
            {
                if (BatchBarHost.Content == null)
                {
                    BatchBarHost.Content = BuildBatchBar();

                    // 功能框的落脚处是个 ContentControl（Control 默认 可聚焦）：
                    // 一旦它拿到焦点，WPF 会沿外框补一圈点状焦点虚线 —— 现场看到的
                    // "批量框有的边是虚线" 就是它画的（Border / StackPanel / TextBlock
                    // 本身都不可聚焦，这颗框的可聚焦元素只有这个落脚处）。
                    // 这里连落脚处一起关掉焦点视觉：只影响这一格，别处一律不碰。
                    BatchBarHost.Focusable = false;          // 不能聚焦，即任何主题下都不会画焦点框
                    BatchBarHost.FocusVisualStyle = null;    // 双保险：显式置空焦点视觉样式

                    ApplyThemeSoon();
                }
                BatchBarHost.Visibility = choosing ? Visibility.Visible : Visibility.Collapsed;
            }

            // 「一键更新」：与统计口径用同一个表达式（UpdatableCount），避免两边不一致
            if (UpdateAllBtn != null)
            {
                bool hasUpdates = UpdatableCount() > 0;
                UpdateAllBtn.Visibility = (!choosing && hasUpdates) ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        catch (Exception ex) { Logger.LogError("ApplyBatchToolbarVisibility", ex); }
    }

    /// <summary>底部统计的"基底"（不含选中数）；选中变化时只需在它后面追一句。</summary>
    private string _pluginsSummaryBase = "";

    /// <summary>
    /// 刷新底部统计：基底 + （有选中时）「已选中 x 个插件」。
    /// 选中数是实时的——点一下卡片就会变，所以批量条每次刷新都会调它。
    /// </summary>
    internal void RefreshPluginsSummaryWithSelection()
    {
        try
        {
            if (PluginsSummaryText == null) return;
            string extra = _batchSelected.Count > 0 ? $"，已选中 {_batchSelected.Count} 个插件" : "";
            PluginsSummaryText.Text = _pluginsSummaryBase + extra;
        }
        catch (Exception ex) { Logger.LogError("RefreshPluginsSummaryWithSelection", ex); }
    }
    /// <summary>兼容性四色：绿 = 完全兼容 / 橙 = 可用但非作者优先版本 / 灰 = 未声明 / 红 = 不兼容。</summary>
    private static Color CompatColor(PluginManager.Compat c) => c switch
    {
        PluginManager.Compat.Ok => Color.FromRgb(0x34, 0xC7, 0x59),
        PluginManager.Compat.Partial => Color.FromRgb(0xFF, 0x9F, 0x0A),
        PluginManager.Compat.Broken => Color.FromRgb(0xFF, 0x3B, 0x30),
        _ => Color.FromRgb(0x8E, 0x8E, 0x93)
    };

    // ══════════════ 版本号的显示口径（卡片右侧那个「v0.1.0」） ══════════════
    /// <summary>
    /// 版本查不到时数据层写的占位文本：<c>PluginManager.Scan</c> 在插件目录下找不到
    /// <c>package.json</c> 时，把 <c>Version</c> 直接写成这个字面量
    /// （PluginManager.cs 第 227 行：<c>else p.Version = "(未安装)";</c>，半角括号）。
    ///
    /// 它不是一个版本号，因此显示时不得再添加「v」前缀（否则将产生「v(未安装)」这类无意义文本）。
    /// 这里只拿它做显示层判断，不参与任何业务判定。
    /// </summary>
    private const string VersionPlaceholder = "(未安装)";

    /// <summary>占位文本在界面上的写法：去掉括号，看着像一句说明，而不是一个"版本值"。</summary>
    private const string VersionMissingLabel = "未安装";

    /// <summary>
    /// 这个版本值是不是「装过但没装成功」的占位文本。
    ///   · 判定与数据层同一个字面量（全角 / 半角括号都认，将来改了括号写法也不会漏判）；
    ///   · 空字符串也算 —— 那同样是"拿不到版本"，显示成光秃秃一个「v」没有意义；
    ///   · 「?」（package.json 在、只是未填写 version 字段，即 <c>Plugin.Version</c> 的默认值）
    ///     不算占位：插件确实装着，只是版本未知，照旧显示「v?」，不改原口径。
    /// </summary>
    private static bool IsVersionPlaceholder(string? version)
    {
        string v = (version ?? "").Trim();
        if (v.Length == 0) return true;
        return v.Replace('（', '(').Replace('）', ')') == VersionPlaceholder;
    }

    private Border BuildPluginCard(PluginManager.Plugin p)
    {
        var card = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 8)
        };
        var sp = new StackPanel();
        card.Child = sp;
        // 卡片悬停提示：内部标识（loader id）不算用户需要的信息，留在界面会被当成插件名，
        // 故这条刻意不显示（内容仍可从日志与插件页的详情里查到）。
        card.ToolTip = null;

        // 名称单独占一行并省略超长内容，避免挤压右侧的版本号与状态；版本与状态靠右
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 标题可点性有两种来源，二者互斥、判据分明：
        //   · 有网址（仓库 / 主页 / npm 包页），即点开网页，箭头 ↗，提示"打开主页"；
        //   · 本地链接插件（link: / file: / 相对路径）本就没有网址（LinkUrl 如实给空串），
        //     但它有本地目录可打开，即同样给手型与 ↗，点击改为在文件管理器中打开它的目录。
        //     现场 dsh-imagegen 的"无法识别，不能进入超链接"就是这一档：不是识别不到包，
        //     是旧实现只认网址，即没网址就"不可点"，也没告诉用户为什么没有网址。
        string link = p.LinkUrl;
        string localDir = link.Length > 0 ? "" : p.LocalDir;
        string localTip = localDir.Length > 0 ? p.LocalSourceTip : "";
        bool clickable = link.Length > 0 || localDir.Length > 0;
        var nameText = new TextBlock
        {
            // 名称按「Name - Scope」格式显示（@scope/name 显示为 Name - Scope）；原始名放在 ToolTip
            Text = PluginMarket.MarketPlugin.FormatDisplayName(p.Name) + (clickable ? " ↗" : ""),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(clickable
                ? Color.FromRgb(0x5A, 0xC8, 0xFA) : Colors.White),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            // 命中区域只给文字本身：这一列是按星号宽度压缩的星号列，TextBlock 默认水平对齐是 Stretch
            //   因此命中框撑满整列，"名字右边的空白处"也会吃 MouseLeftButtonDown、点空白就跳链接
            //   （现场反馈的就是这个）。改成 Left 后命中框收缩到文字实际宽度；名字过长时仍被这一列
            //   夹住、照旧按 CharacterEllipsis 截断。排版 / 字号 / 颜色 / 悬停效果一律不变，只改命中区域。
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 8, 0)
        };
        // 创建时间不再占用卡片版面，改放进插件名的悬停提示：卡片本身只留作者、版本与兼容性。
        // 取不到创建时间（老报告没有这一项）时整段不出现，不写"未知"。
        string createdDate = PluginTimes.FormatCnDate(UpdateOf(p)?.Created);
        string createdTip = createdDate.Length > 0 ? "创建：" + createdDate : "";

        if (clickable)
        {
            string prefix = PluginMarket.MarketPlugin.FormatDisplayName(p.Name) == p.Name ? "" : $"原始名：{p.Name}\n";
            nameText.Cursor = Cursors.Hand;
            // 两种来源的提示不共用措辞：本地插件必须把"为什么没有网址"说出来（"本地插件：来自 …"），
            // 不能复用"打开主页：<空>"或只写"作者未声明"。
            string sourceTip = prefix + (link.Length > 0 ? "打开主页：" + link : localTip);
            nameText.ToolTip = createdTip.Length > 0 ? sourceTip + "\n" + createdTip : sourceTip;
            // Tag 沿用同一根通道（字符串），由 PluginName_Click 按"是不是本地目录"分流动作；
            // 这里再多带一个标记位，免得把外部字符串的形态当成动作判据（本地目录完全可能是 http 形状的怪名字）。
            nameText.Tag = new PluginCardTarget(link, localDir);
            nameText.MouseLeftButtonDown += PluginName_Click;
            AddLinkHover(nameText);
        }
        else if (createdTip.Length > 0)
        {
            // 不可点的卡片没有可跳转地址，拼不出"打开主页"那类提示，只给创建时间即可。
            nameText.ToolTip = createdTip;
        }
        Grid.SetColumn(nameText, 0);
        head.Children.Add(nameText);

        var rightInfo = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        // 版本号：正常版本照旧写「v0.1.0」（风格不变）；数据层拿不到版本时给的是占位文本
        // 「(未安装)」，这种情况不加 v 前缀，直接显示「未安装」——
        // 否则卡片上会出现「v(未安装)」这类无意义文本（现场反馈）。
        bool versionMissing = IsVersionPlaceholder(p.Version);

        // git 源的版本位：只放已装版本号或短提交号（PluginManager.GitVersionLabel），
        //   绝不把「仓库最新」这种显示标签摆在版本位上 —— 那个位置是"版本"，摆标签既误导用户，
        //   又会被后来当成版本号拼进命令（现场：dsh-codearts-auth@仓库最新 被 pnpm 拒掉）。
        //   短提交号加一个「@」前缀表明它是提交而不是版本号。
        string gitSpec = PluginManager.DepSpec(p.Name);
        bool isGitSource = PluginSource.Classify(gitSpec) is PluginSource.Kind.GitCommit
                                                           or PluginSource.Kind.GitRef
                                                           or PluginSource.Kind.GitBare;
        string versionLabel;
        string versionTip;
        if (versionMissing)
        {
            // 三态判定（半截安装修复）：目录在、package.json 缺，即是「安装损坏」而非「未安装」——
            // 旧口径把这种残留态也显示成"未安装"，用户再点安装又被 pnpm 的「目录已存在」拒绝，即死循环。
            bool broken = PluginManager.EvaluateInstallState(p.Name) == PluginManager.InstallStateKind.Broken;
            versionLabel = broken ? "安装损坏" : VersionMissingLabel;
            versionTip = broken
                ? "目录残留，需重装修复：上次安装中断留下的残目录挡住了重装（重装时会自动清理）"
                : "清单中已登记，但本机未安装";
        }
        else if (isGitSource && !PluginManager.IsVersionComparable(p.Version))
        {
            // 版本字段为空 / 为 "?"：回退为短提交号（无法读取时如实显示「版本未知」）
            string commit = PluginManager.ReadInstalledCommit(p.Name);
            string lbl = PluginManager.GitVersionLabel("", commit);
            versionLabel = lbl == "版本未知" ? lbl : "@" + lbl;
            versionTip = lbl == "版本未知"
                ? "git 源插件：无法读取已安装版本，也无法读取提交号"
                : $"git 源插件，当前装的是提交 {lbl}（没有可比较的版本号）";
        }
        else
        {
            versionLabel = "v" + p.Version;
            versionTip = isGitSource
                ? $"git 源插件，当前装的版本是 {p.Version}"
                : "当前安装的版本";
        }

        rightInfo.Children.Add(new TextBlock
        {
            Text = versionLabel,
            FontSize = 12,
            // 悬停提示：查不到版本时把"这是什么状态"说清楚（损坏 = 目录残留；未安装 = 清单里有、机器上没有）；
            // git 源则说明"这是提交号 / 这是版本号"，不让用户把它当成版本号看
            ToolTip = versionMissing
                ? (versionLabel == "安装损坏"
                    ? "上次安装中断留下了残目录 —— 属于「安装损坏」，需重装修复。重新安装时会自动清理残留。"
                    : "该插件已在插件清单中登记，但本机未安装 —— 属于「安装未成功」的状态。")
                : versionTip,
            Foreground = new SolidColorBrush(Color.FromRgb(0x5A, 0xC8, 0xFA)),
            VerticalAlignment = VerticalAlignment.Center
        });
        rightInfo.Children.Add(new TextBlock
        {
            Text = "   " + p.StatusText,
            FontSize = 11,
            ToolTip = p.Disabled ? "这个插件已经关掉了" : "这个插件正在使用",
            Foreground = new SolidColorBrush(p.Disabled
                ? Color.FromRgb(0xFF, 0x9F, 0x0A) : Color.FromRgb(0x34, 0xC7, 0x59)),
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(rightInfo, 1);
        head.Children.Add(rightInfo);
        sp.Children.Add(head);

        var meta = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        // 作者区：头像 + 名字是一个整体的超链接，点头像或点名字都跳作者主页。
        // 名字若是仓库归属兜底来的（包内没写 author，只有仓库地址可推），必须标明这一点。
        // 头像分两种情形，判据是"这个名字的出处站点"：
        //   · 出处是 github.com，即照旧用 https://github.com/<owner>.png。那正是该仓库归属的
        //     真实头像，不是伪造，npm 包与 GitHub 上的 git 源包都走这条（不得倒退成占位）；
        //   · 出处是 gitee / gitlab / bitbucket 等非 GitHub 站点、或仓库根本认不出来，即用本地占位。
        //     拿 gitee 的 owner 去 GitHub 取同名头像，取到的很可能是另一个人的脸 —— 那才是伪造。
        string authorUrl = AuthorProfileUrl(p);
        bool fromGithub = authorUrl.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase);
        bool placeholderAvatar = p.AuthorFromRepo && !fromGithub;
        if (p.Author.Length > 0)
            meta.Children.Add(BuildAuthorLink(p.Author, authorUrl, 18, 11,
                p.AuthorFromRepo ? p.AuthorTip : null, placeholderAvatar));

        // 兼容标识只写版本号（绿 = 当前版本 / 橙 = 作者面向版本 / 红 = 需要的版本 / 灰 = 未声明），说明见悬停提示
        meta.Children.Add(new TextBlock
        {
            Text = "    " + p.CompatText,
            FontSize = 11,
            ToolTip = p.CompatDetail,
            Foreground = new SolidColorBrush(CompatColor(p.Compatibility))
        });

                var upd = UpdateOf(p);
        if (upd != null)
        {
            if (upd.HasUpdate)
            {
                // 卡片行：默认分支 / 旧口径，即「↑ 有新版 最新提交（日期）」；
                // 「可选升级」（具名分支 / 标签被移动），即「↑ 可选升级：…」——含「可选」二字是用户的硬要求。
                // 括注优先用「短号 · 日期 · 作者」（CommitNote，作者证明读的是参与者们的提交、不按作者过滤），
                // 没有就退回日期。
                string note = upd.CommitNote.Length > 0
                    ? upd.CommitNote
                    : (upd.Published.Length > 0 ? upd.Published : "");
                // 悬停优先讲"比的是谁、与发行版不同"（CompareNote，用户：「应该写明白」）；
                // 其次才是 dsh 版本要求那套（那是 npm 源的关注点）。
                // 2026-09-19 文案标准化：与 UpdateStatusHover 同一口径，这一项也带上字段名
                //   「比对基准：」（本处只补字段名，措辞仍只有 PluginSource.CompareBasisNote 一处实现）。
                string hover = upd.CompareNote.Length > 0
                    ? "比对基准：" + upd.CompareNote
                      // CommitNote 本身已经以「提交 xxx · 日期 · 作者 yyy」开头，故这里不再重复「提交」二字
                      + (upd.CommitNote.Length > 0 ? "\n远端最新：" + upd.CommitNote : "")
                      // 2026-09-19 文案标准化：指引句改正式表述（原「点插件名可打开仓库主页」的口语
                      //   「点」→「选中」）；指引本身不许丢 —— 用户要知道插件名能打开仓库主页。
                      + (upd.CommitUrl.Length > 0 ? "\n选中插件名可打开仓库主页" : "")
                      + (string.IsNullOrEmpty(upd.NewRequirement) ? "" : $"\n新版本要求 {upd.NewRequirement}（{upd.NewRequirementSource}）")
                    : (string.IsNullOrEmpty(upd.NewRequirement)
                        ? "新版本未声明 dsh 版本要求"
                        : $"新版本要求 {upd.NewRequirement}（{upd.NewRequirementSource}）");
                meta.Children.Add(new TextBlock
                {
                    // 可选升级：整句话已经带了 ref 名与短号（AdvisoryNote），不再重复括注（否则短号出现两次）；
                    // 默认分支 / 旧口径：照旧「↑ 有新版 最新提交（提交 9669ee4 · 2026-09-18 · 作者 Jet）」。
                    Text = "    ↑ " + (upd.AdvisoryNote.Length > 0
                        ? upd.AdvisoryNote
                        : "有新版 " + upd.TargetText + (note.Length > 0 ? $"（{note}）" : "")),
                    FontSize = 11,
                    ToolTip = hover,
                    Foreground = new SolidColorBrush(upd.Advisory
                        ? Color.FromRgb(0xFF, 0x9F, 0x0A)      // 可选升级：橙色，与"有新版"的蓝色一眼可分
                        : Color.FromRgb(0x5A, 0xC8, 0xFA))
                });
            }
            else if (upd.ErrorText.Length > 0)
            {
                meta.Children.Add(new TextBlock
                {
                    Text = "    " + upd.ErrorText,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93))
                });
            }
            else if (upd.StatusNote.Length > 0)
            {
                // "远端查不到 / 远端已无该 ref / 无法确认是分支还是标签"的如实呈现
                //   （现场：永远提示更新的就是"查不到"被说成了"有新版"）。
                //   这里既不写"有新版"（证明不了），也不写"已是最新"（同样证明不了）。
                meta.Children.Add(new TextBlock
                {
                    Text = "    " + upd.StatusNote,
                    FontSize = 11,
                    // 悬停的"为什么"只能取 StatusNote 里那句真原因（卡片正文同一句）：
                    //   以前在这里硬编码「网络不通 / 站点不支持 / 仓库不可访问」，而 StatusNote 现在有
                    //   三种来源（远端已无该 ref / 来源形态不支持安装与更新 / 未取到仓库最新提交）
                    //   因此出现正文写"来源地址的写法不支持"、悬停却说"网络不通"的自相矛盾（现场缺陷）。
                    ToolTip = UpdateStatusHover(upd),
                    Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93))
                });
            }
            else
            {
                // 没有新版时显示作者最近一次发版的时间（upd.Published），绿色表示"已是最新"。
                // 取不到该时间（老报告没有这一项）时只写「已是最新」，不留空括号、也不编日期。
                string publishedDate = PluginTimes.FormatCnDate(upd.Published);
                meta.Children.Add(new TextBlock
                {
                    Text = "    已是最新" + (publishedDate.Length > 0 ? "（" + publishedDate + "）" : ""),
                    FontSize = 11,
                    ToolTip = upd.CompareNote.Length > 0
                        ? "比对基准：" + upd.CompareNote
                        : (isGitSource ? "已跟到仓库最新提交（git 源按提交比对，不按版本号）" : null),
                    Foreground = new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x59))
                });
            }
        }
        sp.Children.Add(meta);

        if (!string.IsNullOrWhiteSpace(p.Description))
        {
            sp.Children.Add(new TextBlock
            {
                Text = PlainDesc(p.Description),
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                ToolTip = p.Description,          // 全文（含技术细节）放悬停里
                Foreground = new SolidColorBrush(Color.FromRgb(0xA8, 0xA8, 0xB0)),
                Margin = new Thickness(0, 6, 0, 0)
            });
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };

        // 按钮统一为「绿 / 橙 / 红 + 白字」三种：
        //   绿 = 启用、安装、启动引擎
        //   橙 = 需要留意（关闭、更新、回退、查看仓库）
        //   红 = 破坏性操作（卸载、终止引擎）
        if (p.Disabled)
        {
            var enableBtn = MiniButton("启用插件", "#34C759");
            enableBtn.Tag = p;
            enableBtn.Click += EnablePlugin_Click;
            actions.Children.Add(enableBtn);
        }
        else
        {
            var disableBtn = MiniButton("禁用插件", "#FF9F0A");
            disableBtn.Tag = p;
            disableBtn.Click += DisablePlugin_Click;
            actions.Children.Add(disableBtn);
        }

        var updBtn = UpdateOf(p);
        // 入口必须在：「可选升级」（具名分支 / 标签被移动）照样给按钮 —— 它只是不进「一键更新 N 个」，
        //   并非不给升。用户原话：「提示出来，由用户决定」，即决定权在用户，入口在卡片上。
        if (updBtn != null && updBtn.HasUpdate)
        {
            var updateBtn = MiniButton(PluginManager.UpdateButtonText(gitSpec, updBtn.TargetText, updBtn.Advisory), "#4A9EFF");
            updateBtn.Margin = new Thickness(8, 0, 0, 0);
            updateBtn.Tag = p;
            updateBtn.Click += UpdatePlugin_Click;
            // 2026-09-19 文案标准化：与 UpdateStatusHover 同一口径，带上字段名「比对基准：」。
            if (updBtn.CompareNote.Length > 0) updateBtn.ToolTip = "比对基准：" + updBtn.CompareNote;
            actions.Children.Add(updateBtn);
        }

        var uninstallBtn = MiniButton("卸载", "#FF3B30");
        uninstallBtn.Margin = new Thickness(8, 0, 0, 0);
        uninstallBtn.Tag = p;
        uninstallBtn.Click += UninstallPlugin_Click;
        // 正在卸载的那一条，重建后也要保持「停止卸载」形态（对抗性复查【中 2】）：
        //   刷新/重绘会把卡片换成一颗全新的「卸载」按钮，若不在这里认出来，停止入口就又没了
        //   —— 那正是复查指出的现场（"卸载期间根本没有可用的停止入口"，与安装侧第 49 条同一类缺陷）。
        //   只改这一条的文案与提示，配色一律不动。
        if (_uninstalling && IsUninstallingThis(p)) AdoptUninstallButton(uninstallBtn, p.Name);
        AddPluginCheckbox(card, p);      // 卡片右上角勾选框（批量操作入口）
        actions.Children.Add(uninstallBtn);

        // 三态里的「损坏」态：给一颗「重新安装」按钮（先清残留目录、再按清单声明重装）——
        // 旧口径下这种残留态显示"未安装"却没有可点的修复入口，用户只能反复点更新/卸载碰运气。
        if (versionMissing && versionLabel == "安装损坏")
        {
            var reinstallBtn = MiniButton("重新安装", "#34C759");
            reinstallBtn.Margin = new Thickness(8, 0, 0, 0);
            reinstallBtn.Tag = p;
            reinstallBtn.Click += ReinstallPlugin_Click;
            actions.Children.Add(reinstallBtn);
        }

        sp.Children.Add(actions);

        return card;
    }

    /// <summary>
    /// 推断本地插件的作者主页：从仓库地址（清单声明 -> 包内 repository -> homepage）
    /// 解析出「托管站 + owner」并拼成该站点的用户主页（任意站点，含 gitee）。
    /// 认不出仓库时，只有拿到的确实是包内声明的真作者才回落 GitHub 同名主页；
    /// 仓库归属兜底来的名字（<see cref="PluginManager.Plugin.AuthorFromRepo"/>）不回落到 GitHub ——
    /// 那样可能跳到 GitHub 上另一个同名的人，凭空安一个作者主页。
    /// </summary>
    private static string AuthorProfileUrl(PluginManager.Plugin p)
    {
        try
        {
            foreach (string raw in new[] { PluginManager.DepSpec(p.Name), p.RepositoryUrl, p.Homepage })
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var (host, path) = PluginManager.ParseRepoSpec(raw);
                if (host.Length == 0) continue;
                int slash = path.IndexOf('/');
                if (slash <= 0) continue;
                return $"https://{host}/{path.Substring(0, slash)}";
            }
        }
        catch { }
        return p.AuthorFromRepo || string.IsNullOrWhiteSpace(p.Author) ||
               p.Author == PluginManager.Plugin.AuthorUnknown || p.Author == "未知"
            ? ""
            : "https://github.com/" + p.Author.Trim();
    }

    /// <summary>
    /// 统一的链接样式：不使用下划线，以颜色、手型光标和悬停提亮表示可点击。
    ///  ① 还原颜色不能使用写死的色值：日间模式下链接已由主题映射为深色，
    ///     用深色主题原值还原会导致悬停一次后颜色永久改变。
    ///  ② 悬停色须基于当前颜色计算：夜间提亮、日间压深，写死的浅色在浅色底上不可见。
    /// </summary>
    /// <summary>
    /// 作者区（头像 + 名字合起来当一个超链接）：点头像或点名字都跳同一个作者主页。
    /// 没有主页时退化为普通展示（不变手型、不给提示）。
    /// </summary>
    /// <param name="tip">
    /// 悬停说明（如"来自仓库地址：…（包内没写作者信息）"）。为空时沿用原来的"打开作者主页"。
    /// </param>
    /// <param name="placeholderAvatar">
    /// true 则用本地占位头像（首字母色块），不联网取图。
    /// 用于"名字其实是仓库归属、不是作者本人"的情形：拿这个名字去 GitHub 取头像，
    /// 取到的可能是另一个同名用户的头像 —— 那是伪造，不如老老实实给个占位。
    /// </param>
    private FrameworkElement BuildAuthorLink(string owner, string url, double avatarSize, double fontSize,
                                             string? tip = null, bool placeholderAvatar = false)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        var avatar = placeholderAvatar ? BuildAuthorPlaceholder(owner, avatarSize) : BuildAuthorAvatar(owner, avatarSize);
        // 头像的悬停提示也不能把"仓库归属"说成作者：BuildAuthorAvatar 内部写的是「作者：xx」，
        // 这里在这一种情形下把它改掉（与名字的提示一致）。
        if (placeholderAvatar) avatar.ToolTip = "仓库归属：" + owner;
        var name = new TextBlock
        {
            Text = owner,
            FontSize = fontSize,
            MaxWidth = 220,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0xA8, 0xA8, 0xB0))
        };
        row.Children.Add(avatar);
        row.Children.Add(name);

        if (!string.IsNullOrEmpty(tip)) row.ToolTip = tip;

        if (url.Length > 0)
        {
            // 手型与点击落在两个可见部件上（事件冒泡到整行），整行共用同一个提示
            avatar.Cursor = Cursors.Hand;
            avatar.ToolTip = null;
            name.Cursor = Cursors.Hand;
            row.Tag = url;
            row.ToolTip = string.IsNullOrEmpty(tip) ? "打开作者主页：" + url : tip + "\n打开作者主页：" + url;
            row.MouseLeftButtonDown += AuthorName_Click;
            AddLinkHover(name, "#A8A8B0", "#F5F5F7");
        }
        return row;
    }

    /// <summary>
    /// 本地占位头像（首字母色块，圆角=半径）：不联网、不伪造真实头像。
    /// 与 <c>BuildAuthorAvatar</c> 的"取不到图时的退形态"同一套观感（同色算法、同字号比例），
    /// 区别只是它连"去取一次图"都不做。
    /// </summary>
    private static Border BuildAuthorPlaceholder(string owner, double size)
    {
        string name = (owner ?? "").Trim();
        var circle = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Background = new SolidColorBrush(AvatarColor(name)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        if (name.Length == 0) { circle.Visibility = Visibility.Collapsed; return circle; }
        circle.Child = new TextBlock
        {
            Text = name.Substring(0, 1).ToUpperInvariant(),
            FontSize = size * 0.58,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        return circle;
    }

    private static void AddLinkHover(TextBlock t, string? baseColor = null, string? hoverColor = null)
    {
        Color normal = (Color)ColorConverter.ConvertFromString(baseColor ?? "#5AC8FA");
        t.Foreground = new SolidColorBrush(normal);

        Brush? saved = null;
        t.MouseEnter += (_, _) =>
        {
            saved = t.Foreground;                       // 记住"此刻真实颜色"（可能是日间映射后的）
            Color now = (t.Foreground as SolidColorBrush)?.Color ?? normal;
            t.Foreground = new SolidColorBrush(Shift(now, ThemeManager.IsDark));
        };
        t.MouseLeave += (_, _) =>
        {
            if (saved != null) t.Foreground = saved;    // 原样还回去
        };
    }

    /// <summary>悬停变色算法：夜间提亮、日间压深，基于当前颜色计算而不使用写死色值。</summary>
    private static Color Shift(Color c, bool dark) => dark
        ? Color.FromRgb(
            (byte)Math.Min(255, c.R + 0x55),
            (byte)Math.Min(255, c.G + 0x55),
            (byte)Math.Min(255, c.B + 0x55))
        : Color.FromRgb(
            (byte)(c.R * 0.55),
            (byte)(c.G * 0.55),
            (byte)(c.B * 0.55));

    /// <summary>
    /// 点击作者名时打开作者主页（链接取自 Tag）。
    ///
    /// 链接来自 <see cref="AuthorProfileUrl"/>，而它的原料是清单声明 / package.json 的
    /// repository、homepage 字段 —— 插件作者可自填，属外部输入。旧实现把这段字符串直接交给
    /// <c>UseShellExecute=true</c> 的 shell：非 URL 的取值（如 UNC <c>\\attacker\share\evil.exe</c>）
    /// 会被当文件路径打开并执行。这里改为先过与插件市场同一个闸门
    /// （<see cref="PluginMarket.IsAllowedLinkUrl"/>），不通过不开（见 <see cref="OpenExternalLink"/>）。
    ///
    /// 本入口不接本地目录：它只产出网页链接（<see cref="AuthorProfileUrl"/> 认不出仓库时给空串），
    /// 空串在上面已经早退，即不存在"本来是打开本地目录、却被安全闸门堵死"的路径。
    /// </summary>
    private void AuthorName_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (sender is not TextBlock t || t.Tag is not string url || url.Length == 0) return;
            OpenExternalLink(url, "插件卡片作者主页");
            e.Handled = true;
        }
        catch (Exception ex) { Logger.LogError("AuthorName_Click", ex); }
    }

    /// <summary>
    /// 插件标题的点击目标（<c>TextBlock.Tag</c>）。
    /// 有网址就开网页；本地链接插件没有网址、只有本地目录，就在文件管理器里打开那个目录。
    /// 动作由 <see cref="LocalDir"/> 是否非空决定 —— 不靠 URL 的字符串形态猜
    /// （本地目录名完全可以长成 http 形状，拿形态当判据迟早判错）。
    /// </summary>
    private sealed record PluginCardTarget(string Url, string LocalDir);

    /// <summary>
    /// 点击插件名：有网址打开主页；本地链接插件改为在文件管理器中打开它的本地目录。
    /// 目录来自数据层 <see cref="PluginManager.Plugin.LocalDir"/>（已过存在性与越界两道校验），
    /// 这里拿到的要么是已校验过的绝对路径、要么是空串，即不再也不该二次拼接路径。
    /// </summary>
    private void PluginName_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            string url = "", localDir = "";
            if (sender is TextBlock t)
            {
                if (t.Tag is PluginCardTarget target) (url, localDir) = (target.Url, target.LocalDir);
                else if (t.Tag is string s) url = s;   // 兼容旧的纯字符串 Tag（市场卡片等复用本处理器）
            }

            if (localDir.Length > 0)
            {
                OpenLocalFolder(localDir);
                e.Handled = true;
                return;
            }
            if (url.Length == 0) return;

            // 本地目录在上面那条分支已经分流走了（OpenLocalFolder），这里只剩网页链接。
            // LinkUrl 的原料是清单声明 / package.json 的 repository、homepage 字段
            // （见 PluginManager.Plugin.LinkUrl ①~④），插件作者可自填，属外部输入；
            // 旧实现把这段字符串直接交给 UseShellExecute=true 的 shell，即 UNC 投毒会被当文件路径执行。
            // 这里与插件市场 OpenUrl 走同一个闸门，两个页面口径不各写一套。
            // 打开成功才记"已打开"，被拦时不留下与本机事实不符的日志。
            if (OpenExternalLink(url, "插件卡片主页"))
                Logger.Log($"打开插件主页: {url}");
        }
        catch (Exception ex) { Logger.LogError("PluginName_Click", ex); }
    }

    /// <summary>
    /// 把一条来自外部输入的网页链接交给系统浏览器打开 —— 先过闸门，再打开。
    ///
    /// 与 <c>MainWindow.Market.OpenUrl</c> 同一个判据、同一个口径（那边是 <c>private static</c>，
    /// 跨文件用不了，故此处照其写法复刻一份；两边都只是调
    /// <see cref="PluginMarket.IsAllowedLinkUrl"/>，判据本身只有一处、不会各写一套）：
    /// 只放行白名单 host 的 https，其余（UNC / file: / http 明文 / 任意 scheme / 陌生 host /
    /// 空串）一律不打开，给中性中文提示并落 <c>NoteDiagnosis</c> 诊断留痕。
    ///
    /// 为什么必须过闸门：<c>UseShellExecute=true</c> 下，非 URL 字符串不会被当网址拒绝，
    /// 而是按文件 / UNC 路径交给 shell 打开 —— <c>\\attacker\share\evil.exe</c> 这类投毒样本
    /// 因此可被直接执行。闸门失败即关闭，不做字符串猜测式放行。
    ///
    /// 与"打开本地目录"（<see cref="OpenLocalFolder"/>，走 explorer.exe）是两条独立通道：
    /// 本地目录不经过本方法，不会被这里的白名单误判、误堵。
    /// </summary>
    /// <param name="source">诊断留痕用的来源说明（哪个打开点、什么字段），便于事后倒查。</param>
    /// <returns>真正交给系统打开了才 true；被闸门拦下或启动失败均 false（调用方据此决定是否记"已打开"）。</returns>
    private static bool OpenExternalLink(string url, string source)
    {
        try
        {
            if (!PluginMarket.IsAllowedLinkUrl(url))
            {
                Logger.NoteDiagnosis($"已拒绝打开非白名单链接（来源：{source}）：{url}");
                GuardDialog.Show("该链接不在允许打开的网站范围内，未执行打开操作。", "链接已拦截",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) { Logger.LogError($"OpenExternalLink({source})", ex); return false; }
    }

    /// <summary>
    /// 在文件管理器中打开一个本地目录。
    ///
    /// 与 <c>OpenPath_Click</c> / <c>MainWindow.Console.OpenLogFolder_Click</c> 同一套写法：
    /// 固定启动 <c>explorer.exe</c>、目录当参数传，不把路径直接交给 shell
    /// （<c>UseShellExecute=true</c> 直接吃外部字符串会连带执行 .exe/.lnk/.cmd —— 目录名来自
    /// 清单与磁盘，属外部输入，不能当可执行目标）。
    /// 目录必须在点击这一刻仍然存在：数据层校验过、到点击之间它可能已被删掉，
    /// 这里再确认一次，拿不到就给中性提示，绝不打开别的东西。
    /// </summary>
    private static void OpenLocalFolder(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            if (!Directory.Exists(dir))
            {
                Logger.NoteDiagnosis($"本地插件目录已不存在，未打开：{dir}");
                GuardDialog.Show("该插件的本地目录已不存在（可能已被删除或移动），未执行打开操作。",
                    "目录已不存在", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{dir}\"",
                UseShellExecute = true
            });
            Logger.Log($"打开本地插件目录: {dir}");
        }
        catch (Exception ex) { Logger.LogError("OpenLocalFolder", ex); }
    }

    /// <summary>
    /// 小按钮，使用自绘模板：系统默认按钮模板在禁用态会绘制为白色底，
    /// 此处统一使用指定底色并在禁用时以半透明显示；模板同时绑定 Padding，
    /// 否则文字会紧贴色块边缘。
    /// </summary>
    private static Button MiniButton(string text, string bg, double padX = 11, double padY = 5,
                                     string? textColor = null, double fontSize = 11.5)
    {
        var btn = new Button
        {
            Content = text,
            FontSize = fontSize,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(textColor ?? "#FFFFFF")),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(padX, padY, padX, padY),
            MinHeight = 26,
            Cursor = Cursors.Hand,
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bg))
        };

        // 统一圆角外观（模板集中在 RoundBtn，界面里所有按钮共用同一份）
        RoundBtn.Apply(btn);
        return btn;
    }

    /// <summary>启用插件：从 cordis.patch.yml 移除禁用记录（先备份），重启 DSH 后生效。</summary>
    private async void EnablePlugin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not PluginManager.Plugin p) return;

        string warn;
        if (p.Compatibility == PluginManager.Compat.Broken)
            warn = $"\n\n⛔ 它声明要求 {p.Requirement}，而当前 DSH 是 {_currentDshVersion} —— 启用后很可能出问题。";
        else if (p.Compatibility == PluginManager.Compat.Partial)
            warn = $"\n\n🟡 它可用，但作者面向的是 {p.RequirementTarget}（当前为 {_currentDshVersion}）—— 可能存在个别兼容性问题。";
        else if (string.IsNullOrEmpty(p.Requirement))
            warn = "\n\n它未声明 DSH 版本要求，兼容性只能实测；若页面无法打开，本程序会提示回退版本。";
        else
            warn = $"\n\n它声明的版本要求与当前 DSH {_currentDshVersion} 正好对得上。";

        var r = GuardDialog.Show(
            $"启用插件「{p.Name}」？\n\n" +
            "将从配置文件里移除这条禁用记录（修改前自动备份），重启 DSH 后生效。" + warn,
            "确认启用插件", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (r != MessageBoxResult.OK) return;

        try { PluginsSummaryText.Text = $"正在启用 {p.Name}…"; } catch { }

        string msg = await Task.Run(() => PluginManager.Enable(p, force: true));
        bool ok = msg.StartsWith("已重新启用") || msg.StartsWith("已启用");
        AddEvent(ok ? $"已启用插件 {p.Name}" : $"启用插件失败 {p.Name}", ok ? EventKind.Good : EventKind.Bad);
        if (!ok)
            GuardDialog.Show(Shorten(msg, 500), "启用失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        else
            GuardDialog.Show(Shorten(msg, 500), "启用插件", MessageBoxButton.OK, MessageBoxImage.Information);

        await RefreshPluginsAsync(true);
    }

    // ══════════════ 已安装页的排序下拉 ══════════════
    /// <summary>
    /// 当前排序字段。默认「安装时间」：主人要的是**装得最晚的排在最上面**，所以打开插件页第一眼看到的是
    /// "最近装的那个"（筛选时代那套"全部/启用中"已整体移除，排序层取而代之）。
    /// </summary>
    private PluginSortField _pluginSort = PluginSortField.Installed;

    /// <summary>当前是否反选（正选 = 时间戳越新越靠上；反选 = 越旧越靠上）。</summary>
    private bool _pluginSortDesc = true;

    /// <summary>
    /// 排序字段。声明顺序 = 下拉菜单从上到下的顺序（安装时间 → 创建日期 → 更新日期 → 兼容性）。
    ///
    /// ⚠ 与 <c>PluginManager.Compat</c> 的声明顺序**无关**：那个枚举是 <c>Ok, Partial, Unknown, Broken</c>，
    /// 而显示优先级是"红 → 橙 → 绿 → 未声明"⇒ 必须显式映射（见 <see cref="CompatRank"/>），
    /// 不能拿枚举值大小当排序键。
    /// </summary>
    internal enum PluginSortField { Installed, Created, Updated, Compatibility }

    private void InstalledSort_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border b) return;
        var field = (b.Tag as string) switch
        {
            "created" => PluginSortField.Created,
            "updated" => PluginSortField.Updated,
            "compat" => PluginSortField.Compatibility,
            _ => PluginSortField.Installed
        };
        ApplyInstalledSort(field);
        e.Handled = true;
    }

    /// <summary>
    /// 选中一个排序字段并重排。三条规则与理由：
    ///   · 点的是**别的**字段 ⇒ 采用该字段自己的默认方向（时间类一律"新 → 旧"）。
    ///     若沿用上一个字段的方向，从「安装时间 ↓」切到「创建日期」会把方向也带过去 —— 换一项就"串味"，
    ///     用户看到的箭头跟他刚点的那一项对不上；
    ///   · 再点**当前**字段 ⇒ 翻转正反选（主人要的就是"再点击就是反选"）；
    ///   · 兼容性是**绝对优先级**（不兼容 → 基本可用 → 完全兼容 → 未声明），方向开关对它没有意义
    ///     ⇒ 不翻转，并且一律把方向复位成正选，免得按钮上挂着一个对它根本不生效的箭头。
    /// </summary>
    private void ApplyInstalledSort(PluginSortField field)
    {
        if (field == PluginSortField.Compatibility)
        {
            _pluginSort = field;
            _pluginSortDesc = true;              // 方向对它无效，复位成中性值
        }
        else if (_pluginSort == field)
        {
            _pluginSortDesc = !_pluginSortDesc;  // 再点一次 = 反选
        }
        else
        {
            _pluginSort = field;
            _pluginSortDesc = true;              // 首次点某项 = 该项默认方向（新 → 旧）
        }
        InstalledFilterPopup.IsOpen = false;
        PaintInstalledSortMenu();
        RenderPlugins();
    }

    /// <summary>点工具条上的「排序」按钮：开合下拉（下拉内容的选中态由 <see cref="PaintInstalledSortMenu"/> 刷）。</summary>
    private void InstalledSortMenu_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            InstalledFilterPopup.IsOpen = !InstalledFilterPopup.IsOpen;
            if (InstalledFilterPopup.IsOpen) PaintInstalledSortMenu();
            e.Handled = true;
        }
        catch (Exception ex) { Logger.LogError("InstalledSortMenu_Click", ex); }
    }

    // ══════════════ 排序纯函数区（只吃数据，不碰 UI，可穷举自检） ══════════════

    /// <summary>
    /// 把时间串取成可比较的"时刻"。**这个库里的时间串是两种写法混着的**，因此必须容错：
    ///   · <see cref="PluginTimes"/> 写的是 <c>yyyy-MM-dd HH:mm</c>（本机时间，不带时区）；
    ///   · <c>PluginUpdate.Created</c> 来自镜像站的 <c>time</c> 表，通常是 ISO8601。
    /// 用 <see cref="System.Globalization.DateTimeStyles.RoundtripKind"/> 而不是 None：后者会把带 <c>Z</c>
    /// 的串按 UTC 换算到本机时区，平白挪掉一天；对本模块自己写的无时区串则完全等价。
    /// 认不出来的串（含空值）返回 <c>null</c>，由调用方统一按"没有记录"处理 —— 不在这里编一个时间。
    /// </summary>
    internal static DateTime? ParseSortTime(string? raw)
    {
        string s = (raw ?? "").Trim();
        if (s.Length == 0) return null;
        return DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out DateTime t) ? t : null;
    }

    /// <summary>
    /// 兼容性档位的**显示优先级**（越小越靠前），是"绝对优先级"而不是数值大小：
    /// 红（<see cref="PluginManager.Compat.Broken"/> 不兼容）→ 橙（<c>Partial</c> 基本可用）
    /// → 绿（<c>Ok</c> 完全兼容）→ 未声明（<c>Unknown</c>）最后。
    ///
    /// ⚠ 必须显式映射：枚举声明顺序是 <c>Ok, Partial, Unknown, Broken</c>，正好**不是**显示顺序，
    /// 直接比较枚举值会把"未声明"排到"完全兼容"前面（现场就是排序看起来乱掉）。
    /// </summary>
    internal static int CompatRank(PluginManager.Compat c) => c switch
    {
        PluginManager.Compat.Broken => 0,      // 红：不兼容，最该被看见
        PluginManager.Compat.Partial => 1,     // 橙：基本可用
        PluginManager.Compat.Ok => 2,          // 绿：正好兼容
        _ => 3                                 // 未声明：放最后
    };

    /// <summary>
    /// 一次比较所需的全部数据（**值拷贝**，不含任何活对象）。
    /// 特意不直接吃 <see cref="PluginManager.Plugin"/>：排序比较跑在 UI 线程上，拿活对象比较既难自检
    /// （造不出真实插件），也容易顺手把"排序"写成"顺便刷新点东西"。这里只吃四个字段，纯函数可穷举。
    /// </summary>
    internal readonly record struct PluginSortData(
        string Name,
        DateTime? InstalledAt,
        DateTime? CreatedAt,
        DateTime? UpdatedAt,
        int CompatPriority);

    /// <summary>按包名比大小（<see cref="StringComparer.OrdinalIgnoreCase"/>，与插件表的比较器同口径）。</summary>
    private static readonly StringComparer SortNameComparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>取某字段上的排序时间；兼容性这一项没有时间，一律返回 null。</summary>
    private static DateTime? SortTimeOf(PluginSortData d, PluginSortField f) => f switch
    {
        PluginSortField.Installed => d.InstalledAt,
        PluginSortField.Created => d.CreatedAt,
        PluginSortField.Updated => d.UpdatedAt,
        _ => null
    };

    /// <summary>
    /// 比较两个插件（排序的唯一判据，界面与自检都走它）。返回负 / 零 / 正，与 <see cref="Comparer{T}"/> 同义。
    ///
    /// 三条硬规则：
    ///   ① <b>空值一律排最后，与正反选无关</b>。反选（<paramref name="desc"/> = true）只翻转"两个都有值"的相对次序；
    ///      若把 null 也丢进去一起翻转，"未知"就会因为反选浮到最前面 —— 那是把"没有记录"说成了"最新"，
    ///      而它恰恰是最该沉底的一档；
    ///   ② <b>并列必须有确定次序</b>（补"按包名"第二判据）。否则同一天装了三个插件时，
    ///      <c>List.Sort</c> 是不稳定排序，每次刷新（重新查版本、切换主题都会触发重渲染）卡片的先后都可能跳，
    ///      用户会以为"列表自己在乱动"；
    ///   ③ <b>兼容性不吃方向开关</b>：它是绝对优先级，正反选都不改变它的次序。
    /// </summary>
    internal static int ComparePlugins(PluginSortField field, bool desc, PluginSortData a, PluginSortData b)
    {
        int c;
        if (field == PluginSortField.Compatibility)
        {
            c = a.CompatPriority.CompareTo(b.CompatPriority);      // 固定：红 → 橙 → 绿 → 未声明
        }
        else
        {
            DateTime? ta = SortTimeOf(a, field);
            DateTime? tb = SortTimeOf(b, field);
            // 规则①：缺值的沉底，且**不受方向影响**（这一行必须在 desc 翻转之前返回）
            if (ta is null && tb is null) c = 0;
            else if (ta is null) return 1;                          // a 没记录 ⇒ a 靠后
            else if (tb is null) return -1;                         // b 没记录 ⇒ b 靠后
            else
            {
                c = ta.Value.CompareTo(tb.Value);
                if (desc) c = -c;                                   // 正选：新 → 旧（越大越靠前）
            }
        }
        // 规则②：并列（含"两个都没记录"）时按包名定序，保证每次渲染顺序一致
        return c != 0 ? c : SortNameComparer.Compare(a.Name ?? "", b.Name ?? "");
    }

    /// <summary>
    /// 把已装插件排好序（唯一的排序入口，<see cref="RenderPlugins"/> 调它）。
    /// 用 <see cref="List{T}.Sort(Comparison{T})"/> 而不是 <c>OrderBy</c>：比较器里已经带了"按包名"的
    /// 第二判据，次序是**全序**，不再依赖排序算法是否稳定。
    /// </summary>
    private void SortInstalledPlugins(List<PluginManager.Plugin> list)
        => list.Sort((x, y) => ComparePlugins(_pluginSort, _pluginSortDesc, SortDataOf(x), SortDataOf(y)));

    /// <summary>把插件现取成比较用的数据（时间列现查 <see cref="PluginTimes"/>，不缓存 —— 安装/更新后即刻生效）。</summary>
    private PluginSortData SortDataOf(PluginManager.Plugin p)
        => new(p.Name,
               ParseSortTime(PluginTimes.SubscribedOf(p.Name)),   // 订阅列 = 界面上的「安装时间」
               ParseSortTime(UpdateOf(p)?.Created),
               ParseSortTime(PluginTimes.UpdatedOf(p.Name)),
               CompatRank(p.Compatibility));

    // ── 自检出入口（都放在本文件；MainWindow.xaml.cs 只留 InstalledSortForTest 一个入口） ──

    /// <summary>自检用：走**真实**的点选规则选一项（含"再点一次翻转方向"与"兼容性不翻转"）。</summary>
    internal void ClickInstalledSortRowForTest(PluginSortField field) => ApplyInstalledSort(field);

    /// <summary>自检用：最近一次渲染实际写进面板的卡片顺序（包名）。</summary>
    internal string[] InstalledRenderOrderForTest() => _installedRenderOrder.ToArray();

    /// <summary>自检用：工具条「排序」按钮上的回显文字。</summary>
    internal string InstalledSortTextForTest => InstalledSortText?.Text ?? "";

    /// <summary>自检用：排序下拉里某一行的方向箭头（空串 = 该行没有箭头，即未选中）。</summary>
    internal string InstalledSortRowArrowForTest(PluginSortField f)
    {
        Border? row = f switch
        {
            PluginSortField.Created => ISRowCreated,
            PluginSortField.Updated => ISRowUpdated,
            PluginSortField.Compatibility => ISRowCompat,
            _ => ISRowSubscribed
        };
        return row?.Child is Grid g && g.Children.Count > 1 && g.Children[1] is TextBlock t ? t.Text : "";
    }

    /// <summary>自检用：某包名当前的兼容档位优先级（0 = 不兼容 … 3 = 未声明）。</summary>
    internal int InstalledCompatRankForTest(string name)
        => CompatRank(_plugins.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
                      ?.Compatibility ?? PluginManager.Compat.Unknown);

    /// <summary>
    /// 自检用：**按指定字段与方向**独立算一遍排序结果，与渲染时那次排序同源（同一个 <see cref="ComparePlugins"/>）。
    /// 拿它和 <see cref="InstalledRenderOrderForTest"/> 比对，才真的验得出"排序生效了、方向也对" ——
    /// 只数卡片张数是验不出来的：排序**不改变卡片数量**，那个判据恒过。
    /// 刻意收参数（而不是读当前字段）：自检要验"再点一次会翻转"，就得能算出**另一个方向**的期望值。
    /// </summary>
    internal string[] InstalledSortOrderForTest(string[] names, PluginSortField field, bool desc)
    {
        var data = names.Select(n => new PluginSortData(n,
            ParseSortTime(PluginTimes.SubscribedOf(n)),
            ParseSortTime(UpdateOf(new PluginManager.Plugin { Name = n })?.Created),
            ParseSortTime(PluginTimes.UpdatedOf(n)),
            InstalledCompatRankForTest(n))).ToList();
        data.Sort((x, y) => ComparePlugins(field, desc, x, y));
        return data.Select(d => d.Name).ToArray();
    }

    /// <summary>
    /// 排序方向标识（普通文本箭头，不用图标）：**正选 ↑**（时间戳越新越靠上）／**反选 ↓**（越旧越靠上）。
    ///
    /// ⚠ 与市场页的箭头约定**故意不同**：市场页是 <c>_marketSortDesc ? "↓" : "↑"</c>（"降序 = ↓"），
    /// 这里按主人的口径 —— 他在例子里写明"第一次点出现 ↑、再点变 ↓"，且"正选 = 时间戳越新越在上面"，
    /// 即 **↑ 表示"往上 = 往新"**。两处取舍不同是刻意的，以主人给的交互为准。
    ///
    /// 兼容性不吃方向 ⇒ 不给箭头：它是绝对优先级，挂一个不生效的箭头只会让人以为"再点一下能翻过来"。
    /// </summary>
    private string SortArrowText()
        => _pluginSort == PluginSortField.Compatibility ? "" : (_pluginSortDesc ? "↑" : "↓");

    /// <summary>刷新排序下拉的选中态与工具条按钮文案（点选与开合都走它）。</summary>
    private void PaintInstalledSortMenu()
    {
        try
        {
            bool byInstalled = _pluginSort == PluginSortField.Installed;
            bool byCreated = _pluginSort == PluginSortField.Created;
            bool byUpdated = _pluginSort == PluginSortField.Updated;
            bool byCompat = _pluginSort == PluginSortField.Compatibility;

            // 先写两列的文字：第一列 = 名称（选中的加 "✓ "，与市场页同一套选中标记），
            // 第二列 = 方向箭头（只有当前生效的那一行有；兼容性没有箭头）。
            SetInstalledSortRow(ISRowSubscribed, "安装时间", byInstalled, byInstalled ? SortArrowText() : "");
            SetInstalledSortRow(ISRowCreated, "创建日期", byCreated, byCreated ? SortArrowText() : "");
            SetInstalledSortRow(ISRowUpdated, "更新日期", byUpdated, byUpdated ? SortArrowText() : "");
            SetInstalledSortRow(ISRowCompat, "兼容性", byCompat, "");      // 兼容性：绝对优先级，不显示箭头

            // 底色与悬停交给市场页那套公用实现（它按 row.Child is TextBlock 找文字，本页行是 Grid ⇒ 文字那步自然跳过）
            PaintMenuRow(ISRowSubscribed, byInstalled);
            PaintMenuRow(ISRowCreated, byCreated);
            PaintMenuRow(ISRowUpdated, byUpdated);
            PaintMenuRow(ISRowCompat, byCompat);

            WirePopupContent(InstalledFilterPopup);      // 弹层内容不在窗口视觉树里，动效单独挂

            // 按钮只写「排序」：箭头按主人给的例子挂在**菜单里被选中的那一行**上，
            // 两处都写会把同一个状态说两遍（主人只要求菜单项上出现箭头）。
            if (InstalledSortText != null) InstalledSortText.Text = "排序";
        }
        catch (Exception ex) { Logger.LogError("PaintInstalledSortMenu", ex); }
    }

    /// <summary>
    /// 写一行的两列：左 = 名称（选中加 "✓ " 前缀，与市场页的选中标记同一套），右 = 方向箭头。
    ///
    /// 为什么"✓ "由这里写、而不是交给 <see cref="PaintMenuRow"/>：那个方法是按 <c>row.Child is TextBlock</c>
    /// 取文字的，而本页的行为了把箭头**右对齐**改成了"两列 Grid"（左文字 / 右箭头），它取不到文字 ⇒ 钩号会丢。
    /// 又不能为本页去改它（市场页也在用）。底色的选中/悬停仍全部由它处理，这里只负责两段文字。
    /// </summary>
    private static void SetInstalledSortRow(Border? row, string baseText, bool selected, string arrow)
    {
        // 行结构：Border → Grid → [0] 名称 TextBlock，[1] 方向箭头 TextBlock
        if (row?.Child is not Grid g || g.Children.Count < 2) return;
        if (g.Children[0] is TextBlock name) name.Text = (selected ? "✓ " : "") + baseText;
        if (g.Children[1] is TextBlock arr) arr.Text = arrow;
    }

    // ══════════ 更新的重入闸（「一键更新」⇄ 卡片上单颗「更新到 X」共用一个） ══════════
    /// <summary>
    /// 「更新」这条流水线正在跑（一键更新 / 卡片上单颗「更新到 X」共用一个闸）。
    ///
    /// <para><b>为什么两条路径必须共用一个标志</b>：它们跑的是同一条 <c>npx</c> 命令、动的是同一份
    /// <c>node_modules</c> / <c>pnpm-lock.yaml</c>。各管各的只能保证"我这一类不重入"，
    /// 挡不住"一键更新还在跑、用户又点了一张卡片上的更新"——两条 npx 并发改同一个包目录，
    /// 轻则 pnpm store 锁冲突 + 两份快照，重则插件清单被写坏。共用一个才真正互斥。</para>
    ///
    /// <para><b>为什么开头那个模态确认框挡不住</b>：它只在进入流程时挡一次；循环里每项
    /// <c>await RunCommandAsync("npx", …, 600000)</c> 最长 10 分钟，这期间 UI 完全可交互
    /// （GuardDialog 的闸门只防"弹窗叠弹窗"，不防流程重入）。</para>
    ///
    /// <para><b>为什么不复用 <c>_batchBusy</c></b>：它管的是禁用 / 启用 / 更新 / 卸载四种批量动作，
    /// 其中禁用 / 启用只写插件配置、根本不跑 pnpm——拿它当闸会把"批量禁用期间点一键更新"也一并拒掉，
    /// 属于无谓误伤。卸载有自己的 <c>_uninstalling</c>（那是"可中止"的流程，语义不同），也不合并。
    /// <b>（本轮补充）</b>批量里那两种真跑命令的动作（批量更新 / 批量卸载）改由
    /// <see cref="_pluginWriteBusy"/> 占闸，两个入口互查，即三条更新路径与那两种批量动作真正互斥，
    /// 而批量禁用 / 启用照旧不占闸、不会被误拒。</para>
    /// </summary>
    private bool _updatingBusy;

    /// <summary>
    /// <b>「插件正在变动中」的判据，全项目只此一份</b>（纯函数：不读字段、不碰 UI、无副作用，自检可直接断言）。
    ///
    /// <para>四个入参就是本项目里全部<b>会跑命令、会改依赖图</b>的忙标志，由调用方在 UI 线程上现读现传：
    /// <c>pluginWrite</c> = <see cref="_pluginWriteBusy"/>（批量更新 / 批量卸载 / 市场安装 / 单颗卸载 / 回滚重装）、
    /// <c>updating</c> = <see cref="_updatingBusy"/>（一键更新 / 单颗更新 / 重新安装）、
    /// <c>marketBusy</c> / <c>installing</c>（市场安装）。</para>
    ///
    /// <para><b>为什么不做成无参实例方法直接读字段</b>：那样它就不再是纯函数，无法在自检里拿四个布尔量
    /// 穷举断言（真值表 16 种）；而"退出要不要拦一下"这种事恰恰是最不能猜的——判据写成纯函数，
    /// 才谈得上"必须能自检"。</para>
    ///
    /// <para><b>为什么是"或"而不是"与"</b>：这四个标志任意一个为真，就意味着有一轮插件操作正跑在半路，
    /// 此刻退出都会留下一半没做完的现场。所以任一个为真即算"变动中"。</para>
    ///
    /// <para><b>⚠️ 为什么 <c>_batchBusy</c> 不在判据里</b>（2026-09-20 复核）：它<b>一个标志盖住批量四种动作</b>
    /// （禁用 / 启用 / 更新 / 卸载），其中 <b>批量禁用 / 批量启用只写一次插件配置、一条命令都不跑</b>
    /// ——那种时刻退出不存在"跑了一半"的中间态，用 <c>_batchBusy</c> 当判据会让"禁用两个插件时点退出"
    /// 也弹"可能只完成了一半"，<b>文案说的不是事实</b>。而真会留半截的两条批量动作
    /// （更新 / 卸载）本身在入口处<b>成对占着 <see cref="_pluginWriteBusy"/></b>
    /// （<c>MainWindow.Batch.cs</c>：<c>_batchBusy = true;</c> 紧接着 <c>BeginPluginWriteState();</c>，
    /// 收尾为 <c>_batchBusy = false;</c> + <c>EndPluginWriteState();</c>）⇒ <b>去掉 batchBusy 不漏任何一条路径</b>。
    /// 这与写闸 <see cref="PassPluginWriteGate"/> 的口径本来就是同一个判断："只写配置的批量动作不算变动中"
    /// （见 <see cref="_pluginWriteBusy"/> 的注释：禁用 / 启用一个字节都不碰写闸）。</para>
    ///
    /// <para>⚠️ 它只回答"此刻忙不忙"，<b>不决定要不要拦死</b>：调用方（退出拦截）拿到 true 只弹一个确认框，
    /// 用户点「确定」照样退得出去——本程序刚因为"锁窗"被批评过，判据绝不能顺手变成闸门。</para>
    /// </summary>
    internal static bool PluginWorkInProgressFor(
        bool pluginWrite, bool updating, bool marketBusy, bool installing)
        => pluginWrite || updating || marketBusy || installing;

    /// <summary>
    /// <b>「会改插件依赖图」这件事的写闸</b>：批量更新 / 批量卸载整轮占着它
    /// （与 <see cref="_updatingBusy"/> 并列，语义分开、互不替代）。
    ///
    /// <para><b>为什么必须再开一个</b>：批量更新与批量卸载和「一键更新 / 单颗更新 / 重新安装」跑的
    /// 是同一类命令、动的是同一份 <c>node_modules</c> / <c>pnpm-lock.yaml</c>，原先却各管各的
    /// （那三条看 <see cref="_updatingBusy"/>、批量看 <c>_batchBusy</c>）-> 批量更新跑着的时候
    /// 还能点「一键更新」，两条命令并发改同一个包目录。</para>
    ///
    /// <para><b>为什么不直接把 <c>_batchBusy</c> 拿来当闸</b>：它管四种批量动作，其中
    /// <b>批量禁用 / 批量启用根本不跑命令</b>（只写插件配置）—— 拿它当闸会把"批量禁用期间点一键更新"
    /// 也一并拒掉，属于无谓误伤。所以这里只让<b>跑命令</b>的批量动作（更新 / 卸载）占闸，
    /// 禁用 / 启用一个字节都不碰它。</para>
    ///
    /// <para><b>为什么不干脆把它和 <see cref="_updatingBusy"/> 合成一个</b>：两者都还要当"重入闸"
    /// 单独用（更新那条流水线要单独判、批量那四种要共用 <c>_batchBusy</c>），合并会让
    /// "谁占着闸"这件事从标志上读不出来。这里走的是<b>两个标志、入口互查</b>：
    /// 本闸的入口看 <c>_updatingBusy</c>，更新的入口看本闸（见 <see cref="PassPluginWriteGate"/>
    /// 与 <see cref="PassUpdateGate"/>）。</para>
    ///
    /// <para>它与 <c>_batchBusy</c> 的<b>生命周期刻意不完全重合</b>：<c>_batchBusy</c> 从确认框之后
    /// 一直压到整轮收尾（连收尾的弹窗、刷新都算在内），本闸则在确认框之前就查、之后立刻落
    /// —— 查与落之间没有任何 <c>await</c>，中间那几条同步语句插不进第二个动作
    /// （按钮事件都在 UI 线程上排队）。</para>
    /// </summary>
    private bool _pluginWriteBusy;

    /// <summary>重入被拒时的统一提示（两个入口同一句；不出现命令写法）。</summary>
    private const string UpdateBusyMessage = "已经有一轮插件更新在进行中，请等它跑完再试。";

    /// <summary>
    /// 批量更新 / 批量卸载被拒时的提示（同样不出现命令写法）。
    /// 与 <see cref="UpdateBusyMessage"/> 分开一句：这两条路径动的是<b>一批</b>插件、还会动插件清单，
    /// 说成"有一轮插件更新在进行中"不准确（批量卸载时尤其）。
    /// </summary>
    private const string PluginWriteBusyMessage = "已经有一批插件改动在进行中，请等它跑完再试。";
    /// <summary>更新期间被压灰的那颗「一键更新」按钮（收尾恢复；只记不写死）。</summary>
    private Border? _updateAllBtn;
    /// <summary>那颗按钮的原样：底色、提示、标签文字色（收尾照原样还原）。</summary>
    private Brush? _updateAllBtnFace;
    private object? _updateAllBtnTip;
    private Brush? _updateAllBtnTextFace;

    /// <summary>
    /// 重入闸的唯一入口：已经有更新在跑，即如实说一句"正在更新中"并返回 false（绝不静默无反应）。
    /// 三个入口（一键更新 / 卡片上单颗「更新到 X」/ 重新安装）都先过这里——判断只有这一份，
    /// 自检也断言这一段。
    ///
    /// <para><b>（本轮补）它现在是两道事实的合取</b>：① <see cref="_updatingBusy"/> —— 更新流水线
    /// 自己重入（原有行为，一字不改）；② <see cref="_pluginWriteBusy"/> —— 批量更新 / 批量卸载
    /// 正占着写闸。少了②，"批量更新跑着的时候点一键更新"就是两条命令并发改同一个包目录
    /// （跨流程的那半个缺口就是它）。两处命中的都是同一句既有文案
    /// <see cref="UpdateBusyMessage"/>：站在用户角度这两件事就是一句话。</para>
    ///
    /// <para>注意它只查不开：闸门由 <see cref="BeginUpdatingState"/> 在用户点了确认之后才落下
    /// （确认框本身是模态的、那一段不需要闸；三处都放在第一条 await 之前）。</para>
    /// </summary>
    private bool PassUpdateGate()
    {
        if (!_updatingBusy && !_pluginWriteBusy) return true;
        AddEvent(UpdateBusyMessage, EventKind.Warn);
        return false;
    }

    /// <summary>
    /// 插件写闸的唯一入口：已经有"会改依赖图"的动作在跑，即如实说一句并返回 false。
    /// 占闸的有五条：一键更新 / 单颗更新 / 重新安装（<see cref="_updatingBusy"/>）与
    /// 批量更新 / 批量卸载（<see cref="_pluginWriteBusy"/>）。
    /// 批量禁用 / 批量启用不在此列——它们只写插件配置，跟这里的任何一条都不冲突。
    /// <para>与 <see cref="PassUpdateGate"/> 一样只查不开：闸门由
    /// <see cref="BeginPluginWriteState"/> 落在批量路径"立刻要跑第一条命令"那一句。</para>
    /// </summary>
    private bool PassPluginWriteGate()
    {
        // 动依赖图之前先清一次孤儿锁：无锁与活锁都是安全 no-op，只有确证 PID 已不存在才删。
        // 位置选在这里，是因为本方法覆盖了全部"会改依赖图"的路径（单颗卸载 / 批量更新 /
        // 批量卸载 / 市场安装 / 回滚重装 / 本类的更新入口），一处即可全覆盖；且此刻本程序自己
        // 没有在跑插件命令，锁若在只可能是孤儿锁或由别处进程持有。
        PluginManager.TryClearStalePackageLock();
        if (!_pluginWriteBusy && !_updatingBusy) return true;
        AddEvent(PluginWriteBusyMessage, EventKind.Warn);
        return false;
    }

    /// <summary>
    /// 批量更新 / 批量卸载：落入"改依赖图中"（占写闸）+ 把批量弹层里那四颗动作按钮压灰。
    ///
    /// <para>照抄 <see cref="BeginUpdatingState"/> 那套的一半：只落标志、只改"看得出灰"，
    /// 收尾在 <see cref="EndPluginWriteState"/> 里照原样还原。按钮置灰复用既有的
    /// <c>UpdateBatchBar</c>（它按忙闲统一设四颗按钮的 <c>IsEnabled</c>），
    /// 所以这里不另写一套按钮外观还原——那正是重复两份状态、早晚写岔。</para>
    ///
    /// <para>与 <c>_batchBusy</c> 同一条 <c>finally</c> 收尾，即异常路径也一定复位。</para>
    /// </summary>
    private void BeginPluginWriteState()
    {
        _pluginWriteBusy = true;
        try { UpdateBatchBar(); }
        catch (Exception ex) { Logger.LogError("BeginPluginWriteState", ex); }
    }

    /// <summary>
    /// 插件写闸收尾：复位闸门 + 刷新批量条的按钮忙闲（灰的还原）。幂等。
    /// <para>批量那两条路径都把它放在原有的 finally里（与 <c>_batchBusy = false</c> 同一处）
    /// 因此循环里任何一处抛异常、或提前 return，闸门都不会永远关着。</para>
    /// </summary>
    private void EndPluginWriteState()
    {
        _pluginWriteBusy = false;
        try { UpdateBatchBar(); }
        catch (Exception ex) { Logger.LogError("EndPluginWriteState", ex); }
    }

    /// <summary>
    /// 进入「更新中」：落下重入闸 + 把「一键更新」按钮压灰（挡误点）。
    ///
    /// <para>照抄同文件「卸载」那套（<see cref="AdoptUninstallButton"/> / <see cref="EndUninstallState"/>）：
    /// 只在入口改这颗按钮的外观与提示，收尾照原样还原，别的一律不碰。</para>
    ///
    /// <para><c>MiniBtn</c> 样式没有 <c>IsEnabled</c> 触发器、底色又是写死在这一颗上的，
    /// 所以 <c>IsEnabled=false</c> 只负责"点不动"（WPF 里被禁用的元素不参与命中测试、
    /// <c>MouseLeftButtonDown</c> 自然不再触发），"看得出灰"要另外把底色换成次要灰
    /// #8E8E93（映射表里登记过，日间自动转 #6B6B70）。</para>
    ///
    /// <para>标签文字色也一并记下来还原：主题刷新那趟按"最近的不透明底色是不是强调色"决定白字留不留，
    /// 灰底期间它会把这颗的白字改成深色；收尾把底色还原成蓝之后必须连文字色一起还原，
    /// 否则会留下"蓝底深字"。</para>
    /// </summary>
    private void BeginUpdatingState()
    {
        _updatingBusy = true;
        try
        {
            var btn = UpdateAllBtn;
            if (btn == null) return;
            _updateAllBtn = btn;
            _updateAllBtnFace = btn.Background;
            _updateAllBtnTip = btn.ToolTip;
            btn.IsEnabled = false;
            btn.Background = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93));
            btn.ToolTip = "正在更新中，请等这一轮跑完…";
            var label = UpdateAllText;
            if (label != null) _updateAllBtnTextFace = label.Foreground;   // 只记不写
        }
        catch (Exception ex) { Logger.LogError("BeginUpdatingState", ex); }
    }

    /// <summary>
    /// 更新收尾：还原「一键更新」按钮外观 + 复位重入闸。幂等。
    /// <para>两个入口都在 <c>finally</c> 里调它，即循环里任何一处抛异常、或提前 return，
    /// 按钮都不会永远灰着、闸门也不会永远关着（异常路径同样复位）。</para>
    /// <para>没开过（用户点了「取消」/ 入口早退）时 <see cref="_updateAllBtn"/> 为空：
    /// 这时一个属性都不碰——否则会把 XAML 上的提示语清掉。</para>
    /// </summary>
    private void EndUpdatingState()
    {
        _updatingBusy = false;
        var btn = _updateAllBtn;
        _updateAllBtn = null;
        if (btn == null) return;
        try
        {
            if (_updateAllBtnFace != null) btn.Background = _updateAllBtnFace;
            btn.ToolTip = _updateAllBtnTip;
            btn.IsEnabled = true;
            var label = UpdateAllText;
            if (label != null && _updateAllBtnTextFace != null) label.Foreground = _updateAllBtnTextFace;
        }
        catch (Exception ex) { Logger.LogError("EndUpdatingState", ex); }
        finally { _updateAllBtnFace = null; _updateAllBtnTip = null; _updateAllBtnTextFace = null; }
    }

    // ── 自检钩子（只给 SelfTest 用，正常运行路径不受影响）──
    /// <summary>自检用：此刻算不算"更新进行中"（决定重入是否被拒）。</summary>
    internal bool UpdatingBusyForTest() => _updatingBusy;
    /// <summary>自检用：走真实的"进入更新中"入口——断言的是真正在跑的那段代码，不是在这里重写一遍。</summary>
    internal void BeginUpdatingStateForTest() => BeginUpdatingState();
    /// <summary>自检用：走真实的重入闸（断言"重入被拒 + 如实提示"时调它；它只查不开，不会动状态）。</summary>
    internal bool PassUpdateGateForTest() => PassUpdateGate();
    /// <summary>自检用：走真实的"更新收尾"入口（断言"异常路径也复位"时调它）。</summary>
    internal void EndUpdatingStateForTest() => EndUpdatingState();
    /// <summary>自检用：那颗「一键更新」按钮此刻可不可点（置灰 / 恢复的判据）。</summary>
    internal bool UpdateAllBtnEnabledForTest() => UpdateAllBtn?.IsEnabled ?? false;
    /// <summary>重入被拒时的统一提示（防止以后有人把它改回静默 return）。</summary>
    internal static string UpdateBusyTextForTest() => UpdateBusyMessage;

    /// <summary>自检用：此刻插件写闸算不算被占（批量更新 / 批量卸载正在改依赖图）。</summary>
    internal bool PluginWriteBusyForTest() => _pluginWriteBusy;
    /// <summary>自检用：走真实的"占住写闸"入口（断言的是真正在跑的那段代码）。</summary>
    internal void BeginPluginWriteStateForTest() => BeginPluginWriteState();
    /// <summary>自检用：走真实的写闸（只查不开，不会动状态）。</summary>
    internal bool PassPluginWriteGateForTest() => PassPluginWriteGate();
    /// <summary>自检用：走真实的写闸收尾（断言"异常路径也复位"时调它）。</summary>
    internal void EndPluginWriteStateForTest() => EndPluginWriteState();
    /// <summary>写闸被拒时的提示（同样防止被改回静默 return）。</summary>
    internal static string PluginWriteBusyTextForTest() => PluginWriteBusyMessage;

    // ══════════════ 一键更新：把所有有新版本的插件一次更新完 ══════════════
    private async void UpdateAllPlugins_Click(object sender, MouseButtonEventArgs e) => await UpdateAllPluginsAsync();

    private async Task UpdateAllPluginsAsync()
    {
        // 重入闸（本轮修）：循环里每项最长等 10 分钟，这期间按钮与卡片都还能点——
        // 再点一次就是两条 npx 并发改同一个 node_modules / pnpm-lock.yaml。
        // 闸门只有一份（PassUpdateGate），被拒时它自己会说一句"正在更新中"，绝不静默无反应。
        if (!PassUpdateGate()) return;

        try
        {
            // 「一键更新」的目标集 = 「一键更新 N 个」那个计数同一个判据（IsHardUpdatable）。
            // 刻意排除「可选升级」（具名分支 / 标签被移动）：用户要求那种情况"提示出来、由用户决定"，
            //   批量动作不得替他做主；那些插件卡片上仍有单颗「更新到最新提交」按钮，想升随时能升。
            //   （原先这里写的是裸 `HasUpdate == true`，与 UpdatableCount 只是碰巧一致；
            //     加了 Advisory 之后若不同步改，就会出现"按钮说 3 个、实际升 4 个"的错位。）
            var targets = _plugins.Where(p => IsHardUpdatable(UpdateOf(p))).ToList();
            if (targets.Count == 0)
            {
                GuardDialog.Show(
                    _updatesChecking ? "正在检查新版本，请稍后重试。" : "当前没有需要更新的插件。",
                    "一键更新", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 兼容性体检汇总：单独列出不兼容项，一键更新仍会执行（有快照兜底）
            string current = VersionMemory.Pin.Length > 0 ? VersionMemory.Pin : _currentDshVersion;
            var warn = new List<string>();
            var lines = new List<string>();
            foreach (var p in targets)
            {
                var u = UpdateOf(p)!;
                var band = PluginManager.EvaluateBand(u.NewRequirement, current);
                string mark = band switch
                {
                    PluginManager.Compat.Ok => "✅",
                    PluginManager.Compat.Partial => "🟡",
                    PluginManager.Compat.Broken => "⛔",
                    _ => "❔"
                };
                lines.Add($"  {mark} {p.Name}  {u.Installed} → {u.TargetText}");
                if (band == PluginManager.Compat.Broken) warn.Add(p.Name);
            }

            // ★ 引擎忙碌警告（状态③才弹）：压在确认框之前、闸门之后 —— 免得用户白点一次确认。
            if (!await WarnIfEngineBusyAsync()) return;
            var r = GuardDialog.Show(
                $"以下 {targets.Count} 个插件有新版本，一次性更新？\n\n" +
                string.Join("\n", lines) + "\n\n" +
                "✅ 完全兼容　🟡 能用但非作者优先版本　⛔ 不兼容　❔ 作者未声明\n\n" +
                (warn.Count > 0
                    ? $"注意：{string.Join("、", warn)} 声明不支持当前引擎版本，更新后可能报错（可在「快照」页回滚）。\n\n"
                    : "") +
                "将在更新前自动保存快照；全部更新完成后需重启 DSH 才会生效。",
                warn.Count > 0 ? "一键更新 · 注意不兼容" : "一键更新",
                MessageBoxButton.OKCancel,
                warn.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Question);
            if (r != MessageBoxResult.OK) return;

            // 用户已确认，即从这里起进入"更新中"：落下重入闸 + 把「一键更新」按钮压灰。
            //   必须在下面第一条 await（快照）之前——此后 UI 仍可交互，全靠这道闸兜住。
            BeginUpdatingState();

            // ① 统一创建一次快照兜底
            PluginsSummaryText.Text = $"正在给这 {targets.Count} 个插件保存快照…";
            string snapNote = await Task.Run(() => SnapshotBeforePluginBatch(targets.Count));

            // ② 逐个更新（顺序执行，进度写在汇总行 + 事件流）
            int okCount = 0;
            // ★ 真跑过命令、且失败的项数（本轮修）：尾注那两句（"已放宽安全检查…可能是网络…" /
            //   "详细输出已记入日志"）只对**命令真的跑过**的失败成立。缺 Git / 清理未通过 /
            //   定不出更新目标这三种都是"根本没执行命令"，拿它们去说网络或日志都是不实描述。
            int cmdFailCount = 0;
            var failed = new List<string>();
            for (int i = 0; i < targets.Count; i++)
            {
                var p = targets[i];
                var u = UpdateOf(p)!;
                PluginsSummaryText.Text = $"正在更新（{i + 1}/{targets.Count}）：{p.Name} → {u.TargetText}…";
                // ★ 缺 Git 闸门（唯一入口）：这条来源要调系统的 git、而本机 PATH 里确实没有 ⇒ 不跑命令。
                //   位置压在"半截安装自愈"之前：拦下就不该再动磁盘（那一步会把残留目录清掉，清完却装不上）。
                //   与下面"定不出更新目标"那一支同款：记明原因、继续下一项，不中止整批。
                if (BlockedForMissingGit(p.Name, PluginManager.DepSpec(p.Name), "一键更新"))
                {
                    failed.Add(GitMissingItemText(p.Name));
                    continue;
                }
                // 半截安装自愈（与单个更新同一入口）：残留态先清目录，越界/清理失败则记失败、继续下一项
                if (!EnsureNotBrokenInstall(p.Name, out string batchBrokenNote))
                {
                    failed.Add($"{p.Name}（{batchBrokenNote}）");
                    Logger.NoteDiagnosis($"一键更新 {p.Name}：半截安装清理未通过 ⇒ 跳过这一项");
                    continue;
                }
                // 目标一律经 UpdateArgsFor（唯一入口）：npm 则用版本号、git 源则用来源 spec，
                //   显示标签「仓库最新」这类值不可能再被拼进命令。
                string batchArgs = UpdateArgsFor(p, u);
                if (batchArgs.Length == 0)
                {
                    Logger.NoteDiagnosis($"一键更新 {p.Name}：给不出可靠的目标 ⇒ 跳过这一项");
                    failed.Add($"{p.Name}（无法确定更新目标，已跳过）");
                    continue;
                }
                // 跑命令前记下这条 git 依赖当时的提交（理由与单个更新那一处逐字相同，见 UpdatePlugin_Click）：
                //   放到判定那行去读就成了"拿跑完的锁文件跟自己比"，永远相等，即空转仍会被记成成功。
                string gitCommitBefore = PluginManager.ReadInstalledCommit(p.Name);
                var (cmdOk, output) = await RunCommandAsync("npx", batchArgs,
                    timeoutMs: 600000, relaxSupplyChainPolicy: true);

                // 与单个更新同一口径：成败以磁盘上的事实为准，退出码只作参考（判定纯函数见 EvaluateUpdate；
                // git 源另按"提交有没有真的变"判，见上面 gitCommitBefore 的说明）。
                var verdict = EvaluateUpdate(p.Name, u.Latest, cmdOk, output,
                    profileDir: null, expectedCommit: gitCommitBefore);
                bool ok = verdict.Succeeded;
                // 记账：只在**这一档**（ok 为真）盖"更新时间"章 —— 判据就是上面这个 ok（= verdict.Succeeded），
                //   不另立一套"成没成"的判法（本项目要求判据只留一份）。
                //   为什么是这一档：ok 已按磁盘事实合成完毕，包含"命令退出码非零、但磁盘上版本（或 git 源的提交）
                //   已经到位"那一档（即 verdict.NoteDowngraded 的虚惊一场，下面 LogUpdateFalseAlarm 记的就是它）；
                //   其余各档 ok 均为假：磁盘上确实没到目标版本（NotSatisfied）、以及版本不可比时如实回落命令退出码
                //   得到的失败。若改用 cmdOk 另判一次，那批"命令非零、其实已更新"的项就会被漏记。
                //   包名取 p.Name（= PluginManager.Scan 读清单 dependencies 时那个键，见 PluginManager.cs:759/763），
                //   与本地插件页读记账用的键（SortDataOf 传的也是 p.Name）**同一个**，不是任何显示名。
                //   时间格式照 VersionMemory.Now()（yyyy-MM-dd HH:mm）的写法直接给出：它就是记账模块写入的格式，
                //   而那个方法是 private，不去改 VersionMemory。
                if (ok)
                    PluginTimes.StampUpdated(p.Name, DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
                AddEvent(ok ? $"插件已更新：{p.Name} → {u.TargetText}"
                            : $"插件更新失败：{p.Name} · {PluginManager.SupplyChainRelaxNote}",
                    ok ? EventKind.Good : EventKind.Bad);
                Logger.Log($"一键更新 {p.Name} {u.Installed}→{u.Latest}: 命令={cmdOk} "
                         + $"磁盘判定={verdict.Check} 判成功={ok}（{verdict.Note}）\n{output}");
                // 这里不返回命令文本（RunCommandAsync 只回合并后的一段），但拆不出 stderr 也要留一条证据
                if (ok && !cmdOk) LogUpdateFalseAlarm(p.Name, u.Latest, output, verdict.Note);
                if (ok) { okCount++; _pluginUpdates.Remove(p.Name); }
                else
                {
                    // 失败项的**原始命令输出不上界面**（弹窗里只列包名，尾注一句"详细输出已记入日志"）：
                    //   常规失败（退出码非零）由命令层 NoteCommandResult 落盘（MainWindow.xaml.cs），
                    //   这里补一条同款诊断，兜住"命令超时 / 退出码 0 但磁盘上没变动"这类命令层不落盘的失败。
                    LogPluginCmdFailure($"一键更新失败 {p.Name}", batchArgs, output);
                    failed.Add(p.Name);
                    cmdFailCount++;      // 这一项**确实跑过命令**（尾注那两句只对这类失败成立）
                }
            }

            await RefreshPluginsAsync(true);

            // 升级后复查兼容性：新装上的版本可能声明不支持当前引擎，这时给出回滚策略
            var afterRisk = new List<string>();
            foreach (var p in targets)
            {
                try
                {
                    var band = PluginManager.EvaluateBand(p.Requirement, current);
                    if (band == PluginManager.Compat.Broken)
                        afterRisk.Add($"⛔ {p.Name}（要求 {p.Requirement}）");
                    else if (band == PluginManager.Compat.Partial)
                        afterRisk.Add($"🟡 {p.Name}（面向 {p.Requirement}）");
                }
                catch { }
            }

            string rollbackPlan = afterRisk.Count > 0
                ? "\n\n⚠ 有 " + afterRisk.Count + " 个插件与当前引擎版本（" + current + "）不完全匹配：\n" +
                  Shorten(string.Join("\n", afterRisk), 500) + "\n\n" +
                  "回滚策略：\n" +
                  "1) 先重启 DSH 使用一段时间，多数情况下可正常使用；\n" +
                  "2) 若出现报错，去「快照」页选中那份「一键更新 " + targets.Count + " 个插件前」的快照，点一键回滚即可全部还原；\n" +
                  "3) 只需退回个别插件时，在它卡片上点「卸载」再装回旧版本。"
                : "";

            GuardDialog.Show(
                (failed.Count == 0
                    ? $"✅ {okCount} 个插件全部更新完成。"
                    : $"更新完成：成功 {okCount} 个，失败 {failed.Count} 个。\n\n失败：\n" + Shorten(string.Join("\n", failed), 600)) +
                "\n\n" + snapNote + "\n\n" +
                // ★ 这句只对"真的跑过命令"的失败成立（本轮修：原为无条件加 ⇒ 与上面点名 Git 的失败清单
                //   正面矛盾，把人引去查网络）。判据用"真跑过命令的失败数 cmdFailCount"，
                //   而不是"失败项里有没有 Git 那一句"：后者会把"git 源被拦 + npm 源真失败"这种
                //   混合批次里那半句**确实成立**的网络提示也一并吞掉；而只要有一项真跑过命令，
                //   这句就有所指，被拦的那几项在失败清单里各自点明了缺 Git。
                (failed.Count > 0 && cmdFailCount > 0 ? PluginManager.SupplyChainRelaxHint + "\n\n" : "") +
                (okCount > 0 ? "需要重启 DSH 才生效。" : "可用「快照」页回滚到更新前的状态。") +
                // 失败项的原始命令输出不上界面：结论在上面，细节在日志里（用户可在「日志」页翻全文）
                // 走 LogPromise：日志目录不可写时这句承诺要跟着改成实话（否则用户去日志页什么也找不到）
                // ★ 承诺也要跟着"跑没跑过命令"改口（本轮修）：全是"根本没执行命令"的失败（缺 Git /
                //   清理未通过 / 定不出目标）时，这次压根没有命令输出可记 ⇒ 不许再承诺"详细输出"，
                //   改成如实口径 —— 原因确实已由各自的 NoteDiagnosis 落盘。
                (failed.Count > 0
                    ? "\n\n" + (cmdFailCount > 0
                        ? LogPromise("详细输出已记入日志，可在「日志」页查看。")
                        : LogPromise("本次失败的原因已记入日志，可在「日志」页查看。"))
                    : "") +
                rollbackPlan,
                afterRisk.Count > 0 ? "一键更新完成 · 建议留意" : (failed.Count == 0 ? "一键更新完成" : "一键更新（部分失败）"),
                MessageBoxButton.OK,
                afterRisk.Count > 0 || failed.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);

            if (afterRisk.Count > 0)
                AddEvent($"更新后 {afterRisk.Count} 个插件与当前引擎不完全匹配，可按提示回滚", EventKind.Warn);

            if (okCount > 0) await OfferRestartAsync($"更新 {okCount} 个插件");
        }
        catch (Exception ex)
        {
            Logger.LogError("UpdateAllPluginsAsync", ex);
            // 先往事件栏落一条红色事件，再尝试弹窗（与 ClearCache_Click 的 catch 同一个范式）：
            //   GuardDialog.Show 有单例闸门（GuardDialog.cs「已有一个对话框开着」，即直接 return Cancel）。
            //   闸门现在自己会留痕了 —— 事件栏一条橙色 Warn + 异常日志一条 [WARN]，见 GuardDialog.NoteGateSuppressed
            //   （此前它只调 Logger.Log 那个空实现，等于什么痕迹都没有）。
            //   但那句是**通用提示**（"有个框开着，先关掉它"），说不清是哪件事被挡下了 ⇒ 这条点名本次动作
            //   的红色事件仍然必需：没有它，用户只看到"请先关掉那个框"，看不出"一键更新其实出错了"。
            AddEvent("一键更新过程中出错，已中止本次更新；可查看「日志」页了解原因。"
                + "如需恢复更新前的状态，可重启 DSH 后重试，或在「快照」页选取更新前的那份快照回滚",
                EventKind.Bad);
            GuardDialog.Show("一键更新过程中出现错误，已中止本次更新。\n\n"
                + LogPromise("详细原因已记入日志，可在「日志」页查看。"), "一键更新",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        // 异常路径也复位（与批量动作同一个范式）：中途抛出时按钮不该永远灰着、闸门也不该永远关着
        finally { EndUpdatingState(); }
    }

    /// <summary>批量更新前创建一次快照（复用单插件流程，标签注明插件数量，会出现在「快照」页）。</summary>
    private static string SnapshotBeforePluginBatch(int count)
        => SnapshotBeforePluginChange($"DSHGuard：一键更新 {count} 个插件前", SnapshotPolicy.KindFor(GuardAction.UpdatePlugin));

    // ══════════════ 耗时操作的百分比进度 ══════════════
    // 插件安装/卸载/更新可能跑几分钟（要联网拉包），没有进度会让人以为卡死（现场已反馈）。
    // 这里用时间驱动的平滑百分比：起步 5%，按时间爬到 90%，真正结束再补 100%。
    private System.Windows.Threading.DispatcherTimer? _opProgressTimer;
    private DateTime _opProgressStart;

    /// <summary>
    /// 耗时操作的百分比：起步 5%，按秒线性爬升（每秒约 +1.4%，肉眼可见地跳），90% 封顶，
    /// 真正结束时由 EndOpProgress 补到 100%。纯函数，自检可断言"确实一直在变"。
    /// </summary>
    internal static double OpProgressPercent(double seconds)
    {
        if (seconds < 0 || double.IsNaN(seconds)) seconds = 0;
        return Math.Min(90.0, 5.0 + seconds * 1.4);
    }
    private void BeginOpProgress(string label)
    {
        try
        {
            _opProgressStart = DateTime.Now;
            _opProgressTimer?.Stop();
            _opProgressTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(400)
            };
            _opProgressTimer.Tick += (_, _) =>
            {
                double sec = (DateTime.Now - _opProgressStart).TotalSeconds;
                double pct = OpProgressPercent(sec);
                SetProgress($"{label}（{pct:0}%）", pct);
            };
            _progressHideTimer?.Stop();     // 新任务开始：取消上一条的待退场
            _progressHideTimer = null;
            _opProgressTimer.Start();
            SetProgress($"{label}（5%）", 5);
        }
        catch (Exception ex) { Logger.LogError("BeginOpProgress", ex); }
    }

    /// <summary>
    /// 收尾：停表、如实报最终文案、4 秒后自动收起。
    /// <para>
    /// <b>本身不幂等</b>（重复调用不会抛异常，但会把上一次的成败文案覆盖掉），
    /// 所以"异常路径兜底"一律走 <see cref="EndOpProgressIfOpen"/> —— 它只在表还开着时收尾，
    /// 保证正常路径报过的文案不会被 <c>finally</c> 补的话盖掉。
    /// </para>
    /// </summary>
    private void EndOpProgress(string doneText)
    {
        try
        {
            _opProgressTimer?.Stop();
            _opProgressTimer = null;
            SetProgress($"{doneText}（100%）", 100);
            ScheduleProgressHide();
        }
        catch { }
    }

    /// <summary>
    /// 插件操作的异常/提前退出收尾：只有"这张表确实是我开的、而且现在还开着"才补一句如实的
    /// "中断"文案。<paramref name="owned"/> 由调用方在 <see cref="BeginOpProgress"/> 之后置为 true
    /// ——<b>不能只看表开着</b>：这些方法体里夹着确认框/早退 return，别处（启动前体检、批量更新）
    /// 的进度表可能正好开着，只看表就会把别人的进度条误收掉。
    /// <c>_opProgressTimer != null</c> 则负责"正常路径已经收过表了"这一半，即两条一与，天然只放行一次。
    /// 更新 / 重新安装两条路径共用这一个入口，自检也调它 —— 断言的是真正在跑的那段代码，不是抄一份。
    /// （市场安装那一处挂在它自己的收尾入口 <c>EndInstallState</c> 上，判断方式与这里同款。）
    /// </summary>
    private void EndOpProgressIfOpen(bool owned, string abortedText)
    {
        if (owned && _opProgressTimer != null) EndOpProgress(abortedText);
    }

    // ── 自检钩子（只给 SelfTest 用，正常运行路径不受影响）──
    /// <summary>自检用：进度表此刻是否开着（<c>BeginOpProgress</c> 之后、<c>EndOpProgress</c> 之前为 true）。</summary>
    internal bool OpProgressOpenForTest() => _opProgressTimer != null;
    /// <summary>自检用：手动开一次进度表（模拟"插件操作跑到一半"）。</summary>
    internal void BeginOpProgressForTest(string label) => BeginOpProgress(label);
    /// <summary>
    /// 自检用：走真实的异常收尾入口 <see cref="EndOpProgressIfOpen"/>。
    /// 断言"异常路径也一定收尾"时调它 —— 测的是产品代码本身，不是在这里重写一遍判断。
    /// </summary>
    internal void EndOpProgressIfOpenForTest(bool owned, string abortedText) => EndOpProgressIfOpen(owned, abortedText);
    /// <summary>自检用：底部进度文案（进度条收起时为空串）。</summary>
    internal string ProgressTextForTest() => ProgressText?.Text ?? "";

    // 做完就停着不走的进度条会一直挂在底部（现场：引擎都开半天了还写着"正在打开浏览器…"）
    // 即结束 4 秒后自动收起；新任务开始会先取消这条待退场，免得刚开新任务就被清屏。
    private System.Windows.Threading.DispatcherTimer? _progressHideTimer;

    private void ScheduleProgressHide(int delayMs = 4000)
    {
        try
        {
            _progressHideTimer?.Stop();
            _progressHideTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(delayMs)
            };
            _progressHideTimer.Tick += (_, _) =>
            {
                _progressHideTimer?.Stop();
                _progressHideTimer = null;
                SetProgress("", null);      // percent=null 则收起提示与进度条
            };
            _progressHideTimer.Start();
        }
        catch (Exception ex) { Logger.LogError("ScheduleProgressHide", ex); }
    }

    // ══════════ 启动前的「清单体检」：清单里有、盘上没有的包必须先补上再拉引擎 ══════════
    //
    // 【现场 bug】冷启动时引擎先崩、用户先看到失败弹窗，两分钟后才自愈。诊断包原文（1.3.36 自导出）：
    //   引擎状态: 未运行
    //   最近一次启动的引擎输出尾部:
    //     Error: dsh: cannot resolve profile bundle "@furongjun1999/dsh-memory" from the dsh
    //     installation or …\profiles\web; run 'dsh plugin --profile web install' if its dependency is not installed
    // 随后 12:03:06 有一条命令成功：
    //   … plugin --profile web add @furongjun1999/dsh-memory@0.4.7 …，退出码=0
    // 即自愈逻辑本身是好的，只是它挂在「打开插件页 -> 取 loader id」那条流程里（LoadLoaderIdsAsync），
    //   用户不点插件页就永远不跑。这里把它提前到拉起引擎之前。

    /// <summary>本次启动前体检发现的「清单里有、盘上没有」的包名（空 = 没发现）。供启动失败文案与自检读。</summary>
    private IReadOnlyList<string> _preflightMissing = Array.Empty<string>();

    /// <summary>自检用：体检发现的缺包名单。</summary>
    internal IReadOnlyList<string> PreflightMissingForTest() => _preflightMissing;

    /// <summary>
    /// 纯读取：清单体检（不联网、不写盘）。真源不可读时不误判成"缺包"
    /// （判定逻辑在 <see cref="PluginManager.FindMissingFromManifest"/>，那边有自检）。
    /// </summary>
    private static PluginManager.ManifestCheck CheckManifest()
        => PluginManager.FindMissingFromManifest(PluginManager.ProfileDir);

    /// <summary>
    /// 启动前的清单体检 + 一次自愈补装（只补装，绝不删改用户的 package.json）。
    /// 返回空串 = 体检通过（或根本无法进行体检）；返回非空 = 供启动失败文案使用的一句话原因。
    ///
    /// 补装走的是 dsh 自己提示的那条命令（BuildInstallAllArgs = `plugin --profile web install`，
    /// 不带包名 = 按清单装齐），并且必须带放开 pnpm 策略的那组环境变量
    /// （relaxSupplyChainPolicy:true，即注入 minimumReleaseAge=0 / trust-lockfile，
    /// 否则包龄与锁文件策略会把它拦下来，见 PluginManager.PolicyOverride 的现场记录）。
    /// 超时给足 15 分钟，与既有的那条自愈命令同一档。
    /// </summary>
    private async Task<string> PreflightManifestAsync()
    {
        _preflightMissing = Array.Empty<string>();
        var mc = CheckManifest();
        if (!mc.Readable || !mc.HasMissing) return "";     // 读不成清单，即什么都不说（不误判）

        var missing = mc.Missing.ToList();
        _preflightMissing = missing;
        string names = string.Join("、", missing);
        Logger.NoteDiagnosis($"启动前清单体检：清单里登记了 {names}，但 node_modules 下未安装 ⇒ 先补装再拉引擎");

        // ★ 缺 Git 闸门（唯一入口）：这次补装是**按清单整份 install**，而缺的这几个包里若有声明为
        //   git 仓库的，本机没有 git 时就必然要调系统的 git（现场表现就是一句英文 spawn git）
        //   ⇒ 拦下即不执行命令、如实说明缺什么。判据落在"缺的那几条声明"上（比"清单里有没有 git 源"
        //   更窄：已经装好的 git 源插件不需要重新解析，不该因为它挡掉这次补装）。
        //   位置压在下面的半截清理之前：拦下就不该再动磁盘（清理会把残留目录删掉）。
        bool blockedByGit = PluginManager.AnyNeedsGit(missing.Select(PluginManager.DepSpec)) && !GitOnPath();
        if (blockedByGit)
        {
            Logger.NoteDiagnosis($"启动前补装：清单里缺的 {names} 属于代码仓库来源（git 源），"
                               + "但本机 PATH 里没有 git ⇒ 未执行安装命令");
            AddEvent(GitMissingEventText(names), EventKind.Bad);
        }

        // 半截安装自愈（复用 EnsureNotBrokenInstall 唯一入口）：清单判"缺"、但目录残留着（半截态）
        // 的包，直接补装必被 pnpm 的「目录已存在」拒绝 —— 先清残留再装，否则死循环复发。
        // ★ 但缺 Git 时**整段不跑**（与上面那道闸门同一条件、同一方向）：拦下的语义是"这次什么都不做"，
        //   而清理是**真删盘**（EnsureNotBrokenInstall 内部会走 PluginManager.CleanBrokenInstall
        //   删掉 node_modules\<包名>）。命令都不跑却把残留目录删了，正是上面那句"拦下就不该再动磁盘"要防的。
        //   为什么用整段 if 而不是循环里 continue：这一整段就是"未拦截路径"的步骤，与下面那句
        //   `if (!blockedByGit) AddEvent(...)` 同一种写法，一眼看得出"缺 Git 时这一步根本不参与"。
        //   跳过它也不会让状态更差：残留目录本来就是 pnpm 装不进去的死目录（Broken 态），
        //   命令本来就没跑，终态与"这一步没执行过"完全一样。
        if (!blockedByGit)
        {
            foreach (string mp in missing)
            {
                if (!EnsureNotBrokenInstall(mp, out string preNote))
                    Logger.NoteDiagnosis($"启动前补装 {mp}：半截安装清理未通过（{preNote}）");
            }
        }

        // 与插件页那条自愈同一个说法（同一个 AddEvent 口径），只是提前到了启动之前。
        // 缺 Git 时不报这句：上面已经说过真正的原因，这里再说"正在自动重新安装"与实际动作不符。
        if (!blockedByGit)
            AddEvent($"启动前体检：插件清单中登记了 {names}，但本机未安装；正在自动重新安装…", EventKind.Warn);

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var stillMissing = new List<string>(missing);
        bool installed = false;
        string cmdNote = "";      // 命令本身的失败原因（补不上时一起给人看）
        // 缺 Git 拦下时一条命令都不会跑 ⇒ 文案不能说"正在安装"（只改这句话，进度表行为不动）
        BeginOpProgress(blockedByGit ? $"正在检查缺失的插件（{names}）" : $"正在安装缺失的插件（{names}）");
        try
        {
            // 缺 Git 时**不跑这条命令**（原因上面已落盘、也已如实告知）；其余流程一字不动：
            //   仍按磁盘事实复核一遍、仍按"未补齐"如实汇报 —— 绝不因为没跑命令就把结果说成已安装。
            //   cmdNote 带上同一条原因，好让启动失败文案里也点名 Git（用户在那里同样需要知道缺什么）。
            if (blockedByGit)
            {
                cmdNote = GitMissingNote + "。" + GitMissingNextStep + "。";
            }
            else
            {
                var (okInstall, outInstall) = await RunCommandAsync("npx",
                    PluginManager.BuildInstallAllArgs(), home, timeoutMs: 900000, relaxSupplyChainPolicy: true);
                LogDumpFailure(okInstall ? "启动前补装已完成（输出留痕）" : "启动前补装失败",
                    VersionMemory.Spec, home, outInstall);
                if (!okInstall)
                    cmdNote = $"安装命令未成功执行（退出码非 0）：{Tail(StripStreamMarkers(outInstall), 200)}";
            }

            // 不拿退出码当唯一判据：到底补上没有，看磁盘（与插件更新同一口径）。
            // 补装是按清单整份 install，所以这里重新完整体检一次，而不是只看原来那几个。
            var after = CheckManifest();
            stillMissing = after.Missing.ToList();
            installed = after.Readable && stillMissing.Count == 0;
            // 连清单都无法读取，即体检结果作废，按"未能补齐"如实报告（同时说明"无法读取"这一事实）
            if (!after.Readable) installed = false;
        }
        catch (Exception ex)
        {
            Logger.LogError("PreflightManifestAsync", ex);
            cmdNote = "自动重新安装未能完成；" + LogPromise("详细原因已记入日志，可在「日志」页查看。");
        }
        finally { EndOpProgress(installed ? "缺失的插件已安装" : "缺失的插件未能安装"); }

        if (installed)
        {
            _preflightMissing = Array.Empty<string>();
            AddEvent($"启动前体检：{names} 已安装完成", EventKind.Good);
            return "";
        }

        // 无法补齐/超时：不阻塞启动，改为将"是哪个包未安装"写入启动失败的原因文案。
        // 点名的一定是补装之后仍然缺的那几个（可能比一开始更少），不冤枉已经补上的。
        var report = stillMissing.Count > 0 ? stillMissing : missing;
        _preflightMissing = report;
        string note = PluginManager.MissingPackagesNote(report)
                    + (cmdNote.Length > 0 ? $"\n（{cmdNote}）" : "");
        // 缺 Git 拦下时一条安装命令都没跑过 ⇒ 不能说"安装失败"（那隐含"试过了"），改说"未安装"并点名真正的原因；
        // 没拦下时是真跑过命令、真失败了，原话一字不动。
        AddEvent(blockedByGit
            ? $"启动前体检：{string.Join("、", report)} 未安装（{GitMissingNote}），仍继续尝试启动"
            : $"启动前体检：{string.Join("、", report)} 安装失败，仍继续尝试启动", EventKind.Bad);
        Logger.NoteDiagnosis("启动前补装未能补齐 ⇒ 启动失败文案将点名这些包：\n  " + note.Replace("\n", "\n  "));
        return note;
    }

    /// <summary>日志用：把失败命令的合并文本里的流标记去掉，只留原因那一段。</summary>
    private static string StripStreamMarkers(string? combined)
    {
        string s = (combined ?? "").Replace("\r", "");
        int at = s.IndexOf("\n\n错误输出：\n", StringComparison.Ordinal);
        if (at >= 0) s = s.Substring(at + "\n\n错误输出：\n".Length);
        int code = s.LastIndexOf("\n\n（退出码", StringComparison.Ordinal);
        if (code >= 0) s = s.Substring(0, code);
        return s.Trim();
    }

    /// <summary>
    /// 启动失败文案中「启动前体检发现哪个包未安装」的那一段。
    /// 未发现问题时返回空串（不向弹窗加入冗余内容）；有问题时以换行开头，便于直接拼接在诊断文本之后。
    /// </summary>
    private static string PreflightSection(string? preflightNote)
        => string.IsNullOrWhiteSpace(preflightNote) ? "" : $"\n\n⚠ 启动前体检：{preflightNote.Trim()}";

    // ══════════════ 更新插件（体检 -> 快照 -> 更新 -> 提示重启） ══════════════
    private async void UpdatePlugin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not PluginManager.Plugin p) return;
        var upd = UpdateOf(p);
        if (upd == null || !upd.HasUpdate) return;

        // 重入闸（本轮修）：与「一键更新」共用一个闸 —— 两条路径跑的是同一条 npx 命令、
        // 动的是同一份 node_modules / pnpm-lock.yaml。闸门只有一份（PassUpdateGate）。
        // （本轮补：这个闸现在连批量更新 / 批量卸载也一并算"忙"—— 它们占 _pluginWriteBusy。）
        if (!PassUpdateGate()) return;

        string depSpec = PluginManager.DepSpec(p.Name);
        var depKind = PluginSource.Classify(depSpec);

        // ★ 缺 Git 闸门（唯一入口）：清单里这条声明是 git 源、而本机 PATH 里确实没有 git
        //   ⇒ 不执行任何命令，当面说清缺什么。位置与上面那道写闸一致：压在**确认框之前**
        //   （免得用户点了确认、快照都打好了才被告知缺东西），压在 BeginUpdatingState 与
        //   半截安装清理之前（拦下即不动磁盘、不落闸，直接 return 不留悬挂状态）。
        if (BlockedForMissingGit(p.Name, depSpec, "更新插件"))
        {
            GuardDialog.Show(GitMissingDialogText(p.Name), "更新插件", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 只在本方法真的开了表之后才允许 finally 收尾。为什么不能只看 _opProgressTimer：
        // try 体内还夹着确认框，用户在上面点「取消」就 return —— 此刻若别处（启动前体检/批量）
        // 正好开着表，只看"表开着"就会把别人的进度条误收掉。这个局部量是"本方法开的表"的唯一事实。
        bool opOpen = false;

        try
        {
            // ① 兼容性体检：以新版本声明的 dsh 要求对照当前固定版本
            string current = VersionMemory.Pin.Length > 0 ? VersionMemory.Pin : _currentDshVersion;
            var band = PluginManager.EvaluateBand(upd.NewRequirement, current);
            string bandText = band switch
            {
                PluginManager.Compat.Ok => $"✅ 完全兼容（正好是它声明指向的 {current}）",
                PluginManager.Compat.Partial => $"🟡 能用，但不是它优先适配的版本（它面向 {string.Join(" / ", VersionInfo.RequirementVersions(upd.NewRequirement))}，你当前 {current}）",
                PluginManager.Compat.Broken => $"⛔ 不兼容：新版本要求 {upd.NewRequirement}，你当前 {current}",
                _ => "❔ 新版本没有声明 dsh 版本要求，只能实测"
            };

            // ★ 引擎忙碌警告（状态③才弹）：压在确认框之前、闸门之后 —— 免得用户白点一次确认。
            if (!await WarnIfEngineBusyAsync()) return;
            var r = GuardDialog.Show(
                (depKind == PluginSource.Kind.Registry
                    ? $"把插件「{p.Name}」从 {upd.Installed} 更新到 {upd.TargetText}？" +
                      (upd.Published.Length > 0 ? $"（发布于 {upd.Published}）" : "")
                    : $"把插件「{p.Name}」更新到{upd.TargetText}？" +
                      (upd.CommitNote.Length > 0
                          ? $"（{upd.CommitNote}）"
                          : upd.Published.Length > 0 ? $"（仓库最近提交于 {upd.Published}）" : "") + "\n\n" +
                      // 「比的是谁、与发行版不同」——主动写明白，以免用户以为在乱报
                      // （用户：「插件管理页一直在报 09-17 有新提交，但发布页最新是 09-05」）。
                      // 2026-09-19 文案标准化：字段名在这里出现恰好一次（CompareNote 自身不带前缀，
                      //   前缀只由各显示点补），与 UpdateStatusHover 的「比对基准：」逐字同形。
                      (upd.CompareNote.Length > 0 ? $"比对基准：{upd.CompareNote}\n" : "") +
                      (upd.Advisory ? "这是可选升级：本壳不会把它计入「一键更新」，是否升级由你决定。\n" : "") +
                      $"当前：{upd.Installed}\n" +
                      $"来源：{PluginSource.Describe(depSpec)}\n") + "\n\n" +
                $"兼容性体检：{bandText}\n\n" +
                "更新前会自动打一份快照（可在「快照」页回滚）。\n\n" +
                "更新后需要重启 DSH 才生效。是否继续？",
                band == PluginManager.Compat.Broken ? "更新插件 · 注意不兼容" : "更新插件",
                MessageBoxButton.OKCancel,
                band == PluginManager.Compat.Broken ? MessageBoxImage.Warning : MessageBoxImage.Question);
            if (r != MessageBoxResult.OK) return;

            // 用户已确认，即落入"更新中"（闸门 + 按钮压灰），必须在下面第一条 await 之前。
            //   本方法的早退分支（半截残留 / 定不出更新目标）各自 return，闸门由 finally 统一收。
            BeginUpdatingState();

            // ② 快照兜底
            PluginsSummaryText.Text = $"正在给「{p.Name}」保存快照…";
            // 快照标签里用显示标签而不是 Latest：git 源的 Latest 现在是短提交号 / 版本未知这类
            // 实际值，写进快照名里会变成"更新插件 x 0.1.0→版本未知 前"这种怪句子。
            string snapTo = upd.TargetText.Length > 0 ? upd.TargetText : upd.Latest;
            string snapNote = await Task.Run(() => SnapshotBeforePluginUpdate(p.Name, upd.Installed, snapTo));

            // ③ 执行更新
            // 半截安装自愈：目标包若是「目录在、package.json 缺」的残留态，即先清目录再装（否则 pnpm 拒装、死循环）
            if (!EnsureNotBrokenInstall(p.Name, out string updBrokenNote))
            {
                // 如实报结论再弹框：原来这里传空串，进度条直接收起、一句话不留，
                //   用户只看见一个弹窗、底部却"什么都没发生过"。
                //   本处早退的真实原因由 EnsureNotBrokenInstall 给出，它**只有两条 return false 的路**
                //   （见该方法 507-534 行）：①清理越界被拒 ②清理动作没能清掉（多半被占用）。
                //   两条的共同事实是同一个：包处于「目录在、package.json 缺」的半截残留态，而这次没能清掉
                //   ⇒ 所以这里写「上次安装残留未能清理」对两条路都成立，不编第三条原因。
                //   精确原因（含"建议先停止 DSH 引擎"）由下一行的弹窗逐字给出，进度条只报一句结论。
                EndOpProgress($"「{p.Name}」未更新：上次安装残留未能清理");
                GuardDialog.Show(updBrokenNote, "更新插件", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            PluginsSummaryText.Text = $"正在更新「{p.Name}」至 {upd.TargetText}…";
            // 来源类型决定命令长什么样，唯一入口 UpdateArgsFor（npm 则用版本号；git 源则用来源 spec）。
            // 这里以前是 depKind == Registry ? BuildAddArgs(…, upd.Latest) : BuildAddSourceArgs(RefreshSpec(…))：
            // 分流本身是对的，但 upd.Latest 对 git 源一度被写成显示标签「仓库最新」，
            // 一旦走到 BuildAddArgs 那一支就是 `包名@仓库最新`（现场那条被 pnpm 拒掉的命令）。
            string addArgs = UpdateArgsFor(p, upd);
            if (addArgs.Length == 0)
            {
                // 如实报结论再弹框（同上一条早退的理由）：这里真实的早退原因是 UpdateArgsFor
                //   给不出命令（返回空数组）—— 即"定不出可靠的更新目标"，一条命令都没跑过。
                //   与下面弹窗那句话说的是同一个事实，措辞对齐。
                EndOpProgress($"「{p.Name}」未更新：定不出可靠的更新目标，本次未执行任何命令");
                GuardDialog.Show(
                    $"无法为「{p.Name}」确定可靠的更新目标，本次未执行任何命令。\n\n" +
                    "可以在「快照」页确认当前状态，稍后点「刷新」重试；若反复如此，请把日志发给作者。",
                    "更新插件", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BeginOpProgress($"正在更新插件 {p.Name}");
            opOpen = true;
            // 跑命令之前记下这条 git 依赖当时解析到的提交 —— 判定 git 源的成败全靠这一端
            //   （另一端由 EvaluateUpdate 在命令跑完后自己读；只有两端都有才比得出"提交动没动"）。
            //   位置必须在 RunCommandAsync 之前：放到判定那一行去读就等于"拿命令跑完的锁文件跟自己比"，
            //   永远相等，即空转照样判成功（本单要修的缺陷一个都没修掉）。
            //   npm 源的包读出来是空串，EvaluateUpdate 对空串一律走老路，即不干扰版本比对那一套。
            //   与卸载侧同款：PluginManager.EvaluateUninstall 的 existedBefore 也是跑命令前记下的。
            string gitCommitBefore = PluginManager.ReadInstalledCommit(p.Name);
            var (cmdOk, output) = await RunCommandAsync("npx", addArgs,
                timeoutMs: 600000, relaxSupplyChainPolicy: true);

            // ④ 成败以磁盘上的事实为准，不看退出码脸色（现场 bug：pnpm 因某个依赖的构建脚本失败
            //    返回非零，包其实已经装上了；旧口径只看 ExitCode，即弹「更新失败」，卡片却已是新版本）。
            //    目标取 upd.Latest（npm 包的具体版本号）；git 源那一支 Latest 是短提交号或版本号，
            //    版本比对对它没有意义，即 EvaluateUpdate 改为拿锁文件里的提交与
            //    gitCommitBefore 比对（相等则判成功；不等则如实报"命令跑完但提交没变，可重试"），
            //    读不到更新前的提交时仍如实退回命令退出码。
            var verdict = UpdateVerdict(p.Name, upd.Latest, cmdOk, output, gitCommitBefore);
            bool ok = verdict.Succeeded;
            // 记账：只在**这一档**（ok 为真）盖"更新时间"章 —— 判据就是上面这个 ok（= verdict.Succeeded），
            //   不另立一套"成没成"的判法（本项目要求判据只留一份）。
            //   为什么是这一档：ok 已按磁盘事实合成完毕，包含"命令退出码非零、但磁盘上版本（或 git 源的提交）
            //   已经到位"那一档（即 verdict.NoteDowngraded 的虚惊一场，下面 LogUpdateFalseAlarm 记的就是它）；
            //   其余各档 ok 均为假：磁盘上确实没到目标版本（NotSatisfied）、以及版本不可比时如实回落命令退出码
            //   得到的失败。若改用 cmdOk 另判一次，那批"命令非零、其实已更新"的项就会被漏记。
            //   包名取 p.Name（= PluginManager.Scan 读清单 dependencies 时那个键，见 PluginManager.cs:759/763），
            //   与本地插件页读记账用的键（SortDataOf 传的也是 p.Name）**同一个**，不是任何显示名。
            //   时间格式照 VersionMemory.Now()（yyyy-MM-dd HH:mm）的写法直接给出：它就是记账模块写入的格式，
            //   而那个方法是 private，不去改 VersionMemory。
            if (ok)
                PluginTimes.StampUpdated(p.Name, DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            EndOpProgress(ok ? $"「{p.Name}」已更新" : $"「{p.Name}」更新失败");
            AddEvent(ok ? $"插件已更新：{p.Name} → {upd.TargetText}"
                        : $"插件更新失败：{p.Name} · {PluginManager.SupplyChainRelaxNote}",
                ok ? EventKind.Good : EventKind.Bad);
            Logger.Log($"插件更新 {p.Name} {upd.Installed}→{upd.Latest}: 命令={cmdOk} "
                     + $"磁盘判定={verdict.Check} 判成功={ok}（{verdict.Note}）\n{output}");
            // 命令非零、版本却已到位，即这是"虚惊一场"，必须留证据（含 stderr 尾部）。
            // Logger.Log 是空实现（写入不生效），只有 NoteDiagnosis 才真落盘。
            if (ok && !cmdOk) LogUpdateFalseAlarm(p.Name, upd.Latest, output, verdict.Note);
            else if (!ok) LogPluginCmdFailure($"插件更新失败 {p.Name}", addArgs, output);

            GuardDialog.Show(
                (ok
                    ? (verdict.NoteDowngraded
                        ? $"✅ 已更新「{p.Name}」到 {verdict.EffectiveVersion}。\n\n"
                          + $"（{verdict.Note}，一般不影响使用）"
                        : $"✅ 已更新「{p.Name}」到 {upd.TargetText}。")
                    : $"❌ 更新「{p.Name}」失败。") + "\n\n" +
                snapNote + "\n\n" +
                // 原始命令输出不上界面（此前这里直接贴了输出尾部，含包管理器原文、registry 地址与盘符路径）：
                //   失败时全文已落异常日志 —— 本方法在弹窗之前已按成败写过 LogPluginCmdFailure /
                //   LogUpdateFalseAlarm（两条都走 NoteDiagnosis、[WARN] 真落盘），命令层再记一遍原始两个流。
                (ok ? "需要重启 DSH 才生效。"
                    : PluginManager.SupplyChainRelaxHint + "\n\n可用「快照」页回滚到更新前的状态。\n\n"
                      + LogPromise("详细输出已记入日志，可在「日志」页查看。")),
                ok ? "更新完成" : "更新失败",
                MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);

            _pluginUpdates.Remove(p.Name);      // 让下次刷新重新查这个包
            await RefreshPluginsAsync(true);

            if (ok) await OfferRestartAsync($"更新插件 {p.Name}");
        }
        catch (Exception ex)
        {
            Logger.LogError("UpdatePlugin_Click", ex);
            // 先往事件栏落一条红色事件，再尝试弹窗（与 UpdateAllPluginsAsync 的 catch 同一个范式）：
            //   GuardDialog.Show 有单例闸门（GuardDialog.cs「已有一个对话框开着」，即直接 return Cancel）。
            //   闸门现在自己会留痕（事件栏 Warn + 异常日志 [WARN]，见 GuardDialog.NoteGateSuppressed），
            //   但那句是通用提示、说不清是哪件事被挡下了 ⇒ 这条点名本次动作的红色事件仍然必需：
            //   没有它，"单颗更新出错"就只剩一句"请先关掉那个框"，看不出更新本身失败了。
            //   点名 p.Name 是安全的：upd == null 时方法早已 return，能进这个 catch 就一定已经取到插件
            //   （与方法开头 UpdateOf(p) 同一个 p）。
            AddEvent($"更新「{p.Name}」时出错，已中止本次更新；可查看「日志」页了解原因。"
                + "如需恢复更新前的状态，可在「快照」页选取更新前的那份快照回滚",
                EventKind.Bad);
            GuardDialog.Show("更新过程出现错误，已中止本次更新。\n\n"
                + LogPromise("详细原因已记入日志，可在「日志」页查看。"), "更新插件",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        // 异常路径也要收尾（本轮修的：中途抛异常时底部会永远停在「正在更新插件 X（NN%）」，
        // 用户以为程序卡死）。上面的正常路径已经在 EndOpProgress 里报过成败文案，
        // 「早退」两处（半截残留 / 定不出更新目标）也各自报过如实的「未更新」结论了
        // 即到这里 opOpen 仍是 true 只可能意味着"异常把正常收尾跳过了"，此时补一句如实的
        // 收尾文案；正常路径的文案一个字不动。
        finally
        {
            EndOpProgressIfOpen(opOpen, $"「{p.Name}」更新中断");
            EndUpdatingState();     // ← 本轮修：与进度表同一条 finally 收尾，异常路径也一定还原按钮、复位闸门
        }
    }

    /// <summary>
    /// 一次插件更新的结论：命令退出码 + 磁盘上版本比对的合成结果。
    /// <see cref="Measured"/>=true 表示"磁盘版本读到了、能和目标比"，即以 <see cref="Succeeded"/> 为准，
    /// 命令退出码只作参考（现场：pnpm 因依赖构建脚本失败返回非零，包其实已经装好了）。
    /// </summary>
    internal readonly struct UpdateResult
    {
        /// <summary>命令退出码是不是 0。</summary>
        public bool CmdOk { get; }
        /// <summary>磁盘版本能不能与目标比出结论（false 则只能用 <see cref="CmdOk"/>）。</summary>
        public bool Measured { get; }
        /// <summary>磁盘版本是否已达成目标（只有 <see cref="Measured"/>=true 时才有意义）。</summary>
        public bool Succeeded { get; }
        /// <summary>磁盘判定结论（Satisfied / NotSatisfied / Unknown，见 <see cref="PluginManager.VersionCheck"/>）。</summary>
        public PluginManager.VersionCheck Check { get; }
        /// <summary>磁盘上确实未安装该目标版本（仅此一种情况判定为"真失败"）。</summary>
        public bool NotSatisfied => Check == PluginManager.VersionCheck.NotSatisfied;
        /// <summary>判定为成功、但退出码非零 —— 即"显示失败、实际已更新"这类误报（文案须如实且不引起误解）。</summary>
        public bool NoteDowngraded { get; }
        /// <summary>磁盘上读取到的版本（无法读取时为空串）。</summary>
        public string EffectiveVersion { get; }
        /// <summary>判定说明（进日志与弹窗）。</summary>
        public string Note { get; }

        public UpdateResult(bool cmdOk, bool measured, bool succeeded, PluginManager.VersionCheck check,
            bool noteDowngraded, string effectiveVersion, string note)
        {
            CmdOk = cmdOk; Measured = measured; Succeeded = succeeded; Check = check;
            NoteDowngraded = noteDowngraded; EffectiveVersion = effectiveVersion ?? ""; Note = note ?? "";
        }
    }

    /// <summary>
    /// 更新的成败判定（纯函数，便于自检；文件读取之外无副作用）：
    ///   ① 读 <c>&lt;ProfileDir&gt;\node_modules\&lt;包名&gt;\package.json</c> 的 version；
    ///   ② 与目标版本严格比对（<see cref="PluginManager.CompareInstalledToTarget"/>：
    ///      `0.5.9` 相等算达成，`^0.5.8` / `~0.5.8` 落在范围内算达成，git 源不可比）；
    ///   ③ 可比较，即以磁盘事实为准；不可比较（无法读取版本 / git 源 / 上游说"查不到"），即如实退回命令退出码。
    /// 专项：命令退出码为 0 但版本不可比较（例如清单被改为 `^0.5.8`，目标 0.5.9，pnpm 沿用 0.5.8 不做变更）
    /// 时判定为成功，并在说明中写明"pnpm 沿用了现有版本"—— 此为事实，不应报告为失败。
    ///
    /// git 源分流：清单里那条依赖是 <c>git+…</c> / <c>github:o/r</c> 时，
    ///   不做任何 npm 版本比较（理由见下）；成败改看锁文件里那条依赖解析到的提交
    ///   有没有变成"更新前记下的那个提交"，变成则判成功，没变则判失败（不论命令退出码是几）。
    ///   这一个判据由三条更新路径（单个 / 一键 / 批量）共用。
    /// </summary>
    /// <param name="expectedCommit">
    /// 跑命令之前从 <c>pnpm-lock.yaml</c> 读到的、这条 git 依赖当时解析到的那个提交
    /// （<see cref="PluginManager.ReadInstalledCommit"/>）。
    ///
    /// 为什么要把这个值传进来、而不是在方法里现读：判定需要的是"这次更新有没有让提交真的动起来"
    ///   —— 只有一个端点（命令跑完后锁文件里的那个提交）比不出任何东西，必须两端都有。
    ///   这与卸载那条同款：<c>EvaluateUninstall</c> 的 <c>existedBefore</c> 也是调用方在跑命令之前记下的
    ///   （见 <see cref="PluginManager.EvaluateUninstall"/> 的说明）。
    ///
    /// 传空 / 不传（默认 <c>null</c>），即与旧口径完全一致：git 源回落命令退出码（<c>Measured=false</c>）
    ///   因此老调用点与自检样本零改动。npm 源不受本参数影响（版本比对那一套一字未改）。
    /// </param>
    internal static UpdateResult EvaluateUpdate(string packageName, string targetVersion,
        bool cmdOk, string? commandOutput, string? profileDir = null, string? expectedCommit = null)
    {
        string dir = profileDir ?? PluginManager.ProfileDir;
        string installed = PluginManager.ReadInstalledVersion(dir, packageName);

        // git 源一律不参与 npm 版本比较（三条更新路径共用这一个判据，见下面的说明）。
        //   为什么必须在这里拦：git 源插件的版本位现在放的是"已装版本号或短提交号"，
        //   万一那个值和磁盘上的版本恰好相等，版本比对就会判成 Satisfied，即命令明明失败也报"更新成功"。
        var declared = PluginManager.DepSpecIn(Path.Combine(dir, "package.json"), packageName);
        if (PluginSource.Classify(declared) is PluginSource.Kind.GitCommit
                                             or PluginSource.Kind.GitRef
                                             or PluginSource.Kind.GitBare)
        {
            // 能读事实就读事实（本单修的就是这里）：
            //   更新前记下的那个提交（expectedCommit）与命令跑完后锁文件里的提交比对 ——
            //   git 源的"装上了没有"唯一凭据就是锁文件里那一条（改没改 ref / 落在哪个提交，
            //   见 PluginManager.ReadInstalledCommit 的说明），命令退出码只能说明"pnpm 没报错"。
            //
            //   现场缺陷（用户实测「dsh watcher 更新以后又报更新」）：`add <仓库地址>`（不带 ref）
            //   让 pnpm 认为旧提交已满足 spec，即跳过解析（Lockfile is up to date, resolution step is skipped）
            //   结果是退出码 0、锁文件也被重写，提交却没动。旧口径只看退出码，即记成"已更新"，
            //   用户下次刷新又看到"有新提交"。命令构造已改（PluginManager.BuildUpdateArgs 走
            //   `plugin --profile web update {包名}`），此处补上判定侧的同一件事。
            string commitAfter = PluginManager.ReadInstalledCommit(packageName, dir);
            string commitBefore = (expectedCommit ?? "").Trim();
            if (commitBefore.Length > 0)
            {
                // 相等则判没升级、判失败：这是本方法里最容易写反的一处，写反了就是"空转报成功、
                //   真成功报失败"，而且两种错都看不出来（退出码两种情况下都是 0）。
                //   判据用 PluginManager.SameCommit（= 转发 PluginSource.SameSha，判据只有那一份）：
                //   只比前 7 位、忽略大小写，任一侧为空 / 不足 7 位一律 false，即短号与完整 sha 天然可比
                //   （锁里可能是 40 位 codeload tarball 末尾，远端查询给的是 7 位短号）。
                //   别把它读成"有没有新提交" —— 那是 PluginSource.DecideUpdate /
                //   PluginManager.GitRemoteVerdict 的事，两回事，别对调。
                bool advanced = !PluginManager.SameCommit(commitBefore, commitAfter);

                if (advanced)
                    return new UpdateResult(cmdOk, true, true, PluginManager.VersionCheck.Satisfied,
                        !cmdOk, installed,
                        $"「{packageName}」是 git 源（{PluginSource.Describe(declared)}）⇒ 按提交判定："
                        + "该跟的提交已经落到本机");

                return new UpdateResult(cmdOk, true, false, PluginManager.VersionCheck.NotSatisfied,
                    false, installed,
                    $"「{packageName}」是 git 源（{PluginSource.Describe(declared)}）⇒ 按提交判定："
                    + "命令跑完了，但本机解析到的提交还是原来那个，本次没有真正升级，可以重试");
            }

            // 拿不到"更新前的提交"（读不到锁文件 / 本包在锁里没有提交记录），即如实回落命令退出码。
            // 与全文同一条精神：能读事实就读事实，读不到才回落；绝不把"读不到"当成"成功"或"失败"。
            return new UpdateResult(cmdOk, false, cmdOk, PluginManager.VersionCheck.Unknown,
                false, installed,
                $"「{packageName}」是 git 源（{PluginSource.Describe(declared)}）⇒ 没有版本号可比，"
                + "也没读到更新前的提交，只按命令结果判定");
        }

        var v = PluginManager.CompareInstalledToTarget(installed, targetVersion);

        bool measured = v.Check != PluginManager.VersionCheck.Unknown;
        string note = v.Note;
        bool succeeded = measured ? v.Satisfied : cmdOk;
        if (measured && !v.Satisfied && cmdOk)
        {
            succeeded = true;      // 命令成功、版本没动（pnpm 认定清单声明已被满足），即不算失败
            note = $"{note}；本次安装是成功的，说明它按清单声明的版本范围沿用了现有版本";
        }
        return new UpdateResult(cmdOk, measured, succeeded, v.Check, succeeded && !cmdOk, installed, note);
    }

    /// <summary>
    /// 单插件更新入口：合成结论并写一条生效证据（Logger.Log 是空实现，必须走 NoteDiagnosis）。
    ///
    /// <paramref name="expectedBefore"/> = 跑命令之前锁文件里这条 git 依赖解析到的提交
    /// （<see cref="PluginManager.ReadInstalledCommit"/>），原样转交给 <see cref="EvaluateUpdate"/>：
    /// 只有它能让 git 源的成败从"看退出码"变成"看提交有没有真的变"。
    /// 默认 <c>null</c>，即与旧口径完全一致（老调用点零改动）。
    /// </summary>
    private static UpdateResult UpdateVerdict(string packageName, string targetVersion, bool cmdOk, string? output,
                                              string? expectedBefore = null)
    {
        var r = EvaluateUpdate(packageName, targetVersion, cmdOk, output, null, expectedBefore);
        Logger.NoteDiagnosis(
            $"更新判定 {packageName}：命令退出码0={cmdOk} · 磁盘判定={(r.Measured ? "可判" : "不可判")}"
            + $" · 磁盘版本=「{(r.EffectiveVersion.Length > 0 ? r.EffectiveVersion : "(读不到)")}」"
            + $" · 目标={targetVersion} · 结论={(r.Succeeded ? "成功" : "失败")}\n  {r.Note}");
        return r;
    }

    /// <summary>
    /// 命令退出码非零、但版本确实已到位，即落一条"虚惊一场"的诊断（含 stderr 尾部）。
    /// 现场原文（异常-20260916-111220.log）：
    ///   … plugin --profile web add dsh-mnemon@0.5.9 … 退出码=1
    ///     stderr 尾部：… node_modules\cpu-features install: Failed
    /// 有了这条，下次再收到"显示失败、版本却更新了"的反馈，直接看日志就知道是哪一步在报错。
    /// </summary>
    private static void LogUpdateFalseAlarm(string packageName, string targetVersion, string? combined, string note)
    {
        var (so, se, split) = SplitCommandStreams(combined);
        string soTail = Tail(so, 300);
        string seTail = Tail(se, 300);
        Logger.NoteDiagnosis(
            $"更新「{packageName}」的**命令本身报了非零退出码**，但磁盘上版本已经到位（目标 {targetVersion}）"
            + $" ⇒ 判成功，不报失败。判定依据：{note}\n"
            + (split
                ? $"  命令 stderr 尾部（多半是某个依赖的构建脚本没跑成功，一般不影响使用）：\n    {(seTail.Length > 0 ? seTail : "(空)")}"
                : $"  命令输出尾部（stdout/stderr 未拆分）：\n    {(soTail.Length > 0 ? soTail : "(空)")}"));
    }

    /// <summary>
    /// 插件命令真的失败时，把 stderr 尾部落盘（<see cref="Logger.Log"/> 是空实现）。
    /// 失败原因几乎全写在 stderr（pnpm 的 os error 32 / ERR_PNPM_… 都在那儿），
    /// 而 RunCommandAsync 的合并文本把两段拼在一起、只留 800 字，即分开记才看得出原因在哪一行。
    /// </summary>
    private static void LogPluginCmdFailure(string what, string args, string? combined)
    {
        var (so, se, split) = SplitCommandStreams(combined);
        Logger.NoteDiagnosis(
            $"{what}：npx {args}\n"
            + (split
                ? $"  stdout 尾部：{(Tail(so, 300).Length > 0 ? Tail(so, 300) : "(空)")}\n"
                  + $"  stderr 尾部：{(Tail(se, 300).Length > 0 ? Tail(se, 300) : "(空)")}"
                : $"  输出尾部（stdout/stderr 未拆分）：{(Tail(so, 300).Length > 0 ? Tail(so, 300) : "(空)")}"));
    }

    /// <summary>
    /// 更新插件前的快照：使用本程序自带的快照引擎（快照会出现在「快照」页中，可直接回滚）。
    /// 返回供界面显示的结果说明。
    /// </summary>
    private static string SnapshotBeforePluginUpdate(string name, string from, string to)
        => SnapshotBeforePluginChange($"DSHGuard：更新插件 {name} {from}→{to} 前", SnapshotPolicy.KindFor(GuardAction.UpdatePlugin));

    private static string SnapshotBeforePluginChange(string label, string? kind = null)
    {
        string note = SnapshotBeforePluginChangeCore(label, kind);
        AddEvent($"已创建快照备份（{label.Replace("DSHGuard：", "")}）", EventKind.Update);
        return note;
    }

    /// <summary>自检用：直接走一遍「动作前存快照」的核心逻辑。</summary>
    internal static string SnapshotForTest(string label, string? kind = null)
        => SnapshotBeforePluginChangeCore(label, kind);

    /// <summary>
    /// 改动配置/插件前先保存一份快照：直接调用本程序自带的快照引擎，不再依赖 undo 插件。
    /// kind 留空按「自动」记（插件动作）；换版本走「换版本前」。
    /// </summary>
    private static string SnapshotBeforePluginChangeCore(string label, string? kind = null)
    {
        try
        {
            var snap = SnapshotManager.Create(kind ?? SnapshotManager.KindAuto, label.Replace("DSHGuard：", ""));
            if (snap == null) return "⚠ 快照保存失败（可到「日志」页查看原因）";
            return $"📸 已存快照 {snap.LocalTime}（可在「快照」页回滚）";
        }
        catch (Exception ex)
        {
            Logger.LogError("SnapshotBeforePluginChange", ex);
            return "⚠ 快照保存失败，本次未创建备份（可查看「日志」页了解原因）";
        }
    }

    private void DisablePlugin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not PluginManager.Plugin p) return;

        // 执行禁用前，先按"已读取的 id 表"补充一次：卡片若来自重扫，其 LoaderId 可能为空，
        // 而 id 表实际已在内存中 —— 不补充将异常弹出"无法读取内部标识"（现场根因）。
        PluginManager.ApplyLoaderIds(new[] { p }, _loaderIds);

        string id = PluginManager.LoaderIdFor(p);

        // 读不到内部标识，即绝不放行：DSH 按内部标识匹配，写进包名不生效（现场已验证）。
        // 这一档不是"确认框"，而是"这件事现在做不到"的说明：按钮不该出现「继续」，
        // 也不能再往下走——否则用户点完确认却什么都没发生，和点了没生效的假成功一样糟。
        if (id.Length == 0)
        {
            AddEvent($"禁用插件未执行：未读取到「{p.Name}」的内部标识", EventKind.Warn);
            GuardDialog.Show(
                $"暂时无法禁用「{p.Name}」。\n\n" +
                "本次未读取到该插件的内部标识，为避免写入无效记录，已取消这次操作。\n" +
                "请先点「刷新」重新读取插件信息，然后再试一次。",
                "无法禁用插件", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var r = GuardDialog.Show(
            $"禁用插件「{p.Name}」？\n\n" +
            "会在配置文件里写入禁用记录（修改前自动备份），重启 DSH 后生效。",
            "确认禁用插件", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (r != MessageBoxResult.OK) return;

        string msg = PluginManager.Disable(p);
        // 只有真写进去了才报"已禁用"：失败时如实记红色事件，不出现"点了但没生效"的假成功
        bool done = msg.StartsWith("已禁用", StringComparison.Ordinal)
                 || msg.Contains("已经是禁用状态", StringComparison.Ordinal);
        AddEvent(done ? $"已禁用插件 {p.Name}" : $"禁用插件失败：{p.Name}",
                 done ? EventKind.Warn : EventKind.Bad);                 // 停用 = 橙色，失败 = 红色
        GuardDialog.Show(msg, done ? "禁用插件" : "禁用失败", MessageBoxButton.OK,
                 done ? MessageBoxImage.Information : MessageBoxImage.Warning);
        _ = RefreshPluginsAsync(true);
    }

    // ══════════ 卸载的「用户已请求停止」（对抗性复查【中 2】）══════════
    /// <summary>
    /// 卸载进行中（在跑那条命令、尚未收尾）。
    /// 用途有两个：① 让「卸载」按钮在运行期间变成「停止卸载」的入口；② 自检可断言状态收放。
    /// </summary>
    private bool _uninstalling;
    /// <summary>正在卸载哪一个（按对象引用比对，与安装侧 <c>_installingPlugin</c> 同一套判据）。</summary>
    private PluginManager.Plugin? _uninstallingPlugin;
    /// <summary>正在卸载的那个包名（收尾复位；与 <see cref="_uninstalling"/> 同生共死）。</summary>
    private string _uninstallingName = "";
    /// <summary>运行期间被改成「停止卸载」的那颗按钮（收尾时恢复原样）。</summary>
    private Button? _uninstallBtn;
    /// <summary>
    /// 上面那颗按钮**被接管之前**的底色（先存后还；收尾时原样还回去）。
    /// <para>
    /// 存的是"这一颗装置进来时的颜色"，不是某个写死的颜色：本地插件页那颗「卸载」是红的
    /// （<c>MiniButton("卸载", "#FF3B30")</c>），但同一个入口将来也可能被别的底色按钮借去用，
    /// 一律还原成红等于把"谁借的、还给谁"这条纪律改成"统统还成红色"。
    /// </para>
    /// </summary>
    private Brush? _uninstallBtnFace;

    /// <summary>
    /// 「停止」形态的统一橙色（**唯一常量**，两处"停止"共用）。
    /// <para>
    /// 安装侧（寻找插件页那颗「停止」，见 <c>MainWindow.Market.cs</c> 的 <c>PaintInstallBtn</c>）
    /// 与卸载侧（本文件的 <see cref="AdoptUninstallButton"/>）都是"点了就中止本次操作"，
    /// 颜色必须一致 —— 所以两边引用的是**这一个**常量，不许各写一份字面量。
    /// 取值沿用本壳既有的橙色 #FF9F0A（与「禁用插件」「取消本次升级」等同一支橙）。
    /// </para>
    /// </summary>
    internal static readonly Color StopButtonColor = Color.FromRgb(0xFF, 0x9F, 0x0A);

    /// <summary>
    /// 这张卡片是不是"正在卸载的那一条"。按对象引用比对：
    /// <c>RenderPlugins</c> 每次重绘都复用同一批 <see cref="PluginManager.Plugin"/> 实例，所以引用可靠。
    /// 用途：刷新/重绘后新造的按钮要能认出自己就是"卸载中"那颗，而不是又冒出一颗纯「卸载」按钮
    /// （那会让正在跑的那次卸载又没有停止入口）。
    /// </summary>
    private bool IsUninstallingThis(PluginManager.Plugin p)
        => _uninstalling && ReferenceEquals(_uninstallingPlugin, p);

    /// <summary>
    /// 「用户点了停止卸载」这件事的一次性标记（与安装侧的 <c>_installStopRequested</c> 同型同范式）。
    /// <para>
    /// 为什么必须有它：<see cref="StopRunningCommand"/> 是把进程杀掉，正在等它的
    /// <c>RunCommandCancelableAsync</c> 随后必然返回 <c>ok=false</c> —— 从返回值上，
    /// "被用户停止"与"命令自己失败"长得一模一样。而失败分支里有一条"去掉策略参数再试一次"的兜底，
    /// 于是点停止的现场表现是杀了又卸（与安装侧修掉的 bug ① 完全同型）。
    /// </para>
    /// <para>
    /// 为什么不能复用安装侧那个标记：安装与卸载是两条独立的流程，共用一个标记会互相误伤
    /// （卸载消费掉安装留下的真值、或反之）。安装侧的语义一个字都不动。
    /// </para>
    /// </summary>
    private bool _uninstallStopRequested;

    /// <summary>
    /// 消费一次"用户已请求停止卸载"标记：读一次就清掉。
    /// 必须消费（而不是一直留着）：否则下一次卸载的命令万一真的失败，会被这个陈旧的标记
    /// 误判成"用户停的"、连重试都不做。每次卸载只认自己那一次停止。
    /// </summary>
    private bool ConsumeUninstallStopRequest()
    {
        bool v = _uninstallStopRequested;
        _uninstallStopRequested = false;
        return v;
    }

    /// <summary>被用户停止时的统一文案（事件栏与进度条同一句；不出现命令写法）。</summary>
    private const string UninstallStoppedMessage = "卸载已按请求停止";

    /// <summary>
    /// 卸载失败之后该不该自动重试的判定（纯函数，与 <c>UninstallPlugin_Click</c> 里那条分支同源）。
    /// 判据两件事实：包目录还在（<paramref name="removed"/> 为假）且这次失败不是用户主动停止造成的。
    /// 被用户停止，即用户已明确不要这次卸载了，绝不重试。
    /// </summary>
    internal static bool ShouldRetryUninstallAfterFailure(bool cmdOk, bool removed, bool userStopped)
        => !cmdOk && !removed && !userStopped;

    /// <summary>自检用：走真实的"消费一次"入口。</summary>
    internal bool ConsumeUninstallStopForTest() => ConsumeUninstallStopRequest();
    /// <summary>自检用：把"用户已请求停止卸载"标记当成真落一次（走的就是点停止那条路）。</summary>
    internal void RequestUninstallStopForTest() => _uninstallStopRequested = true;
    /// <summary>自检用：此刻标记还在不在（消费过 = 不会误伤下一次卸载）。</summary>
    internal bool UninstallStopPendingForTest() => _uninstallStopRequested;
    /// <summary>自检用：此刻算不算"卸载进行中"（决定按钮是不是「停止卸载」入口）。</summary>
    internal bool UninstallingForTest() => _uninstalling;
    /// <summary>被用户停止时的统一文案（防止以后有人把它改回"卸载失败"）。</summary>
    internal static string UninstallStoppedTextForTest() => UninstallStoppedMessage;

    /// <summary>
    /// 卸载期间把卡片上那颗按钮变成「停止卸载」的有效入口，收尾时恢复。
    /// <para>
    /// 为什么必须给入口：复查实测卸载期间 <c>StopRunningCommand()</c> 拿不到正在跑的进程
    /// （卸载原来走 <c>RunCommandAsync</c>，它从不设置 <c>_runningCmd</c>），即用户根本停不掉。
    /// 现在卸载改走 <c>RunCommandCancelableAsync</c>（它会设置 <c>_runningCmd</c>），
    /// 但市场上那颗"悬停变停止"的按钮受 <c>_installing</c> 管辖、管不到卸载，即这里给卸载自己的入口。
    /// </para>
    /// <para>只改这一颗按钮的文案、提示与底色；文案逐字不变（「停止卸载」四个字），原色先存后还。</para>
    /// <para>
    /// ⚠ 底色改成橙色（本单）：与寻找插件页那颗「停止」同一支橙（<see cref="StopButtonColor"/>）——
    ///   两处都是"点一下就中止本次操作"，颜色不该一个红一个橙。
    ///   原底色先存进 <see cref="_uninstallBtnFace"/>，由 <see cref="EndUninstallState"/> 原样还回去
    ///   （绿色来的还绿色、红色来的还红色，绝不一律还原成某一种）。
    /// </para>
    /// </summary>
    private void AdoptUninstallButton(Button btn, string pluginName)
    {
        // ⚠ 同一颗按钮被接管第二次时**不重存原色**：那一刻它已经是橙的了，再存一次就会把橙色
        //   当成"原色"（收尾把橙还成橙，红按钮永远回不到红）。判据必须在 _uninstallBtn 被覆盖**之前**取。
        bool alreadyAdopted = ReferenceEquals(_uninstallBtn, btn);
        _uninstallBtn = btn;
        _uninstallingName = pluginName;
        try
        {
            // 先存后还：存的是这颗按钮**被接管之前**的真实底色（见 _uninstallBtnFace 的注释）。
            if (!alreadyAdopted) _uninstallBtnFace = btn.Background;
            btn.Content = "停止卸载";
            btn.ToolTip = $"正在卸载「{pluginName}」。点这里可中止本次卸载（已下载/已改动的部分不会回滚）。";
            ApplyStopFace(btn);
        }
        catch (Exception ex) { Logger.LogError("AdoptUninstallButton", ex); }
    }

    /// <summary>
    /// 把一颗按钮的底色落成指定画刷（**唯一落色处**，接管与还原都走它）。
    /// <para>
    /// ⚠ 为什么要写到**两层**上：<see cref="RoundBtn"/> 的模板把内层 <see cref="Border"/>.Background
    /// 绑到 <c>Control.Background</c>，而 <c>ThemeManager</c> 每次刷主题都会给 <c>Border.Background</c>
    /// 赋一个常量画刷 —— 那一赋就把模板里的绑定换成了常量 ⇒ 绑定失效、渲染面被钉死在当时的颜色上，
    /// 此后只改 <c>Control.Background</c> 再也画不动底色（<c>MainWindow.Market.cs</c> 的
    /// <c>ApplyInstallLook</c> 对同一现象有逐条论证）。所以这里把同一支画刷同时钉到两层上。
    /// </para>
    /// <para>
    /// ⚠⚠ **还原也必须走这里**（本单修掉的一处自伤）：接管时写了两层、还原时若只还
    /// <c>Control.Background</c>，那颗 Border 会**永远留在橙色上** —— 绑定已经断了，
    /// 谁也不会再把颜色推回去。接管与还原用同一个入口，两层就一定是同进同出的。
    /// </para>
    /// <para>
    /// 取不到 Border（模板还没实例化）时只写 <c>Control.Background</c> 也不会抛 ——
    /// 那种情况下绑定仍然有效，会自己取到正确颜色。
    /// </para>
    /// </summary>
    private static void ApplyButtonFace(Button btn, Brush brush)
    {
        btn.Background = brush;
        try
        {
            if (VisualTreeHelper.GetChild(btn, 0) is Border surface
                && !ReferenceEquals(surface.Background, brush))
                surface.Background = brush;
        }
        catch { /* 模板还没实例化等情形一律跳过：绑定仍有效，颜色照样对 */ }
    }

    /// <summary>把一颗按钮画成「停止」形态的橙色（与安装侧 <c>PaintInstallBtn</c> 同一支橙）。</summary>
    private static void ApplyStopFace(Button btn) => ApplyButtonFace(btn, new SolidColorBrush(StopButtonColor));

    /// <summary>卸载收尾：把按钮恢复成原来的「卸载」与原底色，并复位"进行中"状态。幂等。</summary>
    private void EndUninstallState()
    {
        _uninstalling = false;
        _uninstallingPlugin = null;
        _uninstallingName = "";
        // 一次性标记跟着一起清：卸载已经结束，就不该再有"用户请求过停止"悬在那里
        // （留着会让下一次卸载的真失败被误判成"用户停的"、连重试都不做）。
        _uninstallStopRequested = false;
        var btn = _uninstallBtn;
        _uninstallBtn = null;
        var face = _uninstallBtnFace;
        _uninstallBtnFace = null;
        try
        {
            // 原底色先存后还（见 _uninstallBtnFace）：红色卸载按钮仍是红色，将来别的底色借这个入口
            // 也照样还它自己的颜色。⚠ 必须走 ApplyButtonFace 把**两层**一起还回去（理由见那边的注释）；
            // 取不到原色（没接管过）就不碰底色，免得把主题色擦成透明。
            if (btn != null)
            {
                btn.Content = "卸载";
                btn.ToolTip = null;
                if (face != null) ApplyButtonFace(btn, face);
            }
        }
        catch (Exception ex) { Logger.LogError("EndUninstallState", ex); }
    }

    /// <summary>
    /// 卸载的执行层：走 <c>RunCommandCancelableAsync</c>（不是 <c>RunCommandAsync</c>），
    /// 因为只有它会设置 <c>_runningCmd</c> —— 那是 <see cref="StopRunningCommand"/> 唯一能杀的对象。
    /// 参数与超时与原来那条完全一致，成功/失败语义不变。
    /// </summary>
    private Task<(bool Success, string Output)> RunUninstallCommandAsync(string args)
        => RunCommandCancelableAsync("npx", args, timeoutMs: 600000, relaxSupplyChainPolicy: true);

    private async void UninstallPlugin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not PluginManager.Plugin p) return;

        // 卸载进行中，分两种情形判 —— 判据必须是"**是不是这一条**"（IsUninstallingThis 按对象引用比对），
        // 不能只看全局的 _uninstalling：
        //   ① 点的是正在卸载的那一条的按钮 ⇒ 它已被 AdoptUninstallButton 改成「停止卸载」，
        //      此刻的语义就是"我要停下来"。
        //   ② 点的是**别的卡片**的按钮 ⇒ 它看着还是「卸载」（卡片渲染只接管 IsUninstallingThis 那一颗），
        //      那就绝不能当成停止信号 —— 否则会"停掉 A、而 B 一点没卸"，即按钮文字承诺的动作
        //      与实际执行的动作相反（本单缺陷②：用户意图被替换）。这时只如实说一句，
        //      并且**绝不静默 return**（同项目所有被拒入口都是"说一句再返回"）。
        if (IsUninstallingThis(p))
        {
            // 顺序不能反：StopRunningCommand 一返回，等命令的那条 await 马上就会带着 ok=false 继续往下跑，
            // 与安装侧 MarketInstall_Click 的停止分支同一个范式。
            _uninstallStopRequested = true;
            bool stopped = StopRunningCommand();
            AddEvent(stopped ? $"已按你的要求停止卸载「{_uninstallingName}」"
                             : $"卸载「{_uninstallingName}」已经结束了", EventKind.Warn);
            return;
        }
        // 别的卡片点「卸载」：卸载同一时刻只可能有一条在跑 ⇒ 照抄同项目那句写闸统一提示（不另编第二句文案）。
        //   与下面的 PassPluginWriteGate 是**两条互斥的触发条件**：_uninstalling 为真时走本支；
        //   _uninstalling 为假、闸被批量/更新占着时走下面那一支。同一时刻只会说出其中一句。
        if (_uninstalling)
        {
            AddEvent(PluginWriteBusyMessage, EventKind.Warn);
            return;
        }

        // ★ 插件写闸（本单缺陷①）：本方法下面要跑的 npx 会改 node_modules / pnpm-lock.yaml（整棵依赖图），
        //   与「一键更新 / 单颗更新 / 重新安装」（_updatingBusy）、「批量更新 / 批量卸载」（_pluginWriteBusy）、
        //   市场安装、回滚重装是同一类命令 ⇒ 先过统一入口。原先这里既不查闸也不占闸，于是上面任意一条
        //   在跑时卡片上这颗「卸载」照旧亮着、点得动 ⇒ 两条 npx 并发改同一份依赖图（pnpm store 锁冲突、
        //   插件清单被写坏）—— 正是本项目注释里写的后果。
        //   位置与市场安装 / 回滚那两处一致：放在**确认框之前** —— 免得用户点了确认、命令都要跑了才被告知"正忙"。
        //   顺序也刻意排在"停止卸载"那一支之后：卸载进行中时，下面那句 Begin 落的正是这道闸，
        //   若把这句提到最前面，"停止卸载"就会被自己的闸挡掉、再也点不到。
        //   闸门只有一份（PassPluginWriteGate，只查不开），被拒时它自己会说那句话，这里不另编文案、绝不静默 return。
        if (!PassPluginWriteGate()) return;

        // ★ 引擎忙碌警告（状态③才弹）：压在确认框之前、写闸之后 —— 免得用户白点一次确认。
        if (!await WarnIfEngineBusyAsync()) return;
        var r = GuardDialog.Show(
            $"卸载插件「{p.Name}」？\n\n" +
            $"将把「{p.Name}」从 DSH 的插件清单中移除（改动前会自动备份配置）。\n" +
            "若 DSH 正在运行，安装可能因文件被占用而失败（建议先停止引擎）。",
            "确认卸载插件", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (r != MessageBoxResult.OK) return;

        if (SnapshotPolicy.NeedSnapshot(GuardAction.UninstallPlugin))
            SnapshotBeforePluginChange($"DSHGuard：卸载插件 {p.Name} 前", SnapshotPolicy.KindFor(GuardAction.UninstallPlugin));
        PluginsSummaryText.Text = $"正在卸载 {p.Name}…";
        string unArgs = PluginManager.BuildUninstallArgs(p.Name);

        // 本单 H2 的第一道闸：包名不合法就什么都不执行（含 `..`、越界分量、空白、超长、shell 元字符）。
        //   BuildUninstallArgs 对非法包名返回空串（同一条白名单），空串再去跑命令只会得到一次无意义的失败；
        //   更糟的是 VerifyUninstalled 的 Path.Combine 不净化输入，即 `../../etc` 这类包名会让
        //   "包名非法"与"包已删除"得出同一个"目录已消失"的结论，即被读成"卸载成功"（H2 的放大器）。
        //   这里如实提示、直接返回（中性：不报成功也不报失败，什么都没改）。
        if (!PluginManager.IsValidPackageName(p.Name) || unArgs.Length == 0)
        {
            AddEvent($"未卸载插件 {p.Name}：包名不合法", EventKind.Bad);
            GuardDialog.Show(
                $"无法卸载「{p.Name}」：这不是一个合法的包名"
                + "（不能含路径分隔符、空白或特殊字符，也不能指向上级目录）。\n\n"
                + "本次没有执行任何命令、也没有改动配置文件。若这个名字来自插件清单，请检查该条目是否被改坏。",
                "包名不合法", MessageBoxButton.OK, MessageBoxImage.Warning);
            _ = RefreshPluginsAsync(true);
            return;
        }

        // 本单 H2 的核心：跑命令之前先记下"这个包原本在不在"。
        //   EvaluateUninstall 事后只看"现在目录在不在"，而"不在"包含两种情形：
        //   「本来装着、卸掉了」与「这台机器上从来就没有过」—— 事后一次目录检查分不开这两者。
        //   不记这一笔，用户在插件页对一张显示「未安装」的卡片点「卸载」（那颗按钮无条件出现在每张卡片上）、
        //   命令必然失败，却仍会报绿色「已卸载插件 X」+ 摘要写"已卸载…（重启 DSH 生效）"（H2 现场）。
        bool existedBefore = PluginManager.PackageDirExists(p.Name);

        // 开表之后全程套 try/finally：异常路径也要收尾（本轮补的同类缺陷——卸载中途抛异常时，
        // 底部会永远停在「正在卸载插件 X（NN%）」，用户以为程序卡死。收尾那几步
        // RefreshPluginsAsync / GuardDialog.Show 都在 await 之后，任何一处抛都会跳过正常收尾）。
        // 与 UpdatePlugin_Click / ReinstallPlugin_Click 同一个范式：正常路径的成败文案留在原位、
        // 一个字不动；finally 只在"本方法开的表还开着"时补一句如实的收尾话。
        bool opOpen = false;
        try
        {
            BeginOpProgress($"正在卸载插件 {p.Name}");
            opOpen = true;
            // ★ 落入"改依赖图中"（插件写闸）：到这一句为止本方法的早退已全部走过（取消 / 包名不合法
            //   各自 return、**一个字节都不落闸**）；下面第一条 await 立刻要跑 npx —— 正是该落闸的点。
            //   与市场安装 / 回滚重装同一个范式：放在 try 内 ⇒ 与进度表同一条 finally 收尾，
            //   异常路径也一定复位（否则闸门会永远关着，此后所有插件改动都被如实拒掉）。
            BeginPluginWriteState();
            // 记下"正在卸载"并把这颗按钮变成「停止卸载」——卸载期间必须有可用的停止入口（【中 2】）
            _uninstalling = true;
            _uninstallingPlugin = p;
            AdoptUninstallButton(b, p.Name);
            // relaxSupplyChainPolicy：卸载同样是一次 pnpm 改动，包龄/锁文件策略一视同仁地放开
            // 走可中止的 RunUninstallCommandAsync（不是 RunCommandAsync）：
            //   只有它会设置 MainWindow._runningCmd，而那是 StopRunningCommand() 唯一能杀的对象——
            //   原来的 RunCommandAsync 从不设置它，即卸载进行中点停止只会得到"没有正在跑的命令"，
            //   而卸载其实在跑（复查【中 2】实测：用户根本停不掉）。
            var (cmdOk, output) = await RunUninstallCommandAsync(unArgs);

            // 成败判据 = 磁盘事实，不是命令退出码（与批量卸载同一口径，见 BatchUninstall_Click 顶部那段）。
            //   为什么：pnpm 在 Windows 上常因"目录不是空的 (os error 145)"/"另一个程序正在使用此文件
            //   (os error 32)"/依赖构建脚本失败而返回非零，包其实已经删掉了，即只看退出码就会弹
            //   「卸载失败」，而用户去插件页一看插件已经没了（本轮用户报告的误报，就是这个）。
            //   EvaluateUninstall 是三态：原本就没装（existedBefore=false），即「无需卸载」这个中性结论；
            //   原本装着、目录确实不在则判成功（哪怕退出码非零）；原本装着、目录还在则判失败（哪怕退出码是 0，
            //   事实优先）；判不了（包名/目录为空/包名非法）才如实回落命令退出码。
            var verdict = PluginManager.EvaluateUninstall(p.Name, cmdOk, null, existedBefore);

            // 「被用户停止」与「命令失败」必须分开（与安装侧同一套判据，复查【中 2】）：
            // StopRunningCommand 杀掉进程后退出码必然非零、ok 也是 false —— 只看这两个就会落进
            // 下面那条重试分支，表现成"点了停止，它反而重新卸一次"。
            // 标记由"点停止"那一处落下（先落标记、再杀进程），这里消费一次。
            bool userStopped = ConsumeUninstallStopRequest();

            // 兜底重试：命令退出码非零、包还在、且不是用户停的，即万一 pnpm 不认
            // --config.minimumReleaseAge=0，去掉它原样再试一次（环境变量仍然注入：真正生效的是它）。
            // 包已经不在了不重试 —— 那就是"命令报了非零、其实卸干净了"那一支，再跑一次纯属多余。
            // 被用户停止的也不重试 —— 他已经明确不要这次卸载了。
            // 「原本就没装」（verdict.Unnecessary）时这一支天然不重试：EvaluateUninstall 那一支给的
            //   Removed=false，而 ShouldRetryUninstallAfterFailure 要求 removed 为假才重试，即
            //   没有可卸的东西时绝不会再跑一遍、白等一个 600 秒超时（不必改那个纯函数的判据）。
            if (ShouldRetryUninstallAfterFailure(cmdOk, verdict.Removed, userStopped))
            {
                Logger.Log($"卸载插件 {p.Name} 首次失败且包目录还在，去掉策略覆盖参数重试。输出：{Shorten(output, 300)}");
                var (cmdOk2, output2) = await RunUninstallCommandAsync(PluginManager.WithoutPolicyOverride(unArgs));
                output = output2 + "\n（首次输出）" + output;
                cmdOk = cmdOk2;
                verdict = PluginManager.EvaluateUninstall(p.Name, cmdOk, null, existedBefore);
            }

            bool ok = verdict.Removed;
            bool unnecessary = verdict.Unnecessary;      // 操作前本机就没有这个包 -> 中性档（本单 H2）
            // ★ 半卸载档（本单新增）：包目录确实已删掉、但插件清单里仍登记着它 —— 它不是成功，
            //   也不等于"什么都没发生"。后面每一处文案都单列这一档，旧档一字不动。
            bool halfDone = verdict.HalfDone;
            // 记账：只在**"真卸干净"这一档**（ok = verdict.Removed 为真）删掉该包的两条记录 —— 判据就是上面这个 ok，
            //   不另立一套"成没成"的判法（本项目要求判据只留一份）。
            //   为什么只有这一档能盖章：verdict 是四态结论（EvaluateUninstall），ok 只在 UninstallOutcome.Clean
            //   ——"包目录没了 **且** 清单里那一条也没了"—— 时为真；其余各档 ok 一律为假：
            //     · 半卸载（目录没了、清单里仍登记着，下次任何一次安装都会把它装回来）：不是成功，删记录等于
            //       把这次的"没卸干净"记成一次成功卸载 —— 正是本项目刚修过的"谎报成功"那类错误；
            //     · 没卸掉（目录还在）/ 判不了（清单读不出来）：都没卸干净，同上；
            //     · 「无需卸载」这个中性档（操作前本机就没有这个包）：本来就没有记录，更不该在这一档动账目；
            //     · 用户主动停止：命令没跑完，这次的结论本来就不可信（下面的三支文案也把它与成败分开），一律不删。
            //   包名取 p.Name（= PluginManager.Scan 读清单 dependencies 时那个键，见 PluginManager.cs:759/763）：
            //   与本地插件页读记账用的键（SortDataOf 传的也是 p.Name）**同一个**，不是任何显示名。
            if (!userStopped && ok)
                PluginTimes.Remove(p.Name);
            EndOpProgress(userStopped ? UninstallStoppedMessage
                                      : (ok ? $"插件 {p.Name} 已卸载"
                                            : unnecessary ? $"插件 {p.Name} 无需卸载"
                                                          : $"插件 {p.Name} 卸载失败"));

            // 留证：结论与依据都落盘（Logger.Log 是空实现，真落盘要走 NoteDiagnosis）。
            Logger.Log($"卸载插件 {p.Name}: 命令退出码0={cmdOk} 用户停止={userStopped} "
                     + $"磁盘判定={(verdict.Measured ? "可判" : "不可判")} 判成功={ok}\n{output}");
            // ★ 落盘取证改用唯一入口（本单新增）：一次给全「包名 · 目录状态 · 清单状态 · 结论」
            //   四样 —— 半卸载（目录没了、清单还在）在这条里可查，事后能复盘"到底卸干净没有"。
            //   ⚠ 原来那一块记的信息一条都没丢，逐条对照（旧块是被本行**替换入口**，不是少记信息）：
            //     · 「**用户主动停止**」      -> 传参 userStopped ⇒ 该文本带「· **用户主动停止**」
            //     · 「命令退出码0=」          -> 该文本原样带「命令退出码0=…」
            //     · 「磁盘判定=可判/不可判」  -> 「包目录判不了」/「包目录仍在」/「包目录已消失」
            //     · 「无需卸载 / 判成功 / 判失败」-> 结论=「无需卸载（操作前本机就没有）」/「真卸干净」/「没卸掉」，
            //        半卸载另给「**半卸载：目录没了、清单还在**（下次安装会装回来）」
            //     · 「依据：{verdict.Note}」  -> 该文本末尾原样带上
            //   ⇒ 旧块末支括号里那句"（用户报告的误报即此）"讲的是**修误报的历史由来**，不是本次卸载的
            //     事实或依据；"事实优先、命令非零也判成功"的说明仍留在上面 RunUninstallCommandAsync
            //     那一段注释里，此处不再重复一句。
            Logger.NoteDiagnosis(PluginManager.UninstallEvidenceText(p.Name, verdict, userStopped));
            // ⚠ 统一取证文本拿不到、而旧块确实记了的两件事，逐字补记（**不删原有信息**）：
            //   ① 被用户停止时"本次未完成、且不重试"——那是后续不会再跑命令的事实，事后排查要看得见；
            //   ② 命令退出码非零却判成功时的"不报失败"——用户报告的误报正是这一支，别让它失去痕迹。
            //   两条都只是**新增**一行日志，判据、界面文案与旧分支一概不动。
            if (userStopped)
                Logger.NoteDiagnosis($"卸载「{p.Name}」：**用户主动停止**，本次未完成（不重试）。");
            else if (ok && !cmdOk)
                Logger.NoteDiagnosis($"卸载「{p.Name}」：**命令退出码非零，但包目录已消失 ⇒ 判成功、不报失败**（用户报告的误报即此）。");

            // 摘要栏与失败弹窗都不再摆原始命令输出 ⇒ 全文必须能在日志里拿到：
            //   常规失败（退出码非零）由命令层 NoteCommandResult 落盘；
            //   这里补一条同款诊断，兜住"退出码 0、包目录却还在"与命令超时这类命令层不落盘的情形。
            //   只覆盖"真失败"这一支：用户停止 / 无需卸载各自已有中性结论文案，不再多记一份输出。
            if (!ok && !unnecessary)
                LogPluginCmdFailure($"插件卸载失败 {p.Name}", unArgs, output);

            // 事实优先：命令报了非零、包却确实没了，即文案照实说"已卸载"（别再报"失败"，
            // 用户回插件页一看插件没了，那个弹窗就是本轮报告的误报）。
            // 真没卸掉时才说失败，且原因写磁盘事实（"包目录还在"），命令输出只作补充。
            // ★ 半卸载档（本单新增）：旧句在能判磁盘时一律说"包目录还在"，而半卸载的定义正是
            //   **包目录已经没了**（只是清单里还留着登记）⇒ 旧句与事实相反，用户看着"目录还在"
            //   却被告知失败、完全不知怎么办。只**新增**这一档：下面两行旧文案与缩进逐字节保持原样。
            string failReason = halfDone
                ? "包文件已删掉，但插件清单里仍留着它这一条记录"
                : verdict.Measured
                ? "包目录还在，未卸载成功"
                : "核对不了本机状态，按操作结果判为失败";
            // 三支：用户停止 / 卸掉了 / 没卸掉。被停止绝不报成"卸载失败" ——
            // 那是用户自己的要求，报成红色失败就是又一次"文案与事实打架"（复查【中 2】配套）。
            // 三支 + 一支中性（本单 H2）：用户停止 / 卸掉了 / 无需卸载 / 没卸掉。
            // 「无需卸载」必须单独一档：它不是成功（谎报"已卸载"正是 H2 那条缺陷），也不是失败（什么都没坏）。
            AddEvent(userStopped ? UninstallStoppedMessage
                                 : unnecessary ? $"无需卸载插件 {p.Name}（本机本来就没有安装它）"
                                               : halfDone ? $"插件 {p.Name} 未卸干净（清单里仍有登记，下次安装会装回来）"
                                               : (ok
                                                   ? (cmdOk ? $"已卸载插件 {p.Name}"
                                                            : $"已卸载插件 {p.Name}（命令报了非零，包已确认删掉）")
                                                   : $"卸载插件失败：{p.Name} · {failReason}"),
                // 卸载是破坏性动作：成功 = 橙色（与同文件上面「停用 = 橙色」同一口径），失败 = 红色。
                // 这里原来是三元两侧同为 EventKind.Bad（成功也标成失败红，等于三元白写了）。
                // 全库正则扫「三元两侧同值」时唯一命中就是这一行（对抗性复查【低 1】）。
                // 这不是恢复用户已撤回的 Destructive 档：不加新档位、不动别处颜色，只修这一处笔误。
                // 「被用户停止」也走橙色：那是用户自己的要求，不是失败（写死 Bad 会又一回"文案与事实打架"）。
                // 「无需卸载」= 灰白（Info）：什么都没发生，既不该染成橙色成功、也不该染成红色失败。
                unnecessary ? EventKind.Info
                            : ok || userStopped ? EventKind.Warn : EventKind.Bad);
            PluginsSummaryText.Text = userStopped
                ? $"{UninstallStoppedMessage}：{p.Name} 未卸载"
                : unnecessary
                    ? $"无需卸载 {p.Name}：本机本来就没有安装它（未执行任何删除）"
                    : halfDone
                        ? $"未卸干净 {p.Name}：包已删、清单里还有登记"
                        : (ok
                            ? $"已卸载 {p.Name}（重启 DSH 生效）"
                            : $"卸载失败：{failReason}");
            if (userStopped)
                GuardDialog.Show($"{UninstallStoppedMessage}。\n\n"
                    + $"「{p.Name}」已改动的部分（若命令写到一半）不会回滚；"
                    + "插件清单与包目录保持当前状态，可点「刷新」查看。",
                    "已停止卸载", MessageBoxButton.OK, MessageBoxImage.Information);
            else if (unnecessary)
                GuardDialog.Show(
                    $"「{p.Name}」在本机没有安装，无需卸载。\n\n"
                    + "插件卡片显示「未安装」即表示本机没有它 —— 本次没有执行任何删除，也没有改动配置文件。\n"
                    + "若它本就不该出现在插件清单里，请在该清单文件中手工确认这一条。",
                    "无需卸载", MessageBoxButton.OK, MessageBoxImage.Information);
            // ★ 半卸载档（本单新增，压在"失败"那一支**之前**）：它不是"插件仍在本机、可直接重试"
            //   那种失败 —— 包其实已经删掉了，缺的是清单里那一条记录 ⇒ 必须给"下一步怎么办"，
            //   文案走唯一入口 HalfUninstallAdvice（中性、不含命令行/网址/内部标识/盘符路径）。
            else if (halfDone)
                GuardDialog.Show(PluginManager.HalfUninstallAdvice(p.Name), "卸载未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
            else if (!ok)
                GuardDialog.Show($"卸载失败：{failReason}。\n\n" +
                    "插件仍在本机，可直接重试（若 DSH 正在运行，可能因文件被占用而失败：建议先停止引擎）。\n\n" +
                    PluginManager.SupplyChainRelaxHint + "\n\n" +
                    LogPromise("详细输出已记入日志，可在「日志」页查看。"),
                    "卸载失败", MessageBoxButton.OK, MessageBoxImage.Warning);

            await RefreshPluginsAsync(true);
        }
        // 收尾必须两条都走：进度表（异常时兜底）与卸载状态/按钮复位。
        // EndUninstallState / EndPluginWriteState 都幂等 —— 任何异常路径都不漏复位。
        finally
        {
            EndOpProgressIfOpen(opOpen, $"「{p.Name}」卸载中断");
            EndUninstallState();
            EndPluginWriteState();      // ← 写闸与上面那条同一条 finally 收尾：异常路径也一定复位
        }
    }

    /// <summary>
    /// 「重新安装」（损坏态专用）：先清理半截安装残留，再按清单声明重装。
    /// 目标一律走 <see cref="UpdateArgsFor"/> / <see cref="PluginManager.BuildAddSourceArgs"/> 的既有唯一入口
    /// （npm 源则用清单声明里的版本；git 源则用仓库地址），白名单/越界拒绝时如实提示、绝不执行命令。
    /// </summary>
    private async void ReinstallPlugin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not PluginManager.Plugin p) return;

        // 重入闸（本轮修）：本方法开头那条 npx 与「一键更新 / 卡片上单颗更新」跑的是同一条命令、
        // 动的是同一份 node_modules / pnpm-lock.yaml，即三条路径共用一个闸。
        // 闸门只有一份（PassUpdateGate），被拒时它自己会说一句"正在更新中"，绝不静默无反应。
        // 位置与另两处一致：放在确认框之前 —— 免得用户点了确认、命令都要跑了才被告知"正在更新中"。
        // 注意它只查不开；真正的闸门落在下面"立刻要跑命令"那一句（BeginUpdatingState）。
        if (!PassUpdateGate()) return;

        // ★ 缺 Git 闸门（唯一入口）：清单里这条声明是 git 源、而本机 PATH 里确实没有 git
        //   ⇒ 不执行任何命令，当面说清缺什么。位置与上面那道闸一致：压在**确认框之前**
        //   （免得用户点了确认、残留目录都清干净了才被告知缺东西），也压在半截安装清理之前
        //   —— 拦下即一个字节都不动磁盘，直接 return（此刻写闸只查未开，不留悬挂状态）。
        // 清单声明在这里取一次，下面构造安装目标时复用（不在两处各读一次盘）。
        string depSpec = PluginManager.DepSpec(p.Name);
        if (BlockedForMissingGit(p.Name, depSpec, "重新安装插件"))
        {
            GuardDialog.Show(GitMissingDialogText(p.Name), "重新安装插件", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // ★ 引擎忙碌警告（状态③才弹）：压在确认框之前、两道闸之后 —— 免得用户白点一次确认。
        if (!await WarnIfEngineBusyAsync()) return;
        var r = GuardDialog.Show(
            $"重新安装插件「{p.Name}」？\n\n" +
            "检测到上次安装留下了一份不完整的目录 —— 将先把它清理干净，再按插件清单重新安装。\n" +
            "若 DSH 正在运行，清理与安装可能因文件占用失败（建议先停止引擎）。",
            "重新安装插件", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (r != MessageBoxResult.OK) return;

        // 半截安装自愈（唯一入口）：越界/清理失败，即中止并如实提示（建议先停引擎）
        if (!EnsureNotBrokenInstall(p.Name, out string brokenNote))
        {
            GuardDialog.Show(brokenNote, "重新安装插件", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 安装目标：清单声明是 git 源，即走来源 spec；否则按包名 + 清单里的版本段装回
        //（depSpec 已在方法开头取过一次：上面的缺 Git 闸门要用它，这里不再重复读盘）
        string args = PluginSource.Classify(depSpec) != PluginSource.Kind.Registry
            ? PluginManager.BuildAddSourceArgs(PluginManager.GitSourceSpec(depSpec))
            : PluginManager.BuildAddSourceArgs(PluginManager.ConcreteVersionOf(depSpec).Length > 0
                ? $"{p.Name}@{PluginManager.ConcreteVersionOf(depSpec)}"
                : p.Name);
        if (args.Length == 0)
        {
            GuardDialog.Show(
                $"无法为「{p.Name}」确定合法的安装来源，本次未执行任何命令。可以在「寻找插件」里手动安装。",
                "重新安装插件", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 开表之后全程套 try/finally：异常路径也要收尾（本轮修的缺陷——重装中途抛异常时，
        // 底部会永远停在「正在重新安装插件 X（NN%）」，用户以为程序卡死）。
        // 上面的正常收尾文案留在原位，一个字不动；finally 只在"本方法开的表还开着"时补一句如实的收尾话。
        bool opOpen = false;
        try
        {
            BeginOpProgress($"正在重新安装插件 {p.Name}");
            opOpen = true;
            // 落入"更新中"（重入闸 + 「一键更新」按钮压灰）：与另两处同一个范式。
            //   为什么落在这里而不是确认框正后面：本方法在 try 之前还有两处早退
            //   （半截残留清理未通过 / 定不出合法安装来源）——那里各自 return，不落闸；
            //   若把 BeginUpdatingState 提到它们前面，这两条早退就绕过了 finally，即闸门永远关着。
            //   到这里早退已全部走过，下面第一句 await 立刻要跑 npx，正是该落闸的点；
            //   放在 try 内，即与进度表同一条 finally 收，异常路径也一定复位。
            BeginUpdatingState();
            PluginsSummaryText.Text = $"正在重新安装「{p.Name}」…";
            var (cmdOk, output) = await RunCommandAsync("npx", args, timeoutMs: 600000, relaxSupplyChainPolicy: true);
            bool okFinal = cmdOk || PluginManager.HasDependency(p.Name);    // 事实优先：清单里回来了就算装上
            // 记账：只在**这一档**（okFinal 为真）盖"订阅时间"章 —— 判据就是上面这个 okFinal，
            //   不另立一套"成没成"的判法（本项目要求判据只留一份）。
            //   为什么这一档该盖"安装"章：本方法是**一次真实的重新安装**（下面跑的是 `npx dsh plugin add …`，
            //   不是更新），装上了就该按"什么时候装到本机"记一笔；而 okFinal 已把两条成功路径合成完毕 ——
            //   命令退出码为 0，以及"命令报了非零、但回读清单时那个包已经回来"（HasDependency ⇒ okFinal 真），
            //   与下面 EndOpProgress / AddEvent 报给用户的结论同源。若改用 cmdOk 另判一次，后者就会被漏记。
            //   其余各档 okFinal 均为假（命令失败且清单里没有）：没装上，盖了就是记账撒谎。
            //   ⚠ 刻意**不清** Updated 列（本鲸裁决）：那两列记的是两件独立的事实 —— 订阅时间 = 什么时候装的，
            //   更新时间 = 什么时候更新过；重装不改变"上次更新是什么时候"这个事实。反过来清掉它，
            //   界面会从"有更新时间"变成"从没更新过"，等于替用户抹掉一条真发生过的记录。
            //   （两列一起删只发生在**卸载**那一路：见 PluginTimes.Remove，那时旧记录整体失效。）
            //   包名取 p.Name（= PluginManager.Scan 读清单 dependencies 时那个键，见 PluginManager.cs:759/763）：
            //   与本地插件页读记账用的键（SortDataOf 传的也是 p.Name）**同一个**，不是任何显示名。
            //   时间格式照 VersionMemory.Now()（yyyy-MM-dd HH:mm）的写法直接给出：它就是记账模块写入的格式，
            //   而那个方法是 private，不去改 VersionMemory。
            if (okFinal)
                PluginTimes.StampSubscribed(p.Name, DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            EndOpProgress(okFinal ? $"「{p.Name}」已重新安装" : $"「{p.Name}」重新安装失败");
            AddEvent(okFinal ? $"已重新安装插件 {p.Name}" : $"重新安装插件失败：{p.Name}",
                okFinal ? EventKind.Good : EventKind.Bad);
            Logger.Log($"重新安装 {p.Name}: 命令={cmdOk} 清单判定={okFinal}\n{output}");
            if (!okFinal)
            {
                // 弹窗不再摆原始命令输出；全文落异常日志（Logger.Log 是空实现，必须走 NoteDiagnosis）。
                // 常规失败由命令层记过，这里补一条兜住超时 / "退出码 0 但清单里没有"这类不落盘的情形。
                LogPluginCmdFailure($"重新安装失败 {p.Name}", args, output);
                GuardDialog.Show(PluginManager.SupplyChainRelaxHint + "\n\n"
                    + LogPromise("详细输出已记入日志，可在「日志」页查看。"),
                    "重新安装失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            await RefreshPluginsAsync(true);
            if (okFinal) await OfferRestartAsync($"重新安装插件 {p.Name}");
        }
        finally
        {
            EndOpProgressIfOpen(opOpen, $"「{p.Name}」重新安装中断");
            EndUpdatingState();     // ← 本轮修：与进度表同一条 finally 收尾，异常路径也一定还原按钮、复位闸门
        }
    }

    // ══════════════ 版本 ══════════════
    private async Task RefreshVersionAsync(bool force = false)
    {
        try
        {
            if (!force && _versionInfo != null) { UpdateVersionCard(); return; }
            if (VerCardCurrent == null) return;

            VerCardCurrent.Text = "检测中…";
            VerCardSub.Text = "正在查询发布时间与最新版…";
            VerCardLatest.Text = "";

            var info = await VersionInfo.QueryAsync();
            _versionInfo = info;
            _currentDshVersion = info.Current;
            // 卡片这条路的失败也要留全证：QueryAsync 内部 catch 之后是**正常返回**（它不抛异常），
            //   外层 catch 里那条 LogError 够不着；而 Logger.Log / LogDiagnosis 都是空实现，
            //   真落盘只能用 NoteDiagnosis。界面只留中性结论，原始异常全文落在这一条里。
            //   ⚠ 只在这里记一次：RenderVersionView 会被反复重渲染调用，放那里会记成 N 条噪音。
            if (info.Latest == null)
                Logger.NoteDiagnosis("版本卡片刷新：查不到最新版本（离线或下载源不可用）。原始错误："
                    + (info.Error ?? "（无）"));
            // 待更新但没记下目标版本（旧版遗留状态）则用查到的最新版补上，否则启动命令只能退回 latest
            if (info.Latest != null) VersionMemory.EnsureUpdateTarget(info.Latest);
            UpdateVersionCard(info);
            // 自动检查到新版本时追加一条事件，显示在右下角「事件信息」。
            // 基准同样取固定版本：引擎此刻在跑的版本可能是外部启动的，拿它比会漏报（现场已发生）。
            string pinBase = VersionMemory.Pin.Length > 0 ? VersionMemory.Pin : info.Current;
            if (info.Latest != null && !string.Equals(info.Latest, pinBase, StringComparison.OrdinalIgnoreCase))
                AddEvent($"发现 DSH 新版本 {info.Latest}" +
                         (info.Channel != null && info.Channel != "正式版" ? $"（{info.Channel}）" : "") +
                         "，去「设置 → 版本」可以升级", EventKind.Update);   // 探测到更新 = 淡蓝
            RenderVersionView();   // 详情页保持同步（也顺带在启动时把这一页渲染一次，异常会记进错误日志）
        }
        catch (Exception ex) { Logger.LogError("RefreshVersionAsync", ex); }
    }

    /// <summary>同步右栏「DSH 版本」卡片（含版本策略行与两个按钮的可用性）。</summary>
    private void UpdateVersionCard(VersionInfo.Info? info = null)
    {
        try
        {
            info ??= _versionInfo;
            if (VerCardCurrent == null) return;

            // 右侧卡片四行（照定下的格式）：
            //   ① 当前版本（白色、稍大一点、最醒目）② 最新版本（灰）③ 最新版本更新时间（灰）④ 状态（绿/淡蓝）
            string pinNow = VersionMemory.Pin.Length > 0 ? VersionMemory.Pin : _currentDshVersion;
            string latestNow = info?.Latest ?? "";
            bool sameAsLatest = latestNow.Length > 0 && string.Equals(latestNow, pinNow, StringComparison.OrdinalIgnoreCase);
            var grey = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93));
            var green = new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x59));
            var lightBlue = new SolidColorBrush(Color.FromRgb(0x5A, 0xC8, 0xFA));

            VerCardCurrent.Text = "DSH " + pinNow;
            VerCardCurrent.Foreground = Brushes.White;
            VerCardCurrent.FontSize = 14;
            VerCardCurrent.FontWeight = FontWeights.SemiBold;

            VerCardSub.Text = info?.Latest == null ? "最新版本：查询失败" : "最新版本：" + latestNow;
            VerCardSub.Foreground = grey;

            // 注意：卡片里 VerCardLatest 画在第三行、VerCardPin 画在第四行（XAML 顺序如此），
            // 所以"更新时间"给上面那个、"状态"给下面那个 —— 之前写反了，现场已指出。
            VerCardLatest.Text = "更新时间：" + (info?.Published != null && info.Published.Length > 0 ? info.Published : "未知");
            VerCardLatest.Foreground = grey;

            if (VerCardPin != null)
            {
                VerCardPin.Text = info?.Latest == null
                    ? "（可能离线，稍后重试）"
                    : sameAsLatest ? "✅ 已是最新" : $"已手动固定 {pinNow} 版本";
                VerCardPin.Foreground = (info?.Latest == null)
                    ? grey : (sameAsLatest ? green : lightBlue);
            }
            // 卡片上的按钮与小字已移除：整张卡可点击，进入「设置 -> 版本」后操作
        }
        catch (Exception ex) { Logger.LogError("UpdateVersionCard", ex); }
    }

    // ═══ 检查更新 / 升级 ═══
    /// <summary>
    /// 检查更新：查最新版 -> 有新版则询问 -> 进入「更新模式」（下一次启动拉 @latest，
    /// 跑通后自动把新版本固定下来，旧版本留作回退候选）。
    /// </summary>
    private async Task CheckUpdateAsync()
    {
        try
        {
            if (VerCardLatest != null)
            {
                VerCardLatest.Text = "正在检查更新…";
                VerCardLatest.Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93));
            }

            var info = await VersionInfo.QueryAsync();
            _versionInfo = info;
            _currentDshVersion = info.Current;
            UpdateVersionCard(info);

            if (info.Latest == null)
            {
                // 弹窗只留结论与去处：info.Error 是 VersionInfo.cs:69 的 ex.Message（英文原始异常，
                //   形如 "No such host is known."），摆在界面上用户看不懂 —— 全文落异常日志留证。
                //   ⚠ 「查不到最新版」是 QueryAsync 内部 catch 之后的**正常返回**（它不抛异常），
                //   外层 catch 里那条 Logger.LogError 够不着它；而 Logger.Log / LogDiagnosis 都是空实现，
                //   真落盘只能用 NoteDiagnosis（[WARN]）。
                Logger.NoteDiagnosis("检查更新：查不到最新版本（离线或下载源不可用）。原始错误："
                    + (info.Error ?? "（无）"));
                GuardDialog.Show(
                    "查不到最新版本（可能离线，或下载来源暂时不可用）。\n\n"
                    + LogPromise("详细原因已记入日志，可在「日志」页查看。"),
                    "检查更新", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            string latest = info.Latest;   // 上方已判非 null，即后续一律用这个非空值

            // 判断"要不要升级"必须以守护壳固定的版本为基准，而不是引擎此刻在跑的版本 ——
            // 引擎可能从命令行等外部方式启动，版本与固定版本不同，用它会得出
            // "明明固定着旧版却说已是最新"的错误结论（现场已发生）。
            string baseline = VersionMemory.Pin.Length > 0 ? VersionMemory.Pin : info.Current;
            bool needsUpgrade = info.Latest != null &&
                                !string.Equals(info.Latest, baseline, StringComparison.OrdinalIgnoreCase);
            if (!needsUpgrade)
            {
                GuardDialog.Show($"已是最新版本（当前固定的 DSH {baseline}）。",
                    "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // ═══ 升级前先做插件兼容性体检，给出策略 ═══
            var (ok, partial, broken, unknown) = await EvaluatePluginsAsync(latest);

            string report =
                $"发现新版本 DSH {latest}（当前固定 {baseline}" +
                (info.LatestPublished != null ? $"，新版发布于 {info.LatestPublished}" : "") + "）。" +
                (info.Channel != null && info.Channel != "正式版"
                    ? $"\n注意：这是「{info.Channel}」，不是正式版，仅供尝鲜。"
                    : "") + "\n\n" +
                $"插件兼容性体检（针对 {latest}）：\n" +
                $"· 兼容（正好是作者的版本）  {ok.Count} 个\n" +
                $"· 可用（在范围内但非作者优先版本） {partial.Count} 个" +
                (partial.Count > 0 ? "：" + Shorten(string.Join("、", partial.Select(p => p.Name)), 110) : "") + "\n" +
                $"· 不兼容                  {broken.Count} 个" +
                (broken.Count > 0 ? "：" + Shorten(string.Join("、", broken.Select(p => p.Name)), 110) : "") + "\n" +
                $"· 未声明要求（无法判定）  {unknown.Count} 个\n";

            string plan;
            MessageBoxResult r;

            if (broken.Count > 0)
            {
                report +=
                    "\n不兼容的插件在新版本下大概率报错，建议随升级一并禁用。\n" +
                    "（禁用前会备份配置文件，出现问题时仍可回退）\n\n" +
                    "策略选择：\n" +
                    "　[是] 升级 + 禁用不兼容插件\n" +
                    "　[否] 只升级，不禁用（出问题再回退）\n" +
                    "　[取消] 维持现状，不升级";
                plan = "升级+禁用";
                r = GuardDialog.Show(report, "检查更新 · 兼容性体检",
                    MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
                if (r == MessageBoxResult.Cancel) { AddEvent($"检查更新：选择维持现状（{latest} 未升级）"); return; }
            }
            else
            {
                report += "\n没有「不兼容」的插件，可以直接升级。" +
                          (partial.Count > 0 ? "\n（橙色几项在新版上可用，但不是作者优先适配的版本，请留意可能的兼容性问题。）" : "") +
                          "\n\n是否升级？";
                plan = "升级";
                r = GuardDialog.Show(report, "检查更新 · 兼容性体检",
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r != MessageBoxResult.Yes) { AddEvent($"检查更新：选择维持现状（{latest} 未升级）"); return; }
            }

            bool disableBroken = broken.Count > 0 && r == MessageBoxResult.Yes;

            if (SnapshotPolicy.NeedSnapshot(GuardAction.UpgradeDsh))
                SnapshotBeforePluginChange($"DSHGuard：升级到 {latest} 前", SnapshotPolicy.KindFor(GuardAction.UpgradeDsh));
            VersionMemory.BeginUpdate(latest);
            Logger.Log($"版本记忆：用户确认升级到 {latest}（策略 {plan}）");
            AddEvent($"已切到新版本 {latest}（重启引擎后生效，策略 {plan}）", EventKind.Good);

            if (disableBroken)
            {
                // 批量改动前先把清单摆出来，用户确认后再动——避免"升级完才发现自己的插件被关了一排"。
                string list = string.Join("\n", broken.Take(12).Select(p => "  · " + p.Name))
                              + (broken.Count > 12 ? $"\n  … 其余 {broken.Count - 12} 个" : "");
                var confirm = GuardDialog.Show(
                    $"升级到 {latest} 前，需先禁用 {broken.Count} 个与它不兼容的插件：\n\n{list}\n\n" +
                    "被禁用的插件会在回退版本时提示恢复；也可先取消，自行到「插件」页处理。",
                    "升级前禁用不兼容插件", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

                if (confirm != MessageBoxResult.OK)
                {
                    disableBroken = false;
                    AddEvent("检查更新：按你的选择未禁用任何插件（升级继续）", EventKind.Warn);
                }
                else
                {
                    // 一次备份 + 一次写入（以前是每个插件重写一次文件，既慢又容易写坏）
                    var (disabledList, detail) = await Task.Run(() => PluginManager.DisableMany(broken));
                    foreach (var p in broken) if (disabledList.Contains(p.Name)) p.Disabled = true;
                    VersionMemory.RecordDisabledForUpdate(disabledList);
                    AddEvent($"升级前已禁用 {disabledList.Count} 个不兼容插件（一次写入）", EventKind.Warn);   // 停用=橙
                    RenderPlugins();
                    GuardDialog.Show(
                        (disabledList.Count > 0 ? detail : "没有任何插件被改动。") + "\n\n回退版本时会提示恢复。",
                        "插件已处理", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }

            UpdateVersionCard(info);
            RenderVersionView();

            await OfferRestartAsync(disableBroken ? $"更新到 DSH {latest}（已禁用不兼容插件）" : $"更新到 DSH {latest}");
        }
        catch (Exception ex) { Logger.LogError("CheckUpdateAsync", ex); }
    }

    // ═══ 升级前的插件兼容性体检（兼容 / 可用 / 不兼容 / 未声明）═══
    // 体检结果仅用于「检查更新」的策略对话框，设置 -> 版本 页不再展示兼容性卡片。

    /// <summary>按目标版本评估插件兼容性（插件清单未加载时先扫描一次）。</summary>
    private async Task<(List<PluginManager.Plugin> Ok, List<PluginManager.Plugin> Partial, List<PluginManager.Plugin> Broken, List<PluginManager.Plugin> Unknown)>
        EvaluatePluginsAsync(string target)
    {
        try
        {
            if (_plugins.Count == 0)
            {
                string cur = _currentDshVersion == "未知" ? VersionInfo.GetCurrentVersion() : _currentDshVersion;
                _plugins = await Task.Run(() => PluginManager.Scan(cur));
                BackfillLoaderIds();        // 换了新对象就必须重贴 id（否则这批插件全都禁用不了）
            }
            if (!_loaderIdsLoaded) await LoadLoaderIdsAsync();

            var (ok, partial, broken, unknown) = PluginManager.Evaluate(_plugins, target);
            Logger.Log($"兼容性体检 {target}：兼容 {ok.Count} / 可用 {partial.Count} / 不兼容 {broken.Count} / 未声明 {unknown.Count}");
            return (ok, partial, broken, unknown);
        }
        catch (Exception ex)
        {
            Logger.LogError("EvaluatePluginsAsync", ex);
            return (new List<PluginManager.Plugin>(), new List<PluginManager.Plugin>(),
                    new List<PluginManager.Plugin>(), new List<PluginManager.Plugin>());
        }
    }

    /// <summary>回退后恢复升级时为避开不兼容而禁用的插件（需用户确认）。</summary>
    private void OfferRestoreDisabledPlugins()
    {
        try
        {
            var names = VersionMemory.DisabledForUpdate.ToList();
            if (names.Count == 0) return;

            var r = GuardDialog.Show(
                "升级时为了规避不兼容，已禁用以下插件：\n  " + Shorten(string.Join("、", names), 200) + "\n\n" +
                "现已回退到旧版本，是否将这些插件恢复启用？（将先备份配置文件）",
                "恢复被禁用的插件", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;

            var done = new List<string>();
            foreach (var name in names)
            {
                var p = _plugins.FirstOrDefault(x => x.Name == name) ?? new PluginManager.Plugin { Name = name };
                string res = PluginManager.Enable(p);
                if (res.StartsWith("已重新启用")) done.Add(name);
            }
            VersionMemory.ClearDisabledForUpdate();
            if (_plugins.Count > 0)
            {
                _plugins = PluginManager.Scan(_currentDshVersion == "未知" ? VersionInfo.GetCurrentVersion() : _currentDshVersion);
                BackfillLoaderIds();        // 重扫出的新对象同样要重贴 id
            }
            RenderPlugins();
            AddEvent($"已恢复 {done.Count} 个插件（重启 DSH 后生效）", EventKind.Good);   // 启用=绿
            GuardDialog.Show($"已恢复 {done.Count} 个插件，重启 DSH 后生效。",
                "恢复完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { Logger.LogError("OfferRestoreDisabledPlugins", ex); }
    }

    // ═══ 回退 ═══
    private async Task RollbackBestAsync()
    {
        try
        {
            string best = VersionMemory.BestRollbackCandidate();
            if (best.Length == 0)
            {
                ShowVersionPage();
                GuardDialog.Show(
                    "暂无可回退的版本记录。\n\n" +
                    "升级过一次（跑通过新版本）、或本机缓存里存在别的版本之后，这里就会出现回退候选。",
                    "回退版本", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            await RollbackAsync(best);
        }
        catch (Exception ex) { Logger.LogError("RollbackBestAsync", ex); }
    }

    private async Task RollbackAsync(string version)
    {
        try
        {
            string cur = VersionMemory.Pin.Length > 0 ? VersionMemory.Pin : _currentDshVersion;
            if (version == cur && !VersionMemory.PendingUpdate)
            {
                GuardDialog.Show($"当前已经固定在 DSH {version}，无需回退。",
                    "回退版本", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var r = GuardDialog.Show(
                $"回退到 DSH {version}？\n\n" +
                $"回退会把它固定为启动版本（当前 {cur}）；\n" +
                "已在运行的任务不受影响，重启引擎后生效。",
                "回退版本", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;

            if (SnapshotPolicy.NeedSnapshot(GuardAction.RollbackDsh))
                SnapshotBeforePluginChange($"DSHGuard：回退到 {version} 前", SnapshotPolicy.KindFor(GuardAction.RollbackDsh));
            VersionMemory.RollbackTo(version);
            Logger.Log($"版本记忆：用户回退到 {version}");
                AddEvent($"已回退到旧版本 {version}", EventKind.Warn);
            UpdateVersionCard();
            RenderVersionView();

            await OfferRestartAsync($"回退到 DSH {version}");
        }
        catch (Exception ex) { Logger.LogError("RollbackAsync", ex); }
    }

    /// <summary>
    /// 版本策略变更后询问是否立即重启引擎。
    /// 外部引擎（终端中启动的）不接管；本程序启动的引擎也需用户确认「是」后才重启，
    /// 重启会中断正在运行的任务。
    /// </summary>
    private async Task OfferRestartAsync(string what)
    {
        try
        {
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

            if (_isRunning)
            {
                await GracefulStopAsync();
                await Task.Delay(800);
            }
            await StartEngineAsync();
        }
        catch (Exception ex) { Logger.LogError("OfferRestartAsync", ex); }
    }

    // ═══ 卡片 / 详情页按钮 ═══
    // 版本卡片上的两个按钮已移除：整张卡可点，进入「设置 -> 版本」后操作
    private async void CheckUpdate_Click(object sender, RoutedEventArgs e) => await CheckUpdateAsync();
    private async void RollbackRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string v && v.Length > 0) await RollbackAsync(v);
    }

    private void PinVersion_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not Button b || b.Tag is not string v || v.Length == 0) return;
            VersionMemory.PinTo(v);
            // 先取结果再报：写盘失败（含读失败主动拒写）时改口径，不再照报「已固定」（重启后会"没了"）
            var pinOutcome = VersionMemoryOutcome($"已固定 DSH {v}");
            AddEvent(pinOutcome.Text, pinOutcome.Kind);
            UpdateVersionCard();
            RenderVersionView();
        }
        catch (Exception ex) { Logger.LogError("PinVersion_Click", ex); }
    }

    private async void FollowLatest_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // 双向开关：不再弹确认框（点错了再点一下就能切回来）
            VersionMemory.FollowLatest();
            var followOutcome = VersionMemoryOutcome("已改回跟随最新版");
            AddEvent(followOutcome.Text, followOutcome.Kind);
            UpdateVersionCard();
            RenderVersionView();
            await OfferRestartAsync("跟随最新版");
        }
        catch (Exception ex) { Logger.LogError("FollowLatest_Click", ex); }
    }

    private void CancelUpdate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            VersionMemory.CancelUpdate();
            var cancelOutcome = VersionMemoryOutcome("已取消更新模式");
            AddEvent(cancelOutcome.Text, cancelOutcome.Kind);
            UpdateVersionCard();
            RenderVersionView();
        }
        catch (Exception ex) { Logger.LogError("CancelUpdate_Click", ex); }
    }

    private static string FormatSpan(int seconds)
    {
        if (seconds < 60) return seconds + " 秒";
        if (seconds < 3600) return (seconds / 60) + " 分钟";
        return $"{seconds / 3600} 小时 {seconds % 3600 / 60} 分";
    }

    private void RenderVersionView()
    {
        if (VersionPanel == null) return;
        try
        {
            VersionPanel.Children.Clear();
            var info = _versionInfo;

            // 启动补删（只跑一次）：上一轮交给安装器的安装包没人认领时，在这里清掉。
            // 挂在版本页渲染这一条上是**有意的** —— 它由启动时的 RefreshVersionAsync 调到，
            // 不需要改动本单不许碰的 MainWindow.xaml.cs；而且只在要用的那一页付出这点开销，
            // 延后 3 秒执行，绝不与启动抢磁盘。
            SweepGuardUpdateStagingSoon();

            Border MakeCard(double top = 0)
            {
                var b = new Border
                {
                    CornerRadius = new CornerRadius(10),
                    Background = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
                    Padding = new Thickness(14, 12, 14, 12),
                    Margin = new Thickness(0, top, 0, 0)
                };
                b.Child = new StackPanel();
                return b;
            }

            static StackPanel Body(Border b) => (StackPanel)b.Child;

            void Row(StackPanel host, string k, string v, Color? c = null, string? tip = null)
            {
                var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var kt = new TextBlock { Text = k, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)) };
                var vt = new TextBlock
                {
                    Text = v,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    ToolTip = tip,
                    Foreground = new SolidColorBrush(c ?? Color.FromRgb(0xF5, 0xF5, 0xF7))
                };
                Grid.SetColumn(vt, 1);
                g.Children.Add(kt);
                g.Children.Add(vt);
                host.Children.Add(g);
            }

            // ① 运行中的 DSH + 版本策略
            var infoCard = MakeCard();
            var isp = Body(infoCard);
            isp.Children.Add(SimpleText("运行中的 DSH", 13, Color.FromRgb(0x5A, 0xC8, 0xFA), true));
            // 当前版本放最上面并标绿 —— 这是"现在真正在跑的那个版本"，其余行都只是参考信息
            // 四行，照定的口径来：
            //   当前版本（永远绿色）-> 最新版本（一致灰、不一致蓝）-> 发布时间 -> 状态（一致绿、不一致淡蓝）
            string pinNow = VersionMemory.Pin.Length > 0 ? VersionMemory.Pin : _currentDshVersion;
            string latestNow = info?.Latest ?? "";
            bool sameAsLatest = latestNow.Length > 0 && string.Equals(latestNow, pinNow, StringComparison.OrdinalIgnoreCase);

            Row(isp, "当前版本", pinNow, Color.FromRgb(0x34, 0xC7, 0x59),
                tip: "守护壳固定使用的版本 —— 下次「一键启动引擎」用的就是它");

            // 发布时间并进这一行的括号里，不再单独占一行
            string latestText = info?.Latest ?? "查询失败";
            if (info?.Published != null && info.Published.Length > 0) latestText += $"（发布 {info.Published}）";
            Row(isp, "最新版本", latestText,
                (info?.Latest == null || sameAsLatest)
                    ? Color.FromRgb(0x8E, 0x8E, 0x93) : Color.FromRgb(0x5A, 0xC8, 0xFA),
                tip: sameAsLatest ? "下载来源上的最新版本，与你当前固定的版本一致" : "下载来源上有别的版本（不是当前固定的这个）");

            Row(isp, "状态", info?.Latest == null
                    ? "暂无法核对（可能离线）"
                    : sameAsLatest ? "✅ 已是最新" : $"已手动固定 {pinNow} 版本",
                (info?.Latest == null || sameAsLatest)
                    ? (info?.Latest == null ? Color.FromRgb(0x8E, 0x8E, 0x93) : Color.FromRgb(0x34, 0xC7, 0x59))
                    : Color.FromRgb(0x5A, 0xC8, 0xFA),
                tip: sameAsLatest
                    ? "当前固定的版本就是下载来源上的最新版"
                    : "没有跟着最新版走：引擎按固定的这个版本启动（想跟最新版可在下面改策略）");



            if (VersionMemory.PreviousPin.Length > 0)
                Row(isp, "上一长期版本", VersionMemory.PreviousPin);
            if (VersionMemory.ConsecutiveErrors > 0)
                // 正文只说次数：LastErrorReason 是引擎输出的原始英文行（英文原始异常文本），摆在界面上看不懂；
                // 技术细节按既定规矩收进悬停 —— 原文一行不失，排查能力不受影响。
                Row(isp, "连续异常", $"{VersionMemory.ConsecutiveErrors} 次",
                    Color.FromRgb(0xFF, 0x9F, 0x0A),
                    tip: "最近一次：\n" + VersionMemory.LastErrorReason);
            Row(isp, "下载来源", Registries.Label(_settings.Registry),
                tip: "点这一行可以切换下载来源。\n当前地址：" + Registries.Current);
            // 这一行只留中性结论：info.Error 是 VersionInfo 里 ex.Message 的原文（英文原始异常文本），
            //   摆在界面上用户看不懂 —— 全文由 RefreshVersionAsync 那条 NoteDiagnosis 落盘，悬停只给去处。
            //   （不再 Shorten 它上屏：同一行里塞半截英文，比不写更糟。）
            if (info?.Error != null)
                Row(isp, "查询失败", "暂时无法从下载来源获取",
                    Color.FromRgb(0xFF, 0x9F, 0x0A),
                    tip: LogPromise("详细原因已记入日志，可在「日志」页查看。"));

            // 「下载来源」做成可点的切换项：点一下在社区镜像 / 官方源之间切换（原先的「打开下载来源」按钮已撤掉）
            var sourceRow = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 8, 0, 0),
                Cursor = Cursors.Hand,
                ToolTip = "点一下切换下载来源（当前：" + Registries.Label(_settings.Registry) + "）\n地址：" + Registries.Current
            };
            var sourceInner = new StackPanel { Orientation = Orientation.Horizontal };
            sourceInner.Children.Add(new TextBlock
            {
                Text = "\uE8AB",                      // MDL2：切换箭头
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(0x5A, 0xC8, 0xFA))
            });
            sourceInner.Children.Add(new TextBlock
            {
                Text = "切换下载来源：当前 " + Registries.Label(_settings.Registry),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF7))
            });
            sourceRow.Child = sourceInner;
            sourceRow.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                try
                {
                    _settings.Registry = Registries.Toggle(_settings.Registry);
                    _settings.Save();
                    Registries.Configure(_settings.Registry);
                    // 写盘失败就不能只说"已切换"：重启后会回到原来的下载来源
                    var registryOutcome = SettingsOutcome("下载来源已切换为 " + Registries.Label(_settings.Registry));
                    AddEvent(registryOutcome.Text, registryOutcome.Kind);
                    RenderVersionView();
                    _ = RefreshVersionAsync();      // 换源后重新查一次最新版
                }
                catch (Exception ex) { Logger.LogError("ToggleRegistry", ex); }
            };
            var srcHost = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 0) };
            srcHost.Children.Add(sourceRow);
            isp.Children.Add(srcHost);

            // 与「当前版本」直接相关的操作就近放在这张卡里
            var infoBtns = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };

            var check = MiniButton("检查更新", "#34C759");
            check.ToolTip = "去下载来源看看有没有新版";
            check.Click += CheckUpdate_Click;
            infoBtns.Children.Add(check);

            if (VersionMemory.PendingUpdate)
            {
                var cancel = MiniButton("取消本次升级", "#FF9F0A");
                cancel.Margin = new Thickness(8, 0, 0, 0);
                cancel.ToolTip = "撤回刚才的「检查更新」，继续用当前版本";
                cancel.Click += CancelUpdate_Click;
                infoBtns.Children.Add(cancel);
            }
            isp.Children.Add(infoBtns);
            VersionPanel.Children.Add(infoCard);

            // ② 版本记忆：策略 + 版本筹码 + 运行履历（原「手动固定版本」「版本记忆」「操作」三块合并）
            var memCard = MakeCard(12);
            var msp = Body(memCard);
            msp.Children.Add(SimpleText("版本记忆", 13, Color.FromRgb(0x5A, 0xC8, 0xFA), true));
            // 只保留两条记录：当前版本与上一长期版本，并提供互相切换
            void VersionRow(string label, string version, bool isCurrent)
            {
                if (version.Length == 0) return;
                var rec = VersionMemory.History.FirstOrDefault(r => r.Version == version);
                var g = new Grid { Margin = new Thickness(0, 8, 0, 0) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var labelText = new TextBlock
                {
                    Text = label,
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93))
                };
                g.Children.Add(labelText);

                var detail = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                detail.Children.Add(new TextBlock
                {
                    Text = "DSH " + version,
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(isCurrent
                        ? Color.FromRgb(0x34, 0xC7, 0x59) : Color.FromRgb(0xF5, 0xF5, 0xF7))
                });
                string stats = rec == null
                    ? "（这次运行开始记录）"
                    : $"启动 {rec.Launches} 次 · 累计运行 {FormatSpan(rec.RunsSeconds)}";
                detail.Children.Add(new TextBlock
                {
                    Text = stats,
                    FontSize = 10.5,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93))
                });
                Grid.SetColumn(detail, 1);
                g.Children.Add(detail);

                if (isCurrent)
                {
                    var usingNow = new TextBlock
                    {
                        Text = "使用中",
                        FontSize = 11,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(10, 0, 2, 0),
                        Foreground = new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x59))
                    };
                    Grid.SetColumn(usingNow, 2);
                    g.Children.Add(usingNow);
                }
                else
                {
                    var swap = MiniButton("切换到它", "#FF9F0A");
                    swap.Tag = version;
                    swap.ToolTip = $"改用 DSH {version} 启动（下次启动生效）";
                    swap.Click += RollbackRow_Click;
                    Grid.SetColumn(swap, 2);
                    g.Children.Add(swap);
                }
                msp.Children.Add(g);
            }

            // 这里说的是"守护壳下次启动会用的那个版本"（版本记忆里的固定项），与上面"正在跑的版本"不同，
            // 所以换个不会混淆的名字 —— 现场就是两张卡都叫"当前版本"把人看懵的。
            VersionRow("当前版本", VersionMemory.Pin.Length > 0 ? VersionMemory.Pin : _currentDshVersion, true);
            if (VersionMemory.PreviousPin.Length > 0)
                VersionRow("上一长期版本", VersionMemory.PreviousPin, false);
            else
                msp.Children.Add(SimpleText("（尚无上一个长期版本：升级或切换过一次之后就会出现）",
                    11, Color.FromRgb(0x6E, 0x6E, 0x73)));

            // 两种策略的双向开关：按钮显示"另一种策略"，配色跟随按钮文字
            // （跟随最新版=蓝 #007AFF、自动管理=绿 #34C759），点一下即切换并在下方即时反映
            var memBtns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            bool followingLatest = VersionMemory.Pin.Length == 0;
            var follow = MiniButton(followingLatest ? "自动管理" : "跟随最新版",
                followingLatest ? "#34C759" : "#007AFF");
            if (followingLatest)
            {
                follow.ToolTip = "改回自动管理：把现在跑着的这个版本固定下来，升级或回退时才自动换";
                follow.Click += (_, _) =>
                {
                    string v = _currentDshVersion.Length > 0 ? _currentDshVersion : VersionMemory.Pin;
                    VersionMemory.PinAuto(v);
                    // 同上：把成功文案交给口径助手，失败时换成"没存上"的事实
                    var autoOutcome = VersionMemoryOutcome($"已改为自动管理（固定 {VersionMemory.Pin}）");
                    AddEvent(autoOutcome.Text, autoOutcome.Kind);
                    UpdateVersionCard();
                    RenderVersionView();
                };
            }
            else
            {
                follow.ToolTip = "跟随最新版：以后每次启动都联网取最新版本（相当于不固定版本）";
                follow.Click += FollowLatest_Click;
            }
            memBtns.Children.Add(follow);
            msp.Children.Add(memBtns);
            msp.Children.Add(SimpleText(
                "固定只影响守护壳怎么启动；从别处启动引擎不受影响。动版本之前建议先去「快照」页存一份，出问题能一键恢复。",
                10.5, Color.FromRgb(0x6E, 0x6E, 0x73)));
            VersionPanel.Children.Add(memCard);

            // ③ 守护壳**自身**的版本检测
            //   位置放在最后，理由：本页前两张卡（「运行中的 DSH」「版本记忆」）说的都是**引擎**用哪个版本，
            //   而这张说的是**本程序自己**有没有新版本 —— 两件事。摆在最后并单独起一个一眼可辨的标题，
            //   才不至于被读成"引擎的又一个版本位"；也不与既有那张引擎卡混在一起（用户明确要求分开）。
            var shellCard = MakeCard(12);
            var shp = Body(shellCard);
            shp.Children.Add(SimpleText("守护壳版本", 13, Color.FromRgb(0x5A, 0xC8, 0xFA), true));

            // 三态如实分开（见 GuardUpdateVerdict 的注释）：
            //   正在查 ⇒ 「正在检查…」；没查过 ⇒ 「尚未检查」（不下任何结论）；
            //   查成了 ⇒ 远端版本号；没问成 ⇒ 「暂时无法确定」。
            //   注意：这里**绝不**把"没问成"写进「已是最新」那一档 —— 那是谎报。
            string shellLatest = _guardUpdateBusy
                ? "正在检查…"
                : !_guardUpdateChecked
                    ? "尚未检查"
                    : _guardUpdateVerdict == GuardUpdateVerdict.Unknown
                        ? "暂时无法确定"
                        : (_guardRemoteVersion.Length > 0 ? _guardRemoteVersion : "暂时无法确定");

            Row(shp, "当前版本", GuardVersion.Version, Color.FromRgb(0x34, 0xC7, 0x59),
                tip: "本程序自身的版本号。与上面「运行中的 DSH」不是同一个：那是引擎的版本。");

            Row(shp, "最新版本", shellLatest,
                _guardUpdateVerdict == GuardUpdateVerdict.NewerAvailable
                    ? Color.FromRgb(0x5A, 0xC8, 0xFA)
                    : Color.FromRgb(0x8E, 0x8E, 0x93),
                tip: !_guardUpdateChecked
                    ? "点下方「检查更新」联网核对一次"
                    : _guardUpdateVerdict == GuardUpdateVerdict.Unknown
                        ? LogPromise("这次没能从发行版页面取到版本号，详细原因已记入日志。")
                        : "从本程序的发行版页面读到的版本号");

            // 状态行：先给"尚未检查"这一档兜底，再按实际情况覆盖 ——
            // 刻意不写成 if/else 链尾接 switch 的形态（那种写法下"变量是否已赋值"要交给
            // 编译器做可达性推理，本单禁 build、验不了，不如写成一眼可证的形态）。
            string shellState = "尚未检查";
            Color shellStateColor = Color.FromRgb(0x8E, 0x8E, 0x93);
            string? shellStateTip = null;
            if (_guardUpdateBusy)
            {
                shellState = "正在检查…";
            }
            else if (_guardUpdateChecked)
            {
                if (_guardUpdateVerdict == GuardUpdateVerdict.UpToDate)
                {
                    shellState = "已是最新";
                    shellStateColor = Color.FromRgb(0x34, 0xC7, 0x59);
                }
                else if (_guardUpdateVerdict == GuardUpdateVerdict.NewerAvailable)
                {
                    shellState = "有新版本可用";
                    shellStateColor = Color.FromRgb(0x5A, 0xC8, 0xFA);
                }
                else
                {
                    // 没问成 / 没得比：只陈述"这次没能确定"，并给去处（原文落在日志里）。
                    // 绝不弹错误框、绝不写成"已是最新"。
                    shellState = "暂时无法确定";
                    shellStateColor = Color.FromRgb(0xFF, 0x9F, 0x0A);
                    shellStateTip = LogPromise("详细原因已记入日志，可在「日志」页查看。");
                }
            }
            Row(shp, "状态", shellState, shellStateColor, tip: shellStateTip);

            var shellBtns = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };

            var shellCheck = MiniButton("检查更新", "#34C759");
            shellCheck.ToolTip = "联网核对本程序有没有新版本";
            shellCheck.Click += GuardCheckUpdate_Click;
            shellBtns.Children.Add(shellCheck);

            // 更新入口只在真有新版本时出现：没有可更新的东西就不摆一颗点了没用的按钮。
            // 界面上一律不出现网址 —— 地址只留在代码里。
            //
            // 语义（2026-09-20 改）：这颗按钮**不再是"去别处下载"**，而是**就地更新** ——
            // 点一下就在本程序里下好、校验、退出并交给安装程序。地址取不到或下载失败时，
            // 它自己会退回"打开下载页"那条老路（那条路是保底，绝不能丢）。
            if (_guardUpdateVerdict == GuardUpdateVerdict.NewerAvailable)
            {
                var shellGet = MiniButton("立即更新", "#007AFF");
                shellGet.Margin = new Thickness(8, 0, 0, 0);
                shellGet.ToolTip = "在本程序内下载并安装新版本（过程中会退出本程序，引擎不受影响）";
                shellGet.Click += GuardUpdateNow_Click;
                shellBtns.Children.Add(shellGet);
            }
            shp.Children.Add(shellBtns);

            shp.Children.Add(SimpleText(
                "这里查的是守护壳自己；上面「运行中的 DSH」是引擎的版本，两者互不影响。",
                10.5, Color.FromRgb(0x6E, 0x6E, 0x73)));
            VersionPanel.Children.Add(shellCard);

            ApplyThemeSoon();   // 新卡片要补刷主题

            ApplyBatchToolbarVisibility();   // 顶部这一行谁显谁隐：唯一一份规则
            UpdateBatchBar();   // 刷新勾选计数与显隐（末尾同样会同步一次外层显隐）
        }
        catch (Exception ex) { Logger.LogError("RenderVersionView", ex); }
    }

    // ══════════════ 守护壳自身的版本检测（设置 → 版本 → 「守护壳版本」卡） ══════════════
    /// <summary>「检查更新」按钮入口（与本文件其余按钮处理器同形：<c>async void</c> + 内部全包 try）。</summary>
    private async void GuardCheckUpdate_Click(object sender, RoutedEventArgs e) => await CheckGuardUpdateAsync();

    /// <summary>
    /// 点「检查更新」：去本程序自己的发行版页面核对一次。
    ///
    /// 交互（逐步）：
    ///   ① 置忙、立即重绘 ⇒ 卡上「最新版本」「状态」两行当场显示「正在检查…」；
    ///   ② 查询（<see cref="PluginSource.FetchGuardLatestReleaseAsync"/>，超时 12 秒，
    ///      网络类失败只落 [WARN] 诊断，不弹任何错误框）；
    ///   ③ 重绘 ⇒ 三态之一：
    ///        · 有新版本 ⇒ 「最新版本」写出远端版本号、「状态」写「有新版本可用」，并多出一颗「立即更新」；
    ///        · 已是最新 ⇒ 「状态」写「已是最新」，不出下载按钮；
    ///        · 没问成 ⇒ 「最新版本」与「状态」都写「暂时无法确定」，悬停给去处（原文在日志里），
    ///          绝不写成"已是最新"，也绝不弹错误框；
    ///   ④ 真有新版本时额外在事件栏留一条（整个会话只留一次，不刷屏）。
    ///
    /// 为什么整段包在 try 里：本方法是 <c>async void</c> 链上的入口（由按钮点击触发），
    /// 未捕获的异常会直接掀掉进程；而查询本身已经"绝不抛"，这里兜的是重绘与状态写入。
    /// </summary>
    private async Task CheckGuardUpdateAsync()
    {
        if (_guardUpdateBusy) return;      // 挡住重入：连点不并发发多次请求
        _guardUpdateBusy = true;
        try
        {
            RenderVersionView();           // 先让「正在检查…」当场可见
            var (verdict, version, _) = await PluginSource.FetchGuardLatestReleaseAsync();

            _guardUpdateVerdict = verdict;
            _guardRemoteVersion = version;
            _guardUpdateChecked = true;

            if (verdict == GuardUpdateVerdict.NewerAvailable && version.Length > 0 && !_guardUpdateNotified)
            {
                _guardUpdateNotified = true;
                AddEvent($"发现守护壳新版本 {version}，去「设置 → 版本」可以下载", EventKind.Update);
            }
        }
        catch (Exception ex)
        {
            // 走到这里说明重绘/写状态自己抛了：如实记 [ERROR]，并把结论退回"暂时无法确定"
            //（宁可说"这次没确定"，也不许因为一次内部异常就显示"已是最新"）。
            Logger.LogError("CheckGuardUpdateAsync", ex);
            _guardUpdateVerdict = GuardUpdateVerdict.Unknown;
            _guardRemoteVersion = "";
            _guardUpdateChecked = true;
        }
        finally
        {
            _guardUpdateBusy = false;
            RenderVersionView();           // 收尾一定重绘（异常路径同样如此），按钮不会一直停在"正在检查"
        }
    }

    /// <summary>
    /// 点「立即更新」：**在本程序里**把新版本下好、校验、退出并交给安装程序。
    ///
    /// ══ 顺序（关键，反了就会失败）══
    ///   ① 查一次最新发行版，顺手拿到安装包直链（<see cref="PluginSource.FetchGuardLatestReleaseDetailedAsync"/>）；
    ///   ② 下载到临时目录的专属子目录（带真进度、可取消）；
    ///   ③ 校验（名字 + 大小，见 <see cref="PluginSource.DownloadGuardSetupAsync"/>）；
    ///   ④ **问过用户**之后才退出本程序；
    ///   ⑤ 本程序退出之后，才由外部进程把安装程序拉起来。
    ///
    /// 为什么 ④ 必须在 ⑤ 之前：安装程序要覆盖 <c>DSHGuard.exe</c>，而**正在运行的 exe 无法被覆盖**。
    ///   先拉起安装器就会卡在"文件被占用"，或者更糟 —— 装到一半失败，留下半套程序。
    ///   本项目刚在别处踩过"文件被占用"这个坑，这里不再重演。
    ///
    /// 为什么不在这里 <c>Process.Start</c> 安装器就完事：那需要本进程先退出，而本进程一退出，
    ///   它启动的子进程会**一起被带走**。所以交给一个独立的 powershell 小进程：它先睡够时间
    ///   等本程序真正退出，再去拉起安装程序并等它结束，最后把那只安装包删掉（见 ⑤ 里的脚本）。
    ///
    /// 降级路径（一条都不能丢）：拿不到直链 / 白名单不放行 / 下载失败 / 校验不过 / 用户取消
    ///   ⇒ 一律退回「打开下载页」那条老路，用户照旧装得上，只是多两步。
    /// </summary>
    private async void GuardUpdateNow_Click(object sender, RoutedEventArgs e) => await GuardUpdateNowAsync();

    private async Task GuardUpdateNowAsync()
    {
        if (_guardUpdateBusy) return;      // 与「检查更新」共用同一道重入闸：两个入口不许并发
        _guardUpdateBusy = true;

        GuardUpdateProgressWindow? ui = null;
        bool owned = false;
        bool handedOff = false;

        // 取消令牌在**流程一开始**就建好，而不是等「② 下载」那一步 —— 这是刻意的：
        //   · OpenGuardUpdateProgress() 在下面、令牌**建好之后**才被调用，所以它挂上的 CancelRequested
        //     从一开始就握着一个活令牌：「查版本 / 下载 / 校验 / 收尾」任何一步点「取消下载」都点得动；
        //   · 否则「查版本」那一段里字段还是 null（或上一轮遗留的旧令牌），按钮点了没反应。
        // 释放**不在这里**：仍由下方 finally 里既有的 `_guardUpdateCts?.Dispose(); _guardUpdateCts = null;` 收口。
        _guardUpdateCts = new System.Threading.CancellationTokenSource();
        // 本地引用取名 flowCts 而**不是** cts：下载循环体内部另有一个作用域更深的既有 cts，
        // 两者同名会触发 CS0136（局部名遮蔽外层局部名，嵌套局部函数同样适用）。
        // 故只给外层换一个不冲突的名字，内层那行**一字不改**，仍然"取本地引用"。
        var flowCts = _guardUpdateCts;     // 取本地引用：await 之后字段可能已被收尾清空

        try
        {
            ui = OpenGuardUpdateProgress();
            owned = ui != null;
            EnsureGuardUpdateExitGuard();      // 退出兜底只挂一次（进程被关时收拾半截状态）

            // ⓪ 先把进度推到"正在检查"
            ui?.SetStage(PluginSource.GuardUpdateProgress.Check, "正在检查新版本…");

            // ① 查一次（与「检查更新」同一发查询、同一份判据，不另发一次请求）
            var (verdict, version, _, asset) = await PluginSource.FetchGuardLatestReleaseDetailedAsync();
            _guardUpdateVerdict = verdict;
            _guardRemoteVersion = version;
            _guardUpdateChecked = true;

            if (verdict != GuardUpdateVerdict.NewerAvailable)
            {
                FinishGuardUpdate(ui, owned, "没有可更新的新版本。");
                RenderVersionView();
                return;
            }

            // ②′ 没有直链（发行版没发 / 附件名对不上 / 地址没过白名单）⇒ 退回下载页那条老路。
            //    这是**正常降级**，不是错误：如实说明 + 打开老路，绝不弹错误框。
            if (asset == null)
            {
                Logger.NoteDiagnosis("应用内更新：本次没有取到可自动安装的安装包直链（或地址未通过白名单），"
                                   + "已退回打开下载页");
                FinishGuardUpdate(ui, owned, "已改为打开下载页。");
                FallBackToDownloadPage(version, "这次没能直接取得安装包");
                return;
            }

            // ② 下载（真进度：按已收字节数换算）
            // 取消令牌已在方法开头建好（见那里的注释），此处**复用、不再新建**：重建会把"查版本"
            // 期间用户点下的取消丢掉，且旧令牌无人释放。
            var cts = _guardUpdateCts;         // 取本地引用：await 之后字段可能已被收尾清空
            // ②′ 从「下载」到「就绪」这一整段收进一个**局部异步函数**：这样它能与下面的整程兜底赛跑，
            //     而函数体内的 return 语义与重构前**完全一致**（return 只结束这一路，收尾照旧交给外层 finally）。
            async Task RunGuardUpdateFlowAsync()
            {
            // ②″ 记下开始下载的时刻，供进度回调判断"这次是不是慢得不像话"。
            //      为什么用**局部变量**而不用字段：这个回调只在 DownloadGuardSetupAsync 存活期间被调用，
            //      而它正是在本方法里创建、也在本方法里 await 完的，所以 lambda 捕获局部变量在时序上绝对安全；
            //      用局部变量还免去了"给窗口加一个只有本次下载才有意义的状态字段"的污染，也不存在两次下载互相覆盖的问题
            //      （对照本文件 BeginOpProgress 的 _opProgressStart：那是字段，因为它的定时器活得比方法长，本处不一样）。
            DateTime guardDownloadStartedAt = DateTime.Now;

            var progress = new Progress<double>(pct =>
            {
                try
                {
                    // ②‴ 下载拖太久 ⇒ 在百分比后面补一句网络环境提示。
                    //      阈值取自 PluginSource.GuardSetupSlowHintSeconds（更新链路的阈值统一收在那份常量里，
                    //      这里只引用、不另抄一个 60，避免以后两处数字各改各的而漂移）。
                    double sec = (DateTime.Now - guardDownloadStartedAt).TotalSeconds;
                    string text = sec >= PluginSource.GuardSetupSlowHintSeconds
                        ? $"正在下载更新（{pct:0}%，网络环境不佳，建议手动下载）"
                        : $"正在下载更新（{pct:0}%）";
                    ui?.SetStage(pct, text);
                }
                catch { }
            });

            var dl = await PluginSource.DownloadGuardSetupAsync(asset, progress, cts.Token);

            // ③ 校验没过 / 中断 ⇒ 半截文件已由下载器删掉，这里退回下载页。
            //    用户自己点的「取消下载」另作一路：**安静收场**，不弹框、也不硬塞一个网页
            //    （用户刚说了不要，再弹一个浏览器的行为只会让人以为程序不听话）。
            if (!dl.Ok)
            {
                if (dl.Cancelled)
                {
                    Logger.NoteDiagnosis("应用内更新：用户取消了下载，半截文件已清理，本程序保持运行");
                    FinishGuardUpdate(ui, owned, "已取消下载。");
                    AddEvent("已取消下载，随时可以再点「立即更新」", EventKind.Warn);
                    RenderVersionView();
                    return;
                }
                Logger.NoteDiagnosis($"应用内更新未完成：{dl.Message}（{dl.Raw}）⇒ 退回打开下载页");
                FinishGuardUpdate(ui, owned, dl.Message + "。");
                FallBackToDownloadPage(version, dl.Message + "，已改为打开下载页");
                return;
            }

            ui?.SetStage(PluginSource.GuardUpdateProgress.Verify, "正在核对更新文件…");
            ui?.SetStage(PluginSource.GuardUpdateProgress.Ready, "更新已就绪，等待确认…");

            // ④ 用户知情：明确告知"会退出本程序、引擎不受影响"，点确认才开始退出
            FinishGuardUpdate(ui, owned, "更新已就绪。");
            owned = false; ui = null;

            var ok = GuardDialog.ShowCustom(
                "新版本的安装包已下载完成并通过核对。\n\n"
                + "· 接下来本程序会退出，然后自动开始安装，请按安装向导完成。\n"
                + "· 安装完成后再重新打开本程序即可。\n"
                + "· 安装只覆盖本程序自身，正在运行的 DSH 引擎不会受影响、也不会被中断。",
                "开始安装更新", MessageBoxImage.Question,
                new GuardDialog.DialogButton("退出并安装", MessageBoxResult.Yes, Color.FromRgb(0x34, 0xC7, 0x59), IsDefault: true),
                new GuardDialog.DialogButton("稍后再说", MessageBoxResult.No, Color.FromRgb(0x8E, 0x8E, 0x93), IsCancel: true));

            if (ok != MessageBoxResult.Yes)
            {
                // 用户改主意：安装包留着没用 ⇒ 当场删掉（不留 68 MB 的残留），并留一条 [WARN]。
                // 下次再点「立即更新」会重新下一次，不会用到这只旧文件。
                bool removed = PluginSource.DeleteStagedGuardSetup(dl.Path);
                Logger.NoteDiagnosis($"应用内更新：用户在确认框选择了稍后再说，已放弃本次安装"
                                   + $"（暂存文件{(removed ? "已删除" : "暂未能删除，将在下次启动时清理")}）");
                AddEvent("已取消本次更新，随时可以再点「立即更新」", EventKind.Warn);
                RenderVersionView();
                return;
            }

            // ④′ 用户已确认 ⇒ 重新锁住界面，直到本程序退出为止。
            //     为什么这里要**再锁一次**：上面为了弹确认框把进度窗收了（确认框需要用户点），
            //     但接下来的"写盘探测 / 复制上一版 / 交接安装器"几步同样不该让用户乱点 ——
            //     尤其"复制上一版"要搬几十 MB，中间被误操作打断就会少一层断电冗余。
            //     用户已经明确选择了「退出并安装」，此刻锁住是符合他预期的（不会让人觉得莫名其妙）。
            ui = OpenGuardUpdateProgress();
            owned = ui != null;
            ui?.SetStage(PluginSource.GuardUpdateProgress.Ready, "正在准备安装…");

            // ⑤ 退出**之前**的最后一道硬闸：确认安装目录现在真的写得进去。
            //
            // 为什么非要在这里查：更新最坏的结局不是"下载失败"，而是**装到一半**——
            // 本程序已经退出了，安装器却发现目标目录写不进去（只读、权限被改、
            // 磁盘满、被安全软件锁住），于是用户既没有旧程序、也没有新程序。
            // 一次"能不能写"的探测只需一毫秒，却能把这一类结局在退出之前拦下来：
            // 写得进才退出，写不进就**根本不退出**，如实说明并退回下载页。
            string? blocked = GuardSetupTargetBlocked();
            if (blocked != null)
            {
                Logger.NoteDiagnosis($"应用内更新：安装目录当前不可写（{blocked}），已中止退出并退回下载页");
                PluginSource.DeleteStagedGuardSetup(dl.Path);
                FinishGuardUpdate(ui, owned, "安装位置当前不可写入。");
                FallBackToDownloadPage(version, "安装位置当前不可写入，已改为打开下载页");
                return;
            }

            // ⑤ 安全冗余：把**当前这一版程序**复制一份到暂存目录（见 PluginSource.BackupCurrentGuardExe）。
            //     它要对付的是最狠的一种情形：覆盖安装期间断电 / 蓝屏，导致安装目录里的程序**起不来**。
            //     到那时本程序已经无法启动，"下次启动时告知"这条兜底也一起失效，
            //     唯一还能把用户救回来的就是磁盘上这两样东西 —— 一个能重跑安装的完整安装包，
            //     和一个**确定能用的旧版程序**。少一样，用户就只剩下"重新下载"这一条路。
            //     备份失败**不阻断**更新（只是少一层冗余，BackupCurrentGuardExe 内部自己会记日志）。
            ui?.SetStage(PluginSource.GuardUpdateProgress.Ready, "正在准备安装…");
            PluginSource.BackupCurrentGuardExe();

            // ⑥ 写下"打算装到哪个版本"：下次启动据此如实报告到底装成了没有。
            //    位置压在交接**之前**：交接成功之后本程序随时可能被用户关掉，
            //    那时若还没写记账，下次启动就再也发现不了"装到一半"这件事。
            PluginSource.WriteGuardUpdatePending(version);

            // ⑦ 本程序退出**之后**才拉起安装程序：顺序不能反（见方法注释）
            if (!LaunchGuardSetupAfterExit(dl.Path))
            {
                // 交接失败（脚本没起来）⇒ 别退出！留在程序里如实说明，并给老路。
                // 记账一并撤掉：什么都没交给安装器，不该让下次启动报"上次更新没完成"。
                Logger.NoteDiagnosis("应用内更新：安装程序未能交接成功，已中止退出，退回打开下载页");
                PluginSource.ClearGuardUpdatePending();
                PluginSource.DeleteStagedGuardSetup(dl.Path);
                FinishGuardUpdate(ui, owned, "安装程序启动失败。");
                FallBackToDownloadPage(version, "安装程序启动失败，已改为打开下载页");
                return;
            }

            handedOff = true;
            _guardUpdateHandedOff = true;      // 交接成功：此后**任何**收尾都不许再删暂存文件

            // 显式放行关窗（FinishAndClose 会把窗口自己的"只许我关"闸置真），**不能靠 WPF 退出期
            // 自动关窗**：本窗默认拦截一切关闭请求，若指望"应用退出时它会放行"，一旦那套语义
            // 不成立（或顺序不同），退出就会被一扇不肯关的窗口卡住 —— 正是本项目反复遭遇的
            // "无法关闭的窗口"。这里先把窗关掉、再退出，退出路径上就没有任何东西能拦。
            ui?.SetStage(PluginSource.GuardUpdateProgress.Ready, "即将退出并开始安装…");
            FinishGuardUpdate(ui, owned);
            owned = false; ui = null;

            AddEvent("更新已就绪，本程序即将退出并开始安装（引擎不受影响）", EventKind.Update);
            Logger.NoteDiagnosis($"应用内更新：安装包已交接，本程序即将退出以完成覆盖安装（版本 {version}）");
            // ⚠️ skipPluginWorkCheck: true —— 这一次退出是**本程序自己发起的**，不是用户随手点退出：
            //   用户已经在更新进度窗上确认过"立即更新并退出"，进度窗也在上面 FinishGuardUpdate 收掉了，
            //   再弹一次"插件操作进行中，确定要退出吗"既是重复询问，用户也没有任何东西可以"等它跑完"
            //   （眼前根本没有进度条）。安装包已交接，此刻唯一该做的就是退出去让它覆盖安装。
            //   托盘「退出」与「退出UI」按钮那两处**不传**（默认 false = 照旧拦一下）。
            ExitGuardAsync(skipPluginWorkCheck: true);   // 正常退出这条路（**不断引擎**，与「退出UI」同一条）
            }   // ← RunGuardUpdateFlowAsync 到此结束（本地函数体故意不缩进：整段流程一字未改，便于对照）

            // 整程兜底：把上面整段流程与一个 25 分钟的上限赛跑。
            // 为什么用 Task.WhenAny + Task.Delay(上限)，而不是 WaitAsync(上限, cts.Token)：
            //   WaitAsync 的取消令牌与「用户点取消下载」共用同一个源 —— 用户一取消就抛
            //   OperationCanceledException，超时与用户取消在异常里**无法区分**，而这两者收场方式
            //   完全不同（超时要如实报超时 + 退回下载页，取消要安静收场）。
            //   这里让超时只表现为"延迟任务先完成"，用户取消则照原样从 flowTask 里体现出来，两者天然分开。
            var flowTask = RunGuardUpdateFlowAsync();
            var timeoutTask = Task.Delay(GuardUpdateTotalTimeout, flowCts.Token);
            if (await Task.WhenAny(flowTask, timeoutTask) != flowTask)
            {
                // 走到这里只有两种可能：① 整程超时；② 用户在等待期间点了取消（Delay 跟着被取消）。
                // 所以必须再看一眼用户是否已经取消过，绝不能把用户取消也报成超时（那会平白弹一句超时、还硬塞一个网页）。
                if (cts.IsCancellationRequested)
                {
                    // 用户主动取消：走与「③ 下载」那里**同一条**安静收场路径（不弹框、不打开下载页）。
                    Logger.NoteDiagnosis("应用内更新：用户取消了更新流程，已安静收场，本程序保持运行");
                    FinishGuardUpdate(ui, owned, "已取消下载。");
                    AddEvent("已取消下载，随时可以再点「立即更新」", EventKind.Warn);
                    RenderVersionView();
                }
                else
                {
                    // 真·整程超时：先取消令牌让还在跑的下载循环立刻退出（不留下跑不完的后台任务），
                    // 再照既有失败分支的写法如实说明并退回下载页 —— 不静默挂死，也不弹错误框。
                    try { cts.Cancel(); } catch { }
                    Logger.NoteDiagnosis($"应用内更新：整程超过 {GuardUpdateTotalTimeout.TotalMinutes:0.#} 分钟上限仍未完成，"
                                       + "已中止本次更新并退回下载页");
                    FinishGuardUpdate(ui, owned, "更新超时，已中止。");   // 该调用内部会关窗，主窗可用状态随之恢复
                    FallBackToDownloadPage(version, $"更新超过 {GuardUpdateTotalTimeout.TotalMinutes:0.#} 分钟仍未完成，已改为打开下载页");
                }
                return;   // 注意：这里只结束本次更新流程（ui/owned/handedOff 由外层 finally 收口），
                          // 与上面各失败分支一样**不退程序**，主窗必须回到可用。
            }

            // 没超时 ⇒ 流程已跑完。等一下是为了看看它有没有异常：
            //   · 用户取消 ⇒ IsCanceled，下面的 catch 会安静收场（走已有的「已取消下载」措辞）；
            //   · 真异常     ⇒ 照旧由 catch 记日志并如实说明。
            await flowTask;
        }
        // 用户取消 ⇒ 安静收场，**不弹错误框**（他刚说了不要，再弹一个框只会让人以为程序不听话）：
        // 走的是与「③ 下载」那里同一条「已取消下载」路径，只不过取消发生在 await 边界上、被翻译成了异常。
        catch (OperationCanceledException)
        {
            Logger.NoteDiagnosis("应用内更新：用户取消了更新流程，已安静收场，本程序保持运行");
            FinishGuardUpdate(ui, owned, "已取消下载。");
            AddEvent("已取消下载，随时可以再点「立即更新」", EventKind.Warn);
            RenderVersionView();
        }
        catch (Exception ex)
        {
            Logger.LogError("GuardUpdateNowAsync", ex);
            FinishGuardUpdate(ui, owned, "更新中断。");
            GuardDialog.Show("更新过程中出现异常，已中止。本程序不会退出，你可以在「日志」页查看详细原因。",
                "更新未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (!handedOff) FinishGuardUpdate(ui, owned, "更新已结束。");
            _guardUpdateCts?.Dispose();
            _guardUpdateCts = null;
            _guardUpdateBusy = false;
        }
    }

    /// <summary>
    /// 退回"打开下载页"那条老路（保底）。<paramref name="why"/> 为空时不弹框（调用方自己已经说明过）。
    /// 走的是既有闸门 <c>OpenExternalLink</c>：地址由本程序自己的常量拼出，不取远端报文里的任何字段。
    /// </summary>
    private void FallBackToDownloadPage(string version, string why)
    {
        try
        {
            if (why.Length > 0)
            {
                AddEvent($"{why}，已为你打开下载页", EventKind.Warn);
                GuardDialog.Show(
                    "这次没能自动完成更新，已为你打开下载页，可以手动下载安装最新版本。\n\n"
                    + "本程序不会因此退出，当前版本可以继续正常使用。",
                    "改为手动下载", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            if (OpenExternalLink(PluginSource.GuardReleasesPageUrl(), "守护壳版本卡更新保底入口"))
                Logger.NoteDiagnosis($"守护壳版本卡：已打开发行版页面供用户手动下载（{version}）");
        }
        catch (Exception ex) { Logger.LogError("FallBackToDownloadPage", ex); }
    }

    /// <summary>
    /// 把安装包交给一个**独立于本进程**的 powershell 小进程，由它在等待本程序退出之后启动安装程序。
    ///
    /// 为什么不能在本进程里 <c>Process.Start</c>：那要求本进程先退出，而本进程一退出，
    /// 它启动的子进程会**一起被带走** —— 安装程序还没开始就被杀掉，用户落得"程序关了、什么也没装"。
    /// 所以必须交给一个不属于本进程树的独立进程。
    ///
    /// 脚本按顺序做四件事（全在这一段字符串里，没有外部脚本文件）：
    ///   ① 等本程序真正退出（按 PID 轮询，最多约 24 秒）——**这一步是"避免覆盖正在运行的文件"的关键**，
    ///      本程序没退干净就启动安装程序只会撞上文件占用；
    ///   ② 启动安装程序，并在随后的 10 秒内确认它**真的起来了**；
    ///      没起来（交接失败）就把本程序重新拉起来 —— 绝不让用户停在"没有程序可用"的状态；
    ///   ③ 等安装程序结束（最多 30 分钟）；
    ///   ④ **删掉暂存目录** —— 这是"不留更新残留"的收口：安装器只在运行时需要这只安装包，
    ///      它一结束就没人认领了。删不掉也不会留下垃圾：程序每次启动还会补删一次
    ///      （见 <see cref="PluginSource.SweepGuardUpdateStaging"/>）。
    ///
    /// 路径全部经 <c>Replace("'", "''")</c> 转义后放进**单引号**里：单引号字符串不做展开，
    /// 用户目录里的 <c>$</c> 之类不会被 powershell 当变量解释。
    /// </summary>
    private bool LaunchGuardSetupAfterExit(string setupPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(setupPath) || !File.Exists(setupPath)) return false;

            static string Q(string s) => "'" + (s ?? "").Replace("'", "''") + "'";

            string dir = PluginSource.GuardUpdateStagingDir;
            string exe = Path.Combine(GuardPaths.ExeDir, "DSHGuard.exe");
            string script =
                "$ErrorActionPreference='SilentlyContinue';"
                + $"$p={Q(setupPath)}; $exe={Q(exe)};"
                // ① 等本程序退出
                + $"for($i=0;$i -lt 60;$i++){{ if(-not (Get-Process -Id {Environment.ProcessId} -ErrorAction SilentlyContinue)){{break}};"
                + " Start-Sleep -Milliseconds 400 };"
                + "Start-Sleep -Milliseconds 1200;"
                // ② 启动安装程序，并确认它真的起来了；没起来就把本程序拉回来
                + "try { Start-Process -FilePath $p } catch { };"
                + "$up=$false;"
                + "for($i=0;$i -lt 25;$i++){ if(Get-Process -Name 'DSHGuard-Setup*' -ErrorAction SilentlyContinue){$up=$true;break};"
                + " Start-Sleep -Milliseconds 400 };"
                + "if(-not $up){ try { Start-Process -FilePath $exe } catch { }; exit };"
                // ③ 等安装程序结束
                + "for($i=0;$i -lt 900;$i++){ if(-not (Get-Process -Name 'DSHGuard-Setup*' -ErrorAction SilentlyContinue)){break};"
                + " Start-Sleep -Seconds 2 };"
                + "Start-Sleep -Seconds 2;"
                // ④ 清掉暂存目录
                + $"Remove-Item -LiteralPath {Q(dir)} -Recurse -Force -ErrorAction SilentlyContinue;";

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -WindowStyle Hidden -Command \"" + script.Replace("\"", "\\\"") + "\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = System.Diagnostics.Process.Start(psi);
            bool started = p != null;
            Logger.NoteDiagnosis(started
                ? "应用内更新：已交接给独立进程（等本程序退出后启动安装程序；起不来会把本程序拉回来；收尾清理暂存文件）"
                : "应用内更新：交接进程未能启动");
            return started;
        }
        catch (Exception ex)
        {
            Logger.LogError("LaunchGuardSetupAfterExit", ex);
            return false;
        }
    }

    /// <summary>
    /// 打开更新进度窗并把主窗置为不可用（**伪模态**，照 <c>MainWindow.RollbackProgress</c> 的先例）：
    ///   · 主窗 <c>IsEnabled = false</c>：WPF 中被禁用的元素不参与命中测试 ⇒ 按钮/下拉/卡片一律点不动，
    ///     与"回滚涉及插件时锁住整个程序"是同一套做法（用户明确点名要用那套）；
    ///   · 进度窗 <c>Show()</c> 非阻塞、刻意**不置顶**（靠 <c>Owner = this</c> 从属于主窗，见方法内注释）⇒ 进度条照常动、看得见，锁的是**操作**不是**显示**；
    ///   · 弹不出来也**不能**阻断更新：进度窗只是给人看的，建不出来就当没弹（仍继续更新）。
    /// 收尾一律走 <see cref="CloseGuardUpdateProgress"/>，它有 finally 兜底（见那边的注释）。
    /// </summary>
    private GuardUpdateProgressWindow? OpenGuardUpdateProgress()
    {
        try
        {
            if (_guardUpdateProgress != null) return _guardUpdateProgress;   // 幂等

            var dlg = new GuardUpdateProgressWindow();
            dlg.Owner = this;
            dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            // 故意**不设** Topmost：置顶的进度窗用户切到别的程序也甩不掉，等于把用户**锁在**这个状态里
            // （升级包可能要下几分钟，中途去看别的窗口是极正常的操作）。
            // 不置顶也不会被埋掉：下面 Owner = this 使本窗**从属于主窗**，主窗最小化它一起最小化、
            // 主窗被激活时它随主窗回到前面；窗口中心又贴着主窗，主窗在哪儿它就在哪儿。
            // 「取消下载」：只中断下载，半截文件由下载器删掉；确认退出那一步另有确认框。
            dlg.CancelRequested += () =>
            {
                try
                {
                    _guardUpdateCts?.Cancel();
                    Logger.NoteDiagnosis("应用内更新：用户点了「取消下载」，已发出取消信号");
                }
                catch (Exception ex) { Logger.LogError("GuardUpdateProgressWindow 取消", ex); }
            };
            // 「发布页面」：直通发行版页面。走的是保底入口 FallBackToDownloadPage 用的**同一条**闸门
            // （地址由本程序自己的常量拼出，不取远端报文里的任何字段）。
            // 点了**不关**这个进度窗 —— 用户可能只是想自己先去下载，同时还想让自动那条继续等着。
            dlg.ReleasesRequested += () =>
            {
                try
                {
                    if (OpenExternalLink(PluginSource.GuardReleasesPageUrl(), "GuardUpdateProgress"))
                        Logger.NoteDiagnosis("应用内更新：用户点了「发布页面」，已打开发行版页面供手动下载");
                }
                catch (Exception ex) { Logger.LogError("GuardUpdateProgressWindow 发布页面", ex); }
            };
            dlg.Closed += (_, _) =>
            {
                // 无论窗口怎么关掉的，主窗都必须回到弹窗前的可用状态
                try { IsEnabled = _guardUpdateOwnerEnabled; } catch { }
                _guardUpdateProgress = null;
            };

            _guardUpdateOwnerEnabled = IsEnabled;
            IsEnabled = false;          // 伪模态：更新期间主窗一律点不动
            dlg.Show();
            try { dlg.Activate(); } catch { }
            _guardUpdateProgress = dlg;
            return dlg;
        }
        catch (Exception ex)
        {
            Logger.LogError("OpenGuardUpdateProgress", ex);
            try { IsEnabled = _guardUpdateOwnerEnabled; } catch { }   // 弹不出来也不能把主窗留在"点不动"
            _guardUpdateProgress = null;
            return null;
        }
    }

    /// <summary>
    /// 关闭更新进度窗并还原主窗可用。**幂等**；主窗还原在这里、也在窗口的 <c>Closed</c> 里各做一次
    /// （同值重写），因为"看着正常、点哪儿都没反应"是本项目反复遭遇的问题，宁可写两遍。
    ///
    /// <paramref name="stepText"/> 为空则不碰文案；<paramref name="percent"/> 默认负数 = **不推进进度条**，
    /// 只把说明那一行换成如实的收尾文案。中止 / 失败路径一律用它 ——
    /// 没装成却把进度条补满到 100%，正是本项目最反感的"谎报"。
    /// </summary>
    private void FinishGuardUpdate(GuardUpdateProgressWindow? ui, bool owned, string stepText = "",
                                   double percent = -1)
    {
        try
        {
            if (ui != null && stepText.Length > 0) ui.SetStage(percent, stepText);
        }
        catch { }
        CloseGuardUpdateProgress(owned);
    }

    /// <summary>关窗 + 还原主窗可用（幂等）。<paramref name="owned"/> 为假时一个属性都不碰。</summary>
    private void CloseGuardUpdateProgress(bool owned)
    {
        var dlg = _guardUpdateProgress;
        _guardUpdateProgress = null;
        try { dlg?.FinishAndClose(); }
        catch (Exception ex) { Logger.LogError("CloseGuardUpdateProgress", ex); }

        // 兜底二连：万一窗口因任何原因没关成（Closed 没触发），主窗也必须回到可用。
        if (owned || dlg != null)
        {
            try { IsEnabled = _guardUpdateOwnerEnabled; } catch { }
        }
    }

    /// <summary>
    /// 退出兜底（只挂一次）：本程序在**不是走完更新流程**的情况下退出时，把两样东西收拾干净 ——
    ///   ① 取消正在进行的下载（否则那只半截文件会在进程死后留在磁盘上，正是"更新残留"）；
    ///   ② 主窗的可用状态还原（伪模态锁是挂在窗口上的，进程退出时窗口没了，但状态要留住口径）。
    ///
    /// ⚠ <b>唯独不删已交接的安装包</b>：那一条路径上，安装器马上就要用它来覆盖本程序，
    ///   此刻删掉就等于把更新掐死在最后一秒（用户落得"程序关了、什么也没装"）。
    ///   判据是 <see cref="_guardUpdateHandedOff"/> —— 只有交接**成功**之后才为真。
    ///
    /// 挂 <c>Exit</c> 事件（不挂 <c>Closing</c>）：本程序正常退出与托盘退出都走
    /// <c>Application.Current.Shutdown()</c>，<c>Exit</c> 是两条路的公共收口，且不会拦下退出本身
    /// （本程序对"关不掉的窗口"有过教训，这里绝不再加一道拦截）。
    /// </summary>
    private void EnsureGuardUpdateExitGuard()
    {
        try
        {
            if (_guardUpdateExitGuardOn) return;
            _guardUpdateExitGuardOn = true;
            Application.Current.Exit += (_, _) =>
            {
                try
                {
                    if (!_guardUpdateHandedOff)
                    {
                        // 没交接 ⇒ 安装器不会来用这只文件 ⇒ 半截文件必须清掉（不留残留）
                        try { _guardUpdateCts?.Cancel(); } catch { }
                    }
                    try { IsEnabled = _guardUpdateOwnerEnabled; } catch { }
                }
                catch (Exception ex) { Logger.LogError("GuardUpdateExitGuard", ex); }
            };
        }
        catch (Exception ex) { Logger.LogError("EnsureGuardUpdateExitGuard", ex); }
    }

    /// <summary>
    /// 安装目录现在能不能被覆盖写入？返回 null = 可以；否则返回一句给**日志**的原因（不是给用户看的）。
    ///
    /// 判据只有一条、且是**真写一次**：在安装目录里建一个临时文件再删掉。
    /// 为什么不用"看文件属性只读位""看目录 ACL""看磁盘剩余空间"这些间接判据：
    /// 它们每一个都只能覆盖一部分原因（只读位管不了权限、ACL 管不了磁盘满、剩余空间管不了占用），
    /// 而真正要知道的问题只有一个 —— **"能不能写进去"**。直接写一次，答案就是答案。
    ///
    /// 探测文件建在安装目录里（不是临时目录）：要验的正是那个要被安装器覆盖的位置。
    /// 探测文件当场删掉、名字固定带 .tmp 后缀，绝不留下垃圾；万一删不掉也只是一只 0 字节文件，
    /// 而这一档本身已经意味着"这个目录写不进去"，用户看到的提示会指向真正的问题。
    /// </summary>
    private string? GuardSetupTargetBlocked()
    {
        string dir = GuardPaths.ExeDir;
        string probe = Path.Combine(dir, ".dshguard-write-probe.tmp");
        try
        {
            if (!Directory.Exists(dir)) return "安装目录不存在";
            File.WriteAllText(probe, "");
            return null;
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            try { if (File.Exists(probe)) File.Delete(probe); } catch { }
        }
    }

    /// <summary>
    /// 启动时补删上一轮遗留的更新暂存目录（只跑一次），并**如实报告上一次更新到底装成了没有**。
    ///
    /// 这是"不留更新残留"的最后一层，也是"更新到一半"唯一能被发现的地方：
    /// 安装包交给安装器之后本程序就退出了，从那以后发生什么本程序一无所知 ——
    /// 若安装器最终没跑成、或用户中途关掉向导，下次启动就在这里把它说出来，
    /// 而不是让用户对着一个"还是旧版本"的壳猜。
    ///
    /// 报法四态（判据在 <see cref="ReportPendingGuardUpdate"/>，与版本号比较同一份工具）：
    ///   · 已装成 ⇒ 中性一行，不打扰；· 仍是旧版本 ⇒ 明确说"上次没完成，可以再试"；
    ///   · 版本读不出来 ⇒ 只说"没有确认完成"，不编结论；· 没有记账 ⇒ 什么都不说。
    ///
    /// 延后 3 秒再做：启动瞬间的磁盘动作已经很多，这件事不着急，也不该跟启动抢时间。
    /// </summary>
    private void SweepGuardUpdateStagingSoon()
    {
        try
        {
            if (_guardUpdateSweepDone) return;
            _guardUpdateSweepDone = true;

            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            t.Tick += (_, _) =>
            {
                try { t.Stop(); } catch { }
                try
                {
                    var swept = PluginSource.SweepGuardUpdateStaging();
                    ReportPendingGuardUpdate(swept.PendingTarget, swept.Outcome);
                }
                catch (Exception ex) { Logger.LogError("SweepGuardUpdateStaging", ex); }
            };
            t.Start();
        }
        catch (Exception ex) { Logger.LogError("SweepGuardUpdateStagingSoon", ex); }
    }

    /// <summary>
    /// 把"上次那次更新装成了没有"如实报出来。四种情形（<see cref="PluginSource.GuardUpdateOutcome"/>）
    /// 逐条对应，**判据只有一处**（版本号比对，走既有的 <see cref="VersionInfo.Compare"/>）：
    ///
    ///   · <c>None</c>          —— 没有记账：上次没有正在进行的更新 ⇒ 一个字都不说；
    ///   · <c>Completed</c>     —— 当前版本已达到目标：上次装成了（很可能是"装完但没来得及确认"）
    ///                            ⇒ 中性一行，不打扰；
    ///   · <c>NotCompleted</c>  —— 当前仍是旧版本：上次没装成 ⇒ 明确告知 + 可再试一次；
    ///   · <c>Unconfirmed</c>   —— 版本读不成可比形式：只报"没有确认完成"，绝不编结论。
    ///
    /// ⚠ 中止 / 断电发生在"下载到一半"时都不会留下记账（记账是在**校验通过、交接之前**才写的），
    ///   所以那种半截状态不会被误报成"上次更新未完成"；真正会在下次启动被认出来的，
    ///   恰好是"已经交给安装器、但结果未知"这一种 —— 也就是唯一需要用户知道的那一种。
    /// </summary>
    private void ReportPendingGuardUpdate(string pendingTarget, PluginSource.GuardUpdateOutcome outcome)
    {
        try
        {
            switch (outcome)
            {
                case PluginSource.GuardUpdateOutcome.None:
                    return;

                case PluginSource.GuardUpdateOutcome.Completed:
                    Logger.NoteDiagnosis($"上次更新已完成：目标 {pendingTarget}，当前 {GuardVersion.Version}");
                    return;

                case PluginSource.GuardUpdateOutcome.NotCompleted:
                    AddEvent($"上次更新没有完成（仍是 {GuardVersion.Version}），可以再点一次「立即更新」", EventKind.Warn);
                    Logger.NoteDiagnosis($"上次更新未完成：目标 {pendingTarget}，当前仍为 {GuardVersion.Version}"
                                       + "（多为安装向导被中途关闭，或安装过程中断电/强制关机）。"
                                       + "已保留上次下载的安装包与一键恢复脚本，可直接重跑安装修复。");
                    return;

                default:
                    AddEvent("上次更新的结果没有确认完成，可以再点一次「立即更新」", EventKind.Warn);
                    Logger.NoteDiagnosis($"上次更新记账：目标 {pendingTarget}、当前 {GuardVersion.Version}，"
                                       + "两边读不成可比版本号 ⇒ 只报未确认（不编结论）");
                    return;
            }
        }
        catch (Exception ex) { Logger.LogError("ReportPendingGuardUpdate", ex); }
    }

    // ══════════════ 说明页 ══════════════
    private void RenderAbout()
    {
        if (AboutPanel == null) return;
        try
        {
            AboutPanel.Children.Clear();

            Border Sec(string title, string body)
            {
                var b = new Border
                {
                    CornerRadius = new CornerRadius(10),
                    Background = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
                    Padding = new Thickness(14, 12, 14, 12),
                    Margin = new Thickness(0, 0, 0, 10)
                };
                var sp = new StackPanel();
                b.Child = sp;
                sp.Children.Add(SimpleText(title, 13, Color.FromRgb(0x5A, 0xC8, 0xFA), true));
                sp.Children.Add(SimpleText(body, 12, Color.FromRgb(0xC7, 0xC7, 0xCC)));
                return b;
            }

            AboutPanel.Children.Add(Sec("这是什么",
                "DSH 守护壳：一键启动 / 停止 DeepSeek Harness 网页引擎，顺带帮你管日志、快照、插件和版本。\n" +
                "它和引擎互不绑定：关掉守护壳不会停掉引擎；引擎本来就是从别处启动的，它就只看着、显示状态。\n" +
                "所有按钮只管一件事——绿的是往前走，橙的是会变点东西，红的是会删东西。不确定就悬停看看提示。"));

            AboutPanel.Children.Add(Sec("怎么用",
                "① 状态页 → 点「一键启动引擎」，引擎起来后自动打开浏览器。\n" +
                "② 日志页 → 出问题时点「复制日志」，一键把日志内容复制走。\n" +
                "③ 快照页 → 改动前后存一份，随时整组恢复。\n" +
                "④ 插件页 → 「寻找插件」里逛社区插件并一键安装；「本地插件」里更新、关闭或卸载。\n" +
                "⑤ 设置页 → 常规（开关与端口）、路径（各种目录）、版本（升级与回退）。"));

            AboutPanel.Children.Add(Sec("常见问题",
                "· 引擎已经在跑？守护壳不会抢，也不会去关它，只显示「运行中」。\n" +
                "· 日志是空的？正常启动不写文件，只有出问题才留档。\n" +
                "· 装了新插件未生效？重启一次引擎即可。卸载插件前建议先停引擎。"));

            // 说明页的口头禅：与底端文字、最近事件那条共用同一句话与同一个颜色
            var versionBox = new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
                Padding = new Thickness(14, 12, 14, 12),
                Margin = new Thickness(0, 0, 0, 10)
            };
            var versionSp = new StackPanel();
            versionBox.Child = versionSp;
            versionSp.Children.Add(SimpleText("版本", 13, Color.FromRgb(0x5A, 0xC8, 0xFA), true));
            versionSp.Children.Add(SimpleText(GuardVersion.Display, 12, Color.FromRgb(0xC7, 0xC7, 0xCC)));
            var mascotLine = new TextBlock
            {
                Text = Mascot.CurrentLine,
                Foreground = new SolidColorBrush(Mascot.IsEgg ? Mascot.CurrentColor : Color.FromRgb(0xC7, 0xC7, 0xCC)),
                FontSize = Mascot.IsEgg ? Mascot.CurrentFormat.Size : 12,
                FontWeight = Mascot.IsEgg ? Mascot.CurrentFormat.Weight : FontWeights.Normal,
                FontStyle = Mascot.IsEgg && Mascot.CurrentFormat.Italic ? FontStyles.Italic : FontStyles.Normal,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            };
            SetAboutMascotLine(mascotLine);
            versionSp.Children.Add(mascotLine);
            AboutPanel.Children.Add(versionBox);
            ApplyThemeSoon();
        }
        catch (Exception ex) { Logger.LogError("RenderAbout", ex); }
    }

    // ══════════════ 设置页：常规（启动命令）/ 路径 ══════════════
    /// <summary>
    /// 「启动方式」一栏的用户可见口径：正文只说明"这一次按什么方式启动"，
    /// 完整命令行（<c>npx --yes …</c>）收进悬停 —— 界面上不出现命令行是本项目的既定规矩，
    /// 而技术细节本就该放悬停或日志；悬停里照旧给得出完整命令，排查能力不受影响。
    /// （「设置 → 路径」那几行路径输入框是有意保留的例外：那一页本来就是给用户看/改路径的，不走这里。）
    /// </summary>
    private void FillLaunchPreview(string? baseCommand)
    {
        LaunchPreviewText.Text = string.IsNullOrWhiteSpace(baseCommand)
            ? "实际启动：使用当前版本策略自动生成的命令"
            : "实际启动：使用你在上方填写的启动方式";
        LaunchPreviewText.ToolTip = "本次启动将执行：\n" + ProcessManager.BuildArgs(_port, baseCommand);

        LaunchPolicyText.Text = "当前版本策略：" + VersionMemory.PolicyText;
        // 「留空时的自动命令」原先直接拼在正文里（完整命令行），改为悬停给全文
        LaunchPolicyText.ToolTip = "留空时自动生成的命令：\n" + VersionMemory.LaunchCommand;
    }

    private void RefreshSettingsView()
    {
        try
        {
            LaunchCommandBox.Text = _settings.LaunchCommand ?? "";
            FillLaunchPreview(_settings.LaunchCommand);
            RefreshEnvInfo();      // 填充「路径」页
            RefreshCacheInfo();    // 填充「常规」页的缓存占用
            ShowSettingsPage(_settingsTab); // 停在当前二级标签（常规 / 路径 / 版本）
        }
        catch (Exception ex) { Logger.LogError("RefreshSettingsView", ex); }
    }

    // ═══ 设置页二级标签：常规 / 路径 / 版本 ═══
    private void SettingsTab_General(object sender, MouseButtonEventArgs e) => ShowSettingsPage(SettingsTab.General);
    private void SettingsTab_Paths(object sender, MouseButtonEventArgs e) => ShowSettingsPage(SettingsTab.Paths);
    private void SettingsTab_Version(object sender, MouseButtonEventArgs e) => ShowSettingsPage(SettingsTab.Version);

    private void ShowSettingsPage(SettingsTab tab)
    {
        try
        {
            _settingsTab = tab;

            SettingsPageGeneral.Visibility = tab == SettingsTab.General ? Visibility.Visible : Visibility.Collapsed;
            SettingsPagePaths.Visibility = tab == SettingsTab.Paths ? Visibility.Visible : Visibility.Collapsed;
            SettingsPageVersion.Visibility = tab == SettingsTab.Version ? Visibility.Visible : Visibility.Collapsed;

            static void StyleTab(Border b, TextBlock t, bool active)
            {
                b.Background = new SolidColorBrush(active
                    ? Color.FromRgb(0x00, 0x7A, 0xFF) : Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
                t.Foreground = active ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93));
            }

            StyleTab(SettingsTabGeneral, SettingsTabGeneralText, tab == SettingsTab.General);
            StyleTab(SettingsTabPaths, SettingsTabPathsText, tab == SettingsTab.Paths);
            StyleTab(SettingsTabVersion, SettingsTabVersionText, tab == SettingsTab.Version);

            if (tab == SettingsTab.Paths) RefreshEnvInfo();
            if (tab == SettingsTab.Version)
            {
                RenderVersionView();
                _ = RefreshVersionAsync();      // 首次进来顺带查一次最新版
            }
            // 页面刚由折叠变为可见时其内容可能才挂上可视化树，补刷一次主题
            ApplyThemeSoon();
        }
        catch (Exception ex) { Logger.LogError("ShowSettingsPage", ex); }
    }

    /// <summary>版本页「手动固定版本」的版本标签：点击后固定该版本，并弹出确认。</summary>
    private void PinChip_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (sender is not Border b || b.Tag is not string version) return;
            if (version == VersionMemory.Pin && VersionMemory.IsManualPin)
            {
                Logger.Log($"已是手动固定的版本 {version}，无需重复固定");
                return;
            }
            var r = GuardDialog.Show(
                $"手动固定版本设为 {version}？\n\n" +
                "· 之后守护壳启动引擎将使用该版本，不再自动切换\n" +
                "· 自动固定（运行成功即记住）不会覆盖此选择\n" +
                "· 如需变更，点「改回自动管理」或「不固定，跟随最新版」\n\n" +
                "（仅影响守护壳自身启动的引擎；从其他方式启动的引擎不受影响）",
                "手动固定版本", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (r != MessageBoxResult.OK) return;

            VersionMemory.PinTo(version);
            // 与「固定版本」按钮同一档口径：没写进去就不能说"已手动固定"
            var chipOutcome = VersionMemoryOutcome($"已手动固定 DSH 版本 {version}");
            AddEvent(chipOutcome.Text, chipOutcome.Kind);
            // 右侧卡与版本页必须同时刷新：只刷页面会出现"卡片还写着旧版本"的不同步（现场反馈）
            UpdateVersionCard();
            RenderVersionView();
        }
        catch (Exception ex) { Logger.LogError("PinChip_Click", ex); }
    }

    /// <summary>设置 -> 常规：清空 cache 目录，不改动配置。</summary>
    private void ClearCache_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            long size = GuardPaths.CacheBytes();
            if (size <= 0)
            {
                GuardDialog.Show("缓存为空，无需清理。", "清理缓存", MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshCacheInfo();
                return;
            }
            var r = GuardDialog.Show(
                $"确认清理缓存？将删除以下内容（共 {GuardPaths.HumanSize(size)}）：\n\n" +
                "· 插件目录缓存（下次打开「寻找插件」时重新获取）\n" +
                "· 插件截图缓存（下次查看时重新下载）\n\n" +
                "不受影响：设置、版本固定、快照、日志。\n\n" +
                "（缓存位置可在「设置 → 路径」页查看）",
                "清理缓存", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (r != MessageBoxResult.OK) return;

            long freed = GuardPaths.ClearCache();
            ResetImageFailures();
            ResetImageMemoryCache();
            _market = null;                       // 下次进「寻找插件」重新拉目录
            AddEvent($"已清理缓存，释放 {GuardPaths.HumanSize(freed)}");
            RefreshCacheInfo();
            GuardDialog.Show($"清理完成，已释放 {GuardPaths.HumanSize(freed)}。", "清理缓存",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Logger.LogError("ClearCache_Click", ex);
            AddEvent("清理缓存失败，缓存未清理干净；" + LogPromise("详细原因已记入日志，可在「日志」页查看。"), EventKind.Bad);
            GuardDialog.Show("清理缓存出现错误，部分内容可能未能删除。请关闭占用缓存的程序后重试。\n\n"
                + LogPromise("详细原因已记入日志，可在「日志」页查看。"),
                "清理缓存", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>设置 -> 常规：刷新缓存占用显示。</summary>
    private void RefreshCacheInfo()
    {
        try
        {
            if (CacheInfoText == null) return;
            long size = GuardPaths.CacheBytes();
            CacheInfoText.Text = size > 0
                ? $"当前占用 {GuardPaths.HumanSize(size)}"
                : "当前没有缓存";
            CacheInfoText.ToolTip = "缓存位置：" + GuardPaths.CacheDir;
        }
        catch (Exception ex) { Logger.LogError("RefreshCacheInfo", ex); }
    }

    /// <summary>路径行尾「...」：在资源管理器中打开对应位置（目标由 Tag 决定）。</summary>
    private void OpenPath_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (sender is not Border b || b.Tag is not string tag) return;

            string? target = tag switch
            {
                "exe" => AppContext.BaseDirectory,
                "logs" => Logger.OpenLogFolderPath,
                "snap" => SnapshotManager.SnapshotRoot,
                "profile" => PluginManager.ProfileDir,
                "curlog" => string.IsNullOrEmpty(Logger.CurrentLogFile)
                    ? Logger.OpenLogFolderPath : Logger.CurrentLogFile,
                "config" => GuardPaths.ConfigDir,
                "cache" => GuardPaths.CacheDir,
                _ => null
            };
            if (string.IsNullOrEmpty(target)) return;

            bool isFile = File.Exists(target);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = isFile ? $"/select,\"{target}\"" : $"\"{target}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex) { Logger.LogError("OpenPath_Click", ex); }
    }

    private void SaveLaunch_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            _settings.LaunchCommand = SettingsManager.NormalizeLaunch(LaunchCommandBox.Text);
            _settings.Save();
            LaunchCommandBox.Text = _settings.LaunchCommand;
            FillLaunchPreview(_settings.LaunchCommand);
            // 先取结果再报：写盘失败时事件栏与弹窗都改口径，不再说"已保存"（否则下次启动用的是旧命令）
            var launchOutcome = SettingsOutcome("已保存启动命令");
            AddEvent(launchOutcome.Text, launchOutcome.Kind);
            GuardDialog.Show(launchOutcome.Kind == EventKind.Warn
                    ? SettingsFailText
                    : (_settings.LaunchCommand.Length == 0
                        ? "已保存。留空 = 使用版本策略自动生成的命令，下次启动引擎时生效。"
                        : "已保存。下次启动引擎时生效。"),
                "启动命令", MessageBoxButton.OK,
                launchOutcome.Kind == EventKind.Warn ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception ex) { Logger.LogError("SaveLaunch_Click", ex); }
    }

    private void ResetLaunch_Click(object sender, MouseButtonEventArgs e)
    {
        _settings.LaunchCommand = "";
        _settings.Save();
        LaunchCommandBox.Text = "";
        FillLaunchPreview("");
        // 与「保存」同一档口径：没写进去就不能说"已恢复默认"（本处原先没有事件栏文案，成功时照样不加）
        var resetOutcome = SettingsOutcome("已恢复默认（按版本策略自动生成命令）。");
        if (resetOutcome.Kind == EventKind.Warn) AddEvent(resetOutcome.Text, resetOutcome.Kind);
        GuardDialog.Show(resetOutcome.Text, "启动命令", MessageBoxButton.OK,
            resetOutcome.Kind == EventKind.Warn ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    // ══════════════ 路径设置（可直接编辑 / 用「...」选择文件夹） ══════════════
    private void PickPath_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (sender is not Border b || b.Tag is not string tag) return;

            string current = tag switch
            {
                "logs" => PathLogsBox.Text,
                "snap" => PathSnapBox.Text,
                "diag" => PathDiagBox.Text,
                "profile" => PathProfileBox.Text,
                _ => ""
            };

            using var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "选择目录",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true
            };
            if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
                dlg.SelectedPath = current;

            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                ApplyPathChoice(tag, dlg.SelectedPath);
        }
        catch (Exception ex) { Logger.LogError("PickPath_Click", ex); }
    }

    private void PathBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb) SavePathFromBox(tb);
    }

    private void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox tb) SavePathFromBox(tb);
    }

    private void SavePathFromBox(TextBox tb)
    {
        string tag = ReferenceEquals(tb, PathLogsBox) ? "logs"
                   : ReferenceEquals(tb, PathSnapBox) ? "snap"
                   : ReferenceEquals(tb, PathDiagBox) ? "diag"
                   : ReferenceEquals(tb, PathProfileBox) ? "profile" : "";
        if (tag.Length == 0) return;
        ApplyPathChoice(tag, tb.Text ?? "", showMessage: false);
    }

    /// <summary>写入路径设置并立即生效（重新 Apply 到 GuardPaths），随后刷新显示。</summary>
    private void ApplyPathChoice(string tag, string value, bool showMessage = true)
    {
        try
        {
            value = (value ?? "").Trim().Trim('"');

            switch (tag)
            {
                case "logs": _settings.PathLogs = value; break;
                case "snap": _settings.PathSnapshots = value; break;
                case "diag": _settings.PathDiagnostics = value; break;
                case "profile": _settings.PathProfile = value; break;
                default: return;
            }
            _settings.Save();

            GuardPaths.Apply(_settings.PathLogs, _settings.PathSnapshots, _settings.PathProfile);
            RefreshEnvInfo();
            // 先取结果再报：路径没写进设置时，重启后会回到旧路径，界面不能说"已更新"
            // 事件栏只写路径页上的中文标签（原先直接拼内部 tag：logs / snap / diag / profile —— 那是内部标识）
            string pathLabel = tag switch
            {
                "logs" => "记录位置",
                "snap" => "快照位置",
                "diag" => "诊断输出",
                "profile" => "引擎配置",
                _ => "路径"
            };
            var pathOutcome = SettingsOutcome($"路径已更新（{pathLabel}）");
            AddEvent(pathOutcome.Text, pathOutcome.Kind);

            if (showMessage)
                GuardDialog.Show(pathOutcome.Kind == EventKind.Warn
                        ? SettingsFailText
                        : "已保存并立即生效。\n\n（留空 = 使用默认探测位置）", "路径设置",
                    MessageBoxButton.OK,
                    pathOutcome.Kind == EventKind.Warn ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception ex) { Logger.LogError("ApplyPathChoice", ex); }
    }

    // ══════════════ 路径：启动时应用 + 「自动配置」按需探测 ══════════════
    /// <summary>
    /// 启动时只把设置中保存的自定义路径应用到 GuardPaths。
    /// 首次启动不弹窗，路径识别改由「设置 -> 路径」页的「自动配置」按钮按需触发。
    /// </summary>
    private void ApplySavedPaths()
    {
        try
        {
            GuardPaths.Apply(_settings.PathLogs, _settings.PathSnapshots, _settings.PathProfile);
            SilentDependencyHint();
        }
        catch (Exception ex) { Logger.LogError("ApplySavedPaths", ex); }
    }

    /// <summary>
    /// 启动时的静默依赖提示：缺少 node / npx / 配置文件或缓存时，
    /// 只在状态页「最近事件」留一行并指向「设置 -> 路径 -> 自动配置」，不弹出对话框。
    /// </summary>
    private void SilentDependencyHint()
    {
        _ = Task.Run(() =>
        {
            try
            {
                var miss = new List<string>();
                AdoptUserNodeDir();
                // 这里只用于自己人检查 miss 里缺的是哪一类（下面按 node 判定要不要装运行环境），
                // 不进任何界面文案：事件栏用的是下面那句中文，故可继续用内部分类名。
                if (!CommandExists("node")) miss.Add("node");
                if (!CommandExists("npx")) miss.Add("runtime");
                if (!File.Exists(Path.Combine(GuardPaths.ProfileDir, "package.json"))) miss.Add("DSH 配置文件");
                if (VersionInfo.GetCurrentVersion() == "未知") miss.Add("DSH 本体");
                if (miss.Count == 0) return;

                // 去重：node 与 npx 都缺时它们是同一个「运行环境」，不该在事件栏里报两遍
                AddEvent("⚠ 依赖检查未通过（缺：" + string.Join("、", miss.Select(RuntimeCheckLabel).Distinct()) + "）—— 可点「设置 → 路径 → 自动配置」查看详情",
                    EventKind.Bad);
                Dispatcher.Invoke(() => RefreshStatusEvents());

                // 缺的正好是运行环境，则回到 UI 线程问一句、一键装好（开包即用的关键一步）
                if (miss.Contains("node") || miss.Contains("runtime"))
                {
                    Dispatcher.Invoke(async () =>
                    {
                        if (await EnsureNodeAsync(interactive: true))
                            AddEvent("运行环境已就绪", EventKind.Good);
                    });
                }
            }
            catch (Exception ex) { Logger.LogError("SilentDependencyHint", ex); }
        });
    }

    /// <summary>
    /// 把「依赖检查」用的内部分类名翻成用户看得懂的说法（纯函数，便于自检）。
    /// 为什么要一层映射：缺件判断必须按内部分类名比对（<see cref="SilentDependencyHint"/> 里
    /// "缺的是不是运行环境"决定要不要弹一键安装），但事件栏是用户可见的 ——
    /// 直接把这几个名字拼上去就是把实现细节摆到台面上（node / npx 这类工具名用户并不需要）。
    /// </summary>
    internal static string RuntimeCheckLabel(string key) => key switch
    {
        "node" => "运行环境",
        "runtime" => "运行环境",
        _ => key                                   // 「DSH 配置文件」「DSH 本体」等本就是人话，原样用
    };

    /// <summary>「自动配置」按钮：重新探测各目录 + 清掉已失效的自定义路径 + 依赖检查，最后汇报结果。</summary>
    private void AutoConfig_Click(object sender, MouseButtonEventArgs e) => RunAutoConfigure();

    private void RunAutoConfigure()
    {
        try
        {
            var notes = new List<string>();

            // ① 自定义路径失效时恢复为自动（留空即每次按程序位置推算，程序移动后仍然有效）
            void ResetIfStale(string label, string path, Action clear)
            {
                if (string.IsNullOrWhiteSpace(path)) return;          // 本来就是自动
                // 只说结论，不把盘符路径抄进弹窗正文（这一处不是「设置 → 路径」页的编辑框）
                if (Directory.Exists(path)) { notes.Add($"· {label}：沿用你原先设置的目录"); return; }
                clear();
                notes.Add($"· {label}：原设置的目录不存在了，已恢复为自动探测");
            }

            ResetIfStale("日志目录", _settings.PathLogs, () => _settings.PathLogs = "");
            ResetIfStale("快照目录", _settings.PathSnapshots, () => _settings.PathSnapshots = "");
            ResetIfStale("配置文件", _settings.PathProfile, () => _settings.PathProfile = "");
            _settings.Save();

            // ② 重新应用并刷新显示
            GuardPaths.Apply(_settings.PathLogs, _settings.PathSnapshots, _settings.PathProfile);
            RefreshEnvInfo();

            // ③ 依赖检查（改为手动触发）
            var missing = new List<string>();

            if (!CommandExists("node"))
                missing.Add("· 未找到运行环境：点「一键启动引擎」会提示自动安装");
            if (!CommandExists("npx"))
                missing.Add("· 运行环境不完整：缺少运行引擎所需的基础组件，点「一键启动引擎」会重新安装");

            if (!File.Exists(Path.Combine(GuardPaths.ProfileDir, "package.json")))
                missing.Add("· 尚未完成初始化：请先启动一次引擎，本程序会自动完成初始化");

            if (VersionInfo.GetCurrentVersion() == "未知")
                missing.Add("· 尚未下载 DSH 本体\n" +
                            "  首次启动引擎会联网下载，请保持网络可用");

            bool snapMissing = !Directory.Exists(GuardPaths.SnapshotRoot);
            var missingTools = ToolScripts
                .Where(t => !File.Exists(Path.Combine(ToolsDir, t)))
                .ToList();

            var lines = new List<string>
            {
                "已自动识别路径。",
                "这几项默认是「自动」：留空就每次按程序所在位置推算，只有你手填过才会固定成写死的路径。"
            };
            // 快照目录还没有时如实说明这一项的状态（原先拼在遍历出来的绝对路径后面，现改为不出现路径）
            if (snapMissing)
            {
                lines.Add("");
                lines.Add("尚无快照：在「快照」页点「保存当前快照」即可。");
            }

            if (notes.Count > 0) { lines.Add(""); lines.AddRange(notes); }

            if (missing.Count > 0)
            {
                lines.Add("");
                lines.Add("⚠ 缺少以下依赖，引擎可能无法启动：");
                lines.AddRange(missing);
            }
            if (missingTools.Count > 0)
            {
                lines.Add("");
                lines.Add("提示：以下工具脚本缺失（不影响启动，仅影响日志分析/清理/端口检测）：");
                lines.Add("  " + string.Join("、", missingTools));
            }
            if (missing.Count == 0 && missingTools.Count == 0)
            {
                lines.Add("");
                lines.Add("依赖检查通过 ✅");
            }
            lines.Add("");
            lines.Add("以上路径随时可以在下面直接编辑，或点右侧「...」选文件夹。");

            Logger.ShowInfo("自动配置", string.Join(Environment.NewLine, lines));
            // 先取结果再报：这次探测顺带清掉失效路径并写盘，没写进去就该如实说，别让用户以为已经生效
            var autoCfgOutcome = SettingsOutcome("已执行自动配置（路径 + 依赖检查）");
            AddEvent(autoCfgOutcome.Text, autoCfgOutcome.Kind);
        }
        catch (Exception ex) { Logger.LogError("RunAutoConfigure", ex); }
    }

    /// <summary>
    /// 运行环境（Node.js）就绪检查。安装包与程序共用同一份脚本完成"一键安装"。
    /// 返回 true 表示 node / npx 可用，可以启动引擎。
    /// </summary>
    private async Task<bool> EnsureNodeAsync(bool interactive)
    {
        try
        {
            if (NodeReady()) return true;
            AdoptUserNodeDir();
            if (NodeReady()) return true;
            if (!interactive) return false;

            var r = GuardDialog.Show(
                "本机缺少运行环境，DSH 引擎需要它才能启动。\n\n" +
                "· 是否现在自动安装？约 100 MB 下载，安装在用户目录下，无需管理员权限，不改动系统设置\n" +
                "· 安装后点「一键启动引擎」即可使用",
                "安装运行环境", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes)
            {
                AddEvent("未安装运行环境，引擎暂时无法启动", EventKind.Bad);
                return false;
            }

            string script = Path.Combine(ToolsDir, "install-node.ps1");
            if (!File.Exists(script))
            {
                GuardDialog.Show(
                    "安装脚本缺失（Tools\\install-node.ps1）。\n也可手动安装：打开 https://nodejs.org/zh-cn 下载 LTS 版。",
                    "安装运行环境", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            SetProgress("正在下载并安装运行环境...");
            var (ok, output) = await RunCommandAsync("powershell",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"", timeoutMs: 1800000);
            // （执行层：这条已由 RunCommandAsync 走 ArgumentList，路径含空格也不会被拆开。）
            Logger.Log($"安装运行环境 ok={ok}\n{Shorten(output, 1500)}");

            AdoptUserNodeDir();
            if (NodeReady())
            {
                AddEvent("运行环境已装好，可以启动引擎了", EventKind.Good);
                SetProgress("");
                GuardDialog.Show("运行环境安装完成。\n\n点「一键启动引擎」即可开始使用。",
                    "安装运行环境", MessageBoxButton.OK, MessageBoxImage.Information);
                return true;
            }

            SetProgress("");
            // 弹窗不再摆安装脚本的原始输出（含下载地址与落盘路径）：结论 + 手动安装办法 + 一处去处就够；
            //   全文落异常日志（上面那条 Logger.Log 是空实现，真落盘只能用 NoteDiagnosis）。
            Logger.NoteDiagnosis(
                $"自动安装运行环境未成功（脚本退出码0={ok}，本机仍然找不到 node / npx）\n"
                + Logger.TruncateCommandOutput(output));
            GuardDialog.Show(
                "自动安装未成功，可手动安装：\n打开 https://nodejs.org/zh-cn 下载 LTS 版，按提示完成安装即可。\n\n" +
                LogPromise("详细输出已记入日志，可在「日志」页查看。"),
                "安装运行环境", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        catch (Exception ex)
        {
            Logger.LogError("EnsureNodeAsync", ex);
            SetProgress("");
            return false;
        }
    }

    /// <summary>运行环境是否可用（node 与 npx 都在 PATH 里）。</summary>
    private static bool NodeReady() => CommandExists("node") && CommandExists("npx");

    /// <summary>
    /// 把用户目录下的运行环境接入本进程 PATH。
    /// 安装包刚装完时，系统环境变量还没广播给已在运行的进程，这一步保证"装完立刻可用"。
    /// </summary>
    private static void AdoptUserNodeDir()
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "nodejs");
            if (!File.Exists(Path.Combine(dir, "node.exe"))) return;

            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            if (path.Split(';').Any(p => p.TrimEnd('\\').Equals(dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
                return;

            Environment.SetEnvironmentVariable("PATH", dir + ";" + path, EnvironmentVariableTarget.Process);
            Logger.Log($"已把运行环境目录接入 PATH：{dir}");
        }
        catch (Exception ex) { Logger.LogError("AdoptUserNodeDir", ex); }
    }

    /// <summary>检查命令是否在 PATH 中（直接启动 where.exe，不再经 cmd /c —— 见 ProcessManager.FindOnPath）。</summary>
    private static bool CommandExists(string name)
    {
        try
        {
            // where.exe 是真 exe；name 只作为单个 token（旧写法 cmd /c where {name} 会被 cmd 解释）
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\where.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            psi.ArgumentList.Add(name);
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return false;
            p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(5000)) { try { p.Kill(true); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    // ══════════════ 缺 Git 闸门（git 源插件的安装前提） ══════════════
    //
    // 背景（本机实测）：npm / pnpm 装 git 源插件时要调起系统的 git，本机没装 git 时它们只抛一句
    //   英文 `spawn git`（`npm view git+https://github.com/octocat/Hello-World.git version`
    //   ⇒ npm error code ENOENT / npm error syscall spawn git）—— 用户看不出缺什么、也不知道下一步做什么。
    //   同类设施本项目早就有：node / npx 缺了有 CommandExists + SilentDependencyHint + EnsureNodeAsync，
    //   唯独 git 一直没接进来，故在此补一道"检测 + 如实告知"。
    //
    // 本闸门只做两件事：拦住（不执行任何命令）+ 用中文说清缺什么、怎么办。
    //   · 不做"一键装 git"（那是另一个功能；本程序不代为安装 git，与 README 的口径一致）；
    //   · 不改任何既有判定：来源白名单（IsValidGitSource）、参数构造（BuildAddSourceArgs /
    //     BuildUpdateArgs 的空串契约）、更新分流一律不动 —— 本闸门只是调用方"已经确定要执行某条命令"
    //     之后，多问一句"这条命令调得动 git 吗"，答案为"调不动"时不让那条命令出去。

    /// <summary>
    /// 本机 PATH 里有没有 git（判据二；只扫 PATH，绝不去猜固定安装位置）。
    ///
    /// 为什么必须只扫 PATH：npm / pnpm 就是**在 PATH 里找 git** —— 用户明明装了 git、只是没进 PATH 时，
    ///   npm 一样失败（`spawn git`）。若这里改去猜 Git 的默认安装目录，就会出现"本壳说没问题、
    ///   npm 照样报错"的分裂。故本方法只回答"PATH 里找不找得到"，口径与 npm 看到的一致。
    ///
    /// ★ 方向：宁可漏拦，不可误拦。只有"确证找不到"才返回 false；任何异常一律返回 true（按有 git 处理）
    ///   —— 误判成"缺 git"会把本来能装的用户挡在门外，比现在那句 `spawn git` 更糟。
    ///   故本方法的实现必须 fail-open：探测过程出任何岔子，都不许变成"缺 git"。
    /// </summary>
    internal static bool GitOnPath()
    {
        try
        {
            // ① 先用既有解析器（ProcessManager.FindOnPath：自己按 PATHEXT 解析，语义即"where 的同款结果"）。
            //    刻意不复用本类的 CommandExists：它末尾是 `catch { return false; }`，
            //    "探测出岔子"与"确实没有"会落进同一个 false —— 那正是本方法绝不能有的方向（见上面 ★）。
            if (ProcessManager.FindOnPath("git") != null) return true;

            // ② 再自扫一遍 PATH 兜底：FindOnPath 不剥 PATH 条目上的引号（`"C:\Program Files\Git\cmd"`
            //    是常见写法，where 与 npm 都认），只信 ① 会把这类用户误判成"缺 git"——正是要避免的误拦。
            return GitSweepPath();
        }
        catch (Exception ex)
        {
            Logger.LogError("GitOnPath", ex);
            return true;                       // 探测出异常 ⇒ 按"有 git"处理，放行（宁可漏拦）
        }
    }

    /// <summary>
    /// 自扫 PATH 找 git（<see cref="GitOnPath"/> 的兜底；只读、不起进程、不猜固定安装位置）。
    /// 多认几个文件名只会让"有 git"更容易成立 —— 方向仍是宁可漏拦。
    /// </summary>
    private static bool GitSweepPath()
    {
        try
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string raw in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                // PATH 条目带引号是常见写法（用户手写、安装器写入都有）：去掉引号再拼，装了才找得到
                string dir = raw.Trim().Trim('"');
                if (dir.Length == 0) continue;
                try { dir = Environment.ExpandEnvironmentVariables(dir); } catch { }
                foreach (string exe in GitExeNames)
                    if (File.Exists(Path.Combine(dir, exe))) return true;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("GitSweepPath", ex);
            return true;                       // 同上：出岔子一律按"有 git"处理
        }
        return false;
    }

    /// <summary>PATH 里可能的 git 可执行文件名（真 exe，或包装脚本 —— 后者 npm 也起得动）。</summary>
    private static readonly string[] GitExeNames = { "git.exe", "git.cmd", "git.bat" };

    /// <summary>缺 Git 时用户可见的核心事实（唯一文案实现处，便于自检断言措辞）。</summary>
    internal const string GitMissingNote = "本机没有找到 Git，无法从代码仓库安装或更新插件";

    /// <summary>缺 Git 时用户可见的下一步（唯一文案实现处）：只说"装什么、然后怎么办"，不给命令行与网址。</summary>
    internal const string GitMissingNextStep = "请先在本机安装 Git，装好后再重试";

    /// <summary>
    /// 缺 Git 时弹窗里的正文（纯函数，唯一实现处）。
    /// 三句各带一件事：这次没做什么（未执行任何命令）+ 缺什么（点名 Git）+ 怎么办（装好再重试）。
    /// 措辞纪律与全项目一致：中文、不出现命令行、不出现网址与站点专名、不摆内部标识。
    /// </summary>
    internal static string GitMissingDialogText(string pluginName)
        => $"无法为「{pluginName}」执行本次操作：{GitMissingNote}。\n\n{GitMissingNextStep}。本次未执行任何命令。";

    /// <summary>缺 Git 时列在"未成功"清单里的那一项（纯函数，唯一实现处；批量路径用）。</summary>
    internal static string GitMissingItemText(string pluginName)
        => $"{pluginName}（{GitMissingNote}）";

    /// <summary>缺 Git 时事件栏那句（纯函数，唯一实现处；按清单整份安装的自愈路径用，故多点名"装什么"）。</summary>
    internal static string GitMissingEventText(string packageNames)
        => $"{GitMissingNote}：{packageNames} 需要从代码仓库取回，本次未执行安装命令。{GitMissingNextStep}";

    /// <summary>
    /// 缺 Git 闸门的**纯判据**（便于自检直接断言：不碰 PATH、不落盘、不弹窗）：
    ///   "这条来源要不要 git" 与 "本机有没有 git" 两个已知事实的合成结果。
    /// 真正会拦住的那种调用（要落盘、要选用户可见通道）见 <see cref="BlockedForMissingGit"/>，
    ///   它走的也是本方法 —— 判据只有这一份，自检钉住它就等于钉住了线上行为。
    /// </summary>
    internal static bool BlocksForMissingGit(string? spec, bool gitOnPath)
        => PluginManager.NeedsGitFor(spec) && !gitOnPath;

    /// <summary>
    /// 缺 Git 闸门（唯一入口）：这条来源要调系统的 git，而本机 PATH 里确实没有 git
    /// ⇒ 返回 true 表示**已拦截**，调用方必须立刻 return / continue，不得再执行任何命令。
    ///
    /// 三道"放行"（逐条对着"宁可漏拦，不可误拦"）：
    ///   ① 不是 git 源（<see cref="PluginManager.NeedsGitFor"/> 为 false）⇒ 放行：npm 源一个字节都不受影响；
    ///   ② <see cref="GitOnPath"/> 为 true（有 git，或探测过程不确定）⇒ 放行：只有确证没有 git 才拦；
    ///   ③ 探测出任何异常 ⇒ <see cref="GitOnPath"/> 内部已按 true 兜底 ⇒ 同样放行。
    ///
    /// 拦住时把原文落盘（<see cref="Logger.NoteDiagnosis"/> 真写文件；Logger.Log 是空实现，不用）：
    ///   含「哪个插件 + 什么来源 + 缺 git」三件事，用户把日志发给作者时一眼看得清。
    /// 落盘之外只返回判据 —— 用户可见文案由调用方按各自的通道给（弹窗 / 事件栏 / 未成功清单），
    ///   文案本身仍是上面那几个唯一实现处，不在调用点重写。
    /// </summary>
    private static bool BlockedForMissingGit(string pluginName, string? spec, string what)
    {
        // 先单独问"要不要 git"这一半：不是 git 源就绝不探 PATH —— npm 源是高频路径
        //（一键更新 / 批量更新是逐个过闸的循环），不该为它们多扫一遍 PATH。
        if (!PluginManager.NeedsGitFor(spec)) return false;        // ① 不是 git 源：永不拦
        if (!BlocksForMissingGit(spec, GitOnPath())) return false; // ②③ 有 git（或探测不确定）：放行
        Logger.NoteDiagnosis($"{what} {pluginName}：来源「{spec}」需要系统的 git，但本机 PATH 里没有 git ⇒ 未执行命令");
        return true;
    }

    // ══════════════ 小工具 ══════════════
    private static TextBlock SimpleText(string text, double size, Color color, bool bold = false) => new()
    {
        Text = text,
        FontSize = size,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 3, 0, 3),
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = new SolidColorBrush(color)
    };

    private static string Shorten(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Trim();
        return s.Length > max ? s.Substring(0, max) + "…" : s;
    }

    /// <summary>
    /// 命令输出专用的截断：保头也保尾。pnpm / dsh 的报错行几乎都压在输出末尾，
    /// 只留开头会把真错因切掉（现场"错误输出里看不到原因"就是这么来的）。
    /// </summary>
    private static string ShortenForError(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Trim();
        if (s.Length <= max) return s;
        int half = Math.Max(1, max / 2);
        return s.Substring(0, half) + $"\n…（中间省略 {s.Length - half * 2} 字）…\n" + s.Substring(s.Length - half);
    }

    /// <summary>
    /// 把插件自带的长说明压缩为一句概述：去掉围栏代码块、行内反引号、图片与链接语法，
    /// 只取第一句或截断到 max 个字符；全文仍放在 ToolTip 中。
    /// </summary>
    private static string PlainDesc(string? raw, int max = 100)
    {
        string s = raw ?? "";
        s = System.Text.RegularExpressions.Regex.Replace(s, @"```[\s\S]*?```", " ");
        s = s.Replace("`", "");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"!\[[^\]]*\]\([^)]*\)", " ");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\[([^\]]*)\]\([^)]*\)", "$1");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
        if (s.Length == 0) return "";

        // 英文说明按词边界截断，避免截断单词
        bool ascii = s.Count(c => c > 0x2E80) < s.Length / 4;
        int cut = s.IndexOf('。');
        if (cut >= 10 && cut + 1 <= max) return s.Substring(0, cut + 1);
        // 中文说明常为「一句话概括：后续细节」，只保留概括部分
        int colon = s.IndexOf('：');
        if (colon >= 6 && colon <= 60 && s.Length > colon + 30) return s.Substring(0, colon + 1);
        if (s.Length > max)
        {
            string head = ascii ? s.Substring(0, max) : s.Substring(0, max);
            if (ascii)
            {
                int space = head.LastIndexOf(' ');
                if (space > 40) head = head.Substring(0, space);
            }
            return head.TrimEnd('，', '、', '；', ' ', ',', '.') + "…";
        }
        return s;
    }

    /// <summary>按钮或文字的提示文本（技术细节放在 ToolTip 中）。</summary>
    private static TextBlock Hint(string text, Color color) => new()
    {
        Text = text,
        FontSize = 10.5,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 4, 0, 0),
        Foreground = new SolidColorBrush(color)
    };
}

/// <summary>
/// 「正在更新」进度窗：进度窗 + 一根**细**绿条 + 一行百分比 + 一颗「取消下载」。
///
/// ══ 与「正在回滚插件」那个进度窗是什么关系 ══
/// 同一套**伪模态**做法（照抄的是思路，不是类型）：<c>Owner</c> = 主窗、主窗 <c>IsEnabled = false</c>、
/// 关闭时按原值还原、关窗判据只有一处 —— 用户要的正是"像回滚涉及插件时那样锁住整个程序"。
/// 不直接复用 <c>RollbackProgressWindow</c> 的原因有两条，都很实：
///   ① 它那根绿条是**不定量**的流动滑块（"不知道还要多久"的语义），而更新有**确切分母**
///      （68 MB 的字节数）—— 拿流动条去表达一个算得出来的百分比，等于把真进度降级成猜；
///   ② 本单只许改两个文件，改不了它所在的 <c>MainWindow.RollbackProgress.cs</c>。
///
/// ══ 与「一键启动」那条绿条的关系 ══
/// 那条（<c>MainWindow.xaml</c> 的 <c>LoadingFill</c>）高 48、铺满整个按钮，是**按钮内嵌进度**。
/// 这里要的是一条**细**的进度条，所以只借它的颜色与"按百分比填充宽度"的做法，
/// 高度取 4px（回滚进度窗的轨道是 6px，这里比它更细）。
///
/// ══ 为什么给一颗「取消下载」 ══
/// 68 MB 在慢网上要几分钟。没有取消出口的"不许操作"会变成"用户被扣在窗口里" ——
/// 本项目对"无法关闭的窗口"有过教训。取消只中断**下载**，下载器会把半截文件删掉（不留残留）。
/// 「退出并安装」那一步另有确认框，且只有用户点了确认才会走到。
/// </summary>
internal sealed class GuardUpdateProgressWindow : Window
{
    /// <summary>卡片内容宽度（进度条轨道与它同宽）。</summary>
    private const double CardWidth = 340;

    /// <summary>
    /// 进度条高度（**细**）：回滚进度窗的轨道是 6px，这里取 4px。
    /// 用户明确要求"绿条不要太粗"，故比既有那条更细一档。
    /// </summary>
    private const double TrackHeight = 4;

    /// <summary>绿色：与全壳「成功 / 可用」同一个绿（<c>#34C759</c>），日夜两套主题下都不变。</summary>
    private static readonly Color Green = Color.FromRgb(0x34, 0xC7, 0x59);

    // ── 本窗两颗按钮的四个底色（**全用项目既有色值，不新造颜色**）──
    //    为什么是实心色而不是原来的半透明：原先那颗按钮的底是 0x18 的淡白，在深色卡片上几乎与卡同色，
    //    用户反馈"看不出是按钮"。改成实心后，两颗按钮成为这个窗里唯一的高饱和元素，一眼可辨。
    /// <summary>「取消下载」常态：橙黄 <c>#FF9F0A</c>（警示/可中断）。</summary>
    private static readonly Color CancelIdleColor = Color.FromRgb(0xFF, 0x9F, 0x0A);

    /// <summary>「取消下载」悬停：红 <c>#FF3B30</c>（把"点了就中断"这层后果提前用颜色说出来）。</summary>
    private static readonly Color CancelHoverColor = Color.FromRgb(0xFF, 0x3B, 0x30);

    /// <summary>「发布页面」常态：蓝 <c>#0A84FF</c>（与全壳链接/主操作同一个蓝）。</summary>
    private static readonly Color ReleasesIdleColor = Color.FromRgb(0x0A, 0x84, 0xFF);

    /// <summary>「发布页面」悬停：绿 <c>#34C759</c>（与 <see cref="Green"/> 同值 —— 去下载 = 往"能好"的方向走）。</summary>
    private static readonly Color ReleasesHoverColor = Color.FromRgb(0x34, 0xC7, 0x59);

    /// <summary>已显示的百分比（只前进不后退；见 <see cref="SetStage"/>）。</summary>
    private double _shown;

    private readonly TextBlock _stepText;
    private readonly TextBlock _pctText;
    private readonly Border _fill;
    private readonly Border _track;
    private readonly Button _cancelBtn;
    private readonly Button _releasesBtn;

    /// <summary>只有流程自己结束才置真（关窗的唯一放行条件）。</summary>
    private bool _allowClose;

    /// <summary>「用户想关但被忽略」只记一次日志，免得狂按 Esc 刷屏。</summary>
    private bool _closeAttemptLogged;

    /// <summary>解析出来的取消处理器（窗口自己不持有流程，交给 MainWindow 挂）。</summary>
    internal event Action? CancelRequested;

    /// <summary>
    /// 「发布页面」被点时的处理器（同样交给 MainWindow 挂）。
    /// <para>
    /// **为什么不在这里直接开网页**：真正开链接的闸门 <c>OpenExternalLink</c> 是 <c>MainWindow</c> 上的
    /// <c>private static</c>，本类是命名空间下的另一个类、在 MainWindow 之外，够不着它；
    /// 而且"链接能不能开"的白名单判断与诊断留痕本来就归 MainWindow 管。
    /// 所以沿用本窗既有的做法（照 <see cref="CancelRequested"/> 的样子）：窗口只**转发意图**，
    /// 由 MainWindow 在挂接处调用 <c>OpenExternalLink(PluginSource.GuardReleasesPageUrl(), …)</c>。
    /// </para>
    /// </summary>
    internal event Action? ReleasesRequested;

    /// <summary>
    /// 给一颗按钮挂"底色随悬停过渡"的动效：常态 <paramref name="idle"/> ⇄ 悬停 <paramref name="hover"/>。
    /// <para>
    /// **为什么每颗按钮各自 new 一个 <see cref="SolidColorBrush"/>**：能动画的前提是笔刷**没有被冻结**。
    /// 静态刷、以及 XAML/资源里取出来的刷子常常已是 <c>IsFrozen = true</c>，对它 <c>BeginAnimation</c> 会直接抛
    /// <c>InvalidOperationException</c>；共用同一个刷子还会让两颗按钮的动效互相踩。所以这里**只认调用方自己 new 出来的刷子**。
    /// </para>
    /// <para>
    /// **为什么用 <see cref="FillBehavior.Stop"/> + 显式把终值写回 <c>brush.Color</c>（本实现选的就是这一种）**：
    /// <c>FillBehavior.HoldEnd</c> 会把动画时钟**永久挂在**刷子上 —— 鼠标来回蹭时旧动画一直压着，后一段动画要不就是被
    /// 快照掉、要不就是和旧值打架，很容易停在半路的颜色上，而且这个窗口是要被反复开关、丢弃的。
    /// 改成 <c>Stop</c> 之后动画只是一层**临时覆盖**：跑完（或被打断）都自动退回底色，底色本身就等于终值，
    /// 于是"移入移出被打断也一定停在正确的终值"这条是**靠数据保证**的，不依赖动画时序。
    /// </para>
    /// </summary>
    private static void AttachHoverFill(Button b, Color idle, Color hover)
    {
        if (b.Background is not SolidColorBrush brush) return;   // 兜底：只认调用方自建的实心刷

        brush.Color = idle;
        b.MouseEnter += (_, _) => Transition(brush, hover, clearAfter: false);
        b.MouseLeave += (_, _) => Transition(brush, idle, clearAfter: true);

        void Transition(SolidColorBrush target, Color to, bool clearAfter)
        {
            try
            {
                // 140ms：够看出是一段"过渡"而不是硬切，又短到快速划过时不觉得拖沓
                target.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation
                {
                    To = to,
                    Duration = new Duration(TimeSpan.FromMilliseconds(140)),
                    FillBehavior = FillBehavior.Stop
                });
                // 底色 = 终值。动画在跑时它被动画值盖着（所以看得见过渡），动画一停就露出它 ⇒ 终值恒正确。
                target.Color = to;

                // 不做交互的这一窗要能被安全丢弃：鼠标移出的那一段跑完后把动画时钟摘掉、并放掉底色这个局部值，
                // 让刷子回到"干净"状态（FillBehavior.Stop 下没有卡住的终值可留，摘掉即等价）。
                // 移入那一段**故意不摘**：一摘就把刚开始的过渡掐掉了。
                if (!clearAfter) return;
                target.BeginAnimation(SolidColorBrush.ColorProperty, null);
                target.ClearValue(SolidColorBrush.ColorProperty);
            }
            catch (Exception ex) { Logger.LogError("GuardUpdateProgressWindow.HoverFill", ex); }
        }
    }

    internal GuardUpdateProgressWindow()
    {
        bool dark = ThemeManager.IsDark;
        Color cardColor = dark ? Color.FromRgb(0x1C, 0x20, 0x29) : Color.FromRgb(0xF2, 0xF3, 0xF7);
        Color textColor = dark ? Color.FromRgb(0xF5, 0xF5, 0xF7) : Color.FromRgb(0x1C, 0x1C, 0x1E);
        Color subColor = dark ? Color.FromRgb(0xC7, 0xC7, 0xCC) : Color.FromRgb(0x4A, 0x4A, 0x4C);
        Color hintColor = dark ? Color.FromRgb(0x8E, 0x8E, 0x93) : Color.FromRgb(0x6B, 0x6B, 0x70);
        Color borderColor = dark ? Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x26, 0x00, 0x00, 0x00);
        Color trackColor = Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF);

        Title = "正在更新";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        var content = new StackPanel { Margin = new Thickness(22, 20, 22, 20), Width = CardWidth };

        // ── 标题行 ──
        var titleRow = new Grid();
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.Children.Add(new TextBlock
        {
            Text = "\uE895",                     // Segoe MDL2 Assets：下载箭头
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 18,
            Foreground = new SolidColorBrush(Green),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        });
        var titleText = new TextBlock
        {
            Text = "正在更新守护壳",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(textColor),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetColumn(titleText, 1);
        titleRow.Children.Add(titleText);

        // 百分比单独摆在右上角：用户要求"写百分比就行"，放在最显眼的位置一眼能读到
        _pctText = new TextBlock
        {
            Text = "0%",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Green),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };
        Grid.SetColumn(_pctText, 2);
        titleRow.Children.Add(_pctText);
        content.Children.Add(titleRow);

        // ── 一行说明「现在在做什么」 ──
        _stepText = new TextBlock
        {
            Text = "正在准备…",
            FontSize = 12.5,
            LineHeight = 19,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(subColor),
            Margin = new Thickness(0, 12, 0, 0)
        };
        content.Children.Add(_stepText);

        // ── 细绿条：槽 + 按百分比撑宽的填充（宽度在 SetStage 里算） ──
        _fill = new Border
        {
            Width = 0,
            Height = TrackHeight,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(Green),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var trackInner = new Grid { Width = CardWidth, Height = TrackHeight };
        trackInner.Clip = new RectangleGeometry(new Rect(0, 0, CardWidth, TrackHeight), 2, 2);
        trackInner.Children.Add(_fill);
        _track = new Border
        {
            Child = trackInner,
            Height = TrackHeight,
            Width = CardWidth,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(trackColor),
            Margin = new Thickness(0, 14, 0, 0)
        };
        content.Children.Add(_track);

        content.Children.Add(new TextBlock
        {
            Text = "更新期间请勿操作，本程序会自动完成下载与核对。",
            FontSize = 11,
            LineHeight = 17,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(hintColor),
            Margin = new Thickness(0, 12, 0, 0)
        });

        // ── 两颗按钮：取消下载（左，原有主操作）+ 发布页面（右，网络长期不佳时的直通车） ──
        //    位置与间距的**为什么**：取消是原有主操作、用户已有肌肉记忆，所以留在左边；
        //    间距只给右边那颗留 14px 左外边距 —— 卡片宽 340，12~16px 是"居中但不显得挤"的那一档
        //    （用户原话："两个按钮水平居中，但不要过度紧凑"）。
        //    原来挂在 _cancelBtn 上的 Margin.Top = 12 一并挪到下面那个容器上，两颗按钮因此顶在同一条水平线。
        _cancelBtn = new Button
        {
            Content = "取消下载",
            FontSize = 11.5,
            // 实心橙黄底 + 白字：原来那层几乎看不见的淡白底，正是"看不出这里有一颗按钮"的根因
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(11, 5, 11, 5),
            MinHeight = 26,
            Cursor = Cursors.Hand,
            // 去掉原来写死的 HorizontalAlignment.Left：水平位置改由**容器**统一决定，两颗才会成组居中
            Margin = new Thickness(0)
        };
        RoundBtn.Apply(_cancelBtn);
        AttachHoverFill(_cancelBtn, CancelIdleColor, CancelHoverColor);      // 常态橙黄 ⇄ 悬停红
        _cancelBtn.Click += (_, _) =>
        {
            try
            {
                _cancelBtn.IsEnabled = false;
                _cancelBtn.Content = "正在取消…";
                CancelRequested?.Invoke();
            }
            catch (Exception ex) { Logger.LogError("GuardUpdateProgressWindow.Cancel", ex); }
        };

        _releasesBtn = new Button
        {
            Content = "发布页面",
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(11, 5, 11, 5),
            MinHeight = 26,
            Cursor = Cursors.Hand,
            Margin = new Thickness(14, 0, 0, 0)     // 只留左边距：与左边那颗拉开 14px（见上面"间距"那条注释）
        };
        RoundBtn.Apply(_releasesBtn);
        AttachHoverFill(_releasesBtn, ReleasesIdleColor, ReleasesHoverColor);  // 常态蓝 ⇄ 悬停绿
        _releasesBtn.Click += (_, _) =>
        {
            try
            {
                // **不关**这个进度窗：用户可能只想自己先去下载，同时还想让自动那条继续跑着等
                ReleasesRequested?.Invoke();
            }
            catch (Exception ex) { Logger.LogError("GuardUpdateProgressWindow.Releases", ex); }
        };

        // 居中的**唯一**决定点：水平 StackPanel + HorizontalAlignment.Center。
        // 两颗按钮自己都不再设 HorizontalAlignment —— 各自设会变成各自定位，整组就散了。
        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 0)     // 原 _cancelBtn 的上边距挪到这里，两颗共用
        };
        btnRow.Children.Add(_cancelBtn);
        btnRow.Children.Add(_releasesBtn);
        content.Children.Add(btnRow);

        var card = new Border
        {
            Child = content,
            Background = new SolidColorBrush(cardColor),
            BorderBrush = new SolidColorBrush(borderColor),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Margin = new Thickness(14),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.45, Color = Colors.Black }
        };

        var root = new Grid { Background = Brushes.Transparent };
        root.Children.Add(card);
        card.MouseLeftButtonDown += (_, e) => { try { DragMove(); e.Handled = true; } catch { } };
        Content = root;

        // ── 关窗：与回滚进度窗同一套纪律——只有流程自己走完才放行 ──
        //    （用户仍能用窗口里那颗「取消下载」表达"不想下"，那是另一条路，不是关窗）
        PreviewKeyDown += (_, e) =>
        {
            bool escape = e.Key == Key.Escape;
            bool altF4 = e.Key == Key.System && e.SystemKey == Key.F4;
            if (!escape && !altF4) return;
            e.Handled = true;
            NoteCloseAttempt(escape ? "Esc" : "Alt+F4");
        };
        Closing += (_, e) =>
        {
            // 应用正在退出时一律放行：再拦就会造成"无法关闭的窗口"（本程序反复遭遇的问题）
            bool appExiting = Application.Current == null
                              || Application.Current.Dispatcher.HasShutdownStarted
                              || (Owner is { IsVisible: false });
            if (appExiting) return;
            if (_allowClose) return;
            e.Cancel = true;
            NoteCloseAttempt("系统关闭");
        };
    }

    /// <summary>
    /// 推进到某一档：文案 + 百分比 + 绿条宽度一起更新（只在 UI 线程调用）。
    /// 百分比**只前进不后退**（与「一键启动」那条同一个纪律：倒着走的进度条更让人怀疑是不是坏了）。
    /// </summary>
    internal void SetStage(double percent, string stepText)
    {
        try
        {
            _stepText.Text = stepText ?? "";
            if (percent > _shown) _shown = percent;
            if (_shown < 0) _shown = 0;
            if (_shown > 100) _shown = 100;

            _pctText.Text = $"{_shown:0}%";
            double w = _track.Width > 0 ? _track.Width : CardWidth;
            _fill.Width = Math.Max(0, w * _shown / 100.0);
        }
        catch (Exception ex) { Logger.LogError("GuardUpdateProgressWindow.SetStage", ex); }
    }

    /// <summary>流程自己走完了：**唯一**允许关窗的入口（用户点不出来，代码才调得到）。</summary>
    internal void FinishAndClose()
    {
        _allowClose = true;
        try { Close(); } catch (Exception ex) { Logger.LogError("GuardUpdateProgressWindow.FinishAndClose", ex); }
    }

    /// <summary>用户尝试关闭 → 忽略并留一条证据（只记一次，狂按 Esc 不刷屏）。</summary>
    private void NoteCloseAttempt(string how)
    {
        if (_closeAttemptLogged) return;
        _closeAttemptLogged = true;
        // Logger.Log 是空实现（写入不生效），留证必须走 NoteDiagnosis
        Logger.NoteDiagnosis($"更新进度窗：收到用户的关闭请求（{how}），已忽略 —— 过程结束时会由代码关掉；"
                           + "想中止请用窗口里的「取消下载」");
    }
}
