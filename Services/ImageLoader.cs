using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media.Imaging;

namespace CrosshairWin.Services;

/// <summary>
/// Loads and caches crosshair images.
///
/// Key behaviours:
///  * BitmapCacheOption.OnLoad  -> the file is fully read into memory, so we never
///    hold a lock on the user's file and the bitmap is safe to use off the UI thread.
///  * Freeze()                  -> the bitmap becomes immutable, which removes the
///    per-frame WPF marshalling cost and prevents cross-thread access exceptions.
///  * GIF                       -> only the first frame is decoded (see DecodeFrameCount).
/// </summary>
internal static class ImageLoader
{
    /// <summary>File extensions offered in the open dialog and accepted from drag/drop.</summary>
    internal static readonly string[] SupportedExtensions =
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".ico"
    };

    /// <summary>Filter string for <see cref="Microsoft.Win32.OpenFileDialog"/>.</summary>
    internal const string FileDialogFilter =
        "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.ico|" +
        "PNG 透明图片|*.png|" +
        "所有文件|*.*";

    /// <summary>
    /// Cache keyed by "absolutePath|lastWriteTicks". Re-importing a file with the
    /// same name but new content therefore invalidates the entry automatically.
    /// </summary>
    private static readonly ConcurrentDictionary<string, BitmapSource> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Result of a load attempt.</summary>
    internal sealed class LoadResult
    {
        public BitmapSource? Image { get; init; }
        public string? Error { get; init; }
        public bool Success => Image is not null;

        public static LoadResult Ok(BitmapSource image) => new() { Image = image };
        public static LoadResult Fail(string error) => new() { Error = error };
    }

    /// <summary>
    /// Loads an image from an absolute path with caching.
    /// Returns a frozen <see cref="BitmapSource"/> on success, or an error description.
    /// </summary>
    internal static LoadResult Load(string? absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
            return LoadResult.Fail("未指定图片路径。");

        if (!File.Exists(absolutePath))
            return LoadResult.Fail($"图片文件不存在：{absolutePath}");

        string extension = Path.GetExtension(absolutePath).ToLowerInvariant();
        if (!SupportedExtensions.Contains(extension))
            return LoadResult.Fail($"不支持的图片格式：{extension}");

        string cacheKey;
        try
        {
            cacheKey = absolutePath + "|" + File.GetLastWriteTimeUtc(absolutePath).Ticks;
        }
        catch (Exception ex)
        {
            return LoadResult.Fail($"无法读取图片信息：{ex.Message}");
        }

        if (Cache.TryGetValue(cacheKey, out var cached))
            return LoadResult.Ok(cached);

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();

            // OnLoad keeps the stream contents in memory and releases the file handle,
            // so the user can move/delete the source file while the app is running.
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            bitmap.UriSource = new Uri(absolutePath, UriKind.Absolute);

            // Animated GIFs are intentionally pinned to their first frame.
            bitmap.EndInit();

            bitmap.Freeze();

            if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
                return LoadResult.Fail("图片尺寸无效或已损坏。");

            Cache[cacheKey] = bitmap;
            PruneCache();
            return LoadResult.Ok(bitmap);
        }
        catch (NotSupportedException ex)
        {
            return LoadResult.Fail($"系统解码器不支持该图片：{ex.Message}");
        }
        catch (FileFormatException ex)
        {
            return LoadResult.Fail($"图片已损坏或格式不正确：{ex.Message}");
        }
        catch (Exception ex)
        {
            return LoadResult.Fail($"加载图片失败：{ex.Message}");
        }
    }

    /// <summary>
    /// Loads the first frame only. For GIF this is the static preview behaviour the
    /// requirement asks for; WPF's BitmapImage decodes frame 0 by default with
    /// BitmapCreateOptions.PreservePixelFormat and no animation controller attached.
    /// </summary>
    internal static LoadResult LoadFirstFrame(string? absolutePath) => Load(absolutePath);

    /// <summary>Removes stale cache entries, keeping memory bounded.</summary>
    private static void PruneCache()
    {
        const int maxEntries = 32;
        if (Cache.Count <= maxEntries)
            return;

        foreach (var key in Cache.Keys.Take(Cache.Count - maxEntries).ToList())
            Cache.TryRemove(key, out _);
    }

    /// <summary>Drops every cached bitmap (used when the image folder changes).</summary>
    internal static void ClearCache() => Cache.Clear();

    /// <summary>True when the extension is one we advertise support for.</summary>
    internal static bool IsSupported(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return SupportedExtensions.Contains(ext);
    }
}
