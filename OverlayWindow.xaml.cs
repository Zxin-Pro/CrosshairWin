using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using CrosshairWin.Interop;
using CrosshairWin.Models;
using CrosshairWin.Monitors;
using CrosshairWin.Services;

namespace CrosshairWin;

/// <summary>
/// The click-through crosshair overlay.
///
/// Window behaviour is achieved with three cooperating mechanisms:
///  1. WPF: WindowStyle=None, AllowsTransparency=True, Background=Transparent,
///     Topmost=True, ShowInTaskbar=False, ResizeMode=NoResize, ShowActivated=False.
///  2. Win32 extended styles applied in <see cref="OnSourceInitialized"/>:
///     WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE.
///  3. An explicit SetWindowPos(HWND_TOPMOST) after style changes, because changing
///     the extended style can reset z-order.
/// </summary>
public partial class OverlayWindow : Window
{
    /// <summary>
    /// Half-size of the overlay window in physical pixels. The window is
    /// (2 * HalfWindowSize) square, so large image crosshairs up to 1024px still fit.
    /// </summary>
    private const int HalfWindowSize = 512;

    private readonly MonitorServiceHost _monitorHost;

    /// <summary>
    /// Low-frequency watchdog that re-validates an image preset's source file.
    ///
    /// WPF will not repaint a window whose content has not changed, so if the user
    /// deletes or replaces the image while the app runs, nothing would trigger a
    /// re-render and the stale bitmap would stay on screen. This timer only runs for
    /// image presets and does a single stat call, so the cost is negligible.
    /// </summary>
    private System.Windows.Threading.DispatcherTimer? _imageWatchdog;

    private MonitorInfo _targetMonitor;
    private CrosshairPreset? _preset;
    private readonly ImageSourceCache _imageCache = new();

    /// <summary>Set when the active image preset could not be loaded.</summary>
    public bool ImageFallbackActive { get; private set; }

    /// <summary>Raised when an image preset fails to load, so the app can notify once.</summary>
    public event Action<string>? ImageLoadFailed;

    public OverlayWindow(MonitorInfo primaryMonitor)
    {
        InitializeComponent();

        _targetMonitor = primaryMonitor;
        _monitorHost = new MonitorServiceHost();

        StartImageWatchdog();
    }

    /// <summary>
    /// Starts the watchdog. It runs on the UI thread so it can safely invalidate the
    /// visual, and only does real work while an image preset is active.
    /// </summary>
    private void StartImageWatchdog()
    {
        _imageWatchdog = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2)
        };

        _imageWatchdog.Tick += (_, _) =>
        {
            try
            {
                // Nothing to watch for built-in shapes.
                if (_preset?.Shape != CrosshairShape.Image)
                    return;

                string? absolute = AppPaths.ResolveImage(_preset.ImageRelativePath);

                // File vanished -> clear the cache and repaint, which makes the canvas
                // draw its built-in cross fallback instead of stale content.
                if (absolute is null)
                {
                    if (!ImageFallbackActive)
                    {
                        ImageFallbackActive = true;
                        _imageCache.Invalidate();
                        Crosshair.InvalidateVisual();

                        ImageLoadFailed?.Invoke(
                            "图片准星文件已丢失，已自动回退为内置十字准星。请在设置中重新选择图片。");
                    }

                    return;
                }

                // File present: force a repaint only when the cache entry was invalidated
                // (i.e. the file was replaced or its timestamp changed).
                if (_imageCache.Get(absolute, _preset.ImageRelativePath) is null)
                {
                    _imageCache.Invalidate();
                    ImageFallbackActive = false;
                    Crosshair.InvalidateVisual();
                }
            }
            catch
            {
                // The watchdog must never crash the overlay.
            }
        };

        _imageWatchdog.Start();
    }

    /// <summary>Current target monitor.</summary>
    public MonitorInfo TargetMonitor => _targetMonitor;

    /// <summary>Applies a new preset and repaints.</summary>
    public void ApplyPreset(CrosshairPreset preset)
    {
        _preset = preset;
        Crosshair.Preset = preset;
        Crosshair.ImageProvider = ResolveImage;
        Crosshair.TargetScreenHeight = _targetMonitor.Height;
        Crosshair.InvalidateVisual();
    }

    /// <summary>Moves the overlay to the centre of the given monitor.</summary>
    public void MoveToMonitor(MonitorInfo monitor)
    {
        _targetMonitor = monitor;
        Reposition();

        if (_preset is not null)
            Crosshair.TargetScreenHeight = monitor.Height;
    }

    /// <summary>
    /// Repositions and resizes the window so the crosshair sits at the exact centre
    /// of the target monitor, correct under any per-monitor DPI scaling.
    /// </summary>
    public void Reposition()
    {
        try
        {
            var monitor = _targetMonitor;

            // Physical-pixel placement: WPF window Left/Top are in DIPs for the
            // monitor the window is on, so we convert using that monitor's scale.
            double scale = monitor.Scale <= 0 ? 1.0 : monitor.Scale;

            int sizePx = HalfWindowSize * 2;

            // Centre in physical pixels, then express as DIPs relative to the monitor origin.
            int centerPxX = monitor.CenterX;
            int centerPxY = monitor.CenterY;

            int leftPx = centerPxX - HalfWindowSize;
            int topPx = centerPxY - HalfWindowSize;

            // Position: WPF expects DIPs, and for a PerMonitorV2 process the virtual
            // desktop origin maps 1:1 with physical pixels divided by the target scale.
            Left = leftPx / scale;
            Top = topPx / scale;
            Width = sizePx / scale;
            Height = sizePx / scale;

            // Re-assert topmost; some drivers demote layered windows on resize.
            WindowStyles.AssertTopmost(this);

            Crosshair.InvalidateVisual();
        }
        catch
        {
            // A transient failure (monitor removed mid-flight) must not crash the app;
            // the display-change handler will reposition us again shortly.
        }
    }

    /// <summary>Shows the overlay without activating it.</summary>
    public void ShowOverlay()
    {
        try
        {
            if (!IsVisible)
                Show();

            Reposition();
            WindowStyles.AssertTopmost(this);
        }
        catch
        {
            // Ignore: caller re-checks state.
        }
    }

    /// <summary>Hides the overlay without disposing it.</summary>
    public void HideOverlay()
    {
        try
        {
            if (IsVisible)
                Hide();
        }
        catch
        {
            // Ignore.
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Apply the Win32 styles that implement transparency, click-through,
        // no-activate and taskbar hiding.
        WindowStyles.ApplyOverlayStyles(this, clickThrough: true);

        // Hook window messages so we can react to display/DPI changes.
        var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        source?.AddHook(WndProc);

        // Ensure the window never takes focus, even transiently.
        try
        {
            var helper = new WindowInteropHelper(this);
            NativeMethods.SetWindowPos(
                helper.Handle,
                NativeMethods.HWND_TOPMOST,
                0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
        catch
        {
            // Non-fatal.
        }
    }

    /// <summary>
    /// Reacts to display topology and DPI changes so the crosshair stays centred
    /// after resolution changes, monitor hot-plug, and scaling changes.
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case NativeMethods.WM_DISPLAYCHANGE:
                HandleDisplayChange();
                break;

            case NativeMethods.WM_DPICHANGED:
                HandleDisplayChange();
                break;

            case NativeMethods.WM_SETTINGCHANGE:
                // Covers some resolution/scaling changes that do not raise DISPLAYCHANGE.
                HandleDisplayChange();
                break;
        }

        return IntPtr.Zero;
    }

    private void HandleDisplayChange()
    {
        // Debounce: DISPLAYCHANGE arrives in bursts while the user drags a
        // resolution slider. Reposition once things settle.
        _monitorHost.Schedule(TimeSpan.FromMilliseconds(400), () =>
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var monitors = MonitorService.GetMonitors();

                    // Re-resolve our target by device name; if it is gone, fall back
                    // to the primary monitor.
                    var match = monitors.FirstOrDefault(m =>
                        string.Equals(m.DeviceName, _targetMonitor.DeviceName,
                                      StringComparison.OrdinalIgnoreCase));

                    _targetMonitor = match
                                     ?? monitors.FirstOrDefault(m => m.IsPrimary)
                                     ?? monitors[0];

                    if (_preset?.Shape == CrosshairShape.Image)
                        _imageCache.Invalidate();

                    Reposition();
                }
                catch
                {
                    // Never let a display change crash the overlay.
                }
            }));
        });
    }

    /// <summary>
    /// Resolves the bitmap for an image preset. Returns null when the image is
    /// missing/corrupt, which makes the canvas draw the built-in cross instead.
    /// </summary>
    private ImageSource? ResolveImage(CrosshairPreset preset)
    {
        if (preset.Shape != CrosshairShape.Image)
            return null;

        string? absolute = AppPaths.ResolveImage(preset.ImageRelativePath);

        if (absolute is null)
        {
            if (!ImageFallbackActive)
            {
                ImageFallbackActive = true;
                ImageLoadFailed?.Invoke(
                    "图片准星文件缺失，已临时回退为内置十字准星。请在设置中重新选择图片。");
            }

            return null;
        }

        var cached = _imageCache.Get(absolute, preset.ImageRelativePath);
        if (cached is not null)
        {
            ImageFallbackActive = false;
            return cached;
        }

        var result = ImageLoader.Load(absolute);
        if (!result.Success || result.Image is null)
        {
            if (!ImageFallbackActive)
            {
                ImageFallbackActive = true;
                ImageLoadFailed?.Invoke(
                    $"{result.Error} 已临时回退为内置十字准星。");
            }

            return null;
        }

        ImageFallbackActive = false;
        _imageCache.Set(absolute, preset.ImageRelativePath, result.Image);
        return result.Image;
    }

    /// <summary>Invalidates the cached image (called after the user picks a new file).</summary>
    public void InvalidateImageCache() => _imageCache.Invalidate();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // The overlay must never be closed by the user/system; the app hides it instead.
        e.Cancel = true;

        // Only stop the watchdog for a genuine shutdown (Application.Shutdown), which
        // is the one path where the app is really exiting.
        if (Application.Current?.Dispatcher.HasShutdownStarted == true)
        {
            _imageWatchdog?.Stop();
            e.Cancel = false;
        }
    }

    /// <summary>
    /// Small wrapper that keeps one decoded bitmap per preset, avoiding a disk read
    /// on every render pass.
    ///
    /// The cache deliberately re-validates the source file (existence + write time)
    /// on every lookup: the user can delete or replace the image at any moment, and a
    /// stale bitmap must not keep being drawn. The stat call is far cheaper than
    /// re-decoding the image, so this keeps the hot path fast without going stale.
    /// </summary>
    private sealed class ImageSourceCache
    {
        private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

        private sealed class Entry
        {
            public required string RelativePath { get; init; }
            public required ImageSource Image { get; init; }
            public required DateTime LastWriteUtc { get; init; }
        }

        public ImageSource? Get(string absolutePath, string? relativePath)
        {
            if (!_entries.TryGetValue(absolutePath, out var entry))
                return null;

            // Reject the cache entry if the config now points somewhere else.
            if (!string.Equals(entry.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase))
                return null;

            // Reject if the file disappeared or was modified since we decoded it.
            try
            {
                var info = new FileInfo(absolutePath);

                if (!info.Exists || info.LastWriteTimeUtc != entry.LastWriteUtc)
                {
                    _entries.Remove(absolutePath);
                    return null;
                }
            }
            catch
            {
                // If we cannot stat the file, treat the entry as unusable.
                _entries.Remove(absolutePath);
                return null;
            }

            return entry.Image;
        }

        public void Set(string absolutePath, string? relativePath, ImageSource image)
        {
            DateTime stamp;
            try
            {
                stamp = File.GetLastWriteTimeUtc(absolutePath);
            }
            catch
            {
                stamp = DateTime.MinValue;
            }

            _entries[absolutePath] = new Entry
            {
                RelativePath = relativePath ?? string.Empty,
                Image = image,
                LastWriteUtc = stamp
            };
        }

        public void Invalidate() => _entries.Clear();
    }
}

/// <summary>
/// Tiny debounce helper used for display-change bursts.
/// Keeps a single pending timer and replaces the callback on each request.
/// </summary>
internal sealed class MonitorServiceHost
{
    private readonly object _gate = new();
    private System.Threading.Timer? _timer;
    private Action? _pending;

    /// <summary>Schedules <paramref name="action"/> to run once after the delay.</summary>
    public void Schedule(TimeSpan delay, Action action)
    {
        lock (_gate)
        {
            _pending = action;

            _timer ??= new System.Threading.Timer(_ =>
            {
                Action? toRun;
                lock (_gate)
                {
                    toRun = _pending;
                    _pending = null;
                }

                try
                {
                    toRun?.Invoke();
                }
                catch
                {
                    // Never let a timer callback crash the process.
                }
            }, null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);

            _timer.Change(delay, System.Threading.Timeout.InfiniteTimeSpan);
        }
    }
}
