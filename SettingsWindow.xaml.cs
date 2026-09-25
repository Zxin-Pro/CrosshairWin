using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CrosshairWin.Controls;
using CrosshairWin.Models;
using CrosshairWin.Monitors;
using CrosshairWin.Services;
using Microsoft.Win32;

namespace CrosshairWin;

/// <summary>
/// Settings UI.
///
/// Design notes:
///  * The window edits a deep copy of the live config, so "Cancel" truly discards changes.
///  * Every control writes through to the working <see cref="CrosshairPreset"/> and then
///    refreshes the preview, which is what makes the preview genuinely live.
///  * A <c>_suppressEvents</c> flag prevents feedback loops while controls are being
///    populated programmatically.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly ConfigService _configService;
    private readonly ConfigPackageService _packageService;

    private AppConfig _workingConfig;
    private CrosshairPreset? _currentPreset;

    /// <summary>Guards against control-change handlers re-entering while we set values.</summary>
    private bool _suppressEvents;

    /// <summary>True once the window has finished its initial load.</summary>
    private bool _loaded;

    private List<MonitorInfo> _monitors = new();

    /// <summary>Raised after the user saves, so the app can apply the new configuration.</summary>
    public event Action<AppConfig>? ConfigurationSaved;

    /// <summary>Raised when hotkey registration failed on save, so the user can be told.</summary>
    public event Action<string>? NotificationRequested;

    /// <summary>
    /// Internal because <see cref="ConfigService"/> is itself internal.
    /// The window is only ever created by <see cref="App"/>.
    /// </summary>
    internal SettingsWindow(ConfigService configService, AppConfig currentConfig)
    {
        InitializeComponent();

        _configService = configService;
        _packageService = new ConfigPackageService(configService);

        // Work on a copy: Cancel must not mutate the running configuration.
        _workingConfig = CloneConfig(currentConfig);

        _configService.Warning += OnConfigWarning;

        Loaded += SettingsWindow_Loaded;
        Closed += SettingsWindow_Closed;
    }

    // =====================================================================
    //  Lifecycle
    // =====================================================================

    private void SettingsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _suppressEvents = true;

        try
        {
            RefreshMonitorList();
            RefreshPresetList(selectPresetId: _workingConfig.ActivePresetId);
            PopulateGlobalFields();
        }
        finally
        {
            _suppressEvents = false;
        }

        _loaded = true;
        UpdatePreview();
    }

    private void SettingsWindow_Closed(object? sender, EventArgs e)
    {
        _configService.Warning -= OnConfigWarning;
    }

    private void OnConfigWarning(string message) => ShowNotification(message);

    /// <summary>Deep-copies a config via the JSON serializer (simple and keeps clones honest).</summary>
    private AppConfig CloneConfig(AppConfig source)
    {
        var json = _configService.Serialize(source);
        return _configService.Deserialize(json) ?? AppConfig.CreateDefault();
    }

    // =====================================================================
    //  Preset list
    // =====================================================================

    private void RefreshPresetList(string? selectPresetId = null)
    {
        bool previous = _suppressEvents;
        _suppressEvents = true;

        try
        {
            PresetList.ItemsSource = null;
            PresetList.ItemsSource = _workingConfig.Presets;
            PresetList.DisplayMemberPath = nameof(CrosshairPreset.Name);

            var target = _workingConfig.Presets
                .FirstOrDefault(p => p.Id == (selectPresetId ?? _currentPreset?.Id));

            if (target is not null)
                PresetList.SelectedItem = target;
            else if (_workingConfig.Presets.Count > 0)
                PresetList.SelectedIndex = 0;
        }
        finally
        {
            _suppressEvents = previous;
        }
    }

    private void PresetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents)
            return;

        if (PresetList.SelectedItem is not CrosshairPreset preset)
            return;

        _currentPreset = preset;
        LoadPresetIntoControls(preset);
        UpdatePreview();
    }

    /// <summary>Pushes a preset's values into every editing control.</summary>
    private void LoadPresetIntoControls(CrosshairPreset preset)
    {
        _suppressEvents = true;

        try
        {
            // --- shape ---
            ShapeCross.IsChecked = preset.Shape == CrosshairShape.Cross;
            ShapeDot.IsChecked = preset.Shape == CrosshairShape.Dot;
            ShapeCircle.IsChecked = preset.Shape == CrosshairShape.Circle;
            ShapeT.IsChecked = preset.Shape == CrosshairShape.TShape;
            ShapeCrossDot.IsChecked = preset.Shape == CrosshairShape.CrossDot;
            ShapeImage.IsChecked = preset.Shape == CrosshairShape.Image;

            // --- shape styling ---
            ColorBox.Text = preset.Color;
            OpacitySlider.Value = Math.Clamp(preset.Opacity, 0, 1);
            SizeSlider.Value = Math.Clamp(preset.Size, SizeSlider.Minimum, SizeSlider.Maximum);
            ThicknessSlider.Value = Math.Clamp(preset.Thickness, ThicknessSlider.Minimum, ThicknessSlider.Maximum);
            GapSlider.Value = Math.Clamp(preset.Gap, GapSlider.Minimum, GapSlider.Maximum);
            RadiusSlider.Value = Math.Clamp(preset.Radius, RadiusSlider.Minimum, RadiusSlider.Maximum);
            OutlineCheck.IsChecked = preset.Outline;

            // --- image ---
            ScalePixels.IsChecked = preset.ImageScaleMode == ImageScaleMode.Pixels;
            ScalePercent.IsChecked = preset.ImageScaleMode == ImageScaleMode.ScreenPercent;
            ImageWidthSlider.Value = Math.Clamp(preset.ImageWidthPx, ImageWidthSlider.Minimum, ImageWidthSlider.Maximum);
            ImagePercentSlider.Value = Math.Clamp(preset.ImageHeightPercent, ImagePercentSlider.Minimum, ImagePercentSlider.Maximum);
            KeepAspectCheck.IsChecked = preset.ImageKeepAspectRatio;
            LockRatioCheck.IsChecked = preset.ImageLockRatio;
            CenterAnchorCheck.IsChecked = preset.ImageCenterAnchor;
            RotationSlider.Value = Math.Clamp(preset.ImageRotation, 0, 360);
            ImageOpacitySlider.Value = Math.Clamp(preset.ImageOpacity, 0, 1);
            OffsetXSlider.Value = Math.Clamp(preset.ImageOffsetX, OffsetXSlider.Minimum, OffsetXSlider.Maximum);
            OffsetYSlider.Value = Math.Clamp(preset.ImageOffsetY, OffsetYSlider.Minimum, OffsetYSlider.Maximum);
            OriginalColorCheck.IsChecked = preset.ImageUseOriginalColors;
            TintColorBox.Text = preset.ImageTintColor;

            UpdateImagePathText(preset);
            UpdateValueLabels();
            UpdateImageControlStates(preset);
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    /// <summary>Shows the resolved absolute path, or a warning when the file is gone.</summary>
    private void UpdateImagePathText(CrosshairPreset preset)
    {
        if (string.IsNullOrWhiteSpace(preset.ImageRelativePath))
        {
            ImagePathText.Text = "（未设置）";
            ImagePathText.Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A));
            return;
        }

        string? absolute = AppPaths.ResolveImage(preset.ImageRelativePath);

        if (absolute is null)
        {
            ImagePathText.Text = $"⚠ 文件缺失：{preset.ImageRelativePath}（将回退为内置十字准星）";
            ImagePathText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xA5, 0x00));
            return;
        }

        ImagePathText.Text = $"{preset.ImageRelativePath}\n{absolute}";
        ImagePathText.Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8));
    }

    /// <summary>Grays out image controls when the preset is not an image preset.</summary>
    private void UpdateImageControlStates(CrosshairPreset preset)
    {
        bool isImage = preset.Shape == CrosshairShape.Image;
        bool isPixels = preset.ImageScaleMode == ImageScaleMode.Pixels;

        ImageWidthSlider.IsEnabled = isImage && isPixels;
        ImagePercentSlider.IsEnabled = isImage && !isPixels;
        ReplaceImage_Click_State(isImage);
    }

    private void ReplaceImage_Click_State(bool isImage)
    {
        // The replace button is looked up by name so we do not need an x:Name on it.
        // Buttons are enabled/disabled to signal that the tab applies to image presets.
        KeepAspectCheck.IsEnabled = isImage;
        LockRatioCheck.IsEnabled = isImage;
        CenterAnchorCheck.IsEnabled = isImage;
        RotationSlider.IsEnabled = isImage;
        ImageOpacitySlider.IsEnabled = isImage;
        OffsetXSlider.IsEnabled = isImage;
        OffsetYSlider.IsEnabled = isImage;
        OriginalColorCheck.IsEnabled = isImage;
        TintColorBox.IsEnabled = isImage;
    }

    /// <summary>Refreshes every numeric read-out next to a slider.</summary>
    private void UpdateValueLabels()
    {
        OpacityText.Text = $"{OpacitySlider.Value * 100:0}%";
        SizeText.Text = $"{SizeSlider.Value:0}";
        ThicknessText.Text = $"{ThicknessSlider.Value:0}";
        GapText.Text = $"{GapSlider.Value:0}";
        RadiusText.Text = $"{RadiusSlider.Value:0}";
        ImageWidthText.Text = $"{ImageWidthSlider.Value:0}";
        ImagePercentText.Text = $"{ImagePercentSlider.Value:0}%";
        RotationText.Text = $"{RotationSlider.Value:0}°";
        ImageOpacityText.Text = $"{ImageOpacitySlider.Value * 100:0}%";
        OffsetXText.Text = $"{OffsetXSlider.Value:0}";
        OffsetYText.Text = $"{OffsetYSlider.Value:0}";
        ScreenOffsetXText.Text = $"{ScreenOffsetXSlider.Value:0}";
        ScreenOffsetYText.Text = $"{ScreenOffsetYSlider.Value:0}";
    }

    // =====================================================================
    //  Global (non-preset) fields
    // =====================================================================

    private void PopulateGlobalFields()
    {
        bool previous = _suppressEvents;
        _suppressEvents = true;

        try
        {
            VisibleCheck.IsChecked = _workingConfig.OverlayVisible;

            bool autoStart = false;
            try
            {
                autoStart = AutoStartService.IsEnabled();
            }
            catch
            {
                autoStart = _workingConfig.StartWithWindows;
            }

            AutoStartCheck.IsChecked = autoStart;
            NotifyCheck.IsChecked = _workingConfig.ShowTrayNotifications;

            ScreenOffsetXSlider.Value = Math.Clamp(_workingConfig.OffsetX, -500, 500);
            ScreenOffsetYSlider.Value = Math.Clamp(_workingConfig.OffsetY, -500, 500);

            HotkeyToggleBox.Text = FindHotkey(HotkeyAction.ToggleVisibility)?.Display ?? "";
            HotkeyCycleBox.Text = FindHotkey(HotkeyAction.CyclePreset)?.Display ?? "";
            HotkeySettingsBox.Text = FindHotkey(HotkeyAction.OpenSettings)?.Display ?? "";
            HotkeyExitBox.Text = FindHotkey(HotkeyAction.Exit)?.Display ?? "";
        }
        finally
        {
            _suppressEvents = previous;
        }
    }

    private HotkeyBinding? FindHotkey(HotkeyAction action)
        => _workingConfig.Hotkeys.FirstOrDefault(h => h.Action == action);

    private void RefreshMonitorList()
    {
        _monitors = MonitorService.GetMonitors();

        MonitorCombo.ItemsSource = _monitors;

        // Match the saved device name; fall back to the primary entry.
        var selected = _monitors.FirstOrDefault(m =>
            string.Equals(m.DeviceName, _workingConfig.MonitorDeviceName, StringComparison.OrdinalIgnoreCase));

        MonitorCombo.SelectedItem = selected
                                    ?? _monitors.FirstOrDefault(m => m.IsPrimary)
                                    ?? _monitors.FirstOrDefault();
    }

    private void MonitorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || MonitorCombo.SelectedItem is not MonitorInfo monitor)
            return;

        _workingConfig.MonitorDeviceName = monitor.DeviceName;
    }

    private void VisibleCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
            return;

        _workingConfig.OverlayVisible = VisibleCheck.IsChecked == true;
    }

    private void NotifyCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
            return;

        _workingConfig.ShowTrayNotifications = NotifyCheck.IsChecked == true;
    }

    private void AutoStartCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
            return;

        // Applied immediately: the registry value is a side effect the user expects on click.
        bool desired = AutoStartCheck.IsChecked == true;
        _workingConfig.StartWithWindows = desired;

        if (AutoStartService.SetEnabled(desired, out string? error))
        {
            ShowNotification(desired ? "已开启开机自启。" : "已关闭开机自启。");
        }
        else
        {
            ShowNotification(error ?? "设置开机自启失败。");

            // Reflect the true state back into the checkbox.
            bool previous = _suppressEvents;
            _suppressEvents = true;
            AutoStartCheck.IsChecked = AutoStartService.IsEnabled();
            _suppressEvents = previous;
        }
    }

    private void ScreenOffsetXSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents)
            return;

        _workingConfig.OffsetX = e.NewValue;
        UpdateValueLabels();
    }

    private void ScreenOffsetYSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents)
            return;

        _workingConfig.OffsetY = e.NewValue;
        UpdateValueLabels();
    }

    // =====================================================================
    //  Shape editing
    // =====================================================================

    private void Shape_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || _currentPreset is null)
            return;

        if (sender is not RadioButton { Tag: string tag })
            return;

        if (!Enum.TryParse<CrosshairShape>(tag, out var shape))
            return;

        _currentPreset.Shape = shape;

        // Choosing "image" without a file is a dead end; guide the user straight away.
        if (shape == CrosshairShape.Image && string.IsNullOrWhiteSpace(_currentPreset.ImageRelativePath))
        {
            ShowNotification("已切换为图片准星，请点击“选择图片”导入图片文件。");
        }

        UpdateImageControlStates(_currentPreset);
        UpdateImagePathText(_currentPreset);
        UpdatePreview();
    }

    private void ColorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents || _currentPreset is null)
            return;

        _currentPreset.Color = ColorBox.Text;
        UpdatePreview();
    }

    private void PickColor_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPreset is null)
            return;

        string? picked = Dialogs.ColorPickerWindow.Pick(this, _currentPreset.Color);
        if (picked is null)
            return;

        _suppressEvents = true;
        ColorBox.Text = picked;
        _suppressEvents = false;

        _currentPreset.Color = picked;
        UpdatePreview();
    }

    private void TintColorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents || _currentPreset is null)
            return;

        _currentPreset.ImageTintColor = TintColorBox.Text;
        UpdatePreview();
    }

    private void PickTintColor_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPreset is null)
            return;

        string? picked = Dialogs.ColorPickerWindow.Pick(this, _currentPreset.ImageTintColor);
        if (picked is null)
            return;

        _suppressEvents = true;
        TintColorBox.Text = picked;
        _suppressEvents = false;

        _currentPreset.ImageTintColor = picked;
        UpdatePreview();
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents || _currentPreset is null)
            return;

        _currentPreset.Opacity = e.NewValue;
        UpdateValueLabels();
        UpdatePreview();
    }

    private void SizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents || _currentPreset is null)
            return;

        _currentPreset.Size = e.NewValue;
        UpdateValueLabels();
        UpdatePreview();
    }

    private void ThicknessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents || _currentPreset is null)
            return;

        _currentPreset.Thickness = e.NewValue;
        UpdateValueLabels();
        UpdatePreview();
    }

    private void GapSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents || _currentPreset is null)
            return;

        _currentPreset.Gap = e.NewValue;
        UpdateValueLabels();
        UpdatePreview();
    }

    private void RadiusSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents || _currentPreset is null)
            return;

        _currentPreset.Radius = e.NewValue;
        UpdateValueLabels();
        UpdatePreview();
    }

    private void OutlineCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || _currentPreset is null)
            return;

        _currentPreset.Outline = OutlineCheck.IsChecked == true;
        UpdatePreview();
    }

    // =====================================================================
    //  Image editing
    // =====================================================================

    private void ScaleMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || _currentPreset is null)
            return;

        if (sender is not RadioButton { Tag: string tag })
            return;

        if (!Enum.TryParse<ImageScaleMode>(tag, out var mode))
            return;

        _currentPreset.ImageScaleMode = mode;
        UpdateImageControlStates(_currentPreset);
        UpdatePreview();
    }

    private void ImageWidthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents || _currentPreset is null)
            return;

        _currentPreset.ImageWidthPx = e.NewValue;

        // "Lock ratio" keeps the stored percentage in step with the pixel width so
        // switching between the two scale modes does not jump.
        if (_currentPreset.ImageLockRatio && _currentPreset.ImageScaleMode == ImageScaleMode.Pixels)
        {
            var monitor = GetSelectedMonitor();
            if (monitor is not null && monitor.Height > 0)
            {
                _workingConfig.OffsetX = _workingConfig.OffsetX;
                double percent = e.NewValue / monitor.Height * 100.0;
                _suppressEvents = true;
                ImagePercentSlider.Value = Math.Clamp(percent, ImagePercentSlider.Minimum, ImagePercentSlider.Maximum);
                _suppressEvents = false;
                _currentPreset.ImageHeightPercent = percent;
            }
        }

        UpdateValueLabels();
        UpdatePreview();
    }

    private void ImagePercentSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents || _currentPreset is null)
            return;

        _currentPreset.ImageHeightPercent = e.NewValue;

        if (_currentPreset.ImageLockRatio && _currentPreset.ImageScaleMode == ImageScaleMode.ScreenPercent)
        {
            var monitor = GetSelectedMonitor();
            if (monitor is not null)
            {
                double pixels = monitor.Height * (e.NewValue / 100.0);
                _suppressEvents = true;
                ImageWidthSlider.Value = Math.Clamp(pixels, ImageWidthSlider.Minimum, ImageWidthSlider.Maximum);
                _suppressEvents = false;
                _currentPreset.ImageWidthPx = pixels;
            }
        }

        UpdateValueLabels();
        UpdatePreview();
    }

    private void KeepAspect_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || _currentPreset is null)
            return;

        _currentPreset.ImageKeepAspectRatio = KeepAspectCheck.IsChecked == true;
        UpdatePreview();
    }

    private void LockRatio_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || _currentPreset is null)
            return;

        _currentPreset.ImageLockRatio = LockRatioCheck.IsChecked == true;
    }

    private void CenterAnchor_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || _currentPreset is null)
            return;

        _currentPreset.ImageCenterAnchor = CenterAnchorCheck.IsChecked == true;
        UpdatePreview();
    }

    private void RotationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents || _currentPreset is null)
            return;

        _currentPreset.ImageRotation = e.NewValue;
        UpdateValueLabels();
        UpdatePreview();
    }

    private void ImageOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents || _currentPreset is null)
            return;

        _currentPreset.ImageOpacity = e.NewValue;
        UpdateValueLabels();
        UpdatePreview();
    }

    private void OffsetXSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents || _currentPreset is null)
            return;

        _currentPreset.ImageOffsetX = e.NewValue;
        UpdateValueLabels();
        UpdatePreview();
    }

    private void OffsetYSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents || _currentPreset is null)
            return;

        _currentPreset.ImageOffsetY = e.NewValue;
        UpdateValueLabels();
        UpdatePreview();
    }

    private void OriginalColor_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || _currentPreset is null)
            return;

        _currentPreset.ImageUseOriginalColors = OriginalColorCheck.IsChecked == true;
        UpdatePreview();
    }

    private void PickImage_Click(object sender, RoutedEventArgs e) => ImportImage(createNewPreset: true);

    private void ReplaceImage_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPreset is null)
        {
            ShowNotification("请先选择一个预设。");
            return;
        }

        ImportImage(createNewPreset: false);
    }

    private void OpenImagesFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AppPaths.EnsureCreated();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = AppPaths.ImagesDir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowNotification($"无法打开图片目录：{ex.Message}");
        }
    }

    /// <summary>
    /// Shared import path for the file dialog, drag &amp; drop and the replace button.
    /// </summary>
    private void ImportImage(bool createNewPreset, string? directPath = null)
    {
        string? sourcePath = directPath;

        if (sourcePath is null)
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择准星图片（推荐透明 PNG）",
                Filter = ImageLoader.FileDialogFilter,
                CheckFileExists = true,
                Multiselect = false
            };

            if (dialog.ShowDialog(this) != true)
                return;

            sourcePath = dialog.FileName;
        }

        if (!File.Exists(sourcePath))
        {
            ShowNotification($"文件不存在：{sourcePath}");
            return;
        }

        string baseName = Path.GetFileNameWithoutExtension(sourcePath);
        var import = ImageImportService.Import(sourcePath, baseName);

        if (!import.Success || import.RelativePath is null)
        {
            ShowNotification(import.Error ?? "导入图片失败。");
            return;
        }

        if (createNewPreset || _currentPreset is null)
        {
            var preset = ImageImportService.CreateImagePreset(import.DisplayName ?? "图片准星", import.RelativePath);

            _workingConfig.Presets.Add(preset);
            _currentPreset = preset;
            _workingConfig.ActivePresetId = preset.Id;

            RefreshPresetList(preset.Id);
            LoadPresetIntoControls(preset);
        }
        else
        {
            // Replacing on the same preset: drop the previous managed file so the
            // Images folder does not accumulate orphans.
            string? old = _currentPreset.ImageRelativePath;

            _currentPreset.ImageRelativePath = import.RelativePath;
            _currentPreset.Shape = CrosshairShape.Image;

            if (!string.IsNullOrWhiteSpace(old) && old != import.RelativePath)
                ImageImportService.DeleteManagedImage(old);

            LoadPresetIntoControls(_currentPreset);
        }

        // A new hash-named file means the cache must not serve the old bitmap.
        ImageLoader.ClearCache();

        ShowNotification($"已导入图片：{import.DisplayName}");
        UpdatePreview();
    }

    // =====================================================================
    //  Drag & drop
    // =====================================================================

    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        e.Effects = HasSupportedImage(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = HasSupportedImage(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (!HasSupportedImage(e))
        {
            ShowNotification("拖入的文件不是受支持的图片格式。");
            return;
        }

        var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;

        // Import the first supported file and report the rest, keeping behaviour predictable.
        string? first = files.FirstOrDefault(ImageLoader.IsSupported);
        if (first is null)
        {
            ShowNotification("拖入的文件不是受支持的图片格式。");
            return;
        }

        ImportImage(createNewPreset: true, directPath: first);

        int skipped = files.Length - 1;
        if (skipped > 0)
            ShowNotification($"共拖入 {files.Length} 个文件，本次仅导入第一个（其余 {skipped} 个可再次拖入）。");
    }

    private static bool HasSupportedImage(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return false;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files)
            return false;

        return files.Any(ImageLoader.IsSupported);
    }

    // =====================================================================
    //  Preset management
    // =====================================================================

    private void AddBuiltIn_Click(object sender, RoutedEventArgs e)
    {
        var preset = new CrosshairPreset
        {
            Name = $"自定义准星 {_workingConfig.Presets.Count + 1}",
            Shape = CrosshairShape.Cross
        };

        _workingConfig.Presets.Add(preset);
        _currentPreset = preset;

        RefreshPresetList(preset.Id);
        LoadPresetIntoControls(preset);
        UpdatePreview();
    }

    private void AddImage_Click(object sender, RoutedEventArgs e) => ImportImage(createNewPreset: true);

    private void DuplicatePreset_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPreset is null)
            return;

        var copy = _currentPreset.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = _currentPreset.Name + " 副本";

        // The image file itself is shared by relative path, so no copy is needed;
        // deleting one preset will not remove the file the other still references.

        _workingConfig.Presets.Add(copy);
        _currentPreset = copy;

        RefreshPresetList(copy.Id);
        LoadPresetIntoControls(copy);
        UpdatePreview();
    }

    private void RenamePreset_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPreset is null)
            return;

        string? name = Dialogs.InputDialogWindow.Ask(this, "重命名预设", "新的名称：", _currentPreset.Name);
        if (string.IsNullOrWhiteSpace(name))
            return;

        _currentPreset.Name = name.Trim();

        RefreshPresetList(_currentPreset.Id);
        UpdatePreview();
    }

    private void DeletePreset_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPreset is null)
            return;

        if (_workingConfig.Presets.Count <= 1)
        {
            ShowNotification("至少需要保留一个准星预设。");
            return;
        }

        var toDelete = _currentPreset;

        var confirm = MessageBox.Show(
            this,
            $"确定要删除预设“{toDelete.Name}”吗？",
            "删除预设",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
            return;

        // Remove the managed image only if no other preset still references it.
        string? image = toDelete.ImageRelativePath;
        _workingConfig.Presets.Remove(toDelete);

        bool stillUsed = !string.IsNullOrWhiteSpace(image) &&
                         _workingConfig.Presets.Any(p =>
                             string.Equals(p.ImageRelativePath, image, StringComparison.OrdinalIgnoreCase));

        if (!stillUsed)
            ImageImportService.DeleteManagedImage(image);

        _currentPreset = _workingConfig.Presets.FirstOrDefault();
        _workingConfig.ActivePresetId = _currentPreset?.Id ?? string.Empty;

        RefreshPresetList(_currentPreset?.Id);

        if (_currentPreset is not null)
            LoadPresetIntoControls(_currentPreset);

        UpdatePreview();
    }

    // =====================================================================
    //  Hotkeys
    // =====================================================================

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;

        if (sender is not TextBox { Tag: string tag })
            return;

        if (!Enum.TryParse<HotkeyAction>(tag, out var action))
            return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Ignore pure modifier presses; the user must supply a real key.
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return;
        }

        if (key == Key.Escape)
        {
            SetHotkey(action, 0, 0);
            return;
        }

        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0)
            return;

        uint modifiers = 0;
        var modifiersNow = Keyboard.Modifiers;

        if (modifiersNow.HasFlag(ModifierKeys.Control)) modifiers |= 0x0002;
        if (modifiersNow.HasFlag(ModifierKeys.Alt)) modifiers |= 0x0001;
        if (modifiersNow.HasFlag(ModifierKeys.Shift)) modifiers |= 0x0004;

        SetHotkey(action, vk, modifiers);
    }

    private void SetHotkey(HotkeyAction action, uint vk, uint modifiers)
    {
        var binding = FindHotkey(action);

        if (binding is null)
        {
            binding = new HotkeyBinding { Action = action };
            _workingConfig.Hotkeys.Add(binding);
        }

        // Detect a clash inside our own config before we even try the registry.
        var clash = _workingConfig.Hotkeys.FirstOrDefault(h =>
            h.Action != action && h.VirtualKey == vk && h.Modifiers == modifiers && vk != 0);

        if (clash is not null && vk != 0)
        {
            ShowNotification(
                $"快捷键冲突：{binding.Display} 与“{HotkeyService.DescribeAction(clash.Action)}”重复，已取消设置。");
            return;
        }

        binding.VirtualKey = vk;
        binding.Modifiers = modifiers;

        RefreshHotkeyBoxes();
    }

    private void RefreshHotkeyBoxes()
    {
        HotkeyToggleBox.Text = FindHotkey(HotkeyAction.ToggleVisibility)?.Display ?? "（无）";
        HotkeyCycleBox.Text = FindHotkey(HotkeyAction.CyclePreset)?.Display ?? "（无）";
        HotkeySettingsBox.Text = FindHotkey(HotkeyAction.OpenSettings)?.Display ?? "（无）";
        HotkeyExitBox.Text = FindHotkey(HotkeyAction.Exit)?.Display ?? "（无）";
    }

    private void ResetHotkeys_Click(object sender, RoutedEventArgs e)
    {
        _workingConfig.Hotkeys.Clear();
        _workingConfig.EnsureHotkeys();
        RefreshHotkeyBoxes();
        ShowNotification("已恢复默认快捷键。");
    }

    // =====================================================================
    //  Save / cancel / import / export
    // =====================================================================

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Keep the active preset in step with the current selection.
            if (_currentPreset is not null)
                _workingConfig.ActivePresetId = _currentPreset.Id;

            // A missing image on the active preset is worth flagging before saving.
            var active = _workingConfig.GetActivePreset();
            if (active.Shape == CrosshairShape.Image &&
                AppPaths.ResolveImage(active.ImageRelativePath) is null)
            {
                ShowNotification("当前图片准星的图片文件缺失，将回退为内置十字准星。");
            }

            if (!_configService.Save(_workingConfig))
            {
                ShowNotification("保存失败，请检查 %AppData%\\CrosshairWin 的写入权限。");
                return;
            }

            ConfigurationSaved?.Invoke(_workingConfig);
            Close();
        }
        catch (Exception ex)
        {
            ShowNotification($"保存时发生错误：{ex.Message}");
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void RestoreDefaults_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            this,
            "确定要恢复默认设置吗？当前所有准星预设将被替换（已导入的图片文件会保留在磁盘上）。",
            "恢复默认设置",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes)
            return;

        _workingConfig = AppConfig.CreateDefault();
        _currentPreset = _workingConfig.Presets.FirstOrDefault();

        _suppressEvents = true;
        try
        {
            RefreshMonitorList();
            PopulateGlobalFields();
        }
        finally
        {
            _suppressEvents = false;
        }

        RefreshPresetList(_currentPreset?.Id);

        if (_currentPreset is not null)
            LoadPresetIntoControls(_currentPreset);

        UpdatePreview();
        ShowNotification("已恢复默认设置，点击“保存”使其生效。");
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出配置（含图片）",
            Filter = ConfigPackageService.PackageFilter,
            FileName = ConfigPackageService.SuggestExportFileName(),
            DefaultExt = ".chwconfig",
            AddExtension = true
        };

        if (dialog.ShowDialog(this) != true)
            return;

        var result = _packageService.Export(_workingConfig, dialog.FileName);

        if (result.Success)
            ShowNotification($"导出成功：已包含 {result.ImageCount} 个图片资源。\n{dialog.FileName}");
        else
            ShowNotification(result.Error ?? "导出失败。");
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入配置（含图片）",
            Filter = ConfigPackageService.PackageFilter,
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true)
            return;

        var result = _packageService.Import(dialog.FileName);

        if (!result.Success || result.Config is null)
        {
            ShowNotification(result.Error ?? "导入失败。");
            return;
        }

        _workingConfig = result.Config;
        _currentPreset = _workingConfig.Presets.FirstOrDefault();

        RefreshPresetList(_currentPreset?.Id);

        _suppressEvents = true;
        try
        {
            RefreshMonitorList();
            PopulateGlobalFields();
        }
        finally
        {
            _suppressEvents = false;
        }

        if (_currentPreset is not null)
            LoadPresetIntoControls(_currentPreset);

        UpdatePreview();
        ShowNotification($"导入成功：恢复了 {result.ImageCount} 个图片资源。点击“保存”使其生效。");
    }

    // =====================================================================
    //  Preview
    // =====================================================================

    /// <summary>Repaints the live preview with the working preset.</summary>
    private void UpdatePreview()
    {
        if (!_loaded && !IsLoaded)
            return;

        try
        {
            var preset = _currentPreset;
            PreviewCanvas.Preset = preset;
            PreviewCanvas.ImageProvider = ResolvePreviewImage;

            // Use the real target monitor height so percentage scaling previews truthfully.
            var monitor = GetSelectedMonitor();
            PreviewCanvas.TargetScreenHeight = monitor?.Height ?? 1080;

            PreviewHint.Text = preset is null
                ? "实时预览"
                : $"{preset.Name}  ·  {DescribeShape(preset.Shape)}" +
                  (preset.Shape == CrosshairShape.Image && AppPaths.ResolveImage(preset.ImageRelativePath) is null
                      ? "  ·  ⚠ 图片缺失，显示回退十字"
                      : string.Empty);

            PreviewCanvas.InvalidateVisual();
        }
        catch
        {
            // The preview is non-critical; never let it break the settings dialog.
        }
    }

    private static string DescribeShape(CrosshairShape shape) => shape switch
    {
        CrosshairShape.Cross => "十字",
        CrosshairShape.Dot => "点",
        CrosshairShape.Circle => "圆",
        CrosshairShape.TShape => "T 形",
        CrosshairShape.CrossDot => "十字+中心点",
        CrosshairShape.Image => "自定义图片",
        _ => shape.ToString()
    };

    private ImageSource? ResolvePreviewImage(CrosshairPreset preset)
    {
        string? absolute = AppPaths.ResolveImage(preset.ImageRelativePath);
        if (absolute is null)
            return null;

        var result = ImageLoader.Load(absolute);
        return result.Success ? result.Image : null;
    }

    private MonitorInfo? GetSelectedMonitor()
    {
        if (MonitorCombo.SelectedItem is MonitorInfo monitor)
            return monitor;

        return _monitors.FirstOrDefault(m => m.IsPrimary) ?? _monitors.FirstOrDefault();
    }

    // =====================================================================
    //  Notifications
    // =====================================================================

    private void ShowNotification(string message)
    {
        NotificationRequested?.Invoke(message);
    }
}
