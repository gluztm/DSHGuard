using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DSHGuard;

/// <summary>
/// 子进程控制台输出的**三档解码器**（G-04，2026-11-04）。
/// <para>
/// 问题：Windows 上 .NET 的重定向输出默认按 UTF-8 解，但子进程不一定输出 UTF-8。
///   · node / dsh 自身输出恒为 UTF-8；
///   · **cmd 在没有控制台时恒输出系统 ANSI（本机 GBK/936）** —— CreateNoWindow=true 的子进程正是这种情形。
///     实测 <c>cmd echo 配置完成</c> 的 stdout 字节是 C5 E4 D6 C3 CD EA B3 C9（GBK），
///     且无控制台时 <c>chcp 65001 &gt; nul &amp; echo</c> 也改不动它（cmd 自己仍是 GBK）。
///   过去统一按 UTF-8 解 ⇒ cmd 的中文报错整段变成 U+FFFD（不可逆，用户看到的就是乱码）。
/// </para>
/// <para>
/// 口径：**按行**判定，不按整流判定。整流判不出来（cmd 一段 GBK + node 一段 UTF-8 混排时，
///   两种编码都可能"部分解得出"），逐行才能把两段各自判对。
///   每行依次试三档：① 严格 UTF-8 → ② 系统 ANSI（ACP）→ ③ 系统 OEM。
/// </para>
/// <para>
/// 为什么不用"整流里没有 U+FFFD"当健康信号：ACP 解失败时 .NET 吐的是 ASCII '?'（U+003F）
///   而不是 U+FFFD ⇒ 没有替换符不等于解对了。
/// </para>
/// <para>
/// 为什么不用"行里有没有汉字"之类的启发式：GBK 行被严格 UTF-8 解析成功的概率约 9.186%
///   （每个汉字对的 lead∈C2..DF 且 trail∈80..BF），而这类误判是**静默**的
///   （"隆垄"→"¡¢"，既没有替换符也没有异常）⇒ 任何启发式都会引入新的误伤，不加。
/// </para>
/// </summary>
internal static class ConsoleTextDecoder
{
    /// <summary>档位 1：严格 UTF-8（本档最常见：node / dsh / 纯 ASCII）。</summary>
    public const int TierUtf8 = 1;

    /// <summary>档位 2：系统 ANSI（ACP）。中文 Windows 上是 936（gb2312）——cmd 无控制台时的输出走这档。</summary>
    public const int TierAcp = 2;

    /// <summary>档位 3：系统 OEM。中文 Windows 上 OEM==ANSI==936，所以本机这档与档位 2 同解。</summary>
    public const int TierOem = 3;

    /// <summary>档位 4：三档都不认 ⇒ 按字节保真回显（Latin-1 一字节一字符，一个字节都不丢），并留一行 hex 诊断。</summary>
    public const int TierFallback = 4;

    /// <summary>hex 诊断的窗口大小：头、尾各这么多字节。</summary>
    private const int HexWindowBytes = 512;

    /// <summary>严格 UTF-8：遇到非法字节直接抛，绝不静默替成 U+FFFD。</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>系统 ANSI（ACP）解码器；拿不到（CodePages 没注册）时为 null。</summary>
    private static readonly Lazy<Encoding?> AcpEncoding = new(
        () => TryGetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage),
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>系统 OEM 解码器；拿不到时为 null。</summary>
    private static readonly Lazy<Encoding?> OemEncoding = new(
        () => TryGetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage),
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>CodePages 提供程序只注册一次（<c>Encoding.RegisterProvider</c> 本身幂等，这里再挡一层）。</summary>
    private static readonly Lazy<bool> CodePagesRegistration = new(
        () =>
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return true;
            }
            catch (Exception)
            {
                // 框架里没有 CodePages：不抛。ANSI/OEM 两档自然不可用，由解码时的 hex 诊断留证，
                // 而不是让程序起不来（这个失败模式必须"看得见但不死机"）。
                return false;
            }
        },
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// 注册 CodePages 提供程序（幂等，可重复调用）。
    /// <para>
    /// 硬前置：不注册的话 <c>Encoding.GetEncoding(936)</c> 直接抛
    /// <c>System.NotSupportedException: No data is available for encoding 936</c>
    /// ⇒ 系统 ANSI/OEM 两档整个不可用，cmd 的中文输出会整段退成字节保真回显。
    /// 自包含单文件发布里已经带了 CodePages（不额外引入任何依赖）。
    /// </para>
    /// </summary>
    public static void EnsureCodePagesRegistered() => _ = CodePagesRegistration.Value;

    /// <summary>
    /// 读完整条流并按三档解码。
    /// <para>
    /// **只读原始字节**（<c>Process.StandardOutput.BaseStream</c>）：一旦碰过 <c>StreamReader</c> 的
    /// Read* API，缓冲区里已读走的字节就再也拿不回来（实测 readerGot=10 + rest=99920 = 99930，丢 70 字节）。
    /// </para>
    /// </summary>
    public static async Task<string> ReadDecodedAsync(Stream? stream, CancellationToken cancellationToken = default)
    {
        if (stream == null) return "";
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        try
        {
            int n;
            while ((n = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken).ConfigureAwait(false)) > 0)
                buffer.Write(chunk, 0, n);
        }
        catch (IOException)
        {
            // 进程被超时杀掉时管道会断（"pipe has been ended"）：已读到的字节照样要解出来，
            // 不能让这个异常盖掉"命令超时"这条真正的结论。
        }
        catch (ObjectDisposedException)
        {
            // 同上：进程已退出、流被释放，按已读到的内容收尾。
        }
        return DecodeBytes(buffer.ToArray());
    }

    /// <summary>按三档解码一整块字节（行界是 0x0A，行尾的 0x0D 剥掉）。</summary>
    public static string DecodeBytes(byte[]? bytes)
    {
        var lines = DecodeLines(bytes);
        var parts = new List<string>(lines.Count * 2);
        foreach (var line in lines)
        {
            parts.Add(line.Text);
            if (line.Terminated) parts.Add("\n");
        }
        return string.Concat(parts);
    }

    /// <summary>
    /// 按三档解码一整块字节，并**逐行给出命中的档位**（自检要用：档位关系是跨机器稳的，
    /// 解出来的具体文字不是——英文机 ACP=1252 时 GBK 行会解成 mojibake，但档位结论不变）。
    /// </summary>
    public static IReadOnlyList<DecodedLine> DecodeLines(byte[]? bytes)
    {
        var lines = new List<DecodedLine>();
        if (bytes == null || bytes.Length == 0) return lines;

        int n = bytes.Length;
        int start = 0;
        int bad = 0;
        for (int i = 0; i < n; i++)
        {
            if (bytes[i] != 0x0A) continue;
            lines.Add(DecodeLine(bytes, start, i - start, ref bad, terminated: true));
            start = i + 1;
        }
        // 末尾没有换行符的残余也算一行
        if (start < n)
            lines.Add(DecodeLine(bytes, start, n - start, ref bad, terminated: false));

        if (bad > 0) NoteUndecodableBytes(bytes, bad);
        return lines;
    }

    /// <summary>
    /// 解一行：三档依次试（严格 UTF-8 → 系统 ANSI → 系统 OEM），全败才退到字节保真回显。
    /// </summary>
    private static DecodedLine DecodeLine(byte[] bytes, int offset, int count, ref int bad, bool terminated)
    {
        // 行尾的 0x0D 剥掉（CRLF 的 CR）；行内的裸 0x0D 留给后面归一成字面 &lt;CR&gt;
        if (count > 0 && bytes[offset + count - 1] == 0x0D) count--;
        if (count <= 0) return new DecodedLine("", TierUtf8, terminated);

        string? text = null;
        int tier = TierFallback;
        try
        {
            text = StrictUtf8.GetString(bytes, offset, count);
            tier = TierUtf8;
        }
        catch (DecoderFallbackException)
        {
            var acp = AcpEncoding.Value;
            if (acp != null)
            {
                try { text = acp.GetString(bytes, offset, count); tier = TierAcp; }
                catch (DecoderFallbackException) { text = null; }
            }
            if (text == null)
            {
                var oem = OemEncoding.Value;
                if (oem != null)
                {
                    try { text = oem.GetString(bytes, offset, count); tier = TierOem; }
                    catch (DecoderFallbackException) { text = null; }
                }
            }
        }
        if (text == null)
        {
            // 三档都不认：按字节保真回显（Latin-1 把字节 1:1 映射成 U+0000..U+00FF，一个字节都不丢），
            // 并留一行 hex 诊断 —— 现场能靠头尾各 512 B 复原原始字节。
            text = Encoding.Latin1.GetString(bytes, offset, count);
            tier = TierFallback;
            bad++;
        }
        // 行内裸 CR 归一成字面 <CR>：日志写端按 '\n' 切行（见 Logger.TruncateCommandOutput），
        // 裸 CR 留着会让"日志里几行"和"实际几行"对不上。
        if (text.IndexOf('\r') >= 0) text = text.Replace("\r", "<CR>");
        return new DecodedLine(text, tier, terminated);
    }

    /// <summary>
    /// 三档全败时的现场留证：**一行** hex（头尾各 512 B）。走 NoteDiagnosis（落盘、不弹窗）。
    /// </summary>
    private static void NoteUndecodableBytes(byte[] bytes, int badLines)
    {
        int headLen = Math.Min(bytes.Length, HexWindowBytes);
        int tailStart = Math.Max(0, bytes.Length - HexWindowBytes);
        int tailLen = bytes.Length - tailStart;
        Logger.NoteDiagnosis(
            $"控制台输出解码：有 {badLines} 行既不是 UTF-8 也不是系统 ANSI/OEM，已按字节保真回显；"
            + $"字节数={bytes.Length}；头部[{headLen}B]={HexPreview(bytes, 0, headLen)}；"
            + $"尾部[{tailLen}B]={HexPreview(bytes, tailStart, tailLen)}");
    }

    /// <summary>hex 预览（大写、每字节两位、不加空格）。</summary>
    private static string HexPreview(byte[] bytes, int offset, int count)
    {
        if (count <= 0) return "";
        const string digits = "0123456789ABCDEF";
        var chars = new char[count * 2];
        for (int i = 0; i < count; i++)
        {
            byte b = bytes[offset + i];
            chars[i * 2] = digits[b >> 4];
            chars[i * 2 + 1] = digits[b & 0x0F];
        }
        return new string(chars);
    }

    /// <summary>取一个"严格解码"的编码器（非法字节直接抛，不静默替成 '?'）；拿不到就 null。</summary>
    private static Encoding? TryGetEncoding(int codePage)
    {
        try
        {
            return Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>一行解码结果：文本 + 命中的档位 + 该行是否以换行符结束。</summary>
    internal readonly struct DecodedLine
    {
        public string Text { get; }
        public int Tier { get; }
        public bool Terminated { get; }

        public DecodedLine(string text, int tier, bool terminated)
        {
            Text = text;
            Tier = tier;
            Terminated = terminated;
        }
    }
}
