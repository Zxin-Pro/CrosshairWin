using System.Windows;
using System.Windows.Interop;
using CrosshairWin.Interop;
using CrosshairWin.Models;

namespace CrosshairWin.Services;

/// <summary>
/// Registers the global hotkeys with RegisterHotKey against a dedicated
/// message-only window, and raises events when they fire.
///
/// A message-only window is used instead of the overlay HWND so hotkeys keep
/// working while the overlay is hidden or being recreated.
/// </summary>
internal sealed class HotkeyService : IDisposable
{
    /// <summary>Base id for RegisterHotKey; ids are base + index.</summary>
    private const int HotkeyIdBase = 0x4348; // "CH"

    private readonly Dictionary<int, HotkeyAction> _registered = new();
    private readonly List<string> _failures = new();

    private HwndSource? _source;
    private bool _disposed;

    /// <summary>Raised on the UI thread when a registered hotkey is pressed.</summary>
    public event Action<HotkeyAction>? HotkeyPressed;

    /// <summary>Human-readable descriptions of hotkeys that could not be registered.</summary>
    public IReadOnlyList<string> Failures => _failures;

    /// <summary>Creates the message window and registers every binding.</summary>
    public bool Initialize(IEnumerable<HotkeyBinding> bindings)
    {
        _failures.Clear();
        UnregisterAll();

        try
        {
            // HwndSource with a zero-size message-only parent (HWND_MESSAGE is -3).
            var parameters = new HwndSourceParameters("CrosshairWinHotkeySink")
            {
                Width = 0,
                Height = 0,
                PositionX = 0,
                PositionY = 0,
                WindowStyle = 0,
                ExtendedWindowStyle = 0,
                ParentWindow = new IntPtr(-3)
            };

            _source = new HwndSource(parameters);
            _source.AddHook(WndProc);
        }
        catch (Exception ex)
        {
            _failures.Add($"无法创建快捷键消息窗口：{ex.Message}");
            return false;
        }

        int index = 0;
        foreach (var binding in bindings)
        {
            int id = HotkeyIdBase + index++;
            Register(binding, id);
        }

        return _failures.Count == 0;
    }

    /// <summary>Registers one binding, recording a friendly message on failure.</summary>
    private void Register(HotkeyBinding binding, int id)
    {
        if (_source is null || binding.VirtualKey == 0)
            return;

        // MOD_NOREPEAT stops auto-repeat from firing the action dozens of times per second
        // while a key is held. Supported since Windows 7, so safe on our floor.
        uint modifiers = binding.Modifiers | NativeMethods.MOD_NOREPEAT;

        bool ok;
        try
        {
            ok = NativeMethods.RegisterHotKey(_source.Handle, id, modifiers, binding.VirtualKey);
        }
        catch (Exception ex)
        {
            _failures.Add($"{binding.Display} 注册异常：{ex.Message}");
            return;
        }

        if (ok)
        {
            _registered[id] = binding.Action;
        }
        else
        {
            int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();

            // 1409 = ERROR_HOTKEY_ALREADY_REGISTERED. The far more common real-world
            // cause is another running app owning the same combination.
            string reason = error == 1409
                ? "已被其他程序占用"
                : $"注册失败（Win32 错误 {error}）";

            _failures.Add($"{DescribeAction(binding.Action)} 的快捷键 {binding.Display} {reason}。");
        }
    }

    /// <summary>Replaces all hotkeys with a new set (used after settings are saved).</summary>
    public bool Rebind(IEnumerable<HotkeyBinding> bindings)
    {
        UnregisterAll();
        return Initialize(bindings);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (_registered.TryGetValue(id, out var action))
            {
                HotkeyPressed?.Invoke(action);
                handled = true;
            }
        }

        return IntPtr.Zero;
    }

    private void UnregisterAll()
    {
        if (_source is not null)
        {
            foreach (int id in _registered.Keys.ToList())
            {
                try
                {
                    NativeMethods.UnregisterHotKey(_source.Handle, id);
                }
                catch
                {
                    // Ignore: we are tearing down anyway.
                }
            }
        }

        _registered.Clear();
    }

    /// <summary>Localised label for a hotkey action, used in error messages.</summary>
    internal static string DescribeAction(HotkeyAction action) => action switch
    {
        HotkeyAction.ToggleVisibility => "显示/隐藏",
        HotkeyAction.CyclePreset => "切换准星",
        HotkeyAction.OpenSettings => "打开设置",
        HotkeyAction.Exit => "退出",
        _ => action.ToString()
    };

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        UnregisterAll();

        if (_source is not null)
        {
            try
            {
                _source.RemoveHook(WndProc);
                _source.Dispose();
            }
            catch
            {
                // Best effort.
            }

            _source = null;
        }
    }
}
