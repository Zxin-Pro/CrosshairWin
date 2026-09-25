using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CrosshairWin.Dialogs;

/// <summary>
/// A small self-contained colour picker.
///
/// WPF has no built-in colour dialog (System.Windows.Forms.ColorDialog would drag in
/// WinForms), so this keeps the dependency surface at zero and matches the dark theme.
/// </summary>
public partial class ColorPickerWindow : Window
{
    private bool _suppress;

    /// <summary>The chosen colour as "#AARRGGBB", or null when cancelled.</summary>
    public string? SelectedColor { get; private set; }

    public ColorPickerWindow()
    {
        InitializeComponent();

        // Block change handlers until Pick() has seeded the initial colour.
        _suppress = true;
        BuildPresetSwatches();
    }

    /// <summary>
    /// Shows the picker modally and returns the chosen colour string, or null if cancelled.
    /// </summary>
    public static string? Pick(Window owner, string? initial)
    {
        var window = new ColorPickerWindow
        {
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        window.SetColor(Controls.CrosshairCanvas.ParseColor(initial));
        window._suppress = false;

        return window.ShowDialog() == true ? window.SelectedColor : null;
    }

    /// <summary>Applies a colour to every input without raising change handlers.</summary>
    private void SetColor(Color color)
    {
        bool previous = _suppress;
        _suppress = true;

        try
        {
            ASlider.Value = color.A;
            RSlider.Value = color.R;
            GSlider.Value = color.G;
            BSlider.Value = color.B;

            HexBox.Text = $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
            SwatchBorder.Background = new SolidColorBrush(color);
        }
        finally
        {
            _suppress = previous;
        }
    }

    /// <summary>Reads the current slider values as a colour.</summary>
    private Color CurrentColor() => Color.FromArgb(
        (byte)ASlider.Value,
        (byte)RSlider.Value,
        (byte)GSlider.Value,
        (byte)BSlider.Value);

    private void Channel_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppress)
            return;

        SetColor(CurrentColor());
    }

    private void HexBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppress)
            return;

        var parsed = Controls.CrosshairCanvas.ParseColor(HexBox.Text);

        // Only sync the sliders when the typed value actually parses; otherwise the
        // user cannot finish typing "#FF" without the box fighting them.
        if (!IsValidHex(HexBox.Text))
            return;

        bool previous = _suppress;
        _suppress = true;

        try
        {
            ASlider.Value = parsed.A;
            RSlider.Value = parsed.R;
            GSlider.Value = parsed.G;
            BSlider.Value = parsed.B;
            SwatchBorder.Background = new SolidColorBrush(parsed);
        }
        finally
        {
            _suppress = previous;
        }
    }

    /// <summary>Validates the hex text without relying on the forgiving parser.</summary>
    private static bool IsValidHex(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.StartsWith('#'))
            return false;

        string hex = text[1..];
        if (hex.Length is not (3 or 4 or 6 or 8))
            return false;

        return hex.All(c => Uri.IsHexDigit(c));
    }

    /// <summary>Creates the quick-pick colour swatches.</summary>
    private void BuildPresetSwatches()
    {
        string[] presets =
        {
            // Default crosshair red first, then a useful spread for aiming reticles.
            "#FFFF3B30", "#FFFF9500", "#FFFFCC00", "#FF34C759", "#FF00C7BE",
            "#FF007AFF", "#FFAF52DE", "#FFFF2D55", "#FFFFFFFF", "#FF8E8E93"
        };

        var normalBorder = new SolidColorBrush(Color.FromRgb(0x34, 0x39, 0x46));
        var hoverBorder = new SolidColorBrush(Controls.CrosshairCanvas.ParseColor("#FF4EA1FF"));

        foreach (string hex in presets)
        {
            // A Border keeps the swatch flat (no button chrome) and still clickable.
            var swatch = new Border
            {
                Width = 30,
                Height = 30,
                Margin = new Thickness(0, 0, 8, 8),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Controls.CrosshairCanvas.ParseColor(hex)),
                BorderBrush = normalBorder,
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = hex,
                Tag = hex
            };

            swatch.MouseLeftButtonUp += (_, _) => SetColor(Controls.CrosshairCanvas.ParseColor(hex));
            swatch.MouseEnter += (_, _) => swatch.BorderBrush = hoverBorder;
            swatch.MouseLeave += (_, _) => swatch.BorderBrush = normalBorder;

            PresetPanel.Children.Add(swatch);
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var color = CurrentColor();
        SelectedColor = $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        SelectedColor = null;
        DialogResult = false;
    }

    /// <summary>Formats a colour for display in tooltips.</summary>
    internal static string Format(Color color) => string.Format(
        CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}{3:X2}", color.A, color.R, color.G, color.B);
}
