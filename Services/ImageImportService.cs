using System.IO;
using CrosshairWin.Models;

namespace CrosshairWin.Services;

/// <summary>
/// Copies user-chosen images into %AppData%\CrosshairWin\Images\ and keeps the
/// persisted config pointing at them by relative path only.
/// </summary>
internal static class ImageImportService
{
    /// <summary>Outcome of an import attempt.</summary>
    internal sealed class ImportResult
    {
        public bool Success { get; init; }
        public string? AbsolutePath { get; init; }
        public string? RelativePath { get; init; }
        public string? Error { get; init; }
        public string? DisplayName { get; init; }

        public static ImportResult Ok(string abs, string rel, string name) =>
            new() { Success = true, AbsolutePath = abs, RelativePath = rel, DisplayName = name };

        public static ImportResult Fail(string error) => new() { Success = false, Error = error };
    }

    /// <summary>
    /// Validates an image and copies it into the app's image folder under a unique name.
    ///
    /// The copy step is what makes the configuration resilient: the preset stores only
    /// "Images\&lt;guid&gt;.png", so moving or deleting the original file afterwards has no effect.
    /// </summary>
    internal static ImportResult Import(string sourcePath, string? preferredName = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
                return ImportResult.Fail("未提供图片路径。");

            if (!File.Exists(sourcePath))
                return ImportResult.Fail($"文件不存在：{sourcePath}");

            string extension = Path.GetExtension(sourcePath).ToLowerInvariant();
            if (!ImageLoader.IsSupported(sourcePath))
            {
                return ImportResult.Fail(
                    $"不支持的图片格式“{extension}”。支持：PNG、JPG、JPEG、BMP、GIF、TIFF、ICO。");
            }

            if (!AppPaths.IsUsable)
                return ImportResult.Fail("无法写入 %AppData%\\CrosshairWin 目录，请检查权限。");

            // Verify the file is actually decodable before copying it, so a corrupt
            // file is rejected up front instead of silently breaking the preset later.
            var probe = ImageLoader.Load(sourcePath);
            if (!probe.Success)
                return ImportResult.Fail(probe.Error ?? "图片无法解码。");

            string baseName = string.IsNullOrWhiteSpace(preferredName)
                ? Path.GetFileNameWithoutExtension(sourcePath)
                : SanitizeName(preferredName);

            string unique = $"{baseName}_{Guid.NewGuid():N}{extension}";
            string destination = Path.Combine(AppPaths.ImagesDir, unique);

            File.Copy(sourcePath, destination, overwrite: false);

            string? relative = AppPaths.ToRelative(destination);
            if (relative is null)
                return ImportResult.Fail("无法计算图片的相对路径。");

            return ImportResult.Ok(destination, relative, unique);
        }
        catch (IOException ex)
        {
            return ImportResult.Fail($"复制图片失败：{ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return ImportResult.Fail($"没有权限写入图片目录：{ex.Message}");
        }
        catch (Exception ex)
        {
            return ImportResult.Fail($"导入图片失败：{ex.Message}");
        }
    }

    /// <summary>
    /// Deletes a managed image. Only files inside the app's own Images folder are removed.
    /// </summary>
    internal static bool DeleteManagedImage(string? relativePath)
    {
        try
        {
            string? absolute = AppPaths.ResolveImage(relativePath);
            if (absolute is null)
                return false;

            // Refuse to delete anything outside our managed folder.
            string imagesRoot = Path.GetFullPath(AppPaths.ImagesDir);
            string target = Path.GetFullPath(absolute);
            if (!target.StartsWith(imagesRoot, StringComparison.OrdinalIgnoreCase))
                return false;

            File.Delete(target);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Re-writes an existing managed image (used when restoring an exported package,
    /// where the archive already contains relative paths).
    /// </summary>
    internal static ImportResult ImportInto(string sourcePath, string targetRelativePath)
    {
        try
        {
            if (!File.Exists(sourcePath))
                return ImportResult.Fail($"文件不存在：{sourcePath}");

            string destination = Path.GetFullPath(Path.Combine(AppPaths.Root, targetRelativePath));
            string rootFull = Path.GetFullPath(AppPaths.Root);

            if (!destination.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                return ImportResult.Fail("压缩包内的图片路径非法。");

            string? dir = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.Copy(sourcePath, destination, overwrite: true);

            string? relative = AppPaths.ToRelative(destination);
            if (relative is null)
                return ImportResult.Fail("无法计算图片的相对路径。");

            return ImportResult.Ok(destination, relative, Path.GetFileName(destination));
        }
        catch (Exception ex)
        {
            return ImportResult.Fail($"恢复图片失败：{ex.Message}");
        }
    }

    /// <summary>Strips characters that are invalid in Windows file names.</summary>
    private static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();

        if (string.IsNullOrWhiteSpace(cleaned))
            cleaned = "image";

        // Keep file names comfortably short.
        return cleaned.Length > 40 ? cleaned[..40] : cleaned;
    }

    /// <summary>Creates a preset pre-filled with sensible defaults for an imported image.</summary>
    internal static CrosshairPreset CreateImagePreset(string displayName, string relativePath)
    {
        return new CrosshairPreset
        {
            Name = displayName,
            Shape = CrosshairShape.Image,
            ImageRelativePath = relativePath,
            ImageScaleMode = ImageScaleMode.Pixels,
            ImageWidthPx = 64,
            ImageHeightPercent = 10,
            ImageKeepAspectRatio = true,
            ImageLockRatio = true,
            ImageRotation = 0,
            ImageOpacity = 1.0,
            ImageOffsetX = 0,
            ImageOffsetY = 0,
            ImageUseOriginalColors = true,
            ImageTintColor = "#FFFF3B30",
            ImageCenterAnchor = true
        };
    }
}
