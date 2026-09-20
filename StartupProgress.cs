namespace DSHGuard;

/// <summary>
/// 启动进度模型（纯函数，便于自检）。
///
/// 设计：**阶段百分比 = 守护壳真正走到的里程碑**，等待时长只做「渐近」映射——
/// 不再用「已等秒数 / 180」当进度（那会让进度条几秒内只爬到 3%，
/// 然后随面板切换一下消失，看起来像"3% 之后突然跳满"）。
/// 另外：即使引擎迟迟不就绪，显示值也会按对数曲线慢慢爬到 99%（<see cref="Creep"/>），
/// 把最后那 1% 留给真正的完成信号。
/// </summary>
internal static class StartupProgress
{
    /// <summary>正在检查运行环境。</summary>
    public const double Check = 6;

    /// <summary>正在拉起引擎进程。</summary>
    public const double Spawn = 14;

    /// <summary>进程已起来，在等端口监听。</summary>
    public const double Waiting = 28;

    /// <summary>端口已就绪，在等页面注册 / 浏览器交接。</summary>
    public const double PageReady = 92;

    /// <summary>完成（只由就绪信号显式给出，蠕行永远到不了）。</summary>
    public const double Done = 100;

    /// <summary>蠕行上限：卡住时也只爬到 99%。</summary>
    public const double CreepCap = 99;

    /// <summary>蠕行时间常数（秒）：越小爬得越快（60s≈66%、180s≈95%）。</summary>
    private const double CreepTau = 55.0;

    /// <summary>等待端口期间的目标值：28% → 88% 渐近，不越过 88（"看着快好了"也不许骗到 99）。</summary>
    public static double WaitTarget(double seconds)
        => Waiting + (88 - Waiting) * (1 - System.Math.Exp(-System.Math.Max(0, seconds) / 45.0));

    /// <summary>卡住时的蠕行值：99*(1-e^(-t/55))，单调递增、恒不超过 99。</summary>
    public static double Creep(double seconds)
        => CreepCap * (1 - System.Math.Exp(-System.Math.Max(0, seconds) / CreepTau));
}
