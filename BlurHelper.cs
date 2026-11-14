using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DSHGuard;

/// <summary>
/// 暗色毛玻璃（Acrylic）效果：背景虚化 + 半透明。
///   · 不使用 DWM Mica：Mica 要求窗口不是分层窗口（WS_EX_LAYERED），
///     而本程序需 <c>WindowStyle=None + AllowsTransparency=True</c> 实现圆角无边框，DWM 背景板会被忽略。
///   · 改用 Win10 1803+ 的 <c>SetWindowCompositionAttribute</c> + <c>ACCENT_ENABLE_ACRYLICBLURBEHIND</c>：
///     该项专用于分层窗口，对窗口后方屏幕内容做虚化后再叠加一层着色（GradientColor，AABBGGRR）。
///   · 虚化范围为整块窗口矩形，圆角需由 <c>SetWindowRgn</c> 裁剪成圆角矩形，否则四角为直角。
///   · 系统不支持或调用失败时返回 false，界面仍按原不透明样式显示。
/// </summary>
internal static class BlurHelper
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr hRgn, bool redraw);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    private enum AccentState
    {
        ACCENT_DISABLED = 0,
        ACCENT_ENABLE_GRADIENT = 1,
        ACCENT_ENABLE_TRANSPARENTGRADIENT = 2,
        ACCENT_ENABLE_BLURBEHIND = 3,
        ACCENT_ENABLE_ACRYLICBLURBEHIND = 4,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public AccentState AccentState;
        public int AccentFlags;
        public int GradientColor;   // 0xAABBGGRR
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    /// <summary>最近一次使用的着色 alpha（自检用）。</summary>
    public static byte LastTintAlphaForTest { get; private set; }

    /// <summary>最近一次同步到 DWM 的「沉浸式深色」取值（自检/诊断用；未同步过为 null）。</summary>
    internal static bool? LastChromeDarkForTest { get; private set; }

    /// <summary>
    /// 当前开着 Acrylic 的窗口句柄。<see cref="ApplyRoundedRegion"/> 只对这里的窗口生效：
    /// 主窗在启用毛玻璃时挂了 SizeChanged ⇒ ApplyRoundedRegion，关掉毛玻璃后这条订阅仍在，
    /// 若不设闸，一次缩放/最大化就会把 GDI 硬裁剪区重新装回去 ⇒ 纯色模式边角又出现锯齿。
    /// 只在 UI 线程读写（开关毛玻璃、尺寸变化都在 UI 线程）。
    /// </summary>
    private static readonly System.Collections.Generic.HashSet<IntPtr> _acrylicWindows = new();

    /// <summary>某窗口当前是否处于 Acrylic 状态（自检/诊断用）。</summary>
    internal static bool IsAcrylicOnForTest(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            return hwnd != IntPtr.Zero && _acrylicWindows.Contains(hwnd);
        }
        catch { return false; }
    }

    /// <summary>
    /// 按主题同步窗口的 DWM 外观：沉浸式深色（影响系统绘制的边框/阴影/标题区配色）+ Win11 圆角偏好。
    ///
    /// 必须**跟着主题走**：改前 <see cref="EnableAcrylic"/> 恒写 <c>DWMWA_USE_IMMERSIVE_DARK_MODE = 1</c>，
    /// 日间模式下系统边框/阴影仍按深色绘制，浅色窗口外沿出现一圈发灰的暗边。
    /// 由主窗在 Loaded 与每次 ApplyTheme 时调用；没有句柄（窗口未显示）时静默返回。
    /// 旧系统不支持这两个属性时 DwmSetWindowAttribute 返回失败 HRESULT，这里只记诊断、不抛。
    /// </summary>
    public static void SyncThemeChrome(Window window, bool dark)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            SyncThemeChrome(hwnd, dark);
        }
        catch (Exception ex) { Logger.LogError("SyncThemeChrome", ex); }
    }

    private static void SyncThemeChrome(IntPtr hwnd, bool dark)
    {
        int darkMode = dark ? 1 : 0;
        int hrDark = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
        int corner = DWMWCP_ROUND;
        int hrCorner = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        LastChromeDarkForTest = dark;
        Logger.LogDiagnosis($"[窗口外观] 深色={(dark ? 1 : 0)} hr={hrDark} · 圆角偏好 hr={hrCorner}");
    }

    /// <summary>
    /// 启用 Acrylic 背景虚化并裁剪圆角；失败返回 false，界面保持不透明。
    /// <paramref name="dark"/>：当前主题（同步 DWM 沉浸式深色；旧调用不传时按夜间处理，与改前行为一致）。
    /// </summary>
    public static bool EnableAcrylic(Window window, byte alpha = 0x46, byte r = 0x12, byte g = 0x16, byte b = 0x1E, bool dark = true)
    {
        LastTintAlphaForTest = alpha;
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return false;

            // 沉浸式深色跟主题走 + Win11 圆角偏好（比 SetWindowRgn 更稳定，且与虚化背景一致）
            SyncThemeChrome(hwnd, dark);

            // GradientColor 为 0xAABBGGRR 格式（字节序与 ARGB 相反）
            int tint = (alpha << 24) | (b << 16) | (g << 8) | r;
            var policy = new AccentPolicy
            {
                AccentState = AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND,
                AccentFlags = 2,
                GradientColor = tint,
                AnimationId = 0
            };

            int size = Marshal.SizeOf<AccentPolicy>();
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(policy, ptr, false);
                var data = new WindowCompositionAttributeData { Attribute = 19 /* WCA_ACCENT_POLICY */, Data = ptr, SizeOfData = size };
                int ok = SetWindowCompositionAttribute(hwnd, ref data);
                if (ok == 0)
                {
                    Logger.LogDiagnosis("[毛玻璃] SetWindowCompositionAttribute 返回 0（系统可能不支持）");
                    _acrylicWindows.Remove(hwnd);
                    return false;
                }
                _acrylicWindows.Add(hwnd);
                ApplyRoundedRegion(window, hwnd);
                return true;
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }
        catch (Exception ex)
        {
            Logger.LogError("EnableAcrylic", ex);
            return false;
        }
    }

    /// <summary>
    /// 仅替换着色（日/夜切换时使用，不影响其它设置）。
    /// 同样的着色不重复下发：Acrylic 每次下发都会让 DWM 重新合成，刷得越勤越容易看见"发飘"。
    /// </summary>
    public static void SetTint(Window window, byte alpha, byte r, byte g, byte b)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int tint = (alpha << 24) | (b << 16) | (g << 8) | r;
            if (tint == _lastTint) { TintSkips++; return; }      // 一样就不下发
            _lastTint = tint;

            var policy = new AccentPolicy { AccentState = AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND, AccentFlags = 2, GradientColor = tint };
            int size = Marshal.SizeOf<AccentPolicy>();
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(policy, ptr, false);
                var data = new WindowCompositionAttributeData { Attribute = 19, Data = ptr, SizeOfData = size };
                SetWindowCompositionAttribute(hwnd, ref data);
                TintApplies++;
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }
        catch (Exception ex) { Logger.LogError("SetTint", ex); }
    }

    private static int _lastTint;
    /// <summary>自检用：着色下发次数 / 因重复被跳过的次数。</summary>
    internal static int TintApplies { get; private set; }
    internal static int TintSkips { get; private set; }
    internal static void ResetTintCountersForTest() { TintApplies = 0; TintSkips = 0; _lastTint = 0; }
    internal static void ForgetLastTintForTest() => _lastTint = 0;

    /// <summary>
    /// 拖动/缩放期间挂起 Acrylic：Windows 的 Acrylic 在窗口移动时会明显滞后（俗称"发飘"），
    /// 期间退回普通模糊，松手后再恢复，既跟手又省一点合成开销。
    /// </summary>
    public static void SuspendForMove(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            var policy = new AccentPolicy { AccentState = AccentState.ACCENT_ENABLE_BLURBEHIND, AccentFlags = 0, GradientColor = 0 };
            int size = Marshal.SizeOf<AccentPolicy>();
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(policy, ptr, false);
                var data = new WindowCompositionAttributeData { Attribute = 19, Data = ptr, SizeOfData = size };
                SetWindowCompositionAttribute(hwnd, ref data);
                _suspendedForMove = true;
                Logger.LogDiagnosis("[毛玻璃] 拖动开始 → 暂时退回普通模糊");
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }
        catch (Exception ex) { Logger.LogError("SuspendForMove", ex); }
    }

    /// <summary>拖动结束：恢复 Acrylic 并重新下发当前着色。</summary>
    public static void ResumeAfterMove(Window window, (byte A, byte R, byte G, byte B) tint)
    {
        try
        {
            if (!_suspendedForMove) return;
            _suspendedForMove = false;
            _lastTint = 0;          // 强制重新下发一次（否则会被去重逻辑跳过）
            SetTint(window, tint.A, tint.R, tint.G, tint.B);
            Logger.LogDiagnosis("[毛玻璃] 拖动结束 → 已恢复 Acrylic");
        }
        catch (Exception ex) { Logger.LogError("ResumeAfterMove", ex); }
    }

    private static bool _suspendedForMove;
    internal static bool SuspendedForMoveForTest => _suspendedForMove;

    /// <summary>
    /// 将窗口裁剪为圆角矩形：虚化背景为矩形，不裁剪时四角会露出直角。
    /// 只对开着 Acrylic 的窗口生效（见 <see cref="_acrylicWindows"/>）：纯色模式下四角由 WPF 自己的
    /// RootBorder 圆角做抗锯齿透明，GDI 硬裁剪区反而会把边缘切出一圈锯齿。
    /// </summary>
    public static void ApplyRoundedRegion(Window window, IntPtr? hwndIn = null)
    {
        try
        {
            var hwnd = hwndIn ?? new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            if (!_acrylicWindows.Contains(hwnd))
            {
                Logger.LogDiagnosis("[毛玻璃] 当前未开毛玻璃 → 跳过圆角硬裁剪（纯色模式由 RootBorder 抗锯齿圆角负责）");
                return;
            }
            double dpi = 1.0;
            try { dpi = VisualTreeHelper.GetDpi(window).DpiScaleX; } catch { }
            int w = (int)Math.Ceiling(window.ActualWidth * dpi);
            int h = (int)Math.Ceiling(window.ActualHeight * dpi);
            if (w <= 0 || h <= 0) return;
            // 与 RootBorder 的 CornerRadius=14 对应（CreateRoundRectRgn 收的是椭圆**直径**，故 ×2）
            int radius = (int)Math.Round(28 * dpi);

            IntPtr rgn = CreateRoundRectRgn(0, 0, w + 1, h + 1, radius, radius);
            if (rgn == IntPtr.Zero) { Logger.LogDiagnosis("[毛玻璃] CreateRoundRectRgn 失败"); return; }
            if (SetWindowRgn(hwnd, rgn, true) == 0)
            {
                DeleteObject(rgn);
                Logger.LogDiagnosis("[毛玻璃] SetWindowRgn 失败（圆角裁剪没生效，改靠 DWM 圆角偏好）");
            }
            else
            {
                Logger.LogDiagnosis($"[毛玻璃] 圆角裁剪已应用 {w}x{h} r={radius}");
            }
        }
        catch (Exception ex) { Logger.LogError("ApplyRoundedRegion", ex); }
    }

    /// <summary>
    /// 关闭毛玻璃（回退到纯色）：
    ///   ① accent 置为 disabled；
    ///   ② <c>SetWindowRgn(hwnd, NULL, true)</c> 清掉开毛玻璃时装上的 GDI 硬裁剪区 ——
    ///      改前只做了 ①，硬裁剪区仍在，纯色窗口的四角被按整像素切掉，出现明显锯齿；
    ///   ③ 维持 DWM 圆角偏好与当前主题的沉浸式深色（<paramref name="dark"/>）。
    /// </summary>
    public static void DisableAcrylic(Window window, bool dark = true)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            _acrylicWindows.Remove(hwnd);
            _suspendedForMove = false;
            _lastTint = 0;      // 下次重新开毛玻璃时，SetTint 一定真正下发
            var policy = new AccentPolicy { AccentState = AccentState.ACCENT_DISABLED, AccentFlags = 0, GradientColor = 0 };
            int size = Marshal.SizeOf<AccentPolicy>();
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(policy, ptr, false);
                var data = new WindowCompositionAttributeData { Attribute = 19, Data = ptr, SizeOfData = size };
                SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally { Marshal.FreeHGlobal(ptr); }

            // 区域交给系统后由系统负责释放；传 NULL = 撤掉裁剪，恢复整窗（含 WPF 抗锯齿透明圆角）
            if (SetWindowRgn(hwnd, IntPtr.Zero, true) == 0)
                Logger.LogDiagnosis("[毛玻璃] 清除圆角硬裁剪失败（SetWindowRgn NULL 返回 0）");
            SyncThemeChrome(hwnd, dark);
            Logger.LogDiagnosis("[毛玻璃] 已关闭：accent=disabled · 硬裁剪已清除 · 维持 DWM 圆角");
        }
        catch (Exception ex) { Logger.LogError("DisableAcrylic", ex); }
    }
}
