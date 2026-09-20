using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DSHGuard;

/// <summary>单个 DSH 版本的运行履历：启动次数、异常次数、最近出现时间。</summary>
public sealed class VersionRecord
{
    public string Version { get; set; } = "";
    public string FirstSeen { get; set; } = "";
    public string LastSeen { get; set; } = "";
    /// <summary>成功就绪（端口已监听）的次数。</summary>
    public int Launches { get; set; }
    /// <summary>启动异常次数（启动失败 / 就绪超时 / 起来后立刻退出）。</summary>
    public int Errors { get; set; }
    /// <summary>累计运行秒数。</summary>
    public int RunsSeconds { get; set; }
    /// <summary>被判定为问题版本（回退时自动打标）。</summary>
    public bool MarkedBad { get; set; }
    public string Note { get; set; } = "";
}

/// <summary>版本记忆的持久化状态。</summary>
public sealed class VersionState
{
    /// <summary>固定版本（空 = 跟随最新版）。</summary>
    public string Pin { get; set; } = "";
    /// <summary>
    /// 固定来源：<c>"manual"</c> = 用户手动固定，自动逻辑不得修改；
    /// 其他值或空 = 自动固定（首次运行成功时记录），允许被升级、回退或手动操作覆盖。
    /// </summary>
    public string PinSource { get; set; } = "";
    /// <summary>上一个固定/长期运行的版本（回退首选）。</summary>
    public string PreviousPin { get; set; } = "";
    /// <summary>更新模式：下一次启动用 @latest 拉新版本，跑通后自动固定到新版本。</summary>
    public bool PendingUpdate { get; set; }
    /// <summary>进入更新模式前的版本（回退候选兜底）。</summary>
    public string PendingFrom { get; set; } = "";
    /// <summary>连续启动异常次数（跑通一次即清零）。</summary>
    public int ConsecutiveErrors { get; set; }
    /// <summary>连续异常归属的版本。</summary>
    public string ErrorVersion { get; set; } = "";
    public string LastErrorReason { get; set; } = "";
    /// <summary>已经就该版本弹过回退提示（同一版本不反复打扰）。</summary>
    public string LastPromptedVersion { get; set; } = "";
    /// <summary>上一次升级的来源版本，作为回退首选。</summary>
    public string UpdatedFrom { get; set; } = "";
    /// <summary>上一次升级完成的时间，格式 yyyy-MM-dd HH:mm。</summary>
    public string UpdatedAt { get; set; } = "";
    /// <summary>上一次升级的目标版本。</summary>
    public string UpdateTarget { get; set; } = "";
    /// <summary>升级时为避开不兼容而禁用的插件名（回退时提示恢复）。</summary>
    public List<string> DisabledForUpdate { get; set; } = new();
    public List<VersionRecord> History { get; set; } = new();
}

/// <summary>
/// DSH 版本记忆：启动不再固定使用 `@latest`。
/// 
/// 规则：
/// ① 引擎就绪（端口监听）即记录该版本；未固定版本时同时固定该版本，后续启动不再每次联网拉取最新版；
/// ② 用户点「检查更新」→ 进入更新模式：下一次启动用 @latest 拉取新版本，就绪后自动固定到新版本，
///    旧版本记为「上一长期版本」作为回退候选；
/// ③ 同一版本连续多次启动异常（启动失败 / 就绪超时 / 启动后立即退出）→ 提示回退到上一长期版本；
/// ④ 回退即把固定版本改回旧版本，异常版本打标 MarkedBad，可再次改回。
/// 
/// 存储：%APPDATA%\DSHGuard\versions.json（与 settings.json 同目录，可随时删除重建）。
/// </summary>
public static class VersionMemory
{
    private static readonly object Gate = new();
    private static VersionState? _state;
    private static bool _loaded;

    /// <summary>
    /// **本次状态不可信**：读盘失败（或无权限/文件损坏/反序列化不出版本状态）时置位。
    ///
    /// 为什么必须有它（本单缺陷①的核心）：读失败时 <see cref="Read"/> 只能返回一份**空**的
    /// <see cref="VersionState"/>，而调用方（NoteEngineReady / PinTo / AddRunSeconds…）会照常改它再
    /// <see cref="Save"/> ⇒ 这份空状态被写回盘上，用户的 Pin / PreviousPin / History 被**永久清空**。
    /// 置位之后 <see cref="Save"/> 一律拒绝写入 —— 宁可这一次不落盘，也绝不许清空用户数据。
    /// 只有 <c>DSHGUARD_DATA_DIR</c> 切换或进程重启（重新读一次）才会改变它。
    /// </summary>
    private static bool _stateUnreliable;

    /// <summary>读失败的原因（供界面/排查看；不可信时为非空）。</summary>
    private static string _stateUnreliableReason = "";

    /// <summary>已经就"拒绝写入"记过一次日志（避免 30 秒计时器把日志刷爆）。</summary>
    private static bool _refusalLogged;

    /// <summary>最近一次写入是否失败（只读；上层据此知道"界面说固定了，其实没落盘"）。</summary>
    private static bool _lastSaveFailed;

    /// <summary>最近一次写入失败的原因（没失败时为空串）。</summary>
    private static string _lastSaveFailureReason = "";

    /// <summary>
    /// 本次加载的版本记忆是否**不可信**（读盘失败 ⇒ 现在是空的/不是盘上真实内容）。
    /// 为 true 时 <see cref="Save"/> 拒绝写入，用户的固定版本与履历不会被清空。
    /// </summary>
    public static bool StateUnreliable => _stateUnreliable;

    /// <summary>不可信的原因（可信时为空串）。</summary>
    public static string StateUnreliableReason => _stateUnreliableReason;

    /// <summary>最近一次 <see cref="Save"/> 是否失败（写失败 / 因不可信而被拒写）。</summary>
    public static bool LastSaveFailed => _lastSaveFailed;

    /// <summary>最近一次 <see cref="Save"/> 失败或被拒的原因（没失败时为空串）。</summary>
    public static string LastSaveFailureReason => _lastSaveFailureReason;

    /// <summary>
    /// 数据目录：与配置文件一同位于主目录\config（原先在 %APPDATA%）；
    /// 自检与测试可用 <c>DSHGUARD_DATA_DIR</c> 整体更换根目录，经 GuardPaths 生效。
    /// </summary>
    private static string DataDir => GuardPaths.ConfigDir;

    public static string StorePath => Path.Combine(DataDir, "versions.json");

    public static VersionState State
    {
        get { EnsureLoaded(); return _state!; }
    }

    private static void EnsureLoaded()
    {
        lock (Gate)
        {
            if (_loaded) return;
            _loaded = true;
            _state = Read();
        }
    }

    private static VersionState Read()
    {
        try
        {
            // 刻意用 ReadAllText 而不是 File.Exists 打头：File.Exists 在"文件不存在"与
            // "有文件但读不了（无权限/路径不可达）"两种情况下都只是**返回 false**，分不出来 ⇒
            // 后者会被当成"首次运行"，随后由 Save 拿空状态覆盖真实数据。
            // ReadAllText 抛的异常能把两者分开：文件/目录不存在 = 首次运行（正常，见下面两个 catch）；
            // 其余异常（IO / 无权限 / JSON 损坏）= 读失败 ⇒ 标记不可信、拒绝写入。
            string text = File.ReadAllText(StorePath);
            var s = JsonSerializer.Deserialize<VersionState>(text);
            if (s != null)
            {
                s.History ??= new List<VersionRecord>();
                return s;
            }
            // 文件在、内容却在反序列化后为空 ⇒ 同样不能把这份空状态当成真相
            MarkUnreliable("读到的内容不是有效的版本状态（反序列化结果为空）");
        }
        catch (FileNotFoundException) { return new VersionState(); }        // 还没落过盘 = 首次运行
        catch (DirectoryNotFoundException) { return new VersionState(); }  // 配置目录还没建 = 首次运行
        catch (Exception ex)
        {
            // 原来这里是空实现的 Logger.Log（"以为会落盘其实什么都没写"）⇒ 读失败无任何痕迹，
            // 且返回空状态、随后被 Save 写回，用户的 Pin / PreviousPin / History 被永久清空（本单缺陷①）。
            MarkUnreliable($"{ex.GetType().Name}: {ex.Message}");
        }
        return new VersionState();
    }

    /// <summary>
    /// 标记"本次状态不可信"，并**真落盘**记一条（走 <see cref="Logger.NoteDiagnosis"/>：写进异常日志，
    /// 不置失败标记、不弹窗）。这里的落盘是刻意的：读取失败是数据安全问题，必须留下可查的痕迹。
    /// </summary>
    private static void MarkUnreliable(string reason)
    {
        _stateUnreliable = true;
        _stateUnreliableReason = reason;
        Logger.NoteDiagnosis($"版本记忆读取失败，本次将拒绝写入以免清空用户数据（{StorePath}）：{reason}");
    }

    public static void Save()
    {
        try
        {
            lock (Gate)
            {
                if (_state == null) return;

                // ★ 失败关闭：读盘失败 ⇒ 手里的 _state 只是"读不出来时的空壳"，
                //   一旦写回就把用户的 Pin / PreviousPin / History 永久抹掉。这里直接拒写。
                //   （宁可这一次不落盘，也绝不许清空用户数据。）
                if (_stateUnreliable)
                {
                    _lastSaveFailed = true;
                    _lastSaveFailureReason = $"版本记忆读取失败，已拒绝写入以免清空数据（{_stateUnreliableReason}）";
                    if (!_refusalLogged)
                    {
                        _refusalLogged = true;
                        Logger.NoteDiagnosis(
                            $"版本记忆拒绝写入：本次读取失败，写回会清空用户的固定版本与履历（{StorePath}）。原因：{_stateUnreliableReason}");
                    }
                    return;
                }

                // 这里原有一句「上一长期版本与当前固定版本相同就清掉」的归一化
                // （`if (_state.Pin.Length > 0 && _state.PreviousPin == _state.Pin) _state.PreviousPin = "";`）。
                // 它是**写入侧**抹掉历史事实，已删除：留在盘上的 PreviousPin 表示"上一次长期用过哪一版"，
                // 它在"用户切回同一版"时本来就会与 Pin 相同；而"相同就不该显示给用户"这件事由
                // PreviousPin 读取属性（见下方 :175 附近）返回空串兜住，不需要把记录本身毁掉。
                // 现场缺陷：跟随最新版把 PreviousPin 记成刚离开的那一版，再点「自动管理」钉回同一版，
                // 两次落盘各抹一次，用户的「上一长期版本」被永久清空（History 里其实还在）。
                // 履历按最近出现时间排序，最多保留 20 条
                _state.History = _state.History
                    .GroupBy(r => r.Version)
                    .Select(g => g.First())
                    .OrderByDescending(r => r.LastSeen)
                    .Take(20)
                    .ToList();

                var dir = Path.GetDirectoryName(StorePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(StorePath, JsonSerializer.Serialize(_state,
                    new JsonSerializerOptions { WriteIndented = true }));

                // 写成功 ⇒ 清掉上一次的失败状态（界面据此不再提示"没存上"）
                _lastSaveFailed = false;
                _lastSaveFailureReason = "";
            }
        }
        catch (Exception ex)
        {
            // 原来这里是空实现的 Logger.Log ⇒ 写失败无痕迹、上层也不知道（本单缺陷①的另一半）。
            _lastSaveFailed = true;
            _lastSaveFailureReason = $"{ex.GetType().Name}: {ex.Message}";
            Logger.NoteDiagnosis($"版本记忆写入失败（{StorePath}）：{_lastSaveFailureReason}");
        }
    }

    private static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm");

    // ══════════════ 启动策略 ══════════════

    /// <summary>
    /// npx 版本说明符：更新模式用**精确目标版本**，未固定用 latest，否则用固定版本。
    /// 更新模式不能用 latest：npm 的 latest 标签可能仍指向旧版
    /// （0.1.5-rc.2 发布在 next 标签上，@latest 会再装一次 rc.1，导致升级不生效）。
    /// </summary>
    public static string Spec
    {
        get
        {
            var s = State;
            if (s.PendingUpdate) return s.UpdateTarget.Length > 0 ? s.UpdateTarget : "latest";
            return s.Pin.Length == 0 ? "latest" : s.Pin;
        }
    }

    /// <summary>默认启动命令（设置页留空时用它，守护壳再追加 --no-open --port N）。</summary>
    public static string LaunchCommand => $"npx --yes @deepseek-ai/dsh@{Spec} web";

    public static string Pin => State.Pin;
    /// <summary>该固定由用户手动设置，自动逻辑不得覆盖。</summary>
    public static bool IsManualPin => State.Pin.Length > 0 &&
        string.Equals(State.PinSource, "manual", StringComparison.OrdinalIgnoreCase);
    public static bool PendingUpdate => State.PendingUpdate;
    public static int ConsecutiveErrors => State.ConsecutiveErrors;
    public static string LastErrorReason => State.LastErrorReason;
    /// <summary>上一长期版本；与当前固定版本相同时视为没有（避免同版本记录两遍）。</summary>
    public static string PreviousPin
    {
        get
        {
            var s = State;
            return s.PreviousPin.Length > 0 && s.PreviousPin == s.Pin ? "" : s.PreviousPin;
        }
    }
    public static IReadOnlyList<VersionRecord> History => State.History;

    /// <summary>策略的单行描述，供 UI 展示。</summary>
    public static string PolicyText
    {
        get
        {
            var s = State;
            if (s.PendingUpdate) return "更新模式 · 下次启动拉取最新版";
            if (s.Pin.Length == 0) return "跟随最新版（尚未固定）";
            return IsManualPin ? "手动固定 " + s.Pin : "自动固定 " + s.Pin;
        }
    }

    // ══════════════ 记录与固定 ══════════════

    private static VersionRecord Touch(string version)
    {
        var s = State;
        var rec = s.History.FirstOrDefault(r => r.Version == version);
        if (rec == null)
        {
            rec = new VersionRecord { Version = version, FirstSeen = Now(), LastSeen = Now() };
            s.History.Add(rec);
        }
        return rec;
    }

    /// <summary>引擎就绪：记一次成功；更新模式则把新版本固定下来。</summary>
    public static void NoteEngineReady(string version)
    {
        if (string.IsNullOrWhiteSpace(version) || version == "未知") return;
        EnsureLoaded();
        var s = State;
        var rec = Touch(version);
        rec.Launches++;
        rec.LastSeen = Now();
        rec.Errors = 0;
        if (rec.MarkedBad) { rec.MarkedBad = false; rec.Note = ""; }

        if (s.PendingUpdate)
        {
            // 更新模式的这一次跑通了 → 固定新版本，旧版本成为回退候选
            string from = s.Pin.Length > 0 ? s.Pin : s.PendingFrom;
            if (version == from)
            {
                // 跑起来还是老版本 → 更新没落地（npx 仍给的是缓存里那份）：保持更新模式，下次启动再试
                Logger.Log($"版本记忆：更新未落地，仍是 {version}（继续保留更新模式）");
            }
            else
            {
                if (from.Length > 0) s.PreviousPin = from;
                s.Pin = version;
                // 这里原有一句「同一版本就把 PreviousPin 清掉」。删除理由同 Save() 与 PinAuto()：
                // 「与当前固定版本相同就不该显示」由 PreviousPin 读取属性兜住，写入侧不该抹掉历史事实。
                s.PinSource = "manual";      // 升级由用户发起，视为用户明确指定，之后自动逻辑不再修改
                s.PendingUpdate = false;
                s.PendingFrom = "";
                // 记录升级完成时间：此后启动异常按可能不兼容处理，首次失败即建议回退
                s.UpdatedFrom = from;
                s.UpdatedAt = Now();
                s.UpdateTarget = version;
                s.LastPromptedVersion = "";
            }
        }
        else if (s.Pin.Length == 0)
        {
            // 尚未固定过：首次启动成功即固定，避免每次启动都拉取 @latest
            s.Pin = version;
            s.PinSource = "auto";
            // 这里原有一句「same as version 就清空 PreviousPin」。首次固定时 PreviousPin 通常是
            // "跟随最新版之前用的那一版"，正是一条有价值的回退记录，更不该在写入侧抹掉。
        }
        // 手动固定的情况不做任何改动：自动逻辑不覆盖用户指定的版本

        if (s.ErrorVersion == version) { s.ConsecutiveErrors = 0; s.ErrorVersion = ""; s.LastErrorReason = ""; }
        Save();
    }

    /// <summary>引擎停止：累计运行时长；runSeconds 与 version 由调用方给出。</summary>
    public static void NoteEngineStopped(int runSeconds, string? version = null)
    {
        EnsureLoaded();
        string v = string.IsNullOrWhiteSpace(version) || version == "未知" ? State.Pin : version!;
        if (v.Length == 0 || runSeconds <= 0) return;
        var rec = Touch(v);
        rec.RunsSeconds += runSeconds;
        rec.LastSeen = Now();
        Save();
    }

    /// <summary>记录一次启动异常：启动失败 / 就绪超时 / 启动后立即退出 / 错误日志反复出现。</summary>
    public static void NoteError(string reason, string? version = null)
    {
        EnsureLoaded();
        var s = State;

        if (string.IsNullOrWhiteSpace(version) || version == "未知")
            version = s.Pin.Length > 0 ? s.Pin : VersionInfo.GetCurrentVersion();

        s.ConsecutiveErrors++;
        s.ErrorVersion = version ?? "";
        s.LastErrorReason = reason ?? "";
        if (!string.IsNullOrEmpty(version))
        {
            var rec = Touch(version!);
            rec.Errors++;
            rec.Note = reason ?? "";
            rec.LastSeen = Now();
        }
        Logger.Log($"版本记忆：{version} 启动异常 ×{s.ConsecutiveErrors}（{reason}）");
        Save();
    }

    // ══════════════ 更新 / 回退 ══════════════

    /// <summary>进入更新模式：下一次启动使用目标版本，就绪后自动固定到新版本。</summary>
    public static void BeginUpdate(string targetVersion)
    {
        EnsureLoaded();
        var s = State;
        s.PendingFrom = s.Pin.Length > 0 ? s.Pin : VersionInfo.GetCurrentVersion();
        s.PendingUpdate = true;
        s.UpdateTarget = targetVersion ?? "";   // 必须落盘：启动命令要用它拼精确版本号
        s.LastPromptedVersion = "";
        s.ConsecutiveErrors = 0;
        Logger.Log($"版本记忆：进入更新模式（目标 {targetVersion}，当前 {s.PendingFrom}）");
        Save();
    }

    /// <summary>
    /// 补写更新目标：早先版本只记了「待更新」而没落盘目标版本，这里在查到最新版后把它补上。
    /// 否则启动命令只能退回 latest，而 latest 标签可能仍指向旧版（表现为升级不生效）。
    /// </summary>
    public static void EnsureUpdateTarget(string latest)
    {
        if (string.IsNullOrWhiteSpace(latest)) return;
        EnsureLoaded();
        var s = State;
        if (!s.PendingUpdate || s.UpdateTarget.Length > 0) return;
        s.UpdateTarget = latest;
        Save();
        Logger.Log($"版本记忆：补写更新目标 {latest}");
    }

    /// <summary>取消更新模式，仍按已固定版本启动。</summary>
    public static void CancelUpdate()
    {
        EnsureLoaded();
        State.PendingUpdate = false;
        State.PendingFrom = "";
        Save();
    }

    /// <summary>跟随最新版：清除固定版本，下次启动使用 @latest。</summary>
    public static void FollowLatest()
    {
        EnsureLoaded();
        var s = State;
        // 上一长期版本只在"还没有更早记录"时才回填。
        // 原先是无条件覆盖（`if (s.Pin.Length > 0) s.PreviousPin = s.Pin;`），会把真正的
        //「上上个长期版本」顶掉：现场 0.1.5-rc.2（90 次启动）就是这么在点「跟随最新版」时消失的
        // —— 记录还在 History 里，但 PreviousPin 已被换成刚离开的那一版，设置页只剩
        //「（尚无上一个长期版本）」。
        //
        // 判据：s.PreviousPin == s.Pin 也算"已有记录"（**不**回填）。留着的同值记录表示
        // "上一次长期用的就是这个版本"，它是对"曾经长期用过它"的事实陈述；而"与当前固定版本
        // 相同就不该显示给用户"由 PreviousPin 读取属性返回空串兜住（见上方）。
        // 若把它当成"没有记录"而用 s.Pin 去回填，结果仍是同一个字符串、值不变，却会连带
        // 在做完清理的 Save()/写入侧之外多绕一圈语义，得不偿失；"该不该显示"是读取侧的事。
        if (s.Pin.Length > 0 && s.PreviousPin.Length == 0) s.PreviousPin = s.Pin;
        s.Pin = "";
        s.PinSource = "";
        s.PendingUpdate = false;
        s.PendingFrom = "";
        Save();
    }

    /// <summary>回退：将固定版本改为旧版本；连续出现异常的当前版本自动打标。</summary>
    public static void RollbackTo(string version, string? reason = null)
    {
        EnsureLoaded();
        var s = State;
        string from = s.Pin.Length > 0 ? s.Pin : s.ErrorVersion;

        if (from.Length > 0 && from != version)
        {
            var bad = s.History.FirstOrDefault(r => r.Version == from);
            if (bad != null && (s.ConsecutiveErrors >= 1 || s.ErrorVersion == from))
            {
                bad.MarkedBad = true;
                bad.Note = string.IsNullOrEmpty(s.LastErrorReason) ? (reason ?? "启动异常") : s.LastErrorReason;
            }
            s.PreviousPin = from;
        }

        s.Pin = version;
        s.PinSource = "manual";     // 回退由用户发起，视为用户明确指定
        s.PendingUpdate = false;
        s.PendingFrom = "";
        s.ConsecutiveErrors = 0;
        s.ErrorVersion = "";
        s.LastErrorReason = "";
        s.LastPromptedVersion = "";
        s.UpdatedFrom = "";       // 刚升级窗口结束，后续异常按常规阈值判定
        s.UpdatedAt = "";
        Logger.Log($"版本记忆：回退固定版本 {from} → {version}");
        Save();
    }

    /// <summary>手动固定指定版本：同时清除 MarkedBad，固定来源记为 manual，自动逻辑不再修改。</summary>
    public static void PinTo(string version)
    {
        EnsureLoaded();
        var s = State;
        if (s.Pin.Length > 0 && s.Pin != version) s.PreviousPin = s.Pin;
        var rec = Touch(version);
        rec.MarkedBad = false;
        rec.Note = "";
        s.Pin = version;
        s.PinSource = "manual";
        s.PendingUpdate = false;
        s.PendingFrom = "";
        s.ConsecutiveErrors = 0;
        s.ErrorVersion = "";
        Save();
    }

    /// <summary>将固定来源改为自动：保留当前版本号，允许后续升级或回退的自动逻辑接管。</summary>
    public static void MakePinAuto()
    {
        EnsureLoaded();
        State.PinSource = State.Pin.Length > 0 ? "auto" : "";
        Save();
    }

    /// <summary>把版本固定为「自动管理」：跟随最新版之后想改回用当前版本时调用。</summary>
    public static void PinAuto(string version)
    {
        if (string.IsNullOrWhiteSpace(version) || version == "未知") return;
        EnsureLoaded();
        var s = State;
        s.Pin = version;
        s.PinSource = "auto";
        s.PendingUpdate = false;
        s.PendingFrom = "";
        // 这里原先还有一句 `if (s.PreviousPin == version) s.PreviousPin = "";`（注释："同一版本不算「上一长期版本」"）。
        // 本意没错，但落在**写入侧**就是错的：它抹掉的是"曾经长期用过这个版本"这件事实本身，
        // 而"与当前固定版本相同就不该显示给用户"这件事，PreviousPin 读取属性返回空串就兜住了
        //（同理，Save() 里那句同款的归一律也已删除），不需要在写入侧把记录毁掉。
        // 现场缺陷：跟随最新版把 PreviousPin 记成刚离开的那一版，用户再点「自动管理」钉回同一版时，
        // 这个参数恰好等于 PreviousPin ⇒ 两个写入侧动作接连把「上一长期版本」清空，
        // 设置页显示「尚无可回退的长期版本」，而 History 里的记录其实还在。
        Logger.Log($"版本记忆：固定 {version}（自动管理）");
        Save();
    }

    /// <summary>
    /// 累加运行时长。计时器每 30 秒结算一次并落盘：只在"停止/关壳"时结算会因进程被强制结束而丢账。
    /// </summary>
    public static void AddRunSeconds(string version, int seconds)
    {
        if (seconds <= 0) return;
        EnsureLoaded();
        var s = State;
        string v = string.IsNullOrWhiteSpace(version) || version == "未知" ? s.Pin : version!;
        if (v.Length == 0) return;
        var rec = Touch(v);
        rec.RunsSeconds += seconds;
        rec.LastSeen = Now();
        Save();
    }

    // ══════════════ 回退候选与提示 ══════════════

    /// <summary>回退候选：上一长期版本优先，其余按最近出现时间排序；本机缓存中已存在但无记录的版本一并列出。</summary>
    public static List<VersionRecord> RollbackOptions()
    {
        EnsureLoaded();
        var s = State;
        var list = s.History
            .Where(r => r.Version != s.Pin)
            .OrderByDescending(r => r.Version == s.PreviousPin)
            .ThenByDescending(r => r.LastSeen)
            .ToList();

        foreach (var (v, t) in VersionInfo.ListCachedVersions())
        {
            if (v == s.Pin || list.Any(r => r.Version == v)) continue;
            list.Add(new VersionRecord
            {
                Version = v,
                LastSeen = t.ToString("yyyy-MM-dd HH:mm"),
                Note = "本机已缓存（未记录运行履历）"
            });
        }
        return list;
    }

    /// <summary>
    /// 最佳回退目标；没有**真的可用**的候选时返回空串（宁可不出"换版本"按钮，也不给空头支票）。
    ///
    /// 现场教训（2026-09-13 图一）：给用 0.1.5 的用户推荐「换回 DSH 0.1.1-rc.2」——那台机器上
    /// 根本没装过、也没缓存过；换成它只会更糟。所以候选必须：① 未标记有问题；② **本机 npx 缓存里确实有**
    /// （能直接跑起来）；③ 优先"近期真的跑起来过"的（错误少、跑得久、最近见过）。
    /// </summary>
    public static string BestRollbackCandidate()
    {
        EnsureLoaded();
        var s = State;

        var cached = VersionInfo.ListCachedVersions();
        // 近期性：候选必须与本机最新缓存版本同属一条"基础版本线"（例如都在 0.1.5 上）。
        // 现场教训：给用 0.1.5 的用户推荐 0.1.1-rc.2 是乱指路——它虽然还在缓存里，但早已过时。
        string newest = cached.Count == 0 ? ""
            : cached.OrderByDescending(c => VersionRank(c.Version)).First().Version;
        string Line(string? v)
        {
            var m = System.Text.RegularExpressions.Regex.Match(v ?? "", @"(\d+)\.(\d+)\.(\d+)");
            return m.Success ? m.Groups[1].Value + "." + m.Groups[2].Value + "." + m.Groups[3].Value : "";
        }
        string newestLine = Line(newest);
        bool IsUsable(string v) => v.Length > 0
            && cached.Any(c => c.Version == v)
            && (newestLine.Length == 0 || Line(v).Equals(newestLine, StringComparison.OrdinalIgnoreCase));

        if (s.PreviousPin.Length > 0 && s.PreviousPin != s.Pin && IsUsable(s.PreviousPin) &&
            s.History.Any(r => r.Version == s.PreviousPin && !r.MarkedBad))
            return s.PreviousPin;

        // 有运行履历的：跑得久、错误少的优先
        var rec = s.History
            .Where(r => r.Version != s.Pin && !r.MarkedBad && IsUsable(r.Version))
            .OrderByDescending(r => r.Errors == 0)
            .ThenByDescending(r => r.RunsSeconds)
            .ThenByDescending(r => r.LastSeen)
            .FirstOrDefault();
        if (rec != null) return rec.Version;

        // 兜底：本机缓存里**最新的**那个（不是列表里碰巧排第一的那个）
        var pick = cached
            .Where(c => c.Version != s.Pin && !s.History.Any(r => r.Version == c.Version && r.MarkedBad))
            .OrderByDescending(c => VersionRank(c.Version))
            .ThenByDescending(c => c.Time)
            .FirstOrDefault();
        return pick.Version ?? "";
    }

    /// <summary>版本号排序用的粗略权重（"0.1.5-rc.2" → 越大越新；认不出的算 0）。</summary>
    internal static double VersionRank(string? version)
    {
        try
        {
            var m = System.Text.RegularExpressions.Regex.Match(version ?? "", @"(\d+)\.(\d+)\.(\d+)(?:-rc\.(\d+))?");
            if (!m.Success) return 0;
            double major = int.Parse(m.Groups[1].Value);
            double minor = int.Parse(m.Groups[2].Value);
            double patch = int.Parse(m.Groups[3].Value);
            double rc = m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : 999;   // 正式版排在 rc 之后
            return major * 1_000_000 + minor * 10_000 + patch * 100 + Math.Min(rc, 99);
        }
        catch { return 0; }
    }

    /// <summary>
    /// 是否应提示回退：存在候选版本、该版本尚未提示过，且
    /// 刚升级时（默认 24 小时内）失败 1 次即提示，否则需连续失败 2 次。
    /// </summary>
    public static bool ShouldPromptRollback(out string candidate, out int errors, out string version)
    {
        EnsureLoaded();
        var s = State;
        version = s.ErrorVersion.Length > 0 ? s.ErrorVersion : s.Pin;
        errors = s.ConsecutiveErrors;
        candidate = BestRollbackCandidate();

        bool justUpdated = IsRecentlyUpdated(24, out _, out _);
        int threshold = justUpdated ? 1 : 2;

        return errors >= threshold
               && candidate.Length > 0
               && version.Length > 0
               && candidate != version
               && s.LastPromptedVersion != version;
    }

    /// <summary>最近是否刚完成升级；刚升级后的启动异常更可能源于不兼容，应更早提示回退。</summary>
    public static bool IsRecentlyUpdated(int hours, out string from, out string at)
    {
        EnsureLoaded();
        from = State.UpdatedFrom;
        at = State.UpdatedAt;
        if (from.Length == 0 || at.Length == 0) return false;
        if (!DateTime.TryParse(at, out var t)) return false;
        return (DateTime.Now - t) <= TimeSpan.FromHours(hours);
    }

    /// <summary>记录升级时为规避兼容问题而禁用的插件名，回退时提示恢复。</summary>
    public static void RecordDisabledForUpdate(IEnumerable<string> names)
    {
        EnsureLoaded();
        var s = State;
        s.DisabledForUpdate = names.Where(n => !string.IsNullOrWhiteSpace(n))
                                   .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Save();
    }

    public static IReadOnlyList<string> DisabledForUpdate => State.DisabledForUpdate;

    public static void ClearDisabledForUpdate()
    {
        EnsureLoaded();
        State.DisabledForUpdate = new List<string>();
        Save();
    }

    public static void MarkPrompted(string version)
    {
        EnsureLoaded();
        State.LastPromptedVersion = version ?? "";
        Save();
    }
}
