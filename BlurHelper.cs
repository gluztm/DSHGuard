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

    /// <summary>启用 Acrylic 背景虚化并裁剪圆角；失败返回 false，界面保持不透明。</summary>
    public static bool EnableAcrylic(Window window, byte alpha = 0x46, byte r = 0x12, byte g = 0x16, byte b = 0x1E)
    {
        LastTintAlphaForTest = alpha;
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return false;

            int darkMode = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

            // Win11：交由 DWM 处理窗口圆角（比 SetWindowRgn 更稳定，且与虚化背景一致）
            int corner = DWMWCP_ROUND;
            int hr = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
            Logger.LogDiagnosis($"[毛玻璃] DWM 圆角偏好 hr={hr}");

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
                if (ok == 0) Logger.LogDiagnosis("[毛玻璃] SetWindowCompositionAttribute 返回 0（系统可能不支持）");
                ApplyRoundedRegion(window, hwnd);
                return ok != 0;
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

    /// <summary>将窗口裁剪为圆角矩形：虚化背景为矩形，不裁剪时四角会露出直角。</summary>
    public static void ApplyRoundedRegion(Window window, IntPtr? hwndIn = null)
    {
        try
        {
            var hwnd = hwndIn ?? new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            double dpi = 1.0;
            try { dpi = VisualTreeHelper.GetDpi(window).DpiScaleX; } catch { }
            int w = (int)Math.Ceiling(window.ActualWidth * dpi);
            int h = (int)Math.Ceiling(window.ActualHeight * dpi);
            if (w <= 0 || h <= 0) return;
            int radius = (int)Math.Round(24 * dpi);   // 与 RootBorder 的 CornerRadius=12 对应

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

    /// <summary>关闭毛玻璃（回退用：将 accent 状态置为 disabled）。</summary>
    public static void DisableAcrylic(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
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
        }
        catch (Exception ex) { Logger.LogError("DisableAcrylic", ex); }
    }
}
