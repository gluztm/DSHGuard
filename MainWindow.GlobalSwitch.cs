using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DSHGuard;

public partial class MainWindow
{
    private async void GlobalTargetSwitch_Click(object sender, MouseButtonEventArgs e)
    {
        var newTarget = _globalTarget == GuardTarget.Web ? GuardTarget.Desktop : GuardTarget.Web;
        await AnimateTargetSwitch(newTarget);
        SetGlobalTarget(newTarget);
    }

    internal void SetGlobalTarget(GuardTarget target)
    {
        if (target == _globalTarget) return;
        _globalTarget = target;
        s_writeTarget = target;

        SetPluginTarget(target, force: true);
        _snapTarget = target;

        RefreshCurrentView();
    }

    private void RefreshCurrentView()
    {
        if (_currentView == GuardView.Plugins)
        {
            _ = RefreshPluginsAsync(true);
        }
        else if (_currentView == GuardView.Snapshots)
        {
            RefreshSnapshots();
        }
    }

    private async Task AnimateTargetSwitch(GuardTarget target)
    {
        bool toDesktop = target == GuardTarget.Desktop;

        var compress = new DoubleAnimation
        {
            To = 0.85,
            Duration = TimeSpan.FromMilliseconds(100),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        SwitchScaleX.BeginAnimation(ScaleTransform.ScaleXProperty, compress);
        await Task.Delay(100);

        var slideMargin = toDesktop ? new Thickness(56, 0, 2, 0) : new Thickness(2, 0, 56, 0);
        var slide = new ThicknessAnimation
        {
            To = slideMargin,
            Duration = TimeSpan.FromMilliseconds(400),
            EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut }
        };
        SwitchSlider.BeginAnimation(FrameworkElement.MarginProperty, slide);

        var bounce = new DoubleAnimationUsingKeyFrames();
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(1.15, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200))));
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(0.95, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(350))));
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(500))));
        SwitchScaleX.BeginAnimation(ScaleTransform.ScaleXProperty, bounce);

        AnimateTextColor(WebLabel, toDesktop ? "#8E8E93" : "#F5F5F7");
        AnimateTextColor(DesktopLabel, toDesktop ? "#F5F5F7" : "#8E8E93");

        var sliderBounce = new DoubleAnimation
        {
            From = 1.0,
            To = 1.1,
            Duration = TimeSpan.FromMilliseconds(150),
            AutoReverse = true,
            EasingFunction = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut }
        };
        SliderScale.BeginAnimation(ScaleTransform.ScaleXProperty, sliderBounce);
    }

    private void AnimateTextColor(UIElement target, string hexColor)
    {
        if (target is not TextBlock tb) return;
        var color = (Color)ColorConverter.ConvertFromString(hexColor);
        var anim = new ColorAnimation
        {
            To = color,
            Duration = TimeSpan.FromMilliseconds(300),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        };
        tb.Foreground.BeginAnimation(SolidColorBrush.ColorProperty, anim);
    }

    private void InitializeGlobalSwitch()
    {
        _globalTarget = GuardTarget.Web;
        UpdateSwitchUI(_globalTarget);
    }

    private void UpdateSwitchUI(GuardTarget target)
    {
        bool isDesktop = target == GuardTarget.Desktop;
        SwitchSlider.Margin = isDesktop ? new Thickness(56, 0, 2, 0) : new Thickness(2, 0, 56, 0);
        WebLabel.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDesktop ? "#8E8E93" : "#F5F5F7"));
        DesktopLabel.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDesktop ? "#F5F5F7" : "#8E8E93"));
    }
}
