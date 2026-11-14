#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shell;

namespace DSHGuard.Unified;

/// <summary>
/// 单模型精细配置弹窗：支持启用开关、显示名称、模态/工具能力、推理等级、协议思考覆盖、自定义请求体参数、上下文窗口与最大输出配置。
/// </summary>
internal sealed class DshModelConfigDialog : Window
{
    private readonly DshModelConfig _targetModel;
    private readonly string _protocol;

    private bool _enabled;
    private string _reasoningMode;
    private readonly Dictionary<string, CheckBox> _levelChecks = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Button> _modeButtons = new();

    private readonly Border _switchTrack;
    private readonly Border _switchThumb;
    private readonly TextBlock _switchStateText;

    private readonly TextBox _nameBox;
    private readonly CheckBox _imageCheck;
    private readonly CheckBox _toolCheck;
    private readonly StackPanel _levelsHost;

    private readonly ComboBox? _adaptiveCombo;
    private readonly ComboBox? _thinkingFormatCombo;
    private readonly CheckBox? _reasoningEffortCheck;

    private readonly Dictionary<string, string> _workingKwargs;
    private readonly TextBlock _kwargsSummary;
    private readonly Border _kwargsEditorHost;
    private readonly TextBox _kwargsBox;
    private bool _kwargsExpanded;

    private readonly TextBox _contextBox;
    private readonly TextBox _maxTokensBox;
    private readonly TextBlock _errorText;

    private static readonly Color SwitchOffFallback = Color.FromRgb(0x3E, 0x45, 0x52);

    public DshModelConfigDialog(string providerTitle, string protocol, DshModelConfig model)
    {
        _targetModel = model ?? throw new ArgumentNullException(nameof(model));
        _protocol = string.IsNullOrWhiteSpace(protocol) ? "openai-completions" : protocol.Trim().ToLowerInvariant();

        var snap = model.Clone();
        _enabled = snap.Enabled;
        _reasoningMode = string.IsNullOrWhiteSpace(snap.ReasoningMode) ? "inherit" : snap.ReasoningMode;
        _workingKwargs = snap.CustomKwargs != null
            ? new Dictionary<string, string>(snap.CustomKwargs, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);

        Title = $"编辑 {providerTitle} / {snap.Id}";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 760;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            CornerRadius = new CornerRadius(14),
            GlassFrameThickness = new Thickness(0),
            ResizeBorderThickness = new Thickness(0),
        });

        var opaqueSurface = ThemeManager.IsDark
            ? Color.FromRgb(0x1E, 0x22, 0x2C)
            : Color.FromRgb(0xF8, 0xFA, 0xFC);

        var shell = new Border
        {
            Margin = new Thickness(14),
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(opaqueSurface),
            Effect = new DropShadowEffect
            {
                BlurRadius = 22,
                ShadowDepth = 4,
                Opacity = ThemeManager.IsDark ? 0.58 : 0.22,
                Color = Colors.Black,
            },
        };
        DshWidgets.BindToken(shell, Border.BorderBrushProperty, "Brush.Border.Default", DshWidgets.Border);

        var rootGrid = new Grid();
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 顶栏
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 内容区
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 底栏

        // ===== 1. 顶栏 =====
        var headerBorder = new Border
        {
            Padding = new Thickness(20, 14, 14, 12),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Background = Brushes.Transparent,
        };
        DshWidgets.BindToken(headerBorder, Border.BorderBrushProperty, "Brush.Border.Subtle", DshWidgets.Divider);
        headerBorder.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try { DragMove(); } catch { }
            }
        };

        var headerDock = new DockPanel { LastChildFill = true };
        var closeBtn = new Button
        {
            Content = "✕",
            Width = 28,
            Height = 28,
            FontSize = 13,
            Cursor = Cursors.Hand,
            ToolTip = "关闭 (Esc)",
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Top,
        };
        DshWidgets.BindToken(closeBtn, Control.ForegroundProperty, "Brush.Text.Secondary", DshWidgets.TextSecondary);
        RoundBtn.Apply(closeBtn);
        closeBtn.Click += (_, _) =>
        {
            DialogResult = false;
            Close();
        };
        DockPanel.SetDock(closeBtn, Dock.Right);
        headerDock.Children.Add(closeBtn);

        var titleStack = new StackPanel();
        var titleBlock = new TextBlock
        {
            Text = $"编辑 {providerTitle} / {snap.Id}",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        DshWidgets.BindToken(titleBlock, TextBlock.ForegroundProperty, "Brush.Text.Primary", DshWidgets.TextPrimary);
        titleStack.Children.Add(titleBlock);

        var subTitleBlock = new TextBlock
        {
            Text = $"模型 ID：{snap.Id}  ·  协议：{_protocol}",
            FontSize = 11,
            Margin = new Thickness(0, 3, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        DshWidgets.BindToken(subTitleBlock, TextBlock.ForegroundProperty, "Brush.Text.Tertiary", DshWidgets.TextTertiary);
        titleStack.Children.Add(subTitleBlock);
        headerDock.Children.Add(titleStack);
        headerBorder.Child = headerDock;
        Grid.SetRow(headerBorder, 0);
        rootGrid.Children.Add(headerBorder);

        // ===== 2. 内容滚动区 =====
        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 590,
            Padding = new Thickness(20, 8, 20, 8),
        };
        var body = new StackPanel();

        // 2.1 启用开关
        _switchThumb = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            Background = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(2, 0, 2, 0),
        };
        _switchTrack = new Border
        {
            Width = 42,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Child = _switchThumb,
        };
        _switchStateText = new TextBlock
        {
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        DshWidgets.BindToken(_switchStateText, TextBlock.ForegroundProperty, "Brush.Text.Secondary", DshWidgets.TextSecondary);
        _switchTrack.MouseLeftButtonDown += (_, _) =>
        {
            _enabled = !_enabled;
            RefreshSwitchVisual();
        };
        RefreshSwitchVisual();

        var switchWrap = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        switchWrap.Children.Add(_switchStateText);
        switchWrap.Children.Add(_switchTrack);
        body.Children.Add(BuildSettingRow("启用模型", "关闭后保留模型参数快照，仅从生效路由配置中暂隐。", switchWrap));
        body.Children.Add(BuildDivider());

        // 2.2 显示名称
        var (nameHost, nameBox) = DshWidgets.Input("显示名称（可选，如 Claude Sonnet 4.5）", double.NaN);
        _nameBox = nameBox;
        _nameBox.Text = snap.Name ?? string.Empty;
        body.Children.Add(BuildSettingRow("显示名称", "面板中展示的名称；留空则直接使用模型 ID。", nameHost, stackVertically: true));
        body.Children.Add(BuildDivider());

        // 2.3 模型能力
        var capPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var textCheck = CreateThemedCheckBox("文本", true, new Thickness(0, 0, 14, 0));
        textCheck.IsEnabled = false;
        textCheck.Opacity = 0.72;
        textCheck.ToolTip = "文本输出为核心基础能力，默认必选";

        _imageCheck = CreateThemedCheckBox("图像", snap.SupportsImage, new Thickness(0, 0, 14, 0));
        _toolCheck = CreateThemedCheckBox("工具使用", snap.ToolUse, new Thickness(0));
        capPanel.Children.Add(textCheck);
        capPanel.Children.Add(_imageCheck);
        capPanel.Children.Add(_toolCheck);
        body.Children.Add(BuildSettingRow("模型能力", "模型支持的输入模态与工具调用能力。", capPanel));
        body.Children.Add(BuildDivider());

        // 2.4 推理等级与思考配置
        var reasoningStack = new StackPanel();
        var modeRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        AddModePill(modeRow, "inherit", "跟随提供商默认");
        AddModePill(modeRow, "custom", "开启并自定义档位");
        AddModePill(modeRow, "disabled", "关闭推理 (false)");
        reasoningStack.Children.Add(modeRow);

        _levelsHost = new StackPanel { Margin = new Thickness(0, 2, 0, 6) };
        var levelsTitle = new TextBlock
        {
            Text = "允许的推理强度档位：",
            FontSize = 11.5,
            Margin = new Thickness(0, 0, 0, 6),
        };
        DshWidgets.BindToken(levelsTitle, TextBlock.ForegroundProperty, "Brush.Text.Secondary", DshWidgets.TextSecondary);
        _levelsHost.Children.Add(levelsTitle);

        var levelsWrap = new WrapPanel();
        var activeLevels = new HashSet<string>(
            snap.ReasoningLevels ?? DshModelConfig.AllowedReasoningLevels.ToList(),
            StringComparer.OrdinalIgnoreCase);
        foreach (var lv in DshModelConfig.AllowedReasoningLevels)
        {
            var cb = CreateThemedCheckBox(lv, activeLevels.Contains(lv), new Thickness(0, 0, 14, 6));
            _levelChecks[lv] = cb;
            levelsWrap.Children.Add(cb);
        }
        _levelsHost.Children.Add(levelsWrap);
        reasoningStack.Children.Add(_levelsHost);
        RefreshReasoningModeVisual();

        // 协议专项覆盖（Anthropic Adaptive Thinking / OpenAI thinkingFormat + supportsReasoningEffort）
        if (_protocol == "anthropic-messages")
        {
            var adaptiveRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0), LastChildFill = true };
            var adaptiveLbl = new TextBlock
            {
                Text = "Adaptive Thinking：",
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            DshWidgets.BindToken(adaptiveLbl, TextBlock.ForegroundProperty, "Brush.Text.Secondary", DshWidgets.TextSecondary);
            DockPanel.SetDock(adaptiveLbl, Dock.Left);
            adaptiveRow.Children.Add(adaptiveLbl);

            _adaptiveCombo = DshWidgets.Combo("跟随提供商默认", "强制开启 Adaptive Thinking", "强制关闭 Adaptive Thinking");
            _adaptiveCombo.Height = 30;
            _adaptiveCombo.SelectedIndex = snap.ForceAdaptiveThinking switch
            {
                true => 1,
                false => 2,
                _ => 0,
            };
            adaptiveRow.Children.Add(_adaptiveCombo);
            reasoningStack.Children.Add(adaptiveRow);
        }
        else if (_protocol is "openai-completions" or "openai-responses")
        {
            var tfRow = new DockPanel { Margin = new Thickness(0, 6, 0, 6), LastChildFill = true };
            var tfLbl = new TextBlock
            {
                Text = "思考参数格式：",
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            DshWidgets.BindToken(tfLbl, TextBlock.ForegroundProperty, "Brush.Text.Secondary", DshWidgets.TextSecondary);
            DockPanel.SetDock(tfLbl, Dock.Left);
            tfRow.Children.Add(tfLbl);

            _thinkingFormatCombo = DshWidgets.Combo("跟随提供商默认", "openai", "qwen", "zai", "qwen-chat-template");
            _thinkingFormatCombo.Height = 30;
            var curTf = (snap.ThinkingFormat ?? string.Empty).Trim();
            int tfIdx = 0;
            for (int i = 1; i < _thinkingFormatCombo.Items.Count; i++)
            {
                if (string.Equals(_thinkingFormatCombo.Items[i]?.ToString(), curTf, StringComparison.OrdinalIgnoreCase))
                {
                    tfIdx = i;
                    break;
                }
            }
            _thinkingFormatCombo.SelectedIndex = tfIdx;
            tfRow.Children.Add(_thinkingFormatCombo);
            reasoningStack.Children.Add(tfRow);

            _reasoningEffortCheck = CreateThemedCheckBox(
                "支持 reasoning_effort 字段（半选=跟随默认，勾选=支持，取消=关闭）",
                snap.SupportsReasoningEffort,
                new Thickness(0, 2, 0, 0),
                threeState: true);
            reasoningStack.Children.Add(_reasoningEffortCheck);
        }
        else if (_protocol == "google-generative-ai")
        {
            var googleHint = new TextBlock
            {
                Text = "Google 原生协议通过提供商级 thinkingBudgets 与内置思考参数自动协同。",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            };
            DshWidgets.BindToken(googleHint, TextBlock.ForegroundProperty, "Brush.Text.Tertiary", DshWidgets.TextTertiary);
            reasoningStack.Children.Add(googleHint);
        }

        body.Children.Add(BuildSettingRow("推理等级与思考配置", "为当前模型覆盖推理能力开关、可用档位及协议思考格式。", reasoningStack, stackVertically: true));
        body.Children.Add(BuildDivider());

        // 2.5 自定义请求体参数
        var kwargsHeaderRight = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _kwargsSummary = new TextBlock
        {
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        DshWidgets.BindToken(_kwargsSummary, TextBlock.ForegroundProperty, "Brush.Text.Secondary", DshWidgets.TextSecondary);
        var editKwargsBtn = DshWidgets.SmallButton("修改");
        kwargsHeaderRight.Children.Add(_kwargsSummary);
        kwargsHeaderRight.Children.Add(editKwargsBtn);

        _kwargsBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            Height = 88,
            Padding = new Thickness(8, 6, 8, 6),
            FontFamily = new FontFamily("Consolas, Cascadia Mono, Microsoft YaHei UI"),
            FontSize = 12,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Text = FormatKwargsText(_workingKwargs),
        };
        DshWidgets.BindToken(_kwargsBox, Control.ForegroundProperty, "Brush.Text.Primary", DshWidgets.TextPrimary);
        DshWidgets.BindToken(_kwargsBox, TextBox.CaretBrushProperty, "Brush.Text.Primary", DshWidgets.TextPrimary);

        _kwargsEditorHost = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed,
            Child = _kwargsBox,
        };
        DshWidgets.BindToken(_kwargsEditorHost, Border.BackgroundProperty, "Brush.Surface.Input", DshWidgets.FieldBg);
        DshWidgets.BindToken(_kwargsEditorHost, Border.BorderBrushProperty, "Brush.Border.Default", DshWidgets.Border);

        _kwargsBox.TextChanged += (_, _) =>
        {
            ParseKwargsIntoWorking(_kwargsBox.Text);
            RefreshKwargsSummary();
        };

        editKwargsBtn.Click += (_, _) =>
        {
            _kwargsExpanded = !_kwargsExpanded;
            _kwargsEditorHost.Visibility = _kwargsExpanded ? Visibility.Visible : Visibility.Collapsed;
            editKwargsBtn.Content = _kwargsExpanded ? "收起" : "修改";
            if (_kwargsExpanded) _kwargsBox.Focus();
        };
        RefreshKwargsSummary();

        var kwargsHint = _protocol == "openai-completions"
            ? "写入 compat.chatTemplateKwargs，每行一条 key=value（如 enable_thinking=true）。"
            : "当前协议非 openai-completions，条目仍会保留，仅 openai-completions 时透传请求体。";
        var kwargsRowStack = new StackPanel();
        kwargsRowStack.Children.Add(BuildSettingRow("自定义请求体参数", kwargsHint, kwargsHeaderRight));
        kwargsRowStack.Children.Add(_kwargsEditorHost);
        body.Children.Add(kwargsRowStack);
        body.Children.Add(BuildDivider());

        // 2.6 上下文窗口大小
        var (ctxHost, ctxBox) = DshWidgets.Input("0（自动）", 120);
        _contextBox = ctxBox;
        _contextBox.Text = snap.ContextWindow > 0 ? snap.ContextWindow.ToString(CultureInfo.InvariantCulture) : "0";
        var ctxControl = BuildNumberPresetPanel(ctxHost, _contextBox, new (string Label, int Value)[]
        {
            ("默认", 0),
            ("128K", 128000),
            ("200K", 200000),
            ("400K", 400000),
            ("1M", 1000000),
        });
        body.Children.Add(BuildSettingRow("模型上下文窗口大小", "最大上下文 Token 大小；填 0 表示使用引擎默认值。", ctxControl, stackVertically: true));
        body.Children.Add(BuildDivider());

        // 2.7 最大输出 Token
        var (maxHost, maxBox) = DshWidgets.Input("0（自动）", 120);
        _maxTokensBox = maxBox;
        _maxTokensBox.Text = snap.MaxTokens > 0 ? snap.MaxTokens.ToString(CultureInfo.InvariantCulture) : "0";
        var maxControl = BuildNumberPresetPanel(maxHost, _maxTokensBox, new (string Label, int Value)[]
        {
            ("默认", 0),
            ("8K", 8192),
            ("16K", 16384),
            ("32K", 32768),
            ("64K", 65536),
        });
        body.Children.Add(BuildSettingRow("最大输出 Token", "单次生成的最大输出 Token 数；填 0 表示使用引擎默认值。", maxControl, stackVertically: true));

        scroller.Content = body;
        Grid.SetRow(scroller, 1);
        rootGrid.Children.Add(scroller);

        // ===== 3. 底栏 =====
        var footerBorder = new Border
        {
            Padding = new Thickness(20, 12, 20, 16),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Background = Brushes.Transparent,
        };
        DshWidgets.BindToken(footerBorder, Border.BorderBrushProperty, "Brush.Border.Subtle", DshWidgets.Divider);

        var footerDock = new DockPanel { LastChildFill = true };
        var btnBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var cancelBtn = DshWidgets.SmallButton("取消");
        cancelBtn.Margin = new Thickness(0, 0, 8, 0);
        cancelBtn.Padding = new Thickness(16, 6, 16, 6);
        cancelBtn.Click += (_, _) =>
        {
            DialogResult = false;
            Close();
        };

        var saveBtn = DshWidgets.SmallButton("保存", "primary");
        saveBtn.Padding = new Thickness(18, 6, 18, 6);
        saveBtn.Click += (_, _) => TryCommitAndClose();

        btnBar.Children.Add(cancelBtn);
        btnBar.Children.Add(saveBtn);
        DockPanel.SetDock(btnBar, Dock.Right);
        footerDock.Children.Add(btnBar);

        _errorText = new TextBlock
        {
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        DshWidgets.BindToken(_errorText, TextBlock.ForegroundProperty, "Brush.Semantic.Danger", DshWidgets.Danger);
        footerDock.Children.Add(_errorText);

        footerBorder.Child = footerDock;
        Grid.SetRow(footerBorder, 2);
        rootGrid.Children.Add(footerBorder);

        shell.Child = rootGrid;
        Content = shell;

        ThemeManager.ApplyTo(this);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                DialogResult = false;
                Close();
                e.Handled = true;
            }
        };
    }

    private UIElement BuildSettingRow(string title, string description, UIElement control, bool stackVertically = false)
    {
        if (stackVertically)
        {
            var stack = new StackPanel { Margin = new Thickness(0, 10, 0, 10) };
            var titleBlock = new TextBlock
            {
                Text = title,
                FontSize = 13,
                FontWeight = FontWeights.Medium,
            };
            DshWidgets.BindToken(titleBlock, TextBlock.ForegroundProperty, "Brush.Text.Primary", DshWidgets.TextPrimary);
            stack.Children.Add(titleBlock);

            var descBlock = new TextBlock
            {
                Text = description,
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 8),
            };
            DshWidgets.BindToken(descBlock, TextBlock.ForegroundProperty, "Brush.Text.Tertiary", DshWidgets.TextTertiary);
            stack.Children.Add(descBlock);
            stack.Children.Add(control);
            return stack;
        }

        var row = new Grid { Margin = new Thickness(0, 10, 0, 10) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var t = new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.Medium,
        };
        DshWidgets.BindToken(t, TextBlock.ForegroundProperty, "Brush.Text.Primary", DshWidgets.TextPrimary);
        left.Children.Add(t);

        var d = new TextBlock
        {
            Text = description,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        };
        DshWidgets.BindToken(d, TextBlock.ForegroundProperty, "Brush.Text.Tertiary", DshWidgets.TextTertiary);
        left.Children.Add(d);

        Grid.SetColumn(left, 0);
        if (control is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 2);
        row.Children.Add(left);
        row.Children.Add(control);
        return row;
    }

    private static UIElement BuildDivider()
    {
        var line = new Border
        {
            Height = 1,
            Margin = new Thickness(0),
        };
        DshWidgets.BindToken(line, Border.BackgroundProperty, "Brush.Border.Subtle", DshWidgets.Divider);
        return line;
    }

    private static CheckBox CreateThemedCheckBox(string label, bool? isChecked, Thickness margin, bool threeState = false)
    {
        var labelTb = new TextBlock
        {
            Text = label,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DshWidgets.BindToken(labelTb, TextBlock.ForegroundProperty, "Brush.Text.Primary", DshWidgets.TextPrimary);
        var cb = new CheckBox
        {
            Content = labelTb,
            IsChecked = isChecked,
            IsThreeState = threeState,
            Margin = margin,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DshWidgets.BindToken(cb, Control.ForegroundProperty, "Brush.Text.Primary", DshWidgets.TextPrimary);
        return cb;
    }

    private void AddModePill(WrapPanel host, string modeKey, string label)
    {
        var btn = DshWidgets.SmallButton(label);
        btn.Margin = new Thickness(0, 0, 6, 4);
        btn.Tag = modeKey;
        btn.Click += (_, _) =>
        {
            _reasoningMode = modeKey;
            RefreshReasoningModeVisual();
        };
        _modeButtons.Add(btn);
        host.Children.Add(btn);
    }

    private void RefreshReasoningModeVisual()
    {
        _levelsHost.Visibility = string.Equals(_reasoningMode, "custom", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;

        foreach (var btn in _modeButtons)
        {
            var key = btn.Tag as string ?? string.Empty;
            bool active = string.Equals(key, _reasoningMode, StringComparison.OrdinalIgnoreCase);
            if (active)
            {
                DshWidgets.BindToken(btn, Control.BackgroundProperty, "Brush.Accent.Primary", DshWidgets.Accent);
                btn.Foreground = Brushes.White;
                btn.FontWeight = FontWeights.SemiBold;
            }
            else
            {
                DshWidgets.BindToken(btn, Control.BackgroundProperty, "Brush.Surface.Button", DshWidgets.ButtonBg);
                DshWidgets.BindToken(btn, Control.ForegroundProperty, "Brush.Text.Primary", DshWidgets.TextPrimary);
                btn.FontWeight = FontWeights.Normal;
            }
        }
    }

    private void RefreshSwitchVisual()
    {
        if (_enabled)
        {
            _switchTrack.Background = new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x59));
            _switchThumb.HorizontalAlignment = HorizontalAlignment.Right;
            _switchStateText.Text = "已启用";
        }
        else
        {
            DshWidgets.BindToken(_switchTrack, Border.BackgroundProperty, "Brush.Surface.SwitchOff", SwitchOffFallback);
            _switchThumb.HorizontalAlignment = HorizontalAlignment.Left;
            _switchStateText.Text = "已停用";
        }
    }

    private UIElement BuildNumberPresetPanel(Grid inputHost, TextBox box, (string Label, int Value)[] presets)
    {
        var row = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        inputHost.Width = 124;
        inputHost.Margin = new Thickness(0, 0, 8, 4);
        row.Children.Add(inputHost);

        foreach (var (label, val) in presets)
        {
            var pill = DshWidgets.SmallButton(label);
            pill.Margin = new Thickness(0, 0, 6, 4);
            pill.Padding = new Thickness(9, 4, 9, 4);
            pill.Click += (_, _) => box.Text = val.ToString(CultureInfo.InvariantCulture);
            row.Children.Add(pill);
        }
        return row;
    }

    private static string FormatKwargsText(Dictionary<string, string> map)
    {
        if (map.Count == 0) return string.Empty;
        return string.Join(Environment.NewLine, map.Select(kv => $"{kv.Key}={kv.Value}"));
    }

    private void ParseKwargsIntoWorking(string raw)
    {
        _workingKwargs.Clear();
        if (string.IsNullOrWhiteSpace(raw)) return;
        foreach (var rawLine in raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var k = line[..eq].Trim();
            var vRaw = line[(eq + 1)..].Trim();
            if (k.Length == 0) continue;
            _workingKwargs[k] = vRaw;
        }
    }

    private void RefreshKwargsSummary()
    {
        _kwargsSummary.Text = _workingKwargs.Count == 0
            ? "暂无项目"
            : $"{_workingKwargs.Count} 项已配置";
    }

    private void TryCommitAndClose()
    {
        _errorText.Text = string.Empty;

        if (!int.TryParse((_contextBox.Text ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ctxVal) || ctxVal < 0)
        {
            _errorText.Text = "上下文窗口大小必须是非负整数。";
            _contextBox.Focus();
            return;
        }

        if (!int.TryParse((_maxTokensBox.Text ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxVal) || maxVal < 0)
        {
            _errorText.Text = "最大输出 Token 必须是非负整数。";
            _maxTokensBox.Focus();
            return;
        }

        if (_kwargsExpanded)
        {
            ParseKwargsIntoWorking(_kwargsBox.Text);
        }

        _targetModel.Enabled = _enabled;
        var cleanName = (_nameBox.Text ?? string.Empty).Trim();
        _targetModel.Name = cleanName.Length == 0 || string.Equals(cleanName, _targetModel.Id, StringComparison.Ordinal)
            ? string.Empty
            : cleanName;

        _targetModel.SetSupportsImage(_imageCheck.IsChecked == true);
        _targetModel.ToolUse = _toolCheck.IsChecked == true;
        _targetModel.ContextWindow = ctxVal;
        _targetModel.MaxTokens = maxVal;

        _targetModel.ReasoningMode = _reasoningMode;
        if (string.Equals(_reasoningMode, "custom", StringComparison.OrdinalIgnoreCase))
        {
            var chosen = DshModelConfig.AllowedReasoningLevels
                .Where(lv => _levelChecks.TryGetValue(lv, out var cb) && cb.IsChecked == true)
                .ToList();
            if (chosen.Count == 0)
            {
                _errorText.Text = "开启自定义推理时请至少勾选一个推理强度档位。";
                return;
            }
            _targetModel.ReasoningLevels = chosen;
        }

        if (_adaptiveCombo != null)
        {
            _targetModel.ForceAdaptiveThinking = _adaptiveCombo.SelectedIndex switch
            {
                1 => true,
                2 => false,
                _ => null,
            };
        }

        if (_thinkingFormatCombo != null)
        {
            _targetModel.ThinkingFormat = _thinkingFormatCombo.SelectedIndex <= 0
                ? string.Empty
                : (_thinkingFormatCombo.SelectedItem?.ToString() ?? string.Empty);
        }

        if (_reasoningEffortCheck != null)
        {
            _targetModel.SupportsReasoningEffort = _reasoningEffortCheck.IsChecked;
        }

        _targetModel.CustomKwargs = new Dictionary<string, string>(_workingKwargs, StringComparer.Ordinal);

        DialogResult = true;
        Close();
    }
}
