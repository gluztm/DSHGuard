using System;
using System.Net.NetworkInformation;
using System.Diagnostics;
using System.Threading.Tasks;

namespace DSHGuard;

public static class NetworkHelper
{
    public static bool IsPortListening(int port)
    {
        try
        {
            var properties = IPGlobalProperties.GetIPGlobalProperties();
            var listeners = properties.GetActiveTcpListeners();
            foreach (var ep in listeners)
            {
                if (ep.Port == port) return true;
            }
        }
        catch { }
        return false;
    }

    public static int GetEstablishedConnections(int port)
    {
        int count = 0;
        try
        {
            var properties = IPGlobalProperties.GetIPGlobalProperties();
            var connections = properties.GetActiveTcpConnections();
            foreach (var conn in connections)
            {
                if (conn.LocalEndPoint.Port == port &&
                    conn.State == TcpState.Established)
                {
                    count++;
                }
            }
        }
        catch { }
        return count;
    }

    public static void OpenBrowser(string url)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
                Verb = "open"
            };
            Process.Start(psi);
        }
        catch { }
    }

    /// <summary>
    /// 从引擎输出里提取它自己打印的访问地址（形如 http://127.0.0.1:3080/?token=xxx）。
    /// 取不到返回空串。带 token 的地址才是可直接打开的入口。
    /// </summary>
    public static string ExtractReadyUrl(string line, int port)
    {
        try
        {
            if (string.IsNullOrEmpty(line)) return "";
            var m = System.Text.RegularExpressions.Regex.Match(
                line, @"https?://(?:127\.0\.0\.1|localhost):" + port + @"/?\?token=[^\s""'<>]+",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return m.Success ? m.Value : "";
        }
        catch { return ""; }
    }

    /// <summary>
    /// 干净地址的就绪判定（探测只用干净地址，见 <see cref="ChooseProbeUrl"/>）：
    /// **404 = 还没注册完**——webserver 在路由注册前对一切请求回 404（源码注释：
    /// *answers anything not yet claimed during startup with 404*），这正是"首开必 404"的来源；
    /// **401 = 已注册**（就等浏览器带着令牌进来，可以开了）；2xx/3xx = 已经能直接看；
    /// 其余（403/5xx 等）一律不算就绪，免得把错误页当成能用的页面打开。
    /// </summary>
    public static bool IsRegistered(int statusCode) => statusCode is (>= 200 and < 400) or 401;

    /// <summary>
    /// 就绪探测该打哪个地址：**永远用干净地址**。
    /// 带令牌的地址是 DSH 交给浏览器的一次性入场券（它同时把干净地址留给模型与 shell），
    /// 拿它去探测等于替浏览器把票用掉，用户随后打开就会落到失效页面。
    /// </summary>
    public static string ChooseProbeUrl(string tokenUrl, int port) => $"http://127.0.0.1:{port}/";

    /// <summary>把环回地址里的 localhost 归一成 127.0.0.1：localhost 可能解析成 ::1，而引擎只监听 IPv4。</summary>
    public static string NormalizeLoopback(string url)
    {
        try
        {
            if (string.IsNullOrEmpty(url)) return "";
            return url.Replace("//localhost", "//127.0.0.1", StringComparison.OrdinalIgnoreCase);
        }
        catch { return url; }
    }

    /// <summary>
    /// 等页面真正可用：反复请求，直到 <see cref="IsRegistered"/> 判定通过。
    /// `probe` 可注入（自检用）：给一个"发一次请求、返回状态码"的函数即可，
    /// 这样这条逻辑不依赖真实网络，测试也就不会因为环回抖动而飘。
    /// </summary>
    public static async Task<bool> WaitUntilHttpReadyAsync(string url, int timeoutMs = 15000,
                                                          Func<string, Task<int>>? probe = null)
    {
        url = NormalizeLoopback(url);
        if (string.IsNullOrWhiteSpace(url)) return false;
        var sw = Stopwatch.StartNew();
        using var http = new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false
        })
        { Timeout = TimeSpan.FromSeconds(5) };

        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                int code = probe != null
                    ? await probe(url)
                    : (int)(await http.GetAsync(url)).StatusCode;
                if (IsRegistered(code)) return true;
            }
            catch { }
            await Task.Delay(probe != null ? 1 : 500);       // 注入探测不必真等，测试跑得快
        }
        return false;
    }
}
