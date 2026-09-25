using System.IO;

namespace CrosshairWin.Services;

/// <summary>
/// Central definition of every path the app reads or writes.
/// Everything lives under %AppData%\CrosshairWin\.
/// </summary>
internal static class AppPaths
{
    /// <summary>%AppData%\CrosshairWin</summary>
    internal static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CrosshairWin");

    /// <summary>%AppData%\CrosshairWin\Images — all imported image crosshairs are copied here.</summary>
    internal static string ImagesDir { get; } = Path.Combine(Root, "Images");

    /// <summary>%AppData%\CrosshairWin\config.json</summary>
    internal static string ConfigFile { get; } = Path.Combine(Root, "config.json");

    /// <summary>Creates the directory tree, ignoring failures so startup never crashes on IO.</summary>
    internal static void EnsureCreated()
    {
        try
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(ImagesDir);
        }
        catch
        {
            // A locked-down profile must not prevent the app from starting.
            // Consumers fall back to in-memory behaviour when the path is unusable.
        }
    }

    /// <summary>True when the app data root could actually be created/written.</summary>
    internal static bool IsUsable
    {
        get
        {
            try
            {
                Directory.CreateDirectory(Root);
                Directory.CreateDirectory(ImagesDir);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Resolves a stored relative image path to an absolute one.
    /// Returns null for empty input or paths that escape the app data root.
    /// </summary>
    internal static string? ResolveImage(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return null;

        try
        {
            // Absolute paths are tolerated on read (older configs) but never written back.
            if (Path.IsPathRooted(relativePath))
                return File.Exists(relativePath) ? relativePath : null;

            string full = Path.GetFullPath(Path.Combine(Root, relativePath));

            // Guard against "../" traversal escaping the app data folder.
            string rootFull = Path.GetFullPath(Root);
            if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                return null;

            return File.Exists(full) ? full : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Converts an absolute path under <see cref="Root"/> into the relative form
    /// that gets persisted. Returns null when the file lives outside the root.
    /// </summary>
    internal static string? ToRelative(string? absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
            return null;

        try
        {
            string rootFull = Path.GetFullPath(Root);
            string full = Path.GetFullPath(absolutePath);

            if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                return null;

            return Path.GetRelativePath(rootFull, full);
        }
        catch
        {
            return null;
        }
    }
}
