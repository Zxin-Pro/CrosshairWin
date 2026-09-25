using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CrosshairWin.Models;

namespace CrosshairWin.Services;

/// <summary>
/// Loads and saves <see cref="AppConfig"/> as JSON.
/// All operations are failure-tolerant: a corrupt config never prevents startup.
/// </summary>
internal sealed class ConfigService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Raised when a config problem worth telling the user about occurs.</summary>
    public event Action<string>? Warning;

    /// <summary>
    /// Loads the config from disk. Returns defaults when the file is missing or unreadable.
    /// </summary>
    public AppConfig Load()
    {
        try
        {
            if (!File.Exists(AppPaths.ConfigFile))
                return AppConfig.CreateDefault();

            string json = File.ReadAllText(AppPaths.ConfigFile);
            if (string.IsNullOrWhiteSpace(json))
            {
                Warning?.Invoke("配置文件为空，已恢复默认设置。");
                return AppConfig.CreateDefault();
            }

            var config = JsonSerializer.Deserialize<AppConfig>(json, Options);

            if (config is null)
            {
                Warning?.Invoke("配置文件内容无效，已恢复默认设置。");
                return AppConfig.CreateDefault();
            }

            // Repair anything a hand-edited or older file may be missing.
            Normalize(config);
            return config;
        }
        catch (JsonException ex)
        {
            Warning?.Invoke($"配置文件格式错误，已恢复默认设置：{ex.Message}");
            BackupBadConfig();
            return AppConfig.CreateDefault();
        }
        catch (Exception ex)
        {
            Warning?.Invoke($"读取配置失败，已恢复默认设置：{ex.Message}");
            return AppConfig.CreateDefault();
        }
    }

    /// <summary>Writes the config to disk. Returns false (and reports) on failure.</summary>
    public bool Save(AppConfig config)
    {
        try
        {
            AppPaths.EnsureCreated();

            // Write to a temp file then move, so a crash mid-write cannot corrupt the config.
            string tmp = AppPaths.ConfigFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(config, Options));

            if (File.Exists(AppPaths.ConfigFile))
                File.Replace(tmp, AppPaths.ConfigFile, null);
            else
                File.Move(tmp, AppPaths.ConfigFile);

            return true;
        }
        catch (Exception ex)
        {
            Warning?.Invoke($"保存配置失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>Serializes the config to a string (used by config export).</summary>
    public string Serialize(AppConfig config) => JsonSerializer.Serialize(config, Options);

    /// <summary>Parses a config from a string, returning null when the JSON is invalid.</summary>
    public AppConfig? Deserialize(string json)
    {
        try
        {
            var config = JsonSerializer.Deserialize<AppConfig>(json, Options);
            if (config is not null)
                Normalize(config);
            return config;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Enforces invariants that the rest of the app relies on.</summary>
    private static void Normalize(AppConfig config)
    {
        config.Presets ??= new List<CrosshairPreset>();
        config.Hotkeys ??= new List<HotkeyBinding>();

        if (config.Presets.Count == 0)
        {
            var preset = new CrosshairPreset { Name = "默认十字" };
            config.Presets.Add(preset);
            config.ActivePresetId = preset.Id;
        }

        // Repair presets with blank ids/names so list operations stay well-defined.
        foreach (var preset in config.Presets)
        {
            if (string.IsNullOrWhiteSpace(preset.Id))
                preset.Id = Guid.NewGuid().ToString("N");

            if (string.IsNullOrWhiteSpace(preset.Name))
                preset.Name = "未命名准星";

            preset.Opacity = Math.Clamp(preset.Opacity, 0.0, 1.0);
            preset.ImageOpacity = Math.Clamp(preset.ImageOpacity, 0.0, 1.0);
            preset.ImageRotation = ((preset.ImageRotation % 360) + 360) % 360;
        }

        if (string.IsNullOrWhiteSpace(config.ActivePresetId) ||
            !config.Presets.Any(p => p.Id == config.ActivePresetId))
        {
            config.ActivePresetId = config.Presets[0].Id;
        }

        config.EnsureHotkeys();
    }

    /// <summary>Keeps a copy of an unparseable config so the user can inspect it.</summary>
    private void BackupBadConfig()
    {
        try
        {
            if (!File.Exists(AppPaths.ConfigFile))
                return;

            string backup = AppPaths.ConfigFile + $".bad-{DateTime.Now:yyyyMMddHHmmss}";
            File.Copy(AppPaths.ConfigFile, backup, overwrite: true);
        }
        catch
        {
            // Backup is best-effort only.
        }
    }
}
