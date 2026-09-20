using System;

namespace DSHGuard;

/// <summary>
/// 启动失败原因归类：把引擎自己的报错翻译成一句"能照做"的人话。
/// 只做字符串特征匹配，不访问系统也不联网——纯函数，便于自检。
/// 面向用户的正文保持短（详情写日志），避免弹窗被堆栈撑高。
/// </summary>
public static class StartupCause
{
    /// <summary>
    /// 是不是「模块解析不到」这一类错误：引擎自带的组件与当前配置文件对不上。
    /// 对这类失败**重试没有意义**（点一百次还是同一个错），换回上一个能用的版本才有效。
    /// </summary>
    public static bool IsUnresolvable(string? issueLine, string? engineTail = null)
    {
        string s = ((issueLine ?? "") + "\n" + (engineTail ?? "")).ToLowerInvariant();
        return s.Contains("err_module_not_found", StringComparison.Ordinal)
            || s.Contains("cannot find package", StringComparison.Ordinal)
            || s.Contains("failed to import loader entry", StringComparison.Ordinal);
    }

    /// <summary>
    /// 是不是「补丁层配置文件语法错误」：cordis.patch.yml 解析不过时，引擎会**忽略整份补丁层**。
    /// 现场表现就是"点了禁用插件、重启却没禁用"，而且原有禁用与插件配置一起失效。
    /// 这类错误重试无用，必须先修文件（本程序在启动时会自动修正/清理自己写的坏块）。
    /// </summary>
    public static bool IsConfigParseError(string? issueLine, string? engineTail = null)
    {
        string s = ((issueLine ?? "") + "\n" + (engineTail ?? "")).ToLowerInvariant();
        return s.Contains("failed to parse overlay", StringComparison.Ordinal)
            || s.Contains("failed to parse cordis.patch", StringComparison.Ordinal)
            || s.Contains("yaml exception", StringComparison.Ordinal);
    }

    /// <summary>一句话原因（按常见程度排序匹配）。</summary>
    public static string Describe(string? issueLine, string? engineTail = null)
    {
        string s = ((issueLine ?? "") + "\n" + (engineTail ?? "")).ToLowerInvariant();
        if (s.Trim().Length == 0) return "原因不明";

        if (IsConfigParseError(issueLine, engineTail))
            return "配置文件（补丁层）有语法错误，引擎已忽略整份补丁";
        if (IsUnresolvable(issueLine, engineTail))
            return "引擎自带组件与当前配置文件不匹配";
        if (s.Contains("eaddrinuse", StringComparison.Ordinal) ||
            s.Contains("address already in use", StringComparison.Ordinal))
            return "端口被其他程序占用";
        if (s.Contains("enotfound", StringComparison.Ordinal) ||
            s.Contains("etimedout", StringComparison.Ordinal) ||
            s.Contains("econnrefused", StringComparison.Ordinal) ||
            s.Contains("registry", StringComparison.Ordinal) ||
            s.Contains("network", StringComparison.Ordinal))
            return "网络未连通，或下载被拦截";
        if (s.Contains("eacces", StringComparison.Ordinal) || s.Contains("eperm", StringComparison.Ordinal))
            return "权限不足";
        if (s.Contains("heap out of memory", StringComparison.Ordinal) ||
            s.Contains("javascript heap", StringComparison.Ordinal))
            return "内存不足";
        return "原因不明";
    }

    /// <summary>
    /// 失败弹窗的正文（保留旧签名，内部走 <see cref="StartupFailure"/>）：
    /// elapsedSeconds 为 0 表示进程未启动，> 0 表示等了这么久仍没就绪。
    /// </summary>
    public static string DialogText(string? issueLine, string? engineTail, int elapsedSeconds)
        => new StartupFailure(
                elapsedSeconds > 0 ? FailureKind.Timeout : FailureKind.LaunchFailed,
                elapsedSeconds, issueLine ?? "", engineTail ?? "").DialogText();
}
