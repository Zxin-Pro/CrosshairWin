// System.Drawing and System.Windows.Media both define Color, Pen and Brush, so the
// GDI+ (tray icon) and WPF (context menu) usages need explicit aliases.
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Hardcodet.Wpf.TaskbarNotification;
using GdiColor = System.Drawing.Color;
using GdiPen = System.Drawing.Pen;
using WpfColor = System.Windows.Media.Color;
using WpfCursors = System.Windows.Input.Cursors;
using WpfGridLength = System.Windows.GridLength;
using WpfGridUnitType = System.Windows.GridUnitType;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;

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

        var menu = new ContextMenu
        {
            // Match the settings window's dark theme so the app looks consistent.
            Background = new WpfSolidColorBrush(WpfColor.FromRgb(0x1C, 0x1F, 0x25)),
            Foreground = new WpfSolidColorBrush(WpfColor.FromRgb(0xE6, 0xE8, 0xEC)),
            BorderBrush = new WpfSolidColorBrush(WpfColor.FromRgb(0x34, 0x39, 0x46)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4),
            FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI, Segoe UI"),
            FontSize = 13
        };

        menu.Items.Add(CreateMenuItem("显示/隐藏准星", "F8", () => ToggleVisibilityRequested?.Invoke()));
        menu.Items.Add(CreateMenuItem("切换准星样式", "F9", () => CyclePresetRequested?.Invoke()));
        menu.Items.Add(CreateMenuItem("设置...", "F10", () => OpenSettingsRequested?.Invoke()));
        menu.Items.Add(CreateSeparator());

        var autoStart = new MenuItem
        {
            Header = "开机自动启动",
            IsCheckable = true,
            IsChecked = startWithWindows,
            Padding = new Thickness(10, 7, 10, 7),
            Cursor = WpfCursors.Hand,
            Foreground = new WpfSolidColorBrush(WpfColor.FromRgb(0xE6, 0xE8, 0xEC))
        };
        autoStart.Click += (_, _) => AutoStartToggled?.Invoke(autoStart.IsChecked);
        menu.Items.Add(autoStart);

        menu.Items.Add(CreateSeparator());
        menu.Items.Add(CreateMenuItem("退出", "Ctrl+Alt+Q", () => ExitRequested?.Invoke()));

        _icon.ContextMenu = menu;

        return _icon;
    }

    /// <summary>Builds a themed menu item with a right-aligned, dimmed shortcut hint.</summary>
    private static MenuItem CreateMenuItem(string header, string shortcut, Action onClick)
    {
        // A Grid header lets the shortcut hint sit right-aligned without a fixed width.
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new WpfGridLength(1, WpfGridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = WpfGridLength.Auto });

        var label = new TextBlock
        {
            Text = header,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(label, 0);

        var hint = new TextBlock
        {
            Text = shortcut,
            FontSize = 11.5,
            Margin = new Thickness(24, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new WpfSolidColorBrush(WpfColor.FromRgb(0x6B, 0x72, 0x80))
        };
        Grid.SetColumn(hint, 1);

        grid.Children.Add(label);
        grid.Children.Add(hint);

        var item = new MenuItem
        {
            Header = grid,
            Padding = new Thickness(10, 7, 10, 7),
            Cursor = WpfCursors.Hand,
            Foreground = new WpfSolidColorBrush(WpfColor.FromRgb(0xE6, 0xE8, 0xEC))
        };

        item.Click += (_, _) => onClick();
        return item;
    }

    /// <summary>A themed separator that is visible against the dark menu background.</summary>
    private static Separator CreateSeparator()
        => new()
        {
            Background = new WpfSolidColorBrush(WpfColor.FromRgb(0x34, 0x39, 0x46)),
            Margin = new Thickness(6, 4, 6, 4)
        };

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
            g.Clear(GdiColor.Transparent);

            using var pen = new GdiPen(GdiColor.FromArgb(255, 255, 59, 48), 3f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };

            // Cross with a central gap, matching the app's default crosshair.
            g.DrawLine(pen, 16, 3, 16, 12);
            g.DrawLine(pen, 16, 20, 16, 29);
            g.DrawLine(pen, 3, 16, 12, 16);
            g.DrawLine(pen, 20, 16, 29, 16);

            using var dotBrush = new SolidBrush(GdiColor.FromArgb(255, 255, 255, 255));
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
