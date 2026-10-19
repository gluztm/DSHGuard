using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DSHGuard;

/// <summary>
/// 盘上的账目（<c>Config\launch-count.json</c>）：<c>count</c> 是累计活跃次数，<c>next</c> 是下一次提醒的门槛。
/// <c>Count</c> 声明为 <c>int?</c>：字段缺失读出来是 null，与“值就是 0”区分开，
/// 缺失的文件按“被改坏”处理，不会被当作全新安装覆盖。
/// </summary>
internal sealed class LaunchCountFile
{
    [JsonPropertyName("count")]
    public int? Count { get; set; }

    /// <summary>下一次提醒的门槛；旧版文件没有这个字段，读到 null 时由程序补一个。</summary>
    [JsonPropertyName("next")]
    public int? Next { get; set; }
}

/// <summary>
/// 活跃次数记账，只用来决定“什么时候提醒用户去点 Star”，界面不显示。
///
/// 两种情况各记一次：程序启动；窗口闲置较久（见 MainWindow 的阈值）后再次被唤起。
/// 门槛取 50~100 之间的随机数：累计达到门槛弹一次，然后在当前次数上再滚一个新门槛。
///
/// 读盘失败时不计数、不写盘（返回 0），避免用空账目覆盖盘上的真实计数；文件不存在视为全新安装。
/// 文件很小，所有入口共用一把锁，读 → 加一 → 写在锁内完成。
/// </summary>
internal static class LaunchCounter
{
    private static readonly object Gate = new();
    private static bool _bumpedThisRun;
    private static int _next;

    /// <summary>门槛的取值范围（含两端）。</summary>
    internal const int MinGap = 50, MaxGap = 100;

    internal static string DefaultPath => Path.Combine(GuardPaths.ConfigDir, "launch-count.json");

    /// <summary>当前累计次数；0 表示本次没有记上（读盘失败等）。</summary>
    internal static int SessionCount { get; private set; }

    // ── 纯函数 ──

    /// <summary>在 <paramref name="count"/> 之后再等 50~100 次的随机门槛。</summary>
    internal static int RollNext(int count, Random? rng = null)
        => count + (rng ?? Random.Shared).Next(MinGap, MaxGap + 1);

    /// <summary>累计次数到达门槛才提醒；任一值无效（&lt;= 0）都不提醒。</summary>
    internal static bool ShouldPromptStar(int count, int next)
        => count > 0 && next > 0 && count >= next;

    /// <summary>解析账目 JSON；内容为空、不是对象、<c>count</c> 缺失或为负数都算失败。</summary>
    internal static bool TryParse(string? json, out int count, out int next)
    {
        count = 0;
        next = 0;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            var read = JsonSerializer.Deserialize<LaunchCountFile>(json!, JsonOpts);
            if (read?.Count is not int n || n < 0) return false;
            count = n;
            next = read.Next is int x && x > 0 ? x : 0;
            return true;
        }
        catch { return false; }
    }

    internal static bool TryParseCount(string? json, out int count) => TryParse(json, out count, out _);

    // ── 读写 ──

    /// <summary>读盘：返回 (次数, 能否安全写回)。文件不存在是全新安装，视为 (0, true)。</summary>
    internal static (int Count, bool Ok) Read(string path)
    {
        lock (Gate)
        {
            var r = ReadLocked(path);
            return (r.Count, r.Ok);
        }
    }

    /// <summary>记一次并落盘，返回新的次数；读写失败返回 0，表示这次没记上。</summary>
    internal static int Bump(string path)
    {
        lock (Gate) { return BumpLocked(path).Count; }
    }

    /// <summary>程序启动时记一次；同一进程只记一次。</summary>
    internal static int BumpForThisRun()
    {
        lock (Gate)
        {
            if (_bumpedThisRun) return SessionCount;
            _bumpedThisRun = true;
            var r = BumpLocked(DefaultPath);
            SessionCount = r.Count;
            _next = r.Next;
            return SessionCount;
        }
    }

    /// <summary>闲置较久后窗口再次被唤起时记一次。启动那次没记上时不记。</summary>
    internal static void NoteReactivation()
    {
        lock (Gate)
        {
            if (!_bumpedThisRun || SessionCount <= 0) return;
            var r = BumpLocked(DefaultPath);
            if (r.Count <= 0) return;
            SessionCount = r.Count;
            _next = r.Next;
        }
    }

    /// <summary>累计次数是否已到提醒门槛。</summary>
    internal static bool IsPromptDue()
    {
        lock (Gate) { return ShouldPromptStar(SessionCount, _next); }
    }

    /// <summary>提醒框弹出过之后调用：在当前次数上重新滚一个门槛并落盘。</summary>
    internal static void MarkPrompted()
    {
        lock (Gate)
        {
            if (SessionCount <= 0) return;
            _next = RollNext(SessionCount);
            WriteLocked(DefaultPath, SessionCount, _next);
        }
    }

    // ── 自检支持 ──

    internal static void SetSessionCountForTest(int n)
    {
        lock (Gate) { SessionCount = n; }
    }

    internal static void ResetForTest()
    {
        lock (Gate)
        {
            _bumpedThisRun = false;
            SessionCount = 0;
            _next = 0;
        }
    }

    // ── 内部实现 ──

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,   // 手改过大小写的文件也能读回
        WriteIndented = false
    };

    private static (int Count, int Next, bool Ok) ReadLocked(string path)
    {
        try
        {
            string text = File.ReadAllText(path);
            if (TryParse(text, out int count, out int next)) return (count, next, true);
            Logger.NoteDiagnosis($"启动计数读取失败，本次不计数也不写盘（{path}）：内容不是合法的计数 JSON");
            return (0, 0, false);
        }
        catch (FileNotFoundException) { return (0, 0, true); }        // 全新安装
        catch (DirectoryNotFoundException) { return (0, 0, true); }
        catch (Exception ex)
        {
            Logger.NoteDiagnosis($"启动计数读取失败，本次不计数也不写盘（{path}）：{ex.GetType().Name}: {ex.Message}");
            return (0, 0, false);
        }
    }

    private static (int Count, int Next) BumpLocked(string path)
    {
        var (count, next, ok) = ReadLocked(path);
        if (!ok) return (0, 0);                      // 读不出来就不写，保护盘上的真实计数
        int newCount = count + 1;
        if (next <= 0) next = RollNext(newCount);   // 旧版文件或全新安装：补一个门槛
        return WriteLocked(path, newCount, next) ? (newCount, next) : (0, 0);
    }

    private static bool WriteLocked(string path, int count, int next)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            AtomicFile.WriteAllText(path,
                JsonSerializer.Serialize(new LaunchCountFile { Count = count, Next = next }, JsonOpts),
                new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex)
        {
            Logger.NoteDiagnosis($"启动计数写入失败（{path}）：{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }
}
