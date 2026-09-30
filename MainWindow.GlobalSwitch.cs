using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DSHGuard;

/// <summary>
/// 2.0.0：左上角全局切换开关 —— 管理目标（Web 引擎 / 桌面版）的**唯一**切换入口。
///   · 动画未结束时再点直接忽略（重入闸）；
///   · 有插件写操作在跑时拒绝切换（否则跑到一半的命令与界面指向的目标不再一致）；
///   · 桌面版未安装时桌面半边置灰、点了不切；
///   · 选择写入 settings.json（LastTarget），下次启动恢复。
/// </summary>
public partial class MainWindow
{
    private bool _switching;

    private async void GlobalTargetSwitch_Click(object sender, MouseButtonEventArgs e)
    {
        if (_switching) return;
        var newTarget = _ctx.Target == GuardTarget.Web ? GuardTarget.Desktop : GuardTarget.Web;
        string? why = SwitchBlockedReason(newTarget);
        if (why != null)
        {
            GuardDialog.Show(why, "切换管理对象", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _switching = true;
        try
        {
            await AnimateTargetSwitch(newTarget);
            SwitchTarget(newTarget);
        }
        catch (Exception ex) { Logger.LogError("GlobalTargetSwitch_Click", ex); }
        finally { _switching = false; }
    }

    /// <summary>
    /// 能不能切到 <paramref name="target"/>：null = 可以；否则返回给用户看的原因。纯判据，唯一一处。
    /// </summary>
    internal string? SwitchBlockedReason(GuardTarget target)
    {
        if (target == _ctx.Target) return null;   // 原地不动，无所谓拦不拦
        if (target == GuardTarget.Desktop && !GuardPaths.DesktopExeFound)
            return "未检测到 DSH 桌面版。\n\n如果装在自定义位置，可到「设置 → 路径」填写桌面版安装目录。";
        if (PluginOpInFlight)
            return $"正在{_ctx.Label}上执行插件操作，完成后再切换。";
        return null;
    }

    /// <summary>任何一种插件写操作是否正在进行（更新 / 批量 / 市场安装 / 卸载 / 回滚重装）。</summary>
    private bool PluginOpInFlight
        => _updatingBusy || _pluginWriteBusy || _batchBusy || _marketBusy || _installing;

    /// <summary>用户触发的切换：改目标 + 记住选择 + 刷新当前视图。</summary>
    internal void SwitchTarget(GuardTarget target)
    {
        if (target == _ctx.Target) return;
        SetTarget(target);
        PersistLastTarget();
        RefreshCurrentView();
    }

    private void PersistLastTarget()
    {
        try
        {
            string v = TargetContext.ToSetting(_ctx.Target);
            if (_settings.LastTarget == v) return;
            _settings.LastTarget = v;
            _settings.Save();
            if (_settings.LastSaveFailed)
                Logger.NoteDiagnosis("切换开关的选择未能写盘（下次启动会回到上次保存的目标）：" + _settings.LastSaveError);
        }
        catch (Exception ex) { Logger.LogError("PersistLastTarget", ex); }
    }

    private void RefreshCurrentView()
    {
        if (_currentView == GuardView.Plugins)
            _ = RefreshPluginsAsync(true);
        else if (_currentView == GuardView.Snapshots)
            RefreshSnapshots();
    }

    // ── 配色：跟随日 / 夜主题（选中字压在蓝色滑块上 ⇒ 恒为白；未选中字按主题取次要文字色）──
    private static Color SwitchActiveText => Colors.White;
    private static Color SwitchIdleText => ThemeManager.IsDark
        ? Color.FromRgb(0x8E, 0x8E, 0x93)
        : Color.FromRgb(0x6B, 0x6B, 0x70);

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

        var slide = new ThicknessAnimation
        {
            To = SliderMarginFor(toDesktop),
            Duration = TimeSpan.FromMilliseconds(400),
            EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        slide.Completed += (_, _) => SwitchSlider.Margin = SliderMarginFor(toDesktop);
        SwitchSlider.BeginAnimation(FrameworkElement.MarginProperty, slide);

        var bounce = new DoubleAnimationUsingKeyFrames();
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(1.15, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200))));
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(0.95, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(350))));
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(500))));
        SwitchScaleX.BeginAnimation(ScaleTransform.ScaleXProperty, bounce);

        AnimateTextColor(WebLabel, toDesktop ? SwitchIdleText : SwitchActiveText);
        AnimateTextColor(DesktopLabel, toDesktop ? SwitchActiveText : SwitchIdleText);

        var sliderBounce = new DoubleAnimation
        {
            From = 1.0,
            To = 1.1,
            Duration = TimeSpan.FromMilliseconds(150),
            AutoReverse = true,
            EasingFunction = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut }
        };
        SliderScale.BeginAnimation(ScaleTransform.ScaleXProperty, sliderBounce);
        await Task.Delay(400);
    }

    private static Thickness SliderMarginFor(bool desktop)
        => desktop ? new Thickness(56, 0, 2, 0) : new Thickness(2, 0, 56, 0);

    private static void AnimateTextColor(TextBlock tb, Color color)
    {
        // 每次换一支新画刷再动画：XAML 里的初始画刷可能是冻结的，直接对它 BeginAnimation 会抛。
        var from = (tb.Foreground as SolidColorBrush)?.Color ?? color;
        var brush = new SolidColorBrush(from);
        tb.Foreground = brush;
        brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation
        {
            To = color,
            Duration = TimeSpan.FromMilliseconds(300),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        });
    }

    /// <summary>启动时恢复上次的目标（桌面版不可用则回落 Web），并画一次开关外观。</summary>
    private void InitializeGlobalSwitch()
    {
        try
        {
            var want = TargetContext.FromSetting(_settings.LastTarget);
            if (want == GuardTarget.Desktop && !GuardPaths.DesktopExeFound) want = GuardTarget.Web;
            if (want != _ctx.Target) SetTarget(want);
            else ApplyTargetChrome();
        }
        catch (Exception ex) { Logger.LogError("InitializeGlobalSwitch", ex); }
    }

    /// <summary>开关外观：滑块位置、两侧文字色、桌面版不可用时的置灰与提示。由 ApplyTargetChrome 调用。</summary>
    private void UpdateSwitchUI(GuardTarget target)
    {
        if (SwitchSlider == null || WebLabel == null || DesktopLabel == null) return;
        bool isDesktop = target == GuardTarget.Desktop;
        SwitchSlider.BeginAnimation(FrameworkElement.MarginProperty, null);
        SwitchSlider.Margin = SliderMarginFor(isDesktop);
        WebLabel.Foreground = new SolidColorBrush(isDesktop ? SwitchIdleText : SwitchActiveText);
        DesktopLabel.Foreground = new SolidColorBrush(isDesktop ? SwitchActiveText : SwitchIdleText);

        bool deskAvail = GuardPaths.DesktopExeFound;
        DesktopLabel.Opacity = deskAvail || isDesktop ? 1.0 : 0.4;
        if (GlobalTargetSwitch != null)
        {
            GlobalTargetSwitch.Cursor = deskAvail || isDesktop ? Cursors.Hand : Cursors.Arrow;
            GlobalTargetSwitch.ToolTip = deskAvail
                ? $"当前管理：{TargetContext.LabelOf(target)}（点击切换 Web 引擎 / 桌面版）"
                : "未检测到 DSH 桌面版（可到「设置 → 路径」填写安装目录）";
        }
    }

    // ── 自检钩子 ──
    internal string? SwitchBlockedReasonForTest(bool desktop)
        => SwitchBlockedReason(desktop ? GuardTarget.Desktop : GuardTarget.Web);
    internal string LastTargetSettingForTest => _settings.LastTarget;
    internal bool PluginOpInFlightForTest => PluginOpInFlight;
    internal PluginCmd InstallCmdForTest(string source) => InstallCmdFor(source);
    internal PluginCmd UninstallCmdForTest(string name) => UninstallCmdFor(name);
    internal string CompatEngineVersionForTest => CompatEngineVersion;
    internal (string Web, string Desktop, double DeskOpacity) SwitchLabelStateForTest
        => (((WebLabel.Foreground as SolidColorBrush)?.Color.ToString()) ?? "",
            ((DesktopLabel.Foreground as SolidColorBrush)?.Color.ToString()) ?? "",
            DesktopLabel.Opacity);
}
