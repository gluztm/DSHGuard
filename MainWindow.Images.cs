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
            if (urls.Count == 0)
            {
                LightboxHint.Text = "目录中无截图，正在从仓库 README 中查找…";
                LightboxHint.Visibility = Visibility.Visible;
                LightboxImage.Source = null;
                LightboxCounter.Text = "";
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

        LightboxTitle.Text = $"{_lightboxName} · 第 {index + 1}/{_lightboxUrls.Count} 张";
        LightboxCounter.Text = $"{index + 1} / {_lightboxUrls.Count}";
        LightboxPrev.IsEnabled = index > 0;
        LightboxNext.IsEnabled = index < _lightboxUrls.Count - 1;
        LightboxHint.Text = "正在加载图片…";
        LightboxHint.Visibility = Visibility.Visible;

        var bmp = await LoadImageAsync(url, full: true);
        if (ImageLightbox.Visibility != Visibility.Visible || _lightboxIndex != index) return;   // 等待期间已翻页或关闭
        if (bmp == null)
        {
            LightboxImage.Source = null;
            LightboxHint.Text = "这张图未能取到（网络或仓库路径变动）\n可以点「看原图」在浏览器里试";
            return;
        }
        LightboxImage.Source = bmp;
        LightboxHint.Visibility = Visibility.Collapsed;
    }

    private void LightboxClose()
    {
        ImageLightbox.Visibility = Visibility.Collapsed;
        LightboxImage.Source = null;
        _lightboxUrls = new List<string>();
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
    private void Lightbox_Prev_Click(object sender, MouseButtonEventArgs e) { _ = ShowLightboxIndexAsync(_lightboxIndex - 1); e.Handled = true; }
    private void Lightbox_Next_Click(object sender, MouseButtonEventArgs e) { _ = ShowLightboxIndexAsync(_lightboxIndex + 1); e.Handled = true; }

    private void Lightbox_OpenInBrowser_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (_lightboxUrls.Count == 0 || _lightboxIndex >= _lightboxUrls.Count) return;
            string url = _lightboxUrls[_lightboxIndex];
            // raw 直链在部分网络环境下无法访问，改用 GitHub 页面地址
            var m = System.Text.RegularExpressions.Regex.Match(url,
                @"^https://raw\.githubusercontent\.com/([^/]+/[^/]+)/([^/]+)/(.+)$");
            string open = m.Success
                ? $"https://github.com/{m.Groups[1].Value}/blob/{m.Groups[2].Value}/{m.Groups[3].Value}"
                : url;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(open) { UseShellExecute = true });
        }
        catch (Exception ex) { Logger.LogError("Lightbox_OpenInBrowser", ex); }
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
