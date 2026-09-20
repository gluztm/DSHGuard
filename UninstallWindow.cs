using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DSHGuard;

/// <summary>
/// 卸载界面：与主程序同一套外观（圆角卡片 + 日/夜跟随 + 按钮动效），文案压到最短。
/// **三个互斥选项**（全部清空 / 删除缓存 / 只删除主程序），与安装包里的卸载程序完全一致。
/// 真正的删除交给 Inno 卸载器（/SILENT [/MODE=…]），这里只负责收集用户选择。
/// </summary>
internal sealed class UninstallWindow : Window
{
    /// <summary>按钮实际文案（供自检读取真实值，而不是把期望写死）。</summary>
    private readonly List<string> _buttonLabels = new();

    /// <summary>单选：保留数据（默认选中）。</summary>
    private RadioButton? _keepCheck;
    /// <summary>单选：全部删除（连程序文件夹）。</summary>
    private RadioButton? _wipeCheck;

    /// <summary>用户点了「取消」或直接关窗。</summary>
    internal bool Cancelled { get; private set; } = true;

    /// <summary>选中的卸载方式（取消时无效）。</summary>
    internal UninstallMode Mode { get; private set; } = UninstallMode.AppOnly;

    /// <summary>要一起删掉的数据（逗号分隔；由 <see cref="Mode"/> 推出来，保持旧调用点可用）。</summary>
    internal string DeleteList => UninstallPlan.DeleteDataArg(Mode);

    internal UninstallWindow(bool engineRunning)
    {
        bool dark = ThemeManager.IsDark;
        Color cardColor = dark ? Color.FromRgb(0x1C, 0x20, 0x29) : Color.FromRgb(0xF2, 0xF3, 0xF7);
        Color textColor = dark ? Color.FromRgb(0xF5, 0xF5, 0xF7) : Color.FromRgb(0x1C, 0x1C, 0x1E);
        Color subColor = dark ? Color.FromRgb(0xC7, 0xC7, 0xCC) : Color.FromRgb(0x4A, 0x4A, 0x4C);
        Color borderColor = dark ? Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x26, 0x00, 0x00, 0x00);

        Title = "卸载 DSH 守护壳";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        UseLayoutRounding = true;

        var content = new StackPanel { Margin = new Thickness(22, 20, 22, 20), Width = 400 };
        content.Children.Add(new TextBlock
        {
            Text = "卸载 DSH 守护壳",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(textColor)
        });
        content.Children.Add(new TextBlock
        {
            Text = UninstallPlan.ConfirmText(GuardVersion.Version)
                   + (engineRunning ? "\n（DSH 引擎还在运行，卸载不会停它。）" : ""),
            FontSize = 12.5,
            LineHeight = 19,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(subColor),
            Margin = new Thickness(0, 8, 0, 0)
        });

        // 只问一件事：**保留还是删干净**。同一组单选按钮（原生互斥、不挂任何事件 ⇒ 不会自我递归）
        void AddOption(string label, string hint, bool isChecked, out RadioButton box)
        {
            box = new RadioButton
            {
                Content = label,
                FontSize = 13,
                IsChecked = isChecked,
                Margin = new Thickness(0, 14, 0, 0),
                Foreground = new SolidColorBrush(textColor),
                GroupName = "uninstallMode"
            };
            content.Children.Add(box);
            content.Children.Add(new TextBlock
            {
                Text = hint,
                FontSize = 11,
                Margin = new Thickness(22, 3, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(subColor)
            });
        }
        AddOption(UninstallPlan.KeepDataLabel, UninstallPlan.KeepDataHint, true, out _keepCheck);
        AddOption(UninstallPlan.DeleteAllLabel, UninstallPlan.DeleteAllHint, false, out _wipeCheck);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        buttons.Children.Add(MakeButton("卸载", Color.FromRgb(0xFF, 0x3B, 0x30), UninstallMode.AppOnly, dark));
        buttons.Children.Add(MakeButton("取消", Color.FromRgb(0x8E, 0x8E, 0x93), null, dark));
        content.Children.Add(buttons);

        var card = new Border
        {
            Child = content,
            Background = new SolidColorBrush(cardColor),
            BorderBrush = new SolidColorBrush(borderColor),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Margin = new Thickness(14),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.45, Color = Colors.Black }
        };
        var root = new Grid { Background = Brushes.Transparent };
        root.Children.Add(card);
        ButtonFx.Wire(root);
        card.MouseLeftButtonDown += (_, e) => { try { DragMove(); e.Handled = true; } catch { } };
        Content = root;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Cancelled = true; Close(); e.Handled = true; }
        };
    }

    /// <summary>当前选择对应的卸载方式（只由那一个对勾决定；计算发生在点按钮时，不挂事件 ⇒ 不会自我递归）。</summary>
    private UninstallMode SelectedMode() => UninstallPlan.FromKeepData(_wipeCheck?.IsChecked != true);

    private Border MakeButton(string label, Color bg, UninstallMode? mode, bool dark)
    {
        _buttonLabels.Add(label);
        var b = new Border
        {
            Child = new TextBlock
            {
                Text = label,
                FontSize = 12.5,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            },
            Background = new SolidColorBrush(bg),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(20, 8, 20, 8),
            Margin = new Thickness(8, 0, 0, 0),
            Cursor = Cursors.Hand
        };
        b.MouseLeftButtonDown += (_, e) =>
        {
            if (mode.HasValue) { Mode = SelectedMode(); Cancelled = false; }   // 确定 = 按勾选来
            else Cancelled = true;
            Close();
            e.Handled = true;
        };
        // 悬停/离开都用**当前**主题现算：本窗是模态 ShowDialog，生命周期内主题不会变（无人能点主题按钮），
        // 所以这与"用构造期主题"在当前代码下等价；区别只在于将来若把它改成非模态，这里不会留下旧主题色。
        b.MouseEnter += (_, _) => b.Background = new SolidColorBrush(Shift(bg, ThemeManager.IsDark));
        b.MouseLeave += (_, _) => b.Background = new SolidColorBrush(bg);
        return b;
    }

    /// <summary>
    /// 悬停底色：在"某套主题里的颜色"上做一级明度偏移（夜间提亮、日间压暗）。
    ///
    /// <paramref name="dark"/> 必须是**取色那一刻的当前主题**，不能沿用构造时捕获值。
    /// 原先 <c>MakeButton</c> 的悬停/离开两句用的是构造期捕获的 <c>dark</c>（同一个 lambda 闭包变量），
    /// 属于"捕获构建时的主题"这一模式的最后一处：窗口一旦在主题变化后仍存活，悬停就会算出另一套主题的偏移色。
    /// 悬停态只由鼠标事件驱动、挂上去之后**每一帧都可能再算**，所以它必须现读主题；
    /// 而底色 <paramref name="c"/>、文字色、描边色仍是构造期一次性落下的值 —— 那些要改就得整体重构，
    /// 不在本次收口范围（模态 ShowDialog 期间主窗被禁用，且没有任何定时器/系统事件会改主题，
    /// 所以构造期与当前期在本窗口生命周期内恒等，见下方 MouseEnter 调用点）。
    /// </summary>
    private static Color Shift(Color c, bool dark)
    {
        int d = dark ? 0x18 : -0x18;
        return Color.FromRgb(
            (byte)Math.Clamp(c.R + d, 0, 255),
            (byte)Math.Clamp(c.G + d, 0, 255),
            (byte)Math.Clamp(c.B + d, 0, 255));
    }

    /// <summary>自检用：那个对勾的文案、卸载/取消按钮文案（读真实值）。</summary>
    internal (string KeepLabel, string WipeLabel, string OkLabel, string CancelLabel) LayoutForTest()
        => (_keepCheck?.Content?.ToString() ?? "",
            _wipeCheck?.Content?.ToString() ?? "",
            _buttonLabels.Count > 0 ? _buttonLabels[0] : "",
            _buttonLabels.Count > 1 ? _buttonLabels[1] : "");

    /// <summary>自检用：模拟勾选（只改状态、不挂事件 ⇒ 不会自我递归）。</summary>
    internal void CheckForTest(bool keepUserFiles)
    {
        if (keepUserFiles) { if (_keepCheck != null) _keepCheck.IsChecked = true; }
        else { if (_wipeCheck != null) _wipeCheck.IsChecked = true; }
    }

    /// <summary>自检用：当前选择。</summary>
    internal UninstallMode SelectedForTest() => SelectedMode();

    /// <summary>自检用：按当前选择算出 /DELETE_DATA= 清单。</summary>
    internal string DeleteListForTest() => DeleteList;

    /// <summary>
    /// 交给 Inno 卸载器执行：本进程先退出（要删的就是本 exe，运行中的文件删不掉），
    /// 由 PowerShell 托底延时后拉起静默卸载——与安装包内自定义页同款做法（延迟 2 秒）。
    ///
    /// 旧写法是 <c>cmd /C ping -n 3 127.0.0.1 &gt;NUL &amp; "{unins}" {args}</c>：
    /// &amp; 是 cmd 的命令分隔符、&gt;NUL 是重定向 —— 一旦 unins/args 出现意外内容即可注入。
    /// 现在：直启 powershell.exe（真 exe）+ ArgumentList 逐 token，
    /// Start-Sleep -Seconds 2 后 Start-Process 卸载器；每个参数都是独立 token，
    /// 路径里的空格/元字符不会再被 shell 重新解释。
    /// </summary>
    internal static bool LaunchUninstaller(string installDir, string deleteList, out string detail, string modeArg = "")
    {
        detail = "";
        try
        {
            string unins = Path.Combine(installDir, "unins000.exe");
            if (!File.Exists(unins)) { detail = "找不到卸载程序"; return false; }
            var psi = new ProcessStartInfo
            {
                FileName = Environment.ExpandEnvironmentVariables(
                    @"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-Command");
            // 路径用单引号包住（PowerShell 单引号串无插值语义）；
            // unins 来自本程序安装目录，单引号成对闭合即可安全传递
            psi.ArgumentList.Add(
                "Start-Sleep -Seconds 2; Start-Process -FilePath '" + unins + "'" +
                " -ArgumentList '/SILENT','/NORESTART'" +
                (modeArg.Length > 0 ? ",'/MODE=" + modeArg + "'" : "") +
                (deleteList.Length > 0 ? ",'/DELETE_DATA=" + deleteList + "'" : ""));
            Process.Start(psi)?.Dispose();
            detail = "已交给卸载程序";
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError("LaunchUninstaller", ex);
            detail = ex.Message;
            return false;
        }
    }
}
