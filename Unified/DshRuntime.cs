using System;
using System.Diagnostics;
using System.Linq;

namespace DSHGuard.Unified;

/// <summary>
/// DSH 是否正在运行。
///
/// ── 为什么需要它 ─────────────────────────────────────────────────────
/// 本功能区的写操作都是**直接改磁盘文件**，而 DSH 自己也在读写这些文件：
///   · 会话：DSH 在对话过程中持续写 session.*.zstd；
///   · 提示词：MEMORY.md 由 DSH 运行时追加记忆；
///   · 技能：DSH 的 skill provider 是 watcher 驱动的，改文件会触发它重扫。
///
/// 所以 DSH 在跑时做这些操作，可能出现「DSH 读到半截内容」或「用户的改动被覆盖」。
/// 本程序的策略不是禁止（用户可能就是想趁它开着改），而是**先提醒、让用户自己决定**。
///
/// ── 风格约定 ─────────────────────────────────────────────────────────
/// 只做只读探测、不抛异常、失败当「不知道」（返回 Unknown），
/// 由调用方决定「不知道」时是提醒还是放过 —— 绝不因为探测失败就阻断用户操作。
/// </summary>
public static class DshRuntime
{
    public enum State
    {
        /// <summary>明确没在跑。</summary>
        Stopped = 0,
        /// <summary>明确在跑。</summary>
        Running = 1,
        /// <summary>探测不到（权限 / 平台限制）。**不等于没在跑**。</summary>
        Unknown = 2,
    }

    private static readonly string[] ProcessNames = { "dsh", "dsh-desktop", "DSH", "DeepSeek Harness" };

    /// <summary>探一次 DSH 运行态。结果缓存 1 秒，避免连续点击时反复枚举进程。</summary>
    public static State Probe()
    {
        long now = Environment.TickCount64;
        if (now - _lastProbeAt < 1000 && _lastProbeAt > 0) return _lastState;

        State st;
        try
        {
            bool running = ProcessNames.Any(n =>
            {
                var list = Process.GetProcessesByName(n);
                try { return list.Length > 0; }
                finally { foreach (var p in list) p.Dispose(); }
            });
            st = running ? State.Running : State.Stopped;
        }
        catch (Exception ex)
        {
            Logger.LogError("DshRuntime.Probe", ex);
            st = State.Unknown;
        }

        _lastState = st;
        _lastProbeAt = now;
        return st;
    }

    private static State _lastState = State.Stopped;
    private static long _lastProbeAt;

    /// <summary>测试用：丢掉缓存，强制下次重探。</summary>
    internal static void ResetCache() => _lastProbeAt = 0;

    /// <summary>
    /// 给「写文件前」的提醒语。没在跑 / 不知道时返回空串（不打扰用户）。
    /// </summary>
    public static string WriteWarning(string what)
    {
        return Probe() switch
        {
            State.Running =>
                "⚠ DSH 现在正开着。它可能同时在读这个" + what + "，改完建议重启 DSH 让它重新读一次。",
            _ => "",
        };
    }

    /// <summary>给「移走/删除类」操作的附加确认行；运行中时语气更强。</summary>
    public static string DestructiveWarning(string what)
    {
        return Probe() switch
        {
            State.Running =>
                "⚠ 检测到 DSH 正在运行。此时移动该" + what + "，若 DSH 正在使用该资源，可能出现报错或写入失败。\n" +
                "建议先完全退出 DSH 后再执行整理操作。",
            State.Unknown =>
                "（没能确认 DSH 是否在运行。如果它开着，建议先退出再操作。）",
            _ => "",
        };
    }
}
