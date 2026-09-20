using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DSHGuard;

/// <summary>
/// 统一的按钮动效：悬停轻微放大 + 变亮，按下缩小 + 变暗，松开/离开复原。
/// 判据是「手型光标」（导航项、页签、工具条按钮、卡片按钮、弹窗按钮都这么标），挂载幂等。
///
/// 两条约定：
///   1. 元素若自带缩放动画（XAML 样式或触发器）则只补按下/松开，避免两套动画争同一属性；
///   2. 缩放变换**按需创建、复位即清除**：静止状态不带 RenderTransform，文字才能保持清晰渲染。
/// </summary>
internal static class ButtonFx
{
    private static readonly DependencyProperty WiredProperty =
        DependencyProperty.RegisterAttached("Wired", typeof(bool), typeof(ButtonFx), new PropertyMetadata(false));

    /// <summary>各元素最近一次动画的目标值（无头环境下动画时钟不推进，自检据此断言）。弱引用表：卡片反复重建时不吊住废弃元素。</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkElement, object> LastTarget = new();

    /// <summary>标记「这个 ScaleTransform 是 ButtonFx 自己建的」——复位时只清自己的，绝不动 XAML/样式自带的。</summary>
    private static readonly DependencyProperty OwnedTransformProperty =
        DependencyProperty.RegisterAttached("OwnedTransform", typeof(bool), typeof(ButtonFx), new PropertyMetadata(false));

    // ── 动效参数 ──
    private const double HoverScale = 1.04, PressScale = 0.96, RestScale = 1.0;
    private const double HoverOpacity = 0.85, PressOpacity = 0.70, RestOpacity = 1.0;

    /// <summary>可挂动效的元素：任何手型光标元素（含卡片里可点的文字）。</summary>
    private static bool IsInteractive(DependencyObject o)
        => o is FrameworkElement fe && fe.Cursor == Cursors.Hand;

    /// <summary>给整棵树里所有手型光标元素挂上动效（幂等）。</summary>
    public static void Wire(DependencyObject? root)
    {
        if (root == null) return;
        try
        {
            if (IsInteractive(root)) Attach((FrameworkElement)root);
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++) Wire(VisualTreeHelper.GetChild(root, i));
        }
        catch { }
    }

    private static void Attach(FrameworkElement el)
    {
        if ((bool)el.GetValue(WiredProperty)) return;
        el.SetValue(WiredProperty, true);

        // 文字清晰：整数像素布局 + 显示模式渲染
        el.UseLayoutRounding = true;
        el.SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(el, TextFormattingMode.Display);

        // 自带缩放动画的元素（MiniBtn 样式、主页大按钮）：只补按下与松开
        bool ownsScale = el.RenderTransform is ScaleTransform;

        // 明确要求"悬停不放大"的元素（现场：寻找插件页的安装按钮——悬停只变色改字、尺寸不动，
        // 见 RoundBtn.DisableHoverScale）。这类元素走单独一条分支：**一次缩放都不做**，
        // 不建变换也不改缩放（下按/松开只改不透明度，仍有点击手感）。
        // 判据只看元素自己的附加标记，别的元素一个都不受影响。
        if (RoundBtn.HoverScaleDisabled(el))
        {
            // 悬停/离开：只做不透明度（放大提亮里的"提亮"留着）
            el.MouseEnter += (_, _) => Animate(el, RestScale, HoverOpacity, ownsOpacity: true, ms: 140);
            el.MouseLeave += (_, _) => Animate(el, RestScale, RestOpacity, ownsOpacity: true, ms: 120);
            // 按下/松开：同样只改不透明度，不缩放
            el.MouseLeftButtonDown += (_, _) => Animate(el, RestScale, PressOpacity, ownsOpacity: true, ms: 90);
            el.MouseLeftButtonUp += (_, _) => Animate(el, RestScale, HoverOpacity, ownsOpacity: true, ms: 120);
            return;
        }

        if (!ownsScale)
        {
            el.MouseEnter += (_, _) => Animate(el, HoverScale, HoverOpacity, ownsOpacity: true, ms: 140);
            el.MouseLeave += (_, _) => Animate(el, RestScale, RestOpacity, ownsOpacity: true, ms: 120);
        }
        else
        {
            el.MouseLeave += (_, _) => Animate(el, RestScale, RestOpacity, ownsOpacity: false, ms: 120);
        }

        el.MouseLeftButtonDown += (_, _) => Animate(el, PressScale, PressOpacity, ownsOpacity: !ownsScale, ms: 90);
        el.MouseLeftButtonUp += (_, _) =>
            Animate(el, HoverScale, HoverOpacity, ownsOpacity: !ownsScale, ms: 120);
    }

    /// <summary>按需取出缩放对象；静止复位完成后清除变换，保证文字锐利。</summary>
    private static ScaleTransform? EnsureTransform(FrameworkElement el, bool create)
    {
        if (el.RenderTransform is ScaleTransform st) return st;
        if (!create) return null;
        if (el.RenderTransform != null && !el.RenderTransform.Equals(Transform.Identity)) return null;   // 有其它变换：不动它
        var scale = new ScaleTransform(RestScale, RestScale);
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        el.RenderTransform = scale;
        el.SetValue(OwnedTransformProperty, true);      // 打标：只有自己建的才允许被复位清掉
        return scale;
    }

    private static void Animate(FrameworkElement el, double scaleTo, double opacityTo, bool ownsOpacity, int ms)
    {
        try
        {
            LastTarget.AddOrUpdate(el, (scaleTo, ownsOpacity ? opacityTo : RestOpacity));

            double current = (el.RenderTransform as ScaleTransform)?.ScaleX ?? RestScale;
            bool needsMotion = Math.Abs(scaleTo - RestScale) > 0.0001 || Math.Abs(current - RestScale) > 0.0001;
            var scale = EnsureTransform(el, create: needsMotion);
            var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };
            var dur = TimeSpan.FromMilliseconds(ms);
            var sb = new Storyboard();

            if (scale != null)
            {
                foreach (var prop in new[] { ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty })
                {
                    var a = new DoubleAnimation(scaleTo, dur) { EasingFunction = ease };
                    Storyboard.SetTarget(a, scale);
                    Storyboard.SetTargetProperty(a, new PropertyPath(prop));
                    sb.Children.Add(a);
                }
            }
            if (ownsOpacity)
            {
                var o = new DoubleAnimation(opacityTo, dur);
                Storyboard.SetTarget(o, el);
                Storyboard.SetTargetProperty(o, new PropertyPath(UIElement.OpacityProperty));
                sb.Children.Add(o);
            }

            if (sb.Children.Count == 0) { ClearIfResting(el, scaleTo); return; }

            // 动画结束若已回到静止态，就把变换清掉，恢复锐利文字
            sb.Completed += (_, _) => ClearIfResting(el, scaleTo);
            sb.Begin();
        }
        catch { }
    }

    private static void ClearIfResting(FrameworkElement el, double scaleTo)
    {
        try
        {
            if (Math.Abs(scaleTo - RestScale) > 0.0001) return;
            // 只清自己建的变换：元素自带的（MiniBtn 样式、主页大按钮）必须留着，
            // 否则它们的 XAML 属性路径动画会解析不到目标，缩放从此失效。
            if (!(bool)el.GetValue(OwnedTransformProperty)) return;
            if (el.RenderTransform is ScaleTransform) el.RenderTransform = null;
            el.SetValue(OwnedTransformProperty, false);
        }
        catch { }
    }

    // ── 自检钩子 ──
    internal static bool IsWiredForTest(FrameworkElement el) => (bool)el.GetValue(WiredProperty);

    /// <summary>最近一次动画的目标（缩放, 不透明度）。</summary>
    internal static (double Scale, double Opacity) LastTargetForTest(FrameworkElement el)
        => LastTarget.TryGetValue(el, out var v) && v is ValueTuple<double, double> t ? t : (RestScale, RestOpacity);

    /// <summary>树里「手型光标但没挂动效」的元素个数——逐页应为 0。</summary>
    internal static int UnwiredCountForTest(DependencyObject? root)
    {
        int n = 0;
        try
        {
            if (IsInteractive(root!) && !(bool)((FrameworkElement)root!).GetValue(WiredProperty)) n++;
            int c = VisualTreeHelper.GetChildrenCount(root!);
            for (int i = 0; i < c; i++) n += UnwiredCountForTest(VisualTreeHelper.GetChild(root!, i));
        }
        catch { }
        return n;
    }

    internal static void HoverForTest(FrameworkElement el, bool enter)
        => Animate(el, enter ? HoverScale : RestScale, enter ? HoverOpacity : RestOpacity, ownsOpacity: true, ms: 140);

    internal static void PressForTest(FrameworkElement el, bool down)
        => Animate(el, down ? PressScale : HoverScale, down ? PressOpacity : HoverOpacity, ownsOpacity: true, ms: 90);

    /// <summary>当前缩放实例的 X（没有变换时视为静止 1.0）。</summary>
    internal static double ScaleForTest(FrameworkElement el)
        => el.RenderTransform is ScaleTransform st ? st.ScaleX : RestScale;

    /// <summary>自检用：这个元素自带的变换是否还在（M1 回归护栏）。</summary>
    internal static bool OwnTransformIntactForTest(FrameworkElement el)
        => el.RenderTransform is ScaleTransform st && !(bool)el.GetValue(OwnedTransformProperty) && Math.Abs(st.ScaleX - RestScale) < 0.0001;

    /// <summary>自检用：手动跑一次「动画结束、回到静止」的收尾（无头环境下动画时钟不推进，Completed 不会触发）。</summary>
    internal static void SettleForTest(FrameworkElement el) => ClearIfResting(el, RestScale);
}
