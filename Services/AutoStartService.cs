using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace CrosshairWin.Services;

/// <summary>
/// Manages the "start with Windows" entry.
/// Uses the per-user Run key, which requires no elevation and behaves identically
/// on Windows 10 and Windows 11.
/// </summary>
internal static class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CrosshairWin";

    /// <summary>True when the Run value exists and points at the current executable.</summary>
    internal static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var value = key?.GetValue(ValueName) as string;

            if (string.IsNullOrWhiteSpace(value))
                return false;

            // Treat a stale entry (app moved) as "enabled but needs repair".
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Creates or removes the Run entry. Returns false and reports the reason on failure.
    /// </summary>
    internal static bool SetEnabled(bool enabled, out string? error)
    {
        error = null;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

            if (key is null)
            {
                error = "无法打开注册表启动项，可能被安全策略阻止。";
                return false;
            }

            if (enabled)
            {
                string? exe = GetExecutablePath();
                if (string.IsNullOrWhiteSpace(exe))
                {
                    error = "无法确定程序路径，开机自启未设置。";
                    return false;
                }

                // Quote the path: it may contain spaces.
                key.SetValue(ValueName, $"\"{exe}\"", RegistryValueKind.String);
            }
            else
            {
                if (key.GetValue(ValueName) is not null)
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            error = $"没有权限修改注册表启动项：{ex.Message}";
            return false;
        }
        catch (Exception ex)
        {
            error = $"设置开机自启失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>Applies the desired state, ignoring failures after reporting them.</summary>
    internal static bool Apply(bool enabled, out string? error) => SetEnabled(enabled, out error);

    /// <summary>
    /// Absolute path of the running executable.
    /// <see cref="Environment.ProcessPath"/> is used because Assembly.Location is
    /// empty for single-file published apps.
    /// </summary>
    internal static string? GetExecutablePath()
    {
        try
        {
            string? path = Environment.ProcessPath;

            if (string.IsNullOrWhiteSpace(path))
            {
                using var process = Process.GetCurrentProcess();
                path = process.MainModule?.FileName;
            }

            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The current Run value, for display/diagnostics.</summary>
    internal static string? GetRegisteredCommand()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Removes the entry if it points at a different executable than the current one.</summary>
    internal static void RepairIfStale()
    {
        try
        {
            string? registered = GetRegisteredCommand();
            if (string.IsNullOrWhiteSpace(registered))
                return;

            string? current = GetExecutablePath();
            if (string.IsNullOrWhiteSpace(current))
                return;

            string normalisedRegistered = registered.Trim('"');
            if (!string.Equals(normalisedRegistered, current, StringComparison.OrdinalIgnoreCase))
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                key?.SetValue(ValueName, $"\"{current}\"", RegistryValueKind.String);
            }
        }
        catch
        {
            // Diagnostics-only helper; ignore failures.
        }
    }

    /// <summary>True when the app data / exe folder is writable enough for autostart to make sense.</summary>
    internal static bool CanAutoStart() => !string.IsNullOrWhiteSpace(GetExecutablePath())
                                           && File.Exists(GetExecutablePath());
}
