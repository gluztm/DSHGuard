using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DSHGuard;

/// <summary>插件页「寻找插件」视图：数据取自 Oh My DSH 社区收录，卡片显示收藏数、下载数、更新时间与适配版本，支持打开仓库与安装。</summary>
public partial class MainWindow : Window
{
    private PluginMarket.MarketCatalog? _market;
    private bool _marketTab;                    // false = 本地插件（默认），true = 寻找插件
    private bool _marketLoading;
    private bool _marketBusy;
    private int _marketLimit = 30;
    private PluginMarket.MarketSort _marketSort = PluginMarket.MarketSort.Stars;
    private bool _marketSortDesc = true;        // 排序方向：默认从多到少 / 从新到旧
    private int _marketTimeDays;                // 更新时间范围（0 = 全部时间）
    private bool _marketOnlyAdapted;            // 仅显示适配当前引擎版本的条目
    private bool _compatScanning;               // 正在逐条核对适配情况
    private string _marketCategory = "全部";
    private bool _marketCatsExpanded;           // 分类标签是否已展开
    private double _catsBuiltWidth = -1;        // 上次构建标签时的面板宽度（宽度变化超过阈值才重建）
    private string? _metaFilling;
    private int _lastMarketListCount;           // 最近一次过滤后的条数（自检断言用）
    private DispatcherTimer? _marketSearchTimer;

    private const int MarketPageSize = 30;

    // ══════════════ 页签切换 ══════════════
    private void PluginTab_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border b) return;
        ShowPluginsTab((b.Tag as string) == "market");
    }

    private void ShowPluginsTab(bool market)
    {
        try
        {
            _marketTab = market;

            PluginToolbar.Visibility = market ? Visibility.Collapsed : Visibility.Visible;
            InstalledScroll.Visibility = market ? Visibility.Collapsed : Visibility.Visible;
            PluginsSummaryText.Visibility = market ? Visibility.Collapsed : Visibility.Visible;
            MarketToolbar.Visibility = market ? Visibility.Visible : Visibility.Collapsed;
            MarketHost.Visibility = market ? Visibility.Visible : Visibility.Collapsed;
            MarketSummaryText.Visibility = market ? Visibility.Visible : Visibility.Collapsed;

            PaintSegment(MarketTabBtn, market);
            PaintSegment(InstalledTabBtn, !market);

            if (_marketTab) _ = EnsureMarketAsync();
            else RenderPlugins();

            // 切换页签后重算「回到顶部」按钮的显隐，否则会残留另一个列表的按钮
            UpdateTopButtons();
        }
        catch (Exception ex)
        {
            Logger.LogError("ShowPluginsTab", ex);
            try { MarketSummaryText.Text = "切换页签没能完成，再点一次即可"; } catch { }
        }
    }

    /// <summary>分段控件：选中为蓝底白字，未选为透明灰字，带 180ms 颜色渐变。</summary>
    /// <remarks>不能直接对 XAML 中写死的 Background="Transparent" 做动画：该画刷是 WPF 内置的已冻结对象，BeginAnimation 会抛异常；因此每次都新建画刷后再动画。</remarks>
    private static void PaintSegment(Border seg, bool on)
    {
        Color target = on ? Color.FromRgb(0x00, 0x7A, 0xFF) : Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF);
        Color from = (seg.Background as SolidColorBrush)?.Color ?? Colors.Transparent;

        var brush = new SolidColorBrush(target);
        seg.Background = brush;
        if (from != target)
            brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation
            {
                From = from,
                To = target,
                Duration = TimeSpan.FromMilliseconds(180),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });

        if (seg.Child is TextBlock t)
        {
            t.Foreground = new SolidColorBrush(on ? Colors.White : Color.FromRgb(0x8E, 0x8E, 0x93));
            t.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    // ══════════════ 目录加载 ══════════════
    private async Task EnsureMarketAsync(bool force = false)
    {
        if (_marketLoading) return;
        if (_market != null && !force)
        {
            BuildCategoryChips();     // 已加载分支同样需要重建分类标签，否则切换页签后标签会丢失
            RenderMarket();
            return;
        }

        _marketLoading = true;
        try
        {
            MarketSummaryText.Text = "正在获取社区插件目录…（首次加载约需 1~2 秒）";
            MarketPanel.Children.Clear();
            MarketPanel.Children.Add(new TextBlock
            {
                Text = "正在获取社区插件目录…",
                FontSize = 12,
                Margin = new Thickness(0, 18, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93))
            });

            var cat = await PluginMarket.LoadAsync(force);
            _market = cat;

            if (cat.Error != null)
            {
                MarketPanel.Children.Clear();
                MarketPanel.Children.Add(new TextBlock
                {
                    Text = "⚠ " + cat.Error,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A))
                });
            }

            BuildCategoryChips();
            RenderMarket();
        }
        catch (Exception ex)
        {
            Logger.LogError("EnsureMarketAsync", ex);
            MarketSummaryText.Text = "目录没能取到，可点「刷新」重试；" + LogPromise("详细原因已记入日志，可在「日志」页查看。");
        }
        finally { _marketLoading = false; }
    }

    private void MarketRefresh_Click(object sender, MouseButtonEventArgs e)
    {
        ResetImageFailures();          // 清除取图失败记录，使之前未取到的图片可重新下载
        _ = EnsureMarketAsync(true);
    }

    private void MarketSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // 输入停顿 300ms 后再渲染，避免每次按键都触发重排
        _marketSearchTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _marketSearchTimer.Tick -= MarketSearchTick;
        _marketSearchTimer.Tick += MarketSearchTick;
        _marketSearchTimer.Stop();
        _marketSearchTimer.Start();
    }

    private void MarketSearchTick(object? sender, EventArgs e)
    {
        _marketSearchTimer?.Stop();
        _marketLimit = MarketPageSize;
        RenderMarket();
    }

    // ══════════════ 筛选下拉（排序 + 过滤） ══════════════
    private void MarketFilter_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            MarketFilterPopup.IsOpen = !MarketFilterPopup.IsOpen;
            if (MarketFilterPopup.IsOpen) PaintFilterMenu();
            e.Handled = true;
        }
        catch (Exception ex) { Logger.LogError("MarketFilter_Click", ex); }
    }

    private void MarketSort_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border b) return;
        _marketSort = (b.Tag as string) switch
        {
            "downloads" => PluginMarket.MarketSort.Downloads,
            "published" => PluginMarket.MarketSort.Published,
            _ => PluginMarket.MarketSort.Stars
        };
        _marketLimit = MarketPageSize;
        MarketFilterPopup.IsOpen = false;
        PaintFilterMenu();
        RenderMarket();
        if (e != null) e.Handled = true;
    }

    private void MarketSortDir_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border b) return;
        _marketSortDesc = (b.Tag as string) != "asc";
        _marketLimit = MarketPageSize;
        MarketFilterPopup.IsOpen = false;
        PaintFilterMenu();
        RenderMarket();
        if (e != null) e.Handled = true;
    }

    private void MarketTime_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border b || !int.TryParse(b.Tag as string, out int days)) return;
        _marketTimeDays = days;
        _marketLimit = MarketPageSize;
        MarketFilterPopup.IsOpen = false;
        PaintFilterMenu();
        RenderMarket();
        if (e != null) e.Handled = true;
    }

    private void MarketHost_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border b) return;
        _marketOnlyAdapted = (b.Tag as string) == "adapted";
        _marketLimit = MarketPageSize;
        MarketFilterPopup.IsOpen = false;
        PaintFilterMenu();
        RenderMarket();
        if (_marketOnlyAdapted) _ = ScanCompatAsync();
        if (e != null) e.Handled = true;
    }

    /// <summary>刷新下拉选中态与工具条「筛选」按钮的文案。</summary>
    private void PaintFilterMenu()
    {
        PaintMenuRow(FilterFieldDownloads, _marketSort == PluginMarket.MarketSort.Downloads);
        PaintMenuRow(FilterFieldStars, _marketSort == PluginMarket.MarketSort.Stars);
        PaintMenuRow(FilterFieldPublished, _marketSort == PluginMarket.MarketSort.Published);
        PaintMenuRow(FilterDirDesc, _marketSortDesc);
        PaintMenuRow(FilterDirAsc, !_marketSortDesc);

        foreach (var (row, days) in new[]
        {
            (FilterTimeAll, 0), (FilterTime7, 7), (FilterTime30, 30), (FilterTime90, 90), (FilterTime365, 365)
        })
            PaintMenuRow(row, _marketTimeDays == days);

        PaintMenuRow(FilterHostAll, !_marketOnlyAdapted);
        PaintMenuRow(FilterHostAdapted, _marketOnlyAdapted);

        string current = VersionMemory.Pin.Length > 0 ? VersionMemory.Pin : _currentDshVersion;
        if (FilterHostAdaptedText != null) FilterHostAdaptedText.Text = $"适配当前版本 {current}";

        string field = _marketSort switch
        {
            PluginMarket.MarketSort.Downloads => "下载量",
            PluginMarket.MarketSort.Published => "更新时间",
            _ => "收藏数"
        };
        string arrow = _marketSortDesc ? "↓" : "↑";
        var tail = new List<string>();
        if (_marketTimeDays > 0) tail.Add($"近 {_marketTimeDays} 天");
        if (_marketOnlyAdapted) tail.Add("已适配");
        MarketFilterText.Text = $"筛选 · {field}{arrow}" + (tail.Count > 0 ? " · " + string.Join(" · ", tail) : "");
        WirePopupContent(MarketFilterPopup);
    }

    /// <summary>下拉选项行是否选中（hover 结束要按它恢复底色）。</summary>
    private static readonly DependencyProperty MenuRowOnProperty =
        DependencyProperty.RegisterAttached("MenuRowOn", typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

    /// <summary>下拉选项行是否已挂过 hover 处理（避免重复挂）。</summary>
    private static readonly DependencyProperty MenuRowHoverReadyProperty =
        DependencyProperty.RegisterAttached("MenuRowHoverReady", typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

    private static void PaintMenuRow(Border row, bool on)
    {
        if (row == null) return;
        row.SetValue(MenuRowOnProperty, on);
        row.Cursor = Cursors.Hand;      // 让 ButtonFx 也能覆盖到弹层里的这些行
        row.Background = new SolidColorBrush(on ? Color.FromArgb(0x38, 0x00, 0x7A, 0xFF) : Colors.Transparent);
        if (row.Child is TextBlock t)
        {
            string baseText = t.Text.TrimStart('✓', ' ');
            t.Text = (on ? "✓ " : "") + baseText;
            t.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }

        // 悬停亮底（选中行在原蓝底上再亮一档，一眼看得出是跟鼠标走的）
        if (!(bool)row.GetValue(MenuRowHoverReadyProperty))
        {
            row.SetValue(MenuRowHoverReadyProperty, true);
            row.MouseEnter += (s, _) =>
            {
                if (s is Border b)
                {
                    bool selected = (bool)b.GetValue(MenuRowOnProperty);
                    b.Background = new SolidColorBrush(selected
                        ? Color.FromArgb(0x66, 0x00, 0x7A, 0xFF)
                        : Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF));
                }
            };
            row.MouseLeave += (s, _) =>
            {
                if (s is Border b) PaintMenuRow(b, (bool)b.GetValue(MenuRowOnProperty));
            };
        }
    }

    /// <summary>弹层内容不在窗口视觉树里，动效要单独挂一次。</summary>
    private static void WirePopupContent(System.Windows.Controls.Primitives.Popup popup)
        => ButtonFx.Wire(popup?.Child);

    /// <summary>把两个筛选下拉都收起来（最小化 / 进托盘 / 切程序 / 换页时调用）；顺带收起悬停预览。</summary>
    internal void CloseFilterPopups()
    {
        try
        {
            if (MarketFilterPopup != null) MarketFilterPopup.IsOpen = false;
            if (InstalledFilterPopup != null) InstalledFilterPopup.IsOpen = false;
            HideThumbPreview();     // 同样的时机也要收掉"悬停半屏预览"，免得它留在屏幕上
        }
        catch { }
    }

    private void MarketCategory_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border b || b.Tag is not string slug) return;
        if (slug == "全部") { _marketCatsExpanded = false; }          // 恢复收起状态，节省面板空间
        _marketCategory = slug;
        _marketLimit = MarketPageSize;
        BuildCategoryChips();
        RenderMarket();
    }

    /// <summary>分类标签的「更多 / 收起」：整块展开或收起，不产生横向滚动条。</summary>
    private void MarketCatsToggle_Click(object sender, MouseButtonEventArgs e)
    {
        _marketCatsExpanded = !_marketCatsExpanded;
        BuildCategoryChips();
        e.Handled = true;
    }

    /// <summary>面板宽度变化（窗口或布局变化）时重建标签；由 SizeChanged 事件驱动，不做轮询。</summary>
    private void MarketCategoryPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        try
        {
            if (_market == null || _marketCatsExpanded) return;
            if (e.NewSize.Width <= 50) return;
            if (Math.Abs(e.NewSize.Width - _catsBuiltWidth) < 40) return;   // 宽度变化小于 40 时不重建，避免抖动
            BuildCategoryChips();
        }
        catch (Exception ex) { Logger.LogError("MarketCategoryPanel_SizeChanged", ex); }
    }

    /// <summary>分类 slug → 中文标签（界面统一显示中文标签，不直接显示原始 slug）。</summary>
    private string CategoryLabel(string slug)
    {
        if (_market != null && _market.CategoryZh.TryGetValue(slug, out var zh) && zh.Length > 0) return zh;
        return slug;
    }

    /// <summary>点击卡片上的分类标签，切换到对应分类的筛选列表。</summary>
    private void MarketCategoryLink_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (sender is not TextBlock t || t.Tag is not string slug || slug.Length == 0) return;
            _marketCategory = slug;
            _marketLimit = MarketPageSize;
            _marketCatsExpanded = false;
            BuildCategoryChips();
            RenderMarket();
            MarketScroll?.ScrollToTop();
            AddEvent($"已按分类「{t.Text}」筛选插件");
            if (e != null) e.Handled = true;      // 自检调用时不传事件参数
        }
        catch (Exception ex) { Logger.LogError("MarketCategoryLink_Click", ex); }
    }

    private void MarketMore_Click(object sender, MouseButtonEventArgs e)
    {
        _marketLimit += MarketPageSize;
        RenderMarket();
    }

    // ══════════════ 回到顶部（两个列表共用） ══════════════

    /// <summary>列表滚动超过一屏时显示「回到顶部」按钮，点击后带缓动滚回顶部。
    /// 顺带收起悬停预览：底下的缩略图已经滚走了，留着会变成一块挡屏的图（且再也收不到 MouseLeave）。</summary>
    private void MarketScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange != 0 || e.HorizontalChange != 0) HideThumbPreview();
        UpdateTopButtons();
    }

    private void InstalledScroll_ScrollChanged(object sender, ScrollChangedEventArgs e) => UpdateTopButtons();

    private void UpdateTopButtons()
    {
        try
        {
            // 按钮需跟随各自列表的可见性与滚动位置；只在切换页签时更新会导致两个按钮同时残留。
            if (MarketTopBtn != null)
                MarketTopBtn.Visibility = MarketScroll != null
                    && MarketScroll.IsVisible
                    && MarketScroll.VerticalOffset > 120
                    ? Visibility.Visible : Visibility.Collapsed;
            if (InstalledTopBtn != null)
                InstalledTopBtn.Visibility = InstalledScroll != null
                    && InstalledScroll.IsVisible
                    && InstalledScroll.VerticalOffset > 120
                    ? Visibility.Visible : Visibility.Collapsed;
        }
        catch { }
    }

    private void ScrollTop_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border b) return;
        var sv = (b.Tag as string) == "installed" ? InstalledScroll : MarketScroll;
        if (sv == null) return;
        // 逐帧按比例收敛偏移量，形成缓动滚动效果
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            double off = sv.VerticalOffset;
            if (off <= 1.5) { sv.ScrollToTop(); timer.Stop(); return; }
            sv.ScrollToVerticalOffset(off * 0.72);
        };
        timer.Start();
        e.Handled = true;
    }

    // ══════════════ 渲染 ══════════════
    private void BuildCategoryChips()
    {
        MarketCategoryPanel.Children.Clear();
        if (_market == null) return;

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in _market.Plugins)
            foreach (var c in p.Categories)
                counts[c] = counts.TryGetValue(c, out int n) ? n + 1 : 1;

        var ordered = counts.Where(kv => kv.Value > 0)
                            .OrderByDescending(kv => kv.Value)
                            .Select(kv => kv.Key)
                            .ToList();

        var all = new List<(string Slug, string Label, int Count)> { ("全部", "全部", _market.Plugins.Count) };
        foreach (var slug in ordered)
        {
            string label = _market.CategoryZh.TryGetValue(slug, out var zh) && zh.Length > 0 ? zh : slug;
            all.Add((slug, label, counts[slug]));
        }

        // 收起时按可用宽度估算两行可容纳的标签数，其余折叠进「更多分类」。
        // 不能用 Dispatcher.BeginInvoke 等待布局完成后再计算：面板未测量到宽度时会以 Loaded
        // 优先级无限重排，阻塞 UI 线程；改用 SizeChanged 事件驱动。
        double width = MarketCategoryPanel.ActualWidth;
        _catsBuiltWidth = width;
        int fit = _marketCatsExpanded ? all.Count : CountChipsInTwoRows(all, width);

        for (int i = 0; i < fit && i < all.Count; i++)
            MarketCategoryPanel.Children.Add(CategoryChip(all[i].Label, all[i].Count, all[i].Slug));

        if (all.Count > fit || _marketCatsExpanded)
            MarketCategoryPanel.Children.Add(CatsToggleChip(_marketCatsExpanded, Math.Max(0, all.Count - fit)));
    }

    /// <summary>按字宽估算两行可容纳的标签数（允许少量误差）。</summary>
    private static int CountChipsInTwoRows(List<(string Slug, string Label, int Count)> all, double width)
    {
        if (width <= 50) width = 560;
        int chips = 0, lines = 1;
        double lineW = 0;
        foreach (var item in all)
        {
            double w = EstimateChipWidth(item.Label) + 5;
            if (lineW + w > width) { lines++; lineW = 0; if (lines > 2) break; }
            lineW += w;
            chips++;
        }
        return Math.Max(4, chips);
    }

    private static double EstimateChipWidth(string label)
    {
        double w = 24 + 6;   // 左右内边距 + 计数后缀余量
        foreach (char c in label) w += c > 0x2E80 ? 11.5 : 6.5;
        return w;
    }

    private Border CatsToggleChip(bool expanded, int hidden)
    {
        var chip = new Border
        {
            CornerRadius = new CornerRadius(13),
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 5, 0),
            Cursor = Cursors.Hand,
            Background = new SolidColorBrush(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF)),
            ToolTip = expanded ? "收起分类" : $"展开全部 {hidden} 个分类",
            Child = new TextBlock
            {
                Text = expanded ? "收起 ⌃" : $"更多分类 ⌄",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x5A, 0xC8, 0xFA))
            }
        };
        chip.MouseLeftButtonDown += MarketCatsToggle_Click;
        return chip;
    }

    private Border CategoryChip(string label, int count, string slug)
    {
        bool on = _marketCategory == slug;
        var chip = new Border
        {
            CornerRadius = new CornerRadius(13),
            Padding = new Thickness(11, 4, 11, 4),
            Margin = new Thickness(0, 0, 5, 5),
            Cursor = Cursors.Hand,
            Background = new SolidColorBrush(on ? Color.FromRgb(0x00, 0x7A, 0xFF) : Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            Tag = slug,
            ToolTip = slug == "全部" ? "全部分类" : $"{slug} · {count} 个",
            Child = new TextBlock
            {
                Text = $"{label} ({ShortCount(count)})",
                FontSize = 11,
                Foreground = new SolidColorBrush(on ? Colors.White : Color.FromRgb(0xC7, 0xC7, 0xCC))
            }
        };
        chip.MouseLeftButtonDown += MarketCategory_Click;
        return chip;
    }

    private void PaintSortChips()
    {
        // 已废弃：排序/筛选已移至「筛选」下拉（PaintFilterMenu）；保留空方法以兼容既有调用点。
        PaintFilterMenu();
    }

    private void RenderMarket(bool preserveScroll = false)
    {
        try
        {
            if (_market == null) return;
            PaintFilterMenu();

            double offset = preserveScroll ? MarketScroll.VerticalOffset : 0;
            var list = PluginMarket.Filter(_market, MarketSearchBox.Text, _marketCategory, _marketSort, _marketSortDesc);
            if (_marketTimeDays > 0) list = list.Where(p => PluginMarket.WithinDays(p, _marketTimeDays)).ToList();
            if (_marketOnlyAdapted)
                list = list.Where(p => PluginMarket.IsAdapted(p)).ToList();
            var page = list.Take(_marketLimit).ToList();
            _lastMarketListCount = list.Count;

            var cards = new List<UIElement>();
            if (list.Count == 0)
            {
                cards.Add(new TextBlock
                {
                    Text = _market.Plugins.Count == 0
                        ? "目录中未取到条目（可能网络受限），可点「刷新」重试。"
                        : "没有匹配的插件——换个关键词、关掉几个筛选项，或者点「全部」看看。",
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 16, 0, 0),
                    Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93))
                });
            }
            else
            {
                foreach (var m in page) cards.Add(BuildMarketCard(m));
            }
            // 先按当前主题离屏渲染再一次性添加到面板，否则日间模式会先按深色闪烁一帧
            SwapThemed(MarketPanel, cards);

            MarketMoreBtn.Visibility = list.Count > page.Count ? Visibility.Visible : Visibility.Collapsed;
            if (list.Count > page.Count)
                MarketMoreText.Text = $"加载更多（还有 {list.Count - page.Count} 个）";

            if (!_marketBusy)
            {
                string hash = _marketCategory == "全部" ? "" : $" · 分类 {CategoryLabel(_marketCategory)}";
                string search = string.IsNullOrWhiteSpace(MarketSearchBox.Text) ? "" : $" · 搜索「{MarketSearchBox.Text.Trim()}」";
                string range = _marketTimeDays > 0 ? $" · 近 {_marketTimeDays} 天更新" : "";
                string adapted = _marketOnlyAdapted ? " · 只看已适配" : "";
                string scanning = _compatScanning ? " · 正在逐条核对适配情况…" : "";
                string stale = _market.Stale ? "⚠ 未连接社区目录，显示的是本地存的那份 · " : "";
                string skip = _market.Skipped > 0 ? $" · ⚠ 有 {_market.Skipped} 条未读出" : "";
                string updated = _market.Updated.Length > 0 ? $" · 收录更新 {_market.Updated}" : "";
                int withShots = _market.Plugins.Count(p => p.Screenshots.Count > 0);
                MarketSummaryText.Text =
                    $"{stale}社区收录 {_market.Plugins.Count} 个插件（{withShots} 个带截图）· 当前显示 {page.Count}/{list.Count}{hash}{search}{range}{adapted}{scanning}{skip}\n" +
                    $"数据来自社区收录{updated} · 收藏/下载数随页面逐条补齐";
            }

            if (preserveScroll && offset > 0)
                Dispatcher.BeginInvoke(new Action(() => MarketScroll.ScrollToVerticalOffset(offset)), DispatcherPriority.Loaded);

            _ = FillVisibleMetaAsync(page);

            // 渲染完也重算一次（列表可见性/滚动位置都可能变），并补刷主题
            Dispatcher.BeginInvoke(new Action(() => { UpdateTopButtons(); ApplyThemeSoon(); }), DispatcherPriority.Loaded);
        }
        catch (Exception ex)
        {
            Logger.LogError("RenderMarket", ex);
            // 渲染异常需在界面上可见，不做静默处理
            try { MarketSummaryText.Text = "市场内容没能显示完整，刷新一次即可；" + LogPromise("详细原因已记入日志，可在「日志」页查看。"); } catch { }
        }
    }

    /// <summary>勾选「适配当前版本」后，按当前排序逐条核对靠前的候选（并发 6），并随进度刷新列表。</summary>
    private async Task ScanCompatAsync()
    {
        if (_compatScanning || _market == null) return;
        _compatScanning = true;
        try
        {
            string current = VersionMemory.Pin.Length > 0 ? VersionMemory.Pin : _currentDshVersion;
            var candidates = PluginMarket.Filter(_market, MarketSearchBox.Text, _marketCategory, _marketSort, _marketSortDesc)
                .Where(p => !p.MetaLoaded && p.Npm.Length > 0)
                .Take(90).ToList();
            RenderMarket(preserveScroll: true);          // 先刷新列表，使核对状态立即显示

            int done = 0;
            foreach (var batch in candidates.Chunk(6))
            {
                await Task.WhenAll(batch.Select(m => PluginMarket.FillMetaAsync(m, current)));
                done += batch.Length;
                if (done % 18 == 0) RenderMarket(preserveScroll: true);
            }
        }
        catch (Exception ex) { Logger.LogError("ScanCompatAsync", ex); }
        finally
        {
            _compatScanning = false;
            RenderMarket(preserveScroll: true);
        }
    }

    /// <summary>为当前页中尚未查询 npm 的条目补充元数据（限制并发，避免压垮镜像源）。</summary>
    private async Task FillVisibleMetaAsync(List<PluginMarket.MarketPlugin> page)    {
        if (_metaFilling != null) return;      // 同一时间仅允许一轮执行
        var todo = page.Where(p => !p.MetaLoaded && p.Npm.Length > 0).Take(15).ToList();
        if (todo.Count == 0) return;

        _metaFilling = "running";
        int done = 0, changed = 0;
        try
        {
            string current = VersionMemory.Pin.Length > 0 ? VersionMemory.Pin : _currentDshVersion;
            foreach (var batch in todo.Chunk(3))
            {
                var results = await Task.WhenAll(batch.Select(m => PluginMarket.FillMetaAsync(m, current)));
                done += batch.Length;
                changed += results.Count(r => r);
                if (changed > 0 && (done % 6 == 0)) RenderMarket(preserveScroll: true);
            }
        }
        catch (Exception ex) { Logger.LogError("FillVisibleMetaAsync", ex); }
        finally
        {
            _metaFilling = null;
            if (changed > 0) RenderMarket(preserveScroll: true);
        }
    }

    private static string ShortCount(long n)
        => n >= 100000000 ? (n / 100000000.0).ToString("0.#") + "亿"
         : n >= 10000 ? (n / 10000.0).ToString("0.#") + "万"
         : n >= 1000 ? (n / 1000.0).ToString("0.#") + "k"
         : n.ToString();

    private Border BuildMarketCard(PluginMarket.MarketPlugin m)
    {
        bool installed = IsInstalledInProfile(m, out string sameNameLocal);

        var card = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 8)
        };
        var sp = new StackPanel();
        card.Child = sp;

        // ── 第一行：名字 + 右侧动作。
        // 名字必须使用单个 TextBlock：横向 StackPanel 内的超长文本不会省略，会覆盖按钮并溢出卡片。
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        string link = m.RepoUrl.Length > 0 ? m.RepoUrl : m.PageUrl;
        var nameText = new TextBlock
        {
            Text = m.DisplayName + (link.Length > 0 ? " ↗" : ""),     // 规范化后的显示名（原始名见 ToolTip）
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(link.Length > 0 ? Color.FromRgb(0x5A, 0xC8, 0xFA) : Colors.White),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            // ★ 命中区域只给文字本身：这一列是星号列，TextBlock 默认水平对齐是 Stretch
            //   ⇒ 命中框撑满整列，"名字右边的空白处"点下去也吃 MouseLeftButtonDown（会跳链接）。
            //   改成 Left 后命中框收缩到文字实际宽度；名字过长时仍被这一列夹住、照旧按
            //   CharacterEllipsis 截断。排版 / 字号 / 颜色 / 悬停效果一律不变，只改命中区域。
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 8, 0)
        };
        if (link.Length > 0)
        {
            nameText.Cursor = Cursors.Hand;
            nameText.Tag = link;
            nameText.ToolTip = (m.DisplayName == m.Name ? "" : $"原始名：{m.Name}\n") + "打开仓库：" + link;
            nameText.MouseLeftButtonDown += PluginName_Click;
            AddLinkHover(nameText);
        }
        Grid.SetColumn(nameText, 0);
        head.Children.Add(nameText);

        // 作者与分类单独占一行：作者名可点击跳转 GitHub 主页，分类显示在其后
        bool subRowReady = false;
        string catText = m.CategoryText(_market?.CategoryZh ?? new Dictionary<string, string>());
        var subRow = new Grid { Margin = new Thickness(0, 3, 0, 0) };
        subRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        subRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        if (m.Owner.Length > 0)
        {
            // 作者区：头像 + 名字是一个整体的超链接（点头像或点名字都跳作者主页）
            var authorRow = BuildAuthorLink(m.Owner, m.AuthorUrl, 16, 11);
            Grid.SetColumn(authorRow, 0);
            subRow.Children.Add(authorRow);
        }
        // 分类标签为链接：点击后切换到该分类的筛选列表
        if (m.Categories.Count > 0)
        {
            var catRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (m.Owner.Length > 0)   // 有作者时补一个分隔符，避免作者名与分类连在一起
                catRow.Children.Add(new TextBlock
                {
                    Text = "   ·   ",
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x6E, 0x6E, 0x73))
                });
            bool first = true;
            foreach (string slug in m.Categories)
            {
                string label = _market != null && _market.CategoryZh.TryGetValue(slug, out var zh) && zh.Length > 0 ? zh : slug;
                if (!first) catRow.Children.Add(new TextBlock
                {
                    Text = "  ·  ",
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x6E, 0x6E, 0x73))
                });
                first = false;

                var catLink = new TextBlock
                {
                    Text = label,
                    FontSize = 11,
                    Cursor = Cursors.Hand,
                    VerticalAlignment = VerticalAlignment.Center,
                    Tag = slug,
                    ToolTip = $"只看「{label}」分类的插件"
                };
                AddLinkHover(catLink, "#8FB8D8", "#BFE3FB");
                catLink.MouseLeftButtonDown += MarketCategoryLink_Click;
                catRow.Children.Add(catLink);
            }
            Grid.SetColumn(catRow, 1);
            subRow.Children.Add(catRow);
        }
        else if (catText.Length > 0)
        {
            var catLine = new TextBlock
            {
                Text = "   ·   " + catText,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93))
            };
            Grid.SetColumn(catLine, 1);
            subRow.Children.Add(catLine);
        }
        if (subRow.Children.Count > 0) subRowReady = true;

        string src = m.InstallSource;

        // ★ 判据顺序：**"正在安装的这一条"排在最前**，先于"已安装"。
        //
        // 【现场 bug ②】安装期间列表只要重绘一次（补元数据 / 取图 / 改筛选 / 切分类都会重建整张卡片），
        // 若这时 IsInstalledInProfile(m) 已经为真（pnpm 把依赖写进清单的那一刻就算"已安装"，
        // 而安装其实还在收尾），卡片就被换成绿色「已安装」徽章 —— 正在跑的那次安装**再也没有停止入口**，
        // 只能干等它跑完（点了没反应）。所以正在安装的这一条在**安装结束之前**始终保留
        // 「安装中…／停止」的按钮形态（结束的复位只有 EndInstallState 一个入口）。
        // ★ 同名不同包（裸名相同、命名空间不同）：显中性徽章，绝不显示绿「已安装」。
        //   徽章与安装入口**并排**：既如实说明"不是同一个包"，也不挡用户装市场里这个包。
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (!installed && sameNameLocal.Length > 0)
            actions.Children.Add(BuildSameNameBadge(m, sameNameLocal));

        if (IsInstallingThis(m))
        {
            var btnLive = BuildInstallButton();
            btnLive.Tag = m;
            btnLive.ToolTip = $"安装源：{src}\n安装完成后需重启 DSH 才会生效\n安装期间鼠标移入按钮可点「停止」中止";
            btnLive.Click += MarketInstall_Click;
            // 接管成"安装中"那颗：挂悬停、记账、按当前三态画一次（此刻是新造的按钮，画出来是蓝「安装中…」）
            AdoptInstallButton(btnLive);
            actions.Children.Add(btnLive);
        }
        else if (installed)
        {
            var badge = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(9, 5, 9, 5),
                Background = new SolidColorBrush(Color.FromArgb(0x24, 0x34, 0xC7, 0x59)),
                ToolTip = "该插件已在你的 DSH 配置中（可在「本地插件」关闭或卸载）",
                Child = new TextBlock
                {
                    Text = "已安装",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x59))
                }
            };
            actions.Children.Add(badge);
        }
        else if (src.Length > 0)
        {
            var btn = BuildInstallButton();
            btn.Tag = m;
            btn.ToolTip = $"安装源：{src}\n安装完成后需重启 DSH 才会生效";
            btn.Click += MarketInstall_Click;
            // 安装期间列表被重绘（补元数据 / 取图 / 改筛选 / 切分类都会重建整张卡片）：
            // 新按钮必须当场接管成"安装中"那颗，否则会冒出一颗绿色「安装」，
            // 而 _installBtn / 悬停处理还挂在已经下树的旧按钮上。
            if (IsInstallingThis(m)) AdoptInstallButton(btn);
            actions.Children.Add(btn);
        }
        else
        {
            var btn = MiniButton("看仓库", "#FF9F0A");
            btn.Tag = link;
            btn.IsEnabled = link.Length > 0;
            btn.ToolTip = "该收录没有可直接安装的插件包，请打开仓库按作者说明安装";
            // 与安装按钮同一口径：点完不留焦点虚框 + 像素对齐（纯视觉属性，不动配色/文案/尺寸）
            btn.Focusable = false;
            btn.FocusVisualStyle = null;
            btn.UseLayoutRounding = true;
            btn.SnapsToDevicePixels = true;
            btn.Click += (_, _) => { if (link.Length > 0) OpenUrl(link); };
            actions.Children.Add(btn);
        }
        Grid.SetColumn(actions, 1);
        head.Children.Add(actions);
        sp.Children.Add(head);
        // 作者行必须在名字行之后添加，否则会显示在插件名之前
        if (subRowReady) sp.Children.Add(subRow);

        // ── 第三行：收藏 / 下载 / 更新时间 / 声明的适配版本 ──
        var meta = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        meta.Children.Add(MetaText("★ " + ShortCount(m.Stars), "#FFD60A", "GitHub 收藏数"));
        meta.Children.Add(MetaText("↓ " + (m.Downloads.HasValue ? ShortCount(m.Downloads.Value) : "—"), "#A8A8B0",
            "近期下载数（下载来源统计）"));

        string upd = m.LatestPublished.Length > 0
            ? m.LatestPublished
            : (m.Added.Length >= 10 ? "收录 " + m.Added.Substring(0, 10) : (m.Added.Length > 0 ? "收录 " + m.Added : "—"));
        meta.Children.Add(MetaText("更新 " + upd, "#A8A8B0",
            m.LatestPublished.Length > 0 ? "最新版本的发布时间" : "暂时只有收录时间（这类收录没有版本发布时间，或尚未查到）"));

        var bandText = new TextBlock
        {
            Text = "    适配 " + m.BandText,
            FontSize = 11,
            Foreground = new SolidColorBrush(CompatColor(m.Band))
        };
        if (m.Band == PluginManager.Compat.Unknown && !m.MetaLoaded && m.Npm.Length > 0)
        {
            bandText.Text = "    适配 查询中…";
            bandText.Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93));
        }
        bandText.ToolTip = m.Requirement.Length > 0
            ? $"它声明要求 DSH {m.Requirement}（{m.RequirementSource}）\n" + BandExplain(m.Band)
            : (m.Npm.Length > 0 ? "尚未查到（或作者未声明）DSH 版本要求" : "这类收录没有版本声明");
        meta.Children.Add(bandText);
        sp.Children.Add(meta);

        // ── 第四行：说明（仅显示首句，全文放入 ToolTip，避免卡片被长描述占满）──
        if (m.DescZh.Length > 0)
        {
            sp.Children.Add(new TextBlock
            {
                Text = PlainDesc(m.DescZh),
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0),
                ToolTip = m.DescZh,
                Foreground = new SolidColorBrush(Color.FromRgb(0xA8, 0xA8, 0xB0))
            });
        }

        // ── 第四行：GitHub 截图（点击放大）；未收录截图时显示抓取按钮，点击后从 README 获取 ──
        if (m.Screenshots.Count > 0)
            sp.Children.Add(BuildThumbStrip(m));
        else if (m.Owner.Length > 0 || m.RepoUrl.Length > 0)
            sp.Children.Add(BuildScrapeChip(m));

        return card;
    }

    private static TextBlock MetaText(string text, string color, string tip) => new()
    {
        Text = text,
        FontSize = 11,
        Margin = new Thickness(0, 0, 12, 0),
        ToolTip = tip,
        Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color))
    };

    private static string BandExplain(PluginManager.Compat c) => c switch
    {
        PluginManager.Compat.Ok => "✅ 正好是作者指向的当前版本",
        PluginManager.Compat.Partial => "🟡 在它声明的范围里，但不是作者优先适配的那一个",
        PluginManager.Compat.Broken => "⛔ 当前 DSH 版本不在它声明的范围里",
        _ => "❔ 没有版本声明，只能实测"
    };

    /// <summary>
    /// 在系统浏览器里打开链接 —— **仅放行白名单 host 的 https 链接**
    /// （<see cref="PluginMarket.IsAllowedLinkUrl"/>）。目录 JSON 的 url/page 是外部输入，
    /// 可被投毒成 <c>\\attacker\share\evil.exe</c> 或任意可执行 scheme；
    /// UseShellExecute=true 会把这类值原样交给 shell，因此这里必须**先关门再放行**：
    /// 非白名单一律不打开，给中性提示并走诊断留痕（不弹异常、不口语化）。
    /// </summary>
    private static void OpenUrl(string url)
    {
        try
        {
            if (!PluginMarket.IsAllowedLinkUrl(url))
            {
                Logger.NoteDiagnosis($"已拒绝打开非白名单链接（来源：插件市场目录）：{url}");
                MessageBox.Show("该链接不在允许打开的网站范围内，未执行打开操作。", "链接已拦截",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) { Logger.LogError("OpenUrl(market)", ex); }
    }

    /// <summary>
    /// 判断该收录是否**真的**已安装在 profile 中。
    /// <para>
    /// 判据按「命名空间是否一致」收紧（本条修的是"跨作用域同名被误判为同一个包"）：
    /// </para>
    /// <list type="number">
    /// <item>两侧**完全相同**（含作用域）⇒ 同一个包，算已安装；</item>
    /// <item>任一侧带 npm 作用域（<c>@scope/name</c>）⇒ 作用域是包身份的一部分，只有完全相同才算；
    /// 裸名相同但作用域不同 = **同名而非同一包**，不算已安装，改由中性徽章如实说明；</item>
    /// <item>两侧都不带 npm 作用域时，才允许裸名相等：</item>
    /// </list>
    /// <para>
    /// ③ 这一条必须保留，不能简单改成"只要含斜杠就不认"：目录里 <c>npm</c> 为空的收录（实测 3727 条中 1834 条）
    /// 其 <see cref="PluginMarket.MarketPlugin.MatchKey"/> 取的是仓库路径 <c>owner/repo</c>，
    /// 而本地由 git 安装时 <see cref="PluginManager.Plugin.Name"/> 是裸包名
    /// （如 <c>aa2246740/dsh-watcher</c> ↔ <c>dsh-watcher</c>）—— 按"含斜杠即不同"会把这些真装了的条目
    /// 一律错判成"未安装"，属于反向回退。
    /// </para>
    /// </summary>
    /// <param name="sameNameLocal">
    /// 裸名相同但命名空间不同的那个本地包名（没有则为空串）。供悬停提示如实写出两个包名。
    /// </param>
    private bool IsInstalledInProfile(PluginMarket.MarketPlugin m, out string sameNameLocal)
    {
        sameNameLocal = "";
        if (_plugins.Count == 0) return false;

        string key = m.MatchKey.Trim().ToLowerInvariant();   // 例：@dickpy/dsh-imagegen / aa2246740/dsh-watcher
        if (key.Length == 0) return false;
        string bare = Bare(key);                             // 例：dsh-imagegen / dsh-watcher
        bool keyScoped = IsNpmScoped(key);

        foreach (var p in _plugins)
        {
            string other = p.Name.Trim().ToLowerInvariant();
            if (other.Length == 0) continue;

            // ① 完全同名（含作用域）⇒ 确实是同一个包
            if (other == key) return true;

            // ② 任一侧是 npm 作用域包 ⇒ 必须完全相同才算同一个包。
            //    本地 dsh-imagegen 与市场 @dickpy/dsh-imagegen 是两个不同的包，不得认成"已安装"。
            if (keyScoped || IsNpmScoped(other))
            {
                // 裸名相同而命名空间不同 ⇒ 记下这个同名包，供中性徽章如实写出（只记第一条）
                if (bare.Length > 0 && Bare(other) == bare && sameNameLocal.Length == 0) sameNameLocal = p.Name.Trim();
                continue;
            }

            // ③ 两侧都不带 npm 作用域时，裸名相等即同一个包：
            //    覆盖"市场收录只有仓库路径 owner/repo、本地装的是同名裸包"的 git 安装情形
            if (Bare(other) == bare) return true;
        }
        return false;
    }

    /// <summary>
    /// 「同名（非同一包）」中性徽章。
    /// <para>
    /// 只在裸名相同、命名空间不同时出现（如市场 <c>@dickpy/dsh-imagegen</c> ↔ 本地 <c>dsh-imagegen</c>）。
    /// 用中性灰而非绿色：绿色是"已安装"的专属语义，这里恰恰**不是**已安装。
    /// 悬停提示把两个包名都如实写出，用户无需猜测究竟是哪一个。
    /// </para>
    /// </summary>
    private Border BuildSameNameBadge(PluginMarket.MarketPlugin m, string localName)
        => new()
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(9, 5, 9, 5),
            Margin = new Thickness(0, 0, 6, 0),
            Background = new SolidColorBrush(Color.FromArgb(0x1F, 0x8E, 0x8E, 0x93)),
            ToolTip = $"名称相同，但不是同一个包：\n"
                    + $"  市场收录：{m.MatchKey}\n"
                    + $"  本地已装：{localName}\n"
                    + "两者命名空间不同，本地已装的这个包不能满足该收录的安装来源。",
            Child = new TextBlock
            {
                Text = "同名（非同一包）",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0xA8, 0xA8, 0xB0))
            }
        };

    /// <summary>
    /// 是不是 npm 作用域包写法（<c>@scope/name</c>）。
    /// <para>
    /// 与"含不含斜杠"区分开：<c>owner/repo</c> 这类**仓库路径**同样含斜杠，但它不是 npm 作用域，
    /// 不构成包身份的一部分（见 <see cref="IsInstalledInProfile"/> ③）。
    /// </para>
    /// </summary>
    private static bool IsNpmScoped(string s) => s.StartsWith("@") && s.Contains('/');

    private static string Bare(string s) => s.Contains('/') ? s.Substring(s.LastIndexOf('/') + 1) : s;

    // ══════════════ 一键安装 ══════════════
    // ══════════ 安装按钮的三态（空闲「安装」/ 安装中「安装中…」/ 悬停「停止」）══════════
    private bool _installing;
    private Button? _installBtn;

    /// <summary>
    /// 这次市场安装<b>是否开着底部进度表</b>（<c>BeginOpProgress</c> 之后为 true，收尾时消掉）。
    /// 只服务于一件事：让 <see cref="EndInstallState"/> 能分清"本次安装异常早退、进度条还悬着"
    /// 与"本次安装压根没开过表（例如来源被拒那处提前 return）"——
    /// 不靠 <c>_opProgressTimer != null</c> 猜，免得把别的操作（启动前体检 / 批量更新）的进度表误收掉。
    /// 不参与任何成败判定，与 <c>_installing</c> / <c>_installStopRequested</c> 无关。
    /// </summary>
    private bool _installProgressOpen;

    /// <summary>
    /// 「用户点了停止」这件事的**一次性标记**（本轮修的 bug ①）。
    /// <para>
    /// 为什么必须有它：<see cref="StopRunningCommand"/> 是把进程杀掉，正在等它的
    /// <c>RunCommandCancelableAsync</c> 随后必然返回 <c>ok=false</c> —— 从返回值上，
    /// "被用户停止"与"命令自己失败"长得一模一样。而失败分支里有一条"去掉策略参数再试一次"的兜底，
    /// 于是点停止的现场表现是**杀了又装**、按钮一直停在「安装中…」。
    /// </para>
    /// <para>
    /// 落在哪一侧：<c>StopRunningCommand</c> 与 <c>RunCommandCancelableAsync</c> 都在
    /// MainWindow.xaml.cs 里（本次不在可改范围），所以标记放在这条流程的调用侧，
    /// 由"点停止"那一处**先落标记、再杀进程**（顺序不能反，见 MarketInstall_Click），
    /// 由命令返回后的失败分支**消费一次**（<see cref="ConsumeInstallStopRequest"/>）。
    /// </para>
    /// </summary>
    private bool _installStopRequested;

    /// <summary>
    /// 消费一次"用户已请求停止"标记：读一次就清掉。
    /// <para>
    /// 必须消费（而不是一直留着）：否则下一次安装的命令万一真的失败，会被这个陈旧的标记
    /// 误判成"用户停的"、连重试都不做。清掉的时机也在这一处 —— 每次安装只认自己那一次停止。
    /// </para>
    /// </summary>
    private bool ConsumeInstallStopRequest()
    {
        bool v = _installStopRequested;
        _installStopRequested = false;
        return v;
    }

    /// <summary>被用户停止时的统一文案（事件栏、进度条、弹窗三处同一句话；不出现命令写法）。</summary>
    private const string InstallStoppedMessage = "安装已按请求停止";

    /// <summary>自检用：把"用户已请求停止"标记当成真落一次（走的就是点停止那条路）。</summary>
    internal void RequestInstallStopForTest() => _installStopRequested = true;

    /// <summary>自检用：消费一次停止标记（返回是否消费到了）。</summary>
    internal bool ConsumeInstallStopForTest() => ConsumeInstallStopRequest();

    /// <summary>自检用：此刻标记有没有被消费掉（消费过 = 不会误伤下一次安装）。</summary>
    internal bool InstallStopPendingForTest() => _installStopRequested;

    /// <summary>自检用：进入"正在安装某一条"的状态（走的就是点「安装」之后那三行赋值）。</summary>
    internal void BeginInstallStateForTest(PluginMarket.MarketPlugin m)
    {
        _marketBusy = true;
        _installing = true;
        _installingPlugin = m;
    }

    /// <summary>自检用：结束"正在安装"（走的就是安装结束时的唯一复位入口）。</summary>
    internal void EndInstallStateForTest() => EndInstallState();

    /// <summary>
    /// 自检用：市场安装入口的**两个标志**之一（<c>_marketBusy</c>）的当前值。
    /// 市场安装刻意不占 <see cref="_pluginWriteBusy"/>（它自己的忙碌事实是这一个），
    /// 所以"入口要查两个标志"这件事只能从这里观测。
    /// </summary>
    internal bool MarketBusyForTest() => _marketBusy;

    /// <summary>
    /// 自检用：把 <c>_marketBusy</c> 当成真落一次（铺"市场安装正忙"的现场，不需要真造一条收录、
    /// 更不需要弹确认框）。与 <see cref="BeginInstallStateForTest"/> 分开一个入口是有意的：
    /// 后者顺带落了 <c>_installing</c>，而 <c>_installing</c> 会把点击语义改成"停止"
    /// （见 <c>MarketInstall_Click</c> 开头那条早退）—— 这里只要"忙"，不要"可停止"。
    /// </summary>
    internal void SetMarketBusyForTest(bool busy) => _marketBusy = busy;

    /// <summary>
    /// 自检用：把插件表换成样本并重渲染，供"某条收录算不算已安装"这类判定用。
    /// 返回还原动作用以收尾（与 <c>SeedPluginStateForTest</c> 同一套习惯）。
    /// </summary>
    internal Action SeedMarketPluginsForTest(List<PluginManager.Plugin> plugins)
    {
        var undo = SeedPluginStateForTest(plugins,
            new Dictionary<string, PluginManager.PluginUpdate>(StringComparer.OrdinalIgnoreCase));
        return () => { undo(); RenderMarket(preserveScroll: true); };
    }

    /// <summary>
    /// 自检用：「这条收录算不算已安装」的判定结果 + 同名不同包时那个本地包名。
    /// 走的就是卡片用的同一个方法（<see cref="IsInstalledInProfile"/>），不另写一份判据。
    /// </summary>
    internal (bool Installed, string SameNameLocal) InstallVerdictForTest(PluginMarket.MarketPlugin m)
    {
        bool installed = IsInstalledInProfile(m, out string sameNameLocal);
        return (installed, sameNameLocal);
    }

    /// <summary>
    /// 自检用：「同名（非同一包）」中性徽章的文字与悬停提示。
    /// 返回空串表示**卡片上不会出现这枚徽章**（既没同名包，或该条目本身已算已安装——
    /// 后者按上屏分支走绿色「已安装」，与 <c>BuildMarketCard</c> 的判据顺序一致）。
    /// </summary>
    internal (string Text, string ToolTip) SameNameBadgeInfoForTest(PluginMarket.MarketPlugin m)
    {
        bool installed = IsInstalledInProfile(m, out string sameNameLocal);
        if (installed || sameNameLocal.Length == 0) return ("", "");
        var badge = BuildSameNameBadge(m, sameNameLocal);
        return ((badge.Child as TextBlock)?.Text ?? "", badge.ToolTip as string ?? "");
    }

    /// <summary>
    /// 自检用：**一次安装失败之后该不该自动重试**的判定，与 <c>MarketInstall_Click</c> 里那条分支同源。
    /// 判据只有一件事实：这次失败是不是用户主动停止造成的 —— 是就绝不重试。
    /// </summary>
    internal static bool ShouldRetryInstallAfterFailure(bool cmdOk, bool userStopped) => !cmdOk && !userStopped;

    /// <summary>自检用：被停止时的事件栏 / 弹窗文案（防止以后有人把它改回"安装失败"）。</summary>
    internal static string InstallStoppedTextForTest() => InstallStoppedMessage;

    /// <summary>自检用：这条收录此刻算不算"正在安装"（卡片重建时保留「安装中…/停止」形态的判据）。</summary>
    internal bool IsInstallingThisForTest(PluginMarket.MarketPlugin m) => IsInstallingThis(m);

    /// <summary>
    /// 正在安装的是哪一条收录（按对象引用比对；<see cref="RenderMarket"/> 每次过滤都复用同一批
    /// <see cref="PluginMarket.MarketPlugin"/> 实例，所以引用比对可靠）。
    /// 用途：列表在安装过程中被重绘时，新造的按钮要能认出自己就是"安装中"那颗，
    /// 而不是又冒出一颗绿色「安装」。
    /// </summary>
    private PluginMarket.MarketPlugin? _installingPlugin;

    /// <summary>
    /// 「安装」按钮的固定尺寸：悬停只换颜色和文字，尺寸必须一动不动。
    /// <para>
    /// **为什么必须定宽（不是想压缩布局）**：<see cref="MiniButton"/> 不设 Width，
    /// 按钮宽度是由 Content 文字**撑开**的；而这三态的文字本来就长短不一 ——
    /// 11.5 号「安装」「停止」实测各 23，加内边距后 45；「安装中…」却是 42.93，加内边距后 64.9。
    /// 于是「安装」→「安装中…」会把卡片右上角顶宽，鼠标一悬停换回「停止」又缩回去，
    /// 同一颗按钮在三态之间反复"呼吸"，旁边的插件名也跟着重新排。
    /// </para>
    /// <para>
    /// 取值 78 的依据：约等于最宽那态的自然宽度 64.9（「安装中…」）留出约 13 的余量，
    /// 三态文字都能整句放下、不会被裁；78 也远窄于卡片本身，不会把卡片右上角撑出多余空白。
    /// Height / MinHeight / FontSize / Padding 一律沿用
    /// <see cref="MiniButton"/> 的取值（26 / 26 / 11.5 / 11,5），这里只是再显式钉一遍，
    /// 免得以后有人调 MiniButton 的默认内边距时，唯独此按钮高度会悄然变化。
    /// </para>
    /// <para>
    /// **只定宽还不够**：定宽只保证"布局尺寸"不变，而悬停看着变大是**动效**造成的 ——
    /// 共用模板 <see cref="RoundBtn"/> 里带 1.05 倍悬停放大 + 回弹（只影响绘制、不参与布局），
    /// 全壳按钮统一，别的按钮要继续保留这份观感。所以这里只对**这一颗**调
    /// <see cref="RoundBtn.DisableHoverScale"/>：它改用无缩放的另一份模板，
    /// 且 <see cref="ButtonFx"/> 挂动效时也跳过这颗的缩放 ⇒ 悬停只剩"变色 + 改字"，尺寸一动不动。
    /// </para>
    /// </summary>
    private static Button BuildInstallButton()
    {
        var btn = MiniButton("安装", "#34C759");
        btn.Width = 78;
        btn.MinWidth = 78;          // 与 Width 同值：防止外层（Grid 自动列 / 主题）把它压窄
        btn.Height = 26;
        btn.MinHeight = 26;
        btn.FontSize = 11.5;
        btn.Padding = new Thickness(11, 5, 11, 5);
        // 定宽后文字必须居中：短文案（「停止」）左对齐会显得整颗按钮歪向一边
        btn.HorizontalContentAlignment = HorizontalAlignment.Center;
        btn.VerticalContentAlignment = VerticalAlignment.Center;
        // 点完不留焦点虚框：这颗按钮是纯鼠标动作片，页面上没有与之配套的键盘流程
        // （卡片、分类标签、刷新/筛选、加载更多全是 Border + MouseLeftButtonDown，本来就不可聚焦），
        // 留着 Tab 焦点只会让点击后多出一圈点状虚线。与批量工具条 BatchBarHost 同一口径
        // （Focusable=false + 显式置空焦点视觉样式，双保险）。
        btn.Focusable = false;
        btn.FocusVisualStyle = null;
        // 1px 圆角/描边吸附到整数像素，避免落在半个像素上发虚
        // （ButtonFx 挂动效时也会设这两项，这里显式钉一遍，不依赖它有没有跑过）
        btn.UseLayoutRounding = true;
        btn.SnapsToDevicePixels = true;
        // 悬停只变色改字（安装中… ⇄ 停止）、尺寸完全不变：去掉这颗按钮的悬停/按下缩放。
        // 按元素级生效，不影响其它按钮的悬停放大回弹。
        RoundBtn.DisableHoverScale(btn);
        return btn;
    }

    /// <summary>
    /// 安装按钮的**唯一**绘制入口：文案与底色一次定完，别处一律不许再单独设这两项
    /// （否则某条分支又会把空闲态的绿画回来）。
    /// <para>
    /// 画哪一态只看两件现场事实：<see cref="_installing"/> 与按钮**此刻**的
    /// <see cref="UIElement.IsMouseOver"/>，不依赖"这次事件是 Enter 还是 Leave"——
    /// <c>MouseLeave</c> 在窗体内移动、按钮被重排/尺寸变化时都会补发，按事件名判定
    /// 会把仍停在按钮上的鼠标误判成"已离开"。
    /// </para>
    /// <para>
    /// 三态：空闲=绿「安装」；安装中=蓝「安装中…」；安装中且鼠标停在按钮上=红「停止」。
    /// 安装真正结束之后，即便鼠标还停在按钮上也只画绿「安装」——那时已经没有"停止"可点。
    /// </para>
    /// </summary>
    /// <param name="hovering">
    /// 可选：调用方给定的悬停事实。缺省（null）以 <c>b.IsMouseOver</c> 为准。
    /// </param>
    private void PaintInstallBtn(Button b, bool? hovering = null)
    {
        try
        {
            bool installing = _installing;
            bool hot = installing && (hovering ?? b.IsMouseOver);

            string text = !installing ? "安装" : (hot ? "停止" : "安装中…");
            Color color = !installing
                ? Color.FromRgb(0x34, 0xC7, 0x59)       // 空闲=绿
                : (hot
                    ? Color.FromRgb(0xFF, 0x3B, 0x30)   // 悬停=红（与右上角"终止引擎"同色）
                    : Color.FromRgb(0x4A, 0x9E, 0xFF)); // 进行中=蓝
            ApplyInstallLook(b, text, color);
        }
        catch { }
    }

    /// <summary>
    /// 落一次"文案 + 底色"。底色必须同时写到**真正渲染的那一层**上，原因是模板层的既有事实：
    /// <list type="number">
    /// <item><see cref="RoundBtn"/> 的模板把里面那层 <see cref="Border"/>.Background 绑到
    /// <c>Control.Background</c>（RoundBtn.cs 的 <c>BuildTemplate</c>）；</item>
    /// <item><see cref="ThemeManager"/> 每次刷主题都会给 <c>Border.Background</c> 赋一个常量画刷
    /// （ThemeManager.cs 的 <c>Walk</c>：<c>case Border b: b.Background = RemapBrush(b.Background, map)</c>）；</item>
    /// <item>绿 / 蓝 / 红都不在颜色映射表里，<c>RemapBrush</c> 原样返回**同一支画刷**，
    /// 于是那次赋值把模板里的绑定换成了常量值 ⇒ 绑定失效，渲染面被钉死在当时的颜色上。</item>
    /// </list>
    /// 之后只改 <c>Control.Background</c> 就再也画不动底色，现场表现正是
    /// "文字变成「停止」、底色还是绿的"。模板本体（RoundBtn.cs / ThemeManager.cs）
    /// 本次不在授权范围内，因此在这里做**局部**补救：绘制后把同一支画刷重新钉到模板的 Border 上。
    /// 模板还没实例化时跳过——那种情况下绑定仍然有效，会自己取到正确颜色。
    /// </summary>
    private static void ApplyInstallLook(Button b, string text, Color color)
    {
        if ((b.Content as string) != text) b.Content = text;
        if (b.Background is not SolidColorBrush cur || cur.Color != color)
            b.Background = new SolidColorBrush(color);

        var surface = RenderedSurface(b);
        if (surface != null && !ReferenceEquals(surface.Background, b.Background))
            surface.Background = b.Background;
    }

    /// <summary>按钮模板里真正画底色的那层 Border（RoundBtn 模板的根；没套模板/还没实例化时为 null）。</summary>
    private static Border? RenderedSurface(Button b)
    {
        try { return VisualTreeHelper.GetChild(b, 0) as Border; }
        catch { return null; }
    }

    /// <summary>
    /// 这条收录是不是"正在安装"的那一条。先按对象引用认（<see cref="PluginMarket.Filter"/> 每次
    /// 都复用同一批实例，引用比对最省事也最准）；万一目录被整份重载过，再按安装标识认一次。
    /// </summary>
    private bool IsInstallingThis(PluginMarket.MarketPlugin m)
    {
        var cur = _installingPlugin;
        if (!_installing || cur == null) return false;
        if (ReferenceEquals(cur, m)) return true;
        return cur.MatchKey.Length > 0 && cur.MatchKey == m.MatchKey;
    }

    /// <summary>
    /// 把一颗（刚点下的、或列表重绘后新造的）安装按钮接管成"正在安装"的那颗：
    /// 挂上悬停处理、记进 <see cref="_installBtn"/>、按当前三态画一次。
    /// <para>
    /// 重绘也必须重新接管：安装期间补元数据 / 取图 / 改筛选都会重建整张卡片，
    /// 旧按钮连同它的 MouseEnter/MouseLeave 一起被丢弃，而 <see cref="_installBtn"/> 还指着它——
    /// 新按钮于是既不显示"安装中"，也不再响应悬停（现场"绿色且悬停无反应"的另一个来源）。
    /// </para>
    /// </summary>
    private void AdoptInstallButton(Button btn)
    {
        btn.MouseEnter -= MarketInstallBtn_Hover;
        btn.MouseLeave -= MarketInstallBtn_Hover;
        btn.MouseEnter += MarketInstallBtn_Hover;
        btn.MouseLeave += MarketInstallBtn_Hover;
        _installBtn = btn;
        PaintInstallBtn(btn);
    }

    /// <summary>
    /// 安装**真正结束**时唯一的复位入口：先把"正在安装"这件事落下去，再画绿「安装」。
    /// 顺序不能反——先画后复位会把刚画好的绿又按"安装中"覆盖成蓝。
    /// 复位后即使鼠标还停在按钮上，<see cref="PaintInstallBtn"/> 也只画绿「安装」。
    /// </summary>
    private void EndInstallState()
    {
        _installing = false;
        _installingPlugin = null;
        // 一次性标记跟着一起清：安装已经结束，就不该再有"用户请求过停止"悬在那里
        // （留着会让下一次安装的真失败被误判成"用户停的"、连重试都不做）。
        _installStopRequested = false;
        // ★ 收尾复位入口同时也是**进度条**的兜底：正常路径已由 EndOpProgress 报过
        //   成功/失败/停止文案（它把表收了、_installProgressOpen 已落回 false）；
        //   出错/异常早退时表还开着，这里补一句如实的「安装中断」，
        //   不让底部永远停在「正在安装插件（NN%）」。没开过表就什么都不做。
        if (_installProgressOpen) { _installProgressOpen = false; EndOpProgress("安装中断"); }
        if (_installBtn != null) PaintInstallBtn(_installBtn);
    }

    /// <summary>
    /// 悬停/离开只当"该重画了"的通知，画哪一态交给 <see cref="PaintInstallBtn"/>。
    /// Enter 一定画"悬停"、Leave 一定画"离开"，其余情况核对 <see cref="UIElement.IsMouseOver"/>
    /// 这个现场事实再决定——<c>MouseLeave</c> 在按钮被重排/改尺寸时也会补发，
    /// 只看事件名容易和真实悬停状态错开一拍。
    /// </summary>
    private void MarketInstallBtn_Hover(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not Button b) return;
        bool enter = e.RoutedEvent == System.Windows.Input.Mouse.MouseEnterEvent;
        bool leave = e.RoutedEvent == System.Windows.Input.Mouse.MouseLeaveEvent;
        // Enter 一律算悬停；Leave 一律算离开（它在窗体内移动、按钮被重排/改尺寸时都会补发，
        // 万一鼠标其实还停在按钮上，WPF 会紧接着再补一次 MouseEnter，下一拍就修回来）；
        // 其余情况以 IsMouseOver 这个现场事实为准。
        bool hot = enter || (!leave && b.IsMouseOver);
        PaintInstallBtn(b, hovering: hot);
    }

    private async void MarketInstall_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not PluginMarket.MarketPlugin m) return;

        // ⚠ 这条判定必须在**安装确认框之前**（本轮修的 bug ②）。
        // 为什么：安装进行中，这颗按钮悬停就显示红色「停止」，用户点它就是"我要停下来"。
        // 确认框若排在前面，点停止会先弹「安装社区插件「X」？…是否继续？」——
        // 用户看到的现场正是"我点停止，它却问我是否安装"，只能先点「确定」才走到停止分支；
        // 点「取消」则连停止都没发生，安装还在后台跑，语义彻底反了。
        // 顺序即语义：安装中 ⇒ 直接停止并返回，任何框都不弹。
        // 再点一次 = 停止当前安装（按钮悬停时会显示"停止"，见下面的 MouseEnter/Leave）
        if (_installing)
        {
            // 停之前先**标记"这是用户主动停的"**，再动手杀进程。
            // 顺序不能反：StopRunningCommand 一返回，等命令的那条 await 马上就会带着 ok=false 继续往下跑，
            // 那时若标记还没落下，失败分支就会把它当成"命令失败"⇒ 去掉策略参数**再装一次**。
            // 现场表现正是"点了停止，它反而重新开始装"（本轮修的 bug ①）。
            _installStopRequested = true;
            bool stopped = StopRunningCommand();
            AddEvent(stopped ? "已按你的要求停止安装" : "安装已经结束了", EventKind.Warn);
            return;
        }

        string src = m.InstallSource;
        if (src.Length == 0) return;

        // ★ 插件写闸：这里跑的 npx 会改 node_modules / pnpm-lock.yaml（整棵依赖图），
        //   与三条更新路径、批量更新 / 批量卸载、以及回滚重装是同一类命令 ⇒ 先过统一入口。
        //   位置与那几处一致：放在**确认框之前** —— 免得用户点了确认、命令都要跑了才被告知"正忙"。
        //   闸门只有一份（PassPluginWriteGate，只查不开），且不另编提示：它自己会说那句话。
        if (!PassPluginWriteGate()) return;
        // 同时保留本处原有的重入判据：市场安装**自己**正在跑（_marketBusy）时，上面那个闸看不见它
        // （市场安装不占 _pluginWriteBusy，占的是 _marketBusy / _installing）⇒ 两个标志入口互查，
        // 与同事那套"两标志 + 入口互查"同一个范式；被拒的提示复用同一句，不新增第二句文案。
        if (_marketBusy)
        {
            AddEvent(PluginWriteBusyMessage, EventKind.Warn);
            return;
        }

        string current = VersionMemory.Pin.Length > 0 ? VersionMemory.Pin : _currentDshVersion;
        var band = m.MetaLoaded ? m.Band : PluginManager.EvaluateBand(m.Requirement, current);
        string bandText = m.MetaLoaded || m.Requirement.Length > 0
            ? (band switch
            {
                PluginManager.Compat.Ok => $"✅ 完全兼容（正好是它声明指向的 {current}）",
                PluginManager.Compat.Partial => $"🟡 能用，但不是它优先适配的版本（它面向 {string.Join(" / ", VersionInfo.RequirementVersions(m.Requirement))}，你当前 {current}）",
                PluginManager.Compat.Broken => $"⛔ 不兼容：它要求 {m.Requirement}，你当前 {current}",
                _ => "❔ 作者未声明 dsh 版本要求，只能实测"
            })
            : "❔ 尚未查到它的版本声明（安装时会用当前固定版本，装完可在「本地插件」看兼容档）";

        var r = GuardDialog.Show(
            $"安装社区插件「{m.Name}」？" +
            (m.Owner.Length > 0 ? $"（作者 {m.Owner}）" : "") + "\n\n" +
            $"安装源：{src}\n" +
            $"版本信息：★ {ShortCount(m.Stars)} · ↓ {(m.Downloads.HasValue ? ShortCount(m.Downloads.Value) : "—")}" +
            (m.LatestPublished.Length > 0 ? $" · 最近更新 {m.LatestPublished}" : "") + "\n" +
            $"兼容性体检：{bandText}\n\n" +
            "会把它登记进 DSH 的插件清单（装错了可在「本地插件」里卸载，改动前会自动备份配置）。\n\n" +
            "装完需要重启 DSH 才生效。是否继续？",
            band == PluginManager.Compat.Broken ? "安装插件 · 注意不兼容" : "安装插件",
            MessageBoxButton.OKCancel,
            band == PluginManager.Compat.Broken ? MessageBoxImage.Warning : MessageBoxImage.Question);
        if (r != MessageBoxResult.OK) return;

        _marketBusy = true;
        _installing = true;
        _installingPlugin = m;
        // ★ 落入"改依赖图中"（插件写闸）：确认框已通过、下面第一条 await 立刻要跑 npx。
        //   为什么落在这里而不是确认框正后面：本方法在 try **之前**有两处早退（安装源为空 / 空参），
        //   它们各自 return、**不落闸**；若把 BeginPluginWriteState 提到早退之前，
        //   这两条早退就绕过了 finally ⇒ 闸门永远关着。到这里早退已全部走过，
        //   放在 try 内 ⇒ 与 _marketBusy / EndInstallState 同一条 finally 收，异常路径也一定复位。
        BeginPluginWriteState();
        // 接管这颗按钮：挂悬停（悬停变红显示"停止"，不改变点击语义：点它就是停）、记账、
        // 并按现场三态画一次。此刻鼠标就停在这颗按钮上（刚点完），所以画出来是红「停止」而不是蓝。
        AdoptInstallButton(b);
        try
        {
            MarketSummaryText.Text = $"正在给「{m.Name}」打快照…";
            string note = SnapshotPolicy.NeedSnapshot(GuardAction.InstallPlugin)
                ? await Task.Run(() => SnapshotBeforePluginChange($"DSHGuard：安装插件 {m.Name} 前", SnapshotPolicy.KindFor(GuardAction.InstallPlugin)))
                : "";

            MarketSummaryText.Text = $"正在安装「{m.Name}」（{src}）…首次安装通常需要十几秒到一分钟";
            // 半截安装自愈：清单名可解析且处于「目录在、package.json 缺」的残留态 ⇒ 先清残留目录再装
            //（否则 pnpm 报「目录已存在」拒绝安装、界面判"未安装" ⇒ 反复点反复失败）。越界/失败只留证不中止。
            string brokenPkgM = PluginManager.PackageNameFromSource(src);
            if (brokenPkgM.Length > 0 && PluginManager.EvaluateInstallState(brokenPkgM) == PluginManager.InstallStateKind.Broken)
            {
                var cleanM = PluginManager.CleanBrokenInstall(brokenPkgM);
                Logger.NoteDiagnosis($"市场安装 {m.Name}：半截安装清理 → {(cleanM.Rejected ? "拒绝（越界）" : cleanM.Cleared ? "已清理" : "清理失败")}");
                if (cleanM.Cleared) AddEvent("检测到上次安装残留，已清理后重新安装：" + m.Name, EventKind.Warn);
            }
            string addArgs = PluginManager.BuildAddSourceArgs(src);
            // ★ 信任边界配套：来源被白名单拒绝 ⇒ 空串，此时**不得**执行任何命令
            //（空参丢给 npx 只会得到一条无意义的失败命令）。给中性提示后原地收场
            //（此时尚未 BeginOpProgress，直接返回即可，不留悬挂的进度操作）。
            if (addArgs.Length == 0)
            {
                _marketBusy = false;
                EndInstallState();
                GuardDialog.Show(
                    $"无法为「{m.Name}」确定合法的安装来源（来源「{src}」未通过安全校验），本次未执行任何命令。\n\n" +
                    "请确认该收录的安装源是否正确；若反复出现，请把日志发给作者。",
                    "安装插件", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            BeginOpProgress("正在安装插件");
            _installProgressOpen = true;      // 表开着了：之后无论走哪条路径，都要在 EndInstallState 里收掉
                // relaxSupplyChainPolicy：给这次命令注入放行 pnpm 包龄/锁文件策略的环境变量。
                // pnpm 12 内置 1440 分钟包龄默认门槛，命令行那条覆盖参数（PolicyOverride）不被识别，
                // 只有环境变量能按次放开 —— 详见 PluginManager.SupplyChainRelaxEnv 的实测记录。
                var (ok, output) = await RunCommandCancelableAsync("npx", addArgs, timeoutMs: 900000,
                    relaxSupplyChainPolicy: true);

                // 「被用户停止」与「命令失败」必须分开（本轮修的 bug ①）：
                // StopRunningCommand 杀掉进程后退出码必然非零，ok 也是 false —— 只看 ok 就会落进
                // 下面那条重试分支，表现成"杀了又装"、按钮一直停在「安装中…」。
                // 标记由"点停止"那一处落下（MarketInstallStopRequestedForTest 是它的自检入口），这里消费一次。
                bool userStopped = ConsumeInstallStopRequest();

                // C：万一 pnpm 不认 --trust-lockfile，去掉它原样再试一次（与卸载同一套兜底）
                //     被用户停止时**不重试** —— 他已经明确不要这次安装了。
                if (!ok && !userStopped)
                {
                    Logger.Log($"市场安装 {m.Name} 首次失败，去掉策略参数重试。输出尾部：{Shorten(output ?? "", 300)}");
                    var (ok2, output2) = await RunCommandAsync("npx", PluginManager.WithoutPolicyOverride(addArgs),
                        timeoutMs: 900000, relaxSupplyChainPolicy: true);
                    if (ok2) { ok = true; output = output2; }
                    else output = output2 + "\n（首次输出）\n" + output;
                }

                // B：以**事实**判成败 —— 回读插件清单，装上了就算成功（哪怕 pnpm 退出码不漂亮）
                //     被用户停止的那一次不做这个判定：命令是被我们杀掉的，它没跑完，
                //     拿"清单里有没有"下结论会把"本来就已经装着"说成"这次装上了"。
                string pkgName = PluginManager.PackageNameFromSource(src);
                bool inManifest = !userStopped && pkgName.Length > 0
                                  && await Task.Run(() => PluginManager.HasDependency(pkgName));
                bool okFinal = ok || inManifest;
                if (inManifest && !ok)
                    Logger.Log($"市场安装 {m.Name}：命令退出码非零，但清单里已出现 {pkgName} ⇒ 按成功处理");
                ok = okFinal;
                EndOpProgress(userStopped ? "安装已停止" : (ok ? "插件已安装" : "插件安装失败"));
                _installProgressOpen = false;     // 表已由 EndOpProgress 收掉，兜底别再补一句「安装中断」
            AddEvent(userStopped ? InstallStoppedMessage
                                 : (ok ? $"本地插件 {m.Name}"
                                       : $"安装插件失败：{m.Name} · {PluginManager.SupplyChainRelaxNote}"),
                userStopped ? EventKind.Warn : (ok ? EventKind.Good : EventKind.Bad));
            // ★ 与技术词的边界（本处补痕）：PluginManager.SupplyChainRelaxNote 原文案把「pnpm 的包龄限制（环境变量方式）」
            //   这种实现细节摆进了用户可见文案，现改为中性中文；细节不丢的落点是日志：
            //   失败时 MainWindow.xaml.cs 的 NoteSupplyChainRelaxOnFailure 已经经 NoteDiagnosis 落盘（含六个键=值）。
            //   Logger.Log 是空实现、不落盘（Logger.cs:162），所以这里另外补一条 NoteDiagnosis 锚住"本次确实放宽过"。
            //   ⚠ 只在**失败**分支补：成功路径不留日志是用户明确要求（见 Logger 类头），普通安装成功不加。
            if (!userStopped && !ok)
                Logger.NoteDiagnosis($"市场安装 {m.Name}：本次插件命令已放宽更新来源的安全检查（环境变量方式）");
            // 弹窗不再摆原始命令输出 ⇒ 全文必须能在日志里拿到：
            //   常规失败（退出码非零）由命令层 NoteCommandResult 落盘；
            //   这里补一条同款诊断，兜住"命令超时 / 进程起不来 / 退出码 0 却没装上"这类命令层不落盘的情形。
            //   用户主动停止的也记一份：包目录可能停在半截，这是复盘"停在哪一步"的唯一现场。
            if (!ok)
                LogPluginCmdFailure($"市场安装未成功 {m.Name}", addArgs, output);
            Logger.Log($"市场安装 {m.Name} [{src}]: ok={ok} 用户停止={userStopped}\n{Shorten(output, 2000)}");

            _marketBusy = false;
            EndInstallState();      // 安装到此才算真正结束：唯一复位入口，复位成绿「安装」
            GuardDialog.Show(
                (userStopped ? InstallStoppedMessage
                             : (ok ? $"✅ 已安装「{m.Name}」。" : $"❌ 安装「{m.Name}」失败。")) + "\n\n" +
                note + "\n\n" +
                (userStopped
                    ? "本次安装未完成，可再次点击「安装」重试。\n\n"
                      + LogPromise("详细输出已记入日志，可在「日志」页查看。")
                    : (ok
                        ? "重启 DSH 后生效，然后可以在「本地插件」里检查兼容档与更新。"
                        : (pkgName.Length > 0 && !inManifest
                            ? $"插件清单中未出现 {pkgName}，本次安装未完成；可直接重试。"
                            : "本次安装未完成（安装源不是普通包名，无法按插件清单核对）；可直接重试。")
                          + "\n\n" + PluginManager.SupplyChainRelaxHint
                          + "\n\n" + LogPromise("详细输出已记入日志，可在「日志」页查看。"))),
                userStopped ? "已停止安装" : (ok ? "安装完成" : "安装失败"),
                MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);

            await RefreshPluginsAsync(true);
            RenderMarket(preserveScroll: true);

            if (ok) await OfferRestartAsync($"安装插件 {m.Name}");
        }
        catch (Exception ex)
        {
            _marketBusy = false;
            EndInstallState();      // 出错同样属于"本次安装已结束"，走同一个复位入口
            Logger.LogError("MarketInstall_Click", ex);
            GuardDialog.Show("安装没能完成，可再点一次「安装」重试。\n\n" + LogPromise("详细原因已记入日志，可在「日志」页查看。"), "安装插件", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _marketBusy = false; EndInstallState(); EndPluginWriteState(); }   // 兜底：任何提前收场都从这里复位，且只复位一次（幂等）
    }
}
