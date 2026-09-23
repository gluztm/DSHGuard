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
    private readonly HashSet<string> _imgFailed = new();
    private readonly SemaphoreSlim _imgGate = new(4, 4);

    private List<string> _lightboxUrls = new();
    private int _lightboxIndex;
    private double _lightboxZoom = 1.0;      // 看图层的缩放倍数；1.0 = 适应窗口
    private FrameworkElement? _lightboxHoverZone;   // 鼠标当前停在哪块热区；null = 都不在

    // ── 拖动与点击互斥的状态 ──
    // 三块热区的点击都绑在"按下"上，而"这一下到底是点击还是拖动"必须等抬起才知道，
    // 所以按下时不能直接执行，只能先记成待办，抬起时再按位移裁决：这就是下面这几个字段的用途。
    private int _lightboxPending;                   // 待办：0=没有；-1=上一张；+1=下一张；2=打开仓库
    private bool _lightboxDragActive;               // 本次按下是否仍在进行中（抬起或复位后为 false）
    private bool _lightboxDragMoved;                // 本次按下是否已够格算拖动（一旦为 true 就不再当点击）
    private Point _lightboxDragStart;               // 按下时的鼠标坐标（以 LightboxScroll 为参照）
    private double _lightboxDragStartOffsetX;       // 按下瞬间的横向偏移；够阈值后以它为基准反算
    private double _lightboxDragStartOffsetY;       // 同上，纵向
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
        lock (_imgFailed) { if (_imgFailed.Contains(url)) return null; }

        byte[]? bytes = null;
        string file = Path.Combine(ImageCacheDir, CacheKey(url));
        try { if (File.Exists(file)) bytes = await File.ReadAllBytesAsync(file); } catch { }

        if (bytes == null)
        {
            await _imgGate.WaitAsync();
            try
            {
                lock (mem) { if (mem.TryGetValue(url, out var hit2)) return hit2; }

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
            }
            finally { _imgGate.Release(); }
        }

        if (bytes == null)
        {
            lock (_imgFailed) { _imgFailed.Add(url); }
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
            lock (_imgFailed) { _imgFailed.Add(url); }
            return null;
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
    /// 避免用户快速翻看市场时堆出多个并发清扫（它们会互相抢删同一批文件，白白浪费 IO）。
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
    // LightboxImage 的缩放用 LayoutTransform（ScaleTransform），而不是 RenderTransform：
    //   · LayoutTransform 参与布局 —— 放大后图片的"布局尺寸"真的变大，ScrollViewer 量得到，
    //     于是才会出现滚动条，也才能拖动查看放大的部分；
    //   · RenderTransform 只改绘制结果、不改布局尺寸 —— 图会被画出控件边界，超出部分连同可滚动
    //     范围一起被裁掉，既看不到也滚不到，等于白放大。
    private const double ZoomMin = 0.2;      // 缩放下限（再小就看不清了）
    private const double ZoomBase = 1.1;     // 滚轮每 120 单位（一格）的缩放系数
    private const double ZoomMax = 8.0;      // 缩放上限（再大只是马赛克，还白占内存）

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
    /// 由"缩放前的滚动偏移"算出"缩放后应有的滚动偏移"，让光标下的那一点在缩放前后始终停在光标下。
    /// 纯函数，与界面无关 ⇒ 可单独自检（与 NextZoom 同一路数）。
    /// 推导：内容坐标 = (偏移 + 光标在视口内的位置) / 旧倍数；
    ///       新偏移   = 内容坐标 * 新倍数 - 光标在视口内的位置。
    /// 之所以把 extent / viewport 做成入参而不在这里直接读 ScrollViewer：读的时机很要命，
    /// 用错时机（布局还没跑完）会取到缩放前的旧尺寸，锚点就偏了 —— 见调用处的说明。
    /// </summary>
    /// <param name="oldZoom">缩放前的倍数。</param>
    /// <param name="newZoom">缩放后的倍数。</param>
    /// <param name="cursorInViewport">光标在视口（ScrollViewer 可视区）内的坐标，只喂一个分量。</param>
    /// <param name="scrollOffset">缩放前该方向的滚动偏移。</param>
    /// <param name="extent">缩放后该方向的内容总尺寸（ExtentWidth / ExtentHeight）。</param>
    /// <param name="viewport">缩放后该方向的视口尺寸（ViewportWidth / ViewportHeight）。</param>
    internal static double ZoomAnchorOffset(double oldZoom, double newZoom,
                                            double cursorInViewport, double scrollOffset,
                                            double extent, double viewport)
    {
        // 倍数非法就反推不出内容坐标：除法不是除零就是给出反方向的解，索性不锚定、原样返回。
        if (oldZoom <= 0 || newZoom <= 0) return scrollOffset;
        // 视口非法、或内容还没视口大 ⇒ 压根没有可滚动余地，锚定只会算出 0 或负数，原样返回最稳。
        if (viewport <= 0 || extent <= viewport) return scrollOffset;

        double contentPoint = (scrollOffset + cursorInViewport) / oldZoom;   // 光标压着的内容坐标（与倍数无关）
        double target = contentPoint * newZoom - cursorInViewport;           // 让这一点重新落回光标下
        double maxOffset = Math.Max(0.0, extent - viewport);                 // 可滚动上限：再多就滚出内容
        // 夹取：越界偏移 ScrollViewer 自己会纠正，先夹掉能避免"先跳出去再弹回来"的抖动。
        if (target < 0) return 0;
        if (target > maxOffset) return maxOffset;
        return target;
    }

    /// <summary>
    /// 只把 _lightboxZoom 落到 ScaleTransform 上，不碰滚动位置。
    /// 这里刻意不判 1.0 归零：连续缩放时倍数必然要经过 1.0 附近，
    /// 若在这里顺手归零，那么无论光标停在哪里，这一段缩放都会把视角硬拽回左上角。
    /// 归零是"复位"的语义，只由显式复位那条路径负责，见 ResetLightboxScroll。
    /// </summary>
    private void ApplyLightboxZoom()
    {
        if (LightboxImage.LayoutTransform is not ScaleTransform st)
        {
            st = new ScaleTransform(1.0, 1.0);
            LightboxImage.LayoutTransform = st;
        }
        st.ScaleX = _lightboxZoom;
        st.ScaleY = _lightboxZoom;
    }

    /// <summary>
    /// 看图层的缩放复位。换图、关层都要复位：新图沿用上一张的倍数会一开就糊成一片、也看不全。
    /// mustReset 会连 Image 上的布局变换一起清掉（换图时旧变换没有必要留着）。
    /// 两个分支都必须显式归零滚动位置：ScrollViewer 的偏移是独立于 LayoutTransform 的状态，
    /// 清掉变换（或把倍数设回 1.0）都不会顺带把偏移带回原点，
    /// 于是上一次留下的 HorizontalOffset / VerticalOffset 会被原样沿用到新图上，
    /// 表现就是一打开看图界面就看到一个放大了的左上角视图。
    /// </summary>
    private void ResetLightboxZoom(bool mustReset)
    {
        _lightboxZoom = 1.0;
        _lightboxHoverZone = null;      // 悬停态跟着一起清，免得箭头残留
        // 拖动的三个标志一并清掉：换图/关层时若还留着"进行中的按下"或"没执行的待办"，
        // 新图上会凭空翻一页，或者一上来就被当成拖到一半，手感直接坏掉。
        // 同时兜底放开鼠标捕获：捕获若留在已经复位的层上，后续鼠标事件会被一直锁死在 LightboxStage。
        _lightboxDragActive = false;
        _lightboxDragMoved = false;
        _lightboxPending = 0;
        if (LightboxStage.IsMouseCaptured) LightboxStage.ReleaseMouseCapture();
        if (mustReset) LightboxImage.LayoutTransform = null;
        else ApplyLightboxZoom();
        ResetLightboxScroll();          // 两个分支都走这里：归零不再依赖倍数是否等于 1.0
        UpdateLightboxArrows();
    }

    /// <summary>
    /// 把看图层的滚动位置显式拉回原点。
    /// 单独拆成一个方法是为了让"归零"只有一个出口：ApplyLightboxZoom 只管倍数，
    /// 偏移只在这里改，两条路径互不干扰，缩放途中就不会被顺手拽回左上角。
    /// </summary>
    private void ResetLightboxScroll()
    {
        LightboxScroll.ScrollToHorizontalOffset(0);
        LightboxScroll.ScrollToVerticalOffset(0);
    }

    /// <summary>
    /// 滚轮缩放。这里挂在 LightboxStage 上（不是 ScrollViewer 上）：三块热区铺满图片区且可命中，
    /// 事件只会上冒、不会横向传给兄弟节点 ScrollViewer ⇒ 挂在 ScrollViewer 上等于是死代码。
    /// 不按 Ctrl 一律放行（e.Handled = false）⇒ 普通滚轮照旧是"滚动查看"，绝不拦；
    /// 按住 Ctrl 才改成缩放，并吞掉事件，免得一边缩放一边又滚一段。
    /// </summary>
    private void LightboxScroll_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) { e.Handled = false; return; }

        // 先记下光标位置、再改倍数：ApplyLightboxZoom 跑完之后布局尺寸已经变了，
        // 那时再取位置，读到的坐标与"缩放前的内容"已经对不上号，锚定必然偏。
        Point cursorPt = e.GetPosition(LightboxScroll);
        double zoomBefore = _lightboxZoom;
        double zoomAfter = NextZoom(zoomBefore, e.Delta);

        _lightboxZoom = zoomAfter;
        ApplyLightboxZoom();

        // 必须等布局跑完再读尺寸：倍数是加在 LayoutTransform 上的，它改的是图片的"布局尺寸"，
        // 而 ScrollViewer 的 ExtentWidth / ExtentHeight 是布局量算的产物，要等这一轮布局结束才更新。
        // 不等 UpdateLayout 就取，拿到的是缩放前的旧 extent ⇒ 可滚动上限与内容坐标一起算错，
        // 锚点会偏掉（放大越狠偏得越明显）—— 这一步不是保险，是正确性的前提。
        LightboxScroll.UpdateLayout();

        // 横纵各锚一次：两个方向的可滚动余量不同，必须分开算，共用一个值会有一轴对不准。
        double offsetX = ZoomAnchorOffset(zoomBefore, zoomAfter, cursorPt.X,
                                          LightboxScroll.HorizontalOffset,
                                          LightboxScroll.ExtentWidth, LightboxScroll.ViewportWidth);
        double offsetY = ZoomAnchorOffset(zoomBefore, zoomAfter, cursorPt.Y,
                                          LightboxScroll.VerticalOffset,
                                          LightboxScroll.ExtentHeight, LightboxScroll.ViewportHeight);
        LightboxScroll.ScrollToHorizontalOffset(offsetX);
        LightboxScroll.ScrollToVerticalOffset(offsetY);

        e.Handled = true;
    }

    /// <summary>窗口尺寸变了必须重算热区，否则图片显示区已经变了、热区还停在旧比例上。</summary>
    private void LightboxStage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateLightboxZones();
        UpdateLightboxArrows();      // 顺带按新边界刷一遍悬停箭头
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

    /// <summary>
    /// 箭头显隐的唯一判据：只有"鼠标停着的那块热区"配得上箭头，并且还得真有上一张 / 下一张。
    /// 首尾边界照搬原来那两行 IsEnabled 的条件（index &gt; 0 / index &lt; Count - 1），
    /// 只是把结果落到"箭头 Opacity + 热区能否命中"上 —— XAML 里那两枚翻页按钮已经删掉，不能再依赖它们。
    /// 箭头保持 IsHitTestVisible = false ⇒ 它自己不吃鼠标，热区的 Enter / Leave 不会被它打断而抖动。
    /// </summary>
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
    /// 把看图层的箭头、渐变遮罩淡入淡出（约 150ms）。
    /// 动画只是"看得见的过渡"，最终状态一律由属性值决定，这样快速划过时不会卡在过渡中间：
    ///   1. <c>FillBehavior.Stop</c> —— 动画结束或被下一条顶替后就不再占用 Opacity，
    ///      属性的本地值立刻透出来。若用 HoldEnd，已结束的动画会一直盖着属性值，
    ///      后面那些"直接赋值"的地方看起来就完全没生效。
    ///   2. 下完动画紧接着把属性本身写成终值 —— 过渡被打断、被顶替或正常跑完，露出的都是这个终值，
    ///      不依赖动画时序，也就不用去关心哪条动画先到。
    /// 起点取当前的 Opacity（含正在跑的动画值），所以来回快速悬停是从"眼前这一帧"接着淡，
    /// 而不是每次都从写死的端点重播。
    /// 箭头与遮罩都是纯装饰，XAML 里已设 <c>IsHitTestVisible="False"</c>，命中判定始终只看热区本身。
    /// </summary>
    private static void FadeLightboxElement(UIElement el, double to)
    {
        // 起点取当前正在显示的不透明度（有动画在跑时取到的就是动画的当前帧），
        // 这样来回快速悬停是从眼前这一帧接着淡，不会跳回上一条动画的端点。
        double from = el.Opacity;
        var anim = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(150))
        {
            FillBehavior = FillBehavior.Stop
        };
        el.BeginAnimation(UIElement.OpacityProperty, anim);
        el.Opacity = to;
    }

    private static void SetLightboxZoneWidths(Grid grid, double left, double mid, double right)
    {
        if (grid.ColumnDefinitions.Count < 3) return;
        // 用 Star 而不是 Relative：Relative 是"占剩余空间的比例"，留白不计进去，
        // 这里要的恰恰是"含留白"的绝对配比（见 UpdateLightboxZones）。
        grid.ColumnDefinitions[0].Width = new GridLength(left, GridUnitType.Star);
        grid.ColumnDefinitions[1].Width = new GridLength(mid, GridUnitType.Star);
        grid.ColumnDefinitions[2].Width = new GridLength(right, GridUnitType.Star);
    }

    /// <summary>
    /// 按"图片在窗口里的实际显示区域"重新配比三栏热区，让可点范围贴着图片本身：
    /// 图片按 Uniform 缩放后左右会留白（单侧 pad），留白一并算进左右两栏，于是
    /// 左栏 = pad + iw·0.20、中栏 = iw·0.60、右栏 = pad + iw·0.20。
    /// 三个宽度全取 Star ⇒ 权重比即宽度比、且总权重归一化后正好填满 W：
    ///   W·(pad + 0.2·iw)/W + W·0.6·iw/W + W·(pad + 0.2·iw)/W
    ///     = pad + 0.2·iw + 0.6·iw + pad + 0.2·iw
    ///     = (pad + iw + pad) + (0.2 + 0.6 + 0.2 - 1)·iw = W + 0
    /// ⇒ 三栏相加恒等于 W，没有死区（点在图片区任意位置都落进某一栏）。
    /// 调用时机有两处，缺一不可：图片加载完成之后、以及 LightboxStage 尺寸变化时 ——
    /// 少了后者，拖动窗口后热区就会错位。
    /// </summary>
    private void UpdateLightboxZones()
    {
        try
        {
            double stageW = LightboxStage.ActualWidth;
            double stageH = LightboxStage.ActualHeight;
            var src = LightboxImage.Source;
            // 尺寸拿不到（首次布局还没跑完 / 没有图 / 尺寸为 0）⇒ 退回 0.2 / 0.6 / 0.2 的兜底比例。
            // 这里是唯一的除法点，先把分母全挡掉，绝不除零、绝不抛。
            if (stageW <= 0 || stageH <= 0 || src == null || src.Width <= 0 || src.Height <= 0)
            {
                SetLightboxZoneWidths(LightboxZones, 0.2, 0.6, 0.2);
                return;
            }

            double scale = Math.Min(stageW / src.Width, stageH / src.Height);   // Uniform：取小的那一边
            double shownW = src.Width * scale;                                 // 图片实际显示宽（≤ W）
            double pad = Math.Max(0.0, (stageW - shownW) / 2);                 // 单侧留白

            SetLightboxZoneWidths(LightboxZones,
                pad + shownW * 0.20,      // 左栏：左侧留白 + 图片左侧 20%
                shownW * 0.60,            // 中栏：图片中间 60% —— 点它打开仓库
                pad + shownW * 0.20);     // 右栏：图片右侧 20% + 右侧留白
        }
        catch (Exception ex) { Logger.LogError("UpdateLightboxZones", ex); }
    }

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
            HideThumbPreview();          // 进放大层了就把悬停预览收掉，免得两层叠着
            WireLightboxDragHandlers();  // 每次打开都重挂一遍（内部先去重再挂），拖动才能一开就能用
            ResetLightboxZoom(mustReset: true);
            if (urls.Count == 0)
            {
                LightboxHint.Text = "目录中无截图，正在从仓库 README 中查找…";
                LightboxHint.Visibility = Visibility.Visible;
                LightboxImage.Source = null;
                LightboxCounter.Text = "";
                UpdateLightboxZones();   // 还没有图 ⇒ 先按兜底比例配好热区
                _ = ScrapeIntoLightboxAsync(m);
            }
            else
            {
                _ = ShowLightboxIndexAsync(_lightboxIndex);
            }
            Keyboard.Focus(this);
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

        // 标题里已经带了完整页码，页码再单独显示一遍会在同一行重复成"第 1/3 张  1 / 3"。
        // 这里留空而不是改成"共 N 张"：N 与标题里的分母是同一个数，同一行并列两处是静态冗余；
        // 而且留空后只有一个 TextBlock 有内容，本就是横向 StackPanel，不占位也不会留下多余间距。
        // 若以后要放别的补充信息，直接写在这里即可 —— 标题承担页码、这里承担补充，分工不变。
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
            UpdateLightboxZones();               // 图没了，热区退回兜底比例
            LightboxHint.Text = "这张图未能取到（网络或仓库路径变动）\n可以点图片中间打开仓库页面";
            return;
        }
        LightboxImage.Source = bmp;
        // 图片就位后再归零：等待取图期间 extent 已经变过一轮，在挂上 Source 之前归零，
        // 会被这一轮尺寸变化带来的偏移调整吃掉。必须放在上面那条守卫之后 ——
        // 否则被丢弃的旧图异步回调（翻页/关闭时）会把新图的偏移一起清零。
        ResetLightboxScroll();
        LightboxHint.Visibility = Visibility.Collapsed;
        UpdateLightboxZones();                   // 图片换好了 ⇒ 按新的显示区域重算三栏热区
    }

    private void LightboxClose()
    {
        ImageLightbox.Visibility = Visibility.Collapsed;
        LightboxImage.Source = null;
        _lightboxUrls = new List<string>();
        ResetLightboxZoom(mustReset: true);      // 缩放与悬停箭头一并复位，下次打开是干净状态
    }

    private void Lightbox_Backdrop_Click(object sender, MouseButtonEventArgs e)
    {
        // 点击**卡片外侧**的遮罩留白即关闭。卡片自身的留白不在这里判（见下），
        // 因为卡片的处理器会把事件吞掉、冒泡根本到不了这里。
        if (ReferenceEquals(e.OriginalSource, ImageLightbox)) LightboxClose();
    }

    /// <summary>
    /// 窗口缩小时卡片挨不满窗口，四周会露出遮罩 —— 点那里应当关闭（由上面那支接管）。
    /// 点**卡片的留白**（标题栏、图片与按钮之间的空隙）也要关闭，而点**图片本身**不关闭：
    /// 这个区分只能在这里做。原来这里是无条件 <c>e.Handled = true</c>，
    /// 事件不再冒泡到 <c>Lightbox_Backdrop_Click</c> ⇒ 那里原本用来判卡片的
    /// <c>ReferenceEquals(e.OriginalSource, LightboxCard)</c> 是**死分支**，点卡片留白永远不会关闭。
    /// 现在把判定挪进来：留白就地关闭，仍然 <c>e.Handled = true</c> 拦住图片点击。
    /// </summary>
    private void LightboxCard_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, LightboxCard)) LightboxClose();
        e.Handled = true;
    }
    private void Lightbox_Close_Click(object sender, MouseButtonEventArgs e) { LightboxClose(); e.Handled = true; }
    // 下面三个处理器绑的都是 MouseLeftButtonDown（按下即触发），但"点击"与"拖动画布"共用左键，
    // 按下那一刻根本区分不出来 ⇒ 按下就翻页/开仓库的话，用户拖一次画面就会顺带翻一页。
    // 所以这里改成：按下只登记待办，真正的执行搬到抬起时（LightboxStage_MouseLeftButtonUp）做判断。
    // 三个处理器各自只记自己的号码，执行统一走 ExecuteLightboxPending。
    private void Lightbox_Prev_Click(object sender, MouseButtonEventArgs e) { _lightboxPending = -1; e.Handled = true; }
    private void Lightbox_Next_Click(object sender, MouseButtonEventArgs e) { _lightboxPending = +1; e.Handled = true; }

    /// <summary>
    /// 点图片中间那块热区 ⇒ 打开这个插件的仓库页面。
    /// 与左右热区同理：按下只是登记待办（2 = 开仓库），等抬起时位移没到阈值才真去开，
    /// 否则拖着画面松手也会被判定成"点了图片"，弹出浏览器。
    /// 这里必须 e.Handled = true：中热区在卡片内部，事件放它冒泡上去就会被
    /// LightboxCard_MouseDown 当成"点了卡片留白"而把整个看图层关掉。
    /// </summary>
    private void Lightbox_Image_Click(object sender, MouseButtonEventArgs e)
    {
        _lightboxPending = 2;
        e.Handled = true;
    }

    /// <summary>
    /// 执行按下时登记的那件待办（翻页 / 开仓库）。只在"确认是点击"之后调用。
    /// 执行完立刻清空待办：一件待办只能兑现一次，否则抬起事件再来一次就会重复翻页。
    /// </summary>
    private void ExecuteLightboxPending()
    {
        int pending = _lightboxPending;
        _lightboxPending = 0;
        if (pending == -1) _ = ShowLightboxIndexAsync(_lightboxIndex - 1);
        else if (pending == +1) _ = ShowLightboxIndexAsync(_lightboxIndex + 1);
        else if (pending == 2) Lightbox_OpenRepo();
    }

    /// <summary>
    /// 按下热区后要开仓库页面的那段原逻辑，从 Lightbox_Image_Click 原样搬来（不是重写）。
    /// 没有图 / 还没拿到插件信息就什么都不做：不弹错、也不自行拼一个地址出来。
    /// 仓库地址仍须过 OpenExternalLink 的白名单校验，不绕开它去直接拉起浏览器。
    /// </summary>
    private void Lightbox_OpenRepo()
    {
        try
        {
            string? repo = _lightboxPlugin?.RepoUrl;
            // 就一件事：交给既有的外部链接通道去开。被白名单拦下时它自己会留痕并提示，
            // 这里不重复记一遍，也不另起一套打开方式。
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
        _lightboxDragStartOffsetX = LightboxScroll.HorizontalOffset;
        _lightboxDragStartOffsetY = LightboxScroll.VerticalOffset;
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
        }

        // 上限取 max(0, extent - viewport)：图没放大时没有可滚动余量，直接夹到 0，
        // 不夹的话 ScrollTo*Offset 会接受越界值，松手后画面自己弹一下。
        double maxX = Math.Max(0, LightboxScroll.ExtentWidth - LightboxScroll.ViewportWidth);
        double maxY = Math.Max(0, LightboxScroll.ExtentHeight - LightboxScroll.ViewportHeight);
        LightboxScroll.ScrollToHorizontalOffset(Math.Max(0, Math.Min(maxX, _lightboxDragStartOffsetX - dx)));
        LightboxScroll.ScrollToVerticalOffset(Math.Max(0, Math.Min(maxY, _lightboxDragStartOffsetY - dy)));
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
        if (LightboxStage.IsMouseCaptured) LightboxStage.ReleaseMouseCapture();

        if (moved) { _lightboxPending = 0; return; }   // 拖动：待办直接作废，绝不顺带翻页
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
    ///   本动画用 <c>FillBehavior.Stop</c>（跑完不占坑），动画一停、值就回落到**基础值**。
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
