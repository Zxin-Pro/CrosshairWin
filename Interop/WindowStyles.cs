using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using CrosshairWin.Interop;

namespace CrosshairWin.Interop;

/// <summary>
/// Applies the extended window styles that make the overlay transparent,
/// click-through, topmost, non-activating and hidden from the taskbar/alt-tab.
/// </summary>
internal static class WindowStyles
{
    /// <summary>
    /// Applied to the overlay HWND after it has a source handle.
    /// </summary>
    internal static void ApplyOverlayStyles(Window window, bool clickThrough)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        long exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();

        // WS_EX_LAYERED        : required so WPF's transparent window composites correctly.
        // WS_EX_TOOLWINDOW     : keeps it out of the taskbar and the Alt+Tab list.
        // WS_EX_NOACTIVATE     : clicking it must never steal focus from the game/desktop.
        exStyle |= NativeMethods.WS_EX_LAYERED
                 | NativeMethods.WS_EX_TOOLWINDOW
                 | NativeMethods.WS_EX_NOACTIVATE;

        if (clickThrough)
        {
            // WS_EX_TRANSPARENT makes hit-testing pass through to whatever is underneath.
            exStyle |= NativeMethods.WS_EX_TRANSPARENT;
        }
        else
        {
            exStyle &= ~(long)NativeMethods.WS_EX_TRANSPARENT;
        }

        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));
    }

    /// <summary>
    /// Toggles click-through at runtime without recreating the window.
    /// </summary>
    internal static void SetClickThrough(Window window, bool clickThrough)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        long exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        if (clickThrough)
            exStyle |= NativeMethods.WS_EX_TRANSPARENT;
        else
            exStyle &= ~(long)NativeMethods.WS_EX_TRANSPARENT;

        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));
    }

    /// <summary>
    /// Re-asserts topmost. Some full-screen apps and shell transitions can push the
    /// overlay down; calling this on a timer/event keeps it above the desktop.
    /// </summary>
    internal static void AssertTopmost(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_TOPMOST,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE
                | NativeMethods.SWP_NOSIZE
                | NativeMethods.SWP_NOACTIVATE);
    }
}
