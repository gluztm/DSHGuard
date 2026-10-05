using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DSHGuard;

// ══════════════════════════════════════════════════════════════════════════════
//  1.4「生态趋势」数据层
//
//  数据源：dsh.so 的公开静态 JSON 端点（免密钥、CORS 全开、CDN 缓存 1 小时）：
//    · /data/star-trend.json    涨星榜（窗口内涨了多少星）
//    · /data/npm-downloads.json 本周下载榜（近 7 天窗口）
//    · /data/stars.json         星标榜（同一份报文里含 plugin / ecoPlugin / ecoApp 三板，本次只用 plugin）
//    · /data/npm-popular.json   总计下载榜（历史累计下载量；顶层 totalsAsOf 是累计截止日）
//
//  许可义务（原文条款，落在 AttributionFull / AttributionText 两个常量与这一段里）：
//    · 署名：dsh.so — DSH Developer Hub (https://www.dsh.so)
//    · CC BY 4.0、CORS 全开。
//    · **必须保留新鲜度标记（generatedAt / asOf / checkedAt），不得在缺原始时间戳时当作实时源转发**。
//      所以 GeneratedAt / SnapshotDate / CheckedAt / FetchedAt 这几个字段存在的全部意义，就是让看的人
//      知道"这是什么时候的数据"：缺原始时间戳时宁可空着，也绝不用本机时间冒充它、绝不写"实时"二字。
//
//  惰性设计：四个端点只在用户切到"生态趋势"那一屏时才抓（LoadAsync 由界面在切屏时调用），
//  不做开机预取、不做后台轮询 —— 对方的静态报文 1 小时才变一次，抓早了纯粹是白打人家的站点。
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// 生态趋势表格里的一行。四个榜共用这一个结构：字段名取并集，
/// 用不到的位置留默认值（例如星标榜不带 week/total/fromStars），
/// 这样界面侧只要一套列渲染逻辑，不必为四个榜各写一个模型。
/// 字段是公开字段而非属性：本项目的数据模型一律当"载荷结构"用，
/// 序列化时靠 IncludeFields 打开（见 EcosystemTrendsService.CacheJson）。
/// </summary>
internal sealed class TrendRow
{
    public int Rank;
    public string Id = "";
    public string Name = "";
    public long Stars;
    public long? Week;
    public long? Total;
    public long FromStars;
    public long ToStars;
    public long DeltaStars;
    public string PageUrl = "";   // 本程序自己拼的；id 非法时为空串
}

/// <summary>
/// 一整屏"生态趋势"的数据 + 它自己的新鲜度元信息。
/// 元信息不是装饰：许可见数据源许可条款，缺了时间戳就不许当实时源转发，
/// 所以这四段时间（GeneratedAt 源生成时间 / SnapshotDate 快照日 / FetchedAt 本地取到时间 /
/// CheckedAt 本地检查时间）必须一路带到界面上，界面组的"新鲜度"文案只认它们。
/// </summary>
internal sealed class EcosystemTrends
{
    public string GeneratedAt = "";
    public string SnapshotDate = "";
    public string BaselineDate = "";

    /// <summary>涨星榜的统计周期（源端 window.days / window.start / window.end）。
    /// 为什么单独存：涨星是"一段时间里的净增"，不给区间就没法解读那个 +47 是什么时候的增量。
    /// 读不到就留默认（0 / 空串），界面据此不显示这一行 —— 宁可不说，也不编一个区间。</summary>
    public int RisingWindowDays;
    public string RisingWindowStart = "";
    public string RisingWindowEnd = "";

    public string FetchedAt = "";     // 上次成功取到的时间 yyyy-MM-dd HH:mm
    public string CheckedAt = "";     // 上次"检查过"的时间（含 304 的"数据没变"）
    public string ETag = "";
    public bool Stale;                // 展示的是过期缓存
    public string Source = "";        // "缓存" / "dsh.so" / "缓存（已过期）"
    public string Error = "";         // 取不到时的人话原因
    public List<TrendRow> Rising = new();
    public List<TrendRow> Downloads = new();
    public List<TrendRow> Stars = new();

    /// <summary>总计下载榜（累计下载量，源站 npm-popular.json）。
    /// 与"本周下载"是**两个口径**：那个是近 7 天窗口，这个是历史累计（含基线初值 + 逐日累加）。
    /// 源端只收"有基线初值的包"（scope.packagesWithBaselineOnly）⇒ 最近才收录的包**没有累计数**，
    /// 那不是我们没取到，是这个数在源端就不存在（界面侧据此决定副行怎么显示）。</summary>
    public List<TrendRow> Popular = new();

    /// <summary>累计数据的截止日（源端 totalsAsOf，如 2026-09-24）。
    /// 累计量是"到某一天为止"的数，不写清截止日就把它当此刻的值会误导（它还在涨）。
    /// 读不到就留空串，界面据此不标日期 —— 宁可不说，也不编一个日子。</summary>
    public string TotalsAsOf = "";
}

/// <summary>
/// 1.4「生态趋势」的数据层：抓 dsh.so 的四份公开静态 JSON、解析成 TrendRow、落一份本地缓存。
///
/// 许可义务（数据源声明的原文条款，别改字）：
///   · 署名：dsh.so — DSH Developer Hub (https://www.dsh.so)  ← AttributionFull / AttributionText
///   · CC BY 4.0；**必须保留新鲜度标记，不得在缺原始时间戳时当作实时源转发**。
///     落法：GeneratedAt/SnapshotDate 只从报文里抄，抄不到就留空（宁可空，也不编）。
///
/// 铁律：**站点坐标全部是本文件里写死的常量，一个字节都不取远端报文里的 url 字段**。
/// 远端字段是不可信输入，拿它去拼链接/拼请求等于让数据源替我们决定点下去会去哪；
/// 详情页统一走 ArtifactUrl(id)（先过 IsSafeId 白名单，不过就是空串，行保留但不可点）。
///
/// 惰性：四个端点只在用户切到那一屏时才抓，不做开机预取、不做后台轮询。
/// 三级兜底（读缓存 → 条件请求 → 过期缓存）：断网时整屏显示上次的快照，绝不抛、绝不弹错。
/// </summary>
internal static class EcosystemTrendsService
{
    // ══ 常量与许可 ══

    internal const string SourceName = "dsh.so";
    internal const string AttributionText = "数据来源 dsh.so — DSH Developer Hub";

    // 数据源许可条款原文里的署名行。AttributionText 是给界面显示用的短句，
    // 这个是完整署名（含站点地址），将来做"关于/许可"页时直接用，别再手打一遍。
    internal const string AttributionFull = "dsh.so — DSH Developer Hub (https://www.dsh.so)";

    // 新鲜度阈值。取 1 小时是跟着对方的 CDN 缓存走的：报文本身 1 小时才可能变，
    // 比它更勤地抓只会拿到同一份字节，白白给对方加流量、白白让本程序多几次超时机会。
    internal static readonly TimeSpan Ttl = TimeSpan.FromHours(1);

    internal static string StarTrendUrl => "https://www.dsh.so/data/star-trend.json";
    internal static string DownloadsUrl => "https://www.dsh.so/data/npm-downloads.json";
    internal static string StarsUrl => "https://www.dsh.so/data/stars.json";
    // 总计下载榜（累计下载量）。与 npm-downloads.json 是**两个口径**：
    // 那份是"近 7 天窗口"，这份是"历史累计"（基线初值 + 逐日累加），所以是两个端点、两张榜，不是一个榜的两种写法。
    internal static string PopularUrl => "https://www.dsh.so/data/npm-popular.json";
    internal static string CachePathForTest => Path.Combine(GuardPaths.CacheDirMarket, "ecosystem.json");

    // 详情页坐标：写死的常量前缀，绝不从报文的 url 字段取。
    private const string ArtifactBase = "https://www.dsh.so/artifact/";

    // 四个端点各自的 ETag 键。ETag 是"每份资源一个"的：把一个端点的 ETag 拿去问另一个端点，
    // 必然对不上（对上了才是见鬼）。所以分开存、分开带。
    // ⚠ 新增端点**必须**配一个新键：PickETag/PutETag 是按 key 分的，键撞了就会把 A 的 ETag
    // 带去问 B —— 服务端认不出、永远回 200，等于第四个端点的条件请求彻底失效（每次全量下载）。
    private const string EtagKeyStarTrend = "star-trend";
    private const string EtagKeyDownloads = "npm-downloads";
    private const string EtagKeyStars = "stars";
    private const string EtagKeyPopular = "npm-popular";

    // 自己的 HttpClient（不共用别的类里的）：静态单例避免每次请求新建导致 socket 耗尽。
    // Timeout 12s：四个端点并发，站点 1 小时才变一次，12 秒还拿不到就该走兜底显示上次的快照。
    // UA 里带版本与用途（+ecosystem-trends）：对方是免费公开源，出问题时人家能一眼看出流量是谁打的。
    private static readonly HttpClient Http = CreateHttp();

    // 缓存序列化选项。IncludeFields 必须打开：本项目的数据模型用的是公开字段而不是属性，
    // System.Text.Json 默认只认属性，不开这个开关序列化出来就是一对空花括号 {}，
    // 缓存文件看着"写成功了"，下次启动读回来却什么也没有 —— 这个坑踩过一次，别再关掉。
    private static readonly JsonSerializerOptions CacheJson = new JsonSerializerOptions
    {
        IncludeFields = true,
        WriteIndented = true,
    };

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        // 用 TryAddWithoutValidation 而不是 Add：UA 里带 "(+ecosystem-trends)" 这种注释片段，
        // 走严格解析（ProductInfoHeaderValue）会被判格式不合法直接抛，这里只要原样发出去就行。
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "DSHGuard/1.4 (+ecosystem-trends)");
        return http;
    }

    // ══ 纯函数：id 白名单 / 详情页坐标 ══

    /// <summary>
    /// id 来自远端报文，是外部输入，只放行 A-Z a-z 0-9 . _ - @（@ 要放行，生态里的 id 确实会带它）。
    /// 其余一律拒绝：null、空串、纯空白、长度 > 100、含 / \ : ? # 空格 或任何白名单外的字符。
    /// 额外一条硬规矩：含 ".." 的一律拒绝（防路径穿越）—— 就算 ".." 全是白名单字符组成也不行，
    /// 因为 id 早晚会被拼进路径/文件名/URL，".." 的破坏力和它由什么字符组成无关。
    /// 白名单函数：失败必须等于拒绝，所以任何异常都按 false 处理（写法上其实没有可抛点，
    /// 包一层是为了将来有人往里加正则或解析时，这个契约不会被悄悄破坏）。
    /// </summary>
    internal static bool IsSafeId(string? id)
    {
        try
        {
            if (string.IsNullOrEmpty(id)) return false;   // null / 空串
            if (id.Length > 100) return false;            // 超长：正常的生态 id 都在 50 以内，长的多半是攻击载荷
            if (id.Contains("..", StringComparison.Ordinal)) return false;
            foreach (char c in id)
            {
                bool ok = (c >= 'A' && c <= 'Z')
                       || (c >= 'a' && c <= 'z')
                       || (c >= '0' && c <= '9')
                       || c == '.' || c == '_' || c == '-' || c == '@';
                if (!ok) return false;                    // 纯空白、空格、/ \ : ? # 以及任何其它字符都落在这里
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 详情页地址。id 不合法 ⇒ 空串（行保留但不可点）。
    /// ⚠ 绝不使用远端报文里的 url 字段：站点坐标全是写死的常量，不取远端字段 —— 这是本项目铁律。
    /// 报文里那个 url 只当"对方这么写"的参考，一旦被改指向别处，我们的界面也不该跟着走。
    /// </summary>
    internal static string ArtifactUrl(string id)
    {
        if (!IsSafeId(id)) return "";
        return $"{ArtifactBase}{id}/";
    }

    // ══ 纯函数：四个解析器 ══
    //
    // 统一口径：全程 try/catch，异常/脏数据一律退化成"少几行"，绝不抛给上层。
    // 理由：远端报文是不可信输入，源端改一次字段（甚至只是某一行脏了）就整屏白屏的话，
    // 用户看到的是"程序坏了"，而真相只是"那一栏暂时没数据"。宁可少一行，不可白一屏。

    /// <summary>
    /// 涨星榜。顶层字段：schema/generatedAt/source/snapshotDate/baselineDate/window/scope/limit/count/items/license。
    /// items[i]：rank, id, name, fromStars, toStars, deltaStars, url（url 故意不用）。
    /// </summary>
    internal static List<TrendRow> ParseStarTrend(string? json)
    {
        // 统计周期（window）只有界面要显示，公开签名不暴露它，所以这里直接丢掉新的三个 out。
        return ParseStarTrendDoc(json, out _, out _, out _, out _, out _, out _);
    }

    /// <summary>
    /// 本周下载榜。顶层：schema/generatedAt/source/snapshotDate/window/scope/limit/count/items/license。
    /// items[i]：rank, id, name, packageName, week, total, stars, url。⚠ total 可能为 null（源端没查到总数就写 null）。
    /// </summary>
    internal static List<TrendRow> ParseDownloads(string? json)
    {
        return ParseDownloadsDoc(json, out _);
    }

    /// <summary>
    /// 总计下载榜。顶层：schema/generatedAt/source/totalsAsOf/scope/limit/count/items/license。
    /// items[i]：rank, id, name, packageName, total, totalAsOf, week, stars, url（url 故意不用）。
    /// 与本周榜的区别只有"主数值读哪个字段"：这里主值是 total（累计），week 当副值。
    /// </summary>
    internal static List<TrendRow> ParsePopular(string? json)
    {
        // 顶层 totalsAsOf / snapshotDate 只有界面要显示，公开签名不暴露它们，所以这里丢掉两个 out
        // （与 ParseStarTrend 丢掉 window 那三个 out 是同一个写法：公开面只承诺"给我 JSON、还你行"）。
        return ParsePopularDoc(json, out _, out _);
    }

    /// <summary>
    /// 星标榜。顶层：schema/generatedAt/source/scope/limit/boards/license；
    /// boards 下有三个板 plugin / ecoPlugin / ecoApp，每板形如 { count, items[] }，
    /// items[i]：rank, id, name, stars, url。
    /// 本次**只解析 boards.plugin**：ecoPlugin / ecoApp 留在同一份报文里不上屏，
    /// 将来要加板子只改这个方法的取值路径（下面的 ParseItemArray 逐字段容错逻辑直接复用）。
    /// </summary>
    internal static List<TrendRow> ParseStars(string? json)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<TrendRow>();
            using JsonDocument doc = JsonDocument.Parse(json!);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new List<TrendRow>();   // 根不是对象：不是我们要的报文

            if (!root.TryGetProperty("boards", out JsonElement boards) || boards.ValueKind != JsonValueKind.Object)
                return new List<TrendRow>();
            if (!boards.TryGetProperty("plugin", out JsonElement board) || board.ValueKind != JsonValueKind.Object)
                return new List<TrendRow>();
            if (!board.TryGetProperty("items", out JsonElement items) || items.ValueKind != JsonValueKind.Array)
                return new List<TrendRow>();

            return ParseItemArray(items, RowFields.Stars);
        }
        catch { return new List<TrendRow>(); }
    }

    // 涨星榜还带顶层元信息（generatedAt/snapshotDate/baselineDate，以及统计周期 window），
    // 必须原样抄出来当新鲜度标记/口径说明；但对外暴露的解析函数签名只能是"给我 JSON、还你行"，
    // 所以元信息走 out 参数的私有版。
    // window 单独抄的理由：涨星是"一段时间里的净增"，只给 generatedAt 的话，
    // 界面上那个 +47 到底是"一天涨的"还是"一个月涨的"完全读不出来。
    private static List<TrendRow> ParseStarTrendDoc(string? json, out string generatedAt, out string snapshotDate, out string baselineDate,
                                                    out int windowDays, out string windowStart, out string windowEnd)
    {
        generatedAt = ""; snapshotDate = ""; baselineDate = "";
        windowDays = 0; windowStart = ""; windowEnd = "";
        try
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<TrendRow>();
            using JsonDocument doc = JsonDocument.Parse(json!);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new List<TrendRow>();

            generatedAt = PropString(root, "generatedAt");
            snapshotDate = PropString(root, "snapshotDate");
            baselineDate = PropString(root, "baselineDate");

            // 统计周期在 window 对象里：{ days, start, end }。整块缺失/类型不对就保持上面的默认值，
            // 界面据此不显示这一行 —— 宁可不说，也不编一个区间（编出来的区间会让 +47 变成假消息）。
            if (root.TryGetProperty("window", out JsonElement win) && win.ValueKind == JsonValueKind.Object)
            {
                // days 是 JSON Number，所以走 TryGetInt32 而不是 PropString：字符串化的 "8" 在这里不算数，
                // 免得把源端某个同名的文本字段误当天数。非正数一律归一成 0（0 的含义就是"没有可用天数"）。
                if (win.TryGetProperty("days", out JsonElement daysEl)
                    && daysEl.ValueKind == JsonValueKind.Number
                    && daysEl.TryGetInt32(out int d)
                    && d > 0)
                {
                    windowDays = d;
                }
                windowStart = PropString(win, "start");
                windowEnd = PropString(win, "end");
            }

            if (!root.TryGetProperty("items", out JsonElement items) || items.ValueKind != JsonValueKind.Array)
                return new List<TrendRow>();
            return ParseItemArray(items, RowFields.Rising);
        }
        catch { return new List<TrendRow>(); }
    }

    private static List<TrendRow> ParseDownloadsDoc(string? json, out string snapshotDate)
    {
        snapshotDate = "";
        try
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<TrendRow>();
            using JsonDocument doc = JsonDocument.Parse(json!);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new List<TrendRow>();

            snapshotDate = PropString(root, "snapshotDate");

            if (!root.TryGetProperty("items", out JsonElement items) || items.ValueKind != JsonValueKind.Array)
                return new List<TrendRow>();
            return ParseItemArray(items, RowFields.Downloads);
        }
        catch { return new List<TrendRow>(); }
    }

    /// <summary>
    /// 总计下载榜的 Doc 版。比 ParseDownloadsDoc 多抄一个顶层字段 totalsAsOf（累计截止日）：
    /// 累计量是"到某一天为止"的数，还在涨，不把截止日带到界面上就等于把一个会变的数说成此刻的值。
    /// snapshotDate 也照读：这份报文实测**没有**这个字段（读不到自然是空串），
    /// 但保持与另两个 Doc 版同构，源端哪天补上就能自动接住，不必回来改签名。
    /// </summary>
    private static List<TrendRow> ParsePopularDoc(string? json, out string totalsAsOf, out string snapshotDate)
    {
        totalsAsOf = "";
        snapshotDate = "";
        try
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<TrendRow>();
            using JsonDocument doc = JsonDocument.Parse(json!);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new List<TrendRow>();

            totalsAsOf = PropString(root, "totalsAsOf");
            snapshotDate = PropString(root, "snapshotDate");

            // items 缺失/类型不对 ⇒ 空表（调用方按"200 但解析为空 = 报文结构变了"处理，不当成"这个榜没数据"）。
            if (!root.TryGetProperty("items", out JsonElement items) || items.ValueKind != JsonValueKind.Array)
                return new List<TrendRow>();
            return ParseItemArray(items, RowFields.Popular);
        }
        catch { return new List<TrendRow>(); }
    }

    // 四个榜的 items 逐字段容错逻辑完全一样，只有"读哪几个数值字段"不同，所以用 kind 分开。
    private enum RowFields { Rising, Downloads, Stars, Popular }

    private static List<TrendRow> ParseItemArray(JsonElement items, RowFields fields)
    {
        var list = new List<TrendRow>();
        int seq = 0;
        foreach (JsonElement el in items.EnumerateArray())
        {
            seq++;
            if (el.ValueKind != JsonValueKind.Object) continue;   // 单行不是对象：跳这一行，不因为一行脏数据丢掉整榜

            string id = PropString(el, "id");
            string name = PropString(el, "name");
            if (name.Length == 0) name = id;                      // name 空 ⇒ 用 id 兜底，界面上总得有个字
            if (id.Length == 0 && name.Length == 0) continue;      // id 与 name 都空 ⇒ 这行没有任何可用信息，丢弃

            var row = new TrendRow { Id = id, Name = name };

            // rank 缺失/非法（含 <=0 这种明显不是排名的值）⇒ 用该行在数组里的序号（1 开始）补。
            // 源端偶尔漏 rank，而表格列不能因为一个字段空就整列错位。
            if (TryNumProp(el, "rank", out long rk) && rk >= 1 && rk <= int.MaxValue) row.Rank = (int)rk;
            else row.Rank = seq;

            if (fields == RowFields.Rising)
            {
                // 数值字段容错：缺失或类型不对一律当 0（week/total 那种"真可能没有"的用 null）。
                TryNumProp(el, "fromStars", out row.FromStars);
                TryNumProp(el, "toStars", out row.ToStars);

                // deltaStars：优先用报文值；缺失、或者是 0 但 to != from（源端可能只更新了 toStars 而把差值留成 0），
                // 就兜底算 ToStars - FromStars。理由：源端改字段时，"涨了多少"这一列不能整榜空白 ——
                // 两个端点在手里时，差值就是同一份快照里的事实，自己算不会引入新错误。
                if (!TryNumProp(el, "deltaStars", out long delta) || (delta == 0 && row.ToStars != row.FromStars))
                    delta = row.ToStars - row.FromStars;
                row.DeltaStars = delta;
                // 涨星榜的 items 里没有 stars 字段，所以这里不读它：Stars 保持 0，
                // 免得将来有人看到 0 以为是"这个仓库没人星标"。
            }
            else if (fields == RowFields.Downloads)
            {
                // week/total 用可空版：JSON 里 total 实测出现过 null，硬当 0 会在表格里显示成"0 次下载"，
                // 而真相是"源端没拿到这个数" —— 空着比编一个 0 诚实。
                TryNumNullableProp(el, "week", out long? wk);
                row.Week = wk;
                TryNumNullableProp(el, "total", out long? tt);
                row.Total = tt;
                TryNumProp(el, "stars", out row.Stars);
            }
            else if (fields == RowFields.Popular)
            {
                // 总计下载榜的主值是 total（累计），week 反过来当副值 —— 与本周榜只有"主副互换"的差别。
                // total 走**可空版**：实测这份报文里 100 条没有 null，但同一站点同一口径随时可能变
                // （本周榜那份实测就有 35 条 null）；硬当 0 会在表格里显示成"0 次下载"，
                // 而真相是"源端没拿到" —— 空着比编一个 0 诚实。
                TryNumNullableProp(el, "total", out long? tt);
                row.Total = tt;
                TryNumNullableProp(el, "week", out long? wk);
                row.Week = wk;
                TryNumProp(el, "stars", out row.Stars);
            }
            else
            {
                TryNumProp(el, "stars", out row.Stars);
            }

            // 坐标自己拼，不用报文里的 url；id 非法 ⇒ 空串，行照留但不可点（宁可不可点，不可点错地方）。
            row.PageUrl = ArtifactUrl(id);

            list.Add(row);
        }
        return list;
    }

    // ══ 纯函数：新鲜度 ══

    /// <summary>
    /// checkedAt 解析不了或为空 ⇒ false（宁可当成过期去抓一次，也不要拿一个读不懂的时间戳当"新鲜"）；
    /// 否则 now - checkedAt &lt;= ttl ⇒ true。
    /// 时间语义统一按本地：解析出来的 ISO（UTC 或带偏移）先 ToLocalTime，
    /// 由调用方传 DateTime.Now 进来比，避免"看起来是 8 小时前"这种跨时区误判。
    /// </summary>
    internal static bool IsFresh(string? checkedAt, DateTime now, TimeSpan ttl)
    {
        if (!TryParseWhen(checkedAt, out DateTime when)) return false;
        // 本机时钟回拨、或服务端时间戳在未来时差值为负，直接判"新鲜"而不是"过期"：
        // 未来时间戳当过期会导致每次切屏都重抓，反而更吵。
        return (now - when) <= ttl;
    }

    /// <summary>
    /// 新鲜度文案：**不含"实时"二字**（许可见数据源条款：缺原始时间戳不得当实时源转发，
    /// 所以我们既不说"实时"，也不在只有本地检查时间时假装知道数据是什么时候生成的）。
    /// 五档：都有 → "数据快照 X · 刚刚检查 / N 分钟前检查 / N 小时前检查 / N 天前检查"；
    /// 只有快照日 → "数据快照 X"；只有检查时间 → 同上（不带"数据快照"）；都空 → ""。
    /// 分钟/小时/天一律向下取整（3.9 小时 ⇒ 3 小时）：宁可说少，不可说多。
    ///
    /// 为什么必须有"分钟"这一档（出图实测踩到）：一小时以内曾被截成"0 小时前检查" ——
    /// 界面上写「0 小时前」看着就像程序算错了，而且它和"刚刚"表达的是同一件事却更难懂。
    /// 现在 1–59 分钟如实写「N 分钟前」，1 分钟以内才写「刚刚」。
    /// </summary>
    internal static string FreshnessText(string? snapshotDate, string? checkedAt, DateTime now)
    {
        string snap = (snapshotDate ?? "").Trim();
        bool hasChecked = TryParseWhen(checkedAt, out DateTime when);

        // 都空 ⇒ 空串。绝不编一个"刚刚"出来：没有时间戳就是没有，
        // 编出来的"刚刚"会让人以为数据是新的，这正是许可条款里点名不许做的事。
        if (snap.Length == 0 && !hasChecked) return "";

        string tail = "";
        if (hasChecked)
        {
            TimeSpan age = now - when;
            if (age < TimeSpan.Zero) age = TimeSpan.Zero;   // 时钟回拨：显示成"刚刚"总好过显示负数小时
            if (age <= TimeSpan.FromMinutes(1)) tail = "刚刚检查";
            else if (age < TimeSpan.FromHours(1)) tail = $"{(int)age.TotalMinutes} 分钟前检查";
            else if (age < TimeSpan.FromHours(24)) tail = $"{(int)age.TotalHours} 小时前检查";   // (int) 截断 = 向下取整
            else tail = $"{(int)age.TotalDays} 天前检查";
        }

        if (snap.Length == 0) return tail;                   // snapshotDate 空 ⇒ 只输出检查时间，不拼"数据快照 ·"这种半截文案
        if (tail.Length == 0) return $"数据快照 {snap}";      // 检查时间拿不到 ⇒ 只输出快照日，同样不拼半截
        return $"数据快照 {snap} · {tail}";
    }

    /// <summary>
    /// 涨星榜的"统计周期"文案。存在的理由：涨星是**区间内的净增**，没有区间的话
    /// 界面上那个 "+47" 就是一句无法解读的数字 —— 不知道它是 8 天涨的还是 8 个月涨的。
    ///
    /// 文案口径（2.1.0 改）：源端这个区间是**滚动的近一周**，随新快照每天整段前移
    /// （09-18→09-26 → 09-26→10-04 → …）。原先写成"统计周期 A → B · 8 天"，两个裸日期
    /// 并排，第一眼会被读成"这个榜从 A 那天开始算"，于是每次刷新都像没动过。
    /// 现在**先说"近 N 天"、再说具体日期**：读者先拿到"这是近一周"这个结论。
    ///
    /// 七档（逐条对应源端 window 的三字段组合）：
    ///   days>0 且 start/end 都有 → "统计周期 近 8 天（2026-09-18 → 2026-09-26）"
    ///   start/end 都有、days<=0   → "统计周期 2026-09-18 → 2026-09-26"
    ///   只有 start（days>0）      → "统计周期 近 8 天（2026-09-18 起）"
    ///   只有 start（days<=0）     → "统计周期 2026-09-18 起"
    ///   只有 end                  → "统计周期 截至 2026-09-26"
    ///   两个都空、days>0          → "统计周期 近 8 天"
    ///   两个都空、days<=0         → ""（空串：什么都不知道时界面就不显示这一行，绝不编区间）
    ///
    /// 日期**只做字符串裁剪与拼接，不解析成 DateTime**：源端给的就是 yyyy-MM-dd，
    /// 解析再格式化会把"源端说的日期"变成"本机区域设置下的日期"（时区/历法/格式都可能变），
    /// 而这里的字符串本身就是要原样展示给用户看的，没有一处需要把它当时间运算。
    /// 纯函数、永不抛：null/空白一律当空串（Trim 后判空，纯空白不当成有效日期）。
    /// </summary>
    internal static string WindowText(int days, string? start, string? end)
    {
        string s = (start ?? "").Trim();
        string e = (end ?? "").Trim();
        // 只有 days>0 才敢写"近 N 天"：0/负数意味着"源端没给天数"，那就一个字都不提。
        string near = days > 0 ? $"近 {days} 天" : "";

        // 天数在前、日期在后（2.1.0 改）。源端这个区间是**滚动的近一周**，随新快照整段前移；
        // 原先写成"统计周期 A → B · 8 天"，两个裸日期并排，第一眼会被读成"这个榜从 A 那天算起"，
        // 于是每天刷新看着都像没动。先给"近一周"这个结论、日期只作佐证，读法就对了。
        if (s.Length > 0 && e.Length > 0)
            return near.Length > 0 ? $"统计周期 {near}（{s} → {e}）" : $"统计周期 {s} → {e}";
        if (s.Length > 0)
            return near.Length > 0 ? $"统计周期 {near}（{s} 起）" : $"统计周期 {s} 起";
        if (e.Length > 0) return $"统计周期 截至 {e}";   // 只有结束日：不写"起"，也不编起始日
        if (near.Length > 0) return $"统计周期 {near}";    // 只有天数：说"近 N 天"而不是"从 X 到 Y"
        return "";                                        // 三者全无：空串，界面不显示这一行
    }

    // ══ 抓取：三级兜底 ══

    /// <summary>
    /// 三级兜底：① 缓存新鲜且非 force ⇒ 直接用缓存；② 并发条件请求四个端点；
    /// ③ 任一失败 ⇒ 有缓存就给"过期缓存 + 人话原因"，没缓存就给空对象 + 人话原因。
    /// 本方法对界面承诺**永不抛**：断网是常态不是异常，界面不该为它写 try/catch。
    /// </summary>
    internal static async Task<EcosystemTrends> LoadAsync(bool force = false)
    {
        EcosystemTrends? cached = null;
        try
        {
            DateTime now = DateTime.Now;
            cached = ReadCache();

            // ① 常态路径：来回切屏只会读一次盘，不发请求。
            if (!force && cached != null && IsFresh(cached.CheckedAt, now, Ttl))
            {
                cached.Source = "缓存";
                cached.Stale = false;
                cached.Error = "";
                return cached;
            }

            // ② 条件请求。304 的"数据没变"在 FetchAsync 里已经并进结果对象了。
            EcosystemTrends? fresh = await FetchAsync(cached).ConfigureAwait(false);
            if (fresh != null) return fresh;

            // ③ 兜底：显示上次的快照，并明确告诉用户这是旧的。
            return Fallback(cached);
        }
        catch (Exception ex)
        {
            // 能走到这里的只有本程序自己的 bug（网络问题在 FetchAsync 里已按 [WARN] 落过诊断），
            // 所以这里用 LogError；对外仍然按"取不到"给一个能显示的兜底对象，绝不把异常扔给界面。
            Logger.LogError("EcosystemTrends.LoadAsync", ex);
            return Fallback(cached);
        }
    }

    /// <summary>
    /// 让下次 LoadAsync 重新读盘。本实现**故意不保留内存缓存**：缓存文件就是唯一真相，
    /// 每次调用都直接读盘（切屏频率远低于磁盘读取的代价，换来的是"多开/改盘即生效、不会读到一个进程内的旧影子"）。
    /// 所以这里没有可清的内存状态；保留空实现是为了让自检/测试的调用面稳定 ——
    /// 将来真加了内存缓存，把字段置空写在这里即可，调用点不用改。
    /// </summary>
    internal static void ResetCacheForTest()
    {
    }

    // 空对象或"过期缓存"两条兜底路径集中在这里，免得外面三处各写一遍文案（文案不一致是这个项目踩过的坑）。
    private static EcosystemTrends Fallback(EcosystemTrends? cached)
    {
        if (cached == null)
            return new EcosystemTrends { Source = "", Error = "暂时取不到（可能离线）" };

        cached.Stale = true;
        cached.Source = "缓存（已过期）";
        cached.Error = "暂时取不到（可能离线），显示的是上次取到的数据";
        // CheckedAt 故意不动：它记的是"上次真的检查成功过"的时刻。
        // 失败时把它刷成 now，新鲜度文案就会变成"刚刚检查"——那是在骗人，而且骗的正是许可条款要求我们如实标注的东西。
        return cached;
    }

    // 条件抓取。返回 null = 本次失败（调用方走兜底）；返回对象 = 成功（可能全是 304 的"数据没变"）。
    private static async Task<EcosystemTrends?> FetchAsync(EcosystemTrends? cached)
    {
        var requests = new List<HttpRequestMessage>();
        var responses = new List<HttpResponseMessage>();
        try
        {
            // 四个端点各自带自己的 If-None-Match。没有缓存/没有 ETag 时不带这个头，服务端自然回 200。
            Task<HttpResponseMessage> tStar = SendConditionalAsync(StarTrendUrl, PickETag(cached?.ETag, EtagKeyStarTrend), requests);
            Task<HttpResponseMessage> tDl = SendConditionalAsync(DownloadsUrl, PickETag(cached?.ETag, EtagKeyDownloads), requests);
            Task<HttpResponseMessage> tStars = SendConditionalAsync(StarsUrl, PickETag(cached?.ETag, EtagKeyStars), requests);
            Task<HttpResponseMessage> tPop = SendConditionalAsync(PopularUrl, PickETag(cached?.ETag, EtagKeyPopular), requests);

            HttpResponseMessage[] r;
            try
            {
                // 并发：串行的话最坏 4×12s，切屏会卡得像没联网。
                r = await Task.WhenAll(tStar, tDl, tStars, tPop).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 任一失败 ⇒ 整屏走兜底。宁可整屏显示上次的快照，也不要四块数据时新时旧拼一屏：
                // 拼出来的那一屏看起来"有数据"，却最容易让人误读成"现在的生态长这样"。
                // 断网/超时是环境问题不是本次运行的异常，所以只落 [WARN] 诊断，不落 LogError。
                Logger.NoteDiagnosis($"生态趋势抓取失败（按离线口径处理）: {ex.Message}");
                return null;
            }
            responses.AddRange(r);

            foreach (HttpResponseMessage resp in r)
            {
                if (resp.StatusCode == HttpStatusCode.OK || resp.StatusCode == HttpStatusCode.NotModified) continue;
                Logger.NoteDiagnosis($"生态趋势端点返回 {(int)resp.StatusCode}，本次显示上次的快照");
                return null;
            }

            DateTime now = DateTime.Now;
            bool okStar = r[0].StatusCode == HttpStatusCode.OK;
            bool okDl = r[1].StatusCode == HttpStatusCode.OK;
            bool okStars = r[2].StatusCode == HttpStatusCode.OK;
            bool okPop = r[3].StatusCode == HttpStatusCode.OK;
            bool anyNew = okStar || okDl || okStars || okPop;

            // 全 304 却没有缓存：理论上不会发生（没缓存就不带 If-None-Match，服务端不会回 304）。
            // 真发生了说明盘上的缓存和 ETag 对不上，按失败处理 —— 别把"没见过的新数据"和"空表"混为一谈。
            if (!anyNew && cached == null)
            {
                Logger.NoteDiagnosis("生态趋势四个端点全部 304，但本地没有可用缓存，按取不到处理");
                return null;
            }

            var data = new EcosystemTrends();

            // 元信息先继承缓存：304 的那几路要原样保留，200 的那几路再覆盖。
            string generatedAt = cached?.GeneratedAt ?? "";
            string snapshotDate = cached?.SnapshotDate ?? "";
            string baselineDate = cached?.BaselineDate ?? "";
            // 统计周期同理继承缓存：只有涨星榜 200 时才可能刷新它（window 只存在于 star-trend 报文里）。
            int risingDays = cached?.RisingWindowDays ?? 0;
            string risingStart = cached?.RisingWindowStart ?? "";
            string risingEnd = cached?.RisingWindowEnd ?? "";
            // 累计截止日同理继承缓存：只有总计下载榜 200 时才可能刷新它（totalsAsOf 只存在于那份报文里）。
            // 源端哪天不给这个字段了，也要保留旧值，绝不拿本机日期去顶替。
            string totalsAsOf = cached?.TotalsAsOf ?? "";
            string etag = cached?.ETag ?? "";

            if (okStar)
            {
                string body = await r[0].Content.ReadAsStringAsync().ConfigureAwait(false);
                List<TrendRow> rows = ParseStarTrendDoc(body, out string g, out string s, out string b,
                                                       out int wd, out string ws, out string we);
                // 200 但解析出来是空表：报文结构变了（或给了个错误页），这不是"这个榜没数据"能解释的。
                // 按失败走兜底，显示上次的快照；这一步不加 LogError —— 只是数据源变了，不是本程序坏了。
                if (rows.Count == 0)
                {
                    Logger.NoteDiagnosis("涨星榜返回 200 但解析为空，本次显示上次的快照");
                    return null;
                }
                data.Rising = rows;
                // 抄不到就留原先的值（可能是缓存里的），绝不拿本机时间去顶替源端的 generatedAt。
                if (g.Length > 0) generatedAt = g;
                if (s.Length > 0) snapshotDate = s;
                if (b.Length > 0) baselineDate = b;
                // 统计周期同一口径：源端这一路没给（或给了空/非正数）就保留缓存里的旧值，
                // 绝不拿"本机现在"去凑一个区间出来。
                if (wd > 0) risingDays = wd;
                if (ws.Length > 0) risingStart = ws;
                if (we.Length > 0) risingEnd = we;
                etag = PutETag(etag, EtagKeyStarTrend, HeadETag(r[0]));
            }
            else
            {
                data.Rising = cached?.Rising ?? new List<TrendRow>();
            }

            if (okDl)
            {
                string body = await r[1].Content.ReadAsStringAsync().ConfigureAwait(false);
                List<TrendRow> rows = ParseDownloadsDoc(body, out string snap);
                if (rows.Count == 0)
                {
                    Logger.NoteDiagnosis("下载榜返回 200 但解析为空，本次显示上次的快照");
                    return null;
                }
                data.Downloads = rows;
                // 涨星榜没给 snapshotDate 时，用下载榜的补上（两份报文是同一次生成的，日期同源，不算编造）。
                if (snapshotDate.Length == 0 && snap.Length > 0) snapshotDate = snap;
                etag = PutETag(etag, EtagKeyDownloads, HeadETag(r[1]));
            }
            else
            {
                data.Downloads = cached?.Downloads ?? new List<TrendRow>();
            }

            if (okStars)
            {
                string body = await r[2].Content.ReadAsStringAsync().ConfigureAwait(false);
                List<TrendRow> rows = ParseStars(body);
                if (rows.Count == 0)
                {
                    Logger.NoteDiagnosis("星标榜返回 200 但解析为空，本次显示上次的快照");
                    return null;
                }
                data.Stars = rows;
                etag = PutETag(etag, EtagKeyStars, HeadETag(r[2]));
            }
            else
            {
                data.Stars = cached?.Stars ?? new List<TrendRow>();
            }

            if (okPop)
            {
                string body = await r[3].Content.ReadAsStringAsync().ConfigureAwait(false);
                List<TrendRow> rows = ParsePopularDoc(body, out string totals, out string snap2);
                // 与另两榜同口径：200 却解析出空表，说明报文结构变了（或给了个错误页），
                // 这不是"这个榜没数据"能解释的，按失败走兜底显示上次的快照。
                if (rows.Count == 0)
                {
                    Logger.NoteDiagnosis("总计下载榜返回 200 但解析为空，本次显示上次的快照");
                    return null;
                }
                data.Popular = rows;
                // 累计截止日抄不到就留原先的值（可能是缓存里的），绝不拿本机日期去顶替源端的 totalsAsOf。
                if (totals.Length > 0) totalsAsOf = totals;
                // 这份报文实测没有 snapshotDate（snap2 通常为空串）；源端哪天补上就顺手补进总快照日，
                // 与下载榜那路的兜底同一个道理：两份报文同源，日期不算编造。
                if (snapshotDate.Length == 0 && snap2.Length > 0) snapshotDate = snap2;
                etag = PutETag(etag, EtagKeyPopular, HeadETag(r[3]));
            }
            else
            {
                data.Popular = cached?.Popular ?? new List<TrendRow>();
            }

            data.GeneratedAt = generatedAt;
            data.SnapshotDate = snapshotDate;
            data.BaselineDate = baselineDate;
            // 三个字段都是 public：缓存序列化开了 IncludeFields（见 CacheJson），公开字段会被写进
            // ecosystem.json 并在下次 ReadCache 时原样读回，所以这里写一次就够，不必再额外持久化。
            data.RisingWindowDays = risingDays;
            data.RisingWindowStart = risingStart;
            data.RisingWindowEnd = risingEnd;
            // 累计截止日：与上面几个字符串同一口径 —— 抄不到就留缓存里的旧值，绝不编一个日子。
            data.TotalsAsOf = totalsAsOf;
            data.ETag = etag;
            data.CheckedAt = Stamp(now);
            // FetchedAt 只在"真的取到新数据"时刷新。全 304 说明数据一个字节都没变，
            // 把它刷成现在的话，"上次取到的时间"就变成了一个假消息（复述了旧数据却说刚取到）。
            data.FetchedAt = anyNew ? Stamp(now) : (cached?.FetchedAt ?? "");
            data.Stale = false;
            data.Error = "";
            data.Source = anyNew ? "dsh.so" : "缓存";

            // 304 也要写回：否则下次启动 CheckedAt 还是旧的，会立刻再抓一遍 —— 白白打站点。
            WriteCache(data);
            return data;
        }
        finally
        {
            // 请求/响应对象要在正文读完之后才能释放，所以统一在这里收尾
            // （写在 try 里面任一 return 之前容易漏，漏一个就是一次连接泄漏）。
            foreach (HttpResponseMessage resp in responses) resp.Dispose();
            foreach (HttpRequestMessage req in requests) req.Dispose();
        }
    }

    // 条件请求必须走 HttpRequestMessage + HttpResponseMessage：我们要读状态码 304 和响应头里的 ETag，
    // GetStringAsync 那种便捷方法拿不到这两样东西（拿不到就只能每次全量下载，白费对方的带宽）。
    private static Task<HttpResponseMessage> SendConditionalAsync(string url, string etag, List<HttpRequestMessage> keep)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (etag.Length > 0)
        {
            // ETag 从响应头读的是 resp.Headers.ETag.Tag（**不带** 弱校验的 "W/" 前缀，那是 IsWeak 标志位，
            // 不属于 Tag 本身）。这里原样放回 If-None-Match 即可：弱/强比较是服务端的事，
            // 我们自己拼 "W/" 反而可能拼出服务端认不出的值、导致 304 永远命不中。
            req.Headers.TryAddWithoutValidation("If-None-Match", etag);
        }
        keep.Add(req);   // 交给调用方统一释放：请求对象要活到响应正文读完
        return Http.SendAsync(req, HttpCompletionOption.ResponseContentRead);
    }

    private static string HeadETag(HttpResponseMessage resp)
    {
        try { return resp.Headers.ETag?.Tag ?? ""; }
        catch { return ""; }
    }

    // ══ ETag 的三路存取 ══
    // 数据模型上只暴露一个 ETag 字段（界面把它当不透明的"缓存版本号"看，不解析），
    // 但内部要装四个端点的 ETag，所以这里用若干行 "key=tag" 的文本装在一起。
    // 用 '\n' 当分隔符是安全的：HTTP 的 etagc 不允许控制字符，标签里不可能出现换行。
    private static string PickETag(string? blob, string key)
    {
        if (string.IsNullOrEmpty(blob)) return "";
        foreach (string line in blob!.Split('\n'))
        {
            int i = line.IndexOf('=');
            if (i > 0 && line.AsSpan(0, i).SequenceEqual(key.AsSpan())) return line[(i + 1)..];
        }
        return "";
    }

    private static string PutETag(string? blob, string key, string tag)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(blob))
        {
            foreach (string line in blob!.Split('\n'))
            {
                int i = line.IndexOf('=');
                if (i <= 0) continue;
                if (line.AsSpan(0, i).SequenceEqual(key.AsSpan())) continue;   // 同键的旧值丢掉，换新的
                parts.Add(line);
            }
        }
        if (tag.Length > 0) parts.Add(key + "=" + tag);   // 服务端没给 ETag 时不写空键，免得下次带个空串去问
        return string.Join("\n", parts);
    }

    // ══ 缓存读写 ══

    // 读缓存失败/损坏一律当作"无缓存"（返回 null）：一份坏掉的缓存文件不该让整屏生态趋势消失，
    // 更不该抛给上层 —— 上层拿到 null 就会去抓网络，这恰好是正确行为。
    private static EcosystemTrends? ReadCache()
    {
        try
        {
            string path = CachePathForTest;
            if (!File.Exists(path)) return null;
            string json = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json)) return null;

            EcosystemTrends? data = JsonSerializer.Deserialize<EcosystemTrends>(json, CacheJson);
            if (data == null) return null;

            // 缓存文件是人也能改的：JSON 里写了 "GeneratedAt": null、或者缺字段，反序列化就会把
            // 这些非空字符串字段变成 null，一路带到界面上就是一串 NullReferenceException。
            // 所以在唯一的入口处一律补成空串/空表 —— 下游所有代码就可以当它们是"永远不为 null"。
            data.GeneratedAt ??= "";
            data.SnapshotDate ??= "";
            data.BaselineDate ??= "";
            // 新增的统计周期两个字符串同样要补：手工改过的缓存里写成 null 的话，
            // 界面上那句"统计周期 {start} → {end}"就会拼出 "→" 这种半截文案。
            data.RisingWindowStart ??= "";
            data.RisingWindowEnd ??= "";
            // 新增的累计截止日同样要补：手工改过的缓存里写成 null 的话，
            // 界面上那句"累计截至 {日期}"就会拼出半截文案（甚至直接 NRE）。
            data.TotalsAsOf ??= "";
            data.FetchedAt ??= "";
            data.CheckedAt ??= "";
            data.ETag ??= "";
            data.Source ??= "";
            data.Error ??= "";
            data.Rising ??= new List<TrendRow>();
            data.Downloads ??= new List<TrendRow>();
            data.Stars ??= new List<TrendRow>();
            data.Popular ??= new List<TrendRow>();
            return data;
        }
        catch (Exception ex)
        {
            Logger.NoteDiagnosis($"生态趋势缓存读取失败（按无缓存处理）: {ex.Message}");
            return null;
        }
    }

    private static void WriteCache(EcosystemTrends data)
    {
        try
        {
            Directory.CreateDirectory(GuardPaths.CacheDirMarket);
            // UTF-8 无 BOM：本项目的文本文件一律无 BOM，带 BOM 的话别的工具按字节比对会认为文件变了。
            File.WriteAllText(CachePathForTest, JsonSerializer.Serialize(data, CacheJson), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            // 写盘失败只是"这次没缓存"，数据还在内存里、界面照常显示，所以是 [WARN] 诊断级、不抛。
            Logger.NoteDiagnosis($"生态趋势缓存写入失败: {ex.Message}");
        }
    }

    // ══ 取值助手：逐字段容错 ══

    private static string Stamp(DateTime now) => now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    // 取字符串字段。除了 String 也认 Number（源端偶尔把 id/name 写成数字），
    // 拿不到就返回空串 —— 调用方自己决定"空了怎么办"（name 空用 id 兜底等）。
    private static string PropString(JsonElement obj, string name)
    {
        try
        {
            if (obj.ValueKind != JsonValueKind.Object) return "";
            if (!obj.TryGetProperty(name, out JsonElement v)) return "";
            if (v.ValueKind == JsonValueKind.String) return v.GetString() ?? "";
            if (v.ValueKind == JsonValueKind.Number) return v.GetRawText();
            return "";
        }
        catch { return ""; }
    }

    /// <summary>
    /// 数值容错：同时接受 Number 与 String（源端出现过把数字写成 "4716" 的形态）。
    /// 两种都不认就返回 false，调用方按"缺失"处理（数字当 0、week/total 当 null）—— 绝不抛。
    /// </summary>
    private static bool TryNum(JsonElement e, out long v)
    {
        v = 0;
        try
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Number:
                    if (e.TryGetInt64(out long n)) { v = n; return true; }
                    // 小数/超范围：四舍五入到 long。星数、下载数不该有小数，真出现就当它想表达这个整数。
                    if (e.TryGetDouble(out double d) && !double.IsNaN(d) && !double.IsInfinity(d)) { v = (long)Math.Round(d); return true; }
                    return false;
                case JsonValueKind.String:
                    return long.TryParse(e.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
                default:
                    return false;
            }
        }
        catch { return false; }
    }

    /// <summary>
    /// 可空数值：JSON 里是 null、或类型不对读不出来，都算"没有这个数"（返回 true 但 v = null 也算不上，
    /// 所以这里用返回值表示"读到了值"，v 为 null 表示"读到了 null"）。
    /// week/total 用它的理由：下载榜的 total 实测出现过 null，硬当 0 会在界面上显示成"0 次下载"。
    /// </summary>
    private static bool TryNumNullable(JsonElement e, out long? v)
    {
        v = null;
        try
        {
            if (e.ValueKind == JsonValueKind.Null || e.ValueKind == JsonValueKind.Undefined) return false;
            if (!TryNum(e, out long n)) return false;
            v = n;
            return true;
        }
        catch { return false; }
    }

    private static bool TryNumProp(JsonElement obj, string name, out long v)
    {
        v = 0;
        try
        {
            if (obj.ValueKind != JsonValueKind.Object) return false;
            if (!obj.TryGetProperty(name, out JsonElement el)) return false;
            return TryNum(el, out v);
        }
        catch { return false; }
    }

    private static bool TryNumNullableProp(JsonElement obj, string name, out long? v)
    {
        v = null;
        try
        {
            if (obj.ValueKind != JsonValueKind.Object) return false;
            if (!obj.TryGetProperty(name, out JsonElement el)) return false;
            return TryNumNullable(el, out v);
        }
        catch { return false; }
    }

    // 时间戳解析：先认本程序自己写的 "yyyy-MM-dd HH:mm"（缓存里的 CheckedAt/FetchedAt 就是这个形态），
    // 再认 ISO（"2026-09-28T07:02:02.485Z" 或带偏移）。
    // ISO 那支按 UTC 解析后 ToLocalTime：源端给的是 UTC，而本程序全线按本机时间显示，
    // 不转换的话东八区会看到"8 小时前检查"这种明显不对的文案。
    private static bool TryParseWhen(string? s, out DateTime when)
    {
        when = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        string t = s!.Trim();

        if (DateTime.TryParseExact(t, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out when))
            return true;

        if (DateTime.TryParse(t, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime u))
        {
            when = u.ToLocalTime();
            return true;
        }
        return false;
    }
}
