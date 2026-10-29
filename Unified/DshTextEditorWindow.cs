using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DSHGuard.Unified;

/// <summary>
/// 纯文本编辑器窗口（代码构建，无 XAML）。
///
/// 只负责「把一整份文本改好交回去」——不解析、不校验语法、不做 diff。
/// 校验与落盘交给调用方（<see cref="DshPromptManager.Save"/>），
/// 这样自检可以绕过窗口直接测写入逻辑。
/// </summary>
public sealed class DshTextEditorWindow : Window
{
    private static readonly Color Bg = Color.FromRgb(0x1B, 0x1B, 0x21);
    private static readonly Color BgSoft = Color.FromRgb(0x23, 0x23, 0x2B);
    private static readonly Color Fg = Color.FromRgb(0xE6, 0xE6, 0xEA);
    private static readonly Color FgDim = Color.FromRgb(0x9A, 0x9A, 0xA6);
    private static readonly Color Border = Color.FromRgb(0x33, 0x33, 0x3C);
    private static readonly Color Accent = Color.FromRgb(0x2B, 0x6B, 0xFF);

    private readonly TextBox _box;

    /// <summary>用户是否按了「保存」（false = 取消 / 直接关窗）。</summary>
    public bool Saved { get; private set; }

    /// <summary>保存时的文本内容。</summary>
    public string ResultText => _box.Text;

    public DshTextEditorWindow(string title, string hint, string content)
    {
        Title = title;
        Width = 900;
        Height = 640;
        Background = new SolidColorBrush(Bg);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 标题
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 说明
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 编辑区
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 按钮

        var head = new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Fg),
            Margin = new Thickness(0, 0, 0, 6),
        };
        Grid.SetRow(head, 0);
        root.Children.Add(head);

        var tip = new TextBlock
        {
            Text = hint,
            FontSize = 12,
            Foreground = new SolidColorBrush(FgDim),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        };
        Grid.SetRow(tip, 1);
        root.Children.Add(tip);

        _box = new TextBox
        {
            Text = content,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(BgSoft),
            Foreground = new SolidColorBrush(Fg),
            CaretBrush = new SolidColorBrush(Fg),
            BorderBrush = new SolidColorBrush(Border),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10),
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"),
            FontSize = 13,
        };
        Grid.SetRow(_box, 2);
        root.Children.Add(_box);

        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };

        var save = MakeButton("保存", true);
        save.Click += (_, _) => { Saved = true; Close(); };
        bar.Children.Add(save);

        var cancel = MakeButton("取消", false);
        cancel.Click += (_, _) => { Saved = false; Close(); };
        bar.Children.Add(cancel);

        Grid.SetRow(bar, 3);
        root.Children.Add(bar);

        Content = root;

        // Esc = 取消（不保存）
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Saved = false; Close(); e.Handled = true; }
        };
    }

    private static Button MakeButton(string text, bool primary)
    {
        var b = new Button
        {
            Content = text,
            MinWidth = 88,
            Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(8, 0, 0, 0),
            Background = new SolidColorBrush(primary ? Accent : BgSoft),
            Foreground = new SolidColorBrush(primary ? Colors.White : Fg),
            BorderBrush = new SolidColorBrush(primary ? Accent : Border),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
        };
        RoundBtn.Apply(b);   // 与主程序同一份圆角模板
        ButtonFx.Wire(b);    // 悬停放大提亮、按下缩小变暗
        return b;
    }
}
