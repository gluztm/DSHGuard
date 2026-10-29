using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    // 配色（与 MainWindow.Unified.cs 共用同一套，不另起一套）
    internal static readonly Color TextPrimary = Color.FromRgb(0xF5, 0xF5, 0xF7);
    internal static readonly Color TextSecondary = Color.FromRgb(0x9A, 0x9A, 0xA2);
    internal static readonly Color TextTertiary = Color.FromRgb(0x6E, 0x6E, 0x78);
    internal static readonly Color Accent = Color.FromRgb(0x2B, 0x6B, 0xFF);
    internal static readonly Color Warn = Color.FromRgb(0xFF, 0xB0, 0x2E);
    internal static readonly Color Danger = Color.FromRgb(0xFF, 0x5A, 0x4D);
    internal static readonly Color Ok = Color.FromRgb(0x35, 0xC7, 0x59);
    internal static readonly Color Border = Color.FromRgb(0x33, 0x33, 0x3C);
    internal static readonly Color Divider = Color.FromRgb(0x26, 0x26, 0x2E);
    internal static readonly Color CardBg = Color.FromRgb(0x1E, 0x1E, 0x24);
    internal static readonly Color PanelBg = Color.FromRgb(0x18, 0x18, 0x1E);
    internal static readonly Color FieldBg = Color.FromRgb(0x2B, 0x2B, 0x34);

    /// <summary>一行文字。<paramref name="wrap"/> = true 时换行且不省略。</summary>
    internal static TextBlock Text(string text, double size, Color color,
        FontWeight? weight = null, bool wrap = false)
    {
        var tb = new TextBlock
        {
            Text = text ?? "",
            FontSize = size,
            Foreground = new SolidColorBrush(color),
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
        };
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
    internal static UIElement InfoDot(string title, string body)
    {
        var b = new Border
        {
            Width = 14,
            Height = 14,
            CornerRadius = new CornerRadius(7),
            BorderBrush = new SolidColorBrush(Border),
            BorderThickness = new Thickness(1),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = string.IsNullOrEmpty(body) ? title : title + "\n\n" + body,
        };
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
        Color bg = FieldBg;
        Color fg = TextPrimary;
        Color br = Border;

        if (tone == "primary") { bg = Accent; fg = Colors.White; br = Accent; }
        else if (tone == "danger") { bg = Color.FromRgb(0x3A, 0x20, 0x20); fg = Danger; br = Danger; }

        var b = new Button
        {
            Content = label,
            FontSize = 11.5,
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 8, 8),
            MinHeight = 28,
            MinWidth = 34,
            Cursor = Cursors.Hand,
            Background = new SolidColorBrush(bg),
            Foreground = new SolidColorBrush(fg),
            BorderBrush = new SolidColorBrush(br),
            BorderThickness = new Thickness(1),
        };
        RoundBtn.Apply(b);
        ButtonFx.Wire(b);
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
    {
        var host = new Grid { Width = width, VerticalAlignment = VerticalAlignment.Center };

        var box = new TextBox
        {
            FontSize = 11.5,
            Padding = new Thickness(7, 5, 7, 5),
            Foreground = new SolidColorBrush(TextPrimary),
            Background = new SolidColorBrush(FieldBg),
            BorderBrush = new SolidColorBrush(Border),
            BorderThickness = new Thickness(1),
            CaretBrush = new SolidColorBrush(TextPrimary),
        };
        host.Children.Add(box);

        var ghost = new TextBlock
        {
            Text = placeholder,
            FontSize = 11.5,
            Foreground = new SolidColorBrush(TextTertiary),
            Margin = new Thickness(9, 0, 9, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Visibility = Visibility.Visible,
        };
        host.Children.Add(ghost);

        void Sync() => ghost.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        box.TextChanged += (_, _) => Sync();
        Sync();

        return (host, box);
    }

    /// <summary>下拉框（深色主题）。</summary>
    internal static ComboBox Combo(params string[] items)
    {
        var c = new ComboBox
        {
            FontSize = 11.5,
            Padding = new Thickness(6, 4, 6, 4),
            Foreground = new SolidColorBrush(TextPrimary),
            Background = new SolidColorBrush(FieldBg),
            BorderBrush = new SolidColorBrush(Border),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        foreach (var i in items) c.Items.Add(i);
        if (c.Items.Count > 0) c.SelectedIndex = 0;
        return c;
    }

    /// <summary>卡片容器。</summary>
    internal static Border Card(UIElement content, Thickness? margin = null, bool dim = false)
    {
        var b = new Border
        {
            Background = new SolidColorBrush(CardBg),
            BorderBrush = new SolidColorBrush(Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(10, 7, 10, 7),
            Margin = margin ?? new Thickness(0, 0, 0, 4),
            Child = content,
        };
        if (dim) b.Opacity = 0.62;
        return b;
    }

    /// <summary>面板容器（比卡片底色更深，用来装表单）。</summary>
    internal static Border Panel(UIElement content, Thickness? margin = null)
        => new()
        {
            Background = new SolidColorBrush(PanelBg),
            BorderBrush = new SolidColorBrush(Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(10, 9, 10, 9),
            Margin = margin ?? new Thickness(0, 8, 0, 8),
            Child = content,
        };

    /// <summary>分隔线。</summary>
    internal static Border Rule(double thickness = 1)
        => new() { Height = thickness, Background = new SolidColorBrush(Divider) };

    /// <summary>状态徽标（小圆角胶囊）。</summary>
    internal static Border Badge(string text, Color color)
    {
        var b = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x28, color.R, color.G, color.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, color.R, color.G, color.B)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(5, 1, 5, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = Text(text, 9.5, color, FontWeights.SemiBold),
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
        var (host, box) = Input(placeholder, width);
        if (!string.IsNullOrEmpty(initialText)) box.Text = initialText;
        box.CaretIndex = box.Text.Length;
        return (host, box);
    }
}
