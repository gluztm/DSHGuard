using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DSHGuard;

/// <summary>
/// 统一的圆角按钮外观。
///
/// WPF 的 Button 默认模板是**直角**，代码里直接 new 出来的按钮因此看着方方正正。
/// 这里给出一份共用模板：圆角 <see cref="Radius"/> + 悬停放大提亮 + 按下缩小，
/// 所有代码创建的按钮都套它，界面上的按钮圆角就此统一。
/// 个别按钮若明确要求"悬停只变色改字、尺寸不动"，对它单独调
/// <see cref="DisableHoverScale"/>（换用无缩放的另一份模板），其余按钮不受影响。
/// 「已套模板」会打一个附加标记，自检据此巡检（见 <see cref="FindUnroundedForTest"/>）。
/// </summary>
internal static class RoundBtn
{
    /// <summary>统一圆角半径（与 XAML 里 Border 型按钮的取值一致）。</summary>
    internal const double Radius = 8;

    private static readonly DependencyProperty RoundedProperty =
        DependencyProperty.RegisterAttached("Rounded", typeof(bool), typeof(RoundBtn), new PropertyMetadata(false));

    /// <summary>
    /// 「悬停不放大」标记。**按元素级**，不是全局开关：
    /// 谁被标记，谁才在共用模板里改走"无缩放"的那一份；其余按钮一律照旧保留悬停放大回弹。
    /// </summary>
    private static readonly DependencyProperty NoHoverScaleProperty =
        DependencyProperty.RegisterAttached("NoHoverScale", typeof(bool), typeof(RoundBtn), new PropertyMetadata(false));

    private static ControlTemplate? _template;
    private static ControlTemplate? _templateNoScale;

    /// <summary>共用模板（懒建一次，所有按钮复用同一份，省内存）。</summary>
    private static ControlTemplate Template() => _template ??= BuildTemplate(hoverScale: true);

    /// <summary>
    /// 只有 <see cref="DisableHoverScale"/> 标记过的元素才用这一份：与共用模板**完全同构**，
    /// 仅去掉悬停/按下的 ScaleTransform 动画，不透明度与配色行为一字不改。
    /// 单独缓存一份，避免动到别的按钮正在用的那份模板实例。
    /// </summary>
    private static ControlTemplate TemplateNoHoverScale() => _templateNoScale ??= BuildTemplate(hoverScale: false);

    /// <param name="hoverScale">
    /// true=共用模板，悬停放大到 1.05；false=无缩放版本，整段动效里不含任何 Scale 属性路径。
    /// </param>
    private static ControlTemplate BuildTemplate(bool hoverScale)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(Radius));
        border.SetBinding(Border.BackgroundProperty,
            new Binding(nameof(Control.Background)) { RelativeSource = RelativeSource.TemplatedParent });
        border.SetBinding(Border.PaddingProperty,
            new Binding(nameof(Control.Padding)) { RelativeSource = RelativeSource.TemplatedParent });
        border.SetValue(UIElement.RenderTransformOriginProperty, new Point(0.5, 0.5));
        border.SetValue(UIElement.RenderTransformProperty, new ScaleTransform(1, 1));

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        // 尊重调用方的内容对齐（快照行设了 Left，要真正贴左）；没设时按默认居中
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty,
            new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
        content.SetValue(FrameworkElement.VerticalAlignmentProperty,
            new TemplateBindingExtension(Control.VerticalContentAlignmentProperty));
        border.AppendChild(content);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };

        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
        template.Triggers.Add(disabled);

        // 悬停：放大并提亮（回弹缓动，与窗口三键一致）
        void Anim(Storyboard sb, string path, double to, double ms, bool back)
        {
            var da = new DoubleAnimation { To = to, Duration = TimeSpan.FromMilliseconds(ms) };
            if (back) da.EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 };
            Storyboard.SetTargetProperty(da, new PropertyPath(path));
            sb.Children.Add(da);
        }
        // 缩放那段只在共用模板里加；hoverScale=false 时这里一个 Scale 路径都不产生，
        // 透明度那两行照旧 ⇒ 该元素只剩「变色/改字」，尺寸一动不动。
        void Scale(Storyboard sb, double to, double ms, bool back)
        {
            if (!hoverScale) return;
            Anim(sb, "(UIElement.RenderTransform).(ScaleTransform.ScaleX)", to, ms, back);
            Anim(sb, "(UIElement.RenderTransform).(ScaleTransform.ScaleY)", to, ms, back);
        }

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        var hoverSb = new Storyboard();
        Scale(hoverSb, 1.05, 140, back: true);
        Anim(hoverSb, "Opacity", 0.85, 120, false);
        hover.EnterActions.Add(new BeginStoryboard { Storyboard = hoverSb });
        template.Triggers.Add(hover);

        var leave = new Trigger { Property = UIElement.IsMouseOverProperty, Value = false };
        var leaveSb = new Storyboard();
        Scale(leaveSb, 1.0, 140, back: false);
        Anim(leaveSb, "Opacity", 1.0, 120, false);
        leave.ExitActions.Add(new BeginStoryboard { Storyboard = leaveSb });
        template.Triggers.Add(leave);

        var press = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        var pressSb = new Storyboard();
        Scale(pressSb, 0.96, 60, back: false);
        press.EnterActions.Add(new BeginStoryboard { Storyboard = pressSb });
        template.Triggers.Add(press);

        return template;
    }

    /// <summary>
    /// 让这一颗元素**不再有悬停放大**（按需调用，别的按钮不受影响）。
    /// <para>
    /// **为什么需要它**：共用模板里带着 1.05 倍悬停放大 + 回弹（<see cref="BuildTemplate"/> 里
    /// <c>Scale(hoverSb, 1.05, …)</c> 那两行）。这是全壳按钮统一的观感，绝大多数按钮都要留着。
    /// 但「寻找插件」页那颗**安装**按钮有个现场要求：悬停时只变色、改字（安装中… → 停止），
    /// **尺寸一点都不许动** —— 因为它的三态是被反复注视的同一颗按钮，放大回弹会被读成"抖了一下"。
    /// </para>
    /// <para>
    /// 实现方式（**只影响这一颗**，可验证）：
    /// ① 只看元素自己的附加标记 <see cref="NoHoverScaleProperty"/>，不碰任何全局状态；
    /// ② 标记过的元素改用另一份**独立缓存**的模板（<see cref="TemplateNoHoverScale"/>），
    ///    全壳共用的那份模板实例原封不动，其它按钮继续放大；
    /// ③ 可见性/圆角/禁用半透明等其它触发器两份模板完全一致 ⇒ 只少了缩放这一个变化。
    /// </para>
    /// <para>
    /// <see cref="ButtonFx"/> 也认这个标记：给标记过的元素挂动效时**跳过悬停放大**，
    /// 避免它那条路径把放大又加回来（见 <c>ButtonFx.Attach</c>）。
    /// </para>
    /// </summary>
    internal static FrameworkElement DisableHoverScale(FrameworkElement el)
    {
        try
        {
            el.SetValue(NoHoverScaleProperty, true);
            if (el is Button b) b.Template = TemplateNoHoverScale();
        }
        catch { }
        return el;
    }

    /// <summary>自检/排查用：这颗元素是否被要求"悬停不放大"。</summary>
    internal static bool HoverScaleDisabled(FrameworkElement f)
    {
        try { return (bool)f.GetValue(NoHoverScaleProperty); }
        catch { return false; }
    }

    /// <summary>把这个按钮换成统一圆角外观（幂等）。</summary>
    internal static Button Apply(Button b)
    {
        try
        {
            b.Template = Template();
            b.BorderThickness = new Thickness(0);
            b.SetValue(RoundedProperty, true);
        }
        catch { }
        return b;
    }

    /// <summary>该元素是否已是圆角（按钮看标记，Border 看圆角设置）。</summary>
    internal static bool IsRounded(FrameworkElement f)
    {
        if ((bool)f.GetValue(RoundedProperty)) return true;
        return f is Border b && b.CornerRadius.TopLeft > 0.5;
    }

    /// <summary>
    /// 自检用：找出树里「手型光标的按钮但没套圆角模板」的元素（逐页应为 0）。
    /// 只查真正的 <see cref="Button"/>——文字链接（TextBlock）与开关（ToggleButton）不算按钮，不需要圆角。
    /// </summary>
    internal static List<FrameworkElement> FindUnroundedForTest(DependencyObject? root)
    {
        var bad = new List<FrameworkElement>();
        Walk(root);
        return bad;

        void Walk(DependencyObject? o)
        {
            if (o == null) return;
            try
            {
                if (o is Button btn && btn.Cursor == Cursors.Hand && !(bool)btn.GetValue(RoundedProperty))
                    bad.Add(btn);
                int n = VisualTreeHelper.GetChildrenCount(o);
                for (int i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(o, i));
            }
            catch { }
        }
    }
}
