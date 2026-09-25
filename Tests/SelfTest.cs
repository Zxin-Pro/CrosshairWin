using System.IO;
using System.Text;
using CrosshairWin.Models;
using CrosshairWin.Services;

namespace CrosshairWin.Tests;

/// <summary>
/// A dependency-free smoke test harness.
///
/// Run with:  CrosshairWin.exe --selftest
///
/// It exercises the non-UI logic that is otherwise awkward to verify by hand:
/// config round-trip, image import/caching, corrupt-image fallback, and the
/// export/import package round-trip. Results are written to stdout and to
/// %AppData%\CrosshairWin\selftest.log so they can be inspected after the fact.
/// </summary>
internal static class SelfTest
{
    private static readonly List<string> Lines = new();
    private static int _passed;
    private static int _failed;

    /// <summary>Runs every check and returns the process exit code (0 = all passed).</summary>
    internal static int Run()
    {
        Log("=== CrosshairWin self-test ===");
        Log($"Time     : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Log($"AppData  : {AppPaths.Root}");
        Log($"Runtime  : {Environment.Version}");
        Log($"OS       : {Environment.OSVersion}");
        Log($"64-bit   : {Environment.Is64BitProcess}");
        Log("");

        try
        {
            TestConfigRoundTrip();
            TestImageImport();
            TestImageCacheAndFreeze();
            TestCorruptImageFallback();
            TestMissingImageResolution();
            TestRelativePathEscapeGuard();
            TestPackageRoundTrip();
            TestPresetCycleOrder();
            TestColorParsing();
            TestCrosshairShapes();
        }
        catch (Exception ex)
        {
            Log($"UNCAUGHT EXCEPTION: {ex}");
            _failed++;
        }

        Log("");
        Log($"=== RESULT: {_passed} passed, {_failed} failed ===");

        WriteLogFile();

        return _failed == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ tests

    private static void TestConfigRoundTrip()
    {
        var service = new ConfigService();
        var config = AppConfig.CreateDefault();

        Log($"Config default presets : {config.Presets.Count}");
        Check("default config has 1 preset", config.Presets.Count == 1);
        Check("default config has 4 hotkeys", config.Hotkeys.Count == 4);

        string json = service.Serialize(config);
        var restored = service.Deserialize(json);

        Check("config deserializes", restored is not null);
        Check("preset count preserved", restored?.Presets.Count == 1);
        Check("active preset id preserved", restored?.ActivePresetId == config.ActivePresetId);
        Check("hotkeys preserved", restored?.Hotkeys.Count == 4);
        Check("F8 default preserved",
            restored?.Hotkeys.Any(h => h.Action == HotkeyAction.ToggleVisibility && h.VirtualKey == 0x77) == true);
        Check("Ctrl+Alt+Q default preserved",
            restored?.Hotkeys.Any(h => h.Action == HotkeyAction.Exit
                                       && h.VirtualKey == 0x51
                                       && h.Modifiers == 0x0003) == true);

        // Enum-as-string serialization keeps the JSON human-editable.
        Check("shape serialized as string", json.Contains("\"Cross\"", StringComparison.Ordinal));

        // Corrupt input must not throw.
        Check("corrupt json returns null, no throw", service.Deserialize("{ this is not json") is null);
    }

    private static void TestImageImport()
    {
        string source = CreateTempPng("import_source.png", 32, 16);

        var result = ImageImportService.Import(source, "test");

        Check("import succeeds", result.Success);
        Check("import returns absolute path", !string.IsNullOrWhiteSpace(result.AbsolutePath));
        Check("import returns RELATIVE path", !string.IsNullOrWhiteSpace(result.RelativePath));
        Check("relative path is under Images", result.RelativePath?.StartsWith("Images", StringComparison.OrdinalIgnoreCase) == true);
        Check("relative path is not rooted", result.RelativePath is not null && !Path.IsPathRooted(result.RelativePath));
        Check("file copied into Images dir", result.AbsolutePath is not null && File.Exists(result.AbsolutePath));
        Check("copied file lives under AppData Images",
            result.AbsolutePath?.StartsWith(AppPaths.ImagesDir, StringComparison.OrdinalIgnoreCase) == true);

        // Unique naming: importing the same source twice must not collide.
        var second = ImageImportService.Import(source, "test");
        Check("second import creates unique file", second.Success && second.AbsolutePath != result.AbsolutePath);

        // Unsupported extension is rejected.
        string bad = Path.Combine(Path.GetTempPath(), "notimage.txt");
        File.WriteAllText(bad, "hello");
        var rejected = ImageImportService.Import(bad, "bad");
        Check("unsupported extension rejected", !rejected.Success);

        // Missing file is rejected.
        var missing = ImageImportService.Import(Path.Combine(Path.GetTempPath(), "nope_xyz.png"));
        Check("missing file rejected", !missing.Success);

        // Deleting a managed image works, and refuses outside paths.
        Check("managed image deletes", ImageImportService.DeleteManagedImage(second.RelativePath));
        Check("delete refuses outside path", !ImageImportService.DeleteManagedImage(@"..\..\windows\system32\drivers\etc\hosts"));

        File.Delete(source);
        File.Delete(bad);
    }

    private static void TestImageCacheAndFreeze()
    {
        string source = CreateTempPng("cache_source.png", 64, 64);
        var import = ImageImportService.Import(source, "cache");

        if (import.AbsolutePath is null)
        {
            Check("cache test import", false);
            return;
        }

        var first = ImageLoader.Load(import.AbsolutePath);
        Check("image loads", first.Success);
        Check("image is frozen (thread-safe/immutable)", first.Image?.IsFrozen == true);

        // The file handle must be released so the user can delete the original.
        bool locked = false;
        try
        {
            using var fs = File.Open(import.AbsolutePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            locked = true;
        }

        Check("file is NOT locked after load (OnLoad)", !locked);

        // Second load must hit the cache and return the same instance.
        var second = ImageLoader.Load(import.AbsolutePath);
        Check("cached load returns same instance", ReferenceEquals(first.Image, second.Image));

        Check("supported extensions include png/jpg/jpeg/bmp/gif/tiff/ico",
            ImageLoader.IsSupported("a.png") && ImageLoader.IsSupported("a.jpg") &&
            ImageLoader.IsSupported("a.jpeg") && ImageLoader.IsSupported("a.bmp") &&
            ImageLoader.IsSupported("a.gif") && ImageLoader.IsSupported("a.tiff") &&
            ImageLoader.IsSupported("a.tif") && ImageLoader.IsSupported("a.ico"));

        ImageImportService.DeleteManagedImage(import.RelativePath);
        File.Delete(source);
    }

    private static void TestCorruptImageFallback()
    {
        // A .png extension with garbage content must fail cleanly, not throw.
        string corrupt = Path.Combine(AppPaths.ImagesDir, "corrupt_test.png");
        AppPaths.EnsureCreated();
        File.WriteAllBytes(corrupt, new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 });

        var result = ImageLoader.Load(corrupt);

        Check("corrupt image does not throw", true);
        Check("corrupt image reports failure", !result.Success);
        Check("corrupt image provides an error message", !string.IsNullOrWhiteSpace(result.Error));

        // Import must reject it too (probe step).
        var import = ImageImportService.Import(corrupt, "corrupt");
        Check("corrupt image rejected on import", !import.Success);

        try { File.Delete(corrupt); } catch { /* best effort */ }
    }

    private static void TestMissingImageResolution()
    {
        // A preset pointing at a deleted file must resolve to null => canvas draws fallback.
        string? resolved = AppPaths.ResolveImage(@"Images\definitely_missing_file_12345.png");
        Check("missing image resolves to null (fallback path)", resolved is null);

        Check("null relative path resolves to null", AppPaths.ResolveImage(null) is null);
        Check("empty relative path resolves to null", AppPaths.ResolveImage("   ") is null);
    }

    private static void TestRelativePathEscapeGuard()
    {
        // Directory traversal must be refused.
        string? escaped = AppPaths.ResolveImage(@"..\..\..\..\Windows\System32\drivers\etc\hosts");
        Check("path traversal refused", escaped is null);

        // Round-trip of a legitimately managed path.
        string source = CreateTempPng("relpath.png", 8, 8);
        var import = ImageImportService.Import(source, "rel");

        if (import.AbsolutePath is not null)
        {
            string? back = AppPaths.ToRelative(import.AbsolutePath);
            Check("ToRelative round-trips", back == import.RelativePath);
            Check("ToRelative of outside path is null",
                AppPaths.ToRelative(@"C:\Windows\notepad.exe") is null);
            ImageImportService.DeleteManagedImage(import.RelativePath);
        }

        File.Delete(source);
    }

    private static void TestPackageRoundTrip()
    {
        var service = new ConfigService();
        var packageService = new ConfigPackageService(service);

        // Build a config with a real image preset.
        string source = CreateTempPng("package_image.png", 48, 48);
        var import = ImageImportService.Import(source, "pkg");

        if (!import.Success || import.RelativePath is null)
        {
            Check("package test image import", false);
            return;
        }

        var config = AppConfig.CreateDefault();
        var imagePreset = ImageImportService.CreateImagePreset("图片预设", import.RelativePath);
        config.Presets.Add(imagePreset);
        config.ActivePresetId = imagePreset.Id;

        string packagePath = Path.Combine(Path.GetTempPath(), "chw_roundtrip.chwconfig");

        var export = packageService.Export(config, packagePath);
        Check("export succeeds", export.Success);
        Check("export packs 1 image", export.ImageCount == 1);
        Check("package file exists", File.Exists(packagePath));

        // Delete the managed image, then confirm import restores it.
        ImageImportService.DeleteManagedImage(import.RelativePath);
        ImageLoader.ClearCache();
        Check("image removed before import", AppPaths.ResolveImage(import.RelativePath) is null);

        var importResult = packageService.Import(packagePath);
        Check("import succeeds", importResult.Success);
        Check("import restores 1 image", importResult.ImageCount == 1);
        Check("imported config has 2 presets", importResult.Config?.Presets.Count == 2);
        Check("restored image resolves on disk", AppPaths.ResolveImage(import.RelativePath) is not null);

        var restoredImage = ImageLoader.Load(AppPaths.ResolveImage(import.RelativePath));
        Check("restored image decodes", restoredImage.Success);

        // A bogus package must fail cleanly.
        string bogus = Path.Combine(Path.GetTempPath(), "chw_bogus.chwconfig");
        File.WriteAllText(bogus, "not a zip at all");
        var badImport = packageService.Import(bogus);
        Check("bogus package rejected without throwing", !badImport.Success);

        try
        {
            File.Delete(packagePath);
            File.Delete(bogus);
            File.Delete(source);
            ImageImportService.DeleteManagedImage(import.RelativePath);
        }
        catch
        {
            // Cleanup is best-effort.
        }
    }

    private static void TestPresetCycleOrder()
    {
        var config = AppConfig.CreateDefault();

        // Mix built-in and image presets, exactly what F9 must cycle across.
        config.Presets.Add(new CrosshairPreset { Name = "点", Shape = CrosshairShape.Dot });
        config.Presets.Add(new CrosshairPreset
        {
            Name = "图片",
            Shape = CrosshairShape.Image,
            ImageRelativePath = @"Images\x.png"
        });

        config.ActivePresetId = config.Presets[0].Id;

        var visited = new List<string>();
        for (int i = 0; i < config.Presets.Count; i++)
        {
            int index = config.Presets.FindIndex(p => p.Id == config.ActivePresetId);
            index = (index + 1) % config.Presets.Count;
            config.ActivePresetId = config.Presets[index].Id;
            visited.Add(config.Presets[index].Name);
        }

        Check("cycle visits all presets", visited.Count == 3);
        Check("cycle wraps back to first", config.ActivePresetId == config.Presets[0].Id);
        Check("cycle covers image preset", visited.Contains("图片"));
        Check("cycle covers built-in presets", visited.Contains("默认十字") && visited.Contains("点"));
    }

    private static void TestColorParsing()
    {
        var red = Controls.CrosshairCanvas.ParseColor("#FFFF3B30");
        Check("#RRGGBB parses", red.R == 0xFF && red.G == 0x3B && red.B == 0x30);

        var withAlpha = Controls.CrosshairCanvas.ParseColor("#80FF0000");
        Check("#AARRGGBB parses alpha", withAlpha.A == 0x80 && withAlpha.R == 0xFF);

        var shortHex = Controls.CrosshairCanvas.ParseColor("#F00");
        Check("#RGB shorthand parses", shortHex.R == 0xFF);

        var named = Controls.CrosshairCanvas.ParseColor("Red");
        Check("named color parses", named.R == 0xFF);

        // Garbage must fall back to red, not throw.
        var fallback = Controls.CrosshairCanvas.ParseColor("###bogus###");
        Check("invalid color falls back without throwing", fallback.R == 0xFF && fallback.G == 0x3B);

        var nullColor = Controls.CrosshairCanvas.ParseColor(null);
        Check("null color falls back", nullColor.R == 0xFF);
    }

    private static void TestCrosshairShapes()
    {
        // Every shape must be representable and survive a serialization round-trip,
        // which is what F9 cycling and config persistence rely on.
        foreach (CrosshairShape shape in Enum.GetValues<CrosshairShape>())
        {
            var preset = new CrosshairPreset { Shape = shape, Name = shape.ToString() };
            var clone = preset.Clone();
            Check($"shape {shape} clones correctly", clone.Shape == shape);
        }

        // Rotation normalisation.
        var rotated = new CrosshairPreset { ImageRotation = 725 };
        var json = new ConfigService().Serialize(new AppConfig { Presets = { rotated } });
        var back = new ConfigService().Deserialize(json);
        Check("rotation > 360 normalised to 0-360",
            back?.Presets[0].ImageRotation is >= 0 and < 360);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Writes a small valid PNG using WPF's encoder.</summary>
    private static string CreateTempPng(string name, int width, int height)
    {
        string path = Path.Combine(Path.GetTempPath(), name);

        var visual = new System.Windows.Media.DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(
                System.Windows.Media.Brushes.DeepSkyBlue,
                null,
                new System.Windows.Rect(0, 0, width, height));
        }

        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));

        using var stream = File.Create(path);
        encoder.Save(stream);

        return path;
    }

    private static void Check(string description, bool condition)
    {
        if (condition)
        {
            _passed++;
            Log($"  [PASS] {description}");
        }
        else
        {
            _failed++;
            Log($"  [FAIL] {description}");
        }
    }

    private static void Log(string line)
    {
        Lines.Add(line);
        Console.WriteLine(line);
    }

    private static void WriteLogFile()
    {
        try
        {
            AppPaths.EnsureCreated();
            File.WriteAllText(
                Path.Combine(AppPaths.Root, "selftest.log"),
                string.Join(Environment.NewLine, Lines),
                new UTF8Encoding(false));
        }
        catch
        {
            // Logging is best-effort.
        }
    }
}
