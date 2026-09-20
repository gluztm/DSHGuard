namespace DSHGuard;

/// <summary>引擎为什么没起来（等待循环的停止原因）。</summary>
internal enum FailureKind
{
    /// <summary>进程未启动。</summary>
    LaunchFailed,
    /// <summary>起来了，但在端口就绪之前就退出了（本机最常见的"秒退"）。</summary>
    ProcessExited,
    /// <summary>真的等满了等待上限，端口始终没监听。</summary>
    Timeout,
    /// <summary>用户自己点了取消——这不是故障。</summary>
    Canceled
}

/// <summary>
/// 一次启动失败的归因与文案（纯函数，便于自检）。
///
/// 立这条规矩的现场教训：引擎 3 秒就退了，弹窗却写「等了 180 秒，引擎还是没起来」，
/// 而且建议「过一会儿再点一次」——对"版本与配置文件对不上"这种确定性失败，点一百次也一样。
/// 所以文案必须由**实际停止原因 + 实际耗时**推出来，且确定性失败不再自动重试。
/// </summary>
internal sealed record StartupFailure(FailureKind Kind, int ElapsedSeconds, string ReasonLine, string Tail)
{
    /// <summary>是不是「模块解析不到」这类失败：原因与建议都用同一口径，免得一格说"换版本"、另一格劝"再点一次"。</summary>
    public bool Deterministic => StartupCause.IsUnresolvable(ReasonLine, Tail);

    /// <summary>
    /// 还要不要再自动重试一次：**任何"秒退/等满"的失败都值得重试一次**，但只给一次。
    /// 现场教训（2026-09-13 11:47 实测）：同一台机器同一个 rc.2，11:11 秒退、11:47 又能正常起来——
    /// 根因是当时配置文件正被写入（插件半安装），属于暂时状态。既然重试真的可能成功，
    /// 就不能因为"看着像确定性失败"而跳过它；重试仍失败时，才把「换回能用的版本」摆出来。
    /// </summary>
    public bool ShouldAutoRetry(bool alreadyRetried)
        => !alreadyRetried && Kind is FailureKind.Timeout or FailureKind.LaunchFailed or FailureKind.ProcessExited;

    /// <summary>
    /// 重试过一轮之后仍然失败，才按"确定性失败"处理（换版本比反复重试有意义）。
    /// 首次失败不动用这个结论——它可能只是配置文件正好在写。
    /// </summary>
    public bool DeterministicAfterRetry(bool alreadyRetried) => Deterministic && alreadyRetried;

    /// <summary>弹窗第一句：**规范表述 + 实际耗时**，永远不写死 180，也不写"这次只等了"这种口语。</summary>
    public string Head => Kind switch
    {
        FailureKind.Canceled => "",
        FailureKind.ProcessExited => $"加载出错，等待时间 {ElapsedSeconds} 秒（引擎启动后立即退出）。",
        FailureKind.Timeout => $"加载出错，等待时间 {ElapsedSeconds} 秒（引擎始终未就绪）。",
        _ => "加载出错（引擎进程未能启动）。"
    };

    /// <summary>
    /// 事件面板里那一行红字：**一句话说完**（要求：别像碎碎念；原因与建议只留在弹窗与日志里）。
    /// </summary>
    public string EventLine => Kind switch
    {
        FailureKind.Canceled => "",
        FailureKind.ProcessExited => $"启动失败：等待 {ElapsedSeconds} 秒后引擎退出",
        FailureKind.Timeout => $"启动失败：等待 {ElapsedSeconds} 秒未就绪",
        _ => "启动失败：引擎进程未能启动"
    };

    /// <summary>失败弹窗的正文：一句规范原因 + 最多三条短建议（技术细节只进日志）。</summary>
    public string DialogText()
    {
        if (Kind == FailureKind.Canceled) return "";
        string cause = StartupCause.Describe(ReasonLine, Tail);
        string tips = Deterministic
            ? "处理方式：\n"
              + "· 换回上一个可用版本（推荐）\n"
              + "· 或在「设置 → 版本」页手动切换"
            : "处理方式：\n"
              + "· 稍后重试「一键启动引擎」\n"
              + "· 确认网络可正常下载";
        return Head + "\n\n原因：" + cause + "\n\n" + tips;
    }
}
