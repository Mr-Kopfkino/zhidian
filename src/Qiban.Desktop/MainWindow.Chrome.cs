using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media;

namespace Qiban.Desktop;

public partial class MainWindow
{
    private HwndSource? _chromeSource;

    private void InitializeWindowChrome()
    {
        SourceInitialized += OnChromeSourceInitialized;
        StateChanged += OnChromeStateChanged;
        Activated += OnChromeActivationChanged;
        Deactivated += OnChromeActivationChanged;
        Closed += OnChromeClosed;
        UpdateCaptionState();
    }

    private void OnMinimizeWindow(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void OnMaximizeWindow(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void OnCloseWindow(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    private void OnChromeSourceInitialized(object? sender, EventArgs e)
    {
        _chromeSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _chromeSource?.AddHook(ChromeWindowProc);
        ApplyNativeFrameStyle();
    }

    private void OnChromeStateChanged(object? sender, EventArgs e)
    {
        UpdateCaptionState();
        ApplyNativeFrameStyle();
    }

    private void OnChromeActivationChanged(object? sender, EventArgs e) => ApplyNativeFrameStyle();

    private void UpdateCaptionState()
    {
        bool maximized = WindowState == WindowState.Maximized;
        MaximizeGlyph.Text = maximized ? "\uE923" : "\uE922";
        MaximizeWindowButton.ToolTip = maximized ? "还原" : "最大化";
        AutomationProperties.SetName(MaximizeWindowButton, maximized ? "还原窗口" : "最大化窗口");
    }

    private void ApplyNativeFrameStyle()
    {
        if (_chromeSource == null || _chromeSource.IsDisposed || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        // Win11 owns the shadow and corner clipping; no layered/transparent WPF window is needed.
        // Unsupported DWM attributes return an HRESULT and leave the normal Win10 frame functional.
        // https://learn.microsoft.com/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute
        bool xp = _state.Settings.InterfaceTheme == "xp";
        int corners = WindowState == WindowState.Maximized || xp ? 1 /* DONOTROUND */ : 2 /* ROUND */;
        var color = ((SolidColorBrush)FindResource(IsActive ? "FrameBrush" : "InactiveFrameBrush")).Color;
        int border = color.R | color.G << 8 | color.B << 16; // DWM expects COLORREF (BGR).
        _ = DwmSetWindowAttribute(_chromeSource.Handle, 33 /* WINDOW_CORNER_PREFERENCE */, ref corners, sizeof(int));
        _ = DwmSetWindowAttribute(_chromeSource.Handle, 34 /* BORDER_COLOR */, ref border, sizeof(int));
    }

    private IntPtr ChromeWindowProc(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != 0x0024 /* WM_GETMINMAXINFO */ || lParam == IntPtr.Zero) return IntPtr.Zero;
        var monitor = MonitorFromWindow(window, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;

        // The native message and monitor rectangles are device pixels in this per-monitor-aware app.
        // Do not use SystemParameters.WorkArea: it describes only the primary monitor in WPF units.
        // https://learn.microsoft.com/windows/win32/api/winuser/ns-winuser-minmaxinfo
        var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        limits.MaxPosition.X = info.Work.Left - info.Monitor.Left;
        limits.MaxPosition.Y = info.Work.Top - info.Monitor.Top;
        limits.MaxSize.X = info.Work.Right - info.Work.Left;
        limits.MaxSize.Y = info.Work.Bottom - info.Work.Top;
        var dpi = VisualTreeHelper.GetDpi(this);
        limits.MinTrackSize.X = Math.Max(limits.MinTrackSize.X, (int)Math.Ceiling(MinWidth * dpi.DpiScaleX));
        limits.MinTrackSize.Y = Math.Max(limits.MinTrackSize.Y, (int)Math.Ceiling(MinHeight * dpi.DpiScaleY));
        Marshal.StructureToPtr(limits, lParam, false);
        handled = true;
        return IntPtr.Zero;
    }

    private void OnChromeClosed(object? sender, EventArgs e)
    {
        if (_chromeSource is { IsDisposed: false }) _chromeSource.RemoveHook(ChromeWindowProc);
        _chromeSource = null;
        SourceInitialized -= OnChromeSourceInitialized;
        StateChanged -= OnChromeStateChanged;
        Activated -= OnChromeActivationChanged;
        Deactivated -= OnChromeActivationChanged;
        Closed -= OnChromeClosed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor, Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
