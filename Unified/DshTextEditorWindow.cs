using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shell;

namespace DSHGuard.Unified;

/// <summary>
/// 纯文本编辑器窗口（代码构建，无 XAML）。
///
/// 只负责「把一整份文本改好交回去」——不解析、不校验语法、不做 diff。
/// 校验与落盘交给调用方（<see cref="DshPromptManager.Save"/>），
/// 这样自检可以绕过窗口直接测写入逻辑。
/// 2.4.0：升级为无边框现代圆角半透明深海石板窗口（与主窗 Oceanic Slate 四态质感统一）。
/// </summary>
public sealed class DshTextEditorWindow : Window
{
    private static Color SurfaceBg => ThemeManager.ForCurrentTheme(Color.FromRgb(0x1C, 0x20, 0x29));

    private static Color EditorBg => ThemeManager.IsDark
        ? Color.FromRgb(0x23, 0x29, 0x36)
        : Color.FromRgb(0xE4, 0xE9, 0xF3);

    private static Color Fg => ThemeManager.ForCurrentTheme(DshWidgets.TextPrimary);
    private static Color FgDim => ThemeManager.ForCurrentTheme(DshWidgets.TextSecondary);
    private static Color FgMuted => ThemeManager.ForCurrentTheme(DshWidgets.TextTertiary);

    private static Color CardBorder => ThemeManager.ForCurrentTheme(DshWidgets.Border);

    private static Color Accent => DshWidgets.Accent;

    private readonly TextBox _box;

    /// <summary>用户是否按了「保存」（false = 取消 / 直接关窗）。</summary>
    public bool Saved { get; private set; }

    /// <summary>保存时的文本内容。</summary>
    public string ResultText => _box.Text;

    public DshTextEditorWindow(string title, string hint, string content)
    {
        bool dark = ThemeManager.IsDark;
        Title = title;
        Width = 920;
        Height = 660;
        MinWidth = 640;
        MinHeight = 440;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        UseLayoutRounding = true;

        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            CornerRadius = new CornerRadius(12),
            GlassFrameThickness = new Thickness(0),
            ResizeBorderThickness = new Thickness(6),
            UseAeroCaptionButtons = false,
        });

        var shell = new Border
        {
            Background = new SolidColorBrush(SurfaceBg),
            BorderBrush = new SolidColorBrush(CardBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Margin = new Thickness(12),
            SnapsToDevicePixels = true,
            Effect = new DropShadowEffect
            {
                BlurRadius = 26,
                ShadowDepth = 4,
                Opacity = dark ? 0.5 : 0.22,
                Color = Colors.Black,
            },
        };

        var root = new Grid { Margin = new Thickness(20, 16, 20, 18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 0: 无边框标题栏
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 1: 说明行
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });  // 2: 等宽代码编辑区
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 3: 底部操作栏

        // ── 0. 自绘标题栏（拖拽移动 / 双击最大化 / 右侧圆角关闭键）──
        var titleBar = new Grid
        {
            Background = Brushes.Transparent,
            Margin = new Thickness(0, 0, 0, 8),
        };
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var glyph = new TextBlock
        {
            Text = "\uE943",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 15,
            Foreground = new SolidColorBrush(Accent),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        titleBar.Children.Add(glyph);

        var head = new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Fg),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        DshWidgets.BindToken(head, TextBlock.ForegroundProperty, "Brush.Text.Primary", Fg);
        Grid.SetColumn(head, 1);
        titleBar.Children.Add(head);

        var closeBtn = new Button
        {
            Content = "✕",
            Width = 28,
            Height = 28,
            FontSize = 12,
            Padding = new Thickness(0),
            Foreground = new SolidColorBrush(FgDim),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            ToolTip = "关闭 (Esc)",
        };
        DshWidgets.BindToken(closeBtn, Control.BackgroundProperty, "Brush.Surface.Button", DshWidgets.ButtonBg);
        DshWidgets.BindToken(closeBtn, Control.BorderBrushProperty, "Brush.Border.Default", CardBorder);
        RoundBtn.Apply(closeBtn);
        ButtonFx.Wire(closeBtn);
        closeBtn.Click += (_, _) => { Saved = false; Close(); };
        Grid.SetColumn(closeBtn, 2);
        titleBar.Children.Add(closeBtn);

        titleBar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                return;
            }
            try { DragMove(); } catch { }
        };
        Grid.SetRow(titleBar, 0);
        root.Children.Add(titleBar);

        // ── 1. 说明文字 ──
        var tip = new TextBlock
        {
            Text = hint,
            FontSize = 12,
            LineHeight = 18,
            Foreground = new SolidColorBrush(FgDim),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        };
        DshWidgets.BindToken(tip, TextBlock.ForegroundProperty, "Brush.Text.Secondary", FgDim);
        if (string.IsNullOrWhiteSpace(hint)) tip.Visibility = Visibility.Collapsed;
        Grid.SetRow(tip, 1);
        root.Children.Add(tip);

        // ── 2. 等宽代码编辑区（套用现代圆角输入框模板，关闭垂直居中以顶部对齐）──
        _box = new TextBox
        {
            Text = content,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Background = new SolidColorBrush(EditorBg),
            Foreground = new SolidColorBrush(Fg),
            CaretBrush = new SolidColorBrush(Fg),
            BorderBrush = new SolidColorBrush(CardBorder),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 10, 12, 10),
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"),
            FontSize = 13,
        };
        DshWidgets.BindToken(_box, Control.ForegroundProperty, "Brush.Text.Primary", Fg);
        DshWidgets.BindToken(_box, Control.BackgroundProperty, "Brush.Surface.Input", EditorBg);
        DshWidgets.BindToken(_box, Control.BorderBrushProperty, "Brush.Border.Default", CardBorder);
        DshWidgets.BindToken(_box, TextBox.CaretBrushProperty, "Brush.Text.Primary", Fg);
        DshWidgets.ApplyModernTextBox(_box);
        Grid.SetRow(_box, 2);
        root.Children.Add(_box);

        // ── 3. 底部栏：快捷键提示 + 保存/取消按钮 ──
        var footer = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var shortcutHint = new TextBlock
        {
            Text = "Ctrl+S 保存 · Esc 取消",
            FontSize = 11.5,
            Foreground = new SolidColorBrush(FgMuted),
            VerticalAlignment = VerticalAlignment.Center,
        };
        DshWidgets.BindToken(shortcutHint, TextBlock.ForegroundProperty, "Brush.Text.Tertiary", FgMuted);
        footer.Children.Add(shortcutHint);

        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var save = MakeButton("保存", true);
        save.Click += (_, _) => { Saved = true; Close(); };
        bar.Children.Add(save);

        var cancel = MakeButton("取消", false);
        cancel.Click += (_, _) => { Saved = false; Close(); };
        bar.Children.Add(cancel);

        Grid.SetColumn(bar, 1);
        footer.Children.Add(bar);

        Grid.SetRow(footer, 3);
        root.Children.Add(footer);

        shell.Child = root;
        Content = shell;

        StateChanged += (_, _) =>
        {
            bool max = WindowState == WindowState.Maximized;
            shell.Margin = max ? new Thickness(0) : new Thickness(12);
            shell.CornerRadius = max ? new CornerRadius(0) : new CornerRadius(12);
        };
        Loaded += (_, _) =>
        {
            BlurHelper.SyncThemeChrome(this, ThemeManager.IsDark);
            _box.Focus();
        };

        // Esc = 取消（不保存），Ctrl+S = 保存
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Saved = false;
                Close();
                e.Handled = true;
            }
            else if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                Saved = true;
                Close();
                e.Handled = true;
            }
        };
    }

    private static Button MakeButton(string text, bool primary)
    {
        var b = new Button
        {
            Content = text,
            MinWidth = 92,
            FontSize = 12.5,
            Padding = new Thickness(16, 7, 16, 7),
            Margin = new Thickness(10, 0, 0, 0),
            Background = new SolidColorBrush(primary ? Accent : EditorBg),
            Foreground = new SolidColorBrush(primary ? Colors.White : Fg),
            BorderBrush = new SolidColorBrush(primary ? Accent : CardBorder),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
        };
        if (primary)
        {
            b.BorderThickness = new Thickness(0);
            DshWidgets.BindToken(b, Control.BackgroundProperty, "Brush.Accent.Primary", Accent);
            DshWidgets.BindToken(b, Control.BorderBrushProperty, "Brush.Accent.Primary", Accent);
        }
        else
        {
            DshWidgets.BindToken(b, Control.BackgroundProperty, "Brush.Surface.Button", DshWidgets.ButtonBg);
            DshWidgets.BindToken(b, Control.ForegroundProperty, "Brush.Text.Primary", Fg);
            DshWidgets.BindToken(b, Control.BorderBrushProperty, "Brush.Border.Default", CardBorder);
        }
        RoundBtn.Apply(b);   // 与主程序同一份圆角模板
        ButtonFx.Wire(b);    // 悬停放大提亮、按下缩小变暗
        return b;
    }
}
