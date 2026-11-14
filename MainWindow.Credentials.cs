#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DSHGuard.Unified;

namespace DSHGuard;

/// <summary>
/// 2.4.0「凭据中心」：独立一级功能区（左侧导航「凭据」/ Ctrl+7）。
///
/// 零缓存铁律：
///   本页所有凭据与提供商均实时直读官方 ~/.dsh/.credentials.yaml 与各端 cordis.patch.yml；
///   拖入文件/读取其他 Agent/粘贴得到的明文候选仅瞬时驻留当前交互，导入完成、收起抽屉或切离本页即清空，
///   守护壳不留任何内存或磁盘备份副本。
/// </summary>
public partial class MainWindow : Window
{
    private string _credImportChannel = "file"; // file | agents | clipboard
    private string _credClipboardText = "";
    private string _credDroppedFileName = "";
    private List<ImportedProviderCandidate> _credManualCandidates = new();
    private DshLlmProvider? _credEditingProvider;

    /// <summary>当前在列表内联展开设置的模型提供商 ID（点击卡片头切换）。</summary>
    private string? _credExpandedProviderId;

    /// <summary>当前在列表内联展开更新的环境变量密钥名（点击卡片头切换）。</summary>
    private string? _credExpandedEnvKey;

    /// <summary>凭据中心当前页签：providers（模型提供商）或 keys（密钥与账号）。</summary>
    private string _credActiveTab = "providers";

    /// <summary>凭据中心独立的搜索关键字（与综合页各分区的搜索互不干扰）。</summary>
    private string _credSearch = "";

    /// <summary>常驻工具条里的搜索框（Ctrl+F 聚焦它）。</summary>
    private TextBox? _credSearchBox;

    /// <summary>工具条里随状态改文案的两颗按钮（原地改 Content，不重建工具条）。</summary>
    private Button? _credImportToggleBtn;
    private Button? _credAddToggleBtn;

    /// <summary>当前正文元素（搜索时只换它）。</summary>
    private UIElement? _credBody;

    /// <summary>正文重建进行中（挡住 TextChanged 自触发）。</summary>
    private bool _credSearchRefreshing;

    /// <summary>「获取模型列表」可搜索下拉浮层，同一时间最多开一个，切页/重绘/最小化时关闭。</summary>
    private Popup? _credModelPickerPopup;

    /// <summary>单模型本次会话瞬时连通性测速缓存（key = pid|proto|modelId），仅存延迟与状态文案，绝不存密钥。</summary>
    private readonly Dictionary<string, (bool Ok, int LatencyMs, string Detail)> _credModelPingCache
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>按 proto|baseUrl 缓存最近一次探测到的公开模型 ID 列表（仅存模型名称，零密钥）。</summary>
    private readonly Dictionary<string, List<string>> _credModelProbeCache
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>收起凭据中心的模型选择下拉浮层。</summary>
    internal void CloseCredentialPopups()
    {
        try
        {
            if (_credModelPickerPopup != null)
            {
                _credModelPickerPopup.IsOpen = false;
                ThemeManager.UnregisterOrphanPopup(_credModelPickerPopup);
                _credModelPickerPopup = null;
            }
        }
        catch { }
    }

    internal void OpenCredentialImportForTest(bool open, string? channel = null)
    {
        _credImportOpen = open;
        if (!open) ClearTransientCredentialImportState();
        else if (!string.IsNullOrWhiteSpace(channel)) _credImportChannel = channel!;
        if (open) { _credFormOpen = false; _credExpandedProviderId = null; _credExpandedEnvKey = null; }
        ShowView(GuardView.Credentials);   // ShowView 内部惰性刷新（RefreshCredentialsView）
    }

    internal void ShowCredentialsTabForTest(string tab)
    {
        _credActiveTab = tab == "keys" ? "keys" : "providers";
        ShowView(GuardView.Credentials);
        RenderCredentialsView();
    }

    internal void ExpandProviderForTest(string? providerId)
    {
        _credExpandedProviderId = providerId;
        if (_currentView != GuardView.Credentials) ShowView(GuardView.Credentials);
        else RenderCredentialsView();
    }

    internal void ExpandCredentialKeyForTest(string? envKey)
    {
        _credExpandedEnvKey = envKey;
        if (_currentView != GuardView.Credentials) ShowView(GuardView.Credentials);
        else RenderCredentialsView();
    }

    internal int TransientImportCandidateCountForTest => _credManualCandidates.Count;
    internal bool IsCredentialImportOpenForTest => _credImportOpen;
    internal void LoadCredentialFileForPreviewForTest(string filePath) => LoadCredentialFileForPreview(filePath);

    /// <summary>清空导入流程在内存中的全部瞬态明文状态，并收起内联展开项，防止明文滞留。</summary>
    internal void ClearTransientCredentialImportState()
    {
        CloseCredentialPopups();
        _credClipboardText = "";
        _credDroppedFileName = "";
        _credManualCandidates.Clear();
        _credExpandedProviderId = null;
        _credExpandedEnvKey = null;
    }

    internal void InvalidateCredentialCacheForTest() => InvalidateDshScans();

    /// <summary>自检/出图用：凭据中心常驻工具条是否已建好（搜索框存活）。</summary>
    internal bool CredentialsToolbarAliveForTest => _credSearchBox != null && CredentialsToolbarHost?.Child != null;

    // ══════════════ 入口：刷新 / 整屏渲染 / 只换正文 ══════════════

    /// <summary>刷新前同步全局非凭据状态（凭据与提供商本身已实行零缓存直读）。</summary>
    private void InvalidateCredentialScans()
    {
    }

    /// <summary>进页 / 点「刷新」：重扫磁盘后整屏渲染（由 ShowView(GuardView.Credentials) 惰性调用）。</summary>
    private void RefreshCredentialsView()
    {
        try
        {
            InvalidateCredentialScans();
            RenderCredentialsView();
        }
        catch (Exception ex)
        {
            Logger.LogError("RefreshCredentialsView", ex);
            RenderCredentialsError(ex);
        }
    }

    /// <summary>整屏渲染：摘要 → 工具条（首建或原地同步）→ 正文。</summary>
    private void RenderCredentialsView()
    {
        if (CredentialsRoot == null) return;
        CloseCredentialPopups();
        UiTrace.Entry trace = UiTrace.Enter("渲染凭据中心");
        try
        {
            var creds = Credentials();
            var providers = Providers();
            SyncCredTabsVisual();
            UpdateCredentialsSummary(creds, providers);

            EnsureCredentialsToolbar();
            SyncCredentialsToolbar();

            CredentialsRoot.Children.Clear();
            _credBody = BuildDshCredentialSection();
            CredentialsRoot.Children.Add(_credBody);

            ButtonFx.Wire(CredentialsRoot);
            if (CredentialsToolbarHost != null) ButtonFx.Wire(CredentialsToolbarHost);

            // 同综合页：新建控件按夜间配色出生，日间模式下必须补刷
            ApplyThemeSoon();
        }
        catch (Exception ex)
        {
            Logger.LogError("RenderCredentialsView", ex);
            RenderCredentialsError(ex);
        }
        finally
        {
            UiTrace.Exit(trace);
        }
    }

    /// <summary>只重建正文，工具条与搜索框始终活着（搜索框每敲一个字走这里）。</summary>
    private void RebuildCredentialsBodyOnly()
    {
        try
        {
            CloseCredentialPopups();
            if (CredentialsRoot == null || _credBody == null)
            {
                RenderCredentialsView();
                return;
            }
            int idx = CredentialsRoot.Children.IndexOf(_credBody);
            if (idx < 0)
            {
                RenderCredentialsView();
                return;
            }

            _credSearchRefreshing = true;
            try
            {
                var fresh = BuildDshCredentialSection();
                CredentialsRoot.Children.RemoveAt(idx);
                CredentialsRoot.Children.Insert(idx, fresh);
                _credBody = fresh;
                SyncCredentialsToolbar();
                ButtonFx.Wire(fresh);
                ApplyThemeSoon();
            }
            finally
            {
                _credSearchRefreshing = false;
            }
        }
        catch (Exception ex)
        {
            _credSearchRefreshing = false;
            Logger.LogError("RebuildCredentialsBodyOnly", ex);
            RenderCredentialsError(ex);
        }
    }

    /// <summary>Ctrl+F：把焦点移到凭据中心的搜索框。</summary>
    internal void FocusCredentialsSearchBox()
    {
        try
        {
            if (_credSearchBox == null) EnsureCredentialsToolbar();
            if (_credSearchBox == null) return;
            _credSearchBox.Focus();
            _credSearchBox.SelectAll();
        }
        catch (Exception ex) { Logger.LogError("FocusCredentialsSearchBox", ex); }
    }

    /// <summary>底部回执（成功 / 失败都走这里，不弹窗）。</summary>
    private void ShowCredToast(string message)
    {
        try
        {
            if (CredentialsToast == null) return;
            CredentialsToast.Text = message ?? "";
            CredentialsToast.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        }
        catch { }
    }

    private void CredTabProviders_Click(object sender, MouseButtonEventArgs e)
    {
        if (_credActiveTab == "providers") return;
        _credActiveTab = "providers";
        RenderCredentialsView();
    }

    private void CredTabKeys_Click(object sender, MouseButtonEventArgs e)
    {
        if (_credActiveTab == "keys") return;
        _credActiveTab = "keys";
        RenderCredentialsView();
    }

    private void SyncCredTabsVisual()
    {
        try
        {
            bool prov = _credActiveTab != "keys";
            if (CredTabProviders != null) PaintSegment(CredTabProviders, prov);
            if (CredTabKeys != null) PaintSegment(CredTabKeys, !prov);
        }
        catch { }
    }

    private void UpdateCredentialsSummary(DshCredentialScanResult creds, List<DshLlmProvider> providers)
    {
        try
        {
            if (CredentialsViewSummary == null) return;
            CredentialsViewSummary.Text = providers.Count == 0 && creds.Entries.Count == 0
                ? "暂无凭据"
                : $"{providers.Count} 提供商 · {creds.Entries.Count} 密钥";
        }
        catch { }
    }

    /// <summary>出错界面：只说哪一步没成、能做什么；技术细节一行小字。</summary>
    private void RenderCredentialsError(Exception ex)
    {
        try
        {
            if (CredentialsRoot == null) return;
            CredentialsRoot.Children.Clear();
            _credBody = null;
            CredentialsRoot.Children.Add(DshWidgets.Text("凭据中心没能加载出来", 15, DshError, FontWeights.SemiBold));
            var why = DshWidgets.Hint("可能是 DSH 目录暂时读不到，或 .credentials.yaml / cordis.patch.yml 格式有问题。\n你的数据没有被改动。");
            why.Margin = new Thickness(0, 6, 0, 0);
            CredentialsRoot.Children.Add(why);
            var detail = DshWidgets.Hint("技术细节（排查用）：" + ex.GetType().Name + " — " + ex.Message, 10);
            detail.Margin = new Thickness(0, 8, 0, 0);
            CredentialsRoot.Children.Add(detail);
            var retry = DshWidgets.SmallButton("再试一次", "primary");
            retry.Margin = new Thickness(0, 12, 0, 0);
            retry.Click += (_, _) => RefreshCredentialsView();
            CredentialsRoot.Children.Add(retry);
            ButtonFx.Wire(CredentialsRoot);
            ApplyThemeSoon();
        }
        catch (Exception inner) { Logger.LogError("RenderCredentialsError", inner); }
    }

    // ══════════════ 常驻工具条 ══════════════

    /// <summary>首次进入时建单行工具条并挂进宿主；之后永不重建（搜索框焦点因此不会丢）。</summary>
    private void EnsureCredentialsToolbar()
    {
        if (CredentialsInfoHost != null && CredentialsInfoHost.Child == null)
        {
            CredentialsInfoHost.Child = DshWidgets.InfoDot(
                "零缓存直读与非订阅凭据导入说明",
                DshText.Explain("ccswitch") + "\n\n" + DshText.Explain("credentials"));
        }

        if (CredentialsToolbarHost == null) return;
        if (CredentialsToolbarHost.Child != null && _credSearchBox != null) return;

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 两段式：先写初值再挂回调（避免 TextChanged 重入）；回调只重建正文。
        var (searchHost, searchBox) = DshWidgets.SearchBox("搜索提供商或密钥…", double.NaN, _credSearch);
        searchHost.HorizontalAlignment = HorizontalAlignment.Stretch;
        searchHost.VerticalAlignment = VerticalAlignment.Center;
        searchHost.MinWidth = 140;
        searchHost.MinHeight = 28;
        searchHost.Height = 28;
        searchBox.Height = 28;
        searchBox.Padding = new Thickness(30, 0, 10, 0);
        searchBox.VerticalContentAlignment = VerticalAlignment.Center;
        searchBox.TextChanged += (_, _) =>
        {
            if (_credSearchRefreshing) return;
            _credSearch = searchBox.Text ?? "";
            RebuildCredentialsBodyOnly();
        };
        searchBox.PreviewKeyDown += (_, ke) =>
        {
            if (ke.Key == Key.Escape && searchBox.Text.Length > 0)
            {
                searchBox.Clear();
                ke.Handled = true;
            }
        };
        Grid.SetColumn(searchHost, 0);
        row.Children.Add(searchHost);
        _credSearchBox = searchBox;

        var compactPad = new Thickness(10, 0, 10, 0);
        var btnGap = new Thickness(6, 0, 0, 0);

        var importBtn = DshWidgets.ToolbarButton("⚡ 导入", "primary");
        importBtn.Padding = compactPad;
        importBtn.Margin = btnGap;
        importBtn.ToolTip = "拖入装着 URL 与 API Key 的文件，或一键读取其他 Agent 的非订阅明文凭据（零缓存直接写官方表单）。";
        importBtn.Click += (_, _) =>
        {
            _credImportOpen = !_credImportOpen;
            if (_credImportOpen) { _credFormOpen = false; _credExpandedProviderId = null; _credExpandedEnvKey = null; }
            else ClearTransientCredentialImportState();
            RenderCredentialsView();
            if (_credImportOpen) ScrollCredentialsToTop();
        };
        Grid.SetColumn(importBtn, 1);
        row.Children.Add(importBtn);
        _credImportToggleBtn = importBtn;

        var addBtn = DshWidgets.ToolbarButton("＋ 添加");
        addBtn.Padding = compactPad;
        addBtn.Margin = btnGap;
        addBtn.ToolTip = "使用 8 套内置平台预设一键填充，或手工添加提供商 / API Key。";
        addBtn.Click += (_, _) =>
        {
            _credFormOpen = !_credFormOpen;
            if (_credFormOpen) { _credImportOpen = false; ClearTransientCredentialImportState(); }
            else _credEditingProvider = null;
            RenderCredentialsView();
            if (_credFormOpen) ScrollCredentialsToTop();
        };
        Grid.SetColumn(addBtn, 2);
        row.Children.Add(addBtn);
        _credAddToggleBtn = addBtn;

        var openDir = DshWidgets.ToolbarButton("目录");
        openDir.Padding = compactPad;
        openDir.Margin = btnGap;
        openDir.ToolTip = "在资源管理器中打开官方 ~/.dsh/.credentials.yaml 所在目录。";
        openDir.Click += (_, _) =>
        {
            try
            {
                string dir = Path.GetDirectoryName(GuardPaths.CredentialsFile) ?? GuardPaths.DshHome;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                RevealInExplorer(File.Exists(GuardPaths.CredentialsFile) ? GuardPaths.CredentialsFile : dir);
            }
            catch (Exception ex) { ShowCredToast("打不开目录：" + ex.Message); }
        };
        Grid.SetColumn(openDir, 3);
        row.Children.Add(openDir);

        var refresh = DshWidgets.ToolbarButton("刷新");
        refresh.Padding = compactPad;
        refresh.Margin = btnGap;
        refresh.ToolTip = "实时重读官方 .credentials.yaml、各端 cordis.patch.yml 与本机其他 Agent 非订阅配置。";
        refresh.Click += (_, _) =>
        {
            RefreshCredentialsView();
            ShowCredToast("已重新扫描凭据与提供商。");
        };
        Grid.SetColumn(refresh, 4);
        row.Children.Add(refresh);

        CredentialsToolbarHost.Child = row;
    }

    /// <summary>按抽屉开合状态原地同步两颗切换按钮的文案（不重建工具条）。</summary>
    private void SyncCredentialsToolbar()
    {
        try
        {
            if (_credImportToggleBtn != null)
                _credImportToggleBtn.Content = _credImportOpen ? "收起导入" : "⚡ 导入";
            if (_credAddToggleBtn != null)
                _credAddToggleBtn.Content = _credFormOpen
                    ? (_credEditingProvider != null ? "收起编辑" : "收起添加")
                    : "＋ 添加";
        }
        catch { }
    }

    // ══════════════ 正文 ══════════════

    private UIElement BuildDshCredentialSection()
    {
        UiTrace.Entry trace = UiTrace.Enter("渲染凭据中心正文");
        try
        {
            var panel = new StackPanel();
            var creds = Credentials();
            var providers = Providers();

            // 1. 折叠抽屉：文件拖入 / 其他 Agent 非订阅读取 / 文本粘贴
            if (_credImportOpen)
                panel.Children.Add(BuildCredentialImportPanel(LocalAgentsReport()));

            // 2. 折叠抽屉：8 平台预设向导（添加 / 编辑提供商与密钥）
            if (_credFormOpen)
                panel.Children.Add(BuildCredentialWizardPanel());

            string kw = (_credSearch ?? "").Trim();

            var matchedProviders = providers
                .Where(p => kw.Length == 0
                    || p.DisplayName.Contains(kw, StringComparison.OrdinalIgnoreCase)
                    || p.ProviderId.Contains(kw, StringComparison.OrdinalIgnoreCase)
                    || p.ApiKeyEnv.Contains(kw, StringComparison.OrdinalIgnoreCase)
                    || p.BaseUrl.Contains(kw, StringComparison.OrdinalIgnoreCase)
                    || p.Models.Any(m => m.Contains(kw, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(p => DshFavorites.IsStarred(DshFavorites.ProviderKey(p.ProviderId)))
                .ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var providerRefsMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in providers)
            {
                if (string.IsNullOrWhiteSpace(p.ApiKeyEnv)) continue;
                if (!providerRefsMap.TryGetValue(p.ApiKeyEnv, out var list))
                {
                    list = new List<string>();
                    providerRefsMap[p.ApiKeyEnv] = list;
                }
                list.Add(p.DisplayName);
            }

            var matchedEntries = creds.Entries
                .Where(e => kw.Length == 0
                    || e.Key.Contains(kw, StringComparison.OrdinalIgnoreCase)
                    || e.SourceLabel.Contains(kw, StringComparison.OrdinalIgnoreCase)
                    || (providerRefsMap.TryGetValue(e.Key, out var linked)
                        && linked.Any(n => n.Contains(kw, StringComparison.OrdinalIgnoreCase))))
                .OrderByDescending(e => DshFavorites.IsStarred(DshFavorites.CredentialKey(e.Key)))
                .ThenBy(e => e.IsReadOnlyAccount ? 1 : 0)
                .ThenBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (_credActiveTab != "keys")
            {
                if (matchedProviders.Count == 0)
                {
                    if (kw.Length > 0)
                    {
                        string hint = matchedEntries.Count > 0
                            ? $"「密钥与账号」页签中另有 {matchedEntries.Count} 条匹配项，可点击上方页签查看。"
                            : "请更换关键词，或清空搜索框后查看全部。";
                        panel.Children.Add(DshWidgets.EmptyState($"没有匹配「{kw}」的模型提供商。", hint));
                    }
                    else
                    {
                        var (title, action) = DshText.Empty("credential");
                        var quickImport = DshWidgets.SmallButton("⚡ 一键导入", "primary");
                        quickImport.Click += (_, _) =>
                        {
                            _credImportOpen = true;
                            _credImportChannel = "file";
                            _credFormOpen = false;
                            RenderCredentialsView();
                        };
                        panel.Children.Add(DshWidgets.EmptyState(title, action, quickImport));
                    }
                    return panel;
                }

                foreach (var p in matchedProviders)
                    panel.Children.Add(BuildProviderCard(p));
                return panel;
            }

            if (matchedEntries.Count == 0)
            {
                if (kw.Length > 0)
                {
                    string hint = matchedProviders.Count > 0
                        ? $"「模型提供商」页签中另有 {matchedProviders.Count} 条匹配项，可点击上方页签查看。"
                        : "请更换关键词，或清空搜索框后查看全部。";
                    panel.Children.Add(DshWidgets.EmptyState($"没有匹配「{kw}」的密钥或账号。", hint));
                }
                else
                {
                    var quickImport = DshWidgets.SmallButton("⚡ 导入", "primary");
                    quickImport.Click += (_, _) =>
                    {
                        _credImportOpen = true;
                        _credImportChannel = "file";
                        _credFormOpen = false;
                        RenderCredentialsView();
                    };
                    panel.Children.Add(DshWidgets.EmptyState(
                        "尚未收纳环境变量 API 密钥",
                        "点上方「⚡ 导入」或「＋ 添加」录入密钥，凭据将直接写入官方 .credentials.yaml。",
                        quickImport));
                }
                return panel;
            }

            var apiEntries = matchedEntries.Where(e => !e.IsReadOnlyAccount).ToList();
            var acctEntries = matchedEntries.Where(e => e.IsReadOnlyAccount).ToList();

            if (apiEntries.Count > 0 && acctEntries.Count > 0)
            {
                var apiLbl = DshWidgets.Hint($"API 密钥（{apiEntries.Count}）", 11);
                apiLbl.Margin = new Thickness(2, 2, 0, 6);
                panel.Children.Add(apiLbl);
            }

            foreach (var e in apiEntries)
            {
                providerRefsMap.TryGetValue(e.Key, out var usedBy);
                panel.Children.Add(BuildCredentialRowCard(e, usedBy));
            }

            if (acctEntries.Count > 0)
            {
                var roLbl = DshWidgets.Hint($"受保护登录态 · 只读（{acctEntries.Count}）", 11);
                roLbl.Margin = new Thickness(2, apiEntries.Count > 0 ? 8 : 2, 0, 6);
                panel.Children.Add(roLbl);

                foreach (var e in acctEntries)
                {
                    providerRefsMap.TryGetValue(e.Key, out var usedBy);
                    panel.Children.Add(BuildCredentialRowCard(e, usedBy));
                }
            }

            return panel;
        }
        finally
        {
            UiTrace.Exit(trace);
        }
    }

    // ══════════════ 文件拖拽处理（CredentialsView AllowDrop）══════════════

    private void CredentialsView_DragOver(object sender, DragEventArgs e)
    {
        try
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
                return;
            }
        }
        catch { }
        e.Effects = DragDropEffects.None;
    }

    private void CredentialsView_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
            string? path = files.FirstOrDefault(File.Exists);
            if (string.IsNullOrWhiteSpace(path)) return;

            e.Handled = true;
            LoadCredentialFileForPreview(path!);
        }
        catch (Exception ex)
        {
            Logger.LogError("CredentialsView_Drop", ex);
            ShowCredToast("读取拖入文件失败：" + ex.Message);
        }
    }

    private void LoadCredentialFileForPreview(string filePath)
    {
        var rep = DshCcSwitchImporter.ImportFromFile(filePath);
        _credImportOpen = true;
        _credImportChannel = "file";
        _credFormOpen = false;
        _credExpandedProviderId = null;
        _credExpandedEnvKey = null;
        _credDroppedFileName = Path.GetFileName(filePath);
        _credManualCandidates = rep.Candidates.ToList();
        ShowCredToast(rep.Summary);
        if (_currentView != GuardView.Credentials)
            ShowView(GuardView.Credentials);
        else
            RenderCredentialsView();
        ScrollCredentialsToTop();
    }

    // ══════════════ 三合一凭据一键读取与导入中心（零缓存）══════════════

    private UIElement BuildCredentialImportPanel(CcSwitchScanReport agentReport)
    {
        var box = new StackPanel();

        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        head.Children.Add(DshWidgets.Text("一键导入凭据", 13, DshTextPrimary, FontWeights.SemiBold));
        var dot = DshWidgets.InfoDot("零缓存与非订阅说明", DshText.Explain("ccswitch"));
        dot.Margin = new Thickness(6, 0, 0, 0);
        head.Children.Add(dot);
        box.Children.Add(head);

        var policyHint = DshWidgets.Hint(
            "仅支持非订阅套餐的明文凭据（自动跳过 OAuth / Plus / Pro 登录态）；本壳不存任何缓存，确认后只写入 DSH 官方表单。",
            10.5);
        policyHint.Margin = new Thickness(0, 0, 0, 8);
        box.Children.Add(policyHint);

        // 3 通道切换胶囊：文件拖入（默认） | 从其他 Agent 读取 | 粘贴文本/命令
        var channels = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        channels.Children.Add(ChannelButton("📂 拖入 / 选择文件", "file"));
        channels.Children.Add(ChannelButton("🤖 从其他 Agent 读取", "agents"));
        channels.Children.Add(ChannelButton("📋 粘贴文本 / 命令", "clipboard"));
        box.Children.Add(channels);

        var r = DshWidgets.Rule();
        r.Margin = new Thickness(0, 0, 0, 8);
        box.Children.Add(r);

        if (_credImportChannel == "agents")
        {
            var rescanBtn = DshWidgets.SmallButton("🔄 重新读取本机其他 Agent");
            rescanBtn.Margin = new Thickness(0, 0, 0, 8);
            rescanBtn.Click += (_, _) =>
            {
                RenderCredentialsView();
                ShowCredToast("已实时重读本机其他 Agent 非订阅配置。");
            };
            box.Children.Add(rescanBtn);

            RenderImportCandidateList(
                box,
                agentReport.Candidates,
                agentReport.Summary,
                emptyHint: "支持实时读取 Claude Code (~/.claude)、Codex (~/.codex)、Gemini CLI (~/.gemini)、OpenCode、Continue、Aider 中的明文 API Key（OAuth/Plus/Pro 订阅登录态按规则跳过）。");
        }
        else if (_credImportChannel == "clipboard")
        {
            var clipBtns = new WrapPanel { Orientation = Orientation.Horizontal };
            var readClip = DshWidgets.SmallButton("📋 从剪贴板读取并解析", "primary");
            var parseBox = DshWidgets.SmallButton("解析下方内容");
            clipBtns.Children.Add(readClip);
            clipBtns.Children.Add(parseBox);
            box.Children.Add(clipBtns);

            var (inHost, inBox) = DshWidgets.Input("粘贴装着 URL 与 API Key 的片段、JSON、.env、TOML 或 curl 命令…", double.NaN);
            inBox.AcceptsReturn = true;
            inBox.TextWrapping = TextWrapping.Wrap;
            inBox.MinHeight = 62;
            inBox.MaxHeight = 110;
            inBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            inBox.Text = _credClipboardText;
            inBox.TextChanged += (_, _) => _credClipboardText = inBox.Text;
            inHost.Margin = new Thickness(0, 0, 0, 8);
            box.Children.Add(inHost);

            readClip.Click += (_, _) =>
            {
                try
                {
                    if (!Clipboard.ContainsText())
                    {
                        ShowCredToast("剪贴板里当前没有文本内容。");
                        return;
                    }
                    string raw = Clipboard.GetText();
                    _credClipboardText = raw;
                    inBox.Text = raw;
                    _credManualCandidates = DshCcSwitchImporter.ParseSmartText(raw).ToList();
                    ShowCredToast(_credManualCandidates.Count > 0
                        ? $"识别到 {_credManualCandidates.Count} 条非订阅凭据，核对后点击一键写入官方表单"
                        : "未能识别到有效 URL / 明文 API Key");
                    RenderCredentialsView();
                }
                catch (Exception ex)
                {
                    ShowCredToast("读取剪贴板失败：" + ex.Message);
                }
            };

            parseBox.Click += (_, _) =>
            {
                _credClipboardText = inBox.Text;
                _credManualCandidates = DshCcSwitchImporter.ParseSmartText(_credClipboardText).ToList();
                ShowCredToast(_credManualCandidates.Count > 0
                    ? $"解析出 {_credManualCandidates.Count} 条非订阅凭据"
                    : "未识别到有效配置，请检查是否包含 URL 与明文 Key");
                RenderCredentialsView();
            };

            if (_credManualCandidates.Count > 0)
            {
                RenderImportCandidateList(box, _credManualCandidates,
                    $"已从粘贴内容瞬时解析出 {_credManualCandidates.Count} 条待写入项（未落盘缓存）：", "");
            }
        }
        else // file (default)
        {
            var dropContent = new StackPanel();
            dropContent.Children.Add(DshWidgets.Text(
                string.IsNullOrEmpty(_credDroppedFileName)
                    ? "把装着 URL 和 API Key 的文件直接拖到这里"
                    : $"已读取文件：{_credDroppedFileName}",
                12, DshTextPrimary, FontWeights.SemiBold));
            var dropDesc = DshWidgets.Hint(
                "支持任意文本 / .env / .json / .toml / .yaml / .md / curl 文件（含无标签两行 URL + sk-...）；" +
                "仅当前交互内存解析预览，导入完成或切页立刻清空，不留任何缓存文件。",
                10.5);
            dropDesc.Margin = new Thickness(0, 3, 0, 8);
            dropContent.Children.Add(dropDesc);

            var pickBtn = DshWidgets.SmallButton("📂 选择文件自动读取", "primary");
            pickBtn.Margin = new Thickness(0);
            pickBtn.Click += (_, _) =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "选择包含 URL 与 API Key 的文件（零缓存读取）",
                    Filter = "所有支持文件|*.txt;*.env;*.json;*.toml;*.yaml;*.yml;*.conf;*.ini;*.md;*.sh;*.ps1;*.db;*.sqlite|文本 / ENV / 配置|*.txt;*.env;*.conf;*.ini;*.md|JSON / TOML / YAML|*.json;*.toml;*.yaml;*.yml|SQLite 数据库 (*.db)|*.db;*.sqlite|所有文件|*.*",
                };
                if (dlg.ShowDialog() != true) return;
                LoadCredentialFileForPreview(dlg.FileName);
            };
            dropContent.Children.Add(pickBtn);

            var dropZone = DshWidgets.Card(dropContent, new Thickness(0, 0, 0, 8));
            box.Children.Add(dropZone);

            if (_credManualCandidates.Count > 0)
            {
                RenderImportCandidateList(box, _credManualCandidates,
                    $"自动提取到 {_credManualCandidates.Count} 条待写入项（确认后直写官方表单）：", "");
            }
        }

        return DshWidgets.Panel(box);
    }

    private Button ChannelButton(string label, string channelId)
    {
        bool on = string.Equals(_credImportChannel, channelId, StringComparison.Ordinal);
        var btn = DshWidgets.SmallButton(label, on ? "primary" : "normal");
        btn.Click += (_, _) =>
        {
            _credImportChannel = channelId;
            RenderCredentialsView();
        };
        return btn;
    }

    private void RenderImportCandidateList(
        StackPanel host,
        IReadOnlyList<ImportedProviderCandidate> candidates,
        string headerText,
        string emptyHint)
    {
        var head = DshWidgets.Hint(headerText, 11);
        head.Margin = new Thickness(0, 0, 0, 6);
        host.Children.Add(head);

        if (candidates.Count == 0)
        {
            if (!string.IsNullOrEmpty(emptyHint))
                host.Children.Add(DshWidgets.Hint(emptyHint, 10.5));
            return;
        }

        var checkPairs = new List<(CheckBox Box, ImportedProviderCandidate Candidate)>();
        foreach (var c in candidates)
        {
            var cb = new CheckBox
            {
                IsChecked = c.ApiKey.Length > 0 || c.BaseUrl.Length > 0,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = Cursors.Hand,
            };

            var info = new StackPanel();
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
            titleRow.Children.Add(DshWidgets.Text(c.DisplayName, 12, DshTextPrimary, FontWeights.SemiBold));
            titleRow.Children.Add(MarginLeft(DshWidgets.Badge(c.Source, DshAccent), 6));
            if (c.IsCurrent)
                titleRow.Children.Add(MarginLeft(DshWidgets.Badge("当前激活", DshOk), 5));
            if (c.ApiKey.Length > 0)
                titleRow.Children.Add(MarginLeft(DshWidgets.Badge(c.MaskedKey, DshOk), 5));
            info.Children.Add(titleRow);

            string detail = $"env: {c.EnvKey}";
            if (!string.IsNullOrWhiteSpace(c.BaseUrl)) detail += $" · {c.BaseUrl}";
            if (c.Models.Count > 0) detail += $" · {c.Models.Count} 模型";
            var sub = DshWidgets.Hint(detail, 10.5);
            sub.Margin = new Thickness(0, 2, 0, 0);
            info.Children.Add(sub);

            var rowGrid = new Grid();
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(cb, 0);
            Grid.SetColumn(info, 1);
            rowGrid.Children.Add(cb);
            rowGrid.Children.Add(info);

            host.Children.Add(DshWidgets.Card(rowGrid, new Thickness(0, 0, 0, 4)));
            checkPairs.Add((cb, c));
        }

        var targetRow = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 6) };
        var writeCredCb = MakeThemedCheckBox("收纳到全局凭据库", true);
        var writeWebCb = MakeThemedCheckBox("同步 Web 版模型列表", true);
        var writeDeskCb = MakeThemedCheckBox("同步桌面版模型列表", true);
        targetRow.Children.Add(writeCredCb);
        targetRow.Children.Add(writeWebCb);
        targetRow.Children.Add(writeDeskCb);
        host.Children.Add(targetRow);

        var actionRow = new StackPanel { Orientation = Orientation.Horizontal };
        var runBtn = DshWidgets.SmallButton("⚡ 一键导入选中项", "primary");
        var cancelBtn = DshWidgets.SmallButton("收起");
        runBtn.Click += (_, _) =>
        {
            var picked = checkPairs.Where(x => x.Box.IsChecked == true).Select(x => x.Candidate).ToList();
            ExecuteBatchImport(picked,
                writeCredCb.IsChecked == true,
                writeWebCb.IsChecked == true,
                writeDeskCb.IsChecked == true);
        };
        cancelBtn.Click += (_, _) =>
        {
            _credImportOpen = false;
            ClearTransientCredentialImportState();
            RenderCredentialsView();
        };
        actionRow.Children.Add(runBtn);
        actionRow.Children.Add(cancelBtn);
        host.Children.Add(actionRow);
    }

    private void ExecuteBatchImport(
        List<ImportedProviderCandidate> picked,
        bool saveCred,
        bool syncWeb,
        bool syncDesktop)
    {
        if (picked.Count == 0)
        {
            ShowCredToast("请至少勾选一条待导入配置。");
            return;
        }
        if (!TryEnterDshBusy())
        {
            ShowCredToast("正在写入，请稍候…");
            return;
        }

        try
        {
            int credOk = 0, provOk = 0, skipped = 0;
            var profiles = DshCredentialManager.ResolveProfiles();
            var webProfile = profiles.FirstOrDefault(p => p.Kind == DshProfileKind.Web);
            var deskProfile = profiles.FirstOrDefault(p => p.Kind == DshProfileKind.Desktop);

            foreach (var c in picked)
            {
                if (saveCred && c.ApiKey.Length > 0)
                {
                    var r = DshCredentialManager.UpsertCredentialRef(c.EnvKey, c.ApiKey);
                    if (r.Ok) credOk++; else skipped++;
                }

                bool hasEndpoint = !string.IsNullOrWhiteSpace(c.BaseUrl);
                if (hasEndpoint && (syncWeb || syncDesktop))
                {
                    var inferredModels = (c.Models ?? Array.Empty<string>())
                        .Where(m => !string.IsNullOrWhiteSpace(m))
                        .Distinct(StringComparer.Ordinal)
                        .Select(m => DshModelConfig.InferDefaults(m, c.Api))
                        .ToList();

                    var draft = new DshProviderDraft
                    {
                        ProviderId = c.ProviderId,
                        DisplayName = c.DisplayName,
                        ApiKeyEnv = c.EnvKey,
                        Api = c.Api,
                        BaseUrl = c.BaseUrl,
                        Models = inferredModels.Where(m => m.Enabled).Select(m => m.Id).ToList(),
                        ModelConfigs = inferredModels,
                    };
                    if (syncWeb && webProfile != null)
                    {
                        var r = DshCredentialManager.ApplyProviderToProfile(webProfile, draft);
                        if (r.Ok)
                        {
                            provOk++;
                            if (!string.IsNullOrEmpty(r.ProviderId)
                                && !string.Equals(r.ProviderId, c.ProviderId, StringComparison.OrdinalIgnoreCase))
                            {
                                DshFavorites.MigrateKey(DshFavorites.ProviderKey(c.ProviderId), DshFavorites.ProviderKey(r.ProviderId));
                            }
                        }
                    }
                    if (syncDesktop && deskProfile != null)
                    {
                        var r = DshCredentialManager.ApplyProviderToProfile(deskProfile, draft);
                        if (r.Ok)
                        {
                            provOk++;
                            if (!string.IsNullOrEmpty(r.ProviderId)
                                && !string.Equals(r.ProviderId, c.ProviderId, StringComparison.OrdinalIgnoreCase))
                            {
                                DshFavorites.MigrateKey(DshFavorites.ProviderKey(c.ProviderId), DshFavorites.ProviderKey(r.ProviderId));
                            }
                        }
                    }
                }
            }

            _credImportOpen = false;
            ClearTransientCredentialImportState();
            InvalidateDshScans();
            ShowCredToast($"已直接写入官方表单：凭据 {credOk} 条、同步提供商配置 {provOk} 端（零缓存）" + (skipped > 0 ? $"，跳过 {skipped} 条受保护或无效项" : ""));
            RenderCredentialsView();
        }
        catch (Exception ex)
        {
            Logger.LogError("ExecuteBatchImport", ex);
            ShowCredToast("批量导入出错：" + ex.Message);
        }
        finally
        {
            ExitDshBusy(refresh: false);
        }
    }

    // ══════════════ 新增 / 编辑向导（8 个内置预设 + /models 探测）══════════════

    private UIElement BuildCredentialWizardPanel()
    {
        var box = new StackPanel();
        var editing = _credEditingProvider;
        string heading = editing != null ? $"编辑提供商「{editing.DisplayName}」" : "添加模型提供商与 API Key（支持一键预设）";
        box.Children.Add(DshWidgets.Text(heading, 13, DshTextPrimary, FontWeights.SemiBold));

        var presetLabel = DshWidgets.Hint("一键套用常用平台预设（点一下自动填充 ID / 环境变量 / Base URL / 模型）：", 10.5);
        presetLabel.Margin = new Thickness(0, 4, 0, 4);
        box.Children.Add(presetLabel);

        var presetsRow = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        box.Children.Add(presetsRow);

        var (nameHost, nameBox) = DshWidgets.Input("显示名称（如 DeepSeek）", double.NaN);
        var (idHost, idBox) = DshWidgets.Input("Provider ID（如 deepseek）", double.NaN);
        var (envHost, envBox) = DshWidgets.Input("环境变量名（如 DEEPSEEK_API_KEY）", double.NaN);
        var protoCombo = CreateProtocolCombo(editing?.Api ?? "openai-completions");
        var (urlHost, urlBox) = DshWidgets.Input("Base URL（仅填存储凭据可留空，如 https://api.deepseek.com/v1）", double.NaN);
        var (keyHost, keyBox) = DshWidgets.Input(editing != null ? "API Key（留空表示保留已有密钥；填写将覆盖更新）" : "API Key（sk-... 安全写入 .credentials.yaml）", double.NaN);

        var workingSpecial = editing != null ? editing.Special.Clone() : new DshProviderSpecialConfig();
        var workingModels = editing != null
            ? (editing.ModelConfigs.Count > 0
                ? editing.ModelConfigs.Select(m => m.Clone()).ToList()
                : editing.Models.Select(m => DshModelConfig.InferDefaults(m, editing.Api)).ToList())
            : new List<DshModelConfig>();

        bool suppressProtoUrlRewrite = false;
        if (editing != null)
        {
            suppressProtoUrlRewrite = true;
            nameBox.Text = editing.DisplayName;
            idBox.Text = editing.ProviderId;
            envBox.Text = editing.ApiKeyEnv;
            urlBox.Text = editing.BaseUrl;
            SelectProtocolInCombo(protoCombo, editing.Api);
            suppressProtoUrlRewrite = false;
        }

        var specialHost = new ContentControl { Margin = new Thickness(0, 2, 0, 8) };
        string initialProto = protoCombo.SelectedItem?.ToString() ?? "openai-completions";
        specialHost.Content = BuildProtocolSettingsPanel(initialProto, workingSpecial);

        Action? refreshWizardModelsUi = null;

        foreach (var preset in DshCredentialManager.BuiltInPresets)
        {
            var pill = DshWidgets.SmallButton(preset.Label);
            pill.Margin = new Thickness(0, 0, 6, 6);
            pill.ToolTip = preset.Note;
            var captured = preset;
            pill.Click += (_, _) =>
            {
                suppressProtoUrlRewrite = true;
                try
                {
                    nameBox.Text = captured.DisplayName;
                    idBox.Text = captured.ProviderId;
                    envBox.Text = captured.EnvKey;
                    urlBox.Text = captured.BaseUrl;
                    SelectProtocolInCombo(protoCombo, captured.Api);
                    workingModels.Clear();
                    foreach (var mid in captured.Models)
                    {
                        if (!string.IsNullOrWhiteSpace(mid))
                            workingModels.Add(DshModelConfig.InferDefaults(mid, captured.Api));
                    }
                    specialHost.Content = BuildProtocolSettingsPanel(captured.Api, workingSpecial);
                    refreshWizardModelsUi?.Invoke();
                }
                finally
                {
                    suppressProtoUrlRewrite = false;
                }
            };
            presetsRow.Children.Add(pill);
        }

        // 双列：显示名 / Provider ID
        var row1 = BuildTwoColRow(nameHost, idHost);
        box.Children.Add(row1);

        // 双列：环境变量名 / 协议
        var row2 = BuildTwoColRow(envHost, protoCombo);
        box.Children.Add(row2);

        urlHost.Margin = new Thickness(0, 0, 0, 6);
        box.Children.Add(urlHost);

        keyHost.Margin = new Thickness(0, 0, 0, 6);
        box.Children.Add(keyHost);

        box.Children.Add(specialHost);

        protoCombo.SelectionChanged += (_, _) =>
        {
            string selectedProto = protoCombo.SelectedItem?.ToString() ?? "openai-completions";
            if (!suppressProtoUrlRewrite && !string.IsNullOrWhiteSpace(urlBox.Text))
                urlBox.Text = DshCredentialManager.SwitchBaseUrlForProtocol(urlBox.Text, selectedProto);
            specialHost.Content = BuildProtocolSettingsPanel(selectedProto, workingSpecial);
            refreshWizardModelsUi?.Invoke();
        };

        var modelSection = BuildModelConfiguratorSection(
            () => string.IsNullOrWhiteSpace(nameBox.Text) ? (idBox.Text.Trim().Length > 0 ? idBox.Text.Trim() : "新提供商") : nameBox.Text.Trim(),
            () => DshCredentialManager.NormalizeProviderId(idBox.Text.Length > 0 ? idBox.Text : nameBox.Text),
            workingModels,
            () => protoCombo.SelectedItem?.ToString() ?? "openai-completions",
            () => urlBox.Text.Trim(),
            () =>
            {
                var typed = keyBox.Text.Trim();
                if (typed.Length > 0) return typed;
                var env = envBox.Text.Trim();
                return env.Length > 0 ? DshCredentialManager.TryResolveEnvSecretForTransientProbe(env) : null;
            },
            onModelStateCommitted: null,
            out refreshWizardModelsUi);
        box.Children.Add(modelSection);

        var targetsRow = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 8) };
        var webCb = MakeThemedCheckBox("写入 Web 版", editing?.EnabledIn("web") ?? true);
        var deskCb = MakeThemedCheckBox("写入 桌面版", editing?.EnabledIn("desktop") ?? true);
        targetsRow.Children.Add(webCb);
        targetsRow.Children.Add(deskCb);
        box.Children.Add(targetsRow);

        var btns = new StackPanel { Orientation = Orientation.Horizontal };
        var saveBtn = DshWidgets.SmallButton("保存并同步", "primary");
        var cancelBtn = DshWidgets.SmallButton("取消");

        saveBtn.Click += (_, _) =>
        {
            SaveWizardProviderOrKey(
                idBox.Text,
                nameBox.Text,
                envBox.Text,
                protoCombo.SelectedItem?.ToString() ?? "openai-completions",
                urlBox.Text,
                keyBox.Text,
                workingModels,
                workingSpecial,
                webCb.IsChecked == true,
                deskCb.IsChecked == true);
        };
        cancelBtn.Click += (_, _) =>
        {
            CloseCredentialPopups();
            _credFormOpen = false;
            _credEditingProvider = null;
            RenderCredentialsView();
        };
        btns.Children.Add(saveBtn);
        btns.Children.Add(cancelBtn);
        box.Children.Add(btns);

        return DshWidgets.Panel(box);
    }

    private void SaveWizardProviderOrKey(
        string rawId,
        string rawDisplay,
        string rawEnvKey,
        string api,
        string rawUrl,
        string rawKey,
        List<DshModelConfig> modelConfigs,
        DshProviderSpecialConfig special,
        bool writeWeb,
        bool writeDesktop)
    {
        if (!TryEnterDshBusy())
        {
            ShowCredToast("正在处理其它写入，请稍候…");
            return;
        }
        try
        {
            string envKey = DshCredentialManager.NormalizeEnvKey(rawEnvKey.Length > 0 ? rawEnvKey : rawId + "_API_KEY");
            var notes = new List<string>();

            if (!string.IsNullOrWhiteSpace(rawKey))
            {
                var cr = DshCredentialManager.UpsertCredentialRef(envKey, rawKey);
                if (!cr.Ok)
                {
                    ShowCredToast(cr.Message);
                    return;
                }
                notes.Add($"已存入 {envKey}");
            }

            string url = rawUrl.Trim();
            if (url.Length > 0 && (writeWeb || writeDesktop))
            {
                string pid = DshCredentialManager.NormalizeProviderId(rawId.Length > 0 ? rawId : rawDisplay);
                var enabledIds = modelConfigs
                    .Where(m => m.Enabled && !string.IsNullOrWhiteSpace(m.Id))
                    .Select(m => m.Id.Trim())
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                var draft = new DshProviderDraft
                {
                    ProviderId = pid,
                    DisplayName = string.IsNullOrWhiteSpace(rawDisplay) ? pid : rawDisplay.Trim(),
                    ApiKeyEnv = envKey,
                    Api = api,
                    BaseUrl = url,
                    Models = enabledIds,
                    ModelConfigs = modelConfigs.Select(m => m.Clone()).ToList(),
                    Special = special.Clone(),
                };

                var profiles = DshCredentialManager.ResolveProfiles();
                if (writeWeb)
                {
                    var wp = profiles.FirstOrDefault(p => p.Kind == DshProfileKind.Web);
                    if (wp != null)
                    {
                        var r = DshCredentialManager.ApplyProviderToProfile(wp, draft);
                        notes.Add(r.Ok ? "已同步 Web 版" : r.Message);
                        if (r.Ok && !string.IsNullOrEmpty(r.ProviderId)
                            && !string.Equals(r.ProviderId, pid, StringComparison.OrdinalIgnoreCase))
                        {
                            DshFavorites.MigrateKey(DshFavorites.ProviderKey(pid), DshFavorites.ProviderKey(r.ProviderId));
                            pid = r.ProviderId;
                            draft.ProviderId = r.ProviderId;
                        }
                    }
                }
                if (writeDesktop)
                {
                    var dp = profiles.FirstOrDefault(p => p.Kind == DshProfileKind.Desktop);
                    if (dp != null)
                    {
                        var r = DshCredentialManager.ApplyProviderToProfile(dp, draft);
                        notes.Add(r.Ok ? "已同步桌面版" : r.Message);
                        if (r.Ok && !string.IsNullOrEmpty(r.ProviderId)
                            && !string.Equals(r.ProviderId, pid, StringComparison.OrdinalIgnoreCase))
                        {
                            DshFavorites.MigrateKey(DshFavorites.ProviderKey(pid), DshFavorites.ProviderKey(r.ProviderId));
                            pid = r.ProviderId;
                        }
                    }
                }
            }
            else if (string.IsNullOrWhiteSpace(rawKey))
            {
                ShowCredToast("请填写 API Key，或填写完整的 Base URL 以保存模型提供商。");
                return;
            }

            CloseCredentialPopups();
            _credFormOpen = false;
            _credEditingProvider = null;
            InvalidateDshScans();
            ShowCredToast(notes.Count > 0 ? string.Join(" · ", notes) : "保存成功");
            RenderCredentialsView();
        }
        finally
        {
            ExitDshBusy(refresh: false);
        }
    }

    // ══════════════ 模型提供商卡片（极简折叠 + 点击原地展开内联设置）══════════════

    private static bool IsInsideButtonBase(object? source, DependencyObject stopAt)
    {
        var cur = source as DependencyObject;
        while (cur != null && !ReferenceEquals(cur, stopAt))
        {
            if (cur is ButtonBase) return true;
            cur = cur is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(cur)
                : LogicalTreeHelper.GetParent(cur);
        }
        return false;
    }

    private UIElement BuildProviderCard(DshLlmProvider p)
    {
        string favKey = DshFavorites.ProviderKey(p.ProviderId);
        bool starred = DshFavorites.IsStarred(favKey);
        string activeProfileId = _ctx.Target == GuardTarget.Desktop ? "desktop" : "web";
        bool activeOnCurrent = p.EnabledIn(activeProfileId);
        bool isExpanded = string.Equals(_credExpandedProviderId, p.ProviderId, StringComparison.OrdinalIgnoreCase);

        var cardBody = new StackPanel();

        // ── 折叠态极简头部（点击直接原地展开/收起设置）──
        var headerGrid = new Grid
        {
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
        };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var avatar = BuildProviderAvatar(p);
        Grid.SetColumn(avatar, 0);
        headerGrid.Children.Add(avatar);

        var center = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };

        var line1 = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        line1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (!p.CredentialReady)
            line1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var starBtn = BuildDshStarButton(favKey, "模型提供商", AfterCredStarToggle);
        Grid.SetColumn(starBtn, 0);
        line1.Children.Add(starBtn);

        var title = DshWidgets.Text(p.DisplayName, 13.5, DshTextPrimary, FontWeights.SemiBold);
        title.VerticalAlignment = VerticalAlignment.Center;
        title.TextWrapping = TextWrapping.NoWrap;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(title, 1);
        line1.Children.Add(title);

        var protoPill = BuildProtocolShortBadge(p.Api);
        protoPill.Margin = new Thickness(8, 0, 0, 0);
        protoPill.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(protoPill, 2);
        line1.Children.Add(protoPill);

        if (!p.CredentialReady)
        {
            var missingBadge = DshWidgets.Badge("未配密钥", DshWarn);
            missingBadge.Margin = new Thickness(6, 0, 0, 0);
            missingBadge.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(missingBadge, 3);
            line1.Children.Add(missingBadge);
        }
        center.Children.Add(line1);

        int enabledModelCount = p.ModelConfigs.Count > 0
            ? p.ModelConfigs.Count(m => m.Enabled)
            : p.Models.Count;
        string urlText = string.IsNullOrWhiteSpace(p.BaseUrl) ? p.ProviderId : p.BaseUrl;
        string subSummary = enabledModelCount > 0 ? $"{urlText} · {enabledModelCount} 模型" : urlText;
        var subLine = DshWidgets.Hint(subSummary, 11.5);
        subLine.TextWrapping = TextWrapping.NoWrap;
        subLine.TextTrimming = TextTrimming.CharacterEllipsis;
        subLine.Margin = new Thickness(0, 3, 0, 0);
        center.Children.Add(subLine);

        Grid.SetColumn(center, 1);
        headerGrid.Children.Add(center);

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        if (activeOnCurrent && p.CredentialReady)
        {
            var activeText = DshWidgets.Text("● 使用中", 11.5, DshOk, FontWeights.SemiBold);
            activeText.VerticalAlignment = VerticalAlignment.Center;
            activeText.Margin = new Thickness(0, 0, 10, 0);
            right.Children.Add(activeText);
        }

        var chevron = DshWidgets.Text(isExpanded ? "\uE70E" : "\uE70D", 10.5, DshTextTertiary);
        chevron.FontFamily = new FontFamily("Segoe MDL2 Assets");
        chevron.VerticalAlignment = VerticalAlignment.Center;
        right.Children.Add(chevron);

        Grid.SetColumn(right, 2);
        headerGrid.Children.Add(right);

        headerGrid.MouseLeftButtonDown += (_, ev) =>
        {
            if (ev.ChangedButton != MouseButton.Left || IsInsideButtonBase(ev.OriginalSource, headerGrid)) return;
            ev.Handled = true;
            _credExpandedProviderId = isExpanded ? null : p.ProviderId;
            RebuildCredentialsBodyOnly();
        };

        cardBody.Children.Add(headerGrid);

        // ── 点击卡片后原地展开内联设置 ──
        if (isExpanded)
        {
            var rule = DshWidgets.Rule();
            rule.Margin = new Thickness(0, 10, 0, 10);
            cardBody.Children.Add(rule);

            var detail = new StackPanel();

            // 1. 启用端胶囊 + 关联环境变量名
            var syncRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            syncRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            syncRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            syncRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var syncLbl = DshWidgets.Hint("启用端", 11);
            syncLbl.VerticalAlignment = VerticalAlignment.Center;
            syncLbl.Margin = new Thickness(0, 0, 6, 0);
            Grid.SetColumn(syncLbl, 0);
            syncRow.Children.Add(syncLbl);

            var chipsPanel = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var profiles = DshCredentialManager.ResolveProfiles();
            foreach (var prof in profiles.Where(x => x.Kind is DshProfileKind.Web or DshProfileKind.Desktop
                                                  || (x.Kind == DshProfileKind.Headless && File.Exists(x.PatchFile()))))
            {
                bool enabled = p.EnabledIn(prof.Id);
                var chip = BuildProfileToggleChip(prof.DisplayName, enabled);
                chip.Margin = new Thickness(0, 0, 6, 0);
                chip.ToolTip = enabled
                    ? $"点击从「{prof.DisplayName}」移除此模型提供商（自动留 .bak 备份）"
                    : $"点击将该模型提供商一键同步到「{prof.DisplayName}」";
                var capturedProfile = prof;
                var capturedProvider = p;
                chip.MouseLeftButtonDown += (_, ev) =>
                {
                    ev.Handled = true;
                    ToggleProviderOnProfile(capturedProfile, capturedProvider, !enabled);
                };
                chipsPanel.Children.Add(chip);
            }
            Grid.SetColumn(chipsPanel, 1);
            syncRow.Children.Add(chipsPanel);

            var envTag = DshWidgets.Hint(p.ApiKeyEnv, 10.5);
            envTag.VerticalAlignment = VerticalAlignment.Center;
            envTag.ToolTip = $"环境变量键名：{p.ApiKeyEnv} · id: {p.ProviderId}";
            Grid.SetColumn(envTag, 2);
            syncRow.Children.Add(envTag);
            detail.Children.Add(syncRow);

            // 2. Base URL + 4 协议下拉（切换协议自动更新 URL 后缀）
            var (urlHost, urlBox) = DshWidgets.Input("Base URL（如 https://api.deepseek.com/v1）", double.NaN);
            urlBox.Text = p.BaseUrl ?? "";
            var protoCombo = CreateProtocolCombo(p.Api);
            protoCombo.Height = 30;

            var urlProtoRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            urlProtoRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            urlProtoRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            urlProtoRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(196) });
            Grid.SetColumn(urlHost, 0);
            Grid.SetColumn(protoCombo, 2);
            urlProtoRow.Children.Add(urlHost);
            urlProtoRow.Children.Add(protoCombo);
            detail.Children.Add(urlProtoRow);

            // 3. API Key（留空不改）
            string keyPlaceholder = p.CredentialReady
                ? $"API Key（当前 {p.CredentialMasked} · 留空不改，填入即更新官方表单）"
                : $"API Key（绑定 {p.ApiKeyEnv} · 填入即写入官方表单）";
            var (keyHost, keyBox) = DshWidgets.Input(keyPlaceholder, double.NaN);
            keyHost.Margin = new Thickness(0, 0, 0, 6);
            detail.Children.Add(keyHost);

            // 4. 协议特色路由配置 + 结构化模型列表
            var workingSpecial = p.Special.Clone();
            var workingModels = p.ModelConfigs.Count > 0
                ? p.ModelConfigs.Select(m => m.Clone()).ToList()
                : p.Models.Select(m => DshModelConfig.InferDefaults(m, p.Api)).ToList();

            var specialHost = new ContentControl { Margin = new Thickness(0, 2, 0, 8) };
            string initProto = protoCombo.SelectedItem?.ToString() ?? p.Api;
            specialHost.Content = BuildProtocolSettingsPanel(initProto, workingSpecial);
            detail.Children.Add(specialHost);

            var currentProviderState = p;
            Action? refreshInlineModelsUi = null;

            protoCombo.SelectionChanged += (_, _) =>
            {
                string nextProto = protoCombo.SelectedItem?.ToString() ?? p.Api;
                if (!string.IsNullOrWhiteSpace(urlBox.Text))
                    urlBox.Text = DshCredentialManager.SwitchBaseUrlForProtocol(urlBox.Text, nextProto);
                specialHost.Content = BuildProtocolSettingsPanel(nextProto, workingSpecial);
                refreshInlineModelsUi?.Invoke();
            };

            var modelSection = BuildModelConfiguratorSection(
                () => currentProviderState.DisplayName,
                () => currentProviderState.ProviderId,
                workingModels,
                () => protoCombo.SelectedItem?.ToString() ?? currentProviderState.Api,
                () => urlBox.Text.Trim(),
                () =>
                {
                    var typed = keyBox.Text.Trim();
                    if (typed.Length > 0) return typed;
                    return DshCredentialManager.TryResolveEnvSecretForTransientProbe(currentProviderState.ApiKeyEnv);
                },
                onModelStateCommitted: () =>
                {
                    string curProto = protoCombo.SelectedItem?.ToString() ?? currentProviderState.Api;
                    string curUrl = urlBox.Text.Trim();
                    if (curUrl.Length > 0 && string.IsNullOrWhiteSpace(keyBox.Text))
                    {
                        var updatedPid = PersistInlineModelsSilently(
                            currentProviderState,
                            curProto,
                            curUrl,
                            workingModels,
                            workingSpecial);
                        if (!string.IsNullOrEmpty(updatedPid)
                            && !string.Equals(updatedPid, currentProviderState.ProviderId, StringComparison.OrdinalIgnoreCase))
                        {
                            DshFavorites.MigrateKey(
                                DshFavorites.ProviderKey(currentProviderState.ProviderId),
                                DshFavorites.ProviderKey(updatedPid));
                            _credExpandedProviderId = updatedPid;
                        }
                        int activeCnt = workingModels.Count(m => m.Enabled);
                        subLine.Text = activeCnt > 0 ? $"{curUrl} · {activeCnt} 模型" : curUrl;
                    }
                },
                out refreshInlineModelsUi);
            detail.Children.Add(modelSection);

            // 5. 操作行
            var actionsBar = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 8, 0, 0) };
            var saveBtn = DshWidgets.SmallButton("保存", "primary");
            saveBtn.Margin = new Thickness(0, 0, 8, 0);
            saveBtn.Click += (_, _) =>
            {
                SaveInlineProviderChanges(
                    currentProviderState,
                    protoCombo.SelectedItem?.ToString() ?? currentProviderState.Api,
                    urlBox.Text,
                    keyBox.Text,
                    workingModels,
                    workingSpecial);
            };
            actionsBar.Children.Add(saveBtn);

            var collapseBtn = DshWidgets.SmallButton("收起");
            collapseBtn.Margin = new Thickness(0, 0, 8, 0);
            collapseBtn.Click += (_, _) =>
            {
                CloseCredentialPopups();
                _credExpandedProviderId = null;
                RebuildCredentialsBodyOnly();
            };
            actionsBar.Children.Add(collapseBtn);

            if (!starred)
            {
                var delBtn = DshWidgets.SmallButton("移除", "danger");
                delBtn.Margin = new Thickness(0);
                DockPanel.SetDock(delBtn, Dock.Right);
                delBtn.Click += (_, _) => RemoveProviderAll(currentProviderState);
                actionsBar.Children.Add(delBtn);
            }
            else
            {
                var locked = DshWidgets.Hint("已收藏置顶（取消星标可移除）", 10.5);
                locked.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(locked, Dock.Right);
                actionsBar.Children.Add(locked);
            }

            detail.Children.Add(actionsBar);
            cardBody.Children.Add(detail);
        }

        var card = DshWidgets.Card(cardBody, new Thickness(0, 0, 0, 8));
        card.Padding = new Thickness(14, 10, 14, 10);
        if (isExpanded)
            card.BorderBrush = new SolidColorBrush(Color.FromArgb(0xAA, 0x00, 0x7A, 0xFF));
        else if (activeOnCurrent)
            card.BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x00, 0x7A, 0xFF));

        var tipIds = p.ModelConfigs.Count > 0
            ? p.ModelConfigs.Where(m => m.Enabled).Select(m => m.Id).ToList()
            : p.Models.ToList();
        string modelTip = tipIds.Count > 0 ? string.Join("、", tipIds.Take(10)) + (tipIds.Count > 10 ? $" 等 {tipIds.Count} 个" : "") : "默认";
        card.ToolTip = $"{p.DisplayName}（点卡片展开设置）\nid: {p.ProviderId} · 协议: {p.Api} · env: {p.ApiKeyEnv} ({p.CredentialMasked})\n模型: {modelTip}";
        return card;
    }

    private void SaveInlineProviderChanges(
        DshLlmProvider existing,
        string api,
        string rawUrl,
        string rawKey,
        List<DshModelConfig> modelConfigs,
        DshProviderSpecialConfig special)
    {
        if (!TryEnterDshBusy())
        {
            ShowCredToast("正在处理其它写入，请稍候…");
            return;
        }
        try
        {
            var notes = new List<string>();
            if (!string.IsNullOrWhiteSpace(rawKey))
            {
                var cr = DshCredentialManager.UpsertCredentialRef(existing.ApiKeyEnv, rawKey);
                if (!cr.Ok)
                {
                    ShowCredToast(cr.Message);
                    return;
                }
                notes.Add($"已更新 {existing.ApiKeyEnv}");
            }

            string url = rawUrl.Trim();
            if (url.Length == 0)
            {
                ShowCredToast("Base URL 不能为空");
                return;
            }

            var enabledIds = modelConfigs
                .Where(m => m.Enabled && !string.IsNullOrWhiteSpace(m.Id))
                .Select(m => m.Id.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();

            string currentPid = existing.ProviderId;
            var draft = new DshProviderDraft
            {
                ProviderId = currentPid,
                DisplayName = existing.DisplayName,
                ApiKeyEnv = existing.ApiKeyEnv,
                Api = string.IsNullOrWhiteSpace(api) ? existing.Api : api,
                BaseUrl = url,
                Models = enabledIds,
                ModelConfigs = modelConfigs.Select(m => m.Clone()).ToList(),
                Special = special.Clone(),
            };

            var profiles = DshCredentialManager.ResolveProfiles();
            var targetProfiles = profiles.Where(prof => existing.EnabledIn(prof.Id)).ToList();
            if (targetProfiles.Count == 0)
            {
                var fallbackKind = _ctx.Target == GuardTarget.Desktop ? DshProfileKind.Desktop : DshProfileKind.Web;
                var fb = profiles.FirstOrDefault(prof => prof.Kind == fallbackKind) ?? profiles.FirstOrDefault();
                if (fb != null) targetProfiles.Add(fb);
            }

            foreach (var prof in targetProfiles)
            {
                var r = DshCredentialManager.ApplyProviderToProfile(prof, draft);
                notes.Add(r.Ok ? $"已同步 {prof.DisplayName}" : r.Message);
                if (r.Ok && !string.IsNullOrEmpty(r.ProviderId)
                    && !string.Equals(r.ProviderId, currentPid, StringComparison.OrdinalIgnoreCase))
                {
                    DshFavorites.MigrateKey(DshFavorites.ProviderKey(currentPid), DshFavorites.ProviderKey(r.ProviderId));
                    currentPid = r.ProviderId;
                    draft.ProviderId = r.ProviderId;
                }
            }

            CloseCredentialPopups();
            _credExpandedProviderId = null;
            InvalidateDshScans();
            ShowCredToast(notes.Count > 0 ? string.Join(" · ", notes) : "已保存");
            RenderCredentialsView();
        }
        finally
        {
            ExitDshBusy(refresh: false);
        }
    }

    private static Border BuildProviderAvatar(DshLlmProvider p)
    {
        string key = (p.ProviderId + " " + p.DisplayName).ToLowerInvariant();
        (string monogram, Color accent) = key switch
        {
            _ when key.Contains("deepseek") => ("DS", Color.FromRgb(0x25, 0x63, 0xEB)),
            _ when key.Contains("anthropic") || key.Contains("claude") => ("AI", Color.FromRgb(0xD9, 0x77, 0x06)),
            _ when key.Contains("openai") => ("OA", Color.FromRgb(0x10, 0xA3, 0x7F)),
            _ when key.Contains("google") || key.Contains("gemini") => ("G", Color.FromRgb(0x42, 0x85, 0xF4)),
            _ when key.Contains("openrouter") => ("OR", Color.FromRgb(0x7C, 0x3A, 0xED)),
            _ when key.Contains("devin") => ("D", Color.FromRgb(0x08, 0x91, 0xB2)),
            _ when key.Contains("kimi") || key.Contains("moonshot") => ("K", Color.FromRgb(0x02, 0x84, 0xC7)),
            _ when key.Contains("minimax") => ("MM", Color.FromRgb(0xE1, 0x1D, 0x48)),
            _ when key.Contains("zhipu") || key.Contains("glm") => ("Z", Color.FromRgb(0x4F, 0x46, 0xE5)),
            _ when key.Contains("qwen") || key.Contains("dashscope") => ("Q", Color.FromRgb(0x93, 0x33, 0xEA)),
            _ => (ExtractMonogram(p.DisplayName, p.ProviderId), Color.FromRgb(0x0A, 0x84, 0xFF)),
        };

        return new Border
        {
            Width = 38,
            Height = 38,
            CornerRadius = new CornerRadius(9),
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(0x28, accent.R, accent.G, accent.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, accent.R, accent.G, accent.B)),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = monogram,
                FontSize = monogram.Length > 1 ? 12.5 : 14.5,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(accent),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    private static string ExtractMonogram(string displayName, string providerId)
    {
        string raw = string.IsNullOrWhiteSpace(displayName) ? providerId : displayName;
        if (string.IsNullOrWhiteSpace(raw)) return "AI";
        var chars = raw.Where(char.IsLetterOrDigit).Take(2).ToArray();
        return chars.Length > 0 ? new string(chars).ToUpperInvariant() : "AI";
    }

    private static Border BuildProfileToggleChip(string label, bool enabled)
    {
        var txt = new TextBlock
        {
            Text = (enabled ? "✓ " : "＋ ") + label,
            FontSize = 10.5,
            FontWeight = enabled ? FontWeights.SemiBold : FontWeights.Normal,
        };
        var chip = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(7, 2, 7, 2),
            Margin = new Thickness(4, 0, 0, 0),
            Cursor = Cursors.Hand,
            Child = txt,
        };
        if (enabled)
        {
            chip.Background = new SolidColorBrush(Color.FromArgb(0x26, DshOk.R, DshOk.G, DshOk.B));
            chip.BorderBrush = new SolidColorBrush(Color.FromArgb(0x77, DshOk.R, DshOk.G, DshOk.B));
            txt.Foreground = new SolidColorBrush(DshOk);
        }
        else
        {
            DshWidgets.BindToken(chip, Border.BackgroundProperty, "Brush.Surface.Button", DshWidgets.ButtonBg);
            DshWidgets.BindToken(chip, Border.BorderBrushProperty, "Brush.Border.Default", DshWidgets.Border);
            DshWidgets.BindToken(txt, TextBlock.ForegroundProperty, "Brush.Text.Secondary", DshWidgets.TextSecondary);
        }
        ButtonFx.Wire(chip);
        return chip;
    }

    private void ToggleProviderOnProfile(DshProfileInfo profile, DshLlmProvider provider, bool enable)
    {
        if (!TryEnterDshBusy()) { ShowCredToast("正在写入，请稍候…"); return; }
        try
        {
            DshCredentialWriteResult res;
            if (enable)
            {
                var draft = new DshProviderDraft
                {
                    ProviderId = provider.ProviderId,
                    DisplayName = provider.DisplayName,
                    ApiKeyEnv = provider.ApiKeyEnv,
                    Api = provider.Api,
                    BaseUrl = provider.BaseUrl,
                    Models = provider.Models,
                    ModelConfigs = provider.ModelConfigs.Select(m => m.Clone()).ToList(),
                    Special = provider.Special.Clone(),
                };
                res = DshCredentialManager.ApplyProviderToProfile(profile, draft);
                if (res.Ok && !string.IsNullOrEmpty(res.ProviderId)
                    && !string.Equals(res.ProviderId, provider.ProviderId, StringComparison.OrdinalIgnoreCase))
                {
                    DshFavorites.MigrateKey(DshFavorites.ProviderKey(provider.ProviderId), DshFavorites.ProviderKey(res.ProviderId));
                    if (string.Equals(_credExpandedProviderId, provider.ProviderId, StringComparison.OrdinalIgnoreCase))
                        _credExpandedProviderId = res.ProviderId;
                }
            }
            else
            {
                res = DshCredentialManager.RemoveProviderFromProfile(profile, provider.ProviderId);
            }

            InvalidateDshScans();
            ShowCredToast(res.Message);
            RenderCredentialsView();
        }
        finally
        {
            ExitDshBusy(refresh: false);
        }
    }

    private void RemoveProviderAll(DshLlmProvider p)
    {
        var ans = GuardDialog.Show(
            $"确定从已启用的 DSH 配置集中移除模型提供商「{p.DisplayName}」？\n\n" +
            $"注：环境变量凭据 {p.ApiKeyEnv} 仍安全保留在 .credentials.yaml 中，写入前会自动留 .bak-provider-* 备份。",
            $"确认移除提供商「{p.DisplayName}」",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (ans != MessageBoxResult.Yes) return;

        if (!TryEnterDshBusy()) { ShowCredToast("正在处理其它写入，请稍候…"); return; }
        try
        {
            var msgs = new List<string>();
            foreach (var prof in DshCredentialManager.ResolveProfiles())
            {
                if (!p.EnabledIn(prof.Id)) continue;
                var r = DshCredentialManager.RemoveProviderFromProfile(prof, p.ProviderId);
                if (r.Ok) msgs.Add(prof.DisplayName);
            }
            DshCredentialManager.ClearDisabledModels(p.ProviderId);
            if (string.Equals(_credExpandedProviderId, p.ProviderId, StringComparison.OrdinalIgnoreCase))
                _credExpandedProviderId = null;
            InvalidateDshScans();
            ShowCredToast(msgs.Count > 0 ? $"已从 {string.Join("、", msgs)} 移除 {p.DisplayName}" : "未发现已启用的配置项");
            RenderCredentialsView();
        }
        finally
        {
            ExitDshBusy(refresh: false);
        }
    }

    // ══════════════ 凭据保险箱条目卡片（极简折叠 + 点击原地展开更新，严格脱敏 + 账号态只读）══════════════

    private UIElement BuildCredentialRowCard(DshCredentialEntry e, List<string>? linkedProviders)
    {
        string favKey = DshFavorites.CredentialKey(e.Key);
        bool starred = DshFavorites.IsStarred(favKey);
        bool canExpand = !e.IsReadOnlyAccount;
        bool isExpanded = canExpand && string.Equals(_credExpandedEnvKey, e.Key, StringComparison.OrdinalIgnoreCase);

        var cardBody = new StackPanel();
        var headerGrid = new Grid
        {
            Background = Brushes.Transparent,
            Cursor = canExpand ? Cursors.Hand : Cursors.Arrow,
        };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Color tone = e.IsReadOnlyAccount ? DshAccent : (e.HasValue ? DshOk : DshWarn);
        var iconBox = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(0x28, tone.R, tone.G, tone.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, tone.R, tone.G, tone.B)),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = e.IsReadOnlyAccount ? "\uE72E" : "\uE8D7",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                Foreground = new SolidColorBrush(tone),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        Grid.SetColumn(iconBox, 0);
        headerGrid.Children.Add(iconBox);

        var center = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var starBtn = BuildDshStarButton(favKey, "凭据条目", AfterCredStarToggle);
        Grid.SetColumn(starBtn, 0);
        top.Children.Add(starBtn);

        var keyText = DshWidgets.Text(e.Key, 13, DshTextPrimary, FontWeights.SemiBold);
        keyText.VerticalAlignment = VerticalAlignment.Center;
        keyText.TextWrapping = TextWrapping.NoWrap;
        keyText.TextTrimming = TextTrimming.CharacterEllipsis;
        keyText.ToolTip = e.Key;
        Grid.SetColumn(keyText, 1);
        top.Children.Add(keyText);
        center.Children.Add(top);

        string subText = e.MaskedValue;
        if (linkedProviders is { Count: > 0 })
            subText += " · " + string.Join("、", linkedProviders);
        else if (e.IsReadOnlyAccount)
            subText += $" · 账号态（{e.LineCount} 行）";
        var sub = DshWidgets.Hint(subText, 11.5);
        sub.TextWrapping = TextWrapping.NoWrap;
        sub.TextTrimming = TextTrimming.CharacterEllipsis;
        sub.Margin = new Thickness(0, 3, 0, 0);
        center.Children.Add(sub);

        Grid.SetColumn(center, 1);
        headerGrid.Children.Add(center);

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        if (e.IsReadOnlyAccount)
        {
            var roHint = DshWidgets.Hint("只读", 11);
            roHint.VerticalAlignment = VerticalAlignment.Center;
            roHint.ToolTip = "账号态 JSON 令牌受只读保护，守护壳禁止改写或删除。";
            right.Children.Add(roHint);
        }
        else
        {
            var chevron = DshWidgets.Text(isExpanded ? "\uE70E" : "\uE70D", 10.5, DshTextTertiary);
            chevron.FontFamily = new FontFamily("Segoe MDL2 Assets");
            chevron.VerticalAlignment = VerticalAlignment.Center;
            right.Children.Add(chevron);
        }
        Grid.SetColumn(right, 2);
        headerGrid.Children.Add(right);

        if (canExpand)
        {
            headerGrid.MouseLeftButtonDown += (_, ev) =>
            {
                if (ev.ChangedButton != MouseButton.Left || IsInsideButtonBase(ev.OriginalSource, headerGrid)) return;
                ev.Handled = true;
                _credExpandedEnvKey = isExpanded ? null : e.Key;
                RebuildCredentialsBodyOnly();
            };
        }

        cardBody.Children.Add(headerGrid);

        if (isExpanded)
        {
            var rule = DshWidgets.Rule();
            rule.Margin = new Thickness(0, 9, 0, 9);
            cardBody.Children.Add(rule);

            var detail = new StackPanel();
            var (keyHost, keyBox) = DshWidgets.Input($"输入新的 {e.Key} 值（直接写入官方 .credentials.yaml）", double.NaN);
            keyHost.Margin = new Thickness(0, 0, 0, 9);
            detail.Children.Add(keyHost);

            var actionsBar = new DockPanel { LastChildFill = false };
            var saveBtn = DshWidgets.SmallButton("保存密钥", "primary");
            saveBtn.Margin = new Thickness(0, 0, 8, 0);
            saveBtn.Click += (_, _) =>
            {
                var r = DshCredentialManager.UpsertCredentialRef(e.Key, keyBox.Text);
                if (!r.Ok) { ShowCredToast(r.Message); return; }
                _credExpandedEnvKey = null;
                InvalidateDshScans();
                ShowCredToast(r.Message);
                RenderCredentialsView();
            };
            actionsBar.Children.Add(saveBtn);

            var copyBtn = DshWidgets.SmallButton("复制键名");
            copyBtn.Margin = new Thickness(0, 0, 8, 0);
            copyBtn.ToolTip = "仅复制环境变量键名（如 DEEPSEEK_API_KEY），不复制明文密钥";
            copyBtn.Click += (_, _) =>
            {
                try
                {
                    Clipboard.SetText(e.Key);
                    ShowCredToast("已复制环境变量名：" + e.Key);
                }
                catch (Exception ex) { ShowCredToast("复制失败：" + ex.Message); }
            };
            actionsBar.Children.Add(copyBtn);

            var collapseBtn = DshWidgets.SmallButton("收起");
            collapseBtn.Margin = new Thickness(0, 0, 8, 0);
            collapseBtn.Click += (_, _) =>
            {
                _credExpandedEnvKey = null;
                RebuildCredentialsBodyOnly();
            };
            actionsBar.Children.Add(collapseBtn);

            if (!starred)
            {
                var delBtn = DshWidgets.SmallButton("删除", "danger");
                delBtn.Margin = new Thickness(0);
                DockPanel.SetDock(delBtn, Dock.Right);
                delBtn.Click += (_, _) => DeleteCredentialEntry(e.Key);
                actionsBar.Children.Add(delBtn);
            }
            else
            {
                var locked = DshWidgets.Hint("已收藏置顶（取消星标可删）", 10.5);
                locked.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(locked, Dock.Right);
                actionsBar.Children.Add(locked);
            }

            detail.Children.Add(actionsBar);
            cardBody.Children.Add(detail);
        }

        var card = DshWidgets.Card(cardBody, new Thickness(0, 0, 0, 8), dim: e.IsReadOnlyAccount);
        card.Padding = new Thickness(14, 10, 14, 10);
        if (isExpanded)
            card.BorderBrush = new SolidColorBrush(Color.FromArgb(0xAA, 0x00, 0x7A, 0xFF));
        return card;
    }

    private void DeleteCredentialEntry(string key)
    {
        var (title, body) = DshText.ConfirmDeleteCredential(key);
        if (GuardDialog.Show(body, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        if (!TryEnterDshBusy()) { ShowCredToast("正在处理其它写入，请稍候…"); return; }
        try
        {
            var r = DshCredentialManager.DeleteCredentialRef(key);
            if (r.Ok && string.Equals(_credExpandedEnvKey, key, StringComparison.OrdinalIgnoreCase))
                _credExpandedEnvKey = null;
            InvalidateDshScans();
            ShowCredToast(r.Message);
            RenderCredentialsView();
        }
        finally
        {
            ExitDshBusy(refresh: false);
        }
    }

    /// <summary>把正文滚动壳滚回顶部（抽屉 / 向导 / 快捷更新面板都在正文最上方）。</summary>
    private void ScrollCredentialsToTop()
    {
        try
        {
            DependencyObject? o = CredentialsRoot;
            for (int i = 0; o != null && i < 6; i++)
            {
                if (o is ScrollViewer sv) { sv.ScrollToTop(); return; }
                o = (o as FrameworkElement)?.Parent ?? VisualTreeHelper.GetParent(o);
            }
        }
        catch { }
    }

    /// <summary>凭据页的星标回调：回执写到凭据页底部，并重绘凭据页（不去动综合页）。</summary>
    private void AfterCredStarToggle(bool nowStarred)
    {
        ShowCredToast(nowStarred ? "已收藏，排到最前面。" : "已取消收藏。");
        RenderCredentialsView();
    }

    // ══════════════ 小布局助手 ══════════════

    private static Grid BuildTwoColRow(UIElement left, UIElement right)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 2);
        g.Children.Add(left);
        g.Children.Add(right);
        return g;
    }

    private static CheckBox MakeThemedCheckBox(string text, bool isChecked)
    {
        var labelTb = new TextBlock
        {
            Text = text,
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DshWidgets.BindToken(labelTb, TextBlock.ForegroundProperty, "Brush.Text.Primary", DshWidgets.TextPrimary);
        var cb = new CheckBox
        {
            Content = labelTb,
            IsChecked = isChecked,
            Margin = new Thickness(0, 0, 12, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
        };
        DshWidgets.BindToken(cb, Control.ForegroundProperty, "Brush.Text.Primary", DshWidgets.TextPrimary);
        return cb;
    }

    private static T MarginRight<T>(T el, double px) where T : FrameworkElement
    {
        el.Margin = new Thickness(0, 0, px, 2);
        return el;
    }

    private static T MarginLeft<T>(T el, double px) where T : FrameworkElement
    {
        el.Margin = new Thickness(px, 0, 0, 0);
        return el;
    }

    // ══════════════ 4 协议路由配置 + 结构化模型下拉与行内操作助手 ══════════════

    private static Border BuildProtocolShortBadge(string? api)
    {
        string norm = (api ?? "openai-completions").Trim().ToLowerInvariant();
        (string text, Color accent) = norm switch
        {
            "openai-responses" => ("responses", Color.FromRgb(0x02, 0x84, 0xC7)),
            "anthropic-messages" => ("anthropic", Color.FromRgb(0xD9, 0x77, 0x06)),
            "google-generative-ai" => ("google", Color.FromRgb(0x16, 0xA3, 0x4A)),
            _ => ("openai", Color.FromRgb(0x25, 0x63, 0xEB)),
        };
        return DshWidgets.Badge(text, accent);
    }

    private static ComboBox CreateProtocolCombo(string? initialApi)
    {
        var combo = DshWidgets.Combo(
            "openai-completions",
            "openai-responses",
            "anthropic-messages",
            "google-generative-ai");
        SelectProtocolInCombo(combo, initialApi);
        return combo;
    }

    private static void SelectProtocolInCombo(ComboBox combo, string? api)
    {
        string target = (api ?? "openai-completions").Trim().ToLowerInvariant();
        if (target is "google" or "gemini" or "gemini-cli") target = "google-generative-ai";
        else if (target is "anthropic" or "claude") target = "anthropic-messages";
        else if (target is "responses") target = "openai-responses";
        else if (target is "openai") target = "openai-completions";

        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (string.Equals(combo.Items[i]?.ToString(), target, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = i;
                return;
            }
        }
        combo.SelectedIndex = 0;
    }

    private static Border BuildProtocolSettingsPanel(string protocol, DshProviderSpecialConfig special)
    {
        string normProto = (protocol ?? "openai-completions").Trim().ToLowerInvariant();
        var stack = new StackPanel();

        // 顶层通用行：默认推理档位 + 默认识图
        var commonRow = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        var reasoningCombo = DshWidgets.Combo("跟随默认", "off", "minimal", "low", "medium", "high", "xhigh", "max");
        reasoningCombo.Height = 28;
        reasoningCombo.Width = 104;
        SelectStringComboValue(reasoningCombo, special.Reasoning);
        reasoningCombo.SelectionChanged += (_, _) =>
        {
            special.Reasoning = reasoningCombo.SelectedIndex <= 0
                ? string.Empty
                : (reasoningCombo.SelectedItem?.ToString() ?? string.Empty);
        };
        commonRow.Children.Add(MakeSettingPair("默认推理档位", reasoningCombo));

        var defaultImgCheck = MakeThemedCheckBox("默认识图 (image)", special.DefaultImageInput);
        defaultImgCheck.ToolTip = "写入路由级 defaultInput: [text, image]，未单独声明模态的模型也将默认允许图片输入";
        defaultImgCheck.Checked += (_, _) => special.DefaultImageInput = true;
        defaultImgCheck.Unchecked += (_, _) => special.DefaultImageInput = false;
        commonRow.Children.Add(defaultImgCheck);
        stack.Children.Add(commonRow);

        // 协议专项设置行
        var protoRow = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
        };

        if (normProto == "anthropic-messages")
        {
            var adaptCombo = DshWidgets.Combo("自动默认", "强制开启", "强制关闭");
            adaptCombo.Height = 28;
            adaptCombo.Width = 108;
            adaptCombo.SelectedIndex = special.ForceAdaptiveThinking switch
            {
                true => 1,
                false => 2,
                _ => 0,
            };
            adaptCombo.SelectionChanged += (_, _) =>
            {
                special.ForceAdaptiveThinking = adaptCombo.SelectedIndex switch
                {
                    1 => true,
                    2 => false,
                    _ => null,
                };
            };
            protoRow.Children.Add(MakeSettingPair("Adaptive Thinking", adaptCombo));

            var cacheCombo = DshWidgets.Combo("默认", "none", "short", "long");
            cacheCombo.Height = 28;
            cacheCombo.Width = 92;
            SelectStringComboValue(cacheCombo, special.CacheRetention);
            cacheCombo.SelectionChanged += (_, _) =>
            {
                special.CacheRetention = cacheCombo.SelectedIndex <= 0
                    ? string.Empty
                    : (cacheCombo.SelectedItem?.ToString() ?? string.Empty);
            };
            protoRow.Children.Add(MakeSettingPair("缓存保留", cacheCombo));

            protoRow.Children.Add(MakeBudgetInput(special));

            var toolCacheCb = MakeThreeStateCheckBox(
                "工具缓存控制",
                special.SupportsCacheControlOnTools,
                "compat.supportsCacheControlOnTools（半选=默认，勾选=true，未选=false）",
                v => special.SupportsCacheControlOnTools = v);
            var longCacheCb = MakeThreeStateCheckBox(
                "长效缓存",
                special.SupportsLongCacheRetention,
                "compat.supportsLongCacheRetention（半选=默认，勾选=true，未选=false）",
                v => special.SupportsLongCacheRetention = v);
            protoRow.Children.Add(toolCacheCb);
            protoRow.Children.Add(longCacheCb);
        }
        else if (normProto == "openai-completions")
        {
            var tfCombo = DshWidgets.Combo("默认", "openai", "qwen", "zai", "qwen-chat-template");
            tfCombo.Height = 28;
            tfCombo.Width = 148;
            SelectStringComboValue(tfCombo, special.ThinkingFormat);
            tfCombo.SelectionChanged += (_, _) =>
            {
                special.ThinkingFormat = tfCombo.SelectedIndex <= 0
                    ? string.Empty
                    : (tfCombo.SelectedItem?.ToString() ?? string.Empty);
            };
            protoRow.Children.Add(MakeSettingPair("思考格式", tfCombo));

            var mtCombo = DshWidgets.Combo("默认", "max_completion_tokens", "max_tokens");
            mtCombo.Height = 28;
            mtCombo.Width = 172;
            SelectStringComboValue(mtCombo, special.MaxTokensField);
            mtCombo.SelectionChanged += (_, _) =>
            {
                special.MaxTokensField = mtCombo.SelectedIndex <= 0
                    ? string.Empty
                    : (mtCombo.SelectedItem?.ToString() ?? string.Empty);
            };
            protoRow.Children.Add(MakeSettingPair("最大 Token 字段", mtCombo));

            protoRow.Children.Add(MakeThreeStateCheckBox(
                "developer 角色",
                special.SupportsDeveloperRole,
                "compat.supportsDeveloperRole（半选=默认，勾选=true，未选=false）",
                v => special.SupportsDeveloperRole = v));
            protoRow.Children.Add(MakeThreeStateCheckBox(
                "回传 reasoning_content",
                special.RequiresReasoningContent,
                "compat.requiresReasoningContentOnAssistantMessages（DeepSeek R1 等推理链回传）",
                v => special.RequiresReasoningContent = v));
            protoRow.Children.Add(MakeThreeStateCheckBox(
                "支持 reasoning_effort",
                special.SupportsReasoningEffort,
                "compat.supportsReasoningEffort（半选=默认，勾选=true，未选=false）",
                v => special.SupportsReasoningEffort = v));
        }
        else if (normProto == "openai-responses")
        {
            var cacheCombo = DshWidgets.Combo("默认", "none", "short", "long");
            cacheCombo.Height = 28;
            cacheCombo.Width = 96;
            SelectStringComboValue(cacheCombo, special.CacheRetention);
            cacheCombo.SelectionChanged += (_, _) =>
            {
                special.CacheRetention = cacheCombo.SelectedIndex <= 0
                    ? string.Empty
                    : (cacheCombo.SelectedItem?.ToString() ?? string.Empty);
            };
            protoRow.Children.Add(MakeSettingPair("缓存保留", cacheCombo));
            protoRow.Children.Add(MakeBudgetInput(special));
        }
        else if (normProto == "google-generative-ai")
        {
            protoRow.Children.Add(MakeBudgetInput(special));
            var gHint = DshWidgets.Hint("保存时自动对齐 google 原生路由（省略 api 字段以启用 /v1beta）", 10.5);
            gHint.VerticalAlignment = VerticalAlignment.Center;
            gHint.Margin = new Thickness(4, 0, 0, 4);
            protoRow.Children.Add(gHint);
        }

        if (protoRow.Children.Count > 0) stack.Children.Add(protoRow);

        var card = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 7, 10, 4),
            Child = stack,
        };
        DshWidgets.BindToken(card, Border.BackgroundProperty, "Brush.Surface.Input", DshWidgets.FieldBg);
        DshWidgets.BindToken(card, Border.BorderBrushProperty, "Brush.Border.Subtle", DshWidgets.Divider);
        return card;
    }

    private static TextBlock MakeSettingInlineLabel(string text)
    {
        var lbl = DshWidgets.Hint(text + "：", 11);
        lbl.VerticalAlignment = VerticalAlignment.Center;
        lbl.Margin = new Thickness(0, 0, 4, 4);
        return lbl;
    }

    private static UIElement MakeBudgetInput(DshProviderSpecialConfig special)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 4),
        };
        row.Children.Add(MakeSettingInlineLabel("思考预算(medium)"));
        var (host, box) = DshWidgets.Input("0", 72);
        host.Height = 28;
        box.FontSize = 11.5;
        box.Text = special.ThinkingBudgetMedium > 0 ? special.ThinkingBudgetMedium.ToString() : "0";
        box.ToolTip = "Google/思考模型 medium 档思考 token 预算，0 表示使用默认";
        box.TextChanged += (_, _) =>
        {
            if (int.TryParse((box.Text ?? string.Empty).Trim(), out int v) && v >= 0)
                special.ThinkingBudgetMedium = v;
        };
        row.Children.Add(host);
        return row;
    }

    private static void SelectStringComboValue(ComboBox combo, string? value)
    {
        string target = (value ?? string.Empty).Trim();
        if (target.Length == 0)
        {
            combo.SelectedIndex = 0;
            return;
        }
        for (int i = 1; i < combo.Items.Count; i++)
        {
            if (string.Equals(combo.Items[i]?.ToString(), target, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = i;
                return;
            }
        }
        combo.SelectedIndex = 0;
    }

    private static UIElement MakeSettingPair(string label, FrameworkElement control)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 4),
        };
        var lbl = DshWidgets.Hint(label + "：", 11);
        lbl.VerticalAlignment = VerticalAlignment.Center;
        lbl.Margin = new Thickness(0, 0, 4, 0);
        row.Children.Add(lbl);
        control.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(control);
        return row;
    }

    private static CheckBox MakeThreeStateCheckBox(string text, bool? initialValue, string tooltip, Action<bool?> onChange)
    {
        var labelTb = new TextBlock
        {
            Text = text,
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DshWidgets.BindToken(labelTb, TextBlock.ForegroundProperty, "Brush.Text.Primary", DshWidgets.TextPrimary);
        var cb = new CheckBox
        {
            Content = labelTb,
            IsThreeState = true,
            IsChecked = initialValue,
            Margin = new Thickness(0, 0, 12, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            ToolTip = tooltip,
        };
        DshWidgets.BindToken(cb, Control.ForegroundProperty, "Brush.Text.Primary", DshWidgets.TextPrimary);
        cb.Checked += (_, _) => onChange(true);
        cb.Unchecked += (_, _) => onChange(false);
        cb.Indeterminate += (_, _) => onChange(null);
        return cb;
    }

    private UIElement BuildModelConfiguratorSection(
        Func<string> getProviderTitle,
        Func<string> getProviderIdKey,
        List<DshModelConfig> workingModels,
        Func<string> getProtocol,
        Func<string> getUrl,
        Func<string?> getTransientKey,
        Action? onModelStateCommitted,
        out Action refreshModelsUi)
    {
        var root = new StackPanel { Margin = new Thickness(0, 2, 0, 4) };

        // 1. 头部标题 + 计数徽章 + 获取模型列表 ▾ + 自定义模型
        var headerDock = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 6) };

        var rightBtns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var fetchBtn = DshWidgets.SmallButton("⬇ 获取模型列表 ▾");
        fetchBtn.Height = 28;
        fetchBtn.MinHeight = 28;
        fetchBtn.Margin = new Thickness(0, 0, 6, 0);
        fetchBtn.Padding = new Thickness(10, 0, 10, 0);
        fetchBtn.ToolTip = "向当前 Base URL 在线获取模型列表，支持搜索筛选与多选勾选";

        var addCustomBtn = DshWidgets.SmallButton("＋ 自定义模型");
        addCustomBtn.Height = 28;
        addCustomBtn.MinHeight = 28;
        addCustomBtn.Margin = new Thickness(0);
        addCustomBtn.Padding = new Thickness(10, 0, 10, 0);

        rightBtns.Children.Add(fetchBtn);
        rightBtns.Children.Add(addCustomBtn);
        DockPanel.SetDock(rightBtns, Dock.Right);
        headerDock.Children.Add(rightBtns);

        var leftTitle = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var titleLbl = DshWidgets.Text("已配置的模型", 12, DshTextPrimary, FontWeights.SemiBold);
        titleLbl.VerticalAlignment = VerticalAlignment.Center;
        leftTitle.Children.Add(titleLbl);

        var countText = new TextBlock
        {
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
        };
        DshWidgets.BindToken(countText, TextBlock.ForegroundProperty, "Brush.Accent.Primary", DshWidgets.Accent);
        var countPill = new Border
        {
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(7, 1, 7, 1),
            Margin = new Thickness(7, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(0x28, 0x0A, 0x84, 0xFF)),
            Child = countText,
        };
        leftTitle.Children.Add(countPill);
        headerDock.Children.Add(leftTitle);
        root.Children.Add(headerDock);

        // 2. 内联自定义模型输入行（默认折叠）
        var (customHost, customBox) = DshWidgets.Input("输入模型 ID（如 gpt-5 / gemini-2.5-pro / deepseek-reasoner）", double.NaN);
        var customAddBtn = DshWidgets.SmallButton("添加", "primary");
        customAddBtn.Height = 30;
        customAddBtn.MinHeight = 30;
        customAddBtn.Margin = new Thickness(6, 0, 0, 0);
        customAddBtn.Padding = new Thickness(12, 0, 12, 0);

        var customRow = new Grid
        {
            Margin = new Thickness(0, 0, 0, 6),
            Visibility = Visibility.Collapsed,
        };
        customRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        customRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(customHost, 0);
        Grid.SetColumn(customAddBtn, 1);
        customRow.Children.Add(customHost);
        customRow.Children.Add(customAddBtn);
        root.Children.Add(customRow);

        // 3. 结构化模型列表容器
        var rowsHost = new StackPanel();
        root.Children.Add(rowsHost);

        void RenderRows()
        {
            rowsHost.Children.Clear();
            int enabledCnt = workingModels.Count(m => m.Enabled);
            countText.Text = enabledCnt == workingModels.Count
                ? workingModels.Count.ToString()
                : $"{enabledCnt}/{workingModels.Count}";

            if (workingModels.Count == 0)
            {
                var emptyBox = new Border
                {
                    CornerRadius = new CornerRadius(8),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(12, 10, 12, 10),
                    Margin = new Thickness(0, 0, 0, 2),
                };
                DshWidgets.BindToken(emptyBox, Border.BackgroundProperty, "Brush.Surface.Input", DshWidgets.FieldBg);
                DshWidgets.BindToken(emptyBox, Border.BorderBrushProperty, "Brush.Border.Subtle", DshWidgets.Divider);
                var hint = DshWidgets.Hint("暂未配置模型，可点击「获取模型列表 ▾」在线拉取勾选，或点击「＋ 自定义模型」添加。", 11);
                emptyBox.Child = hint;
                rowsHost.Children.Add(emptyBox);
                return;
            }

            string curProto = getProtocol();
            string curPid = getProviderIdKey();

            foreach (var model in workingModels.ToList())
            {
                var captured = model;
                var rowCard = new Border
                {
                    CornerRadius = new CornerRadius(8),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(10, 7, 8, 7),
                    Margin = new Thickness(0, 0, 0, 5),
                    Opacity = captured.Enabled ? 1.0 : 0.60,
                };
                DshWidgets.BindToken(rowCard, Border.BackgroundProperty, "Brush.Surface.Input", DshWidgets.FieldBg);
                DshWidgets.BindToken(rowCard, Border.BorderBrushProperty, "Brush.Border.Subtle", DshWidgets.Divider);

                var rowDock = new DockPanel { LastChildFill = true };

                // 右侧操作组：自定义胶囊开关 + 测试 + 设置 + 移除
                var rightOps = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0),
                };

                var toggleSwitch = MakeCustomCapsuleSwitch(
                    captured.Enabled,
                    captured.Enabled ? "点击停用此模型（保留配置快照）" : "点击重新启用此模型",
                    () =>
                    {
                        captured.Enabled = !captured.Enabled;
                        RenderRows();
                        onModelStateCommitted?.Invoke();
                    });
                toggleSwitch.Margin = new Thickness(0, 0, 6, 0);
                rightOps.Children.Add(toggleSwitch);

                var pingBtn = DshWidgets.SmallButton("🔌");
                pingBtn.Width = 28;
                pingBtn.MinWidth = 28;
                pingBtn.Height = 24;
                pingBtn.MinHeight = 24;
                pingBtn.Padding = new Thickness(0);
                pingBtn.Margin = new Thickness(0, 0, 4, 0);
                pingBtn.ToolTip = "真实发起单模型连通性测速";
                pingBtn.Click += async (_, _) =>
                {
                    string url = getUrl().Trim();
                    if (url.Length == 0)
                    {
                        ShowCredToast("请先填写 Base URL 后再测速");
                        return;
                    }
                    string proto = getProtocol();
                    string? key = getTransientKey();
                    pingBtn.IsEnabled = false;
                    pingBtn.Content = "···";
                    try
                    {
                        var (ok, ms, detail) = await DshCredentialManager.TestModelConnectivityAsync(url, proto, key, captured.Id);
                        _credModelPingCache[MakePingKey(getProviderIdKey(), proto, captured.Id)] = (ok, ms, detail);
                        RenderRows();
                        ShowCredToast(ok
                            ? $"✓ {captured.Id} 连通正常（{ms}ms）"
                            : $"✕ {captured.Id} 测试失败：{detail}");
                    }
                    finally
                    {
                        pingBtn.IsEnabled = true;
                        pingBtn.Content = "🔌";
                    }
                };
                rightOps.Children.Add(pingBtn);

                var cfgBtn = DshWidgets.SmallButton("⚙");
                cfgBtn.Width = 28;
                cfgBtn.MinWidth = 28;
                cfgBtn.Height = 24;
                cfgBtn.MinHeight = 24;
                cfgBtn.Padding = new Thickness(0);
                cfgBtn.Margin = new Thickness(0, 0, 4, 0);
                cfgBtn.ToolTip = "编辑显示名称、图像/工具能力、推理等级、自定义请求参数与上下文窗口";
                cfgBtn.Click += (_, _) =>
                {
                    CloseCredentialPopups();
                    var dlg = new DshModelConfigDialog(getProviderTitle(), getProtocol(), captured)
                    {
                        Owner = this,
                    };
                    if (dlg.ShowDialog() == true)
                    {
                        RenderRows();
                        onModelStateCommitted?.Invoke();
                    }
                };
                rightOps.Children.Add(cfgBtn);

                var delBtn = DshWidgets.SmallButton("🗑", "danger");
                delBtn.Width = 28;
                delBtn.MinWidth = 28;
                delBtn.Height = 24;
                delBtn.MinHeight = 24;
                delBtn.Padding = new Thickness(0);
                delBtn.Margin = new Thickness(0);
                delBtn.ToolTip = "移除此模型";
                delBtn.Click += (_, _) =>
                {
                    workingModels.Remove(captured);
                    RenderRows();
                    onModelStateCommitted?.Invoke();
                };
                rightOps.Children.Add(delBtn);

                DockPanel.SetDock(rightOps, Dock.Right);
                rowDock.Children.Add(rightOps);

                // 左侧信息：显示名称（+ 原生 ID）+ 能力/状态徽章
                var leftInfo = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

                var nameLine = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                var mainName = DshWidgets.Text(captured.EffectiveDisplayName, 12.5, DshTextPrimary, FontWeights.SemiBold);
                mainName.TextTrimming = TextTrimming.CharacterEllipsis;
                nameLine.Children.Add(mainName);

                if (!string.IsNullOrWhiteSpace(captured.Name)
                    && !string.Equals(captured.Name.Trim(), captured.Id, StringComparison.Ordinal))
                {
                    var idHint = DshWidgets.Hint($"  {captured.Id}", 11);
                    idHint.VerticalAlignment = VerticalAlignment.Center;
                    idHint.TextTrimming = TextTrimming.CharacterEllipsis;
                    nameLine.Children.Add(idHint);
                }
                leftInfo.Children.Add(nameLine);

                var badgeRow = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
                if (captured.SupportsImage)
                    AddCompactModelBadge(badgeRow, "🖼 图像", Color.FromRgb(0x02, 0x84, 0xC7));
                if (captured.ToolUse)
                    AddCompactModelBadge(badgeRow, "🔧 工具", Color.FromRgb(0x25, 0x63, 0xEB));

                bool isAdaptive = curProto == "anthropic-messages"
                    && (captured.ForceAdaptiveThinking == true
                        || (captured.ForceAdaptiveThinking == null
                            && (captured.Id.Contains("opus-4-6", StringComparison.OrdinalIgnoreCase)
                                || captured.Id.Contains("opus-4-7", StringComparison.OrdinalIgnoreCase)
                                || captured.Id.Contains("sonnet-4-6", StringComparison.OrdinalIgnoreCase)
                                || captured.Id.Contains("sonnet-4-7", StringComparison.OrdinalIgnoreCase)
                                || captured.Id.Contains("claude-opus-5", StringComparison.OrdinalIgnoreCase)
                                || captured.Id.Contains("claude-sonnet-5", StringComparison.OrdinalIgnoreCase))));

                if (isAdaptive)
                    AddCompactModelBadge(badgeRow, "⚡ Adaptive", Color.FromRgb(0x7C, 0x3A, 0xED));
                else if (string.Equals(captured.ReasoningMode, "disabled", StringComparison.OrdinalIgnoreCase))
                    AddCompactModelBadge(badgeRow, "无推理", Color.FromRgb(0x6B, 0x72, 0x80));
                else if (string.Equals(captured.ReasoningMode, "custom", StringComparison.OrdinalIgnoreCase))
                    AddCompactModelBadge(badgeRow, $"🧠 推理 ({captured.ReasoningLevels.Count}档)", Color.FromRgb(0x6D, 0x28, 0xD9));
                else if (captured.ReasoningLevels.Count > 0
                    || captured.Id.Contains("reason", StringComparison.OrdinalIgnoreCase)
                    || captured.Id.Contains("r1", StringComparison.OrdinalIgnoreCase)
                    || captured.Id.Contains("thinking", StringComparison.OrdinalIgnoreCase))
                    AddCompactModelBadge(badgeRow, "🧠 推理", Color.FromRgb(0x6D, 0x28, 0xD9));

                if (captured.ContextWindow > 0)
                    AddCompactModelBadge(badgeRow, FormatTokenBadge(captured.ContextWindow), Color.FromRgb(0x0F, 0x76, 0x6E));

                if (_credModelPingCache.TryGetValue(MakePingKey(curPid, curProto, captured.Id), out var ping))
                {
                    if (ping.Ok)
                    {
                        AddCompactModelBadge(badgeRow, $"✓ {ping.LatencyMs}ms", DshOk);
                    }
                    else
                    {
                        var failBadge = DshWidgets.Badge("✕ 异常", DshError);
                        failBadge.Margin = new Thickness(0, 0, 5, 2);
                        failBadge.ToolTip = ping.Detail;
                        badgeRow.Children.Add(failBadge);
                    }
                }

                if (badgeRow.Children.Count > 0)
                    leftInfo.Children.Add(badgeRow);

                rowDock.Children.Add(leftInfo);
                rowCard.Child = rowDock;
                rowsHost.Children.Add(rowCard);
            }
        }

        void CommitCustomInput()
        {
            string id = (customBox.Text ?? string.Empty).Trim();
            if (id.Length == 0)
            {
                ShowCredToast("请输入模型 ID");
                return;
            }
            var existing = workingModels.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.Ordinal));
            if (existing != null)
            {
                existing.Enabled = true;
            }
            else
            {
                workingModels.Add(DshModelConfig.InferDefaults(id, getProtocol()));
            }
            customBox.Text = string.Empty;
            customRow.Visibility = Visibility.Collapsed;
            RenderRows();
            onModelStateCommitted?.Invoke();
        }

        addCustomBtn.Click += (_, _) =>
        {
            CloseCredentialPopups();
            bool open = customRow.Visibility != Visibility.Visible;
            customRow.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            if (open) customBox.Focus();
        };
        customAddBtn.Click += (_, _) => CommitCustomInput();
        customBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                CommitCustomInput();
                e.Handled = true;
            }
        };

        fetchBtn.Click += (_, _) =>
        {
            OpenModelPickerPopup(
                fetchBtn,
                workingModels,
                getProtocol,
                getUrl,
                getTransientKey,
                onChanged: () => RenderRows(),
                onPopupClosedAfterChange: () => onModelStateCommitted?.Invoke());
        };

        refreshModelsUi = RenderRows;
        RenderRows();
        return root;
    }

    private void OpenModelPickerPopup(
        Button anchor,
        List<DshModelConfig> workingModels,
        Func<string> getProtocol,
        Func<string> getUrl,
        Func<string?> getTransientKey,
        Action onChanged,
        Action onPopupClosedAfterChange)
    {
        if (_credModelPickerPopup != null && _credModelPickerPopup.IsOpen)
        {
            CloseCredentialPopups();
            return;
        }
        CloseCredentialPopups();

        bool changedSinceOpen = false;
        string proto = getProtocol();
        string url = getUrl().Trim();
        string cacheKey = $"{proto}|{url}";

        var surfaceBg = ThemeManager.IsDark
            ? Color.FromRgb(0x1E, 0x22, 0x2C)
            : Color.FromRgb(0xFF, 0xFF, 0xFF);

        var popupCard = new Border
        {
            Width = 340,
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(surfaceBg),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 4,
                Opacity = ThemeManager.IsDark ? 0.55 : 0.18,
                Color = Colors.Black,
            },
        };
        DshWidgets.BindToken(popupCard, Border.BorderBrushProperty, "Brush.Border.Default", DshWidgets.Border);

        var outer = new StackPanel();

        // 顶部栏：搜索框 + [↻] 刷新 + [全选]
        var topBar = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var (searchHost, searchBox) = DshWidgets.SearchBox("搜索模型...", double.NaN, "");
        searchHost.Height = 28;
        Grid.SetColumn(searchHost, 0);
        topBar.Children.Add(searchHost);

        var refreshBtn = DshWidgets.SmallButton("↻");
        refreshBtn.Width = 28;
        refreshBtn.MinWidth = 28;
        refreshBtn.Height = 28;
        refreshBtn.MinHeight = 28;
        refreshBtn.Padding = new Thickness(0);
        refreshBtn.Margin = new Thickness(0);
        refreshBtn.ToolTip = "向当前端点重新发起探测";
        Grid.SetColumn(refreshBtn, 2);
        topBar.Children.Add(refreshBtn);

        var selectAllBtn = DshWidgets.SmallButton("全选");
        selectAllBtn.Height = 28;
        selectAllBtn.MinHeight = 28;
        selectAllBtn.Padding = new Thickness(8, 0, 8, 0);
        selectAllBtn.Margin = new Thickness(0);
        Grid.SetColumn(selectAllBtn, 4);
        topBar.Children.Add(selectAllBtn);

        outer.Children.Add(topBar);

        var statusHint = DshWidgets.Hint("", 10.5);
        statusHint.Margin = new Thickness(2, 0, 2, 6);
        outer.Children.Add(statusHint);

        var listHost = new StackPanel();
        var scroller = new ScrollViewer
        {
            MaxHeight = 260,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = listHost,
        };
        outer.Children.Add(scroller);
        popupCard.Child = outer;

        var popup = new Popup
        {
            AllowsTransparency = true,
            StaysOpen = false,
            Placement = PlacementMode.Bottom,
            PlacementTarget = anchor,
            PopupAnimation = PopupAnimation.Fade,
            Child = popupCard,
        };
        _credModelPickerPopup = popup;
        ThemeManager.RegisterOrphanPopup(popup);
        popup.Closed += (_, _) =>
        {
            ThemeManager.UnregisterOrphanPopup(popup);
            if (ReferenceEquals(_credModelPickerPopup, popup)) _credModelPickerPopup = null;
            if (changedSinceOpen) onPopupClosedAfterChange();
        };

        List<string> candidateIds = _credModelProbeCache.TryGetValue(cacheKey, out var cached)
            ? new List<string>(cached)
            : workingModels.Select(m => m.Id).Distinct(StringComparer.Ordinal).ToList();

        List<string> GetFiltered()
        {
            string q = (searchBox.Text ?? string.Empty).Trim();
            if (q.Length == 0) return candidateIds;
            return candidateIds
                .Where(id => id.Contains(q, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        void RenderCandidateRows()
        {
            listHost.Children.Clear();
            var filtered = GetFiltered();
            var selectedSet = new HashSet<string>(workingModels.Select(m => m.Id), StringComparer.Ordinal);
            bool allPicked = filtered.Count > 0 && filtered.All(id => selectedSet.Contains(id));
            selectAllBtn.Content = allPicked ? "全清" : "全选";

            if (filtered.Count == 0)
            {
                var empty = DshWidgets.Hint(
                    candidateIds.Count == 0
                        ? (url.Length == 0 ? "请先填写 Base URL 后点 ↻ 获取模型列表" : "暂未发现可选模型，点击右上角 ↻ 重新探测")
                        : "未搜索到匹配的模型",
                    11);
                empty.Margin = new Thickness(4, 8, 4, 8);
                listHost.Children.Add(empty);
                return;
            }

            foreach (var mid in filtered)
            {
                bool picked = selectedSet.Contains(mid);
                var row = new Border
                {
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(8, 6, 8, 6),
                    Margin = new Thickness(0, 0, 0, 2),
                    Cursor = Cursors.Hand,
                    Background = picked
                        ? new SolidColorBrush(Color.FromArgb(0x22, 0x0A, 0x84, 0xFF))
                        : Brushes.Transparent,
                };
                row.MouseEnter += (_, _) =>
                {
                    if (!selectedSet.Contains(mid))
                        DshWidgets.BindToken(row, Border.BackgroundProperty, "Brush.Surface.HoverTile", DshWidgets.ButtonBg);
                };
                row.MouseLeave += (_, _) =>
                {
                    row.Background = selectedSet.Contains(mid)
                        ? new SolidColorBrush(Color.FromArgb(0x22, 0x0A, 0x84, 0xFF))
                        : Brushes.Transparent;
                };

                var rowStack = new DockPanel { LastChildFill = true };
                var mark = new TextBlock
                {
                    Text = picked ? "✓" : "○",
                    Width = 18,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                if (picked) DshWidgets.BindToken(mark, TextBlock.ForegroundProperty, "Brush.Accent.Primary", DshWidgets.Accent);
                else DshWidgets.BindToken(mark, TextBlock.ForegroundProperty, "Brush.Text.Tertiary", DshWidgets.TextTertiary);
                DockPanel.SetDock(mark, Dock.Left);
                rowStack.Children.Add(mark);

                var label = new TextBlock
                {
                    Text = mid,
                    FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                DshWidgets.BindToken(label, TextBlock.ForegroundProperty, "Brush.Text.Primary", DshWidgets.TextPrimary);
                rowStack.Children.Add(label);

                row.Child = rowStack;
                row.MouseLeftButtonDown += (_, ev) =>
                {
                    ev.Handled = true;
                    var existing = workingModels.FirstOrDefault(m => string.Equals(m.Id, mid, StringComparison.Ordinal));
                    if (existing != null)
                        workingModels.Remove(existing);
                    else
                        workingModels.Add(DshModelConfig.InferDefaults(mid, getProtocol()));

                    changedSinceOpen = true;
                    onChanged();
                    RenderCandidateRows();
                };
                listHost.Children.Add(row);
            }
        }

        async Task RunProbeAsync(bool forceToast)
        {
            string probeUrl = getUrl().Trim();
            string probeProto = getProtocol();
            if (probeUrl.Length == 0)
            {
                statusHint.Text = "请先填写 Base URL 后再点 ↻ 获取可用模型";
                RenderCandidateRows();
                return;
            }
            refreshBtn.IsEnabled = false;
            statusHint.Text = "正在向端点拉取模型列表…";
            try
            {
                var (ok, msg, list) = await DshCredentialManager.ProbeEndpointModelsAsync(probeUrl, getTransientKey(), probeProto);
                if (ok && list.Count > 0)
                {
                    var merged = list.Concat(workingModels.Select(m => m.Id))
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.Ordinal)
                        .ToList();
                    _credModelProbeCache[$"{probeProto}|{probeUrl}"] = merged;
                    candidateIds = merged;
                    statusHint.Text = $"已探测到 {list.Count} 个模型（勾选即加入，取消即移除）";
                }
                else
                {
                    statusHint.Text = string.IsNullOrWhiteSpace(msg) ? "未获取到模型列表" : msg;
                }
                if (forceToast && !string.IsNullOrWhiteSpace(msg))
                    ShowCredToast(msg);
                RenderCandidateRows();
            }
            finally
            {
                refreshBtn.IsEnabled = true;
            }
        }

        searchBox.TextChanged += (_, _) => RenderCandidateRows();
        refreshBtn.Click += async (_, _) => await RunProbeAsync(forceToast: true);
        selectAllBtn.Click += (_, _) =>
        {
            var filtered = GetFiltered();
            if (filtered.Count == 0) return;
            var selectedSet = new HashSet<string>(workingModels.Select(m => m.Id), StringComparer.Ordinal);
            bool allPicked = filtered.All(id => selectedSet.Contains(id));
            if (allPicked)
            {
                workingModels.RemoveAll(m => filtered.Contains(m.Id, StringComparer.Ordinal));
            }
            else
            {
                foreach (var mid in filtered)
                {
                    if (!selectedSet.Contains(mid))
                        workingModels.Add(DshModelConfig.InferDefaults(mid, getProtocol()));
                }
            }
            changedSinceOpen = true;
            onChanged();
            RenderCandidateRows();
        };

        if (_credModelProbeCache.TryGetValue(cacheKey, out var hitCache))
        {
            statusHint.Text = $"已缓存 {hitCache.Count} 个端点模型（点 ↻ 可重新拉取）";
            RenderCandidateRows();
        }
        else
        {
            RenderCandidateRows();
            if (url.Length > 0)
                _ = RunProbeAsync(forceToast: false);
        }

        ThemeManager.ApplyTo(popupCard);
        popup.IsOpen = true;
        searchBox.Focus();
    }

    private string PersistInlineModelsSilently(
        DshLlmProvider existing,
        string api,
        string url,
        List<DshModelConfig> workingModels,
        DshProviderSpecialConfig special)
    {
        try
        {
            var enabledIds = workingModels
                .Where(m => m.Enabled && !string.IsNullOrWhiteSpace(m.Id))
                .Select(m => m.Id.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();

            string currentPid = existing.ProviderId;
            var draft = new DshProviderDraft
            {
                ProviderId = currentPid,
                DisplayName = existing.DisplayName,
                ApiKeyEnv = existing.ApiKeyEnv,
                Api = string.IsNullOrWhiteSpace(api) ? existing.Api : api,
                BaseUrl = url.Trim(),
                Models = enabledIds,
                ModelConfigs = workingModels.Select(m => m.Clone()).ToList(),
                Special = special.Clone(),
            };

            var profiles = DshCredentialManager.ResolveProfiles();
            var targetProfiles = profiles.Where(prof => existing.EnabledIn(prof.Id)).ToList();
            if (targetProfiles.Count == 0)
            {
                var fallbackKind = _ctx.Target == GuardTarget.Desktop ? DshProfileKind.Desktop : DshProfileKind.Web;
                var fb = profiles.FirstOrDefault(prof => prof.Kind == fallbackKind) ?? profiles.FirstOrDefault();
                if (fb != null) targetProfiles.Add(fb);
            }

            foreach (var prof in targetProfiles)
            {
                var r = DshCredentialManager.ApplyProviderToProfile(prof, draft);
                if (r.Ok && !string.IsNullOrEmpty(r.ProviderId)
                    && !string.Equals(r.ProviderId, currentPid, StringComparison.OrdinalIgnoreCase))
                {
                    currentPid = r.ProviderId;
                    draft.ProviderId = r.ProviderId;
                }
            }
            InvalidateDshScans();
            return currentPid;
        }
        catch (Exception ex)
        {
            Logger.LogError("PersistInlineModelsSilently", ex);
            return existing.ProviderId;
        }
    }

    private static string MakePingKey(string pid, string proto, string modelId)
        => $"{pid}|{proto}|{modelId}";

    private static string FormatTokenBadge(int tokens)
    {
        if (tokens >= 1_000_000 && tokens % 1_000_000 == 0)
            return $"{tokens / 1_000_000}M";
        if (tokens >= 1_000)
            return $"{tokens / 1_000}K";
        return tokens.ToString();
    }

    private static void AddCompactModelBadge(WrapPanel row, string text, Color accent)
    {
        var b = DshWidgets.Badge(text, accent);
        b.Margin = new Thickness(0, 0, 5, 2);
        row.Children.Add(b);
    }

    private static Border MakeCustomCapsuleSwitch(bool isOn, string tooltip, Action onToggle)
    {
        var thumb = new Border
        {
            Width = 14,
            Height = 14,
            CornerRadius = new CornerRadius(7),
            Background = Brushes.White,
            HorizontalAlignment = isOn ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Margin = new Thickness(2, 0, 2, 0),
        };
        var track = new Border
        {
            Width = 34,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = tooltip,
            Child = thumb,
        };
        if (isOn)
            track.Background = new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x59));
        else
            DshWidgets.BindToken(track, Border.BackgroundProperty, "Brush.Surface.SwitchOff", Color.FromRgb(0x3E, 0x45, 0x52));

        track.MouseLeftButtonDown += (_, ev) =>
        {
            ev.Handled = true;
            onToggle();
        };
        return track;
    }
}
