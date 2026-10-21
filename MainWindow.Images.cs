using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DSHGuard;

/// <summary>截图抓取与看图：磁盘 + 内存双层缓存；下载经 <see cref="PluginMarket.ImageRoutes"/> 并限制 4 路并发；未收录截图时可从仓库 README 抓取；点击缩略图打开放大层。</summary>
public partial class MainWindow : Window
{
    private static readonly HttpClient ImgHttp = CreateImgClient();
    private static HttpClient CreateImgClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) DSHGuard/1.0");
        return c;
    }

    private static string ImageCacheDir => GuardPaths.CacheDirImages;

    private readonly Dictionary<string, BitmapSource> _imgThumb = new();
    private readonly Dictionary<string, BitmapSource> _imgFull = new();
    // 审计修正（2026-10-19，P2）：_imgFailed 原先是 HashSet —— 失败即永久拉黑，且只增不减。
    //   代理抖动一次、或 URL 里带过期时间戳的资源变一次，那张图在这次运行里就再也取不回来了，
    //   用户只能重启程序（而当初的失败原因其实早就消失）。改成 记录->过期时间 的字典：TTL 内不重试
    //   （避免对着坏路由反复打），TTL 过后允许重试；并设条目上限，防止长期运行下无限堆积。
    private readonly Dictionary<string, DateTime> _imgFailed = new();
    private static readonly TimeSpan ImageFailTtl = TimeSpan.FromMinutes(10);
    private const int ImageFailMaxEntries = 512;
    // 审计修正（2026-10-19，P2）：同一 URL 的并发取图合流表（单飞）。理由：_imgGate 只有 4 路并发，
    //   而市场页一次铺 30+ 张卡，缩略图与放大层几乎同时请求同一张图；原先那句「门内复查内存」复查不到 ——
    //   首个调用者是先放锁、再解码、最后才写内存，等锁者进门时内存里必然还是空的，于是同一张图被下两遍
    //   （网络与写盘都翻倍）。合流之后同 URL 只下一次，等的人直接拿同一份字节去各自解码。
    private readonly Dictionary<string, Task<byte[]?>> _imgInflight = new();
    private readonly SemaphoreSlim _imgGate = new(4, 4);

    private List<string> _lightboxUrls = new();
    private int _lightboxIndex;
    private double _lightboxZoom = 1.0;      // 看图层的缩放倍数；1.0 = 适应窗口
    // 2.2.1：平移 / 缩放改用纯变换（RenderTransform = 先缩放、再平移），不再借 ScrollViewer 的滚动偏移 ——
    //   滚动偏移被夹在 [0, 内容-视口] 里，图没放大时根本拖不动；纯变换想拖到哪都行（软限制见 ClampPanKeepVisible）。
    //   屏幕坐标 s = 倍数 × 内容坐标 p + 平移 t（以图片盒子左上角为原点）。
    private double _lightboxPanX, _lightboxPanY;
    private double _lightboxDragStartPanX, _lightboxDragStartPanY;
    private ScaleTransform? _lbScale;
    private TranslateTransform? _lbMove;
    private const double LightboxKeepVisible = 80.0;   // 拖动时图片至少还留这么多像素在视野里，免得拖丢

    // ── 独立大窗口（2.2.1）──
    // 放大层原本是主窗口里的一个浮层，永远被主窗口的边界裁住。现在打开时把整个浮层元素挪进一个
    // 无边框的全屏窗口（盖满主窗口所在的那块屏幕），关闭时再放回原处 —— 同一个元素、同一套逻辑
    // （翻页热区 / 箭头 / 拖动 / 缩放全部原样复用），只是换了个更大的容器。
    private Window? _lightboxViewer;
    private Panel? _lightboxHomePanel;
    private int _lightboxHomeIndex;

    /// <summary>
    /// 放大层是否放进独立窗口。自检 / 截图是"不显示窗口"的模式（见 App.IsTestFixtureRun），
    /// 绝不能弹出真窗口，所以那两种模式在启动时把它置为 false，沿用窗口内浮层的老路径。
    /// </summary>
    internal static bool LightboxInOwnWindow = true;
    // 「适应窗口盒子」的实际尺寸（LightboxImage 的显式 Width/Height），设置它的唯一出口是 FitImageBox。
    // 这里存一份是为了留给自检断言用（见 LightboxViewForTest）：光看控件属性分不清
    // "盒子还没量过、属性是上一轮的旧值"和"盒子就是这么大"，留痕才能把这一点钉住。
    private double _lightboxFitW, _lightboxFitH;
    private FrameworkElement? _lightboxHoverZone;   // 鼠标当前停在哪块热区；null = 都不在

    // ── 拖动与点击互斥的状态 ──
    // 三块热区的点击都绑在"按下"上，而"这一下到底是点击还是拖动"必须等抬起才知道，
    // 所以按下时不能直接执行，只能先记成待办，抬起时再按位移裁决：这就是下面这几个字段的用途。
    private int _lightboxPending;                   // 待办：0=没有；-1=上一张；+1=下一张；2=打开仓库
    private bool _lightboxDragActive;               // 本次按下是否仍在进行中（抬起或复位后为 false）
    private bool _lightboxDragMoved;                // 本次按下是否已够格算拖动（一旦为 true 就不再当点击）
    private Point _lightboxDragStart;               // 按下时的鼠标坐标（以 LightboxScroll 为参照）
    private const double LightboxDragThreshold = 5.0;   // 位移到此像素数才算拖动，小于它一律当点击

    // 三个处理器存成字段：用 AddHandler 挂的处理器必须用同一个委托实例才能精确摘掉。
    private MouseButtonEventHandler? _lightboxDragDownHandler;
    private MouseEventHandler? _lightboxDragMoveHandler;
    private MouseButtonEventHandler? _lightboxDragUpHandler;

    // ══════════════ 作者头像 ══════════════

    /// <summary>
    /// 插件作者头像（圆形）：从 GitHub 取图，复用同一套内存/磁盘缓存；
    /// 取不到或没有作者名时退化为「首字母色块」。返回后异步替换，不阻塞卡片渲染。
    /// </summary>
    private FrameworkElement BuildAuthorAvatar(string owner, double size = 18)
    {
        string name = (owner ?? "").Trim();
        var circle = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Background = new SolidColorBrush(AvatarColor(name)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        if (name.Length == 0)
        {
            circle.Visibility = Visibility.Collapsed;
            return circle;
        }

        circle.ToolTip = "作者：" + name;
        circle.Child = new TextBlock
        {
            Text = name.Substring(0, 1).ToUpperInvariant(),
            FontSize = size * 0.58,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        _ = LoadAvatarAsync(circle, $"https://github.com/{name}.png?size=64");
        return circle;
    }

    /// <summary>自检用：构建一个作者头像看看样式（不联网取图也能验证形状与占位）。</summary>
    internal FrameworkElement BuildAvatarForTest(string owner) => BuildAuthorAvatar(owner);

    /// <summary>取到头像图就贴上去（圆形由 Border 的 CornerRadius + ImageBrush 实现）。</summary>
    private async Task LoadAvatarAsync(Border circle, string url)
    {
        try
        {
            var bmp = await LoadImageAsync(url, full: true);
            if (bmp == null) return;
            circle.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
            circle.Child = null;
        }
        catch (Exception ex) { Logger.Log($"头像加载失败 {url}: {ex.Message}"); }
    }

    /// <summary>按用户名派生稳定的占位色（同一作者永远同色）。</summary>
    private static Color AvatarColor(string name)
    {
        if (name.Length == 0) return Color.FromRgb(0x48, 0x48, 0x4A);
        int h = 0;
        foreach (char c in name) h = (h * 31 + c) & 0x7FFFFFFF;
        Color[] palette =
        {
            Color.FromRgb(0x34, 0xC7, 0x59), Color.FromRgb(0x0A, 0x84, 0xFF),
            Color.FromRgb(0xFF, 0x9F, 0x0A), Color.FromRgb(0xFF, 0x37, 0x5F),
            Color.FromRgb(0x5A, 0xC8, 0xFA), Color.FromRgb(0xBF, 0x5A, 0xF2)
        };
        return palette[h % palette.Length];
    }

    // ══════════════ 下载 + 缓存 ══════════════

    /// <summary>取图（内存 → 磁盘 → 网络）；失败返回 null 并记录失败，避免反复重试。</summary>
    private async Task<BitmapSource?> LoadImageAsync(string url, bool full)
    {
        if (string.IsNullOrEmpty(url) || !PluginMarket.IsAllowedImageUrl(url)) return null;

        var mem = full ? _imgFull : _imgThumb;
        lock (mem) { if (mem.TryGetValue(url, out var hit)) return hit; }
        if (IsImageFailed(url)) return null;

        byte[]? bytes = null;
        string file = Path.Combine(ImageCacheDir, CacheKey(url));
        try { if (File.Exists(file)) bytes = await File.ReadAllBytesAsync(file); } catch { }

        if (bytes == null)
        {
            // 审计修正（2026-10-19，P2）：网络段整体挪进 FetchImageBytesAsync 走"单飞"——
            //   同一 URL 的并发调用共用同一个 Task，不再各下一遍。
            bytes = await FetchImageBytesAsync(url, file);
        }

        if (bytes == null)
        {
            NoteImageFailure(url);
            return null;
        }

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            if (!full) bmp.DecodePixelWidth = 260;      // 缩略图限制解码宽度，避免整图解码占用内存
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.EndInit();
            bmp.Freeze();
            lock (mem) { mem[url] = bmp; }
            return bmp;
        }
        catch (Exception ex)
        {
            Logger.Log($"解码图片失败 {url}: {ex.Message}");
            NoteImageFailure(url);
            return null;
        }
    }

    /// <summary>
    /// 同一 URL 的取图合流：并发的第二个调用者直接复用第一个的 Task（返回同一份字节）。
    /// 表项在任务结束后立刻摘除（无论成败）——失败不留在表里，否则 TTL 重试就永远轮不到。
    /// </summary>
    private Task<byte[]?> FetchImageBytesAsync(string url, string file)
    {
        lock (_imgInflight)
        {
            if (_imgInflight.TryGetValue(url, out var running)) return running;
            Task<byte[]?> fresh = FetchImageBytesCoreAsync(url, file);
            _imgInflight[url] = fresh;
            _ = fresh.ContinueWith(_ => { lock (_imgInflight) { _imgInflight.Remove(url); } },
                                   System.Threading.Tasks.TaskScheduler.Default);
            return fresh;
        }
    }

    /// <summary>真正的网络取图：占 _imgGate 的一路并发，进门后复查磁盘，逐路由尝试并落盘。</summary>
    private async Task<byte[]?> FetchImageBytesCoreAsync(string url, string file)
    {
        await _imgGate.WaitAsync();
        try
        {
            // 等锁期间前一个调用者可能刚把文件写好（或自己就是被合流的那一个没赶上）：进门先复查磁盘，
            // 有就不必再走网络。这一句是"合流表还没建立、但文件已存在"窗口期的正确兜底。
            try { if (File.Exists(file)) return await File.ReadAllBytesAsync(file); } catch { }

            byte[]? bytes = null;
            foreach (string route in PluginMarket.ImageRoutes(url))
            {
                try
                {
                    using var resp = await ImgHttp.GetAsync(route, HttpCompletionOption.ResponseHeadersRead);
                    if (!resp.IsSuccessStatusCode) continue;
                    byte[] got = await resp.Content.ReadAsByteArrayAsync();
                    if (got.Length < 64) continue;
                    // 代理失败时可能返回 HTML 内容，按魔数过滤
                    if (got[0] == '<') continue;
                    bytes = got;
                    break;
                }
                catch (Exception ex) { Logger.Log($"取图失败 {route}: {ex.Message}"); }
            }

            if (bytes != null)
            {
                try
                {
                    Directory.CreateDirectory(ImageCacheDir);
                    await File.WriteAllBytesAsync(file, bytes);
                    // 清理放在写盘之后：先保证用户正在等的这张图已经落地，再做维护。
                    // 不 await、丢后台跑：此刻用户正等在图片加载路径上，清理要遍历目录、删文件（几百个文件同步几毫秒，
                    // 但磁盘可能更慢），await 会把这份开销加到本次图片显示上。用 Task.Run 让清理在后台线程跑，
                    // 不占用 UI 线程、不阻塞取图；_imgTrimRunning 保证同一时刻只有一个清理在跑，
                    // 避免用户快速浏览时堆出大量并发清扫。把 file 传进去，让清理显式跳过刚写盘的这个文件。
                    TrimImageCacheInBackground(file);
                }
                catch { }
            }
            return bytes;
        }
        finally { _imgGate.Release(); }
    }

    /// <summary>失败登记（带 TTL 与条目上限）；调用方持锁的旧写法已废弃。</summary>
    private void NoteImageFailure(string url)
    {
        lock (_imgFailed)
        {
            _imgFailed[url] = DateTime.UtcNow;
            if (_imgFailed.Count > ImageFailMaxEntries)
            {
                // 先按过期时间清一轮；仍超上限就把最旧的一批删掉。
                DateTime cutoff = DateTime.UtcNow - ImageFailTtl;
                foreach (string k in _imgFailed.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
                    _imgFailed.Remove(k);
                if (_imgFailed.Count > ImageFailMaxEntries)
                {
                    foreach (string k in _imgFailed.OrderBy(kv => kv.Value)
                                                  .Take(_imgFailed.Count - ImageFailMaxEntries)
                                                  .Select(kv => kv.Key).ToList())
                        _imgFailed.Remove(k);
                }
            }
        }
    }

    /// <summary>该 URL 是否处于失败冷却期内（过期即视为可重试，并从表里摘掉）。</summary>
    private bool IsImageFailed(string url)
    {
        lock (_imgFailed)
        {
            if (!_imgFailed.TryGetValue(url, out DateTime at)) return false;
            if (DateTime.UtcNow - at < ImageFailTtl) return true;
            _imgFailed.Remove(url);   // 冷却期已过：允许重试
            return false;
        }
    }

    private static string CacheKey(string url)
    {
        byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(url));
        return Convert.ToHexString(hash).ToLowerInvariant() + ".img";
    }

    /// <summary>
    /// 图片缓存自动上限（字节）。取 256MB 的理由：本机实测 Cache\Images 已积累 675 个文件 / 约 125.8MB，
    /// 说明原先只增不减、用户浏览市场越久占用越大；256MB 约为实测值的一倍余量，既容得下正常浏览产生的图片，
    /// 又能在长期使用后自动收敛，不会把磁盘吃满。用 long 而不是 int：字节量级天然适合 long，
    /// 以后调大上限（如 1GB）也不会溢出。
    /// </summary>
    private const long ImageCacheMaxBytes = 256L * 1024 * 1024;

    /// <summary>0 表示没有清理在跑，1 表示有；用于保证同一时刻只有一个清理在后台跑。</summary>
    private static int _imgTrimRunning;

    /// <summary>
    /// 把 ImageCacheDir 下的 .img 缓存总量收敛到上限以内，返回本次释放的字节数（供日志）。
    /// 策略：按 LastAccessTimeUtc 升序（最久未用在前）逐个删，直到降到上限的 80%。
    /// 为什么按 LastAccessTime 而不是创建时间：市场图片的复用取决于"最近还看没看过"，
    /// 很久没被读取的缩略图才是真正可以牺牲的；按创建时间删会误删最近仍在频繁访问的图。
    /// 为什么降到 80% 而不是刚好 100%：若只削到 100%，下一张图写入就立刻再次超限、又触发一次全目录扫描，
    /// 退化成"写一张扫一次"的抖动；一次多削 20% 可换来一段安静期。
    /// 这个 80% 同时是"刚写入的文件不会被删"的第一重保护：它刚被写入，LastAccessTimeUtc 是所有文件里最大的，
    /// 排序后天然排最后，正常降不到它就已经停下。但仅靠排序不够：若单张图本身就超过上限（代码只过滤了 < 64 字节的
    /// 响应，没有上限），它自己就是唯一的候选，排序保护会失效。因此再加一重显式保护：keepFile 传入调用方刚写盘的
    /// 文件，循环里直接跳过，绝不删除。有此两重，本方法在任何情况下都不会删掉刚写进去的那个文件。
    /// 本方法绝不抛异常：任何失败只记 Logger.NoteDiagnosis（Logger.Log / Logger.LogDiagnosis 是空实现，
    /// 不会真正落盘）。
    /// </summary>
    /// <param name="keepFile">调用方刚写入的缓存文件全路径，永不删除；可为 null。</param>
    private long TrimImageCacheOverLimit(string? keepFile = null)
    {
        long freed = 0;
        try
        {
            var dir = new DirectoryInfo(ImageCacheDir);
            if (!dir.Exists) return 0;

            FileInfo[] files = dir.GetFiles("*.img");
            long total = 0;
            foreach (FileInfo f in files) { try { total += f.Length; } catch { } }
            if (total <= ImageCacheMaxBytes) return 0;

            // 见方法注释：多削 20%，避免"写一张就触发一次清理"的抖动
            long target = (long)(ImageCacheMaxBytes * 0.8);
            Array.Sort(files, (a, b) => a.LastAccessTimeUtc.CompareTo(b.LastAccessTimeUtc));

            foreach (FileInfo f in files)
            {
                if (total <= target) break;
                // 显式保护刚写盘的文件（见方法注释第二重保护）；跳过它继续删别的，不 break
                if (keepFile != null && string.Equals(f.FullName, keepFile, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    long len = f.Length;
                    f.Delete();   // 被占用 / 权限不足会抛，交给下面的 catch
                    total -= len;
                    freed += len;
                }
                catch { }         // 删不掉就跳过继续，维护动作绝不打断取图
            }

            if (freed > 0)
            {
                Logger.NoteDiagnosis($"图片缓存超限自动清理：释放 {freed / 1024 / 1024}MB，当前 {total / 1024 / 1024}MB（上限 {ImageCacheMaxBytes / 1024 / 1024}MB）");
            }
        }
        catch (Exception ex)
        {
            try { Logger.NoteDiagnosis($"图片缓存自动清理失败（已忽略）：{ex.Message}"); } catch { }
        }
        return freed;
    }

    /// <summary>
    /// 后台触发一次缓存清理（fire-and-forget）。不 await：调用点正处在图片加载路径上，用户正在等这张图，
    /// 维护动作不该占用它的时间。用 Interlocked 做重入保护：若已有清理在跑就直接返回，
    /// 避免用户快速翻看市场时堆出多个并发清扫（它们会互相抢删同一批文件，浪费 IO）。
    /// 有意的 fire-and-forget，所以前置 _ = 丢弃 Task；方法内部已全量吞异常，不会有未观察的异常。
    /// </summary>
    private void TrimImageCacheInBackground(string? keepFile = null)
    {
        if (Interlocked.CompareExchange(ref _imgTrimRunning, 1, 0) != 0) return;
        _ = Task.Run(() =>
        {
            try { TrimImageCacheOverLimit(keepFile); }
            catch { }   // 双保险：维护动作绝不冒泡到线程池
            finally { Interlocked.Exchange(ref _imgTrimRunning, 0); }
        });
    }

    /// <summary>清除取图失败记录（点击刷新时调用，使之前未取到的图片可重新下载）。</summary>
    private void ResetImageFailures()
    {
        lock (_imgFailed) { _imgFailed.Clear(); }
    }

    /// <summary>清除内存中已解码的图片（清理缓存后调用，避免继续显示已删除的缓存图）。</summary>
    private void ResetImageMemoryCache()
    {
        lock (_imgThumb) { _imgThumb.Clear(); }
        lock (_imgFull) { _imgFull.Clear(); }
    }

    // ══════════════ 卡片上的缩略图条 ══════════════

    private const double ThumbW = 84, ThumbH = 54;

    /// <summary>一条收录的截图条（最多 3 张缩略图；超出时在最后一张上叠加 +N）。</summary>
    private FrameworkElement BuildThumbStrip(PluginMarket.MarketPlugin m)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var shown = m.Screenshots.Take(3).ToList();

        for (int i = 0; i < shown.Count; i++)
        {
            int index = i;
            string url = shown[i];

            var img = new Image
            {
                Stretch = Stretch.UniformToFill,
                Width = ThumbW,
                Height = ThumbH,
                SnapsToDevicePixels = true
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

            var holder = new Border
            {
                Width = ThumbW,
                Height = ThumbH,
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
                Margin = new Thickness(0, 0, 6, 0),
                Cursor = Cursors.Hand,
                ClipToBounds = true
                // 这里以前挂过「点开放大」悬停气泡：一旦用"置空 ToolTip"的办法去隐藏它，
                // 屏幕上会残留一个没展开的白色小方框（现场 bug），因此整体移除——
                // 缩略图本身就带手型光标与悬停浮出预览，不需要文字提示。
            };

            // img 只能有一个父节点：先加入 Grid 再赋给 holder.Child 会抛出"指定的元素已经是另一个元素的逻辑子元素"。
            if (i == shown.Count - 1 && m.Screenshots.Count > shown.Count)
            {
                var grid = new Grid();
                grid.Children.Add(img);
                grid.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0x99, 0x00, 0x00, 0x00)),
                    Child = new TextBlock
                    {
                        Text = $"+{m.Screenshots.Count - shown.Count}",
                        FontSize = 12,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = Brushes.White,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                });
                holder.Child = grid;
            }
            else
            {
                holder.Child = img;
            }

            holder.MouseLeftButtonDown += (_, _) => ShowImageLightbox(m, index);
            WireThumbHover(holder, url);
            row.Children.Add(holder);

            _ = FillThumbAsync(img, holder, url);
        }
        return row;
    }

    private async Task FillThumbAsync(Image target, Border holder, string url)
    {
        var bmp = await LoadImageAsync(url, full: false);
        if (bmp == null)
        {
            holder.Child = new TextBlock
            {
                Text = "图片加载失败",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "这张图未能取到（可能是网络或仓库改路径）"
            };
            return;
        }
        if (target.Parent == null && target != holder.Child) return;   // 卡片已被重渲染
        target.Source = bmp;
    }

    // ══════════════ 悬停预览（约半屏，看清个大概；点进去才是放大层看全图） ══════════════
    private DispatcherTimer? _thumbPreviewTimer;
    private Border? _thumbPreviewAnchor;


    /// <summary>鼠标悬停在缩略图上：延迟片刻后显示预览（仅划过时不闪现）。</summary>
    private void WireThumbHover(Border holder, string url)
    {
        holder.MouseEnter += (_, _) =>
        {
            HideThumbPreview();
            _thumbPreviewAnchor = holder;
            _thumbPreviewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
            _thumbPreviewTimer.Tick += (_, _) =>
            {
                _thumbPreviewTimer?.Stop();
                _ = ShowThumbPreviewAsync(holder, url);
            };
            _thumbPreviewTimer.Start();
        };
        holder.MouseMove += (_, e) => { if (Content is FrameworkElement r) FollowThumbPreview(e.GetPosition(r)); };
        holder.MouseLeave += (_, _) => HideThumbPreview();
    }

    /// <summary>光标在缩略图上挪动时，预览跟着走（像贴在光标边上，不是钉死在缩略图旁边）。</summary>
    internal void FollowThumbPreview(Point cursor)
    {
        if (ThumbPreview.Visibility != Visibility.Visible) return;
        PositionThumbPreview(cursor);
    }

    private async Task ShowThumbPreviewAsync(Border holder, string url)
    {
        try
        {
            if (!ReferenceEquals(_thumbPreviewAnchor, holder)) return;      // 等待期间已经移开
            if (ImageLightbox.Visibility == Visibility.Visible) return;     // 放大层开着就不再浮预览
            var bmp = await LoadImageAsync(url, full: true) ?? await LoadImageAsync(url, full: false);
            if (bmp == null) return;
            if (!ReferenceEquals(_thumbPreviewAnchor, holder)) return;      // 取图期间移开了
            ShowThumbPreview(bmp, holder);
        }
        catch (Exception ex) { Logger.LogError("ShowThumbPreview", ex); }
    }

    /// <summary>浮出预览：约界面一半大小（上限 560×380），贴着光标，且整块留在窗口内。</summary>
    internal void ShowThumbPreview(BitmapSource bmp, Border holder)
    {
        try
        {
            ThumbPreviewImage.Source = bmp;
            ThumbPreview.Width = Math.Max(320, Math.Min(560, ActualWidth * 0.5));
            ThumbPreview.Height = Math.Max(200, Math.Min(380, ActualHeight * 0.5));
            ThumbPreview.Visibility = Visibility.Visible;
            // 跟着鼠标走：贴着当前光标浮出；光标不在窗口里（无鼠标输入）就退回缩略图旁边
            PositionThumbPreview(CursorInWindow() ? Mouse.GetPosition((IInputElement)Content) : AnchorOf(holder));
            _thumbPreviewAnchor = holder;
        }
        catch (Exception ex) { Logger.LogError("ShowThumbPreview(apply)", ex); }
    }

    /// <summary>光标是否落在窗口内（自检等无鼠标场景下用来决定贴光标还是贴缩略图）。</summary>
    private bool CursorInWindow()
    {
        if (Content is not FrameworkElement root || root.ActualWidth <= 0) return false;
        Point p = Mouse.GetPosition(root);
        return p.X >= 0 && p.Y >= 0 && p.X <= root.ActualWidth && p.Y <= root.ActualHeight;
    }

    /// <summary>缩略图左上角在窗口内的位置；算不出来时退回左上角内缩处，绝不把预览丢在 (0,0)。</summary>
    private Point AnchorOf(Border holder)
    {
        try
        {
            if (Content is FrameworkElement root) return holder.TransformToAncestor(root).Transform(new Point(0, 0));
        }
        catch { }
        return new Point(8, 8);
    }

    /// <summary>把预览摆到光标右下 18px；右边/下边放不下就翻到另一侧，最后夹进窗口内。</summary>
    private void PositionThumbPreview(Point cursor)
    {
        double left = 8, top = 8;
        try
        {
            if (Content is FrameworkElement root)
            {
                double w = ThumbPreview.Width, h = ThumbPreview.Height;
                const double gap = 18;
                left = cursor.X + gap;
                top = cursor.Y + gap;
                if (left + w > root.ActualWidth - 8) left = cursor.X - w - gap;      // 右边放不下 → 翻到光标左侧
                if (top + h > root.ActualHeight - 8) top = cursor.Y - h - gap;      // 下边放不下 → 翻到光标上方
                left = Math.Max(8, Math.Min(left, Math.Max(8, root.ActualWidth - w - 8)));
                top = Math.Max(8, Math.Min(top, Math.Max(8, root.ActualHeight - h - 8)));
            }
        }
        catch { }
        ThumbPreview.Margin = new Thickness(left, top, 0, 0);
    }

    /// <summary>收起悬停预览：移开缩略图、切页、最小化/收托盘、打开放大层时都会调。</summary>
    internal void HideThumbPreview()
    {
        try
        {
            _thumbPreviewAnchor = null;
            _thumbPreviewTimer?.Stop();
            _thumbPreviewTimer = null;
            if (ThumbPreview != null) ThumbPreview.Visibility = Visibility.Collapsed;
        }
        catch { }
    }

    /// <summary>自检用：悬停预览的当前状态（可见 / 尺寸 / 是否吃鼠标 / 有没有图 / 位置）。</summary>
    internal (bool Visible, double W, double H, bool HitTestVisible, bool HasSource, double Left, double Top)
        ThumbPreviewForTest()
        => (ThumbPreview?.Visibility == Visibility.Visible,
            ThumbPreview?.Width ?? 0,
            ThumbPreview?.Height ?? 0,
            ThumbPreview?.IsHitTestVisible ?? true,
            ThumbPreviewImage?.Source != null,
            ThumbPreview?.Margin.Left ?? 0,
            ThumbPreview?.Margin.Top ?? 0);

    /// <summary>目录未收录截图时，卡片显示「加载图片」按钮，点击后从 README 抓取。</summary>
    private Border BuildScrapeChip(PluginMarket.MarketPlugin m)
    {
        var chip = new Border
        {
            CornerRadius = new CornerRadius(13),
            Padding = new Thickness(10, 4, 12, 4),
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = Cursors.Hand,
            Background = new SolidColorBrush(Color.FromArgb(0x1F, 0x5A, 0xC8, 0xFA)),   // 淡蓝底：突出该按钮但弱于主操作
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x59, 0x5A, 0xC8, 0xFA)),
            BorderThickness = new Thickness(1),
            ToolTip = "点击查看仓库中的截图",
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock
                    {
                        Text = "\uE8B9",                       // 图片小图标
                        FontFamily = new FontFamily("Segoe MDL2 Assets"),
                        FontSize = 11,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 6, 0),
                        Foreground = new SolidColorBrush(Color.FromRgb(0x5A, 0xC8, 0xFA))
                    },
                    new TextBlock
                    {
                        Text = "加载图片",
                        FontSize = 11.5,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = new SolidColorBrush(Color.FromRgb(0xBF, 0xE3, 0xFB))
                    }
                }
            }
        };
        chip.MouseLeftButtonDown += async (_, _) => await ScrapeAndShowAsync(m, chip);
        return chip;      // 不要再套一层容器，否则会抛出"已是另一个元素的子元素"
    }

    private async Task ScrapeAndShowAsync(PluginMarket.MarketPlugin m, Border chip)
    {
        try
        {
            SetChipText(chip, "正在加载图片…");
            var urls = m.Screenshots.Count > 0
                ? m.Screenshots
                : await PluginMarket.ScrapeReadmeImagesAsync(m.Owner, RepoNameOf(m));
            m.ReadmeScraped = true;

            if (urls.Count == 0)
            {
                SetChipText(chip, "未找到图片");
                return;
            }
            foreach (string u in urls) if (!m.Screenshots.Contains(u)) m.Screenshots.Add(u);

            chip.Visibility = Visibility.Collapsed;
            ShowImageLightbox(m, 0);
            RenderMarket(preserveScroll: true);      // 让卡片上也出现缩略图条
        }
        catch (Exception ex)
        {
            Logger.LogError("ScrapeAndShowAsync", ex);
            SetChipText(chip, "加载失败");
        }
    }

    /// <summary>更新「加载图片」按钮上的文字（图标不变）。</summary>
    private static void SetChipText(Border chip, string text)
    {
        if (chip.Child is not StackPanel sp) return;
        for (int i = sp.Children.Count - 1; i >= 0; i--)
            if (sp.Children[i] is TextBlock tb && !tb.FontFamily.Source.Contains("MDL2"))
            {
                tb.Text = text;
                return;
            }
    }

    private static string RepoNameOf(PluginMarket.MarketPlugin m)
    {
        if (m.Owner.Length > 0 && m.Name.Length > 0) return m.Name;
        try { return new Uri(m.RepoUrl).Segments[^1].TrimEnd('/'); } catch { return m.Name; }
    }

    // ══════════════ 放大查看层 ══════════════

    // ── 看图层的缩放与热区 ──
    // 缩放 / 平移走 RenderTransform（不碰 ScrollViewer 的滚动位置，可以超出舞台边界自由拖动）。
    // 图片盒子 = 舞台大小；初始以 LightboxFitRatio 的比例缩小并居中，像普通看图软件那样四周留出空白。
    // 屏幕坐标 s = 倍数·比例·p + 平移 + 居中偏移，其中居中偏移 k = (1 − 比例)·盒子 / 2。
    /// <summary>初始显示比例的上限：图片最多占舞台的 80%，四周留白（点留白关闭）。放大后可以铺满整块舞台。</summary>
    internal const double LightboxFitRatio = 0.80;
    private double _lightboxRatio = LightboxFitRatio;

    /// <summary>
    /// 这张图的初始显示比例（纯函数）：不超过 80%，且小图不放大到超过原始像素尺寸（但不低于 40%）。
    /// fit = 图片在盒子里 Uniform 的缩放比；比例 = 1 / fit 时恰好是原始尺寸。
    /// </summary>
    internal static double InitialRatio(double boxW, double boxH, double srcW, double srcH)
    {
        if (!(boxW > 0) || !(boxH > 0) || !(srcW > 0) || !(srcH > 0)) return LightboxFitRatio;
        double fit = Math.Min(boxW / srcW, boxH / srcH);
        return Math.Clamp(1.0 / fit, 0.4, LightboxFitRatio);
    }

    /// <summary>按当前图片和盒子重算初始显示比例（换图、舞台尺寸变化时调用）。</summary>
    private void RecomputeLightboxRatio()
    {
        var src = LightboxImage.Source;
        _lightboxRatio = src == null
            ? LightboxFitRatio
            : InitialRatio(_lightboxFitW, _lightboxFitH, src.Width, src.Height);
    }
    private const double ZoomMin = 0.2;      // 缩放下限（再小就看不清了）
    private const double ZoomBase = 1.1;     // 滚轮每 120 单位（一格）的缩放系数
    private const double ZoomMax = 16.0;     // 缩放上限（2.2.1 由 8 提到 16：独立大窗口里看细节需要）

    /// <summary>
    /// 由"当前倍数 + 滚轮增量"算出新倍数。纯函数，与界面无关 ⇒ 可单独自检。
    /// 取指数关系是为了让幅度成比例：+240（两格）恰好是 +120（一格）的两倍；
    /// 若写成线性累加（current + delta * k），那只是"增量两倍"，倍率上并不成立。
    /// </summary>
    internal static double NextZoom(double current, int delta)
    {
        if (delta == 0) return current;      // 零增量原样返回：免得浮点算一遍反而抖动
        return Math.Min(ZoomMax, Math.Max(ZoomMin, current * Math.Pow(ZoomBase, delta / 120.0)));
    }

    /// <summary>
    /// 以光标为锚点缩放后的新平移量（单轴，纯函数，与界面无关 ⇒ 可单独自检）。
    /// 屏幕坐标 s = z·p + t；要让光标 c 下面的那个内容点 p 在缩放前后都落在 c 下：
    ///   p = (c − t) / z，t′ = c − z′·p = c − (c − t)·z′/z。
    /// 倍数或坐标非法（≤0 / 非有限）就原样返回，不锚定。
    /// 2.2.1 起取代了旧的 ZoomAnchorOffset（那一版是按 ScrollViewer 的滚动偏移 / extent / viewport 算的）。
    /// </summary>
    internal static double ZoomAboutPan(double cursor, double pan, double oldZoom, double newZoom)
    {
        if (!double.IsFinite(cursor) || !double.IsFinite(pan)) return pan;
        if (oldZoom <= 0 || newZoom <= 0) return pan;
        return cursor - (cursor - pan) * (newZoom / oldZoom);
    }

    /// <summary>
    /// 拖动的软限制（纯函数）：「随意拖动」，但不能把图拖得一点不剩 ——
    /// 保证图片至少还有 <paramref name="keep"/> 像素留在视野里。图或视野比 keep 还小就按小的那个算。
    /// boxSize = 未缩放的图片盒子边长，viewport = 视野边长，二者任一非法就不限制（原样返回）。
    /// </summary>
    internal static double ClampPanKeepVisible(double pan, double boxSize, double zoom, double viewport, double keep)
    {
        double scaled = boxSize * zoom;
        if (!(scaled > 0) || !(viewport > 0) || !double.IsFinite(pan)) return pan;
        double k = Math.Min(keep, Math.Min(scaled, viewport));
        double min = k - scaled;        // 图片右 / 下缘至少还剩 k 在视野里
        double max = viewport - k;      // 图片左 / 上缘至少还剩 k 在视野里
        return Math.Max(min, Math.Min(max, pan));
    }

    /// <summary>居中偏移 k：初始缩小后让图片仍居中所需的平移（盒子尺寸未知时为 0）。</summary>
    private (double X, double Y) LightboxCenterShift()
        => (_lightboxFitW > 0 ? (1 - _lightboxRatio) * _lightboxFitW / 2 : 0,
            _lightboxFitH > 0 ? (1 - _lightboxRatio) * _lightboxFitH / 2 : 0);

    /// <summary>
    /// 把倍数与平移落到图片上，并同步热区。图片元素的尺寸直接设成“适应舞台后的图片大小”，
    /// 位置与缩放全由 RenderTransform（原点是元素左上角）决定，显示范围与 <see cref="ImageDisplayRect"/> 完全一致，
    /// 热区、箭头也按同一个范围计算，不再依赖 Image 控件自己的居中规则。
    /// 不在这里把接近 1.0 的倍数归零：连续缩放要经过 1.0，归零会拽回用户已拖好的位置；复位只走显式复位路径。
    /// </summary>
    private void ApplyLightboxZoom()
    {
        if (_lbScale == null || _lbMove == null || LightboxImage.RenderTransform is not TransformGroup)
        {
            _lbScale = new ScaleTransform(1.0, 1.0);
            _lbMove = new TranslateTransform(0.0, 0.0);
            var g = new TransformGroup();
            g.Children.Add(_lbScale);
            g.Children.Add(_lbMove);
            LightboxImage.RenderTransformOrigin = new Point(0, 0);
            LightboxImage.RenderTransform = g;
            LightboxImage.LayoutTransform = null;
        }
        FitImageBox();                 // 记录当前舞台尺寸
        double e = _lightboxZoom * _lightboxRatio;
        var src = LightboxImage.Source;
        if (src != null && src.Width > 0 && src.Height > 0 && _lightboxFitW > 0 && _lightboxFitH > 0)
        {
            var r = ImageDisplayRect(_lightboxFitW, _lightboxFitH, src.Width, src.Height,
                                     _lightboxZoom, _lightboxRatio, _lightboxPanX, _lightboxPanY);
            double s0 = Math.Min(_lightboxFitW / src.Width, _lightboxFitH / src.Height);
            LightboxImage.Width = src.Width * s0;
            LightboxImage.Height = src.Height * s0;
            _lbMove.X = r.Left;
            _lbMove.Y = r.Top;
        }
        else
        {
            var k = LightboxCenterShift();
            if (_lightboxFitW > 0) LightboxImage.Width = _lightboxFitW;
            if (_lightboxFitH > 0) LightboxImage.Height = _lightboxFitH;
            _lbMove.X = _lightboxPanX + k.X;
            _lbMove.Y = _lightboxPanY + k.Y;
        }
        _lbScale.ScaleX = e;
        _lbScale.ScaleY = e;
        UpdateLightboxZoomText();
        UpdateLightboxZones();
    }

    /// <summary>底栏右侧的补充信息：没动过视图时给操作提示，缩放 / 平移过就显示倍数与复位提示。</summary>
    private void UpdateLightboxZoomText()
    {
        try
        {
            if (LightboxCounter == null) return;
            if (LightboxImage.Source == null) { LightboxCounter.Text = ""; return; }
            bool untouched = Math.Abs(_lightboxZoom - 1.0) < 0.005
                          && Math.Abs(_lightboxPanX) < 0.5 && Math.Abs(_lightboxPanY) < 0.5;
            LightboxCounter.Text = untouched
                ? "滚轮缩放 · 双击放大 · 点空白处关闭"
                : $"{_lightboxZoom * 100:0}% · 按 0 复位";
        }
        catch { }
    }

    /// <summary>把平移量夹进软限制（见 ClampPanKeepVisible）：按图片实际显示的范围算，至少留一小块在视野里。</summary>
    private void ClampLightboxPan()
    {
        var src = LightboxImage.Source;
        double bw = _lightboxFitW, bh = _lightboxFitH;
        if (!(bw > 0) || !(bh > 0)) return;
        double sw = src != null && src.Width > 0 ? src.Width : bw;
        double sh = src != null && src.Height > 0 ? src.Height : bh;
        var r = ImageDisplayRect(bw, bh, sw, sh, _lightboxZoom, _lightboxRatio, _lightboxPanX, _lightboxPanY);
        double left = ClampPanKeepVisible(r.Left, r.Width, 1.0, LightboxStage.ActualWidth, LightboxKeepVisible);
        double top = ClampPanKeepVisible(r.Top, r.Height, 1.0, LightboxStage.ActualHeight, LightboxKeepVisible);
        _lightboxPanX += left - r.Left;
        _lightboxPanY += top - r.Top;
    }

    /// <summary>
    /// 由舞台的可视尺寸算出图片盒子的边长（纯函数）。XAML 里 ScrollViewer 两向 Hidden，内容按无限约束量算，
    /// Uniform 图片会按原始像素渲染；给 Image 一个显式等于舞台的盒子，Uniform 才会在盒子里等比缩放并居中。
    /// 任一入参 &lt;= 0 或非有限 ⇒ (0,0)，表示尺寸还没拿到，调用方跳过设置。
    /// </summary>
    internal static (double W, double H) FitBox(double stageW, double stageH)
    {
        if (!double.IsFinite(stageW) || !double.IsFinite(stageH)) return (0, 0);
        if (stageW <= 0 || stageH <= 0) return (0, 0);
        return (stageW, stageH);
    }

    /// <summary>记录当前舞台尺寸（图片盒子）；尺寸没拿到就保持原状，不写 0。图片元素的实际大小在 ApplyLightboxZoom 里设。</summary>
    private void FitImageBox()
    {
        try
        {
            var (w, h) = FitBox(LightboxStage.ActualWidth, LightboxStage.ActualHeight);
            if (w <= 0 || h <= 0) return;
            _lightboxFitW = w;
            _lightboxFitH = h;
        }
        catch (Exception ex) { Logger.LogError("FitImageBox", ex); }
    }

    /// <summary>
    /// 看图层的缩放复位。换图、关层都要复位：新图沿用上一张的倍数会一开就糊成一片。
    /// 同时清掉悬停态、拖动状态和未执行的待办，避免新图上凭空翻页或被当成拖到一半。
    /// </summary>
    private void ResetLightboxZoom(bool mustReset)
    {
        _lightboxZoom = 1.0;
        _lightboxPanX = 0;              // 平移跟着一起归零（2.2.1：换图 / 关层 / 按 0 都回到整图居中）
        _lightboxPanY = 0;
        _lightboxHoverZone = null;
        _lightboxDragActive = false;
        _lightboxDragMoved = false;
        _lightboxPending = 0;
        if (LightboxStage.IsMouseCaptured) LightboxStage.ReleaseMouseCapture();
        LightboxImage.LayoutTransform = null;
        ApplyLightboxZoom();
        ResetLightboxScroll();
        UpdateLightboxArrows();
    }

    /// <summary>滚动位置恒为 0，这里显式归零作保险。</summary>
    private void ResetLightboxScroll()
    {
        LightboxScroll.ScrollToHorizontalOffset(0);
        LightboxScroll.ScrollToVerticalOffset(0);
    }

    /// <summary>滚轮缩放（不需要按 Ctrl）。挂在舞台上：热区铺满舞台，事件不会横向传给兄弟节点 ScrollViewer。</summary>
    private void LightboxScroll_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        ZoomLightboxAt(e.GetPosition(LightboxScroll), e.Delta);
        e.Handled = true;
    }

    /// <summary>以 cursor（相对舞台左上角）为锚点缩放；滚轮、键盘 + / − 与自检都走这个入口。倍数没变就什么都不做。</summary>
    internal void ZoomLightboxAt(Point cursor, int delta)
    {
        double before = _lightboxZoom;
        double after = NextZoom(before, delta);
        if (Math.Abs(after - before) < 1e-9) return;

        var k = LightboxCenterShift();
        _lightboxPanX = ZoomAboutPan(cursor.X - k.X, _lightboxPanX, before, after);
        _lightboxPanY = ZoomAboutPan(cursor.Y - k.Y, _lightboxPanY, before, after);
        _lightboxZoom = after;
        ClampLightboxPan();
        ApplyLightboxZoom();
    }

    /// <summary>回到整图适应窗口、居中（按 0 / Home）。</summary>
    private void ResetLightboxView()
    {
        _lightboxZoom = 1.0;
        _lightboxPanX = 0;
        _lightboxPanY = 0;
        ApplyLightboxZoom();
    }

    /// <summary>舞台尺寸变了：图片盒子与热区都要跟着重算，否则图被裁掉或热区错位。</summary>
    private void LightboxStage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        FitImageBox();
        RecomputeLightboxRatio();
        ApplyLightboxZoom();         // 内含热区与边框的重算
        UpdateLightboxArrows();
    }

    private void LightboxZone_MouseEnter(object sender, MouseEventArgs e)
    {
        _lightboxHoverZone = sender as FrameworkElement;
        UpdateLightboxArrows();
    }

    private void LightboxZone_MouseLeave(object sender, MouseEventArgs e)
    {
        _lightboxHoverZone = null;
        UpdateLightboxArrows();
    }

    /// <summary>箭头显隐的唯一判据：鼠标停在左 / 右热区上，并且那个方向确实还有图。到头了连热区一起关掉。</summary>
    private void UpdateLightboxArrows()
    {
        bool hasPrev = _lightboxIndex > 0;
        bool hasNext = _lightboxIndex < _lightboxUrls.Count - 1;
        bool showLeft = hasPrev && ReferenceEquals(_lightboxHoverZone, LightboxZoneLeft);
        bool showRight = hasNext && ReferenceEquals(_lightboxHoverZone, LightboxZoneRight);

        // 箭头与渐变遮罩一起做短过渡：遮罩负责"这一侧还有图可翻"的视觉暗示，
        // 单独动箭头会让遮罩突然亮起或突然消失，与箭头的出现节奏对不上。
        FadeLightboxElement(LightboxArrowLeft, showLeft ? 1.0 : 0.0);
        FadeLightboxElement(LightboxShadeLeft, showLeft ? 1.0 : 0.0);
        FadeLightboxElement(LightboxArrowRight, showRight ? 1.0 : 0.0);
        FadeLightboxElement(LightboxShadeRight, showRight ? 1.0 : 0.0);
        LightboxZoneLeft.IsHitTestVisible = hasPrev;      // 到头了就连热区一起关掉，点了也没动作
        LightboxZoneRight.IsHitTestVisible = hasNext;
    }

    /// <summary>
    /// 箭头与渐变遮罩的短淡入淡出（约 150ms）。动画用 FillBehavior.Stop，并紧接着把属性写成终值，
    /// 所以快速划过时不会卡在过渡中间；起点取当前不透明度，来回悬停是从眼前这一帧接着淡。
    /// </summary>
    private static void FadeLightboxElement(UIElement el, double to)
    {
        double from = el.Opacity;
        var anim = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(150))
        {
            FillBehavior = FillBehavior.Stop
        };
        el.BeginAnimation(UIElement.OpacityProperty, anim);
        el.Opacity = to;
    }

    /// <summary>
    /// 图片当前在舞台里的显示范围（纯函数）。boxW/boxH = 图片盒子（等于舞台），src = 图片像素尺寸；
    /// 图片在盒子里 Uniform 居中，再乘以 倍数·比例 并平移。
    /// </summary>
    internal static Rect ImageDisplayRect(double boxW, double boxH, double srcW, double srcH,
                                          double zoom, double ratio, double panX, double panY)
    {
        double s0 = Math.Min(boxW / srcW, boxH / srcH);
        double iw = srcW * s0, ih = srcH * s0;
        double e = zoom * ratio;
        double left = (1 - ratio) * boxW / 2 + panX + e * (boxW - iw) / 2;
        double top = (1 - ratio) * boxH / 2 + panY + e * (boxH - ih) / 2;
        return new Rect(left, top, e * iw, e * ih);
    }

    private static void SetStarSizes(Grid grid, double[] cols, double[] rows)
    {
        for (int i = 0; i < cols.Length && i < grid.ColumnDefinitions.Count; i++)
            grid.ColumnDefinitions[i].Width = new GridLength(Math.Max(0, cols[i]), GridUnitType.Star);
        for (int i = 0; i < rows.Length && i < grid.RowDefinitions.Count; i++)
            grid.RowDefinitions[i].Height = new GridLength(Math.Max(0, rows[i]), GridUnitType.Star);
    }

    /// <summary>
    /// 让热区贴着图片当前的显示范围（含缩放与平移，超出舞台的部分裁掉）：
    /// 范围内左 20% = 上一张、右 20% = 下一张；中间 60% 里只有正中的一小块是“打开仓库页面”
    /// （最大 132×80，且不超过图片显示范围的 28%），其余不响应点击；范围外是空白（点击关闭）。
    /// 图片加载、舞台尺寸变化、缩放 / 平移时都要调用。尺寸拿不到时按“居中的初始范围”配比。
    /// </summary>
    private void UpdateLightboxZones()
    {
        try
        {
            double w = LightboxStage.ActualWidth, h = LightboxStage.ActualHeight;
            if (!(w > 0) || !(h > 0)) { SetStarSizes(LightboxZones, new[] { 1.0, 1, 3, 1, 1 }, new[] { 1.0, 5, 1 }); return; }

            var src = LightboxImage.Source;
            double bw = _lightboxFitW > 0 ? _lightboxFitW : w, bh = _lightboxFitH > 0 ? _lightboxFitH : h;
            double sw = src != null && src.Width > 0 ? src.Width : bw;
            double sh = src != null && src.Height > 0 ? src.Height : bh;
            var r = ImageDisplayRect(bw, bh, sw, sh, _lightboxZoom, _lightboxRatio, _lightboxPanX, _lightboxPanY);

            double l = Math.Max(0, r.Left), t = Math.Max(0, r.Top);
            double rt = Math.Min(w, r.Right), bt = Math.Min(h, r.Bottom);
            if (rt <= l || bt <= t)         // 图片整个被拖出舞台：只剩空白
            {
                SetStarSizes(LightboxZones, new[] { 1.0, 0, 0, 0, 1 }, new[] { 1.0, 0, 1 });
                return;
            }
            double iw = rt - l, ih = bt - t;
            SetStarSizes(LightboxZones,
                new[] { l, iw * 0.2, iw * 0.6, iw * 0.2, w - rt },
                new[] { t, ih, h - bt });

            double midW = iw * 0.6;
            double zw = Math.Min(OpenZoneMaxW, midW * 0.5), zh = Math.Min(OpenZoneMaxH, ih * 0.28);
            SetStarSizes(LightboxMidGrid,
                new[] { (midW - zw) / 2, zw, (midW - zw) / 2 },
                new[] { (ih - zh) / 2, zh, (ih - zh) / 2 });
        }
        catch (Exception ex) { Logger.LogError("UpdateLightboxZones", ex); }
    }

    private const double OpenZoneMaxW = 132, OpenZoneMaxH = 80;

    private void ShowImageLightbox(PluginMarket.MarketPlugin m, int index)
    {
        try
        {
            var urls = m.Screenshots.Count > 0
                ? m.Screenshots
                : new List<string>();     // 无图：先打开放大层显示加载提示，再从 README 抓取
            _lightboxUrls = urls;
            _lightboxIndex = Math.Max(0, Math.Min(index, Math.Max(0, urls.Count - 1)));
            _lightboxName = m.Name;
            _lightboxPlugin = m;

            ImageLightbox.Visibility = Visibility.Visible;
            AttachLightboxToViewer();    // 整个浮层挪进独立的透明全屏窗口（自检 / 截图模式下什么都不做）
            PlayLightboxOpenAnimation();
            HideThumbPreview();          // 进放大层了就把悬停预览收掉，免得两层叠着
            WireLightboxDragHandlers();  // 每次打开都重挂一遍（内部先去重再挂），拖动才能一开就能用
            FitImageBox();               // 必须在复位缩放之前：盒子没定下来，居中偏移就算不对
            ResetLightboxZoom(mustReset: true);
            if (urls.Count == 0)
            {
                LightboxHint.Text = "目录中无截图，正在从仓库 README 中查找…";
                LightboxHint.Visibility = Visibility.Visible;
                LightboxImage.Source = null;
                LightboxCounter.Text = "";
                UpdateLightboxZones();
                _ = ScrapeIntoLightboxAsync(m);
            }
            else
            {
                _ = ShowLightboxIndexAsync(_lightboxIndex);
            }
            if (_lightboxViewer != null) _lightboxViewer.Activate();     // 键盘（Esc / 方向键 / 0）要落在大窗口上
            else Keyboard.Focus(this);
        }
        catch (Exception ex) { Logger.LogError("ShowImageLightbox", ex); }
    }

    private string _lightboxName = "";
    private PluginMarket.MarketPlugin? _lightboxPlugin;

    private async Task ScrapeIntoLightboxAsync(PluginMarket.MarketPlugin m)
    {
        var urls = await PluginMarket.ScrapeReadmeImagesAsync(m.Owner, RepoNameOf(m));
        m.ReadmeScraped = true;
        foreach (string u in urls) if (!m.Screenshots.Contains(u)) m.Screenshots.Add(u);

        if (ImageLightbox.Visibility != Visibility.Visible) return;   // 抓取期间放大层已被关闭
        _lightboxUrls = m.Screenshots;
        if (_lightboxUrls.Count == 0)
        {
            LightboxHint.Text = "该仓库的 README 中也未找到图片";
            return;
        }
        _lightboxIndex = 0;
        await ShowLightboxIndexAsync(0);
    }

    private async Task ShowLightboxIndexAsync(int index)
    {
        if (_lightboxUrls.Count == 0) return;
        index = Math.Max(0, Math.Min(index, _lightboxUrls.Count - 1));
        _lightboxIndex = index;
        string url = _lightboxUrls[index];

        // 标题已带页码；右侧的补充信息（操作提示 / 倍数）由 UpdateLightboxZoomText 填。
        LightboxTitle.Text = $"{_lightboxName} · 第 {index + 1}/{_lightboxUrls.Count} 张";
        LightboxCounter.Text = "";
        ResetLightboxZoom(mustReset: false);     // 换图必须复位缩放：上一张的倍数会一开就糊成一片、也看不全
        LightboxHint.Text = "正在加载图片…";
        LightboxHint.Visibility = Visibility.Visible;

        var bmp = await LoadImageAsync(url, full: true);
        if (ImageLightbox.Visibility != Visibility.Visible || _lightboxIndex != index) return;   // 等待期间已翻页或关闭
        if (bmp == null)
        {
            LightboxImage.Source = null;
            UpdateLightboxZones();
            LightboxHint.Text = "这张图未能取到（网络或仓库路径变动）\n可以点图片中间打开仓库页面";
            return;
        }
        LightboxImage.Source = bmp;
        FitImageBox();
        RecomputeLightboxRatio();                // 小图不放大到超过原始尺寸
        ResetLightboxView();                     // 新图回到“居中、初始比例”
        LightboxScroll.UpdateLayout();           // 等布局跑完再归零滚动位置
        ResetLightboxScroll();                   // 放在取图守卫之后：被丢弃的旧图回调不会清掉新图的偏移
        LightboxHint.Visibility = Visibility.Collapsed;
        FadeInLightboxImage();
        UpdateLightboxZones();                   // 图片换好了 ⇒ 按新的显示范围重算热区
    }

    private static readonly Brush ViewerBackdrop = MakeFrozenBrush(Color.FromArgb(0x01, 0, 0, 0));
    private static readonly Brush DimBackdrop = MakeFrozenBrush(Color.FromArgb(0xD0, 0x0A, 0x0A, 0x0E));
    private static Brush MakeFrozenBrush(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private bool _lightboxClosing;
    private int _lightboxAnimToken;

    /// <summary>关闭看图层。在独立窗口里用时先淡出（约 130ms）再真正收尾；其余情况立即收尾。</summary>
    private void LightboxClose()
    {
        if (_lightboxClosing) return;
        if (_lightboxViewer == null || !LightboxInOwnWindow || ImageLightbox.Visibility != Visibility.Visible)
        {
            LightboxCloseNow();
            return;
        }
        _lightboxClosing = true;
        int token = ++_lightboxAnimToken;
        ImageLightbox.IsHitTestVisible = false;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(130)) { FillBehavior = FillBehavior.HoldEnd };
        fade.Completed += (_, _) =>
        {
            if (token != _lightboxAnimToken) return;      // 动画期间又打开了新的一轮
            _lightboxClosing = false;
            LightboxCloseNow();
            ResetLightboxChrome();
        };
        ImageLightbox.BeginAnimation(UIElement.OpacityProperty, fade);
        var shrink = new DoubleAnimation(1, 0.97, TimeSpan.FromMilliseconds(130)) { FillBehavior = FillBehavior.HoldEnd };
        LightboxCardScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        LightboxCardScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
    }

    /// <summary>真正的收尾：隐藏看图层、清图、复位缩放、把浮层放回主窗口。</summary>
    private void LightboxCloseNow()
    {
        ImageLightbox.Visibility = Visibility.Collapsed;
        LightboxImage.Source = null;
        // 盒子尺寸复位成“自动”，下次打开一定由 FitImageBox 重新定。
        LightboxImage.Width = double.NaN;
        LightboxImage.Height = double.NaN;
        _lightboxFitW = 0;
        _lightboxFitH = 0;
        _lightboxRatio = LightboxFitRatio;
        _lightboxUrls = new List<string>();
        ResetLightboxZoom(mustReset: true);
        DetachLightboxFromViewer();
    }

    /// <summary>去掉淡出动画留下的持有值，恢复可点击。</summary>
    private void ResetLightboxChrome()
    {
        ImageLightbox.BeginAnimation(UIElement.OpacityProperty, null);
        ImageLightbox.Opacity = 1;
        ImageLightbox.IsHitTestVisible = true;
        LightboxCardScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        LightboxCardScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        LightboxCardScale.ScaleX = 1;
        LightboxCardScale.ScaleY = 1;
    }

    /// <summary>打开：遮罩淡入，同时整体从 95% 轻微放大到 100%（仅独立窗口里播放；自检模式不播）。</summary>
    private void PlayLightboxOpenAnimation()
    {
        _lightboxAnimToken++;
        _lightboxClosing = false;
        ResetLightboxChrome();
        if (!LightboxInOwnWindow) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ImageLightbox.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)) { FillBehavior = FillBehavior.Stop });
        var grow = new DoubleAnimation(0.95, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
        LightboxCardScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        LightboxCardScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    /// <summary>换图时新图淡入（约 140ms），翻页不再生硬跳变。</summary>
    private void FadeInLightboxImage()
    {
        if (!LightboxInOwnWindow) return;
        LightboxImage.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)) { FillBehavior = FillBehavior.Stop });
    }

    /// <summary>
    /// 造一个承载放大层的窗口（只造、不显示）：无边框、透明底（浮层自带 90% 不透明的暗色遮罩，
    /// 于是整块屏幕都被压暗）、不进任务栏、盖满主窗口所在的屏幕。
    /// 主窗口已经显示过时才设 Owner（WPF 不允许把没显示过的窗口设成 Owner）—— 自检里就是没显示过的。
    /// 方向键 / Esc / 0 / ± 都转给主窗口既有的键盘处理，不另写一套。
    /// </summary>
    private Window BuildLightboxViewerWindow()
    {
        // 铺满主窗口所在的屏幕。不用 WindowState.Maximized：无边框透明窗口最大化时尺寸有时来得比内容晚，
        // 舞台会先按 800×600 量一遍，改成直接设定屏幕范围，尺寸一次到位。
        var bounds = System.Drawing.Rectangle.Empty;
        double scale = 1.0;
        try
        {
            var screen = IsVisible
                ? System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(this).Handle)
                : System.Windows.Forms.Screen.PrimaryScreen;
            bounds = screen?.Bounds ?? System.Drawing.Rectangle.Empty;
            scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        }
        catch { }
        if (bounds.IsEmpty || scale <= 0)
        {
            bounds = new System.Drawing.Rectangle(0, 0, (int)SystemParameters.PrimaryScreenWidth, (int)SystemParameters.PrimaryScreenHeight);
            scale = 1.0;
        }
        var win = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = bounds.Left / scale,
            Top = bounds.Top / scale,
            Width = bounds.Width / scale,
            Height = bounds.Height / scale,
            Title = "DSH 守护壳 · 看图"
        };
        if (IsVisible) win.Owner = this;
        win.PreviewKeyDown += (s, e) => Window_PreviewKeyDown(s, e);
        win.Closed += (_, _) =>
        {
            // 被用户直接关掉（Alt+F4 等）：立即收尾；我们自己关的会先把字段置空，这里就进不来
            if (ReferenceEquals(_lightboxViewer, win)) LightboxCloseNow();
        };
        return win;
    }

    /// <summary>把放大层挪进独立窗口并显示。任何一步出错都把浮层放回原处，退回窗口内老样子，绝不让它凭空消失。</summary>
    private void AttachLightboxToViewer()
    {
        if (!LightboxInOwnWindow || _lightboxViewer != null) return;
        try
        {
            var home = ImageLightbox.Parent as Panel;
            if (home == null) return;
            var win = BuildLightboxViewerWindow();
            _lightboxHomePanel = home;
            _lightboxHomeIndex = Math.Max(0, home.Children.IndexOf(ImageLightbox));
            home.Children.Remove(ImageLightbox);
            win.Content = ImageLightbox;
            _lightboxViewer = win;
            ImageLightbox.Background = ViewerBackdrop;     // 不压暗屏幕，只留 1/255 的不透明度来接收点击
            win.Show();
            win.UpdateLayout();        // 尺寸立刻有值，FitImageBox 才量得到盒子
            win.Activate();
        }
        catch (Exception ex)
        {
            Logger.LogError("AttachLightboxToViewer", ex);
            var w = _lightboxViewer;
            _lightboxViewer = null;
            RestoreLightboxHome(w);
        }
    }

    /// <summary>放大层放回主窗口原位，并关掉独立窗口。没挪出去过就什么都不做。</summary>
    private void DetachLightboxFromViewer()
    {
        var win = _lightboxViewer;
        if (win == null) return;
        _lightboxViewer = null;        // 先置空：Closed 里据此分辨"是我们自己关的"
        RestoreLightboxHome(win);
        try { win.Close(); } catch { }
    }

    private void RestoreLightboxHome(Window? win)
    {
        try
        {
            ImageLightbox.Background = DimBackdrop;
            if (win != null && ReferenceEquals(win.Content, ImageLightbox)) win.Content = null;
            var home = _lightboxHomePanel;
            if (home != null && ImageLightbox.Parent == null)
                home.Children.Insert(Math.Min(_lightboxHomeIndex, home.Children.Count), ImageLightbox);
        }
        catch (Exception ex) { Logger.LogError("RestoreLightboxHome", ex); }
    }

    /// <summary>
    /// 自检用：不显示窗口，只验证「挪进窗口 → 名字仍能解析 → 放回原位」这条搬家路径本身。
    /// 返回 (搬进去后的父级是窗口, 搬进去后 FindName 仍找得到浮层与图片, 放回后父级与位置都和原来一致)。
    /// </summary>
    internal (bool inViewer, bool namesOk, bool restored) LightboxViewerRoundTripForTest()
    {
        var home = ImageLightbox.Parent as Panel;
        if (home == null) return (false, false, false);
        int idx = home.Children.IndexOf(ImageLightbox);
        var win = BuildLightboxViewerWindow();
        home.Children.Remove(ImageLightbox);
        win.Content = ImageLightbox;
        bool inViewer = ReferenceEquals(ImageLightbox.Parent, win);
        bool namesOk = FindName("ImageLightbox") != null && FindName("LightboxImage") != null && FindName("LightboxStage") != null;
        win.Content = null;
        home.Children.Insert(idx, ImageLightbox);
        bool restored = ReferenceEquals(ImageLightbox.Parent, home) && home.Children.IndexOf(ImageLightbox) == idx;
        try { win.Close(); } catch { }
        return (inViewer, namesOk, restored);
    }

    /// <summary>自检用：当前缩放 / 平移（读同一组字段，避免分两次读被拖动打断）。</summary>
    internal (double zoom, double panX, double panY) LightboxZoomPanForTest() => (_lightboxZoom, _lightboxPanX, _lightboxPanY);

    /// <summary>自检用：直接给倍数 / 平移赋值再落到变换上（测拖动软限制用）。</summary>
    internal void SetLightboxZoomPanForTest(double zoom, double panX, double panY)
    {
        _lightboxZoom = zoom; _lightboxPanX = panX; _lightboxPanY = panY;
        ApplyLightboxZoom();
    }
    internal void ClampLightboxPanForTest() { ClampLightboxPan(); ApplyLightboxZoom(); }

    /// <summary>点遮罩（卡片外侧的边距）关闭。</summary>
    private void Lightbox_Backdrop_Click(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, ImageLightbox)) LightboxClose();
    }

    /// <summary>点卡片自身的留白关闭；点图片区域的事件在这里被拦住，不会冒泡成关闭。</summary>
    private void LightboxCard_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, LightboxCard)) LightboxClose();
        e.Handled = true;
    }

    // 下面几个处理器绑的都是按下事件，但“点击”和“拖动画面”共用左键，按下那一刻分不出来，
    // 所以按下只登记待办，抬起时（LightboxStage_MouseLeftButtonUp）确认没有拖动才执行。
    /// <summary>点图片中间“不响应”的区域：清掉待办（不关闭、不翻页）；双击在适应窗口与 2.5 倍之间切换。</summary>
    private void Lightbox_Neutral_Click(object sender, MouseButtonEventArgs e)
    {
        _lightboxPending = 0;
        e.Handled = true;
        if (e.ClickCount < 2) return;
        if (_lightboxZoom > 1.05) ResetLightboxView();
        else ZoomLightboxAt(e.GetPosition(LightboxScroll), 1153);     // 1.1^(1153/120) ≈ 2.5 倍
    }

    /// <summary>点底部标题：打开插件仓库页面（正中那块小热区之外的另一个入口，不易误触）。</summary>
    private void Lightbox_Title_Click(object sender, MouseButtonEventArgs e)
    {
        Lightbox_OpenRepo();
        e.Handled = true;
    }

    /// <summary>点图片以外的空白处：待办 = 关闭。</summary>
    private void Lightbox_Blank_Click(object sender, MouseButtonEventArgs e) { _lightboxPending = 3; e.Handled = true; }
    private void Lightbox_Prev_Click(object sender, MouseButtonEventArgs e) { _lightboxPending = -1; e.Handled = true; }
    private void Lightbox_Next_Click(object sender, MouseButtonEventArgs e) { _lightboxPending = +1; e.Handled = true; }

    /// <summary>点图片中间的热区：待办 = 打开插件仓库页面（2）。必须标记已处理，否则会冒泡成“点了卡片留白”而关掉看图层。</summary>
    private void Lightbox_Image_Click(object sender, MouseButtonEventArgs e)
    {
        _lightboxPending = 2;
        e.Handled = true;
    }

    /// <summary>执行按下时登记的待办（翻页 / 开仓库 / 关闭）。只在确认是点击之后调用，执行后立即清空。</summary>
    private void ExecuteLightboxPending()
    {
        int pending = _lightboxPending;
        _lightboxPending = 0;
        if (pending == -1) _ = ShowLightboxIndexAsync(_lightboxIndex - 1);
        else if (pending == +1) _ = ShowLightboxIndexAsync(_lightboxIndex + 1);
        else if (pending == 2) Lightbox_OpenRepo();
        else if (pending == 3) LightboxClose();
    }

    /// <summary>打开插件的仓库页面。没有图 / 没有插件信息就什么都不做；地址仍须过 OpenExternalLink 的白名单。</summary>
    private void Lightbox_OpenRepo()
    {
        try
        {
            string? repo = _lightboxPlugin?.RepoUrl;
            if (!string.IsNullOrWhiteSpace(repo)) _ = OpenExternalLink(repo, "LightboxImage");
        }
        catch (Exception ex) { Logger.LogError("Lightbox_Image_Click", ex); }
    }

    /// <summary>
    /// 给 LightboxStage 挂拖动所需的按下 / 移动 / 抬起处理。
    /// 用代码挂而不是改 XAML：图层本身已在 XAML 里成型，这里只是补行为，不动界面结构。
    /// 先摘后挂：每次打开都会走到这里，不摘就会重复挂，一次拖动被处理多遍、偏移成倍跳。
    /// 摘的时候必须用挂的时候那个委托实例，所以三个处理器都存成了字段。
    /// </summary>
    private void WireLightboxDragHandlers()
    {
        if (_lightboxDragDownHandler != null)
        {
            LightboxStage.RemoveHandler(UIElement.MouseLeftButtonDownEvent, _lightboxDragDownHandler);
            LightboxStage.RemoveHandler(UIElement.MouseMoveEvent, _lightboxDragMoveHandler!);
            LightboxStage.RemoveHandler(UIElement.MouseLeftButtonUpEvent, _lightboxDragUpHandler!);
        }

        _lightboxDragDownHandler = LightboxStage_MouseLeftButtonDown;
        _lightboxDragMoveHandler = LightboxStage_MouseMove;
        _lightboxDragUpHandler = LightboxStage_MouseLeftButtonUp;

        // handledEventsToo = true 不是可选项：三块热区压在最上层，它们的按下处理器会把事件标成
        // Handled（原本是为了不让卡片把它当成"点了留白"而关掉整个层）。用普通 += 挂的处理器收不到
        // 已处理的事件 ⇒ 在图片区按下根本传不到这里，拖动会直接变成死代码。
        // 这里显式声明"即便已被标记处理也要收到"，拖动才真正生效。
        LightboxStage.AddHandler(UIElement.MouseLeftButtonDownEvent, _lightboxDragDownHandler, true);
        LightboxStage.AddHandler(UIElement.MouseMoveEvent, _lightboxDragMoveHandler, true);
        LightboxStage.AddHandler(UIElement.MouseLeftButtonUpEvent, _lightboxDragUpHandler, true);
    }

    /// <summary>
    /// 在图片区按下左键：只记起点与当前偏移，不执行任何动作 —— 此时还分不清用户是要点击还是拖动。
    /// 捕获鼠标是为了拖出图片区、甚至拖到窗口外也能继续收到移动与抬起，否则一离开热区就断线。
    /// </summary>
    private void LightboxStage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _lightboxDragActive = true;
        _lightboxDragMoved = false;              // 每次按下重新裁决，上一轮的"已拖动"不能顺延
        _lightboxDragStart = e.GetPosition(LightboxScroll);
        _lightboxDragStartPanX = _lightboxPanX;        // 以按下时的平移为基准反算，不累加
        _lightboxDragStartPanY = _lightboxPanY;
        LightboxStage.CaptureMouse();
    }

    /// <summary>
    /// 按住拖动 ⇒ 平移画面。位移要够 LightboxDragThreshold 才算拖动：
    /// 手抖一两像素是点不准，不是想拖；一旦判定为拖动就把 _lightboxDragMoved 立起来，
    /// 抬起时据此丢弃待办（见下面的抬起处理），这就是"拖了就不翻页/不进仓库"的关口。
    /// 偏移用"按下时的偏移 - 位移"，累加式改偏移会在越过边界被夹住后跟手性变差。
    /// </summary>
    private void LightboxStage_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_lightboxDragActive || e.LeftButton != MouseButtonState.Pressed) return;

        Point cur = e.GetPosition(LightboxScroll);
        double dx = cur.X - _lightboxDragStart.X;
        double dy = cur.Y - _lightboxDragStart.Y;

        if (!_lightboxDragMoved)
        {
            if (Math.Abs(dx) < LightboxDragThreshold && Math.Abs(dy) < LightboxDragThreshold) return;
            // 阈值用"横纵都要够"来判：只看单轴的话，斜着拖一点点就会误判成拖动。
            _lightboxDragMoved = true;
            LightboxStage.Cursor = Cursors.SizeAll;      // 拖动时换成移动光标，松手恢复
        }

        // 2.2.1：「随意拖动」—— 平移 = 按下时的平移 + 鼠标位移，不再被滚动范围夹住；
        // 只留一道软限制（至少还剩一块图在视野里，免得拖丢，见 ClampPanKeepVisible）。
        _lightboxPanX = _lightboxDragStartPanX + dx;
        _lightboxPanY = _lightboxDragStartPanY + dy;
        ClampLightboxPan();
        ApplyLightboxZoom();
        e.Handled = true;
    }

    /// <summary>
    /// 抬起：这一下的性质此刻才定下来。
    /// 够阈值（_lightboxDragMoved）⇒ 这是一次拖动，丢掉待办、什么都不执行 —— 拖动与点击由此互斥；
    /// 不够阈值 ⇒ 当真点击，兑现待办（翻页 / 开仓库）。
    /// 两个标志都要复位：不复位的话，下一次单纯的点击会被上一轮的拖动状态污染。
    /// </summary>
    private void LightboxStage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_lightboxDragActive) return;        // 没经过按下（例如在窗口外松开）就不参与裁决

        _lightboxDragActive = false;
        bool moved = _lightboxDragMoved;
        _lightboxDragMoved = false;
        LightboxStage.ClearValue(FrameworkElement.CursorProperty);
        if (LightboxStage.IsMouseCaptured) LightboxStage.ReleaseMouseCapture();

        if (moved) { _lightboxPending = 0; return; }   // 拖动：待办直接作废，不触发翻页
        ExecuteLightboxPending();
    }

    /// <summary>看图层键盘操作：← / → 翻页，Esc 关闭；筛选下拉 / 批量功能框打开时 Esc 优先关闭下拉。</summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && MarketFilterPopup.IsOpen)
        {
            MarketFilterPopup.IsOpen = false;
            e.Handled = true;
            return;
        }
        // 批量功能框的弹层同样**不在窗口可视树里**，而且它不一定跟看图层一起打开：
        // 原来那句 Esc 收弹层写在下面的看图分支里，没开看图层时按 Esc 会被下面那句 return 漏掉。
        // 提到这里（与上一段筛选下拉同一优先级、仍早于看图分支）补齐，行为与原来一致。
        if (e.Key == Key.Escape && _batchActionPopup is { IsOpen: true })
        {
            CloseBatchMenu();
            e.Handled = true;
            return;
        }
        if (ImageLightbox.Visibility != Visibility.Visible) return;
        switch (e.Key)
        {
            case Key.Escape: if (_batchActionPopup is { IsOpen: true }) { CloseBatchMenu(); e.Handled = true; break; } LightboxClose(); e.Handled = true; break;
            case Key.Left: _ = ShowLightboxIndexAsync(_lightboxIndex - 1); e.Handled = true; break;
            case Key.Right: _ = ShowLightboxIndexAsync(_lightboxIndex + 1); e.Handled = true; break;
            // 2.2.1 小巧思：0 / Home 回到整图居中；+ / − 以视野中心为锚点缩放一格
            case Key.D0: case Key.NumPad0: case Key.Home: ResetLightboxView(); e.Handled = true; break;
            case Key.OemPlus: case Key.Add:
                ZoomLightboxAt(new Point(LightboxStage.ActualWidth / 2, LightboxStage.ActualHeight / 2), 120); e.Handled = true; break;
            case Key.OemMinus: case Key.Subtract:
                ZoomLightboxAt(new Point(LightboxStage.ActualWidth / 2, LightboxStage.ActualHeight / 2), -120); e.Handled = true; break;
        }
    }

    /// <summary>
    /// 窗口失去激活（切到别的程序、点任务栏等）时，把**所有**打开的下拉浮层收起。
    /// 原因：StaysOpen=True 的 Popup 是**独立顶层窗口**，不会跟着主窗口退到后台 ——
    /// 现场表现就是"主程序到后台了，筛选框还浮在别的窗口最上面"。
    /// 这里有**两条**路，缺一不可（顺序无所谓，两者都幂等）：
    ///   ① <see cref="CloseAllPopups"/>：遍历可视树，管的是写在 XAML 里、真正挂在树上的
    ///      两个筛选下拉（InstalledFilterPopup / MarketFilterPopup）；
    ///   ② <see cref="CloseBatchMenu"/>：点名收批量功能框的弹层（BatchActionPopup）。
    ///      它是 Batch 里 <c>new Popup()</c> 出来的，**从没加进任何 Children 集合**
    ///      （只设了 PlacementTarget = 触发按钮），所以它根本不是窗口可视树的一个节点，
    ///      ①那套遍历永远走不到它 —— 这就是"批量框还浮在最上层"的原因，必须单独点名。
    /// 多包一层 try/catch：失活可能连续触发多次，任何一处异常都不许打断另一条路的收起。
    /// </summary>
    private void Window_Deactivated(object? sender, EventArgs e)
    {
        try { CloseBatchMenu(); }
        catch (Exception ex) { Logger.LogError("Window_Deactivated.CloseBatchMenu", ex); }
        CloseAllPopups(this);
    }

    /// <summary>关闭 root 可视树里的全部 Popup（返回关了几个）。</summary>
    internal static int CloseAllPopups(DependencyObject? root)
    {
        int closed = 0;
        try
        {
            if (root == null) return 0;
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is Popup pop)
                {
                    if (pop.IsOpen) { pop.IsOpen = false; closed++; }
                }
                closed += CloseAllPopups(child);
            }
        }
        catch { }
        return closed;
    }
    /// <summary>
    /// 给下拉浮层加"看得见"的弹出动画：透明度 0→1 + 轻微上移 6px，约 130ms。
    /// 为什么不只用 XAML 的 PopupAnimation：那个依赖系统的"显示动画"开关，关掉就完全没有效果；
    /// 这里直接在子元素上跑 Storyboard，任何系统设置下都能看到。
    ///
    /// ⚠ 基础值必须是 <c>(0, 0)</c>（= 动画的**终点**），不能是 <c>(0, -6)</c>：
    ///   本动画用 <c>FillBehavior.Stop</c>（跑完不保留动画值），动画一停、值就回落到**基础值**。
    ///   原先基础值写的是起点 <c>-6</c> ⇒ 每次弹层打开、动画跑完后，内容被**永久顶高 6px**，
    ///   再也回不到设计位置（实测：Canvas.Top=6 的等价布局下视觉顶边从 6 变成 0）。
    ///   三个弹层（InstalledFilterPopup / MarketFilterPopup / BatchActionPopup）的 VerticalOffset
    ///   都是 6，但那是"弹层与触发按钮的间距"，与动画基础值是两回事、并不会互相抵消
    ///   ⇒ 这是真错位，不是有意补偿。改成 (0,0) 后，动画期间照旧从 -6 滑到 0，收尾自然停在设计位置。
    /// </summary>
    private static void AnimatePopupOpen(System.Windows.Controls.Primitives.Popup? popup)
    {
        try
        {
            if (popup?.Child is not FrameworkElement child) return;
            // 基础值 = 动画终点 0：FillBehavior.Stop 回落时停在设计位置（见上面 ⚠ 的说明）
            child.RenderTransform = new System.Windows.Media.TranslateTransform(0, 0);
            var fade = new System.Windows.Media.Animation.DoubleAnimation(0.0, 1.0,
                TimeSpan.FromMilliseconds(130)) { FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop };
            var slide = new System.Windows.Media.Animation.DoubleAnimation(-6.0, 0.0,
                TimeSpan.FromMilliseconds(160)) { FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop };
            child.BeginAnimation(UIElement.OpacityProperty, fade);
            if (child.RenderTransform is System.Windows.Media.TranslateTransform tt)
                tt.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, slide);
            child.Opacity = 1.0;
        }
        catch { }
    }

    /// <summary>把弹出动画挂到这几个下拉上（挂一次即可，重复调用无害）。</summary>
    private void HookPopupAnimations()
    {
        try
        {
            if (_popupAnimHooked) return;
            _popupAnimHooked = true;
            if (InstalledFilterPopup != null) InstalledFilterPopup.Opened += (_, _) => AnimatePopupOpen(InstalledFilterPopup);
            if (MarketFilterPopup != null) MarketFilterPopup.Opened += (_, _) => AnimatePopupOpen(MarketFilterPopup);
            if (_batchActionPopup != null) _batchActionPopup.Opened += (_, _) => AnimatePopupOpen(_batchActionPopup);
        }
        catch (Exception ex) { Logger.LogError("HookPopupAnimations", ex); }
    }

    /// <summary>
    /// 给**从根出发那趟够不到**的弹层挂上"打开即补刷主题"。
    ///
    /// 为什么只有批量功能框要挂：另外三个下拉（InstalledFilterPopup / MarketFilterPopup /
    /// SlimCombo 模板里的 PART_Popup）都写在 XAML 里、挂在窗口的树上，ThemeManager 的遍历能直接
    /// 走到它们；而 <c>MainWindow.Batch.cs</c> 的 BatchActionPopup 是 <c>new Popup { …, Child = … }</c>
    /// 造出来的，只设了 PlacementTarget、**从没加进任何 Children 集合** —— 它既不在视觉树上、
    /// 也不在逻辑树上，遍历从根怎么走都碰不到它（探针实测：给它加了 Popup 分支也依然够不到）。
    /// 因此只能在它打开的那一刻，由持有引用的这里按当前主题补刷一次（见 ThemeManager.ApplyTo）；
    /// 并同时交给 <c>ThemeManager.RegisterOrphanPopup</c> 登记 —— 它开着的时候主题若变了，
    /// 由 <c>ReskinOrphanPopups</c> 补刷（内容建一次就长期复用，之后反复开关都是同一棵子树，
    /// <c>MainWindow.Batch.cs</c> 的 <c>BatchBarHost.Content == null</c> 那段就是"只建一次"）。
    /// 每次鼠标按下都调（幂等，见 <c>_popupThemeHooked</c>）：批量条若建得比第一次点击晚，也能补挂上。
    /// </summary>
    private void HookPopupTheme()
    {
        try
        {
            var pop = _batchActionPopup;
            if (pop == null || ReferenceEquals(pop, _popupThemeHooked)) return;
            _popupThemeHooked = pop;
            // 登记：打开即补刷 + 「开着时切主题」也能补刷；Opened/Closed 的成对挂接与摘除都在这里
            ThemeManager.RegisterOrphanPopup(pop);
        }
        catch (Exception ex) { Logger.LogError("HookPopupTheme", ex); }
    }

    /// <summary>已经挂过"打开即补刷主题"的那个游离弹层（避免重复挂）。</summary>
    private Popup? _popupThemeHooked;

    private bool _popupAnimHooked;
    /// <summary>点击其他位置时收起筛选下拉（StaysOpen=True 需自行处理关闭）。</summary>
    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            // 打开任何下拉都必然先发生一次点击，就在这里把"失活即收起"挂上（先减后加，幂等）
            Deactivated -= Window_Deactivated;
            Deactivated += Window_Deactivated;
            HookPopupAnimations();      // 下拉弹出动画（幂等）
            HookPopupTheme();           // 游离弹层（批量功能框）打开时补刷主题（幂等）
            var src = e.OriginalSource as DependencyObject;
            if (MarketFilterPopup.IsOpen && !IsInsideFilterMenu(src)) MarketFilterPopup.IsOpen = false;
            if (InstalledFilterPopup.IsOpen && !IsInsideNode(InstalledFilterMenuRoot, src)) InstalledFilterPopup.IsOpen = false;
            // 批量操作下拉（子代理在 Batch 里 new 出来的，名字叫 BatchActionPopup）
            if (_batchActionPopup is { IsOpen: true } && !IsInsideNode((DependencyObject)_batchActionPopup.Child, src)) CloseBatchMenu();
        }
        catch { }
    }

    private bool IsInsideFilterMenu(DependencyObject? node) => IsInsideNode(FilterMenuRoot, node);

    private static bool IsInsideNode(DependencyObject root, DependencyObject? node)
    {
        while (node != null)
        {
            if (ReferenceEquals(node, root)) return true;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    // ══════════════ 自检钩子 ══════════════
    internal void ShowLightboxForTest(PluginMarket.MarketPlugin m, int index) => ShowImageLightbox(m, index);
    internal void CloseLightboxForTest() => LightboxClose();
    internal bool LightboxVisibleForTest => ImageLightbox.Visibility == Visibility.Visible;
    internal string LightboxTitleForTest => LightboxTitle.Text;

    /// <summary>
    /// 自检用：把看图层的"当前视图"整组读出来（偏移 / 内容尺寸 / 视口尺寸 / 倍数 / 适应窗口盒子）。
    /// 直接读控件属性而不做任何换算：这条钩子的用途就是把"倍数=1.0 时 Extent 是否等于 Viewport、
    /// 偏移是否归零、盒子是否等于图片区尺寸"钉在断言上。_lightboxFitW/_lightboxFitH 也一并给出：
    /// 光看控件 Width/Height 分不清"盒子还没量过（沿用旧值）"和"盒子就是这么大"，留痕才排得掉这种歧义。
    /// </summary>
    internal (double offX, double offY, double extW, double extH, double vpW, double vpH, double zoom, double fitW, double fitH)
        LightboxViewForTest()
        => (LightboxScroll.HorizontalOffset, LightboxScroll.VerticalOffset,
            LightboxScroll.ExtentWidth, LightboxScroll.ExtentHeight,
            LightboxScroll.ViewportWidth, LightboxScroll.ViewportHeight,
            _lightboxZoom, _lightboxFitW, _lightboxFitH);
    internal int ImageCacheCountForTest
    {
        get { try { return Directory.Exists(ImageCacheDir) ? Directory.GetFiles(ImageCacheDir).Length : 0; } catch { return -1; } }
    }

    /// <summary>指定图片是否已存在于磁盘缓存（不依赖本次新增文件数，缓存跨次运行保留）。</summary>
    internal bool ImageCacheHasForTest(string url)
    {
        try { return File.Exists(Path.Combine(ImageCacheDir, CacheKey(url))); } catch { return false; }
    }
}
