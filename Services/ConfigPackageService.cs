using System.IO;
using System.IO.Compression;
using System.Text;
using CrosshairWin.Models;

namespace CrosshairWin.Services;

/// <summary>
/// Exports/imports a complete configuration *including* the crosshair images.
///
/// Package layout (a .zip renamed to .chwconfig):
///   config.json          - the AppConfig JSON
///   manifest.txt         - human-readable description
///   Images/&lt;files&gt;       - every referenced image, at its stored relative path
/// </summary>
internal sealed class ConfigPackageService
{
    private const string ConfigEntryName = "config.json";
    private const string ManifestEntryName = "manifest.txt";
    private const string ImagesFolder = "Images";

    /// <summary>File dialog filter shared by export and import.</summary>
    public const string PackageFilter =
        "CrosshairWin 配置包 (*.chwconfig)|*.chwconfig|Zip 压缩包 (*.zip)|*.zip|所有文件|*.*";

    private readonly ConfigService _configService;

    public ConfigPackageService(ConfigService configService)
    {
        _configService = configService;
    }

    /// <summary>Result of an export or import operation.</summary>
    public sealed class PackageResult
    {
        public bool Success { get; init; }
        public string? Error { get; init; }
        public int ImageCount { get; init; }
        public AppConfig? Config { get; init; }

        public static PackageResult Ok(int images = 0, AppConfig? config = null) =>
            new() { Success = true, ImageCount = images, Config = config };

        public static PackageResult Fail(string error) => new() { Success = false, Error = error };
    }

    /// <summary>
    /// Writes a package containing the config plus every referenced image.
    /// Missing images are skipped rather than aborting the export.
    /// </summary>
    public PackageResult Export(AppConfig config, string destinationPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(destinationPath))
                return PackageResult.Fail("未指定导出路径。");

            // Make sure the images referenced by presets resolve before we zip them.
            var images = CollectImages(config);

            // Write to a temp file first so a failure cannot leave a half-written package.
            string temp = destinationPath + ".tmp";

            if (File.Exists(temp))
                File.Delete(temp);

            int exported = 0;

            // The archive must be fully flushed AND closed before the temp file is
            // moved: on Windows the move fails while the FileStream still holds it.
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                // --- config.json ---
                var configEntry = archive.CreateEntry(ConfigEntryName, CompressionLevel.Optimal);
                using (var writer = new StreamWriter(configEntry.Open(), new UTF8Encoding(false)))
                {
                    writer.Write(_configService.Serialize(config));
                }

                // --- manifest.txt ---
                var manifestEntry = archive.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
                using (var writer = new StreamWriter(manifestEntry.Open(), new UTF8Encoding(false)))
                {
                    writer.WriteLine("CrosshairWin configuration package");
                    writer.WriteLine($"Exported : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    writer.WriteLine($"Presets  : {config.Presets.Count}");
                    writer.WriteLine($"Images   : {images.Count}");
                    writer.WriteLine();
                    writer.WriteLine("Preset list:");
                    foreach (var preset in config.Presets)
                    {
                        writer.WriteLine(
                            $"  - {preset.Name} ({preset.Shape})" +
                            (string.IsNullOrWhiteSpace(preset.ImageRelativePath)
                                ? string.Empty
                                : $" image={preset.ImageRelativePath}"));
                    }
                }

                // --- Images/ ---
                foreach (var relative in images)
                {
                    string? absolute = AppPaths.ResolveImage(relative);
                    if (absolute is null || !File.Exists(absolute))
                        continue;

                    // Zip entry paths always use forward slashes.
                    string entryName = $"{ImagesFolder}/{Path.GetFileName(absolute)}";
                    var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);

                    using (var entryStream = entry.Open())
                    using (var fileStream = File.OpenRead(absolute))
                    {
                        fileStream.CopyTo(entryStream);
                    }

                    exported++;
                }
            }

            // Archive is closed here, so the temp file is no longer locked.
            if (File.Exists(destinationPath))
                File.Delete(destinationPath);

            File.Move(temp, destinationPath);

            return PackageResult.Ok(exported, config);
        }
        catch (Exception ex)
        {
            return PackageResult.Fail($"导出配置失败：{ex.Message}");
        }
    }

    /// <summary>
    /// Restores a package: images are extracted back into the app data folder and
    /// the returned config is ready to be adopted.
    /// </summary>
    public PackageResult Import(string packagePath)
    {
        try
        {
            if (!File.Exists(packagePath))
                return PackageResult.Fail($"文件不存在：{packagePath}");

            AppPaths.EnsureCreated();

            using var stream = new FileStream(packagePath, FileMode.Open, FileAccess.Read);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            // --- config.json ---
            var configEntry = archive.GetEntry(ConfigEntryName);
            if (configEntry is null)
                return PackageResult.Fail("配置包无效：缺少 config.json。");

            string json;
            using (var reader = new StreamReader(configEntry.Open(), Encoding.UTF8))
            {
                json = reader.ReadToEnd();
            }

            var config = _configService.Deserialize(json);
            if (config is null)
                return PackageResult.Fail("配置包内的 config.json 无法解析。");

            // --- Images/ ---
            int restored = 0;
            string imagesPrefix = ImagesFolder + "/";

            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith('/') || entry.Length == 0)
                    continue; // directory marker

                if (!entry.FullName.StartsWith(imagesPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                string fileName = Path.GetFileName(entry.FullName);
                if (string.IsNullOrWhiteSpace(fileName))
                    continue;

                string target = Path.Combine(AppPaths.ImagesDir, fileName);

                // Reject anything that would escape the images folder.
                string imagesRoot = Path.GetFullPath(AppPaths.ImagesDir);
                if (!Path.GetFullPath(target).StartsWith(imagesRoot, StringComparison.OrdinalIgnoreCase))
                    continue;

                entry.ExtractToFile(target, overwrite: true);
                restored++;
            }

            // The cache may hold bitmaps for paths we just overwrote.
            ImageLoader.ClearCache();

            return PackageResult.Ok(restored, config);
        }
        catch (InvalidDataException)
        {
            return PackageResult.Fail("配置包已损坏或不是有效的压缩文件。");
        }
        catch (Exception ex)
        {
            return PackageResult.Fail($"导入配置失败：{ex.Message}");
        }
    }

    /// <summary>Gathers the distinct relative image paths referenced by the config.</summary>
    private static List<string> CollectImages(AppConfig config)
    {
        var result = new List<string>();

        foreach (var preset in config.Presets)
        {
            string? relative = preset.ImageRelativePath;

            if (string.IsNullOrWhiteSpace(relative))
                continue;

            // Belt and braces: if a stale absolute path slipped in, convert it.
            if (Path.IsPathRooted(relative))
                relative = AppPaths.ToRelative(relative);

            if (!string.IsNullOrWhiteSpace(relative) &&
                !result.Contains(relative, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(relative);
            }
        }

        return result;
    }

    /// <summary>
    /// Creates a timestamped default package file name.
    /// </summary>
    public static string SuggestExportFileName()
        => $"CrosshairWin-config-{DateTime.Now:yyyyMMdd-HHmmss}.chwconfig";
}
