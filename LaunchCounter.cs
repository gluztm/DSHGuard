using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DSHGuard;

/// <summary>
/// 盘上的账目形状（纯数据）：只有一个 <c>count</c> 字段。
/// 照 <see cref="PluginTimeTable"/> 的样子做成**命名的顶层 internal 类**，而不是
/// <see cref="LaunchCounter"/> 里的私有嵌套类 —— 序列化要能拿到这个类型（反射构造它），
/// 嵌套在私有作用域里会让"读回来的路"多一处与可见性有关的隐患，而这里没有任何隐藏它的理由。
/// 用 <see cref="JsonPropertyNameAttribute"/> 写死小写键名：这是本程序自己的格式，
/// 不跟任何外部接口约定走（将来改属性名也不该悄悄改掉盘上格式）。
/// </summary>
internal sealed class LaunchCountFile
{
    /// <summary>
    /// 累计打开次数（本程序自己写的值恒 ≥ 1；读到的负数是别人改坏的，一律当读失败）。
    ///
    /// ⚠ 刻意声明成 <c>int?</c> 而不是 <c>int</c>：这样"**字段整个缺失**"（<c>{}</c> 这种被改坏的文件）
    /// 读出来是 <c>null</c>，能与"字段在、值就是 0"分开。若用非空 <c>int</c>，缺失字段会静默变成 0，
    /// 跟"合法地记着 0 次"长得一模一样，于是坏文件会被当成全新安装、被写回 <c>1</c> 覆盖掉真实计数。
    /// </summary>
    [JsonPropertyName("count")]
    public int? Count { get; set; }
}

/// <summary>
/// 启动次数记账：**本程序（守护壳）被用户真正打开了几次**。
///
/// 为什么需要它：用户希望在"帮到了我"的时刻被提醒一次去 GitHub 点 Star，而提醒的时机只能由
/// **本机事实**决定 —— 打开到第 10 次、以及此后每满 100 次。这个次数没有任何外部接口能提供，
/// 只能由本程序在每次正常启动的那一刻自己记下来，事后无法补算。
///
/// 存储：<see cref="DefaultPath"/>（<c>Config\launch-count.json</c>，与 settings.json 同目录，可随时删除重建）。
/// 格式：<c>{"count":12}</c>，UTF-8 **无 BOM**。
/// 刻意**不写进 settings.json**：那是用户的配置意图（用户可能手改、可能被"恢复默认"整份覆盖），
/// 而这是程序自己的行为流水 —— 两件事混在一个文件里，任何一方被重置都会把另一方一起带走。
///
/// 数据安全口径（fail-closed，与 <see cref="PluginTimes"/> / <see cref="VersionMemory"/> 同一套）：
///   · 文件**存在**但读不出来（IO 异常 / 无权限 / 内容不是合法账目）⇒ <see cref="Read"/> 返回 <c>Ok = false</c>；
///     <see cref="Bump"/> 据此**不计数、不写盘**并返回 <c>0</c>（调用方约定 0 = 本次不计），
///     只留一条 <see cref="Logger.NoteDiagnosis"/>。宁可这一次账目不动，也绝不许拿 <c>1</c> 或空表
///     把盘上可能存在的真实计数**覆盖掉**（覆盖掉之后计数就从 2 位数掉回 1，提醒时机全乱）；
///   · 文件**不存在**（<see cref="FileNotFoundException"/> / <see cref="DirectoryNotFoundException"/>）
///     = 全新安装 ⇒ 不算读失败，允许从 <c>1</c> 开始写。
///     判断依据只有"盘上读到了什么"，与"本程序想写什么"无关。
/// </summary>
internal static class LaunchCounter
{
    /// <summary>内存状态与落盘的互斥闸门（本类每个入口都先进它，理由见类尾"并发"一节）。</summary>
    private static readonly object Gate = new();

    /// <summary>本次进程是否已经记过一次。见 <see cref="BumpForThisRun"/> 的注释。</summary>
    private static bool _bumpedThisRun;

    /// <summary>
    /// 落盘路径：<c>Config\launch-count.json</c>。
    /// 走 <see cref="GuardPaths.ConfigDir"/> 而不是自己拼目录，理由与 <see cref="PluginTimes.StorePath"/> 一致：
    /// 路径来源只有一处，夹具隔离（<c>DSHGUARD_DATA_DIR</c>）才会自动对所有落盘点生效。
    /// </summary>
    internal static string DefaultPath => Path.Combine(GuardPaths.ConfigDir, "launch-count.json");

    /// <summary>
    /// 本次运行的启动计数（读不成 / 还没记账时为 <c>0</c>）。
    /// <c>0</c> 是"没有数字"的哨兵，**不是**"第 0 次启动"：界面据此写「本次未记录」，
    /// <see cref="ShouldPromptStar"/> 也把 0 判为不该提醒（见那里的判据）。
    /// </summary>
    internal static int SessionCount { get; private set; }

    // ══════════════ 纯函数区（不碰盘，自检可直接断言）══════════════

    /// <summary>
    /// 解析计数 JSON（**纯函数**，不碰盘、绝不抛）。
    /// 返回 false 的四种情形：内容为空、JSON 不合法、根不是对象、<c>count</c> 字段缺失或不是非负整数。
    ///
    /// 为什么"字段缺失"也算读失败而不是"当作 0"：本程序自己写的文件永远带 <c>count</c> 且 ≥ 1，
    /// 于是"没有这个字段"只可能意味着文件不是本程序写的、或被改坏了 —— 这两种情况都不知道盘上原本是多少，
    /// 当成 0 再写回就把真实计数抹了。失败关闭，交给上层"不计数、不写盘"。
    /// </summary>
    internal static bool TryParseCount(string? json, out int count)
    {
        count = 0;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            var read = JsonSerializer.Deserialize<LaunchCountFile>(json!, JsonOpts);
            if (read == null) return false;              // JSON 字面量 null ⇒ 这不是一份账目
            if (read.Count is not int n) return false;   // 字段缺失 / 类型不对（{}、"count":"12"）⇒ 读失败
            if (n < 0) return false;                     // 负数不是本程序写的，也不可当计数用
            count = n;
            return true;
        }
        catch { return false; }                          // 坏 JSON ⇒ 读失败（调用方据此拒绝写回）
    }

    /// <summary>
    /// 该不该弹"去点 Star"（**纯函数**，判据只有这一处，自检直接钉）。
    ///
    /// 判据严格照用户口径：**第 10 次**提醒一次，之后**每满 100 次**（100、200、300…）各提醒一次。
    /// 于是 0（没读成 / 没记账）、9、11、99、101、199、201 一律 false —— 尤其
    /// <c>count &gt;= 100 &amp;&amp; count % 100 == 0</c> 里的取模不能省：写成 "≥100 就提醒"
    /// 会让第 101 次之后每次启动都弹框，那是骚扰而不是提醒。
    /// </summary>
    internal static bool ShouldPromptStar(int count)
        => count == 10 || (count >= 100 && count % 100 == 0);

    // ══════════════ 读写区（有副作用）══════════════

    /// <summary>
    /// 读盘（显式 path：自检用临时文件，绝不碰用户真实 Config）。
    /// 返回 <c>(次数, 能不能安全写回)</c>：
    ///   · 文件不存在（全新安装）⇒ <c>(0, true)</c> —— 0 是"还没有任何记录"，可以安全地从 1 开始写；
    ///   · 读到合法账目 ⇒ <c>(n, true)</c>；
    ///   · 文件存在但读不出 / 解析不了 ⇒ <c>(0, false)</c>，并留一条诊断（原因与路径都记下，便于事后倒查）。
    /// </summary>
    internal static (int Count, bool Ok) Read(string path)
    {
        lock (Gate) { return ReadLocked(path); }
    }

    /// <summary>
    /// 记一次启动：读 → +1 → 写盘，返回**本次运行该用的计数**。
    ///   · 写盘成功 ⇒ 返回新值（≥ 1）；
    ///   · 读失败（文件存在但读不出来）或写失败 ⇒ 返回 <c>0</c>，表示"本次不计"。
    ///
    /// 调用方约定：<c>0</c> 不是"第 0 次启动"，而是"这次没记上"。界面与提示判据都按这个约定处理
    /// （<see cref="SessionCount"/> 同理，见那里的注释）。
    /// 本方法**绝不抛**：一次记账失败不该中断启动流程（读/写失败各自只记一条诊断）。
    /// </summary>
    internal static int Bump(string path)
    {
        lock (Gate) { return BumpLocked(path); }
    }

    /// <summary>
    /// <see cref="Bump"/> 的锁内实现（照 <see cref="PluginTimes"/> 的 <c>SaveLocked</c> 做法把"锁"与"活"
    /// 分开）：<see cref="BumpForThisRun"/> 已经在锁内，不能再进一次锁 —— <c>lock</c> 虽可重入、
    /// 不会死锁，但它会让"哪些路径在锁内"变得看不出来，而本类的全部安全性就建立在这一点上。
    /// </summary>
    private static int BumpLocked(string path)
    {
        var (count, ok) = ReadLocked(path);
        // 失败关闭：盘上有账目但读不出来 ⇒ 不计数、不写盘。
        // 若在这里"当作 0 再写 1"，用户盘上真实的 57 次会变成 1 次，之后的提醒时机全乱。
        if (!ok) return 0;
        int next = count + 1;
        return WriteLocked(path, next) ? next : 0;
    }

    /// <summary>
    /// 本次运行的记账入口（<c>App.OnStartup</c> 在"确定是正常交互启动"的那一刻调一次）：
    /// 用 <see cref="DefaultPath"/> 记一次，结果写进 <see cref="SessionCount"/> 并返回。
    ///
    /// 进程内**只记一次**：这个闸门照 <see cref="PluginTimes"/> 的 <c>_loaded</c> 做法，
    /// 挡住"将来有人把调用点挪到窗口构造之后 / 出现第二个窗口"造成的重复计数 ——
    /// 计数比实际启动数偏高，提醒就会提前到来，而且偏高多少完全看不出来。
    /// </summary>
    internal static int BumpForThisRun()
    {
        lock (Gate)
        {
            if (_bumpedThisRun) return SessionCount;
            _bumpedThisRun = true;
            SessionCount = BumpLocked(DefaultPath);
            return SessionCount;
        }
    }

    // ══════════════ 自检支持 ══════════════

    /// <summary>自检用：直接设定本次运行的计数（只赋值，不碰盘）。测提示文案与版本卡显示时调它。</summary>
    internal static void SetSessionCountForTest(int n)
    {
        lock (Gate) { SessionCount = n; }
    }

    /// <summary>
    /// 自检用：把进程内状态清空（含"本次已记过"闸门），让下一次 <see cref="BumpForThisRun"/>
    /// **真的重新读盘**（同一进程里换了 <c>DSHGUARD_DATA_DIR</c> 之后要重新验证读取路径）。只清内存，不碰磁盘。
    /// </summary>
    internal static void ResetForTest()
    {
        lock (Gate)
        {
            _bumpedThisRun = false;
            SessionCount = 0;
        }
    }

    // ══════════════ 内部实现 ══════════════

    /// <summary>
    /// 落盘/读盘共用的序列化选项：属性名大小写不敏感 —— 手改过大小写的文件也读得回来；
    /// 读不回来就会被判成"读失败"进而永远拒写，这个代价比宽容一点大得多。
    /// </summary>
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        // 只有一个字段，缩进成三行只是噪声；紧凑写法与文档里的形状（{"count":12}）一致。
        WriteIndented = false
    };

    /// <summary>
    /// 读盘。刻意用 <c>File.ReadAllText</c> 而不是 <c>File.Exists</c> 打头：<c>File.Exists</c> 在
    /// "文件不存在"与"有文件但读不了（无权限 / 路径不可达）"两种情况下都只是**返回 false**，分不出来 ⇒
    /// 后者会被当成"全新安装"，随后被 <c>1</c> 覆盖掉真实计数。
    /// ReadAllText 抛出的异常能把两者分开：文件 / 目录不存在 = 全新安装（正常，允许写）；
    /// 其余异常以及"读到了但不是合法账目" = 读失败 ⇒ 返回 Ok=false、拒绝写回。
    /// </summary>
    private static (int Count, bool Ok) ReadLocked(string path)
    {
        try
        {
            string text = File.ReadAllText(path);
            if (TryParseCount(text, out int count)) return (count, true);
            Logger.NoteDiagnosis($"启动计数读取失败，本次不计数也不写盘（{path}）：文件内容不是合法的计数 JSON");
            return (0, false);
        }
        catch (FileNotFoundException) { return (0, true); }        // 还没落过盘 = 全新安装，允许写入
        catch (DirectoryNotFoundException) { return (0, true); }  // 配置目录还没建 = 全新安装，允许写入
        catch (Exception ex)
        {
            Logger.NoteDiagnosis($"启动计数读取失败，本次不计数也不写盘（{path}）：{ex.GetType().Name}: {ex.Message}");
            return (0, false);
        }
    }

    /// <summary>
    /// 落盘（UTF-8 **无 BOM**）。写失败只记诊断、不抛，返回 false 让 <see cref="Bump"/> 交回 <c>0</c>
    /// （"本次不计"）：一次记账失败不该让启动流程出问题，但"没记上"这件事必须如实表达出来，
    /// 不能假装记成功（否则版本卡会显示一个盘上并不存在的数字）。
    /// 盘上的账目形状见顶层 <see cref="LaunchCountFile"/>。
    /// </summary>
    private static bool WriteLocked(string path, int count)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(new LaunchCountFile { Count = count }, JsonOpts),
                new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex)
        {
            Logger.NoteDiagnosis($"启动计数写入失败（{path}）：{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    // ══════════════ 并发（为什么加锁）══════════════
    // 调用点分布在两条时间线上：① 启动阶段的记账（App.OnStartup，UI 线程）；
    // ② 版本卡渲染读取 SessionCount、以及界面线程上的 Star 提示判据；③ 自检。
    // 单看"每次启动只记一次"似乎不会并发，但：
    //   · SessionCount 是"记账写、渲染读"的共享状态，WPF 的重绘时机不由这里决定；
    //   · 读 → +1 → 写盘这三步必须是一个整体：两次记账交错就会丢掉一次（两个人都读到 12，都写 13）。
    // 因此与 PluginTimes 同款：一把静态闸门串起本类全部入口，读、写、落盘都在锁内完成。
    // 代价说明：锁内包含一次几十字节的文件读写，且只在启动那一刻发生一次，不会成为界面卡顿来源。
}
