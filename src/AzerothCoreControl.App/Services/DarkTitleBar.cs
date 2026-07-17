using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace AzerothCoreControl.App.Services;

/// <summary>
/// Makes the OS title bar match the app's dark theme.
/// </summary>
/// <remarks>
/// The caption bar is drawn by Windows, not WPF, so no amount of theming inside the window reaches it — a
/// light bar sits on top of a dark app until DWM is told otherwise. Every call here is best-effort: these
/// attributes were added across different Windows builds, and an unsupported one simply returns an error
/// code, which is why nothing is thrown and each is tried independently.
/// </remarks>
public static class DarkTitleBar
{
    // Dark caption. 20 is the documented value (Win10 20H1+ / Win11); 19 was the pre-20H1 value.
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;

    // Exact caption/border/text colours — Windows 11 (build 22000+) only.
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// Apply to a window. Call from <c>SourceInitialized</c>: the HWND must exist, and doing it before the
    /// window is shown avoids a visible flash of light chrome.
    /// </summary>
    public static void Apply(Window window)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10))
            return;

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        ApplyCore(hwnd, window);
    }

    [SupportedOSPlatform("windows10.0")]
    private static void ApplyCore(IntPtr hwnd, Window window)
    {
        try
        {
            // Dark mode first: this alone fixes Windows 10, and is the fallback if the colour attributes
            // below aren't supported.
            var enabled = 1;
            if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeBefore20H1, ref enabled, sizeof(int));

            // On Windows 11, paint the caption in the app's own gunmetal so the bar and the header read as
            // one surface rather than "dark app, darker-but-different bar". Ignored on Windows 10.
            if (TryGetColorRef(window, "WindowColor", out var caption))
                DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref caption, sizeof(int));
            if (TryGetColorRef(window, "BorderColor", out var border))
                DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref border, sizeof(int));
            if (TryGetColorRef(window, "TextPrimaryColor", out var text))
                DwmSetWindowAttribute(hwnd, DwmwaTextColor, ref text, sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // No dwmapi — nothing to do; the window is merely less pretty.
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    /// <summary>Convert a theme Color resource to a Win32 COLORREF (0x00BBGGRR — byte order is reversed).</summary>
    private static bool TryGetColorRef(Window window, string resourceKey, out int colorRef)
    {
        colorRef = 0;
        if (window.TryFindResource(resourceKey) is not Color c)
            return false;
        colorRef = c.R | (c.G << 8) | (c.B << 16);
        return true;
    }
}
