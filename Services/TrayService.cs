using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;

namespace CrosshairWin.Services;

/// <summary>
/// Owns the system tray icon and its context menu.
///
/// The icon bitmap is generated at runtime rather than shipped as a .ico resource,
/// so the project has no binary assets to lose and the icon always matches the
/// crosshair colour.
/// </summary>
internal sealed class TrayService : IDisposable
{
    private TaskbarIcon? _icon;
    private Icon? _generatedIcon;
    private bool _disposed;

    /// <summary>Raised when the user picks "show/hide".</summary>
    public event Action? ToggleVisibilityRequested;

    /// <summary>Raised when the user picks "settings".</summary>
    public event Action? OpenSettingsRequested;

    /// <summary>Raised when the user picks "cycle crosshair".</summary>
    public event Action? CyclePresetRequested;

    /// <summary>Raised when the user toggles "start with Windows".</summary>
    public event Action<bool>? AutoStartToggled;

    /// <summary>Raised when the user picks "exit".</summary>
    public event Action? ExitRequested;

    /// <summary>Creates the tray icon and wires up its menu.</summary>
    public TaskbarIcon Create(bool startWithWindows)
    {
        _icon = new TaskbarIcon
        {
            ToolTipText = "CrosshairWin - 屏幕准星",
            Icon = GenerateIcon()
        };

        _icon.TrayMouseDoubleClick += (_, _) => OpenSettingsRequested?.Invoke();

        var menu = new System.Windows.Controls.ContextMenu();

        var toggle = new System.Windows.Controls.MenuItem { Header = "显示/隐藏准星" };
        toggle.Click += (_, _) => ToggleVisibilityRequested?.Invoke();
        menu.Items.Add(toggle);

        var cycle = new System.Windows.Controls.MenuItem { Header = "切换准星样式" };
        cycle.Click += (_, _) => CyclePresetRequested?.Invoke();
        menu.Items.Add(cycle);

        var settings = new System.Windows.Controls.MenuItem { Header = "设置..." };
        settings.Click += (_, _) => OpenSettingsRequested?.Invoke();
        menu.Items.Add(settings);

        menu.Items.Add(new System.Windows.Controls.Separator());

        var autoStart = new System.Windows.Controls.MenuItem
        {
            Header = "开机自动启动",
            IsCheckable = true,
            IsChecked = startWithWindows
        };
        autoStart.Click += (_, _) => AutoStartToggled?.Invoke(autoStart.IsChecked);
        menu.Items.Add(autoStart);

        menu.Items.Add(new System.Windows.Controls.Separator());

        var exit = new System.Windows.Controls.MenuItem { Header = "退出" };
        exit.Click += (_, _) => ExitRequested?.Invoke();
        menu.Items.Add(exit);

        _icon.ContextMenu = menu;

        return _icon;
    }

    /// <summary>Synchronises the autostart checkmark with the real registry state.</summary>
    public void UpdateAutoStartState(bool enabled)
    {
        if (_icon?.ContextMenu is null)
            return;

        foreach (var item in _icon.ContextMenu.Items)
        {
            if (item is System.Windows.Controls.MenuItem mi && mi.Header as string == "开机自动启动")
            {
                mi.IsChecked = enabled;
                break;
            }
        }
    }

    /// <summary>Shows a balloon/toast notification, if the user enabled them.</summary>
    public void ShowNotification(string title, string message, bool enabled)
    {
        if (!enabled || _icon is null)
            return;

        try
        {
            _icon.ShowBalloonTip(title, message, BalloonIcon.Info);
        }
        catch
        {
            // Notifications are cosmetic; ignore failures on systems where they are blocked.
        }
    }

    /// <summary>
    /// Draws a small crosshair icon into an <see cref="Icon"/>.
    /// Uses System.Drawing only, so no external asset is required.
    /// </summary>
    private Icon GenerateIcon()
    {
        const int size = 32;

        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var pen = new Pen(Color.FromArgb(255, 255, 59, 48), 3f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };

            // Cross with a central gap, matching the app's default crosshair.
            g.DrawLine(pen, 16, 3, 16, 12);
            g.DrawLine(pen, 16, 20, 16, 29);
            g.DrawLine(pen, 3, 16, 12, 16);
            g.DrawLine(pen, 20, 16, 29, 16);

            using var dotBrush = new SolidBrush(Color.FromArgb(255, 255, 255, 255));
            g.FillEllipse(dotBrush, 14, 14, 4, 4);
        }

        // Icon.FromHandle does not own the handle, so we clone into a managed Icon
        // and destroy the temporary HICON to avoid a GDI leak.
        IntPtr handle = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            _generatedIcon = (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }

        return _generatedIcon;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            // TaskbarIcon must be disposed or the icon lingers in the tray until hover.
            _icon?.Dispose();
        }
        catch
        {
            // Best effort.
        }

        try
        {
            _generatedIcon?.Dispose();
        }
        catch
        {
            // Best effort.
        }

        _icon = null;
        _generatedIcon = null;
    }
}
