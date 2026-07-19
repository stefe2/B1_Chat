using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace b1_chat_console.Services;

/// <summary>
/// Recolors the native Win32 title bar to match the app's dark theme (DWM API, Windows 11
/// 22H2+) — the OS title bar otherwise ignores WPF's own dark chrome entirely, leaving a
/// jarring white bar above the app's own dark header. Fails silently on older Windows
/// (DwmSetWindowAttribute just returns a failure HRESULT, ignored here) — Snap Layouts,
/// dragging and resizing all stay fully native, only the paint colors change.
/// </summary>
public static class DarkTitleBar
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    // COLORREF (0x00BBGGRR) — mirrors Theme.xaml: Bg2Brush (#3A3D42) for caption/border,
    // TextBrush (#ECEEEF) for the title text. Kept as literals (not read from the ResourceDictionary)
    // since this runs before/independent of the window's own visual tree being built.
    private const int CaptionColor = 0x00423D3A;
    private const int TextColor = 0x00EFEEEC;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public static void Apply(Window window)
    {
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) ApplyNow(window);
        else window.SourceInitialized += (_, _) => ApplyNow(window);
    }

    private static void ApplyNow(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        int darkMode = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
        int caption = CaptionColor;
        DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
        int border = CaptionColor;
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
        int text = TextColor;
        DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref text, sizeof(int));
    }
}
