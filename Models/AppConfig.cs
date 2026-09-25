using System.Text.Json.Serialization;

namespace CrosshairWin.Models;

/// <summary>
/// The kind of crosshair to render.
/// <see cref="Image"/> delegates to the image renderer; every other value is a built-in shape.
/// </summary>
public enum CrosshairShape
{
    Cross = 0,
    Dot = 1,
    Circle = 2,
    TShape = 3,
    CrossDot = 4,
    Image = 5
}

/// <summary>How the tray/app starts and whether the overlay is visible initially.</summary>
public enum HotkeyAction
{
    ToggleVisibility = 0,
    CyclePreset = 1,
    OpenSettings = 2,
    Exit = 3
}

/// <summary>
/// A single, independently selectable crosshair configuration.
/// F9 cycles through the whole list, mixing built-in shapes and image presets.
/// </summary>
public sealed class CrosshairPreset
{
    /// <summary>Stable id; also used to remember the active preset across restarts.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>User-visible name shown in the settings preset list.</summary>
    public string Name { get; set; } = "默认准星";

    public CrosshairShape Shape { get; set; } = CrosshairShape.Cross;

    // ------------------------------------------------------------ colour
    /// <summary>Stroke/fill colour as #AARRGGBB.</summary>
    public string Color { get; set; } = "#FFFF3B30";

    public double Opacity { get; set; } = 1.0;

    // ------------------------------------------------------- geometry
    /// <summary>Arm length of cross / T shapes, in pixels.</summary>
    public double Size { get; set; } = 20;

    public double Thickness { get; set; } = 2;

    /// <summary>Gap between the centre and the start of each arm, in pixels.</summary>
    public double Gap { get; set; } = 4;

    /// <summary>Dot radius or circle radius, depending on the shape.</summary>
    public double Radius { get; set; } = 3;

    /// <summary>Whether the cross/T shape gets an outline for contrast.</summary>
    public bool Outline { get; set; } = true;

    public string OutlineColor { get; set; } = "#FF000000";

    // ----------------------------------------------------- image options
    /// <summary>
    /// Path RELATIVE to %AppData%\CrosshairWin\ (e.g. <c>Images\abc.png</c>).
    /// Storing it relative is what keeps the config valid when the user's profile
    /// or the original source image moves.
    /// </summary>
    public string? ImageRelativePath { get; set; }

    public ImageScaleMode ImageScaleMode { get; set; } = ImageScaleMode.Pixels;

    /// <summary>Absolute pixel width when <see cref="ImageScaleMode.Pixels"/> is used.</summary>
    public double ImageWidthPx { get; set; } = 64;

    /// <summary>Percentage of the monitor height when <see cref="ImageScaleMode.ScreenPercent"/>.</summary>
    public double ImageHeightPercent { get; set; } = 10;

    public bool ImageKeepAspectRatio { get; set; } = true;

    /// <summary>Locks the aspect ratio used for resizing in the settings UI.</summary>
    public bool ImageLockRatio { get; set; } = true;

    public double ImageRotation { get; set; } = 0;

    public double ImageOpacity { get; set; } = 1.0;

    public double ImageOffsetX { get; set; } = 0;

    public double ImageOffsetY { get; set; } = 0;

    /// <summary>Original-colour vs tinted rendering.</summary>
    public bool ImageUseOriginalColors { get; set; } = true;

    /// <summary>Tint applied when <see cref="ImageUseOriginalColors"/> is false.</summary>
    public string ImageTintColor { get; set; } = "#FFFF3B30";

    /// <summary>Centre-anchored alignment (true for crosshairs; exposed for completeness).</summary>
    public bool ImageCenterAnchor { get; set; } = true;

    public CrosshairPreset Clone() => new()
    {
        Id = Id,
        Name = Name,
        Shape = Shape,
        Color = Color,
        Opacity = Opacity,
        Size = Size,
        Thickness = Thickness,
        Gap = Gap,
        Radius = Radius,
        Outline = Outline,
        OutlineColor = OutlineColor,
        ImageRelativePath = ImageRelativePath,
        ImageScaleMode = ImageScaleMode,
        ImageWidthPx = ImageWidthPx,
        ImageHeightPercent = ImageHeightPercent,
        ImageKeepAspectRatio = ImageKeepAspectRatio,
        ImageLockRatio = ImageLockRatio,
        ImageRotation = ImageRotation,
        ImageOpacity = ImageOpacity,
        ImageOffsetX = ImageOffsetX,
        ImageOffsetY = ImageOffsetY,
        ImageUseOriginalColors = ImageUseOriginalColors,
        ImageTintColor = ImageTintColor,
        ImageCenterAnchor = ImageCenterAnchor
    };
}

public enum ImageScaleMode
{
    /// <summary>Fixed pixel width.</summary>
    Pixels = 0,

    /// <summary>Height expressed as a percentage of the target monitor's height.</summary>
    ScreenPercent = 1
}

/// <summary>A user-editable global hotkey.</summary>
public sealed class HotkeyBinding
{
    public HotkeyAction Action { get; set; }

    /// <summary>Win32 virtual-key code.</summary>
    public uint VirtualKey { get; set; }

    /// <summary>MOD_* bit flags.</summary>
    public uint Modifiers { get; set; }

    /// <summary>Display text, e.g. "F8" or "Ctrl+Alt+Q".</summary>
    [JsonIgnore]
    public string Display
    {
        get
        {
            var parts = new List<string>();
            if ((Modifiers & 0x0002) != 0) parts.Add("Ctrl");
            if ((Modifiers & 0x0001) != 0) parts.Add("Alt");
            if ((Modifiers & 0x0004) != 0) parts.Add("Shift");
            if ((Modifiers & 0x0008) != 0) parts.Add("Win");
            parts.Add(KeyNames.NameOf(VirtualKey));
            return string.Join("+", parts);
        }
    }
}

/// <summary>Root persisted configuration object.</summary>
public sealed class AppConfig
{
    /// <summary>Schema version, so future migrations are possible.</summary>
    public int Version { get; set; } = 1;

    public List<CrosshairPreset> Presets { get; set; } = new();

    /// <summary>Id of the preset that is currently active.</summary>
    public string ActivePresetId { get; set; } = string.Empty;

    /// <summary>Device name of the target monitor, e.g. <c>\\.\DISPLAY1</c>. Empty = primary.</summary>
    public string MonitorDeviceName { get; set; } = string.Empty;

    public bool OverlayVisible { get; set; } = true;

    /// <summary>Extra pixel offset from the monitor centre.</summary>
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }

    public List<HotkeyBinding> Hotkeys { get; set; } = new();

    public bool StartWithWindows { get; set; }

    public bool ShowTrayNotifications { get; set; } = true;

    /// <summary>
    /// Creates the stock configuration: one built-in cross preset and the default hotkeys.
    /// </summary>
    public static AppConfig CreateDefault()
    {
        var preset = new CrosshairPreset
        {
            Name = "默认十字",
            Shape = CrosshairShape.Cross
        };

        var config = new AppConfig
        {
            Presets = { preset },
            ActivePresetId = preset.Id
        };

        config.Hotkeys.AddRange(new[]
        {
            new HotkeyBinding { Action = HotkeyAction.ToggleVisibility, VirtualKey = 0x77, Modifiers = 0 },                                  // F8
            new HotkeyBinding { Action = HotkeyAction.CyclePreset,      VirtualKey = 0x78, Modifiers = 0 },                                  // F9
            new HotkeyBinding { Action = HotkeyAction.OpenSettings,     VirtualKey = 0x79, Modifiers = 0 },                                  // F10
            new HotkeyBinding { Action = HotkeyAction.Exit,             VirtualKey = 0x51, Modifiers = 0x0002 | 0x0001 }                    // Ctrl+Alt+Q
        });

        // Guarantee the four required actions always exist.
        config.EnsureHotkeys();
        return config;
    }

    /// <summary>Adds any missing default hotkey entries (used after import/migration).</summary>
    public void EnsureHotkeys()
    {
        var defaults = new Dictionary<HotkeyAction, (uint Vk, uint Mods)>
        {
            [HotkeyAction.ToggleVisibility] = (0x77, 0),
            [HotkeyAction.CyclePreset] = (0x78, 0),
            [HotkeyAction.OpenSettings] = (0x79, 0),
            [HotkeyAction.Exit] = (0x51, 0x0002 | 0x0001)
        };

        foreach (var kvp in defaults)
        {
            if (!Hotkeys.Any(h => h.Action == kvp.Key))
            {
                Hotkeys.Add(new HotkeyBinding
                {
                    Action = kvp.Key,
                    VirtualKey = kvp.Value.Vk,
                    Modifiers = kvp.Value.Mods
                });
            }
        }
    }

    /// <summary>Returns the active preset, falling back to the first one.</summary>
    public CrosshairPreset GetActivePreset()
    {
        var match = Presets.FirstOrDefault(p => p.Id == ActivePresetId);
        if (match is not null)
            return match;

        if (Presets.Count == 0)
        {
            var created = new CrosshairPreset { Name = "默认十字" };
            Presets.Add(created);
            ActivePresetId = created.Id;
            return created;
        }

        ActivePresetId = Presets[0].Id;
        return Presets[0];
    }
}

/// <summary>Maps Win32 virtual-key codes to display names for hotkey UI.</summary>
public static class KeyNames
{
    private static readonly Dictionary<uint, string> Map = new()
    {
        [0x08] = "Backspace", [0x09] = "Tab", [0x0D] = "Enter", [0x13] = "Pause",
        [0x14] = "CapsLock", [0x1B] = "Esc", [0x20] = "Space",
        [0x21] = "PageUp", [0x22] = "PageDown", [0x23] = "End", [0x24] = "Home",
        [0x25] = "Left", [0x26] = "Up", [0x27] = "Right", [0x28] = "Down",
        [0x2C] = "PrintScreen", [0x2D] = "Insert", [0x2E] = "Delete",
        [0x5B] = "LWin", [0x5C] = "RWin", [0x5D] = "Apps",
        [0x60] = "Num0", [0x61] = "Num1", [0x62] = "Num2", [0x63] = "Num3",
        [0x64] = "Num4", [0x65] = "Num5", [0x66] = "Num6", [0x67] = "Num7",
        [0x68] = "Num8", [0x69] = "Num9",
        [0x6A] = "Num*", [0x6B] = "Num+", [0x6D] = "Num-", [0x6E] = "Num.",
        [0x6F] = "Num/",
        [0x70] = "F1", [0x71] = "F2", [0x72] = "F3", [0x73] = "F4",
        [0x74] = "F5", [0x75] = "F6", [0x76] = "F7", [0x77] = "F8",
        [0x78] = "F9", [0x79] = "F10", [0x7A] = "F11", [0x7B] = "F12",
        [0x90] = "NumLock", [0x91] = "ScrollLock",
        [0xBA] = ";", [0xBB] = "=", [0xBC] = ",", [0xBD] = "-", [0xBE] = ".",
        [0xBF] = "/", [0xC0] = "`", [0xDB] = "[", [0xDC] = "\\",
        [0xDD] = "]", [0xDE] = "'"
    };

    /// <summary>Returns a friendly name for a virtual-key code.</summary>
    public static string NameOf(uint vk)
    {
        if (Map.TryGetValue(vk, out var name))
            return name;

        if (vk is >= 0x30 and <= 0x39) // 0-9
            return ((char)vk).ToString();

        if (vk is >= 0x41 and <= 0x5A) // A-Z
            return ((char)vk).ToString();

        return $"0x{vk:X2}";
    }
}
