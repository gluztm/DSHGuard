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
    private string? _credUpdatingKey;

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

    internal void OpenCredentialImportForTest(bool open, string? channel = null)
    {
        _credImportOpen = open;
        if (!open) ClearTransientCredentialImportState();
        else if (!string.IsNullOrWhiteSpace(channel)) _credImportChannel = channel!;
        if (open) _credFormOpen = false;
        ShowView(GuardView.Credentials);   // ShowView 内部惰性刷新（RefreshCredentialsView）
    }

    internal void ShowCredentialsTabForTest(string tab)
    {
        _credActiveTab = tab == "keys" ? "keys" : "providers";
        ShowView(GuardView.Credentials);
        RenderCredentialsView();
    }

    internal int TransientImportCandidateCountForTest => _credManualCandidates.Count;
    internal bool IsCredentialImportOpenForTest => _credImportOpen;
    internal void LoadCredentialFileForPreviewForTest(string filePath) => LoadCredentialFileForPreview(filePath);

    /// <summary>清空导入流程在内存中的全部瞬态明文状态，防止明文滞留。</summary>
    internal void ClearTransientCredentialImportState()
    {
        _credClipboardText = "";
        _credDroppedFileName = "";
        _credManualCandidates.Clear();
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
                ? "尚未收纳凭据"
                : $"{providers.Count} 提供商 · {creds.Entries.Count} 凭据（已脱敏）";
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
        var (searchHost, searchBox) = DshWidgets.SearchBox("按提供商、环境变量或地址筛选…", double.NaN, _credSearch);
        searchHost.HorizontalAlignment = HorizontalAlignment.Stretch;
        searchHost.VerticalAlignment = VerticalAlignment.Center;
        searchHost.MinWidth = 120;
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

        var importBtn = DshWidgets.ToolbarButton("⚡ 一键导入", "primary");
        importBtn.Padding = compactPad;
        importBtn.Margin = btnGap;
        importBtn.ToolTip = "拖入装着 URL 与 API Key 的文件，或一键读取其他 Agent 的非订阅明文凭据（零缓存直接写官方表单）。";
        importBtn.Click += (_, _) =>
        {
            _credImportOpen = !_credImportOpen;
            if (_credImportOpen) { _credFormOpen = false; _credUpdatingKey = null; }
            else ClearTransientCredentialImportState();
            RenderCredentialsView();
            if (_credImportOpen) ScrollCredentialsToTop();
        };
        Grid.SetColumn(importBtn, 1);
        row.Children.Add(importBtn);
        _credImportToggleBtn = importBtn;

        var addBtn = DshWidgets.ToolbarButton("＋ 添加提供商");
        addBtn.Padding = compactPad;
        addBtn.Margin = btnGap;
        addBtn.ToolTip = "使用 8 套内置平台预设一键填充，或手工添加提供商 / API Key。";
        addBtn.Click += (_, _) =>
        {
            _credFormOpen = !_credFormOpen;
            if (_credFormOpen) { _credImportOpen = false; ClearTransientCredentialImportState(); _credUpdatingKey = null; }
            else _credEditingProvider = null;
            RenderCredentialsView();
            if (_credFormOpen) ScrollCredentialsToTop();
        };
        Grid.SetColumn(addBtn, 2);
        row.Children.Add(addBtn);
        _credAddToggleBtn = addBtn;

        var openDir = DshWidgets.ToolbarButton("打开目录");
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
                _credImportToggleBtn.Content = _credImportOpen ? "收起导入" : "⚡ 一键导入";
            if (_credAddToggleBtn != null)
                _credAddToggleBtn.Content = _credFormOpen
                    ? (_credEditingProvider != null ? "收起编辑" : "收起向导")
                    : "＋ 添加提供商";
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

            // 3. 快捷内联更新某条环境变量密钥
            if (!string.IsNullOrEmpty(_credUpdatingKey))
                panel.Children.Add(BuildQuickUpdateKeyPanel(_credUpdatingKey!));

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
                    var quickImport = DshWidgets.SmallButton("⚡ 一键导入", "primary");
                    quickImport.Click += (_, _) =>
                    {
                        _credImportOpen = true;
                        _credImportChannel = "file";
                        _credFormOpen = false;
                        RenderCredentialsView();
                    };
                    panel.Children.Add(DshWidgets.EmptyState(
                        "尚未收纳环境变量 API 密钥",
                        "点上方「⚡ 一键导入」或「＋ 添加提供商」录入密钥，凭据将直接写入官方 .credentials.yaml。",
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
        _credUpdatingKey = null;
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
                    var draft = new DshProviderDraft
                    {
                        ProviderId = c.ProviderId,
                        DisplayName = c.DisplayName,
                        ApiKeyEnv = c.EnvKey,
                        Api = c.Api,
                        BaseUrl = c.BaseUrl,
                        Models = c.Models,
                    };
                    if (syncWeb && webProfile != null)
                    {
                        if (DshCredentialManager.ApplyProviderToProfile(webProfile, draft).Ok) provOk++;
                    }
                    if (syncDesktop && deskProfile != null)
                    {
                        if (DshCredentialManager.ApplyProviderToProfile(deskProfile, draft).Ok) provOk++;
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
        var protoCombo = DshWidgets.Combo("openai-completions", "anthropic-messages", "google-generative-ai");
        var (urlHost, urlBox) = DshWidgets.Input("Base URL（仅填存储凭据可留空，如 https://api.deepseek.com/v1）", double.NaN);
        var (keyHost, keyBox) = DshWidgets.Input(editing != null ? "API Key（留空表示保留已有密钥；填写将覆盖更新）" : "API Key（sk-... 安全写入 .credentials.yaml）", double.NaN);
        var (modelsHost, modelsBox) = DshWidgets.Input("模型 ID 列表（逗号分隔，可点击右侧探测自动填充）", double.NaN);

        if (editing != null)
        {
            nameBox.Text = editing.DisplayName;
            idBox.Text = editing.ProviderId;
            envBox.Text = editing.ApiKeyEnv;
            urlBox.Text = editing.BaseUrl;
            modelsBox.Text = string.Join(", ", editing.Models);
            for (int i = 0; i < protoCombo.Items.Count; i++)
            {
                if (string.Equals(protoCombo.Items[i]?.ToString(), editing.Api, StringComparison.OrdinalIgnoreCase))
                {
                    protoCombo.SelectedIndex = i;
                    break;
                }
            }
        }

        foreach (var preset in DshCredentialManager.BuiltInPresets)
        {
            var pill = DshWidgets.SmallButton(preset.Label);
            pill.Margin = new Thickness(0, 0, 6, 6);
            pill.ToolTip = preset.Note;
            var captured = preset;
            pill.Click += (_, _) =>
            {
                nameBox.Text = captured.DisplayName;
                idBox.Text = captured.ProviderId;
                envBox.Text = captured.EnvKey;
                urlBox.Text = captured.BaseUrl;
                modelsBox.Text = string.Join(", ", captured.Models);
                for (int i = 0; i < protoCombo.Items.Count; i++)
                {
                    if (string.Equals(protoCombo.Items[i]?.ToString(), captured.Api, StringComparison.OrdinalIgnoreCase))
                    {
                        protoCombo.SelectedIndex = i;
                        break;
                    }
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

        // 模型输入框 + 在线探测按钮
        var modelRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        modelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        modelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(modelsHost, 0);
        modelRow.Children.Add(modelsHost);

        var probeBtn = DshWidgets.SmallButton("🔍 探测 /models");
        probeBtn.Margin = new Thickness(6, 0, 0, 0);
        probeBtn.ToolTip = "向当前 Base URL 发起 /models 请求并自动填入可用模型 ID（6 秒超时）";
        probeBtn.Click += async (_, _) =>
        {
            string url = urlBox.Text.Trim();
            if (url.Length == 0)
            {
                ShowCredToast("请先填写 Base URL");
                return;
            }
            string? key = keyBox.Text.Trim();
            if (key.Length == 0 && !string.IsNullOrWhiteSpace(envBox.Text))
                key = DshCredentialManager.TryReadRawSecret(envBox.Text.Trim());

            probeBtn.IsEnabled = false;
            probeBtn.Content = "探测中…";
            try
            {
                var (ok, msg, list) = await DshCredentialManager.ProbeEndpointModelsAsync(url, key);
                if (ok && list.Count > 0)
                    modelsBox.Text = string.Join(", ", list.Take(12));
                ShowCredToast(msg);
            }
            finally
            {
                probeBtn.IsEnabled = true;
                probeBtn.Content = "🔍 探测 /models";
            }
        };
        Grid.SetColumn(probeBtn, 1);
        modelRow.Children.Add(probeBtn);
        box.Children.Add(modelRow);

        var targetsRow = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 8) };
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
                modelsBox.Text,
                webCb.IsChecked == true,
                deskCb.IsChecked == true);
        };
        cancelBtn.Click += (_, _) =>
        {
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
        string rawModels,
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
                var models = rawModels.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .Where(s => s.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                var draft = new DshProviderDraft
                {
                    ProviderId = pid,
                    DisplayName = string.IsNullOrWhiteSpace(rawDisplay) ? pid : rawDisplay.Trim(),
                    ApiKeyEnv = envKey,
                    Api = api,
                    BaseUrl = url,
                    Models = models,
                };

                var profiles = DshCredentialManager.ResolveProfiles();
                if (writeWeb)
                {
                    var wp = profiles.FirstOrDefault(p => p.Kind == DshProfileKind.Web);
                    if (wp != null)
                    {
                        var r = DshCredentialManager.ApplyProviderToProfile(wp, draft);
                        notes.Add(r.Ok ? "已同步 Web 版" : r.Message);
                    }
                }
                if (writeDesktop)
                {
                    var dp = profiles.FirstOrDefault(p => p.Kind == DshProfileKind.Desktop);
                    if (dp != null)
                    {
                        var r = DshCredentialManager.ApplyProviderToProfile(dp, draft);
                        notes.Add(r.Ok ? "已同步桌面版" : r.Message);
                    }
                }
            }
            else if (string.IsNullOrWhiteSpace(rawKey))
            {
                ShowCredToast("请填写 API Key，或填写完整的 Base URL 以保存模型提供商。");
                return;
            }

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

    private UIElement BuildQuickUpdateKeyPanel(string envKey)
    {
        var box = new StackPanel();
        box.Children.Add(DshWidgets.Text($"更新凭据密钥：{envKey}", 12.5, DshTextPrimary, FontWeights.SemiBold));
        var hint = DshWidgets.Hint("直接更新官方 ~/.dsh/.credentials.yaml（守护壳不留缓存或 .bak-cred 副本）；保存后界面仅显示脱敏摘要。", 10.5);
        hint.Margin = new Thickness(0, 2, 0, 6);
        box.Children.Add(hint);

        var (keyHost, keyBox) = DshWidgets.Input("输入新的 API Key（sk-...）", double.NaN);
        keyHost.Margin = new Thickness(0, 0, 0, 8);
        box.Children.Add(keyHost);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var save = DshWidgets.SmallButton("写入新密钥", "primary");
        var cancel = DshWidgets.SmallButton("取消");
        save.Click += (_, _) =>
        {
            var r = DshCredentialManager.UpsertCredentialRef(envKey, keyBox.Text);
            if (!r.Ok) { ShowCredToast(r.Message); return; }
            _credUpdatingKey = null;
            InvalidateDshScans();
            ShowCredToast(r.Message);
            RenderCredentialsView();
        };
        cancel.Click += (_, _) => { _credUpdatingKey = null; RenderCredentialsView(); };
        row.Children.Add(save);
        row.Children.Add(cancel);
        box.Children.Add(row);

        return DshWidgets.Panel(box);
    }

    // ══════════════ 模型提供商卡片（跨端切换 + 收藏 + 编辑/移除）══════════════

    private UIElement BuildProviderCard(DshLlmProvider p)
    {
        string favKey = DshFavorites.ProviderKey(p.ProviderId);
        bool starred = DshFavorites.IsStarred(favKey);
        string activeProfileId = _ctx.Target == GuardTarget.Desktop ? "desktop" : "web";
        bool activeOnCurrent = p.EnabledIn(activeProfileId);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var avatar = BuildProviderAvatar(p);
        Grid.SetColumn(avatar, 0);
        grid.Children.Add(avatar);

        var center = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };

        var line1 = new Grid();
        line1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var starBtn = BuildDshStarButton(favKey, "模型提供商", AfterCredStarToggle);
        if (starBtn is FrameworkElement starFe)
        {
            starFe.VerticalAlignment = VerticalAlignment.Center;
            starFe.Margin = new Thickness(0, 0, 5, 0);
        }
        Grid.SetColumn(starBtn, 0);
        line1.Children.Add(starBtn);

        var title = DshWidgets.Text(p.DisplayName, 13.5, DshTextPrimary, FontWeights.SemiBold);
        title.VerticalAlignment = VerticalAlignment.Center;
        title.TextWrapping = TextWrapping.NoWrap;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.MaxWidth = 170;
        title.Margin = new Thickness(0, 0, 7, 0);
        Grid.SetColumn(title, 1);
        line1.Children.Add(title);

        var badges = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        string protoLabel = p.Api switch
        {
            "anthropic-messages" => "anthropic",
            "google-generative-ai" => "gemini",
            _ => "openai",
        };
        var protoBadge = DshWidgets.Badge(protoLabel, DshAccent);
        protoBadge.VerticalAlignment = VerticalAlignment.Center;
        protoBadge.Margin = new Thickness(0, 0, 5, 0);
        badges.Children.Add(protoBadge);

        var statusBadge = !p.CredentialReady
            ? DshWidgets.Badge($"缺 {p.ApiKeyEnv}", DshWarn)
            : DshWidgets.Badge(activeOnCurrent ? "● 使用中" : "就绪", DshOk);
        statusBadge.VerticalAlignment = VerticalAlignment.Center;
        badges.Children.Add(statusBadge);

        Grid.SetColumn(badges, 2);
        line1.Children.Add(badges);
        center.Children.Add(line1);

        string urlText = string.IsNullOrWhiteSpace(p.BaseUrl) ? p.ProviderId : p.BaseUrl;
        var subLine = DshWidgets.Hint($"{urlText} · {p.Models.Count} 模型", 11);
        subLine.TextWrapping = TextWrapping.NoWrap;
        subLine.TextTrimming = TextTrimming.CharacterEllipsis;
        subLine.Margin = new Thickness(0, 4, 0, 0);
        center.Children.Add(subLine);

        Grid.SetColumn(center, 1);
        grid.Children.Add(center);

        var right = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var chipsRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 0, 5),
        };
        var profiles = DshCredentialManager.ResolveProfiles();
        foreach (var prof in profiles.Where(x => x.Kind is DshProfileKind.Web or DshProfileKind.Desktop
                                              || (x.Kind == DshProfileKind.Headless && File.Exists(x.PatchFile()))))
        {
            bool enabled = p.EnabledIn(prof.Id);
            var chip = BuildProfileToggleChip(prof.DisplayName, enabled);
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
            chipsRow.Children.Add(chip);
        }
        right.Children.Add(chipsRow);

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var editBtn = DshWidgets.ToolbarButton("编辑");
        editBtn.Height = 24;
        editBtn.MinHeight = 24;
        editBtn.FontSize = 11;
        editBtn.Padding = new Thickness(10, 0, 10, 0);
        editBtn.Margin = new Thickness(0);
        editBtn.Click += (_, _) =>
        {
            _credEditingProvider = p;
            _credFormOpen = true;
            _credImportOpen = false;
            _credUpdatingKey = null;
            RenderCredentialsView();
            ScrollCredentialsToTop();
        };
        btnRow.Children.Add(editBtn);

        if (!starred)
        {
            var delBtn = DshWidgets.ToolbarButton("移除", "danger");
            delBtn.Height = 24;
            delBtn.MinHeight = 24;
            delBtn.FontSize = 11;
            delBtn.Padding = new Thickness(10, 0, 10, 0);
            delBtn.Margin = new Thickness(6, 0, 0, 0);
            delBtn.Click += (_, _) => RemoveProviderAll(p);
            btnRow.Children.Add(delBtn);
        }
        else
        {
            var locked = DshWidgets.Hint("★ 已收藏", 10.5);
            locked.VerticalAlignment = VerticalAlignment.Center;
            locked.Margin = new Thickness(8, 0, 0, 0);
            locked.ToolTip = "★ 已收藏（取消星标后可移除）";
            btnRow.Children.Add(locked);
        }

        right.Children.Add(btnRow);
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        var card = DshWidgets.Card(grid, new Thickness(0, 0, 0, 8));
        card.Padding = new Thickness(14, 11, 14, 11);
        if (activeOnCurrent)
            card.BorderBrush = new SolidColorBrush(Color.FromArgb(0x88, 0x00, 0x7A, 0xFF));

        string modelTip = p.Models.Count > 0 ? string.Join("、", p.Models.Take(10)) + (p.Models.Count > 10 ? $" 等 {p.Models.Count} 个" : "") : "默认";
        card.ToolTip = $"id: {p.ProviderId}\nenv: {p.ApiKeyEnv} ({p.CredentialMasked})\napi: {p.Api}\nurl: {p.BaseUrl}\n模型: {modelTip}";
        return card;
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
                };
                res = DshCredentialManager.ApplyProviderToProfile(profile, draft);
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
            InvalidateDshScans();
            ShowCredToast(msgs.Count > 0 ? $"已从 {string.Join("、", msgs)} 移除 {p.DisplayName}" : "未发现已启用的配置项");
            RenderCredentialsView();
        }
        finally
        {
            ExitDshBusy(refresh: false);
        }
    }

    // ══════════════ 凭据保险箱条目卡片（严格脱敏 + 账号态只读保护）══════════════

    private UIElement BuildCredentialRowCard(DshCredentialEntry e, List<string>? linkedProviders)
    {
        string favKey = DshFavorites.CredentialKey(e.Key);
        bool starred = DshFavorites.IsStarred(favKey);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

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
        grid.Children.Add(iconBox);

        var center = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (e.IsReadOnlyAccount)
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var starBtn = BuildDshStarButton(favKey, "凭据条目", AfterCredStarToggle);
        if (starBtn is FrameworkElement starFe)
        {
            starFe.VerticalAlignment = VerticalAlignment.Center;
            starFe.Margin = new Thickness(0, 0, 5, 0);
        }
        Grid.SetColumn(starBtn, 0);
        top.Children.Add(starBtn);

        var keyText = DshWidgets.Text(e.Key, 13, DshTextPrimary, FontWeights.SemiBold);
        keyText.VerticalAlignment = VerticalAlignment.Center;
        keyText.TextWrapping = TextWrapping.NoWrap;
        keyText.TextTrimming = TextTrimming.CharacterEllipsis;
        keyText.ToolTip = e.Key;
        Grid.SetColumn(keyText, 1);
        top.Children.Add(keyText);

        if (e.IsReadOnlyAccount)
        {
            var roBadge = DshWidgets.Badge("只读保护", DshAccent);
            roBadge.VerticalAlignment = VerticalAlignment.Center;
            roBadge.Margin = new Thickness(7, 0, 0, 0);
            Grid.SetColumn(roBadge, 2);
            top.Children.Add(roBadge);
        }
        center.Children.Add(top);

        var line2 = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        line2.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var maskedBadge = DshWidgets.Badge(e.MaskedValue, tone);
        maskedBadge.VerticalAlignment = VerticalAlignment.Center;
        maskedBadge.Margin = new Thickness(0, 0, 7, 0);
        Grid.SetColumn(maskedBadge, 0);
        line2.Children.Add(maskedBadge);

        string subLine = e.IsReadOnlyAccount
            ? $"占 {e.LineCount} 行（已锁定防误改）"
            : (linkedProviders is { Count: > 0 }
                ? $"关联：{string.Join("、", linkedProviders)}"
                : "通用 API 密钥 / 插件变量");
        var sub = DshWidgets.Hint(subLine, 11);
        sub.TextWrapping = TextWrapping.NoWrap;
        sub.TextTrimming = TextTrimming.CharacterEllipsis;
        sub.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(sub, 1);
        line2.Children.Add(sub);
        center.Children.Add(line2);

        Grid.SetColumn(center, 1);
        grid.Children.Add(center);

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        if (!e.IsReadOnlyAccount)
        {
            var copyEnvBtn = DshWidgets.ToolbarButton("复制键名");
            copyEnvBtn.Height = 24;
            copyEnvBtn.MinHeight = 24;
            copyEnvBtn.FontSize = 11;
            copyEnvBtn.Padding = new Thickness(9, 0, 9, 0);
            copyEnvBtn.Margin = new Thickness(0);
            copyEnvBtn.ToolTip = "仅复制环境变量键名（如 DEEPSEEK_API_KEY），不复制明文密钥";
            copyEnvBtn.Click += (_, _) =>
            {
                try
                {
                    Clipboard.SetText(e.Key);
                    ShowCredToast("已复制环境变量名：" + e.Key);
                }
                catch (Exception ex) { ShowCredToast("复制失败：" + ex.Message); }
            };
            right.Children.Add(copyEnvBtn);

            var updateBtn = DshWidgets.ToolbarButton("更新");
            updateBtn.Height = 24;
            updateBtn.MinHeight = 24;
            updateBtn.FontSize = 11;
            updateBtn.Padding = new Thickness(9, 0, 9, 0);
            updateBtn.Margin = new Thickness(6, 0, 0, 0);
            updateBtn.Click += (_, _) =>
            {
                _credUpdatingKey = e.Key;
                _credFormOpen = false;
                _credImportOpen = false;
                RenderCredentialsView();
                ScrollCredentialsToTop();
            };
            right.Children.Add(updateBtn);

            if (!starred)
            {
                var delBtn = DshWidgets.ToolbarButton("删除", "danger");
                delBtn.Height = 24;
                delBtn.MinHeight = 24;
                delBtn.FontSize = 11;
                delBtn.Padding = new Thickness(9, 0, 9, 0);
                delBtn.Margin = new Thickness(6, 0, 0, 0);
                delBtn.Click += (_, _) => DeleteCredentialEntry(e.Key);
                right.Children.Add(delBtn);
            }
            else
            {
                var locked = DshWidgets.Hint("★ 已收藏", 10.5);
                locked.VerticalAlignment = VerticalAlignment.Center;
                locked.Margin = new Thickness(8, 0, 0, 0);
                locked.ToolTip = "★ 已收藏（取消星标后可删）";
                right.Children.Add(locked);
            }
        }
        else
        {
            var lockedHint = DshWidgets.Hint("已锁定", 10.5);
            lockedHint.VerticalAlignment = VerticalAlignment.Center;
            lockedHint.ToolTip = "账号态 JSON 令牌受只读保护，守护壳禁止改写或删除。";
            right.Children.Add(lockedHint);
        }

        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        var card = DshWidgets.Card(grid, new Thickness(0, 0, 0, 8), dim: e.IsReadOnlyAccount);
        card.Padding = new Thickness(14, 10, 14, 10);
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
        var cb = new CheckBox
        {
            Content = text,
            IsChecked = isChecked,
            FontSize = 11.5,
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
}
