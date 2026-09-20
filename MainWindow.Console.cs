using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;      // 导出诊断包用（ZipFile / CreateEntryFromFile 扩展方法在本命名空间）
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DSHGuard;

/// <summary>控制台视图：实时日志与本程序自带的快照浏览、选择性回滚；MainWindow 的 partial 部分类。</summary>
public partial class MainWindow : Window
{
    // ═══ 视图状态（状态 / 日志 / 快照 / 设置 各自独立视图，窗口尺寸不变） ═══
    private readonly List<string> _logLines = new();
    private const int MaxLogLines = 3000;
    private List<SnapshotManager.Snapshot> _snapshots = new();
    private SnapshotManager.Snapshot? _selectedSnapshot;
    private bool _viewInited;
    private GuardView _currentView = GuardView.Status;
    /// <summary>设置页当前选中的二级标签（常规 / 路径 / 版本）。</summary>
    private SettingsTab _settingsTab = SettingsTab.General;
    /// <summary>当前高亮的左侧导航项（鼠标离开时用于判断是否恢复高亮）。</summary>
    private Border? _currentNavBorder;
    private static readonly Color NavSelectedColor = Color.FromRgb(0x2B, 0x6B, 0xFF);

    /// <summary>
    /// 快照详情页"名称列"的**两行上限**（设备无关像素）：<c>MakeFilePanel</c> / <c>MakeGroupPanel</c> 共用。
    /// 11.5 字号一行约 15.3px ⇒ 32px 装得下两行、装不下三行，超长名称由
    /// <see cref="TextTrimming.CharacterEllipsis"/> 收尾。
    /// 取名常量而不是各处写 32：两处必须同一个上限，否则文件行与聚合行的行高会不一致。
    /// </summary>
    private const double NameTextMaxHeight = 32;

    /// <summary>
    /// 快照详情页里**每一个勾选行**的垂直 Margin（上下同值）：逐文件行 / 聚合行 / 两个开关行共用这一个数。
    ///
    /// 为什么要有它：以前聚合行写 (0,4,0,2)、逐文件行写 (0,2,0,2) ⇒ 同一列里"上间距"在 2px 与 4px 之间跳，
    ///   用户 1.3.57 实机看了说"行间距看着有点乱"。取 2 的理由：逐文件行与开关行本来就是 2（已上屏接受），
    ///   只把聚合行那多出来的 2px 收回 ⇒ 改动最小、整列节奏统一成 2px，不牵动别的版式。
    /// </summary>
    private const double CheckRowMarginV = 2;

    public enum GuardView { Status, Logs, Snapshots, Plugins, Settings, About }

    /// <summary>设置页的二级标签（版本页已并入设置，不再有独立的「版本详情」视图）。</summary>
    private enum SettingsTab { General, Paths, Version }

    /// <summary>切换中栏视图：更新各视图可见性与左侧导航高亮，窗口尺寸保持不变。</summary>
    private void ShowView(GuardView view)
    {
        try
        {
            _currentView = view;

            StatusView.Visibility = view == GuardView.Status ? Visibility.Visible : Visibility.Collapsed;
            LogsView.Visibility = view == GuardView.Logs ? Visibility.Visible : Visibility.Collapsed;
            SnapshotsView.Visibility = view == GuardView.Snapshots ? Visibility.Visible : Visibility.Collapsed;
            PluginsView.Visibility = view == GuardView.Plugins ? Visibility.Visible : Visibility.Collapsed;
            SettingsView.Visibility = view == GuardView.Settings ? Visibility.Visible : Visibility.Collapsed;
            AboutView.Visibility = view == GuardView.About ? Visibility.Visible : Visibility.Collapsed;

            Border target = view switch
            {
                GuardView.Status => NavStatus,
                GuardView.Logs => NavLogs,
                GuardView.Snapshots => NavSnapshots,
                GuardView.Plugins => NavPlugins,
                GuardView.About => NavAbout,
                _ => NavSettings
            };

            foreach (var nav in new[] { NavStatus, NavLogs, NavSnapshots, NavPlugins, NavSettings, NavAbout })
            {
                if (nav == null) continue;
                nav.Background = ReferenceEquals(nav, target)
                    ? new SolidColorBrush(NavSelectedColor)
                    : Brushes.Transparent;
            }
            _currentNavBorder = target;

            if (!_viewInited)
            {
                _viewInited = true;
                Logger.LogFilesChanged += RefreshLogFilePicker;
            }

            switch (view)
            {
                case GuardView.Logs:
                    RefreshLogFilePicker();
                    RefreshLogBox();
                    RefreshLogPreview();
                    break;
                case GuardView.Snapshots:
                    RefreshSnapshots();
                    break;
                case GuardView.Plugins:
                    _ = RefreshPluginsAsync();
                    break;
                case GuardView.Settings:
                    RefreshSettingsView();
                    break;
                case GuardView.About:
                    RenderAbout();
                    break;
                case GuardView.Status:
                    UpdateUI();
                    RefreshStatusEvents();
                    RefreshEnvInfo();
                    RefreshLogPreview();
                    break;
            }

            // 换页时把筛选弹层收起（它是独立顶层窗口，会浮在新页面上）
            CloseFilterPopups();

            // 视图淡入（按压反馈由 ButtonFx 统一负责，避免两套缩放动画互相覆盖）
            UIElement shown = view switch
            {
                GuardView.Status => StatusView,
                GuardView.Logs => LogsView,
                GuardView.Snapshots => SnapshotsView,
                GuardView.Plugins => PluginsView,
                GuardView.About => AboutView,
                _ => SettingsView
            };
            FadeInView(shown);
            ApplyThemeSoon();   // 新显示的页面需重新应用当前主题（日间模式下尤为明显）
        }
        catch (Exception ex) { Logger.LogError("ShowView", ex); }
    }

    // ═══ 日志视图 ═══
    private const string LiveLogItem = "● 本次会话（实时）";
    /// <summary>下拉项显示文案 -> 真实文件路径（下拉中带前缀，取文件时需还原为原始文件名）。</summary>
    private readonly Dictionary<string, string> _logPickMap = new();

    private void RefreshLogFilePicker()
    {
        try
        {
            var items = new List<string> { LiveLogItem };
            _logPickMap.Clear();
            _logPickMap[LiveLogItem] = "";

            // 异常日志 + 启动诊断日志都列出来（启动日志专门记录每次启动的现场，内测反馈据此定位）
            foreach (var f in Logger.ListAllLogs())
            {
                string name = Path.GetFileName(f) ?? "";
                items.Add(name);
                _logPickMap[name] = f;
            }

            LogFilePicker.ItemsSource = items;
            if (LogFilePicker.SelectedIndex < 0) LogFilePicker.SelectedIndex = 0; // 默认「本次会话」
        }
        catch (Exception ex) { Logger.LogError("RefreshLogFilePicker", ex); }
    }

    /// <summary>将一条 DSH 输出追加到实时日志面板（线程安全）。</summary>
    private void AppendLiveLog(string line)
    {
        try
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => AppendLiveLog(line))); return; }
            _logLines.Add(line);
            if (LogBox == null) return; // XAML 尚未初始化（极早期输出）
            if (_logLines.Count > MaxLogLines) _logLines.RemoveRange(0, _logLines.Count - MaxLogLines);
            if (LogsView.Visibility == Visibility.Visible && LogFilePicker.SelectedIndex <= 0)
                LogBox.Text = string.Join(Environment.NewLine, _logLines);
            RefreshLogPreview();
        }
        catch { }
    }

    /// <summary>当前选中日志来源对应的文件路径（选「本次会话」时返回本次日志文件，可能为空）。</summary>
    private string CurrentSelectedLogPath()
    {
        string? pick = LogFilePicker.SelectedItem as string;
        if (string.IsNullOrEmpty(pick) || pick == LiveLogItem) return Logger.CurrentLogFile;
        return Path.Combine(Logger.OpenLogFolderPath, pick);
    }

    private void RefreshLogBox()
    {
        try
        {
            string? pick = LogFilePicker.SelectedItem as string;
            bool live = string.IsNullOrEmpty(pick) || pick == LiveLogItem;

            if (live)
            {
                LogPathText.Text = "本次会话（内存实时输出）";
                LogBox.Text = _logLines.Count == 0
                    ? "（暂无输出。点右侧「一键启动引擎」后，DSH 的运行输出会实时显示在这里）"
                    : string.Join(Environment.NewLine, _logLines);
            }
            else
            {
                string path = _logPickMap.TryGetValue(pick!, out var full) && full.Length > 0
                    ? full
                    : Path.Combine(Logger.OpenLogFolderPath, pick!);
                LogPathText.Text = Path.GetFileName(path);
                LogBox.Text = Logger.ReadLogFile(path);
            }
            LogBox.ScrollToEnd();
        }
        catch (Exception ex) { Logger.LogError("RefreshLogBox", ex); }
    }

    // ═══ 复制日志（一键把当前显示的日志内容复制到剪贴板；不再做分析） ═══
    private void CopyLog_Click(object sender, MouseButtonEventArgs e) => CopyLogToClipboard();

    /// <summary>把日志框里当前显示的内容复制到剪贴板（所见即所得）。</summary>
    private void CopyLogToClipboard()
    {
        try
        {
            string text = LogBox?.Text ?? "";
            if (string.IsNullOrWhiteSpace(text))
            {
                ConsoleStatusText.Text = "没有可复制的内容";
                return;
            }

            // 剪贴板偶发被其他程序占用，重试几次再报错
            Exception? last = null;
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    Clipboard.SetText(text);
                    last = null;
                    break;
                }
                catch (Exception ex)
                {
                    last = ex;
                    System.Threading.Thread.Sleep(120);
                }
            }

            if (last != null)
            {
                Logger.LogError("CopyLog", last);
                ConsoleStatusText.Text = "复制失败（剪贴板被其他程序占用，稍后再试）";
                return;
            }

            int lines = text.Count(c => c == '\n') + 1;
            double kb = text.Length / 1024.0;
            ConsoleStatusText.Text = $"已复制 {lines} 行（{kb:0.#} KB）到剪贴板";
            AddEvent($"已复制日志内容（{lines} 行）");
        }
        catch (Exception ex)
        {
            Logger.LogError("CopyLogToClipboard", ex);
            ConsoleStatusText.Text = "复制失败（剪贴板暂时不可用，稍后再试）";
        }
    }

    /// <summary>自检用：往日志框里放一段文本 / 触发一次复制。</summary>
    internal void SetLogBoxForTest(string text)
    {
        if (LogBox != null) LogBox.Text = text;
    }

    internal void CopyLogForTest() => CopyLogToClipboard();

    private void RefreshLog_Click(object sender, MouseButtonEventArgs e)
    {
        RefreshLogFilePicker();
        RefreshLogBox();
        ConsoleStatusText.Text = "日志已刷新: " + Logger.CurrentLogFile;
    }

    /// <summary>
    /// 诊断摘要（给"导出诊断"用；拼装是纯的，自检可直接断言关键字段）。
    /// 面向排查：版本、端口、命令、各目录、运行环境、最近一次启动的引擎输出尾部。
    /// </summary>
    internal string BuildDiagnosticsSummary()
    {
        var sb = new StringBuilder();
        sb.AppendLine("DSH 守护壳 诊断摘要");
        sb.AppendLine("生成时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("守护壳版本: " + GuardVersion.Version + "（批次 " + GuardVersion.Batch + "）");
        sb.AppendLine("引擎端口: " + _port);
        sb.AppendLine("启动命令: " + ProcessManager.BuildArgs(_port, _settings.LaunchCommand));
        sb.AppendLine("工作目录: " + ProcessManager.WorkDir);
        sb.AppendLine("配置文件目录: " + _settings.PathProfile);
        sb.AppendLine("日志目录: " + Logger.OpenLogFolderPath);
        sb.AppendLine("快照目录: " + _settings.PathSnapshots);
        sb.AppendLine("缓存目录: " + GuardPaths.CacheDir);
        sb.AppendLine("DSH 版本策略: " + VersionMemory.PolicyText + " → " + VersionMemory.Spec);
        sb.AppendLine("引擎状态: " + (_isRunning ? "运行中" : "未运行") + (_engineExternal ? "（外部启动）" : ""));
        sb.AppendLine("系统: " + Environment.OSVersion.VersionString
                      + " / " + (Environment.Is64BitOperatingSystem ? "64 位" : "32 位")
                      + " / .NET " + Environment.Version);
        sb.AppendLine("引擎追踪进程: " + (_processManager?.TrackedPid ?? 0));
        sb.AppendLine();
        sb.AppendLine("最近一次启动的引擎输出尾部:");
        sb.AppendLine(Indent(Logger.RunOutputTail(40)));
        return sb.ToString();
    }

    /// <summary>
    /// 诊断包输出目录：优先用「设置 -> 路径 -> 诊断输出」，留空则落在程序目录的 Logs 里
    /// （与日志同处一地，用户找得到；不再默认丢桌面）。
    /// </summary>
    internal string DiagnosticsDir()
    {
        try
        {
            string custom = (_settings.PathDiagnostics ?? "").Trim();
            if (custom.Length > 0) return custom;
        }
        catch { }
        return Logger.OpenLogFolderPath;
    }

    /// <summary>
    /// 「导出诊断」：把日志 + 设置 + 运行环境摘要打成一个压缩包（默认程序目录的 Logs），
    /// 出问题时把这个包发回来即可定位。
    /// </summary>
    private void ExportDiagnostics_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            string dir = DiagnosticsDir();
            Directory.CreateDirectory(dir);
            string zip = Path.Combine(dir, $"DSHGuard-诊断-{DateTime.Now:yyyyMMdd-HHmmss}.zip");

            using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                void AddText(string name, string text)
                {
                    var entry = z.CreateEntry(name, CompressionLevel.Optimal);
                    using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                    w.Write(text);
                }
                AddText("摘要.txt", BuildDiagnosticsSummary());

                foreach (var f in Logger.ListAllLogs())
                {
                    string name = Path.GetFileName(f);
                    try
                    {
                        // 用 ReadLogFile 读进来再加：日志文件正被本进程写句柄占用，
                        // CreateEntryFromFile 会因共享模式不匹配而失败——最新那个日志就是这样丢失的（现场 bug）。
                        AddText("日志/" + name, Logger.ReadLogFile(f, maxLines: 20000));
                    }
                    catch { }
                }
                foreach (string name in new[] { "settings.json", "versions.json" })
                {
                    string src = Path.Combine(GuardPaths.ConfigDir, name);
                    try
                    {
                        if (File.Exists(src)) AddText("配置/" + name, File.ReadAllText(src));
                    }
                    catch { }
                }
            }

            long size = 0;
            try { size = new FileInfo(zip).Length; } catch { }
            ConsoleStatusText.Text = "诊断已输出：" + zip;
            AddEvent("诊断已输出（" + GuardPaths.HumanSize(size) + "）");
            Logger.ShowInfo("导出诊断", "诊断已输出：\n\n" + zip);
        }
        catch (Exception ex)
        {
            Logger.LogError("ExportDiagnostics_Click", ex);
            ConsoleStatusText.Text = "导出诊断没能完成，可稍后再试（详细原因已记入日志）。";
        }
    }

    private void OpenLogFolder_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{Logger.OpenLogFolderPath}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Logger.NoteDiagnosis("打开日志目录失败：" + ex.Message);
            ConsoleStatusText.Text = "日志目录没能打开，稍后再点一次。";
        }
    }

    private void LogFilePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_viewInited) return;
        RefreshLogBox();
    }

    // ═══ 快照面板（本程序原生，不依赖插件） ═══
    private void RefreshSnapshots_Click(object sender, MouseButtonEventArgs e)
    {
        RefreshSnapshots();
        ConsoleStatusText.Text = $"已读取快照: {_snapshots.Count} 个";
    }

    /// <summary>删除一份快照。</summary>
    private void DeleteSnapshot_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not Button b || b.Tag is not SnapshotManager.Snapshot snap) return;

            var r = GuardDialog.Show(
                $"删除这份快照？\n\n{snap.LocalTime}  [{snap.KindLabel}] {snap.Reason}\n{snap.Summary}\n\n" +
                "删掉之后就找不回来了（目录：" + snap.Dir + "）。",
                "删除快照", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;

            if (SnapshotManager.Delete(snap))
            {
                AddEvent($"已删除快照（{snap.LocalTime}）", EventKind.Update);
                ConsoleStatusText.Text = "已删除快照: " + snap.LocalTime;
                _selectedSnapshot = null;
                RefreshSnapshots();
            }
            else
            {
                GuardDialog.Show("删除失败，可以到「日志」页看看原因。", "删除快照",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex) { Logger.LogError("DeleteSnapshot_Click", ex); }
    }

    /// <summary>在文件管理器里打开本程序的快照文件夹。</summary>
    private void OpenSnapshotDir_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            string dir = SnapshotManager.SnapshotRoot;
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Logger.NoteDiagnosis("打开快照文件夹失败：" + ex.Message);
            GuardDialog.Show("快照文件夹没能打开，可稍后再试；" + LogPromise("详细原因已记入日志，可在「日志」页查看。"), "快照目录",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RefreshSnapshots()
    {
        try
        {
            _snapshots = SnapshotManager.ListSnapshots();
            // 一行放不下太长：屏幕上用短版，完整口径放悬停提示
            // （标识与列表左侧那几类同源：自动-插件 / 自动-时间 / 手动；
            //   「自动-版本」已关停不再计入，历史遗留的这类快照仍按原标签正常显示）
            SnapRootText.Text = $"程序自带 · 自动-插件 {SnapshotManager.SettingsCache.AutoSnapshotKeep} 份 · 自动-时间 {SnapshotManager.TimedKeep} 份 · 手动 {SnapshotManager.ManualKeep} 份";
            SnapRootText.ToolTip = "存放位置：" + SnapshotManager.SnapshotRoot +
                                   "\n保留策略：" + SnapshotManager.RetentionText;

            var rows = new List<UIElement>();
            if (_snapshots.Count == 0)
            {
                rows.Add(new TextBlock
                {
                    Text = "暂无快照。\n右边点「保存当前快照」即可为当前配置存一份存档。",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(4)
                });
                SwapThemed(SnapshotListPanel, rows);
                SnapshotDetailPanel.Children.Clear();
                return;
            }

            foreach (var snap in _snapshots)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                // 行内容用三行固定排版：时间 / [类型] + 说明（类型列定宽，说明统一起排）/ 文件数
                var rowBox = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left };
                rowBox.Children.Add(new TextBlock
                {
                    Text = snap.LocalTime,
                    FontSize = 11.5,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Left
                });
                var line2 = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
                line2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });   // 类型列定宽 -> 说明起排一致
                line2.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var kindText = new TextBlock
                {
                    Text = $"[{snap.KindLabel}]",
                    FontSize = 11.5,
                    Foreground = Brushes.White,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                Grid.SetColumn(kindText, 0);
                line2.Children.Add(kindText);
                var reasonText = new TextBlock
                {
                    Text = snap.Reason,
                    FontSize = 11.5,
                    Foreground = Brushes.White,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                Grid.SetColumn(reasonText, 1);
                line2.Children.Add(reasonText);
                rowBox.Children.Add(line2);
                // 副标题（口径 = 右侧详情表头同一个数法，用户 1.3.58 实机要求"别写 x 个文件、要写 x 项配置"）：
                //   数的是"详情页真画出来的勾选行"，按行算而不是按文件算 —— 所以这里**不再**用
                //   Snapshot.Summary（那个数的是快照里存了几个文件，与详情表头差着聚合与隐藏两条）。
                //   版式：与详情表头同序（可回滚在前、总数在后），"项配置"只在末尾出现一次，行短不换行。
                //   ⚠ 两个开关行的状态走 SnapshotRowCounts —— 与 SelectSnapshot 逐字同一套判定；
                //     这里只算"要显示的数"，picked / DoRestore / RestorableCount 一个字节没动。
                var (rowRollbackable, rowTotal) = SnapshotRowCounts(snap);
                string rowSummary = $"可回滚 {rowRollbackable} / {rowTotal} 项配置";
                var subText = new TextBlock
                {
                    Text = rowSummary,
                    FontSize = 11.5,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    // 窄条里绝不换行：行高固定三行（见下面 MinHeight），一换行整列就错位
                    TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    // 一行放不下两个口径 ⇒ 悬停里点明"为什么与文件数不一样"，好过在窄条里塞两串数字
                    ToolTip = $"{rowSummary}（这份快照里存着 {snap.Files.Count} 个文件；"
                            + "行数是详情页真画出来的勾选项 —— 同一类文件收成一行、不参与回滚的项不出行，"
                            + $"所以与文件数不同：{snap.Files.Count} 个文件 ≠ {rowTotal} 项配置）"
                };
                rowBox.Children.Add(subText);

                var btn = new Button
                {
                    Content = rowBox,
                    Tag = snap,
                    FontSize = 11.5,
                    Foreground = Brushes.White,
                    Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(8, 6, 8, 6),
                    MinHeight = 52,                                  // 三行同高，行与行整齐
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Cursor = Cursors.Hand,
                    ToolTip = snap.Dir
                };
                RoundBtn.Apply(btn);   // 统一圆角外观
                btn.Click += SnapshotRow_Click;
                Grid.SetColumn(btn, 0);
                row.Children.Add(btn);

                // 一键回滚：直接回滚该快照的全部可回滚文件（仍有确认弹窗）
                var oneClick = new Button
                {
                    Content = "↺",
                    Tag = snap,
                    Width = 32,
                    FontSize = 15,
                    Foreground = Brushes.White,
                    Background = new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xFF)),
                    BorderThickness = new Thickness(0),
                    Margin = new Thickness(4, 0, 0, 0),
                    Cursor = Cursors.Hand,
                    ToolTip = "一键回滚这个快照（全部可回滚文件）"
                };
                RoundBtn.Apply(oneClick);   // 统一圆角外观
                oneClick.Click += OneClickRollback_Click;
                Grid.SetColumn(oneClick, 1);
                row.Children.Add(oneClick);

                rows.Add(row);
            }
            SwapThemed(SnapshotListPanel, rows);

            if (_selectedSnapshot == null || !_snapshots.Contains(_selectedSnapshot))
                SelectSnapshot(_snapshots[0]);
            else
                SelectSnapshot(_selectedSnapshot);
        }
        catch (Exception ex)
        {
            Logger.LogError("RefreshSnapshots", ex);
            ConsoleStatusText.Text = "快照列表没能读出来，点「刷新快照」重试（详细原因已记入日志）。";
        }
    }

    private void SnapshotRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is SnapshotManager.Snapshot s) SelectSnapshot(s);
    }

    /// <summary>
    /// 一份快照在界面上显示成"几项配置、其中几项可回滚"—— <b>唯一数法，纯显示</b>。
    ///
    /// 数的是什么：右侧详情页真画出来的**勾选行**（不是文件数）——
    ///   ① 行怎么分交给 <see cref="SnapshotManager.CheckRowCountsFor"/>（它自己走 BuildDisplayRows，
    ///      同一大类的多个文件收成 1 行聚合行；凭据那种整行隐藏的不算）；
    ///   ② 再加两个开关行（「回退 DSH 版本」/「回退插件」）与它们的"勾得动"状态。
    ///
    /// 两个开关的判定**逐字抄自** <see cref="SelectSnapshot"/>（版本那一行见那里的 snapSpec，
    /// 插件那一行见那里的 changedPlugins）—— 只是把"读 manifest / 读锁文件"这两步也收进来，
    /// 好让快照**列表**（RefreshSnapshots）能在同一个方法里数出同一个数：
    ///   · 版本：manifest 的 dshSpec 为空（老快照）⇒ 退到 VersionMemory.PreviousPin，仍为空则那一行是灰的、勾不动；
    ///   · 插件：锁文件比出来的变更名单非空 ⇒ 那一行可选（磁盘版本对不上的那一路只在回滚时才算，
    ///     这里刻意不读 node_modules：列表刷新一次要对每份快照都算，别把它拖慢）。
    /// 所以列表项与详情表头**同源**：同一份快照、同一时刻，两处写出来的是同一个分数。
    ///
    /// ⚠ 只读：不写任何状态、不碰 <c>picked</c> / <c>DoRestore</c> / <c>DoRestoreAsync</c>，
    ///   也绝不改 <c>Snapshot.RestorableCount</c> 的语义（那是回滚用的真值）。
    /// </summary>
    private static (int Rollbackable, int Total) SnapshotRowCounts(SnapshotManager.Snapshot snap)
    {
        string snapSpec = SnapshotManager.ManifestValue(snap.Dir, "dshSpec");
        if (snapSpec.Length == 0) snapSpec = VersionMemory.PreviousPin;      // 与详情页同一句兜底

        string snapLockText = "", curLockText = "";
        try
        {
            string p = Path.Combine(snap.Dir, "profile-pnpm-lock.yaml");
            if (File.Exists(p)) snapLockText = File.ReadAllText(p);
            p = Path.Combine(GuardPaths.ProfileDir, "pnpm-lock.yaml");
            if (File.Exists(p)) curLockText = File.ReadAllText(p);
        }
        catch (Exception ex) { Logger.LogError("SnapshotRowCounts", ex); }

        bool versionRowEnabled = snapSpec.Length > 0;
        bool pluginsRowEnabled = PluginRevertFrom(snapLockText, curLockText).Changed.Count > 0;
        return snap.CheckRowCountsFor(versionRowEnabled, pluginsRowEnabled);
    }

    /// <summary>
    /// 列表项上的「↺」：一键回滚该快照的全部可回滚文件。
    ///
    /// 这份快照若记着"插件与现在不一样"，就连插件一起退回去 —— 判定与详情页那个
    /// 「回退插件」勾选框完全同一个（<see cref="PluginRevertFrom"/>）。
    /// 以前这里写死 <c>restorePlugins:false</c>：清单与锁文件覆盖回去了、node_modules 却没动，
    /// 于是"清单里 ^0.10.0、磁盘上还是 0.11.0"，而结果框只写一句"回滚完成"（现场 bug）。
    /// </summary>
    private void OneClickRollback_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is SnapshotManager.Snapshot s)
        {
            SelectSnapshot(s); // 让右侧详情同步高亮（勾选框也按同一判定落定）
            var backlog = RollbackPluginBacklog(s);
            DoRestore(new List<string>(), $"「{s.LocalTime} [{s.Kind}]」全部可回滚文件", "",
                      restorePlugins: backlog.Count > 0);
        }
    }

    private void SelectSnapshot(SnapshotManager.Snapshot snap)
    {
        _selectedSnapshot = snap;
        var parts = new List<UIElement>();

        // 表头第 3 行的分母 = 面板上**真的画出来的勾选行数**（本机那份快照 = 8：6 个内容行 + 2 个开关行）。
        //   以前分母数的是"快照里的文件"：同一份快照 13 个文件、12 个可回滚，清单上却只有 6 行 ——
        //   同类收成 1 行聚合行、凭据那行整行隐藏 ⇒ 表头写「12 / 12」而用户只看得见 8 个勾选框（对不上）。
        //   ⇒ 口径改成"勾选行"，而且**就在本方法末尾数 parts 里现成的勾选框本身**（见那里）：
        //     parts 就是要交给 SnapshotDetailPanel 的那份内容表，行怎么分只有
        //     SnapshotManager.BuildDisplayRows 一份（BuildFileRows 已按 IsHiddenRow 过滤过），
        //     这里既不复制分组规则、也不另立一份"行名单"去跟渲染对账。
        //   分子 = 这批行里"会真的回滚到东西"的行数（四处建行都令 IsChecked 与 IsEnabled 同值 ⇒
        //     面板刚建好时即"全部打勾"，本机 = 8 / 8）。⚠ 绝不写成"文件数 / 行数"这种分子分母不同量纲的形式。
        //   ⇒ 1.3.58：这套数法与开关状态收进 SnapshotRowCounts（列表与详情共用），见本方法末尾补表头处。
        // ⚠ 快照**列表**上那句副标题原先写的是「13 个文件 / 可回滚 12」（另一处代码、另一个口径，
        //   数的是快照里的文件数）—— 与右边详情的"行数"口径并排摆放就对不上（详见下面补表头那一段）。
        //   现已改成**与右边详情同一个数法**：见 RefreshSnapshots 里调的 SnapshotRowCounts
        //   （它转手去问 SnapshotManager.CheckRowCountsFor，与本方法末尾那两行是同一份判据）。
        var headerText = new TextBlock
        {
            // 第 3 行等到所有行都建完再补（见本方法末尾）—— 分数要数的是"真的画出来的行"。
            Text = $"[{snap.KindLabel}]  {snap.LocalTime}\n说明: {snap.Reason}\n可回滚: ",
            FontSize = 12.5,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        };
        parts.Add(headerText);

        // 文件列表：按大类聚合后建行 —— 同一插件的 N 个内部文件合成一行「插件文件」，
        // 版面从"某个插件刷屏"变成一眼看清有哪几类内容要回滚。分组的判据全在
        // SnapshotManager（CategoryOf / BuildDisplayRows），这里只负责把行画出来。
        var checks = new List<(CheckBox Box, SnapshotManager.SnapshotFile File)>();
        foreach (var el in BuildFileRows(snap, checks)) parts.Add(el);

        // 两个"虚拟勾选项"：除了配置文件，回滚还应能把 DSH 版本 与 插件 一起退回去。
        // 版式与上面的文件行完全同构（左名称 + 右灰色小字），这样上下对齐、字体一致。
        string snapSpec = SnapshotManager.ManifestValue(snap.Dir, "dshSpec");
        if (snapSpec.Length == 0) snapSpec = VersionMemory.PreviousPin;      // 旧快照没记录版本：退到"上一长期版本"
        bool hasLock = File.Exists(Path.Combine(snap.Dir, "profile-pnpm-lock.yaml"));
        string snapLockText = "", curLockText = "";
        int lockChanges = 0;
        try
        {
            string curLock = Path.Combine(GuardPaths.ProfileDir, "pnpm-lock.yaml");
            if (hasLock && File.Exists(curLock))
            {
                snapLockText = File.ReadAllText(Path.Combine(snap.Dir, "profile-pnpm-lock.yaml"));
                curLockText = File.ReadAllText(curLock);
                lockChanges = SnapshotManager.CountLockChanges(snapLockText, curLockText);
            }
        }
        catch { }

        // 与文件行同一套版式：左名称（11.5）+ 右灰色小字（11）
        (CheckBox Box, Grid Row) MakeSwitchRow(string title, string hint, bool enabled)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var left = new TextBlock
            {
                Text = title,
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(left, 0);
            row.Children.Add(left);
            var right = new TextBlock
            {
                Text = hint,
                FontSize = 11,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93))
            };
            Grid.SetColumn(right, 1);
            row.Children.Add(right);
            return (new CheckBox
            {
                Content = row,
                IsChecked = enabled,
                IsEnabled = enabled,
                FontSize = 11.5,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Foreground = enabled
                    ? new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF7))
                    : new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)),
                // 与逐文件行 / 聚合行同一套垂直节奏（见 CheckRowMarginV）
                Margin = new Thickness(0, CheckRowMarginV, 0, CheckRowMarginV)
            }, row);
        }

        var (chkVersion, _) = MakeSwitchRow("回退 DSH 版本",
            snapSpec.Length > 0 ? snapSpec : "没有可回退的旧版本", snapSpec.Length > 0);
        chkVersion.ToolTip = snapSpec.Length > 0
            ? $"把引擎的固定版本改回 {snapSpec}（快照里记的就是它；老快照则用「上一长期版本」）"
            : "这份快照没有版本记录，也没有可用的上一长期版本";
        parts.Add(chkVersion);

        // 插件行：灰字只写「N 个插件」或「不涉及」（长句会把选项文字挤掉）；
        // 具体动哪几个插件放在悬停里列出来。空表有两种含义，靠"这份/那份有没有插件清单"分开说：
        //   确实没变化（两边都有清单）      -> 「不涉及」+ 悬停"插件与现在一致"
        //   根本无法比对（缺清单/未记录锁文件）-> 同样「不涉及」，但悬停必须说明无法判断，不能冒充"没有变化"
        // 判定合成一处：与「↺ 一键回滚」、与回滚结果的核对行共用同一份名单。
        // 名单 = 锁文件对不上的 ∪ 磁盘上已装版本与快照声明对不上的
        //（后者正是现场那个 bug：回滚过一次后锁文件已被覆盖，只有检查磁盘才能识别出"包没退"）。
        var (changedPlugins, lockComparable) = PluginRevertFrom(snapLockText, curLockText);
        var diskMismatch = RollbackDiskMismatch(snap.Dir, GuardPaths.ProfileDir);
        var revertPlugins = MergePluginNames(changedPlugins, diskMismatch);
        int changedCount = revertPlugins.Count;
        var (chkPlugins, _) = MakeSwitchRow("回退插件",
            changedCount > 0 ? $"{changedCount} 个插件" : "不涉及", changedCount > 0);
        chkPlugins.IsChecked = changedCount > 0;
        chkPlugins.ToolTip = changedCount > 0
            ? "回滚 " + string.Join("、", revertPlugins.Take(12)) +
              (changedCount > 12 ? $" 等 {changedCount} 个插件" : "") + " 到原版本"
              + (diskMismatch.Count > 0
                    ? $"（其中 {diskMismatch.Count} 个是磁盘上的版本与快照声明对不上，要重装才退得回去）"
                    : "")
            : lockComparable
                ? "这份快照记的插件清单和现在一致，没有要回退的插件"
                : "这份快照没有记录插件清单（或当前清单无法读取），无法判断插件是否变更；"
                  + "勾不了这一项，回滚时会跳过插件。";
        parts.Add(chkPlugins);

        // 凭据永远不备份、也不回滚，所以列表里不再给它选项，只用一句话说明（避免"有选项却点了没用"）。
        parts.Add(new TextBlock
        {
            Text = "凭据文件永不备份、也不参与回滚。",
            FontSize = 11,
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93))
        });

        // 「整组回滚」按钮已移除：不勾选时「回滚勾选项」即为整组回滚，两个按钮并存容易误操作。
        var saveNow = new Button
        {
            Content = "保存当前快照",
            FontSize = 12,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x59)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0, 10, 8, 0),
            Cursor = Cursors.Hand,
            ToolTip = "把当前配置存成一份快照，出问题可以回滚回来"
        };
        RoundBtn.Apply(saveNow);   // 统一圆角外观
        saveNow.Click += (s, e) => SaveSnapshotNow();

        var restoreChecked = new Button
        {
            Content = "回滚勾选项",
            FontSize = 12,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xFF)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0, 10, 8, 0),
            Cursor = Cursors.Hand,
            ToolTip = "回滚左边打勾的那几项配置（没打勾就默认全部可回滚的）"
        };
        RoundBtn.Apply(restoreChecked);   // 统一圆角外观
        restoreChecked.Click += (s, e) =>
        {
            var picked = checks.Where(c => c.Box.IsChecked == true && c.File.Restorable).Select(c => c.File.Name).ToList();
            // 未勾选任何一项时按整组回滚处理（未勾选与整组回滚为同一操作）
            DoRestore(picked.Count > 0 ? picked : new List<string>(),
                picked.Count > 0 ? "所选文件" : "全部可回滚文件",
                chkVersion.IsChecked == true ? snapSpec : "",
                chkPlugins.IsChecked == true);
        };

        var del = new Button
        {
            Content = "删除快照",
            Tag = snap,
            FontSize = 12,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0, 10, 0, 0),
            Cursor = Cursors.Hand,
            ToolTip = "删除这份快照（不可恢复）"
        };
        RoundBtn.Apply(del);   // 统一圆角外观
        del.Click += DeleteSnapshot_Click;

        // 顺序：绿「保存当前快照」-> 蓝「回滚勾选项」-> 红「删除快照」；用 WrapPanel 以免窄栏里被裁掉
        var row = new WrapPanel();
        row.Children.Add(saveNow);
        row.Children.Add(restoreChecked);
        row.Children.Add(del);
        parts.Add(row);

        parts.Add(new TextBlock
        {
            Text = "回滚会直接按这份快照覆盖当前配置，不会再额外存一份快照。",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x5A, 0xC8, 0xFA)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        });

        // ── 表头第 3 行在这里补全：**数"真的画出来的勾选行"**（就是马上要进面板的那一批行）──
        // 分母 = 面板上真的会出现的勾选行数（内容行 —— 逐文件行与聚合行都是"勾选框套两列 Grid"，
        //   加 2 个开关行；本机那份快照 = 8）；分子 = 其中"会真的回滚到东西"的行数
        //   （没得回滚的行是灰的、勾不动，IsEnabled=false）。
        //   ⚠ 判据只有一份：SnapshotManager.CheckRowCountsFor 自己走 BuildDisplayRows + IsHiddenRow
        //     （与 BuildFileRows 画行时用的过滤同一条），数出来的正是"会画出来的行"；
        //     面板里非勾选的那几行（表头 / 底部说明 / 按钮行）本来就不在这些行里。
        //   四处建行都令 IsChecked 与 IsEnabled 同值 ⇒ 面板刚建好时两者相等，本机 = 8 / 8。
        // ★ 1.3.58 起这两个数**搬进快照自己的属性**（SnapshotManager.CheckRowCountsFor）：
        //   左边列表项要跟这里显示同一个分数（用户实机："你把左边这个 x 个文件也改成 x 项配置"），
        //   所以数法只能有一份 —— 两边都调它（这里的两个开关状态就是上面那两个勾选框的 IsEnabled，
        //   列表侧走 SnapshotRowCounts 数出同一套状态）。
        //   为什么分子取 IsEnabled 而不是 IsChecked：表头说的是"可回滚"，而"一个都没勾"时
        //   「回滚勾选项」按整组回滚，用勾选数会在那种状态下说反话（0 / 8 却整组回滚）；
        //   IsEnabled 在用户手动改勾选之后也不会失真。
        // ⚠ 只读这两个数，不回写任何行、不参与回滚判定（picked / DoRestore 一个字节没动）。
        var rowCounts = snap.CheckRowCountsFor(chkVersion.IsEnabled, chkPlugins.IsEnabled);
        headerText.Text += $"{rowCounts.Rollbackable} / {rowCounts.Total} 项配置";

        // 先按当前主题离屏渲染再一次性添加到面板，否则日间模式下会出现白字配白底
        SwapThemed(SnapshotDetailPanel, parts);
    }

    /// <summary>
    /// 自检用（**只读**）：把快照详情面板里"真的挂上去了的行"点一遍。
    ///
    /// 为什么需要它：详情页的文件行是"建好一个元素 → 加进 <c>SnapshotDetailPanel</c>"，
    /// 而 <b>加不进去是会抛异常的</b>（同一个元素被当成第二个父级的子元素）。异常一抛，
    /// 后面的行一个都加不上，面板上只剩最先加的那行表头 —— 截图看起来就是"文件清单整块为空"，
    /// 但 <see cref="SnapshotManager.BuildDisplayRows"/> 这类纯函数层查不出任何毛病。
    ///
    /// ⇒ 判别力就落在"行到底有没有进面板"上：把 <c>MakeFilePanel</c> 里的 <c>return cb;</c>
    ///   回退成 <c>return fileRow;</c>（曾经的写法）时，<c>FileRows</c> 会从 9 直接掉到 <b>0</b>：
    ///   面板上只剩表头那一行，两个虚拟勾选项（"回退 DSH 版本" / "回退插件"）也一并消失。
    ///
    /// 只读：不新建、不删除、不改动任何控件，也不碰回滚路径。
    /// 走的是**视觉树**（面板真正渲染出来的那棵树）：<c>LogicalTreeHelper</c> 不下钻
    /// <c>ContentControl.Content</c>，用它会把行数数成 0，反而测不出真话。
    /// <list type="bullet">
    ///   <item><c>Items</c>：面板顶层的行数（表头 / 文件行 / 虚拟勾选项 / 按钮行 / 底部说明）。</item>
    ///   <item><c>CheckBoxes</c>：视觉树上全部勾选框（含聚合行里"不显示但承接状态"的成员框）。</item>
    ///   <item><c>FileRows</c>：<b>顶层</b>的逐文件行 / 聚合行数 —— 这一项就是"文件清单有没有显示"。</item>
    ///   <item><c>VisualItems</c>：视觉树上的节点总数（布局跑过之后 &gt; 0；纯逻辑装配阶段可能还是 0）。</item>
    ///   <item><c>Markers</c>：面板里出现的文字（表头、两个虚拟勾选项标题、底部说明等）。</item>
    /// </list>
    /// </summary>
    internal (int Items, int CheckBoxes, int FileRows, int VisualItems, List<string> Markers)
        SnapshotDetailRowsForTest()
    {
        var markers = new List<string>();
        var seen = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        int boxes = 0, fileRows = 0, visual = 0;
        try
        {
            if (SnapshotDetailPanel == null) return (0, 0, 0, 0, markers);

            // 逐文件行 / 聚合行 = 内容是一个两列 Grid 的勾选框（见 MakeFilePanel / MakeGroupPanel）
            static bool IsFileRow(CheckBox c) =>
                c.Content is Grid g && g.ColumnDefinitions.Count == 2;

            foreach (UIElement el in SnapshotDetailPanel.Children)
            {
                if (el is CheckBox top && IsFileRow(top)) fileRows++;
            }

            void Walk(DependencyObject? node)
            {
                if (node == null) return;
                if (!seen.Add(node)) return;                    // 视觉树 + 逻辑树会重叠，防重入
                if (node is CheckBox cb) boxes++;
                if (node is TextBlock t && !string.IsNullOrWhiteSpace(t.Text)) markers.Add(t.Text!);

                int n = VisualTreeHelper.GetChildrenCount(node);
                for (int i = 0; i < n; i++)
                {
                    visual++;
                    Walk(VisualTreeHelper.GetChild(node, i));
                }

                // 逻辑树那趟补上"视觉树还没长出来"的部分：控件没布局时，
                // ContentControl.Content 里的子树不在视觉树上（LogicalTreeHelper 也不下钻 Content），
                // 而"回退 DSH 版本 / 回退插件"这些标题正挂在两列 Grid 里 ⇒ 只走视觉树会漏。
                if (node is ContentControl cc) Walk(cc.Content as DependencyObject);
                if (node is Panel p) foreach (UIElement c in p.Children) Walk(c);
            }
            Walk(SnapshotDetailPanel);
            return (SnapshotDetailPanel.Children.Count, boxes, fileRows, visual, markers);
        }
        catch (Exception ex)
        {
            Logger.LogError("SnapshotDetailRowsForTest", ex);
            return (SnapshotDetailPanel?.Children.Count ?? 0, boxes, fileRows, visual, markers);
        }
    }

    /// <summary>
    /// ★ 回归自证用（**只读**）：把 <see cref="SnapshotDetailRowsForTest"/> 的数落成一行固定格式，
    /// 便于"把修复回退 ⇒ 这一行变红"的对照。判定口径与自检里要落的那条断言完全一致：
    /// 展示行 ≥ 2、逐文件行 ≥ 5、三个标记（两个虚拟勾选项标题 + 底部说明）都在。
    /// </summary>
    internal string SnapshotDetailRowsVerdictForTest()
    {
        var (items, boxes, fileRows, visual, markers) = SnapshotDetailRowsForTest();
        bool hasVersion = markers.Any(m => m.Contains("回退 DSH 版本"));
        bool hasPlugins = markers.Any(m => m.Contains("回退插件"));
        bool hasFooter = markers.Any(m => m.Contains("凭据文件永不备份"));
        bool ok = items >= 2 && fileRows >= 5 && hasVersion && hasPlugins && hasFooter;
        return (ok ? "GREEN" : "RED")
             + $" items={items} boxes={boxes} fileRows={fileRows} visual={visual}"
             + $" version={hasVersion} plugins={hasPlugins} footer={hasFooter}";
    }

    /// <summary>
    /// 建详情页的文件区控件（纯显示层）：先问 <see cref="SnapshotManager.BuildDisplayRows"/>
    /// 要"该分几行、每行是哪些文件"，再照着一个行一个行地画。
    ///
    /// 凭据行在这里被整行丢掉（用户 1.3.56 实机要求"凭据不要出现，直接隐藏"）——
    ///   只是**不画**，<c>SkipReason</c> / <c>SkippedFiles</c> 与"凭据仍不会被回滚"的机制一个字节都不动。
    ///   过滤放在"建控件之前"，所以面板上不存在的行也不会被任何计数或视觉树数到。
    ///
    /// 与回滚的分工：聚合行只是视觉上的一个勾选框。行里每个可回滚文件都照旧建一个逐文件的
    ///   勾选框登记进 <paramref name="checks"/>（只是不显示出来），聚合框勾/取消时逐个同步它们。
    ///   即 "回滚勾选项"按钮收集到的仍然是同一份逐文件名字列表（见 SelectSnapshot 里的 picked），
    ///     回滚路径一个字节都没变。
    /// </summary>
    private List<UIElement> BuildFileRows(SnapshotManager.Snapshot snap,
        List<(CheckBox Box, SnapshotManager.SnapshotFile File)> checks)
    {
        var parts = new List<UIElement>();
        foreach (var row in SnapshotManager.BuildDisplayRows(snap.Files))
        {
            // 整行隐藏的项（凭据 + 不可回滚）：连行带"跳过"一起不画
            if (row.File != null && SnapshotManager.IsHiddenRow(row.File)) continue;
            if (row.Kind == SnapshotManager.DisplayRowKind.Group)
                parts.Add(MakeGroupPanel(row, checks));
            else if (row.File != null)
                parts.Add(MakeFilePanel(row.File, SnapshotManager.RowCaption(row), checks, register: true));
        }
        return parts;
    }

    /// <summary>
    /// 建一个逐文件行：名称左、大小右（大小单独占一列右对齐，名称再长也挡不住它）。
    /// <paramref name="register"/> 为 true 时把勾选框登记进 <paramref name="checks"/> ——
    /// 聚合行内部用的同名勾选框不登记（聚合框一次勾选就等于把它们全部勾上，登记进去会被统计两遍）。
    ///
    /// ⚠ 返回的是**外层勾选框**（与 <see cref="MakeGroupPanel"/> 同构），里面的 <c>fileRow</c>
    ///   只是它的 <c>Content</c>：调用方拿到的元素必须是"还没有逻辑父级"的那一个，详情面板才挂得上。
    /// </summary>
    private CheckBox MakeFilePanel(SnapshotManager.SnapshotFile f, string caption,
        List<(CheckBox Box, SnapshotManager.SnapshotFile File)> checks, bool register)
    {
        var fileRow = new Grid();
        fileRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fileRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var nameText = new TextBlock
        {
            Text = SnapshotManager.FriendlyName(f.Name),
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            // ★ 名称完整显示（用户 1.3.56 实机要求"把完整名字漏出来"）：
            //   原来是 CharacterEllipsis 单行截断 ⇒ 长名一律被切掉尾巴（"工作区与锁…"）。
            //   改成换行显示：名称列是 Grid 的 1* 列，宽度由版面给（右列 Auto 只占尺寸那点宽度），
            //   所以左边缘、名称起排、右列尺寸三者仍然对得齐。
            // ⚠ 必须配上限两行：名称再长也不能把行高撑成三行、四行（下面两个属性一起管这件事）。
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = NameTextMaxHeight,
            // 两行之内不省略号；真超过两行才用省略号收尾（"完整优先、上限兜底"）
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(nameText, 0);
        fileRow.Children.Add(nameText);

        // 不可回滚的项右列显示「跳过」，具体原因放悬停提示（否则长句会被截断）
        var size = new TextBlock
        {
            Text = f.Restorable ? GuardPaths.HumanSize(f.Size) : "跳过",
            FontSize = 11,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93))
        };
        Grid.SetColumn(size, 1);
        fileRow.Children.Add(size);

        string detail = SnapshotManager.FriendlyDetail(f.Name);
        var cb = new CheckBox
        {
            Content = fileRow,
            IsChecked = f.Restorable,
            IsEnabled = f.Restorable,
            FontSize = 11.5,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Foreground = f.Restorable
                ? new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF7))
                : new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)),
            // 与聚合行 / 两个开关行共用同一套垂直节奏（见 CheckRowMarginV）
            Margin = new Thickness(0, CheckRowMarginV, 0, CheckRowMarginV),
            // 悬停显示原始文件名与用途说明（列表只放名称与大小）；
            // 第一行由 SnapshotManager.RowCaption 给（"大类名 · 这一件是什么"），
            // 这样未聚合的单行也看得出自己属于哪一类。
            ToolTip = caption + "\n" +
                      (detail.Length > 0 ? detail + "\n" : "") +
                      (f.Restorable ? $"原文件: {f.Name}\n还原到: {f.Target}" : $"原文件: {f.Name}\n{f.SkipReason}")
        };
        if (register) checks.Add((cb, f));      // 只有直接显示的逐文件框才参与收集
        // ★ 返回外层的勾选框，不是里面的 fileRow：fileRow 已经在上一句成了 cb 的 Content
        //   （逻辑父级 = cb）。把 fileRow 直接交给详情面板，WPF 会以"指定的元素已经是另一个
        //   元素的逻辑子元素"抛 InvalidOperationException ⇒ 文件清单整块加不上、只剩表头。
        return cb;
    }

    /// <summary>
    /// 建一个聚合行：一个勾选框代表该大类的全部可回滚文件，右列是件数与大小合计，
    /// 悬停里把这一类的具体内容（插件名 + 大小，以及不可回滚的项）全列出来 ——
    /// 细节不丢，但不占版面。
    ///
    /// 勾选 / 取消勾选本行，等效于把该类里每个文件的勾选框逐个改成同一勾选状态
    /// （<see cref="SnapshotManager.BuildDisplayRows"/> 里"少于 2 件不聚合"的类不会走到这里，
    /// 它们照旧按逐文件行显示）。
    /// 语义等价性的落点：聚合框从不直接把文件交给回滚，它只改同类的逐文件勾选框，
    /// 而这些勾选框就是界面上逐行勾选时用的同一批对象（<paramref name="checks"/>）。
    /// </summary>
    private CheckBox MakeGroupPanel(SnapshotManager.SnapshotDisplayRow row,
        List<(CheckBox Box, SnapshotManager.SnapshotFile File)> checks)
    {
        bool canCheck = row.RestorableCount > 0;
        var outer = new Grid();
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 左列只有大类名（一行一个条目）。原来这里还挂过一行小灰字「整类勾选」，
        // 用户 1.3.56 实机要求去掉（"太丑了，清单要工整对齐"）⇒ 整行只剩名称，
        // 行高与逐文件行**同构**，左边缘（勾选框列）与名称起排自然对齐。
        // "这一行是整类"的语义改由悬停承载（SnapshotManager.GroupToolTip 第二行），信息不丢。
        var left = new TextBlock
        {
            Text = SnapshotManager.CategoryLabel(row.Category),
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            // 与逐文件行同一口径：名称完整显示、上限两行（见 NameTextMaxHeight）
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = NameTextMaxHeight,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(left, 0);
        outer.Children.Add(left);

        // 右列只留「件数 · 合计尺寸」（+ 跳过项数量；具体是哪些放悬停里）——
        // 尺寸右对齐成一列，与逐文件行的尺寸列同一列宽口径（Star + Auto）。
        var right = new TextBlock
        {
            Text = SnapshotManager.GroupSizeText(row.RestorableCount, row.TotalBytes)
                 + (row.SkippedCount > 0 ? $"（{row.SkippedCount} 件跳过）" : ""),
            FontSize = 11,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            // 右列绝不换行：一换行就又把这一行撑成两行（用户要"并回一行"）
            TextWrapping = TextWrapping.NoWrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93))
        };
        Grid.SetColumn(right, 1);
        outer.Children.Add(right);

        // 行内（不显示，只用来承接聚合框的勾选状态）：每个可回滚文件一个逐文件勾选框，
        // 与逐文件行的对象完全同构 —— 聚合框改的就是它们，不另立一套状态。
        var members = new List<CheckBox>();
        foreach (var f in row.RestorableFiles)
        {
            var memberCb = new CheckBox { IsChecked = true, Visibility = Visibility.Collapsed };
            members.Add(memberCb);
            checks.Add((memberCb, f));      // 登记：回滚收集勾选项时读到的是这一批
        }

        var groupCb = new CheckBox
        {
            Content = outer,
            IsChecked = canCheck,
            IsEnabled = canCheck,
            FontSize = 11.5,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Foreground = canCheck
                ? new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF7))
                : new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)),
            // ★ 行间距统一（用户 1.3.57 实机："看着有点乱"）：原来是 (0,4,0,2)，比逐文件行多 2px 上间距，
            //   同一列里"上间距"2/4 交替跳 ⇒ 改成与逐文件行、两个开关行同一个 CheckRowMarginV。
            Margin = new Thickness(0, CheckRowMarginV, 0, CheckRowMarginV),
            ToolTip = SnapshotManager.GroupToolTip(row)
        };
        groupCb.Click += (s, e) =>
        {
            foreach (var memberCb in members) memberCb.IsChecked = groupCb.IsChecked == true;
        };
        return groupCb;
    }

    /// <summary>
    /// 手动保存一份当前状态快照。点击必过确认框（避免误触）；
    /// 若引擎正处于异常状态（启动异常计数 / 未在运行）会额外提示"先别存"。
    /// </summary>
    private void SaveSnapshotNow()
    {
        try
        {
            int errs = VersionMemory.ConsecutiveErrors;
            bool running = _isRunning;
            string warn = errs > 0
                ? $"\n⚠ 检测到引擎最近有 {errs} 次启动异常：先别在这个状态存，等把问题解决再存，否则会把故障状态一起存下来。\n"
                : !running
                    ? "\n提示：引擎当前没在运行。这时存下来的是停机状态的配置；想记录可用状态，建议等引擎跑起来、确认正常后再存。\n"
                    : "";

            var r = GuardDialog.Show(
                "现在保存一份快照？\n\n" +
                "· 会把当前的配置文件原样存一份（插件清单、补丁层配置、全局设置等）\n" +
                "· 请确认引擎现在是正常状态：如果正在报错或启动失败，先别存，等修好了再存\n" +
                warn +
                "\n存好后可以在左边列表里看到它，随时一键回滚。",
                "保存当前快照", MessageBoxButton.OKCancel,
                errs > 0 ? MessageBoxImage.Warning : MessageBoxImage.Question);
            if (r != MessageBoxResult.OK) return;

            // 手动快照上限：达到上限后先询问「删旧存新」，用户确认后才删除最旧的那份（绝不静默删除）
            int overflow = SnapshotManager.ManualOverflow();
            if (overflow > 0)
            {
                var oldOnes = SnapshotManager.OldestManual(overflow);
                string oldList = oldOnes.Count > 0
                    ? "将删掉最旧的 " + overflow + " 份：\n  " + string.Join("\n  ",
                        oldOnes.Select(s => $"{s.LocalTime}  {s.Reason}"))
                    : "";
                var ask = GuardDialog.Show(
                    $"手动快照已经有 {SnapshotManager.ListNative().Count(s => s.Kind == SnapshotManager.KindManual)} 份，上限是 {SnapshotManager.ManualKeep} 份。\n\n" +
                    "要删掉最旧的、腾出位置保存这一份吗？\n\n" + oldList +
                    "\n\n删掉的快照找不回来；选择「否」就不保存这一份。",
                    "手动快照已满", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (ask != MessageBoxResult.Yes)
                {
                    ConsoleStatusText.Text = "已取消保存（手动快照已达上限）";
                    return;
                }
                int deleted = SnapshotManager.TrimManualToMakeRoom();
                AddEvent($"手动快照已满，删掉最旧的 {deleted} 份后保存新的", EventKind.Warn);
            }

            var snap = SnapshotManager.Create(SnapshotManager.KindManual, "手动保存");
            if (snap == null)
            {
                GuardDialog.Show("快照保存失败，可以到「日志」页查看原因。", "保存当前快照",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            AddEvent($"已保存快照（{snap.LocalTime}，{snap.RestorableCount} 个文件）", EventKind.Update);
            ConsoleStatusText.Text = $"已保存快照: {snap.LocalTime}";
            RefreshSnapshots();
            var fresh = _snapshots.FirstOrDefault(s => s.Id == snap.Id);
            if (fresh != null) SelectSnapshot(fresh);
        }
        catch (Exception ex)
        {
            Logger.LogError("SaveSnapshotNow", ex);
            GuardDialog.Show("快照没能保存，可稍后再存一次；" + LogPromise("详细原因已记入日志，可在「日志」页查看。"), "保存当前快照",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ══════════ 回滚：插件这一步的判定 / 命令行 / 结果文案（都抽成纯函数，自检盯着，不许回归） ══════════

    /// <summary>
    /// 这份快照"要不要连插件一起退回去"——唯一判定。
    /// 详情页的「回退插件」勾选框与列表行上的「↺ 一键回滚」都走它。
    ///
    /// 为什么必须合成一个：勾选框按当初的判定自动打勾（"N 个插件"），而「↺」以前写死
    /// <c>restorePlugins:false</c>，于是回滚只把 package.json 与锁文件覆盖回去、node_modules 没有任何变动 ——
    /// 现场就是"清单里是 ^0.10.0，磁盘上装的还是 0.11.0"，而结果框说"回滚完成"。
    /// 更糟的是插件更新失败时界面自己提示"点一键回滚即可全部还原"，两条路径的说法并不一致。
    /// </summary>
    private static (List<string> Changed, bool Comparable) PluginRevertFrom(string? snapLockText, string? curLockText)
        => (SnapshotManager.ChangedPlugins(snapLockText, curLockText),
            SnapshotManager.HasImporterDeps(snapLockText) && SnapshotManager.HasImporterDeps(curLockText));

    /// <summary>同上，自己把"快照里的锁文件"与"当前锁文件"读出来（无法读取时按空文本处理，即判成不可比）。</summary>
    private static (List<string> Changed, bool Comparable) PluginRevertPlan(SnapshotManager.Snapshot snap)
    {
        string snapLock = "", curLock = "";
        try
        {
            string p = Path.Combine(snap.Dir, "profile-pnpm-lock.yaml");
            if (File.Exists(p)) snapLock = File.ReadAllText(p);
            p = Path.Combine(GuardPaths.ProfileDir, "pnpm-lock.yaml");
            if (File.Exists(p)) curLock = File.ReadAllText(p);
        }
        catch (Exception ex) { Logger.LogError("PluginRevertPlan", ex); }
        return PluginRevertFrom(snapLock, curLock);
    }

    /// <summary>
    /// 磁盘上的已装版本与快照声明的范围不一致的插件（纯函数，只读盘）。
    ///
    /// 为什么只检查锁文件不够（现场那个 bug 正出在这里）：用户回滚过一次之后，当前 pnpm-lock.yaml
    /// 已经被快照那一份覆盖 -> "快照锁 vs 当前锁"从此永远显示"没变化"，而 node_modules 里
    /// 实际装着的还是升级后的版本（现场实测：清单 ^0.10.0、锁文件 0.10.0、磁盘 0.11.0）。
    /// 所以回滚判定必须再检查一次磁盘：已装版本不满足快照声明范围的，就是"要重装才退得回去"的。
    ///
    /// 判不了的一律不算进来（不误触发安装）：git 源 / 本地路径 / * 这类写法、磁盘上无法读取版本、
    /// 以及根本没装的（那种另有专门的提示与自愈）。
    /// </summary>
    internal static List<string> RollbackDiskMismatch(string snapshotDir, string profileDir)
    {
        var list = new List<string>();
        try
        {
            string snapPkg = Path.Combine(snapshotDir ?? "", "profile-package.json");
            if (!File.Exists(snapPkg)) return list;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(snapPkg));
            if (!doc.RootElement.TryGetProperty("dependencies", out var deps)) return list;

            foreach (var d in deps.EnumerateObject())
            {
                string spec = d.Value.GetString() ?? "";
                string have = PluginManager.ReadInstalledVersion(profileDir ?? "", d.Name);
                if (have.Length == 0) continue;                       // 未安装 / 无法读取 -> 此处不判定
                if (CheckRollbackSpec(spec, have) == RollbackSpecCheck.Mismatch) list.Add(d.Name);
            }
            list.Sort(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) { Logger.LogError("RollbackDiskMismatch", ex); }
        return list;
    }

    /// <summary>把两路来源合成回滚的插件名单（去重 + 稳定排序）。</summary>
    internal static List<string> MergePluginNames(IEnumerable<string>? a, IEnumerable<string>? b)
        => (a ?? Enumerable.Empty<string>()).Concat(b ?? Enumerable.Empty<string>())
           .Where(n => !string.IsNullOrWhiteSpace(n))
           .Distinct(StringComparer.OrdinalIgnoreCase)
           .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
           .ToList();

    /// <summary>
    /// 这次回滚"该重装哪几个插件"——唯一名单：详情页勾选框、「↺ 一键回滚」、结果核对行都用它。
    /// = 锁文件对不上的 ∪ 磁盘上已装版本与快照声明对不上的。
    /// </summary>
    internal static List<string> RollbackPluginBacklog(SnapshotManager.Snapshot snap)
    {
        try
        {
            return MergePluginNames(PluginRevertPlan(snap).Changed,
                                    RollbackDiskMismatch(snap.Dir, GuardPaths.ProfileDir));
        }
        catch (Exception ex) { Logger.LogError("RollbackPluginBacklog", ex); return new List<string>(); }
    }

    /// <summary>回滚"重装插件"这一步的命令行（纯函数）：只有这两条，不在此处拼接其他内容。</summary>
    internal static string RollbackReinstallArgs(bool frozenLockfile)
        => frozenLockfile ? "install --frozen-lockfile" : "install";

    /// <summary>
    /// 自检用：给定"快照里的锁文件 / 当前的锁文件"，这份快照要不要连插件一起退回去。
    /// 与详情页勾选框、列表行「↺」共用同一个判定（<see cref="PluginRevertFrom"/>）。
    /// </summary>
    internal static bool PluginRevertDecisionForTest(string? snapLockText, string? curLockText)
        => PluginRevertFrom(snapLockText, curLockText).Changed.Count > 0;

    /// <summary>这一步的结果形态。</summary>
    internal enum RollbackInstallOutcome
    {
        /// <summary>按快照的锁文件精确装好了。</summary>
        FrozenOk,
        /// <summary>锁文件装不成，退回普通安装 —— 装是装了，但版本可能与快照不完全一致。</summary>
        PlainFallback,
        /// <summary>两条都没成。</summary>
        Failed
    }

    /// <summary>
    /// 回滚"重装插件"这一步的结论行（纯函数，便于自检）。
    /// 三种形态必须各不相同且互相区分：尤其退回普通安装那种，不能写成"成功"了事。
    /// </summary>
    internal static string RollbackInstallLine(RollbackInstallOutcome outcome, string reason)
    {
        string why = string.IsNullOrWhiteSpace(reason) ? "（未留下可读的原因）" : reason.Trim();
        return outcome switch
        {
            RollbackInstallOutcome.FrozenOk =>
                "✅ 插件已按快照记录的版本重装（含来自仓库源的提交）",
            RollbackInstallOutcome.PlainFallback =>
                "⚠️ 按快照记录的版本重装失败（快照与当前插件清单不一致），已按当前清单重新安装："
                + "版本可能与快照不完全一致，下面逐项核对了磁盘上的真实版本。\n   原因：" + why,
            _ => "❌ 插件重装失败（两种安装方式都未成功）：磁盘上的版本没有退回去。\n   原因：" + why
        };
    }

    /// <summary>
    /// 回滚重装被"缺 Git 闸门"拦下时的结论行（纯函数，便于自检）。
    ///
    /// ⚠ 必须是 <c>❌</c> 开头：这一项**确实没有还原**（包没退回去），只是原因不是命令失败，
    ///   而是"这条命令根本没跑"。<see cref="SnapshotManager.ClassifyRestoreLine"/> 只认前缀，
    ///   写成中性句就会被数成"未执行"、甚至漏出"回滚完成"—— 那正是本项目栽过的那类疤。
    ///   也不复用 <see cref="RollbackInstallLine"/> 的 Failed 文案：那句写的是"两种安装方式都未成功"，
    ///   而这里第二条**根本没执行**，照抄就成了不实描述。
    ///
    /// 措辞复用缺 Git 那几句的唯一实现处（<see cref="GitMissingNote"/> / <see cref="GitMissingNextStep"/>），
    /// 不在调用点另编一套。
    /// </summary>
    internal static string RollbackGitBlockedLine()
        => "❌ 插件重装失败：" + GitMissingNote + "，本次未执行安装命令；磁盘上的版本没有退回去。\n   "
           + GitMissingNextStep + "。";

    /// <summary>
    /// 从 pnpm 输出里挑出"能让人看懂的那一句"当原因（纯函数）。
    /// 优先 ERR_PNPM_* / ERROR 这类定级行；都没有就退回最后一行，绝不把整段输出直接填入结论框。
    /// </summary>
    internal static string RollbackInstallReason(string? output)
    {
        try
        {
            var lines = (output ?? "").Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .ToList();
            if (lines.Count == 0) return "";
            string hit = lines.FirstOrDefault(l => l.Contains("ERR_PNPM", StringComparison.OrdinalIgnoreCase))
                      ?? lines.FirstOrDefault(l => l.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                      ?? lines.FirstOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase))
                      ?? lines[^1];
            return Shorten(hit, 200);
        }
        catch { return ""; }
    }

    /// <summary>
    /// 回滚后"到底退回去没有"的逐项核对行：拿快照当时的声明（快照里的 profile-package.json）
    /// 与磁盘上真实装着的版本对一遍。纯函数（只读盘），便于自检。
    ///
    /// 为什么要有这一步：清单退回去只说明"文件"退回去了，插件是否装回去需查看 node_modules。
    /// 现场就是清单 ^0.10.0、磁盘上 0.11.0 —— 结果框必须把这件事说出来，而不是说"回滚完成"。
    /// </summary>
    /// <param name="onEach">
    /// 可选的"核到第几个"回调（1 起数，共 <c>list.Count</c> 个）：进度窗据此显示「x / N」。
    /// 只是给人看的进度，不参与任何判定，异常也不会影响核对结果（内部吞掉）。
    /// </param>
    internal static List<string> RollbackVersionCheckLines(string snapshotDir, string profileDir, IEnumerable<string>? names,
                                                           Action<int, int>? onEach = null)
    {
        var lines = new List<string>();
        try
        {
            var list = (names ?? Enumerable.Empty<string>())
                .Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (list.Count == 0) return lines;                       // 没涉及插件 -> 不额外输出

            string snapPkg = Path.Combine(snapshotDir ?? "", "profile-package.json");
            if (!File.Exists(snapPkg))
            {
                lines.Add("⚠️ 快照里没有插件清单，无法核对插件版本是否已回退");
                return lines;
            }

            int doneCount = 0;
            foreach (var name in list)
            {
                // 逐个核对：进度窗据此报「x / N」（回调出错与核对无关，一律不影响结果行）
                try { onEach?.Invoke(++doneCount, list.Count); } catch { }
                string spec = PluginManager.DepSpecIn(snapPkg, name);
                // 磁盘上读到的版本先归一化再给用户看：同伴依赖后缀 `(...)` 不属于版本本身
                //（口径与快照比对同源，见 SnapshotManager.NormalizeLockVersion）
                string have = SnapshotManager.NormalizeLockVersion(PluginManager.ReadInstalledVersion(profileDir ?? "", name));
                string want = spec.Length > 0 ? spec : "快照的清单里未登记它";
                if (have.Length == 0)
                    lines.Add($"⚠️ {name}：磁盘上无法读取已装版本（快照要求 {want}）—— 是否已回退未核实");
                else
                    switch (CheckRollbackSpec(spec, have))
                    {
                        case RollbackSpecCheck.Match:
                            lines.Add($"✅ {name} 现在是 {have}（快照要求 {want}）");
                            break;
                        case RollbackSpecCheck.Mismatch:
                            lines.Add($"❌ {name} 未回退：磁盘上还是 {have}，快照要求 {want}");
                            break;
                        default:
                            lines.Add($"ℹ️ {name} 现在是 {have}（快照要求 {want}，这种写法无法按版本核对）");
                            break;
                    }
            }
        }
        catch (Exception ex) { Logger.LogError("RollbackVersionCheckLines", ex); }
        return lines;
    }

    /// <summary>回滚核对的三态。</summary>
    internal enum RollbackSpecCheck { Match, Mismatch, NotComparable }

    /// <summary>
    /// 磁盘上的版本落没落在快照声明的范围内（纯函数，便于自检）。
    ///
    /// 不能用 <c>VersionInfo.Satisfies</c>：它是给"兼容性四色分档"用的，对 ^ / ~ 采用
    /// "只要 ≥ 声明下限就算满足"的宽松口径 -> <c>Satisfies("^0.10.0","0.11.0")</c> 会返回 true，
    /// 正好把要抓的那种"升上去了没退回来"漏掉。这里按 semver 的真实上界判：
    ///   · <c>^0.10.0</c> -> 0.10.x（0.x 的 caret 锁 minor）；<c>^1.2.0</c> -> 1.x；
    ///   · <c>~1.2.0</c> -> 1.2.x；精确版本号必须完全相等；
    ///   · git 源 / 本地路径 / * / 区间写法 -> NotComparable（不硬判，交给上面的文案如实说明）。
    /// </summary>
    internal static RollbackSpecCheck CheckRollbackSpec(string? spec, string? installed)
    {
        string s = (spec ?? "").Trim();
        string v = (installed ?? "").Trim();
        if (s.Length == 0 || v.Length == 0) return RollbackSpecCheck.NotComparable;

        // 不是"某个版本号"的写法：按版本比对没有意义（git 源按提交、本地路径按目录）
        if (s.StartsWith("github:", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("git+", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("git://", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith(".") || s.Contains("\\") || s.Contains("/") ||
            s == "*" || s.Equals("latest", StringComparison.OrdinalIgnoreCase) ||
            s.Contains(" ") || s.Contains("||")) return RollbackSpecCheck.NotComparable;

        if (s.Equals(v, StringComparison.OrdinalIgnoreCase)) return RollbackSpecCheck.Match;

        if (s.StartsWith("^") || s.StartsWith("~"))
        {
            string lo = s.Substring(1).Trim();
            if (VersionInfo.Compare(v, lo) < 0) return RollbackSpecCheck.Mismatch;   // 比声明下限还低
            var (loMajor, loMinor) = MajorMinor(lo);
            var (vMajor, vMinor) = MajorMinor(v);
            if (loMajor < 0 || vMajor < 0) return RollbackSpecCheck.NotComparable;
            if (s.StartsWith("~")) return (vMajor == loMajor && vMinor == loMinor)
                ? RollbackSpecCheck.Match : RollbackSpecCheck.Mismatch;
            if (loMajor == 0) return (vMajor == 0 && vMinor == loMinor)                 // ^0.x -> 0.x.*
                ? RollbackSpecCheck.Match : RollbackSpecCheck.Mismatch;
            return vMajor == loMajor ? RollbackSpecCheck.Match : RollbackSpecCheck.Mismatch;   // ^x.y -> x.*
        }

        // >=x / <=x / >x / <x 这类区间：无法确定"是不是快照那一版"，不硬判
        if (s.StartsWith(">") || s.StartsWith("<") || s.StartsWith("=")) return RollbackSpecCheck.NotComparable;

        return VersionInfo.Compare(v, s) == 0 ? RollbackSpecCheck.Match : RollbackSpecCheck.Mismatch;
    }

    /// <summary>取版本号的主次版本（纯函数）：解析不出给 -1（调用方据此判"没法比"）。</summary>
    private static (int Major, int Minor) MajorMinor(string v)
    {
        try
        {
            var parts = (v ?? "").Trim().TrimStart('^', '~', '>', '=', 'v', ' ').Split('.');
            int major = parts.Length > 0 && int.TryParse(new string(parts[0].TakeWhile(char.IsDigit).ToArray()), out var a) ? a : -1;
            int minor = parts.Length > 1 && int.TryParse(new string(parts[1].TakeWhile(char.IsDigit).ToArray()), out var b) ? b : -1;
            return (major, minor);
        }
        catch { return (-1, -1); }
    }

    /// <summary>
    /// 回滚：默认只回配置文件（旧行为）；勾上"版本""插件"后升级为完整回滚——
    ///   ① 覆盖快照里的文件（含 pnpm-lock.yaml）
    ///   ② 把 DSH 固定版本钉回快照记录的那个版本
    ///   ③ 按快照的锁文件重装插件（连 git 源解析到的提交一起退回）
    /// 三步顺序固定，任一步失败都如实写进结果，不静默。
    /// </summary>
    private void DoRestore(List<string> names, string what, string versionTarget = "", bool restorePlugins = false)
    {
        var snap = _selectedSnapshot;
        if (snap == null) { ConsoleStatusText.Text = "请先选择快照"; return; }

        bool restoreVersion = versionTarget.Length > 0;

        // 插件写闸：回滚这条路径也会跑 pnpm install（勾了「回退插件」时，见 DoRestoreAsync ③），
        //   动的是整棵依赖树 -> 与三条更新路径、批量更新 / 批量卸载、市场安装必须互斥。
        //   位置与那几处一致：放在确认框之前 —— 以免用户点了「确认回滚」、命令即将执行时才被告知"正忙"。
        //   闸门只有一份（PassPluginWriteGate，只查不开，提示语由它给出），这里不重复编写文案。
        //   为什么放在"算名单"之前而不是只在"勾了回退插件"时才查：回滚整份快照会覆盖
        //   settings.json 与 pnpm-lock.yaml，而"勾没勾"要到确认框那一段才成形（详情页那条路径
        //   是把 restorePlugins 直接传进来的）-> 入口只查一次、不设两套判据；
        //   代价是"正在更新时，纯配置回滚也会被拒"—— 对它而言这是宁严勿松（回滚本就该在安静时做）。
        //   同时把市场安装也纳进来了：它占的是 _marketBusy，会在下面 DoRestoreAsync 落闸时
        //   反过来查这道闸 -> 两条路径互相看得见（与那套"两标志 + 入口互查"同一个道理）。
        //   早退不落闸：上面那条 snap == null 在本行之前，它 return 时闸门一个字节都没动。
        if (!PassPluginWriteGate()) return;

        // 涉及哪几个插件必须在回滚之前算：SnapshotManager.Restore 一跑就把当前锁文件
        // 覆盖成快照里那份了，之后再比就永远是"没变化" -> 核对行会一条都不出（这种情形会被漏掉）。
        // 名单 = 锁文件对不上的 ∪ 磁盘上装着的版本与快照声明对不上的（见 RollbackPluginBacklog）。
        var pluginBacklog = RollbackPluginBacklog(snap);

        string extra = "";
        if (restoreVersion) extra += $"• 同时把 DSH 版本改回 {versionTarget}\n";
        if (restorePlugins)
        {
            extra += "• 同时重装插件（按快照记录的版本精确回退，含来自仓库源的提交）\n";
            if (pluginBacklog.Count > 0)
                extra += $"  涉及 {pluginBacklog.Count} 个：{Shorten(string.Join("、", pluginBacklog.Take(6)), 80)}"
                       + (pluginBacklog.Count > 6 ? " 等" : "") + "\n";
            if (_isRunning || NetworkHelper.IsPortListening(_port))
                extra += "• 引擎正在运行：会先把它停下（重装完请重新「一键启动引擎」）\n";
        }

        var confirm = GuardDialog.Show(
            $"即将回滚{what}（{snap.KindLabel} 快照 {snap.LocalTime}）。\n\n" +
            $"• 直接按这份快照覆盖当前配置（不会再额外存一份快照）\n" +
            $"• 凭据文件永不回滚（快照里不含凭据）\n" +
            extra +
            $"• 回滚后需重启 DSH 才会生效\n\n继续？",
            "确认回滚", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        _ = DoRestoreAsync(snap, names, versionTarget, restoreVersion, restorePlugins, pluginBacklog);
    }

    /// <summary>
    /// 真正干活的那一段（在 UI 线程上跑，多处 await 会回到 UI 线程推进度）。
    ///
    /// 「涉及回滚插件」时才额外弹一个伪模态的进度窗（<see cref="RollbackProgressWindow"/>）：
    /// 那一步要跑一次几分钟的插件安装，用户看到的就是"点哪里都没有反应"，必须有个明确的"还在跑"。
    /// 弹窗期间主窗被置为不可用（<see cref="BeginRollbackProgress"/>），关窗一律走
    /// <see cref="CloseRollbackProgress"/>（本方法的 finally 兜底）—— 成功、失败、异常都能收干净。
    /// </summary>
    private async Task DoRestoreAsync(SnapshotManager.Snapshot snap, List<string> names, string versionTarget,
                                      bool restoreVersion, bool restorePlugins, List<string> pluginBacklog)
    {
        // 落入"改依赖图中"（插件写闸）：下面第一条 await 之后就要跑 pnpm install —— 这一步动的是
        //   整棵依赖树（不止锁文件里点名的那几个包）-> 与三条更新路径、批量更新 / 批量卸载、
        //   以及市场安装改的是同一份 node_modules / pnpm-lock.yaml，必须互斥。
        //   落闸换来的另一面：写闸期间，那几处的入口查的是 _pluginWriteBusy -> 这里跑着时
        //   它们会各自如实说一句"正在忙"并拒绝，不会与回滚重装并发。
        //   为什么落在 try 之外的第一句：本方法在 try 之前没有任何早退（进来就是同步的取数），
        //   放在这里就是"确认框通过之后、第一条 await 之前"；它与 try 之间只隔两个同步的局部变量声明，
        //   而下面的 finally 一定执行 -> 连"进 try 之前那一句就抛"这种极端情况也不会把闸门永远关着。
        //   （对照：那几条路径在 try 之前有早退，所以它们的 Begin 只能放在 try 内、早退之后。）
        BeginPluginWriteState();
        RollbackProgressWindow? progress = null;
        int pluginCount = pluginBacklog.Count;
        try
        {
            // 名单为空 / 没勾回退插件 -> 不弹出（判定是纯函数，自检覆盖），流程与以前完全一致
            if (RollbackProgressNeeded(restorePlugins, pluginCount))
            {
                progress = BeginRollbackProgress(RollbackStepText(RollbackStep.Preparing, pluginCount), pluginCount);
                // 先让出一帧把窗口画出来再执行耗时操作：第一步是同步的文件覆盖，
                // 不让出这一帧的话，用户先看到的会是一个还没画完的白框（甚至误以为程序已无响应）。
                await Dispatcher.Yield(DispatcherPriority.Render);
            }

            var report = SnapshotManager.Restore(snap, names);
            // 插件这一步的实际结果（写进事件栏）：不写就会出现"弹窗已报完成、事件已报成功、包却未回退"
            string pluginNote = "";
            // ⚠ 这里**没有**任何 fail/ok 计数器：成败一律由最后的 SummarizeRestoreReport 按唯一判据数。
            //   本单的疤正是"在别处各记一笔"：✕ 那一行没人记，于是失败恒为 0。

            // ② 版本回退：钉回快照记录的版本
            if (restoreVersion)
            {
                progress?.SetStep(RollbackStepText(RollbackStep.VersionPin, pluginCount));
                // ★ 这件事的三种结果各用各的前缀（判据只有 SnapshotManager 一份）：
                //   · 没有目标版本 ⇒ ⚠️ 没核实到（不能算成功）；
                //   · 本来就是那个版本 ⇒ ✅ 无需改动（确实是想要的样子）；
                //   · 真钉回去了 ⇒ ✅ 成功；钉失败 ⇒ ❌（统计在最后统一过一遍，不在这里各记一笔）。
                if (versionTarget.Length == 0) report.Add("⚠️ 没有可回退的目标版本，已跳过版本回退");
                else if (string.Equals(versionTarget, VersionMemory.Spec, StringComparison.OrdinalIgnoreCase))
                    report.Add($"✅ DSH 版本本来就是 {versionTarget}，无需改动");
                else
                {
                    try
                    {
                        VersionMemory.PinTo(versionTarget);
                        report.Add($"✅ DSH 版本已改回 {versionTarget}（重启引擎后生效）");
                        UpdateVersionCard();
                        if (IsVersionPageVisible()) RenderVersionView();
                    }
                    // ★ 失败就是 ❌，不另造一套前缀；统计在最后统一按唯一判据过一遍，
                    //   所以"这里忘了记一笔失败"这个口子不存在了。
                    catch (Exception ex)
                    {
                        Logger.LogError("RestoreVersionPin", ex);
                        report.Add("❌ 版本没能改回去（详细原因已记入日志）");
                    }
                }
            }

            // ③ 插件回退：按快照的锁文件重装
            if (restorePlugins)
            {
                // 插件重装要动 node_modules，引擎还在跑就会因占用而失败 -> 先停引擎（回滚完提示重启）
                if (_isRunning || NetworkHelper.IsPortListening(_port))
                {
                    progress?.SetStep(RollbackStepText(RollbackStep.StoppingEngine, pluginCount));
                    ConsoleStatusText.Text = "正在停止引擎…";
                    try
                    {
                        await TerminateEngineAsync(interactive: false);
                        report.Add("ℹ️ 已先停止引擎（重装完请重新「一键启动引擎」）");
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError("StopEngineBeforeRestore", ex);
                        report.Add("⚠️ 引擎没能停下，插件这一步可能受影响（详细原因已记入日志）");
                    }
                }
                progress?.SetStep(RollbackStepText(RollbackStep.Reinstalling, pluginCount));
                progress?.SetCount("");     // 这一步是整棵依赖树一次装完，没有"第几个包"可报，就不显示虚假的进度数字
                ConsoleStatusText.Text = "正在重装插件…";
                // 安装中断后的自愈（复用与安装/更新同一入口）：回滚重装前先清「目录在、package.json 缺」的残留，
                // 否则 pnpm 报「目录已存在」拒绝安装 -> 回滚显示已完成、包实际并未回来。逐包清，越界/失败如实入报告。
                foreach (string bl in pluginBacklog)
                {
                    if (!EnsureNotBrokenInstall(bl, out string rbNote))
                        report.Add("⚠️ " + rbNote + "（" + bl + "）");
                }
                // relaxSupplyChainPolicy: true 必须在这里显式给：这一步确实是 pnpm 改动，
                // 但 `install --frozen-lockfile` 里既没有 " plugin " 也没有 " --profile "，
                // 自动判定（PluginManager.LooksLikePluginMutation）永远匹不中它 -> 不显式开，
                // pnpm 的包龄（默认 1440 分钟）与锁文件复核就会把安装挡下来 ——
                // 现场表现正是"回滚显示已完成、磁盘上的版本却未回退"。
                var (okP, outP) = await RunCommandAsync("pnpm", RollbackReinstallArgs(frozenLockfile: true),
                    GuardPaths.ProfileDir, timeoutMs: 900000, relaxSupplyChainPolicy: true);
                string frozenReason = RollbackInstallReason(outP);
                if (!okP)
                {
                    // ★ 缺 Git 闸门（唯一入口）：上面那条 `--frozen-lockfile` **不需要重新解析**（可接受），
                    //   而下面这条普通 install 会**重新解析**清单 —— 清单里若有代码仓库来源（git 源）的插件，
                    //   pnpm 就要去调系统的 git；本机 PATH 里没有 git 时，用户看到的只会是一句英文
                    //   `spawn git` / ERR_PNPM_GIT_RESOLVE_FAILED（本机已实测：两种 git 形态都需要 git）。
                    //   ⇒ 拦下：不跑这条命令。
                    //   判据落在"这次要重装的那几个包（pluginBacklog）的清单声明"上 —— 这条路径手上
                    //   现成的就是这份包名名单，逐名取声明即可（DepSpec 读的就是下面 install 要解析的那份清单：
                    //   上面 SnapshotManager.Restore 已把它覆盖成快照那一份，用户没勾清单那一项时则仍是当前那份）。
                    //   只要有一条是 git 源就把**整条** install 拦下（一条命令装整棵依赖树，
                    //   拦就得整条拦，不能只跳某一个包）；对 npm 源用户一个字节都不影响
                    //   （NeedsGitFor 对 npm 包名恒为 false）。与 PreflightManifestAsync 那条整份清单的闸门同款。
                    //   注：pnpm 的普通 install 只为"锁文件满足不了的条目"重新解析（锁文件已满足的直接跳过解析），
                    //   而这份名单正是回滚认定"锁文件/磁盘对不上"的那一批 ⇒ 与实际会解析的集合同源。
                    //   ⚠ 拦下只是"不跑命令 + 如实记一行"，**绝不 return**：进度窗 / 写闸 / 主窗可用性
                    //     全部照旧走本方法既有的收尾（下面 SummarizeRestoreReport → CloseRollbackProgress → finally）。
                    bool fallbackNeedsGit =
                        PluginManager.AnyNeedsGit(pluginBacklog.Select(PluginManager.DepSpec)) && !GitOnPath();
                    if (fallbackNeedsGit)
                    {
                        Logger.NoteDiagnosis($"回滚重装 {pluginBacklog.Count} 个插件：清单里有代码仓库来源（git 源），"
                                           + "但本机 PATH 里没有 git ⇒ 未执行普通安装命令");
                        report.Add(RollbackGitBlockedLine());
                        pluginNote = "插件重装失败，版本未回退";
                    }
                    else
                    {
                        var (okP2, outP2) = await RunCommandAsync("pnpm", RollbackReinstallArgs(frozenLockfile: false),
                            GuardPaths.ProfileDir, timeoutMs: 900000, relaxSupplyChainPolicy: true);
                        // 插件重装失败这一支不再手工记数：下面的 ❌ 行会被唯一判据数进去
                        //（当年是手工 fail++，本单这类"漏记一处"就是缺陷根源）。
                        report.Add(RollbackInstallLine(
                            okP2 ? RollbackInstallOutcome.PlainFallback : RollbackInstallOutcome.Failed,
                            okP2 ? frozenReason : RollbackInstallReason(outP2 + "\n" + outP)));
                        pluginNote = okP2 ? "插件改用了普通安装（版本可能与快照不完全一致）"
                                          : "插件重装失败，版本未回退";
                    }
                }
                else
                {
                    report.Add(RollbackInstallLine(RollbackInstallOutcome.FrozenOk, ""));
                    pluginNote = "插件已按快照记录的版本重装";
                }

                _pluginUpdates.Clear();          // 让下次刷新重新查版本
            }

            // 插件这一步的核对行：只要"该重装哪几个"不是空的就逐项核对磁盘上的真实版本 ——
            // 用户没勾「回退插件」时同样核对（只是不重装），以免"清单退了、包没退"从结果框里漏掉。
            // 名单是回滚前算好的（见 DoRestore）：此刻锁文件已被覆盖，再比就比不出来了。
            if (pluginBacklog.Count > 0)
            {
                if (!restorePlugins)
                {
                    report.Add("ℹ️ 本次没有勾选「回退插件」，插件不会被重装；下面只核对磁盘上的实际版本");
                    pluginNote = $"插件没有重装（{pluginBacklog.Count} 个的磁盘版本与快照对不上）";
                }
                progress?.SetStep(RollbackStepText(RollbackStep.Verifying, pluginCount));
                foreach (var line in RollbackVersionCheckLines(snap.Dir, GuardPaths.ProfileDir, pluginBacklog,
                            // 「x / N」：这一步是真的一个一个核，报出来的数就是真实进度
                            (done, total) => progress?.SetCount(RollbackCountText(done, total))))
                {
                    // ★ 不在这里数前缀：下面 SummarizeRestoreReport 用唯一判据把整份报告过一遍，
                    //   所以"逐项核对里有一项没回退"照样会把整次回滚拖出"完成"档。
                    report.Add(line);
                }
            }

            // ══ 结果统计：只在这里数一次，且只走 SnapshotManager 那一份分类判据 ══
            //    本单的疤：产出方写的 ✕ 既不是 ✅ 也不是 ❌，消费方只数这两种 ⇒ 失败恒为 0，
            //    弹窗照写「回滚完成: 成功 0 / 失败 0」+ Information 图标，而文件一个字节都没回来。
            //    修法不是"再多补一个前缀"，而是把"哪些行算没成功"收成一份判据（SnapshotManager）。
            var summary = SnapshotManager.SummarizeRestoreReport(report);
            // 状态栏与事件栏共用同一句（同源文案）：有失败或没验证到 ⇒ 说的是「回滚未完成」，
            //   「成功 0 / 失败 0」这种看着没事的句子不复存在。
            string summaryText = SnapshotManager.RollbackSummaryText(summary);

            // 进度窗必须在结果框之前收掉：两个框叠着会互相抢焦点，用户也读不清结果。
            // 之后还有刷新插件列表这类收尾动作，那时主窗已经恢复可用（关窗时还原）。
            CloseRollbackProgress();

            ConsoleStatusText.Text = summaryText;
            // 事件里也把插件这一步的实际结果带上：否则"界面显示回滚成功、实际包没退"仍会从事件栏漏掉
            AddEvent($"已从快照{(restoreVersion || restorePlugins ? "完整" : "")}回滚（{snap.LocalTime} [{snap.KindLabel}]）：{summaryText}"
                     + (pluginNote.Length > 0 ? $"；{pluginNote}" : ""),
                     RollbackEventKind(summary.Failed));   // 回滚 = 橙色；有失败则红色
            GuardDialog.Show(
                string.Join(Environment.NewLine, report) +
                "\n\n回滚完成后需重启 DSH 才会生效。",
                summary.FullyRestored ? "回滚结果" : "回滚未完成", MessageBoxButton.OK,
                // ★ 图标跟着同一个判据走：只要"有一项没还原成功 / 没验证到"就不能是 Information，
                //   否则文案说了没完成、图标还说"没事"，又是一次撒谎。
                summary.FullyRestored ? MessageBoxImage.Information
                                      : (summary.Failed > 0 ? MessageBoxImage.Error : MessageBoxImage.Warning));
            await RefreshPluginsAsync(true);
        }
        catch (Exception ex)
        {
            Logger.LogError("DoRestoreAsync", ex);
            ConsoleStatusText.Text = "回滚没能完成，可重试一次（" + LogPromise("详细原因已记入日志，可在「日志」页查看。") + "）";
        }
        finally
        {
            // 兜底：无论成功、失败、异常（以及将来若支持中途取消），进度窗都需关闭、主窗都需恢复可用。
            // CloseRollbackProgress 是幂等的 —— 正常路径上上面已经关过一次，这里再调不会出错。
            CloseRollbackProgress();
            // 写闸同一条 finally 收尾：上面任何一步抛异常、或将来加了提前 return，闸门都不会永远关着。
            EndPluginWriteState();
        }
    }

    // ═══ 应用内事件（状态页「事件信息」）+ 状态页信息填充 ═══

    /// <summary>事件等级：决定「事件信息」列表中该行的颜色。</summary>
    internal enum EventKind
    {
        /// <summary>普通操作（灰白）。</summary>
        Info,
        /// <summary>探测到更新 / 保存快照（淡蓝）。</summary>
        Update,
        /// <summary>安装插件 / 切到新版本（绿）。</summary>
        Good,
        /// <summary>回滚版本 / 停用插件（橙）。</summary>
        Warn,
        /// <summary>失败 / 异常（红）。</summary>
        Bad,
        /// <summary>口头禅与彩蛋：颜色取当前随机色。</summary>
        Mascot
    }

    private static readonly List<(string Text, EventKind Kind, Color? Color)> _appEvents = new();
    private static MainWindow? _liveInstance;
    private static readonly object _eventGate = new();

    /// <summary>回滚事件的颜色：成功=橙（回滚类），有失败=红。抽成纯函数便于自检。</summary>
    internal static EventKind RollbackEventKind(int failCount) => failCount > 0 ? EventKind.Bad : EventKind.Warn;

    /// <summary>自检用：最近一条事件的等级。</summary>
    internal static EventKind LastEventKindForTest()
    {
        lock (_eventGate)
            return _appEvents.Count > 0 ? _appEvents[^1].Kind : EventKind.Info;
    }

    /// <summary>
    /// 自检用：最近一条事件的文本与等级（读同一条，避免分两次读被新事件插入而撕裂）。
    /// 为什么必须是 internal 不能是 public：返回类型用到了 <see cref="EventKind"/>，
    /// 而它是 MainWindow 的 internal 嵌套枚举 —— 写成 public 会直接触发 CS0051（可访问性不一致）。
    /// </summary>
    internal static (string Text, EventKind Kind) LastEventForTest()
    {
        lock (_eventGate)
            return _appEvents.Count > 0 ? (_appEvents[^1].Text, _appEvents[^1].Kind) : ("", EventKind.Info);
    }

    /// <summary>记录一条应用内事件（供状态页展示；跨 partial 文件可用）。</summary>
    internal static void AddEvent(string msg, EventKind kind = EventKind.Info)
    {
        try
        {
            lock (_eventGate)
            {
                _appEvents.Add(($"[{DateTime.Now:HH:mm:ss}] {msg}", kind, null));
                if (_appEvents.Count > 100) _appEvents.RemoveRange(0, _appEvents.Count - 100);
            }
            // 事件需实时显示，不能等到切回状态页才刷新
            _liveInstance?.RefreshEventsSoon();

            // 「最近事件」有变动 -> 逐条掷彩蛋骰（彩蛋自身那条不再掷，避免递归）
            if (kind != EventKind.Mascot)
            {
                string? egg = Mascot.Roll();
                if (egg != null)
                {
                    string stamped = $"[{DateTime.Now:HH:mm:ss}] {egg}";
                    lock (_eventGate)
                    {
                        _appEvents.Add((stamped, EventKind.Mascot, Mascot.CurrentColor));
                        if (_appEvents.Count > 100) _appEvents.RemoveRange(0, _appEvents.Count - 100);
                    }
                    _liveInstance?.ApplyMascot();
                    _liveInstance?.RefreshEventsSoon();
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// 把口头禅同步到三处：底端文字、说明页文字、最近事件里那条。
    /// 常态 = 各处的原样颜色与格式；彩蛋 = 随机色 + 随机格式（三处一致）。
    /// </summary>
    internal void ApplyMascot()
    {
        try
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(ApplyMascot));
                return;
            }

            var state = Mascot.State();     // 一次取走：避免"旧文案配新颜色"的撕裂
            string line = state.Line;
            bool egg = state.IsEgg;
            var fmt = state.Format;
            var eggBrush = new SolidColorBrush(state.Color);
            var footerBrush = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93));   // 底端原本的颜色
            var aboutBrush = new SolidColorBrush(Color.FromRgb(0xC7, 0xC7, 0xCC));    // 说明页正文色

            if (MascotFooter != null)
            {
                MascotFooter.Text = line;
                MascotFooter.Foreground = egg ? eggBrush : footerBrush;
                MascotFooter.FontSize = egg ? fmt.Size : 12;
                MascotFooter.FontWeight = egg ? fmt.Weight : FontWeights.Normal;
                MascotFooter.FontStyle = egg && fmt.Italic ? FontStyles.Italic : FontStyles.Normal;
                MascotFooter.FontStretch = egg ? fmt.Stretch : FontStretches.Normal;
                MascotFooter.TextDecorations = egg && fmt.Underline ? TextDecorations.Underline : null;
            }
            if (_aboutMascotLine != null)
            {
                _aboutMascotLine.Text = line;
                _aboutMascotLine.Foreground = egg ? eggBrush : aboutBrush;
                _aboutMascotLine.FontSize = egg ? fmt.Size : 12;
                _aboutMascotLine.FontWeight = egg ? fmt.Weight : FontWeights.Normal;
                _aboutMascotLine.FontStyle = egg && fmt.Italic ? FontStyles.Italic : FontStyles.Normal;
                _aboutMascotLine.FontStretch = egg ? fmt.Stretch : FontStretches.Normal;
                _aboutMascotLine.TextDecorations = egg && fmt.Underline ? TextDecorations.Underline : null;
            }
        }
        catch { }
    }

    /// <summary>说明页那句口头禅的引用（实例字段：随窗口一起回收，说明页重建时刷新；未渲染时为 null）。</summary>
    private TextBlock? _aboutMascotLine;

    internal void SetAboutMascotLine(TextBlock? t) => _aboutMascotLine = t;

    /// <summary>自检用：最近事件里最后一条口头禅/彩蛋的文本。</summary>
    internal static string? LastMascotEventForTest()
    {
        lock (_eventGate)
        {
            for (int i = _appEvents.Count - 1; i >= 0; i--)
                if (_appEvents[i].Kind == EventKind.Mascot) return _appEvents[i].Text;
        }
        return null;
    }

    /// <summary>自检用：当前事件条数。</summary>
    internal static int EventCountForTest()
    {
        lock (_eventGate) return _appEvents.Count;
    }

    /// <summary>在 UI 线程刷新「最近事件」；该面板位于右栏，各视图均可见，因此不能只在状态页刷新。</summary>
    private void RefreshEventsSoon()
    {
        try
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(RefreshEventsSoon));
                return;
            }
            RefreshStatusEvents();
        }
        catch { }
    }

    private void RefreshStatusEvents()
    {
        if (StatusEventsPanel == null) return;
        try
        {
            // 先加锁取副本再渲染：事件可能由后台线程写入（例如动作前存快照），直接遍历会与并发写入冲突
            List<(string Text, EventKind Kind, Color? Color)> items;
            lock (_eventGate) items = new List<(string Text, EventKind Kind, Color? Color)>(_appEvents);

            StatusEventsPanel.Children.Clear();
            if (items.Count == 0)
            {
                StatusEventsPanel.Children.Add(EventLine("（暂无事件，可启动引擎查看）"));
                return;
            }
            // 倒序显示：最新事件在最上方
            foreach (var e in items.Skip(Math.Max(0, items.Count - 12)).Reverse())
                StatusEventsPanel.Children.Add(EventLine(e.Text, e.Kind, e.Color));
        }
        catch (Exception ex) { Logger.LogError("RefreshStatusEvents", ex); }
    }

    /// <summary>事件配色：淡蓝=更新/快照，绿=安装/升级，橙=回滚/停用，红=异常/失败，灰白=其余；口头禅取随机色。</summary>
    internal static Color EventColor(EventKind kind) => kind switch
    {
        EventKind.Bad => Color.FromRgb(0xFF, 0x45, 0x3A),           // 红：失败 / 异常
        EventKind.Warn => Color.FromRgb(0xFF, 0x9F, 0x0A),     // 橙
        EventKind.Good => Color.FromRgb(0x34, 0xC7, 0x59),     // 绿
        EventKind.Update => Color.FromRgb(0x5A, 0xC8, 0xFA),   // 淡蓝
        EventKind.Mascot => Mascot.CurrentColor,               // 随机色（口头禅/彩蛋）
        _ => Color.FromRgb(0xA8, 0xA8, 0xB0)                   // 灰白
    };

    private static TextBlock EventLine(string text, EventKind kind = EventKind.Info, Color? color = null) => new()
    {
        Text = text,
        FontSize = 11.5,
        Foreground = new SolidColorBrush(color ?? EventColor(kind)),
        FontFamily = new FontFamily("Consolas"),
        Margin = new Thickness(0, 1, 0, 1),
        TextWrapping = TextWrapping.Wrap
    };

    /// <summary>填充设置页「路径」标签页（逐行只读展示，行尾按钮在资源管理器中打开对应路径）。</summary>
    private void RefreshEnvInfo()
    {
        if (PathExeBox == null) return;
        try
        {
            PathExeBox.Text = AppContext.BaseDirectory;
            PathLogsBox.Text = Logger.OpenLogFolderPath;
            PathSnapBox.Text = SnapshotManager.SnapshotRoot;
            PathProfileBox.Text = PluginManager.ProfileDir;
            if (PathDiagBox != null) PathDiagBox.Text = DiagnosticsDir();
            // 「本次记录」行已移除（当前日志可在「日志」页查看）
            if (PathConfigBox != null) PathConfigBox.Text = GuardPaths.ConfigDir;
            if (PathCacheBox != null) PathCacheBox.Text = GuardPaths.CacheDir;
        }
        catch (Exception ex) { Logger.LogError("RefreshEnvInfo", ex); }
    }

    private void RefreshLogPreview()
    {
        if (LogPreviewBox == null) return;
        try
        {
            // 倒序显示：最新一行在最上方
            var last = _logLines.Skip(Math.Max(0, _logLines.Count - 6)).Reverse().ToList();
            LogPreviewBox.Text = last.Count == 0
                ? "（启动引擎后这里显示最近的输出）"
                : string.Join(Environment.NewLine, last);
            LogPreviewBox.ScrollToHome();
        }
        catch { }
    }


    private void FadeInView(UIElement view)
    {
        try
        {
            var tt = view.RenderTransform as TranslateTransform ?? new TranslateTransform(0, 12);
            view.RenderTransform = tt;
            view.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(170)));
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)));
        }
        catch { }
    }

    // ═══ 最大化按钮悬停 -> 分屏布局选择（仿 Win11 Snap Layouts） ═══
    private DispatcherTimer? _snapHideTimer;

    private void MaximizeButton_MouseEnter(object sender, MouseEventArgs e)
    {
        AnimateTraffic(sender as Border, true);
        _snapHideTimer?.Stop();
        SnapPopup.Visibility = Visibility.Visible;
    }

    private void MaximizeButton_MouseLeave(object sender, MouseEventArgs e)
    {
        AnimateTraffic(sender as Border, false);
        // 延迟 450ms 收起，留出指针移入弹层的时间
        _snapHideTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _snapHideTimer.Tick -= SnapHideTick;
        _snapHideTimer.Tick += SnapHideTick;
        _snapHideTimer.Stop();
        _snapHideTimer.Start();
    }

    private void SnapHideTick(object? sender, EventArgs e)
    {
        _snapHideTimer?.Stop();
        if (!SnapPopup.IsMouseOver) SnapPopup.Visibility = Visibility.Collapsed;
    }

    private void SnapPopup_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!SnapPopup.IsMouseOver) SnapPopup.Visibility = Visibility.Collapsed;
    }

    /// <summary>应用分屏布局：Tag 形如 "x,y,w,h"（占工作区的比例）。</summary>
    private void SnapOption_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (sender is Border b && b.Tag is string spec)
            {
                var parts = spec.Split(',');
                if (parts.Length == 4 &&
                    double.TryParse(parts[0], out double x) && double.TryParse(parts[1], out double y) &&
                    double.TryParse(parts[2], out double w) && double.TryParse(parts[3], out double h))
                {
                    SaveRestoreRectIfNeeded(); // 分屏前记录原始矩形，供「最大化」还原
                    var wa = SystemParameters.WorkArea;
                    Width = Math.Round(wa.Width * w);
                    Height = Math.Round(wa.Height * h);
                    Left = wa.Left + Math.Round(wa.Width * x);
                    Top = wa.Top + Math.Round(wa.Height * y);
                    bool full = w >= 1 && h >= 1;
                    _isMaximized = full;
                    MaxBtnIcon.Text = full ? "\uE923" : "\uE922";
                    AddEvent($"窗口布局 → {b.ToolTip}");
                }
            }
        }
        catch (Exception ex) { Logger.LogError("SnapOption_Click", ex); }
        finally { SnapPopup.Visibility = Visibility.Collapsed; }
    }
}
