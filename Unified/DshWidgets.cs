using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DSHGuard.Unified;

/// <summary>
/// 「综合管理」界面层的小构件工厂。
///
/// 从 <c>MainWindow.Unified.cs</c> 拆出来，原因是那个文件已经 1100+ 行、
/// 界面组装与构件绘制混在一起，改一处文案要滚半屏。
/// 这里只放**无状态**的绘制工具：纯函数、不碰字段、不碰 DSH 资源。
/// </summary>
internal static class DshWidgets
{
    // 配色 · 2.4.0 Oceanic Slate（深海星盘）
    // 与 Themes/Tokens.*.xaml 的夜间值同值；日间目标色登记在 ThemeManager.DarkToLight（八个常量各占一个唯一键）。
    // 告别 2.4.0 之前的「暖紫死黑」不透明底（#1E1E24 / #18181E / #2B2B34）：那一套与主窗的冷调半透明
    // 一个值都不重合，毛玻璃开着时整块「综合管理」像一块不透光的黑板。现在表面全部是冷调半透明层，
    // 四种模式（日/夜 × 毛玻璃/纯色）下都与主窗卡片同一层级关系。
    internal static readonly Color TextPrimary = Color.FromRgb(0xF5, 0xF5, 0xF7);
    internal static readonly Color TextSecondary = Color.FromRgb(0x94, 0xA0, 0xB2);
    internal static readonly Color TextTertiary = Color.FromRgb(0x6D, 0x77, 0x8A);
    internal static readonly Color Accent = Color.FromRgb(0x2B, 0x6B, 0xFF);
    internal static readonly Color Warn = Color.FromRgb(0xFF, 0xB0, 0x2E);
    internal static readonly Color Danger = Color.FromRgb(0xFF, 0x5A, 0x4D);
    internal static readonly Color Ok = Color.FromRgb(0x35, 0xC7, 0x59);
    internal static readonly Color Border = Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF);
    internal static readonly Color Divider = Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF);
    internal static readonly Color CardBg = Color.FromArgb(0x7E, 0x1E, 0x2A, 0x3D);
    internal static readonly Color PanelBg = Color.FromArgb(0x72, 0x0E, 0x14, 0x1F);
    internal static readonly Color FieldBg = Color.FromArgb(0x5C, 0x12, 0x1B, 0x2A);
    internal static readonly Color ButtonBg = Color.FromArgb(0x7E, 0x2A, 0x37, 0x4D);

    /// <summary>焦点高亮色（iOS 系统蓝，两套主题同色）。</summary>
    internal static readonly Color FocusRing = Color.FromRgb(0x00, 0x7A, 0xFF);

    /// <summary>卡片 / 面板统一圆角（与主窗 Card、Primitives.xaml 的 ModernCardStyle 一致）。</summary>
    internal const double CardRadius = 10;

    /// <summary>输入框圆角（与 Primitives.xaml 的 ModernTextBoxStyle 一致）。</summary>
    internal const double InputRadius = 8;

    /// <summary>将控件属性绑定到 <c>Themes/Tokens.*.xaml</c> 语义令牌；无 Application 上下文时回落静态画刷。</summary>
    internal static void BindToken(FrameworkElement element, DependencyProperty property, string tokenKey, Color fallbackColor)
    {
        if (Application.Current != null)
            element.SetResourceReference(property, tokenKey);
        else
            element.SetValue(property, new SolidColorBrush(fallbackColor));
    }

    private static bool TryTokenKeyFor(Color color, out string key)
    {
        if (color == TextPrimary) { key = "Brush.Text.Primary"; return true; }
        if (color == TextSecondary) { key = "Brush.Text.Secondary"; return true; }
        if (color == TextTertiary) { key = "Brush.Text.Tertiary"; return true; }
        if (color == Accent) { key = "Brush.Accent.Primary"; return true; }
        if (color == Ok) { key = "Brush.Semantic.Ok"; return true; }
        if (color == Warn) { key = "Brush.Semantic.Warn"; return true; }
        if (color == Danger) { key = "Brush.Semantic.Danger"; return true; }
        key = "";
        return false;
    }

    /// <summary>一行文字。<paramref name="wrap"/> = true 时换行且不省略。</summary>
    internal static TextBlock Text(string text, double size, Color color,
        FontWeight? weight = null, bool wrap = false)
    {
        var tb = new TextBlock
        {
            Text = text ?? "",
            FontSize = size,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
        };
        if (TryTokenKeyFor(color, out string tokenKey))
            BindToken(tb, TextBlock.ForegroundProperty, tokenKey, color);
        else
            tb.Foreground = new SolidColorBrush(color);
        if (weight.HasValue) tb.FontWeight = weight.Value;
        return tb;
    }

    /// <summary>
    /// 次要说明行（换行）。
    ///
    /// 2026-10 返工：原来这里是 <c>TextTertiary</c>(0x6E6E78) + 10.5px，说明文字不易读。
    /// 统一抬到 <c>TextSecondary</c>(0x9A9AA2) + 11.5px。
    /// 这里放的都是随行诊断（某一项为什么是红的），必须留在原地，不能改成弹窗。
    /// </summary>
    internal static TextBlock Hint(string text, double size = 11.5)
        => Text(text, size, TextSecondary, wrap: true);

    /// <summary>
    /// 单行路径：超过 <paramref name="maxWidth"/> 裁尾显示省略号，鼠标悬停看完整路径。
    ///
    /// 别拿 <see cref="Hint"/> + <c>MaxWidth</c> + <c>TextTrimming.CharacterEllipsis</c>
    /// 凑这个效果：Hint 是 wrap 的（<c>TextWrapping.Wrap</c> + <c>TextTrimming.None</c>），
    /// 在没有高度约束时 CharacterEllipsis 是空转 —— 看着像截断了，其实整段路径都摊在
    /// 那儿把排版顶歪。这里显式走不换行 + 裁剪，并把完整路径挂到 ToolTip 上。
    /// </summary>
    internal static TextBlock PathLine(string path, double maxWidth)
    {
        var tb = Text(path, 9.5, TextTertiary);   // wrap: false → NoWrap + CharacterEllipsis
        tb.MaxWidth = maxWidth;
        tb.ToolTip = path;
        return tb;
    }

    /// <summary>带 ToolTip 的术语解释小图标（? 圆点）。</summary>
    internal static Border InfoDot(string title, string body)
    {
        var b = new Border
        {
            Width = 14,
            Height = 14,
            CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(1),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = string.IsNullOrEmpty(body) ? title : title + "\n\n" + body,
        };
        BindToken(b, System.Windows.Controls.Border.BorderBrushProperty, "Brush.Border.Default", Border);
        var q = Text("?", 9.5, TextTertiary);
        q.HorizontalAlignment = HorizontalAlignment.Center;
        q.VerticalAlignment = VerticalAlignment.Center;
        b.Child = q;
        return b;
    }

    /// <summary>
    /// 小按钮。<paramref name="tone"/>: normal / primary / danger。
    ///
    /// 外观走 <see cref="RoundBtn"/>：和主程序同一份圆角模板，悬停提亮放大、
    /// 按下缩小变暗，不再是一个不会动的方块。内边距按主程序小按钮的口径给足，
    /// 免得字贴着边、几个按钮挤成一团。
    /// </summary>
    internal static Button SmallButton(string label, string tone = "normal")
    {
        var b = new Button
        {
            Content = label,
            FontSize = 11.5,
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 8, 8),
            MinHeight = 28,
            MinWidth = 34,
            Cursor = Cursors.Hand,
            FocusVisualStyle = null,
            BorderThickness = new Thickness(1),
        };
        if (tone == "primary")
        {
            BindToken(b, Control.BackgroundProperty, "Brush.Accent.Primary", Accent);
            b.Foreground = new SolidColorBrush(Colors.White);
            BindToken(b, Control.BorderBrushProperty, "Brush.Accent.Primary", Accent);
            b.BorderThickness = new Thickness(0);
        }
        else if (tone == "danger")
        {
            BindToken(b, Control.BackgroundProperty, "Brush.Surface.DangerButton", Color.FromRgb(0x3A, 0x20, 0x20));
            BindToken(b, Control.ForegroundProperty, "Brush.Semantic.Danger", Danger);
            BindToken(b, Control.BorderBrushProperty, "Brush.Semantic.Danger", Danger);
        }
        else
        {
            BindToken(b, Control.BackgroundProperty, "Brush.Surface.Button", ButtonBg);
            BindToken(b, Control.ForegroundProperty, "Brush.Text.Primary", TextPrimary);
            BindToken(b, Control.BorderBrushProperty, "Brush.Border.Default", Border);
        }
        RoundBtn.Apply(b);
        ButtonFx.Wire(b);
        return b;
    }

    /// <summary>顶栏工具条紧凑按钮（高 28px，右侧间距由左边距控制，对齐插件页工具条）。</summary>
    internal static Button ToolbarButton(string label, string tone = "normal")
    {
        var b = SmallButton(label, tone);
        b.Height = 28;
        b.MinHeight = 28;
        b.FontSize = 12;
        b.Padding = new Thickness(12, 0, 12, 0);
        b.Margin = new Thickness(8, 0, 0, 0);
        b.VerticalAlignment = VerticalAlignment.Center;
        b.VerticalContentAlignment = VerticalAlignment.Center;
        return b;
    }

    /// <summary>
    /// 输入框。
    ///
    /// ⚠ 这里用 WPF 原生的「占位提示」做法，**不能**把提示文字塞进 Text：
    /// 上一版把 placeholder 直接赋给 Text，用户不手动删掉就会当时输入提交进配置
    /// （例如 serverName 变成 "serverName（[A-Za-z0-9_-]，≤32）"）。
    /// 正确做法是空白时用浮层标签提示，一旦有内容就隐藏。
    /// </summary>
    internal static (Grid Host, TextBox Box) Input(string placeholder, double width)
        => BuildInput(placeholder, width, withSearchIcon: false);

    private static (Grid Host, TextBox Box) BuildInput(string placeholder, double width, bool withSearchIcon)
    {
        var host = new Grid { Width = width, VerticalAlignment = VerticalAlignment.Center };
        double leftPad = withSearchIcon ? 30 : 10;

        var box = new TextBox
        {
            FontSize = 11.5,
            Padding = new Thickness(leftPad, 6, 10, 6),
            BorderThickness = new Thickness(1),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        BindToken(box, Control.ForegroundProperty, "Brush.Text.Primary", TextPrimary);
        BindToken(box, Control.BackgroundProperty, "Brush.Surface.Input", FieldBg);
        BindToken(box, Control.BorderBrushProperty, "Brush.Border.Subtle", Divider);
        BindToken(box, TextBox.CaretBrushProperty, "Brush.Text.Primary", TextPrimary);
        ApplyModernTextBox(box);
        host.Children.Add(box);

        if (withSearchIcon)
        {
            var icon = new TextBlock
            {
                Text = "\uE721",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(11, 0, 0, 0),
                IsHitTestVisible = false,
            };
            BindToken(icon, TextBlock.ForegroundProperty, "Brush.Text.Tertiary", TextTertiary);
            host.Children.Add(icon);
        }

        var ghost = new TextBlock
        {
            Text = placeholder,
            FontSize = 11.5,
            // 与输入文字起点对齐：描边 1 + leftPad + TextBox 内容宿主自带的 2px 光标留白
            Margin = new Thickness(leftPad + 3, 0, 11, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsHitTestVisible = false,
            Visibility = Visibility.Visible,
        };
        BindToken(ghost, TextBlock.ForegroundProperty, "Brush.Text.Tertiary", TextTertiary);
        host.Children.Add(ghost);

        void Sync() => ghost.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        box.TextChanged += (_, _) => Sync();
        Sync();

        return (host, box);
    }

    /// <summary>
    /// 给任意 TextBox 换上现代圆角模板（圆角 8 · 悬停描边半透明蓝 · 键盘焦点描边 #007AFF）。
    ///
    /// 为什么必须换模板而不是只改 Background/BorderBrush：WPF 原生 Aero2 模板的悬停/焦点态由模板触发器
    /// 写死系统色（悬停描边 #7EB4EA、焦点 #569DE5），且外框是**直角**；在深色半透明底上表现为
    /// 「鼠标一过就闪一圈刺眼浅蓝/白边」。换成本模板后，配色全部来自控件自身属性（TemplateBinding），
    /// 交给 Tokens 的 DynamicResource 管，日夜切换时零残留。
    /// 与 <c>Themes/Primitives.xaml</c> 的 <c>ModernTextBoxStyle</c> 同构；这里用代码造一份，
    /// 是因为本类在无 Application 资源的自检上下文里也要能用（取不到 XAML 样式时不退化）。
    /// 多行编辑框同样适用（PART_ContentHost 是 ScrollViewer，滚动条按 TextBox 自身设置显示）。
    /// </summary>
    internal static void ApplyModernTextBox(TextBox box)
    {
        if (box == null) return;
        try
        {
            box.Template = ModernTextBoxTemplate();
            box.FocusVisualStyle = null;
            box.SelectionBrush = FocusRingBrush;
            box.SelectionOpacity = 0.35;
            box.SnapsToDevicePixels = true;
        }
        catch { /* 模板失败时保持原生外观，绝不让一个输入框把整页造不出来 */ }
    }

    private static readonly SolidColorBrush FocusRingBrush = Frozen(Color.FromRgb(0x00, 0x7A, 0xFF));
    private static readonly SolidColorBrush HoverRingBrush = Frozen(Color.FromArgb(0x99, 0x00, 0x7A, 0xFF));
    private static ControlTemplate? _modernTextBoxTemplate;

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static ControlTemplate ModernTextBoxTemplate()
    {
        if (_modernTextBoxTemplate != null) return _modernTextBoxTemplate;

        var bd = new FrameworkElementFactory(typeof(System.Windows.Controls.Border), "Bd");
        bd.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new CornerRadius(InputRadius));
        bd.SetValue(System.Windows.Controls.Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        bd.SetValue(System.Windows.Controls.Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        bd.SetValue(System.Windows.Controls.Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        bd.SetValue(UIElement.SnapsToDevicePixelsProperty, true);

        var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        host.SetValue(UIElement.FocusableProperty, false);
        host.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
        host.SetValue(FrameworkElement.VerticalAlignmentProperty, new TemplateBindingExtension(Control.VerticalContentAlignmentProperty));
        host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, new TemplateBindingExtension(ScrollViewer.HorizontalScrollBarVisibilityProperty));
        host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, new TemplateBindingExtension(ScrollViewer.VerticalScrollBarVisibilityProperty));
        bd.AppendChild(host);

        var t = new ControlTemplate(typeof(TextBox)) { VisualTree = bd };

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(System.Windows.Controls.Border.BorderBrushProperty, HoverRingBrush, "Bd"));
        t.Triggers.Add(hover);

        // 焦点排在悬停之后：两者同时成立时以焦点蓝为准
        var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(System.Windows.Controls.Border.BorderBrushProperty, FocusRingBrush, "Bd"));
        t.Triggers.Add(focus);

        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.5, "Bd"));
        t.Triggers.Add(disabled);

        t.Seal();
        _modernTextBoxTemplate = t;
        return t;
    }

    /// <summary>
    /// 下拉框。
    ///
    /// ⚠ **必须套主窗那份 <c>SlimCombo</c> 模板**（2026-10-29 修的现场 bug：夜间模式下这里造出来的
    /// 下拉是**白底**）。原因不是配色填错，而是 WPF 原生 ComboBox 模板**根本不读 Control.Background /
    /// Foreground** —— 它用系统配色画自己的那套边框与箭头，于是深色窗口里嵌进一块浅色控件。
    /// 套上 SlimCombo 之后，边框/文字/弹出层全部由模板按本类的配色常量画，夜间成立；
    /// 而那两个常量（<see cref="FieldBg"/> / <see cref="Border"/> / <see cref="TextPrimary"/>）
    /// 已经进了 <c>ThemeManager</c> 的映射表，日间也会被换掉。
    /// </summary>
    internal static ComboBox Combo(params string[] items)
    {
        var c = new ComboBox
        {
            FontSize = 11.5,
            Padding = new Thickness(6, 4, 6, 4),
            VerticalContentAlignment = VerticalAlignment.Center,
            Style = ComboStyle(),
        };
        BindToken(c, Control.ForegroundProperty, "Brush.Text.Primary", TextPrimary);
        BindToken(c, Control.BackgroundProperty, "Brush.Surface.Input", FieldBg);
        BindToken(c, Control.BorderBrushProperty, "Brush.Border.Default", Border);
        foreach (var i in items) c.Items.Add(i);
        if (c.Items.Count > 0) c.SelectedIndex = 0;
        return c;
    }

    /// <summary>
    /// 取主窗里那份 <c>SlimCombo</c> 样式（在 <c>MainWindow.xaml</c> 的资源字典里）。
    ///
    /// 为什么**运行时按名字查**而不是 <c>{StaticResource SlimCombo}</c>：这里是代码造控件，
    /// 没有 XAML 的解析上下文；而这份样式挂在主窗的 <c>Resources</c> 上，
    /// 按名字取一次、缓存起来即可（样式是不可变对象，进程内一份就够）。
    /// 取不到时返回 null（退回默认模板）—— 宁可样式退化，也不让一次取样式失败把整个页面造不出来。
    /// </summary>
    private static Style? _comboStyle;
    private static bool _comboStyleProbed;

    private static Style? ComboStyle()
    {
        if (_comboStyleProbed) return _comboStyle;
        _comboStyleProbed = true;
        try
        {
            // Application.Current.Resources 与各窗口自己的 Resources 都可能在放这份样式：
            // 先查应用级（若有），再查当前激活窗口（主窗）。
            if (Application.Current?.TryFindResource("SlimCombo") is Style fromApp) { _comboStyle = fromApp; return _comboStyle; }
            if (Application.Current?.MainWindow?.TryFindResource("SlimCombo") is Style fromWin) { _comboStyle = fromWin; return _comboStyle; }
            if (Application.Current?.Windows.OfType<Window>().Select(x => x.TryFindResource("SlimCombo")).OfType<Style>().FirstOrDefault() is Style fromAny)
                _comboStyle = fromAny;
        }
        catch { _comboStyle = null; }
        return _comboStyle;
    }

    /// <summary>
    /// 卡片容器：圆角 10（与主窗 Card / Primitives.xaml 的 ModernCardStyle 一致）、冷调半透明底、细描边。
    /// 内边距比 2.4.0 之前放宽一档，文字不再贴边。
    /// </summary>
    internal static Border Card(UIElement content, Thickness? margin = null, bool dim = false)
    {
        var b = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(CardRadius),
            Padding = new Thickness(12, 9, 12, 9),
            Margin = margin ?? new Thickness(0, 0, 0, 6),
            SnapsToDevicePixels = true,
            Child = content,
        };
        BindToken(b, System.Windows.Controls.Border.BackgroundProperty, "Brush.Surface.Card", CardBg);
        BindToken(b, System.Windows.Controls.Border.BorderBrushProperty, "Brush.Border.Default", Border);
        if (dim) b.Opacity = 0.62;
        return b;
    }

    /// <summary>面板容器（比卡片底色更沉一档，用来装表单）；圆角与卡片统一为 10。</summary>
    internal static Border Panel(UIElement content, Thickness? margin = null)
    {
        var b = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(CardRadius),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = margin ?? new Thickness(0, 8, 0, 8),
            SnapsToDevicePixels = true,
            Child = content,
        };
        BindToken(b, System.Windows.Controls.Border.BackgroundProperty, "Brush.Surface.Panel", PanelBg);
        BindToken(b, System.Windows.Controls.Border.BorderBrushProperty, "Brush.Border.Subtle", Divider);
        return b;
    }

    /// <summary>分隔线。</summary>
    internal static Border Rule(double thickness = 1)
    {
        var r = new Border { Height = thickness };
        BindToken(r, System.Windows.Controls.Border.BackgroundProperty, "Brush.Border.Subtle", Divider);
        return r;
    }

    /// <summary>
    /// 状态徽标（现代圆角胶囊，圆角 6）。
    /// 底 = 主色 0x24 透明度、描边 = 主色 0x55 透明度：两套主题下都成立，故意不进映射表（见 ThemeManager 注释）。
    /// </summary>
    internal static Border Badge(string text, Color color)
    {
        var label = Text(text, 9.5, color, FontWeights.SemiBold);
        label.VerticalAlignment = VerticalAlignment.Center;
        var b = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x24, color.R, color.G, color.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, color.R, color.G, color.B)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(7, 1.5, 7, 1.5),
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true,
            Child = label,
        };
        return b;
    }

    /// <summary>空状态：主文案 + 下一步该做什么 + 可选按钮。</summary>
    internal static UIElement EmptyState(string title, string nextStep, UIElement? action = null)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 16, 0, 12) };
        sp.Children.Add(Text(title, 12.5, TextSecondary, FontWeights.SemiBold));
        var hint = Hint(nextStep);
        hint.Margin = new Thickness(0, 5, 0, 0);
        sp.Children.Add(hint);
        if (action != null)
        {
            var host = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            host.Children.Add(action);
            sp.Children.Add(host);
        }
        return sp;
    }

    /// <summary>
    /// 一行搜索框。**只负责创建与填初值，不挂回调** —— 回调由调用方在拿到 box 之后再挂。
    ///
    /// ⚠ 这个拆分不是洁癖：如果在这里收一个 <c>Action</c> 并在内部挂上，
    /// 调用方写 `var (host, box) = SearchBox(..., () => box.Text)` 就会
    /// 「引用正在声明的变量」编译不过；改成两段式后调用方可以
    /// <c>var (h, b) = SearchBox(...); b.TextChanged += ...;</c>，闭包引用的是已完成的变量。
    /// 另外初值必须在挂回调**之前**写入，否则 TextChanged 会重入导致无限重建。
    /// </summary>
    internal static (Grid Host, TextBox Box) SearchBox(string placeholder, double width, string initialText)
    {
        var (host, box) = BuildInput(placeholder, width, withSearchIcon: true);
        if (!string.IsNullOrEmpty(initialText)) box.Text = initialText;
        box.CaretIndex = box.Text.Length;
        return (host, box);
    }
}
