using System.Runtime.InteropServices;

namespace CrosshairWin;

/// <summary>
/// Lets the GUI-subsystem executable print to a parent console.
///
/// A WinExe project has no console of its own, so <c>--selftest</c> output would
/// otherwise be invisible. AttachConsole borrows the console of the launching shell;
/// when there is none (double-clicked), output silently goes nowhere and the log
/// file written by the self-test remains the record.
/// </summary>
internal static class NativeConsole
{
    private const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();

    private static bool _attached;

    /// <summary>Attaches to the parent console, falling back to a new one.</summary>
    internal static void Attach()
    {
        if (_attached)
            return;

        try
        {
            _attached = AttachConsole(ATTACH_PARENT_PROCESS) || AllocConsole();
        }
        catch
        {
            _attached = false;
        }
    }

    /// <summary>Writes a line to the console when one is attached.</summary>
    internal static void WriteLine(string text)
    {
        try
        {
            if (_attached)
                Console.WriteLine(text);
        }
        catch
        {
            // Console output is a convenience only.
        }
    }
}
