using System.Runtime.InteropServices;
using CrosshairWin.Interop;

namespace CrosshairWin.Monitors;

/// <summary>
/// A physical-pixel description of one display.
/// All coordinates are in the virtual-desktop physical pixel space, which is the
/// space WPF uses once the process is PerMonitorV2 aware.
/// </summary>
public sealed class MonitorInfo
{
    public required IntPtr Handle { get; init; }

    /// <summary>Stable device name, e.g. <c>\\.\DISPLAY1</c>. Used to persist the choice.</summary>
    public required string DeviceName { get; init; }

    /// <summary>Full bounds in physical pixels, virtual-desktop coordinates.</summary>
    public required int X { get; init; }
    public required int Y { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }

    public required uint Dpi { get; init; }

    public bool IsPrimary { get; init; }

    /// <summary>Scale factor relative to 96 DPI (1.0 = 100%, 1.5 = 150%).</summary>
    public double Scale => Dpi / 96.0;

    public int CenterX => X + Width / 2;
    public int CenterY => Y + Height / 2;

    public string DisplayLabel =>
        $"{(IsPrimary ? "主显示器" : "显示器")} {DeviceName}  {Width}×{Height}  {Scale * 100:0}%";

    public override string ToString() => DisplayLabel;
}

/// <summary>
/// Enumerates monitors and converts between the units WPF uses and physical pixels.
/// </summary>
internal static class MonitorService
{
    /// <summary>
    /// Enumerates all active displays with their effective DPI.
    /// Never throws: on failure it returns a single synthetic entry so the app still runs.
    /// </summary>
    internal static List<MonitorInfo> GetMonitors()
    {
        var result = new List<MonitorInfo>();

        try
        {
            IntPtr primary = NativeMethods.MonitorFromPoint(
                new NativeMethods.POINT(0, 0),
                NativeMethods.MONITOR_DEFAULTTOPRIMARY);

            NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                (IntPtr hMonitor, IntPtr hdc, ref NativeMethods.RECT rect, IntPtr data) =>
                {
                    var mi = new NativeMethods.MONITORINFOEX();
                    mi.cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>();

                    if (!NativeMethods.GetMonitorInfo(hMonitor, ref mi))
                        return true; // skip, keep enumerating

                    uint dpi = 96;
                    try
                    {
                        // GetDpiForMonitor exists on Win8.1+, so it is safe on our floor.
                        // It can still fail for a detached monitor; fall back to 96.
                        if (NativeMethods.GetDpiForMonitor(
                                hMonitor, NativeMethods.MDT_EFFECTIVE_DPI, out uint dx, out _) == 0)
                        {
                            dpi = dx;
                        }
                    }
                    catch (DllNotFoundException)
                    {
                        dpi = 96;
                    }

                    result.Add(new MonitorInfo
                    {
                        Handle = hMonitor,
                        DeviceName = string.IsNullOrWhiteSpace(mi.szDevice) ? "UNKNOWN" : mi.szDevice,
                        X = mi.rcMonitor.Left,
                        Y = mi.rcMonitor.Top,
                        Width = mi.rcMonitor.Width,
                        Height = mi.rcMonitor.Height,
                        Dpi = dpi,
                        IsPrimary = hMonitor == primary
                    });

                    return true;
                }, IntPtr.Zero);
        }
        catch
        {
            // Fall through to the synthetic fallback below.
        }

        if (result.Count == 0)
        {
            // Degraded but functional: assume a single 1920x1080 @96dpi primary.
            result.Add(new MonitorInfo
            {
                Handle = IntPtr.Zero,
                DeviceName = "PRIMARY",
                X = 0,
                Y = 0,
                Width = 1920,
                Height = 1080,
                Dpi = 96,
                IsPrimary = true
            });
        }

        return result;
    }

    /// <summary>Returns the primary monitor, or the first one if none is flagged.</summary>
    internal static MonitorInfo GetPrimary()
    {
        var all = GetMonitors();
        return all.FirstOrDefault(m => m.IsPrimary) ?? all[0];
    }

    /// <summary>
    /// Resolves the monitor a saved <see cref="MonitorInfo.DeviceName"/> refers to.
    /// Falls back to the primary monitor when the device is gone (unplugged/disabled),
    /// which is what keeps the app usable after a hot-plug.
    /// </summary>
    internal static MonitorInfo Resolve(string? deviceName)
    {
        var all = GetMonitors();

        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            var match = all.FirstOrDefault(m =>
                string.Equals(m.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;
        }

        return all.FirstOrDefault(m => m.IsPrimary) ?? all[0];
    }

    /// <summary>
    /// Converts a WPF device-independent value to physical pixels for the given monitor.
    /// WPF still reports window coordinates in DIPs relative to the monitor's own scale,
    /// so this conversion is required to size the overlay exactly to the screen.
    /// </summary>
    internal static double DipToPixels(double dip, MonitorInfo monitor) => dip * monitor.Scale;

    /// <summary>
    /// Converts physical pixels to WPF device-independent units for the given monitor.
    /// </summary>
    internal static double PixelsToDip(double pixels, MonitorInfo monitor)
        => monitor.Scale <= 0 ? pixels : pixels / monitor.Scale;

    /// <summary>
    /// True when the reported monitor layout differs from the supplied snapshot.
    /// Used to skip redundant repositioning work on WM_DISPLAYCHANGE.
    /// </summary>
    internal static bool LayoutChanged(IReadOnlyList<MonitorInfo> a, IReadOnlyList<MonitorInfo> b)
    {
        if (a.Count != b.Count)
            return true;

        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].DeviceName != b[i].DeviceName ||
                a[i].X != b[i].X ||
                a[i].Y != b[i].Y ||
                a[i].Width != b[i].Width ||
                a[i].Height != b[i].Height ||
                a[i].Dpi != b[i].Dpi ||
                a[i].IsPrimary != b[i].IsPrimary)
            {
                return true;
            }
        }

        return false;
    }
}
