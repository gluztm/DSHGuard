using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DSHGuard;

/// <summary>
/// 插件本地记账表（纯数据 + 纯操作，**不碰盘**）：两列各记一件事，构造出来即可直接断言，自检不需要任何磁盘夹具。
///   · <see cref="Subscribed"/>：订阅时间 —— 这个插件是什么时候装到本机的；
///   · <see cref="Updated"/>：更新时间 —— 用户什么时候更新过它。
/// 两列同存于一张表、同一个文件，成对读写（读写由 <see cref="PluginTimes"/> 负责）。
/// </summary>
internal sealed class PluginTimeTable
{
    /// <summary>订阅时间：包名 → <c>yyyy-MM-dd HH:mm</c>。</summary>
    public Dictionary<string, string> Subscribed { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>更新时间：包名 → <c>yyyy-MM-dd HH:mm</c>。</summary>
    public Dictionary<string, string> Updated { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 记一次安装：**总是覆盖**为传入时间。
    ///
    /// 为什么必须是覆盖而不是"只记第一次"：卸载之后再装回来，订阅时间要刷新成最近这一次；
    /// 若沿用第一次的时间，重装过的插件会继续显示上一轮的安装日期 —— 记账撒谎比没有记录更糟。
    /// 包名或时间为空、空白 ⇒ 什么也不做（写进去也只会得到一条读出来是空串的记录，与没有记录无法区分）。
    /// 刻意**不动** <see cref="Updated"/> 列：安装这个动作只负责订阅时间这一列，
    /// "卸载后重装要看起来是新的"由卸载路径调 <see cref="Remove"/> 清两列保证，不在这里顺手抹掉另一条事实。
    /// </summary>
    internal void StampSubscribed(string? package, string? time)
    {
        string name = (package ?? "").Trim();
        string stamp = (time ?? "").Trim();
        if (name.Length == 0 || stamp.Length == 0) return;
        Subscribed[name] = stamp;
    }

    /// <summary>
    /// 记一次更新：更新成功后覆盖为传入时间（同样是覆盖，不是只记第一次）。
    /// 包名或时间为空、空白 ⇒ 什么也不做。
    /// </summary>
    internal void StampUpdated(string? package, string? time)
    {
        string name = (package ?? "").Trim();
        string stamp = (time ?? "").Trim();
        if (name.Length == 0 || stamp.Length == 0) return;
        Updated[name] = stamp;
    }

    /// <summary>
    /// 卸载：把该包的两条记录一起删掉。
    /// 为什么两条都删：订阅时间不删 ⇒ "卸载再安装"看不出是一次新的安装（旧记录还在，重装会被当成老记录）；
    /// 更新时间不删 ⇒ 装回来的插件会带着上一轮的更新时间，而那个时间属于上一次安装，不是这一次。
    /// </summary>
    internal void Remove(string? package)
    {
        string name = (package ?? "").Trim();
        if (name.Length == 0) return;
        Subscribed.Remove(name);
        Updated.Remove(name);
    }

    /// <summary>该包的订阅时间；没有记录返回空串（显示成什么由调用方决定，本层不编"未知"之类的话术）。</summary>
    internal string SubscribedOf(string? package) => Lookup(Subscribed, package);

    /// <summary>该包的更新时间；没有记录返回空串。</summary>
    internal string UpdatedOf(string? package) => Lookup(Updated, package);

    /// <summary>查一列；包名为空、无记录、值为空一律返回空串（盘上被写成 null 的条目也按"没有记录"处理）。</summary>
    private static string Lookup(Dictionary<string, string> column, string? package)
    {
        string name = (package ?? "").Trim();
        if (name.Length == 0) return "";
        // 显式声明为可空：字典的 TValue 是 string（非空），但盘上可能存过 null，
        // 反序列化后 TryGetValue 取出来的就可能是 null；写成 string? 才如实反映这一点。
        if (!column.TryGetValue(name, out string? found)) return "";
        return string.IsNullOrEmpty(found) ? "" : found;
    }
}

/// <summary>
/// 插件本地记账：**订阅时间**与**更新时间**两列的落盘与查询（两列同存一个文件）。
///
/// 为什么需要它：插件页的排序要用到两个**本机事实**，而插件市场没有任何接口能提供：
///   · 订阅时间 = 这个插件是什么时候装到本机的（不是作者的发版时间，也不是仓库创建时间）；
///   · 更新时间 = 用户什么时候更新过它（同样是本机行为，不是作者的发布时间）。
/// 两者都只能由本程序在动作发生的那一刻记下来，事后无法补算 —— 因此**从本版本开始记账，对已有插件一律没有记录**，
/// 界面显示"未知"；这里刻意**不做任何回填**（回填只能靠猜，而猜出来的日期会被当成事实展示给用户）。
///
/// 存储：<see cref="GuardPaths.PluginTimesFile"/>（Config 目录，与 settings.json 同目录，可随时删除重建）。
/// 格式：<c>{"Subscribed":{"包名":"2026-09-21 14:46"},"Updated":{"包名":"…"}}</c>，时间一律 <c>yyyy-MM-dd HH:mm</c>。
///
/// 数据安全口径与 <see cref="VersionMemory"/> 一致（fail-closed）：
///   · 读盘失败（无权限 / IO 异常 / 文件内容损坏）⇒ 内存里只有一份空表，**标记不可信**，此后 <see cref="Save"/> 一律拒绝写入；
///     宁可这一次不落盘，也绝不许拿空表把用户的账目覆盖掉；
///   · **文件不存在**（全新安装）不算读失败 ⇒ 允许写入（两类情形在 <see cref="ReadLocked"/> 里被分开）；
///   · 判断依据只有"盘上读到了什么"，与本程序想写什么无关。
/// </summary>
internal static class PluginTimes
{
    /// <summary>内存表与落盘的互斥闸门（本类的每个入口都先进它，见类尾"并发"一节）。</summary>
    private static readonly object Gate = new();

    /// <summary>落盘/读取共用的序列化选项（写与读用同一份，保证写出去的能按同样的规则读回来）。</summary>
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        // 属性名大小写不敏感：手改过大小写的文件也读得回来；读不回来就会被判成"损坏"，进而永久拒写
        PropertyNameCaseInsensitive = true
    };

    private static PluginTimeTable _state = new();
    private static bool _loaded;

    /// <summary>
    /// **本次读到的账目不可信**：读盘失败（IO 异常 / 无权限 / 文件内容不是合法账目）时置位。
    ///
    /// 为什么必须有它：读失败时只能返回一张**空表**，而调用方（安装、更新、卸载之后）会照常往上写记录再
    /// <see cref="Save"/> ⇒ 这张空表被写回盘上，用户已有的账目被**永久清空**。
    /// 置位之后 <see cref="Save"/> 一律拒绝写入 —— 宁可这一次不落盘，也绝不许覆盖用户数据。
    /// 只有 <see cref="Load"/> 重新读一次（或进程重启）才会改变它。
    /// </summary>
    private static bool _unreliable;

    /// <summary>不可信的原因（可信时为空串）。</summary>
    private static string _unreliableReason = "";

    /// <summary>已经就"拒绝写入"记过一次日志（避免同一次读取失败被反复刷进日志）。</summary>
    private static bool _refusalLogged;

    /// <summary>最近一次落盘是否失败（含"因不可信而被拒写"）。</summary>
    private static bool _lastSaveFailed;

    /// <summary>最近一次落盘失败的原因（没失败时为空串）。</summary>
    private static string _lastSaveFailureReason = "";

    /// <summary>记账文件：**只从 <see cref="GuardPaths.PluginTimesFile"/> 取**（不自己拼 %APPDATA% / AppContext，路径来源唯一）。</summary>
    internal static string StorePath => GuardPaths.PluginTimesFile;

    /// <summary>本次读到的账目是否不可信（为 true 时 <see cref="Save"/> 拒绝写入）。</summary>
    internal static bool StateUnreliable => _unreliable;

    /// <summary>不可信的原因（可信时为空串）。</summary>
    internal static string StateUnreliableReason => _unreliableReason;

    /// <summary>最近一次落盘是否失败或被拒写。</summary>
    internal static bool LastSaveFailed => _lastSaveFailed;

    /// <summary>最近一次落盘失败或被拒的原因（没失败时为空串）。</summary>
    internal static string LastSaveFailureReason => _lastSaveFailureReason;

    // ══════════════ 纯函数区（不碰盘、可自检）══════════════

    /// <summary>解析记账 JSON；坏 JSON / 空 ⇒ 返回空表，**绝不抛**（"算不算读成功"由下面带 out 的重载给出）。</summary>
    internal static PluginTimeTable Parse(string? json) => Parse(json, out _);

    /// <summary>
    /// 解析记账 JSON，并报告"这份内容算不算读成功"。
    /// <paramref name="readable"/> 为 false 的三种情形：内容为空、JSON 不合法、根节点为 null —— 都返回空表且不抛。
    ///
    /// 为什么要把"算不算读成功"单独分出来：<see cref="Load"/> 必须区分"文件里没有账目（全新安装）"与
    /// "文件里有账目但读不出来（损坏 / 读不了）"—— 前者允许写入，后者一旦写入就把用户数据覆盖掉了。
    /// 内容合法的 JSON 里若只有一列（另一列缺失或被写成 null），按"该列为空"接受：缺的那一列本来就没有数据可丢，
    /// 没必要为此把整份账目判成损坏而永久拒写。
    /// </summary>
    internal static PluginTimeTable Parse(string? json, out bool readable)
    {
        readable = false;
        var table = new PluginTimeTable();
        if (string.IsNullOrWhiteSpace(json)) return table;
        try
        {
            var read = JsonSerializer.Deserialize<PluginTimeTable>(json, JsonOpts);
            if (read == null) return table;                    // JSON 字面量 null ⇒ 这不是一份账目
            table.Subscribed = SanitizeColumn(read.Subscribed);
            table.Updated = SanitizeColumn(read.Updated);
            readable = true;
            return table;
        }
        catch { return table; }                                 // 坏 JSON ⇒ 空表；readable 保持 false，调用方据此拒绝写回
    }

    /// <summary>
    /// 序列化（纯函数）：写盘与自检共用同一份格式，保证"写出去的能被自己读回来"。
    /// </summary>
    internal static string ToJson(PluginTimeTable? table)
    {
        var t = table ?? new PluginTimeTable();
        return JsonSerializer.Serialize(t, JsonOpts);
    }

    /// <summary>
    /// 中文日期格式化（纯函数）：<c>"2026-09-19 21:52"</c> ⇒ <c>"2026年09月19日"</c>（界面按 xxxx年xx月xx日 显示）。
    ///
    /// 解析不出来就**原样返回输入** —— 不编一个日期、不抛异常、不返回空串：
    /// 这一列要么是本程序自己写的标准格式，要么是从别处来的、认不出的形态；
    /// 后者如实显示原文，至少能让人看出"这里有一条看不懂的记录"；凭空补一个日期则是**编造事实**。
    /// 输入为 null 时返回空串（null 没有"原样"的字符串表示，口径与既有 <c>Logger.TruncateCommandOutput</c> 对 null 的处理一致）。
    ///
    /// 与"直接 TryParse 再格式化"的两处刻意差异，都是为了防止**编出一个用户没记过的日期**：
    ///   · <b>先要求输入里有一个四位数字的年</b>：单纯 <c>DateTime.TryParse</c> 会把 <c>"21:52"</c> 解析成
    ///     **今天的** 21:52（当日零点为基准补全日期），于是界面会显示一个从未发生过的日期。宁可原样显示 <c>21:52</c>。
    ///   · <b>用 <see cref="DateTimeStyles.RoundtripKind"/></b> 而不是 <see cref="DateTimeStyles.None"/>：后者会把
    ///     带 <c>Z</c> / 时区偏移的串按 UTC 换算到本机时区，<c>"2026-09-19T21:52:00Z"</c> 在 UTC+8 会变成
    ///     <c>2026年09月20日</c> —— 平白多出一天。本模块自己写的时间一律不带时区，RoundtripKind 对它完全等价，
    ///     但对带时区的串不再改写日期。
    /// 格式化成字符串时同样传 InvariantCulture：格式串里没有会被区域改写的占位符，但显式传可以避免"当前区域
    /// 使用非公历历法"时年份被换算（例如某些区域下的佛历 / 民国纪年）。
    /// </summary>
    internal static string FormatCnDate(string? time)
    {
        if (string.IsNullOrEmpty(time)) return "";
        try
        {
            // 没有四位数字的年 ⇒ 不认（否则 "21:52" 这类时间串会被补上今天的日期）
            if (!Regex.IsMatch(time, @"\d{4}")) return time;
            return DateTime.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime parsed)
                ? parsed.ToString("yyyy年MM月dd日", CultureInfo.InvariantCulture)
                : time;
        }
        catch { return time; }                                  // 防御：任何解析异常都退回原文，绝不抛给调用方
    }

    // ══════════════ 读写区（有副作用）══════════════

    /// <summary>
    /// 重新从磁盘读一次（强制刷新；供启动时预热、显式刷新与自检使用）。
    /// 读失败**不抛**：内存换成空表并标记不可信，此后 <see cref="Save"/> 拒绝写入。
    /// ⚠ 是强制重读：内存里尚未落盘的改动会被盘上内容取代。
    /// </summary>
    internal static void Load()
    {
        lock (Gate)
        {
            _state = ReadLocked();
            _loaded = true;
        }
    }

    /// <summary>首次访问时读一次盘（进程内只自动读一次，之后的改动都走内存 + <see cref="Save"/>）。</summary>
    private static void EnsureLoadedLocked()
    {
        if (_loaded) return;
        _loaded = true;
        _state = ReadLocked();
    }

    /// <summary>
    /// 读盘。刻意用 <c>File.ReadAllText</c> 而不是 <c>File.Exists</c> 打头：<c>File.Exists</c> 在
    /// "文件不存在"与"有文件但读不了（无权限 / 路径不可达）"两种情况下都只是**返回 false**，分不出来 ⇒
    /// 后者会被当成"全新安装"，随后由 <see cref="Save"/> 拿空表覆盖真实账目。
    /// ReadAllText 抛出的异常能把两者分开：文件 / 目录不存在 = 全新安装（正常，允许写入）；
    /// 其余异常（IO / 无权限）以及"读到了但不是合法账目" = 读失败 ⇒ 标记不可信、拒绝写入。
    /// </summary>
    private static PluginTimeTable ReadLocked()
    {
        // 每次读到结论都重新起算：用户把文件修好之后再读一次，就该重新允许写入
        _unreliable = false;
        _unreliableReason = "";
        _refusalLogged = false;
        try
        {
            string text = File.ReadAllText(StorePath);
            var table = Parse(text, out bool readable);
            if (readable) return table;
            MarkUnreliableLocked("文件内容不是合法的账目 JSON（解析失败）");
            return new PluginTimeTable();
        }
        catch (FileNotFoundException) { return new PluginTimeTable(); }        // 还没落过盘 = 全新安装，允许写入
        catch (DirectoryNotFoundException) { return new PluginTimeTable(); }  // 配置目录还没建 = 全新安装，允许写入
        catch (Exception ex)
        {
            MarkUnreliableLocked($"{ex.GetType().Name}: {ex.Message}");
            return new PluginTimeTable();
        }
    }

    /// <summary>
    /// 标记"本次读到的账目不可信"，并**真落盘**记一条（走 <see cref="Logger.NoteDiagnosis"/>：写进异常日志，
    /// 不置失败标记、不弹窗）。这里刻意不用 <see cref="Logger.LogError"/>：一次记账读失败不该把本次运行
    /// 点亮成"出过异常"，否则一份正常的启动日志会因为记账问题不再按健康清理。
    /// （注：<c>Logger.Log</c> / <c>Logger.LogDiagnosis</c> 是空实现，真落盘只能用 NoteDiagnosis / LogError。）
    /// </summary>
    private static void MarkUnreliableLocked(string reason)
    {
        _unreliable = true;
        _unreliableReason = reason;
        Logger.NoteDiagnosis($"插件记账读取失败，本次将拒绝写入以免覆盖用户账目（{StorePath}）：{reason}");
    }

    /// <summary>
    /// 落盘（UTF-8 无 BOM）。三条规矩：
    ///   ① **失败关闭**：本次读盘失败 ⇒ 手里的表只是"读不出来时的空壳"，写回等于抹掉用户账目 ⇒ 直接拒绝写入；
    ///   ② 写之前先确保盘上内容已读过（<see cref="EnsureLoadedLocked"/>）：否则在没读过盘的情况下调用本方法，
    ///      会用一张空表覆盖盘上已有的账目；
    ///   ③ 写失败只记诊断、不抛：调用方不该因为一次记账失败而中断安装 / 更新流程（真相留在
    ///      <see cref="LastSaveFailed"/> / <see cref="LastSaveFailureReason"/> 上供上层查看）。
    /// </summary>
    internal static void Save()
    {
        lock (Gate)
        {
            EnsureLoadedLocked();
            SaveLocked();
        }
    }

    private static void SaveLocked()
    {
        try
        {
            if (_unreliable)
            {
                _lastSaveFailed = true;
                _lastSaveFailureReason = $"账目读取失败，已拒绝写入以免覆盖（{_unreliableReason}）";
                if (!_refusalLogged)
                {
                    _refusalLogged = true;
                    Logger.NoteDiagnosis(
                        $"插件记账拒绝写入：本次读取失败，写回会覆盖盘上已有的账目（{StorePath}）。原因：{_unreliableReason}");
                }
                return;
            }

            string? dir = Path.GetDirectoryName(StorePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(StorePath, ToJson(_state), new UTF8Encoding(false));

            // 写成功 ⇒ 清掉上一次的失败状态（上层据此不再提示"没存上"）
            _lastSaveFailed = false;
            _lastSaveFailureReason = "";
        }
        catch (Exception ex)
        {
            _lastSaveFailed = true;
            _lastSaveFailureReason = $"{ex.GetType().Name}: {ex.Message}";
            Logger.NoteDiagnosis($"插件记账写入失败（{StorePath}）：{_lastSaveFailureReason}");
        }
    }

    /// <summary>
    /// 把从 JSON 读到的一列整成"本程序认得的形状"：键去空白、空键与空值一律丢弃、比较器统一为忽略大小写。
    ///
    /// 为什么要重建字典：属性初始化器给的"忽略大小写"比较器会在反序列化时被换掉（反序列化器新建一个默认
    /// 比较器的字典再赋给属性）⇒ 不换回来就会出现"明明记过却查不到"（包名大小写不一致时）。
    /// 为什么要丢空值：盘上可能存在被写成 null / 空串的条目，它们读出来与"没有记录"无法区分，留着只会干扰写回。
    /// </summary>
    private static Dictionary<string, string> SanitizeColumn(Dictionary<string, string>? src)
    {
        var dst = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (src == null) return dst;
        foreach (var kv in src)
        {
            string key = kv.Key == null ? "" : kv.Key.Trim();
            string val = kv.Value == null ? "" : kv.Value.Trim();
            if (key.Length == 0 || val.Length == 0) continue;
            dst[key] = val;
        }
        return dst;
    }

    // ══════════════ 查询区（读内存；首次访问顺带读一次盘）══════════════

    /// <summary>该包的订阅时间；没有记录返回空串（显示"未知"还是别的，由调用方决定）。</summary>
    internal static string SubscribedOf(string? package)
    {
        lock (Gate)
        {
            EnsureLoadedLocked();
            return _state.SubscribedOf(package);
        }
    }

    /// <summary>该包的更新时间；没有记录返回空串。</summary>
    internal static string UpdatedOf(string? package)
    {
        lock (Gate)
        {
            EnsureLoadedLocked();
            return _state.UpdatedOf(package);
        }
    }

    // ══════════════ 记账区（改内存 + 立即落盘）══════════════

    /// <summary>安装成功后调用：把该包的订阅时间覆盖成 <paramref name="time"/> 并落盘（卸载后重装同样刷新）。</summary>
    internal static void StampSubscribed(string? package, string? time)
    {
        lock (Gate)
        {
            EnsureLoadedLocked();
            _state.StampSubscribed(package, time);      // 参数无效时表不变，下面的落盘只是把同样的内容再写一遍
            SaveLocked();
        }
    }

    /// <summary>更新成功后调用：把该包的更新时间覆盖成 <paramref name="time"/> 并落盘。</summary>
    internal static void StampUpdated(string? package, string? time)
    {
        lock (Gate)
        {
            EnsureLoadedLocked();
            _state.StampUpdated(package, time);
            SaveLocked();
        }
    }

    /// <summary>卸载成功后调用：删掉该包的两条记录并落盘（否则"卸载再安装"看不出是一次新的安装）。</summary>
    internal static void Remove(string? package)
    {
        lock (Gate)
        {
            EnsureLoadedLocked();
            _state.Remove(package);
            SaveLocked();
        }
    }

    // ══════════════ 自检支持 ══════════════

    /// <summary>
    /// 自检用：把进程内的缓存状态清空，让下一次访问**重新读盘**（例如同一进程里换了
    /// <c>DSHGUARD_DATA_DIR</c> 之后要重新验证读取路径）。只清缓存，不碰磁盘上的文件。
    /// </summary>
    internal static void ResetForTest()
    {
        lock (Gate)
        {
            _state = new PluginTimeTable();
            _loaded = false;
            _unreliable = false;
            _unreliableReason = "";
            _refusalLogged = false;
            _lastSaveFailed = false;
            _lastSaveFailureReason = "";
        }
    }

    // ══════════════ 并发（为什么加锁）══════════════
    // 本类的调用点分布在三条时间线上：① 界面线程（插件页读取并显示日期）；② 安装 / 更新 / 卸载完成后的回调
    // （这些流程本身是异步的，续体可能落在非界面线程上）；③ 启动阶段与自检。三者可能同时读写同一张表：
    //   · 两个线程同时改 Dictionary ⇒ 内部结构可能被写坏（丢条目、甚至死循环）；
    //   · 一个线程在 TryGetValue、另一个在写入 ⇒ 同样是不安全的并发访问。
    // 因此与 VersionMemory 同款：一把静态闸门（Gate）串起本类的全部入口，读、写、落盘都在锁内完成。
    // 代价说明：锁内包含一次文件读写，但文件很小（几百字节 ~ 几 KB）、且只在安装 / 更新 / 卸载这种低频动作时发生，
    // 不会成为界面卡顿来源；换来的是"任何时刻内存表与盘上内容都是一致的"。
}
