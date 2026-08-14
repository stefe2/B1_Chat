using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace b1_chat_console.Services;

/// <summary>
/// Fits the main window into the work area of the monitor where WPF created it. All native
/// calculations stay in physical pixels so mixed-DPI monitor layouts cannot push the title bar
/// outside the smaller display. The work area excludes the taskbar.
/// </summary>
public static class WindowPlacement
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const int MarginDip = 12;

    public static void FitMainWindowToCurrentMonitor(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
            ApplyNow(window);
        else
            window.SourceInitialized += (_, _) => ApplyNow(window);
    }

    internal static WindowPixelBounds CalculateCenteredBounds(
        WindowPixelBounds workArea, int preferredWidth, int preferredHeight, int margin)
    {
        var safeMargin = Math.Max(0, margin);
        var availableWidth = Math.Max(1, workArea.Width - safeMargin * 2);
        var availableHeight = Math.Max(1, workArea.Height - safeMargin * 2);
        var width = Math.Min(Math.Max(1, preferredWidth), availableWidth);
        var height = Math.Min(Math.Max(1, preferredHeight), availableHeight);
        var left = workArea.Left + (workArea.Width - width) / 2;
        var top = workArea.Top + (workArea.Height - height) / 2;
        return new WindowPixelBounds(left, top, width, height);
    }

    private static void ApplyNow(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var current)) return;

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return;
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return;

        var workArea = new WindowPixelBounds(
            info.Work.Left,
            info.Work.Top,
            Math.Max(1, info.Work.Right - info.Work.Left),
            Math.Max(1, info.Work.Bottom - info.Work.Top));
        var currentWidth = Math.Max(1, current.Right - current.Left);
        var dpi = GetDpiForWindow(hwnd);
        var margin = (int)Math.Round(MarginDip * (dpi == 0 ? 1.0 : dpi / 96.0));

        // Preserve the established 1500-DIP width when it fits. Height intentionally fills the
        // usable monitor, as before, but now with a small safety margin on that same monitor.
        var bounds = CalculateCenteredBounds(
            workArea, currentWidth, workArea.Height, margin);
        SetWindowPos(
            hwnd, IntPtr.Zero, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
    }

    internal readonly record struct WindowPixelBounds(int Left, int Top, int Width, int Height);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
