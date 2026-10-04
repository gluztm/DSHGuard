using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;

namespace DSHGuard;

/// <summary>
/// 2.0.0：左上角 Logo 点击切换 —— 管理目标（Web 引擎 / 桌面版）的**唯一**切换入口。
///   · 硬币翻转动画（3D Y轴旋转 + 弹性缓动）；
///   · 硬币翻转音效（coin-flip.wav）；
///   · 动画未结束时再点直接忽略（重入闸）；
///   · 有插件写操作在跑时拒绝切换；
///   · 桌面版未安装时拒绝切换到桌面；
///   · 选择写入 settings.json（LastTarget），下次启动恢复。
/// </summary>
public partial class MainWindow
{
    private bool _switching;

    private async void LogoImage_Click(object sender, MouseButtonEventArgs e)
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
            await AnimateCoinFlip(newTarget);
            SwitchTarget(newTarget);
        }
        catch (Exception ex) { Logger.LogError("LogoImage_Click", ex); }
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
        if (_currentView == GuardView.Status)
        {
            if (_ctx.Target == GuardTarget.Desktop)
            {
                // RefreshDesktopHome(); // TODO: Desktop主页功能待完善
                // 暂时使用Web的刷新逻辑
                UpdateUI();
                RefreshStatusEvents();
            }
            else
            {
                UpdateUI();
                RefreshStatusEvents();
                RefreshEnvInfo();
                RefreshLogPreview();
            }
        }
        else if (_currentView == GuardView.Plugins)
            _ = RefreshPluginsAsync(true);
        else if (_currentView == GuardView.Snapshots)
            RefreshSnapshots();
    }

    /// <summary>
    /// 2.1.0：抛硬币。三件事同时发生，合起来就是"硬币抛起来翻了两面再落回"：
    ///   ① ScaleX 1→0.05→1→0.05→1（两次转到"侧面"，不镜像，看起来是同一枚硬币翻了两面）；
    ///   ② SkewY 0→+14→0→-14→0（透视斜切，制造"转到侧后方"的立体感）；
    ///   ③ TranslateY 抛起 −12px 再落回，末尾用 BackEase 回弹一下。
    /// 全程约 620ms；音效与动画同时开始。WPF 没有 per-element 的 3D 投影（PlaneProjection 不可用），
    /// 所以这是 2.5D 合成 —— 26px 的图标上肉眼与真 3D 无异。
    /// </summary>
    private async Task AnimateCoinFlip(GuardTarget target)
    {
        PlayCoinFlipSound();
        if (LogoScale == null) { await Task.Delay(320); return; }

        var spin = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        spin.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        spin.KeyFrames.Add(new EasingDoubleKeyFrame(0.05, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(155))) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseIn } });
        spin.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(310))) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut } });
        spin.KeyFrames.Add(new EasingDoubleKeyFrame(0.05, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(465))) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseIn } });
        spin.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(620))) { EasingFunction = new BackEase { Amplitude = 0.6, EasingMode = EasingMode.EaseOut } });

        var lift = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        lift.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        lift.KeyFrames.Add(new EasingDoubleKeyFrame(-12, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300))) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        lift.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(600))) { EasingFunction = new BackEase { Amplitude = 0.7, EasingMode = EasingMode.EaseIn } });

        LogoScale.BeginAnimation(ScaleTransform.ScaleXProperty, spin);
        if (LogoLift != null) LogoLift.BeginAnimation(TranslateTransform.YProperty, lift);
        if (LogoSKew != null)
        {
            var skew = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
            skew.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            skew.KeyFrames.Add(new EasingDoubleKeyFrame(14, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(155))));
            skew.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(310))));
            skew.KeyFrames.Add(new EasingDoubleKeyFrame(-14, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(465))));
            skew.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(620))));
            LogoSKew.BeginAnimation(SkewTransform.AngleYProperty, skew);
        }

        await Task.Delay(620);

        // 复位：动画都用 FillBehavior.Stop，这里把属性写回静息值，免得下一轮从旧值起跳
        LogoScale.ScaleX = 1;
        if (LogoLift != null) { LogoLift.BeginAnimation(TranslateTransform.YProperty, null); LogoLift.Y = 0; }
        if (LogoSKew != null) { LogoSKew.BeginAnimation(SkewTransform.AngleYProperty, null); LogoSKew.AngleY = 0; }
    }

    private System.Media.SoundPlayer? _coinSound;

    /// <summary>播放内嵌的抛硬币音效（pack 资源，不依赖 exe 旁边有没有文件）；失败只记日志、不打断切换。</summary>
    private void PlayCoinFlipSound()
    {
        try
        {
            if (_coinSound == null)
            {
                var res = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/coin-flip.wav"));
                if (res == null) return;
                _coinSound = new System.Media.SoundPlayer(res.Stream);
                _coinSound.Load();
            }
            _coinSound.Play();
        }
        catch (Exception ex) { Logger.LogError("PlayCoinFlipSound", ex); }
    }

    /// <summary>启动时恢复上次的目标（桌面版不可用则回落 Web），并更新 Logo 提示。</summary>
    private void InitializeGlobalSwitch()
    {
        try
        {
            // 如果用户从未切换过（LastTarget 还是默认的 "web"），使用智能默认（桌面版可用则优先）
            // 否则尊重用户的上次选择
            var want = _settings.LastTarget == "web" 
                ? TargetContext.DefaultTarget() 
                : TargetContext.FromSetting(_settings.LastTarget);
            if (want == GuardTarget.Desktop && !GuardPaths.DesktopExeFound) want = GuardTarget.Web;
            if (want != _ctx.Target) SetTarget(want);
            else ApplyTargetChrome();
        }
        catch (Exception ex) { Logger.LogError("InitializeGlobalSwitch", ex); }
    }

    /// <summary>更新 Logo 的提示文本。由 ApplyTargetChrome 调用。</summary>
    private void UpdateSwitchUI(GuardTarget target)
    {
        if (LogoImage == null) return;
        
        bool deskAvail = GuardPaths.DesktopExeFound;
        
        LogoImage.Cursor = deskAvail ? Cursors.Hand : Cursors.Arrow;
        LogoImage.ToolTip = deskAvail
            ? $"当前管理：{TargetContext.LabelOf(target)}（点击切换 Web 引擎 / 桌面版）"
            : "未检测到 DSH 桌面版（可到「设置 → 路径」填写安装目录）";
        
        LogoImage.Opacity = deskAvail || target == GuardTarget.Web ? 1.0 : 0.7;
    }

    // ── 自检钩子 ──
    internal string? SwitchBlockedReasonForTest(bool desktop)
        => SwitchBlockedReason(desktop ? GuardTarget.Desktop : GuardTarget.Web);
    internal string LastTargetSettingForTest => _settings.LastTarget;
    internal bool PluginOpInFlightForTest => PluginOpInFlight;
    internal PluginCmd InstallCmdForTest(string source) => InstallCmdFor(source);
    internal PluginCmd UninstallCmdForTest(string name) => UninstallCmdFor(name);
    internal string CompatEngineVersionForTest => CompatEngineVersion;
}