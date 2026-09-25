using System.IO;
using System.Windows;
using System.Windows.Threading;
using CrosshairWin.Models;
using CrosshairWin.Monitors;
using CrosshairWin.Services;

namespace CrosshairWin;

/// <summary>
/// Application entry point and orchestrator.
///
/// Owns the lifetime of every service and is the single place where the pieces are
/// wired together: config -> overlay -> hotkeys -> tray -> settings.
/// </summary>
public partial class App : Application
{
    private readonly SingleInstanceService _singleInstance = new();
    private readonly ConfigService _configService = new();
    private readonly TrayService _trayService = new();
    private readonly HotkeyService _hotkeyService = new();

    private AppConfig _config = AppConfig.CreateDefault();

    private OverlayWindow? _overlay;
    private SettingsWindow? _settingsWindow;

    private bool _isExiting;

    // NOTE: no hand-written Main() here. WPF's XAML build step generates the entry
    // point, and declaring a second one causes CS0111 (duplicate Main). The
    // single-instance check therefore happens at the start of OnStartup instead.

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Headless self-test mode: runs the logic checks and exits with a status code.
        // Invoked as:  CrosshairWin.exe --selftest
        if (e.Args.Any(a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            // Attach a console so output is visible when launched from a terminal.
            NativeConsole.Attach();

            int code = Tests.SelfTest.Run();
            NativeConsole.WriteLine($"Self-test exit code: {code}");

            Shutdown(code);
            return;
        }

        // --- single instance -------------------------------------------------
        if (!_singleInstance.TryAcquire())
        {
            // Another copy is already running: ask it to show itself, then exit quietly.
            SingleInstanceService.SignalExistingInstance();
            Shutdown();
            return;
        }

        _singleInstance.ActivationRequested += OnExternalActivationRequested;

        // --- global exception safety net -------------------------------------
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            AppPaths.EnsureCreated();

            _config = _configService.Load();
            _configService.Warning += message => ShowNotification("配置", message);

            // Materialise the config on first run so the file exists (and is
            // user-editable) even before the user changes anything.
            if (!File.Exists(AppPaths.ConfigFile))
                _configService.Save(_config);

            // --- overlay ------------------------------------------------------
            var target = MonitorService.Resolve(_config.MonitorDeviceName);
            _overlay = new OverlayWindow(target);
            _overlay.ImageLoadFailed += message => ShowNotification("图片准星", message);

            _overlay.ApplyPreset(_config.GetActivePreset());
            _overlay.MoveToMonitor(target);

            if (_config.OverlayVisible)
                _overlay.ShowOverlay();

            // --- hotkeys ------------------------------------------------------
            _hotkeyService.HotkeyPressed += OnHotkeyPressed;

            if (!_hotkeyService.Initialize(_config.Hotkeys))
            {
                // RegisterHotKey failures are reported rather than silently ignored.
                foreach (string failure in _hotkeyService.Failures)
                    ShowNotification("快捷键", failure);
            }

            // --- tray ---------------------------------------------------------
            // Hardcodet creates the underlying shell icon lazily once Icon/Visibility
            // is set, so no explicit "force create" call is required (that API existed
            // in 1.x only and is gone in 2.0.1).
            _trayService.Create(SafeAutoStartEnabled());

            _trayService.ToggleVisibilityRequested += ToggleOverlayVisibility;
            _trayService.OpenSettingsRequested += OpenSettings;
            _trayService.CyclePresetRequested += CyclePreset;
            _trayService.ExitRequested += ExitApplication;
            _trayService.AutoStartToggled += OnTrayAutoStartToggled;

            // Repair a stale Run entry left behind if the exe was moved.
            if (_config.StartWithWindows)
                AutoStartService.RepairIfStale();
        }
        catch (Exception ex)
        {
            // Startup must fail loudly but gracefully rather than vanishing.
            MessageBox.Show(
                $"CrosshairWin 启动失败：\n\n{ex.Message}\n\n{ex.StackTrace}",
                "CrosshairWin",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            ExitApplication();
        }
    }

    // =====================================================================
    //  Actions
    // =====================================================================

    /// <summary>Shows or hides the overlay.</summary>
    private void ToggleOverlayVisibility()
    {
        if (_overlay is null)
            return;

        bool makeVisible = !_config.OverlayVisible;

        if (makeVisible)
        {
            // Re-resolve the monitor each time: the user may have unplugged a display.
            var target = MonitorService.Resolve(_config.MonitorDeviceName);
            _overlay.MoveToMonitor(target);
            _overlay.ShowOverlay();
        }
        else
        {
            _overlay.HideOverlay();
        }

        _config.OverlayVisible = makeVisible;
        PersistConfig();
    }

    /// <summary>Applies the current preset to the overlay and refreshes the preview.</summary>
    private void RefreshOverlayPreset()
    {
        if (_overlay is null)
            return;

        var preset = _config.GetActivePreset();

        // Clear the cached bitmap so a replaced image file is picked up immediately.
        _overlay.InvalidateImageCache();
        _overlay.ApplyPreset(preset);
    }

    /// <summary>Advances to the next preset, wrapping around (covers built-ins and images).</summary>
    private void CyclePreset()
    {
        if (_config.Presets.Count == 0)
            return;

        int index = _config.Presets.FindIndex(p => p.Id == _config.ActivePresetId);
        index = (index + 1) % _config.Presets.Count;

        _config.ActivePresetId = _config.Presets[index].Id;
        RefreshOverlayPreset();
        PersistConfig();

        ShowNotification("准星", $"已切换到：{_config.Presets[index].Name}");
    }

    /// <summary>Opens (or focuses) the settings window.</summary>
    private void OpenSettings()
    {
        try
        {
            if (_settingsWindow is { IsLoaded: true })
            {
                if (_settingsWindow.WindowState == WindowState.Minimized)
                    _settingsWindow.WindowState = WindowState.Normal;

                _settingsWindow.Activate();
                _settingsWindow.Focus();
                return;
            }

            _settingsWindow = new SettingsWindow(_configService, _config);

            _settingsWindow.ConfigurationSaved += OnSettingsSaved;
            _settingsWindow.NotificationRequested += message => ShowNotification("设置", message);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;

            _settingsWindow.Show();
            _settingsWindow.Activate();
        }
        catch (Exception ex)
        {
            ShowNotification("设置", $"无法打开设置窗口：{ex.Message}");
        }
    }

    /// <summary>Adopts a configuration saved from the settings window.</summary>
    private void OnSettingsSaved(AppConfig updated)
    {
        try
        {
            _config = updated;

            // Reposition in case the monitor choice changed.
            var target = MonitorService.Resolve(_config.MonitorDeviceName);

            if (_overlay is not null)
            {
                _overlay.MoveToMonitor(target);
                RefreshOverlayPreset();

                if (_config.OverlayVisible)
                    _overlay.ShowOverlay();
                else
                    _overlay.HideOverlay();
            }

            // Hotkeys may have changed; re-register and report any that failed.
            if (!_hotkeyService.Rebind(_config.Hotkeys))
            {
                foreach (string failure in _hotkeyService.Failures)
                    ShowNotification("快捷键", failure);
            }

            _trayService.UpdateAutoStartState(SafeAutoStartEnabled());

            ShowNotification("设置", "设置已保存。");
        }
        catch (Exception ex)
        {
            ShowNotification("设置", $"应用设置时出错：{ex.Message}");
        }
    }

    /// <summary>Handles a global hotkey press.</summary>
    private void OnHotkeyPressed(HotkeyAction action)
    {
        try
        {
            switch (action)
            {
                case HotkeyAction.ToggleVisibility:
                    ToggleOverlayVisibility();
                    break;

                case HotkeyAction.CyclePreset:
                    CyclePreset();
                    break;

                case HotkeyAction.OpenSettings:
                    OpenSettings();
                    break;

                case HotkeyAction.Exit:
                    ExitApplication();
                    break;
            }
        }
        catch (Exception ex)
        {
            ShowNotification("快捷键", $"执行快捷键操作失败：{ex.Message}");
        }
    }

    /// <summary>Handles the tray's autostart toggle.</summary>
    private void OnTrayAutoStartToggled(bool enabled)
    {
        _config.StartWithWindows = enabled;

        if (AutoStartService.SetEnabled(enabled, out string? error))
        {
            PersistConfig();
            ShowNotification("开机自启", enabled ? "已开启。" : "已关闭。");
        }
        else
        {
            ShowNotification("开机自启", error ?? "设置失败。");

            // Snap the menu back to reality.
            _config.StartWithWindows = SafeAutoStartEnabled();
            _trayService.UpdateAutoStartState(_config.StartWithWindows);
        }
    }

    /// <summary>A secondary launch asked us to surface the app.</summary>
    private void OnExternalActivationRequested()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                // Re-show the overlay and bring settings forward, which is the most
                // useful interpretation of "the user launched it again".
                if (_overlay is not null && !_config.OverlayVisible)
                    ToggleOverlayVisibility();

                OpenSettings();
            }
            catch
            {
                // Never let an activation signal crash the app.
            }
        }));
    }

    // =====================================================================
    //  Persistence & shutdown
    // =====================================================================

    private void PersistConfig()
    {
        try
        {
            _configService.Save(_config);
        }
        catch
        {
            // Saving is best-effort on the hot path; the settings window reports failures.
        }
    }

    /// <summary>Reads autostart state defensively (registry access can be blocked).</summary>
    private static bool SafeAutoStartEnabled()
    {
        try
        {
            return AutoStartService.IsEnabled();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Tears everything down in order and shuts the app down.</summary>
    private void ExitApplication()
    {
        if (_isExiting)
            return;

        _isExiting = true;

        try
        {
            _config.OverlayVisible = _overlay?.IsVisible ?? _config.OverlayVisible;
            _configService.Save(_config);
        }
        catch
        {
            // Best effort.
        }

        // Order matters: release OS resources the shell also owns.
        try
        {
            _hotkeyService.Dispose();
        }
        catch
        {
            // Best effort.
        }

        try
        {
            _trayService.Dispose();
        }
        catch
        {
            // Best effort.
        }

        try
        {
            _settingsWindow?.Close();
        }
        catch
        {
            // Best effort.
        }

        try
        {
            if (_overlay is not null)
            {
                // Let the overlay actually close during shutdown.
                _overlay.Closing -= null;
                _overlay.Hide();
            }
        }
        catch
        {
            // Best effort.
        }

        try
        {
            _singleInstance.Dispose();
        }
        catch
        {
            // Best effort.
        }

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _hotkeyService.Dispose();
            _trayService.Dispose();
            _singleInstance.Dispose();
        }
        catch
        {
            // Best effort.
        }

        base.OnExit(e);
    }

    // =====================================================================
    //  Notifications & exception handling
    // =====================================================================

    private void ShowNotification(string title, string message)
    {
        try
        {
            _trayService.ShowNotification(title, message, _config.ShowTrayNotifications);
        }
        catch
        {
            // Notifications are cosmetic.
        }
    }

    /// <summary>
    /// Last-resort handler for UI-thread exceptions. Keeps the app alive instead of
    /// crashing to the desktop, and tells the user what happened.
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;

        try
        {
            ShowNotification("发生错误", e.Exception.Message);

            if (_overlay is not null && !_overlay.IsVisible && _config.OverlayVisible)
                _overlay.ShowOverlay();
        }
        catch
        {
            // Nothing more we can do.
        }
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            if (e.ExceptionObject is Exception ex)
                ShowNotification("发生严重错误", ex.Message);
        }
        catch
        {
            // Nothing more we can do.
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        // Mark observed so a stray background task cannot take the process down.
        e.SetObserved();
    }
}
